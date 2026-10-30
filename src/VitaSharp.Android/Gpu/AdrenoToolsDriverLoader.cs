using System.Runtime.InteropServices;
using VitaSharp.Android.Storage;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Android.Gpu;

/// <summary>
/// Built into the app, not run on a device yet (no Adreno hardware was available). Loads a user-supplied Adreno/Turnip
/// Vulkan driver package (the Skyline/yuzu/Vita3K zip with meta.json, installed by "Driver da GPU" into
/// &lt;internal&gt;/gpu_drivers) through libadrenotools (github.com/bylaws/libadrenotools) instead of the system driver,
/// and applies its options: BCeNabler (BCn textures on Qualcomm drivers that hide them), file redirection (the driver's
/// file accesses go to &lt;internal&gt;/gpu_driver_files) and turbo clocks. EmulationSession installs it before the
/// renderer is created:
/// <code>
/// VulkanLoader.DriverProvider = AdrenoToolsDriverLoader.Create(settings, store, data, nativeLibraryDir, cacheDir);
/// </code>
/// With no driver selected the system driver is still opened through libadrenotools when one of the options needs it
/// (BCeNabler and turbo work on the stock driver too), else the system loader is used directly.
/// <para>
/// Packaging (src/VitaSharp.Android.csproj, when build/build-android-adrenotools.ps1 or .sh ran): libadrenotools.so
/// and the hook libraries (libhook_impl.so, libmain_hook.so, libfile_redirect_hook.so, libgsl_alloc_hook.so) in
/// lib/arm64-v8a, extracted at install (android:extractNativeLibs="true", libadrenotools' "useLegacyPackaging"
/// requirement): the hooks are loaded by path from the app's native library directory.
/// </para>
/// </summary>
public sealed class AdrenoToolsDriverLoader : IVulkanDriverProvider
{
    /// <summary>The hook libraries libadrenotools loads from the native library directory.</summary>
    public static readonly string[] HookLibraries = ["libhook_impl.so", "libmain_hook.so", "libfile_redirect_hook.so", "libgsl_alloc_hook.so"];

    readonly GpuDriverSettings _settings;
    readonly InstalledGpuDriver? _driver;
    readonly string _hookLibDir, _tmpLibDir, _redirectDir;
    bool _opened;

    AdrenoToolsDriverLoader(GpuDriverSettings settings, InstalledGpuDriver? driver, string hookLibDir, string tmpLibDir, string redirectDir)
    {
        _settings = settings;
        _driver = driver;
        _hookLibDir = WithSlash(hookLibDir);
        _tmpLibDir = WithSlash(tmpLibDir);
        _redirectDir = WithSlash(redirectDir);
    }

    /// <summary>A provider for the selected driver and options; null to use the system loader (no driver and no option
    /// selected, libadrenotools or its hooks not packaged, or the driver package not usable).</summary>
    public static AdrenoToolsDriverLoader? Create(GpuDriverSettings settings, CustomDriverStore store, DataLayout data, string nativeLibDir, string cacheDir)
    {
        var driver = store.Find(settings.Driver);
        if (settings.Driver.Length > 0 && driver == null) Log.Warn($"GPU driver '{settings.Driver}' is not installed: system driver");
        if (driver == null && !settings.Turbo && !settings.PatchBcn && !settings.FileRedirect) return null;
        // everything libadrenotools does is for Qualcomm's kernel driver (kgsl): elsewhere use the system loader
        if (!IsAdreno)
        {
            if (driver != null || settings.FileRedirect || settings.Turbo) Log.Warn("GPU driver options ignored: this is not an Adreno GPU (no /dev/kgsl-3d0)");
            return null;
        }
        if (!NativeLibrary.TryLoad("libadrenotools.so", out var h))
        {
            Log.Info(driver != null ? $"libadrenotools not packaged: '{driver.Meta.Name}' cannot be loaded, system Vulkan driver"
                                    : "libadrenotools not packaged: system Vulkan driver");
            return null;
        }
        NativeLibrary.Free(h);
        // the hooks must be real files in the native library directory (extractNativeLibs), else libadrenotools
        // returns a handle whose hook fails and the device list comes back empty
        var missing = HookLibraries.Where(l => !File.Exists(Path.Combine(nativeLibDir, l))).ToArray();
        if (missing.Length > 0)
        {
            Log.Warn($"libadrenotools hooks missing in {nativeLibDir} ({string.Join(", ", missing)}; native libraries not extracted?): system Vulkan driver");
            return null;
        }
        if (driver != null)
        {
            // the linker refuses libraries on shared storage: the store must be in the internal files folder
            if (!Path.GetFullPath(driver.Directory).StartsWith(data.InternalRoot, StringComparison.Ordinal))
                Log.Warn($"GPU driver folder {driver.Directory} is not in internal storage ({data.InternalRoot}): the driver may fail to load");
            if (driver.Meta.MinApi > (int)global::Android.OS.Build.VERSION.SdkInt)
            {
                Log.Warn($"GPU driver '{driver.Meta.Name}' needs Android API {driver.Meta.MinApi}: system driver");
                driver = null;
            }
        }
        string tmp = Path.Combine(cacheDir, "adrenotools");
        Directory.CreateDirectory(tmp);
        return new AdrenoToolsDriverLoader(settings, driver, nativeLibDir, tmp, data.GpuDriverFiles);
    }

