namespace VitaSharp.Core.Gxm.Shader;

/// <summary>Structured control-flow tree built from USSE branches (port of Vita3K's usse_program_analyzer).</summary>
public abstract class CfNode
{
    public CfNode? Parent;
}

public sealed class CfBlock : CfNode
{
    public uint Start;
    public readonly List<CfNode> Children = new();
    public void Add(CfNode? n)
    {
        if (n == null) return;
        if (n is CfCode c && c.Size == 0) return;
        n.Parent = this;
        Children.Add(n);
    }
}

public sealed class CfCode : CfNode
{
    public uint Offset, Size;
    public byte Condition;
}

public sealed class CfBreak(byte cond) : CfNode { public byte Condition = cond; }
public sealed class CfContinue(byte cond) : CfNode { public byte Condition = cond; }

public sealed class CfConditional : CfNode
{
    public uint MergePoint;
    public byte NegIfCondition;
    public CfBlock IfBlock = new();
    public CfBlock? ElseBlock;
}

public sealed class CfLoop : CfNode
{
    public uint LoopEnd;
    public CfBlock Content = new();
}

public static class UsseControlFlow
{
    // ExtPredicate values: 0 NONE, 1 P0, 2 P1, 3 P2, 4 P3, 5 NEGP0, 6 NEGP1, 7 PN
    static byte ExtVecToExt(byte p) => p switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 5, 5 => 6, _ => 0 };
    static byte ShortToExt(byte p) => p switch { 0 => 0, 1 => 1, 2 => 2, 3 => 5, _ => 0 };

    public static bool IsBranch(ulong inst, out byte pred, out int off)
    {
        uint high = (uint)(inst >> 32), low = (uint)inst;
        bool br = ((high & ~0x07FFFFFFu) >> 27 == 0b11111) && (((high & ~0xFFCFFFFFu) >> 20) == 0) && (high & 0x00400000) == 0
                  && ((((high & ~0xFFFFFE3Fu) >> 6) == 0) || ((high & ~0xFFFFFE3Fu) >> 6) == 1);
        pred = 0;
        off = 0;
        if (br)
        {
            off = (int)(low & ((1u << 20) - 1));
            pred = (byte)((high & ~0xF8FFFFFFu) >> 24);
            if ((inst & (1ul << 38)) != 0 && (off & (1 << 19)) != 0) off |= unchecked((int)0xFFF00000);
        }
        return br;
    }

    static bool IsReturn(ulong inst)
    {
        uint high = (uint)(inst >> 32);
        return (high & ~0x07FFFFFFu) >> 27 == 0b11111 && ((high & ~0xFFCFFFFFu) >> 20) == 0 && (high & 0x00400000) == 0 && ((high & ~0xFFFFFE3Fu) >> 6) == 2;
    }

    public static bool WritesPredicate(ulong inst, out byte pred)
    {
        uint op = (uint)(inst >> 59) & 0b11111;
        if (op is 0b01001 or 0b01111)
        {
            pred = (byte)((inst & ~0xFFFFFFF3FFFFFFFFul) >> 34);
            return true;
        }
        pred = 0;
        return false;
    }

    public static byte GetPredicate(ulong inst)
    {
        uint hi = (uint)(inst >> 32);
        switch (inst >> 59)
        {
            case 0b00000: return (byte)((hi & ~0xFCFFFFFFu) >> 24);
            case 0b00001: case 0b00010: case 0b00011: return ExtVecToExt((byte)((hi & ~0xF8FFFFFFu) >> 24));
            case 0b00100: case 0b00101:
                return ((hi & ~0xFCFFFFFFu) >> 24) switch { 0 => (byte)0, 1 => (byte)1, 2 => (byte)5, 3 => (byte)7, _ => (byte)0 };
            case 0b10000: case 0b10001: case 0b10010: case 0b10011: case 0b10100: case 0b10101:
                return ShortToExt((byte)((hi & ~0xF9FFFFFFu) >> 25));
            case 0b11111:
            {
                uint opcat = (uint)(inst >> (32 + 20)) & 3, extra = (uint)(inst >> (32 + 22)) & 1;
                if (opcat == 0)
                {
                    if (extra == 0) break;
                    return 0;
                }
                uint op2 = (uint)(inst >> (32 + 24)) & 7;
                if (opcat == 3 && op2 == 1)
                    return ((hi & ~0xFFFFF9FFu) >> 9) switch { 0 => (byte)0, 1 => (byte)5, 2 => (byte)6, 3 => (byte)1, _ => (byte)0 };
                if (opcat == 2 && op2 == 4) return (byte)((hi & ~0xFFFFF1FFu) >> 9);
                if (opcat == 3 && op2 == 3) return ShortToExt((byte)((hi & ~0xFFFFF9FFu) >> 9));
                return 0;
            }
        }
        return (byte)((hi & ~0xF8FFFFFFu) >> 24);
    }

    record struct Branch(uint Offset, uint Dest, byte Pred);
    record struct Request(uint Begin, uint End, CfBlock Block);

    public static CfBlock Analyze(Func<uint, ulong> read, uint endOffset)
    {
        var root = new CfBlock();
        var back = new List<Branch>();
        var from = new SortedDictionary<uint, Branch>();
        uint returnOffset = 0;
        var calls = new List<uint>();
        for (uint a = 0; a <= endOffset; a++)
        {
            ulong inst = read(a);
            if (IsBranch(inst, out byte pred, out int off))
            {
                uint dest = (uint)(a + off);
                var info = new Branch(a, dest, pred);
                if (dest == 0 && returnOffset > 0) calls.Add(a);
                if (off < 0) back.Add(info);
                from[a] = info;
            }
            if (IsReturn(inst)) returnOffset = a;
        }

        var queue = new Queue<Request>();
        uint start = returnOffset;
        foreach (var call in calls)
        {
            queue.Enqueue(new Request(start, call - 1, root));
            queue.Enqueue(new Request(0, returnOffset, root));
            start = call + 1;
        }
        queue.Enqueue(new Request(start, endOffset, root));

        int guard = 0;
        while (queue.Count > 0 && guard++ < 100000)
        {
            var req = queue.Dequeue();
            var code = new CfCode { Offset = req.Begin };
            bool codePending = true;
            for (uint a = req.Begin; a <= req.End && a != uint.MaxValue; a++)
            {
                ulong inst = read(a);
                if (inst == 0) break;
                byte pred = GetPredicate(inst);
                if (a == code.Offset) code.Condition = pred;

                if (from.TryGetValue(a, out var br))
                {
                    bool loopStmt = false;
                    CfNode? lp = req.Block;
                    while (lp != null && lp is not CfLoop) lp = lp.Parent;
                    if (lp is CfLoop loop)
                    {
                        CfNode? add = null;
                        if (loop.LoopEnd <= br.Dest - 1) add = new CfBreak(br.Pred);
                        else if (loop.Content.Start == br.Dest) add = new CfContinue(br.Pred);
                        if (add != null)
                        {
                            code.Size = a - code.Offset;
                            req.Block.Add(code);
                            req.Block.Add(add);
                            code = new CfCode { Offset = a + 1 };
                            loopStmt = true;
                        }
                    }
                    if (!loopStmt)
                    {
                        if (br.Pred == 0)
                        {
                            code.Size = a - code.Offset;
                            req.Block.Add(code);
                            a = br.Dest - 1;
                            code = new CfCode { Offset = br.Dest };
                        }
                        else
                        {
                            bool elseExist = false;
                            uint elseEnd = 0;
                            if (from.TryGetValue(br.Dest - 1, out var elseBr))
                            {
                                if (elseBr.Dest >= br.Dest) { elseEnd = elseBr.Dest; elseExist = true; }
                                else
                                {
                                    foreach (var kv in from)
                                    {
                                        if (kv.Key <= elseBr.Dest + 1) continue;
                                        if (kv.Key >= br.Dest - 1) break;
                                        if (kv.Value.Dest > br.Dest) { elseExist = true; elseEnd = kv.Value.Dest; }
                                    }
                                }
                            }
                            uint merge = elseExist ? elseEnd : br.Dest;
                            var cond = new CfConditional { MergePoint = merge, NegIfCondition = pred };
                            cond.IfBlock = new CfBlock { Start = a, Parent = cond };
                            queue.Enqueue(new Request(a + 1, Math.Min(br.Dest - 1, req.End), cond.IfBlock));
                            if (elseExist)
                            {
                                cond.ElseBlock = new CfBlock { Start = br.Dest, Parent = cond };
                                queue.Enqueue(new Request(br.Dest, elseEnd - 1, cond.ElseBlock));
                            }
                            code.Size = a - code.Offset;
                            req.Block.Add(code);
                            req.Block.Add(cond);
                            code = new CfCode { Offset = merge };
                            a = merge - 1;
                        }
                    }
                }
                else if (back.Any(b => b.Dest == a) && req.Block.Start != a)
                {
                    uint found = uint.MaxValue;
                    foreach (var b in back)
                        if (b.Dest == a && b.Pred == 0 && (found == uint.MaxValue || found < b.Offset)) found = b.Offset;
                    if (found == uint.MaxValue)
                    {
                        // conditional back edge without an unconditional end: treat the last one as the loop end
                        foreach (var b in back) if (b.Dest == a) found = Math.Max(found == uint.MaxValue ? 0 : found, b.Offset);
                    }
                    var loop = new CfLoop { LoopEnd = found };
                    loop.Content = new CfBlock { Start = a, Parent = loop };
                    queue.Enqueue(new Request(a, found - 1, loop.Content));
                    code.Size = a - code.Offset;
                    req.Block.Add(code);
                    req.Block.Add(loop);
                    code = new CfCode { Offset = found + 1 };
                    a = code.Offset - 1;
                }
                else
                {
                    bool invalidated = false;
                    if (WritesPredicate(inst, out byte pw))
                        invalidated = pw + 1 == code.Condition || pw + 5 == code.Condition;
                    uint offsetEnd = 0;
                    if (pred != code.Condition)
                    {
                        code.Size = a - code.Offset;
                        offsetEnd = a;
                    }
                    else if (invalidated)
                    {
                        code.Size = a + 1 - code.Offset;
                        offsetEnd = a + 1;
                    }
                    if (offsetEnd != 0)
                    {
                        req.Block.Add(code);
                        code = new CfCode { Offset = offsetEnd };
                        a = offsetEnd - 1;
                    }
                }
            }
            if (codePending)
            {
                code.Size = req.End >= code.Offset ? req.End - code.Offset + 1 : 0;
                req.Block.Add(code);
            }
        }
        return root;
    }
}

