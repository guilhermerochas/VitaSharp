using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The GPU table, block 1 (docs/roadmap.md, "Achar erros sem jogar", front 2): every SceGxmTextureFormat of vita-headers
/// gxm.h in every memory layout (linear, swizzled, tiled, linear strided), sampled by a draw on the software Vulkan
/// device and read back from the colour surface, against the texel this file computes from the format's name alone.
/// The reference is written from the definitions, not from the renderer's code:
/// <list type="bullet">
/// <item>A format name lists its fields from the most significant bit down (gxm.h: U8U8U8U8_ABGR is "A8B8G8R8",
/// U2U10U10U10_ABGR "A2B10G10R10", the legacy A1R5G5B5 = U1U5U5U5_ARGB, R5G6B5 = U5U6U5_RGB); the field widths of the
/// name of each swizzle are its real bit layout (U10U10U10U2_RGBA has alpha in the low 2 bits, X8U8S8S8_1RGB has red
/// unsigned in bits 16-23).</item>
/// <item>With as many letters as fields, each letter names the output of the field under it ('1'/'0': an X field ignored,
/// that output constant); one- and two-field formats with four letters name the outputs A, B, G, R in that order from
/// the fields R (low) and G (high): gxm.h's legacy A8 = U8_R000 (alpha = R), L8 = U8_1RRR, A8L8 = U8U8_GRRR.</item>
/// <item>U = unsigned normalised (raw / (2^n - 1)), S = signed normalised (raw / (2^(n-1) - 1), clamped at -1), F16/F32
/// IEEE floats, F11/F10 unsigned floats (5-bit exponent, bias 15, 6/5-bit mantissa), SE5M9M9M9 a shared 5-bit
/// exponent over three 9-bit mantissas (value = m * 2^(e - 24)); F32M is F32 with its sign masked (Vita3K
/// convert_f32m_to_f32).</item>
/// <item>P4/P8: the index selects a 32-bit palette entry, read as U8U8U8U8 with the format's swizzle; P4 holds the even
/// texel in the low nibble (Vita3K palette.cpp).</item>
/// <item>Layouts (Vita3K texture/cache.cpp, texture_format.cpp): linear rows of align(width, 8) texels; strided rows of the
/// given byte stride; swizzled = Morton order with y in bit 0 and the larger side's extra bits on top; tiled = 32x32
/// tiles in row order, each tile row-major.</item>
/// </list>
/// The texture coordinate is the vertex position, so on a 2w x 2h target pixel (w + x, h - 1 - y) shows texel (x, y)
/// (the viewport has y up; the rest of the target shows clamped coordinates).
/// Not in this block: U32/S32/U32U32 (raw register words), the compressed and YUV formats (their own blocks).
/// </summary>
[Collection("guest")]
public sealed class GpuTextureFormatTableTests(GuestMemoryFixture fx)
{
    /// <summary>gxm.h's SCE_GXM_TEXTURE_FORMAT_* names: name, base format, swizzle (bits 12-14).</summary>
    internal static readonly (string Name, TexBase Base, uint Swizzle)[] s_formats =
    [
        ("U8_000R", TexBase.U8, 1), ("U8_111R", TexBase.U8, 2), ("U8_RRRR", TexBase.U8, 3),
        ("U8_0RRR", TexBase.U8, 4), ("U8_1RRR", TexBase.U8, 5), ("U8_R000", TexBase.U8, 6),
        ("U8_R111", TexBase.U8, 7), ("U8_R", TexBase.U8, 0), ("S8_000R", TexBase.S8, 1),
        ("S8_111R", TexBase.S8, 2), ("S8_RRRR", TexBase.S8, 3), ("S8_0RRR", TexBase.S8, 4),
        ("S8_1RRR", TexBase.S8, 5), ("S8_R000", TexBase.S8, 6), ("S8_R111", TexBase.S8, 7),
        ("S8_R", TexBase.S8, 0), ("U4U4U4U4_ABGR", TexBase.U4U4U4U4, 0), ("U4U4U4U4_ARGB", TexBase.U4U4U4U4, 1),
        ("U4U4U4U4_RGBA", TexBase.U4U4U4U4, 2), ("U4U4U4U4_BGRA", TexBase.U4U4U4U4, 3), ("X4U4U4U4_1BGR", TexBase.U4U4U4U4, 4),
        ("X4U4U4U4_1RGB", TexBase.U4U4U4U4, 5), ("U4U4U4X4_RGB1", TexBase.U4U4U4U4, 6), ("U4U4U4X4_BGR1", TexBase.U4U4U4U4, 7),
        ("U8U3U3U2_ARGB", TexBase.U8U3U3U2, 0), ("U1U5U5U5_ABGR", TexBase.U1U5U5U5, 0), ("U1U5U5U5_ARGB", TexBase.U1U5U5U5, 1),
        ("U5U5U5U1_RGBA", TexBase.U1U5U5U5, 2), ("U5U5U5U1_BGRA", TexBase.U1U5U5U5, 3), ("X1U5U5U5_1BGR", TexBase.U1U5U5U5, 4),
        ("X1U5U5U5_1RGB", TexBase.U1U5U5U5, 5), ("U5U5U5X1_RGB1", TexBase.U1U5U5U5, 6), ("U5U5U5X1_BGR1", TexBase.U1U5U5U5, 7),
        ("U5U6U5_BGR", TexBase.U5U6U5, 0), ("U5U6U5_RGB", TexBase.U5U6U5, 1), ("U6S5S5_BGR", TexBase.S5S5U6, 0),
        ("S5S5U6_RGB", TexBase.S5S5U6, 1), ("U8U8_00GR", TexBase.U8U8, 1), ("U8U8_GRRR", TexBase.U8U8, 2),
        ("U8U8_RGGG", TexBase.U8U8, 3), ("U8U8_GRGR", TexBase.U8U8, 4), ("U8U8_00RG", TexBase.U8U8, 5),
        ("U8U8_GR", TexBase.U8U8, 0), ("S8S8_00GR", TexBase.S8S8, 1), ("S8S8_GRRR", TexBase.S8S8, 2),
        ("S8S8_RGGG", TexBase.S8S8, 3), ("S8S8_GRGR", TexBase.S8S8, 4), ("S8S8_00RG", TexBase.S8S8, 5),
        ("S8S8_GR", TexBase.S8S8, 0), ("U16_000R", TexBase.U16, 1), ("U16_111R", TexBase.U16, 2),
        ("U16_RRRR", TexBase.U16, 3), ("U16_0RRR", TexBase.U16, 4), ("U16_1RRR", TexBase.U16, 5),
        ("U16_R000", TexBase.U16, 6), ("U16_R111", TexBase.U16, 7), ("U16_R", TexBase.U16, 0),
        ("S16_000R", TexBase.S16, 1), ("S16_111R", TexBase.S16, 2), ("S16_RRRR", TexBase.S16, 3),
        ("S16_0RRR", TexBase.S16, 4), ("S16_1RRR", TexBase.S16, 5), ("S16_R000", TexBase.S16, 6),
        ("S16_R111", TexBase.S16, 7), ("S16_R", TexBase.S16, 0), ("F16_000R", TexBase.F16, 1),
        ("F16_111R", TexBase.F16, 2), ("F16_RRRR", TexBase.F16, 3), ("F16_0RRR", TexBase.F16, 4),
        ("F16_1RRR", TexBase.F16, 5), ("F16_R000", TexBase.F16, 6), ("F16_R111", TexBase.F16, 7),
        ("F16_R", TexBase.F16, 0), ("U8U8U8U8_ABGR", TexBase.U8U8U8U8, 0), ("U8U8U8U8_ARGB", TexBase.U8U8U8U8, 1),
        ("U8U8U8U8_RGBA", TexBase.U8U8U8U8, 2), ("U8U8U8U8_BGRA", TexBase.U8U8U8U8, 3), ("X8U8U8U8_1BGR", TexBase.U8U8U8U8, 4),
        ("X8U8U8U8_1RGB", TexBase.U8U8U8U8, 5), ("U8U8U8X8_RGB1", TexBase.U8U8U8U8, 6), ("U8U8U8X8_BGR1", TexBase.U8U8U8U8, 7),
        ("S8S8S8S8_ABGR", TexBase.S8S8S8S8, 0), ("S8S8S8S8_ARGB", TexBase.S8S8S8S8, 1), ("S8S8S8S8_RGBA", TexBase.S8S8S8S8, 2),
        ("S8S8S8S8_BGRA", TexBase.S8S8S8S8, 3), ("X8S8S8S8_1BGR", TexBase.S8S8S8S8, 4), ("X8S8S8S8_1RGB", TexBase.S8S8S8S8, 5),
        ("S8S8S8X8_RGB1", TexBase.S8S8S8S8, 6), ("S8S8S8X8_BGR1", TexBase.S8S8S8S8, 7), ("U2U10U10U10_ABGR", TexBase.U2U10U10U10, 0),
        ("U2U10U10U10_ARGB", TexBase.U2U10U10U10, 1), ("U10U10U10U2_RGBA", TexBase.U2U10U10U10, 2), ("U10U10U10U2_BGRA", TexBase.U2U10U10U10, 3),
        ("X2U10U10U10_1BGR", TexBase.U2U10U10U10, 4), ("X2U10U10U10_1RGB", TexBase.U2U10U10U10, 5), ("U10U10U10X2_RGB1", TexBase.U2U10U10U10, 6),
        ("U10U10U10X2_BGR1", TexBase.U2U10U10U10, 7), ("U16U16_00GR", TexBase.U16U16, 1), ("U16U16_GRRR", TexBase.U16U16, 2),
        ("U16U16_RGGG", TexBase.U16U16, 3), ("U16U16_GRGR", TexBase.U16U16, 4), ("U16U16_00RG", TexBase.U16U16, 5),
        ("U16U16_GR", TexBase.U16U16, 0), ("S16S16_00GR", TexBase.S16S16, 1), ("S16S16_GRRR", TexBase.S16S16, 2),
        ("S16S16_RGGG", TexBase.S16S16, 3), ("S16S16_GRGR", TexBase.S16S16, 4), ("S16S16_00RG", TexBase.S16S16, 5),
        ("S16S16_GR", TexBase.S16S16, 0), ("F16F16_00GR", TexBase.F16F16, 1), ("F16F16_GRRR", TexBase.F16F16, 2),
        ("F16F16_RGGG", TexBase.F16F16, 3), ("F16F16_GRGR", TexBase.F16F16, 4), ("F16F16_00RG", TexBase.F16F16, 5),
        ("F16F16_GR", TexBase.F16F16, 0), ("F32_000R", TexBase.F32, 1), ("F32_111R", TexBase.F32, 2),
        ("F32_RRRR", TexBase.F32, 3), ("F32_0RRR", TexBase.F32, 4), ("F32_1RRR", TexBase.F32, 5),
        ("F32_R000", TexBase.F32, 6), ("F32_R111", TexBase.F32, 7), ("F32_R", TexBase.F32, 0),
        ("F32M_000R", TexBase.F32M, 1), ("F32M_111R", TexBase.F32M, 2), ("F32M_RRRR", TexBase.F32M, 3),
        ("F32M_0RRR", TexBase.F32M, 4), ("F32M_1RRR", TexBase.F32M, 5), ("F32M_R000", TexBase.F32M, 6),
        ("F32M_R111", TexBase.F32M, 7), ("F32M_R", TexBase.F32M, 0), ("X8S8S8U8_1BGR", TexBase.X8S8S8U8, 0),
        ("X8U8S8S8_1RGB", TexBase.X8S8S8U8, 1), ("X8U24_SD", TexBase.X8U24, 0), ("U24X8_DS", TexBase.X8U24, 1),
        ("U32_000R", TexBase.U32, 1), ("U32_111R", TexBase.U32, 2), ("U32_RRRR", TexBase.U32, 3),
        ("U32_0RRR", TexBase.U32, 4), ("U32_1RRR", TexBase.U32, 5), ("U32_R000", TexBase.U32, 6),
        ("U32_R111", TexBase.U32, 7), ("U32_R", TexBase.U32, 0), ("S32_000R", TexBase.S32, 1),
        ("S32_111R", TexBase.S32, 2), ("S32_RRRR", TexBase.S32, 3), ("S32_0RRR", TexBase.S32, 4),
        ("S32_1RRR", TexBase.S32, 5), ("S32_R000", TexBase.S32, 6), ("S32_R111", TexBase.S32, 7),
        ("S32_R", TexBase.S32, 0), ("SE5M9M9M9_BGR", TexBase.SE5M9M9M9, 0), ("SE5M9M9M9_RGB", TexBase.SE5M9M9M9, 1),
        ("F10F11F11_BGR", TexBase.F11F11F10, 0), ("F11F11F10_RGB", TexBase.F11F11F10, 1), ("F16F16F16F16_ABGR", TexBase.F16F16F16F16, 0),
        ("F16F16F16F16_ARGB", TexBase.F16F16F16F16, 1), ("F16F16F16F16_RGBA", TexBase.F16F16F16F16, 2), ("F16F16F16F16_BGRA", TexBase.F16F16F16F16, 3),
        ("X16F16F16F16_1BGR", TexBase.F16F16F16F16, 4), ("X16F16F16F16_1RGB", TexBase.F16F16F16F16, 5), ("F16F16F16X16_RGB1", TexBase.F16F16F16F16, 6),
        ("F16F16F16X16_BGR1", TexBase.F16F16F16F16, 7), ("U16U16U16U16_ABGR", TexBase.U16U16U16U16, 0), ("U16U16U16U16_ARGB", TexBase.U16U16U16U16, 1),
        ("U16U16U16U16_RGBA", TexBase.U16U16U16U16, 2), ("U16U16U16U16_BGRA", TexBase.U16U16U16U16, 3), ("X16U16U16U16_1BGR", TexBase.U16U16U16U16, 4),
        ("X16U16U16U16_1RGB", TexBase.U16U16U16U16, 5), ("U16U16U16X16_RGB1", TexBase.U16U16U16U16, 6), ("U16U16U16X16_BGR1", TexBase.U16U16U16U16, 7),
        ("S16S16S16S16_ABGR", TexBase.S16S16S16S16, 0), ("S16S16S16S16_ARGB", TexBase.S16S16S16S16, 1), ("S16S16S16S16_RGBA", TexBase.S16S16S16S16, 2),
        ("S16S16S16S16_BGRA", TexBase.S16S16S16S16, 3), ("X16S16S16S16_1BGR", TexBase.S16S16S16S16, 4), ("X16S16S16S16_1RGB", TexBase.S16S16S16S16, 5),
        ("S16S16S16X16_RGB1", TexBase.S16S16S16S16, 6), ("S16S16S16X16_BGR1", TexBase.S16S16S16S16, 7), ("F32F32_00GR", TexBase.F32F32, 1),
        ("F32F32_GRRR", TexBase.F32F32, 2), ("F32F32_RGGG", TexBase.F32F32, 3), ("F32F32_GRGR", TexBase.F32F32, 4),
        ("F32F32_00RG", TexBase.F32F32, 5), ("F32F32_GR", TexBase.F32F32, 0), ("U32U32_00GR", TexBase.U32U32, 1),
        ("U32U32_GRRR", TexBase.U32U32, 2), ("U32U32_RGGG", TexBase.U32U32, 3), ("U32U32_GRGR", TexBase.U32U32, 4),
        ("U32U32_00RG", TexBase.U32U32, 5), ("U32U32_GR", TexBase.U32U32, 0), ("PVRT2BPP_ABGR", TexBase.PVRT2BPP, 0),
        ("PVRT2BPP_1BGR", TexBase.PVRT2BPP, 4), ("PVRT4BPP_ABGR", TexBase.PVRT4BPP, 0), ("PVRT4BPP_1BGR", TexBase.PVRT4BPP, 4),
        ("PVRTII2BPP_ABGR", TexBase.PVRTII2BPP, 0), ("PVRTII2BPP_1BGR", TexBase.PVRTII2BPP, 4), ("PVRTII4BPP_ABGR", TexBase.PVRTII4BPP, 0),
        ("PVRTII4BPP_1BGR", TexBase.PVRTII4BPP, 4), ("ETC1_1BGR", TexBase.ETC1, 0), ("UBC1_ABGR", TexBase.UBC1, 0),
        ("UBC1_1BGR", TexBase.UBC1, 4), ("UBC2_ABGR", TexBase.UBC2, 0), ("UBC2_1BGR", TexBase.UBC2, 4),
        ("UBC3_ABGR", TexBase.UBC3, 0), ("UBC3_1BGR", TexBase.UBC3, 4), ("UBC4_000R", TexBase.UBC4, 1),
        ("UBC4_111R", TexBase.UBC4, 2), ("UBC4_RRRR", TexBase.UBC4, 3), ("UBC4_0RRR", TexBase.UBC4, 4),
        ("UBC4_1RRR", TexBase.UBC4, 5), ("UBC4_R000", TexBase.UBC4, 6), ("UBC4_R111", TexBase.UBC4, 7),
        ("UBC4_R", TexBase.UBC4, 0), ("SBC4_000R", TexBase.SBC4, 1), ("SBC4_111R", TexBase.SBC4, 2),
        ("SBC4_RRRR", TexBase.SBC4, 3), ("SBC4_0RRR", TexBase.SBC4, 4), ("SBC4_1RRR", TexBase.SBC4, 5),
        ("SBC4_R000", TexBase.SBC4, 6), ("SBC4_R111", TexBase.SBC4, 7), ("SBC4_R", TexBase.SBC4, 0),
        ("UBC5_00GR", TexBase.UBC5, 1), ("UBC5_GRRR", TexBase.UBC5, 2), ("UBC5_RGGG", TexBase.UBC5, 3),
        ("UBC5_GRGR", TexBase.UBC5, 4), ("UBC5_00RG", TexBase.UBC5, 5), ("UBC5_GR", TexBase.UBC5, 0),
        ("SBC5_00GR", TexBase.SBC5, 1), ("SBC5_GRRR", TexBase.SBC5, 2), ("SBC5_RGGG", TexBase.SBC5, 3),
        ("SBC5_GRGR", TexBase.SBC5, 4), ("SBC5_00RG", TexBase.SBC5, 5), ("SBC5_GR", TexBase.SBC5, 0),
        ("YUV420P2_CSC0", TexBase.YUV420P2, 0), ("YVU420P2_CSC0", TexBase.YUV420P2, 1), ("YUV420P2_CSC1", TexBase.YUV420P2, 2),
        ("YVU420P2_CSC1", TexBase.YUV420P2, 3), ("YUV420P3_CSC0", TexBase.YUV420P3, 0), ("YVU420P3_CSC0", TexBase.YUV420P3, 1),
        ("YUV420P3_CSC1", TexBase.YUV420P3, 2), ("YVU420P3_CSC1", TexBase.YUV420P3, 3), ("YUYV422_CSC0", TexBase.YUV422, 0),
        ("YVYU422_CSC0", TexBase.YUV422, 1), ("UYVY422_CSC0", TexBase.YUV422, 2), ("VYUY422_CSC0", TexBase.YUV422, 3),
        ("YUYV422_CSC1", TexBase.YUV422, 4), ("YVYU422_CSC1", TexBase.YUV422, 5), ("UYVY422_CSC1", TexBase.YUV422, 6),
        ("VYUY422_CSC1", TexBase.YUV422, 7), ("P4_ABGR", TexBase.P4, 0), ("P4_ARGB", TexBase.P4, 1),
        ("P4_RGBA", TexBase.P4, 2), ("P4_BGRA", TexBase.P4, 3), ("P4_1BGR", TexBase.P4, 4),
        ("P4_1RGB", TexBase.P4, 5), ("P4_RGB1", TexBase.P4, 6), ("P4_BGR1", TexBase.P4, 7),
        ("P8_ABGR", TexBase.P8, 0), ("P8_ARGB", TexBase.P8, 1), ("P8_RGBA", TexBase.P8, 2),
        ("P8_BGRA", TexBase.P8, 3), ("P8_1BGR", TexBase.P8, 4), ("P8_1RGB", TexBase.P8, 5),
        ("P8_RGB1", TexBase.P8, 6), ("P8_BGR1", TexBase.P8, 7), ("U8U8U8_BGR", TexBase.U8U8U8, 0),
        ("U8U8U8_RGB", TexBase.U8U8U8, 1), ("S8S8S8_BGR", TexBase.S8S8S8, 0), ("S8S8S8_RGB", TexBase.S8S8S8, 1),
        ("U2F10F10F10_ABGR", TexBase.U2F10F10F10, 0), ("U2F10F10F10_ARGB", TexBase.U2F10F10F10, 1), ("F10F10F10U2_RGBA", TexBase.U2F10F10F10, 2),
        ("F10F10F10U2_BGRA", TexBase.U2F10F10F10, 3), ("X2F10F10F10_1BGR", TexBase.U2F10F10F10, 4), ("X2F10F10F10_1RGB", TexBase.U2F10F10F10, 5),
        ("F10F10F10X2_RGB1", TexBase.U2F10F10F10, 6), ("F10F10F10X2_BGR1", TexBase.U2F10F10F10, 7),
    ];

