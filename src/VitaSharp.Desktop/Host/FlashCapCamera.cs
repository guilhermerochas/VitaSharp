using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using FlashCap;
using VitaSharp.Core.Host;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// Host webcam for SceCamera through FlashCap (DirectShow / Media Foundation on Windows, V4L2 on Linux,
/// AVFoundation on macOS). Both Vita cameras map to the selected host device. Capture runs on FlashCap's thread;
/// each frame is converted to RGBA into a <see cref="CameraFrameBuffer"/> that keeps only the latest one.
/// One device capture serves both Vita cameras and survives a sensor switch (<see cref="SharedCapture"/>).
/// </summary>
public sealed class FlashCapCamera : ICameraSource
{
    readonly string? _selection;
    List<CaptureDeviceDescriptor>? _devices;
    // a stopped Vita camera keeps the webcam open this long, so stop + start of the other sensor reuses it
    readonly SharedCapture _shared = new(TimeSpan.FromSeconds(2));

    FlashCapCamera(string? selection) => _selection = selection;

    /// <summary><paramref name="selection"/>: null/"" = first camera, "none" = disabled (returns null), a device
    /// name (exact or substring, case-insensitive) or a 0-based index into <see cref="ListDevices"/>.</summary>
    public static FlashCapCamera? TryCreate(string? selection)
    {
        if (string.Equals(selection, "none", StringComparison.OrdinalIgnoreCase)) return null;
        return new FlashCapCamera(string.IsNullOrWhiteSpace(selection) ? null : selection.Trim());
    }

