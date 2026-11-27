using System.Runtime.InteropServices;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// VMLA/VMLS/VNMLA/VNMLS under FPSCR.FZ: the product is rounded and flushed on its own, and ARM detects its tininess
/// before rounding (the ARM ARM's FPRound flushes on the unrounded exponent): 2^-63 * (2^-63 - 2^-87), just below
/// 2^-126, rounds up to 2^-126 on x86 and in .NET but is a zero on ARM, so d keeps the accumulator alone. The
/// interpreter added the rounded 2^-126 (its managed math runs without FTZ; seen under qemu-user 10, whose AArch64 FMUL
/// follows the ARM ARM: Arm64SingleCacheMatchesInterpreter / Arm64SimdCacheMatchesInterpreter, D2 0x80800000 against
/// 0x80000000). The x64 code (SSE under FTZ, tininess before rounding too) and the ARM64 code were right; all three
/// are checked (the ARM64 code natively on ARM64 hosts).
/// </summary>
[Collection("guest")]
public sealed unsafe class VfpProductFlushTests(GuestMemoryFixture fx)
{
    private const uint CodeBase = 0x0010_0000;
    private const uint VitaFpscr = 0x0300_0000; // DN + FZ, round to nearest
    private const uint Svc = 0xEF000000;
    private const uint FactorN = 0x20000000;    // s0 = 2^-63
    private const uint FactorM = 0x1FFFFFFF;    // s1 = 2^-63 * (1 - 2^-24): the exact product is tiny
    private const uint Accumulator = 0x01000000; // s2 = 2^-125

    /// <summary>Instruction (s2, s0, s1), accumulator, expected s2.</summary>
    public static readonly TheoryData<uint, uint, uint, string> Cases = new()
    {
        { 0xEE001A20, Accumulator, 0x01000000, "vmla.f32 s2, s0, s1: 2^-125 + 0" },
        { 0xEE001A60, Accumulator, 0x01000000, "vmls.f32 s2, s0, s1: 2^-125 - 0" },
        { 0xEE101A20, Accumulator, 0x81000000, "vnmls.f32 s2, s0, s1: -2^-125 + 0" },
        { 0xEE101A60, Accumulator, 0x81000000, "vnmla.f32 s2, s0, s1: -2^-125 - 0" },
        { 0xEE001A20, 0x00000000, 0x00000000, "vmla.f32 s2, s0, s1: 0 + 0 (not the smallest normal)" },
        { 0xEE201A20, 0x00000000, 0x00000000, "vmul.f32 s2, s0, s1: the product alone" },
    };

    private void MapCode(uint ins)
    {
        if (fx.Memory.FindBlock(CodeBase) == null)
        {
            fx.Memory.MapFixed(CodeBase, 0x1000, "fuzz code");
        }

        BitConverter.GetBytes(ins).CopyTo(GuestMemory.Span(CodeBase, 4));
        BitConverter.GetBytes(Svc).CopyTo(GuestMemory.Span(CodeBase + 4, 4));
    }

    private static void Init(CpuState* s, uint accumulator)
    {
        *s = default;
        s->MemBase = GuestMemory.Base;
        ((uint*)s->D)[0] = FactorN;
        ((uint*)s->D)[1] = FactorM;
        ((uint*)s->D)[2] = accumulator;
        s->R[15] = CodeBase;
        s->Cpsr = 0x10;
        s->Fpscr = VitaFpscr;
        s->CheckWindow = 0;
        s->LinkBreak = 1;
    }

    private static uint S2(CpuState* s) => ((uint*)s->D)[2];

    [Theory]
    [MemberData(nameof(Cases))]
    public void InterpreterFlushesTheTinyProduct(uint ins, uint accumulator, uint expected, string what)
    {
        CpuState* s = CpuState.Allocate();
        try
        {
            Init(s, accumulator);
            Vfp.Arith(s, ins, CodeBase);
            Assert.True(S2(s) == expected, $"{what}: interpreter 0x{S2(s):X8}, expected 0x{expected:X8}");
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void X64CodeFlushesTheTinyProduct(uint ins, uint accumulator, uint expected, string what)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || ExecutableMemory.UnsupportedReason != null)
        {
            return; // the x64 backend only runs on x64 hosts
        }

        MapCode(ins);
        CpuState* s = CpuState.Allocate();
        try
        {
            CompiledBlock cb = NativeX64Compiler.Instance.Compile(BlockBuilder.Build(CodeBase, false))!;
            Init(s, accumulator);
            NativeRuntime.Instance.Run(s, cb.NativeEntry);
            Assert.True(S2(s) == expected, $"{what}: x64 code 0x{S2(s):X8}, expected 0x{expected:X8}");
        }
        finally
        {
            CpuState.Free(s);
        }
    }

    [Arm64SandboxTheory]
    [MemberData(nameof(Cases))]
    public void Arm64CodeFlushesTheTinyProduct(uint ins, uint accumulator, uint expected, string what)
    {
        MapCode(ins);
        using IArm64Runner runner = Arm64Runner.Create();
        CpuState* s = CpuState.Allocate();
        try
        {
            CompiledBlock cb = NativeArm64Compiler.Instance.Compile(BlockBuilder.Build(CodeBase, false), runner.Stubs)!;
            Init(s, accumulator);
            runner.Run(s, cb.NativeEntry);
            Assert.True(S2(s) == expected, $"{what}: ARM64 code 0x{S2(s):X8}, expected 0x{expected:X8}");
        }
        finally
        {
            CpuState.Free(s);
        }
    }
}
