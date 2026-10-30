using System.Numerics;
using System.Runtime.CompilerServices;

namespace VitaSharp.Core.Gpu;

/// <summary>Which channels of the RGBA8 input an ASTC encode keeps.</summary>
public enum AstcChannels
{
    /// <summary>Colour and alpha (BC1-BC3, PVRTC): RGB blocks where alpha is opaque, RGBA (one or two weight planes) elsewhere.</summary>
    Rgba,
    /// <summary>Red only (BC4): luminance blocks, decoded (L, L, L, 1); sample through an (R, 0, 0, 1) view swizzle.</summary>
    R,
    /// <summary>Red and green (BC5): luminance+alpha blocks holding R in L and G in A, decoded (L, L, L, A); sample
    /// through an (R, A, 0, 1) view swizzle.</summary>
    Rg,
}

/// <summary>
/// A small real-time ASTC 4x4 LDR encoder (and the matching software decoder, for tests and quality reports), used to
/// transcode BC1-BC5 and PVRTC textures for GPUs that sample ASTC but not BCn (most Android GPUs).
/// <para>
/// Every block is one partition with a 4x4 weight grid (no infill), encoded with a handful of fixed configurations
/// that are each tried and the one with the smallest squared error kept:
/// <list type="bullet">
/// <item>uniform blocks: void-extent blocks (the exact colour);</item>
/// <item>opaque colour: RGB direct (CEM 8) with 8 weight levels (8-bit endpoints), 16 levels (192-level endpoints) or
/// 4 levels;</item>
/// <item>colour with alpha: RGBA direct (CEM 12) with 4 levels (8-bit endpoints) or 8 levels (192-level endpoints),
/// or two weight planes with alpha on the second (4 levels each, 48-level endpoints) when alpha does not follow the
/// colour (BC3's separate alpha indices);</item>
/// <item>BC4: luminance (CEM 0) with 16 weight levels; BC5: luminance+alpha (CEM 4) with two planes (4 levels each)
/// or one plane (8 levels), or two planes with 8 levels and 8-level endpoints.</item>
/// </list>
/// Endpoints come from the principal axis of the block's texels (min/max of the projections), one least-squares
/// refinement with the chosen weights, quantized with the bounded integer sequence encoding the decoder implies for
/// the bits that are left (binary, trit or quint ranges); the RGB endpoint order avoids blue contraction.
/// </para>
/// </summary>
public static class AstcEncoder
{
    public const int BlockBytes = 16;

