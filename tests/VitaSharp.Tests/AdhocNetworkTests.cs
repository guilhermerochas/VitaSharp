using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
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

/// <summary>
/// The medium's receives end by cancellation, never by closing the socket: on macOS a UDP receive blocked in the kernel
/// is not woken by closing its socket, and .NET's socket Dispose then waits for it forever (the CI hang of 25/11/2026).
/// The links here model that host: a receive that only a datagram or the token ends, and a Dispose that wakes nothing.
/// </summary>
public sealed class AdhocMediumShutdownTests
{
    private const string Ssid = "PSP_AULUS10000_L_GROUP1";

    /// <summary>The failure guard of the waits (the passing runs never reach it: every wait is on an event).</summary>
    private static readonly TimeSpan s_guard = TimeSpan.FromSeconds(120);

    /// <summary>A UDP endpoint whose receive ends only with a datagram handed to it or by its token; closing it does not
    /// end a pending receive (a macOS UDP socket).</summary>
    private sealed class MacLikeLink(int port) : IAdhocLink
    {
        private readonly Channel<(byte[]? Bytes, IPEndPoint From)> _incoming = Channel.CreateUnbounded<(byte[]?, IPEndPoint)>();
        private int _pending;

        public readonly ConcurrentQueue<(byte[] Bytes, IPEndPoint Target)> Sent = new();
        public readonly SemaphoreSlim ReceiveStarted = new(0);
        public int Cancelled;
        public bool Disposed;

        public int LocalPort => port;

        public int Pending => Volatile.Read(ref _pending);

        public void Deliver(byte[] datagram, IPEndPoint from)
        {
            _incoming.Writer.TryWrite((datagram, from));
        }

        /// <summary>The next receive fails with a reset (how Windows reports an earlier send to a closed port).</summary>
        public void DeliverReset(IPEndPoint from)
        {
            _incoming.Writer.TryWrite((null, from));
        }

        public async ValueTask<AdhocDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancel)
        {
            Interlocked.Increment(ref _pending);
            ReceiveStarted.Release();
            try
            {
                (byte[]? bytes, IPEndPoint from) = await _incoming.Reader.ReadAsync(cancel).ConfigureAwait(false);
                if (bytes == null)
                {
                    throw new SocketException((int)SocketError.ConnectionReset);
                }

                bytes.CopyTo(buffer);
                return new AdhocDatagram(bytes.Length, from);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }

        public void SendTo(byte[] datagram, IPEndPoint target)
        {
            Sent.Enqueue((datagram, target));
        }

        public void Dispose()
        {
            // closing does not wake the pending receive: the Dispose of the medium must not count on it
            Assert.Equal(0, Pending);
            Disposed = true;
        }
    }

    /// <summary>Waits until the medium has started its <paramref name="receives"/>-th receive on <paramref name="link"/>
    /// (one per datagram handed over, plus the one left pending).</summary>
    private static void WaitReceiving(MacLikeLink link, int receives = 1)
    {
        for (int i = 0; i < receives; i++)
        {
            Assert.True(link.ReceiveStarted.Wait(s_guard), "the medium started its receive");
        }
    }

    private static void DisposeWithin(UdpAdhocMedium medium)
    {
        var dispose = Task.Run(medium.Dispose);
        Assert.True(dispose.Wait(s_guard), "Dispose did not return: it waited for receives that no datagram and no close end");
    }

    [Fact]
    public void DisposeEndsReceivesThatNoDatagramAndNoCloseWouldEnd()
    {
        var listening = new MacLikeLink(4000);
        var own = new MacLikeLink(4001);
        var medium = new UdpAdhocMedium(AdhocMedium.RandomMac(), "unit", listening, own, group: null);
        WaitReceiving(listening);
        WaitReceiving(own);

        DisposeWithin(medium);

        Assert.Equal(1, listening.Cancelled);
        Assert.Equal(1, own.Cancelled);
        Assert.Equal(0, listening.Pending);
        Assert.Equal(0, own.Pending);
        Assert.True(listening.Disposed);
        Assert.True(own.Disposed);
        medium.Dispose(); // a second Dispose does nothing
    }

    [Fact]
    public void AFrameHeardBeforeTheDisposeIsDeliveredAndAnsweredToItsSender()
    {
        var listening = new MacLikeLink(4000);
        var own = new MacLikeLink(4001);
        var medium = new UdpAdhocMedium(AdhocMedium.RandomMac(), "unit", listening, own, group: null);
        using var delivered = new BlockingCollection<AdhocFrame>();
        medium.FrameReceived += delivered.Add;
        medium.Create(Ssid);

        byte[] peer = AdhocMedium.RandomMac();
        var peerAddress = new IPEndPoint(IPAddress.Loopback, 5001);
        // a reset (Windows: an earlier send to a closed port) and a foreign datagram do not stop the receive
        listening.DeliverReset(peerAddress);
        listening.Deliver([1, 2, 3], peerAddress);
        listening.Deliver(AdhocWire.Data(peer, medium.Mac, medium.Bssid, Ssid, 0x88C8, [4, 2]), peerAddress);
        Assert.True(delivered.TryTake(out AdhocFrame? frame, s_guard));
        Assert.Equal(peer, frame!.Source);
        Assert.Equal([4, 2], frame.Payload);

        Assert.True(medium.Send(peer, 0x88C8, [1]));
        Assert.Contains(own.Sent, sent => sent.Target.Equals(peerAddress));

        // both receives pending again: the listening one after the reset, the foreign datagram and the frame
        WaitReceiving(listening, 4);
        WaitReceiving(own);
        DisposeWithin(medium);
        Assert.Equal(1, listening.Cancelled);
        Assert.Equal(1, own.Cancelled);
        int sentBefore = own.Sent.Count;
        medium.Send(peer, 0x88C8, [1]);
        Assert.Equal(sentBefore, own.Sent.Count); // nothing goes out after Dispose
    }
}
