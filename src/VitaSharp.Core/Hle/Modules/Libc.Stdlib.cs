using System.Globalization;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceLibc stdlib: number conversions (strto*, ato*, the Dinkumware _Sto* helpers), process exit and the atexit
// lists, C++ static-guard and setjmp/longjmp support.
public sealed partial class Libc
{
    // ------------------------------------------------------------------ integer conversion

    /// <summary>Parses an integer as strtoul does: leading space, sign, base prefix; digits saturate. Returns the
    /// magnitude, whether it overflowed 64 bits, the sign and the end of the number (str itself when none).</summary>
    static (ulong Value, bool Overflow, bool Negative, uint End) ParseInt(uint str, int radix, Func<uint, int> ch, uint step)
    {
        uint p = str;
        while (IsSpace(ch(p))) p += step;
        bool neg = false;
        int c = ch(p);
        if (c is '+' or '-') { neg = c == '-'; p += step; }
        if (radix is < 0 or 1 or > 36) return (0, false, false, str);
        uint digitsStart = p;
        if ((radix == 0 || radix == 16) && ch(p) == '0' && (ch(p + step) | 0x20) == 'x' && Digit(ch(p + 2 * step)) < 16) { p += 2 * step; radix = 16; digitsStart = p; }
        else if (radix == 0) radix = ch(p) == '0' ? 8 : 10;
        ulong v = 0;
        bool over = false;
        for (; ; p += step)
        {
            int d = Digit(ch(p));
            if (d >= radix) break;
            ulong nv = v * (ulong)radix + (ulong)d;
            if (v > (ulong.MaxValue - (ulong)d) / (ulong)radix) over = true;
            v = nv;
        }
        if (p == digitsStart) return (0, false, false, str);
        return (v, over, neg, p);
    }

    static int Digit(int c) => c is >= '0' and <= '9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'z' ? (c | 0x20) - 'a' + 10 : 99;
    static int NarrowCh(uint p) => B(p);
    static int WideCh(uint p) => W(p);

    enum IntKind { Long, ULong, LLong, ULLong }

    /// <summary>The strto* result for <paramref name="kind"/>: clamps out-of-range values (ERANGE through
    /// <paramref name="err"/>), negates unsigned results modulo 2^n like the C library.</summary>
    static ulong Convert(uint str, uint endPtr, int radix, IntKind kind, bool wide, out bool range)
    {
        var (v, over, neg, end) = ParseInt(str, radix, wide ? WideCh : NarrowCh, wide ? 2u : 1u);
        if (endPtr != 0) GuestMemory.W32(endPtr, end);
        range = false;
        switch (kind)
        {
            case IntKind.Long:
                if (over || (!neg && v > int.MaxValue) || (neg && v > 0x80000000UL)) { range = true; return neg ? 0x80000000u : int.MaxValue; }
                return (uint)(neg ? -(long)v : (long)v);
            case IntKind.ULong:
                if (over || v > uint.MaxValue) { range = true; return uint.MaxValue; }
                return neg ? (uint)-(long)v : (uint)v;
            case IntKind.LLong:
                if (over || (!neg && v > long.MaxValue) || (neg && v > 0x8000000000000000UL)) { range = true; return neg ? 0x8000000000000000UL : long.MaxValue; }
                return neg ? (ulong)-(long)v : v;
            default:
                if (over) { range = true; return ulong.MaxValue; }
                return neg ? (ulong)-(long)v : v;
        }
    }

    static ulong ConvertErrno(uint str, uint endPtr, int radix, IntKind kind, bool wide = false)
    {
        ulong r = Convert(str, endPtr, radix, kind, wide, out bool range);
        if (range) SetErrno(ERANGE);
        return r;
    }

    /// <summary>The _Sto*x forms report a range error through *perr instead of errno.</summary>
    static ulong ConvertPerr(uint str, uint endPtr, int radix, IntKind kind, uint perr)
    {
        ulong r = Convert(str, endPtr, radix, kind, false, out bool range);
        if (perr != 0) GuestMemory.W32(perr, range ? 1u : 0u);
        return r;
    }

