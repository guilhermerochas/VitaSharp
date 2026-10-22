using System.Runtime.CompilerServices;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceUlt fallback: user-level threads and their synchronisation objects, for a title that imports SceUlt without
/// shipping libult.suprx. Every title seen so far ships it (the loaded module's exports replace these bindings), so
/// this only runs when the LLE library is missing.
///
/// Ulthreads become guest threads (no multiplexing over the runtime's worker threads: blocking, join, yield and exit
/// stay observable the same way). Objects are game memory and opaque: like libult the name goes to +0 (31 chars +
/// NUL); this module adds a tag at +0x20 and the object's UID at +0x24 and keeps the state on the host.
///
/// Checked against the FW 3.60 libult.suprx: error codes 0x80810001.. (NULL 1, ALIGNMENT 2, RANGE 3, INVALID 4,
/// PERMISSION 5, STATE 6, BUSY 7, AGAIN 8), objects and parameters 8-byte aligned, OptParam blocks of 0x80 bytes
/// (three magic words, the build version at +0xC, reserved bytes from +0x10 that must stay zero), Info blocks of 0x80
/// (+0 size, which must be 0x80, +4 object, +8 name), the work area size formulas and the argument order of the Create
/// functions (a trailing build version argument is ignored). Approximate: Info fields past the name are left zero, the
/// mutex recursion attribute is read from OptParam +0x10 bit 0 (inferred), and which error each misuse returns follows
/// the libult paths that were inspected (STATE for relocking a non-recursive mutex, BUSY/AGAIN for the Try forms).
/// </summary>
public sealed class Ult : HleModule
{
    const int ERR_NULL = unchecked((int)0x80810001), ERR_ALIGNMENT = unchecked((int)0x80810002),
        ERR_RANGE = unchecked((int)0x80810003), ERR_INVALID = unchecked((int)0x80810004),
        ERR_PERMISSION = unchecked((int)0x80810005), ERR_STATE = unchecked((int)0x80810006),
        ERR_BUSY = unchecked((int)0x80810007), ERR_AGAIN = unchecked((int)0x80810008);

    const uint OptSize = 0x80, InfoSize = 0x80;
    const uint OptMagic0 = 0x6C696275, OptMagic1 = 0x6C746D61, OptMagic2 = 0x67696300;
    const uint Tag = 0x48544C55, DeadTag = 0x44544C55; // "ULTH" live, "ULTD" destroyed
    const uint TagOffset = 0x20;

    abstract class UltObject : KernelObject
    {
        public uint Address;
        public bool Destroyed;
        public readonly object Lock = new();
        /// <summary>Objects created on top of this one (pools) or waiting in it; Destroy is BUSY while non-zero.</summary>
        public int Users;
    }

    sealed class WaitingPool : UltObject { public uint NumThreads, NumSync; }
    sealed class DataPool : UltObject { public uint NumData, DataSize, NumQueues, UsedData; public WaitingPool? Waiting; }
    sealed class UQueue : UltObject { public uint DataSize; public DataPool Pool = null!; public WaitingPool Waiting = null!; public readonly Queue<byte[]> Items = new(); }
    sealed class UMutex : UltObject { public GuestThread? Owner; public int Count; public bool Recursive; public WaitingPool Waiting = null!; }
    sealed class UCond : UltObject { public long Sequence; public UMutex Mutex = null!; }
    sealed class USema : UltObject { public int Count; public WaitingPool Waiting = null!; }
    sealed class URwLock : UltObject { public int Readers; public GuestThread? Writer; public WaitingPool Waiting = null!; }
    sealed class Runtime : UltObject { public uint MaxUlthreads, Workers; public int Live; }
    sealed class Ulthread : UltObject { public GuestThread Thread = null!; public Runtime Runtime = null!; }

    readonly ConditionalWeakTable<GuestThread, Ulthread> _byThread = new();

    // ------------------------------------------------------------------ helpers

    static int CheckPtr(uint p) => p == 0 ? ERR_NULL : (p & 7) != 0 ? ERR_ALIGNMENT : 0;

