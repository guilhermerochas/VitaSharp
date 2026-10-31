using Android.Media;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Media;

/// <summary>
/// Built into the app, not run on a device yet. The Android video decoder backend: H.264 through MediaCodec (the .NET
/// for Android bindings of android.media.MediaCodec), surface-less, into the planar YUV <see cref="DecodedPicture"/>
/// that SceAvcdec/SceVideodec and the SceAvPlayer media player hand to the existing upload path. It is the default
/// backend on Android (EmulationSession installs it); FFmpeg is not part of the app.
/// <list type="bullet">
/// <item>Decoders: the device's hardware H.264 decoders first, then Android's own software decoder
///   (c2.android.avc.decoder / OMX.google.h264.decoder), which every device has. When a decoder fails while running,
///   the next one continues from the last IDR picture (<see cref="DecoderReplay"/>, the same scheme as the desktop's
///   FFmpeg hwaccel fallback).</item>
/// <item>Output: COLOR_FormatYUV420Flexible read through MediaCodec.getOutputImage (YUV_420_888 planes with their row
///   and pixel strides, so vendor layouts work), inside the crop rectangle; decoders that give no image fall back to
///   the raw buffer in the two standard layouts.</item>
/// <item>Low delay: KEY_LOW_LATENCY (API 30) where the decoder supports it and realtime priority, so pictures come out
///   without a reorder delay the guest does not expect (the Vita's decoder is low delay); colour range and standard
///   from the output format.</item>
/// </list>
/// </summary>
public sealed class MediaCodecVideoBackend : IVideoDecoderBackend
{
    public string Name => "mediacodec";

    public IVideoDecoder? CreateH264(VideoDecoderOptions options) =>
        MediaCodecVideoDecoder.Create(options.MaxWidth > 0 ? options.MaxWidth : 960, options.MaxHeight > 0 ? options.MaxHeight : 544);
}

/// <summary>A synchronous MediaCodec H.264 decoder (see <see cref="MediaCodecVideoBackend"/>).</summary>
public sealed unsafe class MediaCodecVideoDecoder : IVideoDecoder
{
    const string Mime = "video/avc";
    const int InfoTryAgainLater = -1, InfoOutputFormatChanged = -2, InfoOutputBuffersChanged = -3;
    const long InputTimeoutUs = 10_000;
    // an access unit waits at most this long for an input buffer while the output is drained
    const int InputAttempts = 20;

    /// <summary>Process-wide counters (PERF, tests on a device): pictures decoded, decoder fallbacks.</summary>
    public static long Pictures, Fallbacks;

    readonly int _maxWidth, _maxHeight;
    readonly string[] _candidates;
    int _next;
    MediaCodec? _codec;
    string _codecName = "";
    bool _hardware;
    readonly MediaCodec.BufferInfo _info = new();
    readonly Queue<DecodedPicture> _ready = new();
    readonly Stack<DecodedPicture> _free = new();
    readonly DecoderReplay _replay = new();
    int _skip;
    // SceAvcdec hands a picture out in the call that decoded it; MediaCodec decodes asynchronously and holds several
    // pictures in flight (IVideoDecoder.Asynchronous: SceAvcdec repeats or skips pictures to stay in step)
    long _queued, _produced;
    const long OutputGraceUs = 5_000;
    // output format (raw-buffer path and crop)
    int _width, _height, _stride, _sliceHeight, _colorFormat, _cropX, _cropY;
    bool _fullRange, _bt709;

    public string Name => $"mediacodec-{_codecName}";
    public bool IsHardware => _hardware;
    public bool Asynchronous => true;

    MediaCodecVideoDecoder(string[] candidates, int maxWidth, int maxHeight)
    {
        _candidates = candidates;
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
    }

    public static MediaCodecVideoDecoder? Create(int maxWidth, int maxHeight)
    {
        var names = H264Decoders();
        if (names.Length == 0) { Log.Warn("MediaCodec: no H.264 decoder on this device"); return null; }
        var d = new MediaCodecVideoDecoder(names, maxWidth, maxHeight);
        if (!d.OpenNext()) return null;
        return d;
    }

