using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// YUV textures (YUV420P2 and YUV420P3, the movie players' frames, and YUV422) converted to RGBA. The row conversion
/// (one read of the planes, table terms) gives byte for byte what the per-texel conversion gives
/// (TextureDecoder.Yuv420Texel), for both plane layouts, both chroma orders, the four colour space converter profiles,
/// odd sizes and padded pitches; a movie texture is converted every frame, and F1 2011's intro movie on the phone cost
/// the GPU worker ~38 ms per frame through the per-texel path. The matrix is the one of the converter the format names
/// (CSC0 / CSC1), with the profile the scene latched from sceGxmSetYuvProfile (libgxm 3.74).
/// </summary>
public sealed class YuvTextureTests
{
    private const uint Base = 0x81000000;

    [Theory]
    [InlineData(960u, 544u, 960u, false, false, 0)]
    [InlineData(960u, 544u, 1024u, false, true, 1)]
    [InlineData(960u, 544u, 960u, true, false, 2)]
    [InlineData(33u, 17u, 40u, false, true, 3)]
    [InlineData(33u, 17u, 34u, true, true, 1)]
    [InlineData(1u, 1u, 8u, false, false, 0)]
    [InlineData(2u, 3u, 2u, true, true, 3)]
    [InlineData(64u, 8u, 64u, true, false, 1)]
    public void RowConversionGivesTheSameBytesAsThePerTexelOne(uint width, uint height, uint pitch, bool threePlane, bool yvu, int profile)
    {
        var rng = new Random((int)(width * 31 + height * 7 + pitch + (uint)profile * 3 + (yvu ? 500u : 0u) + (threePlane ? 1000u : 0u)));
        // luma plus the larger of the two chroma layouts, with slack after it
        byte[] planes = new byte[pitch * height * 2 + 64];
        rng.NextBytes(planes);
        using (TextureDecoder.UseSource(Base, planes))
        {
            using Texels rows = TextureDecoder.Yuv420(Base, width, height, pitch, threePlane, yvu, profile);
            using Texels texels = TextureDecoder.Yuv420Texel(Base, width, height, pitch, threePlane, yvu, profile);
            Assert.Equal(texels.Span.ToArray(), rows.Span.ToArray());
        }
    }

    [Fact]
    public void ExtremeSamplesClampLikeThePerTexelConversion()
    {
        // every combination of 0 and 255 in Y, U and V, in every profile: the clamping at both ends
        const uint Width = 8, Height = 2, Pitch = 8;
        byte[] planes = new byte[Pitch * Height * 2];
        for (int i = 0; i < planes.Length; i++)
        {
            planes[i] = (byte)(((i * 0x9E37) >> 3 & 1) == 0 ? 0 : 255);
        }

        using (TextureDecoder.UseSource(Base, planes))
        {
            for (int profile = 0; profile < 4; profile++)
            {
                using Texels rows = TextureDecoder.Yuv420(Base, Width, Height, Pitch, false, false, profile);
                using Texels texels = TextureDecoder.Yuv420Texel(Base, Width, Height, Pitch, false, false, profile);
                Assert.Equal(texels.Span.ToArray(), rows.Span.ToArray());
            }
        }
    }

    /// <summary>The four SceGxmYuvProfile matrices in 8 fraction bits. BT.601 standard and full range are the terms
    /// the decoder always had (the same bytes as before for them); the BT.709 ones carry its Kr/Kb, as libgxm 3.74's
    /// table does (in 9 fraction bits there: luma 1.164 / 1, B from U 2.113 / 1.855, R from V 1.793 / 1.574).</summary>
    [Theory]
    [InlineData(0, 298, 16, 409, 100, 208, 516)]
    [InlineData(1, 298, 16, 459, 55, 136, 541)]
    [InlineData(2, 256, 0, 359, 88, 183, 454)]
    [InlineData(3, 256, 0, 403, 48, 120, 475)]
    public void EachProfileHasItsStandardsMatrix(int profile, int luma, int lumaOffset, int rFromV, int gFromU, int gFromV, int bFromU)
    {
        Assert.Equal((luma, lumaOffset, rFromV, gFromU, gFromV, bFromU), GxmYuv.Coefficients(profile));
    }

    /// <summary>The converter each YUV format names: YUV420 swizzle bit 1, YUV422 swizzle bit 2.</summary>
    [Theory]
    [InlineData(0x90000000u, 0)]
    [InlineData(0x90001000u, 0)]
    [InlineData(0x90002000u, 1)]
    [InlineData(0x91003000u, 1)]
    [InlineData(0x92000000u, 0)]
    [InlineData(0x92003000u, 0)]
    [InlineData(0x92004000u, 1)]
    [InlineData(0x92007000u, 1)]
    [InlineData(0x0C000000u, -1)]
    public void EachYuvFormatNamesItsConverter(uint format, int csc)
    {
        Assert.Equal(csc, GxmYuv.CscOf(format));
        var profiles = new GxmYuvProfiles(GxmYuv.Bt709FullRange, GxmYuv.Bt601FullRange);
        int expected = csc switch
        {
            0 => GxmYuv.Bt709FullRange,
            1 => GxmYuv.Bt601FullRange,
            _ => 0,
        };
        Assert.Equal(expected, profiles.ProfileOf(format));
    }

