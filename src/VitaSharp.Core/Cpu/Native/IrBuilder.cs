namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Emits IR and caches guest state in temps. Reads of a register/flag reuse the temp that last defined it; writes only
/// update the cache and are stored at flush points: before conditional regions, callouts and exits. Superseded flag
/// values therefore never reach memory and their computations are removed by <see cref="IrPasses.DeadCode"/> (lazy
/// flags). Constant folding happens here too (PC-relative values, immediates).
/// <para>
/// Conditional instructions become regions: the state is flushed, a branch skips the region when the condition
/// fails, and after the region's label the cache keeps only values valid on both paths (those defined before the
/// region and not written inside it).
/// </para>
/// </summary>
public sealed unsafe class IrBuilder
{
    public readonly IrBlock Block = new();

    // cached guest state: 0..15 = R0..R15, 16..19 = N Z C V, 20 = Thumb
    public const int SlotThumb = 20, SlotFlag0 = 16, Slots = 21;
    readonly int[] _val = new int[Slots];
    readonly bool[] _dirty = new bool[Slots];

    // conditional region
    int _regionLabel = -1;
    readonly int[] _snapshot = new int[Slots];
    readonly bool[] _written = new bool[Slots];

    static readonly uint ThumbOffset = (uint)StateLayout.Of(nameof(CpuState.Thumb));

    public IrBuilder(uint startPc, bool thumb)
    {
        Array.Fill(_val, -1);
        Block.StartPc = startPc;
        Block.Thumb = thumb;
        // at block entry R15 is the block address (the dispatcher looked the block up by it) and Thumb its state
        _val[15] = Const(startPc);
        _val[SlotThumb] = Const(thumb ? 1u : 0u);
    }

    public bool InRegion => _regionLabel >= 0;

    int Emit(IrInst i) { Block.Insts.Add(i); return Block.Insts.Count - 1; }

    public int Const(uint v) => Block.NewConst(v);
    public bool IsConst(int t, out uint v) => Block.TryConst(t, out v);
    public bool IsConst(int t, uint value) => Block.TryConst(t, out var v) && v == value;

    // ------------------------------------------------------------------ guest state

    public int Reg(int r) => Read(r);
    public void SetReg(int r, int v) => Write(r, v);
    public int Flag(IrFlag f) => Read(SlotFlag0 + (int)f);
    public void SetFlag(IrFlag f, int v) { if (v >= 0) Write(SlotFlag0 + (int)f, v); }
    public int Thumb() => Read(SlotThumb);
    public void SetThumb(int v) => Write(SlotThumb, v);

    /// <summary>True when the cached value of a slot is a known constant (R15 after a direct branch).</summary>
    public bool KnownConst(int slot, out uint v) { v = 0; return _val[slot] >= 0 && IsConst(_val[slot], out v); }

    int Read(int slot)
    {
        if (_val[slot] >= 0) return _val[slot];
        int t = Block.NewTemp();
        var i = slot switch
        {
            < 16 => new IrInst(IrOp.GetReg) { D = t, Imm = (uint)slot },
            SlotThumb => new IrInst(IrOp.LoadState) { D = t, Imm = ThumbOffset },
            _ => new IrInst(IrOp.GetFlag) { D = t, Imm = (uint)(slot - SlotFlag0) },
        };
        Emit(i);
        _val[slot] = t;
        _dirty[slot] = false;
        return t;
    }

    void Write(int slot, int v)
    {
        _val[slot] = v;
        _dirty[slot] = true;
        if (InRegion) _written[slot] = true;
    }

    /// <summary>Stores every dirty slot (values stay cached, now clean).</summary>
    public void Flush()
    {
        for (int s = 0; s < Slots; s++)
        {
            if (!_dirty[s]) continue;
            _dirty[s] = false;
            int v = _val[s];
            if (s < 16) Emit(new IrInst(IrOp.SetReg) { A = v, Imm = (uint)s });
            else if (s == SlotThumb) Emit(new IrInst(IrOp.StoreState) { A = v, Imm = ThumbOffset });
            else Emit(new IrInst(IrOp.SetFlag) { A = v, Imm = (uint)(s - SlotFlag0) });
        }
    }

    /// <summary>Forgets the cache (after code that may have changed any state behind its back). Flush first.</summary>
    public void Invalidate()
    {
        for (int s = 0; s < Slots; s++)
        {
            if (_dirty[s]) throw new InvalidOperationException("invalidate with dirty state");
            _val[s] = -1;
            if (InRegion) _written[s] = true;
        }
    }