    [Hle] int strtol(uint s, uint end, int radix) => (int)ConvertErrno(s, end, radix, IntKind.Long);
    [Hle] uint strtoul(uint s, uint end, int radix) => (uint)ConvertErrno(s, end, radix, IntKind.ULong);
    [Hle] long strtoll(uint s, uint end, int radix) => (long)ConvertErrno(s, end, radix, IntKind.LLong);
    [Hle] ulong strtoull(uint s, uint end, int radix) => ConvertErrno(s, end, radix, IntKind.ULLong);
    [Hle] long strtoimax(uint s, uint end, int radix) => (long)ConvertErrno(s, end, radix, IntKind.LLong);
    [Hle] ulong strtoumax(uint s, uint end, int radix) => ConvertErrno(s, end, radix, IntKind.ULLong);
    [Hle] int atoi(uint s) => (int)Convert(s, 0, 10, IntKind.Long, false, out _);
    [Hle] int atol(uint s) => (int)Convert(s, 0, 10, IntKind.Long, false, out _);
    [Hle] long atoll(uint s) => (long)Convert(s, 0, 10, IntKind.LLong, false, out _);

    [Hle] int _Stolx(uint s, uint end, int radix, uint perr) => (int)ConvertPerr(s, end, radix, IntKind.Long, perr);
    [Hle] uint _Stoul(uint s, uint end, int radix) => (uint)ConvertErrno(s, end, radix, IntKind.ULong);
    [Hle] uint _Stoulx(uint s, uint end, int radix, uint perr) => (uint)ConvertPerr(s, end, radix, IntKind.ULong, perr);
    [Hle] long _Stoll(uint s, uint end, int radix) => (long)ConvertErrno(s, end, radix, IntKind.LLong);
    [Hle] long _Stollx(uint s, uint end, int radix, uint perr) => (long)ConvertPerr(s, end, radix, IntKind.LLong, perr);
    [Hle] ulong _Stoull(uint s, uint end, int radix) => ConvertErrno(s, end, radix, IntKind.ULLong);
    [Hle] ulong _Stoullx(uint s, uint end, int radix, uint perr) => ConvertPerr(s, end, radix, IntKind.ULLong, perr);

    // wide forms
    [Hle] int wcstol(uint s, uint end, int radix) => (int)ConvertErrno(s, end, radix, IntKind.Long, true);
    [Hle] uint wcstoul(uint s, uint end, int radix) => (uint)ConvertErrno(s, end, radix, IntKind.ULong, true);
    [Hle] long wcstoll(uint s, uint end, int radix) => (long)ConvertErrno(s, end, radix, IntKind.LLong, true);
    [Hle] ulong wcstoull(uint s, uint end, int radix) => ConvertErrno(s, end, radix, IntKind.ULLong, true);
    [Hle] long wcstoimax(uint s, uint end, int radix) => (long)ConvertErrno(s, end, radix, IntKind.LLong, true);
    [Hle] ulong wcstoumax(uint s, uint end, int radix) => ConvertErrno(s, end, radix, IntKind.ULLong, true);
    [Hle] uint _WStoul(uint s, uint end, int radix) => (uint)ConvertErrno(s, end, radix, IntKind.ULong, true);

    // ------------------------------------------------------------------ floating-point conversion

