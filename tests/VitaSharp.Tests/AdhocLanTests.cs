using System.Net;
using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Two emulator instances in one process on the ad hoc medium (UDP on loopback): as on FW 3.74, the game opens the
/// network check dialog in a PSP ad hoc mode with a group name: one instance creates the group (its IBSS, SSID from the
/// ad hoc id and the name), the other joins it once its beacon is heard; then each sees the other in
/// sceNetAdhocctlGetPeerList and PDP datagrams go both ways (sender MAC and port, data; a small buffer gets
/// NOT_ENOUGH_SPACE and the size). Before, the dialog canceled and nothing ever arrived.
/// </summary>
[Collection("guest")]
public sealed class AdhocLanTests : IDisposable
{
    private const int NotEnoughSpace = unchecked((int)0x80410706);
    private const int ModePspAdhocCreate = 6, ModePspAdhocJoin = 7;
    private const ushort Port = 0x309;

    private readonly HleHarness _creator;
    private readonly HleHarness _joiner;
    private readonly UdpAdhocMedium _creatorMedium = new(AdhocMedium.RandomMac(), "creator", port: 0, multicast: false);
    private readonly UdpAdhocMedium _joinerMedium = new(AdhocMedium.RandomMac(), "joiner", port: 0, multicast: false);

    public AdhocLanTests(GuestMemoryFixture fixture)
    {
        _creator = new HleHarness(fixture, "adhoc-creator");
        _joiner = new HleHarness(fixture, "adhoc-joiner");
        _creatorMedium.AddStation(new IPEndPoint(IPAddress.Loopback, _joinerMedium.LocalPort));
        _joinerMedium.AddStation(new IPEndPoint(IPAddress.Loopback, _creatorMedium.LocalPort));
        _creator.Emu.Host.Adhoc = _creatorMedium;
        _joiner.Emu.Host.Adhoc = _joinerMedium;
    }

    public void Dispose()
    {
        _creator.Dispose();
        _joiner.Dispose();
        _creatorMedium.Dispose();
        _joinerMedium.Dispose();
    }

    /// <summary>sceNetInit, sceNetAdhocInit and sceNetAdhocctlInit with a product id (as a PSP game does).</summary>
    private static void StartAdhoc(HleHarness h)
    {
        uint param = h.Alloc(12);
        GuestMemory.W32(param, h.Alloc(0x4000));
        GuestMemory.W32(param + 4, 0x4000);
        Assert.Equal(0, Call(h.Module<Net>(), "sceNetInit", param));
        Assert.Equal(0, Call(h.Module<NetAdhoc>(), "sceNetAdhocInit"));
        uint id = h.Alloc(16);
        GuestMemory.W32(id, 0); // product id
        Encoding.ASCII.GetBytes("ULUS00000").CopyTo(GuestMemory.Span(id + 4, 9));
        Assert.Equal(0, Call(h.Module<NetAdhoc>(), "sceNetAdhocctlInit", id));
    }

    /// <summary>Runs the network check dialog in <paramref name="mode"/> for the group and returns its result.</summary>
    private static int CheckDialog(HleHarness h, int mode, string group)
    {
        CommonDialog dialogs = h.Module<CommonDialog>();
        uint param = h.Alloc(0xE0);
        GuestMemory.W32(param + 0x50, (uint)mode);
        GuestMemory.W32(param + 0x64, h.Str(group));
        GuestMemory.W32(param + 0x68, 10_000_000);
        Assert.Equal(0, Call(dialogs, "sceNetCheckDialogInit", param));
        Until(() => Call(dialogs, "sceNetCheckDialogGetStatus") == 2, "the network check dialog finishes");
        uint result = h.Alloc(0x84);
        Assert.Equal(0, Call(dialogs, "sceNetCheckDialogGetResult", result));
        Assert.Equal(0, Call(dialogs, "sceNetCheckDialogTerm"));
        return (int)GuestMemory.R32(result);
    }

    private static int CreatePdp(HleHarness h)
    {
        uint own = h.Alloc(8);
        h.Emu.Host.Adhoc!.Mac.CopyTo(GuestMemory.Span(own, 6));
        int id = Call(h.Module<NetAdhoc>(), "sceNetAdhocPdpCreate", own, (uint)Port, 0x2000u, 0);
        Assert.True(id > 0);
        return id;
    }

    private static int Send(HleHarness h, int socket, byte[] destination, byte[] data)
    {
        uint to = h.Alloc(8);
        destination.CopyTo(GuestMemory.Span(to, 6));
        uint buffer = h.Alloc((uint)data.Length);
        data.CopyTo(GuestMemory.Span(buffer, data.Length));
        return Call(h.Module<NetAdhoc>(), "sceNetAdhocPdpSend", socket, to, (uint)Port, buffer, data.Length, 0u, 0);
    }

