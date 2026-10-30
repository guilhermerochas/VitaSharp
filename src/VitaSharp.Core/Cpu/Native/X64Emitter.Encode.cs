using Iced.Intel;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Fast encoding of a prepared block. Iced's BlockEncoder (behind Assembler.Assemble) allocates a few objects per
/// instruction and re-encodes everything for each branch-size pass; the IR only has forward branches to labels plus
/// rel32 jumps/calls to absolute runtime addresses and no RIP-relative operands, so a simpler scheme does: every other
/// instruction is encoded once with a reused <see cref="Encoder"/> (its bytes do not depend on the address), label
/// branches start short and grow to near form until the layout is stable, then branches are encoded at their final
/// address. Anything unexpected (a RIP-relative operand, an out-of-range absolute target, a size surprise) falls back
/// to Iced's BlockEncoder, which stays the reference.
/// </summary>
public sealed unsafe partial class X64Emitter
{
    /// <summary>Use the fast encoder (tests compare it with Iced's BlockEncoder by switching this off).</summary>
    public static bool FastEncode = true;

    /// <summary>Blocks the fast encoder handed to Iced's BlockEncoder (diagnostics; expected 0).</summary>
    public static long FastEncodeFallbacks;

    sealed class ByteWriter : CodeWriter
    {
        public byte[] Buf = new byte[8192];
        public int Pos;
        public override void WriteByte(byte value)
        {
            if (Pos == Buf.Length) Array.Resize(ref Buf, Buf.Length * 2);
            Buf[Pos++] = value;
        }
    }

    ByteWriter? _fw;
    Encoder? _enc;
    int[] _fOff = new int[256], _fLen = new int[256], _fTarget = new int[256];
    ulong[] _fAddr = new ulong[256];
    bool[] _fShort = new bool[256];
    readonly Dictionary<ulong, int> _labelAt = new();

    const int NotBranch = -2, AbsoluteTarget = -1;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    bool TryFastAssemble(ulong rip, out byte[] code)
    {
        code = [];
        var list = _a.Instructions;
        int n = list.Count;
        if (_fOff.Length < n + 1)
        {
            int m = Math.Max(n + 1, _fOff.Length * 2);
            _fOff = new int[m]; _fLen = new int[m]; _fTarget = new int[m]; _fAddr = new ulong[m]; _fShort = new bool[m];
        }
        var fw = _fw ??= new ByteWriter();
        var enc = _enc ??= Encoder.Create(64, fw);
        fw.Pos = 0;
        _labelAt.Clear();
        // Iced's assembler stores a label's id in the IP of the instruction it labels
        for (int i = 0; i < n; i++) { ulong ip = list[i].IP; if (ip != 0) _labelAt[ip] = i; }
        for (int i = 0; i < n; i++)
        {
            var ins = list[i];
            if (ins.IsJmpShortOrNear || ins.IsJccShortOrNear)
            {
                if (_labelAt.TryGetValue(ins.NearBranch64, out int li))
                {
                    if (li <= i) return false; // only forward label branches are expected
                    _fTarget[i] = li; _fShort[i] = true; _fLen[i] = 2;
                }
                else { _fTarget[i] = AbsoluteTarget; _fShort[i] = false; _fLen[i] = ins.IsJmpShortOrNear ? 5 : 6; }
                continue;
            }
            if (ins.IsCallNear) { _fTarget[i] = AbsoluteTarget; _fShort[i] = false; _fLen[i] = 5; continue; }
            if (ins.IsIPRelativeMemoryOperand || ins.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
                return false; // other direct branch forms (jrcxz, loop, ...) are never emitted: leave them to Iced
            _fTarget[i] = NotBranch;
            _fOff[i] = fw.Pos;
            if (!enc.TryEncode(ins, 0, out uint len, out _)) return false;
            _fLen[i] = (int)len;
        }
        // layout: grow short label branches whose displacement does not fit until nothing changes
        ulong end;
        while (true)
        {
            ulong a = rip;
            for (int i = 0; i < n; i++) { _fAddr[i] = a; a += (ulong)_fLen[i]; }
            end = a;
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (_fTarget[i] < 0 || !_fShort[i]) continue;
                long disp = (long)(_fAddr[_fTarget[i]] - (_fAddr[i] + 2));
                if (disp is < sbyte.MinValue or > sbyte.MaxValue)
                {
                    _fShort[i] = false;
                    _fLen[i] = list[i].IsJmpShortOrNear ? 5 : 6;
                    changed = true;
                }
            }
            if (!changed) break;
        }
        // branches at their final addresses
        for (int i = 0; i < n; i++)
        {
            if (_fTarget[i] == NotBranch) continue;
            var ins = list[i];
            if (_fTarget[i] >= 0)
            {
                ins.NearBranch64 = _fAddr[_fTarget[i]];
                if (_fShort[i]) ins.ToShortBranch(); else ins.ToNearBranch();
            }
            else
            {
                ins.ToNearBranch();
                long disp = (long)(ins.NearBranch64 - (_fAddr[i] + (ulong)_fLen[i]));
                if (disp is < int.MinValue or > int.MaxValue) return false;
            }
            int at = fw.Pos;
            if (!enc.TryEncode(ins, _fAddr[i], out uint len, out _) || len != _fLen[i]) return false;
            _fOff[i] = at;
        }
        code = new byte[(int)(end - rip)];
        int p = 0;
        for (int i = 0; i < n; i++)
        {
            Buffer.BlockCopy(fw.Buf, _fOff[i], code, p, _fLen[i]);
            p += _fLen[i];
        }
        return true;
    }
}
