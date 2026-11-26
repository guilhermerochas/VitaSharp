using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// VFP short-vector mode (FPSCR LEN/STRIDE) as compiled code: a block that runs in the mode is expanded into the
/// scalar element instructions (<see cref="BlockBuilder.ForVectorMode"/>) and compiled by the native backends instead
/// of being interpreted. Dead Space (an Android port through so_loader) mixes its audio in this mode; interpreted, a mix
/// round took 20-30 ms instead of the console's few, the port's audio thread polled every 11 ms while the mixer was
/// still busy and the port's pthread semaphores kept a stale unit, so its audio thread stopped for good after a few
/// grains. Every expansion is checked against the interpreter's vector execution (<see cref="Vfp.VectorOp"/>) over the
/// LEN/STRIDE matrix, the register banks (scalar bank 0 destination, bank 0 scalar operand, wrapping inside a bank),
/// singles and doubles and every vector-capable operation.
/// </summary>
[Collection("guest")]
public sealed unsafe class VfpShortVectorJitTests(GuestMemoryFixture fx)
{
    private const uint CodeBase = 0x0010_0000;
    private const uint BaseFpscr = 0x0300_0000; // DN + FZ (the Vita runtime's mode)
    private const uint Svc = 0xEF000000;

    /// <summary>Vector-capable instructions (ARM encodings, condition AL) and what they exercise.</summary>
    public static readonly TheoryData<uint, string> Ops = new()
    {
        { 0xEE384A0C, "vadd.f32 s8, s16, s24" },
        { 0xEE384A4C, "vsub.f32 s8, s16, s24" },
        { 0xEE284A0C, "vmul.f32 s8, s16, s24" },
        { 0xEE284A00, "vmul.f32 s8, s16, s0 (bank 0 Vm: scalar for every element)" },
        { 0xEE084A0C, "vmla.f32 s8, s16, s24" },
        { 0xEE884A0C, "vdiv.f32 s8, s16, s24" },
        { 0xEE380A0C, "vadd.f32 s0, s16, s24 (bank 0 destination: scalar)" },
        { 0xEEB07A48, "vmov.f32 s14, s16 (wraps inside the bank)" },
        { 0xEEB04AC8, "vabs.f32 s8, s16" },
        { 0xEEB14A48, "vneg.f32 s8, s16" },
        { 0xEEB14AC8, "vsqrt.f32 s8, s16" },
        { 0xEEB74A00, "vmov.f32 s8, #1.0" },
        { 0xEE384B0C, "vadd.f64 d4, d8, d12" },
        { 0xEE284B00, "vmul.f64 d4, d8, d0 (bank 0 Vm)" },
        { 0xEEB06B48, "vmov.f64 d6, d8 (wraps inside the bank)" },
    };

    private void MapCode(uint[] words)
    {
        if (fx.Memory.FindBlock(CodeBase) == null)
        {
            fx.Memory.MapFixed(CodeBase, 0x1000, "fuzz code");
        }

        byte[] bytes = words.SelectMany(BitConverter.GetBytes).ToArray();
        bytes.CopyTo(GuestMemory.Span(CodeBase, bytes.Length));
    }

    /// <summary>s0..s31 = distinct values (1.5, 2.75, ...), the FPSCR in <paramref name="fpscr"/>.</summary>
    private static void Init(CpuState* s, uint fpscr)
    {
        *s = default;
        s->MemBase = GuestMemory.Base;
        for (int i = 0; i < 32; i++)
        {
            ((float*)s->D)[i] = 1.5f + i * 1.25f;
        }

        s->R[15] = CodeBase;
        s->Cpsr = 0x10;
        s->Fpscr = fpscr;
    }

    private static ulong[] Registers(CpuState* s)
    {
        ulong[] d = new ulong[32];
        for (int i = 0; i < 32; i++)
        {
            d[i] = s->D[i];
        }

        return d;
    }

    /// <summary>Every FPSCR vector mode: LEN 1-8 with STRIDE 1 and 2 (LEN 1 STRIDE 1 is scalar mode).</summary>
    private static IEnumerable<uint> Modes()
    {
        for (uint len = 0; len < 8; len++)
        {
            foreach (uint stride in new uint[] { 0, 3 })
            {
                if (len != 0 || stride != 0)
                {
                    yield return BaseFpscr | (stride << 20) | (len << 16);
                }
            }
        }
    }

