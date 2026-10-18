using System.Numerics;

namespace VitaSharp.Core.Gxm.Shader;

public sealed partial class UsseTranslator
{
    // ===================================================================== tests

    static readonly VOp[] TestF16 = [VOp.Invalid, VOp.Invalid, VOp.VAdd, VOp.VFrc, VOp.VRcp, VOp.VRsq, VOp.VLog, VOp.VExp, VOp.VDp, VOp.VMin, VOp.VMax, VOp.VDsx, VOp.VDsy, VOp.VMul, VOp.VSub, VOp.Invalid];

    (VOp op, DataType dt, bool isSub) DecodeTest(uint aluSel, uint aluOp, uint prec)
    {
        if (aluSel == 0) return (TestF16[aluOp], prec == 0 ? DataType.F16 : DataType.F32, aluOp == 14);
        switch (aluSel)
        {
            case 1:
                return aluOp switch
                {
                    6 => (VOp.VAdd, DataType.Int16, false), 7 => (VOp.VSub, DataType.Int16, true), 8 => (VOp.VMul, DataType.Int16, false),
                    9 => (VOp.VAdd, DataType.UInt16, false), 10 => (VOp.VSub, DataType.UInt16, true), 11 => (VOp.VMul, DataType.UInt16, false),
                    12 => (VOp.VAdd, DataType.Int32, false), 13 => (VOp.VAdd, DataType.UInt32, false),
                    14 => (VOp.VSub, DataType.Int32, true), 15 => (VOp.VSub, DataType.UInt32, true),
                    _ => (VOp.Invalid, DataType.Unk, false),
                };
            case 2:
                return aluOp switch
                {
                    0 => (VOp.VAdd, DataType.UInt8, false), 1 => (VOp.VSub, DataType.UInt8, true), 2 => (VOp.VAdd, DataType.UInt8, false),
                    3 => (VOp.VSub, DataType.UInt8, true), 4 => (VOp.VMul, DataType.UInt8, false), 5 => (VOp.VMul, DataType.UInt8, false),
                    6 => (VOp.VMul, DataType.UInt8, false), 7 => (VOp.VAdd, DataType.UInt8, false), 8 => (VOp.VSub, DataType.UInt8, true),
                    _ => (VOp.Invalid, DataType.Unk, false),
                };
            default:
                return aluOp switch
                {
                    0 => (VOp.And, DataType.UInt32, false), 1 => (VOp.Or, DataType.UInt32, false), 2 => (VOp.Xor, DataType.UInt32, false),
                    3 => (VOp.Shl, DataType.UInt32, false), 4 => (VOp.Shr, DataType.UInt32, false), 5 => (VOp.Rol, DataType.UInt32, false),
                    7 => (VOp.Asr, DataType.UInt32, false), _ => (VOp.Invalid, DataType.Unk, false),
                };
        }
    }