    /// <summary>Encodes a w x h RGBA8 image (4 bytes per texel, rows of w texels) into ceil(w/4) x ceil(h/4) blocks.</summary>
    public static void Encode(ReadOnlySpan<byte> rgba, int w, int h, Span<byte> blocks, AstcChannels channels)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        if (blocks.Length < bw * bh * BlockBytes) throw new ArgumentException("block buffer too small");
        Span<byte> texels = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                // partial blocks repeat the edge texels (they are never sampled)
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, w - 1), sy = Math.Min(by * 4 + y, h - 1);
                        rgba.Slice((sy * w + sx) * 4, 4).CopyTo(texels.Slice((y * 4 + x) * 4, 4));
                    }
                EncodeBlock(texels, blocks.Slice((by * bw + bx) * BlockBytes, BlockBytes), channels);
            }
    }

    /// <summary>Rows of blocks encoded in parallel (large textures).</summary>
    public static void EncodeParallel(ReadOnlySpan<byte> rgba, int w, int h, byte[] blocks, AstcChannels channels, int maxThreads)
    {
        int bh = (h + 3) / 4;
        if (bh < 8 || maxThreads <= 1) { Encode(rgba, w, h, blocks, channels); return; }
        var src = rgba.ToArray();
        int bw = (w + 3) / 4;
        Parallel.For(0, bh, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, by =>
        {
            Span<byte> texels = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, w - 1), sy = Math.Min(by * 4 + y, h - 1);
                        src.AsSpan((sy * w + sx) * 4, 4).CopyTo(texels.Slice((y * 4 + x) * 4, 4));
                    }
                EncodeBlock(texels, blocks.AsSpan((by * bw + bx) * BlockBytes, BlockBytes), channels);
            }
        });
    }

    // ------------------------------------------------------------------ block configurations

    /// <param name="Cem">colour endpoint mode: 0 L, 4 LA, 8 RGB, 12 RGBA (all direct)</param>
    /// <param name="Ccs">dual plane: the component on the second plane (0-3), -1 = one plane</param>
    sealed class Config
    {
        public readonly int Cem, WeightLevels, Ccs, Nc, PlaneChannel, WeightBits, EndpointLevels;
        public readonly bool Dual;
        public readonly Ise.EndpointTable Eq;
        public readonly int[] Wu;

        public Config(int cem, int weightLevels, int ccs)
        {
            Cem = cem; WeightLevels = weightLevels; Ccs = ccs;
            Nc = cem switch { 0 => 1, 4 => 2, 8 => 3, _ => 4 };
            Dual = ccs >= 0;
            PlaneChannel = Dual ? (Nc == 2 ? 1 : ccs) : -1; // LA: alpha is value index 1
            WeightBits = Ise.BitCount(16 * (Dual ? 2 : 1), weightLevels);
            EndpointLevels = Ise.ColorLevels(128 - 17 - WeightBits - (Dual ? 2 : 0), Nc * 2);
            Eq = Ise.Endpoint(EndpointLevels);
            Wu = Ise.WeightUnquant(weightLevels);
        }
    }

    // tried in this order; the first that is good enough wins (GoodEnough)
    static readonly Config[] RgbConfigs = [new(8, 8, -1), new(8, 16, -1), new(8, 4, -1)];
    static readonly Config[] RgbaConfigs = [new(12, 8, -1), new(12, 4, 3), new(12, 4, -1)];
    static readonly Config[] LConfigs = [new(0, 16, -1)];
    static readonly Config[] LaConfigs = [new(4, 4, 3), new(4, 8, -1), new(4, 8, 3)];
    /// <summary>Average squared error per texel and channel below which no further configuration is tried (~46 dB).</summary>
    const float GoodEnough = 1.5f;

    /// <summary>Encodes one block of 16 RGBA8 texels (row order).</summary>
    public static void EncodeBlock(ReadOnlySpan<byte> texels, Span<byte> block, AstcChannels channels)
    {
        // the channel values the block keeps, in endpoint component order
        Span<float> v = stackalloc float[64];
        int nc;
        Config[] configs;
        switch (channels)
        {
            case AstcChannels.R:
                nc = 1;
                for (int i = 0; i < 16; i++) v[i] = texels[i * 4];
                configs = LConfigs;
                break;
            case AstcChannels.Rg:
                nc = 2;
                for (int i = 0; i < 16; i++) { v[i * 2] = texels[i * 4]; v[i * 2 + 1] = texels[i * 4 + 1]; }
                configs = LaConfigs;
                break;
            default:
            {
                bool opaque = true;
                for (int i = 0; i < 16; i++) opaque &= texels[i * 4 + 3] == 255;
                nc = opaque ? 3 : 4;
                for (int i = 0; i < 16; i++)
                    for (int c = 0; c < nc; c++) v[i * nc + c] = texels[i * 4 + c];
                configs = opaque ? RgbConfigs : RgbaConfigs;
                break;
            }
        }
        if (IsUniform(v, nc))
        {
            WriteVoidExtent(block, channels, texels);
            return;
        }
        Span<byte> best = stackalloc byte[16];
        Span<byte> trial = stackalloc byte[16];
        // principal-axis endpoints, per plane layout (shared by the configurations of that layout)
        Span<float> axis = stackalloc float[16];
        int axisFor0 = int.MinValue, axisFor1 = int.MinValue;
        float bestErr = float.MaxValue;
        foreach (var cfg in configs)
        {
            int slot = axisFor0 == cfg.PlaneChannel ? 0 : axisFor1 == cfg.PlaneChannel ? 1 : axisFor0 == int.MinValue ? 0 : 1;
            var e = axis.Slice(slot * 8, 8);
            if ((slot == 0 ? axisFor0 : axisFor1) != cfg.PlaneChannel)
            {
                FitAxis(v, nc, cfg.PlaneChannel, e[..4], e[4..]);
                if (cfg.PlaneChannel >= 0) FitSingle(v, nc, cfg.PlaneChannel, e[..4], e[4..]);
                if (slot == 0) axisFor0 = cfg.PlaneChannel; else axisFor1 = cfg.PlaneChannel;
            }
            float err = EncodeConfig(v, nc, cfg, e[..4], e[4..], trial);
            if (err < bestErr) { bestErr = err; trial.CopyTo(best); }
            if (bestErr <= GoodEnough * 16 * nc) break;
        }
        best.CopyTo(block);
    }

    static bool IsUniform(ReadOnlySpan<float> v, int nc)
    {
        for (int i = 1; i < 16; i++)
            for (int c = 0; c < nc; c++)
                if (v[i * nc + c] != v[c]) return false;
        return true;
    }

    static void WriteVoidExtent(Span<byte> block, AstcChannels channels, ReadOnlySpan<byte> t)
    {
        // block mode 0x1FC, LDR, reserved bits 11, all extent coordinates 1s; colour as UNORM16
        ulong lo = 0xDFCul | (0xFFFFFFFFFFFFFul << 12);
        int r = t[0], g = t[1], b = t[2], a = t[3];
        if (channels == AstcChannels.R) { g = b = r; a = 255; }
        else if (channels == AstcChannels.Rg) { a = g; g = b = r; }
        ulong hi = (uint)(r * 257) | (ulong)(uint)(g * 257) << 16 | (ulong)(uint)(b * 257) << 32 | (ulong)(uint)(a * 257) << 48;
        Unsafe.WriteUnaligned(ref block[0], lo);
        Unsafe.WriteUnaligned(ref block[8], hi);
    }

    /// <summary>Encodes the block with one configuration into <paramref name="block"/>; returns the squared error.</summary>
    static float EncodeConfig(ReadOnlySpan<float> v, int nc, Config cfg, ReadOnlySpan<float> axis0, ReadOnlySpan<float> axis1, Span<byte> block)
    {
        bool dual = cfg.Dual;
        int planeChannel = cfg.PlaneChannel;
        int weightBits = cfg.WeightBits;
        int endpointLevels = cfg.EndpointLevels;
        if (endpointLevels < 6) return float.MaxValue;
        var eq = cfg.Eq;

        Span<float> e0 = stackalloc float[4], e1 = stackalloc float[4];
        Span<int> q0 = stackalloc int[4], q1 = stackalloc int[4];
        Span<int> w = stackalloc int[32];
        var wu = cfg.Wu;
        float bestErr = float.MaxValue;
        Span<byte> trial = stackalloc byte[16];
        // pass 0: principal-axis endpoints; pass 1: least-squares endpoints for the pass-0 weights
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 0)
            {
                axis0.CopyTo(e0);
                axis1.CopyTo(e1);
            }
            else if (bestErr <= GoodEnough * 16 * nc || !Refine(v, nc, planeChannel, w, wu, e0, e1)) break;
            for (int c = 0; c < nc; c++)
            {
                q0[c] = eq.Quantize[ClampByte(e0[c])];
                q1[c] = eq.Quantize[ClampByte(e1[c])];
            }
            // RGB(A) direct: e1 must not sum below e0, or the decoder applies blue contraction
            if (nc >= 3)
            {
                int s0 = eq.Unquant[q0[0]] + eq.Unquant[q0[1]] + eq.Unquant[q0[2]];
                int s1 = eq.Unquant[q1[0]] + eq.Unquant[q1[1]] + eq.Unquant[q1[2]];
                if (s1 < s0)
                    for (int c = 0; c < nc; c++) (q0[c], q1[c]) = (q1[c], q0[c]);
            }
            float err = ChooseWeights(v, nc, planeChannel, q0, q1, eq.Unquant, wu, w);
            if (err < bestErr)
            {
                bestErr = err;
                Pack(trial, cfg, nc, dual, weightBits, endpointLevels, q0, q1, w);
                trial.CopyTo(block);
            }
            if (err == 0) break;
        }
        return bestErr;
    }

    static int ClampByte(float f) => f <= 0 ? 0 : f >= 255 ? 255 : (int)(f + 0.5f);

    /// <summary>Endpoints along the principal axis of the channels other than <paramref name="skip"/>.</summary>
    static void FitAxis(ReadOnlySpan<float> v, int nc, int skip, Span<float> e0, Span<float> e1)
    {
        // (the assembly skips locals init: accumulators are cleared explicitly)
        Span<float> mean = stackalloc float[4];
        mean.Clear();
        for (int i = 0; i < 16; i++)
            for (int c = 0; c < nc; c++) mean[c] += v[i * nc + c];
        for (int c = 0; c < nc; c++) mean[c] /= 16;
        Span<float> cov = stackalloc float[16];
        cov.Clear();
        for (int i = 0; i < 16; i++)
            for (int a = 0; a < nc; a++)
            {
                if (a == skip) continue;
                float da = v[i * nc + a] - mean[a];
                for (int b = a; b < nc; b++)
                {
                    if (b == skip) continue;
                    cov[a * 4 + b] += da * (v[i * nc + b] - mean[b]);
                }
            }
        for (int a = 0; a < nc; a++) for (int b = 0; b < a; b++) cov[a * 4 + b] = cov[b * 4 + a];
        Span<float> axis = stackalloc float[4];
        for (int c = 0; c < nc; c++) axis[c] = c == skip ? 0 : 1;
        Span<float> next = stackalloc float[4];
        for (int it = 0; it < 8; it++)
        {
            float len = 0;
            for (int a = 0; a < nc; a++)
            {
                float s = 0;
                for (int b = 0; b < nc; b++) s += cov[a * 4 + b] * axis[b];
                next[a] = a == skip ? 0 : s;
                len = MathF.Max(len, MathF.Abs(next[a]));
            }
            if (len < 1e-6f) break;
            for (int a = 0; a < nc; a++) axis[a] = next[a] / len;
        }
        float tmin = float.MaxValue, tmax = float.MinValue, norm = 0;
        for (int c = 0; c < nc; c++) norm += axis[c] * axis[c];
        if (norm < 1e-12f)
        {
            for (int c = 0; c < nc; c++) if (c != skip) e0[c] = e1[c] = mean[c];
            return;
        }
        for (int i = 0; i < 16; i++)
        {
            float t = 0;
            for (int c = 0; c < nc; c++) if (c != skip) t += (v[i * nc + c] - mean[c]) * axis[c];
            t /= norm;
            tmin = MathF.Min(tmin, t);
            tmax = MathF.Max(tmax, t);
        }
        for (int c = 0; c < nc; c++)
        {
            if (c == skip) continue;
            e0[c] = mean[c] + axis[c] * tmin;
            e1[c] = mean[c] + axis[c] * tmax;
        }
    }

    /// <summary>The second plane's channel: its own minimum and maximum.</summary>
    static void FitSingle(ReadOnlySpan<float> v, int nc, int c, Span<float> e0, Span<float> e1)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < 16; i++) { lo = MathF.Min(lo, v[i * nc + c]); hi = MathF.Max(hi, v[i * nc + c]); }
        e0[c] = lo;
        e1[c] = hi;
    }

    /// <summary>Least-squares endpoints for fixed weights, per channel. False when degenerate.</summary>
    static bool Refine(ReadOnlySpan<float> v, int nc, int planeChannel, ReadOnlySpan<int> w, ReadOnlySpan<int> wu, Span<float> e0, Span<float> e1)
    {
        bool any = false;
        Span<float> ax = stackalloc float[4], bx = stackalloc float[4];
        for (int plane = 0; plane < (planeChannel >= 0 ? 2 : 1); plane++)
        {
            float aa = 0, ab = 0, bb = 0;
            ax.Clear();
            bx.Clear();
            int stride = planeChannel >= 0 ? 2 : 1;
            for (int i = 0; i < 16; i++)
            {
                float t = wu[w[i * stride + plane]] / 64f, s = 1 - t;
                aa += s * s; ab += s * t; bb += t * t;
                for (int c = 0; c < nc; c++)
                {
                    if ((c == planeChannel) != (plane == 1)) continue;
                    ax[c] += s * v[i * nc + c];
                    bx[c] += t * v[i * nc + c];
                }
            }
            float det = aa * bb - ab * ab;
            if (MathF.Abs(det) < 1e-6f) continue;
            for (int c = 0; c < nc; c++)
            {
                if ((c == planeChannel) != (plane == 1)) continue;
                e0[c] = (bb * ax[c] - ab * bx[c]) / det;
                e1[c] = (aa * bx[c] - ab * ax[c]) / det;
            }
            any = true;
        }
        return any;
    }

    /// <summary>Best weight per texel (and plane) for the quantized endpoints; returns the squared error.</summary>
    static float ChooseWeights(ReadOnlySpan<float> v, int nc, int planeChannel, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1,
        ReadOnlySpan<int> unq, ReadOnlySpan<int> wu, Span<int> w)
    {
        int planes = planeChannel >= 0 ? 2 : 1;
        int levels = wu.Length;
        Span<int> u0 = stackalloc int[4], u1 = stackalloc int[4];
        for (int c = 0; c < nc; c++) { u0[c] = unq[q0[c]]; u1[c] = unq[q1[c]]; }
        float total = 0;
        for (int i = 0; i < 16; i++)
            for (int plane = 0; plane < planes; plane++)
            {
                // project onto the endpoint line, then check the neighbouring levels exactly
                float num = 0, den = 0;
                for (int c = 0; c < nc; c++)
                {
                    if (planes == 2 && (c == planeChannel) != (plane == 1)) continue;
                    float d = u1[c] - u0[c];
                    num += (v[i * nc + c] - u0[c]) * d;
                    den += d * d;
                }
                float t = den > 0 ? Math.Clamp(num / den, 0, 1) : 0;
                int guess = (int)MathF.Round(t * (levels - 1));
                int bestW = 0;
                float bestE = float.MaxValue;
                for (int k = Math.Max(0, guess - 1); k <= Math.Min(levels - 1, guess + 1); k++)
                {
                    float e = 0;
                    for (int c = 0; c < nc; c++)
                    {
                        if (planes == 2 && (c == planeChannel) != (plane == 1)) continue;
                        float d = AstcDecoder.Interpolate(u0[c], u1[c], wu[k]) - v[i * nc + c];
                        e += d * d;
                    }
                    if (e < bestE) { bestE = e; bestW = k; }
                }
                w[i * planes + plane] = bestW;
                total += bestE;
            }
        return total;
    }

    static void Pack(Span<byte> block, Config cfg, int nc, bool dual, int weightBits, int endpointLevels, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, ReadOnlySpan<int> w)
    {
        var bits = new Bits128();
        var (r, hBit) = Ise.WeightRangeCode(cfg.WeightLevels);
        // 4x4 weight grid: block mode layout "B+4 x A+2" with A = 2, B = 0
        uint mode = (uint)((r >> 1) & 3) | (uint)((r & 1) << 4) | (2u << 5) | (uint)(hBit << 9) | (dual ? 1u << 10 : 0);
        bits.Write(0, 11, mode);
        bits.Write(11, 2, 0);          // one partition
        bits.Write(13, 4, (uint)cfg.Cem);
        // endpoint values: v0 = c0.e0, v1 = c0.e1, v2 = c1.e0 ...
        Span<int> vals = stackalloc int[8];
        for (int c = 0; c < nc; c++) { vals[c * 2] = q0[c]; vals[c * 2 + 1] = q1[c]; }
        Ise.Encode(ref bits, 17, vals[..(nc * 2)], endpointLevels);
        if (dual) bits.Write(128 - weightBits - 2, 2, (uint)cfg.Ccs);
        // weights: their own bit stream, stored bit-reversed from the top of the block
        var wbits = new Bits128();
        Ise.Encode(ref wbits, 0, w[..(16 * (dual ? 2 : 1))], cfg.WeightLevels);
        bits.OrReversed(wbits);
        bits.Store(block);
    }
}

