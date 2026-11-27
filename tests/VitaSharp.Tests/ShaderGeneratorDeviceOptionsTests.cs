using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// A renderer sets the shader generator's process-wide options for its device (far plane, per-sample fetch, memory
/// stores...) and puts the earlier ones back when it is disposed, so a renderer made with another device profile
/// leaves nothing to the shader tests that run after it in the same collection (they used to see the phone's far
/// plane and fail now and then: ShaderGeneratorVersionTests, MaliDeviceTests, IndexRegisterTests).
/// </summary>
[Collection("guest")]
public sealed class ShaderGeneratorDeviceOptionsTests
{
    [SoftwareVulkanTheory]
    [InlineData("mali")]
    [InlineData("adreno")]
    [InlineData(null)]
    public void ADisposedRendererPutsTheGeneratorOptionsBack(string? gpuProfile)
    {
        using IDisposable driver = SoftwareVulkan.Use();
        ShaderGenerator.DeviceOptions before = ShaderGenerator.CaptureDeviceOptions();
        var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false, GpuProfile = gpuProfile });
        renderer.Dispose();
        Assert.Equal(before, ShaderGenerator.CaptureDeviceOptions());
    }

    [SoftwareVulkanFact]
    public void ThePhoneProfileChangesTheOptionsWhileTheRendererLives()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        ShaderGenerator.DeviceOptions before = ShaderGenerator.CaptureDeviceOptions();
        var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false, GpuProfile = "adreno" });
        try
        {
            Assert.True(ShaderGenerator.LowerDynamicIndexing);
            Assert.NotEqual(before, ShaderGenerator.CaptureDeviceOptions());
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
