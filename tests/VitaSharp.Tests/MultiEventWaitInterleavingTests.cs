using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// sceKernelWaitMultipleEvents under adversarial interleavings, against FW 3.74 threadmgr (the syscalls 0x8102c950 /
/// 0x8102cc4c, the wait 0x8101ed4c and the event set 0x8101dcf8, studied in the decompiled module): one wait in several
/// events at once, in each simple event's queue as a single waiter is; the set (or pulse) that reaches it there takes the
/// event inside the set, in queue order with the other waiters, and the results come in the order the events fired. The
/// sets are issued while the test holds the events' locks where the order matters, so the outcome is decided by the
/// calls, never by the host's timing.
/// </summary>
[Collection("guest")]
public sealed class MultiEventWaitInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "multi-event-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private const uint BitA = 0x01000000, BitB = 0x02000000;

    private int Event(string name, bool autoReset) => Call(Tm, "sceKernelCreateSimpleEvent", _h.Str(name), autoReset ? EventBits.AttrAutoReset : 0u, 0u, 0u);

    private SimpleEvent Simple(int uid) => _h.Emu.Kernel.Objects.Get<SimpleEvent>(uid)!;

    /// <summary>One multiple-events call on its own thread: the events array, the results array and the return value.</summary>
    private sealed class MultiCall
    {
        public GuestRun Run = null!;
        public uint Results;
        public int Returned = 1;
    }

    /// <summary>Starts a thread whose sceKernelWaitMultipleEvents waits for <paramref name="events"/> (uid, pattern) in
    /// OR (<paramref name="or"/>) or AND mode, and returns once it is in the queue of the simple events it waits for.</summary>
    private MultiCall StartMulti((int Uid, uint Pattern)[] events, bool or, int queuedOn)
    {
        uint array = _h.Alloc((uint)events.Length * 8);
        for (int i = 0; i < events.Length; i++)
        {
            GuestMemory.W32(array + (uint)i * 8, (uint)events[i].Uid);
            GuestMemory.W32(array + (uint)i * 8 + 4, events[i].Pattern);
        }

        var call = new MultiCall { Results = _h.Alloc((uint)events.Length * 0x18) };
        call.Run = _h.Start(_ => call.Returned = Call(Tm, "sceKernelWaitMultipleEvents", array, (uint)events.Length, or ? 1u : 2u, call.Results, new Ptr<uint>(0)), "multi-wait");
        Soon(() => call.Run.Thread.Status == ThreadStatus.Waiting && Simple(queuedOn).NumWaiters >= 1, "the multiple-events wait in the queue");
        return call;
    }

    /// <summary>Waits for a step the calls make happen at once; the host time only bounds how long a broken build hangs.</summary>
    private static void Soon(Func<bool> done, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > 5_000)
            {
                throw new Xunit.Sdk.XunitException($"never reached: {what}");
            }

            Thread.Sleep(1);
        }
    }

    private static (int Uid, int Code, uint Pattern, ulong UserData) Entry(uint results, int index)
    {
        uint entry = results + (uint)index * 0x18;
        return ((int)GuestMemory.R32(entry), (int)GuestMemory.R32(entry + 4), GuestMemory.R32(entry + 8), GuestMemory.Read<ulong>(entry + 16));
    }

    /// <summary>A pulse is seen by the waiters already in the queue, the multiple-events call among them: it ends with
    /// the pulse's bits and user data, though the pattern does not keep them.</summary>
    [Fact]
    public void APulseReachesTheWaiter()
    {
        _h.OnGuest(main =>
        {
            int a = Event("multi-pulse", autoReset: false);
            MultiCall call = StartMulti([(a, BitA)], or: true, queuedOn: a);
            Assert.Equal(0, Call(Tm, "sceKernelPulseEvent", a, BitA, 7UL));
            _h.Join(call.Run, 5_000);
            Assert.Equal(1, call.Returned);
            Assert.Equal((a, 0, BitA, 7UL), Entry(call.Results, 0));
            Assert.Equal(EventBits.Create, Simple(a).Pattern);
        });
    }

    /// <summary>The call is in an auto-reset event's queue in its arrival order: a set reaches it before a single waiter
    /// that came after it, inside the set (the single waiter goes on waiting), and the next set goes to that waiter.</summary>
    [Fact]
    public void TheQueueOrderDecidesWhoTakesTheSet()
    {
        _h.OnGuest(main =>
        {
            int a = Event("multi-order", autoReset: true);
            SimpleEvent simple = Simple(a);
            MultiCall call = StartMulti([(a, BitA)], or: true, queuedOn: a);
            int single = 1;
            GuestRun singleRun = _h.Start(_ => single = Call(Tm, "sceKernelWaitEvent", a, BitA, new Ptr<uint>(0), new Ptr<ulong>(0), new Ptr<uint>(0)), "single-wait");
            Soon(() => simple.NumWaiters == 2, "the single wait behind it");
            lock (simple)
            {
                Assert.Equal(0, Call(Tm, "sceKernelSetEvent", a, BitA, 1UL));
                Assert.Equal(1, simple.NumWaiters); // the single waiter still waits
            }
            _h.Join(call.Run, 5_000);
            Assert.Equal(1, call.Returned);
            Assert.Equal((a, 0, BitA, 1UL), Entry(call.Results, 0));
            Assert.Equal(0, Call(Tm, "sceKernelSetEvent", a, BitA, 2UL));
            _h.Join(singleRun, 5_000);
            Assert.Equal(0, single);
        });
    }

    /// <summary>An AND call's results come in the order its events fired, not in the order of its array.</summary>
    [Fact]
    public void ResultsComeInTheOrderTheEventsFired()
    {
        _h.OnGuest(main =>
        {
            int a = Event("multi-and-a", autoReset: false);
            int b = Event("multi-and-b", autoReset: false);
            MultiCall call = StartMulti([(a, BitA), (b, BitB)], or: false, queuedOn: b);
            lock (Simple(a))
            {
                lock (Simple(b))
                {
                    Assert.Equal(0, Call(Tm, "sceKernelSetEvent", b, BitB, 2UL));
                    Assert.Equal(0, Call(Tm, "sceKernelSetEvent", a, BitA, 1UL));
                }
            }
            _h.Join(call.Run, 5_000);
            Assert.Equal(2, call.Returned);
            Assert.Equal((b, 0, BitB, 2UL), Entry(call.Results, 0));
            Assert.Equal((a, 0, BitA, 1UL), Entry(call.Results, 1));
        });
    }

    /// <summary>An OR call ends at the first event that fires: an auto-reset event set right after it is not taken by
    /// the call and keeps its bit for the next waiter.</summary>
    [Fact]
    public void AnOrCallTakesOnlyTheFirstEventThatFires()
    {
        _h.OnGuest(main =>
        {
            int a = Event("multi-or-a", autoReset: true);
            int b = Event("multi-or-b", autoReset: true);
            MultiCall call = StartMulti([(a, BitA), (b, BitB)], or: true, queuedOn: b);
            lock (Simple(a))
            {
                lock (Simple(b))
                {
                    Assert.Equal(0, Call(Tm, "sceKernelSetEvent", b, BitB, 2UL));
                    Assert.Equal(0, Call(Tm, "sceKernelSetEvent", a, BitA, 1UL));
                }
            }
            _h.Join(call.Run, 5_000);
            Assert.Equal(1, call.Returned);
            Assert.Equal((b, 0, BitB, 2UL), Entry(call.Results, 0));
            Assert.Equal(EventBits.Create | BitA, Simple(a).Pattern); // a's bit stayed
            Assert.Equal(EventBits.Create, Simple(b).Pattern);
        });
    }

    /// <summary>A cancel of one of the events ends that event's part of the call with {uid, WAIT_CANCEL, ERROR}; in OR
    /// mode that ends the call.</summary>
    [Fact]
    public void ACancelEndsTheEventWithItsError()
    {
        _h.OnGuest(main =>
        {
            int a = Event("multi-cancel", autoReset: false);
            MultiCall call = StartMulti([(a, BitA)], or: true, queuedOn: a);
            uint numWait = _h.Word(9);
            Assert.Equal(0, Call(Tm, "sceKernelCancelEvent", a, new Ptr<uint>(numWait)));
            Assert.Equal(1u, GuestMemory.R32(numWait));
            _h.Join(call.Run, 5_000);
            Assert.Equal(1, call.Returned);
            Assert.Equal((a, SceError.WAIT_CANCEL, EventBits.Error, 0UL), Entry(call.Results, 0));
        });
    }

    /// <summary>A message pipe in the call is queued in the pipe's event core: the send ends the call with IN inside
    /// it, with the user data { result 0, the 4 bytes moved }.</summary>
    [Fact]
    public void APipeSendEndsTheCall()
    {
        _h.OnGuest(main =>
        {
            int pipe = Call(Tm, "sceKernelCreateMsgPipe", "multi-pipe", 0x40u, 0u, 0x1000u, 0u);
            int a = Event("multi-pipe-a", autoReset: false);
            MultiCall call = StartMulti([(a, BitA), (pipe, MsgPipe.EventIn)], or: true, queuedOn: a);
            Assert.Equal(0, Call(Tm, "sceKernelSendMsgPipe", pipe, _h.Alloc(4), 4u, MsgPipe.WaitAll, new Ptr<uint>(0), new Ptr<uint>(0)));
            _h.Join(call.Run, 5_000);
            Assert.Equal(1, call.Returned);
            Assert.Equal((pipe, 0, MsgPipe.EventIn, 4UL << 32), Entry(call.Results, 0));
            Assert.Equal(0, Simple(a).NumWaiters); // its place in the other queue went away
        });
    }
}
