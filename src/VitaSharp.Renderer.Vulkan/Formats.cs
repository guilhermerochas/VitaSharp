using VitaSharp.Core.Util;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gxm;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>How a GXM texture base format is uploaded: either directly or converted on the CPU to RGBA8.</summary>
/// <summary>AstcFromBc / AstcFromPvrtc: the level is transcoded to ASTC 4x4 blocks (AstcTranscoder).</summary>
public enum TexConvert { None, Rgb8ToRgba8, Srgb8ToRgba8, Palette4, Palette8, U8U3U3U2, Yuv420P2, Yuv420P3, Yuv422, Pvrtc, U2F10F10F10, S5S5U6, X8U24, BcSoftware, AstcFromBc, AstcFromPvrtc }

public readonly record struct TexFormatInfo(Format Format, int BitsPerPixel, int BlockW, int BlockH, int BlockBytes, TexConvert Convert)
{
    public bool Compressed => BlockW > 1;
}

public static class Formats
{
    static ComponentMapping M(ComponentSwizzle r, ComponentSwizzle g, ComponentSwizzle b, ComponentSwizzle a) => new(r, g, b, a);
    const ComponentSwizzle R = ComponentSwizzle.R, G = ComponentSwizzle.G, B = ComponentSwizzle.B, A = ComponentSwizzle.A,
        Z = ComponentSwizzle.Zero, O = ComponentSwizzle.One, I = ComponentSwizzle.Identity;

    static readonly ComponentMapping Identity = M(I, I, I, I);

    static TexFormatInfo Unknown(TexBase b)
    {
        VitaSharp.Core.Util.Log.WarnOnce("texfmt" + b, $"[gpu] texture base format {b} (0x{(uint)b:X8}) is not supported: treated as RGBA8");
        return new(Format.R8G8B8A8Unorm, 32, 1, 1, 0, TexConvert.None);
    }