    /// <summary>Returns a bool/bvec expression for a test (port of vtst_impl).</summary>
    Val TestImpl(VOp op, bool isSub, Operand src1, Operand src2, int zeroTest, int signTest, int loadMask)
    {
        bool has4 = BitOperations.PopCount((uint)loadMask) == 4;
        Val lhs, rhs;
        var dt = src1.Type;
        if (isSub)
        {
            lhs = Load(src1, loadMask);
            rhs = Load(src2, loadMask);
        }
        else
        {
            lhs = op switch
            {
                VOp.VRcp => new Val($"(1.0 / {Load(src1, loadMask)})", 'f', BitOperations.PopCount((uint)loadMask)),
                VOp.VRsq => new Val($"inversesqrt({Load(src1, loadMask)})", 'f', BitOperations.PopCount((uint)loadMask)),
                VOp.VLog => new Val($"log2({Load(src1, loadMask)})", 'f', BitOperations.PopCount((uint)loadMask)),
                VOp.VExp => new Val($"exp2({Load(src1, loadMask)})", 'f', BitOperations.PopCount((uint)loadMask)),
                _ => DoAluOp(op, src1, src2, loadMask, has4 ? 0b1111 : 0b1),
            };
            int n = lhs.N;
            rhs = lhs.K switch
            {
                'i' => new Val(n == 1 ? "0" : $"ivec{n}(0)", 'i', n),
                'u' => new Val(n == 1 ? "0u" : $"uvec{n}(0u)", 'u', n),
                _ => new Val(Splat(0f, n), 'f', n),
            };
        }
        lhs = Bind(lhs);
        if (lhs.K != rhs.K)
        {
            // align kinds (integer tests on float-loaded data)
            rhs = lhs.K switch { 'i' => new Val($"ivec{rhs.N}({rhs})".Replace("ivec1", "int"), 'i', rhs.N), 'u' => new Val($"uvec{rhs.N}({rhs})".Replace("uvec1", "uint"), 'u', rhs.N), _ => rhs };
        }
        bool eq = zeroTest == 1;
        if (signTest == 3) return new Val("true", 'b', 1);
        if (lhs.N == 1)
        {
            string o = signTest switch { 0 => eq ? "==" : "!=", 1 => eq ? "<=" : "<", _ => eq ? ">=" : ">" };
            return new Val($"({lhs} {o} {rhs})", 'b', 1);
        }
        string fn = signTest switch { 0 => eq ? "equal" : "notEqual", 1 => eq ? "lessThanEqual" : "lessThan", _ => eq ? "greaterThanEqual" : "greaterThan" };
        return new Val($"{fn}({lhs}, {rhs})", 'b', lhs.N);
    }

    void Vtst(UsseInst i)
    {
        // "01001ppps-oydrceavttiizzmhhhnnbbkkffgggggggwlluuuujjjjjjjqqqqqqq"
        var (op, dt, isSub) = DecodeTest(i['l'], i['u'], i['a']);
        if (op == VOp.Invalid) { Warnings.Add($"vtst op {i['l']}:{i['u']}"); return; }
        uint chan = i['h'];
        if (chan >= 4) { Warnings.Add("vtst chan_cc"); return; }
        int loadMask = 1 << (int)chan;
        bool dbl = i['l'] == 0;
        int bits = dbl ? 8 : 7;
        var src1 = DecodeSrc12(1, i['j'], i['k'], i.Bit('c'), dbl, bits);
        var src2 = DecodeSrc12(2, i['q'], i['f'], i.Bit('e'), dbl, bits);
        src1.Type = src2.Type = dt;
        src2.Swizzle = i.Bit('v') && i['l'] == 0 ? S4("XXXX") : (Sw[])Default4.Clone();
        if (i.Bit('r')) src1.Flags |= OpFlags.Neg;
        var pred = Operand.Make(3); pred.Bank = Bank.Predicate; pred.Num = (int)i['n'];
        var res = TestImpl(op, isSub, src1, src2, (int)i['z'], (int)i['i'], loadMask);
        Store(pred, res.N == 1 ? res : new Val($"any({res})", 'b', 1), 1);
    }

