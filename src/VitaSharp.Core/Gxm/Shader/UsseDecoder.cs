namespace VitaSharp.Core.Gxm.Shader;

public enum UsseOp
{
    Invalid, VMAD2, V32NMAD, V16NMAD, VMAD, VDP, VDUAL, VCOMP, VMOV, VPCK, VTST, VTSTMSK, VBW, SOP2, SOP2M, SOP3,
    I8MAD, I16MAD, I32MAD, Illegal, I8MAD2, I32MAD2, SMP, PHAS, NOP, BR, SMLSI, SMBO, KILL, LIMM, DEPTHF, SPEC, VLDST,
}

/// <summary>A decoded 64-bit USSE instruction: opcode plus named bit-fields (by pattern letter).</summary>
public readonly struct UsseInst(UsseOp op, ulong raw, UssePattern? pattern)
{
    public UsseOp Op { get; } = op;
    public ulong Raw { get; } = raw;
    readonly UssePattern? _pattern = pattern;

    /// <summary>Value of the field identified by <paramref name="letter"/> in the instruction's bit pattern.</summary>
    public uint this[char letter] => _pattern?.Field(Raw, letter) ?? 0;
    public bool Bit(char letter) => this[letter] != 0;
}

public sealed class UssePattern
{
    public UsseOp Op { get; }
    public ulong Mask { get; }
    public ulong Expected { get; }
    readonly int[][] _fieldBits = new int[128][];

    public UssePattern(UsseOp op, string bits)
    {
        if (bits.Length != 64) throw new ArgumentException($"{op}: pattern must be 64 bits");
        Op = op;
        var positions = new Dictionary<char, List<int>>();
        for (int i = 0; i < 64; i++)
        {
            char c = bits[i];
            int bit = 63 - i;
            if (c is '0' or '1')
            {
                Mask |= 1ul << bit;
                if (c == '1') Expected |= 1ul << bit;
            }
            else if (c != '-')
            {
                if (!positions.TryGetValue(c, out var l)) positions[c] = l = new List<int>();
                l.Add(bit);
            }
        }
        foreach (var (c, l) in positions) _fieldBits[c] = l.ToArray();
    }

    public bool Matches(ulong raw) => (raw & Mask) == Expected;

    public uint Field(ulong raw, char letter)
    {
        var bits = _fieldBits[letter];
        if (bits == null) return 0;
        uint v = 0;
        foreach (int b in bits) v = (v << 1) | (uint)((raw >> b) & 1);
        return v;
    }
}

