using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>Window surface provided by the front-end (desktop window now, Android surface later).</summary>
public interface IPresentationSurface
{
    string[] RequiredInstanceExtensions { get; }
    SurfaceKHR CreateSurface(Instance instance);
    (int Width, int Height) FramebufferSize { get; }
}

public static class VkCheck
{
    public static void Ok(this Result r, string what)
    {
        if (r == Result.Success) return;
        if (IsOutOfMemory(r)) throw new VkOutOfMemoryException(r, what, 0, MemCat.Other, "");
        throw new InvalidOperationException($"Vulkan {what} failed: {r}");
    }

    public static bool IsOutOfMemory(Result r) => r is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory;
}

/// <summary>
/// What the renderer's Vulkan memory is used for (VRAM accounting: logs, PERF vram line, renderer.txt and every
/// allocation-failure message). Image memory is sub-allocated from <see cref="ImageMemoryPool"/> pages and counted by
/// what each image is for; buffers are counted at allocation (<see cref="VkCore.AllocateMemory"/>).
/// </summary>
public enum MemCat
{
    /// <summary>Colour render targets (single-sampled surfaces, the CPU framebuffer).</summary>
    RenderTarget,
    /// <summary>Multisampled images of render targets (native MSAA samples, 2x supersampled images).</summary>
    Msaa,
    /// <summary>Depth/stencil targets (including multisampled ones).</summary>
    Depth,
    /// <summary>Textures uploaded from guest memory (the texture cache).</summary>
    Texture,
    /// <summary>Views of render targets with their own image: regions, reinterpreted (alias) and mip-chained
    /// textures, depth/stencil textures, scene-start copies, framebuffer-fetch copies.</summary>
    Region,
    /// <summary>Buffers the pipelines read: the CDRAM mirror (mapped uniform/memory reads), dummy buffers.</summary>
    PipelineBuffer,
    /// <summary>Per-frame upload rings and their overflow buffers.</summary>
    UploadRing,
    /// <summary>Readback buffers: render-target writebacks, frame dumps, trace snapshots.</summary>
    Readback,
    /// <summary>Debug mode: replay ring, NaN scan buffers.</summary>
    DebugRing,
    /// <summary>GPU dump export buffers.</summary>
    GpuDump,
    Other,
}

/// <summary>A Vulkan allocation failed with ErrorOutOfDeviceMemory / ErrorOutOfHostMemory even after the renderer
/// evicted what it could. The message carries the heap budgets and what the renderer holds.</summary>
public sealed class VkOutOfMemoryException(Result result, string what, ulong size, MemCat category, string budget)
    : InvalidOperationException($"Vulkan {what} failed: {result}" + (size > 0 ? $" ({size / 1048576.0:F1} MB for {category})" : "") + (budget.Length > 0 ? $"; {budget}" : ""))
{
    public Result Result { get; } = result;
    public ulong Size { get; } = size;
    public MemCat Category { get; } = category;
}

/// <summary>Instance, device, queue and memory management.</summary>
public sealed unsafe class VkCore : IDisposable
{
    public readonly Vk Vk = VulkanLoader.Load();
    public Instance Instance;
    public PhysicalDevice Physical;
    public Device Device;
    public Queue Queue;
    public uint QueueFamily;
    public KhrSurface? SurfaceExt;
    public KhrSwapchain? SwapchainExt;
    public SurfaceKHR Surface;
    public PhysicalDeviceProperties Properties;
    public PhysicalDeviceMemoryProperties MemoryProperties;
    public Format DepthFormat;
    public string DeviceName = "";
    ExtDebugUtils? _debugUtils;
    DebugUtilsMessengerEXT _messenger;

    /// <summary>Effective capabilities and driver quirks (built in <see cref="CreateDevice"/>; see Core/Gpu/GpuQuirks.cs).</summary>
    public GpuCapabilities Caps = null!;

    /// <param name="gpuProfile">adreno|mali|desktop: behave as that device class (--gpu-profile); null = VITASHARP_GPU_PROFILE</param>
    public VkCore(IPresentationSurface? surface, bool validation, string? gpuProfile = null)
    {
        CreateInstance(surface, validation);
        if (surface != null)
        {
            Vk.TryGetInstanceExtension(Instance, out KhrSurface se);
            SurfaceExt = se;
            Surface = surface.CreateSurface(Instance);
        }
        PickDevice();
        CreateDevice(surface != null, GpuQuirkTable.ParseProfile(gpuProfile ?? Environment.GetEnvironmentVariable(EnvVars.GpuProfile)));
        DepthFormat = PickDepthFormat();
    }

