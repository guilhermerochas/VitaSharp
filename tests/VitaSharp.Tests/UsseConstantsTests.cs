using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Tests;

/// <summary>The USSE constant banks the shader translator reads (SGX543 fixed constants, the table Vita3K's
/// usse_constant_table.h lists): spot checks of entries whose values are mathematical constants.</summary>
public sealed class UsseConstantsTests
{
    static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    [Fact]
    public void F32BankHoldsTheKnownConstants()
    {
        var b = UsseConstants.F32Bank0;
        Assert.Equal(0f, F(b[0]));
        Assert.Equal(1f, F(b[2]));
        Assert.Equal(2f, F(b[4]));
        Assert.Equal(0.5f, F(b[12]));
        Assert.Equal(MathF.E, F(b[20]), 6);
        Assert.Equal(MathF.Sqrt(2), F(b[21]), 6);
        Assert.Equal(MathF.PI, F(b[22]), 6);
        Assert.Equal(MathF.PI / 4, F(b[23]), 6);
        Assert.Equal(2 * MathF.PI, F(b[24]), 6);
        // sin taylor terms: 1/2, 1/48 (0.5^3/6), 0.5^5/120
        Assert.Equal(0.5f, F(b[31]));
        Assert.Equal(0.125f / 6, F(b[30]), 6);
        Assert.NotEmpty(UsseConstants.F32Bank1);
        // the half-precision banks: as many entries in each
        Assert.All(new[] { UsseConstants.F16Bank1, UsseConstants.F16Bank2, UsseConstants.F16Bank3 }, bank => Assert.Equal(UsseConstants.F16Bank0.Length, bank.Length));
    }
}
