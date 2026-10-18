using System.Numerics;

namespace VitaSharp.Core.Gxm.Shader;

public sealed partial class UsseTranslator
{
    enum VOp { VMul, VAdd, VFrc, VDsx, VDsy, VMin, VMax, VDp, VRcp, VRsq, VLog, VExp, VSub, And, Or, Xor, Shl, Shr, Asr, Rol, ISub, Invalid }

    static string Splat(float f, int n) => n == 1 ? F(f) : $"vec{n}({F(f)})";

    public bool Translate(UsseInst ins)
    {
        switch (ins.Op)
        {
            case UsseOp.VMAD: Vmad(ins); return true;
            case UsseOp.VMAD2: Vmad2(ins); return true;
            case UsseOp.VDP: Vdp(ins); return true;
            case UsseOp.V32NMAD:
            case UsseOp.V16NMAD: Vnmad(ins); return true;
            case UsseOp.VCOMP: Vcomp(ins); return true;
            case UsseOp.VDUAL: Vdual(ins); return true;
            case UsseOp.VMOV: Vmov(ins); return true;
            case UsseOp.VPCK: Vpck(ins); return true;
            case UsseOp.VTST: Vtst(ins); return true;
            case UsseOp.VTSTMSK: Vtstmsk(ins); return true;
            case UsseOp.VBW: Vbw(ins); return true;
            case UsseOp.SOP2: Sop2(ins); return true;
            case UsseOp.SOP2M: Sop2m(ins); return true;
            case UsseOp.SOP3: Sop3(ins); return true;
            case UsseOp.I8MAD: I8mad(ins); return true;
            case UsseOp.I16MAD: I16mad(ins); return true;
            case UsseOp.I32MAD: I32mad(ins); return true;
            case UsseOp.I32MAD2: I32mad2(ins); return true;
            case UsseOp.SMP: Smp(ins); return true;
            case UsseOp.VLDST: Vldst(ins); return true;
            case UsseOp.LIMM: Limm(ins); return true;
            case UsseOp.KILL: Line("discard;"); return true;
            case UsseOp.DEPTHF: Depthf(ins); return true;
            case UsseOp.SMLSI: Smlsi(ins); return true;
            case UsseOp.SMBO: Smbo(ins); return true;
            case UsseOp.NOP: case UsseOp.PHAS: case UsseOp.SPEC: case UsseOp.I8MAD2: case UsseOp.BR: return true;
            default:
                Warnings.Add($"unhandled USSE op {ins.Op} ({ins.Raw:X16})");
                return false;
        }
    }

    // ===================================================================== vector multiply-add

    void Vmad(UsseInst i)
    {
        // "00011pppsg1oderaaittnwwwwcbfhzkkjjllmmmmmmqqqquuuuvvxyAAAABBBBBB"
        int type = i['o'] == 0 ? 1 : 2;
        var src1 = DecodeSrc12(1, i['B'], i['j'], i.Bit('r'), true, 7);
        var dest = DecodeDest(3, i['m'], i['k'], i.Bit('d'), true, 7);
        var src0 = Operand.Make(0); src0.Bank = Bank.FpInternal; src0.Flags = OpFlags.Gpi; src0.Num = (int)i['l'];
        var src2 = Operand.Make(2); src2.Bank = Bank.FpInternal; src2.Flags = OpFlags.Gpi; src2.Num = (int)i['v'];
        if (type == 1) dest.Swizzle[3] = Sw.X;
        src1.Swizzle = DecodeVec34Swizzle(i['A'], i.Bit('y'), type);
        src0.Swizzle = DecodeVec34Swizzle(i['q'], i.Bit('z'), type);
        src2.Swizzle = DecodeVec34Swizzle(i['u'], i.Bit('g'), type);
        if (i.Bit('b')) src1.Flags |= OpFlags.Abs;
        if (i.Bit('c')) src1.Flags |= OpFlags.Neg;
        if (i.Bit('i')) src0.Flags |= OpFlags.Abs;
        if (i.Bit('x')) src0.Flags |= OpFlags.Neg;
        if (i.Bit('h')) src2.Flags |= OpFlags.Abs;
        if (i.Bit('f')) src2.Flags |= OpFlags.Neg;
        int mask = (int)i['w'];
        var mode = (RepeatMode)i['a'];
        SetRepeatMultiplier(2, 2, 2, 4);
        int repeats = (int)i['t'] + 1;
        for (int r = 0; r < repeats; r++)
        {
            var a = Load(src0, mask, RepeatOffset(src0, r, mode));
            var b = Load(src1, mask, RepeatOffset(src1, r, mode));
            var c = Load(src2, mask, RepeatOffset(src2, r, mode));
            Store(dest, new Val($"fma({a}, {b}, {c})", 'f', a.N), mask, RepeatOffset(dest, r, mode));
        }
        ResetRepeatMultiplier();
    }

