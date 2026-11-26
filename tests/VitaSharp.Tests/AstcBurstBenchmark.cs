using System.Diagnostics;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Renderer.Vulkan;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// PC model of a level load on a phone without BC (Uncharted's first minute on the Redmi Note 14: 702 BC/PVRTC levels,
/// 15.4 M texels, 6.8 s of ASTC encodes on the GPU worker, frames with 1-2 s of texUpload): the GPU thread's time per
/// frame while the textures of a load arrive (a few new textures each frame, and a burst of all of them, as after a
/// save state load), in the synchronous mode and in the background mode, first session (empty disk cache) and second
/// session (filled). Prints the numbers; the assertions are the structural ones (no encode on the GPU thread in the
/// background mode), not times. Slow: not in the default suite.
/// </summary>
[Collection("AstcTranscoder")]
[Trait("Category", "Slow")]
public sealed class AstcBurstBenchmark(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-astc-burst-" + Guid.NewGuid().ToString("N"));
    private readonly AstcMode _mode = AstcTranscoder.Mode;
    private readonly bool _bc1Direct = AstcTranscoder.Bc1Direct;

    public void Dispose()
    {
        AstcTranscoder.WaitIdle(TimeSpan.FromMinutes(5));
        AstcTranscoder.Mode = _mode;
        AstcTranscoder.Bc1Direct = _bc1Direct;
        AstcTranscoder.Disk = null;
        AstcTranscoder.Reset();
        BcSoftware.ClearCache();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>The textures of the load: full mip chains, BC1 and BC3 (the formats in the Uncharted capture), about
    /// 15 M texels in all.</summary>
    private static List<(TexBase Format, uint W, uint H, byte[] Blocks)[]> Textures(int seed)
    {
        var rng = new Random(seed);
        var textures = new List<(TexBase, uint, uint, byte[])[]>();
        void Add(TexBase format, uint size, int count)
        {
            for (int t = 0; t < count; t++)
            {
                var levels = new List<(TexBase, uint, uint, byte[])>();
                for (uint s = size; s >= 1; s /= 2)
                {
                    int blocks = (int)(((s + 3) / 4) * ((s + 3) / 4));
                    byte[] data = new byte[blocks * (format == TexBase.UBC1 ? 8 : 16)];
                    rng.NextBytes(data);
                    if (format == TexBase.UBC1)
                    {
                        // as a texture compressor writes opaque BC1: four-colour blocks (c0 > c1) but for 1 in 20
                        for (int b = 0; b < blocks; b++)
                        {
                            int o = b * 8, c0 = data[o] | data[o + 1] << 8, c1 = data[o + 2] | data[o + 3] << 8;
                            if ((c0 <= c1) != (b % 20 == 0))
                            {
                                (data[o], data[o + 1], data[o + 2], data[o + 3]) = (data[o + 2], data[o + 3], data[o], data[o + 1]);
                            }
                        }
                    }

                    levels.Add((format, s, s, data));
                }

                textures.Add(levels.ToArray());
            }
        }

        Add(TexBase.UBC1, 512, 24);
        Add(TexBase.UBC3, 512, 8);
        Add(TexBase.UBC1, 256, 40);
        Add(TexBase.UBC3, 128, 60);
        return textures;
    }

    /// <summary>Uploads the textures, <paramref name="perFrame"/> new ones per frame, as the GPU thread does; returns
    /// the GPU thread's time of each frame (ms).</summary>
    private static List<double> Run(List<(TexBase Format, uint W, uint H, byte[] Blocks)[]> textures, int perFrame)
    {
        var frames = new List<double>();
        var pending = new List<ulong[]>();
        for (int i = 0; i < textures.Count; i += perFrame)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int j = i; j < Math.Min(i + perFrame, textures.Count); j++)
            {
                AstcTranscoder.BeginPlaceholders();
                foreach ((TexBase format, uint w, uint h, byte[] blocks) in textures[j])
                {
                    using Texels texels = AstcTranscoder.FromBc(format, blocks, w, h, AstcChannels.Rgba);
                }

                if (AstcTranscoder.EndPlaceholders() is { } keys)
                {
                    pending.Add(keys);
                }
            }

            frames.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }

        return frames;
    }

    private string Describe(string name, List<double> frames)
    {
        var sorted = frames.OrderBy(f => f).ToList();
        return $"{name}: {frames.Count} frames, GPU-thread total {frames.Sum():F0} ms, worst {sorted[^1]:F1} ms, median {sorted[sorted.Count / 2]:F2} ms, " +
               $"frames >50 ms {frames.Count(f => f > 50)}, >100 ms {frames.Count(f => f > 100)}; encoded on the GPU thread {AstcTranscoder.Encoded - AstcTranscoder.BackgroundEncoded}, placeholders {AstcTranscoder.Placeholders}, from disk {AstcTranscoder.DiskHits}, GPU thread waited for the transcoders {AstcTranscoder.GpuThreadWaitTicks * 1000.0 / Stopwatch.Frequency:F0} ms";
    }

    [Theory]
    [InlineData(4)]   // a streaming load: a few new textures per frame
    [InlineData(1000)] // a save state load: every texture in one frame
    public void GpuThreadTimeOfALoad(int perFrame)
    {
        List<(TexBase Format, uint W, uint H, byte[] Blocks)[]> textures = Textures(42);
        output.WriteLine($"{textures.Count} textures, {textures.Sum(t => t.Length)} levels, {textures.Sum(t => t.Sum(l => (long)l.W * l.H)) / 1e6:F1} M texels, {perFrame} per frame; transcoder threads {AstcTranscoder.Workers}, encode threads on the GPU thread {AstcTranscoder.Threads}");
        // before (GPU thread, every block searched), then each change on top of the previous one
        (string Name, AstcMode Mode, bool Bc1Direct, bool Help)[] variants =
        [
            ("before: GPU thread, search", AstcMode.GpuThread, false, false),
            ("GPU thread + BC1 direct", AstcMode.GpuThread, true, false),
            ("wait (GPU thread only waits) + BC1 direct", AstcMode.Wait, true, false),
            ("wait (GPU thread helps) + BC1 direct", AstcMode.Wait, true, true),
            ("placeholder (opt-in) + BC1 direct", AstcMode.Placeholder, true, false),
        ];
        bool help = AstcTranscoder.Help;
        foreach ((string name, AstcMode mode, bool bc1Direct, bool helps) in variants)
        {
            string disk = Path.Combine(_dir, $"{mode}-{bc1Direct}-{helps}");
            AstcTranscoder.Mode = mode;
            AstcTranscoder.Bc1Direct = bc1Direct;
            AstcTranscoder.Help = helps;
            for (int session = 1; session <= 2; session++)
            {
                AstcTranscoder.WaitIdle(TimeSpan.FromMinutes(5));
                AstcTranscoder.Reset();
                BcSoftware.ClearCache();
                AstcTranscoder.Disk = new TranscodeDiskCache(disk, 1L << 30);
                AstcTranscoder.Disk.BuildIndex(); // done at the title's start, before the first texture
                List<double> frames = Run(textures, perFrame);
                long encodedOnGpuThread = AstcTranscoder.Encoded - AstcTranscoder.BackgroundEncoded;
                output.WriteLine(Describe($"{name}, session {session}", frames));
                AstcTranscoder.WaitIdle(TimeSpan.FromMinutes(5));
                if (mode != AstcMode.GpuThread)
                {
                    Assert.Equal(0, encodedOnGpuThread);
                }

                if (session == 2)
                {
                    Assert.Equal(0, AstcTranscoder.Encoded); // everything from the disk cache
                }
            }
        }

        AstcTranscoder.Help = help;
    }
}