    /// <summary>An optional OptParam: aligned and initialised by its OptParamInitialize. The resource pools have no
    /// attributes and also require the bytes from +0x10 to be zero (<paramref name="reserved"/>).</summary>
    static int CheckOpt(uint opt, bool reserved = false)
    {
        if (opt == 0) return 0;
        if ((opt & 7) != 0) return ERR_ALIGNMENT;
        if (GuestMemory.R32(opt) != OptMagic0 || GuestMemory.R32(opt + 4) != OptMagic1 || GuestMemory.R32(opt + 8) != OptMagic2) return ERR_INVALID;
        return reserved && GuestMemory.ReadSpan(opt + 0x10, (int)(OptSize - 0x10)).IndexOfAnyExcept((byte)0) >= 0 ? ERR_INVALID : 0;
    }

    static int InitOpt(uint opt, uint buildVersion)
    {
        int r = CheckPtr(opt);
        if (r < 0) return r;
        GuestMemory.Fill(opt, 0, OptSize);
        GuestMemory.W32(opt, OptMagic0);
        GuestMemory.W32(opt + 4, OptMagic1);
        GuestMemory.W32(opt + 8, OptMagic2);
        GuestMemory.W32(opt + 0xC, buildVersion);
        return 0;
    }

    int Publish(UltObject o, uint addr, string name)
    {
        o.Address = addr;
        int uid = K.Objects.Register(o);
        GuestMemory.Fill(addr, 0, TagOffset + 8);
        GuestMemory.WriteCString(addr, name, 0x20);
        GuestMemory.W32(addr + TagOffset, Tag);
        GuestMemory.W32(addr + TagOffset + 4, (uint)uid);
        return 0;
    }

    T? Find<T>(uint addr, out int err) where T : UltObject
    {
        err = CheckPtr(addr);
        if (err < 0) return null;
        uint tag = GuestMemory.R32(addr + TagOffset);
        var o = tag == Tag ? K.Objects.Get<T>((int)GuestMemory.R32(addr + TagOffset + 4)) : null;
        if (o == null || o.Destroyed || o.Address != addr)
        {
            err = tag == DeadTag || o is { Destroyed: true } ? ERR_STATE : ERR_INVALID; // libult: a destroyed object is STATE
            return null;
        }
        return o;
    }

    int Destroy(UltObject o, UltObject? parent = null, Action? release = null)
    {
        lock (o.Lock)
        {
            if (o.Users > 0) return ERR_STATE;
            o.Destroyed = true;
            Monitor.PulseAll(o.Lock);
        }
        release?.Invoke();
        if (parent != null) Interlocked.Decrement(ref parent.Users);
        K.Objects.Remove(o.Uid);
        GuestMemory.W32(o.Address + TagOffset, DeadTag);
        return 0;
    }

    /// <summary>Info blocks: +0 size (must be 0x80), +4 object, +8 name; the remaining fields are not modelled.</summary>
    static int WriteInfo(UltObject o, uint info)
    {
        int r = CheckPtr(info);
        if (r < 0) return r;
        if (GuestMemory.R32(info) != InfoSize) return ERR_INVALID;
        GuestMemory.Fill(info, 0, InfoSize);
        GuestMemory.W32(info, InfoSize);
        GuestMemory.W32(info + 4, o.Address);
        GuestMemory.WriteCString(info + 8, o.Name, 0x20);
        return 0;
    }

    GuestThread Self => GuestThread.Current!;

    static string ReadName(uint name) => GuestMemory.ReadCString(name, 0x1F);

    /// <summary>Blocks until <paramref name="ready"/> holds; a destroyed object ends the wait with STATE.</summary>
    static int WaitFor(UltObject o, Func<bool> ready, string what)
        => Kernel.Wait.Until(o.Lock, ready, default, () => o.Destroyed ? ERR_STATE : 0, what);

    // ------------------------------------------------------------------ resource pools

    [Hle] int _sceUltWaitingQueueResourcePoolOptParamInitialize(uint opt, uint buildVersion) => InitOpt(opt, buildVersion);
    [Hle] int _sceUltQueueDataResourcePoolOptParamInitialize(uint opt, uint buildVersion) => InitOpt(opt, buildVersion);
    [Hle] int _sceUltQueueOptParamInitialize(uint opt, uint buildVersion) => InitOpt(opt, buildVersion);
    [Hle] int _sceUltMutexOptParamInitialize(uint opt, uint buildVersion) => InitOpt(opt, buildVersion);
    [Hle] int _sceUltConditionVariableOptParamInitialize(uint opt, uint buildVersion) => InitOpt(opt, buildVersion);
    [Hle] int _sceUltSemaphoreOptParamInitialize(uint opt, uint buildVersion) => InitOpt(opt, buildVersion);
    [Hle] int _sceUltReaderWriterLockOptParamInitialize(uint opt, uint buildVersion) => InitOpt(opt, buildVersion);
    [Hle] int _sceUltUlthreadRuntimeOptParamInitialize(uint opt, uint buildVersion) => InitOpt(opt, buildVersion);
    [Hle] int _sceUltUlthreadOptParamInitialize(uint opt, uint buildVersion) => InitOpt(opt, buildVersion);