    /// <summary>strtod's grammar: optional sign, then a decimal or 0x-hex significand with optional exponent,
    /// "inf"/"infinity" or "nan"/"nan(chars)". Returns the value, the end and whether it over/underflowed.</summary>
    internal static (double Value, uint End, bool Range) ParseDouble(uint str, Func<uint, int> ch, uint step)
    {
        uint p = str;
        while (IsSpace(ch(p))) p += step;
        bool neg = false;
        if (ch(p) is '+' or '-') { neg = ch(p) == '-'; p += step; }
        bool Match(uint at, string word)
        {
            for (int i = 0; i < word.Length; i++) if ((ch(at + (uint)i * step) | 0x20) != word[i]) return false;
            return true;
        }
        if (Match(p, "inf"))
        {
            uint e = p + 3 * step;
            if (Match(e, "inity")) e += 5 * step;
            return (neg ? double.NegativeInfinity : double.PositiveInfinity, e, false);
        }
        if (Match(p, "nan"))
        {
            uint e = p + 3 * step;
            if (ch(e) == '(')
            {
                uint q = e + step;
                while (ch(q) is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') q += step;
                if (ch(q) == ')') e = q + step;
            }
            return (neg ? -double.NaN : double.NaN, e, false);
        }
        if (ch(p) == '0' && (ch(p + step) | 0x20) == 'x')
        {
            uint q = p + 2 * step;
            double mant = 0;
            int exp = 0, digits = 0;
            bool dot = false;
            for (; ; q += step)
            {
                int c = ch(q);
                if (c == '.' && !dot) { dot = true; continue; }
                int d = Digit(c);
                if (d >= 16) break;
                mant = mant * 16 + d;
                if (dot) exp -= 4;
                digits++;
            }
            if (digits == 0) return (0, p + step, false); // "0x" without digits: the "0" is the number
            if ((ch(q) | 0x20) == 'p')
            {
                uint r = q + step;
                bool eneg = false;
                if (ch(r) is '+' or '-') { eneg = ch(r) == '-'; r += step; }
                if (ch(r) is >= '0' and <= '9')
                {
                    int e = 0;
                    for (; ch(r) is >= '0' and <= '9'; r += step) e = Math.Min(e * 10 + (ch(r) - '0'), 100000);
                    exp += eneg ? -e : e;
                    q = r;
                }
            }
            double hv = Math.ScaleB(mant, exp);
            bool hr = double.IsInfinity(hv) || (hv == 0 && mant != 0);
            return (neg ? -hv : hv, q, hr);
        }
        var sb = new System.Text.StringBuilder();
        uint s = p;
        int nd = 0;
        for (; ch(s) is >= '0' and <= '9'; s += step) { sb.Append((char)ch(s)); nd++; }
        if (ch(s) == '.')
        {
            uint t = s + step;
            int frac = 0;
            var fs = new System.Text.StringBuilder(".");
            for (; ch(t) is >= '0' and <= '9'; t += step) { fs.Append((char)ch(t)); frac++; }
            if (nd + frac > 0) { sb.Append(fs); nd += frac; s = t; }
        }
        if (nd == 0) return (0, str, false);
        if ((ch(s) | 0x20) == 'e')
        {
            uint r = s + step;
            var es = new System.Text.StringBuilder("e");
            if (ch(r) is '+' or '-') { es.Append((char)ch(r)); r += step; }
            if (ch(r) is >= '0' and <= '9')
            {
                for (; ch(r) is >= '0' and <= '9'; r += step) es.Append((char)ch(r));
                sb.Append(es);
                s = r;
            }
        }
        double v = double.Parse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        bool range = double.IsInfinity(v) || (v == 0 && sb.ToString().Any(c => c is >= '1' and <= '9') && !HasZeroMantissa(sb.ToString()));
        if (double.IsInfinity(v)) v = double.PositiveInfinity;
        return (neg ? -v : v, s, range);
    }

    static bool HasZeroMantissa(string s)
    {
        int e = s.IndexOfAny(['e', 'E']);
        return (e < 0 ? s : s[..e]).All(c => c is '0' or '.');
    }

    double StrToD(uint s, uint end, bool wide, out bool range)
    {
        var (v, e, r) = ParseDouble(s, wide ? WideCh : NarrowCh, wide ? 2u : 1u);
        if (end != 0) GuestMemory.W32(end, e);
        range = r;
        return v;
    }

    [Hle] double strtod(uint s, uint end) { double v = StrToD(s, end, false, out bool r); if (r) SetErrno(ERANGE); return v; }
    [Hle] double strtold(uint s, uint end) => strtod(s, end);
    [Hle] double atof(uint s) => StrToD(s, 0, false, out _);
    [Hle] double wcstod(uint s, uint end) { double v = StrToD(s, end, true, out bool r); if (r) SetErrno(ERANGE); return v; }
    [Hle] double wcstold(uint s, uint end) => wcstod(s, end);

    static float ToFloat(double v, ref bool range)
    {
        float f = (float)v;
        if ((float.IsInfinity(f) && !double.IsInfinity(v)) || (f == 0 && v != 0)) range = true;
        return f;
    }

    [Hle] float strtof(uint s, uint end) { double v = StrToD(s, end, false, out bool r); float f = ToFloat(v, ref r); if (r) SetErrno(ERANGE); return f; }
    [Hle] float wcstof(uint s, uint end) { double v = StrToD(s, end, true, out bool r); float f = ToFloat(v, ref r); if (r) SetErrno(ERANGE); return f; }
    [Hle] float atoff(uint s) { bool r = false; return ToFloat(StrToD(s, 0, false, out _), ref r); }

    /// <summary>_Stod(s, endptr, pten): strtod scaled by 10^pten (Dinkumware's helper for the C++ library).</summary>
    [Hle] double _Stod(uint s, uint end, int pten) { double v = StrToD(s, end, false, out bool r) * Math.Pow(10, pten); if (r || double.IsInfinity(v)) SetErrno(ERANGE); return v; }
    [Hle] double _Stold(uint s, uint end, int pten) => _Stod(s, end, pten);
    [Hle] float _Stof(uint s, uint end, int pten) { bool r; double v = StrToD(s, end, false, out r) * Math.Pow(10, pten); float f = ToFloat(v, ref r); if (r) SetErrno(ERANGE); return f; }
    [Hle] double _WStod(uint s, uint end, int pten) { double v = StrToD(s, end, true, out bool r) * Math.Pow(10, pten); if (r || double.IsInfinity(v)) SetErrno(ERANGE); return v; }
    [Hle] double _WStold(uint s, uint end, int pten) => _WStod(s, end, pten);
    [Hle] float _WStof(uint s, uint end, int pten) { bool r; double v = StrToD(s, end, true, out r) * Math.Pow(10, pten); float f = ToFloat(v, ref r); if (r) SetErrno(ERANGE); return f; }

    [Hle]
    double _Stodx(uint s, uint end, int pten, uint perr)
    {
        double v = StrToD(s, end, false, out bool r) * Math.Pow(10, pten);
        if (perr != 0) GuestMemory.W32(perr, r || double.IsInfinity(v) ? 1u : 0u);
        return v;
    }

    [Hle] double _Stoldx(uint s, uint end, int pten, uint perr) => _Stodx(s, end, pten, perr);

    [Hle]
    float _Stofx(uint s, uint end, int pten, uint perr)
    {
        bool r;
        double v = StrToD(s, end, false, out r) * Math.Pow(10, pten);
        float f = ToFloat(v, ref r);
        if (perr != 0) GuestMemory.W32(perr, r ? 1u : 0u);
        return f;
    }

    // ------------------------------------------------------------------ exit, atexit

    readonly List<(uint Func, uint Arg, uint Dso)> _atexit = new();
    readonly List<uint> _quickExit = new();
    uint _mainDso;

    /// <summary>__cxa_atexit(func, arg, dso): run func(arg) at exit (or at __cxa_finalize(dso)); 0, or 1 when no
    /// room is left (the module keeps 64 entries plus heap ones: here the heap is the limit).</summary>
    [Hle]
    int __cxa_atexit(uint func, uint arg, uint dso)
    {
        lock (_atexit) _atexit.Add((func, arg, dso));
        return 0;
    }

    [Hle] int __aeabi_atexit(uint arg, uint func, uint dso) => __cxa_atexit(func, arg, dso);

    [Hle] void __cxa_set_dso_handle_main(uint dso) => _mainDso = dso;

    /// <summary>Runs the registered destructors of <paramref name="dso"/> (0 = all) in reverse order, once each.</summary>
    [Hle]
    void __cxa_finalize(uint dso)
    {
        while (true)
        {
            (uint Func, uint Arg, uint Dso) e;
            lock (_atexit)
            {
                int i = _atexit.FindLastIndex(x => dso == 0 || x.Dso == dso);
                if (i < 0) return;
                e = _atexit[i];
                _atexit.RemoveAt(i);
            }
            GuestThread.Current?.CallGuest(e.Func, e.Arg);
        }
    }

    [Hle]
    int __at_quick_exit(uint func)
    {
        lock (_quickExit)
        {
            if (_quickExit.Count >= 32) return -1;
            _quickExit.Add(func);
        }
        return 0;
    }

    ProcessMgr? _proc;
    ProcessMgr Proc => _proc ??= Emu.Hle.Modules.OfType<ProcessMgr>().First();

    /// <summary>exit(status): the atexit/__cxa_atexit handlers (newest first), then the streams flushed and closed,
    /// then the process ends (sceKernelExitProcess), as libc.suprx does after sceKernelCallModuleExit.</summary>
    [Hle]
    void exit(int status)
    {
        __cxa_finalize(0);
        CloseAllStreams();
        Proc.ExitProcess(status);
    }

    [Hle] void _Exit(int status) => Proc.ExitProcess(status);

    [Hle]
    void quick_exit(int status)
    {
        while (true)
        {
            uint f;
            lock (_quickExit)
            {
                if (_quickExit.Count == 0) break;
                f = _quickExit[^1];
                _quickExit.RemoveAt(_quickExit.Count - 1);
            }
            GuestThread.Current?.CallGuest(f);
        }
        Proc.ExitProcess(status);
    }

    /// <summary>abort(): "abort() is called." on stderr, then _Exit(1) (the module first reports the abort through
    /// sceKernelCallAbortHandler, which here is logged).</summary>
    [Hle]
    void abort()
    {
        Log.Error("[guest] SceLibc abort()");
        WriteStream(StderrFile(), "abort() is called.\n"u8.ToArray());
        Proc.ExitProcess(1);
    }

    /// <summary>_Assert(message, location): "message -- location" on stderr, then abort.</summary>
    [Hle]
    void _Assert(uint msg, uint loc)
    {
        string text = (msg != 0 ? GuestMemory.ReadCString(msg) : "") + " -- " + (loc != 0 ? GuestMemory.ReadCString(loc) : "");
        Log.Error("[guest] assertion failed: " + text);
        WriteStream(StderrFile(), System.Text.Encoding.Latin1.GetBytes(text + "\n"));
        abort();
    }

    /// <summary>_SCE_Assert(expr, file, line, func): "Assertion failed: ..." on stderr, then abort.</summary>
    [Hle]
    void _SCE_Assert(uint expr, uint file, int line, uint func)
    {
        string S(uint p) => p != 0 ? GuestMemory.ReadCString(p) : "";
        string text = $"Assertion failed: {S(expr)}, file {S(file)}, line {line}, function {S(func)}";
        Log.Error("[guest] " + text);
        WriteStream(StderrFile(), System.Text.Encoding.Latin1.GetBytes(text + "\n"));
        abort();
    }

    // ------------------------------------------------------------------ C++ static guards

    /// <summary>__cxa_guard_acquire(guard): 1 = the caller runs the initializer (bit 8 marks it in progress), 0 =
    /// already done (bit 0); another thread's initialization in progress is waited for (bit 9 = waiters).</summary>
    [Hle]
    int __cxa_guard_acquire(uint guard)
    {
        while (true)
        {
            lock (_atexit)
            {
                uint g = GuestMemory.R32(guard);
                if ((g & 1) != 0) return 0;
                if ((g & 0x100) == 0) { GuestMemory.W32(guard, 0x100); return 1; }
                GuestMemory.W32(guard, g | 0x200);
            }
            Wait.Sleep(100);
        }
    }

    [Hle] void __cxa_guard_release(uint guard) { lock (_atexit) GuestMemory.W32(guard, 1); }
    [Hle] void __cxa_guard_abort(uint guard) { lock (_atexit) GuestMemory.W32(guard, 0); }

    // ------------------------------------------------------------------ setjmp / longjmp

    /// <summary>The jmp_buf key: the word in TLS slot 6 that libc.suprx XORs into the saved sp and lr.</summary>
    static uint JmpKey() => GuestThread.Current is { } t ? GuestMemory.R32(t.TlsBase + 6 * 4) : 0;

    /// <summary>setjmp(env): r4-r11, sp^key, lr^key, d8-d15, fpscr (the 3.74 layout), returns 0.</summary>
    [Hle]
    unsafe int setjmp(HleContext ctx, uint env)
    {
        var c = ctx.Cpu;
        uint key = JmpKey();
        for (int i = 0; i < 8; i++) GuestMemory.W32(env + (uint)i * 4, c->R[4 + i]);
        GuestMemory.W32(env + 32, c->R[13] ^ key);
        GuestMemory.W32(env + 36, c->R[14] ^ key);
        var d = (ulong*)c->D;
        for (int i = 0; i < 8; i++) GuestMemory.Write(env + 40 + (uint)i * 8, d[8 + i]);
        GuestMemory.W32(env + 104, c->Fpscr);
        return 0;
    }

    /// <summary>longjmp(env, val): restores what setjmp saved (fpscr keeps its current exception/control bits outside
    /// 0xF80000FF... as the module merges them) and returns val (1 for 0) from the setjmp call.</summary>
    [Hle]
    unsafe void longjmp(HleContext ctx, uint env, int val)
    {
        var c = ctx.Cpu;
        uint key = JmpKey();
        for (int i = 0; i < 8; i++) c->R[4 + i] = GuestMemory.R32(env + (uint)i * 4);
        c->R[13] = GuestMemory.R32(env + 32) ^ key;
        c->R[14] = GuestMemory.R32(env + 36) ^ key;
        var d = (ulong*)c->D;
        for (int i = 0; i < 8; i++) d[8 + i] = GuestMemory.Read<ulong>(env + 40 + (uint)i * 8);
        const uint keep = 0xF80000FF;
        c->Fpscr = (GuestMemory.R32(env + 104) & ~keep) | (c->Fpscr & keep);
        c->R[0] = val == 0 ? 1u : (uint)val;
    }

    // ------------------------------------------------------------------ misc

    /// <summary>FLT_ROUNDS from the FPSCR rounding mode: nearest 1, +inf 2, -inf 3, zero 0.</summary>
    [Hle]
    unsafe int _Fltrounds(HleContext ctx) => ((ctx.Cpu->Fpscr >> 22) & 3) switch { 0 => 1, 1 => 2, 2 => 3, _ => 0 };

    // _Locksyslock/_Unlocksyslock(n) and _Lockfilelock/_Unlockfilelock(FILE*): the library's recursive locks, owned
    // per guest thread; a thread waits while another owns the lock.
    readonly Dictionary<uint, (int Owner, int Depth)> _locks = new();

    void Lock(uint key)
    {
        int me = GuestThread.Current?.Uid ?? 0;
        while (true)
        {
            lock (_locks)
            {
                if (!_locks.TryGetValue(key, out var l) || l.Depth == 0) { _locks[key] = (me, 1); return; }
                if (l.Owner == me) { _locks[key] = (me, l.Depth + 1); return; }
            }
            Wait.Sleep(100);
        }
    }

    void Unlock(uint key)
    {
        int me = GuestThread.Current?.Uid ?? 0;
        lock (_locks)
            if (_locks.TryGetValue(key, out var l) && l.Owner == me && l.Depth > 0) _locks[key] = (me, l.Depth - 1);
    }

    [Hle] void _Locksyslock(int n) => Lock(0x80000000u | (uint)n);
    [Hle] void _Unlocksyslock(int n) => Unlock(0x80000000u | (uint)n);
    [Hle] void _Lockfilelock(uint file) => Lock(file);
    [Hle] void _Unlockfilelock(uint file) => Unlock(file);
}
