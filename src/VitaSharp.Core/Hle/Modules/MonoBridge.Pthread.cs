using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// POSIX threads of SceLibMonoBridge over the kernel's threads and lightweight objects. The caller's pthread objects
/// may be as small as one word, so each holds a pointer to a block of the bridge (set up on first use, which also
/// covers the static initializers): mutex = { 'PMTX', type, SceKernelLwMutexWork (0x20, 8-aligned at +8) },
/// condition = { 'PCND', mutex block it is bound to, SceKernelLwCondWork at +8 }, attributes = { 'PATR', stack size,
/// detach state } / { 'PMAT', type }. pthread_t is the kernel thread UID. Error numbers are SceLibc's (EINVAL 22,
/// EBUSY 16, ETIMEDOUT 116, EDEADLK 45, ESRCH 3, EAGAIN 11). Mutex types: 0 normal, any other recursive.
/// </summary>
public sealed unsafe partial class MonoBridge
{
    const uint MagicMutex = 0x584D5450, MagicCond = 0x444E4350, MagicAttr = 0x52544150, MagicMutexAttr = 0x54414D50;
    const int ESRCH = 3, EDEADLK = 45, ETIMEDOUT = 116, EBUSY = 16, EAGAIN = 11, EINVAL = 22;
    const int KernelTimeout = unchecked((int)0x80028005);
    const uint DefaultStack = 0x40000;

    sealed class PThread
    {
        public uint Start, Arg, Result;
        public bool Detached, Exited;
        public readonly Stack<(uint Fn, uint Arg)> Cleanup = new();
        public readonly Dictionary<uint, uint> Specific = new();
    }

    readonly Dictionary<int, PThread> _pthreads = new();
    readonly Dictionary<uint, uint> _keyDestructors = new();
    uint _nextKey = 1;

    PThread Self()
    {
        int uid = Tm.sceKernelGetThreadId();
        lock (_pthreads)
        {
            if (!_pthreads.TryGetValue(uid, out var p)) _pthreads[uid] = p = new PThread();
            return p;
        }
    }

    // ------------------------------------------------------------------ attributes

    uint AttrBlock(uint attr)
    {
        uint b = R32(attr);
        if (b != 0 && GuestMemory.IsMapped(b) && R32(b) == MagicAttr) return b;
        b = Malloc0(12);
        W32(b, MagicAttr); W32(b + 4, DefaultStack);
        W32(attr, b);
        return b;
    }

    [Hle("pthread_attr_init", Library = Lib)]
    int PthreadAttrInit(uint attr)
    {
        if (attr == 0) return EINVAL;
        W32(attr, 0);
        AttrBlock(attr);
        return 0;
    }

    [Hle("pthread_attr_setstacksize", Library = Lib)]
    int PthreadAttrSetstacksize(uint attr, uint size)
    {
        if (attr == 0 || size < 0x1000) return EINVAL;
        W32(AttrBlock(attr) + 4, size);
        return 0;
    }

    uint MutexAttrBlock(uint attr)
    {
        uint b = R32(attr);
        if (b != 0 && GuestMemory.IsMapped(b) && R32(b) == MagicMutexAttr) return b;
        b = Malloc0(8);
        W32(b, MagicMutexAttr);
        W32(attr, b);
        return b;
    }

    [Hle("pthread_mutexattr_init", Library = Lib)]
    int PthreadMutexattrInit(uint attr) { if (attr == 0) return EINVAL; W32(attr, 0); MutexAttrBlock(attr); return 0; }

    [Hle("pthread_mutexattr_destroy", Library = Lib)]
    int PthreadMutexattrDestroy(uint attr)
    {
        if (attr == 0) return EINVAL;
        uint b = R32(attr);
        if (b != 0 && GuestMemory.IsMapped(b) && R32(b) == MagicMutexAttr) { W32(b, 0); Free(b); }
        W32(attr, 0);
        return 0;
    }

    [Hle("pthread_mutexattr_settype", Library = Lib)]
    int PthreadMutexattrSettype(uint attr, int type)
    {
        if (attr == 0 || type < 0 || type > 3) return EINVAL;
        W32(MutexAttrBlock(attr) + 4, (uint)type);
        return 0;
    }

    // ------------------------------------------------------------------ mutexes

