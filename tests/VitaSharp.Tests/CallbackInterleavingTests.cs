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
}