    /// <summary>Formats whose fetch still diverges from the table in some layout: each is a finding of the table (see
    /// docs/roadmap.md), checked to still diverge so that a fix removes it from here.</summary>
    private static readonly HashSet<string> s_knownDivergent =
    [
        "F10F10F10U2_RGBA", "F10F10F10U2_BGRA", "F10F10F10X2_RGB1", "F10F10F10X2_BGR1", "F11F11F10_RGB",
        "F32M_000R", "F32M_111R", "F32M_RRRR", "F32M_0RRR", "F32M_1RRR", "F32M_R000", "F32M_R111", "F32M_R",
        "S5S5U6_RGB", "U6S5S5_BGR", "U10U10U10U2_RGBA", "U10U10U10U2_BGRA", "U10U10U10X2_RGB1", "U10U10U10X2_BGR1",
        "U24X8_DS", "X4U4U4U4_1BGR", "X4U4U4U4_1RGB", "U4U4U4X4_RGB1", "U4U4U4X4_BGR1", "U8U3U3U2_ARGB",
        "X8S8S8U8_1BGR", "X8U8S8S8_1RGB",
    ];

    /// <summary>The memory layouts of this block: SceGxmTextureType and the size of its texture.</summary>
    internal enum Layout
    {
        Linear, Swizzled, Tiled, Strided
    }

