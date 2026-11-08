namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibm (libc.suprx, FW 3.74, Dinkumware). Hard-float ABI: doubles in d0-d2, floats in s0-s2, results in d0/s0
/// (the firmware wrappers store d0/s0 straight to the stack). long double is double. Errors follow math_errhandling
/// as the module's _Feraise does: a domain error (NaN from a non-NaN argument) sets errno EDOM, a pole or overflow
/// (an infinite result from finite arguments) ERANGE. The Dinkumware internals the C++ library calls are here too:
/// _Dtest/_Dclass (2 NaN, 1 infinite, 0 zero, -1 finite, -2 subnormal), _Fpcomp (1 less, 2 equal, 4 greater, 0
/// unordered), _Sin/_Sinx(x, qoff[, quads]) = sin(x + (qoff+quads)·π/2), _Log(x, base) (0 e, 1 10, -1 2),
/// _Cosh/_Sinh(x, y) = y·cosh/sinh(x), _Exp(*px, y, eoff): *px = y·e^*px·2^eoff.
/// </summary>
public sealed class Libm : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    const int EDOM = 33, ERANGE = 34;

    static double D(HleContext c, int i) => HleContext.ArgF64(c, i);

    static double Check(double r, double a, double b = 0, double cc = 0)
    {
        bool argNaN = double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(cc);
        bool argInf = double.IsInfinity(a) || double.IsInfinity(b) || double.IsInfinity(cc);
        if (double.IsNaN(r) && !argNaN) Libc.SetErrno(EDOM);
        else if (double.IsInfinity(r) && !argInf && !argNaN) Libc.SetErrno(ERANGE);
        return r;
    }

    static float CheckF(float r, float a, float b = 0, float cc = 0) => (float)Check(r, a, b, cc);

    static double M(HleContext c, Func<double, double> f) { double x = D(c, 0); return Check(f(x), x); }
    static double M2(HleContext c, Func<double, double, double> f) { double x = D(c, 0), y = D(c, 1); return Check(f(x, y), x, y); }
    static float F(float x, Func<float, float> f) => CheckF(f(x), x);
    static float F2(float x, float y, Func<float, float, float> f) => CheckF(f(x, y), x, y);

    // ------------------------------------------------------------------ trigonometric / hyperbolic

    [Hle("sin")] [Hle("sinl")] double sin(HleContext c) => M(c, Math.Sin);
    [Hle("cos")] [Hle("cosl")] double cos(HleContext c) => M(c, Math.Cos);
    [Hle("tan")] [Hle("tanl")] double tan(HleContext c) => M(c, Math.Tan);
    [Hle("asin")] [Hle("asinl")] double asin(HleContext c) => M(c, Math.Asin);
    [Hle("acos")] [Hle("acosl")] double acos(HleContext c) => M(c, Math.Acos);
    [Hle("atan")] [Hle("atanl")] double atan(HleContext c) => M(c, Math.Atan);
    [Hle("atan2")] [Hle("atan2l")] double atan2(HleContext c) => M2(c, Math.Atan2);
    [Hle("sinh")] [Hle("sinhl")] double sinh(HleContext c) => M(c, Math.Sinh);
    [Hle("cosh")] [Hle("coshl")] double cosh(HleContext c) => M(c, Math.Cosh);
    [Hle("tanh")] [Hle("tanhl")] double tanh(HleContext c) => M(c, Math.Tanh);
    [Hle("asinh")] [Hle("asinhl")] double asinh(HleContext c) => M(c, Math.Asinh);
    [Hle("acosh")] [Hle("acoshl")] double acosh(HleContext c) => M(c, Math.Acosh);
    [Hle("atanh")] [Hle("atanhl")] double atanh(HleContext c) => M(c, Math.Atanh);

    [Hle] float sinf(float x) => F(x, MathF.Sin);
    [Hle] float cosf(float x) => F(x, MathF.Cos);
    [Hle] float tanf(float x) => F(x, MathF.Tan);
    [Hle] float asinf(float x) => F(x, MathF.Asin);
    [Hle] float acosf(float x) => F(x, MathF.Acos);
    [Hle] float atanf(float x) => F(x, MathF.Atan);
    [Hle] float atan2f(float y, float x) => F2(y, x, MathF.Atan2);
    [Hle] float sinhf(float x) => F(x, MathF.Sinh);
    [Hle] float coshf(float x) => F(x, MathF.Cosh);
    [Hle] float tanhf(float x) => F(x, MathF.Tanh);
    [Hle] float asinhf(float x) => F(x, MathF.Asinh);
    [Hle] float acoshf(float x) => F(x, MathF.Acosh);
    [Hle] float atanhf(float x) => F(x, MathF.Atanh);

    // ------------------------------------------------------------------ exponential / logarithmic / power

    [Hle("exp")] [Hle("expl")] double exp(HleContext c) => M(c, Math.Exp);
    [Hle("exp2")] [Hle("exp2l")] double exp2(HleContext c) => M(c, x => Math.Pow(2, x));
    [Hle("expm1")] [Hle("expm1l")] double expm1(HleContext c) => M(c, ExpM1);
    [Hle("log")] [Hle("logl")] double log(HleContext c) => M(c, Math.Log);
    [Hle("log10")] [Hle("log10l")] double log10(HleContext c) => M(c, Math.Log10);
    [Hle("log2")] [Hle("log2l")] double log2(HleContext c) => M(c, Math.Log2);
    [Hle("log1p")] [Hle("log1pl")] double log1p(HleContext c) => M(c, Log1P);
    [Hle("logb")] [Hle("logbl")] double logb(HleContext c) => M(c, LogB);
    [Hle("pow")] [Hle("powl")] double pow(HleContext c) => M2(c, Math.Pow);
    [Hle("sqrt")] [Hle("sqrtl")] double sqrt(HleContext c) => M(c, Math.Sqrt);
    [Hle("cbrt")] [Hle("cbrtl")] double cbrt(HleContext c) => M(c, Math.Cbrt);
    [Hle("hypot")] [Hle("hypotl")] double hypot(HleContext c) => M2(c, Hypot);

    [Hle] float expf(float x) => F(x, MathF.Exp);
    [Hle] float exp2f(float x) => F(x, v => MathF.Pow(2, v));
    [Hle] float expm1f(float x) => F(x, v => (float)ExpM1(v));
    [Hle] float logf(float x) => F(x, MathF.Log);
    [Hle] float log10f(float x) => F(x, MathF.Log10);
    [Hle] float log2f(float x) => F(x, MathF.Log2);
    [Hle] float log1pf(float x) => F(x, v => (float)Log1P(v));
    [Hle] float logbf(float x) => F(x, v => (float)LogB(v));
    [Hle] float powf(float x, float y) => F2(x, y, MathF.Pow);
    [Hle] float sqrtf(float x) => F(x, MathF.Sqrt);
    [Hle] float cbrtf(float x) => F(x, MathF.Cbrt);
    [Hle] float hypotf(float x, float y) => F2(x, y, (a, b) => (float)Hypot(a, b));

    static double ExpM1(double x) => Math.Abs(x) < 1e-5 ? x + x * x / 2 + x * x * x / 6 : Math.Exp(x) - 1;
    static double Log1P(double x) => Math.Abs(x) < 1e-5 ? x - x * x / 2 + x * x * x / 3 : Math.Log(1 + x);
    static double LogB(double x) => x == 0 ? double.NegativeInfinity : double.IsInfinity(x) ? double.PositiveInfinity : double.IsNaN(x) ? x : Math.ILogB(x);

    static double Hypot(double x, double y)
    {
        if (double.IsInfinity(x) || double.IsInfinity(y)) return double.PositiveInfinity;
        x = Math.Abs(x); y = Math.Abs(y);
        double m = Math.Max(x, y), n = Math.Min(x, y);
        if (m == 0) return 0;
        double r = n / m;
        return m * Math.Sqrt(1 + r * r);
    }

    // ------------------------------------------------------------------ error and gamma functions

    [Hle("erf")] [Hle("erfl")] double erf(HleContext c) => M(c, Erf);
    [Hle("erfc")] [Hle("erfcl")] double erfc(HleContext c) => M(c, x => 1 - Erf(x));
    [Hle("lgamma")] [Hle("lgammal")] double lgamma(HleContext c) => M(c, LGamma);
    [Hle("tgamma")] [Hle("tgammal")] double tgamma(HleContext c) => M(c, TGamma);
    [Hle] float erff(float x) => F(x, v => (float)Erf(v));
    [Hle] float erfcf(float x) => F(x, v => (float)(1 - Erf(v)));
    [Hle] float lgammaf(float x) => F(x, v => (float)LGamma(v));
    [Hle] float tgammaf(float x) => F(x, v => (float)TGamma(v));

    /// <summary>erf by its series near 0 and the continued fraction of erfc beyond (double precision).</summary>
    internal static double Erf(double x)
    {
        if (double.IsNaN(x)) return x;
        if (double.IsInfinity(x)) return Math.Sign(x);
        double ax = Math.Abs(x);
        if (ax < 2.5)
        {
            double sum = ax, term = ax, x2 = ax * ax;
            for (int n = 1; n < 200; n++)
            {
                term *= -x2 / n;
                double t = term / (2 * n + 1);
                sum += t;
                if (Math.Abs(t) < 1e-17 * Math.Abs(sum)) break;
            }
            return Math.Sign(x) * 2 / Math.Sqrt(Math.PI) * sum;
        }
        if (ax > 6) return Math.Sign(x);
        // erfc(x) = exp(-x^2)/sqrt(pi) * 1/(x+ 1/2/(x+ 1/(x+ 3/2/(x+ ...)))) (Lentz)
        double f = ax, cc = ax, d = 0;
        for (int n = 1; n < 300; n++)
        {
            double an = n / 2.0;
            d = ax + an * d; d = d == 0 ? 1e-300 : 1 / d;
            cc = ax + an / cc; if (cc == 0) cc = 1e-300;
            double delta = cc * d;
            f *= delta;
            if (Math.Abs(delta - 1) < 1e-16) break;
        }
        double erfc = Math.Exp(-ax * ax) / Math.Sqrt(Math.PI) / f;
        return Math.Sign(x) * (1 - erfc);
    }

    static readonly double[] Lanczos =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
        12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    internal static double LGamma(double x)
    {
        if (double.IsNaN(x)) return x;
        if (double.IsInfinity(x)) return double.PositiveInfinity;
        if (x <= 0 && Math.Floor(x) == x) return double.PositiveInfinity; // pole
        if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LGamma(1 - x);
        x -= 1;
        double a = Lanczos[0], t = x + 7.5;
        for (int i = 1; i < 9; i++) a += Lanczos[i] / (x + i);
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    internal static double TGamma(double x)
    {
        if (double.IsNaN(x)) return x;
        if (x == 0) return 1 / x; // ±inf, pole
        if (x < 0 && Math.Floor(x) == x) return double.NaN;
        if (double.IsPositiveInfinity(x)) return x;
        if (double.IsNegativeInfinity(x)) return double.NaN;
        if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * TGamma(1 - x));
        if (x > 171.7) return double.PositiveInfinity;
        if (x == Math.Floor(x) && x < 30) { double f = 1; for (int i = 2; i < x; i++) f *= i; return f; }
        x -= 1;
        double a = Lanczos[0], t = x + 7.5;
        for (int i = 1; i < 9; i++) a += Lanczos[i] / (x + i);
        return Math.Sqrt(2 * Math.PI) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * a;
    }

    // ------------------------------------------------------------------ rounding / remainder / manipulation

    [Hle("ceil")] [Hle("ceill")] double ceil(HleContext c) => Math.Ceiling(D(c, 0));
    [Hle("floor")] [Hle("floorl")] double floor(HleContext c) => Math.Floor(D(c, 0));
    [Hle("trunc")] [Hle("truncl")] double trunc(HleContext c) => Math.Truncate(D(c, 0));
    [Hle("round")] [Hle("roundl")] double round(HleContext c) => Math.Round(D(c, 0), MidpointRounding.AwayFromZero);
    [Hle("rint")] [Hle("rintl")] double rint(HleContext c) => Math.Round(D(c, 0), MidpointRounding.ToEven);
    [Hle("nearbyint")] [Hle("nearbyintl")] double nearbyint(HleContext c) => Math.Round(D(c, 0), MidpointRounding.ToEven);
    [Hle("fabs")] [Hle("fabsl")] double fabs(HleContext c) => Math.Abs(D(c, 0));
    [Hle("fmod")] [Hle("fmodl")] double fmod(HleContext c) => M2(c, (x, y) => x % y);
    [Hle("remainder")] [Hle("remainderl")] double remainder(HleContext c) => M2(c, Math.IEEERemainder);
    [Hle("copysign")] [Hle("copysignl")] double copysign(HleContext c) => Math.CopySign(D(c, 0), D(c, 1));
    [Hle("fdim")] [Hle("fdiml")] double fdim(HleContext c) => M2(c, (x, y) => double.IsNaN(x) || double.IsNaN(y) ? double.NaN : x > y ? x - y : 0);
    [Hle("fmax")] [Hle("fmaxl")] double fmax(HleContext c) => FMax(D(c, 0), D(c, 1));
    [Hle("fmin")] [Hle("fminl")] double fmin(HleContext c) => FMin(D(c, 0), D(c, 1));
    [Hle("nextafter")] [Hle("nextafterl")] [Hle("nexttoward")] [Hle("nexttowardl")] double nextafter(HleContext c) => M2(c, NextAfter);
    [Hle("fma")] [Hle("fmal")] double fma(HleContext c) { double x = D(c, 0), y = D(c, 1), z = D(c, 2); return Check(Math.FusedMultiplyAdd(x, y, z), x, y, z); }

    [Hle] float ceilf(float x) => MathF.Ceiling(x);
    [Hle] float floorf(float x) => MathF.Floor(x);
    [Hle] float truncf(float x) => MathF.Truncate(x);
    [Hle] float roundf(float x) => MathF.Round(x, MidpointRounding.AwayFromZero);
    [Hle] float rintf(float x) => MathF.Round(x, MidpointRounding.ToEven);
    [Hle] float nearbyintf(float x) => MathF.Round(x, MidpointRounding.ToEven);
    [Hle] float fabsf(float x) => MathF.Abs(x);
    [Hle] float fmodf(float x, float y) => F2(x, y, (a, b) => a % b);
    [Hle] float remainderf(float x, float y) => F2(x, y, MathF.IEEERemainder);
    [Hle] float copysignf(float x, float y) => MathF.CopySign(x, y);
    [Hle] float fdimf(float x, float y) => F2(x, y, (a, b) => float.IsNaN(a) || float.IsNaN(b) ? float.NaN : a > b ? a - b : 0);
    [Hle] float fmaxf(float x, float y) => (float)FMax(x, y);
    [Hle] float fminf(float x, float y) => (float)FMin(x, y);
    [Hle] float nextafterf(float x, float y) => float.IsNaN(x) || float.IsNaN(y) ? x + y : x == y ? y : y > x ? MathF.BitIncrement(x) : MathF.BitDecrement(x);
    /// <summary>nexttowardf(float, long double): the direction is a double (d1? no: s0 then d1 for the long double).</summary>
    [Hle] float nexttowardf(HleContext c) { float x = c.Fpr(0); double y = D(c, 1); return double.IsNaN(y) || float.IsNaN(x) ? (float)(x + y) : x == y ? (float)y : y > x ? MathF.BitIncrement(x) : MathF.BitDecrement(x); }
    [Hle] float fmaf(float x, float y, float z) => CheckF(MathF.FusedMultiplyAdd(x, y, z), x, y, z);

    static double FMax(double x, double y) => double.IsNaN(x) ? y : double.IsNaN(y) ? x : Math.Max(x, y);
    static double FMin(double x, double y) => double.IsNaN(x) ? y : double.IsNaN(y) ? x : Math.Min(x, y);
    static double NextAfter(double x, double y) => double.IsNaN(x) || double.IsNaN(y) ? x + y : x == y ? y : y > x ? Math.BitIncrement(x) : Math.BitDecrement(x);

    [Hle("ilogb")] [Hle("ilogbl")] int ilogb(HleContext c) => ILogB(D(c, 0));
    [Hle] int ilogbf(float x) => ILogB(x);

    /// <summary>ilogb: FP_ILOGB0 (INT_MIN) for 0, FP_ILOGBNAN (INT_MAX) for NaN, INT_MAX for infinity (EDOM).</summary>
    static int ILogB(double x)
    {
        if (x == 0) { Libc.SetErrno(EDOM); return int.MinValue; }
        if (double.IsNaN(x) || double.IsInfinity(x)) { Libc.SetErrno(EDOM); return int.MaxValue; }
        return Math.ILogB(x);
    }

    static int ToInt32(double r)
    {
        if (double.IsNaN(r) || r >= 2147483648.0 || r < -2147483648.0) { Libc.SetErrno(EDOM); return int.MinValue; }
        return (int)r;
    }

    static long ToInt64(double r)
    {
        if (double.IsNaN(r) || r >= 9223372036854775808.0 || r < -9223372036854775808.0) { Libc.SetErrno(EDOM); return long.MinValue; }
        return (long)r;
    }

    [Hle("lrint")] [Hle("lrintl")] int lrint(HleContext c) => ToInt32(Math.Round(D(c, 0), MidpointRounding.ToEven));
    [Hle("lround")] [Hle("lroundl")] int lround(HleContext c) => ToInt32(Math.Round(D(c, 0), MidpointRounding.AwayFromZero));
    [Hle("llrint")] [Hle("llrintl")] long llrint(HleContext c) => ToInt64(Math.Round(D(c, 0), MidpointRounding.ToEven));
    [Hle("llround")] [Hle("llroundl")] long llround(HleContext c) => ToInt64(Math.Round(D(c, 0), MidpointRounding.AwayFromZero));
    [Hle] int lrintf(float x) => ToInt32(MathF.Round(x, MidpointRounding.ToEven));
    [Hle] int lroundf(float x) => ToInt32(MathF.Round(x, MidpointRounding.AwayFromZero));
    [Hle] long llrintf(float x) => ToInt64(MathF.Round(x, MidpointRounding.ToEven));
    [Hle] long llroundf(float x) => ToInt64(MathF.Round(x, MidpointRounding.AwayFromZero));

    /// <summary>frexp(x, int *exp): mantissa in [0.5, 1) and the exponent (0 for 0, NaN, infinity).</summary>
    static double Frexp(double x, out int e)
    {
        e = 0;
        if (x == 0 || double.IsNaN(x) || double.IsInfinity(x)) return x;
        e = Math.ILogB(x) + 1;
        return Math.ScaleB(x, -e);
    }

    [Hle("frexp")] [Hle("frexpl")] double frexp(HleContext c, uint pexp) { double r = Frexp(D(c, 0), out int e); Memory.GuestMemory.W32(pexp, (uint)e); return r; }
    [Hle] float frexpf(float x, uint pexp) { float r = (float)Frexp(x, out int e); Memory.GuestMemory.W32(pexp, (uint)e); return r; }

    [Hle("ldexp")] [Hle("ldexpl")] [Hle("scalbn")] [Hle("scalbnl")] [Hle("scalbln")] [Hle("scalblnl")]
    double ldexp(HleContext c, int e) { double x = D(c, 0); return Check(Math.ScaleB(x, e), x); }
    [Hle("ldexpf")] [Hle("scalbnf")] [Hle("scalblnf")]
    float ldexpf(float x, int e) => CheckF(MathF.ScaleB(x, e), x);

    [Hle("modf")] [Hle("modfl")]
    double modf(HleContext c, uint ip)
    {
        double x = D(c, 0), i = double.IsInfinity(x) ? x : Math.Truncate(x);
        Memory.GuestMemory.Write(ip, i);
        return double.IsInfinity(x) ? Math.CopySign(0, x) : double.IsNaN(x) ? x : Math.CopySign(x - i, x);
    }

    [Hle]
    float modff(float x, uint ip)
    {
        float i = float.IsInfinity(x) ? x : MathF.Truncate(x);
        Memory.GuestMemory.Write(ip, i);
        return float.IsInfinity(x) ? MathF.CopySign(0, x) : float.IsNaN(x) ? x : MathF.CopySign(x - i, x);
    }

    static double RemQuo(double x, double y, out int quo)
    {
        quo = 0;
        double r = Math.IEEERemainder(x, y);
        if (double.IsNaN(r)) return r;
        double q = Math.Round((x - r) / y);
        quo = (int)(Math.Abs(q) % 8) * (q < 0 ? -1 : 1);
        return r;
    }

    [Hle("remquo")] [Hle("remquol")]
    double remquo(HleContext c, uint pquo) { double x = D(c, 0), y = D(c, 1); double r = Check(RemQuo(x, y, out int q), x, y); Memory.GuestMemory.W32(pquo, (uint)q); return r; }
    [Hle] float remquof(float x, float y, uint pquo) { float r = (float)Check(RemQuo(x, y, out int q), x, y); Memory.GuestMemory.W32(pquo, (uint)q); return r; }

    /// <summary>nan(tag): a quiet NaN carrying the tag's number in its payload ("" = the default NaN).</summary>
    static ulong NanPayload(string tag)
    {
        ulong v = 0;
        try { v = tag.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? System.Convert.ToUInt64(tag[2..], 16) : tag.Length > 0 && tag.All(char.IsDigit) ? ulong.Parse(tag) : 0; }
        catch (Exception e) when (e is FormatException or OverflowException) { }
        return v;
    }

    [Hle("nan")] [Hle("nanl")] double nan(string tag) => BitConverter.UInt64BitsToDouble(0x7FF8000000000000UL | (NanPayload(tag) & 0x0007FFFFFFFFFFFFUL));
    [Hle] float nanf(string tag) => BitConverter.UInt32BitsToSingle(0x7FC00000u | (uint)(NanPayload(tag) & 0x003FFFFF));

    // ------------------------------------------------------------------ Dinkumware internals

    static short Dtest(double x) => double.IsNaN(x) ? (short)2 : double.IsInfinity(x) ? (short)1 : x == 0 ? (short)0 : double.IsSubnormal(x) ? (short)-2 : (short)-1;

    [Hle("_Dclass")] [Hle("_LDclass")] short _Dclass(HleContext c) => Dtest(D(c, 0));
    static short FDtest(float x) => float.IsNaN(x) ? (short)2 : float.IsInfinity(x) ? (short)1 : x == 0 ? (short)0 : float.IsSubnormal(x) ? (short)-2 : (short)-1;
    [Hle] short _FDclass(float x) => FDtest(x);
    [Hle("_Dtest")] [Hle("_LDtest")] short _Dtest(uint px) => Dtest(Memory.GuestMemory.Read<double>(px));
    [Hle] short _FDtest(uint px) => FDtest(Memory.GuestMemory.Read<float>(px));
    [Hle("_Dsign")] [Hle("_LDsign")] int _Dsign(HleContext c) => double.IsNegative(D(c, 0)) ? 1 : 0;
    [Hle] int _FDsign(float x) => float.IsNegative(x) ? 1 : 0;

    static int Fpcomp(double x, double y) => double.IsNaN(x) || double.IsNaN(y) ? 0 : x < y ? 1 : x == y ? 2 : 4;
    [Hle("_Fpcomp")] [Hle("_LFpcomp")] int _Fpcomp(HleContext c) => Fpcomp(D(c, 0), D(c, 1));
    [Hle] int _FFpcomp(float x, float y) => Fpcomp(x, y);

    static double Quadrant(double x, uint q) => (q & 3) switch { 0 => Math.Sin(x), 1 => Math.Cos(x), 2 => -Math.Sin(x), _ => -Math.Cos(x) };

    [Hle("_Sin")] [Hle("_LSin")] double _Sin(HleContext c, uint qoff) { double x = D(c, 0); return Check(Quadrant(x, qoff), x); }
    [Hle("_Sinx")] [Hle("_LSinx")] double _Sinx(HleContext c, uint qoff, int quads) { double x = D(c, 0); return Check(Quadrant(x, qoff + (uint)quads), x); }
    [Hle] float _FSin(float x, uint qoff) => CheckF((float)Quadrant(x, qoff), x);
    [Hle] float _FSinx(float x, uint qoff, int quads) => CheckF((float)Quadrant(x, qoff + (uint)quads), x);

    static double LogBase(double x, int b) => b switch { 0 => Math.Log(x), 1 => Math.Log10(x), _ => Math.Log2(x) };
    [Hle("_Log")] [Hle("_LLog")] double _Log(HleContext c, int b) { double x = D(c, 0); return Check(LogBase(x, b), x); }
    [Hle] float _FLog(float x, int b) => CheckF((float)LogBase(x, b), x);

    [Hle("_Cosh")] [Hle("_LCosh")] double _Cosh(HleContext c) { double x = D(c, 0), y = D(c, 1); return Check(y * Math.Cosh(x), x, y); }
    [Hle("_Sinh")] [Hle("_LSinh")] double _Sinh(HleContext c) { double x = D(c, 0), y = D(c, 1); return Check(y * Math.Sinh(x), x, y); }
    [Hle] float _FCosh(float x, float y) => CheckF(y * MathF.Cosh(x), x, y);
    [Hle] float _FSinh(float x, float y) => CheckF(y * MathF.Sinh(x), x, y);

    /// <summary>_Exp(double *px, double y, short eoff): *px = y·e^*px·2^eoff; returns 2 (NaN), 1 (overflow to
    /// infinity) or 0.</summary>
    [Hle("_Exp")] [Hle("_LExp")]
    short _Exp(HleContext c, uint px, short eoff)
    {
        double x = Memory.GuestMemory.Read<double>(px), y = D(c, 0);
        double r = y * Math.ScaleB(Math.Exp(x), eoff);
        Memory.GuestMemory.Write(px, r);
        return double.IsNaN(r) ? (short)2 : double.IsInfinity(r) ? (short)1 : (short)0;
    }

    [Hle]
    short _FExp(uint px, float y, short eoff)
    {
        float x = Memory.GuestMemory.Read<float>(px);
        float r = y * MathF.ScaleB(MathF.Exp(x), eoff);
        Memory.GuestMemory.Write(px, r);
        return float.IsNaN(r) ? (short)2 : float.IsInfinity(r) ? (short)1 : (short)0;
    }
}
