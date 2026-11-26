using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The draw path skips vkCmd* calls that would bind what the command buffer has bound (VITASHARP_DRAW_DEDUP, on by
/// default): the index buffer of a draw whose indices were uploaded after the last draw's in the same buffer is not bound
/// again, the draw starts at the first index the offset difference gives; equal push constants are not pushed again.
/// Each draw must still read its own indices and state, on the software Vulkan device.
/// </summary>
[Collection("guest")]
public sealed class RendererDrawDedupTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;

    /// <summary>
    /// One scene: the whole surface drawn with <paramref name="first"/>, then (when <paramref name="second"/> is not 0) a
    /// draw of the same vertices with <paramref name="second"/> through <paramref name="secondIndices"/>; returns the
    /// presented frame (RGB).
    /// </summary>
    private byte[] Scene(Func<RendererTextureTests.Workload, uint> first, Func<RendererTextureTests.Workload, uint> second, ushort[] secondIndices)
    {
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-dedup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var renderer = new VulkanRenderer(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                CompileOnProgramLoad = false,
                DumpFramesDir = frames,
                DumpEvery = 1,
            });
            using var h = new HleHarness(fx, "dedup");
            try
            {
                h.Emu.Gpu.Backend = renderer;
                var w = new RendererTextureTests.Workload(h);
                Gxm g = h.Module<Gxm>();
                uint firstTexture = first(w), secondTexture = second(w);
                uint indices = h.Alloc((uint)secondIndices.Length * 2 + 4);
                for (int i = 0; i < secondIndices.Length; i++)
                {
                    GuestMemory.Write(indices + (uint)i * 2, secondIndices[i]);
                }

                Assert.Equal(0, Call(g, "sceGxmBeginScene", w.Ctx, 0u, w.Rt, 0u, 0u, 0u, w.Color, 0u));
                Invoke(g, "sceGxmSetVertexProgram", w.Ctx, w.Vp);
                Invoke(g, "sceGxmSetFragmentProgram", w.Ctx, w.Fp);
                Invoke(g, "sceGxmSetFrontDepthFunc", w.Ctx, 0x00E00000u); // ALWAYS
                Invoke(g, "sceGxmSetCullMode", w.Ctx, 0u);
                Assert.Equal(0, Call(g, "sceGxmSetVertexStream", w.Ctx, 0u, w.Verts));
                Assert.Equal(0, Call(g, "sceGxmSetFragmentTexture", w.Ctx, 3u, firstTexture));
                Assert.Equal(0, Call(g, "sceGxmDraw", w.Ctx, 0u, 0u, w.Idx, 3u));
                if (secondTexture != 0)
                {
                    Assert.Equal(0, Call(g, "sceGxmSetFragmentTexture", w.Ctx, 3u, secondTexture));
                    Assert.Equal(0, Call(g, "sceGxmDraw", w.Ctx, 0u, 0u, indices, (uint)secondIndices.Length));
                }

                Assert.Equal(0, Call(g, "sceGxmEndScene", w.Ctx, 0u, 0u));
                h.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(w.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
                Assert.Equal(0, Call(g, "sceGxmFinish", w.Ctx));
                h.Emu.Gpu.WaitIdle();
                w.Terminate();
            }
            finally
            {
                h.Emu.Gpu.WaitIdle();
                h.Emu.Gpu.Backend = null;
                renderer.Dispose();
            }

            string last = Directory.GetFiles(frames, "frame_*.png").Order().Last();
            using var bmp = SkiaSharp.SKBitmap.Decode(last);
            return bmp.Pixels.SelectMany(c => new[] { c.Red, c.Green, c.Blue }).ToArray();
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
        }
    }

    private static uint Texture(RendererTextureTests.Workload w, Core.Gxm.TexBase format) => w.Textures[w.TextureFormats.IndexOf(format)];

    /// <summary>
    /// A draw after another in the same rendering, with indices of its own that draw nothing (one degenerate triangle):
    /// the frame is the first draw's alone. Had it read the first draw's indices (the index buffer bound for the first
    /// draw, without the first index of its own data), it would cover the surface with its other texture.
    /// </summary>
    [Fact]
    public void DrawWithIndicesOfItsOwnAfterAnotherReadsThem()
    {
        byte[] firstOnly = Scene(w => Texture(w, Core.Gxm.TexBase.U8U8U8U8), _ => 0, []);
        byte[] secondCovering = Scene(w => Texture(w, Core.Gxm.TexBase.U8U8U8U8), w => Texture(w, Core.Gxm.TexBase.U4U4U4U4), [0, 1, 2]);
        Assert.False(firstOnly.SequenceEqual(secondCovering), "the two textures must give different frames for the check below to mean anything");
        byte[] degenerate = Scene(w => Texture(w, Core.Gxm.TexBase.U8U8U8U8), w => Texture(w, Core.Gxm.TexBase.U4U4U4U4), [1, 1, 1]);
        Assert.True(firstOnly.SequenceEqual(degenerate), "the second draw read indices that were not its own");
    }

    /// <summary>The second draw's own indices, at an odd 16-bit index into the data after the first draw's: the same
    /// frame as when it uses the first draw's index data itself.</summary>
    [Fact]
    public void SecondDrawCoveringTheSurfaceMatchesWithEitherIndexData()
    {
        byte[] ownIndices = Scene(w => Texture(w, Core.Gxm.TexBase.U8U8U8U8), w => Texture(w, Core.Gxm.TexBase.U4U4U4U4), [2, 0, 1]);
        byte[] sharedIndices = Scene(w => Texture(w, Core.Gxm.TexBase.U8U8U8U8), w => Texture(w, Core.Gxm.TexBase.U4U4U4U4), [0, 1, 2]);
        Assert.True(ownIndices.SequenceEqual(sharedIndices), "a rotated copy of the same triangle must give the same frame");
    }
}
