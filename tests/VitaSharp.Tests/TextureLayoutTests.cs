using VitaSharp.Core.Gxm;

namespace VitaSharp.Tests;

/// <summary>
/// Face spacing of cube textures (<see cref="TextureLayout.CubeFaceStride"/>): mipmapped faces reserve their whole
/// power-of-two chain and are 2 KB aligned once large enough; single-level and mip-less faces are packed.
/// </summary>
public sealed class TextureLayoutTests
{
    // UBC1: 4 bits per pixel, 4x4 blocks of 8 bytes; UBC3: 8 bits per pixel, 4x4 blocks of 16 bytes
    private static uint Ubc1(uint raw, uint mips, uint size, uint used) => TextureLayout.CubeFaceStride(raw, mips, size, size, 4, true, 4, 4, 8, used);
    private static uint Ubc3(uint raw, uint mips, uint size, uint used) => TextureLayout.CubeFaceStride(raw, mips, size, size, 8, true, 4, 4, 16, used);
    private static uint Rgba8(uint raw, uint mips, uint size, uint used) => TextureLayout.CubeFaceStride(raw, mips, size, size, 32, false, 1, 1, 4, used);

    [Fact]
    public void SingleLevelCompressedFacesArePacked()
    {
        // Unit 13's 32x32 UBC1 ambient cube (raw mip count 0): 512 bytes per face, faces back to back
        Assert.Equal(512u, Ubc1(0, 1, 32, 512));
        Assert.Equal(1024u, Ubc3(0, 1, 32, 1024));
    }

    [Fact]
    public void SingleLevelUncompressedFacesArePacked()
    {
        // Silent Hill: Book of Memories' 32x32 U8U8U8U8 cube (raw mip count 0): faces 4 KB apart
        Assert.Equal(4096u, Rgba8(0, 1, 32, 4096));
        Assert.Equal(64u, Rgba8(0, 1, 4, 64));
    }

    [Fact]
    public void LargeSingleLevelFacesReserveTheChainAndAlign()
    {
        // FIFA 13's 128x128 UBC3 reflection cubes (raw mip count 0): 16384 + the 64x64..1x1 chain (5488) -> 22528
        Assert.Equal(22528u, Ubc3(0, 1, 128, 16384));
    }

    [Fact]
    public void NoMipsFacesArePacked()
    {
        Assert.Equal(512u, Ubc1(0xF, 1, 32, 512));
    }

    [Fact]
    public void MipmappedFacesReserveTheChainAndAlign()
    {
        // Uncharted's 32x32 UBC3 ambient cube with all 6 levels: 1024+256+64+16+16+16 = 1392 -> 2048
        Assert.Equal(2048u, Ubc3(5, 6, 32, 1392));
        // the same cube with only 2 levels used still reserves the rest of the chain
        Assert.Equal(2048u, Ubc3(1, 2, 32, 1280));
        // 32x32 U8U8U8U8 with 6 levels: 4096+1024+256+64+16+4 = 5460 -> 6144
        Assert.Equal(6144u, Rgba8(5, 6, 32, 5460));
    }

    [Fact]
    public void SmallMipmappedFacesAreOnlyWordAligned()
    {
        // 16x16 UBC1, 5 levels: 128+32+8+8+8 = 184, below the 2 KB alignment threshold (32x32 for compressed)
        Assert.Equal(184u, Ubc1(4, 5, 16, 184));
    }

    /// <summary>The table-based swizzle conversion (<see cref="TextureLayout.MortonToLinear"/>) gives the per-texel
    /// result for square and non-square power-of-two images and every element size, also inside a larger swizzle.</summary>
    [Theory]
    [InlineData(1, 1, 4)]
    [InlineData(64, 64, 1)]
    [InlineData(64, 64, 2)]
    [InlineData(128, 32, 4)]
    [InlineData(16, 256, 8)]
    [InlineData(32, 32, 16)]
    [InlineData(8, 4, 3)]
    [InlineData(256, 256, 4)]
    public void MortonToLinearMatchesPerTexel(int w, int h, int bpp)
    {
        var rng = new Random(w * 131 + h * 7 + bpp);
        byte[] src = new byte[w * h * bpp];
        rng.NextBytes(src);
        byte[] want = new byte[src.Length];
        byte[] got = new byte[src.Length];
        TextureLayout.SwizzledToLinearPerTexel(want, src, (uint)w, (uint)h, bpp);
        Assert.True(TextureLayout.MortonToLinear(got, src, (uint)w, (uint)h, (uint)w, (uint)h, bpp));
        Assert.Equal(want, got);
        // a 5x3-element image inside its 8x4 swizzle (non-power-of-two sizes, compressed block grids)
        uint sw = (uint)Math.Min(w, 5), sh = (uint)Math.Min(h, 3);
        if (w >= 8 && h >= 4)
        {
            byte[] part = new byte[sw * sh * bpp];
            Assert.True(TextureLayout.MortonToLinear(part, src, sw, sh, 8, 4, bpp));
            for (uint y = 0; y < sh; y++)
            {
                for (uint x = 0; x < sw; x++)
                {
                    Assert.Equal(src.AsSpan((int)(TextureLayout.Morton(x, y, 8, 4) * bpp), bpp).ToArray(), part.AsSpan((int)((y * sw + x) * bpp), bpp).ToArray());
                }
            }
        }
    }

    [Fact]
    public void MortonToLinearRefusesShortSpans()
    {
        byte[] src = new byte[64 * 64 * 4 - 1];
        Assert.False(TextureLayout.MortonToLinear(new byte[64 * 64 * 4], src, 64, 64, 64, 64, 4));
        Assert.False(TextureLayout.MortonToLinear(new byte[64 * 64 * 4 - 4], new byte[64 * 64 * 4], 64, 64, 64, 64, 4));
    }

    /// <summary>Tiled to linear by tile rows equals the per-texel copy, including widths that are not a multiple of
    /// the 32-texel tile and a source that ends inside the image.</summary>
    [Theory]
    [InlineData(64, 64, 4, 0)]
    [InlineData(100, 40, 2, 0)]
    [InlineData(33, 70, 4, 1000)]
    [InlineData(31, 31, 1, 0)]
    public void TiledToLinearMatchesPerTexel(int w, int h, int bpp, int cut)
    {
        int tw = (w + 31) & ~31, th = (h + 31) & ~31;
        byte[] src = new byte[tw * th * bpp - cut];
        new Random(w + h + bpp).NextBytes(src);
        byte[] want = new byte[w * h * bpp];
        byte[] got = new byte[w * h * bpp];
        TextureLayout.TiledToLinearPerTexel(want, src, (uint)w, (uint)h, bpp);
        TextureLayout.TiledToLinear(got, src, (uint)w, (uint)h, bpp);
        Assert.Equal(want, got);
    }
}
