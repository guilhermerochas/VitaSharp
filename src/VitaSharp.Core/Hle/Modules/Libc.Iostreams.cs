using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceLibstdcxx iostreams of libc.suprx 3.74 (abridged Dinkumware, no locale): the rest of ios_base, basic_ios,
// basic_istream/basic_ostream/basic_iostream destructors and their thunks, the basic_ostream constructor, and the
// standard streams that ios_base::Init / _Winit build on the first reference: three basic_filebuf over stdin, stdout
// and stderr, then cin (tied to cout), cout, cerr (unitbuf, tied to cout) and clog (tied to cout), each registered
// with _Addstd; wcin/wcout/wcerr/wclog the same over basic_filebuf<wchar_t>. The last reference flushes cout, cerr
// and clog. The reference counts start at -1 and are module variables, as are the stream objects.
// basic_ios<E> (0x30 bytes): ios_base (0x24), +0x24 _Mystrbuf, +0x28 _Tiestr, +0x2C _Fillch. basic_istream: +0
// vptr, +4 _Chcount, +8 basic_ios (virtual base); basic_ostream: +0 vptr, +4 basic_ios; basic_iostream: the istream
// part, +8 the ostream vptr, +0xC basic_ios.
public sealed partial class Libc
{
    const uint IosStrbuf = 0x24, IosTie = 0x28, IosFill = 0x2C, IstreamSize = 0x38, OstreamSize = 0x34;
    const uint VtIosC = 0x147996ED, VtIosW = 0xCC03F184, VtIosBase = 0xD58C5F52;
    const uint TiIstreamC = 0xE302D51A, TiOstreamC = 0xF9426257, TiIostreamC = 0xD7A9284F, TiIstreamW = 0x1582929A,
        TiOstreamW = 0xF4062B77;

    // ------------------------------------------------------------------ vtable groups the module does not export

    readonly Dictionary<string, uint> _vtGroups = new();

    /// <summary>
    /// A vtable group in guest memory (made once per key): hex constants, Fnid (an SceLibstdcxx export of the HLE)
    /// and Tnid (an exported typeinfo). Returns its start.
    /// </summary>
    uint VtGroup(string key, string words)
    {
        lock (_rttiLock)
        {
            if (_vtGroups.TryGetValue(key, out uint a)) return a;
            var w = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            a = StaticAlloc((uint)w.Length * 4, 4);
            for (int i = 0; i < w.Length; i++)
            {
                uint v = w[i][0] switch
                {
                    'F' => ExportStub(System.Convert.ToUInt32(w[i][1..], 16)),
                    'T' => Rtti.Ti(System.Convert.ToUInt32(w[i][1..], 16)),
                    _ => System.Convert.ToUInt32(w[i], 16),
                };
                GuestMemory.W32(a + (uint)i * 4, v);
            }
            return _vtGroups[key] = a;
        }
    }

    // istream: { 8, 0, Si, D1, D0 } then { -8, -8, Si, virtual-thunk D1, D0 }; VTT = the two address points
    (uint Prim, uint Sec) IstreamVtt(bool w)
    {
        uint g = w
            ? VtGroup("istream<wchar_t>", "8 0 T1582929A FB58839C5 FFFFA683E FFFFFFF8 FFFFFFF8 T1582929A FC3718A8D F10E7966F")
            : VtGroup("istream<char>", "8 0 TE302D51A FD089A7C9 F9BA8786E FFFFFFF8 FFFFFFF8 TE302D51A FC46CA5C9 F7CC8951D");
        return (g + 0xC, g + 0x20);
    }

    (uint Prim, uint Sec) OstreamVtt(bool w)
    {
        uint g = w
            ? VtGroup("ostream<wchar_t>", "4 0 TF4062B77 F9B831B60 F0D74F56E FFFFFFFC FFFFFFFC TF4062B77 FB54EC1E5 F159261D1")
            : VtGroup("ostream<char>", "4 0 TF9426257 FEF21C386 F6489E51D FFFFFFFC FFFFFFFC TF9426257 F41DBA7E0 F699E7717");
        return (g + 0xC, g + 0x20);
    }

