using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// sceGxmMidSceneFlush on the software Vulkan device with the workload of <see cref="RendererTextureTests"/>. libgxm
/// (FW 3.74) kicks only the scene's vertex work there: the scene's pixels stay on chip, nothing is stored to its
/// colour surface or loaded back. So a texture over the surface being rendered reads, after the flush as before it,
/// the content from before the scene.
/// </summary>
[Collection("guest")]
public sealed class RendererMidSceneFlushTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;
    private const uint DepthAlways = 0x01C00000; // SCE_GXM_DEPTH_FUNC_ALWAYS

    /// <summary>
    /// A scene draws the workload's random texture over its whole surface and then, with or without a mid-scene flush
    /// between the two, draws a texture over the surface itself (an earlier scene sampled it already, so the renderer
    /// keeps its scene-start content). Both orders must present the same frame: the second draw samples what the
    /// surface held before the scene. The flush used to store the first draw as the scene-start content, and the
    /// second draw copied the random texture instead. Also as a tile-based GPU.
    /// </summary>
    [SoftwareVulkanTheory]
    [InlineData(null)]
    [InlineData("+TileBased")]
    public void MidSceneFlushStoresNothingTheSceneSamples(string? gpuQuirks)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        byte[] withoutFlush = Run("vkmidscene-plain", midSceneFlush: false, gpuQuirks);
        byte[] withFlush = Run("vkmidscene-flush", midSceneFlush: true, gpuQuirks);
        int differing = 0;
        for (int i = 0; i < withFlush.Length; i += 3)
        {
            bool near = Math.Abs(withFlush[i] - withoutFlush[i]) <= 2 && Math.Abs(withFlush[i + 1] - withoutFlush[i + 1]) <= 2
                && Math.Abs(withFlush[i + 2] - withoutFlush[i + 2]) <= 2;
            differing += near ? 0 : 1;
        }

        Assert.True(differing == 0, $"GPU quirks {gpuQuirks ?? "of the device"}: {differing} of {W * H} pixels differ after a mid-scene flush");
    }

    /// <summary>The presented frame (RGB per pixel) of the scenes described at
    /// <see cref="MidSceneFlushStoresNothingTheSceneSamples"/>.</summary>
    private byte[] Run(string tag, bool midSceneFlush, string? gpuQuirks)
    {
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-midscene-" + Guid.NewGuid().ToString("N"));
        try
        {
            VulkanRenderer renderer = new(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                GpuQuirks = gpuQuirks,
                DumpFramesDir = frames,
                DumpEvery = 1,
            });
            using (HleHarness harness = new(fx, tag))
            {
                try
                {
                    harness.Emu.Gpu.Backend = renderer;
                    RendererTextureTests.Workload workload = new(harness);
                    try
                    {
                        RenderScenes(harness, workload, midSceneFlush);
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
                    renderer.Dispose();
                }
            }

            string last = Directory.GetFiles(frames, "frame_*.png").Order().Last();
            using var bitmap = SkiaSharp.SKBitmap.Decode(last);
            Assert.Equal((W, H), (bitmap.Width, bitmap.Height));
            byte[] image = new byte[W * H * 3];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    SkiaSharp.SKColor pixel = bitmap.GetPixel(x, y);
                    image[(y * W + x) * 3] = pixel.Red;
                    image[(y * W + x) * 3 + 1] = pixel.Green;
                    image[(y * W + x) * 3 + 2] = pixel.Blue;
                }
            }

            return image;
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

    private static void RenderScenes(HleHarness harness, RendererTextureTests.Workload workload, bool midSceneFlush)
    {
        Gxm gxm = harness.Module<Gxm>();
        for (uint i = 0; i < W * H; i++)
        {
            GuestMemory.W32(workload.Data + i * 4, 0xFF000000u | (i * 2654435761u >> 8));
        }

        uint random = workload.Textures[workload.TextureFormats.IndexOf(TexBase.U8U8U8U8)];
        uint itself = SurfaceTexture(harness, gxm, workload.Data, W, H);
        // its first texel only: the draw writes that texel's colour everywhere, the texel included, so the result does
        // not depend on the order in which the GPU renders and samples it
        uint firstTexel = SurfaceTexture(harness, gxm, workload.Data, 1, 1);

        // 1. a scene samples its own surface: from the next scene on the renderer keeps the scene-start content
        Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, workload.Rt, 0u, 0u, 0u, workload.Color, 0u));
        Draw(gxm, workload, firstTexel);
        Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));

        // 2. the random texture over the whole surface, [a mid-scene flush,] the surface over itself
        Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, workload.Rt, 0u, 0u, 0u, workload.Color, 0u));
        Draw(gxm, workload, random);
        if (midSceneFlush)
        {
            Assert.Equal(0, Call(gxm, "sceGxmMidSceneFlush", workload.Ctx, 0u, 0u, 0u));
        }

        Draw(gxm, workload, itself);
        Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
        harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
        Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
    }

    /// <summary>A point-sampled U8U8U8U8 linear texture over <paramref name="data"/>.</summary>
    private static uint SurfaceTexture(HleHarness harness, Gxm gxm, uint data, uint width, uint height)
    {
        uint texture = harness.Alloc(16);
        Assert.Equal(0, Call(gxm, "sceGxmTextureInitLinear", texture, data, (uint)TexBase.U8U8U8U8, width, height, 1u));
        Call(gxm, "sceGxmTextureSetMinFilter", texture, 0u);
        Call(gxm, "sceGxmTextureSetMagFilter", texture, 0u);
        return texture;
    }

    /// <summary>The workload's triangle over the whole surface with <paramref name="texture"/> on unit 3.</summary>
    private static void Draw(Gxm gxm, RendererTextureTests.Workload workload, uint texture)
    {
        Invoke(gxm, "sceGxmSetVertexProgram", workload.Ctx, workload.Vp);
        Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, workload.Fp);
        Invoke(gxm, "sceGxmSetFrontDepthFunc", workload.Ctx, DepthAlways);
        Invoke(gxm, "sceGxmSetCullMode", workload.Ctx, 0u);
        Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, workload.Verts));
        Assert.Equal(0, Call(gxm, "sceGxmSetFragmentTexture", workload.Ctx, 3u, texture));
        Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
    }
}
