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
    private const int X0 = 0, X1 = 1, X2 = 2, X3 = 3, X4 = 4, X5 = 5, X6 = 6, X7 = 7, X8 = 8, X16 = 16, X17 = 17;
    private const int State = 19, MemBase = 20;

    /// <summary>Callee-saved registers available for temps (survive callouts).</summary>
    private static readonly int[] s_calleeSaved = [21, 22, 23, 24, 25, 26, 27, 28];
    /// <summary>The callee-saved temps when x26-x28 hold the pinned block counters.</summary>
    private static readonly int[] s_calleeSavedPinned = [21, 22, 23, 24, 25];
    /// <summary>The callee-saved temps when only x28 (budget) and x26 (dirty-page table) are pinned (<see cref="Runtime.NoBlockCounts"/>).</summary>
    private static readonly int[] s_calleeSavedBudgetOnly = [21, 22, 23, 24, 25, 27];
    /// <summary>With <see cref="Runtime.NoBlockCounts"/>: the dirty-page table of the state's address space (guest base -
    /// <see cref="Memory.DirtyPages.TableBytes"/>), set by the trampoline.</summary>
    private const int PinDirty = 26;
    /// <summary>Caller-saved registers available for temps (not across callouts).</summary>
    private static readonly int[] s_callerSaved = [9, 10, 11, 12, 13, 14, 15];
    /// <summary>Pinned block counters (<see cref="A64Opt.PinnedCounters"/>): blocks, instructions, and CycleAcc - CheckWindow
    /// (the window is full once it is &gt;= 0). Loaded by the trampoline, stored back by the epilogue.</summary>
    private const int PinBlocks = 26, PinInstr = 27, PinBudget = 28;

    public const int SpillSlots = 64;
    private const int FrameHostFpcr = 0, FrameGuestFpcr = 8, SpillBase = 16;
    private const int FrameSize = SpillBase + 8 * SpillSlots; // 528, 16-byte aligned
    private const int SavedRegs = 96;                          // x19-x30

    /// <summary>FPCR bits the guest mode owns: AHP, DN, FZ, RMode, FZ16.</summary>
    private const uint FpcrGuestMask = 0x07C80000, FpcrGuestBits = 0x03000000; // DN | FZ, round to nearest

    // CpuState field offsets
    private static readonly int s_offR = 0;
    private static readonly int s_offN = StateLayout.Of(nameof(CpuState.N));
    private static readonly int s_offCycleAcc = StateLayout.Of(nameof(CpuState.CycleAcc));
    private static readonly int s_offCheckWindow = StateLayout.Of(nameof(CpuState.CheckWindow));
    private static readonly int s_offNBlocks = StateLayout.Of(nameof(CpuState.NBlocks));
    private static readonly int s_offNInstr = StateLayout.Of(nameof(CpuState.NInstr));
    private static readonly int s_offLastExitSlot = StateLayout.Of(nameof(CpuState.LastExitSlot));
    private static readonly int s_offLinkBreak = StateLayout.Of(nameof(CpuState.LinkBreak));
    private static readonly int s_offThumb = StateLayout.Of(nameof(CpuState.Thumb));
    private static readonly int s_offFpscr = StateLayout.Of(nameof(CpuState.Fpscr));
    private static readonly int s_offMemBase = StateLayout.Of(nameof(CpuState.MemBase));
    private static readonly int s_offDispatchTable = StateLayout.Of(nameof(CpuState.DispatchTable));
    private static readonly int s_offD = StateLayout.Of(nameof(CpuState.D));
    private static readonly int s_offExit = StateLayout.Of(nameof(CpuState.Exit));
    private static readonly int s_offTierUp = StateLayout.Of(nameof(CpuState.TierUpRequest));

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
        /// <summary>Linked static exits become direct branches patched into the code (<see cref="Runtime.PatchLinks"/>,
        /// set by the runtime that owns the code arena; with pinned counters only).</summary>
        PatchLinks = 512,
        /// <summary>Conditional instructions that fall through keep the dirty guest state cached across their region
        /// (the frontend's <see cref="ArmFrontend.DeferRegions"/>): flags a later instruction supersedes are never
        /// materialised, and only the slots the region writes are stored before its branch.</summary>
        DeferRegions = 1024,
        /// <summary>Exits end in tails shared by the block (one epilogue jump per block), the cold paths of patched exits
        /// go after the block's code (a linked exit is accounting, checks and the B), and unreachable abort paths are
        /// left out.</summary>
        ExitStubs = 2048,
        /// <summary>Static exits skip the stores of registers and flags their (already decoded) target block overwrites
        /// before reading them (<see cref="StateLiveness"/>): the N Z C V a compare-and-branch leaves are usually dead.</summary>
        Successors = 4096,
        /// <summary>With pinned counters, blocks keep only the cycle budget (x28): the executed block and instruction
        /// counts (statistics: the PERF cycles line) are not kept, which saves two instructions per block and frees
        /// x26/x27 for temps. VITASHARP_NATIVE_COUNTERS=1 keeps counting (<see cref="Runtime.NoBlockCounts"/>).</summary>
        NoBlockCounts = 8192,
        /// <summary>A guest DMB right after another, with no memory access between them, is dropped (<see cref="IrPasses.MergeFences"/>).</summary>
        MergeFences = 16384,
        /// <summary>Guest stores through one base register at constant offsets spanning less than a page, in straight-line
        /// code, share one dirty-page mark after the last of them (<see cref="PlanMarks"/>): pushes, STM and struct stores.</summary>
        GroupMarks = 32768,
        /// <summary>A block with scalar VFP code checks the FPSCR mode once at its entry instead of at every VFP op:
        /// in the Vita's mode (VMSR, the only way to change it, ends a block) every op runs inline with no handler path, so
        /// FpOps stop being call sites for register allocation; in another mode the block returns at once and the
        /// dispatcher interprets it (<see cref="CpuState.TierUpRequest"/> = 2).</summary>
        FpEntryCheck = 65536,
        /// <summary>Indirect exits read a dispatch-table entry's key and arena offset with one plain 64-bit load
        /// (<see cref="NativeRuntime.DispatchEntry"/>) instead of two acquire loads and a second key check.</summary>
        PackedDispatch = 131072,
        /// <summary>A block ending in a conditional branch is translated together with the (already executed) blocks
        /// that follow its not-taken path, up to 4 blocks / 128 instructions (<see cref="NativeArm64Compiler"/>): the
        /// guest state stays in temps across them, and each continued branch saves an exit and a block entry.</summary>
        Superblocks = 262144,
        /// <summary>The SIMD register cache is write-back: VecOp and inline FpOp results stay in v18-v31 until a branch,
        /// label, call, exit, eviction or another access to the register file in memory needs them stored.</summary>
        LazyVec = 524288,
        /// <summary>With <see cref="PackedDispatch"/>: the indirect-exit table is two-way set associative
        /// (<see cref="NativeRuntime.TwoWay"/>), so colliding hot targets stop sending each other through the dispatcher.</summary>
        AssocDispatch = 1048576,
        All = 2097151,
    }

    public static readonly string[] OptNames = ["pin", "backedge", "fuse", "pair", "vcache", "fpcr", "fpcheck", "dirty", "lit", "patch", "cond", "stub", "succ", "count", "fence", "group", "fpentry", "packed", "chain", "vlazy", "assoc"];

    /// <summary>Enabled optimisations (tests may change it; the trampoline's pinned-counter mode is fixed per runtime).</summary>
    public static A64Opt Opts = ParseOpts(Environment.GetEnvironmentVariable(Util.EnvVars.NativeA64NoOpt));

    public static A64Opt ParseOpts(string? noOpt)
    {
        if (string.IsNullOrWhiteSpace(noOpt))
        {
            return A64Opt.All;
        }

        A64Opt a64Opt = A64Opt.All;
        foreach (string raw in noOpt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return A64Opt.None;
            }

            int i = Array.FindIndex(OptNames, n => n.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                a64Opt &= ~(A64Opt)(1 << i);
            }
        }
        return a64Opt;
    }

    private static bool On(A64Opt a64Opt) => (Opts & a64Opt) != 0;

    /// <summary>Debug knob for tests: restricts the temp registers so spilling paths get exercised.</summary>
    public static int MaxTempRegisters = int.MaxValue;
    /// <summary>Debug: check the register allocation of every block (tests).</summary>
    public static bool VerifyAllocation;
    /// <summary>Verify mode: blocks record every guest store (see <see cref="X64Emitter.LogStores"/>).</summary>
    public static bool LogStores = NativeVerifier.Enabled;

    private readonly Arm64Assembler _assembler = new();
    private IrBlock _ir = null!;
    private int[] _loc = null!;
    private int[] _labels = null!;
    private int _abort;
    private bool _checkExit;
    private Runtime _rt;

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
        /// <summary>Linked static exits are patched into direct branches (<see cref="A64Opt.PinnedCounters"/> code only):
        /// each exit has a patch site, a B to the next instruction until linked, then a B to the target block; its link
        /// slot is two words, the target and the patch site (<see cref="PatchSites"/>, <see cref="NativeRuntime.Link"/>).</summary>
        public bool PatchLinks;
        /// <summary>With <see cref="PinnedCounters"/>: the trampoline and the blocks keep only the cycle budget, not the
        /// block and instruction counts (<see cref="A64Opt.NoBlockCounts"/>).</summary>
        public bool NoBlockCounts;
        /// <summary>Start of the code arena: dispatch-table entries hold code offsets from it (<see cref="A64Opt.PackedDispatch"/>; 0: not packed).</summary>
        public ulong CodeBase;
        /// <summary>The dispatch table is two-way set associative (<see cref="A64Opt.AssocDispatch"/>, packed entries only).</summary>
        public bool TwoWayDispatch;
    }

    /// <summary>Byte offsets in the last prepared block of each static exit's patch site (<see cref="Runtime.PatchLinks"/>),
    /// indexed like the link slots; -1 where an exit has none.</summary>
    public readonly List<int> PatchSites = new();

    /// <summary>The B instruction from <paramref name="site"/> to <paramref name="target"/>, or 0 when out of range (+-128 MiB).</summary>
    public static uint BranchWord(long site, long target)
    {
        long d = target - site;
        if ((d & 3) != 0 || d < -(1L << 27) || d >= 1L << 27)
        {
            return 0;
        }

        return 0x14000000u | (uint)((d >> 2) & 0x3FFFFFF);
    }

    /// <summary>An unlinked patch site: B to the next instruction (the link-slot path).</summary>
    public const uint UnlinkedSiteWord = 0x14000001;

    private bool _count = true;
    private bool _pin, _backEdge, _fuse, _pair, _vcache, _lazyFpcr, _fastFpCheck, _dirtyReg, _storeLit;

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
        _assembler.Reset();
        PatchSites.Clear();
        A64Opt opts = Opts;
        _pin = rt.PinnedCounters;
        _count = !(rt.PinnedCounters && rt.NoBlockCounts);
        _backEdge = (opts & A64Opt.BackEdgeBreak) != 0;
        _fuse = (opts & A64Opt.FuseBranch) != 0;
        _pair = (opts & A64Opt.PairState) != 0;
        _vcache = (opts & A64Opt.VecCache) != 0;
        _lazyFpcr = (opts & A64Opt.LazyFpcr) != 0;
        _fastFpCheck = (opts & A64Opt.FastFpCheck) != 0;
        _dirtyReg = (opts & A64Opt.DirtyReg) != 0;
        _storeLit = (opts & A64Opt.StoreLiteral) != 0;
        _stub = (opts & A64Opt.ExitStubs) != 0;
        _abortUsed = _slotPathUsed = _tailZeroUsed = false;
        bool fpEntry = false, calls = false;
        {
            Span<IrInst> all = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ir.Insts);
            bool checkFp = (opts & A64Opt.FpEntryCheck) != 0 && !NativeVerifier.Enabled;
            foreach (ref IrInst x in all)
            {
                if (x.Op == IrOp.FpOp && checkFp)
                {
                    x.Imm |= (uint)FpFlags.Checked;
                    fpEntry = true;
                }
                if (LinearScan.IsCallSite(x))
                {
                    calls = true;
                }
            }
        }
        _coldExits.Clear();
        _coldUnlinked.Clear();
        _coldMarks.Clear();
        ForgetHostFlags();
        _fpcrHost = false;
        Array.Fill(_vcOff, -1);
        Array.Clear(_vcDirty);
        _vcAny = _vcAnyDirty = false;
        _vlazy = _vcache && (opts & A64Opt.LazyVec) != 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int[]? loc = LinearScan.Allocate(ir, s_callerSaved, _pin ? _count ? s_calleeSavedPinned : s_calleeSavedBudgetOnly : s_calleeSaved, SpillSlots, MaxTempRegisters, VerifyAllocation, reuse: true);
        AllocTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (loc == null)
        {
            return false;
        }

        _loc = loc;
        PlanMarks(ir, (opts & A64Opt.GroupMarks) != 0);
        _labels = new int[ir.Labels];
        for (int i = 0; i < ir.Labels; i++)
        {
            _labels[i] = _assembler.NewLabel();
        }

        _abort = _assembler.NewLabel();
        _tail = _assembler.NewLabel();
        _tailZero = _assembler.NewLabel();
        _slotPath = _assembler.NewLabel();
        _checkExit = calls;
        int tierUp = _assembler.NewLabel(), fpMode = _assembler.NewLabel();
        if (tierUpCounter != 0)
        {
            _assembler.LdrLit(X16, (ulong)tierUpCounter);
            _assembler.Ldr(X17, X16, 0);
            _assembler.SubsImm(X17, X17, 1);
            _assembler.Str(X17, X16, 0);
            _assembler.BCond(Cond.EQ, tierUp);
        }
        if (fpEntry)
        {
            // FPSCR[25:16] == DN FZ, RMode 0, STRIDE 0, LEN 0, bit 19 clear (see FpOp)
            LdState(X16, s_offFpscr);
            _assembler.Ubfx(X16, X16, 16, 10);
            _assembler.CmpImm(X16, 0x300);
            _assembler.BCond(Cond.NE, fpMode);
        }
        int exitIndex = 0;
        Span<IrInst> insts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ir.Insts);
        for (int i = 0; i < insts.Length; i++)
        {
            if (_pair && i + 1 < insts.Length && TryPair(insts[i], insts[i + 1]))
            {
                i++;
                continue;
            }
            _cur = i;
            Lower(insts[i], slotFor, ref exitIndex);
        }
        _exitCost = 0; // the abort path charges the whole block
        if (_abortUsed || !_stub)
        {
            _assembler.Bind(_abort);
            EmitAccounting();
            ExitZero();
        }
        if (tierUpCounter != 0)
        {
            _assembler.Bind(tierUp);
            StorePc(ir.StartPc); // linked exits into this block may have left R15 to their cold paths (StateLiveness.ColdPc)
            if (!_stub)
            {
                _assembler.StrX(ZR, State, s_offLastExitSlot);
            }

            _assembler.MovImm(X16, 1);
            _assembler.Str(X16, State, s_offTierUp);
            if (_stub)
            {
                ExitZero();
            }
            else
            {
                JumpTo(rt.Epilogue);
            }
        }
        if (fpEntry)
        {
            // another FPSCR mode: nothing ran yet; the dispatcher interprets the block (CpuCore.Run)
            _assembler.Bind(fpMode);
            StorePc(ir.StartPc);
            if (!_stub)
            {
                _assembler.StrX(ZR, State, s_offLastExitSlot);
            }

            _assembler.MovImm(X16, 2);
            _assembler.Str(X16, State, s_offTierUp);
            if (_stub)
            {
                ExitZero();
            }
            else
            {
                JumpTo(rt.Epilogue);
            }
        }
        foreach ((int cold, int back) in _coldMarks)
        {
            // the first store to a clean page: set its byte (x16 = page index, kept from the check)
            _assembler.Bind(cold);
            _assembler.MovImm(X8, 1);
            _assembler.LdStReg(LsKind.Strb, X8, PinDirty, X16, xIndex: true);
            _assembler.B(back);
        }
        if (_stub)
        {
            EmitSharedTails();
        }

        return true;
    }

    // ------------------------------------------------------------------ shared exit tails (A64Opt.ExitStubs)

    /// <summary><see cref="A64Opt.ExitStubs"/>: the block's exit paths end in tails shared by all its exits, and the
    /// cold paths of patched exits (window full, link break, unlinked) are laid out after the block's code.</summary>
    private bool _stub, _abortUsed, _slotPathUsed, _tailZeroUsed;
    private int _tail, _tailZero, _slotPath;
    private readonly List<(int Label, nint Slot, uint? Pc)> _coldExits = new();
    /// <summary>Unlinked paths of patched exits (the site's B to the next word leads here): the slot path.</summary>
    private readonly List<(int Label, nint Slot, uint? Pc)> _coldUnlinked = new();
    /// <summary>Dirty-page marks of clean pages, laid out after the block (<see cref="MarkDirtyReg"/>).</summary>
    private readonly List<(int Cold, int Back)> _coldMarks = new();

    /// <summary>Stores a static exit's target to R15 (<see cref="StateLiveness.ColdPc"/>).</summary>
    private void StorePc(uint pc)
    {
        if (_storeLit && MovImmIsLong(pc))
        {
            _assembler.LdrLitW(X0, pc);
        }
        else
        {
            _assembler.MovImm(X0, pc);
        }

        StState(X0, s_offR + 60);
    }

    /// <summary>Returns to the dispatcher with LastExitSlot = x16 (the exit's link slot).</summary>
    private void ExitSlot()
    {
        if (_stub)
        {
            _assembler.B(_tail);
            return;
        }
        _assembler.StrX(X16, State, s_offLastExitSlot);
        JumpTo(_rt.Epilogue);
    }

    /// <summary>Returns to the dispatcher with no link slot (indirect exits, aborts, tier-up).</summary>
    private void ExitZero()
    {
        if (_stub)
        {
            _tailZeroUsed = true;
            _assembler.B(_tailZero);
            return;
        }
        _assembler.StrX(ZR, State, s_offLastExitSlot);
        JumpTo(_rt.Epilogue);
    }

    private void EmitSharedTails()
    {
        Arm64Assembler assembler = _assembler;
        foreach ((int label, nint slot, uint? pc) in _coldUnlinked)
        {
            assembler.Bind(label);
            if (pc is uint p)
            {
                StorePc(p);
            }

            assembler.LdrLit(X16, (ulong)slot);
            assembler.B(_slotPath);
        }
        foreach ((int label, nint slot, uint? pc) in _coldExits)
        {
            assembler.Bind(label);
            if (pc is uint p)
            {
                StorePc(p);
            }

            assembler.LdrLit(X16, (ulong)slot);
            assembler.B(_tail);
        }
        if (_slotPathUsed)
        {
            // unlinked patch site (or a target out of B range): the link slot, x16 = its address
            assembler.Bind(_slotPath);
            assembler.LdrX(X17, X16, 0);
            assembler.Cbz(X17, _tail, true);
            assembler.Br(X17);
        }
        if (_tailZeroUsed)
        {
            assembler.Bind(_tailZero);
            assembler.Mov(X16, ZR, x: true);
        }
        assembler.Bind(_tail);
        assembler.StrX(X16, State, s_offLastExitSlot);
        JumpTo(_rt.Epilogue);
    }

    /// <summary>Encodes the prepared code (position independent: <paramref name="address"/> is not needed).</summary>
    public byte[] Assemble(ulong address) => _assembler.Finish();

    /// <summary>The words of the prepared block (tests).</summary>
    public IReadOnlyList<uint> Words => _assembler.Words;

    private void JumpTo(ulong target)
    {
        _assembler.LdrLit(X16, target);
        _assembler.Br(X16);
    }

    // ------------------------------------------------------------------ operand helpers

    private bool IsConst(int t, out uint v) => _ir.TryConst(t, out v);
    private bool InReg(int t, out int r)
    {
        r = t >= 0 ? _loc[t] : -1;
        return r >= 0;
    }
    private int SlotOff(int t) => SpillBase + 8 * (-(_loc[t] + 2));

    /// <summary>Loads temp <paramref name="t"/> into W<paramref name="r"/>.</summary>
    private void LoadTo(int r, int t)
    {
        if (IsConst(t, out uint v))
        {
            _assembler.MovImm(r, v);
        }
        else if (InReg(t, out int s))
        {
            if (s != r)
            {
                _assembler.Mov(r, s);
            }
        }
        else
        {
            _assembler.Ldr(r, SP, SlotOff(t));
        }
    }

    /// <summary>A register holding temp t: its own, or <paramref name="scratch"/> loaded with it.</summary>
    private int Use(int t, int scratch)
    {
        if (!IsConst(t, out _) && InReg(t, out int r))
        {
            return r;
        }

        LoadTo(scratch, t);
        return scratch;
    }

    /// <summary>Like <see cref="Use"/>, but the constant 0 is WZR (only for operand fields where 31 means the zero register).</summary>
    private int UseZ(int t, int scratch) => t < 0 || (IsConst(t, out uint v) && v == 0) ? ZR : Use(t, scratch);

    /// <summary>Like <see cref="UseZ"/> for a value that is only stored: a constant needing MOVZ + MOVK is one literal-pool
    /// load instead (<see cref="A64Opt.StoreLiteral"/>; the load's latency is off the critical path of a store).</summary>
    private int UseStored(int t, int scratch)
    {
        if (_storeLit && IsConst(t, out uint v) && MovImmIsLong(v))
        {
            _assembler.LdrLitW(scratch, v);
            return scratch;
        }
        return UseZ(t, scratch);
    }

    private int Def(int t, int scratch) => t >= 0 && InReg(t, out int r) ? r : scratch;

    private void Commit(int t, int r)
    {
        if (t < 0)
        {
            return;
        }

        if (InReg(t, out int own))
        {
            if (own != r)
            {
                _assembler.Mov(own, r);
            }

            return;
        }
        _assembler.Str(r, SP, SlotOff(t));
    }

    private void LdState(int r, int off) => _assembler.Ldr(r, State, off);
    private void StState(int r, int off) => _assembler.Str(r, State, off);

    /// <summary>State offset of a 32-bit state store (SetReg/SetFlag/StoreState) or load (GetReg/GetFlag/LoadState), else -1.</summary>
    private static int StateOffset(in IrInst x, bool store) => (store, x.Op) switch
    {
        (true, IrOp.SetReg) or (false, IrOp.GetReg) => s_offR + 4 * (int)x.Imm,
        (true, IrOp.SetFlag) or (false, IrOp.GetFlag) => s_offN + 4 * (int)x.Imm,
        (true, IrOp.StoreState) or (false, IrOp.LoadState) => (int)x.Imm,
        _ => -1,
    };

    /// <summary>
    /// <see cref="A64Opt.PairState"/>: two consecutive 32-bit state stores (or loads) of adjacent fields as one STP (LDP).
    /// The register flushes the frontend emits before exits and conditional regions are runs of these (R0..R15, then
    /// N Z C V, which follow R15 in the state). Only ops that keep the host flags are paired, so the flag tracking of
    /// <see cref="Lower"/> needs no update.
    /// </summary>
    private bool TryPair(in IrInst x, in IrInst y)
    {
        Arm64Assembler assembler = _assembler;
        if (_vcAnyDirty && (x.Op is IrOp.LoadState or IrOp.StoreState || y.Op is IrOp.LoadState or IrOp.StoreState))
        {
            VcFlushAll();
        }

        int o1 = StateOffset(x, true), o2 = StateOffset(y, true);
        if (o1 >= 0 && o2 == o1 + 4 && FitsPairW(o1))
        {
            int r1 = UseStored(x.A, X0), r2 = UseStored(y.A, X1);
            assembler.StpW(r1, r2, State, o1);
            StateWritten(o1, 8);
            return true;
        }
        o1 = StateOffset(x, false);
        o2 = StateOffset(y, false);
        if (o1 >= 0 && o2 == o1 + 4 && FitsPairW(o1) && x.D >= 0 && y.D >= 0)
        {
            int d1 = Def(x.D, X0), d2 = Def(y.D, X1);
            if (d1 == d2)
            {
                return false;
            }

            assembler.LdpW(d1, d2, State, o1);
            Commit(x.D, d1);
            Commit(y.D, d2);
            return true;
        }
        return false;
    }

    /// <summary>A plain store wrote state bytes [off, off + len): cached copies of SIMD registers there are stale.</summary>
    private void StateWritten(int off, int len) => VcInvalidate(off - s_offD, len);

    // ------------------------------------------------------------------ lowering

    /// <summary>
    /// Adds the block's cycles and counts to the state; leaves CycleAcc in x16. With pinned counters: x26 += 1,
    /// x27 += instructions, ADDS x28 += cycles, so the flags say GE exactly when CycleAcc reached CheckWindow.
    /// </summary>
    /// <summary>Cycles the exit being lowered charges: its own (Imm4 - 1, superblocks: the blocks up to it) or the block's.</summary>
    private uint _exitCost;
    private long ExitCycles => _exitCost != 0 ? _exitCost - 1 : _ir.Cycles;

    private void EmitAccounting()
    {
        Arm64Assembler assembler = _assembler;
        if (_pin)
        {
            if (_count)
            {
                assembler.AddImm(PinBlocks, PinBlocks, 1, true);
                AddConst64(PinInstr, (ulong)_ir.GuestInstructions);
            }
            ulong c = (ulong)Math.Min(ExitCycles, int.MaxValue);
            if (c < 4096 || (IsAddImm((uint)c) && c < 1u << 24))
            {
                assembler.AddsImm(PinBudget, PinBudget, (uint)c, true);
            }
            else
            {
                assembler.MovImm64(X8, c);
                assembler.Adds(PinBudget, PinBudget, X8, x: true);
            }
            return;
        }
        assembler.LdrX(X16, State, s_offCycleAcc);
        AddConst64(X16, (ulong)Math.Min(ExitCycles, int.MaxValue));
        assembler.StrX(X16, State, s_offCycleAcc);
        assembler.LdrX(X17, State, s_offNBlocks);
        assembler.AddImm(X17, X17, 1, true);
        assembler.StrX(X17, State, s_offNBlocks);
        assembler.LdrX(X17, State, s_offNInstr);
        AddConst64(X17, (ulong)_ir.GuestInstructions);
        assembler.StrX(X17, State, s_offNInstr);
    }

    private void AddConst64(int r, ulong v)
    {
        if (v == 0)
        {
            return;
        }

        if (v < 4096 || (IsAddImm((uint)v) && v < 1u << 24))
        {
            _assembler.AddImm(r, r, (uint)v, true);
        }
        else
        {
            _assembler.MovImm64(X8, v);
            _assembler.Add(r, r, X8, x: true);
        }
    }

    // ------------------------------------------------------------------ host flags (compare + branch fusion)

    /// <summary>Flag temps whose values the host NZCV currently hold (-1: none); valid while only ops that keep the
    /// host flags were lowered since the AddC/SubC/TestNZ that set them.</summary>
    private int _hfN = -1, _hfZ = -1, _hfC = -1, _hfV = -1;

    /// <summary>IR ops whose lowering never writes NZCV (and is not a merge point).</summary>
    private static bool KeepsHostFlags(IrOp op) => op is IrOp.Nop or IrOp.GetReg or IrOp.GetFlag or IrOp.LoadState or IrOp.SetReg
        or IrOp.SetFlag or IrOp.StoreState or IrOp.Add or IrOp.Sub or IrOp.And or IrOp.Or or IrOp.Xor or IrOp.AndNot
        or IrOp.OrNot or IrOp.Mul or IrOp.Not or IrOp.Neg or IrOp.Shl or IrOp.Shr or IrOp.Sar or IrOp.Ror or IrOp.Clz
        or IrOp.Bswap or IrOp.Rev16 or IrOp.RevSh or IrOp.Rbit or IrOp.SExt8 or IrOp.SExt16 or IrOp.ZExt8 or IrOp.ZExt16
        or IrOp.Bit or IrOp.UDiv or IrOp.SDiv or IrOp.UMulL or IrOp.SMulL or IrOp.Load8 or IrOp.Load8S or IrOp.Load16
        or IrOp.Load16S or IrOp.Load32 or IrOp.Store8 or IrOp.Store16 or IrOp.Store32 or IrOp.LoadD or IrOp.StoreD
        or IrOp.Fence or IrOp.CondBranch or IrOp.BranchIf;

    private void ForgetHostFlags()
    {
        _hfN = _hfZ = _hfC = _hfV = -1;
    }

    /// <summary>Records that the host NZCV now equal these flag temps (call right after the flag-setting instruction).</summary>
    private void HostFlagsAre(int n, int z, int c, int v)
    {
        if (_fuse)
        {
            _hfN = n;
            _hfZ = z;
            _hfC = c;
            _hfV = v;
        }
    }

    /// <summary>Whether CondBranch <paramref name="x"/> can test the host flags: every flag its condition reads is the
    /// temp the host flag currently holds.</summary>
    private bool HostFlagsMatch(in IrInst x)
    {
        static bool Same(int used, int host) => used < 0 || used == host;
        bool any = x.A >= 0 || x.B >= 0 || x.C >= 0 || x.E >= 0;
        return any && x.Imm < 14 && Same(x.A, _hfN) && Same(x.B, _hfZ) && Same(x.C, _hfC) && Same(x.E, _hfV);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void Lower(IrInst x, Func<int, nint> slotFor, ref int exitIndex)
    {
        Arm64Assembler assembler = _assembler;
        if (_hfN >= 0 || _hfZ >= 0 || _hfC >= 0 || _hfV >= 0)
        {
            if (!KeepsHostFlags(x.Op))
            {
                ForgetHostFlags();
            }
        }
        // merge points and callouts end the SIMD register cache (FpOp reloads it after its handler path instead)
        if (_vcAnyDirty && !KeepsDirtyVec(x))
        {
            VcFlushAll(); // before branches, labels, calls, exits, register-file accesses
        }

        if (_vcAny && x.Op is IrOp.Label or IrOp.Callout)
        {
            VcForget();
        }

        if (_fpcrHost && NeedsGuestFpcr(x.Op))
        {
            EnsureGuestFpcr(); // MSR FPCR leaves NZCV alone (branch fusion)
        }

        switch (x.Op)
        {
            case IrOp.Nop:
                return;

            // ---------------- state
            case IrOp.GetReg:
            {
                int d = Def(x.D, X0);
                LdState(d, s_offR + 4 * (int)x.Imm);
                Commit(x.D, d);
                return;
            }
            case IrOp.GetFlag:
            {
                int d = Def(x.D, X0);
                LdState(d, s_offN + 4 * (int)x.Imm);
                Commit(x.D, d);
                return;
            }
            case IrOp.LoadState:
            {
                int d = Def(x.D, X0);
                LdState(d, (int)x.Imm);
                Commit(x.D, d);
                return;
            }
            case IrOp.SetReg:
                StState(UseStored(x.A, X0), s_offR + 4 * (int)x.Imm);
                return;
            case IrOp.SetFlag:
                StState(UseStored(x.A, X0), s_offN + 4 * (int)x.Imm);
                return;
            case IrOp.StoreState:
                StState(UseStored(x.A, X0), (int)x.Imm);
                StateWritten((int)x.Imm, 4);
                return;

            // ---------------- ALU
            case IrOp.Add:
            case IrOp.Sub:
            case IrOp.And:
            case IrOp.Or:
            case IrOp.Xor:
            case IrOp.AndNot:
            case IrOp.OrNot:
            case IrOp.Mul:
                Bin(x);
                return;
            case IrOp.Not:
            {
                int d = Def(x.D, X0);
                assembler.Mvn(d, UseZ(x.A, X1));
                Commit(x.D, d);
                return;
            }
            case IrOp.Neg:
            {
                int d = Def(x.D, X0);
                assembler.Neg(d, UseZ(x.A, X1));
                Commit(x.D, d);
                return;
            }
            case IrOp.Shl:
            case IrOp.Shr:
            case IrOp.Sar:
            case IrOp.Ror:
                ShiftOp(x);
                return;
            case IrOp.Clz:
            {
                int d = Def(x.D, X0);
                assembler.Clz(d, UseZ(x.A, X1));
                Commit(x.D, d);
                return;
            }
            case IrOp.Bswap:
            {
                int d = Def(x.D, X0);
                assembler.Rev(d, UseZ(x.A, X1));
                Commit(x.D, d);
                return;
            }
            case IrOp.Rev16:
            {
                int d = Def(x.D, X0);
                assembler.Rev16(d, UseZ(x.A, X1));
                Commit(x.D, d);
                return;
            }
            case IrOp.RevSh:
            {
                int d = Def(x.D, X0);
                assembler.Rev16(d, UseZ(x.A, X1));
                assembler.Sxth(d, d);
                Commit(x.D, d);
                return;
            }
            case IrOp.Rbit:
            {
                int d = Def(x.D, X0);
                assembler.Rbit(d, UseZ(x.A, X1));
                Commit(x.D, d);
                return;
            }
            case IrOp.SExt8:
            case IrOp.SExt16:
            case IrOp.ZExt8:
            case IrOp.ZExt16:
            {
                int d = Def(x.D, X0);
                int s = UseZ(x.A, X1);
                switch (x.Op)
                {
                    case IrOp.SExt8:
                        assembler.Sxtb(d, s);
                        break;
                    case IrOp.SExt16:
                        assembler.Sxth(d, s);
                        break;
                    case IrOp.ZExt8:
                        assembler.Uxtb(d, s);
                        break;
                    default:
                        assembler.Uxth(d, s);
                        break;
                }
                Commit(x.D, d);
                return;
            }
            case IrOp.Bit:
            {
                int d = Def(x.D, X0);
                assembler.Ubfx(d, UseZ(x.A, X1), (int)x.Imm, 1);
                Commit(x.D, d);
                return;
            }
            case IrOp.CmpEq:
            {
                CmpWith(Use(x.A, X1), x.B);
                int d = Def(x.D, X0);
                assembler.Cset(d, Cond.EQ);
                Commit(x.D, d);
                return;
            }
            case IrOp.Select:
            {
                int c = Use(x.C, X2);
                int va = UseZ(x.A, X0), vb = UseZ(x.B, X1);
                assembler.CmpImm(c, 0);
                int d = Def(x.D, X3);
                assembler.Csel(d, va, vb, Cond.NE);
                Commit(x.D, d);
                return;
            }
            case IrOp.UDiv:
            case IrOp.SDiv:
            {
                // ARM64 division is ARM's: x / 0 = 0, INT_MIN / -1 = INT_MIN
                int n = UseZ(x.A, X0), m = UseZ(x.B, X1);
                int d = Def(x.D, X2);
                if (x.Op == IrOp.UDiv)
                {
                    assembler.Udiv(d, n, m);
                }
                else
                {
                    assembler.Sdiv(d, n, m);
                }

                Commit(x.D, d);
                return;
            }

            // ---------------- flags
            case IrOp.AddC:
            case IrOp.SubC:
                ArithFlags(x);
                return;
            case IrOp.TestNZ:
            {
                int s = UseZ(x.A, X0);
                assembler.Tst(s, s);
                HostFlagsAre(x.Fn, x.Fz, -1, -1);
                SetFlagOut(x.Fn, Cond.MI);
                SetFlagOut(x.Fz, Cond.EQ);
                return;
            }
            case IrOp.ShiftReg:
                ShiftReg(x);
                return;

            // ---------------- 64-bit
            case IrOp.UMulL:
            case IrOp.SMulL:
            {
                int n = UseZ(x.A, X0), m = UseZ(x.B, X1);
                if (x.Op == IrOp.UMulL)
                {
                    assembler.Umull(X2, n, m);
                }
                else
                {
                    assembler.Smull(X2, n, m);
                }

                if (x.D >= 0)
                {
                    int d = Def(x.D, X3);
                    assembler.Mov(d, X2);
                    Commit(x.D, d);
                }
                if (x.D2 >= 0)
                {
                    int d = Def(x.D2, X3);
                    assembler.LsrImm(d, X2, 32, true);
                    Commit(x.D2, d);
                }
                return;
            }
            case IrOp.Add64:
            {
                int lo1 = UseZ(x.A, X0), hi1 = UseZ(x.B, X1), lo2 = UseZ(x.C, X2), hi2 = UseZ(x.E, X3);
                assembler.Adds(X4, lo1, lo2);
                assembler.Adc(X5, hi1, hi2);
                if (x.D >= 0)
                {
                    Commit(x.D, X4);
                }

                if (x.D2 >= 0)
                {
                    Commit(x.D2, X5);
                }

                return;
            }

            // ---------------- memory
            case IrOp.Load8:
            case IrOp.Load8S:
            case IrOp.Load16:
            case IrOp.Load16S:
            case IrOp.Load32:
            {
                int ad = UseZ(x.A, X16);
                int d = Def(x.D, X0);
                LsKind k = x.Op switch
                {
                    IrOp.Load8 => LsKind.Ldrb,
                    IrOp.Load8S => LsKind.Ldrsb,
                    IrOp.Load16 => LsKind.Ldrh,
                    IrOp.Load16S => LsKind.Ldrsh,
                    _ => LsKind.LdrW,
                };
                assembler.LdStReg(k, d, MemBase, ad);
                Commit(x.D, d);
                return;
            }
            case IrOp.Store8:
            case IrOp.Store16:
            case IrOp.Store32:
            {
                if (LogStores)
                {
                    EmitStoreLog(x);
                }

                int ad = UseZ(x.A, X16);
                int v = UseStored(x.B, X17);
                LsKind k = x.Op == IrOp.Store8 ? LsKind.Strb : x.Op == IrOp.Store16 ? LsKind.Strh : LsKind.StrW;
                assembler.LdStReg(k, v, MemBase, ad);
                MarkPlanned(x.A);
                return;
            }
            case IrOp.Fence:
                assembler.DmbIsh();
                return;
            case IrOp.FpOp:
                FpOp(x);
                return;
            case IrOp.VecOp:
                VecOp(x);
                return;
            case IrOp.LoadD:
            {
                int ad = UseZ(x.A, X16);
                assembler.LdStReg(LsKind.LdrX, X0, MemBase, ad);
                assembler.StrX(X0, State, (int)x.Imm);
                StateWritten((int)x.Imm, 8);
                return;
            }
            case IrOp.StoreD:
            {
                int ad = UseZ(x.A, X16);
                assembler.LdrX(X0, State, (int)x.Imm);
                assembler.LdStReg(LsKind.StrX, X0, MemBase, ad);
                MarkPlanned(x.A);
                return;
            }
            case IrOp.LdrEx:
                LoadExclusive(x);
                return;
            case IrOp.StrEx:
                StoreExclusive(x);
                return;

            // ---------------- control
            case IrOp.Label:
                assembler.Bind(_labels[x.Imm]);
                return;
            case IrOp.BranchIf:
            {
                int s = UseZ(x.A, X0);
                if (x.Imm != 0)
                {
                    assembler.Cbnz(s, _labels[x.Imm2]);
                }
                else
                {
                    assembler.Cbz(s, _labels[x.Imm2]);
                }

                return;
            }
            case IrOp.CondBranch:
                CondBranch(x);
                return;
            case IrOp.Callout:
                CallHandler(x.Imm, x.Imm2, x.Imm3);
                return;
            case IrOp.ExitStatic:
            {
                _exitCost = x.Imm4;
                nint slot = slotFor(exitIndex++);
                int ret = assembler.NewLabel();
                // LinkBreak only on back edges: every cycle of linked blocks has an exit to an address at or below its
                // block's start (forward chains end by themselves, at an unlinked exit or the pacing window)
                bool linkBreak = !_backEdge || x.Imm <= _ir.StartPc;
                if (_pin && _rt.PatchLinks)
                {
                    // linked: the patch site is a direct B to the target (no slot load, no indirect branch); unlinked it
                    // falls into the slot path, which also serves a link whose target is out of B range
                    int ret2 = assembler.NewLabel();
                    EmitAccounting();
                    assembler.BCond(Cond.GE, ret);                     // CycleAcc >= CheckWindow
                    if (linkBreak)
                    {
                        LdState(X8, s_offLinkBreak);
                        assembler.Cbnz(X8, ret);
                    }
                    if (_checkExit)
                    {
                        LdState(X8, s_offExit);
                        assembler.Cbnz(X8, ret);
                    }
                    while (PatchSites.Count < exitIndex - 1)
                    {
                        PatchSites.Add(-1);
                    }

                    PatchSites.Add(assembler.Count * 4);
                    assembler.Emit(UnlinkedSiteWord);
                    // StateLiveness.ColdPc: the IR left R15 to this exit, which stores it on the paths to the dispatcher only
                    bool coldPc = _stub && x.Imm3 == StateLiveness.ColdPc;
                    if (_stub)
                    {
                        // linked, only the accounting, the checks and the patched B are on the hot path (plus one B
                        // word behind the site); the unlinked and return paths go after the block's code
                        int unlinked = assembler.NewLabel();
                        assembler.B(unlinked);
                        _slotPathUsed = true;
                        _coldUnlinked.Add((unlinked, slot, coldPc ? x.Imm : (uint?)null));
                        _coldExits.Add((ret, slot, coldPc ? x.Imm : (uint?)null));
                        return;
                    }
                    assembler.LdrLit(X16, (ulong)slot);
                    assembler.LdrX(X17, X16, 0);
                    assembler.Cbz(X17, ret2, true);
                    assembler.Br(X17);
                    assembler.Bind(ret);
                    assembler.LdrLit(X16, (ulong)slot);
                    assembler.Bind(ret2);
                    assembler.StrX(X16, State, s_offLastExitSlot);
                    JumpTo(_rt.Epilogue);
                    return;
                }
                if (x.Imm3 == StateLiveness.ColdPc)
                {
                    StorePc(x.Imm); // no patched exit here: R15 on every path
                }

                if (_pin)
                {
                    // the slot address first (it is also the return path's LastExitSlot), the link target load early
                    assembler.LdrLit(X16, (ulong)slot);
                    EmitAccounting();
                    assembler.LdrX(X17, X16, 0);
                    if (_stub)
                    {
                        ret = _tail;                   // x16 is the slot: every return path is the shared tail
                    }

                    assembler.BCond(Cond.GE, ret);                     // CycleAcc >= CheckWindow
                    if (linkBreak)
                    {
                        LdState(X8, s_offLinkBreak);
                        assembler.Cbnz(X8, ret);
                    }
                    if (_checkExit)
                    {
                        LdState(X8, s_offExit);
                        assembler.Cbnz(X8, ret);
                    }
                    assembler.Cbz(X17, ret, true);
                    assembler.Br(X17);
                    if (_stub)
                    {
                        return;
                    }

                    assembler.Bind(ret);
                    assembler.StrX(X16, State, s_offLastExitSlot);
                    JumpTo(_rt.Epilogue);
                    return;
                }
                EmitAccounting();
                WindowChecks(ret, linkBreak);
                assembler.LdrLit(X16, (ulong)slot);
                assembler.LdrX(X17, X16, 0);
                assembler.Cbz(X17, ret, true);
                assembler.Br(X17);
                assembler.Bind(ret);
                assembler.LdrLit(X16, (ulong)slot);
                ExitSlot();
                return;
            }
            case IrOp.ExitDynamic:
            {
                _exitCost = x.Imm4;
                int ret = assembler.NewLabel();
                EmitAccounting();
                if (x.Imm == 0 && _rt.DispatchTable != 0 && !NativeRuntime.NoLink)
                {
                    // indirect exit through the dispatch table: key, code, key (see NativeRuntime.Publish). ARM64
                    // reorders loads, so the first two are acquire loads: the code pointer is read after the key and
                    // the second key read after the code pointer.
                    WindowChecks(ret);
                    int pc = X0, th = X1;
                    if (x.A >= 0)
                    {
                        pc = Use(x.A, X0);
                    }
                    else
                    {
                        LdState(X0, s_offR + 60);
                    }

                    if (x.B >= 0)
                    {
                        th = UseZ(x.B, X1);
                    }
                    else
                    {
                        LdState(X1, s_offThumb);
                    }

                    assembler.Orr(X1, pc, th);                                   // key = pc | thumb
                    if (pc != X0)
                    {
                        assembler.Mov(X0, pc);
                    }

                    bool packed = _rt.CodeBase != 0 && (Opts & A64Opt.PackedDispatch) != 0;
                    if (packed && _rt.TwoWayDispatch)
                    {
                        assembler.Ubfx(X2, X0, 2, NativeRuntime.DispatchBits - 1);  // set index: two 16-byte entries
                        assembler.LdrX(X3, State, s_offDispatchTable);
                        assembler.Add(X3, X3, X2, Shift.Lsl, 5, x: true);
                    }
                    else
                    {
                        assembler.Ubfx(X2, X0, 1, NativeRuntime.DispatchBits);
                        assembler.LdrX(X3, State, s_offDispatchTable);                 // the thread's process's table
                        assembler.Add(X3, X3, X2, Shift.Lsl, 4, x: true);            // 16-byte entries
                    }
                    if (packed)
                    {
                        // {key, arena offset} in one single-copy-atomic load: a republished entry is read whole
                        int hit = assembler.NewLabel();
                        assembler.LdrX(X4, X3, 0);
                        assembler.Cmp(X4, X1);                                   // 32-bit compare: the key half
                        if (_rt.TwoWayDispatch)
                        {
                            assembler.BCond(Cond.EQ, hit);
                            assembler.LdrX(X4, X3, 16);                          // the set's second way
                            assembler.Cmp(X4, X1);
                        }
                        assembler.BCond(Cond.NE, ret);
                        assembler.Bind(hit);
                        assembler.LdrLit(X5, _rt.CodeBase);
                        assembler.Add(X5, X5, X4, Shift.Lsr, 32, x: true);
                        assembler.Br(X5);
                        assembler.Bind(ret);
                        ExitZero();
                        return;
                    }
                    assembler.Ldar(X4, X3);
                    assembler.Cmp(X4, X1);
                    assembler.BCond(Cond.NE, ret);
                    assembler.AddImm(X5, X3, 8, true);
                    assembler.Ldar(X6, X5, true);
                    assembler.Ldr(X4, X3, 0);
                    assembler.Cmp(X4, X1);
                    assembler.BCond(Cond.NE, ret);
                    assembler.Br(X6);
                }
                assembler.Bind(ret);
                ExitZero();
                return;
            }
            default:
                throw new NotSupportedException($"IR op {x.Op}");
        }
    }

    /// <summary>
    /// Branches to <paramref name="ret"/> when the block must return to the dispatcher (expects CycleAcc in x16, or
    /// with pinned counters the flags of <see cref="EmitAccounting"/>).
    /// </summary>
    private void WindowChecks(int ret, bool linkBreak = true)
    {
        Arm64Assembler assembler = _assembler;
        if (!_pin)
        {
            assembler.LdrX(X17, State, s_offCheckWindow);
            assembler.Cmp(X16, X17, true);
        }
        assembler.BCond(Cond.GE, ret);
        if (linkBreak)
        {
            LdState(X17, s_offLinkBreak);
            assembler.Cbnz(X17, ret);
        }
        if (_checkExit)
        {
            LdState(X17, s_offExit);
            assembler.Cbnz(X17, ret);
        }
    }

    /// <summary>FPCR holds the host mode at this point of the straight-line code (<see cref="A64Opt.LazyFpcr"/>: after a
    /// callout, until code that needs the guest mode).</summary>
    private bool _fpcrHost;

    /// <summary>Switches FPCR back to the guest mode if a callout left the host mode in it.</summary>
    private void EnsureGuestFpcr()
    {
        if (!_fpcrHost)
        {
            return;
        }

        _assembler.LdrX(X16, SP, FrameGuestFpcr);
        _assembler.Msr(SysReg.Fpcr, X16);
        _fpcrHost = false;
    }

    /// <summary>IR ops that need the guest FPCR: FP arithmetic, and every control transfer (labels merge paths, exits
    /// chain into blocks that start in the guest mode).</summary>
    private static bool NeedsGuestFpcr(IrOp op) => op is IrOp.FpOp or IrOp.VecOp or IrOp.Label or IrOp.CondBranch or IrOp.BranchIf
        or IrOp.ExitStatic or IrOp.ExitDynamic;

    /// <summary>
    /// Calls the interpreter handler of <paramref name="op"/> with FPCR in the host mode. The guest mode is restored
    /// right after the call, or with <see cref="A64Opt.LazyFpcr"/> (unless <paramref name="restore"/>) only before the
    /// next op that needs it, so a run of callouts separated by integer code switches FPCR twice instead of twice per call.
    /// </summary>
    private void CallHandler(uint op, uint ins, uint pc, bool restore = false)
    {
        Arm64Assembler assembler = _assembler;
        if (!_fpcrHost)
        {
            assembler.LdrX(X16, SP, FrameHostFpcr);
            assembler.Msr(SysReg.Fpcr, X16);
        }
        assembler.MovSp(X0, State); // mov x0, x19 (ADD #0 is fine for non-SP registers too)
        assembler.MovImm(X1, op);
        assembler.MovImm(X2, ins);
        assembler.MovImm(X3, pc);
        assembler.LdrLit(X16, _rt.Callout);
        assembler.Blr(X16);
        if (_lazyFpcr && !restore)
        {
            _fpcrHost = true;
        }
        else
        {
            assembler.LdrX(X16, SP, FrameGuestFpcr);
            assembler.Msr(SysReg.Fpcr, X16);
            _fpcrHost = false;
        }
        _abortUsed = true;
        assembler.Cbnz(X0, _abort); // the abort path ends in the epilogue, which sets the host mode anyway
    }

    private void SetFlagOut(int t, Cond c)
    {
        if (t < 0)
        {
            return;
        }

        int r = Def(t, X3);
        _assembler.Cset(r, c);
        Commit(t, r);
    }

    /// <summary>Sets the host flags for Wn - t (t as an immediate when encodable).</summary>
    private void CmpWith(int n, int t)
    {
        if (IsConst(t, out uint v))
        {
            if (IsAddImm(v))
            {
                _assembler.CmpImm(n, v);
                return;
            }
            if (IsAddImm(unchecked(0u - v)))
            {
                _assembler.CmnImm(n, unchecked(0u - v));
                return;
            }
        }
        _assembler.Cmp(n, UseZ(t, X17));
    }

    private void Bin(IrInst x)
    {
        Arm64Assembler assembler = _assembler;
        int d = Def(x.D, X0);
        int A = x.A, B = x.B;
        bool commutative = x.Op is IrOp.Add or IrOp.And or IrOp.Or or IrOp.Xor or IrOp.Mul;
        if (commutative && IsConst(A, out _) && !IsConst(B, out _))
        {
            (A, B) = (B, A);
        }

        int n = UseZ(A, X16);
        if (IsConst(B, out uint v))
        {
            switch (x.Op)
            {
                case IrOp.Add when n != ZR:
                    if (IsAddImm(v))
                    {
                        assembler.AddImm(d, n, v);
                        Commit(x.D, d);
                        return;
                    }
                    if (IsAddImm(unchecked(0u - v)))
                    {
                        assembler.SubImm(d, n, unchecked(0u - v));
                        Commit(x.D, d);
                        return;
                    }
                    break;
                case IrOp.Sub when n != ZR:
                    if (IsAddImm(v))
                    {
                        assembler.SubImm(d, n, v);
                        Commit(x.D, d);
                        return;
                    }
                    if (IsAddImm(unchecked(0u - v)))
                    {
                        assembler.AddImm(d, n, unchecked(0u - v));
                        Commit(x.D, d);
                        return;
                    }
                    break;
                case IrOp.And when TryLogicalImm(v, false, out _):
                    assembler.AndImm(d, n, v);
                    Commit(x.D, d);
                    return;
                case IrOp.Or when TryLogicalImm(v, false, out _):
                    assembler.OrrImm(d, n, v);
                    Commit(x.D, d);
                    return;
                case IrOp.Xor when TryLogicalImm(v, false, out _):
                    assembler.EorImm(d, n, v);
                    Commit(x.D, d);
                    return;
                case IrOp.AndNot when TryLogicalImm(~v, false, out _):
                    assembler.AndImm(d, n, ~v);
                    Commit(x.D, d);
                    return;
                case IrOp.OrNot when TryLogicalImm(~v, false, out _):
                    assembler.OrrImm(d, n, ~v);
                    Commit(x.D, d);
                    return;
            }
        }
        int m = UseZ(B, X17);
        switch (x.Op)
        {
            case IrOp.Add:
                assembler.Add(d, n, m);
                break;
            case IrOp.Sub:
                assembler.Sub(d, n, m);
                break;
            case IrOp.And:
                assembler.And(d, n, m);
                break;
            case IrOp.Or:
                assembler.Orr(d, n, m);
                break;
            case IrOp.Xor:
                assembler.Eor(d, n, m);
                break;
            case IrOp.AndNot:
                assembler.Bic(d, n, m);
                break;
            case IrOp.OrNot:
                assembler.Orn(d, n, m);
                break;
            default:
                assembler.Mul(d, n, m);
                break;
        }
        Commit(x.D, d);
    }

    private void ShiftOp(IrInst x)
    {
        Arm64Assembler assembler = _assembler;
        int d = Def(x.D, X0);
        int s = UseZ(x.A, X16);
        if (IsConst(x.B, out uint k))
        {
            int n = (int)(k & 31);
            if (n == 0)
            {
                assembler.Mov(d, s);
            }
            else
            {
                switch (x.Op)
                {
                    case IrOp.Shl:
                        assembler.LslImm(d, s, n);
                        break;
                    case IrOp.Shr:
                        assembler.LsrImm(d, s, n);
                        break;
                    case IrOp.Sar:
                        assembler.AsrImm(d, s, n);
                        break;
                    default:
                        assembler.RorImm(d, s, n);
                        break;
                }
            }
        }
        else
        {
            int m = Use(x.B, X17); // W-register variable shifts use the amount modulo 32, like the IR
            switch (x.Op)
            {
                case IrOp.Shl:
                    assembler.Lslv(d, s, m);
                    break;
                case IrOp.Shr:
                    assembler.Lsrv(d, s, m);
                    break;
                case IrOp.Sar:
                    assembler.Asrv(d, s, m);
                    break;
                default:
                    assembler.Rorv(d, s, m);
                    break;
            }
        }
        Commit(x.D, d);
    }

    /// <summary>ARM AddWithCarry with NZCV outputs (SubC: x - y with ARM's inverted borrow, which is ARM64's too).</summary>
    private void ArithFlags(IrInst x)
    {
        Arm64Assembler assembler = _assembler;
        bool sub = x.Op == IrOp.SubC;
        int n = UseZ(x.A, X16);
        int d = x.D >= 0 ? Def(x.D, X0) : ZR;
        // carry-in: -1 = none (0 for add, 1 for sub)
        int cinConst = -1;
        if (x.C < 0)
        {
            cinConst = sub ? 1 : 0;
        }
        else if (IsConst(x.C, out uint cv))
        {
            cinConst = (int)(cv & 1);
        }

        bool plain = cinConst == (sub ? 1 : 0);
        if (plain && IsConst(x.B, out uint v) && n != ZR)
        {
            if (IsAddImm(v))
            {
                if (sub)
                {
                    assembler.SubsImm(d, n, v);
                }
                else
                {
                    assembler.AddsImm(d, n, v);
                }

                goto flags;
            }
        }
        int m = UseZ(x.B, X17);
        if (plain)
        {
            if (sub)
            {
                assembler.Subs(d, n, m);
            }
            else
            {
                assembler.Adds(d, n, m);
            }
        }
        else
        {
            if (cinConst == 1)
            {
                assembler.Cmp(ZR, ZR);                    // C = 1
            }
            else if (cinConst == 0)
            {
                assembler.Adds(ZR, ZR, ZR);          // C = 0
            }
            else
            {
                assembler.CmpImm(Use(x.C, X1), 1);                      // C = (carry temp == 1)
            }

            if (sub)
            {
                assembler.Sbcs(d, n, m);
            }
            else
            {
                assembler.Adcs(d, n, m);
            }
        }
    flags:
        HostFlagsAre(x.Fn, x.Fz, x.Fc, x.Fv);
        SetFlagOut(x.Fn, Cond.MI);
        SetFlagOut(x.Fz, Cond.EQ);
        SetFlagOut(x.Fc, Cond.HS);
        SetFlagOut(x.Fv, Cond.VS);
        if (x.D >= 0)
        {
            Commit(x.D, d);
        }
    }

    /// <summary>ARM register-controlled shift (Shift_C with the amount in the low byte of Rs).</summary>
    private void ShiftReg(IrInst x)
    {
        Arm64Assembler assembler = _assembler;
        uint type = x.Imm;
        bool wantC = x.Fc >= 0;
        assembler.Uxtb(X0, UseZ(x.B, X0));          // w0 = amount 0..255
        int v = UseZ(x.A, X1);
        switch (type)
        {
            case 0: // LSL
                assembler.Lslv(X2, v, X0, true);         // 64-bit: amounts 32..63 shift the value out of the low word
                if (wantC)
                {
                    assembler.Ubfx(X3, X2, 32, 1, true);
                }

                assembler.CmpImm(X0, 32);
                assembler.Csel(X2, ZR, X2, Cond.HS);
                if (wantC)
                {
                    assembler.Csel(X3, ZR, X3, Cond.HI);
                }

                break;
            case 1: // LSR
                assembler.Lsrv(X2, v, X0, true);
                if (wantC)
                {
                    assembler.LslImm(X3, v, 1, true);
                    assembler.Lsrv(X3, X3, X0, true);
                    assembler.AndImm(X3, X3, 1);
                }
                assembler.CmpImm(X0, 32);
                assembler.Csel(X2, ZR, X2, Cond.HS);
                if (wantC)
                {
                    assembler.Csel(X3, ZR, X3, Cond.HI);
                }

                break;
            case 2: // ASR: amounts >= 32 act like 31 (sign fill); clamp to 63 for the 64-bit shift
                assembler.MovImm(X4, 63);
                assembler.CmpImm(X0, 63);
                assembler.Csel(X4, X0, X4, Cond.LO);
                assembler.Sxtw(X5, v);
                assembler.Asrv(X2, X5, X4, true);
                if (wantC)
                {
                    assembler.LslImm(X3, X5, 1, true);
                    assembler.Asrv(X3, X3, X4, true);
                    assembler.AndImm(X3, X3, 1);
                }
                assembler.Mov(X2, X2);                   // keep the low word only (zero-extended)
                break;
            default: // ROR
                assembler.Rorv(X2, v, X0);
                if (wantC)
                {
                    assembler.LsrImm(X3, X2, 31);
                }

                break;
        }
        if (wantC)
        {
            assembler.CmpImm(X0, 0);
            assembler.Csel(X3, UseZ(x.C, X4), X3, Cond.EQ); // amount 0: carry unchanged
            Commit(x.Fc, X3);
        }
        Commit(x.D, X2);
    }

    /// <summary>Jumps to the label when ARM condition Imm does not hold (A=N, B=Z, C=C, E=V).</summary>
    private void CondBranch(IrInst x)
    {
        Arm64Assembler assembler = _assembler;
        int skip = _labels[x.Imm2];
        uint cond = x.Imm;
        if (_fuse && HostFlagsMatch(x))
        {
            // the host NZCV are the guest flags these temps hold (0/1 values from the same ADDS/SUBS/TST): skip on the
            // inverse condition (ARM32 and ARM64 condition codes are the same)
            assembler.BCond(Invert((Cond)cond), skip);
            return;
        }
        void If(int t, bool set)
        {
            int r = UseZ(t, X0);
            if (set)
            {
                assembler.Cbnz(r, skip);
            }
            else
            {
                assembler.Cbz(r, skip);
            }
        }
        switch (cond)
        {
            case 0:
                If(x.B, false);
                break;  // EQ: skip when Z clear
            case 1:
                If(x.B, true);
                break;   // NE
            case 2:
                If(x.C, false);
                break;  // CS
            case 3:
                If(x.C, true);
                break;   // CC
            case 4:
                If(x.A, false);
                break;  // MI
            case 5:
                If(x.A, true);
                break;   // PL
            case 6:
                If(x.E, false);
                break;  // VS
            case 7:
                If(x.E, true);
                break;   // VC
            // compound conditions on the full flag values, exactly like Arm.CondPassed (a flag field can hold more than
            // 0/1 after an UNPREDICTABLE encoding wrote past R15)
            case 8:
            case 9: // HI: C != 0 && Z == 0 ; LS: the opposite
                assembler.CmpImm(Use(x.C, X1), 0);
                assembler.Cset(X0, Cond.NE);
                assembler.CmpImm(Use(x.B, X1), 0);
                assembler.Csel(X0, X0, ZR, Cond.EQ);
                if (cond == 8)
                {
                    assembler.Cbz(X0, skip);
                }
                else
                {
                    assembler.Cbnz(X0, skip);
                }

                break;
            case 10:
            case 11: // GE: N == V ; LT: N != V
                assembler.Cmp(UseZ(x.A, X1), UseZ(x.E, X2));
                assembler.BCond(cond == 10 ? Cond.NE : Cond.EQ, skip);
                break;
            case 12:
            case 13: // GT: Z == 0 && N == V ; LE: the opposite
                assembler.Cmp(UseZ(x.A, X1), UseZ(x.E, X2));
                assembler.Cset(X0, Cond.EQ);
                assembler.CmpImm(Use(x.B, X1), 0);
                assembler.Csel(X0, X0, ZR, Cond.EQ);
                if (cond == 12)
                {
                    assembler.Cbz(X0, skip);
                }
                else
                {
                    assembler.Cbnz(X0, skip);
                }

                break;
            default:
                break; // AL
        }
    }

    // ------------------------------------------------------------------ exclusive monitor

    private static readonly ulong s_gensBase = (ulong)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(Arm.s_exclusiveGens, 0);
    private static readonly int s_offExAddr = StateLayout.Of(nameof(CpuState.ExclusiveAddr));
    private static readonly int s_offExValue = StateLayout.Of(nameof(CpuState.ExclusiveValue));
    private static readonly int s_offExValid = StateLayout.Of(nameof(CpuState.ExclusiveValid));
    private static readonly int s_offExGen = StateLayout.Of(nameof(CpuState.ExclusiveGen));

    /// <summary>x3 = address of the generation counter of the granule of guest address w<paramref name="addr"/>.</summary>
    private void GenPointer(int addr)
    {
        _assembler.Ubfx(X2, addr, 3, 12); // (addr >> 3) & (ExclusiveStripes - 1)
        _assembler.LdrLit(X3, s_gensBase);
        _assembler.Add(X3, X3, X2, Shift.Lsl, 2, x: true);
    }

    /// <summary>LDREX / LDREXD: Arm.ReserveExclusive + read + SetExclusive.</summary>
    private void LoadExclusive(IrInst x)
    {
        Arm64Assembler assembler = _assembler;
        bool dual = x.Imm == 1;
        LoadTo(X1, x.A);
        GenPointer(X1);
        int spin = assembler.NewLabel(), ok = assembler.NewLabel();
        assembler.Bind(spin);
        assembler.Ldar(X4, X3);                       // acquire: the value is read after the generation snapshot
        assembler.Tbz(X4, 0, ok);
        assembler.Yield();                            // an exclusive store of another core is in flight
        assembler.B(spin);
        assembler.Bind(ok);
        StState(X4, s_offExGen);
        if (dual)
        {
            assembler.LdStReg(LsKind.LdrX, X6, MemBase, X1); // one 64-bit read, like Volatile.Read(ulong)
        }
        else
        {
            assembler.LdStReg(LsKind.LdrW, X6, MemBase, X1);      // zero-extended to 64 bits, like SetExclusive(uint)
        }

        StState(X1, s_offExAddr);
        assembler.StrX(X6, State, s_offExValue);
        assembler.MovImm(X7, 1);
        StState(X7, s_offExValid);
        if (dual && x.D2 >= 0)
        {
            int d2 = Def(x.D2, X5);
            assembler.LsrImm(d2, X6, 32, true);
            Commit(x.D2, d2);
        }
        if (x.D >= 0)
        {
            int d = Def(x.D, X0);
            assembler.Mov(d, X6);
            Commit(x.D, d);
        }
    }

    /// <summary>
    /// STREX / STREXD: Arm.StoreExclusive32/64 (lock the granule's generation snap -> snap+1, compare-and-swap the
    /// value against what LDREX read, release snap+2 on success or snap on failure). The compare-and-swaps are
    /// LDAXR/STLXR loops (ARMv8.0: no LSE needed); an unaligned address (which the guest cannot use with exclusives on
    /// hardware) gets a plain compare and store instead of an alignment fault.
    /// </summary>
    private void StoreExclusive(IrInst x)
    {
        Arm64Assembler assembler = _assembler;
        bool dual = x.Imm == 1;
        int fail = assembler.NewLabel(), done = assembler.NewLabel(), lockGen = assembler.NewLabel(), lockFail = assembler.NewLabel(), release = assembler.NewLabel();
        int unaligned = assembler.NewLabel(), casLoop = assembler.NewLabel(), valueFail = assembler.NewLabel(), valueDiffers = assembler.NewLabel();
        LoadTo(X6, x.B);
        if (dual)
        {
            int hi = UseZ(x.C, X7);
            assembler.Orr(X6, X6, hi, Shift.Lsl, 32, x: true);
        } // Rt | Rt2 << 32
        LoadTo(X1, x.A);
        LdState(X16, s_offExValid);
        assembler.Cbz(X16, fail);
        LdState(X16, s_offExAddr);
        assembler.Cmp(X16, X1);
        assembler.BCond(Cond.NE, fail);
        StState(ZR, s_offExValid);
        GenPointer(X1);
        LdState(X4, s_offExGen);
        assembler.AddImm(X5, X4, 1);
        assembler.Bind(lockGen);                      // CAS gen: snap -> snap + 1 (odd: store in flight)
        assembler.Ldaxr(X8, X3);
        assembler.Cmp(X8, X4);
        assembler.BCond(Cond.NE, lockFail);
        assembler.Stlxr(X17, X5, X3);
        assembler.Cbnz(X17, lockGen);
        // value CAS against what LDREX read
        assembler.AddExt(X2, MemBase, X1, Extend.Uxtw);
        assembler.LdrX(X7, State, s_offExValue);
        assembler.TstImm(X1, dual ? 7u : 3u);
        assembler.BCond(Cond.NE, unaligned);
        assembler.Bind(casLoop);
        assembler.Ldaxr(X8, X2, dual);
        assembler.Cmp(X8, X7, dual);
        assembler.BCond(Cond.NE, valueFail);
        assembler.Stlxr(X17, X6, X2, dual);
        assembler.Cbnz(X17, casLoop);
        assembler.MovImm(X0, 1);                      // ok
        assembler.B(release);
        assembler.Bind(unaligned);
        assembler.LdSt(dual ? LsKind.LdrX : LsKind.LdrW, X8, X2, 0);
        assembler.Cmp(X8, X7, dual);
        assembler.BCond(Cond.NE, valueDiffers);
        assembler.LdSt(dual ? LsKind.StrX : LsKind.StrW, X6, X2, 0);
        assembler.MovImm(X0, 1);
        assembler.B(release);
        assembler.Bind(valueFail);
        assembler.Clrex();
        assembler.Bind(valueDiffers);
        assembler.Mov(X0, ZR);
        assembler.Bind(release);
        // release: snap + 2 on success, snap on failure
        assembler.AddImm(X5, X4, 2);
        assembler.CmpImm(X0, 0);
        assembler.Csel(X4, X5, X4, Cond.NE);
        assembler.Stlr(X4, X3);
        if (Memory.DirtyPages.Enabled)
        {
            int clean = assembler.NewLabel();
            assembler.Cbz(X0, clean);
            MarkDirtyReg(X1);
            assembler.Bind(clean);
        }
        assembler.EorImm(X0, X0, 1);                  // status: 0 ok, 1 failed
        assembler.B(done);
        assembler.Bind(lockFail);
        assembler.Clrex();
        assembler.Bind(fail);
        assembler.MovImm(X0, 1);
        assembler.Bind(done);
        int d = Def(x.D, X2);
        if (d != X0)
        {
            assembler.Mov(d, X0);
        }

        Commit(x.D, d);
    }


    // ------------------------------------------------------------------ dirty pages / store log

    /// <summary>Marks the page of the guest address in temp t as written (after the store); clobbers x0, x16, x17.</summary>
    /// <summary>Index of the IR instruction being lowered.</summary>
    private int _cur;
    /// <summary>Per IR instruction (<see cref="PlanMarks"/>): -1 = a later store of its group marks the page, 0 = marks its
    /// own address, d &gt; 0 = marks its address minus d (the group's lowest address).</summary>
    private int[] _markPlan = new int[256];

    /// <summary>
    /// <see cref="A64Opt.GroupMarks"/>: stores whose addresses are one temp plus constants (the frontend's base + offset)
    /// spanning less than 4 KB, with no control flow, call, barrier or other store between them, are covered by one mark
    /// of the lowest address after the last store: the sweep treats a marked page's next page as written too
    /// (<see cref="Memory.DirtyPages"/>), and marking after the stores keeps the mark-after-data order. Anything that may
    /// let another thread or the HLE look at the data (calls, exits, barriers, exclusives) ends a group.
    /// </summary>
    private void PlanMarks(IrBlock ir, bool group)
    {
        Span<IrInst> insts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ir.Insts);
        if (_markPlan.Length < insts.Length)
        {
            _markPlan = new int[Math.Max(insts.Length, _markPlan.Length * 2)];
        }

        Array.Clear(_markPlan, 0, insts.Length);
        if (!group || !Memory.DirtyPages.Enabled)
        {
            return;
        }

        int n = ir.TempCount;
        if (_addrBase.Length < n)
        {
            _addrBase = new int[Math.Max(n, _addrBase.Length * 2)];
            _addrOff = new uint[_addrBase.Length];
        }
        Array.Fill(_addrBase, -1, 0, n);
        int gBase = -1, gLast = -1;
        uint gMin = 0, gEnd = 0, lastOff = 0;
        void Close()
        {
            if (gLast >= 0)
            {
                _markPlan[gLast] = (int)(lastOff - gMin);
            }

            gBase = gLast = -1;
        }
        for (int i = 0; i < insts.Length; i++)
        {
            ref readonly IrInst x = ref insts[i];
            switch (x.Op)
            {
                case IrOp.Add when x.D >= 0 && x.A >= 0 && !IsConst(x.A, out _) && IsConst(x.B, out uint k) && k < 0x8000_0000:
                {
                    // base + k, folded through chains of constant adds (both kept small: offsets of one access group)
                    int b = x.A;
                    uint off = k;
                    if (_addrBase[b] >= 0 && _addrOff[b] + k < 0x8000_0000)
                    {
                        off = _addrOff[b] + k;
                        b = _addrBase[b];
                    }
                    _addrBase[x.D] = b;
                    _addrOff[x.D] = off;
                    break;
                }
                case IrOp.Store8:
                case IrOp.Store16:
                case IrOp.Store32:
                case IrOp.StoreD:
                {
                    if (IsConst(x.A, out _))
                    {
                        Close();
                        break;
                    }
                    int b = _addrBase[x.A] >= 0 ? _addrBase[x.A] : x.A;
                    uint off = _addrBase[x.A] >= 0 ? _addrOff[x.A] : 0;
                    uint size = x.Op switch
                    {
                        IrOp.Store8 => 1u,
                        IrOp.Store16 => 2u,
                        IrOp.Store32 => 4u,
                        _ => 8u
                    };
                    uint min = Math.Min(gMin, off), end = Math.Max(gEnd, off + size);
                    if (gBase == b && end - min <= 4096 && off - min < 4096)
                    {
                        _markPlan[gLast] = -1;
                        gMin = min;
                        gEnd = end;
                    }
                    else
                    {
                        Close();
                        gBase = b;
                        gMin = off;
                        gEnd = off + size;
                    }
                    gLast = i;
                    lastOff = off;
                    break;
                }
                case IrOp.Load8:
                case IrOp.Load8S:
                case IrOp.Load16:
                case IrOp.Load16S:
                case IrOp.Load32:
                case IrOp.LoadD:
                case IrOp.Nop:
                case IrOp.GetReg:
                case IrOp.GetFlag:
                case IrOp.LoadState:
                case IrOp.SetReg:
                case IrOp.SetFlag:
                case IrOp.StoreState:
                case IrOp.Sub:
                case IrOp.And:
                case IrOp.Or:
                case IrOp.Xor:
                case IrOp.AndNot:
                case IrOp.OrNot:
                case IrOp.Mul:
                case IrOp.Not:
                case IrOp.Neg:
                case IrOp.Shl:
                case IrOp.Shr:
                case IrOp.Sar:
                case IrOp.Ror:
                case IrOp.Add:
                case IrOp.Clz:
                case IrOp.Bswap:
                case IrOp.Rev16:
                case IrOp.RevSh:
                case IrOp.Rbit:
                case IrOp.SExt8:
                case IrOp.SExt16:
                case IrOp.ZExt8:
                case IrOp.ZExt16:
                case IrOp.Bit:
                case IrOp.CmpEq:
                case IrOp.Select:
                case IrOp.AddC:
                case IrOp.SubC:
                case IrOp.TestNZ:
                case IrOp.ShiftReg:
                case IrOp.UMulL:
                case IrOp.SMulL:
                case IrOp.Add64:
                    break;
                default:
                    Close(); // control flow, calls, barriers, exclusives, SIMD (VecLdSt stores, handler paths)
                    break;
            }
        }
        Close();
    }

    private int[] _addrBase = new int[256];
    private uint[] _addrOff = new uint[256];

    /// <summary>The dirty mark of a guest store as planned by <see cref="PlanMarks"/>.</summary>
    private void MarkPlanned(int t)
    {
        int plan = _markPlan[_cur];
        if (plan < 0 || !Memory.DirtyPages.Enabled)
        {
            return;
        }

        if (plan == 0)
        {
            MarkDirty(t);
            return;
        }
        int r = Use(t, X0);
        _assembler.SubImm(X16, r, (uint)plan);
        MarkDirtyReg(X16);
    }

    private void MarkDirty(int t)
    {
        if (!Memory.DirtyPages.Enabled)
        {
            return;
        }

        if (_dirtyReg && !IsConst(t, out _) && InReg(t, out int r))
        {
            MarkDirtyReg(r);
            return;
        }
        LoadTo(X0, t);
        MarkDirtyReg(X0);
    }

    /// <summary>Dirty mark for the guest address in w<paramref name="addr"/> (the byte is only written while clear).</summary>
    private void MarkDirtyReg(int addr)
    {
        Arm64Assembler assembler = _assembler;
        int done = assembler.NewLabel();
        if (!_count)
        {
            // table base pinned in x26; the store of the mark (first write to a page since the last sweep) is out of line
            int cold = assembler.NewLabel();
            assembler.LsrImm(X16, addr, Memory.DirtyPages.PageShift);
            assembler.LdStReg(LsKind.Ldrb, X8, PinDirty, X16, xIndex: true);
            assembler.Cbz(X8, cold);
            assembler.Bind(done);
            _coldMarks.Add((cold, done));
            return;
        }
        assembler.LsrImm(X16, addr, Memory.DirtyPages.PageShift);
        assembler.SubImm(X17, MemBase, (uint)Memory.DirtyPages.TableBytes, true);
        assembler.LdStReg(LsKind.Ldrb, X8, X17, X16, xIndex: true);
        assembler.Cbnz(X8, done);
        assembler.MovImm(X8, 1);
        assembler.LdStReg(LsKind.Strb, X8, X17, X16, xIndex: true);
        assembler.Bind(done);
    }

    private static readonly int s_offStoreLog = StateLayout.Of(nameof(CpuState.StoreLog));
    private static readonly int s_offStoreLogCount = StateLayout.Of(nameof(CpuState.StoreLogCount));

    /// <summary>Appends {address, size, old value} (16-byte entry) to the state's store log.</summary>
    private void EmitStoreLog(IrInst x)
    {
        Arm64Assembler assembler = _assembler;
        int size = x.Op == IrOp.Store8 ? 1 : x.Op == IrOp.Store16 ? 2 : 4;
        LoadTo(X1, x.A);
        LdState(X16, s_offStoreLogCount);
        assembler.LdrX(X17, State, s_offStoreLog);
        assembler.Add(X17, X17, X16, Shift.Lsl, 4, x: true);
        assembler.Str(X1, X17, 0);
        assembler.MovImm(X2, (uint)size);
        assembler.Str(X2, X17, 4);
        assembler.LdStReg(size == 1 ? LsKind.Ldrb : size == 2 ? LsKind.Ldrh : LsKind.LdrW, X2, MemBase, X1);
        assembler.Str(X2, X17, 8);
        assembler.AddImm(X16, X16, 1);
        StState(X16, s_offStoreLogCount);
    }

    // ------------------------------------------------------------------ VFP

    /// <summary>
    /// Scalar VFP operation. Fast path in the Vita's FPSCR mode (DN=1, FZ=1, round to nearest, no short vectors),
    /// which is the guest FPCR mode generated code runs under: AArch64 FP arithmetic, conversions and compares then
    /// produce the ARMv7 results bit for bit (flush-to-zero with tininess before rounding, default NaN, saturating
    /// conversions with NaN -> 0). Any other FPSCR mode runs the interpreter handler.
    /// </summary>
    private void FpOp(IrInst x)
    {
        Arm64Assembler assembler = _assembler;
        var kind = (FpKind)(x.Imm & 0xFF);
        bool dbl = (x.Imm & (uint)FpFlags.Double) != 0;
        Op op = (x.Imm & (uint)FpFlags.OtherOp) != 0 ? Op.Vfp_Other : Op.Vfp_Arith;
        int dOff = s_offD + (int)(x.Imm4 & 0xFF), nOff = s_offD + (int)((x.Imm4 >> 8) & 0xFF), mOff = s_offD + (int)((x.Imm4 >> 16) & 0xFF);
        int slow = assembler.NewLabel(), done = assembler.NewLabel();
        LsKind ld = dbl ? LsKind.LdrD : LsKind.LdrS;
        LsKind st = dbl ? LsKind.StrD : LsKind.StrS;
        bool checkedAtEntry = (x.Imm & (uint)FpFlags.Checked) != 0;

        if (checkedAtEntry)
        {
        } // A64Opt.FpEntryCheck: the block entry checked the mode
        else if (_fastFpCheck)
        {
            LdState(X16, s_offFpscr);
            // FPSCR[25:16] == DN FZ, RMode 0, STRIDE 0, LEN 0, and bit 19 (reserved on the Vita) clear; a set bit 19 only
            // takes the handler, which is always exact
            assembler.Ubfx(X16, X16, 16, 10);
            assembler.CmpImm(X16, 0x300);
        }
        else
        {
            LdState(X16, s_offFpscr);
            assembler.MovImm(X17, 0x03F70000);
            assembler.And(X16, X16, X17);
            assembler.MovImm(X17, 0x03000000);
            assembler.Cmp(X16, X17);
        }
        if (!checkedAtEntry)
        {
            assembler.BCond(Cond.NE, slow);
        }

        // operands from the SIMD register cache when the op has no handler path (A64Opt.FpEntryCheck): a D register, or
        // an S register in the low half of its D register; double results are cached (write-through, like VecOps)
        bool cache = checkedAtEntry && _vcache, resultCached = false;
        if (cache)
        {
            _vcBusy = 0;
        }

        int Opnd(int scratch, int off)
        {
            int rel = off - s_offD;
            if (cache && (dbl || (rel & 7) == 0))
            {
                return Src(scratch, rel, false);
            }

            VcFlushOverlap(rel, dbl ? 8 : 4);
            assembler.LdSt(ld, scratch, State, off);
            return scratch;
        }
        int ResultReg() => cache && dbl ? Dst(0) : 0;
        void Result(int r)
        {
            if (cache && dbl)
            {
                Put(dOff - s_offD, r, false);
                resultCached = true;
            }
            else
            {
                VcFlushOverlap(dOff - s_offD, dbl ? 8 : 4);
                assembler.LdSt(st, r, State, dOff);
            }
        }
        if (_vcAnyDirty && kind is not (FpKind.Add or FpKind.Sub or FpKind.Mul or FpKind.NMul or FpKind.Div or FpKind.Mla
            or FpKind.Mls or FpKind.Nmla or FpKind.Nmls or FpKind.Sqrt or FpKind.Cmp or FpKind.CmpZero))
        {
            // conversions read and write the register file directly
            VcFlushOverlap((int)(x.Imm4 & 0xFF) & ~7, 16);
            VcFlushOverlap((int)((x.Imm4 >> 16) & 0xFF) & ~7, 16);
        }

        switch (kind)
        {
            case FpKind.Add:
            case FpKind.Sub:
            case FpKind.Mul:
            case FpKind.NMul:
            case FpKind.Div:
            {
                int n = Opnd(1, nOff), m = Opnd(2, mOff), r = ResultReg();
                switch (kind)
                {
                    case FpKind.Add:
                        assembler.Fadd(r, n, m, dbl);
                        break;
                    case FpKind.Sub:
                        assembler.Fsub(r, n, m, dbl);
                        break;
                    case FpKind.Mul:
                        assembler.Fmul(r, n, m, dbl);
                        break;
                    case FpKind.NMul:
                        assembler.Fnmul(r, n, m, dbl);
                        break; // -(n*m): a NaN product negates the default NaN, like VNMUL
                    default:
                        assembler.Fdiv(r, n, m, dbl);
                        break;
                }
                Result(r);
                break;
            }
            case FpKind.Mla:
            case FpKind.Mls:
            case FpKind.Nmla:
            case FpKind.Nmls:
            {
                // d = (+/-)d (+/-) round(n * m): the product is rounded on its own (not fused); cached operands are
                // never written in place
                int n = Opnd(1, nOff), m = Opnd(2, mOff), d = Opnd(3, dOff);
                assembler.Fmul(1, n, m, dbl);
                if (kind is FpKind.Nmla or FpKind.Nmls)
                {
                    assembler.Fneg(3, d, dbl);
                    d = 3;
                }
                int r = ResultReg();
                if (kind is FpKind.Mla or FpKind.Nmls)
                {
                    assembler.Fadd(r, d, 1, dbl);
                }
                else
                {
                    assembler.Fsub(r, d, 1, dbl);
                }

                Result(r);
                break;
            }
            case FpKind.Sqrt:
            {
                int m = Opnd(1, mOff), r = ResultReg();
                assembler.Fsqrt(r, m, dbl);
                Result(r);
                break;
            }
            case FpKind.Cmp:
            case FpKind.CmpZero:
            {
                int d = Opnd(0, dOff);
                if (kind == FpKind.CmpZero)
                {
                    assembler.FcmpZero(d, dbl);
                }
                else
                {
                    assembler.Fcmp(d, Opnd(1, mOff), dbl);
                }

                assembler.Mrs(X16, SysReg.Nzcv);              // FCMP's NZCV are VCMP's (unordered 0011, equal 0110, less 1000, greater 0010)
                LdState(X17, s_offFpscr);
                assembler.AndImm(X17, X17, 0x0FFFFFFF);
                assembler.Orr(X17, X17, X16);
                StState(X17, s_offFpscr);
                break;
            }
            case FpKind.CvtToDouble:
                assembler.LdSt(LsKind.LdrS, 1, State, mOff);
                assembler.FcvtSD(0, 1);
                assembler.LdSt(LsKind.StrD, 0, State, dOff);
                break;
            case FpKind.CvtToSingle:
                assembler.LdSt(LsKind.LdrD, 1, State, mOff);
                assembler.FcvtDS(0, 1);
                assembler.LdSt(LsKind.StrS, 0, State, dOff);
                break;
            case FpKind.CvtFromS32:
            case FpKind.CvtFromU32:
                LdState(X16, mOff);
                if (kind == FpKind.CvtFromS32)
                {
                    assembler.Scvtf(0, X16, dbl);
                }
                else
                {
                    assembler.Ucvtf(0, X16, dbl);
                }

                assembler.LdSt(st, 0, State, dOff);
                break;
            case FpKind.CvtToS32:
            case FpKind.CvtToS32R:
            case FpKind.CvtToU32:
            case FpKind.CvtToU32R:
                assembler.LdSt(ld, 1, State, mOff);
                switch (kind)
                {
                    case FpKind.CvtToS32:
                        assembler.Fcvtzs(X16, 1, dbl);
                        break;
                    case FpKind.CvtToS32R:
                        assembler.Fcvtns(X16, 1, dbl);
                        break;   // FPSCR rounding = nearest in this mode
                    case FpKind.CvtToU32:
                        assembler.Fcvtzu(X16, 1, dbl);
                        break;
                    default:
                        assembler.Fcvtnu(X16, 1, dbl);
                        break;
                }
                StState(X16, dOff);
                break;
            case FpKind.CvtToHalf:
                // IEEE half (AHP=0), FZ16=0: half denormals are produced; single inputs are flushed (FZ); NaN -> 0x7E00
                assembler.LdSt(LsKind.LdrS, 1, State, mOff);
                assembler.FcvtSH(0, 1);
                assembler.LdSt(LsKind.StrH, 0, State, dOff);
                break;
            case FpKind.CvtFromHalf:
                assembler.LdSt(LsKind.LdrH, 1, State, mOff);
                assembler.FcvtHS(0, 1);
                assembler.LdSt(LsKind.StrS, 0, State, dOff);
                break;
            case FpKind.CvtToFixed:
            {
                // AArch64's fixed-point FCVTZS/FCVTZU are VCVT's: round towards zero, saturate, NaN -> 0 (FZ flushes a
                // denormal input); a 16-bit size saturates the 32-bit result further; extended into a double register
                Vfp.FixedParams(x.Imm2, out _, out bool unsigned, out int size, out int frac);
                assembler.LdSt(ld, 1, State, dOff);
                if (frac == 0)
                {
                    if (unsigned)
                    {
                        assembler.Fcvtzu(X16, 1, dbl);
                    }
                    else
                    {
                        assembler.Fcvtzs(X16, 1, dbl);
                    }
                }
                else if (unsigned)
                {
                    assembler.FcvtzuFixed(X16, 1, frac, dbl);
                }
                else
                {
                    assembler.FcvtzsFixed(X16, 1, frac, dbl);
                }

                if (size == 16)
                {
                    if (unsigned)
                    {
                        assembler.MovImm(X17, 0xFFFF);
                        assembler.Cmp(X16, X17);
                        assembler.Csel(X16, X16, X17, Cond.LS);
                    }
                    else
                    {
                        assembler.MovImm(X17, 0x7FFF);
                        assembler.Cmp(X16, X17);
                        assembler.Csel(X16, X16, X17, Cond.LE);
                        assembler.MovImm(X17, 0xFFFF8000);
                        assembler.Cmp(X16, X17);
                        assembler.Csel(X16, X16, X17, Cond.GE);
                    }
                }
                if (dbl)
                {
                    if (!unsigned)
                    {
                        assembler.Sxtw(X16, X16); // a W result already has a zero upper half
                    }

                    assembler.StrX(X16, State, dOff);
                }
                else
                {
                    StState(X16, dOff);
                }

                break;
            }
            case FpKind.CvtFromFixed:
            {
                // one rounding (round to nearest in this mode), as the handler's (float)(v / 2^frac); exact for doubles
                Vfp.FixedParams(x.Imm2, out _, out bool unsigned, out int size, out int frac);
                LdState(X16, dOff);
                if (size == 16)
                {
                    if (unsigned)
                    {
                        assembler.Uxth(X16, X16);
                    }
                    else
                    {
                        assembler.Sxth(X16, X16);
                    }
                }
                if (frac == 0)
                {
                    if (unsigned)
                    {
                        assembler.Ucvtf(0, X16, dbl);
                    }
                    else
                    {
                        assembler.Scvtf(0, X16, dbl);
                    }
                }
                else if (unsigned)
                {
                    assembler.UcvtfFixed(0, X16, frac, dbl);
                }
                else
                {
                    assembler.ScvtfFixed(0, X16, frac, dbl);
                }

                assembler.LdSt(st, 0, State, dOff);
                break;
            }
            default:
                if (checkedAtEntry)
                {
                    throw new NotSupportedException($"FpOp {kind} has no inline form"); // FpOp is then not a call site
                }

                assembler.B(slow);
                break;
        }
        // both paths write d (at most 8 bytes at its offset): cached SIMD copies there are stale; the handler path
        // clobbers v16-v31, so it reloads the surviving cached registers from the (current) register file
        if (checkedAtEntry)
        {
            // exactly the bytes written: a dirty cached neighbour of an S result is still the only copy of its value
            bool arith = kind is FpKind.Add or FpKind.Sub or FpKind.Mul or FpKind.NMul or FpKind.Div or FpKind.Mla or FpKind.Mls
                or FpKind.Nmla or FpKind.Nmls or FpKind.Sqrt;
            if (!resultCached && kind is not (FpKind.Cmp or FpKind.CmpZero))
            {
                VcInvalidate((int)(x.Imm4 & 0xFF), arith && !dbl ? 4 : 8);
            }

            return;
        }
        assembler.B(done);
        VcInvalidate((int)(x.Imm4 & 0xFF), 8);
        assembler.Bind(slow);
        CallHandler((uint)op, x.Imm2, x.Imm3, restore: true); // merges with the fast path, which is in the guest mode
        VcReload();
        assembler.Bind(done);
    }

    // ------------------------------------------------------------------ runtime stubs

    /// <summary>
    /// The entry trampoline (x0 = CpuState*, x1 = block entry): saves x19-x30, builds the frame (host FPCR, guest FPCR,
    /// spill slots), loads x19/x20, switches FPCR to the guest mode and jumps to the block. Blocks leave through the
    /// epilogue (returned in <paramref name="epilogueOffset"/>), which restores the host FPCR.
    /// </summary>
    public static byte[] EmitTrampoline(out int epilogueOffset, bool pinnedCounters = false, bool blockCounts = true)
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
        a.LdrX(MemBase, State, s_offMemBase); // the state's guest memory base: its process's address space
        if (pinnedCounters)
        {
            // x28 = CycleAcc - CheckWindow (the window is full when it reaches 0), x26/x27 = NBlocks/NInstr
            a.LdrX(PinBudget, State, s_offCycleAcc);
            a.LdrX(X17, State, s_offCheckWindow);
            a.Sub(PinBudget, PinBudget, X17, x: true);
            if (blockCounts)
            {
                a.LdrX(PinBlocks, State, s_offNBlocks);
                a.LdrX(PinInstr, State, s_offNInstr);
            }
            else
            {
                a.SubImm(PinDirty, MemBase, (uint)Memory.DirtyPages.TableBytes, true);
            }
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
            a.LdrX(X17, State, s_offCheckWindow);
            a.Add(X16, PinBudget, X17, x: true);
            a.StrX(X16, State, s_offCycleAcc);
            if (blockCounts)
            {
                a.StrX(PinBlocks, State, s_offNBlocks);
                a.StrX(PinInstr, State, s_offNInstr);
            }
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
    /// <param name="twoWayCodeBase">Nonzero: the table is two-way with packed entries (<see cref="A64Opt.AssocDispatch"/>)
    /// and this is the code arena's start.</param>
    public static byte[] EmitDispatchEntry(ulong epilogue, ulong twoWayCodeBase = 0)
    {
        var a = new Arm64Assembler();
        int miss = a.NewLabel();
        a.Ldr(X0, State, s_offR + 60);
        a.Ldr(X1, State, s_offThumb);
        a.Orr(X1, X0, X1);                                   // key = pc | thumb
        if (twoWayCodeBase != 0)
        {
            int hit = a.NewLabel();
            a.Ubfx(X2, X0, 2, NativeRuntime.DispatchBits - 1);
            a.LdrX(X3, State, s_offDispatchTable);
            a.Add(X3, X3, X2, Shift.Lsl, 5, x: true);
            a.LdrX(X4, X3, 0);
            a.Cmp(X4, X1);
            a.BCond(Cond.EQ, hit);
            a.LdrX(X4, X3, 16);
            a.Cmp(X4, X1);
            a.BCond(Cond.NE, miss);
            a.Bind(hit);
            a.LdrLit(X5, twoWayCodeBase);
            a.Add(X5, X5, X4, Shift.Lsr, 32, x: true);
            a.Br(X5);
            a.Bind(miss);
            a.StrX(ZR, State, s_offLastExitSlot);
            a.LdrLit(X16, epilogue);
            a.Br(X16);
            return a.Finish();
        }
        a.Ubfx(X2, X0, 1, NativeRuntime.DispatchBits);
        a.LdrX(X3, State, s_offDispatchTable);
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
        a.StrX(ZR, State, s_offLastExitSlot);
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
        for (int i = 0; i + 4 <= length; i += 4)
        {
            sb.Append($"{i,5:X}: {*(uint*)(code + i):X8}\n");
        }

        return sb.ToString();
    }
}
