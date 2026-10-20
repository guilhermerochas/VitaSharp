using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibMp4Recorder (psp2/mp4rec.h): gameplay recording to MP4 (H.264 + AAC). Phase 1 functional stub: recorders
/// are tracked by the address of their SceMp4RecRecorder, samples are validated, counted and dropped, and no file is
/// written. sceMp4RecCsc is real (A8B8G8R8 → YUV420 packed with scaling), since the game may use its output.
/// Error codes are not documented: <see cref="SceError.INVALID_ARGUMENT"/> for null/unknown pointers, 0 otherwise.
/// Structs: SceMp4RecRecorder (0x10) size/base/base_size/unk; SceMp4RecInitParam (0x20) size/mode/encoder_mem/
/// encoder_size/av_mem/av_size/affinity/priority; SceMp4RecFrame (0x2C) size/pixelformat/stride/width/height/unk[16]/
/// buffer(+0x24)/reserved; SceMp4RecTermParam (0x20C) size/discard/reserved[128]/metadata.
/// </summary>
public sealed class Mp4Recorder : HleModule
{
    internal const int ERR_INVALID_ARGUMENT = SceError.INVALID_ARGUMENT;
    internal const uint PIXEL_A8B8G8R8 = 0x00, PIXEL_YUV420_PACKED = 0x20;
    const int AudioBufferSize = 4096;

    static readonly (uint W, uint H)[] Modes = [(640, 368), (640, 368), (480, 272), (480, 272), (368, 208), (368, 208)];

    sealed class Recorder
    {
        public int Mode = -1;
        public long VideoSamples, AudioSamples;
    }

    readonly object _lock = new();
    readonly Dictionary<uint, Recorder> _recorders = new();

    internal (long Video, long Audio)? Counts(uint rec)
    {
        lock (_lock) return _recorders.TryGetValue(rec, out var r) ? (r.VideoSamples, r.AudioSamples) : null;
    }

    Recorder? Find(uint rec)
    {
        lock (_lock) return rec != 0 && _recorders.TryGetValue(rec, out var r) ? r : null;
    }

    [Hle]
    int sceMp4RecCreateRecorder(uint rec)
    {
        if (rec == 0) return ERR_INVALID_ARGUMENT;
        uint size = GuestMemory.R32(rec);
        if (size != 0x10) Log.WarnOnce("mp4rec-size", $"sceMp4RecCreateRecorder: SceMp4RecRecorder.size = 0x{size:X} (expected 0x10)");
        Log.WarnOnce("mp4rec-stub", "SceLibMp4Recorder: video recording is not emulated, samples are dropped");
        lock (_lock) _recorders[rec] = new Recorder();
        return 0;
    }

    [Hle]
    int sceMp4RecDeleteRecorder(uint rec)
    {
        lock (_lock) return rec != 0 && _recorders.Remove(rec) ? 0 : ERR_INVALID_ARGUMENT;
    }

    /// <summary>Physically contiguous blocks for the video and audio encoders of <paramref name="mode"/>. Real values
    /// are unknown; the HLE does not use the memory, so plausible 256 KiB-aligned sizes are reported.</summary>
    [Hle]
    int sceMp4RecQueryPhysicalMemSize(uint rec, uint mode, uint encoderSize, uint avSize)
    {
        if (mode >= Modes.Length || encoderSize == 0 || avSize == 0) return ERR_INVALID_ARGUMENT;
        var (w, h) = Modes[mode];
        GuestMemory.W32(encoderSize, GuestMemory.AlignUp(w * h * 3 / 2 * 4, 0x40000));
        GuestMemory.W32(avSize, 0x40000);
        return 0;
    }

    [Hle]
    int sceMp4RecInit(uint rec, uint param)
    {
        var r = Find(rec);
        if (r == null || param == 0) return ERR_INVALID_ARGUMENT;
        uint mode = GuestMemory.R32(param + 4);
        if (mode >= Modes.Length) return ERR_INVALID_ARGUMENT;
        lock (_lock)
        {
            r.Mode = (int)mode;
            r.VideoSamples = r.AudioSamples = 0;
        }
        return 0;
    }

