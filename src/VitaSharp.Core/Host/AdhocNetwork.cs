using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Host;

/// <summary>An ad hoc network heard on the medium (an 802.11 IBSS): its SSID, its BSSID and the stations in it.</summary>
public sealed record AdhocNetworkInfo(string Ssid, byte[] Bssid, int Stations);

/// <summary>A station of the joined ad hoc network: its MAC and the nickname its beacons carry.</summary>
public sealed record AdhocPeer(byte[] Mac, string Nickname);

/// <summary>A link-layer frame of the joined network: source and destination MAC, EtherType and payload (what the
/// Vita's raw ad hoc socket, SceNetPs type 9, sends and receives; pspnet_adhoc uses EtherType 0x88C8).</summary>
public sealed record AdhocFrame(byte[] Source, byte[] Destination, ushort EtherType, byte[] Payload);

/// <summary>
/// The Vita's Wi-Fi in ad hoc mode as a medium shared by emulator instances. FW 3.74 joins an IBSS (an ad hoc network
/// named by its SSID): creating one makes the unit send beacons with a new BSSID, scanning lists the networks whose
/// beacons are heard, joining takes the BSSID of a heard network; every station of an IBSS sends beacons, so each one
/// knows the others. Frames go to one MAC or to all (ff:ff:ff:ff:ff:ff) of the same BSSID. Thread-safe.
/// </summary>
public abstract class AdhocMedium : IDisposable
{
    /// <summary>The 802.11 beacon interval (100 TU, 102.4 ms), rounded.</summary>
    public const int BeaconIntervalMs = 100;

    /// <summary>A network or station not heard for this long is gone (the Vita drops a peer after a few seconds).</summary>
    public const int StationTimeoutMs = 3000;

    /// <summary>The longest SSID (802.11).</summary>
    public const int SsidMax = 32;

    /// <summary>The longest frame payload (the 802.11 MSDU).</summary>
    public const int PayloadMax = 2304;

    /// <summary>The broadcast MAC.</summary>
    public static readonly byte[] Broadcast = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private readonly object _lock = new();
    private readonly Dictionary<string, Station> _stations = new();
    private string? _ssid;
    private byte[] _bssid = new byte[6];

    /// <summary>A station heard: its network and when.</summary>
    private sealed class Station
    {
        public byte[] Mac = new byte[6];
        public string Ssid = "";
        public byte[] Bssid = new byte[6];
        public string Nickname = "";
        public long HeardMs;
    }

    /// <param name="mac">The unit's MAC (6 bytes).</param>
    /// <param name="nickname">The nickname the beacons carry (the user name).</param>
    protected AdhocMedium(byte[] mac, string nickname)
    {
        Mac = mac.ToArray();
        Nickname = nickname;
    }

    /// <summary>The unit's MAC on the ad hoc network.</summary>
    public byte[] Mac { get; }

    /// <summary>The nickname of the unit's beacons.</summary>
    public string Nickname { get; set; }

    /// <summary>A frame of the joined network for this unit (to its MAC or broadcast) arrived; raised on the medium's
    /// receive thread.</summary>
    public event Action<AdhocFrame>? FrameReceived;

    /// <summary>The joined network changed or a station of it came or left.</summary>
    public event Action? PeersChanged;

    /// <summary>The SSID of the joined network, null when none.</summary>
    public string? Ssid
    {
        get
        {
            lock (_lock)
            {
                return _ssid;
            }
        }
    }

    /// <summary>The BSSID of the joined network (zeros when none).</summary>
    public byte[] Bssid
    {
        get
        {
            lock (_lock)
            {
                return _bssid.ToArray();
            }
        }
    }

    /// <summary>A random MAC with Sony's OUI (00:1F:A7, a Vita's), so two instances never share one.</summary>
    public static byte[] RandomMac()
    {
        byte[] mac = [0x00, 0x1F, 0xA7, 0, 0, 0];
        Random.Shared.NextBytes(mac.AsSpan(3));
        return mac;
    }

    /// <summary>Creates the IBSS <paramref name="ssid"/> and joins it: a new BSSID (locally administered) and beacons.</summary>
    public void Create(string ssid)
    {
        byte[] bssid = new byte[6];
        Random.Shared.NextBytes(bssid);
        bssid[0] = (byte)((bssid[0] & 0xFC) | 0x02);
        Enter(ssid, bssid);
    }

