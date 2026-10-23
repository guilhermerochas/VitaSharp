using FFmpeg.AutoGen;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Media;

/// <summary>
/// The FFmpeg video decoder backend. Hardware decoding goes through FFmpeg's hwaccel API (the platform decoder does
/// the bitstream work, FFmpeg keeps parsing, reordering and timestamps): D3D11VA on Windows, VAAPI on Linux,
/// VideoToolbox on macOS. Pictures are downloaded to system memory (NV12), since the guest reads them from its own
/// memory anyway. Whenever the hardware path is unavailable or fails, decoding continues in software.
/// Policy: <see cref="Hardware"/>, set from VITASHARP_VIDEO_HW (off | auto | a device type name such as d3d11va,
/// dxva2, vaapi, videotoolbox, vulkan, cuda) or the front-end's --video-decoder option.
/// </summary>
public sealed class FFmpegVideoBackend : IVideoDecoderBackend
{
    public string Name => "ffmpeg";

    /// <summary>Hardware decoding policy (default: VITASHARP_VIDEO_HW, else <see cref="HardwareDecode.Auto"/>).</summary>
    public HardwareDecode Hardware { get; set; } = ParseEnvironment(out _);

    /// <summary>Device type forced by VITASHARP_VIDEO_HW, or none (the platform default).</summary>
    public AVHWDeviceType ForcedDevice { get; set; } = ParseEnvironmentDevice();

    static HardwareDecode ParseEnvironment(out string? value)
    {
        value = Environment.GetEnvironmentVariable("VITASHARP_VIDEO_HW")?.Trim().ToLowerInvariant();
        return IsOff(value) ? HardwareDecode.Off : HardwareDecode.Auto;
    }

    static bool IsOff(string? v) => v is "off" or "0" or "sw" or "software" or "false";

    static AVHWDeviceType ParseEnvironmentDevice()
    {
        ParseEnvironment(out var v);
        return ParseDevice(v, "VITASHARP_VIDEO_HW");
    }

    static AVHWDeviceType ParseDevice(string? v, string source)
    {
        if (string.IsNullOrEmpty(v) || v is "auto" or "on" or "1" or "hw" || IsOff(v)) return AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        if (!FFmpegLoader.Available) return AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        var t = ffmpeg.av_hwdevice_find_type_by_name(v);
        if (t == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) Log.Warn($"{source}={v}: unknown FFmpeg hardware device type, using the platform default");
        return t;
    }

    /// <summary>Applies a front-end setting: "auto"/"hw", "sw"/"off", or an FFmpeg hwaccel device type name.</summary>
    public void Configure(string setting)
    {
        var v = setting.Trim().ToLowerInvariant();
        Hardware = IsOff(v) ? HardwareDecode.Off : HardwareDecode.Auto;
        ForcedDevice = ParseDevice(v, "--video-decoder");
    }

    /// <summary>The platform's preferred hardware device type.</summary>
    public static AVHWDeviceType PlatformDevice =>
        OperatingSystem.IsWindows() ? AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
        : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX
        : OperatingSystem.IsLinux() ? AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI
        : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

    public IVideoDecoder? CreateH264(VideoDecoderOptions options)
    {
        if (!FFmpegLoader.Available) return null;
        var device = Hardware == HardwareDecode.Off ? AVHWDeviceType.AV_HWDEVICE_TYPE_NONE
            : ForcedDevice != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE ? ForcedDevice : PlatformDevice;
        return FFmpegVideoDecoder.Create(AVCodecID.AV_CODEC_ID_H264, device);
    }
}

/// <summary>
/// Decodes H.264 access units (Annex B elementary stream) one at a time with FFmpeg, as SceAvcdec does: software, or
/// hardware through hwaccel with an automatic switch to software when the hardware decoder fails (the access units
/// since the last IDR picture are replayed into the software decoder, so the stream continues without a gap).
/// </summary>
public sealed unsafe class FFmpegVideoDecoder : IVideoDecoder
{
    AVCodecContext* _ctx;
    readonly AVCodecID _codecId;
    readonly AVPacket* _pkt;
    readonly AVFrame* _frame, _swFrame;
    readonly Queue<DecodedPicture> _ready = new();
    AVHWDeviceType _device;
    AVPixelFormat _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    // access units since the last IDR picture, kept while decoding in hardware so a fallback can replay them
    readonly List<(byte[] Data, long Pts)> _sinceIdr = new();
    long _sinceIdrBytes;
    const long MaxReplayBytes = 16 << 20;
    bool _replayable = true;
    // pictures output since the last IDR (a replay after a fallback skips that many, so none is shown twice)
    int _producedSinceIdr, _skip;

