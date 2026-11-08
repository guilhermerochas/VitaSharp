using System.Runtime.CompilerServices;
using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceLibstdcxx C++ exceptions as libc.suprx 3.74 has them: the __cxa_* ABI over the unwinder of Libc.Eh.cs, the
// exception header of the module (0x80 bytes before the thrown object: +0 reference count, +8 type, +0x0C destructor,
// +0x10 unexpected handler, +0x14 terminate handler, +0x18 next caught, +0x1C handler count, +0x20 next propagating,
// +0x24 propagation count, +0x28 the _Unwind_Control_Block with class "ARM\0C++\0"), per-thread globals (uncaught
// count, unexpected and terminate handlers, caught and propagating chains, a 0x88-byte emergency buffer), the
// standard exception classes with the module's vtables and RTTI (Libc.Rtti.g.cs) and the _X* throw helpers.
// An exception cannot cross a guest call made from the HLE (an unexpected or terminate handler, a guest _Doraise):
// such a throw finds no frame above the call and ends in std::terminate.
public sealed partial class Libc
{
    const uint LibstdcxxNid = 0xF7629240;

    // ------------------------------------------------------------------ RTTI data and stubs

    internal sealed class RttiData
    {
        public readonly Dictionary<uint, uint> Address = new();
        public uint PointerTi, PtmTi, NullptrTi, FunctionTi, VoidTi, SiTi, VmiTi, NullPtr, NullPtm;
        public uint Ti(uint nid) => Address.GetValueOrDefault(nid);
        public uint Vptr(uint vtNid) => Address.TryGetValue(vtNid, out uint a) ? a + 8 : 0;
    }

    RttiData? _rtti;
    readonly object _rttiLock = new();
    readonly Dictionary<string, uint> _hostStubs = new();
    uint _stubBlock, _stubUsed;

    internal RttiData Rtti
    {
        get
        {
            var r = Volatile.Read(ref _rtti);
            if (r != null) return r;
            lock (_rttiLock) return _rtti ??= BuildRtti();
        }
    }

    /// <summary>An HLE stub (svc; bx lr; nid) in the module's stub block.</summary>
    uint NewStub(uint svc, uint nid)
    {
        lock (_rttiLock)
        {
            if (_stubBlock == 0 || _stubUsed + 16 > 0x1000) { _stubBlock = StaticAlloc(0x1000, 16); _stubUsed = 0; }
            uint a = _stubBlock + _stubUsed;
            _stubUsed += 16;
            Linker.WriteHleStub(a, svc, nid);
            Emu.Code.Invalidate(a, 16);
            return a;
        }
    }

    readonly Dictionary<uint, uint> _exportStubs = new();

    /// <summary>A guest-callable address of an SceLibstdcxx export of this module.</summary>
    uint ExportStub(uint nid)
    {
        lock (_rttiLock)
        {
            if (_exportStubs.TryGetValue(nid, out uint a)) return a;
            return _exportStubs[nid] = NewStub(Emu.Hle.GetHleFunctionIndex(nid, LibstdcxxNid, "SceLibstdcxx"), nid);
        }
    }

    /// <summary>A guest-callable address of a function the module does not export.</summary>
    uint HostStub(string name, Action<HleContext> body)
    {
        lock (_rttiLock)
        {
            if (_hostStubs.TryGetValue(name, out uint a)) return a;
            return _hostStubs[name] = NewStub(Emu.Hle.RegisterHostFunction("SceLibstdcxx:" + name, body), 0);
        }
    }

    /// <summary>The functions of the module's vtables it does not export (deleting destructors and what() of a few
    /// classes).</summary>
    uint InternalFunction(string name) => name switch
    {
        "bad_array_new_length.D0" => HostStub(name, c => c.Return(DeletingDtor(c.Gpr(0), ExcBadArrayNewLength))),
        "bad_function_call.D0" => HostStub(name, c => c.Return(DeletingDtor(c.Gpr(0), ExcBadFunctionCall))),
        "bad_function_call.what" => HostStub(name, c => c.Return(ConstString("bad function call"))),
        "bad_weak_ptr.D0" => HostStub(name, c => c.Return(DeletingDtor(c.Gpr(0), ExcBadWeakPtr))),
        "bad_weak_ptr.what" => HostStub(name, c => c.Return(ConstString("bad_weak_ptr"))),
        "system_error.D0" => HostStub(name, c => c.Return(DeletingDtor(c.Gpr(0), ExcSystemError))),
        _ => 0,
    };

    static readonly HashSet<string> InternalFunctions =
    [
        "bad_array_new_length.D0", "bad_function_call.D0", "bad_function_call.what", "bad_weak_ptr.D0", "bad_weak_ptr.what",
        "system_error.D0",
    ];

