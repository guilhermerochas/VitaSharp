using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceJpegUser (hardware MJPEG decoder) and SceJpegArm (software decoder, same semantics). Decoding is done by the
/// managed <see cref="JpegDecoder"/>; output is planar YCbCr in the Vita pitch layout (Y, Cb, Cr contiguous, each plane
/// pitch[i].x * pitch[i].y bytes) or RGBA/BGRA8888 with a line width of pitch[0].x pixels.
/// decodeMode: bits 0-2 DHT mode (0 WITH_DHT, 1 WITHOUT_DHT = "standard": 4:2:0/4:2:2 only, pitch height aligned to 8,
/// chroma pitch aligned to 16; 2/3 ANY_SAMPLING = "extended"), bits 4-6 downscale 1/2, 1/4, 1/8.
/// </summary>
public sealed class Jpeg : HleModule
{
    internal const int ERR_IMAGE_EMPTY = unchecked((int)0x80650003), ERR_BAD_MARKER_LENGTH = unchecked((int)0x80650004),
        ERR_BAD_DHT_COUNTS = unchecked((int)0x80650005), ERR_BAD_DHT_INDEX = unchecked((int)0x80650006),
        ERR_BAD_DQT_INDEX = unchecked((int)0x80650007), ERR_DECODE_ERROR = unchecked((int)0x80650009),
        ERR_INVALID_POINTER = unchecked((int)0x80650010), ERR_BAD_COMPONENT_ID = unchecked((int)0x80650011),
        ERR_UNSUPPORT_COLORSPACE = unchecked((int)0x80650013), ERR_BAD_MCU_SIZE = unchecked((int)0x80650014),
        ERR_BAD_PRECISION = unchecked((int)0x80650015), ERR_UNSUPPORT_SAMPLING = unchecked((int)0x80650016),
        ERR_COMPONENT_COUNT = unchecked((int)0x80650017), ERR_UNSUPPORT_IMAGE_SIZE = unchecked((int)0x80650020),
        ERR_NO_HUFF_TABLE = unchecked((int)0x80650021), ERR_NO_QUANT_TABLE = unchecked((int)0x80650022),
        ERR_NO_SOI = unchecked((int)0x80650023), ERR_BAD_DQT_MARKER = unchecked((int)0x80650024),
        ERR_BAD_DHT_MARKER = unchecked((int)0x80650025), ERR_BAD_DRI_MARKER = unchecked((int)0x80650026),
        ERR_BAD_SOF_MARKER = unchecked((int)0x80650027), ERR_BAD_SOS_MARKER = unchecked((int)0x80650028),
        ERR_SOF_DUPLICATE = unchecked((int)0x80650029), ERR_NO_LOSSLESS_SUPPORT = unchecked((int)0x80650031),
        ERR_NO_ARITH_SUPPORT = unchecked((int)0x80650032), ERR_UNKNOWN_MARKER = unchecked((int)0x80650035),
        ERR_BAD_RESTART_MARKER = unchecked((int)0x80650036), ERR_UNEXPECTED_MARKER = unchecked((int)0x80650037),
        ERR_INVALID_STATE = unchecked((int)0x80650039), ERR_INVALID_COLOR_FORMAT = unchecked((int)0x80650050),
        ERR_UNSUPPORT_DOWNSCALE = unchecked((int)0x80650060), ERR_BAD_PROGRESSIVE_PARAM = unchecked((int)0x80650061),
        ERR_EXCEED_MAX_SPLIT_DECODER = unchecked((int)0x80650062);

    internal const int COLORSPACE_GRAYSCALE = 0x10101, COLORSPACE_YUV = 0x20000, COLORSPACE_YUV444 = 0x20101,
        COLORSPACE_YUV422 = 0x20201, COLORSPACE_YUV420 = 0x20202;
    internal const int FORMAT_NO_CSC = -1, FORMAT_RGBA8888 = 0, FORMAT_BGRA8888 = 4, COLOR_BT601 = 0x10;
    /// <summary>sizeof(SceJpegOutputInfo).</summary>
    internal const uint OutputInfoSize = 0x34;

