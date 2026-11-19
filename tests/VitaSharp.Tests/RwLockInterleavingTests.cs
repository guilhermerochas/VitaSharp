using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Reader-writer locks under adversarial interleavings, against FW 3.74 threadmgr (the lock 0x81024bb0, the try-locks
/// 0x810254f0 / 0x81025758, the unlocks 0x810255b4 / 0x81025888, cancel 0x81025a50 and the class's wait-release
/// handler 0x810249b8, studied in the decompiled module): one queue of readers and writers, FIFO or by priority with
/// SCE_KERNEL_RW_LOCK_ATTR_TH_PRIO; a waiting writer keeps new readers out; the unlock that frees the lock hands it,
/// inside the call, to the first waiting writer (past the readers ahead of it) or, with no writer waiting, to every
/// reader; a waiter whose timeout passes leaves the queue at its guest-time deadline and the others are served. The
/// unlocker's view is read while the lock object is held (the woken threads cannot run yet), so the result is decided
/// by the calls, never by the host's timing.
/// </summary>
[Collection("guest")]
public sealed class RwLockInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "rwlock-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private RwLock Lock(int uid) => _h.Emu.Kernel.Objects.Get<RwLock>(uid)!;

    private const int HighPriority = 100, LowPriority = 150;

    /// <summary>Starts a thread that locks <paramref name="uid"/> for reading or writing, records the result in
    /// <paramref name="results"/>[<paramref name="index"/>] and, once it holds the lock, its index in
    /// <paramref name="order"/>; it unlocks once <paramref name="release"/> is set (null: at once). Returns once it is
    /// in the queue.</summary>
    private GuestRun Waiter(int uid, bool write, int[] results, int index, List<int> order, int priority = 0x10000100, uint timeout = 0, ManualResetEventSlim? release = null)
    {
        GuestRun run = _h.Start(_ =>
        {
            results[index] = Call(Tm, write ? "sceKernelLockWriteRWLock" : "sceKernelLockReadRWLock", uid, new Ptr<uint>(timeout));
            if (results[index] != 0)
            {
                return;
            }

            lock (order)
            {
                order.Add(index);
            }

            release?.Wait();
            Assert.Equal(0, Call(Tm, write ? "sceKernelUnlockWriteRWLock" : "sceKernelUnlockReadRWLock", uid));
        }, $"rwlock-waiter-{index}", priority);
        Until(() => run.Thread.Status == ThreadStatus.Waiting, $"waiter {index} in the queue");
        return run;
    }

    /// <summary>lockCount, writeOwnerId, numReadWaitThreads and numWriteWaitThreads as sceKernelGetRWLockInfo reports
    /// them.</summary>
    private (int Count, int Writer, int ReadWaiters, int WriteWaiters) Info(GuestThread main, int uid)
    {
        uint info = _h.Alloc(0x3C);
        GuestMemory.W32(info, 0x3C);
        Assert.Equal(0u, main.DispatchForTest("sceKernelGetRWLockInfo", (uint)uid, info));
        return ((int)GuestMemory.R32(info + 0x2C), (int)GuestMemory.R32(info + 0x30), (int)GuestMemory.R32(info + 0x34), (int)GuestMemory.R32(info + 0x38));
    }

    /// <summary>
    /// A waiting writer keeps new readers out while readers hold the lock: a try-read fails and a read lock queues
    /// behind the writer; the last reader's unlock makes the writer the owner inside the call, and the writer's unlock
    /// then lets the queued reader in.
    /// </summary>
    [Fact]
    public void AWaitingWriterKeepsNewReadersOut()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateRWLock", "writer-first", 0u, 0u);
            RwLock rwLock = Lock(uid);
            Assert.Equal(0, Call(Tm, "sceKernelLockReadRWLock", uid, new Ptr<uint>(0)));
            int[] results = [1, 1, 1];
            List<int> order = [];
            GuestRun writer = Waiter(uid, true, results, 0, order);
            GuestRun tryReader = _h.Start(_ => results[2] = Call(Tm, "sceKernelTryLockReadRWLock", uid), "rwlock-try");
            _h.Join(tryReader, 5_000);
            Assert.Equal(SceError.RW_LOCK_FAILED_TO_LOCK, results[2]);
            GuestRun reader = Waiter(uid, false, results, 1, order);
            Assert.Equal((1, 0, 1, 1), Info(main, uid));
            lock (rwLock)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockReadRWLock", (uint)uid));
                Assert.Equal((1, writer.Thread.Uid, 1, 0), Info(main, uid));
                Assert.Equal(unchecked((uint)SceError.RW_LOCK_FAILED_TO_LOCK), main.DispatchForTest("sceKernelTryLockWriteRWLock", (uint)uid));
            }
            _h.Join(writer, 5_000);
            _h.Join(reader, 5_000);
            Assert.Equal([0, 0], results[..2]);
            Assert.Equal([0, 1], order);
            Assert.Equal((0, 0, 0, 0), Info(main, uid));
        });
    }

    /// <summary>
    /// The writer's unlock hands the lock to the first waiting writer even with readers queued ahead of it; once that
    /// writer unlocks, with no writer left in the queue, every reader gets it at once.
    /// </summary>
    [Fact]
    public void AnUnlockGoesToTheFirstWriterPastTheReadersAheadOfIt()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateRWLock", "writers-first", 0u, 0u);
            RwLock rwLock = Lock(uid);
            Assert.Equal(0, Call(Tm, "sceKernelLockWriteRWLock", uid, new Ptr<uint>(0)));
            int[] results = [1, 1, 1];
            List<int> order = [];
            using var readersRelease = new ManualResetEventSlim();
            using var writerRelease = new ManualResetEventSlim();
            GuestRun firstReader = Waiter(uid, false, results, 0, order, release: readersRelease);
            GuestRun writer = Waiter(uid, true, results, 1, order, release: writerRelease);
            GuestRun secondReader = Waiter(uid, false, results, 2, order, release: readersRelease);
            lock (rwLock)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockWriteRWLock", (uint)uid));
                Assert.Equal((1, writer.Thread.Uid, 2, 0), Info(main, uid));
            }
            Until(() => Volatile.Read(ref results[1]) == 0, "the writer");
            writerRelease.Set();
            Until(() => rwLock.Writer == null, "the writer's unlock");
            Assert.Equal((2, 0, 0, 0), Info(main, uid)); // both readers at once, still holding it
            readersRelease.Set();
            _h.Join(firstReader, 5_000);
            _h.Join(writer, 5_000);
            _h.Join(secondReader, 5_000);
            Assert.Equal([0, 0, 0], results);
            Assert.Equal(1, order[0]);
            Assert.Equal((0, 0, 0, 0), Info(main, uid));
        });
    }

    /// <summary>
    /// With SCE_KERNEL_RW_LOCK_ATTR_TH_PRIO the unlock hands the lock to the higher-priority writer even though it came
    /// last; without the attribute to the one that came first. Equal priorities keep their arrival order.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APriorityLockGoesToTheHigherPriorityWriterFirst(bool threadPriority)
    {
        _h.OnGuest(main =>
        {
            uint attr = threadPriority ? RwLock.AttrThreadPriority : 0u;
            int uid = Call(Tm, "sceKernelCreateRWLock", threadPriority ? "th-prio" : "th-fifo", attr, 0u);
            RwLock rwLock = Lock(uid);
            Assert.Equal(0, Call(Tm, "sceKernelLockWriteRWLock", uid, new Ptr<uint>(0)));
            int[] results = [1, 1, 1];
            List<int> order = [];
            GuestRun low = Waiter(uid, true, results, 0, order, LowPriority);
            GuestRun highFirst = Waiter(uid, true, results, 1, order, HighPriority);
            GuestRun highSecond = Waiter(uid, true, results, 2, order, HighPriority);
            GuestRun first = threadPriority ? highFirst : low;
            lock (rwLock)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockWriteRWLock", (uint)uid));
                Assert.Equal((1, first.Thread.Uid, 0, 2), Info(main, uid));
            }
            _h.Join(low, 5_000);
            _h.Join(highFirst, 5_000);
            _h.Join(highSecond, 5_000);
            Assert.Equal([0, 0, 0], results);
            Assert.Equal(threadPriority ? [1, 2, 0] : [0, 1, 2], order);
        });
    }

    /// <summary>
    /// The writer's own calls, as threadmgr checks them: its read lock is RW_LOCK_FAILED_TO_LOCK (it does not wait), its
    /// write lock RW_LOCK_RECURSIVE without SCE_KERNEL_RW_LOCK_ATTR_RECURSIVE and a count up with it; a read unlock while
    /// a writer holds the lock is RW_LOCK_FAILED_TO_UNLOCK; a cancel flag above 1 is INVALID_FLAGS.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheWritersOwnCallsAndRecursion(bool recursive)
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateRWLock", "recursion", recursive ? RwLock.AttrRecursive : 0u, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelLockWriteRWLock", uid, new Ptr<uint>(0)));
            Assert.Equal(SceError.RW_LOCK_FAILED_TO_LOCK, Call(Tm, "sceKernelLockReadRWLock", uid, new Ptr<uint>(0)));
            Assert.Equal(SceError.RW_LOCK_FAILED_TO_LOCK, Call(Tm, "sceKernelTryLockReadRWLock", uid));
            int again = recursive ? SceError.OK : SceError.RW_LOCK_RECURSIVE;
            Assert.Equal(again, Call(Tm, "sceKernelLockWriteRWLock", uid, new Ptr<uint>(0)));
            Assert.Equal(again, Call(Tm, "sceKernelTryLockWriteRWLock", uid));
            Assert.Equal((recursive ? 3 : 1, main.Uid, 0, 0), Info(main, uid));
            Assert.Equal(SceError.RW_LOCK_FAILED_TO_UNLOCK, Call(Tm, "sceKernelUnlockReadRWLock", uid));
            Assert.Equal(SceError.INVALID_FLAGS, Call(Tm, "sceKernelCancelRWLock", uid, new Ptr<uint>(0), new Ptr<uint>(0), 2));
            for (int i = recursive ? 3 : 1; i > 0; i--)
            {
                Assert.Equal(0, Call(Tm, "sceKernelUnlockWriteRWLock", uid));
            }

            Assert.Equal(SceError.RW_LOCK_NOT_OWNED, Call(Tm, "sceKernelUnlockWriteRWLock", uid));
            Assert.Equal(SceError.RW_LOCK_UNLOCK_UDF, Call(Tm, "sceKernelUnlockReadRWLock", uid));
            Assert.Equal((0, 0, 0, 0), Info(main, uid));
        });
    }

    /// <summary>
    /// A waiting writer whose timeout passes (guest time moved by the test) leaves the queue at its deadline, and the
    /// readers queued behind it get the lock right then, inside the next call that looks at it, however late the
    /// writer's host thread is to notice; it returns WAIT_TIMEOUT with no time left.
    /// </summary>
    [Fact]
    public void ATimedOutWriterLetsTheReadersBehindItIn()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            const uint LimitUs = 50_000;
            int uid = Call(Tm, "sceKernelCreateRWLock", "deadline", 0u, 0u);
            RwLock rwLock = Lock(uid);
            Assert.Equal(0, Call(Tm, "sceKernelLockReadRWLock", uid, new Ptr<uint>(0)));
            uint timeout = _h.Word(LimitUs);
            int[] results = [1, 1];
            List<int> order = [];
            GuestRun writer = _h.Start(_ => results[0] = Call(Tm, "sceKernelLockWriteRWLock", uid, new Ptr<uint>(timeout)), "rwlock-late");
            Until(() => writer.Thread.Status == ThreadStatus.Waiting, "the timed wait");
            GuestRun reader = Waiter(uid, false, results, 1, order);
            lock (rwLock)
            {
                clock.Advance(LimitUs - 1);
                Assert.Equal((1, 0, 1, 1), Info(main, uid)); // still queued a microsecond before
                clock.Advance(1);
                Assert.Equal((2, 0, 0, 0), Info(main, uid));
            }
            _h.Join(writer, 5_000);
            _h.Join(reader, 5_000);
            Assert.Equal(SceError.WAIT_TIMEOUT, results[0]);
            Assert.Equal(0u, GuestMemory.R32(timeout));
            Assert.Equal(0, results[1]);
            Assert.Equal(0, Call(Tm, "sceKernelUnlockReadRWLock", uid));
            Assert.Equal((0, 0, 0, 0), Info(main, uid));
        });
    }

    /// <summary>
    /// Cancel frees the lock and releases the whole queue without handing it to anyone (WAIT_CANCEL; the readers and
    /// writers that waited are counted apart); flag 1 leaves the caller holding it for writing, flag 0 free. A delete
    /// releases the queue with WAIT_DELETE.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void ACancelOrDeleteReleasesTheQueue(int flag)
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateRWLock", "cancel", 0u, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelLockReadRWLock", uid, new Ptr<uint>(0)));
            int[] results = [1, 1, 1];
            List<int> order = [];
            GuestRun[] waiters = [Waiter(uid, true, results, 0, order), Waiter(uid, false, results, 1, order), Waiter(uid, false, results, 2, order)];
            uint numRead = _h.Word(9);
            uint numWrite = _h.Word(9);
            if (flag < 0)
            {
                Assert.Equal(0, Call(Tm, "sceKernelDeleteRWLock", uid));
            }
            else
            {
                Assert.Equal(0, Call(Tm, "sceKernelCancelRWLock", uid, new Ptr<uint>(numRead), new Ptr<uint>(numWrite), flag));
                Assert.Equal((2u, 1u), (GuestMemory.R32(numRead), GuestMemory.R32(numWrite)));
                Assert.Equal(flag == 1 ? (1, main.Uid, 0, 0) : (0, 0, 0, 0), Info(main, uid));
            }
            foreach (GuestRun waiter in waiters)
            {
                _h.Join(waiter, 5_000);
            }

            int error = flag < 0 ? SceError.WAIT_DELETE : SceError.WAIT_CANCEL;
            Assert.Equal([error, error, error], results);
            Assert.Empty(order);
        });
    }

    /// <summary>
    /// A save state keeps the lock's queue: a writer an unlock made the owner that had not returned when the state was
    /// saved returns OK when its lock is reissued (instead of finding itself the writer, RW_LOCK_RECURSIVE), and the
    /// reader still queued keeps its place, getting the lock at the restored writer's unlock.
    /// </summary>
    [Fact]
    public void ASaveStateKeepsTheQueueAndTheNewWriter()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateRWLock", "saved", 0u, 0u);
            RwLock rwLock = Lock(uid);
            RwLock restored = new() { Name = "saved" };
            Assert.Equal(0, Call(Tm, "sceKernelLockWriteRWLock", uid, new Ptr<uint>(0)));
            int[] first = [1, 1];
            int[] second = [1, 1];
            int[] gates = new int[2];
            var runs = new GuestRun[2];
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                bool write = index == 0;
                gates[i] = Call(Tm, "sceKernelCreateSema", $"gate-{i}", 0u, 0, 1, 0u);
                runs[i] = _h.Start(self =>
                {
                    first[index] = Call(Tm, write ? "sceKernelLockWriteRWLock" : "sceKernelLockReadRWLock", uid, new Ptr<uint>(0));
                    Assert.Equal(0, Call(Tm, "sceKernelWaitSema", gates[index], 1, new Ptr<uint>(0))); // the reissue comes when main says
                    second[index] = write ? restored.LockWrite(self, new Ptr<uint>(0)) : restored.LockRead(self, new Ptr<uint>(0));
                    if (write)
                    {
                        Assert.Equal(0, Call(Tm, "sceKernelWaitSema", gates[index], 1, new Ptr<uint>(0)));
                        Assert.Equal(SceError.OK, restored.UnlockWrite(self));
                    }
                    else if (second[index] == 0)
                    {
                        Assert.Equal(SceError.OK, restored.UnlockRead());
                    }
                }, $"rwlock-saved-{i}");
                Until(() => runs[index].Thread.Status == ThreadStatus.Waiting, "the lock");
            }

            var writer = new SnapshotWriter();
            lock (rwLock)
            {
                Assert.Equal(0u, main.DispatchForTest("sceKernelUnlockWriteRWLock", (uint)uid)); // hands it to the writer
                rwLock.Save(writer);
            }
            Assert.Equal(0, Call(Tm, "sceKernelCancelRWLock", uid, new Ptr<uint>(0), new Ptr<uint>(0), 0));
            restored.Load(new SnapshotReader(writer.Data.ToArray()));
            restored.Resolve(_h.Emu.Kernel.Objects);
            Assert.Equal((1, runs[0].Thread, 1, 0), restored.Info());
            // the reader reissues first and queues; the writer's reissue returns at once with the lock it was given
            Assert.Equal(0, Call(Tm, "sceKernelSignalSema", gates[1], 1));
            Until(() => runs[1].Thread.Status == ThreadStatus.Waiting && runs[1].Thread.CurrentWaitSync == restored, "the reissued read lock");
            Assert.Equal(0, Call(Tm, "sceKernelSignalSema", gates[0], 1));
            Until(() => Volatile.Read(ref second[0]) == 0, "the reissued write lock");
            Assert.Equal((1, runs[0].Thread, 1, 0), restored.Info());
            Assert.Equal(0, Call(Tm, "sceKernelSignalSema", gates[0], 1)); // the writer unlocks: the reader gets it
            foreach (GuestRun run in runs)
            {
                _h.Join(run, 5_000);
            }

            Assert.Equal([0, SceError.WAIT_CANCEL], first);
            Assert.Equal([0, 0], second);
            Assert.Equal<(int, GuestThread?, int, int)>((0, null, 0, 0), restored.Info());
        });
    }

    /// <summary>A rw lock record of a build before the saved queue (readers and the writer's count apart, no queue)
    /// loads with an empty queue and the lock count of whoever held it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARwLockRecordWithoutAQueueLoads(bool heldForWriting)
    {
        _h.OnGuest(main =>
        {
            var writer = new SnapshotWriter();
            writer.Write(RwLock.AttrRecursive);
            writer.Write(heldForWriting ? 0 : 3); // readers
            writer.Write(heldForWriting ? main.Uid : 0);
            writer.Write(heldForWriting ? 2 : 0); // the writer's count
            writer.Write(false);
            var restored = new RwLock { Name = "legacy" };
            restored.Load(new SnapshotReader(writer.Data.ToArray()));
            restored.Resolve(_h.Emu.Kernel.Objects);
            Assert.Equal(RwLock.AttrRecursive, restored.Attr);
            Assert.Equal(heldForWriting ? (2, main, 0, 0) : ((int, GuestThread?, int, int))(3, null, 0, 0), restored.Info());
        });
    }
}