    /// <summary>Joins the heard IBSS <paramref name="ssid"/> (its BSSID); false when no beacon of it was heard.</summary>
    public bool Join(string ssid)
    {
        AdhocNetworkInfo? network = Scan().FirstOrDefault(n => n.Ssid == ssid);
        if (network == null)
        {
            return false;
        }

        Enter(ssid, network.Bssid);
        return true;
    }

    /// <summary>Leaves the joined network: no more beacons, no frames.</summary>
    public void Leave()
    {
        lock (_lock)
        {
            _ssid = null;
            _bssid = new byte[6];
        }

        PeersChanged?.Invoke();
    }

    /// <summary>The networks whose beacons were heard lately (a passive scan), one per BSSID.</summary>
    public List<AdhocNetworkInfo> Scan()
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            return _stations.Values
                .Where(s => now - s.HeardMs < StationTimeoutMs)
                .GroupBy(s => (s.Ssid, Convert.ToHexString(s.Bssid)))
                .Select(g => new AdhocNetworkInfo(g.Key.Ssid, g.First().Bssid.ToArray(), g.Count()))
                .ToList();
        }
    }

    /// <summary>The other stations of the joined network heard lately.</summary>
    public List<AdhocPeer> Peers()
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (_ssid == null)
            {
                return [];
            }

            return _stations.Values
                .Where(s => now - s.HeardMs < StationTimeoutMs && s.Ssid == _ssid && s.Bssid.AsSpan().SequenceEqual(_bssid))
                .Select(s => new AdhocPeer(s.Mac.ToArray(), s.Nickname))
                .ToList();
        }
    }

    /// <summary>Sends a frame to <paramref name="destination"/> (a peer's MAC or <see cref="Broadcast"/>) on the joined
    /// network; false when no network is joined or the payload is too long.</summary>
    public bool Send(ReadOnlySpan<byte> destination, ushort etherType, ReadOnlySpan<byte> payload)
    {
        string ssid;
        byte[] bssid;
        lock (_lock)
        {
            if (_ssid == null || payload.Length > PayloadMax)
            {
                return false;
            }

            ssid = _ssid;
            bssid = _bssid;
        }

        byte[] datagram = AdhocWire.Data(Mac, destination, bssid, ssid, etherType, payload);
        Transmit(datagram, destination);
        return true;
    }

    /// <summary>Sends the unit's beacon now when a network is joined (the medium also does it every
    /// <see cref="BeaconIntervalMs"/>).</summary>
    public void Beacon()
    {
        string ssid;
        byte[] bssid;
        lock (_lock)
        {
            if (_ssid == null)
            {
                return;
            }

            ssid = _ssid;
            bssid = _bssid;
        }

        Transmit(AdhocWire.Beacon(Mac, bssid, ssid, Nickname), Broadcast);
    }

    /// <summary>Puts <paramref name="datagram"/> on the medium (<paramref name="destination"/> lets a medium skip stations
    /// that are not addressed).</summary>
    protected abstract void Transmit(byte[] datagram, ReadOnlySpan<byte> destination);

    /// <summary>A datagram the medium received: beacons update the stations, frames of the joined network for this unit
    /// are raised. Returns the sender's MAC (null for a foreign or malformed datagram, or the unit's own).</summary>
    protected byte[]? Receive(ReadOnlySpan<byte> datagram)
    {
        if (!AdhocWire.TryParse(datagram, out AdhocWire.Parsed packet) || packet.Source.AsSpan().SequenceEqual(Mac))
        {
            return null;
        }

        bool changed = false;
        bool deliver = false;
        lock (_lock)
        {
            string key = Convert.ToHexString(packet.Source);
            if (!_stations.TryGetValue(key, out Station? station))
            {
                station = new Station { Mac = packet.Source };
                _stations[key] = station;
                changed = true;
            }

            if (station.Ssid != packet.Ssid || !station.Bssid.AsSpan().SequenceEqual(packet.Bssid))
            {
                changed = true;
            }

            station.Ssid = packet.Ssid;
            station.Bssid = packet.Bssid;
            station.HeardMs = Environment.TickCount64;
            if (packet.Nickname != null)
            {
                station.Nickname = packet.Nickname;
            }

            deliver = !packet.IsBeacon && _ssid == packet.Ssid && _bssid.AsSpan().SequenceEqual(packet.Bssid)
                && (packet.Destination.AsSpan().SequenceEqual(Mac) || packet.Destination.AsSpan().SequenceEqual(Broadcast));
            changed &= _ssid == packet.Ssid;
        }

        if (changed)
        {
            PeersChanged?.Invoke();
        }

        if (deliver)
        {
            FrameReceived?.Invoke(new AdhocFrame(packet.Source, packet.Destination, packet.EtherType, packet.Payload));
        }

        return packet.Source;
    }

    private void Enter(string ssid, byte[] bssid)
    {
        if (ssid.Length == 0 || Encoding.UTF8.GetByteCount(ssid) > SsidMax)
        {
            throw new ArgumentException("an SSID has 1 to 32 bytes", nameof(ssid));
        }

        lock (_lock)
        {
            _ssid = ssid;
            _bssid = bssid.ToArray();
        }

        Beacon();
        PeersChanged?.Invoke();
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

/// <summary>The medium's datagram (big-endian): "VSAH", version 1, kind (1 beacon, 2 frame), source MAC, destination
/// MAC, BSSID, SSID (length + bytes); a beacon then has the nickname (length + bytes), a frame its EtherType and
/// payload.</summary>
internal static class AdhocWire
{
    private const uint Magic = 0x56534148; // "VSAH"
    private const byte Version = 1, KindBeacon = 1, KindFrame = 2;
    private const int NicknameMax = 128;

    internal readonly record struct Parsed(bool IsBeacon, byte[] Source, byte[] Destination, byte[] Bssid, string Ssid,
        string? Nickname, ushort EtherType, byte[] Payload);

    internal static byte[] Beacon(byte[] source, byte[] bssid, string ssid, string nickname)
    {
        byte[] nick = Encoding.UTF8.GetBytes(nickname);
        if (nick.Length > NicknameMax)
        {
            nick = nick.AsSpan(0, NicknameMax).ToArray();
        }

        var bytes = new List<byte>(64);
        Header(bytes, KindBeacon, source, AdhocMedium.Broadcast, bssid, ssid);
        bytes.Add((byte)nick.Length);
        bytes.AddRange(nick);
        return bytes.ToArray();
    }

    internal static byte[] Data(byte[] source, ReadOnlySpan<byte> destination, byte[] bssid, string ssid, ushort etherType,
        ReadOnlySpan<byte> payload)
    {
        var bytes = new List<byte>(64 + payload.Length);
        Header(bytes, KindFrame, source, destination, bssid, ssid);
        bytes.Add((byte)(etherType >> 8));
        bytes.Add((byte)etherType);
        bytes.AddRange(payload.ToArray());
        return bytes.ToArray();
    }

    internal static bool TryParse(ReadOnlySpan<byte> d, out Parsed packet)
    {
        packet = default;
        const int Fixed = 4 + 1 + 1 + 6 + 6 + 6 + 1;
        if (d.Length < Fixed || BinaryPrimitives.ReadUInt32BigEndian(d) != Magic || d[4] != Version)
        {
            return false;
        }

        byte kind = d[5];
        int ssidLength = d[24];
        int at = Fixed + ssidLength;
        if (ssidLength is 0 or > AdhocMedium.SsidMax || d.Length < at)
        {
            return false;
        }

        string ssid = Encoding.UTF8.GetString(d.Slice(Fixed, ssidLength));
        byte[] source = d.Slice(6, 6).ToArray();
        byte[] destination = d.Slice(12, 6).ToArray();
        byte[] bssid = d.Slice(18, 6).ToArray();
        if (kind == KindBeacon)
        {
            if (d.Length < at + 1 || d.Length < at + 1 + d[at])
            {
                return false;
            }

            string nickname = Encoding.UTF8.GetString(d.Slice(at + 1, d[at]));
            packet = new Parsed(true, source, destination, bssid, ssid, nickname, 0, []);
            return true;
        }

        if (kind != KindFrame || d.Length < at + 2 || d.Length - at - 2 > AdhocMedium.PayloadMax)
        {
            return false;
        }

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(at));
        packet = new Parsed(false, source, destination, bssid, ssid, null, etherType, d.Slice(at + 2).ToArray());
        return true;
    }

    private static void Header(List<byte> bytes, byte kind, byte[] source, ReadOnlySpan<byte> destination, byte[] bssid,
        string ssid)
    {
        byte[] name = Encoding.UTF8.GetBytes(ssid);
        bytes.AddRange([0x56, 0x53, 0x41, 0x48, Version, kind]);
        bytes.AddRange(source);
        bytes.AddRange(destination.ToArray());
        bytes.AddRange(bssid);
        bytes.Add((byte)name.Length);
        bytes.AddRange(name);
    }
}

