using System.Diagnostics;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

/// <summary>Blocking-wait helper with Vita semantics: timeout in microseconds, remaining time written back.</summary>
public static class Wait
{
    /// <summary>Wake-up latency of a blocking syscall on the Vita (kernel entry, scheduler, context switch on a 333 MHz
    /// Cortex-A9): tens of microseconds. Host wake-ups take ~1 us, which makes job systems that measure their own
    /// synchronisation-heavy work (physics steps with per-step time budgets) behave differently; 0 disables.
    /// VITASHARP_SYSCALL_LATENCY_US overrides it (timing bisection).</summary>
    public static int SyscallLatencyUs = int.TryParse(Environment.GetEnvironmentVariable(EnvVars.SyscallLatencyUs), out int lat) ? lat : 30;

    /// <summary>Statistics (PERF sched): host waits inside blocking syscalls, and wake-up latencies served.</summary>
    public static long BlockingWaits, Latencies;

    /// <summary>Serves the wake-up latency of a wait that <see cref="UntilGranted"/> ended, once the caller has left the
    /// sync object's lock.</summary>
    public static void WakeLatency() => Latency();

    private static void Latency()
    {
        if (SyscallLatencyUs <= 0 || GuestClock.Turbo)
        {
            return;
        }

        Interlocked.Increment(ref Latencies);
        PreciseSleep.Until(System.Diagnostics.Stopwatch.GetTimestamp() + SyscallLatencyUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000);
    }

    /// <summary>
    /// Waits on <paramref name="sync"/> (which the caller holds) until <paramref name="ready"/> returns true.
    /// Returns OK, WAIT_TIMEOUT, or WAIT_DELETE/WAIT_CANCEL when <paramref name="abort"/> reports it.
    /// </summary>
    public static int Until(object sync, Func<bool> ready, Ptr<uint> timeout, Func<int>? abort = null, string what = "")
        => Until(sync, (ready, abort), static s => s.ready(), timeout, static s => s.abort?.Invoke() ?? 0, what);

    /// <summary>
    /// Same as <see cref="Until(object, Func{bool}, Ptr{uint}, Func{int}?, string)"/> with the predicate state passed
    /// explicitly, so sync objects can use static lambdas: every blocking syscall used to allocate two closures.
    /// </summary>
    public static int Until<T>(object sync, T state, Func<T, bool> ready, Ptr<uint> timeout, Func<T, int>? abort, string what = "")
        => UntilCore(sync, state, ready, timeout, timeout.IsNull ? null : timeout.Value, abort, what);

    /// <summary>Same as <see cref="Until(object, Func{bool}, Ptr{uint}, Func{int}?, string)"/> with the timeout passed by
    /// value (microseconds; null = wait forever). For APIs such as sceNetEpollWait or sceRudpPollWait whose timeout is an
    /// argument rather than a guest pointer.</summary>
    public static int Until(object sync, Func<bool> ready, long? timeoutUs, Func<int>? abort = null, string what = "")
        => UntilCore(sync, (ready, abort), static s => s.ready(), default, timeoutUs, static s => s.abort?.Invoke() ?? 0, what, out _, deferLatency: false);

    /// <summary>
    /// For a sync object whose releasing call hands the waiter its result (the kernel's way: sceKernelSetEventFlag
    /// satisfies a waiter and applies its clear mode before it returns): the same wait, but the wake-up latency is not
    /// served while the caller holds <paramref name="sync"/>. <paramref name="woken"/> says whether the thread slept; the
    /// caller then calls <see cref="WakeLatency"/> after leaving the lock. Serving it inside the lock kept the object
    /// locked for the latency, so a thread calling into the same object right after waking the waiter blocked on the
    /// host lock and depended on the host scheduler to run again (Metal Gear Solid HD: main's sceKernelClearEventFlag
    /// stalled tens of milliseconds behind the End Frame thread it had just woken, and cleared that thread's next
    /// "done" bit).
    /// </summary>
    public static int UntilGranted<T>(object sync, T state, Func<T, bool> ready, Ptr<uint> timeout, Func<T, int>? abort, string what, out bool woken)
        => UntilCore(sync, state, ready, timeout, timeout.IsNull ? null : timeout.Value, abort, what, out woken, deferLatency: true);

    private static int UntilCore<T>(object sync, T state, Func<T, bool> ready, Ptr<uint> timeout, long? limitUs, Func<T, int>? abort, string what)
        => UntilCore(sync, state, ready, timeout, limitUs, abort, what, out _, deferLatency: false);

    private static int UntilCore<T>(object sync, T state, Func<T, bool> ready, Ptr<uint> timeout, long? limitUs, Func<T, int>? abort, string what, out bool woken, bool deferLatency)
    {
        woken = false;
        GuestThread? self = GuestThread.Current;
        if (self != null)
        {
            self.Status = ThreadStatus.Waiting;
            self.WaitingOn = what;
            self.CurrentWaitSync = sync;
        }
        try
        {
            long start = GuestClock.Ticks; // timeouts run on guest time
            bool waited = false;
            while (true)
            {
                if (abort != null)
                {
                    int a = abort(state);
                    if (a != 0)
                    {
                        if (TakeCoreBack(sync, self))
                        {
                            continue; // the lock was left: look again with the core
                        }

                        // threadmgr wakes a cancelled or deleted waiter as a release does (the canceller goes on): the
                        // object's last signaller, the canceller, is its waker. A timeout and a callback notification
                        // are not a thread's doing
                        if (self != null && a != SceError.WAIT_TIMEOUT && a != SceError.NOTIFY_CALLBACK && sync is KernelObject cancelled)
                        {
                            self.WokenBy(cancelled.Signaller, cancelled.SignallerProgress, cancelled.SignallerCoreReleases);
                        }

                        return a;
                    }
                }
                if (ready(state))
                {
                    if (TakeCoreBack(sync, self))
                    {
                        continue; // the lock was left: another thread may have taken the object meanwhile
                    }

                    woken = waited;
                    // woken by, or found ready after, the object's last signaller: it is this thread's waker
                    if (self != null && sync is KernelObject kernelObject)
                    {
                        self.WokenBy(kernelObject.Signaller, kernelObject.SignallerProgress, kernelObject.SignallerCoreReleases);
                    }
                    if (waited && !deferLatency)
                    {
                        Latency();
                    }

                    // a wait that slept ended at the object's last signal, if it came during the wait
                    long signalled = waited && sync is KernelObject signalledObject ? signalledObject.SignalTicks : long.MinValue;
                    WriteRemaining(timeout, limitUs, start, signalled);
                    return SceError.OK;
                }
                if (self != null && self.Core.StopRequested)
                {
                    throw new GuestThreadExitException(0, false);
                }

                if (self != null && self.CanRunCallbacks && Volatile.Read(ref self.PendingCallbacks) > 0 && GuestThread.CallbackProcessor != null)
                {
                    if (TakeCoreBack(sync, self))
                    {
                        continue;
                    }

                    Monitor.Exit(sync);
                    try
                    {
                        GuestThread.CallbackProcessor(self);
                    }
                    finally
                    {
                        Monitor.Enter(sync);
                        // the callback ran as Running (and a wait inside it ended as Running): this wait goes on, so
                        // thread dumps and the watchdog show it waiting on its object again, not "Running"
                        self.Status = ThreadStatus.Waiting;
                        self.WaitingOn = what;
                        self.CurrentWaitSync = sync;
                    }
                    continue;
                }
                int sliceMs = 50;
                if (limitUs.HasValue)
                {
                    long leftUs = limitUs.Value - ElapsedUs(start);
                    if (leftUs <= 0)
                    {
                        if (TakeCoreBack(sync, self))
                        {
                            continue; // ready may have come while the lock was left
                        }

                        timeout.TrySet(0);
                        return SceError.WAIT_TIMEOUT;
                    }
                    sliceMs = (int)Math.Clamp((leftUs / GuestClock.Speed + 999) / 1000, 1, 50);
                    if (GuestClock.Turbo)
                    {
                        // turbo: give the event a moment, then jump guest time to the timeout instead of waiting it out
                        if (!Monitor.Wait(sync, 1))
                        {
                            GuestClock.WaitUntil(start + GuestClock.FromSeconds(limitUs.Value / 1e6));
                        }

                        continue;
                    }
                }
                // blocked: give the emulated core away; take one back before touching guest state again (never while
                // holding sync, or a core-holding signaller could deadlock against us)
                if (self != null)
                {
                    Scheduler.Release(self);
                    self.WokenBy(null, 0, 0); // a thread that blocks is no longer ahead of an earlier waker
                }

                Interlocked.Increment(ref BlockingWaits);
                Monitor.Wait(sync, sliceMs);
                waited = true;
                // no core is taken back just to look again: on the Vita a waiting thread uses no core until it is woken
                // or times out. Taking one after every 50 ms slice put every waiting thread of the process back in the
                // core queue, and under strict priority the low-priority threads never got the core (the shell on its
                // one system core: ScePafJobDefault and its job queues 94-100% waiting for a core, no frame after 30 s)
            }
        }
        finally
        {
            if (self != null)
            {
                self.Status = ThreadStatus.Running;
                self.WaitingOn = "";
                self.CurrentWaitSync = null;
            }
        }
    }

    /// <summary>
    /// The waiting thread takes an emulated core back before it leaves the wait (or runs callbacks): never while it
    /// holds <paramref name="sync"/>, or a core-holding signaller that wants the lock could deadlock against it. True
    /// when it had to leave the lock: the caller then looks at the object again (with the core), because another
    /// thread may have changed it meanwhile (a mutex seen free and taken by a running thread: the TestGame's two-thread
    /// mutex lost increments when the first look was trusted).
    /// </summary>
    private static bool TakeCoreBack(object sync, GuestThread? self)
    {
        if (self == null || self.HasCore)
        {
            return false;
        }

        Monitor.Exit(sync);
        try
        {
            Scheduler.Acquire(self);
        }
        finally
        {
            Monitor.Enter(sync);
        }

        return true;
    }

    private static long ElapsedUs(long startTicks) => (long)((GuestClock.Ticks - startTicks) * 1_000_000 / Stopwatch.Frequency * TimeScale.Wait);

    /// <summary>A wait that started at <paramref name="startTicks"/> with a limit of <paramref name="limitUs"/> has run
    /// out of guest time (the clock and scale every wait's timeout uses).</summary>
    internal static bool Expired(long startTicks, long limitUs) => ElapsedUs(startTicks) >= limitUs;

    /// <summary>What is left, in microseconds, of a wait's limit that started at <paramref name="startTicks"/> (guest
    /// time; negative once it ran out).</summary>
    internal static long RemainingUs(long startTicks, long limitUs) => limitUs - ElapsedUs(startTicks);

    /// <summary>The thread is in a CB wait with notified callbacks to run (threadmgr 3.74: the thread's "callbacks
    /// notified" bit, which every CB wait loop checks before it looks at its object and when it wakes up).</summary>
    internal static bool CallbacksPending(GuestThread? thread)
        => thread != null && thread.CanRunCallbacks && GuestThread.CallbackProcessor != null && Volatile.Read(ref thread.PendingCallbacks) > 0;

    /// <summary>Runs the thread's notified callbacks outside <paramref name="sync"/>'s lock (they are guest code that may
    /// call the object), taking the lock back after them.</summary>
    internal static void RunCallbacks(object sync, GuestThread thread)
    {
        Monitor.Exit(sync);
        try
        {
            GuestThread.CallbackProcessor!(thread);
        }
        finally
        {
            Monitor.Enter(sync);
        }
    }

    /// <summary>
    /// The timeout of a CB wait, one deadline for the whole call (threadmgr 3.74 keeps one timer across the loop of
    /// every CB wait: a callback notification ends the sleep with NOTIFY_CALLBACK, the thread runs its callbacks and
    /// sleeps again for what is left).
    /// </summary>
    internal struct CallbackWaitDeadline
    {
        private long _startTicks;
        private long _limitUs;

        /// <summary>A callback notification ended a sleep of this call.</summary>
        public bool Interrupted;

        public static CallbackWaitDeadline Start(Ptr<uint> timeout)
        {
            return new CallbackWaitDeadline { _startTicks = GuestClock.Ticks, _limitUs = timeout.IsNull ? -1 : timeout.Value };
        }

        /// <summary>Before a sleep after an interruption: writes what is left of the caller's timeout to it (the sleep
        /// waits that long). False when nothing is left: the call ends with WAIT_TIMEOUT (the timeout reads 0).</summary>
        public readonly bool PrepareSleep(Ptr<uint> timeout)
        {
            if (!Interrupted || _limitUs < 0)
            {
                return true;
            }

            long leftUs = Math.Max(0, RemainingUs(_startTicks, _limitUs));
            timeout.Value = (uint)leftUs;
            return leftUs > 0;
        }

        /// <summary>The call ends without sleeping again after an interruption: writes what is left of its timeout.</summary>
        public readonly void WriteLeft(Ptr<uint> timeout)
        {
            if (Interrupted && _limitUs >= 0)
            {
                timeout.TrySet((uint)Math.Max(0, RemainingUs(_startTicks, _limitUs)));
            }
        }
    }

    /// <summary>Writes back the time left of a wait that ended at <paramref name="endedTicks"/> (the signal that woke it;
    /// a value outside the wait, such as long.MinValue, means now).</summary>
    private static void WriteRemaining(Ptr<uint> timeout, long? limitUs, long startTicks, long endedTicks = long.MinValue)
    {
        if (timeout.IsNull || !limitUs.HasValue)
        {
            return;
        }

        long now = GuestClock.Ticks;
        long ended = endedTicks >= startTicks && endedTicks <= now ? endedTicks : now;
        long left = limitUs.Value - (long)((ended - startTicks) * 1_000_000 / Stopwatch.Frequency * TimeScale.Wait);
        timeout.Value = (uint)Math.Max(0, left);
    }

    /// <summary>Delays at least this long (microseconds) spin their last half millisecond to end on time (frame limiters
    /// sleep the rest of a frame); shorter ones only sleep: they are polling loops (DelayThread(1 ms) around a mutex,
    /// Ren'Py's timer and save threads), where the spin took half a host core per thread and the host timer's
    /// ~0.3 ms overshoot does not matter (the thread still resumes at the deadline in guest time, CpuCore.WokeAt). The
    /// spin then costs at most a tenth of a core. VITASHARP_DELAY_SPIN=1 spins on every delay (the old behaviour),
    /// =0 on none.</summary>
    private static readonly ulong s_delaySpinMinUs = Environment.GetEnvironmentVariable(EnvVars.DelaySpin) switch { "1" => 0, "0" => ulong.MaxValue, _ => 5000 };

    /// <summary>Statistics (PERF delays): guest delays per thread name, by length (0, up to 100 us, up to 1 ms, up to
    /// 5 ms, longer).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long[]> Delays = new();

    private static void CountDelay(GuestThread t, ulong us)
    {
        long[] a = Delays.GetOrAdd(t.Name ?? "?", static _ => new long[8]);
        Interlocked.Increment(ref a[us == 0 ? 0 : us <= 100 ? 1 : us <= 1000 ? 2 : us <= 5000 ? 3 : 4]);
    }

    /// <summary>How far ahead of the clock a delay may start because of <see cref="GuestClock.ObservedFloor"/> (a paced
    /// thread runs up to ~1 ms ahead before it sleeps; the cap keeps a stray reading from stretching every delay).</summary>
    private static readonly long s_maxObservedLead = Stopwatch.Frequency / 200; // 5 ms

    /// <summary>Plain sleep that stays responsive to emulator shutdown.</summary>
    public static void Sleep(ulong microseconds)
    {
        GuestThread? self = GuestThread.Current;
        if (self != null)
        {
            CountDelay(self, microseconds);
        }

        if (microseconds == 0)
        {
            Thread.Yield();
            Latency();
            return;
        }
        if (microseconds < (ulong)SyscallLatencyUs)
        {
            microseconds = (ulong)SyscallLatencyUs; // a delay cannot be shorter than a reschedule
        }
        // guest time: follows the test speed factor, and turbo mode skips the wait
        Scheduler.Blocked blocked = Scheduler.Block();
        try
        {
            // the delay starts where the thread's emulated CPU time is, and it resumes at the deadline however late the
            // host wakes it (CpuCore.WokeAt): pacing sleeps and timer wake-ups overshoot by fractions of a millisecond
            // nor before the process time the guest has already read (GuestClock.Observed runs ahead of real time by
            // up to a paced thread's lead): on the Vita the process time measured around a delay is never shorter
            long start = Math.Max(self?.Core.BlockedAt ?? GuestClock.Ticks, Math.Min(GuestClock.ObservedFloor, GuestClock.Ticks + s_maxObservedLead));
            long end = start + GuestClock.FromSeconds(microseconds / 1e6 / TimeScale.Wait), slice = GuestClock.FromSeconds(0.05);
            bool spin = microseconds >= s_delaySpinMinUs, slept = false;
            // sceKernelDelayThreadCB: callbacks notified during the delay run inside it, as they arrive, and the delay
            // goes on. Jak and Daxter's vblank-callback thread sits in DelayThreadCB(0xFFFFFFFF) and counts vblanks in
            // its callback: run only on return, its callback never ran and the game's vsync counter stood still.
            bool callbacks = self != null && self.CanRunCallbacks && GuestThread.CallbackProcessor != null;
            while (GuestClock.Ticks < end)
            {
                if (self != null && self.Core.StopRequested)
                {
                    throw new GuestThreadExitException(0, false);
                }

                if (callbacks)
                {
                    if (Volatile.Read(ref self!.PendingCallbacks) > 0)
                    {
                        blocked.Dispose(); // the callback is guest code: it runs holding an emulated core
                        try
                        {
                            GuestThread.CallbackProcessor!(self);
                        }
                        finally
                        {
                            blocked = Scheduler.Block();
                        }
                        continue;
                    }
                    // Callback.Notify pulses the owner's WaitLock
                    long left = Math.Min(end, GuestClock.Ticks + slice) - GuestClock.Ticks;
                    int ms = (int)Math.Clamp(left * 1000.0 / System.Diagnostics.Stopwatch.Frequency / GuestClock.Speed + 1, 1, 50);
                    lock (self.WaitLock)
                    {
                        if (Volatile.Read(ref self.PendingCallbacks) == 0)
                        {
                            Monitor.Wait(self.WaitLock, ms);
                        }
                    }

                    continue;
                }
                // sleep in slices so a stop request is noticed; short delays really sleep instead of spinning a host core
                GuestClock.WaitUntil(Math.Min(end, GuestClock.Ticks + slice), spin);
                slept = true;
            }
            if (slept && self != null && Delays.TryGetValue(self.Name ?? "?", out long[]? st))
            {
                // statistics: delays that slept on the host, total requested and total overshoot past the deadline (us)
                Interlocked.Increment(ref st[5]);
                Interlocked.Add(ref st[6], (long)microseconds);
                Interlocked.Add(ref st[7], (long)((GuestClock.Ticks - end) * 1e6 / System.Diagnostics.Stopwatch.Frequency));
            }
            self?.Core.WokeAt(end);
        }
        finally
        {
            blocked.Dispose();
        }
    }
}

