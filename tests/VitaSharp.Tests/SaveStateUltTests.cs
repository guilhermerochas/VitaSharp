using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Save states with the SceUlt fallback in use (a title that imports SceUlt without shipping libult): its objects are
/// kernel objects and a save refused them ("not supported by save states yet"). The objects come back with their
/// counts, queued data and links (the pools their users keep busy), the ulthreads join after the load, and a thread
/// inside sceUltConditionVariableWait goes on from where it was: waiting for the signal (it released the mutex before
/// the save and must not be asked for it again), or, signalled, waiting only to take the mutex back. Runs
/// <see cref="SyntheticTitle"/> in the process; the waits are synthetic A32 loops calling the SceUlt stubs.
/// </summary>
[Collection("guest")]
public sealed class SaveStateUltTests : IDisposable
{
    private const int ErrState = unchecked((int)0x80810006), ErrAgain = unchecked((int)0x80810008);
    private const uint WaitSemaNid = 0x0C7B834B;
    private readonly HleHarness _harness;
    private readonly Emulator _emu;

    public SaveStateUltTests(GuestMemoryFixture fixture)
    {
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "ssult");
        _emu = _harness.Emu;
        _emu.LoadTitle(SyntheticTitle.Install(_harness.Root));
        _emu.Start();
    }

    public void Dispose()
    {
        _emu.Stop();
        _harness.Dispose();
    }

    private Ult Ult => _harness.Module<Ult>();

    private string StatePath => Path.Combine(_harness.Root, "state.vss");

    /// <summary>Zeroed user memory of the title's process.</summary>
    private uint UserAlloc(uint size, string name)
    {
        uint address;
        using (ProcessScope.Enter(_emu))
        {
            address = _emu.Memory.Alloc(size, name);
        }

        GuestMemory.Fill(address, 0, size);
        return address;
    }

    private int UltCall(string name, params object[] args)
    {
        using (ProcessScope.Enter(_emu))
        {
            return Call(Ult, name, args);
        }
    }

    private uint Stub(string name, string library)
    {
        uint stub = UserAlloc(0x10, "test:stub " + name);
        uint nid = VitaSharp.Core.Hle.NidDatabase.LookupName(name).First(n => n.Library == library).Nid;
        Linker.WriteHleStub(stub, _emu.Hle.GetHleFunctionIndex(nid, 0, library), nid);
        return stub;
    }

    /// <summary>Writes <paramref name="code"/> followed by <paramref name="literals"/>; an instruction
    /// <c>LoadLiteral(r, i)</c> loads literal i.</summary>
    private uint Assemble(string name, uint[] code, uint[] literals)
    {
        uint address = UserAlloc((uint)(code.Length + literals.Length) * 4, "test:" + name);
        uint pool = address + (uint)code.Length * 4;
        for (int i = 0; i < code.Length; i++)
        {
            uint word = code[i];
            if ((word & 0xFFFF0000) == 0xE59F0000 && (word & 0x0F00) == 0x0F00)
            {
                // a literal load: the literal's index in the low byte, resolved to the pc-relative offset
                uint pc = address + (uint)i * 4 + 8;
                uint literal = pool + (word & 0xFF) * 4;
                word = (word & 0xFFFFF000) | (literal - pc);
            }

            GuestMemory.W32(address + (uint)i * 4, word);
        }

        for (int i = 0; i < literals.Length; i++)
        {
            GuestMemory.W32(pool + (uint)i * 4, literals[i]);
        }

        return address;
    }

    /// <summary><c>ldr rN, [pc, literal]</c> with the literal's index (see <see cref="Assemble"/>).</summary>
    private static uint LoadLiteral(int register, int index) => 0xE59F0F00u | (uint)register << 12 | (uint)index;

    private static uint Branch(int from, int to) => 0xEA000000u | (uint)((to - from - 2) & 0xFFFFFF);

    private GuestThread StartThread(string name, uint entry)
    {
        using (ProcessScope.Enter(_emu))
        {
            GuestThread thread = _emu.Kernel.CreateThread(name, entry, 0x40, 0x4000, 0, 0x70000);
            thread.Start(0, 0);
            return thread;
        }
    }

    /// <summary><see cref="HleHarness.Until"/> with the state at the time out in the message.</summary>
    private static void UntilState(Func<bool> cond, Func<string> what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (watch.ElapsedMilliseconds > 120_000)
            {
                throw new Xunit.Sdk.XunitException($"timed out waiting for {what()}");
            }

            Thread.Sleep(1);
        }
    }

    private GuestThread Restored(string name) => Assert.Single(_emu.Kernel.Threads, t => t.Name == name);

    /// <summary>A waiting pool, a data pool and a queue with two items, a semaphore, a reader/writer lock and an ended
    /// ulthread, saved; changed after the save; loaded: the saved items, counts, users and the ulthread come back.</summary>
    [Fact]
    public void UltObjectsAreSavedWithTheirStateAndLinks()
    {
        uint memory = UserAlloc(0x1000, "test:ult objects");
        uint name = memory;
        GuestMemory.WriteCString(name, "VsTestUlt");
        uint wq = memory + 0x100, dp = memory + 0x180, queue = memory + 0x200, sema = memory + 0x280;
        uint rw = memory + 0x300, runtime = memory + 0x380, ulthread = memory + 0x400;
        uint work = memory + 0x800, data = memory + 0x40, popped = memory + 0x60, status = memory + 0x70;
        uint context = memory + 0x500;
        Assert.Equal(0, UltCall("_sceUltWaitingQueueResourcePoolCreate", wq, name, 4u, 4u, work, 0u));
        Assert.Equal(0, UltCall("_sceUltQueueDataResourcePoolCreate", dp, name, 4u, 8u, 2u, wq, work, 0u));
        Assert.Equal(0, UltCall("_sceUltQueueCreate", queue, name, 8u, wq, dp, 0u));
        GuestMemory.Write(data, 0x4141414141414141UL);
        Assert.Equal(0, UltCall("sceUltQueueTryPush", queue, data));
        GuestMemory.Write(data, 0x4242424242424242UL);
        Assert.Equal(0, UltCall("sceUltQueueTryPush", queue, data));
        Assert.Equal(0, UltCall("_sceUltSemaphoreCreate", sema, name, 3, wq, 0u));
        Assert.Equal(0, UltCall("_sceUltReaderWriterLockCreate", rw, name, wq, 0u));
        Assert.Equal(0, UltCall("_sceUltUlthreadRuntimeCreate", runtime, name, 2u, 1u, work, 0u));
        uint entry = Assemble("ulthread entry", [0xE3A00007, 0xE12FFF1E], []); // mov r0, #7; bx lr
        _harness.OnGuest(_ => Assert.Equal(0, UltCall("_sceUltUlthreadCreate", ulthread, name, entry, 0u, context, 0x4000u, runtime, 0u)));
        GuestThread ulthreadThread = Restored("VsTestUlt");
        Until(() => ulthreadThread.Status == ThreadStatus.Dormant, "the ulthread ends");

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        Assert.Equal(0, UltCall("sceUltQueueTryPop", queue, popped));
        Assert.Equal(0, UltCall("sceUltSemaphoreTryAcquire", sema, 3));
        Assert.Equal(0, UltCall("sceUltUlthreadTryJoin", ulthread, new Ptr<int>(status)));

        SaveStates.Load(_emu, StatePath);
        Assert.Equal(0, UltCall("sceUltQueueTryPop", queue, popped));
        Assert.Equal(0x4141414141414141UL, GuestMemory.Read<ulong>(popped));
        Assert.Equal(0, UltCall("sceUltQueueTryPop", queue, popped));
        Assert.Equal(0x4242424242424242UL, GuestMemory.Read<ulong>(popped));
        Assert.Equal(ErrAgain, UltCall("sceUltQueueTryPop", queue, popped));
        Assert.Equal(0, UltCall("sceUltSemaphoreTryAcquire", sema, 3));
        Assert.Equal(ErrAgain, UltCall("sceUltSemaphoreTryAcquire", sema, 1));

        // the users the links count: the data pool is busy while its queue lives, the waiting pool while anything does
        Assert.Equal(ErrState, UltCall("sceUltQueueDataResourcePoolDestroy", dp));
        Assert.Equal(0, UltCall("sceUltQueueDestroy", queue));
        Assert.Equal(0, UltCall("sceUltQueueDataResourcePoolDestroy", dp));
        Assert.Equal(ErrState, UltCall("sceUltWaitingQueueResourcePoolDestroy", wq));
        Assert.Equal(0, UltCall("sceUltSemaphoreDestroy", sema));
        Assert.Equal(0, UltCall("sceUltReaderWriterLockDestroy", rw));
        Assert.Equal(0, UltCall("sceUltWaitingQueueResourcePoolDestroy", wq));

        // the ulthread is the restored thread's: it joins with its exit status and leaves the runtime free
        GuestMemory.W32(status, 0);
        Assert.Equal(ErrState, UltCall("sceUltUlthreadRuntimeDestroy", runtime));
        Assert.Equal(0, UltCall("sceUltUlthreadTryJoin", ulthread, new Ptr<int>(status)));
        Assert.Equal(7u, GuestMemory.R32(status));
        Assert.Equal(0, UltCall("sceUltUlthreadRuntimeDestroy", runtime));
    }

    /// <summary>The objects of a condition test: a waiting pool, a mutex and a condition bound to it.</summary>
    private (uint Mutex, uint Cond) MutexAndCond()
    {
        uint memory = UserAlloc(0x800, "test:ult cond");
        GuestMemory.WriteCString(memory, "VsTestUltCond");
        uint wq = memory + 0x100, mutex = memory + 0x180, cond = memory + 0x200;
        Assert.Equal(0, UltCall("_sceUltWaitingQueueResourcePoolCreate", wq, memory, 4u, 4u, memory + 0x400, 0u));
        Assert.Equal(0, UltCall("_sceUltMutexCreate", mutex, memory, wq, 0u));
        Assert.Equal(0, UltCall("_sceUltConditionVariableCreate", cond, memory, mutex, 0u));
        return (mutex, cond);
    }

    /// <summary>Guest code: lock the mutex, then wait on the condition again and again, counting the returns in the
    /// counter's word and keeping the last result in the next one.</summary>
    private uint CondWaiterCode(uint mutex, uint cond, uint counter)
    {
        uint lockStub = Stub("sceUltMutexLock", "SceUlt"), waitStub = Stub("sceUltConditionVariableWait", "SceUlt");
        return Assemble("cond waiter",
        [
            LoadLiteral(0, 0),
            LoadLiteral(12, 1),
            0xE12FFF3C, // blx r12
            LoadLiteral(0, 2), // 3: the wait
            LoadLiteral(12, 3),
            0xE12FFF3C,
            LoadLiteral(4, 4),
            0xE5945000, // ldr r5, [r4]
            0xE2855001, // add r5, r5, #1
            0xE5845000, // str r5, [r4]
            0xE5840004, // str r0, [r4, #4]
            Branch(11, 3),
        ], [mutex, lockStub, cond, waitStub, counter]);
    }

    /// <summary>A thread waiting for the signal when the state is saved still waits after the load (it is not asked
    /// for the mutex it released), and the signal wakes it with the mutex taken back.</summary>
    [Fact]
    public void AThreadWaitingOnAnUltConditionWaitsForTheSignalAfterALoad()
    {
        (uint mutex, uint cond) = MutexAndCond();
        uint counter = UserAlloc(0x10, "test:cond counter");
        GuestThread waiter = StartThread("VsTestCondWaiter", CondWaiterCode(mutex, cond, counter));
        UntilState(() => Ult.CondWaitOf(cond, waiter).Inside && waiter.Status == ThreadStatus.Waiting, () => $"the thread waits on the condition ({waiter.Status}, {waiter.WaitingOn}, {Ult.CondWaitOf(cond, waiter)}, counter {GuestMemory.R32(counter)}, result 0x{GuestMemory.R32(counter + 4):X8})");

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        Assert.Equal(0, UltCall("sceUltConditionVariableSignal", cond));
        Until(() => GuestMemory.R32(counter) == 1, "the wake-up after the save");

        SaveStates.Load(_emu, StatePath);
        GuestThread restored = Restored("VsTestCondWaiter");
        Until(() => Ult.CondWaitOf(cond, restored).Inside && restored.Status == ThreadStatus.Waiting, "the loaded thread waits on the condition");
        Assert.Equal(0u, GuestMemory.R32(counter));
        Assert.False(Ult.CondWaitOf(cond, restored).Signalled);

        Assert.Equal(0, UltCall("sceUltConditionVariableSignal", cond));
        Until(() => GuestMemory.R32(counter) == 1, "the wake-up after the load");
        Assert.Equal(0u, GuestMemory.R32(counter + 4));
        Until(() => Ult.CondWaitOf(cond, restored).Inside, "the thread waits again");
        Assert.Equal(1u, GuestMemory.R32(counter));
    }

    /// <summary>A thread that was signalled and waits for the mutex (another thread holds it) when the state is saved
    /// waits only for the mutex after the load: it takes it when the holder unlocks, without a new signal.</summary>
    [Fact]
    public void ASignalledThreadWaitingForTheUltMutexTakesItAfterALoad()
    {
        ThreadMgr threadMgr = _harness.Module<ThreadMgr>();
        (uint mutex, uint cond) = MutexAndCond();
        uint counter = UserAlloc(0x10, "test:cond counter");
        int sema;
        using (ProcessScope.Enter(_emu))
        {
            sema = threadMgr.sceKernelCreateSema("VsTestUltHold", 0, 0, 1, 0);
        }

        Assert.True(sema > 0, $"sceKernelCreateSema: 0x{sema:X8}");
        GuestThread waiter = StartThread("VsTestCondWaiter", CondWaiterCode(mutex, cond, counter));
        UntilState(() => Ult.CondWaitOf(cond, waiter).Inside && waiter.Status == ThreadStatus.Waiting, () => $"the thread waits on the condition ({waiter.Status}, {waiter.WaitingOn}, {Ult.CondWaitOf(cond, waiter)}, counter {GuestMemory.R32(counter)}, result 0x{GuestMemory.R32(counter + 4):X8})");

        // the holder: lock the mutex, wait on the kernel semaphore, unlock, wait again
        uint lockStub = Stub("sceUltMutexLock", "SceUlt"), unlockStub = Stub("sceUltMutexUnlock", "SceUlt");
        uint waitSema = UserAlloc(0x10, "test:wait sema stub");
        Linker.WriteHleStub(waitSema, _emu.Hle.GetHleFunctionIndex(WaitSemaNid, 0, "SceLibKernel"), WaitSemaNid);
        uint holderCode = Assemble("mutex holder",
        [
            LoadLiteral(0, 0),
            LoadLiteral(12, 1),
            0xE12FFF3C, // blx r12: lock
            LoadLiteral(0, 2), // 3
            0xE3A01001, // mov r1, #1
            0xE3A02000, // mov r2, #0
            LoadLiteral(12, 3),
            0xE12FFF3C, // blx r12: wait on the semaphore
            LoadLiteral(0, 0),
            LoadLiteral(12, 4),
            0xE12FFF3C, // blx r12: unlock
            Branch(11, 3),
        ], [mutex, lockStub, (uint)sema, waitSema, unlockStub]);
        GuestThread holder = StartThread("VsTestUltHolder", holderCode);
        Until(() => holder.Status == ThreadStatus.Waiting, "the holder waits on the semaphore with the mutex");

        Assert.Equal(0, UltCall("sceUltConditionVariableSignal", cond));
        Until(() => Ult.CondWaitOf(cond, waiter).Signalled && waiter.Status == ThreadStatus.Waiting, "the signalled thread waits for the mutex");

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        Assert.Equal(0, Signal(threadMgr, sema));
        Until(() => GuestMemory.R32(counter) == 1, "the mutex taken after the save");

        SaveStates.Load(_emu, StatePath);
        GuestThread restored = Restored("VsTestCondWaiter");
        Until(() => Ult.CondWaitOf(cond, restored).Signalled && restored.Status == ThreadStatus.Waiting, "the loaded thread waits for the mutex");
        Assert.Equal(0u, GuestMemory.R32(counter));

        Assert.Equal(0, Signal(threadMgr, sema));
        Until(() => GuestMemory.R32(counter) == 1, "the mutex taken after the load");
        Assert.Equal(0u, GuestMemory.R32(counter + 4));
    }

    private int Signal(ThreadMgr threadMgr, int sema)
    {
        using (ProcessScope.Enter(_emu))
        {
            return threadMgr.sceKernelSignalSema(sema, 1);
        }
    }
}