    [Fact]
    public void TwoInstancesMakeAGroupAndExchangeDatagrams()
    {
        StartAdhoc(_creator);
        StartAdhoc(_joiner);
        Assert.Equal(0, CheckDialog(_creator, ModePspAdhocCreate, "GROUP1"));
        Assert.Equal(0, CheckDialog(_joiner, ModePspAdhocJoin, "GROUP1"));
        Assert.Equal(_creatorMedium.Bssid, _joinerMedium.Bssid);

        // each one sees the other in the group
        NetAdhoc creatorAdhoc = _creator.Module<NetAdhoc>();
        uint list = _creator.Alloc(0x98 * 4);
        uint length = _creator.Word(0x98 * 4);
        Until(() =>
        {
            GuestMemory.W32(length, 0x98 * 4);
            return Call(creatorAdhoc, "sceNetAdhocctlGetPeerList", new Ptr<int>(length), list) == 0 && GuestMemory.R32(length) == 0x98;
        }, "the creator lists the joiner");
        Assert.Equal("joiner", GuestMemory.ReadCString(list + 4, 128));
        Assert.Equal(_joinerMedium.Mac, GuestMemory.ReadSpan(list + 0x84, 6).ToArray());
        uint parameter = _joiner.Alloc(0x94);
        Assert.Equal(0, Call(_joiner.Module<NetAdhoc>(), "sceNetAdhocctlGetParameter", parameter));
        Assert.Equal("GROUP1", GuestMemory.ReadCString(parameter + 4, 8));
        Assert.Equal(_creatorMedium.Bssid, GuestMemory.ReadSpan(parameter + 0x8C, 6).ToArray());

        int creatorSocket = CreatePdp(_creator);
        int joinerSocket = CreatePdp(_joiner);
        byte[] hello = Encoding.ASCII.GetBytes("hello joiner");
        Assert.Equal(hello.Length, Send(_creator, creatorSocket, _joinerMedium.Mac, hello));

        // a blocking receive on the joiner gets it: sender MAC and port, data; a small buffer first gets the size
        uint from = _joiner.Alloc(8), fromPort = _joiner.Alloc(4), buffer = _joiner.Alloc(64), size = _joiner.Word(4);
        NetAdhoc joinerAdhoc = _joiner.Module<NetAdhoc>();
        _joiner.OnGuest(_ =>
        {
            Assert.Equal(NotEnoughSpace, Call(joinerAdhoc, "sceNetAdhocPdpRecv", joinerSocket, from, fromPort, buffer,
                new Ptr<int>(size), 10_000_000u, 0));
            Assert.Equal((uint)hello.Length, GuestMemory.R32(size));
            Assert.Equal(0, Call(joinerAdhoc, "sceNetAdhocPdpRecv", joinerSocket, from, fromPort, buffer, new Ptr<int>(size), 10_000_000u, 0));
        });
        Assert.Equal((uint)hello.Length, GuestMemory.R32(size));
        Assert.Equal("hello joiner", Encoding.ASCII.GetString(GuestMemory.ReadSpan(buffer, hello.Length)));
        Assert.Equal(_creatorMedium.Mac, GuestMemory.ReadSpan(from, 6).ToArray());
        Assert.Equal(Port, GuestMemory.Read<ushort>(fromPort));

        // and back, to everyone in the group
        Assert.Equal(3, Send(_joiner, joinerSocket, AdhocMedium.Broadcast, [1, 2, 3]));
        uint back = _creator.Alloc(64), backSize = _creator.Word(64), backFrom = _creator.Alloc(8), backPort = _creator.Alloc(4);
        _creator.OnGuest(_ => Assert.Equal(0, Call(creatorAdhoc, "sceNetAdhocPdpRecv", creatorSocket, backFrom, backPort, back,
            new Ptr<int>(backSize), 10_000_000u, 0)));
        Assert.Equal(3u, GuestMemory.R32(backSize));
        Assert.Equal([1, 2, 3], GuestMemory.ReadSpan(back, 3).ToArray());
        Assert.Equal(_joinerMedium.Mac, GuestMemory.ReadSpan(backFrom, 6).ToArray());
    }

    /// <summary>Joining a group nobody made: the dialog gives up after its timeout and the user "cancels".</summary>
    [Fact]
    public void JoiningAGroupNobodyMadeIsCanceled()
    {
        StartAdhoc(_joiner);
        CommonDialog dialogs = _joiner.Module<CommonDialog>();
        uint param = _joiner.Alloc(0xE0);
        GuestMemory.W32(param + 0x50, ModePspAdhocJoin);
        GuestMemory.W32(param + 0x64, _joiner.Str("NOBODY"));
        GuestMemory.W32(param + 0x68, 400_000);
        Assert.Equal(0, Call(dialogs, "sceNetCheckDialogInit", param));
        Until(() => Call(dialogs, "sceNetCheckDialogGetStatus") == 2, "the dialog gives up");
        uint result = _joiner.Alloc(0x84);
        Assert.Equal(0, Call(dialogs, "sceNetCheckDialogGetResult", result));
        Assert.Equal(1u, GuestMemory.R32(result)); // user canceled
        Assert.Null(_joinerMedium.Ssid);

        // a JOIN without a group name is the dialog's PSP ad hoc parameter error
        Assert.Equal(0, Call(dialogs, "sceNetCheckDialogTerm"));
        GuestMemory.W32(param + 0x64, 0);
        Assert.Equal(unchecked((int)0x80100C07), Call(dialogs, "sceNetCheckDialogInit", param));
    }
}
