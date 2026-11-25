using System.Diagnostics;
using System.Reflection;
using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>Network HLE (docs/hle/E1-net.md): URI/HTTP utilities, HTTP object model, epoll/rudp waits, ad hoc matching.</summary>
[Collection("guest")]
public sealed class NetHleTests(GuestMemoryFixture fx)
{
    private static object Invoke<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;
    private static int Call<T>(T module, string name, params object[] args) => (int)Invoke(module, name, args);

    private uint Str(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        uint p = fx.Memory.Alloc((uint)b.Length + 16, "str");
        b.CopyTo(GuestMemory.Span(p, b.Length));
        GuestMemory.Write<byte>(p + (uint)b.Length, 0);
        return p;
    }

    private static string Latin1Text(byte[] b) => Encoding.Latin1.GetString(b);

    /// <summary>
    /// Waits until <paramref name="blocked"/> counts the waiter as blocked: a non-preserving abort or cancel only
    /// interrupts waits already in progress, so aborting after a fixed sleep races with a slow (loaded) host.
    /// </summary>
    private static void WaitUntilBlocked(Func<int> blocked, Task<int> waiter)
    {
        Assert.True(SpinWait.SpinUntil(() => blocked() > 0 || waiter.IsCompleted, TimeSpan.FromSeconds(30)), "the wait never blocked");
        Assert.False(waiter.IsCompleted, $"the wait returned {(waiter.IsCompletedSuccessfully ? $"0x{waiter.Result:X8}" : "with an exception")} instead of blocking");
    }

    [Fact]
    public void EscapeUnescape()
    {
        byte[] input = Encoding.UTF8.GetBytes("a b&c=d/é");
        Assert.Equal("a%20b%26c%3Dd%2F%C3%A9", Latin1Text(HttpUri.Escape(input)));
        Assert.Equal("AZaz09-._~", Latin1Text(HttpUri.Escape("AZaz09-._~"u8)));
        Assert.Equal("JJ%zz%", Latin1Text(HttpUri.Unescape("%4a%4A%zz%"u8)));
        byte[] all = Enumerable.Range(1, 255).Select(i => (byte)i).ToArray();
        Assert.Equal(all, HttpUri.Unescape(HttpUri.Escape(all)));
    }

    [Fact]
    public void EscapeThroughHle()
    {
        var http = new Http();
        uint input = Str("a b&c=d/é"), req = fx.Memory.Alloc(16, "req"), outp = fx.Memory.Alloc(64, "out");
        Assert.Equal(0, Call(http, "sceHttpUriEscape", 0u, req, 0u, input));
        Assert.Equal(23u, GuestMemory.R32(req));
        Assert.Equal(unchecked((int)0x80431022), Call(http, "sceHttpUriEscape", outp, req, 10u, input));
        Assert.Equal(0, Call(http, "sceHttpUriEscape", outp, req, 64u, input));
        Assert.Equal("a%20b%26c%3Dd%2F%C3%A9", GuestMemory.ReadCString(outp));
        Assert.Equal(unchecked((int)0x804311FE), Call(http, "sceHttpUriEscape", outp, req, 64u, 0u));
    }