    readonly object _lock = new();
    bool _initialized;
    int _maxSplitDecoders;

    public bool Initialized { get { lock (_lock) return _initialized; } }
    /// <summary>decoderCount of the last init (maximum simultaneous split decoders).</summary>
    public int MaxSplitDecoders { get { lock (_lock) return _maxSplitDecoders; } }

    // ------------------------------------------------------------------ library state

    /// <summary>Vita3K accepts a second init (no INIT_DONE check) and so do we: decoding is stateless here.</summary>
    [Hle]
    int sceJpegInitMJpeg(int decoderCount)
    {
        lock (_lock) { _initialized = true; _maxSplitDecoders = Math.Max(0, decoderCount); }
        return 0;
    }

    /// <summary>SceJpegMJpegInitParam (0xC): +0 size, +4 decoderCount, +8 options.</summary>
    [Hle]
    int sceJpegInitMJpegWithParam(uint param)
    {
        if (param == 0) return ERR_INVALID_POINTER;
        return sceJpegInitMJpeg((int)GuestMemory.R32(param + 4));
    }

    [Hle]
    int sceJpegFinishMJpeg()
    {
        lock (_lock) { _initialized = false; _maxSplitDecoders = 0; }
        return 0;
    }

    // ------------------------------------------------------------------ decoding

    [Hle] int sceJpegGetOutputInfo(uint jpeg, uint size, int format, int mode, uint output) => GetOutputInfo(jpeg, size, format, mode, output);
    [Hle] int sceJpegArmGetOutputInfo(uint jpeg, uint size, int mode, int format, uint output) => GetOutputInfo(jpeg, size, format, mode, output);

    /// <summary>Vita3K argument order (9 arguments, 5..9 on the stack): the decoded YCbCr also lands in pTempBuffer.</summary>
    [Hle]
    int sceJpegDecodeMJpeg(uint jpeg, uint isize, uint rgba, uint osize, int mode, uint temp, uint tempSize, uint coef, uint coefSize)
        => Decode(jpeg, isize, mode, rgba, osize, FORMAT_RGBA8888, temp, tempSize, limitSize: true);

    /// <summary>Vita3K argument order (pJpeg, isize, pYCbCr, osize, decodeMode, pCoef, coefSize); the vitasdk header
    /// lists mode third, which does not match what commercial games pass.</summary>
    [Hle]
    int sceJpegDecodeMJpegYCbCr(uint jpeg, uint isize, uint yuv, uint osize, int mode, uint coef, uint coefSize)
        => Decode(jpeg, isize, mode, yuv, osize, FORMAT_NO_CSC, 0, 0, limitSize: false);

    [Hle]
    int sceJpegArmDecodeMJpeg(uint jpeg, uint isize, int mode, uint rgba, uint osize, uint coef, uint coefSize)
        => Decode(jpeg, isize, mode, rgba, osize, FORMAT_RGBA8888, 0, 0, limitSize: false);

    [Hle]
    int sceJpegArmDecodeMJpegYCbCr(uint jpeg, uint isize, int mode, uint yuv, uint osize, uint coef, uint coefSize)
        => Decode(jpeg, isize, mode, yuv, osize, FORMAT_NO_CSC, 0, 0, limitSize: false);

