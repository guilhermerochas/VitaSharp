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
