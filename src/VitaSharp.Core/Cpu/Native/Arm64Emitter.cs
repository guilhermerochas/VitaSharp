using static VitaSharp.Core.Cpu.Native.Arm64Assembler;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Lowers an <see cref="IrBlock"/> to AArch64 machine code (<see cref="Arm64Assembler"/>) for the native runtime's
/// frame: x19 = CpuState*, x20 = guest memory base (fastmem: host address = x20 + UXTW guest address), sp = the
/// trampoline frame (host FPCR, guest FPCR, then spill slots). x0-x8, x16 and x17 are scratch; temps live in
/// x9-x15 (caller-saved) and x21-x28 (callee-saved: they survive callouts), allocated by <see cref="LinearScan"/>.
/// Every 32-bit value lives in a W register, so its X register is zero-extended and can index guest memory.
/// <para>
/// Guest and host flags agree (ARM64 NZCV after ADDS/SUBS/ADCS/SBCS on W registers is exactly ARM32 AddWithCarry, C
/// after subtraction is NOT borrow), so flag temps are a CSET away. FPCR holds the guest mode (FZ, DN, round to
/// nearest: the Vita's FPSCR and the Advanced SIMD standard mode) for the whole time generated code runs, and the
/// host mode only around callouts: AArch64 flushes denormals with tininess detected before rounding like ARMv7, so
/// VFP and NEON arithmetic needs no fix-ups (the x64 emitter's min-normal slow paths do not exist here).
/// </para>
/// Calls follow AAPCS64 (arguments in x0-x3, no shadow space, sp 16-byte aligned). x18 (platform register on Apple
/// and Windows) is never touched.
/// </summary>
public sealed unsafe partial class Arm64Emitter
{
    const int X0 = 0, X1 = 1, X2 = 2, X3 = 3, X4 = 4, X5 = 5, X6 = 6, X7 = 7, X8 = 8, X16 = 16, X17 = 17;
    const int State = 19, MemBase = 20;

    /// <summary>Callee-saved registers available for temps (survive callouts).</summary>
    static readonly int[] CalleeSaved = [21, 22, 23, 24, 25, 26, 27, 28];
    /// <summary>The callee-saved temps when x26-x28 hold the pinned block counters.</summary>
    static readonly int[] CalleeSavedPinned = [21, 22, 23, 24, 25];
    /// <summary>Caller-saved registers available for temps (not across callouts).</summary>
    static readonly int[] CallerSaved = [9, 10, 11, 12, 13, 14, 15];
    /// <summary>Pinned block counters (<see cref="A64Opt.PinnedCounters"/>): blocks, instructions, and CycleAcc - CheckWindow
    /// (the window is full once it is &gt;= 0). Loaded by the trampoline, stored back by the epilogue.</summary>
    const int PinBlocks = 26, PinInstr = 27, PinBudget = 28;

    public const int SpillSlots = 64;
    const int FrameHostFpcr = 0, FrameGuestFpcr = 8, SpillBase = 16;
    const int FrameSize = SpillBase + 8 * SpillSlots; // 528, 16-byte aligned
    const int SavedRegs = 96;                          // x19-x30

    /// <summary>FPCR bits the guest mode owns: AHP, DN, FZ, RMode, FZ16.</summary>
    const uint FpcrGuestMask = 0x07C80000, FpcrGuestBits = 0x03000000; // DN | FZ, round to nearest

    // CpuState field offsets
    static readonly int OffR = 0;
    static readonly int OffN = StateLayout.Of(nameof(CpuState.N));
    static readonly int OffCycleAcc = StateLayout.Of(nameof(CpuState.CycleAcc));
    static readonly int OffCheckWindow = StateLayout.Of(nameof(CpuState.CheckWindow));
    static readonly int OffNBlocks = StateLayout.Of(nameof(CpuState.NBlocks));
    static readonly int OffNInstr = StateLayout.Of(nameof(CpuState.NInstr));
    static readonly int OffLastExitSlot = StateLayout.Of(nameof(CpuState.LastExitSlot));
    static readonly int OffLinkBreak = StateLayout.Of(nameof(CpuState.LinkBreak));
    static readonly int OffThumb = StateLayout.Of(nameof(CpuState.Thumb));
    static readonly int OffFpscr = StateLayout.Of(nameof(CpuState.Fpscr));
    static readonly int OffMemBase = StateLayout.Of(nameof(CpuState.MemBase));
    static readonly int OffDispatchTable = StateLayout.Of(nameof(CpuState.DispatchTable));
    static readonly int OffD = StateLayout.Of(nameof(CpuState.D));
    static readonly int OffExit = StateLayout.Of(nameof(CpuState.Exit));
    static readonly int OffTierUp = StateLayout.Of(nameof(CpuState.TierUpRequest));

    /// <summary>Code-quality optimisations of this emitter, each of which can be turned off for bisection
    /// (<see cref="Util.EnvVars.NativeA64NoOpt"/>: a comma-separated list of the names in <see cref="OptNames"/>, or all).</summary>
    [Flags]
    public enum A64Opt
    {
        None = 0,
        /// <summary>Block accounting in pinned registers (x26 blocks, x27 instructions, x28 cycles minus the window):
        /// three ALU instructions and a B.GE per exit instead of three memory read-modify-writes and a compare with
        /// the window in memory. Changes the trampoline too (<see cref="Runtime.PinnedCounters"/>).</summary>
        PinnedCounters = 1,
        /// <summary>Static exits to a later guest address skip the LinkBreak load (every cycle of linked blocks has an
        /// exit to an address at or below its block's start, which keeps the check).</summary>
        BackEdgeBreak = 2,
        /// <summary>CondBranch on the host NZCV left by the AddC/SubC/TestNZ that produced its flag temps (B.cond,
        /// no compare of materialised flags).</summary>
        FuseBranch = 4,
        /// <summary>Adjacent 32-bit state stores/loads (SetReg/SetFlag/StoreState, GetReg/GetFlag) as STP/LDP.</summary>
        PairState = 8,
        /// <summary>Guest D/Q registers stay in host v18-v31 between VecOps of a straight-line region (write-through:
        /// the register file in memory stays current, so only reloads are removed).</summary>
        VecCache = 16,
        /// <summary>FPCR is switched back to the guest mode lazily after a callout (only before FP code, a branch,
        /// a label or an exit), so consecutive callouts share one switch pair.</summary>
        LazyFpcr = 32,
        /// <summary>The VFP fast-path FPSCR mode check in 4 instructions (UBFX of bits 16-25) instead of 6.</summary>
        FastFpCheck = 64,
        /// <summary>The dirty-page mark after a guest store indexes from the address temp's own register (no copy).</summary>
        DirtyReg = 128,
        /// <summary>Stored constants that need MOVZ + MOVK (R15 values, addresses) come from the literal pool: one LDR.</summary>
        StoreLiteral = 256,
        All = 511,
    }

    public static readonly string[] OptNames = ["pin", "backedge", "fuse", "pair", "vcache", "fpcr", "fpcheck", "dirty", "lit"];

    /// <summary>Enabled optimisations (tests may change it; the trampoline's pinned-counter mode is fixed per runtime).</summary>
    public static A64Opt Opts = ParseOpts(Environment.GetEnvironmentVariable(Util.EnvVars.NativeA64NoOpt));