    /// <summary>A YUV420P2 texture of BT.709 pure red (Y 63, Cb 102, Cr 240, standard range) through
    /// TextureDecoder.Decode: CSC1 with the context's defaults is BT.709 standard and gives red back; CSC0 is BT.601
    /// standard and gives the BT.601 reading of the same samples; a scene whose CSC1 is BT.601 full range reads them as
    /// that. Before, CSC1 was always BT.601 full range and CSC0 BT.601 standard, whatever the scene said.</summary>
    [Fact]
    public void DecodeConvertsWithTheProfileOfTheTexturesConverter()
    {
        const uint Width = 4, Height = 2;
        byte[] planes = new byte[Width * Height * 3 / 2];
        Array.Fill(planes, (byte)63, 0, (int)(Width * Height));
        for (int i = (int)(Width * Height); i < planes.Length; i += 2)
        {
            planes[i] = 102;
            planes[i + 1] = 240;
        }

        byte[] Pixel(uint swizzle, GxmYuvProfiles profiles)
        {
            // a linear YUV420P2 texture: base 0x90000000 (word 0 bit 31, word 1 bits 24-28), swizzle in word 3
            var desc = new GxmTextureDesc(0x80000000, 0x60000000 | (0x10u << 24) | ((Width - 1) << 12) | (Height - 1), Base, swizzle << 28);
            TexFormatInfo info = Formats.Texture(GxmFormats.BaseOf(desc.Format));
            using (TextureDecoder.UseSource(Base, planes))
            {
                using Texels texels = TextureDecoder.Decode(desc, info, Base, Width, Height, Width, profiles, out _, out _);
                return texels.Span[..4].ToArray();
            }
        }

        Assert.Equal(new byte[] { 255, 1, 0, 255 }, Pixel(2, GxmYuvProfiles.Default));
        Assert.Equal(new byte[] { 234, 0, 2, 255 }, Pixel(0, GxmYuvProfiles.Default));
        Assert.Equal(new byte[] { 220, 0, 17, 255 }, Pixel(2, new GxmYuvProfiles(GxmYuv.Bt601Standard, GxmYuv.Bt601FullRange)));
        Assert.Equal(new byte[] { 255, 1, 0, 255 }, Pixel(0, new GxmYuvProfiles(GxmYuv.Bt709Standard, GxmYuv.Bt601FullRange)));
        // YVU420P2_CSC1: the same samples with V first
        for (int i = (int)(Width * Height); i < planes.Length; i += 2)
        {
            (planes[i], planes[i + 1]) = (planes[i + 1], planes[i]);
        }

        Assert.Equal(new byte[] { 255, 1, 0, 255 }, Pixel(3, GxmYuvProfiles.Default));
        Assert.Equal(new byte[] { 234, 0, 2, 255 }, Pixel(1, GxmYuvProfiles.Default));
    }

    /// <summary>The four YUV422 orders (YUYV, YVYU, UYVY, VYUY) of the same two texels give the same RGBA; the CSC1
    /// orders convert with the scene's CSC1 profile.</summary>
    [Fact]
    public void Yuv422ReadsEachOrderAndItsConverter()
    {
        const byte Y0 = 81, Y1 = 145, U = 90, V = 240;
        byte[][] groups =
        [
            [Y0, U, Y1, V],
            [Y0, V, Y1, U],
            [U, Y0, V, Y1],
            [V, Y0, U, Y1],
        ];
        byte[]? expected = null;
        for (uint order = 0; order < 4; order++)
        {
            using (TextureDecoder.UseSource(Base, groups[order]))
            {
                using Texels texels = TextureDecoder.Yuv422(Base, 2, 1, 4, order, GxmYuv.Bt601Standard);
                expected ??= texels.Span.ToArray();
                Assert.Equal(expected, texels.Span.ToArray());
            }
        }

        // Y 81, U 90, V 240 is BT.601 standard red; Y 145 with the same chroma (255, 74, 74)
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 74, 74, 255 }, expected);
        // YUYV422_CSC1 (swizzle 4) in a scene whose CSC1 is BT.601 standard and CSC0 BT.709
        var desc = new GxmTextureDesc(0x80000000, 0x60000000 | (0x12u << 24) | (1u << 12), Base, 4u << 28);
        using (TextureDecoder.UseSource(Base, groups[0]))
        {
            using Texels csc1 = TextureDecoder.Decode(desc, Formats.Texture(TexBase.YUV422), Base, 2, 1, 4,
                new GxmYuvProfiles(GxmYuv.Bt709Standard, GxmYuv.Bt601Standard), out _, out _);
            Assert.Equal(expected, csc1.Span.ToArray());
        }
    }
}
