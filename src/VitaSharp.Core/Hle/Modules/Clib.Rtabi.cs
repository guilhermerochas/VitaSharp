namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceRtabi (SceLibKernel): the ARM run-time ABI helpers the firmware exports (integer division, 64-bit shifts,
/// compares and multiply, float to 64-bit conversions). They use the base AAPCS (doubles in r0:r1, floats in r0) and
/// return quotient/remainder pairs in r0-r3. Division by zero as the 3.74 libkernel does it: the quotient saturates
/// (unsigned: all ones unless the numerator is 0; signed: INT_MAX/INT_MIN by the numerator's sign, 0 for 0) and the
/// remainder is 0, before the (returning) division-by-zero handler.
/// </summary>
public sealed partial class Clib
{
    static int SatDiv(int n) => n > 0 ? int.MaxValue : n < 0 ? int.MinValue : 0;
    static long SatDiv(long n) => n > 0 ? long.MaxValue : n < 0 ? long.MinValue : 0;

    [Hle] uint __rtabi_uidiv(uint n, uint d) => d == 0 ? (n == 0 ? 0 : uint.MaxValue) : n / d;

    [Hle]
    int __rtabi_idiv(int n, int d) => d == 0 ? SatDiv(n) : d == -1 ? unchecked(-n) : n / d;

    [Hle]
    unsafe void __rtabi_uidivmod(HleContext ctx, uint n, uint d)
    {
        uint q = d == 0 ? (n == 0 ? 0 : uint.MaxValue) : n / d, r = d == 0 ? 0 : n % d;
        ctx.Cpu->R[0] = q;
        ctx.Cpu->R[1] = r;
    }

    [Hle]
    unsafe void __rtabi_idivmod(HleContext ctx, int n, int d)
    {
        int q = d == 0 ? SatDiv(n) : d == -1 ? unchecked(-n) : n / d, r = d is 0 or -1 ? 0 : n % d;
        ctx.Cpu->R[0] = (uint)q;
        ctx.Cpu->R[1] = (uint)r;
    }

    [Hle]
    unsafe void __rtabi_uldivmod(HleContext ctx, ulong n, ulong d)
    {
        ulong q = d == 0 ? (n == 0 ? 0 : ulong.MaxValue) : n / d, r = d == 0 ? 0 : n % d;
        ctx.Cpu->R[0] = (uint)q; ctx.Cpu->R[1] = (uint)(q >> 32);
        ctx.Cpu->R[2] = (uint)r; ctx.Cpu->R[3] = (uint)(r >> 32);
    }

    [Hle]
    unsafe void __rtabi_ldivmod(HleContext ctx, long n, long d)
    {
        long q = d == 0 ? SatDiv(n) : d == -1 ? unchecked(-n) : n / d, r = d is 0 or -1 ? 0 : n % d;
        ctx.Cpu->R[0] = (uint)q; ctx.Cpu->R[1] = (uint)((ulong)q >> 32);
        ctx.Cpu->R[2] = (uint)r; ctx.Cpu->R[3] = (uint)((ulong)r >> 32);
    }

    /// <summary>Shift counts follow the RTABI: only the low byte counts, 64 or more gives 0 (or the sign).</summary>
    [Hle] ulong __rtabi_llsl(ulong v, uint n) => (n & 0xFF) >= 64 ? 0 : v << (int)(n & 0xFF);
    [Hle] ulong __rtabi_llsr(ulong v, uint n) => (n & 0xFF) >= 64 ? 0 : v >> (int)(n & 0xFF);
    [Hle] long __rtabi_lasr(long v, uint n) => v >> (int)Math.Min(n & 0xFF, 63u);
    [Hle] ulong __rtabi_lmul(ulong a, ulong b) => unchecked(a * b);
    [Hle] int __rtabi_lcmp(long a, long b) => a < b ? -1 : a > b ? 1 : 0;
    [Hle] int __rtabi_ulcmp(ulong a, ulong b) => a < b ? -1 : a > b ? 1 : 0;

    // .NET's floating-point to integer casts saturate and turn NaN into 0, like VCVT
    [Hle] long __rtabi_d2lz(ulong bits) => (long)BitConverter.UInt64BitsToDouble(bits);
    [Hle] ulong __rtabi_d2ulz(ulong bits) => (ulong)BitConverter.UInt64BitsToDouble(bits);
    [Hle] long __rtabi_f2lz(uint bits) => (long)BitConverter.UInt32BitsToSingle(bits);
    [Hle] ulong __rtabi_f2ulz(uint bits) => (ulong)BitConverter.UInt32BitsToSingle(bits);
}
