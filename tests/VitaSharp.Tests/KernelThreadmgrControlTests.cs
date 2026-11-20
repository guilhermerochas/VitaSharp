using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// More SceThreadmgrForKernel/ForDriver entries of FW 3.74 threadmgr (decompiled, study only): the per-thread
/// performance monitor words, a work task's info and the waits for a task and for the global queue, a call on a core,
/// the handler slot areas, a call on a new stack and the wait for a thread to be suspended.
/// </summary>
[Collection("guest")]
public sealed class KernelThreadmgrControlTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020005), IllegalAddr = unchecked((int)0x80020006),
        IllegalContext = unchecked((int)0x80027101), UnknownThreadId = unchecked((int)0x80028021),
        UnknownWorkTask = unchecked((int)0x80028302), IllegalSize = unchecked((int)0x8002000B),
        IllegalStackSize = unchecked((int)0x80028024), IllegalThreadId = unchecked((int)0x80028022),
        Dormant = unchecked((int)0x80028027), WaitTimeout = unchecked((int)0x80028005);

    private readonly HleHarness _harness = new(fixture, "kthrctl");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private GuestThread Idle() => _harness.Emu.Kernel.CreateThread("ctlthread", 0, 0x10000100, 0x4000, 0, 0x70000);

    /// <summary>ARM code: r0 = r0 + 1, r1 = 7, return.</summary>
    private uint AddOne()
    {
        uint code = _harness.Alloc(0x10);
        GuestMemory.W32(code, 0xE2800001); // add r0, r0, #1
        GuestMemory.W32(code + 4, 0xE3A01007); // mov r1, #7
        GuestMemory.W32(code + 8, 0xE12FFF1E); // bx lr
        return code;
    }

    [Fact]
    public void ThePerformanceMonitorWordsAreKeptPerThread()
    {
        GuestThread thread = Idle();
        uint value = _harness.Alloc(4);
        Assert.Equal(IllegalContext, Call(Threadmgr, "NID_7F831213", 0, 1u));
        Assert.Equal(UnknownThreadId, Call(Threadmgr, "NID_7F831213", 0x7FFF0001, 1u));
        Assert.Equal(0, Call(Threadmgr, "NID_7F831213", thread.Uid, 0x80000003u));
        Assert.Equal(0, Call(Threadmgr, "NID_1D2A6815", thread.Uid, 0x1u));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_FFB9CD24", thread.Uid, 6u, 0x11u));
        Assert.Equal(0, Call(Threadmgr, "NID_FFB9CD24", thread.Uid, 2u, 0x11u));
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_7B3368F1", thread.Uid, 6u, 5u));
        Assert.Equal(0, Call(Threadmgr, "NID_7B3368F1", thread.Uid, 2u, 1234u));
        Assert.Equal(0, Call(Threadmgr, "NID_7B3368F1", thread.Uid, 31u, 99u));
        Assert.Equal(IllegalAddr, Call(Threadmgr, "NID_170F69D6", thread.Uid, 2u, 0u));
        Assert.Equal(0, Call(Threadmgr, "NID_170F69D6", thread.Uid, 2u, value));
        Assert.Equal(1234u, GuestMemory.R32(value));
        Assert.Equal(0, Call(Threadmgr, "NID_170F69D6", thread.Uid, 31u, value));
        Assert.Equal(99u, GuestMemory.R32(value));
        // another thread's counters are its own
        GuestThread other = Idle();
        Assert.Equal(0, Call(Threadmgr, "NID_170F69D6", other.Uid, 2u, value));
        Assert.Equal(0u, GuestMemory.R32(value));
        // the process words, from a thread (pid 0: the caller's process)
        Assert.Equal(IllegalContext, Call(Threadmgr, "NID_1AAFA818", 0, 5u));
        _harness.OnGuest(t =>
        {
            Assert.Equal(0, Call(Threadmgr, "NID_1AAFA818", 0, 0x11u));
            Assert.Equal(0, Call(Threadmgr, "NID_5053B005", 0, 0x1u));
            Assert.Equal(0x11u, (uint)Invoke(Threadmgr, "ProcessPmu", t.Pid, true)!);
            Assert.Equal(0x1u, (uint)Invoke(Threadmgr, "ProcessPmu", t.Pid, false)!);
        });
    }

    [Fact]
    public void AWorkTaskHasItsInfoAndItsWaitReturnsWhenItRan()
    {
        uint task = AddOne();
        uint info = _harness.Alloc(0x40);
        _harness.OnGuest(_ =>
        {
            int uid = Call(Threadmgr, "ksceKernelEnqueueWorkQueue", 0x10023, "kctl-task", task, 5u);
            Assert.True(uid > 0);
            GuestMemory.W32(info, 0x34);
            int result = Call(Threadmgr, "NID_0B604A3C", uid, info);
            if (result == 0)
            {
                Assert.Equal((uint)uid, GuestMemory.R32(info + 4));
                Assert.Equal("kctl-task", GuestMemory.ReadCString(info + 8));
                Assert.Equal(task, GuestMemory.R32(info + 0x28));
                Assert.Equal(5u, GuestMemory.R32(info + 0x2C));
                Assert.Equal(0x10023u, GuestMemory.R32(info + 0x30));
            }
            else
            {
                Assert.Equal(UnknownWorkTask, result); // already ran
            }

            Assert.Equal(0, Call(Threadmgr, "NID_12FC0FAB", uid, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Threadmgr, "NID_154F2C2A", 0x10023, new Ptr<uint>(0)));
            Assert.Equal(UnknownWorkTask, Call(Threadmgr, "NID_12FC0FAB", 0x7FFF0001, new Ptr<uint>(0)));
            GuestMemory.W32(info, 0x35);
            Assert.Equal(IllegalSize, Call(Threadmgr, "NID_0B604A3C", uid, info));
        });
    }

    [Fact]
    public void ACallOnACoreAndOnANewStackRunTheFunction()
    {
        uint code = AddOne();
        Assert.Equal(InvalidArgument, Call(Threadmgr, "NID_0788C9BC", 4u, code, 1u));
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(Threadmgr, "NID_DF59DF99", 1u, code, 1u));
            Assert.Equal(IllegalStackSize, (int)(long)Invoke(Threadmgr, "NID_A2C801A5", 0x100u, 1u, code)!);
            long result = (long)Invoke(Threadmgr, "NID_4BAA5F3B", 0x1000u, 41u, code)!;
            Assert.Equal(42, (int)result);
            Assert.Equal(7, (int)(result >> 32));
        });
    }

    [Fact]
    public void AHandlerSlotAreaIsMadeOncePerThread()
    {
        uint none = (uint)Invoke(Threadmgr, "NID_3A72C6D8", 2)!;
        Assert.Equal(0u, none); // no current thread
        _harness.OnGuest(_ =>
        {
            int slot = Call(Threadmgr, "NID_B645C7EF", 0x40u, 0u, 0u);
            Assert.InRange(slot, 2, 7);
            Assert.Equal(0u, (uint)Invoke(Threadmgr, "NID_89CA5698", slot, 0, 0)!);
            uint area = (uint)Invoke(Threadmgr, "NID_3A72C6D8", slot)!;
            Assert.NotEqual(0u, area);
            Assert.Equal(area, (uint)Invoke(Threadmgr, "NID_89CA5698", slot, 0, 1)!);
            Assert.Equal(0u, (uint)Invoke(Threadmgr, "NID_89CA5698", 8, 0, 1)!);
        });
    }

    [Fact]
    public void TheWaitForASuspendedThreadSeesTheStop()
    {
        GuestThread dormant = Idle();
        _harness.OnGuest(t =>
        {
            Assert.Equal(IllegalThreadId, Call(Threadmgr, "NID_392599F1", t.Uid, new Ptr<uint>(0)));
            Assert.Equal(Dormant, Call(Threadmgr, "NID_392599F1", dormant.Uid, new Ptr<uint>(0)));
        });
        ThreadMgr user = _harness.Module<ThreadMgr>();
        int sema = Call(user, "sceKernelCreateSema", "susp", 0u, 0, 1, 0u);
        GuestRun waiter = _harness.Start(_ => Call(user, "sceKernelWaitSema", sema, 1, new Ptr<uint>(0)), "susp-waiter");
        Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the thread waits");
        uint timeout = _harness.Word(300);
        _harness.OnGuest(_ => Assert.Equal(WaitTimeout, Call(Threadmgr, "NID_392599F1", waiter.Thread.Uid, new Ptr<uint>(timeout))));
        Assert.Equal(0, Call(Threadmgr, "NID_15D6357C", waiter.Thread.Uid, 0x40));
        _harness.OnGuest(_ => Assert.Equal(0, Call(Threadmgr, "NID_614BF2BE", waiter.Thread.Uid, new Ptr<uint>(0))));
        Assert.Equal(0, Call(Threadmgr, "NID_490DAF14", waiter.Thread.Uid));
        Assert.Equal(0, Call(user, "sceKernelSignalSema", sema, 1));
        _harness.Join(waiter);
    }
}
