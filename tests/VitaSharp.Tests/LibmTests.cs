using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceLibm with C99 semantics and math_errhandling as libc.suprx (Dinkumware) reports errors: a domain error sets
/// errno EDOM (33), a pole or an overflow ERANGE (34); the hard-float ABI passes doubles in d0-d2.
/// </summary>
[Collection("guest")]
public sealed class LibmTests(GuestMemoryFixture fx) : IDisposable
{
    const int EDOM = 33, ERANGE = 34;
    readonly HleHarness _h = new(fx, "libm");
    Libm M => _h.Module<Libm>();

    public void Dispose() => _h.Dispose();

    static unsafe double D(GuestThread t, string fn, double[] args, params object[] extra)
    {
        for (int i = 0; i < args.Length; i++)
        {
            ((double*)t.Core.State->D)[i] = args[i];
        }

        return Convert.ToDouble(Invoke(t.Process.Hle.Modules.OfType<Libm>().Single(), fn, [t.Hle, .. extra]));
    }

    static uint Errno(GuestThread t) => GuestMemory.R32(t.TlsBase + 0x88 * 4);
    static void ClearErrno(GuestThread t) => GuestMemory.W32(t.TlsBase + 0x88 * 4, 0);

    [Fact]
    public void DoubleFunctionsAndErrors()
    {
        _h.OnGuest(t =>
        {
            double R(string fn, params double[] a) => D(t, fn, a);
            Assert.Equal(0, R("sin", 0));
            Assert.Equal(1, R("cos", 0), 12);
            Assert.Equal(1, R("tan", Math.PI / 4), 12);
            Assert.Equal(Math.PI / 2, R("asin", 1), 12);
            Assert.Equal(0, R("acos", 1), 12);
            Assert.Equal(Math.PI / 4, R("atan", 1), 12);
            Assert.Equal(-Math.PI / 2, R("atan2", -1, 0), 12);
            Assert.Equal(0, R("sinh", 0));
            Assert.Equal(1, R("cosh", 0));
            Assert.Equal(0, R("tanh", 0));
            Assert.Equal(0, R("asinh", 0));
            Assert.Equal(0, R("acosh", 1));
            Assert.Equal(0, R("atanh", 0));
            Assert.Equal(Math.E, R("exp", 1), 12);
            Assert.Equal(1024, R("exp2", 10));
            Assert.Equal(1e-10, R("expm1", 1e-10), 1e-18);
            Assert.Equal(1, R("log", Math.E), 12);
            Assert.Equal(3, R("log10", 1000), 12);
            Assert.Equal(10, R("log2", 1024));
            Assert.Equal(1e-10, R("log1p", 1e-10), 1e-18);
            Assert.Equal(3, R("logb", 8));
            Assert.Equal(1024, R("pow", 2, 10));
            Assert.Equal(3, R("sqrt", 9));
            Assert.Equal(3, R("cbrt", 27), 12);
            Assert.Equal(5, R("hypot", 3, 4));
            Assert.Equal(0, R("erf", 0));
            Assert.Equal(1, R("erfc", 0));
            Assert.Equal(24, R("tgamma", 5), 9);
            Assert.Equal(0, R("lgamma", 1), 9);
            Assert.Equal(3, R("ceil", 2.1));
            Assert.Equal(2, R("floor", 2.9));
            Assert.Equal(-2, R("trunc", -2.9));
            Assert.Equal(3, R("round", 2.5));
            Assert.Equal(-3, R("round", -2.5));
            Assert.Equal(2, R("rint", 2.5));
            Assert.Equal(4, R("nearbyint", 3.5));
            Assert.Equal(2.5, R("fabs", -2.5));
            Assert.Equal(1, R("fmod", 7, 3));
            Assert.Equal(-1, R("fmod", -7, 3));
            Assert.Equal(1, R("remainder", 7, 3));
            Assert.Equal(-1, R("remainder", 8, 3));
            Assert.Equal(-3, R("copysign", 3, -0.0));
            Assert.Equal(2, R("fdim", 5, 3));
            Assert.Equal(0, R("fdim", 3, 5));
            Assert.Equal(1, R("fmax", double.NaN, 1));
            Assert.Equal(1, R("fmin", 1, double.NaN));
            Assert.True(R("nextafter", 1, 2) > 1);
            Assert.Equal(7, R("fma", 2, 3, 1));
            Assert.Equal(1024, D(t, "ldexp", [1], 10));

            // errors: domain → EDOM, pole and overflow → ERANGE, a NaN argument sets nothing
            ClearErrno(t);
            Assert.True(double.IsNaN(R("sqrt", -1)));
            Assert.Equal((uint)EDOM, Errno(t));
            ClearErrno(t);
            Assert.True(double.IsNegativeInfinity(R("log", 0)));
            Assert.Equal((uint)ERANGE, Errno(t));
            ClearErrno(t);
            Assert.True(double.IsPositiveInfinity(R("exp", 1000)));
            Assert.Equal((uint)ERANGE, Errno(t));
            ClearErrno(t);
            Assert.True(double.IsNaN(R("sin", double.NaN)));
            Assert.Equal(0u, Errno(t));
            ClearErrno(t);
            Assert.True(double.IsNaN(R("acos", 2)));
            Assert.Equal((uint)EDOM, Errno(t));
        });
    }

