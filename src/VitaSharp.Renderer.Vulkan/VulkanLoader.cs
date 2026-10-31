using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Finds the Vulkan loader: vulkan-1.dll (Windows), libvulkan.so.1 (Linux, Android), libvulkan.1.dylib from the
/// LunarG SDK or Homebrew's vulkan-loader on macOS, or MoltenVK itself (libMoltenVK.dylib, which exports the Vulkan
/// entry points directly). VITASHARP_VULKAN_LIB=&lt;path&gt; forces a specific library.
/// Silk.NET's default search covers the system library path only; on macOS Homebrew's /opt/homebrew/lib and the
/// SDK's $VULKAN_SDK/lib are not on it, so they are tried explicitly.
/// </summary>
public static class VulkanLoader
{
    /// <summary>Library that the last <see cref="Load"/> used ("" = Silk.NET's default search).</summary>
    public static string LoadedFrom { get; private set; } = "";

    /// <summary>Custom driver (Android: libadrenotools), tried before everything else; null = system loader.</summary>
    public static IVulkanDriverProvider? DriverProvider;

    /// <summary>True when <see cref="DriverProvider"/> supplied the driver of the last <see cref="Load"/>.</summary>
    public static bool CustomDriverLoaded { get; private set; }

    public static Vk Load()
    {
        CustomDriverLoaded = false;
        if (DriverProvider is { } provider)
        {
            nint handle = 0;
            try { handle = provider.Open(); }
            catch (Exception e) { Log.Warn($"Custom GPU driver {provider.Description} failed to open: {e.Message}"); }
            if (handle != 0)
            {
                LoadedFrom = provider.Description;
                CustomDriverLoaded = true;
                Log.Info($"Vulkan driver: {provider.Description} (custom)");
                return new Vk(new LamdaNativeContext(name => NativeLibrary.TryGetExport(handle, name, out var p) ? p : 0));
            }
            Log.Warn($"Custom GPU driver {provider.Description} unavailable: using the system Vulkan driver");
        }
        var forced = Environment.GetEnvironmentVariable(EnvVars.VulkanLib);
        if (!string.IsNullOrEmpty(forced))
        {
            LoadedFrom = forced;
            return new Vk(new DefaultNativeContext(forced));
        }
        Exception? first = null;
        try
        {
            LoadedFrom = "";
            return Vk.GetApi();
        }
        catch (Exception e) { first = e; }
        foreach (var path in Candidates())
        {
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var h)) continue;
            NativeLibrary.Free(h);
            try
            {
                var vk = new Vk(new DefaultNativeContext(path));
                LoadedFrom = path;
                Log.Info($"Vulkan loader: {path}");
                return vk;
            }
            catch (Exception) { }
        }
        throw new DllNotFoundException("No Vulkan loader found. " + InstallHint() + $" ({first?.Message})", first);
    }

    /// <summary>Full paths tried after the default search, in order.</summary>
    public static IEnumerable<string> Candidates()
    {
        string exeDir = AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS())
        {
            var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
            foreach (var dir in new[]
                     {
                         exeDir, Path.Combine(exeDir, "..", "Frameworks"), // bundled next to the executable / in an .app
                         string.IsNullOrEmpty(sdk) ? "" : Path.Combine(sdk, "lib"),
                         "/opt/homebrew/lib", "/usr/local/lib",
                     })
            {
                if (dir.Length == 0) continue;
                yield return Path.Combine(dir, "libvulkan.1.dylib");
                yield return Path.Combine(dir, "libvulkan.dylib");
            }
            // MoltenVK without the loader: no layers, but enough to run
            foreach (var dir in new[] { exeDir, Path.Combine(exeDir, "..", "Frameworks"), string.IsNullOrEmpty(sdk) ? "" : Path.Combine(sdk, "lib"), "/opt/homebrew/lib", "/usr/local/lib" })
                if (dir.Length > 0) yield return Path.Combine(dir, "libMoltenVK.dylib");
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            yield return Path.Combine(exeDir, "libvulkan.so.1");
            foreach (var dir in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/local/lib" })
            {
                yield return Path.Combine(dir, "libvulkan.so.1");
                yield return Path.Combine(dir, "libvulkan.so");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(exeDir, "vulkan-1.dll");
        }
    }

    public static string InstallHint() =>
        OperatingSystem.IsAndroid() ? "The GPU driver comes with the Android system: update the system, or on an Adreno choose a custom driver in \"Driver da GPU\"."
        : OperatingSystem.IsMacOS() ? "Install the LunarG Vulkan SDK (it includes MoltenVK) or `brew install molten-vk vulkan-loader`."
        : OperatingSystem.IsLinux() ? "Install the Vulkan loader and a driver, e.g. `sudo apt install libvulkan1 mesa-vulkan-drivers` (lavapipe = software fallback)."
        : "Install or update the GPU driver (it provides vulkan-1.dll).";
}
