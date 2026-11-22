using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using static VitaSharp.Tests.HleHarness;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Tests;

/// <summary>
/// Semaphores under adversarial interleavings, against FW 3.74 threadmgr (sceKernelWaitSema, Poll, Signal, Cancel,
/// Delete and the semaphore class's wait-release handler, studied in the decompiled module): the wait queue is first
/// come first served or, with SCE_KERNEL_ATTR_TH_PRIO, by priority; a signal hands the units out in queue order inside
/// the call and stops at the first waiter they do not cover; a wait or poll does not take units ahead of a waiter; a
/// waiter whose timeout passes leaves the queue and the units go on to the ones behind it. The signaller's view is
/// read while the semaphore's lock is held (the woken threads cannot run yet), so the result is decided by the calls,
/// never by the host's timing.
/// </summary>
[Collection("guest")]
public sealed class SemaphoreInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "sema-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private Semaphore Sema(int uid) => _h.Emu.Kernel.Objects.Get<Semaphore>(uid)!;

    private const int HighPriority = 100, LowPriority = 150;

    /// <summary>Starts a thread that waits on <paramref name="uid"/> for <paramref name="need"/> units and records the
    /// result in <paramref name="results"/>[<paramref name="index"/>]; returns once it is in the queue.</summary>
    private GuestRun Waiter(int uid, int need, int[] results, int index, int priority = 0x10000100, uint timeout = 0)
    {
        GuestRun run = _h.Start(_ => results[index] = Call(Tm, "sceKernelWaitSema", uid, need, new Ptr<uint>(timeout)), $"sema-waiter-{index}", priority);
        Until(() => run.Thread.Status == ThreadStatus.Waiting, $"waiter {index} in the queue");
        return run;
    }

    /// <summary>numWaitThreads and currentCount as sceKernelGetSemaInfo reports them.</summary>
    private (int Count, int Waiters) Info(GuestThread main, int uid)
    {
        uint info = _h.Alloc(0x3C);
        GuestMemory.W32(info, 0x3C);
        Assert.Equal(0u, main.DispatchForTest("sceKernelGetSemaInfo", (uint)uid, info));
        return ((int)GuestMemory.R32(info + 0x30), (int)GuestMemory.R32(info + 0x38));
    }

    /// <summary>
    /// A FIFO semaphore serves its waiters in the order they came: a signal of 2 with three waiters for 1 gives the
    /// units to the first two inside the signal (none left, one waiter), and a poll right after it finds nothing,
    /// even though the woken threads have not run yet.
    /// </summary>
    [Fact]
    public void AFifoSignalServesTheWaitersInArrivalOrder()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateSema", "fifo", 0u, 0, 5, 0u);
            Semaphore sema = Sema(uid);
            int[] results = [1, 1, 1];
            GuestRun[] waiters = [Waiter(uid, 1, results, 0), Waiter(uid, 1, results, 1), Waiter(uid, 1, results, 2)];
            lock (sema)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelSignalSema", (uint)uid, 2u));
                Assert.Equal((0, 1), Info(main, uid));
                Assert.Equal(unchecked((uint)SceError.SEMA_ZERO), main.DispatchForTest("sceKernelPollSema", (uint)uid, 1u));
            }
            _h.Join(waiters[0]);
            _h.Join(waiters[1]);
            Assert.Equal([0, 0, 1], results);
            Assert.Equal(ThreadStatus.Waiting, waiters[2].Thread.Status);
            Assert.Equal(0, Call(Tm, "sceKernelSignalSema", uid, 1));
            _h.Join(waiters[2]);
            Assert.Equal([0, 0, 0], results);
            Assert.Equal((0, 0), Info(main, uid));
        });
    }

    /// <summary>
    /// With SCE_KERNEL_ATTR_TH_PRIO the higher-priority waiter is served first even though it came last; without the
    /// attribute the one that came first is. Equal priorities keep their arrival order.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APrioritySemaphoreServesTheHigherPriorityWaiterFirst(bool threadPriority)
    {
        _h.OnGuest(main =>
        {
            uint attr = threadPriority ? Semaphore.AttrThreadPriority : 0u;
            int uid = Call(Tm, "sceKernelCreateSema", threadPriority ? "th-prio" : "th-fifo", attr, 0, 5, 0u);
            Semaphore sema = Sema(uid);
            int[] results = [1, 1, 1];
            GuestRun low = Waiter(uid, 1, results, 0, LowPriority);
            GuestRun highFirst = Waiter(uid, 1, results, 1, HighPriority);
            GuestRun highSecond = Waiter(uid, 1, results, 2, HighPriority);
            GuestRun[] expectedOrder = threadPriority ? [highFirst, highSecond, low] : [low, highFirst, highSecond];
            for (int served = 0; served < 3; served++)
            {
                lock (sema)
                {
                    Assert.Equal(0u, main.DispatchForTest("sceKernelSignalSema", (uint)uid, 1u));
                    Assert.Equal((0, 2 - served), Info(main, uid));
                }
                _h.Join(expectedOrder[served]);
                for (int later = served + 1; later < 3; later++)
                {
                    Assert.Equal(ThreadStatus.Waiting, expectedOrder[later].Thread.Status);
                }
            }
            Assert.Equal([0, 0, 0], results);
        });
    }

    /// <summary>
    /// A signal stops at the first waiter its units do not cover: with waiters for 1, 3 and 1, a signal of 2 serves
    /// the first, leaves 1 unit, and does not give it to the third, which comes after the waiter for 3. A poll or a
    /// new wait for 1 does not take that unit ahead of the queue either; the next signal of 2 serves the waiter for 3.
    /// </summary>
    [Fact]
    public void ASignalStopsAtTheFirstWaiterItDoesNotCover()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateSema", "partial", 0u, 0, 5, 0u);
            Semaphore sema = Sema(uid);
            int[] results = [1, 1, 1, 1];
            GuestRun first = Waiter(uid, 1, results, 0);
            GuestRun big = Waiter(uid, 3, results, 1);
            GuestRun third = Waiter(uid, 1, results, 2);
            lock (sema)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelSignalSema", (uint)uid, 2u));
                Assert.Equal((1, 2), Info(main, uid));
                Assert.Equal(unchecked((uint)SceError.SEMA_ZERO), main.DispatchForTest("sceKernelPollSema", (uint)uid, 1u));
            }
            _h.Join(first);
            GuestRun late = _h.Start(_ => results[3] = Call(Tm, "sceKernelWaitSema", uid, 1, new Ptr<uint>(0)), "sema-late");
            Until(() => late.Thread.Status == ThreadStatus.Waiting || !late.Thread.IsLive, "the late wait");
            Assert.Equal(ThreadStatus.Waiting, late.Thread.Status);
            Assert.Equal((1, 3), Info(main, uid));
            lock (sema)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelSignalSema", (uint)uid, 2u));
                Assert.Equal((0, 2), Info(main, uid));
            }
            _h.Join(big);
            Assert.Equal(0, Call(Tm, "sceKernelSignalSema", uid, 2));
            _h.Join(third);
            _h.Join(late);
            Assert.Equal([0, 0, 0, 0], results);
            Assert.Equal((0, 0), Info(main, uid));
        });
    }

    /// <summary>
    /// A cancel takes every waiter off the queue (numWait: how many) and sets the count without serving anyone: a
    /// signal that comes before the cancelled threads run adds to that count. A count above the maximum is refused
    /// with nothing changed.
    /// </summary>
    [Fact]
    public void ACancelReleasesTheQueueWithoutServingIt()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateSema", "cancel", 0u, 0, 5, 0u);
            Semaphore sema = Sema(uid);
            int[] results = [1, 1];
            GuestRun big = Waiter(uid, 2, results, 0);
            GuestRun small = Waiter(uid, 1, results, 1);
            uint numWait = _h.Word(0xDEAD);
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelCancelSema", uid, 6, new Ptr<int>(numWait)));
            Assert.Equal(0xDEADu, GuestMemory.R32(numWait));
            Assert.Equal((0, 2), Info(main, uid));
            lock (sema)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelCancelSema", (uint)uid, 1u, numWait));
                Assert.Equal(0u, main.DispatchForTest("sceKernelSignalSema", (uint)uid, 1u));
                Assert.Equal((2, 0), Info(main, uid));
            }
            _h.Join(big);
            _h.Join(small);
            Assert.Equal(2u, GuestMemory.R32(numWait));
            Assert.Equal([SceError.WAIT_CANCEL, SceError.WAIT_CANCEL], results);
            Assert.Equal(2, sema.Count);
        });
    }

    /// <summary>A deleted semaphore's waiters return WAIT_DELETE whenever their threads run, also one a signal had
    /// not covered; the UID is gone for every call after it.</summary>
    [Fact]
    public void ADeleteReleasesTheQueue()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateSema", "delete", 0u, 1, 5, 0u);
            Semaphore sema = Sema(uid);
            int[] results = [1, 1];
            GuestRun big = Waiter(uid, 3, results, 0);
            GuestRun small = Waiter(uid, 1, results, 1);
            lock (sema)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelDeleteSema", (uint)uid));
                Assert.Equal(unchecked((uint)SceError.UNKNOWN_SEMA_ID), main.DispatchForTest("sceKernelSignalSema", (uint)uid, 1u));
            }
            _h.Join(big);
            _h.Join(small);
            Assert.Equal([SceError.WAIT_DELETE, SceError.WAIT_DELETE], results);
            Assert.Equal(SceError.UNKNOWN_SEMA_ID, Call(Tm, "sceKernelPollSema", uid, 1));
        });
    }

    /// <summary>
    /// A wait's timeout is the kernel's timer at a guest-time deadline (the test moves guest time): once it has passed, the waiter is off the queue
    /// and a later signal does not serve it, however late its host thread is to notice. It returns WAIT_TIMEOUT with no
    /// time left, and the signal's unit stays for the next caller.
    /// </summary>
    [Fact]
    public void ASignalAfterTheDeadlineDoesNotServeALateWaiter()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            const uint LimitUs = 50_000;
            int uid = Call(Tm, "sceKernelCreateSema", "deadline", 0u, 0, 5, 0u);
            Semaphore sema = Sema(uid);
            uint timeout = _h.Word(LimitUs);
            int result = 1;
            GuestRun waiter = _h.Start(_ => result = Call(Tm, "sceKernelWaitSema", uid, 1, new Ptr<uint>(timeout)), "sema-late");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the wait");
            lock (sema)
            {
                clock.Advance(LimitUs - 1);
                Assert.Equal((0, 1), Info(main, uid)); // a microsecond before the deadline it still waits
                clock.Advance(1);
                Assert.Equal(0u, main.DispatchForTest("sceKernelSignalSema", (uint)uid, 1u));
                Assert.Equal((1, 0), Info(main, uid));
            }
            _h.Join(waiter);
            Assert.Equal(SceError.WAIT_TIMEOUT, result);
            Assert.Equal(0u, GuestMemory.R32(timeout));
            Assert.Equal(1, sema.Count);
        });
    }

    /// <summary>
    /// A waiter at the head of the queue whose timeout passes hands the units on: with 1 unit left behind a waiter for
    /// 2 (the signal stopped there), the waiter for 1 behind it is served when the first one's deadline passes, without
    /// another signal, and a poll then finds nothing.
    /// </summary>
    [Fact]
    public void ATimedOutHeadHandsTheUnitsToTheWaitersBehindIt()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            const uint LimitUs = 50_000;
            int uid = Call(Tm, "sceKernelCreateSema", "timeout-head", 0u, 0, 5, 0u);
            Semaphore sema = Sema(uid);
            uint timeout = _h.Word(LimitUs);
            int[] results = [1, 1];
            GuestRun head = _h.Start(_ => results[0] = Call(Tm, "sceKernelWaitSema", uid, 2, new Ptr<uint>(timeout)), "sema-head");
            Until(() => head.Thread.Status == ThreadStatus.Waiting, "the head's wait");
            GuestRun behind = Waiter(uid, 1, results, 1);
            lock (sema)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelSignalSema", (uint)uid, 1u));
                Assert.Equal((1, 2), Info(main, uid));
                clock.Advance(LimitUs - 1);
                Assert.Equal((1, 2), Info(main, uid)); // a microsecond before the deadline the head still waits
                clock.Advance(1);
                Assert.Equal(unchecked((uint)SceError.SEMA_ZERO), main.DispatchForTest("sceKernelPollSema", (uint)uid, 1u));
                Assert.Equal((0, 0), Info(main, uid));
            }
            _h.Join(head);
            _h.Join(behind);
            Assert.Equal([SceError.WAIT_TIMEOUT, 0], results);
            Assert.Equal(0u, GuestMemory.R32(timeout));
        });
    }

    /// <summary>
    /// A save state keeps the semaphore's queue: a waiter a signal served that had not returned when the state was
    /// saved returns OK when its wait is reissued (the count was saved without its units), and the waiters still
    /// queued keep their saved order however the restored threads happen to reissue their waits (here in reverse).
    /// </summary>
    [Fact]
    public void ASaveStateKeepsTheQueueOrderAndTheServedWaiters()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateSema", "saved", 0u, 0, 5, 0u);
            Semaphore sema = Sema(uid);
            Semaphore restored = new() { Name = "saved" };
            int[] needs = [2, 1, 1];
            int[] first = [1, 1, 1];
            int[] second = [1, 1, 1];
            int[] gates = new int[3];
            var runs = new GuestRun[3];
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                gates[i] = Call(Tm, "sceKernelCreateSema", $"gate-{i}", 0u, 0, 1, 0u);
                runs[i] = _h.Start(_ =>
                {
                    first[index] = Call(Tm, "sceKernelWaitSema", uid, needs[index], new Ptr<uint>(0));
                    Assert.Equal(0, Call(Tm, "sceKernelWaitSema", gates[index], 1, new Ptr<uint>(0))); // the reissue comes when main says
                    second[index] = restored.WaitSema(needs[index], new Ptr<uint>(0));
                }, $"sema-saved-{i}");
                Until(() => runs[index].Thread.Status == ThreadStatus.Waiting, "the wait");
            }

            var writer = new SnapshotWriter();
            lock (sema)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelSignalSema", (uint)uid, 2u)); // serves the waiter for 2
                sema.Save(writer);
            }
            Assert.Equal(0, Call(Tm, "sceKernelCancelSema", uid, -1, new Ptr<int>(0)));
            restored.Load(new SnapshotReader(writer.Data.ToArray()));
            Assert.Equal((0, 2), restored.Info());
            for (int i = 2; i >= 0; i--)
            {
                int index = i;
                Until(() => runs[index].Thread.Status == ThreadStatus.Waiting && runs[index].Thread.CurrentWaitSync == Sema(gates[index]), "the gate");
                Assert.Equal(0, Call(Tm, "sceKernelSignalSema", gates[index], 1));
                Until(() => (runs[index].Thread.Status == ThreadStatus.Waiting && runs[index].Thread.CurrentWaitSync == restored) || !runs[index].Thread.IsLive,
                    "the reissued wait");
            }

            _h.Join(runs[0]); // served before the save
            Assert.Equal(0, second[0]);
            lock (restored)
            {
                Assert.Equal(SceError.OK, restored.Signal(1));
                Assert.Equal((0, 1), restored.Info());
            }
            _h.Join(runs[1]);
            Assert.Equal(ThreadStatus.Waiting, runs[2].Thread.Status);
            Assert.Equal(SceError.OK, restored.Signal(1));
            _h.Join(runs[2]);
            Assert.Equal([0, SceError.WAIT_CANCEL, SceError.WAIT_CANCEL], first);
            Assert.Equal([0, 0, 0], second);
        });
    }

    /// <summary>A semaphore record of a build before the saved queue (no trailing queue) loads with an empty queue and
    /// its count, and its waits are reissued as new ones.</summary>
    [Fact]
    public void ASemaphoreRecordWithoutAQueueLoads()
    {
        var writer = new SnapshotWriter();
        writer.Write(Semaphore.AttrThreadPriority);
        writer.Write(1);
        writer.Write(3);
        writer.Write(5);
        writer.Write(false);
        var restored = new Semaphore { Name = "legacy" };
        restored.Load(new SnapshotReader(writer.Data.ToArray()));
        Assert.Equal((Semaphore.AttrThreadPriority, 1, 3, 5), (restored.Attr, restored.InitCount, restored.Count, restored.MaxCount));
        Assert.Equal((3, 0), restored.Info());
        Assert.Equal(SceError.OK, restored.Poll(3));
    }

    /// <summary>threadmgr refuses a need below 1 before it looks the UID up, and one above the maximum after it, in
    /// the wait and in the poll.</summary>
    [Fact]
    public void WaitAndPollRefuseANeedOutsideTheRange()
    {
        _h.OnGuest(_ =>
        {
            int uid = Call(Tm, "sceKernelCreateSema", "range", 0u, 2, 2, 0u);
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelPollSema", uid, 0));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelPollSema", uid, 3));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelWaitSema", uid, 0, new Ptr<uint>(0)));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelWaitSema", uid, 3, new Ptr<uint>(0)));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelPollSema", 0x7FFFFFF1, 0));
            Assert.Equal(SceError.INVALID_ARGUMENT, Call(Tm, "sceKernelWaitSema", 0x7FFFFFF1, 0, new Ptr<uint>(0)));
            Assert.Equal(SceError.UNKNOWN_SEMA_ID, Call(Tm, "sceKernelPollSema", 0x7FFFFFF1, 1));
            Assert.Equal(2, Sema(uid).Count);
        });
    }
}