    void Vtstmsk(UsseInst i)
    {
        // "01111ppps-oydtrcevuuiizzm-aa--bbnnkkfffffffwllgggghhhhhhhjjjjjjj"
        var (op, dt, isSub) = DecodeTest(i['l'], i['g'], i['e']);
        if (op == VOp.Invalid) { Warnings.Add("vtstmsk op"); return; }
        uint maskType = i['a'];
        if (maskType == 3) return;
        bool dbl = i['l'] == 0;
        int bits = dbl ? 8 : 7;
        var src1 = DecodeSrc12(1, i['h'], i['n'], i.Bit('r'), dbl, bits);
        var src2 = DecodeSrc12(2, i['j'], i['k'], i.Bit('c'), dbl, bits);
        var dest = DecodeDest(3, i['f'], i['b'], i.Bit('d'), dbl, bits);
        src1.Type = src2.Type = dest.Type = dt;
        src2.Swizzle = i['l'] == 0 && i.Bit('v') ? S4("XXXX") : (Sw[])Default4.Clone();
        if (i.Bit('t')) src1.Flags |= OpFlags.Neg;
        if (!i.Bit('w')) { Warnings.Add("vtstmsk without write"); return; }
        bool isVdp = op == VOp.VDp;
        bool out4 = i['l'] == 0 && maskType == 2;
        var res = Bind(TestImpl(op, isSub, src1, src2, (int)i['z'], (int)i['i'], (isVdp || out4) ? 0b1111 : 0b1));
        Val outV;
        switch (dt)
        {
            case DataType.F16:
            case DataType.F32:
                outV = res.N == 1 ? new Val($"({res} ? 1.0 : 0.0)", 'f', 1) : new Val($"mix(vec{res.N}(0.0), vec{res.N}(1.0), {res})", 'f', res.N);
                break;
            default:
            {
                (uint ones, bool signed) = dt switch
                {
                    DataType.Int8 => (0x7Fu, true), DataType.Int16 => (0x7FFFu, true), DataType.Int32 => (0x7FFFFFFFu, true),
                    DataType.UInt8 => (0xFFu, false), DataType.UInt16 => (0xFFFFu, false), _ => (0xFFFFFFFFu, false),
                };
                string zero = signed ? "0" : "0u", one = signed ? $"{(int)ones}" : $"{ones}u";
                outV = res.N == 1 ? new Val($"({res} ? {one} : {zero})", signed ? 'i' : 'u', 1)
                    : new Val($"mix({(signed ? "ivec" : "uvec")}{res.N}({zero}), {(signed ? "ivec" : "uvec")}{res.N}({one}), {res})", signed ? 'i' : 'u', res.N);
                break;
            }
        }
        Store(dest, outV, out4 ? 0b1111 : 0b1);
    }

    // ===================================================================== bitwise

    void Vbw(UsseInst i)
    {
        // "01ooopppsnrydecxaaaaittttthhbwkkffggjjjjjjjlllllllmmmmmmmqqqqqqq"
        uint op1 = i['o'];
        bool op2 = i.Bit('b');
        VOp op = op1 switch
        {
            0b010 => op2 ? VOp.Or : VOp.And,
            0b011 => VOp.Xor,
            0b100 => op2 ? VOp.Rol : VOp.Shl,
            0b101 => op2 ? VOp.Asr : VOp.Shr,
            _ => VOp.Invalid,
        };
        if (op == VOp.Invalid) { Warnings.Add($"vbw op1 {op1}"); return; }
        var src1 = DecodeSrc12(1, i['m'], i['f'], i.Bit('c'), false, 7);
        var src2 = DecodeSrc12(2, i['q'], i['g'], i.Bit('x'), false, 7);
        var dest = DecodeDest(3, i['j'], i['k'], i.Bit('d'), false, 7);
        var dt = i.Bit('w') ? DataType.UInt16 : DataType.UInt32;
        src1.Type = src2.Type = dest.Type = dt;
        SetRepeatMultiplier(1, 1, 1, 1);
        int repeats = (int)i['a'] + 1;
        uint rot = i['t'];
        for (int r = 0; r < repeats; r++)
        {
            string s2;
            bool immediate = i.Bit('x') && src2.Bank == Bank.Immediate;
            if (immediate)
            {
                uint value = i['q'] | (i['l'] << 7) | (i['h'] << 14);
                if (rot > 0)
                {
                    if (dt == DataType.UInt16)
                    {
                        uint rr = rot & 15;
                        ushort t = (ushort)value;
                        value = (ushort)((t << (int)rr) | (t >> (16 - (int)rr)));
                    }
                    else value = BitOperations.RotateLeft(value, (int)rot);
                }
                if (i.Bit('i')) value = ~value;
                if (dt == DataType.UInt16) value &= 0xFFFF;
                s2 = $"{value}u";
            }
            else
            {
                s2 = Bind(Load(src2, 1, RepeatOffset(src2, r, RepeatMode.Slmsi))).E;
                if (rot != 0)
                {
                    uint rr = dt == DataType.UInt16 ? rot & 15 : rot;
                    uint right = dt == DataType.UInt16 ? 16 - rr : 32 - rr;
                    s2 = $"(({s2} << {rr}u) | ({s2} >> {right}u))";
                    if (dt == DataType.UInt16) s2 = $"({s2} & 0xFFFFu)";
                }
                if (i.Bit('i')) s2 = dt == DataType.UInt16 ? $"(~{s2} & 0xFFFFu)" : $"(~{s2})";
            }
            var a = Load(src1, 1, RepeatOffset(src1, r, RepeatMode.Slmsi));
            string e = op switch
            {
                VOp.And => $"({a} & {s2})",
                VOp.Or => $"({a} | {s2})",
                VOp.Xor => $"({a} ^ {s2})",
                VOp.Shl => $"({a} << {s2})",
                VOp.Shr => $"({a} >> {s2})",
                VOp.Asr => $"uint(int({a}) >> int({s2}))",
                _ => $"(({a} << ({s2} & 31u)) | ({a} >> ((32u - ({s2} & 31u)) & 31u)))", // ROL
            };
            Store(dest, new Val(e, 'u', 1), 1, RepeatOffset(dest, r, RepeatMode.Slmsi));
        }
        ResetRepeatMultiplier();
    }

