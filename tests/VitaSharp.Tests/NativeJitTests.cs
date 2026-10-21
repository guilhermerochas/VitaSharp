using System.Text;
using UcArm = UnicornEngine.Const.Arm;
using Decoder = VitaSharp.Core.Cpu.Decoder;
using UnicornEngine;
using UnicornEngine.Const;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// Differential tests of the compiled CPU backends. Random guest blocks (several instructions, conditional execution,
/// IT blocks, loads/stores, a final branch) run from identical random state in the interpreter, the IL backend and the
/// native backend, and every architectural register, flag, the exit fields and the data memory must match the
/// interpreter bit for bit - including UNPREDICTABLE encodings, since both compiled backends promise the interpreter's
/// behaviour. A second mode also runs the blocks on Unicorn (filtering UNPREDICTABLE encodings like the single
/// instruction fuzz) so the reference itself stays honest for multi-instruction sequences.
/// </summary>
[Collection("guest")]
public sealed unsafe class NativeJitTests(GuestMemoryFixture fx, ITestOutputHelper output)
{
    const uint CodeBase = 0x0010_0000, DataBase = 0x0020_0000, DataSize = 0x10000;

    void EnsureGuestPages()
    {
        if (fx.Memory.FindBlock(CodeBase) == null) fx.Memory.TryAllocAt(CodeBase, 0x1000, "fuzz code");
        if (fx.Memory.FindBlock(DataBase) == null) fx.Memory.TryAllocAt(DataBase, DataSize, "fuzz data");
    }

    /// <summary>VITASHARP_TEST_TRACE=file logs every block before it runs natively (finds the block that crashed the host).</summary>
    static readonly StreamWriter? Trace = Environment.GetEnvironmentVariable("VITASHARP_TEST_TRACE") is { Length: > 0 } p ? new StreamWriter(p) : null;

    // ---- SIMD encodings Unicorn accepts (the interpreter's NEON handlers assume defined encodings)
    static readonly Dictionary<(uint, bool), bool> SimdOk = new();
    static readonly Unicorn?[] SimdUc = new Unicorn?[2];
    static readonly uint[] SimdNext = new uint[2];

    static bool SimdDefined(uint ins, bool thumb)
    {
        if (SimdOk.TryGetValue((ins, thumb), out bool ok)) return ok;
        int k = thumb ? 1 : 0;
        const uint Region = 0x0030_0000, RegionSize = 0x10_0000;
        if (SimdUc[k] == null || SimdNext[k] >= RegionSize - 8)
        {
            SimdUc[k]?.Close();
            var uc = CpuFuzzTests.NewUnicorn(thumb);
            uc.MemMap(Region, RegionSize, Common.UC_PROT_ALL);
            SimdUc[k] = uc;
            SimdNext[k] = 0;
        }
        var u = SimdUc[k]!;
        uint at = Region + SimdNext[k];
        SimdNext[k] += 8; // fresh address: QEMU caches translations
        byte[] code = !thumb ? BitConverter.GetBytes(ins) : [(byte)(ins >> 16), (byte)(ins >> 24), (byte)ins, (byte)(ins >> 8)];
        try
        {
            u.MemWrite(at, code);
            u.RegWrite(UcArm.UC_ARM_REG_CPSR, (long)(0x10u | (thumb ? 0x20u : 0)));
            for (int r = 0; r < 15; r++) u.RegWrite(RegIds[r], DataBase + 0x8000);
            u.EmuStart(at | (thumb ? 1u : 0), at + 4, 0, 1);
            uint pc = (uint)u.RegRead(UcArm.UC_ARM_REG_PC);
            ok = (pc & ~1u) != 0x4 && (pc & ~1u) != 0xFFFF0004;
        }
        catch (Exception) { ok = false; SimdUc[k]!.Close(); SimdUc[k] = null; }
        SimdOk[(ins, thumb)] = ok;
        return ok;
    }

    /// <summary>
    /// Thumb-2 encodings that read PC as a plain operand (UNPREDICTABLE): the interpreter reads the raw R15 (the block
    /// address, or the next address on the last instruction), Unicorn reads PC+4. The single-instruction fuzz never
    /// sees the difference because its instruction is always last.
    /// </summary>
    static bool PcOperandUnpredictable(DecodedInst d, bool thumb)
    {
        if (!thumb) return false;
        uint i = d.Raw, rn = (i >> 16) & 0xF, op = (i >> 21) & 0xF;
        return d.Op switch
        {
            Op.T32_DPShiftReg or Op.T32_DPModImm => rn == 15 && op is not (2 or 3),
            Op.T32_LdStDual => rn == 15 && ((i >> 21) & 1) != 0,
            Op.T32_Mul or Op.T32_LongMul or Op.T32_ShiftReg or Op.T32_Misc or Op.T32_Parallel => rn == 15,
            _ => false,
        };
    }

    static bool Excluded(Op op) => op is Op.A32_Exclusive or Op.T32_ExclusiveWord or Op.T32_ExclusiveOther or Op.A32_Swp;

