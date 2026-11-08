using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceLibstdcxx std::ios_base as libc.suprx 3.74 lays it out (Dinkumware): +0 vptr, +4 _Stdstr (index of a standard
// stream, 0 for others), +8 _Mystate, +0x0C _Except, +0x10 _Fmtfl, +0x14 _Prec, +0x18 _Wide, +0x1C _Arr (iword/pword
// list), +0x20 _Calls (register_callback list); the module has no locale. The manipulators, ios_base::failure with the
// iostream error category, and _Fiopen (the fopen modes of basic_filebuf). The standard streams are in
// Libc.Iostreams.cs.
public sealed partial class Libc
{
    const uint IosStdstr = 4, IosState = 8, IosExcept = 0xC, IosFmtfl = 0x10, IosPrec = 0x14, IosWide = 0x18,
        IosArr = 0x1C, IosCalls = 0x20;

    static readonly ExcClass ExcSystemError = new("system_error", 0x5F5AEBC5, 0x2C, ExcRuntimeError, null);
    static readonly ExcClass ExcIosFailure = new("ios_base::failure", 0x648213CC, 0x2C, ExcSystemError, null);

    /// <summary>ios_base::_Init: the state of a new stream (skipws | dec, precision 6), then clear().</summary>
    [Hle]
    internal void _ZNSt8ios_base5_InitEv(HleContext ctx, uint ios)
    {
        GuestMemory.W32(ios + IosStdstr, 0);
        GuestMemory.W32(ios + IosExcept, 0);
        GuestMemory.W32(ios + IosFmtfl, 0x201);
        GuestMemory.W32(ios + IosPrec, 6);
        GuestMemory.W32(ios + IosWide, 0);
        GuestMemory.W32(ios + IosCalls, 0);
        GuestMemory.W32(ios + IosArr, 0);
        IosClear(ctx, ios, 0, false);
    }

    /// <summary>ios_base::clear(state, reraise): the state (goodbit/eofbit/failbit/badbit and _Hardfail); a state the
    /// exception mask selects rethrows the current exception (reraise) or throws ios_base::failure.</summary>
    [Hle]
    void _ZNSt8ios_base5clearENSt5_IosbIiE8_IostateEb(HleContext ctx, uint ios, uint state, bool reraise) => IosClear(ctx, ios, state, reraise);

    void IosClear(HleContext ctx, uint ios, uint state, bool reraise)
    {
        state &= 0x17;
        GuestMemory.W32(ios + IosState, state);
        uint hit = state & GuestMemory.R32(ios + IosExcept);
        if (hit == 0) return;
        if (reraise) { __cxa_rethrow(ctx); return; }
        string msg = (hit & 4) != 0 ? "ios_base::badbit set" : (hit & 2) != 0 ? "ios_base::failbit set" : "ios_base::eofbit set";
        uint obj = AllocateException(ExcIosFailure.Size);
        Construct(obj, ExcIosFailure, cstr: ConstString(msg));
        GuestMemory.W32(obj + 0x20, obj);
        GuestMemory.W32(obj + 0x24, 1); // io_errc::stream
        GuestMemory.W32(obj + 0x28, IostreamCategory);
        Throw(ctx, obj, TiOf(ExcIosFailure), ExportStubOfSlot(ExcIosFailure, 0));
    }

    // the reference counts of the standard streams' _Stdstr slots (the module's table of 8)
    readonly sbyte[] _stdstrRefs = new sbyte[8];

    /// <summary>ios_base::~ios_base: for a standard stream the last reference only; then the erase_event callbacks
    /// (fn(0, *this, index), in list order) and the iword/pword and callback lists are freed.</summary>
    [Hle("_ZNSt8ios_baseD1Ev")]
    [Hle("_ZNSt8ios_baseD2Ev")]
    uint IosBaseDtor(uint ios)
    {
        GuestMemory.W32(ios, Rtti.Vptr(0xD58C5F52));
        uint std = GuestMemory.R32(ios + IosStdstr);
        if (std != 0 && std < 8)
        {
            lock (_stdstrRefs) if (--_stdstrRefs[std] > 0) return ios;
        }
        for (uint n = GuestMemory.R32(ios + IosCalls); n != 0; n = GuestMemory.R32(n))
            GuestThread.Current?.CallGuest(GuestMemory.R32(n + 8), 0, ios, GuestMemory.R32(n + 4));
        for (uint n = GuestMemory.R32(ios + IosArr); n != 0;) { uint next = GuestMemory.R32(n); free(n); n = next; }
        GuestMemory.W32(ios + IosArr, 0);
        for (uint n = GuestMemory.R32(ios + IosCalls); n != 0;) { uint next = GuestMemory.R32(n); free(n); n = next; }
        GuestMemory.W32(ios + IosCalls, 0);
        return ios;
    }

    [Hle] uint _ZNSt8ios_baseD0Ev(uint ios) { IosBaseDtor(ios); free(ios); return ios; }

    /// <summary>ios_base::_Addstd(ios_base*): the stream takes the first free (or its own) of the 7 standard-stream
    /// slots, whose reference count goes up.</summary>
    [Hle("_ZNSt8ios_base7_AddstdEPS_")]
    [Hle("_ZNSt8ios_base7_AddstdEv")]
    void IosAddstd(uint ios)
    {
        lock (_stdstrRefs)
        {
            uint i = 1;
            while (i < 8 && _stdstrSlots[i] != 0 && _stdstrSlots[i] != ios) i++;
            GuestMemory.W32(ios + IosStdstr, i);
            if (i < 8)
            {
                _stdstrSlots[i] = ios;
                _stdstrRefs[i]++;
            }
        }
    }

    readonly uint[] _stdstrSlots = new uint[8];

    // ------------------------------------------------------------------ manipulators: { function, argument }

    uint Manip(string name, Action<uint, uint> apply) => HostStub("manipulator " + name, c => apply(c.Gpr(0), c.Gpr(1)));

    void MakeManip(uint ret, uint fn, uint arg) { GuestMemory.W32(ret, fn); GuestMemory.W32(ret + 4, arg); }

    [Hle] internal void _ZSt4setwi(uint ret, uint n) => MakeManip(ret, Manip("setw", (ios, v) => GuestMemory.W32(ios + IosWide, v)), n);
    [Hle] internal void _ZSt12setprecisioni(uint ret, uint n) => MakeManip(ret, Manip("setprecision", (ios, v) => GuestMemory.W32(ios + IosPrec, v)), n);

    [Hle]
    internal void _ZSt11setiosflagsNSt5_IosbIiE9_FmtflagsE(uint ret, uint fl) => MakeManip(ret, Manip("setiosflags", (ios, v) =>
        GuestMemory.W32(ios + IosFmtfl, (GuestMemory.R32(ios + IosFmtfl) & ~v) | (v & 0xFFFF))), fl);

    [Hle]
    internal void _ZSt13resetiosflagsNSt5_IosbIiE9_FmtflagsE(uint ret, uint fl) => MakeManip(ret, Manip("resetiosflags", (ios, v) =>
        GuestMemory.W32(ios + IosFmtfl, GuestMemory.R32(ios + IosFmtfl) & ~v)), fl);

    /// <summary>setbase(n): oct for 8, dec for 10, hex for 16, none of them otherwise.</summary>
    [Hle]
    internal void _ZSt7setbasei(uint ret, uint n) => MakeManip(ret, Manip("setbase", (ios, v) =>
    {
        uint basefield = v switch { 8 => 0x400u, 10 => 0x200u, 16 => 0x800u, _ => 0u };
        GuestMemory.W32(ios + IosFmtfl, (GuestMemory.R32(ios + IosFmtfl) & ~0xE00u) | basefield);
    }), n);

    // ------------------------------------------------------------------ ios_base::failure and system_error

    [Hle]
    uint _ZNSt8ios_base7failureC1ERKSs(uint obj, uint s)
    {
        Construct(obj, ExcIosFailure, stringSrc: s);
        GuestMemory.W32(obj + 0x20, obj);
        GuestMemory.W32(obj + 0x24, 1);
        GuestMemory.W32(obj + 0x28, IostreamCategory);
        return obj;
    }

    [Hle] uint _ZNSt8ios_base7failureC1ERKS0_(uint obj, uint other) => Construct(obj, ExcIosFailure, copyOf: other);
    [Hle] uint _ZNSt8ios_base7failureD1Ev(uint obj) => Destruct(obj, ExcIosFailure);
    [Hle] uint _ZNSt8ios_base7failureD0Ev(uint obj) => DeletingDtor(obj, ExcIosFailure);
    [Hle] void _ZNKSt8ios_base7failure8_DoraiseEv(HleContext ctx, uint obj) => Doraise(ctx, obj, ExcIosFailure);
    [Hle("_ZNSt12system_errorD1Ev")] [Hle("_ZNSt12system_errorD2Ev")] uint SystemErrorDtor(uint obj) => Destruct(obj, ExcSystemError);

    uint _iostreamCategory;

    /// <summary>The iostream error category (a static object of the module: name "iostream", message "iostream stream
    /// error" for io_errc::stream, else the generic error text or "unknown error"; error_code/error_condition are
    /// 12 bytes, value at +4 and category at +8).</summary>
    uint IostreamCategory
    {
        get
        {
            lock (_rttiLock)
            {
                if (_iostreamCategory != 0) return _iostreamCategory;
                uint vt = StaticAlloc(0x24, 4), obj = StaticAlloc(4, 4);
                uint[] slots =
                [
                    0, 0,
                    HostStub("iostream_category.D1", c => c.Return(c.Gpr(0))),
                    HostStub("iostream_category.D0", c => c.Return(c.Gpr(0))),
                    HostStub("iostream_category.name", c => c.Return(ConstString("iostream"))),
                    HostStub("iostream_category.message", c =>
                    {
                        int ev = (int)c.Gpr(2);
                        string text = ev == 1 ? "iostream stream error" : SyserrorNames.GetValueOrDefault(ev, "unknown error");
                        uint t = ConstString(text);
                        StrAssign(c.Gpr(0), t, StrLen(t));
                    }),
                    HostStub("iostream_category.default_error_condition", c =>
                    {
                        uint ret = c.Gpr(0);
                        GuestMemory.W32(ret + 4, c.Gpr(2));
                        GuestMemory.W32(ret, c.Gpr(3));
                        GuestMemory.W32(ret + 8, c.Gpr(1));
                    }),
                    HostStub("iostream_category.equivalent(int, condition)", c =>
                    {
                        uint cond = c.Gpr(2);
                        c.Return(GuestMemory.R32(cond + 8) == c.Gpr(0) && GuestMemory.R32(cond + 4) == c.Gpr(1) ? 1 : 0);
                    }),
                    HostStub("iostream_category.equivalent(code, int)", c =>
                    {
                        uint code = c.Gpr(1);
                        c.Return(GuestMemory.R32(code + 8) == c.Gpr(0) && GuestMemory.R32(code + 4) == c.Gpr(2) ? 1 : 0);
                    }),
                ];
                for (int i = 0; i < slots.Length; i++) GuestMemory.W32(vt + (uint)i * 4, slots[i]);
                GuestMemory.W32(obj, vt + 8);
                return _iostreamCategory = obj;
            }
        }
    }

    // ------------------------------------------------------------------ _Fiopen

    // the openmodes basic_filebuf::open accepts (after nocreate adds in and app adds out; ate, nocreate and noreplace
    // are left out) and their fopen modes
    static readonly (uint Mode, string Fopen)[] FiopenModes =
    [
        (0x01, "r"), (0x02, "w"), (0x12, "w"), (0x0A, "a"), (0x21, "rb"), (0x22, "wb"), (0x32, "wb"), (0x2A, "ab"),
        (0x03, "r+"), (0x13, "w+"), (0x0B, "a+"), (0x23, "r+b"), (0x33, "w+b"), (0x2B, "a+b"),
    ];

    /// <summary>_Fiopen(name, openmode, prot): fopen with the mode for <paramref name="mode"/>, 0 for a combination
    /// basic_filebuf does not allow; _Noreplace fails on an existing file opened for input, ate seeks to the end.</summary>
    [Hle]
    uint _ZSt7_FiopenPKcNSt5_IosbIiE9_OpenmodeEi(string name, uint mode, int prot)
    {
        bool ate = (mode & 4) != 0, noreplace = (mode & 0x80) != 0;
        if ((mode & 0x40) != 0) mode |= 1;
        if ((mode & 8) != 0) mode |= 2;
        uint key = mode & ~0xC4u;
        int i = Array.FindIndex(FiopenModes, m => m.Mode == key);
        if (i < 0) return 0;
        if (noreplace && (mode & 1) != 0)
        {
            uint probe = fopen(name, "r");
            if (probe != 0) { fclose(probe); return 0; }
        }
        uint fp = fopen(name, FiopenModes[i].Fopen);
        if (fp == 0) return 0;
        if (ate && fseek(fp, 0, 2) != 0) { fclose(fp); return 0; }
        return fp;
    }
}
