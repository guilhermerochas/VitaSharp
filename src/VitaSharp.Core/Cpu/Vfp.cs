using System.Runtime.CompilerServices;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

/// <summary>VFPv3 handlers. Thumb encodings are bit-identical to ARM with cond = 1110, so both share these.</summary>
internal static unsafe class Vfp
{
    const uint FpscrDN = 1u << 25, FpscrFZ = 1u << 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] static ref float Sf(CpuState* s, uint n) => ref ((float*)s->D)[n];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static ref uint Su(CpuState* s, uint n) => ref ((uint*)s->D)[n];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static ref double Df(CpuState* s, uint n) => ref ((double*)s->D)[n];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static ref ulong Du(CpuState* s, uint n) => ref s->D[n];

    [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint SingleD(uint ins) => (Bits(ins, 15, 12) << 1) | Bit(ins, 22);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint SingleN(uint ins) => (Bits(ins, 19, 16) << 1) | Bit(ins, 7);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint SingleM(uint ins) => ((ins & 0xF) << 1) | Bit(ins, 5);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint DoubleD(uint ins) => (Bit(ins, 22) << 4) | Bits(ins, 15, 12);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint DoubleN(uint ins) => (Bit(ins, 7) << 4) | Bits(ins, 19, 16);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] static uint DoubleM(uint ins) => (Bit(ins, 5) << 4) | (ins & 0xF);

    // ------------------------------------------------------------ FPSCR-controlled result fixups

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float FixF(CpuState* s, float r)
    {
        uint fpscr = s->Fpscr;
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
    internal static float InF(CpuState* s, float v)
    {
        if ((s->Fpscr & FpscrFZ) == 0)
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
    internal static double FixD(CpuState* s, double r)
    {
        uint fpscr = s->Fpscr;
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
    internal static double InD(CpuState* s, double v)
    {
        if ((s->Fpscr & FpscrFZ) == 0)
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
    public static void Arith(CpuState* s, uint ins, uint pc)
    {
        uint opc1 = (Bit(ins, 23) << 2) | Bits(ins, 21, 20);
        bool op = Bit(ins, 6) != 0;
        if (Bit(ins, 8) == 0)
        {
            uint d = SingleD(ins), n = SingleN(ins), m = SingleM(ins);
            float a = InF(s, Sf(s, n)), b = InF(s, Sf(s, m)), acc = InF(s, Sf(s, d));
            float r = opc1 switch
            {
                0 => op ? acc - FixF(s, a * b) : acc + FixF(s, a * b),
                1 => op ? -acc - FixF(s, a * b) : -acc + FixF(s, a * b),
                2 => a * b, // VNMUL negates below
                3 => op ? a - b : a + b,
                4 => a / b,
                5 => op ? MathF.FusedMultiplyAdd(-a, b, -acc) : MathF.FusedMultiplyAdd(a, b, -acc),
                6 => op ? MathF.FusedMultiplyAdd(-a, b, acc) : MathF.FusedMultiplyAdd(a, b, acc),
                _ => 0,
            };
            // ARM flushes a result that is tiny BEFORE rounding: one that rounds up to the smallest normal is zero
            if ((s->Fpscr & FpscrFZ) != 0 && (BitConverter.SingleToUInt32Bits(r) & 0x7FFFFFFF) == 0x00800000 && opc1 >= 2)
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
            Sf(s, d) = opc1 == 2 && op ? -FixF(s, r) : FixF(s, r);
        }
        else
        {
            uint d = DoubleD(ins), n = DoubleN(ins), m = DoubleM(ins);
            double a = InD(s, Df(s, n)), b = InD(s, Df(s, m)), acc = InD(s, Df(s, d));
            double r = opc1 switch
            {
                0 => op ? acc - FixD(s, a * b) : acc + FixD(s, a * b),
                1 => op ? -acc - FixD(s, a * b) : -acc + FixD(s, a * b),
                2 => op ? -(a * b) : a * b,
                3 => op ? a - b : a + b,
                4 => a / b,
                5 => op ? Math.FusedMultiplyAdd(-a, b, -acc) : Math.FusedMultiplyAdd(a, b, -acc),
                6 => op ? Math.FusedMultiplyAdd(-a, b, acc) : Math.FusedMultiplyAdd(a, b, acc),
                _ => 0,
            };
            Df(s, d) = opc1 == 2 && op ? -FixD(s, a * b) : FixD(s, r);
        }
    }

    /// <summary>VMOV imm: cond 11101D11 imm4H Vd 101 sz 0000 imm4L</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovImm(CpuState* s, uint ins, uint pc)
    {
        uint imm8 = (Bits(ins, 19, 16) << 4) | (ins & 0xF);
        if (Bit(ins, 8) == 0)
        {
            uint v = ((imm8 & 0x80) << 24) | ((imm8 & 0x40) != 0 ? 0x3E000000u : 0x40000000u) | ((imm8 & 0x3F) << 19);
            Su(s, SingleD(ins)) = v;
        }
        else
        {
            ulong v = ((ulong)(imm8 & 0x80) << 56) | ((imm8 & 0x40) != 0 ? 0x3FC0000000000000ul : 0x4000000000000000ul) | ((ulong)(imm8 & 0x3F) << 48);
            Du(s, DoubleD(ins)) = v;
        }
    }

    /// <summary>Two-register ops with opc1 = 1x11, bit 6 = 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Other(CpuState* s, uint ins, uint pc)
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
                        Su(s, d) = Su(s, m);
                        return;
                    }
                    if (opc2 == 0)
                    {
                        Su(s, d) = Su(s, m) & 0x7FFFFFFF;
                        return;
                    }
                    if (!b7)
                    {
                        Su(s, d) = Su(s, m) ^ 0x80000000;
                        return;
                    }
                    Sf(s, d) = FixF(s, MathF.Sqrt(InF(s, Sf(s, m))));
                }
                else
                {
                    uint d = DoubleD(ins), m = DoubleM(ins);
                    if (opc2 == 0 && !b7)
                    {
                        Du(s, d) = Du(s, m);
                        return;
                    }
                    if (opc2 == 0)
                    {
                        Du(s, d) = Du(s, m) & 0x7FFFFFFFFFFFFFFF;
                        return;
                    }
                    if (!b7)
                    {
                        Du(s, d) = Du(s, m) ^ 0x8000000000000000;
                        return;
                    }
                    Df(s, d) = FixD(s, Math.Sqrt(InD(s, Df(s, m))));
                }
                return;
            case 2:
            case 3: // VCVTB / VCVTT (half <-> single)
            {
                uint d = SingleD(ins), m = SingleM(ins);
                int shift = b7 ? 16 : 0;
                if (opc2 == 3) // single -> half
                {
                    float f = InF(s, Sf(s, m));
                    ushort h = float.IsNaN(f) && (s->Fpscr & FpscrDN) != 0 ? (ushort)0x7E00 : BitConverter.HalfToUInt16Bits((Half)f);
                    Su(s, d) = (Su(s, d) & ~(0xFFFFu << shift)) | ((uint)h << shift);
                }
                else
                {
                    Half h = BitConverter.UInt16BitsToHalf((ushort)(Su(s, m) >> shift));
                    Sf(s, d) = FixF(s, (float)h);
                }
                return;
            }
            case 4:
            case 5: // VCMP / VCMPE (5: compare with zero)
                if (!dbl)
                {
                    float a = InF(s, Sf(s, SingleD(ins))), b = opc2 == 5 ? 0f : InF(s, Sf(s, SingleM(ins))); // FZ applies to compares too
                    SetCompareFlags(s, float.IsNaN(a) || float.IsNaN(b), a == b, a < b);
                }
                else
                {
                    double a = InD(s, Df(s, DoubleD(ins))), b = opc2 == 5 ? 0 : InD(s, Df(s, DoubleM(ins)));
                    SetCompareFlags(s, double.IsNaN(a) || double.IsNaN(b), a == b, a < b);
                }
                return;
            case 7: // VCVT double <-> single
                if (!dbl)
                {
                    Df(s, DoubleD(ins)) = FixD(s, InF(s, Sf(s, SingleM(ins))));
                }
                else
                {
                    Sf(s, SingleD(ins)) = FixF(s, (float)InD(s, Df(s, DoubleM(ins))));
                }

                return;
            case 8: // VCVT int -> float  (Vm is always a single register)
            {
                uint raw = Su(s, SingleM(ins));
                double v = b7 ? (int)raw : raw;
                if (!dbl)
                {
                    Sf(s, SingleD(ins)) = (float)v;
                }
                else
                {
                    Df(s, DoubleD(ins)) = v;
                }

                return;
            }
            case 0xA:
            case 0xB:
            case 0xE:
            case 0xF: // VCVT fixed-point (operates in place on Vd)
                FixedConvert(s, ins, dbl);
                return;
            case 0xC:
            case 0xD: // VCVT/VCVTR float -> int
            {
                bool signed = opc2 == 0xD;
                double v = dbl ? InD(s, Df(s, DoubleM(ins))) : InF(s, Sf(s, SingleM(ins)));
                int rmode = b7 ? 3 : (int)((s->Fpscr >> 22) & 3);
                Su(s, SingleD(ins)) = signed ? (uint)ToS32(Round(v, rmode)) : ToU32(Round(v, rmode));
                return;
            }
        }
        Udf(s, pc);
    }

    static void Udf(CpuState* s, uint pc)
    {
        s->Exit = ExitReason.Undefined;
        s->SvcPc = pc;
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
    public static void VectorOp(CpuState* s, Op op, uint ins, uint pc)
    {
        uint fpscr = s->Fpscr;
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
                    Arith(s, e, pc);
                    break;
                case Op.Vfp_MovImm:
                    MovImm(s, e, pc);
                    break;
                default:
                    Other(s, e, pc);
                    break;
            }
            d = (d & ~bankMask) | ((d + stride) & bankMask);
            n = (n & ~bankMask) | ((n + stride) & bankMask);
            m = (m & ~bankMask) | ((m + mStride) & bankMask);
        }
    }

    /// <summary>The instruction with other registers: Vd always, Vn for the arithmetic ops, Vm unless it is VMOV
    /// immediate (whose immediate sits in the Vn/Vm fields; VMOV/VABS/VNEG/VSQRT have opc2 in the Vn field).</summary>
    static uint Encode(uint ins, uint d, uint n, uint m, bool dbl, Op op)
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
    static void FixedConvert(CpuState* s, uint ins, bool dbl)
    {
        FixedParams(ins, out bool toFixed, out bool unsigned, out int size, out int frac);
        double scale = Math.Pow(2, frac);
        if (toFixed)
        {
            // round towards zero and saturate to the fixed-point size, then extend to the register (ARM ARM: the
            // result is sign- or zero-extended to 32 bits, or to 64 for a double register)
            double v = Math.Truncate((dbl ? InD(s, Df(s, DoubleD(ins))) : InF(s, Sf(s, SingleD(ins)))) * scale);
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
                Du(s, DoubleD(ins)) = (ulong)r;
            }
            else
            {
                Su(s, SingleD(ins)) = (uint)r;
            }
        }
        else if (dbl)
        {
            uint d = DoubleD(ins);
            ulong raw = Du(s, d);
            double v = size == 32 ? (unsigned ? (uint)raw : (int)(uint)raw) : (unsigned ? (ushort)raw : (short)(ushort)raw);
            Df(s, d) = v / scale;
        }
        else
        {
            uint d = SingleD(ins);
            uint raw = Su(s, d);
            double v = size == 32 ? (unsigned ? raw : (int)raw) : (unsigned ? (ushort)raw : (short)(ushort)raw);
            Sf(s, d) = (float)(v / scale);
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
    static void SetCompareFlags(CpuState* s, bool unordered, bool equal, bool less)
    {
        uint nzcv = unordered ? 0x3u : equal ? 0x6u : less ? 0x8u : 0x2u;
        s->Fpscr = (s->Fpscr & 0x0FFFFFFF) | (nzcv << 28);
    }

    // ------------------------------------------------------------ register transfers

    /// <summary>VMOV between core register and single: cond 1110 000 op Vn Rt 1010 N 001 0000</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovCoreSingle(CpuState* s, uint ins, uint pc)
    {
        uint n = SingleN(ins), rt = Bits(ins, 15, 12);
        if (Bit(ins, 20) != 0)
        {
            s->R[rt] = Su(s, n);
        }
        else
        {
            Su(s, n) = s->R[rt];
        }
    }

    /// <summary>VMRS / VMSR: cond 1110 111 L 0001 Rt 1010 0001 0000 (only FPSCR reg 0001 is modelled)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vmrs(CpuState* s, uint ins, uint pc)
    {
        uint rt = Bits(ins, 15, 12), reg = Bits(ins, 19, 16);
        uint v = reg switch
        {
            1 => s->Fpscr,
            0 => 0x41033094, // FPSID: Cortex-A9 VFPv3
            6 => 0x10110222, // MVFR1
            7 => 0x10110222, // MVFR0
            8 => 0x40000000, // FPEXC.EN
            _ => 0,
        };
        if (rt == 15)
        {
            s->N = v >> 31;
            s->Z = (v >> 30) & 1;
            s->C = (v >> 29) & 1;
            s->V = (v >> 28) & 1;
        }
        else
        {
            s->R[rt] = v;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vmsr(CpuState* s, uint ins, uint pc)
    {
        if (Bits(ins, 19, 16) != 1)
        {
            return;
        }

        uint old = s->Fpscr, v = s->R[Bits(ins, 15, 12)];
        s->Fpscr = v;
        // entering or leaving short-vector mode: the dispatcher switches between generated code and the interpreter
        if (((old ^ v) & FpscrVectorMask) != 0 && s->Exit == ExitReason.None)
        {
            s->Exit = ExitReason.FpMode;
        }
    }

    /// <summary>VMOV core -> scalar: cond 1110 0 opc1 0 Vd Rt 1011 D opc2 1 0000</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovCoreScalar(CpuState* s, uint ins, uint pc)
    {
        uint d = DoubleN(ins) /* D:Vd in the N/Vn positions */, rt = Bits(ins, 15, 12);
        uint opc = (Bits(ins, 22, 21) << 2) | Bits(ins, 6, 5);
        byte* p = (byte*)&s->D[d];
        uint v = s->R[rt];
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
    public static void MovScalarCore(CpuState* s, uint ins, uint pc)
    {
        uint n = DoubleN(ins), rt = Bits(ins, 15, 12);
        bool u = Bit(ins, 23) != 0;
        uint opc = (Bits(ins, 22, 21) << 2) | Bits(ins, 6, 5);
        byte* p = (byte*)&s->D[n];
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

        s->R[rt] = v;
    }

    /// <summary>VDUP (core): cond 1110 1 B Q 0 Vd Rt 1011 D 0 E 1 0000</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void VdupCore(CpuState* s, uint ins, uint pc)
    {
        uint d = DoubleN(ins), rt = Bits(ins, 15, 12);
        bool q = Bit(ins, 21) != 0;
        uint be = (Bit(ins, 22) << 1) | Bit(ins, 5);
        uint v = s->R[rt];
        ulong rep = be switch
        {
            0 => v | ((ulong)v << 32),
            1 => (v & 0xFFFF) * 0x0001000100010001ul,
            _ => (v & 0xFF) * 0x0101010101010101ul,
        };
        s->D[d] = rep;
        if (q)
        {
            s->D[d + 1] = rep;
        }
    }

    /// <summary>VMOV two core registers &lt;-&gt; two singles / one double: cond 1100 010 op Rt2 Rt 101 sz 00 M 1 Vm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mov64(CpuState* s, uint ins, uint pc)
    {
        bool toCore = Bit(ins, 20) != 0;
        uint rt2 = Bits(ins, 19, 16), rt = Bits(ins, 15, 12);
        if (Bit(ins, 8) == 0)
        {
            uint m = SingleM(ins);
            if (toCore)
            {
                s->R[rt] = Su(s, m);
                s->R[rt2] = Su(s, m + 1);
            }
            else
            {
                Su(s, m) = s->R[rt];
                Su(s, m + 1) = s->R[rt2];
            }
        }
        else
        {
            uint m = DoubleM(ins);
            if (toCore)
            {
                ulong v = Du(s, m);
                s->R[rt] = (uint)v;
                s->R[rt2] = (uint)(v >> 32);
            }
            else
            {
                Du(s, m) = s->R[rt] | ((ulong)s->R[rt2] << 32);
            }
        }
    }

    // ------------------------------------------------------------ loads / stores

    /// <summary>VLDR/VSTR: cond 1101 U D 0 L Rn Vd 101 sz imm8</summary>
    public static void LdrStr(CpuState* s, uint ins, uint pc, bool thumb)
    {
        bool u = Bit(ins, 23) != 0, l = Bit(ins, 20) != 0, dbl = Bit(ins, 8) != 0;
        uint rn = Bits(ins, 19, 16);
        uint imm = (ins & 0xFF) << 2;
        uint baseVal = rn == 15 ? ((pc + (thumb ? 4u : 8u)) & ~3u) : s->R[rn];
        uint addr = u ? baseVal + imm : baseVal - imm;
        if (dbl)
        {
            uint d = DoubleD(ins);
            if (l)
            {
                Du(s, d) = Mem.R32(s, addr) | ((ulong)Mem.R32(s, addr + 4) << 32);
            }
            else
            {
                ulong v = Du(s, d);
                Mem.W32(s, addr, (uint)v);
                Mem.W32(s, addr + 4, (uint)(v >> 32));
            }
        }
        else
        {
            uint d = SingleD(ins);
            if (l)
            {
                Su(s, d) = Mem.R32(s, addr);
            }
            else
            {
                Mem.W32(s, addr, Su(s, d));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrStrArm(CpuState* s, uint ins, uint pc) => LdrStr(s, ins, pc, false);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrStrThumb(CpuState* s, uint ins, uint pc) => LdrStr(s, ins, pc, true);

    /// <summary>VLDM/VSTM/VPUSH/VPOP: cond 110 P U D W L Rn Vd 101 sz imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdmStm(CpuState* s, uint ins, uint pc)
    {
        bool p = Bit(ins, 24) != 0, u = Bit(ins, 23) != 0, w = Bit(ins, 21) != 0, l = Bit(ins, 20) != 0, dbl = Bit(ins, 8) != 0;
        uint rn = Bits(ins, 19, 16);
        uint imm8 = ins & 0xFF;
        uint words = imm8;
        uint baseVal = s->R[rn];
        uint addr = p ? baseVal - words * 4 : baseVal;
        if (w)
        {
            s->R[rn] = u ? baseVal + words * 4 : baseVal - words * 4;
        }

        if (dbl)
        {
            uint d = DoubleD(ins);
            uint regs = imm8 >> 1;
            for (uint i = 0; i < regs; i++, addr += 8)
            {
                if (l)
                {
                    Du(s, (d + i) & 31) = Mem.R32(s, addr) | ((ulong)Mem.R32(s, addr + 4) << 32);
                }
                else
                {
                    ulong v = Du(s, (d + i) & 31);
                    Mem.W32(s, addr, (uint)v);
                    Mem.W32(s, addr + 4, (uint)(v >> 32));
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
                    Su(s, (d + i) & 63) = Mem.R32(s, addr);
                }
                else
                {
                    Mem.W32(s, addr, Su(s, (d + i) & 63));
                }
            }
        }
    }
}
