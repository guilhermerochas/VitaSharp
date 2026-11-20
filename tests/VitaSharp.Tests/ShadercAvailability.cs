using Silk.NET.Shaderc;
using System.Runtime.InteropServices;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// shaderc (the GLSL to SPIR-V compiler the renderer feeds every translated shader to): Silk.NET.Shaderc.Native ships
/// it for Windows, Linux and macOS, and on Linux the distribution's package is the fallback (Debian/Ubuntu:
/// libshaderc-dev). Tests that compile shaders are skipped with the reason when the library cannot be loaded.
/// </summary>
public static class ShadercAvailability
{
    /// <summary>The API the probe loaded, never disposed: disposing a Silk.NET API unloads its library (dlclose), and
    /// shaderc registers a process-exit handler, so the test host's exit() jumped into whatever was mapped at the old
    /// address later (the SIGSEGV at the end of every Linux test run; the renderer keeps its own copy loaded too,
    /// a9b03f1b).</summary>
    private static Shaderc? s_probe;

    private static readonly Lazy<string?> s_reason = new(() =>
    {
        try
        {
            s_probe = Shaderc.GetApi();
            return null;
        }
        catch (Exception e) when (e is FileNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            return $"shaderc not available ({e.Message.TrimEnd('.', '!')}; bundled copy: {BundledLoadError()}): " + InstallHint();
        }
    });

    public static string? SkipReason => s_reason.Value;

    /// <summary>Whether the probe's shaderc API is still held (it is kept for the whole process).</summary>
    internal static bool ProbeKeptLoaded => s_probe != null;

    /// <summary>Why the copy from Silk.NET.Shaderc.Native did not load (the loader's own message, e.g. dlerror's).</summary>
    private static string BundledLoadError()
    {
        string name = OperatingSystem.IsWindows() ? "shaderc_shared.dll" : OperatingSystem.IsMacOS() ? "libshaderc_shared.dylib" : "libshaderc_shared.so";
        string path = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", name);
        if (!File.Exists(path))
        {
            return $"{path} is missing";
        }

        try
        {
            NativeProbe.Load(path); // kept loaded: shaderc registers an exit handler (NativeProbe)
            return "loads";
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            return e.Message;
        }
    }

    private static string InstallHint()
    {
        if (OperatingSystem.IsLinux())
        {
            return "install shaderc (Debian/Ubuntu: sudo apt install libshaderc-dev; Fedora: shaderc; Arch: shaderc)";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "install shaderc (brew install shaderc) or restore the Silk.NET.Shaderc.Native package";
        }

        return "restore the Silk.NET.Shaderc.Native package (shaderc_shared.dll next to the tests)";
    }
}

/// <summary>[Fact] that is skipped when shaderc cannot be loaded.</summary>
public sealed class ShadercFactAttribute : FactAttribute
{
    public ShadercFactAttribute()
    {
        if (ShadercAvailability.SkipReason is { } r)
        {
            Skip = r;
        }
    }
}

/// <summary>[Theory] that is skipped when shaderc cannot be loaded.</summary>
public sealed class ShadercTheoryAttribute : TheoryAttribute
{
    public ShadercTheoryAttribute()
    {
        if (ShadercAvailability.SkipReason is { } r)
        {
            Skip = r;
        }
    }
}