    public string Name => _device == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE ? "ffmpeg-sw" : "ffmpeg-" + ffmpeg.av_hwdevice_get_type_name(_device);
    public bool IsHardware => _device != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

    /// <summary>Process-wide counters (tests, PERF): pictures decoded in hardware/software, hardware fallbacks.</summary>
    public static long HardwarePictures, SoftwarePictures, Fallbacks;
    /// <summary>Host time spent decoding (send + receive + surface download), in Stopwatch ticks, all decoders.</summary>
    public static long DecodeTicks;

    FFmpegVideoDecoder(AVCodecID codecId)
    {
        _codecId = codecId;
        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _swFrame = ffmpeg.av_frame_alloc();
    }

    /// <summary>H.264 decoder in software (legacy entry point).</summary>
    public static FFmpegVideoDecoder? Create() => Create(AVCodecID.AV_CODEC_ID_H264, AVHWDeviceType.AV_HWDEVICE_TYPE_NONE);

    /// <summary>A decoder using <paramref name="device"/> when possible, software otherwise; null without FFmpeg/codec.</summary>
    public static FFmpegVideoDecoder? Create(AVCodecID codecId, AVHWDeviceType device)
    {
        if (!FFmpegLoader.Available) return null;
        var d = new FFmpegVideoDecoder(codecId);
        if (device != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE && d.Open(device)) return d;
        if (d.Open(AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)) return d;
        d.Dispose();
        return null;
    }

    static readonly HashSet<AVHWDeviceType> _reportedUnavailable = new(), _reportedOpened = new();

    bool Open(AVHWDeviceType device)
    {
        var codec = ffmpeg.avcodec_find_decoder(_codecId);
        if (codec == null) return false;
        AVPixelFormat hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        if (device != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            for (int i = 0; ; i++)
            {
                var cfg = ffmpeg.avcodec_get_hw_config(codec, i);
                if (cfg == null) break;
                if ((cfg->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 && cfg->device_type == device) { hwFormat = cfg->pix_fmt; break; }
            }
            if (hwFormat == AVPixelFormat.AV_PIX_FMT_NONE) { ReportUnavailable(device, "the decoder has no such hwaccel"); return false; }
        }
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY; // the hardware decoder returns pictures as soon as possible
        ctx->thread_count = 1;
        if (device != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            if (MissingDeviceLibrary(device) is { } missing)
            {
                ffmpeg.avcodec_free_context(&ctx);
                ReportUnavailable(device, $"{missing} not found");
                return false;
            }
            AVBufferRef* hw = null;
            int r = ffmpeg.av_hwdevice_ctx_create(&hw, device, null, null, 0);
            if (r < 0)
            {
                ffmpeg.avcodec_free_context(&ctx);
                ReportUnavailable(device, $"device creation failed ({Error(r)})");
                return false;
            }
            // avcodec's default get_format picks the hardware surface format when a device context is set
            ctx->hw_device_ctx = ffmpeg.av_buffer_ref(hw);
            ffmpeg.av_buffer_unref(&hw);
        }
        if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
        {
            ffmpeg.avcodec_free_context(&ctx);
            if (device != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE) ReportUnavailable(device, "avcodec_open2 failed");
            return false;
        }
        _ctx = ctx;
        _device = device;
        _hwFormat = hwFormat;
        lock (_reportedOpened) if (_reportedOpened.Add(device)) Log.Info($"Video decoder: H.264 via {Name}");
        return true;
    }

    /// <summary>
    /// The Linux FFmpeg builds (BtbN's, see build/FFmpeg.targets) bind libva, libdrm and the Vulkan loader through
    /// implib-gen stubs instead of dlopen: the first call into a library that is not installed aborts the whole process
    /// ("implib-gen: libva-drm.so.2: failed to load library", then an assertion) instead of failing device creation.
    /// So those device types are only tried when their libraries load.
    /// </summary>
    static readonly Dictionary<AVHWDeviceType, string[]> LinuxDeviceLibraries = new()
    {
        [AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI] = ["libva.so.2", "libva-drm.so.2", "libva-x11.so.2", "libdrm.so.2"],
        [AVHWDeviceType.AV_HWDEVICE_TYPE_DRM] = ["libdrm.so.2"],
        [AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN] = ["libvulkan.so.1"],
    };

    static readonly Dictionary<AVHWDeviceType, string?> _missingLibrary = new();

    /// <summary>A shared library <paramref name="device"/> needs that cannot be loaded, or null.</summary>
    static string? MissingDeviceLibrary(AVHWDeviceType device)
    {
        if (!OperatingSystem.IsLinux() || !LinuxDeviceLibraries.TryGetValue(device, out var libs)) return null;
        lock (_missingLibrary)
        {
            if (_missingLibrary.TryGetValue(device, out var known)) return known;
            string? missing = null;
            foreach (var lib in libs)
            {
                if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(lib, out var h)) { missing = lib; break; }
                System.Runtime.InteropServices.NativeLibrary.Free(h);
            }
            _missingLibrary[device] = missing;
            return missing;
        }
    }