    /// <summary>The registers after the interpreter's vector execution of <paramref name="ins"/> in <paramref name="fpscr"/>.</summary>
    private static ulong[] Reference(CpuState* s, Block b, uint fpscr)
    {
        Init(s, fpscr);
        ref DecodedInst d = ref b.Insts[0];
        Vfp.VectorOp(s, d.Op, d.Raw, d.Address);
        return Registers(s);
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public void ExpansionMatchesTheVectorInterpreter(uint ins, string what)
    {
        MapCode([ins, Svc]);
        Block b = BlockBuilder.Build(CodeBase, false);
        CpuState* s = CpuState.Allocate();
        try
        {
            foreach (uint fpscr in Modes())
            {
                ulong[] expected = Reference(s, b, fpscr);
                Block vector = BlockBuilder.ForVectorMode(b, fpscr);
                Assert.Equal(fpscr & Vfp.FpscrVectorMask, vector.VectorFpscr);
                Init(s, fpscr);
                CpuCore.Interpret(s, vector); // the scalar interpreter: the expansion alone carries the vector rules
                Assert.True(expected.SequenceEqual(Registers(s)), $"{what}, FPSCR 0x{fpscr:X8}: scalar run of the expansion");
            }
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public void X64CodeOfTheExpansionMatchesTheVectorInterpreter(uint ins, string what)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || ExecutableMemory.UnsupportedReason != null)
        {
            return; // the x64 backend only runs on x64 hosts (the ARM64 one is checked below, in the sandbox elsewhere)
        }

        MapCode([ins, Svc]);
        Block b = BlockBuilder.Build(CodeBase, false);
        CpuState* s = CpuState.Allocate();
        try
        {
            foreach (uint fpscr in Modes())
            {
                ulong[] expected = Reference(s, b, fpscr);
                CompiledBlock cb = NativeX64Compiler.Instance.Compile(BlockBuilder.ForVectorMode(b, fpscr))!;
                Init(s, fpscr);
                s->CheckWindow = 0;
                s->LinkBreak = 1;
                NativeRuntime.Instance.Run(s, cb.NativeEntry);
                Assert.True(expected.SequenceEqual(Registers(s)), $"{what}, FPSCR 0x{fpscr:X8}: x64 code");
            }
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    [Arm64SandboxTheory]
    [MemberData(nameof(Ops))]
    public void Arm64CodeOfTheExpansionMatchesTheVectorInterpreter(uint ins, string what)
    {
        MapCode([ins, Svc]);
        Block b = BlockBuilder.Build(CodeBase, false);
        using IArm64Runner runner = Arm64Runner.Create();
        CpuState* s = CpuState.Allocate();
        try
        {
            foreach (uint fpscr in Modes())
            {
                ulong[] expected = Reference(s, b, fpscr);
                CompiledBlock cb = NativeArm64Compiler.Instance.Compile(BlockBuilder.ForVectorMode(b, fpscr), runner.Stubs)!;
                Init(s, fpscr);
                s->CheckWindow = 0;
                s->LinkBreak = 1;
                runner.Run(s, cb.NativeEntry);
                // the FPSCR entry check must not send an expansion back to the interpreter (it runs in vector mode)
                Assert.Equal(0u, s->TierUpRequest);
                Assert.True(expected.SequenceEqual(Registers(s)), $"{what}, FPSCR 0x{fpscr:X8}: ARM64 code");
            }
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    /// <summary>
    /// A vector-mode block is compiled on its second run, not once it is as hot as any block: interpreted, every vector
    /// element goes through the managed VFP handler, and the blocks of a vector loop run only a few times each per pass
    /// (Dead Space's mixer). Three iterations of the loop are enough (before: at least 64 executions and 2000
    /// instructions).</summary>
    [Fact]
    public void VectorModeBlockCompilesOnItsSecondRun()
    {
        if (!Jit.Enabled || Jit.Chain.Length == 0)
        {
            return;
        }

        MapCode([0xEEE10A10, 0xEE384A0C, 0xE2522001, 0x1AFFFFFC, 0xEEE11A10, Svc]);
        using var core = new CpuCore(new BlockCache());
        CpuState* s = core.State;
        s->R[0] = BaseFpscr | (3 << 16);
        s->R[1] = BaseFpscr;
        s->R[2] = (uint)CpuCore.VectorCompileExecutions + 1;
        s->R[15] = CodeBase;
        s->Cpsr = 0x10;
        s->Fpscr = BaseFpscr;
        long compiled = CpuCore.VectorBlocksCompiled;
        long run = CpuCore.VectorBlocksRun;
        Assert.Equal(ExitReason.Svc, core.Run());
        Assert.True(CpuCore.VectorBlocksCompiled > compiled, "the vector-mode block was not compiled on its second run");
        Assert.True(CpuCore.VectorBlocksRun > run, "the compiled vector-mode block never ran");
    }

    /// <summary>
    /// The dispatcher end to end: a loop that runs in vector mode (vmsr; loop: vadd.f32 s8, s16, s24; subs; bne;
    /// vmsr; svc) is compiled for the mode once hot and then runs as that code, with the interpreter's results. Before,
    /// every vector-mode block ran in the interpreter however hot it was.
    /// </summary>
    [Fact]
    public void HotVectorModeLoopRunsAsCompiledCode()
    {
        if (!Jit.Enabled || Jit.Chain.Length == 0)
        {
            return; // no backend on this host: only the interpreter exists
        }

        MapCode([0xEEE10A10, 0xEE384A0C, 0xE2522001, 0x1AFFFFFC, 0xEEE11A10, Svc]);
        uint vectorMode = BaseFpscr | (3 << 16); // LEN = 4
        using var core = new CpuCore(new BlockCache());
        CpuState* s = core.State;
        for (int i = 0; i < 32; i++)
        {
            ((float*)s->D)[i] = i + 1;
        }

        s->R[0] = vectorMode;
        s->R[1] = BaseFpscr;
        s->R[2] = 20_000;
        s->R[15] = CodeBase;
        s->Cpsr = 0x10;
        s->Fpscr = BaseFpscr;
        long compiled = CpuCore.VectorBlocksCompiled;
        Assert.Equal(ExitReason.Svc, core.Run());
        Assert.Equal(BaseFpscr, s->Fpscr);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((17f + i) + (25f + i), ((float*)s->D)[8 + i]);
        }

        Assert.Equal(13f, ((float*)s->D)[12]);
        Assert.True(CpuCore.VectorBlocksCompiled > compiled, "the vector-mode loop block was never compiled");
        // most of the 20 000 iterations ran as compiled code, not in the interpreter
        Assert.True(core.NativeCycles + core.IlCycles > core.InterpCycles * 4,
            $"compiled {core.NativeCycles + core.IlCycles} cycles, interpreted {core.InterpCycles}");
    }
}
