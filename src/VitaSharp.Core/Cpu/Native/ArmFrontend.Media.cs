using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// ARMv6/v7 media instructions in plain IR (no new IR ops, so every backend lowers them): halfword and dual multiplies
/// (SMULxy/SMLAxy/SMULWy/SMLAWy/SMLALxy, SMUAD/SMUSD/SMLAD/SMLSD/SMLALD/SMLSLD, SMMUL/SMMLA/SMMLS with and without
/// rounding), parallel add/subtract with the GE flags (S/Q/SH/U/UQ/UH ADD16/ASX/SAX/SUB16/ADD8/SUB8), SEL, the saturating
/// QADD family, SSAT/USAT(16), PKH, USAD8 and UMAAL. Each mirrors the interpreter core it replaces (A32.HalfMultiplyCore,
/// SignedMultiplyMediaCore, ParallelCore, SatAddSubCore, SatCore, Sat16Core, PkhCore, SelCore, Usad8Core), including
/// the cases the architecture leaves UNPREDICTABLE. Operands the handlers read as the raw R15 (Thumb operands, ARM
/// accumulators) use the cached R15, which holds exactly that value (the block address, or the next address on the
/// last instruction); PC destinations stay callouts.
/// </summary>
public sealed unsafe partial class ArmFrontend
{
    static readonly uint QOffset = (uint)StateLayout.Of(nameof(CpuState.Q));
    static readonly uint GeOffset = (uint)StateLayout.Of(nameof(CpuState.GE));

    /// <summary>CPSR.Q |= bit (0/1 temp).</summary>
    void OrQ(int bit)
    {
        if (B.IsConst(bit, 0u)) return;
        B.StoreState(QOffset, B.Or(B.LoadState(QOffset), bit));
    }

    int NotBit(int bit) => B.Xor(bit, C(1));

    /// <summary>Signed 16-bit half of v (top or bottom), sign-extended.</summary>
    int Half(int v, bool top) => top ? B.Sar(v, 16) : B.SExt16(v);

    /// <summary>Arm.SignedSat of an exact int32 value to <paramref name="bits"/> (1-32): (result, saturated 0/1).</summary>
    (int R, int Sat) SignedSatIr(int v, int bits)
    {
        if (bits >= 32) return (v, C(0));
        int sh = 32 - bits;
        int inRange = B.CmpEq(B.Sar(B.Shl(v, sh), sh), v);
        uint max = (1u << (bits - 1)) - 1;
        // out of range: the sign of v picks min (all ones ^ max = -2^(bits-1)) or max
        return (B.Select(inRange, v, B.Xor(B.Sar(v, 31), C(max))), NotBit(inRange));
    }

    /// <summary>Arm.UnsignedSat of an exact int32 value to <paramref name="bits"/> (0-31).</summary>
    (int R, int Sat) UnsignedSatIr(int v, int bits)
    {
        int inRange = B.CmpEq(B.Shr(v, bits), C(0));
        uint max = (uint)((1ul << bits) - 1);
        return (B.Select(inRange, v, B.AndNot(C(max), B.Sar(v, 31))), NotBit(inRange));
    }

    /// <summary>Arm.SatQ of the 32-bit result r of an addition/subtraction whose signed overflow flag is v.</summary>
    int SatOnOverflow(int r, int v) => B.Select(v, B.Xor(B.Sar(r, 31), C(0x80000000)), r);

    // ------------------------------------------------------------------ multiplies