    [Fact]
    public void FloatFunctionsAndDecomposition()
    {
        _h.OnGuest(t =>
        {
            float F(string fn, params float[] a) => Convert.ToSingle(Invoke(M, fn, a.Cast<object>().ToArray()));
            Assert.Equal(0f, F("sinf", 0));
            Assert.Equal(1f, F("cosf", 0));
            Assert.Equal(1f, F("tanf", MathF.PI / 4), 5);
            Assert.Equal(MathF.PI / 2, F("asinf", 1), 5);
            Assert.Equal(0f, F("acosf", 1));
            Assert.Equal(MathF.PI / 4, F("atanf", 1), 5);
            Assert.Equal(MathF.PI / 2, F("atan2f", 1, 0), 5);
            Assert.Equal(0f, F("sinhf", 0));
            Assert.Equal(1f, F("coshf", 0));
            Assert.Equal(0f, F("tanhf", 0));
            Assert.Equal(0f, F("asinhf", 0));
            Assert.Equal(0f, F("acoshf", 1));
            Assert.Equal(0f, F("atanhf", 0));
            Assert.Equal(MathF.E, F("expf", 1), 5);
            Assert.Equal(8f, F("exp2f", 3));
            Assert.Equal(0f, F("expm1f", 0));
            Assert.Equal(1f, F("logf", MathF.E), 5);
            Assert.Equal(2f, F("log10f", 100), 5);
            Assert.Equal(3f, F("log2f", 8));
            Assert.Equal(0f, F("log1pf", 0));
            Assert.Equal(3f, F("logbf", 8));
            Assert.Equal(8f, F("powf", 2, 3));
            Assert.Equal(4f, F("sqrtf", 16));
            Assert.Equal(2f, F("cbrtf", 8), 5);
            Assert.Equal(5f, F("hypotf", 3, 4));
            Assert.Equal(0f, F("erff", 0));
            Assert.Equal(1f, F("erfcf", 0));
            Assert.Equal(6f, F("tgammaf", 4), 4);
            Assert.Equal(0f, F("lgammaf", 2), 5);
            Assert.Equal(3f, F("ceilf", 2.1f));
            Assert.Equal(2f, F("floorf", 2.9f));
            Assert.Equal(-2f, F("truncf", -2.9f));
            Assert.Equal(3f, F("roundf", 2.5f));
            Assert.Equal(2f, F("rintf", 2.5f));
            Assert.Equal(4f, F("nearbyintf", 3.5f));
            Assert.Equal(2f, F("fabsf", -2f));
            Assert.Equal(1f, F("fmodf", 7, 3));
            Assert.Equal(-1f, F("remainderf", 8, 3));
            Assert.Equal(-3f, F("copysignf", 3, -1));
            Assert.Equal(2f, F("fdimf", 5, 3));
            Assert.Equal(2f, F("fmaxf", 1, 2));
            Assert.Equal(1f, F("fminf", 1, 2));
            Assert.True(F("nextafterf", 1, 0) < 1);
            Assert.Equal(7f, F("fmaf", 2, 3, 1));
            Assert.Equal(3, Call(M, "ilogbf", 8f));
            Assert.Equal(2, Call(M, "lrintf", 2.5f));
            Assert.Equal(-3, Call(M, "lroundf", -2.5f));
            Assert.Equal(2L, Convert.ToInt64(Invoke(M, "llrintf", 2.5f)));
            Assert.Equal(3L, Convert.ToInt64(Invoke(M, "llroundf", 2.5f)));
            Assert.Equal(1024f, Convert.ToSingle(Invoke(M, "ldexpf", 1f, 10)));

            // frexp/modf/remquo write through their pointers
            uint p = _h.Alloc(8);
            Assert.Equal(0.5, D(t, "frexp", [8], p));
            Assert.Equal(4u, GuestMemory.R32(p));
            Assert.Equal(0.5f, Convert.ToSingle(Invoke(M, "frexpf", 8f, p)));
            Assert.Equal(0.75, D(t, "modf", [3.75], p));
            Assert.Equal(3.0, GuestMemory.Read<double>(p));
            Assert.Equal(0.75f, Convert.ToSingle(Invoke(M, "modff", 3.75f, p)));
            Assert.Equal(3.0f, GuestMemory.Read<float>(p));
            Assert.Equal(1, D(t, "remquo", [7, 3], p));
            Assert.Equal(2u, GuestMemory.R32(p) & 7);
            Assert.Equal(1f, Convert.ToSingle(Invoke(M, "remquof", 7f, 3f, p)));
            Assert.Equal(3, D(t, "ilogb", [8]));
            Assert.Equal(-3, D(t, "lround", [-2.5]));
            Assert.Equal(2, D(t, "lrint", [2.5]));

            // nan(tag) payloads; the Dinkumware classification helpers
            double nan = Convert.ToDouble(Invoke(M, "nan", "5"));
            Assert.True(double.IsNaN(nan));
            Assert.Equal(5UL, BitConverter.DoubleToUInt64Bits(nan) & 0xFFFFF);
            Assert.True(float.IsNaN(Convert.ToSingle(Invoke(M, "nanf", ""))));
            GuestMemory.Write(p, double.PositiveInfinity);
            Assert.Equal((short)1, (short)Invoke(M, "_Dtest", p)!);     // infinite
            GuestMemory.Write(p, 0.0);
            Assert.Equal((short)0, (short)Invoke(M, "_Dtest", p)!);     // zero
            GuestMemory.Write(p, 1.5f);
            Assert.Equal((short)-1, (short)Invoke(M, "_FDtest", p)!);   // finite
            Assert.Equal((short)2, (short)Invoke(M, "_FDclass", float.NaN)!);
            Assert.Equal(1, Call(M, "_FDsign", -1f));
            Assert.Equal(1, Call(M, "_FFpcomp", 1f, 2f));   // less
            Assert.Equal(2, Call(M, "_FFpcomp", 2f, 2f));   // equal
            Assert.Equal(4, Call(M, "_FFpcomp", 3f, 2f));   // greater
            Assert.Equal(0, Call(M, "_FFpcomp", float.NaN, 2f));
            Assert.Equal(1f, Convert.ToSingle(Invoke(M, "_FSin", 0f, 1u)), 5);  // sin(x + pi/2)
            Assert.Equal(2f, Convert.ToSingle(Invoke(M, "_FLog", 100f, 1)), 5); // base 10
            Assert.Equal(2f, Convert.ToSingle(Invoke(M, "_FCosh", 0f, 2f)));    // 2 cosh 0
        });
    }
}
