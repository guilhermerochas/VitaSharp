using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>Allegrex loads and stores: sign/zero extension, segment mirrors, alignment exceptions, lwl/lwr/swl/swr, ll/sc.</summary>
public sealed class AllegrexMemoryTests
{
    const uint Data = 0x88010000;

    static AllegrexRig RigWithData(uint instruction, uint word)
    {
        AllegrexRig rig = new(instruction);
        rig.Bus.Load(Data, [word]);
        rig[T0] = Data;
        return rig;
    }

    [Theory]
    [InlineData(0x20, 0, 0xFFFFFF80u)] // lb sign-extends
    [InlineData(0x24, 0, 0x00000080u)] // lbu zero-extends
    [InlineData(0x20, 3, 0x00000012u)]
    [InlineData(0x21, 0, 0xFFFF8080u)] // lh
    [InlineData(0x25, 0, 0x00008080u)] // lhu
    [InlineData(0x25, 2, 0x00001234u)]
    [InlineData(0x23, 0, 0x12348080u)] // lw
    public void LoadsExtendByWidth(int opcode, int offset, uint expected)
    {
        AllegrexRig rig = RigWithData(IType(opcode, T1, T0, offset), 0x12348080);
        rig.Step();
        Assert.Equal(expected, rig[T1]);
    }

    [Theory]
    [InlineData(0x88010000u)] // kseg0
    [InlineData(0xA8010000u)] // kseg1 (uncached)
    [InlineData(0x08010000u)] // kuseg
    [InlineData(0x48010000u)] // the uncached user mirror
    public void SegmentsMirrorTheSamePhysicalWord(uint address)
    {
        AllegrexRig rig = new(Lw(T1, T0, 0));
        rig.Bus.Load(Data, [0xCAFEF00D]);
        rig[T0] = address;
        rig.Step();
        Assert.Equal(0xCAFEF00Du, rig[T1]);
    }

    [Fact]
    public void StoresWriteOnlyTheirWidth()
    {
        AllegrexRig rig = new(IType(0x28, T1, T0, 0), IType(0x29, T1, T0, 2), Sw(T1, T0, 4));
        rig[T0] = Data;
        rig[T1] = 0xAABBCCDD;
        rig.Bus.Load(Data, [0x11111111, 0x22222222]);
        rig.Step(3);
        Assert.Equal(0xCCDD11DDu, rig.Bus.Read32(Data & AllegrexCore.PhysicalMask));
        Assert.Equal(0xAABBCCDDu, rig.Bus.Read32((Data + 4) & AllegrexCore.PhysicalMask));
    }

    [Theory]
    [InlineData(0x21, 1, AllegrexExceptionCode.AddressErrorLoad)]  // lh
    [InlineData(0x25, 3, AllegrexExceptionCode.AddressErrorLoad)]  // lhu
    [InlineData(0x23, 2, AllegrexExceptionCode.AddressErrorLoad)]  // lw
    [InlineData(0x30, 1, AllegrexExceptionCode.AddressErrorLoad)]  // ll
    [InlineData(0x29, 1, AllegrexExceptionCode.AddressErrorStore)] // sh
    [InlineData(0x2B, 2, AllegrexExceptionCode.AddressErrorStore)] // sw
    [InlineData(0x38, 3, AllegrexExceptionCode.AddressErrorStore)] // sc
    public void MisalignedAccessRaisesAddressError(int opcode, int offset, AllegrexExceptionCode code)
    {
        AllegrexRig rig = RigWithData(IType(opcode, T1, T0, offset), 0x12345678);
        rig[T1] = 0x55555555;
        rig.Step();
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
        Assert.Equal((uint)code << 2, rig.Core.Cause & AllegrexCore.CauseExceptionCodeMask);
        Assert.Equal(Data + (uint)offset, rig.Core.ReadCop0(AllegrexCore.Cop0BadVAddr));
        Assert.Equal(0x55555555u, rig[T1]);
        Assert.Equal(0, rig.Bus.Writes);
    }

