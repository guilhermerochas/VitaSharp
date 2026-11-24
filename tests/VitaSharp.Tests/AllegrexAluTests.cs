using VitaSharp.Core.Compat.Allegrex;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>Allegrex integer ALU: register and immediate arithmetic, logic, compares, shifts and rotates.</summary>
public sealed class AllegrexAluTests
{
    [Theory]
    [InlineData(0x21, 0x7FFFFFFFu, 1u, 0x80000000u)]       // addu wraps, never traps
    [InlineData(0x21, 0xFFFFFFFFu, 1u, 0u)]
    [InlineData(0x23, 0x80000000u, 1u, 0x7FFFFFFFu)]       // subu wraps
    [InlineData(0x23, 0u, 1u, 0xFFFFFFFFu)]
    [InlineData(0x20, 5u, 0xFFFFFFFDu, 2u)]                // add, no overflow
    [InlineData(0x20, 0x7FFFFFFFu, 0x80000000u, 0xFFFFFFFFu)] // add of opposite signs never overflows
    [InlineData(0x22, 0x80000000u, 0x80000000u, 0u)]       // sub, no overflow
    [InlineData(0x24, 0xF0F0F0F0u, 0xFF00FF00u, 0xF000F000u)] // and
    [InlineData(0x25, 0xF0F0F0F0u, 0x0F000000u, 0xFFF0F0F0u)] // or
    [InlineData(0x26, 0xF0F0F0F0u, 0xFFFFFFFFu, 0x0F0F0F0Fu)] // xor
    [InlineData(0x27, 0xF0F0F0F0u, 0x0000000Fu, 0x0F0F0F00u)] // nor
    [InlineData(0x2A, 0xFFFFFFFFu, 0u, 1u)]                // slt: -1 < 0
    [InlineData(0x2A, 0u, 0xFFFFFFFFu, 0u)]
    [InlineData(0x2B, 0xFFFFFFFFu, 0u, 0u)]                // sltu: 0xFFFFFFFF > 0
    [InlineData(0x2B, 0u, 0xFFFFFFFFu, 1u)]
    public void RegisterArithmetic(int funct, uint left, uint right, uint expected)
    {
        Assert.Equal(expected, AllegrexRig.Evaluate(RType(funct, T2, T0, T1), left, right));
    }

