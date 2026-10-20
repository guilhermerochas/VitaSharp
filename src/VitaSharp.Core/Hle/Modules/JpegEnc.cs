using VitaSharp.Core.Media;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceJpegEncUser (hardware JPEG encoder) and SceJpegEncArm (software encoder, YCbCr input only). The encoder state
/// lives host-side keyed by the guest context address; the guest context memory is never touched.
/// Input YCbCr layout (as Vita3K): planar Y (inWidth x inHeight), then Cb and Cr of ceil(inWidth / 2) x
/// ceil(inHeight / vdiv) each, contiguous and unpadded. SetValidRegion encodes the top-left part of that image.
/// </summary>
public sealed class JpegEnc : HleModule
{
    internal const int ERR_IMAGE_SIZE = unchecked((int)0x80650200), ERR_INSUFFICIENT_BUFFER = unchecked((int)0x80650201),
        ERR_INVALID_COMPRATIO = unchecked((int)0x80650202), ERR_INVALID_PIXELFORMAT = unchecked((int)0x80650203),
        ERR_INVALID_HEADER_MODE = unchecked((int)0x80650204), ERR_INVALID_POINTER = unchecked((int)0x80650205);
    internal const int ARM_ERR_IMAGE_SIZE = unchecked((int)0x80650300), ARM_ERR_INSUFFICIENT_BUFFER = unchecked((int)0x80650301),
        ARM_ERR_INVALID_COMP_RATIO = unchecked((int)0x80650302), ARM_ERR_INVALID_PIXELFORMAT = unchecked((int)0x80650303),
        ARM_ERR_INVALID_HEADER_MODE = unchecked((int)0x80650304), ARM_ERR_INVALID_POINTER = unchecked((int)0x80650305);

    internal const int PIXEL_RGBA8888 = 0, PIXEL_BGRA8888 = 4, PIXEL_YCBCR420 = 8, PIXEL_YCBCR422 = 9, PIXEL_CSC_ARGB_YCBCR = 16;
    internal const int HEADER_JPEG = 0, HEADER_MJPEG = 1;
    internal const int DefaultRatio = 64;
    /// <summary>Context size reported to the game. The state is host-side, so any value works; Vita3K reports the
    /// size of its own context struct (0x20), kept here until the hardware value is known.</summary>
    internal const int ContextSize = 0x20;

    sealed class Ctx
    {
        public int Width, Height, PixelFormat, Ratio = DefaultRatio, HeaderMode = HEADER_JPEG, ValidWidth, ValidHeight, Option;
        public uint OutBuffer, OutSize;
    }

    readonly object _lock = new();
    readonly Dictionary<uint, Ctx> _ctx = new(), _armCtx = new();

    readonly record struct Errors(int ImageSize, int Insufficient, int Ratio, int PixelFormat, int HeaderMode, int Pointer);
    static readonly Errors UserErr = new(ERR_IMAGE_SIZE, ERR_INSUFFICIENT_BUFFER, ERR_INVALID_COMPRATIO, ERR_INVALID_PIXELFORMAT, ERR_INVALID_HEADER_MODE, ERR_INVALID_POINTER);
    static readonly Errors ArmErr = new(ARM_ERR_IMAGE_SIZE, ARM_ERR_INSUFFICIENT_BUFFER, ARM_ERR_INVALID_COMP_RATIO, ARM_ERR_INVALID_PIXELFORMAT, ARM_ERR_INVALID_HEADER_MODE, ARM_ERR_INVALID_POINTER);

    // ------------------------------------------------------------------ SceJpegEncUser

    [Hle] int sceJpegEncoderGetContextSize() => ContextSize;

    [Hle]
    int sceJpegEncoderInit(uint context, int inWidth, int inHeight, int pixelFormat, uint outBuffer, uint outSize)
        => Init(_ctx, UserErr, false, context, inWidth, inHeight, pixelFormat, outBuffer, outSize, 0);

    /// <summary>SceJpegEncoderInitParam (0x1C): +0 size, +4 inWidth, +8 inHeight, +0xC pixelFormat, +0x10 outBuffer,
    /// +0x14 outSize, +0x18 option (1 = buffers in LPDDR2 instead of CDRAM: no effect here).</summary>
    [Hle]
    int sceJpegEncoderInitWithParam(uint context, uint param)
    {
        if (param == 0) return ERR_INVALID_POINTER;
        return Init(_ctx, UserErr, false, context, (int)GuestMemory.R32(param + 4), (int)GuestMemory.R32(param + 8),
            (int)GuestMemory.R32(param + 0xC), GuestMemory.R32(param + 0x10), GuestMemory.R32(param + 0x14), (int)GuestMemory.R32(param + 0x18));
    }

