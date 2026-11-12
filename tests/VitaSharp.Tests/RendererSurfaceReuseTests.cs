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
/// <item>a colour surface replaced by a larger one at the same address: the new one starts with what the old one
/// rendered (the 1024x512 bloom atlas started from guest memory a frame or more old, and the old frames' effects
/// ghosted over the new picture).</item>
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
            VulkanRenderer renderer = new(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                Msaa = "native",
                DumpFramesDir = frames,
                DumpEvery = 1,
            });
            using (HleHarness harness = new(fx, tag))
            {
                try
                {
                    harness.Emu.Gpu.Backend = renderer;
                    RendererTextureTests.Workload workload = new(harness, multisample, scaleMode: scaleMode);
                    try
                    {
                        body(harness, workload);
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
            return bitmap.Pixels.SelectMany(pixel => new[] { pixel.Red, pixel.Green, pixel.Blue }).ToArray();
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

    private static (byte, byte, byte) At(byte[] image, int x, int y) => (image[(y * W + x) * 3], image[(y * W + x) * 3 + 1], image[(y * W + x) * 3 + 2]);

    private static uint PatternTexel(uint i) => 0xFF000000u | (i * 2654435761u >> 8);

    private static (byte, byte, byte) Pattern(int x, int y, int stride)
    {
        uint p = PatternTexel((uint)(y * stride + x));
        return ((byte)p, (byte)(p >> 8), (byte)(p >> 16));
    }

    private static bool Near((byte, byte, byte) a, (byte, byte, byte) b) =>
        Math.Abs(a.Item1 - b.Item1) <= 2 && Math.Abs(a.Item2 - b.Item2) <= 2 && Math.Abs(a.Item3 - b.Item3) <= 2;

    /// <summary>The workload's RGBA8 texture on unit 3 and its programs, depth test <paramref name="func"/>.</summary>
    private static void Setup(Gxm gxm, RendererTextureTests.Workload workload, uint func, bool depthWrite)
    {
        Invoke(gxm, "sceGxmSetVertexProgram", workload.Ctx, workload.Vp);
        Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, workload.Fp);
        Invoke(gxm, "sceGxmSetFrontDepthFunc", workload.Ctx, func);
        Invoke(gxm, "sceGxmSetFrontDepthWriteEnable", workload.Ctx, depthWrite ? 0u : 0x00100000u);
        Invoke(gxm, "sceGxmSetCullMode", workload.Ctx, 0u);
        uint rgba = workload.Textures[workload.TextureFormats.IndexOf(TexBase.U8U8U8U8)];
        Assert.Equal(0, Call(gxm, "sceGxmSetFragmentTexture", workload.Ctx, 3u, rgba));
    }

    private static uint RenderTarget(HleHarness harness, Gxm gxm, int width, int height, ushort multisample)
    {
        uint renderTargetParams = harness.Alloc(0x14), outWord = harness.Word(0);
        GuestMemory.Write(renderTargetParams + 4, (ushort)width);
        GuestMemory.Write(renderTargetParams + 6, (ushort)height);
        GuestMemory.Write(renderTargetParams + 8, (ushort)1);
        GuestMemory.Write(renderTargetParams + 10, multisample);
        Assert.Equal(0, Call(gxm, "sceGxmCreateRenderTarget", renderTargetParams, new Ptr<uint>(outWord)));
        return GuestMemory.R32(outWord);
    }

    [SoftwareVulkanFact]
    public void FourXSceneLoadsTheDepthAOneSampleSceneStored()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        byte[] image = Run("vkreuse-depth", (harness, workload) =>
        {
            Gxm gxm = harness.Module<Gxm>();
            // the 4X scene's 64x64 downscaled surface holds a pattern; its depth surface is 128x128 samples
            for (uint i = 0; i < W * H; i++)
            {
                GuestMemory.W32(workload.Data + i * 4, PatternTexel(i));
            }

            const int S = W * 2;
            uint depth = harness.Alloc(0x20), depthData = harness.Alloc(S * S * 4);
            Assert.Equal(0, Call(gxm, "sceGxmDepthStencilSurfaceInit", depth, 0x00044000u, 0u, (uint)S, depthData, 0u));
            Invoke(gxm, "sceGxmDepthStencilSurfaceSetForceStoreMode", depth, 4u);

            // 1. a one-sample 128x128 scene: depth cleared to the background (1.0), the left half drawn at 0.2
            uint rt1 = RenderTarget(harness, gxm, S, S, 0), color1 = harness.Alloc(0x40), data1 = harness.Alloc(S * S * 4);
            Assert.Equal(0, Call(gxm, "sceGxmColorSurfaceInit", color1, 0u, 0u, 0u, 0u, (uint)S, (uint)S, (uint)S, data1));
            uint quad = harness.Alloc(64), quadIdx = harness.Alloc(12);
            float[] vertices = [-1, -1, -0.6f, 1, 0, -1, -0.6f, 1, -1, 1, -0.6f, 1, 0, 1, -0.6f, 1];
            for (int i = 0; i < vertices.Length; i++)
            {
                GuestMemory.Write(quad + (uint)i * 4, vertices[i]);
            }

            ushort[] indices = [0, 1, 2, 2, 1, 3];
            for (int i = 0; i < indices.Length; i++)
            {
                GuestMemory.Write(quadIdx + (uint)i * 2, indices[i]);
            }

            Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, rt1, 0u, 0u, 0u, color1, depth));
            Setup(gxm, workload, DepthAlways, depthWrite: true);
            Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, quad));
            Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, quadIdx, 6u));
            Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));

            // 2. the 4X scene loads that depth and draws the whole surface at 0.75 with LESS: only the right half passes
            Invoke(gxm, "sceGxmDepthStencilSurfaceSetForceLoadMode", depth, 2u);
            Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, workload.Rt, 0u, 0u, 0u, workload.Color, depth));
            Setup(gxm, workload, DepthLess, depthWrite: false);
            Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, workload.Verts));
            Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
            Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
            harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
            Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
        }, multisample: 2, scaleMode: 1);

        int leftKept = 0, rightDrawn = 0, total = 0;
        for (int y = 2; y < H - 2; y++)
        {
            for (int x = 2; x < W / 2 - 2; x++)
            {
                total++;
                leftKept += Near(At(image, x, y), Pattern(x, y, W)) ? 1 : 0;
                rightDrawn += Near(At(image, x + W / 2, y), Pattern(x + W / 2, y, W)) ? 0 : 1;
            }
        }
        // the left half's samples hold 0.2 (texels 0-63 of each row of the depth), the right half's 1.0
        Assert.True(leftKept == total, $"{total - leftKept} of {total} pixels of the left half were drawn over the one-sample scene's nearer depth");
        Assert.True(rightDrawn > total * 9 / 10, $"only {rightDrawn} of {total} pixels of the right half were drawn: the 4X scene did not load the one-sample scene's depth");
    }

    [SoftwareVulkanFact]
    public void LargerSurfaceAtTheSameAddressStartsWithTheSmallerOnesRendering()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        byte[]? texelsDrawn = null;
        byte[] image = Run("vkreuse-color", (harness, workload) =>
        {
            Gxm gxm = harness.Module<Gxm>();
            for (uint i = 0; i < W * H; i++)
            {
                GuestMemory.W32(workload.Data + i * 4, PatternTexel(i));
            }

            // 1. a 32x32 surface over the first rows of the 64x64 one (same memory, same stride), drawn over entirely
            const int Half = W / 2;
            uint rtA = RenderTarget(harness, gxm, Half, Half, 0), colorA = harness.Alloc(0x40);
            Assert.Equal(0, Call(gxm, "sceGxmColorSurfaceInit", colorA, 0u, 0u, 0u, 0u, (uint)Half, (uint)Half, (uint)W, workload.Data));
            Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, rtA, 0u, 0u, 0u, colorA, 0u));
            Setup(gxm, workload, DepthAlways, depthWrite: false);
            Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, workload.Verts));
            Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
            Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
            // 2. at once the 64x64 surface at the same address: a scene without draws keeps what memory holds
            Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, workload.Rt, 0u, 0u, 0u, workload.Color, 0u));
            Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
            harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
            Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
            // what the 32x32 scene stored, as the CPU sees it once the GPU is idle
            texelsDrawn = GuestMemory.ReadSpan(workload.Data, Half * W * 4).ToArray();
        }, multisample: 0, scaleMode: 0);

        // the 32x32 scene drew its texture over its whole surface: the 64x64 surface's top-left quarter shows it
        int same = 0, kept = 0, total = 0;
        for (int y = 2; y < H / 2 - 2; y++)
        {
            for (int x = 2; x < W / 2 - 2; x++)
            {
                total++;
                kept += Near(At(image, x, y), Pattern(x, y, W)) ? 1 : 0;
                uint memoryTexel = BitConverter.ToUInt32(texelsDrawn!, (y * W + x) * 4);
                same += Near(At(image, x, y), ((byte)memoryTexel, (byte)(memoryTexel >> 8), (byte)(memoryTexel >> 16))) ? 1 : 0;
            }
        }
        Assert.True(kept < total / 10, $"{kept} of {total} pixels of the 64x64 surface show guest memory from before the 32x32 scene");
        Assert.True(same > total * 9 / 10, $"only {same} of {total} pixels show the 32x32 scene's rendering");
        // the rest of the 64x64 surface keeps its memory
        Assert.True(Near(At(image, W - 3, H - 3), Pattern(W - 3, H - 3, W)), "the 64x64 surface lost its memory outside the 32x32 one");
    }
}
