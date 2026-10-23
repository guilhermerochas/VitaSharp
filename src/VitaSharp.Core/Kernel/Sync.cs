using VitaSharp.Core.Util;
using System.Diagnostics;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Kernel;

/// <summary>Blocking-wait helper with Vita semantics: timeout in microseconds, remaining time written back.</summary>
public static class Wait
{
    /// <summary>Wake-up latency of a blocking syscall on the Vita (kernel entry, scheduler, context switch on a 333 MHz
    /// Cortex-A9): tens of microseconds. Host wake-ups take ~1 us, which makes job systems that measure their own
    /// synchronisation-heavy work (physics steps with per-step time budgets) behave differently; 0 disables.
    /// VITASHARP_SYSCALL_LATENCY_US overrides it (timing bisection).</summary>
    public static int SyscallLatencyUs = int.TryParse(Environment.GetEnvironmentVariable("VITASHARP_SYSCALL_LATENCY_US"), out var lat) ? lat : 30;

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
        // the delay starts where the thread's emulated CPU time is, and it resumes at the deadline however late the
        // host wakes it (CpuCore.WokeAt): pacing sleeps and timer wake-ups overshoot by fractions of a millisecond
        long end = (self?.Core.BlockedAt ?? GuestClock.Ticks) + GuestClock.FromSeconds(microseconds / 1e6 / TimeScale.Wait), slice = GuestClock.FromSeconds(0.05);
        while (GuestClock.Ticks < end)
        {
            if (self != null && self.Core.StopRequested) throw new GuestThreadExitException(0, false);
            // sleep in slices so a stop request is noticed; short delays really sleep instead of spinning a host core
            GuestClock.WaitUntil(Math.Min(end, GuestClock.Ticks + slice));
        }
        self?.Core.WokeAt(end);
    }
}

