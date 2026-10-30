using System.IO.Hashing;
using VitaSharp.Core.Gxm;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// CPU decoding of GXM's block-compressed textures (UBC1-UBC3, UBC4/SBC4, UBC5/SBC5; GXM has no BC6H/BC7) for
/// devices without textureCompressionBC (most Android GPUs). The output matches what the hardware formats sample:
/// BC1-3 to RGBA8, BC4 to (R, 0, 0, 1) and BC5 to (R, G, 0, 1), unsigned ones as UNORM bytes and signed ones as
/// SNORM bytes (the image format then is R8G8B8A8_SNORM, see <see cref="FormatFor"/>).
/// <para>
/// Decoded levels are kept in a small content-addressed cache (xxHash3 of the compressed blocks + format + size,
/// LRU, <see cref="CacheBudget"/> bytes): re-uploads of unchanged data (texture cache evictions, the same texture
/// at another address, streaming that rewrites identical data) skip the decode.
/// </para>
/// <para>On GPUs that sample ASTC the decoded texels are re-encoded as ASTC 4x4 instead of uploaded (<see cref="AstcTranscoder"/>);
/// this RGBA8 path remains for GPUs without ASTC and for the signed formats.</para>
/// </summary>
public static class BcSoftware
{

    public static long CacheBudget = 64L << 20;
    static readonly object Lock = new();
    static readonly Dictionary<(ulong, TexBase, uint, uint), LinkedListNode<(ulong Hash, TexBase F, uint W, uint H, byte[] Data)>> Map = new();
    static readonly LinkedList<(ulong Hash, TexBase F, uint W, uint H, byte[] Data)> Lru = new();
    static long _bytes;
    public static long Decodes, CacheHits;

    /// <summary>Signed BC4/BC5 decode to SNORM texels; everything else to UNORM.</summary>
    public static Silk.NET.Vulkan.Format FormatFor(TexBase f) =>
        f is TexBase.SBC4 or TexBase.SBC5 ? Silk.NET.Vulkan.Format.R8G8B8A8SNorm : Silk.NET.Vulkan.Format.R8G8B8A8Unorm;

    public static string Summary()
    {
        lock (Lock) return $"{Decodes} levels decoded, {CacheHits} from the cache ({Map.Count} entries, {_bytes / 1048576.0:F1} MB)";
    }

    /// <summary>Decodes one level (blocks in row order, ((w+3)/4)*((h+3)/4) blocks) into w*h RGBA8 texels.</summary>
    public static Texels Decode(TexBase f, ReadOnlySpan<byte> blocks, uint w, uint h)
    {
        var key = (XxHash3.HashToUInt64(blocks), f, w, h);
        lock (Lock)
        {
            if (Map.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                CacheHits++;
                var hit = Texels.Rent(node.Value.Data.Length);
                node.Value.Data.CopyTo(hit.Array, 0);
                return hit;
            }
        }
        var t = DecodeUncached(f, blocks, w, h);
        Interlocked.Increment(ref Decodes);
        if (t.Length <= CacheBudget / 8)
        {
            var copy = t.Span.ToArray();
            lock (Lock)
            {
                if (!Map.ContainsKey(key))
                {
                    Map[key] = Lru.AddFirst((key.Item1, f, w, h, copy));
                    _bytes += copy.Length;
                    while (_bytes > CacheBudget && Lru.Last is { } last)
                    {
                        Lru.RemoveLast();
                        Map.Remove((last.Value.Hash, last.Value.F, last.Value.W, last.Value.H));
                        _bytes -= last.Value.Data.Length;
                    }
                }
            }
        }
        return t;
    }

    public static void ClearCache()
    {
        lock (Lock) { Map.Clear(); Lru.Clear(); _bytes = 0; }
    }

    static Texels DecodeUncached(TexBase f, ReadOnlySpan<byte> src, uint w, uint h) => f switch
    {
        TexBase.UBC4 => Rgtc(src, w, h, false, false),
        TexBase.SBC4 => Rgtc(src, w, h, false, true),
        TexBase.UBC5 => Rgtc(src, w, h, true, false),
        TexBase.SBC5 => Rgtc(src, w, h, true, true),
        TexBase.UBC1 => Bc13(src, w, h, 8, false, false),
        TexBase.UBC2 => Bc13(src, w, h, 16, true, false),
        TexBase.UBC3 => Bc13(src, w, h, 16, false, true),
        _ => Texels.Rent(w * h * 4L),
    };

