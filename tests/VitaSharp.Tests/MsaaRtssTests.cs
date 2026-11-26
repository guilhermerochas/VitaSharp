using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// MSAA scenes rendered to the single-sampled surface (VulkanRenderer.MsaaRtss.cs, VITASHARP_MSAA_RTSS): the path is
/// taken only with the extension enabled, on the render-pass path with renderings begun when they end, for a surface
/// that never needed its samples outside a rendering; everything else keeps the multisampled image. Its tile traffic:
/// the surface loaded and stored once per pixel, depth/stencil per sample, no separate resolve.
/// </summary>
public sealed class MsaaRtssTests
{
    [Fact]
    public void TakenWithTheExtensionOnTheDeferredRenderPassPath()
    {
        Assert.True(VulkanRenderer.UseRtss(true, true, false, false));
    }

    [Fact]
    public void WithoutTheExtensionTheMultisampledImage()
    {
        Assert.False(VulkanRenderer.UseRtss(false, true, false, false));
    }

    [Fact]
    public void DynamicRenderingOrDirectRenderingsKeepTheMultisampledImage()
    {
        Assert.False(VulkanRenderer.UseRtss(true, false, false, false));
        Assert.False(VulkanRenderer.UseRtss(true, true, true, false));
    }

    [Fact]
    public void ASurfaceThatNeededItsSamplesKeepsTheMultisampledImage()
    {
        Assert.False(VulkanRenderer.UseRtss(true, true, false, true));
    }

    [Fact]
    public void TileTrafficCountsTheSurfaceOncePerPixel()
    {
        // 960x544 RGBA16F, 4 samples: surface loaded and stored at 8 bytes per pixel, depth (4 bytes) per sample
        (long load, long store, long resolve) = PassCostRecord.Bytes(960, 544, 8, 4, true, true, true, true, false, false, colorSamples: 1);

        Assert.Equal(960L * 544 * 8 + 960L * 544 * 4 * 4, load);
        Assert.Equal(960L * 544 * 8, store);
        Assert.Equal(0, resolve);
    }
}
