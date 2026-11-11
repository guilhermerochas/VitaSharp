using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibMonoBridge (libmono_bridge.suprx of the PlayStation Mobile runtime): the platform layer the PSM Mono runtime
/// (libmono.suprx) is built on. It exports Mono's own GLib subset (eglib, the <c>monoeg_</c> prefix: memory, strings,
/// lists, arrays, hash tables, queues, Unicode conversion, paths, files, the markup parser, logging, timers), POSIX
/// threads, the libgcc integer helpers and the <c>pss_*</c> platform calls (files, sockets, threads, semaphores, JIT
/// code memory, time, the encrypted-file reader). The eglib part keeps eglib's public structure layouts (GList
/// {data, next, prev}, GSList {data, next}, GString {str, len, allocated_len}, GArray {data, len} + element size,
/// GPtrArray {pdata, len} + size, GQueue {head, tail, length}, GError {domain, code, message}, GHashTable's slot table)
/// in guest memory, so libmono's inline field reads and the saved state see the same objects; its allocator is the
/// process C heap (SceLibc). The runtime itself is an application (PCSI00007), not firmware; no copy of its modules is
/// installed here, so the <c>pss_*</c> prototypes are inferred from the names and from Mono's platform layer
/// (marked "inferred" where it matters).
/// </summary>
public sealed unsafe partial class MonoBridge : HleModule
{
    const string Lib = "SceLibMonoBridge";
    public override bool FirmwareFallbackOnly => true;

    Libc? _libc;
    Libc C => _libc ??= Emu.Hle.Modules.OfType<Libc>().First();
    ThreadMgr? _tm;
    ThreadMgr Tm => _tm ??= Emu.Hle.Modules.OfType<ThreadMgr>().First();
    IoFileMgr? _io;
    IoFileMgr Io => _io ??= Emu.Hle.Modules.OfType<IoFileMgr>().First();

    static uint R32(uint a) => GuestMemory.R32(a);
    static void W32(uint a, uint v) => GuestMemory.W32(a, v);
    static byte R8(uint a) => GuestMemory.Read<byte>(a);
    static void W8(uint a, byte v) => GuestMemory.Write(a, v);
    static ushort R16(uint a) => GuestMemory.Read<ushort>(a);

    // ------------------------------------------------------------------ guest heap and strings

    uint Malloc(uint n) => n == 0 ? 0 : C.HeapMalloc(n);
    uint Malloc0(uint n) => n == 0 ? 0 : C.HeapCalloc(1, n);
    void Free(uint p) { if (p != 0) C.HeapFree(p); }
    uint Realloc(uint p, uint n) => C.HeapRealloc(p, n);

    static uint StrLen(uint s) { uint n = 0; while (R8(s + n) != 0) n++; return n; }

    static byte[] Bytes(uint s, int len = -1)
    {
        if (s == 0) return [];
        int n = len < 0 ? (int)StrLen(s) : len;
        return GuestMemory.ReadSpan(s, n).ToArray();
    }

    static string Utf8(uint s, int len = -1) => s == 0 ? "" : Encoding.UTF8.GetString(GuestMemory.ReadSpan(s, len < 0 ? (int)StrLen(s) : len));

    /// <summary>A malloc'ed NUL-terminated copy of <paramref name="b"/>.</summary>
    uint DupBytes(ReadOnlySpan<byte> b)
    {
        uint p = C.HeapMalloc((uint)b.Length + 1);
        if (p == 0) return 0;
        b.CopyTo(GuestMemory.Span(p, b.Length));
        W8(p + (uint)b.Length, 0);
        return p;
    }

    uint StrDup(string s) => DupBytes(Encoding.UTF8.GetBytes(s));

    /// <summary>A malloc'ed copy of the guest string (NULL for NULL).</summary>
    uint StrDupGuest(uint s) => s == 0 ? 0 : DupBytes(GuestMemory.ReadSpan(s, (int)StrLen(s)));

    static GuestThread Me => GuestThread.Current ?? throw new InvalidOperationException("SceLibMonoBridge: needs a guest thread");

    static uint Call(uint fn, params ReadOnlySpan<uint> args) => Me.CallGuest(fn, args);

