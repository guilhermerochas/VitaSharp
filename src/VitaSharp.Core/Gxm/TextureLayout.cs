using System.Numerics;

namespace VitaSharp.Core.Gxm;

/// <summary>Address computations for GXM surface layouts (linear, 32x32 tiled, morton swizzled).</summary>
public static class TextureLayout
{
    private static uint Part1By1(uint x)
    {
        x &= 0x0000FFFF;
        x = (x ^ (x << 8)) & 0x00FF00FF;
        x = (x ^ (x << 4)) & 0x0F0F0F0F;
        x = (x ^ (x << 2)) & 0x33333333;
        x = (x ^ (x << 1)) & 0x55555555;
        return x;
    }

    private static uint Compact1By1(uint x)
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

    /// <summary>VITASHARP_TEX_LAYOUT=legacy: the per-texel conversions (A/B against the table/run copies below).</summary>
    public static readonly bool Legacy = Environment.GetEnvironmentVariable(Util.EnvVars.TexLayout) == "legacy";

    /// <summary>Converts a swizzled image to linear rows (bpp must be a whole number of bytes).</summary>
    public static void SwizzledToLinear(Span<byte> dst, ReadOnlySpan<byte> src, uint width, uint height, int bytesPerPixel)
    {
        if (!Legacy && MortonToLinear(dst, src, width, height, width, height, bytesPerPixel))
        {
            return;
        }

        SwizzledToLinearPerTexel(dst, src, width, height, bytesPerPixel);
    }

    /// <summary>The per-texel conversion: texels whose source or destination lies outside the spans are skipped.</summary>
    internal static void SwizzledToLinearPerTexel(Span<byte> dst, ReadOnlySpan<byte> src, uint width, uint height, int bytesPerPixel)
    {
        uint min = Math.Min(width, height);
        int k = BitOperations.Log2(Math.Max(min, 1));
        uint count = width * height;
        for (uint i = 0; i < count; i++)
        {
            uint x = Compact1By1(i >> 1) & (min - 1);
            uint y = Compact1By1(i) & (min - 1);
            uint upper = (i >> (2 * k)) << k;
            if (width >= height)
            {
                x |= upper;
            }
            else
            {
                y |= upper;
            }

            int s = (int)i * bytesPerPixel, d = (int)(y * width + x) * bytesPerPixel;
            if (s + bytesPerPixel > src.Length || d + bytesPerPixel > dst.Length)
            {
                continue;
            }

            src.Slice(s, bytesPerPixel).CopyTo(dst.Slice(d, bytesPerPixel));
        }
    }

    /// <summary>Bytes of source a <see cref="MortonToLinear"/> of a <paramref name="width"/>x<paramref name="height"/>
    /// image inside a <paramref name="pw"/>x<paramref name="ph"/> swizzle reads (up to its last texel).</summary>
    public static long MortonSourceBytes(uint width, uint height, uint pw, uint ph, int bytesPerElement) =>
        width == 0 || height == 0 ? 0 : ((long)Morton(width - 1, height - 1, pw, ph) + 1) * bytesPerElement;