    public static TexFormatInfo Texture(TexBase b) => b switch
    {
        TexBase.U8 => new(Format.R8Unorm, 8, 1, 1, 0, TexConvert.None),
        TexBase.S8 => new(Format.R8SNorm, 8, 1, 1, 0, TexConvert.None),
        TexBase.U16 => new(Format.R16Unorm, 16, 1, 1, 0, TexConvert.None),
        TexBase.S16 => new(Format.R16SNorm, 16, 1, 1, 0, TexConvert.None),
        TexBase.F16 => new(Format.R16Sfloat, 16, 1, 1, 0, TexConvert.None),
        TexBase.U32 => new(Format.R32Uint, 32, 1, 1, 0, TexConvert.None),
        TexBase.S32 => new(Format.R32Sint, 32, 1, 1, 0, TexConvert.None),
        TexBase.F32 or TexBase.F32M => new(Format.R32Sfloat, 32, 1, 1, 0, TexConvert.None),
        TexBase.X8U24 => new(Format.R32Sfloat, 32, 1, 1, 0, TexConvert.X8U24), // 24-bit depth, read normalised
        TexBase.U8U8 => new(Format.R8G8Unorm, 16, 1, 1, 0, TexConvert.None),
        TexBase.S8S8 => new(Format.R8G8SNorm, 16, 1, 1, 0, TexConvert.None),
        TexBase.U16U16 => new(Format.R16G16Unorm, 32, 1, 1, 0, TexConvert.None),
        TexBase.S16S16 => new(Format.R16G16SNorm, 32, 1, 1, 0, TexConvert.None),
        TexBase.F16F16 => new(Format.R16G16Sfloat, 32, 1, 1, 0, TexConvert.None),
        TexBase.U32U32 => new(Format.R32G32Uint, 64, 1, 1, 0, TexConvert.None),
        TexBase.F32F32 => new(Format.R32G32Sfloat, 64, 1, 1, 0, TexConvert.None),
        TexBase.U8U8U8U8 => new(Format.R8G8B8A8Unorm, 32, 1, 1, 0, TexConvert.None),
        TexBase.S8S8S8S8 => new(Format.R8G8B8A8SNorm, 32, 1, 1, 0, TexConvert.None),
        TexBase.X8S8S8U8 => new(Format.R8G8B8A8SNorm, 32, 1, 1, 0, TexConvert.None),
        TexBase.U16U16U16U16 => new(Format.R16G16B16A16Unorm, 64, 1, 1, 0, TexConvert.None),
        TexBase.S16S16S16S16 => new(Format.R16G16B16A16SNorm, 64, 1, 1, 0, TexConvert.None),
        TexBase.F16F16F16F16 => new(Format.R16G16B16A16Sfloat, 64, 1, 1, 0, TexConvert.None),
        TexBase.U5U6U5 => new(Format.R5G6B5UnormPack16, 16, 1, 1, 0, TexConvert.None),
        TexBase.S5S5U6 => new(Format.R8G8B8A8SNorm, 16, 1, 1, 0, TexConvert.S5S5U6),
        TexBase.F11F11F10 => new(Format.B10G11R11UfloatPack32, 32, 1, 1, 0, TexConvert.None),
        TexBase.SE5M9M9M9 => new(Format.E5B9G9R9UfloatPack32, 32, 1, 1, 0, TexConvert.None),
        TexBase.U4U4U4U4 => new(Format.R4G4B4A4UnormPack16, 16, 1, 1, 0, TexConvert.None),
        TexBase.U1U5U5U5 => new(Format.A1R5G5B5UnormPack16, 16, 1, 1, 0, TexConvert.None),
        TexBase.U2U10U10U10 => new(Format.A2R10G10B10UnormPack32, 32, 1, 1, 0, TexConvert.None),
        TexBase.U2F10F10F10 => new(Format.R16G16B16A16Sfloat, 32, 1, 1, 0, TexConvert.U2F10F10F10),
        TexBase.U8U8U8 => new(Format.R8G8B8A8Unorm, 24, 1, 1, 0, TexConvert.Rgb8ToRgba8),
        TexBase.S8S8S8 => new(Format.R8G8B8A8SNorm, 24, 1, 1, 0, TexConvert.Srgb8ToRgba8),
        TexBase.U8U3U3U2 => new(Format.R8G8B8A8Unorm, 16, 1, 1, 0, TexConvert.U8U3U3U2),
        TexBase.P4 => new(Format.R8G8B8A8Unorm, 4, 1, 1, 0, TexConvert.Palette4),
        TexBase.P8 => new(Format.R8G8B8A8Unorm, 8, 1, 1, 0, TexConvert.Palette8),
        TexBase.UBC1 => new(Format.BC1RgbaUnormBlock, 4, 4, 4, 8, TexConvert.None),
        TexBase.UBC2 => new(Format.BC2UnormBlock, 8, 4, 4, 16, TexConvert.None),
        TexBase.UBC3 => new(Format.BC3UnormBlock, 8, 4, 4, 16, TexConvert.None),
        TexBase.UBC4 => new(Format.BC4UnormBlock, 4, 4, 4, 8, TexConvert.None),
        TexBase.SBC4 => new(Format.BC4SNormBlock, 4, 4, 4, 8, TexConvert.None),
        TexBase.UBC5 => new(Format.BC5UnormBlock, 8, 4, 4, 16, TexConvert.None),
        TexBase.SBC5 => new(Format.BC5SNormBlock, 8, 4, 4, 16, TexConvert.None),
        TexBase.PVRT2BPP or TexBase.PVRTII2BPP => new(Format.R8G8B8A8Unorm, 2, 8, 4, 8, TexConvert.Pvrtc),
        TexBase.PVRT4BPP or TexBase.PVRTII4BPP => new(Format.R8G8B8A8Unorm, 4, 4, 4, 8, TexConvert.Pvrtc),
        TexBase.YUV420P2 => new(Format.R8G8B8A8Unorm, 12, 1, 1, 0, TexConvert.Yuv420P2),
        TexBase.YUV420P3 => new(Format.R8G8B8A8Unorm, 12, 1, 1, 0, TexConvert.Yuv420P3),
        TexBase.YUV422 => new(Format.R8G8B8A8Unorm, 16, 1, 1, 0, TexConvert.Yuv422),
        _ => Unknown(b),
    };

