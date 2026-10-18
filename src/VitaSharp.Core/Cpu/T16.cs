using System.Numerics;
using System.Runtime.CompilerServices;
using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// 16-bit Thumb handlers. The instruction word carries the halfword in bits [15:0]; bit 31 is set by the
/// decoder when the instruction sits inside an IT block (which suppresses flag setting for many ops).
/// </summary>
internal static unsafe class T16
{
    const uint InItBit = 0x8000_0000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool SetFlags(uint ins) => (ins & InItBit) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Rg(CpuState* s, uint n, uint pc) => n == 15 ? pc + 4 : s->R[n];

    /// <summary>LSL/LSR/ASR (immediate): 000 op imm5 Rm Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShiftImm(CpuState* s, uint ins, uint pc)
    {
        uint op = Bits(ins, 12, 11), imm5 = Bits(ins, 10, 6), rm = Bits(ins, 5, 3), rd = ins & 7;
        DecodeImmShift(op, imm5, out int t, out int n);
        uint r = ShiftC(s->R[rm], t, n, s->C, out uint c);
        s->R[rd] = r;
        if (SetFlags(ins)) { SetNZ(s, r); s->C = c; }
    }

    /// <summary>ADD/SUB register and 3-bit immediate: 00011 I op Rm/imm3 Rn Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSub3(CpuState* s, uint ins, uint pc)
    {
        bool imm = Bit(ins, 10) != 0, sub = Bit(ins, 9) != 0;
        uint m = Bits(ins, 8, 6), rn = Bits(ins, 5, 3), rd = ins & 7;
        uint b = imm ? m : s->R[m];
        uint r = sub ? AddWithCarry(s->R[rn], ~b, 1, out uint c, out uint v) : AddWithCarry(s->R[rn], b, 0, out c, out v);
        s->R[rd] = r;
        if (SetFlags(ins)) { SetNZ(s, r); s->C = c; s->V = v; }
    }

    /// <summary>MOV/CMP/ADD/SUB imm8: 001 op Rdn imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Imm8(CpuState* s, uint ins, uint pc)
    {
        uint op = Bits(ins, 12, 11), rdn = Bits(ins, 10, 8), imm = ins & 0xFF;
        switch (op)
        {
            case 0:
                s->R[rdn] = imm;
                if (SetFlags(ins)) SetNZ(s, imm);
                break;
            case 1: AddFlags(s, s->R[rdn], ~imm, 1); break;
            case 2:
            {
                uint r = AddWithCarry(s->R[rdn], imm, 0, out uint c, out uint v);
                s->R[rdn] = r;
                if (SetFlags(ins)) { SetNZ(s, r); s->C = c; s->V = v; }
                break;
            }
            default:
            {
                uint r = AddWithCarry(s->R[rdn], ~imm, 1, out uint c, out uint v);
                s->R[rdn] = r;
                if (SetFlags(ins)) { SetNZ(s, r); s->C = c; s->V = v; }
                break;
            }
        }
    }

    /// <summary>Data processing: 010000 op Rm Rdn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DataProc(CpuState* s, uint ins, uint pc)
    {
        uint op = Bits(ins, 9, 6), rm = Bits(ins, 5, 3), rdn = ins & 7;
        uint a = s->R[rdn], b = s->R[rm];
        bool sf = SetFlags(ins);
        uint r, c, v;
        switch (op)
        {
            case 0x0: r = a & b; s->R[rdn] = r; if (sf) SetNZ(s, r); return;
            case 0x1: r = a ^ b; s->R[rdn] = r; if (sf) SetNZ(s, r); return;
            case 0x2: r = ShiftRegC(a, 0, b, s->C, out c); s->R[rdn] = r; if (sf) { SetNZ(s, r); s->C = c; } return;
            case 0x3: r = ShiftRegC(a, 1, b, s->C, out c); s->R[rdn] = r; if (sf) { SetNZ(s, r); s->C = c; } return;
            case 0x4: r = ShiftRegC(a, 2, b, s->C, out c); s->R[rdn] = r; if (sf) { SetNZ(s, r); s->C = c; } return;
            case 0x5: r = AddWithCarry(a, b, s->C, out c, out v); break;
            case 0x6: r = AddWithCarry(a, ~b, s->C, out c, out v); break;
            case 0x7: r = ShiftRegC(a, 3, b, s->C, out c); s->R[rdn] = r; if (sf) { SetNZ(s, r); s->C = c; } return;
            case 0x8: SetNZ(s, a & b); return;                                  // TST
            case 0x9: r = AddWithCarry(~b, 0, 1, out c, out v); break;          // RSB #0 (NEG): Rd = 0 - Rm
            case 0xA: AddFlags(s, a, ~b, 1); return;                            // CMP
            case 0xB: AddFlags(s, a, b, 0); return;                             // CMN
            case 0xC: r = a | b; s->R[rdn] = r; if (sf) SetNZ(s, r); return;
            case 0xD: r = a * b; s->R[rdn] = r; if (sf) SetNZ(s, r); return;
            case 0xE: r = a & ~b; s->R[rdn] = r; if (sf) SetNZ(s, r); return;
            default: r = ~b; s->R[rdn] = r; if (sf) SetNZ(s, r); return;
        }
        s->R[rdn] = r;
        if (sf) { SetNZ(s, r); s->C = c; s->V = v; }
    }

