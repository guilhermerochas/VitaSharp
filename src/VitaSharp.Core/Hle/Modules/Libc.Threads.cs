using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceLibstdcxx's C11-style threads of libc.suprx 3.74 (Dinkumware xthreads) over the kernel's lightweight objects,
// called the way the module calls them (SceLibKernel/SceThreadmgr):
//   _Mtx_t points to a calloc'ed 0x30-byte block: +0 the type (_Mtx_recursive 0x100 makes the lwmutex recursive),
//   +8 an lwmutex "SceLibc", +0x28 the lock count, +0x2C the owner thread id.
//   _Cnd_t points to 0x30 bytes: +0 an lwcond "SceLibc" over the lwmutex at +0x10 (a wait releases the caller's
//   mutex state with _Save_state, waits on the condition under that inner lwmutex, then restores it).
//   Results: 0 success, 1 nomem, 2 timedout, 3 busy, 4 error. xtime: { seconds, nanoseconds } against
//   sceKernelLibcGettimeofday, waits rounded up to microseconds.
// _Thrd_start/_Thrd_create make "SceLibc" threads (priority 0xBF, 4 KiB stack, any core). std::_Pad starts a thread
// that runs its virtual _Go and waits until _Release; the thread-exit notifications keep (thread, mutex, condition,
// flag) entries. Also: the generic error category, _Throw_C_error/_Throw_Cpp_error (std::system_error) and the
// Dinkum::threads lock_error/thread_resource_error.
public sealed partial class Libc
{
    const int ThrdSuccess = 0, ThrdNomem = 1, ThrdTimedout = 2, ThrdBusy = 3, ThrdError = 4;
    const uint KErrTimeout = 0x80028005, KErrBusy = 0x80028185;

    readonly Dictionary<(uint, string), uint> _libStubs = new();

    /// <summary>A guest-callable stub of a function of another library (the module's import).</summary>
    uint LibStub(uint nid, string lib)
    {
        lock (_rttiLock)
        {
            if (_libStubs.TryGetValue((nid, lib), out uint a)) return a;
            return _libStubs[(nid, lib)] = NewStub(Emu.Hle.GetHleFunctionIndex(nid, 0, lib), nid);
        }
    }

    static GuestThread Me => GuestThread.Current ?? throw new InvalidOperationException("SceLibstdcxx: needs a guest thread");

    int KCall(uint nid, string lib, params ReadOnlySpan<uint> args) => (int)Me.CallGuest(LibStub(nid, lib), args);

    int CreateLwMutex(uint work, uint attr) => KCall(0xDA6EC8EF, "SceLibKernel", work, ConstString("SceLibc"), attr, 0, 0);
    int DeleteLwMutex(uint work) => KCall(0x244E76D2, "SceLibKernel", work);
    int LockLwMutex(uint work, uint timeout) => KCall(0x46E7BE7B, "SceLibKernel", work, 1, timeout);
    int TryLockLwMutex(uint work) => KCall(0xA6A2C915, "SceLibKernel", work, 1);
    int UnlockLwMutex(uint work) => KCall(0x91FA6614, "SceLibKernel", work, 1);
    int CreateLwCond(uint work, uint mutex) => KCall(0x48C7EAE6, "SceLibKernel", work, ConstString("SceLibc"), 0x2000, mutex, 0);
    int DeleteLwCond(uint work) => KCall(0x721F6CB3, "SceLibKernel", work);
    int WaitLwCond(uint work, uint timeout) => KCall(0xE1878282, "SceLibKernel", work, timeout);
    int SignalLwCond(uint work) => KCall(0x3AC63B9A, "SceLibKernel", work);
    int SignalLwCondAll(uint work) => KCall(0xE5241A0C, "SceLibKernel", work);
    int ThreadId() => KCall(0x0FB972F9, "SceLibKernel");

    /// <summary>A 4-byte guest cell for a timeout or result, freed by the caller.</summary>
    uint XCell(uint value)
    {
        uint c = HeapAlloc(8, HeapAlign);
        if (c == 0) throw new InvalidOperationException("SceLibstdcxx: no memory");
        GuestMemory.W32(c, value);
        return c;
    }

    // ------------------------------------------------------------------ xtime

