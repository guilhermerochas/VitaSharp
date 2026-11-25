using Silk.NET.Vulkan;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The renderer's GXM-to-Vulkan tables (<see cref="Formats"/>) against the enumerations of vita-headers
/// (include/psp2/gxm.h): texture swizzles (SCE_GXM_TEXTURE_SWIZZLE1-4, named from the most significant component, so
/// SWIZZLE4_ABGR is (R, G, B, A) in Vulkan order), colour base formats and swizzles, attribute formats, blend factors
/// and functions, colour masks, primitive types, polygon modes and texture address modes.
/// </summary>
public sealed class GxmFormatMappingTests
{
    private const ComponentSwizzle R = ComponentSwizzle.R, G = ComponentSwizzle.G, B = ComponentSwizzle.B, A = ComponentSwizzle.A,
        Z = ComponentSwizzle.Zero, O = ComponentSwizzle.One, I = ComponentSwizzle.Identity;

    private static ComponentMapping M(ComponentSwizzle r, ComponentSwizzle g, ComponentSwizzle b, ComponentSwizzle a) => new(r, g, b, a);

    private static void Same(ComponentMapping expected, ComponentMapping actual, string what)
    {
        Assert.True(expected.R == actual.R && expected.G == actual.G && expected.B == actual.B && expected.A == actual.A,
            $"{what}: expected ({expected.R}, {expected.G}, {expected.B}, {expected.A}), got ({actual.R}, {actual.G}, {actual.B}, {actual.A})");
    }

    private static ComponentMapping Texture(TexBase texBase, uint swizzle) => Formats.TextureSwizzle((uint)texBase | swizzle << 12);

    [Fact]
    public void FourComponentTextureSwizzlesAreNamedFromTheTopComponent()
    {
        // SWIZZLE4_ABGR .. BGR1 (0x0000 .. 0x7000): ABGR = (R, G, B, A); a '1' replaces the named component by one
        ComponentMapping[] expected =
        [
            M(R, G, B, A), M(B, G, R, A), M(A, B, G, R), M(G, B, A, R),
            M(R, G, B, O), M(B, G, R, O), M(A, B, G, O), M(G, B, A, O),
        ];
        foreach (TexBase f in new[] { TexBase.U8U8U8U8, TexBase.S8S8S8S8, TexBase.U16U16U16U16, TexBase.F16F16F16F16, TexBase.U2U10U10U10 })
        {
            for (uint s = 0; s < 8; s++)
            {
                Same(expected[s], Texture(f, s), $"{f} swizzle {s}");
            }
        }
    }

    [Fact]
    public void TwoComponentTextureSwizzles()
    {
        // SWIZZLE2_GR, 00GR, GRRR, RGGG, GRGR, 00RG (0x0000 .. 0x5000)
        ComponentMapping[] expected = [M(R, G, Z, O), M(R, G, Z, Z), M(R, R, R, G), M(G, G, G, R), M(R, G, R, G), M(G, R, Z, Z)];
        foreach (TexBase f in new[] { TexBase.U8U8, TexBase.S8S8, TexBase.U16U16, TexBase.S16S16, TexBase.F16F16, TexBase.F32F32, TexBase.UBC5, TexBase.SBC5 })
        {
            for (uint s = 0; s < expected.Length; s++)
            {
                Same(expected[s], Texture(f, s), $"{f} swizzle {s}");
            }
        }
    }

    [Fact]
    public void OneComponentTextureSwizzles()
    {
        // SWIZZLE1_R, 000R, 111R, RRRR, 0RRR, 1RRR, R000, R111 (0x0000 .. 0x7000), read from the top component like
        // SWIZZLE2/SWIZZLE4 by default; VITASHARP_SWZ1=B selects the RGBA-order reading kept for comparison
        bool rgbaOrder = Environment.GetEnvironmentVariable(EnvVars.Swz1) == "B";
        ComponentMapping[] expected = rgbaOrder
            ? [M(R, Z, Z, O), M(Z, Z, Z, R), M(O, O, O, R), M(R, R, R, R), M(Z, R, R, R), M(O, R, R, R), M(R, Z, Z, Z), M(R, O, O, O)]
            : [M(R, Z, Z, O), M(R, Z, Z, Z), M(R, O, O, O), M(R, R, R, R), M(R, R, R, Z), M(R, R, R, O), M(Z, Z, Z, R), M(O, O, O, R)];
        foreach (TexBase f in new[] { TexBase.U8, TexBase.S8, TexBase.U16, TexBase.F16, TexBase.F32, TexBase.UBC4 })
        {
            for (uint s = 0; s < 8; s++)
            {
                Same(expected[s], Texture(f, s), $"{f} swizzle {s}");
            }
        }
    }

