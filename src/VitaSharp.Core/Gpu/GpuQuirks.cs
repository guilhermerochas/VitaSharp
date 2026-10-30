using System.Text;

namespace VitaSharp.Core.Gpu;

/// <summary>GPU vendors the renderer distinguishes (PCI / Khronos vendor IDs).</summary>
public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Qualcomm, Arm, Imagination, Samsung, Apple, Broadcom, Software }

/// <summary>Driver families, from VkPhysicalDeviceDriverProperties::driverID (with vendor/version fallbacks).</summary>
public enum GpuDriver
{
    Unknown, NvidiaProprietary, AmdProprietary, AmdOpenSource, Radv, IntelWindows, IntelMesa,
    QualcommProprietary, Turnip, ArmProprietary, Panvk, ImaginationProprietary, ImaginationMesa, SamsungProprietary,
    MoltenVk, Lavapipe, SwiftShader, Broadcom, Nvk, OtherMesa,
}

/// <summary>Optional Vulkan features, extensions and properties the renderer relies on (or deliberately avoids).</summary>
[Flags]
public enum GpuFeature : long
{
    None = 0,
    /// <summary>textureCompressionBC: GXM's UBC1-UBC5/SBC4-SBC5 textures are sampled compressed.</summary>
    TextureCompressionBC = 1L << 0,
    TextureCompressionAstc = 1L << 1,
    TextureCompressionEtc2 = 1L << 2,
    ShaderStorageImageWriteWithoutFormat = 1L << 3,
    /// <summary>sampleRateShading: programmable blending in MSAA scenes runs per sample.</summary>
    SampleRateShading = 1L << 4,
    /// <summary>depthClamp: GXM does not clip at the near plane.</summary>
    DepthClamp = 1L << 5,
    /// <summary>fillModeNonSolid: GXM line/point polygon modes.</summary>
    FillModeNonSolid = 1L << 6,
    GeometryShader = 1L << 7,
    LogicOp = 1L << 8,
    /// <summary>storageBuffer16BitAccess (VkPhysicalDevice16BitStorageFeatures).</summary>
    Storage16Bit = 1L << 9,
    ShaderInt16 = 1L << 10,
    WideLines = 1L << 11,
    OcclusionQueryPrecise = 1L << 12,
    /// <summary>VK_EXT_memory_budget.</summary>
    MemoryBudget = 1L << 13,
    /// <summary>VK_EXT/ARM_rasterization_order_attachment_access with rasterizationOrderColorAttachmentAccess.</summary>
    RasterizationOrderColor = 1L << 14,
    /// <summary>Timestamp queries on the graphics queue (timestampValidBits &gt; 0, timestampPeriod &gt; 0).</summary>
    Timestamps = 1L << 15,
    /// <summary>VK_KHR_push_descriptor (not used by the renderer; listed so the quirk table can veto it).</summary>
    PushDescriptor = 1L << 16,
    IndependentBlend = 1L << 17,
    DepthBiasClamp = 1L << 18,
    SamplerMirrorClampToEdge = 1L << 19,
    SamplerAnisotropy = 1L << 20,
    /// <summary>VK_EXT_custom_border_color (not used).</summary>
    CustomBorderColor = 1L << 21,
    /// <summary>dynamicRendering and synchronization2 (Vulkan 1.3 core). Without it renderings use render passes
    /// (<see cref="GpuQuirk.RenderPasses"/>). VK_KHR_dynamic_rendering on a 1.2 device is not wired up (its
    /// commands have KHR names): such a device uses render passes too.</summary>
    DynamicRendering = 1L << 22,
    /// <summary>Extended dynamic state 1 and 2 (Vulkan 1.3 core). Without it the state is baked into the pipelines
    /// (<see cref="GpuQuirk.StaticExtendedDynamicState"/>, <see cref="GpuQuirk.StaticDepthBiasEnable"/>).</summary>
    ExtendedDynamicState = 1L << 23,
}

