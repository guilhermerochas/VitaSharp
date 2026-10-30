using System.Diagnostics;
using System.IO.Hashing;
using Silk.NET.Vulkan;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// BC1-BC5 and PVRTC textures transcoded to ASTC 4x4 for GPUs that sample ASTC but not BCn (most Android GPUs):
/// a quarter of the memory of the RGBA8 fallback (<see cref="BcSoftware"/>), the same as BC2/BC3 and twice BC1/BC4.
/// <para>
/// The source is decoded to RGBA8 (BC by <see cref="BcSoftware"/>, PVRTC by <see cref="Pvrtc"/>) and encoded by
/// <see cref="AstcEncoder"/> (pure C#, real-time: a handful of fixed block configurations instead of a search; a
/// direct BC-to-ASTC block mapping is not used because BC1's punch-through and BC3's independent alpha indices have no
/// exact ASTC equivalent). BC4 becomes luminance blocks and BC5 luminance+alpha blocks, sampled through a view swizzle
/// (<see cref="ChannelSwizzle"/>). Signed BC4/BC5 stay on the RGBA8 SNORM fallback (ASTC LDR is unsigned), and so do
/// sRGB BC4/BC5 (ASTC applies sRGB to the luminance only).
/// </para>
/// <para>
/// Results are cached: in memory (content-addressed, LRU, <see cref="MemoryBudget"/>) and on disk per title
/// (<see cref="TranscodeDiskCache"/> in the shader cache folder), so a texture is encoded once per title, not once per
/// run. The key is the xxHash3 of the source data plus format, size and <see cref="EncoderVersion"/>.
/// </para>
/// </summary>
public static class AstcTranscoder
{
    /// <summary>Bump when the encoder's output changes (old disk entries are then simply not found).</summary>
    public const int EncoderVersion = 1;
    public static long MemoryBudget = 32L << 20;
    /// <summary>Per-title disk cache (set when a title starts; null = memory only).</summary>
    public static TranscodeDiskCache? Disk;
    /// <summary>Encoder threads for one large level (the renderer's GPU thread waits for them).</summary>
    public static int Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    static readonly object Lock = new();
    static readonly Dictionary<ulong, LinkedListNode<(ulong Key, byte[] Data)>> Map = new();
    static readonly LinkedList<(ulong Key, byte[] Data)> Lru = new();
    static long _bytes;
    public static long Encoded, EncodedTexels, EncodeTicks, MemoryHits, DiskHits;

    public static Format FormatFor(bool gamma) => gamma ? Format.Astc4x4SrgbBlock : Format.Astc4x4UnormBlock;

    /// <summary>The ASTC channel layout for a GXM BC/PVRTC base format; null when it stays on the RGBA8 fallback.</summary>
    public static AstcChannels? ChannelsFor(TexBase f, bool gamma) => f switch
    {
        TexBase.UBC1 or TexBase.UBC2 or TexBase.UBC3 => AstcChannels.Rgba,
        TexBase.PVRT2BPP or TexBase.PVRT4BPP or TexBase.PVRTII2BPP or TexBase.PVRTII4BPP => AstcChannels.Rgba,
        TexBase.UBC4 when !gamma => AstcChannels.R,
        TexBase.UBC5 when !gamma => AstcChannels.Rg,
        _ => null,
    };

    /// <summary>How the decoded ASTC channels map to the source format's (R, G, B, A).</summary>
    public static (ComponentSwizzle R, ComponentSwizzle G, ComponentSwizzle B, ComponentSwizzle A) ChannelSwizzle(AstcChannels c) => c switch
    {
        AstcChannels.R => (ComponentSwizzle.R, ComponentSwizzle.Zero, ComponentSwizzle.Zero, ComponentSwizzle.One),
        AstcChannels.Rg => (ComponentSwizzle.R, ComponentSwizzle.A, ComponentSwizzle.Zero, ComponentSwizzle.One),
        _ => (ComponentSwizzle.R, ComponentSwizzle.G, ComponentSwizzle.B, ComponentSwizzle.A),
    };

    /// <summary>The texture's own swizzle applied on top of <see cref="ChannelSwizzle"/>.</summary>
    public static ComponentMapping Compose(ComponentMapping texture, AstcChannels c)
    {
        var (r, g, b, a) = ChannelSwizzle(c);
        ComponentSwizzle Map(ComponentSwizzle s, ComponentSwizzle identity) => (s == ComponentSwizzle.Identity ? identity : s) switch
        {
            ComponentSwizzle.R => r, ComponentSwizzle.G => g, ComponentSwizzle.B => b, ComponentSwizzle.A => a, var other => other,
        };
        return new ComponentMapping(Map(texture.R, ComponentSwizzle.R), Map(texture.G, ComponentSwizzle.G), Map(texture.B, ComponentSwizzle.B), Map(texture.A, ComponentSwizzle.A));
    }

