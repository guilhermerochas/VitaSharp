using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A process that ends takes its GPU surfaces with it, as on the Vita, where its memory goes. The renderer kept the
/// render targets and depth targets of an ended process (the capture of Metal Gear Solid resumed after God of War was
/// closed still listed God of War's surfaces): device memory held for nothing, and stale surfaces at addresses another
/// process's next allocation reuses. On the software Vulkan device: the targets of the ended process go, those of the
/// process still running stay, and a surface another process rendered into after the ended one stays too. What was
/// made of the ended process's memory goes as well, CDRAM included (its blocks are freed with it): its textures, vertex
/// copies and surfaces, whoever used them last, once the frames in flight completed; another process's stay, and a
/// process drawing from the same address next reads its own data.
/// </summary>
[Collection("guest")]
public sealed class RendererProcessEndTests(GuestMemoryFixture fx)
{
    private const uint W = 64, H = 64;

    private static ColorSurfaceInfo Color(uint data) => new(data, W, H, W, 0, 0, false, 0);

    private static DepthSurfaceInfo Depth(uint data) => new(data, 0, W, 0x00044000, 0, 1f, 0, false, false);

    private static void Scene(VulkanRenderer renderer, int pid, uint color, uint depth)
    {
        renderer.Execute(new BeginSceneCommand { Width = W, Height = H, Color = Color(color), Depth = depth != 0 ? Depth(depth) : null, Pid = pid });
        renderer.Execute(new EndSceneCommand { Pid = pid });
    }