    /// <summary>
    /// Builds the exported RTTI of Libc.Rtti.g.cs in guest memory: every typeinfo and name, and the vtables whose
    /// functions the HLE implements (the others, the stream and locale classes, stay unprovided).
    /// </summary>
    RttiData BuildRtti()
    {
        var rows = new List<(uint Nid, string Kind, string Name, string[] Words)>();
        foreach (var line in RttiTable.Split('\n'))
        {
            var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) continue;
            rows.Add((System.Convert.ToUInt32(p[0], 16), p[1], p[2], p[3..]));
        }
        bool Buildable(string[] words) => words.All(w => w[0] switch
        {
            'F' => Emu.Hle.Implements(System.Convert.ToUInt32(w[1..], 16)),
            'H' => InternalFunctions.Contains(w[1..]),
            'X' => false,
            _ => true,
        });
        var keep = rows.Where(r => r.Kind != "TV" || Buildable(r.Words)).ToList();
        var known = keep.Select(r => r.Nid).ToHashSet();
        // a vtable referring to an object left out (none today) is left out too
        keep = keep.Where(r => r.Words.All(w => w[0] != 'V' || known.Contains(System.Convert.ToUInt32(w[1..].Split('+')[0], 16)))).ToList();
        uint size = 16; // nullptr constants: { 0, 0 } and { -1, 0 }
        var at = new Dictionary<uint, uint>();
        foreach (var r in keep)
        {
            at[r.Nid] = size;
            size += r.Kind == "TS" ? GuestMemory.AlignUp((uint)r.Name.Length + 1, 4) : (uint)r.Words.Length * 4;
        }
        uint block = StaticAlloc(size, 8);
        var data = new RttiData();
        foreach (var (nid, off) in at) data.Address[nid] = block + off;
        GuestMemory.W32(block + 8, 0xFFFFFFFF);
        data.NullPtr = block;
        data.NullPtm = block + 8;
        foreach (var r in keep)
        {
            uint a = data.Address[r.Nid];
            if (r.Kind == "TS")
            {
                GuestMemory.WriteCString(a, r.Name);
                continue;
            }
            for (int i = 0; i < r.Words.Length; i++)
            {
                string w = r.Words[i];
                uint v = w[0] switch
                {
                    'V' => Ref(w[1..]),
                    'F' => ExportStub(System.Convert.ToUInt32(w[1..], 16)),
                    'H' => InternalFunction(w[1..]),
                    _ => System.Convert.ToUInt32(w, 16),
                };
                GuestMemory.W32(a + (uint)i * 4, v);
            }
        }
        uint Ref(string s)
        {
            var p = s.Split('+');
            return data.Address[System.Convert.ToUInt32(p[0], 16)] + (p.Length > 1 ? System.Convert.ToUInt32(p[1], 16) : 0);
        }
        data.PointerTi = data.Ti(0x835E9E78);  // N10__cxxabiv119__pointer_type_infoE
        data.PtmTi = data.Ti(0xDF23E7B5);      // N10__cxxabiv129__pointer_to_member_type_infoE
        data.FunctionTi = data.Ti(0x62941235); // N10__cxxabiv120__function_type_infoE
        data.SiTi = data.Ti(0x461E17F0);       // N10__cxxabiv120__si_class_type_infoE
        data.VmiTi = data.Ti(0xB80BFF06);      // N10__cxxabiv121__vmi_class_type_infoE
        data.NullptrTi = data.Ti(0x64F462B0);  // Dn
        data.VoidTi = data.Ti(0x76C6BDCE);     // v
        Log.Info($"SceLibstdcxx (HLE): {keep.Count} RTTI objects ({rows.Count - keep.Count} vtables of classes the HLE does not implement left out)");
        return data;
    }

    public override void Initialize()
    {
        foreach (var line in RttiTable.Split('\n'))
        {
            var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) continue;
            uint nid = System.Convert.ToUInt32(p[0], 16);
            if (nid >= 0xFFFF0000) continue; // not exported
            Emu.Hle.RegisterVariable(nid, () => Rtti.Address.GetValueOrDefault(nid));
        }
        RegisterStreamVariables();
        RegisterLibcVariables();
    }

    // ------------------------------------------------------------------ per-thread globals

    sealed class CxaGlobals
    {
        public uint Uncaught, Unexpected, Terminate, OneShot, Caught, Propagating, Emergency;
    }

    readonly ConditionalWeakTable<GuestThread, CxaGlobals> _cxaGlobals = new();
    readonly CxaGlobals _noThreadGlobals = new();

    CxaGlobals Globals()
    {
        var t = GuestThread.Current;
        var g = t == null ? _noThreadGlobals : _cxaGlobals.GetValue(t, _ => new CxaGlobals());
        if (g.Terminate == 0)
        {
            g.Unexpected = HostStub("default unexpected handler", _ => TerminateNow());
            g.Terminate = HostStub("default terminate handler", _ => DefaultTerminate());
            g.Emergency = HeapAlloc(0x88, HeapAlign);
            if (g.Emergency != 0) GuestMemory.Write(g.Emergency, (byte)0);
        }
        return g;
    }

    // ------------------------------------------------------------------ handlers and terminate

    [Hle] uint _ZSt13set_terminatePFvvE(uint h) { var g = Globals(); uint old = g.Terminate; g.Terminate = h; return old; }
    [Hle] uint _ZSt14set_unexpectedPFvvE(uint h) { var g = Globals(); uint old = g.Unexpected; g.Unexpected = h; return old; }

    /// <summary>std::terminate: the handler a __cxa_call_terminate armed, else the thread's terminate handler, then
    /// abort.</summary>
    [Hle("_ZSt9terminatev")]
    void TerminateNow()
    {
        var g = Globals();
        uint h = g.OneShot != 0 ? g.OneShot : g.Terminate;
        g.OneShot = 0;
        if (h != 0) GuestThread.Current?.CallGuest(h);
        abort();
    }

    /// <summary>std::unexpected: the unexpected handler, then terminate.</summary>
    [Hle("_ZSt10unexpectedv")]
    void UnexpectedNow()
    {
        var g = Globals();
        uint h = g.OneShot != 0 ? g.OneShot : g.Unexpected;
        g.OneShot = 0;
        if (h != 0) GuestThread.Current?.CallGuest(h);
        TerminateNow();
    }

    /// <summary>The module's default terminate handler: "error: exception: : TYPE" for a caught C++ exception, then
    /// abort.</summary>
    void DefaultTerminate()
    {
        var g = Globals();
        uint h = g.Caught;
        if (h != 0 && IsNativeException(h + 0x28) && GuestMemory.R32(h + 8) is var ti && ti != 0)
            DebugMessage("exception: ", GuestMemory.ReadCString(GuestMemory.R32(ti + 4)), 0);
        abort();
    }

    /// <summary>_Debug_message(message, file[, line]): "error: MESSAGE[: FILE[: line N]]" on stderr, then abort.</summary>
    void DebugMessage(string msg, string? file, uint line)
    {
        var sb = new StringBuilder("error: ").Append(msg);
        if (file != null)
        {
            sb.Append(": ").Append(file);
            if (line != 0) sb.Append(": line ").Append(line);
        }
        sb.Append('\n');
        Log.Error("[guest] " + sb.ToString().TrimEnd());
        WriteStream(StderrFile(), Encoding.Latin1.GetBytes(sb.ToString()));
        abort();
    }

    [Hle] void _ZSt14_Debug_messagePKcS0_(uint msg, uint file) => DebugMessage(Str(msg), file != 0 ? Str(file) : null, 0);
    [Hle] void _ZSt14_Debug_messagePKcS0_j(uint msg, uint file, uint line) => DebugMessage(Str(msg), file != 0 ? Str(file) : null, line);

    /// <summary>std::_Throw(const exception&): _Debug_message("exception", e.what()).</summary>
    [Hle]
    void _ZSt6_ThrowRKSt9exception(uint e)
    {
        uint what = CallVirtual(e, 2);
        DebugMessage("exception", what != 0 ? Str(what) : null, 0);
    }

    static string Str(uint p) => GuestMemory.ReadCString(p);

    /// <summary>Calls the <paramref name="slot"/>-th virtual function of <paramref name="obj"/> (this in r0).</summary>
    static uint CallVirtual(uint obj, int slot, uint arg = 0)
    {
        uint fn = GuestMemory.R32(GuestMemory.R32(obj) + (uint)slot * 4);
        return GuestThread.Current?.CallGuest(fn, obj, arg) ?? 0;
    }

    /// <summary>__cxa_call_terminate(ucb): the exception becomes caught, then std::terminate with its terminate
    /// handler.</summary>
    [Hle("__cxa_call_terminate")]
    void CallTerminate(uint ucb)
    {
        var g = Globals();
        if (ucb != 0) BeginCatch(ucb);
        g.OneShot = ucb != 0 && IsNativeException(ucb) ? GuestMemory.R32(ucb - 0x14) : g.Terminate;
        TerminateNow();
    }

    [Hle] internal int _ZSt18uncaught_exceptionv() => Globals().Uncaught != 0 ? 1 : 0;

    // ------------------------------------------------------------------ exception objects

    [Hle("__cxa_allocate_exception")]
    uint AllocateException(uint size)
    {
        var g = Globals();
        uint h = HeapAlloc(size + 0x80, HeapAlign);
        if (h == 0)
        {
            uint e = g.Emergency;
            if (size + 0x80 <= 0x84 && e != 0 && GuestMemory.Read<byte>(e) == 0)
            {
                GuestMemory.Write(e, (byte)1);
                h = e + 4;
            }
            else
            {
                g.OneShot = g.Terminate;
                TerminateNow();
                return 0;
            }
        }
        ArmCxxClass.CopyTo(GuestMemory.Span(h + 0x28, 8));
        foreach (uint o in (uint[])[0x30, 0x18, 0x20, 0x34, 0x1C, 0x24]) GuestMemory.W32(h + o, 0);
        GuestMemory.W32(h + 0x14, g.Terminate);
        GuestMemory.W32(h + 0x10, g.Unexpected);
        return h + 0x80;
    }

    /// <summary>Frees an exception header (or gives back the emergency buffer).</summary>
    void FreeHeader(uint h)
    {
        var g = Globals();
        if (g.Emergency != 0 && h == g.Emergency + 4) GuestMemory.Write(g.Emergency, (byte)0);
        else free(h);
    }

    [Hle] void __cxa_free_exception(uint obj) => FreeHeader(obj - 0x80);

    /// <summary>__cxa_get_exception_ptr(ucb): the catch parameter the personality routine stored.</summary>
    [Hle] uint __cxa_get_exception_ptr(uint ucb) => GuestMemory.R32(ucb + UcbBarrier1);

    [Hle]
    void __cxa_increment_exception_refcount(uint obj)
    {
        if (obj != 0) Interlocked.Increment(ref GuestMemory.Ref<int>(obj - 0x80));
    }

    [Hle("__cxa_decrement_exception_refcount")]
    void DecrementRefcount(uint obj)
    {
        if (obj == 0 || Interlocked.Decrement(ref GuestMemory.Ref<int>(obj - 0x80)) != 0) return;
        uint dest = GuestMemory.R32(obj - 0x74);
        if (dest != 0) GuestThread.Current?.CallGuest(dest, obj);
        FreeHeader(obj - 0x80);
    }

    uint _cleanupStub, _dependentCleanupStub;

    /// <summary>The UCB exception_cleanup of a thrown exception (reason, ucb): released when nothing handles it any
    /// more.</summary>
    uint CleanupStub => _cleanupStub != 0 ? _cleanupStub : _cleanupStub = HostStub("exception cleanup", c => ExceptionCleanup(c.Gpr(1)));

    uint DependentCleanupStub => _dependentCleanupStub != 0 ? _dependentCleanupStub
        : _dependentCleanupStub = HostStub("dependent exception cleanup", c =>
        {
            uint ucb = c.Gpr(1);
            DecrementRefcount(GuestMemory.R32(ucb - 0x28));
            FreeHeader(ucb - 0x28);
        });

    void ExceptionCleanup(uint ucb)
    {
        if (GuestMemory.R32(ucb - 0xC) == 0) { DecrementRefcount(ucb + UcbSize); return; }
        GuestMemory.W32(ucb - 4, GuestMemory.R32(ucb - 4) - 1);
        Globals().Uncaught--;
    }

    /// <summary>_Unwind_DeleteException: the exception's cleanup function with _URC_FOREIGN_EXCEPTION_CAUGHT.</summary>
    internal void DeleteException(uint ucb)
    {
        uint cleanup = GuestMemory.R32(ucb + UcbCleanup);
        if (cleanup == _cleanupStub && cleanup != 0) ExceptionCleanup(ucb);
        else if (cleanup != 0) GuestThread.Current?.CallGuest(cleanup, URC_FOREIGN_EXCEPTION_CAUGHT, ucb);
    }

    // ------------------------------------------------------------------ throw, catch, rethrow

    /// <summary>Throws <paramref name="obj"/> (from __cxa_allocate_exception): returns only to terminate.</summary>
    void Throw(HleContext ctx, uint obj, uint ti, uint dest)
    {
        uint h = obj - 0x80, ucb = h + 0x28;
        GuestMemory.W32(h + 0x0C, dest);
        GuestMemory.W32(h + 0x08, ti);
        GuestMemory.W32(ucb + UcbCleanup, CleanupStub);
        GuestMemory.W32(h + 0x24, 1);
        GuestMemory.W32(h, 1);
        Globals().Uncaught++;
        if (RaiseException(ctx, ucb, Vrs.AtCall(ctx)) == URC_INSTALL_CONTEXT) return;
        CallTerminate(ucb);
    }

    [Hle] void __cxa_throw(HleContext ctx, uint obj, uint ti, uint dest) => Throw(ctx, obj, ti, dest);

    /// <summary>The header standing for a foreign exception in the caught/propagating chains (made once per UCB).</summary>
    uint ForeignHeader(uint chain, uint ucb)
    {
        if (chain != 0 && GuestMemory.R32(chain + 0x28) == GuestMemory.R32(ucb) && GuestMemory.R32(chain + 0x2C) == GuestMemory.R32(ucb + 4)
            && GuestMemory.R32(chain + 8) == ucb)
            return chain;
        uint h = AllocateException(0) - 0x80;
        GuestMemory.Copy(h + 0x28, ucb, 8);
        GuestMemory.W32(h + 0x24, 0);
        GuestMemory.W32(h + 0x18, 0);
        GuestMemory.W32(h + 0x08, ucb);
        GuestMemory.W32(h + 0x1C, 0);
        return h;
    }

    [Hle("__cxa_begin_catch")]
    uint BeginCatch(uint ucb)
    {
        var g = Globals();
        uint adjusted = GuestMemory.R32(ucb + UcbBarrier1), h;
        if (!IsNativeException(ucb)) h = ForeignHeader(g.Caught, ucb);
        else
        {
            GuestMemory.W32(ucb - 4, GuestMemory.R32(ucb - 4) - 1);
            h = ucb - 0x28;
            g.Uncaught--;
        }
        GuestMemory.W32(h + 0x1C, GuestMemory.R32(h + 0x1C) + 1);
        if (g.Caught != h)
        {
            GuestMemory.W32(h + 0x18, g.Caught);
            g.Caught = h;
        }
        return adjusted;
    }

    [Hle]
    void __cxa_end_catch()
    {
        var g = Globals();
        uint h = g.Caught;
        if (h == 0) { TerminateNow(); return; }
        uint count = GuestMemory.R32(h + 0x1C) - 1;
        GuestMemory.W32(h + 0x1C, count);
        if (count != 0) return;
        bool foreign = !IsNativeException(h + 0x28);
        uint propagation = GuestMemory.R32(h + 0x24), ucb = h + 0x28;
        g.Caught = GuestMemory.R32(h + 0x18);
        if (foreign) { ucb = GuestMemory.R32(h + 8); FreeHeader(h); }
        else GuestMemory.W32(h + 0x18, 0);
        if (propagation == 0) DeleteException(ucb);
    }

    [Hle]
    void __cxa_rethrow(HleContext ctx)
    {
        var g = Globals();
        uint h = g.Caught;
        if (h == 0) { g.OneShot = g.Terminate; TerminateNow(); return; }
        GuestMemory.W32(h + 0x24, GuestMemory.R32(h + 0x24) + 1);
        uint ucb = h + 0x28;
        if (!IsNativeException(ucb)) ucb = GuestMemory.R32(h + 8);
        else g.Uncaught++;
        if (RaiseException(ctx, ucb, Vrs.AtCall(ctx)) == URC_INSTALL_CONTEXT) return;
        CallTerminate(ucb);
    }

    /// <summary>__cxa_begin_cleanup(ucb) (ARM descriptors' cleanups): the exception joins the propagating chain.</summary>
    [Hle("__cxa_begin_cleanup")]
    bool BeginCleanup(uint ucb)
    {
        var g = Globals();
        uint h;
        if (!IsNativeException(ucb))
        {
            h = ForeignHeader(g.Propagating, ucb);
            GuestMemory.W32(h + 0x24, GuestMemory.R32(h + 0x24) + 1);
        }
        else h = ucb - 0x28;
        if (GuestMemory.R32(h + 0x24) == 1)
        {
            GuestMemory.W32(h + 0x20, g.Propagating);
            g.Propagating = h;
        }
        return true;
    }

    /// <summary>The UCB of the exception a cleanup was run for, leaving the propagating chain.</summary>
    uint EndCleanup()
    {
        var g = Globals();
        uint h = g.Propagating;
        if (h == 0) { TerminateNow(); return 0; }
        if (!IsNativeException(h + 0x28))
        {
            uint ucb = GuestMemory.R32(h + 8);
            if (GuestMemory.R32(h + 0x24) == 1) { g.Propagating = GuestMemory.R32(h + 0x20); FreeHeader(h); }
            else GuestMemory.W32(h + 0x24, GuestMemory.R32(h + 0x24) - 1);
            return ucb;
        }
        if (GuestMemory.R32(h + 0x24) == 1) g.Propagating = GuestMemory.R32(h + 0x20);
        return h + 0x28;
    }

    /// <summary>__cxa_end_cleanup (end of a cleanup landing pad): _Unwind_Resume with the exception the cleanup ran
    /// for.</summary>
    [Hle]
    void __cxa_end_cleanup(HleContext ctx)
    {
        uint ucb = EndCleanup();
        var v = Vrs.AtCall(ctx);
        v.R[0] = ucb;
        if (ResumeUnwind(ctx, ucb, v) != URC_INSTALL_CONTEXT) abort();
    }

    /// <summary>_Unwind_Resume(ucb), called at the end of a cleanup landing pad.</summary>
    [Hle]
    void _Unwind_Resume(HleContext ctx, uint ucb)
    {
        if (ResumeUnwind(ctx, ucb, Vrs.AtCall(ctx)) != URC_INSTALL_CONTEXT) abort();
    }

    /// <summary>__snc_personality_v0(state, ucb, context) called by a guest unwinder (context in the layout of
    /// <see cref="Vrs"/>); like the module's, it fails a forced unwind (US_FORCE_UNWIND).</summary>
    [Hle]
    int __snc_personality_v0(int state, uint ucb, uint context)
    {
        var v = Vrs.Load(context);
        int r = CommonPersonality(state, ucb, v, 4);
        v.Store(context);
        return r;
    }

    uint _callUnexpectedStub;
    uint CxaCallUnexpectedStub => _callUnexpectedStub != 0 ? _callUnexpectedStub
        : _callUnexpectedStub = ExportStub(0x7C1A0217);

    /// <summary>__cxa_call_unexpected(ucb): the exception becomes caught and the unexpected handler runs; a handler
    /// that returns, or throws (the throw cannot leave the HLE call), ends in terminate.</summary>
    [Hle]
    void __cxa_call_unexpected(uint ucb)
    {
        var g = Globals();
        BeginCatch(ucb);
        g.OneShot = IsNativeException(ucb) ? GuestMemory.R32(ucb - 0x18) : g.Unexpected;
        UnexpectedNow();
    }

    [Hle]
    uint __cxa_current_primary_exception()
    {
        uint h = Globals().Caught;
        if (h == 0 || !IsNativeException(h + 0x28)) return 0;
        if (IsDependentException(h + 0x28)) h = GuestMemory.R32(h) - 0x80;
        __cxa_increment_exception_refcount(h + 0x80);
        return h + 0x80;
    }

    /// <summary>__cxa_rethrow_primary_exception(obj): throws a dependent exception sharing <paramref name="obj"/>.</summary>
    [Hle]
    void __cxa_rethrow_primary_exception(HleContext ctx, uint obj)
    {
        if (obj == 0) return;
        var g = Globals();
        uint d = HeapAlloc(0x80, HeapAlign);
        if (d == 0)
        {
            uint e = g.Emergency;
            if (e != 0 && GuestMemory.Read<byte>(e) == 0) { GuestMemory.Write(e, (byte)1); d = e + 4; }
            else { g.OneShot = g.Terminate; TerminateNow(); return; }
        }
        GuestMemory.Fill(d, 0, 0x80);
        GuestMemory.W32(d, obj);
        __cxa_increment_exception_refcount(obj);
        GuestMemory.W32(d + 8, GuestMemory.R32(obj - 0x78));
        GuestMemory.W32(d + 0x10, g.Unexpected);
        GuestMemory.W32(d + 0x14, g.Terminate);
        ArmCxxDependentClass.CopyTo(GuestMemory.Span(d + 0x28, 8));
        GuestMemory.W32(d + 0x24, 1);
        GuestMemory.W32(d + 0x28 + UcbCleanup, DependentCleanupStub);
        g.Uncaught++;
        if (RaiseException(ctx, d + 0x28, Vrs.AtCall(ctx)) == URC_INSTALL_CONTEXT) return;
        CallTerminate(d + 0x28);
    }

    // ------------------------------------------------------------------ std::string (the exceptions' message)

    // basic_string<char> (0x1C bytes): +0 allocator, +4 the 16-byte buffer or the heap pointer, +0x14 size, +0x18
    // capacity (15 while the text fits the buffer).

    static uint StrData(uint s) => GuestMemory.R32(s + 0x18) >= 16 ? GuestMemory.R32(s + 4) : s + 4;

    void StrAssign(uint s, uint text, uint len)
    {
        GuestMemory.Write(s, (byte)0);
        GuestMemory.W32(s + 0x14, 0);
        GuestMemory.W32(s + 0x18, 15);
        uint buf = s + 4;
        if (len > 15)
        {
            uint res = len | 15;
            buf = New(res + 1, false);
            GuestMemory.W32(s + 4, buf);
            GuestMemory.W32(s + 0x18, res);
        }
        if (len != 0) GuestMemory.Copy(buf, text, len);
        GuestMemory.Write(buf + len, (byte)0);
        GuestMemory.W32(s + 0x14, len);
    }

    void StrFree(uint s)
    {
        if (GuestMemory.R32(s + 0x18) >= 16) free(GuestMemory.R32(s + 4));
        GuestMemory.W32(s + 0x18, 15);
        GuestMemory.W32(s + 0x14, 0);
        GuestMemory.Write(s + 4, (byte)0);
    }

    void StrEos(uint s, uint n)
    {
        GuestMemory.W32(s + 0x14, n);
        GuestMemory.Write(StrData(s) + n, (byte)0);
    }

    /// <summary>basic_string::_Copy: a buffer for <paramref name="newSize"/> characters keeping the first
    /// <paramref name="oldLen"/> (capacity newSize | 15, or 1.5 times the old one when that is larger).</summary>
    void StrCopy(uint s, uint newSize, uint oldLen)
    {
        uint res = GuestMemory.R32(s + 0x18), newRes = newSize | 15;
        if (newRes == 0xFFFFFFFF) newRes = newSize;
        else if (res / 2 > newRes / 3 && res <= 0xFFFFFFFE - res / 2) newRes = res + res / 2;
        uint p = New(newRes + 1, false);
        if (oldLen != 0) GuestMemory.Copy(p, StrData(s), oldLen);
        if (res >= 16) free(GuestMemory.R32(s + 4));
        GuestMemory.W32(s + 4, p);
        GuestMemory.W32(s + 0x18, newRes);
        StrEos(s, oldLen);
    }

    /// <summary>basic_string::_Grow(newsize, trim): room for newsize characters (length_error past max_size);
    /// with trim, a short string goes back to the internal buffer; true when newsize is not 0.</summary>
    [Hle]
    bool _ZNSs5_GrowEjb(HleContext ctx, uint s, uint newSize, bool trim)
    {
        if (newSize > 0xFFFFFFFE) { ThrowNew(ctx, ExcLengthError, text: "string too long"); return false; }
        uint res = GuestMemory.R32(s + 0x18), size = GuestMemory.R32(s + 0x14);
        if (res < newSize) StrCopy(s, newSize, size);
        else if (trim && newSize < 16)
        {
            uint keep = Math.Min(newSize, size);
            if (res >= 16)
            {
                uint p = GuestMemory.R32(s + 4);
                if (keep != 0) GuestMemory.Copy(s + 4, p, keep);
                free(p);
            }
            GuestMemory.W32(s + 0x18, 15);
            StrEos(s, keep);
        }
        else if (newSize == 0) StrEos(s, 0);
        return newSize > 0;
    }

    [Hle] uint _ZNSsC1EPKc(uint s, uint text) { StrAssign(s, text, StrLen(text)); return s; }
    [Hle] uint _ZNSsC1ERKSs(uint s, uint other) { StrAssign(s, StrData(other), GuestMemory.R32(other + 0x14)); return s; }

    /// <summary>basic_string::assign(str, pos, count): the substring (out_of_range when pos is past the end).</summary>
    [Hle]
    uint _ZNSs6assignERKSsjj(HleContext ctx, uint s, uint other, uint pos, uint count)
    {
        uint size = GuestMemory.R32(other + 0x14);
        if (size < pos) { ThrowNew(ctx, ExcOutOfRange, text: "invalid string position"); return s; }
        uint num = Math.Min(count, size - pos);
        if (s == other)
        {
            uint d = StrData(s);
            if (num != 0 && pos != 0) GuestMemory.Copy(d, d + pos, num);
            StrEos(s, num);
        }
        else if (_ZNSs5_GrowEjb(ctx, s, num, false))
        {
            GuestMemory.Copy(StrData(s), StrData(other) + pos, num);
            StrEos(s, num);
        }
        return s;
    }

    // ------------------------------------------------------------------ the standard exception classes

    sealed record ExcClass(string Name, uint Vtable, uint Size, ExcClass? Base, string? What);

    // vptr-only classes (std::exception and its message-less subclasses) and the ones with a std::string at +4
    static readonly ExcClass ExcException = new("exception", 0xBC97F7D6, 4, null, "");
    static readonly ExcClass ExcBadAlloc = new("bad_alloc", 0xA27EFBA3, 4, ExcException, "bad allocation");
    static readonly ExcClass ExcBadArrayNewLength = new("bad_array_new_length", 0x59160432, 4, ExcBadAlloc, "bad allocation");
    static readonly ExcClass ExcBadException = new("bad_exception", 0x4AA9C954, 4, ExcException, "bad exception");
    static readonly ExcClass ExcBadCast = new("bad_cast", 0xAA09FD32, 4, ExcException, "bad cast");
    static readonly ExcClass ExcBadTypeid = new("bad_typeid", 0x76BDD7CC, 4, ExcException, "bad typeid");
    static readonly ExcClass ExcBadFunctionCall = new("bad_function_call", 0xA696AC3E, 4, ExcException, "bad function call");
    static readonly ExcClass ExcBadWeakPtr = new("bad_weak_ptr", 0xD2EA5FFC, 4, ExcException, "bad_weak_ptr");
    static readonly ExcClass ExcLogicError = new("logic_error", 0x82A84E5E, 0x20, ExcException, null);
    static readonly ExcClass ExcDomainError = new("domain_error", 0x80C77E16, 0x20, ExcLogicError, null);
    static readonly ExcClass ExcInvalidArgument = new("invalid_argument", 0x23EEDAF0, 0x20, ExcLogicError, null);
    static readonly ExcClass ExcLengthError = new("length_error", 0x064ADA35, 0x20, ExcLogicError, null);
    static readonly ExcClass ExcOutOfRange = new("out_of_range", 0xDDAE7CBE, 0x20, ExcLogicError, null);
    static readonly ExcClass ExcRuntimeError = new("runtime_error", 0x53F02A18, 0x20, ExcException, null);
    static readonly ExcClass ExcOverflowError = new("overflow_error", 0x177FCCDC, 0x20, ExcRuntimeError, null);
    static readonly ExcClass ExcUnderflowError = new("underflow_error", 0x515AE097, 0x20, ExcRuntimeError, null);
    static readonly ExcClass ExcRangeError = new("range_error", 0x1D583475, 0x20, ExcRuntimeError, null);

    static bool HasMessage(ExcClass c) => c.What == null;

    uint Vptr(ExcClass c) => Rtti.Vptr(c.Vtable);

    /// <summary>A constructor: the vptr (and the message copied from a std::string, a C string or another object).</summary>
    uint Construct(uint obj, ExcClass c, uint stringSrc = 0, uint cstr = 0, uint copyOf = 0)
    {
        GuestMemory.W32(obj, Vptr(c));
        if (HasMessage(c))
        {
            if (copyOf != 0) stringSrc = copyOf + 4;
            if (stringSrc != 0) StrAssign(obj + 4, StrData(stringSrc), GuestMemory.R32(stringSrc + 0x14));
            else StrAssign(obj + 4, cstr, cstr != 0 ? StrLen(cstr) : 0);
        }
        return obj;
    }

    /// <summary>A complete or base destructor: each class's vptr in turn down to std::exception, freeing the message
    /// at logic_error/runtime_error.</summary>
    uint Destruct(uint obj, ExcClass c)
    {
        for (var k = c; k != null; k = k.Base)
        {
            GuestMemory.W32(obj, Vptr(k));
            if (HasMessage(k) && k.Base == ExcException) StrFree(obj + 4);
        }
        return obj;
    }

    uint DeletingDtor(uint obj, ExcClass c)
    {
        Destruct(obj, c);
        free(obj);
        return obj;
    }

    uint What(uint obj, ExcClass c) => c.What != null ? ConstString(c.What) : StrData(obj + 4);

    /// <summary>_Doraise: throws a copy of the object (the most derived class's own _Doraise).</summary>
    void Doraise(HleContext ctx, uint obj, ExcClass c)
    {
        uint copy = AllocateException(c.Size);
        Construct(copy, c, copyOf: obj);
        Throw(ctx, copy, TiOf(c), ExportStubOfSlot(c, 0));
    }

    /// <summary>The class's typeinfo (its vtable's slot -1).</summary>
    uint TiOf(ExcClass c) => GuestMemory.R32(Rtti.Address[c.Vtable] + 4);

    /// <summary>The <paramref name="slot"/>-th virtual function of the class's vtable (0: complete destructor).</summary>
    uint ExportStubOfSlot(ExcClass c, int slot) => GuestMemory.R32(Rtti.Address[c.Vtable] + 8 + (uint)slot * 4);

    /// <summary>Throws a new exception of class <paramref name="c"/> with a C-string message.</summary>
    void ThrowNew(HleContext ctx, ExcClass c, uint cstr = 0, string? text = null)
    {
        uint obj = AllocateException(c.Size);
        if (text != null) cstr = ConstString(text);
        Construct(obj, c, cstr: cstr);
        Throw(ctx, obj, TiOf(c), ExportStubOfSlot(c, 0));
    }

    uint _raiseHandler;

    [Hle] uint _ZNSt9exception18_Set_raise_handlerEPFvRKS_E(uint h) { uint old = _raiseHandler; _raiseHandler = h; return old; }

    /// <summary>exception::_Raise: the raise handler, the object's _Doraise, then a std::exception is thrown.</summary>
    [Hle]
    void _ZNKSt9exception6_RaiseEv(HleContext ctx, uint obj)
    {
        if (_raiseHandler != 0) GuestThread.Current?.CallGuest(_raiseHandler, obj);
        uint doraise = GuestMemory.R32(GuestMemory.R32(obj) + 0xC);
        var cls = AllExc.FirstOrDefault(k => ExportStubOfSlot(k, 3) == doraise);
        if (cls != null && cls != ExcException && cls != ExcBadFunctionCall && cls != ExcBadWeakPtr) { Doraise(ctx, obj, DoraiseClassOf(cls)); return; }
        if (cls == null) GuestThread.Current?.CallGuest(doraise, obj);
        ThrowNew(ctx, ExcException);
    }

    static readonly ExcClass[] AllExc =
    [
        ExcException, ExcBadAlloc, ExcBadArrayNewLength, ExcBadException, ExcBadCast, ExcBadTypeid, ExcBadFunctionCall,
        ExcBadWeakPtr, ExcLogicError, ExcDomainError, ExcInvalidArgument, ExcLengthError, ExcOutOfRange, ExcRuntimeError,
        ExcOverflowError, ExcUnderflowError, ExcRangeError,
    ];

    /// <summary>The class whose copy a _Doraise throws: bad_array_new_length uses bad_alloc's.</summary>
    static ExcClass DoraiseClassOf(ExcClass c) => c == ExcBadArrayNewLength ? ExcBadAlloc : c;

    // std::exception
    [Hle("_ZNSt9exceptionC1Ev")] [Hle("_ZNSt9exceptionC2Ev")] uint ExceptionCtor(uint o) => Construct(o, ExcException);
    [Hle("_ZNSt9exceptionC1ERKS_")] [Hle("_ZNSt9exceptionC2ERKS_")] uint ExceptionCopy(uint o, uint other) => Construct(o, ExcException);
    [Hle("_ZNSt9exceptionD1Ev")] [Hle("_ZNSt9exceptionD2Ev")] uint ExceptionDtor(uint o) => Destruct(o, ExcException);
    [Hle] uint _ZNSt9exceptionD0Ev(uint o) => DeletingDtor(o, ExcException);

    // std::bad_alloc
    [Hle("_ZNSt9bad_allocC1Ev")] [Hle("_ZNSt9bad_allocC2Ev")] uint BadAllocCtor(uint o) => Construct(o, ExcBadAlloc);
    [Hle("_ZNSt9bad_allocC1EPKc")] [Hle("_ZNSt9bad_allocC2EPKc")] uint BadAllocCtorMsg(uint o, uint msg) => Construct(o, ExcBadAlloc);
    [Hle("_ZNSt9bad_allocC1ERKS_")] [Hle("_ZNSt9bad_allocC2ERKS_")] uint BadAllocCopy(uint o, uint other) => Construct(o, ExcBadAlloc);
    [Hle("_ZNSt9bad_allocD1Ev")] [Hle("_ZNSt9bad_allocD2Ev")] uint BadAllocDtor(uint o) => Destruct(o, ExcBadAlloc);
    [Hle] uint _ZNSt9bad_allocD0Ev(uint o) => DeletingDtor(o, ExcBadAlloc);
    [Hle] uint _ZNSt9bad_allocaSERKS_(uint o, uint other) => o;
    [Hle] void _ZNKSt9bad_alloc8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcBadAlloc);
    [Hle] uint _ZNSt20bad_array_new_lengthD1Ev(uint o) => Destruct(o, ExcBadArrayNewLength);

    // std::bad_exception
    [Hle("_ZNSt13bad_exceptionC1Ev")] [Hle("_ZNSt13bad_exceptionC2Ev")] uint BadExceptionCtor(uint o) => Construct(o, ExcBadException);
    [Hle("_ZNSt13bad_exceptionC1ERKS_")] [Hle("_ZNSt13bad_exceptionC2ERKS_")] uint BadExceptionCopy(uint o, uint other) => Construct(o, ExcBadException);
    [Hle("_ZNSt13bad_exceptionD1Ev")] [Hle("_ZNSt13bad_exceptionD2Ev")] uint BadExceptionDtor(uint o) => Destruct(o, ExcBadException);
    [Hle] uint _ZNSt13bad_exceptionD0Ev(uint o) => DeletingDtor(o, ExcBadException);
    [Hle] void _ZNKSt13bad_exception8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcBadException);

    // std::bad_cast
    [Hle("_ZNSt8bad_castC1Ev")] [Hle("_ZNSt8bad_castC2Ev")] uint BadCastCtor(uint o) => Construct(o, ExcBadCast);
    [Hle("_ZNSt8bad_castC1ERKS_")] [Hle("_ZNSt8bad_castC2ERKS_")] uint BadCastCopy(uint o, uint other) => Construct(o, ExcBadCast);
    [Hle("_ZNSt8bad_castD1Ev")] [Hle("_ZNSt8bad_castD2Ev")] uint BadCastDtor(uint o) => Destruct(o, ExcBadCast);
    [Hle] uint _ZNSt8bad_castD0Ev(uint o) => DeletingDtor(o, ExcBadCast);
    [Hle] uint _ZNSt8bad_castaSERKS_(uint o, uint other) => o;
    [Hle] void _ZNKSt8bad_cast8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcBadCast);

    // std::bad_typeid
    [Hle("_ZNSt10bad_typeidC1Ev")] [Hle("_ZNSt10bad_typeidC2Ev")] uint BadTypeidCtor(uint o) => Construct(o, ExcBadTypeid);
    [Hle("_ZNSt10bad_typeidC1ERKS_")] [Hle("_ZNSt10bad_typeidC2ERKS_")] uint BadTypeidCopy(uint o, uint other) => Construct(o, ExcBadTypeid);
    [Hle("_ZNSt10bad_typeidD1Ev")] [Hle("_ZNSt10bad_typeidD2Ev")] uint BadTypeidDtor(uint o) => Destruct(o, ExcBadTypeid);
    [Hle] uint _ZNSt10bad_typeidD0Ev(uint o) => DeletingDtor(o, ExcBadTypeid);
    [Hle] uint _ZNSt10bad_typeidaSERKS_(uint o, uint other) => o;
    [Hle] void _ZNKSt10bad_typeid8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcBadTypeid);

    [Hle] uint _ZNSt17bad_function_callD1Ev(uint o) => Destruct(o, ExcBadFunctionCall);
    [Hle] uint _ZNSt12bad_weak_ptrD1Ev(uint o) => Destruct(o, ExcBadWeakPtr);

    // std::logic_error and subclasses
    [Hle] uint _ZNSt11logic_errorC2ERKSs(uint o, uint s) => Construct(o, ExcLogicError, stringSrc: s);
    [Hle] uint _ZNSt11logic_errorC1ERKS_(uint o, uint other) => Construct(o, ExcLogicError, copyOf: other);
    [Hle("_ZNSt11logic_errorD1Ev")] [Hle("_ZNSt11logic_errorD2Ev")] uint LogicErrorDtor(uint o) => Destruct(o, ExcLogicError);
    [Hle] uint _ZNSt11logic_errorD0Ev(uint o) => DeletingDtor(o, ExcLogicError);
    [Hle] uint _ZNKSt11logic_error4whatEv(uint o) => What(o, ExcLogicError);
    [Hle] void _ZNKSt11logic_error8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcLogicError);

    [Hle] uint _ZNSt12domain_errorC1ERKS_(uint o, uint other) => Construct(o, ExcDomainError, copyOf: other);
    [Hle("_ZNSt12domain_errorD1Ev")] [Hle("_ZNSt12domain_errorD2Ev")] uint DomainErrorDtor(uint o) => Destruct(o, ExcDomainError);
    [Hle] uint _ZNSt12domain_errorD0Ev(uint o) => DeletingDtor(o, ExcDomainError);
    [Hle] void _ZNKSt12domain_error8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcDomainError);

    [Hle] uint _ZNSt16invalid_argumentC1ERKS_(uint o, uint other) => Construct(o, ExcInvalidArgument, copyOf: other);
    [Hle("_ZNSt16invalid_argumentD1Ev")] [Hle("_ZNSt16invalid_argumentD2Ev")] uint InvalidArgumentDtor(uint o) => Destruct(o, ExcInvalidArgument);
    [Hle] uint _ZNSt16invalid_argumentD0Ev(uint o) => DeletingDtor(o, ExcInvalidArgument);
    [Hle] void _ZNKSt16invalid_argument8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcInvalidArgument);

    [Hle] uint _ZNSt12length_errorC1ERKSs(uint o, uint s) => Construct(o, ExcLengthError, stringSrc: s);
    [Hle] uint _ZNSt12length_errorC1ERKS_(uint o, uint other) => Construct(o, ExcLengthError, copyOf: other);
    [Hle("_ZNSt12length_errorD1Ev")] [Hle("_ZNSt12length_errorD2Ev")] uint LengthErrorDtor(uint o) => Destruct(o, ExcLengthError);
    [Hle] uint _ZNSt12length_errorD0Ev(uint o) => DeletingDtor(o, ExcLengthError);
    [Hle] void _ZNKSt12length_error8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcLengthError);

    [Hle] uint _ZNSt12out_of_rangeC1ERKS_(uint o, uint other) => Construct(o, ExcOutOfRange, copyOf: other);
    [Hle("_ZNSt12out_of_rangeD1Ev")] [Hle("_ZNSt12out_of_rangeD2Ev")] uint OutOfRangeDtor(uint o) => Destruct(o, ExcOutOfRange);
    [Hle] uint _ZNSt12out_of_rangeD0Ev(uint o) => DeletingDtor(o, ExcOutOfRange);
    [Hle] void _ZNKSt12out_of_range8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcOutOfRange);

    // std::runtime_error and subclasses
    [Hle] uint _ZNSt13runtime_errorC1ERKS_(uint o, uint other) => Construct(o, ExcRuntimeError, copyOf: other);
    [Hle("_ZNSt13runtime_errorD1Ev")] [Hle("_ZNSt13runtime_errorD2Ev")] uint RuntimeErrorDtor(uint o) => Destruct(o, ExcRuntimeError);
    [Hle] uint _ZNSt13runtime_errorD0Ev(uint o) => DeletingDtor(o, ExcRuntimeError);
    [Hle] uint _ZNKSt13runtime_error4whatEv(uint o) => What(o, ExcRuntimeError);
    [Hle] void _ZNKSt13runtime_error8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcRuntimeError);

    [Hle] uint _ZNSt14overflow_errorC1ERKS_(uint o, uint other) => Construct(o, ExcOverflowError, copyOf: other);
    [Hle("_ZNSt14overflow_errorD1Ev")] [Hle("_ZNSt14overflow_errorD2Ev")] uint OverflowErrorDtor(uint o) => Destruct(o, ExcOverflowError);
    [Hle] uint _ZNSt14overflow_errorD0Ev(uint o) => DeletingDtor(o, ExcOverflowError);
    [Hle] void _ZNKSt14overflow_error8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcOverflowError);

    [Hle] uint _ZNSt15underflow_errorC1ERKS_(uint o, uint other) => Construct(o, ExcUnderflowError, copyOf: other);
    [Hle("_ZNSt15underflow_errorD1Ev")] [Hle("_ZNSt15underflow_errorD2Ev")] uint UnderflowErrorDtor(uint o) => Destruct(o, ExcUnderflowError);
    [Hle] uint _ZNSt15underflow_errorD0Ev(uint o) => DeletingDtor(o, ExcUnderflowError);
    [Hle] void _ZNKSt15underflow_error8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcUnderflowError);

    [Hle] uint _ZNSt11range_errorC1ERKS_(uint o, uint other) => Construct(o, ExcRangeError, copyOf: other);
    [Hle("_ZNSt11range_errorD1Ev")] [Hle("_ZNSt11range_errorD2Ev")] uint RangeErrorDtor(uint o) => Destruct(o, ExcRangeError);
    [Hle] uint _ZNSt11range_errorD0Ev(uint o) => DeletingDtor(o, ExcRangeError);
    [Hle] void _ZNKSt11range_error8_DoraiseEv(HleContext ctx, uint o) => Doraise(ctx, o, ExcRangeError);

    // the __cxxabiv1 type_info classes: their destructors (type_info objects are never destroyed by the program, the
    // module still exports them): complete/base set the class's vptr and return, deleting also frees
    [Hle("_ZN10__cxxabiv117__class_type_infoD1Ev")] [Hle("_ZN10__cxxabiv117__class_type_infoD2Ev")]
    [Hle("_ZN10__cxxabiv120__si_class_type_infoD1Ev")] [Hle("_ZN10__cxxabiv120__si_class_type_infoD2Ev")]
    [Hle("_ZN10__cxxabiv121__vmi_class_type_infoD1Ev")] [Hle("_ZN10__cxxabiv121__vmi_class_type_infoD2Ev")]
    [Hle("_ZN10__cxxabiv123__fundamental_type_infoD1Ev")] [Hle("_ZN10__cxxabiv123__fundamental_type_infoD2Ev")]
    [Hle("_ZN10__cxxabiv117__array_type_infoD1Ev")] [Hle("_ZN10__cxxabiv117__array_type_infoD2Ev")]
    [Hle("_ZN10__cxxabiv120__function_type_infoD1Ev")] [Hle("_ZN10__cxxabiv120__function_type_infoD2Ev")]
    [Hle("_ZN10__cxxabiv116__enum_type_infoD1Ev")] [Hle("_ZN10__cxxabiv116__enum_type_infoD2Ev")]
    [Hle("_ZN10__cxxabiv117__pbase_type_infoD1Ev")] [Hle("_ZN10__cxxabiv117__pbase_type_infoD2Ev")]
    [Hle("_ZN10__cxxabiv119__pointer_type_infoD1Ev")] [Hle("_ZN10__cxxabiv119__pointer_type_infoD2Ev")]
    [Hle("_ZN10__cxxabiv129__pointer_to_member_type_infoD1Ev")] [Hle("_ZN10__cxxabiv129__pointer_to_member_type_infoD2Ev")]
    uint TypeInfoDtor(uint o) => o;

    [Hle("_ZN10__cxxabiv117__class_type_infoD0Ev")] [Hle("_ZN10__cxxabiv120__si_class_type_infoD0Ev")]
    [Hle("_ZN10__cxxabiv121__vmi_class_type_infoD0Ev")] [Hle("_ZN10__cxxabiv123__fundamental_type_infoD0Ev")]
    [Hle("_ZN10__cxxabiv117__array_type_infoD0Ev")] [Hle("_ZN10__cxxabiv120__function_type_infoD0Ev")]
    [Hle("_ZN10__cxxabiv116__enum_type_infoD0Ev")] [Hle("_ZN10__cxxabiv117__pbase_type_infoD0Ev")]
    [Hle("_ZN10__cxxabiv119__pointer_type_infoD0Ev")] [Hle("_ZN10__cxxabiv129__pointer_to_member_type_infoD0Ev")]
    uint TypeInfoDeletingDtor(uint o) { free(o); return o; }

    // ------------------------------------------------------------------ the _X* throw helpers

    [Hle] void _ZSt11_Xbad_allocv(HleContext ctx) => ThrowNew(ctx, ExcBadAlloc);
    [Hle] void _ZSt19_Xbad_function_callv(HleContext ctx) => ThrowNew(ctx, ExcBadFunctionCall);
    [Hle] void _ZSt14_Xlength_errorPKc(HleContext ctx, uint msg) => ThrowNew(ctx, ExcLengthError, msg);
    [Hle] void _ZSt14_Xout_of_rangePKc(HleContext ctx, uint msg) => ThrowNew(ctx, ExcOutOfRange, msg);
    [Hle] void _ZSt18_Xinvalid_argumentPKc(HleContext ctx, uint msg) => ThrowNew(ctx, ExcInvalidArgument, msg);
    [Hle] void _ZSt16_Xoverflow_errorPKc(HleContext ctx, uint msg) => ThrowNew(ctx, ExcOverflowError, msg);
    [Hle] void _ZSt15_Xruntime_errorPKc(HleContext ctx, uint msg) => ThrowNew(ctx, ExcRuntimeError, msg);
    [Hle] void _ZNKSt12_String_base5_XlenEv(HleContext ctx, uint o) => ThrowNew(ctx, ExcLengthError, text: "string too long");
    [Hle] void _ZNKSt12_String_base5_XranEv(HleContext ctx, uint o) => ThrowNew(ctx, ExcOutOfRange, text: "invalid string position");
}
