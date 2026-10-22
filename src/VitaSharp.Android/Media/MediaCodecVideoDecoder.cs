using System.Runtime.InteropServices;
using VitaSharp.Core.Media;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Media;

/// <summary>
/// SKELETON (not built or tested yet: there is no Android front-end). Hardware H.264 decoding through the NDK
/// MediaCodec API (libmediandk.so, API 21+), behind the same <see cref="IVideoDecoder"/> interface the desktop's
/// FFmpeg decoder implements. The Android front-end installs it with
/// <c>VideoDecoders.Backend = new MediaCodecVideoBackend();</c>, keeping FFmpeg (software) as the fallback.
///
/// Remaining work:
/// <list type="bullet">
/// <item>Output layouts: only COLOR_FormatYUV420Planar (19) and COLOR_FormatYUV420SemiPlanar (21) are copied here.
///   Many vendor decoders output tiled/opaque formats (e.g. Qualcomm 0x7FA30C04): read them through an AImageReader
///   (AIMAGE_FORMAT_YUV_420_888, per-plane pixel/row strides) instead of the raw output buffer.</item>
/// <item>Crop rectangle (crop-left/top/right/bottom in the output format) instead of width/height only.</item>
/// <item>Colour metadata (color-range, color-standard) into <see cref="DecodedPicture.FullRange"/>/Bt709.</item>
/// <item>KEY_LOW_LATENCY (API 30) / vendor low-latency keys, so pictures come out without the decoder's reorder delay
///   the guest does not expect (the Vita decoder is low delay).</item>
/// <item>Asynchronous mode (AMediaCodec_setAsyncNotifyCallback) instead of polling with small timeouts.</item>
/// <item>Fallback: when configure/start or a later call fails, switch to the FFmpeg software decoder and replay the
///   access units since the last IDR, like FFmpegVideoDecoder does for hwaccel failures.</item>
/// <item>Lifecycle: release codecs when the activity pauses (codec instances are a shared, limited resource).</item>
/// <item>Validation on devices: bit-exactness on the I_PCM test stream (VideoDecoderTests), F1 2011 movies.</item>
/// </list>
/// </summary>
public sealed class MediaCodecVideoBackend : IVideoDecoderBackend
{
    public string Name => "mediacodec";

    public IVideoDecoder? CreateH264(VideoDecoderOptions options)
    {
        var d = MediaCodecVideoDecoder.Create(options.MaxWidth > 0 ? options.MaxWidth : 960, options.MaxHeight > 0 ? options.MaxHeight : 544);
        // no hardware decoder for this stream: software through FFmpeg (when it is bundled with the app)
        return d ?? new FFmpegVideoBackend { Hardware = HardwareDecode.Off }.CreateH264(options);
    }
}

/// <summary>Synchronous NDK MediaCodec H.264 decoder (see <see cref="MediaCodecVideoBackend"/> for what remains).</summary>
public sealed unsafe class MediaCodecVideoDecoder : IVideoDecoder
{
    const int ColorFormatYuv420Planar = 19, ColorFormatYuv420SemiPlanar = 21;
    const int BufferFlagEndOfStream = 4;
    const long InputTimeoutUs = 10_000, OutputTimeoutUs = 0;

    nint _codec;
    readonly Queue<DecodedPicture> _ready = new();
    readonly Stack<DecodedPicture> _free = new();
    int _width, _height, _stride, _sliceHeight, _colorFormat = ColorFormatYuv420SemiPlanar;

    public string Name => "mediacodec";
    public bool IsHardware => true;

    MediaCodecVideoDecoder(nint codec, int w, int h) { _codec = codec; _width = _stride = w; _height = _sliceHeight = h; }

