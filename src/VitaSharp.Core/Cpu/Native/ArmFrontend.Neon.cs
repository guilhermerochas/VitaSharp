using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Advanced SIMD translation. Each decoder mirrors the interpreter handler (Neon.ThreeSame/Scalar/Shift/Misc/...)
/// field by field and emits one <see cref="IrOp.VecOp"/> on the register file (or plain IR for lane loads/stores);
/// encodings the architecture leaves UNDEFINED (odd Q registers, reserved sizes) and forms without an inline lowering
/// return null and stay interpreter callouts, so behaviour never diverges from the handler.
/// </summary>
public sealed unsafe partial class ArmFrontend
{
    static uint NRd(uint ins) => (Bit(ins, 22) << 4) | Bits(ins, 15, 12);
    static uint NRn(uint ins) => (Bit(ins, 7) << 4) | Bits(ins, 19, 16);
    static uint NRm(uint ins) => (Bit(ins, 5) << 4) | (ins & 0xF);

    uint _ins;
    Op _op;

    /// <summary>
    /// Float kinds whose rounded product (or fused result) can be +/- the smallest normal while the exact value was
    /// below it: ARM flushes those (FZ tininess is detected before rounding), so the emitter sends such lanes to the
    /// interpreter handler (<see cref="VecFlags.SlowPath"/>, instruction word in Imm3, op in Imm2 bits 16+).
    /// </summary>
    static bool NeedsTinyCheck(VecKind k) => k is VecKind.MulF or VecKind.MlaF or VecKind.MlsF or VecKind.FmaF or VecKind.FmsF
        or VecKind.MulFS or VecKind.MlaFS or VecKind.MlsFS;

    bool? Vec(VecKind k, bool q, uint size, uint vd, uint vn, uint vm, VecFlags f = 0, uint aux = 0)
    {
        if (!_supportsVec(k, size)) return null;
        if (_tinyCheck && NeedsTinyCheck(k)) { f |= VecFlags.SlowPath; aux |= (uint)_op << 16; }
        B.VecOp(k, q, size, vd * 8, vn * 8, vm * 8, f, aux, _ins);
        return false;
    }

    /// <summary>Neon.ThreeSame: three registers of the same length.</summary>
    bool? NeonThreeSame(in DecodedInst d)
    {
        uint ins = d.Raw, a = Bits(ins, 11, 8), size = Bits(ins, 21, 20);
        bool b = Bit(ins, 4) != 0, u = Bit(ins, 24) != 0, q = Bit(ins, 6) != 0;
        uint vd = NRd(ins), vn = NRn(ins), vm = NRm(ins);
        if (q && ((vd | vn | vm) & 1) != 0) return null; // odd Q registers: UNDEFINED, the handler decides
        var f = u ? VecFlags.Unsigned : 0;
        bool sz1 = (size & 2) != 0, s64 = size == 3;
        VecKind? k = a switch
        {
            0 => b ? VecKind.QaddI : s64 ? null : VecKind.HaddI,
            1 => b ? (VecKind)((u ? 4u : 0u) + size) : s64 ? null : VecKind.RhaddI,
            2 => b ? VecKind.QsubI : s64 ? null : VecKind.HsubI,
            3 => s64 ? null : b ? VecKind.CgeI : VecKind.CgtI,
            4 => !b ? VecKind.ShlRegI : null, // VSHL (register), every lane size (the emitter's SupportsVec decides)
            6 => s64 ? null : b ? VecKind.MinI : VecKind.MaxI,
            7 => s64 ? null : b ? VecKind.AbaI : VecKind.AbdI,
            8 => !b ? (u ? VecKind.SubI : VecKind.AddI) : s64 ? null : u ? VecKind.CeqI : VecKind.TstI,
            9 => s64 ? null : !b ? (u ? VecKind.MlsI : VecKind.MlaI) : u ? null : VecKind.MulI,
            10 => s64 || q ? null : b ? VecKind.PminI : VecKind.PmaxI,
            11 => !b ? (size == 1 ? (u ? VecKind.QrdmulhI : VecKind.QdmulhI) : null) : u || q || s64 ? null : VecKind.PaddI,
            _ => null,
        };
        if (a >= 12)
        {
            if ((size & 1) != 0) return null; // half precision: UNDEFINED
            k = a switch
            {
                12 => b && !u ? (sz1 ? VecKind.FmsF : VecKind.FmaF) : null,
                13 => (b, u) switch
                {
                    (false, false) => sz1 ? VecKind.SubF : VecKind.AddF,
                    (false, true) => sz1 ? VecKind.AbdF : q ? null : VecKind.PaddF,
                    (true, false) => sz1 ? VecKind.MlsF : VecKind.MlaF,
                    _ => sz1 ? null : VecKind.MulF,
                },
                14 => (b, u) switch
                {
                    (false, false) => sz1 ? null : VecKind.CeqF,
                    (false, true) => sz1 ? VecKind.CgtF : VecKind.CgeF,
                    (true, true) => sz1 ? VecKind.AcgtF : VecKind.AcgeF,
                    _ => null,
                },
                _ => (b, u) switch
                {
                    (false, false) => sz1 ? VecKind.MinF : VecKind.MaxF,
                    (false, true) => q ? null : sz1 ? VecKind.PminF : VecKind.PmaxF,
                    (true, false) => sz1 ? VecKind.RsqrtsF : VecKind.RecpsF,
                    _ => null,
                },
            };
        }
        if (k is not { } kind) return null;
        return Vec(kind, q, size, vd, vn, vm, f);
    }

