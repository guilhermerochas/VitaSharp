using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>Allegrex HI/LO: multiplies (signed vs unsigned), the madd/msub accumulators and the hardware's division edge cases.</summary>
public sealed class AllegrexMultiplyDivideTests
{
    static (uint Hi, uint Lo) RunHiLo(int funct, uint left, uint right, uint hiBefore = 0, uint loBefore = 0)
    {
        AllegrexRig rig = new(RType(funct, 0, T0, T1));
        rig[T0] = left;
        rig[T1] = right;
        rig.Core.Hi = hiBefore;
        rig.Core.Lo = loBefore;
        rig.Step();
        return (rig.Core.Hi, rig.Core.Lo);
    }

    [Theory]
    [InlineData(0x18, 0xFFFFFFFFu, 2u, 0xFFFFFFFFu, 0xFFFFFFFEu)]          // mult: -1 * 2 = -2
    [InlineData(0x19, 0xFFFFFFFFu, 2u, 0x00000001u, 0xFFFFFFFEu)]          // multu: 0xFFFFFFFF * 2
    [InlineData(0x18, 0x80000000u, 0x80000000u, 0x40000000u, 0u)]          // mult: min * min
    [InlineData(0x19, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFEu, 0x00000001u)]
    [InlineData(0x18, 0x00010000u, 0x00010000u, 1u, 0u)]
    public void Multiplies(int funct, uint left, uint right, uint hi, uint lo)
    {
        Assert.Equal((hi, lo), RunHiLo(funct, left, right));
    }

    [Theory]
    [InlineData(0x1C, 0xFFFFFFFFu, 3u, 0u, 5u, 0u, 2u)]                     // madd: 5 + (-3)
    [InlineData(0x1C, 0xFFFFFFFFu, 3u, 0u, 2u, 0xFFFFFFFFu, 0xFFFFFFFFu)]   // madd borrows into HI
    [InlineData(0x1D, 0xFFFFFFFFu, 3u, 0u, 5u, 3u, 2u)]                     // maddu: 5 + 0x2FFFFFFFD
    [InlineData(0x2E, 0xFFFFFFFFu, 3u, 0u, 5u, 0u, 8u)]                     // msub: 5 - (-3)
    [InlineData(0x2F, 0x00000001u, 6u, 0u, 5u, 0xFFFFFFFFu, 0xFFFFFFFFu)]   // msubu: 5 - 6 wraps the 64-bit pair
    [InlineData(0x1C, 0x7FFFFFFFu, 0x7FFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0x3FFFFFFFu, 0x00000000u)]
    public void Accumulates(int funct, uint left, uint right, uint hiBefore, uint loBefore, uint hi, uint lo)
    {
        Assert.Equal((hi, lo), RunHiLo(funct, left, right, hiBefore, loBefore));
    }

    [Theory]
    [InlineData(0x1A, 7u, 2u, 1u, 3u)]                                // div
    [InlineData(0x1A, 0xFFFFFFF9u, 2u, 0xFFFFFFFFu, 0xFFFFFFFDu)]     // -7 / 2 = -3 rem -1 (towards zero)
    [InlineData(0x1A, 7u, 0xFFFFFFFEu, 1u, 0xFFFFFFFDu)]              // 7 / -2 = -3 rem 1
    [InlineData(0x1A, 0x80000000u, 0xFFFFFFFFu, 0u, 0x80000000u)]     // the one overflow: remainder 0
    [InlineData(0x1A, 5u, 0u, 5u, 0xFFFFFFFFu)]                       // by zero, dividend >= 0
    [InlineData(0x1A, 0u, 0u, 0u, 0xFFFFFFFFu)]
    [InlineData(0x1A, 0xFFFFFFFBu, 0u, 0xFFFFFFFBu, 1u)]              // by zero, dividend < 0
    [InlineData(0x1B, 0xFFFFFFF9u, 2u, 1u, 0x7FFFFFFCu)]              // divu
    [InlineData(0x1B, 0xFFFFu, 0u, 0xFFFFu, 0xFFFFu)]                 // divu by zero, dividend <= 0xFFFF
    [InlineData(0x1B, 0x10000u, 0u, 0x10000u, 0xFFFFFFFFu)]           // divu by zero, dividend > 0xFFFF
    [InlineData(0x1B, 0u, 0u, 0u, 0xFFFFu)]
    public void Divides(int funct, uint dividend, uint divisor, uint hi, uint lo)
    {
        Assert.Equal((hi, lo), RunHiLo(funct, dividend, divisor, 0x11111111, 0x22222222));
    }

    [Fact]
    public void DivisionByZeroRaisesNoException()
    {
        AllegrexRig rig = new(RType(0x1A, 0, T0, T1), RType(0x1B, 0, T0, T1));
        rig[T0] = 1;
        rig.Step(2);
        Assert.Equal(AllegrexRig.CodeBase + 8, rig.Core.Pc);
    }

    [Fact]
    public void MovesToAndFromHiLo()
    {
        AllegrexRig rig = new(
            RType(0x11, 0, T0, 0),  // mthi t0
            RType(0x13, 0, T1, 0),  // mtlo t1
            RType(0x10, T2, 0, 0),  // mfhi t2
            RType(0x12, A0, 0, 0)); // mflo a0
        rig[T0] = 0xAAAA5555;
        rig[T1] = 0x12345678;
        rig.Step(4);
        Assert.Equal(0xAAAA5555u, rig[T2]);
        Assert.Equal(0x12345678u, rig[A0]);
        Assert.Equal(0xAAAA5555u, rig.Core.Hi);
        Assert.Equal(0x12345678u, rig.Core.Lo);
    }
}