    public static MediaCodecVideoDecoder? Create(int width, int height)
    {
        nint codec = MediaNdk.AMediaCodec_createDecoderByType("video/avc");
        if (codec == 0) return null;
        nint fmt = MediaNdk.AMediaFormat_new();
        MediaNdk.AMediaFormat_setString(fmt, "mime", "video/avc");
        MediaNdk.AMediaFormat_setInt32(fmt, "width", width);
        MediaNdk.AMediaFormat_setInt32(fmt, "height", height);
        MediaNdk.AMediaFormat_setInt32(fmt, "color-format", ColorFormatYuv420SemiPlanar);
        // SPS/PPS arrive in band with each IDR (Annex B), so no csd-0/csd-1 is set here
        int r = MediaNdk.AMediaCodec_configure(codec, fmt, 0, 0, 0);
        MediaNdk.AMediaFormat_delete(fmt);
        if (r != 0 || MediaNdk.AMediaCodec_start(codec) != 0)
        {
            Log.Warn($"MediaCodec: video/avc decoder could not be configured ({r})");
            MediaNdk.AMediaCodec_delete(codec);
            return null;
        }
        return new MediaCodecVideoDecoder(codec, width, height);
    }

    public void Send(ReadOnlySpan<byte> accessUnit, long pts)
    {
        if (_codec == 0) return;
        nint idx = MediaNdk.AMediaCodec_dequeueInputBuffer(_codec, InputTimeoutUs);
        if (idx < 0) { Log.WarnOnce("mediacodec-in", "MediaCodec: no input buffer available, access unit dropped"); Drain(); return; }
        byte* buf = MediaNdk.AMediaCodec_getInputBuffer(_codec, (nuint)idx, out nuint size);
        int n = (int)Math.Min((nuint)accessUnit.Length, size);
        accessUnit[..n].CopyTo(new Span<byte>(buf, n));
        MediaNdk.AMediaCodec_queueInputBuffer(_codec, (nuint)idx, 0, (nuint)n, (ulong)pts, 0);
        Drain();
    }

    void Drain()
    {
        while (true)
        {
            MediaNdk.BufferInfo info;
            nint idx = MediaNdk.AMediaCodec_dequeueOutputBuffer(_codec, &info, OutputTimeoutUs);
            if (idx == MediaNdk.InfoOutputFormatChanged) { ReadOutputFormat(); continue; }
            if (idx < 0) return; // try again later / buffers changed
            byte* data = MediaNdk.AMediaCodec_getOutputBuffer(_codec, (nuint)idx, out nuint size);
            if (data != null && info.Size > 0) _ready.Enqueue(Copy(data + info.Offset, info.PresentationTimeUs));
            MediaNdk.AMediaCodec_releaseOutputBuffer(_codec, (nuint)idx, false);
            if ((info.Flags & BufferFlagEndOfStream) != 0) return;
        }
    }

    void ReadOutputFormat()
    {
        nint f = MediaNdk.AMediaCodec_getOutputFormat(_codec);
        if (MediaNdk.AMediaFormat_getInt32(f, "width", out int w)) _width = w;
        if (MediaNdk.AMediaFormat_getInt32(f, "height", out int h)) _height = h;
        _stride = MediaNdk.AMediaFormat_getInt32(f, "stride", out int s) && s > 0 ? s : _width;
        _sliceHeight = MediaNdk.AMediaFormat_getInt32(f, "slice-height", out int sh) && sh > 0 ? sh : _height;
        if (MediaNdk.AMediaFormat_getInt32(f, "color-format", out int cf)) _colorFormat = cf;
        if (_colorFormat is not (ColorFormatYuv420Planar or ColorFormatYuv420SemiPlanar))
            Log.WarnOnce($"mediacodec-cf{_colorFormat}", $"MediaCodec: output colour format 0x{_colorFormat:X} not handled yet (needs AImageReader)");
        MediaNdk.AMediaFormat_delete(f);
    }

