using System.Globalization;
using System.Numerics;
using System.Text;

namespace VitaSharp.Core.Gxm.Shader;

public enum Bank { Temp, PrimAttr, Output, SecAttr, FpInternal, Special, Global, FpConstant, Immediate, Index, Indexed1, Indexed2, Predicate, Invalid }

public enum Sw : byte { X, Y, Z, W, Zero, One, Two, Half, Undef }

[Flags]
public enum OpFlags { None = 0, Abs = 1, Neg = 2, Gpi = 4 }

public struct Operand
{
    public int Num;
    public Bank Bank;
    public OpFlags Flags;
    public Sw[] Swizzle;
    public DataType Type;
    public int Index; // 0..2 = src0..src2, 3 = dest (repeat increment tables)

    public static Operand Make(int index) => new() { Num = 0, Bank = Bank.Invalid, Swizzle = [Sw.X, Sw.Y, Sw.Z, Sw.W], Type = DataType.F32, Index = index };

    public readonly bool IsSame(in Operand o, int mask)
    {
        if (o.Bank != Bank || o.Num != Num) return false;
        for (int i = 0; i < 4; i++) if ((mask & (1 << i)) != 0 && Swizzle[i] != o.Swizzle[i]) return false;
        return true;
    }
}

/// <summary>A typed GLSL expression: kind f/i/u/b and component count 1..4.</summary>
public readonly record struct Val(string E, char K, int N)
{
    public string GlslType => K switch
    {
        'f' => N == 1 ? "float" : $"vec{N}",
        'i' => N == 1 ? "int" : $"ivec{N}",
        'u' => N == 1 ? "uint" : $"uvec{N}",
        _ => N == 1 ? "bool" : $"bvec{N}",
    };
    public override string ToString() => E;
}

/// <summary>
/// USSE â†’ GLSL translator. The register model follows Vita3K: every bank is an array of vec4 holding raw
/// 32-bit words; typed loads/stores pack and unpack f16/u16/u8/c10 data inside those words.
/// </summary>
public sealed partial class UsseTranslator
{
    readonly GxpProgram _program;
    readonly StringBuilder _sb = new();
    int _indent = 1;
    int _tmp;
    public bool SecondProgram { get; set; }
    public int TextureBufferSaOffset { get; set; } = -1;
    public int LiteralBufferSaOffset { get; set; } = -1;
    public int ThreadBufferSaOffset { get; set; } = -1;
    public int TextureBufferBase { get; set; }
    public int LiteralBufferBase { get; set; }
    public int ThreadBufferBase { get; set; }
    public bool UsesFragDepth { get; private set; }
    public bool UsesThreadBuffer { get; private set; }
    public readonly Dictionary<int, SamplerUse> Samplers = new(); // keyed by SA offset (dependent) or REG_SA_COUNT+index
    public readonly HashSet<string> Warnings = new();

    // repeat state (SMLSI / SMBO can modify the increment tables)
    readonly int[,] _repeatIncrease = new int[4, 17];
    readonly int[] _repeatMultiplier = new int[4];

    public int MaxTemp, MaxPa, MaxSa, MaxOut;

    public sealed record SamplerUse(string GlslName, uint Index, bool IsCube, DataType ComponentType, int ComponentCount);

    public UsseTranslator(GxpProgram program)
    {
        _program = program;
        ResetRepeat();
    }

    public void ResetRepeat()
    {
        for (int i = 0; i < 4; i++) for (int j = 0; j < 17; j++) _repeatIncrease[i, j] = j;
        ResetRepeatMultiplier();
    }

    void ResetRepeatMultiplier() { for (int i = 0; i < 4; i++) _repeatMultiplier[i] = 2; }
    void SetRepeatMultiplier(int a, int b, int c, int d) { _repeatMultiplier[0] = a; _repeatMultiplier[1] = b; _repeatMultiplier[2] = c; _repeatMultiplier[3] = d; }

    public string Body => _sb.ToString();
    public void Line(string s) => _sb.Append(' ', _indent * 4).AppendLine(s);
    public void Open(string s) { Line(s + " {"); _indent++; }
    public void Close() { _indent--; Line("}"); }
    string Tmp() => "t" + (_tmp++).ToString(CultureInfo.InvariantCulture);