    /// <summary>A32.HalfMultiplyCore: op 0 SMLAxy, 1 SMLAWy (x = 0) / SMULWy (x = 1), 2 SMLALxy, 3 SMULxy.</summary>
    void HalfMultiply(uint op, uint rd, uint ra, int vn, int vm, bool x, bool y)
    {
        switch (op)
        {
            case 0:
            {
                var f = B.AddC(B.Mul(Half(vn, x), Half(vm, y)), Reg(ra), -1);
                SetReg(rd, f.R);
                OrQ(f.V);
                break;
            }
            case 1:
            {
                var (lo, hi) = B.MulLong(vn, Half(vm, y), true);
                int p = B.Or(B.Shr(lo, 16), B.Shl(hi, 16)); // (n * m) >> 16, low 32 bits (the product fits in 48)
                if (!x)
                {
                    var f = B.AddC(p, Reg(ra), -1);
                    SetReg(rd, f.R);
                    OrQ(f.V);
                }
                else SetReg(rd, p);
                break;
            }
            case 2:
            {
                int p = B.Mul(Half(vn, x), Half(vm, y));
                var (l, h) = B.Add64(Reg(ra), Reg(rd), p, B.Sar(p, 31));
                SetReg(ra, l); SetReg(rd, h);
                break;
            }
            default:
                SetReg(rd, B.Mul(Half(vn, x), Half(vm, y)));
                break;
        }
    }

    /// <summary>
    /// A32.SignedMultiplyMediaCore, the dual and most-significant-word forms: op1 0 SMLAD/SMUAD (op2 &amp; 2 = 0) and
    /// SMLSD/SMUSD, 4 SMLALD/SMLSLD (rd = RdHi, ra = RdLo), 5 SMMLA/SMMUL (op2 &amp; 6 = 0) and SMMLS; op2 &amp; 1 = swap
    /// the halves of m (dual forms) or round (SMM forms). <paramref name="accumulate"/> = false for Ra = 15 (SMUAD/SMUSD/SMMUL).
    /// </summary>
    void SignedMultiplyMedia(uint op1, uint op2, uint rd, uint ra, bool accumulate, int vn, int vm)
    {
        bool swap = (op2 & 1) != 0;
        if (op1 == 5)
        {
            var (lo, hi) = B.MulLong(vn, vm, true);
            uint round = swap ? 0x80000000u : 0;
            int r;
            if ((op2 & 6) == 0) r = B.Add64(lo, hi, C(round), accumulate ? Reg(ra) : C(0)).Hi; // (a << 32) + p (+ 2^31)
            else r = B.Add64(B.Not(lo), B.Not(hi), C(round + 1), Reg(ra)).Hi;                  // (a << 32) - p (+ 2^31)
            SetReg(rd, r);
            return;
        }
        if (swap) vm = B.Ror(vm, 16);
        int p1 = B.Mul(B.SExt16(vn), B.SExt16(vm));
        int p2 = B.Mul(B.Sar(vn, 16), B.Sar(vm, 16));
        bool sub = (op2 & 2) != 0;
        if (op1 == 4)
        {
            var (l, h) = (Reg(ra), Reg(rd));
            if (sub)
            {
                int dif = B.Sub(p1, p2); // never overflows: |p1|, |p2| <= 2^30
                (l, h) = B.Add64(l, h, dif, B.Sar(dif, 31));
            }
            else
            {
                (l, h) = B.Add64(l, h, p1, B.Sar(p1, 31));
                (l, h) = B.Add64(l, h, p2, B.Sar(p2, 31));
            }
            SetReg(ra, l); SetReg(rd, h);
            return;
        }
        if (sub)
        {
            int dif = B.Sub(p1, p2);
            if (!accumulate) { SetReg(rd, dif); return; }
            var f = B.AddC(dif, Reg(ra), -1);
            SetReg(rd, f.R);
            OrQ(f.V);
            return;
        }
        // p1 + p2 (+ a) in 64 bits: Q when the sum is not an int32 (only the final result counts, like the handler)
        var (sl, sh) = B.Add64(p1, B.Sar(p1, 31), p2, B.Sar(p2, 31));
        if (accumulate) { int a = Reg(ra); (sl, sh) = B.Add64(sl, sh, a, B.Sar(a, 31)); }
        SetReg(rd, sl);
        OrQ(NotBit(B.CmpEq(sh, B.Sar(sl, 31))));
    }