    [Hle] uint sceUltWaitingQueueResourcePoolGetWorkAreaSize(uint numThreads, uint numSyncObjects) => (numThreads + numSyncObjects) * 32 + 0x40;

    [Hle]
    int _sceUltWaitingQueueResourcePoolCreate(uint pool, uint name, uint numThreads, uint numSyncObjects, uint workArea, uint opt)
    {
        int r = CheckPtr(pool);
        if (r < 0) return r;
        if (name == 0) return ERR_NULL;
        if ((r = CheckOpt(opt, true)) < 0) return r;
        if (numThreads == 0 || numSyncObjects == 0) return ERR_INVALID;
        if ((r = CheckPtr(workArea)) < 0) return r;
        return Publish(new WaitingPool { Name = ReadName(name), NumThreads = numThreads, NumSync = numSyncObjects }, pool, ReadName(name));
    }

    [Hle] int sceUltWaitingQueueResourcePoolDestroy(uint pool) => Find<WaitingPool>(pool, out int e) is { } p ? Destroy(p) : e;
    [Hle] int sceUltGetWaitingQueueResourcePoolInfo(uint pool, uint info) => Find<WaitingPool>(pool, out int e) is { } p ? WriteInfo(p, info) : e;

    [Hle] uint sceUltQueueDataResourcePoolGetWorkAreaSize(uint numData, uint dataSize, uint numQueueObjects)
        => numData * dataSize + (numData + numQueueObjects) * 32 + 0x40;

    [Hle]
    int _sceUltQueueDataResourcePoolCreate(uint pool, uint name, uint numData, uint dataSize, uint numQueueObjects, uint waitingPool, uint workArea, uint opt)
    {
        int r = CheckPtr(pool);
        if (r < 0) return r;
        if (name == 0) return ERR_NULL;
        if ((r = CheckOpt(opt, true)) < 0) return r;
        if (numData == 0 || dataSize == 0 || numQueueObjects == 0) return ERR_INVALID;
        if (Find<WaitingPool>(waitingPool, out r) is not { } wq) return r;
        if ((r = CheckPtr(workArea)) < 0) return r;
        Interlocked.Increment(ref wq.Users);
        return Publish(new DataPool { Name = ReadName(name), NumData = numData, DataSize = dataSize, NumQueues = numQueueObjects, Waiting = wq }, pool, ReadName(name));
    }

    [Hle] int sceUltQueueDataResourcePoolDestroy(uint pool) => Find<DataPool>(pool, out int e) is { } p ? Destroy(p, p.Waiting) : e;
    [Hle] int sceUltGetQueueDataResourcePoolInfo(uint pool, uint info) => Find<DataPool>(pool, out int e) is { } p ? WriteInfo(p, info) : e;

    // ------------------------------------------------------------------ queues

    [Hle]
    int _sceUltQueueCreate(uint queue, uint name, uint dataSize, uint waitingPool, uint dataPool, uint opt)
    {
        int r = CheckPtr(queue);
        if (r < 0) return r;
        if (name == 0) return ERR_NULL;
        if ((r = CheckOpt(opt)) < 0) return r;
        if (Find<WaitingPool>(waitingPool, out r) is not { } wq) return r;
        if (Find<DataPool>(dataPool, out r) is not { } dp) return r;
        if (dataSize == 0 || dataSize > dp.DataSize) return ERR_INVALID;
        Interlocked.Increment(ref wq.Users);
        Interlocked.Increment(ref dp.Users);
        return Publish(new UQueue { Name = ReadName(name), DataSize = dataSize, Pool = dp, Waiting = wq }, queue, ReadName(name));
    }

    [Hle]
    int sceUltQueueDestroy(uint queue)
    {
        if (Find<UQueue>(queue, out int e) is not { } q) return e;
        int r = Destroy(q, q.Waiting, () =>
        {
            lock (q.Pool.Lock) { q.Pool.UsedData -= (uint)q.Items.Count; Monitor.PulseAll(q.Pool.Lock); }
        });
        if (r == 0) Interlocked.Decrement(ref q.Pool.Users);
        return r;
    }

