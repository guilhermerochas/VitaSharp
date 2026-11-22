using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Tests;

/// <summary>
/// Lightweight mutexes under adversarial interleavings, against FW 3.74 (SceLibKernel's user-mode lock, try-lock and
/// unlock on the work area, and threadmgr's kernel side, studied in the decompiled modules): the kernel keeps a wait
/// queue, first come first served or, with SCE_KERNEL_MUTEX_ATTR_TH_PRIO, by priority; the unlock that brings the count
/// to 0 with waiters writes the head of the queue into the work area as the owner, with the count it asked for, inside
/// the call (the work area is never free in between, so no caller takes it ahead of the queue); a waiter whose timeout
/// passes is off the queue at its guest-time deadline; a delete releases the queue. A count below 1, or above 1 without
/// the recursive attribute, is ILLEGAL_COUNT. The unlocker's view is read while the mutex's lock is held (the woken
/// threads cannot run yet), so the result is decided by the calls, never by the host's timing.
/// </summary>
[Collection("guest")]
public sealed class LwMutexInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "lwmutex-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private const int HighPriority = 100, LowPriority = 150;

    /// <summary>A lightweight mutex in a fresh work area, owned by the caller with <paramref name="initCount"/>.</summary>
    private (uint Work, Mutex Mutex) Create(string name, uint attr, int initCount)
    {
        uint work = _h.Alloc(32);
        Assert.Equal(0, Call(Tm, "sceKernelCreateLwMutex", work, name, attr, initCount, 0u));
        return (work, _h.Emu.Kernel.Objects.Get<Mutex>((int)GuestMemory.R32(work + Mutex.LwUidOffset))!);
    }

    /// <summary>Starts a thread that locks <paramref name="work"/> with <paramref name="count"/>, records the result in
    /// <paramref name="results"/>[<paramref name="index"/>] and, once it owns the mutex, its index in
    /// <paramref name="order"/> before it unlocks; returns once it is in the queue.</summary>
    private GuestRun Waiter(uint work, int count, int[] results, int index, List<int> order, int priority = 0x10000100, uint timeout = 0)
    {
        GuestRun run = _h.Start(_ =>
        {
            results[index] = Call(Tm, "sceKernelLockLwMutex", work, count, new Ptr<uint>(timeout));
            if (results[index] == 0)
            {
                lock (order)
                {
                    order.Add(index);
                }

                Assert.Equal(0, Call(Tm, "sceKernelUnlockLwMutex", work, count));
            }
        }, $"lwmutex-waiter-{index}", priority);
        Until(() => run.Thread.Status == ThreadStatus.Waiting, $"waiter {index} in the queue");
        return run;
    }

    /// <summary>currentCount, the owner thread and numWaitThreads as sceKernelGetLwMutexInfo reports them.</summary>
    private (int Count, int Owner, int Waiters) Info(GuestThread main, uint work)
    {
        uint info = _h.Alloc(0x40);
        GuestMemory.W32(info, 0x40);
        Assert.Equal(0u, main.DispatchForTest("sceKernelGetLwMutexInfo", work, info));
        return ((int)GuestMemory.R32(info + 0x34), (int)GuestMemory.R32(info + 0x38), (int)GuestMemory.R32(info + 0x3C));
    }

    private static uint OwnerWord(uint work) => GuestMemory.R32(work + Mutex.LwOwnerOffset);

    /// <summary>
    /// The unlock that frees a FIFO lightweight mutex writes the first waiter into the work area as the owner, with its
    /// count, inside the call (flagged while more wait, so its unlock comes to the kernel too): the unlocker's try-lock
    /// right after it fails and a lock queues, even though the new owner has not run yet, and the mutex goes round the
    /// waiters in the order they came. The last one's owner word is not flagged (its unlock stays in guest code).
    /// </summary>
    [Fact]
    public void AnUnlockHandsTheMutexToTheFirstWaiter()
    {
        _h.OnGuest(main =>
        {
            (uint work, Mutex mutex) = Create("fifo", Mutex.AttrRecursive, 1);
            int[] results = [1, 1, 1];
            List<int> order = [];
            GuestRun[] waiters = [Waiter(work, 2, results, 0, order), Waiter(work, 1, results, 1, order), Waiter(work, 3, results, 2, order)];
            Assert.Equal((uint)main.Uid | Mutex.Contended, OwnerWord(work));
            GuestRun again;
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockLwMutex", work, 1u));
                Assert.Equal((2, waiters[0].Thread.Uid, 2), Info(main, work));
                Assert.Equal((uint)waiters[0].Thread.Uid | Mutex.Contended, OwnerWord(work));
                Assert.Equal(unchecked((uint)SceError.LW_MUTEX_FAILED_TO_OWN), main.DispatchForTest("sceKernelTryLockLwMutex", work, 1u));
                again = _h.Start(_ => Assert.Equal(0, Call(Tm, "sceKernelLockLwMutex", work, 1, new Ptr<uint>(0))), "lwmutex-again");
            }
            foreach (GuestRun waiter in waiters)
            {
                _h.Join(waiter);
            }

            _h.Join(again);
            Assert.Equal([0, 0, 0], results);
            Assert.Equal([0, 1, 2], order);
            Assert.Equal((1, again.Thread.Uid, 0), Info(main, work)); // it never unlocked
            Assert.Equal((uint)again.Thread.Uid, OwnerWord(work));
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
            (uint work, Mutex mutex) = Create(threadPriority ? "th-prio" : "th-fifo", threadPriority ? Mutex.AttrThreadPriority : 0u, 1);
            int[] results = [1, 1, 1];
            List<int> order = [];
            GuestRun low = Waiter(work, 1, results, 0, order, LowPriority);
            GuestRun highFirst = Waiter(work, 1, results, 1, order, HighPriority);
            GuestRun highSecond = Waiter(work, 1, results, 2, order, HighPriority);
            GuestRun first = threadPriority ? highFirst : low;
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockLwMutex", work, 1u));
                Assert.Equal((1, first.Thread.Uid, 2), Info(main, work));
            }
            _h.Join(low);
            _h.Join(highFirst);
            _h.Join(highSecond);
            Assert.Equal([0, 0, 0], results);
            Assert.Equal(threadPriority ? [1, 2, 0] : [0, 1, 2], order);
        });
    }

    /// <summary>
    /// Counts and ownership as FW 3.74 checks them. Lock and unlock (threadmgr): a count below 1, or above 1 without the
    /// recursive attribute, is ILLEGAL_COUNT before the owner is looked at. Try-lock (SceLibKernel): a count below 1 is
    /// ILLEGAL_COUNT, then a held mutex is the owner's recursion (LW_MUTEX_RECURSIVE) or LW_MUTEX_FAILED_TO_OWN, and a
    /// free one refuses a count above 1 without the attribute. A recursive owner adds counts up to LW_MUTEX_LOCK_OVF;
    /// its partial unlock keeps the mutex (the waiter goes on waiting) and the one that brings it to 0 hands it over.
    /// </summary>
    [Fact]
    public void CountsOwnershipAndRecursion()
    {
        _h.OnGuest(main =>
        {
            (uint plain, _) = Create("plain", 0u, 0);
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelLockLwMutex", plain, 0, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelLockLwMutex", plain, 2, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelTryLockLwMutex", plain, 0));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelTryLockLwMutex", plain, 2));
            Assert.Equal((0u, 0u), (OwnerWord(plain), GuestMemory.R32(plain + Mutex.LwCountOffset)));
            Assert.Equal(0, Call(Tm, "sceKernelTryLockLwMutex", plain, 1));
            Assert.Equal(SceError.LW_MUTEX_RECURSIVE, Call(Tm, "sceKernelTryLockLwMutex", plain, 2)); // held: the owner first
            Assert.Equal(SceError.LW_MUTEX_RECURSIVE, Call(Tm, "sceKernelLockLwMutex", plain, 1, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelLockLwMutex", plain, 2, new Ptr<uint>(0)));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelUnlockLwMutex", plain, 0));
            Assert.Equal(SceError.ILLEGAL_COUNT, Call(Tm, "sceKernelUnlockLwMutex", plain, 2));
            GuestRun other = _h.Start(_ =>
            {
                Assert.Equal(SceError.LW_MUTEX_FAILED_TO_OWN, Call(Tm, "sceKernelTryLockLwMutex", plain, 2));
                Assert.Equal(SceError.LW_MUTEX_NOT_OWNED, Call(Tm, "sceKernelUnlockLwMutex", plain, 1));
            }, "lwmutex-other");
            _h.Join(other);
            Assert.Equal(0, Call(Tm, "sceKernelUnlockLwMutex", plain, 1));

            (uint work, Mutex mutex) = Create("recursive", Mutex.AttrRecursive, 2);
            Assert.Equal(SceError.LW_MUTEX_LOCK_OVF, Call(Tm, "sceKernelLockLwMutex", work, int.MaxValue, new Ptr<uint>(0)));
            Assert.Equal(SceError.LW_MUTEX_LOCK_OVF, Call(Tm, "sceKernelTryLockLwMutex", work, int.MaxValue));
            Assert.Equal(SceError.LW_MUTEX_UNLOCK_UDF, Call(Tm, "sceKernelUnlockLwMutex", work, 5));
            int[] results = [1];
            List<int> order = [];
            GuestRun waiter = Waiter(work, 3, results, 0, order);
            Assert.Equal(0, Call(Tm, "sceKernelUnlockLwMutex", work, 1));
            Assert.Equal((1, main.Uid, 1), Info(main, work));
            Assert.Equal(ThreadStatus.Waiting, waiter.Thread.Status);
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockLwMutex", work, 1u));
                Assert.Equal((3, waiter.Thread.Uid, 0), Info(main, work));
            }
            _h.Join(waiter);
            Assert.Equal([0], results);
            Assert.Equal((0, 0, 0), Info(main, work));
            Assert.Equal(0u, OwnerWord(work));
        });
    }

    /// <summary>A deleted lightweight mutex's waiters return WAIT_DELETE whenever their threads run, an unlock right
    /// after it hands nothing to them, and the work area's UID is gone for every call after it.</summary>
    [Fact]
    public void ADeleteReleasesTheQueue()
    {
        _h.OnGuest(main =>
        {
            (uint work, Mutex mutex) = Create("delete", 0u, 1);
            int[] results = [1, 1];
            List<int> order = [];
            GuestRun first = Waiter(work, 1, results, 0, order);
            GuestRun second = Waiter(work, 1, results, 1, order);
            lock (mutex)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelDeleteLwMutex", work));
                Assert.Equal(0u, GuestMemory.R32(work + Mutex.LwUidOffset));
                Assert.Equal(unchecked((uint)SceError.UNKNOWN_LW_MUTEX_ID), main.DispatchForTest("sceKernelLockLwMutex", work, 1u, 0u));
            }
            _h.Join(first);
            _h.Join(second);
            Assert.Equal([SceError.WAIT_DELETE, SceError.WAIT_DELETE], results);
            Assert.Empty(order);
        });
    }

    /// <summary>
    /// A lock's timeout is the kernel's timer at a guest-time deadline (the test moves guest time): once it has passed,
    /// the waiter is off the queue, and an unlock skips it for the waiter behind it (or leaves the mutex free, the owner
    /// word 0), however late the timed-out thread is to notice. It returns WAIT_TIMEOUT with no time left.
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
            (uint work, Mutex mutex) = Create("deadline", 0u, 1);
            uint timeout = _h.Word(LimitUs);
            int[] results = [1, 1];
            List<int> order = [];
            GuestRun late = _h.Start(_ => results[0] = Call(Tm, "sceKernelLockLwMutex", work, 1, new Ptr<uint>(timeout)), "lwmutex-late");
            Until(() => late.Thread.Status == ThreadStatus.Waiting, "the timed wait");
            GuestRun? behind = waiterBehind ? Waiter(work, 1, results, 1, order) : null;
            lock (mutex)
            {
                clock.Advance(LimitUs - 1);
                Assert.Equal((1, main.Uid, waiterBehind ? 2 : 1), Info(main, work)); // still queued a microsecond before
                clock.Advance(1);
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockLwMutex", work, 1u));
                Assert.Equal(waiterBehind ? (1, behind!.Thread.Uid, 0) : (0, 0, 0), Info(main, work));
                Assert.Equal(waiterBehind ? (uint)behind!.Thread.Uid : 0u, OwnerWord(work));
            }
            _h.Join(late);
            if (behind != null)
            {
                _h.Join(behind);
                Assert.Equal([1], order);
            }

            Assert.Equal(SceError.WAIT_TIMEOUT, results[0]);
            Assert.Equal(0u, GuestMemory.R32(timeout));
            Assert.Equal((0, 0, 0), Info(main, work));
        });
    }

    /// <summary>A lock that waited and was handed the mutex before its deadline writes back the time that was left at
    /// the unlock, however late its host thread runs.</summary>
    [Fact]
    public void AHandOverWritesBackTheTimeLeftAtTheUnlock()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            const uint LimitUs = 50_000, BeforeUnlockUs = 20_000;
            (uint work, Mutex mutex) = Create("time-left", 0u, 1);
            uint timeout = _h.Word(LimitUs);
            int result = 1;
            GuestRun waiter = _h.Start(_ =>
            {
                result = Call(Tm, "sceKernelLockLwMutex", work, 1, new Ptr<uint>(timeout));
                Assert.Equal(0, Call(Tm, "sceKernelUnlockLwMutex", work, 1));
            }, "lwmutex-time-left");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting, "the timed wait");
            lock (mutex)
            {
                clock.Advance(BeforeUnlockUs);
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockLwMutex", work, 1u));
                clock.Advance(LimitUs); // the waiter's thread runs past its deadline
            }
            _h.Join(waiter);
            Assert.Equal(0, result);
            Assert.Equal(LimitUs - BeforeUnlockUs, GuestMemory.R32(timeout));
        });
    }

    /// <summary>
    /// A save state keeps the lightweight mutex's queue: restored, the owner word is flagged again (the owner's unlock
    /// must come to the kernel to hand the mutex over), the waiters keep their order, and an unlock before they reissue
    /// their locks makes the head the owner in the work area.
    /// </summary>
    [Fact]
    public void ASaveStateKeepsTheQueueAndFlagsTheOwnerWord()
    {
        _h.OnGuest(main =>
        {
            (uint work, Mutex mutex) = Create("saved", 0u, 1);
            int[] results = [1, 1];
            List<int> order = [];
            GuestRun first = Waiter(work, 1, results, 0, order);
            GuestRun second = Waiter(work, 1, results, 1, order);
            var writer = new SnapshotWriter();
            lock (mutex)
            {
                mutex.Save(writer);
            }

            var restored = new Mutex { Name = "saved" };
            GuestMemory.W32(work + Mutex.LwOwnerOffset, (uint)main.Uid); // the memory of the state, without the flag
            restored.Load(new SnapshotReader(writer.Data.ToArray()));
            restored.Resolve(_h.Emu.Kernel.Objects);
            Assert.Equal((uint)main.Uid | Mutex.Contended, OwnerWord(work));
            Assert.Equal((1, 2, main), restored.Info());
            lock (restored)
            {
                Assert.Equal(SceError.OK, restored.Unlock(main, 1));
                Assert.Equal((1, 1, first.Thread), restored.Info());
                Assert.Equal((uint)first.Thread.Uid | Mutex.Contended, OwnerWord(work));
            }

            // the live mutex shares the work area: put it back for the threads still waiting on it
            GuestMemory.W32(work + Mutex.LwOwnerOffset, (uint)main.Uid | Mutex.Contended);
            Assert.Equal(0, Call(Tm, "sceKernelUnlockLwMutex", work, 1));
            _h.Join(first);
            _h.Join(second);
            Assert.Equal([0, 0], results);
            Assert.Equal([0, 1], order);
        });
    }
}
