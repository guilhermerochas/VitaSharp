using VitaSharp.Core.Util;
using System.Diagnostics;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Kernel;

/// <summary>Blocking-wait helper with Vita semantics: timeout in microseconds, remaining time written back.</summary>
public static class Wait
{
    /// <summary>Wake-up latency of a blocking syscall on the Vita (kernel entry, scheduler, context switch on a 333 MHz
    /// Cortex-A9): tens of microseconds. Host wake-ups take ~1 us, which makes job systems that measure their own
    /// synchronisation-heavy work (physics steps with per-step time budgets) behave differently; 0 disables.</summary>
    public static int SyscallLatencyUs = 30;

    static void Latency()
    {
        if (SyscallLatencyUs <= 0 || GuestClock.Turbo) return;
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
        => UntilCore(sync, (ready, abort), static s => s.ready(), default, timeoutUs, static s => s.abort?.Invoke() ?? 0, what);

    static int UntilCore<T>(object sync, T state, Func<T, bool> ready, Ptr<uint> timeout, long? limitUs, Func<T, int>? abort, string what)
    {
        var self = GuestThread.Current;
        if (self != null) { self.Status = ThreadStatus.Waiting; self.WaitingOn = what; self.CurrentWaitSync = sync; }
        try
        {
            long start = GuestClock.Ticks; // timeouts run on guest time
            bool waited = false;
            while (true)
            {
                if (abort != null)
                {
                    int a = abort(state);
                    if (a != 0) return a;
                }
                if (ready(state)) { if (waited) Latency(); WriteRemaining(timeout, limitUs, start); return SceError.OK; }
                if (self != null && self.Core.StopRequested) throw new GuestThreadExitException(0, false);
                if (self != null && self.CallbackWait && Volatile.Read(ref self.PendingCallbacks) > 0 && GuestThread.CallbackProcessor != null)
                {
                    Monitor.Exit(sync);
                    try { GuestThread.CallbackProcessor(self); }
                    finally { Monitor.Enter(sync); }
                    continue;
                }
                int sliceMs = 50;
                if (limitUs.HasValue)
                {
                    long leftUs = limitUs.Value - ElapsedUs(start);
                    if (leftUs <= 0) { timeout.TrySet(0); return SceError.WAIT_TIMEOUT; }
                    sliceMs = (int)Math.Clamp((leftUs / GuestClock.Speed + 999) / 1000, 1, 50);
                    if (GuestClock.Turbo)
                    {
                        // turbo: give the event a moment, then jump guest time to the timeout instead of waiting it out
                        if (!Monitor.Wait(sync, 1)) GuestClock.WaitUntil(start + GuestClock.FromSeconds(limitUs.Value / 1e6));
                        continue;
                    }
                }
                // blocked: give the emulated core away; take one back before touching guest state again (never while
                // holding sync, or a core-holding signaller could deadlock against us)
                if (self != null) Scheduler.Release(self);
                Monitor.Wait(sync, sliceMs);
                waited = true;
                if (self != null && !self.HasCore)
                {
                    Monitor.Exit(sync);
                    try { Scheduler.Acquire(self); }
                    finally { Monitor.Enter(sync); }
                }
            }
        }
        finally
        {
            if (self != null) { self.Status = ThreadStatus.Running; self.WaitingOn = ""; self.CurrentWaitSync = null; }
        }
    }

    static long ElapsedUs(long startTicks) => (long)((GuestClock.Ticks - startTicks) * 1_000_000 / Stopwatch.Frequency * TimeScale.Wait);

    static void WriteRemaining(Ptr<uint> timeout, long? limitUs, long startTicks)
    {
        if (timeout.IsNull || !limitUs.HasValue) return;
        long left = limitUs.Value - ElapsedUs(startTicks);
        timeout.Value = (uint)Math.Max(0, left);
    }

    /// <summary>Plain sleep that stays responsive to emulator shutdown.</summary>
    public static void Sleep(ulong microseconds)
    {
        var self = GuestThread.Current;
        if (microseconds == 0) { Thread.Yield(); Latency(); return; }
        if (microseconds < (ulong)SyscallLatencyUs) microseconds = (ulong)SyscallLatencyUs; // a delay cannot be shorter than a reschedule
        // guest time: follows the test speed factor, and turbo mode skips the wait
        using var blocked = Scheduler.Block();
        long end = GuestClock.Ticks + GuestClock.FromSeconds(microseconds / 1e6 / TimeScale.Wait), slice = GuestClock.FromSeconds(0.05);
        while (GuestClock.Ticks < end)
        {
            if (self != null && self.Core.StopRequested) throw new GuestThreadExitException(0, false);
            // sleep in slices so a stop request is noticed; short delays really sleep instead of spinning a host core
            GuestClock.WaitUntil(Math.Min(end, GuestClock.Ticks + slice));
        }
    }
}

public sealed class Semaphore : KernelObject
{
    public uint Attr;
    public int InitCount, Count, MaxCount;
    public int Waiters;
    bool _deleted;
    int _cancelled;

