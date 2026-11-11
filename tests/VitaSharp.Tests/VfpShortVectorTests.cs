using VitaSharp.Core.Cpu;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// VFP short vectors (FPSCR LEN/STRIDE), as VFPv2-era code (Android ports run through so_loader) uses them: VMSR sets
/// the mode, vector-capable instructions run on register banks, VMSR clears it. The Cortex-A9 has no short-vector
/// hardware (nor has Unicorn's Cortex-A9 model), so the expectations follow the ARM ARM rules by hand.
/// </summary>
[Collection("guest")]
public sealed unsafe class VfpShortVectorTests(GuestMemoryFixture fx)
{
    const uint CodeBase = 0x0010_0000;
    const uint BaseFpscr = 0x0300_0000; // DN + FZ (the Vita runtime's mode)
    const uint Vadd = 0xEE384A0C; // vadd.f32 s8, s16, s24

    /// <summary>Runs vmsr fpscr, r0; the instruction; vmsr fpscr, r1; svc 0 through the CPU's dispatcher.</summary>
    static void Run(CpuCore core, uint ins, uint vecFpscr)
    {
        uint[] code = [0xEEE10A10, ins, 0xEEE11A10, 0xEF000000];
        var bytes = code.SelectMany(BitConverter.GetBytes).ToArray();
        bytes.CopyTo(GuestMemory.Span(CodeBase, bytes.Length));
        var s = core.State;
        s->R[0] = vecFpscr;
        s->R[1] = BaseFpscr;
        s->R[15] = CodeBase;
        s->Cpsr = 0x10;
        s->Fpscr = BaseFpscr;
        Assert.Equal(ExitReason.Svc, core.Run());
        Assert.Equal(BaseFpscr, s->Fpscr);
    }

    static ref float S(CpuState* s, int n) => ref ((float*)s->D)[n];
    static ref double D(CpuState* s, int n) => ref ((double*)s->D)[n];

    CpuCore NewCore()
    {
        if (fx.Memory.FindBlock(CodeBase) == null)
        {
            fx.Memory.MapFixed(CodeBase, 0x1000, "fuzz code");
        }

        var core = new CpuCore(new BlockCache());
        for (int i = 0; i < 32; i++)
        {
            core.State->D[i] = 0;
        }

        for (int i = 0; i < 32; i++)
        {
            S(core.State, i) = i + 1; // s0..s31 = 1..32
        }

        return core;
    }

    [Fact]
    public void VectorAddUsesFourElements()
    {
        using var core = NewCore();
        var s = core.State;
        Run(core, Vadd, BaseFpscr | (3 << 16)); // LEN = 4
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((17 + i) + (25 + i), S(s, 8 + i));
        }

        Assert.Equal(13f, S(s, 12)); // the fifth element is untouched
    }

    [Fact]
    public void BankZeroOperandIsScalar()
    {
        using var core = NewCore();
        var s = core.State;
        Run(core, 0xEE284A00, BaseFpscr | (3 << 16)); // vmul.f32 s8, s16, s0: s0 applies to every element
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((17 + i) * 1f, S(s, 8 + i));
        }

        using var core2 = NewCore();
        s = core2.State;
        Run(core2, 0xEE380A0C, BaseFpscr | (3 << 16)); // vadd.f32 s0, s16, s24: destination in bank 0 = scalar
        Assert.Equal(17f + 25f, S(s, 0));
        Assert.Equal(2f, S(s, 1));
    }

    [Fact]
    public void VectorsWrapInsideTheirBank()
    {
        using var core = NewCore();
        var s = core.State;
        Run(core, 0xEEB07A48, BaseFpscr | (3 << 16)); // vmov.f32 s14, s16, LEN = 4: s14, s15, s8, s9
        Assert.Equal([17f, 18f, 19f, 20f], new[] { S(s, 14), S(s, 15), S(s, 8), S(s, 9) });
        Assert.Equal(11f, S(s, 10));
    }

    [Fact]
    public void DoubleStrideTwo()
    {
        using var core = NewCore();
        var s = core.State;
        for (int i = 0; i < 16; i++)
        {
            D(s, i) = i * 10;
        }

        Run(core, 0xEE384B0C, BaseFpscr | (3 << 20) | (1 << 16)); // vadd.f64 d4, d8, d12, LEN = 2, STRIDE = 2
        Assert.Equal(80.0 + 120.0, D(s, 4));
        Assert.Equal(100.0 + 140.0, D(s, 6));
        Assert.Equal(50.0, D(s, 5));
    }

    [Fact]
    public void ScalarModeIsUnchanged()
    {
        using var core = NewCore();
        var s = core.State;
        Run(core, Vadd, BaseFpscr); // no LEN
        Assert.Equal(17f + 25f, S(s, 8));
        Assert.Equal(10f, S(s, 9));
    }
}