/// <summary>128-bit little-endian bit buffer.</summary>
struct Bits128
{
    public ulong Lo, Hi;

    public void Write(int pos, int n, uint value)
    {
        if (n <= 0 || pos >= 128) return;
        ulong v = value & (n >= 32 ? 0xFFFFFFFFul : (1ul << n) - 1);
        if (pos < 64)
        {
            Lo |= v << pos;
            if (pos + n > 64) Hi |= v >> (64 - pos);
        }
        else Hi |= v << (pos - 64);
    }

    public readonly uint Read(int pos, int n)
    {
        if (n <= 0 || pos >= 128) return 0;
        ulong v = pos < 64 ? (Lo >> pos) | (pos > 0 ? Hi << (64 - pos) : 0) : Hi >> (pos - 64);
        return (uint)(v & (n >= 32 ? 0xFFFFFFFFul : (1ul << n) - 1));
    }

    static ulong Reverse(ulong x)
    {
        // swap ever larger groups: bits, pairs, nibbles, then the byte order
        x = ((x >> 1) & 0x5555555555555555ul) | ((x & 0x5555555555555555ul) << 1);
        x = ((x >> 2) & 0x3333333333333333ul) | ((x & 0x3333333333333333ul) << 2);
        x = ((x >> 4) & 0x0F0F0F0F0F0F0F0Ful) | ((x & 0x0F0F0F0F0F0F0F0Ful) << 4);
        return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(x);
    }