    /// <summary>Neon.Scalar: two registers and a scalar (non-long forms).</summary>
    bool? NeonScalar(in DecodedInst d)
    {
        uint ins = d.Raw, a = Bits(ins, 11, 8), size = Bits(ins, 21, 20);
        bool q = Bit(ins, 24) != 0;
        uint vd = NRd(ins), vn = NRn(ins);
        if (q && ((vd | vn) & 1) != 0) return null;
        VecKind? k = a switch
        {
            0 => VecKind.MlaIS, 4 => VecKind.MlsIS, 8 => VecKind.MulIS,
            1 => VecKind.MlaFS, 5 => VecKind.MlsFS, 9 => VecKind.MulFS,
            _ => null,
        };
        if (k is not { } kind) return null;
        bool fp = a is 1 or 5 or 9;
        uint scalar;
        if (size == 2) scalar = (ins & 0xF) * 8 + Bit(ins, 5) * 4;
        else if (size == 1 && !fp) scalar = (ins & 7) * 8 + ((Bit(ins, 5) << 1) | Bit(ins, 3)) * 2;
        else return null;
        return Vec(kind, q, size, vd, vn, 0, 0, scalar);
    }

    /// <summary>Neon.Shift: two registers and a shift amount.</summary>
    bool? NeonShift(in DecodedInst d)
    {
        uint ins = d.Raw, a = Bits(ins, 11, 8), imm6 = Bits(ins, 21, 16);
        bool u = Bit(ins, 24) != 0, l = Bit(ins, 7) != 0, q = Bit(ins, 6) != 0;
        uint vd = NRd(ins), vm = NRm(ins);
        if (!l && imm6 < 8) return null;
        int esize = l ? 64 : imm6 >= 32 ? 32 : imm6 >= 16 ? 16 : 8;
        uint size = esize == 8 ? 0u : esize == 16 ? 1u : esize == 32 ? 2u : 3u;
        uint rshift = (uint)(l ? 64 - (int)imm6 : 2 * esize - (int)imm6);
        uint lshift = (uint)(l ? (int)imm6 : (int)imm6 - esize);
        var uf = u ? VecFlags.Unsigned : 0;
        bool oddQ = q && ((vd | vm) & 1) != 0;
        switch (a)
        {
            case 0: case 1: case 2: case 3:
                if (oddQ) return null;
                return Vec(VecKind.ShrImm, q, size, vd, 0, vm,
                    uf | ((a & 2) != 0 ? VecFlags.Round : 0) | ((a & 1) != 0 ? VecFlags.Accumulate : 0), rshift);
            case 4:
                if (oddQ || !u) return null;
                return Vec(VecKind.SriImm, q, size, vd, 0, vm, 0, rshift);
            case 5:
                if (oddQ) return null;
                return Vec(u ? VecKind.SliImm : VecKind.ShlImm, q, size, vd, 0, vm, 0, lshift);
            case 8: case 9:
                if (l || (vm & 1) != 0) return null;
                var rf = q ? VecFlags.Round : 0;
                if (a == 8 && !u) return Vec(VecKind.ShrnImm, false, size, vd, 0, vm, rf, rshift);
                if (esize == 32) return null; // 64 -> 32 saturating narrow: callout
                return a == 8 ? Vec(VecKind.QshrunImm, false, size, vd, 0, vm, rf, rshift) : Vec(VecKind.QshrnImm, false, size, vd, 0, vm, rf | uf, rshift);
            case 10:
                if (l || (vd & 1) != 0) return null;
                return Vec(VecKind.ShllImm, false, size, vd, 0, vm, uf, lshift);
            default:
                return null;
        }
    }

