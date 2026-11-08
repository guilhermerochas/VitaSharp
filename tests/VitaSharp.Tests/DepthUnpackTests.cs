using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// Multisampled depth stored by the scene (force store) and read as a sample-sized texture: 4X keeps 2x2 samples per
/// pixel, 2X keeps two samples one above the other (Ninja Gaiden Sigma Plus's depth of field reads a 960x544 2X
/// scene's depth as 960x1088).
/// </summary>
public class DepthUnpackTests
{
    [Fact]
    public void FourXUnpacksTwoByTwo()
    {
        string fs = VulkanRenderer.DepthUnpackFs(0);
        Assert.Contains("texelFetch(src, p >> 1, (p.y & 1) * 2 + (p.x & 1))", fs);
    }

    [Fact]
    public void TwoXUnpacksOneAboveTheOther()
    {
        // full width, every row pair is one pixel row: the upper texel is the upper sample
        Assert.Contains("texelFetch(src, ivec2(p.x, p.y >> 1), 1 - (p.y & 1))", VulkanRenderer.DepthUnpackFs(1));
        // emulated with 4 samples (no 2-sample framebuffers): samples 0 (top) and 3 (bottom)
        Assert.Contains("texelFetch(src, ivec2(p.x, p.y >> 1), (p.y & 1) * 3)", VulkanRenderer.DepthUnpackFs(2));
        Assert.Contains("gl_FragDepth", VulkanRenderer.DepthUnpackFs(1));
    }
}