    [SoftwareVulkanFact]
    public void TheSurfacesOfAnEndedProcessAreReleased()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vkprocend");
        uint a = h.Alloc(W * H * 4), b = h.Alloc(W * H * 4), shared = h.Alloc(W * H * 4);
        uint depthA = h.Alloc(W * H * 4), depthB = h.Alloc(W * H * 4);
        using var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false });

        // process 5 renders into A (with a depth surface) and into a surface process 6 renders into after it
        Scene(renderer, 5, a, depthA);
        Scene(renderer, 5, shared, 0);
        // process 6 renders into B (with its own depth surface) and then into the shared surface
        Scene(renderer, 6, b, depthB);
        Scene(renderer, 6, shared, 0);
        renderer.Execute(new FinishCommand());
        // (a scene without a depth surface has a depth image of its own too)
        (int targets, int depths, _) = renderer.HeldSurfaces;
        Assert.Equal(3, targets);
        Assert.True(depths >= 2, $"{depths} depth targets");

        // process 5 ends: A and its depth surface go; B, B's depth and the shared surface (process 6's since) stay
        renderer.Execute(new ProcessEndedCommand { Pid = 5 });
        renderer.Execute(new FinishCommand());
        (targets, int depthsAfter, _) = renderer.HeldSurfaces;
        Assert.Equal(2, targets);
        Assert.True(depthsAfter < depths && depthsAfter >= 1, $"depth targets {depths} -> {depthsAfter}: process 5's go, process 6's (B's at least) stay");

        // process 6 ends: nothing of it is left
        renderer.Execute(new ProcessEndedCommand { Pid = 6 });
        renderer.Execute(new FinishCommand());
        (targets, depths, _) = renderer.HeldSurfaces;
        Assert.Equal(0, targets);
        Assert.Equal(0, depths);

        // host work (pid 0) is never a process's: an end of pid 0 releases nothing
        Scene(renderer, 0, a, 0);
        renderer.Execute(new ProcessEndedCommand { Pid = 0 });
        Assert.Equal(1, renderer.HeldSurfaces.RenderTargets);
    }

    [SoftwareVulkanFact]
    public void AProcessEndingWithItsSceneOpenEndsItAndReleasesItsSurfaces()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "vkprocopen");
        // without interleaved scenes (VITASHARP_GPU_SCENE_INTERLEAVE=0) a process never leaves a scene open to another
        if (!GpuQueue.InterleaveScenes)
        {
            return;
        }

        uint a = h.Alloc(W * H * 4), b = h.Alloc(W * H * 4);
        using var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false });
        Scene(renderer, 6, b, 0);
        // process 5 begins a scene and ends without closing it
        renderer.Execute(new BeginSceneCommand { Width = W, Height = H, Color = Color(a), Pid = 5 });
        renderer.Execute(new ProcessEndedCommand { Pid = 5, HadOpenScene = GpuQueue.InterleaveScenes });
        renderer.Execute(new FinishCommand());
        Assert.Equal(1, renderer.HeldSurfaces.RenderTargets);
        // the other process renders on
        Scene(renderer, 6, b, 0);
        renderer.Execute(new FinishCommand());
        Assert.Equal(1, renderer.HeldSurfaces.RenderTargets);
    }

    private const uint Red = 0xFF0000FFu, Green = 0xFF00FF00u, Blue = 0xFFFF0000u;
    private const int EndedPid = 0x1001;

    /// <summary>A CDRAM block (shared memory, as the Vita's video memory) filled with <paramref name="texel"/>.</summary>
    private uint CdramTexels(uint texel, uint bytes = W * H * 4)
    {
        uint address = fx.Memory.Alloc(bytes, "vkprocend cdram", MemoryRegion.Cdram);
        for (uint i = 0; i < bytes; i += 4)
        {
            GuestMemory.W32(address + i, texel);
        }

        return address;
    }

    /// <summary>Device-local and host memory together (a software device's host-visible memory is device-local too).</summary>
    private static long HeldBytes(VulkanRenderer renderer, MemCat category)
    {
        (long device, long host) = renderer.HeldBytes(category);
        return device + host;
    }

    private static uint LinearTexture(HleHarness harness, Gxm gxm, uint data)
    {
        uint texture = harness.Alloc(16);
        Assert.Equal(0, Call(gxm, "sceGxmTextureInitLinear", texture, data, (uint)TexBase.U8U8U8U8, (uint)W, (uint)H, 1u));
        return texture;
    }

    private static void BeginScene(Gxm gxm, RendererTextureTests.Workload workload)
    {
        Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, workload.Rt, 0u, 0u, 0u, workload.Color, 0u));
        Invoke(gxm, "sceGxmSetVertexProgram", workload.Ctx, workload.Vp);
        Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, workload.Fp);
        Invoke(gxm, "sceGxmSetFrontDepthFunc", workload.Ctx, 0x00E00000u); // ALWAYS
        Invoke(gxm, "sceGxmSetCullMode", workload.Ctx, 0u);
    }

    /// <summary>A draw over the whole surface showing <paramref name="texture"/>, its triangle read from
    /// <paramref name="vertices"/> with <paramref name="indices"/> (<paramref name="count"/> of them).</summary>
    private static void DrawTexture(Gxm gxm, RendererTextureTests.Workload workload, uint texture, uint vertices, uint indices, uint count)
    {
        Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, vertices));
        Assert.Equal(0, Call(gxm, "sceGxmSetFragmentTexture", workload.Ctx, 3u, texture));
        Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, indices, count));
    }

    private static void EndAndPresent(HleHarness harness, Gxm gxm, RendererTextureTests.Workload workload)
    {
        Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
        harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(workload.Data, W, W, H, DisplayPixelFormat.A8B8G8R8) });
        Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
        harness.Emu.Gpu.WaitIdle();
    }

    /// <summary>Runs <paramref name="body"/> on a renderer attached to the harness's GPU queue; returns the last
    /// presented frame (RGB per pixel).</summary>
    private byte[] Run(string tag, Action<HleHarness, Gxm, RendererTextureTests.Workload, VulkanRenderer> body)
    {
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-procend-" + Guid.NewGuid().ToString("N"));
        try
        {
            var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false, DumpFramesDir = frames, DumpEvery = 1 });
            using (var harness = new HleHarness(fx, tag))
            {
                try
                {
                    harness.Emu.Gpu.Backend = renderer;
                    var workload = new RendererTextureTests.Workload(harness);
                    try
                    {
                        body(harness, harness.Module<Gxm>(), workload, renderer);
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

    /// <summary>
    /// The process's CDRAM holds a texture and a vertex buffer the GPU cached (God of War's textures at 0x6B8xxxxx), the
    /// shell's CDRAM another texture. The process ends: its texture and vertex copy go and their memory comes back once
    /// the frame in flight that drew with them completed (not before: the GPU may still read them); the shell's texture
    /// and the surface it renders into stay.
    /// </summary>
    [SoftwareVulkanFact]
    public void TheCachesOfAnEndedProcesssMemoryGoAfterTheFramesInFlightAndTheShellsStay()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        uint endedTexels = CdramTexels(Red), shellTexels = CdramTexels(Blue);
        // the triangle over the surface at the start of 16 KB (large enough for the vertex cache), the last vertex read
        // by a degenerate triangle so the draw reads the whole range
        const uint VertexBytes = 1024 * 16;
        uint endedVertices = CdramTexels(0, VertexBytes);
        float[] triangle = [-1, -1, 0.5f, 1, 3, -1, 0.5f, 1, -1, 3, 0.5f, 1];
        for (int i = 0; i < triangle.Length; i++)
        {
            GuestMemory.Write(endedVertices + (uint)i * 4, triangle[i]);
        }

        try
        {
            Run("vkprocendmem", (harness, gxm, workload, renderer) =>
            {
                uint endedTexture = LinearTexture(harness, gxm, endedTexels), shellTexture = LinearTexture(harness, gxm, shellTexels);
                uint indices = harness.Alloc(12);
                ushort[] list = [0, 1, 2, 1023, 1023, 1023];
                for (int i = 0; i < list.Length; i++)
                {
                    GuestMemory.Write(indices + (uint)i * 2, list[i]);
                }

                BeginScene(gxm, workload);
                DrawTexture(gxm, workload, shellTexture, workload.Verts, workload.Idx, 3);
                EndAndPresent(harness, gxm, workload);
                (int targets, int depths, int textures0) = renderer.HeldSurfaces;
                long textureBytes0 = HeldBytes(renderer, MemCat.Texture);
                long vertexBytes0 = HeldBytes(renderer, MemCat.UploadRing);

                // the process draws with its texture and vertices, and ends while that frame is still recording
                BeginScene(gxm, workload);
                DrawTexture(gxm, workload, endedTexture, endedVertices, indices, 6);
                harness.Emu.Gpu.WaitIdle();
                Assert.Equal(textures0 + 1, renderer.HeldSurfaces.Textures);
                long textureBytes1 = HeldBytes(renderer, MemCat.Texture);
                long vertexBytes1 = HeldBytes(renderer, MemCat.UploadRing);
                Assert.True(textureBytes1 >= textureBytes0 + W * H * 4, $"texture bytes {textureBytes0} -> {textureBytes1}");
                Assert.True(vertexBytes1 >= vertexBytes0 + VertexBytes, $"vertex copy bytes {vertexBytes0} -> {vertexBytes1}");
                AddressSpace? endedSpace = fx.Memory.SeparateSpaces ? fx.Memory.CreateSpace("vkprocend ended") : null;
                try
                {
                    harness.Emu.Gpu.ReleaseScene(EndedPid, endedSpace, [(endedTexels, W * H * 4), (endedVertices, VertexBytes)]);
                    harness.Emu.Gpu.WaitIdle();
                    VulkanRenderer.ProcessEndRelease release = renderer.LastProcessEndRelease;
                    Assert.Equal(1, release.Textures);
                    Assert.Equal(1, release.VertexRanges);
                    Assert.Equal(0, release.RenderTargets);
                    Assert.Equal(textures0, renderer.HeldSurfaces.Textures);
                    // the frame recording the draw still uses them: destroyed once it completes
                    Assert.Equal(textureBytes1, HeldBytes(renderer, MemCat.Texture));
                    Assert.Equal(vertexBytes1, HeldBytes(renderer, MemCat.UploadRing));

                    // the shell renders on: its texture and surface stay, the ended process's memory is back
                    DrawTexture(gxm, workload, shellTexture, workload.Verts, workload.Idx, 3);
                    EndAndPresent(harness, gxm, workload);
                    Assert.Equal((targets, depths, textures0), renderer.HeldSurfaces);
                    Assert.Equal(textureBytes0, HeldBytes(renderer, MemCat.Texture));
                    Assert.Equal(vertexBytes0, HeldBytes(renderer, MemCat.UploadRing));
                }
                finally
                {
                    if (endedSpace != null)
                    {
                        fx.Memory.ReleaseSpace(endedSpace);
                    }
                }
            });
        }
        finally
        {
            fx.Memory.Free(endedTexels);
            fx.Memory.Free(shellTexels);
            fx.Memory.Free(endedVertices);
        }
    }

    /// <summary>
    /// A process draws from CDRAM it holds and ends; another process's next draw, in the same GPU frame, reads its own
    /// data at that address. The texture the ended process left was checked in this frame and would not be checked
    /// again: without the release the draw showed the ended process's texels.
    /// </summary>
    [SoftwareVulkanFact]
    public void AProcessDrawingFromTheSameAddressAfterTheEndSeesItsOwnData()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        uint texels = CdramTexels(Red);
        try
        {
            byte[] frame = Run("vkprocendsame", (harness, gxm, workload, renderer) =>
            {
                uint texture = LinearTexture(harness, gxm, texels);
                BeginScene(gxm, workload);
                DrawTexture(gxm, workload, texture, workload.Verts, workload.Idx, 3);
                harness.Emu.Gpu.WaitIdle();
                harness.Emu.Gpu.ReleaseScene(EndedPid, null, [(texels, W * H * 4)]);
                harness.Emu.Gpu.WaitIdle();
                // the memory is the next process's now, with its own data
                for (uint i = 0; i < W * H * 4; i += 4)
                {
                    GuestMemory.W32(texels + i, Green);
                }

                DrawTexture(gxm, workload, texture, workload.Verts, workload.Idx, 3);
                EndAndPresent(harness, gxm, workload);
            });
            Assert.Equal(((byte)0, (byte)0xFF, (byte)0), (frame[0], frame[1], frame[2]));
            int center = (int)(H / 2 * W + W / 2) * 3;
            Assert.Equal(((byte)0, (byte)0xFF, (byte)0), (frame[center], frame[center + 1], frame[center + 2]));
        }
        finally
        {
            fx.Memory.Free(texels);
        }
    }

    /// <summary>A surface in the ended process's CDRAM goes even when another process (the shell) rendered into it last:
    /// its memory is freed with the process. Surfaces in other memory stay.</summary>
    [SoftwareVulkanFact]
    public void ASurfaceInTheEndedProcesssMemoryGoesWhoeverRenderedLast()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        uint ended = fx.Memory.Alloc(W * H * 4, "vkprocend surface", MemoryRegion.Cdram), other = fx.Memory.Alloc(W * H * 4, "vkprocend other", MemoryRegion.Cdram);
        try
        {
            using var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false });
            Scene(renderer, 5, ended, 0);
            Scene(renderer, 6, ended, 0);
            Scene(renderer, 6, other, 0);
            renderer.Execute(new FinishCommand());
            Assert.Equal(2, renderer.HeldSurfaces.RenderTargets);
            renderer.Execute(new ProcessEndedCommand { Pid = 5, SharedMemory = [(ended, W * H * 4)] });
            renderer.Execute(new FinishCommand());
            Assert.Equal(1, renderer.HeldSurfaces.RenderTargets);
            Assert.Equal(1, renderer.LastProcessEndRelease.RenderTargets);
        }
        finally
        {
            fx.Memory.Free(ended);
            fx.Memory.Free(other);
        }
    }

    /// <summary>The kernel names the shared blocks a process held (the CDRAM and kernel memory freed with it), not those
    /// of other processes nor its private memory.</summary>
    [Fact]
    public void TheSharedBlocksOfAProcessAreTheOnesItAllocated()
    {
        uint mine, kernel, others, user;
        using (ProcessScope.Enter(0x7701))
        {
            mine = fx.Memory.Alloc(0x10000, "vkprocend mine", MemoryRegion.Cdram);
            kernel = fx.Memory.Alloc(0x1000, "vkprocend kernel", MemoryRegion.Kernel);
            user = fx.Memory.Alloc(0x1000, "vkprocend user");
        }

        using (ProcessScope.Enter(0x7702))
        {
            others = fx.Memory.Alloc(0x10000, "vkprocend others", MemoryRegion.Cdram);
        }

        try
        {
            (uint Address, uint Size)[] owned = fx.Memory.SharedBlocksOwnedBy(0x7701);
            Assert.Equal(2, owned.Length);
            Assert.Contains((mine, 0x10000u), owned);
            Assert.Contains((kernel, 0x1000u), owned);
        }
        finally
        {
            fx.Memory.Free(mine);
            fx.Memory.Free(kernel);
            fx.Memory.Free(user);
            fx.Memory.Free(others);
        }
    }
}