    DecodedPicture Copy(byte* src, long pts)
    {
        int w = _width, h = _height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        DecodedPicture? p = null;
        while (_free.Count > 0 && p == null) { var c = _free.Pop(); if (c.Y.Length == w * h) p = c; }
        p ??= new DecodedPicture { Y = new byte[w * h], U = new byte[cw * ch], V = new byte[cw * ch] };
        p.Width = w; p.Height = h; p.Pts = pts;
        for (int y = 0; y < h; y++) new ReadOnlySpan<byte>(src + y * _stride, w).CopyTo(p.Y.AsSpan(y * w));
        byte* chroma = src + _stride * _sliceHeight;
        if (_colorFormat == ColorFormatYuv420SemiPlanar)
        {
            for (int y = 0; y < ch; y++)
            {
                byte* row = chroma + y * _stride;
                for (int x = 0; x < cw; x++) { p.U[y * cw + x] = row[2 * x]; p.V[y * cw + x] = row[2 * x + 1]; }
            }
        }
        else
        {
            int cs = _stride / 2;
            byte* v = chroma + cs * (_sliceHeight / 2);
            for (int y = 0; y < ch; y++)
            {
                new ReadOnlySpan<byte>(chroma + y * cs, cw).CopyTo(p.U.AsSpan(y * cw));
                new ReadOnlySpan<byte>(v + y * cs, cw).CopyTo(p.V.AsSpan(y * cw));
            }
        }
        return p;
    }

    public void Flush()
    {
        if (_codec == 0) return;
        nint idx = MediaNdk.AMediaCodec_dequeueInputBuffer(_codec, InputTimeoutUs);
        if (idx >= 0) MediaNdk.AMediaCodec_queueInputBuffer(_codec, (nuint)idx, 0, 0, 0, BufferFlagEndOfStream);
        Drain();
        MediaNdk.AMediaCodec_flush(_codec); // accepts a new stream afterwards
    }

    public void Reset()
    {
        if (_codec != 0) MediaNdk.AMediaCodec_flush(_codec);
        _ready.Clear();
    }

    public DecodedPicture? Take() => _ready.Count > 0 ? _ready.Dequeue() : null;

    public void Recycle(DecodedPicture picture) { if (_free.Count < 4) _free.Push(picture); }

    public void Dispose()
    {
        if (_codec == 0) return;
        MediaNdk.AMediaCodec_stop(_codec);
        MediaNdk.AMediaCodec_delete(_codec);
        _codec = 0;
    }
}

/// <summary>NDK media API (media/NdkMediaCodec.h, media/NdkMediaFormat.h).</summary>
internal static unsafe partial class MediaNdk
{
    const string Lib = "libmediandk.so";
    public const nint InfoTryAgainLater = -1, InfoOutputFormatChanged = -2, InfoOutputBuffersChanged = -3;

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferInfo
    {
        public int Offset, Size;
        public long PresentationTimeUs;
        public uint Flags;
    }

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint AMediaCodec_createDecoderByType(string mime);
    [LibraryImport(Lib)] public static partial int AMediaCodec_configure(nint codec, nint format, nint surface, nint crypto, uint flags);
    [LibraryImport(Lib)] public static partial int AMediaCodec_start(nint codec);
    [LibraryImport(Lib)] public static partial int AMediaCodec_stop(nint codec);
    [LibraryImport(Lib)] public static partial int AMediaCodec_flush(nint codec);
    [LibraryImport(Lib)] public static partial int AMediaCodec_delete(nint codec);
    [LibraryImport(Lib)] public static partial nint AMediaCodec_dequeueInputBuffer(nint codec, long timeoutUs);
    [LibraryImport(Lib)] public static partial byte* AMediaCodec_getInputBuffer(nint codec, nuint idx, out nuint size);
    [LibraryImport(Lib)] public static partial int AMediaCodec_queueInputBuffer(nint codec, nuint idx, long offset, nuint size, ulong timeUs, uint flags);
    [LibraryImport(Lib)] public static partial nint AMediaCodec_dequeueOutputBuffer(nint codec, BufferInfo* info, long timeoutUs);
    [LibraryImport(Lib)] public static partial byte* AMediaCodec_getOutputBuffer(nint codec, nuint idx, out nuint size);
    [LibraryImport(Lib)] public static partial int AMediaCodec_releaseOutputBuffer(nint codec, nuint idx, [MarshalAs(UnmanagedType.U1)] bool render);
    [LibraryImport(Lib)] public static partial nint AMediaCodec_getOutputFormat(nint codec);
    [LibraryImport(Lib)] public static partial nint AMediaFormat_new();
    [LibraryImport(Lib)] public static partial int AMediaFormat_delete(nint format);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void AMediaFormat_setString(nint format, string name, string value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void AMediaFormat_setInt32(nint format, string name, int value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AMediaFormat_getInt32(nint format, string name, out int value);
}
