using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexVfpuRegisterTests;

namespace VitaSharp.Tests;

/// <summary>Allegrex VFPU matrix instructions: vmmul, vtfm/vhtfm, vmscl, vcrsp/vqmul, vmmov, vmidt/vmzero/vmone, vrot.</summary>
public sealed class AllegrexVfpuMatrixTests
{
    private const uint One = 0x3F800000;

    private static uint B(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

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

    private static uint Op3C(uint group, int size, int vd, int vs, int vt)
    {
        return 0xF0000000u | (group << 21) | ((uint)vt << 16) | ((uint)vs << 8) | (uint)vd | SizeBits(size);
    }

    /// <summary>Sets column <paramref name="column"/> of matrix <paramref name="matrix"/>.</summary>
    private static void SetColumn(AllegrexCore core, int matrix, int column, params float[] lanes)
    {
        for (int i = 0; i < lanes.Length; i++)
        {
            core.WriteVfpuRegister((matrix * 4) | column | (i << 5), B(lanes[i]));
        }
    }

    private static float[] GetColumn(AllegrexCore core, int matrix, int column, int size)
    {
        float[] lanes = new float[size];
        for (int i = 0; i < size; i++)
        {
            lanes[i] = BitConverter.UInt32BitsToSingle(core.ReadVfpuRegister((matrix * 4) | column | (i << 5)));
        }

        return lanes;
    }

    [Fact]
    public void MatrixMultiplyDotsTheSourceVectorsWithTheTargetVectors()
    {
        AllegrexRig rig = new(Op3C(0, 2, 0, 4, 8)); // vmmul.p M000, M100, M200
        SetColumn(rig.Core, 1, 0, 1, 2);
        SetColumn(rig.Core, 1, 1, 3, 4);
        SetColumn(rig.Core, 2, 0, 5, 6);
        SetColumn(rig.Core, 2, 1, 7, 8);
        rig.Step();
        Assert.Equal(new float[] { 17, 39 }, GetColumn(rig.Core, 0, 0, 2));
        Assert.Equal(new float[] { 23, 53 }, GetColumn(rig.Core, 0, 1, 2));
    }

    [Fact]
    public void MatrixMultiplyOfFourByFourWithTheIdentity()
    {
        AllegrexRig rig = new(Op3C(28, 4, 4, 0, 0) | (3u << 16), Op3C(0, 4, 0, 4, 8)); // vmidt.q M100; vmmul.q M000, M100, M200
        for (int column = 0; column < 4; column++)
        {
            SetColumn(rig.Core, 2, column, column, column + 10, column + 20, column + 30);
        }

        rig.Step(2);
        for (int column = 0; column < 4; column++)
        {
            Assert.Equal(new float[] { column, column + 10, column + 20, column + 30 }, GetColumn(rig.Core, 0, column, 4));
        }
    }

    [Fact]
    public void TransformByAMatrix()
    {
        AllegrexRig rig = new(Op3C(12, 4, 0, 4, 8)); // vtfm4.q C000, M100, C200
        SetColumn(rig.Core, 1, 0, 1, 0, 0, 0);
        SetColumn(rig.Core, 1, 1, 0, 2, 0, 0);
        SetColumn(rig.Core, 1, 2, 0, 0, 3, 0);
        SetColumn(rig.Core, 1, 3, 1, 1, 1, 1);
        SetColumn(rig.Core, 2, 0, 1, 1, 1, 1);
        rig.Step();
        Assert.Equal(new float[] { 1, 2, 3, 4 }, GetColumn(rig.Core, 0, 0, 4));
    }

    [Fact]
    public void HomogeneousTransformReadsTheMissingComponentAsOne()
    {
        AllegrexRig rig = new(Op3C(12, 3, 0, 4, 8)); // vhtfm4.t: a 4x4 matrix, a triple
        SetColumn(rig.Core, 1, 0, 1, 0, 0, 0);
        SetColumn(rig.Core, 1, 1, 0, 2, 0, 0);
        SetColumn(rig.Core, 1, 2, 0, 0, 3, 0);
        SetColumn(rig.Core, 1, 3, 1, 1, 1, 1);
        SetColumn(rig.Core, 2, 0, 1, 2, 3);
        rig.Step();
        Assert.Equal(new float[] { 1, 4, 9, 7 }, GetColumn(rig.Core, 0, 0, 4));
    }

    [Fact]
    public void MatrixScale()
    {
        AllegrexRig rig = new(Op3C(16, 2, 0, 4, 8)); // vmscl.p M000, M100, S200
        SetColumn(rig.Core, 1, 0, 1, 2);
        SetColumn(rig.Core, 1, 1, 3, 4);
        SetColumn(rig.Core, 2, 0, 10);
        rig.Step();
        Assert.Equal(new float[] { 10, 20 }, GetColumn(rig.Core, 0, 0, 2));
        Assert.Equal(new float[] { 30, 40 }, GetColumn(rig.Core, 0, 1, 2));
    }

    [Fact]
    public void CrossProductAndQuaternionProduct()
    {
        AllegrexRig cross = new(Op3C(20, 3, 0, 4, 8)); // vcrsp.t: x cross y = z
        SetColumn(cross.Core, 1, 0, 1, 0, 0);
        SetColumn(cross.Core, 2, 0, 0, 1, 0);
        cross.Step();
        Assert.Equal(new float[] { 0, 0, 1 }, GetColumn(cross.Core, 0, 0, 3));

        AllegrexRig quaternion = new(Op3C(20, 4, 0, 4, 8)); // vqmul.q: q * identity = q
        SetColumn(quaternion.Core, 1, 0, 1, 2, 3, 4);
        SetColumn(quaternion.Core, 2, 0, 0, 0, 0, 1);
        quaternion.Step();
        Assert.Equal(new float[] { 1, 2, 3, 4 }, GetColumn(quaternion.Core, 0, 0, 4));
    }

    [Fact]
    public void MatrixMoveAndFills()
    {
        AllegrexRig rig = new(
            Op3C(28, 2, 0, 4, 0),             // vmmov.p M000, M100
            Op3C(28, 4, 8, 0, 0) | (6u << 16), // vmzero.q M200
            Op3C(28, 3, 12, 0, 0) | (7u << 16)); // vmone.t M300
        SetColumn(rig.Core, 1, 0, 5, 6);
        SetColumn(rig.Core, 1, 1, 7, 8);
        SetColumn(rig.Core, 2, 3, 9, 9, 9, 9);
        rig.Step(3);
        Assert.Equal(new float[] { 5, 6 }, GetColumn(rig.Core, 0, 0, 2));
        Assert.Equal(new float[] { 7, 8 }, GetColumn(rig.Core, 0, 1, 2));
        Assert.Equal(new float[] { 0, 0, 0, 0 }, GetColumn(rig.Core, 2, 3, 4));
        Assert.Equal(new float[] { 1, 1, 1 }, GetColumn(rig.Core, 3, 2, 3));
    }

    [Fact]
    public void MatrixMoveTransposes()
    {
        AllegrexRig rig = new(Op3C(28, 2, 0, 0x24, 0)); // vmmov.p M000, E100
        SetColumn(rig.Core, 1, 0, 1, 2);
        SetColumn(rig.Core, 1, 1, 3, 4);
        rig.Step();
        Assert.Equal(new float[] { 1, 3 }, GetColumn(rig.Core, 0, 0, 2));
        Assert.Equal(new float[] { 2, 4 }, GetColumn(rig.Core, 0, 1, 2));
    }

    [Fact]
    public void MatrixOpsLeaveTheDPrefixToTheLastVector()
    {
        // vmone.p with a D mask on lane 0: only the last vector's lane 0 keeps its old value
        AllegrexRig rig = new(Prefix(AllegrexCore.VfpuDPrefix, 1u << 8), Op3C(28, 2, 0, 0, 0) | (7u << 16));
        SetColumn(rig.Core, 0, 0, 5, 5);
        SetColumn(rig.Core, 0, 1, 5, 5);
        rig.Step(2);
        Assert.Equal(new float[] { 1, 1 }, GetColumn(rig.Core, 0, 0, 2));
        Assert.Equal(new float[] { 5, 1 }, GetColumn(rig.Core, 0, 1, 2));
    }

    [Fact]
    public void RotationRow()
    {
        // vrot.q C000, S100, [c, s, 0, 0] at angle 0
        AllegrexRig rig = new(0xF3A00000u | (4u << 16) | (4u << 8) | 0u | SizeBits(4));
        rig.Core.WriteVfpuRegister(4, 0);
        rig.Step();
        Assert.Equal(new float[] { 1, 0, 0, 0 }, GetColumn(rig.Core, 0, 0, 4));

        // [c, 0, -s, 0] at a quarter turn: the sine lane is -1, the cosine lane 0
        AllegrexRig negated = new(0xF3A00000u | ((0x10u | (2u << 2)) << 16) | (4u << 8) | 0u | SizeBits(4));
        negated.Core.WriteVfpuRegister(4, One);
        negated.Step();
        float[] row = GetColumn(negated.Core, 0, 0, 4);
        Assert.Equal(0f, Math.Abs(row[0]));
        Assert.Equal(new float[] { 0, -1, 0 }, new[] { row[1], row[2], row[3] });

        // the same sine and cosine lane: the sine everywhere, then the cosine in its lane
        AllegrexRig same = new(0xF3A00000u | (5u << 16) | (4u << 8) | 0u | SizeBits(4));
        same.Core.WriteVfpuRegister(4, B(0.5f));
        same.Step();
        uint diagonal = 0x3F3504F0; // the PSP's sin and cos of an eighth turn (pspautotests cpu/vfpu/exact)
        Assert.Equal(diagonal, same.Core.ReadVfpuRegister(0));
        Assert.Equal(diagonal, same.Core.ReadVfpuRegister(0x20));
    }
}