    /// <summary>ORs in <paramref name="other"/> with its bit order reversed (bit 0 becomes bit 127).</summary>
    public void OrReversed(in Bits128 other)
    {
        Lo |= Reverse(other.Hi);
        Hi |= Reverse(other.Lo);
    }

    public readonly Bits128 Reversed() => new() { Lo = Reverse(Hi), Hi = Reverse(Lo) };

    public readonly void Store(Span<byte> b)
    {
        Unsafe.WriteUnaligned(ref b[0], Lo);
        Unsafe.WriteUnaligned(ref b[8], Hi);
    }

    public static Bits128 Load(ReadOnlySpan<byte> b) => new() { Lo = Unsafe.ReadUnaligned<ulong>(ref Unsafe.AsRef(in b[0])), Hi = Unsafe.ReadUnaligned<ulong>(ref Unsafe.AsRef(in b[8])) };
}

/// <summary>Bounded integer sequence encoding (ASTC specification C.2.12) and the quantization tables.</summary>
static class Ise
{
    /// <summary>All ranges, largest first: (levels, bits, trit, quint).</summary>
    static readonly (int Levels, int Bits, bool Trit, bool Quint)[] Ranges =
    [
        (256, 8, false, false), (192, 6, true, false), (160, 5, false, true), (128, 7, false, false), (96, 5, true, false),
        (80, 4, false, true), (64, 6, false, false), (48, 4, true, false), (40, 3, false, true), (32, 5, false, false),
        (24, 3, true, false), (20, 2, false, true), (16, 4, false, false), (12, 2, true, false), (10, 1, false, true),
        (8, 3, false, false), (6, 1, true, false), (5, 0, false, true), (4, 2, false, false), (3, 0, true, false), (2, 1, false, false),
    ];

