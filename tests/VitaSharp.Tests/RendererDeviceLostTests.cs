using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// After VK_ERROR_DEVICE_LOST the renderer records nothing more: a lost device never completes a submission, so no frame
/// retires and every upload takes new memory (Mali-G57, FIFA 13: the upload rings grew from 176 MB to 1.8 GB in 30 s
/// and the process died out of memory inside the driver). The guest still runs: its scenes, presents and sceGxmFinish
/// return, and a sync point the GPU worker would have held for a frame is signalled at once.
/// </summary>
[Collection("guest")]
public sealed class RendererDeviceLostTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;

    /// <summary>One scene that draws the whole surface, presented, then sceGxmFinish.</summary>
    private static void Frame(HleHarness h, RendererTextureTests.Workload w)
    {
        Gxm g = h.Module<Gxm>();
        Assert.Equal(0, Call(g, "sceGxmBeginScene", w.Ctx, 0u, w.Rt, 0u, 0u, 0u, w.Color, 0u));
        Invoke(g, "sceGxmSetVertexProgram", w.Ctx, w.Vp);
        Invoke(g, "sceGxmSetFragmentProgram", w.Ctx, w.Fp);
        Invoke(g, "sceGxmSetFrontDepthFunc", w.Ctx, 0x00E00000u); // ALWAYS
        Invoke(g, "sceGxmSetCullMode", w.Ctx, 0u);
        Assert.Equal(0, Call(g, "sceGxmSetVertexStream", w.Ctx, 0u, w.Verts));
        Assert.Equal(0, Call(g, "sceGxmSetFragmentTexture", w.Ctx, 3u, w.Textures[0]));
        Assert.Equal(0, Call(g, "sceGxmDraw", w.Ctx, 0u, 0u, w.Idx, 3u));
        Assert.Equal(0, Call(g, "sceGxmEndScene", w.Ctx, 0u, 0u));
        h.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(w.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
        Assert.Equal(0, Call(g, "sceGxmFinish", w.Ctx));
        h.Emu.Gpu.WaitIdle();
    }

    [Fact]
    public void NothingIsSubmittedOrUploadedAfterTheDeviceIsLost()
    {
        var renderer = new VulkanRenderer(new VulkanRendererOptions
        {
            VSync = false,
            AsyncShaders = false,
            AsyncPipelines = false,
            CompileOnProgramLoad = false,
        });
        using var h = new HleHarness(fx, "devicelost");
        try
        {
            h.Emu.Gpu.Backend = renderer;
            var w = new RendererTextureTests.Workload(h);
            Frame(h, w);
            Assert.True(renderer.SubmitCount > 0, "the frame before the loss must reach the GPU for the check below to mean anything");
            Assert.True(renderer.UploadedBytes > 0, "the frame before the loss must upload for the check below to mean anything");

            renderer.MarkDeviceLost();
            long submits = renderer.SubmitCount, uploaded = renderer.UploadedBytes;
            for (int i = 0; i < 4; i++)
            {
                Frame(h, w);
            }

            Assert.Equal(submits, renderer.SubmitCount);
            Assert.Equal(uploaded, renderer.UploadedBytes);
            Assert.True(renderer.CommandsDroppedAfterDeviceLost > 0);

            // a sync point is not held for a frame that will never retire: the caller signals it now
            Assert.False(renderer.DeferSignal(() => { }));
            w.Terminate();
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }
    }

    /// <summary>Every frame keeps the programs it drew with (vertex and fragment program hashes, surface, draws), the
    /// device-lost report's "programs in flight": one pair, drawn once into the workload's surface.</summary>
    [Fact]
    public void AFrameKeepsTheProgramsItDrewWith()
    {
        var renderer = new VulkanRenderer(new VulkanRendererOptions
        {
            VSync = false,
            AsyncShaders = false,
            AsyncPipelines = false,
            CompileOnProgramLoad = false,
        });
        using var h = new HleHarness(fx, "lostdraws");
        try
        {
            h.Emu.Gpu.Backend = renderer;
            var w = new RendererTextureTests.Workload(h);
            Frame(h, w);
            List<string> lines = renderer.ProgramsInFlight(includeRetired: true);
            string pair = Assert.Single(lines, l => l.Contains("draws=1", StringComparison.Ordinal));
            Assert.Contains($"rt=0x{GuestColorAddress(w):X8}(64x64)", pair);
            Assert.Matches(@"vs=[0-9A-F]{16} fs=[0-9A-F]{16}", pair);
            Assert.Contains(lines, l => l.StartsWith("frame slot ", StringComparison.Ordinal) && l.Contains("1 program pairs", StringComparison.Ordinal));
            w.Terminate();
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }
    }

    private static uint GuestColorAddress(RendererTextureTests.Workload w) => w.Data;
}