    [Hle] int sceJpegEncoderEnd(uint context) => End(_ctx, context);
    [Hle] int sceJpegEncoderEncode(uint context, uint inBuffer) => Encode(_ctx, UserErr, context, inBuffer);

    [Hle]
    int sceJpegEncoderSetCompressionRatio(uint context, int ratio)
        => ratio is < 0 or > 255 ? ERR_INVALID_COMPRATIO : With(_ctx, UserErr, context, c => c.Ratio = ratio);

    [Hle] int sceJpegEncoderSetOutputAddr(uint context, uint outBuffer, uint outSize) => SetOutput(_ctx, UserErr, context, outBuffer, outSize);
    [Hle] int sceJpegEncoderSetValidRegion(uint context, int width, int height) => SetRegion(_ctx, UserErr, context, width, height);
    [Hle] int sceJpegEncoderSetHeaderMode(uint context, int mode) => SetHeader(_ctx, UserErr, context, mode);

    /// <summary>RGBA8888 (0, bytes R,G,B,A - the header's "ARGB8888") or BGRA8888 (4) with a pitch in pixels, to the
    /// context's YCbCr layout (JFIF full range, chroma averaged). The whole Init size is converted.</summary>
    [Hle]
    int sceJpegEncoderCsc(uint context, uint outBuffer, uint inBuffer, int inPitch, int inPixelFormat)
    {
        Ctx? c;
        lock (_lock) _ctx.TryGetValue(context, out c);
        if (c == null) return ERR_INVALID_POINTER;
        if (!Subsampling(c.PixelFormat, out int vdiv)) return ERR_INVALID_PIXELFORMAT;
        if (inPixelFormat != PIXEL_RGBA8888 && inPixelFormat != PIXEL_BGRA8888) return ERR_INVALID_PIXELFORMAT;
        if (outBuffer == 0 || inBuffer == 0) return ERR_INVALID_POINTER;
        if (inPitch < c.Width) inPitch = c.Width;
        int w = c.Width, h = c.Height, cw = (w + 1) / 2, ch = (h + vdiv - 1) / vdiv;
        var src = GuestMemory.Span(inBuffer, ((h - 1) * inPitch + w) * 4);
        var dst = GuestMemory.Span(outBuffer, w * h + 2 * cw * ch);
        YuvConvert.FromRgba(src, inPitch, w, h, inPixelFormat == PIXEL_BGRA8888, 2, vdiv,
            dst[..(w * h)], w, dst.Slice(w * h, cw * ch), dst.Slice(w * h + cw * ch, cw * ch), cw);
        return 0;
    }

    // ------------------------------------------------------------------ SceJpegEncArm

    [Hle] uint sceJpegArmEncoderGetContextSize() => ContextSize;

    [Hle]
    int sceJpegArmEncoderInit(uint context, ushort inWidth, ushort inHeight, int pixelFormat, uint outBuffer, uint outSize)
        => Init(_armCtx, ArmErr, true, context, inWidth, inHeight, pixelFormat, outBuffer, outSize, 0);

    [Hle] int sceJpegArmEncoderEnd(uint context) => End(_armCtx, context);
    [Hle] int sceJpegArmEncoderEncode(uint context, uint inBuffer) => Encode(_armCtx, ArmErr, context, inBuffer);

    /// <summary>SceUInt8 ratio 1..255 (SCE_JPEGENCARM_MIN/MAX_COMP_RATIO).</summary>
    [Hle]
    int sceJpegArmEncoderSetCompressionRatio(uint context, byte ratio)
        => ratio == 0 ? ARM_ERR_INVALID_COMP_RATIO : With(_armCtx, ArmErr, context, c => c.Ratio = ratio);

    [Hle] int sceJpegArmEncoderSetOutputAddr(uint context, uint outBuffer, uint outSize) => SetOutput(_armCtx, ArmErr, context, outBuffer, outSize);
    [Hle] int sceJpegArmEncoderSetValidRegion(uint context, ushort width, ushort height) => SetRegion(_armCtx, ArmErr, context, width, height);
    [Hle] int sceJpegArmEncoderSetHeaderMode(uint context, int mode) => SetHeader(_armCtx, ArmErr, context, mode);

    // ------------------------------------------------------------------ shared

    /// <summary>Output subsampling of a pixel format (low nibble 8 = 4:2:0, 9 = 4:2:2; the CSC flag is ignored).</summary>
    static bool Subsampling(int pixelFormat, out int vdiv)
    {
        vdiv = (pixelFormat & 0xF) == PIXEL_YCBCR420 ? 2 : 1;
        return (pixelFormat & 0xF) is PIXEL_YCBCR420 or PIXEL_YCBCR422;
    }

