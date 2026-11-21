using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Tests;

/// <summary>
/// Condition variables under adversarial interleavings, against FW 3.74 threadmgr (sceKernelWaitCond, Signal,
/// SignalAll, SignalTo and Delete, normal and lightweight, studied in the decompiled module): a wait unlocks its mutex
/// by 1 and queues, first come first served or, with SCE_KERNEL_COND_ATTR_TH_PRIO, by priority; a signal releases the
/// head; a waiter whose timeout passes is off the queue at its guest-time deadline (the test moves guest time) and the
/// signal goes to the next one; the released or timed-out waiter locks the mutex again by 1; a deleted cond's waiters
/// return WAIT_DELETE_COND (WAIT_DELETE_LW_COND) without the mutex. The signaller's view is read while the cond's lock
/// is held, so the result is decided by the calls, never by the host's timing.
/// </summary>
[Collection("guest")]
public sealed class CondInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "cond-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private const int HighPriority = 100, LowPriority = 150;

    private CondVar Cond(int uid) => _h.Emu.Kernel.Objects.Get<CondVar>(uid)!;

    private Mutex Mtx(int uid) => _h.Emu.Kernel.Objects.Get<Mutex>(uid)!;

    /// <summary>Starts a thread that locks <paramref name="mutex"/>, waits on <paramref name="cond"/>, records the
    /// result in <paramref name="results"/>[<paramref name="index"/>] and, holding the mutex again, its index in
    /// <paramref name="order"/>, then unlocks; returns once it waits on the cond.</summary>
    private GuestRun Waiter(int mutex, int cond, int[] results, int index, List<int> order, int priority = 0x10000100, uint timeout = 0)
    {
        GuestRun run = _h.Start(_ =>
        {
            Assert.Equal(0, Call(Tm, "sceKernelLockMutex", mutex, 1, new Ptr<uint>(0)));
            results[index] = Call(Tm, "sceKernelWaitCond", cond, new Ptr<uint>(timeout));
            lock (order)
            {
                order.Add(index);
            }

            Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", mutex, 1));
        }, $"cond-waiter-{index}", priority);
        Until(() => run.Thread.Status == ThreadStatus.Waiting && run.Thread.CurrentWaitSync is CondVar, $"waiter {index} on the cond");
        return run;
    }

    /// <summary>numWaitThreads as sceKernelGetCondInfo reports it.</summary>
    private int Waiting(GuestThread main, int cond)
    {
        uint info = _h.Alloc(0x34);
        GuestMemory.W32(info, 0x34);
        Assert.Equal(0u, main.DispatchForTest("sceKernelGetCondInfo", (uint)cond, info));
        return (int)GuestMemory.R32(info + 0x30);
    }

    /// <summary>
    /// Each signal releases the head of the queue: with SCE_KERNEL_COND_ATTR_TH_PRIO the higher-priority waiters first
    /// (in arrival order among equal priorities), without it in arrival order. A signal with nobody waiting is 0 and is
    /// not kept for a later wait.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASignalReleasesTheHeadOfTheQueue(bool threadPriority)
    {
        _h.OnGuest(main =>
        {
            int mutex = Call(Tm, "sceKernelCreateMutex", "cond-mutex", 0u, 0, 0u);
            int cond = Call(Tm, "sceKernelCreateCond", "cond", threadPriority ? CondVar.AttrThreadPriority : 0u, mutex, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelSignalCond", cond));
            int[] results = [1, 1, 1];
            List<int> order = [];
            GuestRun[] waiters = [Waiter(mutex, cond, results, 0, order, LowPriority), Waiter(mutex, cond, results, 1, order, HighPriority),
                Waiter(mutex, cond, results, 2, order, HighPriority)];
            for (int served = 0; served < 3; served++)
            {
                lock (Cond(cond))
                {
                    Assert.Equal(0u, main.DispatchForTest("sceKernelSignalCond", (uint)cond));
                    Assert.Equal(2 - served, Waiting(main, cond));
                }
                Until(() => Count(order) == served + 1, "the released waiter");
            }

            foreach (GuestRun waiter in waiters)
            {
                _h.Join(waiter);
            }

            Assert.Equal([0, 0, 0], results);
            Assert.Equal(threadPriority ? [1, 2, 0] : [0, 1, 2], order);
        });
    }

    private static int Count(List<int> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    /// <summary>
    /// A waiter whose timeout passed is off the queue: the signal after the deadline releases the waiter behind it, and
    /// the timed-out one returns WAIT_TIMEOUT with no time left, holding the mutex again. A waiter released before its
    /// deadline writes back the time that was left at the signal, however late its host thread runs.
    /// </summary>
    [Fact]
    public void ASignalAfterTheDeadlineGoesToTheNextWaiter()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            const uint LimitUs = 50_000, BeforeSignalUs = 20_000;
            int mutex = Call(Tm, "sceKernelCreateMutex", "cond-mutex", 0u, 0, 0u);
            int cond = Call(Tm, "sceKernelCreateCond", "deadline", 0u, mutex, 0u);
            uint lateTimeout = _h.Word(LimitUs), nextTimeout = _h.Word(LimitUs * 2);
            int[] results = [1, 1];
            List<int> order = [];
            GuestRun late = Waiter(mutex, cond, results, 0, order, timeout: lateTimeout);
            GuestRun next = Waiter(mutex, cond, results, 1, order, timeout: nextTimeout);
            lock (Cond(cond))
            {
                clock.Advance(LimitUs - 1);
                Assert.Equal(2, Waiting(main, cond)); // a microsecond before the deadline both wait
                clock.Advance(1);
                Assert.Equal(1, Waiting(main, cond));
                clock.Advance(BeforeSignalUs);
                Assert.Equal(0u, main.DispatchForTest("sceKernelSignalCond", (uint)cond));
                Assert.Equal(0, Waiting(main, cond));
                clock.Advance(LimitUs * 2); // the released thread runs past its own deadline
            }
            _h.Join(late);
            _h.Join(next);
            Assert.Equal([SceError.WAIT_TIMEOUT, 0], results);
            Assert.Equal(0u, GuestMemory.R32(lateTimeout));
            Assert.Equal(LimitUs * 2 - LimitUs - BeforeSignalUs, GuestMemory.R32(nextTimeout));
            Assert.Equal(2, Count(order)); // both held the mutex again
        });
    }

    /// <summary>A deleted cond's waiters return WAIT_DELETE_COND (WAIT_DELETE_LW_COND for a lightweight one) without
    /// locking the mutex again, and a signal right after the delete reaches none of them.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeleteReleasesTheWaitersWithoutTheMutex(bool light)
    {
        _h.OnGuest(main =>
        {
            uint mutexWork = _h.Alloc(32), condWork = _h.Alloc(32);
            int mutex = 0, cond = 0;
            if (light)
            {
                Assert.Equal(0, Call(Tm, "sceKernelCreateLwMutex", mutexWork, "lw-cond-mutex", 0u, 0, 0u));
                Assert.Equal(0, Call(Tm, "sceKernelCreateLwCond", condWork, "lw-cond", 0u, mutexWork, 0u));
            }
            else
            {
                mutex = Call(Tm, "sceKernelCreateMutex", "cond-mutex", 0u, 0, 0u);
                cond = Call(Tm, "sceKernelCreateCond", "cond", 0u, mutex, 0u);
            }

            CondVar condVar = light ? Cond((int)GuestMemory.R32(condWork)) : Cond(cond);
            Mutex mutexObject = condVar.Mutex;
            int[] results = [1, 1];
            var runs = new GuestRun[2];
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                runs[i] = _h.Start(_ =>
                {
                    if (light)
                    {
                        Assert.Equal(0, Call(Tm, "sceKernelLockLwMutex", mutexWork, 1, new Ptr<uint>(0)));
                        results[index] = Call(Tm, "sceKernelWaitLwCond", condWork, new Ptr<uint>(0));
                    }
                    else
                    {
                        Assert.Equal(0, Call(Tm, "sceKernelLockMutex", mutex, 1, new Ptr<uint>(0)));
                        results[index] = Call(Tm, "sceKernelWaitCond", cond, new Ptr<uint>(0));
                    }
                }, $"cond-deleted-{i}");
                Until(() => runs[index].Thread.Status == ThreadStatus.Waiting && runs[index].Thread.CurrentWaitSync is CondVar, "the cond wait");
            }

            lock (condVar)
            {
                if (light)
                {
                    Assert.Equal(0u, main.DispatchForTest("sceKernelDeleteLwCond", condWork));
                }
                else
                {
                    Assert.Equal(0u, main.DispatchForTest("sceKernelDeleteCond", (uint)cond));
                }
            }
            _h.Join(runs[0]);
            _h.Join(runs[1]);
            int expected = light ? SceError.WAIT_DELETE_LW_COND : SceError.WAIT_DELETE_COND;
            Assert.Equal([expected, expected], results);
            Assert.Equal((0, 0, null), mutexObject.Info()); // nobody took the mutex back
        });
    }

    /// <summary>
    /// A wait unlocks its mutex by 1, not completely: a recursive owner with a count of 2 keeps the mutex (count 1) while
    /// it waits, and locks it again by 1 when released (count 2). A caller that does not own the mutex gets
    /// MUTEX_NOT_OWNED (LW_MUTEX_NOT_OWNED for a lightweight cond) and does not wait.
    /// </summary>
    [Fact]
    public void AWaitUnlocksItsMutexByOne()
    {
        _h.OnGuest(main =>
        {
            int mutex = Call(Tm, "sceKernelCreateMutex", "recursive", Mutex.AttrRecursive, 0, 0u);
            int cond = Call(Tm, "sceKernelCreateCond", "cond", 0u, mutex, 0u);
            Assert.Equal(SceError.MUTEX_NOT_OWNED, Call(Tm, "sceKernelWaitCond", cond, new Ptr<uint>(0)));
            uint mutexWork = _h.Alloc(32), condWork = _h.Alloc(32);
            Assert.Equal(0, Call(Tm, "sceKernelCreateLwMutex", mutexWork, "lw-mutex", 0u, 0, 0u));
            Assert.Equal(0, Call(Tm, "sceKernelCreateLwCond", condWork, "lw-cond", 0u, mutexWork, 0u));
            Assert.Equal(SceError.LW_MUTEX_NOT_OWNED, Call(Tm, "sceKernelWaitLwCond", condWork, new Ptr<uint>(0)));

            int result = 1;
            (int, int, GuestThread?) after = default;
            GuestRun waiter = _h.Start(self =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelLockMutex", mutex, 2, new Ptr<uint>(0)));
                result = Call(Tm, "sceKernelWaitCond", cond, new Ptr<uint>(0));
                after = Mtx(mutex).Info();
                Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", mutex, 2));
            }, "cond-recursive");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && waiter.Thread.CurrentWaitSync is CondVar, "the cond wait");
            Assert.Equal((1, 0, waiter.Thread), Mtx(mutex).Info()); // still held by the waiter, by 1
            Assert.Equal(0, Call(Tm, "sceKernelSignalCond", cond));
            _h.Join(waiter);
            Assert.Equal(0, result);
            Assert.Equal((2, 0, waiter.Thread), after);
        });
    }

    /// <summary>sceKernelSignalCondTo (threadmgr 3.74): 0 or the caller's own UID is ILLEGAL_THREAD_ID, before the cond
    /// is looked up; an unknown thread is UNKNOWN_THREAD_ID; a thread that does not wait on the cond ILLEGAL_THREAD_ID;
    /// a waiting one is released, even behind others.</summary>
    [Fact]
    public void SignalToChecksTheThread()
    {
        _h.OnGuest(main =>
        {
            int mutex = Call(Tm, "sceKernelCreateMutex", "cond-mutex", 0u, 0, 0u);
            int cond = Call(Tm, "sceKernelCreateCond", "cond", 0u, mutex, 0u);
            Assert.Equal(SceError.ILLEGAL_THREAD_ID, Call(Tm, "sceKernelSignalCondTo", 0x7FFFFFF1, 0));
            Assert.Equal(SceError.ILLEGAL_THREAD_ID, Call(Tm, "sceKernelSignalCondTo", 0x7FFFFFF1, main.Uid));
            Assert.Equal(SceError.UNKNOWN_THREAD_ID, Call(Tm, "sceKernelSignalCondTo", cond, 0x7FFFFFF1));
            int[] results = [1, 1];
            List<int> order = [];
            GuestRun first = Waiter(mutex, cond, results, 0, order);
            GuestRun second = Waiter(mutex, cond, results, 1, order);
            int gate = Call(Tm, "sceKernelCreateSema", "gate", 0u, 0, 1, 0u);
            GuestRun other = _h.Start(_ => Assert.Equal(0, Call(Tm, "sceKernelWaitSema", gate, 1, new Ptr<uint>(0))), "cond-other");
            Until(() => other.Thread.Status == ThreadStatus.Waiting, "the other wait");
            Assert.Equal(SceError.ILLEGAL_THREAD_ID, Call(Tm, "sceKernelSignalCondTo", cond, other.Thread.Uid));
            Assert.Equal(0, Call(Tm, "sceKernelSignalSema", gate, 1));
            _h.Join(other);
            lock (Cond(cond))
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelSignalCondTo", (uint)cond, (uint)second.Thread.Uid));
                Assert.Equal(1, Waiting(main, cond));
            }
            _h.Join(second);
            Assert.Equal(ThreadStatus.Waiting, first.Thread.Status);
            Assert.Equal(0, Call(Tm, "sceKernelSignalCondAll", cond));
            _h.Join(first);
            Assert.Equal([0, 0], results);
            Assert.Equal([1, 0], order);
        });
    }
}
