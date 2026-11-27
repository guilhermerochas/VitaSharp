using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCsiForDriver and SceGrabForDriver (lowio 3.74): the two CSI-2 receivers' port and format checks and their
/// start/stop state, and the grabber's secure monitor calls, as documented defaults (the emulated unit has neither).
/// </summary>
[Collection("guest")]
public sealed class CsiKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Port = unchecked((int)0x803F0900), Format = unchecked((int)0x803F0901);

    private readonly HleHarness _harness = new(fixture, "csik");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x10545393u)]
    [InlineData(0x2AFEA1B0u)]
    [InlineData(0x4AE39F26u)]
    [InlineData(0xA1D1805Du)]
    [InlineData(0xB508822Bu)]
    [InlineData(0xFC165297u)]
    public void EveryExportHasADefault(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceCsiForDriver").ToList();
        Assert.Single(bindings);
        Assert.True(bindings[0].Default);
    }

    [Fact]
    public void EveryEntryChecksThePort()
    {
        uint timing = _harness.Alloc(0x2C);
        Assert.Equal(Port, Call(Services, "CsiSetCallback", 2u, 0u, 0u));
        Assert.Equal(Port, Call(Services, "CsiSetTiming", 2u, timing));
        Assert.Equal(Port, Call(Services, "CsiStartReceiver", 2u, 0u));
        Assert.Equal(Port, Call(Services, "CsiStopReceiver", 2u));
        Assert.Equal(Port, Call(Services, "CsiStartDma", 2u, 0u));
        Assert.Equal(Port, Call(Services, "CsiStopDma", 2u));
        Assert.Equal(0, Call(Services, "CsiSetCallback", 1u, 0x81000000u, 5u));
        Assert.Equal(0, Call(Services, "CsiSetTiming", 1u, timing));
    }

    [Theory]
    [InlineData(0x00u, true)]
    [InlineData(0x08u, true)]
    [InlineData(0x12u, true)]
    [InlineData(0x2030u, true)]
    [InlineData(0x40u, true)]
    [InlineData(0x01u, false)]
    [InlineData(0x13u, false)]
    [InlineData(0x20u, false)]
    public void TheReceiverTakesItsFormats(uint format, bool taken)
    {
        Assert.Equal(taken ? 0 : Format, Call(Services, "CsiStartReceiver", 0u, format));
        Assert.Equal(taken ? (int)format : -1, Services.CsiPortState(0).Format);
    }

    [Fact]
    public void TheReceiverAndTheDmaStartAndStop()
    {
        Call(Services, "CsiStartReceiver", 1u, 0x10u);
        Assert.Equal(0, Call(Services, "CsiStartDma", 1u, 0x81000000u));
        Assert.Equal((0x10, true), Services.CsiPortState(1));
        Assert.Equal((-1, false), Services.CsiPortState(0));
        Assert.Equal(0, Call(Services, "CsiStopDma", 1u));
        Assert.Equal(0, Call(Services, "CsiStopReceiver", 1u));
        Assert.Equal((-1, false), Services.CsiPortState(1));
    }

    [Theory]
    [InlineData(0x379130B2u)]
    [InlineData(0x072B8D93u)]
    [InlineData(0xF13C63DDu)]
    [InlineData(0xB22EE8BCu)]
    [InlineData(0x1F292554u)]
    [InlineData(0x5F36ABC4u)]
    [InlineData(0x188BBCC8u)]
    public void TheGrabberCallsAreAcceptedDefaults(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceGrabForDriver").ToList();
        Assert.Single(bindings);
        Assert.True(bindings[0].Default);
        Assert.Equal(0, Call(Services, "GrabSecureMonitorCall"));
    }

    /// <summary>ksceGrabSetMemoryBankAddress is not a default: it maps a bank of the PSP compat core's RAM (the
    /// grabber's bank register), which SceCompat's InitEx programs (CompatImportHookTests).</summary>
    [Fact]
    public void TheGrabberBankCallMapsTheCompatCoresBank()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == 0xE9C25A28u && b.Library == "SceGrabForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }
}