    [Theory]
    [InlineData(0x20, 0x7FFFFFFFu, 1u)]          // add: positive overflow
    [InlineData(0x20, 0x80000000u, 0xFFFFFFFFu)] // add: negative overflow
    [InlineData(0x22, 0x80000000u, 1u)]          // sub: negative overflow
    [InlineData(0x22, 0x7FFFFFFFu, 0xFFFFFFFFu)] // sub: positive overflow
    public void SignedOverflowTrapsAndKeepsTheDestination(int funct, uint left, uint right)
    {
        AllegrexRig rig = new(RType(funct, T2, T0, T1));
        rig[T0] = left;
        rig[T1] = right;
        rig[T2] = 0x12345678;
        rig.Step();
        Assert.Equal(0x12345678u, rig[T2]);
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);
        Assert.Equal((uint)AllegrexExceptionCode.Overflow << 2, rig.Core.Cause & AllegrexCore.CauseExceptionCodeMask);
        Assert.Equal(AllegrexRig.CodeBase, rig.Core.Epc);
        Assert.NotEqual(0u, rig.Core.Status & AllegrexCore.StatusExl);
    }

    [Fact]
    public void AddiOverflowTrapsAddiuWraps()
    {
        AllegrexRig rig = new(IType(0x08, T1, T0, 1), Addiu(T2, T0, 1));
        rig[T0] = 0x7FFFFFFF;
        rig[T1] = 7;
        rig.Step();
        Assert.Equal(7u, rig[T1]);
        Assert.Equal(AllegrexRig.ExceptionBase, rig.Core.Pc);

        AllegrexRig wrap = new(Addiu(T2, T0, 1), IType(0x08, T1, T0, -1));
        wrap[T0] = 0x7FFFFFFF;
        wrap.Step(2);
        Assert.Equal(0x80000000u, wrap[T2]);
        Assert.Equal(0x7FFFFFFEu, wrap[T1]);
    }

    [Theory]
    [InlineData(0x09, 0xFFFFu, 5u, 4u)]             // addiu sign-extends
    [InlineData(0x0A, 0xFFFFu, 0xFFFFFFFEu, 1u)]    // slti: -2 < -1
    [InlineData(0x0A, 0x0001u, 0xFFFFFFFFu, 1u)]
    [InlineData(0x0B, 0xFFFFu, 5u, 1u)]             // sltiu compares with the sign-extended value, unsigned
    [InlineData(0x0B, 0x0001u, 0xFFFFFFFFu, 0u)]
    [InlineData(0x0C, 0xFFFFu, 0xFFFF1234u, 0x1234u)] // andi zero-extends
    [InlineData(0x0D, 0x8000u, 0xFFFF0000u, 0xFFFF8000u)] // ori zero-extends
    [InlineData(0x0E, 0xFFFFu, 0xFFFFFFFFu, 0xFFFF0000u)] // xori zero-extends
    public void ImmediateArithmetic(int opcode, uint immediate, uint source, uint expected)
    {
        AllegrexRig rig = new(IType(opcode, T1, T0, (int)immediate));
        rig[T0] = source;
        rig.Step();
        Assert.Equal(expected, rig[T1]);
    }

    [Fact]
    public void LuiLoadsTheUpperHalfAndR0StaysZero()
    {
        AllegrexRig rig = new(Lui(T0, 0x8800), Lui(Zero, 0x1234), Addu(T1, Zero, T0));
        rig.Step(3);
        Assert.Equal(0x88000000u, rig[T0]);
        Assert.Equal(0u, rig[Zero]);
        Assert.Equal(0x88000000u, rig[T1]);
    }

    [Theory]
    [InlineData(0x00, 0, 4, 0x80000001u, 0x00000010u)]  // sll
    [InlineData(0x00, 0, 31, 1u, 0x80000000u)]
    [InlineData(0x02, 0, 4, 0x80000010u, 0x08000001u)]  // srl
    [InlineData(0x02, 1, 4, 0x80000011u, 0x18000001u)]  // rotr (rs bit 0)
    [InlineData(0x02, 3, 4, 0x80000011u, 0x18000001u)]  // only bit 0 of rs is decoded
    [InlineData(0x02, 2, 4, 0x80000011u, 0x08000001u)]  // rs = 2 is still srl
    [InlineData(0x02, 1, 0, 0x80000011u, 0x80000011u)]  // rotr by 0
    [InlineData(0x03, 0, 4, 0x80000010u, 0xF8000001u)]  // sra
    [InlineData(0x03, 0, 31, 0x80000000u, 0xFFFFFFFFu)]
    public void ImmediateShifts(int funct, int rsField, int amount, uint value, uint expected)
    {
        Assert.Equal(expected, AllegrexRig.Evaluate(RType(funct, T2, rsField, T1, amount), 0, value));
    }

    [Theory]
    [InlineData(0x04, 0, 36u, 1u, 0x10u)]               // sllv uses the low 5 bits
    [InlineData(0x06, 0, 4u, 0x80000010u, 0x08000001u)] // srlv
    [InlineData(0x06, 1, 4u, 0x80000011u, 0x18000001u)] // rotrv (sa bit 0)
    [InlineData(0x06, 1, 36u, 0x80000011u, 0x18000001u)] // rotrv masks the amount
    [InlineData(0x06, 2, 4u, 0x80000011u, 0x08000001u)] // sa = 2 is still srlv
    [InlineData(0x07, 0, 33u, 0x80000000u, 0xC0000000u)] // srav
    public void VariableShifts(int funct, int saField, uint amount, uint value, uint expected)
    {
        Assert.Equal(expected, AllegrexRig.Evaluate(RType(funct, T2, T0, T1, saField), amount, value));
    }
}