    /// <summary>Generic YCbCr to RGBA8888 (unaligned pitch: planes width x height). Only 4:4:4 (sampling 0x101) and
    /// grayscale (0x10101); colorOption = RGBA8888 (0) | BT601 flag (0x10).</summary>
    [Hle]
    int sceJpegCsc(uint rgba, uint yuv, uint xysize, int frameWidth, int colorOption, int sampling)
    {
        bool bt601 = (colorOption & COLOR_BT601) != 0;
        if ((colorOption & ~COLOR_BT601) != FORMAT_RGBA8888) return ERR_INVALID_COLOR_FORMAT;
        int cs = sampling == COLORSPACE_GRAYSCALE ? COLORSPACE_GRAYSCALE : COLORSPACE_YUV | sampling;
        if (cs != COLORSPACE_GRAYSCALE && cs != COLORSPACE_YUV444) return ERR_INVALID_COLOR_FORMAT;
        if (rgba == 0 || yuv == 0) return ERR_INVALID_POINTER;
        int w = (int)(xysize >> 16), h = (int)(xysize & 0xFFFF);
        var pitch = PitchInfo(w, h, 0, cs, standard: false);
        Csc(yuv, rgba, frameWidth, cs, pitch, bgra: false, bt601);
        return 0;
    }

    /// <summary>Converts DecodeMJpegYCbCr output (standard layout: luma pitch = width, chroma pitch aligned to 16) to
    /// RGBA or BGRA. xysize = (width &lt;&lt; 16) | height.</summary>
    [Hle]
    int sceJpegMJpegCsc(uint rgba, uint yuv, uint xysize, int frameWidth, int colorOption, int sampling)
    {
        bool bt601 = (colorOption & COLOR_BT601) != 0;
        int fmt = colorOption & ~COLOR_BT601;
        if (fmt != FORMAT_RGBA8888 && fmt != FORMAT_BGRA8888) return ERR_INVALID_COLOR_FORMAT;
        int w = (int)(xysize >> 16), h = (int)(xysize & 0xFFFF);
        if (IsUnsupportedImageSize(w, h)) return ERR_UNSUPPORT_IMAGE_SIZE;
        int cs = COLORSPACE_YUV | sampling;
        if (!Divisors(cs, out _, out _)) return ERR_UNSUPPORT_SAMPLING;
        if (rgba == 0 || yuv == 0) return ERR_INVALID_POINTER;
        var pitch = PitchInfo(w, h, 0, cs, standard: true);
        Csc(yuv, rgba, frameWidth, cs, pitch, fmt == FORMAT_BGRA8888, bt601);
        return 0;
    }

    // ------------------------------------------------------------------ split decoders

    // The split (incremental) decoder API has no public prototype (not in vitasdk, unimplemented in Vita3K). Until a game
    // shows its control structure, creation fails with EXCEED_MAX_SPLIT_DECODER so callers fall back to whole-image decoding.
    [Hle] int sceJpegCreateSplitDecoder(uint a0, uint a1, uint a2, uint a3) => SplitCreate("sceJpegCreateSplitDecoder", a0, a1, a2, a3);
    [Hle] int sceJpegArmCreateSplitDecoder(uint a0, uint a1, uint a2, uint a3) => SplitCreate("sceJpegArmCreateSplitDecoder", a0, a1, a2, a3);
    [Hle] int sceJpegSplitDecodeMJpeg(uint ctrl) => SplitDecode("sceJpegSplitDecodeMJpeg", ctrl);
    [Hle] int sceJpegArmSplitDecodeMJpeg(uint ctrl) => SplitDecode("sceJpegArmSplitDecodeMJpeg", ctrl);
    [Hle] int sceJpegDeleteSplitDecoder(uint ctrl) => 0;
    [Hle] int sceJpegArmDeleteSplitDecoder(uint ctrl) => 0;

    static int SplitCreate(string name, uint a0, uint a1, uint a2, uint a3)
    {
        string dump = "";
        if (a0 != 0)
        {
            var b = GuestMemory.Span(a0, 0x40);
            dump = " ctrl[0..0x40]=" + Convert.ToHexString(b);
        }
        Log.WarnOnce(name, $"{name}(0x{a0:X8}, 0x{a1:X8}, 0x{a2:X8}, 0x{a3:X8}): split decoder layout unknown, refused{dump}");
        return ERR_EXCEED_MAX_SPLIT_DECODER;
    }

    static int SplitDecode(string name, uint ctrl)
    {
        Log.WarnOnce(name, $"{name}(0x{ctrl:X8}): no split decoder exists");
        return ERR_INVALID_STATE;
    }

