using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Save states with the emulator option "Enable Kernel Plugins" on: the plugin framework's hooks and data injections
/// (the kernel process's, where kernel plugins call it) are part of the state. A load puts back the hooks the saved
/// session had, as they were (the patched code, the thunks, the reference records, the import stubs a hook on an HLE
/// function took, also for stubs bound after the load) and drops the ones made after the save, without loading any
/// plugin again. Before, a save with a kernel plugin's hook in place was refused ("module kernel PluginFramework: its
/// state is not supported by save states yet"). Synthetic kernel modules (SceModuleBuilder) and <see cref="SyntheticTitle"/>.
/// </summary>
[Collection("guest")]
public sealed unsafe class SaveStatePluginTests : IDisposable
{
    private const uint TargetLib = 0x0BAD7A60, TargetNid = 0x2468ACE0;
    private const uint ThreadmgrLib = 0x859A24B1, GetThreadId = 0x0FB972F9;
    private const uint PluginLib = 0x0BAD7A61, PluginNid = 0x2468ACE1;
    private const int LibraryAlreadyRegistered = unchecked((int)0x8002D013);
    private const int LibraryNotRegistered = unchecked((int)0x8002D081);
    private const int GlobalWorkQueue = 0x10023;
    private const string WorkerName = "SceKernelGlobalWorkQueue";
    private readonly HleHarness _harness;
    private readonly Emulator _emu;

