using System.Numerics;
using System.Runtime.CompilerServices;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// ARM (A32) instruction handlers. Every handler has the signature (CpuState*, instruction, address)
/// so the JIT can call it with constant operands and let RyuJIT fold the field decoding away.
/// Condition codes are evaluated by the caller.
/// </summary>
internal static unsafe partial class A32
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Rg(CpuState* state, uint n, uint pc) => n == 15 ? pc + 8 : state->R[n];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRd(CpuState* state, uint rd, uint v)
    {
        if (rd == 15)
        {
            AluWritePcArm(state, v);
        }
        else
        {
            state->R[rd] = v;
        }
    }

    // ================================================================ data processing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DataProc(CpuState* state, uint opc, bool setFlags, uint rd, uint a, uint b, uint shCarry)
    {
        uint r, c, v;
        switch (opc)
        {
            case 0x0:
                r = a & b;
                goto logical;
            case 0x1:
                r = a ^ b;
                goto logical;
            case 0x2:
                r = AddWithCarry(a, ~b, 1, out c, out v);
                goto arith;
            case 0x3:
                r = AddWithCarry(~a, b, 1, out c, out v);
                goto arith;
            case 0x4:
                r = AddWithCarry(a, b, 0, out c, out v);
                goto arith;
            case 0x5:
                r = AddWithCarry(a, b, state->C, out c, out v);
                goto arith;
            case 0x6:
                r = AddWithCarry(a, ~b, state->C, out c, out v);
                goto arith;
            case 0x7:
                r = AddWithCarry(~a, b, state->C, out c, out v);
                goto arith;
            case 0x8:
                r = a & b;
                SetNZ(state, r);
                state->C = shCarry;
                return;
            case 0x9:
                r = a ^ b;
                SetNZ(state, r);
                state->C = shCarry;
                return;
            case 0xA:
                r = AddWithCarry(a, ~b, 1, out c, out v);
                SetNZ(state, r);
                state->C = c;
                state->V = v;
                return;
            case 0xB:
                r = AddWithCarry(a, b, 0, out c, out v);
                SetNZ(state, r);
                state->C = c;
                state->V = v;
                return;
            case 0xC:
                r = a | b;
                goto logical;
            case 0xD:
                r = b;
                goto logical;
            case 0xE:
                r = a & ~b;
                goto logical;
            default:
                r = ~b;
                goto logical;
        }
    logical:
        if (setFlags && rd != 15)
        {
            SetNZ(state, r);
            state->C = shCarry;
        }
        WriteRd(state, rd, r);
        return;
    arith:
        if (setFlags && rd != 15)
        {
            SetNZ(state, r);
            state->C = c;
            state->V = v;
        }
        WriteRd(state, rd, r);
    }

    /// <summary>cond 00 1 opcode S Rn Rd imm12</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DPImm(CpuState* state, uint ins, uint pc)
    {
        uint opc = Bits(ins, 24, 21);
        bool S = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12);
        uint imm = ArmExpandImmC(ins & 0xFFF, state->C, out uint carry);
        DataProc(state, opc, S, rd, Rg(state, rn, pc), imm, carry);
    }

    /// <summary>cond 00 0 opcode S Rn Rd imm5 type 0 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DPRegImm(CpuState* state, uint ins, uint pc)
    {
        uint opc = Bits(ins, 24, 21);
        bool S = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        DecodeImmShift(Bits(ins, 6, 5), Bits(ins, 11, 7), out int t, out int n);
        uint b = ShiftC(Rg(state, rm, pc), t, n, state->C, out uint carry);
        DataProc(state, opc, S, rd, Rg(state, rn, pc), b, carry);
    }

    /// <summary>cond 00 0 opcode S Rn Rd Rs 0 type 1 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DPRegReg(CpuState* state, uint ins, uint pc)
    {
        uint opc = Bits(ins, 24, 21);
        bool S = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rs = Bits(ins, 11, 8), rm = ins & 0xF;
        uint b = ShiftRegC(state->R[rm], Bits(ins, 6, 5), state->R[rs], state->C, out uint carry);
        DataProc(state, opc, S, rd, state->R[rn], b, carry);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovW(CpuState* state, uint ins, uint pc)
        => WriteRd(state, Bits(ins, 15, 12), (Bits(ins, 19, 16) << 12) | (ins & 0xFFF));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovT(CpuState* state, uint ins, uint pc)
    {
        uint rd = Bits(ins, 15, 12);
        state->R[rd] = (state->R[rd] & 0xFFFF) | (((Bits(ins, 19, 16) << 12) | (ins & 0xFFF)) << 16);
    }

    // ================================================================ multiply

    /// <summary>MUL/MLA/UMAAL/MLS/UMULL/UMLAL/SMULL/SMLAL: cond 0000 op S Rd/RdHi Ra/RdLo Rm 1001 Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Multiply(CpuState* state, uint ins, uint pc)
    {
        uint op = Bits(ins, 23, 21);
        bool S = Bit(ins, 20) != 0;
        uint hi = Bits(ins, 19, 16), lo = Bits(ins, 15, 12), rm = Bits(ins, 11, 8), rn = ins & 0xF;
        uint a = Rg(state, rn, pc), b = Rg(state, rm, pc);
        switch (op)
        {
            case 0:
            {
                uint r = a * b;
                state->R[hi] = r;
                if (S)
                {
                    SetNZ(state, r);
                }

                break;
            }
            case 1:
            {
                uint r = a * b + state->R[lo];
                state->R[hi] = r;
                if (S)
                {
                    SetNZ(state, r);
                }

                break;
            }
            case 2:
            {
                ulong r = (ulong)a * b + state->R[lo] + state->R[hi];
                state->R[lo] = (uint)r;
                state->R[hi] = (uint)(r >> 32);
                break;
            } // UMAAL
            case 3:
            {
                state->R[hi] = state->R[lo] - a * b;
                break;
            } // MLS
            case 4:
            {
                ulong r = (ulong)a * b;
                state->R[lo] = (uint)r;
                state->R[hi] = (uint)(r >> 32);
                if (S)
                {
                    SetNZ64(state, r);
                }

                break;
            }
            case 5:
            {
                ulong r = (ulong)a * b + (((ulong)state->R[hi] << 32) | state->R[lo]);
                state->R[lo] = (uint)r;
                state->R[hi] = (uint)(r >> 32);
                if (S)
                {
                    SetNZ64(state, r);
                }

                break;
            }
            case 6:
            {
                long r = (long)(int)a * (int)b;
                state->R[lo] = (uint)r;
                state->R[hi] = (uint)((ulong)r >> 32);
                if (S)
                {
                    SetNZ64(state, (ulong)r);
                }

                break;
            }
            default:
            {
                long r = (long)(int)a * (int)b + (long)(((ulong)state->R[hi] << 32) | state->R[lo]);
                state->R[lo] = (uint)r;
                state->R[hi] = (uint)((ulong)r >> 32);
                if (S)
                {
                    SetNZ64(state, (ulong)r);
                }

                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetNZ64(CpuState* state, ulong r)
    {
        state->N = (uint)(r >> 63);
        state->Z = r == 0 ? 1u : 0u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Half(uint v, uint top) => top != 0 ? (short)(v >> 16) : (short)v;

    /// <summary>SMLAxy/SMLAWy/SMULWy/SMLALxy/SMULxy: cond 00010 op 0 Rd Ra Rm 1 y x 0 Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void HalfMultiply(CpuState* state, uint ins, uint pc)
    {
        uint op = Bits(ins, 22, 21);
        uint rd = Bits(ins, 19, 16), ra = Bits(ins, 15, 12), rm = Bits(ins, 11, 8), rn = ins & 0xF;
        uint y = Bit(ins, 6), x = Bit(ins, 5);
        HalfMultiplyCore(state, op, rd, ra, Rg(state, rn, pc), Rg(state, rm, pc), x, y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void HalfMultiplyCore(CpuState* state, uint op, uint rd, uint ra, uint vn, uint vm, uint x, uint y)
    {
        switch (op)
        {
            case 0: // SMLAxy
            {
                long r = (long)Half(vn, x) * Half(vm, y) + (int)state->R[ra];
                state->R[rd] = (uint)r;
                if (r != (int)r)
                {
                    state->Q = 1;
                }

                break;
            }
            case 1:
                if (x == 0) // SMLAWy
                {
                    long r = (((long)(int)vn * Half(vm, y)) >> 16) + (int)state->R[ra];
                    state->R[rd] = (uint)r;
                    if (r != (int)r)
                    {
                        state->Q = 1;
                    }
                }
                else
                {
                    state->R[rd] = (uint)(((long)(int)vn * Half(vm, y)) >> 16); // SMULWy
                }

                break;
            case 2: // SMLALxy  (rd=RdHi, ra=RdLo)
            {
                long acc = (long)(((ulong)state->R[rd] << 32) | state->R[ra]);
                long r = acc + (long)Half(vn, x) * Half(vm, y);
                state->R[ra] = (uint)r;
                state->R[rd] = (uint)((ulong)r >> 32);
                break;
            }
            default: // SMULxy
                state->R[rd] = (uint)(Half(vn, x) * Half(vm, y));
                break;
        }
    }

    /// <summary>SMLAD/SMLSD/SMUAD/SMUSD/SMLALD/SMLSLD/SDIV/UDIV/SMMLA/SMMLS/SMMUL: cond 01110 op1 Rd Ra Rm op2 1 Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SignedMultiplyMedia(CpuState* state, uint ins, uint pc)
    {
        uint op1 = Bits(ins, 22, 20), op2 = Bits(ins, 7, 5);
        uint rd = Bits(ins, 19, 16), ra = Bits(ins, 15, 12), rm = Bits(ins, 11, 8), rn = ins & 0xF;
        SignedMultiplyMediaCore(state, op1, op2, rd, ra, Rg(state, rn, pc), Rg(state, rm, pc));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SignedMultiplyMediaCore(CpuState* state, uint op1, uint op2, uint rd, uint ra, uint vn, uint vm)
    {
        bool swap = (op2 & 1) != 0;
        if (swap && (op1 == 0 || op1 == 4))
        {
            vm = BitOperations.RotateRight(vm, 16);
        }

        int p1 = (short)vn * (short)vm;
        int p2 = (short)(vn >> 16) * (short)(vm >> 16);
        switch (op1)
        {
            case 0:
                if ((op2 & 2) == 0) // SMLAD / SMUAD (ra==15)
                {
                    long r = (long)p1 + p2 + (ra == 15 ? 0 : (int)state->R[ra]);
                    state->R[rd] = (uint)r;
                    if (r != (int)r)
                    {
                        state->Q = 1;
                    }
                }
                else // SMLSD / SMUSD
                {
                    long r = (long)p1 - p2 + (ra == 15 ? 0 : (int)state->R[ra]);
                    state->R[rd] = (uint)r;
                    if (r != (int)r)
                    {
                        state->Q = 1;
                    }
                }
                break;
            case 1: // SDIV
            {
                int n = (int)vn, m = (int)vm;
                state->R[rd] = m == 0 ? 0 : (n == int.MinValue && m == -1) ? 0x80000000u : (uint)(n / m);
                break;
            }
            case 3: // UDIV
                state->R[rd] = vm == 0 ? 0 : vn / vm;
                break;
            case 4: // SMLALD / SMLSLD  (rd=RdHi, ra=RdLo)
            {
                long acc = (long)(((ulong)state->R[rd] << 32) | state->R[ra]);
                long r = (op2 & 2) == 0 ? acc + p1 + p2 : acc + p1 - p2;
                state->R[ra] = (uint)r;
                state->R[rd] = (uint)((ulong)r >> 32);
                break;
            }
            case 5:
            {
                bool round = (op2 & 1) != 0;
                long prod = (long)(int)vn * (int)vm;
                long r;
                if ((op2 & 6) == 0)
                {
                    r = (ra == 15 ? 0 : ((long)(int)state->R[ra] << 32)) + prod;          // SMMLA / SMMUL
                }
                else
                {
                    r = ((long)(int)state->R[ra] << 32) - prod;                                           // SMMLS
                }

                if (round)
                {
                    r += 0x80000000L;
                }

                state->R[rd] = (uint)(r >> 32);
                break;
            }
        }
    }

    // ================================================================ saturating arithmetic

    /// <summary>QADD/QSUB/QDADD/QDSUB: cond 00010 op 0 Rn Rd 0000 0101 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SatAddSub(CpuState* state, uint ins, uint pc)
    {
        uint op = Bits(ins, 22, 21);
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        state->R[rd] = SatAddSubCore(state, op, Rg(state, rm, pc), Rg(state, rn, pc));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint SatAddSubCore(CpuState* state, uint op, uint vm, uint vn)
    {
        long a = (int)vm, b = (int)vn;
        if ((op & 2) != 0)
        {
            b = (int)SatQ(state, b * 2);
        }

        return (op & 1) == 0 ? SatQ(state, a + b) : SatQ(state, a - b);
    }

    // ================================================================ media: parallel add/sub

    /// <summary>Parallel add/subtract: cond 01100 op1 Rn Rd 1111 op2 1 Rm (op1: 1=S 2=Q 3=SH 5=U 6=UQ 7=UH)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Parallel(CpuState* state, uint ins, uint pc)
    {
        uint op1 = Bits(ins, 22, 20), op2 = Bits(ins, 7, 5);
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        state->R[rd] = ParallelCore(state, op1, op2, Rg(state, rn, pc), Rg(state, rm, pc));
    }

    /// <summary>op2: 0=ADD16 1=ASX 2=SAX 3=SUB16 4=ADD8 7=SUB8</summary>
    internal static uint ParallelCore(CpuState* state, uint op1, uint op2, uint a, uint b)
    {
        bool signed = op1 < 4;
        uint kind = op1 & 3; // 1 = plain (sets GE), 2 = saturating, 3 = halving
        if (op2 == 4 || op2 == 7)
        {
            uint r = 0, ge = 0;
            for (int i = 0; i < 4; i++)
            {
                int x = signed ? (sbyte)(a >> (i * 8)) : (byte)(a >> (i * 8));
                int y = signed ? (sbyte)(b >> (i * 8)) : (byte)(b >> (i * 8));
                int v = op2 == 4 ? x + y : x - y;
                uint res;
                if (kind == 1)
                {
                    res = (uint)v & 0xFF;
                    if (signed ? v >= 0 : (op2 == 4 ? v >= 0x100 : v >= 0))
                    {
                        ge |= 1u << i;
                    }
                }
                else if (kind == 2)
                {
                    res = signed ? (uint)SignedSat(v, 8, out _) & 0xFF : UnsignedSat(v, 8, out _);
                }
                else
                {
                    res = (uint)(v >> 1) & 0xFF;
                }

                r |= res << (i * 8);
            }
            if (kind == 1)
            {
                state->GE = ge;
            }

            return r;
        }
        else
        {
            int ah = signed ? (short)(a >> 16) : (ushort)(a >> 16), al = signed ? (short)a : (ushort)a;
            int bh = signed ? (short)(b >> 16) : (ushort)(b >> 16), bl = signed ? (short)b : (ushort)b;
            int lo, hi;
            switch (op2)
            {
                case 0:
                    lo = al + bl;
                    hi = ah + bh;
                    break;   // ADD16
                case 1:
                    lo = al - bh;
                    hi = ah + bl;
                    break;   // ASX
                case 2:
                    lo = al + bh;
                    hi = ah - bl;
                    break;   // SAX
                default:
                    lo = al - bl;
                    hi = ah - bh;
                    break;  // SUB16
            }
            uint rl, rh;
            if (kind == 1)
            {
                rl = (uint)lo & 0xFFFF;
                rh = (uint)hi & 0xFFFF;
                uint ge = 0;
                bool geLo, geHi;
                if (signed)
                {
                    geLo = lo >= 0;
                    geHi = hi >= 0;
                }
                else
                {
                    geLo = op2 switch
                    {
                        0 => lo >= 0x10000,
                        1 => lo >= 0,
                        2 => lo >= 0x10000,
                        _ => lo >= 0
                    };
                    geHi = op2 switch
                    {
                        0 => hi >= 0x10000,
                        1 => hi >= 0x10000,
                        2 => hi >= 0,
                        _ => hi >= 0
                    };
                }
                if (geLo)
                {
                    ge |= 3;
                }

                if (geHi)
                {
                    ge |= 0xC;
                }

                state->GE = ge;
            }
            else if (kind == 2)
            {
                rl = signed ? (uint)SignedSat(lo, 16, out _) & 0xFFFF : UnsignedSat(lo, 16, out _);
                rh = signed ? (uint)SignedSat(hi, 16, out _) & 0xFFFF : UnsignedSat(hi, 16, out _);
            }
            else
            {
                rl = (uint)(lo >> 1) & 0xFFFF;
                rh = (uint)(hi >> 1) & 0xFFFF;
            }
            return rl | (rh << 16);
        }
    }

    // ================================================================ media: pack / unpack / sat / reverse

    /// <summary>PKHBT/PKHTB: cond 01101000 Rn Rd imm5 tb 01 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pkh(CpuState* state, uint ins, uint pc)
    {
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        bool tb = Bit(ins, 6) != 0;
        state->R[rd] = PkhCore(state->R[rn], state->R[rm], tb, Bits(ins, 11, 7));
    }

    internal static uint PkhCore(uint vn, uint vm, bool tb, uint imm5)
    {
        if (tb)
        {
            uint sh = Shift(vm, ASR, imm5 == 0 ? 32 : (int)imm5, 0);
            return (vn & 0xFFFF0000) | (sh & 0xFFFF);
        }
        uint sl = Shift(vm, LSL, (int)imm5, 0);
        return (sl & 0xFFFF0000) | (vn & 0xFFFF);
    }

    /// <summary>SSAT/USAT: cond 0110 1U1 sat_imm Rd imm5 sh 01 Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sat(CpuState* state, uint ins, uint pc)
    {
        bool unsigned = Bit(ins, 22) != 0;
        uint sat = Bits(ins, 20, 16), rd = Bits(ins, 15, 12), rn = ins & 0xF;
        uint sh = Bit(ins, 6), imm5 = Bits(ins, 11, 7);
        state->R[rd] = SatCore(state, unsigned, sat, state->R[rn], sh, imm5);
    }

    internal static uint SatCore(CpuState* state, bool unsigned, uint satImm, uint v, uint sh, uint imm5)
    {
        DecodeImmShift(sh << 1, imm5, out int t, out int n);
        int operand = (int)Shift(v, t, n, state->C);
        bool sat;
        uint r = unsigned ? UnsignedSat(operand, (int)satImm, out sat) : (uint)SignedSat(operand, (int)satImm + 1, out sat);
        if (sat)
        {
            state->Q = 1;
        }

        return r;
    }

    /// <summary>SSAT16/USAT16: cond 0110 1U10 sat_imm Rd 1111 0011 Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sat16(CpuState* state, uint ins, uint pc)
    {
        bool unsigned = Bit(ins, 22) != 0;
        uint sat = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rn = ins & 0xF;
        state->R[rd] = Sat16Core(state, unsigned, sat, state->R[rn]);
    }

    internal static uint Sat16Core(CpuState* state, bool unsigned, uint satImm, uint v)
    {
        bool s1, s2;
        uint lo, hi;
        if (unsigned)
        {
            lo = UnsignedSat((short)v, (int)satImm, out s1);
            hi = UnsignedSat((short)(v >> 16), (int)satImm, out s2);
        }
        else
        {
            lo = (uint)SignedSat((short)v, (int)satImm + 1, out s1) & 0xFFFF;
            hi = (uint)SignedSat((short)(v >> 16), (int)satImm + 1, out s2) & 0xFFFF;
        }
        if (s1 || s2)
        {
            state->Q = 1;
        }

        return (lo & 0xFFFF) | (hi << 16);
    }

    /// <summary>SXTAB16/SXTB16/SXTAB/SXTB/SXTAH/SXTH and unsigned forms: cond 01101 op Rn Rd rot 00 0111 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Extend(CpuState* state, uint ins, uint pc)
    {
        uint op = Bits(ins, 22, 20);
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        int rot = (int)Bits(ins, 11, 10) * 8;
        state->R[rd] = ExtendCore(op, rn == 15 ? 0 : state->R[rn], rn == 15, BitOperations.RotateRight(state->R[rm], rot));
    }

    /// <summary>op: 0=SXTB16 2=SXTB 3=SXTH 4=UXTB16 6=UXTB 7=UXTH</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ExtendCore(uint op, uint vn, bool noAdd, uint rotated)
    {
        switch (op)
        {
            case 0:
            {
                uint lo = (uint)(sbyte)rotated & 0xFFFF, hi = (uint)(sbyte)(rotated >> 16) & 0xFFFF;
                if (!noAdd)
                {
                    lo = (vn + lo) & 0xFFFF;
                    hi = ((vn >> 16) + hi) & 0xFFFF;
                }
                return lo | (hi << 16);
            }
            case 2:
                return vn + (uint)(sbyte)rotated;
            case 3:
                return vn + (uint)(short)rotated;
            case 4:
            {
                uint lo = rotated & 0xFF, hi = (rotated >> 16) & 0xFF;
                if (!noAdd)
                {
                    lo = (vn + lo) & 0xFFFF;
                    hi = ((vn >> 16) + hi) & 0xFFFF;
                }
                return lo | (hi << 16);
            }
            case 6:
                return vn + (rotated & 0xFF);
            default:
                return vn + (rotated & 0xFFFF);
        }
    }

    /// <summary>REV/REV16/RBIT/REVSH: cond 01101 o1 11 1111 Rd 1111 o2 1 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reverse(CpuState* state, uint ins, uint pc)
    {
        uint rd = Bits(ins, 15, 12), rm = ins & 0xF;
        uint o1 = Bit(ins, 22), o2 = Bit(ins, 7);
        uint v = state->R[rm];
        uint r;
        if (o1 == 0)
        {
            r = o2 == 0 ? BinaryPrimitivesReverse(v) : Rev16(v);
        }
        else
        {
            r = o2 == 0 ? RBit(v) : RevSh(v);
        }

        state->R[rd] = r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static uint BinaryPrimitivesReverse(uint v) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static uint Rev16(uint v) => ((v & 0x00FF00FF) << 8) | ((v >> 8) & 0x00FF00FF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal static uint RevSh(uint v) => (uint)(short)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF));

    internal static uint RBit(uint v)
    {
        v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
        v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
        v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
        return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    }

    /// <summary>SEL: cond 01101000 Rn Rd 1111 1011 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Sel(CpuState* state, uint ins, uint pc)
    {
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        state->R[rd] = SelCore(state, state->R[rn], state->R[rm]);
    }

    internal static uint SelCore(CpuState* state, uint a, uint b)
    {
        uint mask = 0;
        for (int i = 0; i < 4; i++)
        {
            if (((state->GE >> i) & 1) != 0)
            {
                mask |= 0xFFu << (i * 8);
            }
        }

        return (a & mask) | (b & ~mask);
    }

    /// <summary>USAD8/USADA8: cond 01111000 Rd Ra Rm 0001 Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Usad8(CpuState* state, uint ins, uint pc)
    {
        uint rd = Bits(ins, 19, 16), ra = Bits(ins, 15, 12), rm = Bits(ins, 11, 8), rn = ins & 0xF;
        state->R[rd] = Usad8Core(Rg(state, rn, pc), Rg(state, rm, pc)) + (ra == 15 ? 0 : state->R[ra]);
    }

    internal static uint Usad8Core(uint a, uint b)
    {
        uint sum = 0;
        for (int i = 0; i < 32; i += 8)
        {
            sum += (uint)Math.Abs((int)((a >> i) & 0xFF) - (int)((b >> i) & 0xFF));
        }

        return sum;
    }

    /// <summary>SBFX/UBFX: cond 0111 1U1 widthm1 Rd lsb 101 Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bfx(CpuState* state, uint ins, uint pc)
    {
        bool unsigned = Bit(ins, 22) != 0;
        uint widthm1 = Bits(ins, 20, 16), rd = Bits(ins, 15, 12), lsb = Bits(ins, 11, 7), rn = ins & 0xF;
        state->R[rd] = BfxCore(state->R[rn], unsigned, (int)lsb, (int)widthm1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint BfxCore(uint v, bool unsigned, int lsb, int widthm1)
    {
        int width = widthm1 + 1;
        uint field = (uint)((v >> lsb) & (width >= 32 ? 0xFFFFFFFFul : ((1ul << width) - 1)));
        return unsigned ? field : SignExtend(field, width);
    }

    /// <summary>BFC/BFI: cond 0111110 msb Rd lsb 001 Rn (Rn=15: BFC)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bfi(CpuState* state, uint ins, uint pc)
    {
        uint msb = Bits(ins, 20, 16), rd = Bits(ins, 15, 12), lsb = Bits(ins, 11, 7), rn = ins & 0xF;
        state->R[rd] = BfiCore(state->R[rd], rn == 15 ? 0 : state->R[rn], (int)lsb, (int)msb);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint BfiCore(uint dst, uint src, int lsb, int msb)
    {
        if (msb < lsb)
        {
            return dst;
        }

        int width = msb - lsb + 1;
        uint mask = (uint)((width >= 32 ? 0xFFFFFFFFul : ((1ul << width) - 1)) << lsb);
        return (dst & ~mask) | ((src << lsb) & mask);
    }

    // ================================================================ misc

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clz(CpuState* state, uint ins, uint pc)
        => state->R[Bits(ins, 15, 12)] = (uint)BitOperations.LeadingZeroCount(state->R[ins & 0xF]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bx(CpuState* state, uint ins, uint pc) => BxWritePc(state, state->R[ins & 0xF]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BlxReg(CpuState* state, uint ins, uint pc)
    {
        uint target = state->R[ins & 0xF];
        state->R[14] = pc + 4;
        BxWritePc(state, target);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void B(CpuState* state, uint ins, uint pc)
        => state->R[15] = pc + 8 + (SignExtend(ins & 0xFFFFFF, 24) << 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bl(CpuState* state, uint ins, uint pc)
    {
        state->R[14] = pc + 4;
        state->R[15] = pc + 8 + (SignExtend(ins & 0xFFFFFF, 24) << 2);
    }

    /// <summary>BLX (immediate) from ARM: 1111101H imm24</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BlxImm(CpuState* state, uint ins, uint pc)
    {
        state->R[14] = pc + 4;
        uint target = pc + 8 + (SignExtend(ins & 0xFFFFFF, 24) << 2) + (Bit(ins, 24) << 1);
        state->Thumb = 1;
        state->R[15] = target;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mrs(CpuState* state, uint ins, uint pc) => state->R[Bits(ins, 15, 12)] = state->Cpsr & 0xF80F0000 | 0x10;

    /// <summary>MSR APSR, Rn / #imm: only the flags (nzcvq) and GE fields are writable from user mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MsrReg(CpuState* state, uint ins, uint pc) => MsrCore(state, Bits(ins, 19, 18), state->R[ins & 0xF]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MsrImm(CpuState* state, uint ins, uint pc)
        => MsrCore(state, Bits(ins, 19, 18), ArmExpandImmC(ins & 0xFFF, 0, out _));

    internal static void MsrCore(CpuState* state, uint mask, uint v)
    {
        if ((mask & 2) != 0)
        {
            state->N = v >> 31;
            state->Z = (v >> 30) & 1;
            state->C = (v >> 29) & 1;
            state->V = (v >> 28) & 1;
            state->Q = (v >> 27) & 1;
        }
        if ((mask & 1) != 0)
        {
            state->GE = (v >> 16) & 0xF;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Nop(CpuState* state, uint ins, uint pc)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Svc(CpuState* state, uint ins, uint pc)
    {
        state->Exit = ExitReason.Svc;
        state->SvcImm = ins & 0xFFFFFF;
        state->SvcPc = pc;
        state->R[15] = pc + 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bkpt(CpuState* state, uint ins, uint pc)
    {
        state->Exit = ExitReason.Breakpoint;
        state->SvcPc = pc;
        state->R[15] = pc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Udf(CpuState* state, uint ins, uint pc)
    {
        state->Exit = ExitReason.Undefined;
        state->SvcPc = pc;
        state->SvcImm = ins;
        state->R[15] = pc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Yield(CpuState* state, uint ins, uint pc)
    {
        state->Exit = ExitReason.Yield;
        state->R[15] = pc + 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clrex(CpuState* state, uint ins, uint pc) => state->ExclusiveValid = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Barrier(CpuState* state, uint ins, uint pc) => Interlocked.MemoryBarrier();

    /// <summary>MRC/MCR p15: only the c13 thread-ID registers exist for user mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mrc(CpuState* state, uint ins, uint pc)
    {
        uint rt = Bits(ins, 15, 12), crn = Bits(ins, 19, 16), opc2 = Bits(ins, 7, 5);
        uint v = 0;
        if (crn == 13 && opc2 == 3)
        {
            v = state->TpidrUro;
        }
        else if (crn == 13 && opc2 == 2)
        {
            v = state->TpidrUrw;
        }

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
    public static void Mcr(CpuState* state, uint ins, uint pc)
    {
        uint rt = Bits(ins, 15, 12), crn = Bits(ins, 19, 16), opc2 = Bits(ins, 7, 5);
        if (crn == 13 && opc2 == 2)
        {
            state->TpidrUrw = state->R[rt];
        }

        // c7 cache maintenance / barriers (CP15DMB etc.)
        if (crn == 7)
        {
            Interlocked.MemoryBarrier();
            if (IsInstructionCacheInvalidate(ins))
            {
                InvalidateInstructionCache(ins, state->R[rt]);
            }
        }
    }

    /// <summary>True for the CP15 c7 instruction cache invalidations: ICIALLUIS (c7, c1, 0), ICIALLU (c7, c5, 0) and
    /// ICIMVAU (c7, c5, 1). The native backend runs these through the interpreter (a callout).</summary>
    public static bool IsInstructionCacheInvalidate(uint ins)
    {
        uint crn = Bits(ins, 19, 16), crm = ins & 0xF, opc1 = Bits(ins, 23, 21), opc2 = Bits(ins, 7, 5), coproc = Bits(ins, 11, 8);
        if (coproc != 15 || crn != 7 || opc1 != 0)
        {
            return false;
        }

        return (crm == 1 && opc2 == 0) || (crm == 5 && opc2 is 0 or 1);
    }

    /// <summary>Size of a Cortex-A9 instruction cache line (ICIMVAU invalidates one).</summary>
    private const uint IcacheLine = 32;

    /// <summary>
    /// An instruction cache invalidation run by guest code (kernel code that rewrote instructions, as ARMv7 requires
    /// before the new code may run): the translated blocks of the line (ICIMVAU, <paramref name="mva"/>) or of the
    /// whole process (ICIALLU, ICIALLUIS) are dropped, in every backend, so the rewritten code runs as written. Like the
    /// reference ARM recompilers (Dynarmic's and Ryujinx's cache-maintenance exits), stores alone do not drop code: on
    /// the hardware the instruction side sees new code only after this maintenance.
    /// </summary>
    private static void InvalidateInstructionCache(uint ins, uint mva)
    {
        if (Kernel.GuestThread.Current?.Process.Code is not { } code)
        {
            return;
        }

        bool line = (ins & 0xF) == 5 && Bits(ins, 7, 5) == 1;
        if (line)
        {
            code.Invalidate(mva & ~(IcacheLine - 1), IcacheLine);
        }
        else
        {
            code.Invalidate(0, uint.MaxValue);
        }
    }


    // ================================================================ load / store

    /// <summary>LDR/STR/LDRB/STRB immediate: cond 010 P U B W L Rn Rt imm12</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStImm(CpuState* state, uint ins, uint pc)
    {
        uint rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12);
        LdStCore(state, ins, pc, rn, rt, ins & 0xFFF);
    }

    /// <summary>LDR/STR/LDRB/STRB register: cond 011 P U B W L Rn Rt imm5 type 0 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStReg(CpuState* state, uint ins, uint pc)
    {
        uint rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), rm = ins & 0xF;
        DecodeImmShift(Bits(ins, 6, 5), Bits(ins, 11, 7), out int t, out int n);
        LdStCore(state, ins, pc, rn, rt, Shift(Rg(state, rm, pc), t, n, state->C));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LdStCore(CpuState* state, uint ins, uint pc, uint rn, uint rt, uint offset)
    {
        bool P = Bit(ins, 24) != 0, U = Bit(ins, 23) != 0, Bb = Bit(ins, 22) != 0, W = Bit(ins, 21) != 0, L = Bit(ins, 20) != 0;
        uint baseVal = rn == 15 ? (pc + 8) & ~3u : state->R[rn];
        uint offAddr = U ? baseVal + offset : baseVal - offset;
        uint addr = P ? offAddr : baseVal;
        bool wback = !P || W;
        if (L)
        {
            uint v = Bb ? Mem.R8(state, addr) : Mem.R32(state, addr);
            if (wback)
            {
                state->R[rn] = offAddr;
            }

            if (rt == 15)
            {
                BxWritePc(state, v);
            }
            else
            {
                state->R[rt] = v;
            }
        }
        else
        {
            uint v = rt == 15 ? pc + 8 : state->R[rt];
            if (Bb)
            {
                Mem.W8(state, addr, v);
            }
            else
            {
                Mem.W32(state, addr, v);
            }

            if (wback)
            {
                state->R[rn] = offAddr;
            }
        }
    }

    /// <summary>
    /// LDRH/STRH/LDRSB/LDRSH/LDRD/STRD: cond 000 P U I W L Rn Rt imm4H 1 op 1 imm4L|Rm
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStExtra(CpuState* state, uint ins, uint pc)
    {
        bool P = Bit(ins, 24) != 0, U = Bit(ins, 23) != 0, I = Bit(ins, 22) != 0, W = Bit(ins, 21) != 0, L = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), op = Bits(ins, 6, 5);
        uint offset = I ? (Bits(ins, 11, 8) << 4) | (ins & 0xF) : state->R[ins & 0xF];
        uint baseVal = rn == 15 ? (pc + 8) & ~3u : state->R[rn];
        uint offAddr = U ? baseVal + offset : baseVal - offset;
        uint addr = P ? offAddr : baseVal;
        bool wback = !P || W;
        switch ((L ? 4u : 0u) | op)
        {
            case 1:
                Mem.W16(state, addr, Rg(state, rt, pc));
                break;                                        // STRH
            case 2:
                state->R[rt] = Mem.R32(state, addr);
                state->R[rt + 1] = Mem.R32(state, addr + 4);
                break;          // LDRD
            case 3:
                Mem.W32(state, addr, Rg(state, rt, pc));
                Mem.W32(state, addr + 4, Rg(state, rt + 1, pc));
                break;    // STRD
            case 5:
                if (wback)
                {
                    state->R[rn] = offAddr;
                }

                state->R[rt] = Mem.R16(state, addr);
                return;            // LDRH
            case 6:
                if (wback)
                {
                    state->R[rn] = offAddr;
                }

                state->R[rt] = (uint)(sbyte)Mem.R8(state, addr);
                return; // LDRSB
            case 7:
                if (wback)
                {
                    state->R[rn] = offAddr;
                }

                state->R[rt] = (uint)(short)Mem.R16(state, addr);
                return; // LDRSH
        }
        if (wback)
        {
            state->R[rn] = offAddr;
        }
    }

    /// <summary>LDM/STM family: cond 100 P U S W L Rn register_list</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdmStm(CpuState* state, uint ins, uint pc)
    {
        bool P = Bit(ins, 24) != 0, U = Bit(ins, 23) != 0, W = Bit(ins, 21) != 0, L = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16);
        uint list = ins & 0xFFFF;
        BlockTransfer(state, rn, list, P, U, W, L, pc + 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void BlockTransfer(CpuState* state, uint rn, uint list, bool P, bool U, bool W, bool L, uint pcValue)
    {
        uint count = (uint)BitOperations.PopCount(list);
        uint baseVal = state->R[rn];
        uint start = U ? (P ? baseVal + 4 : baseVal) : (P ? baseVal - 4 * count : baseVal - 4 * count + 4);
        uint newBase = U ? baseVal + 4 * count : baseVal - 4 * count;
        uint addr = start;
        if (L)
        {
            if (W)
            {
                state->R[rn] = newBase;
            }

            uint pcLoad = 0;
            bool loadPc = false;
            for (int i = 0; i < 16; i++)
            {
                if ((list & (1u << i)) == 0)
                {
                    continue;
                }

                uint v = Mem.R32(state, addr);
                addr += 4;
                if (i == 15)
                {
                    pcLoad = v;
                    loadPc = true;
                }
                else
                {
                    state->R[i] = v;
                }
            }
            if (loadPc)
            {
                BxWritePc(state, pcLoad);
            }
        }
        else
        {
            for (int i = 0; i < 16; i++)
            {
                if ((list & (1u << i)) == 0)
                {
                    continue;
                }

                Mem.W32(state, addr, i == 15 ? pcValue : (i == rn ? baseVal : state->R[i]));
                addr += 4;
            }
            if (W)
            {
                state->R[rn] = newBase;
            }
        }
    }

    // ================================================================ synchronization

    /// <summary>LDREX/STREX (word, doubleword, byte, halfword): cond 0001 1 op L Rn Rd 1111 1001 Rt</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Exclusive(CpuState* state, uint ins, uint pc)
    {
        uint op = Bits(ins, 22, 21);
        bool L = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rt = ins & 0xF;
        // doubleword: Rt2 = Rt + 1, where Rt is bits 15:12 for LDREXD and bits 3:0 for STREXD
        uint first = L ? rd : rt;
        ExclusiveCore(state, op, L, state->R[rn], first, rd, (first + 1) & 15);
    }

    /// <summary>op: 0=word 1=doubleword 2=byte 3=halfword. For loads rt = destination; for stores rd = status.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ExclusiveCore(CpuState* state, uint op, bool load, uint addr, uint rt, uint rd, uint rt2)
    {
        if (load)
        {
            ReserveExclusive(state, addr);
            switch (op)
            {
                case 0:
                {
                    uint v = Volatile.Read(ref *(uint*)Mem.Ptr(state, addr));
                    state->R[rt] = v;
                    SetExclusive(state, addr, v);
                    break;
                }
                case 1:
                {
                    ulong v = Volatile.Read(ref *(ulong*)Mem.Ptr(state, addr));
                    state->R[rt] = (uint)v;
                    state->R[rt2] = (uint)(v >> 32);
                    SetExclusive(state, addr, v);
                    break;
                }
                case 2:
                {
                    uint v = Mem.R8(state, addr);
                    state->R[rt] = v;
                    SetExclusive(state, addr, v);
                    break;
                }
                default:
                {
                    uint v = Mem.R16(state, addr);
                    state->R[rt] = v;
                    SetExclusive(state, addr, v);
                    break;
                }
            }
        }
        else
        {
            bool ok = op switch
            {
                0 => StoreExclusive32(state, addr, state->R[rt]),
                1 => StoreExclusive64(state, addr, state->R[rt] | ((ulong)state->R[rt2] << 32)),
                2 => StoreExclusive8(state, addr, state->R[rt]),
                _ => StoreExclusive16(state, addr, state->R[rt]),
            };
            state->R[rd] = ok ? 0u : 1u;
        }
    }

    /// <summary>SWP/SWPB: cond 00010 B 00 Rn Rt 0000 1001 Rt2</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swp(CpuState* state, uint ins, uint pc)
    {
        bool b = Bit(ins, 22) != 0;
        uint rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), rt2 = ins & 0xF;
        uint addr = state->R[rn];
        if (b)
        {
            uint old = Mem.R8(state, addr);
            Mem.W8(state, addr, state->R[rt2]);
            state->R[rt] = old;
        }
        else
        {
            state->R[rt] = Interlocked.Exchange(ref *(uint*)Mem.Ptr(state, addr), state->R[rt2]);
            Mem.MarkDirty(state, addr);
        }
    }
}