    // ------------------------------------------------------------------ helpers

    internal readonly record struct Pitch(int X, int Y);

    static int DhtMode(int mode) => mode & 7;
    static bool IsStandard(int mode) => DhtMode(mode) is 0 or 1;

    /// <summary>Downscale ratio from decodeMode bits 4-6 (0x10 = 1/2, 0x20 = 1/4, 0x40 = 1/8). Several bits ("ANY")
    /// pick the smallest downscale, so buffers sized by GetOutputInfo always fit.</summary>
    internal static int DownscaleRatio(int mode)
    {
        int b = mode & 0x70;
        if ((b & 0x10) != 0) return 2;
        if ((b & 0x20) != 0) return 4;
        if ((b & 0x40) != 0) return 8;
        return 1;
    }

    /// <summary>The hardware limit applies to the output pitch (after downscaling), not the image size.</summary>
    static bool IsUnsupportedImageSize(int w, int h) => w < 64 || h < 64 || w > 2032 || h > 1088;

    /// <summary>Chroma divisors of a colorspace code; false if unknown.</summary>
    static bool Divisors(int cs, out int hdiv, out int vdiv)
    {
        hdiv = vdiv = 1;
        if (cs == COLORSPACE_GRAYSCALE) return true;
        if ((cs & ~0xFFFF) != COLORSPACE_YUV) return false;
        hdiv = (cs >> 8) & 0xFF;
        vdiv = cs & 0xFF;
        return (hdiv, vdiv) is (1, 1) or (1, 2) or (1, 4) or (2, 1) or (2, 2) or (4, 1);
    }

    static int Align(int v, int a) => (v + a - 1) / a * a;

    /// <summary>Output pitches (Vita3K calculate_pitch_info): the image is aligned to whole MCUs, divided by the
    /// downscale ratio and realigned (width to the MCU width, height to 8 in standard mode). ratio 0 = no MCU
    /// alignment (the Csc entry points). Chroma = luma / divisor, its width aligned to 16 for standard 4:2:0/4:2:2.</summary>
    internal static Pitch[] PitchInfo(int width, int height, int ratio, int cs, bool standard)
    {
        Divisors(cs, out int hdiv, out int vdiv);
        bool gray = cs == COLORSPACE_GRAYSCALE;
        int bw = 8 * hdiv, bh = 8 * vdiv;
        var p = new Pitch[4];
        var basic = new Pitch(width, height);
        if (ratio != 0)
            basic = new Pitch(Align(Align(width, bw) / ratio, bw), Align(Align(height, bh) / ratio, standard ? 8 : 1));
        p[0] = basic;
        if (!gray) p[1] = p[2] = new Pitch(basic.X / hdiv, basic.Y / vdiv);
        if (standard && (cs == COLORSPACE_YUV420 || cs == COLORSPACE_YUV422)) p[1] = p[2] = p[1] with { X = Align(p[1].X, 16) };
        return p;
    }

    static int TotalSize(Pitch[] p) => p.Sum(x => x.X * x.Y);