    /// <summary>Writes a random block at CodeBase and decodes it like the dispatcher would.</summary>
    static Block? RandomBlock(Random rng, bool thumb, int maxInsts, bool forUnicorn)
    {
        var code = new List<byte>();
        uint addr = CodeBase;
        int n = rng.Next(1, maxInsts + 1);
        int accepted = 0;
        for (int attempt = 0; accepted < n && attempt < n * 40; attempt++)
        {
            bool last = accepted == n - 1;
            uint ins;
            byte[] bytes;
            DecodedInst d;
            if (!thumb)
            {
                ins = (uint)rng.NextInt64();
                int c = rng.Next(8);
                if (c < 5) ins = (ins & 0x0FFFFFFF) | 0xE000_0000;          // mostly unconditional
                else if (c < 7) ins = (ins & 0x0FFFFFFF) | ((uint)rng.Next(15) << 28);
                if (rng.Next(3) == 0) ins = (ins & 0xF1FFFFFF) | ((uint)rng.Next(5) << 25); // bias: DP / LDR / STR / LDM
                bytes = BitConverter.GetBytes(ins);
                d = Decoder.DecodeArm(ins, addr);
            }
            else
            {
                ushort hw1 = (ushort)rng.Next(0x10000);
                if (rng.Next(2) == 0) hw1 |= 0xE800;
                if (Decoder.IsThumb32(hw1))
                {
                    ushort hw2 = (ushort)rng.Next(0x10000);
                    ins = ((uint)hw1 << 16) | hw2;
                    bytes = [(byte)hw1, (byte)(hw1 >> 8), (byte)hw2, (byte)(hw2 >> 8)];
                    d = Decoder.DecodeThumb32(ins, addr, false, 14);
                }
                else
                {
                    ins = hw1;
                    bytes = [(byte)hw1, (byte)(hw1 >> 8)];
                    d = Decoder.DecodeThumb16(ins, addr, false, 14);
                }
                if (d.Op == Op.Thumb_It && forUnicorn) continue; // IT blocks are compared by the interpreter mode
            }
            // SIMD: raw random words hit UNDEFINED NEON encodings whose interpreter handlers overrun their scratch
            // buffers; SIMD slots come from the vetted generator of the single-instruction fuzz instead
            bool simd = d.Op.ToString().StartsWith("Neon_") || d.Op.ToString().StartsWith("Vfp_");
            if (!forUnicorn && rng.Next(12) == 0)
            {
                // VFP loads/stores (VLDR/VSTR/VLDM/VSTM/VPUSH/VPOP): cond 110P UDWL Rn Vd 101s imm8, same bits in Thumb
                ins = 0xEC000A00u | ((uint)rng.NextInt64() & 0x01FFF1FFu);
                if (rng.Next(2) == 0) ins = (ins & ~0xFFu) | (uint)rng.Next(1, 9); // short register lists mostly
                bytes = !thumb ? BitConverter.GetBytes(ins) : [(byte)(ins >> 16), (byte)(ins >> 24), (byte)ins, (byte)(ins >> 8)];
                d = !thumb ? Decoder.DecodeArm(ins, addr) : Decoder.DecodeThumb32(ins, addr, false, 14);
                simd = false; // the VFP transfer handlers are safe for any encoding
            }
            else if (simd || rng.Next(10) == 0)
            {
                if (!simd && forUnicorn) { }
                else
                {
                    ins = CpuFuzzTests.RandomSimdInstruction(rng, thumb);
                    bytes = !thumb ? BitConverter.GetBytes(ins) : [(byte)(ins >> 16), (byte)(ins >> 24), (byte)ins, (byte)(ins >> 8)];
                    d = !thumb ? Decoder.DecodeArm(ins, addr) : Decoder.DecodeThumb32(ins, addr, false, 14);
                    if (forUnicorn || CpuFuzzTests.IsUnpredictable(d, thumb) || !SimdDefined(ins, thumb)) continue;
                }
            }
            if (Excluded(d.Op)) continue;
            if (d.Op is Op.A32_Udf or Op.T16_Udf or Op.T32_Udf or Op.A32_Bkpt or Op.T16_Bkpt) continue;
            if (forUnicorn && (d.Op is Op.A32_Svc or Op.T16_Svc or Op.A32_Yield or Op.T16_Yield or Op.T32_Yield or Op.A32_Mrc or Op.A32_Mcr
                or Op.T32_Mrc or Op.T32_Mcr or Op.Vfp_Vmrs or Op.Vfp_Vmsr or Op.A32_Mrs or Op.T32_Mrs or Op.A32_MsrImm or Op.A32_MsrReg
                or Op.T32_Msr or Op.T32_BxjOrSubsPcLr || CpuFuzzTests.IsUnpredictable(d, thumb) || PcOperandUnpredictable(d, thumb))) continue;
            // a block-ending instruction only at the end (the builder would stop there anyway)
            if (d.EndsBlock && !last && rng.Next(4) != 0) continue;
            code.AddRange(bytes);
            accepted++;
            addr += (uint)bytes.Length;
            if (d.EndsBlock || last) break;
        }
        if (code.Count == 0) return null;
        // pad with a branch-to-self so the builder stops inside the written code
        code.AddRange(thumb ? [0xFE, 0xE7] : [0xFE, 0xFF, 0xFF, 0xEA]);
        code.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
        var b = BlockBuilder.Build(CodeBase, thumb);
        // drop the padding branch unless the random code ran into it (it is the natural end otherwise)
        return b;
    }

    struct Snapshot
    {
        public uint[] R; public uint Cpsr, Fpscr, Exit, SvcImm, SvcPc, Q, GE, ExclusiveValid, ExclusiveAddr, TpidrUrw; public ulong[] D; public byte[] Mem;
        public string? Error;
    }

    static Snapshot Take(CpuState* s, string? error)
    {
        var r = new uint[16]; for (int i = 0; i < 16; i++) r[i] = s->R[i];
        var d = new ulong[32]; for (int i = 0; i < 32; i++) d[i] = s->D[i];
        return new Snapshot
        {
            R = r, D = d, Cpsr = s->Cpsr, Fpscr = s->Fpscr, Exit = (uint)s->Exit, SvcImm = s->SvcImm, SvcPc = s->SvcPc, Q = s->Q, GE = s->GE,
            ExclusiveValid = s->ExclusiveValid, ExclusiveAddr = s->ExclusiveAddr, TpidrUrw = s->TpidrUrw,
            Mem = GuestMemory.Span(DataBase, (int)DataSize).ToArray(), Error = error,
        };
    }

    static string Diff(Snapshot a, Snapshot b)
    {
        var sb = new StringBuilder();
        if (a.Error != b.Error) { sb.Append($" error: {a.Error ?? "-"} vs {b.Error ?? "-"}"); return sb.ToString(); }
        if (a.Error != null) return "";
        for (int i = 0; i < 16; i++) if (a.R[i] != b.R[i]) sb.Append($" R{i}: {a.R[i]:X8} vs {b.R[i]:X8}");
        if (a.Cpsr != b.Cpsr) sb.Append($" CPSR: {a.Cpsr:X8} vs {b.Cpsr:X8}");
        if (a.Fpscr != b.Fpscr) sb.Append($" FPSCR: {a.Fpscr:X8} vs {b.Fpscr:X8}");
        if (a.Exit != b.Exit) sb.Append($" Exit: {a.Exit} vs {b.Exit}");
        if (a.Exit != 0 && (a.SvcImm != b.SvcImm || a.SvcPc != b.SvcPc)) sb.Append($" Svc: {a.SvcImm:X}@{a.SvcPc:X8} vs {b.SvcImm:X}@{b.SvcPc:X8}");
        if (a.ExclusiveValid != b.ExclusiveValid || a.ExclusiveAddr != b.ExclusiveAddr) sb.Append(" exclusive monitor");
        if (a.TpidrUrw != b.TpidrUrw) sb.Append($" TPIDRURW: {a.TpidrUrw:X8} vs {b.TpidrUrw:X8}");
        for (int i = 0; i < 32; i++) if (a.D[i] != b.D[i]) sb.Append($" D{i}: {a.D[i]:X16} vs {b.D[i]:X16}");
        for (int i = 0; i < a.Mem.Length; i++)
            if (a.Mem[i] != b.Mem[i]) { sb.Append($" MEM@{DataBase + i:X8}: {a.Mem[i]:X2} vs {b.Mem[i]:X2}"); break; }
        return sb.ToString();
    }