    private static readonly (Layout Layout, uint Width, uint Height)[] s_layouts =
    [
        (Layout.Linear, 12, 3), (Layout.Swizzled, 8, 4), (Layout.Tiled, 36, 3), (Layout.Strided, 12, 3),
    ];

    /// <summary>Byte stride of the strided textures: a row of 12 texels of up to 8 bytes, and room after it.</summary>
    private const uint StridedBytes = 112;

    private enum Kind
    {
        Unorm, Snorm, Float, UFloat, Mantissa, SharedExponent, Ignored
    }

    private readonly record struct Field(Kind Kind, int Bits, bool Abs = false);

    /// <summary>A channel's source: a field index, or a constant (<see cref="Zero"/>, <see cref="One"/>), or
    /// <see cref="Unspecified"/> (a component the format does not return: not compared).</summary>
    private const int Zero = -1, One = -2, Unspecified = -3;

    /// <summary>The reference model of one format: its fields from the most significant bit down, the source of R, G, B
    /// and A, and (P4/P8) the index width whose palette entry the fields describe.</summary>
    private sealed record Model(Field[] Fields, int[] Channels, int IndexBits)
    {
        public int Bits => Fields.Sum(f => f.Bits);

        /// <summary>The bits per texel in memory (the index for P4/P8).</summary>
        public int TexelBits => IndexBits != 0 ? IndexBits : Bits;
    }

