using System.Collections.Concurrent;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>A straight-line run of decoded guest instructions ending at a control-flow instruction.</summary>
public sealed unsafe class Block
{
    public uint Start;
    public uint End;
    public bool Thumb;
    public DecodedInst[] Insts = [];
    /// <summary>Approximate hardware cost of the block (see <see cref="BlockBuilder.CycleCost"/>).</summary>
    public long Cycles;
    public int ExecCount;
    public volatile int JitState; // 0 = interpreted, 1 = queued, 2 = compiled, 3 = no backend takes it, 4 = translated ahead, not reached yet
    /// <summary>Index in <see cref="Jit.Chain"/> of the next backend to offer the block to.</summary>
    public int Tier;
    /// <summary>Executed instructions (ExecCount x length) at which the backend at <see cref="Tier"/> takes the block.</summary>
    public long HotAt = long.MaxValue;
    /// <summary>Managed code of the block (IL backend).</summary>
    public JitBlockFn? Compiled;
    /// <summary>Whatever backend compiled the block.</summary>
    public Backends.CompiledBlock? Code;
    /// <summary>Native code entry (native backend), 0 when none. Run through <see cref="Native.NativeRuntime.Run"/>.</summary>
    public nint NativeEntry;
    /// <summary>Set when the block was dropped from the cache (guest code rewritten): exits must not link to it.</summary>
    public volatile bool Invalidated;
    /// <summary>Translated before it ran (<see cref="Jit.CompileAheadOne"/>), not because it got hot.</summary>
    public bool Ahead;
    /// <summary>The <see cref="CodePrecompiler.Priority"/> whose work translated the block ahead (statistics).</summary>
    public byte AheadPriority;
    /// <summary>Translated ahead on a guess of the analysis (not because an earlier session ran it): it may be evicted
    /// while it has not run (<see cref="Jit.EvictAhead"/>).</summary>
    public bool Speculative;
    /// <summary>When the block's compiled code was first used (a process-wide sequence number; 0: not yet): the code
    /// cache keeps the blocks in this order, so the next boot translates what the game needs first, first.</summary>
    public long FirstUse;
    /// <summary>VFP short-vector mode (FPSCR LEN/STRIDE bits) the block was expanded for (<see cref="BlockBuilder.ForVectorMode"/>),
    /// 0 for an ordinary block. Such a block only runs while the FPSCR is in that mode, one block per native entry.</summary>
    public uint VectorFpscr;
    /// <summary>The last short-vector expansion of this block (<see cref="BlockBuilder.ForVectorMode"/>), null when
    /// it never ran in vector mode.</summary>
    public Block? VectorVariant;
    /// <summary>Exit link slots that jump into this block's native code (cleared on invalidation).</summary>
    internal List<nint>? _linkedFrom;
    /// <summary>The cache (process) the block belongs to: its indirect-exit table is the process's.</summary>
    public BlockCache? Owner;
    /// <summary>Guest state slots the block overwrites before reading them (<see cref="Native.StateLiveness"/>: bits 0-14
    /// R0-R14, 16-19 N Z C V), <see cref="Native.StateLiveness.Unknown"/> until computed.</summary>
    internal int _deadIn = Native.StateLiveness.Unknown;
    /// <summary>Guest code ranges [start, end) other than the block's own that its compiled code depends on (the
    /// successors whose <see cref="_deadIn"/> let it drop state stores): rewriting them drops this block too.</summary>
    internal uint[]? _deps;
    /// <summary>The block is in its <see cref="Owner"/>'s cache (set when it went in, cleared when it left): the
    /// cache's page index drops the entries of blocks that left it.</summary>
    internal volatile bool _inCache;

    /// <summary>Sets <see cref="_deps"/>: the owner's page index learns the new ranges, so rewriting one of them finds
    /// this block without scanning the whole cache.</summary>
    internal void SetDependencies(uint[]? deps)
    {
        _deps = deps;
        if (deps != null)
        {
            Owner?.IndexRanges(this, deps);
        }
    }