    sealed class RandomState
    {
        public uint[] Regs = new uint[15];
        public uint Flags, Fpscr;
        public ulong[] D = new ulong[32];
        public byte[] Data = new byte[DataSize];

        public RandomState(Random rng)
        {
            for (int r = 0; r < 15; r++)
                Regs[r] = rng.Next(6) == 0 ? (uint)rng.NextInt64() : rng.Next(5) == 0 ? (uint)rng.Next(-64, 64) : DataBase + 0x4000 + (uint)rng.Next(0x4000) * (rng.Next(2) == 0 ? 4u : 1u) % 0x7000;
            Flags = (uint)rng.Next(32) << 27 | (uint)rng.Next(16) << 16;
            Fpscr = (uint)rng.Next(16) << 28 | 0x0300_0000;
            for (int i = 0; i < 32; i++) D[i] = (ulong)rng.NextInt64();
            rng.NextBytes(Data);
        }

        public void Apply(CpuState* s, bool thumb)
        {
            *s = default;
            for (int r = 0; r < 15; r++) s->R[r] = Regs[r];
            s->R[15] = CodeBase;
            s->Cpsr = Flags | 0x10 | (thumb ? 0x20u : 0);
            for (int i = 0; i < 32; i++) s->D[i] = D[i];
            s->Fpscr = Fpscr;
            Data.CopyTo(GuestMemory.Span(DataBase, (int)DataSize));
            // small and small-negative register values address the first and last pages: reset them too
            GuestMemory.Span(0, 0x2000).Clear();
            GuestMemory.Span(0xFFFFE000, 0x2000).Clear();
        }
    }

    static string DescribeNative(Block b, CompiledBlock cb)
    {
        var ir = new ArmFrontend().Translate(b);
        IrPasses.DeadCode(ir);
        var info = (NativeCodeInfo)cb.Tag!;
        return $"guest: {string.Join(" ", b.Insts.Select(i => $"{i.Address:X}:{i.Raw:X8}:{i.Op}{(i.Cond != 14 ? $"/c{i.Cond}" : "")}"))}\nIR:\n{ir}\nx64:\n{info.Disassemble(cb.NativeEntry)}";
    }

    static string? Guard(Action a)
    {
        try { a(); return null; }
        catch (Exception e) { return e.GetType().Name; }
    }

    /// <summary>Runs native code for one block without linking (the dispatcher's per-block contract).</summary>
    static void RunNative(CpuState* s, nint entry)
    {
        s->CycleAcc = 0; s->CheckWindow = 0; s->LinkBreak = 1; s->NBlocks = 0; s->NInstr = 0;
        NativeRuntime.Instance.Run(s, entry);
        if (s->CalloutFault != 0) NativeRuntime.ThrowPendingFault(s);
    }

    /// <summary>
    /// End to end through <see cref="CpuCore.Run"/>: a guest loop with calls, interworking, conditional code and memory
    /// traffic runs once interpreted and once on the native backend (compiled in the background while it runs, then
    /// chained through link slots). Registers, memory and the dispatcher's counters (blocks, instructions, cycles) must
    /// be identical; then the code is patched and invalidated and both runs must pick up the new code.
    /// </summary>
    [Fact]
    public void DispatcherNativeMatchesInterpreter()
    {
        EnsureGuestPages();
        uint[] arm =
        [
            0xE3A00000, // 00 mov r0, #0
            0xE3A01000, // 04 mov r1, #0
            0xE3004000, // 08 movw r4, #0
            0xE3404020, // 0C movt r4, #0x20        (r4 = DataBase)
            0xEB000006, // 10 loop: bl func (0x30)
            0xE7840101, // 14 str r0, [r4, r1, lsl #2]
            0xE2811001, // 18 add r1, r1, #1
            0xE3A0CB0F, // 1C mov r12, #0x3C00     (15360 iterations)
            0xE151000C, // 20 cmp r1, r12
            0xBAFFFFF9, // 24 blt loop
            0xEF000042, // 28 svc #0x42
            0xEAFFFFFE, // 2C b .
            0xE0812081, // 30 func: add r2, r1, r1, lsl #1
            0xE0800002, // 34 add r0, r0, r2       (patched to sub below)
            0xE3100001, // 38 tst r0, #1
            0x12800007, // 3C addne r0, r0, #7
            0xE52DE004, // 40 push {lr}
            0xFA000001, // 44 blx thumbfn (0x50)
            0xE49DE004, // 48 pop {lr}
            0xE12FFF1E, // 4C bx lr
        ];
        ushort[] thumbFn =
        [
            0x1C43,     // 50 adds r3, r0, #1
            0x2B05,     // 52 cmp r3, #5
            0xD800,     // 54 bhi +0 (skip next)
            0x3301,     // 56 adds r3, #1
            0x18C0,     // 58 adds r0, r0, r3
            0x4770,     // 5A bx lr
        ];
        void Load(uint patchAdd)
        {
            var code = GuestMemory.Span(CodeBase, 0x1000);
            code.Clear();
            for (int i = 0; i < arm.Length; i++) BitConverter.GetBytes(i == 13 ? patchAdd : arm[i]).CopyTo(code[(i * 4)..]);
            for (int i = 0; i < thumbFn.Length; i++) BitConverter.GetBytes(thumbFn[i]).CopyTo(code[(0x50 + i * 2)..]);
        }

        long oldRate = CpuCore.InstructionsPerSecond;
        long oldHot = NativeX64Compiler.Instance.HotInstructions;
        var oldChain = Jit.Mode;
        CpuCore.InstructionsPerSecond = 0;
        NativeX64Compiler.Instance.HotInstructions = 1;
        try
        {
            (uint[] Regs, byte[] Mem, long Blocks, long Insts, long Cycles) Run(string backend, uint patch)
            {
                Assert.True(Jit.Select(backend));
                Load(patch);
                var cache = new BlockCache();
                using var core = new CpuCore(cache);
                var s = core.State;
                s->R[13] = DataBase + 0xF000;
                s->R[15] = CodeBase;
                GuestMemory.Span(DataBase, 0xF000).Clear();
                // run a few times: the native backend compiles in the background and links as blocks appear
                for (int pass = 0; pass < 3; pass++)
                {
                    s->R[15] = CodeBase; s->Thumb = 0; s->R[13] = DataBase + 0xF000;
                    var exit = core.Run();
                    Assert.Equal(ExitReason.Svc, exit);
                    Assert.Equal(0x42u, s->SvcImm);
                    Thread.Sleep(50);
                }
                var regs = Enumerable.Range(0, 16).Select(i => s->R[i]).Append(s->Cpsr).ToArray();
                return (regs, GuestMemory.Span(DataBase, 0xF000).ToArray(), core.BlocksExecuted, core.InstructionsExecuted, core.BlockCycles);
            }

            long links0 = NativeRuntime.Links;
            foreach (uint patch in new uint[] { 0xE0800002, 0xE0400002 }) // add r0,r0,r2 / sub r0,r0,r2
            {
                var interp = Run("interp", patch);
                var native = Run("native", patch);
                Assert.Equal(interp.Regs, native.Regs);
                Assert.True(interp.Mem.AsSpan().SequenceEqual(native.Mem), "memory differs");
                Assert.Equal(interp.Blocks, native.Blocks);
                Assert.Equal(interp.Insts, native.Insts);
                Assert.Equal(interp.Cycles, native.Cycles);
                output.WriteLine($"patch {patch:X8}: r0={native.Regs[0]:X8} blocks={native.Blocks} insts={native.Insts} cycles={native.Cycles}");
            }
            output.WriteLine($"links made: {NativeRuntime.Links - links0}; native: {NativeX64Compiler.Instance.Stats}");
            Assert.True(NativeRuntime.Links > links0, "no exit was ever linked");
        }
        finally
        {
            CpuCore.InstructionsPerSecond = oldRate;
            NativeX64Compiler.Instance.HotInstructions = oldHot;
            Jit.Select(oldChain);
        }
    }

