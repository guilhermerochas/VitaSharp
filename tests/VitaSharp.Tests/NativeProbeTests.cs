using System.Runtime.CompilerServices;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// A native library is never unloaded after it was loaded (<see cref="NativeProbe"/>): one that registered a process
/// exit handler leaves it behind, and on Linux exit() then jumps into whatever is mapped there later (shaderc did; the
/// probes of shaderc, GLFW, the Vulkan loader, Unicorn and FFmpeg's device libraries loaded and freed them again).
/// </summary>
public sealed class NativeProbeTests
{
    private static string? RepoRoot([CallerFilePath] string source = "")
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
        {
            for (DirectoryInfo? d = start.Length > 0 ? new DirectoryInfo(start) : null; d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
                {
                    return d.FullName;
                }
            }
        }

        return null;
    }

    [Fact]
    public void NoCodeUnloadsANativeLibrary()
    {
        string? root = RepoRoot();
        Assert.NotNull(root);
        string forbidden = "NativeLibrary" + ".Free(";
        var found = new List<string>();
        foreach (string top in new[] { "src", "tests", "build", "tools" })
        {
            string dir = Path.Combine(root!, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string sep = Path.DirectorySeparatorChar.ToString();
                if (file.Contains(sep + "bin" + sep) || file.Contains(sep + "obj" + sep) || file.Contains(sep + "obj-dev" + sep))
                {
                    continue;
                }

                int line = 0;
                foreach (string text in File.ReadLines(file))
                {
                    line++;
                    if (text.Contains(forbidden, StringComparison.Ordinal))
                    {
                        found.Add($"{Path.GetRelativePath(root!, file)}:{line}");
                    }
                }
            }
        }

        Assert.True(found.Count == 0, "native libraries unloaded (use NativeProbe, which keeps them loaded): " + string.Join(", ", found));
    }

    /// <summary>Disposing a Silk.NET API object (<c>X.GetApi()</c>) frees its library the same way (dlclose): the shaderc
    /// availability probe of the tests did, and the test host died with SIGSEGV after "Testhost process exiting" on every
    /// Linux CI job (exit() called shaderc's handler at the old address, by then part of VitaSharp.Desktop.dll's mapping).</summary>
    [Fact]
    public void NoCodeDisposesANativeApi()
    {
        string? root = RepoRoot();
        Assert.NotNull(root);
        // a Dispose call on a GetApi result, and a using declaration of one (patterns split so this file does not match)
        var disposed = new System.Text.RegularExpressions.Regex(@"GetApi\([^;]*\)\s*\." + @"Dispose\(");
        var usingApi = new System.Text.RegularExpressions.Regex(@"\busing\s*\(?\s*(var|[\w.<>?]+)\s+\w+\s*=\s*[\w.]+\." + @"GetApi\(");
        var found = new List<string>();
        foreach (string top in new[] { "src", "tests", "build", "tools" })
        {
            string dir = Path.Combine(root!, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string sep = Path.DirectorySeparatorChar.ToString();
                if (file.Contains(sep + "bin" + sep) || file.Contains(sep + "obj" + sep) || file.Contains(sep + "obj-dev" + sep))
                {
                    continue;
                }

                int line = 0;
                foreach (string text in File.ReadLines(file))
                {
                    line++;
                    if (disposed.IsMatch(text) || usingApi.IsMatch(text))
                    {
                        found.Add($"{Path.GetRelativePath(root!, file)}:{line}");
                    }
                }
            }
        }

        Assert.True(found.Count == 0, "Silk.NET APIs disposed (that unloads their library; keep the object for the whole process): " + string.Join(", ", found));
    }

    /// <summary>The shaderc probe of the tests leaves the library loaded (Linux: still in /proc/self/maps after it).</summary>
    [Fact]
    public void TheShadercProbeKeepsTheLibraryLoaded()
    {
        if (ShadercAvailability.SkipReason != null)
        {
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("libshaderc_shared", File.ReadAllText("/proc/self/maps"), StringComparison.Ordinal);
        }

        Assert.True(ShadercAvailability.ProbeKeptLoaded);
    }

    [Fact]
    public void AProbeTellsWhetherALibraryLoads()
    {
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        // the runtime's own JIT, next to System.Private.CoreLib on every OS
        string name = OperatingSystem.IsWindows() ? "clrjit.dll" : OperatingSystem.IsMacOS() ? "libclrjit.dylib" : "libclrjit.so";
        Assert.True(NativeProbe.CanLoad(Path.Combine(runtime, name)));
        Assert.False(NativeProbe.CanLoad(Path.Combine(runtime, "vitasharp-no-such-library")));
    }
}
