using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Tests;

/// <summary>
/// Normal mutexes under adversarial interleavings, against FW 3.74 threadmgr (the lock core behind sceKernelLockMutex
/// and LockMutexCB, TryLock, Unlock, Cancel and the mutex class, studied in the decompiled module): the wait queue is
/// first come first served or, with SCE_KERNEL_MUTEX_ATTR_TH_PRIO, by priority; the unlock that frees the mutex makes the
/// head of the queue the owner with the count it asked for, inside the call, so no caller takes it ahead of the queue;
/// a waiter whose timeout passes is off the queue at its guest-time deadline; a cancel and a delete release the whole
/// queue. The unlocker's view is read while the mutex's lock is held (the woken threads cannot run yet), so the result
/// is decided by the calls, never by the host's timing.
/// </summary>
[Collection("guest")]
public sealed class MutexInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "mutex-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private Mutex Mtx(int uid) => _h.Emu.Kernel.Objects.Get<Mutex>(uid)!;

    private const int HighPriority = 100, LowPriority = 150;

    /// <summary>Starts a thread that locks <paramref name="uid"/> with <paramref name="count"/>, records the result in
    /// <paramref name="results"/>[<paramref name="index"/>] and, once it owns the mutex, its index in
    /// <paramref name="order"/> before it unlocks; returns once it is in the queue.</summary>
    private GuestRun Waiter(int uid, int count, int[] results, int index, List<int> order, int priority = 0x10000100, uint timeout = 0)
    {
        GuestRun run = _h.Start(_ =>
        {
            results[index] = Call(Tm, "sceKernelLockMutex", uid, count, new Ptr<uint>(timeout));
            if (results[index] == 0)
            {
                lock (order)
                {
                    order.Add(index);
                }

                Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", uid, count));
            }
        }, $"mutex-waiter-{index}", priority);
        Until(() => run.Thread.Status == ThreadStatus.Waiting, $"waiter {index} in the queue");
        return run;
    }

    /// <summary>currentCount, currentOwnerId and numWaitThreads as sceKernelGetMutexInfo reports them.</summary>
    private (int Count, int Owner, int Waiters) Info(GuestThread main, int uid)
    {
        uint info = _h.Alloc(0x3C);
        GuestMemory.W32(info, 0x3C);
        Assert.Equal(0u, main.DispatchForTest("sceKernelGetMutexInfo", (uint)uid, info));
        return ((int)GuestMemory.R32(info + 0x30), (int)GuestMemory.R32(info + 0x34), (int)GuestMemory.R32(info + 0x38));
    }

    /// <summary>
    /// The unlock that frees a FIFO mutex makes the first waiter the owner, with its count, inside the call: the
    /// unlocker's try-lock and lock right after it fail and queue, even though the new owner has not run yet, and the
    /// mutex goes round the waiters in the order they came.
    /// </summary>
    [Fact]
    public void AnUnlockHandsTheMutexToTheFirstWaiter()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateMutex", "fifo", Mutex.AttrRecursive, 1, 0u);
            Mutex mutex = Mtx(uid);
            int[] results = [1, 1, 1];
            List<int> order = [];
            GuestRun[] waiters = [Waiter(uid, 2, results, 0, order), Waiter(uid, 1, results, 1, order), Waiter(uid, 3, results, 2, order)];
            GuestRun again;
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockMutex", (uint)uid, 1u));
                Assert.Equal((2, waiters[0].Thread.Uid, 2), Info(main, uid));
                Assert.Equal(unchecked((uint)SceError.MUTEX_FAILED_TO_OWN), main.DispatchForTest("sceKernelTryLockMutex", (uint)uid, 1u));
                again = _h.Start(_ => Assert.Equal(0, Call(Tm, "sceKernelLockMutex", uid, 1, new Ptr<uint>(0))), "mutex-again");
            }
            foreach (GuestRun waiter in waiters)
            {
                _h.Join(waiter);
            }

            _h.Join(again);
            Assert.Equal([0, 0, 0], results);
            Assert.Equal([0, 1, 2], order);
            Assert.Equal((1, again.Thread.Uid, 0), Info(main, uid)); // it never unlocked
        });
    }

    /// <summary>
    /// With SCE_KERNEL_MUTEX_ATTR_TH_PRIO the unlock hands the mutex to the higher-priority waiter even though it came
    /// last; without the attribute to the one that came first. Equal priorities keep their arrival order.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APriorityMutexGoesToTheHigherPriorityWaiterFirst(bool threadPriority)
    {
        _h.OnGuest(main =>
        {
            uint attr = threadPriority ? Mutex.AttrThreadPriority : 0u;
            int uid = Call(Tm, "sceKernelCreateMutex", threadPriority ? "th-prio" : "th-fifo", attr, 1, 0u);
            Mutex mutex = Mtx(uid);
            int[] results = [1, 1, 1];
            List<int> order = [];
            GuestRun low = Waiter(uid, 1, results, 0, order, LowPriority);
            GuestRun highFirst = Waiter(uid, 1, results, 1, order, HighPriority);
            GuestRun highSecond = Waiter(uid, 1, results, 2, order, HighPriority);
            GuestRun first = threadPriority ? highFirst : low;
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockMutex", (uint)uid, 1u));
                Assert.Equal((1, first.Thread.Uid, 2), Info(main, uid));
            }
            _h.Join(low);
            _h.Join(highFirst);
            _h.Join(highSecond);
            Assert.Equal([0, 0, 0], results);
            Assert.Equal(threadPriority ? [1, 2, 0] : [0, 1, 2], order);
        });
    }

    /// <summary>
    /// Counts as threadmgr checks them, after the UID: below 1, or above 1 without the recursive attribute, is
    /// ILLEGAL_COUNT in the lock, try-lock and unlock; the owner's own lock of a plain mutex is MUTEX_RECURSIVE, an
    /// unlock past its count MUTEX_UNLOCK_UDF, another thread's MUTEX_NOT_OWNED. A recursive owner's partial unlock
    /// keeps the mutex (the waiter goes on waiting), and the one that brings it to 0 hands it over.
    /// </summary>
    [Fact]
    public void CountsOwnershipAndRecursion()
    {
        _h.OnGuest(main =>
        {
            int plain = Call(Tm, "sceKernelCreateMutex", "plain", 0u, 1, 0u);
            Assert.Equal(SceError.UNKNOWN_MUTEX_ID, Call(Tm, "sceKernelLockMutex", 0x7FFFFFF1, 0, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelLockMutex", plain, 0, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelLockMutex", plain, 2, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelTryLockMutex", plain, 0));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelUnlockMutex", plain, 2));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelUnlockMutex", plain, 0));
            Assert.Equal(SceError.MUTEX_RECURSIVE, Call(Tm, "sceKernelLockMutex", plain, 1, new Ptr<uint>(0)));
            Assert.Equal(SceError.MUTEX_RECURSIVE, Call(Tm, "sceKernelTryLockMutex", plain, 1));

            int uid = Call(Tm, "sceKernelCreateMutex", "recursive", Mutex.AttrRecursive, 2, 0u);
            Mutex mutex = Mtx(uid);
            Assert.Equal(0, Call(Tm, "sceKernelLockMutex", uid, 2, new Ptr<uint>(0)));
            Assert.Equal(SceError.MUTEX_UNLOCK_UDF, Call(Tm, "sceKernelUnlockMutex", uid, 5));
            int[] results = [1];
            List<int> order = [];
            GuestRun waiter = Waiter(uid, 3, results, 0, order);
            GuestRun stranger = _h.Start(_ => Assert.Equal(SceError.MUTEX_NOT_OWNED, Call(Tm, "sceKernelUnlockMutex", uid, 1)), "mutex-stranger");
            _h.Join(stranger);
            Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", uid, 3));
            Assert.Equal((1, main.Uid, 1), Info(main, uid));
            Assert.Equal(ThreadStatus.Waiting, waiter.Thread.Status);
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockMutex", (uint)uid, 1u));
                Assert.Equal((3, waiter.Thread.Uid, 0), Info(main, uid));
            }
            _h.Join(waiter);
            Assert.Equal([0], results);
            Assert.Equal((0, 0, 0), Info(main, uid));
        });
    }

    /// <summary>
    /// A cancel takes every waiter off the queue (numWait: how many) without the mutex and gives the count to the caller;
    /// a negative count is the initial one, and a count above 1 on a plain mutex is ILLEGAL_COUNT with nothing changed.
    /// An unlock that comes before the cancelled threads run hands nothing to them.
    /// </summary>
    [Fact]
    public void ACancelReleasesTheQueueWithoutHandingItOver()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateMutex", "cancel", 0u, 1, 0u);
            Mutex mutex = Mtx(uid);
            int[] results = [1, 1];
            List<int> order = [];
            GuestRun first = Waiter(uid, 1, results, 0, order);
            GuestRun second = Waiter(uid, 1, results, 1, order);
            uint numWait = _h.Word(0xDEAD);
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelCancelMutex", uid, 2, new Ptr<int>(numWait)));
            Assert.Equal(0xDEADu, GuestMemory.R32(numWait));
            Assert.Equal((1, main.Uid, 2), Info(main, uid));
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelCancelMutex", (uint)uid, unchecked((uint)-5), numWait));
                Assert.Equal((1, main.Uid, 0), Info(main, uid)); // -5: the initial count, to the caller
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockMutex", (uint)uid, 1u));
                Assert.Equal((0, 0, 0), Info(main, uid));
            }
            _h.Join(first);
            _h.Join(second);
            Assert.Equal(2u, GuestMemory.R32(numWait));
            Assert.Equal([SceError.WAIT_CANCEL, SceError.WAIT_CANCEL], results);
            Assert.Empty(order);
            Assert.Equal(0, Call(Tm, "sceKernelCancelMutex", uid, 0, new Ptr<int>(numWait)));
            Assert.Equal((0, 0, 0), Info(main, uid));
        });
    }

    /// <summary>A deleted mutex's waiters return WAIT_DELETE whenever their threads run; the UID is gone for every call
    /// after it.</summary>
    [Fact]
    public void ADeleteReleasesTheQueue()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateMutex", "delete", 0u, 1, 0u);
            Mutex mutex = Mtx(uid);
            int[] results = [1, 1];
            List<int> order = [];
            GuestRun first = Waiter(uid, 1, results, 0, order);
            GuestRun second = Waiter(uid, 1, results, 1, order);
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelDeleteMutex", (uint)uid));
                Assert.Equal(unchecked((uint)SceError.UNKNOWN_MUTEX_ID), main.DispatchForTest("sceKernelUnlockMutex", (uint)uid, 1u));
            }
            _h.Join(first);
            _h.Join(second);
            Assert.Equal([SceError.WAIT_DELETE, SceError.WAIT_DELETE], results);
            Assert.Empty(order);
        });
    }

    /// <summary>
    /// A lock's timeout is the kernel's timer at a guest-time deadline: once it has passed, the waiter is off the queue,
    /// and an unlock skips it for the waiter behind it (or leaves the mutex free), however late the timed-out thread is
    /// to notice. It returns WAIT_TIMEOUT with no time left.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnlockAfterTheDeadlineSkipsTheTimedOutWaiter(bool waiterBehind)
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            const uint LimitUs = 50_000;
            int uid = Call(Tm, "sceKernelCreateMutex", "deadline", 0u, 1, 0u);
            Mutex mutex = Mtx(uid);
            uint timeout = _h.Word(LimitUs);
            int[] results = [1, 1];
            List<int> order = [];
            GuestRun late = _h.Start(_ => results[0] = Call(Tm, "sceKernelLockMutex", uid, 1, new Ptr<uint>(timeout)), "mutex-late");
            Until(() => late.Thread.Status == ThreadStatus.Waiting, "the timed wait");
            GuestRun? behind = waiterBehind ? Waiter(uid, 1, results, 1, order) : null;
            lock (mutex)
            {
                clock.Advance(LimitUs - 1);
                Assert.Equal((1, main.Uid, waiterBehind ? 2 : 1), Info(main, uid)); // still queued a microsecond before
                clock.Advance(1);
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockMutex", (uint)uid, 1u));
                Assert.Equal(waiterBehind ? (1, behind!.Thread.Uid, 0) : (0, 0, 0), Info(main, uid));
            }
            _h.Join(late);
            if (behind != null)
            {
                _h.Join(behind);
                Assert.Equal([1], order);
            }

            Assert.Equal(SceError.WAIT_TIMEOUT, results[0]);
            Assert.Equal(0u, GuestMemory.R32(timeout));
            Assert.Equal((0, 0, 0), Info(main, uid));
        });
    }

    /// <summary>
    /// A save state keeps the mutex's queue: a waiter an unlock made the owner that had not returned when the state was
    /// saved returns OK when its lock is reissued (it owns the restored mutex, instead of finding itself the owner,
    /// MUTEX_RECURSIVE), and the waiters still queued keep their saved order however the restored threads happen to
    /// reissue their locks (here in reverse).
    /// </summary>
    [Fact]
    public void ASaveStateKeepsTheQueueOrderAndTheNewOwner()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateMutex", "saved", 0u, 1, 0u);
            Mutex mutex = Mtx(uid);
            Mutex restored = new() { Name = "saved" };
            int[] first = [1, 1, 1];
            int[] second = [1, 1, 1];
            int[] gates = new int[3];
            List<int> order = [];
            var runs = new GuestRun[3];
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                gates[i] = Call(Tm, "sceKernelCreateSema", $"gate-{i}", 0u, 0, 1, 0u);
                runs[i] = _h.Start(self =>
                {
                    first[index] = Call(Tm, "sceKernelLockMutex", uid, 1, new Ptr<uint>(0));
                    Assert.Equal(0, Call(Tm, "sceKernelWaitSema", gates[index], 1, new Ptr<uint>(0))); // the reissue comes when main says
                    second[index] = restored.Lock(self, 1, new Ptr<uint>(0));
                    if (second[index] == 0)
                    {
                        lock (order)
                        {
                            order.Add(index);
                        }

                        Assert.Equal(SceError.OK, restored.Unlock(self, 1));
                    }
                }, $"mutex-saved-{i}");
                Until(() => runs[index].Thread.Status == ThreadStatus.Waiting, "the lock");
            }

            var writer = new SnapshotWriter();
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockMutex", (uint)uid, 1u)); // hands it to the first
                mutex.Save(writer);
            }
            Assert.Equal(0, Call(Tm, "sceKernelCancelMutex", uid, 0, new Ptr<int>(0)));
            restored.Load(new SnapshotReader(writer.Data.ToArray()));
            restored.Resolve(_h.Emu.Kernel.Objects);
            Assert.Equal((1, 2, runs[0].Thread), restored.Info());
            for (int i = 2; i >= 0; i--)
            {
                int index = i;
                Until(() => runs[index].Thread.Status == ThreadStatus.Waiting && runs[index].Thread.CurrentWaitSync == _h.Emu.Kernel.Objects.Get<VitaSharp.Core.Kernel.Semaphore>(gates[index]), "the gate");
                Assert.Equal(0, Call(Tm, "sceKernelSignalSema", gates[index], 1));
                if (index > 0)
                {
                    Until(() => runs[index].Thread.Status == ThreadStatus.Waiting && runs[index].Thread.CurrentWaitSync == restored, "the reissued lock");
                }
            }

            foreach (GuestRun run in runs)
            {
                _h.Join(run);
            }

            Assert.Equal([0, SceError.WAIT_CANCEL, SceError.WAIT_CANCEL], first);
            Assert.Equal([0, 0, 0], second);
            Assert.Equal([0, 1, 2], order);
            Assert.Equal<(int, int, GuestThread?)>((0, 0, null), restored.Info());
        });
    }

    /// <summary>A mutex record of a build before the saved queue (no trailing queue) loads with an empty queue, its
    /// owner and count, and its locks are reissued as new ones.</summary>
    [Fact]
    public void AMutexRecordWithoutAQueueLoads()
    {
        var writer = new SnapshotWriter();
        writer.Write(Mutex.AttrRecursive);
        writer.Write(1);
        writer.Write(0); // no owner
        writer.Write(0);
        writer.Write(0u);
        writer.Write(false);
        var restored = new Mutex { Name = "legacy" };
        restored.Load(new SnapshotReader(writer.Data.ToArray()));
        restored.Resolve(new ObjectTable());
        Assert.Equal((Mutex.AttrRecursive, 1), (restored.Attr, restored.InitCount));
        Assert.Equal((0, 0, null), restored.Info());
    }

    /// <summary>
    /// A condition wait taking its mutex back that an unlock made the owner, but that had not returned, is saved as the
    /// cond wait it reissues (which takes the mutex itself): the saved mutex is free and keeps no waiter for it.
    /// </summary>
    [Fact]
    public void ASaveStateDoesNotKeepACondWaitsReacquire()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateMutex", "cond-mutex", 0u, 0, 0u);
            int cond = Call(Tm, "sceKernelCreateCond", "cond", 0u, uid, 0u);
            Mutex mutex = Mtx(uid);
            int result = 1;
            GuestRun waiter = _h.Start(_ =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelLockMutex", uid, 1, new Ptr<uint>(0)));
                result = Call(Tm, "sceKernelWaitCond", cond, new Ptr<uint>(0));
                Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", uid, 1));
            }, "cond-waiter");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && waiter.Thread.CurrentWaitSync is CondVar, "the cond wait");
            Assert.Equal(0, Call(Tm, "sceKernelLockMutex", uid, 1, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelSignalCond", cond));
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && waiter.Thread.CurrentWaitSync == mutex, "the reacquire");
            var writer = new SnapshotWriter();
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockMutex", (uint)uid, 1u));
                Assert.Equal((1, waiter.Thread.Uid, 0), Info(main, uid));
                mutex.Save(writer);
            }
            _h.Join(waiter);
            Assert.Equal(0, result);
            var restored = new Mutex { Name = "cond-mutex" };
            restored.Load(new SnapshotReader(writer.Data.ToArray()));
            restored.Resolve(_h.Emu.Kernel.Objects);
            Assert.Equal((0, 0, null), restored.Info());
        });
    }
}