    /// <summary>basic_iostream&lt;char&gt;'s group and construction vtables; VTT: Sd, istream-in-Sd, its basic_ios,
    /// ostream-in-Sd, its basic_ios, Sd's basic_ios, Sd's ostream part.</summary>
    uint[] IostreamVtt()
    {
        uint g = VtGroup("iostream<char>",
            "C 0 TD7A9284F F6C2D3707 F2DA9FCEC 4 FFFFFFF8 TD7A9284F FABFBB0B4 F4A804953 FFFFFFF4 FFFFFFF4 TD7A9284F F1081224F FE4BD5E6A " +
            "C 0 TE302D51A FD089A7C9 F9BA8786E FFFFFFF4 FFFFFFF4 TE302D51A FC46CA5C9 F7CC8951D " +
            "4 0 TF9426257 FEF21C386 F6489E51D FFFFFFFC FFFFFFFC TF9426257 F41DBA7E0 F699E7717");
        return [g + 0xC, g + 0x48, g + 0x5C, g + 0x70, g + 0x84, g + 0x34, g + 0x20];
    }

    static uint VbaseOffset(uint vptr) => GuestMemory.R32(vptr - 12);

    // ------------------------------------------------------------------ ios_base: iword/pword, callbacks, copyfmt

    /// <summary>_Findarr(idx): the iword/pword node of idx (+0 next, +4 index, +8 long, +0xC pointer), else the first
    /// unused one takes the index, else a new node goes first in the list.</summary>
    [Hle("_ZNSt8ios_base8_FindarrEi")]
    uint IosFindarr(uint ios, int idx)
    {
        uint free0 = 0;
        for (uint n = GuestMemory.R32(ios + IosArr); n != 0; n = GuestMemory.R32(n))
        {
            if ((int)GuestMemory.R32(n + 4) == idx) return n;
            if (free0 == 0 && GuestMemory.R32(n + 8) == 0 && GuestMemory.R32(n + 0xC) == 0) free0 = n;
        }
        if (free0 != 0) { GuestMemory.W32(free0 + 4, (uint)idx); return free0; }
        uint node = malloc(0x10);
        if (node != 0)
        {
            GuestMemory.W32(node, GuestMemory.R32(ios + IosArr));
            GuestMemory.W32(node + 4, (uint)idx);
            GuestMemory.W32(node + 8, 0);
            GuestMemory.W32(node + 0xC, 0);
        }
        GuestMemory.W32(ios + IosArr, node);
        return node;
    }

    /// <summary>register_callback(fn, idx): a node (+0 next, +4 index, +8 function) first in the list.</summary>
    [Hle("_ZNSt8ios_base17register_callbackEPFvNS_5eventERS_iEi")]
    void IosRegisterCallback(uint ios, uint fn, int idx)
    {
        uint node = malloc(0xC);
        if (node != 0)
        {
            GuestMemory.W32(node, GuestMemory.R32(ios + IosCalls));
            GuestMemory.W32(node + 4, (uint)idx);
            GuestMemory.W32(node + 8, fn);
        }
        GuestMemory.W32(ios + IosCalls, node);
    }

    /// <summary>_Callfns(ev): fn(ev, *this, index) for every callback, in list order.</summary>
    [Hle("_ZNSt8ios_base8_CallfnsENS_5eventE")]
    void IosCallfns(uint ios, int ev)
    {
        for (uint n = GuestMemory.R32(ios + IosCalls); n != 0; n = GuestMemory.R32(n))
            GuestThread.Current?.CallGuest(GuestMemory.R32(n + 8), (uint)ev, ios, GuestMemory.R32(n + 4));
    }

    /// <summary>_Tidy: the erase_event callbacks, then both lists are freed.</summary>
    [Hle("_ZNSt8ios_base5_TidyEv")]
    void IosTidy(uint ios)
    {
        IosCallfns(ios, 0);
        for (uint n = GuestMemory.R32(ios + IosArr); n != 0;) { uint next = GuestMemory.R32(n); free(n); n = next; }
        GuestMemory.W32(ios + IosArr, 0);
        for (uint n = GuestMemory.R32(ios + IosCalls); n != 0;) { uint next = GuestMemory.R32(n); free(n); n = next; }
        GuestMemory.W32(ios + IosCalls, 0);
    }

