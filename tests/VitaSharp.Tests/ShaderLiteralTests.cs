using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>Float literals emitted by the USSE translator.</summary>
public class ShaderLiteralTests
{
    [Theory]
    // a U16x2 / U8x4 constant of all ones (LittleBigPlanet clears its U16 shadow map with 0xFFFFFFFF): a NaN as a float
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x7FC00000u)]
    [InlineData(0x7F800001u)]
    [InlineData(0xFFC0FFFFu)]
    public void NaNLiteralsKeepTheirBits(uint bits)
    {
        float f = BitConverter.UInt32BitsToSingle(bits);
        Assert.Equal($"uintBitsToFloat(0x{bits:X8}u)", UsseTranslator.F(f));
    }

    [Fact]
    public void OrdinaryLiterals()
    {
        Assert.Equal("1.0", UsseTranslator.F(1f));
        Assert.Equal("-0.5", UsseTranslator.F(-0.5f));
        Assert.Equal("uintBitsToFloat(0x7F800000u)", UsseTranslator.F(float.PositiveInfinity));
        Assert.Equal("uintBitsToFloat(0x80000000u)", UsseTranslator.F(-0f));
    }
}
