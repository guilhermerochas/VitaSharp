using VitaSharp.Core.Util;
using Decoder = VitaSharp.Core.Cpu.Decoder;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Code quality of the ARM64 backend on typical loop blocks: host instructions executed per guest instruction (counted
/// in the sandbox while the block runs chained to itself through its link slot) and static code size, with every
/// optimisation of <see cref="Arm64Emitter.A64Opt"/> off, each one alone, and all on. Every run is also compared with
/// the interpreter, so the table doubles as a correctness check of each switch position.
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    /// <summary>Loop bodies: each ends in a conditional branch back to its first instruction; r2 counts iterations.</summary>
    internal static readonly (string Name, bool Thumb, string Words)[] PerfBlocks =
    [
        // ldr r3,[r0],#4; add r4,r4,r3; subs r2,r2,#1; bne loop
        ("arm sum", false, "E4903004,E0844003,E2522001,1AFFFFFB"),
        // add r5,r5,#1; cmp r5,r2; blt loop
        ("arm cmp loop", false, "E2855001,E1550002,BAFFFFFC"),
        // ldr r3,[r0],#4; cmp r3,r4; movgt r4,r3; subs r2,r2,#1; bne loop (running maximum: a GT region)
        ("arm max", false, "E4903004,E1530004,C1A04003,E2522001,1AFFFFFA"),
        // ldmia r1!,{r3-r6}; stmia r0!,{r3-r6}; subs r2,#1; bne loop
        ("thumb memcpy", true, "C978,C078,3A01,D1FB"),
        // ldrb r3,[r1,r5]; strb r3,[r0,r5]; adds r5,#1; cmp r5,r2; blt loop
        ("thumb bytecopy", true, "5D4B,5543,3501,4295,DBFA"),
        // vld1.32 {d0-d1},[r0]!; vmul.f32 q1,q0,q2; vmla.f32 q3,q1,q2; vadd.f32 q4,q3,q1; vst1.32 {d8-d9},[r1]!; subs r2,r2,#1; bne loop
        ("neon mla", false, "F4200A8D,F3002D54,F2026D54,F2068D42,F4018A8D,E2522001,1AFFFFF8"),
        // vqshl.s32 d0,d1,d2; vqshl.s32 d3,d4,d5; subs r2,r2,#1; bne loop (two SIMD callouts in a row)
        ("arm 2 callouts", false, "F2220411,F2253414,E2522001,1AFFFFFB"),
        // vldr s0,[r0]; vmul.f32 s1,s0,s2; vadd.f32 s3,s3,s1; add r0,r0,#4; subs r2,r2,#1; bne loop
        ("vfp dot", false, "ED900A00,EE600A01,EE711AA0,E2800004,E2522001,1AFFFFF9"),
    ];

    static byte[] GuestBytes(bool thumb, string words)
    {
        var bytes = new List<byte>();
        foreach (var w in words.Split(','))
        {
            uint v = Convert.ToUInt32(w, 16);
            if (!thumb) bytes.AddRange(BitConverter.GetBytes(v));
            else if (w.Length > 4) bytes.AddRange([(byte)(v >> 16), (byte)(v >> 24), (byte)v, (byte)(v >> 8)]);
            else bytes.AddRange([(byte)v, (byte)(v >> 8)]);
        }
        return bytes.ToArray();
    }

    static void PerfState(CpuState* s, bool thumb, int iterations)
    {
        *s = default;
        s->MemBase = GuestMemory.Base;
        s->R[0] = DataBase + 0x1000; s->R[1] = DataBase + 0x4000; s->R[2] = (uint)iterations; s->R[5] = 0;
        s->R[13] = DataBase + 0x100; s->R[15] = CodeBase; s->Thumb = thumb ? 1u : 0u;
        s->Fpscr = 0x0300_0000;
        for (int i = 0; i < 32; i++) s->D[i] = 0x3F80_0000_4000_0000UL + (ulong)i * 0x0001_0000_0001_0000UL;
    }

    /// <summary>
    /// Runs <paramref name="iterations"/> loop iterations natively (first run unlinked, then linked to itself) and
    /// returns host instructions per loop iteration in the linked run, static words, and whether the state matched the
    /// interpreter.
    /// </summary>
    (double PerIter, int Words, string Diff) MeasureLoop(Arm64Sandbox sb, bool thumb, string words, int iterations)
    {
        var code = GuestMemory.Span(CodeBase, 0x1000);
        code.Clear();
        GuestBytes(thumb, words).CopyTo(code);
        var b = BlockBuilder.Build(CodeBase, thumb);
        var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
        var info = (NativeCodeInfo)cb.Tag!;
        var s = CpuState.Allocate();
        try
        {
            var data = GuestMemory.Span(DataBase, 0x8000);
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + 3);
            PerfState(s, thumb, iterations);
            while (s->R[15] == CodeBase && s->Exit == ExitReason.None) CpuCore.Interpret(s, b);
            var interp = Take(s, null);

            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + 3);
            PerfState(s, thumb, iterations);
            s->CycleAcc = 0; s->CheckWindow = 1L << 40; s->LinkBreak = 0; s->NBlocks = 0; s->NInstr = 0;
            sb.Run(s, cb.NativeEntry); // one iteration: the exit back to the loop is not linked yet
            Assert.Equal(1, s->NBlocks);
            Assert.True(s->LastExitSlot != 0 && *(nint*)s->LastExitSlot == 0);
            *(nint*)s->LastExitSlot = cb.NativeEntry;
            s->NBlocks = 0; s->NInstr = 0;
            sb.Executed = 0;
            sb.Counting = true;
            sb.Run(s, cb.NativeEntry);
            sb.Counting = false;
            long blocks = s->NBlocks;
            Assert.Equal(iterations - 1, blocks);
            var native = Take(s, null);
            *(nint*)s->LastExitSlot = 0;
            return ((double)sb.Executed / blocks, info.Size / 4, Diff(interp, native));
        }
        finally { CpuState.Free(s); }
    }

    /// <summary>
    /// Compare + branch fusion: every flag-setting form (CMP, CMN, TST, TEQ, ADDS, SUBS, ADCS, SBCS, MOVS with a shift
    /// carry) followed by an instruction under each of the 14 conditions, with operands at the flag edges, against the
    /// interpreter, with fusion on and off. The fused forms must also be shorter.
    /// </summary>
    [Arm64SandboxFact]
    public void Arm64FusedConditionsMatchInterpreter()
    {
        EnsureGuestPages();
        using var arena = new ArenaScope();
        using var sb = Arm64Runner.Create();
        uint[] setters = [0xE1500001, 0xE1700001, 0xE1100001, 0xE1300001, 0xE0903001, 0xE0503001, 0xE0B03001, 0xE0D03001, 0xE1B03080];
        uint[] edge = [0, 1, 2, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFFFF, 0xFFFFFFFE, 0x12345678];
        var rng = new Random(77);
        var old = Arm64Emitter.Opts;
        var s = CpuState.Allocate();
        int compared = 0, shorter = 0;
        var failures = new List<string>();
        try
        {
            foreach (uint setter in setters)
                for (uint cond = 0; cond < 14; cond++)
                {
                    uint[] prog = [setter, cond << 28 | 0x03A02001, (cond ^ 1) << 28 | 0x03A02002, 0xE12FFF1E];
                    var bytes = prog.SelectMany(BitConverter.GetBytes).ToArray();
                    bytes.CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                    var b = BlockBuilder.Build(CodeBase, false);
                    int[] size = new int[2];
                    for (int fused = 0; fused < 2; fused++)
                    {
                        Arm64Emitter.Opts = fused == 1 ? old | Arm64Emitter.A64Opt.FuseBranch : old & ~Arm64Emitter.A64Opt.FuseBranch;
                        var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
                        size[fused] = ((NativeCodeInfo)cb.Tag!).Size;
                        for (int k = 0; k < 24; k++)
                        {
                            var st = new RandomState(rng);
                            st.Regs[0] = edge[rng.Next(edge.Length)];
                            st.Regs[1] = rng.Next(3) == 0 ? (uint)rng.NextInt64() : edge[rng.Next(edge.Length)];
                            st.Flags = (uint)rng.Next(16) << 28;
                            st.Apply(s, false);
                            var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                            st.Apply(s, false);
                            var native = Take(s, Guard(() => RunArm64(sb, s, cb.NativeEntry)));
                            compared++;
                            string d = Diff(interp, native);
                            if (d.Length > 0 && failures.Count < 10) failures.Add($"{setter:X8} cond {cond} fused {fused} r0 {st.Regs[0]:X8} r1 {st.Regs[1]:X8} flags {st.Flags:X8}:{d}");
                        }
                    }
                    if (size[1] < size[0]) shorter++;
                }
        }
        finally { Arm64Emitter.Opts = old; CpuState.Free(s); }
        output.WriteLine($"compared {compared} runs; fused code shorter for {shorter} of {setters.Length * 14} setter/condition pairs");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        // CMP/CMN/ADDS/SUBS/ADCS/SBCS under HI/LS/GT/LE save four instructions (GE/LT save one, which the literal pool's
        // 8-byte alignment can absorb); TST/TEQ/MOVS leave C/V to older flags and are not fused for those conditions
        Assert.True(shorter >= 6 * 4, $"fusion shortened only {shorter} blocks");
    }

    /// <summary>The VFP fast-path mode check (<see cref="Arm64Emitter.A64Opt.FastFpCheck"/>) at every FPSCR bit around
    /// the mode field (LEN, the reserved bit 19, STRIDE, RMode, FZ, DN, AHP, QC and the flags), against the interpreter.</summary>
    [Arm64SandboxFact]
    public void Arm64VfpModeCheckEdges()
    {
        EnsureGuestPages();
        using var arena = new ArenaScope();
        using var sb = Arm64Runner.Create();
        // vadd.f32 s3,s1,s4; vmul.f64 d5,d6,d7; vcmp.f32 s1,s4; b .
        uint[] prog = [0xEE701A82, 0xEE265B07, 0xEEF40A42, 0xEAFFFFFE];
        prog.SelectMany(BitConverter.GetBytes).ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
        var b = BlockBuilder.Build(CodeBase, false);
        var old = Arm64Emitter.Opts;
        var rng = new Random(91);
        var s = CpuState.Allocate();
        var failures = new List<string>();
        try
        {
            foreach (bool fast in new[] { false, true })
            {
                Arm64Emitter.Opts = fast ? old | Arm64Emitter.A64Opt.FastFpCheck : old & ~Arm64Emitter.A64Opt.FastFpCheck;
                var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
                for (int bit = 15; bit <= 31; bit++)
                    for (int k = 0; k < 8; k++)
                    {
                        var st = new RandomState(rng);
                        for (int i = 0; i < 32; i++) st.D[i] = CpuFuzzTests.RandomSpecialSimd(rng);
                        st.Fpscr = 0x0300_0000u ^ (1u << bit);
                        st.Apply(s, false);
                        var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                        st.Apply(s, false);
                        var native = Take(s, Guard(() => RunArm64(sb, s, cb.NativeEntry)));
                        string d = Diff(interp, native);
                        if (d.Length > 0 && failures.Count < 10) failures.Add($"fast {fast} fpscr {st.Fpscr:X8}:{d}");
                    }
            }
        }
        finally { Arm64Emitter.Opts = old; CpuState.Free(s); }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The SIMD register cache (<see cref="Arm64Emitter.A64Opt.VecCache"/>) under stress: long ARM blocks whose NEON,
    /// VFP, VMOV core/S, VLDR/VSTR and conditional VFP instructions all use D0-D7 / S0-S15 (so cached registers are
    /// reused, overwritten through every other path, and reloaded after FpOp handler calls when FPSCR is not in the Vita
    /// mode), against the interpreter with the cache on and off.
    /// </summary>
    static readonly NeonClass[] NeonNoMemory =
        [NeonClass.ThreeSame, NeonClass.ThreeSameFloat, NeonClass.ThreeSameFloat, NeonClass.Scalar, NeonClass.Shift, NeonClass.Misc, NeonClass.Vext, NeonClass.Vtbl, NeonClass.ThreeDiff];

    [Arm64SandboxTheory]
    [InlineData(400, 3101)]
    public void Arm64SimdCacheMatchesInterpreter(int iterations, int seed)
    {
        EnsureGuestPages();
        using var arena = new ArenaScope();
        using var sb = Arm64Runner.Create();
        if (int.TryParse(Environment.GetEnvironmentVariable(EnvVars.FuzzIter), out int itOverride)) iterations = itOverride;
        var rng = new Random(seed);
        var old = Arm64Emitter.Opts;
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0, insts = 0, detailed = 0, withCallouts = 0;
        const uint LowRegs = ~(0x00400000u | 0x00088000u | 0x000000A8u); // D/N/M bits and the top bit of each field: D0-D7, S0-S15
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var code = new List<byte>();
                int n = rng.Next(6, 16);
                for (int k = 0, attempts = 0; k < n && attempts < 400; attempts++)
                {
                    uint r = (uint)rng.NextInt64();
                    uint ins = rng.Next(12) switch
                    {
                        < 4 => RandomNeon(rng, NeonNoMemory[rng.Next(NeonNoMemory.Length)]) & LowRegs,
                        < 7 => ((rng.Next(4) == 0 ? (uint)rng.Next(14) : 14u) << 28) | 0x0E000A00u | (r & 0x00FFF1EF & LowRegs), // VFP data processing
                        7 => 0xEE000A10u | (r & 0x00106000) | 0x1000 | (r & 0x00070080),                                            // VMOV S0-S15 <-> r1/r3/r5/r7
                        8 => 0xED900B00u | (r & 0x0000700F),                                                                        // VLDR d0-d7, [r0, #0-60]
                        9 => 0xED800B00u | (r & 0x0000700F),                                                                        // VSTR
                        // callouts (handler calls, FPCR switched to the host mode): MRS r1/r3/r5/r7, APSR; MSR APSR_nzcvq, r3;
                        // VQSHL/VQRSHL (register), a SIMD-only callout
                        10 => rng.Next(3) == 0 ? 0xE128F003u : 0xE10F1000u | (r & 0x6000),
                        _ => (0xF2000410u | (r & 0x01300100) | (r & 0x00076007 & LowRegs)) & ~0x40u,
                    };
                    var d = Decoder.DecodeArm(ins, CodeBase);
                    if (d.EndsBlock || CpuFuzzTests.IsUnpredictable(d, false)) continue;
                    if (d.Op.ToString().StartsWith("Neon_") && !SimdDefined(ins, false)) continue;
                    code.AddRange(BitConverter.GetBytes(ins));
                    k++;
                }
                if (code.Count == 0) continue;
                code.AddRange([0xFE, 0xFF, 0xFF, 0xEA]);
                code.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                var b = BlockBuilder.Build(CodeBase, false);
                var st = new RandomState(rng);
                NeonState(rng, st);
                st.Regs[0] = DataBase + 0x400;
                st.Fpscr = rng.Next(4) != 0 ? (uint)rng.Next(16) << 28 | 0x0300_0000 : (uint)rng.Next(16) << 28 | (uint)rng.Next(16) << 22;
                st.Apply(s, false);
                var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                if (NativeArm64Compiler.NewFrontend().Translate(b).Callouts > 0) withCallouts++;
                for (int mode = 0; mode < 4; mode++)
                {
                    bool cache = (mode & 1) != 0, lazy = (mode & 2) != 0;
                    var o = old & ~(Arm64Emitter.A64Opt.VecCache | Arm64Emitter.A64Opt.LazyFpcr);
                    Arm64Emitter.Opts = o | (cache ? Arm64Emitter.A64Opt.VecCache : 0) | (lazy ? Arm64Emitter.A64Opt.LazyFpcr : 0);
                    var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
                    st.Apply(s, false);
                    var native = Take(s, Guard(() => RunArm64(sb, s, cb.NativeEntry)));
                    compared++;
                    insts += b.Insts.Length;
                    string diff = Diff(interp, native);
                    if (diff.Length > 0)
                    {
                        if (detailed++ < 4) output.WriteLine($"--- cache {cache} lazy fpcr {lazy} fpscr {st.Fpscr:X8}{diff}\n{DescribeArm64(b, cb)}");
                        Record(failures, $"cache {cache} lazy {lazy}", b, diff);
                    }
                }
            }
        }
        finally { Arm64Emitter.Opts = old; CpuState.Free(s); }
        output.WriteLine($"compared {compared} runs ({insts} instructions, {withCallouts} blocks with callouts); failing kinds {failures.Count}");
        Assert.True(withCallouts > iterations / 10, "too few blocks with callouts");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }

    /// <summary>
    /// Deferred conditional regions (<see cref="Arm64Emitter.A64Opt.DeferRegions"/>) under stress: long ARM blocks of
    /// conditional data processing (flag setters and not, inside and outside the regions), conditional loads/stores,
    /// conditional MRS/MSR APSR (core callouts: a flush and invalidation inside the region) and VQSHL (SIMD-only
    /// callouts), against the interpreter with the option on and off.
    /// </summary>
    [Arm64SandboxTheory]
    [InlineData(600, 3301)]
    public void Arm64DeferredRegionsMatchInterpreter(int iterations, int seed)
    {
        EnsureGuestPages();
        using var arena = new ArenaScope();
        using var sb = Arm64Runner.Create();
        if (int.TryParse(Environment.GetEnvironmentVariable(EnvVars.FuzzIter), out int itOverride)) iterations = itOverride;
        var rng = new Random(seed);
        var old = Arm64Emitter.Opts;
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0, shorter = 0, longer = 0, detailed = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var code = new List<byte>();
                int n = rng.Next(4, 28);
                for (int k = 0, attempts = 0; k < n && attempts < 400; attempts++)
                {
                    uint r = (uint)rng.NextInt64();
                    uint cond = rng.Next(3) == 0 ? 14u : (uint)rng.Next(14);
                    uint ins = rng.Next(10) switch
                    {
                        // data processing, r0-r7, immediate or shifted register
                        < 6 => (cond << 28) | (r & 0x03F77F67) & ~(rng.Next(2) == 0 ? 0x02000000u : 0x10u),
                        6 => (cond << 28) | 0x05800000u | (r & 0x00106000) | 0x1000 | (r & 0x3FC), // LDR/STR r1/r3/r5/r7, [r0, #imm]
                        7 => (cond << 28) | (rng.Next(3) == 0 ? 0x0128F003u : 0x010F1000u | (r & 0x6000)), // MSR APSR_nzcvq, r3 / MRS
                        8 => (cond << 28) | 0x00100000u | (r & 0x00E00000) | (r & 0x00077007) & ~0x8000u, // flag setters, Rd r0-r7
                        _ => (0xF2000410u | (r & 0x01300100) | (r & 0x00076007)) & ~0x40u,  // VQSHL/VQRSHL d, d, d
                    };
                    if (rng.Next(10) == 0) ins = (cond << 28) | 0x0A000000u; // a conditional branch (ends the block)
                    var d = Decoder.DecodeArm(ins, CodeBase);
                    if (d.EndsBlock && d.Op.ToString() != "A32_B" || CpuFuzzTests.IsUnpredictable(d, false)) continue;
                    if (d.Op.ToString().StartsWith("Neon_") && !SimdDefined(ins, false)) continue;
                    code.AddRange(BitConverter.GetBytes(ins));
                    k++;
                    if (d.EndsBlock) break;
                }
                if (code.Count == 0) continue;
                code.AddRange([0xFE, 0xFF, 0xFF, 0xEA]);
                code.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                var b = BlockBuilder.Build(CodeBase, false);
                var st = new RandomState(rng);
                NeonState(rng, st);
                st.Regs[0] = DataBase + 0x400;
                st.Apply(s, false);
                var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                int[] words = new int[2];
                for (int mode = 0; mode < 2; mode++)
                {
                    Arm64Emitter.Opts = mode == 1 ? old | Arm64Emitter.A64Opt.DeferRegions : old & ~Arm64Emitter.A64Opt.DeferRegions;
                    var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
                    words[mode] = ((NativeCodeInfo)cb.Tag!).Size;
                    st.Apply(s, false);
                    var native = Take(s, Guard(() => RunArm64(sb, s, cb.NativeEntry)));
                    compared++;
                    string diff = Diff(interp, native);
                    if (diff.Length > 0)
                    {
                        if (detailed++ < 4) output.WriteLine($"--- deferred {mode == 1}{diff}\n{DescribeArm64(b, cb)}");
                        Record(failures, $"deferred {mode == 1}", b, diff);
                    }
                }
                if (words[1] < words[0]) shorter++;
                if (words[1] > words[0]) longer++;
            }
        }
        finally { Arm64Emitter.Opts = old; CpuState.Free(s); }
        output.WriteLine($"compared {compared} runs, {shorter} blocks shorter, {longer} longer with deferred regions; failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(shorter > 2 * longer && shorter > 0, "deferred regions made code longer");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }

    /// <summary>
    /// <see cref="Arm64Emitter.A64Opt.BackEdgeBreak"/>: with LinkBreak set and a full pacing window left, a linked forward
    /// exit chains on and the linked back edge returns (two blocks); without the option the first exit returns.
    /// The pinned counters must also hand the dispatcher the right block and cycle totals.
    /// </summary>
    [Arm64SandboxFact]
    public void Arm64LinkBreakOnBackEdges()
    {
        if (Arm64Runner.Native) return; // the switches change the trampoline (sandbox only)
        EnsureGuestPages();
        using var arena = new ArenaScope();
        // A: add r0,r0,#1; b B ; B: add r1,r1,#1; b A
        uint[] prog = [0xE2800001, 0xEA000000, 0xE320F000, 0xE2811001, 0xEAFFFFFA];
        prog.SelectMany(BitConverter.GetBytes).ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
        var old = Arm64Emitter.Opts;
        var s = CpuState.Allocate();
        try
        {
            foreach (bool backEdge in new[] { true, false })
            {
                Arm64Emitter.Opts = backEdge ? old | Arm64Emitter.A64Opt.BackEdgeBreak : old & ~Arm64Emitter.A64Opt.BackEdgeBreak;
                using var sb = new Arm64Sandbox();
                var a = BlockBuilder.Build(CodeBase, false);
                var b = BlockBuilder.Build(CodeBase + 0xC, false);
                var ca = NativeArm64Compiler.Instance.Compile(a, sb.Stubs)!;
                var cbb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
                var entries = new Dictionary<uint, nint> { [a.Start] = ca.NativeEntry, [b.Start] = cbb.NativeEntry };
                *s = default; s->MemBase = GuestMemory.Base; s->R[15] = CodeBase;
                for (int link = 0; link < 2; link++) // link A -> B, then B -> A
                {
                    s->CycleAcc = 0; s->CheckWindow = 1L << 40; s->LinkBreak = 0; s->NBlocks = 0; s->NInstr = 0;
                    sb.Run(s, entries[s->R[15]]);
                    Assert.Equal(1, s->NBlocks);
                    *(nint*)s->LastExitSlot = entries[s->R[15]];
                }
                s->R[15] = CodeBase;
                s->CycleAcc = 5; s->CheckWindow = 1L << 40; s->LinkBreak = 1; s->NBlocks = 0; s->NInstr = 0;
                sb.Run(s, ca.NativeEntry);
                Assert.Equal(backEdge ? 2 : 1, s->NBlocks);
                Assert.Equal(5 + a.Cycles + (backEdge ? b.Cycles : 0), s->CycleAcc);
                Assert.Equal(backEdge ? CodeBase : CodeBase + 0xC, s->R[15]);
            }
        }
        finally { Arm64Emitter.Opts = old; CpuState.Free(s); }
    }

    [Arm64SandboxFact]
    public void Arm64CodeQualityTable()
    {
        if (Arm64Runner.Native) return; // counts instructions with a Unicorn hook; the switches also change the trampoline
        EnsureGuestPages();
        using var arena = new ArenaScope();
        var old = Arm64Emitter.Opts;
        var configs = new List<(string Name, Arm64Emitter.A64Opt Opts)> { ("none", Arm64Emitter.A64Opt.None) };
        for (int i = 0; i < Arm64Emitter.OptNames.Length; i++) configs.Add(("+" + Arm64Emitter.OptNames[i], (Arm64Emitter.A64Opt)(1 << i)));
        configs.Add(("all", Arm64Emitter.A64Opt.All));
        var results = new Dictionary<(string, string), (double, int)>();
        var diffs = new List<string>();
        const int Iterations = 40;
        try
        {
            foreach (var (cname, opts) in configs)
            {
                Arm64Emitter.Opts = opts;
                using var sb = new Arm64Sandbox(countInstructions: true); // the trampoline follows the pinned-counter switch
                foreach (var (name, thumb, words) in PerfBlocks)
                {
                    var (perIter, size, diff) = MeasureLoop(sb, thumb, words, Iterations);
                    results[(cname, name)] = (perIter, size);
                    if (diff.Length > 0) diffs.Add($"{cname} {name}:{diff}");
                }
            }
        }
        finally { Arm64Emitter.Opts = old; }
        output.WriteLine($"{"block",-16} {"guest",5} | " + string.Join(" | ", configs.Select(c => $"{c.Name,15}")));
        foreach (var (name, thumb, words) in PerfBlocks)
        {
            int guest = words.Split(',').Length;
            output.WriteLine($"{name,-16} {guest,5} | " + string.Join(" | ", configs.Select(c =>
            {
                var (p, w) = results[(c.Name, name)];
                return $"{p,5:F1} {p / guest,4:F1} {w,4}";
            })));
        }
        output.WriteLine("cells: host instructions per iteration, per guest instruction, static words (code + literal pool)");
        foreach (var (name, thumb, words) in PerfBlocks)
        {
            GuestBytes(thumb, words).CopyTo(GuestMemory.Span(CodeBase, 0x1000));
            var ir = NativeArm64Compiler.NewFrontend().Translate(BlockBuilder.Build(CodeBase, thumb));
            IrPasses.DeadCode(ir);
            output.WriteLine($"--- {name}\n{ir}");
        }
        Assert.True(diffs.Count == 0, string.Join("\n", diffs));
        foreach (var (name, _, _) in PerfBlocks)
            Assert.True(results[("all", name)].Item1 <= results[("none", name)].Item1, $"{name}: optimised code executes more instructions");
    }
}