    static (int Bits, bool Trit, bool Quint) Info(int levels)
    {
        foreach (var r in Ranges) if (r.Levels == levels) return (r.Bits, r.Trit, r.Quint);
        throw new ArgumentException($"no ISE range with {levels} levels");
    }

    public static int BitCount(int count, int levels)
    {
        var (b, t, q) = Info(levels);
        return count * b + (t ? (count * 8 + 4) / 5 : q ? (count * 7 + 2) / 3 : 0);
    }

    /// <summary>The endpoint range a decoder derives from the bits left for <paramref name="values"/> values.</summary>
    public static int ColorLevels(int bits, int values)
    {
        foreach (var r in Ranges)
            if (BitCount(values, r.Levels) <= bits) return r.Levels;
        return 0;
    }

    /// <summary>Block mode R (3 bits) and H for a binary weight range.</summary>
    public static (int R, int H) WeightRangeCode(int levels) => levels switch
    {
        2 => (2, 0), 3 => (3, 0), 4 => (4, 0), 5 => (5, 0), 6 => (6, 0), 8 => (7, 0),
        10 => (2, 1), 12 => (3, 1), 16 => (4, 1), 20 => (5, 1), 24 => (6, 1), 32 => (7, 1),
        _ => throw new ArgumentException($"no weight range with {levels} levels"),
    };

    public static int WeightLevelsFromCode(int r, int h) => (r, h) switch
    {
        (2, 0) => 2, (3, 0) => 3, (4, 0) => 4, (5, 0) => 5, (6, 0) => 6, (7, 0) => 8,
        (2, 1) => 10, (3, 1) => 12, (4, 1) => 16, (5, 1) => 20, (6, 1) => 24, (7, 1) => 32,
        _ => 0,
    };

    static readonly Dictionary<int, int[]> WeightTables = new();

    /// <summary>Weight index -> 0..64 for the binary weight ranges the encoder uses (bit replication to 6 bits, then
    /// values above 32 plus one).</summary>
    public static int[] WeightUnquant(int levels)
    {
        lock (WeightTables)
        {
            if (WeightTables.TryGetValue(levels, out var t)) return t;
            var (bits, trit, quint) = Info(levels);
            if (trit || quint) throw new NotSupportedException($"weight range {levels} (trit/quint weights are not used)");
            t = new int[levels];
            for (int i = 0; i < levels; i++)
            {
                int u = 0, filled = 0;
                while (filled < 6) { u = (u << bits) | i; filled += bits; }
                u >>= filled - 6;
                t[i] = u > 32 ? u + 1 : u;
            }
            return WeightTables[levels] = t;
        }
    }