    /// <summary>copyfmt(rhs): _Tidy, then the format flags, precision, width, the non-empty iword/pword entries and the
    /// callbacks (pushed in rhs's order, so reversed); copyfmt_event callbacks; exceptions(rhs.exceptions()).</summary>
    [Hle("_ZNSt8ios_base7copyfmtERKS_")]
    uint IosCopyfmt(HleContext ctx, uint ios, uint rhs)
    {
        if (ios == rhs) return ios;
        IosTidy(ios);
        GuestMemory.W32(ios + IosFmtfl, GuestMemory.R32(rhs + IosFmtfl));
        GuestMemory.W32(ios + IosPrec, GuestMemory.R32(rhs + IosPrec));
        GuestMemory.W32(ios + IosWide, GuestMemory.R32(rhs + IosWide));
        GuestMemory.W32(ios + IosArr, 0);
        for (uint n = GuestMemory.R32(rhs + IosArr); n != 0; n = GuestMemory.R32(n))
        {
            uint lo = GuestMemory.R32(n + 8), vp = GuestMemory.R32(n + 0xC);
            if (lo == 0 && vp == 0) continue;
            int idx = (int)GuestMemory.R32(n + 4);
            uint a = IosFindarr(ios, idx);
            if (a != 0) GuestMemory.W32(a + 8, lo);
            a = IosFindarr(ios, idx);
            if (a != 0) GuestMemory.W32(a + 0xC, vp);
        }
        for (uint n = GuestMemory.R32(rhs + IosCalls); n != 0; n = GuestMemory.R32(n))
            IosRegisterCallback(ios, GuestMemory.R32(n + 8), (int)GuestMemory.R32(n + 4));
        IosCallfns(ios, 1);
        GuestMemory.W32(ios + IosExcept, GuestMemory.R32(rhs + IosExcept) & 0x17);
        IosClear(ctx, ios, GuestMemory.R32(ios + IosState), false);
        return ios;
    }

    // ------------------------------------------------------------------ basic_ios

    uint BasicIosDtor(uint ios, bool w)
    {
        GuestMemory.W32(ios, Rtti.Vptr(w ? VtIosW : VtIosC));
        return IosBaseDtor(ios);
    }

    [Hle("_ZNSt9basic_iosIcSt11char_traitsIcEED1Ev")] uint IosDtorC(uint ios) => BasicIosDtor(ios, false);
    [Hle("_ZNSt9basic_iosIwSt11char_traitsIwEED1Ev")] uint IosDtorW(uint ios) => BasicIosDtor(ios, true);
    [Hle("_ZNSt9basic_iosIcSt11char_traitsIcEED0Ev")] uint IosDelC(uint ios) { BasicIosDtor(ios, false); free(ios); return ios; }
    [Hle("_ZNSt9basic_iosIwSt11char_traitsIwEED0Ev")] uint IosDelW(uint ios) { BasicIosDtor(ios, true); free(ios); return ios; }

    /// <summary>basic_ios::init(sb, isstd) as the inline constructors run it: ios_base::_Init, the buffer, no tie, fill
    /// widen(' '); no buffer sets badbit; a standard stream is registered with _Addstd.</summary>
    void BasicIosInit(HleContext ctx, uint ios, uint sb, bool isstd, bool w)
    {
        _ZNSt8ios_base5_InitEv(ctx, ios);
        GuestMemory.W32(ios + IosStrbuf, sb);
        GuestMemory.W32(ios + IosTie, 0);
        WrE(ios + IosFill, ' ', w);
        if (sb == 0) IosClear(ctx, ios, GuestMemory.R32(ios + IosState) | 4, false);
        if (isstd) IosAddstd(ios);
    }

    // ------------------------------------------------------------------ basic_istream / basic_ostream / basic_iostream

    uint IstreamDtor(uint obj, bool w)
    {
        var (prim, sec) = IstreamVtt(w);
        GuestMemory.W32(obj, prim);
        GuestMemory.W32(obj + VbaseOffset(prim), sec);
        GuestMemory.W32(obj + 8, Rtti.Vptr(w ? VtIosW : VtIosC));
        IosBaseDtor(obj + 8);
        return obj;
    }

    uint OstreamDtor(uint obj, bool w)
    {
        var (prim, sec) = OstreamVtt(w);
        GuestMemory.W32(obj, prim);
        GuestMemory.W32(obj + VbaseOffset(prim), sec);
        GuestMemory.W32(obj + 4, Rtti.Vptr(w ? VtIosW : VtIosC));
        IosBaseDtor(obj + 4);
        return obj;
    }

