using VitaSharp.Core.Gpu;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The renderer's other paths on the software Vulkan device, with the same GXM workload as
/// <see cref="RendererTextureTests"/>: 4X MSAA render targets in each MSAA mode (native, supersampled, off), the
/// internal resolution at 2x, shaders and pipelines compiled on the background workers, the on-disk shader cache found
/// again by a second renderer, and the F10 diagnostic capture and the draw trace (draws.txt, render targets, textures,
/// screenshot).
/// </summary>
[Collection("guest")]
public sealed class RendererFeatureTests(GuestMemoryFixture fx)
{
    private static void WithRenderer(HleHarness h, VulkanRendererOptions o, Action<VulkanRenderer> body)
    {
        var r = new VulkanRenderer(o);
        try
        {
            h.Emu.Gpu.Backend = r;
            body(r);
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            r.Dispose();
        }
    }

    [SoftwareVulkanFact]
    public void MsaaModesResolutionScaleAndAsyncShaders()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vkfeat");
        // SCE_GXM_MULTISAMPLE_4X with the MSAA downscale colour surface: libgxm 3.74's sceGxmBeginScene refuses a 4X
        // target whose colour surface is neither downscaled nor at the sample grid (twice the size each way)
        var w = new RendererTextureTests.Workload(h, multisample: 2, scaleMode: 1);
        string cache = Path.Combine(h.Root, "shadercache");
        try
        {
            foreach ((string msaa, float scale, bool async) in new[] { ("native", 1f, true), ("ss", 2f, false), ("off", 1f, true) })
            {
                WithRenderer(h, new VulkanRendererOptions
                {
                    VSync = false,
                    Msaa = msaa,
                    ResScale = scale,
                    AsyncShaders = async,
                    AsyncPipelines = async,
                    ShaderCacheDir = cache,
                }, r =>
                {
                    Assert.True(w.Run(64) > 0);
                    Assert.True(r.PresentCount > 0);
                    Assert.False(string.IsNullOrEmpty(r.DescribeForDiagnostics()));
                    Assert.False(string.IsNullOrEmpty(r.RecentFrames()));
                    Assert.NotNull(r.DebugState());
                });
            }
            // the shaders went to the disk cache, which the later renderers read back
            Assert.NotEmpty(Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories));
        }
        finally
        {
            w.Terminate();
        }
    }

    /// <summary>
    /// The draw trace (F8's draws.txt, replay-gpu --trace-draws) at 2x internal resolution: every scene says the scale
    /// of its attachments and every texture unit of a traced draw says where its texture came from and at which
    /// scale ("[tex] ... src=mem", "src=rt@...*2x", "*2x>1x" for a 2x target read through its guest-size copy), so a
    /// picture problem seen only at one scale shows which pass lost the internal resolution. Before, the trace listed
    /// the descriptors only, and a texture reused from the previous draw's set was not resolved again.
    /// </summary>
    [SoftwareVulkanFact]
    public void DrawTraceAtTwoTimesNamesTheScaleOfEverySceneAndTheSourceOfEveryTexture()
    {
        Assert.Equal("", VulkanRenderer.ScaleTag(1f, 1f));
        Assert.Equal("*2x", VulkanRenderer.ScaleTag(2f, 2f));
        Assert.Equal("*2x>1x", VulkanRenderer.ScaleTag(2f, 1f));
        Assert.Equal("*0.75x", VulkanRenderer.ScaleTag(0.75f, 0.75f));

        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vktrace2x");
        var w = new RendererTextureTests.Workload(h);
        try
        {
            WithRenderer(h, new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false, ResScale = 2f }, r =>
            {
                w.Run(4);
                string traceDir = Path.Combine(h.Root, "trace2x");
                DiagnosticCaptureResult? trace = null;
                using var traceDone = new ManualResetEventSlim();
                h.Emu.Gpu.Submit(new CallbackCommand { Action = () => r.BeginDrawTrace(traceDir, "2x trace", r.PresentCount, res => { trace = res; traceDone.Set(); }) });
                w.Run(4);
                h.Emu.Gpu.Submit(new CallbackCommand { Action = r.EndDrawTrace });
                h.Emu.Gpu.WaitIdle();
                Assert.True(traceDone.Wait(60_000), "the trace did not complete");
                Assert.Null(trace!.Error);
                string[] lines = File.ReadAllLines(Path.Combine(traceDir, "draws.txt"));
                int draws = lines.Count(l => l.StartsWith("[draw] id=", StringComparison.Ordinal));
                Assert.True(draws > 1, $"{draws} draws traced");
                // every draw samples the workload's texture: one source line per draw, the repeated ones included
                string[] tex = lines.Where(l => l.StartsWith("[tex] id=", StringComparison.Ordinal)).ToArray();
                Assert.True(tex.Length >= draws, $"{tex.Length} texture source lines for {draws} draws:\n{string.Join("\n", lines.Take(20))}");
                Assert.All(tex, l => Assert.Contains(" src=", l));
                Assert.Contains(tex, l => l.Contains(" src=mem", StringComparison.Ordinal));
                string[] scenes = lines.Where(l => l.StartsWith("[scene] begin", StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(scenes);
                Assert.All(scenes, l => Assert.Contains(" scale=2 ", l));
            });
        }
        finally
        {
            w.Terminate();
        }
    }

    [SoftwareVulkanFact]
    public void DiagnosticCaptureAndDrawTraceWriteTheirFiles()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vkdiag");
        var w = new RendererTextureTests.Workload(h);
        try
        {
            WithRenderer(h, new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false }, r =>
            {
                w.Run(250); // shaders and pipelines exist before the capture
                string capDir = Path.Combine(h.Root, "capture");
                DiagnosticCaptureResult? cap = null;
                using var capDone = new ManualResetEventSlim();
                Assert.True(r.RequestDiagnosticCapture(capDir, res => { cap = res; capDone.Set(); }));
                Assert.False(r.RequestDiagnosticCapture(capDir, _ => { })); // one at a time
                w.Run(250); // the next full frame is captured
                w.Run(250);
                Assert.True(capDone.Wait(60_000), "the capture did not complete");
                Assert.Null(cap!.Error);
                Assert.True(cap.Draws > 0);
                Assert.NotEmpty(Directory.EnumerateFiles(capDir, "*", SearchOption.AllDirectories));

                // a draw trace over the draws of one run, ended between scenes on the GPU worker
                string traceDir = Path.Combine(h.Root, "trace");
                DiagnosticCaptureResult? trace = null;
                using var traceDone = new ManualResetEventSlim();
                h.Emu.Gpu.Submit(new CallbackCommand { Action = () => r.BeginDrawTrace(traceDir, "test trace", r.PresentCount, res => { trace = res; traceDone.Set(); }) });
                w.Run(250);
                h.Emu.Gpu.Submit(new CallbackCommand { Action = r.EndDrawTrace });
                h.Emu.Gpu.WaitIdle();
                Assert.True(traceDone.Wait(60_000), "the trace did not complete");
                Assert.Null(trace!.Error);
                Assert.True(trace.Draws > 0);
                Assert.True(File.Exists(Path.Combine(traceDir, "draws.txt")));
                Assert.False(r.CancelDiagnosticCapture()); // nothing pending
            });
        }
        finally
        {
            w.Terminate();
        }
    }
}