    // The data pool's elements are shared by its queues: a push needs a free element of the pool.
    int Push(uint queue, uint data, bool wait)
    {
        if (Find<UQueue>(queue, out int e) is not { } q) return e;
        if (data == 0) return ERR_NULL;
        byte[] item = GuestMemory.ReadSpan(data, (int)q.DataSize).ToArray();
        var pool = q.Pool;
        lock (pool.Lock)
        {
            if (pool.UsedData >= pool.NumData)
            {
                if (!wait) return ERR_AGAIN;
                int r = Kernel.Wait.Until(pool.Lock, () => pool.UsedData < pool.NumData || q.Destroyed, default, null, q.WaitDescription);
                if (r < 0) return r;
                if (q.Destroyed) return ERR_STATE;
            }
            pool.UsedData++;
        }
        lock (q.Lock) { q.Items.Enqueue(item); Monitor.PulseAll(q.Lock); }
        return 0;
    }

    int Pop(uint queue, uint data, bool wait)
    {
        if (Find<UQueue>(queue, out int e) is not { } q) return e;
        if (data == 0) return ERR_NULL;
        byte[] item;
        lock (q.Lock)
        {
            if (q.Items.Count == 0)
            {
                if (!wait) return ERR_AGAIN;
                int r = WaitFor(q, () => q.Items.Count > 0, q.WaitDescription);
                if (r < 0) return r;
            }
            item = q.Items.Dequeue();
        }
        item.CopyTo(GuestMemory.Span(data, item.Length));
        lock (q.Pool.Lock) { q.Pool.UsedData--; Monitor.PulseAll(q.Pool.Lock); }
        return 0;
    }

    [Hle] int sceUltQueuePush(uint queue, uint data) => Push(queue, data, true);
    [Hle] int sceUltQueueTryPush(uint queue, uint data) => Push(queue, data, false);
    [Hle] int sceUltQueuePop(uint queue, uint data) => Pop(queue, data, true);
    [Hle] int sceUltQueueTryPop(uint queue, uint data) => Pop(queue, data, false);
    [Hle] int sceUltGetQueueInfo(uint queue, uint info) => Find<UQueue>(queue, out int e) is { } q ? WriteInfo(q, info) : e;

    // ------------------------------------------------------------------ mutexes

    [Hle]
    int _sceUltMutexCreate(uint mutex, uint name, uint waitingPool, uint opt)
    {
        int r = CheckPtr(mutex);
        if (r < 0) return r;
        if (name == 0) return ERR_NULL;
        if ((r = CheckOpt(opt)) < 0) return r;
        if (Find<WaitingPool>(waitingPool, out r) is not { } wq) return r;
        bool recursive = opt != 0 && (GuestMemory.R32(opt + 0x10) & 1) != 0;
        Interlocked.Increment(ref wq.Users);
        return Publish(new UMutex { Name = ReadName(name), Recursive = recursive, Waiting = wq }, mutex, ReadName(name));
    }

    [Hle] int sceUltMutexDestroy(uint mutex) => Find<UMutex>(mutex, out int e) is { } m ? DestroyMutex(m) : e;

    int DestroyMutex(UMutex m)
    {
        lock (m.Lock) if (m.Owner != null) return ERR_STATE;
        return Destroy(m, m.Waiting);
    }

    int Lock(uint mutex, bool wait)
    {
        if (Find<UMutex>(mutex, out int e) is not { } m) return e;
        var self = Self;
        lock (m.Lock)
        {
            if (m.Owner == self)
            {
                if (!m.Recursive) return ERR_STATE;
                m.Count++;
                return 0;
            }
            if (m.Owner != null)
            {
                if (!wait) return ERR_AGAIN;
                int r = WaitFor(m, () => m.Owner == null, m.WaitDescription);
                if (r < 0) return r;
            }
            m.Owner = self;
            m.Count = 1;
        }
        return 0;
    }

    [Hle] int sceUltMutexLock(uint mutex) => Lock(mutex, true);
    [Hle] int sceUltMutexTryLock(uint mutex) => Lock(mutex, false);

    [Hle]
    int sceUltMutexUnlock(uint mutex)
    {
        if (Find<UMutex>(mutex, out int e) is not { } m) return e;
        lock (m.Lock)
        {
            if (m.Owner == null || m.Owner != Self) return ERR_PERMISSION;
            if (--m.Count == 0) { m.Owner = null; Monitor.PulseAll(m.Lock); }
        }
        return 0;
    }

