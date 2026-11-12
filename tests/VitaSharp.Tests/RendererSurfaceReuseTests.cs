using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Surfaces that two scenes see through the same memory (Metal Gear Solid HD Collection, the MGS3 plane intro), on the
/// software Vulkan device with the texture workload of <see cref="RendererTextureTests"/>:
/// <list type="bullet">
/// <item>a depth surface written by a one-sample scene and loaded by a 4X scene: the depth surface is sample-sized,
/// so the 4X scene's samples hold the one-sample scene's texels (the plane's monitor screen, drawn in the 4X effects
/// scene with a depth test, was drawn over the crew in front of it);</item>
/// </list>
/// </summary>
[Collection("guest")]
public sealed class RendererSurfaceReuseTests(GuestMemoryFixture fx)
{
    private const int W = 64, H = 64;
    private const uint DepthLess = 0x00400000, DepthAlways = 0x01C00000;

    /// <summary>The last presented frame (RGB per pixel) after <paramref name="body"/> ran on a native-MSAA renderer.</summary>
    private byte[] Run(string tag, Action<HleHarness, RendererTextureTests.Workload> body, ushort multisample, uint scaleMode)
    {
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-reuse-" + Guid.NewGuid().ToString("N"));
        try
        {
            var renderer = new VulkanRenderer(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                Msaa = "native",
                DumpFramesDir = frames,
                DumpEvery = 1,
            });
            using (var h = new HleHarness(fx, tag))
            {
                try
                {
                    h.Emu.Gpu.Backend = renderer;
                    var w = new RendererTextureTests.Workload(h, multisample, scaleMode: scaleMode);
                    try
                    {
                        body(h, w);
                        h.Emu.Gpu.WaitIdle();
                    }
                    finally
                    {
                        w.Terminate();
                    }
                }
                finally
                {
                    h.Emu.Gpu.WaitIdle();
                    h.Emu.Gpu.Backend = null;
                    renderer.Dispose();
                }
            }
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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static (byte, byte, byte) At(byte[] img, int x, int y) => (img[(y * W + x) * 3], img[(y * W + x) * 3 + 1], img[(y * W + x) * 3 + 2]);

    private static uint PatternTexel(uint i) => 0xFF000000u | (i * 2654435761u >> 8);

    private static (byte, byte, byte) Pattern(int x, int y, int stride)
    {
        uint p = PatternTexel((uint)(y * stride + x));
        return ((byte)p, (byte)(p >> 8), (byte)(p >> 16));
    }

    private static bool Near((byte, byte, byte) a, (byte, byte, byte) b) =>
        Math.Abs(a.Item1 - b.Item1) <= 2 && Math.Abs(a.Item2 - b.Item2) <= 2 && Math.Abs(a.Item3 - b.Item3) <= 2;

    /// <summary>The workload's RGBA8 texture on unit 3 and its programs, depth test <paramref name="func"/>.</summary>
    private static void Setup(Gxm g, RendererTextureTests.Workload w, uint func, bool depthWrite)
    {
        Invoke(g, "sceGxmSetVertexProgram", w.Ctx, w.Vp);
        Invoke(g, "sceGxmSetFragmentProgram", w.Ctx, w.Fp);
        Invoke(g, "sceGxmSetFrontDepthFunc", w.Ctx, func);
        Invoke(g, "sceGxmSetFrontDepthWriteEnable", w.Ctx, depthWrite ? 0u : 0x00100000u);
        Invoke(g, "sceGxmSetCullMode", w.Ctx, 0u);
        uint rgba = w.Textures[w.TextureFormats.IndexOf(TexBase.U8U8U8U8)];
        Assert.Equal(0, Call(g, "sceGxmSetFragmentTexture", w.Ctx, 3u, rgba));
    }

    private static uint RenderTarget(HleHarness h, Gxm g, int width, int height, ushort multisample)
    {
        uint rp = h.Alloc(0x14), o = h.Word(0);
        GuestMemory.Write(rp + 4, (ushort)width);
        GuestMemory.Write(rp + 6, (ushort)height);
        GuestMemory.Write(rp + 8, (ushort)1);
        GuestMemory.Write(rp + 10, multisample);
        Assert.Equal(0, Call(g, "sceGxmCreateRenderTarget", rp, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    [SoftwareVulkanFact]
    public void FourXSceneLoadsTheDepthAOneSampleSceneStored()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        byte[] img = Run("vkreuse-depth", (h, w) =>
        {
            Gxm g = h.Module<Gxm>();
            // the 4X scene's 64x64 downscaled surface holds a pattern; its depth surface is 128x128 samples
            for (uint i = 0; i < W * H; i++)
            {
                GuestMemory.W32(w.Data + i * 4, PatternTexel(i));
            }

            const int S = W * 2;
            uint depth = h.Alloc(0x20), depthData = h.Alloc(S * S * 4);
            Assert.Equal(0, Call(g, "sceGxmDepthStencilSurfaceInit", depth, 0x00044000u, 0u, (uint)S, depthData, 0u));
            Invoke(g, "sceGxmDepthStencilSurfaceSetForceStoreMode", depth, 4u);

            // 1. a one-sample 128x128 scene: depth cleared to the background (1.0), the left half drawn at 0.2
            uint rt1 = RenderTarget(h, g, S, S, 0), color1 = h.Alloc(0x40), data1 = h.Alloc(S * S * 4);
            Assert.Equal(0, Call(g, "sceGxmColorSurfaceInit", color1, 0u, 0u, 0u, 0u, (uint)S, (uint)S, (uint)S, data1));
            uint quad = h.Alloc(64), quadIdx = h.Alloc(12);
            float[] v = [-1, -1, -0.6f, 1, 0, -1, -0.6f, 1, -1, 1, -0.6f, 1, 0, 1, -0.6f, 1];
            for (int i = 0; i < v.Length; i++)
            {
                GuestMemory.Write(quad + (uint)i * 4, v[i]);
            }

            ushort[] ix = [0, 1, 2, 2, 1, 3];
            for (int i = 0; i < ix.Length; i++)
            {
                GuestMemory.Write(quadIdx + (uint)i * 2, ix[i]);
            }

            Assert.Equal(0, Call(g, "sceGxmBeginScene", w.Ctx, 0u, rt1, 0u, 0u, 0u, color1, depth));
            Setup(g, w, DepthAlways, depthWrite: true);
            Assert.Equal(0, Call(g, "sceGxmSetVertexStream", w.Ctx, 0u, quad));
            Assert.Equal(0, Call(g, "sceGxmDraw", w.Ctx, 0u, 0u, quadIdx, 6u));
            Assert.Equal(0, Call(g, "sceGxmEndScene", w.Ctx, 0u, 0u));

            // 2. the 4X scene loads that depth and draws the whole surface at 0.75 with LESS: only the right half passes
            Invoke(g, "sceGxmDepthStencilSurfaceSetForceLoadMode", depth, 2u);
            Assert.Equal(0, Call(g, "sceGxmBeginScene", w.Ctx, 0u, w.Rt, 0u, 0u, 0u, w.Color, depth));
            Setup(g, w, DepthLess, depthWrite: false);
            Assert.Equal(0, Call(g, "sceGxmSetVertexStream", w.Ctx, 0u, w.Verts));
            Assert.Equal(0, Call(g, "sceGxmDraw", w.Ctx, 0u, 0u, w.Idx, 3u));
            Assert.Equal(0, Call(g, "sceGxmEndScene", w.Ctx, 0u, 0u));
            h.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(w.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
            Assert.Equal(0, Call(g, "sceGxmFinish", w.Ctx));
        }, multisample: 2, scaleMode: 1);

        int leftKept = 0, rightDrawn = 0, n = 0;
        for (int y = 2; y < H - 2; y++)
        {
            for (int x = 2; x < W / 2 - 2; x++)
            {
                n++;
                leftKept += Near(At(img, x, y), Pattern(x, y, W)) ? 1 : 0;
                rightDrawn += Near(At(img, x + W / 2, y), Pattern(x + W / 2, y, W)) ? 0 : 1;
            }
        }
        // the left half's samples hold 0.2 (texels 0-63 of each row of the depth), the right half's 1.0
        Assert.True(leftKept == n, $"{n - leftKept} of {n} pixels of the left half were drawn over the one-sample scene's nearer depth");
        Assert.True(rightDrawn > n * 9 / 10, $"only {rightDrawn} of {n} pixels of the right half were drawn: the 4X scene did not load the one-sample scene's depth");
    }
}