    /// <summary>The NID an HLE import stub (svc #n; bx lr; nid) stands for, 0 for any other code.</summary>
    static uint StubNid(uint fn)
    {
        if (fn == 0 || (fn & 3) != 0 || !GuestMemory.IsMapped(fn)) return 0;
        return (R32(fn) & 0xFF000000) == 0xEF000000 && R32(fn + 4) == 0xE12FFF1E ? R32(fn + 8) : 0;
    }

    readonly Dictionary<string, uint> _exportStubs = new();
    uint _stubBlock, _stubUsed;

    /// <summary>A guest-callable address of one of this library's functions (eglib's defaults, g_direct_hash...).</summary>
    uint ExportStub(string name)
    {
        lock (_exportStubs)
        {
            if (_exportStubs.TryGetValue(name, out uint a)) return a;
            var info = NidDatabase.LookupName(name).First(n => n.Library == Lib);
            return _exportStubs[name] = NewStub(Emu.Hle.GetHleFunctionIndex(info.Nid, 0, Lib), info.Nid);
        }
    }

    uint HostStub(string name, Action<HleContext> body)
    {
        lock (_exportStubs)
        {
            if (_exportStubs.TryGetValue("host:" + name, out uint a)) return a;
            return _exportStubs["host:" + name] = NewStub(Emu.Hle.RegisterHostFunction("SceLibMonoBridge:" + name, body), 0);
        }
    }

    uint NewStub(uint svc, uint nid)
    {
        if (_stubBlock == 0 || _stubUsed + 16 > 0x400) { _stubBlock = C.PafStatic(0x400, 16); _stubUsed = 0; }
        uint a = _stubBlock + _stubUsed;
        _stubUsed += 16;
        Loader.Linker.WriteHleStub(a, svc, nid);
        Emu.Code.Invalidate(a, 16);
        return a;
    }

    // ------------------------------------------------------------------ libgcc integer helpers

    [Hle("__ashldi3", Library = Lib)] long Ashldi3(long a, int b) => (uint)b >= 64 ? 0 : a << b;
    [Hle("__lshrdi3", Library = Lib)] ulong Lshrdi3(ulong a, int b) => (uint)b >= 64 ? 0 : a >> b;
    [Hle("__divdi3", Library = Lib)] long Divdi3(long a, long b) => b == 0 ? 0 : b == -1 ? unchecked(-a) : a / b;
    [Hle("__moddi3", Library = Lib)] long Moddi3(long a, long b) => b == 0 ? a : b == -1 ? 0 : a % b;
    [Hle("__udivdi3", Library = Lib)] ulong Udivdi3(ulong a, ulong b) => b == 0 ? 0 : a / b;
    [Hle("__umoddi3", Library = Lib)] ulong Umoddi3(ulong a, ulong b) => b == 0 ? a : a % b;
    [Hle("__divsi3", Library = Lib)] int Divsi3(int a, int b) => b == 0 ? 0 : b == -1 ? unchecked(-a) : a / b;
    [Hle("__modsi3", Library = Lib)] int Modsi3(int a, int b) => b == 0 ? a : b == -1 ? 0 : a % b;
    [Hle("__udivsi3", Library = Lib)] uint Udivsi3(uint a, uint b) => b == 0 ? 0 : a / b;
    [Hle("__umodsi3", Library = Lib)] uint Umodsi3(uint a, uint b) => b == 0 ? a : a % b;

    /// <summary>The division-by-zero handlers the helpers above call (as SceLibKernel's __sce_aeabi_idiv0): a warning, 0.</summary>
    [Hle("__sce_aeabi_idiv1", Library = Lib)] int Idiv1() { Log.WarnOnce("monobridge-idiv0", "SceLibMonoBridge: integer division by zero"); return 0; }
    [Hle("__sce_aeabi_ldiv1", Library = Lib)] long Ldiv1() { Log.WarnOnce("monobridge-ldiv0", "SceLibMonoBridge: 64-bit integer division by zero"); return 0; }

    // ------------------------------------------------------------------ environment and small POSIX calls