    [Fact]
    public void ParseAndBuild()
    {
        const string Url = "http://user:pw@host:8080/a/b?x=1#f";
        Assert.Equal(0, HttpUri.ParseStrict(Url, out HttpUri.Parts? p));
        var e = HttpUri.ToElement(p);
        Assert.Equal(("http", "user", "pw", "host", "/a/b", "?x=1", "#f", (ushort)8080), (e.Scheme, e.User, e.Password, e.Host, e.Path, e.Query, e.Fragment, e.Port));
        Assert.Equal(Url, HttpUri.Build(e, HttpUri.BuildAll));
        Assert.Equal("host/a/b", HttpUri.Build(e, HttpUri.BuildHostname | HttpUri.BuildPath));
        Assert.Equal(HttpUri.ERR_UNKNOWN_SCHEME, HttpUri.ParseStrict("ftp://x/", out _));
        Assert.Equal(HttpUri.ERR_INVALID_URL, HttpUri.ParseStrict("nothing", out _));

        var http = new Http();
        uint src = Str(Url), elem = fx.Memory.Alloc(0x40, "elem"), pool = fx.Memory.Alloc(64, "pool"), req = fx.Memory.Alloc(16, "req");
        Assert.Equal(0, Call(http, "sceHttpUriParse", 0u, src, 0u, req, 0u));
        Assert.Equal(31u, GuestMemory.R32(req));
        Assert.Equal(0, Call(http, "sceHttpUriParse", elem, src, pool, req, 64u));
        Assert.Equal("host", GuestMemory.ReadCString(GuestMemory.R32(elem + 0x10)));
        Assert.Equal("?x=1", GuestMemory.ReadCString(GuestMemory.R32(elem + 0x18)));
        Assert.Equal((ushort)8080, GuestMemory.Read<ushort>(elem + 0x20));
        uint outp = fx.Memory.Alloc(64, "out");
        Assert.Equal(0, Call(http, "sceHttpUriBuild", outp, req, 64u, elem, HttpUri.BuildAll));
        Assert.Equal(Url, GuestMemory.ReadCString(outp));
    }

    [Theory]
    [InlineData("g", "http://a/b/c/g")]
    [InlineData("./g", "http://a/b/c/g")]
    [InlineData("g/", "http://a/b/c/g/")]
    [InlineData("/g", "http://a/g")]
    [InlineData("//g", "http://g")]
    [InlineData("?y", "http://a/b/c/d;p?y")]
    [InlineData("g?y", "http://a/b/c/g?y")]
    [InlineData("#s", "http://a/b/c/d;p?q#s")]
    [InlineData("", "http://a/b/c/d;p?q")]
    [InlineData(".", "http://a/b/c/")]
    [InlineData("..", "http://a/b/")]
    [InlineData("../..", "http://a/")]
    [InlineData("../../../g", "http://a/g")]
    [InlineData("g;x=1/../y", "http://a/b/c/y")]
    public void MergeRfc3986(string rel, string expected)
    {
        Assert.Equal(0, HttpUri.Merge("http://a/b/c/d;p?q", rel, out string? r));
        Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData("/a/b/c/./../../g", "/a/g")]
    [InlineData("/a/../../b", "/b")]
    [InlineData("mid/content=5/../6", "mid/content=5/../6")]
    [InlineData("http://h/a/./b/../c?x=/../", "http://h/a/c?x=/../")]
    public void SweepPath(string input, string expected) => Assert.Equal(expected, HttpUri.SweepPath(input));

    [Fact]
    public void StatusLineAndHeaders()
    {
        Assert.Equal(0, HttpParse.StatusLine("HTTP/1.1 404 Not Found\r\n"u8, out int maj, out int min, out int code, out int off, out int len));
        Assert.Equal((1, 1, 404, 13, 9), (maj, min, code, off, len));
        Assert.Equal(0, HttpParse.StatusLine("HTTP/1.0 200\r\n"u8, out _, out _, out code, out _, out len));
        Assert.Equal((200, 0), (code, len));
        Assert.Equal(HttpParse.ERR_INVALID_RESPONSE, HttpParse.StatusLine("HTTX/1.1 200 OK"u8, out _, out _, out _, out _, out _));

        ReadOnlySpan<byte> hdr = "Content-Type: text/html\r\nX-A:  1 \r\n"u8;
        Assert.True(HttpParse.FindHeader(hdr, "x-a"u8, out off, out len));
        Assert.Equal("1", Encoding.ASCII.GetString(hdr.Slice(off, len)));
        Assert.False(HttpParse.FindHeader(hdr, "nope"u8, out _, out _));
    }

