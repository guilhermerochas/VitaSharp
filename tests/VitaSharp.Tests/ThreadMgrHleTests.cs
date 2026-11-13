using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Tests;

/// <summary>
/// SceThreadmgr through the HLE entry points (SceLibKernel names and the <c>_sceKernel*</c> syscall forms): threads,
/// semaphores, event flags, mutexes, lightweight mutexes and conds, condition variables, message pipes, rw locks,
/// callbacks, timers and simple events. Error codes and the Info struct layouts are the FW 3.60 ones (psp2/kernel
/// headers, Vita3K); blocking waits run on real guest threads.
/// </summary>
[Collection("guest")]
public sealed class ThreadMgrHleTests(GuestMemoryFixture fx) : IDisposable
{
    private readonly HleHarness _h = new(fx, "threadmgr");
    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private uint Info(uint size)
    {
        uint p = _h.Alloc(Math.Max(size, 0x100));
        GuestMemory.W32(p, size);
        return p;
    }

    // ------------------------------------------------------------------ threads

    [Fact]
    public void ThreadCreateInfoAndDelete()
    {
        _h.OnGuest(self =>
        {
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelCreateThread", "noentry", 0u, 0x10000100, 0x4000, 0u, 0, 0u));
            int uid = Call(Tm, "sceKernelCreateThread", "worker", 0x81000001u, 0x10000100 + 2, 0x2000, 0x80000000u, 0x20000, 0u);
            Assert.True(uid > 0);
            Assert.Equal(1, Call(Tm, "sceKernelGetThreadmgrUIDClass", uid));

            uint info = Info(0x80);
            Assert.Equal(0, Call(Tm, "sceKernelGetThreadInfo", uid, info));
            Assert.Equal(0x80u, GuestMemory.R32(info));
            Assert.Equal("worker", GuestMemory.ReadCString(info + 8, 32));
            Assert.Equal(0x80000000u, GuestMemory.R32(info + 0x28));               // attr
            Assert.Equal((uint)ThreadStatus.Dormant, GuestMemory.R32(info + 0x2C)); // never started
            Assert.Equal(0x81000001u, GuestMemory.R32(info + 0x30));               // entry
            Assert.Equal(0x4000u, GuestMemory.R32(info + 0x38));                   // the 16 KiB minimum stack
            Assert.Equal(162u, GuestMemory.R32(info + 0x3C));                      // DEFAULT_PRIORITY + 2 = 160 + 2
            Assert.Equal(162u, GuestMemory.R32(info + 0x40));
            Assert.Equal(0x20000u, GuestMemory.R32(info + 0x44));                  // affinity
            // the size field limits the copy; a larger size than the struct is NO_MEMORY, NULL ILLEGAL_ADDR
            uint small = Info(0x2C);
            GuestMemory.W32(small + 0x28, 0xAAAAAAAA);
            GuestMemory.W32(small + 0x2C, 0xBBBBBBBB);
            Assert.Equal(0, Call(Tm, "sceKernelGetThreadInfo", uid, small));
            Assert.Equal(0x80000000u, GuestMemory.R32(small + 0x28));
            Assert.Equal(0xBBBBBBBBu, GuestMemory.R32(small + 0x2C));
            Assert.Equal(SceError.NO_MEMORY, Call(Tm, "sceKernelGetThreadInfo", uid, Info(0x84)));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelGetThreadInfo", uid, 0u));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelGetThreadInfo", 0x7FFF0001, info));
            // the syscall takes the size from its 8-byte opt
            uint opt = _h.Word(0x30);
            Assert.Equal(0, Call(Tm, "_sceKernelGetThreadInfo", uid, info, opt));
            Assert.Equal(0x30u, GuestMemory.R32(info));

            // a dormant thread: its whole stack is free, its exit status readable, it can be deleted
            Assert.Equal(0x4000, Call(Tm, "sceKernelGetThreadStackFreeSize", uid));
            uint st = _h.Word(0xFFFFFFFF);
            Assert.Equal(0, Call(Tm, "sceKernelGetThreadExitStatus", uid, new Ptr<int>(st)));
            Assert.Equal(0u, GuestMemory.R32(st));
            Assert.Equal(0, Call(Tm, "sceKernelDeleteThread", uid));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelDeleteThread", uid));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelStartThread", uid, 0u, 0u));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelGetThreadStackFreeSize", uid));

            // the running thread is not dormant
            Assert.Equal(SceError.NOT_DORMANT, Call(Tm, "sceKernelDeleteThread", self.Uid));
            Assert.Equal(SceError.NOT_DORMANT, Call(Tm, "sceKernelGetThreadExitStatus", self.Uid, new Ptr<int>(st)));
            Assert.Equal(self.Uid, Call(Tm, "sceKernelGetThreadId"));
            Assert.Equal(1, Call(Tm, "sceKernelGetThreadmgrUIDClass", 0)); // 0 = the calling thread
            Assert.Equal(SceError.INVALID_UID, Call(Tm, "sceKernelGetThreadmgrUIDClass", 0x7FFF0001));
        });
    }

    [Fact]
    public void ThreadPriorityAffinityAndTls()
    {
        _h.OnGuest(self =>
        {
            int uid = Call(Tm, "sceKernelCreateThread", "prio", 0x81000001u, 100, 0x4000, 0u, 0x10000, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelChangeThreadPriority", uid, 120));
            uint info = Info(0x80);
            Call(Tm, "sceKernelGetThreadInfo", uid, info);
            Assert.Equal(100u, GuestMemory.R32(info + 0x3C));
            Assert.Equal(120u, GuestMemory.R32(info + 0x40));
            // 0 restores the initial priority
            Assert.Equal(0, Call(Tm, "sceKernelChangeThreadPriority", uid, 0));
            Call(Tm, "sceKernelGetThreadInfo", uid, info);
            Assert.Equal(100u, GuestMemory.R32(info + 0x40));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelChangeThreadPriority", 0x7FFF0001, 100));

            // affinity: Change returns the previous mask
            Assert.Equal(0x10000, Call(Tm, "sceKernelChangeThreadCpuAffinityMask", uid, 0x40000));
            Assert.Equal(0x40000, Call(Tm, "sceKernelGetThreadCpuAffinityMask", uid));
            Assert.Equal(0x40000, Call(Tm, "_sceKernelGetThreadCpuAffinityMask", uid));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelGetThreadCpuAffinityMask", 0x7FFF0001));

            Assert.Equal(self.Priority, Call(Tm, "sceKernelGetThreadCurrentPriority"));

            // TLS: slot addresses inside the thread's TLS block, 0 out of range or for an unknown thread
            Assert.Equal(self.TlsBase + 4 * 0x10, (uint)Invoke(Tm, "sceKernelGetTLSAddr", 0x10)!);
            Assert.Equal(0u, (uint)Invoke(Tm, "sceKernelGetTLSAddr", 0x100)!);
            Assert.Equal(0u, (uint)Invoke(Tm, "sceKernelGetTLSAddr", -1)!);
            GuestThread other = Emu.Kernel.GetThread(uid)!;
            Assert.Equal(other.TlsBase + 8, (uint)Invoke(Tm, "sceKernelGetThreadTLSAddr", uid, 2)!);
            Assert.Equal(self.TlsBase + 8, (uint)Invoke(Tm, "sceKernelGetThreadTLSAddr", 0, 2)!);
            Assert.Equal(0u, (uint)Invoke(Tm, "sceKernelGetThreadTLSAddr", 0x7FFF0001, 2)!);
            Assert.Equal(0u, (uint)Invoke(Tm, "sceKernelGetThreadTLSAddr", uid, 0x100)!);
            // the kernel TLS words a new thread starts with: its UID and its stack bounds
            Assert.Equal((uint)uid, GuestMemory.R32(other.TlsBase + 4));
            Assert.Equal(other.StackAddr + other.StackSize, GuestMemory.R32(other.TlsBase + 8));
            Assert.Equal(other.StackAddr, GuestMemory.R32(other.TlsBase + 12));
            Assert.Equal(0, Call(Tm, "sceKernelDeleteThread", uid));
        });
    }

    private VitaSharp.Core.Emulator Emu => _h.Emu;

    [Fact]
    public void CreateThreadForUserChecksTheOptions()
    {
        _h.OnGuest(self =>
        {
            uint name = _h.Str("user-thread"), opt = _h.Alloc(0x18);
            GuestMemory.W32(opt, 0x18);
            GuestMemory.W32(opt + 4, 0x8000);
            GuestMemory.W32(opt + 0xC, 0x10000);
            Assert.Equal(unchecked((int)0x80022005), Call(Tm, "sceKernelCreateThreadForUser", 0u, 0x81000001u, 160, opt));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelCreateThreadForUser", name, 0u, 160, opt));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelCreateThreadForUser", name, 0x81000001u, 160, 0u));
            Assert.Equal(SceError.ILLEGAL_PRIORITY, Call(Tm, "sceKernelCreateThreadForUser", name, 0x81000001u, 40, opt));
            Assert.Equal(SceError.ILLEGAL_PRIORITY, Call(Tm, "sceKernelCreateThreadForUser", name, 0x81000001u, 192, opt));
            int uid = Call(Tm, "sceKernelCreateThreadForUser", name, 0x81000001u, 0, opt); // 0: the caller's priority
            Assert.True(uid > 0);
            Assert.Equal(self.Priority, Emu.Kernel.GetThread(uid)!.Priority);
            Assert.Equal(0x8000u, Emu.Kernel.GetThread(uid)!.StackSize);
            GuestMemory.W32(opt + 4, 0);
            Assert.Equal(SceError.ILLEGAL_STACK_SIZE, Call(Tm, "sceKernelCreateThreadForUser", name, 0x81000001u, 160, opt));
            GuestMemory.W32(opt + 4, 0x4000);
            GuestMemory.W32(opt + 0xC, 0x80000); // only the three user cores (0x10000-0x40000)
            Assert.Equal(SceError.ILLEGAL_CPU_AFFINITY_MASK, Call(Tm, "sceKernelCreateThreadForUser", name, 0x81000001u, 160, opt));
            GuestMemory.W32(opt + 0xC, 0);
            GuestMemory.W32(opt + 8, 0x1);
            Assert.Equal(SceError.ILLEGAL_ATTR, Call(Tm, "sceKernelCreateThreadForUser", name, 0x81000001u, 160, opt));
        });
    }

    [Fact]
    public void WaitThreadEndReturnsTheExitStatus()
    {
        int exitCode = 0;
        _h.OnGuest(self =>
        {
            // a worker that ends with sceKernelExitThread(0x1234)
            GuestRun worker = _h.Start(_ => { Thread.Sleep(20); Invoke(Tm, "sceKernelExitThread", 0x1234); }, "ender");
            uint stat = _h.Word(0), timeout = _h.Word(5_000_000);
            exitCode = Call(Tm, "sceKernelWaitThreadEnd", worker.Thread.Uid, new Ptr<int>(stat), new Ptr<uint>(timeout));
            Assert.Equal(0x1234u, GuestMemory.R32(stat));
            Assert.True(GuestMemory.R32(timeout) < 5_000_000);
            // the thread itself cannot be waited for, nor an unknown one
            Assert.Equal(SceError.ILLEGAL_THREAD_ID, Call(Tm, "sceKernelWaitThreadEnd", self.Uid, new Ptr<int>(0), new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelWaitThreadEnd", 0x7FFF0001, new Ptr<int>(0), new Ptr<uint>(0)));
            // a thread that does not end in time: WAIT_TIMEOUT with the remaining time 0
            GuestRun stuck = _h.Start(_ => Thread.Sleep(400), "slow");
            uint t2 = _h.Word(1000);
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "_sceKernelWaitThreadEnd", stuck.Thread.Uid, new Ptr<int>(stat), new Ptr<uint>(t2)));
            Assert.Equal(0u, GuestMemory.R32(t2));
            _h.Join(stuck);
            Assert.Equal(0, Call(Tm, "sceKernelGetThreadExitStatus", worker.Thread.Uid, new Ptr<int>(stat)));
            Assert.Equal(0x1234u, GuestMemory.R32(stat));
            Assert.Equal(0, Call(Tm, "_sceKernelGetThreadExitStatus", worker.Thread.Uid, new Ptr<int>(stat)));
        });
        Assert.Equal(0x1234, exitCode);
    }

    [Fact]
    public void ThreadSignalsAreLatchedOnce()
    {
        _h.OnGuest(self =>
        {
            Assert.Equal(SceError.ILLEGAL_THREAD_ID, Call(Tm, "sceKernelSendSignal", 0));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelSendSignal", 0x7FFF0001));
            Assert.Equal(0, Call(Tm, "sceKernelSendSignal", self.Uid));
            Assert.Equal(SceError.ALREADY_SENT, Call(Tm, "sceKernelSendSignal", self.Uid));
            // a pending signal is consumed at once
            Assert.Equal(0, Call(Tm, "sceKernelWaitSignal", 0u, 0u, new Ptr<uint>(_h.Word(1000))));
            // none pending: times out
            uint t = _h.Word(1000);
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelWaitSignal", 0u, 0u, new Ptr<uint>(t)));
            Assert.Equal(0u, GuestMemory.R32(t));
            // another thread wakes a waiting one
            GuestRun waiter = _h.Start(_ => Assert.Equal(0, Call(Tm, "sceKernelWaitSignal", 0u, 0u, new Ptr<uint>(0))), "sig-wait");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the signal wait");
            Assert.Equal(0, Call(Tm, "sceKernelSendSignal", waiter.Thread.Uid));
            _h.Join(waiter);
        });
    }

    [Fact]
    public void VfpExceptionMaskAndThreadAttrs()
    {
        _h.OnGuest(self =>
        {
            // bits outside IOC|DZC|OFC|UFC|IXC|IDC|QC, or the same bit set and cleared, are invalid
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelChangeThreadVfpException", 0u, 0x100u));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelChangeThreadVfpException", 0x1u, 0x1u));
            Assert.Equal(0, Call(Tm, "sceKernelChangeThreadVfpException", 0u, 0x0800009Fu));
            Assert.Equal(0x0800009F, Call(Tm, "sceKernelGetCurrentThreadVfpException"));
            Assert.Equal(0, Call(Tm, "sceKernelChangeThreadVfpException", 0x08000000u, 0x2u));
            Assert.Equal(0x9F, Call(Tm, "sceKernelGetCurrentThreadVfpException"));

            Assert.Equal(0, Call(Tm, "sceKernelChangeCurrentThreadAttr", 0x01000000u, 0x80000000u));
            Assert.Equal(SceError.ILLEGAL_ATTR, Call(Tm, "sceKernelChangeCurrentThreadAttr", 0u, 0x2u));
            Assert.Equal(Emu.Pid, Call(Tm, "sceKernelGetProcessId"));
            Assert.Equal(0, Call(Tm, "sceKernelCheckWaitableStatus"));
        });
    }

    [Fact]
    public void RunStatusAndSystemInfo()
    {
        _h.OnGuest(self =>
        {
            uint rs = Info(0x34);
            Assert.Equal(0, Call(Tm, "sceKernelGetThreadRunStatus", 0, rs));
            Assert.Equal(0x34u, GuestMemory.R32(rs));
            // the calling thread holds a core: it is among the running ones
            var running = Enumerable.Range(0, 3).Select(i => (int)GuestMemory.R32(rs + 8 + (uint)i * 12)).ToList();
            Assert.Contains(self.Uid, running);
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelGetThreadRunStatus", 0x7FFF0001, rs));
            uint rs2 = _h.Alloc(0x40), opt = _h.Word(0x34);
            Assert.Equal(0, Call(Tm, "_sceKernelGetThreadRunStatus", rs2, opt));
            Assert.Equal(0x34u, GuestMemory.R32(rs2));

            uint si = Info(0x48);
            Assert.Equal(0, Call(Tm, "sceKernelGetSystemInfo", si));
            Assert.Equal(0x48u, GuestMemory.R32(si));
            Assert.Equal(0x7u, GuestMemory.R32(si + 4)); // three user cores active
            Assert.Equal(SceError.NO_MEMORY, Call(Tm, "sceKernelGetSystemInfo", Info(0x4C)));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelGetSystemInfo", 0u));
            Assert.Equal(0, Call(Tm, "_sceKernelGetSystemInfo", si, _h.Word(0x8)));
            Assert.Equal(0x8u, GuestMemory.R32(si));

            // the stack check: free bytes below sp of the calling thread (a host body's sp is the stack top)
            int free = Call(Tm, "sceKernelCheckThreadStack");
            Assert.InRange(free, 0, (int)self.StackSize);
        });
    }

    [Fact]
    public void ThreadEventHandlersRegisterAndUnregister()
    {
        _h.OnGuest(self =>
        {
            int uid = Call(Tm, "sceKernelRegisterThreadEventHandler", "evh", 0, 0xFu, 0x81000001u, 0x55u);
            Assert.True(uid > 0);
            uint info = Info(0x40);
            Assert.Equal(0, Call(Tm, "sceKernelGetThreadEventInfo", uid, info));
            Assert.Equal((uint)self.Uid, GuestMemory.R32(info + 0x30)); // 0 means the calling thread
            Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(info + 0x34));    // a specific thread
            Assert.Equal(0x55u, GuestMemory.R32(info + 0x3C));
            // syscall form: (name, threadMask, eventMask, opt {handler, common})
            uint opt = _h.Alloc(8);
            GuestMemory.W32(opt, 0x81000101);
            GuestMemory.W32(opt + 4, 0x66);
            int uid2 = Call(Tm, "_sceKernelRegisterThreadEventHandler", "evh2", 0x10027, 0x3u, opt);
            Assert.Equal(0, Call(Tm, "sceKernelGetThreadEventInfo", uid2, info));
            Assert.Equal(0x81000101u, GuestMemory.R32(info + 0x38));
            Assert.Equal(0u, GuestMemory.R32(info + 0x34)); // a thread group
            Assert.Equal(0, Call(Tm, "sceKernelUnregisterThreadEventHandler", uid));
            Assert.Equal(SceError.UNKNOWN_THREAD_EVENT_ID, Call(Tm, "sceKernelUnregisterThreadEventHandler", uid));
            Assert.Equal(0, Call(Tm, "sceKernelUnregisterThreadEventHandler", uid2));
        });
    }

    [Fact]
    public void DelaysAndProcessTimeAdvance()
    {
        _h.OnGuest(self =>
        {
            ulong t0 = (ulong)Invoke(Tm, "sceKernelGetProcessTimeWide")!;
            Assert.Equal(0, Call(Tm, "sceKernelDelayThread", 2000u));
            Assert.Equal(0, Call(Tm, "sceKernelDelayThread200", 10u));
            Assert.Equal(0, Call(Tm, "sceKernelDelayThreadCB200", 10u));
            ulong t1 = (ulong)Invoke(Tm, "sceKernelGetProcessTimeWide")!;
            Assert.True(t1 > t0);
            uint low = (uint)Invoke(Tm, "sceKernelGetProcessTimeLow")!;
            Assert.True(low >= (uint)t1);
            uint clk = _h.Alloc(8);
            Assert.Equal(0, Call(Tm, "sceKernelGetProcessTime", new Ptr<ulong>(clk)));
            Assert.True(GuestMemory.Read<ulong>(clk) >= t1);
            Assert.Equal(0, Call(Tm, "sceKernelGetSystemTime", new Ptr<ulong>(clk)));
            Assert.Equal(0, Call(Tm, "_sceKernelGetSystemTime", new Ptr<ulong>(clk)));
            Assert.True((ulong)Invoke(Tm, "sceKernelGetSystemTimeWide")! >= GuestMemory.Read<ulong>(clk));
        });
    }

    /// <summary>On the Vita the process time measured around sceKernelDelayThread(2000) is at least 2 ms. The process
    /// clock (GuestClock.Observed) is monotonic across threads, and another, paced thread leaves it up to ~1 ms ahead of
    /// real time (simulated here with a 1 ms lead before each reading); the delay counts from that reading, or the
    /// measured delay comes out shorter (1839 us seen).</summary>
    [Fact]
    public void ProcessTimeAroundADelayIsAtLeastTheDelay()
    {
        _h.OnGuest(_ =>
        {
            for (int i = 0; i < 20; i++)
            {
                var other = new Thread(() => GuestClock.Observed(GuestClock.Ticks + GuestClock.FromSeconds(0.001)));
                other.Start();
                other.Join();
                ulong t0 = (ulong)Invoke(Tm, "sceKernelGetProcessTimeWide")!;
                Call(Tm, "sceKernelDelayThread", 2000u);
                ulong t1 = (ulong)Invoke(Tm, "sceKernelGetProcessTimeWide")!;
                Assert.True(t1 - t0 >= 2000, $"process time advanced {t1 - t0} us over a 2 ms delay");
            }
        });
    }

    // ------------------------------------------------------------------ semaphores

    [Fact]
    public void SemaphoreCountsAndErrors()
    {
        _h.OnGuest(_ =>
        {
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelCreateSema", "bad", 0u, 3, 2, 0u));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelCreateSema", "bad", 0u, -1, 2, 0u));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelCreateSema", "bad", 0u, 0, 0, 0u));
            int s = Call(Tm, "sceKernelCreateSema", "sema", 0x1u, 1, 3, 0u);
            Assert.Equal(2, Call(Tm, "sceKernelGetThreadmgrUIDClass", s));
            Assert.Equal(0, Call(Tm, "sceKernelSignalSema", s, 2));
            Assert.Equal(SceError.SEMA_OVF, Call(Tm, "sceKernelSignalSema", s, 1));
            Assert.Equal(0, Call(Tm, "sceKernelPollSema", s, 3));
            Assert.Equal(SceError.SEMA_ZERO, Call(Tm, "sceKernelPollSema", s, 1));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelWaitSema", s, 4, new Ptr<uint>(0)));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelWaitSema", s, 0, new Ptr<uint>(0)));
            uint t = _h.Word(1000);
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelWaitSema", s, 1, new Ptr<uint>(t)));
            Assert.Equal(0u, GuestMemory.R32(t));

            // SceKernelSemaInfo (0x3C): +0x28 attr, +0x2C initCount, +0x30 currentCount, +0x34 maxCount, +0x38 waiters
            Call(Tm, "sceKernelSignalSema", s, 2);
            uint info = Info(0x3C);
            Assert.Equal(0, Call(Tm, "sceKernelGetSemaInfo", s, info));
            Assert.Equal("sema", GuestMemory.ReadCString(info + 8, 32));
            Assert.Equal(0x1u, GuestMemory.R32(info + 0x28));
            Assert.Equal(1u, GuestMemory.R32(info + 0x2C));
            Assert.Equal(2u, GuestMemory.R32(info + 0x30));
            Assert.Equal(3u, GuestMemory.R32(info + 0x34));
            Assert.Equal(0u, GuestMemory.R32(info + 0x38));
            Assert.Equal(0, Call(Tm, "_sceKernelGetSemaInfo", s, info, _h.Word(0x30)));
            Assert.Equal(0x30u, GuestMemory.R32(info));
            GuestMemory.W32(info, 0x3C); // the syscall form wrote its smaller size there

            // cancel: count reset (-1 = the initial count)
            uint n = _h.Word(0xFF);
            Assert.Equal(0, Call(Tm, "sceKernelCancelSema", s, -1, new Ptr<int>(n)));
            Assert.Equal(0u, GuestMemory.R32(n));
            Assert.Equal(0, Call(Tm, "sceKernelPollSema", s, 1));
            Assert.Equal(SceError.SEMA_ZERO, Call(Tm, "sceKernelPollSema", s, 1));
            Assert.Equal(0, Call(Tm, "_sceKernelCancelSema", s, 3, new Ptr<int>(n)));
            Assert.Equal(0, Call(Tm, "sceKernelPollSema", s, 3));

            Assert.Equal(0, Call(Tm, "sceKernelDeleteSema", s));
            Assert.Equal(SceError.UNKNOWN_SEMA_ID, Call(Tm, "sceKernelDeleteSema", s));
            Assert.Equal(SceError.UNKNOWN_SEMA_ID, Call(Tm, "sceKernelSignalSema", s, 1));
            Assert.Equal(SceError.UNKNOWN_SEMA_ID, Call(Tm, "sceKernelPollSema", s, 1));
            Assert.Equal(SceError.UNKNOWN_SEMA_ID, Call(Tm, "sceKernelWaitSema", s, 1, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_SEMA_ID, Call(Tm, "sceKernelGetSemaInfo", s, info));
            Assert.Equal(SceError.UNKNOWN_SEMA_ID, Call(Tm, "sceKernelCancelSema", s, 0, new Ptr<int>(0)));

            // syscall create: (name, attr, init, opt {max, option}); opt NULL is ILLEGAL_ADDR
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "_sceKernelCreateSema", "sys", 0u, 0, 0u));
            uint opt = _h.Alloc(8);
            GuestMemory.W32(opt, 5);
            int s2 = Call(Tm, "_sceKernelCreateSema", "sys", 0u, 5, opt);
            Assert.Equal(0, Call(Tm, "_sceKernelWaitSema", s2, 5, new Ptr<uint>(0)));
            int s3 = Call(Tm, "sceKernelCreateSema_16XX", "old", 0u, 0, 1, 0u);
            Assert.True(s3 > 0);
        });
    }

    [Fact]
    public void SemaphoreWakesWaitersAndReportsDeleteAndCancel()
    {
        _h.OnGuest(_ =>
        {
            int s = Call(Tm, "sceKernelCreateSema", "wake", 0u, 0, 4, 0u);
            int got = 1;
            GuestRun w = _h.Start(_ => got = Call(Tm, "sceKernelWaitSema", s, 2, new Ptr<uint>(0)), "sema-wait");
            Until(() => w.Thread.Status == ThreadStatus.Waiting, "the semaphore wait");
            uint info = Info(0x3C);
            Call(Tm, "sceKernelGetSemaInfo", s, info);
            Assert.Equal(1u, GuestMemory.R32(info + 0x38));
            Call(Tm, "sceKernelSignalSema", s, 1); // not enough yet
            Thread.Sleep(20);
            Assert.Equal(ThreadStatus.Waiting, w.Thread.Status);
            Call(Tm, "sceKernelSignalSema", s, 1);
            _h.Join(w);
            Assert.Equal(0, got);
            Call(Tm, "sceKernelGetSemaInfo", s, info);
            Assert.Equal(0u, GuestMemory.R32(info + 0x30));

            // cancel: the waiter ends with WAIT_CANCEL and the count is the cancel's
            GuestRun c = _h.Start(_ => got = Call(Tm, "sceKernelWaitSema", s, 1, new Ptr<uint>(0)), "sema-cancel");
            Until(() => c.Thread.Status == ThreadStatus.Waiting, "the semaphore wait");
            uint n = _h.Word(0);
            Assert.Equal(0, Call(Tm, "sceKernelCancelSema", s, 0, new Ptr<int>(n)));
            _h.Join(c);
            Assert.Equal(SceError.WAIT_CANCEL, got);
            Assert.Equal(1u, GuestMemory.R32(n));

            // delete: WAIT_DELETE
            GuestRun d = _h.Start(_ => got = Call(Tm, "sceKernelWaitSema", s, 1, new Ptr<uint>(0)), "sema-delete");
            Until(() => d.Thread.Status == ThreadStatus.Waiting, "the semaphore wait");
            Assert.Equal(0, Call(Tm, "sceKernelDeleteSema", s));
            _h.Join(d);
            Assert.Equal(SceError.WAIT_DELETE, got);
        });
    }

    [Fact]
    public void OpenAndCloseByName()
    {
        _h.OnGuest(_ =>
        {
            int s = Call(Tm, "sceKernelCreateSema", "named-sema", 0u, 0, 1, 0u);
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelOpenSema", 0u));
            Assert.Equal(SceError.UID_NAME_TOO_LONG, Call(Tm, "sceKernelOpenSema", _h.Str(new string('x', 32))));
            Assert.Equal(SceError.UID_CANNOT_FIND_BY_NAME, Call(Tm, "sceKernelOpenSema", _h.Str("nobody")));
            int opened = Call(Tm, "sceKernelOpenSema", _h.Str("named-sema"));
            Assert.Equal(s, opened);
            // deleted while a handle is open: the object stays until that handle is closed, but cannot be opened again
            Assert.Equal(0, Call(Tm, "sceKernelDeleteSema", s));
            Assert.Equal(SceError.UID_CANNOT_FIND_BY_NAME, Call(Tm, "sceKernelOpenSema", _h.Str("named-sema")));
            Assert.Equal(0, Call(Tm, "sceKernelCloseSema", opened));
            Assert.Equal(SceError.UNKNOWN_SEMA_ID, Call(Tm, "sceKernelCloseSema", opened));

            int e = Call(Tm, "sceKernelCreateEventFlag", "named-evf", 0u, 0u, 0u);
            Assert.Equal(e, Call(Tm, "sceKernelOpenEventFlag", _h.Str("named-evf")));
            Assert.Equal(0, Call(Tm, "sceKernelCloseEventFlag", e));
            Assert.Equal(SceError.UNKNOWN_EVF_ID, Call(Tm, "sceKernelCloseEventFlag", 0x7FFF0001));

            int m = Call(Tm, "sceKernelCreateMutex", "named-mtx", 0u, 0, 0u);
            Assert.Equal(m, Call(Tm, "sceKernelOpenMutex", _h.Str("named-mtx")));
            Assert.Equal(m, Call(Tm, "sceKernelOpenMutex_089", _h.Str("named-mtx")));
            Assert.Equal(0, Call(Tm, "sceKernelCloseMutex", m));
            Assert.Equal(0, Call(Tm, "sceKernelCloseMutex_089", m));
            // a lightweight mutex is not a mutex for Open/Close
            uint work = _h.Alloc(32);
            Assert.Equal(0, Call(Tm, "sceKernelCreateLwMutex", work, "named-lw", 0u, 0, 0u));
            Assert.Equal(SceError.UID_CANNOT_FIND_BY_NAME, Call(Tm, "sceKernelOpenMutex", _h.Str("named-lw")));
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Tm, "sceKernelCloseMutex", (int)GuestMemory.R32(work + 16)));

            int c = Call(Tm, "sceKernelCreateCond", "named-cond", 0u, m, 0u);
            Assert.Equal(c, Call(Tm, "sceKernelOpenCond", _h.Str("named-cond")));
            Assert.Equal(0, Call(Tm, "sceKernelCloseCond", c));
            int rw = Call(Tm, "sceKernelCreateRWLock", "named-rw", 0u, 0u);
            Assert.Equal(rw, Call(Tm, "sceKernelOpenRWLock", _h.Str("named-rw")));
            Assert.Equal(0, Call(Tm, "sceKernelCloseRWLock", rw));
            int tm = Call(Tm, "sceKernelCreateTimer", "named-timer", 0u, 0u);
            Assert.Equal(tm, Call(Tm, "sceKernelOpenTimer", _h.Str("named-timer")));
            Assert.Equal(0, Call(Tm, "sceKernelCloseTimer", tm));
            Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, "sceKernelCloseTimer", 0x7FFF0001));
            int mp = Call(Tm, "sceKernelCreateMsgPipe", "named-mpp", 0x40u, 0u, 0x100u, 0u);
            Assert.Equal(mp, Call(Tm, "sceKernelOpenMsgPipe", _h.Str("named-mpp")));
            Assert.Equal(0, Call(Tm, "sceKernelCloseMsgPipe", mp));
            // simple events open only with the OPENABLE attribute
            int ev = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("named-ev"), 0u, 0u, 0u);
            Assert.Equal(SceError.UID_CANNOT_FIND_BY_NAME, Call(Tm, "sceKernelOpenSimpleEvent", _h.Str("named-ev")));
            int ev2 = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("named-ev2"), EventBits.AttrOpenable, 0u, 0u);
            Assert.Equal(ev2, Call(Tm, "sceKernelOpenSimpleEvent", _h.Str("named-ev2")));
            Assert.Equal(0, Call(Tm, "sceKernelCloseSimpleEvent", ev2));
            Assert.True(ev > 0);
        });
    }

    // ------------------------------------------------------------------ event flags

    [Fact]
    public void EventFlagModesAndInfo()
    {
        _h.OnGuest(_ =>
        {
            int e = Call(Tm, "sceKernelCreateEventFlag", "evf", 0u, 0x5u, 0u);
            Assert.Equal(3, Call(Tm, "sceKernelGetThreadmgrUIDClass", e));
            uint outBits = _h.Word(0);
            // AND (0): every bit; OR (1): any bit
            Assert.Equal(SceError.EVF_COND, Call(Tm, "sceKernelPollEventFlag", e, 0x3u, EventFlag.WaitAnd, new Ptr<uint>(outBits)));
            Assert.Equal(0x5u, GuestMemory.R32(outBits)); // the pattern is reported even when it does not match
            Assert.Equal(0, Call(Tm, "sceKernelPollEventFlag", e, 0x3u, EventFlag.WaitOr, new Ptr<uint>(outBits)));
            Assert.Equal(0, Call(Tm, "sceKernelPollEventFlag", e, 0x5u, EventFlag.WaitAnd, new Ptr<uint>(outBits)));
            // CLEAR_PAT (4) clears the bits waited for, CLEAR (2) the whole pattern
            Assert.Equal(0, Call(Tm, "sceKernelPollEventFlag", e, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat, new Ptr<uint>(outBits)));
            Assert.Equal(0x5u, GuestMemory.R32(outBits)); // the pattern before the clear
            Assert.Equal(SceError.EVF_COND, Call(Tm, "sceKernelPollEventFlag", e, 0x1u, EventFlag.WaitOr, new Ptr<uint>(outBits)));
            Assert.Equal(0x4u, GuestMemory.R32(outBits));
            Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", e, 0xF0u));
            Assert.Equal(0, Call(Tm, "_sceKernelPollEventFlag", e, 0x10u, EventFlag.WaitOr | EventFlag.WaitClear, new Ptr<uint>(outBits)));
            Assert.Equal(SceError.EVF_COND, Call(Tm, "sceKernelPollEventFlag", e, 0xFFu, EventFlag.WaitOr, new Ptr<uint>(outBits)));
            Assert.Equal(0u, GuestMemory.R32(outBits));
            // Clear keeps only the given bits
            Call(Tm, "sceKernelSetEventFlag", e, 0xFFu);
            Assert.Equal(0, Call(Tm, "sceKernelClearEventFlag", e, 0x0Fu));
            uint info = Info(0x38);
            Assert.Equal(0, Call(Tm, "sceKernelGetEventFlagInfo", e, info));
            Assert.Equal(0x5u, GuestMemory.R32(info + 0x2C)); // initPattern
            Assert.Equal(0x0Fu, GuestMemory.R32(info + 0x30)); // currentPattern
            Assert.Equal(0u, GuestMemory.R32(info + 0x34));
            Assert.Equal(0, Call(Tm, "_sceKernelGetEventFlagInfo", e, info, _h.Word(0x34)));
            Assert.Equal(0x34u, GuestMemory.R32(info));
            GuestMemory.W32(info, 0x38); // the syscall form wrote its smaller size there
            // a wait for no bits is invalid; one that does not match times out with the pattern written
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelWaitEventFlag", e, 0u, 0u, new Ptr<uint>(0), new Ptr<uint>(0)));
            uint t = _h.Word(1000);
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelWaitEventFlag", e, 0x100u, 0u, new Ptr<uint>(outBits), new Ptr<uint>(t)));
            Assert.Equal(0x0Fu, GuestMemory.R32(outBits));
            // the syscall form takes {outBits*, timeout*} in an opt
            uint opt = _h.Alloc(8);
            GuestMemory.W32(opt, outBits);
            GuestMemory.W32(opt + 4, _h.Word(1000));
            Assert.Equal(0, Call(Tm, "_sceKernelWaitEventFlag", e, 0x3u, EventFlag.WaitAnd, opt));
            Assert.Equal(0, Call(Tm, "sceKernelCancelEventFlag", e, 0x77u, new Ptr<int>(_h.Word(9))));
            Assert.Equal(0, Call(Tm, "_sceKernelCancelEventFlag", e, 0x77u, new Ptr<int>(0)));
            Call(Tm, "sceKernelGetEventFlagInfo", e, Info(0x38));
            Assert.Equal(0, Call(Tm, "sceKernelPollEventFlag", e, 0x77u, EventFlag.WaitAnd, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelDeleteEventFlag", e));
            foreach ((string? fn, object[]? args) in new (string, object[])[]
            {
                ("sceKernelDeleteEventFlag", [e]), ("sceKernelSetEventFlag", [e, 1u]), ("sceKernelClearEventFlag", [e, 1u]),
                ("sceKernelPollEventFlag", [e, 1u, 0u, new Ptr<uint>(0)]), ("sceKernelCancelEventFlag", [e, 0u, new Ptr<int>(0)]),
                ("sceKernelGetEventFlagInfo", [e, info]), ("sceKernelWaitEventFlag", [e, 1u, 0u, new Ptr<uint>(0), new Ptr<uint>(0)]),
            })
            {
                Assert.Equal(SceError.UNKNOWN_EVF_ID, Call(Tm, fn, args));
            }

            int sys = Call(Tm, "_sceKernelCreateEventFlag", "sys-evf", 0u, 1u, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelPollEventFlag", sys, 1u, 0u, new Ptr<uint>(0)));
        });
    }

    [Fact]
    public void EventFlagSingleWaiterUnlessMulti()
    {
        _h.OnGuest(_ =>
        {
            int e = Call(Tm, "sceKernelCreateEventFlag", "single", 0u, 0u, 0u);
            int got = 1;
            uint outBits = _h.Word(0);
            GuestRun w = _h.Start(_ => got = Call(Tm, "sceKernelWaitEventFlag", e, 0x3u, EventFlag.WaitAnd | EventFlag.WaitClearPat, new Ptr<uint>(outBits), new Ptr<uint>(0)), "evf-wait");
            Until(() => w.Thread.Status == ThreadStatus.Waiting, "the event flag wait");
            // a second waiter on a flag without SCE_EVENT_WAITMULTIPLE is refused
            Assert.Equal(SceError.EVF_MULTI, Call(Tm, "sceKernelWaitEventFlag", e, 0x1u, 0u, new Ptr<uint>(0), new Ptr<uint>(_h.Word(1000))));
            uint info = Info(0x38);
            Call(Tm, "sceKernelGetEventFlagInfo", e, info);
            Assert.Equal(1u, GuestMemory.R32(info + 0x34));
            Call(Tm, "sceKernelSetEventFlag", e, 0x1u);
            Thread.Sleep(20);
            Assert.Equal(ThreadStatus.Waiting, w.Thread.Status); // AND: bit 1 still missing
            Call(Tm, "sceKernelSetEventFlag", e, 0x6u);
            _h.Join(w);
            Assert.Equal(0, got);
            Assert.Equal(0x7u, GuestMemory.R32(outBits));
            Assert.Equal(SceError.EVF_COND, Call(Tm, "sceKernelPollEventFlag", e, 0x3u, EventFlag.WaitOr, new Ptr<uint>(outBits)));
            Assert.Equal(0x4u, GuestMemory.R32(outBits)); // the waited bits were cleared

            // with WAITMULTIPLE two threads wait; a cancel wakes both with WAIT_CANCEL
            int m = Call(Tm, "sceKernelCreateEventFlag", "multi", EventFlag.AttrMulti, 0u, 0u);
            int r1 = 1, r2 = 1;
            GuestRun a = _h.Start(_ => r1 = Call(Tm, "sceKernelWaitEventFlag", m, 0x1u, 0u, new Ptr<uint>(0), new Ptr<uint>(0)), "evf-a");
            GuestRun b = _h.Start(_ => r2 = Call(Tm, "sceKernelWaitEventFlagCB", m, 0x1u, 0u, new Ptr<uint>(0), new Ptr<uint>(0)), "evf-b");
            Until(() => a.Thread.Status == ThreadStatus.Waiting && b.Thread.Status == ThreadStatus.Waiting, "both waits");
            uint n = _h.Word(0);
            Assert.Equal(0, Call(Tm, "sceKernelCancelEventFlag", m, 0x8u, new Ptr<int>(n)));
            _h.Join(a);
            _h.Join(b);
            Assert.Equal(2u, GuestMemory.R32(n));
            Assert.Equal(SceError.WAIT_CANCEL, r1);
            Assert.Equal(SceError.WAIT_CANCEL, r2);
            // delete wakes with WAIT_DELETE
            GuestRun d = _h.Start(_ => r1 = Call(Tm, "sceKernelWaitEventFlag", m, 0x1u, 0u, new Ptr<uint>(0), new Ptr<uint>(0)), "evf-d");
            Until(() => d.Thread.Status == ThreadStatus.Waiting, "the wait");
            Call(Tm, "sceKernelDeleteEventFlag", m);
            _h.Join(d);
            Assert.Equal(SceError.WAIT_DELETE, r1);
        });
    }

    /// <summary>
    /// The kernel satisfies a waiter inside sceKernelSetEventFlag and applies its clear mode there (threadmgr walks the
    /// wait queue on every set): the waiter returns that result whenever its thread runs again, even if the pattern
    /// changed in the meantime. Here the flag is cleared before the woken thread can look at it (the flag stays
    /// locked from the set to the clear); a waiter that re-checked the flag on waking found nothing and went back to
    /// sleep for good.
    /// </summary>
    [Fact]
    public void EventFlagSetSatisfiesTheWaiterBeforeItRuns()
    {
        _h.OnGuest(_ =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "granted", 0u, 0u, 0u);
            EventFlag flag = _h.Emu.Kernel.Objects.Get<EventFlag>(uid)!;
            int result = 1;
            uint outBits = _h.Word(0);
            GuestRun waiter = _h.Start(_ => result = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                new Ptr<uint>(outBits), new Ptr<uint>(0)), "evf-granted");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the event flag wait");
            lock (flag)
            {
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x3u));
                Assert.Equal(0x2u, flag.Pattern); // the waiter's clear pattern took bit 0 inside the set
                Assert.Equal(0, Call(Tm, "sceKernelClearEventFlag", uid, 0u));
            }
            _h.Join(waiter, 5_000);
            Assert.Equal(0, result);
            Assert.Equal(0x3u, GuestMemory.R32(outBits)); // the pattern when the set satisfied it
            Assert.Equal(0u, flag.Pattern);
        });
    }

    /// <summary>A cancel releases the waiters at once: a set right after it does not satisfy them (they return
    /// WAIT_CANCEL and the set's bits stay in the pattern).</summary>
    [Fact]
    public void EventFlagSetAfterACancelDoesNotSatisfyTheCancelledWaiter()
    {
        _h.OnGuest(_ =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "cancelled", 0u, 0u, 0u);
            EventFlag flag = _h.Emu.Kernel.Objects.Get<EventFlag>(uid)!;
            int result = 1;
            GuestRun waiter = _h.Start(_ => result = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                new Ptr<uint>(0), new Ptr<uint>(0)), "evf-cancelled");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the event flag wait");
            lock (flag)
            {
                Assert.Equal(0, Call(Tm, "sceKernelCancelEventFlag", uid, 0u, new Ptr<int>(0)));
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x1u));
            }
            _h.Join(waiter, 5_000);
            Assert.Equal(SceError.WAIT_CANCEL, result);
            Assert.Equal(0x1u, flag.Pattern);
        });
    }

    /// <summary>A save state taken while a satisfied waiter has not returned yet keeps the bits its clear mode took: the
    /// wait is reissued when the state loads and takes them again.</summary>
    [Fact]
    public void EventFlagSnapshotKeepsTheBitsOfAPendingGrant()
    {
        _h.OnGuest(_ =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "snapshot", 0u, 0u, 0u);
            EventFlag flag = _h.Emu.Kernel.Objects.Get<EventFlag>(uid)!;
            GuestRun waiter = _h.Start(_ => Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                new Ptr<uint>(0), new Ptr<uint>(0)), "evf-snapshot");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the event flag wait");
            lock (flag)
            {
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x5u));
                Assert.Equal(0x4u, flag.Pattern);
                Assert.Equal(0x5u, flag.SnapshotPattern); // the waiter cannot have returned: the flag is locked
            }
            _h.Join(waiter, 5_000);
            Assert.Equal(0x4u, flag.SnapshotPattern);
        });
    }

    /// <summary>
    /// Metal Gear Solid HD's End Frame protocol with the waker's host thread stopped right after its set (a busy host
    /// preempting it, or CPU pacing sleeping it off): main sets "go" (bit 0) and clears "done" (bit 1) right after, the
    /// End Frame thread waits for "go" with a clear pattern, works and sets "done". On the Vita main's clear comes ~1 us
    /// after its set, long before the woken thread's next call; here the woken thread ran its whole cycle while main's
    /// host thread was stopped, set "done" first, main's clear wiped it and both waited forever. The woken thread now
    /// waits at its system call for its waker to get past the waking call (or block). The sets go through the real
    /// HLE dispatch (as the guest's SVC), and the host preemption is the <see cref="GuestThread.HleReturned"/> seam
    /// stopping main right after its set returns.
    /// </summary>
    [Fact]
    public void WokenThreadDoesNotRunAheadOfAStoppedWaker()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "End Frame Event", 0u, 0u, 0u);
            EventFlag flag = _h.Emu.Kernel.Objects.Get<EventFlag>(uid)!;
            GuestRun endFrame = _h.Start(th =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat, new Ptr<uint>(0), new Ptr<uint>(0)));
                th.Core.ChargeInstructions(200_000); // the frame's work
                Assert.Equal(0u, th.DispatchForTest("sceKernelSetEventFlag", (uint)uid, 0x2u));
            }, "End Frame Thread");
            Until(() => endFrame.Thread.Status == ThreadStatus.Waiting, "the End Frame wait");
            uint patternAfterStop = 0xFFFFFFFF;
            GuestThread.HleReturned = (thread, function) =>
            {
                if (thread == main && function.Name == "sceKernelSetEventFlag")
                {
                    Thread.Sleep(50); // main's host thread does not run (it still holds its emulated core)
                    patternAfterStop = flag.Pattern;
                }
            };
            try
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelSetEventFlag", (uint)uid, 0x1u));
            }
            finally
            {
                GuestThread.HleReturned = null;
            }
            Assert.Equal(0u, patternAfterStop); // "done" not set ahead of main's clear
            Assert.Equal(0, Call(Tm, "sceKernelClearEventFlag", uid, ~0x2u));
            uint timeout = _h.Word(2_000_000);
            Assert.Equal(0, Call(Tm, "sceKernelWaitEventFlag", uid, 0x2u, EventFlag.WaitAnd, new Ptr<uint>(0), new Ptr<uint>(timeout)));
            _h.Join(endFrame, 5_000);
        });
    }

    /// <summary>
    /// The same ordering for every threadmgr object that wakes a waiter: the waker's host thread is stopped right after
    /// the waking call returns (semaphore signal, mutex unlock handing the lock over, event flag set), and the woken
    /// thread's next system call must not run before the waker gets going again; once the waker blocks (a delay here),
    /// the woken thread goes on.
    /// </summary>
    [Theory]
    [InlineData("sema")]
    [InlineData("mutex")]
    [InlineData("evf")]
    public void WokenThreadWaitsForAStoppedWakerOnEveryWakingObject(string kind)
    {
        _h.OnGuest(main =>
        {
            int uid = kind switch
            {
                "sema" => Call(Tm, "sceKernelCreateSema", "woken-sema", 0u, 0, 1, 0u),
                "mutex" => Call(Tm, "sceKernelCreateMutex", "woken-mutex", 0u, 1, 0u), // locked by main
                _ => Call(Tm, "sceKernelCreateEventFlag", "woken-evf", 0u, 0u, 0u),
            };
            string wakingCall = kind switch
            {
                "sema" => "sceKernelSignalSema",
                "mutex" => "sceKernelUnlockMutex",
                _ => "sceKernelSetEventFlag",
            };
            bool nextCallDone = false;
            GuestRun woken = _h.Start(th =>
            {
                int result = kind switch
                {
                    "sema" => Call(Tm, "sceKernelWaitSema", uid, 1, new Ptr<uint>(0)),
                    "mutex" => Call(Tm, "sceKernelLockMutex", uid, 1, new Ptr<uint>(0)),
                    _ => Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd, new Ptr<uint>(0), new Ptr<uint>(0)),
                };
                Assert.Equal(0, result);
                th.Core.ChargeInstructions(200_000);
                th.DispatchForTest("sceKernelGetThreadId");
                Volatile.Write(ref nextCallDone, true);
            }, "woken");
            Until(() => woken.Thread.Status == ThreadStatus.Waiting, "the wait");
            bool doneWhileStopped = true;
            GuestThread.HleReturned = (thread, function) =>
            {
                if (thread == main && function.Name == wakingCall)
                {
                    Thread.Sleep(50);
                    doneWhileStopped = Volatile.Read(ref nextCallDone);
                }
            };
            try
            {
                Assert.Equal(0u, main.DispatchForTest(wakingCall, (uint)uid, 1u));
            }
            finally
            {
                GuestThread.HleReturned = null;
            }
            Assert.False(doneWhileStopped);
            Assert.Equal(0, Call(Tm, "sceKernelDelayThread", 1000u)); // main blocks: the woken thread goes on
            _h.Join(woken, 5_000);
            Assert.True(Volatile.Read(ref nextCallDone));
        });
    }

    /// <summary>
    /// The End Frame protocol when the End Frame thread reaches its wait only after main's set (its host thread was the
    /// late one): the wait is satisfied at once, without blocking, and main's host thread is stopped right after the
    /// set. Taking a ready object is an observation of the signaller just like a wake-up: the End Frame thread's next
    /// call must still not run ahead of main's clear.
    /// </summary>
    [Fact]
    public void ThreadFindingTheObjectReadyDoesNotRunAheadOfAStoppedSignaller()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "End Frame Event", 0u, 0u, 0u);
            EventFlag flag = _h.Emu.Kernel.Objects.Get<EventFlag>(uid)!;
            using var go = new ManualResetEventSlim();
            GuestRun endFrame = _h.Start(th =>
            {
                go.Wait();
                Assert.Equal(0, Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat, new Ptr<uint>(0), new Ptr<uint>(0)));
                th.Core.ChargeInstructions(200_000); // the frame's work
                Assert.Equal(0u, th.DispatchForTest("sceKernelSetEventFlag", (uint)uid, 0x2u));
            }, "End Frame Thread");
            uint patternAfterStop = 0xFFFFFFFF;
            GuestThread.HleReturned = (thread, function) =>
            {
                if (thread == main && function.Name == "sceKernelSetEventFlag")
                {
                    go.Set(); // the End Frame thread gets to its wait now, with "go" already set
                    Thread.Sleep(50);
                    patternAfterStop = flag.Pattern;
                }
            };
            try
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelSetEventFlag", (uint)uid, 0x1u));
            }
            finally
            {
                GuestThread.HleReturned = null;
            }
            Assert.Equal(0u, patternAfterStop); // "go" taken, "done" not set ahead of main's clear
            Assert.Equal(0, Call(Tm, "sceKernelClearEventFlag", uid, ~0x2u));
            uint timeout = _h.Word(2_000_000);
            Assert.Equal(0, Call(Tm, "sceKernelWaitEventFlag", uid, 0x2u, EventFlag.WaitAnd, new Ptr<uint>(0), new Ptr<uint>(timeout)));
            _h.Join(endFrame, 5_000);
        });
    }

    /// <summary>A thread woken by no guest thread (a host-side signal: a timer, a device) has nobody to wait for.</summary>
    [Fact]
    public void ThreadWokenByTheHostDoesNotWait()
    {
        _h.OnGuest(_ =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "host-signal", 0u, 0u, 0u);
            EventFlag flag = _h.Emu.Kernel.Objects.Get<EventFlag>(uid)!;
            long waits = -1;
            GuestRun woken = _h.Start(th =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd, new Ptr<uint>(0), new Ptr<uint>(0)));
                th.Core.ChargeInstructions(200_000);
                long before = GuestThread.WakerWaits;
                th.DispatchForTest("sceKernelGetThreadId");
                waits = GuestThread.WakerWaits - before;
            }, "woken-by-host");
            Until(() => woken.Thread.Status == ThreadStatus.Waiting, "the wait");
            Task.Run(() => flag.Set(0x1)).Wait(); // a host thread, not a guest one
            _h.Join(woken, 5_000);
            Assert.Equal(0, waits);
        });
    }

    /// <summary>A waker that keeps running releases the thread it woke once it is well past the waking call, without
    /// having to block.</summary>
    [Fact]
    public void WokenThreadGoesOnOnceItsWakerIsPastTheWake()
    {
        _h.OnGuest(self =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "go", 0u, 0u, 0u);
            bool released = false;
            GuestRun worker = _h.Start(th =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat, new Ptr<uint>(0), new Ptr<uint>(0)));
                th.Core.ChargeInstructions(50_000);
                th.DispatchForTest("sceKernelGetThreadId"); // its next system call
                Volatile.Write(ref released, true);
            }, "worker");
            Until(() => worker.Thread.Status == ThreadStatus.Waiting, "the worker wait");
            Assert.Equal(0u, self.DispatchForTest("sceKernelSetEventFlag", (uint)uid, 0x1u));
            Thread.Sleep(30);
            Assert.False(Volatile.Read(ref released)); // main has not moved since the set
            self.Core.ChargeInstructions(GuestThread.WakerReleaseCycles); // main keeps running, without blocking
            Until(() => Volatile.Read(ref released), "the worker going on");
            _h.Join(worker, 5_000);
        });
    }

    /// <summary>
    /// A set satisfies the waiters in queue order and stops when the pattern is empty: with two threads waiting for the
    /// same bit with a clear mode, only the first one in the queue gets it (attribute SCE_KERNEL_ATTR_TH_PRIO: the
    /// higher-priority one, whatever the order they started waiting in).
    /// </summary>
    [Fact]
    public void EventFlagSetGrantsTheWaitersInQueueOrder()
    {
        _h.OnGuest(_ =>
        {
            foreach (bool byPriority in new[] { false, true })
            {
                uint attr = EventFlag.AttrMulti | (byPriority ? EventFlag.AttrThreadPriority : 0u);
                int uid = Call(Tm, "sceKernelCreateEventFlag", byPriority ? "prio" : "fifo", attr, 0u, 0u);
                int firstResult = 1;
                int secondResult = 1;
                GuestRun first = _h.Start(_ => firstResult = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                    new Ptr<uint>(0), new Ptr<uint>(0)), "evf-first", 0x10000110);
                Until(() => first.Thread.Status == ThreadStatus.Waiting, "the first wait");
                GuestRun second = _h.Start(_ => secondResult = Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                    new Ptr<uint>(0), new Ptr<uint>(_h.Word(200_000))), "evf-second", 0x10000100);
                Until(() => second.Thread.Status == ThreadStatus.Waiting, "the second wait");
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x1u));
                GuestRun winner = byPriority ? second : first;
                GuestRun loser = byPriority ? first : second;
                _h.Join(winner, 5_000);
                Assert.Equal(0, byPriority ? secondResult : firstResult);
                Assert.Equal(ThreadStatus.Waiting, loser.Thread.Status);
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x1u));
                _h.Join(loser, 5_000);
                Assert.Equal(0, byPriority ? firstResult : secondResult);
            }
        });
    }

    /// <summary>
    /// A woken waiter serves its wake-up latency (<see cref="Wait.SyscallLatencyUs"/>) after leaving the event flag:
    /// served inside, the flag stayed locked for it, and the thread that had just set the flag blocked in its next call
    /// on the same flag. Metal Gear Solid HD's main thread sets "work" and clears "done" right after; blocked there, it
    /// lost its host thread for tens of milliseconds, the End Frame thread finished a whole cycle meanwhile, and the
    /// late clear erased its "done": both threads waited for each other forever.
    /// </summary>
    [Fact]
    public void EventFlagWakeLatencyDoesNotHoldTheFlag()
    {
        int savedLatency = Wait.SyscallLatencyUs;
        Wait.SyscallLatencyUs = 1_000_000;
        try
        {
            _h.OnGuest(_ =>
            {
                int uid = Call(Tm, "sceKernelCreateEventFlag", "latency", 0u, 0x2u, 0u);
                GuestRun waiter = _h.Start(_ => Call(Tm, "sceKernelWaitEventFlag", uid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat,
                    new Ptr<uint>(0), new Ptr<uint>(0)), "evf-latency");
                Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the event flag wait");
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x1u));
                using (VitaSharp.Core.Kernel.Scheduler.Block())
                {
                    Thread.Sleep(150); // the waiter has woken up and is serving its one-second latency
                }

                var clearTime = System.Diagnostics.Stopwatch.StartNew();
                Assert.Equal(0, Call(Tm, "sceKernelClearEventFlag", uid, ~0x2u));
                Assert.True(clearTime.ElapsedMilliseconds < 400, $"sceKernelClearEventFlag waited {clearTime.ElapsedMilliseconds} ms for the woken thread");
                _h.Join(waiter, 5_000);
            });
        }
        finally
        {
            Wait.SyscallLatencyUs = savedLatency;
        }
    }

    // ------------------------------------------------------------------ mutexes

    [Fact]
    public void MutexOwnershipAndCounts()
    {
        _h.OnGuest(self =>
        {
            int m = Call(Tm, "sceKernelCreateMutex", "mtx", 0u, 1, 0u); // created locked once by the caller
            Assert.Equal(4, Call(Tm, "sceKernelGetThreadmgrUIDClass", m));
            uint info = Info(0x3C);
            Assert.Equal(0, Call(Tm, "sceKernelGetMutexInfo", m, info));
            Assert.Equal(1u, GuestMemory.R32(info + 0x2C)); // initCount
            Assert.Equal(1u, GuestMemory.R32(info + 0x30)); // currentCount
            Assert.Equal((uint)self.Uid, GuestMemory.R32(info + 0x34));
            // not recursive: locking again fails
            Assert.Equal(SceError.MUTEX_RECURSIVE, Call(Tm, "sceKernelTryLockMutex", m, 1));
            Assert.Equal(SceError.MUTEX_RECURSIVE, Call(Tm, "sceKernelLockMutex", m, 1, new Ptr<uint>(0)));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelLockMutex", m, 0, new Ptr<uint>(0)));
            Assert.Equal(SceError.MUTEX_UNLOCK_UDF, Call(Tm, "sceKernelUnlockMutex", m, 2));
            // another thread cannot take it or unlock it
            GuestRun other = _h.Start(_ =>
            {
                Assert.Equal(SceError.MUTEX_FAILED_TO_OWN, Call(Tm, "sceKernelTryLockMutex", m, 1));
                Assert.Equal(SceError.MUTEX_NOT_OWNED, Call(Tm, "sceKernelUnlockMutex", m, 1));
                uint t = _h.Word(1000);
                Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelLockMutex", m, 1, new Ptr<uint>(t)));
                Assert.Equal(0u, GuestMemory.R32(t));
            }, "mtx-other");
            _h.Join(other);
            Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", m, 1));
            Call(Tm, "sceKernelGetMutexInfo", m, info);
            Assert.Equal(0u, GuestMemory.R32(info + 0x30));
            Assert.Equal(0u, GuestMemory.R32(info + 0x34));
            Assert.Equal(SceError.MUTEX_NOT_OWNED, Call(Tm, "sceKernelUnlockMutex", m, 1));

            // recursive (attr 2): counts add up
            int r = Call(Tm, "sceKernelCreateMutex", "rec", Mutex.AttrRecursive, 0, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelLockMutex", r, 2, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelTryLockMutex", r, 1));
            Assert.Equal(0, Call(Tm, "_sceKernelLockMutex", r, 1, new Ptr<uint>(0)));
            Call(Tm, "sceKernelGetMutexInfo", r, info);
            Assert.Equal(4u, GuestMemory.R32(info + 0x30));
            Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", r, 4));

            // Info: pointer checked first, then the size, then the uid
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelGetMutexInfo", 0x7FFF0001, 0u));
            Assert.Equal(SceError.NO_MEMORY, Call(Tm, "sceKernelGetMutexInfo", 0x7FFF0001, Info(0x40)));
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Tm, "sceKernelGetMutexInfo", 0x7FFF0001, info));
            Assert.Equal(0, Call(Tm, "_sceKernelGetMutexInfo", m, info, _h.Word(0x30)));
            Assert.Equal(0x30u, GuestMemory.R32(info));
            GuestMemory.W32(info, 0x3C); // the syscall form wrote its smaller size there

            // cancel: a count above 1 needs a recursive mutex; a count hands it to the caller
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelCancelMutex", m, 2, new Ptr<int>(0)));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelCancelMutex", m, -2, new Ptr<int>(0)));
            uint n = _h.Word(0xFF);
            Assert.Equal(0, Call(Tm, "sceKernelCancelMutex", m, 1, new Ptr<int>(n)));
            Assert.Equal(0u, GuestMemory.R32(n));
            Call(Tm, "sceKernelGetMutexInfo", m, info);
            Assert.Equal((uint)self.Uid, GuestMemory.R32(info + 0x34));
            Assert.Equal(0, Call(Tm, "_sceKernelCancelMutex", m, 0, new Ptr<int>(n)));
            Call(Tm, "sceKernelGetMutexInfo", m, info);
            Assert.Equal(0u, GuestMemory.R32(info + 0x34));

            Assert.Equal(0, Call(Tm, "sceKernelDeleteMutex", m));
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Tm, "sceKernelDeleteMutex", m));
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Tm, "sceKernelLockMutex", m, 1, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Tm, "sceKernelTryLockMutex", m, 1));
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Tm, "sceKernelUnlockMutex", m, 1));
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Tm, "sceKernelCancelMutex", m, 0, new Ptr<int>(0)));
            int sys = Call(Tm, "_sceKernelCreateMutex", "sys-mtx", 0u, 0, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelTryLockMutex", sys, 1));
        });
    }

    [Fact]
    public void MutexHandsOverToAWaiter()
    {
        _h.OnGuest(self =>
        {
            int m = Call(Tm, "sceKernelCreateMutex", "handover", 0u, 1, 0u);
            int got = 1;
            GuestRun w = _h.Start(_ => { got = Call(Tm, "sceKernelLockMutexCB", m, 1, new Ptr<uint>(0)); Call(Tm, "sceKernelUnlockMutex", m, 1); }, "mtx-wait");
            Until(() => w.Thread.Status == ThreadStatus.Waiting, "the mutex wait");
            uint info = Info(0x3C);
            Call(Tm, "sceKernelGetMutexInfo", m, info);
            Assert.Equal(1u, GuestMemory.R32(info + 0x38)); // one waiter
            Call(Tm, "sceKernelUnlockMutex", m, 1);
            _h.Join(w);
            Assert.Equal(0, got);
            // a cancel wakes a waiter with WAIT_CANCEL, a delete with WAIT_DELETE
            Call(Tm, "sceKernelLockMutex", m, 1, new Ptr<uint>(0));
            GuestRun c = _h.Start(_ => got = Call(Tm, "sceKernelLockMutex", m, 1, new Ptr<uint>(0)), "mtx-cancel");
            Until(() => c.Thread.Status == ThreadStatus.Waiting, "the mutex wait");
            uint n = _h.Word(0);
            Assert.Equal(0, Call(Tm, "sceKernelCancelMutex", m, 1, new Ptr<int>(n)));
            _h.Join(c);
            Assert.Equal(SceError.WAIT_CANCEL, got);
            Assert.Equal(1u, GuestMemory.R32(n));
            GuestRun d = _h.Start(_ => got = Call(Tm, "sceKernelLockMutex", m, 1, new Ptr<uint>(0)), "mtx-delete");
            Until(() => d.Thread.Status == ThreadStatus.Waiting, "the mutex wait");
            Call(Tm, "sceKernelDeleteMutex", m);
            _h.Join(d);
            Assert.Equal(SceError.WAIT_DELETE, got);
        });
    }

    // ------------------------------------------------------------------ lightweight mutexes and conds

    [Fact]
    public void LwMutexWorkAreaAndInfo()
    {
        _h.OnGuest(self =>
        {
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelCreateLwMutex", 0u, "nowork", 0u, 0, 0u));
            uint work = _h.Alloc(32);
            GuestMemory.Fill(work, 0xEE, 32);
            Assert.Equal(0, Call(Tm, "sceKernelCreateLwMutex", work, "lw", Mutex.AttrRecursive, 1, 0u));
            // SceKernelLwMutexWork: +0 owner UID, +8 count, +12 attr, +16 the mutex UID
            int uid = (int)GuestMemory.R32(work + 16);
            Assert.Equal((uint)self.Uid, GuestMemory.R32(work));
            Assert.Equal(1u, GuestMemory.R32(work + 8));
            Assert.Equal(Mutex.AttrRecursive, GuestMemory.R32(work + 12));
            Assert.Equal(10, Call(Tm, "sceKernelGetThreadmgrUIDClass", uid));
            Assert.Equal(0, Call(Tm, "sceKernelLockLwMutex", work, 2, new Ptr<uint>(0)));
            Assert.Equal(3u, GuestMemory.R32(work + 8));
            // SceKernelLwMutexInfo (0x40): +0x2C work, +0x30 initCount, +0x34 currentCount, +0x38 owner, +0x3C waiters
            uint info = Info(0x40);
            Assert.Equal(0, Call(Tm, "sceKernelGetLwMutexInfo", work, info));
            Assert.Equal((uint)uid, GuestMemory.R32(info + 4));
            Assert.Equal(work, GuestMemory.R32(info + 0x2C));
            Assert.Equal(1u, GuestMemory.R32(info + 0x30));
            Assert.Equal(3u, GuestMemory.R32(info + 0x34));
            Assert.Equal((uint)self.Uid, GuestMemory.R32(info + 0x38));
            Assert.Equal(0, Call(Tm, "sceKernelGetLwMutexInfoById", uid, info));
            Assert.Equal(0, Call(Tm, "_sceKernelGetLwMutexInfoById", uid, info, _h.Word(0x30)));
            Assert.Equal(0x30u, GuestMemory.R32(info));
            GuestMemory.W32(info, 0x40); // the syscall form wrote its smaller size there
            Assert.Equal(SceError.UNKNOWN_LW_MUTEX_ID, Call(Tm, "sceKernelGetLwMutexInfoById", 0x7FFF0001, info));
            Assert.Equal(SceError.LW_MUTEX_UNLOCK_UDF, Call(Tm, "sceKernelUnlockLwMutex", work, 4));
            Assert.Equal(0, Call(Tm, "_sceKernelUnlockLwMutex", work, 3));
            Assert.Equal(0u, GuestMemory.R32(work));     // owner word free
            Assert.Equal(SceError.LW_MUTEX_NOT_OWNED, Call(Tm, "sceKernelUnlockLwMutex", work, 1));

            // non-recursive: second lock is LW_MUTEX_RECURSIVE; another thread fails to own / times out
            uint plain = _h.Alloc(32);
            Assert.Equal(0, Call(Tm, "_sceKernelCreateLwMutex", plain, "lw-plain", 0u, 0, 0u));
            Assert.Equal(0, Call(Tm, "sceKernelTryLockLwMutex", plain, 1));
            Assert.Equal(SceError.LW_MUTEX_RECURSIVE, Call(Tm, "sceKernelTryLockLwMutex_16XX", plain, 1));
            Assert.Equal(SceError.LW_MUTEX_RECURSIVE, Call(Tm, "sceKernelLockLwMutex", plain, 1, new Ptr<uint>(0)));
            GuestRun other = _h.Start(_ =>
            {
                Assert.Equal(SceError.LW_MUTEX_FAILED_TO_OWN, Call(Tm, "sceKernelTryLockLwMutex", plain, 1));
                Assert.Equal(SceError.LW_MUTEX_NOT_OWNED, Call(Tm, "sceKernelUnlockLwMutex", plain, 1));
                Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "_sceKernelLockLwMutex", plain, 1, new Ptr<uint>(_h.Word(1000))));
            }, "lw-other");
            _h.Join(other);
            // the waiter's wait leaves nothing behind: an unlock frees the word completely
            Assert.Equal(0, Call(Tm, "sceKernelUnlockLwMutex", plain, 1));
            Assert.Equal(0u, GuestMemory.R32(plain));

            // syscall create: (work, name, attr, opt {initCount, option})
            uint w3 = _h.Alloc(32), opt = _h.Alloc(8);
            GuestMemory.W32(opt, 1);
            Assert.Equal(0, Call(Tm, "__sceKernelCreateLwMutex", w3, "lw-sys", 0u, opt));
            Assert.Equal(1u, GuestMemory.R32(w3 + 8));

            // delete: the UID word is cleared so the guest fast paths come to the kernel, which no longer knows it
            Assert.Equal(0, Call(Tm, "sceKernelDeleteLwMutex", work));
            Assert.Equal(0u, GuestMemory.R32(work + 16));
            Assert.Equal(SceError.UNKNOWN_LW_MUTEX_ID, Call(Tm, "sceKernelDeleteLwMutex", work));
            Assert.Equal(SceError.UNKNOWN_LW_MUTEX_ID, Call(Tm, "sceKernelLockLwMutex", work, 1, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_LW_MUTEX_ID, Call(Tm, "sceKernelTryLockLwMutex", work, 1));
            Assert.Equal(SceError.UNKNOWN_LW_MUTEX_ID, Call(Tm, "sceKernelGetLwMutexInfo", work, info));
            Assert.Equal(SceError.UNKNOWN_LW_MUTEX_ID, Call(Tm, "sceKernelDeleteLwMutex", 0u));
            Assert.Equal(0, Call(Tm, "_sceKernelDeleteLwMutex", w3));
        });
    }

    [Fact]
    public void LwCondSignalsAWaiterThatGetsItsMutexBack()
    {
        _h.OnGuest(self =>
        {
            uint mtx = _h.Alloc(32), cond = _h.Alloc(16), stray = _h.Alloc(32);
            Assert.Equal(0, Call(Tm, "sceKernelCreateLwMutex", mtx, "lwc-mtx", 0u, 0, 0u));
            Assert.Equal(SceError.UNKNOWN_LW_MUTEX_ID, Call(Tm, "sceKernelCreateLwCond", cond, "lwc", 0u, stray, 0u));
            Assert.Equal(0, Call(Tm, "sceKernelCreateLwCond", cond, "lwc", 0u, mtx, 0u));
            int cuid = (int)GuestMemory.R32(cond);
            // SceKernelLwCondWork: +0 UID, +4 the mutex's UID, +8 queued waiters, +12 its own address
            Assert.Equal(GuestMemory.R32(mtx + 16), GuestMemory.R32(cond + 4));
            Assert.Equal(0u, GuestMemory.R32(cond + 8));
            Assert.Equal(cond, GuestMemory.R32(cond + 12));
            Assert.Equal(11, Call(Tm, "sceKernelGetThreadmgrUIDClass", cuid));
            // waiting without owning the mutex fails
            Assert.Equal(SceError.LW_MUTEX_NOT_OWNED, Call(Tm, "sceKernelWaitLwCond", cond, new Ptr<uint>(0)));
            Assert.Equal(0u, GuestMemory.R32(cond + 8));

            int got = 1;
            uint ownerSeen = 0;
            GuestRun w = _h.Start(t =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelLockLwMutex", mtx, 1, new Ptr<uint>(0)));
                got = Call(Tm, "sceKernelWaitLwCondCB", cond, new Ptr<uint>(0));
                ownerSeen = GuestMemory.R32(mtx) & ~Mutex.Contended;
                Call(Tm, "sceKernelUnlockLwMutex", mtx, 1);
            }, "lwc-wait");
            Until(() => GuestMemory.R32(cond + 8) == 1 && w.Thread.Status == ThreadStatus.Waiting, "the cond wait");
            // the waiter released the mutex while it waits
            Assert.Equal(0u, GuestMemory.R32(mtx) & ~Mutex.Contended);
            uint info = Info(0x38);
            Assert.Equal(0, Call(Tm, "sceKernelGetLwCondInfo", cond, info));
            Assert.Equal(cond, GuestMemory.R32(info + 0x2C));
            Assert.Equal(mtx, GuestMemory.R32(info + 0x30));
            Assert.Equal(1u, GuestMemory.R32(info + 0x34));
            Assert.Equal(0, Call(Tm, "_sceKernelGetLwCondInfo", cond, info));
            Assert.Equal(0, Call(Tm, "_sceKernelGetLwCondInfoById", cuid, info, _h.Word(0x30)));
            GuestMemory.W32(info, 0x38); // the syscall form wrote its smaller size there
            Assert.Equal(SceError.ILLEGAL_THREAD_ID, Call(Tm, "sceKernelSignalLwCondTo", cond, self.Uid)); // not waiting
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelSignalLwCondTo", cond, 0x7FFF0001));
            Assert.Equal(0, Call(Tm, "_sceKernelSignalLwCondTo", cond, w.Thread.Uid));
            _h.Join(w);
            Assert.Equal(0, got);
            Assert.Equal((uint)w.Thread.Uid, ownerSeen); // woke holding the mutex again
            Assert.Equal(0u, GuestMemory.R32(cond + 8));

            // signals with nobody waiting are no-ops; the all/one forms and the syscalls
            Assert.Equal(0, Call(Tm, "sceKernelSignalLwCond", cond));
            Assert.Equal(0, Call(Tm, "sceKernelSignalLwCondAll", cond));
            Assert.Equal(0, Call(Tm, "_sceKernelSignalLwCond", cond));
            Assert.Equal(0, Call(Tm, "_sceKernelSignalLwCondAll", cond));
            // a timed wait times out and still returns holding the mutex
            Call(Tm, "sceKernelLockLwMutex", mtx, 1, new Ptr<uint>(0));
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "_sceKernelWaitLwCond", cond, new Ptr<uint>(_h.Word(1000))));
            Assert.Equal((uint)self.Uid, GuestMemory.R32(mtx) & ~Mutex.Contended);
            Call(Tm, "sceKernelUnlockLwMutex", mtx, 1);

            Assert.Equal(0, Call(Tm, "sceKernelDeleteLwCond", cond));
            Assert.Equal(0u, GuestMemory.R32(cond));
            Assert.Equal(SceError.UNKNOWN_LW_COND_ID, Call(Tm, "sceKernelDeleteLwCond", cond));
            Assert.Equal(SceError.UNKNOWN_LW_COND_ID, Call(Tm, "sceKernelSignalLwCond", cond));
            Assert.Equal(SceError.UNKNOWN_LW_COND_ID, Call(Tm, "sceKernelSignalLwCondAll", cond));
            Assert.Equal(SceError.UNKNOWN_LW_COND_ID, Call(Tm, "sceKernelSignalLwCondTo", cond, self.Uid));
            Assert.Equal(SceError.UNKNOWN_LW_COND_ID, Call(Tm, "sceKernelWaitLwCond", cond, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_LW_COND_ID, Call(Tm, "sceKernelGetLwCondInfo", cond, info));
            Assert.Equal(SceError.UNKNOWN_LW_COND_ID, Call(Tm, "sceKernelGetLwCondInfoById", cuid, info));

            // syscall create: opt {lw mutex work, option}; NULL opt is ILLEGAL_ADDR
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "_sceKernelCreateLwCond", cond, "c2", 0u, 0u));
            uint opt = _h.Alloc(8);
            GuestMemory.W32(opt, mtx);
            Assert.Equal(0, Call(Tm, "_sceKernelCreateLwCond", cond, "c2", 0u, opt));
            Assert.Equal(0, Call(Tm, "_sceKernelDeleteLwCond", cond));
        });
    }

    [Fact]
    public void CondVarSignalAllAndInfo()
    {
        _h.OnGuest(self =>
        {
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Tm, "sceKernelCreateCond", "c", 0u, 0x7FFF0001, 0u));
            int m = Call(Tm, "sceKernelCreateMutex", "cv-mtx", 0u, 0, 0u);
            int c = Call(Tm, "sceKernelCreateCond", "cv", 0u, m, 0u);
            Assert.Equal(5, Call(Tm, "sceKernelGetThreadmgrUIDClass", c));
            Assert.Equal(SceError.MUTEX_NOT_OWNED, Call(Tm, "sceKernelWaitCond", c, new Ptr<uint>(0)));
            int r1 = 1, r2 = 1;
            GuestRun Waiter(string name, Action<int> set) => _h.Start(_ =>
            {
                Call(Tm, "sceKernelLockMutex", m, 1, new Ptr<uint>(0));
                set(Call(Tm, "sceKernelWaitCondCB", c, new Ptr<uint>(0)));
                Call(Tm, "sceKernelUnlockMutex", m, 1);
            }, name);
            GuestRun a = Waiter("cv-a", v => r1 = v);
            GuestRun b = Waiter("cv-b", v => r2 = v);
            uint info = Info(0x34);
            Until(() => { Call(Tm, "sceKernelGetCondInfo", c, info); return GuestMemory.R32(info + 0x30) == 2; }, "two cond waiters");
            Assert.Equal((uint)m, GuestMemory.R32(info + 0x2C));
            Assert.Equal(0, Call(Tm, "_sceKernelGetCondInfo", c, info, _h.Word(0x30)));
            GuestMemory.W32(info, 0x34); // the syscall form wrote its smaller size there
            Assert.Equal(SceError.ILLEGAL_THREAD_ID, Call(Tm, "sceKernelSignalCondTo", c, self.Uid));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelSignalCondTo", c, 0x7FFF0001));
            Assert.Equal(0, Call(Tm, "sceKernelSignalCondTo", c, b.Thread.Uid));
            _h.Join(b);
            Assert.Equal(0, r2);
            Assert.Equal(ThreadStatus.Waiting, a.Thread.Status);
            Assert.Equal(0, Call(Tm, "sceKernelSignalCondAll", c));
            _h.Join(a);
            Assert.Equal(0, r1);
            Assert.Equal(0, Call(Tm, "sceKernelSignalCond", c));
            // timed wait from the syscall form
            Call(Tm, "sceKernelLockMutex", m, 1, new Ptr<uint>(0));
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "_sceKernelWaitCond", c, new Ptr<uint>(_h.Word(1000))));
            Call(Tm, "sceKernelUnlockMutex", m, 1);
            // a delete wakes a waiter with WAIT_DELETE
            GuestRun d = Waiter("cv-d", v => r1 = v);
            Until(() => { Call(Tm, "sceKernelGetCondInfo", c, info); return GuestMemory.R32(info + 0x30) == 1; }, "the cond waiter");
            Assert.Equal(0, Call(Tm, "sceKernelDeleteCond", c));
            _h.Join(d);
            Assert.Equal(SceError.WAIT_DELETE, r1);
            foreach (string? fn in new[] { "sceKernelDeleteCond", "sceKernelSignalCond", "sceKernelSignalCondAll" })
            {
                Assert.Equal(SceError.UNKNOWN_COND_ID, Call(Tm, fn, c));
            }

            Assert.Equal(SceError.UNKNOWN_COND_ID, Call(Tm, "sceKernelSignalCondTo", c, self.Uid));
            Assert.Equal(SceError.UNKNOWN_COND_ID, Call(Tm, "sceKernelWaitCond", c, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_COND_ID, Call(Tm, "sceKernelGetCondInfo", c, info));
            Assert.Equal(SceError.UNKNOWN_COND_ID, Call(Tm, "sceKernelCloseCond", c));
            int c2 = Call(Tm, "_sceKernelCreateCond", "cv2", 0u, m, 0u);
            Assert.True(c2 > 0);
        });
    }

    // ------------------------------------------------------------------ message pipes

    [Fact]
    public void MsgPipeSendReceiveModes()
    {
        _h.OnGuest(self =>
        {
            // threadmgr rounds the buffer up to whole 4 KB pages
            int p = Call(Tm, "sceKernelCreateMsgPipe", "mpp", 0x40u, 0u, 16u, 0u);
            Assert.Equal(7, Call(Tm, "sceKernelGetThreadmgrUIDClass", p));
            Assert.Equal(self.Uid, Call(Tm, "sceKernelGetMsgPipeCreatorId", (uint)p));
            uint src = _h.Alloc(0x1100), dst = _h.Alloc(0x1100), res = _h.Word(0);
            for (uint i = 0; i < 0x1100; i++)
            {
                GuestMemory.Write(src + i, (byte)(i * 7 + 1));
            }
            // a message larger than the buffer is refused in either mode
            Assert.Equal(SceError.ILLEGAL_SIZE, Call(Tm, "sceKernelSendMsgPipe", p, src, 0x1001u, MsgPipe.WaitAsap, new Ptr<uint>(res), new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_SIZE, Call(Tm, "sceKernelTrySendMsgPipe", p, src, 0x1001u, MsgPipe.WaitAll, new Ptr<uint>(res)));
            // FULL (1): all 0xFF4 bytes fit
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", p, src, 0xFF4u, MsgPipe.WaitAll, new Ptr<uint>(res), new Ptr<uint>(0)));
            Assert.Equal(0xFF4u, GuestMemory.R32(res));
            // SceKernelMppInfo: +0x2C bufSize, +0x30 freeSize
            uint info = Info(0x3C);
            Assert.Equal(0, Call(Tm, "sceKernelGetMsgPipeInfo", p, info));
            Assert.Equal(0x1000u, GuestMemory.R32(info + 0x2C));
            Assert.Equal(12u, GuestMemory.R32(info + 0x30));
            Assert.Equal(0, Call(Tm, "_sceKernelGetMsgPipeInfo", p, info, _h.Word(0x34)));
            GuestMemory.W32(info, 0x3C); // the syscall form wrote its smaller size there
            // FULL that does not fit: the Try form sends nothing and fails, the blocking one times out with nothing sent
            Assert.Equal(SceError.MPP_FULL, Call(Tm, "sceKernelTrySendMsgPipe", p, src + 0xFF4, 16u, MsgPipe.WaitAll, new Ptr<uint>(res)));
            Assert.Equal(0u, GuestMemory.R32(res));
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelSendMsgPipe", p, src + 0xFF4, 16u, MsgPipe.WaitAll, new Ptr<uint>(res), new Ptr<uint>(_h.Word(1000))));
            Assert.Equal(0u, GuestMemory.R32(res));
            Assert.Equal(0, Call(Tm, "sceKernelGetMsgPipeInfo", p, info));
            Assert.Equal(12u, GuestMemory.R32(info + 0x30));
            // ASAP (0): moves what fits
            Assert.Equal(0, Call(Tm, "sceKernelTrySendMsgPipe", p, src + 0xFF4, 16u, MsgPipe.WaitAsap, new Ptr<uint>(res)));
            Assert.Equal(12u, GuestMemory.R32(res));
            Assert.Equal(SceError.MPP_FULL, Call(Tm, "sceKernelTrySendMsgPipe", p, src, 1u, MsgPipe.WaitAsap, new Ptr<uint>(res)));
            Assert.Equal(0u, GuestMemory.R32(res));
            // a full pipe with a timed send: WAIT_TIMEOUT, nothing sent
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelSendMsgPipe", p, src, 1u, 0u, new Ptr<uint>(res), new Ptr<uint>(_h.Word(1000))));
            // receive ASAP takes what is there, in order
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipe", p, dst, 10u, MsgPipe.WaitAsap, new Ptr<uint>(res), new Ptr<uint>(0)));
            Assert.Equal(10u, GuestMemory.R32(res));
            Assert.True(GuestMemory.ReadSpan(dst, 10).SequenceEqual(GuestMemory.ReadSpan(src, 10)));
            Assert.Equal(0, Call(Tm, "sceKernelTryReceiveMsgPipe", p, dst, 0x1100u, MsgPipe.WaitAsap, new Ptr<uint>(res)));
            Assert.Equal(0xFF6u, GuestMemory.R32(res));
            Assert.True(GuestMemory.ReadSpan(dst, 0xFF6).SequenceEqual(GuestMemory.ReadSpan(src + 10, 0xFF6)));
            Assert.Equal(SceError.MPP_EMPTY, Call(Tm, "sceKernelTryReceiveMsgPipe", p, dst, 4u, MsgPipe.WaitAsap, new Ptr<uint>(res)));
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelReceiveMsgPipe", p, dst, 4u, MsgPipe.WaitAll, new Ptr<uint>(res), new Ptr<uint>(_h.Word(1000))));
            Assert.Equal(0u, GuestMemory.R32(res));
            // DONT_WAIT (0x10) on the blocking call: queued for the kernel's async threads, success at once (the cancel
            // below ends it)
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipe", p, dst, 4u, MsgPipe.DontWait, new Ptr<uint>(res), new Ptr<uint>(0)));

            Assert.Equal(0, Call(Tm, "sceKernelCancelMsgPipe", p, new Ptr<int>(_h.Word(9)), new Ptr<int>(_h.Word(9))));
            Assert.Equal(0, Call(Tm, "_sceKernelCancelMsgPipe", p, new Ptr<int>(0), new Ptr<int>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelDeleteMsgPipe", p));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelDeleteMsgPipe", p));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelSendMsgPipe", p, src, 1u, 0u, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelTrySendMsgPipe", p, src, 1u, 0u, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelReceiveMsgPipe", p, dst, 1u, 0u, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelTryReceiveMsgPipe", p, dst, 1u, 0u, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelCancelMsgPipe", p, new Ptr<int>(0), new Ptr<int>(0)));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelGetMsgPipeInfo", p, info));

            // creator id by name: the creating process
            int named = Call(Tm, "sceKernelCreateMsgPipeWithLR", "ServerMP", 0x40u, 0u, 64u, 0u);
            Assert.True(named > 0);
            Assert.Equal(Emu.Pid, Call(Tm, "sceKernelGetMsgPipeCreatorId", _h.Str("ServerMP")));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelGetMsgPipeCreatorId", _h.Str("NoSuchMP")));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelGetMsgPipeCreatorId", 0x1234u));
            // syscall create: opt {bufSize, option, lr}
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "_sceKernelCreateMsgPipeWithLR", "sys", 0x40u, 0u, 0u));
            uint opt = _h.Alloc(12);
            GuestMemory.W32(opt, 8);
            int sys = Call(Tm, "_sceKernelCreateMsgPipeWithLR", "sys", 0x40u, 0u, opt);
            Call(Tm, "sceKernelGetMsgPipeInfo", sys, info);
            Assert.Equal(0x1000u, GuestMemory.R32(info + 0x2C));
        });
    }

    [Fact]
    public void MsgPipeVectorsGatherAndScatter()
    {
        _h.OnGuest(_ =>
        {
            int p = Call(Tm, "sceKernelCreateMsgPipe", "mppv", 0x40u, 0u, 64u, 0u);
            uint a = _h.Alloc(4), b = _h.Alloc(4), vec = _h.Alloc(16), res = _h.Word(0);
            GuestMemory.W32(a, 0x44332211);
            GuestMemory.W32(b, 0x88776655);
            GuestMemory.W32(vec, a);
            GuestMemory.W32(vec + 4, 4);
            GuestMemory.W32(vec + 8, b);
            GuestMemory.W32(vec + 12, 4);
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelSendMsgPipeVector", p, 0u, 2u, 1u, new Ptr<uint>(res), new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelSendMsgPipeVector", p, vec, 0u, 1u, new Ptr<uint>(res), new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_MSG_PIPE_ID, Call(Tm, "sceKernelTrySendMsgPipeVector", 0x7FFF0001, vec, 2u, 1u, new Ptr<uint>(res)));
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipeVector", p, vec, 2u, MsgPipe.WaitAll, new Ptr<uint>(res), new Ptr<uint>(0)));
            Assert.Equal(8u, GuestMemory.R32(res));
            // receive into two buffers of 3 and 5 bytes: the bytes run across them in order
            uint x = _h.Alloc(8), y = _h.Alloc(8), rv = _h.Alloc(16);
            GuestMemory.W32(rv, x);
            GuestMemory.W32(rv + 4, 3);
            GuestMemory.W32(rv + 8, y);
            GuestMemory.W32(rv + 12, 5);
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipeVector", p, rv, 2u, MsgPipe.WaitAll, new Ptr<uint>(res), new Ptr<uint>(0)));
            Assert.Equal(8u, GuestMemory.R32(res));
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, GuestMemory.ReadSpan(x, 3).ToArray());
            Assert.Equal(new byte[] { 0x44, 0x55, 0x66, 0x77, 0x88 }, GuestMemory.ReadSpan(y, 5).ToArray());
            Assert.Equal(SceError.MPP_EMPTY, Call(Tm, "sceKernelTryReceiveMsgPipeVector", p, rv, 2u, 0u, new Ptr<uint>(res)));
            // a NULL base in the vector
            GuestMemory.W32(rv + 8, 0);
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelTryReceiveMsgPipeVector", p, rv, 2u, 0u, new Ptr<uint>(res)));
            // zero total bytes: OK, nothing moved
            uint z = _h.Alloc(8);
            GuestMemory.W32(z, a);
            GuestMemory.W32(z + 4, 0);
            Assert.Equal(0, Call(Tm, "sceKernelTrySendMsgPipeVector", p, z, 1u, 0u, new Ptr<uint>(res)));
            // syscall forms: opt {mode, result*, timeout*}
            uint opt = _h.Alloc(12);
            GuestMemory.W32(opt, MsgPipe.WaitAll);
            GuestMemory.W32(opt + 4, res);
            Assert.Equal(0, Call(Tm, "_sceKernelSendMsgPipeVector", p, vec, 2u, opt));
            Assert.Equal(0, Call(Tm, "_sceKernelTrySendMsgPipeVector", p, vec, 1u, opt));
            GuestMemory.W32(rv + 8, y);
            Assert.Equal(0, Call(Tm, "_sceKernelReceiveMsgPipeVector", p, rv, 2u, opt));
            Assert.Equal(0, Call(Tm, "_sceKernelTryReceiveMsgPipeVector", p, rv, 1u, opt));
            Assert.Equal(3u, GuestMemory.R32(res));
        });
    }

    [Fact]
    public void MsgPipeBlockingReceiveWakesOnSend()
    {
        _h.OnGuest(_ =>
        {
            int p = Call(Tm, "sceKernelCreateMsgPipe", "mppw", 0x40u, 0u, 8u, 0u);
            uint dst = _h.Alloc(8), res = _h.Word(0), src = _h.Alloc(8);
            GuestMemory.W32(src, 0xCAFEBABE);
            int got = 1;
            GuestRun w = _h.Start(_ => got = Call(Tm, "sceKernelReceiveMsgPipeCB", p, dst, 4u, MsgPipe.WaitAll, new Ptr<uint>(res), new Ptr<uint>(0)), "mpp-recv");
            uint info = Info(0x3C);
            Until(() => { Call(Tm, "sceKernelGetMsgPipeInfo", p, info); return GuestMemory.R32(info + 0x38) == 1; }, "the receive wait");
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", p, src, 4u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            _h.Join(w);
            Assert.Equal(0, got);
            Assert.Equal(0xCAFEBABEu, GuestMemory.R32(dst));
            // cancel wakes a receiver with WAIT_CANCEL and counts it
            GuestRun c = _h.Start(_ => got = Call(Tm, "sceKernelReceiveMsgPipe", p, dst, 4u, MsgPipe.WaitAll, new Ptr<uint>(res), new Ptr<uint>(0)), "mpp-cancel");
            Until(() => { Call(Tm, "sceKernelGetMsgPipeInfo", p, info); return GuestMemory.R32(info + 0x38) == 1; }, "the receive wait");
            uint ns = _h.Word(9), nr = _h.Word(9);
            Assert.Equal(0, Call(Tm, "sceKernelCancelMsgPipe", p, new Ptr<int>(ns), new Ptr<int>(nr)));
            _h.Join(c);
            Assert.Equal(SceError.WAIT_CANCEL, got);
            Assert.Equal(0u, GuestMemory.R32(ns));
            Assert.Equal(1u, GuestMemory.R32(nr));
            // the pipe as an event object: OUT while there is room, IN once there is data
            uint pat = _h.Word(0);
            Assert.Equal(0, Call(Tm, "sceKernelPollEvent", p, MsgPipe.EventIn | MsgPipe.EventOut, new Ptr<uint>(pat), new Ptr<ulong>(0)));
            Assert.Equal(MsgPipe.EventOut, GuestMemory.R32(pat));
            Assert.Equal(SceError.EVENT_COND, Call(Tm, "sceKernelPollEvent", p, MsgPipe.EventIn, new Ptr<uint>(pat), new Ptr<ulong>(0)));
            Call(Tm, "sceKernelSendMsgPipe", p, src, 2u, 0u, new Ptr<uint>(0), new Ptr<uint>(0));
            Assert.Equal(0, Call(Tm, "sceKernelWaitEvent", p, MsgPipe.EventIn, new Ptr<uint>(pat), new Ptr<ulong>(0), new Ptr<uint>(_h.Word(1000))));
            Assert.Equal(MsgPipe.EventIn, GuestMemory.R32(pat));
            // delete wakes with WAIT_DELETE
            Call(Tm, "sceKernelReceiveMsgPipe", p, dst, 2u, 0u, new Ptr<uint>(0), new Ptr<uint>(0));
            GuestRun d = _h.Start(_ => got = Call(Tm, "sceKernelReceiveMsgPipe", p, dst, 4u, MsgPipe.WaitAll, new Ptr<uint>(res), new Ptr<uint>(0)), "mpp-del");
            Until(() => { Call(Tm, "sceKernelGetMsgPipeInfo", p, info); return GuestMemory.R32(info + 0x38) == 1; }, "the receive wait");
            Assert.Equal(0, Call(Tm, "sceKernelDeleteMsgPipe", p));
            _h.Join(d);
            Assert.Equal(SceError.WAIT_DELETE, got);
        });
    }

    /// <summary>A blocking FULL send that does not fit waits with nothing sent (threadmgr copies a FULL message into the
    /// buffer only whole) and completes once a receive leaves room for all of it.</summary>
    [Fact]
    public void MsgPipeFullSendWaitsForRoomForTheWholeMessage()
    {
        _h.OnGuest(_ =>
        {
            int p = Call(Tm, "sceKernelCreateMsgPipe", "mppf", 0x40u, 0u, 0x1000u, 0u);
            uint src = _h.Alloc(0x1000), dst = _h.Alloc(0x1000), res = _h.Word(9), info = Info(0x3C);
            for (uint i = 0; i < 0x1000; i++)
            {
                GuestMemory.Write(src + i, (byte)i);
            }

            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", p, src, 0xFFCu, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            int got = 1;
            GuestRun w = _h.Start(_ => got = Call(Tm, "sceKernelSendMsgPipe", p, src + 0x100, 8u, MsgPipe.WaitAll, new Ptr<uint>(res), new Ptr<uint>(0)), "mpp-full");
            Until(() => { Call(Tm, "sceKernelGetMsgPipeInfo", p, info); return GuestMemory.R32(info + 0x34) == 1; }, "the send wait");
            Assert.Equal(4u, GuestMemory.R32(info + 0x30)); // nothing of the 8 bytes went in
            // 2 bytes out: still no room for the whole message
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipe", p, dst, 2u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            Thread.Sleep(20);
            Call(Tm, "sceKernelGetMsgPipeInfo", p, info);
            Assert.Equal(1u, GuestMemory.R32(info + 0x34));
            Assert.Equal(6u, GuestMemory.R32(info + 0x30));
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipe", p, dst + 2, 2u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            _h.Join(w);
            Assert.Equal(0, got);
            Assert.Equal(8u, GuestMemory.R32(res));
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipe", p, dst, 0x1000u, MsgPipe.WaitAsap, new Ptr<uint>(res), new Ptr<uint>(0)));
            Assert.Equal(0x1000u, GuestMemory.R32(res));
            Assert.True(GuestMemory.ReadSpan(dst + 0xFF8, 8).SequenceEqual(GuestMemory.ReadSpan(src + 0x100, 8)));
            Assert.Equal(0, Call(Tm, "sceKernelDeleteMsgPipe", p));
        });
    }

    // ------------------------------------------------------------------ rw locks

    [Fact]
    public void RwLockReadersAndWriters()
    {
        _h.OnGuest(self =>
        {
            int l = Call(Tm, "sceKernelCreateRWLock", "rw", 0u, 0u);
            Assert.Equal(12, Call(Tm, "sceKernelGetThreadmgrUIDClass", l));
            Assert.Equal(0, Call(Tm, "sceKernelLockReadRWLock", l, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelTryLockReadRWLock", l));
            // SceKernelRWLockInfo: +0x2C lock count (readers), +0x30 writer, +0x34 read waiters, +0x38 write waiters
            uint info = Info(0x3C);
            Assert.Equal(0, Call(Tm, "sceKernelGetRWLockInfo", l, info));
            Assert.Equal(2u, GuestMemory.R32(info + 0x2C));
            Assert.Equal(0u, GuestMemory.R32(info + 0x30));
            // readers block a writer
            Assert.Equal(SceError.RW_LOCK_FAILED_TO_LOCK, Call(Tm, "sceKernelTryLockWriteRWLock", l));
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelLockWriteRWLock", l, new Ptr<uint>(_h.Word(1000))));
            Assert.Equal(0, Call(Tm, "sceKernelUnlockReadRWLock", l));
            Assert.Equal(0, Call(Tm, "sceKernelUnlockReadRWLock", l));
            Assert.Equal(SceError.RW_LOCK_UNLOCK_UDF, Call(Tm, "sceKernelUnlockReadRWLock", l));
            // a writer: recursive for itself, blocks other readers and writers
            Assert.Equal(0, Call(Tm, "sceKernelLockWriteRWLockCB", l, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelTryLockWriteRWLock", l));
            Call(Tm, "sceKernelGetRWLockInfo", l, info);
            Assert.Equal(2u, GuestMemory.R32(info + 0x2C));
            Assert.Equal((uint)self.Uid, GuestMemory.R32(info + 0x30));
            GuestRun other = _h.Start(_ =>
            {
                Assert.Equal(SceError.RW_LOCK_FAILED_TO_LOCK, Call(Tm, "sceKernelTryLockReadRWLock", l));
                Assert.Equal(SceError.RW_LOCK_FAILED_TO_LOCK, Call(Tm, "sceKernelTryLockWriteRWLock", l));
                Assert.Equal(SceError.RW_LOCK_NOT_OWNED, Call(Tm, "sceKernelUnlockWriteRWLock", l));
                Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "_sceKernelLockReadRWLock", l, new Ptr<uint>(_h.Word(1000))));
            }, "rw-other");
            _h.Join(other);
            Assert.Equal(0, Call(Tm, "sceKernelUnlockWriteRWLock", l));
            Assert.Equal(0, Call(Tm, "sceKernelUnlockWriteRWLock", l));
            Assert.Equal(SceError.RW_LOCK_NOT_OWNED, Call(Tm, "sceKernelUnlockWriteRWLock", l));

            // a reader waiting on a writer; cancel with the write lock kept by the caller
            Call(Tm, "_sceKernelLockWriteRWLock", l, new Ptr<uint>(0));
            int got = 1;
            GuestRun w = _h.Start(_ => got = Call(Tm, "sceKernelLockReadRWLock", l, new Ptr<uint>(0)), "rw-wait");
            Until(() => { Call(Tm, "sceKernelGetRWLockInfo", l, info); return GuestMemory.R32(info + 0x34) == 1; }, "the read wait");
            uint nr = _h.Word(9), nw = _h.Word(9);
            Assert.Equal(0, Call(Tm, "sceKernelCancelRWLock", l, new Ptr<uint>(nr), new Ptr<uint>(nw), 1));
            _h.Join(w);
            Assert.Equal(SceError.WAIT_CANCEL, got);
            Assert.Equal(1u, GuestMemory.R32(nr));
            Assert.Equal(0u, GuestMemory.R32(nw));
            Call(Tm, "sceKernelGetRWLockInfo", l, info);
            Assert.Equal((uint)self.Uid, GuestMemory.R32(info + 0x30)); // CANCEL_WITH_WRITE_LOCK
            Assert.Equal(0, Call(Tm, "_sceKernelCancelRWLock", l, new Ptr<uint>(0), new Ptr<uint>(0), 0));
            Call(Tm, "sceKernelGetRWLockInfo", l, info);
            Assert.Equal(0u, GuestMemory.R32(info + 0x30));
            Assert.Equal(0, Call(Tm, "_sceKernelGetRWLockInfo", l, info, _h.Word(0x30)));
            GuestMemory.W32(info, 0x3C); // the syscall form wrote its smaller size there

            // delete wakes a waiter with WAIT_DELETE
            Call(Tm, "sceKernelLockWriteRWLock", l, new Ptr<uint>(0));
            GuestRun d = _h.Start(_ => got = Call(Tm, "sceKernelLockWriteRWLock", l, new Ptr<uint>(0)), "rw-del");
            Until(() => { Call(Tm, "sceKernelGetRWLockInfo", l, info); return GuestMemory.R32(info + 0x38) == 1; }, "the write wait");
            Assert.Equal(0, Call(Tm, "sceKernelDeleteRWLock", l));
            _h.Join(d);
            Assert.Equal(SceError.WAIT_DELETE, got);
            foreach (string? fn in new[] { "sceKernelDeleteRWLock", "sceKernelTryLockReadRWLock", "sceKernelTryLockWriteRWLock", "sceKernelUnlockReadRWLock", "sceKernelUnlockWriteRWLock", "sceKernelCloseRWLock" })
            {
                Assert.Equal(SceError.UNKNOWN_RW_LOCK_ID, Call(Tm, fn, l));
            }

            Assert.Equal(SceError.UNKNOWN_RW_LOCK_ID, Call(Tm, "sceKernelGetRWLockInfo", l, info));
            Assert.Equal(SceError.UNKNOWN_RW_LOCK_ID, Call(Tm, "sceKernelLockReadRWLock", l, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_RW_LOCK_ID, Call(Tm, "sceKernelLockWriteRWLock", l, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_RW_LOCK_ID, Call(Tm, "sceKernelCancelRWLock", l, new Ptr<uint>(0), new Ptr<uint>(0), 0));
            Assert.True(Call(Tm, "_sceKernelCreateRWLock", "rw-sys", 0u, 0u) > 0);
        });
    }

    // ------------------------------------------------------------------ callbacks

    [Fact]
    public void CallbacksCountNotifiesAndCancel()
    {
        _h.OnGuest(self =>
        {
            int cb = Call(Tm, "sceKernelCreateCallback", "cb", 0u, 0x81000001u, 0x77u);
            Assert.Equal(8, Call(Tm, "sceKernelGetThreadmgrUIDClass", cb));
            Assert.Equal(0, Call(Tm, "sceKernelGetCallbackCount", cb));
            // notifications from another thread add up; the last argument and notifier are kept
            GuestRun n = _h.Start(_ => { Call(Tm, "sceKernelNotifyCallback", cb, 5); Call(Tm, "sceKernelNotifyCallback", cb, 6); }, "cb-notify");
            _h.Join(n);
            Assert.Equal(2, Call(Tm, "sceKernelGetCallbackCount", cb));
            Assert.Equal(2, self.PendingCallbacks);
            // SceKernelCallbackInfo: +0x2C thread, +0x30 func, +0x34 notifier, +0x38 count, +0x3C arg, +0x40 common
            uint info = Info(0x44);
            Assert.Equal(0, Call(Tm, "sceKernelGetCallbackInfo", cb, info));
            Assert.Equal((uint)self.Uid, GuestMemory.R32(info + 0x2C));
            Assert.Equal(0x81000001u, GuestMemory.R32(info + 0x30));
            Assert.Equal((uint)n.Thread.Uid, GuestMemory.R32(info + 0x34));
            Assert.Equal(2u, GuestMemory.R32(info + 0x38));
            Assert.Equal(6u, GuestMemory.R32(info + 0x3C));
            Assert.Equal(0x77u, GuestMemory.R32(info + 0x40));
            Assert.Equal(0, Call(Tm, "_sceKernelGetCallbackInfo", cb, info, _h.Word(0x3C)));
            Assert.Equal(0x3Cu, GuestMemory.R32(info));
            GuestMemory.W32(info, 0x44); // the syscall form wrote its smaller size there
            // cancel drops the notifications without running the callback
            // a second callback of the thread notified once: cancelling the first leaves only its notification pending
            int cb2 = Call(Tm, "sceKernelCreateCallback", "cb2", 0u, 0x81000003u, 0u);
            _h.Join(_h.Start(_ => Call(Tm, "sceKernelNotifyCallback", cb2, 1), "cb2-notify"));
            Assert.Equal(3, self.PendingCallbacks);
            // the kernel clears the count, the argument and the notifier: all of its notifications stop being pending
            Assert.Equal(0, Call(Tm, "sceKernelCancelCallback", cb));
            Assert.Equal(0, Call(Tm, "sceKernelGetCallbackCount", cb));
            Assert.Equal(1, self.PendingCallbacks);
            Assert.Equal(0, Call(Tm, "sceKernelGetCallbackInfo", cb, info));
            Assert.Equal(0u, GuestMemory.R32(info + 0x34));
            Assert.Equal(0u, GuestMemory.R32(info + 0x38));
            Assert.Equal(0u, GuestMemory.R32(info + 0x3C));
            Assert.Equal(0, Call(Tm, "sceKernelCancelCallback", cb2));
            Assert.Equal(0, self.PendingCallbacks);
            Assert.Equal(0, Call(Tm, "sceKernelDeleteCallback", cb2));
            Assert.Equal(0, Call(Tm, "sceKernelCheckCallback")); // nothing pending: nothing runs
            Assert.Equal(0, Call(Tm, "sceKernelDeleteCallback", cb));
            Assert.Equal(SceError.UNKNOWN_CALLBACK_ID, Call(Tm, "sceKernelNotifyCallback", cb, 1));
            Assert.Equal(SceError.UNKNOWN_CALLBACK_ID, Call(Tm, "sceKernelGetCallbackCount", cb));
            Assert.Equal(SceError.UNKNOWN_CALLBACK_ID, Call(Tm, "sceKernelCancelCallback", cb));
            Assert.Equal(SceError.UNKNOWN_CALLBACK_ID, Call(Tm, "sceKernelGetCallbackInfo", cb, info));
        });
    }

    // ------------------------------------------------------------------ timers

    [Fact]
    public void TimerCountsWhileRunning()
    {
        _h.OnGuest(_ =>
        {
            int t = Call(Tm, "sceKernelCreateTimer", "tmr", 0u, 0u);
            Assert.Equal(6, Call(Tm, "sceKernelGetThreadmgrUIDClass", t));
            Assert.Equal(0ul, (ulong)Invoke(Tm, "sceKernelGetTimerTimeWide", t)!);
            Assert.Equal(0ul, (ulong)Invoke(Tm, "sceKernelGetTimerBaseWide", t)!); // never started
            Assert.Equal(0, Call(Tm, "sceKernelStopTimer", t));  // already stopped
            Assert.Equal(0, Call(Tm, "sceKernelStartTimer", t)); // started now
            Assert.Equal(1, Call(Tm, "sceKernelStartTimer", t)); // already running
            Call(Tm, "sceKernelDelayThread", 3000u);
            Assert.Equal(1, Call(Tm, "sceKernelStopTimer", t));
            ulong count = (ulong)Invoke(Tm, "sceKernelGetTimerTimeWide", t)!;
            Assert.True(count >= 3000, $"timer counted {count} us over a 3 ms delay");
            // stopped: the count holds
            Call(Tm, "sceKernelDelayThread", 1000u);
            Assert.Equal(count, (ulong)Invoke(Tm, "sceKernelGetTimerTimeWide", t)!);
            uint v = _h.Alloc(8);
            Assert.Equal(0, Call(Tm, "sceKernelGetTimerTime", t, new Ptr<ulong>(v)));
            Assert.Equal(count, GuestMemory.Read<ulong>(v));
            Assert.Equal(0, Call(Tm, "_sceKernelGetTimerTime", t, new Ptr<ulong>(v)));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelGetTimerTime", t, new Ptr<ulong>(0)));
            // SetTime returns the old count (in/out pointer for the non-wide form)
            Assert.Equal(count, (ulong)Invoke(Tm, "sceKernelSetTimerTimeWide", t, 500ul)!);
            GuestMemory.Write(v, 42ul);
            Assert.Equal(0, Call(Tm, "sceKernelSetTimerTime", t, new Ptr<ulong>(v)));
            Assert.Equal(500ul, GuestMemory.Read<ulong>(v));
            Assert.Equal(42ul, (ulong)Invoke(Tm, "sceKernelGetTimerTimeWide", t)!);
            GuestMemory.Write(v, 7ul);
            Assert.Equal(0, Call(Tm, "_sceKernelSetTimerTime", t, new Ptr<ulong>(v)));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelSetTimerTime", t, new Ptr<ulong>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelGetTimerBase", t, new Ptr<ulong>(v)));
            Assert.Equal(0, Call(Tm, "_sceKernelGetTimerBase", t, new Ptr<ulong>(v)));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelGetTimerBase", t, new Ptr<ulong>(0)));

            // SceKernelTimerInfo (0x60): +0x2C active, +0x38 current time
            uint info = Info(0x60);
            Assert.Equal(0, Call(Tm, "sceKernelGetTimerInfo", t, info));
            Assert.Equal(0u, GuestMemory.R32(info + 0x2C));
            Assert.Equal(7ul, GuestMemory.Read<ulong>(info + 0x38));
            Assert.Equal(0, Call(Tm, "_sceKernelGetTimerInfo", t, info, _h.Word(0x30)));
            Assert.Equal(0x30u, GuestMemory.R32(info));
            GuestMemory.W32(info, 0x60); // the syscall form wrote its smaller size there

            Assert.Equal(0, Call(Tm, "sceKernelDeleteTimer", t));
            Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, "sceKernelDeleteTimer", t));
            Assert.Equal(ulong.MaxValue, (ulong)Invoke(Tm, "sceKernelGetTimerTimeWide", t)!); // an error code could pass for a time
            Assert.Equal(ulong.MaxValue, (ulong)Invoke(Tm, "sceKernelSetTimerTimeWide", t, 1ul)!);
            Assert.Equal(ulong.MaxValue, (ulong)Invoke(Tm, "sceKernelGetTimerBaseWide", t)!);
            foreach (string? fn in new[] { "sceKernelStartTimer", "sceKernelStopTimer" })
            {
                Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, fn, t));
            }

            Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, "sceKernelGetTimerTime", t, new Ptr<ulong>(v)));
            Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, "sceKernelSetTimerTime", t, new Ptr<ulong>(v)));
            Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, "sceKernelGetTimerBase", t, new Ptr<ulong>(v)));
            Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, "sceKernelGetTimerInfo", t, info));
            Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, "sceKernelSetTimerEvent", t, 0, new Ptr<ulong>(v), 0));
            Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, "sceKernelCancelTimer", t, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_TIMER_ID, Call(Tm, "sceKernelGetTimerEventRemainingTime", t, new Ptr<ulong>(v)));
            Assert.True(Call(Tm, "_sceKernelCreateTimer", "tmr-sys", 0u, 0u) > 0);
        });
    }

    [Fact]
    public void TimerEventsFireAndCancel()
    {
        _h.OnGuest(_ =>
        {
            int t = Call(Tm, "sceKernelCreateTimer", "tmr-ev", EventBits.AttrAutoReset, 0u);
            // the timer's process clock under the test's control: what the test checks is the order of events
            // (period, wait, poll), not how fast the host runs (a loaded machine let a 20 ms period pass between
            // the wait and the poll)
            TimerObject timer = Emu.Kernel.Objects.Get<TimerObject>(t)!;
            Func<ulong> processClock = timer.Clock;
            ulong now = 1_000_000;
            timer.Clock = () => Volatile.Read(ref now);
            uint iv = _h.Alloc(8), rem = _h.Alloc(8), pat = _h.Word(0);
            Assert.Equal(unchecked((int)0x80028242), Call(Tm, "sceKernelGetTimerEventRemainingTime", t, new Ptr<ulong>(rem))); // none armed
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelGetTimerEventRemainingTime", t, new Ptr<ulong>(0)));
            GuestMemory.Write(iv, 20000ul);
            Assert.Equal(SceError.ILLEGAL_TYPE, Call(Tm, "sceKernelSetTimerEvent", t, 2, new Ptr<ulong>(iv), 0));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelSetTimerEvent", t, 0, new Ptr<ulong>(0), 0));
            Assert.Equal(0, Call(Tm, "sceKernelSetTimerEvent", t, 0, new Ptr<ulong>(iv), 1)); // SET_EVENT, repeating
            Assert.Equal(0, Call(Tm, "sceKernelStartTimer", t));
            Volatile.Write(ref now, now + 5000);
            Assert.Equal(0, Call(Tm, "sceKernelGetTimerEventRemainingTime", t, new Ptr<ulong>(rem)));
            Assert.Equal(15000ul, GuestMemory.Read<ulong>(rem)); // the period less the 5 ms that passed
            Assert.Equal(0, Call(Tm, "_sceKernelGetTimerEventRemainingTime", t, new Ptr<ulong>(rem)));
            // the timer is an event object: the wait blocks until the period ends, then ends with EVENT_TIMER (0x8000)
            using (var periodEnds = new System.Threading.Timer(_ => Volatile.Write(ref now, 1_000_000 + 20001), null, 50, Timeout.Infinite))
            {
                Assert.Equal(0, Call(Tm, "sceKernelWaitEvent", t, 0u, new Ptr<uint>(pat), new Ptr<ulong>(0), new Ptr<uint>(_h.Word(1_000_000))));
            }
            Assert.Equal(1_000_000ul + 20001, Volatile.Read(ref now)); // it did not end before the period
            Assert.Equal(EventBits.Timer, GuestMemory.R32(pat));
            uint info = Info(0x60);
            Call(Tm, "sceKernelGetTimerInfo", t, info);
            Assert.Equal(1u, GuestMemory.R32(info + 0x2C));      // active
            Assert.Equal(20000ul, GuestMemory.Read<ulong>(info + 0x48)); // interval
            Assert.Equal(0u, GuestMemory.R32(info + 0x50));      // SET, not PULSE
            Assert.Equal(1u, GuestMemory.R32(info + 0x54));      // repeat
            // auto-reset: consumed by the wait, so an immediate poll does not see it
            Assert.Equal(SceError.EVENT_COND, Call(Tm, "sceKernelPollEvent", t, 0u, new Ptr<uint>(pat), new Ptr<ulong>(0)));
            // after the next period it is there again
            Volatile.Write(ref now, 1_000_000 + 40001);
            Assert.Equal(0, Call(Tm, "sceKernelPollEvent", t, 0u, new Ptr<uint>(pat), new Ptr<ulong>(0)));
            Assert.Equal(EventBits.Timer, GuestMemory.R32(pat));
            Assert.Equal(0, Call(Tm, "sceKernelClearEvent", t, 0u));
            // cancel disarms
            uint n = _h.Word(9);
            Assert.Equal(0, Call(Tm, "sceKernelCancelTimer", t, new Ptr<uint>(n)));
            Assert.Equal(0u, GuestMemory.R32(n));
            Assert.Equal(unchecked((int)0x80028242), Call(Tm, "sceKernelGetTimerEventRemainingTime", t, new Ptr<ulong>(rem)));
            Assert.Equal(0, Call(Tm, "_sceKernelCancelTimer", t, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelCancelEvent", t, new Ptr<uint>(n)));
            // a timeout counts on the process clock as it runs
            timer.Clock = processClock;
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelWaitEvent", t, 0u, new Ptr<uint>(pat), new Ptr<ulong>(0), new Ptr<uint>(_h.Word(1000))));
            Assert.Equal(0, Call(Tm, "_sceKernelSetTimerEvent", t, 1, new Ptr<ulong>(iv), 0));
        });
    }

    [Fact]
    public void MsgPipeDontWaitReturnsAtOnceAndTheKernelTransfersLater()
    {
        _h.OnGuest(_ =>
        {
            int pipe = Call(Tm, "sceKernelCreateMsgPipe", "mpp-later", 0x40u, 0u, 0x1000u, 0u);
            uint buf = _h.Alloc(8), msg = _h.Alloc(8), res = _h.Word(0xFFFFFFFF);
            GuestMemory.W32(msg, 0x11223344);
            GuestMemory.W32(msg + 4, 0x55667788);
            // the blocking receive with DONT_WAIT on an empty pipe: success at once (not MPP_EMPTY), nothing moved yet
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipe", pipe, buf, 8u, MsgPipe.WaitAll | MsgPipe.DontWait, new Ptr<uint>(res), new Ptr<uint>(0)));
            Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(res));
            // a send brings the data: the kernel's transfer takes it
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", pipe, msg, 8u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal(8u, GuestMemory.R32(res));
            Assert.Equal(0x55667788u, GuestMemory.R32(buf + 4));
            // the vector form refuses other mode bits
            uint vec = _h.Alloc(8);
            GuestMemory.W32(vec, buf);
            GuestMemory.W32(vec + 4, 8);
            Assert.Equal(SceError.ILLEGAL_MODE, Call(Tm, "sceKernelReceiveMsgPipeVector", pipe, vec, 1u, 0x20u | MsgPipe.DontWait, new Ptr<uint>(0), new Ptr<uint>(0)));
            // the poll form keeps failing at once
            Assert.Equal(SceError.MPP_EMPTY, Call(Tm, "sceKernelTryReceiveMsgPipe", pipe, buf, 8u, MsgPipe.DontWait, new Ptr<uint>(0)));
            // a process may have 0x100 transfers queued, then the kernel refuses
            for (int i = 0; i < ThreadMgr.MaxLaterTransfers; i++)
            {
                Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipeVector", pipe, vec, 1u, MsgPipe.DontWait, new Ptr<uint>(0), new Ptr<uint>(0)));
            }

            Assert.Equal(ThreadMgr.ErrorLaterTransfersFull, Call(Tm, "sceKernelReceiveMsgPipeVector", pipe, vec, 1u, MsgPipe.DontWait, new Ptr<uint>(0), new Ptr<uint>(0)));
            // deleting the pipe ends them, and the process may queue again
            Assert.Equal(0, Call(Tm, "sceKernelDeleteMsgPipe", pipe));
            int other = Call(Tm, "sceKernelCreateMsgPipe", "mpp-later2", 0x40u, 0u, 0x1000u, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipeVector", other, vec, 1u, MsgPipe.DontWait, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelDeleteMsgPipe", other));
        });
    }

    // ------------------------------------------------------------------ simple events

    [Fact]
    public void SimpleEventSetPollWaitAndInfo()
    {
        _h.OnGuest(_ =>
        {
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelCreateSimpleEvent", 0u, 0u, 0u, 0u));
            Assert.Equal(SceError.ILLEGAL_ATTR, Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("e"), 0x1u, 0u, 0u));
            Assert.Equal(SceError.ILLEGAL_PATTERN, Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("e"), 0u, 0x1u, 0u));
            Assert.Equal(SceError.UID_NAME_TOO_LONG, Call(Tm, "sceKernelCreateSimpleEvent", _h.Str(new string('n', 32)), EventBits.AttrOpenable, 0u, 0u));
            int e = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("sev"), EventBits.AttrAutoReset, 0x01000000u, 0u);
            Assert.Equal(13, Call(Tm, "sceKernelGetThreadmgrUIDClass", e));
            // the CREATE bit (4) is set at creation next to the initial user bits
            uint pat = _h.Word(0);
            Assert.Equal(0, Call(Tm, "sceKernelGetEventPattern", e, new Ptr<uint>(pat)));
            Assert.Equal(0x01000000u | EventBits.Create, GuestMemory.R32(pat));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelGetEventPattern", e, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "_sceKernelGetEventPattern", e, new Ptr<uint>(pat)));
            // user bits only for set / clear
            Assert.Equal(SceError.ILLEGAL_PATTERN, Call(Tm, "sceKernelSetEvent", e, 0x1u, 0ul));
            Assert.Equal(SceError.ILLEGAL_PATTERN, Call(Tm, "sceKernelClearEvent", e, 0x1u));
            Assert.Equal(0, Call(Tm, "sceKernelSetEvent", e, 0x02000000u, 0x1122334455667788ul));
            uint ud = _h.Alloc(8);
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelPollEvent", e, 0u, new Ptr<uint>(pat), new Ptr<ulong>(ud)));
            Assert.Equal(0, Call(Tm, "sceKernelPollEvent", e, 0x02000000u, new Ptr<uint>(pat), new Ptr<ulong>(ud)));
            Assert.Equal(0x02000000u, GuestMemory.R32(pat));
            Assert.Equal(0x1122334455667788ul, GuestMemory.Read<ulong>(ud));
            // auto-reset consumed it
            Assert.Equal(SceError.EVENT_COND, Call(Tm, "_sceKernelPollEvent", e, 0x02000000u, new Ptr<uint>(pat), new Ptr<ulong>(ud)));
            Assert.Equal(0u, GuestMemory.R32(pat));

            // SceKernelEventInfo (0x40): +0x2C pattern, +0x30 user data, +0x38 waiters
            uint info = Info(0x40);
            Assert.Equal(0, Call(Tm, "sceKernelGetEventInfo", e, info));
            Assert.Equal(EventBits.AttrAutoReset, GuestMemory.R32(info + 0x28));
            Assert.Equal(0x01000000u | EventBits.Create, GuestMemory.R32(info + 0x2C));
            Assert.Equal(0x1122334455667788ul, GuestMemory.Read<ulong>(info + 0x30));
            Assert.Equal(0, Call(Tm, "_sceKernelGetEventInfo", e, info, _h.Word(0x30)));
            GuestMemory.W32(info, 0x40); // the syscall form wrote its smaller size there
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelGetEventInfo", 0x7FFF0001, info));
            // Clear keeps the given bits (the CREATE bit goes with a user-bits-only mask)
            Assert.Equal(0, Call(Tm, "sceKernelClearEvent", e, 0xFF000000u));
            Call(Tm, "sceKernelGetEventPattern", e, new Ptr<uint>(pat));
            Assert.Equal(0x01000000u, GuestMemory.R32(pat));

            // a waiter woken by a set receives the bits and the user data
            uint wpat = _h.Word(0), wud = _h.Alloc(8);
            int got = 1;
            GuestRun w = _h.Start(_ => got = Call(Tm, "sceKernelWaitEventCB", e, 0x04000000u, new Ptr<uint>(wpat), new Ptr<ulong>(wud), new Ptr<uint>(0)), "sev-wait");
            Until(() => { Call(Tm, "sceKernelGetEventInfo", e, info); return GuestMemory.R32(info + 0x38) == 1; }, "the event wait");
            Assert.Equal(0, Call(Tm, "sceKernelSetEvent", e, 0x04000000u, 99ul));
            _h.Join(w);
            Assert.Equal(0, got);
            Assert.Equal(0x04000000u, GuestMemory.R32(wpat));
            Assert.Equal(99ul, GuestMemory.Read<ulong>(wud));
            Call(Tm, "sceKernelGetEventPattern", e, new Ptr<uint>(pat));
            Assert.Equal(0x01000000u, GuestMemory.R32(pat)); // auto-reset took the woken waiter's bits

            // pulse: wakes current waiters only, the pattern is not left set
            GuestRun p = _h.Start(_ => got = Call(Tm, "sceKernelWaitEvent", e, 0x08000000u, new Ptr<uint>(wpat), new Ptr<ulong>(0), new Ptr<uint>(0)), "sev-pulse");
            Until(() => { Call(Tm, "sceKernelGetEventInfo", e, info); return GuestMemory.R32(info + 0x38) == 1; }, "the event wait");
            Assert.Equal(0, Call(Tm, "sceKernelPulseEvent", e, 0x08000000u, 5ul));
            _h.Join(p);
            Assert.Equal(0, got);
            Assert.Equal(SceError.EVENT_COND, Call(Tm, "sceKernelPollEvent", e, 0x08000000u, new Ptr<uint>(pat), new Ptr<ulong>(0)));
            // syscall wait: opt {userData*, timeout*}
            uint opt = _h.Alloc(8);
            GuestMemory.W32(opt + 4, _h.Word(1000));
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "_sceKernelWaitEvent", e, 0x10000000u, new Ptr<uint>(pat), opt));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelWaitEvent", e, 0u, new Ptr<uint>(pat), new Ptr<ulong>(0), new Ptr<uint>(0)));
        });
    }

    [Fact]
    public void SimpleEventCancelAndDelete()
    {
        _h.OnGuest(_ =>
        {
            int e = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("sev-c"), EventBits.AttrThPrio, 0u, 0u);
            uint pat = _h.Word(0), info = Info(0x40);
            int got = 1;
            GuestRun w = _h.Start(_ => got = Call(Tm, "sceKernelWaitEvent", e, 0x01000000u, new Ptr<uint>(pat), new Ptr<ulong>(0), new Ptr<uint>(0)), "sev-cancel");
            Until(() => { Call(Tm, "sceKernelGetEventInfo", e, info); return GuestMemory.R32(info + 0x38) == 1; }, "the event wait");
            uint n = _h.Word(9);
            Assert.Equal(0, Call(Tm, "sceKernelCancelEvent", e, new Ptr<uint>(n)));
            _h.Join(w);
            Assert.Equal(SceError.WAIT_CANCEL, got);
            Assert.Equal(1u, GuestMemory.R32(n));
            Assert.Equal(EventBits.Error, GuestMemory.R32(pat));
            Assert.Equal(0, Call(Tm, "_sceKernelCancelEvent", e, new Ptr<uint>(n)));
            Assert.Equal(0u, GuestMemory.R32(n));

            // cancel with a set pattern replaces the whole pattern and the user data
            Assert.Equal(SceError.ILLEGAL_PATTERN, Call(Tm, "sceKernelCancelEventWithSetPattern", e, 0x1u, 0ul, new Ptr<uint>(n)));
            Assert.Equal(0, Call(Tm, "sceKernelCancelEventWithSetPattern", e, 0x20000000u, 0xABCDul, new Ptr<uint>(n)));
            Call(Tm, "sceKernelGetEventPattern", e, new Ptr<uint>(pat));
            Assert.Equal(0x20000000u, GuestMemory.R32(pat));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelCancelEventWithSetPattern", 0x7FFF0001, 0u, 0ul, new Ptr<uint>(n)));
            Assert.Equal(0u, GuestMemory.R32(n));
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "_sceKernelCancelEventWithSetPattern", e, 0u, 0u));
            uint opt = _h.Alloc(12);
            GuestMemory.W32(opt, 0x5);
            GuestMemory.W32(opt + 4, 0x1);
            GuestMemory.W32(opt + 8, n);
            Assert.Equal(0, Call(Tm, "_sceKernelCancelEventWithSetPattern", e, 0x40000000u, opt));
            Call(Tm, "sceKernelGetEventInfo", e, info);
            Assert.Equal(0x1_00000005ul, GuestMemory.Read<ulong>(info + 0x30));

            // delete: a wait including the DELETE bit (8) ends successfully with it, others with WAIT_DELETE
            int withDelete = 1, plain = 1;
            uint dpat = _h.Word(0);
            GuestRun a = _h.Start(_ => withDelete = Call(Tm, "sceKernelWaitEvent", e, 0x01000000u | EventBits.Delete, new Ptr<uint>(dpat), new Ptr<ulong>(0), new Ptr<uint>(0)), "sev-del-a");
            GuestRun b = _h.Start(_ => plain = Call(Tm, "sceKernelWaitEvent", e, 0x01000000u, new Ptr<uint>(0), new Ptr<ulong>(0), new Ptr<uint>(0)), "sev-del-b");
            Until(() => { Call(Tm, "sceKernelGetEventInfo", e, info); return GuestMemory.R32(info + 0x38) == 2; }, "two event waits");
            Assert.Equal(0, Call(Tm, "sceKernelDeleteSimpleEvent", e));
            _h.Join(a);
            _h.Join(b);
            Assert.Equal(0, withDelete);
            Assert.Equal(EventBits.Delete, GuestMemory.R32(dpat));
            Assert.Equal(SceError.WAIT_DELETE, plain);
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelDeleteSimpleEvent", e));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelSetEvent", e, 0u, 0ul));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelPulseEvent", e, 0u, 0ul));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelClearEvent", e, 0u));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelCancelEvent", e, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelGetEventPattern", e, new Ptr<uint>(pat)));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelWaitEvent", e, 1u, new Ptr<uint>(0), new Ptr<ulong>(0), new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelPollEvent", e, 1u, new Ptr<uint>(0), new Ptr<ulong>(0)));
            Assert.True(Call(Tm, "_sceKernelCreateSimpleEvent", _h.Str("sev-sys"), 0u, 0u, 0u) > 0);
        });
    }

    [Fact]
    public void WaitMultipleEventsOrAndAnd()
    {
        _h.OnGuest(_ =>
        {
            int e1 = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("wm1"), 0u, 0u, 0u);
            int e2 = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("wm2"), 0u, 0u, 0u);
            uint events = _h.Alloc(8 * 3), results = _h.Alloc(0x18 * 3);
            GuestMemory.W32(events, (uint)e1);
            GuestMemory.W32(events + 4, 0x01000000);
            GuestMemory.W32(events + 8, (uint)e2);
            GuestMemory.W32(events + 12, 0x02000000);
            Assert.Equal(SceError.ILLEGAL_ADDR, Call(Tm, "sceKernelWaitMultipleEvents", 0u, 2u, 1u, results, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_SIZE, Call(Tm, "sceKernelWaitMultipleEvents", events, 65u, 1u, results, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_MODE, Call(Tm, "sceKernelWaitMultipleEvents", events, 2u, 4u, results, new Ptr<uint>(0)));
            // nothing set: times out
            uint t = _h.Word(2000);
            Assert.Equal(SceError.WAIT_TIMEOUT, Call(Tm, "sceKernelWaitMultipleEvents", events, 2u, 1u, results, new Ptr<uint>(t)));
            Assert.Equal(0u, GuestMemory.R32(t));
            // OR: the one that is set
            Call(Tm, "sceKernelSetEvent", e2, 0x02000000u, 0x77ul);
            Assert.Equal(1, Call(Tm, "sceKernelWaitMultipleEvents", events, 2u, 1u, results, new Ptr<uint>(_h.Word(100_000))));
            Assert.Equal((uint)e2, GuestMemory.R32(results));
            Assert.Equal(0u, GuestMemory.R32(results + 4));
            Assert.Equal(0x02000000u, GuestMemory.R32(results + 8));
            Assert.Equal(0x77ul, GuestMemory.Read<ulong>(results + 16));
            // AND: both, with the second set from another thread
            GuestRun s = _h.Start(_ => { Thread.Sleep(20); Call(Tm, "sceKernelSetEvent", e1, 0x01000000u, 0ul); }, "wm-set");
            Assert.Equal(2, Call(Tm, "sceKernelWaitMultipleEventsCB", events, 2u, 2u, results, new Ptr<uint>(_h.Word(5_000_000))));
            _h.Join(s);
            Assert.Equal((uint)e2, GuestMemory.R32(results));       // fired first
            Assert.Equal((uint)e1, GuestMemory.R32(results + 0x18));
            // an unknown uid counts as done with UNKNOWN_EVENT_ID and the ERROR bit
            GuestMemory.W32(events + 16, 0x7FFF0001);
            GuestMemory.W32(events + 20, 1);
            Assert.Equal(3, Call(Tm, "sceKernelWaitMultipleEvents", events, 3u, 2u, results, new Ptr<uint>(0)));
            var entries = Enumerable.Range(0, 3).Select(i => (uid: GuestMemory.R32(results + (uint)i * 0x18), r: (int)GuestMemory.R32(results + (uint)i * 0x18 + 4), p: GuestMemory.R32(results + (uint)i * 0x18 + 8))).ToList();
            Assert.Contains((0x7FFF0001u, SceError.UNKNOWN_EVENT_ID, EventBits.Error), entries);
            // without a results array the count of results is 0; the syscall form takes {results*, timeout*}
            Assert.Equal(0, Call(Tm, "sceKernelWaitMultipleEvents", events, 2u, 1u, 0u, new Ptr<uint>(0)));
            uint opt = _h.Alloc(8);
            GuestMemory.W32(opt, results);
            Assert.Equal(1, Call(Tm, "_sceKernelWaitMultipleEvents", events, 1u, 1u, opt));
        });
    }
}
