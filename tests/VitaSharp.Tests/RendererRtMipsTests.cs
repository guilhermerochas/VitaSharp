using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// A mipmapped linear texture whose level 0 is a rendered colour surface (VulkanRenderer.RtMips.cs), on the software
/// Vulkan device with the texture workload of <see cref="RendererTextureTests"/>: a scene renders a triangle with a
/// slanted edge into a 64x64 surface, whose memory is followed by levels 1-6 holding a pattern; later scenes sample
/// that memory as a 64x64 texture with 7 levels, at level 0 (a 128x128 surface: one texel per pixel) and at level 1
/// (a 64x64 surface: two texels per pixel), point-sampled. Above native resolution level 0 must be the surface at
/// its internal resolution (it was its guest-size copy: the edge came out as the native staircase enlarged), and the
/// levels from memory must come out at the same scale; at native resolution the chain is the guest one.
/// </summary>
[Collection("guest")]
public sealed class RendererRtMipsTests(GuestMemoryFixture fx)
{
    private const int SourceSize = 64, LargeSize = 128, Levels = 7;

    /// <summary>The frames presented by one run: the source surface, the level-0 read and the level-1 read, each at
    /// the internal resolution (RGB per attachment pixel), and the number of mip chains built over a target.</summary>
    private sealed record Frames(byte[] Source, byte[] Level0, byte[] Level1, int Scale, int Chains);

    private static (byte, byte, byte) Level1Texel(int x, int y)
    {
        uint p = unchecked((uint)(y * 32 + x) * 2654435761u) >> 8;
        return ((byte)p, (byte)(p >> 8), (byte)(p >> 16));
    }