    /// <summary>
    /// Inline LDREX/STREX (word) against the interpreter: monitor armed or not, other address, stale generation (an
    /// exclusive store by another core happened), value changed by a plain store. Status, memory, registers and the
    /// monitor fields must match, and the generation must advance the same way.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExclusivesMatchInterpreter(bool thumb)
    {
        EnsureGuestPages();
        // r0 = address: [ldrex r1,[r0]; add r1,r1,#1;] strex r2,r1,[r0]; b .
        uint[] armFull = [0xE1901F9F, 0xE2811001, 0xE1802F91, 0xEAFFFFFE];
        uint[] armStore = [0xE1802F91, 0xEAFFFFFE];
        string[] thumbFull = ["E8501F00", "3101", "E8401200", "E7FE"];  // ldrex r1,[r0]; adds r1,#1; strex r2,r1,[r0]; b .
        string[] thumbStore = ["E8401200", "E7FE"];
        uint addr = DataBase + 0x100;
        var s = CpuState.Allocate();
        try
        {
            for (int scenario = 0; scenario < 6; scenario++)
            {
                bool storeOnly = scenario >= 1;
                var bytes = new List<byte>();
                if (!thumb) foreach (var w in storeOnly ? armStore : armFull) bytes.AddRange(BitConverter.GetBytes(w));
                else foreach (var w in storeOnly ? thumbStore : thumbFull)
                {
                    uint v = Convert.ToUInt32(w, 16);
                    if (w.Length > 4) bytes.AddRange([(byte)(v >> 16), (byte)(v >> 24), (byte)v, (byte)(v >> 8)]); else bytes.AddRange([(byte)v, (byte)(v >> 8)]);
                }
                bytes.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                var b = BlockBuilder.Build(CodeBase, thumb);
                var cb = NativeX64Compiler.Instance.Compile(b)!;
                int genIdx = (int)((addr >> 3) & (VitaSharp.Core.Cpu.Arm.ExclusiveStripes - 1));
                var gens = VitaSharp.Core.Cpu.Arm.ExclusiveGens;

                (Snapshot Snap, uint GenDelta) Run(bool native)
                {
                    *s = default;
                    s->R[0] = addr; s->R[1] = 0x1234; s->R[15] = CodeBase; s->Thumb = thumb ? 1u : 0u;
                    GuestMemory.Write(addr, 41u);
                    uint g0 = gens[genIdx];
                    switch (scenario)
                    {
                        case 1: s->ExclusiveValid = 0; break;                                                         // no LDREX
                        case 2: s->ExclusiveValid = 1; s->ExclusiveAddr = addr + 8; s->ExclusiveGen = g0; s->ExclusiveValue = 41; break; // other address
                        case 3: s->ExclusiveValid = 1; s->ExclusiveAddr = addr; s->ExclusiveGen = g0 - 2; s->ExclusiveValue = 41; break;  // stale generation
                        case 4: s->ExclusiveValid = 1; s->ExclusiveAddr = addr; s->ExclusiveGen = g0; s->ExclusiveValue = 40; break;      // value changed
                        case 5: s->ExclusiveValid = 1; s->ExclusiveAddr = addr; s->ExclusiveGen = g0; s->ExclusiveValue = 41; break;      // armed: succeeds
                    }
                    if (native) RunNative(s, cb.NativeEntry); else CpuCore.Interpret(s, b);
                    return (Take(s, null), gens[genIdx] - g0);
                }
                var i = Run(false);
                var n = Run(true);
                string diff = Diff(i.Snap, n.Snap);
                Assert.True(diff.Length == 0, $"scenario {scenario}: {diff}");
                Assert.Equal(i.GenDelta, n.GenDelta);
                output.WriteLine($"scenario {scenario}: status r2={n.Snap.R[2]} gen +{n.GenDelta}");
            }
        }
        finally { CpuState.Free(s); }
    }