    internal static int MapError(JpegError e) => e switch
    {
        JpegError.ImageEmpty => ERR_IMAGE_EMPTY,
        JpegError.BadMarkerLength => ERR_BAD_MARKER_LENGTH,
        JpegError.BadDhtCounts => ERR_BAD_DHT_COUNTS,
        JpegError.BadDhtIndex => ERR_BAD_DHT_INDEX,
        JpegError.BadDqtIndex => ERR_BAD_DQT_INDEX,
        JpegError.BadComponentId => ERR_BAD_COMPONENT_ID,
        JpegError.BadMcuSize => ERR_BAD_MCU_SIZE,
        JpegError.BadPrecision => ERR_BAD_PRECISION,
        JpegError.UnsupportedSampling => ERR_UNSUPPORT_SAMPLING,
        JpegError.ComponentCount => ERR_COMPONENT_COUNT,
        JpegError.NoHuffTable => ERR_NO_HUFF_TABLE,
        JpegError.NoQuantTable => ERR_NO_QUANT_TABLE,
        JpegError.NoSoi => ERR_NO_SOI,
        JpegError.BadDqtMarker => ERR_BAD_DQT_MARKER,
        JpegError.BadDhtMarker => ERR_BAD_DHT_MARKER,
        JpegError.BadDriMarker => ERR_BAD_DRI_MARKER,
        JpegError.BadSofMarker => ERR_BAD_SOF_MARKER,
        JpegError.BadSosMarker => ERR_BAD_SOS_MARKER,
        JpegError.SofDuplicate => ERR_SOF_DUPLICATE,
        JpegError.NoLosslessSupport => ERR_NO_LOSSLESS_SUPPORT,
        JpegError.NoArithSupport => ERR_NO_ARITH_SUPPORT,
        JpegError.UnknownMarker => ERR_UNKNOWN_MARKER,
        JpegError.BadRestartMarker => ERR_BAD_RESTART_MARKER,
        JpegError.UnexpectedMarker => ERR_UNEXPECTED_MARKER,
        JpegError.BadProgressiveParam => ERR_BAD_PROGRESSIVE_PARAM,
        _ => ERR_DECODE_ERROR,
    };

    /// <summary>SceJpegOutputInfo (0x34): +0 colorSpace, +4 u16 width, +6 u16 height, +8 outputSize, +0xC tempBufferSize
    /// (YCbCr staging when converting to RGBA), +0x10 coefBufferSize, +0x14 SceJpegPitch[4] {x, y}.</summary>
    int GetOutputInfo(uint jpeg, uint size, int format, int mode, uint output)
    {
        if (jpeg == 0 || output == 0 || size == 0) return ERR_INVALID_POINTER;
        if (format != FORMAT_NO_CSC && format != FORMAT_RGBA8888 && format != FORMAT_BGRA8888) return ERR_INVALID_COLOR_FORMAT;
        JpegInfo info;
        try { info = JpegDecoder.ReadInfo(GuestMemory.Span(jpeg, (int)size)); }
        catch (JpegFormatException e)
        {
            Log.Debug($"sceJpegGetOutputInfo: {e.Message}");
            return MapError(e.Error);
        }
        GuestMemory.Fill(output, 0, OutputInfoSize);
        int cs = info.ColorSpace;
        GuestMemory.W32(output, (uint)cs);
        bool standard = IsStandard(mode);
        if (standard && cs == COLORSPACE_GRAYSCALE) return ERR_UNSUPPORT_COLORSPACE;
        if (standard && cs != COLORSPACE_YUV420 && cs != COLORSPACE_YUV422) return ERR_UNSUPPORT_SAMPLING;
        if (!standard && format != FORMAT_NO_CSC) return ERR_UNSUPPORT_DOWNSCALE;

        var pitch = PitchInfo(info.Width, info.Height, DownscaleRatio(mode), cs, standard);
        int yuvTotal = TotalSize(pitch);
        uint outputSize, tempSize = 0;
        if (format != FORMAT_NO_CSC)
        {
            pitch[0] = pitch[0] with { X = pitch[0].X * 4 };
            pitch[1] = pitch[2] = pitch[3] = default;
            tempSize = GuestMemory.AlignUp((uint)yuvTotal, 256);
            outputSize = GuestMemory.AlignUp((uint)(pitch[0].X * pitch[0].Y), 256);
        }
        else outputSize = GuestMemory.AlignUp((uint)yuvTotal, 256);
        // baseline needs no coefficient buffer; Vita3K reports 0x100 because games mishandle 0. Progressive images
        // keep every coefficient (16-bit) of the frame.
        uint coefSize = 0x100;
        if (info.Progressive)
        {
            long blocks = 0;
            int mx = (info.Width + info.McuWidth - 1) / info.McuWidth, my = (info.Height + info.McuHeight - 1) / info.McuHeight;
            blocks += (long)mx * my * info.HDiv * info.VDiv;
            if (!info.Gray) blocks += 2L * mx * my;
            coefSize = GuestMemory.AlignUp((uint)(blocks * 128), 256);
        }
        GuestMemory.Write(output + 4, (ushort)info.Width);
        GuestMemory.Write(output + 6, (ushort)info.Height);
        GuestMemory.W32(output + 8, outputSize);
        GuestMemory.W32(output + 0xC, tempSize);
        GuestMemory.W32(output + 0x10, coefSize);
        for (int i = 0; i < 4; i++)
        {
            GuestMemory.W32(output + 0x14 + (uint)i * 8, (uint)pitch[i].X);
            GuestMemory.W32(output + 0x18 + (uint)i * 8, (uint)pitch[i].Y);
        }
        return 0;
    }

