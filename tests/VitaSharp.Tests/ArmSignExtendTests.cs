using VitaSharp.Core.Cpu;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>Arm.SignExtend, which every branch offset goes through (its shift-pair form was miscompiled by Mono).</summary>
public sealed class ArmSignExtendTests
{
    [Theory]
    [InlineData(0x7F8u, 11, 0xFFFFFFF8u)] // Thumb B E7F8: -8 halfwords
    [InlineData(0x3F8u, 11, 0x3F8u)]
    [InlineData(0x80u, 8, 0xFFFFFF80u)]
    [InlineData(0x7Fu, 8, 0x7Fu)]
    [InlineData(0xFFFFFFu, 24, 0xFFFFFFFFu)]
    [InlineData(0x800000u, 24, 0xFF800000u)]
    [InlineData(0x80000000u, 32, 0x80000000u)]
    [InlineData(0xFFFFF7F8u, 11, 0xFFFFFFF8u)] // bits above the field are ignored
    public void SignExtends(uint v, int bits, uint expected) => Assert.Equal(expected, Arm.SignExtend(v, bits));

    [Fact]
    public void MatchesTheShiftDefinition()
    {
        for (int bits = 1; bits <= 32; bits++)
            for (uint v = 0; v < 4096; v += 7)
            {
                uint x = v * 2654435761u;
                Assert.Equal((uint)(((int)(x << (32 - bits))) >> (32 - bits)), Arm.SignExtend(x, bits));
            }
    }
}