    uint MutexOf(uint m, int type = 0)
    {
        uint b = R32(m);
        if (b != 0 && GuestMemory.IsMapped(b) && R32(b) == MagicMutex) return b;
        b = Malloc0(0x28);
        W32(b, MagicMutex); W32(b + 4, (uint)type);
        Tm.sceKernelCreateLwMutex(b + 8, "SceMonoBridgeMutex", type != 0 ? 2u : 0u, 0, 0);
        W32(m, b);
        return b;
    }

    [Hle("pthread_mutex_init", Library = Lib)]
    int PthreadMutexInit(uint m, uint attr)
    {
        if (m == 0) return EINVAL;
        int type = attr != 0 && R32(attr) is var ab and not 0 && GuestMemory.IsMapped(ab) && R32(ab) == MagicMutexAttr ? (int)R32(ab + 4) : 0;
        W32(m, 0);
        MutexOf(m, type);
        return 0;
    }

    [Hle("pthread_mutex_destroy", Library = Lib)]
    int PthreadMutexDestroy(uint m)
    {
        if (m == 0) return EINVAL;
        uint b = R32(m);
        if (b == 0 || !GuestMemory.IsMapped(b) || R32(b) != MagicMutex) return 0;
        if (Tm.sceKernelDeleteLwMutex(b + 8) < 0) return EBUSY;
        W32(b, 0);
        Free(b);
        W32(m, 0);
        return 0;
    }

    [Hle("pthread_mutex_lock", Library = Lib)]
    int PthreadMutexLock(uint m)
    {
        if (m == 0) return EINVAL;
        uint b = MutexOf(m);
        int r = Tm.sceKernelLockLwMutex(b + 8, 1, new Ptr<uint>(0));
        return r == 0 ? 0 : r == unchecked((int)0x80028184) ? EDEADLK : EINVAL;
    }

    [Hle("pthread_mutex_trylock", Library = Lib)]
    int PthreadMutexTrylock(uint m)
    {
        if (m == 0) return EINVAL;
        uint b = MutexOf(m);
        return Tm.sceKernelTryLockLwMutex(b + 8, 1) == 0 ? 0 : EBUSY;
    }

    [Hle("pthread_mutex_unlock", Library = Lib)]
    int PthreadMutexUnlock(uint m)
    {
        if (m == 0) return EINVAL;
        uint b = MutexOf(m);
        return Tm.sceKernelUnlockLwMutex(b + 8, 1) == 0 ? 0 : 1; // EPERM: not the owner
    }

    // ------------------------------------------------------------------ condition variables

    uint CondBlock(uint c)
    {
        uint b = R32(c);
        if (b != 0 && GuestMemory.IsMapped(b) && R32(b) == MagicCond) return b;
        b = Malloc0(0x28);
        W32(b, MagicCond);
        W32(c, b);
        return b;
    }

    /// <summary>The kernel condition of <paramref name="c"/>, bound to mutex block <paramref name="mb"/> on the first wait.</summary>
    uint CondFor(uint c, uint mb)
    {
        uint b = CondBlock(c);
        if (R32(b + 4) == 0)
        {
            Tm.sceKernelCreateLwCond(b + 8, "SceMonoBridgeCond", 0, mb + 8, 0);
            W32(b + 4, mb);
        }
        return b;
    }

    [Hle("pthread_cond_init", Library = Lib)]
    int PthreadCondInit(uint c, uint attr) { if (c == 0) return EINVAL; W32(c, 0); CondBlock(c); return 0; }

    [Hle("pthread_cond_destroy", Library = Lib)]
    int PthreadCondDestroy(uint c)
    {
        if (c == 0) return EINVAL;
        uint b = R32(c);
        if (b == 0 || !GuestMemory.IsMapped(b) || R32(b) != MagicCond) return 0;
        if (R32(b + 4) != 0 && Tm.sceKernelDeleteLwCond(b + 8) < 0) return EBUSY;
        W32(b, 0);
        Free(b);
        W32(c, 0);
        return 0;
    }

    [Hle("pthread_cond_signal", Library = Lib)]
    int PthreadCondSignal(uint c)
    {
        if (c == 0) return EINVAL;
        uint b = CondBlock(c);
        if (R32(b + 4) != 0) Tm.sceKernelSignalLwCond(b + 8);
        return 0;
    }