    private static readonly Regex s_field = new(@"(SE|[USFXM])(\d+)(M(?!\d))?", RegexOptions.CultureInvariant);

    /// <summary>The model of a gxm.h format name, or null for the formats outside this block.</summary>
    private static Model? Parse(string name, TexBase texBase)
    {
        if (texBase is TexBase.U32 or TexBase.S32 or TexBase.U32U32 || (uint)texBase >= 0x80000000 && texBase is not (TexBase.P4 or TexBase.P8 or TexBase.U8U8U8 or TexBase.S8S8S8 or TexBase.U2F10F10F10))
        {
            return null;
        }

        string[] parts = name.Split('_');
        string fieldText = parts[0], letters = parts[1];
        int indexBits = 0;
        if (texBase is TexBase.P4 or TexBase.P8)
        {
            // the palette entry: four bytes, named by the swizzle like U8U8U8U8 (letters for X fields included)
            indexBits = texBase == TexBase.P4 ? 4 : 8;
            fieldText = "U8U8U8U8";
        }

        var fields = new List<Field>();
        foreach (Match m in s_field.Matches(fieldText))
        {
            int bits = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            Kind kind = m.Groups[1].Value switch
            {
                "U" => Kind.Unorm,
                "S" => Kind.Snorm,
                "X" => Kind.Ignored,
                "M" => Kind.Mantissa,
                "SE" => Kind.SharedExponent,
                _ => bits is 16 or 32 ? Kind.Float : Kind.UFloat,
            };
            fields.Add(new Field(kind, bits, Abs: m.Groups[3].Success));
        }

        // the shared exponent is no channel: the letters name the mantissas
        var channelFields = Enumerable.Range(0, fields.Count).Where(i => fields[i].Kind != Kind.SharedExponent).ToList();
        int[] channels = [Unspecified, Unspecified, Unspecified, Unspecified];
        if (texBase == TexBase.X8U24)
        {
            // SD / DS: S (stencil) and D (depth, read as unsigned normalised) from the high bits down; the depth is the
            // one component returned
            channels[0] = letters.IndexOf('D', StringComparison.Ordinal);
            return new Model([.. fields], channels, 0);
        }

        if (letters.Length == channelFields.Count)
        {
            // one letter per field, from the high bits down
            for (int i = 0; i < letters.Length; i++)
            {
                int channel = "RGBA".IndexOf(letters[i], StringComparison.Ordinal);
                if (channel >= 0)
                {
                    channels[channel] = channelFields[i];
                }
                else
                {
                    channels[3] = letters[i] == '1' ? One : Zero;
                }
            }

            if (letters.Length == 3)
            {
                channels[3] = One;
            }
        }
        else
        {
            // four letters over one or two fields: outputs A, B, G, R from the fields R (low) and G (high)
            Assert.Equal(4, letters.Length);
            int fieldR = channelFields[^1], fieldG = channelFields.Count > 1 ? channelFields[0] : Unspecified;
            for (int i = 0; i < 4; i++)
            {
                channels[3 - i] = letters[i] switch
                {
                    'R' => fieldR,
                    'G' => fieldG,
                    '1' => One,
                    _ => Zero,
                };
            }
        }
        return new Model([.. fields], channels, indexBits);
    }