    [Fact]
    public void PackedTextureFormatSwizzles()
    {
        // SWIZZLE3_BGR / RGB: U5U6U5 is uploaded as R5G6B5 (R in the top bits), so its BGR (B on top) swaps R and B
        Same(M(B, G, R, O), Texture(TexBase.U5U6U5, 0), "U5U6U5_BGR");
        Same(M(R, G, B, O), Texture(TexBase.U5U6U5, 1), "U5U6U5_RGB");
        Same(M(R, G, B, O), Texture(TexBase.U8U8U8, 0), "U8U8U8_BGR");
        Same(M(B, G, R, O), Texture(TexBase.U8U8U8, 1), "U8U8U8_RGB");
        Same(M(R, G, B, O), Texture(TexBase.F11F11F10, 0), "F11F11F10_BGR");
        // U4U4U4U4 is uploaded as R4G4B4A4 (R in the top nibble)
        ComponentMapping[] u4 = [M(A, B, G, R), M(G, B, A, R), M(R, G, B, A), M(B, G, R, A)];
        for (uint s = 0; s < u4.Length; s++)
        {
            Same(u4[s], Texture(TexBase.U4U4U4U4, s), $"U4U4U4U4 swizzle {s}");
        }

        // X4U4U4U4_1BGR / _1RGB and U4U4U4X4_RGB1 / _BGR1: the colour nibbles as their ABGR / ARGB / RGBA / BGRA siblings, alpha 1
        ComponentMapping[] u4x = [M(A, B, G, O), M(G, B, A, O), M(R, G, B, O), M(B, G, R, O)];
        for (uint s = 0; s < u4x.Length; s++)
        {
            Same(u4x[s], Texture(TexBase.U4U4U4U4, s + 4), $"U4U4U4U4 swizzle {s + 4}");
        }

        // U1U5U5U5 is uploaded as A1R5G5B5: ABGR and BGRA swap R and B, the 1-variants have alpha one
        ComponentMapping[] u1 = [M(B, G, R, A), M(R, G, B, A), M(R, G, B, A), M(B, G, R, A), M(B, G, R, O), M(R, G, B, O), M(R, G, B, O), M(B, G, R, O)];
        for (uint s = 0; s < u1.Length; s++)
        {
            Same(u1[s], Texture(TexBase.U1U5U5U5, s), $"U1U5U5U5 swizzle {s}");
        }

        // YUV is converted to RGBA on upload and X8U24 depth is read as is
        Same(M(I, I, I, I), Texture(TexBase.YUV420P2, 0), "YUV420P2");
        Same(M(I, I, I, I), Texture(TexBase.YUV422, 1), "YUV422");
        Same(M(I, I, I, I), Texture(TexBase.X8U24, 0), "X8U24");
    }

