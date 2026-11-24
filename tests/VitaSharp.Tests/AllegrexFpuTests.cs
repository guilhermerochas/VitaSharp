using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// Allegrex FPU (COP1): arithmetic in the four rounding modes, the IEEE flags/causes/enables of FCR31, the PSP's NaN
/// and flush-to-zero rules, conversions, compares, the condition branches, moves and loads/stores.
/// </summary>
public sealed class AllegrexFpuTests
{
    private const uint One = 0x3F800000;
    private const uint Two = 0x40000000;
    private const uint Three = 0x40400000;
    private const uint MinDenormal = 0x00000001;
    private const uint MaxFloat = 0x7F7FFFFF;
    private const uint Infinity = 0x7F800000;

    private static uint Fpu(int funct, int fd, int fs, int ft)
    {
        return 0x46000000u | (uint)(ft << 16) | (uint)(fs << 11) | (uint)(fd << 6) | (uint)funct;
    }

    private static uint Bits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    /// <summary>Runs fd(2) = fs(0) op ft(1) with FCR31 preset; returns the result bits and the FCR31 after.</summary>
    private static (uint Result, uint Fcr31) Run(int funct, uint left, uint right, uint fcr31 = 0)
    {
        AllegrexRig rig = new(Fpu(funct, 2, 0, 1));
        rig.Core.Fpr[0] = left;
        rig.Core.Fpr[1] = right;
        rig.Core.Fpr[2] = 0xDEADBEEF;
        rig.Core.Fcr31 = fcr31;
        rig.Step();
        Assert.Equal(AllegrexRig.CodeBase + 4, rig.Core.Pc);
        return (rig.Core.Fpr[2], rig.Core.Fcr31);
    }

    [Theory]
    [InlineData(0x00, One, Two, Three)]                 // add.s
    [InlineData(0x01, One, Two, 0xBF800000u)]           // sub.s
    [InlineData(0x02, Two, Three, 0x40C00000u)]         // mul.s
    [InlineData(0x03, Three, Two, 0x3FC00000u)]         // div.s
    [InlineData(0x04, 0x40800000u, 0u, Two)]            // sqrt.s 4 = 2
    [InlineData(0x04, 0x80000000u, 0u, 0x80000000u)]    // sqrt.s -0 = -0
    [InlineData(0x05, 0xBF800000u, 0u, One)]            // abs.s
    [InlineData(0x06, 0x7F812345u, 0u, 0x7F812345u)]    // mov.s keeps the bits
    [InlineData(0x07, One, 0u, 0xBF800000u)]            // neg.s
    [InlineData(0x07, 0x7FC00000u, 0u, 0xFFC00000u)]    // neg.s flips a NaN's sign
    public void ExactArithmetic(int funct, uint left, uint right, uint expected)
    {
        (uint result, uint fcr31) = Run(funct, left, right);
        Assert.Equal(expected, result);
        Assert.Equal(0u, fcr31 & 0x3F07C);
    }

    // 1 + 2^-24: halfway between 1 and the next float; 1 + 3*2^-25 lies above the halfway point
    [Theory]
    [InlineData(0u, 0x33800000u, One)]          // nearest, tie to even: 1
    [InlineData(1u, 0x33800000u, One)]          // towards zero
    [InlineData(2u, 0x33800000u, 0x3F800001u)]  // towards +inf
    [InlineData(3u, 0x33800000u, One)]          // towards -inf
    [InlineData(0u, 0x33C00000u, 0x3F800001u)]  // nearest, above the tie
    [InlineData(1u, 0x33C00000u, One)]
    public void AddRoundsByTheMode(uint mode, uint addend, uint expected)
    {
        (uint result, uint fcr31) = Run(0x00, One, addend, mode);
        Assert.Equal(expected, result);
        Assert.Equal(AllegrexCore.FpuInexact, (fcr31 >> 12) & 0x1F);
        Assert.Equal(AllegrexCore.FpuInexact, (fcr31 >> 2) & 0x1F);
    }

