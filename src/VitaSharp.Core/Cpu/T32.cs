using System.Numerics;
using System.Runtime.CompilerServices;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

/// <summary>32-bit Thumb-2 handlers. ins = (hw1 &lt;&lt; 16) | hw2.</summary>
internal static unsafe class T32
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Rg(CpuState* s, uint n, uint pc) => n == 15 ? pc + 4 : s->R[n];

    // ---------------------------------------------------------------- data processing

    /// <summary>
    /// Shared Thumb-2 data-processing semantics. op: 0 AND,1 BIC,2 ORR,3 ORN,4 EOR,8 ADD,10 ADC,11 SBC,13 SUB,14 RSB.
    /// Rd==15 with S means the compare/test form (TST/TEQ/CMN/CMP).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void DataProc(CpuState* s, uint op, bool S, uint rd, uint a, uint b, uint carry)
    {
        uint r, c = 0, v = 0;
        bool arith;
        switch (op)
        {
            case 0: r = a & b; arith = false; break;
            case 1: r = a & ~b; arith = false; break;
            case 2: r = a | b; arith = false; break;
            case 3: r = a | ~b; arith = false; break;
            case 4: r = a ^ b; arith = false; break;
            case 8: r = AddWithCarry(a, b, 0, out c, out v); arith = true; break;
            case 10: r = AddWithCarry(a, b, s->C, out c, out v); arith = true; break;
            case 11: r = AddWithCarry(a, ~b, s->C, out c, out v); arith = true; break;
            case 13: r = AddWithCarry(a, ~b, 1, out c, out v); arith = true; break;
            case 14: r = AddWithCarry(~a, b, 1, out c, out v); arith = true; break;
            default: r = 0; arith = false; break;
        }
        if (S)
        {
            SetNZ(s, r);
            if (arith) { s->C = c; s->V = v; } else s->C = carry;
        }
        if (rd != 15) s->R[rd] = r;
    }

    /// <summary>Data processing (modified immediate): 11110 i 0 op S Rn | 0 imm3 Rd imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DPModImm(CpuState* s, uint ins, uint pc)
    {
        uint op = Bits(ins, 24, 21);
        bool S = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8);
        uint imm12 = (Bit(ins, 26) << 11) | (Bits(ins, 14, 12) << 8) | (ins & 0xFF);
        uint imm = ThumbExpandImmC(imm12, s->C, out uint carry);
        uint a = (rn == 15 && (op == 2 || op == 3)) ? 0 : s->R[rn];
        DataProc(s, op, S, rd, a, imm, carry);
    }

    /// <summary>Data processing (shifted register): 1110101 op S Rn | 0 imm3 Rd imm2 type Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DPShiftReg(CpuState* s, uint ins, uint pc)
    {
        uint op = Bits(ins, 24, 21);
        bool S = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8), rm = ins & 0xF;
        uint imm5 = (Bits(ins, 14, 12) << 2) | Bits(ins, 7, 6);
        if (op == 6) // PKHBT/PKHTB
        {
            s->R[rd] = A32.PkhCore(s->R[rn], s->R[rm], Bit(ins, 5) != 0, imm5);
            return;
        }
        DecodeImmShift(Bits(ins, 5, 4), imm5, out int t, out int n);
        uint b = ShiftC(s->R[rm], t, n, s->C, out uint carry);
        uint a = (rn == 15 && (op == 2 || op == 3)) ? 0 : s->R[rn];
        DataProc(s, op, S, rd, a, b, carry);
    }

    /// <summary>Data processing (plain binary immediate): 11110 i 1 op Rn | 0 imm3 Rd imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DPPlainImm(CpuState* s, uint ins, uint pc)
    {
        uint op = Bits(ins, 24, 20), rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8);
        uint imm12 = (Bit(ins, 26) << 11) | (Bits(ins, 14, 12) << 8) | (ins & 0xFF);
        uint imm3 = Bits(ins, 14, 12), imm2 = Bits(ins, 7, 6);
        switch (op)
        {
            case 0x00: s->R[rd] = (rn == 15 ? (pc + 4) & ~3u : s->R[rn]) + imm12; break;   // ADDW / ADR
            case 0x0A: s->R[rd] = (rn == 15 ? (pc + 4) & ~3u : s->R[rn]) - imm12; break;   // SUBW / ADR
            case 0x04: s->R[rd] = (Bits(ins, 19, 16) << 12) | imm12; break;                 // MOVW
            case 0x0C: s->R[rd] = (s->R[rd] & 0xFFFF) | (((Bits(ins, 19, 16) << 12) | imm12) << 16); break; // MOVT
            case 0x10: case 0x12: // SSAT / SSAT16
                if (op == 0x12 && imm3 == 0 && imm2 == 0) s->R[rd] = A32.Sat16Core(s, false, ins & 0xF, s->R[rn]);
                else s->R[rd] = A32.SatCore(s, false, ins & 0x1F, s->R[rn], Bit(ins, 21), (imm3 << 2) | imm2);
                break;
            case 0x18: case 0x1A: // USAT / USAT16
                if (op == 0x1A && imm3 == 0 && imm2 == 0) s->R[rd] = A32.Sat16Core(s, true, ins & 0xF, s->R[rn]);
                else s->R[rd] = A32.SatCore(s, true, ins & 0x1F, s->R[rn], Bit(ins, 21), (imm3 << 2) | imm2);
                break;
            case 0x14: s->R[rd] = A32.BfxCore(s->R[rn], false, (int)((imm3 << 2) | imm2), (int)(ins & 0x1F)); break; // SBFX
            case 0x1C: s->R[rd] = A32.BfxCore(s->R[rn], true, (int)((imm3 << 2) | imm2), (int)(ins & 0x1F)); break;  // UBFX
            case 0x16: s->R[rd] = A32.BfiCore(s->R[rd], rn == 15 ? 0 : s->R[rn], (int)((imm3 << 2) | imm2), (int)(ins & 0x1F)); break; // BFI/BFC
        }
    }

    // ---------------------------------------------------------------- register-controlled ops

    /// <summary>LSL/LSR/ASR/ROR (register): 11111010 0 type S Rn | 1111 Rd 0000 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShiftReg(CpuState* s, uint ins, uint pc)
    {
        uint type = Bits(ins, 22, 21);
        bool S = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8), rm = ins & 0xF;
        uint r = ShiftRegC(s->R[rn], type, s->R[rm], s->C, out uint c);
        s->R[rd] = r;
        if (S) { SetNZ(s, r); s->C = c; }
    }

    /// <summary>SXTAH..UXTAB: 11111010 0 op Rn | 1111 Rd 1 0 rot Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Extend(CpuState* s, uint ins, uint pc)
    {
        uint op = Bits(ins, 22, 20), rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8), rm = ins & 0xF;
        int rot = (int)Bits(ins, 5, 4) * 8;
        // Thumb op: 000 SXTAH,001 UXTAH,010 SXTAB16,011 UXTAB16,100 SXTAB,101 UXTAB -> ARM ExtendCore op
        uint armOp = op switch { 0 => 3u, 1 => 7u, 2 => 0u, 3 => 4u, 4 => 2u, _ => 6u };
        s->R[rd] = A32.ExtendCore(armOp, rn == 15 ? 0 : s->R[rn], rn == 15, BitOperations.RotateRight(s->R[rm], rot));
    }

    /// <summary>Parallel add/sub: 11111010 1 op1 Rn | 1111 Rd 0 U kind(2) op2 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Parallel(CpuState* s, uint ins, uint pc)
    {
        uint op1 = Bits(ins, 22, 20), rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8), rm = ins & 0xF;
        bool unsigned = Bit(ins, 6) != 0;
        uint kind = Bits(ins, 5, 4);
        uint armOp2 = op1 switch { 1 => 0u, 2 => 1u, 6 => 2u, 5 => 3u, 0 => 4u, _ => 7u };
        uint armOp1 = (unsigned ? 4u : 0u) + (kind == 0 ? 1u : kind == 1 ? 2u : 3u);
        s->R[rd] = A32.ParallelCore(s, armOp1, armOp2, s->R[rn], s->R[rm]);
    }

    /// <summary>Misc: QADD..QDSUB, REV.., SEL, CLZ: 11111010 10 op1 Rn | 1111 Rd 10 op2 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Misc(CpuState* s, uint ins, uint pc)
    {
        uint op1 = Bits(ins, 21, 20), op2 = Bits(ins, 5, 4);
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8), rm = ins & 0xF;
        switch (op1)
        {
            case 0:
            {
                // op2: 00 QADD, 01 QDADD, 10 QSUB, 11 QDSUB -> ARM op: bit0 = sub, bit1 = double
                uint armOp = ((op2 & 1) << 1) | (op2 >> 1);
                s->R[rd] = A32.SatAddSubCore(s, armOp, s->R[rm], s->R[rn]);
                break;
            }
            case 1:
            {
                uint v = s->R[rm];
                s->R[rd] = op2 switch
                {
                    0 => A32.BinaryPrimitivesReverse(v),
                    1 => A32.Rev16(v),
                    2 => A32.RBit(v),
                    _ => A32.RevSh(v),
                };
                break;
            }
            case 2: s->R[rd] = A32.SelCore(s, s->R[rn], s->R[rm]); break;
            default: s->R[rd] = (uint)BitOperations.LeadingZeroCount(s->R[rm]); break;
        }
    }

    /// <summary>Multiply/accumulate: 11111011 0 op1 Rn | Ra Rd 00 op2 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mul(CpuState* s, uint ins, uint pc)
    {
        uint op1 = Bits(ins, 22, 20), op2 = Bits(ins, 5, 4);
        uint rn = Bits(ins, 19, 16), ra = Bits(ins, 15, 12), rd = Bits(ins, 11, 8), rm = ins & 0xF;
        uint vn = s->R[rn], vm = s->R[rm];
        switch (op1)
        {
            case 0:
                if (op2 == 0) s->R[rd] = vn * vm + (ra == 15 ? 0 : s->R[ra]);
                else s->R[rd] = s->R[ra] - vn * vm;
                break;
            case 1: // SMLAxy / SMULxy
                if (ra == 15) A32.HalfMultiplyCore(s, 3, rd, 0, vn, vm, Bit(ins, 5), Bit(ins, 4));
                else A32.HalfMultiplyCore(s, 0, rd, ra, vn, vm, Bit(ins, 5), Bit(ins, 4));
                break;
            case 2: A32.SignedMultiplyMediaCore(s, 0, op2 & 1, rd, ra, vn, vm); break;          // SMLAD/SMUAD
            case 3: // SMLAWy / SMULWy
                if (ra == 15) A32.HalfMultiplyCore(s, 1, rd, 0, vn, vm, 1, Bit(ins, 4));
                else A32.HalfMultiplyCore(s, 1, rd, ra, vn, vm, 0, Bit(ins, 4));
                break;
            case 4: A32.SignedMultiplyMediaCore(s, 0, 2 | (op2 & 1), rd, ra, vn, vm); break;    // SMLSD/SMUSD
            case 5: A32.SignedMultiplyMediaCore(s, 5, op2 & 1, rd, ra, vn, vm); break;          // SMMLA/SMMUL
            case 6: A32.SignedMultiplyMediaCore(s, 5, 6 | (op2 & 1), rd, ra, vn, vm); break;    // SMMLS
            default: s->R[rd] = A32.Usad8Core(vn, vm) + (ra == 15 ? 0 : s->R[ra]); break;      // USAD8/USADA8
        }
    }

    /// <summary>Long multiply/divide: 11111011 1 op1 Rn | RdLo RdHi op2 Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LongMul(CpuState* s, uint ins, uint pc)
    {
        uint op1 = Bits(ins, 22, 20), op2 = Bits(ins, 7, 4);
        uint rn = Bits(ins, 19, 16), lo = Bits(ins, 15, 12), hi = Bits(ins, 11, 8), rm = ins & 0xF;
        uint vn = s->R[rn], vm = s->R[rm];
        switch (op1)
        {
            case 0: { long r = (long)(int)vn * (int)vm; s->R[lo] = (uint)r; s->R[hi] = (uint)((ulong)r >> 32); break; }
            case 1: A32.SignedMultiplyMediaCore(s, 1, 0, hi, 0, vn, vm); break;   // SDIV (Rd in hi field)
            case 2: { ulong r = (ulong)vn * vm; s->R[lo] = (uint)r; s->R[hi] = (uint)(r >> 32); break; }
            case 3: A32.SignedMultiplyMediaCore(s, 3, 0, hi, 0, vn, vm); break;   // UDIV
            case 4:
                if (op2 == 0) { long r = (long)(int)vn * (int)vm + (long)(((ulong)s->R[hi] << 32) | s->R[lo]); s->R[lo] = (uint)r; s->R[hi] = (uint)((ulong)r >> 32); }
                else if ((op2 & 0xC) == 0x8) A32.HalfMultiplyCore(s, 2, hi, lo, vn, vm, Bit(ins, 5), Bit(ins, 4)); // SMLALxy
                else A32.SignedMultiplyMediaCore(s, 4, op2 & 1, hi, lo, vn, vm);   // SMLALD
                break;
            case 5: A32.SignedMultiplyMediaCore(s, 4, 2 | (op2 & 1), hi, lo, vn, vm); break; // SMLSLD
            case 6:
                if (op2 == 0) { ulong r = (ulong)vn * vm + (((ulong)s->R[hi] << 32) | s->R[lo]); s->R[lo] = (uint)r; s->R[hi] = (uint)(r >> 32); }
                else { ulong r = (ulong)vn * vm + s->R[lo] + s->R[hi]; s->R[lo] = (uint)r; s->R[hi] = (uint)(r >> 32); } // UMAAL
                break;
        }
    }

    // ---------------------------------------------------------------- branches / control

    /// <summary>B&lt;cond&gt;.W (T3): 11110 S cond imm6 | 10 J1 0 J2 imm11 (cond evaluated by the caller)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BCondW(CpuState* s, uint ins, uint pc)
    {
        uint S = Bit(ins, 26), j1 = Bit(ins, 13), j2 = Bit(ins, 11);
        uint imm = (S << 20) | (j2 << 19) | (j1 << 18) | (Bits(ins, 21, 16) << 12) | ((ins & 0x7FF) << 1);
        s->R[15] = pc + 4 + SignExtend(imm, 21);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint BranchImm24(uint ins)
    {
        uint S = Bit(ins, 26), j1 = Bit(ins, 13), j2 = Bit(ins, 11);
        uint i1 = ~(j1 ^ S) & 1, i2 = ~(j2 ^ S) & 1;
        uint imm = (S << 24) | (i1 << 23) | (i2 << 22) | (Bits(ins, 25, 16) << 12) | ((ins & 0x7FF) << 1);
        return SignExtend(imm, 25);
    }

    /// <summary>B.W (T4)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BW(CpuState* s, uint ins, uint pc) => s->R[15] = pc + 4 + BranchImm24(ins);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bl(CpuState* s, uint ins, uint pc)
    {
        s->R[14] = (pc + 4) | 1;
        s->R[15] = pc + 4 + BranchImm24(ins);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BlxImm(CpuState* s, uint ins, uint pc)
    {
        s->R[14] = (pc + 4) | 1;
        s->Thumb = 0;
        s->R[15] = (((pc + 4) & ~3u) + BranchImm24(ins)) & ~3u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Msr(CpuState* s, uint ins, uint pc) => A32.MsrCore(s, Bits(ins, 11, 10), s->R[Bits(ins, 19, 16)]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mrs(CpuState* s, uint ins, uint pc) => s->R[Bits(ins, 11, 8)] = (s->Cpsr & 0xF80F0000) | 0x10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BxjOrSubsPcLr(CpuState* s, uint ins, uint pc)
    {
        // BXJ behaves as BX when Jazelle is not available; SUBS PC, LR, #imm is an exception return
        if (Bits(ins, 26, 20) == 0x3C) BxWritePc(s, s->R[Bits(ins, 19, 16)]);
        else BxWritePc(s, s->R[14] - (ins & 0xFF));
    }

    /// <summary>TBB/TBH: 111010001101 Rn | 1111 0000 000 H Rm</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Tbb(CpuState* s, uint ins, uint pc)
    {
        uint rn = Bits(ins, 19, 16), rm = ins & 0xF;
        uint baseAddr = rn == 15 ? pc + 4 : s->R[rn];
        uint off = Bit(ins, 4) != 0 ? Mem.R16(s, baseAddr + (s->R[rm] << 1)) : Mem.R8(s, baseAddr + s->R[rm]);
        s->R[15] = pc + 4 + off * 2;
    }

    // ---------------------------------------------------------------- loads / stores

    /// <summary>LDM/STM (IA/DB), PUSH/POP .W: 1110100 op 0 W L Rn | P M 0 list</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdmStm(CpuState* s, uint ins, uint pc)
    {
        uint op = Bits(ins, 24, 23);
        bool W = Bit(ins, 21) != 0, L = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), list = ins & 0xFFFF;
        bool ia = op == 1;
        A32.BlockTransfer(s, rn, list, !ia, ia, W, L, pc + 4);
    }

    /// <summary>LDRD/STRD (immediate): 1110100 P U 1 W L Rn | Rt Rt2 imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStDual(CpuState* s, uint ins, uint pc)
    {
        bool P = Bit(ins, 24) != 0, U = Bit(ins, 23) != 0, W = Bit(ins, 21) != 0, L = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), rt2 = Bits(ins, 11, 8);
        uint imm = (ins & 0xFF) << 2;
        uint baseVal = rn == 15 ? (pc + 4) & ~3u : s->R[rn];
        uint offAddr = U ? baseVal + imm : baseVal - imm;
        uint addr = P ? offAddr : baseVal;
        if (L)
        {
            uint a = Mem.R32(s, addr), b = Mem.R32(s, addr + 4);
            if (W) s->R[rn] = offAddr;
            s->R[rt] = a; s->R[rt2] = b;
        }
        else
        {
            Mem.W32(s, addr, s->R[rt]); Mem.W32(s, addr + 4, s->R[rt2]);
            if (W) s->R[rn] = offAddr;
        }
    }

    /// <summary>LDREX/STREX: 11101000010 L Rn | Rt Rd imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExclusiveWord(CpuState* s, uint ins, uint pc)
    {
        bool L = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), rd = Bits(ins, 11, 8);
        uint addr = s->R[rn] + ((ins & 0xFF) << 2);
        A32.ExclusiveCore(s, 0, L, addr, rt, rd, 0);
    }

    /// <summary>LDREXB/H/D, STREXB/H/D: 11101000110 L Rn | Rt Rt2/Rd op3 Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExclusiveOther(CpuState* s, uint ins, uint pc)
    {
        bool L = Bit(ins, 20) != 0;
        uint rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12), rt2 = Bits(ins, 11, 8), op3 = Bits(ins, 7, 4), rd = ins & 0xF;
        uint op = op3 switch { 4 => 2u, 5 => 3u, _ => 1u };
        A32.ExclusiveCore(s, op, L, s->R[rn], rt, rd, rt2);
    }

    /// <summary>
    /// Single loads/stores: 1111100 S A size L Rn | Rt ...
    /// A=1: imm12, A=0: imm8 with P/U/W (bit 11 set) or register with LSL #imm2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdSt(CpuState* s, uint ins, uint pc)
    {
        bool signed = Bit(ins, 24) != 0, imm12Form = Bit(ins, 23) != 0, L = Bit(ins, 20) != 0;
        uint size = Bits(ins, 22, 21);
        uint rn = Bits(ins, 19, 16), rt = Bits(ins, 15, 12);
        uint addr, offAddr;
        bool wback = false;
        if (rn == 15)
        {
            uint basePc = (pc + 4) & ~3u;
            addr = offAddr = imm12Form ? basePc + (ins & 0xFFF) : basePc - (ins & 0xFFF);
        }
        else if (imm12Form)
        {
            addr = offAddr = s->R[rn] + (ins & 0xFFF);
        }
        else if (Bit(ins, 11) != 0)
        {
            bool P = Bit(ins, 10) != 0, U = Bit(ins, 9) != 0;
            wback = Bit(ins, 8) != 0;
            uint imm8 = ins & 0xFF;
            offAddr = U ? s->R[rn] + imm8 : s->R[rn] - imm8;
            addr = P ? offAddr : s->R[rn];
        }
        else
        {
            addr = offAddr = s->R[rn] + (s->R[ins & 0xF] << (int)Bits(ins, 5, 4));
        }

        if (L)
        {
            uint v = size switch
            {
                0 => signed ? (uint)(sbyte)Mem.R8(s, addr) : Mem.R8(s, addr),
                1 => signed ? (uint)(short)Mem.R16(s, addr) : Mem.R16(s, addr),
                _ => Mem.R32(s, addr),
            };
            if (wback) s->R[rn] = offAddr;
            if (rt == 15)
            {
                if (size == 2) BxWritePc(s, v); // PLD/PLI otherwise (no-op)
            }
            else s->R[rt] = v;
        }
        else
        {
            uint v = s->R[rt];
            switch (size)
            {
                case 0: Mem.W8(s, addr, v); break;
                case 1: Mem.W16(s, addr, v); break;
                default: Mem.W32(s, addr, v); break;
            }
            if (wback) s->R[rn] = offAddr;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clrex(CpuState* s, uint ins, uint pc) => s->ExclusiveValid = 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Barrier(CpuState* s, uint ins, uint pc) => Interlocked.MemoryBarrier();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Nop(CpuState* s, uint ins, uint pc) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Yield(CpuState* s, uint ins, uint pc)
    {
        s->Exit = ExitReason.Yield;
        s->R[15] = pc + 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Udf(CpuState* s, uint ins, uint pc)
    {
        s->Exit = ExitReason.Undefined;
        s->SvcPc = pc;
        s->SvcImm = ins;
        s->R[15] = pc;
    }

    /// <summary>MRC/MCR in Thumb are encoded like ARM with cond=1110; reuse the ARM handlers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mrc(CpuState* s, uint ins, uint pc) => A32.Mrc(s, ins, pc);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mcr(CpuState* s, uint ins, uint pc) => A32.Mcr(s, ins, pc);
}
