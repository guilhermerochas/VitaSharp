using System.Reflection;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// Renderer paths beyond the plain draw, on the software Vulkan device with the texture workload of
/// <see cref="RendererTextureTests"/>: GXM MSAA downscale scenes in each emulation mode (they must resolve to the image a
/// one-sample scene gives), the per-title shader caches (a second session translates nothing), and debug mode (the
/// F10 capture with its draw trace, sampled textures, draw ring and replay ring, and the NaN/Inf scan of float colour
/// surfaces raising its anomaly).
/// </summary>
[Collection("guest")]
public sealed class RendererPathsTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;

    private static VulkanRenderer NewRenderer(string? cacheDir = null, string? msaa = null) => new(new VulkanRendererOptions
    {
        VSync = false,
        AsyncShaders = false,
        AsyncPipelines = false,
        ShaderCacheDir = cacheDir,
        Msaa = msaa,
    });

    /// <summary>Runs <paramref name="body"/> with <paramref name="renderer"/> as the GPU queue's backend of a fresh
    /// harness, then waits for the GPU and disposes the renderer.</summary>
    private void WithRenderer(string tag, VulkanRenderer renderer, Action<HleHarness> body)
    {
        using var h = new HleHarness(fx, tag);
        try
        {
            h.Emu.Gpu.Backend = renderer;
            body(h);
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }
    }

    // ---------------------------------------------------------------- MSAA

    /// <summary>The presented frame (RGBA) after one scene drawing the U8U8U8U8 textures (random texels).</summary>
    private byte[] MsaaScene(string? msaa, ushort multisample, out string mode)
    {
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-vkmsaa-" + Guid.NewGuid().ToString("N"));
        string m = "";
        try
        {
            var renderer = new VulkanRenderer(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                Msaa = msaa,
                DumpFramesDir = frames,
                DumpEvery = 1,
            });
            WithRenderer("vkmsaa", renderer, h =>
            {
                var w = new RendererTextureTests.Workload(h, multisample, scaleMode: multisample != 0 ? 1u : 0u);
                // a surface holding a pattern, and one triangle over its lower-left half sampling an RGBA8 texture
                for (uint i = 0; i < W * H; i++)
                {
                    GuestMemory.W32(w.Data + i * 4, 0xFF000000u | (i * 2654435761u >> 8));
                }

                float[] v = [-1, -1, 0.5f, 1, 1, -1, 0.5f, 1, -1, 1, 0.5f, 1];
                for (int i = 0; i < v.Length; i++)
                {
                    GuestMemory.Write(w.Verts + (uint)i * 4, v[i]);
                }

                uint rgba = w.Textures[w.TextureFormats.IndexOf(VitaSharp.Core.Gxm.TexBase.U8U8U8U8)];
                w.Textures.Clear();
                w.Textures.Add(rgba);
                Assert.Equal(1, w.Run(1));
                h.Emu.Gpu.WaitIdle();
                m = renderer.MsaaMode;
                w.Terminate();
            });
            mode = m;
            string last = Directory.GetFiles(frames, "frame_*.png").Order().Last();
            using var bmp = SkiaSharp.SKBitmap.Decode(last);
            Assert.Equal((W, H), (bmp.Width, bmp.Height));
            return bmp.Pixels.SelectMany(c => new[] { c.Red, c.Green, c.Blue }).ToArray();
        }
        finally
        {
            try
            {
                Directory.Delete(frames, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [SoftwareVulkanFact]
    public void MsaaDownscaleScenesResolveToTheOneSampleImageInEveryMode()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        // the scene loads the surface into every sample, draws the textured triangle and resolves: away from the
        // triangle's diagonal edge (where the samples of a pixel differ) every mode gives the one-sample image, the
        // texture inside and the pattern outside; along the edge only a band of a pixel or two may differ
        byte[] single = MsaaScene(null, 0, out _);
        static (byte, byte, byte) At(byte[] img, int x, int y) => (img[(y * W + x) * 3], img[(y * W + x) * 3 + 1], img[(y * W + x) * 3 + 2]);
        static (byte, byte, byte) Pattern(int x, int y)
        {
            uint p = unchecked((uint)(y * W + x) * 2654435761u) >> 8;
            return ((byte)p, (byte)(p >> 8), (byte)(p >> 16));
        }
        // y grows downwards: the triangle covers the lower-left half (x < y)
        Assert.Equal(Pattern(60, 3), At(single, 60, 3));
        int drawn = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < y - 2; x++)
            {
                if (At(single, x, y) != Pattern(x, y))
                {
                    drawn++;
                }
            }
        }

        Assert.True(drawn > (W - 3) * (H - 3) / 2 * 9 / 10, $"the triangle drew {drawn} pixels");
        foreach ((string mode, ushort samples) in new[] { ("native", (ushort)2), ("native", (ushort)1), ("ss", (ushort)2), ("off", (ushort)2) })
        {
            byte[] img = MsaaScene(mode, samples, out string? shown);
            Assert.Equal(mode, shown);
            int far = 0, edge = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    ((byte, byte, byte) a, (byte, byte, byte) b) = (At(single, x, y), At(img, x, y));
                    bool same = Math.Abs(a.Item1 - b.Item1) <= 2 && Math.Abs(a.Item2 - b.Item2) <= 2 && Math.Abs(a.Item3 - b.Item3) <= 2;
                    if (same)
                    {
                        continue;
                    }

                    if (Math.Abs(x + (H - 1 - y) - (W - 1)) <= 2)
                    {
                        edge++;
                    }
                    else
                    {
                        far++;
                    }
                }
            }

            Assert.True(far == 0, $"MSAA {mode} {samples}: {far} pixels away from the edge differ ({edge} on it)");
            Assert.True(edge <= 3 * W, $"MSAA {mode} {samples}: {edge} edge pixels differ");
        }
    }

    // ---------------------------------------------------------------- shader caches

    private static int Stat(VulkanRenderer r, string field) =>
        (int)typeof(VulkanRenderer).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!;

    [SoftwareVulkanFact]
    public void SecondSessionTakesItsShadersFromTheTitleCache()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string cache = Path.Combine(Path.GetTempPath(), "vitasharp-vkcache-" + Guid.NewGuid().ToString("N"));
        const string Title = "PCSA00077";
        try
        {
            (int Pack, int Misses) Session()
            {
                VulkanRenderer r = NewRenderer(cache);
                (int, int) stats = default;
                WithRenderer("vkcache", r, h =>
                {
                    r.BeginTitle(Title);
                    var w = new RendererTextureTests.Workload(h);
                    w.Run(16);
                    h.Emu.Gpu.WaitIdle();
                    stats = (Stat(r, "_statShaderPackHits"), Stat(r, "_statShaderMisses"));
                    w.Terminate();
                });
                return stats;
            }

            (int Pack, int Misses) first = Session();
            Assert.True(first.Misses >= 2, $"first session: {first.Misses} shaders translated");
            string dir = Path.Combine(cache, "titles", Title);
            foreach (string f in new[] { "shaders.pack", "pipelines.list", "programs.pack" })
            {
                Assert.True(File.Exists(Path.Combine(dir, f)) && new FileInfo(Path.Combine(dir, f)).Length > 0, $"{f} missing in {dir}");
            }

            (int Pack, int Misses) second = Session();
            Assert.Equal(0, second.Misses);
        }
        finally
        {
            try
            {
                Directory.Delete(cache, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- debug mode

    [SoftwareVulkanFact]
    public void DebugModeCaptureWritesTheTraceTexturesDrawRingAndReplay()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-vkdiag-" + Guid.NewGuid().ToString("N"));
        bool wasOn = DebugMode.Enabled;
        DebugMode.Enable();
        try
        {
            VulkanRenderer r = NewRenderer(Path.Combine(dir, "cache"));
            DiagnosticCaptureResult? result = null;
            var done = new ManualResetEventSlim();
            WithRenderer("vkdiag", r, h =>
            {
                r.BeginTitle("PCSA00078");
                var w = new RendererTextureTests.Workload(h);
                w.Textures.RemoveRange(8, w.Textures.Count - 8);
                for (int i = 0; i < 3; i++)
                {
                    w.Run(8); // a few frames in the rings
                }

                Assert.True(r.RequestDiagnosticCapture(Path.Combine(dir, "capture"), res => { result = res; done.Set(); }));
                Assert.False(r.RequestDiagnosticCapture(dir, _ => { }), "one capture at a time");
                for (int i = 0; i < 4 && !done.IsSet; i++)
                {
                    w.Run(8);
                    h.Emu.Gpu.WaitIdle();
                }
                Assert.True(done.Wait(30_000), "the capture did not complete");
                w.Terminate();
            });
            Assert.NotNull(result);
            Assert.True(result!.Error == null, result.Error);
            // the traced frame: its 8 draws, their textures, the screenshot and the render targets
            Assert.Equal(8, result.Draws);
            Assert.True(result.Textures >= 1, $"{result.Textures} textures");
            Assert.True(result.Screenshot);
            Assert.Equal((64u, 64u), (result.FrameWidth, result.FrameHeight));
            string cap = Path.Combine(dir, "capture");
            Assert.Equal(8, File.ReadAllLines(Path.Combine(cap, "draws.txt")).Count(l => l.StartsWith("[draw]")));
            Assert.True(File.Exists(Path.Combine(cap, "screenshot.png")));
            Assert.Contains("noise", File.ReadAllText(Path.Combine(cap, "textures", "textures.txt")));
            // the rings: every frame before the capture, each draw list with its 8 draws
            Assert.True(result.DrawRingFrames >= 3, $"{result.DrawRingFrames} draw ring frames");
            Assert.True(result.ReplayFrames >= 3, $"{result.ReplayFrames} replay frames");
            string[] ring = Directory.GetFiles(Path.Combine(cap, "drawring"), "frame_*.txt");
            Assert.NotEmpty(ring);
            Assert.Contains(ring, f => File.ReadAllLines(f).Count(l => l.StartsWith("[draw]")) == 8);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(cap, "replay"), "*.png"));
        }
        finally
        {
            if (!wasOn)
            {
                DebugMode.Disable();
            }

            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The anomalies a debug-mode session raised and its NaN scan count, after four frames on an
    /// F16F16F16F16 colour surface whose memory the CPU filled with <paramref name="half"/> before the first scene.</summary>
    private (List<string> Anomalies, long Scans) F16Session(ushort half)
    {
        VulkanRenderer r = NewRenderer();
        var anomalies = new List<string>();
        r.AnomalyDetected += (kind, detail, present) =>
        {
            lock (anomalies)
            {
                anomalies.Add(kind + ": " + detail);
            }
        };
        long scans = 0;
        WithRenderer("vknan", r, h =>
        {
            var w = new RendererTextureTests.Workload(h, colorFormat: 0x01000000);
            for (uint i = 0; i < W * H * 4; i++)
            {
                GuestMemory.Write(w.Data + i * 2, half);
            }
            // the draw covers the lower-left half: the rest of the surface keeps what the CPU wrote
            float[] v = [-1, -1, 0.5f, 1, 1, -1, 0.5f, 1, -1, 1, 0.5f, 1];
            for (int i = 0; i < v.Length; i++)
            {
                GuestMemory.Write(w.Verts + (uint)i * 4, v[i]);
            }

            w.Textures.RemoveRange(1, w.Textures.Count - 1);
            for (int i = 0; i < 4; i++)
            {
                w.Run(1);
                h.Emu.Gpu.WaitIdle();
            }
            w.Terminate();
            h.Emu.Gpu.WaitIdle();
            scans = (long)typeof(VulkanRenderer).GetField("_nanScans", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!;
        });
        // the anomaly handler runs on the thread pool
        for (int i = 0; i < 40; i++)
        {
            lock (anomalies)
            {
                if (anomalies.Count > 0)
                {
                    break;
                }
            }
            Thread.Sleep(50);
        }
        lock (anomalies)
        {
            return (anomalies.ToList(), scans);
        }
    }

    /// <summary>
    /// Debug mode's NaN/Inf scan of float colour surfaces: every scene on an F16F16F16F16 surface is scanned at its
    /// end; a surface the CPU filled with NaN halves (the scene loads the surface's memory, as the tiles do) raises the
    /// "nan" anomaly naming the target, and a clean one raises none.
    /// </summary>
    [SoftwareVulkanFact]
    public void NanInAFloatColourSurfaceRaisesTheAnomalyAndACleanOneDoesNot()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        bool wasOn = DebugMode.Enabled;
        DebugMode.Enable();
        try
        {
            (List<string>? nan, long nanScans) = F16Session(0x7E00);
            Assert.True(nanScans >= 4, $"{nanScans} scans");
            string hit = Assert.Single(nan);
            Assert.StartsWith(VulkanRenderer.NanAnomalyKind + ": rt 0x", hit);
            Assert.Contains("F16x4", hit);
            Assert.Contains("NaN", hit);

            (List<string>? clean, long cleanScans) = F16Session(0x3C00); // 1.0
            Assert.True(cleanScans >= 4, $"{cleanScans} scans");
            Assert.Empty(clean);
        }
        finally
        {
            if (!wasOn)
            {
                DebugMode.Disable();
            }
        }
    }
}