    /// <summary>xtime_get(xt, base): TIME_UTC (1) only: the time of day as seconds and nanoseconds; returns base, 0 for
    /// another base or no xt.</summary>
    [Hle]
    int xtime_get(uint xt, int @base)
    {
        if (@base != 1 || xt == 0) return 0;
        long us = NowUs();
        GuestMemory.W32(xt, (uint)(us / 1_000_000));
        GuestMemory.W32(xt + 4, (uint)(us % 1_000_000 * 1000));
        return 1;
    }

    /// <summary>The time of day in microseconds from sceKernelLibcGettimeofday, as the module reads it.</summary>
    long NowUs()
    {
        uint tv = HeapAlloc(0x10, HeapAlign);
        if (tv == 0) return 0;
        try
        {
            KCall(0x4B879059, "SceProcessmgr", tv, tv + 8);
            return (long)(int)GuestMemory.R32(tv) * 1_000_000 + (int)GuestMemory.R32(tv + 4);
        }
        finally { free(tv); }
    }

    /// <summary>The microseconds from now until the xtime (0 when it has passed), nanoseconds rounded up.</summary>
    uint XtimeToUsec(uint xt)
    {
        long sec = (int)GuestMemory.R32(xt), nsec = (int)GuestMemory.R32(xt + 4);
        sec += Math.DivRem(nsec, 1_000_000_000L, out nsec);
        if (nsec < 0) { nsec += 1_000_000_000L; sec--; }
        long nowUs = NowUs();
        long dSec = sec - nowUs / 1_000_000, dNs = nsec - nowUs % 1_000_000 * 1000;
        if (dNs < 0) { dSec--; dNs += 1_000_000_000L; }
        if (dSec < 0 || (dSec == 0 && dNs <= 0)) return 0;
        long us = dSec * 1_000_000 + (dNs + 999) / 1000;
        return (uint)Math.Min(us, uint.MaxValue);
    }

    // ------------------------------------------------------------------ _Mtx

    /// <summary>_Mtx_init(mtx, type): the block and its lwmutex (recursive for _Mtx_recursive).</summary>
    [Hle]
    int _Mtx_init(uint mtx, uint type)
    {
        GuestMemory.W32(mtx, 0);
        uint p = calloc(1, 0x30);
        if (p == 0) return ThrdNomem;
        if (CreateLwMutex(p + 8, ((type & 0x100) != 0 ? 2u : 0u) | 0x2000) != 0) { free(p); return ThrdError; }
        GuestMemory.W32(p, type);
        GuestMemory.W32(mtx, p);
        return ThrdSuccess;
    }

    [Hle]
    void _Mtx_destroy(uint mtx)
    {
        if (mtx == 0 || GuestMemory.R32(mtx) is not (var p and not 0)) return;
        DeleteLwMutex(p + 8);
        free(p);
        GuestMemory.W32(mtx, 0);
    }

    void MtxTaken(uint p)
    {
        GuestMemory.W32(p + 0x28, GuestMemory.R32(p + 0x28) + 1);
        GuestMemory.W32(p + 0x2C, (uint)ThreadId());
    }

    [Hle]
    int _Mtx_lock(uint mtx)
    {
        uint p = GuestMemory.R32(mtx);
        if (LockLwMutex(p + 8, 0) != 0) return ThrdError;
        MtxTaken(p);
        return ThrdSuccess;
    }

    [Hle]
    int _Mtx_trylock(uint mtx)
    {
        uint p = GuestMemory.R32(mtx);
        int r = TryLockLwMutex(p + 8);
        if (r == 0) { MtxTaken(p); return ThrdSuccess; }
        return (uint)r == KErrBusy ? ThrdBusy : ThrdError;
    }

    [Hle]
    int _Mtx_timedlock(uint mtx, uint xt)
    {
        uint p = GuestMemory.R32(mtx), t = XCell(XtimeToUsec(xt));
        try
        {
            int r = LockLwMutex(p + 8, t);
            if (r == 0) { MtxTaken(p); return ThrdSuccess; }
            return (uint)r == KErrTimeout ? ThrdTimedout : ThrdError;
        }
        finally { free(t); }
    }

    [Hle]
    int _Mtx_unlock(uint mtx)
    {
        uint p = GuestMemory.R32(mtx);
        GuestMemory.W32(p + 0x28, GuestMemory.R32(p + 0x28) - 1);
        return UnlockLwMutex(p + 8) == 0 ? ThrdSuccess : ThrdError;
    }