    static readonly Sw[][] Vmad2Src0 = [S4("XXXX"), S4("YYYY"), S4("ZZZZ"), S4("WWWW"), S4("XYZW"), S4("YZXW"), S4("XYWW"), S4("ZWXY")];
    static readonly Sw[][] Vmad2Src1 = [S4("XXXX"), S4("YYYY"), S4("ZZZZ"), S4("WWWW"), S4("XYZW"), S4("XYYZ"), S4("YYWW"), S4("WYZW")];
    static readonly Sw[][] Vmad2Src2 = [S4("XXXX"), S4("YYYY"), S4("ZZZZ"), S4("WWWW"), S4("XYZW"), S4("XZWW"), S4("XXYZ"), S4("XYZZ")];

    void Vmad2(UsseInst i)
    {
        // "00000dpps-ry-cbawwwineeeemmookttffgghhhhhhzzjjllllllqqqqqquuuuuu"
        bool f16 = i.Bit('d');
        var dt = f16 ? DataType.F16 : DataType.F32;
        var dest = DecodeDest(3, i['h'], i['t'], false, true, 7);
        var src0 = DecodeSrc0(0, i['l'], i['k'], false, true, 7);
        var src1 = DecodeSrc12(1, i['q'], i['f'], i.Bit('b'), true, 7);
        var src2 = DecodeSrc12(2, i['u'], i['g'], i.Bit('a'), true, 7);
        int mask = DecodeWriteMask(dest.Bank, (int)i['e'], f16);
        dest.Type = src0.Type = src1.Type = src2.Type = dt;
        src0.Swizzle = Vmad2Src0[i['j'] | (i['r'] << 2)];
        src1.Swizzle = Vmad2Src1[i['z'] | (i['i'] << 2)];
        src2.Swizzle = Vmad2Src2[i['w']];
        if (i.Bit('c')) src0.Flags |= OpFlags.Abs;
        src1.Flags = DecodeModifier(i['m']);
        src2.Flags = DecodeModifier(i['o']);
        var a = Load(src0, mask);
        var b = Load(src1, mask);
        var c = Load(src2, mask);
        Store(dest, new Val($"fma({a}, {b}, {c})", 'f', a.N), mask);
    }

    void Vdp(UsseInst i)
    {
        // "00011pppsc0oderaagttnwwwwbflllkkhhiijjjjjjzzzzmmmqqqyyyxxxuuuuuu"
        int type = i['o'] == 0 ? 1 : 2;
        var src1 = DecodeSrc12(1, i['u'], i['h'], i.Bit('r'), true, 7);
        var dest = DecodeDest(3, i['j'], i['k'], i.Bit('d'), true, 7);
        var src2 = Operand.Make(2); src2.Bank = Bank.FpInternal; src2.Flags = OpFlags.Gpi; src2.Num = (int)i['i'];
        src2.Swizzle = DecodeVec34Swizzle(i['z'], false, type);
        Sw Dec(uint v) => v < 8 ? (Sw)v : Sw.Undef;
        src1.Swizzle = [Dec(i['x']), Dec(i['y']), Dec(i['q']), Dec(i['m'])];
        if (i.Bit('b')) src1.Flags |= OpFlags.Neg;
        if (i.Bit('f')) src1.Flags |= OpFlags.Abs;
        if (i.Bit('g')) src2.Flags |= OpFlags.Abs;
        int writeMask = (int)i['w'];
        var mode = (RepeatMode)i['a'];
        int repeats = (int)i['t'] + 1;
        int lm = type == 1 ? 0b0111 : 0b1111;
        for (int r = 0; r < repeats; r++)
        {
            var lhs = Load(src1, lm, RepeatOffset(src1, r, mode));
            var rhs = Load(src2, lm, RepeatOffset(src2, r, mode));
            int n = BitOperations.PopCount((uint)writeMask);
            string dot = $"dot({lhs}, {rhs})";
            Store(dest, new Val(n == 1 ? dot : $"vec{n}({dot})", 'f', n), writeMask);
            writeMask = ((writeMask << 1) | (writeMask >> 3)) & 0xF;
        }
    }

