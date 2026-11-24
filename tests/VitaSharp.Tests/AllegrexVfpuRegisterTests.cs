using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// Allegrex VFPU register side: the S/C/R register views of the eight 4x4 matrices, lv/sv in every form, mfv/mtv and
/// the control registers with their write masks, the prefix registers, vflush/vnop, bvf/bvt, viim/vfim.
/// </summary>
public sealed class AllegrexVfpuRegisterTests
{
    private const uint Data = 0x88010000;

    public static uint LoadQuad(int vt, int baseRegister, int offset)
    {
        return 0xD8000000u | (uint)(baseRegister << 21) | (uint)((vt & 0x1F) << 16) | (uint)(offset & 0xFFFC) | (uint)((vt >> 5) & 1);
    }

    public static uint StoreQuad(int vt, int baseRegister, int offset)
    {
        return 0xF8000000u | (uint)(baseRegister << 21) | (uint)((vt & 0x1F) << 16) | (uint)(offset & 0xFFFC) | (uint)((vt >> 5) & 1);
    }

    private static uint LoadSingle(int vt, int baseRegister, int offset)
    {
        return 0xC8000000u | (uint)(baseRegister << 21) | (uint)((vt & 0x1F) << 16) | (uint)(offset & 0xFFFC) | (uint)((vt >> 5) & 3);
    }

    private static uint StoreSingle(int vt, int baseRegister, int offset)
    {
        return 0xE8000000u | (uint)(baseRegister << 21) | (uint)((vt & 0x1F) << 16) | (uint)(offset & 0xFFFC) | (uint)((vt >> 5) & 3);
    }

    private static uint Mfv(int rt, int register)
    {
        return 0x48600000u | (uint)(rt << 16) | (uint)register;
    }

    private static uint Mtv(int rt, int register)
    {
        return 0x48E00000u | (uint)(rt << 16) | (uint)register;
    }

    public static uint Prefix(int control, uint data)
    {
        return 0xDC000000u | ((uint)control << 24) | (data & 0xFFFFF);
    }

    private static AllegrexRig RigWithMatrixZero(params uint[] code)
    {
        AllegrexRig rig = new(code);
        // matrix 0: S(column)(row) = 0x10 * column + row + 1
        for (int column = 0; column < 4; column++)
        {
            for (int row = 0; row < 4; row++)
            {
                rig.Core.WriteVfpuRegister(column | (row << 5), (uint)((0x10 * column) + row + 1));
            }
        }

        rig[T0] = Data;
        return rig;
    }

    private static uint[] Stored(AllegrexRig rig)
    {
        uint[] words = new uint[4];
        for (int i = 0; i < 4; i++)
        {
            words[i] = rig.Bus.Read32((Data + (uint)(i * 4)) & AllegrexCore.PhysicalMask);
        }

        return words;
    }

    [Theory]
    [InlineData(0x00, new uint[] { 0x01, 0x02, 0x03, 0x04 })] // C000: column 0, rows 0..3
    [InlineData(0x01, new uint[] { 0x11, 0x12, 0x13, 0x14 })] // C010: column 1
    [InlineData(0x20, new uint[] { 0x01, 0x11, 0x21, 0x31 })] // R000: row 0, columns 0..3
    [InlineData(0x22, new uint[] { 0x03, 0x13, 0x23, 0x33 })] // R020: row 2, columns 0..3
    public void QuadViewsOfAMatrix(int register, uint[] expected)
    {
        AllegrexRig rig = RigWithMatrixZero(StoreQuad(register, T0, 0));
        rig.Step();
        Assert.Equal(expected, Stored(rig));
    }

    [Fact]
    public void SingleRegisterNumbersNameColumnAndRow()
    {
        AllegrexRig rig = RigWithMatrixZero(Mfv(T1, 0x01), Mfv(T2, 0x20), Mfv(A0, 0x63), Mfv(A1, 0x7F));
        rig.Core.WriteVfpuRegister(0x7F, 0xABCD);
        rig.Step(4);
        Assert.Equal(0x11u, rig[T1]);    // S010: column 1, row 0
        Assert.Equal(0x02u, rig[T2]);    // S001: column 0, row 1
        Assert.Equal(0x34u, rig[A0]);    // S033: column 3, row 3
        Assert.Equal(0xABCDu, rig[A1]);  // S733
    }

