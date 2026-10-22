using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
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
        if (r != Result.Success) throw new InvalidOperationException($"Vulkan {what} failed: {r}");
    }
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

    public VkCore(IPresentationSurface? surface, bool validation)
    {
        CreateInstance(surface, validation);
        if (surface != null)
        {
            Vk.TryGetInstanceExtension(Instance, out KhrSurface se);
            SurfaceExt = se;
            Surface = surface.CreateSurface(Instance);
        }
        PickDevice();
        CreateDevice(surface != null);
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

    void CreateDevice(bool present)
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

        // what the device supports (1.2/1.3 feature structs, plus the portability subset on MoltenVK)
        var has13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var has12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &has13 };
        var hasPortability = new PhysicalDevicePortabilitySubsetFeaturesKHR { SType = StructureType.PhysicalDevicePortabilitySubsetFeaturesKhr, PNext = &has12 };
        var supported2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = portability ? &hasPortability : &has12 };
        Vk.GetPhysicalDeviceFeatures2(Physical, &supported2);
        if (!has13.DynamicRendering || !has13.Synchronization2)
            throw new InvalidOperationException($"{DeviceName} lacks dynamicRendering/synchronization2 (Vulkan 1.3 features). {VulkanLoader.InstallHint()}");
        SamplerMirrorClampToEdge = has12.SamplerMirrorClampToEdge;
        Formats.MirrorClampToEdgeSupported = SamplerMirrorClampToEdge;

        var f13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features, DynamicRendering = true, Synchronization2 = true };
        var f12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &f13, SamplerMirrorClampToEdge = has12.SamplerMirrorClampToEdge };
        // VK_KHR_portability_subset must be enabled when present, and each of its features must be enabled to be used:
        // enable whatever the device has, then degrade where the renderer depends on a missing one
        var fPortability = hasPortability with { PNext = &f12 };
        var feats = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = supported2.Features.SamplerAnisotropy,
            FillModeNonSolid = supported2.Features.FillModeNonSolid,
            WideLines = supported2.Features.WideLines,
            TextureCompressionBC = supported2.Features.TextureCompressionBC,
            ShaderStorageImageWriteWithoutFormat = supported2.Features.ShaderStorageImageWriteWithoutFormat,
            IndependentBlend = supported2.Features.IndependentBlend,
            DepthBiasClamp = supported2.Features.DepthBiasClamp,
            FragmentStoresAndAtomics = supported2.Features.FragmentStoresAndAtomics,
            OcclusionQueryPrecise = supported2.Features.OcclusionQueryPrecise,
        };
        OcclusionQueryPrecise = supported2.Features.OcclusionQueryPrecise;
        TextureCompressionBC = supported2.Features.TextureCompressionBC;
        FillModeNonSolid = supported2.Features.FillModeNonSolid;
        WideLines = supported2.Features.WideLines;
        if (portability) ApplyPortabilityLimits(hasPortability, ref feats);

        var exts = new List<string>();
        if (present) exts.Add(KhrSwapchain.ExtensionName);
        if (portability) exts.Add(PortabilitySubset);
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
    }

    public bool TextureCompressionBC, FillModeNonSolid, WideLines, OcclusionQueryPrecise, SamplerMirrorClampToEdge;

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

    public (Buffer Buffer, DeviceMemory Memory, nint Mapped) CreateHostBuffer(ulong size, BufferUsageFlags usage)
    {
        var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Vk.CreateBuffer(Device, in bci, null, out var buf).Ok("vkCreateBuffer");
        Vk.GetBufferMemoryRequirements(Device, buf, out var req);
        uint type;
        try { type = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit); }
        catch { type = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit); }
        var mai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = type };
        Vk.AllocateMemory(Device, in mai, null, out var mem).Ok("vkAllocateMemory");
        Vk.BindBufferMemory(Device, buf, mem, 0);
        void* p;
        Vk.MapMemory(Device, mem, 0, size, 0, &p);
        return (buf, mem, (nint)p);
    }

    public (Buffer Buffer, DeviceMemory Memory) CreateDeviceBuffer(ulong size, BufferUsageFlags usage)
    {
        var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Vk.CreateBuffer(Device, in bci, null, out var buf).Ok("vkCreateBuffer");
        Vk.GetBufferMemoryRequirements(Device, buf, out var req);
        var mai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        Vk.AllocateMemory(Device, in mai, null, out var mem).Ok("vkAllocateMemory");
        Vk.BindBufferMemory(Device, buf, mem, 0);
        return (buf, mem);
    }

    public void DestroyBuffer(Buffer b, DeviceMemory m)
    {
        Vk.DestroyBuffer(Device, b, null);
        Vk.FreeMemory(Device, m, null);
    }

    public void Dispose()
    {
        Vk.DeviceWaitIdle(Device);
        Vk.DestroyDevice(Device, null);
        if (Surface.Handle != 0) SurfaceExt?.DestroySurface(Instance, Surface, null);
        if (_debugUtils != null) _debugUtils.DestroyDebugUtilsMessenger(Instance, _messenger, null);
        Vk.DestroyInstance(Instance, null);
    }
}

/// <summary>Sub-allocates device-local memory for images from large pages (first fit, coalescing free list).</summary>
public sealed unsafe class ImageMemoryPool : IDisposable
{
    const ulong PageSize = 128ul << 20;
    readonly VkCore _vk;

    sealed class Page
    {
        public DeviceMemory Memory;
        public ulong Size;
        public uint Type;
        public readonly SortedList<ulong, ulong> Free = new(); // offset -> size
    }

    readonly List<Page> _pages = new();

    public ImageMemoryPool(VkCore vk) => _vk = vk;

    public readonly record struct Allocation(DeviceMemory Memory, ulong Offset, ulong Size, object Page);

    public Allocation Allocate(MemoryRequirements req)
    {
        uint type = _vk.FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        ulong align = Math.Max(req.Alignment, 256);
        foreach (var p in _pages)
        {
            if (p.Type != type) continue;
            if (TryTake(p, req.Size, align, out var off)) return new Allocation(p.Memory, off, req.Size, p);
        }
        var page = new Page { Size = Math.Max(PageSize, (req.Size + align) & ~(align - 1)), Type = type };
        var mai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = page.Size, MemoryTypeIndex = type };
        _vk.Vk.AllocateMemory(_vk.Device, in mai, null, out page.Memory).Ok("vkAllocateMemory(image page)");
        page.Free.Add(0, page.Size);
        _pages.Add(page);
        TryTake(page, req.Size, align, out var o2);
        return new Allocation(page.Memory, o2, req.Size, page);
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
        var p = (Page)a.Page;
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

    public void Dispose()
    {
        foreach (var p in _pages) _vk.Vk.FreeMemory(_vk.Device, p.Memory, null);
        _pages.Clear();
    }
}
