using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexVfpuRegisterTests;

namespace VitaSharp.Tests;

/// <summary>
/// Allegrex VFPU vector instructions: lane arithmetic with the VFPU's NaN/denormal rules, prefixes, the exact dot
/// product family, compares, min/max, the unary and transcendental group, conversions, the generator and the helpers.
/// Registers: vd = C000 (0), vs = C010 (1), vt = C020 (2), all quads unless a test says otherwise.
/// </summary>
public sealed class AllegrexVfpuVectorTests
{
    private const int Vd = 0;
    private const int Vs = 1;
    private const int Vt = 2;
    private const uint One = 0x3F800000;
    private const uint Two = 0x40000000;
    private const uint Half = 0x3F000000;
    private const uint Infinity = 0x7F800000;
    private const uint VfpuNaN = 0x7F800001;

    private static uint SizeBits(int size)
    {
        return size switch
        {
            1 => 0u,
            2 => 0x80u,
            3 => 0x8000u,
            _ => 0x8080u,
        };
    }

    private static uint Op(uint opcode, uint sub, int size = 4, int vd = Vd, int vs = Vs, int vt = Vt)
    {
        return (opcode << 26) | (sub << 23) | ((uint)vt << 16) | ((uint)vs << 8) | (uint)vd | SizeBits(size);
    }

    /// <summary>Opcode 0x34 forms: group (bits 21-25) and operation (bits 16-20).</summary>
    private static uint Op34(uint group, uint operation, int size = 4, int vd = Vd, int vs = Vs)
    {
        return 0xD0000000u | (group << 21) | (operation << 16) | ((uint)vs << 8) | (uint)vd | SizeBits(size);
    }

    private static float F(uint bits)
    {
        return BitConverter.UInt32BitsToSingle(bits);
    }