    public static A64Opt ParseOpts(string? noOpt)
    {
        if (string.IsNullOrWhiteSpace(noOpt)) return A64Opt.All;
        var o = A64Opt.All;
        foreach (var raw in noOpt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase)) return A64Opt.None;
            int i = Array.FindIndex(OptNames, n => n.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) o &= ~(A64Opt)(1 << i);
        }
        return o;
    }

    static bool On(A64Opt o) => (Opts & o) != 0;

    /// <summary>Debug knob for tests: restricts the temp registers so spilling paths get exercised.</summary>
    public static int MaxTempRegisters = int.MaxValue;
    /// <summary>Debug: check the register allocation of every block (tests).</summary>
    public static bool VerifyAllocation;
    /// <summary>Verify mode: blocks record every guest store (see <see cref="X64Emitter.LogStores"/>).</summary>
    public static bool LogStores = NativeVerifier.Enabled;

    readonly Arm64Assembler _a = new();
    IrBlock _ir = null!;
    int[] _loc = null!;
    int[] _labels = null!;
    int _abort;
    bool _checkExit;
    Runtime _rt;

    /// <summary>Addresses of the runtime entry points the code jumps to or calls.</summary>
    public struct Runtime
    {
        public ulong Epilogue;
        public ulong Callout;
        /// <summary>Nonzero: indirect exits look their target up in the dispatch table (each thread's state holds its
        /// process's, <see cref="CpuState.DispatchTable"/>).</summary>
        public ulong DispatchTable;
        /// <summary>The trampoline keeps the block counters in x26-x28 (<see cref="A64Opt.PinnedCounters"/>): blocks
        /// compiled for this runtime must account in them.</summary>
        public bool PinnedCounters;
    }

    bool _pin, _backEdge, _fuse, _pair, _vcache, _lazyFpcr, _fastFpCheck, _dirtyReg, _storeLit;

    /// <summary>
    /// Allocates registers and lowers the block. <paramref name="slotFor"/> returns the address of the link slot of
    /// the k-th static exit. Returns false when the block needs more spill slots than the frame has.
    /// </summary>
    /// <summary>Stopwatch ticks the last <see cref="Prepare"/> spent in register allocation (compile-phase statistics).</summary>
    public long AllocTicks;

    public bool Prepare(IrBlock ir, Runtime rt, Func<int, nint> slotFor, nint tierUpCounter = 0)
    {
        _ir = ir;
        _rt = rt;
        _a.Reset();
        var opts = Opts;
        _pin = rt.PinnedCounters;
        _backEdge = (opts & A64Opt.BackEdgeBreak) != 0;
        _fuse = (opts & A64Opt.FuseBranch) != 0;
        _pair = (opts & A64Opt.PairState) != 0;
        _vcache = (opts & A64Opt.VecCache) != 0;
        _lazyFpcr = (opts & A64Opt.LazyFpcr) != 0;
        _fastFpCheck = (opts & A64Opt.FastFpCheck) != 0;
        _dirtyReg = (opts & A64Opt.DirtyReg) != 0;
        _storeLit = (opts & A64Opt.StoreLiteral) != 0;
        ForgetHostFlags();
        _fpcrHost = false;
        Array.Fill(_vcOff, -1);
        _vcAny = false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var loc = LinearScan.Allocate(ir, CallerSaved, _pin ? CalleeSavedPinned : CalleeSaved, SpillSlots, MaxTempRegisters, VerifyAllocation, reuse: true);
        AllocTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (loc == null) return false;
        _loc = loc;
        _labels = new int[ir.Labels];
        for (int i = 0; i < ir.Labels; i++) _labels[i] = _a.NewLabel();
        _abort = _a.NewLabel();
        _checkExit = ir.Callouts > 0;
        int tierUp = _a.NewLabel();
        if (tierUpCounter != 0)
        {
            _a.LdrLit(X16, (ulong)tierUpCounter);
            _a.Ldr(X17, X16, 0);
            _a.SubsImm(X17, X17, 1);
            _a.Str(X17, X16, 0);
            _a.BCond(Cond.EQ, tierUp);
        }
        int exitIndex = 0;
        var insts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ir.Insts);
        for (int i = 0; i < insts.Length; i++)
        {
            if (_pair && i + 1 < insts.Length && TryPair(insts[i], insts[i + 1])) { i++; continue; }
            Lower(insts[i], slotFor, ref exitIndex);
        }
        _a.Bind(_abort);
        EmitAccounting();
        _a.StrX(ZR, State, OffLastExitSlot);
        JumpTo(rt.Epilogue);
        if (tierUpCounter != 0)
        {
            _a.Bind(tierUp);
            _a.StrX(ZR, State, OffLastExitSlot);
            _a.MovImm(X16, 1);
            _a.Str(X16, State, OffTierUp);
            JumpTo(rt.Epilogue);
        }
        return true;
    }

    /// <summary>Encodes the prepared code (position independent: <paramref name="address"/> is not needed).</summary>
    public byte[] Assemble(ulong address) => _a.Finish();

    /// <summary>The words of the prepared block (tests).</summary>
    public IReadOnlyList<uint> Words => _a.Words;

    void JumpTo(ulong target) { _a.LdrLit(X16, target); _a.Br(X16); }

    // ------------------------------------------------------------------ operand helpers

    bool IsConst(int t, out uint v) => _ir.TryConst(t, out v);
    bool InReg(int t, out int r) { r = t >= 0 ? _loc[t] : -1; return r >= 0; }
    int SlotOff(int t) => SpillBase + 8 * (-(_loc[t] + 2));

    /// <summary>Loads temp <paramref name="t"/> into W<paramref name="r"/>.</summary>
    void LoadTo(int r, int t)
    {
        if (IsConst(t, out uint v)) _a.MovImm(r, v);
        else if (InReg(t, out int s)) { if (s != r) _a.Mov(r, s); }
        else _a.Ldr(r, SP, SlotOff(t));
    }

    /// <summary>A register holding temp t: its own, or <paramref name="scratch"/> loaded with it.</summary>
    int Use(int t, int scratch)
    {
        if (!IsConst(t, out _) && InReg(t, out int r)) return r;
        LoadTo(scratch, t);
        return scratch;
    }

    /// <summary>Like <see cref="Use"/>, but the constant 0 is WZR (only for operand fields where 31 means the zero register).</summary>
    int UseZ(int t, int scratch) => t < 0 || (IsConst(t, out uint v) && v == 0) ? ZR : Use(t, scratch);

    /// <summary>Like <see cref="UseZ"/> for a value that is only stored: a constant needing MOVZ + MOVK is one literal-pool
    /// load instead (<see cref="A64Opt.StoreLiteral"/>; the load's latency is off the critical path of a store).</summary>
    int UseStored(int t, int scratch)
    {
        if (_storeLit && IsConst(t, out uint v) && MovImmIsLong(v)) { _a.LdrLitW(scratch, v); return scratch; }
        return UseZ(t, scratch);
    }

    int Def(int t, int scratch) => t >= 0 && InReg(t, out int r) ? r : scratch;

    void Commit(int t, int r)
    {
        if (t < 0) return;
        if (InReg(t, out int own)) { if (own != r) _a.Mov(own, r); return; }
        _a.Str(r, SP, SlotOff(t));
    }

    void LdState(int r, int off) => _a.Ldr(r, State, off);
    void StState(int r, int off) => _a.Str(r, State, off);

    /// <summary>State offset of a 32-bit state store (SetReg/SetFlag/StoreState) or load (GetReg/GetFlag/LoadState), else -1.</summary>
    static int StateOffset(in IrInst x, bool store) => (store, x.Op) switch
    {
        (true, IrOp.SetReg) or (false, IrOp.GetReg) => OffR + 4 * (int)x.Imm,
        (true, IrOp.SetFlag) or (false, IrOp.GetFlag) => OffN + 4 * (int)x.Imm,
        (true, IrOp.StoreState) or (false, IrOp.LoadState) => (int)x.Imm,
        _ => -1,
    };

    /// <summary>
    /// <see cref="A64Opt.PairState"/>: two consecutive 32-bit state stores (or loads) of adjacent fields as one STP (LDP).
    /// The register flushes the frontend emits before exits and conditional regions are runs of these (R0..R15, then
    /// N Z C V, which follow R15 in the state). Only ops that keep the host flags are paired, so the flag tracking of
    /// <see cref="Lower"/> needs no update.
    /// </summary>
    bool TryPair(in IrInst x, in IrInst y)
    {
        var a = _a;
        int o1 = StateOffset(x, true), o2 = StateOffset(y, true);
        if (o1 >= 0 && o2 == o1 + 4 && FitsPairW(o1))
        {
            int r1 = UseStored(x.A, X0), r2 = UseStored(y.A, X1);
            a.StpW(r1, r2, State, o1);
            StateWritten(o1, 8);
            return true;
        }
        o1 = StateOffset(x, false); o2 = StateOffset(y, false);
        if (o1 >= 0 && o2 == o1 + 4 && FitsPairW(o1) && x.D >= 0 && y.D >= 0)
        {
            int d1 = Def(x.D, X0), d2 = Def(y.D, X1);
            if (d1 == d2) return false;
            a.LdpW(d1, d2, State, o1);
            Commit(x.D, d1);
            Commit(y.D, d2);
            return true;
        }
        return false;
    }

    /// <summary>A plain store wrote state bytes [off, off + len): cached copies of SIMD registers there are stale.</summary>
    void StateWritten(int off, int len) => VcInvalidate(off - OffD, len);

    // ------------------------------------------------------------------ lowering

    /// <summary>
    /// Adds the block's cycles and counts to the state; leaves CycleAcc in x16. With pinned counters: x26 += 1,
    /// x27 += instructions, ADDS x28 += cycles, so the flags say GE exactly when CycleAcc reached CheckWindow.
    /// </summary>
    void EmitAccounting()
    {
        var a = _a;
        if (_pin)
        {
            a.AddImm(PinBlocks, PinBlocks, 1, true);
            AddConst64(PinInstr, (ulong)_ir.GuestInstructions);
            ulong c = (ulong)Math.Min(_ir.Cycles, int.MaxValue);
            if (c < 4096 || (IsAddImm((uint)c) && c < 1u << 24)) a.AddsImm(PinBudget, PinBudget, (uint)c, true);
            else { a.MovImm64(X8, c); a.Adds(PinBudget, PinBudget, X8, x: true); }
            return;
        }
        a.LdrX(X16, State, OffCycleAcc);
        AddConst64(X16, (ulong)Math.Min(_ir.Cycles, int.MaxValue));
        a.StrX(X16, State, OffCycleAcc);
        a.LdrX(X17, State, OffNBlocks);
        a.AddImm(X17, X17, 1, true);
        a.StrX(X17, State, OffNBlocks);
        a.LdrX(X17, State, OffNInstr);
        AddConst64(X17, (ulong)_ir.GuestInstructions);
        a.StrX(X17, State, OffNInstr);
    }

    void AddConst64(int r, ulong v)
    {
        if (v == 0) return;
        if (v < 4096 || (IsAddImm((uint)v) && v < 1u << 24)) _a.AddImm(r, r, (uint)v, true);
        else { _a.MovImm64(X8, v); _a.Add(r, r, X8, x: true); }
    }

    // ------------------------------------------------------------------ host flags (compare + branch fusion)

    /// <summary>Flag temps whose values the host NZCV currently hold (-1: none); valid while only ops that keep the
    /// host flags were lowered since the AddC/SubC/TestNZ that set them.</summary>
    int _hfN = -1, _hfZ = -1, _hfC = -1, _hfV = -1;

    /// <summary>IR ops whose lowering never writes NZCV (and is not a merge point).</summary>
    static bool KeepsHostFlags(IrOp op) => op is IrOp.Nop or IrOp.GetReg or IrOp.GetFlag or IrOp.LoadState or IrOp.SetReg
        or IrOp.SetFlag or IrOp.StoreState or IrOp.Add or IrOp.Sub or IrOp.And or IrOp.Or or IrOp.Xor or IrOp.AndNot
        or IrOp.OrNot or IrOp.Mul or IrOp.Not or IrOp.Neg or IrOp.Shl or IrOp.Shr or IrOp.Sar or IrOp.Ror or IrOp.Clz
        or IrOp.Bswap or IrOp.Rev16 or IrOp.RevSh or IrOp.Rbit or IrOp.SExt8 or IrOp.SExt16 or IrOp.ZExt8 or IrOp.ZExt16
        or IrOp.Bit or IrOp.UDiv or IrOp.SDiv or IrOp.UMulL or IrOp.SMulL or IrOp.Load8 or IrOp.Load8S or IrOp.Load16
        or IrOp.Load16S or IrOp.Load32 or IrOp.Store8 or IrOp.Store16 or IrOp.Store32 or IrOp.LoadD or IrOp.StoreD
        or IrOp.Fence or IrOp.CondBranch or IrOp.BranchIf;

    void ForgetHostFlags() { _hfN = _hfZ = _hfC = _hfV = -1; }

    /// <summary>Records that the host NZCV now equal these flag temps (call right after the flag-setting instruction).</summary>
    void HostFlagsAre(int n, int z, int c, int v) { if (_fuse) { _hfN = n; _hfZ = z; _hfC = c; _hfV = v; } }

    /// <summary>Whether CondBranch <paramref name="x"/> can test the host flags: every flag its condition reads is the
    /// temp the host flag currently holds.</summary>
    bool HostFlagsMatch(in IrInst x)
    {
        static bool Same(int used, int host) => used < 0 || used == host;
        bool any = x.A >= 0 || x.B >= 0 || x.C >= 0 || x.E >= 0;
        return any && x.Imm < 14 && Same(x.A, _hfN) && Same(x.B, _hfZ) && Same(x.C, _hfC) && Same(x.E, _hfV);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    void Lower(IrInst x, Func<int, nint> slotFor, ref int exitIndex)
    {
        var a = _a;
        if (_hfN >= 0 || _hfZ >= 0 || _hfC >= 0 || _hfV >= 0)
            if (!KeepsHostFlags(x.Op)) ForgetHostFlags();
        // merge points and callouts end the SIMD register cache (FpOp reloads it after its handler path instead)
        if (_vcAny && x.Op is IrOp.Label or IrOp.Callout) VcForget();
        if (_fpcrHost && NeedsGuestFpcr(x.Op)) EnsureGuestFpcr(); // MSR FPCR leaves NZCV alone (branch fusion)
        switch (x.Op)
        {
            case IrOp.Nop: return;

            // ---------------- state
            case IrOp.GetReg: { int d = Def(x.D, X0); LdState(d, OffR + 4 * (int)x.Imm); Commit(x.D, d); return; }
            case IrOp.GetFlag: { int d = Def(x.D, X0); LdState(d, OffN + 4 * (int)x.Imm); Commit(x.D, d); return; }
            case IrOp.LoadState: { int d = Def(x.D, X0); LdState(d, (int)x.Imm); Commit(x.D, d); return; }
            case IrOp.SetReg: StState(UseStored(x.A, X0), OffR + 4 * (int)x.Imm); return;
            case IrOp.SetFlag: StState(UseStored(x.A, X0), OffN + 4 * (int)x.Imm); return;
            case IrOp.StoreState: StState(UseStored(x.A, X0), (int)x.Imm); StateWritten((int)x.Imm, 4); return;

            // ---------------- ALU
            case IrOp.Add: case IrOp.Sub: case IrOp.And: case IrOp.Or: case IrOp.Xor: case IrOp.AndNot: case IrOp.OrNot: case IrOp.Mul:
                Bin(x); return;
            case IrOp.Not: { int d = Def(x.D, X0); a.Mvn(d, UseZ(x.A, X1)); Commit(x.D, d); return; }
            case IrOp.Neg: { int d = Def(x.D, X0); a.Neg(d, UseZ(x.A, X1)); Commit(x.D, d); return; }
            case IrOp.Shl: case IrOp.Shr: case IrOp.Sar: case IrOp.Ror: ShiftOp(x); return;
            case IrOp.Clz: { int d = Def(x.D, X0); a.Clz(d, UseZ(x.A, X1)); Commit(x.D, d); return; }
            case IrOp.Bswap: { int d = Def(x.D, X0); a.Rev(d, UseZ(x.A, X1)); Commit(x.D, d); return; }
            case IrOp.Rev16: { int d = Def(x.D, X0); a.Rev16(d, UseZ(x.A, X1)); Commit(x.D, d); return; }
            case IrOp.RevSh: { int d = Def(x.D, X0); a.Rev16(d, UseZ(x.A, X1)); a.Sxth(d, d); Commit(x.D, d); return; }
            case IrOp.Rbit: { int d = Def(x.D, X0); a.Rbit(d, UseZ(x.A, X1)); Commit(x.D, d); return; }
            case IrOp.SExt8: case IrOp.SExt16: case IrOp.ZExt8: case IrOp.ZExt16:
            {
                int d = Def(x.D, X0);
                int s = UseZ(x.A, X1);
                switch (x.Op)
                {
                    case IrOp.SExt8: a.Sxtb(d, s); break;
                    case IrOp.SExt16: a.Sxth(d, s); break;
                    case IrOp.ZExt8: a.Uxtb(d, s); break;
                    default: a.Uxth(d, s); break;
                }
                Commit(x.D, d);
                return;
            }
            case IrOp.Bit: { int d = Def(x.D, X0); a.Ubfx(d, UseZ(x.A, X1), (int)x.Imm, 1); Commit(x.D, d); return; }
            case IrOp.CmpEq:
            {
                CmpWith(Use(x.A, X1), x.B);
                int d = Def(x.D, X0);
                a.Cset(d, Cond.EQ);
                Commit(x.D, d);
                return;
            }
            case IrOp.Select:
            {
                int c = Use(x.C, X2);
                int va = UseZ(x.A, X0), vb = UseZ(x.B, X1);
                a.CmpImm(c, 0);
                int d = Def(x.D, X3);
                a.Csel(d, va, vb, Cond.NE);
                Commit(x.D, d);
                return;
            }
            case IrOp.UDiv: case IrOp.SDiv:
            {
                // ARM64 division is ARM's: x / 0 = 0, INT_MIN / -1 = INT_MIN
                int n = UseZ(x.A, X0), m = UseZ(x.B, X1);
                int d = Def(x.D, X2);
                if (x.Op == IrOp.UDiv) a.Udiv(d, n, m); else a.Sdiv(d, n, m);
                Commit(x.D, d);
                return;
            }

            // ---------------- flags
            case IrOp.AddC: case IrOp.SubC: ArithFlags(x); return;
            case IrOp.TestNZ:
            {
                int s = UseZ(x.A, X0);
                a.Tst(s, s);
                HostFlagsAre(x.Fn, x.Fz, -1, -1);
                SetFlagOut(x.Fn, Cond.MI);
                SetFlagOut(x.Fz, Cond.EQ);
                return;
            }
            case IrOp.ShiftReg: ShiftReg(x); return;

            // ---------------- 64-bit
            case IrOp.UMulL: case IrOp.SMulL:
            {
                int n = UseZ(x.A, X0), m = UseZ(x.B, X1);
                if (x.Op == IrOp.UMulL) a.Umull(X2, n, m); else a.Smull(X2, n, m);
                if (x.D >= 0) { int d = Def(x.D, X3); a.Mov(d, X2); Commit(x.D, d); }
                if (x.D2 >= 0) { int d = Def(x.D2, X3); a.LsrImm(d, X2, 32, true); Commit(x.D2, d); }
                return;
            }
            case IrOp.Add64:
            {
                int lo1 = UseZ(x.A, X0), hi1 = UseZ(x.B, X1), lo2 = UseZ(x.C, X2), hi2 = UseZ(x.E, X3);
                a.Adds(X4, lo1, lo2);
                a.Adc(X5, hi1, hi2);
                if (x.D >= 0) Commit(x.D, X4);
                if (x.D2 >= 0) Commit(x.D2, X5);
                return;
            }

            // ---------------- memory
            case IrOp.Load8: case IrOp.Load8S: case IrOp.Load16: case IrOp.Load16S: case IrOp.Load32:
            {
                int ad = UseZ(x.A, X16);
                int d = Def(x.D, X0);
                var k = x.Op switch
                {
                    IrOp.Load8 => LsKind.Ldrb, IrOp.Load8S => LsKind.Ldrsb, IrOp.Load16 => LsKind.Ldrh,
                    IrOp.Load16S => LsKind.Ldrsh, _ => LsKind.LdrW,
                };
                a.LdStReg(k, d, MemBase, ad);
                Commit(x.D, d);
                return;
            }
            case IrOp.Store8: case IrOp.Store16: case IrOp.Store32:
            {
                if (LogStores) EmitStoreLog(x);
                int ad = UseZ(x.A, X16);
                int v = UseStored(x.B, X17);
                var k = x.Op == IrOp.Store8 ? LsKind.Strb : x.Op == IrOp.Store16 ? LsKind.Strh : LsKind.StrW;
                a.LdStReg(k, v, MemBase, ad);
                MarkDirty(x.A);
                return;
            }
            case IrOp.Fence: a.DmbIsh(); return;
            case IrOp.FpOp: FpOp(x); return;
            case IrOp.VecOp: VecOp(x); return;
            case IrOp.LoadD:
            {
                int ad = UseZ(x.A, X16);
                a.LdStReg(LsKind.LdrX, X0, MemBase, ad);
                a.StrX(X0, State, (int)x.Imm);
                StateWritten((int)x.Imm, 8);
                return;
            }
            case IrOp.StoreD:
            {
                int ad = UseZ(x.A, X16);
                a.LdrX(X0, State, (int)x.Imm);
                a.LdStReg(LsKind.StrX, X0, MemBase, ad);
                MarkDirty(x.A);
                return;
            }
            case IrOp.LdrEx: LoadExclusive(x); return;
            case IrOp.StrEx: StoreExclusive(x); return;

            // ---------------- control
            case IrOp.Label: a.Bind(_labels[x.Imm]); return;
            case IrOp.BranchIf:
            {
                int s = UseZ(x.A, X0);
                if (x.Imm != 0) a.Cbnz(s, _labels[x.Imm2]); else a.Cbz(s, _labels[x.Imm2]);
                return;
            }
            case IrOp.CondBranch: CondBranch(x); return;
            case IrOp.Callout: CallHandler(x.Imm, x.Imm2, x.Imm3); return;
            case IrOp.ExitStatic:
            {
                nint slot = slotFor(exitIndex++);
                int ret = a.NewLabel();
                // LinkBreak only on back edges: every cycle of linked blocks has an exit to an address at or below its
                // block's start (forward chains end by themselves, at an unlinked exit or the pacing window)
                bool linkBreak = !_backEdge || x.Imm <= _ir.StartPc;
                if (_pin)
                {
                    // the slot address first (it is also the return path's LastExitSlot), the link target load early
                    a.LdrLit(X16, (ulong)slot);
                    EmitAccounting();
                    a.LdrX(X17, X16, 0);
                    a.BCond(Cond.GE, ret);                     // CycleAcc >= CheckWindow
                    if (linkBreak) { LdState(X8, OffLinkBreak); a.Cbnz(X8, ret); }
                    if (_checkExit) { LdState(X8, OffExit); a.Cbnz(X8, ret); }
                    a.Cbz(X17, ret, true);
                    a.Br(X17);
                    a.Bind(ret);
                    a.StrX(X16, State, OffLastExitSlot);
                    JumpTo(_rt.Epilogue);
                    return;
                }
                EmitAccounting();
                WindowChecks(ret, linkBreak);
                a.LdrLit(X16, (ulong)slot);
                a.LdrX(X17, X16, 0);
                a.Cbz(X17, ret, true);
                a.Br(X17);
                a.Bind(ret);
                a.LdrLit(X16, (ulong)slot);
                a.StrX(X16, State, OffLastExitSlot);
                JumpTo(_rt.Epilogue);
                return;
            }
            case IrOp.ExitDynamic:
            {
                int ret = a.NewLabel();
                EmitAccounting();
                if (x.Imm == 0 && _rt.DispatchTable != 0 && !NativeRuntime.NoLink)
                {
                    // indirect exit through the dispatch table: key, code, key (see NativeRuntime.Publish). ARM64
                    // reorders loads, so the first two are acquire loads: the code pointer is read after the key and
                    // the second key read after the code pointer.
                    WindowChecks(ret);
                    LdState(X0, OffR + 60);
                    LdState(X1, OffThumb);
                    a.Orr(X1, X0, X1);                                   // key = pc | thumb
                    a.Ubfx(X2, X0, 1, NativeRuntime.DispatchBits);
                    a.LdrX(X3, State, OffDispatchTable);                 // the thread's process's table
                    a.Add(X3, X3, X2, Shift.Lsl, 4, x: true);            // 16-byte entries
                    a.Ldar(X4, X3);
                    a.Cmp(X4, X1);
                    a.BCond(Cond.NE, ret);
                    a.AddImm(X5, X3, 8, true);
                    a.Ldar(X6, X5, true);
                    a.Ldr(X4, X3, 0);
                    a.Cmp(X4, X1);
                    a.BCond(Cond.NE, ret);
                    a.Br(X6);
                }
                a.Bind(ret);
                a.StrX(ZR, State, OffLastExitSlot);
                JumpTo(_rt.Epilogue);
                return;
            }
            default: throw new NotSupportedException($"IR op {x.Op}");
        }
    }

    /// <summary>
    /// Branches to <paramref name="ret"/> when the block must return to the dispatcher (expects CycleAcc in x16, or
    /// with pinned counters the flags of <see cref="EmitAccounting"/>).
    /// </summary>
    void WindowChecks(int ret, bool linkBreak = true)
    {
        var a = _a;
        if (!_pin)
        {
            a.LdrX(X17, State, OffCheckWindow);
            a.Cmp(X16, X17, true);
        }
        a.BCond(Cond.GE, ret);
        if (linkBreak) { LdState(X17, OffLinkBreak); a.Cbnz(X17, ret); }
        if (_checkExit) { LdState(X17, OffExit); a.Cbnz(X17, ret); }
    }

    /// <summary>FPCR holds the host mode at this point of the straight-line code (<see cref="A64Opt.LazyFpcr"/>: after a
    /// callout, until code that needs the guest mode).</summary>
    bool _fpcrHost;

    /// <summary>Switches FPCR back to the guest mode if a callout left the host mode in it.</summary>
    void EnsureGuestFpcr()
    {
        if (!_fpcrHost) return;
        _a.LdrX(X16, SP, FrameGuestFpcr);
        _a.Msr(SysReg.Fpcr, X16);
        _fpcrHost = false;
    }

    /// <summary>IR ops that need the guest FPCR: FP arithmetic, and every control transfer (labels merge paths, exits
    /// chain into blocks that start in the guest mode).</summary>
    static bool NeedsGuestFpcr(IrOp op) => op is IrOp.FpOp or IrOp.VecOp or IrOp.Label or IrOp.CondBranch or IrOp.BranchIf
        or IrOp.ExitStatic or IrOp.ExitDynamic;

    /// <summary>
    /// Calls the interpreter handler of <paramref name="op"/> with FPCR in the host mode. The guest mode is restored
    /// right after the call, or with <see cref="A64Opt.LazyFpcr"/> (unless <paramref name="restore"/>) only before the
    /// next op that needs it, so a run of callouts separated by integer code switches FPCR twice instead of twice per call.
    /// </summary>
    void CallHandler(uint op, uint ins, uint pc, bool restore = false)
    {
        var a = _a;
        if (!_fpcrHost)
        {
            a.LdrX(X16, SP, FrameHostFpcr);
            a.Msr(SysReg.Fpcr, X16);
        }
        a.MovSp(X0, State); // mov x0, x19 (ADD #0 is fine for non-SP registers too)
        a.MovImm(X1, op);
        a.MovImm(X2, ins);
        a.MovImm(X3, pc);
        a.LdrLit(X16, _rt.Callout);
        a.Blr(X16);
        if (_lazyFpcr && !restore) _fpcrHost = true;
        else
        {
            a.LdrX(X16, SP, FrameGuestFpcr);
            a.Msr(SysReg.Fpcr, X16);
            _fpcrHost = false;
        }
        a.Cbnz(X0, _abort); // the abort path ends in the epilogue, which sets the host mode anyway
    }

    void SetFlagOut(int t, Cond c)
    {
        if (t < 0) return;
        int r = Def(t, X3);
        _a.Cset(r, c);
        Commit(t, r);
    }

    /// <summary>Sets the host flags for Wn - t (t as an immediate when encodable).</summary>
    void CmpWith(int n, int t)
    {
        if (IsConst(t, out uint v))
        {
            if (IsAddImm(v)) { _a.CmpImm(n, v); return; }
            if (IsAddImm(unchecked(0u - v))) { _a.CmnImm(n, unchecked(0u - v)); return; }
        }
        _a.Cmp(n, UseZ(t, X17));
    }

    void Bin(IrInst x)
    {
        var a = _a;
        int d = Def(x.D, X0);
        int A = x.A, B = x.B;
        bool commutative = x.Op is IrOp.Add or IrOp.And or IrOp.Or or IrOp.Xor or IrOp.Mul;
        if (commutative && IsConst(A, out _) && !IsConst(B, out _)) (A, B) = (B, A);
        int n = UseZ(A, X16);
        if (IsConst(B, out uint v))
        {
            switch (x.Op)
            {
                case IrOp.Add when n != ZR:
                    if (IsAddImm(v)) { a.AddImm(d, n, v); Commit(x.D, d); return; }
                    if (IsAddImm(unchecked(0u - v))) { a.SubImm(d, n, unchecked(0u - v)); Commit(x.D, d); return; }
                    break;
                case IrOp.Sub when n != ZR:
                    if (IsAddImm(v)) { a.SubImm(d, n, v); Commit(x.D, d); return; }
                    if (IsAddImm(unchecked(0u - v))) { a.AddImm(d, n, unchecked(0u - v)); Commit(x.D, d); return; }
                    break;
                case IrOp.And when TryLogicalImm(v, false, out _): a.AndImm(d, n, v); Commit(x.D, d); return;
                case IrOp.Or when TryLogicalImm(v, false, out _): a.OrrImm(d, n, v); Commit(x.D, d); return;
                case IrOp.Xor when TryLogicalImm(v, false, out _): a.EorImm(d, n, v); Commit(x.D, d); return;
                case IrOp.AndNot when TryLogicalImm(~v, false, out _): a.AndImm(d, n, ~v); Commit(x.D, d); return;
                case IrOp.OrNot when TryLogicalImm(~v, false, out _): a.OrrImm(d, n, ~v); Commit(x.D, d); return;
            }
        }
        int m = UseZ(B, X17);
        switch (x.Op)
        {
            case IrOp.Add: a.Add(d, n, m); break;
            case IrOp.Sub: a.Sub(d, n, m); break;
            case IrOp.And: a.And(d, n, m); break;
            case IrOp.Or: a.Orr(d, n, m); break;
            case IrOp.Xor: a.Eor(d, n, m); break;
            case IrOp.AndNot: a.Bic(d, n, m); break;
            case IrOp.OrNot: a.Orn(d, n, m); break;
            default: a.Mul(d, n, m); break;
        }
        Commit(x.D, d);
    }

    void ShiftOp(IrInst x)
    {
        var a = _a;
        int d = Def(x.D, X0);
        int s = UseZ(x.A, X16);
        if (IsConst(x.B, out uint k))
        {
            int n = (int)(k & 31);
            if (n == 0) a.Mov(d, s);
            else switch (x.Op)
            {
                case IrOp.Shl: a.LslImm(d, s, n); break;
                case IrOp.Shr: a.LsrImm(d, s, n); break;
                case IrOp.Sar: a.AsrImm(d, s, n); break;
                default: a.RorImm(d, s, n); break;
            }
        }
        else
        {
            int m = Use(x.B, X17); // W-register variable shifts use the amount modulo 32, like the IR
            switch (x.Op)
            {
                case IrOp.Shl: a.Lslv(d, s, m); break;
                case IrOp.Shr: a.Lsrv(d, s, m); break;
                case IrOp.Sar: a.Asrv(d, s, m); break;
                default: a.Rorv(d, s, m); break;
            }
        }
        Commit(x.D, d);
    }

    /// <summary>ARM AddWithCarry with NZCV outputs (SubC: x - y with ARM's inverted borrow, which is ARM64's too).</summary>
    void ArithFlags(IrInst x)
    {
        var a = _a;
        bool sub = x.Op == IrOp.SubC;
        int n = UseZ(x.A, X16);
        int d = x.D >= 0 ? Def(x.D, X0) : ZR;
        // carry-in: -1 = none (0 for add, 1 for sub)
        int cinConst = -1;
        if (x.C < 0) cinConst = sub ? 1 : 0;
        else if (IsConst(x.C, out uint cv)) cinConst = (int)(cv & 1);
        bool plain = cinConst == (sub ? 1 : 0);
        if (plain && IsConst(x.B, out uint v) && n != ZR)
        {
            if (IsAddImm(v)) { if (sub) a.SubsImm(d, n, v); else a.AddsImm(d, n, v); goto flags; }
        }
        int m = UseZ(x.B, X17);
        if (plain)
        {
            if (sub) a.Subs(d, n, m); else a.Adds(d, n, m);
        }
        else
        {
            if (cinConst == 1) a.Cmp(ZR, ZR);                    // C = 1
            else if (cinConst == 0) a.Adds(ZR, ZR, ZR);          // C = 0
            else a.CmpImm(Use(x.C, X1), 1);                      // C = (carry temp == 1)
            if (sub) a.Sbcs(d, n, m); else a.Adcs(d, n, m);
        }
    flags:
        HostFlagsAre(x.Fn, x.Fz, x.Fc, x.Fv);
        SetFlagOut(x.Fn, Cond.MI);
        SetFlagOut(x.Fz, Cond.EQ);
        SetFlagOut(x.Fc, Cond.HS);
        SetFlagOut(x.Fv, Cond.VS);
        if (x.D >= 0) Commit(x.D, d);
    }

    /// <summary>ARM register-controlled shift (Shift_C with the amount in the low byte of Rs).</summary>
    void ShiftReg(IrInst x)
    {
        var a = _a;
        uint type = x.Imm;
        bool wantC = x.Fc >= 0;
        a.Uxtb(X0, UseZ(x.B, X0));          // w0 = amount 0..255
        int v = UseZ(x.A, X1);
        switch (type)
        {
            case 0: // LSL
                a.Lslv(X2, v, X0, true);         // 64-bit: amounts 32..63 shift the value out of the low word
                if (wantC) a.Ubfx(X3, X2, 32, 1, true);
                a.CmpImm(X0, 32);
                a.Csel(X2, ZR, X2, Cond.HS);
                if (wantC) a.Csel(X3, ZR, X3, Cond.HI);
                break;
            case 1: // LSR
                a.Lsrv(X2, v, X0, true);
                if (wantC) { a.LslImm(X3, v, 1, true); a.Lsrv(X3, X3, X0, true); a.AndImm(X3, X3, 1); }
                a.CmpImm(X0, 32);
                a.Csel(X2, ZR, X2, Cond.HS);
                if (wantC) a.Csel(X3, ZR, X3, Cond.HI);
                break;
            case 2: // ASR: amounts >= 32 act like 31 (sign fill); clamp to 63 for the 64-bit shift
                a.MovImm(X4, 63);
                a.CmpImm(X0, 63);
                a.Csel(X4, X0, X4, Cond.LO);
                a.Sxtw(X5, v);
                a.Asrv(X2, X5, X4, true);
                if (wantC) { a.LslImm(X3, X5, 1, true); a.Asrv(X3, X3, X4, true); a.AndImm(X3, X3, 1); }
                a.Mov(X2, X2);                   // keep the low word only (zero-extended)
                break;
            default: // ROR
                a.Rorv(X2, v, X0);
                if (wantC) a.LsrImm(X3, X2, 31);
                break;
        }
        if (wantC)
        {
            a.CmpImm(X0, 0);
            a.Csel(X3, UseZ(x.C, X4), X3, Cond.EQ); // amount 0: carry unchanged
            Commit(x.Fc, X3);
        }
        Commit(x.D, X2);
    }

    /// <summary>Jumps to the label when ARM condition Imm does not hold (A=N, B=Z, C=C, E=V).</summary>
    void CondBranch(IrInst x)
    {
        var a = _a;
        int skip = _labels[x.Imm2];
        uint cond = x.Imm;
        if (_fuse && HostFlagsMatch(x))
        {
            // the host NZCV are the guest flags these temps hold (0/1 values from the same ADDS/SUBS/TST): skip on the
            // inverse condition (ARM32 and ARM64 condition codes are the same)
            a.BCond(Invert((Cond)cond), skip);
            return;
        }
        void If(int t, bool set) { int r = UseZ(t, X0); if (set) a.Cbnz(r, skip); else a.Cbz(r, skip); }
        switch (cond)
        {
            case 0: If(x.B, false); break;  // EQ: skip when Z clear
            case 1: If(x.B, true); break;   // NE
            case 2: If(x.C, false); break;  // CS
            case 3: If(x.C, true); break;   // CC
            case 4: If(x.A, false); break;  // MI
            case 5: If(x.A, true); break;   // PL
            case 6: If(x.E, false); break;  // VS
            case 7: If(x.E, true); break;   // VC
            // compound conditions on the full flag values, exactly like Arm.CondPassed (a flag field can hold more than
            // 0/1 after an UNPREDICTABLE encoding wrote past R15)
            case 8: case 9: // HI: C != 0 && Z == 0 ; LS: the opposite
                a.CmpImm(Use(x.C, X1), 0);
                a.Cset(X0, Cond.NE);
                a.CmpImm(Use(x.B, X1), 0);
                a.Csel(X0, X0, ZR, Cond.EQ);
                if (cond == 8) a.Cbz(X0, skip); else a.Cbnz(X0, skip);
                break;
            case 10: case 11: // GE: N == V ; LT: N != V
                a.Cmp(UseZ(x.A, X1), UseZ(x.E, X2));
                a.BCond(cond == 10 ? Cond.NE : Cond.EQ, skip);
                break;
            case 12: case 13: // GT: Z == 0 && N == V ; LE: the opposite
                a.Cmp(UseZ(x.A, X1), UseZ(x.E, X2));
                a.Cset(X0, Cond.EQ);
                a.CmpImm(Use(x.B, X1), 0);
                a.Csel(X0, X0, ZR, Cond.EQ);
                if (cond == 12) a.Cbz(X0, skip); else a.Cbnz(X0, skip);
                break;
            default: break; // AL
        }
    }

    // ------------------------------------------------------------------ exclusive monitor

    static readonly ulong GensBase = (ulong)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(Arm.ExclusiveGens, 0);
    static readonly int OffExAddr = StateLayout.Of(nameof(CpuState.ExclusiveAddr));
    static readonly int OffExValue = StateLayout.Of(nameof(CpuState.ExclusiveValue));
    static readonly int OffExValid = StateLayout.Of(nameof(CpuState.ExclusiveValid));
    static readonly int OffExGen = StateLayout.Of(nameof(CpuState.ExclusiveGen));

    /// <summary>x3 = address of the generation counter of the granule of guest address w<paramref name="addr"/>.</summary>
    void GenPointer(int addr)
    {
        _a.Ubfx(X2, addr, 3, 12); // (addr >> 3) & (ExclusiveStripes - 1)
        _a.LdrLit(X3, GensBase);
        _a.Add(X3, X3, X2, Shift.Lsl, 2, x: true);
    }

    /// <summary>LDREX / LDREXD: Arm.ReserveExclusive + read + SetExclusive.</summary>
    void LoadExclusive(IrInst x)
    {
        var a = _a;
        bool dual = x.Imm == 1;
        LoadTo(X1, x.A);
        GenPointer(X1);
        int spin = a.NewLabel(), ok = a.NewLabel();
        a.Bind(spin);
        a.Ldar(X4, X3);                       // acquire: the value is read after the generation snapshot
        a.Tbz(X4, 0, ok);
        a.Yield();                            // an exclusive store of another core is in flight
        a.B(spin);
        a.Bind(ok);
        StState(X4, OffExGen);
        if (dual) a.LdStReg(LsKind.LdrX, X6, MemBase, X1); // one 64-bit read, like Volatile.Read(ulong)
        else a.LdStReg(LsKind.LdrW, X6, MemBase, X1);      // zero-extended to 64 bits, like SetExclusive(uint)
        StState(X1, OffExAddr);
        a.StrX(X6, State, OffExValue);
        a.MovImm(X7, 1);
        StState(X7, OffExValid);
        if (dual && x.D2 >= 0) { int d2 = Def(x.D2, X5); a.LsrImm(d2, X6, 32, true); Commit(x.D2, d2); }
        if (x.D >= 0) { int d = Def(x.D, X0); a.Mov(d, X6); Commit(x.D, d); }
    }

    /// <summary>
    /// STREX / STREXD: Arm.StoreExclusive32/64 (lock the granule's generation snap -> snap+1, compare-and-swap the
    /// value against what LDREX read, release snap+2 on success or snap on failure). The compare-and-swaps are
    /// LDAXR/STLXR loops (ARMv8.0: no LSE needed); an unaligned address (which the guest cannot use with exclusives on
    /// hardware) gets a plain compare and store instead of an alignment fault.
    /// </summary>
    void StoreExclusive(IrInst x)
    {
        var a = _a;
        bool dual = x.Imm == 1;
        int fail = a.NewLabel(), done = a.NewLabel(), lockGen = a.NewLabel(), lockFail = a.NewLabel(), release = a.NewLabel();
        int unaligned = a.NewLabel(), casLoop = a.NewLabel(), valueFail = a.NewLabel(), valueDiffers = a.NewLabel();
        LoadTo(X6, x.B);
        if (dual) { int hi = UseZ(x.C, X7); a.Orr(X6, X6, hi, Shift.Lsl, 32, x: true); } // Rt | Rt2 << 32
        LoadTo(X1, x.A);
        LdState(X16, OffExValid);
        a.Cbz(X16, fail);
        LdState(X16, OffExAddr);
        a.Cmp(X16, X1);
        a.BCond(Cond.NE, fail);
        StState(ZR, OffExValid);
        GenPointer(X1);
        LdState(X4, OffExGen);
        a.AddImm(X5, X4, 1);
        a.Bind(lockGen);                      // CAS gen: snap -> snap + 1 (odd: store in flight)
        a.Ldaxr(X8, X3);
        a.Cmp(X8, X4);
        a.BCond(Cond.NE, lockFail);
        a.Stlxr(X17, X5, X3);
        a.Cbnz(X17, lockGen);
        // value CAS against what LDREX read
        a.AddExt(X2, MemBase, X1, Extend.Uxtw);
        a.LdrX(X7, State, OffExValue);
        a.TstImm(X1, dual ? 7u : 3u);
        a.BCond(Cond.NE, unaligned);
        a.Bind(casLoop);
        a.Ldaxr(X8, X2, dual);
        a.Cmp(X8, X7, dual);
        a.BCond(Cond.NE, valueFail);
        a.Stlxr(X17, X6, X2, dual);
        a.Cbnz(X17, casLoop);
        a.MovImm(X0, 1);                      // ok
        a.B(release);
        a.Bind(unaligned);
        a.LdSt(dual ? LsKind.LdrX : LsKind.LdrW, X8, X2, 0);
        a.Cmp(X8, X7, dual);
        a.BCond(Cond.NE, valueDiffers);
        a.LdSt(dual ? LsKind.StrX : LsKind.StrW, X6, X2, 0);
        a.MovImm(X0, 1);
        a.B(release);
        a.Bind(valueFail);
        a.Clrex();
        a.Bind(valueDiffers);
        a.Mov(X0, ZR);
        a.Bind(release);
        // release: snap + 2 on success, snap on failure
        a.AddImm(X5, X4, 2);
        a.CmpImm(X0, 0);
        a.Csel(X4, X5, X4, Cond.NE);
        a.Stlr(X4, X3);
        if (Memory.DirtyPages.Enabled)
        {
            int clean = a.NewLabel();
            a.Cbz(X0, clean);
            MarkDirtyReg(X1);
            a.Bind(clean);
        }
        a.EorImm(X0, X0, 1);                  // status: 0 ok, 1 failed
        a.B(done);
        a.Bind(lockFail);
        a.Clrex();
        a.Bind(fail);
        a.MovImm(X0, 1);
        a.Bind(done);
        int d = Def(x.D, X2);
        if (d != X0) a.Mov(d, X0);
        Commit(x.D, d);
    }


    // ------------------------------------------------------------------ dirty pages / store log

    /// <summary>Marks the page of the guest address in temp t as written (after the store); clobbers x0, x16, x17.</summary>
    void MarkDirty(int t)
    {
        if (!Memory.DirtyPages.Enabled) return;
        if (_dirtyReg && !IsConst(t, out _) && InReg(t, out int r)) { MarkDirtyReg(r); return; }
        LoadTo(X0, t);
        MarkDirtyReg(X0);
    }

    /// <summary>Dirty mark for the guest address in w<paramref name="addr"/> (the byte is only written while clear).</summary>
    void MarkDirtyReg(int addr)
    {
        var a = _a;
        int done = a.NewLabel();
        a.LsrImm(X16, addr, Memory.DirtyPages.PageShift);
        a.SubImm(X17, MemBase, (uint)Memory.DirtyPages.TableBytes, true);
        a.LdStReg(LsKind.Ldrb, X8, X17, X16, xIndex: true);
        a.Cbnz(X8, done);
        a.MovImm(X8, 1);
        a.LdStReg(LsKind.Strb, X8, X17, X16, xIndex: true);
        a.Bind(done);
    }

    static readonly int OffStoreLog = StateLayout.Of(nameof(CpuState.StoreLog));
    static readonly int OffStoreLogCount = StateLayout.Of(nameof(CpuState.StoreLogCount));

    /// <summary>Appends {address, size, old value} (16-byte entry) to the state's store log.</summary>
    void EmitStoreLog(IrInst x)
    {
        var a = _a;
        int size = x.Op == IrOp.Store8 ? 1 : x.Op == IrOp.Store16 ? 2 : 4;
        LoadTo(X1, x.A);
        LdState(X16, OffStoreLogCount);
        a.LdrX(X17, State, OffStoreLog);
        a.Add(X17, X17, X16, Shift.Lsl, 4, x: true);
        a.Str(X1, X17, 0);
        a.MovImm(X2, (uint)size);
        a.Str(X2, X17, 4);
        a.LdStReg(size == 1 ? LsKind.Ldrb : size == 2 ? LsKind.Ldrh : LsKind.LdrW, X2, MemBase, X1);
        a.Str(X2, X17, 8);
        a.AddImm(X16, X16, 1);
        StState(X16, OffStoreLogCount);
    }

    // ------------------------------------------------------------------ VFP

    /// <summary>
    /// Scalar VFP operation. Fast path in the Vita's FPSCR mode (DN=1, FZ=1, round to nearest, no short vectors),
    /// which is the guest FPCR mode generated code runs under: AArch64 FP arithmetic, conversions and compares then
    /// produce the ARMv7 results bit for bit (flush-to-zero with tininess before rounding, default NaN, saturating
    /// conversions with NaN -> 0). Any other FPSCR mode runs the interpreter handler.
    /// </summary>
    void FpOp(IrInst x)
    {
        var a = _a;
        var kind = (FpKind)(x.Imm & 0xFF);
        bool dbl = (x.Imm & (uint)FpFlags.Double) != 0;
        var op = (x.Imm & (uint)FpFlags.OtherOp) != 0 ? Op.Vfp_Other : Op.Vfp_Arith;
        int dOff = OffD + (int)(x.Imm4 & 0xFF), nOff = OffD + (int)((x.Imm4 >> 8) & 0xFF), mOff = OffD + (int)((x.Imm4 >> 16) & 0xFF);
        int slow = a.NewLabel(), done = a.NewLabel();
        var ld = dbl ? LsKind.LdrD : LsKind.LdrS;
        var st = dbl ? LsKind.StrD : LsKind.StrS;

        LdState(X16, OffFpscr);
        if (_fastFpCheck)
        {
            // FPSCR[25:16] == DN FZ, RMode 0, STRIDE 0, LEN 0, and bit 19 (reserved on the Vita) clear; a set bit 19 only
            // takes the handler, which is always exact
            a.Ubfx(X16, X16, 16, 10);
            a.CmpImm(X16, 0x300);
        }
        else
        {
            a.MovImm(X17, 0x03F70000);
            a.And(X16, X16, X17);
            a.MovImm(X17, 0x03000000);
            a.Cmp(X16, X17);
        }
        a.BCond(Cond.NE, slow);

        switch (kind)
        {
            case FpKind.Add: case FpKind.Sub: case FpKind.Mul: case FpKind.NMul: case FpKind.Div:
                a.LdSt(ld, 1, State, nOff);
                a.LdSt(ld, 2, State, mOff);
                switch (kind)
                {
                    case FpKind.Add: a.Fadd(0, 1, 2, dbl); break;
                    case FpKind.Sub: a.Fsub(0, 1, 2, dbl); break;
                    case FpKind.Mul: a.Fmul(0, 1, 2, dbl); break;
                    case FpKind.NMul: a.Fnmul(0, 1, 2, dbl); break; // -(n*m): a NaN product negates the default NaN, like VNMUL
                    default: a.Fdiv(0, 1, 2, dbl); break;
                }
                a.LdSt(st, 0, State, dOff);
                break;
            case FpKind.Mla: case FpKind.Mls: case FpKind.Nmla: case FpKind.Nmls:
                // d = (+/-)d (+/-) round(n * m): the product is rounded on its own (not fused)
                a.LdSt(ld, 1, State, nOff);
                a.LdSt(ld, 2, State, mOff);
                a.LdSt(ld, 3, State, dOff);
                a.Fmul(1, 1, 2, dbl);
                if (kind is FpKind.Nmla or FpKind.Nmls) a.Fneg(3, 3, dbl);
                if (kind is FpKind.Mla or FpKind.Nmls) a.Fadd(0, 3, 1, dbl); else a.Fsub(0, 3, 1, dbl);
                a.LdSt(st, 0, State, dOff);
                break;
            case FpKind.Sqrt:
                a.LdSt(ld, 1, State, mOff);
                a.Fsqrt(0, 1, dbl);
                a.LdSt(st, 0, State, dOff);
                break;
            case FpKind.Cmp: case FpKind.CmpZero:
                a.LdSt(ld, 0, State, dOff);
                if (kind == FpKind.CmpZero) a.FcmpZero(0, dbl);
                else { a.LdSt(ld, 1, State, mOff); a.Fcmp(0, 1, dbl); }
                a.Mrs(X16, SysReg.Nzcv);              // FCMP's NZCV are VCMP's (unordered 0011, equal 0110, less 1000, greater 0010)
                LdState(X17, OffFpscr);
                a.AndImm(X17, X17, 0x0FFFFFFF);
                a.Orr(X17, X17, X16);
                StState(X17, OffFpscr);
                break;
            case FpKind.CvtToDouble:
                a.LdSt(LsKind.LdrS, 1, State, mOff);
                a.FcvtSD(0, 1);
                a.LdSt(LsKind.StrD, 0, State, dOff);
                break;
            case FpKind.CvtToSingle:
                a.LdSt(LsKind.LdrD, 1, State, mOff);
                a.FcvtDS(0, 1);
                a.LdSt(LsKind.StrS, 0, State, dOff);
                break;
            case FpKind.CvtFromS32: case FpKind.CvtFromU32:
                LdState(X16, mOff);
                if (kind == FpKind.CvtFromS32) a.Scvtf(0, X16, dbl); else a.Ucvtf(0, X16, dbl);
                a.LdSt(st, 0, State, dOff);
                break;
            case FpKind.CvtToS32: case FpKind.CvtToS32R: case FpKind.CvtToU32: case FpKind.CvtToU32R:
                a.LdSt(ld, 1, State, mOff);
                switch (kind)
                {
                    case FpKind.CvtToS32: a.Fcvtzs(X16, 1, dbl); break;
                    case FpKind.CvtToS32R: a.Fcvtns(X16, 1, dbl); break;   // FPSCR rounding = nearest in this mode
                    case FpKind.CvtToU32: a.Fcvtzu(X16, 1, dbl); break;
                    default: a.Fcvtnu(X16, 1, dbl); break;
                }
                StState(X16, dOff);
                break;
            case FpKind.CvtToHalf:
                // IEEE half (AHP=0), FZ16=0: half denormals are produced; single inputs are flushed (FZ); NaN -> 0x7E00
                a.LdSt(LsKind.LdrS, 1, State, mOff);
                a.FcvtSH(0, 1);
                a.LdSt(LsKind.StrH, 0, State, dOff);
                break;
            case FpKind.CvtFromHalf:
                a.LdSt(LsKind.LdrH, 1, State, mOff);
                a.FcvtHS(0, 1);
                a.LdSt(LsKind.StrS, 0, State, dOff);
                break;
            default:
                a.B(slow);
                break;
        }
        a.B(done);
        // both paths write d (at most 8 bytes at its offset): cached SIMD copies there are stale; the handler path
        // clobbers v16-v31, so it reloads the surviving cached registers from the (current) register file
        VcInvalidate((int)(x.Imm4 & 0xFF), 8);
        a.Bind(slow);
        CallHandler((uint)op, x.Imm2, x.Imm3, restore: true); // merges with the fast path, which is in the guest mode
        VcReload();
        a.Bind(done);
    }

    // ------------------------------------------------------------------ runtime stubs

    /// <summary>
    /// The entry trampoline (x0 = CpuState*, x1 = block entry): saves x19-x30, builds the frame (host FPCR, guest FPCR,
    /// spill slots), loads x19/x20, switches FPCR to the guest mode and jumps to the block. Blocks leave through the
    /// epilogue (returned in <paramref name="epilogueOffset"/>), which restores the host FPCR.
    /// </summary>
    public static byte[] EmitTrampoline(out int epilogueOffset, bool pinnedCounters = false)
    {
        var a = new Arm64Assembler();
        a.Stp(FP, LR, SP, -SavedRegs, PairMode.Pre);
        a.Stp(19, 20, SP, 16);
        a.Stp(21, 22, SP, 32);
        a.Stp(23, 24, SP, 48);
        a.Stp(25, 26, SP, 64);
        a.Stp(27, 28, SP, 80);
        a.MovSp(FP, SP);
        a.SubImm(SP, SP, FrameSize, true);
        a.MovSp(State, X0);
        a.LdrX(MemBase, State, OffMemBase); // the state's guest memory base: its process's address space
        if (pinnedCounters)
        {
            // x28 = CycleAcc - CheckWindow (the window is full when it reaches 0), x26/x27 = NBlocks/NInstr
            a.LdrX(PinBudget, State, OffCycleAcc);
            a.LdrX(X17, State, OffCheckWindow);
            a.Sub(PinBudget, PinBudget, X17, x: true);
            a.LdrX(PinBlocks, State, OffNBlocks);
            a.LdrX(PinInstr, State, OffNInstr);
        }
        a.Mrs(X16, SysReg.Fpcr);
        a.StrX(X16, SP, FrameHostFpcr);
        a.MovImm(X17, FpcrGuestMask);
        a.Bic(X16, X16, X17, x: true);
        a.OrrImm(X16, X16, FpcrGuestBits, true);
        a.StrX(X16, SP, FrameGuestFpcr);
        a.Msr(SysReg.Fpcr, X16);
        a.Br(X1);
        epilogueOffset = a.Count * 4;
        if (pinnedCounters)
        {
            a.LdrX(X17, State, OffCheckWindow);
            a.Add(X16, PinBudget, X17, x: true);
            a.StrX(X16, State, OffCycleAcc);
            a.StrX(PinBlocks, State, OffNBlocks);
            a.StrX(PinInstr, State, OffNInstr);
        }
        a.LdrX(X16, SP, FrameHostFpcr);
        a.Msr(SysReg.Fpcr, X16);
        a.AddImm(SP, SP, FrameSize, true);
        a.Ldp(27, 28, SP, 80);
        a.Ldp(25, 26, SP, 64);
        a.Ldp(23, 24, SP, 48);
        a.Ldp(21, 22, SP, 32);
        a.Ldp(19, 20, SP, 16);
        a.Ldp(FP, LR, SP, SavedRegs, PairMode.Post);
        a.Ret();
        return a.Finish();
    }

    /// <summary>
    /// An entry that continues at the state's pc through the dispatch table, like an indirect exit (no block accounting):
    /// the dispatcher enters here after an HLE call instead of looking the return address up in managed code. A miss
    /// returns to the dispatcher with no exit slot, which then looks the block up as usual.
    /// </summary>
    public static byte[] EmitDispatchEntry(ulong epilogue)
    {
        var a = new Arm64Assembler();
        int miss = a.NewLabel();
        a.Ldr(X0, State, OffR + 60);
        a.Ldr(X1, State, OffThumb);
        a.Orr(X1, X0, X1);                                   // key = pc | thumb
        a.Ubfx(X2, X0, 1, NativeRuntime.DispatchBits);
        a.LdrX(X3, State, OffDispatchTable);
        a.Add(X3, X3, X2, Shift.Lsl, 4, x: true);            // 16-byte entries
        a.Ldar(X4, X3);
        a.Cmp(X4, X1);
        a.BCond(Cond.NE, miss);
        a.AddImm(X5, X3, 8, true);
        a.Ldar(X6, X5, true);
        a.Ldr(X4, X3, 0);
        a.Cmp(X4, X1);
        a.BCond(Cond.NE, miss);
        a.Br(X6);
        a.Bind(miss);
        a.StrX(ZR, State, OffLastExitSlot);
        a.LdrLit(X16, epilogue);
        a.Br(X16);
        return a.Finish();
    }

    /// <summary>
    /// Instruction-cache maintenance for code written through another mapping (x0 = start, x1 = length): clean the
    /// data cache to the point of unification and invalidate the instruction cache line by line (line sizes from
    /// CTR_EL0, which Linux lets user space read), then synchronize. This is what __clear_cache does; Linux and Android
    /// export no libc function for it, so the backend generates it.
    /// </summary>
    public static byte[] EmitCacheFlush()
    {
        var a = new Arm64Assembler();
        int dLoop = a.NewLabel(), iLoop = a.NewLabel(), dDone = a.NewLabel(), iDone = a.NewLabel();
        a.Mrs(X3, SysReg.CtrEl0);
        a.MovImm(X5, 4);
        a.Ubfx(X4, X3, 16, 4, true);        // DminLine: log2 words
        a.Lslv(X4, X5, X4, true);           // data line bytes
        a.AndImm(X6, X3, 0xF, true);        // IminLine
        a.Lslv(X6, X5, X6, true);           // instruction line bytes
        a.Add(X1, X0, X1, x: true);         // end
        a.SubImm(X7, X4, 1, true);
        a.Bic(X2, X0, X7, x: true);
        a.Bind(dLoop);
        a.Cmp(X2, X1, true);
        a.BCond(Cond.HS, dDone);
        a.DcCvau(X2);
        a.Add(X2, X2, X4, x: true);
        a.B(dLoop);
        a.Bind(dDone);
        a.DsbIsh();
        a.SubImm(X7, X6, 1, true);
        a.Bic(X2, X0, X7, x: true);
        a.Bind(iLoop);
        a.Cmp(X2, X1, true);
        a.BCond(Cond.HS, iDone);
        a.IcIvau(X2);
        a.Add(X2, X2, X6, x: true);
        a.B(iLoop);
        a.Bind(iDone);
        a.DsbIsh();
        a.Isb();
        a.Ret();
        return a.Finish();
    }
}