/// <summary>
/// sceKernel*Sema, as threadmgr does it (FW 3.74): a count and a wait queue, first come first served or, with
/// SCE_KERNEL_ATTR_TH_PRIO, by thread priority (first come first served among equal priorities). A signal hands the
/// units to the waiters in queue order, each one taking all it asked for, and stops at the first one they do not
/// cover; the waiters it reached return OK whenever their threads run again and never look at the count. A wait or a
/// poll takes units at once only when no waiter is ahead of it (any waiter in a FIFO semaphore, one of equal or higher
/// priority in a TH_PRIO one). A waiter whose timeout passes leaves the queue, and the units go on to the waiters behind
/// it (the class's wait-release handler); a cancel or a delete takes every waiter off the queue at once.
/// </summary>
public sealed partial class Semaphore : KernelObject, ICallbackInterruptedWait
{
    public override int UidClass => 2;
    /// <summary>SCE_KERNEL_ATTR_TH_PRIO: waiters queue by thread priority instead of first come first served.</summary>
    public const uint AttrThreadPriority = 0x2000;
    public uint Attr;
    public int InitCount, Count, MaxCount;
    /// <summary>Threads in the wait queue (SceKernelSemaInfo.numWaitThreads).</summary>
    public int Waiters;
    private bool _deleted;

    /// <summary>One thread in sceKernelWaitSema.</summary>
    private sealed class WaitRequest
    {
        public int Need;
        public int Priority;
        public int ThreadUid;
        /// <summary>The waiting thread (null: a host caller, or a restored waiter until its thread reissues the wait).</summary>
        public GuestThread? Thread;
        /// <summary>A signal (or a timed-out waiter ahead of it) gave it its units.</summary>
        public bool Granted;
        /// <summary>Off the queue without the units: WAIT_CANCEL, WAIT_DELETE or WAIT_TIMEOUT.</summary>
        public int Error;
        /// <summary>Guest time at the wait and its limit in microseconds (-1: none).</summary>
        public long StartTicks;
        public long LimitUs = -1;
    }

    /// <summary>The wait queue, in the order a signal serves it.</summary>
    private readonly List<WaitRequest> _queue = new();

    /// <summary>Waiters given their units that have not returned yet (their thread still has to wake up).</summary>
    private readonly List<WaitRequest> _granted = new();

    /// <summary>Waiters of a loaded save state whose threads have not issued their wait again yet: the reissued wait
    /// takes its place (and its outcome, if a signal or cancel reached it meanwhile).</summary>
    private readonly List<WaitRequest> _restored = new();

    private static int CurrentPriority() => GuestThread.Current?.Priority ?? 0;

    /// <summary>A waiter is ahead of a caller of this priority: a wait or poll may not take units before it.</summary>
    private bool WaiterAhead(int priority)
    {
        if (_queue.Count == 0)
        {
            return false;
        }

        return (Attr & AttrThreadPriority) == 0 || _queue[0].Priority <= priority;
    }

    public int WaitSema(int need, Ptr<uint> timeout)
    {
        if (need <= 0 || need > MaxCount)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int result;
        bool woken;
        lock (this)
        {
            if (_deleted)
            {
                return SceError.WAIT_DELETE;
            }

            ReleaseExpired();
            GuestThread? self = GuestThread.Current;
            WaitRequest? request = TakeRestored(self);
            if (request != null && (request.Granted || request.Error != 0))
            {
                _granted.Remove(request);
                ObserveSignal();
                return request.Granted ? SceError.OK : request.Error;
            }

            var deadline = Wait.CallbackWaitDeadline.Start(timeout);
            while (true)
            {
                // threadmgr 3.74 (0x81011bc0): a CB wait runs the notified callbacks before it looks at the count
                if (request == null && Wait.CallbacksPending(self))
                {
                    Wait.RunCallbacks(this, self!);
                    if (_deleted)
                    {
                        return SceError.WAIT_DELETE;
                    }

                    ReleaseExpired();
                }
                if (request == null)
                {
                    int priority = self?.Priority ?? 0;
                    if (!WaiterAhead(priority) && Count >= need)
                    {
                        Count -= need;
                        ObserveSignal();
                        deadline.WriteLeft(timeout);
                        return SceError.OK;
                    }

                    if (!deadline.PrepareSleep(timeout))
                    {
                        return SceError.WAIT_TIMEOUT;
                    }

                    request = new WaitRequest { Need = need, Priority = priority, ThreadUid = self?.Uid ?? 0 };
                    Enqueue(request);
                    Waiters++;
                }

                request.Thread = self;
                request.StartTicks = GuestClock.Ticks;
                request.LimitUs = timeout.IsNull ? -1 : timeout.Value;
                try
                {
                    result = Wait.UntilGranted(this, request, static r => r.Granted || Wait.CallbacksPending(r.Thread), timeout,
                        static r => r.Granted ? 0 : r.Error, WaitDescription, out woken);
                }
                finally
                {
                    if (request.Granted)
                    {
                        _granted.Remove(request);
                    }
                    else if (request.Error == 0 && _queue.Remove(request))
                    {
                        // its own timeout, a callback notification that came before the thread was in the queue, or
                        // the thread's end: the units go on to the waiters behind it
                        Waiters--;
                        if (Deliver())
                        {
                            Monitor.PulseAll(this);
                        }
                    }
                }
                if (request.Granted)
                {
                    result = SceError.OK; // a timeout or cancel that came after the units does not undo them
                    break;
                }

                if (request.Error == SceError.NOTIFY_CALLBACK || (request.Error == 0 && result == SceError.OK))
                {
                    // off the queue for its callbacks: run them, look at the count again, queue anew behind the others
                    deadline.Interrupted = true;
                    request = null;
                    continue;
                }
                if (request.Error == SceError.WAIT_TIMEOUT)
                {
                    result = SceError.WAIT_TIMEOUT;
                    timeout.TrySet(0);
                }

                break;
            }
        }
        if (woken && result == SceError.OK)
        {
            Wait.WakeLatency();
        }

        return result;
    }

    private void Enqueue(WaitRequest request)
    {
        if ((Attr & AttrThreadPriority) == 0)
        {
            _queue.Add(request);
            return;
        }

        int index = 0;
        while (index < _queue.Count && _queue[index].Priority <= request.Priority)
        {
            index++;
        }

        _queue.Insert(index, request);
    }

    /// <summary>A callback notification reached a CB waiter (threadmgr 3.74's notify ends its sleep with
    /// NOTIFY_CALLBACK): it leaves the queue as a timed-out waiter does (the class's wait-release handler hands the count
    /// on to the waiters behind it), runs its callbacks and waits again.</summary>
    public void InterruptForCallbacks(GuestThread thread)
    {
        for (int i = 0; i < _queue.Count; i++)
        {
            WaitRequest request = _queue[i];
            if (request.Thread != thread)
            {
                continue;
            }

            request.Error = SceError.NOTIFY_CALLBACK;
            _queue.RemoveAt(i);
            Waiters--;
            Deliver();
            return;
        }
    }

    /// <summary>Hands the count to the waiters in queue order until the first one it does not cover. True when it
    /// reached one.</summary>
    private bool Deliver()
    {
        bool granted = false;
        while (_queue.Count > 0 && _queue[0].Need <= Count)
        {
            WaitRequest request = _queue[0];
            Count -= request.Need;
            request.Granted = true;
            _queue.RemoveAt(0);
            _granted.Add(request);
            Waiters--;
            granted = true;
        }

        return granted;
    }

    /// <summary>
    /// The kernel's timers at the waiters' guest-time deadlines: a waiter whose timeout has passed is off the queue
    /// (WAIT_TIMEOUT), however late its host thread is to notice, and each one leaving hands the count on to the
    /// waiters behind it, in the order the deadlines came. Called before every operation that looks at the queue.
    /// </summary>
    private void ReleaseExpired()
    {
        bool changed = false;
        while (true)
        {
            int earliest = -1;
            for (int i = 0; i < _queue.Count; i++)
            {
                WaitRequest request = _queue[i];
                if (request.LimitUs < 0 || !Wait.Expired(request.StartTicks, request.LimitUs))
                {
                    continue;
                }

                if (earliest < 0 || Deadline(request) < Deadline(_queue[earliest]))
                {
                    earliest = i;
                }
            }
            if (earliest < 0)
            {
                break;
            }

            _queue[earliest].Error = SceError.WAIT_TIMEOUT;
            _queue.RemoveAt(earliest);
            Waiters--;
            Deliver();
            changed = true;
        }
        if (changed)
        {
            Monitor.PulseAll(this); // the timed-out and the served waiters: nobody was woken by this thread
        }
    }

    /// <summary>A waiter's deadline, for the order the timers fire in (the limit in guest microseconds).</summary>
    private static double Deadline(WaitRequest request) => request.StartTicks + (double)request.LimitUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000 / TimeScale.Wait;

    /// <summary>The place a loaded save state kept for the calling thread's wait, if any.</summary>
    private WaitRequest? TakeRestored(GuestThread? self)
    {
        if (_restored.Count == 0 || self == null)
        {
            return null;
        }

        for (int i = 0; i < _restored.Count; i++)
        {
            if (_restored[i].ThreadUid == self.Uid)
            {
                WaitRequest request = _restored[i];
                _restored.RemoveAt(i);
                return request;
            }
        }

        return null;
    }

    /// <summary>sceKernelPollSema: takes the units only when they are there and no waiter is ahead of the caller,
    /// else SEMA_ZERO.</summary>
    public int Poll(int need)
    {
        if (need <= 0 || need > MaxCount)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            ReleaseExpired();
            if (WaiterAhead(CurrentPriority()) || Count < need)
            {
                return SceError.SEMA_ZERO;
            }

            Count -= need;
            ObserveSignal();
            return SceError.OK;
        }
    }

    /// <summary>sceKernelSignalSema: adds the units (SEMA_OVF past the maximum, nothing changed) and hands them to
    /// the queue.</summary>
    public int Signal(int n)
    {
        lock (this)
        {
            ReleaseExpired();
            if (Count + n > MaxCount)
            {
                return SceError.SEMA_OVF;
            }

            Count += n;
            RecordSignal();
            if (Deliver())
            {
                WakeWaiters();
            }

            return SceError.OK;
        }
    }

    /// <summary>The count and waiters sceKernelGetSemaInfo reports (timed-out waiters already off the queue).</summary>
    internal (int Count, int Waiters) Info()
    {
        lock (this)
        {
            ReleaseExpired();
            return (Count, Waiters);
        }
    }

    /// <summary>sceKernelCancelSema: every waiter returns WAIT_CANCEL (numWait: how many), and the count becomes
    /// <paramref name="setCount"/> (negative: the initial count) without serving anyone. A count above the maximum is
    /// INVALID_ARGUMENT.</summary>
    public int Cancel(int setCount, Ptr<int> numWait)
    {
        if (setCount > MaxCount)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            ReleaseExpired();
            numWait.TrySet(Waiters);
            DequeueAll(SceError.WAIT_CANCEL);
            Count = setCount < 0 ? InitCount : setCount;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            DequeueAll(SceError.WAIT_DELETE);
            WakeWaiters();
        }
    }

    private void DequeueAll(int error)
    {
        foreach (WaitRequest request in _queue)
        {
            request.Error = error;
        }

        _queue.Clear();
        Waiters = 0;
    }
}

public sealed partial class EventFlag : KernelObject, ICallbackInterruptedWait
{
    public override int UidClass => 3;
    public const uint WaitAnd = 0, WaitOr = 1, WaitClear = 2, WaitClearPat = 4;
    public const uint AttrMulti = 0x1000;
    /// <summary>SCE_KERNEL_ATTR_TH_PRIO: waiters queue by thread priority instead of first come first served.</summary>
    public const uint AttrThreadPriority = 0x2000;
    public uint Attr;
    public uint InitPattern, Pattern;
    /// <summary>Threads waiting and not satisfied yet (SceKernelEventFlagInfo.numWaitThreads).</summary>
    public int Waiters;
    private bool _deleted;
    private int _cancelled;

    /// <summary>One thread in sceKernelWaitEventFlag.</summary>
    private sealed class WaitRequest
    {
        public uint Bits;
        public uint Mode;
        public int Priority;
        public int CancelGeneration;
        /// <summary>A set satisfied it: the pattern it returns and the bits its clear mode took (save states).</summary>
        public bool Granted;
        public uint Result;
        public uint Consumed;
        /// <summary>A cancel or delete took it off the queue (it returns that error).</summary>
        public bool Dequeued;
        /// <summary>Guest time at the wait and its limit in microseconds (-1: none).</summary>
        public long StartTicks;
        public long LimitUs = -1;
        /// <summary>Its timeout had passed when a set reached it: off the queue, it returns WAIT_TIMEOUT.</summary>
        public bool Expired;
        /// <summary>The waiting thread (null: a host caller).</summary>
        public GuestThread? Thread;
        /// <summary>A callback notification took it off the queue: its thread runs its callbacks and waits anew.</summary>
        public bool Interrupted;

        /// <summary>Its thread is in a CB wait with callbacks to run (a notification that came before the thread was
        /// in the queue, so <see cref="InterruptForCallbacks"/> did not find it).</summary>
        public bool CallbacksPending => Thread != null && Thread.CanRunCallbacks && GuestThread.CallbackProcessor != null
            && Volatile.Read(ref Thread.PendingCallbacks) > 0;
    }

    /// <summary>The wait queue, in the order the kernel checks it on a set.</summary>
    private readonly List<WaitRequest> _queue = new();

    /// <summary>Satisfied waiters that have not returned yet (their thread still has to wake up).</summary>
    private readonly List<WaitRequest> _granted = new();

    private bool Matches(uint bits, uint mode) => (mode & WaitOr) != 0 ? (Pattern & bits) != 0 : (Pattern & bits) == bits;

    /// <summary>A wait mode threadmgr accepts (FW 3.74 wait and poll): only the OR, CLEAR and CLEAR_PAT bits, and not
    /// both clears at once; any other mode is ILLEGAL_MODE.</summary>
    public static bool IsLegalMode(uint mode) => (mode & ~(WaitOr | WaitClear | WaitClearPat)) == 0
        && (mode & (WaitClear | WaitClearPat)) != (WaitClear | WaitClearPat);

    /// <summary>The checks threadmgr's wait makes before it looks the flag up: ILLEGAL_MODE, then INVALID_ARGUMENT for
    /// no bits.</summary>
    public static int CheckWaitArguments(uint bits, uint mode)
    {
        if (!IsLegalMode(mode))
        {
            return SceError.ILLEGAL_MODE;
        }

        return bits == 0 ? SceError.INVALID_ARGUMENT : SceError.OK;
    }