    [Theory]
    [InlineData(0x22, 0, 0x44BBCCDDu)] // lwl
    [InlineData(0x22, 1, 0x3344CCDDu)]
    [InlineData(0x22, 2, 0x223344DDu)]
    [InlineData(0x22, 3, 0x11223344u)]
    [InlineData(0x26, 0, 0x11223344u)] // lwr
    [InlineData(0x26, 1, 0xAA112233u)]
    [InlineData(0x26, 2, 0xAABB1122u)]
    [InlineData(0x26, 3, 0xAABBCC11u)]
    public void UnalignedLoadHalves(int opcode, int offset, uint expected)
    {
        AllegrexRig rig = RigWithData(IType(opcode, T1, T0, offset), 0x11223344);
        rig[T1] = 0xAABBCCDD;
        rig.Step();
        Assert.Equal(expected, rig[T1]);
    }

    [Theory]
    [InlineData(0x2A, 0, 0x112233AAu)] // swl
    [InlineData(0x2A, 1, 0x1122AABBu)]
    [InlineData(0x2A, 2, 0x11AABBCCu)]
    [InlineData(0x2A, 3, 0xAABBCCDDu)]
    [InlineData(0x2E, 0, 0xAABBCCDDu)] // swr
    [InlineData(0x2E, 1, 0xBBCCDD44u)]
    [InlineData(0x2E, 2, 0xCCDD3344u)]
    [InlineData(0x2E, 3, 0xDD223344u)]
    public void UnalignedStoreHalves(int opcode, int offset, uint expected)
    {
        AllegrexRig rig = RigWithData(IType(opcode, T1, T0, offset), 0x11223344);
        rig[T1] = 0xAABBCCDD;
        rig.Step();
        Assert.Equal(expected, rig.Bus.Read32(Data & AllegrexCore.PhysicalMask));
    }

    [Fact]
    public void LwlLwrPairLoadsAnUnalignedWord()
    {
        // the classic ulw: lwl t1, 3(t0); lwr t1, 0(t0) at Data + 1
        AllegrexRig rig = new(IType(0x22, T1, T0, 3), IType(0x26, T1, T0, 0));
        rig.Bus.Load(Data, [0x44332211, 0x88776655]);
        rig[T0] = Data + 1;
        rig.Step(2);
        Assert.Equal(0x55443322u, rig[T1]);
    }

    [Fact]
    public void StoreConditionalSucceedsAfterLoadLinked()
    {
        AllegrexRig rig = new(IType(0x30, T1, T0, 0), Addiu(T1, T1, 1), IType(0x38, T1, T0, 0));
        rig.Bus.Load(Data, [41]);
        rig[T0] = Data;
        rig.Step(3);
        Assert.Equal(1u, rig[T1]);
        Assert.Equal(42u, rig.Bus.Read32(Data & AllegrexCore.PhysicalMask));
    }

    [Fact]
    public void StoreConditionalFailsWithoutLoadLinkedOrAfterEret()
    {
        AllegrexRig rig = new(IType(0x38, T1, T0, 0));
        rig.Bus.Load(Data, [7]);
        rig[T0] = Data;
        rig[T1] = 99;
        rig.Step();
        Assert.Equal(0u, rig[T1]);
        Assert.Equal(7u, rig.Bus.Read32(Data & AllegrexCore.PhysicalMask));

        // ll, then an exception return in between: the LL bit is gone
        AllegrexRig interrupted = new(IType(0x30, T1, T0, 0), Eret);
        interrupted.Bus.Load(AllegrexRig.CodeBase + 0x100, [IType(0x38, T1, T0, 0)]);
        interrupted.Bus.Load(Data, [7]);
        interrupted[T0] = Data;
        interrupted.Core.Epc = AllegrexRig.CodeBase + 0x100;
        interrupted.Core.Status = AllegrexCore.StatusExl;
        interrupted.Step(3);
        Assert.Equal(0u, interrupted[T1]);
        Assert.Equal(7u, interrupted.Bus.Read32(Data & AllegrexCore.PhysicalMask));
    }
}
