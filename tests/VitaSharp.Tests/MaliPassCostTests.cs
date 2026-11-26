using Silk.NET.Vulkan;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The tile traffic model of the Mali cost estimate (VulkanRenderer.DrawCost.cs, vitasharp-perf mali-cost): bytes a
/// rendering loads into and stores from the tile memory by its attachments' format, samples and load/store ops, and the
/// resolve of a multisampled scene.
/// </summary>
public sealed class MaliPassCostTests
{
    [Fact]
    public void SingleSampledRgba8LoadsAndStoresColourAndDepth()
    {
        (long load, long store, long resolve) = PassCostRecord.Bytes(960, 544, 4, 1, true, true, true, true, true, false);

        Assert.Equal(960L * 544 * 8, load);
        Assert.Equal(960L * 544 * 8, store);
        Assert.Equal(0, resolve);
    }

    [Fact]
    public void ColourFormatSizeCounts()
    {
        double bpp = VulkanRenderer.CostBytesPerTexel(Format.R16G16B16A16Sfloat);
        (long load, long store, _) = PassCostRecord.Bytes(100, 100, bpp, 1, true, true, true, false, false, false);

        Assert.Equal(8, bpp);
        Assert.Equal(100L * 100 * 8, load);
        Assert.Equal(100L * 100 * 8, store);
    }

    [Fact]
    public void MultisampledSamplesDiscardedOnlyResolveWritten()
    {
        // the Mali path: samples start DONT_CARE, resolve in the pass, samples and depth discarded at the scene end
        (long load, long store, long resolve) = PassCostRecord.Bytes(960, 544, 4, 4, true, false, false, false, false, true);

        Assert.Equal(0, load);
        Assert.Equal(0, store);
        Assert.Equal(960L * 544 * 4, resolve);
    }

    [Fact]
    public void MultisampledStoreCountsEverySample()
    {
        (long load, long store, _) = PassCostRecord.Bytes(10, 10, 4, 4, true, true, true, true, true, false);

        Assert.Equal(10L * 10 * 4 * 8, load);
        Assert.Equal(10L * 10 * 4 * 8, store);
    }

    [Fact]
    public void DepthOnlyRenderingHasNoColourBytes()
    {
        (long load, long store, long resolve) = PassCostRecord.Bytes(64, 64, 4, 1, false, true, true, true, true, true);

        Assert.Equal(64L * 64 * 4, load);
        Assert.Equal(64L * 64 * 4, store);
        Assert.Equal(0, resolve);
    }

    [Fact]
    public void BlockFormatsCountBytesPerTexel()
    {
        Assert.Equal(1, VulkanRenderer.CostBytesPerTexel(Format.Astc4x4UnormBlock));
        Assert.Equal(0.5, VulkanRenderer.CostBytesPerTexel(Format.BC1RgbaUnormBlock));
        Assert.Equal(4, VulkanRenderer.CostBytesPerTexel(Format.R8G8B8A8Unorm));
    }
}
