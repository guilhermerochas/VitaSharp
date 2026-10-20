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
    static object Invoke<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;
    static int Call<T>(T module, string name, params object[] args) => (int)Invoke(module, name, args);

    uint Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        uint p = fx.Memory.Alloc((uint)b.Length + 16, "str");
        b.CopyTo(GuestMemory.Span(p, b.Length));
        GuestMemory.Write<byte>(p + (uint)b.Length, 0);
        return p;
    }

    static string L1(byte[] b) => Encoding.Latin1.GetString(b);

    [Fact]
    public void EscapeUnescape()
    {
        var input = Encoding.UTF8.GetBytes("a b&c=d/é");
        Assert.Equal("a%20b%26c%3Dd%2F%C3%A9", L1(HttpUri.Escape(input)));
        Assert.Equal("AZaz09-._~", L1(HttpUri.Escape("AZaz09-._~"u8)));
        Assert.Equal("JJ%zz%", L1(HttpUri.Unescape("%4a%4A%zz%"u8)));
        var all = Enumerable.Range(1, 255).Select(i => (byte)i).ToArray();
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
        const string url = "http://user:pw@host:8080/a/b?x=1#f";
        Assert.Equal(0, HttpUri.ParseStrict(url, out var p));
        var e = HttpUri.ToElement(p);
        Assert.Equal(("http", "user", "pw", "host", "/a/b", "?x=1", "#f", (ushort)8080), (e.Scheme, e.User, e.Password, e.Host, e.Path, e.Query, e.Fragment, e.Port));
        Assert.Equal(url, HttpUri.Build(e, HttpUri.BuildAll));
        Assert.Equal("host/a/b", HttpUri.Build(e, HttpUri.BuildHostname | HttpUri.BuildPath));
        Assert.Equal(HttpUri.ERR_UNKNOWN_SCHEME, HttpUri.ParseStrict("ftp://x/", out _));
        Assert.Equal(HttpUri.ERR_INVALID_URL, HttpUri.ParseStrict("nothing", out _));

        var http = new Http();
        uint src = Str(url), elem = fx.Memory.Alloc(0x40, "elem"), pool = fx.Memory.Alloc(64, "pool"), req = fx.Memory.Alloc(16, "req");
        Assert.Equal(0, Call(http, "sceHttpUriParse", 0u, src, 0u, req, 0u));
        Assert.Equal(31u, GuestMemory.R32(req));
        Assert.Equal(0, Call(http, "sceHttpUriParse", elem, src, pool, req, 64u));
        Assert.Equal("host", GuestMemory.ReadCString(GuestMemory.R32(elem + 0x10)));
        Assert.Equal("?x=1", GuestMemory.ReadCString(GuestMemory.R32(elem + 0x18)));
        Assert.Equal((ushort)8080, GuestMemory.Read<ushort>(elem + 0x20));
        uint outp = fx.Memory.Alloc(64, "out");
        Assert.Equal(0, Call(http, "sceHttpUriBuild", outp, req, 64u, elem, HttpUri.BuildAll));
        Assert.Equal(url, GuestMemory.ReadCString(outp));
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
        Assert.Equal(0, HttpUri.Merge("http://a/b/c/d;p?q", rel, out var r));
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

        var hdr = "Content-Type: text/html\r\nX-A:  1 \r\n"u8;
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
        var now = DateTime.UtcNow;
        Assert.True(jar.Add("http://h/x/y", "a=1; Path=/x", now));
        Assert.Equal("a=1", jar.Get("http://h/x/z", false, now));
        Assert.Null(jar.Get("http://h/w", false, now));
        var data = jar.Export();
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

        var waiter = Task.Run(() => Call(net, "sceNetEpollWait", eid, ev, 1, -1));
        Thread.Sleep(50);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(0, Call(net, "sceNetEpollAbort", eid, 0));
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Net.EINTR, await waiter);
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
        var waiter = Task.Run(() => Call(net, "sceNetRecv", s, 0u, 0u, 0));
        Thread.Sleep(50);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(0, Call(net, "sceNetSocketAbort", s, 0));
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
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
        var waiter = Task.Run(() => Call(rudp, "sceRudpPollWait", poll, evs, 4u, ulong.MaxValue));
        Thread.Sleep(50);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(0, Call(rudp, "sceRudpPollCancel", poll));
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Call(rudp, "sceRudpSetMaxSegmentSize", (ushort)1000));
        var mss = fx.Memory.Alloc(16, "mss");
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
}
