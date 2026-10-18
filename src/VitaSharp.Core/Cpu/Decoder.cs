namespace VitaSharp.Core.Cpu;

[Flags]
public enum InstFlags : byte
{
    None = 0,
    /// <summary>Instruction may write PC or leave the CPU loop: it terminates a translation block.</summary>
    EndsBlock = 1,
    /// <summary>Conditional (needs a runtime condition check).</summary>
    Conditional = 2,
}

public struct DecodedInst
{
    public Op Op;
    public byte Cond;
    public byte Size;
    public InstFlags Flags;
    public uint Raw;
    public uint Address;

    public readonly bool EndsBlock => (Flags & InstFlags.EndsBlock) != 0;
    public override readonly string ToString() => $"{Address:X8}: {Raw:X8} {Op} cond={Cond}";
}

/// <summary>A32/T16/T32 decoders producing handler ids. VFP/NEON encodings are routed to <see cref="SimdDecoder"/>.</summary>
public static class Decoder
{
    static uint B(uint v, int n) => (v >> n) & 1;
    static uint Bs(uint v, int hi, int lo) => (v >> lo) & (uint)((1L << (hi - lo + 1)) - 1);

    // ================================================================ ARM

    public static DecodedInst DecodeArm(uint ins, uint addr)
    {
        var d = new DecodedInst { Raw = ins, Address = addr, Size = 4, Cond = (byte)(ins >> 28) };
        bool writesPc;
        d.Op = DecodeArmOp(ins, out writesPc);
        if (writesPc) d.Flags |= InstFlags.EndsBlock;
        if (d.Cond == 0xF) d.Cond = 14;
        if (d.Cond != 14) d.Flags |= InstFlags.Conditional;
        return d;
    }

    static Op DecodeArmOp(uint ins, out bool writesPc)
    {
        writesPc = false;
        uint cond = ins >> 28;
        uint rd = Bs(ins, 15, 12);

        if (cond == 0xF) return DecodeArmUnconditional(ins, out writesPc);

        switch (Bs(ins, 27, 25))
        {
            case 0:
            {
                bool b4 = B(ins, 4) != 0, b7 = B(ins, 7) != 0;
                if (b4 && b7)
                {
                    if (Bs(ins, 6, 5) == 0)
                    {
                        if (B(ins, 24) == 0)
                        {
                            return Op.A32_Multiply;
                        }
                        // synchronization primitives
                        if (B(ins, 23) == 0) return Op.A32_Swp;
                        return Op.A32_Exclusive;
                    }
                    // extra load/store
                    if (B(ins, 20) != 0 && rd == 15) writesPc = true;
                    return Op.A32_LdStExtra;
                }
                if ((ins & 0x0190_0000) == 0x0100_0000)
                {
                    // miscellaneous / halfword multiply (op 10xx0)
                    if (b7 && !b4) return Op.A32_HalfMultiply;
                    switch (Bs(ins, 6, 4))
                    {
                        case 0:
                            return B(ins, 21) != 0 ? Op.A32_MsrReg : Op.A32_Mrs;
                        case 1:
                            if (Bs(ins, 22, 21) == 1) { writesPc = true; return Op.A32_Bx; }
                            if (Bs(ins, 22, 21) == 3) return Op.A32_Clz;
                            break;
                        case 2:
                            if (Bs(ins, 22, 21) == 1) { writesPc = true; return Op.A32_Bx; } // BXJ
                            break;
                        case 3:
                            if (Bs(ins, 22, 21) == 1) { writesPc = true; return Op.A32_BlxReg; }
                            break;
                        case 5: return Op.A32_SatAddSub;
                        case 7:
                            writesPc = true;
                            return Bs(ins, 22, 21) == 1 ? Op.A32_Bkpt : Op.A32_Udf;
                    }
                    writesPc = true;
                    return Op.A32_Udf;
                }
                if (rd == 15 && Bs(ins, 24, 23) != 2) writesPc = true;
                return b4 ? Op.A32_DPRegReg : Op.A32_DPRegImm;
            }
            case 1:
            {
                uint op = Bs(ins, 24, 20);
                if (op == 0x10) return Op.A32_MovW;
                if (op == 0x14) return Op.A32_MovT;
                if (op == 0x12 || op == 0x16)
                {
                    if (Bs(ins, 19, 16) == 0 && op == 0x12)
                    {
                        switch (ins & 0xFF)
                        {
                            case 1: writesPc = true; return Op.A32_Yield;
                            case 2: case 3: writesPc = true; return Op.A32_Yield; // WFE / WFI
                            default: return Op.A32_Nop;
                        }
                    }
                    return Op.A32_MsrImm;
                }
                if (rd == 15 && Bs(ins, 24, 23) != 2) writesPc = true;
                return Op.A32_DPImm;
            }
            case 2:
                if (B(ins, 20) != 0 && rd == 15) writesPc = true;
                return Op.A32_LdStImm;
            case 3:
                if (B(ins, 4) == 0)
                {
                    if (B(ins, 20) != 0 && rd == 15) writesPc = true;
                    return Op.A32_LdStReg;
                }
                return DecodeArmMedia(ins, out writesPc);
            case 4:
                if (B(ins, 20) != 0 && B(ins, 15) != 0) writesPc = true;
                return Op.A32_LdmStm;
            case 5:
                writesPc = true;
                return B(ins, 24) != 0 ? Op.A32_Bl : Op.A32_B;
            default:
            {
                if (Bs(ins, 27, 24) == 0xF) { writesPc = true; return Op.A32_Svc; }
                uint coproc = Bs(ins, 11, 8);
                if ((coproc & 0xE) == 0xA) return SimdDecoder.DecodeVfp(ins, out writesPc);
                if (coproc == 15 && Bs(ins, 27, 24) == 0xE && B(ins, 4) != 0)
                    return B(ins, 20) != 0 ? Op.A32_Mrc : Op.A32_Mcr;
                writesPc = true;
                return Op.A32_Udf;
            }
        }
    }