    public int WaitSema(int need, Ptr<uint> timeout)
    {
        if (need <= 0 || need > MaxCount) return SceError.INVALID_ARGUMENT;
        lock (this)
        {
            Waiters++;
            int gen = _cancelled;
            try
            {
                int r = Wait.Until(this, (s: this, need, gen), static t => t.s.Count >= t.need, timeout,
                    static t => t.s._deleted ? SceError.WAIT_DELETE : t.s._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
                if (r == SceError.OK) Count -= need;
                return r;
            }
            finally { Waiters--; }
        }
    }

    public int Poll(int need)
    {
        lock (this)
        {
            if (Count < need) return SceError.SEMA_ZERO;
            Count -= need;
            return SceError.OK;
        }
    }

    public int Signal(int n)
    {
        lock (this)
        {
            if (Count + n > MaxCount) return SceError.SEMA_OVF;
            Count += n;
            Monitor.PulseAll(this);
            return SceError.OK;
        }
    }

    public int Cancel(int setCount, Ptr<int> numWait)
    {
        lock (this)
        {
            numWait.TrySet(Waiters);
            Count = setCount < 0 ? InitCount : setCount;
            _cancelled++;
            Monitor.PulseAll(this);
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this) { _deleted = true; Monitor.PulseAll(this); }
    }
}

public sealed class EventFlag : KernelObject
{
    public const uint WaitAnd = 0, WaitOr = 1, WaitClear = 2, WaitClearPat = 4;
    public const uint AttrMulti = 0x1000;
    public uint Attr;
    public uint InitPattern, Pattern;
    public int Waiters;
    bool _deleted;
    int _cancelled;

    bool Matches(uint bits, uint mode) => (mode & WaitOr) != 0 ? (Pattern & bits) != 0 : (Pattern & bits) == bits;

    public int WaitFlag(uint bits, uint mode, Ptr<uint> outBits, Ptr<uint> timeout)
    {
        if (bits == 0) return SceError.INVALID_ARGUMENT;
        lock (this)
        {
            if ((Attr & AttrMulti) == 0 && Waiters > 0) return SceError.EVF_MULTI;
            Waiters++;
            int gen = _cancelled;
            try
            {
                int r = Wait.Until(this, (e: this, bits, mode, gen), static t => t.e.Matches(t.bits, t.mode), timeout,
                    static t => t.e._deleted ? SceError.WAIT_DELETE : t.e._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
                outBits.TrySet(Pattern);
                if (r == SceError.OK) Consume(bits, mode);
                return r;
            }
            finally { Waiters--; }
        }
    }

    void Consume(uint bits, uint mode)
    {
        if ((mode & WaitClear) != 0) Pattern = 0;
        else if ((mode & WaitClearPat) != 0) Pattern &= ~bits;
    }

    public int Poll(uint bits, uint mode, Ptr<uint> outBits)
    {
        lock (this)
        {
            outBits.TrySet(Pattern);
            if (!Matches(bits, mode)) return SceError.EVF_COND;
            Consume(bits, mode);
            return SceError.OK;
        }
    }

    public void Set(uint bits)
    {
        lock (this) { Pattern |= bits; Monitor.PulseAll(this); }
    }

    public void Clear(uint bits)
    {
        lock (this) Pattern &= bits;
    }

    public int Cancel(uint setPattern, Ptr<int> numWait)
    {
        lock (this)
        {
            numWait.TrySet(Waiters);
            Pattern = setPattern;
            _cancelled++;
            Monitor.PulseAll(this);
        }
        return SceError.OK;
    }

    public void Delete()
    {
        lock (this) { _deleted = true; Monitor.PulseAll(this); }
    }
}

/// <summary>Mutex used both for SceKernelMutex and lightweight mutexes (whose work area mirrors the state).</summary>
public sealed class Mutex : KernelObject
{
    public const uint AttrRecursive = 0x02;
    public uint Attr;
    public int InitCount;
    public GuestThread? Owner;
    public int LockCount;
    public int Waiters;
    public uint WorkArea; // lightweight mutex work area (0 for normal mutexes)
    bool _deleted;

