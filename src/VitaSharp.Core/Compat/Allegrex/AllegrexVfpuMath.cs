namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The VFPU's arithmetic on float bits. Unlike the FPU, the VFPU ignores FCR31: it always rounds to nearest and always
/// treats denormal inputs and results as signed zeros; a NaN it makes never keeps a payload: it is 0x7F800001 with the
/// sign rule of each operation (PPSSPP docs/cpu-equivalence.md, pspautotests cpu/vfpu/fcr31, nan_gen, signed_zero).
/// </summary>
public static class AllegrexVfpuMath
{
    public const uint PositiveNaN = 0x7F800001;
    public const uint NegativeNaN = 0xFF800001;

    // results below FLT_MIN - FLT_TRUE_MIN / 4 flush (a product that would round up to FLT_MIN does not)
    private const double FlushThreshold = 1.1754943157898259E-38;

    public static float Single(uint bits)
    {
        return BitConverter.UInt32BitsToSingle(bits);
    }

    public static uint Bits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    public static bool IsNaN(uint bits)
    {
        return (bits & 0x7FFFFFFF) > 0x7F800000;
    }

    public static bool IsInfinity(uint bits)
    {
        return (bits & 0x7FFFFFFF) == 0x7F800000;
    }

    /// <summary>A denormal becomes the zero of its sign.</summary>
    public static uint FlushDenormal(uint bits)
    {
        return (bits & 0x7F800000) == 0 ? bits & 0x80000000 : bits;
    }

    /// <summary>The value an input is used as: denormals as zeros.</summary>
    public static float Input(uint bits)
    {
        return Single(FlushDenormal(bits));
    }

    /// <summary>
    /// Finishes a lane result: a NaN becomes the VFPU NaN with <paramref name="nanSign"/>, a result below the flush
    /// threshold (judged on the exact value) the zero of its sign.
    /// </summary>
    public static uint Finish(float result, double exact, uint nanSign)
    {
        uint bits = Bits(result);
        if (IsNaN(bits))
        {
            return PositiveNaN | (nanSign & 0x80000000);
        }

        if ((bits & 0x7FFFFFFF) <= 0x00800000 && Math.Abs(exact) < FlushThreshold)
        {
            return bits & 0x80000000;
        }

        return bits;
    }

    public static uint Add(uint left, uint right)
    {
        float a = Input(left);
        float b = Input(right);
        return Finish(a + b, (double)a + b, 0);
    }

    public static uint Subtract(uint left, uint right)
    {
        float a = Input(left);
        float b = Input(right);
        return Finish(a - b, (double)a - b, 0);
    }

    public static uint Multiply(uint left, uint right)
    {
        float a = Input(left);
        float b = Input(right);
        return Finish(a * b, (double)a * b, left ^ right);
    }

    public static uint Divide(uint left, uint right)
    {
        float a = Input(left);
        float b = Input(right);
        return Finish(a / b, (double)a / b, left ^ right);
    }

    /// <summary>vh2f: zero and subnormal halves give a signed zero; inf/NaN keep their mantissa bits unshifted.</summary>
    public static uint HalfToSingle(ushort half)
    {
        uint sign = (uint)(half & 0x8000) << 16;
        uint exponent = (uint)(half >> 10) & 0x1F;
        uint mantissa = (uint)half & 0x3FF;
        if (exponent == 0)
        {
            return sign;
        }

        if (exponent == 31)
        {
            return sign | 0x7F800000 | mantissa;
        }

        return sign | ((exponent + 112) << 23) | (mantissa << 13);
    }

    /// <summary>vf2h: the mantissa is truncated, below 2^-14 is a signed zero, 65536 and up inf, a NaN keeps its low ten mantissa bits.</summary>
    public static ushort SingleToHalf(uint bits)
    {
        uint sign = (bits >> 16) & 0x8000;
        uint exponent = (bits >> 23) & 0xFF;
        uint mantissa = bits & 0x7FFFFF;
        if (exponent == 255)
        {
            return (ushort)(sign | 0x7C00u | (mantissa & 0x3FF));
        }

        if (exponent < 113)
        {
            return (ushort)sign;
        }

        if (exponent >= 143)
        {
            return (ushort)(sign | 0x7C00u);
        }

        return (ushort)(sign | ((exponent - 112) << 10) | (mantissa >> 13));
    }
}