public sealed partial class UsseTranslator
{
    ulong[] _code = [];

    string Condition(byte pred, bool neg = false)
    {
        int num = 0;
        bool doNeg = neg;
        if (pred is >= 1 and <= 4) num = pred - 1;
        else if (pred is >= 5 and <= 6) { num = pred - 5; doNeg = !doNeg; }
        return doNeg ? $"!p[{num}]" : $"p[{num}]";
    }

    /// <summary>Translates a complete USSE program (primary or secondary phase) into GLSL statements.</summary>
    public void TranslateProgram(ulong[] code, bool secondary)
    {
        SecondProgram = secondary;
        ResetRepeat();
        _code = code;
        if (code.Length == 0) return;
        var root = UsseControlFlow.Analyze(a => a < code.Length ? code[a] : 0, (uint)code.Length - 1);
        EmitBlock(root);
    }

    void EmitBlock(CfBlock b)
    {
        foreach (var n in b.Children)
        {
            switch (n)
            {
                case CfCode c: EmitCode(c); break;
                case CfBreak br:
                    if (br.Condition != 0) Line($"if ({Condition(br.Condition)}) break;"); else Line("break;");
                    break;
                case CfContinue ct:
                    if (ct.Condition != 0) Line($"if ({Condition(ct.Condition)}) continue;"); else Line("continue;");
                    break;
                case CfConditional cd:
                    Open($"if ({Condition(cd.NegIfCondition, true)})");
                    EmitBlock(cd.IfBlock);
                    if (cd.ElseBlock != null)
                    {
                        _indent--;
                        Line("} else {");
                        _indent++;
                        EmitBlock(cd.ElseBlock);
                    }
                    Close();
                    break;
                case CfLoop lp:
                    Open("for (int loop_guard = 0; loop_guard < 65536; loop_guard++)");
                    EmitBlock(lp.Content);
                    Close();
                    break;
            }
        }
    }

    void EmitCode(CfCode c)
    {
        if (c.Size == 0) return;
        bool cond = c.Condition != 0 && !(c.Size == 1 && (_code[c.Offset] >> 59) == 0b10001);
        if (cond) Open($"if ({Condition(c.Condition)})");
        for (uint pc = c.Offset; pc < c.Offset + c.Size && pc < _code.Length; pc++)
        {
            var ins = UsseDecoder.Decode(_code[pc]);
            Line($"// {pc}: {ins.Op} {_code[pc]:X16}");
            Translate(ins);
        }
        if (cond) Close();
    }
}