    static string WithSlash(string d) => d.EndsWith('/') ? d : d + "/";

    /// <summary>An Adreno GPU: Qualcomm's kernel graphics driver node exists.</summary>
    public static bool IsAdreno => File.Exists("/dev/kgsl-3d0");

    public string Description => (_driver is { } d ? $"{d.Meta.Name} {d.Meta.DriverVersion} ({d.Meta.Vendor}, {d.Meta.LibraryName})" : "system driver") +
        $" via libadrenotools{(_settings.FileRedirect ? ", file redirect" : "")}{(_settings.PatchBcn ? ", BCeNabler" : "")}{(_settings.Turbo ? ", turbo" : "")}";

    /// <summary>The feature flags passed to adrenotools_open_libvulkan.</summary>
    internal int Flags => (_driver != null ? AdrenoTools.DriverCustom : 0) | (_settings.FileRedirect ? AdrenoTools.DriverFileRedirect : 0);

    public nint Open()
    {
        int flags = Flags;
        if (_settings.FileRedirect) Directory.CreateDirectory(_redirectDir);
        // RTLD_NOW; libadrenotools loads the driver into its own linker namespace with the hooks applied. The temporary
        // directory is only used below API 29 (no memfd)
        nint handle = AdrenoTools.adrenotools_open_libvulkan(2, flags, _tmpLibDir, _hookLibDir,
            _driver?.Directory, _driver?.Meta.LibraryName, _settings.FileRedirect ? _redirectDir : null, 0);
        if (handle == 0)
        {
            Log.Warn($"adrenotools_open_libvulkan failed for {Description} (flags {flags}): system driver");
            return 0;
        }
        _opened = true;
        Log.Info($"Vulkan driver: {Description}");
        if (_settings.Turbo)
        {
            try { AdrenoTools.adrenotools_set_turbo(true); }
            catch (EntryPointNotFoundException) { }
        }
        return handle;
    }

    public bool TryEnableBcn(uint vendorId, uint driverVersion, nint getPhysicalDeviceFormatProperties)
    {
        if (!_opened || !_settings.PatchBcn || vendorId != GpuQuirkTable.VendorQualcomm) return false;
        // VK_VERSION_MAJOR/MINOR of the driver version, as Vita3K passes them
        var type = AdrenoTools.adrenotools_get_bcn_type(driverVersion >> 22, (driverVersion >> 12) & 0x3FF, vendorId);
        if (type == AdrenoTools.BcnBlob) { Log.Info("BCeNabler: the driver decodes BCn already"); return true; }
        if (type != AdrenoTools.BcnPatch) { Log.Info("BCeNabler: this driver version cannot be patched"); return false; }
        bool ok = AdrenoTools.adrenotools_patch_bcn(getPhysicalDeviceFormatProperties);
        Log.Info($"BCeNabler: {(ok ? "BCn textures enabled" : "patch failed")}");
        return ok;
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

    /// <summary>void adrenotools_set_turbo(bool turbo)</summary>
    [LibraryImport(Lib)]
    public static partial void adrenotools_set_turbo([MarshalAs(UnmanagedType.U1)] bool turbo);
}