    // ===================================================================== SOP (colour combine)

    static string BlendOp(int op, string l, string r) => op switch
    {
        0 => $"({l} + {r})",
        1 => $"({l} - {r})",
        2 => $"min({l}, {r})",
        _ => $"max({l}, {r})",
    };

    void Sop2(UsseInst i)
    {
        // "10000ppcsnaaderbmooofllggghhhittkkjjqqqqqqquvvwwxyzzzzzzzAAAAAAA"
        var src1 = DecodeSrc12(1, i['z'], i['k'], i.Bit('r'), false, 7);
        var src2 = DecodeSrc12(2, i['A'], i['j'], i.Bit('b'), false, 7);
        var dest = DecodeDest(3, i['q'], i['t'], i.Bit('d'), false, 7);
        src1.Type = src2.Type = dest.Type = DataType.UInt8;
        uint csel1 = i['g'], csel2 = i['h'], asel1 = i['a'], asel2 = i['l'];
        if (csel1 > 4 || csel2 > 4 || asel1 > 2 || asel2 > 2) { Warnings.Add("sop2 selector"); return; }
        int cop = (int)i['v'], aop = (int)i['w'];
        int repeats = (int)i['o'] + 1;
        for (int r = 0; r < repeats; r++)
        {
            int o1 = RepeatOffset(src1, r, RepeatMode.Slmsi), o2 = RepeatOffset(src2, r, RepeatMode.Slmsi);
            var c1 = Bind(ToFloat(Load(src1, 0b0111, o1), DataType.UInt8, true));
            var c2 = Bind(ToFloat(Load(src2, 0b0111, o2), DataType.UInt8, true));
            var a1 = Bind(ToFloat(Load(src1, 0b1000, o1), DataType.UInt8, true));
            var a2 = Bind(ToFloat(Load(src2, 0b1000, o2), DataType.UInt8, true));
            string sc1 = c1.E, sa1 = a1.E;
            if (i.Bit('u')) { sc1 = $"(vec3(1.0) - {c1})"; sa1 = $"(1.0 - {a1})"; }
            string CSel(uint s) => s switch { 0 => "vec3(0.0)", 1 => sc1, 2 => c2.E, 3 => $"vec3({sa1})", _ => $"vec3({a2})" };
            string ASel(uint s) => s switch { 0 => "0.0", 1 => sa1, _ => a2.E };
            string fl = CSel(csel1), fr = CSel(csel2), al = ASel(asel1), ar = ASel(asel2);
            if (i.Bit('c')) fl = $"(vec3(1.0) - {fl})";
            if (i.Bit('m')) fr = $"(vec3(1.0) - {fr})";
            if (i.Bit('f')) al = $"(1.0 - {al})";
            if (i.Bit('i')) ar = $"(1.0 - {ar})";
            string color = BlendOp(cop, $"({fl} * {sc1})", $"({fr} * {c2})");
            string alpha = BlendOp(aop, $"({al} * {sa1})", $"({ar} * {a2})");
            Store(dest, ToInt(new Val(color, 'f', 3), DataType.UInt8, true), 0b0111, RepeatOffset(dest, r, RepeatMode.Slmsi));
            Store(dest, ToInt(new Val(alpha, 'f', 1), DataType.UInt8, true), 0b1000, RepeatOffset(dest, r, RepeatMode.Slmsi));
        }
    }

