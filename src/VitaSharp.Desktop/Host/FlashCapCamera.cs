using System.Buffers.Binary;
using FlashCap;
using VitaSharp.Core.Host;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// Host webcam for SceCamera through FlashCap (DirectShow / Media Foundation on Windows, V4L2 on Linux,
/// AVFoundation on macOS). Both Vita cameras map to the selected host device. Capture runs on FlashCap's thread;
/// each frame is converted to RGBA into a <see cref="CameraFrameBuffer"/> that keeps only the latest one.
/// </summary>
public sealed class FlashCapCamera : ICameraSource
{
    readonly string? _selection;
    List<CaptureDeviceDescriptor>? _devices;

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
            var all = new CaptureDevices().EnumerateDescriptors()
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
        Log.Info($"Camera: {facing} camera -> '{desc.Name}' ({desc.DeviceType}) {ch}");
        return new Stream(desc, ch);
    }

    public void Dispose() { }

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

    sealed class Stream : ICameraStream
    {
        readonly CameraFrameBuffer _frames = new();
        readonly CancellationTokenSource _cts = new();
        readonly Task _run;
        readonly CameraHostFormat _format;
        readonly int _w, _h, _expected;
        readonly bool _rgb;
        CaptureDevice? _device;
        bool _loggedError;

        public CameraFrameBuffer Frames => _frames;

        public Stream(CaptureDeviceDescriptor desc, VideoCharacteristics ch)
        {
            _format = Map(ch.PixelFormat)!.Value;
            _w = ch.Width; _h = ch.Height;
            _expected = CameraImage.HostSize(_format, _w, _h);
            _rgb = _format is CameraHostFormat.Bgr24 or CameraHostFormat.Bgra32;
            // opening a DirectShow graph can take a second: never block the guest thread that called sceCameraStart
            _run = Task.Run(async () =>
            {
                try
                {
                    var dev = await desc.OpenAsync(ch, TranscodeFormats.DoNotTranscode, false, 1, OnFrame, _cts.Token);
                    lock (_frames) _device = dev;
                    if (_cts.IsCancellationRequested) return;
                    await dev.StartAsync(_cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { Log.Warn($"Camera: could not start '{desc.Name}': {e.Message} (grey frames)"); }
            });
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

        public void Dispose()
        {
            _cts.Cancel();
            // stop on a worker: DirectShow teardown may block a little; wait a bounded time
            var stop = Task.Run(async () =>
            {
                try { await _run; } catch { }
                CaptureDevice? dev;
                lock (_frames) { dev = _device; _device = null; }
                if (dev == null) return;
                try { await dev.StopAsync(); } catch { }
                try { await dev.DisposeAsync(); } catch { }
            });
            stop.Wait(TimeSpan.FromSeconds(3));
        }
    }
}
