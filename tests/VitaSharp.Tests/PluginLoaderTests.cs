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
public sealed class PluginLoaderTests(GuestMemoryFixture fx, Xunit.Abstractions.ITestOutputHelper output) : IDisposable
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

    void CopyInstalledPlugins(params string[] only)
    {
        string tai = Path.Combine(_root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        foreach (var file in Directory.GetFiles(InstalledTai, "*.skprx"))
        {
            if (only.Length == 0 || only.Contains(Path.GetFileName(file)))
            {
                File.Copy(file, Path.Combine(tai, Path.GetFileName(file)));
            }
        }
    }

    /// <summary>
    /// The kernel plugins of the installed data folder (copied into the test's own folder), listed as a HENkaku Vita
    /// lists them (taiHEN first): each one is read by the loader. taiHEN starts and fails on its own (its module_start
    /// needs its hooks into the firmware's signature and DRM checks, which VitaSharp does not make work), so the plugins
    /// that import its libraries (kubridge, fd_fix) are not started: nothing provides taihenForKernel, as on a console
    /// without taiHEN.
    /// </summary>
    [RealPluginsFact]
    public void RealKernelPluginsGoThroughTheGenericLoader()
    {
        CopyInstalledPlugins();
        string tai = Path.Combine(_root, "ur0", "tai");
        var listed = Directory.GetFiles(tai, "*.skprx").Select(f => "ur0:tai/" + Path.GetFileName(f))
            .OrderBy(p => p == "ur0:tai/taihen.skprx" ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal).ToList();
        using var emu = NewEmulator("*KERNEL\n" + string.Join("\n", listed) + "\n");
        var results = emu.LoadPlugins();
        output.WriteLine(string.Join("\n", results));
        Assert.Equal(listed.Count, results.Count);
        foreach (var r in results)
        {
            Assert.DoesNotContain("not loaded", r);
            if (r.StartsWith("ur0:tai/taihen.skprx", StringComparison.Ordinal))
            {
                Assert.Equal("ur0:tai/taihen.skprx: not started: module_start of taihen returned 0x2, unloaded", r);
            }
            else if (r.StartsWith("ur0:tai/kubridge.skprx", StringComparison.Ordinal) || r.StartsWith("ur0:tai/fd_fix.skprx", StringComparison.Ordinal))
            {
                Assert.Contains("not started: no module provides taihenForKernel", r);
            }
        }

        Assert.DoesNotContain(emu.Kernel.Modules.Values, m => m.IsKernel);
    }

    /// <summary>
    /// The real taihen.skprx (skipped when it is not installed): found through the plugin configuration, loaded into the
    /// kernel region, every import bound to a firmware kernel export VitaSharp implements, its own module_start run on a
    /// kernel thread; it reports SCE_KERNEL_START_FAILED (2) because its hooks into the firmware's signature and DRM
    /// checks do not take effect, and the loader unloads it.
    /// </summary>
    [RealTaihenFact]
    public void RealTaihenRunsItsModuleStartAndFailsOnItsOwn()
    {
        CopyInstalledPlugins("taihen.skprx");
        using var emu = NewEmulator("*KERNEL\nur0:tai/taihen.skprx\n");
        var m = emu.LoadModule("ur0:tai/taihen.skprx", kernel: true);
        Assert.True(m.IsKernel);
        Assert.InRange(m.Segments[0].Address, VitaSharp.Core.Memory.GuestMemory.KernelStart, VitaSharp.Core.Memory.GuestMemory.KernelEnd - 1);
        var unbound = m.Imports.SelectMany(i => i.FuncNids.Select(n => (i.Library, Nid: n)))
            .Where(x => !emu.Hle.Implements(x.Nid) && !emu.Linker.TryGetVisibleExport(x.Nid, 0, kernelImporter: true, out _))
            .Select(x => $"{x.Library} 0x{x.Nid:X8}").ToList();
        Assert.True(unbound.Count == 0, string.Join(", ", unbound));
        Assert.Equal(Emulator.ModuleStartFailed, emu.StartKernelModule(m, 0, 0));
        emu.UnloadModule(m);

        var results = emu.LoadPlugins();
        Assert.Equal(["ur0:tai/taihen.skprx: not started: module_start of taihen returned 0x2, unloaded"], results);
        Assert.DoesNotContain(emu.Kernel.Modules.Values, x => x.IsKernel);
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

/// <summary>[Fact] skipped when the installed data folder has no ur0:tai/taihen.skprx.</summary>
public sealed class RealTaihenFactAttribute : FactAttribute
{
    public RealTaihenFactAttribute()
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable(EnvVars.Data) is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VitaSharp"), "fs", "ur0", "tai");
        if (!File.Exists(Path.Combine(dir, "taihen.skprx")))
        {
            Skip = "no ur0:tai/taihen.skprx in the installed data folder";
        }
    }
}