    Val DoAluOp(VOp op, Operand s1, Operand s2, int srcMask, int possibleDestMask)
    {
        var a = Load(s1, srcMask);
        var b = Load(s2, srcMask);
        switch (op)
        {
            case VOp.And: return new Val($"({a} & {b})", a.K, a.N);
            case VOp.Or: return new Val($"({a} | {b})", a.K, a.N);
            case VOp.Xor: return new Val($"({a} ^ {b})", a.K, a.N);
            case VOp.Shl: return new Val($"({a} << {b})", a.K, a.N);
            case VOp.Shr: return new Val($"({a} >> {b})", a.K, a.N);
            case VOp.Asr: return new Val($"(int({a}) >> int({b}))", 'i', a.N);
            case VOp.VDsx: return new Val($"dFdx({a})", 'f', a.N);
            case VOp.VDsy: return new Val($"dFdy({a})", 'f', a.N);
            case VOp.VAdd: return new Val($"({a} + {b})", a.K, a.N);
            case VOp.VSub: return new Val($"({a} - {b})", a.K, a.N);
            case VOp.VMul: return new Val($"({a} * {b})", a.K, a.N);
            case VOp.VMin: return new Val($"min({a}, {b})", a.K, a.N);
            case VOp.VMax: return new Val($"max({a}, {b})", a.K, a.N);
            case VOp.VFrc:
                return s1.IsSame(s2, srcMask) ? new Val($"fract({a})", 'f', a.N) : new Val($"({a} - floor({b}))", 'f', a.N);
            case VOp.VDp:
            {
                string d = a.N > 1 ? $"dot({a}, {b})" : $"({a} * {b})";
                int n = BitOperations.PopCount((uint)possibleDestMask);
                return new Val(n == 1 ? d : $"vec{n}({d})", 'f', n);
            }
            case VOp.ISub: return new Val($"(int({a}) - int({b}))", 'i', a.N);
            default:
                Warnings.Add($"alu op {op} not supported");
                return new Val(Splat(0f, a.N), 'f', a.N);
        }
    }

    static readonly VOp[] NmadOps = [VOp.VMul, VOp.VAdd, VOp.VFrc, VOp.VDsx, VOp.VDsy, VOp.VMin, VOp.VMax, VOp.VDp];

    void Vnmad(UsseInst i)
    {
        // "0000Xpppsrrydcbawwwwneeeemmoiittkkllffffffzzzzzzzggghhhhhhjjjjjj"
        bool is32 = ((i.Raw >> 59) & 1) != 0;
        var op = NmadOps[i['g']];
        var dest = DecodeDest(3, i['f'], i['t'], i.Bit('d'), true, 7);
        var src1 = DecodeSrc12(1, i['h'], i['k'], i.Bit('b'), true, 7);
        src1.Flags = DecodeModifier(i['m']);
        var src2 = DecodeSrc12(2, i['j'], i['l'], i.Bit('a'), true, 7);
        if (i['o'] == 1) src2.Flags = OpFlags.Abs;
        var dt = is32 ? DataType.F32 : DataType.F16;
        dest.Type = src1.Type = src2.Type = dt;
        uint swz = i['z'] | (i['i'] << 7) | (i['c'] << 9) | (i['r'] << 10);
        src1.Swizzle = DecodeSwizzle4(swz);
        src2.Swizzle = DecodeVec34Swizzle(i['w'], false, 2);
        int destMask = (int)i['e'];
        int srcMask = op == VOp.VDp ? 0b1111 : destMask;
        var r = DoAluOp(op, src1, src2, srcMask, destMask);
        Store(dest, r, destMask);
    }

    static readonly VOp[] VcompOps = [VOp.VRcp, VOp.VRsq, VOp.VLog, VOp.VExp];
    static readonly DataType[] VcompTypes = [DataType.F32, DataType.F16, DataType.C10, DataType.Unk];

