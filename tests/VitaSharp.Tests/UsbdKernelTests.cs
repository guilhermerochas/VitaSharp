using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceUsbdForDriver (SceUsbd 3.74): class and composite drivers register into sixteen shared slots (incomplete blocks
/// 0x80240003, a duplicate name 0x80240002, a full table 0x80240004); with nothing on the host port a device id is
/// 0x80240006, a pipe id 0x80240003 and a descriptor scan NULL; the three buses start and stop.
/// </summary>
[Collection("guest")]
public sealed class UsbdKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80240003), Duplicate = unchecked((int)0x80240002);
    private const int NoSlot = unchecked((int)0x80240004), NoDevice = unchecked((int)0x80240006);

    private readonly HleHarness _harness = new(fixture, "usbdk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private uint Driver(string name)
    {
        uint driver = _harness.Alloc(16);
        GuestMemory.W32(driver, _harness.Str(name));
        GuestMemory.W32(driver + 4, 0x81000001);
        GuestMemory.W32(driver + 8, 0x81000003);
        GuestMemory.W32(driver + 12, 0x81000005);
        return driver;
    }

    [Theory]
    [InlineData(0x05073925u)]
    [InlineData(0x0E73D253u)]
    [InlineData(0x1CDBFF9Fu)]
    [InlineData(0x1EC94F18u)]
    [InlineData(0x206634F9u)]
    [InlineData(0x216F108Du)]
    [InlineData(0x2249BF99u)]
    [InlineData(0x235C094Au)]
    [InlineData(0x2E05660Fu)]
    [InlineData(0x3C3396BBu)]
    [InlineData(0x3EBF5FFEu)]
    [InlineData(0x470D5289u)]
    [InlineData(0x5E301E18u)]
    [InlineData(0x6E53D7F4u)]
    [InlineData(0x7934BB4Au)]
    [InlineData(0x7938DAC7u)]
    [InlineData(0xA0BF85B8u)]
    [InlineData(0xBC3EF82Bu)]
    [InlineData(0xC3BECF5Fu)]
    [InlineData(0xD7AA730Du)]
    [InlineData(0xF304DC5Cu)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceUsbdForDriver").ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void DriversRegisterOnceIntoSixteenSlots()
    {
        uint incomplete = Driver("x");
        GuestMemory.W32(incomplete + 8, 0);
        Assert.Equal(Invalid, Call(Services, "ksceUsbdRegisterDriver", 0u));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdRegisterDriver", incomplete));
        Assert.Equal(0, Call(Services, "ksceUsbdRegisterDriver", Driver("mass")));
        Assert.Equal(Duplicate, Call(Services, "ksceUsbdRegisterDriver", Driver("mass")));
        Assert.Equal(Duplicate, Call(Services, "ksceUsbdRegisterCompositeLdd", Driver("mass")));
        for (int i = 1; i < 16; i++)
        {
            Assert.Equal(0, Call(Services, "ksceUsbdRegisterCompositeLdd", Driver($"d{i}")));
        }

        Assert.Equal(NoSlot, Call(Services, "ksceUsbdRegisterDriver", Driver("last")));
        Assert.Equal(0, Call(Services, "ksceUsbdUnregisterCompositeLdd", Driver("mass")));
        Assert.Equal(NoSlot, Call(Services, "ksceUsbdRegisterDriver", Driver("last")));
        Assert.Equal(0, Call(Services, "ksceUsbdUnregisterDriver", Driver("mass")));
        Assert.Equal(0, Call(Services, "ksceUsbdRegisterDriver", Driver("last")));
        Assert.Equal(0, Call(Services, "ksceUsbdUnregisterDriver", Driver("never")));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdUnregisterDriver", incomplete));
    }

    [Fact]
    public void NothingIsAttachedToTheHostPort()
    {
        uint word = _harness.Alloc(4);
        Assert.Equal(NoDevice, Call(Services, "ksceUsbdOpenPipe", 0x10001u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdClosePipe", 0x10001u));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdBulkTransfer", 0x10001u, 0u, 0u, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdBulkTransfer2", 0x10001u, 0u, 0u, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdInterruptTransfer", 0x10001u, 0u, 0u, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdControlTransfer", 0x10001u, word, 0u, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdIsochronousTransfer", 0x10001u, word, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceUsbd_7938DAC7", 0x10001u));
        Assert.Equal(0u, (uint)Invoke(Services, "ksceUsbdScanStaticDescriptor", 0x10001u, 0u, 4u)!);
        Assert.Equal(Invalid, Call(Services, "ksceUsbdGetDeviceSpeed", 0x10001u, 0u));
        Assert.Equal(NoDevice, Call(Services, "ksceUsbdGetDeviceSpeed", 0x10001u, word));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdGetDeviceLocation", 0x10001u, 0u));
        Assert.Equal(NoDevice, Call(Services, "ksceUsbdGetDeviceLocation", 0x10001u, word));
        Assert.Equal(Invalid, Call(Services, "ksceUsbd_05073925", 0x10001u, 0u, word));
        Assert.Equal(NoDevice, Call(Services, "ksceUsbd_05073925", 0x10001u, word, word));
    }

    [Fact]
    public void TheThreeBusesStartAndStop()
    {
        for (uint bus = 0; bus < 3; bus++)
        {
            Assert.Equal(0, Call(Services, "ksceUsbdHostStart", bus));
            Assert.Equal(0, Call(Services, "ksceUsbdSuspend", bus));
            Assert.Equal(0, Call(Services, "ksceUsbdSuspendPhase2", bus, 1u));
            Assert.Equal(0, Call(Services, "ksceUsbdResume", bus));
            Assert.Equal(0, Call(Services, "ksceUsbdHostStop", bus));
        }

        Assert.Equal(Invalid, Call(Services, "ksceUsbdHostStart", 3u));
        Assert.Equal(Invalid, Call(Services, "ksceUsbdHostStop", 3u));
    }
}
