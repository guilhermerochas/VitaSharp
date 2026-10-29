namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Supplies the Vulkan driver instead of the system loader: on Android a user-installed Adreno/Turnip driver loaded
/// through libadrenotools (VitaSharp.Android/Gpu/AdrenoToolsDriverLoader.cs). Installed with
/// <see cref="VulkanLoader.DriverProvider"/> before the renderer is created.
/// </summary>
public interface IVulkanDriverProvider
{
    /// <summary>Shown in logs and info.txt ("Turnip 24.1.0 (custom)").</summary>
    string Description { get; }

    /// <summary>Opens the driver: a native library handle that exports vkGetInstanceProcAddr (and the global
    /// commands), or 0 to fall back to the system loader (the reason is logged by the provider).</summary>
    nint Open();

    /// <summary>
    /// Called once the physical device is known when it reports no textureCompressionBC. Returns true when BCn
    /// textures can be sampled anyway: libadrenotools' BCeNabler patches Qualcomm drivers whose hardware decodes BCn
    /// but which hide the feature (Vita3K: "some Adreno GPUs support BCn textures even though they say they don't").
    /// <paramref name="getPhysicalDeviceFormatProperties"/> is the driver's vkGetPhysicalDeviceFormatProperties.
    /// </summary>
    bool TryEnableBcn(uint vendorId, uint driverVersion, nint getPhysicalDeviceFormatProperties) => false;
}
