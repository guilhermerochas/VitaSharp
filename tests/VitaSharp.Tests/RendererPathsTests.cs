using System.Diagnostics;
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
        // what a first session leaves to its draws is what these tests record and replay: nothing built ahead at program
        // load (FirstSessionCompilesAtProgramLoadAndTheGpuThreadWaitsForNothing covers that)
        CompileOnProgramLoad = false,
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
    private byte[] MsaaScene(string? msaa, ushort multisample, out string mode, float resScale = 0, string? gpuProfile = null)
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
                ResScale = resScale,
                GpuProfile = gpuProfile,
            });
            int scale = resScale >= 2 ? (int)resScale : 1; // frame dumps above native are at the internal resolution
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
                // a desktop device with dynamic rendering (lavapipe) begins native MSAA renderings in the primary, as the
                // single-sampled ones (Metal Gear's one-frame bands through secondaries); a tile-based one (MoltenVK on
                // the macOS runner, the Mali profile) or the render-pass path keeps them in secondaries
                if (m == "native" && multisample != 0)
                {
                    bool direct = !renderer.DefersRenderingsByDevice;
                    if (gpuProfile == "mali")
                    {
                        Assert.False(direct, "the tile-based Mali profile begins its multisampled renderings directly");
                    }
                    else if (gpuProfile == "desktop")
                    {
                        Assert.True(direct, "the desktop profile defers its multisampled renderings");
                    }

                    Assert.True(renderer.BeginsMsaaRenderingsDirectly == direct, $"{renderer.DeviceName}: multisampled renderings begun directly {renderer.BeginsMsaaRenderingsDirectly}, expected {direct}");
                    Assert.True((renderer.MsaaDirectRenderings > 0) == direct, $"{renderer.DeviceName}: {renderer.MsaaDirectRenderings} multisampled renderings begun directly, expected direct {direct}");
                }
                w.Terminate();
            });
            mode = m;
            string last = Directory.GetFiles(frames, "frame_*.png").Order().Last();
            using var bmp = SkiaSharp.SKBitmap.Decode(last);
            Assert.Equal((W * scale, H * scale), (bmp.Width, bmp.Height));
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
        // the device's own path, plus both paths forced: "desktop" (direct, also on a tile-based runner) and "mali"
        // (tile-based: the secondaries, also on a desktop runner)
        foreach ((string mode, ushort samples, string? profile) in new[]
        {
            ("native", (ushort)2, (string?)null), ("native", (ushort)1, null), ("ss", (ushort)2, null), ("off", (ushort)2, null),
            ("native", (ushort)2, "desktop"), ("native", (ushort)2, "mali"),
        })
        {
            byte[] img = MsaaScene(mode, samples, out string? shown, gpuProfile: profile);
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

            Assert.True(far == 0, $"MSAA {mode} {samples} {profile}: {far} pixels away from the edge differ ({edge} on it)");
            Assert.True(edge <= 3 * W, $"MSAA {mode} {samples} {profile}: {edge} edge pixels differ");
        }
    }

    /// <summary>
    /// The Android per-game "2x" internal resolution (d1a8f9b9) on the phone's renderer path (the Mali profile:
    /// tile-based scenes): the scene renders at twice the size and every 2x2 block shows what the native scene shows
    /// at that pixel, away from the triangle's edge.
    /// </summary>
    [SoftwareVulkanFact]
    public void AndroidTwoTimesResolutionRendersTheNativeImageAtDoubleSize()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        byte[] native = MsaaScene(null, 0, out _, gpuProfile: "mali");
        byte[] doubled = MsaaScene(null, 0, out _, resScale: 2, gpuProfile: "mali");
        Assert.Equal(native.Length * 4, doubled.Length);
        int far = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (x <= y + 2)
                {
                    continue; // the textured triangle (sampled at another density) and its edge
                }

                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int a = (y * W + x) * 3;
                        int b = ((y * 2 + dy) * W * 2 + x * 2 + dx) * 3;
                        if (Math.Abs(native[a] - doubled[b]) > 2 || Math.Abs(native[a + 1] - doubled[b + 1]) > 2 || Math.Abs(native[a + 2] - doubled[b + 2]) > 2)
                        {
                            far++;
                        }
                    }
                }
            }
        }

        Assert.True(far == 0, $"{far} pixels of the 2x image differ from the native one outside the triangle");
        // inside, the triangle was drawn at 2x too: most of its pixels are not the surface's pattern any more
        int drawn = 0;
        for (int y = 3; y < H; y++)
        {
            for (int x = 0; x < y - 2; x++)
            {
                int b = (y * 2 * W * 2 + x * 2) * 3;
                uint pattern = unchecked((uint)(y * W + x) * 2654435761u) >> 8;
                if ((doubled[b], doubled[b + 1], doubled[b + 2]) != ((byte)pattern, (byte)(pattern >> 8), (byte)(pattern >> 16)))
                {
                    drawn++;
                }
            }
        }

        Assert.True(drawn > (W - 3) * (H - 3) / 2 * 9 / 10, $"the 2x triangle drew {drawn} pixels");
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

    /// <summary>"Carregar o cache de shaders antes de jogar": with the option, the second session's recorded pipelines are all built before the
    /// game's first frame, so that frame translates no shader and builds no pipeline on the GPU thread. Without it the
    /// game starts at once and its first frame races the (below normal priority) prewarm.</summary>
    [SoftwareVulkanFact]
    public void ShaderCacheLoadBuildsTheRecordedPipelinesBeforeTheFirstFrame()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string cache = Path.Combine(Path.GetTempPath(), "vitasharp-shadercache-load-" + Guid.NewGuid().ToString("N"));
        const string Title = "PCSA00079";
        try
        {
            (bool Waited, int Prewarmed, int Progress, int GpuPipelines, int Misses) Session(bool loadCache)
            {
                VulkanRenderer r = NewRenderer(cache);
                (bool, int, int, int, int) stats = default;
                PropertyInfo hook = typeof(VulkanRenderer).GetProperty("PrewarmItemHook", BindingFlags.Static | BindingFlags.NonPublic)!;
                using var gate = new ManualResetEventSlim();
                using var reported = new ManualResetEventSlim();
                // the prewarm of this renderer is held until the wait has been seen
                hook.SetValue(null, new Action<VulkanRenderer>(x =>
                {
                    if (ReferenceEquals(x, r))
                    {
                        gate.Wait();
                    }
                }));
                try
                {
                    WithRenderer("shadercache-load", r, h =>
                    {
                        r.BeginTitle(Title);
                        int reports = 0;
                        bool waited = false;
                        if (loadCache)
                        {
                            Task<bool> wait = Task.Run(() => ((IGpuBackend)r).LoadShaderCacheBeforeStart(_ =>
                            {
                                reports++;
                                reported.Set();
                            }, CancellationToken.None));
                            // either the call returns or it reports progress while the prewarm is held
                            Assert.True(SpinWait.SpinUntil(() => wait.IsCompleted || reported.IsSet, 120_000), "neither returned nor reported");
                            bool heldTheGame = !wait.IsCompleted || !wait.Result;
                            gate.Set();
                            waited = wait.Result;
                            Assert.True(heldTheGame || !waited, "returned while the recorded pipelines were still being built");
                        }
                        gate.Set();
                        int prewarmed = Stat(r, "_statPrewarmed");
                        var w = new RendererTextureTests.Workload(h);
                        w.Run(1); // the game's first frame
                        h.Emu.Gpu.WaitIdle();
                        stats = (waited, prewarmed, reports, Stat(r, "_statGpuPipelines"), Stat(r, "_statShaderMisses"));
                        w.Terminate();
                    });
                }
                finally
                {
                    gate.Set();
                    hook.SetValue(null, null);
                }

                return stats;
            }

            (bool Waited, int Prewarmed, int Progress, int GpuPipelines, int Misses) first = Session(loadCache: true);
            Assert.False(first.Waited, "a first session has nothing recorded to wait for");
            Assert.True(first.GpuPipelines > 0 && first.Misses > 0, $"first session: {first.GpuPipelines} pipelines, {first.Misses} shaders on the GPU thread");

            (bool Waited, int Prewarmed, int Progress, int GpuPipelines, int Misses) second = Session(loadCache: true);
            Assert.True(second.Waited);
            Assert.True(second.Progress >= 1, "the wait reports its end");
            Assert.True(second.Prewarmed >= first.GpuPipelines, $"{second.Prewarmed} of {first.GpuPipelines} recorded pipelines built before the first frame");
            Assert.Equal(0, second.GpuPipelines);
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

    /// <summary>A programmable-blending draw of a one-sample scene renders in a fetch pass, with a pipeline made for its
    /// render pass. That pipeline is recorded in pipelines.list like any other, so after the shader cache load the second
    /// session builds no pipeline on the GPU thread (the replay of a dump built its 8 fetch-pass pipelines there in
    /// every session when they were left out).</summary>
    [SoftwareVulkanFact]
    public void SecondSessionBuildsNoFetchPassPipelineOnTheGpuThread()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string cache = Path.Combine(Path.GetTempPath(), "vitasharp-fetchlist-" + Guid.NewGuid().ToString("N"));
        const string Title = "PCSA00081";
        try
        {
            (int GpuPipelines, int FetchPass, int Prewarmed) Session()
            {
                VulkanRenderer r = NewRenderer(cache);
                (int, int, int) stats = default;
                WithRenderer("fetchlist", r, h =>
                {
                    r.BeginTitle(Title);
                    ((IGpuBackend)r).LoadShaderCacheBeforeStart(_ => { }, CancellationToken.None);
                    var w = new RendererTextureTests.Workload(h);
                    w.RunFetch();
                    w.Run(1);
                    h.Emu.Gpu.WaitIdle();
                    int[] kinds = (int[])typeof(VulkanRenderer).GetField("_statGpuPipelineKinds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!;
                    stats = (Stat(r, "_statGpuPipelines"), kinds[1], Stat(r, "_statPrewarmed"));
                    w.Terminate();
                });
                return stats;
            }

            (int GpuPipelines, int FetchPass, int Prewarmed) first = Session();
            Assert.True(first.FetchPass > 0, $"first session: no fetch-pass pipeline ({first.GpuPipelines} on the GPU thread)");
            (int GpuPipelines, int FetchPass, int Prewarmed) second = Session();
            Assert.True(second.Prewarmed >= first.GpuPipelines, $"{second.Prewarmed} of {first.GpuPipelines} pipelines prewarmed");
            Assert.Equal(0, second.FetchPass);
            Assert.Equal(0, second.GpuPipelines);
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

    /// <summary>
    /// "Compilar shaders ao carregar o programa": a first session (nothing recorded) whose programs are created before
    /// the draws, as a game does while loading. With the option the shaders and the pipeline are built on the shader
    /// workers when the programs are created, so the first frame translates no shader, builds no pipeline and waits for
    /// none on the GPU thread, also for textures whose exact format was not predicted (BC1/ETC1/P8 sample as four
    /// unsigned bytes like the predicted U8U8U8U8: the same shader) and for the fetch pass of a programmable-blending
    /// draw (built for the surface the game initialised). Without it the GPU thread builds them at the draw.
    /// Draw-time prefetch is off in both sessions, so nothing else can build them first.
    /// </summary>
    [SoftwareVulkanFact]
    public void FirstSessionCompilesAtProgramLoadAndTheGpuThreadWaitsForNothing()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        (int GpuPipelines, int Misses, int ShaderWaits, int PipelineWaits, int Aliases, int Built) Session(bool programLoad)
        {
            string cache = Path.Combine(Path.GetTempPath(), "vitasharp-programload-" + Guid.NewGuid().ToString("N"));
            (int, int, int, int, int, int) stats = default;
            try
            {
                var r = new VulkanRenderer(new VulkanRendererOptions
                {
                    VSync = false,
                    AsyncShaders = false,
                    AsyncPipelines = false,
                    CompileOnProgramLoad = programLoad,
                    ShaderCacheDir = cache,
                });
                WithRenderer("programload", r, h =>
                {
                    r.BeginTitle("PCSA00082");
                    var w = new RendererTextureTests.Workload(h); // creates the vertex and fragment programs
                    Assert.True(r.WaitForBackgroundCompiles(CancellationToken.None));
                    // 2D textures of the four-unsigned-byte class only: the predicted class (other classes are other shaders)
                    var keep = new List<uint>();
                    for (int i = 0; i < w.Textures.Count; i++)
                    {
                        if (w.TextureFormats[i] is Core.Gxm.TexBase.U8U8U8U8 or Core.Gxm.TexBase.UBC1 or Core.Gxm.TexBase.ETC1 or Core.Gxm.TexBase.P8
                            && !w.CubeTextures.Contains(w.Textures[i]))
                        {
                            keep.Add(w.Textures[i]);
                        }
                    }

                    Assert.True(keep.Count >= 4, $"{keep.Count} textures of the class");
                    w.Textures.Clear();
                    w.Textures.AddRange(keep);
                    w.RunFetch(); // a programmable-blending draw: in a one-sample scene it renders in a fetch pass
                    w.Run(keep.Count); // and one scene drawing them all
                    h.Emu.Gpu.WaitIdle();
                    (int Shaders, int Pipelines, int PipelinesBuilt, int Aliases) load = r.ProgramLoadStats;
                    stats = (Stat(r, "_statGpuPipelines"), Stat(r, "_statShaderMisses"), Stat(r, "_statAsyncWaited"), Stat(r, "_statPipelineWaits"), load.Aliases, load.PipelinesBuilt);
                    w.Terminate();
                });
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

            return stats;
        }

        (int GpuPipelines, int Misses, int ShaderWaits, int PipelineWaits, int Aliases, int Built) off = Session(programLoad: false);
        Assert.True(off.GpuPipelines > 0 && off.Misses > 0, $"without the option: {off.GpuPipelines} pipelines and {off.Misses} shaders on the GPU thread");

        (int GpuPipelines, int Misses, int ShaderWaits, int PipelineWaits, int Aliases, int Built) on = Session(programLoad: true);
        Assert.True(on.Built > 0, "no pipeline built at program load");
        Assert.True(on.Aliases > 0, "no draw served by the module of its canonical hints");
        Assert.Equal((0, 0, 0, 0), (on.GpuPipelines, on.Misses, on.ShaderWaits, on.PipelineWaits));
    }

    /// <summary>The GPU queue's backend for <see cref="DrawPrefetchBuildsTheFetchPassOfEachDrawBeforeItRuns"/>: forwards
    /// everything to the renderer and, before each draw runs, waits for the shader workers. The draw's prefetch (on
    /// submit, before it is queued) has then finished, as when the game submits a frame ahead of the GPU thread, and
    /// whatever the GPU thread still builds is what the prefetch did not predict.</summary>
    /// <param name="held">when given, every command waits for it first (the GPU thread held while the game submits)</param>
    /// <param name="waitForWorkers">false: the draws do not wait for the shader workers</param>
    private sealed class PrefetchedFirstBackend(VulkanRenderer renderer, ManualResetEventSlim? held = null, bool waitForWorkers = true) : IGpuBackend
    {
        private readonly IGpuBackend _inner = renderer;

        public void Execute(GpuCommand command)
        {
            held?.Wait();
            if (waitForWorkers && command is DrawCommand)
            {
                Assert.True(renderer.WaitForBackgroundCompiles(CancellationToken.None));
            }

            _inner.Execute(command);
        }

        public void Idle() => _inner.Idle();
        public void Attach(GpuQueue queue) => _inner.Attach(queue);
        public bool DeferSignal(Action signal) => _inner.DeferSignal(signal);
        public void BeginTitle(string titleId) => _inner.BeginTitle(titleId);
        public string? TitleId => _inner.TitleId;
        public bool LoadShaderCacheBeforeStart(Action<ShaderCacheLoadProgress>? progress, CancellationToken cancel) => _inner.LoadShaderCacheBeforeStart(progress, cancel);
        public void VertexProgramCreated(Core.Gxm.GxmVertexProgram vertexProgram) => _inner.VertexProgramCreated(vertexProgram);
        public void FragmentProgramCreated(Core.Gxm.GxmFragmentProgram fragmentProgram, Core.Gxm.GxmProgramInfo? vertexProgram) => _inner.FragmentProgramCreated(fragmentProgram, vertexProgram);
        public void RenderTargetCreated(uint multisample) => _inner.RenderTargetCreated(multisample);
        public void ColorSurfaceInitialised(uint colorFormat, bool downscale) => _inner.ColorSurfaceInitialised(colorFormat, downscale);
        public void Prefetch(GpuCommand command) => _inner.Prefetch(command);
        public long PresentCount => _inner.PresentCount;
        public byte[]? ExportDumpState() => _inner.ExportDumpState();
        public void BeforeCallback(CallbackCommand command) => _inner.BeforeCallback(command);
        public bool DeferCallback(CallbackCommand command) => _inner.DeferCallback(command);
        public string? Report() => _inner.Report();
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>
    /// A programmable-blending draw of a one-sample scene renders in a fetch pass, whose pipeline (input attachment
    /// shader, <c>InputPass</c> key) depends on the draw's own textures and colour mask. The draw-time prefetch sees both
    /// when the draw is submitted and builds that pipeline on the shader workers, as it builds the plain one; it used to
    /// build only the plain one and the multisampled input-fetch variant, so in a first session every fetch-pass pipeline
    /// was built on the GPU thread (the replay of a dump: 8, with F16x4, S8x4 and compressed textures that the program-load
    /// prediction, four unsigned bytes, cannot foresee, two of them with the colour mask 0). Covered: textures of three
    /// classes other than the predicted one, each with the colour written and with the fragment program disabled (mask 0).
    /// </summary>
    [SoftwareVulkanFact]
    public void DrawPrefetchBuildsTheFetchPassOfEachDrawBeforeItRuns()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string cache = Path.Combine(Path.GetTempPath(), "vitasharp-fetchprefetch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new VulkanRenderer(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = true,
                AsyncPipelines = true,
                CompileOnProgramLoad = false,
                ShaderCacheDir = cache,
            });
            int gpuPipelines = -1, fetchPass = -1, prefetched = -1;
            using var h = new HleHarness(fx, "fetchprefetch");
            try
            {
                h.Emu.Gpu.Backend = new PrefetchedFirstBackend(r);
                r.BeginTitle("PCSA00083");
                var w = new RendererTextureTests.Workload(h);
                var drawn = new List<uint>();
                foreach (Core.Gxm.TexBase format in new[] { Core.Gxm.TexBase.F16F16F16F16, Core.Gxm.TexBase.S8S8S8S8, Core.Gxm.TexBase.UBC3 })
                {
                    int index = Enumerable.Range(0, w.Textures.Count).First(i => w.TextureFormats[i] == format && !w.CubeTextures.Contains(w.Textures[i]));
                    drawn.Add(w.Textures[index]);
                }

                foreach (uint texture in drawn)
                {
                    // RunFetch draws Textures[0]
                    w.Textures.Clear();
                    w.Textures.Add(texture);
                    w.RunFetch();
                    w.RunFetch(fragmentEnabled: false);
                }

                h.Emu.Gpu.WaitIdle();
                int[] kinds = (int[])typeof(VulkanRenderer).GetField("_statGpuPipelineKinds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!;
                (gpuPipelines, fetchPass, prefetched) = (Stat(r, "_statGpuPipelines"), kinds[1], Stat(r, "_statPipelinesPrefetched"));
                w.Terminate();
            }
            finally
            {
                h.Emu.Gpu.WaitIdle();
                h.Emu.Gpu.Backend = null;
                r.Dispose();
            }

            Assert.True(prefetched >= 6, $"{prefetched} pipelines prefetched");
            Assert.Equal((0, 0), (fetchPass, gpuPipelines));
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

    /// <summary>
    /// The shader workers finish the draw prefetch's work in the order the draws were submitted: a pipeline job runs as
    /// soon as its shaders are no longer pending, ahead of the shader jobs of later draws, because the GPU thread needs
    /// the draws in that order. They used to run every queued shader job first, so a frame of new draws (the first frame
    /// of a cold dump replay without program load: 127 shaders, 40 pipelines) left every pipeline queued behind all the
    /// shaders, and the GPU thread built the 8-13 it reached first itself although the prefetch had predicted each one.
    /// One worker, held (as is the GPU thread) until the whole scene is submitted, so the queues hold every job: draws of
    /// four texture classes give a shader each and one vertex shader; each pipeline but the last runs while a later
    /// shader job waits, every pipeline is built by the worker, and the GPU thread then builds none.
    /// </summary>
    [SoftwareVulkanFact]
    public void DrawPrefetchFinishesEachPipelineBeforeTheShadersOfLaterDraws()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string cache = Path.Combine(Path.GetTempPath(), "vitasharp-pipelineorder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new VulkanRenderer(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = true,
                AsyncPipelines = true,
                CompileOnProgramLoad = false,
                ShaderWorkers = 1,
                ShaderCacheDir = cache,
            });
            using var workersHeld = new ManualResetEventSlim(false);
            using var gpuHeld = new ManualResetEventSlim(false);
            typeof(VulkanRenderer).GetProperty("TestWorkerGate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(r, workersHeld);
            Core.Gxm.TexBase[] classes = [Core.Gxm.TexBase.U8U8U8U8, Core.Gxm.TexBase.F16F16F16F16, Core.Gxm.TexBase.S8S8S8S8, Core.Gxm.TexBase.UBC3];
            int ahead = -1, prefetched = -1, gpuPipelines = -1;
            using var h = new HleHarness(fx, "pipelineorder");
            try
            {
                h.Emu.Gpu.Backend = new PrefetchedFirstBackend(r, gpuHeld, waitForWorkers: false);
                r.BeginTitle("PCSA00083");
                var w = new RendererTextureTests.Workload(h);
                var drawn = new List<uint>();
                foreach (Core.Gxm.TexBase format in classes)
                {
                    int index = Enumerable.Range(0, w.Textures.Count).First(i => w.TextureFormats[i] == format && !w.CubeTextures.Contains(w.Textures[i]));
                    drawn.Add(w.Textures[index]);
                }

                w.Textures.Clear();
                w.Textures.AddRange(drawn);
                w.Run(drawn.Count, finish: false); // one scene with the four draws, all prefetched
                workersHeld.Set();
                Assert.True(r.WaitForBackgroundCompiles(CancellationToken.None));
                gpuHeld.Set();
                h.Emu.Gpu.WaitIdle();
                (ahead, prefetched, gpuPipelines) = (Stat(r, "_statPipelinesAheadOfShaders"), Stat(r, "_statPipelinesPrefetched"), Stat(r, "_statGpuPipelines"));
                w.Terminate();
            }
            finally
            {
                workersHeld.Set();
                gpuHeld.Set();
                h.Emu.Gpu.WaitIdle();
                h.Emu.Gpu.Backend = null;
                r.Dispose();
            }

            Assert.Equal((classes.Length - 1, classes.Length, 0), (ahead, prefetched, gpuPipelines));
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

    /// <summary>
    /// The shader workers follow the CPU budget (<see cref="ShaderWorkerBudget"/>): while the GPU thread is working only
    /// the steady share runs, so the guest CPU thread and the GPU thread keep their cores, and while the GPU thread waits
    /// for a job the whole pool runs, independent jobs in parallel, because the frame cannot go on. The pool used to be a
    /// fixed half of the processors in every state (a cold replay of a dump: the first frame waited 2-4 s for 2 workers
    /// on 4 CPUs while 2 cores were idle). Covered: one job running and the other workers held back while the GPU thread
    /// is held; three jobs at once (the pool) once it waits for the first; back to one when a later frame brings new
    /// jobs while the GPU thread is held again.
    /// </summary>
    [SoftwareVulkanFact]
    public void ShaderWorkersRunTheWholePoolOnlyWhileTheGpuThreadWaitsForThem()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string cache = Path.Combine(Path.GetTempPath(), "vitasharp-workerbudget-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new VulkanRenderer(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = true,
                AsyncPipelines = true,
                CompileOnProgramLoad = false,
                WorkerBudget = new ShaderWorkerBudget(Steady: 1, Stall: 3),
                ShaderCacheDir = cache,
            });
            int running = 0, most = 0;
            var jobGate = new ManualResetEventSlim(false);
            void JobStarted()
            {
                int now = Interlocked.Increment(ref running);
                int seen;
                do
                {
                    seen = Volatile.Read(ref most);
                }
                while (now > seen && Interlocked.CompareExchange(ref most, now, seen) != seen);

                Volatile.Read(ref jobGate).Wait();
                Interlocked.Decrement(ref running);
            }

            typeof(VulkanRenderer).GetProperty("TestJobStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(r, (Action)JobStarted);
            int Parked() => Stat(r, "_workersParked");
            // a stuck pool fails the test instead of hanging it; the result never depends on how long anything takes
            void Until(Func<bool> condition, string what) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromMinutes(2)), what);
            var gpuHeld = new ManualResetEventSlim(false);
            using var h = new HleHarness(fx, "workerbudget");
            try
            {
                h.Emu.Gpu.Backend = new PrefetchedFirstBackend(r, gpuHeld, waitForWorkers: false);
                r.BeginTitle("PCSA00085");
                var w = new RendererTextureTests.Workload(h);
                List<uint> plain = Enumerable.Range(0, w.Textures.Count).Where(i => !w.CubeTextures.Contains(w.Textures[i]))
                    .GroupBy(i => w.TextureFormats[i]).Select(g => w.Textures[g.First()]).ToList();
                Assert.True(plain.Count >= 7, $"{plain.Count} texture formats");

                // a frame of new draws, the GPU thread held: one worker runs, the other two wait (the steady share)
                w.Textures.Clear();
                w.Textures.AddRange(plain.Take(4));
                w.Run(4, finish: false);
                Until(() => Volatile.Read(ref running) >= 1 && (Parked() == 2 || Volatile.Read(ref running) > 1), "one job running, two workers held back");
                Assert.Equal(1, Volatile.Read(ref running));

                // the GPU thread reaches the first draw and waits for its shader: the whole pool runs
                gpuHeld.Set();
                Until(() => Volatile.Read(ref running) == 3, "three jobs at once while the GPU thread waits");
                jobGate.Set();
                h.Emu.Gpu.WaitIdle();
                Assert.True(r.WaitForBackgroundCompiles(CancellationToken.None));

                // the wait is over: a later frame of new draws, the GPU thread held again, runs one job at a time again
                gpuHeld.Reset();
                Volatile.Write(ref jobGate, new ManualResetEventSlim(false));
                w.Textures.Clear();
                w.Textures.AddRange(plain.Skip(4).Take(3));
                w.Run(3, finish: false);
                Until(() => Volatile.Read(ref running) >= 1 && (Parked() == 2 || Volatile.Read(ref running) > 1), "one job running again after the wait");
                Assert.Equal(1, Volatile.Read(ref running));
                jobGate.Set();
                gpuHeld.Set();
                h.Emu.Gpu.WaitIdle();
                w.Terminate();
            }
            finally
            {
                jobGate.Set();
                gpuHeld.Set();
                h.Emu.Gpu.WaitIdle();
                h.Emu.Gpu.Backend = null;
                r.Dispose();
            }

            Assert.Equal(3, most);
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

    /// <summary>
    /// The ceiling of "compilar shaders ao carregar o programa": a surface-dependent program (programmable blending) is
    /// built at creation for its primary prediction and for each known surface, which multiplies in a game with many
    /// programs and surfaces. Covered: a load queues at most the budget of such combinations (the rest is left out); a
    /// new load (presents without a program created) gets a new budget; a program created then is built only for the
    /// most recently used surfaces, not for every surface ever made known.
    /// </summary>
    [SoftwareVulkanFact]
    public void ProgramLoadBuildsOnlyRecentSurfacesWithinTheLoadBudget()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string cache = Path.Combine(Path.GetTempPath(), "vitasharp-loadceiling-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new VulkanRenderer(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                CompileOnProgramLoad = true,
                ProgramLoadSurfaces = 2,
                ProgramLoadBudget = 2,
                ShaderCacheDir = cache,
            });
            WithRenderer("loadceiling", r, h =>
            {
                r.BeginTitle("PCSA00084");
                var w = new RendererTextureTests.Workload(h); // its programs, one surface (U8U8U8U8)
                Assert.True(r.WaitForBackgroundCompiles(CancellationToken.None));
                int Queued() => r.ProgramLoadStats.Pipelines;

                // five new surfaces in one load: the one programmable-blending program (one vertex program, one
                // pipeline per combination) is queued for two of them, the budget
                int before = Queued();
                uint[] surfaces = [0x10800000u, 0x11000000u, 0x41000000u, 0x80800000u, 0x01000000u];
                foreach (uint colorFormat in surfaces)
                {
                    ((IGpuBackend)r).ColorSurfaceInitialised(colorFormat, downscale: false);
                }

                Assert.Equal(2, Queued() - before);

                // a new load: presents without a program created; the program created then is built for its primary
                // prediction and the two most recent surfaces (0x80800000, 0x01000000), not the six known ones
                for (int i = 0; i < 40; i++)
                {
                    h.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(w.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
                }

                h.Emu.Gpu.WaitIdle();
                before = Queued();
                w.CreateFetchProgram(outputFormat: 5); // SCE_GXM_OUTPUT_REGISTER_FORMAT_HALF4
                Assert.Equal(1 + 2, Queued() - before);

                Assert.True(r.WaitForBackgroundCompiles(CancellationToken.None));
                w.Terminate();
            });
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

    /// <summary>
    /// A fragment program with an integer output writes raw bits through an integer view of the surface (R32G32Uint on
    /// F32F32, R16G16B16A16Uint on F16x4, R8G8B8A8Uint on U8x4...), a pipeline of its own attachment format. The draw
    /// prefetch sees the draw's textures and the predicted surface and builds that pipeline on the shader workers, as
    /// program load does; it used to leave it to the GPU thread, so a first session built every integer-view pipeline
    /// there (the cold replay of a dump with raw G-buffer outputs on an F32F32 surface: 37 without program load, and with
    /// it the one whose textures were of another class than the program-load prediction). Covered: every surface format
    /// with an integer view, signed and unsigned outputs, a multisampled surface, textures of the predicted class and of
    /// another one, program load on and off, and pipeline libraries (the pipeline linked ahead instead of built).
    /// </summary>
    [SoftwareVulkanTheory]
    [InlineData(0x00000000u, (byte)ParamTypeU32, (byte)1, (ushort)0, false, false)] // U8U8U8U8 -> R8G8B8A8Uint
    [InlineData(0x01000000u, (byte)ParamTypeU32, (byte)2, (ushort)0, false, false)] // F16F16F16F16 -> R16G16B16A16Uint
    [InlineData(0x00800000u, (byte)ParamTypeS32, (byte)1, (ushort)0, false, false)] // F16F16 -> R16G16Uint
    [InlineData(0x11000000u, (byte)ParamTypeU32, (byte)2, (ushort)0, false, false)] // F32F32 -> R32G32Uint (the G-buffer case)
    [InlineData(0x11000000u, (byte)ParamTypeU32, (byte)2, (ushort)0, true, false)]  // the same with program load on
    [InlineData(0x10800000u, (byte)ParamTypeS32, (byte)1, (ushort)0, false, false)] // F32 -> R32Uint
    [InlineData(0x00000000u, (byte)ParamTypeU32, (byte)1, (ushort)2, false, false)] // U8U8U8U8, 4x MSAA downscaled
    [InlineData(0x11000000u, (byte)ParamTypeU32, (byte)2, (ushort)0, false, true)]  // F32F32 with pipeline libraries (linked ahead)
    public void DrawPrefetchBuildsTheIntegerViewOfEachRawDrawBeforeItRuns(uint colorFormat, byte outputType, byte components, ushort multisample, bool programLoad, bool pipelineLibrary)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string cache = Path.Combine(Path.GetTempPath(), "vitasharp-rawprefetch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new VulkanRenderer(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = true,
                AsyncPipelines = true,
                CompileOnProgramLoad = programLoad,
                PipelineLibrary = pipelineLibrary,
                ShaderCacheDir = cache,
            });
            int gpuPipelines = -1, integerView = -1, raw = -1;
            using var h = new HleHarness(fx, "rawprefetch");
            try
            {
                h.Emu.Gpu.Backend = new PrefetchedFirstBackend(r);
                r.BeginTitle("PCSA00083");
                var w = new RendererTextureTests.Workload(h, multisample, colorFormat, scaleMode: multisample != 0 ? 1u : 0u);
                uint fragmentProgram = w.CreateRawProgram((Core.Gxm.Shader.ParamType)outputType, components);
                foreach (Core.Gxm.TexBase format in new[] { Core.Gxm.TexBase.U8U8U8U8, Core.Gxm.TexBase.F16F16F16F16, Core.Gxm.TexBase.UBC3 })
                {
                    int index = Enumerable.Range(0, w.Textures.Count).First(i => w.TextureFormats[i] == format && !w.CubeTextures.Contains(w.Textures[i]));
                    w.RunWith(fragmentProgram, w.Textures[index]);
                }

                h.Emu.Gpu.WaitIdle();
                int[] kinds = (int[])typeof(VulkanRenderer).GetField("_statGpuPipelineKinds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!;
                (gpuPipelines, integerView) = (Stat(r, "_statGpuPipelines"), kinds[2]);
                raw = Stat(r, "_statPipelinesPrefetched") + r.ProgramLoadStats.PipelinesBuilt + r.PipelineStats.LinkedInBackground;
                w.Terminate();
            }
            finally
            {
                h.Emu.Gpu.WaitIdle();
                h.Emu.Gpu.Backend = null;
                r.Dispose();
            }

            Assert.Equal((0, 0), (integerView, gpuPipelines));
            Assert.True(raw >= 1, $"{raw} pipelines built ahead");
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

    private const byte ParamTypeU32 = (byte)Core.Gxm.Shader.ParamType.U32;
    private const byte ParamTypeS32 = (byte)Core.Gxm.Shader.ParamType.S32;

    [SoftwareVulkanFact]
    public void CancelledShaderCacheLoadLetsTheGameStartAndTheRestBuildsInTheBackground()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        string cache = Path.Combine(Path.GetTempPath(), "vitasharp-shadercache-load-" + Guid.NewGuid().ToString("N"));
        const string Title = "PCSA00080";
        try
        {
            void Session(bool cancelled)
            {
                VulkanRenderer r = NewRenderer(cache);
                WithRenderer("shadercache-load-cancel", r, h =>
                {
                    r.BeginTitle(Title);
                    using var cancel = new CancellationTokenSource();
                    if (cancelled)
                    {
                        cancel.Cancel();
                    }

                    ((IGpuBackend)r).LoadShaderCacheBeforeStart(null, cancel.Token);
                    var w = new RendererTextureTests.Workload(h);
                    w.Run(1);
                    h.Emu.Gpu.WaitIdle();
                    w.Terminate();
                });
            }

            Session(cancelled: false);
            Session(cancelled: true); // returns without waiting; the frame still renders (no deadlock with the prewarm)
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
                Monitor.PulseAll(anomalies);
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
        // The anomaly handler runs on the thread pool, which a loaded run (the coverage job: the whole suite under
        // coverlet on two cores) can keep busy for seconds: the test waits for the handler calls the renderer raised
        // (its recent events that ask for a capture, recorded on the GPU thread before the handler is queued), not for
        // a host-clock deadline. The timeout only keeps a lost call from hanging the run.
        var events = (Queue<FrameAnomalyDetector.Event>)typeof(VulkanRenderer).GetField("_recentEvents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(r)!;
        int raised = events.Count(e => e.Capture);
        var lost = Stopwatch.StartNew();
        lock (anomalies)
        {
            while (anomalies.Count < raised && lost.Elapsed < TimeSpan.FromMinutes(2))
            {
                Monitor.Wait(anomalies, TimeSpan.FromSeconds(1));
            }

            Assert.True(anomalies.Count >= raised, $"{raised} anomalies raised, {anomalies.Count} reached the handler");
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