    public static string F(float f)
    {
        if (float.IsNaN(f)) return "uintBitsToFloat(0x7FC00000u)";
        if (float.IsPositiveInfinity(f)) return "uintBitsToFloat(0x7F800000u)";
        if (float.IsNegativeInfinity(f)) return "uintBitsToFloat(0xFF800000u)";
        // denormals (often packed f16/u8 data reinterpreted as f32) would be flushed by the compiler: keep exact bits
        if (float.IsSubnormal(f) || (f == 0 && float.IsNegative(f))) return $"uintBitsToFloat(0x{BitConverter.SingleToUInt32Bits(f):X8}u)";
        var s = f.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e')) s += ".0";
        return s;
    }

    Val Bind(Val v)
    {
        if (IsSimple(v.E)) return v;
        string t = Tmp();
        Line($"{v.GlslType} {t} = {v.E};");
        return v with { E = t };
    }

    static bool IsSimple(string e) => e.Length < 24 && e.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '[' or ']');

    // ------------------------------------------------------------------ register banks

    static string BankName(Bank b) => b switch
    {
        Bank.Temp => "r",
        Bank.PrimAttr => "pa",
        Bank.Output => "o",
        Bank.SecAttr => "sa",
        Bank.FpInternal => "fi",
        _ => "",
    };

    void Track(Bank b, int word)
    {
        int v = word >> 2;
        switch (b)
        {
            case Bank.Temp: MaxTemp = Math.Max(MaxTemp, v); break;
            case Bank.PrimAttr: MaxPa = Math.Max(MaxPa, v); break;
            case Bank.SecAttr: MaxSa = Math.Max(MaxSa, v); break;
            case Bank.Output: MaxOut = Math.Max(MaxOut, v); break;
        }
    }

    const string Comps = "xyzw";

    /// <summary>Expression for raw 32-bit word (as float) at constant word index.</summary>
    string Word(Bank b, int w)
    {
        Track(b, w);
        return $"{BankName(b)}[{w >> 2}].{Comps[w & 3]}";
    }

    /// <summary>Expression for a raw word at a dynamic index.</summary>
    string WordDyn(Bank b, string idx)
    {
        Track(b, 255);
        return $"{BankName(b)}[({idx}) >> 2][({idx}) & 3]";
    }

    static int PackedCount(DataType t) => t == DataType.C10 ? 3 : 4 / GxpProgram.TypeSize(t);

    static char KindOf(DataType t) => GxpProgram.IsSigned(t) ? 'i' : GxpProgram.IsUnsigned(t) ? 'u' : 'f';

    static string ConstOf(Sw sw, char kind)
    {
        float v = sw switch { Sw.Zero => 0, Sw.One => 1, Sw.Two => 2, Sw.Half => 0.5f, _ => 0 };
        return kind switch
        {
            'f' => F(v),
            'i' => ((int)v).ToString(CultureInfo.InvariantCulture),
            _ => ((uint)v).ToString(CultureInfo.InvariantCulture) + "u",
        };
    }

    static Val Compose(List<string> comps, char kind)
    {
        if (comps.Count == 1) return new Val(comps[0], kind, 1);
        string t = kind switch { 'f' => "vec", 'i' => "ivec", 'u' => "uvec", _ => "bvec" };
        return new Val($"{t}{comps.Count}({string.Join(", ", comps)})", kind, comps.Count);
    }

    Val ApplyModifiers(Val v, OpFlags f)
    {
        if ((f & OpFlags.Abs) != 0)
        {
            if (v.K == 'u') return v;
            v = v with { E = $"abs({v.E})" };
        }
        if ((f & OpFlags.Neg) != 0)
            v = v with { E = v.K == 'u' ? $"(0u - {v.E})" : $"(-{v.E})" };
        return v;
    }

    /// <summary>Port of Vita3K utils::load.</summary>
    public Val Load(Operand op, int destMask, int shift = 0)
    {
        if (destMask == 0) destMask = 1;
        char kind = KindOf(op.Type);

        if (op.Bank == Bank.FpConstant)
        {
            var cs = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                if ((destMask & (1 << i)) == 0) continue;
                var sw = op.Swizzle[i];
                if (sw >= Sw.Zero) { cs.Add(ConstOf(sw, kind)); continue; }
                int s = (int)sw;
                if (op.Type is DataType.F32 or DataType.UInt32 or DataType.Int32)
                {
                    var table = s == 1 ? UsseConstants.F32Bank1 : UsseConstants.F32Bank0;
                    uint raw = op.Num < table.Length ? table[op.Num] : 0;
                    cs.Add(kind == 'f' ? $"uintBitsToFloat({raw}u)" : kind == 'u' ? $"{raw}u" : $"int({raw}u)");
                }
                else
                {
                    var table = s switch { 1 => UsseConstants.F16Bank1, 2 => UsseConstants.F16Bank2, 3 => UsseConstants.F16Bank3, _ => UsseConstants.F16Bank0 };
                    float fv = op.Num < table.Length ? table[op.Num] : 0;
                    if (kind == 'f') cs.Add(F(fv));
                    else
                    {
                        ushort h = BitConverter.HalfToUInt16Bits((Half)fv);
                        cs.Add(kind == 'u' ? $"{h}u" : $"{(short)fv}");
                    }
                }
            }
            return ApplyModifiers(Compose(cs, kind), op.Flags);
        }

        if (op.Bank == Bank.FpInternal)
        {
            op.Type = op.Type switch
            {
                DataType.F16 => DataType.F32,
                DataType.Int16 or DataType.Int8 => DataType.Int32,
                DataType.UInt16 or DataType.UInt8 => DataType.UInt32,
                _ => op.Type,
            };
            kind = KindOf(op.Type);
            op.Num <<= 2;
            shift <<= 2;
        }

        if (op.Bank == Bank.Predicate) return new Val($"p[{op.Num}]", 'b', 1);
        if (op.Bank == Bank.Index)
        {
            string e = $"idx[{op.Num - 1}]";
            int sz = GxpProgram.TypeSize(op.Type);
            if (!GxpProgram.IsFloat(op.Type) && sz < 4) e = $"({e} & {(sz == 1 ? 0xFF : 0xFFFF)})";
            return kind == 'u' ? new Val($"uint({e})", 'u', 1) : new Val(e, 'i', 1);
        }

        int count = BitOperations.PopCount((uint)destMask);
        bool indexed = op.Bank is Bank.Indexed1 or Bank.Indexed2;
        if (op.Bank == Bank.Immediate || (!indexed && BankName(op.Bank) == ""))
        {
            if (op.Bank != Bank.Immediate) Warnings.Add($"load from unsupported bank {op.Bank}");
            int imm = op.Bank == Bank.Immediate ? op.Num : 0;
            string c = kind switch { 'f' => F(imm), 'i' => imm.ToString(CultureInfo.InvariantCulture), _ => $"{(uint)imm}u" };
            var cs = new List<string>();
            for (int i = 0; i < 4; i++)
                if ((destMask & (1 << i)) != 0) cs.Add(op.Swizzle[i] >= Sw.Zero ? ConstOf(op.Swizzle[i], kind) : c);
            return ApplyModifiers(Compose(cs, kind), op.Flags);
        }

        string? dynBase = null;
        int baseWord = op.Num + shift;
        Bank bank = op.Bank;
        if (indexed)
        {
            int enc = (op.Num >> 5) & 3;
            bank = enc switch { 0 => Bank.Temp, 1 => Bank.Output, 2 => Bank.PrimAttr, _ => Bank.SecAttr };
            int addOff = (op.Num & 31) + shift;
            int idxReg = op.Bank == Bank.Indexed1 ? 0 : 1;
            dynBase = $"(idx[{idxReg}] * 2 + {addOff})";
        }

        string W(int rel) => dynBase == null ? Word(bank, baseWord + rel) : WordDyn(bank, $"{dynBase} + {rel}");

        var comps = new List<string>();
        int size = GxpProgram.TypeSize(op.Type);
        for (int i = 0; i < 4; i++)
        {
            if ((destMask & (1 << i)) == 0) continue;
            var sw = op.Swizzle[i];
            if (sw >= Sw.Zero) { comps.Add(ConstOf(sw, kind)); continue; }
            int s = (int)sw;
            switch (op.Type)
            {
                case DataType.F32: comps.Add(W(s)); break;
                case DataType.UInt32: comps.Add($"floatBitsToUint({W(s)})"); break;
                case DataType.Int32: comps.Add($"floatBitsToInt({W(s)})"); break;
                case DataType.F16: comps.Add($"unpackHalf2x16(floatBitsToUint({W(s / 2)})).{Comps[s % 2]}"); break;
                case DataType.UInt16: comps.Add($"bitfieldExtract(floatBitsToUint({W(s / 2)}), {16 * (s % 2)}, 16)"); break;
                case DataType.Int16: comps.Add($"bitfieldExtract(floatBitsToInt({W(s / 2)}), {16 * (s % 2)}, 16)"); break;
                case DataType.UInt8: case DataType.O8: comps.Add($"bitfieldExtract(floatBitsToUint({W(s / 4)}), {8 * (s % 4)}, 8)"); break;
                case DataType.Int8: comps.Add($"bitfieldExtract(floatBitsToInt({W(s / 4)}), {8 * (s % 4)}, 8)"); break;
                case DataType.C10: comps.Add($"unpackC10({W(s / 3)}).{Comps[s % 3]}"); break;
                default: comps.Add(W(s)); break;
            }
        }
        if (op.Type == DataType.O8) kind = 'u';
        return ApplyModifiers(Compose(comps, kind), op.Flags);
    }

    static string Comp(Val v, int i) => v.N == 1 ? v.E : $"{Paren(v.E)}.{Comps[Math.Min(i, v.N - 1)]}";
    static string Paren(string e) => IsSimple(e) ? e : $"({e})";

    /// <summary>Port of Vita3K utils::store.</summary>
    public void Store(Operand dest, Val v, int destMask, int off = 0)
    {
        if (destMask == 0) return;
        if (dest.Bank == Bank.Predicate)
        {
            Line($"p[{dest.Num}] = {(v.K == 'b' ? Comp(v, 0) : $"({Comp(v, 0)} != {(v.K == 'f' ? "0.0" : v.K == 'u' ? "0u" : "0")})")};");
            return;
        }
        if (dest.Bank == Bank.Index)
        {
            string e = v.K switch { 'f' => $"floatBitsToInt({Comp(v, 0)})", 'u' => $"int({Comp(v, 0)})", _ => Comp(v, 0) };
            int sz = GxpProgram.TypeSize(dest.Type);
            if (!GxpProgram.IsFloat(dest.Type) && sz < 4) e = $"({e} & {(sz == 1 ? 0xFF : 0xFFFF)})";
            Line($"idx[{dest.Num - 1}] = {e};");
            return;
        }
        bool indexed = dest.Bank is Bank.Indexed1 or Bank.Indexed2;
        Bank bank = dest.Bank;
        string? dynBase = null;
        if (indexed)
        {
            int enc = (dest.Num >> 5) & 3;
            bank = enc switch { 0 => Bank.Temp, 1 => Bank.Output, 2 => Bank.PrimAttr, _ => Bank.SecAttr };
            dynBase = $"(idx[{(dest.Bank == Bank.Indexed1 ? 0 : 1)}] * 2 + {(dest.Num & 31) + off})";
        }
        else if (BankName(bank) == "")
        {
            Warnings.Add($"store to unsupported bank {dest.Bank}");
            return;
        }

        if (bank == Bank.FpInternal)
        {
            dest.Type = GxpProgram.IsInteger(dest.Type) ? DataType.UInt32 : DataType.F32;
            dest.Num <<= 2;
            off <<= 2;
        }

        v = Bind(v);
        int baseWord = dest.Num + off;
        string W(int rel) => dynBase == null ? Word(bank, baseWord + rel) : WordDyn(bank, $"{dynBase} + {rel}");

        // source component cursor
        int taken = 0;
        string NextSrc()
        {
            string c = Comp(v, v.N == 1 ? 0 : taken);
            taken++;
            return c;
        }

        string AsF32Bits(string c) => v.K switch { 'f' => c, 'u' => $"uintBitsToFloat({c})", 'i' => $"intBitsToFloat({c})", _ => $"({c} ? 1.0 : 0.0)" };
        string AsU(string c) => v.K switch { 'f' => $"floatBitsToUint({c})", 'i' => $"uint({c})", 'b' => $"uint({c})", _ => c };
        string AsI(string c) => v.K switch { 'f' => $"floatBitsToInt({c})", 'u' => $"int({c})", 'b' => $"int({c})", _ => c };
        string AsF(string c) => v.K switch { 'f' => c, 'u' => $"float({c})", 'i' => $"float({c})", _ => $"({c} ? 1.0 : 0.0)" };

        switch (dest.Type)
        {
            case DataType.F32:
            case DataType.UInt32:
            case DataType.Int32:
                for (int i = 0; i < 4; i++)
                    if ((destMask & (1 << i)) != 0) Line($"{W(i)} = {AsF32Bits(NextSrc())};");
                return;
            default:
            {
                int n = PackedCount(dest.Type);
                int bits = dest.Type == DataType.C10 ? 10 : 32 / n;
                for (int word = 0; word * n < 4; word++)
                {
                    int any = 0;
                    for (int j = 0; j < n && word * n + j < 4; j++) if ((destMask & (1 << (word * n + j))) != 0) any = 1;
                    if (any == 0) continue;
                    string w = W(word);
                    if (dest.Type == DataType.F16)
                    {
                        string t = Tmp();
                        Line($"vec2 {t} = unpackHalf2x16(floatBitsToUint({w}));");
                        for (int j = 0; j < 2; j++)
                            if ((destMask & (1 << (word * 2 + j))) != 0) Line($"{t}.{Comps[j]} = {AsF(NextSrc())};");
                        Line($"{w} = uintBitsToFloat(packHalf2x16({t}));");
                    }
                    else if (dest.Type == DataType.C10)
                    {
                        string t = Tmp();
                        Line($"vec3 {t} = unpackC10({w});");
                        for (int j = 0; j < 3; j++)
                            if ((destMask & (1 << (word * 3 + j))) != 0) Line($"{t}.{Comps[j]} = {AsF(NextSrc())};");
                        Line($"{w} = packC10({t});");
                    }
                    else
                    {
                        string t = Tmp();
                        Line($"uint {t} = floatBitsToUint({w});");
                        for (int j = 0; j < n; j++)
                        {
                            if ((destMask & (1 << (word * n + j))) == 0) continue;
                            string src = GxpProgram.IsSigned(dest.Type) ? $"uint({AsI(NextSrc())})" : AsU(NextSrc());
                            Line($"{t} = bitfieldInsert({t}, {src}, {j * bits}, {bits});");
                        }
                        Line($"{w} = uintBitsToFloat({t});");
                    }
                }
                return;
            }
        }
    }

    // ------------------------------------------------------------------ conversions

    static float IntNormRange(DataType t) => t switch
    {
        DataType.UInt8 => 255f,
        DataType.Int8 => 127f,
        DataType.C10 => 255f,
        DataType.UInt16 => 65535f,
        DataType.Int16 => 32767f,
        DataType.UInt32 => 4294967295f,
        DataType.Int32 => 2147483647f,
        _ => 1f,
    };

    static string Splat(string scalar, int n, char k) => n == 1 ? scalar : $"{(k == 'f' ? "vec" : k == 'i' ? "ivec" : "uvec")}{n}({scalar})";

    public Val ToFloat(Val v, DataType type, bool normalize)
    {
        if (v.K == 'f') return v;
        string t = v.N == 1 ? "float" : $"vec{v.N}";
        string e = $"{t}({v.E})";
        if (normalize)
        {
            e = $"({e} / {Splat(F(IntNormRange(type)), v.N, 'f')})";
            if (v.K == 'i') e = $"max({e}, {Splat(F(type == DataType.C10 ? -2f : -1f), v.N, 'f')})";
        }
        return new Val(e, 'f', v.N);
    }

    public Val ToInt(Val v, DataType type, bool normalize)
    {
        if (v.K is 'i' or 'u') return v;
        bool uns = GxpProgram.IsUnsigned(type) || type == DataType.O8;
        string e = v.E;
        if (normalize)
        {
            bool fx10 = type == DataType.C10;
            float lo = uns ? 0f : (fx10 ? -2f : -1f), hi = fx10 ? 2f : 1f;
            e = $"round(clamp({e}, {Splat(F(lo), v.N, 'f')}, {Splat(F(hi), v.N, 'f')}) * {Splat(F(IntNormRange(type)), v.N, 'f')})";
        }
        char k = uns ? 'u' : 'i';
        string t = v.N == 1 ? (uns ? "uint" : "int") : $"{(uns ? "uvec" : "ivec")}{v.N}";
        return new Val($"{t}({e})", k, v.N);
    }

    // ------------------------------------------------------------------ operand decoding (usse_decode_helpers)

    static void CheckRegInternal(ref Operand r, bool dbl, int regBits)
    {
        int temps = dbl ? 8 : 4;
        int max = 1 << regBits;
        int limit = max - temps;
        if (r.Bank == Bank.Temp && r.Num >= limit)
        {
            r.Num -= limit;
            if (dbl) r.Num >>= 1;
            r.Bank = Bank.FpInternal;
        }
    }

    void FinalizeRegister(ref Operand r, bool dbl, int regBits)
    {
        CheckRegInternal(ref r, dbl, regBits);
        if (r.Bank == Bank.Special)
        {
            if ((r.Num & 0x40) != 0) { r.Num &= ~0x40; r.Bank = Bank.Global; }
            else r.Bank = Bank.FpConstant;
        }
        if (r.Bank != Bank.FpInternal && r.Bank != Bank.FpConstant && r.Bank != Bank.Immediate && SecondProgram)
            r.Bank = Bank.SecAttr;
    }

    static void DoubleReg(ref int num, Bank b)
    {
        if (b != Bank.Special && b != Bank.Immediate) num = (num << 1) & 0xFF;
    }

    Operand DecodeDest(int index, uint n, uint bank, bool ext, bool dbl, int regBits)
    {
        var o = Operand.Make(index);
        o.Num = (int)n;
        o.Bank = ext
            ? bank switch { 0 => Bank.SecAttr, 1 => Bank.Special, 2 => Bank.Index, _ => Bank.Indexed2 }
            : bank switch { 0 => Bank.Temp, 1 => Bank.Output, 2 => Bank.PrimAttr, _ => Bank.Indexed1 };
        if (dbl) DoubleReg(ref o.Num, o.Bank);
        FinalizeRegister(ref o, dbl, regBits);
        return o;
    }

    Operand DecodeSrc12(int index, uint n, uint bank, bool ext, bool dbl, int regBits)
    {
        var o = Operand.Make(index);
        o.Num = (int)n;
        o.Bank = ext
            ? bank switch { 0 => Bank.Indexed1, 1 => Bank.Special, 2 => Bank.Immediate, _ => Bank.Indexed2 }
            : bank switch { 0 => Bank.Temp, 1 => Bank.Output, 2 => Bank.PrimAttr, _ => Bank.SecAttr };
        if (dbl) DoubleReg(ref o.Num, o.Bank);
        FinalizeRegister(ref o, dbl, regBits);
        return o;
    }

    Operand DecodeSrc0(int index, uint n, uint bank, bool ext, bool dbl, int regBits)
    {
        var o = Operand.Make(index);
        o.Num = (int)n;
        o.Bank = ext ? (bank == 0 ? Bank.Output : Bank.SecAttr) : (bank == 0 ? Bank.Temp : Bank.PrimAttr);
        if (dbl) DoubleReg(ref o.Num, o.Bank);
        FinalizeRegister(ref o, dbl, regBits);
        return o;
    }

    static int DecodeWriteMask(Bank destBank, int mask, bool f16)
    {
        bool f16CanNormal = destBank is not (Bank.PrimAttr or Bank.SecAttr or Bank.Output or Bank.FpInternal or Bank.Temp);
        if (destBank == Bank.FpInternal || (f16 && f16CanNormal)) return mask;
        int nm = 0;
        if (f16)
        {
            if ((mask & 1) != 0) nm |= 0b11;
            if ((mask & 4) != 0) nm |= 0b1100;
        }
        else nm = mask & 0b11;
        return nm;
    }

    static OpFlags DecodeModifier(uint mod) => mod switch
    {
        1 => OpFlags.Neg,
        2 => OpFlags.Abs,
        3 => OpFlags.Neg | OpFlags.Abs,
        _ => OpFlags.None,
    };

    static Sw[] S4(string s) => s.Select(c => c switch
    {
        'X' => Sw.X, 'Y' => Sw.Y, 'Z' => Sw.Z, 'W' => Sw.W, '0' => Sw.Zero, '1' => Sw.One, '2' => Sw.Two, 'H' => Sw.Half, _ => Sw.Undef,
    }).ToArray();

    static Sw[] To4(Sw[] s3) => s3.Length == 4 ? s3 : [s3[0], s3[1], s3[2], Sw.X];

    static readonly string[] V4Std = ["XXXX", "YYYY", "ZZZZ", "WWWW", "XYZW", "YZWW", "XYZZ", "XXYZ", "XYXY", "XYWZ", "ZXYW", "ZWZW", "YZXZ", "XXYY", "XZWW", "XYZ1"];
    static readonly string[] V4Ext = ["YZXW", "ZWXY", "XZWY", "YYWW", "WYZW", "WZWZ", "XYZX", "ZZWW", "XWZX", "YYYX", "YYYZ", "XZYW", "XXXY", "ZYXW", "YYZZ", "ZZZY"];
    static readonly string[] V3Std = ["XXX", "YYY", "ZZZ", "WWW", "XYZ", "YZW", "XXY", "XYX", "YYX", "YYZ", "ZXY", "XZY", "YZX", "ZYX", "ZZY", "XY1"];
    static readonly string[] V3Ext = ["XYY", "YXY", "XXZ", "YXX", "XY0", "X10", "000", "111", "HHH", "222", "X00", "???", "???", "???", "???", "???"];
    static readonly string[] ScalarStd = ["XXXX", "YYYY", "ZZZZ", "WWWW", "0000", "1111", "2222", "HHHH", "????", "????", "????", "????", "????", "????", "????", "????"];

    static Sw[] DecodeVec34Swizzle(uint swz, bool ext, int type) => type switch
    {
        0 => ext ? S4("????") : S4(ScalarStd[swz]),
        1 => To4(S4(ext ? V3Ext[swz] : V3Std[swz])),
        2 => S4(ext ? V4Ext[swz] : V4Std[swz]),
        _ => S4("????"),
    };

    static readonly string[] DualV4Std = ["XXXX", "YYYY", "ZZZZ", "WWWW", "XYZW", "YZWW", "XYZZ", "XXYZ", "XYXY", "XYWZ", "ZXYW", "ZWZW", "0000", "HHHH", "1111", "2222"];
    static readonly string[] DualV4Ext = ["YZXW", "ZWXY", "XZWY", "YYWW", "WYZW", "WZWZ", "XYZX", "ZZWW", "XWZX", "YYYX", "YYYZ", "ZWZW", "YZXZ", "XXYY", "XZWW", "XYZ1"];
    static readonly string[] DualV3Std = ["XXX", "YYY", "ZZZ", "WWW", "XYZ", "YZW", "XXY", "XYX", "YYX", "YYZ", "ZXY", "XZY", "000", "HHH", "111", "222"];
    static readonly string[] DualV3Ext = ["XYY", "YXY", "XXZ", "YXX", "XY0", "X10", "XZY", "YZX", "ZYX", "ZZY", "XY1", "???", "???", "???", "???", "???"];

    static Sw[] DecodeDualSwizzle(uint swz, bool ext, bool vec4)
        => vec4 ? S4(ext ? DualV4Ext[swz] : DualV4Std[swz]) : To4(S4(ext ? DualV3Ext[swz] : DualV3Std[swz]));

    static Sw[] DecodeSwizzle4(uint enc) =>
    [
        (Sw)(enc & 7), (Sw)((enc >> 3) & 7), (Sw)((enc >> 6) & 7), (Sw)((enc >> 9) & 7),
    ];

    static readonly Sw[] Default4 = [Sw.X, Sw.Y, Sw.Z, Sw.W];

    // ------------------------------------------------------------------ repeat offsets

    enum RepeatMode { External, Internal, Both, Slmsi }

    int RepeatOffset(in Operand op, int idx, RepeatMode mode)
    {
        if (mode is RepeatMode.Internal or RepeatMode.Both)
            if (op.Bank == Bank.FpInternal) return idx;
        if ((op.Flags & OpFlags.Gpi) != 0) return 0;
        if (mode == RepeatMode.Both) return op.Bank == Bank.FpInternal ? idx : idx * 4;
        if (mode == RepeatMode.External && op.Bank != Bank.FpInternal) return idx * 4;
        if (mode == RepeatMode.Slmsi)
        {
            int inc = _repeatIncrease[op.Index, Math.Min(idx, 16)];
            if (op.Bank is >= Bank.Temp and <= Bank.SecAttr or Bank.Predicate or Bank.Indexed1 or Bank.Indexed2)
                inc *= _repeatMultiplier[op.Index];
            return inc;
        }
        return 0;
    }
}