    void CreateInstance(IPresentationSurface? surface, bool validation)
    {
        var exts = new List<string>(surface?.RequiredInstanceExtensions ?? []);
        var layers = new List<string>();
        if (validation)
        {
            uint lc = 0;
            Vk.EnumerateInstanceLayerProperties(ref lc, null);
            var props = new LayerProperties[lc];
            fixed (LayerProperties* p = props) Vk.EnumerateInstanceLayerProperties(ref lc, p);
            foreach (var l in props)
                if (Marshal.PtrToStringAnsi((nint)l.LayerName) == "VK_LAYER_KHRONOS_validation") layers.Add("VK_LAYER_KHRONOS_validation");
            if (layers.Count > 0) exts.Add(ExtDebugUtils.ExtensionName);
        }
        // MoltenVK (macOS) and other non-conformant implementations are only enumerated by loaders >= 1.3.216 when
        // the application opts in with VK_KHR_portability_enumeration
        var available = InstanceExtensions();
        var flags = InstanceCreateFlags.None;
        if (available.Contains(PortabilityEnumeration))
        {
            exts.Add(PortabilityEnumeration);
            flags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;
        }
        foreach (var e in exts.Where(e => !available.Contains(e)).ToList())
            Log.Warn($"Vulkan instance extension {e} is not available ({VulkanLoader.InstallHint()})");
        var appName = (byte*)SilkMarshal.StringToPtr("VitaSharp");
        var app = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            ApplicationVersion = new Version32(0, 1, 0),
            PEngineName = appName,
            EngineVersion = new Version32(0, 1, 0),
            ApiVersion = Vk.Version13,
        };
        var extPtrs = (byte**)SilkMarshal.StringArrayToPtr(exts);
        var layerPtrs = (byte**)SilkMarshal.StringArrayToPtr(layers);
        var ci = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            Flags = flags,
            PApplicationInfo = &app,
            EnabledExtensionCount = (uint)exts.Count,
            PpEnabledExtensionNames = extPtrs,
            EnabledLayerCount = (uint)layers.Count,
            PpEnabledLayerNames = layerPtrs,
        };
        Vk.CreateInstance(in ci, null, out Instance).Ok("vkCreateInstance");
        SilkMarshal.Free((nint)appName);
        SilkMarshal.Free((nint)extPtrs);
        SilkMarshal.Free((nint)layerPtrs);

        if (layers.Count > 0 && Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils du))
        {
            _debugUtils = du;
            var mci = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback,
            };
            du.CreateDebugUtilsMessenger(Instance, in mci, null, out _messenger);
        }
    }

    const string PortabilityEnumeration = "VK_KHR_portability_enumeration";
    const string PortabilitySubset = "VK_KHR_portability_subset";

    HashSet<string> InstanceExtensions()
    {
        uint n = 0;
        Vk.EnumerateInstanceExtensionProperties((byte*)null, ref n, null);
        var props = new ExtensionProperties[n];
        fixed (ExtensionProperties* p = props) Vk.EnumerateInstanceExtensionProperties((byte*)null, ref n, p);
        var set = new HashSet<string>();
        foreach (var e in props) set.Add(Marshal.PtrToStringAnsi((nint)e.ExtensionName) ?? "");
        return set;
    }

    HashSet<string> DeviceExtensions(PhysicalDevice d)
    {
        uint n = 0;
        Vk.EnumerateDeviceExtensionProperties(d, (byte*)null, ref n, null);
        var props = new ExtensionProperties[n];
        fixed (ExtensionProperties* p = props) Vk.EnumerateDeviceExtensionProperties(d, (byte*)null, ref n, p);
        var set = new HashSet<string>();
        foreach (var e in props) set.Add(Marshal.PtrToStringAnsi((nint)e.ExtensionName) ?? "");
        return set;
    }

    static uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type, DebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        var msg = Marshal.PtrToStringAnsi((nint)data->PMessage) ?? "";
        Log.WarnOnce("vk:" + msg[..Math.Min(msg.Length, 120)], $"[Vulkan] {msg}");
        return Vk.False;
    }

    void PickDevice()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, ref count, null);
        if (count == 0) throw new InvalidOperationException("No Vulkan device found");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices) Vk.EnumeratePhysicalDevices(Instance, ref count, p);
        PhysicalDevice best = default;
        int bestScore = -1;
        var rejected = new List<string>();
        foreach (var d in devices)
        {
            Vk.GetPhysicalDeviceProperties(d, out var props);
            if (props.ApiVersion < Vk.Version13)
            {
                rejected.Add($"{Marshal.PtrToStringAnsi((nint)props.DeviceName)} (Vulkan {props.ApiVersion >> 22}.{(props.ApiVersion >> 12) & 0x3FF})");
                continue;
            }
            int score = props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3000,
                PhysicalDeviceType.IntegratedGpu => 2000,
                _ => 1000,
            };
            if (score > bestScore) { bestScore = score; best = d; }
        }
        if (bestScore < 0)
            throw new InvalidOperationException($"No Vulkan 1.3 capable device found (found: {string.Join(", ", rejected)}). " +
                                                (OperatingSystem.IsMacOS() ? "MoltenVK 1.3 or newer is required. " : "") + VulkanLoader.InstallHint());
        Physical = best;
        Vk.GetPhysicalDeviceProperties(Physical, out Properties);
        Vk.GetPhysicalDeviceMemoryProperties(Physical, out MemoryProperties);
        fixed (byte* n = Properties.DeviceName) DeviceName = Marshal.PtrToStringAnsi((nint)n) ?? "";
        Log.Info($"Vulkan device: {DeviceName} (API {Properties.ApiVersion >> 22}.{(Properties.ApiVersion >> 12) & 0x3FF})");
    }

    const string RoaaExt = "VK_EXT_rasterization_order_attachment_access", RoaaArm = "VK_ARM_rasterization_order_attachment_access";
    const string PushDescriptorExt = "VK_KHR_push_descriptor", CustomBorderColorExt = "VK_EXT_custom_border_color";

    /// <summary>VK_EXT/ARM_rasterization_order_attachment_access is enabled: framebuffer-fetch reads of the colour
    /// attachment are ordered against earlier fragments without per-draw barriers (Adreno, Mali).</summary>
    public bool RasterizationOrderColor;

    /// <summary>The driver's identity and what it supports, for <see cref="GpuQuirkTable.Select"/>.</summary>
    GpuDeviceInfo DescribeDevice(in PhysicalDeviceFeatures f, in PhysicalDeviceVulkan11Features f11, in PhysicalDeviceVulkan12Features f12,
        HashSet<string> exts, bool roaa, uint timestampBits, bool bcPatched)
    {
        uint driverId = 0;
        string driverName = "", driverInfo = "";
        if (Properties.ApiVersion >= Vk.Version12)
        {
            var drv = new PhysicalDeviceDriverProperties { SType = StructureType.PhysicalDeviceDriverProperties };
            var p2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &drv };
            Vk.GetPhysicalDeviceProperties2(Physical, &p2);
            driverId = (uint)drv.DriverID;
            driverName = Marshal.PtrToStringAnsi((nint)drv.DriverName) ?? "";
            driverInfo = Marshal.PtrToStringAnsi((nint)drv.DriverInfo) ?? "";
        }
        var feats = GpuFeature.None;
        void Set(bool on, GpuFeature g) { if (on) feats |= g; }
        Set(f.TextureCompressionBC || bcPatched, GpuFeature.TextureCompressionBC);
        Set(f.TextureCompressionAstcLdr, GpuFeature.TextureCompressionAstc);
        Set(f.TextureCompressionEtc2, GpuFeature.TextureCompressionEtc2);
        Set(f.ShaderStorageImageWriteWithoutFormat, GpuFeature.ShaderStorageImageWriteWithoutFormat);
        Set(f.SampleRateShading, GpuFeature.SampleRateShading);
        Set(f.DepthClamp, GpuFeature.DepthClamp);
        Set(f.FillModeNonSolid, GpuFeature.FillModeNonSolid);
        Set(f.GeometryShader, GpuFeature.GeometryShader);
        Set(f.LogicOp, GpuFeature.LogicOp);
        Set(f11.StorageBuffer16BitAccess, GpuFeature.Storage16Bit);
        Set(f.ShaderInt16, GpuFeature.ShaderInt16);
        Set(f.WideLines, GpuFeature.WideLines);
        Set(f.OcclusionQueryPrecise, GpuFeature.OcclusionQueryPrecise);
        Set(f.IndependentBlend, GpuFeature.IndependentBlend);
        Set(f.DepthBiasClamp, GpuFeature.DepthBiasClamp);
        Set(f.SamplerAnisotropy, GpuFeature.SamplerAnisotropy);
        Set(f12.SamplerMirrorClampToEdge, GpuFeature.SamplerMirrorClampToEdge);
        Set(exts.Contains(MemoryBudgetExtension), GpuFeature.MemoryBudget);
        Set(exts.Contains(PushDescriptorExt), GpuFeature.PushDescriptor);
        Set(exts.Contains(CustomBorderColorExt), GpuFeature.CustomBorderColor);
        Set(roaa, GpuFeature.RasterizationOrderColor);
        Set(timestampBits > 0 && Properties.Limits.TimestampPeriod > 0, GpuFeature.Timestamps);
        var l = Properties.Limits;
        bool unified = true;
        for (int i = 0; i < MemoryProperties.MemoryTypeCount; i++)
        {
            var t = MemoryProperties.MemoryTypes[i].PropertyFlags;
            if ((t & MemoryPropertyFlags.DeviceLocalBit) != 0 && (t & MemoryPropertyFlags.HostVisibleBit) == 0) unified = false;
        }
        return new GpuDeviceInfo
        {
            VendorId = Properties.VendorID, DeviceId = Properties.DeviceID, DriverId = driverId, DriverVersion = Properties.DriverVersion,
            ApiVersion = Properties.ApiVersion, DeviceName = DeviceName, DriverName = driverName, DriverInfo = driverInfo, Features = feats,
            UnifiedMemory = unified || MemoryProperties.MemoryHeapCount == 1,
            Limits = new GpuLimits
            {
                MaxPushConstantsSize = l.MaxPushConstantsSize, MaxBoundDescriptorSets = l.MaxBoundDescriptorSets,
                MaxPerStageDescriptorSamplers = l.MaxPerStageDescriptorSamplers, MaxPerStageDescriptorSampledImages = l.MaxPerStageDescriptorSampledImages,
                MaxPerStageDescriptorStorageBuffers = l.MaxPerStageDescriptorStorageBuffers, MaxPerStageDescriptorInputAttachments = l.MaxPerStageDescriptorInputAttachments,
                MaxPerStageResources = l.MaxPerStageResources, MaxDescriptorSetSamplers = l.MaxDescriptorSetSamplers,
                FramebufferSampleCounts = (uint)(l.FramebufferColorSampleCounts & l.FramebufferDepthSampleCounts & l.FramebufferStencilSampleCounts),
                MaxImageDimension2D = l.MaxImageDimension2D,
            },
        };
    }

    /// <summary>Driver identity strings (name, info, version) for <see cref="GpuQuirk.PipelineCachePerDriverBuild"/>.</summary>
    public string DriverBuildId => $"{Caps.Device.DriverId}:{Caps.Device.DriverName}:{Caps.Device.DriverInfo}:{Caps.Device.DriverVersion:X8}";

    void CreateDevice(bool present, GpuProfile profile)
    {
        uint qc = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(Physical, ref qc, null);
        var families = new QueueFamilyProperties[qc];
        fixed (QueueFamilyProperties* p = families) Vk.GetPhysicalDeviceQueueFamilyProperties(Physical, ref qc, p);
        QueueFamily = uint.MaxValue;
        for (uint i = 0; i < qc; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0) continue;
            if (present)
            {
                SurfaceExt!.GetPhysicalDeviceSurfaceSupport(Physical, i, Surface, out var supported);
                if (!supported) continue;
            }
            QueueFamily = i;
            break;
        }
        if (QueueFamily == uint.MaxValue) throw new InvalidOperationException("No graphics queue");

        float prio = 1.0f;
        var qci = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = QueueFamily, QueueCount = 1, PQueuePriorities = &prio };

        var devExts = DeviceExtensions(Physical);
        bool portability = devExts.Contains(PortabilitySubset);

        // what the device supports (1.1/1.2/1.3 feature structs, rasterization-order attachment access, plus the
        // portability subset on MoltenVK)
        string? roaaName = devExts.Contains(RoaaExt) ? RoaaExt : devExts.Contains(RoaaArm) ? RoaaArm : null;
        var hasRoaa = new PhysicalDeviceRasterizationOrderAttachmentAccessFeaturesEXT { SType = StructureType.PhysicalDeviceRasterizationOrderAttachmentAccessFeaturesExt };
        var has13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features, PNext = roaaName != null ? &hasRoaa : null };
        var has12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &has13 };
        var has11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &has12 };
        var hasPortability = new PhysicalDevicePortabilitySubsetFeaturesKHR { SType = StructureType.PhysicalDevicePortabilitySubsetFeaturesKhr, PNext = &has11 };
        var supported2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = portability ? &hasPortability : &has11 };
        Vk.GetPhysicalDeviceFeatures2(Physical, &supported2);
        if (!has13.DynamicRendering || !has13.Synchronization2)
            throw new InvalidOperationException($"{DeviceName} lacks dynamicRendering/synchronization2 (Vulkan 1.3 features). {VulkanLoader.InstallHint()}");
        if (!supported2.Features.ShaderClipDistance)
            throw new InvalidOperationException($"{DeviceName} lacks shaderClipDistance (needed for the GXM far plane). {VulkanLoader.InstallHint()}");

        // capabilities and quirks: the real features, minus what a test profile (--gpu-profile) removes
        // Android: a Qualcomm driver that hides BCn may be patched to sample it (libadrenotools BCeNabler)
        bool bcPatched = false;
        if (!supported2.Features.TextureCompressionBC && VulkanLoader.DriverProvider is { } provider)
        {
            var fn = Vk.GetInstanceProcAddr(Instance, "vkGetPhysicalDeviceFormatProperties");
            try { bcPatched = fn != 0 && provider.TryEnableBcn(Properties.VendorID, Properties.DriverVersion, fn); }
            catch (Exception e) { Log.Warn($"BCn patch failed: {e.Message}"); }
            if (bcPatched) Log.Info("BCn textures enabled by the driver patch (BCeNabler)");
        }
        Caps = GpuQuirkTable.Select(DescribeDevice(supported2.Features, has11, has12, devExts, roaaName != null && hasRoaa.RasterizationOrderColorAttachmentAccess,
            families[QueueFamily].TimestampValidBits, bcPatched), profile, Environment.GetEnvironmentVariable(EnvVars.GpuQuirks));
        bool Has(GpuFeature f) => Caps.Has(f);
        // the sample counts every multisampling check reads: the profile's (e.g. Mali: no 2x)
        var counts = (SampleCountFlags)Caps.Limits.FramebufferSampleCounts;
        Properties.Limits.FramebufferColorSampleCounts &= counts | SampleCountFlags.Count1Bit;
        Properties.Limits.FramebufferDepthSampleCounts &= counts | SampleCountFlags.Count1Bit;
        Properties.Limits.FramebufferStencilSampleCounts &= counts | SampleCountFlags.Count1Bit;
        _sampleCountMask = counts | SampleCountFlags.Count1Bit;

        SamplerMirrorClampToEdge = Has(GpuFeature.SamplerMirrorClampToEdge);
        Formats.MirrorClampToEdgeSupported = SamplerMirrorClampToEdge;

        RasterizationOrderColor = Has(GpuFeature.RasterizationOrderColor);
        var fRoaa = new PhysicalDeviceRasterizationOrderAttachmentAccessFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceRasterizationOrderAttachmentAccessFeaturesExt, RasterizationOrderColorAttachmentAccess = true,
        };
        var f13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features, DynamicRendering = true, Synchronization2 = true,
            PNext = RasterizationOrderColor ? &fRoaa : null };
        var f12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &f13, SamplerMirrorClampToEdge = SamplerMirrorClampToEdge };
        // VK_KHR_portability_subset must be enabled when present, and each of its features must be enabled to be used:
        // enable whatever the device has, then degrade where the renderer depends on a missing one
        var fPortability = hasPortability with { PNext = &f12 };
        var feats = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = Has(GpuFeature.SamplerAnisotropy),
            FillModeNonSolid = Has(GpuFeature.FillModeNonSolid),
            WideLines = Has(GpuFeature.WideLines),
            // a patched driver samples BCn without the feature (it still reports none, so it is not enabled)
            TextureCompressionBC = supported2.Features.TextureCompressionBC && Has(GpuFeature.TextureCompressionBC),
            ShaderStorageImageWriteWithoutFormat = Has(GpuFeature.ShaderStorageImageWriteWithoutFormat),
            IndependentBlend = Has(GpuFeature.IndependentBlend),
            DepthBiasClamp = Has(GpuFeature.DepthBiasClamp),
            FragmentStoresAndAtomics = supported2.Features.FragmentStoresAndAtomics,
            OcclusionQueryPrecise = Has(GpuFeature.OcclusionQueryPrecise),
            DepthClamp = Has(GpuFeature.DepthClamp),
            ShaderClipDistance = true,
            SampleRateShading = Has(GpuFeature.SampleRateShading),
        };
        OcclusionQueryPrecise = Has(GpuFeature.OcclusionQueryPrecise);
        DepthClamp = Has(GpuFeature.DepthClamp);
        TextureCompressionBC = Has(GpuFeature.TextureCompressionBC);
        FillModeNonSolid = Has(GpuFeature.FillModeNonSolid);
        WideLines = Has(GpuFeature.WideLines);
        SampleRateShading = Has(GpuFeature.SampleRateShading);
        Timestamps = Has(GpuFeature.Timestamps);
        if (portability) ApplyPortabilityLimits(hasPortability, ref feats);

        var exts = new List<string>();
        if (present) exts.Add(KhrSwapchain.ExtensionName);
        if (portability) exts.Add(PortabilitySubset);
        // per-heap budget and this process's usage (VRAM logs, eviction under memory pressure)
        MemoryBudgetSupported = Has(GpuFeature.MemoryBudget);
        if (MemoryBudgetSupported) exts.Add(MemoryBudgetExtension);
        if (RasterizationOrderColor) exts.Add(roaaName!);
        var extPtrs = (byte**)SilkMarshal.StringArrayToPtr(exts);
        var dci = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = portability ? &fPortability : &f12,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &qci,
            PEnabledFeatures = &feats,
            EnabledExtensionCount = (uint)exts.Count,
            PpEnabledExtensionNames = extPtrs,
        };
        Vk.CreateDevice(Physical, in dci, null, out Device).Ok("vkCreateDevice");
        SilkMarshal.Free((nint)extPtrs);
        Vk.GetDeviceQueue(Device, QueueFamily, 0, out Queue);
        if (present)
        {
            Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain sw);
            SwapchainExt = sw;
        }
        // capabilities and active quirks, once at startup (also in the F10 capture's info.txt)
        foreach (var line in Caps.Describe().Split('\n')) Log.Info(line);
    }

    public bool TextureCompressionBC, FillModeNonSolid, WideLines, OcclusionQueryPrecise, SamplerMirrorClampToEdge;
    /// <summary>sampleRateShading: fragment shaders that read the framebuffer in a multisampled scene run per sample
    /// (as GXM's NATIVECOLOR_MSAA programs do). Without it they read sample 0 and their output covers the pixel.</summary>
    public bool SampleRateShading;
    /// <summary>Timestamp queries on the graphics queue (PERF gpu line).</summary>
    public bool Timestamps;
    SampleCountFlags _sampleCountMask = (SampleCountFlags)0x7F;

    /// <summary>Sample counts usable for an attachment of <paramref name="format"/> with <paramref name="usage"/>
    /// (image format properties, mutable views allowed).</summary>
    public SampleCountFlags ImageSampleCounts(Format format, ImageUsageFlags usage, ImageCreateFlags flags = ImageCreateFlags.CreateMutableFormatBit)
    {
        var r = Vk.GetPhysicalDeviceImageFormatProperties(Physical, format, ImageType.Type2D, ImageTiling.Optimal, usage, flags, out var props);
        return r == Result.Success ? props.SampleCounts & _sampleCountMask : SampleCountFlags.Count1Bit;
    }
    /// <summary>depthClamp: GXM does not clip at the near plane, fragments in front of it get depth 0 (clamped per
    /// fragment). Without the feature the near plane clips instead.</summary>
    public bool DepthClamp;

    /// <summary>True on a portability-subset implementation (MoltenVK) that reports no triangle fan support. GXM fan
    /// draws are still issued as fans (MoltenVK converts them internally for most draws); a fan-to-list index rewrite
    /// can key off this flag.</summary>
    public bool TriangleFansUnsupported;

    /// <summary>
    /// MoltenVK reports what Metal cannot do through VkPhysicalDevicePortabilitySubsetFeaturesKHR. The renderer's
    /// dependencies on it are handled here, so the draw code needs no Metal-specific paths:
    ///  - samplerMipLodBias: Metal samplers have no LOD bias; the bias limit is forced to 0, so the existing clamp of
    ///    the GXM sampler bias yields 0 (textures may look slightly blurrier/sharper than on hardware);
    ///  - pointPolygons: GXM point fill mode would be invalid; polygon modes fall back to fill;
    ///  - triangleFans, separateStencilMaskRef, constantAlphaColorBlendFactors: logged (no fallback yet).
    /// Geometry/tessellation shaders and 64-bit floats are not used by the renderer.
    /// </summary>
    void ApplyPortabilityLimits(in PhysicalDevicePortabilitySubsetFeaturesKHR p, ref PhysicalDeviceFeatures feats)
    {
        var missing = new List<string>();
        if (!p.SamplerMipLodBias) { Properties.Limits.MaxSamplerLodBias = 0; missing.Add("samplerMipLodBias (sampler LOD bias ignored)"); }
        if (!p.PointPolygons) { FillModeNonSolid = false; feats.FillModeNonSolid = false; missing.Add("pointPolygons (wireframe/point fill drawn filled)"); }
        if (!p.TriangleFans) { TriangleFansUnsupported = true; missing.Add("triangleFans"); }
        if (!p.SeparateStencilMaskRef) missing.Add("separateStencilMaskRef");
        if (!p.ConstantAlphaColorBlendFactors) missing.Add("constantAlphaColorBlendFactors");
        if (!p.ImageViewFormatSwizzle) missing.Add("imageViewFormatSwizzle");
        if (!p.ImageViewFormatReinterpretation) missing.Add("imageViewFormatReinterpretation");
        if (!p.MutableComparisonSamplers) missing.Add("mutableComparisonSamplers");
        Log.Info($"Vulkan portability subset (MoltenVK) on {DeviceName}; missing: {(missing.Count == 0 ? "nothing" : string.Join(", ", missing))}");
    }

    Format PickDepthFormat()
    {
        // D24S8 matches the Vita's S8D24 surfaces bit for bit, so depth can be copied back into guest layout
        foreach (var f in new[] { Format.D24UnormS8Uint, Format.D32SfloatS8Uint })
        {
            Vk.GetPhysicalDeviceFormatProperties(Physical, f, out var props);
            const FormatFeatureFlags need = FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.TransferSrcBit;
            if ((props.OptimalTilingFeatures & need) == need) return f;
        }
        return Format.D32SfloatS8Uint;
    }

    public bool SupportsVertexFormat(Format f)
    {
        Vk.GetPhysicalDeviceFormatProperties(Physical, f, out var props);
        return (props.BufferFeatures & FormatFeatureFlags.VertexBufferBit) != 0;
    }

    public bool SupportsSampled(Format f)
    {
        Vk.GetPhysicalDeviceFormatProperties(Physical, f, out var props);
        return (props.OptimalTilingFeatures & FormatFeatureFlags.SampledImageBit) != 0;
    }

    public bool SupportsColorAttachment(Format f)
    {
        Vk.GetPhysicalDeviceFormatProperties(Physical, f, out var props);
        return (props.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBlendBit) != 0;
    }

    /// <summary>Colour attachment without blending (integer formats never blend).</summary>
    public bool SupportsUnblendedColorAttachment(Format f)
    {
        Vk.GetPhysicalDeviceFormatProperties(Physical, f, out var props);
        return (props.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBit) != 0;
    }

    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags flags)
    {
        for (int i = 0; i < MemoryProperties.MemoryTypeCount; i++)
            if ((typeBits & (1u << i)) != 0 && (MemoryProperties.MemoryTypes[i].PropertyFlags & flags) == flags) return (uint)i;
        throw new InvalidOperationException($"No memory type for {flags}");
    }

    public (Buffer Buffer, DeviceMemory Memory, nint Mapped) CreateHostBuffer(ulong size, BufferUsageFlags usage, MemCat category)
    {
        var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Vk.CreateBuffer(Device, in bci, null, out var buf).Ok("vkCreateBuffer");
        try
        {
            Vk.GetBufferMemoryRequirements(Device, buf, out var req);
            uint type;
            try { type = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit); }
            catch { type = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit); }
            var mem = AllocateMemory(req.Size, type, category, "vkAllocateMemory(host buffer)");
            Vk.BindBufferMemory(Device, buf, mem, 0);
            void* p;
            var r = Vk.MapMemory(Device, mem, 0, size, 0, &p);
            if (r != Result.Success) { FreeMemory(mem); r.Ok("vkMapMemory"); }
            return (buf, mem, (nint)p);
        }
        catch
        {
            Vk.DestroyBuffer(Device, buf, null);
            throw;
        }
    }

    public (Buffer Buffer, DeviceMemory Memory) CreateDeviceBuffer(ulong size, BufferUsageFlags usage, MemCat category)
    {
        var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Vk.CreateBuffer(Device, in bci, null, out var buf).Ok("vkCreateBuffer");
        try
        {
            Vk.GetBufferMemoryRequirements(Device, buf, out var req);
            var mem = AllocateMemory(req.Size, FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit), category, "vkAllocateMemory(device buffer)");
            Vk.BindBufferMemory(Device, buf, mem, 0);
            return (buf, mem);
        }
        catch
        {
            Vk.DestroyBuffer(Device, buf, null);
            throw;
        }
    }

    public void DestroyBuffer(Buffer b, DeviceMemory m)
    {
        Vk.DestroyBuffer(Device, b, null);
        FreeMemory(m);
    }

    // =================================================================== memory accounting and budget

    const string MemoryBudgetExtension = "VK_EXT_memory_budget";
    /// <summary>VK_EXT_memory_budget is enabled: <see cref="Heaps"/> reports per-heap budgets and this process's usage.</summary>
    public bool MemoryBudgetSupported;

    /// <summary>
    /// Test switch VITASHARP_VRAM_BUDGET_MB=N: the renderer may hold at most N MB of device-local memory (image pool
    /// pages + device buffers). An allocation beyond it fails as ErrorOutOfDeviceMemory without reaching the driver,
    /// which exercises the eviction path on any GPU. 0 = no cap.
    /// </summary>
    public readonly ulong BudgetCap = ParseBudgetCap(Environment.GetEnvironmentVariable(EnvVars.VramBudgetMb));

    static ulong ParseBudgetCap(string? s) => ulong.TryParse(s?.Trim(), out var mb) && mb > 0 ? mb << 20 : 0;

    public const int MemCats = (int)MemCat.Other + 1;
    readonly long[] _poolUsed = new long[MemCats];      // image pool sub-allocations (device-local)
    readonly long[] _directDevice = new long[MemCats];  // whole allocations in device-local heaps
    readonly long[] _directHost = new long[MemCats];    // whole allocations in host heaps
    long _poolPages, _peakDevice, _peakHost, _failures, _capRefusals;
    readonly Dictionary<ulong, (ulong Size, MemCat Cat, bool Device, bool Pooled)> _allocations = new();

    /// <summary>
    /// Called when an allocation failed with an out-of-memory error: frees memory the renderer can recreate (level 0:
    /// caches not used recently; level 1: everything not used by the frame being recorded) and returns whether
    /// anything was freed, so the allocation is retried. Runs on the allocating (GPU worker) thread.
    /// </summary>
    public Func<ulong, bool, int, bool>? Reclaim;
    public const int ReclaimLevels = 2;
    /// <summary>Text appended to allocation failures (budgets and what the renderer holds by category).</summary>
    public Func<string>? DescribeMemory;

    public bool IsDeviceLocal(uint type) => (MemoryProperties.MemoryTypes[(int)type].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0;

    /// <summary>Device-local memory the renderer holds (image pool pages + device buffers).</summary>
    public long DeviceBytes
    {
        get
        {
            long sum = Volatile.Read(ref _poolPages);
            for (int i = 0; i < MemCats; i++) sum += Volatile.Read(ref _directDevice[i]);
            return sum;
        }
    }

    public long HostBytes
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < MemCats; i++) sum += Volatile.Read(ref _directHost[i]);
            return sum;
        }
    }

    public long PoolPageBytes => Volatile.Read(ref _poolPages);
    public long PeakDeviceBytes => Volatile.Read(ref _peakDevice);
    public long PeakHostBytes => Volatile.Read(ref _peakHost);
    public long AllocationFailures => Volatile.Read(ref _failures);
    public long CapRefusals => Volatile.Read(ref _capRefusals);

    /// <summary>Bytes the renderer holds for <paramref name="cat"/>: device-local (pool sub-allocations + device buffers) and host.</summary>
    public (long Device, long Host) CategoryBytes(MemCat cat) =>
        (Volatile.Read(ref _poolUsed[(int)cat]) + Volatile.Read(ref _directDevice[(int)cat]), Volatile.Read(ref _directHost[(int)cat]));

    internal void PoolAccount(MemCat cat, long delta) => Interlocked.Add(ref _poolUsed[(int)cat], delta);

    /// <summary>One vkAllocateMemory with accounting; the budget cap refuses device-local allocations past it. No eviction.</summary>
    public Result TryAllocateMemory(ulong size, uint type, MemCat cat, bool pooled, out DeviceMemory mem)
    {
        mem = default;
        bool device = IsDeviceLocal(type);
        if (device && BudgetCap > 0 && (ulong)DeviceBytes + size > BudgetCap)
        {
            Interlocked.Increment(ref _capRefusals);
            return Result.ErrorOutOfDeviceMemory;
        }
        var mai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = size, MemoryTypeIndex = type };
        var r = Vk.AllocateMemory(Device, in mai, null, out mem);
        if (r != Result.Success) return r;
        lock (_allocations) _allocations[mem.Handle] = (size, cat, device, pooled);
        if (pooled) Interlocked.Add(ref _poolPages, (long)size);
        else if (device) Interlocked.Add(ref _directDevice[(int)cat], (long)size);
        else Interlocked.Add(ref _directHost[(int)cat], (long)size);
        if (device) { long d = DeviceBytes; if (d > _peakDevice) _peakDevice = d; }
        else { long h = HostBytes; if (h > _peakHost) _peakHost = h; }
        return r;
    }

    /// <summary>vkAllocateMemory for a whole buffer: on out-of-memory the renderer reclaims memory and it is retried;
    /// throws <see cref="VkOutOfMemoryException"/> when nothing helped.</summary>
    public DeviceMemory AllocateMemory(ulong size, uint type, MemCat cat, string what)
    {
        var r = TryAllocateMemory(size, type, cat, false, out var mem);
        if (r == Result.Success) return mem;
        if (!VkCheck.IsOutOfMemory(r)) throw new InvalidOperationException($"Vulkan {what} failed: {r}");
        for (int level = 0; level < ReclaimLevels && !ReclaimHopeless(size); level++)
        {
            if (Reclaim?.Invoke(size, IsDeviceLocal(type), level) != true) continue;
            r = TryAllocateMemory(size, type, cat, false, out mem);
            if (r == Result.Success) return mem;
        }
        MarkHopeless(size);
        throw OutOfMemory(r, what, size, cat);
    }

    ulong _hopelessSize = ulong.MaxValue;
    long _hopelessUntil;

    /// <summary>
    /// An allocation at least this large failed even after every reclaim level less than 2 s ago: retrying the
    /// reclaim for each such request (e.g. every draw needing a buffer that cannot fit) would evict every cache over
    /// and over for nothing, so those requests fail at once until then.
    /// </summary>
    public bool ReclaimHopeless(ulong size) => size >= _hopelessSize && Environment.TickCount64 < _hopelessUntil;

    public void MarkHopeless(ulong size)
    {
        _hopelessSize = Environment.TickCount64 < _hopelessUntil ? Math.Min(_hopelessSize, size) : size;
        _hopelessUntil = Environment.TickCount64 + 2000;
    }

    public VkOutOfMemoryException OutOfMemory(Result r, string what, ulong size, MemCat cat)
    {
        Interlocked.Increment(ref _failures);
        string desc;
        try { desc = DescribeMemory?.Invoke() ?? MemorySummary(); }
        catch (Exception e) { desc = $"(memory summary failed: {e.Message})"; }
        return new VkOutOfMemoryException(r, what, size, cat, desc);
    }

    public void FreeMemory(DeviceMemory mem)
    {
        if (mem.Handle == 0) return;
        (ulong Size, MemCat Cat, bool Device, bool Pooled) a;
        bool known;
        lock (_allocations) known = _allocations.Remove(mem.Handle, out a);
        if (known)
        {
            if (a.Pooled) Interlocked.Add(ref _poolPages, -(long)a.Size);
            else if (a.Device) Interlocked.Add(ref _directDevice[(int)a.Cat], -(long)a.Size);
            else Interlocked.Add(ref _directHost[(int)a.Cat], -(long)a.Size);
        }
        Vk.FreeMemory(Device, mem, null);
    }

    public readonly record struct HeapInfo(int Index, ulong Size, bool DeviceLocal, ulong Budget, ulong Usage);

    /// <summary>Memory heaps with this process's usage and its budget (VK_EXT_memory_budget; 0 when unsupported).
    /// Any thread (a physical-device query).</summary>
    public HeapInfo[] Heaps()
    {
        int n = (int)MemoryProperties.MemoryHeapCount;
        var heaps = new HeapInfo[n];
        var budget = new PhysicalDeviceMemoryBudgetPropertiesEXT { SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt };
        var p2 = new PhysicalDeviceMemoryProperties2 { SType = StructureType.PhysicalDeviceMemoryProperties2, PNext = &budget };
        if (MemoryBudgetSupported) Vk.GetPhysicalDeviceMemoryProperties2(Physical, &p2);
        for (int i = 0; i < n; i++)
        {
            var h = MemoryProperties.MemoryHeaps[i];
            heaps[i] = new HeapInfo(i, h.Size, (h.Flags & MemoryHeapFlags.DeviceLocalBit) != 0,
                MemoryBudgetSupported ? budget.HeapBudget[i] : 0, MemoryBudgetSupported ? budget.HeapUsage[i] : 0);
        }
        return heaps;
    }

    /// <summary>
    /// How full device-local memory is, 0..1+: with the budget cap, the renderer's device-local bytes over the cap;
    /// with VK_EXT_memory_budget, the fullest device-local heap's usage over its budget (the budget shrinks when other
    /// processes use the GPU); otherwise the renderer's bytes over 80% of the largest device-local heap (50% on
    /// mobile/unified-memory devices, <see cref="GpuQuirk.ConservativeMemoryBudget"/>).
    /// </summary>
    public double DevicePressure()
    {
        if (BudgetCap > 0) return DeviceBytes / (double)BudgetCap;
        double worst = 0;
        ulong largest = 0;
        foreach (var h in Heaps())
        {
            if (!h.DeviceLocal) continue;
            largest = Math.Max(largest, h.Size);
            if (h.Budget > 0) worst = Math.Max(worst, h.Usage / (double)h.Budget);
        }
        if (MemoryBudgetSupported) return worst;
        return largest == 0 ? 0 : DeviceBytes / (largest * Caps.FallbackBudgetFraction);
    }

    static string Mb(double bytes) => $"{bytes / 1048576.0:F0} MB";

    /// <summary>"heap 0 device-local 3.9 GB: budget 3.4 GB, usage 1.2 GB (35%); heap 1 ..." (sizes only without the extension).</summary>
    public string HeapSummary()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var h in Heaps())
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"heap {h.Index} {(h.DeviceLocal ? "device-local" : "host")} {h.Size / 1073741824.0:F1} GB");
            if (h.Budget > 0) sb.Append($": budget {Mb(h.Budget)}, usage {Mb(h.Usage)} ({100.0 * h.Usage / h.Budget:F0}%)");
        }
        if (!MemoryBudgetSupported) sb.Append(" (no VK_EXT_memory_budget: budget/usage unknown)");
        return sb.ToString();
    }

    /// <summary>Heap budgets plus the renderer's own allocations by category.</summary>
    public string MemorySummary(Func<MemCat, string>? detail = null)
    {
        var sb = new System.Text.StringBuilder("VRAM ");
        sb.Append(HeapSummary());
        sb.Append($"; renderer holds {Mb(DeviceBytes)} device-local (image pool pages {Mb(PoolPageBytes)}");
        long used = 0;
        for (int i = 0; i < MemCats; i++) used += Volatile.Read(ref _poolUsed[i]);
        sb.Append($", {Mb(PoolPageBytes - used)} of them free)");
        sb.Append($" + {Mb(HostBytes)} host:");
        for (int i = 0; i < MemCats; i++)
        {
            var (d, h) = CategoryBytes((MemCat)i);
            string extra = detail?.Invoke((MemCat)i) ?? "";
            if (d == 0 && h == 0 && extra.Length == 0) continue;
            sb.Append($" {CategoryName((MemCat)i)} {Mb(d)}");
            if (h > 0) sb.Append($" (+{Mb(h)} host)");
            if (extra.Length > 0) sb.Append($" [{extra}]");
            sb.Append(',');
        }
        if (sb[^1] == ',') sb.Length--;
        sb.Append($"; peak {Mb(PeakDeviceBytes)} device-local, {Mb(PeakHostBytes)} host");
        if (BudgetCap > 0) sb.Append($"; test cap VITASHARP_VRAM_BUDGET_MB={BudgetCap >> 20} ({CapRefusals} allocations refused)");
        return sb.ToString();
    }

    public static string CategoryName(MemCat c) => c switch
    {
        MemCat.RenderTarget => "render targets",
        MemCat.Msaa => "MSAA/supersample images",
        MemCat.Depth => "depth/stencil",
        MemCat.Texture => "textures",
        MemCat.Region => "regions/reinterpret/mip views",
        MemCat.PipelineBuffer => "pipeline buffers",
        MemCat.UploadRing => "upload rings",
        MemCat.Readback => "readback/writeback",
        MemCat.DebugRing => "debug rings",
        MemCat.GpuDump => "GPU dump",
        _ => "other",
    };

    public void Dispose()
    {
        Vk.DeviceWaitIdle(Device);
        Vk.DestroyDevice(Device, null);
        if (Surface.Handle != 0) SurfaceExt?.DestroySurface(Instance, Surface, null);
        if (_debugUtils != null) _debugUtils.DestroyDebugUtilsMessenger(Instance, _messenger, null);
        Vk.DestroyInstance(Instance, null);
    }
}

