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

    public override string ToString() => $"Block {Start:X8}-{End:X8} {(Thumb ? "T" : "A")} n={Insts.Length}";
}

public static unsafe class BlockBuilder
{
    public const int MaxInstructions = 192;

    public static Block Build(uint pc, bool thumb)
    {
        var list = new List<DecodedInst>(16);
        uint addr = pc;
        if (!thumb)
        {
            while (true)
            {
                if (addr != pc && CpuCore.Probes is { } pa && pa.Contains(addr)) break; // probes are block starts
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
        long cycles = 0;
        foreach (ref var d in insts.AsSpan()) cycles += CycleCost(in d);
        return new Block { Start = pc, End = addr, Thumb = thumb, Insts = insts, Cycles = cycles, HotAt = Jit.InitialThreshold };
    }

    /// <summary>
    /// Approximate Cortex-A9 cost of one instruction in CPU cycles, including the average share of cache misses
    /// and branch penalties. The emulated CPU is paced by these so game code sees hardware-like execution times
    /// (physics budgets, frame timers, busy-wait loops), whatever the host speed.
    /// </summary>
    public static int CycleCost(in DecodedInst d) => d.Op switch
    {
        // single loads/stores: issue + average L1/L2 miss share
        Op.A32_LdStImm or Op.A32_LdStReg or Op.A32_LdStExtra or Op.T16_LdrLit or Op.T16_LdStReg or Op.T16_LdStImm
            or Op.T16_LdStH or Op.T16_LdStSp or Op.T32_LdSt or Op.Vfp_LdrStrArm or Op.Vfp_LdrStrThumb => 4,
        Op.T32_LdStDual => 5,
        Op.A32_Exclusive or Op.T32_ExclusiveWord or Op.T32_ExclusiveOther or Op.A32_Swp => 8,
        // multiple loads/stores: one cycle per register plus the memory share
        Op.A32_LdmStm or Op.T32_LdmStm => 3 + System.Numerics.BitOperations.PopCount(d.Raw & 0xFFFF),
        Op.T16_Push or Op.T16_Pop => 3 + System.Numerics.BitOperations.PopCount(d.Raw & 0x1FF),
        Op.T16_Stm or Op.T16_Ldm => 3 + System.Numerics.BitOperations.PopCount(d.Raw & 0xFF),
        Op.Vfp_LdmStm => 3 + (int)(d.Raw & 0xFF) / 2,
        Op.Neon_LoadStore => 6,
        // multiplies
        Op.A32_Multiply or Op.A32_HalfMultiply or Op.A32_SignedMultiplyMedia or Op.T32_Mul or Op.A32_Usad8 => 2,
        Op.T32_LongMul => 3,
        // branches: taken-branch and misprediction share
        Op.A32_B or Op.A32_Bl or Op.A32_BlxImm or Op.A32_Bx or Op.A32_BlxReg or Op.T16_B or Op.T16_BCond or Op.T16_BxBlx
            or Op.T16_Cbz or Op.T32_BW or Op.T32_BCondW or Op.T32_Bl or Op.T32_BlxImm or Op.T32_BxjOrSubsPcLr => 3,
        Op.T32_Tbb => 5,
        // VFP (A9: pipelined, 4-5 cycle latency chains) and NEON (64-bit datapath: a Q operation takes two passes)
        Op.Vfp_Arith or Op.Vfp_MovCoreSingle or Op.Vfp_MovCoreScalar or Op.Vfp_MovScalarCore or Op.Vfp_Mov64 or Op.Vfp_VdupCore => 2,
        Op.Vfp_Other => 8, // VDIV / VSQRT / conversions
        Op.Vfp_Vmrs or Op.Vfp_Vmsr => 4,
        Op.Neon_ThreeSame or Op.Neon_ThreeDiff or Op.Neon_Scalar or Op.Neon_Shift or Op.Neon_Misc or Op.Neon_Imm
            or Op.Neon_Vext or Op.Neon_Vtbl or Op.Neon_VdupScalar => (d.Raw & 0x40) != 0 ? 2 : 1, // Q bit
        // system
        Op.A32_Svc or Op.T16_Svc => 20,
        Op.A32_Mrc or Op.A32_Mcr or Op.T32_Mrc or Op.T32_Mcr or Op.A32_Barrier or Op.T32_Barrier => 10,
        Op.Thumb_It or Op.A32_Nop or Op.T16_Nop or Op.T32_Nop => 1,
        _ => 1,
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
            if (b.Start < addr + size && b.End > addr) _blocks.TryRemove(kv.Key, out _);
        }
    }

    public void Clear() => _blocks.Clear();

    public int Count => _blocks.Count;
}
