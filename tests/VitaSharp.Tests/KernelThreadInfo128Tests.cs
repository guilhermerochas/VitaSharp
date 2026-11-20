using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The kernel's 0x128-byte thread info of FW 3.74 threadmgr (decompiled, study only), its lists, the waiting-thread
/// list of an object, the fast mutexes by UID, and the scheduler stops (0x810075fc and family) on VitaSharp's
/// scheduler. The threads that are stopped never ran, so nothing depends on when a running thread reaches a block.
/// </summary>
[Collection("guest")]
public sealed class KernelThreadInfo128Tests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalContext = unchecked((int)0x80027101), IllegalSize = unchecked((int)0x8002000B),
        IllegalThreadId = unchecked((int)0x80028022), UnknownThreadId = unchecked((int)0x80028021),
        IllegalPriority = unchecked((int)0x80028023), NotSuspended = unchecked((int)0x8002802E),
        IllegalAffinity = unchecked((int)0x80028025), InvalidArgument = unchecked((int)0x80020005),
        NotDormant = unchecked((int)0x80028028);

    private readonly HleHarness _harness = new(fixture, "kthrinfo");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private GuestThread Idle(string name = "infothread") => _harness.Emu.Kernel.CreateThread(name, 0x81000001, 0x10000100, 0x4000, 0x80000, 0x70000);

    private uint Info(uint size = 0x128)
    {
        uint info = _harness.Alloc(0x130);
        GuestMemory.W32(info, size);
        return info;
    }

    [Fact]
    public void TheInfoHasTheThreadsFieldsAtTheFirmwareOffsets()
    {
        GuestThread thread = Idle();
        uint info = Info();
        Assert.Equal(0, Call(Threadmgr, "NID_C976719E", thread.Uid, 0u, info));
        Assert.Equal(0x128u, GuestMemory.R32(info));
        Assert.Equal((uint)thread.Uid, GuestMemory.R32(info + 4));
        Assert.Equal((uint)thread.Pid, GuestMemory.R32(info + 8));
        Assert.Equal("infothread", GuestMemory.ReadCString(info + 0xC));
        Assert.Equal(thread.Attr, GuestMemory.R32(info + 0x30));
        Assert.Equal((uint)ThreadStatus.Dormant, GuestMemory.R32(info + 0x34));
        Assert.Equal(0x81000001u, GuestMemory.R32(info + 0x38));
        Assert.Equal(thread.StackAddr, GuestMemory.R32(info + 0x4C));
        Assert.Equal(thread.StackSize, GuestMemory.R32(info + 0x50));
        Assert.Equal((uint)thread.Priority, GuestMemory.R32(info + 0x7C));
        Assert.Equal((uint)thread.Affinity, GuestMemory.R32(info + 0x84));
        // the control block: the object's words at the firmware's offsets
        uint tcb = GuestMemory.R32(info + 0x100);
        Assert.NotEqual(0u, tcb);
        Assert.Equal((uint)thread.Uid, GuestMemory.R32(tcb + 0x08));
        Assert.Equal((uint)thread.Pid, GuestMemory.R32(tcb - 0x28 + 0x60));
        Assert.Equal((uint)thread.Priority, GuestMemory.R32(tcb - 0x28 + 0xE0));
        // the same block every time
        Assert.Equal(0, Call(Threadmgr, "NID_555CA6BB", thread.Uid, 0u, info));
        Assert.Equal(tcb, GuestMemory.R32(info + 0x100));
    }

    [Fact]
    public void TheInfoCallsCheckTheirArguments()
    {
        GuestThread thread = Idle();
        Assert.Equal(IllegalSize, Call(Threadmgr, "NID_555CA6BB", thread.Uid, 0u, Info(0x129)));
        Assert.Equal(IllegalContext, Call(Threadmgr, "NID_555CA6BB", 0, 0u, Info()));
        Assert.Equal(UnknownThreadId, Call(Threadmgr, "ksceKernelGetThreadInfoForDebugger", 0x7FFF0001, 0u, Info()));
        // a smaller size copies that much
        uint small = Info(8);
        GuestMemory.W32(small + 8, 0xDEADBEEF);
        Assert.Equal(0, Call(Threadmgr, "NID_BFF892B4", thread.Uid, 0u, small));
        Assert.Equal((uint)thread.Uid, GuestMemory.R32(small + 4));
        Assert.Equal(0xDEADBEEFu, GuestMemory.R32(small + 8));
        _harness.OnGuest(t =>
        {
            uint own = Info();
            Assert.Equal(0, Call(Threadmgr, "NID_555CA6BB", 0, 0u, own));
            Assert.Equal((uint)t.Uid, GuestMemory.R32(own + 4));
            Assert.Equal((uint)ThreadStatus.Running, GuestMemory.R32(own + 0x34));
        });
    }

    [Fact]
    public void TheListsCountEveryThreadAndCopyWhatFits()
    {
        GuestThread a = Idle("lista");
        GuestThread b = Idle("listb");
        uint count = _harness.Alloc(4);
        int all = Call(Threadmgr, "NID_D7455187", 0u, 0u, 0u, count);
        Assert.True(all >= 2);
        uint buffer = _harness.Alloc(0x128 * 64);
        Assert.Equal(all, Call(Threadmgr, "NID_B011EA69", 0u, buffer, 0x128u * 64, count));
        Assert.Equal((uint)all, GuestMemory.R32(count));
        // a buffer of one entry: the first copied, every thread counted
        Assert.Equal(all, Call(Threadmgr, "NID_D7455187", 0u, buffer, 0x128u, count));
        Assert.Equal(1u, GuestMemory.R32(count));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_D7455187", 0u, 0u, 0x128u, count));
        // the process's threads, and their names
        int ofProcess = Call(Threadmgr, "NID_F2C9DC97", a.Pid, 0u, buffer, 0x128u * 64, count);
        Assert.Equal(ofProcess, (int)GuestMemory.R32(count));
        uint names = _harness.Alloc(0x24 * 64);
        Assert.Equal(ofProcess, Call(Threadmgr, "NID_FD87586C", a.Pid, names, 0x24u * 64, count));
        var listed = new List<string>();
        for (uint i = 0; i < GuestMemory.R32(count); i++)
        {
            listed.Add(GuestMemory.ReadCString(names + i * 0x24 + 4));
        }

        Assert.Contains("lista", listed);
        Assert.Contains("listb", listed);
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_A4D270F9", a.Pid, 0u, 0x24u, count));
        Assert.True(b.Uid > 0);
    }

    [Fact]
    public void TheWaitingThreadsOfAnObjectAreListed()
    {
        ThreadMgr user = _harness.Module<ThreadMgr>();
        int sema = Call(user, "sceKernelCreateSema", "w", 0u, 0, 1, 0u);
        GuestRun waiter = _harness.Start(_ => Call(user, "sceKernelWaitSema", sema, 1, new Ptr<uint>(0)), "waiter");
        Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the waiter waits");
        uint list = _harness.Alloc(0x40);
        uint count = _harness.Alloc(4);
        Assert.Equal(1, Call(Threadmgr, "NID_B5C782A3", sema, list, 0x40u, count));
        Assert.Equal(1u, GuestMemory.R32(count));
        Assert.Equal((uint)waiter.Thread.Uid, GuestMemory.R32(list));
        Assert.Equal(1, Call(Threadmgr, "NID_54E1AF04", waiter.Thread.Pid, sema));
        uint info = Info();
        Assert.Equal(0, Call(Threadmgr, "NID_555CA6BB", waiter.Thread.Uid, 0u, info));
        Assert.Equal(0x20u, GuestMemory.R32(info + 0x94)); // SCE_KERNEL_WAITTYPE_SEMAPHORE
        Assert.Equal((uint)sema, GuestMemory.R32(info + 0x98));
        Assert.Equal(0, Call(user, "sceKernelSignalSema", sema, 1));
        _harness.Join(waiter);
        Assert.Equal(0, Call(Threadmgr, "NID_B5C782A3", sema, list, 0x40u, count));
    }

    [Fact]
    public void AFastMutexIsFoundByItsUid()
    {
        uint work = _harness.Alloc(0x40);
        uint info = _harness.Alloc(0x44);
        uint count = _harness.Alloc(4);
        _harness.OnGuest(_ => Assert.Equal(0, Call(Threadmgr, "ksceKernelInitializeFastMutex", work, "kfast", 0u, 0u)));
        int uid = (int)GuestMemory.R32(work + 8);
        GuestMemory.W32(info, 0x44);
        Assert.Equal(0, Call(Threadmgr, "NID_AF302193", uid, info));
        Assert.Equal((uint)uid, GuestMemory.R32(info + 4));
        Assert.Equal(work, GuestMemory.R32(info + 0x2C));
        Assert.Equal(unchecked((int)0x80028161), Call(Threadmgr, "NID_AF302193", 0x7FFF0001, info));
        uint buffer = _harness.Alloc(0x44 * 8);
        Assert.True(Call(Threadmgr, "NID_6D4C1EB7", 0u, buffer, 0x44u * 8, count) >= 1);
    }

    [Fact]
    public void AStoppedThreadRunsAgainWithItsPriority()
    {
        GuestThread thread = Idle();
        Assert.Equal(IllegalThreadId, Call(Threadmgr, "NID_15D6357C", 0, 0x40));
        Assert.Equal(IllegalPriority, Call(Threadmgr, "NID_15D6357C", thread.Uid, 0));
        Assert.Equal(IllegalPriority, Call(Threadmgr, "NID_800ACB9D", thread.Uid, 0xFF));
        Assert.Equal(UnknownThreadId, Call(Threadmgr, "NID_9928DFBA", 0x7FFF0001, 0x40));
        Assert.Equal(unchecked((int)0x80028027), Call(Threadmgr, "NID_490DAF14", thread.Uid)); // dormant comes first

        Assert.Equal(0, Call(Threadmgr, "NID_15D6357C", thread.Uid, 0x40));
        Assert.Equal(1, thread.SuspendCount);
        Assert.Equal(0x40, thread.Priority);
        Assert.Equal(0, Call(Threadmgr, "NID_9928DFBA", thread.Uid, 0x50));
        Assert.Equal(1, thread.SuspendCount);
        Assert.Equal(0x50, thread.Priority);
        _harness.OnGuest(t => Assert.Equal(IllegalThreadId, Call(Threadmgr, "NID_490DAF14", t.Uid)));
        Assert.Equal(unchecked((int)0x80028027), Call(Threadmgr, "NID_490DAF14", thread.Uid)); // dormant
        Assert.Equal(1, thread.SuspendCount);
    }

    [Fact]
    public void AStartedThreadStoppedByTheSchedulerResumes()
    {
        ThreadMgr user = _harness.Module<ThreadMgr>();
        int sema = Call(user, "sceKernelCreateSema", "stop", 0u, 0, 1, 0u);
        GuestRun waiter = _harness.Start(_ => Call(user, "sceKernelWaitSema", sema, 1, new Ptr<uint>(0)), "stopped");
        Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the thread waits");
        int priority = waiter.Thread.Priority;
        Assert.Equal(0, Call(Threadmgr, "NID_800ACB9D", waiter.Thread.Uid, 0x30));
        Assert.Equal(1, waiter.Thread.SuspendCount);
        Assert.Equal(0, Call(Threadmgr, "NID_490DAF14", waiter.Thread.Uid));
        Assert.Equal(0, waiter.Thread.SuspendCount);
        Assert.Equal(priority, waiter.Thread.Priority);
        Assert.Equal(NotSuspended, Call(Threadmgr, "NID_490DAF14", waiter.Thread.Uid));
        Assert.Equal(NotDormant, Call(Threadmgr, "NID_70131EA5", waiter.Thread.Uid, _harness.Alloc(4)));
        Assert.Equal(0, Call(user, "sceKernelSignalSema", sema, 1));
        _harness.Join(waiter);
    }

    [Fact]
    public void PriorityAndAffinityChangeTogether()
    {
        GuestThread thread = Idle();
        Assert.Equal(IllegalAffinity, Call(Threadmgr, "NID_3857C94E", thread.Uid, 0x40, 0x10u));
        Assert.Equal(IllegalAffinity, Call(Threadmgr, "NID_3857C94E", thread.Uid, 0x40, 0x10001u));
        Assert.Equal(IllegalPriority, Call(Threadmgr, "NID_3857C94E", thread.Uid, 0x10000200, 0x1u));
        Assert.Equal(0, Call(Threadmgr, "NID_3857C94E", thread.Uid, 0x48, 0x2u));
        Assert.Equal(0x48, thread.Priority);
        Assert.Equal(2, thread.Affinity);
        Assert.Equal(0, Call(Threadmgr, "NID_3857C94E", thread.Uid, 0x49, 0x40000u));
        Assert.Equal(4, thread.Affinity);
    }

    [Fact]
    public void TheExitStatusAndTheAttributes()
    {
        uint status = _harness.Alloc(4);
        uint attr = _harness.Alloc(4);
        GuestRun ended = _harness.Start(_ => { }, "ender");
        _harness.Join(ended);
        Until(() => ended.Thread.Status == ThreadStatus.Dormant, "the thread ended");
        Assert.Equal(0, Call(Threadmgr, "NID_70131EA5", ended.Thread.Uid, status));
        Assert.Equal((uint)ended.Thread.ExitStatus, GuestMemory.R32(status));
        Assert.Equal(IllegalThreadId, Call(Threadmgr, "NID_70131EA5", 0, status));
        _harness.OnGuest(t =>
        {
            Assert.Equal(0, Call(Threadmgr, "NID_AB977C72", attr));
            Assert.Equal(t.Attr, GuestMemory.R32(attr));
        });
        Assert.Equal(IllegalContext, Call(Threadmgr, "NID_AB977C72", attr));
    }
}