    static Op DecodeArmMedia(uint ins, out bool writesPc)
    {
        writesPc = false;
        uint op1 = Bs(ins, 24, 20), op2 = Bs(ins, 7, 5);
        uint rn = ins & 0xF;
        switch (op1 >> 3)
        {
            case 0: // parallel add/sub
                if ((op1 & 3) != 0 && op2 != 5 && op2 != 6) return Op.A32_Parallel;
                break;
            case 1:
                switch (op1)
                {
                    case 0x08:
                        if ((op2 & 1) == 0) return Op.A32_Pkh;
                        if (op2 == 3) return Op.A32_Extend;
                        if (op2 == 5) return Op.A32_Sel;
                        break;
                    case 0x0A:
                        if ((op2 & 1) == 0) return Op.A32_Sat;
                        if (op2 == 1) return Op.A32_Sat16;
                        if (op2 == 3) return Op.A32_Extend;
                        break;
                    case 0x0B:
                        if ((op2 & 1) == 0) return Op.A32_Sat;
                        if (op2 == 1 || op2 == 5) return Op.A32_Reverse;
                        if (op2 == 3) return Op.A32_Extend;
                        break;
                    case 0x0C:
                        if (op2 == 3) return Op.A32_Extend;
                        break;
                    case 0x0E:
                        if ((op2 & 1) == 0) return Op.A32_Sat;
                        if (op2 == 1) return Op.A32_Sat16;
                        if (op2 == 3) return Op.A32_Extend;
                        break;
                    case 0x0F:
                        if ((op2 & 1) == 0) return Op.A32_Sat;
                        if (op2 == 1 || op2 == 5) return Op.A32_Reverse;
                        if (op2 == 3) return Op.A32_Extend;
                        break;
                }
                break;
            case 2:
                return Op.A32_SignedMultiplyMedia;
            case 3:
                if (op1 == 0x18 && op2 == 0) return Op.A32_Usad8;
                if ((op1 & 0x1E) == 0x1A && (op2 & 3) == 2) return Op.A32_Bfx;
                if ((op1 & 0x1E) == 0x1E && (op2 & 3) == 2) return Op.A32_Bfx;
                if ((op1 & 0x1E) == 0x1C && (op2 & 3) == 0) return Op.A32_Bfi;
                break;
        }
        writesPc = true;
        return Op.A32_Udf;
    }