/// <summary>Driver workarounds and tile-based-GPU behaviour. Each one is selected by <see cref="GpuQuirkTable"/>.</summary>
[Flags]
public enum GpuQuirk : long
{
    None = 0,
    /// <summary>Tile-based GPU: per-scene load/store ops (DONT_CARE wherever the Vita allows it), in-pass resolves.</summary>
    TileBased = 1L << 0,
    /// <summary>Shader generator: dynamically indexed register-bank words are read/written through switch helpers
    /// instead of a dynamic vector component index (OpVectorExtractDynamic / OpVectorInsertDynamic).</summary>
    DynamicIndexLowering = 1L << 1,
    /// <summary>Cull mode, front face, primitive topology, depth test/write/compare and stencil test/ops are baked
    /// into the pipelines instead of set with VK_EXT_extended_dynamic_state (core 1.3) commands.</summary>
    StaticExtendedDynamicState = 1L << 2,
    /// <summary>Depth bias is always enabled in the pipeline (zero bias when off) instead of the
    /// VK_EXT_extended_dynamic_state2 depthBiasEnable command.</summary>
    StaticDepthBiasEnable = 1L << 3,
    /// <summary>VK_KHR_push_descriptor must not be used (the renderer allocates descriptor sets: no change).</summary>
    AvoidPushDescriptors = 1L << 4,
    /// <summary>vkCmdClearAttachments must not be used inside renderings (the renderer clears with load ops and
    /// vkCmdClearColorImage outside renderings: no change).</summary>
    AvoidClearAttachments = 1L << 5,
    /// <summary>The driver's VkPipelineCache blob is kept per driver build (name, info string and version), not only
    /// per pipelineCacheUUID: custom drivers and some proprietary builds reuse UUIDs across incompatible versions.</summary>
    PipelineCachePerDriverBuild = 1L << 6,
    /// <summary>The device has no 2-sample framebuffers but has 4: SCE_GXM_MULTISAMPLE_2X scenes render with 4 samples.</summary>
    Msaa2xAs4x = 1L << 7,
    /// <summary>A Qualcomm device on a Mesa (Turnip) or user-supplied driver: the proprietary-driver workarounds do
    /// not apply.</summary>
    TurnipDriver = 1L << 8,
    /// <summary>Android: the device can load a user-supplied Adreno/Turnip driver through libadrenotools.</summary>
    CustomDriverCapable = 1L << 9,
    /// <summary>Known proprietary-driver problems with VK_KHR_dynamic_rendering (DuckStation disables it on
    /// Qualcomm, ARM and Imagination blobs): such drivers get <see cref="RenderPasses"/>.</summary>
    DynamicRenderingSuspect = 1L << 10,
    /// <summary>No usable memory budget: eviction pressure is computed against a smaller share of the heap (unified
    /// memory on phones is shared with Android, which kills apps before the heap is full).</summary>
    ConservativeMemoryBudget = 1L << 11,
    /// <summary>Every rendering uses a VkRenderPass and a VkFramebuffer instead of dynamic rendering (the Vulkan 1.1
    /// path): selected without <see cref="GpuFeature.DynamicRendering"/> and on <see cref="DynamicRenderingSuspect"/>
    /// drivers; VITASHARP_GPU_QUIRKS=+RenderPasses forces it on a desktop GPU for testing.</summary>
    RenderPasses = 1L << 12,
}

/// <summary>Driver limits the renderer checks (a subset of VkPhysicalDeviceLimits).</summary>
public sealed record GpuLimits
{
    public uint MaxPushConstantsSize { get; init; } = 128;
    public uint MaxBoundDescriptorSets { get; init; } = 4;
    public uint MaxPerStageDescriptorSamplers { get; init; } = 16;
    public uint MaxPerStageDescriptorSampledImages { get; init; } = 16;
    public uint MaxPerStageDescriptorStorageBuffers { get; init; } = 4;
    public uint MaxPerStageDescriptorInputAttachments { get; init; } = 4;
    public uint MaxPerStageResources { get; init; } = 128;
    public uint MaxDescriptorSetSamplers { get; init; } = 96;
    /// <summary>VkSampleCountFlags bits valid for colour and depth/stencil framebuffers (intersection).</summary>
    public uint FramebufferSampleCounts { get; init; } = 1 | 4;
    public uint MaxImageDimension2D { get; init; } = 4096;
}