    static void ReportUnavailable(AVHWDeviceType device, string why)
    {
        lock (_reportedUnavailable)
            if (_reportedUnavailable.Add(device))
                Log.Info($"Video decoder: hardware decoding ({ffmpeg.av_hwdevice_get_type_name(device)}) unavailable: {why}; using software");
    }

    static string Error(int code)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return new string((sbyte*)buf);
    }

    void Close()
    {
        var c = _ctx;
        if (c != null) ffmpeg.avcodec_free_context(&c);
        _ctx = null;
    }

    /// <summary>The hardware decoder failed: continue in software from the last IDR picture.</summary>
    void FallBackToSoftware(string why)
    {
        Interlocked.Increment(ref Fallbacks);
        Log.Warn($"Video decoder: {Name} failed ({why}); switching to software decoding");
        Close();
        if (!Open(AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)) { Log.Error("Video decoder: software H.264 decoder unavailable"); return; }
        // pictures already output (taken or still queued) come out of the replay again: skip them
        var replay = _replayable ? _sinceIdr.ToArray() : [];
        _skip = replay.Length > 0 ? _producedSinceIdr : 0;
        _producedSinceIdr = 0;
        _sinceIdr.Clear();
        _sinceIdrBytes = 0;
        foreach (var (data, pts) in replay) SendPacket(data, pts);
    }

    /// <summary>Tests: behaves as if the hardware decoder had just failed.</summary>
    internal void SimulateHardwareFailure() { if (IsHardware) FallBackToSoftware("simulated failure"); }

    /// <summary>True when the Annex B access unit contains an IDR slice (NAL type 5).</summary>
    internal static bool ContainsIdr(ReadOnlySpan<byte> au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0) continue;
            int hdr = au[i + 2] == 1 ? i + 3 : au[i + 2] == 0 && au[i + 3] == 1 ? i + 4 : -1;
            if (hdr < 0 || hdr >= au.Length) continue;
            if ((au[hdr] & 0x1F) == 5) return true;
            i = hdr;
        }
        return false;
    }

    public void Send(ReadOnlySpan<byte> au, long pts)
    {
        if (_ctx == null) return;
        if (IsHardware)
        {
            if (ContainsIdr(au)) { _sinceIdr.Clear(); _sinceIdrBytes = 0; _replayable = true; _producedSinceIdr = 0; }
            if (_sinceIdrBytes + au.Length <= MaxReplayBytes) { _sinceIdr.Add((au.ToArray(), pts)); _sinceIdrBytes += au.Length; }
            else _replayable = false; // too long since the last IDR: a fallback restarts at the next one
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SendPacket(au, pts);
        Interlocked.Add(ref DecodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
    }

    void SendPacket(ReadOnlySpan<byte> au, long pts)
    {
        var buf = (byte*)ffmpeg.av_malloc((ulong)au.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        au.CopyTo(new Span<byte>(buf, au.Length));
        new Span<byte>(buf + au.Length, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();
        if (ffmpeg.av_packet_from_data(_pkt, buf, au.Length) < 0) { ffmpeg.av_free(buf); return; }
        _pkt->pts = pts;
        int r = ffmpeg.avcodec_send_packet(_ctx, _pkt);
        ffmpeg.av_packet_unref(_pkt);
        if (r < 0 && r != ffmpeg.AVERROR(ffmpeg.EAGAIN) && IsHardware) { FallBackToSoftware($"send_packet: {Error(r)}"); return; }
        if (r >= 0) Receive();
    }

    /// <summary>Drains pictures still held for reordering (end of stream).</summary>
    public void Flush()
    {
        if (_ctx == null) return;
        ffmpeg.avcodec_send_packet(_ctx, null);
        Receive();
        if (_ctx != null) ffmpeg.avcodec_flush_buffers(_ctx);
        _sinceIdr.Clear();
        _sinceIdrBytes = 0;
    }

    public void Reset()
    {
        if (_ctx != null) ffmpeg.avcodec_flush_buffers(_ctx);
        _ready.Clear();
        _sinceIdr.Clear();
        _sinceIdrBytes = 0;
        _skip = _producedSinceIdr = 0;
    }

    public DecodedPicture? Take() => _ready.Count > 0 ? _ready.Dequeue() : null;

    /// <summary>Pictures the caller is done with; their planes are reused by later frames of the same size.</summary>
    readonly Stack<DecodedPicture> _free = new();

    public void Recycle(DecodedPicture p) { if (_free.Count < 4) _free.Push(p); }

    DecodedPicture RentPicture(int w, int h, int cw, int ch)
    {
        while (_free.Count > 0)
        {
            var p = _free.Pop();
            if (p.Y.Length == w * h && p.U.Length == cw * ch) return p; // other sizes are dropped
        }
        return new DecodedPicture { Y = new byte[w * h], U = new byte[cw * ch], V = new byte[cw * ch] };
    }

    void Receive()
    {
        while (true)
        {
            int r = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF) return;
            if (r < 0)
            {
                if (IsHardware) FallBackToSoftware($"receive_frame: {Error(r)}");
                return;
            }
            AVFrame* src = _frame;
            if (_hwFormat != AVPixelFormat.AV_PIX_FMT_NONE && (AVPixelFormat)_frame->format == _hwFormat)
            {
                int t = ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0);
                if (t < 0)
                {
                    ffmpeg.av_frame_unref(_frame);
                    FallBackToSoftware($"surface download: {Error(t)}");
                    return;
                }
                src = _swFrame;
                Interlocked.Increment(ref HardwarePictures);
            }
            else
            {
                Interlocked.Increment(ref SoftwarePictures);
                if (IsHardware)
                {
                    // FFmpeg's hwaccel setup refused the stream (profile, size, driver) and avcodec decodes in software
                    Log.Info($"Video decoder: {Name} declined the stream ({_frame->width}x{_frame->height}, profile {_ctx->profile}); FFmpeg decodes it in software");
                    _device = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
                    _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
                    _sinceIdr.Clear();
                    _sinceIdrBytes = 0;
                }
            }
            if (_skip > 0)
            {
                _skip--;
                if (src == _swFrame) ffmpeg.av_frame_unref(_swFrame);
                ffmpeg.av_frame_unref(_frame);
                continue;
            }
            _producedSinceIdr++;
            int w = _frame->width, h = _frame->height, cw = (w + 1) / 2, ch = (h + 1) / 2;
            var p = RentPicture(w, h, cw, ch);
            p.Width = w; p.Height = h; p.Pts = _frame->pts;
            p.FullRange = _frame->color_range == AVColorRange.AVCOL_RANGE_JPEG
                            || (AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            p.Bt709 = _frame->colorspace == AVColorSpace.AVCOL_SPC_BT709;
            CopyPlanes(src, p, w, h, cw, ch);
            _ready.Enqueue(p);
            if (src == _swFrame) ffmpeg.av_frame_unref(_swFrame);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    static void CopyPlanes(AVFrame* f, DecodedPicture p, int w, int h, int cw, int ch)
    {
        Copy(f->data[0], f->linesize[0], p.Y, w, h);
        var fmt = (AVPixelFormat)f->format;
        if (fmt == AVPixelFormat.AV_PIX_FMT_NV12)
        {
            for (int y = 0; y < ch; y++)
            {
                byte* row = f->data[1] + y * f->linesize[1];
                for (int x = 0; x < cw; x++) { p.U[y * cw + x] = row[2 * x]; p.V[y * cw + x] = row[2 * x + 1]; }
            }
        }
        else if (fmt is AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P)
        {
            Copy(f->data[1], f->linesize[1], p.U, cw, ch);
            Copy(f->data[2], f->linesize[2], p.V, cw, ch);
        }
        else
        {
            Log.WarnOnce("viddec-pix" + fmt, $"Video decoder: unsupported pixel format {fmt}, showing luma only");
            p.U.AsSpan().Fill(128);
            p.V.AsSpan().Fill(128);
        }
    }

    static void Copy(byte* src, int stride, byte[] dst, int w, int h)
    {
        fixed (byte* d = dst)
            for (int y = 0; y < h; y++) Buffer.MemoryCopy(src + y * stride, d + y * w, w, w);
    }

    public void Dispose()
    {
        Close();
        var p = _pkt; ffmpeg.av_packet_free(&p);
        var f = _frame; ffmpeg.av_frame_free(&f);
        var s = _swFrame; ffmpeg.av_frame_free(&s);
    }
}