    public bool IsLight => WorkArea != 0;

    void UpdateWorkArea()
    {
        if (WorkArea == 0) return;
        GuestMemory.W32(WorkArea + 0, Owner != null ? (uint)Owner.Uid : 0);
        GuestMemory.W32(WorkArea + 8, (uint)LockCount);
    }

    public void InitOwner(GuestThread? t, int count)
    {
        lock (this)
        {
            if (count > 0) { Owner = t; LockCount = count; }
            UpdateWorkArea();
        }
    }

    int FailedToOwn => IsLight ? SceError.LW_MUTEX_FAILED_TO_OWN : SceError.MUTEX_FAILED_TO_OWN;
    int Recursive => IsLight ? SceError.LW_MUTEX_RECURSIVE : SceError.MUTEX_RECURSIVE;

    public int TryLock(GuestThread self, int count)
    {
        lock (this)
        {
            if (Owner == self)
            {
                if ((Attr & AttrRecursive) == 0) return Recursive;
                LockCount += count;
                UpdateWorkArea();
                return SceError.OK;
            }
            if (Owner != null) return FailedToOwn;
            Owner = self;
            LockCount = count;
            UpdateWorkArea();
            return SceError.OK;
        }
    }

    public int Lock(GuestThread self, int count, Ptr<uint> timeout)
    {
        if (count <= 0) return SceError.INVALID_ARGUMENT;
        lock (this)
        {
            if (Owner == self)
            {
                if ((Attr & AttrRecursive) == 0) return Recursive;
                LockCount += count;
                UpdateWorkArea();
                return SceError.OK;
            }
            Waiters++;
            try
            {
                int r = Wait.Until(this, this, static m => m.Owner == null || m.Owner.Status == ThreadStatus.Dormant, timeout,
                    static m => m._deleted ? SceError.WAIT_DELETE : 0, WaitDescription);
                if (r != SceError.OK) return r;
                Owner = self;
                LockCount = count;
                UpdateWorkArea();
                return SceError.OK;
            }
            finally { Waiters--; }
        }
    }

    public int Unlock(GuestThread self, int count)
    {
        lock (this)
        {
            if (Owner != self) return IsLight ? SceError.LW_MUTEX_NOT_OWNED : SceError.MUTEX_NOT_OWNED;
            if (count > LockCount) return IsLight ? SceError.LW_MUTEX_UNLOCK_UDF : SceError.MUTEX_UNLOCK_UDF;
            LockCount -= count;
            if (LockCount == 0) Owner = null;
            UpdateWorkArea();
            Monitor.PulseAll(this);
            return SceError.OK;
        }
    }

    /// <summary>Fully releases the mutex (for condition waits) and returns the previous lock count.</summary>
    public int ReleaseAll(GuestThread self)
    {
        lock (this)
        {
            if (Owner != self) return -1;
            int c = LockCount;
            LockCount = 0;
            Owner = null;
            UpdateWorkArea();
            Monitor.PulseAll(this);
            return c;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            _deleted = true;
            Owner = null;
            LockCount = 0;
            if (WorkArea != 0) GuestMemory.W32(WorkArea, 0);
            Monitor.PulseAll(this);
        }
    }
}

/// <summary>Condition variable bound to a mutex (SceKernelCond and lightweight conds).</summary>
public sealed class CondVar : KernelObject
{
    public uint Attr;
    public Mutex Mutex = null!;
    public uint WorkArea;
    public int Waiters;
    long _signals;      // pending single-signal tokens
    long _broadcastGen;
    bool _deleted;