    [Hle] int sceUltGetMutexInfo(uint mutex, uint info) => Find<UMutex>(mutex, out int e) is { } m ? WriteInfo(m, info) : e;

    // ------------------------------------------------------------------ condition variables

    [Hle]
    int _sceUltConditionVariableCreate(uint cv, uint name, uint mutex, uint opt)
    {
        int r = CheckPtr(cv);
        if (r < 0) return r;
        if (name == 0 || mutex == 0) return ERR_NULL;
        if ((r = CheckOpt(opt)) < 0) return r;
        if (Find<UMutex>(mutex, out r) is not { } m) return r;
        return Publish(new UCond { Name = ReadName(name), Mutex = m }, cv, ReadName(name));
    }

    [Hle] int sceUltConditionVariableDestroy(uint cv) => Find<UCond>(cv, out int e) is { } c ? Destroy(c) : e;

    /// <summary>The mutex is bound at creation (libult: Create(cv, name, mutex, opt), Wait(cv)). Wait releases it (all
    /// recursion levels), waits for a signal, then takes it back; the caller must own it (else PERMISSION).</summary>
    [Hle]
    int sceUltConditionVariableWait(uint cv)
    {
        if (Find<UCond>(cv, out int e) is not { } c) return e;
        var m = c.Mutex;
        var self = Self;
        int depth;
        lock (m.Lock)
        {
            if (m.Owner == null || m.Owner != self) return ERR_PERMISSION;
            depth = m.Count;
        }
        lock (c.Lock)
        {
            long seq = c.Sequence;
            Interlocked.Increment(ref c.Users);
            lock (m.Lock) { m.Owner = null; m.Count = 0; Monitor.PulseAll(m.Lock); }
            int r = WaitFor(c, () => c.Sequence != seq, c.WaitDescription);
            Interlocked.Decrement(ref c.Users);
            if (r < 0 && r != ERR_STATE) return r;
        }
        lock (m.Lock)
        {
            if (m.Owner != null)
            {
                int r = WaitFor(m, () => m.Owner == null, m.WaitDescription);
                if (r < 0) return r;
            }
            m.Owner = self;
            m.Count = depth;
        }
        return 0;
    }

    int Signal(uint cv)
    {
        if (Find<UCond>(cv, out int e) is not { } c) return e;
        lock (c.Lock) { c.Sequence++; Monitor.PulseAll(c.Lock); }
        return 0;
    }

    // a signal wakes every waiter here (spurious wakeups are allowed by condition variable semantics)
    [Hle] int sceUltConditionVariableSignal(uint cv) => Signal(cv);
    [Hle] int sceUltConditionVariableSignalAll(uint cv) => Signal(cv);
    [Hle] int sceUltGetConditionVariableInfo(uint cv, uint info) => Find<UCond>(cv, out int e) is { } c ? WriteInfo(c, info) : e;

    // ------------------------------------------------------------------ semaphores

    [Hle]
    int _sceUltSemaphoreCreate(uint sema, uint name, int numInitialResource, uint waitingPool, uint opt)
    {
        int r = CheckPtr(sema);
        if (r < 0) return r;
        if (name == 0) return ERR_NULL;
        if ((r = CheckOpt(opt)) < 0) return r;
        if (numInitialResource < 0) return ERR_RANGE;
        if (Find<WaitingPool>(waitingPool, out r) is not { } wq) return r;
        Interlocked.Increment(ref wq.Users);
        return Publish(new USema { Name = ReadName(name), Count = numInitialResource, Waiting = wq }, sema, ReadName(name));
    }

    [Hle] int sceUltSemaphoreDestroy(uint sema) => Find<USema>(sema, out int e) is { } s ? Destroy(s, s.Waiting) : e;

    int Acquire(uint sema, int n, bool wait)
    {
        if (Find<USema>(sema, out int e) is not { } s) return e;
        if (n <= 0) return ERR_RANGE;
        lock (s.Lock)
        {
            if (s.Count < n)
            {
                if (!wait) return ERR_AGAIN;
                int r = WaitFor(s, () => s.Count >= n, s.WaitDescription);
                if (r < 0) return r;
            }
            s.Count -= n;
        }
        return 0;
    }

