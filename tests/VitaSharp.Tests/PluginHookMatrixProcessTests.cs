using VitaSharp.Core;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The process-level cells of the hook validation (<see cref="PluginHookMatrixTests"/>), on each CPU path
/// (<see cref="HookExecutionPath"/>): a kernel plugin's hooks (on a firmware syscall the HLE serves and on a kernel
/// module's own syscall export) take the calls of an application the shell opens after the hook, run on the
/// application's threads in the application's process, with the application's code compiled; and a save state made
/// with the hooks in place loads with them active (also after the session released them and compiled the original
/// code again), while one made before the hooks loads without them (the compiled hooked code goes). Synthetic: two
/// synthetic titles, a firmware module in os0:kd, a kernel plugin and user modules of <see cref="SceModuleBuilder"/>.
/// </summary>
// Quarantined (Category=Slow) on 23/11/2026: in a full suite run, other classes of the shared "guest" collection
// leave kernel memory freed under these tests (registers of the framework read as zero); isolated they pass. Roadmap
// P1 "Hook tests: shared memory interference" tracks it; they come back to the fast suite when it is fixed.
[Collection("guest")]
[Trait("Category", "Slow")]
public sealed class PluginHookMatrixProcessTests : IDisposable
{
    private const uint ThreadmgrLib = 0x859A24B1, GetProcessId = 0x9DCB4B7A;
    private const uint SyscallLib = 0x0BAD7A62, SyscallNid = 0x2468ACE4;
    private const uint TargetLib = 0x0BAD7A60, TargetNid = 0x2468ACE0;

    private readonly HleHarness _harness;
    private readonly Emulator _shell;
    private readonly List<Emulator> _apps = [];

    public PluginHookMatrixProcessTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "hookmatrixp", kernelPlugins: true);
        _shell = _harness.Emu;
        var threadmgr = new SceModuleBuilder("SceKernelThreadMgr");
        int body = threadmgr.ReturnConstant(0);
        threadmgr.Export("SceThreadmgr", ThreadmgrLib, Linker.SyscallExportAttr | 1, (GetProcessId, body));
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "threadmgr.skprx"), threadmgr.Build());
        FirmwareKernelImage.Forget(_harness.Root);
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

    public static TheoryData<string> Paths => HookExecutionPath.Supported;

    private Emulator Kernel => KernelSpace.ProcessOf(_shell, KernelSpace.KernelPid)!;

    /// <summary>The framework of the kernel process: the one kernel plugins call.</summary>
    private PluginFramework KernelFramework => Kernel.Hle.Modules.OfType<PluginFramework>().Single();

    private string Folder(string name)
    {
        string dir = Path.Combine(_harness.Root, "ur0", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string WriteUser(string file, SceModuleBuilder module)
    {
        File.WriteAllBytes(Path.Combine(Folder("data"), file), module.Build());
        return "ur0:data/" + file;
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

    private uint KernelString(string text)
    {
        uint address;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            address = _shell.Memory.Alloc(0x40, "test:kernel string", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(address, 0, 0x40);
        GuestMemory.WriteCString(address, text);
        return address;
    }

    /// <summary>The kernel plugin of these tests, loaded at boot from *KERNEL: two hooks and a module exporting an A32 sum
    /// in a syscall library and another in a kernel library.</summary>
    private sealed record Plugin(uint[] Hooks, uint[] Refs, uint SyscallSum, uint KernelSum);

    private Plugin Boot(bool start)
    {
        SceModuleBuilder plugin = new SceModuleBuilder("ProcHookPlugin").Start(0);
        int[] hooks = [HookTestKit.ChainHook(plugin, 1), HookTestKit.ChainHook(plugin, 2)];
        SceModuleBuilder target = new SceModuleBuilder("ProcHookTarget").Start(0);
        int syscall = HookTestKit.ArmSum(target);
        int kernelSum = HookTestKit.ArmSum(target);
        target.Export("ProcSyscallLib", SyscallLib, Linker.SyscallExportAttr | 1, (SyscallNid, syscall));
        target.Export("ProcTargetLib", TargetLib, 0x0001, (TargetNid, kernelSum));
        File.WriteAllBytes(Path.Combine(Folder("tai"), "prochookplugin.skprx"), plugin.Build());
        File.WriteAllBytes(Path.Combine(Folder("tai"), "prochooktarget.skprx"), target.Build());
        File.WriteAllText(Path.Combine(Folder("tai"), "config.txt"), "*KERNEL\nur0:tai/prochooktarget.skprx\nur0:tai/prochookplugin.skprx\n");
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
        if (start)
        {
            _shell.Start();
        }

        LoadedModule pluginModule = KernelModules(_shell).Values.Single(m => m.Name == "ProcHookPlugin");
        LoadedModule targetModule = KernelModules(_shell).Values.Single(m => m.Name == "ProcHookTarget");
        uint[] refs = [KernelWord(), KernelWord()];
        uint[] addresses = new uint[hooks.Length];
        for (int i = 0; i < hooks.Length; i++)
        {
            addresses[i] = plugin.AddressOf(pluginModule.Segments[0].Address, hooks[i]);
            GuestMemory.W32(addresses[i] + HookTestKit.RefLiteral, refs[i]);
            _shell.InvalidateCode(addresses[i], HookTestKit.ChainHookSize);
        }

        uint targetBase = targetModule.Segments[0].Address;
        return new Plugin(addresses, refs, target.AddressOf(targetBase, syscall), target.AddressOf(targetBase, kernelSum));
    }

    /// <summary>A user module of <paramref name="process"/> calling sceKernelGetProcessId and the kernel module's
    /// syscall sum through its imports: (the processId caller, the sum caller).</summary>
    private (uint ProcessId, uint Sum) LoadClient(Emulator process, string file)
    {
        SceModuleBuilder client = new SceModuleBuilder("ProcClient")
            .Import("SceThreadmgr", ThreadmgrLib, GetProcessId)
            .Import("ProcSyscallLib", SyscallLib, SyscallNid);
        int processId = client.CallImport(0, 0);
        int sum = client.CallImport(1, 0);
        LoadedModule module = process.LoadModule(WriteUser(file, client));
        uint codeBase = module.Segments[0].Address;
        return (SceModuleBuilder.FunctionAddress(codeBase, processId), SceModuleBuilder.FunctionAddress(codeBase, sum));
    }

    /// <summary>Runs <paramref name="body"/> on a new thread of <paramref name="process"/> and waits for it.</summary>
    private static void OnThreadOf(Emulator process, Action<GuestThread> body, bool kernelMode = false)
    {
        Exception? failure = null;
        GuestThread thread = process.Kernel.CreateThread("hook-matrix", 0, 0x10000100, 0x10000, 0, 0x70000);
        thread.HostBody = t =>
        {
            try
            {
                t.KernelMode = kernelMode;
                body(t);
            }
            catch (GuestThreadExitException)
            {
                throw;
            }
            catch (Exception e)
            {
                failure = e;
            }
        };
        Assert.Equal(0, thread.Start(0, 0));
        Assert.True(thread.WaitForExit(120_000), "the guest thread did not finish");
        if (failure != null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>Calls the process-id caller and the sum caller <paramref name="count"/> times, expecting
    /// <paramref name="pid"/> and the sum, each times 10 plus its hook's number when hooked (0: not hooked).</summary>
    private static void Check(GuestThread t, (uint ProcessId, uint Sum) client, uint pid, int count, uint processIdHook, uint sumHook)
    {
        for (int i = 0; i < count; i++)
        {
            (uint a, uint b, uint c) = HookTestKit.Arguments(i);
            uint wantPid = processIdHook != 0 ? pid * 10 + processIdHook : pid;
            uint wantSum = sumHook != 0 ? (a + b + c) * 10 + sumHook : a + b + c;
            uint gotPid = t.CallGuest(client.ProcessId, a, b, c);
            uint gotSum = t.CallGuest(client.Sum, a, b, c);
            Assert.True(wantPid == gotPid, $"call {i}: sceKernelGetProcessId gave {gotPid}, expected {wantPid}");
            Assert.True(wantSum == gotSum, $"call {i}: the syscall sum gave {gotSum}, expected {wantSum}");
        }
    }

    private int HookKernel(string module, uint libNid, uint nid, uint hook, uint refVariable)
    {
        int uid;
        using (ProcessScope.Enter(Kernel))
        {
            uid = Call(KernelFramework, "HookFunctionExportForKernel", KernelSpace.KernelPid, refVariable, KernelString(module), libNid, nid, hook);
        }

        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        return uid;
    }

    private int ReleaseKernel(int uid, uint record)
    {
        using (ProcessScope.Enter(Kernel))
        {
            return Call(KernelFramework, "HookReleaseForKernel", uid, record);
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void KernelHooksTakeTheCallsOfAnApplicationOpenedAfterThemOnItsThreads(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        Plugin plugin = Boot(start: false);
        int processIdHook = HookKernel("SceKernelThreadMgr", ThreadmgrLib, GetProcessId, plugin.Hooks[0], plugin.Refs[0]);
        int sumHook = HookKernel("ProcHookTarget", SyscallLib, SyscallNid, plugin.Hooks[1], plugin.Refs[1]);

        // an application opened after the hooks: its imports bind to them and its calls run them in its process
        Emulator app = _shell.SpawnProcess(SyntheticTitle.Install(_harness.Root, "VSTS00002"), "eboot.bin", []);
        _apps.Add(app);
        (uint ProcessId, uint Sum) client = LoadClient(app, "procclient.suprx");
        uint pid = (uint)app.Pid;
        if (path.Name == HookExecutionPath.Precompiled)
        {
            HookTestKit.Precompile(app, client.ProcessId, client.Sum);
            path.AssertTranslated(app, "the application's callers", client.ProcessId, client.Sum);
        }

        OnThreadOf(app, t => Check(t, client, pid, path.WarmCalls, 1, 2));
        path.WaitForJit();
        if (path.CompilesHotCode)
        {
            path.AssertTranslated(app, "the application's callers", client.ProcessId, client.Sum);
        }

        OnThreadOf(app, t => Check(t, client, pid, 1, 1, 2));

        // released: the application's calls reach the originals again
        Assert.Equal(0, ReleaseKernel(processIdHook, GuestMemory.R32(plugin.Refs[0])));
        Assert.Equal(0, ReleaseKernel(sumHook, GuestMemory.R32(plugin.Refs[1])));
        OnThreadOf(app, t => Check(t, client, pid, path.WarmCalls, 0, 0));
        path.WaitForJit();
        OnThreadOf(app, t => Check(t, client, pid, 1, 0, 0));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void AStateSavedWithTheHooksLoadsThemActiveAndOneSavedBeforeLoadsWithout(string pathName)
    {
        using var path = new HookExecutionPath(pathName);
        Plugin plugin = Boot(start: true);
        (uint ProcessId, uint Sum) client = LoadClient(_shell, "procclient.suprx");
        uint pid = (uint)_shell.Pid;
        string before = Path.Combine(_harness.Root, "before.vss");
        string hooked = Path.Combine(_harness.Root, "hooked.vss");
        if (path.Name == HookExecutionPath.Precompiled)
        {
            HookTestKit.Precompile(_shell, client.ProcessId, client.Sum);
        }

        OnThreadOf(_shell, t => Check(t, client, pid, path.WarmCalls, 0, 0));
        path.WaitForJit();
        SaveStates.Save(_shell, before, timeoutMs: 60_000);

        int processIdHook = HookKernel("SceKernelThreadMgr", ThreadmgrLib, GetProcessId, plugin.Hooks[0], plugin.Refs[0]);
        int sumHook = HookKernel("ProcHookTarget", SyscallLib, SyscallNid, plugin.Hooks[1], plugin.Refs[1]);
        uint processIdRecord = GuestMemory.R32(plugin.Refs[0]);
        uint sumRecord = GuestMemory.R32(plugin.Refs[1]);
        OnThreadOf(_shell, t => Check(t, client, pid, path.WarmCalls, 1, 2));
        path.WaitForJit();
        SaveStates.Save(_shell, hooked, timeoutMs: 60_000);

        // the session releases the hooks and compiles the original code again; the hooked state brings them back
        Assert.Equal(0, ReleaseKernel(processIdHook, processIdRecord));
        Assert.Equal(0, ReleaseKernel(sumHook, sumRecord));
        OnThreadOf(_shell, t => Check(t, client, pid, path.WarmCalls, 0, 0));
        path.WaitForJit();
        SaveStates.Load(_shell, hooked);
        OnThreadOf(_shell, t => Check(t, client, pid, path.WarmCalls, 1, 2));
        path.WaitForJit();
        OnThreadOf(_shell, t => Check(t, client, pid, 1, 1, 2));

        // the state from before the hooks: the compiled hooked code goes, the calls are the originals
        SaveStates.Load(_shell, before);
        OnThreadOf(_shell, t => Check(t, client, pid, path.WarmCalls, 0, 0));
        path.WaitForJit();
        OnThreadOf(_shell, t => Check(t, client, pid, 1, 0, 0));
        Assert.Equal(SceError.UNKNOWN_UID, ReleaseKernel(processIdHook, processIdRecord));

        // the hooked state again: the saved UIDs and records release them, the originals come back
        SaveStates.Load(_shell, hooked);
        OnThreadOf(_shell, t => Check(t, client, pid, 1, 1, 2));
        Assert.Equal(0, ReleaseKernel(processIdHook, processIdRecord));
        Assert.Equal(0, ReleaseKernel(sumHook, sumRecord));
        OnThreadOf(_shell, t => Check(t, client, pid, path.WarmCalls, 0, 0));
    }
}
