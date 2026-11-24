using System.Numerics;

namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// The GE's 24-bit float arithmetic where it changes what is drawn: operands keep 15 mantissa bits (the float's top
/// 24 bits), a matrix row is summed in one go with every term truncated to the weight of the largest, the adder has
/// no guard bits, and the reciprocal is a measured table (<see cref="GeTables"/>). Used for the clip coordinates (the
/// combined world, view and projection matrix) and the viewport. Rules from PPSSPP's hardware probes
/// (GPU/Software/GEMath.h, gpu/probe exp32-42, gpu/depth/transformprecision), our own code.
/// </summary>
public static class GeFloat24
{
    /// <summary>The float with its low 8 mantissa bits cleared.</summary>
    public static float Truncate(float value)
    {
        return BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(value) & 0xFFFFFF00);
    }

    /// <summary>A product of two float24s (up to 32 significant bits) truncated to a float24 in double precision.</summary>
    public static float Product(double value)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        if (((bits >> 52) & 0x7FF) != 0x7FF)
        {
            bits &= ~((1UL << (52 - 15)) - 1);
        }

        return (float)BitConverter.UInt64BitsToDouble(bits);
    }

    /// <summary>The GE's reciprocal of a normal float24 w: 1 / 1.i from the segment table, times 2^-e.</summary>
    public static float Reciprocal(float w)
    {
        uint bits = BitConverter.SingleToUInt32Bits(w);
        uint mantissa = (bits >> 8) & 0x7FFF;
        int exponent = (int)((bits >> 23) & 0xFF) - 127;
        int segment = (int)(mantissa >> 8) * 2;
        int start = GeTables.ReciprocalSegments[segment];
        int slope = GeTables.ReciprocalSegments[segment + 1];
        int q = ((64 * start) + 63 + (slope * (int)(mantissa & 255))) >> 7;
        float magnitude = MathF.ScaleB(q, -16 - exponent);
        return (bits & 0x80000000) != 0 ? -magnitude : magnitude;
    }

    /// <summary>a + b on the GE's adder: each operand loses the bits below the larger one's float24 precision.</summary>
    public static float Add(float a, float b)
    {
        uint bitsA = BitConverter.SingleToUInt32Bits(a);
        uint bitsB = BitConverter.SingleToUInt32Bits(b);
        int exponentA = (int)((bitsA >> 23) & 0xFF);
        int exponentB = (int)((bitsB >> 23) & 0xFF);
        if (exponentA == 0 || exponentB == 0)
        {
            // zero (or a denormal, which the GE takes as zero) leaves the other term alone
            if (exponentA == 0)
            {
                return exponentB == 0 ? 0f : Truncate(b);
            }

            return Truncate(a);
        }

        int highest = Math.Max(exponentA, exponentB);
        int dropA = 8 + highest - exponentA;
        int dropB = 8 + highest - exponentB;
        bitsA = dropA >= 24 ? bitsA & 0x80000000 : bitsA & ~((1u << dropA) - 1);
        bitsB = dropB >= 24 ? bitsB & 0x80000000 : bitsB & ~((1u << dropB) - 1);
        return BitConverter.UInt32BitsToSingle(bitsA) + BitConverter.UInt32BitsToSingle(bitsB);
    }

    /// <summary>One term of a matrix row: the 17 top bits of the significand product and the weight of their lowest bit.</summary>
    private static (int Mantissa, int LowestBit) Term(float a, float b)
    {
        uint bitsA = BitConverter.SingleToUInt32Bits(a);
        uint bitsB = BitConverter.SingleToUInt32Bits(b);
        int exponentA = (int)((bitsA >> 23) & 0xFF);
        int exponentB = (int)((bitsB >> 23) & 0xFF);
        if (exponentA == 0 || exponentB == 0)
        {
            return (0, int.MinValue);
        }

        ulong mantissaA = (bitsA & 0x007FFFFF) | 0x00800000;
        ulong mantissaB = (bitsB & 0x007FFFFF) | 0x00800000;
        int product = (int)((mantissaA * mantissaB) >> 31);
        return (((bitsA ^ bitsB) & 0x80000000) != 0 ? -product : product, exponentA - 127 + exponentB - 127 - 15);
    }

    /// <summary>Four products summed like a GE matrix row: each truncated to the largest term's lowest bit, added exactly.</summary>
    private static float RowSum(float a0, float b0, float a1, float b1, float a2, float b2, float a3, float b3)
    {
        Span<int> mantissas = stackalloc int[4];
        Span<int> lowest = stackalloc int[4];
        (mantissas[0], lowest[0]) = Term(a0, b0);
        (mantissas[1], lowest[1]) = Term(a1, b1);
        (mantissas[2], lowest[2]) = Term(a2, b2);
        (mantissas[3], lowest[3]) = Term(a3, b3);
        int weight = int.MinValue;
        for (int i = 0; i < 4; i++)
        {
            weight = Math.Max(weight, lowest[i]);
        }

        int sum = 0;
        for (int i = 0; i < 4; i++)
        {
            int mantissa = mantissas[i];
            if (mantissa == 0)
            {
                continue;
            }

            int shift = weight - lowest[i];
            if (shift < 32)
            {
                sum += mantissa < 0 ? -(-mantissa >> shift) : mantissa >> shift;
            }
        }

        return sum == 0 ? 0f : Truncate(MathF.ScaleB(sum, weight));
    }

    /// <summary>Component <paramref name="component"/> of the clip position: (x, y, z, 1) times the combined matrix.</summary>
    public static float ClipComponent(Vector3 position, ReadOnlySpan<float> combined, int component)
    {
        return RowSum(
            Truncate(position.X), combined[component],
            Truncate(position.Y), combined[4 + component],
            Truncate(position.Z), combined[8 + component],
            1f, combined[12 + component]);
    }

    /// <summary>A 4x3 matrix (column after column, as the GE loads it) as a 4x4 one, row r giving the input's component r.</summary>
    private static float Entry43(ReadOnlySpan<float> matrix, int row, int column)
    {
        if (column == 3)
        {
            return row == 3 ? 1f : 0f;
        }

        return matrix[(row * 3) + column];
    }

    /// <summary>(world * view) * projection, every entry summed like a matrix row, as the GE combines them.</summary>
    public static void CombineMatrices(ReadOnlySpan<float> world, ReadOnlySpan<float> view, ReadOnlySpan<float> projection, Span<float> result)
    {
        Span<float> worldView = stackalloc float[16];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                worldView[(row * 4) + column] = RowSum(
                    Entry43(world, row, 0), Entry43(view, 0, column),
                    Entry43(world, row, 1), Entry43(view, 1, column),
                    Entry43(world, row, 2), Entry43(view, 2, column),
                    Entry43(world, row, 3), Entry43(view, 3, column));
            }
        }

        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                result[(row * 4) + column] = RowSum(
                    worldView[row * 4], projection[column],
                    worldView[(row * 4) + 1], projection[4 + column],
                    worldView[(row * 4) + 2], projection[8 + column],
                    worldView[(row * 4) + 3], projection[12 + column]);
            }
        }
    }

    /// <summary>
    /// A screen coordinate: the clip component times the GE's reciprocal of w, truncated to a float24, then scaled and
    /// moved by the center on the GE's adder (around a center of 2048 that lands on the 1/16 subpixel grid). A w that
    /// is not a normal finite float24 falls back to the plain division.
    /// </summary>
    public static float Viewport(float clip, float clipW, float scale, float center)
    {
        float w = Truncate(clipW);
        if (!float.IsFinite(w) || !float.IsFinite(clip) || MathF.Abs(w) < 1.17549435E-38f)
        {
            return (clip * scale / clipW) + center;
        }

        float normalized = Product((double)Truncate(clip) * Reciprocal(w));
        return Add(Product((double)normalized * scale), center);
    }
}
