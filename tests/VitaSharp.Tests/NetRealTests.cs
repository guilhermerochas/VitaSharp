using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Hle.Np.Rpcn;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// Real internet (--net=real, docs/hle/E2-np.md): SceNet sockets/epoll/resolver, SceHttp and the RPCN client against
/// servers on 127.0.0.1 only. Every module runs with an allow list of localhost, so nothing can leave the machine.
/// </summary>
[Collection("guest")]
public sealed class NetRealTests(GuestMemoryFixture fx) : IDisposable
{
    static readonly NetConfig LocalOnly = new(NetMode.Real, ["localhost"]);
    readonly PsnAccount _savedAccount = PsnAccount.Current;

    public void Dispose() => PsnAccount.Current = _savedAccount;

    static object? Invoke(object module, string name, object[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);
    static int Call(object module, string name, params object[] args) => Convert.ToInt32(Invoke(module, name, args));

    uint Alloc(uint size)
    {
        uint p = fx.Memory.Alloc(size, "net test", MemoryRegion.User, 16);
        GuestMemory.Fill(p, 0, size);
        return p;
    }

    uint Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        uint p = Alloc((uint)b.Length + 1);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    uint Bytes(byte[] b)
    {
        uint p = Alloc((uint)Math.Max(b.Length, 1));
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    /// <summary>SceNetSockaddrIn for 127.0.0.1:port.</summary>
    uint Sa(int port, string ip = "127.0.0.1")
    {
        uint p = Alloc(16);
        GuestMemory.Write<byte>(p, 16);
        GuestMemory.Write<byte>(p + 1, 2);
        GuestMemory.Write(p + 2, (ushort)IPAddress.HostToNetworkOrder((short)port));
        IPAddress.Parse(ip).GetAddressBytes().CopyTo(GuestMemory.Span(p + 4, 4));
        return p;
    }

    static int SaPort(uint sa) => (ushort)IPAddress.NetworkToHostOrder((short)GuestMemory.Read<ushort>(sa + 2));

    uint Int(int v) { uint p = Alloc(4); GuestMemory.W32(p, (uint)v); return p; }

    /// <summary>An Emulator shell with only the guest memory (SceHttp allocates the library-owned header buffers there).</summary>
    Emulator Shell()
    {
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        typeof(Emulator).GetField("<Memory>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(emu, fx.Memory);
        return emu;
    }

    Net NewNet() => new() { ConfigOverride = LocalOnly };

    static void WaitUntilBlocked(Func<int> blocked, Task<int> waiter)
    {
        Assert.True(SpinWait.SpinUntil(() => blocked() > 0 || waiter.IsCompleted, TimeSpan.FromSeconds(30)), "the wait never blocked");
        Assert.False(waiter.IsCompleted, "the call returned instead of blocking");
    }

    // ---------------------------------------------------------------- policy

    [Fact]
    public void PolicyBlocksSonyAndUnlistedHosts()
    {
        Assert.False(NetConfig.Off.Permits("example.com", out _));
        var any = new NetConfig(NetMode.Real);
        Assert.True(any.Permits("example.com", out _));
        Assert.False(any.Permits("auth.np.ac.playstation.net", out var why));
        Assert.Contains("Sony", why);
        Assert.False(any.Permits("PLAYSTATION.COM.", out _));
        Assert.True(any.Permits("notplaystation.net", out _));
        Assert.True(LocalOnly.Permits("localhost", out _));
        Assert.True(LocalOnly.PermitsAddress(IPAddress.Loopback, out _));
        Assert.False(LocalOnly.Permits("example.com", out _));
        Assert.False(LocalOnly.PermitsAddress(IPAddress.Parse("192.0.2.1"), out _));
        // an address resolved from a blocked name stays blocked even when listed by address
        var byIp = new NetConfig(NetMode.Real, ["192.0.2.7"]);
        Assert.True(byIp.PermitsAddress(IPAddress.Parse("192.0.2.7"), out _));
        byIp.NoteResolved("x.playstation.net", [IPAddress.Parse("192.0.2.7")]);
        Assert.False(byIp.PermitsAddress(IPAddress.Parse("192.0.2.7"), out _));
        Assert.Equal(NetMode.Real, NetConfig.ParseMode("real"));
        Assert.Equal(NetMode.Off, NetConfig.ParseMode("OFF"));
        Assert.Null(NetConfig.ParseMode("maybe"));
    }

    [Fact]
    public void BlockedDestinationsNeverConnect()
    {
        var net = NewNet();
        int s = Call(net, "sceNetSocket", "t", 2, 1, 0);
        Assert.True(s > 0);
        Assert.Equal(unchecked((int)0x80410133), Call(net, "sceNetConnect", s, Sa(80, "192.0.2.1"), 16u)); // ENETUNREACH
        int u = Call(net, "sceNetSocket", "u", 2, 2, 0);
        uint buf = Bytes([1, 2, 3]);
        Assert.Equal(unchecked((int)0x80410133), Call(net, "sceNetSendto", u, buf, 3u, 0, Sa(9, "192.0.2.1"), 16u));
        int rid = Call(net, "sceNetResolverCreate", "r", 0u, 0);
        uint addr = Alloc(4);
        Assert.Equal(Net.RESOLVER_ENOHOST, Call(net, "sceNetResolverStartNtoa", rid, "ps4-system.sec.np.dl.playstation.net", addr, 0, 0, 0));
        Assert.Equal(Net.RESOLVER_ENOHOST, Call(net, "sceNetResolverStartNtoa", rid, "example.com", addr, 0, 0, 0)); // not in the allow list
        // offline (the default) nothing changes: no host socket, connect fails as unreachable
        var off = new Net { ConfigOverride = NetConfig.Off };
        int o = Call(off, "sceNetSocket", "o", 2, 1, 0);
        Assert.Equal(unchecked((int)0x80410133), Call(off, "sceNetConnect", o, Sa(80), 16u));
        Assert.Equal(Net.RESOLVER_ENODNS, Call(off, "sceNetResolverStartNtoa", Call(off, "sceNetResolverCreate", "r", 0u, 0), "localhost", addr, 0, 0, 0));
    }

    [Fact]
    public void ResolverUsesTheHost()
    {
        var net = NewNet();
        int rid = Call(net, "sceNetResolverCreate", "r", 0u, 0);
        uint addr = Alloc(4), err = Alloc(4);
        Assert.Equal(0, Call(net, "sceNetResolverStartNtoa", rid, "localhost", addr, 0, 0, 0));
        Assert.Equal(127, GuestMemory.Read<byte>(addr));
        Assert.Equal(0, Call(net, "sceNetResolverGetError", rid, new Ptr<int>(err)));
        Assert.Equal(0u, GuestMemory.R32(err));
        Assert.Equal(0, Call(net, "sceNetResolverDestroy", rid));
    }

    // ---------------------------------------------------------------- TCP / UDP

    [Fact]
    public async Task TcpBlockingConnectSendRecv()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        var net = NewNet();
        int s = Call(net, "sceNetSocket", "tcp", 2, 1, 0);
        var accept = server.AcceptTcpClientAsync();
        Assert.Equal(0, Call(net, "sceNetConnect", s, Sa(port), 16u));
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        var ps = peer.GetStream();

        uint msg = Bytes("ping"u8.ToArray());
        Assert.Equal(4, Call(net, "sceNetSend", s, msg, 4u, 0));
        var got = new byte[4];
        await ps.ReadExactlyAsync(got).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ping", Encoding.ASCII.GetString(got));

        // a blocking recv waits (cooperatively) until the peer sends
        uint buf = Alloc(64);
        var recv = Task.Run(() => Call(net, "sceNetRecv", s, buf, 64u, 0));
        WaitUntilBlocked(() => net.BlockedWaits, recv);
        await ps.WriteAsync("pong!"u8.ToArray());
        Assert.Equal(5, await recv.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("pong!", GuestMemory.ReadCString(buf));

        uint name = Alloc(16), len = Int(16);
        Assert.Equal(0, Call(net, "sceNetGetpeername", s, name, len));
        Assert.Equal(port, SaPort(name));
        Assert.Equal(0, Call(net, "sceNetGetsockname", s, name, len));
        Assert.Equal(((IPEndPoint)peer.Client.RemoteEndPoint!).Port, SaPort(name));
        Assert.Equal(unchecked((int)0x80410138), Call(net, "sceNetConnect", s, Sa(port), 16u)); // EISCONN

        // the peer closes: recv returns 0
        peer.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, Call(net, "sceNetRecv", s, buf, 64u, 0));
        Assert.Equal(0, Call(net, "sceNetSocketClose", s));
    }

    [Fact]
    public async Task TcpNonblockingConnectAndEpoll()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        var net = NewNet();
        int s = Call(net, "sceNetSocket", "nb", 2, 1, 0);
        Assert.Equal(0, Call(net, "sceNetSetsockopt", s, 0xFFFF, 0x1100, Int(1), 4u)); // SO_NBIO
        int r = Call(net, "sceNetConnect", s, Sa(port), 16u);
        Assert.True(r is 0 or unchecked((int)0x80410124), $"connect returned 0x{r:X8}"); // EINPROGRESS
        using var peer = await server.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));