    [Hle("pthread_cond_broadcast", Library = Lib)]
    int PthreadCondBroadcast(uint c)
    {
        if (c == 0) return EINVAL;
        uint b = CondBlock(c);
        if (R32(b + 4) != 0) Tm.sceKernelSignalLwCondAll(b + 8);
        return 0;
    }

    [Hle("pthread_cond_wait", Library = Lib)]
    int PthreadCondWait(uint c, uint m)
    {
        if (c == 0 || m == 0) return EINVAL;
        uint b = CondFor(c, MutexOf(m));
        return Tm.sceKernelWaitLwCond(b + 8, new Ptr<uint>(0)) == 0 ? 0 : EINVAL;
    }

    /// <summary>pthread_cond_timedwait(cond, mutex, const timespec *abstime): the absolute time is the wall clock.</summary>
    [Hle("pthread_cond_timedwait", Library = Lib)]
    int PthreadCondTimedwait(uint c, uint m, uint abstime)
    {
        if (c == 0 || m == 0 || abstime == 0 || R32(abstime + 4) >= 1_000_000_000) return EINVAL;
        uint b = CondFor(c, MutexOf(m));
        long deadlineUs = (long)R32(abstime) * 1_000_000 + R32(abstime + 4) / 1000;
        long nowUs = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
        long rel = Math.Clamp(deadlineUs - nowUs, 0, uint.MaxValue);
        uint cell = Malloc0(4);
        W32(cell, (uint)rel);
        int r = Tm.sceKernelWaitLwCond(b + 8, new Ptr<uint>(cell));
        Free(cell);
        return r == 0 ? 0 : r == KernelTimeout ? ETIMEDOUT : EINVAL;
    }

    // ------------------------------------------------------------------ threads

    uint ThreadEntry => HostStub("pthread.entry", c =>
    {
        int uid = Tm.sceKernelGetThreadId();
        PThread p;
        lock (_pthreads) p = _pthreads[uid];
        try { p.Result = c.Thread.CallGuest(p.Start, p.Arg); }
        catch (GuestThreadExitException) { /* pthread_exit: Result already set */ }
        RunThreadExit(p);
        c.Return(0);
    });

    void RunThreadExit(PThread p)
    {
        while (p.Cleanup.Count > 0) { var (fn, arg) = p.Cleanup.Pop(); Call(fn, arg); }
        // destructors of the keys with a value (up to 4 rounds, as POSIX allows)
        for (int round = 0; round < 4; round++)
        {
            bool any = false;
            foreach (var (key, value) in p.Specific.ToArray())
            {
                if (value == 0) continue;
                uint dtor;
                lock (_keyDestructors) dtor = _keyDestructors.GetValueOrDefault(key);
                p.Specific[key] = 0;
                if (dtor != 0) { Call(dtor, value); any = true; }
            }
            if (!any) break;
        }
        p.Exited = true;
    }

    /// <summary>pthread_create(thread*, attr, start, arg): a kernel thread at the default user priority running
    /// start(arg); 0 or EAGAIN.</summary>
    [Hle("pthread_create", Library = Lib)]
    int PthreadCreate(uint thread, uint attr, uint start, uint arg)
    {
        if (thread == 0 || start == 0) return EINVAL;
        uint stack = DefaultStack;
        bool detached = false;
        if (attr != 0 && R32(attr) is var ab and not 0 && GuestMemory.IsMapped(ab) && R32(ab) == MagicAttr)
        {
            stack = R32(ab + 4);
            detached = R32(ab + 8) != 0;
        }
        int uid = Tm.sceKernelCreateThread("SceMonoBridgePthread", ThreadEntry, 0x10000100, (int)stack, 0, 0, 0);
        if (uid < 0) return EAGAIN;
        lock (_pthreads) _pthreads[uid] = new PThread { Start = start, Arg = arg, Detached = detached };
        if (Tm.sceKernelStartThread(uid, 0, 0) < 0)
        {
            lock (_pthreads) _pthreads.Remove(uid);
            Tm.sceKernelDeleteThread(uid);
            return EAGAIN;
        }
        W32(thread, (uint)uid);
        return 0;
    }