    /// <summary>The device's H.264 decoders, hardware ones first (vendor names before Android's own c2.android/OMX.google).</summary>
    static string[] H264Decoders()
    {
        var list = new List<(string Name, bool Software)>();
        try
        {
            foreach (var info in new MediaCodecList(MediaCodecListKind.RegularCodecs).GetCodecInfos() ?? [])
            {
                if (info.IsEncoder || info.Name is not { } name) continue;
                if (!(info.GetSupportedTypes() ?? []).Any(t => string.Equals(t, Mime, StringComparison.OrdinalIgnoreCase))) continue;
                bool software = OperatingSystem.IsAndroidVersionAtLeast(29) ? info.IsSoftwareOnly
                    : name.StartsWith("OMX.google.", StringComparison.Ordinal) || name.StartsWith("c2.android.", StringComparison.Ordinal);
                list.Add((name, software));
            }
        }
        catch (Java.Lang.Exception e) { Log.Warn($"MediaCodec: listing decoders failed: {e.Message}"); }
        return list.OrderBy(c => c.Software).Select(c => c.Name).ToArray();
    }

    bool OpenNext()
    {
        while (_next < _candidates.Length)
        {
            string name = _candidates[_next++];
            MediaCodec? codec = null;
            try
            {
                codec = MediaCodec.CreateByCodecName(name);
                var fmt = MediaFormat.CreateVideoFormat(Mime, _maxWidth, _maxHeight);
                fmt.SetInteger(MediaFormat.KeyColorFormat, YuvImageCopy.ColorFormatYuv420Flexible);
                fmt.SetInteger(MediaFormat.KeyMaxInputSize, Math.Max(_maxWidth * _maxHeight, 256 * 1024));
                fmt.SetInteger("priority", 0); // realtime
                bool lowLatency = false;
                if (OperatingSystem.IsAndroidVersionAtLeast(30)
                    && codec.CodecInfo.GetCapabilitiesForType(Mime) is { } caps && caps.IsFeatureSupported("low-latency")) // FEATURE_LowLatency
                {
                    fmt.SetInteger(MediaFormat.KeyLowLatency, 1);
                    lowLatency = true;
                }
                // SPS/PPS arrive in band with each IDR (Annex B), so no csd-0/csd-1 is set here
                codec.Configure(fmt, null, null, MediaCodecConfigFlags.None);
                codec.Start();
                _codec = codec;
                _codecName = name;
                _hardware = OperatingSystem.IsAndroidVersionAtLeast(29) ? codec.CodecInfo.IsHardwareAccelerated
                    : !(name.StartsWith("OMX.google.", StringComparison.Ordinal) || name.StartsWith("c2.android.", StringComparison.Ordinal));
                _width = _stride = _maxWidth;
                _height = _sliceHeight = _maxHeight;
                _cropX = _cropY = 0;
                _colorFormat = YuvImageCopy.ColorFormatYuv420Flexible;
                Log.Info($"MediaCodec: H.264 with {name} ({(_hardware ? "hardware" : "software")}{(lowLatency ? ", low latency" : "")})");
                return true;
            }
            catch (Java.Lang.Exception e)
            {
                Log.Warn($"MediaCodec: {name} could not be configured: {e.Message}");
                try { codec?.Release(); } catch (Java.Lang.Exception) { }
            }
        }
        return false;
    }

    void ReleaseCodec()
    {
        var c = _codec;
        _codec = null;
        if (c == null) return;
        try { c.Stop(); } catch (Java.Lang.Exception) { }
        try { c.Release(); } catch (Java.Lang.Exception) { }
        c.Dispose();
    }

    /// <summary>The decoder failed: the next one continues from the last IDR picture.</summary>
    void FallBack(string why)
    {
        Interlocked.Increment(ref Fallbacks);
        string failed = _codecName;
        ReleaseCodec();
        if (!OpenNext()) { Log.Warn($"MediaCodec: {failed} failed ({why}) and no other H.264 decoder is left"); return; }
        Log.Warn($"MediaCodec: {failed} failed ({why}); continuing with {_codecName} from the last IDR picture");
        var (units, skip) = _replay.TakeReplay();
        _skip = skip;
        _queued = _produced = 0;
        foreach (var (data, pts) in units) Send(data, pts);
    }