    /// <summary>Four guest threads increment one counter with LDREX/ADD/STREX loops in native code: no update may be lost.</summary>
    [Fact]
    public void ExclusiveIncrementIsAtomicAcrossThreads()
    {
        EnsureGuestPages();
        uint counter = DataBase + 0x200;
        uint[] arm =
        [
            0xE1901F9F, // 00 loop: ldrex r1, [r0]
            0xE2811001, // 04 add r1, r1, #1
            0xE1802F91, // 08 strex r2, r1, [r0]
            0xE3520000, // 0C cmp r2, #0
            0x1AFFFFFA, // 10 bne loop
            0xE2533001, // 14 subs r3, r3, #1
            0x1AFFFFF8, // 18 bne loop
            0xEF000042, // 1C svc #0x42
        ];
        var code = GuestMemory.Span(CodeBase, 0x1000);
        code.Clear();
        for (int i = 0; i < arm.Length; i++) BitConverter.GetBytes(arm[i]).CopyTo(code[(i * 4)..]);
        GuestMemory.Write(counter, 0u);
        long oldRate = CpuCore.InstructionsPerSecond, oldHot = NativeX64Compiler.Instance.HotInstructions;
        var oldMode = Jit.Mode;
        CpuCore.InstructionsPerSecond = 0;
        NativeX64Compiler.Instance.HotInstructions = 1;
        const int Threads = 4, Per = 1_000_000;
        try
        {
            Assert.True(Jit.Select("native"));
            var cache = new BlockCache();
            var threads = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
            {
                using var core = new CpuCore(cache);
                var s = core.State;
                s->R[0] = counter; s->R[3] = Per; s->R[15] = CodeBase;
                while (true) { var e = core.Run(); if (e == ExitReason.Svc) break; }
            })).ToArray();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();
            Assert.Equal((uint)(Threads * Per), GuestMemory.Read<uint>(counter));
            Assert.True(cache.Get(CodeBase, false).NativeEntry != 0, "the loop never ran natively");
        }
        finally
        {
            CpuCore.InstructionsPerSecond = oldRate;
            NativeX64Compiler.Instance.HotInstructions = oldHot;
            Jit.Select(oldMode);
        }
    }

    /// <summary>
    /// Code rewritten under a running native cache (module unload/reload, guest JIT + sceKernelSyncVMDomain):
    /// after BlockCache.Invalidate the linked, compiled blocks of the range must never run again.
    /// </summary>
    [Fact]
    public void InvalidationRetiresLinkedNativeCode()
    {
        EnsureGuestPages();
        // loop: r0 += 3 (patched to r0 += 5), 4096 times, then svc
        uint[] prog(uint addImm) =>
        [
            0xE3A00000, // mov r0, #0
            0xE3A01000, // mov r1, #0
            0xE2800000 | addImm, // loop: add r0, r0, #imm
            0xE2811001, // add r1, r1, #1
            0xE3510A01, // cmp r1, #0x1000
            0xBAFFFFFB, // blt loop
            0xEF000042, // svc #0x42
        ];
        long oldRate = CpuCore.InstructionsPerSecond, oldHot = NativeX64Compiler.Instance.HotInstructions;
        var oldMode = Jit.Mode;
        CpuCore.InstructionsPerSecond = 0;
        NativeX64Compiler.Instance.HotInstructions = 1;
        try
        {
            Assert.True(Jit.Select("native"));
            var cache = new BlockCache();
            using var core = new CpuCore(cache);
            var s = core.State;
            void Load(uint imm) { var c = GuestMemory.Span(CodeBase, 0x1000); c.Clear(); var p = prog(imm); for (int i = 0; i < p.Length; i++) BitConverter.GetBytes(p[i]).CopyTo(c[(i * 4)..]); }
            uint RunOnce() { s->R[15] = CodeBase; s->Thumb = 0; Assert.Equal(ExitReason.Svc, core.Run()); return s->R[0]; }
            Load(3);
            for (int i = 0; i < 5; i++) { Assert.Equal(3u * 0x1000, RunOnce()); Thread.Sleep(30); }
            var loop = cache.Get(CodeBase + 8, false);
            Assert.True(loop.NativeEntry != 0, "loop block was not compiled natively");
            Load(5);
            cache.Invalidate(CodeBase, 0x1000);
            Assert.True(loop.Invalidated);
            for (int i = 0; i < 5; i++) { Assert.Equal(5u * 0x1000, RunOnce()); Thread.Sleep(30); }
        }
        finally
        {
            CpuCore.InstructionsPerSecond = oldRate;
            NativeX64Compiler.Instance.HotInstructions = oldHot;
            Jit.Select(oldMode);
        }
    }

    /// <summary>
    /// A NEON-heavy loop: its native block carries a tier-up budget, gets recompiled by the IL backend while running
    /// and its native code is retired (unlinked). State and dispatcher counters must still match the interpreter.
    /// </summary>
    [Fact]
    public void TierUpToIlMatchesInterpreter()
    {
        EnsureGuestPages();
        uint[] arm =
        [
            0xE3A00000, // 00 mov r0, #0
            0xF2200150, // 04 loop: veor q0, q0, q0  (= vorr? any NEON three-same)
            0xF2222844, // 08 vadd.i32 q1, q1, q2
            0xF2244846, // 0C vadd.i32 q2, q2, q3
            0xF2266848, // 10 vadd.i32 q3, q3, q4
            0xE2800001, // 14 add r0, r0, #1
            0xE3500B02, // 18 cmp r0, #0x800
            0xBAFFFFF8, // 1C blt loop
            0xEF000042, // 20 svc #0x42
        ];
        long oldRate = CpuCore.InstructionsPerSecond, oldHot = NativeX64Compiler.Instance.HotInstructions, oldIl = IlJitCompiler.Instance.HotInstructions;
        var oldMode = Jit.Mode;
        CpuCore.InstructionsPerSecond = 0;
        NativeX64Compiler.Instance.HotInstructions = 1;
        IlJitCompiler.Instance.HotInstructions = 200; // budget = max(64, 200 / 7) executions
        try
        {
            (uint[] Regs, ulong[] D, long Blocks, long Insts, long Cycles) Run(string backend)
            {
                Assert.True(Jit.Select(backend));
                var code = GuestMemory.Span(CodeBase, 0x1000);
                code.Clear();
                for (int i = 0; i < arm.Length; i++) BitConverter.GetBytes(arm[i]).CopyTo(code[(i * 4)..]);
                using var core = new CpuCore(new BlockCache());
                var s = core.State;
                for (int i = 0; i < 32; i++) s->D[i] = 0x0001000200030004ul * (ulong)(i + 1);
                for (int pass = 0; pass < 6; pass++)
                {
                    s->R[15] = CodeBase; s->Thumb = 0;
                    Assert.Equal(ExitReason.Svc, core.Run());
                    Thread.Sleep(40); // let the workers compile
                }
                return (Enumerable.Range(0, 16).Select(i => s->R[i]).Append(s->Cpsr).ToArray(), Enumerable.Range(0, 32).Select(i => s->D[i]).ToArray(),
                    core.BlocksExecuted, core.InstructionsExecuted, core.BlockCycles);
            }
            long tiered0 = NativeRuntime.TieredUp;
            var interp = Run("interp");
            var native = Run("native");
            Assert.Equal(interp.Regs, native.Regs);
            Assert.Equal(interp.D, native.D);
            Assert.Equal(interp.Blocks, native.Blocks);
            Assert.Equal(interp.Insts, native.Insts);
            Assert.Equal(interp.Cycles, native.Cycles);
            output.WriteLine($"tiered up: {NativeRuntime.TieredUp - tiered0}; {Jit.Describe()}");
            Assert.True(NativeRuntime.TieredUp > tiered0, "the NEON loop was never recompiled by the IL backend");
        }
        finally
        {
            CpuCore.InstructionsPerSecond = oldRate;
            NativeX64Compiler.Instance.HotInstructions = oldHot;
            IlJitCompiler.Instance.HotInstructions = oldIl;
            Jit.Select(oldMode);
        }
    }

    /// <summary>Debug aid: VITASHARP_HUNT=T|A:words runs one block in the interpreter from many random states, tracing each.</summary>
    [Fact]
    public void HuntBlock()
    {
        if (Environment.GetEnvironmentVariable("VITASHARP_HUNT") is not { Length: > 2 } spec) return;
        bool thumb = spec[0] == 'T';
        EnsureGuestPages();
        var bytes = new List<byte>();
        foreach (var w in spec[2..].Split(','))
        {
            uint v = Convert.ToUInt32(w, 16);
            if (!thumb) bytes.AddRange(BitConverter.GetBytes(v));
            else if (w.Length > 4) bytes.AddRange([(byte)(v >> 16), (byte)(v >> 24), (byte)v, (byte)(v >> 8)]);
            else bytes.AddRange([(byte)v, (byte)(v >> 8)]);
        }
        var s = CpuState.Allocate();
        if (Environment.GetEnvironmentVariable("VITASHARP_HUNT_NATIVE") == "1")
        {
            // native vs interpreter from many random states: print the first mismatch in full
            bytes.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
            var nb = BlockBuilder.Build(CodeBase, thumb);
            var cb = NativeX64Compiler.Instance.Compile(nb)!;
            var code = GuestMemory.Span(CodeBase, 0x1000).ToArray();
            for (int seed = 0; seed < 20000; seed++)
            {
                var st = new RandomState(new Random(seed));
                Snapshot a = default, p = default; bool stable = false;
                for (int k = 0; k < 4 && !stable; k++)
                {
                    st.Apply(s, thumb); code.CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                    a = Take(s, Guard(() => CpuCore.Interpret(s, nb)));
                    stable = k > 0 && Diff(p, a).Length == 0; p = a;
                }
                if (!stable) continue;
                st.Apply(s, thumb); code.CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                var n = Take(s, Guard(() => RunNative(s, cb.NativeEntry)));
                string d = Diff(a, n);
                if (d.Length == 0) continue;
                output.WriteLine($"seed {seed}:{d}\nregs {string.Join(" ", st.Regs.Select(r => r.ToString("X8")))} flags {st.Flags:X8}\n{DescribeNative(nb, cb)}");
                break;
            }
            CpuState.Free(s);
            return;
        }
        for (int seed = 0; seed < 20000; seed++)
        {
            bytes.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
            var b = BlockBuilder.Build(CodeBase, thumb);
            var st = new RandomState(new Random(seed));
            st.Apply(s, thumb);
            Trace?.WriteLine($"seed {seed} regs {string.Join(" ", st.Regs.Select(r => r.ToString("X8")))}"); Trace?.Flush();
            for (int i = 0; i < b.Insts.Length; i++)
            {
                var one = new Block { Start = b.Insts[i].Address, Thumb = thumb, Insts = [b.Insts[i]], End = b.Insts[i].Address + b.Insts[i].Size };
                Trace?.WriteLine($"  {b.Insts[i]} r=[{string.Join(" ", Enumerable.Range(0, 16).Select(r => s->R[r].ToString("X8")))}]"); Trace?.Flush();
                Guard(() => CpuCore.Interpret(s, one));
            }
        }
        CpuState.Free(s);
    }

    /// <summary>Hand-written blocks with known results (and a printed disassembly for debugging).</summary>
    [Theory]
    [InlineData(false, "E3A00001,E2811005,E0802001,E12FFF1E")] // mov r0,#1; add r1,r1,#5; add r2,r0,r1; bx lr
    [InlineData(false, "E3500000,03A03007,13A03009,E12FFF1E")] // cmp r0,#0; moveq r3,#7; movne r3,#9; bx lr
    [InlineData(false, "E5901004,E5812008,E8BD000F,E12FFF1E")] // ldr r1,[r0,#4]; str r2,[r1,#8]; pop {r0-r3}; bx lr
    [InlineData(true, "2001,1840,4770")]                        // movs r0,#1; adds r0,r0,r1; bx lr
    [InlineData(true, "C927,78DA,E90767C2,F8133981,B2B0,F23508CD,E7FE")]
    [InlineData(false, "D02A1B7C,E457C548,E8D97DAA,E75FBA81,68F3FB5E")]
    public void NativeSmoke(bool thumb, string words)
    {
        EnsureGuestPages();
        X64Emitter.VerifyAllocation = true;
        var bytes = new List<byte>();
        foreach (var w in words.Split(','))
        {
            uint v = Convert.ToUInt32(w, 16);
            if (!thumb) bytes.AddRange(BitConverter.GetBytes(v));
            else if (w.Length > 4) bytes.AddRange([(byte)(v >> 16), (byte)(v >> 24), (byte)v, (byte)(v >> 8)]);
            else bytes.AddRange([(byte)v, (byte)(v >> 8)]);
        }
        bytes.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
        var b = BlockBuilder.Build(CodeBase, thumb);
        var cb = NativeX64Compiler.Instance.Compile(b)!;
        output.WriteLine(DescribeNative(b, cb));
        var s = CpuState.Allocate();
        try
        {
            var st = new RandomState(new Random(5));
            st.Apply(s, thumb);
            s->R[13] = DataBase + 0x100;
            s->R[0] = DataBase + 0x200;
            CpuCore.Interpret(s, b);
            var interp = Take(s, null);
            st.Apply(s, thumb);
            s->R[13] = DataBase + 0x100;
            s->R[0] = DataBase + 0x200;
            RunNative(s, cb.NativeEntry);
            var native = Take(s, null);
            output.WriteLine($"accounting: blocks {s->NBlocks} instr {s->NInstr} cycles {s->CycleAcc} (expected 1/{b.Insts.Length}/{b.Cycles}) slot {s->LastExitSlot:X}");
            Assert.Equal("", Diff(interp, native));
            Assert.Equal(b.Cycles, s->CycleAcc);
        }
        finally { CpuState.Free(s); }
    }

    [Theory]
    [InlineData(false, 60_000, 101)]
    [InlineData(true, 60_000, 102)]
    public void NativeMatchesInterpreter(bool thumb, int iterations, int seed) => Differential(thumb, iterations, seed, withIl: false, spillStress: false);

    /// <summary>Same with only two registers for temps: exercises the spill paths of the register allocator.</summary>
    [Theory]
    [InlineData(false, 15_000, 201)]
    [InlineData(true, 15_000, 202)]
    public void NativeMatchesInterpreterWithSpills(bool thumb, int iterations, int seed) => Differential(thumb, iterations, seed, withIl: false, spillStress: true);

    /// <summary>
    /// VFP blocks with special operands (denormals, values next to the smallest normal, NaNs, infinities): the native
    /// FP fast paths (FTZ/DAZ SSE + default NaN + handler fallback) must reproduce the interpreter bit for bit, in the
    /// Vita FPSCR mode and - through the fallback - in any other mode.
    /// </summary>
    [Theory]
    [InlineData(false, 40_000, 501)]
    [InlineData(true, 40_000, 502)]
    public void NativeVfpMatchesInterpreter(bool thumb, int iterations, int seed)
    {
        EnsureGuestPages();
        if (int.TryParse(Environment.GetEnvironmentVariable("VITASHARP_FUZZ_ITER"), out int itOverride)) iterations = itOverride;
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0, detailed = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                // 1-6 VFP data-processing / transfer instructions (cond AL), then a branch to self
                var code = new List<byte>();
                int n = rng.Next(1, 7);
                for (int k = 0; k < n; k++)
                {
                    uint r = (uint)rng.NextInt64();
                    uint ins = rng.Next(5) == 0 ? 0xEE000A10u | (r & 0x00FFF1EF) : 0xEE000A00u | (r & 0x00FFF1EF);
                    if (rng.Next(3) == 0) ins = (ins & ~0x00B000C0u) | ((uint)rng.Next(8) << 4 & 0x00B00000) | (r & 0x40); // bias: opc1 0..7
                    if (rng.Next(3) == 0)
                    {
                        // NEON three-same (ARM form 1111 001U 0Dsz nnnn dddd AAAA NQMB mmmm), biased to the inline subset
                        uint[] aField = [0x1, 0x8, 0xD, 0xD, 0xD, (uint)rng.Next(16)];
                        uint arm = 0xF2000000u | ((r & 1) << 24) | (r & 0x007FF0FF & ~0xF00u) | (aField[rng.Next(aField.Length)] << 8);
                        if (!SimdDefined(thumb ? (((arm & 0x01000000) != 0 ? 0xFF000000u : 0xEF000000u) | (arm & 0x00FFFFFF)) : arm, thumb)) continue;
                        ins = thumb ? (((arm & 0x01000000) != 0 ? 0xFF000000u : 0xEF000000u) | (arm & 0x00FFFFFF)) : arm;
                    }
                    var d = !thumb ? Decoder.DecodeArm(ins, CodeBase) : Decoder.DecodeThumb32(ins, CodeBase, false, 14);
                    if (d.EndsBlock || CpuFuzzTests.IsUnpredictable(d, thumb)) continue;
                    code.AddRange(!thumb ? BitConverter.GetBytes(ins) : [(byte)(ins >> 16), (byte)(ins >> 24), (byte)ins, (byte)(ins >> 8)]);
                }
                if (code.Count == 0) continue;
                code.AddRange(thumb ? [0xFE, 0xE7] : [0xFE, 0xFF, 0xFF, 0xEA]);
                code.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                var b = BlockBuilder.Build(CodeBase, thumb);
                var st = new RandomState(rng);
                for (int i = 0; i < 32; i++) st.D[i] = CpuFuzzTests.RandomSpecialSimd(rng);
                st.Fpscr = rng.Next(5) != 0 ? (uint)rng.Next(16) << 28 | 0x0300_0000 : (uint)rng.Next(16) << 28 | (uint)rng.Next(16) << 22;

                st.Apply(s, thumb);
                var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                var cb = NativeX64Compiler.Instance.Compile(b)!;
                st.Apply(s, thumb);
                var native = Take(s, Guard(() => RunNative(s, cb.NativeEntry)));
                compared++;
                string diff = Diff(interp, native);
                if (diff.Length > 0)
                {
                    if (detailed++ < 4) output.WriteLine($"--- fpscr {st.Fpscr:X8} D=[{string.Join(" ", st.D.Take(16).Select(v => v.ToString("X16")))}]{diff}\n{DescribeNative(b, cb)}");
                    Record(failures, "native", b, diff);
                }
            }
        }
        finally { CpuState.Free(s); }
        output.WriteLine($"compared {compared} VFP blocks; failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }

    /// <summary>All three backends on the same blocks (the IL backend is slow to compile: fewer iterations).</summary>
    [Theory]
    [InlineData(false, 1_500, 301)]
    [InlineData(true, 1_500, 302)]
    public void AllBackendsMatchInterpreter(bool thumb, int iterations, int seed) => Differential(thumb, iterations, seed, withIl: true, spillStress: false);

    void Differential(bool thumb, int iterations, int seed, bool withIl, bool spillStress)
    {
        EnsureGuestPages();
        bool oldValidate = NativeX64Compiler.ValidateIr;
        NativeX64Compiler.ValidateIr = true;
        X64Emitter.VerifyAllocation = true;
        int oldMax = X64Emitter.MaxTempRegisters;
        if (spillStress) X64Emitter.MaxTempRegisters = 2;
        if (int.TryParse(Environment.GetEnvironmentVariable("VITASHARP_FUZZ_ITER"), out int itOverride)) iterations = itOverride;
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int Count, string Example)>();
        int compared = 0, insts = 0, callouts = 0, unstable = 0, detailed = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var b = RandomBlock(rng, thumb, 12, forUnicorn: false);
                if (b == null) continue;
                var st = new RandomState(rng);

                // Random addresses reach memory outside the restored windows (and the code page holds literals):
                // rerun the interpreter until it reproduces itself, so every run starts from the same memory.
                var codeSnap = GuestMemory.Span(CodeBase, 0x1000).ToArray();
                void Restore() { st.Apply(s, thumb); codeSnap.CopyTo(GuestMemory.Span(CodeBase, 0x1000)); }
                if (Trace != null) { Trace.WriteLine($"{it} {(thumb ? "T" : "A")} {string.Join(",", b.Insts.Select(i => $"{i.Raw:X8}:{i.Op}"))}"); Trace.Flush(); }
                Snapshot interp = default, prev = default;
                bool stable = false;
                for (int k = 0; k < 4 && !stable; k++)
                {
                    Restore();
                    interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                    stable = k > 0 && Diff(prev, interp).Length == 0;
                    prev = interp;
                }
                if (!stable) { unstable++; continue; }

                if (Trace != null) { Trace.WriteLine("  native"); Trace.Flush(); }
                var cb = NativeX64Compiler.Instance.Compile(b);
                Assert.NotNull(cb);
                Restore();
                var native = Take(s, Guard(() => RunNative(s, cb!.NativeEntry)));
                bool accountingOk = native.Error != null || s->NBlocks == 1 && s->NInstr == b.Insts.Length && s->CycleAcc == b.Cycles;
                compared++;
                insts += b.Insts.Length;
                string d = Diff(interp, native);
                if (!accountingOk) d += $" accounting: blocks {s->NBlocks} instr {s->NInstr} cycles {s->CycleAcc} (expected 1/{b.Insts.Length}/{b.Cycles})";
                if (d.Length > 0)
                {
                    if (detailed++ < 6) output.WriteLine($"--- {b}:{d}\n{DescribeNative(b, cb!)}");
                    Record(failures, "native", b, d);
                }

                if (withIl)
                {
                    var il = IlJitCompiler.Instance.Compile(b)!;
                    Restore();
                    var ils = Take(s, Guard(() => il.Managed!(s)));
                    string di = Diff(interp, ils);
                    if (di.Length > 0) Record(failures, "il", b, di);
                }
            }
        }
        finally
        {
            NativeX64Compiler.ValidateIr = oldValidate;
            X64Emitter.MaxTempRegisters = oldMax;
            CpuState.Free(s);
        }
        foreach (var c in ArmFrontend.CalloutCount) callouts += (int)Math.Min(c, int.MaxValue);
        output.WriteLine($"compared {compared} blocks, {insts} instructions (callouts so far {callouts}, {unstable} blocks skipped: memory-dependent); failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Count).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }

    static void Record(Dictionary<string, (int, string)> f, string backend, Block b, string diff)
    {
        // classify by the first instruction's op of the block (the usual culprit in a 1-3 instruction repro)
        string key = backend + ":" + string.Join(",", b.Insts.Select(i => i.Op).Distinct().Take(3));
        string ex = $"[{string.Join(" ", b.Insts.Select(i => $"{i.Raw:X8}{(i.Cond != 14 ? $"/c{i.Cond}" : "")}"))}]:{diff}";
        if (f.TryGetValue(key, out var v)) f[key] = (v.Item1 + 1, v.Item2);
        else f[key] = (1, ex);
    }

    // ------------------------------------------------------------------ against Unicorn

    static readonly int[] RegIds =
    [
        UcArm.UC_ARM_REG_R0, UcArm.UC_ARM_REG_R1, UcArm.UC_ARM_REG_R2, UcArm.UC_ARM_REG_R3, UcArm.UC_ARM_REG_R4,
        UcArm.UC_ARM_REG_R5, UcArm.UC_ARM_REG_R6, UcArm.UC_ARM_REG_R7, UcArm.UC_ARM_REG_R8, UcArm.UC_ARM_REG_R9,
        UcArm.UC_ARM_REG_R10, UcArm.UC_ARM_REG_R11, UcArm.UC_ARM_REG_R12, UcArm.UC_ARM_REG_R13, UcArm.UC_ARM_REG_R14,
    ];

    /// <summary>Native blocks against Unicorn: multi-instruction sequences of non-UNPREDICTABLE integer encodings.</summary>
    [Theory]
    [InlineData(false, 8_000, 401)]
    [InlineData(true, 8_000, 402)]
    public void NativeBlocksMatchUnicorn(bool thumb, int iterations, int seed)
    {
        EnsureGuestPages();
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0, skipped = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var b = RandomBlock(rng, thumb, 6, forUnicorn: true);
                if (b == null) continue;
                // only the random instructions: a block that ran into the padding branch is cut before it
                var st = new RandomState(rng);
                var codeBytes = GuestMemory.Span(CodeBase, 0x1000).ToArray();

                Unicorn? uc = null;
                try
                {
                    uc = new Unicorn(Common.UC_ARCH_ARM, thumb ? Common.UC_MODE_THUMB : Common.UC_MODE_ARM);
                    uc.MemMap(CodeBase, 0x1000, Common.UC_PROT_ALL);
                    uc.MemMap(DataBase, DataSize, Common.UC_PROT_ALL);
                    uc.MemWrite(CodeBase, codeBytes);
                    uc.MemWrite(DataBase, st.Data);
                    uc.RegWrite(UcArm.UC_ARM_REG_CPSR, (long)(st.Flags | 0x10 | (thumb ? 0x20u : 0)));
                    for (int r = 0; r < 15; r++) uc.RegWrite(RegIds[r], st.Regs[r]);
                    uc.EmuStart(CodeBase | (thumb ? 1u : 0), CodeBase + 0x800, 0, b.Insts.Length);
                }
                catch (Exception) { skipped++; uc?.Close(); continue; }

                st.Apply(s, thumb);
                var cb = NativeX64Compiler.Instance.Compile(b)!;
                string? err = Guard(() => RunNative(s, cb.NativeEntry));
                if (err != null) { skipped++; uc.Close(); continue; }
                compared++;
                var sb = new StringBuilder();
                for (int r = 0; r < 15; r++)
                {
                    uint u = (uint)uc.RegRead(RegIds[r]);
                    if (u != s->R[r]) sb.Append($" R{r}: uc={u:X8} us={s->R[r]:X8}");
                }
                uint ucPc = (uint)uc.RegRead(UcArm.UC_ARM_REG_PC), ucCpsr = (uint)uc.RegRead(UcArm.UC_ARM_REG_CPSR);
                bool oddArmTarget = (ucPc & 3) == 2 && (ucCpsr & 0x20) == 0;
                if ((ucPc & ~1u) != s->R[15] && !oddArmTarget) sb.Append($" PC: uc={ucPc:X8} us={s->R[15]:X8}");
                if ((ucCpsr & 0xF80F0020) != (s->Cpsr & 0xF80F0020)) sb.Append($" CPSR: uc={ucCpsr & 0xF80F0020:X8} us={s->Cpsr & 0xF80F0020:X8}");
                var ucData = new byte[DataSize];
                uc.MemRead(DataBase, ucData);
                var ours = GuestMemory.Span(DataBase, (int)DataSize);
                for (int i = 0; i < DataSize; i++) if (ucData[i] != ours[i]) { sb.Append($" MEM@{DataBase + i:X8}: uc={ucData[i]:X2} us={ours[i]:X2}"); break; }
                uc.Close();
                if (sb.Length > 0) Record(failures, "unicorn", b, sb.ToString());
            }
        }
        finally { CpuState.Free(s); }
        output.WriteLine($"compared {compared} blocks against Unicorn, skipped {skipped}; failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences against Unicorn (see output)");
    }
}