    static readonly bool Swizzle1Legacy = Environment.GetEnvironmentVariable(EnvVars.Swz1) != "B"; // RGBA-order reading opt-in until verified

    public static ComponentMapping TextureSwizzle(uint format)
    {
        var b = GxmFormats.BaseOf(format);
        uint s = GxmFormats.SwizzleOf(format) >> 12;
        switch (b)
        {
            case TexBase.U8: case TexBase.S8: case TexBase.U16: case TexBase.S16: case TexBase.F16:
            case TexBase.U32: case TexBase.S32: case TexBase.F32: case TexBase.F32M: case TexBase.UBC4: case TexBase.SBC4:
                // SCE_GXM_TEXTURE_SWIZZLE1_{R,000R,111R,RRRR,0RRR,1RRR,R000,R111}: names list the result in RGBA order
                // (R111 = (R,1,1,1)); the legacy ABGR-order reading is kept behind VITASHARP_SWZ1=A for comparison
                if (Swizzle1Legacy)
                    return s switch
                    {
                        0 => M(R, Z, Z, O), 1 => M(R, Z, Z, Z), 2 => M(R, O, O, O), 3 => M(R, R, R, R),
                        4 => M(R, R, R, Z), 5 => M(R, R, R, O), 6 => M(Z, Z, Z, R), _ => M(O, O, O, R),
                    };
                return s switch
                {
                    0 => M(R, Z, Z, O), 1 => M(Z, Z, Z, R), 2 => M(O, O, O, R), 3 => M(R, R, R, R),
                    4 => M(Z, R, R, R), 5 => M(O, R, R, R), 6 => M(R, Z, Z, Z), _ => M(R, O, O, O),
                };
            case TexBase.U8U8: case TexBase.S8S8: case TexBase.U16U16: case TexBase.S16S16: case TexBase.F16F16:
            case TexBase.U32U32: case TexBase.F32F32: case TexBase.UBC5: case TexBase.SBC5:
                return s switch
                {
                    0 => M(R, G, Z, O), 1 => M(R, G, Z, Z), 2 => M(R, R, R, G), 3 => M(G, G, G, R),
                    4 => M(R, G, R, G), 5 => M(G, R, Z, Z), _ => Identity,
                };
            case TexBase.X8U24: return Identity;
            case TexBase.U8U8U8: case TexBase.S8S8S8: case TexBase.F11F11F10: case TexBase.SE5M9M9M9:
                return s == 0 ? M(R, G, B, O) : M(B, G, R, O);
            case TexBase.U5U6U5:
                return s == 0 ? M(B, G, R, O) : M(R, G, B, O);
            case TexBase.U4U4U4U4:
                return s switch { 0 => M(A, B, G, R), 1 => M(G, B, A, R), 2 => M(R, G, B, A), 3 => M(B, G, R, A), _ => Identity };
            case TexBase.U1U5U5U5:
                // uploaded as A1R5G5B5 (A in bit 15, then 5-bit fields from the top). GXM names the fields from the most
                // significant bit, so ABGR is A1B5G5R5 and ARGB is A1R5G5B5; the U5U5U5U1 variants (RGBA, BGRA, RGB1,
                // BGR1) get their 1-bit field moved to bit 15 on upload (TextureDecoder) and become ARGB/ABGR/1RGB/1BGR
                // (Vita3K translate_swizzle4_a1r5g5b5)
                return s switch
                {
                    0 or 3 => M(B, G, R, A), 1 or 2 => M(R, G, B, A),
                    4 or 7 => M(B, G, R, O), _ => M(R, G, B, O),
                };
            case TexBase.YUV420P2: case TexBase.YUV420P3: case TexBase.YUV422:
                return Identity;
            default:
                return s switch
                {
                    0 => M(R, G, B, A), 1 => M(B, G, R, A), 2 => M(A, B, G, R), 3 => M(G, B, A, R),
                    4 => M(R, G, B, O), 5 => M(B, G, R, O), 6 => M(A, B, G, O), _ => M(G, B, A, O),
                };
        }
    }