    /// <summary>Neon.Misc: two registers, miscellaneous.</summary>
    bool? NeonMisc(in DecodedInst d)
    {
        uint ins = d.Raw, a = Bits(ins, 17, 16), b = Bits(ins, 10, 6), size = Bits(ins, 19, 18);
        bool q = Bit(ins, 6) != 0;
        uint vd = NRd(ins), vm = NRm(ins), op = b >> 1;
        bool oddQ = q && ((vd | vm) & 1) != 0;
        switch (a)
        {
            case 0:
                if (oddQ) return null;
                if (op <= 2)
                {
                    uint group = op == 0 ? 8u : op == 1 ? 4u : 2u;
                    if ((1u << (int)size) >= group) return null;
                    return Vec(VecKind.Rev, q, size, vd, 0, vm, 0, group);
                }
                if (op is 4 or 5 or 0xC or 0xD)
                {
                    if (size == 3) return null;
                    return Vec(VecKind.Paddl, q, size, vd, 0, vm, ((op & 1) != 0 ? VecFlags.Unsigned : 0) | (op >= 0xC ? VecFlags.Accumulate : 0));
                }
                if (op == 8 && size != 3) return Vec(VecKind.Cls, q, size, vd, 0, vm);
                if (op == 9 && size != 3) return Vec(VecKind.Clz, q, size, vd, 0, vm);
                if (op == 0xA && size == 0) return Vec(VecKind.Cnt, q, 0, vd, 0, vm);
                if (op == 0xB) return Vec(VecKind.Mvn, q, size, vd, 0, vm);
                return null;
            case 1:
            {
                if (oddQ) return null;
                uint kind = op & 7;
                if ((op & 8) != 0)
                {
                    if (size != 2) return null;
                    return kind switch
                    {
                        <= 4 => Vec(VecKind.CmpZeroF, q, 2, vd, 0, vm, 0, kind),
                        6 => Vec(VecKind.AbsF, q, 2, vd, 0, vm),
                        7 => Vec(VecKind.NegF, q, 2, vd, 0, vm),
                        _ => null,
                    };
                }
                if (size == 3) return null;
                return kind switch
                {
                    <= 4 => Vec(VecKind.CmpZeroI, q, size, vd, 0, vm, 0, kind),
                    6 => Vec(VecKind.AbsI, q, size, vd, 0, vm),
                    7 => Vec(VecKind.NegI, q, size, vd, 0, vm),
                    _ => null,
                };
            }
            case 2:
                switch (op)
                {
                    case 0: return oddQ ? null : Vec(VecKind.Swp, q, size, vd, 0, vm);
                    case 1: case 2: case 3:
                        if (oddQ || size == 3) return null;
                        return Vec(op == 1 ? VecKind.Trn : op == 2 ? VecKind.Uzp : VecKind.Zip, q, size, vd, 0, vm);
                    case 4: case 5:
                        if (size == 3 || (vm & 1) != 0) return null;
                        // b = 8 VMOVN, 9 VQMOVUN (signed -> unsigned), 10 VQMOVN.S, 11 VQMOVN.U
                        if (b == 8) return Vec(VecKind.Movn, false, size, vd, 0, vm);
                        return Vec(VecKind.Qmovn, false, size, vd, 0, vm, b == 11 ? VecFlags.Unsigned : 0, b == 9 ? 1u : 0u);
                    case 6:
                        if (size == 3 || (vd & 1) != 0 || q) return null;
                        return Vec(VecKind.ShllImm, false, size, vd, 0, vm, VecFlags.Unsigned, 8u << (int)size);
                    case 0xC:
                        if (q || (vm & 1) != 0) return null;
                        return Vec(VecKind.CvtF2H, false, 1, vd, 0, vm);
                    case 0xE:
                        if (q || (vd & 1) != 0) return null;
                        return Vec(VecKind.CvtH2F, false, 1, vd, 0, vm);
                    default: return null;
                }
            default:
            {
                if (oddQ || size != 2) return null;
                if ((op & 0xC) == 0x8)
                {
                    bool fp = (op & 2) != 0, rs = (op & 1) != 0;
                    return Vec(fp ? (rs ? VecKind.RsqrteF : VecKind.RecpeF) : (rs ? VecKind.RsqrteU : VecKind.RecpeU), q, 2, vd, 0, vm);
                }
                if ((op & 0xC) != 0xC) return null;
                return Vec((op & 3) switch { 0 => VecKind.CvtS2F, 1 => VecKind.CvtU2F, 2 => VecKind.CvtF2S, _ => VecKind.CvtF2U }, q, 2, vd, 0, vm);
            }
        }
    }