    /// <summary>The checks threadmgr's poll makes before it looks the flag up: INVALID_ARGUMENT for no bits, then
    /// ILLEGAL_MODE (the other order from the wait's).</summary>
    public static int CheckPollArguments(uint bits, uint mode)
    {
        if (bits == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return IsLegalMode(mode) ? SceError.OK : SceError.ILLEGAL_MODE;
    }

    /// <summary>
    /// sceKernelWaitEventFlag(CB), as threadmgr 3.74 does it (0x8100f0e4). On a flag without SCE_EVENT_WAITMULTIPLE
    /// the first thread that waits owns the flag's wait until its wait ends: another thread's wait or poll meanwhile is
    /// EVF_MULTI, the owner's own (from one of its callbacks) is not. A CB wait runs the thread's notified callbacks
    /// first (writing the pattern of that moment to outBits), then checks the pattern; a notification that reaches it
    /// asleep takes it off the queue (<see cref="InterruptForCallbacks"/>), and once the callbacks ran it checks the
    /// pattern again and, if it still has to wait, joins the queue anew behind the waiters there, with the time left of
    /// its timeout. A set while its callbacks run does not see it.
    /// </summary>
    public int WaitFlag(uint bits, uint mode, Ptr<uint> outBits, Ptr<uint> timeout)
    {
        int invalid = CheckWaitArguments(bits, mode);
        if (invalid != SceError.OK)
        {
            return invalid;
        }

        lock (this)
        {
            GuestThread? self = GuestThread.Current;
            object waiter = (object?)self ?? s_hostWaiter;
            bool single = (Attr & AttrMulti) == 0;
            if (single && _waitOwner != null && _waitOwner != waiter)
            {
                return SceError.EVF_MULTI;
            }

            if (single)
            {
                _waitOwner = waiter;
                _waitOwnerDepth++;
            }
            try
            {
                return WaitLoop(self, bits, mode, outBits, timeout);
            }
            finally
            {
                if (single && --_waitOwnerDepth == 0)
                {
                    _waitOwner = null;
                }
            }
        }
    }

    /// <summary>The owner of a single-wait flag's wait (a guest thread, or this for a host caller), and how many of its
    /// waits are in progress (a callback of the owner may wait on it again).</summary>
    private object? _waitOwner;
    private int _waitOwnerDepth;
    private static readonly object s_hostWaiter = new();

    /// <summary>A caller other than the waiting owner of a single-wait flag: EVF_MULTI.</summary>
    private bool OwnedByAnother() => _waitOwner != null && _waitOwner != ((object?)GuestThread.Current ?? s_hostWaiter);

    private int WaitLoop(GuestThread? self, uint bits, uint mode, Ptr<uint> outBits, Ptr<uint> timeout)
    {
        bool callbacks = self != null && self.CanRunCallbacks && GuestThread.CallbackProcessor != null;
        long startTicks = GuestClock.Ticks;
        long limitUs = timeout.IsNull ? -1 : timeout.Value;
        bool interrupted = false;
        while (true)
        {
            if (callbacks && Volatile.Read(ref self!.PendingCallbacks) > 0)
            {
                outBits.TrySet(Pattern);
                Monitor.Exit(this);
                try
                {
                    GuestThread.CallbackProcessor!(self);
                }
                finally
                {
                    Monitor.Enter(this);
                }
            }
            if (interrupted && _deleted)
            {
                outBits.TrySet(Pattern);
                return SceError.WAIT_DELETE;
            }

            if (Matches(bits, mode))
            {
                outBits.TrySet(Pattern);
                Consume(bits, mode);
                ObserveSignal();
                if (interrupted)
                {
                    WriteTimeLeft(timeout, startTicks, limitUs);
                }

                return SceError.OK;
            }
            long leftUs = limitUs;
            if (interrupted && limitUs >= 0)
            {
                leftUs = Math.Max(0, Wait.RemainingUs(startTicks, limitUs));
                timeout.Value = (uint)leftUs; // the helper waits for what is left of the caller's timeout
                if (leftUs == 0)
                {
                    outBits.TrySet(Pattern);
                    return SceError.WAIT_TIMEOUT;
                }
            }

            WaitRequest request = new()
            {
                Bits = bits,
                Mode = mode,
                Thread = self,
                Priority = self?.Priority ?? 0,
                CancelGeneration = _cancelled,
                StartTicks = GuestClock.Ticks,
                LimitUs = leftUs,
            };
            Enqueue(request);
            Waiters++;
            int result;
            bool woken;
            try
            {
                result = Wait.UntilGranted(this, (flag: this, request), static t => t.request.Granted || t.request.Interrupted || t.request.CallbacksPending,
                    timeout,
                    static t => t.request.Granted || t.request.Interrupted ? 0 : t.request.Expired ? SceError.WAIT_TIMEOUT : t.flag._deleted ? SceError.WAIT_DELETE : t.flag._cancelled != t.request.CancelGeneration ? SceError.WAIT_CANCEL : 0,
                    WaitDescription, out woken);
            }
            finally
            {
                if (request.Granted)
                {
                    _granted.Remove(request);
                }
                else if (!request.Dequeued && _queue.Remove(request))
                {
                    Waiters--;
                }
            }
            if (request.Granted)
            {
                outBits.TrySet(request.Result); // satisfied by a set: a timeout or cancel that came after it does not undo that
                if (woken)
                {
                    Monitor.Exit(this);
                    try
                    {
                        Wait.WakeLatency();
                    }
                    finally
                    {
                        Monitor.Enter(this);
                    }
                }

                return SceError.OK;
            }
            if (request.Dequeued)
            {
                outBits.TrySet(request.Result); // the pattern when the cancel, delete or timeout woke it
                if (request.Expired)
                {
                    timeout.TrySet(0);
                }
                else
                {
                    // threadmgr wakes a cancelled or deleted waiter as a set does: its thread runs after the canceller's
                    // call returns, so it does not get ahead of a canceller that has not run on yet
                    ObserveSignal();
                }

                return result;
            }
            if (result != SceError.OK)
            {
                outBits.TrySet(Pattern);
                return result;
            }

            // a callback notification ended the wait (NOTIFY_CALLBACK): run them and look again
            interrupted = true;
        }
    }

    /// <summary>Writes what is left of the caller's timeout when the wait ends after a callback interruption.</summary>
    private static void WriteTimeLeft(Ptr<uint> timeout, long startTicks, long limitUs)
    {
        if (limitUs >= 0)
        {
            timeout.TrySet((uint)Math.Max(0, Wait.RemainingUs(startTicks, limitUs)));
        }
    }

    /// <summary>A callback notification reached a CB waiter of this flag: off the queue, it runs its callbacks and
    /// waits again (threadmgr's notify ends the wait with NOTIFY_CALLBACK).</summary>
    public void InterruptForCallbacks(GuestThread thread)
    {
        for (int i = 0; i < _queue.Count; i++)
        {
            WaitRequest request = _queue[i];
            if (request.Thread == thread)
            {
                request.Interrupted = true;
                _queue.RemoveAt(i);
                Waiters--;
                return;
            }
        }
    }

    private void Enqueue(WaitRequest request)
    {
        if ((Attr & AttrThreadPriority) == 0)
        {
            _queue.Add(request);
            return;
        }

        int index = 0;
        while (index < _queue.Count && _queue[index].Priority <= request.Priority)
        {
            index++;
        }

        _queue.Insert(index, request);
    }

    private void Consume(uint bits, uint mode)
    {
        if ((mode & WaitClear) != 0)
        {
            Pattern = 0;
        }
        else if ((mode & WaitClearPat) != 0)
        {
            Pattern &= ~bits;
        }
    }

    /// <summary>sceKernelPollEventFlag. On a flag without SCE_EVENT_WAITMULTIPLE that a thread waits on, another
    /// thread's poll is refused like a second wait (EVF_MULTI, FW 3.74 threadmgr), without touching the pattern.</summary>
    public int Poll(uint bits, uint mode, Ptr<uint> outBits)
    {
        int invalid = CheckPollArguments(bits, mode);
        if (invalid != SceError.OK)
        {
            return invalid;
        }

        lock (this)
        {
            if (OwnedByAnother())
            {
                return SceError.EVF_MULTI;
            }

            outBits.TrySet(Pattern);
            if (!Matches(bits, mode))
            {
                return SceError.EVF_COND;
            }

            Consume(bits, mode);
            ObserveSignal();
            return SceError.OK;
        }
    }

    /// <summary>
    /// sceKernelSetEventFlag, as the kernel does it (threadmgr): ORs the bits in, then walks the wait queue in order and
    /// satisfies every waiter whose condition now holds, applying its clear mode right there (so the next waiter in
    /// the queue, and any call after this one, sees the pattern without the consumed bits), until the pattern is
    /// empty. A satisfied waiter returns that result whenever its thread runs again; it never re-checks the flag. A
    /// waiter whose timeout has passed in guest time is no longer in the kernel's queue (its timer took it off at the
    /// deadline), however late its host thread is to notice: it is not satisfied and returns WAIT_TIMEOUT.
    /// </summary>
    public void Set(uint bits)
    {
        lock (this)
        {
            uint patternBefore = Pattern;
            Pattern |= bits;
            RecordSignal();
            bool satisfied = false;
            bool expired = false;
            for (int i = 0; i < _queue.Count && Pattern != 0;)
            {
                WaitRequest request = _queue[i];
                if (request.LimitUs >= 0 && Wait.Expired(request.StartTicks, request.LimitUs))
                {
                    request.Expired = true;
                    request.Dequeued = true;
                    request.Result = patternBefore; // its timer fired before this set
                    _queue.RemoveAt(i);
                    Waiters--;
                    expired = true;
                    continue;
                }

                if (!Matches(request.Bits, request.Mode))
                {
                    i++;
                    continue;
                }

                uint before = Pattern;
                request.Result = Pattern;
                Consume(request.Bits, request.Mode);
                request.Consumed = before & ~Pattern;
                request.Granted = true;
                _queue.RemoveAt(i);
                _granted.Add(request);
                Waiters--;
                satisfied = true;
            }
            if (satisfied)
            {
                WakeWaiters();
            }
            else if (expired)
            {
                Monitor.PulseAll(this); // only timed-out waiters: nobody was woken by this thread
            }
        }
    }

    public void Clear(uint bits)
    {
        lock (this)
        {
            Pattern &= bits;
        }
    }

    public int Cancel(uint setPattern, Ptr<int> numWait)
    {
        lock (this)
        {
            numWait.TrySet(Waiters);
            Pattern = setPattern;
            _cancelled++;
            DequeueAll();
            WakeWaiters();
        }
        return SceError.OK;
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            DequeueAll();
            WakeWaiters();
        }
    }

    /// <summary>Cancel and delete release every waiter at once: a set that follows cannot satisfy them any more. Each
    /// one returns the pattern of this moment (threadmgr's wake writes the waiter's outBits under the flag's lock), not
    /// the one it finds when its thread runs again.</summary>
    private void DequeueAll()
    {
        foreach (WaitRequest request in _queue)
        {
            request.Dequeued = true;
            request.Result = Pattern;
        }

        _queue.Clear();
        Waiters = 0;
    }
}

/// <summary>
/// Mutex used both for SceKernelMutex and lightweight mutexes. A lightweight mutex's state lives in its guest work area
/// (SceKernelLwMutexWork), the only copy: +0 the owner's thread UID (0 = free; bit 31 <see cref="Contended"/>: a thread
/// sleeps on it), +8 the lock count, +12 the attributes, +16 the mutex UID (0 once deleted). As in the real SceLibKernel
/// (and the futex protocol of Linux's NPTL), an uncontended lock and unlock are atomic operations on that memory by
/// the caller itself, guest code that never enters the kernel (Hle/HleGuestCode: lock = compare-and-swap 0 -> own
/// UID, unlock = swap back to 0 unless a sleeper set <see cref="Contended"/>); only a lock that must wait and an
/// unlock that must wake someone come here. Both kinds keep the same kernel wait queue: a lightweight mutex's owner
/// word stays flagged while it has waiters, and the unlock that brings the count to 0 writes the head of the queue into
/// it as the owner (threadmgr 3.74's lightweight unlock), so the word is never free for guest code to take in between.
/// A normal mutex is threadmgr's (FW 3.74): a wait queue, first come first served or, with
/// SCE_KERNEL_MUTEX_ATTR_TH_PRIO, by thread priority (arrival order among equal priorities). The unlock that frees it
/// makes the head of the queue the owner with the count it asked for, inside the call; a lock or try-lock takes a free
/// mutex only when no waiter is ahead of it (a strictly higher priority caller of a TH_PRIO mutex). A count below 1,
/// or above 1 without SCE_KERNEL_MUTEX_ATTR_RECURSIVE, is ILLEGAL_COUNT in every call; a waiter whose timeout passes
/// leaves the queue at its guest-time deadline; a cancel or a delete takes every waiter off it at once.
/// </summary>
public sealed partial class Mutex : KernelObject, ICallbackInterruptedWait
{
    public override int UidClass => IsLight ? 10 : 4;
    public const uint AttrRecursive = 0x02;
    /// <summary>SCE_KERNEL_MUTEX_ATTR_TH_PRIO: waiters queue by thread priority instead of first come first served.</summary>
    public const uint AttrThreadPriority = 0x2000;
    /// <summary>Owner word bit: at least one thread waits (or is about to) in <see cref="Lock"/>, so the owner's unlock
    /// must go through the kernel to wake it. Thread UIDs are positive, so the bit is free.</summary>
    public const uint Contended = 0x80000000;
    public const uint LwOwnerOffset = 0, LwCountOffset = 8, LwAttrOffset = 12, LwUidOffset = 16;
    public uint Attr;
    public int InitCount;
    private GuestThread? _owner;
    private int _lockCount;
    public int Waiters;
    public uint WorkArea; // lightweight mutex work area (0 for normal mutexes)
    /// <summary>The kernel's objects (lightweight mutexes: the owner word holds a UID, resolved through this).</summary>
    public ObjectTable? Threads;
    private bool _deleted;

    public bool IsLight => WorkArea != 0;

    /// <summary>The owner word for stores (its page is marked written).</summary>
    private ref int OwnerWord => ref GuestMemory.Ref<int>(WorkArea + LwOwnerOffset);
    /// <summary>The owner word, read without marking its page written.</summary>
    private unsafe int OwnerWordValue => Volatile.Read(ref *(int*)GuestMemory.Ptr(WorkArea + LwOwnerOffset));
    private uint OwnerUidWord => (uint)OwnerWordValue & ~Contended;

    /// <summary>The owning thread (a lightweight mutex: the UID in its work area).</summary>
    public GuestThread? Owner
    {
        get => IsLight ? (OwnerUidWord is var u and not 0 ? Threads?.Get<GuestThread>((int)u) : null) : _owner;
        set
        {
            if (IsLight)
            {
                Volatile.Write(ref OwnerWord, value?.Uid ?? 0);
            }
            else
            {
                _owner = value;
            }
        }
    }

    public int LockCount
    {
        get => IsLight ? (int)GuestMemory.R32(WorkArea + LwCountOffset) : _lockCount;
        set
        {
            if (IsLight)
            {
                GuestMemory.W32(WorkArea + LwCountOffset, (uint)value);
            }
            else
            {
                _lockCount = value;
            }
        }
    }

    public void InitOwner(GuestThread? t, int count)
    {
        lock (this)
        {
            if (count > 0)
            {
                Owner = t;
                LockCount = count;
            }
            else if (IsLight)
            {
                Owner = null;
                LockCount = 0;
            }
        }
    }

    private int FailedToOwn => IsLight ? SceError.LW_MUTEX_FAILED_TO_OWN : SceError.MUTEX_FAILED_TO_OWN;
    private int Recursive => IsLight ? SceError.LW_MUTEX_RECURSIVE : SceError.MUTEX_RECURSIVE;
    private int LockOverflow => IsLight ? SceError.LW_MUTEX_LOCK_OVF : SceError.MUTEX_LOCK_OVF;
    private int UnlockUnderflow => IsLight ? SceError.LW_MUTEX_UNLOCK_UDF : SceError.MUTEX_UNLOCK_UDF;
    private int NotOwned => IsLight ? SceError.LW_MUTEX_NOT_OWNED : SceError.MUTEX_NOT_OWNED;

    /// <summary>A lightweight mutex's owner word can be taken: free, or held by a thread that has ended.</summary>
    private bool LightFree(int w) => w == 0 || Threads?.Get<GuestThread>(w & (int)~Contended) is not { } o || o.Status == ThreadStatus.Dormant;

    /// <summary>The calling thread owns the mutex.</summary>
    private bool IsOwner(GuestThread self) => IsLight ? OwnerUidWord == (uint)self.Uid : _owner == self;

    /// <summary>No owner, or one that has ended.</summary>
    private bool Free => IsLight ? LightFree(OwnerWordValue) : OwnerFree;

    /// <summary>
    /// Makes <paramref name="threadUid"/> the owner of a free mutex with <paramref name="count"/> (under this object's
    /// lock). A lightweight mutex's owner word is compared and swapped from the free value seen, since a guest fast-path
    /// lock may take it meanwhile (false), and keeps <see cref="Contended"/> while the queue has waiters, so the owner's
    /// unlock comes to the kernel to hand it over.
    /// </summary>
    private bool TakeFree(GuestThread? thread, int threadUid, int count)
    {
        if (!IsLight)
        {
            if (!OwnerFree)
            {
                return false;
            }

            _owner = thread;
            _lockCount = count;
            return true;
        }

        while (true)
        {
            int w = OwnerWordValue;
            if (!LightFree(w))
            {
                return false;
            }

            int mine = threadUid | (_queue.Count > 0 ? unchecked((int)Contended) : 0);
            if (Interlocked.CompareExchange(ref OwnerWord, mine, w) == w)
            {
                LockCount = count;
                return true;
            }
        }
    }

