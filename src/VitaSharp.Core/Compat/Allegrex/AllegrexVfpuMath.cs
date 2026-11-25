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
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static float Input(uint bits)
    {
        return Single(FlushDenormal(bits));
    }

    /// <summary>
    /// Finishes a lane result: a NaN becomes the VFPU NaN with <paramref name="nanSign"/>, a result below the flush
    /// threshold (judged on the exact value) the zero of its sign.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static uint Add(uint left, uint right)
    {
        float a = Input(left);
        float b = Input(right);
        return Finish(a + b, (double)a + b, 0);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static uint Subtract(uint left, uint right)
    {
        float a = Input(left);
        float b = Input(right);
        return Finish(a - b, (double)a - b, 0);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
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
        return Dot4(left[0], left[1], left[2], left[3], right[0], right[1], right[2], right[3]);
    }

    private const int DotExtraBits = 2;
    private const uint DotImplicitBit = 1u << 23;

    /// <summary><see cref="Dot"/> of lanes given one by one (no spans: the IL tier calls it with its registers in
    /// locals).</summary>
    public static uint Dot4(uint x0, uint x1, uint x2, uint x3, uint y0, uint y1, uint y2, uint y3)
    {
        // no infinity or NaN among the operands (the common case): every lane is a plain product
        if (!HasMaximumExponent(x0) && !HasMaximumExponent(x1) && !HasMaximumExponent(x2) && !HasMaximumExponent(x3)
            && !HasMaximumExponent(y0) && !HasMaximumExponent(y1) && !HasMaximumExponent(y2) && !HasMaximumExponent(y3))
        {
            FiniteLane(x0, y0, out int f0, out uint q0);
            FiniteLane(x1, y1, out int f1, out uint q1);
            FiniteLane(x2, y2, out int f2, out uint q2);
            FiniteLane(x3, y3, out int f3, out uint q3);
            int top = Math.Max(Math.Max(f0, f1), Math.Max(f2, f3));
            int total = DotTerm(q0, top - f0, ((x0 ^ y0) >> 31) != 0) + DotTerm(q1, top - f1, ((x1 ^ y1) >> 31) != 0)
                + DotTerm(q2, top - f2, ((x2 ^ y2) >> 31) != 0) + DotTerm(q3, top - f3, ((x3 ^ y3) >> 31) != 0);
            return DotRound(total, top);
        }

        return DotWithInfinities(x0, x1, x2, x3, y0, y1, y2, y3);
    }

    /// <summary><see cref="Dot4"/> when an operand is an infinity or a NaN (kept out of the common path's code).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static uint DotWithInfinities(uint x0, uint x1, uint x2, uint x3, uint y0, uint y1, uint y2, uint y3)
    {
        int infinitySign = 0;
        if (!DotLane(x0, y0, ref infinitySign, out int e0, out uint p0, out bool n0)
            || !DotLane(x1, y1, ref infinitySign, out int e1, out uint p1, out bool n1)
            || !DotLane(x2, y2, ref infinitySign, out int e2, out uint p2, out bool n2)
            || !DotLane(x3, y3, ref infinitySign, out int e3, out uint p3, out bool n3))
        {
            return PositiveNaN;
        }

        if (infinitySign != 0)
        {
            return infinitySign < 0 ? 0xFF800000u : 0x7F800000u;
        }

        int highest = Math.Max(Math.Max(Math.Max(-2 * 127, e0), Math.Max(e1, e2)), e3);
        int sum = DotTerm(p0, highest - e0, n0) + DotTerm(p1, highest - e1, n1) + DotTerm(p2, highest - e2, n2) + DotTerm(p3, highest - e3, n3);
        return DotRound(sum, highest);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool HasMaximumExponent(uint bits)
    {
        return (bits & 0x7F800000) == 0x7F800000;
    }

    /// <summary>A lane of finite operands: as <see cref="DotLane"/>, without branches on the common path.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void FiniteLane(uint x, uint y, out int exponent, out uint product)
    {
        int exponentX = (int)((x >> 23) & 0xFF);
        int exponentY = (int)((y >> 23) & 0xFF);
        ulong full = (ulong)(DotImplicitBit | (x & (DotImplicitBit - 1))) * (DotImplicitBit | (y & (DotImplicitBit - 1)));
        uint kept = (uint)(full >> (23 - DotExtraBits)) | ((full & ((1ul << (23 - DotExtraBits)) - 1)) != 0 ? 1u : 0u);
        bool zero = exponentX == 0 || exponentY == 0;
        exponent = zero ? -2 * 127 : exponentX + exponentY - (2 * 127);
        product = zero ? 0 : kept;
    }

    /// <summary>The integer sum of the aligned products to float bits: the extra bits truncated, nearest even to 24
    /// bits, below FLT_MIN zero, a zero sum +0.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint DotRound(int sum, int highest)
    {
        // |sum| without the overflow check (four 26-bit terms cannot reach int.MinValue), the extra bits truncated
        int signMask = sum >> 31;
        uint magnitude = (uint)((sum ^ signMask) - signMask) >> DotExtraBits;
        uint signBit = (uint)signMask & 0x80000000u;
        if (magnitude == 0)
        {
            return signBit;
        }

        // the host's int-to-float conversion rounds to 24 bits, nearest even, as the hardware does: the value is
        // magnitude * 2^(highest - 23), so only the exponent moves
        uint rounded = BitConverter.SingleToUInt32Bits((int)magnitude);
        int exponent = (int)(rounded >> 23) - 127 + highest - 23;
        if (exponent <= -127)
        {
            return signBit;
        }

        if (exponent >= 128)
        {
            return signBit | 0x7F800000u;
        }

        return signBit | ((uint)(exponent + 127) << 23) | (rounded & 0x007FFFFF);
    }

    /// <summary>One lane of the dot product: false when it makes the result NaN (a NaN operand, inf * 0, or an
    /// infinity of the other sign than an earlier one); else its exponent, its product with two extra bits (round to
    /// odd) and its sign.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool DotLane(uint x, uint y, ref int infinitySign, out int exponent, out uint product, out bool negative)
    {
        int exponentX = (int)((x >> 23) & 0xFF);
        int exponentY = (int)((y >> 23) & 0xFF);
        uint mantissaX = x & (DotImplicitBit - 1);
        uint mantissaY = y & (DotImplicitBit - 1);
        negative = ((x ^ y) >> 31) != 0;
        if (exponentX == 255 || exponentY == 255)
        {
            bool nanOperand = (exponentX == 255 && mantissaX != 0) || (exponentY == 255 && mantissaY != 0);
            bool infinityTimesZero = (exponentX == 255 && exponentY == 0) || (exponentY == 255 && exponentX == 0);
            int sign = negative ? -1 : 1;
            if (nanOperand || infinityTimesZero || (infinitySign != 0 && infinitySign != sign))
            {
                exponent = 0;
                product = 0;
                return false;
            }

            infinitySign = sign;
        }

        if (exponentX == 0 || exponentY == 0)
        {
            exponent = -2 * 127;
            product = 0;
            return true;
        }

        exponent = exponentX + exponentY - (2 * 127);
        ulong full = (ulong)(DotImplicitBit + mantissaX) * (DotImplicitBit + mantissaY);
        product = (uint)(full >> (23 - DotExtraBits));
        if ((full & ((1ul << (23 - DotExtraBits)) - 1)) != 0)
        {
            product |= 1;
        }

        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int DotTerm(uint product, int distance, bool negative)
    {
        int term = (int)(product >> Math.Min(distance, 28));
        return negative ? -term : term;
    }

    /// <summary>
    /// The unary group by the VFPU4 operation number: 16 vrcp, 17 vrsq, 18 vsin, 19 vcos, 20 vexp2, 21 vlog2, 22 vsqrt,
    /// 23 vasin, 24 vnrcp, 26 vnsin, 28 vrexp2. The hardware evaluates all of them with one quadratic interpolator over
    /// 128 segments (the coefficients in <see cref="AllegrexVfpuTables"/>): the top seven bits of a 23-bit index choose
    /// the segment, the low 16 bits (x2) enter the linear term in full and the squared term as the distance of their top
    /// ten bits from the segment's middle, squared and rounded up to a multiple of 256; results keep 22 bits. Angles are
    /// in quarter turns. Matches the outputs recorded on a PSP (pspautotests cpu/vfpu/exact) bit for bit.
    /// </summary>
    public static uint Transcendental(int operation, uint bits)
    {
        switch (operation)
        {
            case 16:
                return Reciprocal(bits);
            case 24:
                return Reciprocal(bits) ^ 0x80000000;
            case 17:
                return ReciprocalSquareRoot(bits);
            case 18:
                return Sine(bits);
            case 26:
                return Sine(bits) ^ 0x80000000;
            case 19:
                return Cosine(bits);
            case 20:
                return Exp2(bits);
            case 28:
                return Exp2(bits ^ 0x80000000);
            case 21:
                return Log2(bits);
            case 22:
                return SquareRoot(bits);
            default:
                return ArcSine(bits);
        }
    }

    /// <summary>The squared term's input: |top ten bits of x2 - 512|, squared, divided by 256 rounding up.</summary>
    private static int SquaredDistance(uint x2)
    {
        int distance = Math.Abs((int)(x2 >> 6) - 512);
        return ((distance * distance) + 255) >> 8;
    }

    /// <summary>c0 + m * x2 / 2^17 + n * square / 2^9 of a segment, in ulps of its binade (before truncation).</summary>
    private static int Interpolate(int[] table, int segment, uint x2)
    {
        int index = segment * AllegrexVfpuTables.SegmentStride;
        int start = table[index];
        int slope = table[index + 1];
        int curvature = table[index + 2];
        return start + (int)(((long)slope * x2) >> 17) + ((curvature * SquaredDistance(x2)) >> 9);
    }

    private static int Binade(int[] table, int segment)
    {
        return table[(segment * AllegrexVfpuTables.SegmentStride) + 3];
    }

    /// <summary>
    /// A float-bits result from <paramref name="index"/> (7 segment bits, 16 position bits) with the segment's binade
    /// as the exponent, truncated to 22 bits; <paramref name="exponentAdjust"/> is added to the exponent field.
    /// </summary>
    private static uint InterpolatedBits(int[] table, uint index, uint exponentAdjust)
    {
        int segment = (int)((index >> 16) & 0x7F);
        uint exponent = (uint)(Binade(table, segment) - 1) << 23;
        return (exponent + exponentAdjust + (uint)Interpolate(table, segment, index & 0xFFFF)) & ~3u;
    }

    /// <summary>vrcp: |x| in [2^-126, 2^126] interpolates; zero and denormals give inf, NaN NaN, larger values 0, all signed like x.</summary>
    private static uint Reciprocal(uint bits)
    {
        uint magnitude = bits & 0x7FFFFFFF;
        uint sign = bits & 0x80000000;
        if ((bits << 1) - 0x01000000u <= 0xFC000000u)
        {
            // the table's exponent is for 1/x with x in [1, 2): subtract x's exponent (and its sign bit, which flips back)
            return InterpolatedBits(AllegrexVfpuTables.Reciprocal, bits, (127u << 23) - (bits & 0xFF800000));
        }

        if (magnitude < 0x00800000)
        {
            return sign | 0x7F800000;
        }

        return magnitude > 0x7F800000 ? sign | PositiveNaN : sign;
    }

    private static bool PositiveNormalFinite(uint bits)
    {
        return bits - 0x00800000u < 0x7F000000u;
    }

    /// <summary>vrsq: positive normal x interpolates on (bits + 2^23) / 2; zero/denormal give a signed inf, negatives -NaN, +inf 0.</summary>
    private static uint ReciprocalSquareRoot(uint bits)
    {
        if (PositiveNormalFinite(bits))
        {
            uint halved = (bits + 0x00800000u) >> 1;
            return InterpolatedBits(AllegrexVfpuTables.ReciprocalSquareRoot, halved, (64u << 23) - (halved & 0x7F800000));
        }

        if ((bits & 0x7FFFFFFF) < 0x00800000)
        {
            return (bits & 0x80000000) | 0x7F800000;
        }

        if ((bits >> 31) != 0)
        {
            return NegativeNaN;
        }

        return bits > 0x7F800000 ? PositiveNaN : 0u;
    }

    /// <summary>vsqrt: like vrsq's index; zero and denormals give +0, negatives and NaN +NaN, inf inf.</summary>
    private static uint SquareRoot(uint bits)
    {
        if (PositiveNormalFinite(bits))
        {
            uint halved = (bits + 0x00800000u) >> 1;
            return InterpolatedBits(AllegrexVfpuTables.SquareRoot, halved, (halved & 0x7F800000) - (64u << 23));
        }

        if ((bits & 0x7FFFFFFF) < 0x00800000)
        {
            return 0;
        }

        return bits == 0x7F800000 ? 0x7F800000u : PositiveNaN;
    }

    /// <summary>
    /// The angle in quarter turns as a 24-bit fraction of a half turn (bit 24 tells an odd half turn, where the sine
    /// changes sign). Exponents from 25 to 31 above the bias, and those with the bits of 0x9F set, lose every bit, as on
    /// the hardware. False for inf and NaN.
    /// </summary>
    private static bool ReduceAngle(uint bits, out uint angle, out bool oddHalfTurn)
    {
        uint exponent = (bits >> 23) & 0xFF;
        uint significand = (bits & 0x007FFFFF) | 0x00800000;
        angle = 0;
        oddHalfTurn = false;
        if (exponent == 0xFF)
        {
            return false;
        }

        if (exponent < 0x7F)
        {
            significand = exponent < 0x7F - 23 ? 0 : significand >> (int)(0x7F - exponent);
        }
        else if (exponent > 0x7F)
        {
            uint shift = exponent - 0x7F;
            if ((shift >= 25 && shift < 32) || (exponent & 0x9F) == 0x9F)
            {
                significand = 0;
            }
            else
            {
                significand <<= (int)(shift & 31);
            }
        }

        oddHalfTurn = ((significand >> 24) & 1) != 0;
        angle = significand & 0x00FFFFFF;
        return true;
    }

    /// <summary>sin of a quarter-wave angle in [0, 2^23] as fixed point with 28 fraction bits (the table runs from the top down).</summary>
    private static uint SineFixed(uint angle)
    {
        if (angle == 0)
        {
            return 0;
        }

        if (angle == 0x00800000)
        {
            return 0x10000000;
        }

        uint fromTop = 0x00800000 - angle;
        int segment = (int)(fromTop >> 16);
        uint value = (uint)Interpolate(AllegrexVfpuTables.Sine, segment, fromTop & 0xFFFF) & ~3u;
        int binade = Binade(AllegrexVfpuTables.Sine, segment);
        return binade >= 122 ? value << (binade - 122) : value >> (122 - binade);
    }

    /// <summary>Float bits of value * 2^-fractionBits with the given sign (also for zero); value fits a float significand.</summary>
    private static uint FixedToBits(ulong value, int fractionBits, bool negative)
    {
        uint sign = negative ? 0x80000000u : 0u;
        if (value == 0)
        {
            return sign;
        }

        int top = 63 - BitOperations.LeadingZeroCount(value);
        ulong significand = top <= 23 ? value << (23 - top) : value >> (top - 23);
        return sign + ((uint)(top - fractionBits + 127) << 23) + ((uint)significand & 0x007FFFFF);
    }

    private static uint SineOfHalfTurnAngle(uint angle, bool negative)
    {
        if (angle > 0x00800000)
        {
            angle = 0x01000000 - angle;
        }

        return FixedToBits(SineFixed(angle), 28, negative);
    }

    private static uint Sine(uint bits)
    {
        if (!ReduceAngle(bits, out uint angle, out bool odd))
        {
            return (bits & 0x80000000) ^ PositiveNaN;
        }

        return SineOfHalfTurnAngle(angle, ((bits >> 31) != 0) != odd);
    }

    private static uint Cosine(uint bits)
    {
        if (!ReduceAngle(bits, out uint angle, out bool odd))
        {
            return PositiveNaN;
        }

        bool negative = odd;
        if (angle >= 0x00800000)
        {
            angle = 0x01000000 - angle;
            negative = !negative;
        }

        return FixedToBits(SineFixed(0x00800000 - angle), 28, negative);
    }

    /// <summary>vasin: |x| above 1 is NaN with x's sign; below 2^-23 (denormals too) 0; the result in quarter turns.</summary>
    private static uint ArcSine(uint bits)
    {
        uint sign = bits & 0x80000000;
        uint magnitude = bits & 0x7FFFFFFF;
        if (magnitude > 0x3F800000)
        {
            return PositiveNaN ^ sign;
        }

        int exponent = (int)(magnitude >> 23);
        uint significand = (magnitude & 0x007FFFFF) | 0x00800000;
        uint fixedInput = exponent < 104 ? 0 : significand >> (127 - exponent);
        uint fixedOutput;
        if (fixedInput == 0)
        {
            fixedOutput = 0;
        }
        else if (fixedInput == 1u << 23)
        {
            fixedOutput = 1u << 30;
        }
        else
        {
            int segment = (int)(fixedInput >> 16);
            // the first segment is linear and keeps the 2^-30 step of the output
            uint mask = segment == 0 ? ~1u : ~3u;
            uint value = (uint)Interpolate(AllegrexVfpuTables.ArcSine, segment, fixedInput & 0xFFFF) & mask;
            int binade = Binade(AllegrexVfpuTables.ArcSine, segment);
            fixedOutput = binade >= 120 ? value << (binade - 120) : value >> (120 - binade);
        }

        return FixedToBits(fixedOutput, 30, sign != 0);
    }

    /// <summary>
    /// vexp2 (vrexp2 is vexp2 of -x): x * 2^23 truncated towards zero, one less when negative; its integer part is the
    /// exponent, the fraction indexes the table. Denormals count as 0 (result 1), -126 and below give +0, 128 and up inf.
    /// </summary>
    private static uint Exp2(uint bits)
    {
        uint magnitude = bits & 0x7FFFFFFF;
        bool negative = (bits >> 31) != 0;
        if (magnitude <= 0x007FFFFF)
        {
            return 0x3F800000;
        }

        if (magnitude > 0x7F800000)
        {
            return PositiveNaN;
        }

        if (negative && magnitude >= 0x42FC0000)
        {
            return 0;
        }

        if (!negative && magnitude >= 0x43000000)
        {
            return 0x7F800000;
        }

        int exponent = (int)(magnitude >> 23);
        uint significand = (magnitude & 0x007FFFFF) | 0x00800000;
        int fixedValue;
        if (exponent >= 127)
        {
            fixedValue = (int)(significand << (exponent - 127));
        }
        else
        {
            fixedValue = exponent < 104 ? 0 : (int)(significand >> (127 - exponent));
        }

        if (negative)
        {
            fixedValue = -fixedValue - 1;
        }

        uint fraction = (uint)fixedValue & 0x007FFFFF;
        uint fractionBits = fraction == 0 ? 0 : InterpolatedBits(AllegrexVfpuTables.Exp2, fraction, 0) - 0x3F800000;
        return 0x3F800000 + ((uint)fixedValue & 0xFF800000) + fractionBits;
    }

    /// <summary>
    /// vlog2: exponent + log2(1.mantissa), truncated towards zero to 22 significant bits. Where the step is coarser than
    /// 2^-22 (exponents from 2 up, and every negative exponent) the datapath drops coefficient bits too: the slope to the
    /// step, the curvature to 2^d, and the start value takes the dropped part of the squared term. Zero and denormals
    /// give -inf, negatives NaN, +inf inf.
    /// </summary>
    private static uint Log2(uint bits)
    {
        if ((bits & 0x7FFFFFFF) <= 0x007FFFFF)
        {
            return 0xFF800000;
        }

        if ((bits & 0x80000000) != 0)
        {
            return PositiveNaN;
        }

        if ((bits >> 23) == 255)
        {
            return 0x7F800000u + ((bits & 0x007FFFFF) != 0 ? 1u : 0u);
        }

        int exponent = (int)(bits >> 23) - 127;
        uint index = bits & 0x007FFFFF;
        int level;
        if (exponent < 0)
        {
            level = 7;
        }
        else if (exponent < 2)
        {
            level = 0;
        }
        else
        {
            level = 31 - BitOperations.LeadingZeroCount((uint)exponent);
        }

        int granule = 1 << level;
        long step = (long)(4 << level) << 17;
        int segment = (int)(index >> 16);
        int tableIndex = segment * AllegrexVfpuTables.SegmentStride;
        int start = AllegrexVfpuTables.Log2[tableIndex];
        int slope = AllegrexVfpuTables.Log2[tableIndex + 1];
        int curvature = AllegrexVfpuTables.Log2[tableIndex + 2];
        uint x2 = index & 0xFFFF;
        int keptCurvature = curvature < 0 ? -(-curvature & ~(granule - 1)) : curvature & ~(granule - 1);
        int keptStart = (start + (2 * (curvature - keptCurvature))) & ~(granule - 1);
        int square = ((keptCurvature * SquaredDistance(x2)) >> 9) & ~(granule - 1);
        long keptSlope = slope & ~((4 << level) - 1);

        // in units of 2^-41
        long value = ((long)(keptStart + square) << 17) + (keptSlope * x2);
        long fraction = exponent >= 0 ? value & ~(step - 1) : -(-value & ~(step - 1));
        long sum = ((long)exponent << 41) + fraction;
        return FixedToBits((ulong)(sum < 0 ? -sum : sum), 41, exponent < 0);
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