    static uint VirtualThunkThis(uint obj) => obj + VbaseOffset(GuestMemory.R32(obj));

    [Hle("_ZNSiD1Ev")] uint IstreamDtorC(uint obj) => IstreamDtor(obj, false);
    [Hle("_ZNSiD0Ev")] uint IstreamDelC(uint obj) { IstreamDtor(obj, false); free(obj); return obj; }
    [Hle("_ZTv0_n12_NSiD1Ev")] uint IstreamThunkDtorC(uint obj) => IstreamDtorC(VirtualThunkThis(obj));
    [Hle("_ZTv0_n12_NSiD0Ev")] uint IstreamThunkDelC(uint obj) => IstreamDelC(VirtualThunkThis(obj));
    [Hle("_ZNSt13basic_istreamIwSt11char_traitsIwEED1Ev")] uint IstreamDtorW(uint obj) => IstreamDtor(obj, true);
    [Hle("_ZNSt13basic_istreamIwSt11char_traitsIwEED0Ev")] uint IstreamDelW(uint obj) { IstreamDtor(obj, true); free(obj); return obj; }
    [Hle("_ZTv0_n12_NSt13basic_istreamIwSt11char_traitsIwEED1Ev")] uint IstreamThunkDtorW(uint obj) => IstreamDtorW(VirtualThunkThis(obj));
    [Hle("_ZTv0_n12_NSt13basic_istreamIwSt11char_traitsIwEED0Ev")] uint IstreamThunkDelW(uint obj) => IstreamDelW(VirtualThunkThis(obj));

    [Hle("_ZNSoD1Ev")] uint OstreamDtorC(uint obj) => OstreamDtor(obj, false);
    [Hle("_ZNSoD0Ev")] uint OstreamDelC(uint obj) { OstreamDtor(obj, false); free(obj); return obj; }
    [Hle("_ZTv0_n12_NSoD1Ev")] uint OstreamThunkDtorC(uint obj) => OstreamDtorC(VirtualThunkThis(obj));
    [Hle("_ZTv0_n12_NSoD0Ev")] uint OstreamThunkDelC(uint obj) => OstreamDelC(VirtualThunkThis(obj));
    [Hle("_ZNSt13basic_ostreamIwSt11char_traitsIwEED1Ev")] uint OstreamDtorW(uint obj) => OstreamDtor(obj, true);
    [Hle("_ZNSt13basic_ostreamIwSt11char_traitsIwEED0Ev")] uint OstreamDelW(uint obj) { OstreamDtor(obj, true); free(obj); return obj; }
    [Hle("_ZTv0_n12_NSt13basic_ostreamIwSt11char_traitsIwEED1Ev")] uint OstreamThunkDtorW(uint obj) => OstreamDtorW(VirtualThunkThis(obj));
    [Hle("_ZTv0_n12_NSt13basic_ostreamIwSt11char_traitsIwEED0Ev")] uint OstreamThunkDelW(uint obj) => OstreamDelW(VirtualThunkThis(obj));

    /// <summary>basic_ostream(sb, isstd): the vtables, then basic_ios::init.</summary>
    uint OstreamCtor(HleContext ctx, uint obj, uint sb, bool isstd, bool w)
    {
        var (prim, sec) = OstreamVtt(w);
        GuestMemory.W32(obj, prim);
        GuestMemory.W32(obj + 4, Rtti.Vptr(w ? VtIosW : VtIosC));
        GuestMemory.W32(obj + VbaseOffset(prim), sec);
        BasicIosInit(ctx, obj + VbaseOffset(prim), sb, isstd, w);
        return obj;
    }

    [Hle("_ZNSoC1EPSt15basic_streambufIcSt11char_traitsIcEEb")]
    uint OstreamCtorC(HleContext ctx, uint obj, uint sb, bool isstd) => OstreamCtor(ctx, obj, sb, isstd, false);

    [Hle("_ZNSt13basic_ostreamIwSt11char_traitsIwEEC1EPSt15basic_streambufIwS1_Eb")]
    uint OstreamCtorW(HleContext ctx, uint obj, uint sb, bool isstd) => OstreamCtor(ctx, obj, sb, isstd, true);

