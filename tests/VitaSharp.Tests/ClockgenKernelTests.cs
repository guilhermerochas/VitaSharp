using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceClockgenForDriver (SceClockgen 3.74) without the chip on I2C: the registers read as 0 (no wireless clock, no
/// second clock bit, no spread table), each enable/disable flips its bit of register 0x81 and returns the previous
/// value, the register, rate and spread checks, the reread failing as the bus does.
/// </summary>
[Collection("guest")]
public sealed class ClockgenKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "clockk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x06663ECAu)]
    [InlineData(0x09072366u)]
    [InlineData(0x0BA41732u)]
    [InlineData(0x5130682Du)]
    [InlineData(0x62478916u)]
    [InlineData(0x66AC5580u)]
    [InlineData(0x7B28A724u)]
    [InlineData(0x820D3516u)]
    [InlineData(0x82F39378u)]
    [InlineData(0xA2CA8B65u)]
    [InlineData(0xB6F0A532u)]
    [InlineData(0xCF63B11Du)]
    [InlineData(0xEFE8A68Cu)]
    [InlineData(0xF0A0242Du)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceClockgenForDriver").ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void WithoutTheChipEveryRegisterIsZero()
    {
        Assert.Equal(0, Call(Services, "ksceClockgenGetChipId"));
        Assert.Equal(0, Call(Services, "ksceClockgenGetRegister", 2u));
        Assert.Equal(unchecked((int)0x803F0500), Call(Services, "ksceClockgenGetRegister", 3u));
        Assert.Equal(unchecked((int)0x803F0380), Call(Services, "ksceClockgenReadRegisters"));
        Assert.Equal(1, Services.ClockgenReadMode);
        Assert.Equal(0, Call(Services, "ksceClockgenSetReadMode", 0u));
        Assert.Equal(0, Services.ClockgenReadMode);
        Assert.Equal(0, Call(Services, "ksceClockgenWlanBtClkEnable"));
        Assert.Equal(0, Call(Services, "ksceClockgenWlanBtClkDisable"));
        Assert.Equal(0, Call(Services, "ksceClockgenMotionClkEnable"));
        Assert.Equal(0, Call(Services, "ksceClockgenMotionClkDisable"));
        Assert.Equal(0, Services.ClockgenRegisterValues[1]);
    }

    [Fact]
    public void EachClockFlipsItsBitAndReturnsThePreviousValue()
    {
        Assert.Equal(unchecked((int)0x803F0501), Call(Services, "ksceClockgenSetAudioRate", 32000u));
        Assert.Equal(0, Call(Services, "ksceClockgenSetAudioRate", 48000u));
        Assert.Equal(1, Call(Services, "ksceClockgenSetAudioRate", 44100u));
        Assert.Equal(0, Call(Services, "ksceClockgenAudioClkEnable"));
        Assert.Equal(1, Call(Services, "ksceClockgenAudioClkEnable"));
        Assert.Equal(0, Call(Services, "ksceClockgenCameraClkEnable"));
        Assert.Equal(0x18, Call(Services, "ksceClockgenGetRegister", 1u));
        Assert.Equal(1, Call(Services, "ksceClockgenAudioClkDisable"));
        Assert.Equal(1, Call(Services, "ksceClockgenCameraClkDisable"));
        Assert.Equal(0, Call(Services, "ksceClockgenCameraClkDisable"));
        Assert.Equal(0, Call(Services, "ksceClockgenGetRegister", 1u));
    }

    [Fact]
    public void TheSpreadNeedsTheChipsTable()
    {
        Assert.Equal(unchecked((int)0x803F0502), Call(Services, "ksceClockgenSetSpread", 10));
        Assert.Equal(-1, Call(Services, "ksceClockgenSetSpread", -1));
    }
}