/// <summary>A datagram a link received: its length in the buffer and the address it came from.</summary>
internal readonly record struct AdhocDatagram(int Length, IPEndPoint From);

/// <summary>
/// One UDP endpoint of <see cref="UdpAdhocMedium"/>. A receive ends when a datagram arrives or when the token is
/// cancelled: closing the endpoint is not a way to stop it, because a UDP receive blocked in the kernel is not woken by
/// closing its socket on every host (macOS: .NET's SafeSocketHandle then spins in Dispose until the receive returns,
/// which for UDP is never).
/// </summary>
internal interface IAdhocLink : IDisposable
{
    /// <summary>The local UDP port.</summary>
    int LocalPort { get; }

    /// <summary>Waits for the next datagram into <paramref name="buffer"/>; throws <see cref="OperationCanceledException"/>
    /// when <paramref name="cancel"/> is cancelled first.</summary>
    ValueTask<AdhocDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancel);

    /// <summary>Sends <paramref name="datagram"/> to <paramref name="target"/>.</summary>
    void SendTo(byte[] datagram, IPEndPoint target);
}

/// <summary>An <see cref="IAdhocLink"/> on a host UDP socket (receives through the socket's asynchronous path, which
/// every host can cancel).</summary>
internal sealed class SocketAdhocLink : IAdhocLink
{
    private static readonly IPEndPoint s_anySender = new(IPAddress.Any, 0);