/// <summary>What the renderer learned about the device before creating it (filled from Vulkan queries).</summary>
public sealed record GpuDeviceInfo
{
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    /// <summary>VkDriverId (0 = VkPhysicalDeviceDriverProperties unavailable).</summary>
    public uint DriverId { get; init; }
    public uint DriverVersion { get; init; }
    public uint ApiVersion { get; init; }
    public string DeviceName { get; init; } = "";
    public string DriverName { get; init; } = "";
    public string DriverInfo { get; init; } = "";
    public GpuFeature Features { get; init; }
    public GpuLimits Limits { get; init; } = new();
    /// <summary>All device-local heaps are also host-visible / there is a single heap (phones, integrated GPUs).</summary>
    public bool UnifiedMemory { get; init; }
}

/// <summary>Emulated device classes for testing the mobile paths on a desktop GPU (--gpu-profile / VITASHARP_GPU_PROFILE).</summary>
public enum GpuProfile
{
    /// <summary>The real device, quirks selected from its vendor and driver (the default).</summary>
    Native,
    /// <summary>The real device's features, but no vendor quirks (on a phone: checks whether a workaround is needed).</summary>
    Desktop,
    /// <summary>A Qualcomm Adreno 6xx on the proprietary driver (512.x): its missing features, limits and driver quirks.</summary>
    Adreno,
    /// <summary>An ARM Mali (Bifrost/Valhall, proprietary driver): its missing features, limits and quirks.</summary>
    Mali,
    /// <summary>An Adreno 6xx on an old proprietary driver that exposes only Vulkan 1.1: as <see cref="Adreno"/>,
    /// without dynamic rendering and extended dynamic state (the device is created without the 1.2/1.3 features).</summary>
    AdrenoVulkan11,
}

/// <summary>Effective device capabilities: real features and limits, minus what a test profile removes, plus the
/// active quirks. Built once at device creation; every optional renderer path checks it.</summary>
public sealed class GpuCapabilities
{
    public required GpuDeviceInfo Device { get; init; }
    public required GpuProfile Profile { get; init; }
    /// <summary>Vendor and driver the quirks were selected for (the emulated ones under a test profile).</summary>
    public required GpuVendor Vendor { get; init; }
    public required GpuDriver Driver { get; init; }
    public required uint DriverVersion { get; init; }
    public required GpuFeature Features { get; init; }
    public required GpuLimits Limits { get; init; }
    public required GpuQuirk Quirks { get; init; }
    /// <summary>Features the profile or an override removed from the real device.</summary>
    public GpuFeature Masked { get; init; }
    /// <summary>One line per active quirk: why (driver/version range) and where it comes from.</summary>
    public IReadOnlyList<string> QuirkReasons { get; init; } = [];
    /// <summary>VITASHARP_GPU_QUIRKS entries that named nothing.</summary>
    public IReadOnlyList<string> UnknownOverrides { get; init; } = [];

    public bool Has(GpuFeature f) => (Features & f) == f;
    public bool Has(GpuQuirk q) => (Quirks & q) == q;

    /// <summary>Fraction of the largest device-local heap the renderer may fill when there is no VK_EXT_memory_budget.</summary>
    public double FallbackBudgetFraction => Has(GpuQuirk.ConservativeMemoryBudget) ? 0.5 : 0.8;

    /// <summary>Sample count (2 or 4) a SCE_GXM_MULTISAMPLE_2X/4X scene renders with; 0 = no real multisampling possible.</summary>
    public int MsaaSamples(int gxmSamples)
    {
        uint counts = Limits.FramebufferSampleCounts;
        if ((counts & (uint)gxmSamples) != 0) return gxmSamples;
        if (gxmSamples == 2 && Has(GpuQuirk.Msaa2xAs4x) && (counts & 4) != 0) return 4;
        return 0;
    }

    public static string VendorName(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", GpuVendor.Qualcomm => "Qualcomm",
        GpuVendor.Arm => "ARM", GpuVendor.Imagination => "Imagination", GpuVendor.Samsung => "Samsung", GpuVendor.Apple => "Apple",
        GpuVendor.Broadcom => "Broadcom", GpuVendor.Software => "software", _ => "unknown",
    };