public sealed unsafe partial class Arm64Emitter
{
    /// <summary>
    /// Windows on ARM: the guest-memory lazy-commit vectored exception handler (the ARM64 twin of the hand-assembled
    /// x64 one in <see cref="Memory.HostVirtualMemory.InstallLazyCommitHandler"/>). x0 = EXCEPTION_POINTERS*; an access
    /// violation whose address (ExceptionInformation[1], at the same offset as on x64) lies in [base, base + size)
    /// commits that page with VirtualAlloc and returns EXCEPTION_CONTINUE_EXECUTION (-1), anything else returns
    /// EXCEPTION_CONTINUE_SEARCH (0). Pure machine code: it never re-enters the CLR.
    /// </summary>
    public static byte[] EmitLazyCommitHandler(ulong memBase, ulong size, ulong virtualAlloc)
    {
        var a = new Arm64Assembler();
        int notOurs = a.NewLabel();
        a.Stp(FP, LR, SP, -16, PairMode.Pre);
        a.MovSp(FP, SP);
        a.LdrX(X1, X0, 0);                   // ExceptionRecord
        a.Ldr(X2, X1, 0);                    // ExceptionCode
        a.MovImm(X3, 0xC0000005);
        a.Cmp(X2, X3);
        a.BCond(Cond.NE, notOurs);
        a.LdrX(X4, X1, 0x28);                // faulting address
        a.LdrLit(X5, memBase);
        a.Sub(X4, X4, X5, x: true);
        a.LdrLit(X6, size);
        a.Cmp(X4, X6, true);
        a.BCond(Cond.HS, notOurs);
        a.LdrX(X0, X1, 0x28);
        a.AndImm(X0, X0, ~0xFFFUL, true);    // page
        a.MovImm(X1, 0x1000);                // size
        a.MovImm(X2, 0x1000);                // MEM_COMMIT
        a.MovImm(X3, 4);                     // PAGE_READWRITE
        a.LdrLit(X16, virtualAlloc);
        a.Blr(X16);
        a.Cbz(X0, notOurs, true);
        a.Movn(X0, 0);                       // -1: EXCEPTION_CONTINUE_EXECUTION
        a.Ldp(FP, LR, SP, 16, PairMode.Post);
        a.Ret();
        a.Bind(notOurs);
        a.Mov(X0, ZR);                       // 0: EXCEPTION_CONTINUE_SEARCH
        a.Ldp(FP, LR, SP, 16, PairMode.Post);
        a.Ret();
        return a.Finish();
    }
}

/// <summary>Debug view of generated ARM64 code (no disassembler is bundled: one hex word per instruction).</summary>
public static unsafe class Arm64Dump
{
    public static string Dump(nint code, int length)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i + 4 <= length; i += 4) sb.Append($"{i,5:X}: {*(uint*)(code + i):X8}\n");
        return sb.ToString();
    }
}
