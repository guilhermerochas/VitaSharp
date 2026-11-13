using Silk.NET.Shaderc;
using System.Runtime.InteropServices;

namespace VitaSharp.Tests;

/// <summary>
/// shaderc (the GLSL to SPIR-V compiler the renderer feeds every translated shader to): Silk.NET.Shaderc.Native ships
/// it for Windows, Linux and macOS, and on Linux the distribution's package is the fallback (Debian/Ubuntu:
/// libshaderc-dev). Tests that compile shaders are skipped with the reason when the library cannot be loaded.
/// </summary>
/// <remarks>
/// The probe never unloads the library (no Dispose, no NativeLibrary.Free), as the renderer does not
/// (<c>ShaderCompiler.Dispose</c>): shaderc registers a process-exit handler that stays behind after dlclose, so on
/// Linux every test host that ran the probe jumped into the unmapped library at exit (SIGSEGV after a passing run,
/// and a crash dump each time under WSL's crash collector).
/// </remarks>
public static class ShadercAvailability
{
    private static readonly Lazy<string?> s_reason = new(Probe);

    /// <summary>The API the probe loaded, kept for the whole process.</summary>
    private static Shaderc? s_api;

    public static string? SkipReason => s_reason.Value;

    /// <summary>Loads shaderc (once per process: the library stays loaded) and returns null, or why it cannot be loaded.</summary>
    internal static string? Probe()
    {
        try
        {
            s_api ??= Shaderc.GetApi();
            return null;
        }
        catch (Exception e) when (e is FileNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            return $"shaderc not available ({e.Message.TrimEnd('.', '!')}; bundled copy: {BundledLoadError()}): " + InstallHint();
        }
    }

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
            // loaded and kept: unloading it leaves its exit handler behind (see the remarks above)
            NativeLibrary.Load(path);
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
