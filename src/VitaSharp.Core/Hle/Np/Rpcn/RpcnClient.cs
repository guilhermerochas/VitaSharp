using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Np.Rpcn;

/// <summary>An RPCN account on a server (<c>--rpcn=host[:port] --rpcn-user=</c>, password from VITASHARP_RPCN_PASSWORD).</summary>
public sealed record RpcnConfig(string Host, int Port, string User, string Password, string Token = "")
{
    public const int DefaultPort = 31313;

    /// <summary>"host" or "host:port"; null when empty.</summary>
    public static (string Host, int Port)? ParseServer(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        int c = s.LastIndexOf(':');
        if (c > 0 && s.IndexOf(':') == c && int.TryParse(s[(c + 1)..], out int port) && port is > 0 and < 65536) return (s[..c], port);
        return (s, DefaultPort);
    }

    public override string ToString() => $"{User}@{Host}:{Port}"; // never the password
}

/// <summary>RPCN commands (RPCS3 rpcn_types.h, protocol <see cref="RpcnClient.ProtocolVersion"/>).</summary>
public enum RpcnCommand : ushort
{
    Login, Terminate, Create, Delete, SendToken, SendResetToken, ResetPassword, ResetState, AddFriend, RemoveFriend,
    AddBlock, RemoveBlock, GetServerList, GetWorldList, CreateRoom, JoinRoom, LeaveRoom, SearchRoom,
    GetRoomDataExternalList, SetRoomDataExternal, GetRoomDataInternal, SetRoomDataInternal, GetRoomMemberDataInternal,
    SetRoomMemberDataInternal, SetUserInfo, PingRoomOwner, SendRoomMessage, RequestSignalingInfos, RequestTicket,
    SendMessage, GetBoardInfos, RecordScore, RecordScoreData, GetScoreData, GetScoreRange, GetScoreFriends, GetScoreNpid,
    GetNetworkTime, TusSetMultiSlotVariable, TusGetMultiSlotVariable, TusGetMultiUserVariable, TusGetFriendsVariable,
    TusAddAndGetVariable, TusTryAndSetVariable, TusDeleteMultiSlotVariable, TusSetData, TusGetData,
    TusGetMultiSlotDataStatus, TusGetMultiUserDataStatus, TusGetFriendsDataStatus, TusDeleteMultiSlotData,
}

/// <summary>RPCN reply error byte (RPCS3 rpcn_types.h).</summary>
public enum RpcnError : byte
{
    NoError, Malformed, Invalid, InvalidInput, TooSoon, LoginError, LoginAlreadyLoggedIn, LoginInvalidUsername,
    LoginInvalidPassword, LoginInvalidToken, CreationError, CreationExistingUsername, CreationBannedEmailProvider,
    CreationExistingEmail, RoomMissing, RoomAlreadyJoined, RoomFull, RoomPasswordMismatch, RoomPasswordMissing,
    RoomGroupNoJoinLabel, RoomGroupFull, RoomGroupJoinLabelNotFound, RoomGroupMaxSlotMismatch, Unauthorized, DbFail,
    EmailFail, NotFound, Blocked, AlreadyFriend, ScoreNotBest, ScoreInvalid, ScoreHasData, CondFail, Unsupported,
}

/// <summary>A friend of the RPCN account (from the login reply).</summary>
public sealed record RpcnFriend(string Name, bool Online);

/// <summary>
/// Client of RPCN, RPCS3's open PSN replacement server. Transport: TLS over TCP (the servers use self-signed
/// certificates, which RPCS3 does not verify either). Packets: a 15-byte little-endian header (u8 type: 0 request,
/// 1 reply, 2 notification, 3 server info; u16 command; u32 total size; u64 packet id) and a payload. The server first
/// sends ServerInfo with its u32 protocol version; Login sends "user\0password\0token\0" and replies with the error
/// byte, online name, avatar URL, user id and the friend lists. Score/TUS requests carry the 12-byte communication id,
/// a u32 size and a protobuf message (np2_structs.proto); replies start with the error byte.
/// <para>Requests are synchronous: the calling guest thread releases its emulated core (<see cref="Scheduler.Block"/>)
/// for the round trip. The connection is opened (and the account logged in) on first use and again after a failure.
/// Notifications are read and dropped (friend presence, rooms: not implemented).</para>
/// </summary>
public sealed class RpcnClient : IDisposable
{
    public const uint ProtocolVersion = 34;
    public const int HeaderSize = 15;
    const byte PacketRequest = 0, PacketReply = 1, PacketNotification = 2, PacketServerInfo = 3;