    [Hle] int sceUltSemaphoreAcquire(uint sema, int numResource) => Acquire(sema, numResource, true);
    [Hle] int sceUltSemaphoreTryAcquire(uint sema, int numResource) => Acquire(sema, numResource, false);

    [Hle]
    int sceUltSemaphoreRelease(uint sema, int numResource)
    {
        if (Find<USema>(sema, out int e) is not { } s) return e;
        if (numResource <= 0) return ERR_RANGE;
        lock (s.Lock) { s.Count += numResource; Monitor.PulseAll(s.Lock); }
        return 0;
    }

    [Hle] int sceUltGetSemaphoreInfo(uint sema, uint info) => Find<USema>(sema, out int e) is { } s ? WriteInfo(s, info) : e;

    // ------------------------------------------------------------------ reader/writer locks

    [Hle]
    int _sceUltReaderWriterLockCreate(uint rw, uint name, uint waitingPool, uint opt)
    {
        int r = CheckPtr(rw);
        if (r < 0) return r;
        if (name == 0) return ERR_NULL;
        if ((r = CheckOpt(opt)) < 0) return r;
        if (Find<WaitingPool>(waitingPool, out r) is not { } wq) return r;
        Interlocked.Increment(ref wq.Users);
        return Publish(new URwLock { Name = ReadName(name), Waiting = wq }, rw, ReadName(name));
    }

    [Hle]
    int sceUltReaderWriterLockDestroy(uint rw)
    {
        if (Find<URwLock>(rw, out int e) is not { } l) return e;
        // like libult, a held lock does not prevent destruction (waiters wake with STATE)
        return Destroy(l, l.Waiting);
    }

    int RwLock(uint rw, bool write, bool wait)
    {
        if (Find<URwLock>(rw, out int e) is not { } l) return e;
        var self = Self;
        lock (l.Lock)
        {
            if (l.Writer == self) return ERR_STATE;
            bool Free() => write ? l.Writer == null && l.Readers == 0 : l.Writer == null;
            if (!Free())
            {
                if (!wait) return ERR_AGAIN;
                int r = WaitFor(l, Free, l.WaitDescription);
                if (r < 0) return r;
            }
            if (write) l.Writer = self;
            else l.Readers++;
        }
        return 0;
    }

    [Hle] int sceUltReaderWriterLockLockRead(uint rw) => RwLock(rw, false, true);
    [Hle] int sceUltReaderWriterLockLockWrite(uint rw) => RwLock(rw, true, true);
    [Hle] int sceUltReaderWriterLockTryLockRead(uint rw) => RwLock(rw, false, false);
    [Hle] int sceUltReaderWriterLockTryLockWrite(uint rw) => RwLock(rw, true, false);

    [Hle]
    int sceUltReaderWriterLockUnlockRead(uint rw)
    {
        if (Find<URwLock>(rw, out int e) is not { } l) return e;
        lock (l.Lock)
        {
            if (l.Readers == 0) return ERR_STATE;
            if (--l.Readers == 0) Monitor.PulseAll(l.Lock);
        }
        return 0;
    }

    [Hle]
    int sceUltReaderWriterLockUnlockWrite(uint rw)
    {
        if (Find<URwLock>(rw, out int e) is not { } l) return e;
        lock (l.Lock)
        {
            if (l.Writer == null) return ERR_STATE; // libult does not check which thread holds the write lock
            l.Writer = null;
            Monitor.PulseAll(l.Lock);
        }
        return 0;
    }

    [Hle] int sceUltGetReaderWriterLockInfo(uint rw, uint info) => Find<URwLock>(rw, out int e) is { } l ? WriteInfo(l, info) : e;

    // ------------------------------------------------------------------ ulthreads

    /// <summary>libult's formula: per worker and per ulthread control blocks plus the runtime header.</summary>
    [Hle]
    uint sceUltUlthreadRuntimeGetWorkAreaSize(uint numMaxUlthread, uint numWorkerThread)
    {
        uint n = numMaxUlthread, w = numWorkerThread;
        uint a = ((n + w + 1) * 32 + (n + w) * 4 + 0x47) & ~7u;
        uint b = ((w * 32 + 0x87) & ~7u) + w * 256;
        uint c = (n * 36 + 0x67) & ~7u;
        return a + b + c + n * 4;
    }