    /// <summary>
    /// A lightweight mutex with a waiter just queued: flags the owner word <see cref="Contended"/> (the owner's guest
    /// fast-path unlock then comes to the kernel, which hands the mutex to the head of the queue). A word found free (its
    /// owner unlocked in guest code meanwhile, or ended) goes to the head of the queue at once. True when that handed
    /// it to a waiter.
    /// </summary>
    private bool FlagContended()
    {
        while (true)
        {
            int w = OwnerWordValue;
            if (LightFree(w))
            {
                if (HandOver())
                {
                    return true;
                }

                continue; // a guest fast-path lock took it meanwhile: flag the new owner
            }
            if ((w & unchecked((int)Contended)) != 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref OwnerWord, w | unchecked((int)Contended), w) == w)
            {
                return false;
            }
        }
    }

    /// <summary>One thread in sceKernelLockMutex or sceKernelLockLwMutex.</summary>
    private sealed class WaitRequest
    {
        public int Count;
        public int Priority;
        public int ThreadUid;
        /// <summary>The thread to make the owner (null for a restored waiter until it is resolved).</summary>
        public GuestThread? Thread;
        /// <summary>An unlock (or a cancel's or the owner's end) made it the owner.</summary>
        public bool Granted;
        /// <summary>Off the queue without the mutex: WAIT_CANCEL, WAIT_DELETE or WAIT_TIMEOUT.</summary>
        public int Error;
        /// <summary>Guest time at the wait and its limit in microseconds (-1: none).</summary>
        public long StartTicks;
        public long LimitUs = -1;
        /// <summary>A condition wait taking its mutex back: a save state reissues the cond wait, not this lock.</summary>
        public bool Reacquire;
    }

    /// <summary>The wait queue, in the order an unlock hands the mutex over.</summary>
    private readonly List<WaitRequest> _queue = new();

    /// <summary>Waiters made the owner that have not returned yet (their thread still has to wake up).</summary>
    private readonly List<WaitRequest> _granted = new();

    /// <summary>Waiters of a loaded save state whose threads have not issued their lock again yet.</summary>
    private readonly List<WaitRequest> _restored = new();

    /// <summary>threadmgr's count check (before the owner): a count below 1, or above 1 on a mutex without
    /// SCE_KERNEL_MUTEX_ATTR_RECURSIVE, is ILLEGAL_COUNT.</summary>
    private bool IllegalCount(int count) => count < 1 || (count > 1 && (Attr & AttrRecursive) == 0);

    /// <summary>A normal mutex with no owner, or one whose owner has ended.</summary>
    private bool OwnerFree => _owner == null || _owner.Status == ThreadStatus.Dormant;

    /// <summary>A waiter is ahead of a caller of this priority: the caller may not take a free mutex before it (any
    /// waiter of a FIFO mutex, one of equal or higher priority of a TH_PRIO one).</summary>
    private bool WaiterAhead(int priority)
    {
        if (_queue.Count == 0)
        {
            return false;
        }

        return (Attr & AttrThreadPriority) == 0 || _queue[0].Priority <= priority;
    }

    private void Enqueue(WaitRequest request)
    {
        if ((Attr & AttrThreadPriority) == 0)
        {
            _queue.Add(request);
            return;
        }

        int index = 0;
        while (index < _queue.Count && _queue[index].Priority <= request.Priority)
        {
            index++;
        }

        _queue.Insert(index, request);
    }

    /// <summary>A callback notification reached a CB waiter (threadmgr 3.74's notify ends its sleep with
    /// NOTIFY_CALLBACK): it leaves the queue as a timed-out waiter does, runs its callbacks and locks again.</summary>
    public void InterruptForCallbacks(GuestThread thread)
    {
        for (int i = 0; i < _queue.Count; i++)
        {
            WaitRequest request = _queue[i];
            if (request.Thread != thread)
            {
                continue;
            }

            request.Error = SceError.NOTIFY_CALLBACK;
            _queue.RemoveAt(i);
            Waiters--;
            HandOver();
            return;
        }
    }

    /// <summary>Hands a free mutex to the head of the queue with the count it asked for. True when it did.</summary>
    private bool HandOver()
    {
        if (_queue.Count == 0 || !Free)
        {
            return false;
        }

        WaitRequest request = _queue[0];
        _queue.RemoveAt(0);
        if (!TakeFree(request.Thread, request.ThreadUid, request.Count))
        {
            _queue.Insert(0, request); // a guest fast-path lock took the lightweight mutex first
            return false;
        }

        Waiters--;
        request.Granted = true;
        _granted.Add(request);
        return true;
    }

    /// <summary>
    /// The owner's unlock that brings the count to 0 (threadmgr 3.74, the normal and the lightweight unlock alike): the
    /// head of the queue becomes the owner with the count it asked for inside the call, before any thread runs, so the
    /// mutex is never free in between for a caller to take ahead of the queue (a lightweight mutex's owner word goes
    /// straight from the old owner to the new one, as threadmgr writes the work area); with nobody queued it is free.
    /// </summary>
    private void ReleaseByOwner()
    {
        RecordSignal();
        if (_queue.Count > 0)
        {
            WaitRequest request = _queue[0];
            _queue.RemoveAt(0);
            Waiters--;
            if (IsLight)
            {
                // the word is the owner's, flagged Contended since a waiter queued: no guest code changes it meanwhile
                Volatile.Write(ref OwnerWord, request.ThreadUid | (_queue.Count > 0 ? unchecked((int)Contended) : 0));
                LockCount = request.Count;
            }
            else
            {
                _owner = request.Thread;
                _lockCount = request.Count;
            }

            request.Granted = true;
            _granted.Add(request);
            WakeWaiters();
            return;
        }

        if (IsLight)
        {
            LockCount = 0;
            Interlocked.Exchange(ref OwnerWord, 0);
        }
        else
        {
            _lockCount = 0;
            _owner = null;
        }
    }

    /// <summary>Wait predicate: an owner that ended without unlocking leaves the mutex to the head of the queue (the
    /// woken head may be another waiter). True when <paramref name="request"/> got it.</summary>
    private bool HandOverFromEndedOwner(WaitRequest request)
    {
        if (HandOver())
        {
            Monitor.PulseAll(this);
        }

        return request.Granted;
    }

    /// <summary>
    /// The kernel's timers at the waiters' guest-time deadlines: a waiter whose timeout has passed is off the queue
    /// (WAIT_TIMEOUT), however late its host thread is to notice, so a later unlock does not make it the owner.
    /// </summary>
    private void ReleaseExpired()
    {
        bool changed = false;
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            WaitRequest request = _queue[i];
            if (request.LimitUs < 0 || !Wait.Expired(request.StartTicks, request.LimitUs))
            {
                continue;
            }

            request.Error = SceError.WAIT_TIMEOUT;
            _queue.RemoveAt(i);
            Waiters--;
            changed = true;
        }
        if (changed)
        {
            HandOver();
            Monitor.PulseAll(this);
        }
    }

    /// <summary>The place a loaded save state kept for the calling thread's lock, if any.</summary>
    private WaitRequest? TakeRestored(GuestThread self)
    {
        for (int i = 0; i < _restored.Count; i++)
        {
            if (_restored[i].ThreadUid == self.Uid)
            {
                WaitRequest request = _restored[i];
                _restored.RemoveAt(i);
                return request;
            }
        }

        return null;
    }

    /// <summary>The lock count and waiters sceKernelGetMutexInfo reports (timed-out waiters already off the queue).</summary>
    internal (int Count, int Waiters, GuestThread? Owner) Info()
    {
        lock (this)
        {
            ReleaseExpired();
            return (LockCount, Waiters, Owner);
        }
    }

    /// <summary>
    /// sceKernelTryLockMutex / sceKernelTryLockLwMutex. A normal mutex checks the count first (ILLEGAL_COUNT), then the
    /// owner. A lightweight one in SceLibKernel's order (FW 3.74, user mode): a count below 1 is ILLEGAL_COUNT, then a
    /// held mutex is the owner's recursion (LW_MUTEX_RECURSIVE without the attribute) or LW_MUTEX_FAILED_TO_OWN, and only
    /// a free one refuses a count above 1 without the recursive attribute. Neither takes it ahead of the queue.
    /// </summary>
    public int TryLock(GuestThread self, int count)
    {
        lock (this)
        {
            if (IsLight ? count < 1 : IllegalCount(count))
            {
                return SceError.ILLEGAL_COUNT;
            }

            ReleaseExpired();
            if (IsOwner(self))
            {
                return LockAgain(count);
            }

            if (HandOver())
            {
                WakeWaiters(); // the owner had ended: the head of the queue takes it first
            }

            if (!Free)
            {
                return FailedToOwn;
            }

            if (IllegalCount(count))
            {
                return SceError.ILLEGAL_COUNT;
            }

            if (WaiterAhead(self.Priority) || !TakeFree(self, self.Uid, count))
            {
                return FailedToOwn;
            }

            return SceError.OK;
        }
    }

    /// <summary>The owner locks again: a recursive mutex adds the count (LOCK_OVF past int.MaxValue), any other is
    /// RECURSIVE.</summary>
    private int LockAgain(int count)
    {
        if ((Attr & AttrRecursive) == 0)
        {
            return Recursive;
        }

        int held = LockCount;
        if (held + count < 0)
        {
            return LockOverflow;
        }

        LockCount = held + count;
        return SceError.OK;
    }

    public int Lock(GuestThread self, int count, Ptr<uint> timeout)
    {
        return LockQueued(self, count, timeout, reacquire: false);
    }

    /// <summary>A condition wait takes its mutex back with the count it had (the cond's thread is not reissued as a
    /// lock by a save state: see <see cref="WaitRequest.Reacquire"/>).</summary>
    internal int Reacquire(GuestThread self, int count) => LockQueued(self, count, default, reacquire: true);

    /// <summary>
    /// sceKernelLockMutex and the kernel side of sceKernelLockLwMutex (threadmgr 3.74: the count check, the owner's
    /// recursion, then the wait queue). A lightweight lock gets here when its guest fast path could not take the work
    /// area (held, a count other than 1, a deleted mutex).
    /// </summary>
    private int LockQueued(GuestThread self, int count, Ptr<uint> timeout, bool reacquire)
    {
        int result;
        bool woken;
        lock (this)
        {
            if (IllegalCount(count))
            {
                return SceError.ILLEGAL_COUNT;
            }

            if (_deleted)
            {
                return SceError.WAIT_DELETE;
            }

            ReleaseExpired();
            WaitRequest? request = TakeRestored(self);
            if (request != null && (request.Granted || request.Error != 0))
            {
                _granted.Remove(request);
                ObserveSignal();
                return request.Granted ? SceError.OK : request.Error;
            }

            if (request == null && IsOwner(self))
            {
                return LockAgain(count);
            }

            var deadline = Wait.CallbackWaitDeadline.Start(timeout);
            while (true)
            {
                // threadmgr 3.74 (0x8100d79c): a CB lock runs the notified callbacks before it looks at the mutex
                if (request == null && Wait.CallbacksPending(self))
                {
                    Wait.RunCallbacks(this, self);
                    if (_deleted)
                    {
                        return SceError.WAIT_DELETE;
                    }

                    ReleaseExpired();
                }
                if (request == null)
                {
                    if (!WaiterAhead(self.Priority) && TakeFree(self, self.Uid, count))
                    {
                        ObserveSignal();
                        deadline.WriteLeft(timeout);
                        return SceError.OK;
                    }

                    if (!deadline.PrepareSleep(timeout))
                    {
                        return SceError.WAIT_TIMEOUT;
                    }

                    request = new WaitRequest { Count = count, Priority = self.Priority, ThreadUid = self.Uid, Thread = self, Reacquire = reacquire };
                    Enqueue(request);
                    Waiters++;
                }

                request.Thread = self;
                if (IsLight && FlagContended())
                {
                    WakeWaiters(); // the owner had unlocked in guest code: the head of the queue has it
                }

                request.StartTicks = GuestClock.Ticks;
                request.LimitUs = timeout.IsNull ? -1 : timeout.Value;
                try
                {
                    // an owner that ends without unlocking leaves the mutex to the head of the queue: looked at on every
                    // wake-up slice, as before the queue
                    result = Wait.UntilGranted(this, (mutex: this, request),
                        static s => s.request.Granted || s.mutex.HandOverFromEndedOwner(s.request) || Wait.CallbacksPending(s.request.Thread),
                        timeout, static s => s.request.Granted ? 0 : s.request.Error, WaitDescription, out woken);
                }
                finally
                {
                    if (request.Granted)
                    {
                        _granted.Remove(request);
                    }
                    else if (request.Error == 0 && _queue.Remove(request))
                    {
                        Waiters--; // its own timeout, a notification that came before it was queued, or the thread's end
                        if (HandOver())
                        {
                            Monitor.PulseAll(this);
                        }
                    }
                }
                if (request.Granted)
                {
                    result = SceError.OK; // a timeout or cancel that came after the hand-over does not undo it
                    break;
                }

                if (request.Error == SceError.NOTIFY_CALLBACK || (request.Error == 0 && result == SceError.OK))
                {
                    // off the queue for its callbacks: run them, look at the mutex again, queue anew behind the others
                    deadline.Interrupted = true;
                    request = null;
                    continue;
                }
                if (request.Error == SceError.WAIT_TIMEOUT)
                {
                    result = SceError.WAIT_TIMEOUT;
                    timeout.TrySet(0);
                }

                break;
            }
        }
        if (woken && result == SceError.OK)
        {
            Wait.WakeLatency();
        }

        return result;
    }

    /// <summary>sceKernelUnlockMutex and the kernel side of sceKernelUnlockLwMutex (threadmgr 3.74): ILLEGAL_COUNT, then
    /// NOT_OWNED, then UNLOCK_UDF; the unlock that brings the count to 0 hands the mutex to the head of the queue.</summary>
    public int Unlock(GuestThread self, int count)
    {
        lock (this)
        {
            if (IllegalCount(count))
            {
                return SceError.ILLEGAL_COUNT;
            }

            ReleaseExpired();
            if (!IsOwner(self))
            {
                return NotOwned;
            }

            int held = LockCount;
            if (count > held)
            {
                return UnlockUnderflow;
            }

            if (held - count > 0)
            {
                LockCount = held - count;
            }
            else
            {
                ReleaseByOwner();
            }

            return SceError.OK;
        }
    }

    /// <summary>Fully releases the mutex (for condition waits) and returns the previous lock count.</summary>
    public int ReleaseAll(GuestThread self)
    {
        lock (this)
        {
            if (!IsOwner(self))
            {
                return -1;
            }

            int c = LockCount;
            ReleaseExpired();
            ReleaseByOwner();
            return c;
        }
    }

    /// <summary>sceKernelCancelMutex (threadmgr 3.74): a negative count is the initial count; a count above 1 needs
    /// SCE_KERNEL_MUTEX_ATTR_RECURSIVE (ILLEGAL_COUNT, nothing changed). Every waiter returns WAIT_CANCEL (numWait: how
    /// many) without the mutex, and the count goes to the caller (0: the mutex is free).</summary>
    public int Cancel(GuestThread? self, int newCount, Ptr<int> numWait)
    {
        lock (this)
        {
            if (newCount < 0)
            {
                newCount = InitCount;
            }

            if (newCount > 1 && (Attr & AttrRecursive) == 0)
            {
                return SceError.ILLEGAL_COUNT;
            }

            ReleaseExpired();
            numWait.TrySet(Waiters);
            DequeueAll(SceError.WAIT_CANCEL);
            LockCount = newCount;
            Owner = newCount > 0 ? self : null;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            DequeueAll(SceError.WAIT_DELETE);
            LockCount = 0;
            Owner = null;
            // the guest fast paths check the UID word: a deleted mutex's calls come to the kernel (UNKNOWN_LW_MUTEX_ID)
            if (IsLight)
            {
                GuestMemory.W32(WorkArea + LwUidOffset, 0);
            }

            WakeWaiters();
        }
    }

    private void DequeueAll(int error)
    {
        foreach (WaitRequest request in _queue)
        {
            request.Error = error;
        }

        Waiters -= _queue.Count;
        _queue.Clear();
    }
}

/// <summary>Condition variable bound to a mutex (SceKernelCond and lightweight conds), as threadmgr does it (FW 3.74):
/// a wait unlocks its mutex by 1 (MUTEX_NOT_OWNED / LW_MUTEX_NOT_OWNED when the caller does not own it) and queues,
/// first come first served or, with SCE_KERNEL_COND_ATTR_TH_PRIO, by thread priority; a signal releases the head of the
/// queue, SignalTo a given waiter, SignalAll every waiter, and the released thread locks the mutex again by 1 when it
/// runs. A waiter whose timeout passes is off the queue at its guest-time deadline (a later signal goes to the next
/// one) and locks the mutex again; a deleted cond's waiters return WAIT_DELETE_COND (WAIT_DELETE_LW_COND) without
/// locking it. A lightweight cond publishes the number of queued waiters in its work area (+0 its UID, +4 its mutex's,
/// +8 <see cref="LwQueuedOffset"/>), so a signal with nobody to wake, the common case, is guest code that never enters
/// the kernel (Hle/HleGuestCode). A waiter counts itself there before its mutex is released: a signaller holding the
/// mutex always sees it. Games may copy a work area (MotorStorm RC waits through a copy on a thread's stack and signals
/// through another one); only the work area the cond was created in carries the count, so it also holds its own address
/// (+12 <see cref="LwSelfOffset"/>) and the guest fast path takes a copy (a different address there) to the kernel.</summary>
public sealed partial class CondVar : KernelObject, ICallbackInterruptedWait
{
    public override int UidClass => WorkArea != 0 ? 11 : 5;
    /// <summary>SCE_KERNEL_COND_ATTR_TH_PRIO: waiters queue by thread priority instead of first come first served.</summary>
    public const uint AttrThreadPriority = 0x2000;
    public uint Attr;
    public Mutex Mutex = null!;
    public uint WorkArea;
    private bool _deleted;
    public const uint LwQueuedOffset = 8, LwSelfOffset = 12;

    /// <summary>Threads in the wait queue (numWaitThreads; timed-out waiters already off it).</summary>
    public int Waiters
    {
        get
        {
            lock (this)
            {
                ReleaseExpired();
                return _queue.Count;
            }
        }
    }

    private void PublishQueued(int n)
    {
        if (WorkArea != 0)
        {
            GuestMemory.W32(WorkArea + LwQueuedOffset, (uint)n);
        }
    }

    private sealed class Waiter
    {
        public GuestThread Thread = null!;
        public int Priority;
        public bool Released;
        /// <summary>Off the queue without a signal: WAIT_TIMEOUT (its deadline passed) or the delete's error.</summary>
        public int Error;
        /// <summary>Guest time at the wait and its limit in microseconds (-1: none).</summary>
        public long StartTicks;
        public long LimitUs = -1;
    }

    private readonly LinkedList<Waiter> _queue = new();
    /// <summary>Queue nodes of finished waits, reused by later waits (guarded by this object's lock).</summary>
    private readonly Stack<LinkedListNode<Waiter>> _freeNodes = new();

    /// <summary>What a deleted cond's waiters return.</summary>
    private int DeletedError => WorkArea != 0 ? SceError.WAIT_DELETE_LW_COND : SceError.WAIT_DELETE_COND;

    /// <summary>Queues <paramref name="node"/>: at the end, or with SCE_KERNEL_COND_ATTR_TH_PRIO after the waiters of
    /// equal or higher priority.</summary>
    private void Enqueue(LinkedListNode<Waiter> node)
    {
        if ((Attr & AttrThreadPriority) != 0)
        {
            for (LinkedListNode<Waiter>? n = _queue.First; n != null; n = n.Next)
            {
                if (n.Value.Priority > node.Value.Priority)
                {
                    _queue.AddBefore(n, node);
                    return;
                }
            }
        }

        _queue.AddLast(node);
    }

    /// <summary>The kernel's timers at the waiters' guest-time deadlines: a waiter whose timeout has passed is off the
    /// queue (WAIT_TIMEOUT), however late its host thread is to notice, so a later signal goes to the next one.</summary>
    private void ReleaseExpired()
    {
        bool changed = false;
        LinkedListNode<Waiter>? n = _queue.First;
        while (n != null)
        {
            LinkedListNode<Waiter>? next = n.Next;
            Waiter w = n.Value;
            if (w.LimitUs >= 0 && Wait.Expired(w.StartTicks, w.LimitUs))
            {
                w.Error = SceError.WAIT_TIMEOUT;
                _queue.Remove(n);
                changed = true;
            }

            n = next;
        }
        if (changed)
        {
            PublishQueued(_queue.Count);
            Monitor.PulseAll(this);
        }
    }