    /// <summary>VEXT: byte extraction from the concatenation of two registers.</summary>
    bool? NeonVext(in DecodedInst d)
    {
        uint ins = d.Raw, imm = Bits(ins, 11, 8);
        bool q = Bit(ins, 6) != 0;
        uint vd = NRd(ins), vn = NRn(ins), vm = NRm(ins);
        if (q ? ((vd | vn | vm) & 1) != 0 : imm > 7) return null;
        return Vec(VecKind.Ext, q, 0, vd, vn, vm, 0, imm);
    }

    /// <summary>VTBL/VTBX.</summary>
    bool? NeonVtbl(in DecodedInst d)
    {
        uint ins = d.Raw, len = Bits(ins, 9, 8) + 1;
        bool tbx = Bit(ins, 6) != 0;
        return Vec(VecKind.Tbl, false, 0, NRd(ins), NRn(ins), NRm(ins), 0, len | (tbx ? 0x10u : 0));
    }

    /// <summary>VDUP (scalar): plain data movement on the register file.</summary>
    bool? NeonVdupScalar(in DecodedInst d)
    {
        uint ins = d.Raw, imm4 = Bits(ins, 19, 16), vd = NRd(ins), vm = NRm(ins);
        bool q = Bit(ins, 6) != 0;
        int rep;
        if ((imm4 & 1) != 0)
        {
            uint bi = imm4 >> 1;
            int w = B.LoadState(DOff(vm) + (bi & 4));
            rep = B.Mul(B.AndK(B.Shr(w, (int)((bi & 3) * 8)), 0xFF), C(0x01010101));
        }
        else if ((imm4 & 2) != 0)
        {
            uint hi = (imm4 >> 2) & 3, bo = hi * 2;
            int w = B.LoadState(DOff(vm) + (bo & 4));
            rep = B.Mul(B.AndK(B.Shr(w, (int)((bo & 2) * 8)), 0xFFFF), C(0x00010001));
        }
        else rep = B.LoadState(DOff(vm) + (imm4 >> 3) * 4);
        B.StoreState(DOff(vd), rep); B.StoreState(DOff(vd) + 4, rep);
        if (q) { uint d2 = (vd + 1) & 31; B.StoreState(DOff(d2), rep); B.StoreState(DOff(d2) + 4, rep); }
        return false;
    }

