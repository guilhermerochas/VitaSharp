using System.Runtime.InteropServices;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// A software (or portable) Vulkan device for renderer tests, so they run on machines and CI runners without a GPU:
/// <list type="bullet">
/// <item>any OS: the driver manifest in VK_DRIVER_FILES / VK_ICD_FILENAMES when one is set (a downloaded SwiftShader
/// or Mesa build), through the system Vulkan loader;</item>
/// <item>Windows: Google's SwiftShader as Microsoft Edge and Google Chrome ship it (vk_swiftshader.dll next to the
/// browser, loaded directly as the driver, no system loader needed);</item>
/// <item>Linux: Mesa's lavapipe (the distribution's mesa-vulkan-drivers package) through the system loader
/// (libvulkan1);</item>
/// <item>macOS: MoltenVK (the LunarG Vulkan SDK, or Homebrew's molten-vk and vulkan-loader) through the loader.</item>
/// </list>
/// Tests are skipped when none is installed; <see cref="SkipReason"/> says what is missing on this OS.
/// </summary>
public static class SoftwareVulkan
{
    private static readonly (string? Driver, string? Loader, string? Missing) s_found = Find();

    /// <summary>vk_swiftshader.dll (Windows, loaded directly) or the driver's ICD manifest (through the loader); null when absent.</summary>
    public static string? Driver => s_found.Driver;

    public static string? SkipReason => s_found.Missing;