    // ------------------------------------------------------------------ regions

    public int NewLabel() => Block.Labels++;

    /// <summary>Starts a region that runs only when ARM condition <paramref name="cond"/> holds.</summary>
    public void BeginCond(uint cond)
    {
        if (InRegion) throw new InvalidOperationException("nested region");
        // condition inputs are read (and possibly loaded) before the flush and the branch
        int n = -1, z = -1, c = -1, v = -1;
        switch (cond >> 1)
        {
            case 0: z = Flag(IrFlag.Z); break;
            case 1: c = Flag(IrFlag.C); break;
            case 2: n = Flag(IrFlag.N); break;
            case 3: v = Flag(IrFlag.V); break;
            case 4: c = Flag(IrFlag.C); z = Flag(IrFlag.Z); break;
            case 5: n = Flag(IrFlag.N); v = Flag(IrFlag.V); break;
            case 6: n = Flag(IrFlag.N); v = Flag(IrFlag.V); z = Flag(IrFlag.Z); break;
        }
        Flush();
        int label = NewLabel();
        Emit(new IrInst(IrOp.CondBranch) { A = n, B = z, C = c, E = v, Imm = cond, Imm2 = (uint)label });
        Enter(label);
    }

    /// <summary>Starts a region that runs only when <c>(value != 0) == runIfNonZero</c>.</summary>
    public void BeginIf(int value, bool runIfNonZero)
    {
        if (InRegion) throw new InvalidOperationException("nested region");
        Flush();
        int label = NewLabel();
        Emit(new IrInst(IrOp.BranchIf) { A = value, Imm = runIfNonZero ? 0u : 1u, Imm2 = (uint)label });
        Enter(label);
    }

    void Enter(int label)
    {
        _regionLabel = label;
        Array.Copy(_val, _snapshot, Slots);
        Array.Clear(_written);
    }

    /// <summary>
    /// Closes the region. <paramref name="terminated"/>: the region's path ended in an exit, so only the skip path
    /// reaches the label and the cache is exactly the one from before the region.
    /// </summary>
    public void EndRegion(bool terminated)
    {
        if (!InRegion) throw new InvalidOperationException("no region");
        if (!terminated) Flush();
        int label = _regionLabel;
        _regionLabel = -1;
        for (int s = 0; s < Slots; s++)
        {
            _val[s] = terminated || !_written[s] ? _snapshot[s] : -1;
            _dirty[s] = false; // everything was flushed before the region (and inside it when it falls through)
        }
        Emit(new IrInst(IrOp.Label) { Imm = (uint)label });
    }

    // ------------------------------------------------------------------ exits / callouts

    /// <summary>Leaves the block with the current state: a linkable static exit when PC and Thumb are known.</summary>
    public void Exit(bool neverLink = false)
    {
        if (!neverLink && KnownConst(15, out uint pc) && KnownConst(SlotThumb, out uint t))
        {
            Flush();
            Emit(new IrInst(IrOp.ExitStatic) { Imm = pc, Imm2 = t });
            return;
        }
        Flush();
        Emit(new IrInst(IrOp.ExitDynamic) { Imm = neverLink ? 1u : 0u });
    }

    /// <summary>
    /// Runs the interpreter handler for one instruction. <paramref name="touchesCore"/> = false for SIMD data
    /// operations that only use the D registers and FPSCR: the core-register cache then stays valid across the call.
    /// </summary>
    public void Callout(Op op, uint ins, uint pc, bool touchesCore)
    {
        if (touchesCore) Flush();
        Emit(new IrInst(IrOp.Callout) { Imm = (uint)op, Imm2 = ins, Imm3 = pc });
        if (touchesCore) Invalidate();
        Block.Callouts++;
    }

    /// <summary>
    /// Scalar VFP operation on the register file in the state (offsets relative to D0). Like a SIMD-only callout it
    /// never touches the cached core state.
    /// </summary>
    public void FpOp(FpKind kind, FpFlags flags, uint dOff, uint nOff, uint mOff, uint ins, uint pc)
    {
        Emit(new IrInst(IrOp.FpOp) { Imm = (uint)kind | (uint)flags, Imm4 = dOff | (nOff << 8) | (mOff << 16), Imm2 = ins, Imm3 = pc });
        Block.Callouts++;
    }