/// <summary>
/// Sub-allocates device-local memory for images from large pages (first fit, coalescing free list). Each allocation
/// is counted by what it is for (<see cref="MemCat"/>). When a new page cannot be allocated the renderer is asked to
/// free memory (<see cref="VkCore.Reclaim"/>) and the free space and smaller pages are tried before giving up; empty
/// pages are returned to the driver by <see cref="TrimEmptyPages"/>.
/// </summary>
public sealed unsafe class ImageMemoryPool : IDisposable
{
    const ulong PageSize = 128ul << 20;
    /// <summary>Page size tried when a full page cannot be allocated (memory pressure or the budget cap).</summary>
    const ulong SmallPageSize = 16ul << 20;
    readonly VkCore _vk;

    sealed class Page
    {
        public DeviceMemory Memory;
        public ulong Size;
        public uint Type;
        public ulong Used;
        public readonly SortedList<ulong, ulong> Free = new(); // offset -> size
    }

    readonly List<Page> _pages = new();
    long _pagesFreed;

    public ImageMemoryPool(VkCore vk) => _vk = vk;

    public readonly record struct Allocation(DeviceMemory Memory, ulong Offset, ulong Size, object Page, MemCat Category);

    public int PageCount => _pages.Count;
    public long PagesFreed => _pagesFreed;