    /// <summary>
    /// Row-major <paramref name="width"/>x<paramref name="height"/> elements (texels or compressed blocks) from a
    /// swizzled (Morton) <paramref name="pw"/>x<paramref name="ph"/> layout (powers of two, at least the size): the
    /// same result as <see cref="Morton"/> per element, with the index split into a column part and a row part
    /// looked up from two tables (the interleaved bits of x and y never overlap). False, with nothing written, when a
    /// span is too short for the whole image (the per-element paths skip what lies outside).
    /// </summary>
    public static bool MortonToLinear(Span<byte> dst, ReadOnlySpan<byte> src, uint width, uint height, uint pw, uint ph, int bytesPerElement)
    {
        if (width == 0 || height == 0)
        {
            return true;
        }

        if (width > pw || height > ph || !BitOperations.IsPow2(pw) || !BitOperations.IsPow2(ph) || width > 1u << 15 || height > 1u << 15)
        {
            return false;
        }

        if (src.Length < MortonSourceBytes(width, height, pw, ph, bytesPerElement) || dst.Length < (long)width * height * bytesPerElement)
        {
            return false;
        }

        uint min = Math.Min(pw, ph);
        int k = BitOperations.Log2(min);
        int[]? rented = width + height > 4096 ? System.Buffers.ArrayPool<int>.Shared.Rent((int)(width + height)) : null;
        Span<int> tables = rented ?? stackalloc int[(int)(width + height)];
        Span<int> xs = tables[..(int)width];
        Span<int> ys = tables.Slice((int)width, (int)height);
        for (uint x = 0; x < width; x++)
        {
            xs[(int)x] = (int)(((x >> k) << (2 * k)) | (Part1By1(x & (min - 1)) << 1));
        }

        for (uint y = 0; y < height; y++)
        {
            ys[(int)y] = (int)(((y >> k) << (2 * k)) | Part1By1(y & (min - 1)));
        }

        switch (bytesPerElement)
        {
            case 1:
                Gather<byte>(dst, src, xs, ys);
                break;
            case 2:
                Gather<ushort>(dst, src, xs, ys);
                break;
            case 4:
                Gather<uint>(dst, src, xs, ys);
                break;
            case 8:
                Gather<ulong>(dst, src, xs, ys);
                break;
            case 16:
                Gather<System.Runtime.Intrinsics.Vector128<byte>>(dst, src, xs, ys);
                break;
            default:
                for (int y = 0; y < ys.Length; y++)
                {
                    Span<byte> row = dst.Slice(y * xs.Length * bytesPerElement, xs.Length * bytesPerElement);
                    for (int x = 0; x < xs.Length; x++)
                    {
                        src.Slice((xs[x] | ys[y]) * bytesPerElement, bytesPerElement).CopyTo(row.Slice(x * bytesPerElement, bytesPerElement));
                    }
                }
                break;
        }
        if (rented != null)
        {
            System.Buffers.ArrayPool<int>.Shared.Return(rented);
        }

        return true;

        static void Gather<T>(Span<byte> dst, ReadOnlySpan<byte> src, ReadOnlySpan<int> xs, ReadOnlySpan<int> ys) where T : unmanaged
        {
            global::System.Span<T> d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(dst);
            global::System.ReadOnlySpan<T> s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(src);
            ref T sRef = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(s);
            int w = xs.Length;
            for (int y = 0; y < ys.Length; y++)
            {
                int yi = ys[y];
                global::System.Span<T> row = d.Slice(y * w, w);
                // the spans were checked against the last element: every index is in range
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = System.Runtime.CompilerServices.Unsafe.Add(ref sRef, xs[x] | yi);
                }
            }
        }
    }

    /// <summary>Converts a tiled image (32x32-texel tiles, each 1024 texels in row order) to linear rows; texels whose
    /// source lies outside <paramref name="src"/> are skipped.</summary>
    public static void TiledToLinear(Span<byte> dst, ReadOnlySpan<byte> src, uint width, uint height, int bytesPerPixel)
    {
        if (Legacy)
        {
            TiledToLinearPerTexel(dst, src, width, height, bytesPerPixel);
            return;
        }
        // each tile row is 32 texels in a row of the source: one copy per tile row
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x += 32)
            {
                int run = (int)Math.Min(32, width - x);
                long s = (long)Tiled(x, y, width) * bytesPerPixel;
                long fit = s >= src.Length ? 0 : (src.Length - s) / bytesPerPixel;
                int n = (int)Math.Min(run, fit);
                if (n <= 0)
                {
                    continue;
                }

                src.Slice((int)s, n * bytesPerPixel).CopyTo(dst.Slice((int)((y * width + x) * bytesPerPixel), n * bytesPerPixel));
            }
        }
    }

    internal static void TiledToLinearPerTexel(Span<byte> dst, ReadOnlySpan<byte> src, uint width, uint height, int bytesPerPixel)
    {
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                int s = (int)Tiled(x, y, width) * bytesPerPixel, d = (int)(y * width + x) * bytesPerPixel;
                if (s + bytesPerPixel > src.Length)
                {
                    continue;
                }

                src.Slice(s, bytesPerPixel).CopyTo(dst.Slice(d, bytesPerPixel));
            }
        }
    }

    /// <summary>
    /// Distance in bytes between the faces of a cube texture whose levels take <paramref name="usedBytes"/> per face.
    /// After Vita3K (texture/cache.cpp), a mipmapped face reserves its whole power-of-two chain, down to the level
    /// where one dimension reaches 1, even past the levels the texture uses. It is aligned to 2 KB when its base level
    /// is large enough: 32x32 for compressed and 8-bit formats, 16x16 for 16- and 32-bit ones, 8x8 for 64-bit ones.
    /// Otherwise faces are only 4-byte aligned.
    /// Faces without mips (raw mip count 0xF) are packed back to back. So are single-level faces (raw count 0) of
    /// at most 32x32: Silent Hill: Book of Memories' 32x32 U8U8U8U8 cube has faces 4 KB apart (with the chain, faces
    /// 4-5 read past the cube), and Unit 13's 32x32 UBC1 ambient cube has faces 512 bytes apart (with 2 KB alignment,
    /// faces 2-5 read other data and the deferred ambient term turned into coloured blocks). Larger single-level
    /// faces follow Vita3K's rule: FIFA 13's 128x128 UBC3 reflection cubes have faces 22528 bytes apart (16 KB of
    /// data, the 64x64..1x1 chain and 2 KB alignment); packed, faces 1-5 read the gaps and the players' reflections
    /// turned into cyan and green blocks.
    /// </summary>
    public static uint CubeFaceStride(uint mipCountRaw, uint mips, uint width, uint height, int bitsPerPixel, bool compressed,
        uint blockWidth, uint blockHeight, uint blockBytes, uint usedBytes)
    {
        if (mipCountRaw == 0xF || (mips <= 1 && (width <= 32 || height <= 32)))
        {
            return Align(usedBytes, 4);
        }

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

    private static uint Align(uint v, uint a) => (v + a - 1) / a * a;
}
