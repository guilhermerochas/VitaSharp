using VitaSharp.Core.Util;
using Decoder = VitaSharp.Core.Cpu.Decoder;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The ARM64 backend on an x64 host: blocks are compiled by <see cref="NativeArm64Compiler"/> and the AArch64 code runs
/// in <see cref="Arm64Sandbox"/> (Unicorn) on the real CpuState and guest memory. The same random guest blocks,
/// states and snapshots as the x64 differential tests: registers, flags, FPSCR (QC included), the SIMD register file,
/// the exclusive monitor, memory and the per-block accounting must match the interpreter bit for bit.
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    static string DescribeArm64(Block b, CompiledBlock cb)
    {
        var ir = NativeArm64Compiler.NewFrontend().Translate(b);
        IrPasses.DeadCode(ir);
        var info = (NativeCodeInfo)cb.Tag!;
        return $"guest: {string.Join(" ", b.Insts.Select(i => $"{i.Address:X}:{i.Raw:X8}:{i.Op}{(i.Cond != 14 ? $"/c{i.Cond}" : "")}"))}\nIR:\n{ir}\narm64 (entry {cb.NativeEntry:X}):\n{info.Disassemble(cb.NativeEntry)}";
    }

    static void RunArm64(Arm64Sandbox sb, CpuState* s, nint entry)
    {
        s->CycleAcc = 0; s->CheckWindow = 0; s->LinkBreak = 1; s->NBlocks = 0; s->NInstr = 0;
        sb.Run(s, entry);
        if (s->CalloutFault != 0) NativeRuntime.ThrowPendingFault(s);
    }

    [Arm64SandboxTheory]
    [InlineData(false, "E3A00001,E2811005,E0802001,E12FFF1E")] // mov r0,#1; add r1,r1,#5; add r2,r0,r1; bx lr
    [InlineData(false, "E3500000,03A03007,13A03009,E12FFF1E")] // cmp r0,#0; moveq r3,#7; movne r3,#9; bx lr
    [InlineData(false, "E5901004,E5812008,E8BD000F,E12FFF1E")] // ldr r1,[r0,#4]; str r2,[r1,#8]; pop {r0-r3}; bx lr
    [InlineData(true, "2001,1840,4770")]                        // movs r0,#1; adds r0,r0,r1; bx lr
    [InlineData(true, "C927,78DA,E90767C2,F8133981,B2B0,F23508CD,E7FE")]
    [InlineData(false, "D02A1B7C,E457C548,E8D97DAA,E75FBA81,68F3FB5E")]
    [InlineData(false, "E0D12113,E1B02251,E0A33004,C0534775,EAFFFFFE")]  // register shifts with carry, ADC, SUBS gt
    [InlineData(false, "E7F0F0F0")]                                      // UDF: exit raised inline
    public void Arm64Smoke(bool thumb, string words)
    {
        EnsureGuestPages();
        using var sb = new Arm64Sandbox();
        Arm64Emitter.VerifyAllocation = true;
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
        var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
        output.WriteLine(DescribeArm64(b, cb));
        var s = CpuState.Allocate();
        try
        {
            var st = new RandomState(new Random(5));
            void Init() { st.Apply(s, thumb); s->R[13] = DataBase + 0x100; s->R[0] = DataBase + 0x200; }
            Init();
            var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
            Init();
            var native = Take(s, Guard(() => RunArm64(sb, s, cb.NativeEntry)));
            Assert.Equal("", Diff(interp, native));
            Assert.Equal(b.Cycles, s->CycleAcc);
            Assert.Equal(1, s->NBlocks);
            Assert.Equal(b.Insts.Length, s->NInstr);
        }
        finally { CpuState.Free(s); }
    }

    [Arm64SandboxTheory]
    [InlineData(false, 1_500, 2101, false)]
    [InlineData(true, 1_500, 2102, false)]
    [InlineData(false, 500, 2103, true)]
    [InlineData(true, 500, 2104, true)]
    public void Arm64BlocksMatchInterpreterQuick(bool thumb, int iterations, int seed, bool spillStress) => Arm64Differential(thumb, iterations, seed, spillStress);

    [Arm64SandboxTheory]
    [Trait("Category", "Slow")]
    [InlineData(false, 30_000, 2201, false)]
    [InlineData(true, 30_000, 2202, false)]
    [InlineData(false, 8_000, 2203, true)]
    [InlineData(true, 8_000, 2204, true)]
    public void Arm64BlocksMatchInterpreter(bool thumb, int iterations, int seed, bool spillStress) => Arm64Differential(thumb, iterations, seed, spillStress);

    void Arm64Differential(bool thumb, int iterations, int seed, bool spillStress)
    {
        EnsureGuestPages();
        using var sb = new Arm64Sandbox();
        bool oldValidate = NativeX64Compiler.ValidateIr;
        NativeX64Compiler.ValidateIr = true;
        Arm64Emitter.VerifyAllocation = true;
        int oldMax = Arm64Emitter.MaxTempRegisters;
        if (spillStress) Arm64Emitter.MaxTempRegisters = 2;
        if (int.TryParse(Environment.GetEnvironmentVariable(EnvVars.FuzzIter), out int itOverride)) iterations = itOverride;
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int Count, string Example)>();
        int compared = 0, insts = 0, unstable = 0, detailed = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var b = RandomBlock(rng, thumb, 12, forUnicorn: false);
                if (b == null) continue;
                var st = new RandomState(rng);
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
                var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs);
                Assert.NotNull(cb);
                Restore();
                var native = Take(s, Guard(() => RunArm64(sb, s, cb!.NativeEntry)));
                bool accountingOk = native.Error != null || s->NBlocks == 1 && s->NInstr == b.Insts.Length && s->CycleAcc == b.Cycles;
                compared++;
                insts += b.Insts.Length;
                string d = Diff(interp, native);
                if (native.Error == "InvalidOperationException" && interp.Error == null) d += " sandbox fault";
                if (!accountingOk) d += $" accounting: blocks {s->NBlocks} instr {s->NInstr} cycles {s->CycleAcc} (expected 1/{b.Insts.Length}/{b.Cycles})";
                if (d.Length > 0)
                {
                    if (detailed++ < 6)
                    {
                        string why = "";
                        try { Restore(); RunArm64(sb, s, cb!.NativeEntry); } catch (Exception e) { why = e.Message; }
                        output.WriteLine($"--- {b}:{d} {why}\nregs {string.Join(" ", st.Regs.Select(r => r.ToString("X8")))} flags {st.Flags:X8} fpscr {st.Fpscr:X8}\n{DescribeArm64(b, cb!)}");
                    }
                    Record(failures, "arm64", b, d);
                }
            }
        }
        finally
        {
            NativeX64Compiler.ValidateIr = oldValidate;
            Arm64Emitter.MaxTempRegisters = oldMax;
            CpuState.Free(s);
        }
        output.WriteLine($"compared {compared} blocks, {insts} instructions on the ARM64 backend ({unstable} skipped: memory-dependent), {sb.Instructions} callouts; failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Count).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }

    /// <summary>VFP blocks with special operands, Vita FPSCR mode mostly (fast paths) and other modes (handler).</summary>
    [Arm64SandboxTheory]
    [InlineData(false, 1_500, 2501)]
    [InlineData(true, 1_000, 2502)]
    public void Arm64VfpQuick(bool thumb, int iterations, int seed) => Arm64VfpMatchesInterpreter(thumb, iterations, seed);

    [Arm64SandboxTheory]
    [Trait("Category", "Slow")]
    [InlineData(false, 30_000, 2511)]
    [InlineData(true, 20_000, 2512)]
    public void Arm64VfpMatchesInterpreter(bool thumb, int iterations, int seed)
    {
        EnsureGuestPages();
        using var sb = new Arm64Sandbox();
        if (int.TryParse(Environment.GetEnvironmentVariable(EnvVars.FuzzIter), out int itOverride)) iterations = itOverride;
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0, detailed = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var code = new List<byte>();
                int n = rng.Next(1, 7);
                for (int k = 0; k < n; k++)
                {
                    uint r = (uint)rng.NextInt64();
                    uint ins = rng.Next(5) == 0 ? 0xEE000A10u | (r & 0x00FFF1EF) : 0xEE000A00u | (r & 0x00FFF1EF);
                    if (rng.Next(3) == 0) ins = (ins & ~0x00B000C0u) | ((uint)rng.Next(8) << 4 & 0x00B00000) | (r & 0x40);
                    if (thumb) ins = (ins & 0x00FFFFFF) | 0xEE000000;
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
                var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
                st.Apply(s, thumb);
                var native = Take(s, Guard(() => RunArm64(sb, s, cb.NativeEntry)));
                compared++;
                string diff = Diff(interp, native);
                if (diff.Length > 0)
                {
                    if (detailed++ < 4) output.WriteLine($"--- fpscr {st.Fpscr:X8} D=[{string.Join(" ", st.D.Take(16).Select(v => v.ToString("X16")))}]{diff}\n{DescribeArm64(b, cb)}");
                    Record(failures, "arm64", b, diff);
                }
            }
        }
        finally { CpuState.Free(s); }
        output.WriteLine($"compared {compared} VFP blocks on the ARM64 backend; failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }

    [Arm64SandboxTheory]
    [InlineData(false, NeonClass.ThreeSame, 150, 2601)]
    [InlineData(true, NeonClass.ThreeSame, 150, 2602)]
    [InlineData(false, NeonClass.ThreeSameFloat, 150, 2603)]
    [InlineData(false, NeonClass.Scalar, 150, 2604)]
    [InlineData(false, NeonClass.Shift, 150, 2605)]
    [InlineData(true, NeonClass.Shift, 150, 2606)]
    [InlineData(false, NeonClass.Misc, 150, 2607)]
    [InlineData(true, NeonClass.Misc, 150, 2608)]
    [InlineData(false, NeonClass.Vext, 150, 2609)]
    [InlineData(false, NeonClass.Vtbl, 150, 2610)]
    [InlineData(false, NeonClass.LoadStore, 150, 2611)]
    [InlineData(false, NeonClass.Any, 150, 2613)]
    [InlineData(true, NeonClass.Any, 150, 2614)]
    public void Arm64NeonQuick(bool thumb, NeonClass cls, int iterations, int seed) => Arm64NeonMatchesInterpreter(thumb, cls, iterations, seed);

    [Arm64SandboxTheory]
    [Trait("Category", "Slow")]
    [InlineData(false, NeonClass.ThreeSame, 6_000, 2701)]
    [InlineData(true, NeonClass.ThreeSame, 3_000, 2702)]
    [InlineData(false, NeonClass.ThreeSameFloat, 6_000, 2703)]
    [InlineData(false, NeonClass.Scalar, 4_000, 2704)]
    [InlineData(false, NeonClass.Shift, 6_000, 2705)]
    [InlineData(true, NeonClass.Shift, 3_000, 2706)]
    [InlineData(false, NeonClass.Misc, 6_000, 2707)]
    [InlineData(true, NeonClass.Misc, 3_000, 2708)]
    [InlineData(false, NeonClass.Vext, 2_000, 2709)]
    [InlineData(false, NeonClass.Vtbl, 2_000, 2710)]
    [InlineData(false, NeonClass.LoadStore, 6_000, 2711)]
    [InlineData(false, NeonClass.Any, 6_000, 2713)]
    [InlineData(true, NeonClass.Any, 6_000, 2714)]
    public void Arm64NeonMatchesInterpreter(bool thumb, NeonClass cls, int iterations, int seed)
    {
        EnsureGuestPages();
        using var sb = new Arm64Sandbox();
        if (int.TryParse(Environment.GetEnvironmentVariable(EnvVars.FuzzIter), out int itOverride)) iterations = itOverride;
        bool oldValidate = NativeX64Compiler.ValidateIr;
        NativeX64Compiler.ValidateIr = true;
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0, detailed = 0, inlined = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var b = NeonBlock(rng, thumb, cls);
                if (b == null) continue;
                var st = new RandomState(rng);
                NeonState(rng, st);
                st.Apply(s, thumb);
                var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
                if (NativeArm64Compiler.NewFrontend().Translate(b).Callouts == 0) inlined++;
                st.Apply(s, thumb);
                var native = Take(s, Guard(() => RunArm64(sb, s, cb.NativeEntry)));
                compared++;
                string diff = Diff(interp, native);
                if (diff.Length > 0)
                {
                    if (detailed++ < 6) output.WriteLine($"--- fpscr {st.Fpscr:X8} D=[{string.Join(" ", st.D.Select(v => v.ToString("X16")))}]{diff}\n{DescribeArm64(b, cb)}");
                    Record(failures, "arm64", b, diff);
                }
            }
        }
        finally { CpuState.Free(s); NativeX64Compiler.ValidateIr = oldValidate; }
        output.WriteLine($"compared {compared} NEON blocks on the ARM64 backend ({inlined} fully inline); failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }

    /// <summary>LDREX/STREX and LDREXD/STREXD monitor scenarios (same as the x64 tests), inline on ARM64.</summary>
    [Arm64SandboxTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Arm64ExclusivesMatchInterpreter(bool thumb, bool dual)
    {
        EnsureGuestPages();
        using var sb = new Arm64Sandbox();
        uint[] armFull = dual ? [0xE1B02F9F, 0xE2922001, 0xE1A04F92, 0xEAFFFFFE] : [0xE1901F9F, 0xE2811001, 0xE1802F91, 0xEAFFFFFE];
        uint[] armStore = dual ? [0xE1A04F92, 0xEAFFFFFE] : [0xE1802F91, 0xEAFFFFFE];
        string[] thumbFull = dual ? ["E8D0237F", "3201", "E8C02374", "E7FE"] : ["E8501F00", "3101", "E8401200", "E7FE"];
        string[] thumbStore = dual ? ["E8C02374", "E7FE"] : ["E8401200", "E7FE"];
        uint addr = DataBase + 0x100;
        ulong mem0 = dual ? 41 | (7ul << 32) : 41;
        var s = CpuState.Allocate();
        try
        {
            for (int scenario = 0; scenario < 7; scenario++)
            {
                bool storeOnly = scenario >= 1;
                uint at = scenario == 6 ? addr + 2 : addr; // unaligned: the plain-store path
                var bytes = new List<byte>();
                if (!thumb) foreach (var w in storeOnly ? armStore : armFull) bytes.AddRange(BitConverter.GetBytes(w));
                else foreach (var w in storeOnly ? thumbStore : thumbFull)
                {
                    uint v = Convert.ToUInt32(w, 16);
                    if (w.Length > 4) bytes.AddRange([(byte)(v >> 16), (byte)(v >> 24), (byte)v, (byte)(v >> 8)]); else bytes.AddRange([(byte)v, (byte)(v >> 8)]);
                }
                bytes.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
                var b = BlockBuilder.Build(CodeBase, thumb);
                var cb = NativeArm64Compiler.Instance.Compile(b, sb.Stubs)!;
                Assert.Equal(0, NativeArm64Compiler.NewFrontend().Translate(b).Callouts);
                int genIdx = (int)((at >> 3) & (VitaSharp.Core.Cpu.Arm.ExclusiveStripes - 1));
                var gens = VitaSharp.Core.Cpu.Arm.ExclusiveGens;

                (Snapshot Snap, uint GenDelta) Run(bool native)
                {
                    *s = default;
                    s->R[0] = at; s->R[1] = 0x1234; s->R[2] = 0x1234; s->R[3] = 0x5678; s->R[4] = 0x99; s->R[15] = CodeBase; s->Thumb = thumb ? 1u : 0u;
                    GuestMemory.Write(at, unchecked((uint)mem0)); GuestMemory.Write(at + 4, (uint)(mem0 >> 32));
                    uint g0 = gens[genIdx];
                    switch (scenario)
                    {
                        case 1: s->ExclusiveValid = 0; break;
                        case 2: s->ExclusiveValid = 1; s->ExclusiveAddr = at + 8; s->ExclusiveGen = g0; s->ExclusiveValue = mem0; break;
                        case 3: s->ExclusiveValid = 1; s->ExclusiveAddr = at; s->ExclusiveGen = g0 - 2; s->ExclusiveValue = mem0; break;
                        case 4: s->ExclusiveValid = 1; s->ExclusiveAddr = at; s->ExclusiveGen = g0; s->ExclusiveValue = mem0 ^ (dual ? 1ul << 40 : 1); break;
                        case 5: case 6: s->ExclusiveValid = 1; s->ExclusiveAddr = at; s->ExclusiveGen = g0; s->ExclusiveValue = mem0; break;
                    }
                    if (native) RunArm64(sb, s, cb.NativeEntry); else CpuCore.Interpret(s, b);
                    return (Take(s, null), gens[genIdx] - g0);
                }
                var i = Run(false);
                var n = Run(true);
                string diff = Diff(i.Snap, n.Snap);
                Assert.True(diff.Length == 0, $"scenario {scenario}: {diff}");
                Assert.Equal(i.GenDelta, n.GenDelta);
                output.WriteLine($"scenario {scenario}: status {(dual ? n.Snap.R[4] : n.Snap.R[2])} gen +{n.GenDelta}");
            }
        }
        finally { CpuState.Free(s); }
    }

    /// <summary>
    /// Chained execution inside generated code: a loop block linked to itself through its link slot runs until the
    /// pacing window or the loop ends, and an indirect exit (BX LR) reaches its target through the dispatch table.
    /// Counters and registers must equal running the blocks one at a time in the interpreter.
    /// </summary>
    [Arm64SandboxFact]
    public void Arm64LinksAndDispatchTable()
    {
        EnsureGuestPages();
        using var sb = new Arm64Sandbox();
        uint[] prog =
        [
            0xE2800001, // 00 loop: add r0, r0, #1
            0xE3500064, // 04 cmp r0, #100
            0xBAFFFFFC, // 08 blt loop
            0xE12FFF1E, // 0C bx lr
            0xE3A01007, // 10 target: mov r1, #7
            0xEF000042, // 14 svc #0x42
        ];
        var code = GuestMemory.Span(CodeBase, 0x1000);
        code.Clear();
        for (int i = 0; i < prog.Length; i++) BitConverter.GetBytes(prog[i]).CopyTo(code[(i * 4)..]);
        var loop = BlockBuilder.Build(CodeBase, false);
        var tail = BlockBuilder.Build(CodeBase + 0xC, false);
        var target = BlockBuilder.Build(CodeBase + 0x10, false);
        var cLoop = NativeArm64Compiler.Instance.Compile(loop, sb.Stubs)!;
        var cTail = NativeArm64Compiler.Instance.Compile(tail, sb.Stubs)!;
        var cTarget = NativeArm64Compiler.Instance.Compile(target, sb.Stubs)!;
        var s = CpuState.Allocate();
        try
        {
            // interpreter reference: block by block
            *s = default; s->R[14] = CodeBase + 0x10; s->R[15] = CodeBase;
            long blocks = 0, cycles = 0;
            var all = new[] { loop, tail, target };
            while (s->Exit == ExitReason.None)
            {
                var b = all.First(x => x.Start == s->R[15]);
                CpuCore.Interpret(s, b); blocks++; cycles += b.Cycles;
            }
            uint r0 = s->R[0], r1 = s->R[1];

            // native, driven like CpuCore.Run: run, then link the static exit that returned (if its slot is empty)
            sb.Publish(target.Start, false, cTarget.NativeEntry);
            var entries = new Dictionary<uint, nint> { [loop.Start] = cLoop.NativeEntry, [tail.Start] = cTail.NativeEntry, [target.Start] = cTarget.NativeEntry };
            *s = default; s->R[14] = CodeBase + 0x10; s->R[15] = CodeBase;
            long nb = 0, cyc = 0;
            int runs = 0;
            while (s->Exit == ExitReason.None)
            {
                s->CycleAcc = 0; s->CheckWindow = 1L << 40; s->LinkBreak = 0; s->NBlocks = 0; s->NInstr = 0;
                sb.Run(s, entries[s->R[15]]);
                runs++;
                nb += s->NBlocks; cyc += s->CycleAcc;
                if (s->LastExitSlot != 0 && *(nint*)s->LastExitSlot == 0) *(nint*)s->LastExitSlot = entries[s->R[15]];
            }
            Assert.True(runs <= 3, $"{runs} trips through the dispatcher: chaining did not happen");
            output.WriteLine($"interpreter: {blocks} blocks; native: {nb} blocks");
            Assert.Equal(r0, s->R[0]);
            Assert.Equal(r1, s->R[1]);
            Assert.Equal(ExitReason.Svc, s->Exit);
            Assert.Equal(blocks, nb);
            Assert.Equal(cycles, cyc);
        }
        finally { CpuState.Free(s); }
    }

    /// <summary>The generated cache-maintenance helper (Linux/Android ARM64 __clear_cache) runs and returns.</summary>
    [Arm64SandboxFact]
    public void Arm64CacheFlushHelperRuns()
    {
        using var sb = new Arm64Sandbox();
        var code = NativeRuntime.Instance.Code;
        byte* helper = code.Place(_ => Arm64Emitter.EmitCacheFlush());
        sb.Call((ulong)helper, (ulong)code.ExecBase, 4096);
        Assert.Equal(Arm64Sandbox.ReturnStub, sb.Pc);
    }
}