    /// <summary>
    /// BC1-BC3 as the reference decoder does it: 5/6-bit endpoints expanded by bit replication, the interpolated
    /// colours rounded ((2a + b + 1) / 3), BC1's three-colour mode with transparent black, BC2's 4-bit alpha x 17 and
    /// BC3's alpha block rounded like BC4 (<see cref="UnsignedBlock"/>).
    /// </summary>
    static Texels Bc13(ReadOnlySpan<byte> src, uint w, uint h, int blockBytes, bool bc2, bool bc3)
    {
        uint bw = (w + 3) / 4, bh = (h + 3) / 4;
        var dstT = Texels.Rent(w * h * 4L);
        var dst = dstT.Array;
        Span<byte> c = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[16];
        for (uint by = 0; by < bh; by++)
            for (uint bx = 0; bx < bw; bx++)
            {
                int o = (int)((by * bw + bx) * blockBytes);
                if (o + blockBytes > src.Length) return dstT;
                var block = src.Slice(o, blockBytes);
                if (bc2) for (int i = 0; i < 16; i++) alpha[i] = (byte)(((block[i / 2] >> ((i & 1) * 4)) & 15) * 17);
                else if (bc3) UnsignedBlock(block[..8], alpha);
                var col = bc2 || bc3 ? block[8..] : block;
                int c0 = col[0] | col[1] << 8, c1 = col[2] | col[3] << 8;
                Expand565(c0, c, 0);
                Expand565(c1, c, 4);
                bool four = c0 > c1 || bc2 || bc3;
                for (int k = 0; k < 3; k++)
                {
                    c[8 + k] = four ? (byte)((2 * c[k] + c[4 + k] + 1) / 3) : (byte)((c[k] + c[4 + k] + 1) / 2);
                    c[12 + k] = four ? (byte)((c[k] + 2 * c[4 + k] + 1) / 3) : (byte)0;
                }
                c[3] = c[7] = c[11] = 255;
                c[15] = four ? (byte)255 : (byte)0;
                uint idx = (uint)(col[4] | col[5] << 8 | col[6] << 16 | col[7] << 24);
                for (int i = 0; i < 16; i++)
                {
                    uint x = bx * 4 + (uint)(i & 3), y = by * 4 + (uint)(i >> 2);
                    if (x >= w || y >= h) continue;
                    int ci = (int)((idx >> (2 * i)) & 3) * 4, p = (int)((y * w + x) * 4);
                    dst[p] = c[ci]; dst[p + 1] = c[ci + 1]; dst[p + 2] = c[ci + 2];
                    dst[p + 3] = bc2 || bc3 ? alpha[i] : c[ci + 3];
                }
            }
        return dstT;
    }

    static void Expand565(int v, Span<byte> c, int o)
    {
        int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
        c[o] = (byte)(r << 3 | r >> 2);
        c[o + 1] = (byte)(g << 2 | g >> 4);
        c[o + 2] = (byte)(b << 3 | b >> 2);
        c[o + 3] = 255;
    }

    /// <summary>BC4 (one channel) / BC5 (two channels): R(,G) from the blocks, B = 0, A = 1 (255 UNORM, 127 SNORM).</summary>
    static Texels Rgtc(ReadOnlySpan<byte> src, uint w, uint h, bool two, bool signed)
    {
        uint bw = (w + 3) / 4, bh = (h + 3) / 4;
        int blockBytes = two ? 16 : 8;
        var dstT = Texels.Rent(w * h * 4L);
        var dst = dstT.Array;
        byte one = signed ? (byte)127 : (byte)255;
        for (int i = 3; i < w * h * 4; i += 4) dst[i] = one;
        Span<byte> ch = stackalloc byte[16];
        for (uint by = 0; by < bh; by++)
            for (uint bx = 0; bx < bw; bx++)
            {
                int o = (int)((by * bw + bx) * blockBytes);
                if (o + blockBytes > src.Length) return dstT;
                for (int c = 0; c < (two ? 2 : 1); c++)
                {
                    if (signed) SignedBlock(src.Slice(o + c * 8, 8), ch); else UnsignedBlock(src.Slice(o + c * 8, 8), ch);
                    for (int i = 0; i < 16; i++)
                    {
                        uint x = bx * 4 + (uint)(i & 3), y = by * 4 + (uint)(i >> 2);
                        if (x >= w || y >= h) continue;
                        dst[(int)((y * w + x) * 4) + c] = ch[i];
                    }
                }
            }
        return dstT;
    }

    static ulong Indices(ReadOnlySpan<byte> b)
    {
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        return bits;
    }

    /// <summary>RGTC unsigned block: endpoints 0..255, 8 or 6 interpolated values (+ 0 and 255).</summary>
    public static void UnsignedBlock(ReadOnlySpan<byte> b, Span<byte> outv)
    {
        int r0 = b[0], r1 = b[1];
        ulong bits = Indices(b);
        for (int i = 0; i < 16; i++)
        {
            int code = (int)((bits >> (3 * i)) & 7);
            int v = code switch
            {
                0 => r0,
                1 => r1,
                _ when r0 > r1 => ((8 - code) * r0 + (code - 1) * r1 + 3) / 7,
                6 => 0,
                7 => 255,
                _ => ((6 - code) * r0 + (code - 1) * r1 + 2) / 5,
            };
            outv[i] = (byte)v;
        }
    }

    /// <summary>RGTC signed block: endpoints -127..127 (-128 reads as -127), interpolated like the unsigned block,
    /// with -1.0 (-127) and +1.0 (127) as the extra values; output as two's complement SNORM bytes.</summary>
    public static void SignedBlock(ReadOnlySpan<byte> b, Span<byte> outv)
    {
        int r0 = Math.Max((sbyte)b[0], (sbyte)-127), r1 = Math.Max((sbyte)b[1], (sbyte)-127);
        ulong bits = Indices(b);
        for (int i = 0; i < 16; i++)
        {
            int code = (int)((bits >> (3 * i)) & 7);
            double v = code switch
            {
                0 => r0,
                1 => r1,
                _ when r0 > r1 => ((8 - code) * r0 + (code - 1) * r1) / 7.0,
                6 => -127,
                7 => 127,
                _ => ((6 - code) * r0 + (code - 1) * r1) / 5.0,
            };
            outv[i] = (byte)(sbyte)Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), -127, 127);
        }
    }
}