    public sealed record EndpointTable(int[] Unquant, int[] Quantize);
    static readonly Dictionary<int, EndpointTable> EndpointTables = new();

    /// <summary>Endpoint quantization of a range: index -> 0..255, and 0..255 -> nearest index.</summary>
    public static EndpointTable Endpoint(int levels)
    {
        lock (EndpointTables)
        {
            if (EndpointTables.TryGetValue(levels, out var e)) return e;
            var unq = new int[levels];
            for (int i = 0; i < levels; i++) unq[i] = UnquantEndpoint(i, levels);
            var q = new int[256];
            for (int x = 0; x < 256; x++)
            {
                int best = 0, bestD = int.MaxValue;
                for (int i = 0; i < levels; i++)
                {
                    int d = Math.Abs(unq[i] - x);
                    if (d < bestD) { bestD = d; best = i; }
                }
                q[x] = best;
            }
            return EndpointTables[levels] = new EndpointTable(unq, q);
        }
    }

    /// <summary>Colour endpoint unquantization (specification C.2.13).</summary>
    public static int UnquantEndpoint(int v, int levels)
    {
        var (bits, trit, quint) = Info(levels);
        if (!trit && !quint)
        {
            int u = 0, filled = 0;
            while (filled < 8) { u = (u << bits) | v; filled += bits; }
            return u >> (filled - 8);
        }
        int d = v >> bits, m = v & ((1 << bits) - 1);
        int a = (m & 1) != 0 ? 0x1FF : 0;
        int b1 = (m >> 1) & 1, c1 = (m >> 2) & 1, d1 = (m >> 3) & 1, e1 = (m >> 4) & 1, f1 = (m >> 5) & 1;
        int bv, cv;
        // B is a 9-bit pattern written MSB first in the specification
        static int P(string pattern, int b, int c, int d, int e, int f)
        {
            int r = 0;
            foreach (char ch in pattern)
                r = (r << 1) | ch switch { '0' => 0, 'b' => b, 'c' => c, 'd' => d, 'e' => e, 'f' => f, _ => 0 };
            return r;
        }
        if (trit)
        {
            (bv, cv) = bits switch
            {
                1 => (0, 204),
                2 => (P("b000b0bb0", b1, c1, d1, e1, f1), 93),
                3 => (P("cb000cbcb", b1, c1, d1, e1, f1), 44),
                4 => (P("dcb000dcb", b1, c1, d1, e1, f1), 22),
                5 => (P("edcb000ed", b1, c1, d1, e1, f1), 11),
                _ => (P("fedcb000f", b1, c1, d1, e1, f1), 5),
            };
        }
        else
        {
            (bv, cv) = bits switch
            {
                1 => (0, 113),
                2 => (P("b0000bb00", b1, c1, d1, e1, f1), 54),
                3 => (P("cb0000cbc", b1, c1, d1, e1, f1), 26),
                4 => (P("dcb0000dc", b1, c1, d1, e1, f1), 13),
                _ => (P("edcb0000e", b1, c1, d1, e1, f1), 6),
            };
        }
        int t = d * cv + bv;
        t ^= a;
        return (a & 0x80) | (t >> 2);
    }

    // trit/quint packing: decode tables from the specification, encode tables by search (with the bits a short last
    // group omits kept zero, as the decoder assumes)
    static readonly int[,] TritOf = new int[256, 5];
    static readonly int[,] QuintOf = new int[128, 3];
    static readonly Dictionary<int, int>[] TritEnc = new Dictionary<int, int>[6], QuintEnc = new Dictionary<int, int>[4];
    static readonly int[] TritBitsPresent = [0, 2, 4, 5, 7, 8], QuintBitsPresent = [0, 3, 5, 7];

