using VitaSharp.Core.Compat;

namespace VitaSharp.Tests;

/// <summary>The compat LCD DMA's conversion of the PSP pixel formats to the Vita framebuffer's A8B8G8R8 (the copy
/// itself at the PSP's vertical blank is CompatCoreTests.ThePspPictureReachesTheFramebufferBlockAtTheVblank).</summary>
public sealed class CompatLcdDmaTests
{
    [Theory]
    [InlineData(0u, 0xF800, 0xFFFF0000u)] // BGR565: blue in the top bits
    [InlineData(0u, 0x001F, 0xFF0000FFu)]
    [InlineData(0u, 0x07E0, 0xFF00FF00u)]
    [InlineData(1u, 0x801F, 0xFF0000FFu)] // ABGR1555
    [InlineData(1u, 0x7C00, 0x00FF0000u)]
    [InlineData(2u, 0xF00F, 0xFF0000FFu)] // ABGR4444
    [InlineData(2u, 0x0F80, 0x00FF8800u)]
    public void SixteenBitPixelsWidenToA8B8G8R8(uint format, int pixel, uint expected)
    {
        Assert.Equal(expected, CompatLcdDma.Widen((ushort)pixel, format));
    }

    [Fact]
    public void ThirtyTwoBitPixelsCopyAsTheyAre()
    {
        byte[] line = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];
        uint[] to = new uint[2];
        CompatLcdDma.ConvertLine(line, to, 3);
        Assert.Equal([0x44332211u, 0x88776655u], to);
    }
}
