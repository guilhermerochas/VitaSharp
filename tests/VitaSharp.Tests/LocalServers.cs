using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VitaSharp.Core.Hle.Np.Rpcn;

namespace VitaSharp.Tests;

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 (TcpListener: no URL ACL needed) for the real-internet SceHttp tests. Routes:
/// /hello (Content-Length body), /chunked (chunked transfer), /redirect (302 to /hello), /echo (POST: echoes the body,
/// X-Test header and Cookie header), /cookie (Set-Cookie), /slow (headers, then the body after a delay), /gzip (gzip body).
/// Every connection is closed after one response.
/// </summary>
public sealed class LocalHttpServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    public int Port { get; }
    public string Base => $"http://127.0.0.1:{Port}";
    public int Requests;
    public readonly ManualResetEventSlim ReleaseSlow = new();

    public LocalHttpServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    async Task Serve(TcpClient c)
    {
        using var _ = c;
        try
        {
            var s = c.GetStream();
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n"))
            {
                if (await s.ReadAsync(one) == 0) return;
                head.Append((char)one[0]);
            }
            Interlocked.Increment(ref Requests);
            var lines = head.ToString().Split("\r\n");
            var parts = lines[0].Split(' ');
            string method = parts[0], path = parts[1];
            var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim(), l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
            byte[] body = [];
            if (headers.TryGetValue("Content-Length", out var cl) && int.Parse(cl) > 0)
            {
                body = new byte[int.Parse(cl)];
                await s.ReadExactlyAsync(body);
            }
            async Task Send(string status, string extraHeaders, byte[] content, bool chunked = false)
            {
                var h = $"HTTP/1.1 {status}\r\nConnection: close\r\n{extraHeaders}" +
                        (chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {content.Length}\r\n") + "\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(h));
                if (!chunked) await s.WriteAsync(content);
                else
                {
                    for (int i = 0; i < content.Length; i += 7)
                    {
                        int n = Math.Min(7, content.Length - i);
                        await s.WriteAsync(Encoding.ASCII.GetBytes($"{n:X}\r\n"));
                        await s.WriteAsync(content.AsMemory(i, n));
                        await s.WriteAsync("\r\n"u8.ToArray());
                        await s.FlushAsync();
                    }
                    await s.WriteAsync("0\r\n\r\n"u8.ToArray());
                }
                await s.FlushAsync();
            }
            switch (path)
            {
                case "/hello": await Send("200 OK", "Content-Type: text/plain\r\nX-Server: local\r\n", "hello vita"u8.ToArray()); break;
                case "/chunked": await Send("200 OK", "", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 20).Select(i => $"line {i}\n"))), chunked: true); break;
                case "/redirect": await Send("302 Found", "Location: /hello\r\n", []); break;
                case "/cookie": await Send("200 OK", "Set-Cookie: session=abc123; Path=/\r\n", "ok"u8.ToArray()); break;
                case "/echo":
                    string info = $"{method} x-test={headers.GetValueOrDefault("X-Test")} cookie={headers.GetValueOrDefault("Cookie")} ua={headers.GetValueOrDefault("User-Agent")} body=";
                    await Send("201 Created", "", [.. Encoding.ASCII.GetBytes(info), .. body]);
                    break;
                case "/slow":
                    await s.WriteAsync("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 4\r\n\r\n"u8.ToArray());
                    await s.FlushAsync();
                    ReleaseSlow.Wait(TimeSpan.FromSeconds(30));
                    await s.WriteAsync("done"u8.ToArray());
                    break;
                case "/gzip":
                    using (var ms = new MemoryStream())
                    {
                        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) gz.Write("compressed body"u8);
                        await Send("200 OK", "Content-Encoding: gzip\r\n", ms.ToArray());
                    }
                    break;
                default: await Send("404 Not Found", "", "missing"u8.ToArray()); break;
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
    }

    public void Dispose()
    {
        ReleaseSlow.Set();
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>
/// A mock RPCN server on 127.0.0.1 speaking the RPCN packet protocol (RPCS3 rpcn_client.cpp, protocol 34) for the
/// login, Score and TUS commands, with in-memory data. TLS with a throwaway self-signed certificate, or plain TCP.
/// </summary>
public sealed class MockRpcnServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    readonly X509Certificate2? _cert;
    public int Port { get; }
    public uint Version { get; init; } = RpcnClient.ProtocolVersion;
    public string User = "Tester", Password = "secret";
    public readonly List<RpcnCommand> Commands = new();
    public int Logins;

    // TUS: (comm, owner, slot) -> variable / data
    readonly Dictionary<(string, string, int), (long Value, long Old, ulong Date, string Author)> _vars = new();
    readonly Dictionary<(string, string, int), (byte[] Data, byte[] Info, ulong Date, string Author)> _data = new();
    // Score: (comm, board) -> entries
    readonly Dictionary<(string, uint), List<(string NpId, int PcId, long Score, string Comment, byte[] Info, ulong Date, byte[]? GameData)>> _boards = new();

    public MockRpcnServer(bool tls = true)
    {
        if (tls)
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=rpcn-mock", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _cert = X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
        }
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    static byte[] Packet(byte type, ushort cmd, ulong id, byte[] data)
    {
        var p = new byte[RpcnClient.HeaderSize + data.Length];
        p[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), cmd);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(3), (uint)p.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(7), id);
        data.CopyTo(p, RpcnClient.HeaderSize);
        return p;
    }

    void Serve(TcpClient c)
    {
        using var _ = c;
        try
        {
            Stream s = c.GetStream();
            if (_cert != null)
            {
                var ssl = new SslStream(s);
                ssl.AuthenticateAsServer(_cert);
                s = ssl;
            }
            var ver = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(ver, Version);
            s.Write(Packet(3, 0, 0, ver));
            // an unsolicited notification first: the client must skip it
            s.Write(Packet(2, 8, 0, "x\0"u8.ToArray()));
            bool logged = false;
            var h = new byte[RpcnClient.HeaderSize];
            while (true)
            {
                s.ReadExactly(h);
                var cmd = (RpcnCommand)BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(1));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(3));
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(7));
                var data = new byte[size - RpcnClient.HeaderSize];
                s.ReadExactly(data);
                lock (Commands) Commands.Add(cmd);
                byte[] reply;
                if (cmd == RpcnCommand.Login)
                {
                    var f = Encoding.UTF8.GetString(data).Split('\0');
                    if (f[0] != User || f[1] != Password) reply = [(byte)RpcnError.LoginInvalidPassword];
                    else
                    {
                        logged = true;
                        Interlocked.Increment(ref Logins);
                        var ms = new MemoryStream();
                        ms.WriteByte(0);
                        ms.Write(Encoding.UTF8.GetBytes(User + " Online\0http://avatar\0"));
                        ms.Write(BitConverter.GetBytes(42L));
                        ms.Write(BitConverter.GetBytes(1u)); // one friend
                        ms.Write("Buddy\0"u8);
                        ms.WriteByte(1);
                        ms.Write(new byte[12]);
                        ms.Write("\0\0\0"u8);
                        ms.Write(BitConverter.GetBytes(0u));
                        for (int i = 0; i < 3; i++) ms.Write(BitConverter.GetBytes(0u)); // requests sent/received, blocked
                        reply = ms.ToArray();
                    }
                }
                else if (!logged) reply = [(byte)RpcnError.Invalid];
                else reply = Handle(cmd, data);
                s.Write(Packet(1, (ushort)cmd, id, reply));
                s.Flush();
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException) { }
    }

    static byte[] Ok(ProtoWriter m) { var b = m.ToArray(); return [0, .. BitConverter.GetBytes((uint)b.Length), .. b]; }
    static byte[] Err(RpcnError e) => [(byte)e];

    byte[] Handle(RpcnCommand cmd, byte[] data)
    {
        string comm = Encoding.ASCII.GetString(data, 0, 12).TrimEnd('\0');
        if (cmd == RpcnCommand.GetBoardInfos)
        {
            uint board = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
            if (board == 99) return Err(RpcnError.NotFound);
            return Ok(new ProtoWriter().UInt32(1, 100).UInt32(2, 0).UInt32(3, board == 2 ? 1u : 0u).UInt32(4, 10).UInt32(5, 1000));
        }
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var m = new ProtoReader(data.AsMemory(16, len));
        ulong now = (ulong)DateTime.UtcNow.Ticks / 10;
        lock (_vars)
        {
            switch (cmd)
            {
                case RpcnCommand.TusSetMultiSlotVariable:
                {
                    string owner = m.Message(1).String(2);
                    var slots = m.Repeated(2);
                    var values = m.Repeated(3);
                    for (int i = 0; i < slots.Count; i++)
                    {
                        long old = _vars.TryGetValue((comm, owner, (int)slots[i]), out var v) ? v.Value : 0;
                        _vars[(comm, owner, (int)slots[i])] = (values[i], old, now, User);
                    }
                    return [0];
                }
                case RpcnCommand.TusGetMultiSlotVariable:
                {
                    string owner = m.Message(1).String(2);
                    var resp = new ProtoWriter();
                    foreach (var slot in m.Repeated(2))
                    {
                        var var = new ProtoWriter().String(1, owner);
                        if (_vars.TryGetValue((comm, owner, (int)slot), out var v))
                            var.Bool(2, true).UInt64(3, v.Date).String(4, v.Author).Int64(5, v.Value).Int64(6, v.Old);
                        resp.Message(1, var);
                    }
                    return Ok(resp);
                }
                case RpcnCommand.TusAddAndGetVariable:
                {
                    string owner = m.Message(1).String(2);
                    int slot = m.Int32(2);
                    long old = _vars.TryGetValue((comm, owner, slot), out var v) ? v.Value : 0;
                    _vars[(comm, owner, slot)] = (old + m.Int64(3), old, now, User);
                    return Ok(new ProtoWriter().String(1, owner).Bool(2, true).UInt64(3, now).String(4, User).Int64(5, old + m.Int64(3)).Int64(6, old));
                }
                case RpcnCommand.TusTryAndSetVariable:
                {
                    string owner = m.Message(1).String(2);
                    int slot = m.Int32(2), op = m.Int32(3);
                    long value = m.Int64(4);
                    bool has = _vars.TryGetValue((comm, owner, slot), out var v);
                    bool ok = !has || op switch { 3 => value > v.Value, 5 => value < v.Value, 1 => value == v.Value, _ => true };
                    if (!ok) return Err(RpcnError.CondFail);
                    _vars[(comm, owner, slot)] = (value, v.Value, now, User);
                    return Ok(new ProtoWriter().String(1, owner).Bool(2, true).UInt64(3, now).String(4, User).Int64(5, value).Int64(6, v.Value));
                }
                case RpcnCommand.TusSetData:
                {
                    string owner = m.Message(1).String(2);
                    _data[(comm, owner, m.Int32(2))] = (m.Bytes(3).ToArray(), m.Bytes(4).ToArray(), now, User);
                    return [0];
                }
                case RpcnCommand.TusGetData:
                {
                    string owner = m.Message(1).String(2);
                    var st = new ProtoWriter().String(1, owner);
                    var resp = new ProtoWriter();
                    if (_data.TryGetValue((comm, owner, m.Int32(2)), out var d))
                    {
                        st.Bool(2, true).UInt64(3, d.Date).String(4, d.Author).Bytes(5, d.Info);
                        resp.Message(1, st).Bytes(2, d.Data);
                    }
                    else resp.Message(1, st);
                    return Ok(resp);
                }
                case RpcnCommand.TusGetMultiSlotDataStatus:
                {
                    string owner = m.Message(1).String(2);
                    var resp = new ProtoWriter();
                    foreach (var slot in m.Repeated(2))
                    {
                        var st = new ProtoWriter().String(1, owner);
                        if (_data.TryGetValue((comm, owner, (int)slot), out var d)) st.Bool(2, true).UInt64(3, d.Date).String(4, d.Author).Bytes(5, d.Info);
                        resp.Message(1, st);
                    }
                    return Ok(resp);
                }
                case RpcnCommand.TusDeleteMultiSlotData:
                {
                    string owner = m.Message(1).String(2);
                    foreach (var slot in m.Repeated(2)) _data.Remove((comm, owner, (int)slot));
                    return [0];
                }
                case RpcnCommand.RecordScore:
                {
                    uint board = m.UInt32(1);
                    var list = Board(comm, board);
                    long score = m.Int64(3);
                    int pc = m.Int32(2);
                    int i = list.FindIndex(e => e.NpId == User && e.PcId == pc);
                    if (i >= 0 && list[i].Score >= score) return Err(RpcnError.ScoreNotBest);
                    if (i >= 0) list.RemoveAt(i);
                    list.Add((User, pc, score, m.String(4), m.Bytes(5).ToArray(), now, null));
                    list.Sort((a, b) => b.Score.CompareTo(a.Score));
                    uint rank = (uint)list.FindIndex(e => e.NpId == User && e.PcId == pc) + 1;
                    return [0, .. BitConverter.GetBytes(rank)];
                }
                case RpcnCommand.GetScoreRange:
                {
                    var list = Board(comm, m.UInt32(1));
                    int start = (int)m.UInt32(2), n = (int)m.UInt32(3);
                    return Ok(Scores(list.Select((e, i) => (e, i + 1)).Skip(start - 1).Take(n).ToList(), list.Count));
                }
                case RpcnCommand.GetScoreNpid:
                {
                    var list = Board(comm, m.UInt32(1));
                    var rows = m.Messages(2).Select(q =>
                    {
                        int i = list.FindIndex(e => e.NpId == q.String(1) && e.PcId == q.Int32(2));
                        return i < 0 ? ((string, int, long, string, byte[], ulong, byte[]?)?)null : list[i];
                    }).ToList();
                    var resp = new ProtoWriter();
                    foreach (var r in rows)
                    {
                        if (r is not { } e) { resp.Message(1, new ProtoWriter()); resp.String(2, ""); resp.Message(3, new ProtoWriter()); continue; }
                        int rank = list.IndexOf(e) + 1;
                        resp.Message(1, new ProtoWriter().String(1, e.Item1).String(2, e.Item1).Int32(3, e.Item2).UInt32(4, (uint)rank).Int64(5, e.Item3).Bool(6, e.Item7 != null).UInt64(7, e.Item6));
                        resp.String(2, e.Item4);
                        resp.Message(3, new ProtoWriter().Bytes(1, e.Item5));
                    }
                    resp.UInt64(4, now).UInt32(5, (uint)list.Count);
                    return Ok(resp);
                }
                case RpcnCommand.GetScoreData:
                {
                    var list = Board(comm, m.UInt32(1));
                    var e = list.FirstOrDefault(x => x.NpId == m.String(2) && x.PcId == m.Int32(3));
                    if (e.GameData == null) return Err(RpcnError.NotFound);
                    return [0, .. BitConverter.GetBytes((uint)e.GameData.Length), .. e.GameData];
                }
                case RpcnCommand.RecordScoreData:
                {
                    var list = Board(comm, m.UInt32(1));
                    int dlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16 + len));
                    var blob = data.AsSpan(20 + len, dlen).ToArray();
                    int i = list.FindIndex(x => x.NpId == User && x.PcId == m.Int32(2) && x.Score == m.Int64(3));
                    if (i < 0) return Err(RpcnError.ScoreInvalid);
                    if (list[i].GameData != null) return Err(RpcnError.ScoreHasData);
                    list[i] = list[i] with { GameData = blob };
                    return [0];
                }
                default:
                    return Err(RpcnError.Unsupported);
            }
        }
    }

    List<(string NpId, int PcId, long Score, string Comment, byte[] Info, ulong Date, byte[]? GameData)> Board(string comm, uint board)
    {
        if (!_boards.TryGetValue((comm, board), out var l)) _boards[(comm, board)] = l = new();
        return l;
    }

    static ProtoWriter Scores(List<((string NpId, int PcId, long Score, string Comment, byte[] Info, ulong Date, byte[]? GameData) E, int Rank)> rows, int total)
    {
        var resp = new ProtoWriter();
        foreach (var (e, rank) in rows)
            resp.Message(1, new ProtoWriter().String(1, e.NpId).String(2, e.NpId).Int32(3, e.PcId).UInt32(4, (uint)rank).Int64(5, e.Score).Bool(6, e.GameData != null).UInt64(7, e.Date));
        foreach (var (e, _) in rows) resp.String(2, e.Comment);
        foreach (var (e, _) in rows) resp.Message(3, new ProtoWriter().Bytes(1, e.Info));
        resp.UInt64(4, (ulong)DateTime.UtcNow.Ticks / 10).UInt32(5, (uint)total);
        return resp;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cert?.Dispose();
    }
}