    [Hle]
    int sceMp4RecTerm(uint rec, uint param)
    {
        var r = Find(rec);
        if (r == null) return ERR_INVALID_ARGUMENT;
        bool discard = param != 0 && GuestMemory.R32(param + 4) != 0;
        long v, a;
        lock (_lock)
        {
            v = r.VideoSamples; a = r.AudioSamples;
            r.Mode = -1;
        }
        Log.Info($"sceMp4RecTerm: recording of {v} video / {a} audio samples {(discard ? "discarded" : "not saved (recording not emulated)")}");
        return 0;
    }

    [Hle]
    int sceMp4RecAddVideoSample(uint rec, uint buffer, int size)
    {
        var r = Find(rec);
        if (r == null || buffer == 0) return ERR_INVALID_ARGUMENT;
        lock (_lock)
        {
            if (r.Mode < 0) Log.WarnOnce("mp4rec-noinit", "sceMp4RecAdd*Sample before sceMp4RecInit");
            r.VideoSamples++;
        }
        return 0;
    }

    /// <summary>4096 bytes of S16 stereo 48 kHz (1024 frames) per call; other sizes are only logged (error code
    /// unknown).</summary>
    [Hle]
    int sceMp4RecAddAudioSample(uint rec, uint buffer, int size)
    {
        var r = Find(rec);
        if (r == null || buffer == 0) return ERR_INVALID_ARGUMENT;
        if (size != AudioBufferSize) Log.WarnOnce("mp4rec-audiosize", $"sceMp4RecAddAudioSample: size {size} (expected {AudioBufferSize})");
        lock (_lock)
        {
            if (r.Mode < 0) Log.WarnOnce("mp4rec-noinit", "sceMp4RecAdd*Sample before sceMp4RecInit");
            r.AudioSamples++;
        }
        return 0;
    }

