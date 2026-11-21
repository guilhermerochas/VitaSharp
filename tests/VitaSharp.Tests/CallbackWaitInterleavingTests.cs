using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Tests;

/// <summary>
/// The CB waits of the semaphore, mutex, lightweight mutex, cond, message pipe and simple event under a callback
/// notification, against FW 3.74 threadmgr (the waits 0x81011bc0, 0x8100d79c, 0x810229c4, 0x8101fab4, 0x81013dfc /
/// 0x8101456c and 0x8101d220, and the notify 0x8100c820, studied in the decompiled module): every CB wait is a loop
/// that runs the thread's notified callbacks, looks at its object and sleeps; a notification ends the sleep with
/// NOTIFY_CALLBACK through the class's wait-release handler, so the waiter is off the queue while its callbacks run
/// (what the object hands out meanwhile goes to the waiters behind), and it then joins the queue anew at the end, with
/// what is left of its timeout. Every step is forced by the test (the object's lock held across calls, guest time
/// moved by the test), never left to the host's timing.
/// </summary>
[Collection("guest")]
public sealed class CallbackWaitInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "cb-wait-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private const uint EventBit = 0x01000000;

    /// <summary>A guest address that runs <paramref name="body"/> (an HLE host stub) and returns its result.</summary>
    private uint Stub(string name, Func<HleContext, uint> body)
    {
        uint stub = _h.Alloc(16);
        VitaSharp.Core.Loader.Linker.WriteHleStub(stub, _h.Emu.Hle.RegisterHostFunction(name, context => context.Return(body(context))), 0);
        _h.Emu.Code.Invalidate(stub, 16);
        return stub;
    }

    /// <summary>One object kind: how its CB wait and its plain wait are called, what hands one waiter what it waits
    /// for, how many wait in its queue, and what a waiter does around its wait (a cond's mutex).</summary>
    private sealed class Subject
    {
        public object Sync = null!;
        /// <summary>The CB wait on the calling thread, through the real dispatch path, with a timeout word (0: none).</summary>
        public Func<GuestThread, uint, int> CbWait = null!;
        public Func<int> PlainWait = null!;
        /// <summary>Main hands one waiter what it waits for (null: the previous waiter's release does it).</summary>
        public Action? Give;
        public Func<int> Waiters = null!;
        public Action Before = static () => { };
        public Action After = static () => { };
    }

    /// <summary>The syscall form of sceKernelWaitMultipleEventsCB in OR mode on <paramref name="uid"/>'s EventBit,
    /// without a results array (it returns 0 when the event came), with a timeout word (0: none).</summary>
    private int MultiCbWait(GuestThread thread, int uid, uint timeout)
    {
        uint events = _h.Alloc(8);
        GuestMemory.W32(events, (uint)uid);
        GuestMemory.W32(events + 4, EventBit);
        uint opt = _h.Alloc(8);
        GuestMemory.W32(opt, 0);
        GuestMemory.W32(opt + 4, timeout);
        return (int)thread.DispatchForTest("_sceKernelWaitMultipleEventsCB", events, 1u, 1u, opt);
    }

    private Subject Make(string kind)
    {
        switch (kind)
        {
            case "sema":
            {
                int uid = Call(Tm, "sceKernelCreateSema", "cb-sema", 0u, 0, 2, 0u);
                Semaphore sema = _h.Emu.Kernel.Objects.Get<Semaphore>(uid)!;
                return new Subject
                {
                    Sync = sema,
                    CbWait = (th, timeout) => (int)th.DispatchForTest("_sceKernelWaitSemaCB", (uint)uid, 1u, timeout),
                    PlainWait = () => Call(Tm, "sceKernelWaitSema", uid, 1, new Ptr<uint>(0)),
                    Give = () => Assert.Equal(0, Call(Tm, "sceKernelSignalSema", uid, 1)),
                    Waiters = () => sema.Waiters,
                };
            }
            case "mutex":
            {
                int uid = Call(Tm, "sceKernelCreateMutex", "cb-mutex", 0u, 1, 0u); // held by main
                Mutex mutex = _h.Emu.Kernel.Objects.Get<Mutex>(uid)!;
                bool first = true;
                return new Subject
                {
                    Sync = mutex,
                    CbWait = (th, timeout) => (int)th.DispatchForTest("_sceKernelLockMutexCB", (uint)uid, 1u, timeout),
                    PlainWait = () => Call(Tm, "sceKernelLockMutex", uid, 1, new Ptr<uint>(0)),
                    Give = () =>
                    {
                        if (first)
                        {
                            first = false;
                            Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", uid, 1));
                        }
                    },
                    Waiters = () => mutex.Waiters,
                    After = () => Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", uid, 1)),
                };
            }
            case "lwmutex":
            case "lwmutex-syscall":
            {
                uint work = _h.Alloc(32);
                Assert.Equal(0, Call(Tm, "sceKernelCreateLwMutex", work, "cb-lwmutex", 0u, 1, 0u)); // held by main
                Mutex mutex = _h.Emu.Kernel.Objects.Get<Mutex>((int)GuestMemory.R32(work + Mutex.LwUidOffset))!;
                bool first = true;
                return new Subject
                {
                    Sync = mutex,
                    // the syscall SceLibKernel's sceKernelLockLwMutexCB makes: its fourth argument asks for the CB wait
                    CbWait = (th, timeout) => kind == "lwmutex"
                        ? (int)th.DispatchForTest("sceKernelLockLwMutexCB", work, 1u, timeout)
                        : (int)th.DispatchForTest("_sceKernelLockLwMutex", work, 1u, timeout, 1u),
                    PlainWait = () => Call(Tm, "sceKernelLockLwMutex", work, 1, new Ptr<uint>(0)),
                    Give = () =>
                    {
                        if (first)
                        {
                            first = false;
                            Assert.Equal(0, Call(Tm, "sceKernelUnlockLwMutex", work, 1));
                        }
                    },
                    Waiters = () => mutex.Waiters,
                    After = () => Assert.Equal(0, Call(Tm, "sceKernelUnlockLwMutex", work, 1)),
                };
            }
            case "cond":
            {
                int mutexUid = Call(Tm, "sceKernelCreateMutex", "cb-cond-mutex", 0u, 0, 0u);
                int uid = Call(Tm, "sceKernelCreateCond", "cb-cond", 0u, mutexUid, 0u);
                CondVar cond = _h.Emu.Kernel.Objects.Get<CondVar>(uid)!;
                return new Subject
                {
                    Sync = cond,
                    CbWait = (th, timeout) => (int)th.DispatchForTest("_sceKernelWaitCondCB", (uint)uid, timeout),
                    PlainWait = () => Call(Tm, "sceKernelWaitCond", uid, new Ptr<uint>(0)),
                    Give = () => Assert.Equal(0, Call(Tm, "sceKernelSignalCond", uid)),
                    Waiters = () => cond.Waiters,
                    Before = () => Assert.Equal(0, Call(Tm, "sceKernelLockMutex", mutexUid, 1, new Ptr<uint>(0))),
                    After = () => Assert.Equal(0, Call(Tm, "sceKernelUnlockMutex", mutexUid, 1)),
                };
            }
            case "msgpipe":
            {
                int uid = Call(Tm, "sceKernelCreateMsgPipe", "cb-pipe", 0x40u, 0u, 0x1000u, 0u);
                MsgPipe pipe = _h.Emu.Kernel.Objects.Get<MsgPipe>(uid)!;
                uint source = _h.Alloc(4);
                return new Subject
                {
                    Sync = pipe,
                    CbWait = (th, timeout) =>
                    {
                        uint sink = _h.Alloc(4);
                        uint vector = _h.Alloc(8);
                        GuestMemory.W32(vector, sink);
                        GuestMemory.W32(vector + 4, 4);
                        uint opt = _h.Alloc(12);
                        GuestMemory.W32(opt, MsgPipe.WaitAll);
                        GuestMemory.W32(opt + 4, 0);
                        GuestMemory.W32(opt + 8, timeout);
                        return (int)th.DispatchForTest("_sceKernelReceiveMsgPipeVectorCB", (uint)uid, vector, 1u, opt);
                    },
                    PlainWait = () => Call(Tm, "sceKernelReceiveMsgPipe", uid, _h.Alloc(4), 4u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)),
                    Give = () => Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", uid, source, 4u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0))),
                    Waiters = () => pipe.RecvWaiters,
                };
            }
            case "rwlock":
            {
                int uid = Call(Tm, "sceKernelCreateRWLock", "cb-rwlock", 0u, 0u);
                Assert.Equal(0, Call(Tm, "sceKernelLockWriteRWLock", uid, new Ptr<uint>(0))); // held by main
                RwLock rwLock = _h.Emu.Kernel.Objects.Get<RwLock>(uid)!;
                bool first = true;
                return new Subject
                {
                    Sync = rwLock,
                    CbWait = (th, timeout) => (int)th.DispatchForTest("_sceKernelLockWriteRWLockCB", (uint)uid, timeout),
                    PlainWait = () => Call(Tm, "sceKernelLockWriteRWLock", uid, new Ptr<uint>(0)),
                    Give = () =>
                    {
                        if (first)
                        {
                            first = false;
                            Assert.Equal(0, Call(Tm, "sceKernelUnlockWriteRWLock", uid));
                        }
                    },
                    Waiters = () => rwLock.WriteWaiters,
                    After = () => Assert.Equal(0, Call(Tm, "sceKernelUnlockWriteRWLock", uid)),
                };
            }
            case "multi":
            {
                // sceKernelWaitMultipleEventsCB on one simple event (0x8102cc4c / 0x8101ed4c): the notify ends its
                // sleep, it leaves the event's queue, and after the callbacks its next pass joins the queue again
                int uid = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("cb-multi"), EventBits.AttrAutoReset, 0u, 0u);
                SimpleEvent simpleEvent = _h.Emu.Kernel.Objects.Get<SimpleEvent>(uid)!;
                return new Subject
                {
                    Sync = simpleEvent,
                    CbWait = (th, timeout) => MultiCbWait(th, uid, timeout),
                    PlainWait = () => Call(Tm, "sceKernelWaitEvent", uid, EventBit, new Ptr<uint>(0), new Ptr<ulong>(0), new Ptr<uint>(0)),
                    Give = () => Assert.Equal(0, Call(Tm, "sceKernelSetEvent", uid, EventBit, 0UL)),
                    Waiters = () => simpleEvent.NumWaiters,
                };
            }
            default:
            {
                int uid = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("cb-event"), EventBits.AttrAutoReset, 0u, 0u);
                SimpleEvent simpleEvent = _h.Emu.Kernel.Objects.Get<SimpleEvent>(uid)!;
                return new Subject
                {
                    Sync = simpleEvent,
                    CbWait = (th, timeout) =>
                    {
                        uint opt = _h.Alloc(8);
                        GuestMemory.W32(opt, 0);
                        GuestMemory.W32(opt + 4, timeout);
                        return (int)th.DispatchForTest("_sceKernelWaitEventCB", (uint)uid, EventBit, _h.Alloc(4), opt);
                    },
                    PlainWait = () => Call(Tm, "sceKernelWaitEvent", uid, EventBit, new Ptr<uint>(0), new Ptr<ulong>(0), new Ptr<uint>(0)),
                    Give = () => Assert.Equal(0, Call(Tm, "sceKernelSetEvent", uid, EventBit, 0UL)),
                    Waiters = () => simpleEvent.NumWaiters,
                };
            }
        }
    }

    /// <summary>
    /// A notification takes the CB waiter at the head of the queue off it, inside the notify (the object's lock is
    /// held across the notify and the next call): what main hands out right after goes to the waiter behind, and the
    /// notified thread, after its callback, waits again behind the others until the next one.
    /// </summary>
    [Theory]
    [InlineData("sema")]
    [InlineData("mutex")]
    [InlineData("lwmutex")]
    [InlineData("lwmutex-syscall")]
    [InlineData("cond")]
    [InlineData("msgpipe")]
    [InlineData("rwlock")]
    [InlineData("event")]
    [InlineData("multi")]
    public void ANotifiedCbWaiterLeavesTheQueueAndJoinsItAgainAtTheEnd(string kind)
    {
        bool callbackRan = false;
        uint stub = Stub($"cbwait:queue-{kind}", _ =>
        {
            Volatile.Write(ref callbackRan, true);
            return 0;
        });
        _h.OnGuest(main =>
        {
            Subject subject = Make(kind);
            int callback = 0;
            int notifiedResult = 1;
            int otherResult = 1;
            bool notifiedDone = false;
            bool otherDone = false;
            using var release = new ManualResetEventSlim();
            GuestRun notified = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "cb-wait", 0u, stub, 0u);
                subject.Before();
                notifiedResult = subject.CbWait(th, 0);
                Volatile.Write(ref notifiedDone, true);
                subject.After();
            }, "cb-wait-notified");
            Until(() => notified.Thread.Status == ThreadStatus.Waiting && subject.Waiters() == 1, "the CB wait");
            GuestRun other = _h.Start(th =>
            {
                subject.Before();
                otherResult = subject.PlainWait();
                Volatile.Write(ref otherDone, true);
                release.Wait();
                subject.After();
            }, "cb-wait-other");
            Until(() => other.Thread.Status == ThreadStatus.Waiting && subject.Waiters() == 2, "the second wait");
            lock (subject.Sync)
            {
                Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", callback, 0));
                // off the queue inside the notify; a multiple-events call's place stays until the call leaves its
                // queues itself (0x8101d7a4), but a set no longer hands it anything (0x8101dcf8)
                Assert.Equal(kind == "multi" ? 2 : 1, subject.Waiters());
                subject.Give!();
            }
            Until(() => Volatile.Read(ref otherDone) || Volatile.Read(ref notifiedDone), "a wait to end");
            Assert.True(Volatile.Read(ref otherDone), "what main gave after the notify went to the waiter behind");
            Until(() => Volatile.Read(ref callbackRan) && subject.Waiters() == 1 && notified.Thread.Status == ThreadStatus.Waiting, "the notified thread's new wait");
            Assert.False(Volatile.Read(ref notifiedDone));
            Assert.Equal(0, otherResult);
            release.Set();
            subject.Give();
            _h.Join(other, 5_000);
            _h.Join(notified, 5_000);
            Assert.Equal(0, notifiedResult);
        });
    }

    /// <summary>
    /// The timeout of a CB wait is one deadline for the whole call (the test moves guest time): the wait that resumes
    /// after the callback times out at the original deadline, not a full timeout later, and writes 0 back.
    /// </summary>
    [Theory]
    [InlineData("sema")]
    [InlineData("mutex")]
    [InlineData("lwmutex")]
    [InlineData("lwmutex-syscall")]
    [InlineData("cond")]
    [InlineData("msgpipe")]
    [InlineData("rwlock")]
    [InlineData("event")]
    [InlineData("multi")]
    public void ACallbackInTheMiddleDoesNotRestartTheTimeout(string kind)
    {
        const uint LimitUs = 50_000;
        bool callbackRan = false;
        uint stub = Stub($"cbwait:timeout-{kind}", _ =>
        {
            Volatile.Write(ref callbackRan, true);
            return 0;
        });
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            Subject subject = Make(kind);
            int callback = 0;
            int result = 1;
            uint timeout = _h.Word(LimitUs);
            GuestRun waiter = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "cb-timeout", 0u, stub, 0u);
                subject.Before();
                result = subject.CbWait(th, timeout);
                if (kind == "cond")
                {
                    subject.After(); // a timed-out cond wait locked its mutex again; a timed-out lock holds nothing
                }
            }, "cb-wait-timeout");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && subject.Waiters() == 1, "the CB wait");
            clock.Advance(20_000);
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", callback, 0));
            Until(() => Volatile.Read(ref callbackRan) && subject.Waiters() == 1 && waiter.Thread.Status == ThreadStatus.Waiting, "the wait after the callback");
            lock (subject.Sync)
            {
                clock.Advance(LimitUs - 20_000 - 1);
                Assert.Equal(1, subject.Waiters()); // a microsecond before the original deadline it still waits
                clock.Advance(1);
            }
            _h.Join(waiter, 5_000);
            Assert.Equal(SceError.WAIT_TIMEOUT, result);
            Assert.Equal(0u, GuestMemory.R32(timeout));
            if (kind is "mutex" or "lwmutex" or "lwmutex-syscall" or "rwlock")
            {
                subject.Give!(); // main still holds it
            }
        });
    }

    /// <summary>
    /// A CB wait runs the callbacks notified before it, then looks at its object (threadmgr's loop checks the callbacks
    /// first): a callback that takes the only unit (a poll of the same semaphore, the event's bit) leaves the wait
    /// to sleep until the next one, instead of the wait taking it and the callback finding nothing.
    /// </summary>
    [Theory]
    [InlineData("sema")]
    [InlineData("event")]
    [InlineData("multi")]
    public void CallbacksNotifiedBeforeTheWaitRunBeforeItLooksAtTheObject(string kind)
    {
        int uid = 0;
        int pollInCallback = 1;
        uint stub = Stub($"cbwait:first-{kind}", _ =>
        {
            pollInCallback = kind == "sema"
                ? Call(Tm, "sceKernelPollSema", uid, 1)
                : Call(Tm, "sceKernelPollEvent", uid, EventBit, new Ptr<uint>(0), new Ptr<ulong>(0));
            return 0;
        });
        _h.OnGuest(main =>
        {
            if (kind == "sema")
            {
                uid = Call(Tm, "sceKernelCreateSema", "cb-first", 0u, 1, 2, 0u);
            }
            else
            {
                uid = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("cb-first"), EventBits.AttrAutoReset, EventBit, 0u);
            }

            int callback = 0;
            int result = 1;
            bool done = false;
            using var go = new ManualResetEventSlim();
            GuestRun waiter = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "cb-first", 0u, stub, 0u);
                go.Wait();
                if (kind == "sema")
                {
                    result = (int)th.DispatchForTest("_sceKernelWaitSemaCB", (uint)uid, 1u, 0u);
                }
                else if (kind == "multi")
                {
                    result = MultiCbWait(th, uid, 0);
                }
                else
                {
                    uint opt = _h.Alloc(8);
                    result = (int)th.DispatchForTest("_sceKernelWaitEventCB", (uint)uid, EventBit, _h.Alloc(4), opt);
                }
                Volatile.Write(ref done, true);
            }, "cb-wait-first");
            Until(() => Volatile.Read(ref callback) != 0, "the callback");
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", callback, 0));
            go.Set();
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting || Volatile.Read(ref done), "the CB wait");
            Assert.False(Volatile.Read(ref done), "the callback took the unit first: the wait sleeps");
            Assert.Equal(0, pollInCallback);
            if (kind == "sema")
            {
                Assert.Equal(0, Call(Tm, "sceKernelSignalSema", uid, 1));
            }
            else
            {
                Assert.Equal(0, Call(Tm, "sceKernelSetEvent", uid, EventBit, 0UL));
            }
            _h.Join(waiter, 5_000);
            Assert.Equal(0, result);
        });
    }

    /// <summary>
    /// An AND-mode sceKernelWaitMultipleEventsCB keeps the results it had across the callbacks (threadmgr 3.74 keeps
    /// the events' done bits in the call): its pass after the callbacks queues it again only on the events still
    /// without a result, so a later set of an event it already took stays in that event, and the call returns both
    /// results in the order the events fired.
    /// </summary>
    [Fact]
    public void AMultipleEventsCbCallKeepsItsResultsAcrossTheCallbacks()
    {
        bool callbackRan = false;
        uint stub = Stub("cbwait:multi-and", _ =>
        {
            Volatile.Write(ref callbackRan, true);
            return 0;
        });
        _h.OnGuest(main =>
        {
            int first = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("cb-multi-a"), EventBits.AttrAutoReset, 0u, 0u);
            int second = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("cb-multi-b"), EventBits.AttrAutoReset, 0u, 0u);
            SimpleEvent firstEvent = _h.Emu.Kernel.Objects.Get<SimpleEvent>(first)!;
            SimpleEvent secondEvent = _h.Emu.Kernel.Objects.Get<SimpleEvent>(second)!;
            uint events = _h.Alloc(16);
            GuestMemory.W32(events, (uint)first);
            GuestMemory.W32(events + 4, EventBit);
            GuestMemory.W32(events + 8, (uint)second);
            GuestMemory.W32(events + 12, EventBit);
            uint results = _h.Alloc(2 * 0x18);
            uint opt = _h.Alloc(8);
            GuestMemory.W32(opt, results);
            GuestMemory.W32(opt + 4, 0);
            int callback = 0;
            int returned = 1;
            GuestRun waiter = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "cb-multi-and", 0u, stub, 0u);
                returned = (int)th.DispatchForTest("_sceKernelWaitMultipleEventsCB", events, 2u, 2u, opt);
            }, "cb-multi-and");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && firstEvent.NumWaiters == 1 && secondEvent.NumWaiters == 1, "the AND wait in both queues");
            Assert.Equal(0, Call(Tm, "sceKernelSetEvent", first, EventBit, 5UL));
            Assert.Equal(0, firstEvent.NumWaiters);
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", callback, 0));
            Until(() => Volatile.Read(ref callbackRan) && secondEvent.NumWaiters == 1 && waiter.Thread.Status == ThreadStatus.Waiting, "the pass after the callback");
            Assert.Equal(0, firstEvent.NumWaiters); // the event it already took is not waited for again
            Assert.Equal(0, Call(Tm, "sceKernelSetEvent", first, EventBit, 6UL));
            Assert.Equal(EventBit, firstEvent.Pattern & EventBit); // nobody took it
            Assert.Equal(0, Call(Tm, "sceKernelSetEvent", second, EventBit, 7UL));
            _h.Join(waiter, 5_000);
            Assert.Equal(2, returned);
            Assert.Equal((uint)first, GuestMemory.R32(results));
            Assert.Equal(5UL, GuestMemory.Read<ulong>(results + 16));
            Assert.Equal((uint)second, GuestMemory.R32(results + 0x18));
            Assert.Equal(7UL, GuestMemory.Read<ulong>(results + 0x18 + 16));
        });
    }

    /// <summary>
    /// A reader-writer lock's CB lock, behind readers, is not counted as a waiter while its callbacks run, and a
    /// callback in the middle does not restart its timeout.
    /// </summary>
    [Fact]
    public void ARwLockCbWaiterIsNotCountedWhileItsCallbacksRun()
    {
        const uint LimitUs = 50_000;
        bool callbackRan = false;
        int waitersInCallback = -1;
        RwLock? rwLock = null;
        uint stub = Stub("cbwait:rwlock", _ =>
        {
            waitersInCallback = rwLock!.WriteWaiters;
            Volatile.Write(ref callbackRan, true);
            return 0;
        });
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateRWLock", "cb-rwlock", 0u, 0u);
            rwLock = _h.Emu.Kernel.Objects.Get<RwLock>(uid)!;
            Assert.Equal(0, Call(Tm, "sceKernelLockReadRWLock", uid, new Ptr<uint>(0)));
            int callback = 0;
            int result = 1;
            uint timeout = _h.Word(LimitUs);
            GuestRun waiter = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "cb-rwlock", 0u, stub, 0u);
                result = (int)th.DispatchForTest("_sceKernelLockWriteRWLockCB", (uint)uid, timeout);
            }, "cb-wait-rwlock");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && rwLock.WriteWaiters == 1, "the CB lock");
            clock.Advance(20_000);
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", callback, 0));
            Until(() => Volatile.Read(ref callbackRan) && rwLock.WriteWaiters == 1 && waiter.Thread.Status == ThreadStatus.Waiting, "the lock after the callback");
            Assert.Equal(0, waitersInCallback);
            lock (rwLock)
            {
                clock.Advance(LimitUs - 20_000 - 1);
                Assert.Equal(1, rwLock.WriteWaiters);
                clock.Advance(1);
            }
            _h.Join(waiter, 5_000);
            Assert.Equal(SceError.WAIT_TIMEOUT, result);
            Assert.Equal(0, Call(Tm, "sceKernelUnlockReadRWLock", uid));
        });
    }

    /// <summary>
    /// Callbacks registered to a simple event (threadmgr 3.74: register 0x8100cfcc, unregister 0x8100d108 / 0x8100d1d0,
    /// the notify 0x8101cfcc): only the *WithNotifyCallback set and pulse notify them, in callback UID order whatever
    /// the registration order, each with r0 = the event's UID, r1 = its count, r2 = the notify argument; a set pattern
    /// of 0 notifies nobody; unregistering a callback that is not registered is CALLBACK_NOT_REGISTERED; the All form
    /// empties the event's list.
    /// </summary>
    [Fact]
    public void CallbacksRegisteredToAnEventRunOnItsNotifyingSet()
    {
        List<(string Name, uint R0, uint R1, uint R2)> runs = new();
        uint first = Stub("cbwait:event-a", context =>
        {
            runs.Add(("a", context.Gpr(0), context.Gpr(1), context.Gpr(2)));
            return 0;
        });
        uint second = Stub("cbwait:event-b", context =>
        {
            runs.Add(("b", context.Gpr(0), context.Gpr(1), context.Gpr(2)));
            return 0;
        });
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("cb-registered"), 0u, 0u, 0u);
            SimpleEvent simpleEvent = _h.Emu.Kernel.Objects.Get<SimpleEvent>(uid)!;
            int a = Call(Tm, "sceKernelCreateCallback", "event-a", 0u, first, 0u);
            int b = Call(Tm, "sceKernelCreateCallback", "event-b", 0u, second, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", uid, b));
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", uid, a));
            Assert.Equal(2, simpleEvent.RegisteredCallbacks);

            Assert.Equal(0, Call(Tm, "sceKernelSetEvent", uid, EventBit, 0UL));
            Assert.Equal(0, Call(Tm, "sceKernelCheckCallback")); // a plain set notifies nobody
            Assert.Equal(0, Call(Tm, "sceKernelSetEventWithNotifyCallback", uid, 0u, 0UL, 5));
            Assert.Equal(0, Call(Tm, "sceKernelCheckCallback")); // nor does a set of nothing

            Assert.Equal(0, Call(Tm, "sceKernelPulseEventWithNotifyCallback", uid, 0x02000000u, 0UL, 7));
            Assert.Equal(0, Call(Tm, "sceKernelSetEventWithNotifyCallback", uid, 0x02000000u, 0UL, 9));
            Assert.Equal(2, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([("a", (uint)uid, 2u, 9u), ("b", (uint)uid, 2u, 9u)], runs);
            Assert.Equal(EventBit | 0x02000000u | EventBits.Create, simpleEvent.Pattern); // the pulse's bit did not stay

            Assert.Equal(0, Call(Tm, "sceKernelUnregisterCallbackFromEvent", uid, a));
            Assert.Equal(SceError.CALLBACK_NOT_REGISTERED, Call(Tm, "sceKernelUnregisterCallbackFromEvent", uid, a));
            Assert.Equal(SceError.UNKNOWN_CALLBACK_ID, Call(Tm, "sceKernelRegisterCallbackToEvent", uid, uid)); // not a callback
            runs.Clear();
            Assert.Equal(0, Call(Tm, "sceKernelSetEventWithNotifyCallback", uid, EventBit, 0UL, 1));
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([("b", (uint)uid, 1u, 1u)], runs);

            Assert.Equal(0, Call(Tm, "sceKernelUnregisterCallbackFromEventAll", uid));
            Assert.Equal(0, simpleEvent.RegisteredCallbacks);
            Assert.Equal(0, Call(Tm, "sceKernelSetEventWithNotifyCallback", uid, EventBit, 0UL, 1));
            Assert.Equal(0, Call(Tm, "sceKernelCheckCallback"));
        });
    }

    /// <summary>
    /// Every object of threadmgr's event class takes callbacks, not only simple events (FW 3.74: the register looks the
    /// UID up in SceUIDRegisterCallbackClass, whose child SceUIDEventClass is the parent of the simple event, message
    /// pipe and timer classes): a message pipe's send that moves bytes notifies them with IN and "can receive" while the
    /// bytes wait in its buffer (r0 = the pipe's UID, r2 = the bits), a receive with OUT, an open by name with OPEN; a
    /// timer keeps them too. A threadmgr object outside the event class (a semaphore) is UNKNOWN_EVENT_ID.
    /// </summary>
    [Fact]
    public void CallbacksRegisterToEveryEventClassObject()
    {
        List<(uint R0, uint R2)> runs = new();
        uint stub = Stub("cbwait:event-class", context =>
        {
            runs.Add((context.Gpr(0), context.Gpr(2)));
            return 0;
        });
        _h.OnGuest(main =>
        {
            int callback = Call(Tm, "sceKernelCreateCallback", "event-class", 0u, stub, 0u);
            int pipe = Call(Tm, "sceKernelCreateMsgPipe", "cb-registered-pipe", 0x40u, 0x80u, 0x1000u, 0u);
            int timer = Call(Tm, "sceKernelCreateTimer", "cb-registered-timer", 0u, 0u);
            int sema = Call(Tm, "sceKernelCreateSema", "cb-not-an-event", 0u, 0, 1, 0u);
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelRegisterCallbackToEvent", sema, callback));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelUnregisterCallbackFromEventAll", sema));
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", pipe, callback));
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", timer, callback));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelUnregisterCallbackFromEvent", sema, callback));

            uint data = _h.Alloc(8);
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", pipe, data, 8u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([((uint)pipe, MsgPipe.EventIn | MsgPipe.EventReceivable)], runs);
            runs.Clear();
            Assert.Equal(0, Call(Tm, "sceKernelReceiveMsgPipe", pipe, data, 8u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([((uint)pipe, MsgPipe.EventOut)], runs);
            runs.Clear();
            int opened = Call(Tm, "sceKernelOpenMsgPipe", _h.Str("cb-registered-pipe"));
            Assert.Equal(pipe, opened);
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([((uint)pipe, MsgPipe.EventOpen)], runs);
            runs.Clear();

            Assert.Equal(0, Call(Tm, "sceKernelUnregisterCallbackFromEvent", pipe, callback));
            Assert.Equal(SceError.CALLBACK_NOT_REGISTERED, Call(Tm, "sceKernelUnregisterCallbackFromEvent", pipe, callback));
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", pipe, data, 8u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal(0, Call(Tm, "sceKernelUnregisterCallbackFromEvent", timer, callback));
            Assert.Equal(SceError.CALLBACK_NOT_REGISTERED, Call(Tm, "sceKernelUnregisterCallbackFromEvent", timer, callback));
        });
    }

    /// <summary>The callbacks registered to a message pipe and to a timer are in the save state (as a simple event's
    /// are); a record of a build before them loads with none.</summary>
    [Fact]
    public void ASaveStateKeepsTheCallbacksOfPipesAndTimers()
    {
        _h.OnGuest(main =>
        {
            int callback = Call(Tm, "sceKernelCreateCallback", "saved-registration", 0u, Stub("cbwait:saved", _ => 0), 0u);
            int pipeUid = Call(Tm, "sceKernelCreateMsgPipe", "cb-saved-pipe", 0x40u, 0u, 0x1000u, 0u);
            int timerUid = Call(Tm, "sceKernelCreateTimer", "cb-saved-timer", 0u, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", pipeUid, callback));
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", timerUid, callback));
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", timerUid, callback));
            MsgPipe pipe = _h.Emu.Kernel.Objects.Get<MsgPipe>(pipeUid)!;
            TimerObject timer = _h.Emu.Kernel.Objects.Get<TimerObject>(timerUid)!;

            var pipeState = new VitaSharp.Core.SaveState.SnapshotWriter();
            pipe.Save(pipeState);
            var restoredPipe = new MsgPipe { Name = "cb-saved-pipe" };
            restoredPipe.Load(new VitaSharp.Core.SaveState.SnapshotReader(pipeState.Data.ToArray()));
            restoredPipe.ResolveRegistrations(_h.Emu.Kernel.Objects);
            Assert.Equal(1, restoredPipe.RegisteredCallbacks);

            var timerState = new VitaSharp.Core.SaveState.SnapshotWriter();
            timer.Save(timerState);
            var restoredTimer = new TimerObject { Name = "cb-saved-timer", Clock = () => 0 };
            restoredTimer.Load(new VitaSharp.Core.SaveState.SnapshotReader(timerState.Data.ToArray()));
            restoredTimer.ResolveRegistrations(_h.Emu.Kernel.Objects);
            Assert.Equal(2, restoredTimer.RegisteredCallbacks);

            // the timer record of the builds before the registrations: its fields only (the two registrations, 20 bytes,
            // and the event's user bits and user data after them, 12 bytes)
            byte[] legacy = timerState.Data.ToArray()[..^32];
            var legacyTimer = new TimerObject { Name = "legacy-timer", Clock = () => 0 };
            legacyTimer.Load(new VitaSharp.Core.SaveState.SnapshotReader(legacy));
            legacyTimer.ResolveRegistrations(_h.Emu.Kernel.Objects);
            Assert.Equal(0, legacyTimer.RegisteredCallbacks);
        });
    }

    /// <summary>
    /// The notifying set reaches the callback's thread asleep in a CB wait on the same event for another bit: the
    /// callback runs (r0 = the event's UID) and the thread waits again; the set of its own bit then ends the wait. The
    /// syscall form passes the user data and the notify argument in a 16-byte block.
    /// </summary>
    [Fact]
    public void ANotifyingSetRunsTheCallbackOfAThreadWaitingOnTheSameEvent()
    {
        uint ranWith = 0;
        uint argument = 0;
        uint stub = Stub("cbwait:event-same", context =>
        {
            Volatile.Write(ref argument, context.Gpr(2));
            Volatile.Write(ref ranWith, context.Gpr(0));
            return 0;
        });
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateSimpleEvent", _h.Str("cb-same"), 0u, 0u, 0u);
            SimpleEvent simpleEvent = _h.Emu.Kernel.Objects.Get<SimpleEvent>(uid)!;
            int result = 1;
            int callback = 0;
            GuestRun waiter = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "event-same", 0u, stub, 0u);
                Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", uid, callback));
                uint opt = _h.Alloc(8);
                result = (int)th.DispatchForTest("_sceKernelWaitEventCB", (uint)uid, 0x02000000u, _h.Alloc(4), opt);
            }, "cb-wait-same-event");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && simpleEvent.NumWaiters == 1, "the CB wait");
            uint block = _h.Alloc(16);
            GuestMemory.W32(block + 8, 0x77);
            Assert.Equal(0, (int)main.DispatchForTest("_sceKernelSetEventWithNotifyCallback", (uint)uid, EventBit, block));
            Until(() => Volatile.Read(ref ranWith) != 0 && simpleEvent.NumWaiters == 1 && waiter.Thread.Status == ThreadStatus.Waiting, "the callback and the new wait");
            Assert.Equal((uint)uid, ranWith);
            Assert.Equal(0x77u, argument);
            Assert.Equal(0, Call(Tm, "sceKernelSetEvent", uid, 0x02000000u, 0UL));
            _h.Join(waiter, 5_000);
            Assert.Equal(0, result);
        });
    }
}