    public void Send(ReadOnlySpan<byte> accessUnit, long pts)
    {
        if (_codec == null) return;
        _replay.Add(accessUnit, pts);
        try
        {
            Queue(accessUnit, pts, 0);
            Drain();
            // a short grace for a picture already in the works; never longer: the MediaTek decoder holds several
            // pictures in flight and waiting for each one (40 ms) slowed F1 2011's movies below real time
            if (_codec != null && _ready.Count == 0 && _produced > 0 && _queued > _produced) Drain(OutputGraceUs);
        }
        catch (Java.Lang.Exception e) { FallBack(e.Message ?? e.GetType().Name); }
    }

    void Queue(ReadOnlySpan<byte> data, long pts, MediaCodecBufferFlags flags)
    {
        var c = _codec!;
        for (int attempt = 0; attempt < InputAttempts; attempt++)
        {
            int idx = c.DequeueInputBuffer(InputTimeoutUs);
            if (idx < 0) { Drain(); continue; } // every input buffer is held: make room by taking pictures out
            using var buf = c.GetInputBuffer(idx)!;
            int n = Math.Min(data.Length, buf.Capacity());
            if (n < data.Length) Log.WarnOnce("mediacodec-in-size", $"MediaCodec: access unit of {data.Length} bytes truncated to {n}");
            data[..n].CopyTo(new Span<byte>((void*)buf.GetDirectBufferAddress(), n));
            c.QueueInputBuffer(idx, 0, n, Math.Max(0, pts), flags);
            if (n > 0) _queued++;
            return;
        }
        Log.WarnOnce("mediacodec-in", "MediaCodec: no input buffer available, access unit dropped");
    }