    /// <summary>Random field contents: every bit pattern for normalised fields, and for float fields finite values the
    /// F16 colour surface holds (|x| below 2^15).</summary>
    private static ulong RandomField(Field field, Random rng)
    {
        switch (field.Kind)
        {
            case Kind.Float when field.Bits == 32:
                return BitConverter.SingleToUInt32Bits((float)Math.Round((rng.NextDouble() * 16 - 8) * 1024) / 1024);
            case Kind.Float:
                return BitConverter.HalfToUInt16Bits((Half)((rng.NextDouble() * 16 - 8)));
            case Kind.UFloat:
            {
                int mantissaBits = field.Bits - 5;
                return ((ulong)(uint)rng.Next(0, 31) << mantissaBits) | (uint)rng.Next(0, 1 << mantissaBits);
            }
            case Kind.SharedExponent:
                return (ulong)rng.Next(0, 31);
            default:
                return (ulong)rng.NextInt64(0, 1L << field.Bits);
        }
    }

    private static double Decode(Field field, ulong raw, ulong sharedExponent)
    {
        switch (field.Kind)
        {
            case Kind.Unorm:
                return raw / (double)((1UL << field.Bits) - 1);
            case Kind.Snorm:
            {
                long signed = (long)(raw << (64 - field.Bits)) >> (64 - field.Bits);
                return Math.Max(signed / (double)((1L << (field.Bits - 1)) - 1), -1);
            }
            case Kind.Float when field.Bits == 32:
            {
                double value = BitConverter.UInt32BitsToSingle((uint)raw);
                return field.Abs ? Math.Abs(value) : value;
            }
            case Kind.Float:
                return (double)BitConverter.UInt16BitsToHalf((ushort)raw);
            case Kind.UFloat:
            {
                // unsigned small float: 5-bit exponent (bias 15), the rest mantissa; exponent 0 = denormal
                int mantissaBits = field.Bits - 5;
                ulong exponent = raw >> mantissaBits, mantissa = raw & ((1UL << mantissaBits) - 1);
                return exponent == 0
                    ? mantissa / (double)(1UL << mantissaBits) * Math.Pow(2, -14)
                    : (1 + mantissa / (double)(1UL << mantissaBits)) * Math.Pow(2, (int)exponent - 15);
            }
            case Kind.Mantissa:
                return raw * Math.Pow(2, (int)sharedExponent - 15 - 9);
            default:
                return 0;
        }
    }

