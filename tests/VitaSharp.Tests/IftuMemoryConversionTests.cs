using System.Buffers.Binary;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;

namespace VitaSharp.Tests;

/// <summary>
/// The IFTU's conversion into memory (sceDisplaySetFrameBufForCompat with opt): the framebuffer scaled into the
/// rectangle of an A8B8G8R8 output, every other output pixel 0; nearest or bilinear by the flags' bit 20; the 16-bit
/// format widened; a rectangle partly off the output clipped; the output's pitch wider than its width left alone.
/// </summary>
public sealed class IftuMemoryConversionTests
{
    private static byte[] Source32(int width, int height, Func<int, int, uint> pixel)
    {
        byte[] source = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan((y * width + x) * 4), pixel(x, y));
            }
        }

        return source;
    }

    private static uint At(byte[] output, int pitch, int x, int y) => BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan((y * pitch + x) * 4));

    [Fact]
    public void NearestScalingFillsTheRectangleAndZeroesTheRest()
    {
        // 2x2 source: four colours, scaled 4x into (4, 2, 8, 8) of a 16x12 output
        byte[] source = Source32(2, 2, (x, y) => (uint)(0xFF000000 | (uint)(x + 1) | ((uint)(y + 1) << 8)));
        byte[] output = new byte[16 * 12 * 4];
        Array.Fill(output, (byte)0xAA);
        IftuMemoryConversion.Convert(source, 2, 2, 2, DisplayPixelFormat.A8B8G8R8, output, 16, 16, 12, new DisplayPlacement(4, 2, 8, 8, Bilinear: false));

        Assert.Equal(0xFF000101u, At(output, 16, 4, 2)); // top left texel
        Assert.Equal(0xFF000101u, At(output, 16, 7, 5));
        Assert.Equal(0xFF000102u, At(output, 16, 8, 2)); // top right texel
        Assert.Equal(0xFF000202u, At(output, 16, 11, 9)); // bottom right texel
        Assert.Equal(0u, At(output, 16, 3, 2)); // left of the rectangle
        Assert.Equal(0u, At(output, 16, 4, 10)); // below
        Assert.Equal(0u, At(output, 16, 15, 11));
    }

    [Fact]
    public void BilinearMixesNeighboursAndKeepsFlatAreas()
    {
        // 2x1 source: black and white, scaled to 4x1: the inner pixels are mixes, the outer ones nearer their texel
        byte[] source = Source32(2, 1, (x, _) => x == 0 ? 0xFF000000u : 0xFFFFFFFFu);
        byte[] output = new byte[4 * 4];
        IftuMemoryConversion.Convert(source, 2, 2, 1, DisplayPixelFormat.A8B8G8R8, output, 4, 4, 1, new DisplayPlacement(0, 0, 4, 1, Bilinear: true));

        uint first = At(output, 4, 0, 0) & 0xFF, second = At(output, 4, 1, 0) & 0xFF, third = At(output, 4, 2, 0) & 0xFF, fourth = At(output, 4, 3, 0) & 0xFF;
        Assert.Equal(0u, first); // the edge is clamped to the first texel
        Assert.InRange(second, 0x30u, 0x50u); // a quarter of the way
        Assert.InRange(third, 0xAFu, 0xCFu);
        Assert.Equal(0xFFu, fourth);
        Assert.Equal(0xFFu, At(output, 4, 1, 0) >> 24); // alpha mixes too: both opaque
    }

    [Fact]
    public void TheSixteenBitFormatIsWidenedAndAPartlyOffRectangleIsClipped()
    {
        // A1B5G5R5 green with alpha 0, a pitch of 4 pixels for 2; placed at (-2, 0) 4x2 in a 2x2 output
        byte[] source = new byte[4 * 2 * 2];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(i * 2), 0x03E0);
        }

        byte[] output = new byte[4 * 2 * 4];
        Array.Fill(output, (byte)0x55);
        IftuMemoryConversion.Convert(source, 4, 2, 2, DisplayPixelFormat.A1B5G5R5, output, 4, 2, 2, new DisplayPlacement(-2, 0, 4, 2, Bilinear: false));

        Assert.Equal(0x0000FF00u, At(output, 4, 0, 0));
        Assert.Equal(0x0000FF00u, At(output, 4, 1, 1));
        Assert.Equal(0x55555555u, At(output, 4, 2, 0)); // the pitch's padding is not written
        Assert.Equal(0x55555555u, At(output, 4, 3, 1));
    }
}
