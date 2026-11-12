using System.Runtime.CompilerServices;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

/// <summary>VFPv3 handlers. Thumb encodings are bit-identical to ARM with cond = 1110, so both share these.</summary>
internal static unsafe class Vfp
{
    private const uint FpscrDN = 1u << 25, FpscrFZ = 1u << 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static ref float Sf(CpuState* state, uint n) => ref ((float*)state->D)[n];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static ref uint Su(CpuState* state, uint n) => ref ((uint*)state->D)[n];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static ref double Df(CpuState* state, uint n) => ref ((double*)state->D)[n];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static ref ulong Du(CpuState* state, uint n) => ref state->D[n];

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static uint SingleD(uint ins) => (Bits(ins, 15, 12) << 1) | Bit(ins, 22);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static uint SingleN(uint ins) => (Bits(ins, 19, 16) << 1) | Bit(ins, 7);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static uint SingleM(uint ins) => ((ins & 0xF) << 1) | Bit(ins, 5);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static uint DoubleD(uint ins) => (Bit(ins, 22) << 4) | Bits(ins, 15, 12);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static uint DoubleN(uint ins) => (Bit(ins, 7) << 4) | Bits(ins, 19, 16);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static uint DoubleM(uint ins) => (Bit(ins, 5) << 4) | (ins & 0xF);

