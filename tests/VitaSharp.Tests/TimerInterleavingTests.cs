using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The timer's event against FW 3.74 threadmgr (SceUIDTimerClass, a child of SceUIDCpuTimerClass and SceUIDEventClass;
/// the timer handler calls the event set 0x8101dcf8, or the pulse 0x8101e370, with the EVENT_TIMER bit 0x8000, user
/// data 0 and notify argument 0, and the set notifies the registered callbacks through 0x8101cfcc), and
/// sceKernelSet/PulseEventWithNotifyCallback on every object of the event class (0xB7DF3EDF / 0x714A107A look the UID up
/// in SceUIDEventClass). Guest time is moved by the test (<see cref="ControlledGuestClock"/>); each timer counts on the
/// stopped guest clock itself, so its event comes exactly when the test moves the clock past it.
/// </summary>
[Collection("guest")]
public sealed class TimerInterleavingTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _h = new(fixture, "timer-interleavings");

    private ThreadMgr Tm => _h.Module<ThreadMgr>();

    public void Dispose() => _h.Dispose();

    private const uint UserBit = 0x01000000;

    private uint Stub(string name, Func<HleContext, uint> body)
    {
        uint stub = _h.Alloc(16);
        VitaSharp.Core.Loader.Linker.WriteHleStub(stub, _h.Emu.Hle.RegisterHostFunction(name, context => context.Return(body(context))), 0);
        _h.Emu.Code.Invalidate(stub, 16);
        return stub;
    }

    /// <summary>A timer whose event is <paramref name="type"/> (0 SET, 1 PULSE) every <paramref name="interval"/> us,
    /// started now. Its count runs on the stopped guest clock (the process clock also follows the threads' emulated CPU
    /// positions, which the test does not decide).</summary>
    private (int Uid, TimerObject Timer) StartTimer(string name, uint attr, int type, ulong interval, bool repeat)
    {
        int uid = Call(Tm, "sceKernelCreateTimer", name, attr, 0u);
        TimerObject timer = _h.Emu.Kernel.Objects.Get<TimerObject>(uid)!;
        timer.Clock = static () => (ulong)GuestClock.Microseconds;
        uint word = _h.Alloc(8);
        new Ptr<ulong>(word).Value = interval;
        Assert.Equal(0, Call(Tm, "sceKernelSetTimerEvent", uid, type, new Ptr<ulong>(word), repeat ? 1 : 0));
        Assert.Equal(0, Call(Tm, "sceKernelStartTimer", uid));
        return (uid, timer);
    }

    /// <summary>Waits for a step the calls (or the timer's fire on the dispatcher) make happen at once; the host time
    /// only bounds how long a broken build hangs.</summary>
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

    private Callback CallbackObject(int uid) => _h.Emu.Kernel.Objects.Get<Callback>(uid)!;

    /// <summary>The timer's event notifies the callbacks registered to it when it comes, in guest time, with nobody
    /// reading the timer: r0 = the timer's UID, r1 = the count, r2 = 0 (the handler's notify argument).</summary>
    [Fact]
    public void TheTimersEventNotifiesItsCallbacksAtItsTime()
    {
        using var clock = ControlledGuestClock.Stop();
        List<(uint R0, uint R1, uint R2)> runs = new();
        uint stub = Stub("timer:notified", context =>
        {
            runs.Add((context.Gpr(0), context.Gpr(1), context.Gpr(2)));
            return 0;
        });
        _h.OnGuest(main =>
        {
            int callback = Call(Tm, "sceKernelCreateCallback", "timer-notified", 0u, stub, 0u);
            (int uid, _) = StartTimer("notifying-timer", 0u, type: 0, interval: 1000, repeat: false);
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", uid, callback));
            clock.Advance(999);
            Assert.Equal(0, Call(Tm, "sceKernelCheckCallback")); // not yet
            clock.Advance(2);
            Soon(() => Volatile.Read(ref CallbackObject(callback).NotifyCount) == 1, "the timer's fire notified the callback");
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([((uint)uid, 1u, 0u)], runs);
            clock.Advance(5_000);
            Assert.Equal(0, Call(Tm, "sceKernelCheckCallback")); // a one-shot event fires once
        });
    }

    /// <summary>A repeating event fires once per interval: guest time moved past three of them notifies three times
    /// (one callback run with the count 3), and the next one stays scheduled.</summary>
    [Fact]
    public void ARepeatingEventFiresOncePerInterval()
    {
        using var clock = ControlledGuestClock.Stop();
        List<(uint R0, uint R1, uint R2)> runs = new();
        uint stub = Stub("timer:repeat", context =>
        {
            runs.Add((context.Gpr(0), context.Gpr(1), context.Gpr(2)));
            return 0;
        });
        _h.OnGuest(main =>
        {
            int callback = Call(Tm, "sceKernelCreateCallback", "timer-repeat", 0u, stub, 0u);
            (int uid, _) = StartTimer("repeating-timer", 0u, type: 1, interval: 1000, repeat: true);
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", uid, callback));
            clock.Advance(3_001);
            Soon(() => Volatile.Read(ref CallbackObject(callback).NotifyCount) == 3, "three fires");
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([((uint)uid, 3u, 0u)], runs);
            uint word = _h.Alloc(8);
            Assert.Equal(0, Call(Tm, "sceKernelGetTimerEventRemainingTime", uid, new Ptr<ulong>(word)));
            Assert.InRange(new Ptr<ulong>(word).Value, 998ul, 1000ul);
        });
    }

    /// <summary>A pulse reaches every waiter in the timer's queue (the pulse 0x8101e370 walks the whole queue), not
    /// just the first, and the pattern does not keep the bit.</summary>
    [Fact]
    public void APulseReachesEveryWaiter()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            (int uid, TimerObject timer) = StartTimer("pulse-timer", 0u, type: 1, interval: 1000, repeat: false);
            uint results = _h.Alloc(8);
            int first = 1;
            int second = 1;
            GuestRun firstRun = _h.Start(_ => first = Call(Tm, "sceKernelWaitEvent", uid, EventBits.Timer, new Ptr<uint>(results), new Ptr<ulong>(0), new Ptr<uint>(0)), "pulse-first");
            Soon(() => timer.NumWaiters == 1, "the first waiter in the queue");
            GuestRun secondRun = _h.Start(_ => second = Call(Tm, "sceKernelWaitEvent", uid, EventBits.Timer, new Ptr<uint>(results + 4), new Ptr<ulong>(0), new Ptr<uint>(0)), "pulse-second");
            Soon(() => timer.NumWaiters == 2, "the second waiter behind it");
            clock.Advance(1_001);
            _h.Join(firstRun);
            _h.Join(secondRun);
            Assert.Equal((0, 0), (first, second));
            Assert.Equal((EventBits.Timer, EventBits.Timer), (GuestMemory.R32(results), GuestMemory.R32(results + 4)));
            Assert.Equal(SceError.EVENT_COND, Call(Tm, "sceKernelPollEvent", uid, EventBits.Timer, new Ptr<uint>(0), new Ptr<ulong>(0)));
        });
    }

    /// <summary>sceKernelWaitMultipleEvents is in the timer's queue as a single waiter is: the fire hands it the bit
    /// inside the fire, in queue order, so with AUTO_RESET it takes the event before a single waiter that came after it
    /// (who goes on waiting for the next fire), and a pulse reaches it too.</summary>
    [Fact]
    public void TheTimersFireTakesAMultipleEventsCallInQueueOrder()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            (int uid, TimerObject timer) = StartTimer("multi-timer", EventBits.AttrAutoReset, type: 0, interval: 1000, repeat: true);
            uint array = _h.Alloc(8);
            GuestMemory.W32(array, (uint)uid);
            GuestMemory.W32(array + 4, EventBits.Timer);
            uint results = _h.Alloc(0x18);
            int multi = 1;
            GuestRun multiRun = _h.Start(_ => multi = Call(Tm, "sceKernelWaitMultipleEvents", array, 1u, 1u, results, new Ptr<uint>(0)), "multi-timer-wait");
            Soon(() => timer.NumWaiters == 1, "the multiple-events call in the timer's queue");
            int single = 1;
            GuestRun singleRun = _h.Start(_ => single = Call(Tm, "sceKernelWaitEvent", uid, EventBits.Timer, new Ptr<uint>(0), new Ptr<ulong>(0), new Ptr<uint>(0)), "single-timer-wait");
            Soon(() => timer.NumWaiters == 2, "the single wait behind it");
            clock.Advance(1_001);
            _h.Join(multiRun);
            Assert.Equal(1, multi);
            Assert.Equal((uint)uid, GuestMemory.R32(results));
            Assert.Equal(0u, GuestMemory.R32(results + 4));
            Assert.Equal(EventBits.Timer, GuestMemory.R32(results + 8));
            Assert.Equal(1, timer.NumWaiters); // the single waiter waits for the next fire
            Assert.Equal(1, single);
            clock.Advance(1_000);
            _h.Join(singleRun);
            Assert.Equal(0, single);
        });
    }

    /// <summary>A pulse timer in sceKernelWaitMultipleEvents: the pulse is never in the pattern, so only the call in
    /// the queue sees it (a call reading the timer's state never would).</summary>
    [Fact]
    public void APulseTimerEndsAMultipleEventsCall()
    {
        using var clock = ControlledGuestClock.Stop();
        _h.OnGuest(main =>
        {
            (int uid, TimerObject timer) = StartTimer("multi-pulse-timer", 0u, type: 1, interval: 1000, repeat: false);
            uint array = _h.Alloc(8);
            GuestMemory.W32(array, (uint)uid);
            GuestMemory.W32(array + 4, EventBits.Timer);
            uint results = _h.Alloc(0x18);
            int multi = 1;
            GuestRun multiRun = _h.Start(_ => multi = Call(Tm, "sceKernelWaitMultipleEvents", array, 1u, 1u, results, new Ptr<uint>(0)), "multi-pulse-wait");
            Soon(() => timer.NumWaiters == 1, "the multiple-events call in the timer's queue");
            clock.Advance(1_001);
            _h.Join(multiRun);
            Assert.Equal(1, multi);
            Assert.Equal(EventBits.Timer, GuestMemory.R32(results + 8));
        });
    }

    /// <summary>sceKernelSet/PulseEventWithNotifyCallback takes any object of the event class: on a timer the user bits
    /// go through the timer's event (a waiter for them wakes, a poll sees them with the user data; a pulse leaves
    /// nothing) and its callbacks are notified with the argument; on a message pipe the callbacks are notified; another
    /// object is UNKNOWN_EVENT_ID, and non-user bits are ILLEGAL_PATTERN.</summary>
    [Fact]
    public void NotifyCallbackSetsTakeEveryEventClassObject()
    {
        List<(uint R0, uint R2)> runs = new();
        uint stub = Stub("timer:notify-set", context =>
        {
            runs.Add((context.Gpr(0), context.Gpr(2)));
            return 0;
        });
        _h.OnGuest(main =>
        {
            int callback = Call(Tm, "sceKernelCreateCallback", "notify-set", 0u, stub, 0u);
            int timer = Call(Tm, "sceKernelCreateTimer", "notify-set-timer", 0u, 0u);
            int pipe = Call(Tm, "sceKernelCreateMsgPipe", "notify-set-pipe", 0x40u, 0u, 0x1000u, 0u);
            int sema = Call(Tm, "sceKernelCreateSema", "notify-set-sema", 0u, 0, 1, 0u);
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", timer, callback));
            Assert.Equal(0, Call(Tm, "sceKernelRegisterCallbackToEvent", pipe, callback));

            TimerObject timerObject = _h.Emu.Kernel.Objects.Get<TimerObject>(timer)!;
            uint result = _h.Alloc(16);
            int woken = 1;
            GuestRun waiter = _h.Start(_ => woken = Call(Tm, "sceKernelWaitEvent", timer, UserBit, new Ptr<uint>(result), new Ptr<ulong>(result + 8), new Ptr<uint>(0)), "user-bit-wait");
            Soon(() => timerObject.NumWaiters == 1, "the wait for the user bit");
            Assert.Equal(0, Call(Tm, "sceKernelPulseEventWithNotifyCallback", timer, UserBit, 4UL, 8));
            _h.Join(waiter);
            Assert.Equal(0, woken);
            Assert.Equal(UserBit, GuestMemory.R32(result));
            Assert.Equal(4UL, GuestMemory.Read<ulong>(result + 8));
            Assert.Equal(SceError.EVENT_COND, Call(Tm, "sceKernelPollEvent", timer, UserBit, new Ptr<uint>(0), new Ptr<ulong>(0)));
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([((uint)timer, 8u)], runs);
            runs.Clear();

            Assert.Equal(0, Call(Tm, "sceKernelSetEventWithNotifyCallback", timer, UserBit, 5UL, 9));
            Assert.Equal(0, Call(Tm, "sceKernelPollEvent", timer, UserBit, new Ptr<uint>(result), new Ptr<ulong>(result + 8)));
            Assert.Equal(UserBit, GuestMemory.R32(result));
            Assert.Equal(5UL, GuestMemory.Read<ulong>(result + 8));
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([((uint)timer, 9u)], runs);
            runs.Clear();

            Assert.Equal(0, Call(Tm, "sceKernelSetEventWithNotifyCallback", pipe, UserBit, 0UL, 3));
            Assert.Equal(1, Call(Tm, "sceKernelCheckCallback"));
            Assert.Equal([((uint)pipe, 3u)], runs);

            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelSetEventWithNotifyCallback", sema, UserBit, 0UL, 0));
            Assert.Equal(SceError.UNKNOWN_EVENT_ID, Call(Tm, "sceKernelPulseEventWithNotifyCallback", sema, UserBit, 0UL, 0));
            Assert.Equal(SceError.ILLEGAL_PATTERN, Call(Tm, "sceKernelSetEventWithNotifyCallback", timer, EventBits.Timer, 0UL, 0));
        });
    }

    /// <summary>The user bits and user data of a timer's event are in the save state; a record of a build before them
    /// loads with none, and an armed event fires again after the load.</summary>
    [Fact]
    public void ASaveStateKeepsTheTimersEvent()
    {
        _h.OnGuest(main =>
        {
            int uid = Call(Tm, "sceKernelCreateTimer", "saved-timer-event", 0u, 0u);
            TimerObject timer = _h.Emu.Kernel.Objects.Get<TimerObject>(uid)!;
            Assert.Equal(0, Call(Tm, "sceKernelSetEventWithNotifyCallback", uid, UserBit, 6UL, 0));
            var state = new VitaSharp.Core.SaveState.SnapshotWriter();
            timer.Save(state);
            var restored = new TimerObject { Name = "saved-timer-event", Clock = () => 0 };
            restored.Load(new VitaSharp.Core.SaveState.SnapshotReader(state.Data.ToArray()));
            Assert.Equal((UserBit, 6UL), (restored.UserPattern, restored.LastUserData));

            byte[] legacy = state.Data.ToArray()[..^12]; // the record as the builds before the user bits wrote it
            var old = new TimerObject { Name = "saved-timer-event", Clock = () => 0 };
            old.Load(new VitaSharp.Core.SaveState.SnapshotReader(legacy));
            Assert.Equal((0u, 0UL), (old.UserPattern, old.LastUserData));
        });
    }
}