    /// <summary>Whether the block's code or a range it depends on overlaps [addr, addr + size).</summary>
    internal bool Overlaps(uint addr, uint size)
    {
        if (Start < addr + size && End > addr)
        {
            return true;
        }

        if (_deps is { } d)
        {
            for (int i = 0; i + 1 < d.Length; i += 2)
            {
                if (d[i] < addr + size && d[i + 1] > addr)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public override string ToString() => $"Block {Start:X8}-{End:X8} {(Thumb ? "T" : "A")} n={Insts.Length}";
}

public static unsafe class BlockBuilder
{
    public const int MaxInstructions = 192;

    /// <summary>
    /// Instruction fetch from a page no allocation covers (a jump through a null or garbage pointer). The hardware
    /// raises a prefetch abort and the kernel kills the thread; here the block ends in a permanently undefined
    /// instruction at that address, which the thread reports as a prefetch abort (Vita3K stops the thread too).
    /// </summary>
    private static DecodedInst FetchFault(uint addr, bool thumb)
    {
        DecodedInst d = thumb ? Decoder.DecodeThumb16(0xDE00, addr, false, 14) : Decoder.DecodeArm(0xE7F000F0, addr);
        d.Flags |= InstFlags.EndsBlock;
        return d;
    }

    /// <summary>
    /// The IT state (CPSR.IT, as <see cref="CpuState.ITState"/>) the Thumb instruction at <paramref name="pc"/> of
    /// <paramref name="b"/> executes in: 0 outside IT blocks and in ARM code.
    /// </summary>
    public static uint ItStateAt(Block b, uint pc)
    {
        if (!b.Thumb)
        {
            return 0;
        }

        uint itState = 0;
        foreach (ref readonly DecodedInst d in b.Insts.AsSpan())
        {
            if (d.Address == pc)
            {
                return itState;
            }

            if ((itState & 0xF) != 0)
            {
                itState = (itState & 7) == 0 ? 0 : (itState & 0xE0) | ((itState << 1) & 0x1F);
            }

            if (d.Op == Op.Thumb_It)
            {
                itState = d.Raw & 0xFFu;
            }
        }

        return 0;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static List<DecodedInst>? t_decoded;

    /// <summary>Decodes the block at <paramref name="pc"/>; a Thumb block can start inside an IT block, in IT state
    /// <paramref name="itState"/> (the return from an exception taken there).</summary>
    public static Block Build(uint pc, bool thumb, uint itState = 0)
    {
        // the decoding thread's buffer: a block's instructions are copied out (Insts), so only that array and the block
        // are allocated per decode (the precompilation of a large game decodes ~700k blocks in the background)
        List<DecodedInst> list = t_decoded ??= new List<DecodedInst>(MaxInstructions + 8);
        list.Clear();
        uint addr = pc;
        if (!thumb)
        {
            while (true)
            {
                if (addr != pc && CpuCore.Probes is { } pa && pa.Contains(addr))
                {
                    break; // probes are block starts
                }

                if (!GuestMemory.IsMapped(addr))
                {
                    list.Add(FetchFault(addr, false));
                    break;
                }
                DecodedInst d = Decoder.DecodeArm(GuestMemory.Read<uint>(addr), addr); // the building thread's (its process's) space
                list.Add(d);
                addr += 4;
                if (d.EndsBlock || list.Count >= MaxInstructions || d.Op == Op.Vfp_Vmsr)
                {
                    break;
                }
            }
        }
        else
        {
            while (true)
            {
                if (addr != pc && CpuCore.Probes is { } pt && pt.Contains(addr) && (itState & 0xF) == 0)
                {
                    break; // probes are block starts
                }

                bool inIt = (itState & 0xF) != 0;
                byte itCond = (byte)(itState >> 4);
                if (!GuestMemory.IsMapped(addr) || (Decoder.IsThumb32(GuestMemory.Read<ushort>(addr)) && !GuestMemory.IsMapped(addr + 2)))
                {
                    list.Add(FetchFault(addr, true));
                    break;
                }
                ushort hw1 = GuestMemory.Read<ushort>(addr);
                DecodedInst d;
                if (Decoder.IsThumb32(hw1))
                {
                    uint ins = ((uint)hw1 << 16) | GuestMemory.Read<ushort>(addr + 2);
                    d = Decoder.DecodeThumb32(ins, addr, inIt, itCond);
                }
                else
                {
                    d = Decoder.DecodeThumb16(hw1, addr, inIt, itCond);
                }

                if (inIt)
                {
                    // ITAdvance
                    itState = (itState & 7) == 0 ? 0 : (itState & 0xE0) | ((itState << 1) & 0x1F);
                    // condition 1110 inside IT means "always"; 1111 never appears
                }
                if (d.Op == Op.Thumb_It)
                {
                    itState = hw1 & 0xFFu;
                }
                list.Add(d);
                addr += d.Size;
                if (d.EndsBlock)
                {
                    break;
                }
                // VMSR may switch VFP short-vector mode, which the rest of a block cannot follow
                if ((list.Count >= MaxInstructions || d.Op == Op.Vfp_Vmsr) && (itState & 0xF) == 0)
                {
                    break;
                }
            }
        }
        DecodedInst[] insts = list.ToArray();
        long quarters = 0;
        foreach (ref DecodedInst d in insts.AsSpan())
        {
            quarters += CycleCost(in d);
        }

        return new Block { Start = pc, End = addr, Thumb = thumb, Insts = insts, Cycles = Math.Max(1, (quarters + CostUnit / 2) / CostUnit), HotAt = Jit.InitialThreshold };
    }

    /// <summary>
    /// The block as it runs in VFP short-vector mode (<paramref name="fpscr"/> LEN/STRIDE non-zero): every
    /// vector-capable instruction (<see cref="Vfp.IsVectorCapable"/>) is replaced by the scalar instructions it runs as
    /// (<see cref="Vfp.ExpandVector"/>, the same expansion the interpreter's <see cref="Vfp.VectorOp"/> runs), each at
    /// the instruction's address and with its condition, so any backend translates it as scalar code. The Vita's
    /// Cortex-A9 has no short-vector hardware: on the console such an instruction traps and homebrew support code
    /// (VFPVector, through kubridge) runs the same element operations. The mode cannot change inside a block (VMSR
    /// ends it), so the expansion holds for the whole block. The result is not in any block cache: it is kept on the
    /// scalar block (<see cref="Block.VectorVariant"/>) and goes away with it when the guest rewrites the code.
    /// </summary>
    public static Block ForVectorMode(Block scalar, uint fpscr)
    {
        uint mode = fpscr & Vfp.FpscrVectorMask;
        var list = new List<DecodedInst>(scalar.Insts.Length + 8);
        Span<uint> elements = stackalloc uint[Vfp.MaxVectorLength];
        long quarters = 0;
        foreach (ref DecodedInst d in scalar.Insts.AsSpan())
        {
            if (!Vfp.IsVectorCapable(d.Op, d.Raw))
            {
                list.Add(d);
                quarters += CycleCost(in d);
                continue;
            }

            int count = Vfp.ExpandVector(d.Op, d.Raw, mode, elements);
            for (int i = 0; i < count; i++)
            {
                DecodedInst element = d;
                element.Raw = elements[i];
                list.Add(element);
                quarters += CycleCost(in element);
            }
        }

        return new Block
        {
            Start = scalar.Start,
            End = scalar.End,
            Thumb = scalar.Thumb,
            Insts = list.ToArray(),
            Cycles = Math.Max(1, (quarters + CostUnit / 2) / CostUnit),
            HotAt = Jit.InitialThreshold,
            Owner = scalar.Owner,
            VectorFpscr = mode,
        };
    }

    /// <summary><see cref="CycleCost"/> is in 1/CostUnit cycles, so that dual issue (an average below one cycle) can
    /// be expressed; <see cref="Block.Cycles"/> is in whole cycles.</summary>
    public const int CostUnit = 4;

    /// <summary>
    /// Average cost of one instruction on the Vita's Cortex-A9, in quarter cycles (<see cref="CostUnit"/>). The
    /// emulated CPU is paced by these (<see cref="CpuCore.InstructionsPerSecond"/>, 333 MHz unless the game raises it), so game code sees
    /// hardware-like execution times (physics budgets, frame timers, busy-wait loops) whatever the host speed. Every
    /// CPU backend charges the block total (<see cref="Block.Cycles"/>), so this table is the only place the model lives.
    /// <para>
    /// The A9 decodes two instructions per cycle and issues them out of order to two ALUs (one with the multiplier),
    /// a load/store unit and the VFP/NEON unit, behind an 8-11 stage pipeline (Cortex-A9 TRM, "Cycle timings and
    /// interlock behavior"; Cortex-A9 NEON MPE / FPU TRMs). On typical compiled code it sustains about 1.2-1.5 cycles
    /// per instruction: independent ALU operations pair, L1-hit loads (3-4 cycle load-use latency) are mostly hidden
    /// by renaming, and the global-history predictor, BTAC and return stack predict most branches (a misprediction
    /// costs ~8 cycles). The table charges the issue cost plus the average share of stalls per instruction class: an
    /// average-case approximation, not a pipeline model.
    /// </para>
    /// <list type="bullet">
    /// <item>data processing, moves, compares, IT, extends, bitfield, VMOV core/VFP: 0.75 (about one in three pairs)</item>
    /// <item>single loads (incl. LDRD, VLDR, VLDn): 1.5 (issue plus the exposed load-use latency and the cache-miss
    /// share); single stores: 1 (they retire through the store buffer)</item>
    /// <item>LDM/STM/PUSH/POP/VLDM/VSTM: 1 + 0.5 per register word (the load/store unit moves 64 bits per cycle)</item>
    /// <item>direct unconditional branches (B, BL, BLX imm): 1 (BTAC-predicted); conditional branches, CBZ/CBNZ and
    /// register branches (BX/BLX, return stack): 1.5 (issue plus ~5% mispredictions at ~8 cycles); TBB/TBH: 4 (a
    /// load and an indirect branch)</item>
    /// <item>multiplies: 2; long multiplies: 3 (32x32 multiplies occupy the MUL pipe for 2-3 cycles)</item>
    /// <item>VFP arithmetic: 2 (single-cycle issue for F32, but 4-8 cycle result latencies stall the dependent chains
    /// game math is made of); VDIV/VSQRT: 15 (not pipelined; F32 13-15 cycles); other VFP (VMOV, VABS, VNEG, VCMP,
    /// VCVT): 1; VMRS/VMSR: 3 (wait for the VFP pipeline)</item>
    /// <item>NEON: 1 per D register operation, 2 per Q register operation (the A9 NEON datapath is 64 bits wide)</item>
    /// <item>exclusives/SWP: 4; MRC/MCR (TLS register reads): 3; barriers: 10; SVC: 20 (the HLE function itself is
    /// charged by the HLE cost ledger, <see cref="Hle.HleFunction.Cost"/>)</item>
    /// </list>
    /// Measured averages: ~1.07 cycles per instruction on Sonic Mania's software renderer (pixel loops: ~55% ALU/IT,
    /// 22% loads/stores, 14% conditional branches, 4% multiplies), 1.3-1.5 on Killzone's game, job and Havok threads.
    /// The previous table (ALU 1, loads/stores 4, branches 3: ~2.5 per instruction) made Mania's menus need 22 ms of
    /// emulated CPU per frame at 444 MHz and run at 30-45 fps where hardware runs them at 60.
    /// </summary>
    public static int CycleCost(in DecodedInst d) => d.Op switch
    {
        // single loads and stores (L bit: bit 20 in 32-bit encodings, bit 11 in 16-bit ones, bit 21 for VLDn/VSTn)
        Op.A32_LdStImm or Op.A32_LdStReg or Op.A32_LdStExtra or Op.T32_LdSt or Op.T32_LdStDual or Op.Vfp_LdrStrArm
            or Op.Vfp_LdrStrThumb => (d.Raw & (1u << 20)) != 0 ? 6 : 4,
        Op.T16_LdStImm or Op.T16_LdStH or Op.T16_LdStSp => (d.Raw & (1u << 11)) != 0 ? 6 : 4,
        Op.T16_LdStReg => ((d.Raw >> 9) & 7) >= 3 ? 6 : 4, // opB 000-010: STR, STRH, STRB
        Op.T16_LdrLit => 6,
        Op.Neon_LoadStore => (d.Raw & (1u << 21)) != 0 ? 6 : 4,
        Op.A32_Exclusive or Op.T32_ExclusiveWord or Op.T32_ExclusiveOther or Op.A32_Swp => 16,
        // multiple loads/stores: 1 + half a cycle per register word
        Op.A32_LdmStm or Op.T32_LdmStm => 4 + 2 * System.Numerics.BitOperations.PopCount(d.Raw & 0xFFFF),
        Op.T16_Push or Op.T16_Pop => 4 + 2 * System.Numerics.BitOperations.PopCount(d.Raw & 0x1FF),
        Op.T16_Stm or Op.T16_Ldm => 4 + 2 * System.Numerics.BitOperations.PopCount(d.Raw & 0xFF),
        Op.Vfp_LdmStm => 4 + 2 * (int)(d.Raw & 0xFF), // imm8 = words transferred
        // multiplies
        Op.A32_Multiply or Op.A32_HalfMultiply or Op.A32_SignedMultiplyMedia or Op.T32_Mul or Op.A32_Usad8 => 8,
        Op.T32_LongMul => 12,
        // branches: direct unconditional ones are predicted by the BTAC; the rest pay the misprediction share
        Op.A32_B or Op.A32_Bl => d.Raw >> 28 == 0xE ? 4 : 6,
        Op.A32_BlxImm or Op.T16_B or Op.T32_BW or Op.T32_Bl or Op.T32_BlxImm => 4,
        Op.A32_Bx or Op.A32_BlxReg or Op.T16_BCond or Op.T16_BxBlx or Op.T16_Cbz or Op.T32_BCondW or Op.T32_BxjOrSubsPcLr => 6,
        Op.T32_Tbb => 16,
        // VFP: VDIV is opc1 = 0b100 (bits 23, 21:20); VSQRT is opc1 = 0b111 with opc2 = 0b0001 and bit 7 set
        Op.Vfp_Arith => (d.Raw & 0x00B0_0000) == 0x0080_0000 ? 60 : 8,
        Op.Vfp_Other => (d.Raw & 0x000F_0080) == 0x0001_0080 ? 60 : 4,
        Op.Vfp_Vmrs or Op.Vfp_Vmsr => 12,
        Op.Neon_ThreeSame or Op.Neon_ThreeDiff or Op.Neon_Scalar or Op.Neon_Shift or Op.Neon_Misc or Op.Neon_Imm
            or Op.Neon_Vext or Op.Neon_Vtbl or Op.Neon_VdupScalar => (d.Raw & 0x40) != 0 ? 8 : 4, // Q bit
        // system
        Op.A32_Svc or Op.T16_Svc => 80,
        Op.A32_Barrier or Op.T32_Barrier => 40,
        Op.A32_Mrc or Op.A32_Mcr or Op.T32_Mrc or Op.T32_Mcr => 12,
        _ => 3, // data processing, moves, IT, extends, bitfield, NOPs, VMOV between core and VFP registers
    };
}

/// <summary>Process-wide translation cache shared by all guest threads.</summary>
/// <summary>
/// The translated code of one process (each <see cref="Emulator"/> has its own: processes in different address spaces
/// can have different code at the same address), with the native backend's indirect-exit table for its threads.
/// </summary>
public sealed unsafe class BlockCache
{
    private readonly ConcurrentDictionary<ulong, Block> _blocks = new();

    /// <summary>
    /// The cached blocks by the guest pages (<see cref="PageShift"/>) their code and dependency ranges touch, so that
    /// <see cref="Invalidate"/> of a small range looks only at the blocks of its pages. Scanning every block for each
    /// rewritten import stub cost seconds once the ARM code precompilation filled the cache with ~300,000 blocks: every
    /// firmware module the shell loads rebinds hundreds of stubs (Linker.RegisterExports), 5 s for livespace_db alone,
    /// on the shell's single core (Dolphin's JitBaseBlockCache keeps the same kind of per-page block map, Ryujinx a range
    /// list). Entries of blocks that left the cache are dropped when their page is looked at (<see cref="Block._inCache"/>).
    /// Guarded by <see cref="_pagesLock"/>.
    /// </summary>
    private readonly Dictionary<uint, List<Block>> _blocksByPage = new();
    private readonly object _pagesLock = new();

    /// <summary>Statistics and tests: the block entries <see cref="Invalidate"/> looked at (the cost of finding what a
    /// rewrite drops).</summary>
    internal long InvalidateExamined => Interlocked.Read(ref _invalidateExamined);

    private long _invalidateExamined;

    /// <summary>log2 of the page size of <see cref="_blocksByPage"/> (4 KB).</summary>
    internal const int PageShift = 12;

    private nint _dispatch;
    private volatile bool _dataAbortGuards;

    /// <summary>
    /// The process's system has a data abort handler (<see cref="DataAborts"/>): guest accesses check the page mapping,
    /// and the code translated for this cache carries the guards. Switching it on drops every translated block.
    /// </summary>
    public bool DataAbortGuards => _dataAbortGuards;

    /// <summary>Switches <see cref="DataAbortGuards"/> on; true the first time (the blocks were dropped).</summary>
    public bool ArmDataAborts()
    {
        if (_dataAbortGuards)
        {
            return false;
        }

        // where the host's memory protection can find the aborting accesses, native code needs no guards
        // (HostFaultAborts): the space's unmapped pages are made inaccessible first
        // (the handler first: a host access to a page made inaccessible needs it)
        if (Native.HostFaultAborts.Supported && Memory.GuestMemory.Instance is { } memory && Native.HostFaultAborts.EnsureInstalled())
        {
            memory.ArmHostFaults(Space ?? memory.Primary);
            _hostFaultAborts = true;
        }

        _dataAbortGuards = true;
        Invalidate(0, uint.MaxValue);
        return true;
    }

    private volatile bool _hostFaultAborts;

    /// <summary>
    /// With <see cref="DataAbortGuards"/>: the space's unmapped pages fault on the host and native code finds data
    /// aborts through those faults (<see cref="Native.HostFaultAborts"/>) instead of a guard per access.
    /// </summary>
    public bool HostFaultAborts => _hostFaultAborts;

    /// <summary>Tests: native code of this cache uses the guards even where host faults could replace them.</summary>
    internal void UseGuardsForTests()
    {
        _hostFaultAborts = false;
        Invalidate(0, uint.MaxValue);
    }

    /// <summary>Tests: the guards off again (the blocks translated with them are dropped).</summary>
    internal void DisarmDataAbortsForTests()
    {
        _dataAbortGuards = false;
        _hostFaultAborts = false;
        Invalidate(0, uint.MaxValue);
    }

    /// <summary>The process's address space: where blocks are decoded from by threads outside it (the JIT workers,
    /// <see cref="GetFromAnyThread"/>). Null: the calling thread's.</summary>
    public AddressSpace? Space;

    private static ulong Key(uint pc, bool thumb) => pc | (thumb ? 1ul << 32 : 0);

    /// <summary>
    /// Counts the invalidations (<see cref="Invalidate"/>, <see cref="Revalidate"/>, <see cref="Clear"/>): a block
    /// decoded while one ran may hold the code from before the rewrite and reach the cache after the invalidation looked
    /// for it (a JIT worker or another guest thread decoding while a plugin patches the code), so <see cref="Get"/>
    /// checks such a block against the memory again before handing it out.
    /// </summary>
    private long _invalidations;

    /// <summary>Counts the code changes of every cache of the process (each <see cref="Invalidate"/>,
    /// <see cref="Revalidate"/> and <see cref="Clear"/>): whoever caches a fact about guest code (whether a firmware
    /// function's entry is patched, <see cref="Hle.FirmwareKernelModules"/>) checks it again only when this moved.</summary>
    private static long s_codeChanges;

    /// <summary>See <see cref="s_codeChanges"/>.</summary>
    public static long CodeChanges => Volatile.Read(ref s_codeChanges);

    /// <summary>Test point: called in <see cref="Get"/> between decoding a block and adding it to the cache.</summary>
    internal Action<Block>? AfterDecode { get; set; }

    public Block Get(uint pc, bool thumb)
    {
        ulong key = Key(pc, thumb);
        if (_blocks.TryGetValue(key, out Block? b))
        {
            return b;
        }

        while (true)
        {
            long generation = Volatile.Read(ref _invalidations);
            Block built = BlockBuilder.Build(pc, thumb);
            built.Owner = this;
            AfterDecode?.Invoke(built);
            b = _blocks.GetOrAdd(key, built);
            if (ReferenceEquals(b, built))
            {
                built._inCache = true;
                IndexRange(built, built.Start, built.End);
            }

            // the block is in the cache (and its pages) before the count is read again: an invalidation counted after
            // this read finds it in its scan; one counted between the two reads may have scanned before it was there
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _invalidations) == generation)
            {
                return b;
            }

            if (!b.Invalidated && CodeUnchanged(b))
            {
                return b;
            }

            if (_blocks.TryRemove(new KeyValuePair<ulong, Block>(key, b)))
            {
                b._inCache = false;
                Retire(b);
            }
        }
    }

    /// <summary>Puts <paramref name="b"/> in the page index for the ranges [start, end) of <paramref name="ranges"/>
    /// (its dependency ranges, <see cref="Block.SetDependencies"/>).</summary>
    internal void IndexRanges(Block b, uint[] ranges)
    {
        for (int i = 0; i + 1 < ranges.Length; i += 2)
        {
            IndexRange(b, ranges[i], ranges[i + 1]);
        }
    }

    /// <summary>Puts <paramref name="b"/> in the page index for every page [start, end) touches (the page of
    /// <paramref name="start"/> for an empty range).</summary>
    private void IndexRange(Block b, uint start, uint end)
    {
        uint first = start >> PageShift;
        uint last = end > start ? (end - 1) >> PageShift : first;
        lock (_pagesLock)
        {
            for (uint page = first; ; page++)
            {
                if (!_blocksByPage.TryGetValue(page, out List<Block>? list))
                {
                    list = new List<Block>(4);
                    _blocksByPage[page] = list;
                }

                if (list.Count == 0 || !ReferenceEquals(list[^1], b))
                {
                    list.Add(b);
                }

                if (page == last)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The cached blocks that overlap [addr, addr + size) (<see cref="Block.Overlaps"/>), from the page index: the pages
    /// of the range, or every indexed page when the range has more pages than the index. Entries of blocks that left the
    /// cache are dropped on the way, and so are those of the blocks returned (the caller takes them out of the cache).
    /// </summary>
    private HashSet<Block> TakeOverlapping(uint addr, uint size)
    {
        var found = new HashSet<Block>(ReferenceEqualityComparer.Instance);
        uint first = addr >> PageShift;
        uint last = size == 0 ? first : (uint)(((ulong)addr + size - 1) >> PageShift);
        lock (_pagesLock)
        {
            if ((ulong)last - first + 1 > (ulong)_blocksByPage.Count)
            {
                List<uint>? emptied = null;
                foreach (KeyValuePair<uint, List<Block>> entry in _blocksByPage)
                {
                    if (entry.Key >= first && entry.Key <= last && TakeOverlappingOfPage(entry.Value, addr, size, found, ref _invalidateExamined))
                    {
                        emptied ??= new List<uint>();
                        emptied.Add(entry.Key);
                    }
                }

                if (emptied != null)
                {
                    foreach (uint page in emptied)
                    {
                        _blocksByPage.Remove(page);
                    }
                }
            }
            else
            {
                for (uint page = first; ; page++)
                {
                    if (_blocksByPage.TryGetValue(page, out List<Block>? list) && TakeOverlappingOfPage(list, addr, size, found, ref _invalidateExamined))
                    {
                        _blocksByPage.Remove(page);
                    }

                    if (page == last)
                    {
                        break;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>Moves the blocks of one page's list that overlap the range into <paramref name="found"/> and drops the
    /// entries of blocks no longer cached; true when the list is left empty.</summary>
    private static bool TakeOverlappingOfPage(List<Block> list, uint addr, uint size, HashSet<Block> found, ref long examined)
    {
        examined += list.Count;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            Block b = list[i];
            bool overlaps = b._inCache && b.Overlaps(addr, size);
            if (!b._inCache || overlaps)
            {
                list[i] = list[^1];
                list.RemoveAt(list.Count - 1);
            }

            if (overlaps)
            {
                found.Add(b);
            }
        }

        return list.Count == 0;
    }

    /// <summary>The native backend's indirect-exit table of this process (<see cref="Native.NativeRuntime.Publish"/>),
    /// allocated when native code first needs it.</summary>
    public nint DispatchTable
    {
        get
        {
            if (_dispatch != 0)
            {
                return _dispatch;
            }

            nint t = Native.NativeRuntime.AllocateDispatchTable();
            if (Interlocked.CompareExchange(ref _dispatch, t, 0) != 0)
            {
                Native.NativeRuntime.FreeDispatchTable(t);
            }

            return _dispatch;
        }
    }

    ~BlockCache()
    {
        if (_dispatch != 0)
        {
            Native.NativeRuntime.FreeDispatchTable(_dispatch);
        }
    }

    public bool TryGet(uint pc, bool thumb, out Block? block) => _blocks.TryGetValue(Key(pc, thumb), out block);

    /// <summary>Removes <paramref name="block"/> if it is still the cached block at its start (<see cref="Jit.EvictAhead"/>).</summary>
    internal bool Remove(Block block)
    {
        if (!_blocks.TryRemove(new KeyValuePair<ulong, Block>(Key(block.Start, block.Thumb), block)))
        {
            return false;
        }

        // its page index entries go when their pages are next looked at
        block._inCache = false;
        return true;
    }

    /// <summary><see cref="Get"/> from a thread that may work in another address space (a JIT worker decoding the
    /// targets of the block it compiles): decodes in <see cref="Space"/>.</summary>
    public Block GetFromAnyThread(uint pc, bool thumb)
    {
        if (_blocks.TryGetValue(Key(pc, thumb), out Block? b))
        {
            return b;
        }

        using (AddressSpace.Use(Space ?? AddressSpace.Current))
        {
            return Get(pc, thumb);
        }
    }

    /// <summary>Drop every block overlapping [addr, addr+size) (code was rewritten, e.g. module load/unload).</summary>
    public void Invalidate(uint addr, uint size)
    {
        Interlocked.Increment(ref _invalidations);
        Interlocked.Increment(ref s_codeChanges);
        foreach (Block b in TakeOverlapping(addr, size))
        {
            if (_blocks.TryRemove(new KeyValuePair<ulong, Block>(Key(b.Start, b.Thumb), b)))
            {
                b._inCache = false;
                Retire(b);
            }
        }
    }

    /// <summary>
    /// Keeps the blocks whose guest code is still what they were decoded from and drops the others (a save state was
    /// loaded: its memory replaced the process's). A block is kept when decoding its start again gives the same
    /// instructions, and when every range its compiled code depends on (<see cref="Block._deps"/>: superblock
    /// continuations, successors whose dead state it relied on) is itself an unchanged block. Dropped blocks leave the
    /// cache like an instruction-cache invalidation drops them (their links and dispatch entries are undone). Returns the
    /// numbers kept and dropped.
    /// </summary>
    public (int Kept, int Dropped) Revalidate()
    {
        using AddressSpace.Activation activation = AddressSpace.Use(Space ?? AddressSpace.Current);
        Interlocked.Increment(ref _invalidations);
        Interlocked.Increment(ref s_codeChanges);
        var unchanged = new HashSet<(uint Start, uint End)>();
        var changed = new List<KeyValuePair<ulong, Block>>();
        var withDeps = new List<KeyValuePair<ulong, Block>>();
        foreach (KeyValuePair<ulong, Block> kv in _blocks)
        {
            if (!kv.Value.Invalidated && CodeUnchanged(kv.Value))
            {
                unchanged.Add((kv.Value.Start, kv.Value.End));
                if (kv.Value._deps != null)
                {
                    withDeps.Add(kv);
                }
            }
            else
            {
                changed.Add(kv);
            }
        }
        foreach (KeyValuePair<ulong, Block> kv in withDeps)
        {
            uint[] deps = kv.Value._deps!;
            for (int i = 0; i + 1 < deps.Length; i += 2)
            {
                if (!unchanged.Contains((deps[i], deps[i + 1])))
                {
                    changed.Add(kv);
                    break;
                }
            }
        }
        int dropped = 0;
        foreach (KeyValuePair<ulong, Block> kv in changed)
        {
            if (_blocks.TryRemove(kv.Key, out _))
            {
                kv.Value._inCache = false;
                Retire(kv.Value);
                dropped++;
            }
        }
        return (_blocks.Count, dropped);
    }

    /// <summary>Whether decoding the guest memory at the block's start again gives the same instructions (decoding is a
    /// function of the bytes, so equal instructions mean the block's translation is still right).</summary>
    private static bool CodeUnchanged(Block b)
    {
        Block now = BlockBuilder.Build(b.Start, b.Thumb);
        if (now.End != b.End || now.Insts.Length != b.Insts.Length)
        {
            return false;
        }

        for (int i = 0; i < now.Insts.Length; i++)
        {
            ref readonly DecodedInst x = ref now.Insts[i];
            ref readonly DecodedInst y = ref b.Insts[i];
            if (x.Raw != y.Raw || x.Op != y.Op || x.Size != y.Size || x.Cond != y.Cond || x.Flags != y.Flags || x.Address != y.Address)
            {
                return false;
            }
        }
        return true;
    }

    public void Clear()
    {
        Interlocked.Increment(ref _invalidations);
        Interlocked.Increment(ref s_codeChanges);
        foreach (KeyValuePair<ulong, Block> kv in _blocks)
        {
            kv.Value._inCache = false;
            Retire(kv.Value);
        }

        _blocks.Clear();
        lock (_pagesLock)
        {
            _blocksByPage.Clear();
        }
    }

    /// <summary>A block left the cache: native exits linked to it must go back through the dispatcher. A block
    /// translated ahead that never ran gives its code back to the arena at once (it was never published).</summary>
    private static void Retire(Block b)
    {
        if (b.JitState == Jit.AheadPending && Jit.EvictAhead(b))
        {
            return;
        }

        if (b.NativeEntry != 0)
        {
            Native.NativeRuntime.Unlink(b);
        }
        else
        {
            b.Invalidated = true;
        }
    }

    public int Count => _blocks.Count;

    /// <summary>A snapshot of the cached blocks (<see cref="CodeCache"/> records the translated ones).</summary>
    public ICollection<Block> Blocks => _blocks.Values;

    /// <summary>Called once per block when its compiled code is first used (<see cref="Jit.ActivateAhead"/>, or the JIT
    /// compiled it because it got hot): the ARM code precompilation widens its frontier there.</summary>
    public Action<Block>? FirstUse;

    /// <summary>Called when the interpreter runs a block for the first time (code no translation covered yet, which
    /// may never get hot: an interpreted block calls and jumps to code that will): the precompilation widens its
    /// frontier there too, without waiting for the block to be compiled.</summary>
    public Action<Block>? FirstRun;
}
