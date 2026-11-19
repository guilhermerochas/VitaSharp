using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceHdmiForDriver (hdmi.skprx 3.74) on a handheld: no transmitter answers and no monitor (EDID, monitor information,
/// chip settings), the video mode table, the audio mode needing a video mode, six callback slots (unregistering clears
/// slot 0, as the module does), the plain settings kept, CEC unavailable (not a PS TV), the CEC statistics block.
/// </summary>
[Collection("guest")]
public sealed class HdmiKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80370001), NoEdid = unchecked((int)0x80370002);
    private const int VideoMode = unchecked((int)0x80370004), NoVideoMode = unchecked((int)0x80370006);
    private const int NoMonitor = unchecked((int)0x80370008), Chip = unchecked((int)0x8037000B);
    private const int CecUnsupported = unchecked((int)0x8037000E), CecOff = unchecked((int)0x8037000F);

    private readonly HleHarness _harness = new(fixture, "hdmik");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x05174BC2u)]
    [InlineData(0x0686FBD6u)]
    [InlineData(0x08AFFB03u)]
    [InlineData(0x0F12364Au)]
    [InlineData(0x291F000Bu)]
    [InlineData(0x3126A070u)]
    [InlineData(0x47894820u)]
    [InlineData(0x47B753EBu)]
    [InlineData(0x4C31F5E8u)]
    [InlineData(0x5A212BB3u)]
    [InlineData(0x6D2D10D8u)]
    [InlineData(0x7AD0F6C9u)]
    [InlineData(0x854EEB78u)]
    [InlineData(0x8EB0FD9Cu)]
    [InlineData(0x93AA48ACu)]
    [InlineData(0x99A3CABEu)]
    [InlineData(0xB04F5B6Au)]
    [InlineData(0xD929E4E5u)]
    [InlineData(0xE331EC90u)]
    [InlineData(0xE66D5A4Du)]
    [InlineData(0xEA486A37u)]
    [InlineData(0xF20529CCu)]
    [InlineData(0xFA30ACFCu)]
    [InlineData(0xFAB3A2E9u)]
    [InlineData(0xFF5546D8u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceHdmiForDriver").ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void NoTransmitterAndNoMonitor()
    {
        uint info = _harness.Alloc(0x100);
        GuestMemory.Fill(info, 0xAA, 0x100);
        Assert.Equal(Invalid, Call(Services, "ksceHdmiGetMonitorInfo", 0u));
        Assert.Equal(NoMonitor, Call(Services, "ksceHdmiGetMonitorInfo", info));
        Assert.Equal(0u, GuestMemory.R32(info + 0xF0));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(info + 0xF4));
        uint length = _harness.Word(0);
        Assert.Equal(Invalid, Call(Services, "ksceHdmiGetEdid", info, 4u, length));
        Assert.Equal(Invalid, Call(Services, "ksceHdmiGetEdid", info, 0x100u, 0u));
        Assert.Equal(NoEdid, Call(Services, "ksceHdmiGetEdid", info, 0x100u, length));
        Assert.Equal(Chip, Call(Services, "ksceHdmiSetAvMute", 1u));
        Assert.Equal(Chip, Call(Services, "ksceHdmiSetOutputEnable", 1u));
        Assert.Equal(Chip, Call(Services, "ksceHdmiSetBlankScreen", 1u));
        Assert.Equal(Chip, Call(Services, "ksceHdmiGetSinkPhysicalAddress"));
        Assert.Equal(0, Call(Services, "ksceHdmiSetTmdsOutput", 1u));
        Assert.Equal(0, Call(Services, "ksceHdmiSetHdcp", 1u));
        Assert.Equal(0, Call(Services, "ksceHdmiResetTmdsOutput", 0u));
        Assert.Equal(0, Call(Services, "ksceHdmiPowerOn"));
        Assert.Equal(0, Call(Services, "ksceHdmiPowerOn"));
    }

    [Fact]
    public void TheAudioModeNeedsAVideoModeOfTheTable()
    {
        uint mode = _harness.Alloc(16);
        Assert.Equal(NoVideoMode, Call(Services, "ksceHdmiSetAudioMode", mode));
        Assert.Equal(VideoMode, Call(Services, "ksceHdmiSetVideoMode", 0u));
        GuestMemory.Write<ushort>(mode, 0x1234);
        Assert.Equal(VideoMode, Call(Services, "ksceHdmiSetVideoMode", mode));
        Assert.Equal(0, Services.HdmiKernelValues.VideoMode);
        GuestMemory.Write<ushort>(mode, 0x8B00);
        Assert.Equal(VideoMode, Call(Services, "ksceHdmiSetVideoMode", mode));
        Assert.Equal(0x8B00, Services.HdmiKernelValues.VideoMode);
        uint audio = _harness.Alloc(16);
        GuestMemory.Write<ushort>(audio, 2);
        Assert.Equal(Invalid, Call(Services, "ksceHdmiSetAudioMode", audio));
        GuestMemory.Write<ushort>(audio, 1);
        Assert.Equal(2, Call(Services, "ksceHdmiSetAudioMode", audio));
        GuestMemory.Write<ushort>(mode, 0x8B01);
        Call(Services, "ksceHdmiSetVideoMode", mode);
        Assert.Equal(NoVideoMode, Call(Services, "ksceHdmiSetAudioMode", audio));
    }

    [Fact]
    public void SixCallbackSlotsAndThePlainSettings()
    {
        Assert.Equal(Invalid, Call(Services, "ksceHdmiRegisterCallback", 6u, 0x81000001u));
        Assert.Equal(Invalid, Call(Services, "ksceHdmiRegisterCallback", 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceHdmiRegisterCallback", 0u, 0x81000001u));
        Assert.Equal(0, Call(Services, "ksceHdmiRegisterCallback", 5u, 0x81000005u));
        Assert.Equal(Invalid, Call(Services, "ksceHdmiUnregisterCallback", 6u));
        Assert.Equal(0, Call(Services, "ksceHdmiUnregisterCallback", 5u));
        Assert.Equal(new uint[] { 0, 0, 0, 0, 0, 0x81000005 }, Services.HdmiKernelValues.Callbacks);
        Assert.Equal(0, Call(Services, "ksceHdmiSetOutputSetting", 1u, 7u));
        Assert.Equal(0, Call(Services, "ksceHdmiSetOutputSetting", 2u, 9u));
        Assert.Equal(0, Call(Services, "ksceHdmiSetOutputSetting", 3u, 11u));
        Assert.Equal(0, Call(Services, "ksceHdmiSetMode", 1u));
        Assert.Equal(0, Call(Services, "ksceHdmiSetMode", 2u));
        Assert.Equal(0, Call(Services, "ksceHdmiSetControllerSetting", 0x55u));
        (uint[] Callbacks, ushort VideoMode, uint Output, uint Display, uint Mode, uint Controller) values =
            Services.HdmiKernelValues;
        Assert.Equal((7u, 9u, 1u, 0x55u), (values.Output, values.Display, values.Mode, values.Controller));
    }

    [Fact]
    public void CecIsNotAvailableOnAHandheld()
    {
        Assert.Equal(CecUnsupported, Call(Services, "ksceHdmiCecEnable"));
        Assert.Equal(CecUnsupported, Call(Services, "ksceHdmiCecDisable"));
        Assert.Equal(CecUnsupported, Call(Services, "ksceHdmiCecWakeup"));
        Assert.Equal(-1, Call(Services, "ksceHdmiCecGetPowerStatus"));
        Assert.Equal(CecOff, Call(Services, "ksceHdmiCecOneTouchPlay", 1u));
        Assert.Equal(CecOff, Call(Services, "ksceHdmiCecOneTouchPlay", 2u));
        Assert.Equal(Invalid, Call(Services, "ksceHdmiCecOneTouchPlay", 3u));
        Assert.Equal(0, Call(Services, "ksceHdmiCecSetActive", 1u));
        Assert.Equal(CecOff, Call(Services, "ksceHdmiCecOneTouchPlay", 0u));
    }

    [Fact]
    public void TheCecStatisticsBlock()
    {
        uint stats = _harness.Alloc(0x20);
        GuestMemory.W32(stats, 0x21);
        Assert.Equal(Invalid, Call(Services, "ksceHdmiCecGetStats", stats));
        Assert.Equal(Invalid, Call(Services, "ksceHdmiCecGetStats", 0u));
        GuestMemory.W32(stats, 0x20);
        Assert.Equal(0, Call(Services, "ksceHdmiCecGetStats", stats));
        Assert.Equal(0u, GuestMemory.R32(stats));
        Assert.Equal(0, Call(Services, "ksceHdmiCecResetStats"));
        Assert.Equal(0, Call(Services, "ksceHdmiCecGetStats", stats));
        Assert.Equal(0x20u, GuestMemory.R32(stats));
        Assert.Equal(0u, GuestMemory.R32(stats + 0x10));
    }
}