    readonly object _envLock = new();
    readonly SortedDictionary<string, (string Value, uint Guest)> _env = new(StringComparer.Ordinal);

    public override void Initialize()
    {
        if (NidDatabase.LookupName("environ").FirstOrDefault(n => n.Library == Lib) is { } env)
            Emu.Hle.RegisterVariable(env.Nid, EnvironVar);
    }

    uint _environCell;
    readonly List<uint> _environBlocks = new();

    /// <summary>environ (char **): a guest word holding the NULL-terminated "NAME=value" list, rebuilt when the
    /// environment changes.</summary>
    uint EnvironVar()
    {
        lock (_envLock)
        {
            if (_environCell == 0) { _environCell = C.PafStatic(4, 4); RebuildEnviron(); }
            return _environCell;
        }
    }

    void RebuildEnviron()
    {
        if (_environCell == 0) return;
        foreach (uint p in _environBlocks) Free(p);
        _environBlocks.Clear();
        uint arr = Malloc((uint)(_env.Count + 1) * 4);
        _environBlocks.Add(arr);
        int i = 0;
        foreach (var (k, v) in _env)
        {
            uint s = StrDup(k + "=" + v.Value);
            _environBlocks.Add(s);
            W32(arr + (uint)i++ * 4, s);
        }
        W32(arr + (uint)i * 4, 0);
        W32(_environCell, arr);
    }

    /// <summary>getenv(name): the value kept in guest memory until the variable changes; NULL when unset.</summary>
    [Hle("getenv", Library = Lib)]
    [Hle("monoeg_g_getenv", Library = Lib)]
    uint Getenv(uint name)
    {
        if (name == 0) return 0;
        string n = Utf8(name);
        lock (_envLock)
        {
            if (!_env.TryGetValue(n, out var v)) return 0;
            if (v.Guest == 0) { v.Guest = StrDup(v.Value); _env[n] = v; }
            return v.Guest;
        }
    }

    /// <summary>g_setenv(variable, value, overwrite): TRUE (an existing variable is kept unless overwrite).</summary>
    [Hle("monoeg_g_setenv", Library = Lib)]
    bool Setenv(uint variable, uint value, bool overwrite)
    {
        if (variable == 0) return false;
        string n = Utf8(variable), v = Utf8(value);
        lock (_envLock)
        {
            if (_env.TryGetValue(n, out var old))
            {
                if (!overwrite) return true;
                Free(old.Guest);
            }
            _env[n] = (v, 0);
            RebuildEnviron();
        }
        return true;
    }

    [Hle("monoeg_g_unsetenv", Library = Lib)]
    void Unsetenv(uint variable)
    {
        if (variable == 0) return;
        lock (_envLock)
            if (_env.Remove(Utf8(variable), out var old)) { Free(old.Guest); RebuildEnviron(); }
    }

    /// <summary>unlink(path): sceIoRemove; 0, or -1 with errno.</summary>
    [Hle("unlink", Library = Lib)]
    int Unlink(uint path)
    {
        int r = Io.sceIoRemove(PsmPath(Utf8(path)));
        if (r >= 0) return 0;
        Libc.SetErrno(IoErrno(r));
        return -1;
    }

    /// <summary>sched_yield: gives the core away (sceKernelDelayThread(0)), 0.</summary>
    [Hle("sched_yield", Library = Lib)] int SchedYield() { Tm.sceKernelDelayThread(0); return 0; }

    /// <summary>mono_security_enable_core_clr (SceLibMono, the one export of libmono.suprx in the VitaSDK list): turns on
    /// the CoreCLR security model (platform code trusted, the program's transparent), as the PSM runtime does at start.
    /// The flag is kept; the bridge's own calls are all platform code.</summary>
    [Hle("mono_security_enable_core_clr", Library = "SceLibMono")] void MonoSecurityEnableCoreClr() => CoreClrSecurity = true;

    internal bool CoreClrSecurity { get; private set; }

    /// <summary>errno from an sceIo* result (0x8001xxxx: the low half is the errno number).</summary>
    static int IoErrno(int r) => (r & 0xFFFF0000) == unchecked((int)0x80010000) ? r & 0xFFFF : Libc.EIO;
}