    void Vcomp(UsseInst i)
    {
        // "00110pppsddyenr-aaaaobbccmmff-ttkk--ggggggg-------hhhhhhh---wwww"
        var dest = DecodeDest(3, i['g'], i['t'], i.Bit('e'), true, 8);
        var src1 = DecodeSrc12(1, i['h'], i['k'], i.Bit('r'), true, 8);
        src1.Flags = DecodeModifier(i['m']);
        var op = VcompOps[i['b']];
        src1.Type = VcompTypes[i['c']];
        dest.Type = VcompTypes[i['d']];
        int srcMask = 1 << (int)i['f'];
        int writeMask = (int)i['w'];
        int nb = 32 - BitOperations.LeadingZeroCount((uint)writeMask);
        int repeats = (int)i['a'] + 1;
        for (int r = 0; r < repeats; r++)
        {
            var v = Load(src1, srcMask, RepeatOffset(src1, r, RepeatMode.Slmsi));
            string e = op switch
            {
                VOp.VRcp => $"(1.0 / {v})",
                VOp.VRsq => $"inversesqrt({v})",
                VOp.VLog => $"log2({v})",
                _ => $"(isnan({v}) ? 1.0 : exp2({v}))",
            };
            var res = new Val(nb > 1 ? $"vec{nb}({e})" : e, 'f', Math.Max(nb, 1));
            Store(dest, res, writeMask, RepeatOffset(dest, r, RepeatMode.Slmsi));
        }
    }

    // ===================================================================== dual issue

    enum DualSrc { I0, I1, I2, U, None }

    static DualSrc[]? DualOp1Layout(int count, uint cfg) => count switch
    {
        1 => cfg switch { 0 => [DualSrc.U], 1 => [DualSrc.I0], 2 => [DualSrc.I1], 3 => [DualSrc.I2], _ => null },
        2 => cfg switch { 0 => [DualSrc.U, DualSrc.I1], 1 => [DualSrc.I0, DualSrc.U], 2 => [DualSrc.I0, DualSrc.I1], _ => null },
        3 => cfg switch
        {
            0 => [DualSrc.U, DualSrc.I1, DualSrc.I2], 1 => [DualSrc.I0, DualSrc.U, DualSrc.I2],
            2 => [DualSrc.I0, DualSrc.I1, DualSrc.U], 3 => [DualSrc.I0, DualSrc.I1, DualSrc.I2], _ => null,
        },
        _ => null,
    };

    static DualSrc[]? DualOp2Layout(int c1, int c2, uint cfg) => (c1, c2) switch
    {
        (1, 1) => cfg == 0 ? [DualSrc.I0] : [DualSrc.U],
        (1, 2) => cfg switch { 0 => [DualSrc.I0, DualSrc.I1], 1 => [DualSrc.U, DualSrc.I1], 2 => [DualSrc.I0, DualSrc.U], _ => [DualSrc.I1, DualSrc.U] },
        (1, 3) => cfg switch
        {
            0 => [DualSrc.I0, DualSrc.I1, DualSrc.I2], 1 => [DualSrc.U, DualSrc.I1, DualSrc.I2],
            2 => [DualSrc.I0, DualSrc.U, DualSrc.I2], _ => [DualSrc.I0, DualSrc.U, DualSrc.I1],
        },
        (2, 1) => cfg switch { 0 => [DualSrc.I0], 1 => [DualSrc.I1], 2 => [DualSrc.U], _ => null },
        (2, 2) => cfg switch { 0 => [DualSrc.I0, DualSrc.I2], 1 => [DualSrc.I1, DualSrc.I2], 2 => [DualSrc.I2, DualSrc.U], _ => null },
        (3, 1) => cfg switch { 0 => [DualSrc.I0], 1 => [DualSrc.I1], 2 => [DualSrc.I2], _ => [DualSrc.U] },
        _ => null,
    };

    enum DOp { VMad, VDp, VSsq, VMul, VAdd, VMov, FRsq, FRcp, FMad, FAdd, FMul, FSubFlr, FExp, FLog, Invalid }