    public RpcnConfig Config { get; }
    readonly NetConfig _net;
    readonly object _io = new();
    TcpClient? _tcp;
    Stream? _stream;
    ulong _nextPacket = 1;
    DateTime _retryAfter;

    /// <summary>Reply timeout (RPCS3 RPCN_TIMEOUT).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Tests: plain TCP instead of TLS.</summary>
    public bool UseTls { get; init; } = true;

    public string? OnlineName { get; private set; }
    public IReadOnlyList<RpcnFriend> Friends { get; private set; } = [];

    public RpcnClient(RpcnConfig config, NetConfig net)
    {
        Config = config;
        _net = net;
    }

    public bool Connected { get { lock (_io) return _stream != null; } }

    /// <summary>Sends a request and returns the reply's error byte and payload (after the error byte). An unreachable,
    /// refused or misbehaving server throws <see cref="IOException"/>.</summary>
    public (RpcnError Error, byte[] Payload) Request(RpcnCommand cmd, byte[] payload)
    {
        using var blocked = Scheduler.Block();
        lock (_io)
        {
            EnsureLoggedIn();
            try { return Exchange(cmd, payload); }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidDataException)
            {
                Drop();
                throw new IOException($"RPCN {Config.Host}: {cmd} failed: {e.Message}", e);
            }
        }
    }

    /// <summary>A request with the communication id and a protobuf message (RPCS3 forge_request_with_com_id).</summary>
    public (RpcnError Error, byte[] Payload) Request(RpcnCommand cmd, string commId, ProtoWriter message) =>
        Request(cmd, WithCommId(commId, message.ToArray()));

    public static byte[] WithCommId(string commId, ReadOnlySpan<byte> body, ReadOnlySpan<byte> trailer = default, bool trailerSized = false)
    {
        var id = CommIdBytes(commId);
        int extra = trailerSized ? 4 + trailer.Length : trailer.Length;
        var b = new byte[12 + 4 + body.Length + extra];
        id.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)body.Length);
        body.CopyTo(b.AsSpan(16));
        int o = 16 + body.Length;
        if (trailerSized) { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), (uint)trailer.Length); o += 4; }
        trailer.CopyTo(b.AsSpan(o));
        return b;
    }

    /// <summary>"NPWR12345_00" as the 12 bytes RPCN expects.</summary>
    public static byte[] CommIdBytes(string commId)
    {
        var b = new byte[12];
        Encoding.ASCII.GetBytes(commId.Length > 12 ? commId[..12] : commId).CopyTo(b, 0);
        return b;
    }

    void EnsureLoggedIn()
    {
        if (_stream != null) return;
        if (DateTime.UtcNow < _retryAfter) throw new IOException($"RPCN {Config.Host}: not connected (retrying later)");
        try { Connect(); }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or ObjectDisposedException)
        {
            Drop();
            _retryAfter = DateTime.UtcNow.AddSeconds(15); // do not hammer a server that is down
            Log.Warn($"RPCN {Config}: {e.Message}");
            throw e as IOException ?? new IOException(e.Message, e);
        }
    }

    void Connect()
    {
        if (!_net.Permits(Config.Host, out string why))
        {
            _net.LogBlocked($"RPCN server {Config.Host}", why);
            throw new IOException($"server not allowed: {why}");
        }
        _net.LogContact($"RPCN server {Config.Host}:{Config.Port} as {Config.User}");
        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = (int)Timeout.TotalMilliseconds, SendTimeout = (int)Timeout.TotalMilliseconds };
        var connect = tcp.ConnectAsync(Config.Host, Config.Port);
        if (!connect.Wait(Timeout)) { tcp.Dispose(); throw new IOException("connection timed out"); }
        if (connect.IsFaulted) { tcp.Dispose(); throw new IOException(connect.Exception!.InnerException?.Message ?? "connection failed"); }
        Stream stream = tcp.GetStream();
        if (UseTls)
        {
            // RPCN servers use self-signed certificates; RPCS3 does not verify them either
            var ssl = new SslStream(stream, false, (_, _, _, _) => true);
            try { ssl.AuthenticateAsClient(Config.Host); }
            catch (System.Security.Authentication.AuthenticationException e) { tcp.Dispose(); throw new IOException($"TLS handshake failed: {e.Message}", e); }
            stream = ssl;
        }
        _tcp = tcp;
        _stream = stream;
        // the server announces its protocol version first
        var (type, _, _, data) = ReadPacket();
        if (type != PacketServerInfo || data.Length != 4) throw new InvalidDataException("expected the ServerInfo packet");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (version != ProtocolVersion) throw new InvalidDataException($"server protocol version {version}, VitaSharp speaks {ProtocolVersion}");
        Login();
    }

    void Login()
    {
        var ms = new MemoryStream();
        foreach (var s in new[] { Config.User, Config.Password, Config.Token })
        {
            ms.Write(Encoding.UTF8.GetBytes(s));
            ms.WriteByte(0);
        }
        var (err, payload) = Exchange(RpcnCommand.Login, ms.ToArray());
        if (err != RpcnError.NoError) throw new IOException($"login as {Config.User} refused ({err})");
        var r = new RpcnReader(payload);
        OnlineName = r.String();
        r.String(); // avatar URL
        r.U64(); // user id
        var friends = new List<RpcnFriend>();
        uint n = r.U32();
        for (uint i = 0; i < n && !r.Error; i++)
        {
            string name = r.String();
            bool online = r.U8() != 0;
            r.Skip(12); // presence communication id
            r.String(); r.String(); r.String(); // presence title, status, comment
            r.Raw(); // presence data
            friends.Add(new RpcnFriend(name, online));
        }
        Friends = friends;
        Log.Info($"RPCN: logged in to {Config.Host} as {Config.User} ({OnlineName}), {friends.Count} friend(s)");
    }

    (RpcnError, byte[]) Exchange(RpcnCommand cmd, byte[] payload)
    {
        ulong id = _nextPacket++;
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = PacketRequest;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)cmd);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), (uint)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(7), id);
        payload.CopyTo(packet, HeaderSize);
        _stream!.Write(packet);
        _stream.Flush();
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (type, rcmd, rid, data) = ReadPacket();
            if (type == PacketNotification || type == PacketServerInfo) continue; // not implemented (friends, rooms)
            if (type != PacketReply) throw new InvalidDataException($"unexpected packet type {type}");
            if (rid != id) continue; // a late reply of an earlier request
            if (data.Length == 0) throw new InvalidDataException($"empty reply to {(RpcnCommand)rcmd}");
            return ((RpcnError)data[0], data[1..]);
        }
        throw new IOException($"no reply to {cmd}");
    }

    (byte Type, ushort Cmd, ulong Id, byte[] Data) ReadPacket()
    {
        Span<byte> h = stackalloc byte[HeaderSize];
        _stream!.ReadExactly(h);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(h[3..]);
        if (size < HeaderSize || size > 64u << 20) throw new InvalidDataException($"invalid packet size {size}");
        var data = new byte[size - HeaderSize];
        _stream.ReadExactly(data);
        return (h[0], BinaryPrimitives.ReadUInt16LittleEndian(h[1..]), BinaryPrimitives.ReadUInt64LittleEndian(h[7..]), data);
    }

    void Drop()
    {
        try { _stream?.Dispose(); } catch (IOException) { }
        try { _tcp?.Dispose(); } catch (SocketException) { }
        _stream = null;
        _tcp = null;
    }

    public void Dispose()
    {
        lock (_io) Drop();
    }
}

