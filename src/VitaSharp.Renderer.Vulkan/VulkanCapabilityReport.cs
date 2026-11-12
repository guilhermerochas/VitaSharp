using Silk.NET.Core;
using Silk.NET.Vulkan;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The raw Vulkan capabilities of the device, logged once at startup before the device is created (so a device the
/// renderer cannot use still leaves the full picture in the log, there being no vulkaninfo on a phone): every Vulkan
/// 1.0-1.3 feature the device lacks, the limits the renderer depends on, the formats it prefers, and the extension list.
/// <see cref="Core.Gpu.GpuCapabilities.Describe"/> is the renderer's interpretation of it (features used, quirks).
/// </summary>
internal static unsafe class VulkanCapabilityReport
{
    /// <summary>Names of the Bool32 fields of a feature struct that are false (true ones go to <paramref name="have"/> when given).</summary>
    public static List<string> Missing<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(in T features, List<string>? have = null) where T : struct
    {
        var missing = new List<string>();
        object boxed = features;
        foreach (FieldInfo fieldInfo in typeof(T).GetFields())
        {
            if (fieldInfo.FieldType != typeof(Bool32))
            {
                continue;
            }

            bool on = ((Bool32)fieldInfo.GetValue(boxed)!).Value != 0;
            string name = char.ToLowerInvariant(fieldInfo.Name[0]) + fieldInfo.Name[1..];
            if (!on)
            {
                missing.Add(name);
            }
            else
            {
                have?.Add(name);
            }
        }
        return missing;
    }

    public static List<string> Lines(in PhysicalDeviceProperties props, in PhysicalDeviceFeatures physicalDeviceFeatures, in PhysicalDeviceVulkan11Features physicalDeviceVulkan11Features,
        in PhysicalDeviceVulkan12Features physicalDeviceVulkan12Features, in PhysicalDeviceVulkan13Features physicalDeviceVulkan13Features, uint api, HashSet<string> exts, Func<Format, string> format, Func<Format, bool>? vertex = null)
    {
        var r = new List<string> { $"Vulkan 1.0 features missing: {Join(Missing(physicalDeviceFeatures))}" };
        if (api >= Vk.Version12)
        {
            r.Add($"Vulkan 1.1 features missing: {Join(Missing(physicalDeviceVulkan11Features))}");
            r.Add($"Vulkan 1.2 features missing: {Join(Missing(physicalDeviceVulkan12Features))}");
        }
        if (api >= Vk.Version13)
        {
            r.Add($"Vulkan 1.3 features missing: {Join(Missing(physicalDeviceVulkan13Features))}");
        }

        PhysicalDeviceLimits physicalDeviceLimits = props.Limits;
        r.Add($"Vulkan limits: maxClipDistances {physicalDeviceLimits.MaxClipDistances}, vertex outputs {physicalDeviceLimits.MaxVertexOutputComponents}, fragment inputs {physicalDeviceLimits.MaxFragmentInputComponents}, " +
                     $"colour attachments {physicalDeviceLimits.MaxColorAttachments}, viewport {physicalDeviceLimits.MaxViewportDimensions[0]}x{physicalDeviceLimits.MaxViewportDimensions[1]}, max 2D {physicalDeviceLimits.MaxImageDimension2D}, " +
                     $"LOD bias {physicalDeviceLimits.MaxSamplerLodBias}, anisotropy {physicalDeviceLimits.MaxSamplerAnisotropy}, line width {physicalDeviceLimits.LineWidthRange[0]}-{physicalDeviceLimits.LineWidthRange[1]}, " +
                     $"point size {physicalDeviceLimits.PointSizeRange[0]}-{physicalDeviceLimits.PointSizeRange[1]}, uniform buffer range {physicalDeviceLimits.MaxUniformBufferRange}, storage buffer range {physicalDeviceLimits.MaxStorageBufferRange}, " +
                     $"min storage buffer alignment {physicalDeviceLimits.MinStorageBufferOffsetAlignment}, push constants {physicalDeviceLimits.MaxPushConstantsSize}, timestamp period {physicalDeviceLimits.TimestampPeriod}");
        r.Add("Vulkan formats: " + string.Join(", ", new[]
        {
            Format.D24UnormS8Uint, Format.D32SfloatS8Uint, Format.D16Unorm, Format.R16G16B16A16Sfloat, Format.B10G11R11UfloatPack32,
            Format.A2B10G10R10UnormPack32, Format.R5G6B5UnormPack16, Format.BC1RgbaUnormBlock, Format.Astc4x4UnormBlock, Format.Etc2R8G8B8A8UnormBlock,
        }.Select(x => $"{x} {format(x)}")));
        if (vertex != null)
        {
            r.Add($"Vulkan vertex formats missing (of the GXM attribute formats): {Join(Formats.AllAttributeFormats().Where(x => !vertex(x)).Select(x => x.ToString()).ToList())}");
        }

        r.Add($"Vulkan device extensions ({exts.Count}): {string.Join(" ", exts.Order(StringComparer.Ordinal))}");
        return r;
    }

    private static string Join(List<string> names) => names.Count == 0 ? "none" : string.Join(" ", names);
}
