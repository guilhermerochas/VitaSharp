using static VitaSharp.Core.Cpu.Arm;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Diagnostics: splits interpreter callouts by encoding class (op plus the bits that select the instruction within
/// the handler: NEON A/B/U/size fields, load/store type, exclusive size), so the report says which forms are worth
/// translating inline next. <see cref="Hits"/> is indexed by <c>op * Sub + Of(op, ins)</c>.
/// </summary>
public static class CalloutClasses
{
    public const int Sub = 256;
    public static readonly long[] Hits = new long[(int)Op.Count * Sub];

    public static int Of(int op, uint ins) => (Op)op switch
    {
        Op.Neon_ThreeSame => (int)((Bits(ins, 11, 8) << 4) | (Bit(ins, 4) << 3) | (Bit(ins, 24) << 2) | Bits(ins, 21, 20)),
        Op.Neon_ThreeDiff => (int)((Bits(ins, 11, 8) << 1) | Bit(ins, 24)),
        Op.Neon_Scalar => (int)((Bits(ins, 11, 8) << 1) | Bit(ins, 24)),
        Op.Neon_Shift => (int)((Bits(ins, 11, 8) << 2) | (Bit(ins, 24) << 1) | Bit(ins, 7)),
        Op.Neon_Misc => (int)((Bits(ins, 17, 16) << 4) | Bits(ins, 10, 7)),
        Op.Neon_LoadStore => (int)((Bit(ins, 23) << 5) | (Bit(ins, 21) << 4) | Bits(ins, 11, 8)),
        Op.Neon_Vtbl => (int)(Bit(ins, 6)),
        Op.A32_Exclusive => (int)((Bits(ins, 22, 21) << 1) | Bit(ins, 20)),
        Op.T32_ExclusiveOther => (int)((Bits(ins, 6, 4) << 1) | Bit(ins, 20)),
        _ => 0,
    };

    /// <summary>Human-readable name of a class (mnemonic family where the fields identify one).</summary>
    public static string Describe(int op, int sub)
    {
        string n = ((Op)op).ToString();
        uint s = (uint)sub;
        switch ((Op)op)
        {
            case Op.Neon_ThreeSame:
            {
                uint a = s >> 4, b = (s >> 3) & 1, u = (s >> 2) & 1, size = s & 3;
                string m = (a, b) switch
                {
                    (0, 0) => "VHADD", (0, 1) => "VQADD", (1, 0) => "VRHADD", (1, 1) => "bitwise", (2, 0) => "VHSUB", (2, 1) => "VQSUB",
                    (3, 0) => "VCGT", (3, 1) => "VCGE", (4, 0) => "VSHL.reg", (4, 1) => "VQSHL.reg", (5, 0) => "VRSHL", (5, 1) => "VQRSHL",
                    (6, 0) => "VMAX", (6, 1) => "VMIN", (7, 0) => "VABD", (7, 1) => "VABA", (8, 0) => u == 0 ? "VADD" : "VSUB",
                    (8, 1) => u == 0 ? "VTST" : "VCEQ", (9, 0) => u == 0 ? "VMLA" : "VMLS", (9, 1) => "VMUL", (10, 0) => "VPMAX",
                    (10, 1) => "VPMIN", (11, 0) => u == 0 ? "VQDMULH" : "VQRDMULH", (11, 1) => "VPADD",
                    (12, _) => "VFMA/VFMS.F32",
                    (13, 0) => u == 0 ? (size >= 2 ? "VSUB.F32" : "VADD.F32") : (size >= 2 ? "VABD.F32" : "VPADD.F32"),
                    (13, 1) => u == 0 ? (size >= 2 ? "VMLS.F32" : "VMLA.F32") : "VMUL.F32",
                    (14, 0) => u == 0 ? "VCEQ.F32" : (size >= 2 ? "VCGT.F32" : "VCGE.F32"),
                    (14, 1) => size >= 2 ? "VACGT.F32" : "VACGE.F32",
                    (15, 0) => u == 0 ? (size >= 2 ? "VMIN.F32" : "VMAX.F32") : (size >= 2 ? "VPMIN.F32" : "VPMAX.F32"),
                    _ => size >= 2 ? "VRSQRTS.F32" : "VRECPS.F32",
                };
                return $"{n}.{m}{(a >= 12 ? "" : $".{(u != 0 ? "U" : "S")}{8 << (int)size}")}";
            }
            case Op.Neon_ThreeDiff:
            {
                string[] names = ["VADDL", "VADDW", "VSUBL", "VSUBW", "VADDHN", "VABAL", "VSUBHN", "VABDL", "VMLAL", "VQDMLAL", "VMLSL", "VQDMLSL", "VMULL", "VQDMULL", "VMULL.P", "?"];
                return $"{n}.{names[s >> 1]}{((s & 1) != 0 ? ".U" : "")}";
            }
            case Op.Neon_Scalar:
            {
                string[] names = ["VMLA", "VMLA.F32", "VMLAL", "VQDMLAL", "VMLS", "VMLS.F32", "VMLSL", "VQDMLSL", "VMUL", "VMUL.F32", "VMULL", "VQDMULL", "VQDMULH", "VQRDMULH", "?", "?"];
                return $"{n}.{names[s >> 1]}{((s & 1) != 0 ? ".Q/U" : "")}";
            }
            case Op.Neon_Shift:
            {
                uint a = s >> 2, u = (s >> 1) & 1, l = s & 1;
                string m = a switch
                {
                    0 => "VSHR", 1 => "VSRA", 2 => "VRSHR", 3 => "VRSRA", 4 => u != 0 ? "VSRI" : "?", 5 => u != 0 ? "VSLI" : "VSHL",
                    6 => "VQSHLU", 7 => "VQSHL", 8 => u != 0 ? "VQSHRUN/VQRSHRUN" : "VSHRN/VRSHRN", 9 => "VQSHRN/VQRSHRN", 10 => "VSHLL/VMOVL",
                    14 or 15 => "VCVT.fixed", _ => "?",
                };
                return $"{n}.{m}.{(u != 0 ? "U" : "S")}{(l != 0 ? "64" : "")}";
            }
            case Op.Neon_Misc:
            {
                uint a = s >> 4, op2 = s & 0xF;
                string m = a switch
                {
                    0 => op2 switch { 0 => "VREV64", 1 => "VREV32", 2 => "VREV16", 4 or 5 => "VPADDL", 8 => "VCLS", 9 => "VCLZ", 0xA => "VCNT", 0xB => "VMVN", 0xC or 0xD => "VPADAL", 0xE => "VQABS", 0xF => "VQNEG", _ => "?" },
                    1 => (op2 & 7) switch { 0 => "VCGT#0", 1 => "VCGE#0", 2 => "VCEQ#0", 3 => "VCLE#0", 4 => "VCLT#0", 6 => "VABS", 7 => "VNEG", _ => "?" } + ((op2 & 8) != 0 ? ".F32" : ""),
                    2 => op2 switch { 0 => "VSWP", 1 => "VTRN", 2 => "VUZP", 3 => "VZIP", 4 => "VMOVN/VQMOVUN", 5 => "VQMOVN", 6 => "VSHLL.max", 0xC => "VCVT.F16.F32", 0xE => "VCVT.F32.F16", _ => "?" },
                    _ => (op2 & 0xC) == 8 ? ((op2 & 1) != 0 ? "VRSQRTE" : "VRECPE") + ((op2 & 2) != 0 ? ".F32" : ".U32") : "VCVT.int",
                };
                return $"{n}.{m}";
            }
            case Op.Neon_LoadStore:
            {
                bool lanes = (s >> 5) != 0, load = ((s >> 4) & 1) != 0;
                uint t = s & 0xF;
                string v = load ? "VLD" : "VST";
                if (!lanes)
                {
                    string m = t switch { 7 or 0xA or 6 or 2 => "1", 8 or 9 or 3 => "2", 4 or 5 => "3", 0 or 1 => "4", _ => "?" };
                    return $"{n}.{v}{m}.multiple";
                }
                if ((t >> 2) == 3) return $"{n}.{v}{(t & 3) + 1}.alllanes";
                return $"{n}.{v}{(t & 3) + 1}.lane.{8 << (int)(t >> 2)}";
            }
            case Op.A32_Exclusive:
            {
                string[] sz = ["", "D", "B", "H"];
                return $"{n}.{((s & 1) != 0 ? "LDREX" : "STREX")}{sz[s >> 1]}";
            }
            case Op.T32_ExclusiveOther:
            {
                uint o = s >> 1;
                return $"{n}.{((s & 1) != 0 ? "LDREX" : "STREX")}{(o == 4 ? "B" : o == 5 ? "H" : "D")}";
            }
            default: return n;
        }
    }

    /// <summary>The hottest classes, "name count" (counts in thousands).</summary>
    public static IEnumerable<string> Top(int count)
    {
        var h = Hits;
        return Enumerable.Range(0, h.Length).Where(i => h[i] > 0).OrderByDescending(i => h[i]).Take(count)
            .Select(i => $"{Describe(i / Sub, i % Sub)} {h[i] / 1000}K");
    }
}