    /// <summary>Human-readable driver version: Qualcomm proprietary "512.615", NVIDIA 10.8.8.6 bits, else Vulkan's 10.10.12.</summary>
    public static string FormatDriverVersion(GpuVendor vendor, GpuDriver driver, uint v) => (vendor, driver) switch
    {
        (GpuVendor.Nvidia, _) => $"{v >> 22}.{(v >> 14) & 0xFF}.{(v >> 6) & 0xFF}.{v & 0x3F}",
        (GpuVendor.Intel, GpuDriver.IntelWindows) => $"{v >> 14}.{v & 0x3FFF}",
        (GpuVendor.Qualcomm, GpuDriver.QualcommProprietary) => $"{v >> 22}.{(v >> 12) & 0x3FF}",
        _ => $"{v >> 22}.{(v >> 12) & 0x3FF}.{v & 0xFFF}",
    };

    /// <summary>The startup log / info.txt block: vendor, driver, profile, missing features, limits and quirks.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append($"GPU profile: {Profile.ToString().ToLowerInvariant()}");
        if (Profile is GpuProfile.Adreno or GpuProfile.AdrenoVulkan11 or GpuProfile.Mali) sb.Append($" (emulating {VendorName(Vendor)} {Driver} {FormatDriverVersion(Vendor, Driver, DriverVersion)} on {Device.DeviceName})");
        sb.AppendLine();
        sb.AppendLine($"GPU class: {VendorName(Vendor)}, driver {Driver} {FormatDriverVersion(Vendor, Driver, DriverVersion)}{(Has(GpuQuirk.TileBased) ? ", tile-based" : "")}{(Device.UnifiedMemory ? ", unified memory" : "")}");
        var all = Enum.GetValues<GpuFeature>().Where(f => f != GpuFeature.None).ToArray();
        sb.AppendLine($"GPU features: {string.Join(" ", all.Where(Has).Select(f => f.ToString()))}");
        sb.AppendLine($"GPU missing: {string.Join(" ", all.Where(f => !Has(f)).Select(f => f.ToString() + ((Masked & f) != 0 ? "(profile)" : "")))}");
        var l = Limits;
        sb.AppendLine($"GPU limits: pushConstants {l.MaxPushConstantsSize}, sets {l.MaxBoundDescriptorSets}, per-stage samplers {l.MaxPerStageDescriptorSamplers}/sampled images {l.MaxPerStageDescriptorSampledImages}/storage buffers {l.MaxPerStageDescriptorStorageBuffers}/input attachments {l.MaxPerStageDescriptorInputAttachments}/resources {l.MaxPerStageResources}, set samplers {l.MaxDescriptorSetSamplers}, framebuffer samples {SampleCounts(l.FramebufferSampleCounts)}, max 2D {l.MaxImageDimension2D}");
        sb.Append($"GPU quirks: {(Quirks == GpuQuirk.None ? "none" : string.Join(" ", Enum.GetValues<GpuQuirk>().Where(q => q != GpuQuirk.None && Has(q))))}");
        foreach (var r in QuirkReasons) sb.Append($"\n  {r}");
        foreach (var u in UnknownOverrides) sb.Append($"\n  {Util.EnvVars.GpuQuirks}: '{u}' is not a quirk or feature name (ignored)");
        return sb.ToString();
    }

    static string SampleCounts(uint bits)
    {
        var s = new List<string>();
        for (int i = 0; i < 7; i++) if ((bits & (1u << i)) != 0) s.Add($"{1 << i}x");
        return string.Join("/", s);
    }
}

/// <summary>
/// The quirk table: which workaround applies to which vendor/driver/version, the emulated test profiles, and the
/// selection logic (pure: unit-tested with sample vendor/driver tuples, no Vulkan needed).
/// </summary>
public static class GpuQuirkTable
{
    public const uint VendorNvidia = 0x10DE, VendorAmd = 0x1002, VendorIntel = 0x8086, VendorQualcomm = 0x5143,
        VendorArm = 0x13B5, VendorImagination = 0x1010, VendorSamsung = 0x144D, VendorApple = 0x106B, VendorBroadcom = 0x14E4,
        VendorMesa = 0x10005;

    /// <summary>VkDriverId values (Vulkan 1.2).</summary>
    public const uint DriverAmdProprietary = 1, DriverAmdOpenSource = 2, DriverRadv = 3, DriverNvidiaProprietary = 4,
        DriverIntelWindows = 5, DriverIntelMesa = 6, DriverImaginationProprietary = 7, DriverQualcommProprietary = 8,
        DriverArmProprietary = 9, DriverSwiftShader = 10, DriverBroadcomProprietary = 12, DriverLavapipe = 13, DriverMoltenVk = 14,
        DriverTurnip = 18, DriverV3dv = 19, DriverPanvk = 20, DriverSamsungProprietary = 21, DriverVenus = 22, DriverDozen = 23,
        DriverNvk = 24, DriverImaginationMesa = 25, DriverHoneykrisp = 26;