    // ------------------------------------------------------------ FPSCR-controlled result fixups

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float FixF(CpuState* state, float r)
    {
        uint fpscr = state->Fpscr;
        if ((fpscr & (FpscrDN | FpscrFZ)) == 0)
        {
            return r;
        }

        uint bits = BitConverter.SingleToUInt32Bits(r);
        if ((fpscr & FpscrDN) != 0 && float.IsNaN(r))
        {
            return BitConverter.UInt32BitsToSingle(0x7FC00000);
        }

        if ((fpscr & FpscrFZ) != 0 && (bits & 0x7F800000) == 0 && (bits & 0x7FFFFF) != 0)
        {
            return BitConverter.UInt32BitsToSingle(bits & 0x80000000);
        }

        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float InF(CpuState* state, float v)
    {
        if ((state->Fpscr & FpscrFZ) == 0)
        {
            return v;
        }

        uint bits = BitConverter.SingleToUInt32Bits(v);
        if ((bits & 0x7F800000) == 0 && (bits & 0x7FFFFF) != 0)
        {
            return BitConverter.UInt32BitsToSingle(bits & 0x80000000);
        }

        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double FixD(CpuState* state, double r)
    {
        uint fpscr = state->Fpscr;
        if ((fpscr & (FpscrDN | FpscrFZ)) == 0)
        {
            return r;
        }

        ulong bits = BitConverter.DoubleToUInt64Bits(r);
        if ((fpscr & FpscrDN) != 0 && double.IsNaN(r))
        {
            return BitConverter.UInt64BitsToDouble(0x7FF8000000000000);
        }

        if ((fpscr & FpscrFZ) != 0 && (bits & 0x7FF0000000000000) == 0 && (bits & 0xFFFFFFFFFFFFF) != 0)
        {
            return BitConverter.UInt64BitsToDouble(bits & 0x8000000000000000);
        }

        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double InD(CpuState* state, double v)
    {
        if ((state->Fpscr & FpscrFZ) == 0)
        {
            return v;
        }

        ulong bits = BitConverter.DoubleToUInt64Bits(v);
        if ((bits & 0x7FF0000000000000) == 0 && (bits & 0xFFFFFFFFFFFFF) != 0)
        {
            return BitConverter.UInt64BitsToDouble(bits & 0x8000000000000000);
        }

        return v;
    }

    // ------------------------------------------------------------ data processing

    /// <summary>VMLA/VMLS/VNMLA/VNMLS/VMUL/VNMUL/VADD/VSUB/VDIV/VFMA/VFMS/VFNMA/VFNMS</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Arith(CpuState* state, uint ins, uint pc)
    {
        uint opc1 = (Bit(ins, 23) << 2) | Bits(ins, 21, 20);
        bool op = Bit(ins, 6) != 0;
        if (Bit(ins, 8) == 0)
        {
            uint d = SingleD(ins), n = SingleN(ins), m = SingleM(ins);
            float a = InF(state, Sf(state, n)), b = InF(state, Sf(state, m)), acc = InF(state, Sf(state, d));
            float r = opc1 switch
            {
                0 => op ? acc - FixF(state, a * b) : acc + FixF(state, a * b),
                1 => op ? -acc - FixF(state, a * b) : -acc + FixF(state, a * b),
                2 => a * b, // VNMUL negates below
                3 => op ? a - b : a + b,
                4 => a / b,
                5 => op ? MathF.FusedMultiplyAdd(-a, b, -acc) : MathF.FusedMultiplyAdd(a, b, -acc),
                6 => op ? MathF.FusedMultiplyAdd(-a, b, acc) : MathF.FusedMultiplyAdd(a, b, acc),
                _ => 0,
            };
            // ARM flushes a result that is tiny BEFORE rounding: one that rounds up to the smallest normal is zero
            if ((state->Fpscr & FpscrFZ) != 0 && (BitConverter.SingleToUInt32Bits(r) & 0x7FFFFFFF) == 0x00800000 && opc1 >= 2)
            {
                // x + y with x, y exact in double: TwoSum gives the rounded sum and its exact error, so the
                // comparison with 2^-126 is exact (a float product is exact in double)
                double x, y;
                switch (opc1)
                {
                    case 2:
                        x = (double)a * b;
                        y = 0;
                        break;
                    case 3:
                        x = a;
                        y = op ? -(double)b : b;
                        break;
                    case 4:
                        x = (double)a / b;
                        y = 0;
                        break;
                    case 5:
                        x = (op ? -(double)a : a) * b;
                        y = -(double)acc;
                        break;
                    default:
                        x = (op ? -(double)a : a) * b;
                        y = acc;
                        break;
                }
                double sum = x + y, bv = sum - x, err = (x - (sum - bv)) + (y - bv);
                const double MinNormal = 1.1754943508222875e-38;
                bool tiny = Math.Abs(sum) < MinNormal || (Math.Abs(sum) == MinNormal && err != 0 && Math.Sign(err) != Math.Sign(sum));
                if (tiny)
                {
                    r = BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(r) & 0x80000000);
                }
            }
            // VNMUL negates the rounded product, after any default-NaN substitution (so its NaN is 0xFFC00000)
            Sf(state, d) = opc1 == 2 && op ? -FixF(state, r) : FixF(state, r);
        }
        else
        {
            uint d = DoubleD(ins), n = DoubleN(ins), m = DoubleM(ins);
            double a = InD(state, Df(state, n)), b = InD(state, Df(state, m)), acc = InD(state, Df(state, d));
            double r = opc1 switch
            {
                0 => op ? acc - FixD(state, a * b) : acc + FixD(state, a * b),
                1 => op ? -acc - FixD(state, a * b) : -acc + FixD(state, a * b),
                2 => op ? -(a * b) : a * b,
                3 => op ? a - b : a + b,
                4 => a / b,
                5 => op ? Math.FusedMultiplyAdd(-a, b, -acc) : Math.FusedMultiplyAdd(a, b, -acc),
                6 => op ? Math.FusedMultiplyAdd(-a, b, acc) : Math.FusedMultiplyAdd(a, b, acc),
                _ => 0,
            };
            Df(state, d) = opc1 == 2 && op ? -FixD(state, a * b) : FixD(state, r);
        }
    }

    /// <summary>VMOV imm: cond 11101D11 imm4H Vd 101 sz 0000 imm4L</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovImm(CpuState* state, uint ins, uint pc)
    {
        uint imm8 = (Bits(ins, 19, 16) << 4) | (ins & 0xF);
        if (Bit(ins, 8) == 0)
        {
            uint v = ((imm8 & 0x80) << 24) | ((imm8 & 0x40) != 0 ? 0x3E000000u : 0x40000000u) | ((imm8 & 0x3F) << 19);
            Su(state, SingleD(ins)) = v;
        }
        else
        {
            ulong v = ((ulong)(imm8 & 0x80) << 56) | ((imm8 & 0x40) != 0 ? 0x3FC0000000000000ul : 0x4000000000000000ul) | ((ulong)(imm8 & 0x3F) << 48);
            Du(state, DoubleD(ins)) = v;
        }
    }

    /// <summary>Two-register ops with opc1 = 1x11, bit 6 = 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Other(CpuState* state, uint ins, uint pc)
    {
        uint opc2 = Bits(ins, 19, 16);
        bool dbl = Bit(ins, 8) != 0;
        bool b7 = Bit(ins, 7) != 0;
        switch (opc2)
        {
            case 0: // VMOV / VABS
            case 1: // VNEG / VSQRT
                if (!dbl)
                {
                    uint d = SingleD(ins), m = SingleM(ins);
                    if (opc2 == 0 && !b7)
                    {
                        Su(state, d) = Su(state, m);
                        return;
                    }
                    if (opc2 == 0)
                    {
                        Su(state, d) = Su(state, m) & 0x7FFFFFFF;
                        return;
                    }
                    if (!b7)
                    {
                        Su(state, d) = Su(state, m) ^ 0x80000000;
                        return;
                    }
                    Sf(state, d) = FixF(state, MathF.Sqrt(InF(state, Sf(state, m))));
                }
                else
                {
                    uint d = DoubleD(ins), m = DoubleM(ins);
                    if (opc2 == 0 && !b7)
                    {
                        Du(state, d) = Du(state, m);
                        return;
                    }
                    if (opc2 == 0)
                    {
                        Du(state, d) = Du(state, m) & 0x7FFFFFFFFFFFFFFF;
                        return;
                    }
                    if (!b7)
                    {
                        Du(state, d) = Du(state, m) ^ 0x8000000000000000;
                        return;
                    }
                    Df(state, d) = FixD(state, Math.Sqrt(InD(state, Df(state, m))));
                }
                return;
            case 2:
            case 3: // VCVTB / VCVTT (half <-> single)
            {
                uint d = SingleD(ins), m = SingleM(ins);
                int shift = b7 ? 16 : 0;
                if (opc2 == 3) // single -> half
                {
                    float f = InF(state, Sf(state, m));
                    ushort h = float.IsNaN(f) && (state->Fpscr & FpscrDN) != 0 ? (ushort)0x7E00 : BitConverter.HalfToUInt16Bits((Half)f);
                    Su(state, d) = (Su(state, d) & ~(0xFFFFu << shift)) | ((uint)h << shift);
                }
                else
                {
                    Half h = BitConverter.UInt16BitsToHalf((ushort)(Su(state, m) >> shift));
                    Sf(state, d) = FixF(state, (float)h);
                }
                return;
            }
            case 4:
            case 5: // VCMP / VCMPE (5: compare with zero)
                if (!dbl)
                {
                    float a = InF(state, Sf(state, SingleD(ins))), b = opc2 == 5 ? 0f : InF(state, Sf(state, SingleM(ins))); // FZ applies to compares too
                    SetCompareFlags(state, float.IsNaN(a) || float.IsNaN(b), a == b, a < b);
                }
                else
                {
                    double a = InD(state, Df(state, DoubleD(ins))), b = opc2 == 5 ? 0 : InD(state, Df(state, DoubleM(ins)));
                    SetCompareFlags(state, double.IsNaN(a) || double.IsNaN(b), a == b, a < b);
                }
                return;
            case 7: // VCVT double <-> single
                if (!dbl)
                {
                    Df(state, DoubleD(ins)) = FixD(state, InF(state, Sf(state, SingleM(ins))));
                }
                else
                {
                    Sf(state, SingleD(ins)) = FixF(state, (float)InD(state, Df(state, DoubleM(ins))));
                }

                return;
            case 8: // VCVT int -> float  (Vm is always a single register)
            {
                uint raw = Su(state, SingleM(ins));
                double v = b7 ? (int)raw : raw;
                if (!dbl)
                {
                    Sf(state, SingleD(ins)) = (float)v;
                }
                else
                {
                    Df(state, DoubleD(ins)) = v;
                }

                return;
            }
            case 0xA:
            case 0xB:
            case 0xE:
            case 0xF: // VCVT fixed-point (operates in place on Vd)
                FixedConvert(state, ins, dbl);
                return;
            case 0xC:
            case 0xD: // VCVT/VCVTR float -> int
            {
                bool signed = opc2 == 0xD;
                double v = dbl ? InD(state, Df(state, DoubleM(ins))) : InF(state, Sf(state, SingleM(ins)));
                int rmode = b7 ? 3 : (int)((state->Fpscr >> 22) & 3);
                Su(state, SingleD(ins)) = signed ? (uint)ToS32(Round(v, rmode)) : ToU32(Round(v, rmode));
                return;
            }
        }
        Udf(state, pc);
    }

    private static void Udf(CpuState* state, uint pc)
    {
        state->Exit = ExitReason.Undefined;
        state->SvcPc = pc;
    }

    // ------------------------------------------------------------ short vectors (FPSCR LEN / STRIDE)

    /// <summary>FPSCR LEN (bits 18:16) and STRIDE (bits 21:20): non-zero selects VFP short-vector operation.</summary>
    public const uint FpscrVectorMask = 0x00370000;

    /// <summary>
    /// The VFP data-processing instructions that short-vector mode turns into vector operations (ARMv7 ARM, "VFP vector
    /// operation support"): VMLA/VMLS/VNMLA/VNMLS/VMUL/VNMUL/VADD/VSUB/VDIV, VMOV (register and immediate), VABS,
    /// VNEG and VSQRT. Compares and conversions stay scalar.
    /// </summary>
    public static bool IsVectorCapable(Op op, uint ins) => op switch
    {
        Op.Vfp_Arith or Op.Vfp_MovImm => true,
        Op.Vfp_Other => Bits(ins, 19, 16) <= 1,
        _ => false,
    };

    /// <summary>
    /// A vector-capable instruction run with FPSCR LEN/STRIDE set (as QEMU's ARM model does it). Registers are in banks
    /// of eight singles or four doubles; a destination in bank 0 makes the instruction scalar, a Vm in bank 0 is a scalar applied to every
    /// element; the other operands step by STRIDE (1, or 2 for STRIDE = 11) and wrap inside their bank. Each element
    /// is the scalar operation on re-encoded registers, in order (overlapping vectors read the updated values).
    /// The Vita's Cortex-A9 has no short-vector hardware: it raises an undefined-instruction exception that homebrew
    /// (VFPVector through kubridge, e.g. Android ports of games built for VFPv2 vectors) emulates the same way.
    /// </summary>
    public static void VectorOp(CpuState* state, Op op, uint ins, uint pc)
    {
        uint fpscr = state->Fpscr;
        uint len = ((fpscr >> 16) & 7) + 1, stride = ((fpscr >> 20) & 3) == 3 ? 2u : 1u;
        bool dbl = Bit(ins, 8) != 0;
        uint d = dbl ? DoubleD(ins) : SingleD(ins), n = dbl ? DoubleN(ins) : SingleN(ins), m = dbl ? DoubleM(ins) : SingleM(ins);
        // bank 0 is S0-S7, or D0-D3 (and D16-D19, the bank 0 of the upper half: the bank field is bits 3:2)
        uint bankMask = dbl ? 3u : 7u, bankField = dbl ? 0xCu : 0x18u;
        if ((d & bankField) == 0)
        {
            len = 1;
        }

        uint mStride = (m & bankField) == 0 ? 0 : stride;
        // the vector bits are only read here: the scalar handlers ignore them
        for (uint i = 0; i < len; i++)
        {
            uint e = Encode(ins, d, n, m, dbl, op);
            switch (op)
            {
                case Op.Vfp_Arith:
                    Arith(state, e, pc);
                    break;
                case Op.Vfp_MovImm:
                    MovImm(state, e, pc);
                    break;
                default:
                    Other(state, e, pc);
                    break;
            }
            d = (d & ~bankMask) | ((d + stride) & bankMask);
            n = (n & ~bankMask) | ((n + stride) & bankMask);
            m = (m & ~bankMask) | ((m + mStride) & bankMask);
        }
    }

    /// <summary>The instruction with other registers: Vd always, Vn for the arithmetic ops, Vm unless it is VMOV
    /// immediate (whose immediate sits in the Vn/Vm fields; VMOV/VABS/VNEG/VSQRT have opc2 in the Vn field).</summary>
    private static uint Encode(uint ins, uint d, uint n, uint m, bool dbl, Op op)
    {
        ins &= ~((1u << 22) | (0xFu << 12));
        ins |= dbl ? ((d >> 4) << 22) | ((d & 0xF) << 12) : ((d & 1) << 22) | ((d >> 1) << 12);
        if (op == Op.Vfp_MovImm)
        {
            return ins;
        }

        ins &= ~((1u << 5) | 0xFu);
        ins |= dbl ? ((m >> 4) << 5) | (m & 0xF) : ((m & 1) << 5) | (m >> 1);
        if (op != Op.Vfp_Arith)
        {
            return ins;
        }

        ins &= ~((0xFu << 16) | (1u << 7));
        return ins | (dbl ? ((n & 0xF) << 16) | ((n >> 4) << 7) : ((n >> 1) << 16) | ((n & 1) << 7));
    }

    /// <summary>
    /// Operands of VCVT between floating-point and fixed-point: op=bit18 (to fixed), U=bit16 (unsigned), sx=bit7 (32-bit
    /// fixed-point size, else 16), imm=imm4:i; the fraction bits are size - imm (negative for a 16-bit size with imm
    /// &gt; 16: UNPREDICTABLE). Shared by the interpreter and the native backends.
    /// </summary>
    internal static void FixedParams(uint ins, out bool toFixed, out bool unsigned, out int size, out int frac)
    {
        toFixed = Bit(ins, 18) != 0;
        unsigned = Bit(ins, 16) != 0;
        size = Bit(ins, 7) != 0 ? 32 : 16;
        frac = size - (int)(((ins & 0xF) << 1) | Bit(ins, 5));
    }

    /// <summary>VCVT between floating-point and fixed-point (see <see cref="FixedParams"/>).</summary>
    private static void FixedConvert(CpuState* state, uint ins, bool dbl)
    {
        FixedParams(ins, out bool toFixed, out bool unsigned, out int size, out int frac);
        double scale = Math.Pow(2, frac);
        if (toFixed)
        {
            // round towards zero and saturate to the fixed-point size, then extend to the register (ARM ARM: the
            // result is sign- or zero-extended to 32 bits, or to 64 for a double register)
            double v = Math.Truncate((dbl ? InD(state, Df(state, DoubleD(ins))) : InF(state, Sf(state, SingleD(ins)))) * scale);
            long r;
            if (double.IsNaN(v))
            {
                r = 0;
            }
            else if (unsigned)
            {
                r = (long)Math.Clamp(v, 0, size == 32 ? uint.MaxValue : ushort.MaxValue);
            }
            else
            {
                r = (long)Math.Clamp(v, size == 32 ? int.MinValue : short.MinValue, size == 32 ? int.MaxValue : short.MaxValue);
            }

            if (dbl)
            {
                Du(state, DoubleD(ins)) = (ulong)r;
            }
            else
            {
                Su(state, SingleD(ins)) = (uint)r;
            }
        }
        else if (dbl)
        {
            uint d = DoubleD(ins);
            ulong raw = Du(state, d);
            double v = size == 32 ? (unsigned ? (uint)raw : (int)(uint)raw) : (unsigned ? (ushort)raw : (short)(ushort)raw);
            Df(state, d) = v / scale;
        }
        else
        {
            uint d = SingleD(ins);
            uint raw = Su(state, d);
            double v = size == 32 ? (unsigned ? raw : (int)raw) : (unsigned ? (ushort)raw : (short)(ushort)raw);
            Sf(state, d) = (float)(v / scale);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double Round(double v, int rmode) => rmode switch
    {
        0 => Math.Round(v, MidpointRounding.ToEven),
        1 => Math.Ceiling(v),
        2 => Math.Floor(v),
        _ => Math.Truncate(v),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ToS32(double v)
    {
        if (double.IsNaN(v))
        {
            return 0;
        }

        if (v >= 2147483647.0)
        {
            return int.MaxValue;
        }

        if (v <= -2147483648.0)
        {
            return int.MinValue;
        }

        return (int)v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ToU32(double v)
    {
        if (double.IsNaN(v) || v <= 0)
        {
            return 0;
        }

        if (v >= 4294967295.0)
        {
            return uint.MaxValue;
        }

        return (uint)v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetCompareFlags(CpuState* state, bool unordered, bool equal, bool less)
    {
        uint nzcv = unordered ? 0x3u : equal ? 0x6u : less ? 0x8u : 0x2u;
        state->Fpscr = (state->Fpscr & 0x0FFFFFFF) | (nzcv << 28);
    }

    // ------------------------------------------------------------ register transfers

    /// <summary>VMOV between core register and single: cond 1110 000 op Vn Rt 1010 N 001 0000</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovCoreSingle(CpuState* state, uint ins, uint pc)
    {
        uint n = SingleN(ins), rt = Bits(ins, 15, 12);
        if (Bit(ins, 20) != 0)
        {
            state->R[rt] = Su(state, n);
        }
        else
        {
            Su(state, n) = state->R[rt];
        }
    }

    /// <summary>VMRS / VMSR: cond 1110 111 L 0001 Rt 1010 0001 0000 (only FPSCR reg 0001 is modelled)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vmrs(CpuState* state, uint ins, uint pc)
    {
        uint rt = Bits(ins, 15, 12), reg = Bits(ins, 19, 16);
        uint v = reg switch
        {
            1 => state->Fpscr,
            0 => 0x41033094, // FPSID: Cortex-A9 VFPv3
            6 => 0x10110222, // MVFR1
            7 => 0x10110222, // MVFR0
            8 => 0x40000000, // FPEXC.EN
            _ => 0,
        };
        if (rt == 15)
        {
            state->N = v >> 31;
            state->Z = (v >> 30) & 1;
            state->C = (v >> 29) & 1;
            state->V = (v >> 28) & 1;
        }
        else
        {
            state->R[rt] = v;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vmsr(CpuState* state, uint ins, uint pc)
    {
        if (Bits(ins, 19, 16) != 1)
        {
            return;
        }

        uint old = state->Fpscr, v = state->R[Bits(ins, 15, 12)];
        state->Fpscr = v;
        // entering or leaving short-vector mode: the dispatcher switches between generated code and the interpreter
        if (((old ^ v) & FpscrVectorMask) != 0 && state->Exit == ExitReason.None)
        {
            state->Exit = ExitReason.FpMode;
        }
    }

    /// <summary>VMOV core -> scalar: cond 1110 0 opc1 0 Vd Rt 1011 D opc2 1 0000</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovCoreScalar(CpuState* state, uint ins, uint pc)
    {
        uint d = DoubleN(ins) /* D:Vd in the N/Vn positions */, rt = Bits(ins, 15, 12);
        uint opc = (Bits(ins, 22, 21) << 2) | Bits(ins, 6, 5);
        byte* p = (byte*)&state->D[d];
        uint v = state->R[rt];
        if ((opc & 8) != 0)
        {
            p[opc & 7] = (byte)v;
        }
        else if ((opc & 1) != 0)
        {
            ((ushort*)p)[(opc >> 1) & 3] = (ushort)v;
        }
        else
        {
            ((uint*)p)[(opc >> 2) & 1] = v;
        }
    }

    /// <summary>VMOV scalar -> core: cond 1110 U opc1 1 Vn Rt 1011 N opc2 1 0000</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovScalarCore(CpuState* state, uint ins, uint pc)
    {
        uint n = DoubleN(ins), rt = Bits(ins, 15, 12);
        bool u = Bit(ins, 23) != 0;
        uint opc = (Bits(ins, 22, 21) << 2) | Bits(ins, 6, 5);
        byte* p = (byte*)&state->D[n];
        uint v;
        if ((opc & 8) != 0)
        {
            byte b = p[opc & 7];
            v = u ? b : (uint)(sbyte)b;
        }
        else if ((opc & 1) != 0)
        {
            ushort h = ((ushort*)p)[(opc >> 1) & 3];
            v = u ? h : (uint)(short)h;
        }
        else
        {
            v = ((uint*)p)[(opc >> 2) & 1];
        }

        state->R[rt] = v;
    }

    /// <summary>VDUP (core): cond 1110 1 B Q 0 Vd Rt 1011 D 0 E 1 0000</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void VdupCore(CpuState* state, uint ins, uint pc)
    {
        uint d = DoubleN(ins), rt = Bits(ins, 15, 12);
        bool q = Bit(ins, 21) != 0;
        uint be = (Bit(ins, 22) << 1) | Bit(ins, 5);
        uint v = state->R[rt];
        ulong rep = be switch
        {
            0 => v | ((ulong)v << 32),
            1 => (v & 0xFFFF) * 0x0001000100010001ul,
            _ => (v & 0xFF) * 0x0101010101010101ul,
        };
        state->D[d] = rep;
        if (q)
        {
            state->D[d + 1] = rep;
        }
    }

    /// <summary>VMOV two core registers &lt;-&gt; two singles / one double: cond 1100 010 op Rt2 Rt 101 sz 00 M 1 Vm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mov64(CpuState* state, uint ins, uint pc)
    {
        bool toCore = Bit(ins, 20) != 0;
        uint rt2 = Bits(ins, 19, 16), rt = Bits(ins, 15, 12);
        if (Bit(ins, 8) == 0)
        {
            uint m = SingleM(ins);
            if (toCore)
            {
                state->R[rt] = Su(state, m);
                state->R[rt2] = Su(state, m + 1);
            }
            else
            {
                Su(state, m) = state->R[rt];
                Su(state, m + 1) = state->R[rt2];
            }
        }
        else
        {
            uint m = DoubleM(ins);
            if (toCore)
            {
                ulong v = Du(state, m);
                state->R[rt] = (uint)v;
                state->R[rt2] = (uint)(v >> 32);
            }
            else
            {
                Du(state, m) = state->R[rt] | ((ulong)state->R[rt2] << 32);
            }
        }
    }

    // ------------------------------------------------------------ loads / stores

    /// <summary>VLDR/VSTR: cond 1101 U D 0 L Rn Vd 101 sz imm8</summary>
    public static void LdrStr(CpuState* state, uint ins, uint pc, bool thumb)
    {
        bool u = Bit(ins, 23) != 0, l = Bit(ins, 20) != 0, dbl = Bit(ins, 8) != 0;
        uint rn = Bits(ins, 19, 16);
        uint imm = (ins & 0xFF) << 2;
        uint baseVal = rn == 15 ? ((pc + (thumb ? 4u : 8u)) & ~3u) : state->R[rn];
        uint addr = u ? baseVal + imm : baseVal - imm;
        if (dbl)
        {
            uint d = DoubleD(ins);
            if (l)
            {
                Du(state, d) = Mem.R32(state, addr) | ((ulong)Mem.R32(state, addr + 4) << 32);
            }
            else
            {
                ulong v = Du(state, d);
                Mem.W32(state, addr, (uint)v);
                Mem.W32(state, addr + 4, (uint)(v >> 32));
            }
        }
        else
        {
            uint d = SingleD(ins);
            if (l)
            {
                Su(state, d) = Mem.R32(state, addr);
            }
            else
            {
                Mem.W32(state, addr, Su(state, d));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrStrArm(CpuState* state, uint ins, uint pc) => LdrStr(state, ins, pc, false);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrStrThumb(CpuState* state, uint ins, uint pc) => LdrStr(state, ins, pc, true);

    /// <summary>VLDM/VSTM/VPUSH/VPOP: cond 110 P U D W L Rn Vd 101 sz imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdmStm(CpuState* state, uint ins, uint pc)
    {
        bool p = Bit(ins, 24) != 0, u = Bit(ins, 23) != 0, w = Bit(ins, 21) != 0, l = Bit(ins, 20) != 0, dbl = Bit(ins, 8) != 0;
        uint rn = Bits(ins, 19, 16);
        uint imm8 = ins & 0xFF;
        uint words = imm8;
        uint baseVal = state->R[rn];
        uint addr = p ? baseVal - words * 4 : baseVal;
        if (w)
        {
            state->R[rn] = u ? baseVal + words * 4 : baseVal - words * 4;
        }

        if (dbl)
        {
            uint d = DoubleD(ins);
            uint regs = imm8 >> 1;
            for (uint i = 0; i < regs; i++, addr += 8)
            {
                if (l)
                {
                    Du(state, (d + i) & 31) = Mem.R32(state, addr) | ((ulong)Mem.R32(state, addr + 4) << 32);
                }
                else
                {
                    ulong v = Du(state, (d + i) & 31);
                    Mem.W32(state, addr, (uint)v);
                    Mem.W32(state, addr + 4, (uint)(v >> 32));
                }
            }
        }
        else
        {
            uint d = SingleD(ins);
            for (uint i = 0; i < imm8; i++, addr += 4)
            {
                if (l)
                {
                    Su(state, (d + i) & 63) = Mem.R32(state, addr);
                }
                else
                {
                    Mem.W32(state, addr, Su(state, (d + i) & 63));
                }
            }
        }
    }
}