    [Fact]
    public void TextureBaseFormatsUploadWithTheirSizeAndConversion()
    {
        Assert.Equal(new TexFormatInfo(Format.R8Unorm, 8, 1, 1, 0, TexConvert.None), Formats.Texture(TexBase.U8));
        Assert.Equal(new TexFormatInfo(Format.R16G16B16A16Sfloat, 64, 1, 1, 0, TexConvert.None), Formats.Texture(TexBase.F16F16F16F16));
        // 24-bit RGB and the formats Vulkan has no layout for are expanded on the CPU
        Assert.Equal(TexConvert.Rgb8ToRgba8, Formats.Texture(TexBase.U8U8U8).Convert);
        Assert.Equal(24, Formats.Texture(TexBase.U8U8U8).BitsPerPixel);
        Assert.Equal(TexConvert.U8U3U3U2, Formats.Texture(TexBase.U8U3U3U2).Convert);
        Assert.Equal(TexConvert.S5S5U6, Formats.Texture(TexBase.S5S5U6).Convert);
        Assert.Equal(TexConvert.Palette4, Formats.Texture(TexBase.P4).Convert);
        Assert.Equal(4, Formats.Texture(TexBase.P4).BitsPerPixel);
        Assert.Equal(TexConvert.X8U24, Formats.Texture(TexBase.X8U24).Convert);
        // 32-bit integer texels are kept bit for bit in a float image (sampled through a float sampler)
        Assert.Equal(Format.R32Sfloat, Formats.Texture(TexBase.U32).Format);
        Assert.Equal(Format.R32G32Sfloat, Formats.Texture(TexBase.U32U32).Format);
        // block formats: 4x4 blocks, PVRTC 2bpp is 8x4
        TexFormatInfo bc1 = Formats.Texture(TexBase.UBC1);
        Assert.True(bc1.Compressed);
        Assert.Equal((4, 4, 8), (bc1.BlockW, bc1.BlockH, bc1.BlockBytes));
        Assert.Equal((4, 4, 16), (Formats.Texture(TexBase.UBC3).BlockW, Formats.Texture(TexBase.UBC3).BlockH, Formats.Texture(TexBase.UBC3).BlockBytes));
        Assert.Equal(Format.BC4SNormBlock, Formats.Texture(TexBase.SBC4).Format);
        TexFormatInfo pvrtc2 = Formats.Texture(TexBase.PVRTII2BPP);
        Assert.Equal((8, 4, 8, TexConvert.Pvrtc), (pvrtc2.BlockW, pvrtc2.BlockH, pvrtc2.BlockBytes, pvrtc2.Convert));
        Assert.Equal((4, 4), (Formats.Texture(TexBase.PVRT4BPP).BlockW, Formats.Texture(TexBase.PVRT4BPP).BlockH));
        Assert.Equal(TexConvert.Yuv420P3, Formats.Texture(TexBase.YUV420P3).Convert);
        Assert.Equal(12, Formats.Texture(TexBase.YUV420P3).BitsPerPixel);
        Assert.False(Formats.Texture(TexBase.YUV422).Compressed);
        // a base format the table does not know is read as RGBA8 (and warned about once)
        Assert.Equal(new TexFormatInfo(Format.R8G8B8A8Unorm, 32, 1, 1, 0, TexConvert.None), Formats.Texture((TexBase)0x7F000000));
    }

    [Fact]
    public void ColourBaseFormatsMapToTheirVulkanFormatAndSize()
    {
        (uint Base, Format Format, int Bytes)[] table =
        [
            (0x00000000, Format.R8G8B8A8Unorm, 4), // U8U8U8U8
            (0x10000000, Format.R8G8B8A8Unorm, 3), // U8U8U8
            (0x30000000, Format.R5G6B5UnormPack16, 2), // U5U6U5
            (0x40000000, Format.A1R5G5B5UnormPack16, 2), // U1U5U5U5
            (0x50000000, Format.R4G4B4A4UnormPack16, 2), // U4U4U4U4
            (0xF0000000, Format.R16Sfloat, 2), // F16
            (0x00800000, Format.R16G16Sfloat, 4), // F16F16
            (0x10800000, Format.R32Sfloat, 4), // F32
            (0x20800000, Format.R16SNorm, 2), // S16
            (0x30800000, Format.R16G16SNorm, 4), // S16S16
            (0x40800000, Format.R16Unorm, 2), // U16
            (0x50800000, Format.R16G16Unorm, 4), // U16U16
            (0x60800000, Format.A2B10G10R10UnormPack32, 4), // U2U10U10U10
            (0x80800000, Format.R8Unorm, 1), // U8
            (0x90800000, Format.R8SNorm, 1), // S8
            (0xB0800000, Format.R8G8Unorm, 2), // U8U8
            (0xC0800000, Format.R8G8SNorm, 2), // S8S8
            (0xE0800000, Format.R8G8B8A8SNorm, 4), // S8S8S8S8
            (0x01000000, Format.R16G16B16A16Sfloat, 8), // F16F16F16F16
            (0x11000000, Format.R32G32Sfloat, 8), // F32F32
            (0x21000000, Format.B10G11R11UfloatPack32, 4), // F11F11F10
            (0x31000000, Format.E5B9G9R9UfloatPack32, 4), // SE5M9M9M9
            (0x41000000, Format.R16G16B16A16Sfloat, 4), // U2F10F10F10
        ];
        foreach ((uint b, Format f, int bytes) in table)
        {
            for (uint s = 0; s < 4; s++)
            {
                uint format = b | s << 20;
                Assert.Equal(f, Formats.ColorFormat(format));
                Assert.Equal(bytes, Formats.ColorBytesPerPixel(format));
            }
        }

        // U8U3U3U2 and S5S5U6 are 16-bit surfaces
        Assert.Equal(2, Formats.ColorBytesPerPixel(0x60000000));
        Assert.Equal(2, Formats.ColorBytesPerPixel(0xA0800000));
    }