    public static string Summary()
    {
        lock (Lock)
            return $"ASTC 4x4 transcoding: {Encoded} levels encoded ({EncodedTexels / 1e6:F1} M texels, {EncodeTicks * 1000.0 / Stopwatch.Frequency:F0} ms, " +
                   $"{(EncodeTicks > 0 ? EncodedTexels / (EncodeTicks / (double)Stopwatch.Frequency) / 1e6 : 0):F1} M texels/s), {MemoryHits} from memory, {DiskHits} from disk" +
                   (Disk != null ? $" ({Disk.Summary()})" : " (no disk cache)");
    }

    static ulong Key(ReadOnlySpan<byte> source, TexBase f, uint w, uint h) =>
        XxHash3.HashToUInt64(source, (long)(((ulong)f * 0x9E3779B97F4A7C15ul) ^ ((ulong)w << 32 | h) ^ ((ulong)EncoderVersion << 56)));

    static ulong Tag(TexBase f, uint w, uint h) => (ulong)(uint)f << 40 ^ (ulong)w << 20 ^ h;

    /// <summary>One level of BC blocks (row order) to ASTC 4x4 blocks.</summary>
    public static Texels FromBc(TexBase f, ReadOnlySpan<byte> blocks, uint w, uint h, AstcChannels channels) =>
        Transcode(Key(blocks, f, w, h), Tag(f, w, h), w, h, channels, blocks, static (src, f, w, h) => BcSoftware.Decode(f, src, w, h), f);

    /// <summary>One level of PVRTC data to ASTC 4x4 blocks.</summary>
    public static Texels FromPvrtc(TexBase f, ReadOnlySpan<byte> data, uint w, uint h, bool twoBpp, bool ii) =>
        Transcode(Key(data, f, w, h), Tag(f, w, h), w, h, AstcChannels.Rgba, data, (src, _, sw, sh) => Pvrtc.Decode(src, sw, sh, twoBpp, ii), f);

    delegate Texels DecodeFn(ReadOnlySpan<byte> src, TexBase f, uint w, uint h);

    static Texels Transcode(ulong key, ulong tag, uint w, uint h, AstcChannels channels, ReadOnlySpan<byte> source, DecodeFn decode, TexBase f)
    {
        int length = (int)(((w + 3) / 4) * ((h + 3) / 4) * AstcEncoder.BlockBytes);
        lock (Lock)
        {
            if (Map.TryGetValue(key, out var node) && node.Value.Data.Length == length)
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                MemoryHits++;
                return Copy(node.Value.Data);
            }
        }
        if (Disk?.TryGet(key, tag, length) is { } cached)
        {
            Interlocked.Increment(ref DiskHits);
            Remember(key, cached);
            return Copy(cached);
        }
        long t0 = Stopwatch.GetTimestamp();
        var outBlocks = new byte[length];
        using (var rgba = decode(source, f, w, h))
            AstcEncoder.EncodeParallel(rgba.Span, (int)w, (int)h, outBlocks, channels, Threads);
        long dt = Stopwatch.GetTimestamp() - t0;
        lock (Lock)
        {
            Encoded++;
            EncodedTexels += (long)w * h;
            EncodeTicks += dt;
        }
        Disk?.Put(key, tag, outBlocks);
        Remember(key, outBlocks);
        return Copy(outBlocks);
    }

    static Texels Copy(byte[] data)
    {
        var t = Texels.Rent(data.Length);
        data.CopyTo(t.Array, 0);
        return t;
    }

    static void Remember(ulong key, byte[] data)
    {
        if (data.Length > MemoryBudget / 8) return;
        lock (Lock)
        {
            if (Map.ContainsKey(key)) return;
            Map[key] = Lru.AddFirst((key, data));
            _bytes += data.Length;
            while (_bytes > MemoryBudget && Lru.Last is { } last)
            {
                Lru.RemoveLast();
                Map.Remove(last.Value.Key);
                _bytes -= last.Value.Data.Length;
            }
        }
    }

    public static void Reset()
    {
        lock (Lock) { Map.Clear(); Lru.Clear(); _bytes = 0; Encoded = EncodedTexels = EncodeTicks = MemoryHits = DiskHits = 0; }
    }
}