    /// <summary>Bytes in pages not allocated to any image.</summary>
    public ulong FreeBytes
    {
        get
        {
            ulong free = 0;
            foreach (var p in _pages) free += p.Size - p.Used;
            return free;
        }
    }

    public Allocation Allocate(MemoryRequirements req, MemCat cat)
    {
        uint type = _vk.FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        ulong align = Math.Max(req.Alignment, 256);
        if (TryExisting(type, req.Size, align, cat, out var a)) return a;
        ulong need = (req.Size + align - 1) & ~(align - 1);
        ulong full = Math.Max(PageSize, need), small = Math.Max(SmallPageSize, need);
        var r = TryNewPage(full, type, req.Size, align, cat, out a);
        if (r == Result.Success) return a;
        if (!VkCheck.IsOutOfMemory(r)) throw new InvalidOperationException($"Vulkan vkAllocateMemory(image page) failed: {r}");
        // out of memory: free what the renderer can recreate, then use the space that freed up, a new page, or a
        // smaller one (a full page may not fit although the image would)
        bool hopeless = _vk.ReclaimHopeless(need);
        for (int level = 0; level < VkCore.ReclaimLevels; level++)
        {
            if (!hopeless && _vk.Reclaim?.Invoke(need, true, level) == true)
            {
                if (TryExisting(type, req.Size, align, cat, out a)) return a;
                if (level == 0 && (r = TryNewPage(full, type, req.Size, align, cat, out a)) == Result.Success) return a;
            }
            if (small < full && (r = TryNewPage(small, type, req.Size, align, cat, out a)) == Result.Success) return a;
        }
        // last resort: a page of exactly this image (what is left under a budget may be smaller than a small page)
        if (need < small && (r = TryNewPage(need, type, req.Size, align, cat, out a)) == Result.Success) return a;
        _vk.MarkHopeless(need);
        throw _vk.OutOfMemory(r, "vkAllocateMemory(image page)", req.Size, cat);
    }

