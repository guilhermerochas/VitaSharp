using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceMtpIfForDriver and SceUsbMtpForDriver (3.74) with no PC on the cable: the one interface and its check on every
/// call (0x80543603), no registered transport (0x8054360C), start and stop going to SceUsbMtp, whose driver starts but
/// never attaches to a host (0x8054360C).
/// </summary>
[Collection("guest")]
public sealed class MtpKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80543603), NotReady = unchecked((int)0x8054360C);

    private readonly HleHarness _harness = new(fixture, "mtpk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    public static TheoryData<string, uint> Exports()
    {
        var data = new TheoryData<string, uint>();
        uint[] mtpIf =
        [
            0x05408B5B, 0x117F7519, 0x16D7DBCC, 0x27D40294, 0x2D9318E1, 0x33F1826F, 0x5245D237, 0x5766D257,
            0x8D16B997, 0x92CD107E, 0xB5E78858, 0xCB685AC0, 0xE6453F21, 0xEDC3AB26, 0xF0FA2D86, 0xF1EC5992,
            0xFDF8C3C0,
        ];
        uint[] usbMtp =
        [
            0x08CD359F, 0x151947D2, 0x25D92570, 0x4463009B, 0x576A266F, 0x5FA86BA4, 0x6F7DF4B8, 0x6FC97594,
            0x76D707B1, 0xB01C1369, 0xB958E5DE, 0xE64513A9,
        ];
        foreach (uint nid in mtpIf)
        {
            data.Add("SceMtpIfForDriver", nid);
        }

        foreach (uint nid in usbMtp)
        {
            data.Add("SceUsbMtpForDriver", nid);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Exports))]
    public void EveryExportHasAnEntry(string library, uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == library).ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void TheInterfaceIsCheckedAndNoTransportIsRegistered()
    {
        Assert.Equal(0u, (uint)Invoke(Services, "ksceMtpIfGetInterface", 2u)!);
        uint handle = (uint)Invoke(Services, "ksceMtpIfGetInterface", 1u)!;
        Assert.NotEqual(0u, handle);
        Assert.Equal(handle, (uint)Invoke(Services, "ksceMtpIfGetInterface", 1u)!);
        uint buffer = _harness.Alloc(0x40);
        Assert.Equal(Invalid, Call(Services, "ksceMtpIfGetStatus", handle + 4));
        Assert.Equal(NotReady, Call(Services, "ksceMtpIfGetStatus", handle));
        Assert.Equal(Invalid, Call(Services, "ksceMtpIfGetInfo", handle, 0u));
        Assert.Equal(NotReady, Call(Services, "ksceMtpIfGetInfo", handle, buffer));
        Assert.Equal(Invalid, Call(Services, "ksceMtpIfReceiveRequest", handle, buffer));
        Assert.Equal(NotReady, Call(Services, "ksceMtpIfSend", handle, buffer, 4u));
        Assert.Equal(NotReady, Call(Services, "ksceMtpIfSendResponse", handle, buffer, buffer));
        Assert.Equal(NotReady, Call(Services, "ksceMtpIfIsConnected", handle));
        Assert.Equal(Invalid, Call(Services, "ksceMtpIfStartDriver", 2u));
    }

    [Fact]
    public void TheDriverStartsButNeverAttaches()
    {
        uint handle = (uint)Invoke(Services, "ksceMtpIfGetInterface", 1u)!;
        Assert.Equal(0, Call(Services, "ksceMtpIfStart", handle));
        Assert.Equal(0, Call(Services, "ksceUsbMtpStart"));
        Assert.Equal(0, Call(Services, "ksceUsbMtpGetStatus"));
        Assert.Equal(NotReady, Call(Services, "ksceUsbMtpSendData", 0u));
        Assert.Equal(NotReady, Call(Services, "ksceUsbMtpWaitEvent", 1000u));
        Assert.Equal(NotReady, Call(Services, "ksceUsbMtpSetState", 4u));
        Assert.Equal(NotReady, Call(Services, "ksceMtpIfStop", handle));
        Assert.Equal(NotReady, Call(Services, "ksceMtpIfStopDriver", 1u));
    }
}