    /// <summary>The sRGB variant of an 8-bit UNORM format (texture/surface gamma), or the format itself.</summary>
    public static Format Srgb(Format f) => f switch
    {
        Format.R8G8B8A8Unorm => Format.R8G8B8A8Srgb,
        Format.B8G8R8A8Unorm => Format.B8G8R8A8Srgb,
        Format.A8B8G8R8UnormPack32 => Format.A8B8G8R8SrgbPack32,
        Format.R8G8B8Unorm => Format.R8G8B8Srgb,
        Format.R8G8Unorm => Format.R8G8Srgb,
        Format.R8Unorm => Format.R8Srgb,
        Format.BC1RgbaUnormBlock => Format.BC1RgbaSrgbBlock,
        Format.BC1RgbUnormBlock => Format.BC1RgbSrgbBlock,
        Format.BC2UnormBlock => Format.BC2SrgbBlock,
        Format.BC3UnormBlock => Format.BC3SrgbBlock,
        Format.BC7UnormBlock => Format.BC7SrgbBlock,
        _ => f,
    };

    // ---------------------------------------------------------------- colour surfaces

    public static Format ColorFormat(uint colorFormat) => (colorFormat & 0xF1800000) switch
    {
        0x80800000 => Format.R8Unorm,
        0x90800000 => Format.R8SNorm,
        0x40800000 => Format.R16Unorm,
        0x20800000 => Format.R16SNorm,
        0xF0000000 => Format.R16Sfloat,
        0x10800000 => Format.R32Sfloat,
        0xB0800000 => Format.R8G8Unorm,
        0xC0800000 => Format.R8G8SNorm,
        0x50800000 => Format.R16G16Unorm,
        0x30800000 => Format.R16G16SNorm,
        0x00800000 => Format.R16G16Sfloat,
        0x11000000 => Format.R32G32Sfloat,
        0x00000000 => Format.R8G8B8A8Unorm,
        0xE0800000 => Format.R8G8B8A8SNorm,
        0x01000000 => Format.R16G16B16A16Sfloat,
        0x30000000 => Format.R5G6B5UnormPack16,
        0x21000000 => Format.B10G11R11UfloatPack32,
        0x31000000 => Format.E5B9G9R9UfloatPack32,
        0x10000000 => Format.R8G8B8A8Unorm,
        0x40000000 => Format.A1R5G5B5UnormPack16,
        0x50000000 => Format.R4G4B4A4UnormPack16,
        0x60800000 => Format.A2R10G10B10UnormPack32,
        0x41000000 => Format.R16G16B16A16Sfloat,
        _ => Format.R8G8B8A8Unorm,
    };

    /// <summary>Swizzle applied when the colour surface is sampled/presented (the shader writes logical RGBA).</summary>
    public static ComponentMapping ColorSwizzle(uint colorFormat)
    {
        uint s = (colorFormat >> 20) & 3;
        return (colorFormat & 0xF1800000) switch
        {
            0x00000000 or 0xE0800000 or 0x01000000 or 0x60800000 or 0x41000000 =>
                s switch { 0 => M(R, G, B, A), 1 => M(B, G, R, A), 2 => M(A, B, G, R), _ => M(G, B, A, R) },
            _ => Identity,
        };
    }