    static Ise()
    {
        for (int T = 0; T < 256; T++)
        {
            int t0, t1, t2, t3, t4, C;
            if (((T >> 2) & 7) == 7)
            {
                C = (((T >> 5) & 7) << 2) | (T & 3);
                t4 = 2; t3 = 2;
            }
            else
            {
                C = T & 0x1F;
                if (((T >> 5) & 3) == 3) { t4 = 2; t3 = (T >> 7) & 1; }
                else { t4 = (T >> 7) & 1; t3 = (T >> 5) & 3; }
            }
            if ((C & 3) == 3) { t2 = 2; t1 = (C >> 4) & 1; t0 = (((C >> 3) & 1) << 1) | (((C >> 2) & 1) & ~((C >> 3) & 1) & 1); }
            else if (((C >> 2) & 3) == 3) { t2 = 2; t1 = 2; t0 = C & 3; }
            else { t2 = (C >> 4) & 1; t1 = (C >> 2) & 3; t0 = (((C >> 1) & 1) << 1) | ((C & 1) & ~((C >> 1) & 1) & 1); }
            TritOf[T, 0] = t0; TritOf[T, 1] = t1; TritOf[T, 2] = t2; TritOf[T, 3] = t3; TritOf[T, 4] = t4;
        }
        for (int Q = 0; Q < 128; Q++)
        {
            int q0, q1, q2;
            if (((Q >> 1) & 3) == 3 && ((Q >> 5) & 3) == 0)
            {
                int b0 = Q & 1, b3 = (Q >> 3) & 1, b4 = (Q >> 4) & 1;
                q2 = (b0 << 2) | ((b4 & ~b0 & 1) << 1) | (b3 & ~b0 & 1);
                q1 = 4; q0 = 4;
            }
            else
            {
                int C;
                if (((Q >> 1) & 3) == 3)
                {
                    q2 = 4;
                    C = (((Q >> 3) & 3) << 3) | ((~(Q >> 5) & 3) << 1) | (Q & 1);
                }
                else
                {
                    q2 = (Q >> 5) & 3;
                    C = Q & 0x1F;
                }
                if ((C & 7) == 5) { q1 = 4; q0 = (C >> 3) & 3; }
                else { q1 = (C >> 3) & 3; q0 = C & 7; }
            }
            QuintOf[Q, 0] = q0; QuintOf[Q, 1] = q1; QuintOf[Q, 2] = q2;
        }
        for (int k = 1; k <= 5; k++)
        {
            var d = TritEnc[k] = new Dictionary<int, int>();
            for (int T = 0; T < 256; T++)
            {
                if ((T >> TritBitsPresent[k]) != 0) continue;
                int key = 0;
                for (int j = k - 1; j >= 0; j--) key = key * 3 + TritOf[T, j];
                d.TryAdd(key, T);
            }
        }
        for (int k = 1; k <= 3; k++)
        {
            var d = QuintEnc[k] = new Dictionary<int, int>();
            for (int Q = 0; Q < 128; Q++)
            {
                if ((Q >> QuintBitsPresent[k]) != 0) continue;
                int key = 0;
                for (int j = k - 1; j >= 0; j--) key = key * 5 + QuintOf[Q, j];
                d.TryAdd(key, Q);
            }
        }
    }

    // bit positions of the trit/quint packed value after each value's low bits
    static readonly (int From, int Count)[] TritChunks = [(0, 2), (2, 2), (4, 1), (5, 2), (7, 1)];
    static readonly (int From, int Count)[] QuintChunks = [(0, 3), (3, 2), (5, 2)];

    public static void Encode(ref Bits128 bits, int pos, ReadOnlySpan<int> values, int levels)
    {
        var (b, trit, quint) = Info(levels);
        uint mask = (1u << b) - 1;
        if (!trit && !quint)
        {
            foreach (int v in values) { bits.Write(pos, b, (uint)v); pos += b; }
            return;
        }
        int group = trit ? 5 : 3;
        var chunks = trit ? TritChunks : QuintChunks;
        for (int g = 0; g < values.Length; g += group)
        {
            int k = Math.Min(group, values.Length - g);
            int key = 0, radix = trit ? 3 : 5;
            for (int j = k - 1; j >= 0; j--) key = key * radix + (values[g + j] >> b);
            int packed = trit ? TritEnc[k][key] : QuintEnc[k][key];
            for (int j = 0; j < k; j++)
            {
                bits.Write(pos, b, (uint)values[g + j] & mask); pos += b;
                var (from, count) = chunks[j];
                bits.Write(pos, count, (uint)(packed >> from)); pos += count;
            }
        }
    }

    public static void Decode(in Bits128 bits, int pos, Span<int> values, int levels)
    {
        var (b, trit, quint) = Info(levels);
        if (!trit && !quint)
        {
            for (int i = 0; i < values.Length; i++) { values[i] = (int)bits.Read(pos, b); pos += b; }
            return;
        }
        int group = trit ? 5 : 3;
        var chunks = trit ? TritChunks : QuintChunks;
        Span<int> low = stackalloc int[5];
        for (int g = 0; g < values.Length; g += group)
        {
            int k = Math.Min(group, values.Length - g);
            int packed = 0;
            for (int j = 0; j < k; j++)
            {
                low[j] = (int)bits.Read(pos, b); pos += b;
                var (from, count) = chunks[j];
                packed |= (int)bits.Read(pos, count) << from; pos += count;
            }
            for (int j = 0; j < k; j++)
                values[g + j] = ((trit ? TritOf[packed, j] : QuintOf[packed, j]) << b) | low[j];
        }
    }
}

/// <summary>
/// Software ASTC LDR decoder for what <see cref="AstcEncoder"/> produces (void-extent blocks; one partition, 4x4 weight
/// grid, one or two planes, direct L/LA/RGB/RGBA endpoints), to 8 bits per channel. Other blocks decode to magenta.
/// </summary>
public static class AstcDecoder
{
    /// <summary>ASTC LDR interpolation of two 8-bit endpoints (expanded to 16 bits) with a 0..64 weight, back to 8 bits.</summary>
    public static int Interpolate(int e0, int e1, int w)
    {
        int c0 = e0 * 257, c1 = e1 * 257;
        int c = (c0 * (64 - w) + c1 * w + 32) >> 6;
        return c >> 8;
    }