    /// <summary>VK_MAKE_API_VERSION(0, major, minor, patch): how Qualcomm encodes its "512.615" driver versions.</summary>
    public static uint MakeVersion(uint major, uint minor, uint patch = 0) => (major << 22) | (minor << 12) | patch;

    public static GpuVendor VendorOf(uint vendorId) => vendorId switch
    {
        VendorNvidia => GpuVendor.Nvidia, VendorAmd => GpuVendor.Amd, VendorIntel => GpuVendor.Intel,
        VendorQualcomm => GpuVendor.Qualcomm, VendorArm => GpuVendor.Arm, VendorImagination => GpuVendor.Imagination,
        VendorSamsung => GpuVendor.Samsung, VendorApple => GpuVendor.Apple, VendorBroadcom => GpuVendor.Broadcom,
        VendorMesa => GpuVendor.Software, _ => GpuVendor.Unknown,
    };

    /// <summary>
    /// Driver family from the driver ID; without VkPhysicalDeviceDriverProperties (driverId 0) from the vendor and
    /// version. Qualcomm: Vita3K's rule, the proprietary driver reports major versions of 100 and above (512.x),
    /// Turnip reports Mesa's (2x.y), and some Turnip builds do not say "Turnip" in their name.
    /// </summary>
    public static GpuDriver DriverOf(uint vendorId, uint driverId, uint driverVersion) => driverId switch
    {
        DriverNvidiaProprietary => GpuDriver.NvidiaProprietary,
        DriverAmdProprietary => GpuDriver.AmdProprietary,
        DriverAmdOpenSource => GpuDriver.AmdOpenSource,
        DriverRadv => GpuDriver.Radv,
        DriverIntelWindows => GpuDriver.IntelWindows,
        DriverIntelMesa => GpuDriver.IntelMesa,
        DriverQualcommProprietary => GpuDriver.QualcommProprietary,
        DriverTurnip => GpuDriver.Turnip,
        DriverArmProprietary => GpuDriver.ArmProprietary,
        DriverPanvk => GpuDriver.Panvk,
        DriverImaginationProprietary => GpuDriver.ImaginationProprietary,
        DriverImaginationMesa => GpuDriver.ImaginationMesa,
        DriverSamsungProprietary => GpuDriver.SamsungProprietary,
        DriverMoltenVk => GpuDriver.MoltenVk,
        DriverLavapipe => GpuDriver.Lavapipe,
        DriverSwiftShader => GpuDriver.SwiftShader,
        DriverBroadcomProprietary or DriverV3dv => GpuDriver.Broadcom,
        DriverNvk => GpuDriver.Nvk,
        DriverVenus or DriverDozen or DriverHoneykrisp => GpuDriver.OtherMesa,
        _ => vendorId switch
        {
            VendorQualcomm => driverVersion >> 22 >= 100 ? GpuDriver.QualcommProprietary : GpuDriver.Turnip,
            VendorArm => GpuDriver.ArmProprietary,
            VendorImagination => GpuDriver.ImaginationProprietary,
            VendorNvidia => GpuDriver.NvidiaProprietary,
            VendorSamsung => GpuDriver.SamsungProprietary,
            _ => GpuDriver.Unknown,
        },
    };

    /// <summary>Tile-based renderers: Adreno (proprietary or Turnip), Mali, PowerVR, Apple, VideoCore. Samsung
    /// Xclipse is RDNA (immediate mode) and is not listed.</summary>
    public static bool IsTileBased(GpuVendor v) => v is GpuVendor.Qualcomm or GpuVendor.Arm or GpuVendor.Imagination or GpuVendor.Apple or GpuVendor.Broadcom;

    static bool MobileProprietary(GpuDriver d) => d is GpuDriver.QualcommProprietary or GpuDriver.ArmProprietary or GpuDriver.ImaginationProprietary;

