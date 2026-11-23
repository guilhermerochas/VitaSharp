using Silk.NET.Core.Contexts;
using Silk.NET.Shaderc;
using System.Runtime.InteropServices;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Finds shaderc, the GLSL to SPIR-V compiler: Silk.NET's own search first (the copy next to the program, the
/// system library path; on Android the APK's libshaderc_shared.so), then the paths that search misses. Silk.NET looks
/// for the Silk.NET.Shaderc.Native copy in runtimes/&lt;RID&gt;/native by the distribution's RID ("ubuntu.26.04-x64")
/// and only falls back to linux-x64 for the distributions it knows, which do not include Ubuntu, so on Ubuntu a
/// portable build (tests, `dotnet run`) never found its own copy. The Linux distributions' package also names the
/// library libshaderc.so.1 (Debian/Ubuntu libshaderc1, Fedora, Arch), not libshaderc_shared.so.
/// A loaded library is never unloaded: shaderc registers a process-exit handler (<see cref="NativeProbe"/>).
/// </summary>
public static class ShadercLoader
{
    /// <summary>The library that the last successful <see cref="Load()"/> used ("" = Silk.NET's default search).</summary>
    public static string LoadedFrom { get; private set; } = "";

    /// <summary>Loads shaderc; throws the default search's error when no candidate loads either.</summary>
    public static Shaderc Load()
    {
        return Load(Shaderc.GetApi);
    }

    /// <summary><see cref="Load()"/> with <paramref name="defaultSearch"/> in place of Silk.NET's search (tests make it
    /// fail, as it does on Ubuntu).</summary>
    public static Shaderc Load(Func<Shaderc> defaultSearch)
    {
        Exception first;
        try
        {
            Shaderc api = defaultSearch();
            LoadedFrom = "";
            return api;
        }
        catch (Exception e) when (e is FileNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            first = e;
        }

        foreach (string candidate in Candidates())
        {
            if (Path.IsPathRooted(candidate) && !File.Exists(candidate))
            {
                continue;
            }

            if (!NativeLibrary.TryLoad(candidate, out nint handle))
            {
                continue;
            }

            if (!NativeLibrary.TryGetExport(handle, "shaderc_compiler_initialize", out _))
            {
                continue;
            }

            LoadedFrom = candidate;
            Log.Info($"shaderc: {candidate}");
            return new Shaderc(new LamdaNativeContext(name => NativeLibrary.TryGetExport(handle, name, out nint address) ? address : 0));
        }

        throw first;
    }

    /// <summary>The bundled copy's file name on this system.</summary>
    public static string BundledFileName =>
        OperatingSystem.IsWindows() ? "shaderc_shared.dll" : OperatingSystem.IsMacOS() ? "libshaderc_shared.dylib" : "libshaderc_shared.so";

    /// <summary>What <see cref="Load()"/> tries after Silk.NET's search, in order: the bundled copy under the portable
    /// RID (linux-x64, osx-arm64...), then the system's own library names and folders.</summary>
    public static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", BundledFileName);
        if (OperatingSystem.IsLinux())
        {
            yield return "libshaderc.so.1";
            yield return "libshaderc.so";
            yield return "libshaderc_shared.so.1";
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Homebrew's shaderc (/opt/homebrew on Apple silicon, /usr/local on Intel), off the default search path
            yield return "/opt/homebrew/lib/libshaderc_shared.dylib";
            yield return "/usr/local/lib/libshaderc_shared.dylib";
        }
    }
}
