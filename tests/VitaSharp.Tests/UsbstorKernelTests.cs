using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceUsbstorForDriver (SceUsbstorDriver 3.74): the handler table (checks, priority order, eight at most, duplicates,
/// removal), the maximum LUN, the inquiry strings of a LUN, and the data entries with no PC plugged in.
/// </summary>
[Collection("guest")]
public sealed class UsbstorKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80244103), Registered = unchecked((int)0x80244102);
    private const int Full = unchecked((int)0x80244104), NotRegistered = unchecked((int)0x80244105);
    private const int Transfer = unchecked((int)0x80244109);

    private readonly HleHarness _harness = new(fixture, "usbstork");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x73F2FF9Du)]
    [InlineData(0x77B7E69Eu)]
    [InlineData(0xADB2AAF2u)]
    [InlineData(0xB6689597u)]
    [InlineData(0xBEF6B5AEu)]
    [InlineData(0xC0944A04u)]
    [InlineData(0xD3C7EE70u)]
    [InlineData(0xEAC08C24u)]
    [InlineData(0xF1A690DCu)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceUsbstorForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    private uint Handler(byte flags, byte bits, byte priority)
    {
        uint handler = _harness.Alloc(8);
        GuestMemory.Write(handler, flags);
        GuestMemory.Write(handler + 2, bits);
        GuestMemory.Write(handler + 3, priority);
        GuestMemory.W32(handler + 4, 0x81000101);
        return handler;
    }

    [Fact]
    public void HandlersAreKeptByPriority()
    {
        Assert.Equal(Invalid, Call(Services, "UsbstorRegisterHandler", 0u));
        Assert.Equal(Invalid, Call(Services, "UsbstorRegisterHandler", Handler(0x08, 0, 1)));
        uint late = Handler(0, 1, 0xFF), high = Handler(0, 2, 5), low = Handler(0x40, 4, 1), mid = Handler(0, 8, 5);
        Assert.Equal(0, Call(Services, "UsbstorRegisterHandler", late));
        Assert.Equal(0, Call(Services, "UsbstorRegisterHandler", high));
        Assert.Equal(0, Call(Services, "UsbstorRegisterHandler", low));
        Assert.Equal(0, Call(Services, "UsbstorRegisterHandler", mid));
        Assert.Equal(Registered, Call(Services, "UsbstorRegisterHandler", mid));
        Assert.Equal([low, high, mid, late], Services.UsbstorHandlerOrder);
        Assert.Equal(15, Call(Services, "UsbstorHandlerBits"));

        Assert.Equal(0, Call(Services, "UsbstorUnregisterHandler", high));
        Assert.Equal(NotRegistered, Call(Services, "UsbstorUnregisterHandler", high));
        Assert.Equal([low, mid, late], Services.UsbstorHandlerOrder);
        for (int index = 0; index < 5; index++)
        {
            Assert.Equal(0, Call(Services, "UsbstorRegisterHandler", Handler(0, 0, 9)));
        }

        Assert.Equal(Full, Call(Services, "UsbstorRegisterHandler", Handler(0, 0, 9)));
    }

    [Fact]
    public void TheLunsAndTheirInquiryStrings()
    {
        Assert.Equal(0, Call(Services, "UsbstorGetMaxLun"));
        Assert.Equal(Invalid, Call(Services, "UsbstorSetMaxLun", 7u));
        Assert.Equal(0, Call(Services, "UsbstorSetMaxLun", 2u));
        Assert.Equal(2, Call(Services, "UsbstorGetMaxLun"));

        Assert.Equal(Invalid, Call(Services, "UsbstorSetInquiry", _harness.Str("p"), _harness.Str("r"), 3u)); // past the table
        Assert.Equal(0, Call(Services, "UsbstorSetInquiry", _harness.Str("PS Vita Card Drive"), _harness.Str("1.00X"), 1u));
        uint output = _harness.Alloc(0x80);
        Assert.Equal(8, Call(Services, "UsbstorGetInquiry", 1u, 0u, output, 0x80u));
        Assert.Equal("PS Vita ", GuestMemory.ReadCString(output)[..8]);
        Assert.Equal(16, Call(Services, "UsbstorGetInquiry", 1u, 1u, output, 0x80u));
        Assert.Equal("Card Dri1.00    ", GuestMemory.ReadCString(output)[..16]); // the revision follows; zeros read as spaces
        Assert.Equal(2, Call(Services, "UsbstorGetInquiry", 1u, 2u, output, 2u));
        Assert.Equal(Invalid, Call(Services, "UsbstorGetInquiry", 1u, 4u, output, 2u));
        Assert.Equal(Invalid, Call(Services, "UsbstorGetInquiry", 1u, 0u, 0u, 2u));
    }

    [Fact]
    public void NoCommandIsInFlightWithoutAPc()
    {
        uint data = _harness.Alloc(0x200);
        Assert.Equal(Invalid, Call(Services, "UsbstorSendData", 0u, 0x200u, 0));
        Assert.Equal(0, Call(Services, "UsbstorSendData", data, 0u, 0));
        Assert.Equal(Transfer, Call(Services, "UsbstorSendData", data, 0x200u, 0));
        Assert.Equal(Transfer, Call(Services, "UsbstorReceiveData", data, 0x200u, 0));
    }
}
