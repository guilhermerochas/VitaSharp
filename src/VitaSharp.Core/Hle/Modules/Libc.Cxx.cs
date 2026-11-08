using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceLibstdcxx (libc.suprx 3.74): the parts of the C++ runtime that do not need the module's own data (vtables,
// exception tables): operator new/delete and the new handler, std::_Mutex, the 4-byte atomics, type_info compares,
// the what() texts, _Syserror_map, _Xtime_get_ticks; and SceLibc's module TLS (_sceLdTls*, __tls_get_addr).
// C++ exceptions are not raised here: where the library would throw (new without memory and no handler) the
// process is terminated as an uncaught exception would.
public sealed partial class Libc
{
    // ------------------------------------------------------------------ operator new / delete

    uint _newHandler;

    /// <summary>operator new: malloc, calling the new handler while it fails; with no handler std::bad_alloc would
    /// be thrown, which here ends the process (terminate).</summary>
    uint New(uint size, bool nothrow)
    {
        while (true)
        {
            uint p = HeapAlloc(size == 0 ? 1 : size, HeapAlign);
            if (p != 0) return p;
            uint h = _newHandler;
            if (h == 0)
            {
                if (nothrow) return 0;
                Log.Error($"[guest] operator new({size}): out of memory and no new handler (std::bad_alloc)");
                terminate();
                return 0;
            }
            GuestThread.Current?.CallGuest(h);
        }
    }

    [Hle("_Znwj")] [Hle("_Znaj")] uint _Znwj(uint size) => New(size, false);
    [Hle("_ZnwjRKSt9nothrow_t")] [Hle("_ZnajRKSt9nothrow_t")] uint _ZnwjRKSt9nothrow_t(uint size, uint nothrow) => New(size, true);
    [Hle("_ZdlPv")] [Hle("_ZdaPv")] [Hle("_ZdlPvRKSt9nothrow_t")] [Hle("_ZdaPvRKSt9nothrow_t")] void _ZdlPv(uint p) => free(p);
    /// <summary>The placement forms do nothing.</summary>
    [Hle("_ZdlPvS_")] [Hle("_ZdaPvS_")] void _ZdlPvS_(uint p, uint place) { }

    [Hle] uint _ZSt15set_new_handlerPFvvE(uint h) { uint old = _newHandler; _newHandler = h; return old; }
    [Hle] uint _ZSt16_get_new_handlerv() => _newHandler;

    uint _terminateHandler;

    [Hle] uint _ZSt13set_terminatePFvvE(uint h) { uint old = _terminateHandler; _terminateHandler = h; return old; }

    /// <summary>std::terminate: the terminate handler, then abort.</summary>
    [Hle("_ZSt9terminatev")]
    void terminate()
    {
        uint h = _terminateHandler;
        if (h != 0) GuestThread.Current?.CallGuest(h);
        abort();
    }

    /// <summary>No exception is ever in flight in the HLE runtime.</summary>
    [Hle] int _ZSt18uncaught_exceptionv() => 0;

    // ------------------------------------------------------------------ std::_Mutex

    /// <summary>std::_Mutex: +0 a word set to 1, +8 the library's recursive lock ("SceLibcStreambuf").</summary>
    [Hle] uint _ZNSt6_MutexC1Ev(uint obj) { GuestMemory.W32(obj, 1); return obj; }
    [Hle] void _ZNSt6_MutexD1Ev(uint obj) { lock (_locks) _locks.Remove(obj + 8); }
    [Hle] void _ZNSt6_Mutex5_LockEv(uint obj) => Lock(obj + 8);
    [Hle] void _ZNSt6_Mutex7_UnlockEv(uint obj) => Unlock(obj + 8);

    // ------------------------------------------------------------------ 4-byte atomics (memory_order ignored: host atomics are sequentially consistent)

    static ref int Cell(uint p) => ref GuestMemory.Ref<int>(p);

    [Hle] int _Atomic_load_4(uint p, int order) => Volatile.Read(ref Cell(p));
    [Hle] void _Atomic_store_4(uint p, int v, int order) => Volatile.Write(ref Cell(p), v);
    [Hle] int _Atomic_exchange_4(uint p, int v, int order) => Interlocked.Exchange(ref Cell(p), v);
    [Hle] int _Atomic_fetch_add_4(uint p, int v, int order) => Interlocked.Add(ref Cell(p), v) - v;
    [Hle] int _Atomic_fetch_sub_4(uint p, int v, int order) => Interlocked.Add(ref Cell(p), -v) + v;
    [Hle] int _Atomic_fetch_and_4(uint p, int v, int order) => Interlocked.And(ref Cell(p), v);
    [Hle] int _Atomic_fetch_or_4(uint p, int v, int order) => Interlocked.Or(ref Cell(p), v);