    private static uint B(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    private static void SetColumn(AllegrexCore core, int column, params uint[] lanes)
    {
        for (int i = 0; i < lanes.Length; i++)
        {
            core.WriteVfpuRegister(column | (i << 5), lanes[i]);
        }
    }

    private static uint[] Column(AllegrexCore core, int column, int size = 4)
    {
        uint[] lanes = new uint[size];
        for (int i = 0; i < size; i++)
        {
            lanes[i] = core.ReadVfpuRegister(column | (i << 5));
        }

        return lanes;
    }

    private static AllegrexRig Run(uint[] s, uint[] t, params uint[] code)
    {
        AllegrexRig rig = new(code);
        SetColumn(rig.Core, Vs, s);
        SetColumn(rig.Core, Vt, t);
        SetColumn(rig.Core, Vd, 0xDEAD0000, 0xDEAD0001, 0xDEAD0002, 0xDEAD0003);
        rig.Step(code.Length);
        return rig;
    }

    [Fact]
    public void LaneArithmetic()
    {
        uint[] s = [One, Two, B(3), B(-8)];
        uint[] t = [Two, Two, Half, B(2)];
        Assert.Equal(new[] { B(3), B(4), B(3.5f), B(-6) }, Column(Run(s, t, Op(0x18, 0)).Core, Vd));     // vadd
        Assert.Equal(new[] { B(-1), 0u, B(2.5f), B(-10) }, Column(Run(s, t, Op(0x18, 1)).Core, Vd));     // vsub
        Assert.Equal(new[] { Two, B(4), B(1.5f), B(-16) }, Column(Run(s, t, Op(0x19, 0)).Core, Vd));     // vmul
        Assert.Equal(new[] { Half, One, B(6), B(-4) }, Column(Run(s, t, Op(0x18, 7)).Core, Vd));         // vdiv
    }

    [Fact]
    public void VfpuNaNsHaveNoPayloadAndFollowTheSignRules()
    {
        uint[] s = [Infinity, 0x7FC12345, 0, 0xFF800000];
        uint[] t = [0xFF800000, One, 0xFF800000, 0];
        // add: inf - inf and a NaN operand give the positive VFPU NaN
        Assert.Equal(new[] { VfpuNaN, VfpuNaN, 0xFF800000u, 0xFF800000u }, Column(Run(s, t, Op(0x18, 0)).Core, Vd));
        uint[] negativeNaN = Column(Run([0xFFC00000, 0, 0, 0], [One, 0, 0, 0], Op(0x18, 0)).Core, Vd);
        Assert.Equal(VfpuNaN, negativeNaN[0]);
        uint[] product = Column(Run(s, t, Op(0x19, 0)).Core, Vd);
        Assert.Equal(B(float.NegativeInfinity), product[0]);   // inf * -inf
        Assert.Equal(VfpuNaN, product[1]);                    // NaN * 1: no payload
        Assert.Equal(0xFF800001u, product[2]);                // 0 * -inf: the signs' XOR
        Assert.Equal(0xFF800001u, product[3]);                // -inf * 0
    }

    [Fact]
    public void DenormalInputsAndResultsAreZero()
    {
        uint[] s = [0x00000005, 0x00800000, 0x80000000, One];
        uint[] t = [0, Half, 0x80000000, 0x00000001];
        uint[] sum = Column(Run(s, t, Op(0x18, 0)).Core, Vd);
        Assert.Equal(0u, sum[0]);            // a denormal plus zero is zero
        Assert.Equal(0x80000000u, sum[2]);   // -0 + -0 stays -0 (vadd is not a summing op)
        Assert.Equal(One, sum[3]);           // 1 + denormal = 1
        uint[] product = Column(Run(s, t, Op(0x19, 0)).Core, Vd);
        Assert.Equal(0u, product[1]);        // FLT_MIN * 0.5 is a denormal result: zero
    }

    [Fact]
    public void SourcePrefixSwizzlesNegatesAndTakesConstants()
    {
        // S prefix: lane0 = w, lane1 = -z, lane2 = |y|, lane3 = constant 1/2 (constant bit, swizzle 3)
        uint prefix = 3u | (2u << 2) | (1u << 4) | (3u << 6) | (1u << (8 + 2)) | (1u << (12 + 3)) | (1u << (16 + 1));
        uint[] s = [One, B(-5), B(3), B(4)];
        uint[] t = [0, 0, 0, 0];
        AllegrexRig rig = Run(s, t, Prefix(AllegrexCore.VfpuSPrefix, prefix), Op(0x18, 0));
        Assert.Equal(new[] { B(4), B(-3), B(5), Half }, Column(rig.Core, Vd));
        Assert.Equal(AllegrexCore.VfpuIdentityPrefix, rig.Core.ReadVfpuControl(AllegrexCore.VfpuSPrefix)); // eaten
    }

    [Fact]
    public void DestinationPrefixSaturatesAndMasks()
    {
        // lane0 [0,1], lane1 [-1,1], lane2 masked, lane3 untouched
        uint prefix = 1u | (3u << 2) | (1u << (8 + 2));
        uint[] s = [B(5), B(-5), B(7), B(-7)];
        uint[] t = [0, 0, 0, 0];
        AllegrexRig rig = Run(s, t, Prefix(AllegrexCore.VfpuDPrefix, prefix), Op(0x18, 0));
        Assert.Equal(new[] { One, B(-1), 0xDEAD0002u, B(-7) }, Column(rig.Core, Vd));
        Assert.Equal(0u, rig.Core.ReadVfpuControl(AllegrexCore.VfpuDPrefix));
    }

    [Fact]
    public void DotProductIsSummedExactlyThenRoundedOnce()
    {
        uint vdot = Op(0x19, 1);
        Assert.Equal(B(70), Run([One, Two, B(3), B(4)], [B(5), B(6), B(7), B(8)], vdot).Core.ReadVfpuRegister(Vd));
        // 1 + 2^-24 + 2^-24: one rounding of the exact sum gives 1 + 2^-23 (adding in turn would give 1)
        Assert.Equal(0x3F800001u, Run([One, 0x33800000, 0x33800000, 0], [One, One, One, 0], vdot).Core.ReadVfpuRegister(Vd));
        // the alignment truncates: 2^24 + 1 - 2^24 is 0
        Assert.Equal(0u, Run([0x4B800000, One, 0xCB800000, 0], [One, One, One, 0], vdot).Core.ReadVfpuRegister(Vd));
        // a zero sum is +0 whatever the signs
        Assert.Equal(0u, Run([B(-1), One, 0x80000000, 0], [One, One, One, 0], vdot).Core.ReadVfpuRegister(Vd));
        Assert.Equal(VfpuNaN, Run([Infinity, 0, 0, 0], [0, 0, 0, 0], vdot).Core.ReadVfpuRegister(Vd));     // inf * 0
        Assert.Equal(VfpuNaN, Run([Infinity, Infinity, 0, 0], [One, B(-1), 0, 0], vdot).Core.ReadVfpuRegister(Vd)); // inf - inf
        Assert.Equal(0xFF800000u, Run([Infinity, One, 0, 0], [B(-1), One, 0, 0], vdot).Core.ReadVfpuRegister(Vd));
    }

    [Fact]
    public void ScaleCrossDeterminantAndHomogeneousDot()
    {
        uint[] s = [One, Two, B(3), B(4)];
        uint[] t = [B(10), B(20), B(30), B(40)];
        Assert.Equal(new[] { B(10), B(20), B(30), B(40) }, Column(Run(s, t, Op(0x19, 2)).Core, Vd)); // vscl by t.x
        // vcrs.t: (s.y t.z, s.z t.x, s.x t.y)
        Assert.Equal(new[] { B(60), B(30), B(20) }, Column(Run(s, t, Op(0x19, 5, 3)).Core, Vd, 3));
        // vdet.p: s.x t.y - s.y t.x = 1*20 - 2*10 = 0; with t = (3, 4): 1*4 - 2*3 = -2
        Assert.Equal(B(-2), Run(s, [B(3), B(4), 0, 0], Op(0x19, 6, 2)).Core.ReadVfpuRegister(Vd));
        // vhdp.q: s.xyz . t.xyz + t.w = 10 + 40 + 90 + 40
        Assert.Equal(B(180), Run(s, t, Op(0x19, 4)).Core.ReadVfpuRegister(Vd));
    }

    [Theory]
    [InlineData(1u, 0b0101u)]  // EQ
    [InlineData(2u, 0b0010u)]  // LT
    [InlineData(3u, 0b0111u)]  // LE
    [InlineData(5u, 0b1010u)]  // NE
    [InlineData(7u, 0b1000u)]  // GT
    [InlineData(8u, 0b0001u)]  // EZ: the denormal counts as zero
    [InlineData(9u, 0b0000u)]  // EN
    [InlineData(4u, 0b1111u)]  // TR
    public void CompareSetsLaneAnyAndAllBits(uint condition, uint lanes)
    {
        uint[] s = [0x00000003, One, B(3), B(9)];
        uint[] t = [0, Two, B(3), B(4)];
        AllegrexRig rig = Run(s, t, Op(0x1B, 0) | condition);
        uint any = lanes != 0 ? 1u : 0u;
        uint all = lanes == 0xF ? 1u : 0u;
        Assert.Equal(lanes | (any << 4) | (all << 5), rig.Core.ReadVfpuControl(AllegrexCore.VfpuConditionCodes));
    }

    [Fact]
    public void CompareOfAPairKeepsTheUpperLaneBits()
    {
        AllegrexRig rig = new(Op(0x1B, 0, 2) | 4u); // vcmp.p TR
        rig.Core.WriteVfpuControl(AllegrexCore.VfpuConditionCodes, 0b001100);
        rig.Step();
        Assert.Equal(0b111111u, rig.Core.ReadVfpuControl(AllegrexCore.VfpuConditionCodes));
    }

    [Fact]
    public void MinMaxUseTheHardwareOrder()
    {
        uint[] s = [0x80000000, 0x00000001, B(-1), 0x7F800001];
        uint[] t = [0, 0x80000000, B(1), Infinity];
        // ties (zeros and denormals) return the second operand, bits kept; NaN sorts above inf
        Assert.Equal(new[] { 0u, 0x80000000u, B(-1), Infinity }, Column(Run(s, t, Op(0x1B, 2)).Core, Vd)); // vmin
        Assert.Equal(new[] { 0u, 0x80000000u, One, 0x7F800001u }, Column(Run(s, t, Op(0x1B, 3)).Core, Vd)); // vmax
    }

    [Fact]
    public void SetAndSignCompares()
    {
        uint[] s = [One, B(5), 0x7FC00000, B(-2)];
        uint[] t = [One, B(4), One, B(3)];
        Assert.Equal(new[] { One, One, 0u, 0u }, Column(Run(s, t, Op(0x1B, 6)).Core, Vd));      // vsge
        Assert.Equal(new[] { 0u, 0u, 0u, One }, Column(Run(s, t, Op(0x1B, 7)).Core, Vd));       // vslt
        Assert.Equal(new[] { 0u, One, One, B(-1) }, Column(Run(s, t, Op(0x1B, 5)).Core, Vd));   // vscmp (NaN by magnitude)
    }

    [Fact]
    public void UnaryMovesAndSaturations()
    {
        uint[] s = [B(-2), 0x80000000, B(0.25f), 0x7FC00001];
        uint[] none = [0, 0, 0, 0];
        Assert.Equal(s, Column(Run(s, none, Op34(0, 0)).Core, Vd));                                        // vmov
        Assert.Equal(new[] { Two, 0u, B(0.25f), 0x7FC00001u }, Column(Run(s, none, Op34(0, 1)).Core, Vd));    // vabs
        Assert.Equal(new[] { Two, 0u, B(-0.25f), 0xFFC00001u }, Column(Run(s, none, Op34(0, 2)).Core, Vd));   // vneg
        Assert.Equal(new[] { 0u, 0u, B(0.25f), 0x7FC00001u }, Column(Run(s, none, Op34(0, 4)).Core, Vd));     // vsat0
        Assert.Equal(new[] { B(-1), 0x80000000u, B(0.25f), 0x7FC00001u }, Column(Run(s, none, Op34(0, 5)).Core, Vd)); // vsat1
        Assert.Equal(new[] { 0u, 0u, 0u, 0u }, Column(Run(s, none, Op34(0, 6)).Core, Vd));                   // vzero
        Assert.Equal(new[] { One, One, One, One }, Column(Run(s, none, Op34(0, 7)).Core, Vd));               // vone
    }

    [Fact]
    public void IdentityRowFollowsTheRegister()
    {
        AllegrexRig rig = new(Op34(0, 3, 4, 0x21)); // vidt.q R010: the identity's row 1
        rig.Step();
        Assert.Equal(new[] { 0u, One, 0u, 0u }, new[] { rig.Core.ReadVfpuRegister(0x01), rig.Core.ReadVfpuRegister(0x21), rig.Core.ReadVfpuRegister(0x41), rig.Core.ReadVfpuRegister(0x61) });
    }

    [Theory]
    [InlineData(16u, 0x40000000u, 0x3F000000u)]  // vrcp 2
    [InlineData(16u, 0x00000000u, 0x7F800000u)]  // vrcp 0 = inf
    [InlineData(17u, 0x40800000u, 0x3F000000u)]  // vrsq 4
    [InlineData(18u, 0x3F800000u, 0x3F800000u)]  // vsin 1 (a quarter turn) = 1
    [InlineData(18u, 0xBF800000u, 0xBF800000u)]  // vsin -1
    [InlineData(18u, 0x3F000000u, 0x3F3504F3u)]  // vsin 0.5 = sin(pi/4)
    [InlineData(19u, 0x00000000u, 0x3F800000u)]  // vcos 0
    [InlineData(19u, 0x40000000u, 0xBF800000u)]  // vcos 2 = -1
    [InlineData(20u, 0x40400000u, 0x41000000u)]  // vexp2 3 = 8
    [InlineData(21u, 0x41000000u, 0x40400000u)]  // vlog2 8 = 3
    [InlineData(21u, 0x00000000u, 0xFF800000u)]  // vlog2 0 = -inf
    [InlineData(22u, 0x41100000u, 0x40400000u)]  // vsqrt 9 = 3
    [InlineData(22u, 0xBF800000u, 0x7F800001u)]  // vsqrt -1: the positive VFPU NaN
    [InlineData(23u, 0x3F800000u, 0x3F800000u)]  // vasin 1 = 1 (quarter turns)
    [InlineData(23u, 0xC0000000u, 0xFF800001u)]  // vasin -2: NaN with the input's sign
    [InlineData(24u, 0x40000000u, 0xBF000000u)]  // vnrcp 2
    [InlineData(26u, 0x3F800000u, 0xBF800000u)]  // vnsin 1
    [InlineData(28u, 0x3F800000u, 0x3F000000u)]  // vrexp2 1
    [InlineData(16u, 0x00000001u, 0x7F800000u)]  // a denormal input is zero
    public void Transcendentals(uint operation, uint input, uint expected)
    {
        AllegrexRig rig = new(Op34(0, operation, 1));
        rig.Core.WriteVfpuRegister(Vs, input);
        rig.Step();
        Assert.Equal(expected, rig.Core.ReadVfpuRegister(Vd));
    }

    [Fact]
    public void TranscendentalPrefixesReachTheLastLaneOnly()
    {
        // vrcp.p with S prefix lane 0 = -x: only the last lane (y) is negated
        AllegrexRig rig = new(Prefix(AllegrexCore.VfpuSPrefix, 0xE4 | (1u << 16)), Op34(0, 16, 2));
        SetColumn(rig.Core, Vs, Two, Two);
        rig.Step(2);
        Assert.Equal(new[] { Half, 0xBF000000u }, Column(rig.Core, Vd, 2));
    }

    [Theory]
    [InlineData(16u, 0, 0x40200000u, 2u)]           // vf2in 2.5 -> 2 (nearest even)
    [InlineData(16u, 0, 0x40600000u, 4u)]           // 3.5 -> 4
    [InlineData(17u, 0, 0xC0600000u, 0xFFFFFFFDu)]  // vf2iz -3.5 -> -3
    [InlineData(18u, 0, 0x3F800001u, 2u)]           // vf2iu
    [InlineData(19u, 0, 0xBF800001u, 0xFFFFFFFEu)]  // vf2id
    [InlineData(17u, 4, 0x3F800000u, 16u)]          // scaled by 2^4
    [InlineData(17u, 31, 0x3F800000u, 0x7FFFFFFFu)] // 2^31 saturates
    [InlineData(17u, 0, 0xFF800000u, 0x80000000u)]  // -inf
    [InlineData(17u, 0, 0x7FC00000u, 0x7FFFFFFFu)]  // NaN
    [InlineData(18u, 0, 0x00000001u, 0u)]           // vf2iu of a denormal: 0
    public void FloatToInteger(uint group, int scale, uint input, uint expected)
    {
        uint instruction = 0xD0000000u | (group << 21) | ((uint)scale << 16) | ((uint)Vs << 8) | Vd;
        AllegrexRig rig = new(instruction);
        rig.Core.WriteVfpuRegister(Vs, input);
        rig.Step();
        Assert.Equal(expected, rig.Core.ReadVfpuRegister(Vd));
    }

    [Fact]
    public void IntegerToFloatScales()
    {
        uint instruction = 0xD0000000u | (20u << 21) | (1u << 16) | ((uint)Vs << 8) | Vd;
        AllegrexRig rig = new(instruction);
        rig.Core.WriteVfpuRegister(Vs, unchecked((uint)-5));
        rig.Step();
        Assert.Equal(B(-2.5f), rig.Core.ReadVfpuRegister(Vd));
    }

    [Theory]
    [InlineData(0u, 6u, 0b0101u, new uint[] { 1, 0xDEAD0001, 3, 0xDEAD0003 })] // vcmovt per lane
    [InlineData(1u, 6u, 0b0101u, new uint[] { 0xDEAD0000, 2, 0xDEAD0002, 4 })] // vcmovf per lane
    [InlineData(0u, 2u, 0b0100u, new uint[] { 1, 2, 3, 4 })]                   // vcmovt on CC bit 2
    [InlineData(0u, 7u, 0b1111u, new uint[] { 0xDEAD0000, 0xDEAD0001, 0xDEAD0002, 0xDEAD0003 })] // bit 7 is never set
    [InlineData(1u, 7u, 0b0000u, new uint[] { 1, 2, 3, 4 })]
    public void ConditionalMove(uint moveIfFalse, uint imm3, uint codes, uint[] expected)
    {
        uint instruction = 0xD0000000u | (21u << 21) | (moveIfFalse << 19) | (imm3 << 16) | ((uint)Vs << 8) | Vd | 0x8080u;
        AllegrexRig rig = new(instruction);
        SetColumn(rig.Core, Vs, 1, 2, 3, 4);
        SetColumn(rig.Core, Vd, 0xDEAD0000, 0xDEAD0001, 0xDEAD0002, 0xDEAD0003);
        rig.Core.WriteVfpuControl(AllegrexCore.VfpuConditionCodes, codes);
        rig.Step();
        Assert.Equal(expected, Column(rig.Core, Vd));
    }

    [Fact]
    public void ConstantsFromVcst()
    {
        uint vcst = 0xD0600000u | (2u << 16) | Vd | 0x8080u; // sqrt(2)
        AllegrexRig rig = new(vcst, 0xD0600000u | (1u << 16) | 4u); // and FLT_MAX into S100
        rig.Step(2);
        Assert.Equal(new[] { 0x3FB504F3u, 0x3FB504F3u, 0x3FB504F3u, 0x3FB504F3u }, Column(rig.Core, Vd));
        Assert.Equal(0x7F7FFFFFu, rig.Core.ReadVfpuRegister(4));
    }

    [Fact]
    public void HalfConversionsOfVectors()
    {
        AllegrexRig toHalf = new(Op34(1, 18, 4));
        SetColumn(toHalf.Core, Vs, One, Two, B(-0.5f), 0x47800000);
        toHalf.Step();
        Assert.Equal(new[] { 0x40003C00u, 0x7C00B800u }, Column(toHalf.Core, Vd, 2));
        AllegrexRig fromHalf = new(Op34(1, 19, 2));
        SetColumn(fromHalf.Core, Vs, 0x40003C00, 0x7C00B800);
        fromHalf.Step();
        Assert.Equal(new[] { One, Two, B(-0.5f), Infinity }, Column(fromHalf.Core, Vd));
    }

    [Fact]
    public void ByteAndHalfIntegerConversions()
    {
        AllegrexRig rig = new(Op34(1, 24, 1), Op34(1, 25, 1, 4), Op34(1, 26, 1, 8), Op34(1, 27, 1, 12));
        rig.Core.WriteVfpuRegister(Vs, 0x80FF017F);
        rig.Step(4);
        Assert.Equal(new[] { 0x3FBFBFBFu, 0x00808080u, 0x7FFFFFFFu, 0x40404040u }, Column(rig.Core, 0)); // vuc2ifs
        Assert.Equal(new[] { 0x7F000000u, 0x01000000u, 0xFF000000u, 0x80000000u }, Column(rig.Core, 4)); // vc2i
        Assert.Equal(new[] { 0x00BF8000u, 0x407F8000u }, Column(rig.Core, 8, 2));                        // vus2i
        Assert.Equal(new[] { 0x017F0000u, 0x80FF0000u }, Column(rig.Core, 12, 2));                       // vs2i
    }

    [Fact]
    public void IntegerNarrowing()
    {
        AllegrexRig rig = new(Op34(1, 28, 4), Op34(1, 29, 4, 4), Op34(1, 30, 4, 8), Op34(1, 31, 4, 12));
        SetColumn(rig.Core, Vs, 0x7F800000, 0x00800000, 0x80000000, 0x12345678);
        rig.Step(4);
        Assert.Equal(0x240001FFu, rig.Core.ReadVfpuRegister(0));  // vi2uc: (v >> 23), negatives 0
        Assert.Equal(0x1280007Fu, rig.Core.ReadVfpuRegister(4));  // vi2c: top bytes
        Assert.Equal(new[] { 0x0100FF00u, 0x24680000u }, Column(rig.Core, 8, 2)); // vi2us
        Assert.Equal(new[] { 0x00807F80u, 0x12348000u }, Column(rig.Core, 12, 2)); // vi2s
    }

    [Fact]
    public void ColorConversion()
    {
        AllegrexRig rig = new(Op34(2, 27, 4));
        SetColumn(rig.Core, Vs, 0xFF0000FF, 0xFF00FF00, 0xFFFF0000, 0xFFFFFFFF);
        rig.Step();
        Assert.Equal(new[] { 0x07E0001Fu, 0xFFFFF800u }, Column(rig.Core, Vd, 2)); // vt5650
    }

    [Fact]
    public void RandomGeneratorIsDeterministicAndInRange()
    {
        uint seed = Op34(1, 0, 1, 0, 5);
        AllegrexRig first = new(seed, Op34(1, 2, 4), Op34(1, 3, 4, 4), Op34(1, 1, 4, 8));
        first.Core.WriteVfpuRegister(5, 0x12345678);
        first.Step(4);
        AllegrexRig second = new(seed, Op34(1, 2, 4), Op34(1, 3, 4, 4), Op34(1, 1, 4, 8));
        second.Core.WriteVfpuRegister(5, 0x12345678);
        second.Step(4);
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(F(first.Core.ReadVfpuRegister(i << 5)), 1f, 1.9999999f);
            Assert.InRange(F(first.Core.ReadVfpuRegister(4 | (i << 5))), 2f, 3.9999998f);
        }

        Assert.Equal(Column(first.Core, 8), Column(second.Core, 8));
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0x3F800000u, first.Core.ReadVfpuControl(AllegrexCore.VfpuRandom0 + i) & 0xFFF00000);
        }
    }

    [Fact]
    public void SeedSpreadsOverTheStateWords()
    {
        Span<uint> state = stackalloc uint[8];
        AllegrexVfpuMath.SeedRandom(0x87654321, state);
        Assert.Equal(0x3F814321u, state[0]);
        Assert.Equal(0x3F824321u, state[1]);
        Assert.Equal(0x3F858765u, state[4]);
        Assert.Equal(0x3F888765u, state[7]);
    }

    [Fact]
    public void SortButterflyAndComplements()
    {
        uint[] s = [B(4), B(1), B(3), B(2)];
        uint[] none = [0, 0, 0, 0];
        Assert.Equal(new[] { B(1), B(4), B(2), B(3) }, Column(Run(s, none, Op34(2, 0)).Core, Vd));    // vsrt1
        Assert.Equal(new[] { B(5), B(3), B(5), B(1) }, Column(Run(s, none, Op34(2, 2)).Core, Vd));    // vbfy1: x+y, x-y, z+w, z-w
        Assert.Equal(new[] { B(7), B(3), B(1), B(-1) }, Column(Run(s, none, Op34(2, 3)).Core, Vd));   // vbfy2
        Assert.Equal(new[] { B(-3), 0u, B(-2), B(-1) }, Column(Run(s, none, Op34(2, 4)).Core, Vd));   // vocp: 1 - x
        Assert.Equal(B(10), Run(s, none, Op34(2, 6)).Core.ReadVfpuRegister(Vd));                       // vfad
        Assert.Equal(B(2.5f), Run(s, none, Op34(2, 7)).Core.ReadVfpuRegister(Vd));                     // vavg
        uint[] signs = [B(-3), 0x00000001, B(2), 0xFFC00000];
        Assert.Equal(new[] { B(-1), 0u, One, B(-1) }, Column(Run(signs, none, Op34(2, 10)).Core, Vd)); // vsgn
    }

    [Fact]
    public void SignedOneComplementOfAPair()
    {
        AllegrexRig rig = new(Op34(2, 5, 2));
        SetColumn(rig.Core, Vs, B(0.25f), B(2));
        rig.Step();
        Assert.Equal(new[] { B(0.75f), B(0.25f), 0u, One }, Column(rig.Core, Vd));
    }

    [Fact]
    public void ExponentBitOperations()
    {
        AllegrexRig rig = new(Op34(1, 23, 1), Op34(1, 22, 1, 4), 0xD0000000u | (24u << 21) | (130u << 16) | ((uint)Vs << 8) | 8u, Op(0x18, 2, 1, 12, Vs, Vt));
        rig.Core.WriteVfpuRegister(Vs, B(12));     // 1.5 * 2^3
        rig.Core.WriteVfpuRegister(Vt, 1);         // vsbn: exponent 127 + 1
        rig.Step(4);
        Assert.Equal(B(3), rig.Core.ReadVfpuRegister(0));       // vlgb 12 = 3
        Assert.Equal(B(1.5f), rig.Core.ReadVfpuRegister(4));    // vsbz: mantissa in [1, 2)
        Assert.Equal(B(12), rig.Core.ReadVfpuRegister(8));      // vwbn to exponent 130 (same as 12's)
        Assert.Equal(B(3), rig.Core.ReadVfpuRegister(12));      // vsbn: 1.5 * 2^1
    }

    [Fact]
    public void MoveBetweenVectorAndControlRegisters()
    {
        uint vmtvc = 0xD0000000u | (2u << 21) | (17u << 16) | ((uint)Vs << 8) | AllegrexCore.VfpuConditionCodes;
        uint vmfvc = 0xD0000000u | (2u << 21) | (16u << 16) | ((uint)AllegrexCore.VfpuRevision << 8) | Vd;
        AllegrexRig rig = new(vmtvc, vmfvc);
        rig.Core.WriteVfpuRegister(Vs, 0xFFFFFFFF);
        rig.Step(2);
        Assert.Equal(0x3Fu, rig.Core.ReadVfpuControl(AllegrexCore.VfpuConditionCodes));
        Assert.Equal(0x7772CEABu, rig.Core.ReadVfpuRegister(Vd));
    }
}