    public static int ColorBytesPerPixel(uint colorFormat) => (colorFormat & 0xF1800000) switch
    {
        0x80800000 or 0x90800000 => 1,
        0x40800000 or 0x20800000 or 0xF0000000 or 0xB0800000 or 0xC0800000 or 0x30000000 or 0x40000000 or 0x50000000 or 0x60000000 or 0xA0800000 => 2,
        0x10000000 => 3,
        0x01000000 or 0x11000000 => 8,
        _ => 4,
    };

    // ---------------------------------------------------------------- vertex attributes

    /// <summary>Every vertex format <see cref="Attribute"/> can return (the capability report checks them).</summary>
    public static IEnumerable<Format> AllAttributeFormats()
    {
        var all = new List<Format>();
        for (byte g = 0; g <= 10; g++)
            foreach (char k in "fiu")
                for (int c = 1; c <= 4; c++)
                {
                    var f = Attribute(g, c, k).Format;
                    if (!all.Contains(f)) all.Add(f);
                }
        return all;
    }

    public static (Format Format, int Size) Attribute(byte gxmFormat, int count, char kind)
    {
        count = Math.Clamp(count, 1, 4);
        int ci = count - 1;
        bool integer = kind is 'i' or 'u';
        Format[] row = gxmFormat switch
        {
            0 => integer ? [Format.R8Uint, Format.R8G8Uint, Format.R8G8B8Uint, Format.R8G8B8A8Uint] : [Format.R8Uscaled, Format.R8G8Uscaled, Format.R8G8B8Uscaled, Format.R8G8B8A8Uscaled],
            1 => integer ? [Format.R8Sint, Format.R8G8Sint, Format.R8G8B8Sint, Format.R8G8B8A8Sint] : [Format.R8Sscaled, Format.R8G8Sscaled, Format.R8G8B8Sscaled, Format.R8G8B8A8Sscaled],
            2 => integer ? [Format.R16Uint, Format.R16G16Uint, Format.R16G16B16Uint, Format.R16G16B16A16Uint] : [Format.R16Uscaled, Format.R16G16Uscaled, Format.R16G16B16Uscaled, Format.R16G16B16A16Uscaled],
            3 => integer ? [Format.R16Sint, Format.R16G16Sint, Format.R16G16B16Sint, Format.R16G16B16A16Sint] : [Format.R16Sscaled, Format.R16G16Sscaled, Format.R16G16B16Sscaled, Format.R16G16B16A16Sscaled],
            4 => integer ? [Format.R8Uint, Format.R8G8Uint, Format.R8G8B8Uint, Format.R8G8B8A8Uint] : [Format.R8Unorm, Format.R8G8Unorm, Format.R8G8B8Unorm, Format.R8G8B8A8Unorm],
            5 => integer ? [Format.R8Sint, Format.R8G8Sint, Format.R8G8B8Sint, Format.R8G8B8A8Sint] : [Format.R8SNorm, Format.R8G8SNorm, Format.R8G8B8SNorm, Format.R8G8B8A8SNorm],
            6 => integer ? [Format.R16Uint, Format.R16G16Uint, Format.R16G16B16Uint, Format.R16G16B16A16Uint] : [Format.R16Unorm, Format.R16G16Unorm, Format.R16G16B16Unorm, Format.R16G16B16A16Unorm],
            7 => integer ? [Format.R16Sint, Format.R16G16Sint, Format.R16G16B16Sint, Format.R16G16B16A16Sint] : [Format.R16SNorm, Format.R16G16SNorm, Format.R16G16B16SNorm, Format.R16G16B16A16SNorm],
            8 => [Format.R16Sfloat, Format.R16G16Sfloat, Format.R16G16B16Sfloat, Format.R16G16B16A16Sfloat],
            9 => kind == 'u' ? [Format.R32Uint, Format.R32G32Uint, Format.R32G32B32Uint, Format.R32G32B32A32Uint]
                : kind == 'i' ? [Format.R32Sint, Format.R32G32Sint, Format.R32G32B32Sint, Format.R32G32B32A32Sint]
                : [Format.R32Sfloat, Format.R32G32Sfloat, Format.R32G32B32Sfloat, Format.R32G32B32A32Sfloat],
            _ => kind == 'f' ? [Format.R32Sfloat, Format.R32G32Sfloat, Format.R32G32B32Sfloat, Format.R32G32B32A32Sfloat]
                : kind == 'i' ? [Format.R32Sint, Format.R32G32Sint, Format.R32G32B32Sint, Format.R32G32B32A32Sint]
                : [Format.R32Uint, Format.R32G32Uint, Format.R32G32B32Uint, Format.R32G32B32A32Uint],
        };
        int compSize = gxmFormat switch { 0 or 1 or 4 or 5 => 1, 2 or 3 or 6 or 7 or 8 => 2, _ => 4 };
        return (row[ci], compSize * count);
    }