    [Fact]
    public void FourComponentColourSurfacesFollowTheirSwizzle()
    {
        // SCE_GXM_COLOR_SWIZZLE4_ABGR/ARGB/RGBA/BGRA (bits 20-21) on the four-component bases
        ComponentMapping[] expected = [M(R, G, B, A), M(B, G, R, A), M(A, B, G, R), M(G, B, A, R)];
        foreach (uint b in new uint[] { 0x00000000, 0xE0800000, 0x01000000, 0x60800000, 0x41000000 })
        {
            for (uint s = 0; s < 4; s++)
            {
                Same(expected[s], Formats.ColorSwizzle(b | s << 20), $"colour 0x{b:X8} swizzle {s}");
            }
        }

        Same(M(I, I, I, I), Formats.ColorSwizzle(0x80800000 | 1u << 20), "U8_A");
        Same(M(I, I, I, I), Formats.ColorSwizzle(0x30000000), "U5U6U5_BGR");
    }

    [Fact]
    public void OnlyRgbaAndBgraU8SurfacesRenderInLogicalOrder()
    {
        Assert.Equal(0u, Formats.LogicalOrderSwizzle(0x00000000)); // ABGR: memory order already
        Assert.Equal(0u, Formats.LogicalOrderSwizzle(0x00100000)); // ARGB: a B8G8R8A8 image
        Assert.Equal(2u, Formats.LogicalOrderSwizzle(0x00200000));
        Assert.Equal(3u, Formats.LogicalOrderSwizzle(0x00300000));
        Assert.Equal(0u, Formats.LogicalOrderSwizzle(0xE0800000 | 2u << 20)); // S8S8S8S8_RGBA is not U8
        // the view that shows such an image in the memory byte order: RGBA is bytes A,B,G,R; BGRA is bytes A,R,G,B
        Same(M(A, B, G, R), Formats.SurfaceMemoryOrder(0x00200000), "RGBA");
        Same(M(A, R, G, B), Formats.SurfaceMemoryOrder(0x00300000), "BGRA");
        Same(M(I, I, I, I), Formats.SurfaceMemoryOrder(0x00000000), "ABGR");
    }

    [Fact]
    public void ComposeAppliesTheOuterMappingThroughTheInnerOne()
    {
        ComponentMapping identity = M(I, I, I, I);
        ComponentMapping swap = M(B, G, R, A);
        Same(swap, Formats.Compose(identity, swap), "identity then swap");
        Same(swap, Formats.Compose(swap, identity), "swap then identity");
        // swapping R and B twice is the identity
        Same(M(R, G, B, A), Formats.Compose(swap, swap), "swap twice");
        // RGBA memory order seen through the ABGR texture swizzle reverses the bytes again
        Same(M(R, G, B, A), Formats.Compose(M(A, B, G, R), M(A, B, G, R)), "reverse twice");
        // constants of the outer mapping stay constants
        Same(M(B, Z, O, A), Formats.Compose(swap, M(R, Z, O, A)), "constants");
    }