    /// <summary>The software device's driver is a manifest for the system loader (not a library loaded directly).</summary>
    private static bool ThroughLoader => Driver != null && Driver.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static (string?, string?, string?) Find()
    {
        // an explicit driver manifest wins (a SwiftShader or Mesa build downloaded for the tests)
        if ((Env("VK_DRIVER_FILES") ?? Env("VK_ICD_FILENAMES")) is { } given)
        {
            string first = given.Split(Path.PathSeparator)[0];
            if (File.Exists(first))
            {
                return (first, OperatingSystem.IsMacOS() ? MacLoader() : null, null);
            }

            return (null, null, $"VK_DRIVER_FILES / VK_ICD_FILENAMES names {first}, which does not exist");
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (string? root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Microsoft\Edge\Application",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Microsoft\Edge\Application",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Google\Chrome\Application",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Google\Chrome\Application",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Google\Chrome\Application",
            })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                string? dll = Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "vk_swiftshader.dll")).Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (dll != null)
                {
                    return (dll, null, null);
                }
            }
            return (null, null, "no software Vulkan driver: install Microsoft Edge or Google Chrome (they ship SwiftShader, vk_swiftshader.dll), "
                + "or set VK_DRIVER_FILES to the ICD manifest of a SwiftShader or Mesa build");
        }
        if (OperatingSystem.IsLinux())
        {
            foreach (string? dir in new[] { "/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d", "/usr/local/share/vulkan/icd.d" })
            {
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "lvp_icd*.json").FirstOrDefault() is { } json)
                {
                    return NativeLibrary.TryLoad("libvulkan.so.1", out nint h) ? Free(h, json) : (null, null, "Mesa lavapipe is installed but not the Vulkan loader: install libvulkan1");
                }
            }

            return (null, null, "no software Vulkan driver: install Mesa lavapipe (Debian/Ubuntu: sudo apt install mesa-vulkan-drivers libvulkan1; "
                + "Fedora: mesa-vulkan-drivers vulkan-loader), or set VK_DRIVER_FILES to a SwiftShader ICD manifest");
        }
        if (OperatingSystem.IsMacOS())
        {
            string home = Env("HOME") ?? "";
            var dirs = new List<string> { "/opt/homebrew/share/vulkan/icd.d", "/opt/homebrew/etc/vulkan/icd.d", "/usr/local/share/vulkan/icd.d", "/usr/local/etc/vulkan/icd.d" };
            if (Env("VULKAN_SDK") is { } sdk)
            {
                dirs.Insert(0, Path.Combine(sdk, "share", "vulkan", "icd.d"));
            }

            if (Directory.Exists(Path.Combine(home, "VulkanSDK")))
            {
                dirs.AddRange(Directory.EnumerateDirectories(Path.Combine(home, "VulkanSDK")).Select(d => Path.Combine(d, "macOS", "share", "vulkan", "icd.d")));
            }

            string? icd = dirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "MoltenVK_icd.json")).FirstOrDefault();
            if (icd == null)
            {
                return (null, null, "no Vulkan driver: install MoltenVK (the LunarG Vulkan SDK, or brew install molten-vk vulkan-loader), "
                    + "or set VK_DRIVER_FILES to a SwiftShader ICD manifest");
            }

            return MacLoader() is { } loader ? (icd, loader, null) : (null, null, "MoltenVK is installed but not the Vulkan loader (libvulkan.1.dylib): brew install vulkan-loader, or the LunarG SDK");
        }
        return (null, null, "no software Vulkan device for this OS");
    }

    private static (string?, string?, string?) Free(nint h, string json)
    {
        NativeLibrary.Free(h);
        return (json, null, null);
    }

    /// <summary>macOS: the Vulkan loader's path (Homebrew and the SDK are not on dyld's default search path).</summary>
    private static string? MacLoader()
    {
        string home = Env("HOME") ?? "";
        var candidates = new List<string> { "/opt/homebrew/lib/libvulkan.1.dylib", "/usr/local/lib/libvulkan.1.dylib" };
        if (Env("VULKAN_SDK") is { } sdk)
        {
            candidates.Insert(0, Path.Combine(sdk, "lib", "libvulkan.1.dylib"));
        }

        if (Directory.Exists(Path.Combine(home, "VulkanSDK")))
        {
            candidates.AddRange(Directory.EnumerateDirectories(Path.Combine(home, "VulkanSDK")).Select(d => Path.Combine(d, "macOS", "lib", "libvulkan.1.dylib")));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed class SwiftShaderProvider(string dll) : IVulkanDriverProvider
    {
        public string Description => "SwiftShader (tests)";
        public nint Open() => NativeLibrary.TryLoad(dll, out nint h) ? h : 0;
    }

    /// <summary>Makes the next renderer use the software driver; dispose to restore the previous setup.</summary>
    public static IDisposable Use() => ThroughLoader ? Loader(Driver!) : Direct();

    private static IDisposable Direct()
    {
        IVulkanDriverProvider? saved = VulkanLoader.DriverProvider;
        VulkanLoader.DriverProvider = new SwiftShaderProvider(Driver!);
        return new Restore(() => VulkanLoader.DriverProvider = saved);
    }

    private static IDisposable Loader(string manifest)
    {
        IVulkanDriverProvider? saved = VulkanLoader.DriverProvider;
        string? savedFiles = Env("VK_DRIVER_FILES"), savedIcd = Env("VK_ICD_FILENAMES"), savedLib = Env(EnvVars.VulkanLib);
        VulkanLoader.DriverProvider = null;
        Environment.SetEnvironmentVariable("VK_DRIVER_FILES", manifest);
        Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", manifest);
        if (s_found.Loader is { } lib)
        {
            Environment.SetEnvironmentVariable(EnvVars.VulkanLib, lib);
        }

        return new Restore(() =>
        {
            VulkanLoader.DriverProvider = saved;
            Environment.SetEnvironmentVariable("VK_DRIVER_FILES", savedFiles);
            Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", savedIcd);
            Environment.SetEnvironmentVariable(EnvVars.VulkanLib, savedLib);
        });
    }

    /// <summary>The software driver as an ICD manifest for the system loader (layers such as validation apply then);
    /// null when there is no loader or manifest.</summary>
    public static string? IcdManifest => Driver == null ? null
        : ThroughLoader ? Driver
        : File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll")) && File.Exists(Path.ChangeExtension(Driver, null) + "_icd.json")
            ? Path.ChangeExtension(Driver, null) + "_icd.json" : null;

    /// <summary>Like <see cref="Use"/>, always through the system loader (<see cref="IcdManifest"/>).</summary>
    public static IDisposable UseThroughLoader() => Loader(IcdManifest!);

    /// <summary>The environment a child process (<see cref="DesktopProcess"/>) needs for the same device:
    /// <paramref name="systemLoader"/> forces the loader on Windows too (a window surface goes through it).</summary>
    public static IEnumerable<(string Name, string Value)> ChildEnvironment(bool systemLoader)
    {
        if (Driver == null)
        {
            yield break;
        }

        if (!ThroughLoader && !systemLoader)
        {
            yield return (EnvVars.VulkanLib, Driver);
            yield break;
        }
        string manifest = ThroughLoader ? Driver : IcdManifest ?? Path.ChangeExtension(Driver, null) + "_icd.json";
        yield return ("VK_DRIVER_FILES", manifest);
        yield return ("VK_ICD_FILENAMES", manifest);
        if (s_found.Loader is { } lib)
        {
            yield return (EnvVars.VulkanLib, lib);
        }
    }

    /// <summary>Why a child process cannot get the software driver through the system Vulkan loader (a window
    /// surface needs the loader), or null when it can. Windows: the loader ignores VK_DRIVER_FILES / VK_ICD_FILENAMES in
    /// an elevated process ("Loader is running with elevated permissions"; GitHub's Windows runners run elevated), so
    /// there the manifest must be registered under HKLM\SOFTWARE\Khronos\Vulkan\Drivers (the CI does that).</summary>
    public static string? SystemLoaderSkipReason
    {
        get
        {
            if (SkipReason is { } missing)
            {
                return missing;
            }

            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll")))
            {
                return "no system Vulkan loader (vulkan-1.dll, installed with any GPU driver or the LunarG Vulkan Runtime)";
            }

            if (IcdManifest is not { } manifest)
            {
                return $"the software driver {Driver} has no ICD manifest (*_icd.json) next to it for the system Vulkan loader";
            }

            if (IsElevated() && !RegisteredDriver(manifest))
            {
                return "this process runs elevated, and the Vulkan loader then ignores VK_DRIVER_FILES: register the software driver "
                    + $"for the loader (a DWORD value 0 named {manifest} under HKLM\\SOFTWARE\\Khronos\\Vulkan\\Drivers), or run unelevated";
            }

            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool RegisteredDriver(string manifest)
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\Drivers");
        return key != null && key.GetValueNames().Any(name => string.Equals(Path.GetFullPath(name), Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Restore(Action a) : IDisposable
    {
        public void Dispose() => a();
    }
}

/// <summary>[Fact] of a hidden-window run in a child process (<see cref="DesktopProcess"/> with <c>systemLoader</c>):
/// skipped when the child cannot get the software Vulkan driver through the system loader
/// (<see cref="SoftwareVulkan.SystemLoaderSkipReason"/>), shaderc does not load, no desktop window can be created
/// (<see cref="WindowAvailability"/>: Linux without a display), or on macOS, where the window and its MoltenVK surface
/// are not covered by the tests.</summary>
public sealed class HiddenWindowVulkanFactAttribute : FactAttribute
{
    public HiddenWindowVulkanFactAttribute()
    {
        string? display = null;
        if (OperatingSystem.IsLinux())
        {
            display = WindowAvailability.SkipReason;
        }
        else if (!OperatingSystem.IsWindows())
        {
            display = "the window run is tested on Windows and on Linux with a display, not on this OS";
        }

        if ((display ?? SoftwareVulkan.SystemLoaderSkipReason ?? ShadercAvailability.SkipReason) is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>[Fact] skipped when no software Vulkan driver is installed, or shaderc (which the renderer compiles every
/// shader with) cannot be loaded.</summary>
public sealed class SoftwareVulkanFactAttribute : FactAttribute
{
    public SoftwareVulkanFactAttribute()
    {
        if ((SoftwareVulkan.SkipReason ?? ShadercAvailability.SkipReason) is { } r)
        {
            Skip = r;
        }
    }
}
