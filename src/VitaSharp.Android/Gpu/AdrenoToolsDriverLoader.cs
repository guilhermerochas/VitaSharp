using System.Runtime.InteropServices;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Android.Gpu;

/// <summary>
/// SKELETON (not run on a device yet: there is no Android front-end and no Adreno hardware was available). Loads a
/// user-supplied Adreno/Turnip Vulkan driver through libadrenotools (github.com/bylaws/libadrenotools, the library
/// yuzu, Skyline, Strato and Vita3K use) instead of the system driver, and applies its BCeNabler patch and turbo
/// mode. The Android front-end installs it before creating the renderer:
/// <code>
/// var settings = GpuDriverSettings.Load(Path.Combine(dataRoot, "gpu_driver.json"));
/// var store = new CustomDriverStore(Path.Combine(dataRoot, "gpu_drivers"));
/// VulkanLoader.DriverProvider = AdrenoToolsDriverLoader.Create(settings, store, context.ApplicationInfo.NativeLibraryDir,
///     context.CacheDir.AbsolutePath, Path.Combine(dataRoot, "gpu_driver_files"));
/// </code>
/// With no driver selected the system driver is still opened through libadrenotools (BCeNabler and turbo work on
/// the stock driver too). Packaging: libadrenotools.so plus its hook libraries (libhook_impl.so, libmain_hook.so,
/// libfile_redirect_hook.so, libgsl_alloc_hook.so) go into the APK's lib/arm64-v8a, built from the libadrenotools
/// sources with the NDK (CMake); the hook directory passed to adrenotools_open_libvulkan is the app's native
/// library directory.
///
/// Remaining work (needs a device):
/// <list type="bullet">
/// <item>Build libadrenotools for arm64-v8a and add it to the APK (AndroidNativeLibrary items).</item>
/// <item>Verify that Silk.NET resolves every instance/device command through the returned handle's
/// vkGetInstanceProcAddr (VulkanLoader wraps the handle in a LamdaNativeContext).</item>
/// <item>UI: the "Driver da GPU" screen itself (its model, <see cref="GpuDriverScreen"/>, lists, selects, installs a
/// .zip from a Storage Access Framework stream and removes; mockup docs/android/mockups/driver-gpu.html).</item>
/// <item>Restart the emulation when the driver changes (a process can load only one driver safely).</item>
/// <item>Check the quirk table against the custom driver (Turnip: no proprietary quirks; see docs/notes/mobile-gpu.md).</item>
/// </list>
/// </summary>
public sealed class AdrenoToolsDriverLoader : IVulkanDriverProvider
{
    readonly GpuDriverSettings _settings;
    readonly InstalledGpuDriver? _driver;
    readonly string _hookLibDir, _tmpLibDir, _redirectDir;

    AdrenoToolsDriverLoader(GpuDriverSettings settings, InstalledGpuDriver? driver, string hookLibDir, string tmpLibDir, string redirectDir)
    {
        _settings = settings;
        _driver = driver;
        _hookLibDir = WithSlash(hookLibDir);
        _tmpLibDir = WithSlash(tmpLibDir);
        _redirectDir = WithSlash(redirectDir);
    }

    /// <summary>A provider for the selected driver (null when libadrenotools is not packaged: use the system loader).</summary>
    public static AdrenoToolsDriverLoader? Create(GpuDriverSettings settings, CustomDriverStore store, string nativeLibDir, string cacheDir, string redirectDir)
    {
        if (!NativeLibrary.TryLoad("libadrenotools.so", out var h))
        {
            Log.Info("libadrenotools not packaged: system Vulkan driver");
            return null;
        }
        NativeLibrary.Free(h);
        var driver = store.Find(settings.Driver);
        if (settings.Driver.Length > 0 && driver == null) Log.Warn($"GPU driver '{settings.Driver}' is not installed: system driver");
        return new AdrenoToolsDriverLoader(settings, driver, nativeLibDir, cacheDir, redirectDir);
    }

    static string WithSlash(string d) => d.EndsWith('/') ? d : d + "/";

    public string Description => _driver is { } d ? $"{d.Meta.Name} {d.Meta.DriverVersion} ({d.Meta.Vendor}, {d.Meta.LibraryName})" : "system driver via libadrenotools";

    public nint Open()
    {
        int flags = 0;
        if (_driver != null) flags |= AdrenoTools.DriverCustom;
        if (_settings.FileRedirect)
        {
            Directory.CreateDirectory(_redirectDir);
            flags |= AdrenoTools.DriverFileRedirect;
        }
        // RTLD_NOW; libadrenotools loads the driver into its own linker namespace with the hooks applied
        nint handle = AdrenoTools.adrenotools_open_libvulkan(2, flags, _tmpLibDir, _hookLibDir,
            _driver?.Directory, _driver?.Meta.LibraryName, _settings.FileRedirect ? _redirectDir : null, 0);
        if (handle == 0)
        {
            Log.Warn($"adrenotools_open_libvulkan failed for {Description}");
            return 0;
        }
        if (_settings.Turbo)
        {
            try { AdrenoTools.adrenotools_set_turbo(true); }
            catch (EntryPointNotFoundException) { }
        }
        return handle;
    }

    public bool TryEnableBcn(uint vendorId, uint driverVersion, nint getPhysicalDeviceFormatProperties)
    {
        if (!_settings.PatchBcn || vendorId != GpuQuirkTable.VendorQualcomm) return false;
        // VK_VERSION_MAJOR/MINOR of the driver version, as Vita3K passes them
        var type = AdrenoTools.adrenotools_get_bcn_type(driverVersion >> 22, (driverVersion >> 12) & 0x3FF, vendorId);
        if (type == AdrenoTools.BcnBlob) return true;   // the blob decodes BCn already (the feature bit lies)
        if (type != AdrenoTools.BcnPatch) return false;
        return AdrenoTools.adrenotools_patch_bcn(getPhysicalDeviceFormatProperties);
    }
}

/// <summary>libadrenotools C API (include/adrenotools/driver.h, bcenabler.h).</summary>
static partial class AdrenoTools
{
    const string Lib = "libadrenotools.so";
    public const int DriverCustom = 1 << 0, DriverFileRedirect = 1 << 1, DriverGpuMappingImport = 1 << 2;
    public const int BcnIncompatible = 0, BcnBlob = 1, BcnPatch = 2;

    /// <summary>void *adrenotools_open_libvulkan(int dlopenMode, int featureFlags, const char *tmpLibDir,
    /// const char *hookLibDir, const char *customDriverDir, const char *customDriverName,
    /// const char *fileRedirectDir, void **userMappingHandle)</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint adrenotools_open_libvulkan(int dlopenMode, int featureFlags, string? tmpLibDir, string hookLibDir,
        string? customDriverDir, string? customDriverName, string? fileRedirectDir, nint userMappingHandle);

    [LibraryImport(Lib)]
    public static partial int adrenotools_get_bcn_type(uint major, uint minor, uint vendorId);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool adrenotools_patch_bcn(nint vkGetPhysicalDeviceFormatPropertiesFn);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool adrenotools_set_turbo([MarshalAs(UnmanagedType.U1)] bool turbo);
}