public sealed class Semaphore : KernelObject
{
    public override int UidClass => 2;
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
    public override int UidClass => 3;
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
    public override int UidClass => IsLight ? 10 : 4;
    public const uint AttrRecursive = 0x02;
    public uint Attr;
    public int InitCount;
    public GuestThread? Owner;
    public int LockCount;
    public int Waiters;
    public uint WorkArea; // lightweight mutex work area (0 for normal mutexes)
    bool _deleted;
    int _cancelled;

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
            int gen = _cancelled;
            try
            {
                int r = Wait.Until(this, (m: this, gen), static t => t.m.Owner == null || t.m.Owner.Status == ThreadStatus.Dormant, timeout,
                    static t => t.m._deleted ? SceError.WAIT_DELETE : t.m._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
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

    /// <summary>sceKernelCancelMutex: wakes every waiter with WAIT_CANCEL and resets the lock count (-1 = initCount);
    /// a count above 0 hands the mutex to the caller.</summary>
    public int Cancel(GuestThread? self, int newCount, Ptr<int> numWait)
    {
        lock (this)
        {
            if (newCount == -1) newCount = InitCount;
            if (newCount < 0 || (newCount > 1 && (Attr & AttrRecursive) == 0)) return SceError.ILLEGAL_COUNT;
            numWait.TrySet(Waiters);
            _cancelled++;
            LockCount = newCount;
            Owner = newCount > 0 ? self : null;
            UpdateWorkArea();
            Monitor.PulseAll(this);
            return SceError.OK;
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

/// <summary>Condition variable bound to a mutex (SceKernelCond and lightweight conds). Waiters queue in arrival order;
/// a signal releases the first one, SignalTo a specific thread, SignalAll every waiter.</summary>
public sealed class CondVar : KernelObject
{
    public override int UidClass => WorkArea != 0 ? 11 : 5;
    public uint Attr;
    public Mutex Mutex = null!;
    public uint WorkArea;
    public int Waiters;
    bool _deleted;

    sealed class Waiter
    {
        public GuestThread Thread = null!;
        public bool Released;
    }

    readonly LinkedList<Waiter> _queue = new();
    /// <summary>Queue nodes of finished waits, reused by later waits (guarded by this object's lock).</summary>
    readonly Stack<LinkedListNode<Waiter>> _freeNodes = new();

    public int WaitCond(GuestThread self, Ptr<uint> timeout)
    {
        int count;
        lock (this)
        {
            count = Mutex.ReleaseAll(self);
            if (count < 0) return Mutex.IsLight ? SceError.LW_MUTEX_NOT_OWNED : SceError.MUTEX_NOT_OWNED;
            Waiters++;
            var node = _freeNodes.Count > 0 ? _freeNodes.Pop() : new LinkedListNode<Waiter>(new Waiter());
            var w = node.Value;
            w.Thread = self;
            w.Released = false;
            _queue.AddLast(node);
            int r;
            try
            {
                r = Wait.Until(this, (c: this, w), static t => t.w.Released, timeout,
                    static t => t.c._deleted ? SceError.WAIT_DELETE : 0, WaitDescription);
            }
            finally
            {
                Waiters--;
                if (!w.Released) _queue.Remove(node);
                w.Thread = null!;
                if (_freeNodes.Count < 64) _freeNodes.Push(node);
            }
            // reacquire outside our lock to avoid lock-order inversion with the mutex
            Monitor.Exit(this);
            try { Mutex.Lock(self, count, default); }
            finally { Monitor.Enter(this); }
            return r;
        }
    }

    void Release(LinkedListNode<Waiter> node)
    {
        node.Value.Released = true;
        _queue.Remove(node);
    }

    public void Signal()
    {
        lock (this)
        {
            if (_queue.First is { } first) Release(first);
            Monitor.PulseAll(this);
        }
    }

    /// <summary>Releases <paramref name="target"/> if it waits on this condition; false if it does not.</summary>
    public bool SignalTo(GuestThread target)
    {
        lock (this)
        {
            for (var n = _queue.First; n != null; n = n.Next)
            {
                if (n.Value.Thread != target) continue;
                Release(n);
                Monitor.PulseAll(this);
                return true;
            }
            return false;
        }
    }

    public void SignalAll()
    {
        lock (this)
        {
            while (_queue.First is { } first) Release(first);
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
    public override int UidClass => 7;
    public const uint WaitAll = 0, WaitAsap = 1;
    public uint Attr;
    public int BufferSize;
    /// <summary>UID of the thread that created the pipe (sceKernelGetMsgPipeCreatorId).</summary>
    public int CreatorThreadUid;
    readonly Queue<byte> _data = new();
    public int SendWaiters, RecvWaiters;
    public int FreeSize { get { lock (this) return BufferSize - _data.Count; } }
    bool _deleted;
    int _cancelled;

    /// <summary>Guest buffers taken as one contiguous byte range in order (MsgPipeVector entries {base, size}).</summary>
    public readonly struct Segments(uint[] addrs, int[] sizes)
    {
        public readonly uint[] Addrs = addrs;
        public readonly int[] Sizes = sizes;
        public int Total => Sizes.Sum();
        public static Segments One(uint addr, int size) => new([addr], [size]);

        /// <summary>Visits the guest spans covering [offset, offset + count).</summary>
        public void ForEach(int offset, int count, Action<uint, int> f)
        {
            for (int i = 0; i < Addrs.Length && count > 0; i++)
            {
                if (offset >= Sizes[i]) { offset -= Sizes[i]; continue; }
                int n = Math.Min(Sizes[i] - offset, count);
                f(Addrs[i] + (uint)offset, n);
                count -= n;
                offset = 0;
            }
        }
    }

    public int Send(uint src, int size, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
        => Send(Segments.One(src, size), mode, result, timeout, poll);

    public int Send(Segments src, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
    {
        int size = src.Total;
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
                        src.ForEach(sent, n, (a, len) => { foreach (var b in GuestMemory.Span(a, len)) _data.Enqueue(b); });
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
        => Receive(Segments.One(dst, size), mode, result, timeout, poll);

    public int Receive(Segments dst, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool poll)
    {
        int size = dst.Total;
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
                        dst.ForEach(got, n, (a, len) => { var span = GuestMemory.Span(a, len); for (int i = 0; i < len; i++) span[i] = _data.Dequeue(); });
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
    public override int UidClass => 12;
    public uint Attr;
    public int Readers;
    public GuestThread? Writer;
    public int WriteCount;
    public int ReadWaiters, WriteWaiters;
    bool _deleted;
    int _cancelled;

    public int LockRead(GuestThread self, Ptr<uint> timeout)
    {
        lock (this)
        {
            ReadWaiters++;
            int gen = _cancelled;
            try
            {
                int r = Wait.Until(this, (l: this, self, gen), static t => t.l.Writer == null || t.l.Writer == t.self, timeout,
                    static t => t.l._deleted ? SceError.WAIT_DELETE : t.l._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
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
            int gen = _cancelled;
            try
            {
                int r = Wait.Until(this, (l: this, gen), static t => t.l.Writer == null && t.l.Readers == 0, timeout,
                    static t => t.l._deleted ? SceError.WAIT_DELETE : t.l._cancelled != t.gen ? SceError.WAIT_CANCEL : 0, WaitDescription);
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

    /// <summary>Wakes every waiter with WAIT_CANCEL and resets the lock; with <paramref name="keepWriteLock"/> the caller
    /// ends up holding it for writing (SCE_KERNEL_RW_LOCK_CANCEL_WITH_WRITE_LOCK).</summary>
    public int Cancel(GuestThread self, Ptr<uint> numRead, Ptr<uint> numWrite, bool keepWriteLock)
    {
        lock (this)
        {
            numRead.TrySet((uint)ReadWaiters);
            numWrite.TrySet((uint)WriteWaiters);
            _cancelled++;
            Readers = 0;
            Writer = keepWriteLock ? self : null;
            WriteCount = keepWriteLock ? 1 : 0;
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
    public override int UidClass => 8;
    public GuestThread Owner = null!;
    public uint Function;
    public uint Arg;
    public uint Attr;
    public int NotifyCount;
    public int NotifyArg;
    /// <summary>UID of the thread that sent the last notification.</summary>
    public int NotifyId;

    public void Notify(int arg)
    {
        lock (this)
        {
            NotifyCount++;
            NotifyArg = arg;
            NotifyId = GuestThread.Current?.Uid ?? 0;
        }
        Interlocked.Increment(ref Owner.PendingCallbacks);
        lock (Owner.WaitLock) Monitor.PulseAll(Owner.WaitLock);
        var s = Owner.CurrentWaitSync;
        if (s != null) lock (s) Monitor.PulseAll(s);
    }
}

/// <summary>Event bits shared by simple events and timers (psp2kern/kernel/threadmgr.h).</summary>
public static class EventBits
{
    public const uint Create = 0x4, Delete = 0x8, Error = 0x10, Timer = 0x8000, UserMask = 0xFF000000;
    public const uint AttrAutoReset = 0x100, AttrOpenable = 0x80, AttrThPrio = 0x2000;
}

/// <summary>
/// Kernel timer: a microsecond counter on process time plus an optional (repeating) SET or PULSE event that
/// sceKernelWaitEvent/PollEvent can wait for (result EVENT_TIMER). Only the first waiter waits for the event.
/// </summary>
public sealed class TimerObject : KernelObject
{
    public override int UidClass => 6;
    public uint Attr;
    /// <summary>Process time in microseconds (sceKernelGetProcessTime base).</summary>
    public required Func<ulong> Clock;
    public bool Started, Repeat, Pulse, EventSet;
    public ulong Base, StoppedCount, Interval;
    public ulong NextEvent = ulong.MaxValue;
    readonly LinkedList<object> _waiters = new();
    bool _deleted;
    int _cancelled;

    public int NumWaiters { get { lock (this) return _waiters.Count; } }

    public ulong Count() { lock (this) return Started ? Clock() - Base : StoppedCount; }

    /// <summary>Returns 1 when it was already running, else 0.</summary>
    public int Start()
    {
        lock (this)
        {
            if (Started) return 1;
            Started = true;
            Base = Clock() - StoppedCount;
            if (Interval != 0) Schedule();
            return 0;
        }
    }

    /// <summary>Returns 1 when it stopped a running timer, 0 when it was already stopped.</summary>
    public int Stop()
    {
        lock (this)
        {
            if (!Started) return 0;
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
            if (Started) Base = now - value;
            else StoppedCount = value;
            return old;
        }
    }

    void Schedule()
    {
        NextEvent = Clock() + Interval;
        Monitor.PulseAll(this);
    }

    public int SetEvent(int type, ulong interval, bool repeat)
    {
        if (type is not (0 or 1)) return SceError.ILLEGAL_TYPE;
        lock (this)
        {
            Pulse = type == 1;
            Repeat = repeat;
            Interval = interval;
            if (Started) Schedule();
            return SceError.OK;
        }
    }

    void Advance(ulong now)
    {
        if (Repeat && Interval != 0) NextEvent += ((now - NextEvent - 1) / Interval + 1) * Interval;
        else NextEvent = ulong.MaxValue;
    }

    int TryTake(Ptr<uint> result, Ptr<ulong> userData, ulong now)
    {
        result.TrySet(0);
        userData.TrySet(0);
        if (NextEvent < now)
        {
            if (!Pulse) EventSet = true; // a pulse is only seen by waiters
            Advance(now);
        }
        if (!EventSet) return SceError.EVENT_COND;
        result.TrySet(EventBits.Timer);
        if ((Attr & EventBits.AttrAutoReset) != 0) EventSet = false;
        return SceError.OK;
    }

    public int Poll(Ptr<uint> result, Ptr<ulong> userData) { lock (this) return TryTake(result, userData, Clock()); }

    public int Clear() { lock (this) EventSet = false; return SceError.OK; }

    bool Fired(LinkedListNode<object> node, ulong now) => _waiters.First == node && (EventSet || now > NextEvent);

    public int Wait(Ptr<uint> result, Ptr<ulong> userData, Ptr<uint> timeout)
    {
        lock (this)
        {
            ulong now = Clock();
            int r = TryTake(result, userData, now);
            if (r != SceError.EVENT_COND) return r;
            var node = _waiters.AddLast(new object());
            int gen = _cancelled;
            ulong start = now;
            long? limit = timeout.IsNull ? null : timeout.Value;
            try
            {
                while (true)
                {
                    if (_deleted) { WriteBack(timeout, limit, start); return SceError.WAIT_DELETE; }
                    if (_cancelled != gen) { WriteBack(timeout, limit, start); return SceError.WAIT_CANCEL; }
                    if (Fired(node, now)) break;
                    long? slice = limit.HasValue ? limit.Value - (long)(now - start) : null;
                    if (slice is <= 0) { timeout.TrySet(0); return SceError.WAIT_TIMEOUT; }
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
                Monitor.PulseAll(this);
            }
        }
    }

    void WriteBack(Ptr<uint> timeout, long? limit, ulong start)
    {
        if (limit.HasValue) timeout.TrySet((uint)Math.Max(0, limit.Value - (long)(Clock() - start)));
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
            Monitor.PulseAll(this);
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this) { _deleted = true; Monitor.PulseAll(this); }
    }
}

/// <summary>
/// Simple event (sceKernelCreateSimpleEvent): a 32-bit pattern plus the last user data. Set wakes, in queue order,
/// every waiter whose pattern matches (consuming the bits with AUTO_RESET); Pulse does the same and then restores the
/// previous pattern, so only threads already waiting see it.
/// </summary>
public sealed class SimpleEvent : KernelObject
{
    public override int UidClass => 13;
    public uint Attr;
    public uint Pattern;
    public ulong LastUserData;
    bool AutoReset => (Attr & EventBits.AttrAutoReset) != 0;

    sealed class Waiter
    {
        public uint WaitPattern;
        public Ptr<uint> Result;
        public Ptr<ulong> UserData;
        public int Priority;
        public int? Code;
    }

    readonly LinkedList<Waiter> _waiters = new();
    public int NumWaiters { get { lock (this) return _waiters.Count; } }

    int TryTake(uint waitPattern, Ptr<uint> result, Ptr<ulong> userData)
    {
        if ((Pattern & waitPattern) == 0) { result.TrySet(0); return SceError.EVENT_COND; }
        result.TrySet(Pattern & waitPattern);
        if (AutoReset) Pattern &= ~waitPattern;
        userData.TrySet(LastUserData);
        return SceError.OK;
    }

    public int Poll(uint waitPattern, Ptr<uint> result, Ptr<ulong> userData)
    {
        if (waitPattern == 0) return SceError.INVALID_ARGUMENT;
        lock (this) return TryTake(waitPattern, result, userData);
    }

    public int Wait(GuestThread? self, uint waitPattern, Ptr<uint> result, Ptr<ulong> userData, Ptr<uint> timeout)
    {
        if (waitPattern == 0) return SceError.INVALID_ARGUMENT;
        lock (this)
        {
            int r = TryTake(waitPattern, result, userData);
            if (r != SceError.EVENT_COND) return r;
            var w = new Waiter { WaitPattern = waitPattern, Result = result, UserData = userData, Priority = self?.Priority ?? 0 };
            LinkedListNode<Waiter> node;
            if ((Attr & EventBits.AttrThPrio) != 0)
            {
                var at = _waiters.First;
                while (at != null && at.Value.Priority <= w.Priority) at = at.Next;
                node = at == null ? _waiters.AddLast(w) : _waiters.AddBefore(at, w);
            }
            else node = _waiters.AddLast(w);
            r = global::VitaSharp.Core.Kernel.Wait.Until(this, w, static w => w.Code.HasValue, timeout, null, WaitDescription);
            if (w.Code is { } code) r = code;
            else _waiters.Remove(node);
            if (r < 0 && r != SceError.WAIT_TIMEOUT)
            {
                result.TrySet(EventBits.Error);
                userData.TrySet(LastUserData);
            }
            return r;
        }
    }

    public int SetOrPulse(uint setPattern, ulong userData, bool pulse)
    {
        if ((setPattern & ~EventBits.UserMask) != 0) return SceError.ILLEGAL_PATTERN;
        lock (this)
        {
            uint oldPattern = Pattern;
            ulong oldData = LastUserData;
            Pattern |= setPattern;
            LastUserData = userData;
            for (var n = _waiters.First; n != null;)
            {
                var next = n.Next;
                var w = n.Value;
                if ((Pattern & w.WaitPattern) != 0)
                {
                    w.Result.TrySet(Pattern & w.WaitPattern);
                    w.UserData.TrySet(LastUserData);
                    if (AutoReset) Pattern &= ~w.WaitPattern;
                    w.Code = SceError.OK;
                    _waiters.Remove(n);
                }
                n = next;
            }
            if (pulse) { Pattern = oldPattern & ~setPattern; LastUserData = oldData; }
            Monitor.PulseAll(this);
            return SceError.OK;
        }
    }

    /// <summary>Keeps only the bits of <paramref name="keepPattern"/> (Vita3K: only user bits may be passed).</summary>
    public int Clear(uint keepPattern)
    {
        if ((keepPattern & ~EventBits.UserMask) != 0) return SceError.ILLEGAL_PATTERN;
        lock (this) Pattern &= keepPattern;
        return SceError.OK;
    }

    public int Cancel(Ptr<uint> numWait)
    {
        lock (this)
        {
            numWait.TrySet((uint)_waiters.Count);
            foreach (var w in _waiters) w.Code = SceError.WAIT_CANCEL;
            _waiters.Clear();
            Monitor.PulseAll(this);
            return SceError.OK;
        }
    }

    public void Delete()
    {
        lock (this)
        {
            // waits that include the DELETE bit end successfully with it, the others with WAIT_DELETE
            foreach (var w in _waiters)
            {
                if ((w.WaitPattern & EventBits.Delete) != 0)
                {
                    w.Result.TrySet((Pattern | EventBits.Delete) & w.WaitPattern);
                    w.UserData.TrySet(LastUserData);
                    w.Code = SceError.OK;
                }
                else w.Code = SceError.WAIT_DELETE;
            }
            _waiters.Clear();
            Monitor.PulseAll(this);
        }
    }
}

/// <summary>Handler registered by sceKernelRegisterThreadEventHandler (thread start/exit notifications).</summary>
public sealed class ThreadEventHandler : KernelObject
{
    public override int UidClass => 9;
    public int ThreadMask;
    public uint EventMask, Handler, Common;
}