    /// <summary>Decodes into the pitch layout; returns (pitch[0].x &lt;&lt; 16) | pitch[0].y. Writes are clamped to the
    /// buffer sizes the caller gave.</summary>
    int Decode(uint jpeg, uint isize, int mode, uint dst, uint osize, int format, uint temp, uint tempSize, bool limitSize)
    {
        if (jpeg == 0 || isize == 0 || dst == 0) return ERR_INVALID_POINTER;
        JpegImage img;
        try { img = JpegDecoder.Decode(GuestMemory.Span(jpeg, (int)isize)); }
        catch (JpegFormatException e)
        {
            Log.Debug($"sceJpegDecode: {e.Message}");
            return MapError(e.Error);
        }
        bool standard = IsStandard(mode);
        int cs = img.Info.ColorSpace;
        var pitch = PitchInfo(img.Info.Width, img.Info.Height, DownscaleRatio(mode), cs, standard);
        if (limitSize && IsUnsupportedImageSize(pitch[0].X, pitch[0].Y)) return ERR_UNSUPPORT_IMAGE_SIZE;
        var yuv = BuildPitched(img, pitch, DownscaleRatio(mode), standard);

        if (format == FORMAT_NO_CSC)
        {
            WriteClamped(dst, osize, yuv, "YCbCr");
        }
        else
        {
            if (temp != 0) WriteClamped(temp, tempSize, yuv, "temp");
            var rgba = new byte[pitch[0].X * pitch[0].Y * 4];
            CscCore(yuv, rgba, pitch[0].X, cs, pitch, format == FORMAT_BGRA8888, false);
            WriteClamped(dst, osize, rgba, "RGBA");
        }
        return (pitch[0].X << 16) | pitch[0].Y;
    }

    static void WriteClamped(uint dst, uint size, byte[] data, string what)
    {
        int n = data.Length;
        if (size < (uint)n)
        {
            Log.WarnOnce("jpeg-short-" + what, $"sceJpeg decode: {what} buffer of {size} bytes, {n} needed; output truncated");
            n = (int)size;
        }
        data.AsSpan(0, n).CopyTo(GuestMemory.Span(dst, n));
    }