    /// <summary>pthread_join(thread, retval*): waits for the end, hands over start's result and frees the thread.</summary>
    [Hle("pthread_join", Library = Lib)]
    int PthreadJoin(int thread, uint retval)
    {
        if (thread == Tm.sceKernelGetThreadId()) return EDEADLK;
        PThread? p;
        lock (_pthreads) _pthreads.TryGetValue(thread, out p);
        if (p == null) return ESRCH;
        if (p.Detached) return EINVAL;
        int r = Tm.sceKernelWaitThreadEnd(thread, new Ptr<int>(0), new Ptr<uint>(0));
        if (r < 0 && !p.Exited) return ESRCH;
        if (retval != 0) W32(retval, p.Result);
        lock (_pthreads) _pthreads.Remove(thread);
        Tm.sceKernelDeleteThread(thread);
        return 0;
    }

    [Hle("pthread_detach", Library = Lib)]
    int PthreadDetach(int thread)
    {
        lock (_pthreads)
        {
            if (!_pthreads.TryGetValue(thread, out var p)) return ESRCH;
            if (p.Detached) return EINVAL;
            p.Detached = true;
        }
        return 0;
    }

    /// <summary>pthread_exit(value): runs the cleanup handlers and key destructors and ends the thread.</summary>
    [Hle("pthread_exit", Library = Lib)]
    void PthreadExit(uint value)
    {
        var p = Self();
        p.Result = value;
        throw new GuestThreadExitException(0, false);
    }

    [Hle("pthread_self", Library = Lib)] int PthreadSelf() { Self(); return Tm.sceKernelGetThreadId(); }
    [Hle("pthread_equal", Library = Lib)] bool PthreadEqual(uint a, uint b) => a == b;

    /// <summary>pthread_cleanup_push_(routine, arg) / pthread_cleanup_pop_(execute): the calling thread's handler stack.</summary>
    [Hle("pthread_cleanup_push_", Library = Lib)]
    void PthreadCleanupPush(uint routine, uint arg) { if (routine != 0) Self().Cleanup.Push((routine, arg)); }

    [Hle("pthread_cleanup_pop_", Library = Lib)]
    void PthreadCleanupPop(int execute)
    {
        var p = Self();
        if (p.Cleanup.Count == 0) return;
        var (fn, arg) = p.Cleanup.Pop();
        if (execute != 0) Call(fn, arg);
    }

    // ------------------------------------------------------------------ thread-specific data (pthread keys and the
    // "fast TLS" slots, the same store)

    [Hle("pthread_key_create", Library = Lib)]
    [Hle("pthread_vita_tls_create_np", Library = Lib)]
    int PthreadKeyCreate(uint key, uint destructor)
    {
        if (key == 0) return EINVAL;
        lock (_keyDestructors)
        {
            uint k = _nextKey++;
            _keyDestructors[k] = destructor;
            W32(key, k);
        }
        return 0;
    }

    [Hle("pthread_key_delete", Library = Lib)]
    int PthreadKeyDelete(uint key)
    {
        lock (_keyDestructors) if (!_keyDestructors.Remove(key)) return EINVAL;
        lock (_pthreads) foreach (var p in _pthreads.Values) p.Specific.Remove(key);
        return 0;
    }

    [Hle("pthread_setspecific", Library = Lib)]
    [Hle("pthread_vita_tls_set_np", Library = Lib)]
    int PthreadSetspecific(uint key, uint value)
    {
        lock (_keyDestructors) if (!_keyDestructors.ContainsKey(key)) return EINVAL;
        Self().Specific[key] = value;
        return 0;
    }

    [Hle("pthread_getspecific", Library = Lib)]
    [Hle("pthread_vita_tls_get_np", Library = Lib)]
    uint PthreadGetspecific(uint key) => Self().Specific.GetValueOrDefault(key);

    /// <summary>pthread_getspecific_for_thread(thread, key): another thread's value (the runtime's debugger and GC).</summary>
    [Hle("pthread_getspecific_for_thread", Library = Lib)]
    uint PthreadGetspecificForThread(int thread, uint key)
    {
        lock (_pthreads) return _pthreads.TryGetValue(thread, out var p) ? p.Specific.GetValueOrDefault(key) : 0;
    }
}