    void Sop2m(UsseInst i)
    {
        // "10010ppmsnccderbowwwwaalllfff-ttkkgguuuuuuu-------hhhhhhhiiiiiii"
        var src1 = DecodeSrc12(1, i['h'], i['k'], i.Bit('r'), false, 7);
        var src2 = DecodeSrc12(2, i['i'], i['g'], i.Bit('b'), false, 7);
        var dest = DecodeDest(3, i['u'], i['t'], i.Bit('d'), false, 7);
        src1.Type = src2.Type = dest.Type = DataType.UInt8;
        uint sel1 = i['l'], sel2 = i['f'];
        string? Sel(uint s, string a, string b) => s switch { 0 => "vec4(0.0)", 2 => a, 3 => $"{a}.wwww", 6 => b, 7 => $"{b}.wwww", _ => null };
        var s1 = Bind(ToFloat(Load(src1, 0b1111), DataType.UInt8, true));
        var s2 = Bind(ToFloat(Load(src2, 0b1111), DataType.UInt8, true));
        var op1 = Sel(sel1, s1.E, s2.E);
        var op2 = Sel(sel2, s1.E, s2.E);
        if (op1 == null || op2 == null) { Warnings.Add("sop2m selector"); return; }
        if (i.Bit('m')) op1 = $"(vec4(1.0) - {op1})";
        if (i.Bit('o')) op2 = $"(vec4(1.0) - {op2})";
        string t1 = Bind(new Val($"({op1} * {s1})", 'f', 4)).E, t2 = Bind(new Val($"({op2} * {s2})", 'f', 4)).E;
        int cop = (int)i['c'], aop = (int)i['a'];
        string res = Bind(new Val(BlendOp(cop, t1, t2), 'f', 4)).E;
        int wmask = (int)i['w'];
        wmask = ((wmask & 0b1110) >> 1) | ((wmask & 1) << 3);
        if ((wmask & 0b1000) != 0) Line($"{res}.w = {BlendOp(aop, $"{t1}.w", $"{t2}.w")};");
        Store(dest, ToInt(new Val(res, 'f', 4), DataType.UInt8, true), wmask);
    }

