using VitaSharp.Core.Hle.Audio.Atrac9;

namespace VitaSharp.Tests;

/// <summary>
/// The ATRAC9 IMDCT and PCM conversion were rewritten for Mono's AOT on phones (pointers instead of bounds-checked
/// arrays, no Math.Floor per sample): they must give exactly the results of the original code.
/// </summary>
public class Atrac9MdctTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Dct4MatchesTheOriginal(int bits)
    {
        int n = 1 << bits;
        var window = new double[n];
        for (int i = 0; i < n; i++)
        {
            window[i] = Math.Sin(Math.PI * (i + 0.5) / n);
        }

        var mdct = new Mdct(bits, window, 1.0 / 32768);
        var rng = new Random(bits);
        for (int round = 0; round < 20; round++)
        {
            var input = new double[n];
            for (int i = 0; i < n; i++)
            {
                input[i] = (rng.NextDouble() - 0.5) * 65536;
            }

            var fast = new double[n];
            var reference = new double[n];
            mdct.Dct4ForTest(input, fast);
            mdct.Dct4Reference(input, reference);
            Assert.Equal(reference, fast);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.49)]
    [InlineData(0.5)]
    [InlineData(-0.5)]
    [InlineData(-0.51)]
    [InlineData(-1.5)]
    [InlineData(1.5)]
    [InlineData(-2.4999)]
    [InlineData(32766.5)]
    [InlineData(32767.0)]
    [InlineData(40000.0)]
    [InlineData(-32768.0)]
    [InlineData(-32768.5)]
    [InlineData(-32769.0)]
    [InlineData(-1e9)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void PcmRoundingMatchesFloorPlusHalf(double sample)
    {
        short expected = Helpers.Clamp16((int)Math.Floor(sample + 0.5));
        Assert.Equal(expected, Atrac9Decoder.RoundToPcm16(sample));
    }

    [Fact]
    public void PcmRoundingMatchesOnManyValues()
    {
        var rng = new Random(7);
        for (int i = 0; i < 100_000; i++)
        {
            double s = (rng.NextDouble() - 0.5) * 80000;
            Assert.Equal(Helpers.Clamp16((int)Math.Floor(s + 0.5)), Atrac9Decoder.RoundToPcm16(s));
        }
    }
}