    public void StoreState(uint offset, int v) => Emit(new IrInst(IrOp.StoreState) { A = v, Imm = offset });
    public int LoadState(uint offset) { int t = Block.NewTemp(); Emit(new IrInst(IrOp.LoadState) { D = t, Imm = offset }); return t; }
    public void Fence() => Emit(new IrInst(IrOp.Fence));

    // ------------------------------------------------------------------ ALU with folding

    int Op2(IrOp op, int a, int b)
    {
        if (IsConst(a, out uint x) && IsConst(b, out uint y))
        {
            switch (op)
            {
                case IrOp.Add: return Const(x + y);
                case IrOp.Sub: return Const(x - y);
                case IrOp.And: return Const(x & y);
                case IrOp.Or: return Const(x | y);
                case IrOp.Xor: return Const(x ^ y);
                case IrOp.AndNot: return Const(x & ~y);
                case IrOp.OrNot: return Const(x | ~y);
                case IrOp.Mul: return Const(x * y);
                case IrOp.Shl: return Const(x << (int)(y & 31));
                case IrOp.Shr: return Const(x >> (int)(y & 31));
                case IrOp.Sar: return Const((uint)((int)x >> (int)(y & 31)));
                case IrOp.Ror: return Const(System.Numerics.BitOperations.RotateRight(x, (int)(y & 31)));
                case IrOp.CmpEq: return Const(x == y ? 1u : 0u);
            }
        }
        if (IsConst(b, out uint k))
        {
            if (k == 0 && op is IrOp.Add or IrOp.Sub or IrOp.Or or IrOp.Xor or IrOp.Shl or IrOp.Shr or IrOp.Sar or IrOp.Ror or IrOp.AndNot) return a;
            if (k == 0xFFFFFFFF && op == IrOp.And) return a;
        }
        if (IsConst(a, 0) && op is IrOp.Add or IrOp.Or or IrOp.Xor) return b;
        int t = Block.NewTemp();
        Emit(new IrInst(op) { D = t, A = a, B = b });
        return t;
    }

    int Op1(IrOp op, int a, uint imm = 0)
    {
        if (IsConst(a, out uint x))
        {
            switch (op)
            {
                case IrOp.Not: return Const(~x);
                case IrOp.Neg: return Const(0 - x);
                case IrOp.SExt8: return Const((uint)(sbyte)x);
                case IrOp.SExt16: return Const((uint)(short)x);
                case IrOp.ZExt8: return Const(x & 0xFF);
                case IrOp.ZExt16: return Const(x & 0xFFFF);
                case IrOp.Bit: return Const((x >> (int)imm) & 1);
                case IrOp.Clz: return Const((uint)System.Numerics.BitOperations.LeadingZeroCount(x));
                case IrOp.Bswap: return Const(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(x));
            }
        }
        int t = Block.NewTemp();
        Emit(new IrInst(op) { D = t, A = a, Imm = imm });
        return t;
    }

    public int Add(int a, int b) => Op2(IrOp.Add, a, b);
    public int AddK(int a, uint k) => Op2(IrOp.Add, a, Const(k));
    public int Sub(int a, int b) => Op2(IrOp.Sub, a, b);
    public int SubK(int a, uint k) => Op2(IrOp.Sub, a, Const(k));
    public int And(int a, int b) => Op2(IrOp.And, a, b);
    public int AndK(int a, uint k) => Op2(IrOp.And, a, Const(k));
    public int Or(int a, int b) => Op2(IrOp.Or, a, b);
    public int Xor(int a, int b) => Op2(IrOp.Xor, a, b);
    public int AndNot(int a, int b) => Op2(IrOp.AndNot, a, b);
    public int OrNot(int a, int b) => Op2(IrOp.OrNot, a, b);
    public int Mul(int a, int b) => Op2(IrOp.Mul, a, b);
    public int Shl(int a, int n) => Op2(IrOp.Shl, a, Const((uint)n));
    public int Shr(int a, int n) => Op2(IrOp.Shr, a, Const((uint)n));
    public int Sar(int a, int n) => Op2(IrOp.Sar, a, Const((uint)n));
    public int Ror(int a, int n) => Op2(IrOp.Ror, a, Const((uint)n));
    public int CmpEq(int a, int b) => Op2(IrOp.CmpEq, a, b);
    public int Not(int a) => Op1(IrOp.Not, a);
    public int SExt8(int a) => Op1(IrOp.SExt8, a);
    public int SExt16(int a) => Op1(IrOp.SExt16, a);
    public int ZExt8(int a) => Op1(IrOp.ZExt8, a);
    public int ZExt16(int a) => Op1(IrOp.ZExt16, a);
    public int Bit(int a, int n) => Op1(IrOp.Bit, a, (uint)n);
    public int Clz(int a) => Op1(IrOp.Clz, a);
    public int Bswap(int a) => Op1(IrOp.Bswap, a);
    public int Rev16(int a) => Op1(IrOp.Rev16, a);
    public int RevSh(int a) => Op1(IrOp.RevSh, a);
    public int Rbit(int a) => Op1(IrOp.Rbit, a);
    public int UDiv(int a, int b) => Op2(IrOp.UDiv, a, b);
    public int SDiv(int a, int b) => Op2(IrOp.SDiv, a, b);

