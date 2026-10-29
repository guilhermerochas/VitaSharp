using VitaSharp.Core.Util;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VitaSharp.Desktop;

/// <summary>
/// macOS: GLFW (the Silk.NET window backend) loads the Vulkan loader itself with dlopen("libvulkan.1.dylib"), which
/// only searches DYLD_LIBRARY_PATH / DYLD_FALLBACK_LIBRARY_PATH (default ~/lib:/usr/local/lib:/usr/lib). The LunarG
/// SDK's system install lands in /usr/local/lib and works as is, but Homebrew on Apple Silicon (/opt/homebrew/lib) or
/// an SDK used in place ($VULKAN_SDK/lib) are invisible to it, and the window would have no Vulkan surface.
/// dyld reads those variables only at process start, so when the loader is in such a folder VitaSharp restarts itself
/// once with the folder added to DYLD_FALLBACK_LIBRARY_PATH.
/// </summary>
static class MacVulkanLoader
{
    const string Marker = EnvVars.DyldRelaunched;

    /// <summary>Returns the child's exit code after a relaunch, or null to continue in this process.</summary>
    public static int? RelaunchIfNeeded(string[] args)
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable(Marker) == "1") return null;
        if (NativeLibrary.TryLoad("libvulkan.1.dylib", out var h)) { NativeLibrary.Free(h); return null; }

        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var dir = new[] { string.IsNullOrEmpty(sdk) ? "" : Path.Combine(sdk, "lib"), "/opt/homebrew/lib", AppContext.BaseDirectory }
            .FirstOrDefault(d => d.Length > 0 && File.Exists(Path.Combine(d, "libvulkan.1.dylib")));
        if (dir == null) return null; // nothing to add: the renderer reports the missing loader with install hints

        var exe = Environment.ProcessPath;
        if (exe == null) return null;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        // framework-dependent "dotnet VitaSharp.Desktop.dll ..."
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]); // the .dll path
        if (Program.DataFromCommandLine) psi.ArgumentList.Add("--data=" + Program.DataRoot); // Main consumed it
        foreach (var a in args) psi.ArgumentList.Add(a);
        var current = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH");
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        psi.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = dir + ":" + (string.IsNullOrEmpty(current) ? $"{home}/lib:/usr/local/lib:/usr/lib" : current);
        psi.Environment[Marker] = "1";
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not restart with the Vulkan loader from {dir} on the library path: {e.Message}");
            return null;
        }
    }
}