        int eid = Call(net, "sceNetEpollCreate", "ep", 0);
        uint ev = Alloc(0x40);
        GuestMemory.W32(ev, Net.EPOLLOUT | Net.EPOLLIN);
        GuestMemory.Write(ev + 0x18, 0x1234_5678_9ABCUL);
        Assert.Equal(0, Call(net, "sceNetEpollControl", eid, 1, s, ev));
        uint outEv = Alloc(0x40);
        Assert.Equal(1, Call(net, "sceNetEpollWait", eid, outEv, 2, 5_000_000));
        Assert.Equal(Net.EPOLLOUT, GuestMemory.R32(outEv) & (Net.EPOLLOUT | Net.EPOLLIN));
        Assert.Equal(0x1234_5678_9ABCUL, GuestMemory.Read<ulong>(outEv + 0x18));
        uint soerr = Alloc(4), soerrLen = Int(4);
        Assert.Equal(0, Call(net, "sceNetGetsockopt", s, 0xFFFF, 0x1007, soerr, soerrLen)); // SO_ERROR
        Assert.Equal(0u, GuestMemory.R32(soerr));

        // only IN now: nothing to read yet -> EAGAIN, then the epoll wakes when data arrives
        GuestMemory.W32(ev, Net.EPOLLIN);
        Assert.Equal(0, Call(net, "sceNetEpollControl", eid, 2, s, ev));
        uint buf = Alloc(32);
        Assert.Equal(Net.EAGAIN, Call(net, "sceNetRecv", s, buf, 32u, 0));
        Assert.Equal(0, Call(net, "sceNetEpollWait", eid, outEv, 2, 0));
        var wait = Task.Run(() => Call(net, "sceNetEpollWait", eid, outEv, 2, -1));
        WaitUntilBlocked(() => net.BlockedWaits, wait);
        await peer.GetStream().WriteAsync("data"u8.ToArray());
        Assert.Equal(1, await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Net.EPOLLIN, GuestMemory.R32(outEv) & Net.EPOLLIN);
        Assert.Equal(4, Call(net, "sceNetRecv", s, buf, 32u, 0));

