using VitaSharp.Core.Util;
using Decoder = VitaSharp.Core.Cpu.Decoder;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Media instructions inline on both native backends (halfword/dual/most-significant-word multiplies, parallel
/// add/subtract with GE, SEL, QADD family, SSAT/USAT(16), PKH, USAD8, UMAAL, SXTB16/UXTB16): blocks of them with
/// operands at the saturation and overflow edges, against the interpreter bit for bit (Q and GE included). Every block
/// must compile without callouts.
/// </summary>
public sealed unsafe partial class NativeJitTests
{
    static readonly Op[] MediaOps =
    [
        Op.A32_HalfMultiply, Op.A32_SignedMultiplyMedia, Op.A32_SatAddSub, Op.A32_Parallel, Op.A32_Pkh, Op.A32_Sat,
        Op.A32_Sat16, Op.A32_Sel, Op.A32_Usad8, Op.A32_Extend, Op.A32_Multiply,
        Op.T32_Mul, Op.T32_LongMul, Op.T32_Parallel, Op.T32_Misc, Op.T32_DPShiftReg, Op.T32_DPPlainImm, Op.T32_Extend,
    ];

    /// <summary>A random media instruction (ARM word, or Thumb-2 as hw1 &lt;&lt; 16 | hw2). Destinations are never PC.</summary>
    static uint RandomMedia(Random rng, bool thumb)
    {
        uint R() => (uint)rng.Next(15);                          // destinations
        uint S() => rng.Next(12) == 0 ? 15u : (uint)rng.Next(15); // sources: PC now and then
        uint r = (uint)rng.NextInt64();
        if (!thumb)
        {
            switch (rng.Next(11))
            {
                case 0: return 0xE1000080 | (r & 3) << 21 | R() << 16 | ((r & 3) == 2 ? R() : S()) << 12 | S() << 8 | (r & 0x60) | S(); // SMLAxy..SMULxy
                case 1:
                {
                    uint op1 = new uint[] { 0, 0, 1, 3, 4, 5, 5 }[rng.Next(7)];
                    uint op2 = op1 is 1 or 3 ? 0 : op1 == 5 ? new uint[] { 0, 1, 6, 7 }[rng.Next(4)] : r >> 8 & 3;
                    uint ra = op1 is 1 or 3 ? 15 : op1 == 4 ? R() : S();
                    return 0xE7000010 | op1 << 20 | R() << 16 | ra << 12 | S() << 8 | op2 << 5 | S();
                }
                case 2: return 0xE1000050 | (r & 3) << 21 | S() << 16 | R() << 12 | S();                       // QADD..QDSUB
                case 3:
                {
                    uint op1 = new uint[] { 1, 2, 3, 5, 6, 7 }[rng.Next(6)], op2 = new uint[] { 0, 1, 2, 3, 4, 7 }[rng.Next(6)];
                    return 0xE6000F10 | op1 << 20 | S() << 16 | R() << 12 | op2 << 5 | S();                    // parallel
                }
                case 4: return 0xE6800010 | S() << 16 | R() << 12 | (r & 0x1F) << 7 | (r >> 9 & 1) << 6 | S(); // PKH
                case 5: return 0xE6A00010 | (r >> 9 & 1) << 22 | (r & 0x1F) << 16 | R() << 12 | (r >> 10 & 0x1F) << 7 | (r >> 15 & 1) << 6 | S(); // SSAT/USAT
                case 6: return 0xE6A00F30 | (r >> 9 & 1) << 22 | (r & 0xF) << 16 | R() << 12 | S();           // SSAT16/USAT16
                case 7: return 0xE6800FB0 | S() << 16 | R() << 12 | S();                                       // SEL
                case 8: return 0xE7800010 | R() << 16 | S() << 12 | S() << 8 | S();                            // USAD8/USADA8
                case 9: return 0xE6800070 | new uint[] { 0, 4 }[rng.Next(2)] << 20 | S() << 16 | R() << 12 | (r & 3) << 10 | S(); // SXTAB16/UXTAB16
                default: return 0xE0400090 | R() << 16 | R() << 12 | S() << 8 | S();                           // UMAAL
            }
        }
        uint hw1, hw2;
        switch (rng.Next(8))
        {
            case 0: hw1 = 0xFB00 | (uint)rng.Next(8) << 4 | S(); hw2 = S() << 12 | R() << 8 | (r & 3) << 4 | S(); break; // multiply
            case 1:
            {
                uint op1 = new uint[] { 4, 5, 6 }[rng.Next(3)];
                uint op2 = op1 == 4 ? new uint[] { 8, 9, 10, 11, 12, 13 }[rng.Next(6)] : op1 == 5 ? 12 + (r & 1) : 6;
                uint lo = R(), hi = R();
                hw1 = 0xFB80 | op1 << 4 | S(); hw2 = lo << 12 | hi << 8 | op2 << 4 | S(); break;      // SMLALxy/SMLALD/SMLSLD/UMAAL
            }
            case 2:
            {
                uint op1 = new uint[] { 0, 1, 2, 4, 5, 6 }[rng.Next(6)];
                hw1 = 0xFA80 | op1 << 4 | S(); hw2 = 0xF000 | R() << 8 | (r & 7) << 4 | S(); break;     // parallel
            }
            case 3: hw1 = 0xFA80 | (r & 1) << 5 | S(); hw2 = 0xF080 | R() << 8 | (r >> 4 & 3) << 4 | S(); break; // QADD family / SEL
            case 4: hw1 = 0xEAC0 | S(); hw2 = (r & 7) << 12 | R() << 8 | (r >> 3 & 3) << 6 | (r >> 5 & 1) << 5 | S(); break; // PKH
            case 5:
            {
                uint op = new uint[] { 0x10, 0x12, 0x18, 0x1A }[rng.Next(4)];
                uint imm3 = rng.Next(3) == 0 ? 0 : r & 7, imm2 = rng.Next(3) == 0 ? 0 : r >> 3 & 3;
                hw1 = 0xF200 | op << 4 | S(); hw2 = imm3 << 12 | R() << 8 | imm2 << 6 | (r >> 5 & 0x1F); break; // SSAT/USAT(16)
            }
            case 6: hw1 = 0xFA00 | new uint[] { 2, 3 }[rng.Next(2)] << 4 | S(); hw2 = 0xF080 | R() << 8 | (r & 3) << 4 | S(); break; // SXTAB16/UXTAB16
            default: hw1 = 0xFB70 | S(); hw2 = S() << 12 | R() << 8 | S(); break;                      // USAD8/USADA8
        }
        return hw1 << 16 | hw2;
    }

    /// <summary>Register values at the halfword/byte/word saturation and overflow edges.</summary>
    static uint SpecialMediaValue(Random rng)
    {
        uint[] halves = [0x0000, 0x0001, 0x7FFF, 0x8000, 0x8001, 0xFFFF, 0xFFFE, 0x4000, 0xC000, 0x00FF, 0xFF00, 0x7F80, 0x807F];
        uint[] words = [0, 1, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFFFF, 0x40000000, 0xC0000000, 0x7F7F7F7F, 0x80808080, 0xFF00FF00, 0x01FF7F80];
        return rng.Next(4) switch
        {
            0 => words[rng.Next(words.Length)],
            1 => (uint)rng.NextInt64(),
            _ => halves[rng.Next(halves.Length)] << 16 | halves[rng.Next(halves.Length)],
        };
    }

    static Block? MediaBlock(Random rng, bool thumb)
    {
        var code = new List<byte>();
        int n = rng.Next(1, 7);
        for (int k = 0, attempts = 0; k < n && attempts < 200; attempts++)
        {
            uint ins = RandomMedia(rng, thumb);
            var d = !thumb ? Decoder.DecodeArm(ins, CodeBase) : Decoder.DecodeThumb32(ins, CodeBase, false, 14);
            if (Array.IndexOf(MediaOps, d.Op) < 0 || d.EndsBlock) continue;
            code.AddRange(!thumb ? BitConverter.GetBytes(ins) : [(byte)(ins >> 16), (byte)(ins >> 24), (byte)ins, (byte)(ins >> 8)]);
            k++;
        }
        if (code.Count == 0) return null;
        code.AddRange(thumb ? [0xFE, 0xE7] : [0xFE, 0xFF, 0xFF, 0xEA]);
        code.ToArray().CopyTo(GuestMemory.Span(CodeBase, 0x1000));
        return BlockBuilder.Build(CodeBase, thumb);
    }

    [NativeTheory]
    [InlineData(false, 3_000, 3101)]
    [InlineData(true, 3_000, 3102)]
    public void NativeMediaQuick(bool thumb, int iterations, int seed) => MediaDifferential(thumb, iterations, seed, arm64: false);

    [Arm64SandboxTheory]
    [InlineData(false, 1_000, 3201)]
    [InlineData(true, 1_000, 3202)]
    public void Arm64MediaQuick(bool thumb, int iterations, int seed) => MediaDifferential(thumb, iterations, seed, arm64: true);

    [NativeTheory]
    [Trait("Category", "Slow")]
    [InlineData(false, 40_000, 3111)]
    [InlineData(true, 40_000, 3112)]
    public void NativeMediaMatchesInterpreter(bool thumb, int iterations, int seed) => MediaDifferential(thumb, iterations, seed, arm64: false);

    [Arm64SandboxTheory]
    [Trait("Category", "Slow")]
    [InlineData(false, 15_000, 3211)]
    [InlineData(true, 15_000, 3212)]
    public void Arm64MediaMatchesInterpreter(bool thumb, int iterations, int seed) => MediaDifferential(thumb, iterations, seed, arm64: true);

    void MediaDifferential(bool thumb, int iterations, int seed, bool arm64)
    {
        using var arena = new ArenaScope();
        EnsureGuestPages();
        using var sb = arm64 ? Arm64Runner.Create() : null;
        if (int.TryParse(Environment.GetEnvironmentVariable(EnvVars.FuzzIter), out int itOverride)) iterations = itOverride;
        bool oldValidate = NativeX64Compiler.ValidateIr;
        NativeX64Compiler.ValidateIr = true;
        if (arm64) Arm64Emitter.VerifyAllocation = true;
        var rng = new Random(seed);
        var s = CpuState.Allocate();
        var failures = new Dictionary<string, (int, string)>();
        int compared = 0, detailed = 0, withCallouts = 0;
        try
        {
            for (int it = 0; it < iterations; it++)
            {
                var b = MediaBlock(rng, thumb);
                if (b == null) continue;
                var st = new RandomState(rng);
                for (int r = 0; r < 15; r++) if (rng.Next(4) != 0) st.Regs[r] = SpecialMediaValue(rng);
                var fe = arm64 ? NativeArm64Compiler.NewFrontend() : new ArmFrontend();
                if (fe.Translate(b).Callouts != 0)
                {
                    if (withCallouts++ < 4) output.WriteLine($"callout in {string.Join(" ", b.Insts.Select(i => $"{i.Raw:X8}:{i.Op}"))}");
                    continue;
                }
                st.Apply(s, thumb);
                var interp = Take(s, Guard(() => CpuCore.Interpret(s, b)));
                var cb = arm64 ? NativeArm64Compiler.Instance.Compile(b, sb!.Stubs)! : NativeX64Compiler.Instance.Compile(b)!;
                st.Apply(s, thumb);
                var native = Take(s, Guard(() => { if (arm64) RunArm64(sb!, s, cb.NativeEntry); else RunNative(s, cb.NativeEntry); }));
                compared++;
                string diff = Diff(interp, native);
                if (interp.Q != native.Q) diff += $" Q: {interp.Q} vs {native.Q}";
                if (interp.GE != native.GE) diff += $" GE: {interp.GE:X} vs {native.GE:X}";
                if (diff.Length > 0)
                {
                    if (detailed++ < 6) output.WriteLine($"--- regs {string.Join(" ", st.Regs.Select(v => v.ToString("X8")))} flags {st.Flags:X8}{diff}\n{(arm64 ? DescribeArm64(b, cb) : DescribeNative(b, cb))}");
                    Record(failures, arm64 ? "arm64" : "native", b, diff);
                }
            }
        }
        finally { CpuState.Free(s); NativeX64Compiler.ValidateIr = oldValidate; }
        output.WriteLine($"compared {compared} media blocks on the {(arm64 ? "ARM64" : "x64")} backend; {withCallouts} with callouts; failing kinds {failures.Count}");
        foreach (var (k, (count, ex)) in failures.OrderByDescending(f => f.Value.Item1).Take(40))
            output.WriteLine($"{k,-40} x{count,-5} {ex}");
        Assert.True(withCallouts == 0, $"{withCallouts} media blocks still contain callouts (see output)");
        Assert.True(failures.Count == 0, $"{failures.Count} kinds of differences (see output)");
    }
}