    [Fact]
    public void SrgbVariantsOfTheEightBitFormats()
    {
        Assert.Equal(Format.R8G8B8A8Srgb, Formats.Srgb(Format.R8G8B8A8Unorm));
        Assert.Equal(Format.B8G8R8A8Srgb, Formats.Srgb(Format.B8G8R8A8Unorm));
        Assert.Equal(Format.A8B8G8R8SrgbPack32, Formats.Srgb(Format.A8B8G8R8UnormPack32));
        Assert.Equal(Format.R8G8B8Srgb, Formats.Srgb(Format.R8G8B8Unorm));
        Assert.Equal(Format.R8G8Srgb, Formats.Srgb(Format.R8G8Unorm));
        Assert.Equal(Format.R8Srgb, Formats.Srgb(Format.R8Unorm));
        Assert.Equal(Format.BC1RgbaSrgbBlock, Formats.Srgb(Format.BC1RgbaUnormBlock));
        Assert.Equal(Format.BC1RgbSrgbBlock, Formats.Srgb(Format.BC1RgbUnormBlock));
        Assert.Equal(Format.BC2SrgbBlock, Formats.Srgb(Format.BC2UnormBlock));
        Assert.Equal(Format.BC3SrgbBlock, Formats.Srgb(Format.BC3UnormBlock));
        Assert.Equal(Format.BC7SrgbBlock, Formats.Srgb(Format.BC7UnormBlock));
        // no sRGB variant: the format itself
        Assert.Equal(Format.R16G16B16A16Sfloat, Formats.Srgb(Format.R16G16B16A16Sfloat));
        Assert.Equal(Format.BC4UnormBlock, Formats.Srgb(Format.BC4UnormBlock));
    }

    [Fact]
    public void AttributeFormatsFollowSceGxmAttributeFormat()
    {
        // U8, S8, U16, S16 (integer or scaled), U8N, S8N, U16N, S16N (normalised), F16, F32, UNTYPED
        Assert.Equal((Format.R8G8B8A8Uscaled, 4), Formats.Attribute(0, 4, 'f'));
        Assert.Equal((Format.R8G8B8A8Uint, 4), Formats.Attribute(0, 4, 'u'));
        Assert.Equal((Format.R8G8Sscaled, 2), Formats.Attribute(1, 2, 'f'));
        Assert.Equal((Format.R16G16B16Uscaled, 6), Formats.Attribute(2, 3, 'f'));
        Assert.Equal((Format.R16Sint, 2), Formats.Attribute(3, 1, 'i'));
        Assert.Equal((Format.R8G8B8A8Unorm, 4), Formats.Attribute(4, 4, 'f'));
        Assert.Equal((Format.R8G8B8SNorm, 3), Formats.Attribute(5, 3, 'f'));
        Assert.Equal((Format.R16G16Unorm, 4), Formats.Attribute(6, 2, 'f'));
        Assert.Equal((Format.R16G16B16A16SNorm, 8), Formats.Attribute(7, 4, 'f'));
        Assert.Equal((Format.R16G16Sfloat, 4), Formats.Attribute(8, 2, 'f'));
        Assert.Equal((Format.R32G32B32Sfloat, 12), Formats.Attribute(9, 3, 'f'));
        Assert.Equal((Format.R32Uint, 4), Formats.Attribute(9, 1, 'u'));
        Assert.Equal((Format.R32G32Sint, 8), Formats.Attribute(9, 2, 'i'));
        // UNTYPED: 32-bit words read as the shader's register type
        Assert.Equal((Format.R32G32B32A32Uint, 16), Formats.Attribute(10, 4, 'u'));
        Assert.Equal((Format.R32Sfloat, 4), Formats.Attribute(10, 1, 'f'));
        Assert.Equal((Format.R32G32Sint, 8), Formats.Attribute(10, 2, 'i'));
        // component counts outside 1..4 are clamped
        Assert.Equal((Format.R8G8B8A8Unorm, 4), Formats.Attribute(4, 7, 'f'));
        Assert.Equal((Format.R8Unorm, 1), Formats.Attribute(4, 0, 'f'));
        var all = Formats.AllAttributeFormats().ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Contains(Format.R32G32B32A32Sfloat, all);
        Assert.Contains(Format.R16G16B16A16SNorm, all);
    }

    [Fact]
    public void BlendFactorsAndFunctionsFollowGxmH()
    {
        BlendFactor[] factors =
        [
            BlendFactor.Zero, BlendFactor.One, BlendFactor.SrcColor, BlendFactor.OneMinusSrcColor, BlendFactor.SrcAlpha,
            BlendFactor.OneMinusSrcAlpha, BlendFactor.DstColor, BlendFactor.OneMinusDstColor, BlendFactor.DstAlpha,
            BlendFactor.OneMinusDstAlpha, BlendFactor.SrcAlphaSaturate,
        ];
        for (byte f = 0; f < factors.Length; f++)
        {
            Assert.Equal(factors[f], Formats.Factor(f));
        }

        // DST_ALPHA_SATURATE has no Vulkan factor: the destination alpha is the closest
        Assert.Equal(BlendFactor.DstAlpha, Formats.Factor(11));
        Assert.Equal(BlendFactor.One, Formats.Factor(12));
        // NONE and ADD both add (NONE disables blending elsewhere)
        Assert.Equal(BlendOp.Add, Formats.Op(0));
        Assert.Equal(BlendOp.Add, Formats.Op(1));
        Assert.Equal(BlendOp.Subtract, Formats.Op(2));
        Assert.Equal(BlendOp.ReverseSubtract, Formats.Op(3));
        Assert.Equal(BlendOp.Min, Formats.Op(4));
        Assert.Equal(BlendOp.Max, Formats.Op(5));
    }

