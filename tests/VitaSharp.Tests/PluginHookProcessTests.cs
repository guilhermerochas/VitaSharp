using VitaSharp.Core;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Plugin framework hooks across processes (option "Enable Kernel Plugins"), as on the Vita, where there is one kernel:
/// a kernel plugin's hook on a firmware syscall export holds for every process, also one the shell opens after the
/// hook; a kernel plugin's hook or data injection in a user process is made in that process's memory (the framework
/// switches to the process's address space), not in the caller's; and the patches of a process go when the process
/// ends (the framework drops them when the process unloads). Synthetic: two synthetic titles (the shell's process and
/// an application next to it), a firmware module in os0:kd exporting sceKernelGetProcessId's syscall library, a kernel
/// plugin and user modules of <see cref="SceModuleBuilder"/>.
/// </summary>
[Collection("guest")]
public sealed class PluginHookProcessTests : IDisposable
{
    private const uint ThreadmgrLib = 0x859A24B1, GetProcessId = 0x9DCB4B7A;
    private const uint TargetLib = 0x0BAD7A60, TargetNid = 0x2468ACE0;

    private readonly HleHarness _harness;
    private readonly Emulator _shell;
    private readonly List<Emulator> _apps = [];

    public PluginHookProcessTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "khookp", kernelPlugins: true);
        _shell = _harness.Emu;
        var threadmgr = new SceModuleBuilder("SceKernelThreadMgr");
        int body = threadmgr.ReturnConstant(0);
        threadmgr.Export("SceThreadmgr", ThreadmgrLib, Linker.SyscallExportAttr | 1, (GetProcessId, body));
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "threadmgr.skprx"), threadmgr.Build());
        FirmwareKernelImage.Forget(_harness.Root);

        SceModuleBuilder plugin = new SceModuleBuilder("HookPlugin").Start(0);
        plugin.ReturnConstant(9);
        File.WriteAllBytes(Path.Combine(Folder("tai"), "hookplugin.skprx"), plugin.Build());
        File.WriteAllText(Path.Combine(Folder("tai"), "config.txt"), "*KERNEL\nur0:tai/hookplugin.skprx\n");
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
    }

    public void Dispose()
    {
        foreach (Emulator app in _apps)
        {
            app.Stop();
        }

        _shell.Stop();
        FirmwareKernelImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    private string Folder(string name)
    {
        string dir = Path.Combine(_harness.Root, "ur0", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private Emulator Kernel => KernelSpace.ProcessOf(_shell, KernelSpace.KernelPid)!;

    /// <summary>The framework of the kernel process: the one kernel plugins call.</summary>
    private PluginFramework KernelFramework => Kernel.Hle.Modules.OfType<PluginFramework>().Single();

    private uint HookFunction => SceModuleBuilder.FunctionAddress(KernelModules(_shell).Values.Single(m => m.Name == "HookPlugin").Segments[0].Address, 1);

    private Emulator OpenApp()
    {
        Emulator app = _shell.SpawnProcess(SyntheticTitle.Install(_harness.Root, "VSTS00002"), "eboot.bin", []);
        _apps.Add(app);
        return app;
    }

    private uint KernelWord()
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uint word = _shell.Memory.Alloc(16, "test:hook ref", MemoryRegion.Kernel, 16);
            GuestMemory.W32(word, 0);
            return word;
        }
    }

    /// <summary>The target of the jump stub (movw/movt r12; bx r12) at <paramref name="at"/> in
    /// <paramref name="process"/>'s memory, or null when it is not one.</summary>
    private static uint? JumpTarget(Emulator process, uint at)
    {
        using (AddressSpace.Use(process.Space))
        {
            uint w0 = GuestMemory.R32(at), w1 = GuestMemory.R32(at + 4);
            if ((w0 & 0xFFF0F000) != 0xE300C000 || (w1 & 0xFFF0F000) != 0xE340C000)
            {
                return null;
            }

            return ((w0 >> 4) & 0xF000) | (w0 & 0xFFF) | ((((w1 >> 4) & 0xF000) | (w1 & 0xFFF)) << 16);
        }
    }

    private string WriteUser(string file, SceModuleBuilder module)
    {
        File.WriteAllBytes(Path.Combine(Folder("data"), file), module.Build());
        return "ur0:data/" + file;
    }

    [Fact]
    public void AKernelHookOnASyscallExportTakesTheImportsOfAProcessMadeAfterIt()
    {
        uint refVariable = KernelWord();
        int uid = Call(KernelFramework, "HookFunctionExportForKernel", KernelSpace.KernelPid, refVariable, _harness.Str("SceKernelThreadMgr"),
            ThreadmgrLib, GetProcessId, HookFunction);
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        uint? thunk = JumpTarget(_shell, Kernel.FirmwareKernel.EntryOf(ThreadmgrLib, GetProcessId));
        Assert.NotNull(thunk);

        // an application opened after the hook: its import of the syscall goes to the hook too
        Emulator app = OpenApp();
        SceModuleBuilder client = new SceModuleBuilder("ProcessIdClient").Import("SceThreadmgr", ThreadmgrLib, GetProcessId);
        client.CallImport(0, 0);
        app.LoadModule(WriteUser("client.suprx", client));
        uint stub = Assert.Single(app.Linker.BoundStubs(GetProcessId, ThreadmgrLib, kernel: false));
        Assert.Equal(thunk, JumpTarget(app, stub));

        // released: the application's import is the syscall again
        Assert.Equal(0, Call(KernelFramework, "HookReleaseForKernel", uid, GuestMemory.R32(refVariable)));
        Assert.Null(JumpTarget(app, stub));
    }

    [Fact]
    public void AKernelPluginsPatchesOfAUserProcessAreMadeInItsMemoryAndGoWhenItEnds()
    {
        Emulator app = OpenApp();
        var target = new SceModuleBuilder("AppTarget");
        int add = target.Arm(0xE0800001, 0xE2800000, 0xE2800000, 0xE12FFF1E);
        target.Export("AppTargetLib", TargetLib, 0x0001, (TargetNid, add));
        LoadedModule module = app.LoadModule(WriteUser("apptarget.suprx", target));
        uint function = target.AddressOf(module.Segments[0].Address, add);
        uint dataWord = module.Segments[0].Address + 0xA00;
        uint before;
        using (AddressSpace.Use(_shell.Space))
        {
            before = GuestMemory.R32(function);
        }

        // the hook is written over the application's function, in the application's memory
        uint refVariable = KernelWord();
        int hookUid = Call(KernelFramework, "HookFunctionExportForKernel", app.Pid, refVariable, _harness.Str("AppTarget"), TargetLib, TargetNid, HookFunction);
        Assert.True(hookUid > 0, $"hook returned 0x{hookUid:X8}");
        Assert.NotNull(JumpTarget(app, function));
        using (AddressSpace.Use(_shell.Space))
        {
            Assert.Equal(before, GuestMemory.R32(function));
        }

        // a data injection into the application: its bytes land in its memory, the source read in the caller's
        uint source = KernelWord();
        GuestMemory.W32(source, 0x600DF00D);
        int injectUid = Call(KernelFramework, "InjectAbsForKernel", app.Pid, dataWord, source, 4u);
        Assert.True(injectUid > 0, $"injection returned 0x{injectUid:X8}");
        using (AddressSpace.Use(app.Space))
        {
            Assert.Equal(0x600DF00Du, GuestMemory.R32(dataWord));
        }

        // the application ends: the framework drops its patches (their UIDs are gone)
        app.Terminate("closed by the test");
        Assert.Null(Kernel.Kernel.Objects.Get<KernelObject>(hookUid));
        Assert.Null(Kernel.Kernel.Objects.Get<KernelObject>(injectUid));
        Assert.Equal(SceError.UNKNOWN_UID, Call(KernelFramework, "HookReleaseForKernel", hookUid, GuestMemory.R32(refVariable)));
    }
}