        // an epoll wait is still abortable
        var wait2 = Task.Run(() => Call(net, "sceNetEpollWait", eid, outEv, 2, -1));
        WaitUntilBlocked(() => net.BlockedWaits, wait2);
        Assert.Equal(0, Call(net, "sceNetEpollAbort", eid, 0));
        Assert.Equal(Net.EINTR, await wait2.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, Call(net, "sceNetSocketClose", s));
    }

    [Fact]
    public async Task BlockingRecvIsAbortable()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var net = NewNet();
        int s = Call(net, "sceNetSocket", "a", 2, 1, 0);
        var accept = server.AcceptTcpClientAsync();
        Assert.Equal(0, Call(net, "sceNetConnect", s, Sa(((IPEndPoint)server.LocalEndpoint).Port), 16u));
        using var peer = await accept;
        uint buf = Alloc(8);
        var recv = Task.Run(() => Call(net, "sceNetRecv", s, buf, 8u, 0));
        WaitUntilBlocked(() => net.BlockedWaits, recv);
        Assert.Equal(0, Call(net, "sceNetSocketAbort", s, 0));
        Assert.Equal(Net.EINTR, await recv.WaitAsync(TimeSpan.FromSeconds(10)));
        // SO_RCVTIMEO: EAGAIN after the timeout
        Assert.Equal(0, Call(net, "sceNetSetsockopt", s, 0xFFFF, 0x1006, Int(30_000), 4u));
        Assert.Equal(Net.EAGAIN, Call(net, "sceNetRecv", s, buf, 8u, 0));
        // close from another thread wakes a blocked recv with EBADF
        Assert.Equal(0, Call(net, "sceNetSetsockopt", s, 0xFFFF, 0x1006, Int(0), 4u));
        var recv2 = Task.Run(() => Call(net, "sceNetRecv", s, buf, 8u, 0));
        WaitUntilBlocked(() => net.BlockedWaits, recv2);
        Assert.Equal(0, Call(net, "sceNetSocketClose", s));
        Assert.Equal(Net.EBADF, await recv2.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task UdpSendtoRecvfrom()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int peerPort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;
        var net = NewNet();
        int s = Call(net, "sceNetSocket", "udp", 2, 2, 0);
        Assert.Equal(0, Call(net, "sceNetBind", s, Sa(0), 16u));
        uint name = Alloc(16), len = Int(16);
        Assert.Equal(0, Call(net, "sceNetGetsockname", s, name, len));
        int myPort = SaPort(name);
        Assert.True(myPort > 0);

        Assert.Equal(5, Call(net, "sceNetSendto", s, Bytes("hello"u8.ToArray()), 5u, 0, Sa(peerPort), 16u));
        var got = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("hello", Encoding.ASCII.GetString(got.Buffer));
        Assert.Equal(myPort, got.RemoteEndPoint.Port);

        uint buf = Alloc(64), from = Alloc(16), fromLen = Int(16);
        Assert.Equal(Net.EAGAIN, Call(net, "sceNetRecvfrom", s, buf, 64u, 0x80 /* DONTWAIT */, from, fromLen));
        await peer.SendAsync("reply"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, myPort));
        Assert.Equal(5, Call(net, "sceNetRecvfrom", s, buf, 64u, 0, from, fromLen));
        Assert.Equal("reply", GuestMemory.ReadCString(buf));
        Assert.Equal(peerPort, SaPort(from));
        Assert.Equal(16u, GuestMemory.R32(fromLen));
    }

    [Fact]
    public async Task GuestListensAndAccepts()
    {
        var net = NewNet();
        int l = Call(net, "sceNetSocket", "srv", 2, 1, 0);
        Assert.Equal(0, Call(net, "sceNetSetsockopt", l, 0xFFFF, 0x4, Int(1), 4u)); // SO_REUSEADDR
        Assert.Equal(0, Call(net, "sceNetBind", l, Sa(0), 16u));
        Assert.Equal(0, Call(net, "sceNetListen", l, 4));
        uint name = Alloc(16), len = Int(16);
        Assert.Equal(0, Call(net, "sceNetGetsockname", l, name, len));
        using var client = new TcpClient();
        var acc = Task.Run(() => Call(net, "sceNetAccept", l, Alloc(16), Int(16)));
        await client.ConnectAsync(IPAddress.Loopback, SaPort(name)).WaitAsync(TimeSpan.FromSeconds(10));
        int c = await acc.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(c > 0, $"accept returned 0x{c:X8}");
        // MSG_WAITALL: returns only when the whole buffer is filled, across several TCP segments
        uint buf = Alloc(8);
        var recv = Task.Run(() => Call(net, "sceNetRecv", c, buf, 6u, 0x40 /* WAITALL */));
        await client.GetStream().WriteAsync("abc"u8.ToArray());
        WaitUntilBlocked(() => net.BlockedWaits, recv);
        await client.GetStream().WriteAsync("def"u8.ToArray());
        Assert.Equal(6, await recv.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("abcdef", GuestMemory.ReadCString(buf));
        Assert.Equal(0, Call(net, "sceNetSocketClose", c));
        Assert.Equal(0, Call(net, "sceNetSocketClose", l));
    }

    [Fact]
    public void NetCtlReportsTheHostAddress()
    {
        var net = NewNet();
        uint info = Alloc(256);
        Assert.Equal(0, Call(net, "sceNetCtlInetGetInfo", 15, info));
        Assert.True(IPAddress.TryParse(GuestMemory.ReadCString(info), out _));
        Assert.Equal(0, Call(net, "sceNetCtlInetGetInfo", 4, info));
        Assert.True(GuestMemory.R32(info) is > 500 and < 65536);
    }

    // ---------------------------------------------------------------- SceHttp

    const int HTTP_EAGAIN = unchecked((int)0x80431082), HTTP_NETWORK = unchecked((int)0x80431063),
        HTTP_NO_CONTENT_LENGTH = unchecked((int)0x80431071);

    (Http Http, int Tmpl) NewHttp(string ua = "VitaSharpTest/1.0")
    {
        var http = new Http { Emu = Shell(), ConfigOverride = LocalOnly };
        Assert.Equal(0, Call(http, "sceHttpInit", 0x40000u));
        int t = Call(http, "sceHttpCreateTemplate", Str(ua), 2, 0);
        Assert.True(t > 0);
        return (http, t);
    }

    int Request(Http http, int tmpl, string url, int method = 0, ulong len = 0)
    {
        int c = Call(http, "sceHttpCreateConnectionWithURL", tmpl, Str(url), 1);
        Assert.True(c > 0, $"connection 0x{c:X8}");
        int r = Call(http, "sceHttpCreateRequestWithURL", c, method, Str(url), len);
        Assert.True(r > 0, $"request 0x{r:X8}");
        return r;
    }

    string ReadAll(Http http, int req)
    {
        var sb = new StringBuilder();
        uint buf = Alloc(10);
        for (int i = 0; i < 10_000; i++)
        {
            int n = Call(http, "sceHttpReadData", req, buf, 10u);
            Assert.True(n >= 0, $"read 0x{n:X8}");
            if (n == 0) return sb.ToString();
            sb.Append(Encoding.Latin1.GetString(GuestMemory.Span(buf, n)));
        }
        throw new InvalidOperationException("body never ended");
    }

    [Fact]
    public void HttpGetBlocking()
    {
        using var srv = new LocalHttpServer();
        var (http, t) = NewHttp();
        int r = Request(http, t, srv.Base + "/hello");
        uint v = Alloc(16);
        Assert.Equal(0, Call(http, "sceHttpSendRequest", r, 0u, 0u));
        Assert.Equal(0, Call(http, "sceHttpGetStatusCode", r, v));
        Assert.Equal(200u, GuestMemory.R32(v));
        Assert.Equal(0, Call(http, "sceHttpGetResponseContentLength", r, v));
        Assert.Equal(10UL, GuestMemory.Read<ulong>(v));
        uint hdr = Alloc(4), hdrSize = Alloc(4);
        Assert.Equal(0, Call(http, "sceHttpGetAllResponseHeaders", r, hdr, hdrSize));
        string headers = Encoding.Latin1.GetString(GuestMemory.Span(GuestMemory.R32(hdr), (int)GuestMemory.R32(hdrSize)));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", headers);
        Assert.Contains("X-Server: local\r\n", headers);
        Assert.Equal("hello vita", ReadAll(http, r));
        Assert.Equal(0, Call(http, "sceHttpDeleteRequest", r));
        Assert.Equal(0, Call(http, "sceHttpTerm"));
    }

    [Fact]
    public void HttpChunkedRedirectPostAndCookies()
    {
        using var srv = new LocalHttpServer();
        var (http, t) = NewHttp();
        uint v = Alloc(16);

        int chunked = Request(http, t, srv.Base + "/chunked");
        Assert.Equal(0, Call(http, "sceHttpSendRequest", chunked, 0u, 0u));
        Assert.Equal(HTTP_NO_CONTENT_LENGTH, Call(http, "sceHttpGetResponseContentLength", chunked, v));
        Assert.Equal(string.Concat(Enumerable.Range(0, 20).Select(i => $"line {i}\n")), ReadAll(http, chunked));

        int redirect = Request(http, t, srv.Base + "/redirect");
        Assert.Equal(0, Call(http, "sceHttpSendRequest", redirect, 0u, 0u));
        Assert.Equal(0, Call(http, "sceHttpGetStatusCode", redirect, v));
        Assert.Equal(200u, GuestMemory.R32(v));
        Assert.Equal("hello vita", ReadAll(http, redirect));

        int noRedirect = Request(http, t, srv.Base + "/redirect");
        Assert.Equal(0, Call(http, "sceHttpSetAutoRedirect", noRedirect, 0));
        Assert.Equal(0, Call(http, "sceHttpSendRequest", noRedirect, 0u, 0u));
        Assert.Equal(0, Call(http, "sceHttpGetStatusCode", noRedirect, v));
        Assert.Equal(302u, GuestMemory.R32(v));

        int cookie = Request(http, t, srv.Base + "/cookie");
        Assert.Equal(0, Call(http, "sceHttpSendRequest", cookie, 0u, 0u));
        ReadAll(http, cookie);

        byte[] body = "payload-123"u8.ToArray();
        int post = Request(http, t, srv.Base + "/echo", 1 /* POST */, (ulong)body.Length);
        Assert.Equal(0, Call(http, "sceHttpAddRequestHeader", post, Str("X-Test"), Str("yes"), 0u));
        Assert.Equal(0, Call(http, "sceHttpSendRequest", post, Bytes(body), (uint)body.Length));
        Assert.Equal(0, Call(http, "sceHttpGetStatusCode", post, v));
        Assert.Equal(201u, GuestMemory.R32(v));
        Assert.Equal("POST x-test=yes cookie=session=abc123 ua=VitaSharpTest/1.0 body=payload-123", ReadAll(http, post));

        int gz = Request(http, t, srv.Base + "/gzip");
        Assert.Equal(0, Call(http, "sceHttpSendRequest", gz, 0u, 0u));
        Assert.Equal("compressed body", ReadAll(http, gz)); // SceHttp inflates gzip by default
    }

    [Fact]
    public void HttpNonblockingWithEpoll()
    {
        using var srv = new LocalHttpServer();
        var (http, t) = NewHttp();
        int r = Request(http, t, srv.Base + "/slow");
        Assert.Equal(0, Call(http, "sceHttpSetNonblock", r, 1));
        uint eh = Alloc(4), nbev = Alloc(0x40), v = Alloc(16);
        Assert.Equal(0, Call(http, "sceHttpCreateEpoll", eh));
        Assert.Equal(0, Call(http, "sceHttpSetEpoll", r, GuestMemory.R32(eh), 0xABCDu));
        Assert.Equal(HTTP_EAGAIN, Call(http, "sceHttpSendRequest", r, 0u, 0u));
        // the headers arrive: one IN event with the request id and user argument
        Assert.Equal(1, Call(http, "sceHttpWaitRequest", GuestMemory.R32(eh), nbev, 4, 10_000_000));
        Assert.Equal(1u, GuestMemory.R32(nbev));
        Assert.Equal((uint)r, GuestMemory.R32(nbev + 8));
        Assert.Equal(0xABCDu, GuestMemory.R32(nbev + 12));
        Assert.Equal(0, Call(http, "sceHttpSendRequest", r, 0u, 0u));
        Assert.Equal(0, Call(http, "sceHttpGetStatusCode", r, v));
        Assert.Equal(200u, GuestMemory.R32(v));
        // the body is held back by the server: non-blocking reads say EAGAIN
        uint buf = Alloc(16);
        Assert.Equal(HTTP_EAGAIN, Call(http, "sceHttpReadData", r, buf, 16u));
        srv.ReleaseSlow.Set();
        Assert.Equal(1, Call(http, "sceHttpWaitRequest", GuestMemory.R32(eh), nbev, 4, 10_000_000));
        int got = HTTP_EAGAIN;
        Assert.True(SpinWait.SpinUntil(() => (got = Call(http, "sceHttpReadData", r, buf, 16u)) != HTTP_EAGAIN, TimeSpan.FromSeconds(10)));
        Assert.Equal(4, got);
        Assert.Equal("done", GuestMemory.ReadCString(buf));
    }

    [Fact]
    public void HttpToBlockedHostsFailsLikeOffline()
    {
        using var srv = new LocalHttpServer();
        var (http, t) = NewHttp();
        int r = Request(http, t, "http://ps4-system.sec.np.dl.playstation.net/hello");
        Assert.Equal(HTTP_NETWORK, Call(http, "sceHttpSendRequest", r, 0u, 0u));
        uint errno = Alloc(4);
        Assert.Equal(0, Call(http, "sceHttpGetLastErrno", r, errno));
        Assert.NotEqual(0u, GuestMemory.R32(errno));
        int r2 = Request(http, t, "http://example.com/");
        Assert.Equal(HTTP_NETWORK, Call(http, "sceHttpSendRequest", r2, 0u, 0u)); // not in the allow list
        Assert.Equal(0, srv.Requests);
        // a closed port on localhost: a real connection failure
        int dead;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); dead = ((IPEndPoint)probe.LocalEndpoint).Port; }
        int r3 = Request(http, t, $"http://127.0.0.1:{dead}/x");
        Assert.Equal(HTTP_NETWORK, Call(http, "sceHttpSendRequest", r3, 0u, 0u));
        Assert.Equal(0, Call(http, "sceHttpGetLastErrno", r3, errno));
        Assert.Equal(61u, GuestMemory.R32(errno)); // ECONNREFUSED
    }

    // ---------------------------------------------------------------- RPCN

    static RpcnClient Client(MockRpcnServer srv, string password = "secret", bool tls = true) =>
        new(new RpcnConfig("localhost", srv.Port, "Tester", password), LocalOnly) { UseTls = tls, Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public void RpcnLoginAndTus()
    {
        using var srv = new MockRpcnServer(tls: true);
        using var client = Client(srv);
        var tus = new RpcnTusService(client);
        const string comm = "NPWR00001_00";
        Assert.Null(tus.GetVariable(comm, "Tester", 1));
        Assert.True(client.Connected);
        Assert.Equal("Tester Online", client.OnlineName);
        Assert.Equal([new RpcnFriend("Buddy", true)], client.Friends);
        tus.SetVariables(comm, "Tester", [(1, 100), (2, -5)], "Tester");
        Assert.Equal(100, tus.GetVariable(comm, "Tester", 1)!.Value);
        Assert.Equal(-5, tus.GetVariable(comm, "Tester", 2)!.Value);
        var added = tus.AddAndGet(comm, "Tester", 1, 7, "Tester");
        Assert.Equal((107L, 100L), (added.Value, added.OldValue));
        var (v, written) = tus.TryAndSet(comm, "Tester", 1, TusOpeType.GreaterThan, 50, null, "Tester");
        Assert.False(written);
        Assert.Equal(107, v.Value);
        (v, written) = tus.TryAndSet(comm, "Tester", 1, TusOpeType.GreaterThan, 500, null, "Tester");
        Assert.True(written);
        Assert.Equal(500, v.Value);

        Assert.Null(tus.GetData(comm, "Tester", 3));
        byte[] save = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        tus.SetData(comm, "Tester", 3, save, [9, 8, 7], "Tester");
        var d = tus.GetData(comm, "Tester", 3)!.Value;
        Assert.Equal(save, d.Data);
        Assert.Equal([9, 8, 7], d.Status.Info);
        Assert.Equal(3000, d.Status.Size);
        Assert.NotNull(tus.GetDataStatus(comm, "Tester", 3));
        tus.DeleteData(comm, "Tester", [3]);
        Assert.Null(tus.GetData(comm, "Tester", 3));
        Assert.Equal(1, srv.Logins); // one connection for every request
    }

    [Fact]
    public void RpcnScore()
    {
        using var srv = new MockRpcnServer(tls: false);
        using var client = Client(srv, tls: false);
        var score = new RpcnScoreService(client);
        const string comm = "NPWR00002_00";
        Assert.Equal(1u, score.GetBoard(comm, 2).SortMode);
        var e = Assert.Throws<NpServerException>(() => score.GetBoard(comm, 99));
        Assert.Equal(unchecked((int)0x80550817), e.Code); // RANKING_BOARD_MASTER_NOT_FOUND
        Assert.True(score.Record(comm, 1, "Tester", 0, 5000, "great run", [1, 2], out uint rank));
        Assert.Equal(1u, rank);
        Assert.False(score.Record(comm, 1, "Tester", 0, 10, "", [], out _)); // NOT_BEST_SCORE
        var (rows, total, _) = score.GetRange(comm, 1, 1, 10);
        Assert.Equal(1u, total);
        var row = Assert.Single(rows);
        Assert.Equal(("Tester", 5000L, "great run", 1u, 1u), (row.Entry.OnlineId, row.Entry.Score, row.Entry.Comment, row.Rank, row.SerialRank));
        Assert.Equal([1, 2], row.Entry.GameInfo);
        var (byId, _, _) = score.GetByIds(comm, 1, [("Tester", 0), ("Nobody", 0)]);
        Assert.Equal(2, byId.Count);
        Assert.Equal(5000, byId[0]!.Value.Entry.Score);
        Assert.Null(byId[1]);
        Assert.Null(score.GetGameData(comm, 1, "Tester", 0));
        Assert.True(score.RecordGameData(comm, 1, "Tester", 0, 5000, [7, 7, 7]));
        Assert.Equal([7, 7, 7], score.GetGameData(comm, 1, "Tester", 0));
        Assert.Equal(unchecked((int)0x8055082C), Assert.Throws<NpServerException>(() => score.RecordGameData(comm, 1, "Tester", 0, 5000, [1])).Code);
        Assert.False(score.RecordGameData(comm, 1, "Tester", 0, 1234, [1])); // no such score
    }

    [Fact]
    public void RpcnFailuresAreUnreachableServers()
    {
        using var srv = new MockRpcnServer(tls: false);
        using var bad = Client(srv, password: "wrong", tls: false);
        Assert.Throws<IOException>(() => new RpcnTusService(bad).GetVariable("NPWR00001_00", "Tester", 0));
        Assert.False(bad.Connected);
        using var old = new MockRpcnServer(tls: false) { Version = 20 };
        using var c2 = Client(old, tls: false);
        Assert.Contains("protocol", Assert.Throws<IOException>(() => new RpcnScoreService(c2).GetBoard("NPWR00001_00", 1)).Message);
        // a server that is not allowed is never contacted
        using var notAllowed = new RpcnClient(new RpcnConfig("np.rpcs3.net", 31313, "Tester", "secret"), LocalOnly);
        Assert.Throws<IOException>(() => new RpcnScoreService(notAllowed).GetBoard("NPWR00001_00", 1));
        Assert.Throws<IOException>(() => new RpcnClient(new RpcnConfig("localhost", srv.Port, "Tester", "secret"), NetConfig.Off) { UseTls = false }
            .Request(RpcnCommand.GetNetworkTime, []));
        Assert.Equal(("np.rpcs3.net", 31313), RpcnConfig.ParseServer("np.rpcs3.net"));
        Assert.Equal(("h", 1234), RpcnConfig.ParseServer(" h:1234 "));
        Assert.Null(RpcnConfig.ParseServer(""));
    }

    [Fact]
    public void TusThroughTheLibraryOnRpcn()
    {
        using var srv = new MockRpcnServer(tls: false);
        using var client = Client(srv, tls: false);
        PsnAccount.Current = PsnAccount.Create(true, "Tester", localServices: false) with { Server = $"localhost:{srv.Port}" };
        var m = new NpTus { StoreOverride = new RpcnTusService(client) };
        Assert.Equal(0, Call(m, "sceNpTusInit", 0, 0, 0u));
        uint comm = Alloc(12);
        GuestMemory.WriteCString(comm, "NPWR00003");
        int ctx = Call(m, "sceNpTusCreateTitleCtx", comm, 0u, 0u);
        int Req() => Call(m, "sceNpTusCreateRequest", ctx);
        uint me = Alloc(0x24);
        NpManager.WriteNpId(me, "Tester");
        uint slots = Alloc(8), values = Alloc(16);
        GuestMemory.W32(slots, 1); GuestMemory.W32(slots + 4, 2);
        GuestMemory.Write(values, 41L); GuestMemory.Write(values + 8, 42L);
        Assert.Equal(0, Call(m, "sceNpTusSetMultiSlotVariable", Req(), me, slots, values, 2, 0u));
        uint vars = Alloc(2 * NpTus.VariableSize), result = Alloc(4);
        int get = Req();
        Assert.Equal(0, Call(m, "sceNpTusGetMultiSlotVariableAsync", get, me, slots, vars, 2 * NpTus.VariableSize, 2, 0u));
        Assert.Equal(0, Call(m, "sceNpTusPollAsync", get, result));
        Assert.Equal(2u, GuestMemory.R32(result));
        Assert.Equal(1u, GuestMemory.R32(vars + 36));
        Assert.Equal(41, GuestMemory.Read<long>(vars + 88));
        Assert.Equal(42, GuestMemory.Read<long>(vars + NpTus.VariableSize + 88));
        uint outVar = Alloc(NpTus.VariableSize);
        Assert.Equal(NpCommunity.SERVER_CONDITIONS_NOT_SATISFIED,
            Call(m, "sceNpTusTryAndSetVariable", Req(), me, 1, 3 /* GREATER_THAN */, 10L, outVar, NpTus.VariableSize, 0u));
        Assert.Equal(41, GuestMemory.Read<long>(outVar + 88));
        // the server goes away: the next transaction fails like an unreachable server, it does not hang
        srv.Dispose();
        client.Dispose();
        Assert.Equal(PsnAccount.ServerUnreachable, Call(m, "sceNpTusGetMultiSlotVariable", Req(), me, slots, vars, 2 * NpTus.VariableSize, 2, 0u));
    }
}