    /// <summary>_Mtx_current_owns: locked by the calling thread.</summary>
    [Hle]
    int _Mtx_current_owns(uint mtx)
    {
        uint p = GuestMemory.R32(mtx);
        return GuestMemory.R32(p + 0x28) != 0 && GuestMemory.R32(p + 0x2C) == (uint)ThreadId() ? 1 : 0;
    }

    /// <summary>_Save_state(mtx, state): the count and owner into state, then the mutex is released.</summary>
    [Hle]
    int _Save_state(uint mtx, uint state)
    {
        uint p = GuestMemory.R32(mtx);
        GuestMemory.W32(state, GuestMemory.R32(p + 0x28));
        GuestMemory.W32(state + 4, GuestMemory.R32(p + 0x2C));
        GuestMemory.W32(p + 0x28, 0);
        return UnlockLwMutex(p + 8) == 0 ? ThrdSuccess : ThrdError;
    }

    /// <summary>_Restore_state(mtx, state): the mutex is taken again with the saved count and owner.</summary>
    [Hle]
    int _Restore_state(uint mtx, uint state)
    {
        uint p = GuestMemory.R32(mtx);
        if (LockLwMutex(p + 8, 0) == 0) MtxTaken(p);
        GuestMemory.W32(p + 0x28, GuestMemory.R32(state));
        GuestMemory.W32(p + 0x2C, GuestMemory.R32(state + 4));
        return ThrdSuccess;
    }

    // ------------------------------------------------------------------ _Cnd

    [Hle]
    int _Cnd_init(uint cnd)
    {
        uint p = calloc(1, 0x30);
        if (p == 0) return ThrdNomem;
        if (CreateLwMutex(p + 0x10, 0x2000) != 0) { free(p); return ThrdError; }
        if (CreateLwCond(p, p + 0x10) != 0) { DeleteLwMutex(p + 0x10); free(p); return ThrdError; }
        GuestMemory.W32(cnd, p);
        return ThrdSuccess;
    }

    [Hle]
    void _Cnd_destroy(uint cnd)
    {
        if (cnd == 0 || GuestMemory.R32(cnd) is not (var p and not 0)) return;
        DeleteLwCond(p);
        DeleteLwMutex(p + 0x10);
        free(p);
        GuestMemory.W32(cnd, 0);
    }

    int CndWait(uint cnd, uint mtx, uint timeout)
    {
        uint st = HeapAlloc(8, HeapAlign);
        try
        {
            if (_Save_state(mtx, st) != 0) return ThrdError;
            uint p = GuestMemory.R32(cnd);
            if (LockLwMutex(p + 0x10, 0) != 0) return ThrdError;
            int r = WaitLwCond(p, timeout);
            if (UnlockLwMutex(GuestMemory.R32(cnd) + 0x10) != 0) return ThrdError;
            if (_Restore_state(mtx, st) != 0) return ThrdError;
            if (timeout != 0 && (uint)r == KErrTimeout) return ThrdTimedout;
            return r != 0 ? ThrdError : ThrdSuccess;
        }
        finally { free(st); }
    }

    [Hle] int _Cnd_wait(uint cnd, uint mtx) => CndWait(cnd, mtx, 0);

    [Hle]
    int _Cnd_timedwait(uint cnd, uint mtx, uint xt)
    {
        uint t = XCell(XtimeToUsec(xt));
        try { return CndWait(cnd, mtx, t); }
        finally { free(t); }
    }

    [Hle] int _Cnd_signal(uint cnd) => SignalLwCond(GuestMemory.R32(cnd)) == 0 ? ThrdSuccess : ThrdError;
    [Hle] int _Cnd_broadcast(uint cnd) => SignalLwCondAll(GuestMemory.R32(cnd)) == 0 ? ThrdSuccess : ThrdError;

    // the at-thread-exit notifications: (thread, mutex, condition, flag), under the module's lock
    readonly List<(int Thread, uint Mtx, uint Cnd, uint Res)> _atThreadExit = new();

    /// <summary>_Cnd_register_at_thread_exit(cnd, mtx, res): at the calling thread's exit (or
    /// _Cnd_do_broadcast_at_thread_exit) *res = 1, mtx is unlocked and cnd broadcast.</summary>
    [Hle]
    int _Cnd_register_at_thread_exit(uint cnd, uint mtx, uint res)
    {
        int me = ThreadId();
        lock (_atThreadExit) _atThreadExit.Add((me, mtx, cnd, res));
        return ThrdSuccess;
    }

