using System.Collections.Concurrent;
using System.Net;
using VitaSharp.Core.Host;

namespace VitaSharp.Tests;

/// <summary>
/// The ad hoc medium over the host's UDP (two instances on loopback, by address: no multicast needed): one creates an
/// IBSS and beacons, the other hears it in a scan, joins it (the same BSSID), both see each other as peers, and frames
/// go to one MAC or to all; a network nobody beacons cannot be joined, and a station that left gets nothing.
/// </summary>
public sealed class AdhocNetworkTests : IDisposable
{
    private const ushort PspAdhocEtherType = 0x88C8;
    private const string Ssid = "PSP_AULUS10000_L_GROUP1";

    private readonly UdpAdhocMedium _first = new(AdhocMedium.RandomMac(), "first", port: 0, multicast: false);
    private readonly UdpAdhocMedium _second = new(AdhocMedium.RandomMac(), "second", port: 0, multicast: false);

    public AdhocNetworkTests()
    {
        _first.AddStation(new IPEndPoint(IPAddress.Loopback, _second.LocalPort));
        _second.AddStation(new IPEndPoint(IPAddress.Loopback, _first.LocalPort));
    }

    public void Dispose()
    {
        _first.Dispose();
        _second.Dispose();
    }

    [Fact]
    public void TwoStationsMeetInAnIbssAndExchangeFrames()
    {
        var atFirst = new BlockingCollection<AdhocFrame>();
        var atSecond = new BlockingCollection<AdhocFrame>();
        _first.FrameReceived += atFirst.Add;
        _second.FrameReceived += atSecond.Add;

        Assert.False(_second.Join(Ssid)); // nobody beacons it yet
        _first.Create(Ssid);
        HleHarness.Until(() => _second.Scan().Any(n => n.Ssid == Ssid), "the second station hears the beacon");
        Assert.True(_second.Join(Ssid));
        Assert.Equal(_first.Bssid, _second.Bssid);
        HleHarness.Until(() => _first.Peers().Any(p => p.Mac.AsSpan().SequenceEqual(_second.Mac)), "the creator sees the joiner");
        HleHarness.Until(() => _second.Peers().Any(p => p.Nickname == "first"), "the joiner sees the creator");

        Assert.True(_first.Send(_second.Mac, PspAdhocEtherType, [1, 2, 3]));
        Assert.True(atSecond.TryTake(out AdhocFrame? frame, 10_000));
        Assert.Equal(_first.Mac, frame!.Source);
        Assert.Equal(PspAdhocEtherType, frame.EtherType);
        Assert.Equal([1, 2, 3], frame.Payload);

        Assert.True(_second.Send(AdhocMedium.Broadcast, PspAdhocEtherType, [9]));
        Assert.True(atFirst.TryTake(out frame, 10_000));
        Assert.Equal(_second.Mac, frame!.Source);
        Assert.Equal([9], frame.Payload);

        // a frame for a third MAC is not this unit's
        Assert.True(_second.Send(AdhocMedium.RandomMac(), PspAdhocEtherType, [7]));
        _first.Leave();
        Assert.False(_first.Send(_second.Mac, PspAdhocEtherType, [5])); // no network joined
        Assert.True(_second.Send(_first.Mac, PspAdhocEtherType, [6]));
        Assert.False(atFirst.TryTake(out _, 500));
    }

    [Fact]
    public void StationsGiveTheirAddressAsHostAndPort()
    {
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.0.20"), 4000), UdpAdhocMedium.ParseStation("192.168.0.20:4000"));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.2"), UdpAdhocMedium.DefaultPort), UdpAdhocMedium.ParseStation("10.0.0.2"));
        Assert.Null(UdpAdhocMedium.ParseStation("no-such-host.invalid"));
    }
}