    /// <summary>One row of the table: a quirk, when it applies, and where the knowledge comes from.</summary>
    public sealed record Entry(GpuQuirk Quirk, string Applies, string Source, Func<GpuVendor, GpuDriver, uint, bool> When);

    /// <summary>
    /// The driver quirk table. Version ranges are inclusive lower bound / exclusive upper bound in the vendor's own
    /// encoding; "all versions" entries have no known fixed version (the references disable the feature on every
    /// version of that driver). Capability-derived quirks (tile-based, 2x MSAA as 4x, memory budget) are added by
    /// <see cref="Select"/> from the device's features.
    /// </summary>
    public static readonly Entry[] Table =
    [
        new(GpuQuirk.DynamicIndexLowering, "Qualcomm proprietary, all versions",
            "Dolphin DriverDetails BUG_BROKEN_DYNAMIC_SAMPLER_INDEXING (lowered to a switch); yuzu has_broken_spirv_subgroup_mask_vector_extract_dynamic (OpVectorExtractDynamic on Qualcomm)",
            (v, d, _) => d == GpuDriver.QualcommProprietary),
        new(GpuQuirk.StaticExtendedDynamicState, "Qualcomm proprietary, all versions",
            "yuzu vulkan_device.cpp: 'Qualcomm drivers have broken VK_EXT_extended_dynamic_state' (removed on Qualcomm)",
            (v, d, _) => d == GpuDriver.QualcommProprietary),
        new(GpuQuirk.StaticDepthBiasEnable, "Qualcomm proprietary, all versions",
            "yuzu vulkan_device.cpp: VK_EXT_extended_dynamic_state2 reported by newer Qualcomm drivers but broken",
            (v, d, _) => d == GpuDriver.QualcommProprietary),
        new(GpuQuirk.AvoidPushDescriptors, "Qualcomm, ARM and Imagination proprietary, all versions",
            "yuzu: 'Qualcomm drivers have a slow VK_KHR_push_descriptor implementation'; DuckStation: 'Push descriptor is broken on Adreno v502' (disabled on mobile blobs)",
            (v, d, _) => MobileProprietary(d)),
        new(GpuQuirk.AvoidClearAttachments, "Qualcomm and ARM proprietary, all versions",
            "precautionary: DuckStation ends the pass instead of vkCmdClearAttachments where it misbehaves (NVIDIA, ROV passes); tilers prefer load-op clears",
            (v, d, _) => d is GpuDriver.QualcommProprietary or GpuDriver.ArmProprietary),
        new(GpuQuirk.DynamicRenderingSuspect, "Qualcomm, ARM and Imagination proprietary, all versions",
            "DuckStation vulkan_device.cpp: 'Disabling VK_KHR_dynamic_rendering on broken mobile driver'",
            (v, d, _) => MobileProprietary(d)),
        new(GpuQuirk.PipelineCachePerDriverBuild, "Qualcomm (proprietary and Turnip), all versions",
            "Vita3K/yuzu Android: custom drivers (libadrenotools) replace the system driver without changing vendor/device IDs",
            (v, d, _) => v == GpuVendor.Qualcomm),
        new(GpuQuirk.TurnipDriver, "Qualcomm on Mesa Turnip (driverID MESA_TURNIP, or major version < 100)",
            "Vita3K renderer.cpp: 'say it is a turnip driver if the major driver version is less than 100'",
            (v, d, _) => v == GpuVendor.Qualcomm && d == GpuDriver.Turnip),
        new(GpuQuirk.CustomDriverCapable, "Qualcomm (stock or Turnip), Android",
            "Vita3K support_custom_drivers(): 'vendor ID 0x5143 is Qualcomm, being stock or turnip'",
            (v, d, _) => v == GpuVendor.Qualcomm),
    ];