    private static byte[] ReadFrame(string file, int size)
    {
        using var bitmap = SkiaSharp.SKBitmap.Decode(file);
        Assert.Equal((size, size), (bitmap.Width, bitmap.Height));
        byte[] image = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                SkiaSharp.SKColor pixel = bitmap.GetPixel(x, y);
                image[(y * size + x) * 3] = pixel.Red;
                image[(y * size + x) * 3 + 1] = pixel.Green;
                image[(y * size + x) * 3 + 2] = pixel.Blue;
            }
        }

        return image;
    }

    private Frames Run(int resScale)
    {
        string frames = Path.Combine(Path.GetTempPath(), "vitasharp-rtmips-" + Guid.NewGuid().ToString("N"));
        int chains = 0;
        try
        {
            VulkanRenderer renderer = new(new VulkanRendererOptions
            {
                VSync = false,
                AsyncShaders = false,
                AsyncPipelines = false,
                DumpFramesDir = frames,
                DumpEvery = 1,
                ResScale = resScale,
            });
            using (HleHarness harness = new(fx, "vkrtmips"))
            {
                try
                {
                    harness.Emu.Gpu.Backend = renderer;
                    RendererTextureTests.Workload workload = new(harness);
                    try
                    {
                        Scenes(harness, workload);
                        harness.Emu.Gpu.WaitIdle();
                        chains = renderer.RtMipChainCount;
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
            string[] files = Directory.GetFiles(frames, "frame_*.png").Order().ToArray();
            Assert.True(files.Length >= 3, $"{files.Length} frames dumped");
            return new Frames(ReadFrame(files[^3], SourceSize * resScale), ReadFrame(files[^2], LargeSize * resScale),
                ReadFrame(files[^1], SourceSize * resScale), resScale, chains);
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

    private static void Scenes(HleHarness harness, RendererTextureTests.Workload workload)
    {
        Gxm gxm = harness.Module<Gxm>();
        // the source surface (page aligned, so the levels after it are pages of their own) and levels 1-6 after it
        uint levelsBytes = 0;
        for (int m = 1; m < Levels; m++)
        {
            uint side = (uint)SourceSize >> m;
            levelsBytes += Math.Max(side, 8) * side * 4;
        }

        uint source = harness.Alloc(SourceSize * SourceSize * 4 + levelsBytes + 0x2000) + 0xFFF & ~0xFFFu;
        uint level1 = source + SourceSize * SourceSize * 4;
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                (byte r, byte g, byte b) = Level1Texel(x, y);
                GuestMemory.W32(level1 + (uint)(y * 32 + x) * 4, 0xFF000000u | (uint)b << 16 | (uint)g << 8 | r);
            }
        }

        uint sourceSurface = harness.Alloc(0x40);
        Assert.Equal(0, Call(gxm, "sceGxmColorSurfaceInit", sourceSurface, 0u, 0u, 0u, 0u, (uint)SourceSize, (uint)SourceSize, (uint)SourceSize, source));
        // the large surface and its render target
        uint large = harness.Alloc(LargeSize * LargeSize * 4);
        uint largeSurface = harness.Alloc(0x40);
        Assert.Equal(0, Call(gxm, "sceGxmColorSurfaceInit", largeSurface, 0u, 0u, 0u, 0u, (uint)LargeSize, (uint)LargeSize, (uint)LargeSize, large));
        uint parameters = harness.Alloc(0x14), output = harness.Word(0);
        GuestMemory.Write(parameters + 4, (ushort)LargeSize);
        GuestMemory.Write(parameters + 6, (ushort)LargeSize);
        GuestMemory.Write(parameters + 8, (ushort)1);
        Assert.Equal(0, Call(gxm, "sceGxmCreateRenderTarget", parameters, new Ptr<uint>(output)));
        uint largeTarget = GuestMemory.R32(output);

        // a white texture, and a triangle with a slanted edge (not along pixel diagonals) drawing it
        uint white = harness.Alloc(8 * 8 * 4);
        GuestMemory.Fill(white, 0xFF, 8 * 8 * 4);
        uint whiteTexture = harness.Alloc(16);
        Assert.Equal(0, Call(gxm, "sceGxmTextureInitLinear", whiteTexture, white, (uint)TexBase.U8U8U8U8, 8u, 8u, 1u));
        uint triangle = harness.Alloc(48);
        float[] vertices = [-1, -1, 0.5f, 1, 1, -1, 0.5f, 1, -1, 0.37f, 0.5f, 1];
        for (int i = 0; i < vertices.Length; i++)
        {
            GuestMemory.Write(triangle + (uint)i * 4, vertices[i]);
        }

        // the source surface sampled with its levels, point-sampled, repeating (the full-surface triangle's texture
        // coordinates run from -1 to 1: the texture twice across the surface)
        uint mipped = harness.Alloc(16);
        Assert.Equal(0, Call(gxm, "sceGxmTextureInitLinear", mipped, source, (uint)TexBase.U8U8U8U8, (uint)SourceSize, (uint)SourceSize, (uint)Levels));
        Call(gxm, "sceGxmTextureSetMinFilter", mipped, 0u);
        Call(gxm, "sceGxmTextureSetMagFilter", mipped, 0u);
        Call(gxm, "sceGxmTextureSetMipFilter", mipped, 1u);
        Call(gxm, "sceGxmTextureSetUAddrMode", mipped, 0u);
        Call(gxm, "sceGxmTextureSetVAddrMode", mipped, 0u);

        void Scene(uint target, uint surface, uint vertexData, uint texture)
        {
            Assert.Equal(0, Call(gxm, "sceGxmBeginScene", workload.Ctx, 0u, target, 0u, 0u, 0u, surface, 0u));
            Invoke(gxm, "sceGxmSetVertexProgram", workload.Ctx, workload.Vp);
            Invoke(gxm, "sceGxmSetFragmentProgram", workload.Ctx, workload.Fp);
            Invoke(gxm, "sceGxmSetFrontDepthFunc", workload.Ctx, 0x00E00000u); // ALWAYS
            Invoke(gxm, "sceGxmSetCullMode", workload.Ctx, 0u);
            Assert.Equal(0, Call(gxm, "sceGxmSetVertexStream", workload.Ctx, 0u, vertexData));
            Assert.Equal(0, Call(gxm, "sceGxmSetFragmentTexture", workload.Ctx, 3u, texture));
            Assert.Equal(0, Call(gxm, "sceGxmDraw", workload.Ctx, 0u, 0u, workload.Idx, 3u));
            Assert.Equal(0, Call(gxm, "sceGxmEndScene", workload.Ctx, 0u, 0u));
        }

        void Present(uint address, uint size) =>
            harness.Emu.Gpu.Submit(new PresentCommand { Frame = new DisplayFrame(address, size, size, size, DisplayPixelFormat.A8B8G8R8) });

        Scene(workload.Rt, sourceSurface, triangle, whiteTexture);
        Present(source, SourceSize);
        Scene(largeTarget, largeSurface, workload.Verts, mipped);
        Present(large, LargeSize);
        Scene(workload.Rt, workload.Color, workload.Verts, mipped);
        Present(workload.Data, SourceSize);
        Assert.Equal(0, Call(gxm, "sceGxmFinish", workload.Ctx));
    }

    private static (byte, byte, byte) At(byte[] image, int size, int x, int y) =>
        (image[(y * size + x) * 3], image[(y * size + x) * 3 + 1], image[(y * size + x) * 3 + 2]);

    private static bool Near((byte, byte, byte) a, (byte, byte, byte) b) =>
        Math.Abs(a.Item1 - b.Item1) <= 2 && Math.Abs(a.Item2 - b.Item2) <= 2 && Math.Abs(a.Item3 - b.Item3) <= 2;

    /// <summary>Pixels of the level-0 read that differ from the source surface (both at the internal resolution: one
    /// texel per pixel, the texture twice across the large surface). Texture coordinates follow the clip-space y, which
    /// points up while surface rows run down, so the read is the surface upside down.</summary>
    private static int Level0Mismatches(Frames frames)
    {
        int sourceSize = SourceSize * frames.Scale, largeSize = LargeSize * frames.Scale, mismatches = 0;
        for (int y = 0; y < largeSize; y++)
        {
            for (int x = 0; x < largeSize; x++)
            {
                if (!Near(At(frames.Level0, largeSize, x, y), At(frames.Source, sourceSize, x % sourceSize, sourceSize - 1 - y % sourceSize)))
                {
                    mismatches++;
                }
            }
        }

        return mismatches;
    }

    /// <summary>Pixels of the level-1 read that are not the guest level-1 pattern (two texels per guest pixel: the
    /// level twice across the surface, each guest texel a block of scale x scale pixels).</summary>
    private static int Level1Mismatches(Frames frames)
    {
        int size = SourceSize * frames.Scale, mismatches = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!Near(At(frames.Level1, size, x, y), Level1Texel(x / frames.Scale % 32, 31 - y / frames.Scale % 32)))
                {
                    mismatches++;
                }
            }
        }

        return mismatches;
    }

    /// <summary>2x2 blocks of the image that are not one colour: detail finer than a guest pixel.</summary>
    private static int SubPixelBlocks(byte[] image, int size)
    {
        int blocks = 0;
        for (int y = 0; y < size; y += 2)
        {
            for (int x = 0; x < size; x += 2)
            {
                (byte, byte, byte) corner = At(image, size, x, y);
                if (!Near(corner, At(image, size, x + 1, y)) || !Near(corner, At(image, size, x, y + 1)) || !Near(corner, At(image, size, x + 1, y + 1)))
                {
                    blocks++;
                }
            }
        }

        return blocks;
    }

    [SoftwareVulkanFact]
    public void TwoTimesResolutionSamplesLevelZeroAtTheTargetsResolution()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        Frames frames = Run(2);
        Assert.Equal(1, frames.Chains);
        // the source's slanted edge at 2x has steps finer than a guest pixel: the level-0 read must keep them
        Assert.True(SubPixelBlocks(frames.Source, SourceSize * 2) > SourceSize / 2, "the 2x source surface has no detail finer than a guest pixel");
        int mismatches = Level0Mismatches(frames);
        Assert.True(mismatches == 0, $"{mismatches} pixels of level 0 read at 2x are not the 2x surface (sub-pixel blocks in the read: {SubPixelBlocks(frames.Level0, LargeSize * 2)})");
        // the levels from memory at the same scale: each guest texel a 2x2 block
        int level1 = Level1Mismatches(frames);
        Assert.True(level1 == 0, $"{level1} pixels of level 1 read at 2x are not the guest level");
    }

    [SoftwareVulkanFact]
    public void NativeResolutionChainIsTheGuestOne()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        Frames frames = Run(1);
        Assert.Equal(1, frames.Chains);
        int level0 = Level0Mismatches(frames);
        Assert.True(level0 == 0, $"{level0} pixels of level 0 read at 1x are not the surface");
        int level1 = Level1Mismatches(frames);
        Assert.True(level1 == 0, $"{level1} pixels of level 1 read at 1x are not the guest level");
        // the triangle drew: part white, part the black the surface started from
        int whitePixels = 0;
        for (int i = 0; i < SourceSize * SourceSize; i++)
        {
            if (frames.Source[i * 3] == 255)
            {
                whitePixels++;
            }
        }

        Assert.InRange(whitePixels, SourceSize * SourceSize / 8, SourceSize * SourceSize * 7 / 8);
    }
}