    [Fact]
    public void LoadAndStoreQuadRoundTripAndIgnoreTheDMask()
    {
        AllegrexRig rig = new(Prefix(AllegrexCore.VfpuDPrefix, 0xF00), LoadQuad(0x05, T0, 0), StoreQuad(0x25, T0, 16));
        rig.Bus.Load(Data, [1, 2, 3, 4]);
        rig[T0] = Data;
        rig.Step(3);
        Assert.Equal(0xF00u, rig.Core.ReadVfpuControl(AllegrexCore.VfpuDPrefix)); // still pending
        // C110 got 1..4 (all lanes, despite the mask); R110... stored back through its row view
        Assert.Equal(1u, rig.Core.ReadVfpuRegister(0x05));
        Assert.Equal(4u, rig.Core.ReadVfpuRegister(0x65));
    }

    [Fact]
    public void LoadQuadNeedsSixteenByteAlignment()
    {
        AllegrexRig rig = new(LoadQuad(0, T0, 4));
        rig[T0] = Data;
        rig.Step();
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
        Assert.Equal(Data + 4, rig.Core.ReadCop0(AllegrexCore.Cop0BadVAddr));
    }

    [Fact]
    public void LvrAndLvlLoadAnUnalignedQuad()
    {
        // ulv.q at Data + 4: lvr.q fills lanes 0..2 from Data+4..Data+12, lvl.q lane 3 from Data+16
        uint lvr = 0xD4000002u | ((uint)T0 << 21) | 4u;
        uint lvl = 0xD4000000u | ((uint)T0 << 21) | 16u;
        AllegrexRig rig = new(lvr, lvl, StoreQuad(0, T1, 0));
        rig.Bus.Load(Data, [10, 11, 12, 13, 14, 15, 16, 17]);
        rig[T0] = Data;
        rig[T1] = Data + 0x100;
        rig.Step(3);
        uint[] words = new uint[4];
        for (int i = 0; i < 4; i++)
        {
            words[i] = rig.Bus.Read32((Data + 0x100 + (uint)(i * 4)) & AllegrexCore.PhysicalMask);
        }

        Assert.Equal(new uint[] { 11, 12, 13, 14 }, words);
    }

    [Fact]
    public void SvlAndSvrStoreAnUnalignedQuad()
    {
        uint svr = 0xF4000002u | ((uint)T0 << 21) | 4u;
        uint svl = 0xF4000000u | ((uint)T0 << 21) | 16u;
        AllegrexRig rig = new(svr, svl);
        rig.Bus.Load(Data, [0, 0, 0, 0, 0, 0, 0, 0]);
        for (int row = 0; row < 4; row++)
        {
            rig.Core.WriteVfpuRegister(row << 5, (uint)(row + 21));
        }

        rig[T0] = Data;
        rig.Step(2);
        uint[] words = new uint[6];
        for (int i = 0; i < 6; i++)
        {
            words[i] = rig.Bus.Read32((Data + (uint)(i * 4)) & AllegrexCore.PhysicalMask);
        }

        Assert.Equal(new uint[] { 0, 21, 22, 23, 24, 0 }, words);
    }