    /// <summary>Features a test profile removes from the real device (what the emulated device class lacks).</summary>
    public static GpuFeature ProfileMask(GpuProfile p) => p switch
    {
        // Adreno 6xx on the 512.x blob: no BCn (unless patched, see the Android BCeNabler hook), no
        // shaderStorageImageWriteWithoutFormat, no memory budget, sample-rate shading treated as unusable
        GpuProfile.Adreno => GpuFeature.TextureCompressionBC | GpuFeature.ShaderStorageImageWriteWithoutFormat | GpuFeature.SampleRateShading
                             | GpuFeature.MemoryBudget | GpuFeature.FillModeNonSolid | GpuFeature.LogicOp | GpuFeature.Storage16Bit | GpuFeature.WideLines
                             | GpuFeature.PushDescriptor | GpuFeature.CustomBorderColor,
        GpuProfile.AdrenoVulkan11 => ProfileMask(GpuProfile.Adreno) | GpuFeature.DynamicRendering | GpuFeature.ExtendedDynamicState,
        // Mali Bifrost/Valhall: additionally no depth clamp, no geometry shaders, no logic op, no fill modes
        GpuProfile.Mali => GpuFeature.TextureCompressionBC | GpuFeature.ShaderStorageImageWriteWithoutFormat | GpuFeature.SampleRateShading
                           | GpuFeature.MemoryBudget | GpuFeature.DepthClamp | GpuFeature.FillModeNonSolid | GpuFeature.GeometryShader | GpuFeature.LogicOp
                           | GpuFeature.Storage16Bit | GpuFeature.WideLines | GpuFeature.PushDescriptor | GpuFeature.OcclusionQueryPrecise,
        _ => GpuFeature.None,
    };

    /// <summary>Limits of the emulated device class (each is the minimum of this and the real device's).</summary>
    public static GpuLimits ProfileLimits(GpuProfile p, GpuLimits real) => p switch
    {
        GpuProfile.Adreno or GpuProfile.AdrenoVulkan11 => real with
        {
            MaxPushConstantsSize = Math.Min(real.MaxPushConstantsSize, 128),
            FramebufferSampleCounts = real.FramebufferSampleCounts & (1 | 2 | 4),
        },
        GpuProfile.Mali => real with
        {
            MaxPushConstantsSize = Math.Min(real.MaxPushConstantsSize, 128),
            MaxBoundDescriptorSets = Math.Min(real.MaxBoundDescriptorSets, 4),
            MaxPerStageDescriptorSamplers = Math.Min(real.MaxPerStageDescriptorSamplers, 16),
            MaxPerStageDescriptorSampledImages = Math.Min(real.MaxPerStageDescriptorSampledImages, 16),
            MaxPerStageDescriptorInputAttachments = Math.Min(real.MaxPerStageDescriptorInputAttachments, 8),
            MaxPerStageResources = Math.Min(real.MaxPerStageResources, 44),
            MaxDescriptorSetSamplers = Math.Min(real.MaxDescriptorSetSamplers, 768),
            // Mali framebuffers: 1x and 4x only
            FramebufferSampleCounts = real.FramebufferSampleCounts & (1 | 4),
        },
        _ => real,
    };

    /// <summary>The vendor, driver and version a profile pretends to be (quirk selection).</summary>
    public static (GpuVendor Vendor, GpuDriver Driver, uint Version) ProfileIdentity(GpuProfile p, GpuDeviceInfo real) => p switch
    {
        GpuProfile.Adreno => (GpuVendor.Qualcomm, GpuDriver.QualcommProprietary, MakeVersion(512, 615)),
        GpuProfile.AdrenoVulkan11 => (GpuVendor.Qualcomm, GpuDriver.QualcommProprietary, MakeVersion(512, 454)),
        GpuProfile.Mali => (GpuVendor.Arm, GpuDriver.ArmProprietary, MakeVersion(38, 1)),
        _ => (VendorOf(real.VendorId), DriverOf(real.VendorId, real.DriverId, real.DriverVersion), real.DriverVersion),
    };

    public static GpuProfile ParseProfile(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "adreno" or "qualcomm" => GpuProfile.Adreno,
        "adreno-vk11" or "adreno-vulkan11" or "vk11" => GpuProfile.AdrenoVulkan11,
        "mali" or "arm" => GpuProfile.Mali,
        "desktop" or "none" => GpuProfile.Desktop,
        _ => GpuProfile.Native,
    };

