using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The rest of the C++ library helpers of libc.suprx 3.74 (SceLibstdcxx): call_once, the <future> error texts,
// std::random_device's entropy, the regex_error / future_error destructors, _Rng_abort, the wide-name _Fiopen and
// _FCbuild. Read in the module; the behaviour is the module's.
public sealed partial class Libc
{
    static readonly ExcClass ExcRegexError = new("regex_error", 0x7ACD1239, 0x24, ExcRuntimeError, null);
    static readonly ExcClass ExcFutureError = new("future_error", 0x84AC8256, 0x2C, ExcLogicError, null);

    readonly object _callOnceLock = new();

    /// <summary>_Call_onceEx(_Once_flag *flag, void (*fn)(void *), void *arg): under the library's once-mutex, runs
    /// fn(arg) when the flag is 0 and then sets it to 1.</summary>
    [Hle]
    void _Call_onceEx(uint flag, uint fn, uint arg)
    {
        lock (_callOnceLock)
        {
            if (GuestMemory.R32(flag) != 0) return;
            GuestThread.Current!.CallGuest(fn, arg);
            GuestMemory.W32(flag, 1);
        }
    }

    /// <summary>_Do_call(void *callable): calls the object's first virtual function on it (the thunk call_once and
    /// std::thread use).</summary>
    [Hle] void _Do_call(uint obj) => GuestThread.Current!.CallGuest(GuestMemory.R32(GuestMemory.R32(obj)), obj);

    static string? FutureErrorText(int code) => code switch
    {
        1 => "broken promise",
        2 => "future already retrieved",
        3 => "promise already satisfied",
        4 => "no state",
        _ => null,
    };

    /// <summary>std::_Future_error_map(int): the future_errc message, NULL for another value.</summary>
    [Hle] uint _ZSt17_Future_error_mapi(int code) => FutureErrorText(code) is { } s ? ConstString(s) : 0;

    /// <summary>std::_Get_future_error_what(int): the same texts as _Future_error_map.</summary>
    [Hle] uint _ZSt22_Get_future_error_whati(int code) => FutureErrorText(code) is { } s ? ConstString(s) : 0;

    /// <summary>std::_Random_device_entropy(): 0 (the module's random_device reports no entropy).</summary>
    [Hle] int _ZSt22_Random_device_entropyv() => 0;

    [Hle] uint _ZNSt11regex_errorD1Ev(uint o) => Destruct(o, ExcRegexError);
    [Hle] uint _ZNSt12future_errorD1Ev(uint o) => Destruct(o, ExcFutureError);

    /// <summary>std::_Rng_abort(const char *msg): the message and a newline on stderr, then abort().</summary>
    [Hle]
    void _ZSt10_Rng_abortPKc(uint msg)
    {
        string text = msg == 0 ? "" : GuestMemory.ReadCString(msg);
        Log.Error($"[guest] std::_Rng_abort: {text}");
        WriteStream(StderrFile(), Encoding.Latin1.GetBytes(text + "\n"));
        abort();
    }

    /// <summary>_Fiopen(const wchar_t *name, openmode, prot): the name through wcstombs (a 1 KiB buffer; NULL when it
    /// cannot be converted), then the narrow _Fiopen.</summary>
    [Hle]
    uint _ZSt7_FiopenPKwNSt5_IosbIiE9_OpenmodeEi(uint wname, uint mode, int prot)
    {
        var sb = new StringBuilder();
        for (uint p = wname; sb.Length < 0x400; p += 2)
        {
            ushort ch = GuestMemory.Read<ushort>(p);
            if (ch == 0) break;
            sb.Append((char)ch);
        }
        string name = sb.ToString();
        if (Encoding.UTF8.GetByteCount(name) >= 0x400 || name.Any(char.IsSurrogate) && !IsWellFormed(name)) return 0;
        return _ZSt7_FiopenPKcNSt5_IosbIiE9_OpenmodeEi(name, mode, prot);
    }

    static bool IsWellFormed(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i])) { if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false; i++; }
            else if (char.IsLowSurrogate(s[i])) return false;
        }
        return true;
    }

    /// <summary>_FCbuild(float re, float im): the float complex {re, im}. Under the hard-float ABI the arguments
    /// arrive in s0/s1 and the result goes back in s0/s1, so nothing moves.</summary>
    [Hle] void _FCbuild(HleContext c) { }

    // ------------------------------------------------------------------ _XLgamma (the log-gamma of <random>)

    static readonly double[] LanczosCoefficients =
        [76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5];

    /// <summary>The module's log-gamma for x &gt; 0: the six-term Lanczos series (Numerical Recipes' gammln).</summary>
    internal static double XLgamma(double x)
    {
        double tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015, y = x;
        foreach (double c in LanczosCoefficients) ser += c / ++y;
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }

    static float XLgammaF(float x)
    {
        float tmp = x + 5.5f;
        tmp -= (x + 0.5f) * MathF.Log(tmp);
        float ser = 1.0f, y = x;
        foreach (double c in LanczosCoefficients) ser += (float)c / ++y;
        return -tmp + MathF.Log(2.5066283f * ser / x);
    }

    [Hle("_ZSt8_XLgammad")]
    [Hle("_ZSt8_XLgammae")] // long double is double
    double XLgammaD(HleContext c) => XLgamma(HleContext.ArgF64(c, 0));

    [Hle] float _ZSt8_XLgammaf(float x) => XLgammaF(x);

    // ------------------------------------------------------------------ _MP_* (the multiprecision helpers of the
    // linear congruential engines): an _MP_arr is 5 unsigned long long "digits" of 32 bits each, least significant first

    const int MpLen = 5;

    static System.Numerics.BigInteger MpRead(uint a)
    {
        System.Numerics.BigInteger v = 0;
        for (int i = MpLen - 1; i >= 0; i--) v = (v << 32) + GuestMemory.Read<ulong>(a + (uint)i * 8);
        return v;
    }

    static void MpWrite(uint a, System.Numerics.BigInteger v)
    {
        var mask = (System.Numerics.BigInteger)uint.MaxValue;
        for (int i = 0; i < MpLen; i++, v >>= 32) GuestMemory.Write(a + (uint)i * 8, (ulong)(v & mask));
    }

    /// <summary>_MP_Add(_MP_arr u, unsigned long long v): u += v.</summary>
    [Hle] void _ZSt7_MP_AddPyy(uint u, ulong v) => MpWrite(u, MpRead(u) + v);

    /// <summary>_MP_Get(_MP_arr u): the low 64 bits (digit 0 plus digit 1 shifted, as the module adds them).</summary>
    [Hle]
    ulong _ZSt7_MP_GetPy(uint u) => GuestMemory.Read<ulong>(u) + (GuestMemory.Read<ulong>(u + 8) << 32);

    /// <summary>_MP_Mul(_MP_arr w, unsigned long long u, unsigned long long v): w = u * v.</summary>
    [Hle] void _ZSt7_MP_MulPyyy(uint w, ulong u, ulong v) => MpWrite(w, (System.Numerics.BigInteger)u * v);

    /// <summary>_MP_Rem(_MP_arr u, unsigned long long m): u %= m.</summary>
    [Hle] void _ZSt7_MP_RemPyy(uint u, ulong m) { if (m != 0) MpWrite(u, MpRead(u) % m); }
}