    [Fact]
    public void LoadAndStoreSingle()
    {
        AllegrexRig rig = new(LoadSingle(0x65, T0, 8), StoreSingle(0x65, T0, 12), LoadSingle(0, T1, 0));
        rig.Bus.Load(Data, [0, 0, 0x3F800000, 0]);
        rig[T0] = Data;
        rig[T1] = Data + 2;
        rig.Step(3);
        Assert.Equal(0x3F800000u, rig.Core.ReadVfpuRegister(0x65));
        Assert.Equal(0x3F800000u, rig.Bus.Read32((Data + 12) & AllegrexCore.PhysicalMask));
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc); // the misaligned lv.s
    }

    [Fact]
    public void MtvAndMfvMoveRegistersAndControls()
    {
        AllegrexRig rig = new(
            Mtv(T0, 0x12),
            Mfv(T1, 0x12),
            Mtv(T2, 128 + AllegrexCore.VfpuDPrefix),
            Mfv(A0, 128 + AllegrexCore.VfpuDPrefix),
            Mtv(T2, 128 + AllegrexCore.VfpuConditionCodes),
            Mfv(A1, 128 + AllegrexCore.VfpuConditionCodes),
            Mtv(T2, 128 + AllegrexCore.VfpuRevision),
            Mfv(A2, 128 + AllegrexCore.VfpuRevision),
            Mtv(T2, 128 + AllegrexCore.VfpuRandom0),
            Mfv(A3, 128 + AllegrexCore.VfpuRandom0));
        rig[T0] = 0xCAFEBABE;
        rig[T2] = 0xFFFFFFFF;
        rig.Step(10);
        Assert.Equal(0xCAFEBABEu, rig[T1]);
        Assert.Equal(0xFFFu, rig[A0]);           // D prefix: 12 bits
        Assert.Equal(0x3Fu, rig[A1]);            // CC: 6 bits
        Assert.Equal(0x7772CEABu, rig[A2]);      // the revision is read-only
        Assert.Equal(0x3F8FFFFFu, rig[A3]);      // random state: 20 bits, 0x3F800000 always set
    }

    [Fact]
    public void ResetControlValues()
    {
        AllegrexCore core = new(new AllegrexTestBus());
        Assert.Equal(0xE4u, core.ReadVfpuControl(AllegrexCore.VfpuSPrefix));
        Assert.Equal(0xE4u, core.ReadVfpuControl(AllegrexCore.VfpuTPrefix));
        Assert.Equal(0u, core.ReadVfpuControl(AllegrexCore.VfpuDPrefix));
        Assert.Equal(0x3Fu, core.ReadVfpuControl(AllegrexCore.VfpuConditionCodes));
        Assert.Equal(0x3F800001u, core.ReadVfpuControl(AllegrexCore.VfpuRandom0));
        Assert.Equal(0x3F800008u, core.ReadVfpuControl(AllegrexCore.VfpuRandom0 + 3));
        Assert.Equal(0x3F800000u, core.ReadVfpuControl(AllegrexCore.VfpuRandom0 + 7));
    }

    [Fact]
    public void PrefixInstructionsSetTheirRegisters()
    {
        AllegrexRig rig = new(Prefix(0, 0xFFFFF), Prefix(1, 0x12345), Prefix(2, 0xFFFFF));
        rig.Step(3);
        Assert.Equal(0xFFFFFu, rig.Core.ReadVfpuControl(AllegrexCore.VfpuSPrefix));
        Assert.Equal(0x12345u, rig.Core.ReadVfpuControl(AllegrexCore.VfpuTPrefix));
        Assert.Equal(0xFFFu, rig.Core.ReadVfpuControl(AllegrexCore.VfpuDPrefix));
    }

    [Theory]
    [InlineData(0xFFFF0000u, false)] // vnop keeps the prefixes
    [InlineData(0xFFFF0320u, false)] // vsync too (top half 0xFFFF)
    [InlineData(0xFFFF040Du, false)] // vflush too
    [InlineData(0xFC000000u, true)]  // the rest of opcode 0x3F resets them
    public void FlushFamilyKeepsPrefixesOnlyWithTopHalfFFFF(uint instruction, bool resets)
    {
        AllegrexRig rig = new(Prefix(0, 0x55), instruction);
        rig.Step(2);
        Assert.Equal(resets ? 0xE4u : 0x55u, rig.Core.ReadVfpuControl(AllegrexCore.VfpuSPrefix));
    }

    [Theory]
    [InlineData(0, 0x00u, true)]   // bvf, bit 0 clear: taken
    [InlineData(1, 0x01u, true)]   // bvt, bit 0 set
    [InlineData(1, 0x00u, false)]
    [InlineData(3, 0x00u, false)]  // bvtl not taken: slot annulled
    public void ConditionCodeBranches(int type, uint conditionCodes, bool taken)
    {
        uint branch = 0x49000000u | (0u << 18) | ((uint)type << 16) | 3u;
        AllegrexRig rig = new(branch, Addiu(T1, T1, 1), Nop, Nop, Nop);
        rig.Core.WriteVfpuControl(AllegrexCore.VfpuConditionCodes, conditionCodes);
        rig.Step(2);
        bool likely = type >= 2;
        Assert.Equal(taken ? AllegrexRig.CodeBase + 16 : AllegrexRig.CodeBase + (likely ? 12u : 8u), rig.Core.Pc);
        Assert.Equal(!likely || taken ? 1u : 0u, rig[T1]);
    }

    [Fact]
    public void BranchTestsTheSelectedConditionBit()
    {
        uint bvtBit5 = 0x49000000u | (5u << 18) | (1u << 16) | 3u;
        AllegrexRig rig = new(bvtBit5, Nop, Nop, Nop, Nop);
        rig.Core.WriteVfpuControl(AllegrexCore.VfpuConditionCodes, 0x20);
        rig.Step(2);
        Assert.Equal(AllegrexRig.CodeBase + 16, rig.Core.Pc);
    }

    [Fact]
    public void ViimAndVfimLoadImmediates()
    {
        uint viim = 0xDF000000u | (0x03u << 16) | 0xFFFEu;  // S030 = -2.0
        uint vfim = 0xDF800000u | (0x23u << 16) | 0x3C00u;  // S031 = half 1.0
        uint vfimSubnormal = 0xDF800000u | (0x43u << 16) | 0x8001u; // a subnormal half: -0
        AllegrexRig rig = new(viim, vfim, vfimSubnormal, Prefix(AllegrexCore.VfpuDPrefix, 1), viim);
        rig.Step(5);
        Assert.Equal(0x3F800000u, rig.Core.ReadVfpuRegister(0x23));
        Assert.Equal(0x80000000u, rig.Core.ReadVfpuRegister(0x43));
        Assert.Equal(0x00000000u, rig.Core.ReadVfpuRegister(0x03)); // the last viim saturated to [0, 1]
        Assert.Equal(0u, rig.Core.ReadVfpuControl(AllegrexCore.VfpuDPrefix)); // and ate the prefix
    }

    [Theory]
    [InlineData((ushort)0x3C00, 0x3F800000u)]
    [InlineData((ushort)0xC000, 0xC0000000u)]
    [InlineData((ushort)0x7C00, 0x7F800000u)]
    [InlineData((ushort)0x7E01, 0x7F800201u)]   // NaN: mantissa bits stay in place
    [InlineData((ushort)0x0001, 0x00000000u)]   // subnormal: zero
    [InlineData((ushort)0x7BFF, 0x477FE000u)]   // 65504
    public void HalfToSingle(ushort half, uint expected)
    {
        Assert.Equal(expected, AllegrexVfpuMath.HalfToSingle(half));
    }

    [Theory]
    [InlineData(0x3F800000u, (ushort)0x3C00)]
    [InlineData(0x3F801FFFu, (ushort)0x3C00)]   // truncated, not rounded
    [InlineData(0x38800000u, (ushort)0x0400)]   // 2^-14: the smallest normal half
    [InlineData(0x38000000u, (ushort)0x0000)]   // 2^-15: below it, zero
    [InlineData(0x47800000u, (ushort)0x7C00)]   // 65536: inf
    [InlineData(0xFF800000u, (ushort)0xFC00)]
    [InlineData(0x7FC00000u, (ushort)0x7C00)]   // a NaN with the low ten bits clear becomes inf
    [InlineData(0x7F800155u, (ushort)0x7D55)]
    public void SingleToHalf(uint bits, ushort expected)
    {
        Assert.Equal(expected, AllegrexVfpuMath.SingleToHalf(bits));
    }
}
