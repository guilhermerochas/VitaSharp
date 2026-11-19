using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceUsbMassForDriver (SceUsbMass 3.74): no USB stick attached (0x80010013 from the block entries), and the
/// sub-interrupt bits of interrupt 0xF (both enabled at start; other interrupts 0x80010016; the poll answers -1).
/// </summary>
[Collection("guest")]
public sealed class UsbMassKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NoDevice = unchecked((int)0x80010013), BadInterrupt = unchecked((int)0x80010016);

    private readonly HleHarness _harness = new(fixture, "umassk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x081CA197u)]
    [InlineData(0x3C821E99u)]
    [InlineData(0x7833D935u)]
    [InlineData(0xB80D1DF8u)]
    [InlineData(0xD989A9F6u)]
    [InlineData(0xF2BAB182u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceUsbMassForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void WithoutAStickTheBlockEntriesAnswerNoDevice()
    {
        uint buffer = _harness.Alloc(0x200);
        Assert.Equal(NoDevice, Call(Services, "UsbMassGetDevInfo", buffer));
        Assert.Equal(NoDevice, Call(Services, "UsbMassReadSector", 0u, buffer, 1u));
        Assert.Equal(NoDevice, Call(Services, "UsbMassWriteSector", 0u, buffer, 0u));
    }

    [Fact]
    public void TheSubInterruptsOfInterrupt15AreKept()
    {
        Assert.Equal(3u, Services.UsbMassEnabled);
        Assert.Equal(BadInterrupt, Call(Services, "UsbMassDisableSubInterrupt", 14, 0u));
        Assert.Equal(0, Call(Services, "UsbMassDisableSubInterrupt", 15, 0u));
        Assert.Equal(2u, Services.UsbMassEnabled);
        Assert.Equal(0, Call(Services, "UsbMassEnableSubInterrupt", 15, 4u));
        Assert.Equal(0x12u, Services.UsbMassEnabled);
        Assert.Equal(BadInterrupt, Call(Services, "UsbMassEnableSubInterrupt", 0, 4u));
        Assert.Equal(-1, Call(Services, "UsbMassPollInterrupt", 15));
        Assert.Equal(BadInterrupt, Call(Services, "UsbMassPollInterrupt", 1));
    }
}