    bool? A32HalfMultiply(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 22, 21);
        uint rd = Bits(ins, 19, 16), ra = Bits(ins, 15, 12), rm = Bits(ins, 11, 8), rn = ins & 0xF;
        bool x = Bit(ins, 5) != 0, y = Bit(ins, 6) != 0;
        if (rd == 15 || (op == 2 && ra == 15)) return null; // PC destinations (SMLALxy: RdLo = ra)
        HalfMultiply(op, rd, ra, Rg(rn, in d), Rg(rm, in d), x, y);
        return false;
    }

    bool? A32SignedMultiplyMedia(in DecodedInst d)
    {
        uint ins = d.Raw, op1 = Bits(ins, 22, 20), op2 = Bits(ins, 7, 5);
        uint rd = Bits(ins, 19, 16), ra = Bits(ins, 15, 12), rm = Bits(ins, 11, 8), rn = ins & 0xF;
        if (rd == 15) return null;
        int vn = Rg(rn, in d), vm = Rg(rm, in d);
        switch (op1)
        {
            case 0:
                SignedMultiplyMedia(0, op2, rd, ra, ra != 15, vn, vm);
                return false;
            case 1: SetReg(rd, B.SDiv(vn, vm)); return false;
            case 3: SetReg(rd, B.UDiv(vn, vm)); return false;
            case 4:
                if (ra == 15) return null; // RdLo = PC
                SignedMultiplyMedia(4, op2, rd, ra, true, vn, vm);
                return false;
            case 5:
                SignedMultiplyMedia(5, op2, rd, ra, (op2 & 6) != 0 || ra != 15, vn, vm);
                return false;
            default: return null; // no effect in the handler: keep it there
        }
    }

    bool? T32MulMedia(in DecodedInst d)
    {
        uint ins = d.Raw, op1 = Bits(ins, 22, 20), op2 = Bits(ins, 5, 4);
        uint rn = Bits(ins, 19, 16), ra = Bits(ins, 15, 12), rd = Bits(ins, 11, 8), rm = ins & 0xF;
        if (rd == 15) return null;
        int vn = Reg(rn), vm = Reg(rm); // raw registers, like the handler
        switch (op1)
        {
            case 0:
            {
                int prod = B.Mul(vn, vm);
                if (op2 == 0) SetReg(rd, ra == 15 ? prod : B.Add(prod, Reg(ra)));
                else SetReg(rd, B.Sub(Reg(ra), prod));
                return false;
            }
            case 1: HalfMultiply(ra == 15 ? 3u : 0u, rd, ra, vn, vm, Bit(ins, 5) != 0, Bit(ins, 4) != 0); return false;
            case 2: SignedMultiplyMedia(0, op2 & 1, rd, ra, ra != 15, vn, vm); return false;
            case 3: HalfMultiply(1, rd, ra, vn, vm, ra == 15, Bit(ins, 4) != 0); return false;
            case 4: SignedMultiplyMedia(0, 2 | (op2 & 1), rd, ra, ra != 15, vn, vm); return false;
            case 5: SignedMultiplyMedia(5, op2 & 1, rd, ra, ra != 15, vn, vm); return false;
            case 6:
                SignedMultiplyMedia(5, 6 | (op2 & 1), rd, ra, true, vn, vm);
                return false;
            default:
            {
                int sad = Usad8Ir(vn, vm);
                SetReg(rd, ra == 15 ? sad : B.Add(sad, Reg(ra)));
                return false;
            }
        }
    }

    /// <summary>T32.LongMul forms beyond SMULL/UMULL/SMLAL/UMLAL/SDIV/UDIV: SMLALxy, SMLALD, SMLSLD, UMAAL.</summary>
    bool? T32LongMulMedia(in DecodedInst d, uint op1, uint op2, uint rn, uint lo, uint hi, uint rm)
    {
        if (lo == 15 || hi == 15) return null;
        int vn = Reg(rn), vm = Reg(rm);
        switch (op1)
        {
            case 4:
                if ((op2 & 0xC) == 0x8) HalfMultiply(2, hi, lo, vn, vm, Bit(d.Raw, 5) != 0, Bit(d.Raw, 4) != 0);
                else SignedMultiplyMedia(4, op2 & 1, hi, lo, true, vn, vm);
                return false;
            case 5: SignedMultiplyMedia(4, 2 | (op2 & 1), hi, lo, true, vn, vm); return false;
            case 6:
            {
                var (pl, ph) = B.MulLong(vn, vm, false);
                var (l1, h1) = B.Add64(pl, ph, Reg(lo), C(0));
                var (l2, h2) = B.Add64(l1, h1, Reg(hi), C(0));
                SetReg(lo, l2); SetReg(hi, h2);
                return false;
            }
            default: return null;
        }
    }

    int Usad8Ir(int a, int b)
    {
        int sum = C(0);
        for (int i = 0; i < 32; i += 8)
        {
            int x = i == 24 ? B.Shr(a, 24) : B.ZExt8(B.Shr(a, i));
            int y = i == 24 ? B.Shr(b, 24) : B.ZExt8(B.Shr(b, i));
            int dif = B.Sub(x, y);
            int sign = B.Sar(dif, 31);
            sum = B.Add(sum, B.Sub(B.Xor(dif, sign), sign));
        }
        return sum;
    }

    bool? A32Usad8(in DecodedInst d)
    {
        uint ins = d.Raw, rd = Bits(ins, 19, 16), ra = Bits(ins, 15, 12), rm = Bits(ins, 11, 8), rn = ins & 0xF;
        if (rd == 15) return null;
        int sad = Usad8Ir(Rg(rn, in d), Rg(rm, in d));
        SetReg(rd, ra == 15 ? sad : B.Add(sad, Reg(ra)));
        return false;
    }

    // ------------------------------------------------------------------ parallel add/subtract, SEL

    /// <summary>
    /// A32.ParallelCore: op1 1 = S, 2 = Q, 3 = SH, 5 = U, 6 = UQ, 7 = UH (op1 &amp; 3 = 1 plain and sets GE, 2 saturating,
    /// else halving); op2 0 ADD16, 1 ASX, 2 SAX, 3 SUB16, 4 ADD8, 7 SUB8 (other values: SUB16, like the handler).
    /// </summary>
    int Parallel(uint op1, uint op2, int a, int b)
    {
        bool signed = op1 < 4;
        uint kind = op1 & 3;
        int r = C(0), ge = C(0);
        if (op2 == 4 || op2 == 7)
        {
            bool add = op2 == 4;
            for (int i = 0; i < 4; i++)
            {
                int sh = 8 * i;
                int x = Lane8(a, sh, signed), y = Lane8(b, sh, signed);
                int v = add ? B.Add(x, y) : B.Sub(x, y);
                int res;
                if (kind == 1)
                {
                    res = B.AndK(v, 0xFF);
                    int g = signed || !add ? NotBit(B.Shr(v, 31)) : B.Bit(v, 8);
                    ge = B.Or(ge, i == 0 ? g : B.Shl(g, i));
                }
                else if (kind == 2) res = B.AndK((signed ? SignedSatIr(v, 8) : UnsignedSatIr(v, 8)).R, 0xFF);
                else res = B.AndK(B.Sar(v, 1), 0xFF);
                r = B.Or(r, i == 0 ? res : B.Shl(res, sh));
            }
        }
        else
        {
            int al = signed ? B.SExt16(a) : B.ZExt16(a), ah = signed ? B.Sar(a, 16) : B.Shr(a, 16);
            int bl = signed ? B.SExt16(b) : B.ZExt16(b), bh = signed ? B.Sar(b, 16) : B.Shr(b, 16);
            int lo, hi;
            bool loAdd, hiAdd;
            switch (op2)
            {
                case 0: lo = B.Add(al, bl); hi = B.Add(ah, bh); loAdd = hiAdd = true; break;   // ADD16
                case 1: lo = B.Sub(al, bh); hi = B.Add(ah, bl); loAdd = false; hiAdd = true; break; // ASX
                case 2: lo = B.Add(al, bh); hi = B.Sub(ah, bl); loAdd = true; hiAdd = false; break; // SAX
                default: lo = B.Sub(al, bl); hi = B.Sub(ah, bh); loAdd = hiAdd = false; break; // SUB16
            }
            int rl, rh;
            if (kind == 1)
            {
                rl = B.ZExt16(lo); rh = B.ZExt16(hi);
                int gl = signed || !loAdd ? NotBit(B.Shr(lo, 31)) : B.Bit(lo, 16);
                int gh = signed || !hiAdd ? NotBit(B.Shr(hi, 31)) : B.Bit(hi, 16);
                ge = B.Or(B.Mul(gl, C(3)), B.Mul(gh, C(0xC)));
            }
            else if (kind == 2)
            {
                rl = B.ZExt16((signed ? SignedSatIr(lo, 16) : UnsignedSatIr(lo, 16)).R);
                rh = B.ZExt16((signed ? SignedSatIr(hi, 16) : UnsignedSatIr(hi, 16)).R);
            }
            else { rl = B.ZExt16(B.Sar(lo, 1)); rh = B.ZExt16(B.Sar(hi, 1)); }
            r = B.Or(rl, B.Shl(rh, 16));
        }
        if (kind == 1) B.StoreState(GeOffset, ge);
        return r;
    }

    int Lane8(int v, int sh, bool signed)
    {
        if (sh == 24) return signed ? B.Sar(v, 24) : B.Shr(v, 24);
        int s = sh == 0 ? v : B.Shr(v, sh);
        return signed ? B.SExt8(s) : B.ZExt8(s);
    }

    bool? A32Parallel(in DecodedInst d)
    {
        uint ins = d.Raw, op1 = Bits(ins, 22, 20), op2 = Bits(ins, 7, 5);
        uint rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        if (rd == 15) return null;
        SetReg(rd, Parallel(op1, op2, Rg(rn, in d), Rg(rm, in d)));
        return false;
    }

    bool? T32Parallel(in DecodedInst d)
    {
        uint ins = d.Raw, op1 = Bits(ins, 22, 20), rn = Bits(ins, 19, 16), rd = Bits(ins, 11, 8), rm = ins & 0xF;
        if (rd == 15) return null;
        bool unsigned = Bit(ins, 6) != 0;
        uint kind = Bits(ins, 5, 4);
        uint armOp2 = op1 switch { 1 => 0u, 2 => 1u, 6 => 2u, 5 => 3u, 0 => 4u, _ => 7u };
        uint armOp1 = (unsigned ? 4u : 0u) + (kind == 0 ? 1u : kind == 1 ? 2u : 3u);
        SetReg(rd, Parallel(armOp1, armOp2, Reg(rn), Reg(rm)));
        return false;
    }

    /// <summary>A32.SelCore: bytes of a where GE[i] is set, else of b.</summary>
    int Sel(int a, int b)
    {
        // spread GE[3:0] to bits 0, 8, 16, 24 (no carries: the shifted copies never overlap), then to whole bytes
        int ge = B.AndK(B.LoadState(GeOffset), 0xF);
        int mask = B.Mul(B.AndK(B.Mul(ge, C(0x00204081)), 0x01010101), C(0xFF));
        return B.Or(B.And(a, mask), B.AndNot(b, mask));
    }

    bool? A32Sel(in DecodedInst d)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        if (rd == 15) return null;
        SetReg(rd, Sel(Reg(rn), Reg(rm))); // the handler reads the raw registers
        return false;
    }

    // ------------------------------------------------------------------ saturation, packing

    /// <summary>A32.SatAddSubCore: op bit 0 = subtract, bit 1 = double n first (QADD/QSUB/QDADD/QDSUB).</summary>
    int SatAddSub(uint op, int vm, int vn)
    {
        int b = vn;
        if ((op & 2) != 0)
        {
            var dbl = B.AddC(vn, vn, -1);
            b = SatOnOverflow(dbl.R, dbl.V);
            OrQ(dbl.V);
        }
        var f = (op & 1) == 0 ? B.AddC(vm, b, -1) : B.SubC(vm, b, -1);
        OrQ(f.V);
        return SatOnOverflow(f.R, f.V);
    }

    bool? A32SatAddSub(in DecodedInst d)
    {
        uint ins = d.Raw, op = Bits(ins, 22, 21), rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        if (rd == 15) return null;
        SetReg(rd, SatAddSub(op, Rg(rm, in d), Rg(rn, in d)));
        return false;
    }

    /// <summary>A32.SatCore: SSAT/USAT of the shifted operand (sh = 0 LSL, 1 ASR).</summary>
    int SatIr(bool unsigned, uint satImm, int v, uint sh, uint imm5)
    {
        DecodeImmShift(sh << 1, imm5, out int t, out int n);
        int operand = n == 0 ? v : t == LSL ? B.Shl(v, n) : B.Sar(v, Math.Min(n, 31));
        var (r, sat) = unsigned ? UnsignedSatIr(operand, (int)satImm) : SignedSatIr(operand, (int)satImm + 1);
        OrQ(sat);
        return r;
    }

    /// <summary>A32.Sat16Core: SSAT16/USAT16 of both halves.</summary>
    int Sat16Ir(bool unsigned, uint satImm, int v)
    {
        int lo = B.SExt16(v), hi = B.Sar(v, 16);
        var (rl, s1) = unsigned ? UnsignedSatIr(lo, (int)satImm) : SignedSatIr(lo, (int)satImm + 1);
        var (rh, s2) = unsigned ? UnsignedSatIr(hi, (int)satImm) : SignedSatIr(hi, (int)satImm + 1);
        OrQ(B.Or(s1, s2));
        return B.Or(B.ZExt16(rl), B.Shl(rh, 16));
    }

    bool? A32Sat(in DecodedInst d)
    {
        uint ins = d.Raw, rd = Bits(ins, 15, 12), rn = ins & 0xF;
        if (rd == 15) return null;
        SetReg(rd, SatIr(Bit(ins, 22) != 0, Bits(ins, 20, 16), Reg(rn), Bit(ins, 6), Bits(ins, 11, 7)));
        return false;
    }

    bool? A32Sat16(in DecodedInst d)
    {
        uint ins = d.Raw, rd = Bits(ins, 15, 12), rn = ins & 0xF;
        if (rd == 15) return null;
        SetReg(rd, Sat16Ir(Bit(ins, 22) != 0, Bits(ins, 19, 16), Reg(rn)));
        return false;
    }

    /// <summary>A32.PkhCore: PKHTB (tb) = top of n, bottom of ASR(m); PKHBT = bottom of n, top of LSL(m).</summary>
    int Pkh(int vn, int vm, bool tb, uint imm5)
    {
        if (tb)
        {
            int sh = B.Sar(vm, imm5 == 0 ? 31 : (int)imm5);
            return B.Or(B.AndK(vn, 0xFFFF0000), B.ZExt16(sh));
        }
        return B.Or(B.AndK(B.Shl(vm, (int)imm5), 0xFFFF0000), B.ZExt16(vn));
    }

    bool? A32Pkh(in DecodedInst d)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), rd = Bits(ins, 15, 12), rm = ins & 0xF;
        if (rd == 15) return null;
        SetReg(rd, Pkh(Reg(rn), Reg(rm), Bit(ins, 6) != 0, Bits(ins, 11, 7)));
        return false;
    }

    /// <summary>T32 SSAT/USAT/SSAT16/USAT16 (data processing, plain immediate, op 0x10/0x12/0x18/0x1A).</summary>
    bool? T32Sat(in DecodedInst d, uint op, uint rn, uint rd, uint imm3, uint imm2)
    {
        if (rd == 15) return null;
        uint ins = d.Raw;
        bool unsigned = op >= 0x18;
        if ((op & 2) != 0 && imm3 == 0 && imm2 == 0) SetReg(rd, Sat16Ir(unsigned, ins & 0xF, Reg(rn)));
        else SetReg(rd, SatIr(unsigned, ins & 0x1F, Reg(rn), Bit(ins, 21), (imm3 << 2) | imm2));
        return false;
    }
}