    /// <summary>The inline basic_istream(sb, isstd) constructor (no count read yet).</summary>
    uint IstreamCtor(HleContext ctx, uint obj, uint sb, bool isstd, bool w)
    {
        var (prim, sec) = IstreamVtt(w);
        GuestMemory.W32(obj, prim);
        GuestMemory.W32(obj + 8, Rtti.Vptr(w ? VtIosW : VtIosC));
        GuestMemory.W32(obj + VbaseOffset(prim), sec);
        GuestMemory.W32(obj + 4, 0);
        BasicIosInit(ctx, obj + VbaseOffset(prim), sb, isstd, w);
        return obj;
    }

    uint IostreamDtor(uint obj)
    {
        var v = IostreamVtt();
        GuestMemory.W32(obj, v[0]);
        GuestMemory.W32(obj + VbaseOffset(v[0]), v[5]);
        GuestMemory.W32(obj + 8, v[6]);
        GuestMemory.W32(obj + 8, v[3]);
        GuestMemory.W32(obj + 8 + VbaseOffset(v[3]), v[4]);
        GuestMemory.W32(obj, v[1]);
        GuestMemory.W32(obj + VbaseOffset(v[1]), v[2]);
        GuestMemory.W32(obj + 0xC, Rtti.Vptr(VtIosC));
        IosBaseDtor(obj + 0xC);
        return obj;
    }

    [Hle("_ZNSdD1Ev")] uint IostreamDtorC(uint obj) => IostreamDtor(obj);
    [Hle("_ZNSdD0Ev")] uint IostreamDelC(uint obj) { IostreamDtor(obj); free(obj); return obj; }
    [Hle("_ZThn8_NSdD1Ev")] uint IostreamThn8Dtor(uint obj) => IostreamDtorC(obj - 8);
    [Hle("_ZThn8_NSdD0Ev")] uint IostreamThn8Del(uint obj) => IostreamDelC(obj - 8);
    [Hle("_ZTv0_n12_NSdD1Ev")] uint IostreamThunkDtor(uint obj) => IostreamDtorC(VirtualThunkThis(obj));
    [Hle("_ZTv0_n12_NSdD0Ev")] uint IostreamThunkDel(uint obj) => IostreamDelC(VirtualThunkThis(obj));

    /// <summary>basic_ostream::flush: under the buffer's lock (sentry), a good stream flushes its tie, then pubsync
    /// (-1 sets badbit); the sentry's end syncs again for unitbuf (no exception in flight).</summary>
    void OstreamFlush(HleContext ctx, uint os)
    {
        uint ios = os + VbaseOffset(GuestMemory.R32(os));
        uint sb = GuestMemory.R32(ios + IosStrbuf);
        if (sb == 0) return;
        VCall(sb, VsLock);
        if (GuestMemory.R32(ios + IosState) == 0 && GuestMemory.R32(ios + IosTie) is var tie and not 0) OstreamFlush(ctx, tie);
        if (GuestMemory.R32(ios + IosState) == 0 && VCall(sb, VsSync) == EofE)
            IosClear(ctx, ios, GuestMemory.R32(ios + IosState) | 4, false);
        if (_ZSt18uncaught_exceptionv() == 0 && GuestMemory.R32(ios + IosState) == 0 && (GuestMemory.R32(ios + IosFmtfl) & 2) != 0
            && VCall(sb, VsSync) == EofE)
            IosClear(ctx, ios, GuestMemory.R32(ios + IosState) | 4, false);
        VCall(sb, VsUnlock);
    }

    // ------------------------------------------------------------------ the standard streams

    /// <summary>The module's static stream objects of one character type: the init count (-1 before the first
    /// Init), the three filebufs, cin, cout, cerr, clog.</summary>
    sealed record StdStreams(uint Count, uint FbIn, uint FbOut, uint FbErr, uint In, uint Out, uint Err, uint Log);

    StdStreams? _stdC, _stdW;

    StdStreams Std(bool w)
    {
        lock (_rttiLock)
        {
            ref var s = ref w ? ref _stdW : ref _stdC;
            if (s != null) return s;
            uint a = StaticAlloc(8 + 3 * FilebufSize + IstreamSize + 3 * OstreamSize, 8);
            GuestMemory.W32(a, 0xFFFFFFFF);
            uint fb = a + 8, st = fb + 3 * FilebufSize;
            return s = new StdStreams(a, fb, fb + FilebufSize, fb + 2 * FilebufSize, st, st + IstreamSize,
                st + IstreamSize + OstreamSize, st + IstreamSize + 2 * OstreamSize);
        }
    }