    public static void Decode(ReadOnlySpan<byte> blocks, int w, int h, Span<byte> rgba)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Span<byte> texels = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                DecodeBlock(blocks.Slice((by * bw + bx) * 16, 16), texels);
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int px = bx * 4 + x, py = by * 4 + y;
                        if (px < w && py < h) texels.Slice((y * 4 + x) * 4, 4).CopyTo(rgba.Slice((py * w + px) * 4, 4));
                    }
            }
    }

    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> texels)
    {
        var bits = Bits128.Load(block);
        uint mode = bits.Read(0, 11);
        if ((mode & 0x1FF) == 0x1FC)
        {
            for (int i = 0; i < 16; i++)
                for (int c = 0; c < 4; c++) texels[i * 4 + c] = (byte)(bits.Read(64 + c * 16, 16) >> 8);
            return;
        }
        // only the 4x4-grid single-partition layout the encoder writes
        int r = (int)(((mode & 3) << 1) | ((mode >> 4) & 1));
        int hBit = (int)((mode >> 9) & 1);
        bool dual = ((mode >> 10) & 1) != 0;
        int levels = Ise.WeightLevelsFromCode(r, hBit);
        if ((mode & 3) == 0 || ((mode >> 2) & 3) != 0 || ((mode >> 5) & 3) != 2 || ((mode >> 7) & 3) != 0 || levels == 0 || bits.Read(11, 2) != 0)
        {
            Magenta(texels);
            return;
        }
        int cem = (int)bits.Read(13, 4);
        int nc = cem switch { 0 => 1, 4 => 2, 8 => 3, 12 => 4, _ => 0 };
        if (nc == 0) { Magenta(texels); return; }
        int weightCount = 16 * (dual ? 2 : 1);
        int weightBits = Ise.BitCount(weightCount, levels);
        int colorBits = 128 - 17 - weightBits - (dual ? 2 : 0);
        int endpointLevels = Ise.ColorLevels(colorBits, nc * 2);
        Span<int> vals = stackalloc int[8];
        Ise.Decode(bits, 17, vals[..(nc * 2)], endpointLevels);
        var eq = Ise.Endpoint(endpointLevels);
        for (int i = 0; i < nc * 2; i++) vals[i] = eq.Unquant[vals[i]];
        int ccs = dual ? (int)bits.Read(128 - weightBits - 2, 2) : -1;
        Span<int> wv = stackalloc int[32];
        Ise.Decode(bits.Reversed(), 0, wv[..weightCount], levels);
        var wu = Ise.WeightUnquant(levels);

        Span<int> e0 = stackalloc int[4], e1 = stackalloc int[4];
        switch (cem)
        {
            case 0:
                e0[0] = e0[1] = e0[2] = vals[0]; e1[0] = e1[1] = e1[2] = vals[1]; e0[3] = e1[3] = 255;
                break;
            case 4:
                e0[0] = e0[1] = e0[2] = vals[0]; e1[0] = e1[1] = e1[2] = vals[1]; e0[3] = vals[2]; e1[3] = vals[3];
                break;
            default:
            {
                int a0 = cem == 12 ? vals[6] : 255, a1 = cem == 12 ? vals[7] : 255;
                int s0 = vals[0] + vals[2] + vals[4], s1 = vals[1] + vals[3] + vals[5];
                if (s1 >= s0)
                {
                    e0[0] = vals[0]; e0[1] = vals[2]; e0[2] = vals[4]; e0[3] = a0;
                    e1[0] = vals[1]; e1[1] = vals[3]; e1[2] = vals[5]; e1[3] = a1;
                }
                else
                {
                    // blue contraction, endpoints swapped
                    e0[0] = (vals[1] + vals[5]) >> 1; e0[1] = (vals[3] + vals[5]) >> 1; e0[2] = vals[5]; e0[3] = a1;
                    e1[0] = (vals[0] + vals[4]) >> 1; e1[1] = (vals[2] + vals[4]) >> 1; e1[2] = vals[4]; e1[3] = a0;
                }
                break;
            }
        }
        for (int i = 0; i < 16; i++)
            for (int c = 0; c < 4; c++)
            {
                // LA with two planes: the second plane drives alpha (CCS 3)
                int plane = dual && c == ccs ? 1 : 0;
                int wt = wu[wv[dual ? i * 2 + plane : i]];
                texels[i * 4 + c] = (byte)Interpolate(e0[c], e1[c], wt);
            }
    }

    static void Magenta(Span<byte> texels)
    {
        for (int i = 0; i < 16; i++) { texels[i * 4] = 255; texels[i * 4 + 1] = 0; texels[i * 4 + 2] = 255; texels[i * 4 + 3] = 255; }
    }

    /// <summary>Peak signal-to-noise ratio of two RGBA8 images over the given channels (dB; 99 when identical).</summary>
    public static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int channelMask = 0xF)
    {
        double sum = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if ((channelMask & (1 << (i & 3))) == 0) continue;
            double d = a[i] - b[i];
            sum += d * d;
            n++;
        }
        if (sum == 0) return 99;
        return 10 * Math.Log10(255.0 * 255.0 * n / sum);
    }
}
