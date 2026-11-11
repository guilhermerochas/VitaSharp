using VitaSharp.Core;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The plugin configuration (ur0:tai/config.txt): kernel modules (*KERNEL) and user modules (*ALL, *TITLEID) load
/// through the same module loader path, kernel ones first, and a module whose imports name a library nothing provides
/// is not started (<see cref="Emulator.LoadPlugins"/>). The real-module test uses the plugins of the installed data
/// folder (read only: copied into the test's own folder) and is skipped without them; none is in the repository.
/// </summary>
[Collection("guest")]
public sealed class PluginLoaderTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-plugins-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    static string InstalledTai => Path.Combine(Environment.GetEnvironmentVariable(EnvVars.Data) is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp"), "fs", "ur0", "tai");

    Emulator NewEmulator(string config)
    {
        var tai = Path.Combine(_root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        File.WriteAllText(Path.Combine(tai, "config.txt"), config);
        return new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    [Fact]
    public void ConfigSectionsPickTheModulesKernelFirst()
    {
        using var emu = NewEmulator("# comment\n*ALL\nur0:tai/user_all.suprx\n*OTHER00001\nur0:tai/other.suprx\n*KERNEL\nur0:tai/kernel.skprx\n");
        var results = emu.LoadPlugins();
        Assert.Equal(2, results.Count);
        Assert.StartsWith("ur0:tai/kernel.skprx: not loaded", results[0]);
        Assert.StartsWith("ur0:tai/user_all.suprx: not loaded", results[1]); // missing file: the loader's error, no exception
    }

    [Fact]
    public void NoConfigLoadsNothing()
    {
        using var emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
        Assert.Empty(emu.LoadPlugins());
    }

    [RealPluginsFact]
    public void RealKernelPluginsLoadAndNameTheLibrariesNothingProvides()
    {
        string tai = Path.Combine(_root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        foreach (var file in Directory.GetFiles(InstalledTai, "*.skprx"))
        {
            File.Copy(file, Path.Combine(tai, Path.GetFileName(file)));
        }

        var listed = Directory.GetFiles(tai, "*.skprx").Order(StringComparer.Ordinal).Select(f => "ur0:tai/" + Path.GetFileName(f)).ToList();
        using var emu = NewEmulator("*KERNEL\n" + string.Join("\n", listed) + "\n");
        var results = emu.LoadPlugins();
        Assert.Equal(listed.Count, results.Count);
        foreach (var r in results)
        {
            // every module was read by the loader; a library no loaded module and no HLE provides is named
            Assert.DoesNotContain("not loaded", r);
            Assert.True(r.Contains("loaded (", StringComparison.Ordinal) || r.Contains("no module provides Sce", StringComparison.Ordinal) || r.Contains("no module provides taihen", StringComparison.Ordinal), r);
        }
    }
}

/// <summary>[Fact] skipped when the installed data folder has no kernel plugins (ur0:tai/*.skprx).</summary>
public sealed class RealPluginsFactAttribute : FactAttribute
{
    public RealPluginsFactAttribute()
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable(EnvVars.Data) is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp"), "fs", "ur0", "tai");
        if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*.skprx").Length == 0)
        {
            Skip = "no kernel plugins (ur0:tai/*.skprx) in the installed data folder";
        }
    }
}