    [Fact]
    public void HttpObjectModel()
    {
        const int BEFORE_INIT = unchecked((int)0x80431001), ALREADY = unchecked((int)0x80431020), INVALID_ID = unchecked((int)0x80431100),
            NETWORK = unchecked((int)0x80431063), BEFORE_SEND = unchecked((int)0x80431065), ABORTED = unchecked((int)0x80431080);
        var http = new Http();
        uint ua = Str("agent"), host = Str("example.com"), scheme = Str("http"), path = Str("/x"), outv = fx.Memory.Alloc(16, "v");
        Assert.Equal(BEFORE_INIT, Call(http, "sceHttpCreateTemplate", ua, 2, 0));
        Assert.Equal(0, Call(http, "sceHttpInit", 0x10000u));
        Assert.Equal(ALREADY, Call(http, "sceHttpInit", 0x10000u));
        int t = Call(http, "sceHttpCreateTemplate", ua, 2, 0);
        int c = Call(http, "sceHttpCreateConnection", t, host, scheme, (ushort)80, 1);
        int r = Call(http, "sceHttpCreateRequest", c, 0, path, 0UL);
        Assert.True(t > 0 && c > 0 && r > 0);
        Assert.Equal(INVALID_ID, Call(http, "sceHttpCreateRequest", t, 0, path, 0UL)); // template id is not a connection
        Assert.Equal(BEFORE_SEND, Call(http, "sceHttpGetStatusCode", r, outv));
        Assert.Equal(0, Call(http, "sceHttpSetNonblock", r, 1));
        Assert.Equal(0, Call(http, "sceHttpGetNonblock", r, outv));
        Assert.Equal(1u, GuestMemory.R32(outv));
        Assert.Equal(INVALID_ID, Call(http, "sceHttpGetNonblock", 999, outv));
        Assert.Equal(NETWORK, Call(http, "sceHttpSendRequest", r, 0u, 0u));
        Assert.Equal(NETWORK, Call(http, "sceHttpGetStatusCode", r, outv));
        Assert.Equal(0, Call(http, "sceHttpGetLastErrno", r, outv));
        Assert.NotEqual(0u, GuestMemory.R32(outv));
        Assert.Equal(0, Call(http, "sceHttpAbortRequest", r));
        Assert.Equal(ABORTED, Call(http, "sceHttpSendRequest", r, 0u, 0u));
        Assert.Equal(0, Call(http, "sceHttpDeleteRequest", r));
        Assert.Equal(INVALID_ID, Call(http, "sceHttpDeleteRequest", r));
        Assert.Equal(0, Call(http, "sceHttpDeleteConnection", c));
        Assert.Equal(0, Call(http, "sceHttpDeleteTemplate", t));
        Assert.Equal(0, Call(http, "sceHttpTerm"));
        Assert.Equal(BEFORE_INIT, Call(http, "sceHttpTerm"));
    }

    [Fact]
    public void Cookies()
    {
        var jar = new CookieJar();
        DateTime now = DateTime.UtcNow;
        Assert.True(jar.Add("http://h/x/y", "a=1; Path=/x", now));
        Assert.Equal("a=1", jar.Get("http://h/x/z", false, now));
        Assert.Null(jar.Get("http://h/w", false, now));
        byte[] data = jar.Export();
        jar.Flush();
        Assert.Null(jar.Get("http://h/x/z", false, now));
        Assert.True(jar.Import(data));
        Assert.Equal("a=1", jar.Get("http://h/x/z", false, now));
        Assert.False(jar.Import(new byte[32])); // a real Vita export is ignored
    }