    /// <summary>The bit offset of texel (x, y) in the layout.</summary>
    private static long TexelBitOffset(Layout layout, uint x, uint y, uint width, uint height, int texelBits)
    {
        switch (layout)
        {
            case Layout.Swizzled:
            {
                // Morton order over the power-of-two square of the smaller side, y in bit 0; the larger side's
                // remaining bits above (Vita3K encode_morton)
                uint min = Math.Min(width, height);
                int k = System.Numerics.BitOperations.Log2(min);
                long index = (long)((x >> k) | (y >> k)) << (2 * k);
                for (int b = 0; b < k; b++)
                {
                    index |= (long)((x >> b) & 1) << (2 * b + 1);
                    index |= (long)((y >> b) & 1) << (2 * b);
                }

                return index * texelBits;
            }
            case Layout.Tiled:
            {
                long tilesPerRow = (width + 31) / 32;
                long tile = (x / 32) + tilesPerRow * (y / 32);
                return ((tile << 10) | ((y & 31) << 5) | (x & 31)) * texelBits;
            }
            case Layout.Strided:
                // rows of the byte stride
                return y * StridedBytes * 8L + (long)x * texelBits;
            default:
                return (y * ((width + 7) & ~7u) + x) * (long)texelBits;
        }
    }

    private static uint TextureBytes(Layout layout, uint width, uint height, int texelBits) => layout switch
    {
        Layout.Tiled => (uint)(((width + 31) / 32) * ((height + 31) / 32) * 1024 * texelBits / 8),
        Layout.Strided => StridedBytes * height,
        _ => (uint)(((width + 7) & ~7u) * Math.Max(height, 1) * texelBits / 8 + 64),
    };

    private sealed class Case
    {
        public required string Name;
        public required Layout Layout;
        public required uint Width, Height, Surface, SurfaceData;
        public required double?[] Expected; // per texel, R, G, B, A (null: not compared)
    }