    bool TryExisting(uint type, ulong size, ulong align, MemCat cat, out Allocation a)
    {
        foreach (var p in _pages)
        {
            if (p.Type != type || p.Size - p.Used < size) continue;
            if (TryTake(p, size, align, out var off)) { a = Take(p, off, size, cat); return true; }
        }
        a = default;
        return false;
    }

    Result TryNewPage(ulong pageSize, uint type, ulong size, ulong align, MemCat cat, out Allocation a)
    {
        a = default;
        var r = _vk.TryAllocateMemory(pageSize, type, MemCat.Other, true, out var mem);
        if (r != Result.Success) return r;
        var page = new Page { Memory = mem, Size = pageSize, Type = type };
        page.Free.Add(0, page.Size);
        _pages.Add(page);
        TryTake(page, size, align, out var off);
        a = Take(page, off, size, cat);
        return r;
    }

    Allocation Take(Page p, ulong offset, ulong size, MemCat cat)
    {
        p.Used += size;
        _vk.PoolAccount(cat, (long)size);
        return new Allocation(p.Memory, offset, size, p, cat);
    }

    static bool TryTake(Page p, ulong size, ulong align, out ulong offset)
    {
        for (int i = 0; i < p.Free.Count; i++)
        {
            ulong start = p.Free.Keys[i], len = p.Free.Values[i];
            ulong aligned = (start + align - 1) & ~(align - 1);
            ulong pad = aligned - start;
            if (len < pad + size) continue;
            p.Free.RemoveAt(i);
            if (pad > 0) p.Free.Add(start, pad);
            ulong rest = len - pad - size;
            if (rest > 0) p.Free.Add(aligned + size, rest);
            offset = aligned;
            return true;
        }
        offset = 0;
        return false;
    }