    // ---------------------------------------------------------------- blending / misc

    public static BlendFactor Factor(byte f) => f switch
    {
        0 => BlendFactor.Zero,
        1 => BlendFactor.One,
        2 => BlendFactor.SrcColor,
        3 => BlendFactor.OneMinusSrcColor,
        4 => BlendFactor.SrcAlpha,
        5 => BlendFactor.OneMinusSrcAlpha,
        6 => BlendFactor.DstColor,
        7 => BlendFactor.OneMinusDstColor,
        8 => BlendFactor.DstAlpha,
        9 => BlendFactor.OneMinusDstAlpha,
        10 => BlendFactor.SrcAlphaSaturate,
        11 => BlendFactor.DstAlpha,
        _ => BlendFactor.One,
    };

    public static BlendOp Op(byte f) => f switch
    {
        2 => BlendOp.Subtract,
        3 => BlendOp.ReverseSubtract,
        4 => BlendOp.Min,
        5 => BlendOp.Max,
        _ => BlendOp.Add,
    };

    public static ColorComponentFlags Mask(byte gxmMask)
    {
        ColorComponentFlags f = 0;
        if ((gxmMask & 1) != 0) f |= ColorComponentFlags.ABit;
        if ((gxmMask & 2) != 0) f |= ColorComponentFlags.RBit;
        if ((gxmMask & 4) != 0) f |= ColorComponentFlags.GBit;
        if ((gxmMask & 8) != 0) f |= ColorComponentFlags.BBit;
        return f;
    }

    public static PrimitiveTopology Topology(byte prim) => prim switch
    {
        1 => PrimitiveTopology.LineList,
        2 => PrimitiveTopology.PointList,
        3 => PrimitiveTopology.TriangleStrip,
        4 => PrimitiveTopology.TriangleFan,
        _ => PrimitiveTopology.TriangleList,
    };

    public static PolygonMode Polygon(byte mode) => mode switch
    {
        1 or 5 => PolygonMode.Line,
        2 or 3 or 4 or 6 => PolygonMode.Point,
        _ => PolygonMode.Fill,
    };

    /// <summary>samplerMirrorClampToEdge (Vulkan 1.2 feature) is optional; set by VkCore at device creation.</summary>
    public static bool MirrorClampToEdgeSupported = true;

    public static SamplerAddressMode Address(uint m) => m switch
    {
        0 => SamplerAddressMode.Repeat,
        1 => SamplerAddressMode.MirroredRepeat,
        2 => SamplerAddressMode.ClampToEdge,
        3 => MirrorClampToEdgeSupported ? SamplerAddressMode.MirrorClampToEdge : SamplerAddressMode.MirroredRepeat,
        4 => SamplerAddressMode.Repeat,
        _ => SamplerAddressMode.ClampToBorder,
    };
}