    static Op DecodeArmUnconditional(uint ins, out bool writesPc)
    {
        writesPc = false;
        if ((ins & 0x0E00_0000) == 0x0A00_0000) { writesPc = true; return Op.A32_BlxImm; }
        if ((ins & 0x0E00_0000) == 0x0200_0000) return SimdDecoder.DecodeNeonData(ins, false);
        if ((ins & 0x0F10_0000) == 0x0400_0000) return SimdDecoder.DecodeNeonLoadStore(ins);
        if ((ins & 0x0FFF_FFF0) == 0x057F_F010) return Op.A32_Clrex;
        if ((ins & 0x0FFF_FF00) == 0x057F_F000) return Op.A32_Barrier; // DSB/DMB/ISB
        if ((ins & 0x0D70_0000) == 0x0550_0000 || (ins & 0x0D70_0000) == 0x0510_0000 ||
            (ins & 0x0F70_0000) == 0x0450_0000 || (ins & 0x0F70_0000) == 0x0650_0000 || (ins & 0x0F70_0000) == 0x0610_0000)
            return Op.A32_Nop; // PLD/PLDW/PLI
        if ((ins & 0x0FF0_0000) == 0x0100_0000) return Op.A32_Nop; // CPS/SETEND
        if ((ins & 0x0F70_0000) == 0x0410_0000) return Op.A32_Nop; // unallocated memory hint
        writesPc = true;
        return Op.A32_Udf;
    }

    // ================================================================ Thumb

    public static bool IsThumb32(ushort hw1) => (hw1 >> 11) is 0x1D or 0x1E or 0x1F;

    public static DecodedInst DecodeThumb16(uint hw, uint addr, bool inIt, byte itCond)
    {
        var d = new DecodedInst { Address = addr, Size = 2, Cond = inIt ? itCond : (byte)14 };
        d.Raw = hw | (inIt ? 0x8000_0000u : 0);
        d.Op = DecodeT16Op(hw, out bool writesPc, ref d.Cond);
        if (writesPc) d.Flags |= InstFlags.EndsBlock;
        if (d.Cond != 14) d.Flags |= InstFlags.Conditional;
        return d;
    }

    static Op DecodeT16Op(uint hw, out bool writesPc, ref byte cond)
    {
        writesPc = false;
        switch (hw >> 10)
        {
            case <= 0x05: return Op.T16_ShiftImm;               // 000xx (except 00011)
            case 0x06: case 0x07: return Op.T16_AddSub3;        // 00011
            case >= 0x08 and <= 0x0F: return Op.T16_Imm8;
            case 0x10: return Op.T16_DataProc;
            case 0x11:
            {
                uint op = Bs(hw, 9, 8);
                uint rdn = (B(hw, 7) << 3) | (hw & 7);
                switch (op)
                {
                    case 0: if (rdn == 15) writesPc = true; return Op.T16_AddHi;
                    case 1: return Op.T16_CmpHi;
                    case 2: if (rdn == 15) writesPc = true; return Op.T16_MovHi;
                    default: writesPc = true; return Op.T16_BxBlx;
                }
            }
            case 0x12: case 0x13: return Op.T16_LdrLit;
        }
        switch (hw >> 12)
        {
            case 0x5: return Op.T16_LdStReg;
            case 0x6: case 0x7: return Op.T16_LdStImm;
            case 0x8: return Op.T16_LdStH;
            case 0x9: return Op.T16_LdStSp;
            case 0xA: return B(hw, 11) != 0 ? Op.T16_AddSpImm : Op.T16_Adr;
            case 0xB: return DecodeT16Misc(hw, out writesPc);
            case 0xC:
                return B(hw, 11) != 0 ? Op.T16_Ldm : Op.T16_Stm;
            case 0xD:
            {
                uint c = Bs(hw, 11, 8);
                writesPc = true;
                if (c == 0xE) return Op.T16_Udf;
                if (c == 0xF) return Op.T16_Svc;
                cond = (byte)c;
                return Op.T16_BCond;
            }
            case 0xE:
                writesPc = true;
                return Op.T16_B;
        }
        writesPc = true;
        return Op.T16_Udf;
    }

