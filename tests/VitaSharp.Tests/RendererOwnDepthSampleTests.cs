using System.Reflection;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A multisampled scene that samples its own forced-stored depth surface as a texture, on the software Vulkan device
/// with native MSAA. On the Vita a texture fetch reads memory, and the tile-based GPU stores a scene's depth there only
/// at the end of the scene's tiles: what the draw sees is what the last store left, the depth as the scene began, even
/// after the scene drew over it. The renderer unpacks the samples once per scene, before the open rendering, instead
/// of ending the rendering at every sampling draw (FIFA 13's match: 2.5 renderings per scene on a Mali-G57, each break a
/// store and a load of the samples and depth).
/// </summary>
[Collection("guest")]
public sealed class RendererOwnDepthSampleTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;
    private const uint DepthAlways = 0x01C00000;   // SCE_GXM_DEPTH_FUNC_ALWAYS
    private const uint DepthS8D24 = 0x01844000;    // SCE_GXM_DEPTH_STENCIL_FORMAT_S8D24

    /// <summary>
    /// Scene 1 writes depth z1 and stores it. Scene 2 loads it, draws z2 over the whole surface (or not), then samples
    /// its own depth surface twice. Both versions of scene 2 must present the same frame, the depth of scene 1: the
    /// renderer used to end the rendering at both sampling draws and unpack what scene 2 had drawn by then. As a
    /// tile-based GPU (renderings recorded first, begun when they end) no rendering ends for the samples. On a desktop
    /// GPU the renderings are begun directly, so the unpack still ends the rendering, once per scene (at most), and sees
    /// what the scene drew before it (the frame with no draw before the samples is still the exact one).
    /// The path follows the device (<see cref="VulkanRenderer.DefersRenderingsByDevice"/>): the vendor turns
    /// <see cref="GpuQuirk.TileBased"/> on by itself (the macOS CI's Apple device under MoltenVK, where the desktop case
    /// used to run tile-based and found no break), so the desktop case asks for the desktop profile (no vendor quirks)
    /// with the quirk off, and the profile case gets the deferred renderings from the emulated vendor instead of a
    /// forced quirk.
    /// </summary>
    [SoftwareVulkanTheory]
    [InlineData((ushort)2, "+TileBased", null, true)]
    [InlineData((ushort)1, "+TileBased", null, true)]
    [InlineData((ushort)2, "-TileBased", "desktop", false)]
    [InlineData((ushort)2, null, "mali", true)]
    public void SamplingItsOwnDepthSeesTheDepthAsTheSceneBegan(ushort multisample, string? gpuQuirks, string? gpuProfile, bool hoisted)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        (byte[] drawnOver, long breaks) = Run("owndepth-over", multisample, gpuQuirks, gpuProfile, hoisted, drawOver: true);
        (byte[] untouched, long untouchedBreaks) = Run("owndepth-plain", multisample, gpuQuirks, gpuProfile, hoisted, drawOver: false);
        // scene 1's depth: z = 0.25 in clip space is 0.625 in the depth buffer (159 of 255), not the drawn-over 0.875
        Assert.True(untouched.All(value => Math.Abs(value - 159) <= 2), $"the sampling draw saw depth {untouched[0]}, not scene 1's (159)");
        if (hoisted)
        {
            Assert.True(drawnOver.All(value => Math.Abs(value - 159) <= 2), $"the sampling draw saw depth {drawnOver[0]}, the depth its scene drew, not scene 1's (159)");
        }

        // no rendering is open yet when the samples come first in the scene
        Assert.Equal((hoisted ? 0L : 1L, 0L), (breaks, untouchedBreaks));
    }

    /// <summary>The PERF gpu line (and so the F8 report) gives the tile traffic per presented frame.</summary>
    [Fact]
    public void PerfLineGivesTheTileTrafficPerPresentedFrame()
    {
        Assert.Equal("per presented frame: renderings 2.50, mid-scene breaks 1.50, depth stores 1.51, depth loads 1.51",
            VulkanRenderer.PerFrameTileTraffic(10_000, 25_000, 15_000, 15_100, 15_100));
        Assert.Equal("per presented frame: renderings 3.00, mid-scene breaks 0.00, depth stores 0.00, depth loads 0.00",
            VulkanRenderer.PerFrameTileTraffic(0, 3, 0, 0, 0));
    }

    /// <summary>The presented frame (R per pixel) and the mid-scene breaks of the sampling scene's kind.</summary>
    private (byte[] Image, long Breaks) Run(string tag, ushort multisample, string? gpuQuirks, string? gpuProfile, bool deferred, bool drawOver)
    {
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-owndepth-" + Guid.NewGuid().ToString("N"));
        try
        {
            VulkanRenderer renderer = new(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                GpuQuirks = gpuQuirks,
                GpuProfile = gpuProfile,
                Msaa = "native",
                DumpFramesDir = frames,
                DumpEvery = 1,
            });
            bool deferredByDevice = renderer.DefersRenderingsByDevice;
            long breaks;
            using (HleHarness harness = new(fx, tag))
            {
                try
                {
                    harness.Emu.Gpu.Backend = renderer;
                    RendererTextureTests.Workload workload = new(harness, multisample, 0, scaleMode: 1);
                    try
                    {
                        RenderScenes(harness, workload, multisample, drawOver);
                        harness.Emu.Gpu.WaitIdle();
                    }
                    finally
                    {
                        workload.Terminate();
                    }
                }
                finally
                {
                    harness.Emu.Gpu.WaitIdle();
                    harness.Emu.Gpu.Backend = null;
                    var reasons = (Dictionary<string, long>)typeof(VulkanRenderer).GetField("_breakReasons", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
                    breaks = reasons.GetValueOrDefault("UnpackedDepthTexture");
                    long scenes = (long)typeof(VulkanRenderer).GetField("_statMsScenes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
                    renderer.Dispose();
                    Assert.True(deferredByDevice == deferred, $"the case expects renderings {(deferred ? "recorded first (tile-based or render passes)" : "begun directly")}, the device {(deferredByDevice ? "records them first" : "begins them directly")}");
                    Assert.True(scenes >= 2, $"{scenes} multisampled scenes (the device must support native MSAA)");
                }
            }

            string last = Directory.GetFiles(frames, "frame_*.png").Order().Last();
            using var bitmap = SkiaSharp.SKBitmap.Decode(last);
            Assert.Equal((W, H), (bitmap.Width, bitmap.Height));
            byte[] image = new byte[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    image[y * W + x] = bitmap.GetPixel(x, y).Red;
                }
            }

            return (image, breaks);
        }
        finally
        {
            try
            {
                Directory.Delete(frames, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RenderScenes(HleHarness harness, RendererTextureTests.Workload workload, ushort multisample, bool drawOver)
    {
        Gxm gxm = harness.Module<Gxm>();
        // the depth surface is sample-sized: 2x2 samples per pixel (4X), two samples one above the other (2X)
        uint widthInSamples = multisample == 2 ? 2u * W : W;
        uint heightInSamples = 2u * H;
        uint depthData = harness.Alloc(widthInSamples * heightInSamples * 4);
        uint depth = harness.Alloc(0x20);
        Assert.Equal(0, Call(gxm, "sceGxmDepthStencilSurfaceInit", depth, DepthS8D24, 0u, widthInSamples, depthData, 0u));
        Invoke(gxm, "sceGxmDepthStencilSurfaceSetForceStoreMode", depth, 4u);
        uint depthTexture = harness.Alloc(16);
        Assert.Equal(0, Call(gxm, "sceGxmTextureInitLinear", depthTexture, depthData, (uint)TexBase.X8U24, widthInSamples, heightInSamples, 1u));
        Call(gxm, "sceGxmTextureSetMinFilter", depthTexture, 0u);
        Call(gxm, "sceGxmTextureSetMagFilter", depthTexture, 0u);
        uint near = Triangle(harness, 0.25f), far = Triangle(harness, 0.75f);
        uint plain = workload.Textures[workload.TextureFormats.IndexOf(TexBase.U8U8U8U8)];

        // 1. depth 0.25 everywhere, stored at the scene's end
        Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, workload.Rt, 0u, 0u, 0u, workload.Color, depth));
        Draw(gxm, workload, near, plain);
        Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));

        // 2. loads it, [draws depth 0.75 over it,] samples its own depth surface twice
        Invoke(gxm, "sceGxmDepthStencilSurfaceSetForceLoadMode", depth, 2u);
        Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, workload.Rt, 0u, 0u, 0u, workload.Color, depth));
        if (drawOver)
        {
            Draw(gxm, workload, far, plain);
        }

        Draw(gxm, workload, far, depthTexture);
        Draw(gxm, workload, far, depthTexture);
        Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
        harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
        Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
    }

    /// <summary>A triangle over the whole surface at depth <paramref name="z"/>.</summary>
    private static uint Triangle(HleHarness harness, float z)
    {
        uint vertices = harness.Alloc(48);
        float[] values = [-1, -1, z, 1, 3, -1, z, 1, -1, 3, z, 1];
        for (int i = 0; i < values.Length; i++)
        {
            GuestMemory.Write(vertices + (uint)i * 4, values[i]);
        }

        return vertices;
    }

    private static void Draw(Gxm gxm, RendererTextureTests.Workload workload, uint vertices, uint texture)
    {
        Invoke(gxm, "sceGxmSetVertexProgram", workload.Ctx, workload.Vp);
        Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, workload.Fp);
        Invoke(gxm, "sceGxmSetFrontDepthFunc", workload.Ctx, DepthAlways);
        Invoke(gxm, "sceGxmSetCullMode", workload.Ctx, 0u);
        Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, vertices));
        Assert.Equal(0, Call(gxm, "sceGxmSetFragmentTexture", workload.Ctx, 3u, texture));
        Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
    }
}