    [Fact]
    public void ColourMaskBitsAreARGB()
    {
        // SCE_GXM_COLOR_MASK_A = 1, R = 2, G = 4, B = 8
        Assert.Equal(ColorComponentFlags.ABit, Formats.Mask(1));
        Assert.Equal(ColorComponentFlags.RBit, Formats.Mask(2));
        Assert.Equal(ColorComponentFlags.GBit, Formats.Mask(4));
        Assert.Equal(ColorComponentFlags.BBit, Formats.Mask(8));
        Assert.Equal(ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, Formats.Mask(15));
        Assert.Equal((ColorComponentFlags)0, Formats.Mask(0));
    }

    [Fact]
    public void PrimitivesAndPolygonModesFollowGxmH()
    {
        // SceGxmPrimitiveType >> 26: TRIANGLES, LINES, POINTS, TRIANGLE_STRIP, TRIANGLE_FAN, TRIANGLE_EDGES
        Assert.Equal(PrimitiveTopology.TriangleList, Formats.Topology(0));
        Assert.Equal(PrimitiveTopology.LineList, Formats.Topology(1));
        Assert.Equal(PrimitiveTopology.PointList, Formats.Topology(2));
        Assert.Equal(PrimitiveTopology.TriangleStrip, Formats.Topology(3));
        Assert.Equal(PrimitiveTopology.TriangleFan, Formats.Topology(4));
        Assert.Equal(PrimitiveTopology.TriangleList, Formats.Topology(5));
        // SceGxmPolygonMode >> 15: TRIANGLE_FILL, LINE, POINT_10UV, POINT, POINT_01UV, TRIANGLE_LINE, TRIANGLE_POINT
        Assert.Equal(PolygonMode.Fill, Formats.Polygon(0));
        Assert.Equal(PolygonMode.Line, Formats.Polygon(1));
        Assert.Equal(PolygonMode.Point, Formats.Polygon(2));
        Assert.Equal(PolygonMode.Point, Formats.Polygon(3));
        Assert.Equal(PolygonMode.Point, Formats.Polygon(4));
        Assert.Equal(PolygonMode.Line, Formats.Polygon(5));
        Assert.Equal(PolygonMode.Point, Formats.Polygon(6));
    }

    [Fact]
    public void TextureAddressModesFollowGxmH()
    {
        // REPEAT, MIRROR, CLAMP, MIRROR_CLAMP, REPEAT_IGNORE_BORDER, CLAMP_FULL_BORDER, CLAMP_IGNORE_BORDER, CLAMP_HALF_BORDER
        Assert.Equal(SamplerAddressMode.Repeat, Formats.Address(0));
        Assert.Equal(SamplerAddressMode.MirroredRepeat, Formats.Address(1));
        Assert.Equal(SamplerAddressMode.ClampToEdge, Formats.Address(2));
        Assert.Equal(SamplerAddressMode.Repeat, Formats.Address(4));
        Assert.Equal(SamplerAddressMode.ClampToBorder, Formats.Address(5));
        Assert.Equal(SamplerAddressMode.ClampToBorder, Formats.Address(6));
        Assert.Equal(SamplerAddressMode.ClampToBorder, Formats.Address(7));
        // MIRROR_CLAMP needs the optional samplerMirrorClampToEdge feature (set at device creation), else it is
        // mirrored repeat; the flag is not changed here: renderer tests running in parallel create samplers with it
        SamplerAddressMode mirrorClamp = Formats.MirrorClampToEdgeSupported ? SamplerAddressMode.MirrorClampToEdge : SamplerAddressMode.MirroredRepeat;
        Assert.Equal(mirrorClamp, Formats.Address(3));
    }
}