    public int WaitCond(GuestThread self, Ptr<uint> timeout)
    {
        int count;
        long myGen;
        lock (this)
        {
            count = Mutex.ReleaseAll(self);
            if (count < 0) return Mutex.IsLight ? SceError.LW_MUTEX_NOT_OWNED : SceError.MUTEX_NOT_OWNED;
            Waiters++;
            myGen = _broadcastGen;
            int r;
            try
            {
                r = Wait.Until(this, (c: this, myGen), static t =>
                {
                    if (t.c._broadcastGen != t.myGen) return true;
                    if (t.c._signals > 0) { t.c._signals--; return true; }
                    return false;
                }, timeout, static t => t.c._deleted ? SceError.WAIT_DELETE : 0, WaitDescription);
            }
            finally { Waiters--; }
            // reacquire outside our lock to avoid lock-order inversion with the mutex
            Monitor.Exit(this);
            try { Mutex.Lock(self, count, default); }
            finally { Monitor.Enter(this); }
            return r;
        }
    }

    public void Signal()
    {
        lock (this)
        {
            if (Waiters > _signals) _signals++;
            Monitor.PulseAll(this);
        }
    }

    public void SignalAll()
    {
        lock (this)
        {
            _broadcastGen++;
            _signals = 0;
            Monitor.PulseAll(this);
        }
    }

    public void Delete()
    {
        lock (this) { _deleted = true; Monitor.PulseAll(this); }
    }
}

public sealed class MsgPipe : KernelObject
{
    public const uint WaitAll = 0, WaitAsap = 1;
    public uint Attr;
    public int BufferSize;
    readonly Queue<byte> _data = new();
    public int SendWaiters, RecvWaiters;
    bool _deleted;
    int _cancelled;