    /// <summary>Video capture devices, one entry per physical camera (DirectShow preferred over Media Foundation
    /// on Windows; Video for Windows only when nothing else exists). Never throws.</summary>
    public static List<CaptureDeviceDescriptor> ListDevices()
    {
        try
        {
            var all = EnumerateAll()
                .Where(d => d.Characteristics.Any(c => Map(c.PixelFormat) != null))
                .ToList();
            static int Rank(DeviceTypes t) => t switch
            {
                DeviceTypes.DirectShow or DeviceTypes.V4L2 or DeviceTypes.AVFoundation => 0,
                DeviceTypes.MediaFoundation => 1,
                _ => 2, // Video for Windows: legacy "Default" alias of a camera listed elsewhere
            };
            var list = new List<CaptureDeviceDescriptor>();
            foreach (var d in all.OrderBy(d => Rank(d.DeviceType)))
            {
                if (d.DeviceType == DeviceTypes.VideoForWindows && list.Count > 0) continue;
                if (list.Any(e => e.Name == d.Name)) continue;
                list.Add(d);
            }
            return list;
        }
        catch (Exception e)
        {
            Log.Warn($"Camera: device enumeration failed: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// Every capture device of this OS's backends, through FlashCap's platform-specific device types. DirectShow is
    /// built on runtime-generated COM interop, which trimmed and Native AOT builds do not have: there it is switched
    /// off (<see cref="DirectShowEnabled"/>) and Windows lists Media Foundation and Video for Windows devices only.
    /// </summary>
    static IEnumerable<CaptureDeviceDescriptor> EnumerateAll()
    {
        if (OperatingSystem.IsWindows())
        {
            var vfw = Enumerate(new FlashCap.Devices.VideoForWindowsDevices());
            var mf = OperatingSystem.IsWindowsVersionAtLeast(6, 1)
                ? new FlashCap.Devices.MediaFoundationDevices().EnumerateDescriptors() // AOT-safe overload
                : [];
            return DirectShowEnabled ? EnumerateDirectShow().Concat(mf).Concat(vfw) : mf.Concat(vfw);
        }
        if (OperatingSystem.IsLinux()) return Enumerate(new FlashCap.Devices.V4L2Devices());
        if (OperatingSystem.IsMacOS()) return Enumerate(new FlashCap.Devices.AVFoundationDevices());
        return [];
    }

    // FlashCap marks the general CaptureDevices.EnumerateDescriptors() as RequiresUnreferencedCode because the default
    // CaptureDevices also enumerates DirectShow; it only has an unannotated overload for MediaFoundationDevices.
    // The V4L2, AVFoundation and Video for Windows types override the enumeration without DirectShow or COM.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Never called with DirectShowDevices or the default CaptureDevices: platform types without COM interop only.")]
    static IEnumerable<CaptureDeviceDescriptor> Enumerate(CaptureDevices platformDevices) => platformDevices.EnumerateDescriptors();

    /// <summary>
    /// Feature switch "VitaSharp.Camera.DirectShow": on by default; the project sets it to false for trimmed and
    /// Native AOT publishes (RuntimeHostConfigurationOption, Trim="true"), so the compiler removes the DirectShow path.
    /// </summary>
    [FeatureSwitchDefinition("VitaSharp.Camera.DirectShow")]
    [FeatureGuard(typeof(RequiresUnreferencedCodeAttribute))]
    [UnconditionalSuppressMessage("Trimming", "IL4000", Justification = "Feature switch: substituted with false by the trimmer/ILC in trimmed and AOT builds (see VitaSharp.Desktop.csproj).")]
    static bool DirectShowEnabled => !AppContext.TryGetSwitch("VitaSharp.Camera.DirectShow", out bool on) || on;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [RequiresUnreferencedCode("DirectShow uses runtime-generated COM interop.")]
    static IEnumerable<CaptureDeviceDescriptor> EnumerateDirectShow() => new FlashCap.Devices.DirectShowDevices().EnumerateDescriptors();

    List<CaptureDeviceDescriptor> Devices => _devices ??= ListDevices();

    public IReadOnlyList<string> DeviceNames => Devices.Select(d => d.Name).ToList();

    CaptureDeviceDescriptor? Select()
    {
        var devs = Devices;
        if (devs.Count == 0) return null;
        if (_selection == null) return devs[0];
        if (int.TryParse(_selection, out int idx) && idx >= 0 && idx < devs.Count) return devs[idx];
        return devs.FirstOrDefault(d => d.Name.Equals(_selection, StringComparison.OrdinalIgnoreCase))
               ?? devs.FirstOrDefault(d => d.Name.Contains(_selection, StringComparison.OrdinalIgnoreCase));
    }

    public ICameraStream? Open(CameraFacing facing, int width, int height, int fps)
    {
        var desc = Select();
        if (desc == null)
        {
            Log.Info(_selection == null ? "Camera: no capture device found" : $"Camera: capture device '{_selection}' not found");
            return null;
        }
        var ch = Choose(desc, width, height, fps);
        if (ch == null) { Log.Warn($"Camera: '{desc.Name}' has no supported uncompressed format"); return null; }
        var mode = (desc.Name, desc.DeviceType, ch.Width, ch.Height, ch.PixelFormat, (double)ch.FramesPerSecond);
        var stream = _shared.Acquire(mode, after => new Capture(desc, ch, after), out bool reused);
        Log.Info($"Camera: {facing} camera -> '{desc.Name}' ({desc.DeviceType}) {ch}{(reused ? " (capture already running, reused)" : "")}");
        return stream;
    }

    /// <summary>Stops the capture and releases the device (bounded wait).</summary>
    public void Dispose() => _shared.Dispose();

    static CameraHostFormat? Map(PixelFormats f) => f switch
    {
        PixelFormats.YUYV => CameraHostFormat.Yuyv,
        PixelFormats.UYVY => CameraHostFormat.Uyvy,
        PixelFormats.NV12 => CameraHostFormat.Nv12,
        PixelFormats.RGB24 => CameraHostFormat.Bgr24,
        PixelFormats.RGB32 or PixelFormats.ARGB32 => CameraHostFormat.Bgra32,
        _ => null, // JPEG/PNG would need a decoder; RGB8/15/16 are not produced by real webcams
    };

    /// <summary>Picks the capture mode closest to the request: at least the requested size (smallest such), the
    /// same aspect ratio, at least the requested rate, YUV formats first.</summary>
    public static VideoCharacteristics? Choose(CaptureDeviceDescriptor desc, int width, int height, int fps) =>
        Choose(desc.Characteristics, width, height, fps);

    public static VideoCharacteristics? Choose(IEnumerable<VideoCharacteristics> modes, int width, int height, int fps)
    {
        double aspect = (double)width / height;
        return modes
            .Where(c => Map(c.PixelFormat) != null && c.Width > 0 && c.Height > 0)
            .OrderBy(c => c.Width >= width && c.Height >= height ? 0 : 1)
            .ThenBy(c => (double)c.FramesPerSecond >= fps - 0.5 ? 0 : 1)
            .ThenBy(c => Math.Round(Math.Abs((double)c.Width / c.Height - aspect), 2))
            .ThenBy(c => c.Width >= width && c.Height >= height ? (long)c.Width * c.Height : -(long)c.Width * c.Height)
            .ThenBy(c => c.PixelFormat switch { PixelFormats.YUYV => 0, PixelFormats.NV12 => 1, PixelFormats.UYVY => 2, _ => 3 })
            .ThenByDescending(c => (double)c.FramesPerSecond)
            .FirstOrDefault();
    }

    /// <summary>Raw image bytes of a FlashCap buffer: FlashCap wraps the device data in a BMP file header even when
    /// not transcoding; returns the pixel data offset and whether rows are bottom-up.</summary>
    public static (int Offset, bool BottomUp) ParseBitmap(ReadOnlySpan<byte> data, int expected)
    {
        if (data.Length >= 54 && data[0] == 'B' && data[1] == 'M')
        {
            int off = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
            int h = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
            if (off >= 54 && off <= data.Length - expected) return (off, h > 0);
        }
        return (Math.Max(0, data.Length - expected), false);
    }

    /// <summary>
    /// One opened capture device. Opening runs in the background (a DirectShow graph can take a second: the guest
    /// thread that called sceCameraStart never waits for it). A start that fails (device busy, driver error, the
    /// process out of window handles...) is retried with a growing delay, and at once when a guest starts a camera
    /// again, until it succeeds or the capture is stopped; until then the Vita cameras deliver grey frames.
    /// </summary>
    sealed class Capture : IHostCapture
    {
        readonly CameraFrameBuffer _frames = new();
        readonly CancellationTokenSource _cts = new();
        readonly SemaphoreSlim _retry = new(0, 1);
        readonly object _lock = new();
        readonly CaptureDeviceDescriptor _desc;
        readonly VideoCharacteristics _ch;
        readonly CameraHostFormat _format;
        readonly int _w, _h, _expected;
        readonly bool _rgb;
        readonly Task _run;
        CaptureDevice? _device;
        volatile bool _waitingRetry;
        bool _loggedError;

        public CameraFrameBuffer Frames => _frames;

        public Capture(CaptureDeviceDescriptor desc, VideoCharacteristics ch, Task after)
        {
            _desc = desc;
            _ch = ch;
            _format = Map(ch.PixelFormat)!.Value;
            _w = ch.Width; _h = ch.Height;
            _expected = CameraImage.HostSize(_format, _w, _h);
            _rgb = _format is CameraHostFormat.Bgr24 or CameraHostFormat.Bgra32;
            _run = Task.Run(() => RunAsync(after));
        }

        async Task RunAsync(Task after)
        {
            var ct = _cts.Token;
            try { await after.WaitAsync(TimeSpan.FromSeconds(5), ct); } // the previous capture of this device closes first
            catch (OperationCanceledException) { return; }
            catch { }
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var dev = await _desc.OpenAsync(_ch, TranscodeFormats.DoNotTranscode, false, 1, OnFrame, ct);
                    lock (_lock) _device = dev;
                    ct.ThrowIfCancellationRequested();
                    await dev.StartAsync(ct);
                    if (attempt > 1) Log.Info($"Camera: '{_desc.Name}' started on attempt {attempt}");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    if (attempt == 1) Log.Warn($"Camera: could not start '{_desc.Name}': {e.Message} (grey frames, retrying)");
                    else if (attempt % 10 == 0) Log.Warn($"Camera: '{_desc.Name}' still failing after {attempt} attempts: {e.Message}");
                    await CloseDeviceAsync(); // release whatever the failed start opened before trying again
                }
                _waitingRetry = true;
                try { await _retry.WaitAsync(TimeSpan.FromSeconds(Math.Min(10, 2 * attempt)), ct); }
                catch (OperationCanceledException) { return; }
                finally { _waitingRetry = false; }
            }
        }

        public void RetryNow()
        {
            if (!_waitingRetry) return;
            try { _retry.Release(); } catch (SemaphoreFullException) { }
        }

        void OnFrame(PixelBufferScope scope)
        {
            try
            {
                var seg = scope.Buffer.ReferImage();
                var data = seg.AsSpan();
                var (off, bottomUp) = ParseBitmap(data, _expected);
                if (data.Length - off < _expected) return; // truncated frame
                var pixels = seg.Slice(off, _expected);
                _frames.Publish(_w, _h, dst => CameraImage.DecodeToRgba(pixels, _format, _w, _h, _rgb && bottomUp, dst));
            }
            catch (Exception e)
            {
                if (!_loggedError) { _loggedError = true; Log.Warn($"Camera: frame conversion failed: {e.Message}"); }
            }
        }

        async Task CloseDeviceAsync()
        {
            CaptureDevice? dev;
            lock (_lock) { dev = _device; _device = null; }
            if (dev == null) return;
            try { await dev.StopAsync(); } catch { }
            try { await dev.DisposeAsync(); } catch { }
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            try { await _run; } catch { }
            await CloseDeviceAsync();
            _cts.Dispose();
        }
    }
}