    static Op DecodeT16Misc(uint hw, out bool writesPc)
    {
        writesPc = false;
        uint op = Bs(hw, 11, 5);
        if ((op & 0x78) == 0x00) return Op.T16_AdjSp;                  // 1011 0000 S imm7
        if ((op & 0x78) == 0x10) return Op.T16_Extend;              // 001000x
        if ((op & 0x28) == 0x08) { writesPc = true; return Op.T16_Cbz; }  // 1011 o0i1
        if ((op & 0x70) == 0x20) return Op.T16_Push;                // 010xxxx
        if (op == 0x32 || op == 0x33) return Op.T16_Nop;            // SETEND / CPS
        if ((op & 0x78) == 0x50) return Op.T16_Rev;                 // 101000x / 101001x / 101011x
        if ((op & 0x70) == 0x60) { if (B(hw, 8) != 0) writesPc = true; return Op.T16_Pop; }
        if ((op & 0x78) == 0x70) { writesPc = true; return Op.T16_Bkpt; }
        if ((op & 0x78) == 0x78)
        {
            if ((hw & 0xF) != 0) return Op.Thumb_It;
            uint hint = Bs(hw, 7, 4);
            if (hint is 1 or 2 or 3) { writesPc = true; return Op.T16_Yield; }
            return Op.T16_Nop;
        }
        writesPc = true;
        return Op.T16_Udf;
    }

    public static DecodedInst DecodeThumb32(uint ins, uint addr, bool inIt, byte itCond)
    {
        var d = new DecodedInst { Raw = ins, Address = addr, Size = 4, Cond = inIt ? itCond : (byte)14 };
        d.Op = DecodeT32Op(ins, ref d.Raw, out bool writesPc, ref d.Cond);
        if (writesPc) d.Flags |= InstFlags.EndsBlock;
        if (d.Cond != 14) d.Flags |= InstFlags.Conditional;
        return d;
    }