    public int WaitCond(GuestThread self, Ptr<uint> timeout)
    {
        int r;
        lock (this)
        {
            PublishQueued(_queue.Count + 1); // this waiter, before its mutex goes
            // a wait reissued by a save state load unlocked its mutex before the state was saved
            int unlocked = self._condReissueCount > 0 ? SceError.OK : Mutex.Unlock(self, 1);
            self._condReissueCount = 0;
            if (unlocked != SceError.OK)
            {
                PublishQueued(_queue.Count);
                return unlocked;
            }
            self._condWaitCount = 1; // save states: the reissued wait has unlocked its mutex by this much
            LinkedListNode<Waiter> node = _freeNodes.Count > 0 ? _freeNodes.Pop() : new LinkedListNode<Waiter>(new Waiter());
            Waiter w = node.Value;
            var deadline = Wait.CallbackWaitDeadline.Start(timeout);
            try
            {
                while (true)
                {
                    // threadmgr 3.74 (0x8101fab4): a CB wait runs the notified callbacks, its mutex already unlocked,
                    // before it joins the queue; a notification takes it off the queue and it joins again at the end
                    if (Wait.CallbacksPending(self))
                    {
                        PublishQueued(_queue.Count);
                        Wait.RunCallbacks(this, self);
                        if (_deleted)
                        {
                            return DeletedError;
                        }

                        ReleaseExpired();
                    }
                    if (!deadline.PrepareSleep(timeout))
                    {
                        r = SceError.WAIT_TIMEOUT; // the timer ran out during the callbacks: the mutex is locked again
                        break;
                    }

                    w.Thread = self;
                    w.Priority = self.Priority;
                    w.Released = false;
                    w.Error = 0;
                    w.StartTicks = GuestClock.Ticks;
                    w.LimitUs = timeout.IsNull ? -1 : timeout.Value;
                    Enqueue(node);
                    PublishQueued(_queue.Count);
                    try
                    {
                        r = Wait.Until(this, w, static t => t.Released || Wait.CallbacksPending(t.Thread), timeout,
                            static t => t.Released ? 0 : t.Error, WaitDescription);
                    }
                    finally
                    {
                        if (!w.Released && node.List != null)
                        {
                            _queue.Remove(node);
                            PublishQueued(_queue.Count);
                        }
                    }
                    if (w.Released)
                    {
                        r = SceError.OK; // a timeout that came after the signal does not undo it
                        break;
                    }

                    if (w.Error == SceError.NOTIFY_CALLBACK || (w.Error == 0 && r == SceError.OK))
                    {
                        deadline.Interrupted = true; // off the queue for its callbacks
                        continue;
                    }
                    if (r == SceError.WAIT_TIMEOUT)
                    {
                        timeout.TrySet(0);
                        break;
                    }

                    return r; // deleted: the mutex is not locked again
                }
            }
            finally
            {
                w.Thread = null!;
                if (_freeNodes.Count < 64)
                {
                    _freeNodes.Push(node);
                }
            }

            // lock the mutex again outside our lock to avoid lock-order inversion with the mutex
            Monitor.Exit(this);
            int relocked;
            try
            {
                relocked = Mutex.Reacquire(self, 1);
            }
            finally
            {
                Monitor.Enter(this);
            }
            if (relocked == SceError.WAIT_DELETE)
            {
                return WorkArea != 0 ? SceError.WAIT_DELETE_LW_MUTEX : SceError.WAIT_DELETE_MUTEX;
            }

            if (relocked == SceError.WAIT_CANCEL)
            {
                return SceError.WAIT_CANCEL_MUTEX;
            }

            return relocked < 0 ? relocked : r;
        }
    }

    /// <summary>A callback notification reached a CB waiter (threadmgr 3.74's notify ends its sleep with
    /// NOTIFY_CALLBACK): it leaves the queue, so a signal meanwhile goes to the next waiter, and joins it again at the
    /// end once its callbacks ran (its mutex stays unlocked meanwhile).</summary>
    public void InterruptForCallbacks(GuestThread thread)
    {
        for (LinkedListNode<Waiter>? n = _queue.First; n != null; n = n.Next)
        {
            if (n.Value.Thread != thread)
            {
                continue;
            }

            n.Value.Error = SceError.NOTIFY_CALLBACK;
            _queue.Remove(n);
            PublishQueued(_queue.Count);
            return;
        }
    }

    private void Release(LinkedListNode<Waiter> node)
    {
        node.Value.Released = true;
        _queue.Remove(node);
        PublishQueued(_queue.Count);
    }

    public void Signal()
    {
        lock (this)
        {
            ReleaseExpired();
            if (_queue.First is { } first)
            {
                Release(first);
            }

            WakeWaiters();
        }
    }

    /// <summary>Releases <paramref name="target"/> if it waits on this condition; false if it does not.</summary>
    public bool SignalTo(GuestThread target)
    {
        lock (this)
        {
            ReleaseExpired();
            for (LinkedListNode<Waiter>? n = _queue.First; n != null; n = n.Next)
            {
                if (n.Value.Thread != target)
                {
                    continue;
                }

                Release(n);
                WakeWaiters();
                return true;
            }
            return false;
        }
    }

    public void SignalAll()
    {
        lock (this)
        {
            ReleaseExpired();
            while (_queue.First is { } first)
            {
                Release(first);
            }

            WakeWaiters();
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            foreach (Waiter w in _queue)
            {
                w.Error = DeletedError;
            }

            _queue.Clear();
            PublishQueued(0);
            // the guest fast paths check the UID word: a deleted cond's signals come to the kernel (UNKNOWN_LW_COND_ID)
            if (WorkArea != 0)
            {
                GuestMemory.W32(WorkArea, 0);
            }

            WakeWaiters();
        }
    }
}

/// <summary>
/// sceKernel*MsgPipe, as threadmgr does it (FW 3.74: the blocking send 0x81013DFC and receive 0x8101456C, the Try forms
/// 0x810165E8 / 0x81016D30, cancel 0x8101738C and the class's wait handlers 0x81013964 / 0x81013C30, studied in the
/// decompiled module): a ring buffer of whole 4 KB pages and ONE wait queue, whose waiters are senders or receivers,
/// first come first served or, with <see cref="AttrSendPriority"/> (senders) / <see cref="AttrReceivePriority"/>
/// (receivers), by thread priority (arrival order among equal priorities).
/// <list type="bullet">
/// <item>A send hands its bytes straight to the waiting receivers, in queue order, inside the call: an ASAP receiver is
/// served by the first bytes it gets, a FULL one stays at the head until it has all it asked for. The rest goes into the
/// buffer: ASAP what fits, FULL all of it or nothing.</item>
/// <item>A receive takes the buffer's bytes, then the waiting senders' bytes in queue order, each sender putting its
/// rest into the buffer (ASAP: what fits; FULL: all or nothing) and being served when done, up to the first one that
/// stays.</item>
/// <item>A send or receive does not go ahead of a waiter of its own kind (FIFO: any; TH_PRIO: one of equal or higher
/// priority), and nothing goes ahead of a head that already moved part of its bytes.</item>
/// <item>ASAP returns once it moved something, FULL once it moved everything; the byte count is written after every
/// step, so a wait that ends in an error reports what it moved.</item>
/// <item>A waiter whose guest-time timeout passes leaves the queue (WAIT_TIMEOUT) and the senders behind it are tried
/// again; a cancel empties the buffer and releases every waiter (WAIT_CANCEL); a delete releases them (WAIT_DELETE).</item>
/// <item>DONT_WAIT on a blocking form is one of the kernel's "SceKernelMsgPipeAsync" requests: a waiter at the caller's
/// priority that no guest thread waits on (<see cref="TransferLater"/>).</item>
/// </list>
/// </summary>
public sealed partial class MsgPipe : EventObject, ICallbackInterruptedWait
{
    public override int UidClass => 7;
    /// <summary>SCE_KERNEL_MSG_PIPE_MODE_*: ASAP (0, move what fits / what is there) or FULL (1, the whole size),
    /// plus DONT_WAIT (0x10): on the blocking forms the kernel does the transfer later and the call returns at once
    /// (<see cref="TransferLater"/>). Not the PSP's order (there 0 was "all"): Jak and Daxter's renderer thread
    /// receives with mode 0 into a 0x600-byte buffer and waited forever for 12-byte messages.</summary>
    public const uint WaitAsap = 0, WaitAll = 1, DontWait = 0x10;

    /// <summary>SCE_KERNEL_MSG_PIPE_MODE_DONT_REMOVE: a receive that copies the bytes and leaves them in the pipe.</summary>
    public const uint DontRemove = 0x100;

    /// <summary>The mode bits a DONT_WAIT request may carry (any other: ILLEGAL_MODE).</summary>
    public const uint ValidModes = WaitAll | DontWait;

    /// <summary>The mode bits sceKernelTrySendMsgPipe and sceKernelTryReceiveMsgPipe accept (threadmgr 0x810165E8 /
    /// 0x81016D30; any other: ILLEGAL_MODE).</summary>
    public const uint TrySendModes = WaitAll, TryReceiveModes = WaitAll | DontRemove;

    /// <summary>SCE_KERNEL_MSG_PIPE_ATTR_TH_PRIO_S / _R: senders / receivers queue by thread priority.</summary>
    public const uint AttrSendPriority = 0x1, AttrReceivePriority = 0x2;

    public uint Attr;
    public int BufferSize;
    /// <summary>UID of the thread that created the pipe (sceKernelGetMsgPipeCreatorId).</summary>
    public int CreatorThreadUid;
    private bool _deleted;
    private int _cancelled;

    /// <summary>The ring buffer (allocated at the first byte), its read and write positions and the bytes it holds.</summary>
    private byte[]? _ring;
    private int _readPos, _writePos, _held;

    /// <summary>One send or receive in the wait queue (or one being tried).</summary>
    private sealed class Transfer
    {
        public bool Send;
        public Segments Buffers;
        public int Total;
        public int Left;
        public bool Full;
        public bool Peek;
        public int Priority;
        public int ThreadUid;
        /// <summary>The waiting thread (null: a host caller, a DONT_WAIT request, or a restored waiter until its thread
        /// reissues the call).</summary>
        public GuestThread? Thread;
        public Ptr<uint> Result;
        /// <summary>Done: the thread returns OK whenever it runs again.</summary>
        public bool Served;
        /// <summary>Off the queue without being done: WAIT_CANCEL, WAIT_DELETE or WAIT_TIMEOUT.</summary>
        public int Error;
        /// <summary>Guest time at the wait and its limit in microseconds (-1: none).</summary>
        public long StartTicks;
        public long LimitUs = -1;
        /// <summary>A DONT_WAIT request: run when it ends (no thread waits on it).</summary>
        public Action? Completed;

        public int Moved => Total - Left;

        /// <summary>What ends it: FULL all of its bytes, ASAP any.</summary>
        public bool Done => Full ? Left == 0 : Left < Total;

        /// <summary>It moved part of its bytes: as the head it keeps the first place.</summary>
        public bool KeepsPlace => Left != Total;

        /// <summary>Writes the byte count (a DONT_WAIT request's only when it ends: its kernel thread runs later).</summary>
        public void Report()
        {
            if (Completed == null)
            {
                Result.TrySet((uint)Moved);
            }
        }
    }

    /// <summary>The wait queue, in the order the pipe serves it.</summary>
    private readonly List<Transfer> _queue = new();

    /// <summary>Waiters off the queue whose threads have not returned yet.</summary>
    private readonly List<Transfer> _ended = new();

    /// <summary>Waiters of a loaded save state whose threads have not issued their call again yet: the reissued call
    /// takes its place (and its outcome, if the pipe served or released it meanwhile).</summary>
    private readonly List<Transfer> _restored = new();

    /// <summary>The head of the queue moved part of its bytes (threadmgr's +0x4C): no send or receive goes ahead of it.</summary>
    private bool _headPartial;

    /// <summary>Bytes the buffer can still take.</summary>
    public int FreeSize
    {
        get
        {
            lock (this)
            {
                ReleaseExpired();
                return BufferSize - _held;
            }
        }
    }

    /// <summary>Senders in the queue (SceKernelMppInfo.numSendWaitThreads; DONT_WAIT requests count: the kernel's
    /// async threads wait there).</summary>
    public int SendWaiters => CountWaiters(true);

    /// <summary>Receivers in the queue (SceKernelMppInfo.numReceiveWaitThreads).</summary>
    public int RecvWaiters => CountWaiters(false);