    void Sop3(UsseInst i)
    {
        // "10001ppcsnooderbmallfgghhhiiikttjjqquuuuuuuvvvvvvvwwwwwwwxxxxxxx"
        var src0 = DecodeSrc0(0, i['v'], i['k'], false, false, 7);
        var src1 = DecodeSrc12(1, i['w'], i['j'], i.Bit('r'), false, 7);
        var src2 = DecodeSrc12(2, i['x'], i['q'], i.Bit('b'), false, 7);
        var dest = DecodeDest(3, i['u'], i['t'], i.Bit('d'), false, 7);
        src0.Type = src1.Type = src2.Type = dest.Type = DataType.UInt8;
        if (i.Bit('f')) { Warnings.Add("sop3 dmod"); return; }
        uint csel1 = i['h'], csel2 = i['i'], asel1 = i['l'];
        var c0 = Bind(ToFloat(Load(src0, 0b0111), DataType.UInt8, true));
        var c1 = Bind(ToFloat(Load(src1, 0b0111), DataType.UInt8, true));
        var c2 = Bind(ToFloat(Load(src2, 0b0111), DataType.UInt8, true));
        var a0 = Bind(ToFloat(Load(src0, 0b1000), DataType.UInt8, true));
        var a1 = Bind(ToFloat(Load(src1, 0b1000), DataType.UInt8, true));
        var a2 = Bind(ToFloat(Load(src2, 0b1000), DataType.UInt8, true));
        string? CSel(uint s) => s switch { 0 => "vec3(0.0)", 2 => c1.E, 3 => $"vec3({a1})", 4 => c0.E, 5 => $"vec3({a0})", 6 => c2.E, 7 => $"vec3({a2})", _ => null };
        string ASel1(uint s) => s switch { 0 => "0.0", 1 => a0.E, 2 => a1.E, _ => a2.E };
        string? ASel2(uint s) => s switch { 0 => "0.0", 2 or 3 => a1.E, 4 or 5 => a0.E, 6 or 7 => a2.E, _ => null };
        var fl = CSel(csel1); var fr = CSel(csel2); var al = ASel1(asel1); var ar = ASel2(csel2);
        if (fl == null || fr == null || ar == null) { Warnings.Add("sop3 selector"); return; }
        if (i.Bit('c')) fl = $"(vec3(1.0) - {fl})";
        if (i.Bit('m')) { fr = $"(vec3(1.0) - {fr})"; ar = $"(1.0 - {ar})"; }
        if (i.Bit('a')) al = $"(1.0 - {al})";
        int cop = (int)i['o'], aop = (int)i['g'];
        string color = cop == 0 ? $"(({fl} * {c1}) + ({fr} * {c2}))" : $"(({fl} * {c1}) - ({fr} * {c2}))";
        string alpha = aop == 0 ? $"(({al} * {a1}) + ({ar} * {a2}))" : $"(({al} * {a1}) - ({ar} * {a2}))";
        Store(dest, ToInt(new Val(color, 'f', 3), DataType.UInt8, true), 0b0111);
        Store(dest, ToInt(new Val(alpha, 'f', 1), DataType.UInt8, true), 0b1000);
    }

    // ===================================================================== integer multiply-add

    void I8mad(UsseInst i)
    {
        // "10011ppcsneedarbmtttuolfghijkqvvwwxxyyyyyyyzzzzzzzAAAAAAABBBBBBB"
        var src0 = DecodeSrc0(0, i['z'], i['q'], false, false, 7);
        var src1 = DecodeSrc12(1, i['A'], i['w'], i.Bit('r'), false, 7);
        var src2 = DecodeSrc12(2, i['B'], i['x'], i.Bit('b'), false, 7);
        var dest = DecodeDest(3, i['y'], i['v'], i.Bit('d'), false, 7);
        src0.Type = src1.Type = src2.Type = dest.Type = DataType.UInt8;
        if (i.Bit('i')) src1.Swizzle = S4("WWWW");
        if (i.Bit('j')) src2.Swizzle = S4("WWWW");
        int repeats = (int)i['t'] + 1;
        for (int r = 0; r < repeats; r++)
        {
            var m1 = Load(src1, 0b1111, RepeatOffset(src1, r, RepeatMode.Slmsi));
            var m2 = Load(src2, 0b1111, RepeatOffset(src2, r, RepeatMode.Slmsi));
            var add = Load(src0, 0b1111, RepeatOffset(src0, r, RepeatMode.Slmsi));
            string e = i.Bit('k') ? $"({m1} * {m2} - {add})" : $"({m1} * {m2} + {add})";
            Store(dest, new Val(e, 'u', 4), 0b1111, RepeatOffset(dest, r, RepeatMode.Slmsi));
        }
    }