    static readonly DOp[] DualOp1Codes = [DOp.VMad, DOp.VDp, DOp.VSsq, DOp.VMul, DOp.VAdd, DOp.VMov, DOp.FRsq, DOp.FRcp, DOp.FMad, DOp.FAdd, DOp.FMul, DOp.FSubFlr, DOp.FExp, DOp.FLog, DOp.Invalid, DOp.Invalid];
    static readonly DOp[] DualOp2Codes = [DOp.Invalid, DOp.VDp, DOp.VSsq, DOp.VMul, DOp.VAdd, DOp.VMov, DOp.FRsq, DOp.FRcp, DOp.FMad, DOp.FAdd, DOp.FMul, DOp.FSubFlr, DOp.FExp, DOp.FLog, DOp.Invalid, DOp.Invalid];

    static (int srcCount, bool vecLoad, bool vecStore) DualInfo(DOp op) => op switch
    {
        DOp.VMad => (3, true, true), DOp.VDp => (2, true, false), DOp.VSsq => (1, true, false), DOp.VMul => (2, true, true),
        DOp.VAdd => (2, true, true), DOp.VMov => (1, true, true), DOp.FRsq => (1, false, false), DOp.FRcp => (1, false, false),
        DOp.FMad => (3, false, false), DOp.FAdd => (2, false, false), DOp.FMul => (2, false, false), DOp.FSubFlr => (2, false, false),
        DOp.FExp => (1, false, false), DOp.FLog => (1, false, false), _ => (0, false, false),
    };