    public int Send(uint src, int size, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
    {
        lock (this)
        {
            int gen = _cancelled;
            int sent = 0;
            bool asap = (mode & WaitAsap) != 0;
            SendWaiters++;
            try
            {
                while (sent < size)
                {
                    int free = BufferSize - _data.Count;
                    if (free > 0)
                    {
                        int n = Math.Min(free, size - sent);
                        var span = GuestMemory.Span(src + (uint)sent, n);
                        foreach (var b in span) _data.Enqueue(b);
                        sent += n;
                        Monitor.PulseAll(this);
                        if (asap) break;
                        continue;
                    }
                    if (poll) break;
                    int r = Wait.Until(this, (p: this, gen), static t => t.p._data.Count < t.p.BufferSize, timeout,
                        static t => t.p._deleted ? SceError.WAIT_DELETE : t.p._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
                    if (r != SceError.OK) { result.TrySet((uint)sent); return r; }
                }
            }
            finally { SendWaiters--; }
            result.TrySet((uint)sent);
            if (sent == 0 && size > 0) return SceError.MPP_FULL;
            if (!asap && sent < size) return SceError.MPP_FULL;
            return SceError.OK;
        }
    }

    public int Receive(uint dst, int size, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
    {
        lock (this)
        {
            int gen = _cancelled;
            int got = 0;
            bool asap = (mode & WaitAsap) != 0;
            RecvWaiters++;
            try
            {
                while (got < size)
                {
                    if (_data.Count > 0)
                    {
                        int n = Math.Min(_data.Count, size - got);
                        var span = GuestMemory.Span(dst + (uint)got, n);
                        for (int i = 0; i < n; i++) span[i] = _data.Dequeue();
                        got += n;
                        Monitor.PulseAll(this);
                        if (asap) break;
                        continue;
                    }
                    if (poll) break;
                    int r = Wait.Until(this, (p: this, gen), static t => t.p._data.Count > 0, timeout,
                        static t => t.p._deleted ? SceError.WAIT_DELETE : t.p._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
                    if (r != SceError.OK) { result.TrySet((uint)got); return r; }
                }
            }
            finally { RecvWaiters--; }
            result.TrySet((uint)got);
            if (got == 0 && size > 0) return SceError.MPP_EMPTY;
            return SceError.OK;
        }
    }

    public int Cancel(Ptr<int> numSend, Ptr<int> numRecv)
    {
        lock (this)
        {
            numSend.TrySet(SendWaiters);
            numRecv.TrySet(RecvWaiters);
            _cancelled++;
            Monitor.PulseAll(this);
        }
        return SceError.OK;
    }

    public void Delete()
    {
        lock (this) { _deleted = true; Monitor.PulseAll(this); }
    }
}

public sealed class RwLock : KernelObject
{
    public uint Attr;
    public int Readers;
    public GuestThread? Writer;
    public int WriteCount;
    public int ReadWaiters, WriteWaiters;
    bool _deleted;

    public int LockRead(GuestThread self, Ptr<uint> timeout)
    {
        lock (this)
        {
            ReadWaiters++;
            try
            {
                int r = Wait.Until(this, (l: this, self), static t => t.l.Writer == null || t.l.Writer == t.self, timeout, static t => t.l._deleted ? SceError.WAIT_DELETE : 0, WaitDescription);
                if (r == SceError.OK) Readers++;
                return r;
            }
            finally { ReadWaiters--; }
        }
    }

    public int TryLockRead(GuestThread self)
    {
        lock (this)
        {
            if (Writer != null && Writer != self) return SceError.RW_LOCK_FAILED_TO_LOCK;
            Readers++;
            return SceError.OK;
        }
    }

    public int UnlockRead()
    {
        lock (this)
        {
            if (Readers == 0) return SceError.RW_LOCK_UNLOCK_UDF;
            Readers--;
            Monitor.PulseAll(this);
            return SceError.OK;
        }
    }

    public int LockWrite(GuestThread self, Ptr<uint> timeout)
    {
        lock (this)
        {
            if (Writer == self) { WriteCount++; return SceError.OK; }
            WriteWaiters++;
            try
            {
                int r = Wait.Until(this, this, static l => l.Writer == null && l.Readers == 0, timeout, static l => l._deleted ? SceError.WAIT_DELETE : 0, WaitDescription);
                if (r == SceError.OK) { Writer = self; WriteCount = 1; }
                return r;
            }
            finally { WriteWaiters--; }
        }
    }

    public int TryLockWrite(GuestThread self)
    {
        lock (this)
        {
            if (Writer == self) { WriteCount++; return SceError.OK; }
            if (Writer != null || Readers > 0) return SceError.RW_LOCK_FAILED_TO_LOCK;
            Writer = self;
            WriteCount = 1;
            return SceError.OK;
        }
    }

    public int UnlockWrite(GuestThread self)
    {
        lock (this)
        {
            if (Writer != self) return SceError.RW_LOCK_NOT_OWNED;
            if (--WriteCount == 0) Writer = null;
            Monitor.PulseAll(this);
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this) { _deleted = true; Monitor.PulseAll(this); }
    }
}

public sealed class Callback : KernelObject
{
    public GuestThread Owner = null!;
    public uint Function;
    public uint Arg;
    public uint Attr;
    public int NotifyCount;
    public int NotifyArg;

    public void Notify(int arg)
    {
        lock (this)
        {
            NotifyCount++;
            NotifyArg = arg;
        }
        Interlocked.Increment(ref Owner.PendingCallbacks);
        lock (Owner.WaitLock) Monitor.PulseAll(Owner.WaitLock);
        var s = Owner.CurrentWaitSync;
        if (s != null) lock (s) Monitor.PulseAll(s);
    }
}

public sealed class TimerObject : KernelObject
{
    public uint Attr;
    public readonly GuestStopwatch Clock = new();
    public ulong BaseUs;
}

public sealed class SimpleEvent : KernelObject
{
    public uint Attr;
    public uint Pattern;
}