    [Hle]
    int _Cnd_unregister_at_thread_exit(uint mtx)
    {
        lock (_atThreadExit) _atThreadExit.RemoveAll(e => e.Mtx == mtx);
        return ThrdSuccess;
    }

    [Hle]
    int _Cnd_do_broadcast_at_thread_exit()
    {
        int me = ThreadId();
        List<(int Thread, uint Mtx, uint Cnd, uint Res)> mine;
        lock (_atThreadExit)
        {
            mine = _atThreadExit.Where(e => e.Thread == me).ToList();
            _atThreadExit.RemoveAll(e => e.Thread == me);
        }
        foreach (var e in mine)
        {
            if (e.Res != 0) GuestMemory.W32(e.Res, 1);
            _Mtx_unlock(e.Mtx);
            _Cnd_broadcast(e.Cnd);
        }
        return ThrdSuccess;
    }

    // ------------------------------------------------------------------ _Thrd

    /// <summary>_Thrd_start(thr, func, arg): a "SceLibc" thread running func with arg (copied to its stack).</summary>
    [Hle]
    int _Thrd_start(uint thr, uint func, uint arg)
    {
        int uid = KCall(0xC5C11EE7, "SceLibKernel", ConstString("SceLibc"), func, 0xBF, 0x1000, 0, 0x70000, 0);
        GuestMemory.W32(thr, (uint)uid);
        if (uid < 0) return ThrdError;
        uint a = XCell(arg);
        try { return KCall(0xF08DE149, "SceLibKernel", (uint)uid, 4, a) == 0 ? ThrdSuccess : ThrdError; }
        finally { free(a); }
    }

    /// <summary>_Thrd_create(thr, func, arg): as in the module, the thread starts at an entry that only returns 0 while
    /// the caller waits for a start flag that entry never sets (a caller that gets the thread started waits forever).</summary>
    [Hle]
    int _Thrd_create(uint thr, uint func, uint arg)
    {
        uint started = XCell(0), cnd = XCell(0), mtx = XCell(0);
        try
        {
            _Cnd_init(cnd);
            _Mtx_init(mtx, 1);
            _Mtx_lock(mtx);
            int uid = KCall(0xC5C11EE7, "SceLibKernel", ConstString("SceLibc"), HostStub("_Thrd_create entry", c => c.Return(0)), 0xBF,
                0x1000, 0, 0x70000, 0);
            GuestMemory.W32(thr, (uint)uid);
            int r = ThrdError;
            if (uid >= 0)
            {
                uint a = XCell(started);
                r = KCall(0xF08DE149, "SceLibKernel", (uint)uid, 4, a) == 0 ? ThrdSuccess : ThrdError;
                free(a);
                if (r == ThrdSuccess)
                    while (GuestMemory.R32(started) == 0) _Cnd_wait(cnd, mtx);
            }
            _Mtx_unlock(mtx);
            _Cnd_destroy(cnd);
            _Mtx_destroy(mtx);
            return r;
        }
        finally { free(started); free(cnd); free(mtx); }
    }

    /// <summary>_Thrd_join(thr, res): waits for the end, stores the exit status, deletes the thread.</summary>
    [Hle]
    int _Thrd_join(uint thr, uint res)
    {
        uint st = XCell(0);
        try
        {
            if (KCall(0xDDB395A9, "SceLibKernel", thr, st, 0) != 0) return ThrdError;
            if (res != 0) GuestMemory.W32(res, GuestMemory.R32(st));
            return KCall(0x1BBDE3D9, "SceThreadmgr", thr) == 0 ? ThrdSuccess : ThrdError;
        }
        finally { free(st); }
    }

    [Hle] int _Thrd_detach(uint thr) => KCall(0x1BBDE3D9, "SceThreadmgr", thr) == 0 ? ThrdSuccess : ThrdError;
    [Hle] void _Thrd_exit(int res) => KCall(0x0C8A38E1, "SceThreadmgrCoredumpTime", (uint)res);
    [Hle] void _Thrd_sleep(uint xt) => KCall(0x4B675D05, "SceThreadmgr", XtimeToUsec(xt));
    [Hle] void _Thrd_yield() => KCall(0x4B675D05, "SceThreadmgr", 0);
    [Hle] int _Thrd_equal(int a, int b) => a == b ? 1 : 0;
    [Hle] int _Thrd_lt(int a, int b) => a < b ? 1 : 0;
    [Hle] int _Thrd_current() => ThreadId();

