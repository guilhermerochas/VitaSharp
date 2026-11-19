using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceGpsForDriver (SceGps 3.74): driver registration by type (1..4, the functions not NULL, a type once), the
/// primary driver (the lowest type), the per-state answers of the data entries (state 1 without a driver, 2 with one;
/// no GPS session ever starts on the emulated unit), unregistering, and the driver events.
/// </summary>
[Collection("guest")]
public sealed class GpsKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80390020), NoDriver = unchecked((int)0x80390002);
    private const int Registered = unchecked((int)0x80390003), NotStarted = unchecked((int)0x80390004);

    private readonly HleHarness _harness = new(fixture, "gpsk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x092AD9BFu)]
    [InlineData(0x54DBA451u)]
    [InlineData(0x7F001760u)]
    [InlineData(0x884A5867u)]
    [InlineData(0x8D5688DAu)]
    [InlineData(0xBE3EB4E9u)]
    [InlineData(0xF2049C02u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceGpsForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    private uint Driver(uint type, uint function = 0x81000101)
    {
        uint driver = _harness.Alloc(24);
        GuestMemory.W32(driver, type);
        GuestMemory.W32(driver + 4, 0x55);
        GuestMemory.W32(driver + 8, function);
        GuestMemory.W32(driver + 12, function);
        return driver;
    }

    [Fact]
    public void ADescriptorIsChecked()
    {
        Assert.Equal(Invalid, Call(Services, "GpsRegisterDriver", 0u));
        Assert.Equal(Invalid, Call(Services, "GpsRegisterDriver", Driver(0)));
        Assert.Equal(Invalid, Call(Services, "GpsRegisterDriver", Driver(5)));
        Assert.Equal(Invalid, Call(Services, "GpsRegisterDriver", Driver(1, 0)));
        Assert.Equal(Invalid, Call(Services, "GpsIsDriverRegistered", 0));
        Assert.Equal(Invalid, Call(Services, "GpsUnregisterDriver", 5));
        Assert.Equal((0, 1), Services.GpsKernelValues);
    }

    [Fact]
    public void WithoutADriverTheDataAnswersStateOne()
    {
        uint data = _harness.Alloc(0x104);
        Assert.Equal(NoDriver, Call(Services, "GpsIsDriverRegistered", 1));
        Assert.Equal(NoDriver, Call(Services, "GpsUnregisterDriver", 1));

        // the primary type is 0 until a driver registers: type 0 passes the module's check
        Assert.Equal(NoDriver, Call(Services, "GpsSubmitPosition", 0, 0u, data));
        Assert.Equal(NoDriver, Call(Services, "GpsSubmitSatellites", 0, data));
        Assert.Equal(NoDriver, Call(Services, "GpsSubmitAssistance", 0, data, 16u));
        Assert.Equal(Invalid, Call(Services, "GpsSubmitPosition", 1, 0u, data));
        Assert.Equal(Invalid, Call(Services, "GpsSubmitPosition", 0, 2u, data));
        Assert.Equal(Invalid, Call(Services, "GpsSubmitPosition", 0, 0u, 0u));
    }

    [Fact]
    public void ARegisteredDriverIsPrimaryAndItsDataWaitsForASession()
    {
        uint data = _harness.Alloc(0x104);
        Assert.Equal(0, Call(Services, "GpsRegisterDriver", Driver(3)));
        Assert.Equal(Registered, Call(Services, "GpsRegisterDriver", Driver(3)));
        Assert.Equal(0, Call(Services, "GpsRegisterDriver", Driver(2)));
        Assert.Equal((3, 2), Services.GpsKernelValues); // the first one stays primary
        Assert.Equal(0, Call(Services, "GpsIsDriverRegistered", 2));
        Assert.Equal(NotStarted, Call(Services, "GpsSubmitPosition", 3, 1u, data));
        Assert.Equal(NotStarted, Call(Services, "GpsSubmitSatellites", 3, data));
        Assert.Equal(NotStarted, Call(Services, "GpsSubmitAssistance", 3, data, 8u));
        Assert.Equal(Invalid, Call(Services, "GpsSubmitPosition", 2, 1u, data));

        Assert.Equal(0, Call(Services, "GpsUnregisterDriver", 3));
        Assert.Equal((2, 2), Services.GpsKernelValues); // the lowest type left
        Assert.Equal(NoDriver, Call(Services, "GpsIsDriverRegistered", 3));
        Assert.Equal(0, Call(Services, "GpsUnregisterDriver", 2));
        Assert.Equal((0, 1), Services.GpsKernelValues);
    }

    [Fact]
    public void DriverEventsHaveNoSessionToReach()
    {
        Assert.Equal(0, Call(Services, "GpsDriverEvent", 0u, 0x10001u));
        Assert.Equal(0, Call(Services, "GpsDriverEvent", 0u, 0x80002u));
        Assert.Equal(unchecked((int)0x8039000B), Call(Services, "GpsDriverEvent", 0u, 0x60000u));
        Assert.Equal(0, Call(Services, "GpsDriverEvent", 0x10000000u, 0x60000u));
        Assert.Equal(0, Call(Services, "GpsDriverEvent", 0x20000000u, 0x20003u));
        Assert.Equal(0, Call(Services, "GpsDriverEvent", 0x50000000u, 0x20003u));
    }
}
