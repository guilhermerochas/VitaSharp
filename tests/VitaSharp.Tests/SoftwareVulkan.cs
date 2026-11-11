using System.Runtime.InteropServices;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// A software Vulkan device for renderer tests, so they run on machines and CI runners without a GPU: on Windows
/// Google's SwiftShader as Microsoft Edge and Google Chrome ship it (vk_swiftshader.dll next to the browser, loaded
/// directly as the driver, no system loader needed); on Linux Mesa's lavapipe (the distribution's mesa-vulkan-drivers
/// package, picked through the system loader). Tests are skipped when neither is installed.
/// </summary>
public static class SoftwareVulkan
{
    /// <summary>vk_swiftshader.dll (Windows) or lavapipe's ICD manifest (Linux); null when absent.</summary>
    public static string? Driver { get; } = Find();

    public static string? SkipReason => Driver == null ? "no software Vulkan driver (SwiftShader from Edge/Chrome, or Mesa lavapipe)" : null;

    static string? Find()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Microsoft\Edge\Application",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Microsoft\Edge\Application",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Google\Chrome\Application",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Google\Chrome\Application",
            })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var dll = Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "vk_swiftshader.dll")).Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (dll != null)
                {
                    return dll;
                }
            }
            return null;
        }
        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in new[] { "/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d" })
            {
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "lvp_icd*.json").FirstOrDefault() is { } json)
                {
                    return json;
                }
            }
        }

        return null;
    }

    sealed class SwiftShaderProvider(string dll) : IVulkanDriverProvider
    {
        public string Description => "SwiftShader (tests)";
        public nint Open() => NativeLibrary.TryLoad(dll, out var h) ? h : 0;
    }

    /// <summary>Makes the next renderer use the software driver; dispose to restore the previous setup.</summary>
    public static IDisposable Use()
    {
        var saved = VulkanLoader.DriverProvider;
        string? savedFiles = Environment.GetEnvironmentVariable("VK_DRIVER_FILES"), savedIcd = Environment.GetEnvironmentVariable("VK_ICD_FILENAMES");
        if (OperatingSystem.IsWindows())
        {
            VulkanLoader.DriverProvider = new SwiftShaderProvider(Driver!);
        }
        else
        {
            Environment.SetEnvironmentVariable("VK_DRIVER_FILES", Driver);
            Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", Driver);
        }
        return new Restore(() =>
        {
            VulkanLoader.DriverProvider = saved;
            Environment.SetEnvironmentVariable("VK_DRIVER_FILES", savedFiles);
            Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", savedIcd);
        });
    }

    sealed class Restore(Action a) : IDisposable
    {
        public void Dispose() => a();
    }
}

/// <summary>[Fact] skipped when no software Vulkan driver is installed.</summary>
public sealed class SoftwareVulkanFactAttribute : FactAttribute
{
    public SoftwareVulkanFactAttribute()
    {
        if (SoftwareVulkan.SkipReason is { } r)
        {
            Skip = r;
        }
    }
}