    void Vdual(UsseInst i)
    {
        // "0010cgsskdtpuuuunaaalriiiiwwwwmmffeebbbbbbbooohhjqvvxxyyyzzzzzzz"
        bool compCountType = i.Bit('c');
        bool hasW = i.Bit('d');
        bool f16 = i.Bit('t');
        bool primUstore = i.Bit('r');
        var op1 = DualOp1Codes[((!compCountType && hasW) ? 8 : 0) | (int)i['a']];
        var op2 = DualOp2Codes[(i['l'] << 3) | i['o']];
        if (op1 == DOp.Invalid || op2 == DOp.Invalid) { Warnings.Add("invalid vdual"); return; }
        var info1 = DualInfo(op1);
        var info2 = DualInfo(op2);
        bool unifiedVec = (primUstore && info1.vecStore) || (!primUstore && info2.vecStore);
        var unifiedDest = DecodeDest(3, i['b'], i['m'], false, unifiedVec, unifiedVec ? 8 : 7);
        unifiedDest.Type = f16 ? DataType.F16 : DataType.F32;
        var internalDest = Operand.Make(3); internalDest.Bank = Bank.FpInternal; internalDest.Num = (int)i['e'];
        var dest1 = primUstore ? unifiedDest : internalDest;
        var dest2 = primUstore ? internalDest : unifiedDest;
        var l1 = DualOp1Layout(info1.srcCount, i['h']);
        var l2 = DualOp2Layout(info1.srcCount, info2.srcCount, i['h']);
        if (l1 == null || l2 == null) { Warnings.Add("vdual layout"); return; }
        int fixedMask = (int)i['y'] | ((compCountType && hasW) ? 8 : 0);
        int MaskFor((int, bool, bool vecStore) info, bool destInternal)
        {
            if (destInternal) return compCountType ? 0b1111 : 0b0111;
            if (info.vecStore) return f16 ? 0b1111 : 0b0011;
            return f16 ? 0b0011 : 0b0001;
        }
        int mask1 = primUstore ? MaskFor(info1, dest1.Bank == Bank.FpInternal) : fixedMask;
        int mask2 = primUstore ? fixedMask : MaskFor(info2, dest2.Bank == Bank.FpInternal);
        int op1Count = info1.srcCount;

        List<Operand> Srcs(DualSrc[] layout, (int, bool vecLoad, bool) info)
        {
            var list = new List<Operand>();
            foreach (var id in layout)
            {
                Operand op;
                switch (id)
                {
                    case DualSrc.U:
                        op = DecodeSrc12(0, i['z'], i['f'], false, info.vecLoad, info.vecLoad ? 8 : 7);
                        op.Swizzle = DecodeDualSwizzle(i['u'], op1Count < 2 && i.Bit('j'), compCountType);
                        if (op1Count < 2 && i.Bit('q')) op.Flags |= OpFlags.Abs;
                        if (i.Bit('n')) op.Flags |= OpFlags.Neg;
                        break;
                    case DualSrc.I0:
                        op = Operand.Make(0); op.Bank = Bank.FpInternal; op.Num = (int)i['x'];
                        op.Swizzle = DecodeDualSwizzle(i['i'], false, compCountType);
                        break;
                    case DualSrc.I1:
                        op = Operand.Make(1); op.Bank = Bank.FpInternal; op.Num = (int)i['v'];
                        op.Swizzle = DecodeDualSwizzle(i['w'], i.Bit('p'), compCountType);
                        if (i.Bit('g')) op.Flags |= OpFlags.Neg;
                        break;
                    default:
                        op = Operand.Make(2); op.Bank = Bank.FpInternal;
                        op.Num = op1Count >= 2 ? (int)((i['j'] << 1) | i['q']) : 2;
                        op.Swizzle = S4("XYZW");
                        break;
                }
                op.Type = f16 ? DataType.F16 : DataType.F32;
                list.Add(op);
            }
            return list;
        }

        Val DoOp(DOp code, List<Operand> ops, int destMask, (int, bool vecLoad, bool) info)
        {
            bool isVdp = code is DOp.VDp or DOp.VSsq;
            int srcMask = info.vecLoad ? (isVdp ? (0b1111 >> (compCountType ? 0 : 1)) : destMask) : 0b0001;
            switch (code)
            {
                case DOp.VMov: return Load(ops[0], srcMask);
                case DOp.FAdd: case DOp.VAdd: { var a = Load(ops[0], srcMask); var b = Load(ops[1], srcMask); return new Val($"({a} + {b})", 'f', a.N); }
                case DOp.FMul: case DOp.VMul: { var a = Load(ops[0], srcMask); var b = Load(ops[1], srcMask); return new Val($"({a} * {b})", 'f', a.N); }
                case DOp.FRcp: { var a = Load(ops[0], srcMask); return new Val($"(1.0 / {a})", 'f', a.N); }
                case DOp.FRsq: { var a = Load(ops[0], srcMask); return new Val($"inversesqrt({a})", 'f', a.N); }
                case DOp.VDp: { var a = Load(ops[0], srcMask); var b = Load(ops[1], srcMask); return new Val(a.N > 1 ? $"dot({a}, {b})" : $"({a} * {b})", 'f', 1); }
                case DOp.FExp: { var a = Bind(Load(ops[0], srcMask)); return new Val($"mix(exp2({a}), {Splat(1f, a.N)}, isnan(exp2({a})))", 'f', a.N); }
                case DOp.FLog: { var a = Load(ops[0], srcMask); return new Val($"log2({a})", 'f', a.N); }
                case DOp.VSsq: { var a = Bind(Load(ops[0], srcMask)); return new Val(a.N > 1 ? $"dot({a}, {a})" : $"({a} * {a})", 'f', 1); }
                case DOp.FMad: case DOp.VMad:
                { var a = Load(ops[0], srcMask); var b = Load(ops[1], srcMask); var c = Load(ops[2], srcMask); return new Val($"({a} * {b} + {c})", 'f', a.N); }
                case DOp.FSubFlr:
                {
                    var a = Load(ops[0], srcMask);
                    if (ops[0].IsSame(ops[1], srcMask)) return new Val($"fract({a})", 'f', a.N);
                    var b = Load(ops[1], srcMask);
                    return new Val($"({a} - floor({b}))", 'f', a.N);
                }
            }
            return new Val("0.0", 'f', 1);
        }

        var r1 = Bind(DoOp(op1, Srcs(l1, info1), mask1, info1));
        var r2 = Bind(DoOp(op2, Srcs(l2, info2), mask2, info2));
        Store(dest1, r1, mask1);
        Store(dest2, r2, mask2);
    }

    // ===================================================================== moves / packs

    static readonly DataType[] MovTypes = [DataType.Int8, DataType.Int16, DataType.Int32, DataType.C10, DataType.F16, DataType.F32, DataType.UInt8, DataType.UInt16, DataType.UInt32, DataType.O8];