    /// <summary>_Thrd_abort(msg): the message and a newline on stderr, then abort.</summary>
    [Hle]
    void _Thrd_abort(uint msg)
    {
        uint err = StderrFile().Addr;
        fputs(msg, err);
        fputc('\n', err);
        abort();
    }

    // ------------------------------------------------------------------ std::_Pad

    /// <summary>_Pad(): its vtable (one pure virtual _Go), the condition and mutex, the start flag; the mutex is held
    /// until _Release.</summary>
    [Hle("_ZNSt4_PadC1Ev")]
    [Hle("_ZNSt4_PadC2Ev")]
    uint PadCtor(HleContext ctx, uint pad)
    {
        GuestMemory.W32(pad, PadVtable() + 8);
        int r = _Cnd_init(pad + 4);
        if (r != 0) { ThrowCError(ctx, r); return pad; }
        r = _Mtx_init(pad + 8, 1);
        if (r != 0) { ThrowCError(ctx, r); return pad; }
        GuestMemory.Write(pad + 0xC, (byte)0);
        GuestMemory.W32(pad + 0x10, pad + 0xC);
        r = _Mtx_lock(pad + 8);
        if (r != 0) ThrowCError(ctx, r);
        return pad;
    }

    uint _padVtable;

    /// <summary>_Pad's vtable { 0, typeinfo "St4_Pad" (an si_class_type_info without base... the module's class has
    /// none), __cxa_pure_virtual }.</summary>
    uint PadVtable()
    {
        lock (_rttiLock)
        {
            if (_padVtable != 0) return _padVtable;
            uint ti = StaticAlloc(8, 4), vt = StaticAlloc(12, 4);
            GuestMemory.W32(ti, Rtti.Vptr(0xF79AE8E1)); // __class_type_info
            GuestMemory.W32(ti + 4, ConstString("St4_Pad"));
            GuestMemory.W32(vt + 4, ti);
            GuestMemory.W32(vt + 8, HostStub("_Pad::_Go (pure)", _ => DebugMessage("pure virtual function call", null, 0)));
            return _padVtable = vt;
        }
    }

    [Hle("_ZNSt4_PadD1Ev")]
    [Hle("_ZNSt4_PadD2Ev")]
    uint PadDtor(HleContext ctx, uint pad)
    {
        GuestMemory.W32(pad, PadVtable() + 8);
        int r = _Mtx_unlock(pad + 8);
        if (r != 0) ThrowCError(ctx, r);
        _Mtx_destroy(pad + 8);
        _Cnd_destroy(pad + 4);
        return pad;
    }

    /// <summary>_Launch(thr): a thread runs _Go (then the at-thread-exit broadcasts); the caller waits for _Release.</summary>
    [Hle("_ZNSt4_Pad7_LaunchEPij")]
    void PadLaunch(HleContext ctx, uint pad, uint thr)
    {
        GuestMemory.W32(pad + 0x10, pad + 0xC);
        uint entry = HostStub("_Pad thread", c =>
        {
            uint self = GuestMemory.R32(c.Gpr(1));
            uint r = VCall(self, 0);
            _Cnd_do_broadcast_at_thread_exit();
            c.Return(r);
        });
        int s = _Thrd_start(thr, entry, pad);
        if (s != 0) { ThrowCError(ctx, s == ThrdError ? 1 : s); return; }
        while (B(pad + 0xC) == 0)
        {
            int r = _Cnd_wait(pad + 4, pad + 8);
            if (r != 0) { ThrowCError(ctx, r); return; }
        }
    }

    /// <summary>_Release: sets the start flag and wakes the launcher.</summary>
    [Hle("_ZNSt4_Pad8_ReleaseEv")]
    void PadRelease(HleContext ctx, uint pad)
    {
        int r = _Mtx_lock(pad + 8);
        if (r == 0)
        {
            GuestMemory.Write(GuestMemory.R32(pad + 0x10), (byte)1);
            r = _Cnd_signal(pad + 4);
            if (r == 0) r = _Mtx_unlock(pad + 8);
        }
        if (r != 0) ThrowCError(ctx, r);
    }