/// <summary>Little-endian reader of RPCN payloads (RPCS3 vec_stream): sets <see cref="Error"/> instead of throwing.</summary>
public sealed class RpcnReader(byte[] data)
{
    int _i;
    public bool Error { get; private set; }
    public int Remaining => data.Length - _i;

    bool Need(int n) { if (Error || _i + n > data.Length) { Error = true; return false; } return true; }
    public byte U8() => Need(1) ? data[_i++] : (byte)0;
    public uint U32() { if (!Need(4)) return 0; uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_i)); _i += 4; return v; }
    public ulong U64() { if (!Need(8)) return 0; ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_i)); _i += 8; return v; }
    public void Skip(int n) { if (Need(n)) _i += n; }

    public string String()
    {
        int z = Array.IndexOf(data, (byte)0, Math.Min(_i, data.Length));
        if (Error || z < 0) { Error = true; return ""; }
        string s = Encoding.UTF8.GetString(data, _i, z - _i);
        _i = z + 1;
        return s;
    }

    /// <summary>u32 size + bytes (RPCS3 get_rawdata).</summary>
    public byte[] Raw()
    {
        uint n = U32();
        if (!Need((int)Math.Min(n, int.MaxValue))) return [];
        var b = data.AsSpan(_i, (int)n).ToArray();
        _i += (int)n;
        return b;
    }

    /// <summary>u32 size + protobuf message (RPCS3 get_protobuf).</summary>
    public ProtoReader Proto()
    {
        var b = Raw();
        if (Error) throw new InvalidDataException("RPCN: malformed reply");
        return new ProtoReader(b);
    }
}