    [Hle]
    int _Atomic_fetch_xor_4(uint p, int v, int order)
    {
        int old;
        do old = Volatile.Read(ref Cell(p)); while (Interlocked.CompareExchange(ref Cell(p), old ^ v, old) != old);
        return old;
    }

    /// <summary>(p, expected*, desired, success order, failure order): 1 and stored when *p == *expected, else 0 with
    /// *expected = *p.</summary>
    [Hle("_Atomic_compare_exchange_weak_4")]
    [Hle("_Atomic_compare_exchange_strong_4")]
    int _Atomic_compare_exchange_strong_4(uint p, uint expected, int desired, int success, int failure)
    {
        int exp = (int)GuestMemory.R32(expected);
        int old = Interlocked.CompareExchange(ref Cell(p), desired, exp);
        if (old == exp) return 1;
        GuestMemory.W32(expected, (uint)old);
        return 0;
    }

    [Hle] int _Atomic_is_lock_free_4() => 1;

    // ------------------------------------------------------------------ type_info, what(), _Syserror_map, xtime

    /// <summary>type_info: +0 vtable, +4 the name; equality and order compare the name pointers (libc.suprx).</summary>
    [Hle] uint _ZNKSt9type_info4nameEv(uint obj) => GuestMemory.R32(obj + 4);
    [Hle] int _ZNKSt9type_infoeqERKS_(uint obj, uint other) => GuestMemory.R32(obj + 4) == GuestMemory.R32(other + 4) ? 1 : 0;
    [Hle] int _ZNKSt9type_infoneERKS_(uint obj, uint other) => GuestMemory.R32(obj + 4) != GuestMemory.R32(other + 4) ? 1 : 0;
    [Hle] int _ZNKSt9type_info6beforeERKS_(uint obj, uint other) => GuestMemory.R32(obj + 4) < GuestMemory.R32(other + 4) ? 1 : 0;

    [Hle] uint _ZNKSt9exception4whatEv(uint obj) => ConstString("");
    [Hle] uint _ZNKSt9bad_alloc4whatEv(uint obj) => ConstString("bad allocation");
    [Hle] uint _ZNKSt13bad_exception4whatEv(uint obj) => ConstString("bad exception");
    [Hle] uint _ZNKSt8bad_cast4whatEv(uint obj) => ConstString("bad cast");
    [Hle] uint _ZNKSt10bad_typeid4whatEv(uint obj) => ConstString("bad typeid");

    /// <summary>The generic error names of std::error_code messages (libc.suprx's table), NULL for others.</summary>
    static readonly Dictionary<int, string> SyserrorNames = new()
    {
        [1004] = "address family not supported", [1002] = "address in use", [1003] = "address not available", [1028] = "already connected",
        [7] = "argument list too long", [33] = "argument out of domain", [14] = "bad address", [9] = "bad file descriptor",
        [77] = "bad message", [32] = "broken pipe", [1012] = "connection aborted", [1006] = "connection already in progress",
        [1013] = "connection refused", [1014] = "connection reset", [18] = "cross device link", [1016] = "destination address required",
        [16] = "device or resource busy", [90] = "directory not empty", [8] = "executable format error", [17] = "file exists",
        [27] = "file too large", [91] = "filename too long", [88] = "function not supported", [1021] = "host unreachable",
        [1022] = "identifier removed", [138] = "illegal byte sequence", [25] = "inappropriate io control operation", [4] = "interrupted",
        [22] = "invalid argument", [29] = "invalid seek", [5] = "io error", [21] = "is a directory", [122] = "message size",
        [1035] = "network down", [1036] = "network reset", [1037] = "network unreachable", [1039] = "no buffer space",
        [10] = "no child process", [1045] = "no link", [46] = "no lock available", [1040] = "no message available", [1047] = "no message",
        [1048] = "no protocol option", [28] = "no space on device", [1050] = "no stream resources", [6] = "no such device or address",
        [19] = "no such device", [2] = "no such file or directory", [3] = "no such process", [20] = "not a directory",
        [1057] = "not a socket", [1051] = "not a stream", [1053] = "not connected", [12] = "not enough memory", [134] = "not supported",
        [140] = "operation canceled", [119] = "operation in progress", [1] = "operation not permitted", [1061] = "operation not supported",
        [1077] = "operation would block", [1064] = "owner dead", [13] = "permission denied", [1067] = "protocol error",
        [1068] = "protocol not supported", [30] = "read only file system", [45] = "resource deadlock would occur",
        [11] = "resource unavailable try again", [34] = "result out of range", [1056] = "state not recoverable", [1074] = "stream timeout",
        [1076] = "text file busy", [116] = "timed out", [23] = "too many files open in system", [24] = "too many files open",
        [31] = "too many links", [1030] = "too many symbolic link levels", [1063] = "value too large", [1069] = "wrong protocol type",
    };

