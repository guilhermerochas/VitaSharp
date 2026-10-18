using System.Numerics;

namespace VitaSharp.Core.Gxm;

/// <summary>Address computations for GXM surface layouts (linear, 32x32 tiled, morton swizzled).</summary>
public static class TextureLayout
{
    static uint Part1By1(uint x)
    {
        x &= 0x0000FFFF;
        x = (x ^ (x << 8)) & 0x00FF00FF;
        x = (x ^ (x << 4)) & 0x0F0F0F0F;
        x = (x ^ (x << 2)) & 0x33333333;
        x = (x ^ (x << 1)) & 0x55555555;
        return x;
    }

    static uint Compact1By1(uint x)
    {
        x &= 0x55555555;
        x = (x ^ (x >> 1)) & 0x33333333;
        x = (x ^ (x >> 2)) & 0x0F0F0F0F;
        x = (x ^ (x >> 4)) & 0x00FF00FF;
        x = (x ^ (x >> 8)) & 0x0000FFFF;
        return x;
    }

    /// <summary>Morton index of (x, y) inside a power-of-two texture (non-square handled like the hardware).</summary>
    public static uint Morton(uint x, uint y, uint width, uint height)
    {
        uint min = Math.Min(width, height);
        int k = BitOperations.Log2(Math.Max(min, 1));
        uint result = ((x >> k) | (y >> k)) << (2 * k);
        result |= Part1By1(x & (min - 1)) << 1;
        result |= Part1By1(y & (min - 1));
        return result;
    }

    public static uint Tiled(uint x, uint y, uint strideInPixels)
    {
        uint inTile = (x & 31) | ((y & 31) << 5);
        uint tile = (x >> 5) + ((strideInPixels + 31) >> 5) * (y >> 5);
        return (tile << 10) | inTile;
    }

    /// <summary>Converts a swizzled image to linear rows (bpp must be a whole number of bytes).</summary>
    public static void SwizzledToLinear(Span<byte> dst, ReadOnlySpan<byte> src, uint width, uint height, int bytesPerPixel)
    {
        uint min = Math.Min(width, height);
        int k = BitOperations.Log2(Math.Max(min, 1));
        uint count = width * height;
        for (uint i = 0; i < count; i++)
        {
            uint x = Compact1By1(i >> 1) & (min - 1);
            uint y = Compact1By1(i) & (min - 1);
            uint upper = (i >> (2 * k)) << k;
            if (width >= height) x |= upper; else y |= upper;
            int s = (int)i * bytesPerPixel, d = (int)(y * width + x) * bytesPerPixel;
            if (s + bytesPerPixel > src.Length || d + bytesPerPixel > dst.Length) continue;
            src.Slice(s, bytesPerPixel).CopyTo(dst.Slice(d, bytesPerPixel));
        }
    }

    public static void TiledToLinear(Span<byte> dst, ReadOnlySpan<byte> src, uint width, uint height, int bytesPerPixel)
    {
        for (uint y = 0; y < height; y++)
            for (uint x = 0; x < width; x++)
            {
                int s = (int)Tiled(x, y, width) * bytesPerPixel, d = (int)(y * width + x) * bytesPerPixel;
                if (s + bytesPerPixel > src.Length) continue;
                src.Slice(s, bytesPerPixel).CopyTo(dst.Slice(d, bytesPerPixel));
            }
    }
}