    int Init(Dictionary<uint, Ctx> map, Errors err, bool arm, uint context, int w, int h, int pixelFormat, uint outBuffer, uint outSize, int option)
    {
        if (context == 0 || outBuffer == 0) return err.Pointer;
        bool fmtOk = arm
            ? pixelFormat is PIXEL_YCBCR420 or PIXEL_YCBCR422
            : (pixelFormat & ~0x1F) == 0 && (pixelFormat & 0xF) is PIXEL_RGBA8888 or PIXEL_BGRA8888 or PIXEL_YCBCR420 or PIXEL_YCBCR422;
        if (!fmtOk) return err.PixelFormat;
        if (w is < 1 or > 0xFFFF || h is < 1 or > 0xFFFF) return err.ImageSize;
        var c = new Ctx
        {
            Width = w, Height = h, PixelFormat = pixelFormat, OutBuffer = outBuffer, OutSize = outSize,
            ValidWidth = w, ValidHeight = h, Option = option,
        };
        lock (_lock) map[context] = c;
        Log.Debug($"sceJpeg{(arm ? "Arm" : "")}EncoderInit(ctx=0x{context:X8}, {w}x{h}, fmt={pixelFormat}, out=0x{outBuffer:X8}/{outSize})");
        return 0;
    }

    int End(Dictionary<uint, Ctx> map, uint context)
    {
        lock (_lock) map.Remove(context);
        return 0;
    }

    int With(Dictionary<uint, Ctx> map, Errors err, uint context, Action<Ctx> f)
    {
        lock (_lock)
        {
            if (!map.TryGetValue(context, out var c)) return err.Pointer;
            f(c);
        }
        return 0;
    }

    int SetOutput(Dictionary<uint, Ctx> map, Errors err, uint context, uint outBuffer, uint outSize)
        => outBuffer == 0 ? err.Pointer : With(map, err, context, c => { c.OutBuffer = outBuffer; c.OutSize = outSize; });

    int SetHeader(Dictionary<uint, Ctx> map, Errors err, uint context, int mode)
        => mode is not (HEADER_JPEG or HEADER_MJPEG) ? err.HeaderMode : With(map, err, context, c => c.HeaderMode = mode);

    int SetRegion(Dictionary<uint, Ctx> map, Errors err, uint context, int w, int h)
    {
        lock (_lock)
        {
            if (!map.TryGetValue(context, out var c)) return err.Pointer;
            if (w < 1 || h < 1 || w > c.Width || h > c.Height) return err.ImageSize;
            c.ValidWidth = w;
            c.ValidHeight = h;
        }
        return 0;
    }

    /// <summary>Encodes the context's YCbCr image at <paramref name="inBuffer"/>; returns the JPEG size.</summary>
    int Encode(Dictionary<uint, Ctx> map, Errors err, uint context, uint inBuffer)
    {
        Ctx c;
        lock (_lock)
        {
            if (!map.TryGetValue(context, out var cc)) return err.Pointer;
            c = new Ctx
            {
                Width = cc.Width, Height = cc.Height, PixelFormat = cc.PixelFormat, Ratio = cc.Ratio, HeaderMode = cc.HeaderMode,
                ValidWidth = cc.ValidWidth, ValidHeight = cc.ValidHeight, OutBuffer = cc.OutBuffer, OutSize = cc.OutSize,
            };
        }
        if (inBuffer == 0) return err.Pointer;
        if (!Subsampling(c.PixelFormat, out int vdiv)) return err.PixelFormat;
        int w = c.Width, h = c.Height, cw = (w + 1) / 2, ch = (h + vdiv - 1) / vdiv;
        var src = GuestMemory.Span(inBuffer, w * h + 2 * cw * ch);
        var (lq, cq) = JpegEncoder.TablesForRatio(c.Ratio);
        byte[] jpeg = JpegEncoder.Encode(src[..(w * h)], w, src.Slice(w * h, cw * ch), src.Slice(w * h + cw * ch, cw * ch), cw,
            c.ValidWidth, c.ValidHeight, 2, vdiv, lq, cq, writeHuffmanTables: c.HeaderMode == HEADER_JPEG);
        if ((uint)jpeg.Length > c.OutSize)
        {
            Log.Debug($"sceJpegEncoderEncode: {jpeg.Length} bytes do not fit the {c.OutSize}-byte output buffer");
            return err.Insufficient;
        }
        jpeg.CopyTo(GuestMemory.Span(c.OutBuffer, jpeg.Length));
        return jpeg.Length;
    }
}
