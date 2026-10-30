using System.Diagnostics;
using VitaSharp.Core.Gpu;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// The ASTC 4x4 transcoder (Core/Gpu/Astc.cs) used for BC/PVRTC textures on GPUs without BCn: bounded integer
/// sequence encoding, endpoint quantization, round-trip quality (PSNR through the software decoder) and the disk cache.
/// </summary>
public sealed class AstcTests(ITestOutputHelper output)
{
    static readonly int[] AllLevels = [2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256];

    [Fact]
    public void IseRoundTripsEveryRangeAndLength()
    {
        var rng = new Random(1);
        foreach (int levels in AllLevels)
            for (int count = 1; count <= 12; count++)
            {
                var values = new int[count];
                for (int i = 0; i < count; i++) values[i] = rng.Next(levels);
                var bits = new Bits128();
                Ise.Encode(ref bits, 5, values, levels);
                var back = new int[count];
                Ise.Decode(bits, 5, back, levels);
                Assert.Equal(values, back);
                // nothing is written past the sequence's bit count
                int end = 5 + Ise.BitCount(count, levels);
                Assert.Equal(0u, bits.Read(end, Math.Min(32, 128 - end)));
            }
    }

    [Fact]
    public void EndpointRangesCoverTheByteRange()
    {
        foreach (int levels in AllLevels.Where(l => l >= 6))
        {
            var t = Ise.Endpoint(levels);
            Assert.Equal(levels, t.Unquant.Distinct().Count());
            Assert.Contains(0, t.Unquant);
            Assert.Contains(255, t.Unquant);
            for (int x = 0; x < 256; x++) Assert.InRange(Math.Abs(t.Unquant[t.Quantize[x]] - x), 0, 256 / (levels - 1) + 1);
        }
        // the decoder's choice of endpoint range for the bits that are left (RGB, 8 weight levels: 8-bit endpoints;
        // RGBA, 8 levels: the 192-level trit range)
        Assert.Equal(256, Ise.ColorLevels(128 - 17 - 48, 6));
        Assert.Equal(192, Ise.ColorLevels(128 - 17 - 48, 8));
        Assert.Equal(48, Ise.ColorLevels(128 - 17 - 64 - 2, 8));
    }

    [Fact]
    public void WeightsUnquantizeLikeTheSpecification()
    {
        Assert.Equal([0, 21, 43, 64], Ise.WeightUnquant(4));
        Assert.Equal([0, 9, 18, 27, 37, 46, 55, 64], Ise.WeightUnquant(8));
        Assert.Equal(64, Ise.WeightUnquant(16)[15]);
        Assert.Equal(4, Ise.WeightUnquant(16)[1]);
    }

    [Fact]
    public void UniformBlocksAreExactVoidExtents()
    {
        var img = new byte[8 * 8 * 4];
        for (int i = 0; i < 64; i++) { img[i * 4] = 12; img[i * 4 + 1] = 200; img[i * 4 + 2] = 99; img[i * 4 + 3] = 7; }
        var blocks = new byte[4 * 16];
        AstcEncoder.Encode(img, 8, 8, blocks, AstcChannels.Rgba);
        Assert.Equal(0xFCu, blocks[0]);
        var back = new byte[img.Length];
        AstcDecoder.Decode(blocks, 8, 8, back);
        Assert.Equal(img, back);
    }