    /// <summary>Lays the decoded planes out with the output pitches. Samples past the decoded MCUs replicate the last
    /// column/row. Downscaling averages ratio x ratio boxes of the full-size layout.</summary>
    internal static byte[] BuildPitched(JpegImage img, Pitch[] pitch, int ratio, bool standard)
    {
        int planes = img.Planes.Length;
        Pitch[] full = ratio == 1 ? pitch : PitchInfo(img.Info.Width, img.Info.Height, 1, img.Info.ColorSpace, standard);
        var dst = new byte[TotalSize(pitch)];
        int off = 0;
        for (int p = 0; p < 3; p++)
        {
            var dp = pitch[p];
            if (p >= planes || dp.X == 0 || dp.Y == 0) { off += dp.X * dp.Y; continue; }
            var src = img.Planes[p];
            var fp = full[p];
            // full-size plane in the pitch layout
            byte[] fb = ratio == 1 ? dst : new byte[fp.X * fp.Y];
            int fo = ratio == 1 ? off : 0;
            int cols = Math.Min(fp.X, src.Stride), rows = Math.Min(fp.Y, src.Rows);
            for (int y = 0; y < fp.Y; y++)
            {
                var o = fb.AsSpan(fo + y * fp.X, fp.X);
                src.Data.AsSpan(Math.Min(y, rows - 1) * src.Stride, cols).CopyTo(o);
                if (cols < fp.X) o[cols..].Fill(o[cols - 1]);
            }
            if (ratio != 1)
            {
                for (int y = 0; y < dp.Y; y++)
                    for (int x = 0; x < dp.X; x++)
                    {
                        int sy0 = Math.Min(y * ratio, fp.Y - 1), sx0 = Math.Min(x * ratio, fp.X - 1);
                        int sy1 = Math.Min(sy0 + ratio, fp.Y), sx1 = Math.Min(sx0 + ratio, fp.X);
                        int sum = 0, n = 0;
                        for (int sy = sy0; sy < sy1; sy++)
                            for (int sx = sx0; sx < sx1; sx++) { sum += fb[sy * fp.X + sx]; n++; }
                        dst[off + y * dp.X + x] = (byte)((sum + n / 2) / n);
                    }
            }
            off += dp.X * dp.Y;
        }
        return dst;
    }

    static void Csc(uint yuv, uint rgba, int frameWidth, int cs, Pitch[] pitch, bool bgra, bool bt601)
    {
        int total = TotalSize(pitch);
        if (pitch[0].X <= 0 || pitch[0].Y <= 0 || frameWidth < pitch[0].X) return;
        var src = GuestMemory.Span(yuv, total);
        var dst = GuestMemory.Span(rgba, ((pitch[0].Y - 1) * frameWidth + pitch[0].X) * 4);
        CscCore(src, dst, frameWidth, cs, pitch, bgra, bt601);
    }

    static void CscCore(ReadOnlySpan<byte> src, Span<byte> dst, int frameWidth, int cs, Pitch[] pitch, bool bgra, bool bt601)
    {
        int w = pitch[0].X, h = pitch[0].Y;
        var y = src[..(w * h)];
        if (cs == COLORSPACE_GRAYSCALE)
        {
            YuvConvert.GrayToRgba(y, w, w, h, dst, frameWidth, bt601); // grey: RGBA == BGRA
            return;
        }
        Divisors(cs, out int hdiv, out int vdiv);
        int cSize = pitch[1].X * pitch[1].Y;
        var cb = src.Slice(w * h, cSize);
        var cr = src.Slice(w * h + cSize, pitch[2].X * pitch[2].Y);
        // planes may be shorter than w/hdiv when the pitch was not MCU aligned: clamp the visible chroma
        int cw = pitch[1].X, ch = pitch[1].Y;
        if (cw * hdiv >= w && ch * vdiv >= h)
            YuvConvert.ToRgba(y, w, cb, cr, cw, w, h, hdiv, vdiv, dst, frameWidth, bgra, bt601);
        else
        {
            // odd sizes: convert the part covered by chroma, then the remaining columns/rows by edge replication
            var ecb = Extend(cb, cw, ch, (w + hdiv - 1) / hdiv, (h + vdiv - 1) / vdiv);
            var ecr = Extend(cr, cw, ch, (w + hdiv - 1) / hdiv, (h + vdiv - 1) / vdiv);
            YuvConvert.ToRgba(y, w, ecb, ecr, (w + hdiv - 1) / hdiv, w, h, hdiv, vdiv, dst, frameWidth, bgra, bt601);
        }
    }

    static byte[] Extend(ReadOnlySpan<byte> p, int w, int h, int nw, int nh)
    {
        var o = new byte[nw * nh];
        if (w == 0 || h == 0) { o.AsSpan().Fill(128); return o; }
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++) o[y * nw + x] = p[Math.Min(y, h - 1) * w + Math.Min(x, w - 1)];
        return o;
    }
}
