using VitaSharp.Core.Gxm;

namespace VitaSharp.Tests;

/// <summary>
/// Face spacing of cube textures (<see cref="TextureLayout.CubeFaceStride"/>): mipmapped faces reserve their whole
/// power-of-two chain and are 2 KB aligned once large enough; single-level and mip-less faces are packed.
/// </summary>
public sealed class TextureLayoutTests
{
    // UBC1: 4 bits per pixel, 4x4 blocks of 8 bytes; UBC3: 8 bits per pixel, 4x4 blocks of 16 bytes
    static uint Ubc1(uint raw, uint mips, uint size, uint used) => TextureLayout.CubeFaceStride(raw, mips, size, size, 4, true, 4, 4, 8, used);
    static uint Ubc3(uint raw, uint mips, uint size, uint used) => TextureLayout.CubeFaceStride(raw, mips, size, size, 8, true, 4, 4, 16, used);
    static uint Rgba8(uint raw, uint mips, uint size, uint used) => TextureLayout.CubeFaceStride(raw, mips, size, size, 32, false, 1, 1, 4, used);

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
}
