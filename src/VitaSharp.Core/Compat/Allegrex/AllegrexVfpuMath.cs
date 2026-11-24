using System.Numerics;

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

    /// <summary>
    /// The VFPU dot product of four lanes (the hardware sums exactly, then rounds once): each 24x24-bit product is kept
    /// with two extra bits (round to odd), aligned to the largest exponent by truncation and summed as an integer; the
    /// sum loses the extra bits by truncation and rounds to 24 bits, nearest even. Denormal inputs count as zero, a
    /// result below FLT_MIN is zero, a zero sum is +0; NaN operands, inf * 0 and inf - inf give 0x7F800001
    /// (described in PPSSPP issue 21070 and its vfpu_dot reference, pspautotests cpu/vfpu/vdot; our own code).
    /// </summary>
    public static uint Dot(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
    {
        const int ExtraBits = 2;
        const uint ImplicitBit = 1u << 23;
        Span<int> exponents = stackalloc int[4];
        Span<uint> products = stackalloc uint[4];
        Span<bool> negative = stackalloc bool[4];
        int infinitySign = 0;
        int highest = -2 * 127;
        for (int i = 0; i < 4; i++)
        {
            uint x = left[i];
            uint y = right[i];
            int exponentX = (int)((x >> 23) & 0xFF);
            int exponentY = (int)((y >> 23) & 0xFF);
            uint mantissaX = x & (ImplicitBit - 1);
            uint mantissaY = y & (ImplicitBit - 1);
            if (exponentX == 255 || exponentY == 255)
            {
                bool nanOperand = (exponentX == 255 && mantissaX != 0) || (exponentY == 255 && mantissaY != 0);
                bool infinityTimesZero = (exponentX == 255 && exponentY == 0) || (exponentY == 255 && exponentX == 0);
                int sign = ((x ^ y) >> 31) != 0 ? -1 : 1;
                if (nanOperand || infinityTimesZero || (infinitySign != 0 && infinitySign != sign))
                {
                    return PositiveNaN;
                }

                infinitySign = sign;
            }

            negative[i] = ((x ^ y) >> 31) != 0;
            if (exponentX == 0 || exponentY == 0)
            {
                exponents[i] = -2 * 127;
                products[i] = 0;
            }
            else
            {
                exponents[i] = exponentX + exponentY - (2 * 127);
                ulong product = (ulong)(ImplicitBit + mantissaX) * (ImplicitBit + mantissaY);
                uint kept = (uint)(product >> (23 - ExtraBits));
                if ((product & ((1ul << (23 - ExtraBits)) - 1)) != 0)
                {
                    kept |= 1;
                }

                products[i] = kept;
            }

            highest = Math.Max(highest, exponents[i]);
        }

        if (infinitySign != 0)
        {
            return infinitySign < 0 ? 0xFF800000u : 0x7F800000u;
        }

        int sum = 0;
        for (int i = 0; i < 4; i++)
        {
            int shift = Math.Min(highest - exponents[i], 28);
            int term = (int)(products[i] >> shift);
            sum += negative[i] ? -term : term;
        }

        uint magnitude = (uint)Math.Abs(sum) >> ExtraBits;
        int exponent = highest;
        if (magnitude != 0)
        {
            int normalize = 8 - BitOperations.LeadingZeroCount(magnitude);
            exponent += normalize;
            if (normalize > 0)
            {
                uint half = 1u << (normalize - 1);
                uint kept = magnitude >> normalize;
                uint dropped = magnitude & ((2 * half) - 1);
                magnitude = kept + ((dropped + (kept & 1)) > half ? 1u : 0u);
                if (magnitude >= 2 * ImplicitBit)
                {
                    magnitude >>= 1;
                    exponent++;
                }
            }
            else if (normalize < 0)
            {
                magnitude <<= -normalize;
            }
        }
        else
        {
            exponent = -128;
        }

        if (exponent <= -127)
        {
            exponent = -127;
            magnitude = 0;
        }

        if (exponent >= 128)
        {
            exponent = 128;
            magnitude = 0;
        }

        uint signBit = sum < 0 ? 0x80000000u : 0u;
        return signBit | ((uint)(exponent + 127) << 23) | (magnitude & 0x007FFFFF);
    }

    /// <summary>
    /// The unary group by the VFPU4 operation number: 16 vrcp, 17 vrsq, 18 vsin, 19 vcos, 20 vexp2, 21 vlog2, 22 vsqrt,
    /// 23 vasin, 24 vnrcp, 26 vnsin, 28 vrexp2. Angles are in quarter turns (vsin(1) = 1). Computed from the definition in
    /// double and rounded once (the hardware's interpolator tables come in a second step); NaN signs follow the hardware:
    /// the input's for vrcp, vrsq, vsin and vasin, positive for the others.
    /// </summary>
    public static uint Transcendental(int operation, uint bits)
    {
        uint input = FlushDenormal(bits);
        double value = Single(input);
        uint inputSign = input & 0x80000000;
        double result;
        uint nanSign = 0;
        switch (operation)
        {
            case 16:
            case 24:
                result = 1.0 / value;
                nanSign = inputSign;
                break;
            case 17:
                result = value < 0 ? double.NaN : 1.0 / Math.Sqrt(value);
                nanSign = inputSign;
                break;
            case 18:
            case 26:
                result = SineQuarterTurns(value);
                nanSign = inputSign;
                break;
            case 19:
                result = CosineQuarterTurns(value);
                break;
            case 20:
                result = Math.Pow(2, value);
                break;
            case 21:
                result = value < 0 ? double.NaN : Math.Log2(value);
                break;
            case 22:
                result = value < 0 ? double.NaN : Math.Sqrt(value);
                break;
            case 23:
                result = Math.Abs(value) > 1 ? double.NaN : Math.Asin(value) * 2 / Math.PI;
                nanSign = inputSign;
                break;
            default:
                result = Math.Pow(2, -value);
                break;
        }

        uint finished = Finish((float)result, result, nanSign);
        if (operation == 24 || operation == 26)
        {
            finished ^= 0x80000000;
        }

        return finished;
    }

    /// <summary>sin(x * pi / 2), reduced in quarter turns so that whole turns give exact 0 and 1.</summary>
    private static double SineQuarterTurns(double x)
    {
        if (!double.IsFinite(x))
        {
            return double.NaN;
        }

        if (x == 0)
        {
            return x;
        }

        double reduced = x - (4 * Math.Floor(x / 4));
        int quadrant = (int)Math.Floor(reduced);
        double fraction = (reduced - quadrant) * Math.PI / 2;
        return quadrant switch
        {
            0 => Math.Sin(fraction),
            1 => Math.Cos(fraction),
            2 => -Math.Sin(fraction),
            _ => -Math.Cos(fraction),
        };
    }

    private static double CosineQuarterTurns(double x)
    {
        if (!double.IsFinite(x))
        {
            return double.NaN;
        }

        double reduced = x - (4 * Math.Floor(x / 4));
        int quadrant = (int)Math.Floor(reduced);
        double fraction = (reduced - quadrant) * Math.PI / 2;
        return quadrant switch
        {
            0 => Math.Cos(fraction),
            1 => -Math.Sin(fraction),
            2 => -Math.Cos(fraction),
            _ => Math.Sin(fraction),
        };
    }

    /// <summary>vrnds: each of the eight state words keeps 1.0's top bits, a half of the seed and one of its nibbles.</summary>
    public static void SeedRandom(uint seed, Span<uint> state)
    {
        for (int i = 0; i < 8; i++)
        {
            state[i] = 0x3F800000u | ((seed >> ((i / 4) * 16)) & 0xFFFF) | (((seed >> (4 * i)) & 0xF) << 16);
        }
    }

    /// <summary>
    /// The VFPU generator (PPSSPP issue 16946's model of the hardware): a 69069 LCG, a 13/17/5 xorshift and a Pell-like
    /// pair with a carry, spread over the eight state words; the value is their sum.
    /// </summary>
    public static uint NextRandom(Span<uint> state)
    {
        uint lcg = (state[0] & 0xFFFF) | (state[4] << 16);
        uint xorshift = (state[1] & 0xFFFF) | (state[5] << 16);
        uint previous = (state[2] & 0xFFFF) | (state[6] << 16);
        uint current = (state[3] & 0xFFFF) | (state[7] << 16);
        uint carry = 0;
        for (int i = 0; i < 8; i++)
        {
            carry |= ((state[i] >> 16) & 0xF) << (4 * i);
        }

        lcg = (69069u * lcg) + 1u;
        xorshift ^= xorshift << 13;
        xorshift ^= xorshift >> 17;
        xorshift ^= xorshift << 5;
        uint next = (2u * current) + previous + carry;
        carry = (uint)(((ulong)previous + (current >> 1) + carry) >> 32);
        previous = current;
        current = next;
        Span<uint> parts = stackalloc uint[4];
        parts[0] = lcg;
        parts[1] = xorshift;
        parts[2] = previous;
        parts[3] = current;
        for (int i = 0; i < 4; i++)
        {
            state[i] = 0x3F800000u | (((carry >> (4 * i)) & 0xF) << 16) | (parts[i] & 0xFFFF);
            state[i + 4] = 0x3F800000u | (((carry >> (4 * (i + 4))) & 0xF) << 16) | (parts[i] >> 16);
        }

        return lcg + xorshift + current;
    }
}