    /// <summary>Colour conversion + bilinear rescale: A8B8G8R8 (bytes R,G,B,A) → YUV420 packed (Y plane of
    /// stride × height, then interleaved chroma with V before U, the SceVideodec YUV420_PACKED layout) using BT.601
    /// limited range. Same-format frames of equal size are copied.</summary>
    [Hle]
    int sceMp4RecCsc(uint dst, uint src)
    {
        if (dst == 0 || src == 0) return ERR_INVALID_ARGUMENT;
        uint sFmt = GuestMemory.R32(src + 4), dFmt = GuestMemory.R32(dst + 4);
        int sStride = (int)GuestMemory.R32(src + 8), sw = (int)GuestMemory.R32(src + 0xC), sh = (int)GuestMemory.R32(src + 0x10);
        int dStride = (int)GuestMemory.R32(dst + 8), dw = (int)GuestMemory.R32(dst + 0xC), dh = (int)GuestMemory.R32(dst + 0x10);
        uint sBuf = GuestMemory.R32(src + 0x24), dBuf = GuestMemory.R32(dst + 0x24);
        if (sBuf == 0 || dBuf == 0) return ERR_INVALID_ARGUMENT;
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0 || sStride < sw || dStride < dw || sStride > 8192 || dStride > 8192 || sh > 4096 || dh > 4096)
            return ERR_INVALID_ARGUMENT;
        if (sFmt == dFmt && sw == dw && sh == dh)
        {
            int bpr = sFmt == PIXEL_A8B8G8R8 ? 4 : 1;
            int rows = sFmt == PIXEL_A8B8G8R8 ? sh : sh + sh / 2;
            for (int y = 0; y < rows; y++)
                GuestMemory.Copy(dBuf + (uint)(y * dStride * bpr), sBuf + (uint)(y * sStride * bpr), (uint)(sw * bpr));
            return 0;
        }
        if (sFmt != PIXEL_A8B8G8R8 || dFmt != PIXEL_YUV420_PACKED) return ERR_INVALID_ARGUMENT;
        RgbaToYuv420Packed(sBuf, sStride, sw, sh, dBuf, dStride, dw, dh);
        return 0;
    }

    internal static void RgbaToYuv420Packed(uint sBuf, int sStride, int sw, int sh, uint dBuf, int dStride, int dw, int dh)
    {
        var y = new byte[dw * dh];
        var u = new int[dw * dh];
        var v = new int[dw * dh];
        var src = GuestMemory.Span(sBuf, sStride * 4 * (sh - 1) + sw * 4);
        for (int j = 0; j < dh; j++)
        {
            // bilinear sample at the destination pixel centre
            double fy = Math.Clamp((j + 0.5) * sh / dh - 0.5, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
            double wy = fy - y0;
            for (int i = 0; i < dw; i++)
            {
                double fx = Math.Clamp((i + 0.5) * sw / dw - 0.5, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                double wx = fx - x0;
                double r = 0, g = 0, b = 0;
                Accumulate(src, sStride, x0, y0, (1 - wx) * (1 - wy), ref r, ref g, ref b);
                Accumulate(src, sStride, x1, y0, wx * (1 - wy), ref r, ref g, ref b);
                Accumulate(src, sStride, x0, y1, (1 - wx) * wy, ref r, ref g, ref b);
                Accumulate(src, sStride, x1, y1, wx * wy, ref r, ref g, ref b);
                int k = j * dw + i;
                y[k] = (byte)Math.Clamp((int)Math.Round(16 + 0.256788 * r + 0.504129 * g + 0.097906 * b), 16, 235);
                u[k] = (int)Math.Round(128 - 0.148223 * r - 0.290993 * g + 0.439216 * b);
                v[k] = (int)Math.Round(128 + 0.439216 * r - 0.367788 * g - 0.071427 * b);
            }
        }
        for (int j = 0; j < dh; j++) y.AsSpan(j * dw, dw).CopyTo(GuestMemory.Span(dBuf + (uint)(j * dStride), dw));
        uint uv = dBuf + (uint)(dStride * dh);
        for (int j = 0; j < dh / 2; j++)
        {
            var row = GuestMemory.Span(uv + (uint)(j * dStride), dw / 2 * 2);
            for (int i = 0; i < dw / 2; i++)
            {
                int a = 2 * j * dw + 2 * i, c = a + dw;
                int uu = (u[a] + u[a + 1] + u[c] + u[c + 1] + 2) / 4, vv = (v[a] + v[a + 1] + v[c] + v[c + 1] + 2) / 4;
                row[2 * i] = (byte)Math.Clamp(vv, 16, 240);
                row[2 * i + 1] = (byte)Math.Clamp(uu, 16, 240);
            }
        }
    }

    static void Accumulate(ReadOnlySpan<byte> src, int stride, int x, int y, double w, ref double r, ref double g, ref double b)
    {
        int o = (y * stride + x) * 4;
        r += src[o] * w;
        g += src[o + 1] * w;
        b += src[o + 2] * w;
    }

    /// <summary>Not in mp4rec.h: arguments unknown (r0–r3 logged), returns 0.</summary>
    [Hle]
    int sceMp4RecRecorderInit(uint r0, uint r1, uint r2, uint r3)
    {
        Log.WarnOnce("mp4rec-recinit", $"sceMp4RecRecorderInit(0x{r0:X8}, 0x{r1:X8}, 0x{r2:X8}, 0x{r3:X8}): prototype unknown, returning 0");
        return 0;
    }

    [Hle]
    int sceMp4RecRecorderEnd(uint r0)
    {
        Log.WarnOnce("mp4rec-recend", $"sceMp4RecRecorderEnd(0x{r0:X8}): prototype unknown, returning 0");
        return 0;
    }
}
