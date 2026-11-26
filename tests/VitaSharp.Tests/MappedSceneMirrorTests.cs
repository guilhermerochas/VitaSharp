using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// Memory reads of the scene's own surfaces (VulkanRenderer.Mapped.cs, VITASHARP_MAPPED_SCENE_START): the Vita's memory
/// holds a surface as it was before the scene until the scene stores it, so a mirror copy taken during the scene is
/// kept for the rest of it; copies of other surfaces, copies from an earlier scene and reads outside a scene are
/// refreshed when the surface changed.
/// </summary>
public sealed class MappedSceneMirrorTests
{
    [Fact]
    public void TheOwnSurfaceCopyTakenInTheSceneIsKept()
    {
        Assert.True(VulkanRenderer.KeepSceneMirror(true, true, true, 7, 7));
    }

    [Fact]
    public void ACopyFromAnEarlierSceneIsRefreshed()
    {
        Assert.False(VulkanRenderer.KeepSceneMirror(true, true, true, 6, 7));
        Assert.False(VulkanRenderer.KeepSceneMirror(true, true, true, -1, 7));
    }

    [Fact]
    public void AnotherSurfaceIsRefreshed()
    {
        Assert.False(VulkanRenderer.KeepSceneMirror(true, true, false, 7, 7));
    }

    [Fact]
    public void OutsideASceneTheCopyIsRefreshed()
    {
        Assert.False(VulkanRenderer.KeepSceneMirror(true, false, true, 7, 7));
    }

    [Fact]
    public void OffByDefaultEverythingIsRefreshed()
    {
        Assert.False(VulkanRenderer.KeepSceneMirror(false, true, true, 7, 7));
    }
}
