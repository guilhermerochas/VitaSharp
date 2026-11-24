using VitaSharp.Core;
using VitaSharp.Core.Loader;

namespace VitaSharp.Tests;

/// <summary>
/// The plugin configuration (ur0:tai/config.txt): kernel modules (*KERNEL) and user modules (*ALL, *TITLEID) load
/// through the same module loader path, kernel ones first, and a module whose imports name a library nothing provides
/// is not started (<see cref="Emulator.LoadPlugins"/>). Only synthetic modules in the test's own folder: a test never
/// reads the user's data folder or installed plugins.
/// </summary>
[Collection("guest")]
public sealed class PluginLoaderTests(GuestMemoryFixture fx, Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-plugins-" + Guid.NewGuid().ToString("N"));

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

    private Emulator NewEmulator(string config)
    {
        string tai = Path.Combine(_root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        File.WriteAllText(Path.Combine(tai, "config.txt"), config);
        return new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp", KernelPluginsDir = Path.Combine(_root, "kernel_plugins") });
    }

    [Fact]
    public void ConfigSectionsPickTheModulesKernelFirst()
    {
        using Emulator emu = NewEmulator("# comment\n*ALL\nur0:tai/user_all.suprx\n*OTHER00001\nur0:tai/other.suprx\n*KERNEL\nur0:tai/kernel.skprx\n");
        List<string> results = emu.LoadPlugins();
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
    /// Kernel plugins, all listed in *KERNEL, each read by the loader: a module that exports the plugin framework
    /// libraries (a user's copy of the framework) is not loaded, since the emulator provides them; one whose imports
    /// resolve (here a framework function the emulator implements) loads and starts; one importing a library nothing
    /// provides is not started. Synthetic modules in the test's own folder, never the user's installed plugins.
    /// </summary>
    [Fact]
    public void KernelPluginsGoThroughTheGenericLoader()
    {
        const uint FrameworkFunction = 0x87E73973; // a module lookup the emulator's plugin framework implements
        string tai = Path.Combine(_root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        SceModuleBuilder framework = new("VsPluginFramework");
        framework.Export("VsFrameworkForKernel", 0x11110001, 0x0001, (FrameworkFunction, framework.ReturnConstant(0)));
        File.WriteAllBytes(Path.Combine(tai, "a_framework.skprx"), framework.Start(0).Build());
        File.WriteAllBytes(Path.Combine(tai, "b_plugin.skprx"),
            new SceModuleBuilder("VsKernelPlugin").Import("VsFrameworkForKernel", 0x11110001, FrameworkFunction).Start(0).Build());
        File.WriteAllBytes(Path.Combine(tai, "c_missing.skprx"),
            new SceModuleBuilder("VsMissingImport").Import("VsNothingForKernel", 0x22220002, 0x12345678).Start(0).Build());
        List<string> listed = ["ur0:tai/a_framework.skprx", "ur0:tai/b_plugin.skprx", "ur0:tai/c_missing.skprx"];
        using Emulator emu = NewEmulator("*KERNEL\n" + string.Join("\n", listed) + "\n");
        List<string> results = emu.LoadPlugins();
        output.WriteLine(string.Join("\n", results));
        Assert.Equal(listed.Count, results.Count);
        Assert.EndsWith("not loaded (the emulator provides its libraries)", results[0]);
        Assert.Contains("loaded and started", results[1], StringComparison.Ordinal);
        Assert.Contains("not started: no module provides VsNothingForKernel", results[2], StringComparison.Ordinal);
        var plugins = HleHarness.KernelModules(emu).Values.Where(m => m.IsKernel && m.Name.StartsWith("Vs", StringComparison.Ordinal)).ToList();
        LoadedModule started = Assert.Single(plugins);
        Assert.Equal("VsKernelPlugin", started.Name);
        Assert.True(started.Started);
    }
}