    public void Free(Allocation a)
    {
        if (a.Page is not Page p) return;
        p.Used -= a.Size;
        _vk.PoolAccount(a.Category, -(long)a.Size);
        p.Free.Add(a.Offset, a.Size);
        // coalesce neighbours
        for (int i = 0; i < p.Free.Count - 1;)
        {
            ulong s = p.Free.Keys[i], l = p.Free.Values[i];
            if (s + l == p.Free.Keys[i + 1])
            {
                ulong l2 = p.Free.Values[i + 1];
                p.Free.RemoveAt(i + 1);
                p.Free[s] = l + l2;
            }
            else i++;
        }
    }

    /// <summary>Returns empty pages to the driver, keeping up to <paramref name="keepSpare"/> of them for reuse
    /// (the largest). Returns the bytes freed.</summary>
    public ulong TrimEmptyPages(int keepSpare)
    {
        ulong freed = 0;
        int spare = 0;
        // keep the largest empty pages as spares
        _pages.Sort((x, y) => y.Size.CompareTo(x.Size));
        for (int i = 0; i < _pages.Count;)
        {
            var p = _pages[i];
            if (p.Used != 0 || spare++ < keepSpare) { i++; continue; }
            _vk.FreeMemory(p.Memory);
            freed += p.Size;
            _pages.RemoveAt(i);
            _pagesFreed++;
        }
        return freed;
    }

    public void Dispose()
    {
        foreach (var p in _pages) _vk.FreeMemory(p.Memory);
        _pages.Clear();
    }
}
