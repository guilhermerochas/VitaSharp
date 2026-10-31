using System.Diagnostics.CodeAnalysis;
using System.Text;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// The raw Vulkan capabilities of the device, logged once at startup before the device is created (so a device the
/// renderer cannot use still leaves the full picture in the log, there being no vulkaninfo on a phone): every Vulkan
/// 1.0-1.3 feature the device lacks, the limits the renderer depends on, the formats it prefers, and the extension list.
/// <see cref="Core.Gpu.GpuCapabilities.Describe"/> is the renderer's interpretation of it (features used, quirks).
/// </summary>
static unsafe class VulkanCapabilityReport
{
    /// <summary>Names of the Bool32 fields of a feature struct that are false (true ones go to <paramref name="have"/> when given).</summary>
    public static List<string> Missing<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(in T features, List<string>? have = null) where T : struct
    {
        var missing = new List<string>();
        object boxed = features;
        foreach (var f in typeof(T).GetFields())
        {
            if (f.FieldType != typeof(Bool32)) continue;
            bool on = ((Bool32)f.GetValue(boxed)!).Value != 0;
            string name = char.ToLowerInvariant(f.Name[0]) + f.Name[1..];
            if (!on) missing.Add(name);
            else have?.Add(name);
        }
        return missing;
    }

    public static List<string> Lines(in PhysicalDeviceProperties props, in PhysicalDeviceFeatures f10, in PhysicalDeviceVulkan11Features f11,
        in PhysicalDeviceVulkan12Features f12, in PhysicalDeviceVulkan13Features f13, uint api, HashSet<string> exts, Func<Format, string> format)
    {
        var r = new List<string> { $"Vulkan 1.0 features missing: {Join(Missing(f10))}" };
        if (api >= Vk.Version12)
        {
            r.Add($"Vulkan 1.1 features missing: {Join(Missing(f11))}");
            r.Add($"Vulkan 1.2 features missing: {Join(Missing(f12))}");
        }
        if (api >= Vk.Version13) r.Add($"Vulkan 1.3 features missing: {Join(Missing(f13))}");
        var l = props.Limits;
        r.Add($"Vulkan limits: maxClipDistances {l.MaxClipDistances}, vertex outputs {l.MaxVertexOutputComponents}, fragment inputs {l.MaxFragmentInputComponents}, " +
                     $"colour attachments {l.MaxColorAttachments}, viewport {l.MaxViewportDimensions[0]}x{l.MaxViewportDimensions[1]}, max 2D {l.MaxImageDimension2D}, " +
                     $"LOD bias {l.MaxSamplerLodBias}, anisotropy {l.MaxSamplerAnisotropy}, line width {l.LineWidthRange[0]}-{l.LineWidthRange[1]}, " +
                     $"point size {l.PointSizeRange[0]}-{l.PointSizeRange[1]}, uniform buffer range {l.MaxUniformBufferRange}, storage buffer range {l.MaxStorageBufferRange}, " +
                     $"min storage buffer alignment {l.MinStorageBufferOffsetAlignment}, push constants {l.MaxPushConstantsSize}, timestamp period {l.TimestampPeriod}");
        r.Add("Vulkan formats: " + string.Join(", ", new[]
        {
            Format.D24UnormS8Uint, Format.D32SfloatS8Uint, Format.D16Unorm, Format.R16G16B16A16Sfloat, Format.B10G11R11UfloatPack32,
            Format.A2B10G10R10UnormPack32, Format.R5G6B5UnormPack16, Format.BC1RgbaUnormBlock, Format.Astc4x4UnormBlock, Format.Etc2R8G8B8A8UnormBlock,
        }.Select(x => $"{x} {format(x)}")));
        r.Add($"Vulkan device extensions ({exts.Count}): {string.Join(" ", exts.Order(StringComparer.Ordinal))}");
        return r;
    }

    static string Join(List<string> names) => names.Count == 0 ? "none" : string.Join(" ", names);
}
