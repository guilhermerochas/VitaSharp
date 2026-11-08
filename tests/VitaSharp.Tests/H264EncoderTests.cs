using VitaSharp.Core.Media;

namespace VitaSharp.Tests;

public class H264EncoderTests
{
    static readonly byte[] Sps = [0x67, 0x42, 0xC0, 0x1E, 0x8C, 0x68, 0x0A, 0x03, 0xDA, 0x01, 0xE1, 0x10, 0x8D, 0x40];
    static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];

    [Fact]
    public void ParameterSetsFromAnnexB()
    {
        byte[] extra = [0, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps];
        var ps = H264Encoder.ParseParameterSets(extra);
        Assert.NotNull(ps);
        Assert.Equal(Sps, ps.Value.Sps);
        Assert.Equal(Pps, ps.Value.Pps);
    }

    [Fact]
    public void ParameterSetsFromAvcC()
    {
        byte[] extra = [1, 0x42, 0xC0, 0x1E, 0xFF, 0xE1, 0, (byte)Sps.Length, .. Sps, 1, 0, (byte)Pps.Length, .. Pps];
        var ps = H264Encoder.ParseParameterSets(extra);
        Assert.NotNull(ps);
        Assert.Equal(Sps, ps.Value.Sps);
        Assert.Equal(Pps, ps.Value.Pps);
    }

    [Fact]
    public void ParameterSetsNeedBoth()
    {
        Assert.Null(H264Encoder.ParseParameterSets([0, 0, 0, 1, .. Sps]));
        Assert.Null(H264Encoder.ParseParameterSets([]));
    }
}