    private readonly Socket _socket;

    private SocketAdhocLink(Socket socket)
    {
        _socket = socket;
    }

    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    /// <summary>The medium's listening socket on <paramref name="port"/> (shared by the instances of one PC), in the LAN
    /// multicast group when <paramref name="multicast"/>.</summary>
    public static SocketAdhocLink Listening(int port, bool multicast)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // several instances on one PC share the port: each one receives the group's datagrams
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        if (multicast)
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(UdpAdhocMedium.DefaultGroup));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        }

        return new SocketAdhocLink(socket);
    }

    /// <summary>The instance's own socket on a free port (what it sends from, so its address is unique).</summary>
    public static SocketAdhocLink Own(bool multicast)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        if (multicast)
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        }

        return new SocketAdhocLink(socket);
    }

    public async ValueTask<AdhocDatagram> ReceiveAsync(Memory<byte> buffer, CancellationToken cancel)
    {
        SocketReceiveFromResult result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, s_anySender, cancel).ConfigureAwait(false);
        return new AdhocDatagram(result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint);
    }

    public void SendTo(byte[] datagram, IPEndPoint target)
    {
        _socket.SendTo(datagram, target);
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}

/// <summary>
/// The ad hoc medium over the host's LAN: UDP datagrams to a multicast group of the local network (TTL 1, never
/// routed) and to the stations given by address (a VPN, or another PC where multicast is filtered); every instance on
/// the LAN, or on the same PC, that has the option on hears the others. No server. Off by default (the "Ad hoc pela
/// rede local" setting, --adhoc-lan); the host firewall must let the port in (UDP, <see cref="DefaultPort"/>).
/// Each instance sends from its own socket (a free port), so a station's address is unique even when instances on one
/// PC share the listening port; unicast frames go to the address the destination station was last heard from.
/// The receives wait for a datagram or for <see cref="Dispose"/>, which cancels them (as the Vita's ad hoc receive ends
/// on its timeout or when the socket is deleted), and only then closes the sockets.
/// </summary>
public sealed class UdpAdhocMedium : AdhocMedium
{
    /// <summary>The UDP port of the medium.</summary>
    public const int DefaultPort = 30860;

    /// <summary>The multicast group (organisation-local scope, 239.255.0.0/16).</summary>
    public static readonly IPAddress DefaultGroup = IPAddress.Parse("239.255.86.83");

    private readonly IAdhocLink _listening;
    private readonly IAdhocLink _own;
    private readonly IPEndPoint? _group;
    private readonly List<IPEndPoint> _stations = new();
    private readonly Dictionary<string, IPEndPoint> _heardFrom = new();
    private readonly object _addressLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiver;
    private readonly Task _ownReceiver;
    private readonly Timer _beacons;
    private volatile bool _disposed;

