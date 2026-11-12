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
    private const uint InItBit = 0x8000_0000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SetFlags(uint ins) => (ins & InItBit) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Rg(CpuState* state, uint n, uint pc) => n == 15 ? pc + 4 : state->R[n];

    /// <summary>LSL/LSR/ASR (immediate): 000 op imm5 Rm Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ShiftImm(CpuState* state, uint ins, uint pc)
    {
        uint op = Bits(ins, 12, 11), imm5 = Bits(ins, 10, 6), rm = Bits(ins, 5, 3), rd = ins & 7;
        DecodeImmShift(op, imm5, out int t, out int n);
        uint r = ShiftC(state->R[rm], t, n, state->C, out uint c);
        state->R[rd] = r;
        if (SetFlags(ins))
        {
            SetNZ(state, r);
            state->C = c;
        }
    }

    /// <summary>ADD/SUB register and 3-bit immediate: 00011 I op Rm/imm3 Rn Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSub3(CpuState* state, uint ins, uint pc)
    {
        bool imm = Bit(ins, 10) != 0, sub = Bit(ins, 9) != 0;
        uint m = Bits(ins, 8, 6), rn = Bits(ins, 5, 3), rd = ins & 7;
        uint b = imm ? m : state->R[m];
        uint r = sub ? AddWithCarry(state->R[rn], ~b, 1, out uint c, out uint v) : AddWithCarry(state->R[rn], b, 0, out c, out v);
        state->R[rd] = r;
        if (SetFlags(ins))
        {
            SetNZ(state, r);
            state->C = c;
            state->V = v;
        }
    }

    /// <summary>MOV/CMP/ADD/SUB imm8: 001 op Rdn imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Imm8(CpuState* state, uint ins, uint pc)
    {
        uint op = Bits(ins, 12, 11), rdn = Bits(ins, 10, 8), imm = ins & 0xFF;
        switch (op)
        {
            case 0:
                state->R[rdn] = imm;
                if (SetFlags(ins))
                {
                    SetNZ(state, imm);
                }

                break;
            case 1:
                AddFlags(state, state->R[rdn], ~imm, 1);
                break;
            case 2:
            {
                uint r = AddWithCarry(state->R[rdn], imm, 0, out uint c, out uint v);
                state->R[rdn] = r;
                if (SetFlags(ins))
                {
                    SetNZ(state, r);
                    state->C = c;
                    state->V = v;
                }
                break;
            }
            default:
            {
                uint r = AddWithCarry(state->R[rdn], ~imm, 1, out uint c, out uint v);
                state->R[rdn] = r;
                if (SetFlags(ins))
                {
                    SetNZ(state, r);
                    state->C = c;
                    state->V = v;
                }
                break;
            }
        }
    }

    /// <summary>Data processing: 010000 op Rm Rdn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DataProc(CpuState* state, uint ins, uint pc)
    {
        uint op = Bits(ins, 9, 6), rm = Bits(ins, 5, 3), rdn = ins & 7;
        uint a = state->R[rdn], b = state->R[rm];
        bool sf = SetFlags(ins);
        uint r, c, v;
        switch (op)
        {
            case 0x0:
                r = a & b;
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                }

                return;
            case 0x1:
                r = a ^ b;
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                }

                return;
            case 0x2:
                r = ShiftRegC(a, 0, b, state->C, out c);
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                    state->C = c;
                }
                return;
            case 0x3:
                r = ShiftRegC(a, 1, b, state->C, out c);
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                    state->C = c;
                }
                return;
            case 0x4:
                r = ShiftRegC(a, 2, b, state->C, out c);
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                    state->C = c;
                }
                return;
            case 0x5:
                r = AddWithCarry(a, b, state->C, out c, out v);
                break;
            case 0x6:
                r = AddWithCarry(a, ~b, state->C, out c, out v);
                break;
            case 0x7:
                r = ShiftRegC(a, 3, b, state->C, out c);
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                    state->C = c;
                }
                return;
            case 0x8:
                SetNZ(state, a & b);
                return;                                  // TST
            case 0x9:
                r = AddWithCarry(~b, 0, 1, out c, out v);
                break;          // RSB #0 (NEG): Rd = 0 - Rm
            case 0xA:
                AddFlags(state, a, ~b, 1);
                return;                            // CMP
            case 0xB:
                AddFlags(state, a, b, 0);
                return;                             // CMN
            case 0xC:
                r = a | b;
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                }

                return;
            case 0xD:
                r = a * b;
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                }

                return;
            case 0xE:
                r = a & ~b;
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                }

                return;
            default:
                r = ~b;
                state->R[rdn] = r;
                if (sf)
                {
                    SetNZ(state, r);
                }

                return;
        }
        state->R[rdn] = r;
        if (sf)
        {
            SetNZ(state, r);
            state->C = c;
            state->V = v;
        }
    }

    /// <summary>ADD (high registers): 01000100 DN Rm Rdn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddHi(CpuState* state, uint ins, uint pc)
    {
        uint rdn = (Bit(ins, 7) << 3) | (ins & 7), rm = Bits(ins, 6, 3);
        uint r = Rg(state, rdn, pc) + Rg(state, rm, pc);
        if (rdn == 15)
        {
            BranchWritePcThumb(state, r);
        }
        else
        {
            state->R[rdn] = r;
        }
    }

    /// <summary>CMP (high registers): 01000101 N Rm Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CmpHi(CpuState* state, uint ins, uint pc)
    {
        uint rn = (Bit(ins, 7) << 3) | (ins & 7), rm = Bits(ins, 6, 3);
        AddFlags(state, Rg(state, rn, pc), ~Rg(state, rm, pc), 1);
    }

    /// <summary>MOV (high registers): 01000110 D Rm Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MovHi(CpuState* state, uint ins, uint pc)
    {
        uint rd = (Bit(ins, 7) << 3) | (ins & 7), rm = Bits(ins, 6, 3);
        uint v = Rg(state, rm, pc);
        if (rd == 15)
        {
            BranchWritePcThumb(state, v);
        }
        else
        {
            state->R[rd] = v;
        }
    }

    /// <summary>BX/BLX register: 01000111 L Rm 000</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BxBlx(CpuState* state, uint ins, uint pc)
    {
        uint rm = Bits(ins, 6, 3);
        uint target = Rg(state, rm, pc);
        if (Bit(ins, 7) != 0)
        {
            state->R[14] = (pc + 2) | 1;
        }

        BxWritePc(state, target);
    }

    /// <summary>LDR (literal): 01001 Rt imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdrLit(CpuState* state, uint ins, uint pc)
        => state->R[Bits(ins, 10, 8)] = Mem.R32(state, ((pc + 4) & ~3u) + ((ins & 0xFF) << 2));

    /// <summary>Load/store register offset: 0101 op Rm Rn Rt</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStReg(CpuState* state, uint ins, uint pc)
    {
        uint op = Bits(ins, 11, 9), rm = Bits(ins, 8, 6), rn = Bits(ins, 5, 3), rt = ins & 7;
        uint addr = state->R[rn] + state->R[rm];
        switch (op)
        {
            case 0:
                Mem.W32(state, addr, state->R[rt]);
                break;
            case 1:
                Mem.W16(state, addr, state->R[rt]);
                break;
            case 2:
                Mem.W8(state, addr, state->R[rt]);
                break;
            case 3:
                state->R[rt] = (uint)(sbyte)Mem.R8(state, addr);
                break;
            case 4:
                state->R[rt] = Mem.R32(state, addr);
                break;
            case 5:
                state->R[rt] = Mem.R16(state, addr);
                break;
            case 6:
                state->R[rt] = Mem.R8(state, addr);
                break;
            default:
                state->R[rt] = (uint)(short)Mem.R16(state, addr);
                break;
        }
    }

    /// <summary>STR/LDR/STRB/LDRB imm5: 011 B L imm5 Rn Rt</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStImm(CpuState* state, uint ins, uint pc)
    {
        bool b = Bit(ins, 12) != 0, l = Bit(ins, 11) != 0;
        uint imm5 = Bits(ins, 10, 6), rn = Bits(ins, 5, 3), rt = ins & 7;
        uint addr = state->R[rn] + (b ? imm5 : imm5 << 2);
        if (l)
        {
            state->R[rt] = b ? Mem.R8(state, addr) : Mem.R32(state, addr);
        }
        else if (b)
        {
            Mem.W8(state, addr, state->R[rt]);
        }
        else
        {
            Mem.W32(state, addr, state->R[rt]);
        }
    }

    /// <summary>STRH/LDRH imm5: 1000 L imm5 Rn Rt</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStH(CpuState* state, uint ins, uint pc)
    {
        uint imm5 = Bits(ins, 10, 6), rn = Bits(ins, 5, 3), rt = ins & 7;
        uint addr = state->R[rn] + (imm5 << 1);
        if (Bit(ins, 11) != 0)
        {
            state->R[rt] = Mem.R16(state, addr);
        }
        else
        {
            Mem.W16(state, addr, state->R[rt]);
        }
    }

    /// <summary>STR/LDR SP-relative: 1001 L Rt imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LdStSp(CpuState* state, uint ins, uint pc)
    {
        uint rt = Bits(ins, 10, 8);
        uint addr = state->R[13] + ((ins & 0xFF) << 2);
        if (Bit(ins, 11) != 0)
        {
            state->R[rt] = Mem.R32(state, addr);
        }
        else
        {
            Mem.W32(state, addr, state->R[rt]);
        }
    }

    /// <summary>ADR: 10100 Rd imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Adr(CpuState* state, uint ins, uint pc) => state->R[Bits(ins, 10, 8)] = ((pc + 4) & ~3u) + ((ins & 0xFF) << 2);

    /// <summary>ADD Rd, SP, #imm8*4: 10101 Rd imm8</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSpImm(CpuState* state, uint ins, uint pc) => state->R[Bits(ins, 10, 8)] = state->R[13] + ((ins & 0xFF) << 2);

    /// <summary>ADD/SUB SP, SP, #imm7*4: 1011 0000 S imm7</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AdjSp(CpuState* state, uint ins, uint pc)
    {
        uint imm = (ins & 0x7F) << 2;
        state->R[13] = Bit(ins, 7) != 0 ? state->R[13] - imm : state->R[13] + imm;
    }

    /// <summary>CBZ/CBNZ: 1011 op 0 i 1 imm5 Rn</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Cbz(CpuState* state, uint ins, uint pc)
    {
        uint rn = ins & 7;
        bool nonZero = Bit(ins, 11) != 0;
        uint imm = (Bit(ins, 9) << 6) | (Bits(ins, 7, 3) << 1);
        if ((state->R[rn] != 0) == nonZero)
        {
            state->R[15] = pc + 4 + imm;
        }
    }

    /// <summary>SXTH/SXTB/UXTH/UXTB: 1011 0010 op Rm Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Extend(CpuState* state, uint ins, uint pc)
    {
        uint rm = Bits(ins, 5, 3), rd = ins & 7;
        uint v = state->R[rm];
        state->R[rd] = Bits(ins, 7, 6) switch
        {
            0 => (uint)(short)v,
            1 => (uint)(sbyte)v,
            2 => v & 0xFFFF,
            _ => v & 0xFF,
        };
    }

    /// <summary>PUSH: 1011 010 M list</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Push(CpuState* state, uint ins, uint pc)
    {
        uint list = (ins & 0xFF) | (Bit(ins, 8) << 14);
        A32.BlockTransfer(state, 13, list, true, false, true, false, pc + 4);
    }

    /// <summary>POP: 1011 110 P list</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pop(CpuState* state, uint ins, uint pc)
    {
        uint list = (ins & 0xFF) | (Bit(ins, 8) << 15);
        A32.BlockTransfer(state, 13, list, false, true, true, true, pc + 4);
    }

    /// <summary>REV/REV16/REVSH: 1011 1010 op Rm Rd</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Rev(CpuState* state, uint ins, uint pc)
    {
        uint rm = Bits(ins, 5, 3), rd = ins & 7;
        uint v = state->R[rm];
        state->R[rd] = Bits(ins, 7, 6) switch
        {
            0 => A32.BinaryPrimitivesReverse(v),
            1 => A32.Rev16(v),
            _ => A32.RevSh(v),
        };
    }

    /// <summary>STM Rn!, list: 11000 Rn list</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Stm(CpuState* state, uint ins, uint pc)
        => A32.BlockTransfer(state, Bits(ins, 10, 8), ins & 0xFF, false, true, true, false, pc + 4);

    /// <summary>LDM Rn{!}, list: 11001 Rn list (writeback unless Rn is in the list)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ldm(CpuState* state, uint ins, uint pc)
    {
        uint rn = Bits(ins, 10, 8), list = ins & 0xFF;
        A32.BlockTransfer(state, rn, list, false, true, (list & (1u << (int)rn)) == 0, true, pc + 4);
    }

    /// <summary>B&lt;cond&gt;: 1101 cond imm8 (condition evaluated by the caller)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BCond(CpuState* state, uint ins, uint pc) => state->R[15] = pc + 4 + (SignExtend(ins & 0xFF, 8) << 1);

    /// <summary>B: 11100 imm11</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void B(CpuState* state, uint ins, uint pc) => state->R[15] = pc + 4 + (SignExtend(ins & 0x7FF, 11) << 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Svc(CpuState* state, uint ins, uint pc)
    {
        state->Exit = ExitReason.Svc;
        state->SvcImm = ins & 0xFF;
        state->SvcPc = pc;
        state->R[15] = pc + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bkpt(CpuState* state, uint ins, uint pc)
    {
        state->Exit = ExitReason.Breakpoint;
        state->SvcPc = pc;
        state->SvcImm = ins & 0xFF;
        state->R[15] = pc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Udf(CpuState* state, uint ins, uint pc)
    {
        state->Exit = ExitReason.Undefined;
        state->SvcPc = pc;
        state->SvcImm = ins & 0xFFFF;
        state->R[15] = pc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Yield(CpuState* state, uint ins, uint pc)
    {
        state->Exit = ExitReason.Yield;
        state->R[15] = pc + 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Nop(CpuState* state, uint ins, uint pc)
    {
    }
}