    public int Select(int cond, int ifTrue, int ifFalse)
    {
        if (IsConst(cond, out uint c)) return c != 0 ? ifTrue : ifFalse;
        int t = Block.NewTemp();
        Emit(new IrInst(IrOp.Select) { D = t, A = ifTrue, B = ifFalse, C = cond });
        return t;
    }

    /// <summary>ARM AddWithCarry(a, b, carryIn) (carryIn = -1 means 0). Returns the result and the NZCV temps.</summary>
    public (int R, int N, int Z, int C, int V) AddC(int a, int b, int carryIn)
    {
        var i = new IrInst(IrOp.AddC) { A = a, B = b, C = carryIn, D = Block.NewTemp(), Fn = Block.NewTemp(), Fz = Block.NewTemp(), Fc = Block.NewTemp(), Fv = Block.NewTemp() };
        Emit(i);
        return (i.D, i.Fn, i.Fz, i.Fc, i.Fv);
    }

    /// <summary>ARM AddWithCarry(a, NOT b, carryIn) (carryIn = -1 means 1, i.e. a plain subtraction).</summary>
    public (int R, int N, int Z, int C, int V) SubC(int a, int b, int carryIn)
    {
        var i = new IrInst(IrOp.SubC) { A = a, B = b, C = carryIn, D = Block.NewTemp(), Fn = Block.NewTemp(), Fz = Block.NewTemp(), Fc = Block.NewTemp(), Fv = Block.NewTemp() };
        Emit(i);
        return (i.D, i.Fn, i.Fz, i.Fc, i.Fv);
    }

    public (int N, int Z) TestNZ(int a)
    {
        if (IsConst(a, out uint x)) return (Const(x >> 31), Const(x == 0 ? 1u : 0u));
        var i = new IrInst(IrOp.TestNZ) { A = a, Fn = Block.NewTemp(), Fz = Block.NewTemp() };
        Emit(i);
        return (i.Fn, i.Fz);
    }

    /// <summary>ARM register-controlled shift (amount = low byte of <paramref name="rs"/>) with carry-out.</summary>
    public (int R, int C) ShiftReg(uint type, int v, int rs, int carryIn)
    {
        var i = new IrInst(IrOp.ShiftReg) { Imm = type, A = v, B = rs, C = carryIn, D = Block.NewTemp(), Fc = Block.NewTemp() };
        Emit(i);
        return (i.D, i.Fc);
    }

    public (int Lo, int Hi) MulLong(int a, int b, bool signed)
    {
        var i = new IrInst(signed ? IrOp.SMulL : IrOp.UMulL) { A = a, B = b, D = Block.NewTemp(), D2 = Block.NewTemp() };
        Emit(i);
        return (i.D, i.D2);
    }

    public (int Lo, int Hi) Add64(int lo1, int hi1, int lo2, int hi2)
    {
        var i = new IrInst(IrOp.Add64) { A = lo1, B = hi1, C = lo2, E = hi2, D = Block.NewTemp(), D2 = Block.NewTemp() };
        Emit(i);
        return (i.D, i.D2);
    }

    // ------------------------------------------------------------------ memory

    public int Load(IrOp op, int addr)
    {
        int t = Block.NewTemp();
        Emit(new IrInst(op) { D = t, A = addr });
        return t;
    }

    public void Store(IrOp op, int addr, int value) => Emit(new IrInst(op) { A = addr, B = value });

    public int LoadExclusive(int addr) { int t = Block.NewTemp(); Emit(new IrInst(IrOp.LdrEx) { D = t, A = addr }); return t; }
    public int StoreExclusive(int addr, int value) { int t = Block.NewTemp(); Emit(new IrInst(IrOp.StrEx) { D = t, A = addr, B = value }); return t; }
}