    // ------------------------------------------------------------------ system_error, the generic category

    uint _genericCategory;

    /// <summary>The generic error category (name "generic", the _Syserror_map texts, "unknown error" otherwise).</summary>
    uint GenericCategory
    {
        get
        {
            lock (_rttiLock)
            {
                if (_genericCategory != 0) return _genericCategory;
                uint vt = StaticAlloc(0x24, 4), obj = StaticAlloc(4, 4);
                uint[] slots =
                [
                    0, 0,
                    HostStub("generic_category.D1", c => c.Return(c.Gpr(0))),
                    HostStub("generic_category.D0", c => c.Return(c.Gpr(0))),
                    HostStub("generic_category.name", c => c.Return(ConstString("generic"))),
                    HostStub("generic_category.message", c =>
                    {
                        uint t = ConstString(SyserrorNames.GetValueOrDefault((int)c.Gpr(2), "unknown error"));
                        StrAssign(c.Gpr(0), t, StrLen(t));
                    }),
                    HostStub("generic_category.default_error_condition", c =>
                    {
                        uint ret = c.Gpr(0);
                        GuestMemory.W32(ret + 4, c.Gpr(2));
                        GuestMemory.W32(ret + 8, c.Gpr(1));
                    }),
                    HostStub("generic_category.equivalent(int, condition)", c =>
                    {
                        uint cond = c.Gpr(2);
                        c.Return(GuestMemory.R32(cond + 8) == c.Gpr(0) && GuestMemory.R32(cond + 4) == c.Gpr(1) ? 1 : 0);
                    }),
                    HostStub("generic_category.equivalent(code, int)", c =>
                    {
                        uint code = c.Gpr(1);
                        c.Return(GuestMemory.R32(code + 8) == c.Gpr(0) && GuestMemory.R32(code + 4) == c.Gpr(2) ? 1 : 0);
                    }),
                ];
                for (int i = 0; i < slots.Length; i++) GuestMemory.W32(vt + (uint)i * 4, slots[i]);
                GuestMemory.W32(obj, vt + 8);
                return _genericCategory = obj;
            }
        }
    }

    // the errc values and texts of the module's thread errors (_Throw_Cpp_error's index)
    static readonly (int Errc, string Text)[] CppErrors =
    [
        (16, "device or resource busy"), (22, "invalid argument"), (3, "no such process"), (12, "not enough memory"),
        (1, "operation not permitted"), (45, "resource deadlock would occur"), (11, "resource unavailable try again"),
    ];

    /// <summary>Throws std::system_error(errc, generic_category(), text) of the module's table.</summary>
    void ThrowCppError(HleContext ctx, int index)
    {
        var (errc, text) = CppErrors[index];
        uint obj = AllocateException(ExcSystemError.Size);
        Construct(obj, ExcSystemError, cstr: ConstString(text));
        GuestMemory.W32(obj + 0x24, (uint)errc);
        GuestMemory.W32(obj + 0x28, GenericCategory);
        Throw(ctx, obj, TiOf(ExcSystemError), ExportStubOfSlot(ExcSystemError, 0));
    }

    [Hle("_ZSt16_Throw_Cpp_errori")] void ThrowCppErrorFn(HleContext ctx, int code) => ThrowCppError(ctx, code);

    /// <summary>_Throw_C_error(code): nomem and timedout as resource_unavailable_try_again, busy as
    /// device_or_resource_busy, error as invalid_argument; other codes return.</summary>
    [Hle("_ZSt14_Throw_C_errori")]
    void ThrowCError(HleContext ctx, int code)
    {
        switch (code)
        {
            case 1 or 2: ThrowCppError(ctx, 6); break;
            case 3: ThrowCppError(ctx, 0); break;
            case 4: ThrowCppError(ctx, 1); break;
        }
    }