    /// <summary>ADD (high registers): 01000100 DN Rm Rdn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddHi(CpuState* s, uint ins, uint pc)
    {
        uint rdn = (Bit(ins, 7) << 3) | (ins & 7), rm = Bits(ins, 6, 3);
        uint r = Rg(s, rdn, pc) + Rg(s, rm, pc);
        if (rdn == 15) BranchWritePcThumb(s, r); else s->R[rdn] = r;
    }

    /// <summary>CMP (high registers): 01000101 N Rm Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CmpHi(CpuState* s, uint ins, uint pc)
    {
        uint rn = (Bit(ins, 7) << 3) | (ins & 7), rm = Bits(ins, 6, 3);
        AddFlags(s, Rg(s, rn, pc), ~Rg(s, rm, pc), 1);
    }

    /// <summary>MOV (high registers): 01000110 D Rm Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovHi(CpuState* s, uint ins, uint pc)
    {
        uint rd = (Bit(ins, 7) << 3) | (ins & 7), rm = Bits(ins, 6, 3);
        uint v = Rg(s, rm, pc);
        if (rd == 15) BranchWritePcThumb(s, v); else s->R[rd] = v;
    }

    /// <summary>BX/BLX register: 01000111 L Rm 000</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BxBlx(CpuState* s, uint ins, uint pc)
    {
        uint rm = Bits(ins, 6, 3);
        uint target = Rg(s, rm, pc);
        if (Bit(ins, 7) != 0) s->R[14] = (pc + 2) | 1;
        BxWritePc(s, target);
    }

    /// <summary>LDR (literal): 01001 Rt imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrLit(CpuState* s, uint ins, uint pc)
        => s->R[Bits(ins, 10, 8)] = Mem.R32(((pc + 4) & ~3u) + ((ins & 0xFF) << 2));

    /// <summary>Load/store register offset: 0101 op Rm Rn Rt</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStReg(CpuState* s, uint ins, uint pc)
    {
        uint op = Bits(ins, 11, 9), rm = Bits(ins, 8, 6), rn = Bits(ins, 5, 3), rt = ins & 7;
        uint addr = s->R[rn] + s->R[rm];
        switch (op)
        {
            case 0: Mem.W32(addr, s->R[rt]); break;
            case 1: Mem.W16(addr, s->R[rt]); break;
            case 2: Mem.W8(addr, s->R[rt]); break;
            case 3: s->R[rt] = (uint)(sbyte)Mem.R8(addr); break;
            case 4: s->R[rt] = Mem.R32(addr); break;
            case 5: s->R[rt] = Mem.R16(addr); break;
            case 6: s->R[rt] = Mem.R8(addr); break;
            default: s->R[rt] = (uint)(short)Mem.R16(addr); break;
        }
    }

    /// <summary>STR/LDR/STRB/LDRB imm5: 011 B L imm5 Rn Rt</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStImm(CpuState* s, uint ins, uint pc)
    {
        bool b = Bit(ins, 12) != 0, l = Bit(ins, 11) != 0;
        uint imm5 = Bits(ins, 10, 6), rn = Bits(ins, 5, 3), rt = ins & 7;
        uint addr = s->R[rn] + (b ? imm5 : imm5 << 2);
        if (l) s->R[rt] = b ? Mem.R8(addr) : Mem.R32(addr);
        else if (b) Mem.W8(addr, s->R[rt]); else Mem.W32(addr, s->R[rt]);
    }

    /// <summary>STRH/LDRH imm5: 1000 L imm5 Rn Rt</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStH(CpuState* s, uint ins, uint pc)
    {
        uint imm5 = Bits(ins, 10, 6), rn = Bits(ins, 5, 3), rt = ins & 7;
        uint addr = s->R[rn] + (imm5 << 1);
        if (Bit(ins, 11) != 0) s->R[rt] = Mem.R16(addr); else Mem.W16(addr, s->R[rt]);
    }

    /// <summary>STR/LDR SP-relative: 1001 L Rt imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStSp(CpuState* s, uint ins, uint pc)
    {
        uint rt = Bits(ins, 10, 8);
        uint addr = s->R[13] + ((ins & 0xFF) << 2);
        if (Bit(ins, 11) != 0) s->R[rt] = Mem.R32(addr); else Mem.W32(addr, s->R[rt]);
    }