    // the exported variables: init counts and the stream objects (cin, cout, cerr, clog, wcin, wcout, wcerr, wclog)
    void RegisterStreamVariables()
    {
        Emu.Hle.RegisterVariable(0x554B7625, () => Std(false).Count);
        Emu.Hle.RegisterVariable(0x8137F8D7, () => Std(true).Count);
        Emu.Hle.RegisterVariable(0x768F2944, () => Std(false).In);
        Emu.Hle.RegisterVariable(0xB3867BB4, () => Std(false).Out);
        Emu.Hle.RegisterVariable(0x14B35A37, () => Std(false).Err);
        Emu.Hle.RegisterVariable(0xD361A52A, () => Std(false).Log);
        Emu.Hle.RegisterVariable(0x8054BC8E, () => Std(true).In);
        Emu.Hle.RegisterVariable(0x4F726424, () => Std(true).Out);
        Emu.Hle.RegisterVariable(0x8B2001F7, () => Std(true).Err);
        Emu.Hle.RegisterVariable(0x8E04C18C, () => Std(true).Log);
    }

    /// <summary>ios_base::Init / _Winit constructor: under the system lock 0, the first reference (count still
    /// negative) builds the standard streams; later ones count.</summary>
    void StdInit(HleContext ctx, bool w)
    {
        var s = Std(w);
        bool first;
        _Locksyslock(0);
        try
        {
            int n = (int)GuestMemory.R32(s.Count);
            first = n < 0;
            GuestMemory.W32(s.Count, first ? 1u : (uint)(n + 1));
        }
        finally { _Unlocksyslock(0); }
        if (!first) return;
        FilebufCtor(s.FbIn, StdinFile().Addr, w);
        FilebufCtor(s.FbOut, StdoutFile().Addr, w);
        IstreamCtor(ctx, s.In, s.FbIn, true, w);
        OstreamCtor(ctx, s.Out, s.FbOut, true, w);
        GuestMemory.W32(s.In + 8 + IosTie, s.Out);
        FilebufCtor(s.FbErr, StderrFile().Addr, w);
        OstreamCtor(ctx, s.Err, s.FbErr, true, w);
        GuestMemory.W32(s.Err + 4 + IosTie, s.Out);
        GuestMemory.W32(s.Err + 4 + IosFmtfl, GuestMemory.R32(s.Err + 4 + IosFmtfl) | 2); // unitbuf
        OstreamCtor(ctx, s.Log, s.FbErr, true, w);
        GuestMemory.W32(s.Log + 4 + IosTie, s.Out);
    }

    /// <summary>ios_base::Init / _Winit destructor: the last reference flushes cout, cerr and clog.</summary>
    void StdFini(HleContext ctx, bool w)
    {
        var s = Std(w);
        bool last;
        _Locksyslock(0);
        try
        {
            uint n = GuestMemory.R32(s.Count) - 1;
            GuestMemory.W32(s.Count, n);
            last = n == 0;
        }
        finally { _Unlocksyslock(0); }
        if (!last) return;
        OstreamFlush(ctx, s.Out);
        OstreamFlush(ctx, s.Err);
        OstreamFlush(ctx, s.Log);
    }

    [Hle("_ZNSt8ios_base4InitC1Ev")] [Hle("_ZNSt8ios_base4InitC2Ev")] uint IosInitCtor(HleContext ctx, uint obj) { StdInit(ctx, false); return obj; }
    [Hle("_ZNSt8ios_base4InitD1Ev")] [Hle("_ZNSt8ios_base4InitD2Ev")] uint IosInitDtor(HleContext ctx, uint obj) { StdFini(ctx, false); return obj; }
    [Hle("_ZNSt6_WinitC1Ev")] [Hle("_ZNSt6_WinitC2Ev")] uint WinitCtor(HleContext ctx, uint obj) { StdInit(ctx, true); return obj; }
    [Hle("_ZNSt6_WinitD1Ev")] [Hle("_ZNSt6_WinitD2Ev")] uint WinitDtor(HleContext ctx, uint obj) { StdFini(ctx, true); return obj; }
}