    [Fact]
    public async Task EpollWaitTimesOutAndAborts()
    {
        var net = new Net();
        int eid = Call(net, "sceNetEpollCreate", "e", 0);
        int s = Call(net, "sceNetSocket", "s", 2, 2, 0);
        uint ev = fx.Memory.Alloc(0x40, "ev");
        GuestMemory.W32(ev, 1);
        Assert.Equal(0, Call(net, "sceNetEpollControl", eid, 1, s, ev));
        Assert.Equal(Net.EEXIST, Call(net, "sceNetEpollControl", eid, 1, s, ev));
        var sw = Stopwatch.StartNew();
        Assert.Equal(0, Call(net, "sceNetEpollWait", eid, ev, 1, 20_000));
        Assert.True(sw.ElapsedMilliseconds >= 10, $"waited {sw.ElapsedMilliseconds} ms");

        Task<int> waiter = Task.Run(() => Call(net, "sceNetEpollWait", eid, ev, 1, -1));
        WaitUntilBlocked(() => net.BlockedWaits, waiter); // a non-preserving abort only interrupts waits in progress
        Assert.Equal(0, Call(net, "sceNetEpollAbort", eid, 0));
        await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Net.EINTR, await waiter);
        // vita-headers SCE_NET_EPOLL_ABORT_FLAG_PRESERVATION: without it an abort with no wait in progress is lost,
        // with it every later wait fails until the epoll is destroyed
        Assert.Equal(0, Call(net, "sceNetEpollAbort", eid, 0));
        Assert.Equal(0, Call(net, "sceNetEpollWait", eid, ev, 1, 1_000));
        Assert.Equal(0, Call(net, "sceNetEpollAbort", eid, 1));
        Assert.Equal(Net.EINTR, Call(net, "sceNetEpollWait", eid, ev, 1, -1));
        Assert.Equal(0, Call(net, "sceNetEpollDestroy", eid));
        Assert.Equal(Net.EBADF, Call(net, "sceNetEpollDestroy", eid));
    }

    [Fact]
    public async Task BlockingRecvWaitsForAbort()
    {
        var net = new Net();
        int s = Call(net, "sceNetSocket", "u", 2, 2, 0);
        uint opt = fx.Memory.Alloc(16, "opt");
        GuestMemory.W32(opt, 1);
        Assert.Equal(0, Call(net, "sceNetSetsockopt", s, 0xFFFF, 0x1100, opt, 4u)); // SO_NBIO
        Assert.Equal(Net.EAGAIN, Call(net, "sceNetRecv", s, 0u, 0u, 0));
        GuestMemory.W32(opt, 0);
        Assert.Equal(0, Call(net, "sceNetSetsockopt", s, 0xFFFF, 0x1100, opt, 4u));
        Task<int> waiter = Task.Run(() => Call(net, "sceNetRecv", s, 0u, 0u, 0));
        WaitUntilBlocked(() => net.BlockedWaits, waiter);
        Assert.Equal(0, Call(net, "sceNetSocketAbort", s, 0));
        await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Net.EINTR, await waiter);
        Assert.Equal(0, Call(net, "sceNetSocketClose", s));
        Assert.Equal(Net.EBADF, Call(net, "sceNetSocketClose", s));
    }

    [Fact]
    public async Task RudpPollWaitBlocksUntilCancel()
    {
        var rudp = new Rudp();
        Assert.Equal(0, Call(rudp, "sceRudpInit", 0u, 0u));
        int poll = Call(rudp, "sceRudpPollCreate", 8u);
        uint evs = fx.Memory.Alloc(64, "rudpev");
        var sw = Stopwatch.StartNew();
        Assert.Equal(0, Call(rudp, "sceRudpPollWait", poll, evs, 4u, 20_000UL));
        Assert.True(sw.ElapsedMilliseconds >= 10);
        Task<int> waiter = Task.Run(() => Call(rudp, "sceRudpPollWait", poll, evs, 4u, ulong.MaxValue));
        WaitUntilBlocked(() => rudp.BlockedPollWaits, waiter);
        Assert.Equal(0, Call(rudp, "sceRudpPollCancel", poll));
        await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, Call(rudp, "sceRudpSetMaxSegmentSize", (ushort)1000));
        uint mss = fx.Memory.Alloc(16, "mss");
        Call(rudp, "sceRudpGetMaxSegmentSize", new Ptr<ushort>(mss));
        Assert.Equal((ushort)1000, GuestMemory.Read<ushort>(mss));
    }

    [Fact]
    public void AdhocMatchingOffline()
    {
        var m = new NetAdhocMatching();
        uint pool = fx.Memory.Alloc(0x1000, "pool"), addr = fx.Memory.Alloc(16, "addr");
        Assert.Equal(NetAdhocMatching.ERR_NOT_INITIALIZED, Call(m, "sceNetAdhocMatchingCreate", 1, 4, (ushort)1, 2048, 0u, 0u, 3, 0u, 0u));
        Assert.Equal(0, Call(m, "sceNetAdhocMatchingInit", 0x1000u, pool));
        Assert.Equal(NetAdhocMatching.ERR_ALREADY_INITIALIZED, Call(m, "sceNetAdhocMatchingInit", 0x1000u, pool));
        int id = Call(m, "sceNetAdhocMatchingCreate", 1, 4, (ushort)1, 2048, 0u, 0u, 3, 0u, 0u);
        Assert.True(id > 0);
        Assert.Equal(NetAdhocMatching.ERR_PORT_IN_USE, Call(m, "sceNetAdhocMatchingCreate", 2, 4, (ushort)1, 2048, 0u, 0u, 3, 0u, 0u));
        Assert.Equal(NetAdhocMatching.ERR_INVALID_MODE, Call(m, "sceNetAdhocMatchingCreate", 0, 4, (ushort)2, 2048, 0u, 0u, 3, 0u, 0u));
        Assert.Equal(NetAdhocMatching.ERR_NOT_RUNNING, Call(m, "sceNetAdhocMatchingSelectTarget", id, addr, 0, 0u));
        Assert.Equal(0, Call(m, "sceNetAdhocMatchingStart", id, 0x40, 0x4000, 0, 0, 0u));
        Assert.Equal(NetAdhocMatching.ERR_IS_RUNNING, Call(m, "sceNetAdhocMatchingStart", id, 0x40, 0x4000, 0, 0, 0u));
        Assert.Equal(NetAdhocMatching.ERR_UNKNOWN_TARGET, Call(m, "sceNetAdhocMatchingSelectTarget", id, addr, 0, 0u));
        Assert.Equal(NetAdhocMatching.ERR_UNKNOWN_TARGET, Call(m, "sceNetAdhocMatchingSendData", id, addr, 4, addr));
        Assert.Equal(0, Call(m, "sceNetAdhocMatchingStop", id));
        Assert.Equal(0, Call(m, "sceNetAdhocMatchingDelete", id));
        Assert.Equal(NetAdhocMatching.ERR_INVALID_ID, Call(m, "sceNetAdhocMatchingDelete", id));
        Assert.Equal(0, Call(m, "sceNetAdhocMatchingTerm"));
    }

    /// <summary>CA_LIST.cer: "#name" lines and blank lines between the PEM blocks are not part of a certificate; each
    /// block runs from BEGIN through the CRLF after END (the byte ranges libssl 3.74 reads).</summary>
    [Fact]
    public void CaListBlocks()
    {
        string pem1 = "-----BEGIN CERTIFICATE-----\r\nAAAA\r\n-----END CERTIFICATE-----\r\n";
        string pem2 = "-----BEGIN CERTIFICATE-----\r\nBBBBBB\r\n-----END CERTIFICATE-----";
        byte[] file = Encoding.ASCII.GetBytes("#Root A  \r\n" + pem1 + "\r\n##Group\r\n#Root B\r\n" + pem2);
        byte[][] certs = Ssl.ParseCaList(file);
        Assert.Equal(2, certs.Length);
        Assert.Equal(pem1, Encoding.ASCII.GetString(certs[0]));
        Assert.Equal(pem2, Encoding.ASCII.GetString(certs[1]));
    }

    /// <summary>
    /// sceSslInternalGetCertificateAuthority serves the roots of vs0:data/external/cert/CA_LIST.cer as libssl 3.74 does:
    /// without the firmware's file 0x80010002; a size query (no list or buffer) writes only the size, each certificate
    /// taking its text, a NUL padded to 4 and an 8-byte node; a fill copies the texts and appends a node per certificate
    /// to the caller's list after the nodes already there; a buffer too small is 0x8001000C. Only the roots are handed
    /// out: what the guest's TLS code verifies against them is its own.
    /// </summary>
    [Fact]
    public void CertificateAuthoritiesComeFromTheFirmwaresCaList()
    {
        using var h = new HleHarness(fx, "ssl-ca");
        Ssl ssl = h.Module<Ssl>();
        uint used = h.Word(0);
        Assert.Equal(unchecked((int)0x80010002), Call(ssl, "sceSslInternalGetCertificateAuthority", 0, 0xFFFFFFFFu, 0u, 0u, 0u, used));

        string pem1 = "-----BEGIN CERTIFICATE-----\r\nAAAA\r\n-----END CERTIFICATE-----\r\n";
        string pem2 = "-----BEGIN CERTIFICATE-----\r\nBBBBBBB\r\n-----END CERTIFICATE-----\r\n";
        string caList = h.Emu.Vfs.Resolve("vs0:data/external/cert/CA_LIST.cer", forWrite: true)!;
        Directory.CreateDirectory(Path.GetDirectoryName(caList)!);
        File.WriteAllBytes(caList, Encoding.ASCII.GetBytes("#Root A\r\n" + pem1 + "\r\n#Root B\r\n" + pem2));
        // the missing file is not remembered: the next call reads it
        uint size1 = (((uint)pem1.Length + 1 + 3) & ~3u) + 8, size2 = (((uint)pem2.Length + 1 + 3) & ~3u) + 8;
        Assert.Equal(2, Call(ssl, "sceSslInternalGetCertificateAuthority", 0, 0xFFFFFFFFu, 0u, 0u, 0u, used));
        Assert.Equal(size1 + size2, GuestMemory.R32(used));

        // the caller's list already holds one node: the roots go after it
        uint existing = h.Alloc(8);
        GuestMemory.W32(existing, 0x1234);
        GuestMemory.W32(existing + 4, 0);
        uint list = h.Word(existing);
        uint buffer = h.Alloc(size1 + size2);
        Assert.Equal(unchecked((int)0x8001000C), Call(ssl, "sceSslInternalGetCertificateAuthority", 0, 0xFFFFFFFFu, list, buffer, size1 + size2 - 1, used));
        GuestMemory.W32(existing + 4, 0);
        Assert.Equal(2, Call(ssl, "sceSslInternalGetCertificateAuthority", 0, 0xFFFFFFFFu, list, buffer, size1 + size2, used));
        Assert.Equal(existing, GuestMemory.R32(list));
        uint first = GuestMemory.R32(existing + 4);
        Assert.Equal(buffer + size1 - 8, first);
        Assert.Equal(pem1, GuestMemory.ReadCString(GuestMemory.R32(first), 0x100));
        uint second = GuestMemory.R32(first + 4);
        Assert.Equal(buffer + size1 + size2 - 8, second);
        Assert.Equal(pem2, GuestMemory.ReadCString(GuestMemory.R32(second), 0x100));
        Assert.Equal(0u, GuestMemory.R32(second + 4));
    }

    /// <summary>
    /// libnet's errno is per thread and always there: a failed call writes the calling guest thread's errno even
    /// before that thread ever asked for sceNetErrnoLoc (ScePsp2Compat's connect() reads it only after a non-blocking
    /// sceNetConnect failed; with one slot made on the first sceNetErrnoLoc, WebKit's first connection saw 0), and
    /// another thread's errno is another word, untouched.
    /// </summary>
    [Fact]
    public void EachGuestThreadHasItsOwnErrnoWrittenBeforeItAsksForIt()
    {
        using var h = new HleHarness(fx, "net-errno");
        Net net = h.Module<Net>();
        uint first = 0, second = 0;
        h.OnGuest(_ =>
        {
            Assert.Equal(Net.EBADF, Call(net, "sceNetSocketClose", 0x7FFF));
            first = (uint)Invoke(net, "sceNetErrnoLoc");
            Assert.Equal(first, (uint)Invoke(net, "sceNetErrnoLoc"));
        }, "errno-a");
        Assert.NotEqual(0u, first);
        Assert.Equal((uint)(Net.EBADF & 0xFF), GuestMemory.R32(first));

        h.OnGuest(_ =>
        {
            second = (uint)Invoke(net, "sceNetErrnoLoc");
        }, "errno-b");
        Assert.NotEqual(first, second);
        Assert.Equal(0u, GuestMemory.R32(second));
        Assert.Equal((uint)(Net.EBADF & 0xFF), GuestMemory.R32(first));
    }
}
