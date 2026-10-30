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

    /// <summary>
    /// Distance in bytes between the faces of a cube texture whose levels take <paramref name="usedBytes"/> per face.
    /// After Vita3K (texture/cache.cpp), a mipmapped face reserves its whole power-of-two chain, down to the level
    /// where one dimension reaches 1, even past the levels the texture uses. It is aligned to 2 KB when its base level
    /// is large enough: 32x32 for compressed and 8-bit formats, 16x16 for 16- and 32-bit ones, 8x8 for 64-bit ones.
    /// Otherwise faces are only 4-byte aligned.
    /// Faces that are not mipmapped are packed back to back: a raw mip count of 0xF (no mips), or a single level (raw
    /// count 0). Vita3K reserves the chain and the 2 KB alignment for the single-level case too, but the data says
    /// otherwise. Silent Hill: Book of Memories' single-level 32x32 U8U8U8U8 cube has faces 4 KB apart; with the chain,
    /// faces 4-5 read past the cube. Unit 13's single-level 32x32 UBC1 ambient cube has faces 512 bytes apart; with
    /// 2 KB alignment, faces 2-5 read other data and the deferred ambient term turned into coloured blocks.
    /// </summary>
    public static uint CubeFaceStride(uint mipCountRaw, uint mips, uint width, uint height, int bitsPerPixel, bool compressed,
        uint blockWidth, uint blockHeight, uint blockBytes, uint usedBytes)
    {
        if (mipCountRaw == 0xF || mips <= 1) return Align(usedBytes, 4);
        uint size = usedBytes;
        uint pw = BitOperations.RoundUpToPowerOf2(width), ph = BitOperations.RoundUpToPowerOf2(height);
        for (int m = (int)mips; m < 32 && (pw >> m) > 0 && (ph >> m) > 0; m++)
        {
            uint lw = pw >> m, lh = ph >> m;
            size += compressed
                ? Align(lw, blockWidth) / blockWidth * (Align(lh, blockHeight) / blockHeight) * blockBytes
                : (uint)((ulong)lw * lh * (uint)bitsPerPixel / 8);
        }
        bool large = (width >= 32 && height >= 32 && (bitsPerPixel <= 8 || compressed))
            || (width >= 16 && height >= 16 && bitsPerPixel is 16 or 32)
            || (width >= 8 && height >= 8 && bitsPerPixel == 64);
        return Align(size, large ? 2048u : 4u);
    }

    static uint Align(uint v, uint a) => (v + a - 1) / a * a;
}