    void Vmov(UsseInst i)
    {
        // "00111pppstrydecbmmaanoooiwwwwkllffgghhhhjjjjjjqqqqqquuuuuuvvvvvv"
        uint moveType = i['m'];
        if (moveType == 3) { Warnings.Add("vmov invalid move type"); return; }
        var dt = i['o'] < MovTypes.Length ? MovTypes[i['o']] : DataType.Unk;
        bool dbl = dt is DataType.C10 or DataType.F16 or DataType.F32;
        bool conditional = moveType != 0;
        bool u8cond = moveType == 2;
        int regBits = dbl ? 7 : 6;
        var dest = DecodeDest(3, i['j'], i['l'], i.Bit('d'), dbl, regBits);
        var src1 = DecodeSrc12(1, i['u'], i['f'], i.Bit('c'), dbl, regBits);
        int mask = (int)i['h'];
        if (dt is DataType.F16 or DataType.F32) mask = DecodeWriteMask(dest.Bank, mask, dt == DataType.F16);
        else if (!dbl) mask = 1;
        src1.Swizzle = dbl ? DecodeVec34Swizzle(i['w'], false, 2) : (Sw[])Default4.Clone();
        src1.Type = dest.Type = dt;
        var testType = u8cond ? DataType.UInt8 : dt;
        Operand src0 = default, src2 = default;
        int cmp = 0;
        if (conditional)
        {
            cmp = (int)((i['t'] << 1) | i['i']);
            src0 = DecodeSrc0(0, i['q'], i['k'], i.Bit('e'), dbl, regBits);
            src0.Type = testType;
            src2 = DecodeSrc12(2, i['v'], i['g'], i.Bit('b'), dbl, regBits);
            src2.Type = dt;
            if (i.Bit('r')) src0.Swizzle = (Sw[])src1.Swizzle.Clone();
            src2.Swizzle = (Sw[])src1.Swizzle.Clone();
        }
        if (dest.Bank == Bank.Special || src1.Bank == Bank.Special || (conditional && (src0.Bank == Bank.Special || src2.Bank == Bank.Special)))
        {
            Warnings.Add("vmov special regs");
            return;
        }
        if (dt is DataType.F16 or DataType.F32) SetRepeatMultiplier(2, 2, 2, 2); else SetRepeatMultiplier(1, 1, 1, 1);
        int repeats = (int)i['a'] + 1;
        for (int r = 0; r < repeats; r++)
        {
            var s1 = Load(src1, mask, RepeatOffset(src1, r, RepeatMode.Slmsi));
            Val result;
            if (conditional)
            {
                var c = Bind(Load(src0, mask, RepeatOffset(src0, r, RepeatMode.Slmsi)));
                var s2 = Load(src2, mask, RepeatOffset(src2, r, RepeatMode.Slmsi));
                string zero = c.K switch { 'f' => c.N == 1 ? "0.0" : $"vec{c.N}(0.0)", 'i' => c.N == 1 ? "0" : $"ivec{c.N}(0)", _ => c.N == 1 ? "0u" : $"uvec{c.N}(0u)" };
                string cond;
                if (c.N == 1)
                    cond = cmp switch { 0 => $"({c} == {zero})", 1 => $"({c} != {zero})", 2 => $"({c} < {zero})", _ => $"({c} <= {zero})" };
                else
                    cond = cmp switch { 0 => $"equal({c}, {zero})", 1 => $"notEqual({c}, {zero})", 2 => $"lessThan({c}, {zero})", _ => $"lessThanEqual({c}, {zero})" };
                // select(cond ? src1 : src2) â€” component-wise via mix for vectors
                if (s1.N == 1) result = new Val($"({cond} ? {s1} : {s2})", s1.K, 1);
                else result = new Val($"mix({s2}, {s1}, {(c.N == s1.N ? cond : $"bvec{s1.N}({cond})")})", s1.K, s1.N);
            }
            else result = s1;
            Store(dest, result, mask, RepeatOffset(dest, r, RepeatMode.Slmsi));
        }
        ResetRepeatMultiplier();
    }

    static readonly DataType[] PckTypes = [DataType.UInt8, DataType.Int8, DataType.O8, DataType.UInt16, DataType.Int16, DataType.F16, DataType.F32, DataType.C10];

