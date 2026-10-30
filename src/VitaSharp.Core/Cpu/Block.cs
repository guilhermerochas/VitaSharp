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
    public volatile int JitState; // 0 = interpreted, 1 = queued, 2 = compiled, 3 = no backend takes it
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
    /// <summary>Exit link slots that jump into this block's native code (cleared on invalidation).</summary>
    internal List<nint>? LinkedFrom;

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
    static DecodedInst FetchFault(uint addr, bool thumb)
    {
        var d = thumb ? Decoder.DecodeThumb16(0xDE00, addr, false, 14) : Decoder.DecodeArm(0xE7F000F0, addr);
        d.Flags |= InstFlags.EndsBlock;
        return d;
    }

    public static Block Build(uint pc, bool thumb)
    {
        var list = new List<DecodedInst>(16);
        uint addr = pc;
        if (!thumb)
        {
            while (true)
            {
                if (addr != pc && CpuCore.Probes is { } pa && pa.Contains(addr)) break; // probes are block starts
                if (!GuestMemory.IsMapped(addr)) { list.Add(FetchFault(addr, false)); break; }
                var d = Decoder.DecodeArm(Mem.R32(addr), addr);
                list.Add(d);
                addr += 4;
                if (d.EndsBlock || list.Count >= MaxInstructions) break;
            }
        }
        else
        {
            uint itState = 0;
            while (true)
            {
                if (addr != pc && CpuCore.Probes is { } pt && pt.Contains(addr) && (itState & 0xF) == 0) break; // probes are block starts
                bool inIt = (itState & 0xF) != 0;
                byte itCond = (byte)(itState >> 4);
                if (!GuestMemory.IsMapped(addr) || (Decoder.IsThumb32(Mem.R16(addr)) && !GuestMemory.IsMapped(addr + 2))) { list.Add(FetchFault(addr, true)); break; }
                ushort hw1 = Mem.R16(addr);
                DecodedInst d;
                if (Decoder.IsThumb32(hw1))
                {
                    uint ins = ((uint)hw1 << 16) | Mem.R16(addr + 2);
                    d = Decoder.DecodeThumb32(ins, addr, inIt, itCond);
                }
                else d = Decoder.DecodeThumb16(hw1, addr, inIt, itCond);

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
                if (d.EndsBlock) break;
                if (list.Count >= MaxInstructions && (itState & 0xF) == 0) break;
            }
        }
        var insts = list.ToArray();
        long quarters = 0;
        foreach (ref var d in insts.AsSpan()) quarters += CycleCost(in d);
        return new Block { Start = pc, End = addr, Thumb = thumb, Insts = insts, Cycles = Math.Max(1, (quarters + CostUnit / 2) / CostUnit), HotAt = Jit.InitialThreshold };
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
public sealed class BlockCache
{
    readonly ConcurrentDictionary<ulong, Block> _blocks = new();

    static ulong Key(uint pc, bool thumb) => pc | (thumb ? 1ul << 32 : 0);

    public Block Get(uint pc, bool thumb)
    {
        var key = Key(pc, thumb);
        if (_blocks.TryGetValue(key, out var b)) return b;
        b = BlockBuilder.Build(pc, thumb);
        return _blocks.GetOrAdd(key, b);
    }

    public bool TryGet(uint pc, bool thumb, out Block? block) => _blocks.TryGetValue(Key(pc, thumb), out block);

    /// <summary>Drop every block overlapping [addr, addr+size) (code was rewritten, e.g. module load/unload).</summary>
    public void Invalidate(uint addr, uint size)
    {
        foreach (var kv in _blocks)
        {
            var b = kv.Value;
            if (b.Start < addr + size && b.End > addr && _blocks.TryRemove(kv.Key, out _))
                Retire(b);
        }
    }

    public void Clear()
    {
        foreach (var kv in _blocks) Retire(kv.Value);
        _blocks.Clear();
    }

    /// <summary>A block left the cache: native exits linked to it must go back through the dispatcher.</summary>
    static void Retire(Block b)
    {
        if (b.NativeEntry != 0) Native.NativeRuntime.Unlink(b);
        else b.Invalidated = true;
    }

    public int Count => _blocks.Count;
}
