using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>The Allegrex additions: ext/ins, seb/seh/wsbh/wsbw/bitrev (with the partial sa decode), clz/clo, max/min, movz/movn.</summary>
public sealed class AllegrexExtensionTests
{
    static uint Ext(int rt, int rs, int position, int size)
    {
        return Special3(0x00, size - 1, rs, rt, position);
    }

    static uint Ins(int rt, int rs, int position, int size)
    {
        return Special3(0x04, position + size - 1, rs, rt, position);
    }

    [Theory]
    [InlineData(0, 32, 0x89ABCDEFu, 0x89ABCDEFu)]  // the whole word
    [InlineData(4, 8, 0x89ABCDEFu, 0xDEu)]
    [InlineData(31, 1, 0x80000000u, 1u)]           // top bit
    [InlineData(0, 1, 0xFFFFFFFEu, 0u)]
    [InlineData(16, 16, 0x89ABCDEFu, 0x89ABu)]
    [InlineData(28, 8, 0xF0000000u, 0xFu)]         // pos + size > 32: only the bits that exist
    public void Extract(int position, int size, uint source, uint expected)
    {
        AllegrexRig rig = new(Ext(T1, T0, position, size));
        rig[T0] = source;
        rig[T1] = 0xFFFFFFFF;
        rig.Step();
        Assert.Equal(expected, rig[T1]);
    }

    [Theory]
    [InlineData(0, 32, 0x12345678u, 0xFFFFFFFFu, 0x12345678u)]
    [InlineData(4, 8, 0x000000ABu, 0xFFFFFFFFu, 0xFFFFFABFu)]
    [InlineData(31, 1, 0x00000001u, 0x00000000u, 0x80000000u)]
    [InlineData(0, 1, 0xFFFFFFFEu, 0xFFFFFFFFu, 0xFFFFFFFEu)]  // only the low bit of rs
    [InlineData(16, 16, 0xFFFF1234u, 0x0000ABCDu, 0x1234ABCDu)]
    public void Insert(int position, int size, uint source, uint destination, uint expected)
    {
        AllegrexRig rig = new(Ins(T1, T0, position, size));
        rig[T0] = source;
        rig[T1] = destination;
        rig.Step();
        Assert.Equal(expected, rig[T1]);
    }

    [Fact]
    public void InsertWithMsbBelowPositionChangesNothingAboveMsb()
    {
        // msb = 3, pos = 8: MIPS32 calls it unpredictable; the mask built from msb then moved down by pos is empty
        AllegrexRig rig = new(Special3(0x04, 3, T0, T1, 8));
        rig[T0] = 0xFFFFFFFF;
        rig[T1] = 0x12345678;
        rig.Step();
        Assert.Equal(0x12345678u, rig[T1]);
    }

    [Theory]
    [InlineData(0x10, 0x00000080u, 0xFFFFFF80u)] // seb
    [InlineData(0x10, 0x1234567Fu, 0x0000007Fu)]
    [InlineData(0x18, 0x00008000u, 0xFFFF8000u)] // seh
    [InlineData(0x18, 0xFFFF7FFFu, 0x00007FFFu)]
    [InlineData(0x02, 0x11223344u, 0x22114433u)] // wsbh
    [InlineData(0x03, 0x11223344u, 0x44332211u)] // wsbw
    [InlineData(0x14, 0x00000001u, 0x80000000u)] // bitrev
    [InlineData(0x14, 0x12345678u, 0x1E6A2C48u)]
    [InlineData(0x00, 0x11223344u, 0x22114433u)] // partial decode: bit 4 clear, bit 0 clear = wsbh
    [InlineData(0x0F, 0x11223344u, 0x44332211u)] // bit 4 clear, bit 0 set = wsbw
    [InlineData(0x1C, 0x00000001u, 0x80000000u)] // bit 4 and bit 2 = bitrev
    [InlineData(0x19, 0x00008000u, 0xFFFF8000u)] // bit 4 and bit 3 = seh
    [InlineData(0x11, 0x00000080u, 0xFFFFFF80u)] // bit 4 alone = seb
    public void BitOperations(int operation, uint value, uint expected)
    {
        Assert.Equal(expected, AllegrexRig.Evaluate(Special3(0x20, T2, 0, T1, operation), 0, value));
    }

    [Theory]
    [InlineData(0x16, 0u, 32u)]           // clz of 0
    [InlineData(0x16, 1u, 31u)]
    [InlineData(0x16, 0x80000000u, 0u)]
    [InlineData(0x17, 0xFFFFFFFFu, 32u)]  // clo of all ones
    [InlineData(0x17, 0xF0000000u, 4u)]
    [InlineData(0x17, 0x7FFFFFFFu, 0u)]
    public void CountLeading(int funct, uint value, uint expected)
    {
        Assert.Equal(expected, AllegrexRig.Evaluate(RType(funct, T2, T0, T1), value, 0));
    }

    [Theory]
    [InlineData(0x2C, 0xFFFFFFFFu, 1u, 1u)]          // max is signed
    [InlineData(0x2C, 0x80000000u, 0x7FFFFFFFu, 0x7FFFFFFFu)]
    [InlineData(0x2D, 0xFFFFFFFFu, 1u, 0xFFFFFFFFu)] // min is signed
    [InlineData(0x2D, 0x80000000u, 0x7FFFFFFFu, 0x80000000u)]
    public void MaxMin(int funct, uint left, uint right, uint expected)
    {
        Assert.Equal(expected, AllegrexRig.Evaluate(RType(funct, T2, T0, T1), left, right));
    }

    [Theory]
    [InlineData(0x0A, 0u, 0x11u)]   // movz moves when rt == 0
    [InlineData(0x0A, 5u, 0xDEADBEEFu)]
    [InlineData(0x0B, 5u, 0x11u)]   // movn moves when rt != 0
    [InlineData(0x0B, 0u, 0xDEADBEEFu)]
    public void ConditionalMoves(int funct, uint condition, uint expected)
    {
        Assert.Equal(expected, AllegrexRig.Evaluate(RType(funct, T2, T0, T1), 0x11, condition));
    }
}