    void I16mad(UsseInst i)
    {
        // "10100ppasnredbck-tttmmffoolhhgiijjqquuuuuuuvvvvvvvwwwwwwwxxxxxxx"
        uint mode = i['m'];
        var dt = mode <= 1 ? DataType.UInt16 : DataType.Int16;
        bool signed = mode >= 2;
        var dest = DecodeDest(3, i['u'], i['i'], i.Bit('d'), false, 7);
        var src0 = DecodeSrc0(0, i['v'], i['g'], false, false, 7);
        var src1 = DecodeSrc12(1, i['w'], i['j'], i.Bit('c'), false, 7);
        var src2 = DecodeSrc12(2, i['x'], i['q'], i.Bit('k'), false, 7);
        dest.Type = src0.Type = src1.Type = src2.Type = dt;
        int m1 = 1, m2 = 1;
        if (i['o'] != 0)
        {
            if (i['o'] == 1) src1.Type = signed ? DataType.Int8 : DataType.UInt8;
            if (i.Bit('e')) m1 = 0b10;
        }
        if (i['f'] != 0)
        {
            if (i['f'] == 1) src2.Type = signed ? DataType.Int8 : DataType.UInt8;
            if (i.Bit('l')) m2 = 0b10;
        }
        int repeats = (int)i['t'] + 1;
        for (int r = 0; r < repeats; r++)
        {
            var a = Load(src0, 1, RepeatOffset(src0, r, RepeatMode.Slmsi));
            var b = Load(src1, m1, RepeatOffset(src1, r, RepeatMode.Slmsi));
            var c = Load(src2, m2, RepeatOffset(src2, r, RepeatMode.Slmsi));
            string e = signed ? $"(int({a}) * int({b}) + int({c}))" : $"(uint({a}) * uint({b}) + uint({c}))";
            Store(dest, new Val(e, signed ? 'i' : 'u', 1), 1, RepeatOffset(dest, r, RepeatMode.Slmsi));
        }
    }

    void I32mad(UsseInst i)
    {
        // "10101pps-nrcdeba0tttif00yy000kgghhjjlllllllmmmmmmmoooooooqqqqqqq"
        bool signed = i.Bit('i');
        var dest = DecodeDest(3, i['l'], i['g'], i.Bit('d'), false, 7);
        var src0 = DecodeSrc0(0, i['m'], i['k'], false, false, 7);
        var src1 = DecodeSrc12(1, i['o'], i['h'], i.Bit('b'), false, 7);
        var src2 = DecodeSrc12(2, i['q'], i['j'], i.Bit('a'), false, 7);
        var dt32 = signed ? DataType.Int32 : DataType.UInt32;
        var dt16 = signed ? DataType.Int16 : DataType.UInt16;
        dest.Type = dt32; src0.Type = dt16; src1.Type = dt16;
        src2.Type = i['y'] == 2 ? dt32 : dt16;
        int m0 = i.Bit('s') ? 0b10 : 1, m1 = i.Bit('r') ? 0b10 : 1, m2 = i['y'] != 2 && i.Bit('c') ? 0b10 : 1;
        SetRepeatMultiplier(1, 1, 1, 1);
        int repeats = (int)i['t'] + 1;
        for (int r = 0; r < repeats; r++)
        {
            var a = Load(src0, m0, RepeatOffset(src0, r, RepeatMode.Slmsi));
            var b = Load(src1, m1, RepeatOffset(src1, r, RepeatMode.Slmsi));
            var c = Load(src2, m2, RepeatOffset(src2, r, RepeatMode.Slmsi));
            string e = signed ? $"(int({a}) * int({b}) + int({c}))" : $"(uint({a}) * uint({b}) + uint({c}))";
            Store(dest, new Val(e, signed ? 'i' : 'u', 1), 1, RepeatOffset(dest, r, RepeatMode.Slmsi));
        }
        ResetRepeatMultiplier();
    }

