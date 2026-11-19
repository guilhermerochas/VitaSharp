using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceIftuForDriver (lowio 3.74): the plane ranges of each entry, the input and output format sets, the output's
/// alignment and its refusal while the plane is on, alpha and merge on planes 1 and 3, enable and disable.
/// </summary>
[Collection("guest")]
public sealed class IftuKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Plane = unchecked((int)0x803F0700), Value = unchecked((int)0x803F0701);
    private const int Format = unchecked((int)0x803F0703), Enabled = unchecked((int)0x803F0704);

    private readonly HleHarness _harness = new(fixture, "iftuk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x0FCBF457u)]
    [InlineData(0x357EAE24u)]
    [InlineData(0xD64F4C6Bu)]
    [InlineData(0xC11F30B3u)]
    [InlineData(0x0D7C02F7u)]
    [InlineData(0x7CE0C4DAu)]
    [InlineData(0xAF19FD85u)]
    [InlineData(0xE6EE2C6Bu)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceIftuForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void ThePlaneRangesDifferByEntry()
    {
        uint block = _harness.Alloc(0x60);
        Assert.Equal(0, Call(Services, "IftuSetConversion", 4u, block));
        Assert.Equal(Plane, Call(Services, "IftuSetConversion", 5u, block));
        Assert.Equal(0, Call(Services, "IftuSetConversion2", 4u, 0u));
        Assert.Equal(Plane, Call(Services, "ksceIftuEnable", 4u));
        Assert.Equal(Plane, Call(Services, "ksceIftuDisable", 4u));
        Assert.Equal(Plane, Call(Services, "ksceIftuSetOutputFormat", 4u, 0u, 0u, 0x10u));
        Assert.Equal(Plane, Call(Services, "IftuSetAlpha", 0u, 0x80u));
        Assert.Equal(Plane, Call(Services, "IftuSetAlpha", 2u, 0x80u));
        Assert.Equal(Plane, Call(Services, "ksceIftuSetMergeSetting", 4u, 0u));
        Assert.Equal(0, Call(Services, "ksceIftuSetMergeSetting", 3u, 0x80u));
    }

    [Fact]
    public void TheFormatsAreChecked()
    {
        uint frameBuffer = _harness.Alloc(0x54);
        GuestMemory.W32(frameBuffer, 3);
        Assert.Equal(Format, Call(Services, "ksceIftuSetInputFrameBuffer", 0u, frameBuffer, 0, 0));
        GuestMemory.W32(frameBuffer, 0x80140000);
        Assert.Equal(0, Call(Services, "ksceIftuSetInputFrameBuffer", 0u, frameBuffer, 0, 0));
        Assert.Equal(0x80140000u, Services.IftuPlane(0).InputFormat);
        Assert.Equal(0, Call(Services, "ksceIftuSetInputFrameBuffer", 0u, 0u, -1, 0)); // the kept one again
        Assert.Equal(0x80140000u, Services.IftuPlane(0).InputFormat);

        Assert.Equal(Value, Call(Services, "ksceIftuSetOutputFormat", 0u, 968u, 544u, 0x10u)); // pitch not by 16
        Assert.Equal(Value, Call(Services, "ksceIftuSetOutputFormat", 0u, 960u, 543u, 0x10u));
        Assert.Equal(Format, Call(Services, "ksceIftuSetOutputFormat", 0u, 960u, 544u, 0x11u));
        Assert.Equal(0, Call(Services, "ksceIftuSetOutputFormat", 0u, 960u, 544u, 0x2000u));
        Assert.Equal(0x2000u, Services.IftuPlane(0).OutputFormat);
    }

    [Fact]
    public void AnEnabledPlaneRefusesItsOutputFormat()
    {
        Assert.Equal(0, Call(Services, "ksceIftuEnable", 1u));
        Assert.Equal(2, Services.IftuPlane(1).State);
        Assert.Equal(0, Call(Services, "ksceIftuEnable", 1u));
        Assert.Equal(Enabled, Call(Services, "ksceIftuSetOutputFormat", 1u, 960u, 544u, 0x10u));
        Assert.Equal(Value, Call(Services, "IftuSetAlpha", 1u, 0x101u));
        Assert.Equal(0, Call(Services, "IftuSetAlpha", 1u, 0x100u));
        Assert.Equal(0x100u, Services.IftuPlane(1).Alpha);
        Assert.Equal(0, Call(Services, "ksceIftuDisable", 1u));
        Assert.Equal(0, Call(Services, "ksceIftuDisable", 1u));
        Assert.Equal(0, Call(Services, "ksceIftuSetOutputFormat", 1u, 960u, 544u, 0x10u));
    }
}
