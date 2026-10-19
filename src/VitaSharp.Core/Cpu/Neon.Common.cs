using System.Runtime.CompilerServices;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

/// <summary>Advanced SIMD (NEON) handlers. Instructions are always given in their ARM encoding.</summary>
internal static unsafe partial class Neon
{
    // Register numbering: Vd = D:Vd(15:12), Vn = N:Vn(19:16), Vm = M:Vm(3:0)
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint Rd(uint ins) => (Bit(ins, 22) << 4) | Bits(ins, 15, 12);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint Rn(uint ins) => (Bit(ins, 7) << 4) | Bits(ins, 19, 16);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint Rm(uint ins) => (Bit(ins, 5) << 4) | (ins & 0xF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static byte* Reg(CpuState* s, uint d) => (byte*)&s->D[d & 31];

    const uint FpscrQC = 1u << 27;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] static void SetQC(CpuState* s) => s->Fpscr |= FpscrQC;

    // ---------------------------------------------------------- element access (esize in bits)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong GetU(byte* v, int i, int esize) => esize switch
    {
        8 => v[i],
        16 => ((ushort*)v)[i],
        32 => ((uint*)v)[i],
        _ => ((ulong*)v)[i],
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetS(byte* v, int i, int esize) => esize switch
    {
        8 => ((sbyte*)v)[i],
        16 => ((short*)v)[i],
        32 => ((int*)v)[i],
        _ => ((long*)v)[i],
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Get(byte* v, int i, int esize, bool unsigned) => unsigned ? (long)GetU(v, i, esize) : GetS(v, i, esize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Set(byte* v, int i, int esize, ulong x)
    {
        switch (esize)
        {
            case 8: v[i] = (byte)x; break;
            case 16: ((ushort*)v)[i] = (ushort)x; break;
            case 32: ((uint*)v)[i] = (uint)x; break;
            default: ((ulong*)v)[i] = x; break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Mask(int esize) => esize == 64 ? ulong.MaxValue : (1ul << esize) - 1;

    // ---------------------------------------------------------- saturation helpers

    internal static long SatS(CpuState* s, Int128 v, int esize)
    {
        Int128 max = (Int128.One << (esize - 1)) - 1, min = -(Int128.One << (esize - 1));
        if (v > max) { SetQC(s); return (long)max; }
        if (v < min) { SetQC(s); return (long)min; }
        return (long)v;
    }

    internal static ulong SatU(CpuState* s, Int128 v, int esize)
    {
        Int128 max = esize == 64 ? (Int128)ulong.MaxValue : (Int128)((1ul << esize) - 1);
        if (v > max) { SetQC(s); return (ulong)max; }
        if (v < 0) { SetQC(s); return 0; }
        return (ulong)v;
    }

    // ---------------------------------------------------------- float helpers (Advanced SIMD: FZ + DN, round to nearest)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Ftz(float v)
    {
        uint b = BitConverter.SingleToUInt32Bits(v);
        if ((b & 0x7F800000) == 0 && (b & 0x7FFFFF) != 0) return BitConverter.UInt32BitsToSingle(b & 0x80000000);
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float FRes(float v)
    {
        if (float.IsNaN(v)) return BitConverter.UInt32BitsToSingle(0x7FC00000);
        return Ftz(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] static float F(byte* v, int i) => Ftz(((float*)v)[i]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static void SetF(byte* v, int i, float x) => ((float*)v)[i] = FRes(x);

    internal static float FMax(float a, float b)
    {
        if (float.IsNaN(a) || float.IsNaN(b)) return BitConverter.UInt32BitsToSingle(0x7FC00000); // Advanced SIMD: always the default NaN
        if (a == 0 && b == 0) return BitConverter.SingleToUInt32Bits(a) == 0 ? a : b;
        return a > b ? a : b;
    }

    internal static float FMin(float a, float b)
    {
        if (float.IsNaN(a) || float.IsNaN(b)) return BitConverter.UInt32BitsToSingle(0x7FC00000); // Advanced SIMD: always the default NaN
        if (a == 0 && b == 0) return BitConverter.SingleToUInt32Bits(a) != 0 ? a : b;
        return a < b ? a : b;
    }

    // ARM ARM "recip_estimate" / "recip_sqrt_estimate": 9-bit table-equivalent computations.
    static double RecipEstimateCore(double a) // 0.5 <= a < 1.0
    {
        int q = (int)(a * 512.0);
        double r = 1.0 / ((q + 0.5) / 512.0);
        int s = (int)(256.0 * r + 0.5);
        return s / 256.0;
    }

    static double RecipSqrtEstimateCore(double a) // 0.25 <= a < 1.0
    {
        double r;
        if (a < 0.5) { int q0 = (int)(a * 512.0); r = 1.0 / Math.Sqrt((q0 + 0.5) / 512.0); }
        else { int q1 = (int)(a * 256.0); r = 1.0 / Math.Sqrt((q1 + 0.5) / 256.0); }
        int s = (int)(256.0 * r + 0.5);
        return s / 256.0;
    }

    internal static float RecipEstimate(float a)
    {
        uint bits = BitConverter.SingleToUInt32Bits(a);
        uint sign = bits & 0x80000000;
        int exp = (int)((bits >> 23) & 0xFF);
        if (float.IsNaN(a)) return BitConverter.UInt32BitsToSingle(0x7FC00000);
        if (float.IsInfinity(a)) return BitConverter.UInt32BitsToSingle(sign);
        if (exp == 0) return BitConverter.UInt32BitsToSingle(sign | 0x7F800000); // zero / flushed denormal
        if (Math.Abs(a) >= MathF.Pow(2, 126)) return BitConverter.UInt32BitsToSingle(sign); // underflows (FZ)
        double scaled = BitConverter.UInt64BitsToDouble(0x3FE0000000000000ul | ((ulong)(bits & 0x7FFFFF) << 29));
        int resultExp = 253 - exp;
        double est = RecipEstimateCore(scaled);
        ulong estBits = BitConverter.DoubleToUInt64Bits(est);
        uint frac = (uint)((estBits >> 29) & 0x7FFFFF);
        return BitConverter.UInt32BitsToSingle(sign | ((uint)(resultExp & 0xFF) << 23) | frac);
    }

    internal static float RSqrtEstimate(float a)
    {
        uint bits = BitConverter.SingleToUInt32Bits(a);
        int exp = (int)((bits >> 23) & 0xFF);
        if (float.IsNaN(a)) return BitConverter.UInt32BitsToSingle(0x7FC00000);
        if (exp == 0) return BitConverter.UInt32BitsToSingle((bits & 0x80000000) | 0x7F800000);
        if ((bits & 0x80000000) != 0) return BitConverter.UInt32BitsToSingle(0x7FC00000);
        if (float.IsPositiveInfinity(a)) return 0f;
        // ARM ARM FPRSqrtEstimate: an even exponent scales the operand into [0.5, 1.0), an odd one into [0.25, 0.5)
        // (e.g. 1.0 has exponent 127: scaled 0.25, estimate 1.996 -> 0.998). Reversed, every estimate is off by sqrt(2).
        double scaled = (exp & 1) == 0
            ? BitConverter.UInt64BitsToDouble(0x3FE0000000000000ul | ((ulong)(bits & 0x7FFFFF) << 29))
            : BitConverter.UInt64BitsToDouble(0x3FD0000000000000ul | ((ulong)(bits & 0x7FFFFF) << 29));
        int resultExp = (380 - exp) / 2;
        double est = RecipSqrtEstimateCore(scaled);
        ulong estBits = BitConverter.DoubleToUInt64Bits(est);
        uint frac = (uint)((estBits >> 29) & 0x7FFFFF);
        return BitConverter.UInt32BitsToSingle(((uint)(resultExp & 0xFF) << 23) | frac);
    }

    internal static uint URecipEstimate(uint a)
    {
        if ((a & 0x80000000) == 0) return 0xFFFFFFFF;
        double dp = BitConverter.UInt64BitsToDouble(0x3FE0000000000000ul | ((ulong)(a & 0x7FFFFFFF) << 21));
        return (uint)(RecipEstimateCore(dp) * 2147483648.0);
    }

    internal static uint URSqrtEstimate(uint a)
    {
        if ((a & 0xC0000000) == 0) return 0xFFFFFFFF;
        double dp = (a & 0x80000000) != 0
            ? BitConverter.UInt64BitsToDouble(0x3FE0000000000000ul | ((ulong)(a & 0x7FFFFFFF) << 21))
            : BitConverter.UInt64BitsToDouble(0x3FD0000000000000ul | ((ulong)(a & 0x3FFFFFFF) << 22));
        return (uint)(RecipSqrtEstimateCore(dp) * 2147483648.0);
    }

    // ---------------------------------------------------------- shifts by register (VSHL/VQSHL/VRSHL/VQRSHL)

    /// <summary>Element value widened to Int128 so 64-bit unsigned lanes keep their magnitude.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Int128 GetI(byte* v, int i, int esize, bool unsigned) => unsigned ? (Int128)GetU(v, i, esize) : (Int128)GetS(v, i, esize);

    internal static ulong ShiftElem(CpuState* s, Int128 v, long shiftReg, bool unsignedValue, int esize, bool round, bool saturate, bool unsignedSat)
    {
        int shift = (sbyte)(byte)shiftReg;
        Int128 r;
        if (shift >= 0)
        {
            if (shift >= 64) r = v == 0 ? 0 : (v > 0 ? (Int128.One << 100) : -(Int128.One << 100));
            else r = v << shift;
        }
        else
        {
            int sh = Math.Min(-shift, 100);
            Int128 roundConst = round ? (Int128.One << (sh - 1)) : 0;
            r = (v + roundConst) >> sh;
        }
        if (saturate) return unsignedSat ? SatU(s, r, esize) : (ulong)SatS(s, r, esize);
        return (ulong)(long)r & Mask(esize);
    }
}