    [Theory]
    [InlineData(0u, 0xBF800001u, 0xBF800000u)] // nearest
    [InlineData(1u, 0xBF800001u, 0xBF800000u)] // towards zero
    [InlineData(2u, 0xBF800001u, 0xBF800000u)] // towards +inf
    [InlineData(3u, 0xBF800001u, 0xBF800001u)] // towards -inf
    public void MultiplyRoundsNegativeResultsByTheMode(uint mode, uint left, uint expected)
    {
        // (-1 - 2^-23) * (1 - 2^-24) = -1 - 2^-24 + 2^-47: between -1 and its neighbour, just short of the tie
        (uint result, _) = Run(0x02, left, 0x3F7FFFFFu, mode);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0u, One, Three, 0x3EAAAAABu)] // 1/3 nearest
    [InlineData(1u, One, Three, 0x3EAAAAAAu)]
    [InlineData(2u, One, Three, 0x3EAAAAABu)]
    [InlineData(3u, One, Three, 0x3EAAAAAAu)]
    [InlineData(0u, Two, 0u, 0x3FB504F3u)]    // sqrt 2 nearest
    [InlineData(2u, Two, 0u, 0x3FB504F4u)]
    [InlineData(3u, Two, 0u, 0x3FB504F3u)]
    public void DivideAndSquareRootRoundByTheMode(uint mode, uint left, uint right, uint expected)
    {
        int funct = right == 0 ? 0x04 : 0x03;
        (uint result, _) = Run(funct, left, right, mode);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0u, Infinity)]  // nearest: inf
    [InlineData(1u, MaxFloat)]  // towards zero: the largest float
    [InlineData(2u, Infinity)]
    [InlineData(3u, MaxFloat)]
    public void OverflowFollowsTheMode(uint mode, uint expected)
    {
        (uint result, uint fcr31) = Run(0x02, MaxFloat, Two, mode);
        Assert.Equal(expected, result);
        Assert.Equal(AllegrexCore.FpuOverflow | AllegrexCore.FpuInexact, (fcr31 >> 12) & 0x1F);
    }

    [Theory]
    [InlineData(0x00, Infinity, 0xFF800000u)] // inf - inf
    [InlineData(0x02, Infinity, 0u)]          // inf * 0
    [InlineData(0x03, 0u, 0u)]                // 0 / 0
    [InlineData(0x03, Infinity, Infinity)]    // inf / inf
    [InlineData(0x04, 0xBF800000u, 0u)]       // sqrt(-1)
    public void InvalidOperationsMakeThePositiveDefaultNaN(int funct, uint left, uint right)
    {
        (uint result, uint fcr31) = Run(funct, left, right);
        Assert.Equal(0x7FC00000u, result);
        Assert.Equal(AllegrexCore.FpuInvalid, (fcr31 >> 12) & 0x1F);
        Assert.Equal(AllegrexCore.FpuInvalid, (fcr31 >> 2) & 0x1F);
    }

    [Fact]
    public void NaNOperandsPropagateQuietened()
    {
        Assert.Equal(0xFFC12345u, Run(0x00, 0xFFC12345, One).Result);   // quiet NaN kept, no flag
        Assert.Equal(0u, Run(0x00, 0xFFC12345, One).Fcr31);
        (uint result, uint fcr31) = Run(0x02, One, 0x7F812345);           // signaling NaN: quietened, invalid
        Assert.Equal(0x7FC12345u, result);
        Assert.Equal(AllegrexCore.FpuInvalid, (fcr31 >> 12) & 0x1F);
    }

    [Fact]
    public void DivisionByZeroGivesASignedInfinityAndTheFlag()
    {
        (uint result, uint fcr31) = Run(0x03, 0xBF800000, 0);
        Assert.Equal(0xFF800000u, result);
        Assert.Equal(AllegrexCore.FpuDivideByZero, (fcr31 >> 12) & 0x1F);
        Assert.Equal(AllegrexCore.FpuDivideByZero, (fcr31 >> 2) & 0x1F);
        Assert.Equal(0u, Run(0x03, Infinity, 0).Fcr31); // inf / 0 is exact
    }

    [Fact]
    public void EnabledExceptionTrapsAndKeepsTheDestination()
    {
        uint enableDivideByZero = AllegrexCore.FpuDivideByZero << 7;
        AllegrexRig rig = new(Fpu(0x03, 2, 0, 1));
        rig.Core.Fpr[0] = One;
        rig.Core.Fpr[2] = 0x12345678;
        rig.Core.Fcr31 = enableDivideByZero;
        rig.Step();
        Assert.Equal(0x12345678u, rig.Core.Fpr[2]);
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
        Assert.Equal((uint)AllegrexExceptionCode.FloatingPoint << 2, rig.Core.Cause & AllegrexCore.CauseExceptionCodeMask);
        Assert.Equal(AllegrexCore.FpuDivideByZero, (rig.Core.Fcr31 >> 12) & 0x1F); // the cause is set
        Assert.Equal(0u, (rig.Core.Fcr31 >> 2) & 0x1F);                            // the flag is not
    }

    [Fact]
    public void CausesAreClearedByTheNextOperationFlagsStay()
    {
        AllegrexRig rig = new(Fpu(0x03, 2, 0, 1), Fpu(0x00, 3, 0, 0));
        rig.Core.Fpr[0] = One;
        rig.Step(2);
        Assert.Equal(0u, (rig.Core.Fcr31 >> 12) & 0x1F);
        Assert.Equal(AllegrexCore.FpuDivideByZero, (rig.Core.Fcr31 >> 2) & 0x1F);
    }

    [Fact]
    public void DenormalsAreKeptWithoutFlushToZero()
    {
        (uint result, uint fcr31) = Run(0x03, 0x00800000, Two); // FLT_MIN / 2
        Assert.Equal(0x00400000u, result);
        Assert.Equal(0u, (fcr31 >> 12) & 0x1F);                 // exact: no underflow
        (uint tiny, uint tinyFcr) = Run(0x02, MinDenormal, 0x3F000000); // 2^-149 * 0.5: inexact underflow
        Assert.Equal(0u, tiny);                                          // ties to even (zero)
        Assert.Equal(AllegrexCore.FpuUnderflow | AllegrexCore.FpuInexact, (tinyFcr >> 12) & 0x1F);
    }

    [Fact]
    public void FlushToZeroFlushesResultsNotInputs()
    {
        uint fs = AllegrexCore.Fcr31FlushToZero;
        Assert.Equal(0u, Run(0x03, 0x00800000, Two, fs).Result);            // a denormal result flushes
        Assert.Equal(0x80000000u, Run(0x03, 0x80800000, Two, fs).Result);   // keeping its sign
        // tininess before rounding: (FLT_MIN - ulp) * (1 + 2^-23) rounds up to FLT_MIN but is still flushed
        Assert.Equal(0u, Run(0x02, 0x007FFFFF, 0x3F800001, fs).Result);
        Assert.Equal(0x3F800000u, Run(0x03, 0x00400000, 0x00400000, fs).Result); // inputs are seen as they are
        Assert.Equal(0x00000005u, Run(0x00, 0, 0x00000005, fs).Result);    // add with a zero returns the other
        Assert.Equal(0x80000005u, Run(0x01, 0, 0x00000005, fs).Result);    // sub: 0 - x
    }

    [Fact]
    public void ExactZeroSumIsNegativeOnlyRoundingDown()
    {
        Assert.Equal(0u, Run(0x00, One, 0xBF800000).Result);
        Assert.Equal(0x80000000u, Run(0x00, One, 0xBF800000, 3).Result);
        Assert.Equal(0u, Run(0x00, 0, 0, 3).Result);
    }

    [Theory]
    [InlineData(0x0C, 0x40200000u, 2u)]           // round.w.s 2.5 -> 2 (half to even)
    [InlineData(0x0C, 0x40600000u, 4u)]           // round.w.s 3.5 -> 4
    [InlineData(0x0C, 0xC0200000u, 0xFFFFFFFEu)]  // -2.5 -> -2
    [InlineData(0x0D, 0xC0600000u, 0xFFFFFFFDu)]  // trunc.w.s -3.5 -> -3
    [InlineData(0x0E, 0x3F800001u, 2u)]           // ceil.w.s
    [InlineData(0x0E, 0x00000001u, 1u)]           // ceil of a denormal (never flushed)
    [InlineData(0x0F, 0xBF800001u, 0xFFFFFFFEu)]  // floor.w.s
    [InlineData(0x0D, 0x4F000000u, 0x7FFFFFFFu)]  // 2^31 saturates
    [InlineData(0x0D, 0xCF000000u, 0x80000000u)]  // -2^31 fits
    [InlineData(0x0D, 0xDF000000u, 0x80000000u)]  // -2^63 saturates
    [InlineData(0x0D, 0x7FC00000u, 0x7FFFFFFFu)]  // NaN
    [InlineData(0x0D, 0xFFC00000u, 0x7FFFFFFFu)]
    public void ConversionsToWord(int funct, uint value, uint expected)
    {
        Assert.Equal(expected, Run(funct, value, 0).Result);
    }

    [Theory]
    [InlineData(0u, 0x40200000u, 2u)]          // cvt.w.s follows the mode: nearest even
    [InlineData(1u, 0x40600000u, 3u)]          // towards zero
    [InlineData(2u, 0x40200000u, 3u)]          // towards +inf
    [InlineData(3u, 0xC0200000u, 0xFFFFFFFDu)] // towards -inf
    public void CvtWordFollowsTheRoundingMode(uint mode, uint value, uint expected)
    {
        (uint result, uint fcr31) = Run(0x24, value, 0, mode);
        Assert.Equal(expected, result);
        Assert.Equal(AllegrexCore.FpuInexact, (fcr31 >> 12) & 0x1F);
    }

    [Theory]
    [InlineData(0u, 0x01000001u, 0x4B800000u)] // 16777217 -> 16777216 (nearest even)
    [InlineData(2u, 0x01000001u, 0x4B800001u)] // towards +inf
    [InlineData(0u, 0xFFFFFFFFu, 0xBF800000u)] // -1
    public void CvtSingleFromWord(uint mode, uint value, uint expected)
    {
        AllegrexRig rig = new(0x46800020u | (0u << 11) | (2u << 6));
        rig.Core.Fpr[0] = value;
        rig.Core.Fcr31 = mode;
        rig.Step();
        Assert.Equal(expected, rig.Core.Fpr[2]);
    }

    [Theory]
    [InlineData(0x30, One, Two, false)]          // c.f
    [InlineData(0x32, One, One, true)]           // c.eq
    [InlineData(0x32, 0x80000000u, 0u, true)]    // -0 == +0
    [InlineData(0x34, One, Two, true)]           // c.olt
    [InlineData(0x34, Two, One, false)]
    [InlineData(0x36, Two, Two, true)]           // c.ole
    [InlineData(0x31, 0x7FC00000u, One, true)]   // c.un
    [InlineData(0x33, 0x7FC00000u, One, true)]   // c.ueq
    [InlineData(0x34, 0x7FC00000u, One, false)]  // c.olt with NaN
    [InlineData(0x35, 0x7FC00000u, One, true)]   // c.ult with NaN
    [InlineData(0x32, 0x00000001u, 0u, false)]   // a denormal is not zero (never flushed)
    public void Compares(int funct, uint left, uint right, bool expected)
    {
        AllegrexRig rig = new(Fpu(funct, 0, 0, 1));
        rig.Core.Fpr[0] = left;
        rig.Core.Fpr[1] = right;
        rig.Core.Fcr31 = expected ? 0 : AllegrexCore.Fcr31Condition;
        rig.Step();
        Assert.Equal(expected, rig.Core.FpuCondition);
    }

    [Fact]
    public void SignalingComparesFlagInvalidOnAQuietNaN()
    {
        AllegrexRig quiet = new(Fpu(0x34, 0, 0, 1));
        quiet.Core.Fpr[0] = 0x7FC00000;
        quiet.Step();
        Assert.Equal(0u, quiet.Core.Fcr31 & 0x3F07C);

        AllegrexRig signaling = new(Fpu(0x3C, 0, 0, 1)); // c.lt
        signaling.Core.Fpr[0] = 0x7FC00000;
        signaling.Step();
        Assert.Equal(AllegrexCore.FpuInvalid, (signaling.Core.Fcr31 >> 2) & 0x1F);
        Assert.False(signaling.Core.FpuCondition);
    }

    [Theory]
    [InlineData(0, false, true)]  // bc1f
    [InlineData(1, true, true)]   // bc1t
    [InlineData(1, false, false)]
    [InlineData(2, true, false)]  // bc1fl, not taken: the slot is annulled
    [InlineData(3, true, true)]   // bc1tl
    public void ConditionBranches(int kind, bool condition, bool taken)
    {
        uint branch = 0x45000000u | (uint)(kind << 16) | 3u;
        AllegrexRig rig = new(branch, Addiu(T1, T1, 1), Nop, Nop, Nop);
        rig.Core.Fcr31 = condition ? AllegrexCore.Fcr31Condition : 0;
        rig.Step(2);
        bool likely = kind >= 2;
        Assert.Equal(taken ? AllegrexRig.CodeBase + 16 : AllegrexRig.CodeBase + (likely ? 12u : 8u), rig.Core.Pc);
        Assert.Equal(!likely || taken ? 1u : 0u, rig[T1]);
    }

    [Fact]
    public void MovesAndControlRegisters()
    {
        uint mtc1 = 0x44800000u | ((uint)T0 << 16) | (5u << 11);
        uint mfc1 = 0x44000000u | ((uint)T1 << 16) | (5u << 11);
        uint ctc1 = 0x44C00000u | ((uint)T2 << 16) | (31u << 11);
        uint cfc1 = 0x44400000u | ((uint)A0 << 16) | (31u << 11);
        uint cfc0 = 0x44400000u | ((uint)A1 << 16) | (0u << 11);
        AllegrexRig rig = new(mtc1, mfc1, ctc1, cfc1, cfc0);
        rig[T0] = 0x40490FDB;
        rig[T2] = 0xFFFFFFFF;
        rig.Step(5);
        Assert.Equal(0x40490FDBu, rig.Core.Fpr[5]);
        Assert.Equal(0x40490FDBu, rig[T1]);
        Assert.Equal(AllegrexCore.Fcr31WriteMask, rig[A0]);
        Assert.True(rig.Core.FpuCondition);
        Assert.Equal(AllegrexCore.Fcr0Value, rig[A1]);
    }

    [Fact]
    public void Lwc1AndSwc1()
    {
        AllegrexRig rig = new(IType(0x31, 3, T0, 4), IType(0x39, 3, T0, 8), IType(0x31, 4, T0, 2));
        rig.Bus.Load(0x88010004, [0x3F800000]);
        rig[T0] = 0x88010000;
        rig.Step(3);
        Assert.Equal(One, rig.Core.Fpr[3]);
        Assert.Equal(One, rig.Bus.Read32(0x08010008));
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc); // the misaligned lwc1
        Assert.Equal((uint)AllegrexExceptionCode.AddressErrorLoad << 2, rig.Core.Cause & AllegrexCore.CauseExceptionCodeMask);
    }
}
