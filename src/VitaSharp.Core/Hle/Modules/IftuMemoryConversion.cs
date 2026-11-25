using System.Buffers.Binary;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The IFTU's conversion of a framebuffer into memory (SceIftuForDriver_67E37EFC, FW 3.74 lowio.skprx, study only),
/// as sceDisplaySetFrameBufForCompat with <c>opt</c> uses it (display.skprx FUN_810023b8): the input descriptor is the
/// one the screen would get (the framebuffer scaled into the rectangle (x, y, width, height), the flags' bilinear bit),
/// and the output is opt's buffer: A8B8G8R8 (IFTU format 0x10), opt's width x height pixels, opt's pitch, every pixel
/// written, the ones outside the rectangle with the background the driver passes (all four components 0). The kernel
/// waits for the unit to finish before returning.
/// </summary>
public static class IftuMemoryConversion
{
    /// <summary>Converts the <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/> framebuffer
    /// <paramref name="source"/> (<paramref name="sourcePitch"/> pixels a row, <paramref name="sourceFormat"/>) into
    /// <paramref name="destination"/> (A8B8G8R8, <paramref name="destinationPitch"/> pixels a row).</summary>
    public static void Convert(ReadOnlySpan<byte> source, int sourcePitch, int sourceWidth, int sourceHeight, DisplayPixelFormat sourceFormat,
        Span<byte> destination, int destinationPitch, int destinationWidth, int destinationHeight, DisplayPlacement placement)
    {
        bool inside = placement.TryClip((uint)sourceWidth, (uint)sourceHeight, destinationWidth, destinationHeight,
            out _, out (int X0, int Y0, int X1, int Y1) rectangle);
        int bytesPerPixel = sourceFormat == DisplayPixelFormat.A1B5G5R5 ? 2 : 4;
        for (int y = 0; y < destinationHeight; y++)
        {
            Span<byte> row = destination.Slice(y * destinationPitch * 4, destinationWidth * 4);
            for (int x = 0; x < destinationWidth; x++)
            {
                uint pixel = 0;
                if (inside && x >= rectangle.X0 && x < rectangle.X1 && y >= rectangle.Y0 && y < rectangle.Y1)
                {
                    pixel = Sample(source, sourcePitch, sourceWidth, sourceHeight, bytesPerPixel, placement, x, y);
                }

                BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(x * 4, 4), pixel);
            }
        }
    }

    /// <summary>The source at the centre of output pixel (x, y): the nearest texel, or the 2x2 bilinear mix.</summary>
    private static uint Sample(ReadOnlySpan<byte> source, int pitch, int width, int height, int bytesPerPixel,
        DisplayPlacement placement, int x, int y)
    {
        // the source position of the output pixel's centre, in 16.16 fixed point, texel centres at .5
        long sourceX = ((2L * (x - placement.X) + 1) * width << 16) / (2L * placement.Width);
        long sourceY = ((2L * (y - placement.Y) + 1) * height << 16) / (2L * placement.Height);
        if (!placement.Bilinear)
        {
            int nearestX = Math.Clamp((int)(sourceX >> 16), 0, width - 1);
            int nearestY = Math.Clamp((int)(sourceY >> 16), 0, height - 1);
            return Fetch(source, pitch, bytesPerPixel, nearestX, nearestY);
        }

        long leftX = sourceX - 0x8000;
        long topY = sourceY - 0x8000;
        int x0 = (int)(leftX >> 16);
        int y0 = (int)(topY >> 16);
        int weightX = (int)(leftX & 0xFFFF);
        int weightY = (int)(topY & 0xFFFF);
        int clampedX0 = Math.Clamp(x0, 0, width - 1);
        int clampedX1 = Math.Clamp(x0 + 1, 0, width - 1);
        int clampedY0 = Math.Clamp(y0, 0, height - 1);
        int clampedY1 = Math.Clamp(y0 + 1, 0, height - 1);
        uint topLeft = Fetch(source, pitch, bytesPerPixel, clampedX0, clampedY0);
        uint topRight = Fetch(source, pitch, bytesPerPixel, clampedX1, clampedY0);
        uint bottomLeft = Fetch(source, pitch, bytesPerPixel, clampedX0, clampedY1);
        uint bottomRight = Fetch(source, pitch, bytesPerPixel, clampedX1, clampedY1);
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            long top = ((topLeft >> shift) & 0xFF) * (0x10000L - weightX) + ((topRight >> shift) & 0xFF) * weightX;
            long bottom = ((bottomLeft >> shift) & 0xFF) * (0x10000L - weightX) + ((bottomRight >> shift) & 0xFF) * weightX;
            long mixed = (top * (0x10000L - weightY) + bottom * weightY + (1L << 31)) >> 32;
            result |= (uint)Math.Clamp(mixed, 0, 0xFF) << shift;
        }

        return result;
    }

    /// <summary>The texel at (x, y) as an A8B8G8R8 word (red in the low byte); A1B5G5R5 widened (the top bits
    /// repeated, the alpha bit to 0 or 0xFF).</summary>
    private static uint Fetch(ReadOnlySpan<byte> source, int pitch, int bytesPerPixel, int x, int y)
    {
        int offset = (y * pitch + x) * bytesPerPixel;
        if (bytesPerPixel == 4)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));
        }

        int pixel = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, 2));
        uint red = Widen5(pixel & 0x1F);
        uint green = Widen5((pixel >> 5) & 0x1F);
        uint blue = Widen5((pixel >> 10) & 0x1F);
        uint alpha = (pixel & 0x8000) != 0 ? 0xFFu : 0u;
        return red | (green << 8) | (blue << 16) | (alpha << 24);
    }

    private static uint Widen5(int component) => (uint)((component << 3) | (component >> 2));
}