    void Vpck(UsseInst i)
    {
        // "01000pppsnuydercaaaaffftttmmmmbbkkllgggggggoohiijjqqqqqqvwwwwwwx"
        var destType = PckTypes[i['t']];
        var srcType = PckTypes[i['f']];
        var dest = DecodeDest(3, i['g'], i['b'], i.Bit('d'), false, 7);
        dest.Type = destType;
        uint src1n = i['q'];
        bool src1Dreg = true;
        if (!GxpProgram.IsFloat(srcType)) { src1n = i['v'] | (src1n << 1); src1Dreg = false; }
        var src1 = DecodeSrc12(1, src1n, i['k'], i.Bit('r'), src1Dreg, 7);
        src1.Type = srcType;
        var src2 = DecodeSrc12(2, i['w'], i['l'], i.Bit('c'), true, 7);
        if (dest.Bank == Bank.Special || src1.Bank == Bank.Special || src2.Bank == Bank.Special) { Warnings.Add("vpck special"); return; }
        uint cs0 = i['x'] | (srcType == DataType.F32 ? (i['v'] & 1) << 1 : (i['w'] & 1) << 1);
        src1.Swizzle = [(Sw)cs0, (Sw)i['i'], (Sw)i['j'], (Sw)i['o']];
        int mask = (int)i['m'];
        bool scale = i.Bit('h');
        bool noCycle = (GxpProgram.IsFloat(srcType) && GxpProgram.IsFloat(destType)) || (scale && (srcType == DataType.UInt8 || destType == DataType.UInt8));
        bool useSrc2 = false;
        if (srcType == DataType.F32 && src2.Bank != Bank.Immediate)
        {
            bool same = src1.Num == src2.Num && src1.Bank == src2.Bank;
            bool contiguous = mask is 0b1 or 0b11 or 0b111 or 0b1111;
            bool isDefault = true;
            int cnt = BitOperations.PopCount((uint)mask);
            for (int k = 0; k < cnt; k++) if (src1.Swizzle[k] != (Sw)k) isDefault = false;
            if (!isDefault || !same || !contiguous) useSrc2 = true;
        }
        if (!noCycle)
        {
            var tmp = (Sw[])src1.Swizzle.Clone();
            bool onlyTwo = srcType == DataType.UInt8 || destType == DataType.UInt8;
            int taken = 0;
            for (int k = 0; k < 4; k++)
                if ((mask & (1 << k)) != 0)
                    src1.Swizzle[k] = onlyTwo && mask == 0b1111 ? tmp[k % 2] : tmp[taken++];
        }
        if (GxpProgram.IsFloat(srcType)) SetRepeatMultiplier(1, 2, 2, 1); else SetRepeatMultiplier(1, 1, 1, 1);
        int repeats = (int)i['a'] + 1;
        for (int r = 0; r < repeats; r++)
        {
            int o1 = RepeatOffset(src1, r, RepeatMode.Slmsi);
            Val source;
            if (useSrc2)
            {
                var a = src1; a.Swizzle = (Sw[])Default4.Clone();
                var b = src2; b.Swizzle = (Sw[])Default4.Clone();
                var s1 = Bind(Load(a, 0b11, o1));
                var s2 = Bind(Load(b, 0b11, RepeatOffset(src2, r, RepeatMode.Slmsi)));
                var comps = new List<string>();
                for (int k = 0; k < 4; k++)
                {
                    if ((mask & (1 << k)) == 0) continue;
                    var sw = src1.Swizzle[k];
                    comps.Add(sw switch { Sw.X => $"{s1}.x", Sw.Y => $"{s1}.y", Sw.Z => $"{s2}.x", Sw.W => $"{s2}.y", _ => ConstOf(sw, 'f') });
                }
                source = Compose(comps, 'f');
            }
            else source = Load(src1, mask, o1);
            if (GxpProgram.IsFloat(destType) && !GxpProgram.IsFloat(srcType)) source = ToFloat(source, srcType, scale);
            if (!GxpProgram.IsFloat(destType) && GxpProgram.IsFloat(srcType)) source = ToInt(source, destType, scale);
            Store(dest, source, mask, RepeatOffset(dest, r, RepeatMode.Slmsi));
        }
        ResetRepeatMultiplier();
    }

    void Limm(UsseInst i)
    {
        // "11111100sn10deiiiiiipppmmmmm--tt----uuuuuuuvvvvvvvvvvvvvvvvvvvvv"
        uint imm = i['v'] | (i['m'] << 21) | (i['i'] << 26);
        var dest = DecodeDest(3, i['u'], i['t'], i.Bit('d'), false, 7);
        dest.Type = DataType.UInt32;
        Store(dest, new Val($"{imm}u", 'u', 1), 1);
    }
}