    [Hle] uint _ZSt13_Syserror_mapi(int code) => SyserrorNames.TryGetValue(code, out var s) ? ConstString(s) : 0;

    /// <summary>_Xtime_get_ticks: microseconds since 1970 (sceKernelLibcGettimeofday: sec * 1000000 + usec).</summary>
    [Hle] long _Xtime_get_ticks() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;

    // ------------------------------------------------------------------ module TLS

    // _sceLdTlsRegisterModuleInfo(moduleAddr, indexTable, isMain): the module's TLS image (sceKernelGetModuleInfoByAddr
    // +0x48 init, +0x4C init size, +0x50 area size) gets an index (0 for the main module, else the first free from 1)
    // written into every tls_index { module, offset } of indexTable [begin +4, end +8). __tls_get_addr(ti) is the
    // calling thread's block of that module + offset: the main module's is the thread's own TLS (TPIDRURO), the others
    // are made on first use from the image.
    readonly Dictionary<int, (uint Key, uint Init, uint InitSize, uint AreaSize)> _tlsModules = new();
    readonly Dictionary<(int Thread, int Index), uint> _tlsBlocks = new();

    [Hle]
    int _sceLdTlsRegisterModuleInfo(uint moduleAddr, uint indexTable, int isMain)
    {
        var m = K.Modules.Values.FirstOrDefault(x => x.Segments.Values.Any(s => moduleAddr - s.Address < s.Size));
        if (m == null) return -1;
        if (m.TlsInit == 0 && m.TlsInitSize == 0 && m.TlsAreaSize == 0) return 0;
        int index;
        lock (_tlsModules)
        {
            index = isMain != 0 ? 0 : 1;
            if (isMain == 0) while (_tlsModules.ContainsKey(index)) index++;
            _tlsModules[index] = (moduleAddr, m.TlsInit, m.TlsInitSize, m.TlsAreaSize);
        }
        if (indexTable != 0)
            for (uint p = GuestMemory.R32(indexTable + 4), end = GuestMemory.R32(indexTable + 8); p < end; p += 8) GuestMemory.W32(p, (uint)index);
        return 0;
    }

    [Hle]
    int _sceLdTlsUnregisterModuleInfo(uint moduleAddr)
    {
        lock (_tlsModules)
        {
            foreach (var (i, e) in _tlsModules)
                if (i >= 1 && e.Key == moduleAddr)
                {
                    _tlsModules.Remove(i);
                    foreach (var k in _tlsBlocks.Keys.Where(k => k.Index == i).ToList()) _tlsBlocks.Remove(k);
                    return 0;
                }
        }
        return -1;
    }

    [Hle]
    uint __tls_get_addr(uint ti)
    {
        if (ti == 0 || GuestThread.Current is not { } t) return 0;
        int index = (int)GuestMemory.R32(ti);
        uint off = GuestMemory.R32(ti + 4);
        if (index == 0) return t.UserTls + off;
        (uint Key, uint Init, uint InitSize, uint AreaSize) m;
        lock (_tlsModules)
        {
            if (!_tlsModules.TryGetValue(index, out m)) return 0;
            if (_tlsBlocks.TryGetValue((t.Uid, index), out uint b)) return b + off;
        }
        uint block = HeapAlloc(Math.Max(m.AreaSize, m.InitSize), 16);
        if (block == 0) return 0;
        GuestMemory.Fill(block, 0, Math.Max(m.AreaSize, m.InitSize));
        if (m.Init != 0 && m.InitSize > 0) GuestMemory.Copy(block, m.Init, m.InitSize);
        lock (_tlsModules) _tlsBlocks[(t.Uid, index)] = block;
        return block + off;
    }
}