    /// <summary>ADR: 10100 Rd imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Adr(CpuState* s, uint ins, uint pc) => s->R[Bits(ins, 10, 8)] = ((pc + 4) & ~3u) + ((ins & 0xFF) << 2);

    /// <summary>ADD Rd, SP, #imm8*4: 10101 Rd imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSpImm(CpuState* s, uint ins, uint pc) => s->R[Bits(ins, 10, 8)] = s->R[13] + ((ins & 0xFF) << 2);

    /// <summary>ADD/SUB SP, SP, #imm7*4: 1011 0000 S imm7</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AdjSp(CpuState* s, uint ins, uint pc)
    {
        uint imm = (ins & 0x7F) << 2;
        s->R[13] = Bit(ins, 7) != 0 ? s->R[13] - imm : s->R[13] + imm;
    }

    /// <summary>CBZ/CBNZ: 1011 op 0 i 1 imm5 Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Cbz(CpuState* s, uint ins, uint pc)
    {
        uint rn = ins & 7;
        bool nonZero = Bit(ins, 11) != 0;
        uint imm = (Bit(ins, 9) << 6) | (Bits(ins, 7, 3) << 1);
        if ((s->R[rn] != 0) == nonZero) s->R[15] = pc + 4 + imm;
    }

    /// <summary>SXTH/SXTB/UXTH/UXTB: 1011 0010 op Rm Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Extend(CpuState* s, uint ins, uint pc)
    {
        uint rm = Bits(ins, 5, 3), rd = ins & 7;
        uint v = s->R[rm];
        s->R[rd] = Bits(ins, 7, 6) switch
        {
            0 => (uint)(short)v,
            1 => (uint)(sbyte)v,
            2 => v & 0xFFFF,
            _ => v & 0xFF,
        };
    }

    /// <summary>PUSH: 1011 010 M list</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Push(CpuState* s, uint ins, uint pc)
    {
        uint list = (ins & 0xFF) | (Bit(ins, 8) << 14);
        A32.BlockTransfer(s, 13, list, true, false, true, false, pc + 4);
    }

    /// <summary>POP: 1011 110 P list</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pop(CpuState* s, uint ins, uint pc)
    {
        uint list = (ins & 0xFF) | (Bit(ins, 8) << 15);
        A32.BlockTransfer(s, 13, list, false, true, true, true, pc + 4);
    }

    /// <summary>REV/REV16/REVSH: 1011 1010 op Rm Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Rev(CpuState* s, uint ins, uint pc)
    {
        uint rm = Bits(ins, 5, 3), rd = ins & 7;
        uint v = s->R[rm];
        s->R[rd] = Bits(ins, 7, 6) switch
        {
            0 => A32.BinaryPrimitivesReverse(v),
            1 => A32.Rev16(v),
            _ => A32.RevSh(v),
        };
    }

    /// <summary>STM Rn!, list: 11000 Rn list</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Stm(CpuState* s, uint ins, uint pc)
        => A32.BlockTransfer(s, Bits(ins, 10, 8), ins & 0xFF, false, true, true, false, pc + 4);

    /// <summary>LDM Rn{!}, list: 11001 Rn list (writeback unless Rn is in the list)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ldm(CpuState* s, uint ins, uint pc)
    {
        uint rn = Bits(ins, 10, 8), list = ins & 0xFF;
        A32.BlockTransfer(s, rn, list, false, true, (list & (1u << (int)rn)) == 0, true, pc + 4);
    }

    /// <summary>B&lt;cond&gt;: 1101 cond imm8 (condition evaluated by the caller)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BCond(CpuState* s, uint ins, uint pc) => s->R[15] = pc + 4 + (SignExtend(ins & 0xFF, 8) << 1);

    /// <summary>B: 11100 imm11</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void B(CpuState* s, uint ins, uint pc) => s->R[15] = pc + 4 + (SignExtend(ins & 0x7FF, 11) << 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Svc(CpuState* s, uint ins, uint pc)
    {
        s->Exit = ExitReason.Svc;
        s->SvcImm = ins & 0xFF;
        s->SvcPc = pc;
        s->R[15] = pc + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bkpt(CpuState* s, uint ins, uint pc)
    {
        s->Exit = ExitReason.Breakpoint;
        s->SvcPc = pc;
        s->SvcImm = ins & 0xFF;
        s->R[15] = pc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Udf(CpuState* s, uint ins, uint pc)
    {
        s->Exit = ExitReason.Undefined;
        s->SvcPc = pc;
        s->SvcImm = ins & 0xFFFF;
        s->R[15] = pc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Yield(CpuState* s, uint ins, uint pc)
    {
        s->Exit = ExitReason.Yield;
        s->R[15] = pc + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Nop(CpuState* s, uint ins, uint pc) { }
}
