using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Kernel modules (the <c>*KERNEL</c> plugins and the kernel module manager's loads): they load into the kernel region
/// owned by the kernel process, module_start runs at once on a kernel thread and decides whether the module stays, and
/// the linker keeps kernel and user code apart: user modules reach only a kernel module's syscall-exported libraries,
/// kernel modules only kernel exports. Built from synthetic modules (<see cref="SceModuleBuilder"/>).
/// </summary>
[Collection("guest")]
public sealed class KernelModuleLoaderTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint SysLib = 0x5EC5A11E, KernLib = 0x5EC5A11F, SysFn = 0x11111111, KernFn = 0x22222222;
    private const uint ThreadmgrForDriver = 0xE2C40624, GetProcessId = 0x9DCB4B7A;

    private readonly HleHarness _harness = new(fixture, "kernmod");

    public void Dispose() => _harness.Dispose();

    private string Write(string file, byte[] elf)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), elf);
        return "ur0:tai/" + file;
    }

    private void Config(string text)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.txt"), text);
    }

    /// <summary>A kernel module exporting one syscall library (returns 42) and one kernel library (returns 7).</summary>
    private static byte[] Provider(byte startStatus = 0)
    {
        SceModuleBuilder b = new SceModuleBuilder("TestKernelProvider").Start(startStatus);
        int sys = b.ReturnConstant(42), kern = b.ReturnConstant(7);
        return b.Export("TestSys", SysLib, 0x4001, (SysFn, sys)).Export("TestKern", KernLib, 0x0001, (KernFn, kern)).Build();
    }

    private LoadedModule Loaded(string path) => _harness.Emu.Kernel.Modules.Values.Single(m => m.Path == path);

    [Fact]
    public void KernelPluginLoadsInTheKernelRegionAndStarts()
    {
        string path = Write("provider.skprx", Provider());
        Config($"*KERNEL\n{path}\n");
        List<string> results = _harness.Emu.LoadPlugins();
        Assert.Equal([$"{path}: loaded and started (TestKernelProvider)"], results);
        LoadedModule m = Loaded(path);
        Assert.True(m.IsKernel);
        Assert.True(m.Started);
        uint seg = m.Segments[0].Address;
        Assert.InRange(seg, GuestMemory.KernelStart, GuestMemory.KernelEnd - 1);
        Assert.True(KernelSpace.IsKernelOwned(seg));
        Assert.False(KernelSpace.IsUserRange(seg, 4));
        Assert.Equal(KernelSpace.KernelPid, _harness.Emu.Kernel.Objects.Get<ModuleObject>(m.Uid)!.OwnerPid);
        // user module queries do not list it
        Assert.DoesNotContain(m.Uid, UserModuleList());
        // listed again, it is not loaded twice
        Assert.Empty(_harness.Emu.LoadKernelPlugins());
    }

    private List<int> UserModuleList()
    {
        SystemMisc misc = _harness.Module<VitaSharp.Core.Hle.Modules.SystemMisc>();
        uint uids = _harness.Alloc(64 * 4), count = _harness.Word(64);
        Assert.Equal(0, Call(misc, "sceKernelGetModuleList", 0, uids, new Ptr<uint>(count)));
        return Enumerable.Range(0, (int)GuestMemory.R32(count)).Select(i => (int)GuestMemory.R32(uids + (uint)i * 4)).ToList();
    }

    [Fact]
    public void ModuleStartDecidesWhetherTheKernelModuleStays()
    {
        string failed = Write("failed.skprx", Provider(startStatus: 2));
        string noResident = Write("noresident.skprx", Provider(startStatus: 1));
        Config($"*KERNEL\n{failed}\n{noResident}\n");
        List<string> results = _harness.Emu.LoadPlugins();
        Assert.Equal($"{failed}: not started: module_start of TestKernelProvider returned 0x2, unloaded", results[0]);
        Assert.StartsWith($"{noResident}: started and unloaded", results[1]);
        Assert.DoesNotContain(_harness.Emu.Kernel.Modules.Values, m => m.IsKernel);
        // the exports went with them
        Assert.False(_harness.Emu.Linker.TryGetFunctionExport(SysFn, out _));
    }

    [Fact]
    public void ModuleStartRunsOnAKernelThread()
    {
        // module_start returns ksceKernelGetProcessId(): the kernel's pid, which is not a success status
        SceModuleBuilder b = new SceModuleBuilder("PidProbe").Import("SceThreadmgrForDriver", ThreadmgrForDriver, GetProcessId);
        int probe = b.CallImport(0, 0);
        byte[] elf = b.Export("PidProbeLib", 0x0BADF00D, 0x0001, (0x33333333, probe)).Build();
        string path = Write("pid.skprx", elf);
        LoadedModule lm = _harness.Emu.LoadModule(path, kernel: true);
        int status = _harness.Emu.StartKernelModule(lm, 0, 0);
        Assert.Equal(Emulator.ModuleStartSuccess, status); // no module_start: success
        uint fn = SceModuleBuilder.FunctionAddress(lm.Segments[0].Address, probe);
        _harness.OnGuest(t => Assert.Equal((uint)_harness.Emu.Pid, t.CallGuest(fn))); // a user thread calling in: the process
        GuestThread kernelThread = _harness.Emu.Kernel.CreateThread("kprobe", fn, 0x40, 0x4000, 0, 0x70000);
        kernelThread.KernelMode = true;
        kernelThread.Start(0, 0);
        Assert.True(kernelThread.WaitForExit(10_000));
        Assert.Equal(KernelSpace.KernelPid, kernelThread.ExitStatus);
    }

    [Fact]
    public void UserModulesSeeOnlySyscallExportsAndKernelModulesOnlyKernelExports()
    {
        string provider = Write("provider.skprx", Provider());
        Config($"*KERNEL\n{provider}\n");
        _harness.Emu.LoadPlugins();

        SceModuleBuilder user = new SceModuleBuilder("UserClient").Import("TestSys", SysLib, SysFn).Import("TestKern", KernLib, KernFn);
        int callSys = user.CallImport(0, 0), callKern = user.CallImport(1, 0);
        LoadedModule um = _harness.Emu.LoadModule(Write("client.suprx", user.Build()));
        uint ubase = um.Segments[0].Address;
        _harness.OnGuest(t => Assert.Equal(42u, t.CallGuest(SceModuleBuilder.FunctionAddress(ubase, callSys))));
        // the kernel library is not linked into user code: its stub stays an HLE stub
        List<uint> userStubs = _harness.Emu.Linker.BoundStubs(KernFn, KernLib, kernel: false);
        Assert.Single(userStubs);
        Assert.Equal(0xEFu, GuestMemory.R32(userStubs[0]) >> 24);

        SceModuleBuilder kclient = new SceModuleBuilder("KernelClient").Import("TestKern", KernLib, KernFn).Import("TestSys", SysLib, SysFn);
        int kCallKern = kclient.CallImport(0, 0), kCallSys = kclient.CallImport(1, 0);
        LoadedModule km = _harness.Emu.LoadModule(Write("kclient.skprx", kclient.Build()), kernel: true);
        uint kbase = km.Segments[0].Address;
        _harness.OnGuest(t =>
        {
            Assert.Equal(7u, t.CallGuest(SceModuleBuilder.FunctionAddress(kbase, kCallKern)));
            Assert.Equal(42u, t.CallGuest(SceModuleBuilder.FunctionAddress(kbase, kCallSys)));
        });

        // a user module's export is not linked into kernel code
        var userLib = new SceModuleBuilder("UserLib");
        int ret9 = userLib.ReturnConstant(9);
        _harness.Emu.LoadModule(Write("userlib.suprx", userLib.Export("UserOnly", 0x0E5E0001, 0x0001, (0x44444444, ret9)).Build()));
        SceModuleBuilder kclient2 = new SceModuleBuilder("KernelClient2").Import("UserOnly", 0x0E5E0001, 0x44444444);
        kclient2.CallImport(0, 0);
        _harness.Emu.LoadModule(Write("kclient2.skprx", kclient2.Build()), kernel: true);
        List<uint> kernelStubs = _harness.Emu.Linker.BoundStubs(0x44444444, 0x0E5E0001, kernel: true);
        Assert.Single(kernelStubs);
        Assert.Equal(0xEFu, GuestMemory.R32(kernelStubs[0]) >> 24);

        // unloading the provider sends the user import back to the HLE
        _harness.Emu.UnloadModule(Loaded(provider));
        List<uint> again = _harness.Emu.Linker.BoundStubs(SysFn, SysLib, kernel: false);
        Assert.Equal(0xEFu, GuestMemory.R32(again[0]) >> 24);
    }

    /// <summary>A user module with no imports that only starts (taiHEN loads such plugins into every process).</summary>
    private static byte[] UserPlugin(string name) => new SceModuleBuilder(name).Start(0).Build();

    [Fact]
    public void TheSystemsProcessesGetTheirPluginsByTheirKernelTitleId()
    {
        // taiHEN: *KERNEL at boot, then each process's sections in file order: *ALL for all, *main for SceShell, the
        // NPXS ID for a system application
        string kernel = Write("k.skprx", Provider());
        string all = Write("all.suprx", UserPlugin("PluginAll"));
        string shell = Write("shell.suprx", UserPlugin("PluginShell"));
        string settings = Write("settings.suprx", UserPlugin("PluginSettings"));
        Config($"*KERNEL\n{kernel}\n*main\n{shell}\n*ALL\n{all}\n*NPXS10015\n{settings}\n");
        Emulator emu = _harness.Emu;
        emu.RunsOnSystemCore = true;
        emu.TitleId = "shell";

        Assert.Equal(Emulator.ShellProcessTitleId, emu.ProcessTitleId);
        Assert.Equal([$"{kernel}: loaded and started (TestKernelProvider)", $"{shell}: loaded (PluginShell)", $"{all}: loaded (PluginAll)"],
                     emu.LoadPlugins());

        // the next process: the kernel ones are there already
        emu.RunsOnSystemCore = false;
        emu.TitleId = "NPXS10015";
        Assert.Equal([$"{all}: loaded (PluginAll)", $"{settings}: loaded (PluginSettings)"], emu.LoadPlugins());
    }
}
