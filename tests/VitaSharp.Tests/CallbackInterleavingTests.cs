using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Callbacks and the CB waits under adversarial interleavings, against FW 3.74 threadmgr (create 0x8100c4bc, delete
/// 0x8100c700, notify 0x8100c820, the runner 0x8100c110, cancel 0x8100ca98 and the event flag wait 0x8100f0e4, studied
/// in the decompiled module): the callbacks of a thread run in creation order with r0 = notifier ID (-1 for
/// sceKernelNotifyCallback), r1 = count, r2 = last argument, r3 = common argument; one notified again while it runs runs
/// again in the same pass; a delete during its run frees it after the run. A notification that reaches a thread asleep
/// in a CB wait takes it off the wait queue: it runs its callbacks, checks the object again and joins the queue anew,
/// with what is left of its timeout. Every step is forced by the test (callbacks are host stubs run on the owner's
/// thread, the flag's lock held across calls), never left to the host's timing.
/// </summary>
[Collection("guest")]
public sealed class CallbackInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "callback-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private EventFlag Flag(int uid) => _h.Emu.Kernel.Objects.Get<EventFlag>(uid)!;

    /// <summary>A guest address that runs <paramref name="body"/> (an HLE host stub) and returns its result.</summary>
    private uint Stub(string name, Func<HleContext, uint> body)
    {
        uint stub = _h.Alloc(16);
        VitaSharp.Core.Loader.Linker.WriteHleStub(stub, _h.Emu.Hle.RegisterHostFunction(name, context => context.Return(body(context))), 0);
        _h.Emu.Code.Invalidate(stub, 16);
        return stub;
    }

    /// <summary>The CB wait through the real dispatch path (the syscall form: opt = {outBits*, timeout*}).</summary>
    private int WaitFlagCb(GuestThread thread, int uid, uint bits, uint mode, uint outBits, uint timeout)
    {
        uint opt = _h.Alloc(8);
        GuestMemory.W32(opt, outBits);
        GuestMemory.W32(opt + 4, timeout);
        return (int)thread.DispatchForTest("_sceKernelWaitEventFlagCB", (uint)uid, bits, mode, opt);
    }

    /// <summary>
    /// The runner walks the thread's callbacks in creation order, whatever order the notifications came in, and calls
    /// each with r0 = -1 (sceKernelNotifyCallback's notifier ID), r1 = notifications since its last run, r2 = the last
    /// argument, r3 = its common argument; sceKernelCheckCallback returns how many runs there were.
    /// </summary>
    [Fact]
    public void CallbacksRunInCreationOrderWithTheFirmwaresArguments()
    {
        List<(string Name, uint R0, uint R1, uint R2, uint R3)> runs = new();
        uint[] stubs = new uint[3];
        string[] names = ["a", "b", "c"];
        for (int i = 0; i < 3; i++)
        {
            string name = names[i];
            stubs[i] = Stub($"cbtest:order-{name}", context =>
            {
                runs.Add((name, context.Gpr(0), context.Gpr(1), context.Gpr(2), context.Gpr(3)));
                return 0;
            });
        }
        _h.OnGuest(self =>
        {
            int[] uids = new int[3];
            for (int i = 0; i < 3; i++)
            {
                uids[i] = Call(Tm, "sceKernelCreateCallback", names[i], 0u, stubs[i], 0xA0u + (uint)i);
            }

            _h.Join(_h.Start(_ =>
            {
                Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", uids[2], 3));
                Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", uids[0], 1));
                Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", uids[0], 2));
                Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", uids[1], 5));
            }, "cb-notifier"));
            uint info = _h.Alloc(0x44);
            GuestMemory.W32(info, 0x44);
            Assert.Equal(0, Call(Tm, "sceKernelGetCallbackInfo", uids[0], info));
            Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(info + 0x34)); // notifyId: -1, not the notifying thread
            Assert.Equal(3, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal(
                [("a", 0xFFFFFFFFu, 2u, 2u, 0xA0u), ("b", 0xFFFFFFFFu, 1u, 5u, 0xA1u), ("c", 0xFFFFFFFFu, 1u, 3u, 0xA2u)],
                runs);
            Assert.Equal(0, self.PendingCallbacks);
            Assert.Equal(0, Call(Tm, "sceKernelGetCallbackInfo", uids[0], info));
            Assert.Equal((0u, 0u, 0u), (GuestMemory.R32(info + 0x34), GuestMemory.R32(info + 0x38), GuestMemory.R32(info + 0x3C)));
        });
    }

    /// <summary>
    /// A callback notified again while it runs (here by itself) runs again in the same pass, with the new count and
    /// argument, before the runner moves on: the check returns 2 and nothing stays pending.
    /// </summary>
    [Fact]
    public void ACallbackNotifiedWhileItRunsRunsAgainInTheSamePass()
    {
        List<(uint Count, uint Arg)> runs = new();
        int uid = 0;
        uint stub = Stub("cbtest:renotify", context =>
        {
            runs.Add((context.Gpr(1), context.Gpr(2)));
            if (runs.Count == 1)
            {
                Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", uid, 7));
            }

            return 0;
        });
        _h.OnGuest(self =>
        {
            uid = Call(Tm, "sceKernelCreateCallback", "renotify", 0u, stub, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", uid, 4));
            Assert.Equal(2, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([(1u, 4u), (1u, 7u)], runs);
            Assert.Equal(0, self.PendingCallbacks);
            Assert.Equal(0, Call(Tm, "sceKernelCheckCallback"));
        });
    }

    /// <summary>
    /// Create refuses attributes (ILLEGAL_ATTR). Delete of an unknown or already deleted UID is UNKNOWN_CALLBACK_ID; a
    /// deleted callback's notifications stop being pending and it never runs. A callback that deletes itself while it
    /// runs finishes that run, is not run again for a notification that came during it, and its UID is gone after.
    /// </summary>
    [Fact]
    public void DeleteFollowsTheFirmware()
    {
        int selfDeleting = 0;
        int ran = 0;
        uint plain = Stub("cbtest:delete-plain", _ =>
        {
            ran++;
            return 0;
        });
        uint deleting = Stub("cbtest:delete-self", _ =>
        {
            ran++;
            Assert.Equal(0, Call(Tm, "sceKernelDeleteCallback", selfDeleting));
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", selfDeleting, 1)); // the UID lives until the run ends
            return 0;
        });
        _h.OnGuest(self =>
        {
            Assert.Equal(SceError.ILLEGAL_ATTR, Call(Tm, "sceKernelCreateCallback", "attr", 1u, plain, 0u));
            Assert.Equal(SceError.UNKNOWN_CALLBACK_ID, Call(Tm, "sceKernelDeleteCallback", 0x7FFFFFF1));
            int pendingOne = Call(Tm, "sceKernelCreateCallback", "pending", 0u, plain, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", pendingOne, 1));
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", pendingOne, 2));
            Assert.Equal(2, self.PendingCallbacks);
            Assert.Equal(0, Call(Tm, "sceKernelDeleteCallback", pendingOne));
            Assert.Equal(0, self.PendingCallbacks);
            Assert.Equal(SceError.UNKNOWN_CALLBACK_ID, Call(Tm, "sceKernelDeleteCallback", pendingOne));
            Assert.Equal(0, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal(0, ran);

            selfDeleting = Call(Tm, "sceKernelCreateCallback", "self-deleting", 0u, deleting, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", selfDeleting, 1));
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal(1, ran);
            Assert.Equal(0, self.PendingCallbacks);
            Assert.Equal(SceError.UNKNOWN_CALLBACK_ID, Call(Tm, "sceKernelGetCallbackCount", selfDeleting));
            Assert.Equal(SceError.UNKNOWN_CALLBACK_ID, Call(Tm, "sceKernelDeleteCallback", selfDeleting));
        });
    }

    /// <summary>
    /// A notification takes the CB waiter at the head of the queue off it while its callback runs: a set made then
    /// (here by the callback itself) satisfies the waiter behind it, and the notified thread, finding the bit cleared,
    /// waits again, now behind the others, until the next set.
    /// </summary>
    [Fact]
    public void ANotifiedCbWaiterIsOffTheQueueWhileItsCallbacksRun()
    {
        int flagUid = 0;
        bool callbackRan = false;
        uint stub = Stub("cbtest:evf-set-in-callback", _ =>
        {
            Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", flagUid, 0x1u));
            callbackRan = true;
            return 0;
        });
        _h.OnGuest(main =>
        {
            flagUid = Call(Tm, "sceKernelCreateEventFlag", "cb-queue", EventFlag.AttrMulti, 0u, 0u);
            EventFlag flag = Flag(flagUid);
            int callback = 0;
            int notifiedResult = 1;
            int otherResult = 1;
            uint notifiedBits = _h.Word(0);
            uint otherBits = _h.Word(0);
            bool notifiedDone = false;
            bool otherDone = false;
            GuestRun notified = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "evf-cb", 0u, stub, 0u);
                notifiedResult = WaitFlagCb(th, flagUid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat, notifiedBits, 0);
                Volatile.Write(ref notifiedDone, true);
            }, "cb-evf-notified");
            Until(() => notified.Thread.Status == ThreadStatus.Waiting, "the CB wait");
            GuestRun other = _h.Start(_ =>
            {
                otherResult = Call(Tm, "sceKernelWaitEventFlag", flagUid, 0x1u, EventFlag.WaitAnd | EventFlag.WaitClearPat, new Ptr<uint>(otherBits), new Ptr<uint>(0));
                Volatile.Write(ref otherDone, true);
            }, "cb-evf-other");
            Until(() => other.Thread.Status == ThreadStatus.Waiting && flag.Waiters == 2, "the second wait");
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", callback, 0));
            Until(() => Volatile.Read(ref otherDone) || Volatile.Read(ref notifiedDone), "a wait to end");
            Assert.True(Volatile.Read(ref otherDone), "the set inside the callback went to the waiter behind");
            Until(() => callbackRan && flag.Waiters == 1 && notified.Thread.Status == ThreadStatus.Waiting, "the notified thread's new wait");
            Assert.False(Volatile.Read(ref notifiedDone));
            Assert.Equal(0, otherResult);
            Assert.Equal(0x1u, GuestMemory.R32(otherBits));
            Assert.Equal(0u, flag.Pattern);
            Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", flagUid, 0x1u));
            _h.Join(notified);
            _h.Join(other);
            Assert.Equal(0, notifiedResult);
            Assert.Equal(0x1u, GuestMemory.R32(notifiedBits));
            Assert.Equal(0u, flag.Pattern);
        });
    }

    /// <summary>
    /// A callback notified after a set satisfied a CB wait, before the waiting thread ran again (the flag's lock is held
    /// across both calls), runs before the wait returns.
    /// </summary>
    [Fact]
    public void ACallbackNotifiedAfterTheSetRunsBeforeTheCbWaitReturns()
    {
        int ran = 0;
        uint stub = Stub("cbtest:evf-after-set", _ =>
        {
            Interlocked.Increment(ref ran);
            return 0;
        });
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "cb-after-set", EventFlag.AttrMulti, 0u, 0u);
            EventFlag flag = Flag(uid);
            int callback = 0;
            int result = 1;
            int ranBeforeReturn = -1;
            uint outBits = _h.Word(0);
            GuestRun waiter = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "after-set", 0u, stub, 0u);
                result = WaitFlagCb(th, uid, 0x1u, EventFlag.WaitAnd, outBits, 0);
                ranBeforeReturn = Volatile.Read(ref ran);
            }, "cb-evf-after-set");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && flag.Waiters == 1, "the CB wait");
            lock (flag)
            {
                Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", uid, 0x1u));
                Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", callback, 0));
            }
            _h.Join(waiter);
            Assert.Equal(0, result);
            Assert.Equal(1, ranBeforeReturn);
            Assert.Equal(0, waiter.Thread.PendingCallbacks);
        });
    }

    /// <summary>
    /// A flag without WAITMULTIPLE belongs to its waiting thread until its wait ends: another thread's poll is
    /// EVF_MULTI before, during and after the owner's callback, but the owner's own poll from its callback is a normal
    /// poll (EVF_COND when the bits are not there).
    /// </summary>
    [Fact]
    public void TheOwnerOfASingleWaitFlagMayPollItFromItsCallback()
    {
        int flagUid = 0;
        int ownPoll = 1;
        int otherPollInCallback = 1;
        bool callbackRan = false;
        uint stub = Stub("cbtest:evf-owner-poll", _ =>
        {
            ownPoll = Call(Tm, "sceKernelPollEventFlag", flagUid, 0x2u, EventFlag.WaitAnd, new Ptr<uint>(0));
            Volatile.Write(ref callbackRan, true);
            return 0;
        });
        _h.OnGuest(main =>
        {
            flagUid = Call(Tm, "sceKernelCreateEventFlag", "cb-single", 0u, 0u, 0u);
            EventFlag flag = Flag(flagUid);
            int callback = 0;
            int result = 1;
            GuestRun owner = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "owner-poll", 0u, stub, 0u);
                result = WaitFlagCb(th, flagUid, 0x1u, EventFlag.WaitAnd, 0, 0);
            }, "cb-evf-owner");
            Until(() => owner.Thread.Status == ThreadStatus.Waiting && flag.Waiters == 1, "the owner's wait");
            Assert.Equal(SceError.EVF_MULTI, Call(Tm, "sceKernelPollEventFlag", flagUid, 0x2u, EventFlag.WaitAnd, new Ptr<uint>(0)));
            lock (flag)
            {
                Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", callback, 0));
                // taken off the queue for its callback, it still owns the flag's wait
                Assert.Equal(0, flag.Waiters);
                otherPollInCallback = Call(Tm, "sceKernelPollEventFlag", flagUid, 0x2u, EventFlag.WaitAnd, new Ptr<uint>(0));
            }
            Until(() => Volatile.Read(ref callbackRan) && flag.Waiters == 1, "the owner's callback and new wait");
            Assert.Equal(SceError.EVF_MULTI, otherPollInCallback);
            Assert.Equal(SceError.EVF_COND, ownPoll);
            Assert.Equal(SceError.EVF_MULTI, Call(Tm, "sceKernelPollEventFlag", flagUid, 0x2u, EventFlag.WaitAnd, new Ptr<uint>(0)));
            Assert.Equal(0, Call(Tm, "sceKernelSetEventFlag", flagUid, 0x1u));
            _h.Join(owner);
            Assert.Equal(0, result);
            Assert.Equal(0, Call(Tm, "sceKernelPollEventFlag", flagUid, 0x1u, EventFlag.WaitAnd, new Ptr<uint>(0))); // the wait ended: free again
        });
    }

    /// <summary>
    /// The timeout of a CB wait is one deadline for the whole call (the test moves guest time): a callback in the middle
    /// does not restart it, and the wait that resumes after it times out at the original deadline.
    /// </summary>
    [Fact]
    public void ACallbackInTheMiddleDoesNotRestartTheTimeout()
    {
        const uint LimitUs = 50_000;
        bool callbackRan = false;
        uint stub = Stub("cbtest:evf-timeout", _ =>
        {
            Volatile.Write(ref callbackRan, true);
            return 0;
        });
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateEventFlag", "cb-timeout", EventFlag.AttrMulti, 0u, 0u);
            EventFlag flag = Flag(uid);
            int callback = 0;
            int result = 1;
            uint timeout = _h.Word(LimitUs);
            GuestRun waiter = _h.Start(th =>
            {
                callback = Call(Tm, "sceKernelCreateCallback", "timeout-cb", 0u, stub, 0u);
                result = WaitFlagCb(th, uid, 0x1u, EventFlag.WaitAnd, 0, timeout);
            }, "cb-evf-timeout");
            Until(() => waiter.Thread.Status == ThreadStatus.Waiting && flag.Waiters == 1, "the CB wait");
            clock.Advance(20_000);
            Assert.Equal(0, Call(Tm, "sceKernelNotifyCallback", callback, 0));
            Until(() => Volatile.Read(ref callbackRan) && flag.Waiters == 1 && waiter.Thread.Status == ThreadStatus.Waiting, "the wait after the callback");
            lock (flag)
            {
                clock.Advance(LimitUs - 20_000 - 1);
                Assert.Equal(1, flag.Waiters); // a microsecond before the original deadline it still waits
                clock.Advance(1);
            }
            _h.Join(waiter);
            Assert.Equal(SceError.WAIT_TIMEOUT, result);
            Assert.Equal(0u, GuestMemory.R32(timeout));
        });
    }
}
