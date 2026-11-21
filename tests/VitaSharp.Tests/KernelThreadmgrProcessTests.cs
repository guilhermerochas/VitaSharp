using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The thread manager entries of FW 3.74 threadmgr (decompiled, study only) the process manager and drivers call:
/// a process's worker made and ended, a thread created in a process, the process type's thread kind, the RW lock,
/// timer and event calls that wrap the user ones, a work queue of one's own, the trace hooks, and the alarms of a
/// loaded state, whose timer line is armed again.
/// </summary>
[Collection("guest")]
public sealed unsafe class KernelThreadmgrProcessTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020005), IllegalAttr = unchecked((int)0x8002000E),
        IllegalPattern = unchecked((int)0x8002000D), TimerNoEvent = unchecked((int)0x80028242),
        IllegalAffinity = unchecked((int)0x80028025), UnknownWorkQueue = unchecked((int)0x80028301);

    private const uint TimerLine = 0x8D;

    private readonly HleHarness _harness = new(fixture, "kthrproc");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private ThreadMgr User => _harness.Module<ThreadMgr>();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private static int Run(GuestThread thread, object module, string name, params uint[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            thread.Hle.Cpu->R[i] = args[i];
        }

        Invoke(module, name, thread.Hle);
        return (int)thread.Hle.Cpu->R[0];
    }

    [Fact]
    public void AProcessWorkerIsMadeAndEnded()
    {
        uint process = _harness.Alloc(0x400);
        GuestMemory.W32(process + 0x64, (uint)_harness.Emu.Pid);
        Assert.Equal(0, Call(Threadmgr, "NID_B46502E6", process));
        int flag = (int)GuestMemory.R32(process + 0x334);
        int worker = (int)GuestMemory.R32(process + 0x330);
        Assert.NotNull(_harness.Emu.Kernel.Objects.Get<EventFlag>(flag));
        GuestThread thread = _harness.Emu.Kernel.Objects.Get<GuestThread>(worker)!;
        Assert.StartsWith("tworker: ", thread.Name);
        Assert.Equal(process + 0x30, GuestMemory.R32(process + 0x30));
        // the worker waits on its flag (threadmgr 3.74: bits 0x80000001, OR and clear, 16 ms at a time); a wait the
        // flag refuses would end it at once
        Until(() => thread.CurrentWaitSync != null, "the worker waits on its flag");
        Assert.Equal(ThreadStatus.Waiting, thread.Status);
        // bit 0 (the debug handler request) wakes it and it waits again
        Assert.Equal(0, Call(_harness.Module<ThreadMgr>(), "sceKernelSetEventFlag", flag, 1u));
        Until(() => thread.CurrentWaitSync != null && _harness.Emu.Kernel.Objects.Get<EventFlag>(flag)!.Pattern == 0, "the worker waits again");
        Assert.NotEqual(ThreadStatus.Dormant, thread.Status);
        Invoke(Threadmgr, "NID_5A23A1BE", process);
        Assert.Null(_harness.Emu.Kernel.Objects.Get<EventFlag>(flag));
        Assert.Null(_harness.Emu.Kernel.Objects.Get<GuestThread>(worker));
    }

    [Fact]
    public void AThreadIsCreatedInAProcessAndTheKindFollowsTheType()
    {
        uint name = _harness.Str("inproc");
        int pid = _harness.Emu.Pid;
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_C8E57BB4", 0, "x", 0x81000001u, 0x40, 0x1000, 0u, 0u, 0u));
        Assert.Equal(IllegalAffinity, Call(Threadmgr, "NID_C8E57BB4", pid, "x", 0x81000001u, 0x40, 0x1000, 0u, 0x10001u, 0u));
        int uid = Call(Threadmgr, "NID_C8E57BB4", pid, "inproc", 0x81000001u, 0x40, 0x1000, 0u, 0u, 0u);
        Assert.True(uid > 0);
        Assert.Equal(pid, _harness.Emu.Kernel.Objects.Get<GuestThread>(uid)!.Pid);
        uint parameters = _harness.Alloc(0x20);
        (uint Type, int Kind)[] kinds = [(0x1000000, 7), (0x2000000, 8), (0x4000000, 8), (0x5000000, 0xF)];
        foreach ((uint type, int kind) in kinds)
        {
            GuestMemory.W32(parameters + 0x14, type);
            Assert.Equal(kind, Call(Threadmgr, "NID_D9C097DD", parameters));
        }

        GuestMemory.W32(parameters + 0x14, 0x3000000);
        Assert.Throws<InvalidOperationException>(() => Invoke(Threadmgr, "NID_D9C097DD", parameters));
        Assert.True(name != 0);
    }

    [Fact]
    public void TheObjectCallsWrapTheUserOnes()
    {
        uint name = _harness.Str("kproc");
        uint left = _harness.Alloc(8);
        uint waiting = _harness.Alloc(4);
        _harness.OnGuest(t =>
        {
            int rwlock = Run(t, Threadmgr, "NID_04150799", name, 0u, 0u);
            Assert.NotNull(_harness.Emu.Kernel.Objects.Get<RwLock>(rwlock));
            int timer = Call(User, "sceKernelCreateTimer", "kt", 0u, 0u);
            Assert.Equal(TimerNoEvent, Call(Threadmgr, "NID_3634FFE6", timer, left));
            Assert.Equal(0, Run(t, Threadmgr, "NID_F285C94F", (uint)timer, waiting));
            Assert.Equal(0, Run(t, Threadmgr, "NID_91A8F38B", (uint)timer));
            Assert.Null(_harness.Emu.Kernel.Objects.Get<KernelObject>(timer));
            int simple = Call(User, "sceKernelCreateSimpleEvent", name, 0u, 0u, 0u);
            Assert.Equal(IllegalPattern, Run(t, Threadmgr, "NID_E5CAD3E2", (uint)simple, 0x10000u, 0u, 0u));
            Assert.Equal(0, Run(t, Threadmgr, "NID_E5CAD3E2", (uint)simple, 0x02000000u, 0u, 0u));
            Assert.Equal(0x02000000u, _harness.Emu.Kernel.Objects.Get<SimpleEvent>(simple)!.Pattern & 0xFF000000);
        });
    }

    [Fact]
    public void AWorkQueueOfOnesOwnTakesTasks()
    {
        uint code = _harness.Alloc(0x10);
        GuestMemory.W32(code, 0xE12FFF1E); // bx lr
        Assert.Equal(unchecked((int)0x80027101), Call(Threadmgr, "NID_771522C6", "q", 0u, 0u));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(IllegalAttr, Call(Threadmgr, "NID_771522C6", "q", 0x10u, 0u));
            int queue = Call(Threadmgr, "NID_771522C6", "kqueue", 0u, 0u);
            Assert.True(queue > 0);
            int task = Call(Threadmgr, "ksceKernelEnqueueWorkQueue", queue, "kqtask", code, 0u);
            Assert.True(task > 0);
            Assert.Equal(0, Call(Threadmgr, "NID_154F2C2A", queue, new Ptr<uint>(0)));
            Assert.Equal(UnknownWorkQueue, Call(Threadmgr, "NID_154F2C2A", 0x7FFF0001, new Ptr<uint>(0)));
        });
    }

    [Fact]
    public void TheTraceHooksAndThePerCpuWords()
    {
        uint globals = (uint)Invoke(Threadmgr, "NID_19440C83")!;
        Assert.Equal(0, Call(Threadmgr, "NID_0AE9C713", 0x81000011u));
        Assert.Equal(0, Call(Threadmgr, "NID_4F2B45DC", 0x81000021u));
        Assert.Equal(0x81000011u, GuestMemory.R32(globals + 0x28AC));
        Assert.Equal(0x81000021u, GuestMemory.R32(globals + 0x28A8));
        uint time = _harness.Alloc(8);
        Assert.Equal(0, Call(Threadmgr, "NID_419E6736", time));
        Assert.Equal(unchecked((int)0x80010058), Call(Threadmgr, "NID_86DAE59B")); // no current thread: not the kernel's permission
        uint words = _harness.Alloc(8);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Threadmgr, "NID_6C1F092F", words));
            Assert.NotEqual(0u, GuestMemory.R32(words));
        });
    }

    /// <summary>A save state keeps the pending alarms; the timer line's schedule is not in it, so the load arms the line
    /// again for the earliest alarm (a fresh session's schedule is empty, as after the disarm here).</summary>
    [Fact]
    public void ALoadedStateArmsTheTimerLineForItsAlarms()
    {
        using var clock = ControlledGuestClock.Stop();
        Invoke(Services, "IntrDispatchByHand");
        uint handler = _harness.Alloc(0x10);
        GuestMemory.W32(handler, 0xE3A00000); // mov r0, #0
        GuestMemory.W32(handler + 4, 0xE12FFF1E); // bx lr
        ulong start = _harness.Emu.Kernel.ProcessTimeUs;
        int uid = Call(Threadmgr, "ksceKernelRegisterTimer", _harness.Str("kload"), 5000u, handler, 0u);
        Assert.True(uid > 0);
        Assert.Equal(start + 5000, Deadline());
        var writer = new SnapshotWriter();
        ((ISnapshotState)Threadmgr).SaveState(writer);
        Invoke(Services, "DisarmThreadmgrTimer");
        Assert.Null(Deadline());
        ((ISnapshotState)Threadmgr).LoadState(new SnapshotReader(writer.Data.ToArray()) { Objects = _harness.Emu.Kernel.Objects });
        Assert.Equal(start + 5000, Deadline());
    }

    private ulong? Deadline() => (((ulong? Deadline, bool Dispatcher))Invoke(Services, "IntrSchedule", TimerLine)!).Deadline;
}