    static byte[] Gradient(int w, int h)
    {
        var img = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                img[o] = (byte)(x * 255 / (w - 1));
                img[o + 1] = (byte)(y * 255 / (h - 1));
                img[o + 2] = (byte)((x + y) * 255 / (w + h - 2));
                img[o + 3] = 255;
            }
        return img;
    }

    /// <summary>A texture-like image: smooth colour regions, edges and fine noise, with an independent alpha.</summary>
    static byte[] Textured(int w, int h, bool alpha, int seed)
    {
        var rng = new Random(seed);
        var img = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                double fx = x / (double)w, fy = y / (double)h;
                bool stripe = ((x / 11) + (y / 7)) % 3 == 0;
                img[o] = (byte)Math.Clamp(128 + 100 * Math.Sin(fx * 9 + fy * 3) + rng.Next(-6, 7), 0, 255);
                img[o + 1] = (byte)Math.Clamp((stripe ? 200 : 60) + 40 * Math.Cos(fy * 7) + rng.Next(-6, 7), 0, 255);
                img[o + 2] = (byte)Math.Clamp(90 + 80 * Math.Sin(fx * fy * 20) + rng.Next(-6, 7), 0, 255);
                img[o + 3] = alpha ? (byte)Math.Clamp(255 * (0.5 + 0.5 * Math.Cos(fx * 13)) + rng.Next(-4, 5), 0, 255) : (byte)255;
            }
        return img;
    }

    /// <summary>What BC1/BC3 blocks decode to: per 4x4 block two colours and four (alpha: eight) levels between them.</summary>
    static byte[] BcLike(int w, int h, bool alpha, int seed)
    {
        var rng = new Random(seed);
        var img = new byte[w * h * 4];
        for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                var c0 = new int[] { rng.Next(256), rng.Next(256), rng.Next(256) };
                var c1 = new int[] { Math.Clamp(c0[0] + rng.Next(-60, 61), 0, 255), Math.Clamp(c0[1] + rng.Next(-60, 61), 0, 255), Math.Clamp(c0[2] + rng.Next(-60, 61), 0, 255) };
                int a0 = rng.Next(256), a1 = rng.Next(256);
                for (int y = by; y < Math.Min(by + 4, h); y++)
                    for (int x = bx; x < Math.Min(bx + 4, w); x++)
                    {
                        int k = rng.Next(4), ka = rng.Next(8), o = (y * w + x) * 4;
                        for (int c = 0; c < 3; c++) img[o + c] = (byte)((c0[c] * (3 - k) + c1[c] * k + 1) / 3);
                        img[o + 3] = alpha ? (byte)((a0 * (7 - ka) + a1 * ka + 3) / 7) : (byte)255;
                    }
            }
        return img;
    }

    static double RoundTrip(byte[] img, int w, int h, AstcChannels ch, int mask, out double mtexelsPerSecond)
    {
        var blocks = new byte[((w + 3) / 4) * ((h + 3) / 4) * 16];
        var sw = Stopwatch.StartNew();
        AstcEncoder.Encode(img, w, h, blocks, ch);
        mtexelsPerSecond = w * h / sw.Elapsed.TotalSeconds / 1e6;
        var back = new byte[img.Length];
        AstcDecoder.Decode(blocks, w, h, back);
        if (ch == AstcChannels.R) for (int i = 0; i < back.Length; i += 4) { back[i + 1] = img[i + 1]; back[i + 2] = img[i + 2]; back[i + 3] = img[i + 3]; }
        if (ch == AstcChannels.Rg) for (int i = 0; i < back.Length; i += 4) { back[i + 1] = back[i + 3]; back[i + 2] = img[i + 2]; back[i + 3] = img[i + 3]; }
        return AstcDecoder.Psnr(img, back, mask);
    }

    [Theory]
    [InlineData("gradient RGB", 0, AstcChannels.Rgba, 0x7, 42.0)]
    [InlineData("textured RGB", 1, AstcChannels.Rgba, 0x7, 33.0)]
    [InlineData("textured RGBA", 2, AstcChannels.Rgba, 0xF, 30.0)]
    [InlineData("BC4-like R", 3, AstcChannels.R, 0x1, 40.0)]
    [InlineData("BC5-like RG", 4, AstcChannels.Rg, 0x3, 33.0)]
    [InlineData("BC1 blocks", 5, AstcChannels.Rgba, 0x7, 42.0)]
    [InlineData("BC3 blocks (independent alpha)", 6, AstcChannels.Rgba, 0xF, 30.0)]
    public void RoundTripQuality(string name, int kind, AstcChannels ch, int mask, double minPsnr)
    {
        const int w = 128, h = 128;
        var img = kind switch { 0 => Gradient(w, h), 2 => Textured(w, h, true, 3), 5 => BcLike(w, h, false, 5), 6 => BcLike(w, h, true, 6), _ => Textured(w, h, false, kind) };
        double psnr = RoundTrip(img, w, h, ch, mask, out double speed);
        output.WriteLine($"{name}: PSNR {psnr:F2} dB, {speed:F2} M texels/s (one thread)");
        Assert.True(psnr >= minPsnr, $"{name}: PSNR {psnr:F2} dB < {minPsnr}");
    }

    [Fact]
    public void EncoderSpeed()
    {
        const int w = 256, h = 256;
        var rgb = Textured(w, h, false, 7);
        var rgba = Textured(w, h, true, 8);
        RoundTrip(rgb, w, h, AstcChannels.Rgba, 0x7, out _); // warm up
        RoundTrip(rgb, w, h, AstcChannels.Rgba, 0x7, out double sRgb);
        RoundTrip(rgba, w, h, AstcChannels.Rgba, 0xF, out double sRgba);
        RoundTrip(rgb, w, h, AstcChannels.R, 0x1, out double sR);
        RoundTrip(rgb, w, h, AstcChannels.Rg, 0x3, out double sRg);
        output.WriteLine($"one thread: RGB {sRgb:F2}, RGBA {sRgba:F2}, R {sR:F2}, RG {sRg:F2} M texels/s");
        Assert.True(sRgb > 0.1 && sRgba > 0.1);
    }

    [Fact]
    public void OddSizesAndTinyMipsEncode()
    {
        // (smooth content: four unrelated RGBA texels in one block are beyond any one-partition encoding)
        foreach (var (w, h) in new[] { (1, 1), (2, 2), (3, 5), (17, 9) })
        {
            var big = Gradient(64, 64);
            var img = new byte[w * h * 4];
            for (int y = 0; y < h; y++) big.AsSpan(y * 64 * 4, w * 4).CopyTo(img.AsSpan(y * w * 4));
            double psnr = RoundTrip(img, w, h, AstcChannels.Rgba, 0xF, out _);
            Assert.True(psnr >= 30, $"{w}x{h}: {psnr:F1} dB");
        }
    }

    [Fact]
    public void DiskCacheStoresChecksAndEvictsOldest()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"vs-astc-{Guid.NewGuid():N}");
        try
        {
            var cache = new TranscodeDiskCache(dir, 1 << 20);
            var a = new byte[300_000];
            new Random(2).NextBytes(a);
            cache.Put(1, 10, a);
            Assert.Equal(a, cache.TryGet(1, 10, a.Length));
            Assert.Null(cache.TryGet(1, 11, a.Length));   // other format/size
            Assert.Null(cache.TryGet(1, 10, a.Length + 16));
            Assert.Null(cache.TryGet(2, 10, a.Length));
            // fill past the 1 MB limit: the least recently used entries go first
            File.SetLastWriteTimeUtc(Path.Combine(dir, $"{1:x16}.astc"), DateTime.UtcNow.AddHours(-1));
            cache.Put(2, 10, a);
            cache.Put(3, 10, a);
            Assert.NotNull(cache.TryGet(2, 10, a.Length)); // touched: now the newest
            cache.Put(4, 10, a);
            string listing = string.Join("; ", new DirectoryInfo(dir).GetFiles().Select(f => $"{f.Name} {f.Length} {f.LastWriteTimeUtc:O}")) + " / " + cache.Summary();
            Assert.True(cache.TryGet(1, 10, a.Length) == null, listing);
            Assert.True(cache.TryGet(4, 10, a.Length) != null, listing);
            Assert.True(cache.Bytes <= 1 << 20);
            Assert.True(cache.Evictions >= 1);
        }
        finally { Directory.Delete(dir, true); }
    }
}