    void I32mad2(UsseInst i)
    {
        // "11010ppp-nssdercbooo00iga0000kttffhhjjjjjjjlllllllmmmmmmmqqqqqqq"
        bool signed = i.Bit('i');
        var dest = DecodeDest(3, i['j'], i['t'], i.Bit('d'), false, 7);
        var src0 = DecodeSrc0(0, i['l'], i['k'], i.Bit('b'), false, 7);
        var src1 = DecodeSrc12(1, i['m'], i['f'], i.Bit('r'), false, 7);
        var src2 = DecodeSrc12(2, i['q'], i['h'], i.Bit('c'), false, 7);
        var dt = signed ? DataType.Int32 : DataType.UInt32;
        dest.Type = src0.Type = src1.Type = src2.Type = dt;
        if (i.Bit('g')) src1.Flags |= OpFlags.Neg;
        if (i.Bit('a')) src2.Flags |= OpFlags.Neg;
        SetRepeatMultiplier(1, 1, 1, 1);
        int repeats = (int)i['o'] + 1;
        for (int r = 0; r < repeats; r++)
        {
            var a = Load(src0, 1, RepeatOffset(src0, r, RepeatMode.Slmsi));
            var b = Load(src1, 1, RepeatOffset(src1, r, RepeatMode.Slmsi));
            var c = Load(src2, 1, RepeatOffset(src2, r, RepeatMode.Slmsi));
            // 32-bit x 16-bit + 32-bit: only the low half of src1 takes part. Shaders pack 16-bit coordinates into
            // registers whose upper half still holds unrelated bits and use them directly as src1 (row index * stride),
            // while src0 carries full 32-bit values (pixel index * bytes per pixel).
            string b16 = signed ? $"bitfieldExtract(int({b}), 0, 16)" : $"({b} & 0xFFFFu)";
            var res = i['s'] == 0 ? new Val($"({a} * {b16} + {c})", a.K, 1) : c;
            Store(dest, res, 1, RepeatOffset(dest, r, RepeatMode.Slmsi));
        }
        ResetRepeatMultiplier();
    }

    // ===================================================================== special

    void Depthf(UsseInst i)
    {
        // "11111011s-11recb----npp---tffa--kkddggggggghhhhhhhiiiiiiijjjjjjj"
        var src0 = DecodeSrc0(0, i['h'], i['a'], i.Bit('r'), true, 7);
        UsesFragDepth = true;
        Line($"gl_FragDepth = {Load(src0, 1)};");
    }

    void Smlsi(UsseInst i)
    {
        // "11111010--01-n--ttttppppssssdrcieeeeeeeeaaaaaaaabbbbbbbbffffffff"
        void Parse(int idx, bool mode, uint value)
        {
            if (mode) for (int k = 0; k < 4; k++) _repeatIncrease[idx, k] = (int)((value >> (2 * k)) & 3);
            else for (int k = 0; k < 17; k++) _repeatIncrease[idx, k] = k * (sbyte)(byte)value;
        }
        Parse(3, i.Bit('d'), i['e']);
        Parse(0, i.Bit('r'), i['a']);
        Parse(1, i.Bit('c'), i['b']);
        Parse(2, i.Bit('i'), i['f']);
    }

    void Smbo(UsseInst i)
    {
        // "11111011--01-n--ddddddddddddssssssssssssrrrrrrrrrrrrcccccccccccc"
        void Parse(int idx, uint off) { for (int k = 0; k < 17; k++) _repeatIncrease[idx, k] = k + (int)off; }
        Parse(3, i['d']);
        Parse(0, i['s']);
        Parse(1, i['r']);
        Parse(2, i['c']);
    }
}
