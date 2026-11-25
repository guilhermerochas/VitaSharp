using Silk.NET.Vulkan;
using VitaSharp.Core.Compat.Ge;
using VitaSharp.Core.Gpu;
using VitaSharp.Renderer.Vulkan;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>The whole GE rasterizer suite on the software device as an Adreno (no logic op, its limits) rendering in
/// tiles (+TileBased), as the GE runs on Android.</summary>
public sealed class GeVulkanRasterizerAdrenoTests() : GeVulkanRasterizerTests("adreno", "+TileBased")
{
    [SoftwareVulkanFact]
    public void TheDeviceIsATiledAdrenoWithoutLogicOps()
    {
        Assert.True(Core!.Caps.Has(GpuQuirk.TileBased));
        Assert.False(Core.LogicOp);
    }
}

/// <summary>The whole GE rasterizer suite on the software device as a Mali (Valhall's profile) rendering in tiles.</summary>
public sealed class GeVulkanRasterizerMaliTests() : GeVulkanRasterizerTests("mali", "+TileBased")
{
    [SoftwareVulkanFact]
    public void TheDeviceIsATiledMali()
    {
        Assert.True(Core!.Caps.Has(GpuQuirk.TileBased));
    }
}

/// <summary>
/// The GE's Vulkan device (GeVulkanRasterizerDevice): on the renderer's device, on its second queue, when there is
/// one, else a device of its own; the shared device lives until both are done with it; the depth of every
/// depth-stencil format Android drivers offer goes through a buffer copy unchanged.
/// </summary>
public sealed class GeVulkanDeviceTests
{
    private const uint ListAddress = 0x08001000, Vertices = 0x08010000;
    private const uint ThroughColorS16 = (1u << 23) | (7u << 2) | (2u << 7);

    /// <summary>Every 16-bit depth through the depth texels of D16S8, D24S8 (Adreno, Mali) and D32S8 (PowerVR, the
    /// software drivers) and back is the same value; D24 spans its whole range.</summary>
    [Theory]
    [InlineData(Format.D16UnormS8Uint, 2)]
    [InlineData(Format.D24UnormS8Uint, 4)]
    [InlineData(Format.D32SfloatS8Uint, 4)]
    public void EveryDepthComesBackFromEveryDepthStencilFormat(Format format, int texelBytes)
    {
        byte[] depths = new byte[65536 * 2];
        for (int i = 0; i < 65536; i++)
        {
            depths[i * 2] = (byte)i;
            depths[i * 2 + 1] = (byte)(i >> 8);
        }

        byte[] texels = new byte[65536 * texelBytes];
        GeVulkanRasterizer.DepthToTexels(depths, format, texels);
        byte[] back = new byte[depths.Length];
        GeVulkanRasterizer.DepthFromTexels(texels, format, back);
        Assert.Equal(depths, back);
        if (format == Format.D24UnormS8Uint)
        {
            Assert.Equal(0xFFFFFFu, BitConverter.ToUInt32(texels, 65535 * 4));
            Assert.Equal(0u, BitConverter.ToUInt32(texels, 0));
        }
    }

    /// <summary>A red rectangle drawn by a GE on <paramref name="rasterizer"/>; true when its pixel is in memory.</summary>
    private static bool DrawsARedRectangle(IGeRasterizer rasterizer)
    {
        GeTestMemory memory = new();
        GeEngine engine = new(memory) { Rasterizer = rasterizer };
        memory.Write32(Vertices, 0xFF0000FF);
        memory.Write16(Vertices + 4, 0);
        memory.Write16(Vertices + 6, 0);
        memory.Write32(Vertices + 12, 0xFF0000FF);
        memory.Write16(Vertices + 16, 8);
        memory.Write16(Vertices + 18, 8);
        GeListBuilder list = new GeListBuilder(memory, ListAddress).RamBase()
            .Command(GeCommand.FrameBufferPointer, 0).Command(GeCommand.FrameBufferWidth, 512)
            .Command(GeCommand.FrameBufferPixelFormat, GePixels.Format8888)
            .Command(GeCommand.Scissor1, 0).Command(GeCommand.Scissor2, 479 | (271u << 10))
            .Command(GeCommand.VertexAddress, Vertices & 0xFFFFFF).Command(GeCommand.VertexType, ThroughColorS16)
            .Command(GeCommand.Primitive, (6u << 16) | 2).End();
        GeRegisters.Run(engine, list.Start);
        return (memory.Read32(GeTestMemory.VramBase + (4 * 512 + 4) * 4) & 0xFFFFFF) == 0x0000FF; // no alpha written without the stencil
    }

    /// <summary>The GE on the renderer's device keeps it alive when the renderer ends first (a GE still running its
    /// list then), and the device is destroyed when the GE is done; the GE's submits and waits use its own queue and
    /// fence (the software device has one queue: nothing else submits there in this test).</summary>
    [SoftwareVulkanFact]
    public void TheSharedDeviceLivesUntilTheGeIsDone()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        VkCore renderer = new(null, false);
        GeVulkanRasterizerDevice? ge = GeVulkanRasterizerDevice.CreateOn(renderer.AddUser(), renderer.Queue, renderer.SecondaryQueueLock);
        Assert.NotNull(ge);
        try
        {
            Assert.True(ge.Shared);
            Assert.True(DrawsARedRectangle(ge));
            renderer.WaitIdle();
            renderer.Dispose(); // the renderer ends
            Assert.False(renderer.Destroyed);
            Assert.True(DrawsARedRectangle(ge));
        }
        finally
        {
            ge.Dispose();
        }

        Assert.True(renderer.Destroyed);
    }

    /// <summary>The device the GE takes: the offered renderer's when it has a second queue (the GE never submits on the
    /// renderer's queue), else one of its own; a withdrawn device is not taken.</summary>
    [SoftwareVulkanFact]
    public void TheGeTakesTheRenderersDeviceOnlyWithASecondQueue()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        VkCore renderer = new(null, false);
        try
        {
            GeVulkanRasterizerDevice.Offer(renderer);
            using (GeVulkanRasterizerDevice ge = Assert.IsType<GeVulkanRasterizerDevice>(GeVulkanRasterizerDevice.Create()))
            {
                Assert.Equal(renderer.HasSecondaryQueue, ge.Shared);
                Assert.True(DrawsARedRectangle(ge));
            }

            GeVulkanRasterizerDevice.Withdraw(renderer);
            using (GeVulkanRasterizerDevice own = Assert.IsType<GeVulkanRasterizerDevice>(GeVulkanRasterizerDevice.Create()))
            {
                Assert.False(own.Shared);
            }

            Assert.False(renderer.Destroyed);
        }
        finally
        {
            GeVulkanRasterizerDevice.Withdraw(renderer);
            renderer.Dispose();
        }
    }
}