    /// <summary>VMOV/VMVN/VORR/VBIC (immediate): the expanded immediate is a compile-time constant.</summary>
    bool? NeonImm(in DecodedInst d)
    {
        uint ins = d.Raw, imm8 = (Bit(ins, 24) << 7) | (Bits(ins, 18, 16) << 4) | (ins & 0xF), cmode = Bits(ins, 11, 8);
        bool op = Bit(ins, 5) != 0, q = Bit(ins, 6) != 0;
        uint vd = NRd(ins) & 31;
        ulong imm = Neon.ExpandImm(op, cmode, imm8);
        bool orrBic = (cmode & 1) != 0 && cmode < 12;
        for (uint i = 0; i < (q ? 2u : 1u); i++)
        {
            uint off = DOff(vd + i); // like the handler: D[d + 1] past D31 lands in the guard area
            if (orrBic)
            {
                int lo = B.LoadState(off), hi = B.LoadState(off + 4);
                if (op) { lo = B.AndK(lo, ~(uint)imm); hi = B.AndK(hi, ~(uint)(imm >> 32)); }
                else { lo = B.Or(lo, C((uint)imm)); hi = B.Or(hi, C((uint)(imm >> 32))); }
                B.StoreState(off, lo); B.StoreState(off + 4, hi);
            }
            else
            {
                ulong v = op && cmode != 14 ? ~imm : imm;
                B.StoreState(off, C((uint)v)); B.StoreState(off + 4, C((uint)(v >> 32)));
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ element / structure loads and stores

    /// <summary>Neon.LoadStore: VLD1/VST1 multiple (contiguous), single lanes and all lanes; VLD2-4/VST2-4 multiple.</summary>
    bool? NeonLoadStore(in DecodedInst d)
    {
        uint ins = d.Raw, rn = Bits(ins, 19, 16), rm = ins & 0xF;
        if (rn == 15) return null;
        bool load = Bit(ins, 21) != 0;
        uint vd = NRd(ins);
        int baseVal = Reg(rn);
        uint transferred;
        if (Bit(ins, 23) == 0)
        {
            uint type = Bits(ins, 11, 8);
            int regs = type switch { 0x7 => 1, 0xA => 2, 0x6 => 3, 0x2 => 4, _ => 0 };
            if (regs == 0) return InterleavedLoadStore(load, type, Bits(ins, 7, 6), vd, baseVal, rn, rm);
            for (uint r = 0; r < regs; r++)
            {
                int a = B.AddK(baseVal, r * 8);
                if (load) B.LoadD(a, DOff((vd + r) & 31)); else B.StoreD(a, DOff((vd + r) & 31));
            }
            transferred = (uint)(8 * regs);
        }
        else if (Bits(ins, 11, 10) == 3)
        {
            // all lanes (Neon.AllLanes): one element per structure register, replicated
            if (!load) return null;
            int nStruct = (int)Bits(ins, 9, 8) + 1;
            uint size = Bits(ins, 7, 6);
            if (size == 3 && nStruct != 4) return null;
            uint eb = size == 3 ? 4u : 1u << (int)size;
            bool t = Bit(ins, 5) != 0;
            for (int k = 0; k < nStruct; k++)
            {
                int v = LoadElem(B.AddK(baseVal, (uint)k * eb), eb);
                int rep = eb switch { 1 => B.Mul(v, C(0x01010101)), 2 => B.Mul(v, C(0x00010001)), _ => v };
                uint r0 = nStruct == 1 ? vd & 31 : (vd + (uint)k * (t ? 2u : 1u)) & 31;
                B.StoreState(DOff(r0), rep); B.StoreState(DOff(r0) + 4, rep);
                if (nStruct == 1 && t) { uint r1 = (vd + 1) & 31; B.StoreState(DOff(r1), rep); B.StoreState(DOff(r1) + 4, rep); }
            }
            transferred = (uint)nStruct * eb;
        }
        else
        {
            // single lane (Neon.SingleLane)
            int nStruct = (int)Bits(ins, 9, 8) + 1;
            uint size = Bits(ins, 11, 10), ia = Bits(ins, 7, 4);
            uint eb = 1u << (int)size;
            uint index, inc;
            switch (size)
            {
                case 0: index = ia >> 1; inc = 1; break;
                case 1: index = ia >> 2; inc = (ia & 2) != 0 ? 2u : 1u; break;
                default: index = ia >> 3; inc = (ia & 4) != 0 ? 2u : 1u; break;
            }
            if (nStruct == 1) inc = 1;
            for (int k = 0; k < nStruct; k++)
            {
                uint off = DOff((vd + (uint)k * inc) & 31) + index * eb;
                int a = B.AddK(baseVal, (uint)k * eb);
                uint word = off & ~3u, shift = (off & 3) * 8;
                if (load)
                {
                    int v = LoadElem(a, eb);
                    if (eb == 4) B.StoreState(off, v);
                    else
                    {
                        uint mask = (eb == 1 ? 0xFFu : 0xFFFFu) << (int)shift;
                        int w = B.LoadState(word);
                        B.StoreState(word, B.Or(B.AndK(w, ~mask), shift == 0 ? v : B.Shl(v, (int)shift)));
                    }
                }
                else
                {
                    int w = B.LoadState(word);
                    int v = shift == 0 ? w : B.Shr(w, (int)shift);
                    B.Store(eb == 1 ? IrOp.Store8 : eb == 2 ? IrOp.Store16 : IrOp.Store32, a, v);
                }
            }
            transferred = (uint)nStruct * eb;
        }
        if (rm != 15) SetReg(rn, rm == 13 ? B.AddK(baseVal, transferred) : B.Add(baseVal, Reg(rm)));
        return false;
    }

    int LoadElem(int addr, uint eb) => B.Load(eb == 1 ? IrOp.Load8 : eb == 2 ? IrOp.Load16 : IrOp.Load32, addr);

    /// <summary>
    /// VLD2-4/VST2-4 (multiple structures, Neon.MultipleStructures/Interleave) in plain IR: element e of structure
    /// register k is at base + (e * n + k) * esize. Loads assemble each 32-bit word of the destination registers from its
    /// elements (the registers are distinct, so the final state equals the handler's element order); stores run in the
    /// handler's order, element by element, so overlapping stores land the same way.
    /// </summary>
    bool? InterleavedLoadStore(bool load, uint type, uint size, uint vd, int baseVal, uint rn, uint rm)
    {
        int nStruct, inc, passes = 1;
        switch (type)
        {
            case 0x8: nStruct = 2; inc = 1; break;
            case 0x9: nStruct = 2; inc = 2; break;
            case 0x3: nStruct = 2; inc = 2; passes = 2; break;   // two interleaved pairs: (d, d+2) then (d+1, d+3)
            case 0x4: nStruct = 3; inc = 1; break;
            case 0x5: nStruct = 3; inc = 2; break;
            case 0x0: nStruct = 4; inc = 1; break;
            case 0x1: nStruct = 4; inc = 2; break;
            default: return null;                                // UNDEFINED type: the handler raises it
        }
        if (size == 3) return null;                              // UNDEFINED for VLD2-4 (the handler moves 64-bit elements)
        uint eb = 1u << (int)size, perWord = 4 / eb;
        int elements = (int)(8 / eb);
        for (int p = 0; p < passes; p++)
        {
            uint d0 = vd + (uint)p, passBase = (uint)(p * nStruct * 8);
            uint Addr(int e, int k) => passBase + (uint)(e * nStruct + k) * eb;
            uint RegOff(int k) => DOff((d0 + (uint)(k * inc)) & 31);
            if (load)
            {
                for (int k = 0; k < nStruct; k++)
                    for (int w = 0; w < 2; w++)
                    {
                        int word = -1;
                        for (int j = 0; j < perWord; j++)
                        {
                            int v = LoadElem(B.AddK(baseVal, Addr(w * (int)perWord + j, k)), eb);
                            word = j == 0 ? v : B.Or(word, B.Shl(v, (int)(j * eb * 8)));
                        }
                        B.StoreState(RegOff(k) + (uint)w * 4, word);
                    }
            }
            else
            {
                var words = new int[nStruct * 2];
                Array.Fill(words, -1);
                for (int e = 0; e < elements; e++)
                    for (int k = 0; k < nStruct; k++)
                    {
                        int w = e / (int)perWord, j = e % (int)perWord;
                        ref int word = ref words[k * 2 + w];
                        if (word < 0) word = B.LoadState(RegOff(k) + (uint)w * 4);
                        int v = j == 0 ? word : B.Shr(word, (int)(j * eb * 8));
                        B.Store(eb == 1 ? IrOp.Store8 : eb == 2 ? IrOp.Store16 : IrOp.Store32, B.AddK(baseVal, Addr(e, k)), v);
                    }
            }
        }
        uint transferred = (uint)(passes * nStruct * 8);
        if (rm != 15) SetReg(rn, rm == 13 ? B.AddK(baseVal, transferred) : B.Add(baseVal, Reg(rm)));
        return false;
    }

    /// <summary>Neon.ThreeDiff: VMULL (signed, unsigned, polynomial), VMLAL, VMLSL. The other long/wide/narrow forms stay callouts.</summary>
    bool? NeonThreeDiff(in DecodedInst d)
    {
        uint ins = d.Raw, a = Bits(ins, 11, 8), size = Bits(ins, 21, 20);
        bool u = Bit(ins, 24) != 0;
        uint vd = NRd(ins), vn = NRn(ins), vm = NRm(ins);
        if ((vd & 1) != 0 || size == 3) return null; // Q destination: odd registers are UNDEFINED
        var uf = u ? VecFlags.Unsigned : 0;
        return a switch
        {
            12 => Vec(VecKind.MullI, false, size, vd, vn, vm, uf),
            8 => Vec(VecKind.MullI, false, size, vd, vn, vm, uf | VecFlags.Accumulate),
            10 => Vec(VecKind.MullI, false, size, vd, vn, vm, uf | VecFlags.Accumulate, 2),
            14 => u || size != 0 ? null : Vec(VecKind.MullI, false, 0, vd, vn, vm, 0, 1),
            _ => null,
        };
    }
}