    /// <summary>Writes the texture of <paramref name="model"/> with random contents into guest memory and returns, for
    /// each texel, what a fetch returns (from the format definition).</summary>
    private static double?[] Fill(Model model, Layout layout, uint width, uint height, uint data, uint palette, Random rng)
    {
        double?[] expected = new double?[width * height * 4];
        uint[] paletteWords = new uint[1 << Math.Max(model.IndexBits, 1)];
        if (model.IndexBits != 0)
        {
            for (int i = 0; i < paletteWords.Length; i++)
            {
                paletteWords[i] = (uint)rng.NextInt64(0, 1L << 32);
                GuestMemory.W32(palette + (uint)i * 4, paletteWords[i]);
            }
        }

        ulong[] raws = new ulong[model.Fields.Length];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                ulong word = 0;
                if (model.IndexBits != 0)
                {
                    uint index = (uint)rng.Next(0, 1 << model.IndexBits);
                    word = paletteWords[index];
                    long nibbleOrByte = TexelBitOffset(layout, x, y, width, height, model.IndexBits) / model.IndexBits;
                    if (model.IndexBits == 4)
                    {
                        uint address = data + (uint)(nibbleOrByte / 2);
                        byte old = GuestMemory.Read<byte>(address);
                        GuestMemory.Write(address, (byte)((nibbleOrByte & 1) != 0 ? (old & 0x0Fu) | (index << 4) : (old & 0xF0u) | index));
                    }
                    else
                    {
                        GuestMemory.Write(data + (uint)nibbleOrByte, (byte)index);
                    }
                }
                else
                {
                    for (int f = 0; f < model.Fields.Length; f++)
                    {
                        raws[f] = RandomField(model.Fields[f], rng);
                        word = (word << model.Fields[f].Bits) | raws[f];
                    }

                    long offset = TexelBitOffset(layout, x, y, width, height, model.Bits) / 8;
                    for (int b = 0; b < model.Bits / 8; b++)
                    {
                        GuestMemory.Write(data + (uint)offset + (uint)b, (byte)(word >> (8 * b)));
                    }
                }

                // the fields of the texel (or palette entry), from the high bits down
                int shift = model.Bits;
                ulong sharedExponent = 0;
                for (int f = 0; f < model.Fields.Length; f++)
                {
                    shift -= model.Fields[f].Bits;
                    raws[f] = (word >> shift) & ((1UL << model.Fields[f].Bits) - 1);
                    if (model.Fields[f].Kind == Kind.SharedExponent)
                    {
                        sharedExponent = raws[f];
                    }
                }

                for (int c = 0; c < 4; c++)
                {
                    int source = model.Channels[c];
                    expected[(y * width + x) * 4 + c] = source switch
                    {
                        Unspecified => null,
                        Zero => 0,
                        One => 1,
                        _ => Decode(model.Fields[source], raws[source], sharedExponent),
                    };
                }
            }
        }
        return expected;
    }

    /// <summary>F16 equality within one half-precision step of the expected value (the colour surface's rounding).</summary>
    private static bool Close(double expected, double actual)
    {
        double magnitude = Math.Max(Math.Abs(expected), Math.Pow(2, -14));
        double step = Math.Pow(2, Math.Floor(Math.Log2(magnitude)) - 10);
        return Math.Abs(expected - actual) <= step * 1.01;
    }

    /// <summary>Every format of the table in every layout of this block: the fetched texels against the reference.</summary>
    [SoftwareVulkanTheory]
    [InlineData(null)]
    [InlineData("+TileBased")]
    public void EveryTextureFormatAndLayoutFetchesTheTexelsOfItsDefinition(string? gpuQuirks)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "gputab");
        var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false, GpuQuirks = gpuQuirks });
        var failures = new StringBuilder();
        var known = new StringBuilder();
        var uncovered = new SortedSet<string>();
        int compared = 0;
        try
        {
            h.Emu.Gpu.Backend = renderer;
            Gxm g = h.Module<Gxm>();
            uint init = h.Alloc(0x14), o = h.Word(0);
            GuestMemory.W32(init + 4, 2);
            Assert.Equal(0, Call(g, "sceGxmInitialize", init));
            uint cp = h.Alloc(0x24);
            GuestMemory.W32(cp + 8, h.Alloc(0x1000));
            GuestMemory.W32(cp + 12, 0x1000);
            GuestMemory.W32(cp + 16, h.Alloc(0x10000));
            GuestMemory.W32(cp + 20, 0x10000);
            GuestMemory.W32(cp + 24, h.Alloc(0x10000));
            GuestMemory.W32(cp + 28, 0x10000);
            Assert.Equal(0, Call(g, "sceGxmCreateContext", cp, new Ptr<uint>(o)));
            uint ctx = GuestMemory.R32(o);

            uint pp = h.Alloc(0x40);
            Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreate", pp, new Ptr<uint>(o)));
            uint patcher = GuestMemory.R32(o), vid = h.Word(0), fid = h.Word(0);
            Call(g, "sceGxmShaderPatcherRegisterProgram", patcher, RendererTextureTests.VertexProgram(h), new Ptr<uint>(vid));
            Call(g, "sceGxmShaderPatcherRegisterProgram", patcher, RendererTextureTests.SamplingFragmentProgram(h), new Ptr<uint>(fid));
            uint attr = h.Alloc(8), stream = h.Alloc(4), vp = h.Word(0), fp = h.Word(0);
            GuestMemory.Write(attr + 4, (byte)9); // F32
            GuestMemory.Write(attr + 5, (byte)4);
            GuestMemory.Write(stream, (ushort)16);
            Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreateVertexProgram", patcher, GuestMemory.R32(vid), attr, 1u, stream, 1u, new Ptr<uint>(vp)));
            uint vertexGxp = GuestMemory.R32(GuestMemory.R32(vid));
            Assert.Equal(0, Call(g, "sceGxmShaderPatcherCreateFragmentProgram", patcher, GuestMemory.R32(fid), 0u, 0u, 0u, vertexGxp, new Ptr<uint>(fp)));
            uint verts = h.Alloc(48), idx = h.Alloc(8);
            float[] triangle = [-1, -1, 0.5f, 1, 3, -1, 0.5f, 1, -1, 3, 0.5f, 1]; // the whole surface
            for (int i = 0; i < triangle.Length; i++)
            {
                GuestMemory.Write(verts + (uint)i * 4, triangle[i]);
            }

            GuestMemory.Write(idx + 2, (ushort)1);
            GuestMemory.Write(idx + 4, (ushort)2);

            var targets = new Dictionary<(uint, uint), uint>();
            var cases = new List<Case>();
            var rng = new Random(20261123);
            foreach ((string name, TexBase texBase, uint swizzle) in s_formats)
            {
                Model? model = Parse(name, texBase);
                if (model == null)
                {
                    uncovered.Add(name);
                    continue;
                }

                uint format = (uint)texBase | (swizzle << 12);
                foreach ((Layout layout, uint width, uint height) in s_layouts)
                {
                    uint data = h.Alloc(TextureBytes(layout, width, height, model.TexelBits) + 256);
                    uint palette = model.IndexBits != 0 ? (h.Alloc(256 * 4 + 64) + 63) & ~63u : 0;
                    double?[] expected = Fill(model, layout, width, height, data, palette, rng);
                    uint texture = h.Alloc(16);
                    int r = layout switch
                    {
                        Layout.Strided => Call(g, "sceGxmTextureInitLinearStrided", texture, data, format, width, height, StridedBytes),
                        Layout.Swizzled => Call(g, "sceGxmTextureInitSwizzled", texture, data, format, width, height, 0u),
                        Layout.Tiled => Call(g, "sceGxmTextureInitTiled", texture, data, format, width, height, 0u),
                        _ => Call(g, "sceGxmTextureInitLinear", texture, data, format, width, height, 0u),
                    };
                    if (r != 0)
                    {
                        failures.AppendLine($"{name} {layout}: texture init 0x{r:X8}");
                        continue;
                    }

                    if (palette != 0)
                    {
                        Assert.Equal(0, Call(g, "sceGxmTextureSetPalette", texture, palette));
                    }

                    // a colour surface per case (F16F16F16F16_ABGR, the fetched values nearly unrounded) on a target
                    // of twice the texture's size
                    uint surfaceWidth = width * 2, surfaceHeight = height * 2;
                    if (!targets.TryGetValue((surfaceWidth, surfaceHeight), out uint rt))
                    {
                        uint rp = h.Alloc(0x14);
                        GuestMemory.Write(rp + 4, (ushort)surfaceWidth);
                        GuestMemory.Write(rp + 6, (ushort)surfaceHeight);
                        GuestMemory.Write(rp + 8, (ushort)1);
                        Assert.Equal(0, Call(g, "sceGxmCreateRenderTarget", rp, new Ptr<uint>(o)));
                        rt = GuestMemory.R32(o);
                        targets[(surfaceWidth, surfaceHeight)] = rt;
                    }

                    uint stride = (surfaceWidth + 7) & ~7u;
                    uint surfaceData = h.Alloc(stride * surfaceHeight * 8);
                    uint surface = h.Alloc(0x40);
                    Assert.Equal(0, Call(g, "sceGxmColorSurfaceInit", surface, 0x01000000u, 0u, 0u, 0u, surfaceWidth, surfaceHeight, stride, surfaceData));
                    Assert.Equal(0, Call(g, "sceGxmBeginScene", ctx, 0u, rt, 0u, 0u, 0u, surface, 0u));
                    Invoke(g, "sceGxmSetVertexProgram", ctx, GuestMemory.R32(vp));
                    Invoke(g, "sceGxmSetFragmentProgram", ctx, GuestMemory.R32(fp));
                    Invoke(g, "sceGxmSetFrontDepthFunc", ctx, 0x00E00000u); // ALWAYS
                    Invoke(g, "sceGxmSetCullMode", ctx, 0u);
                    Assert.Equal(0, Call(g, "sceGxmSetVertexStream", ctx, 0u, verts));
                    Assert.Equal(0, Call(g, "sceGxmSetFragmentTexture", ctx, 3u, texture));
                    Assert.Equal(0, Call(g, "sceGxmDraw", ctx, 0u, 0u, idx, 3u));
                    Assert.Equal(0, Call(g, "sceGxmEndScene", ctx, 0u, 0u));
                    cases.Add(new Case { Name = name, Layout = layout, Width = width, Height = height, Surface = stride, SurfaceData = surfaceData, Expected = expected });
                }
            }

            Assert.Equal(0, Call(g, "sceGxmFinish", ctx));
            h.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(cases[0].SurfaceData, 8, 8, 8, DisplayPixelFormat.A8B8G8R8) });
            Assert.Equal(0, Call(g, "sceGxmFinish", ctx));
            h.Emu.Gpu.WaitIdle();

            // the fetched texels: every pixel of the 2x2 repetitions
            var failedCases = new HashSet<string>();
            var divergentNames = new HashSet<string>();
            foreach (Case c in cases)
            {
                string label = $"{c.Name} {c.Layout}";
                // the quadrant whose coordinates fall in [0, 1): x from the middle, y from the top (the viewport
                // has y up), clamped texture coordinates elsewhere
                for (uint y = 0; y < c.Height && !failedCases.Contains(label); y++)
                {
                    for (uint x = c.Width; x < c.Width * 2 && !failedCases.Contains(label); x++)
                    {
                        uint texelX = x - c.Width, texelY = c.Height - 1 - y;
                        uint texel = texelY * c.Width + texelX;
                        uint pixel = c.SurfaceData + (y * c.Surface + x) * 8;
                        for (int ch = 0; ch < 4; ch++)
                        {
                            if (c.Expected[texel * 4 + ch] is not { } want)
                            {
                                continue;
                            }

                            double got = (double)BitConverter.UInt16BitsToHalf(GuestMemory.Read<ushort>(pixel + (uint)ch * 2));
                            compared++;
                            if (!Close(want, got))
                            {
                                failedCases.Add(label);
                                divergentNames.Add(c.Name);
                                (s_knownDivergent.Contains(c.Name) ? known : failures).AppendLine($"{label}: texel ({texelX},{texelY}) {"RGBA"[ch]} = {got:G5}, the format gives {want:G5}");
                                break;
                            }
                        }
                    }
                }
            }

            foreach (string name in s_knownDivergent)
            {
                if (!divergentNames.Contains(name))
                {
                    failures.AppendLine($"{name}: matches the table now, remove it from the known divergences");
                }
            }

            Call(g, "sceGxmTerminate");
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }

        Assert.True(failures.Length == 0, $"divergences ({failures.ToString().Split('\n').Length - 1}):\n{failures}\nknown:\n{known}\nnot in this block: {string.Join(' ', uncovered)}");
        Assert.True(compared > 10_000, $"only {compared} components compared");
    }

    /// <summary>The table's model reads the gxm.h names as their definitions do (no renderer involved).</summary>
    [Fact]
    public void TheReferenceModelReadsTheNamesOfTheLegacyFormats()
    {
        // gxm.h legacy names: A8 = U8_R000 (alpha only), L8 = U8_1RRR (luminance, opaque), A8L8 = U8U8_GRRR
        Model a8 = Parse("U8_R000", TexBase.U8)!;
        Assert.Equal([Zero, Zero, Zero, 0], a8.Channels);
        Model l8 = Parse("U8_1RRR", TexBase.U8)!;
        Assert.Equal([0, 0, 0, One], l8.Channels);
        Model a8l8 = Parse("U8U8_GRRR", TexBase.U8U8)!;
        Assert.Equal([1, 1, 1, 0], a8l8.Channels); // fields: G (high byte) = 0, R (low byte) = 1
        // A1R5G5B5 = U1U5U5U5_ARGB: alpha in bit 15, blue in the low bits
        Model argb1555 = Parse("U1U5U5U5_ARGB", TexBase.U1U5U5U5)!;
        Assert.Equal([1, 2, 3, 0], argb1555.Channels);
        Assert.Equal(16, argb1555.Bits);
        // X8U8S8S8_1RGB: red unsigned in bits 16-23, green and blue signed
        Model x8u8s8s8 = Parse("X8U8S8S8_1RGB", TexBase.X8S8S8U8)!;
        Assert.Equal(Kind.Unorm, x8u8s8s8.Fields[x8u8s8s8.Channels[0]].Kind);
        Assert.Equal(Kind.Snorm, x8u8s8s8.Fields[x8u8s8s8.Channels[2]].Kind);
        Assert.Equal(One, x8u8s8s8.Channels[3]);
    }
}