    /// <summary>Takes every finished picture out; true once the end-of-stream buffer came out.</summary>
    bool Drain(long timeoutUs = 0)
    {
        var c = _codec!;
        while (true)
        {
            int idx = c.DequeueOutputBuffer(_info, timeoutUs);
            if (idx >= 0) timeoutUs = 0; // waited for one buffer; the rest only if already there
            if (idx == InfoOutputFormatChanged) { ReadOutputFormat(c.OutputFormat); continue; }
            if (idx == InfoOutputBuffersChanged) continue;
            if (idx < 0) return false; // InfoTryAgainLater
            bool eos = (_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
            if (_info.Size > 0)
            {
                var p = Output(c, idx);
                if (p != null)
                {
                    Interlocked.Increment(ref Pictures);
                    _produced++;
                    _replay.Produced();
                    if (_skip > 0) { _skip--; Recycle(p); }
                    else _ready.Enqueue(p);
                }
            }
            c.ReleaseOutputBuffer(idx, false);
            if (eos) return true;
        }
    }

    void ReadOutputFormat(MediaFormat f)
    {
        int Get(string k, int d) => f.ContainsKey(k) ? f.GetInteger(k) : d;
        _width = Get(MediaFormat.KeyWidth, _width);
        _height = Get(MediaFormat.KeyHeight, _height);
        _stride = Math.Max(_width, Get(MediaFormat.KeyStride, _width));
        _sliceHeight = Math.Max(_height, Get(MediaFormat.KeySliceHeight, _height));
        _colorFormat = Get(MediaFormat.KeyColorFormat, _colorFormat);
        // crop-left/top/right/bottom (inclusive), present when the coded size is larger than the picture
        if (f.ContainsKey("crop-left") && f.ContainsKey("crop-right") && f.ContainsKey("crop-top") && f.ContainsKey("crop-bottom"))
        {
            _cropX = f.GetInteger("crop-left");
            _cropY = f.GetInteger("crop-top");
            _width = f.GetInteger("crop-right") - _cropX + 1;
            _height = f.GetInteger("crop-bottom") - _cropY + 1;
        }
        _fullRange = Get(MediaFormat.KeyColorRange, 2) == 1;                 // COLOR_RANGE_FULL = 1, LIMITED = 2
        _bt709 = Get(MediaFormat.KeyColorStandard, 0) == 1;                   // COLOR_STANDARD_BT709 = 1
        Log.Info($"MediaCodec: {_codecName} output {_width}x{_height} (stride {_stride}, slice height {_sliceHeight}, crop {_cropX},{_cropY}), " +
                 $"colour format 0x{_colorFormat:X}, {(_fullRange ? "full" : "limited")} range{(_bt709 ? ", BT.709" : "")}");
    }

    DecodedPicture Rent()
    {
        var p = _free.Count > 0 ? _free.Pop() : new DecodedPicture();
        p.Pts = _info.PresentationTimeUs;
        p.FullRange = _fullRange;
        p.Bt709 = _bt709;
        return p;
    }

    DecodedPicture? Output(MediaCodec c, int idx)
    {
        // YUV_420_888 image: works for every layout the decoder describes with strides
        using (var image = c.GetOutputImage(idx))
        {
            if (image != null && image.GetPlanes() is { Length: 3 } planes)
            {
                var crop = image.CropRect;
                int cx = crop?.Left ?? 0, cy = crop?.Top ?? 0;
                int w = crop is { } r && r.Width() > 0 ? r.Width() : image.Width;
                int h = crop is { } r2 && r2.Height() > 0 ? r2.Height() : image.Height;
                var p = Rent();
                YuvImageCopy.Copy(Plane(planes[0]), Plane(planes[1]), Plane(planes[2]), cx, cy, w, h, p);
                foreach (var pl in planes) pl.Dispose();
                return p;
            }
        }
        // no image (decoders without flexible YUV): the raw buffer in a standard layout
        using var buf = c.GetOutputBuffer(idx);
        if (buf == null) return null;
        var data = new ReadOnlySpan<byte>((byte*)buf.GetDirectBufferAddress() + _info.Offset, _info.Size);
        var pic = Rent();
        if (YuvImageCopy.CopyRaw(data, _colorFormat, _stride, _sliceHeight, _cropX, _cropY, _width, _height, pic)) return pic;
        Log.WarnOnce($"mediacodec-cf{_colorFormat}", $"MediaCodec: {_codecName} gives no image and an unknown colour format 0x{_colorFormat:X}: pictures dropped");
        _free.Push(pic);
        return null;
    }

    static YuvImageCopy.Plane Plane(global::Android.Media.Image.Plane p)
    {
        var b = p.Buffer!;
        // the plane's buffer starts at its first sample; its limit covers the last one
        var span = new ReadOnlySpan<byte>((byte*)b.GetDirectBufferAddress() + b.Position(), b.Remaining());
        return new YuvImageCopy.Plane(span, p.RowStride, p.PixelStride);
    }

    public void Flush()
    {
        if (_codec == null) return;
        try
        {
            Queue([], 0, MediaCodecBufferFlags.EndOfStream);
            // pictures held for reordering come out before the end-of-stream buffer
            for (int i = 0; i < 50; i++) if (Drain(10_000)) break;
            _codec?.Flush(); // accepts a new stream afterwards
        }
        catch (Java.Lang.Exception e) { FallBack(e.Message ?? e.GetType().Name); }
        _replay.Clear();
        _queued = _produced = 0;
    }

    public void Reset()
    {
        try { _codec?.Flush(); }
        catch (Java.Lang.Exception e) { FallBack(e.Message ?? e.GetType().Name); }
        while (_ready.Count > 0) Recycle(_ready.Dequeue());
        _replay.Clear();
        _queued = _produced = 0;
        _skip = 0;
    }

    public DecodedPicture? Take() => _ready.Count > 0 ? _ready.Dequeue() : null;

    public void Recycle(DecodedPicture picture) { if (_free.Count < 4) _free.Push(picture); }

    public void Dispose()
    {
        ReleaseCodec();
        _info.Dispose();
    }
}