    [Hle]
    int _sceUltUlthreadRuntimeCreate(uint runtime, uint name, uint numMaxUlthread, uint numWorkerThread, uint workArea, uint opt)
    {
        int r = CheckPtr(runtime);
        if (r < 0) return r;
        if (name == 0) return ERR_NULL;
        if ((r = CheckOpt(opt)) < 0) return r;
        if (numMaxUlthread == 0 || numWorkerThread == 0) return ERR_INVALID;
        if ((r = CheckPtr(workArea)) < 0) return r;
        return Publish(new Runtime { Name = ReadName(name), MaxUlthreads = numMaxUlthread, Workers = numWorkerThread }, runtime, ReadName(name));
    }

    [Hle]
    int sceUltUlthreadRuntimeDestroy(uint runtime)
    {
        if (Find<Runtime>(runtime, out int e) is not { } rt) return e;
        if (Volatile.Read(ref rt.Live) > 0) return ERR_STATE;
        return Destroy(rt);
    }

    [Hle] int sceUltGetUlthreadRuntimeInfo(uint runtime, uint info) => Find<Runtime>(runtime, out int e) is { } rt ? WriteInfo(rt, info) : e;

    /// <summary>Starts <c>entry(arg)</c> at once on a guest thread with the creator's priority and affinity; the game's
    /// context buffer is not used as the stack (the thread gets its own, at least as large).</summary>
    [Hle]
    int _sceUltUlthreadCreate(uint ulthread, uint name, uint entry, uint arg, uint context, uint sizeContext, uint runtime, uint opt)
    {
        int r = CheckPtr(ulthread);
        if (r < 0) return r;
        if (name == 0 || entry == 0) return ERR_NULL;
        if ((r = CheckOpt(opt)) < 0) return r;
        if ((r = CheckPtr(context)) < 0) return r;
        if (Find<Runtime>(runtime, out r) is not { } rt) return r;
        if (Interlocked.Increment(ref rt.Live) > rt.MaxUlthreads) { Interlocked.Decrement(ref rt.Live); return ERR_AGAIN; }
        var self = Self;
        string n = ReadName(name);
        var t = K.CreateThread(n, entry, self.Priority, Math.Max(sizeContext, 0x4000u), 0, self.Affinity);
        var ut = new Ulthread { Name = n, Thread = t, Runtime = rt };
        Publish(ut, ulthread, n);
        _byThread.AddOrUpdate(t, ut);
        t.Start(arg, 0); // r0 = arg
        return 0;
    }

    Ulthread? Current => GuestThread.Current is { } t && _byThread.TryGetValue(t, out var ut) ? ut : null;

    [Hle]
    int sceUltUlthreadYield()
    {
        if (Current == null) return ERR_PERMISSION;
        Kernel.Wait.Sleep(0);
        return 0;
    }

    [Hle]
    int sceUltUlthreadExit(int status)
    {
        if (Current == null) return ERR_PERMISSION;
        throw new GuestThreadExitException(status, false);
    }

    int Join(uint ulthread, Ptr<int> status, bool wait)
    {
        if (Find<Ulthread>(ulthread, out int e) is not { } ut) return e;
        if (ut.Thread == GuestThread.Current) return ERR_STATE;
        var t = ut.Thread;
        lock (t.WaitLock)
        {
            if (t.Status != ThreadStatus.Dormant)
            {
                if (!wait) return ERR_AGAIN;
                int r = Kernel.Wait.Until(t.WaitLock, () => t.Status == ThreadStatus.Dormant, default, null, $"ulthread join {t.Name}");
                if (r < 0) return r;
            }
        }
        status.TrySet(t.ExitStatus);
        _byThread.Remove(t);
        K.DeleteThread(t);
        Interlocked.Decrement(ref ut.Runtime.Live);
        return Destroy(ut);
    }

    [Hle] int sceUltUlthreadJoin(uint ulthread, Ptr<int> status) => Join(ulthread, status, true);
    [Hle] int sceUltUlthreadTryJoin(uint ulthread, Ptr<int> status) => Join(ulthread, status, false);

    /// <summary>Writes the calling ulthread's object (NULL outside an ulthread, as libult) and returns 0.</summary>
    [Hle]
    int sceUltUlthreadGetSelf(Ptr<uint> ulthread)
    {
        if (ulthread.IsNull) return ERR_NULL;
        ulthread.Value = Current?.Address ?? 0;
        return 0;
    }

    [Hle] int sceUltGetUlthreadInfo(uint ulthread, uint info) => Find<Ulthread>(ulthread, out int e) is { } ut ? WriteInfo(ut, info) : e;
}
