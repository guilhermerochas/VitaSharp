using VitaSharp.Core.Gpu;
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
    public void SwitchedOffEverythingIsRefreshed()
    {
        Assert.False(VulkanRenderer.KeepSceneMirror(false, true, true, 7, 7));
    }

    [Fact]
    public void TileBasedGpusKeepTheSceneCopyByDefault()
    {
        Assert.True(VulkanRenderer.MappedSceneStartOn(null, true));
        Assert.False(VulkanRenderer.MappedSceneStartOn(null, false));
    }

    [Fact]
    public void TheSwitchOverridesTheDefault()
    {
        Assert.False(VulkanRenderer.MappedSceneStartOn(false, true));
        Assert.True(VulkanRenderer.MappedSceneStartOn(true, false));
    }

    /// <summary>The device decides, not the title: the default follows the tile-based quirk the GPU table selects from
    /// the vendor and driver (Mali, Adreno blob and Turnip on; NVIDIA, AMD and Intel off). The renderer passes
    /// <c>Caps.Has(GpuQuirk.TileBased)</c> (VulkanRenderer.Mapped.cs); no renderer is made here, since one sets the
    /// shader generator's process-wide options that the shader tests running beside this class read.</summary>
    [Theory]
    [InlineData(GpuQuirkTable.VendorArm, GpuQuirkTable.DriverArmProprietary, 54u, true)]
    [InlineData(GpuQuirkTable.VendorQualcomm, GpuQuirkTable.DriverQualcommProprietary, 512u, true)]
    [InlineData(GpuQuirkTable.VendorQualcomm, GpuQuirkTable.DriverTurnip, 24u, true)]
    [InlineData(GpuQuirkTable.VendorNvidia, GpuQuirkTable.DriverNvidiaProprietary, 546u, false)]
    [InlineData(GpuQuirkTable.VendorAmd, GpuQuirkTable.DriverAmdProprietary, 2u, false)]
    [InlineData(GpuQuirkTable.VendorIntel, GpuQuirkTable.DriverIntelWindows, 101u, false)]
    public void TheDefaultFollowsTheDevice(uint vendor, uint driver, uint major, bool expected)
    {
        GpuCapabilities caps = GpuQuirkTable.Select(new GpuDeviceInfo
        {
            VendorId = vendor,
            DeviceId = 0x1234,
            DriverId = driver,
            DriverVersion = GpuQuirkTable.MakeVersion(major, 1, 0),
            ApiVersion = GpuQuirkTable.MakeVersion(1, 3, 250),
            DeviceName = "test GPU",
        });
        Assert.Equal(expected, VulkanRenderer.MappedSceneStartOn(null, caps.Has(GpuQuirk.TileBased)));
    }
}
