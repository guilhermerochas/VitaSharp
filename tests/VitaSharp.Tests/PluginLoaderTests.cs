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

    /// <summary>
    /// The kernel plugins of the installed data folder (copied into the test's own folder): each is read by the loader
    /// and, when nothing it imports is missing, started on a kernel thread. kubridge and fd_fix run on the kernel
    /// environment and taiHEN's HLE and stay; taiHEN itself does not stay (its module_start fails without its hooks into
    /// the firmware's signature and DRM checks, which VitaSharp does not make work).
    /// </summary>
    [RealPluginsFact]
    public void RealKernelPluginsLoadAndStart()
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
        output.WriteLine(string.Join("\n", results));
        Assert.Equal(listed.Count, results.Count);
        foreach (var r in results)
        {
            Assert.DoesNotContain("not loaded", r);
            if (r.StartsWith("ur0:tai/kubridge.skprx", StringComparison.Ordinal) || r.StartsWith("ur0:tai/fd_fix.skprx", StringComparison.Ordinal))
            {
                Assert.Contains("loaded and started", r);
            }

            if (r.StartsWith("ur0:tai/taihen.skprx", StringComparison.Ordinal))
            {
                Assert.Contains("not started", r);
            }
        }

        if (results.Any(r => r.StartsWith("ur0:tai/kubridge.skprx", StringComparison.Ordinal)))
        {
            var ku = emu.Kernel.Modules.Values.Single(m => m.Name == "kubridge");
            Assert.True(ku.IsKernel);
            Assert.InRange(ku.Segments[0].Address, VitaSharp.Core.Memory.GuestMemory.KernelStart, VitaSharp.Core.Memory.GuestMemory.KernelEnd - 1);
            // its user library is a syscall export user programs bind to
            Assert.True(emu.Linker.TryGetVisibleExport(0x2EF7C290, 0x6DB9389B, kernelImporter: false, out _)); // kuKernelAllocMemBlock
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