    /// <param name="mac">The unit's MAC.</param>
    /// <param name="nickname">The beacons' nickname.</param>
    /// <param name="port">The UDP port (0 = any free one, for tests: <see cref="LocalPort"/> tells which).</param>
    /// <param name="multicast">Whether to use the LAN multicast group (off: only the stations added by address).</param>
    public UdpAdhocMedium(byte[] mac, string nickname, int port = DefaultPort, bool multicast = true)
        : this(mac, nickname, SocketAdhocLink.Listening(port, multicast), SocketAdhocLink.Own(multicast),
            multicast ? new IPEndPoint(DefaultGroup, port) : null)
    {
    }

    /// <summary>The medium on the given links (tests give links that model a host's sockets).</summary>
    internal UdpAdhocMedium(byte[] mac, string nickname, IAdhocLink listening, IAdhocLink own, IPEndPoint? group)
        : base(mac, nickname)
    {
        _listening = listening;
        _own = own;
        _group = group;
        CancellationToken stop = _stop.Token;
        _receiver = Task.Run(() => ReceiveLoop(_listening, stop));
        _ownReceiver = Task.Run(() => ReceiveLoop(_own, stop));
        _beacons = new Timer(_ => SendBeacon(), null, BeaconIntervalMs, BeaconIntervalMs);
    }

    /// <summary>The UDP port the medium listens on.</summary>
    public int LocalPort => _listening.LocalPort;

    /// <summary>Adds a station by address (datagrams go there too, besides the multicast group).</summary>
    public void AddStation(IPEndPoint address)
    {
        lock (_addressLock)
        {
            _stations.Add(address);
        }
    }

    /// <summary>Parses "host:port" (port optional, <see cref="DefaultPort"/>) of the --adhoc-peer option.</summary>
    public static IPEndPoint? ParseStation(string text)
    {
        string host = text.Trim();
        int port = DefaultPort;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host.AsSpan(colon + 1), out int p) && p is > 0 and < 65536)
        {
            port = p;
            host = host[..colon];
        }

        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return new IPEndPoint(address, port);
        }

        try
        {
            IPAddress? resolved = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return resolved == null ? null : new IPEndPoint(resolved, port);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    protected override void Transmit(byte[] datagram, ReadOnlySpan<byte> destination)
    {
        if (_disposed)
        {
            return;
        }

        List<IPEndPoint> targets;
        lock (_addressLock)
        {
            if (!destination.SequenceEqual(Broadcast) && _heardFrom.TryGetValue(Convert.ToHexString(destination), out IPEndPoint? known))
            {
                targets = [known];
            }
            else
            {
                targets = new List<IPEndPoint>(_stations);
                if (_group != null)
                {
                    targets.Add(_group);
                }
            }
        }

        foreach (IPEndPoint target in targets)
        {
            try
            {
                _own.SendTo(datagram, target);
            }
            catch (SocketException e)
            {
                Log.WarnOnce("adhoc-lan-send" + target, $"ad hoc LAN: cannot send to {target}: {e.SocketErrorCode}");
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void SendBeacon()
    {
        if (!_disposed)
        {
            Beacon();
        }
    }

    private async Task ReceiveLoop(IAdhocLink link, CancellationToken stop)
    {
        byte[] buffer = new byte[4096];
        while (!stop.IsCancellationRequested)
        {
            AdhocDatagram datagram;
            try
            {
                datagram = await link.ReceiveAsync(buffer, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // Windows reports an earlier send to a closed port here
            }
            catch (SocketException e)
            {
                Log.WarnOnce("adhoc-lan-receive", $"ad hoc LAN: receive stopped: {e.SocketErrorCode}");
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            byte[]? source = Receive(buffer.AsSpan(0, datagram.Length));
            if (source != null)
            {
                lock (_addressLock)
                {
                    _heardFrom[Convert.ToHexString(source)] = datagram.From;
                }
            }
        }
    }

    /// <summary>Stops the beacons, cancels the receives and waits for them to end, then closes the sockets (closing a
    /// socket with a receive still pending would wait for that receive on macOS).</summary>
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _beacons.Dispose();
        _stop.Cancel();
        try
        {
            Task.WaitAll(_receiver, _ownReceiver);
        }
        catch (AggregateException e)
        {
            Log.Warn($"ad hoc LAN: a receive ended with {e.InnerException?.GetType().Name}: {e.InnerException?.Message}");
        }

        _listening.Dispose();
        _own.Dispose();
        _stop.Dispose();
        base.Dispose();
    }
}