    // Dinkum::threads::lock_error and thread_resource_error: runtime_error subclasses the module does not export (their
    // typeinfo, name and vtable { D1, D0, runtime_error::what, _Doraise } under HLE-only ids, in Libc.Rtti.g.cs' format)
    const string InternalRttiTable = """
        FFFF8010 TS N6Dinkum7threads10lock_errorE
        FFFF8011 TI N6Dinkum7threads10lock_errorE V6BC74629+8 VFFFF8010 V2451AF0A
        FFFF8012 TV N6Dinkum7threads10lock_errorE 0 VFFFF8011 Hlock_error.D1 Hlock_error.D0 FAE85C2EE Hlock_error._Doraise
        FFFF8020 TS N6Dinkum7threads21thread_resource_errorE
        FFFF8021 TI N6Dinkum7threads21thread_resource_errorE V6BC74629+8 VFFFF8020 V2451AF0A
        FFFF8022 TV N6Dinkum7threads21thread_resource_errorE 0 VFFFF8021 Hthread_resource_error.D1 Hthread_resource_error.D0 FAE85C2EE Hthread_resource_error._Doraise
        """;

    static readonly ExcClass ExcLockError = new("lock_error", 0xFFFF8012, 0x20, ExcRuntimeError, null);
    static readonly ExcClass ExcThreadResourceError = new("thread_resource_error", 0xFFFF8022, 0x20, ExcRuntimeError, null);

    [Hle("_ZN6Dinkum7threads17_Throw_lock_errorEv")] void ThrowLockError(HleContext ctx) => ThrowNew(ctx, ExcLockError, text: "lock error");

    [Hle("_ZN6Dinkum7threads21_Throw_resource_errorEv")]
    void ThrowResourceError(HleContext ctx) => ThrowNew(ctx, ExcThreadResourceError, text: "thread resource error");

    // ------------------------------------------------------------------ small leftovers

    /// <summary>_Mutex(_Uninitialized): only the "not created" mark (the destructor then leaves the lock alone).</summary>
    [Hle("_ZNSt6_MutexC1ESt14_Uninitialized")]
    [Hle("_ZNSt6_MutexC2ESt14_Uninitialized")]
    uint MutexUninit(uint obj) { GuestMemory.W32(obj, 0xFFFFFFFF); return obj; }

    [Hle("_ZNSt6_MutexC2Ev")] uint MutexC2(uint obj) => _ZNSt6_MutexC1Ev(obj);
    [Hle("_ZNSt6_MutexD2Ev")] void MutexD2(uint obj) => _ZNSt6_MutexD1Ev(obj);

    /// <summary>get_new_handler under the system lock 2.</summary>
    [Hle]
    uint _ZSt15get_new_handlerv()
    {
        _Locksyslock(2);
        uint h = _newHandler;
        _Unlocksyslock(2);
        return h;
    }

    // type_info's destructors only set its vptr (and the deleting one frees)
    [Hle("_ZNSt9type_infoD1Ev")] [Hle("_ZNSt9type_infoD2Ev")] uint StdTypeInfoDtor(uint obj) { GuestMemory.W32(obj, Rtti.Vptr(0x8E9879A7)); return obj; }
    [Hle("_ZNSt9type_infoD0Ev")] uint TypeInfoDel(uint obj) { StdTypeInfoDtor(obj); free(obj); return obj; }

    /// <summary>nested_exception (+4 the exception_ptr): the destructor drops the reference.</summary>
    [Hle("_ZNSt16nested_exceptionD1Ev")]
    [Hle("_ZNSt16nested_exceptionD2Ev")]
    uint NestedDtor(uint obj)
    {
        GuestMemory.W32(obj, Rtti.Vptr(0xD7BE6282));
        DecrementRefcount(GuestMemory.R32(obj + 4));
        return obj;
    }

    [Hle("_ZNSt16nested_exceptionD0Ev")] uint NestedDel(uint obj) { NestedDtor(obj); free(obj); return obj; }

    /// <summary>out_of_range(const string&amp;).</summary>
    [Hle("_ZNSt12out_of_rangeC1ERKSs")] uint OutOfRangeFromString(uint obj, uint s) => Construct(obj, ExcOutOfRange, stringSrc: s);

    int _sharedPtrSpin;

    /// <summary>The module's spin lock of shared_ptr's atomic operations (an atomic flag).</summary>
    [Hle]
    void _Lock_shared_ptr_spin_lock()
    {
        while (Interlocked.Exchange(ref _sharedPtrSpin, 1) != 0) Thread.Yield();
    }

    [Hle] void _Unlock_shared_ptr_spin_lock() => Volatile.Write(ref _sharedPtrSpin, 0);
}