    /// <summary>
    /// Builds the capabilities. <paramref name="overrides"/> (VITASHARP_GPU_QUIRKS): comma-separated quirk or
    /// feature names, '+' (or nothing) forces a quirk on, '-' turns a quirk off or removes a feature, e.g.
    /// "-StaticExtendedDynamicState,+TileBased,-Timestamps".
    /// </summary>
    public static GpuCapabilities Select(GpuDeviceInfo dev, GpuProfile profile = GpuProfile.Native, string? overrides = null)
    {
        var (vendor, driver, version) = ProfileIdentity(profile, dev);
        var mask = ProfileMask(profile);
        var features = dev.Features & ~mask;
        var limits = ProfileLimits(profile, dev.Limits);
        var quirks = GpuQuirk.None;
        var reasons = new List<string>();
        if (profile != GpuProfile.Desktop)
        {
            foreach (var e in Table)
            {
                if (!e.When(vendor, driver, version)) continue;
                quirks |= e.Quirk;
                reasons.Add($"{e.Quirk}: {e.Applies} [{e.Source}]");
            }
        }
        // capability-derived
        if (IsTileBased(vendor) && profile != GpuProfile.Desktop)
        {
            quirks |= GpuQuirk.TileBased;
            reasons.Add($"{GpuQuirk.TileBased}: {GpuCapabilities.VendorName(vendor)} renders in tiles");
        }
        if ((limits.FramebufferSampleCounts & 2) == 0 && (limits.FramebufferSampleCounts & 4) != 0)
        {
            quirks |= GpuQuirk.Msaa2xAs4x;
            reasons.Add($"{GpuQuirk.Msaa2xAs4x}: no 2-sample framebuffers (framebuffer sample counts 0x{limits.FramebufferSampleCounts:X})");
        }
        if ((quirks & GpuQuirk.DynamicRenderingSuspect) != 0)
        {
            quirks |= GpuQuirk.RenderPasses;
            reasons.Add($"{GpuQuirk.RenderPasses}: {GpuQuirk.DynamicRenderingSuspect} driver, renderings use render passes (DuckStation)");
        }
        if ((features & GpuFeature.MemoryBudget) == 0 && (dev.UnifiedMemory || IsTileBased(vendor)) && profile != GpuProfile.Desktop)
        {
            quirks |= GpuQuirk.ConservativeMemoryBudget;
            reasons.Add($"{GpuQuirk.ConservativeMemoryBudget}: no VK_EXT_memory_budget on a {(dev.UnifiedMemory ? "unified-memory" : "mobile")} device");
        }

        var unknown = new List<string>();
        foreach (var raw in (overrides ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool off = raw.StartsWith('-');
            string name = raw.TrimStart('+', '-');
            if (Enum.TryParse<GpuQuirk>(name, true, out var q) && q != GpuQuirk.None && Enum.IsDefined(q))
            {
                if (off) quirks &= ~q; else quirks |= q;
                reasons.Add($"{q}: forced {(off ? "off" : "on")} by {Util.EnvVars.GpuQuirks}");
            }
            else if (Enum.TryParse<GpuFeature>(name, true, out var f) && f != GpuFeature.None && Enum.IsDefined(f) && off)
            {
                features &= ~f;
                mask |= f & dev.Features;
            }
            else unknown.Add(raw);
        }
        // what the device (or the profile/overrides) cannot do at all: not overridable
        if ((features & GpuFeature.DynamicRendering) == 0 && (quirks & GpuQuirk.RenderPasses) == 0)
        {
            quirks |= GpuQuirk.RenderPasses;
            reasons.Add($"{GpuQuirk.RenderPasses}: no dynamic rendering (Vulkan {dev.ApiVersion >> 22}.{(dev.ApiVersion >> 12) & 0x3FF} device or profile)");
        }
        if ((features & GpuFeature.ExtendedDynamicState) == 0)
        {
            foreach (var q in new[] { GpuQuirk.StaticExtendedDynamicState, GpuQuirk.StaticDepthBiasEnable })
            {
                if ((quirks & q) != 0) continue;
                quirks |= q;
                reasons.Add($"{q}: no extended dynamic state (Vulkan 1.3 core)");
            }
        }
        // a quirk forced off must not leave its reason line behind
        reasons.RemoveAll(r => !r.Contains(" forced ") && Enum.TryParse<GpuQuirk>(r[..r.IndexOf(':')], out var q) && (quirks & q) == 0);
        return new GpuCapabilities
        {
            Device = dev, Profile = profile, Vendor = vendor, Driver = driver, DriverVersion = version,
            Features = features, Limits = limits, Quirks = quirks, Masked = mask & dev.Features,
            QuirkReasons = reasons, UnknownOverrides = unknown,
        };
    }
}