/// <summary>USSE instruction table (bit patterns from Vita3K's usse_translator_entry.cpp).</summary>
public static class UsseDecoder
{
    static readonly UssePattern[] Table =
    [
        new(UsseOp.VMAD2, "00000dpps-ry-cbawwwineeeemmookttffgghhhhhhzzjjllllllqqqqqquuuuuu"),
        new(UsseOp.V32NMAD, "00001pppsrrydcbawwwwneeeemmoiittkkllffffffzzzzzzzggghhhhhhjjjjjj"),
        new(UsseOp.V16NMAD, "00010pppsrrydcbawwwwneeeemmoiittkkllffffffzzzzzzzggghhhhhhjjjjjj"),
        new(UsseOp.VMAD, "00011pppsg1oderaaittnwwwwcbfhzkkjjllmmmmmmqqqquuuuvvxyAAAABBBBBB"),
        new(UsseOp.VDP, "00011pppsc0oderaagttnwwwwbflllkkhhiijjjjjjzzzzmmmqqqyyyxxxuuuuuu"),
        new(UsseOp.VDUAL, "0010cgsskdtpuuuunaaalriiiiwwwwmmffeebbbbbbbooohhjqvvxxyyyzzzzzzz"),
        new(UsseOp.VCOMP, "00110pppsddyenr-aaaaobbccmmff-ttkk--ggggggg-------hhhhhhh---wwww"),
        new(UsseOp.VMOV, "00111pppstrydecbmmaanoooiwwwwkllffgghhhhjjjjjjqqqqqquuuuuuvvvvvv"),
        new(UsseOp.VPCK, "01000pppsnuydercaaaaffftttmmmmbbkkllgggggggoohiijjqqqqqqvwwwwwwx"),
        new(UsseOp.VTST, "01001ppps-oydrceavttiizzmhhhnnbbkkffgggggggwlluuuujjjjjjjqqqqqqq"),
        new(UsseOp.VTSTMSK, "01111ppps-oydtrcevuuiizzm-aa--bbnnkkfffffffwllgggghhhhhhhjjjjjjj"),
        new(UsseOp.VBW, "01ooopppsnrydecxaaaaittttthhbwkkffggjjjjjjjlllllllmmmmmmmqqqqqqq"),
        new(UsseOp.SOP2, "10000ppcsnaaderbmooofllggghhhittkkjjqqqqqqquvvwwxyzzzzzzzAAAAAAA"),
        new(UsseOp.SOP2M, "10010ppmsnccderbowwwwaalllfff-ttkkgguuuuuuu-------hhhhhhhiiiiiii"),
        new(UsseOp.SOP3, "10001ppcsnooderbmallfgghhhiiikttjjqquuuuuuuvvvvvvvwwwwwwwxxxxxxx"),
        new(UsseOp.I8MAD, "10011ppcsneedarbmtttuolfghijkqvvwwxxyyyyyyyzzzzzzzAAAAAAABBBBBBB"),
        new(UsseOp.I16MAD, "10100ppasnredbck-tttmmffoolhhgiijjqquuuuuuuvvvvvvvwwwwwwwxxxxxxx"),
        new(UsseOp.I32MAD, "10101pps-nrcdeba0tttif00yy000kgghhjjlllllllmmmmmmmoooooooqqqqqqq"),
        new(UsseOp.Illegal, "10110-----------------------------------------------------------"),
        new(UsseOp.Illegal, "10111-----------------------------------------------------------"),
        new(UsseOp.Illegal, "11000-----------------------------------------------------------"),
        new(UsseOp.I8MAD2, "11001-----------------------------------------------------------"),
        new(UsseOp.I32MAD2, "11010ppp-nssdercbooo00iga0000kttffhhjjjjjjjlllllllmmmmmmmqqqqqqq"),
        new(UsseOp.Illegal, "11011-----------------------------------------------------------"),
        new(UsseOp.SMP, "11100pppsn-ymrceffaaddlltbbggkhhiijjoooooooqqqqqqquuuuuuuvvvvvvv"),
        new(UsseOp.PHAS, "11111010s100eirc--matwwwppppppppbbnn--------xxxxxxoooooooddddddd"),
        new(UsseOp.NOP, "11111----000-----------101--------------------------------------"),
        new(UsseOp.BR, "11111ppps000e-----wynba00r----------------iloooooooooooooooooooo"),
        new(UsseOp.SMLSI, "11111010--01-n--ttttppppssssdrcieeeeeeeeaaaaaaaabbbbbbbbffffffff"),
        new(UsseOp.SMBO, "11111011--01-n--ddddddddddddssssssssssssrrrrrrrrrrrrcccccccccccc"),
        new(UsseOp.KILL, "11111001--11000000000pp0000001101111----------------------------"),
        new(UsseOp.LIMM, "11111100sn10deiiiiiipppmmmmm--tt----uuuuuuuvvvvvvvvvvvvvvvvvvvvv"),
        new(UsseOp.DEPTHF, "11111011s-11recb----npp---tffa--kkddggggggghhhhhhhiiiiiiijjjjjjj"),
        new(UsseOp.SPEC, "11111----scc----------------------------------------------------"),
        new(UsseOp.VLDST, "111oopppsnmycrbakkkkddeetgffihjlqquuvvvvvvvwwwwwwwxxxxxxxzzzzzzz"),
    ];

    public static UsseInst Decode(ulong raw)
    {
        foreach (var p in Table)
            if (p.Matches(raw)) return new UsseInst(p.Op, raw, p);
        return new UsseInst(UsseOp.Invalid, raw, null);
    }
}