    /// <summary>Decodes a 32-bit Thumb instruction. NEON encodings are rewritten into their ARM form in <paramref name="raw"/>.</summary>
    static Op DecodeT32Op(uint ins, ref uint raw, out bool writesPc, ref byte cond)
    {
        writesPc = false;
        uint hw1 = ins >> 16, hw2 = ins & 0xFFFF;
        uint op1 = Bs(hw1, 12, 11), op2 = Bs(hw1, 10, 4);
        uint rn = hw1 & 0xF;
        switch (op1)
        {
            case 1:
                if ((op2 & 0x64) == 0x00)
                {
                    // load/store multiple
                    if (B(hw1, 4) != 0 && B(hw2, 15) != 0) writesPc = true;
                    uint op = Bs(hw1, 8, 7);
                    if (op == 0 || op == 3) { writesPc = true; return Op.T32_Udf; } // SRS/RFE
                    return Op.T32_LdmStm;
                }
                if ((op2 & 0x64) == 0x04)
                {
                    uint o1 = Bs(hw1, 8, 7), o2 = Bs(hw1, 5, 4);
                    if (o1 == 0 && o2 <= 1) return Op.T32_ExclusiveWord;
                    if (o1 == 1 && o2 == 0) return Op.T32_ExclusiveOther;
                    if (o1 == 1 && o2 == 1)
                    {
                        uint op3 = Bs(hw2, 7, 4);
                        if (op3 <= 1) { writesPc = true; return Op.T32_Tbb; }
                        return Op.T32_ExclusiveOther;
                    }
                    return Op.T32_LdStDual;
                }
                if ((op2 & 0x60) == 0x20) return Op.T32_DPShiftReg;
                return SimdDecoder.DecodeThumbCoproc(ref raw, out writesPc);
            case 2:
                if (B(hw2, 15) == 0)
                    return B(hw1, 9) == 0 ? Op.T32_DPModImm : Op.T32_DPPlainImm;
                return DecodeT32Branch(ins, out writesPc, ref cond);
            default:
            {
                if ((op2 & 0x71) == 0x00) return Op.T32_LdSt;                          // store single
                if ((op2 & 0x67) == 0x01 || (op2 & 0x67) == 0x03 || (op2 & 0x67) == 0x05)
                {
                    if (Bs(hw2, 15, 12) == 15 && (op2 & 0x67) == 0x05) writesPc = true;
                    return Op.T32_LdSt;                                                 // loads
                }
                if ((op2 & 0x71) == 0x10)
                {
                    raw = ThumbNeonLsToArm(ins);
                    return SimdDecoder.DecodeNeonLoadStore(raw);
                }
                if ((op2 & 0x70) == 0x20)
                {
                    uint dop1 = Bs(hw1, 7, 4), dop2 = Bs(hw2, 7, 4);
                    if ((dop1 & 0x8) == 0 && dop2 == 0) return Op.T32_ShiftReg;
                    if ((dop1 & 0x8) == 0 && (dop2 & 0x8) != 0) return Op.T32_Extend;
                    if ((dop1 & 0x8) != 0 && (dop2 & 0xC) == 0) return Op.T32_Parallel;
                    if ((dop1 & 0x8) != 0 && (dop2 & 0xC) == 4) return Op.T32_Parallel;
                    if ((dop1 & 0xC) == 0x8 && (dop2 & 0xC) == 8) return Op.T32_Misc;
                    writesPc = true;
                    return Op.T32_Udf;
                }
                if ((op2 & 0x78) == 0x30) return Op.T32_Mul;
                if ((op2 & 0x78) == 0x38) return Op.T32_LongMul;
                if ((op2 & 0x40) != 0) return SimdDecoder.DecodeThumbCoproc(ref raw, out writesPc);
                writesPc = true;
                return Op.T32_Udf;
            }
        }
    }

    static Op DecodeT32Branch(uint ins, out bool writesPc, ref byte cond)
    {
        writesPc = true;
        uint hw1 = ins >> 16, hw2 = ins & 0xFFFF;
        uint op = Bs(hw1, 10, 4), op1 = Bs(hw2, 14, 12);
        if ((op1 & 5) == 0)
        {
            if ((op & 0x38) != 0x38)
            {
                cond = (byte)Bs(hw1, 9, 6);
                return Op.T32_BCondW;
            }
            writesPc = false;
            switch (op)
            {
                case 0x38: case 0x39: return Op.T32_Msr;
                case 0x3A:
                {
                    uint hint = hw2 & 0xFF;
                    if (Bs(hw2, 10, 8) != 0) return Op.T32_Nop; // CPS
                    if (hint is 1 or 2 or 3) { writesPc = true; return Op.T32_Yield; }
                    return Op.T32_Nop;
                }
                case 0x3B:
                {
                    uint o = Bs(hw2, 7, 4);
                    if (o == 2) return Op.T32_Clrex;
                    return Op.T32_Barrier;
                }
                case 0x3C: case 0x3D: writesPc = true; return Op.T32_BxjOrSubsPcLr;
                case 0x3E: case 0x3F: return Op.T32_Mrs;
            }
            writesPc = true;
            return Op.T32_Udf;
        }
        if ((op1 & 5) == 1) return Op.T32_BW;
        if ((op1 & 5) == 4) return Op.T32_BlxImm;
        return Op.T32_Bl;
    }

    /// <summary>Thumb NEON element load/store 1111 1001 → ARM 1111 0100.</summary>
    internal static uint ThumbNeonLsToArm(uint ins) => (ins & 0x00FF_FFFF) | 0xF400_0000;
}