    private int CountWaiters(bool send)
    {
        lock (this)
        {
            ReleaseExpired();
            int count = 0;
            foreach (Transfer transfer in _queue)
            {
                if (transfer.Send == send)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Deleted while a handle opened by name was still open (<see cref="ObjectTable.Delete(MsgPipe)"/>).</summary>
    public bool IsDeleted
    {
        get
        {
            lock (this)
            {
                return _deleted;
            }
        }
    }

    /// <summary>Guest buffers taken as one contiguous byte range in order (MsgPipeVector entries {base, size}).</summary>
    public readonly struct Segments(uint[] addrs, int[] sizes)
    {
        public readonly uint[] Addrs = addrs;
        public readonly int[] Sizes = sizes;

        public int Total
        {
            get
            {
                long total = 0;
                foreach (int size in Sizes)
                {
                    total += (uint)size;
                }

                return (int)Math.Min(total, int.MaxValue);
            }
        }

        public static Segments One(uint addr, int size) => new([addr], [size]);

        /// <summary>The guest run at <paramref name="offset"/>: its address and length, at most <paramref name="max"/>
        /// (0 past the end).</summary>
        public int Run(int offset, int max, out uint address)
        {
            for (int i = 0; i < Addrs.Length; i++)
            {
                if (offset >= Sizes[i])
                {
                    offset -= Sizes[i];
                    continue;
                }

                address = Addrs[i] + (uint)offset;
                return Math.Min(Sizes[i] - offset, max);
            }

            address = 0;
            return 0;
        }

        /// <summary>threadmgr's check of the buffer list: an entry without a base is ILLEGAL_ADDR, an entry or a
        /// running total larger than the pipe's buffer ILLEGAL_SIZE.</summary>
        public int Check(int capacity)
        {
            long total = 0;
            for (int i = 0; i < Addrs.Length; i++)
            {
                if (Addrs[i] == 0)
                {
                    return SceError.ILLEGAL_ADDR;
                }

                total += (uint)Sizes[i];
                if ((uint)Sizes[i] > (uint)capacity || total > capacity)
                {
                    return SceError.ILLEGAL_SIZE;
                }
            }

            return SceError.OK;
        }
    }

    private static int CurrentPriority() => GuestThread.Current?.Priority ?? 0;

    // ---------------------------------------------------------------- byte moves

    /// <summary>Copies <paramref name="count"/> bytes from a sender's buffers to a receiver's, at their positions.</summary>
    private static void CopyBetween(Transfer from, Transfer to, int count)
    {
        int fromOffset = from.Moved;
        int toOffset = to.Moved;
        while (count > 0)
        {
            int length = from.Buffers.Run(fromOffset, count, out uint source);
            length = to.Buffers.Run(toOffset, length, out uint destination);
            if (length <= 0)
            {
                break;
            }

            GuestMemory.Span(source, length).CopyTo(GuestMemory.Span(destination, length));
            fromOffset += length;
            toOffset += length;
            count -= length;
        }
    }

    /// <summary>Puts <paramref name="count"/> of a sender's bytes, from its position, at the buffer's end.</summary>
    private void RingWrite(Transfer from, int count)
    {
        _ring ??= new byte[BufferSize];
        int offset = from.Moved;
        while (count > 0)
        {
            int length = from.Buffers.Run(offset, Math.Min(count, BufferSize - _writePos), out uint source);
            if (length <= 0)
            {
                break;
            }

            GuestMemory.Span(source, length).CopyTo(_ring.AsSpan(_writePos, length));
            _writePos = (_writePos + length) % BufferSize;
            _held += length;
            offset += length;
            count -= length;
        }
    }

    /// <summary>Takes <paramref name="count"/> bytes from the buffer's start into a receiver's buffers, at its position.</summary>
    private void RingRead(Transfer to, int count)
    {
        int offset = to.Moved;
        while (count > 0 && _ring != null)
        {
            int length = to.Buffers.Run(offset, Math.Min(count, BufferSize - _readPos), out uint destination);
            if (length <= 0)
            {
                break;
            }

            _ring.AsSpan(_readPos, length).CopyTo(GuestMemory.Span(destination, length));
            _readPos = (_readPos + length) % BufferSize;
            _held -= length;
            offset += length;
            count -= length;
        }
    }

    /// <summary>A receiver takes what the buffer holds, up to what it wants; a peek leaves the buffer as it was.</summary>
    private void TakeFromRing(Transfer receiver)
    {
        int readWas = _readPos;
        int heldWas = _held;
        int count = Math.Min(receiver.Left, _held);
        RingRead(receiver, count);
        receiver.Left -= count;
        if (receiver.Peek)
        {
            _readPos = readWas;
            _held = heldWas;
        }
    }

    /// <summary>A sender puts its rest into the buffer: ASAP what fits, FULL all of it or nothing.</summary>
    private void Fill(Transfer sender)
    {
        int count = Math.Min(sender.Left, BufferSize - _held);
        if (count <= 0 || (sender.Full && count < sender.Left))
        {
            return;
        }

        RingWrite(sender, count);
        sender.Left -= count;
    }

    // ---------------------------------------------------------------- the queue

    private bool ByPriority(bool send) => (Attr & (send ? AttrSendPriority : AttrReceivePriority)) != 0;

    private Transfer? First(bool send)
    {
        foreach (Transfer transfer in _queue)
        {
            if (transfer.Send == send)
            {
                return transfer;
            }
        }

        return null;
    }

    /// <summary>A send (or receive) of this priority may move bytes now: no waiter of its kind is ahead of it.</summary>
    private bool GoesFirst(bool send, int priority)
    {
        Transfer? head = First(send);
        if (head == null)
        {
            return true;
        }

        return ByPriority(send) && head.Priority > priority && !_headPartial;
    }

    /// <summary>Queues a waiter: at the end, or by priority with the side's TH_PRIO attribute (behind its equals); a
    /// head that keeps its place stays first.</summary>
    private void Enqueue(Transfer transfer)
    {
        if (!ByPriority(transfer.Send))
        {
            _queue.Add(transfer);
            return;
        }

        int index = 0;
        while (index < _queue.Count && _queue[index].Priority <= transfer.Priority)
        {
            index++;
        }

        if (index == 0 && _queue.Count > 0 && _headPartial)
        {
            index = 1;
        }

        _queue.Insert(index, transfer);
    }

    /// <summary>Takes a waiter off the queue, served (<paramref name="error"/> 0) or released with an error, and lets
    /// the next head take its turn (threadmgr's wait-end handler).</summary>
    private void End(Transfer transfer, int error)
    {
        _queue.Remove(transfer);
        if (error == 0)
        {
            transfer.Served = true;
        }
        else
        {
            transfer.Error = error;
        }

        transfer.Report();
        if (transfer.Completed != null)
        {
            transfer.Result.TrySet((uint)transfer.Moved);
            transfer.Completed();
        }
        else
        {
            _ended.Add(transfer);
        }

        HeadChanged();
    }

    /// <summary>A callback notification reached a CB waiter (threadmgr 3.74's notify ends its sleep with
    /// NOTIFY_CALLBACK): it leaves the queue with what it moved, as the class's wait-end handler lets the next head and
    /// the senders behind take their turn, runs its callbacks and tries again.</summary>
    public void InterruptForCallbacks(GuestThread thread)
    {
        foreach (Transfer transfer in _queue)
        {
            if (transfer.Thread == thread && transfer.Completed == null)
            {
                transfer.Error = SceError.NOTIFY_CALLBACK;
                LeaveForCallbacks(transfer);
                return;
            }
        }
    }

    /// <summary>Takes a waiter off the queue without ending its call (it runs its callbacks and tries again).</summary>
    private void LeaveForCallbacks(Transfer transfer)
    {
        _queue.Remove(transfer);
        HeadChanged();
        Serve();
    }

    /// <summary>The queue lost a waiter: a sender head keeps its place if it moved part of its bytes; a receiver head
    /// takes what the buffer holds (threadmgr 0x81013C30).</summary>
    private void HeadChanged()
    {
        _headPartial = false;
        if (_queue.Count == 0)
        {
            return;
        }

        Transfer head = _queue[0];
        if (head.Send)
        {
            _headPartial = head.KeepsPlace;
            return;
        }

        TakeFromRing(head);
        head.Report();
        _headPartial = head.Left != 0 && head.KeepsPlace;
    }

    /// <summary>A send moves its bytes: straight to the waiting receivers in queue order, then its rest into the
    /// buffer. True when it is done.</summary>
    private bool Give(Transfer sender)
    {
        Transfer? receiver;
        while (sender.Left > 0 && (receiver = First(false)) != null)
        {
            int count = Math.Min(receiver.Left, sender.Left);
            CopyBetween(sender, receiver, count);
            receiver.Left -= count;
            if (!receiver.Peek)
            {
                sender.Left -= count;
            }

            receiver.Report();
            if (receiver.Full && receiver.Left > 0)
            {
                _headPartial = receiver.KeepsPlace;
                break;
            }

            _headPartial = false;
            End(receiver, SceError.OK);
        }

        Fill(sender);
        sender.Report();
        return sender.Done;
    }

    /// <summary>A receive moves bytes: the buffer's, then the waiting senders' in queue order, each one putting its
    /// rest into the buffer, up to the first sender that stays. A peek takes them all as copies. True when it is done.</summary>
    private bool Take(Transfer receiver)
    {
        int writeWas = _writePos;
        int readWas = _readPos;
        int heldWas = _held;
        TakeFromRing(receiver);
        Transfer? sender;
        while ((sender = First(true)) != null)
        {
            int count = Math.Min(receiver.Left, sender.Left);
            CopyBetween(sender, receiver, count);
            receiver.Left -= count;
            if (receiver.Peek)
            {
                break; // a peek frees nothing
            }

            sender.Left -= count;
            Fill(sender);
            sender.Report();
            if (!sender.Done)
            {
                _headPartial = sender.KeepsPlace;
                break;
            }

            _headPartial = false;
            End(sender, SceError.OK);
        }

        if (receiver.Peek)
        {
            _writePos = writeWas;
            _readPos = readWas;
            _held = heldWas;
        }

        receiver.Report();
        return receiver.Done;
    }

    /// <summary>The waiting senders put what they can into the buffer, in queue order, up to the first one that stays
    /// (threadmgr 0x81013964, after a waiter left the queue).</summary>
    private void Serve()
    {
        Transfer? sender;
        while ((sender = First(true)) != null)
        {
            Fill(sender);
            sender.Report();
            if (!sender.Done)
            {
                return;
            }

            End(sender, SceError.OK);
        }
    }

    /// <summary>
    /// The kernel's timers at the waiters' guest-time deadlines: a waiter whose timeout has passed is off the queue
    /// (WAIT_TIMEOUT), however late its host thread is to notice, in the order the deadlines came, and the senders
    /// behind it are tried again. Called before every operation that looks at the queue.
    /// </summary>
    private void ReleaseExpired()
    {
        bool changed = false;
        while (true)
        {
            Transfer? earliest = null;
            foreach (Transfer transfer in _queue)
            {
                if (transfer.LimitUs < 0 || !Wait.Expired(transfer.StartTicks, transfer.LimitUs))
                {
                    continue;
                }

                if (earliest == null || Deadline(transfer) < Deadline(earliest))
                {
                    earliest = transfer;
                }
            }
            if (earliest == null)
            {
                break;
            }

            End(earliest, SceError.WAIT_TIMEOUT);
            Serve();
            changed = true;
        }
        if (changed)
        {
            Monitor.PulseAll(this);
        }
    }

    private static double Deadline(Transfer transfer) => transfer.StartTicks + (double)transfer.LimitUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000 / TimeScale.Wait;

    /// <summary>The place a loaded save state kept for the calling thread's call, if any.</summary>
    private Transfer? TakeRestored(GuestThread? self, bool send)
    {
        if (_restored.Count == 0 || self == null)
        {
            return null;
        }

        for (int i = 0; i < _restored.Count; i++)
        {
            if (_restored[i].ThreadUid == self.Uid && _restored[i].Send == send)
            {
                Transfer transfer = _restored[i];
                _restored.RemoveAt(i);
                return transfer;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- calls

    public int Send(uint src, int size, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
        => Send(Segments.One(src, size), mode, result, timeout, poll);

    public int Send(Segments src, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
        => Transmit(true, src, mode, result, timeout, poll);

    public int Receive(uint dst, int size, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
        => Receive(Segments.One(dst, size), mode, result, timeout, poll);

    public int Receive(Segments dst, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
        => Transmit(false, dst, mode, result, timeout, poll);

    /// <summary>
    /// A send or receive, blocking or Try (<paramref name="poll"/>). threadmgr's order: the Try forms' mode
    /// (ILLEGAL_MODE), the buffer list (ILLEGAL_ADDR / ILLEGAL_SIZE), the deleted pipe (MPP_DELETED), then nothing to
    /// move (OK, 0 bytes). A Try FULL send that the buffer and the waiting receivers cannot take whole, or a Try FULL
    /// receive that the buffer and the waiting senders cannot fill, fails without moving or writing anything; one that
    /// waiters of its kind are ahead of fails the same way.
    /// </summary>
    /// <summary>
    /// A send or receive, then the pipe's event set when it moved bytes (threadmgr 3.74, the end of 0x81013dfc /
    /// 0x8101456c through the event class's set 0x8101dcf8): a send raises IN (with the pipe's "can receive" bit while
    /// bytes wait in its buffer), a receive OUT, either one ERROR as well when it ended in an error after moving part of
    /// them; the callbacks registered to the pipe are notified with those bits as the notify argument.
    /// </summary>
    private int Transmit(bool send, Segments buffers, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
    {
        int code = TransmitCore(send, buffers, mode, result, timeout, poll, out Transfer? transfer);
        if (transfer == null || transfer.Moved == 0)
        {
            return code;
        }

        lock (this)
        {
            uint raised = send ? EventIn | (ReceivableBytes() > 0 ? EventReceivable : 0) : EventOut;
            if (code < 0)
            {
                raised |= EventBits.Error;
            }

            NotifyRegisteredCallbacks((int)raised);
        }

        return code;
    }

    private int TransmitCore(bool send, Segments buffers, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll, out Transfer? used)
    {
        used = null;
        if (poll && (mode & ~(send ? TrySendModes : TryReceiveModes)) != 0)
        {
            return SceError.ILLEGAL_MODE;
        }

        int result0;
        bool woken;
        lock (this)
        {
            int check = buffers.Check(BufferSize);
            if (check != SceError.OK)
            {
                return check;
            }

            if (_deleted)
            {
                return SceError.MPP_DELETED;
            }

            int size = buffers.Total;
            if (size == 0)
            {
                result.TrySet(0);
                return SceError.OK;
            }

            ReleaseExpired();
            GuestThread? self = GuestThread.Current;
            Transfer? transfer = poll ? null : TakeRestored(self, send);
            if (transfer != null && (transfer.Served || transfer.Error != 0))
            {
                _ended.Remove(transfer);
                transfer.Result = result;
                transfer.Report();
                ObserveSignal();
                return transfer.Served ? SceError.OK : transfer.Error;
            }

            int priority = self?.Priority ?? 0;
            bool full = (mode & WaitAll) != 0;
            bool queued = transfer != null; // a restored waiter is in the queue already
            if (transfer == null)
            {
                transfer = new Transfer
                {
                    Send = send,
                    Buffers = buffers,
                    Total = size,
                    Left = size,
                    Full = full,
                    Peek = !send && (mode & DontRemove) != 0,
                    Priority = priority,
                    ThreadUid = self?.Uid ?? 0,
                    Result = result,
                };
                used = transfer;
                if (poll)
                {
                    if (!GoesFirst(send, priority) || (full && size > (send ? SendRoom() : ReceivableBytes())))
                    {
                        return send ? SceError.MPP_FULL : SceError.MPP_EMPTY;
                    }

                    bool done = send ? Give(transfer) : Take(transfer);
                    WakeWaiters();
                    ObserveSignal();
                    return done ? SceError.OK : send ? SceError.MPP_FULL : SceError.MPP_EMPTY;
                }
            }
            else
            {
                transfer.Result = result;
                transfer.Buffers = buffers;
                used = transfer;
            }

            transfer.Thread = self;
            var deadline = Wait.CallbackWaitDeadline.Start(timeout);
            while (true)
            {
                if (!queued)
                {
                    // threadmgr 3.74 (0x81013dfc / 0x8101456c): a CB call runs the notified callbacks before it moves
                    // bytes; a notification takes it off the queue with what it moved, and it tries again from there
                    if (Wait.CallbacksPending(self))
                    {
                        Wait.RunCallbacks(this, self!);
                        if (_deleted)
                        {
                            transfer.Report();
                            return SceError.WAIT_DELETE;
                        }

                        ReleaseExpired();
                    }
                    if (GoesFirst(send, priority))
                    {
                        bool done = send ? Give(transfer) : Take(transfer);
                        if (done)
                        {
                            WakeWaiters();
                            deadline.WriteLeft(timeout);
                            return SceError.OK;
                        }

                        // it tried first: as the new head it keeps its place if it moved part of its bytes
                        _headPartial = transfer.KeepsPlace;
                    }
                    else
                    {
                        transfer.Report();
                    }

                    if (!deadline.PrepareSleep(timeout))
                    {
                        return SceError.WAIT_TIMEOUT;
                    }

                    Enqueue(transfer);
                    WakeWaiters();
                }

                queued = false;
                transfer.StartTicks = GuestClock.Ticks;
                transfer.LimitUs = timeout.IsNull ? -1 : timeout.Value;
                try
                {
                    result0 = Wait.UntilGranted(this, transfer, static t => t.Served || Wait.CallbacksPending(t.Thread), timeout,
                        static t => t.Served ? 0 : t.Error, WaitDescription, out woken);
                }
                finally
                {
                    if (transfer.Served || transfer.Error != 0)
                    {
                        _ended.Remove(transfer);
                    }
                    else if (_queue.Contains(transfer))
                    {
                        if (Wait.CallbacksPending(self))
                        {
                            LeaveForCallbacks(transfer); // a notification that came before it was queued
                        }
                        else
                        {
                            // its own timeout (or the thread's end): the turn goes on to the ones behind it
                            End(transfer, SceError.WAIT_TIMEOUT);
                            _ended.Remove(transfer);
                            Serve();
                        }
                        Monitor.PulseAll(this);
                    }
                }
                transfer.Report();
                if (transfer.Served)
                {
                    result0 = SceError.OK; // a timeout or cancel that came after the bytes does not undo them
                    break;
                }

                if (transfer.Error == SceError.NOTIFY_CALLBACK || (transfer.Error == 0 && result0 == SceError.OK))
                {
                    transfer.Error = 0;
                    deadline.Interrupted = true;
                    continue;
                }
                if (transfer.Error == SceError.WAIT_TIMEOUT)
                {
                    result0 = SceError.WAIT_TIMEOUT;
                    timeout.TrySet(0);
                }

                break;
            }
        }
        if (woken && result0 == SceError.OK)
        {
            Wait.WakeLatency();
        }

        return result0;
    }

    /// <summary>What a FULL Try send can hand over at once: the buffer's free bytes, plus what the waiting receivers
    /// want (threadmgr 0x810165E8).</summary>
    private int SendRoom()
    {
        long room = BufferSize - _held;
        foreach (Transfer transfer in _queue)
        {
            if (!transfer.Send)
            {
                room += transfer.Left;
            }
        }

        return (int)Math.Min(room, int.MaxValue);
    }

    /// <summary>What a FULL Try receive can get at once: the buffer's bytes, plus the waiting senders' rest.</summary>
    private int ReceivableBytes()
    {
        long bytes = _held;
        foreach (Transfer transfer in _queue)
        {
            if (transfer.Send)
            {
                bytes += transfer.Left;
            }
        }

        return (int)Math.Min(bytes, int.MaxValue);
    }

    /// <summary>sceKernelCancelMsgPipe: the buffer is emptied and every waiter returns WAIT_CANCEL with what it moved;
    /// numSend / numRecv get how many senders / receivers waited (threadmgr 0x8101738C).</summary>
    public int Cancel(Ptr<int> numSend, Ptr<int> numRecv)
    {
        lock (this)
        {
            if (_deleted)
            {
                return SceError.MPP_DELETED;
            }

            ReleaseExpired();
            int senders = 0;
            int receivers = 0;
            foreach (Transfer transfer in _queue)
            {
                if (transfer.Send)
                {
                    senders++;
                }
                else
                {
                    receivers++;
                }
            }

            _readPos = 0;
            _writePos = 0;
            _held = 0;
            ReleaseAll(SceError.WAIT_CANCEL);
            _cancelled++;
            numSend.TrySet(senders);
            numRecv.TrySet(receivers);
            WakeWaiters();
        }
        return SceError.OK;
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            ReleaseAll(SceError.WAIT_DELETE);
            WakeWaiters();
        }
    }

    private void ReleaseAll(int error)
    {
        while (_queue.Count > 0)
        {
            End(_queue[0], error);
        }

        _headPartial = false;
    }

    /// <summary>
    /// DONT_WAIT on a blocking send or receive (FW 3.74 threadmgr, the vector forms at 0x81015004 / 0x8101533C): the
    /// kernel copies the buffer list and queues the transfer for its "SceKernelMsgPipeAsync" threads, which run it as a
    /// blocking call at the caller's priority; the call returns at once with success. Here it is that call's waiter,
    /// with no guest thread behind it: tried at once, then queued. <paramref name="result"/> (null for none) gets the
    /// bytes moved; <paramref name="completed"/> runs when it ends, also when a cancel or delete ends it.
    /// </summary>
    public void TransferLater(bool send, Segments buffers, uint mode, Ptr<uint> result, Action completed)
    {
        lock (this)
        {
            int size = buffers.Total;
            if (_deleted || buffers.Check(BufferSize) != SceError.OK || size == 0)
            {
                result.TrySet(0);
                completed();
                return;
            }

            ReleaseExpired();
            int priority = CurrentPriority();
            var transfer = new Transfer
            {
                Send = send,
                Buffers = buffers,
                Total = size,
                Left = size,
                Full = (mode & WaitAll) != 0,
                Priority = priority,
                Result = result,
                Completed = completed,
            };
            if (GoesFirst(send, priority))
            {
                if (send ? Give(transfer) : Take(transfer))
                {
                    WakeWaiters();
                    result.TrySet((uint)transfer.Moved);
                    completed();
                    return;
                }

                _headPartial = transfer.KeepsPlace;
            }

            Enqueue(transfer);
            WakeWaiters();
        }
    }

    /// <summary>The DONT_WAIT transfers still in the queue.</summary>
    public int LaterCount
    {
        get
        {
            lock (this)
            {
                int count = 0;
                foreach (Transfer transfer in _queue)
                {
                    if (transfer.Completed != null)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    // ---- sceKernelWaitEvent / PollEvent on a message pipe (SCE_KERNEL_EVENT_*): IN while data can be received, OUT
    // while there is room to send, DELETE once deleted, and OPEN / CLOSE when a handle was opened or closed by name
    // (latched until a wait reports them). The Browser's web engine (SceWebKitProcess) creates its IPC pipe and waits
    // for OPEN | DELETE until the Browser opens it; the wait failed with UNKNOWN_EVENT_ID and its IPC thread ended.
    public const uint EventIn = 0x1, EventOut = 0x2, EventOpen = 0x100, EventClose = 0x200;
    /// <summary>The message pipe's own "can receive" / "can send" bits: the web engine's IPC loop waits for 0x10218
    /// (received data | CLOSE | ERROR | DELETE).</summary>
    public const uint EventReceivable = 0x10000, EventSendable = 0x20000;
    private uint _latched;

    private uint Pattern => (ReceivableBytes() > 0 ? EventIn | EventReceivable : 0)
        | (_held < BufferSize ? EventOut | EventSendable : 0)
        | (_deleted ? EventBits.Delete : 0) | _latched;

    /// <summary>A handle to the pipe was opened or closed (<see cref="EventOpen"/>, <see cref="EventClose"/>): the event
    /// class's set (threadmgr 3.74, 0x8101dcf8), which notifies the callbacks registered to the pipe with those bits.</summary>
    public void Signal(uint ev)
    {
        lock (this)
        {
            _latched |= ev;
            WakeWaiters();
            NotifyRegisteredCallbacks((int)ev);
        }
    }

    private int TakeEvent(uint waitPattern, Ptr<uint> result)
    {
        uint hit = Pattern & waitPattern;
        result.TrySet(hit);
        if (hit == 0)
        {
            return SceError.EVENT_COND;
        }

        _latched &= ~hit;
        return SceError.OK;
    }

    public int PollEvent(uint waitPattern, Ptr<uint> result)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            return TakeEvent(waitPattern, result);
        }
    }

    public int WaitEvent(uint waitPattern, Ptr<uint> result, Ptr<uint> timeout)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            int gen = _cancelled;
            int r = Wait.Until(this, (p: this, waitPattern, gen), static t => (t.p.Pattern & t.waitPattern) != 0, timeout,
                static t => t.p._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
            if (r != SceError.OK)
            {
                result.TrySet(r == SceError.WAIT_TIMEOUT ? 0 : EventBits.Error);
                return r;
            }
            return TakeEvent(waitPattern, result);
        }
    }
}

/// <summary>
/// sceKernel*RWLock, as threadmgr does it (FW 3.74: the lock 0x81024bb0 (read and write, plain and CB), the try-locks
/// 0x810254f0 / 0x81025758, the unlocks 0x810255b4 / 0x81025888, cancel 0x81025a50, info 0x81024ef4, and the class's
/// wait-release handler 0x810249b8 and destructor 0x81024aa4, studied in the decompiled module): one lock count (the
/// readers holding it, or the writer's recursion) and ONE wait queue of readers and writers, first come first served
/// or, with <see cref="AttrThreadPriority"/>, by thread priority (arrival order among equal priorities).
/// <list type="bullet">
/// <item>A read lock takes it at once only when no writer holds it and nobody waits (a waiting writer keeps new readers
/// out); a write lock only when nobody holds it. The writer's own read lock is RW_LOCK_FAILED_TO_LOCK; its own write
/// lock counts up with <see cref="AttrRecursive"/>, else RW_LOCK_RECURSIVE.</item>
/// <item>The unlock that frees it hands it over inside the call: to the first waiting writer in queue order (past the
/// readers queued ahead of it) when a writer waits, else to every waiting reader at once.</item>
/// <item>A waiter whose guest-time timeout passes, or that a callback notification takes off the queue, leaves it, and
/// the hand-over is tried again for the others (with no writer holding it: every reader when no writer waits, else the
/// first writer once nobody holds it).</item>
/// <item>Cancel frees the lock and releases every waiter with WAIT_CANCEL (how many readers and writers waited), the
/// caller then holding it for writing with flag 1; delete releases them with WAIT_DELETE.</item>
/// </list>
/// </summary>
public sealed partial class RwLock : KernelObject, ICallbackInterruptedWait
{
    public override int UidClass => 12;
    /// <summary>SCE_KERNEL_RW_LOCK_ATTR_RECURSIVE: the writer may lock it for writing again.</summary>
    public const uint AttrRecursive = 0x2;
    /// <summary>SCE_KERNEL_RW_LOCK_ATTR_TH_PRIO: waiters queue by thread priority instead of first come first served.</summary>
    public const uint AttrThreadPriority = 0x2000;
    public uint Attr;
    /// <summary>The lock count: the readers holding it, or the writer's recursion (SceKernelRWLockInfo.lockCount).</summary>
    public int LockCount;
    public GuestThread? Writer;
    /// <summary>Readers and writers in the wait queue (SceKernelRWLockInfo.numReadWaitThreads / numWriteWaitThreads).</summary>
    public int ReadWaiters, WriteWaiters;
    private bool _deleted;

    /// <summary>One thread in sceKernelLockReadRWLock or sceKernelLockWriteRWLock.</summary>
    private sealed class WaitRequest
    {
        public bool Write;
        public int Priority;
        public int ThreadUid;
        /// <summary>The waiting thread (null: a host caller, or a restored waiter until it is resolved).</summary>
        public GuestThread? Thread;
        /// <summary>An unlock (or a waiter leaving ahead of it) gave it the lock.</summary>
        public bool Granted;
        /// <summary>Off the queue without the lock: WAIT_CANCEL, WAIT_DELETE, WAIT_TIMEOUT or NOTIFY_CALLBACK.</summary>
        public int Error;
        /// <summary>Guest time at the wait and its limit in microseconds (-1: none).</summary>
        public long StartTicks;
        public long LimitUs = -1;
    }

    /// <summary>The wait queue, in the order the hand-over looks at it.</summary>
    private readonly List<WaitRequest> _queue = new();

    /// <summary>Waiters given the lock that have not returned yet (their thread still has to wake up).</summary>
    private readonly List<WaitRequest> _granted = new();

    /// <summary>Waiters of a loaded save state whose threads have not issued their lock again yet.</summary>
    private readonly List<WaitRequest> _restored = new();

    public int LockRead(GuestThread self, Ptr<uint> timeout) => Lock(self, write: false, timeout);

    public int LockWrite(GuestThread self, Ptr<uint> timeout) => Lock(self, write: true, timeout);

    /// <summary>A free lock for this kind of caller: no writer and, for a reader, nobody queued (a waiting writer goes
    /// first); for a writer, no readers either.</summary>
    private bool Available(bool write) => Writer == null && (write ? LockCount == 0 : _queue.Count == 0);

    /// <summary>The writer's own lock again: counted with <see cref="AttrRecursive"/> (for writing only).</summary>
    private int LockAgain(bool write)
    {
        if (!write)
        {
            return SceError.RW_LOCK_FAILED_TO_LOCK;
        }

        if ((Attr & AttrRecursive) == 0)
        {
            return SceError.RW_LOCK_RECURSIVE;
        }

        if (LockCount == int.MaxValue)
        {
            return SceError.RW_LOCK_LOCK_OVF;
        }

        LockCount++;
        return SceError.OK;
    }

    /// <summary>Takes an <see cref="Available"/> lock for the caller.</summary>
    private int Take(GuestThread? self, bool write)
    {
        if (LockCount == int.MaxValue)
        {
            return SceError.RW_LOCK_LOCK_OVF;
        }

        if (write)
        {
            Writer = self;
        }

        LockCount++;
        return SceError.OK;
    }

    private int Lock(GuestThread self, bool write, Ptr<uint> timeout)
    {
        int result;
        bool woken;
        lock (this)
        {
            if (_deleted)
            {
                return SceError.WAIT_DELETE;
            }

            ReleaseExpired();
            WaitRequest? request = TakeRestored(self);
            if (request != null && (request.Granted || request.Error != 0))
            {
                _granted.Remove(request);
                ObserveSignal();
                return request.Granted ? SceError.OK : request.Error;
            }

            if (request == null && Writer == self)
            {
                return LockAgain(write);
            }

            var deadline = Wait.CallbackWaitDeadline.Start(timeout);
            while (true)
            {
                // threadmgr 3.74 (0x81024bb0): a CB lock runs the notified callbacks before it looks at the lock
                if (request == null && Wait.CallbacksPending(self))
                {
                    Wait.RunCallbacks(this, self);
                    if (_deleted)
                    {
                        return SceError.WAIT_DELETE;
                    }

                    ReleaseExpired();
                }
                if (request == null)
                {
                    if (Available(write))
                    {
                        int taken = Take(self, write);
                        if (taken == SceError.OK)
                        {
                            ObserveSignal();
                            deadline.WriteLeft(timeout);
                        }

                        return taken;
                    }

                    if (!deadline.PrepareSleep(timeout))
                    {
                        return SceError.WAIT_TIMEOUT;
                    }

                    request = new WaitRequest { Write = write, Priority = self.Priority, ThreadUid = self.Uid };
                    Enqueue(request);
                }

                request.Thread = self;
                request.StartTicks = GuestClock.Ticks;
                request.LimitUs = timeout.IsNull ? -1 : timeout.Value;
                try
                {
                    // a writer that ends without unlocking leaves the lock to the queue: looked at on every wake-up slice
                    result = Wait.UntilGranted(this, (rwLock: this, request),
                        static s => s.request.Granted || s.rwLock.HandOverFromEndedWriter(s.request) || Wait.CallbacksPending(s.request.Thread),
                        timeout, static s => s.request.Granted ? 0 : s.request.Error, WaitDescription, out woken);
                }
                finally
                {
                    if (request.Granted)
                    {
                        _granted.Remove(request);
                    }
                    else if (request.Error == 0 && Dequeue(request))
                    {
                        // its own timeout, a notification that came before it was queued, or the thread's end
                        if (HandOver())
                        {
                            Monitor.PulseAll(this);
                        }
                    }
                }
                if (request.Granted)
                {
                    result = SceError.OK; // a timeout or cancel that came after the hand-over does not undo it
                    break;
                }

                if (request.Error == SceError.NOTIFY_CALLBACK || (request.Error == 0 && result == SceError.OK))
                {
                    // off the queue for its callbacks: run them, look at the lock again, queue anew behind the others
                    deadline.Interrupted = true;
                    request = null;
                    continue;
                }
                if (request.Error == SceError.WAIT_TIMEOUT)
                {
                    result = SceError.WAIT_TIMEOUT;
                    timeout.TrySet(0);
                }

                break;
            }
        }
        if (woken && result == SceError.OK)
        {
            Wait.WakeLatency();
        }

        return result;
    }

    private void Enqueue(WaitRequest request)
    {
        if (request.Write)
        {
            WriteWaiters++;
        }
        else
        {
            ReadWaiters++;
        }

        if ((Attr & AttrThreadPriority) == 0)
        {
            _queue.Add(request);
            return;
        }

        int index = 0;
        while (index < _queue.Count && _queue[index].Priority <= request.Priority)
        {
            index++;
        }

        _queue.Insert(index, request);
    }

    /// <summary>Takes the request off the queue; false when it was not there.</summary>
    private bool Dequeue(WaitRequest request)
    {
        if (!_queue.Remove(request))
        {
            return false;
        }

        if (request.Write)
        {
            WriteWaiters--;
        }
        else
        {
            ReadWaiters--;
        }

        return true;
    }

    private void Grant(WaitRequest request)
    {
        Dequeue(request);
        if (request.Write)
        {
            Writer = request.Thread;
        }

        LockCount++;
        request.Granted = true;
        _granted.Add(request);
    }

    /// <summary>The first waiting writer, in queue order, gets the lock (threadmgr 3.74: the unlocks and the class's
    /// wait-release handler look past the readers queued ahead of it).</summary>
    private bool GrantFirstWriter()
    {
        foreach (WaitRequest request in _queue)
        {
            if (request.Write)
            {
                Grant(request);
                return true;
            }
        }

        return false;
    }

    /// <summary>Every waiter (all readers: no writer waits) gets the lock.</summary>
    private bool GrantAllReaders()
    {
        bool granted = _queue.Count > 0;
        while (_queue.Count > 0)
        {
            Grant(_queue[0]);
        }

        return granted;
    }

    /// <summary>
    /// The class's wait-release handler (threadmgr 3.74, 0x810249b8), run when a waiter leaves the queue without the lock
    /// (its timeout, a callback notification): with no writer holding it, every waiting reader gets it when no writer
    /// waits, else the first waiting writer once nobody holds it. True when it gave the lock to someone.
    /// </summary>
    private bool HandOver()
    {
        if (_queue.Count == 0 || Writer != null)
        {
            return false;
        }

        if (WriteWaiters == 0)
        {
            return GrantAllReaders();
        }

        return LockCount == 0 && GrantFirstWriter();
    }

    /// <summary>Wait predicate: a writer that ended without unlocking leaves the lock to the queue (the woken one may be
    /// another waiter). True when <paramref name="request"/> got it.</summary>
    private bool HandOverFromEndedWriter(WaitRequest request)
    {
        if (Writer is { Status: ThreadStatus.Dormant })
        {
            Writer = null;
            LockCount = 0;
            if (HandOver())
            {
                Monitor.PulseAll(this);
            }
        }

        return request.Granted;
    }

    /// <summary>A callback notification reached a CB waiter (threadmgr 3.74's notify ends its sleep with
    /// NOTIFY_CALLBACK): it leaves the queue as a timed-out waiter does, runs its callbacks and locks again.</summary>
    public void InterruptForCallbacks(GuestThread thread)
    {
        foreach (WaitRequest request in _queue)
        {
            if (request.Thread != thread)
            {
                continue;
            }

            request.Error = SceError.NOTIFY_CALLBACK;
            Dequeue(request);
            HandOver();
            return;
        }
    }

    /// <summary>
    /// The kernel's timers at the waiters' guest-time deadlines: a waiter whose timeout has passed is off the queue
    /// (WAIT_TIMEOUT), however late its host thread is to notice, and each one leaving runs the hand-over for the
    /// others, in the order the deadlines came. Called before every operation that looks at the queue.
    /// </summary>
    private void ReleaseExpired()
    {
        bool changed = false;
        while (true)
        {
            WaitRequest? earliest = null;
            foreach (WaitRequest request in _queue)
            {
                if (request.LimitUs < 0 || !Wait.Expired(request.StartTicks, request.LimitUs))
                {
                    continue;
                }

                if (earliest == null || Deadline(request) < Deadline(earliest))
                {
                    earliest = request;
                }
            }
            if (earliest == null)
            {
                break;
            }

            earliest.Error = SceError.WAIT_TIMEOUT;
            Dequeue(earliest);
            HandOver();
            changed = true;
        }
        if (changed)
        {
            Monitor.PulseAll(this); // the timed-out and the served waiters: nobody was woken by this thread
        }
    }

    /// <summary>A waiter's deadline, for the order the timers fire in (the limit in guest microseconds).</summary>
    private static double Deadline(WaitRequest request) => request.StartTicks + (double)request.LimitUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000 / TimeScale.Wait;

    /// <summary>The place a loaded save state kept for the calling thread's lock, if any.</summary>
    private WaitRequest? TakeRestored(GuestThread self)
    {
        for (int i = 0; i < _restored.Count; i++)
        {
            if (_restored[i].ThreadUid == self.Uid)
            {
                WaitRequest request = _restored[i];
                _restored.RemoveAt(i);
                return request;
            }
        }

        return null;
    }

    /// <summary>What sceKernelGetRWLockInfo reports (timed-out waiters already off the queue).</summary>
    internal (int Count, GuestThread? Writer, int ReadWaiters, int WriteWaiters) Info()
    {
        lock (this)
        {
            ReleaseExpired();
            return (LockCount, Writer, ReadWaiters, WriteWaiters);
        }
    }

    /// <summary>sceKernelTryLockReadRWLock (threadmgr 3.74, 0x810254f0): only with no writer and nobody waiting.</summary>
    public int TryLockRead(GuestThread self)
    {
        lock (this)
        {
            ReleaseExpired();
            if (!Available(write: false))
            {
                return SceError.RW_LOCK_FAILED_TO_LOCK;
            }

            int taken = Take(self, write: false);
            if (taken == SceError.OK)
            {
                ObserveSignal();
            }

            return taken;
        }
    }

    /// <summary>sceKernelTryLockWriteRWLock (threadmgr 3.74, 0x81025758): the writer's recursion, else only when
    /// nobody holds it.</summary>
    public int TryLockWrite(GuestThread self)
    {
        lock (this)
        {
            ReleaseExpired();
            if (Writer == self)
            {
                return LockAgain(write: true);
            }

            if (!Available(write: true))
            {
                return SceError.RW_LOCK_FAILED_TO_LOCK;
            }

            int taken = Take(self, write: true);
            if (taken == SceError.OK)
            {
                ObserveSignal();
            }

            return taken;
        }
    }

    /// <summary>sceKernelUnlockReadRWLock (threadmgr 3.74, 0x810255b4): RW_LOCK_FAILED_TO_UNLOCK while a writer holds
    /// it, RW_LOCK_UNLOCK_UDF with no reader; the last reader's unlock hands it to the first waiting writer.</summary>
    public int UnlockRead()
    {
        lock (this)
        {
            ReleaseExpired();
            if (Writer != null)
            {
                return SceError.RW_LOCK_FAILED_TO_UNLOCK;
            }

            if (LockCount < 1)
            {
                return SceError.RW_LOCK_UNLOCK_UDF;
            }

            LockCount--;
            RecordSignal();
            if (LockCount == 0 && WriteWaiters > 0 && GrantFirstWriter())
            {
                WakeWaiters();
            }

            return SceError.OK;
        }
    }

    /// <summary>sceKernelUnlockWriteRWLock (threadmgr 3.74, 0x81025888): RW_LOCK_NOT_OWNED for any thread but the
    /// writer; the unlock that brings the count to 0 hands it to the first waiting writer, or with none to every waiting
    /// reader.</summary>
    public int UnlockWrite(GuestThread self)
    {
        lock (this)
        {
            ReleaseExpired();
            if (Writer != self)
            {
                return SceError.RW_LOCK_NOT_OWNED;
            }

            RecordSignal();
            if (--LockCount > 0)
            {
                return SceError.OK;
            }

            Writer = null;
            LockCount = 0;
            if (HandOver())
            {
                WakeWaiters();
            }

            return SceError.OK;
        }
    }

    /// <summary>sceKernelCancelRWLock (threadmgr 3.74, 0x81025a50): a flag above 1 is INVALID_FLAGS; the lock is freed
    /// and every waiter returns WAIT_CANCEL (numRead / numWrite: how many readers and writers waited); with flag 1
    /// (SCE_KERNEL_RW_LOCK_CANCEL_WITH_WRITE_LOCK) the caller then holds it for writing.</summary>
    public int Cancel(GuestThread? self, Ptr<uint> numRead, Ptr<uint> numWrite, uint flag)
    {
        if (flag > 1)
        {
            return SceError.INVALID_FLAGS;
        }

        lock (this)
        {
            ReleaseExpired();
            numRead.TrySet((uint)ReadWaiters);
            numWrite.TrySet((uint)WriteWaiters);
            DequeueAll(SceError.WAIT_CANCEL);
            Writer = flag == 1 ? self : null;
            LockCount = flag == 1 ? 1 : 0;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            DequeueAll(SceError.WAIT_DELETE);
            Writer = null;
            LockCount = 0;
            WakeWaiters();
        }
    }

    private void DequeueAll(int error)
    {
        foreach (WaitRequest request in _queue)
        {
            request.Error = error;
        }

        _queue.Clear();
        ReadWaiters = 0;
        WriteWaiters = 0;
    }
}

public sealed partial class Callback : KernelObject
{
    public override int UidClass => 8;
    public GuestThread Owner = null!;
    public uint Function;
    public uint Arg;
    public uint Attr;
    public int NotifyCount;
    public int NotifyArg;
    /// <summary>The notifier's UID the callback gets in r0 and SceKernelCallbackInfo reports: threadmgr 3.74's
    /// sceKernelNotifyCallback (0x8100c820) stores -1 (<see cref="DirectNotifier"/>); 0 after a cancel.</summary>
    public int NotifyId;

    /// <summary>The notifier ID of sceKernelNotifyCallback (FW 3.74 writes -1, no notifying object).</summary>
    public const int DirectNotifier = -1;

    /// <summary>sceKernelDeleteCallback reached it: it runs no more, and its UID is freed once a run in progress ends
    /// (threadmgr's deleted flag, +0x20, and running count, +0x3C).</summary>
    public bool Deleted;

    /// <summary>Runs of it in progress.</summary>
    public int Running;

    /// <summary>sceKernelNotifyCallback (threadmgr 3.74, 0x8100c820): adds one to the count, keeps the last argument,
    /// marks the owner as having callbacks to run and, when it sleeps in a CB wait, takes it off that wait (the wait
    /// ends with the kernel's internal NOTIFY_CALLBACK, runs the callbacks and waits again from the start).</summary>
    public void Notify(int arg) => Notify(arg, DirectNotifier);

    /// <summary>A notification from <paramref name="notifyId"/> (a simple event's notify sets the event's UID, threadmgr
    /// 3.74's 0x8101cfcc), otherwise as <see cref="Notify(int)"/>.</summary>
    public void Notify(int arg, int notifyId)
    {
        lock (this)
        {
            NotifyCount++;
            NotifyArg = arg;
            NotifyId = notifyId;
        }
        Interlocked.Increment(ref Owner.PendingCallbacks);
        lock (Owner.WaitLock)
        {
            Monitor.PulseAll(Owner.WaitLock);
        }

        object? s = Owner.CurrentWaitSync;
        if (s != null)
        {
            lock (s)
            {
                if (s is ICallbackInterruptedWait interrupted && Owner.CanRunCallbacks)
                {
                    interrupted.InterruptForCallbacks(Owner);
                }

                Monitor.PulseAll(s);
            }
        }
    }
}

/// <summary>A kernel object whose wait queue a callback notification takes a CB waiter off (threadmgr 3.74: the
/// notify ends the wait with NOTIFY_CALLBACK; the waiter runs its callbacks, checks the object again and, if it still
/// has to wait, joins the queue anew, behind the waiters already there). Called holding the object's lock.</summary>
public interface ICallbackInterruptedWait
{
    void InterruptForCallbacks(GuestThread thread);
}

/// <summary>Event bits shared by simple events and timers (psp2kern/kernel/threadmgr.h).</summary>
public static class EventBits
{
    public const uint Create = 0x4, Delete = 0x8, Error = 0x10, Timer = 0x8000, UserMask = 0xFF000000;
    public const uint AttrAutoReset = 0x100, AttrOpenable = 0x80, AttrThPrio = 0x2000;
    /// <summary>SCE_KERNEL_ATTR_NOTIFY_CB_WAKEUP_ONLY: accepted at creation (Injustice's NRS async IO events use 0x900);
    /// it only affects which callbacks a CB wait runs, nothing reads it here (Vita3K neither).</summary>
    public const uint AttrNotifyCbWakeupOnly = 0x800;
}

/// <summary>
/// Kernel timer: a microsecond counter on process time plus an optional (repeating) SET or PULSE event that
/// sceKernelWaitEvent/PollEvent can wait for (result EVENT_TIMER). Only the first waiter waits for the event.
/// </summary>
public sealed partial class TimerObject : EventObject
{
    public override int UidClass => 6;
    public uint Attr;
    /// <summary>Process time in microseconds (sceKernelGetProcessTime base).</summary>
    public required Func<ulong> Clock;
    public bool Started, Repeat, Pulse, EventSet;
    public ulong Base, StoppedCount, Interval;
    public ulong NextEvent = ulong.MaxValue;
    private readonly LinkedList<object> _waiters = new();
    private bool _deleted;
    private int _cancelled;

    public int NumWaiters
    {
        get
        {
            lock (this)
            {
                return _waiters.Count;
            }
        }
    }

    public ulong Count()
    {
        lock (this)
        {
            return Started ? Clock() - Base : StoppedCount;
        }
    }

    /// <summary>Returns 1 when it was already running, else 0.</summary>
    public int Start()
    {
        lock (this)
        {
            if (Started)
            {
                return 1;
            }

            Started = true;
            Base = Clock() - StoppedCount;
            if (Interval != 0)
            {
                Schedule();
            }

            return 0;
        }
    }

    /// <summary>Returns 1 when it stopped a running timer, 0 when it was already stopped.</summary>
    public int Stop()
    {
        lock (this)
        {
            if (!Started)
            {
                return 0;
            }

            StoppedCount = Clock() - Base;
            Started = false;
            NextEvent = ulong.MaxValue;
            return 1;
        }
    }

    /// <summary>Replaces the count, returns the old one.</summary>
    public ulong SetTime(ulong value)
    {
        lock (this)
        {
            ulong now = Clock();
            ulong old = Started ? now - Base : StoppedCount;
            if (Started)
            {
                Base = now - value;
            }
            else
            {
                StoppedCount = value;
            }

            return old;
        }
    }

    private void Schedule()
    {
        NextEvent = Clock() + Interval;
        WakeWaiters();
    }

    public int SetEvent(int type, ulong interval, bool repeat)
    {
        if (type is not (0 or 1))
        {
            return SceError.ILLEGAL_TYPE;
        }

        lock (this)
        {
            Pulse = type == 1;
            Repeat = repeat;
            Interval = interval;
            if (Started)
            {
                Schedule();
            }

            return SceError.OK;
        }
    }

    private void Advance(ulong now)
    {
        if (Repeat && Interval != 0)
        {
            NextEvent += ((now - NextEvent - 1) / Interval + 1) * Interval;
        }
        else
        {
            NextEvent = ulong.MaxValue;
        }
    }

    private int TryTake(Ptr<uint> result, Ptr<ulong> userData, ulong now)
    {
        result.TrySet(0);
        userData.TrySet(0);
        if (NextEvent < now)
        {
            if (!Pulse)
            {
                EventSet = true; // a pulse is only seen by waiters
            }

            Advance(now);
        }
        if (!EventSet)
        {
            return SceError.EVENT_COND;
        }

        result.TrySet(EventBits.Timer);
        if ((Attr & EventBits.AttrAutoReset) != 0)
        {
            EventSet = false;
        }

        return SceError.OK;
    }

    public int Poll(Ptr<uint> result, Ptr<ulong> userData)
    {
        lock (this)
        {
            return TryTake(result, userData, Clock());
        }
    }

    public int Clear()
    {
        lock (this)
        {
            EventSet = false;
        }

        return SceError.OK;
    }

    private bool Fired(LinkedListNode<object> node, ulong now) => _waiters.First == node && (EventSet || now > NextEvent);

    public int Wait(Ptr<uint> result, Ptr<ulong> userData, Ptr<uint> timeout)
    {
        lock (this)
        {
            ulong now = Clock();
            int r = TryTake(result, userData, now);
            if (r != SceError.EVENT_COND)
            {
                return r;
            }

            LinkedListNode<object> node = _waiters.AddLast(new object());
            int gen = _cancelled;
            ulong start = now;
            long? limit = timeout.IsNull ? null : timeout.Value;
            try
            {
                while (true)
                {
                    if (_deleted)
                    {
                        WriteBack(timeout, limit, start);
                        return SceError.WAIT_DELETE;
                    }
                    if (_cancelled != gen)
                    {
                        WriteBack(timeout, limit, start);
                        return SceError.WAIT_CANCEL;
                    }
                    if (Fired(node, now))
                    {
                        break;
                    }

                    long? slice = limit.HasValue ? limit.Value - (long)(now - start) : null;
                    if (slice is <= 0)
                    {
                        timeout.TrySet(0);
                        return SceError.WAIT_TIMEOUT;
                    }
                    if (_waiters.First == node && NextEvent != ulong.MaxValue)
                    {
                        long toEvent = NextEvent >= now ? (long)(NextEvent - now) + 1 : 1;
                        slice = slice.HasValue ? Math.Min(slice.Value, toEvent) : toEvent;
                    }
                    // wakes on delete/cancel/reschedule (PulseAll) or when the slice to the next event ends
                    global::VitaSharp.Core.Kernel.Wait.Until(this, () => _deleted || _cancelled != gen, slice, null, WaitDescription);
                    now = Clock();
                }
                WriteBack(timeout, limit, start);
                result.TrySet(EventBits.Timer);
                userData.TrySet(0);
                EventSet = !Pulse && (Attr & EventBits.AttrAutoReset) == 0;
                Advance(now);
                return SceError.OK;
            }
            finally
            {
                _waiters.Remove(node);
                WakeWaiters();
            }
        }
    }

    private void WriteBack(Ptr<uint> timeout, long? limit, ulong start)
    {
        if (limit.HasValue)
        {
            timeout.TrySet((uint)Math.Max(0, limit.Value - (long)(Clock() - start)));
        }
    }

    public int Cancel(Ptr<uint> numWait)
    {
        lock (this)
        {
            numWait.TrySet((uint)_waiters.Count);
            _cancelled++;
            Repeat = Pulse = false;
            Interval = 0;
            NextEvent = ulong.MaxValue;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            WakeWaiters();
        }
    }
}

/// <summary>
/// Simple event (sceKernelCreateSimpleEvent): a 32-bit pattern plus the last user data. Set wakes, in queue order,
/// every waiter whose pattern matches (consuming the bits with AUTO_RESET); Pulse does the same and then restores the
/// previous pattern, so only threads already waiting see it.
/// </summary>
public sealed partial class SimpleEvent : EventObject, ICallbackInterruptedWait
{
    public override int UidClass => 13;
    public uint Attr;
    public uint Pattern;
    public ulong LastUserData;
    private bool AutoReset => (Attr & EventBits.AttrAutoReset) != 0;
    private bool _deleted;

    private sealed class Waiter
    {
        public uint WaitPattern;
        public Ptr<uint> Result;
        public Ptr<ulong> UserData;
        public int Priority;
        public int? Code;
        /// <summary>The waiter's address space: the thread that sets the event writes the results there (simple
        /// events are shared between processes).</summary>
        public AddressSpace? Space;
        /// <summary>The waiting thread (null: a host caller).</summary>
        public GuestThread? Thread;
    }

    private readonly LinkedList<Waiter> _waiters = new();
    public int NumWaiters
    {
        get
        {
            lock (this)
            {
                return _waiters.Count;
            }
        }
    }

    private int TryTake(uint waitPattern, Ptr<uint> result, Ptr<ulong> userData)
    {
        if ((Pattern & waitPattern) == 0)
        {
            result.TrySet(0);
            return SceError.EVENT_COND;
        }
        result.TrySet(Pattern & waitPattern);
        if (AutoReset)
        {
            Pattern &= ~waitPattern;
        }

        userData.TrySet(LastUserData);
        return SceError.OK;
    }

    public int Poll(uint waitPattern, Ptr<uint> result, Ptr<ulong> userData)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            return TryTake(waitPattern, result, userData);
        }
    }

    public int Wait(GuestThread? self, uint waitPattern, Ptr<uint> result, Ptr<ulong> userData, Ptr<uint> timeout)
    {
        if (waitPattern == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (this)
        {
            var deadline = global::VitaSharp.Core.Kernel.Wait.CallbackWaitDeadline.Start(timeout);
            while (true)
            {
                // threadmgr 3.74 (0x8101d220): a CB wait runs the notified callbacks before it looks at the pattern; a
                // notification takes it off the queue, and it looks again and queues anew at the end
                if (global::VitaSharp.Core.Kernel.Wait.CallbacksPending(self))
                {
                    global::VitaSharp.Core.Kernel.Wait.RunCallbacks(this, self!);
                    if (_deleted)
                    {
                        result.TrySet(EventBits.Error);
                        userData.TrySet(LastUserData);
                        return SceError.WAIT_DELETE;
                    }
                }
                int r = TryTake(waitPattern, result, userData);
                if (r != SceError.EVENT_COND)
                {
                    deadline.WriteLeft(timeout);
                    return r;
                }

                if (!deadline.PrepareSleep(timeout))
                {
                    return SceError.WAIT_TIMEOUT;
                }

                var w = new Waiter { WaitPattern = waitPattern, Result = result, UserData = userData, Priority = self?.Priority ?? 0, Space = AddressSpace.Current, Thread = self };
                LinkedListNode<Waiter> node;
                if ((Attr & EventBits.AttrThPrio) != 0)
                {
                    LinkedListNode<Waiter>? at = _waiters.First;
                    while (at != null && at.Value.Priority <= w.Priority)
                    {
                        at = at.Next;
                    }

                    node = at == null ? _waiters.AddLast(w) : _waiters.AddBefore(at, w);
                }
                else
                {
                    node = _waiters.AddLast(w);
                }

                r = global::VitaSharp.Core.Kernel.Wait.Until(this, w, static w => w.Code.HasValue || global::VitaSharp.Core.Kernel.Wait.CallbacksPending(w.Thread),
                    timeout, null, WaitDescription);
                if (w.Code is { } code)
                {
                    r = code;
                }
                else if (node.List != null)
                {
                    _waiters.Remove(node);
                }

                if (r == SceError.NOTIFY_CALLBACK || (!w.Code.HasValue && r == SceError.OK))
                {
                    deadline.Interrupted = true; // off the queue for its callbacks
                    continue;
                }
                if (r < 0 && r != SceError.WAIT_TIMEOUT)
                {
                    result.TrySet(EventBits.Error);
                    userData.TrySet(LastUserData);
                }
                return r;
            }
        }
    }

    /// <summary>A callback notification reached a CB waiter (threadmgr 3.74's notify ends its sleep with
    /// NOTIFY_CALLBACK): it leaves the queue, so a set meanwhile goes to the waiters behind, runs its callbacks, looks
    /// at the pattern again and queues anew at the end.</summary>
    public void InterruptForCallbacks(GuestThread thread)
    {
        for (LinkedListNode<Waiter>? n = _waiters.First; n != null; n = n.Next)
        {
            if (n.Value.Thread != thread || n.Value.Code.HasValue)
            {
                continue;
            }

            n.Value.Code = SceError.NOTIFY_CALLBACK;
            _waiters.Remove(n);
            return;
        }
    }

    /// <summary>
    /// sceKernelSetEventWithNotifyCallback / sceKernelPulseEventWithNotifyCallback (threadmgr 3.74: 0xB7DF3EDF /
    /// 0x714A107A, then 0x8101cfcc): a set pattern of 0 does nothing; otherwise the set (pulse) of
    /// <see cref="SetOrPulse"/>, then, under the event's lock, every registered callback is notified in list order as
    /// sceKernelNotifyCallback does, with the registration's notifier ID and <paramref name="notifyArg"/> (a deleted
    /// callback's registration is dropped).
    /// </summary>
    public int SetOrPulseWithNotify(uint setPattern, ulong userData, int notifyArg, bool pulse)
    {
        if ((setPattern & ~EventBits.UserMask) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        if (setPattern == 0)
        {
            return SceError.OK;
        }

        lock (this)
        {
            SetOrPulse(setPattern, userData, pulse);
            NotifyRegisteredCallbacks(notifyArg);
            return SceError.OK;
        }
    }

    public int SetOrPulse(uint setPattern, ulong userData, bool pulse)
    {
        if ((setPattern & ~EventBits.UserMask) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        lock (this)
        {
            uint oldPattern = Pattern;
            ulong oldData = LastUserData;
            Pattern |= setPattern;
            LastUserData = userData;
            for (LinkedListNode<Waiter>? n = _waiters.First; n != null;)
            {
                LinkedListNode<Waiter>? next = n.Next;
                Waiter w = n.Value;
                if ((Pattern & w.WaitPattern) != 0)
                {
                    using (AddressSpace.Use(w.Space))
                    {
                        w.Result.TrySet(Pattern & w.WaitPattern);
                        w.UserData.TrySet(LastUserData);
                    }
                    if (AutoReset)
                    {
                        Pattern &= ~w.WaitPattern;
                    }

                    w.Code = SceError.OK;
                    _waiters.Remove(n);
                }
                n = next;
            }
            if (pulse)
            {
                Pattern = oldPattern & ~setPattern;
                LastUserData = oldData;
            }
            WakeWaiters();
            return SceError.OK;
        }
    }

    /// <summary>Keeps only the bits of <paramref name="keepPattern"/> (Vita3K: only user bits may be passed).</summary>
    public int Clear(uint keepPattern)
    {
        if ((keepPattern & ~EventBits.UserMask) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        lock (this)
        {
            Pattern &= keepPattern;
        }

        return SceError.OK;
    }

    /// <summary>Cancels every wait (WAIT_CANCEL) and replaces the whole pattern (the create bit too) and the user data.</summary>
    public int CancelWithSetPattern(uint setPattern, ulong userData, Ptr<uint> numWait)
    {
        if ((setPattern & ~EventBits.UserMask) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        lock (this)
        {
            numWait.TrySet((uint)_waiters.Count);
            foreach (Waiter w in _waiters)
            {
                w.Code = SceError.WAIT_CANCEL;
            }

            _waiters.Clear();
            Pattern = setPattern;
            LastUserData = userData;
            WakeWaiters();
            return SceError.OK;
        }
    }

    public int Cancel(Ptr<uint> numWait)
    {
        lock (this)
        {
            numWait.TrySet((uint)_waiters.Count);
            foreach (Waiter w in _waiters)
            {
                w.Code = SceError.WAIT_CANCEL;
            }

            _waiters.Clear();
            WakeWaiters();
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            // waits that include the DELETE bit end successfully with it, the others with WAIT_DELETE
            foreach (Waiter w in _waiters)
            {
                if ((w.WaitPattern & EventBits.Delete) != 0)
                {
                    using (AddressSpace.Use(w.Space))
                    {
                        w.Result.TrySet((Pattern | EventBits.Delete) & w.WaitPattern);
                        w.UserData.TrySet(LastUserData);
                    }
                    w.Code = SceError.OK;
                }
                else
                {
                    w.Code = SceError.WAIT_DELETE;
                }
            }
            _waiters.Clear();
            WakeWaiters();
        }
    }
}

/// <summary>Handler registered by sceKernelRegisterThreadEventHandler (thread start/exit notifications).</summary>
public sealed partial class ThreadEventHandler : KernelObject
{
    public override int UidClass => 9;
    public int ThreadMask;
    public uint EventMask, Handler, Common;
}
