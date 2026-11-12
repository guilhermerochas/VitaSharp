using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// Where single-sampled scene renderings are recorded (VulkanRenderer.SceneBreaks.cs): deferred into secondaries on
/// tile-based GPUs and on the render-pass path, begun directly on desktop GPUs with dynamic rendering. Recorded through
/// secondaries, a Metal Gear Solid HD Collection cockpit scene lost a strip of its later draws on a GTX 1650 (the red
/// and dark-blue wedges at the left edge, timing dependent); the replay of the dump recorded directly was right.
/// </summary>
public sealed class SceneBreakPolicyTests
{
    [Fact]
    public void DesktopGpusBeginSingleSampledRenderingsDirectly()
    {
        bool deferred = VulkanRenderer.DefersSingleSampledRenderings(tiledRenderings: false, renderPasses: false, strict: false, deferOnDesktop: false);

        Assert.False(deferred);
    }

    [Fact]
    public void TileBasedGpusAndTheRenderPassPathKeepDeferring()
    {
        Assert.True(VulkanRenderer.DefersSingleSampledRenderings(tiledRenderings: true, renderPasses: false, strict: false, deferOnDesktop: false));
        Assert.True(VulkanRenderer.DefersSingleSampledRenderings(tiledRenderings: false, renderPasses: true, strict: false, deferOnDesktop: false));
        // strict ends renderings for every upload but tile-based GPUs still record them into secondaries
        Assert.True(VulkanRenderer.DefersSingleSampledRenderings(tiledRenderings: true, renderPasses: false, strict: true, deferOnDesktop: false));
    }

    [Fact]
    public void DeferOnDesktopIsAnOptInThatStrictOverrides()
    {
        Assert.True(VulkanRenderer.DefersSingleSampledRenderings(tiledRenderings: false, renderPasses: false, strict: false, deferOnDesktop: true));
        Assert.False(VulkanRenderer.DefersSingleSampledRenderings(tiledRenderings: false, renderPasses: false, strict: true, deferOnDesktop: true));
    }
}