    public SaveStatePluginTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "ssplugin", kernelPlugins: true);
        _emu = _harness.Emu;
        _emu.LoadTitle(SyntheticTitle.Install(_harness.Root));
        _emu.Start();
    }

    public void Dispose()
    {
        _emu.Stop();
        _harness.Dispose();
    }

    private Emulator Kernel => _emu.Vita.KernelProcessFor(_emu);

    /// <summary>The kernel process's framework: the one kernel plugins call.</summary>
    private PluginFramework Framework => Kernel.Hle.Modules.OfType<PluginFramework>().Single();

    private string StatePath => Path.Combine(_harness.Root, "state.vss");

    private LoadedModule Load(string file, SceModuleBuilder builder, bool kernel)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), builder.Build());
        return _emu.LoadModule("ur0:tai/" + file, kernel);
    }

    /// <summary>A kernel module exporting a function that returns 7.</summary>
    private uint LoadTarget()
    {
        var builder = new SceModuleBuilder("SsHookTarget");
        int returns7 = builder.ReturnConstant(7);
        builder.Export("SsHookTargetLib", TargetLib, 0x0001, (TargetNid, returns7));
        LoadedModule module = Load("sstarget.skprx", builder, kernel: true);
        return SceModuleBuilder.FunctionAddress(module.Segments[0].Address, returns7);
    }

    /// <summary>A kernel plugin with functions returning <paramref name="values"/> (the hook functions).</summary>
    private uint[] LoadHooks(params byte[] values)
    {
        var builder = new SceModuleBuilder("SsHookProvider");
        int[] functions = values.Select(builder.ReturnConstant).ToArray();
        LoadedModule module = Load("sshooks.skprx", builder, kernel: true);
        return functions.Select(f => SceModuleBuilder.FunctionAddress(module.Segments[0].Address, f)).ToArray();
    }

    /// <summary>A user module of the game's process that calls sceKernelGetThreadId through its import stub.</summary>
    private uint LoadGameCaller(string name)
    {
        SceModuleBuilder builder = new SceModuleBuilder(name).Import("SceThreadmgr", ThreadmgrLib, GetThreadId);
        int call = builder.CallImport(0, 0);
        LoadedModule module = Load(name + ".suprx", builder, kernel: false);
        return SceModuleBuilder.FunctionAddress(module.Segments[0].Address, call);
    }

    /// <summary>The firmware export entry of sceKernelGetThreadId as the kernel has it (an SVC stub in kernel memory).</summary>
    private uint KernelExportEntry()
    {
        uint entry;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            entry = _emu.Memory.Alloc(0x10, "test:export entry", MemoryRegion.Kernel);
        }

        Linker.WriteHleStub(entry, Kernel.Hle.GetHleFunctionIndex(GetThreadId, ThreadmgrLib, "SceThreadmgr"), GetThreadId);
        return entry;
    }

    private uint KernelWord(uint value)
    {
        uint word;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            word = _emu.Memory.Alloc(0x10, "test:kernel word", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(word, 0, 0x10);
        GuestMemory.W32(word, value);
        return word;
    }

    /// <summary>A string in kernel memory (what a kernel plugin passes).</summary>
    private uint KernelString(string text)
    {
        uint address;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            address = _emu.Memory.Alloc(0x40, "test:kernel string", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(address, 0, 0x40);
        GuestMemory.WriteCString(address, text);
        return address;
    }

    /// <summary>Calls a framework function as a kernel plugin does (in the kernel process).</summary>
    private int KernelCall(string name, params object?[] args)
    {
        using (ProcessScope.Enter(Kernel))
        {
            return Call(Framework, name, args);
        }
    }

    private uint RunGuest(uint function)
    {
        uint result = 0;
        _harness.OnGuest(t => result = t.CallGuest(function));
        return result;
    }

    /// <summary>
    /// A kernel plugin's export hook and data injection are saved: after the save the hook is released, the injection
    /// undone and another hook installed; the load puts back the saved hook (the function runs it, its reference and
    /// UID release it, which puts the original code back) and the saved injection (its UID writes the original bytes
    /// back), and the hook made after the save is gone.
    /// </summary>
    [Fact]
    public void AKernelPluginsHookAndInjectionComeBackWithALoad()
    {
        // the module had no save state support: a save after a plugin used it was refused naming it
        Assert.False(SnapshotSchema.IsStateful(typeof(PluginFramework)));
        uint target = LoadTarget();
        uint[] hooks = LoadHooks(42, 43);
        uint data = KernelWord(1);
        uint source = KernelWord(99);
        uint hookRef = KernelWord(0);
        uint laterRef = KernelWord(0);

        int uid = KernelCall("HookFunctionExportForKernel", KernelSpace.KernelPid, hookRef, KernelString("SsHookTarget"), TargetLib, TargetNid, hooks[0]);
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        int injection = KernelCall("InjectAbsForKernel", KernelSpace.KernelPid, data, source, 4u);
        Assert.True(injection > 0, $"injection returned 0x{injection:X8}");
        Assert.Equal(42u, RunGuest(target));
        Assert.Equal(99u, GuestMemory.R32(data));
        uint record = GuestMemory.R32(hookRef);

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);

        // the session goes on: the hook and the injection are undone, another hook is installed
        Assert.Equal(0, KernelCall("HookReleaseForKernel", uid, record));
        Assert.Equal(0, KernelCall("InjectReleaseForKernel", injection));
        Assert.Equal(1u, GuestMemory.R32(data));
        int later = KernelCall("HookFunctionExportForKernel", KernelSpace.KernelPid, laterRef, KernelString("SsHookTarget"), TargetLib, TargetNid, hooks[1]);
        Assert.True(later > 0, $"second hook returned 0x{later:X8}");
        Assert.Equal(43u, RunGuest(target));

        SaveStates.Load(_emu, StatePath);

        Assert.Equal(42u, RunGuest(target));
        Assert.Equal(99u, GuestMemory.R32(data));
        Assert.Equal(record, GuestMemory.R32(hookRef));
        // the framework's records are the saved ones: the saved UIDs undo the saved patches
        Assert.Equal(0, KernelCall("InjectReleaseForKernel", injection));
        Assert.Equal(1u, GuestMemory.R32(data));
        Assert.Equal(0, KernelCall("HookReleaseForKernel", uid, record));
        Assert.Equal(7u, RunGuest(target));
        Assert.NotEqual(0, KernelCall("HookReleaseForKernel", later, GuestMemory.R32(laterRef)));
    }

    /// <summary>
    /// A hook on an HLE function's firmware export takes the game's import stubs of it: saved with the hook in place
    /// and loaded after the hook was released, the game's call runs the hook again, and so does a module of the game
    /// loaded after the load (the redirection of stubs bound later is part of the state).
    /// </summary>
    [Fact]
    public void TheGamesImportStubOfAHookedHleFunctionRunsTheHookAfterALoad()
    {
        uint gameCall = LoadGameCaller("SsGameCaller");
        uint[] hooks = LoadHooks(42);
        uint entry = KernelExportEntry();
        uint hookRef = KernelWord(0);
        uint tid = RunGuest(gameCall);
        Assert.NotEqual(42u, tid);

        int uid;
        using (ProcessScope.Enter(Kernel))
        {
            uid = Convert.ToInt32(Invoke(Framework, "HookAbsolute", KernelSpace.KernelPid, hookRef, entry, hooks[0], GetThreadId));
        }

        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        Assert.Equal(42u, RunGuest(gameCall));
        uint record = GuestMemory.R32(hookRef);

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        Assert.Equal(0, KernelCall("HookReleaseForKernel", uid, record));
        Assert.NotEqual(42u, RunGuest(gameCall));

        SaveStates.Load(_emu, StatePath);

        Assert.Equal(42u, RunGuest(gameCall));
        uint laterCall = LoadGameCaller("SsLaterCaller");
        Assert.Equal(42u, RunGuest(laterCall));
        Assert.Equal(0, KernelCall("HookReleaseForKernel", uid, record));
        Assert.NotEqual(42u, RunGuest(gameCall));
        Assert.NotEqual(42u, RunGuest(laterCall));
    }

    /// <summary>
    /// The other way round: saved before the hook, loaded with the hook in place, the game's import stub calls the HLE
    /// function again and so does a module of the game loaded after the load (the running session's redirection goes).
    /// </summary>
    [Fact]
    public void AHookMadeAfterTheSaveIsGoneAfterALoad()
    {
        uint gameCall = LoadGameCaller("SsGameCaller");
        uint[] hooks = LoadHooks(42);
        uint entry = KernelExportEntry();
        uint hookRef = KernelWord(0);
        uint tid = RunGuest(gameCall);
        Assert.NotEqual(42u, tid);

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        int uid;
        using (ProcessScope.Enter(Kernel))
        {
            uid = Convert.ToInt32(Invoke(Framework, "HookAbsolute", KernelSpace.KernelPid, hookRef, entry, hooks[0], GetThreadId));
        }

        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        Assert.Equal(42u, RunGuest(gameCall));

        SaveStates.Load(_emu, StatePath);

        Assert.NotEqual(42u, RunGuest(gameCall));
        uint laterCall = LoadGameCaller("SsLaterCaller");
        Assert.NotEqual(42u, RunGuest(laterCall));
    }

    private string StateB => Path.Combine(_harness.Root, "state-b.vss");

    private ModulemgrKernel Modulemgr => Kernel.Hle.Modules.OfType<ModulemgrKernel>().Single();

    private ThreadmgrKernel Threadmgr => Kernel.Hle.Modules.OfType<ThreadmgrKernel>().Single();

    /// <summary>A kernel plugin's library: an export entry without AUTO_EXPORT (in the database only once the plugin
    /// registers it), whose function returns 7; returns the entry's address.</summary>
    private uint LoadPluginLibrary()
    {
        var builder = new SceModuleBuilder("SsPluginLib");
        int returns7 = builder.ReturnConstant(7);
        builder.Export("SsPluginLib", PluginLib, 0x0000, (PluginNid, returns7));
        LoadedModule module = Load("sspluginlib.skprx", builder, kernel: true);
        return module.ExportLibraries.Single(library => library.LibNid == PluginLib).EntryAddress;
    }

    /// <summary>A kernel module that imports the plugin's library weakly (bound while it is in the database, to the HLE
    /// otherwise); returns its function that calls the import.</summary>
    private uint LoadLibraryCaller(string name)
    {
        SceModuleBuilder builder = new SceModuleBuilder(name).WeakImport("SsPluginLib", PluginLib, PluginNid);
        int call = builder.CallImport(0, 0);
        LoadedModule module = Load(name + ".skprx", builder, kernel: true);
        return SceModuleBuilder.FunctionAddress(module.Segments[0].Address, call);
    }

    private bool LibraryInDatabase => KernelLinker(_emu).TryGetFunctionExport(PluginLib, PluginNid, out _);

    private int RegisterLibrary(uint entry)
    {
        using (ProcessScope.Enter(Kernel))
        {
            return Call(Modulemgr, "ksceKernelRegisterLibary", entry);
        }
    }

    private int ReleaseLibrary(uint entry)
    {
        using (ProcessScope.Enter(Kernel))
        {
            return Call(Modulemgr, "ksceKernelReleaseLibary", entry);
        }
    }

    /// <summary>
    /// A library a kernel plugin registered (ksceKernelRegisterLibary) is in the library database after a load as it
    /// was saved: released after the save, the load puts it back (its importers call it, registering it again answers
    /// "already registered", releasing it works); registered only after the save, the load takes it out (registering it
    /// works, a module loaded after the load binds to the HLE). Before, the database stayed the running session's.
    /// </summary>
    [Fact]
    public void ALibraryAKernelPluginRegisteredIsInTheDatabaseAsSaved()
    {
        uint entry = LoadPluginLibrary();
        uint caller = LoadLibraryCaller("SsLibCaller");
        Assert.NotEqual(7u, RunGuest(caller));
        Assert.Equal(0, RegisterLibrary(entry));
        Assert.Equal(7u, RunGuest(caller));

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        Assert.Equal(0, ReleaseLibrary(entry));
        Assert.False(LibraryInDatabase);
        Assert.NotEqual(7u, RunGuest(caller));

        SaveStates.Load(_emu, StatePath);

        Assert.True(LibraryInDatabase);
        Assert.Equal(7u, RunGuest(caller));
        Assert.Equal(LibraryAlreadyRegistered, RegisterLibrary(entry));
        Assert.Equal(0, ReleaseLibrary(entry));
        Assert.NotEqual(7u, RunGuest(caller));

        // the other way round: saved while it is out of the database, registered after the save
        SaveStates.Save(_emu, StateB, timeoutMs: 60_000);
        Assert.Equal(0, RegisterLibrary(entry));
        Assert.Equal(7u, RunGuest(caller));

        SaveStates.Load(_emu, StateB);

        Assert.False(LibraryInDatabase);
        Assert.NotEqual(7u, RunGuest(caller));
        Assert.Equal(LibraryNotRegistered, ReleaseLibrary(entry));
        uint later = LoadLibraryCaller("SsLibLaterCaller");
        Assert.NotEqual(7u, RunGuest(later));
        Assert.Equal(0, RegisterLibrary(entry));
        Assert.Equal(7u, RunGuest(later));
    }

    /// <summary>A task in kernel memory: adds 1 to the word its argument points to.</summary>
    private uint CountingTask()
    {
        uint code;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            code = _emu.Memory.Alloc(0x10, "test:work task", MemoryRegion.Kernel);
        }

        GuestMemory.W32(code, 0xE5901000); // ldr r1, [r0]
        GuestMemory.W32(code + 4, 0xE2811001); // add r1, r1, #1
        GuestMemory.W32(code + 8, 0xE5801000); // str r1, [r0]
        GuestMemory.W32(code + 12, 0xE12FFF1E); // bx lr
        return code;
    }

    /// <summary>Queues the task on the global work queue as a kernel plugin does and waits until it ran and the worker
    /// sleeps again.</summary>
    private void RunWorkTask(uint task, uint argument)
    {
        int before = Threadmgr.WorkTasksRun;
        using (ProcessScope.Enter(Kernel))
        {
            Assert.True(Call(Threadmgr, "ksceKernelEnqueueWorkQueue", GlobalWorkQueue, "ss-plugin-task", task, argument) > 0);
        }

        Until(() => Threadmgr.WorkTasksRun == before + 1, "the worker to run the task");
        Until(() => Workers().Count == 1 && Workers()[0].BlockedInHost, "the worker to sleep");
    }

    /// <summary>The global work queue's worker threads in the kernel's object table.</summary>
    private List<GuestThread> Workers() => Kernel.Kernel.Threads.Where(t => t.Name == WorkerName && t.IsLive).ToList();

    /// <summary>
    /// The global work queue's worker (a service thread, made with the first task a kernel plugin queues) follows the
    /// state: a load to before it existed lets the running one go (it ends, no thread keeps running outside the table)
    /// and the next task makes a new one; a load of a state that had one while this session has none gives the saved
    /// thread back, with its UID, and it runs the next task. Before, the running worker stayed alive outside the object
    /// table and the second load was refused ("the service thread ... does not exist in this session yet").
    /// </summary>
    [Fact]
    public void TheWorkQueuesWorkerFollowsTheState()
    {
        uint task = CountingTask();
        uint counter = KernelWord(0);
        Assert.Empty(Workers());
        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);

        RunWorkTask(task, counter);
        GuestThread saved = Workers()[0];
        int savedUid = saved.Uid;
        SaveStates.Save(_emu, StateB, timeoutMs: 60_000);

        SaveStates.Load(_emu, StatePath);
        Assert.Empty(Workers());
        Until(() => !saved.IsLive, "the worker made after the save to end");

        SaveStates.Load(_emu, StateB);
        GuestThread worker = Assert.Single(Workers());
        Assert.Equal(savedUid, worker.Uid);
        Assert.Equal(1u, GuestMemory.R32(counter));
        RunWorkTask(task, counter);
        Assert.Same(worker, Assert.Single(Workers()));
        Assert.Equal(2u, GuestMemory.R32(counter));

        // and back to before the worker: the next task makes a new one, which runs it once
        SaveStates.Load(_emu, StatePath);
        Until(() => !worker.IsLive, "the restored worker to end");
        Assert.Equal(0u, GuestMemory.R32(counter));
        RunWorkTask(task, counter);
        Assert.Equal(1u, GuestMemory.R32(counter));
    }

    /// <summary>
    /// A kernel plugin at work: a hook on a kernel module's export, a library it registered and a task on the global
    /// work queue (its worker alive). Saved, then all of it undone or run again, then loaded: the hook runs again, the
    /// library is in the database, the worker is the saved one and runs the next task once, and the game's call
    /// through an HLE import still answers (a thread's UID).
    /// </summary>
    [Fact]
    public void AKernelPluginsStateIsTheSavedOneAfterALoad()
    {
        uint target = LoadTarget();
        uint[] hooks = LoadHooks(42);
        uint entry = LoadPluginLibrary();
        uint caller = LoadLibraryCaller("SsLibCaller");
        uint gameCall = LoadGameCaller("SsGameCaller");
        uint task = CountingTask();
        uint counter = KernelWord(0);
        uint hookRef = KernelWord(0);
        int hook = KernelCall("HookFunctionExportForKernel", KernelSpace.KernelPid, hookRef, KernelString("SsHookTarget"), TargetLib, TargetNid, hooks[0]);
        Assert.True(hook > 0, $"hook returned 0x{hook:X8}");
        Assert.Equal(0, RegisterLibrary(entry));
        RunWorkTask(task, counter);
        int workerUid = Workers()[0].Uid;
        Assert.True((int)RunGuest(gameCall) > 0);

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        Assert.Equal(0, KernelCall("HookReleaseForKernel", hook, GuestMemory.R32(hookRef)));
        Assert.Equal(0, ReleaseLibrary(entry));
        RunWorkTask(task, counter);
        Assert.Equal(7u, RunGuest(target));
        Assert.NotEqual(7u, RunGuest(caller));
        Assert.Equal(2u, GuestMemory.R32(counter));

        SaveStates.Load(_emu, StatePath);

        Assert.Equal(42u, RunGuest(target));
        Assert.Equal(7u, RunGuest(caller));
        Assert.True(LibraryInDatabase);
        Assert.Equal(LibraryAlreadyRegistered, RegisterLibrary(entry));
        GuestThread worker = Assert.Single(Workers());
        Assert.Equal(workerUid, worker.Uid);
        Assert.Equal(1u, GuestMemory.R32(counter));
        RunWorkTask(task, counter);
        Assert.Equal(2u, GuestMemory.R32(counter));
        Assert.True((int)RunGuest(gameCall) > 0);
        Assert.True(_emu.Running);
    }

    /// <summary>The exception handlers kernel modules registered with SceExcpmgr (the kernel process's) are the saved
    /// ones after a load: one registered after the save is gone.</summary>
    [Fact]
    public void TheKernelsExceptionHandlersAreTheSavedOnesAfterALoad()
    {
        ProcessmgrKernel manager = Kernel.Hle.Modules.OfType<ProcessmgrKernel>().Single();
        uint first = KernelWord(0);
        uint second = KernelWord(0);
        using (ProcessScope.Enter(Kernel))
        {
            Assert.Equal(0, Call(manager, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 1u, first));
        }

        List<uint> saved = manager.ExceptionHandlers(GuestThread.ExceptionUndefined);
        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        using (ProcessScope.Enter(Kernel))
        {
            Assert.Equal(0, Call(manager, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 0u, second));
        }

        Assert.NotEqual(saved, manager.ExceptionHandlers(GuestThread.ExceptionUndefined));
        SaveStates.Load(_emu, StatePath);
        Assert.Equal(saved, manager.ExceptionHandlers(GuestThread.ExceptionUndefined));
        Assert.Equal((first & ~3u) + 8, manager.ExceptionVector(GuestThread.ExceptionUndefined));
    }

    /// <summary>A thread saved inside a kernel exception handler keeps its mode, banked registers, saved program status
    /// and fault registers: restored in user mode with the handler's stack pointer, its return (SUBS PC, LR / RFE)
    /// would have gone wrong.</summary>
    [Fact]
    public void AThreadInAnExceptionModeKeepsItsPrivilegedStateInTheRecord()
    {
        GuestThread thread;
        using (ProcessScope.Enter(Kernel))
        {
            thread = Kernel.Kernel.CreateThread("VsTestExceptionMode", 0, 0x40, 0x4000, 0, 0x70000);
        }

        CpuState* state = thread.Core.State;
        ExceptionModes.EnterException(state, ExceptionModes.Abort, 0x8100_0004, 0x8200_0008);
        state->BankedSp[ExceptionModes.Bank(ExceptionModes.User)] = 0x8130_0000;
        state->Ifar = 0x8100_0000;
        state->Ifsr = GuestThread.FaultTranslationPage;
        uint spsr = state->Spsr[ExceptionModes.Bank(ExceptionModes.Abort)];

        var writer = new SnapshotWriter();
        thread.WriteSnapshot(writer, GuestThread.SafeKind.Dormant);
        var restored = GuestThread.ReadSnapshot(Kernel, new SnapshotReader(writer.Data.ToArray()), out _, out _, out _);
        try
        {
            CpuState* loaded = restored.Core.State;
            Assert.Equal(ExceptionModes.Abort, restored.CpuMode);
            Assert.Equal(0x8100_0004u, loaded->R[14]);
            Assert.Equal(0x8130_0000u, loaded->BankedSp[ExceptionModes.Bank(ExceptionModes.User)]);
            Assert.Equal(spsr, loaded->Spsr[ExceptionModes.Bank(ExceptionModes.Abort)]);
            Assert.Equal(0x8100_0000u, loaded->Ifar);
            Assert.Equal(GuestThread.FaultTranslationPage, loaded->Ifsr);
        }
        finally
        {
            restored.Dispose();
        }
    }
}
