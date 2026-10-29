using System.Buffers.Binary;
using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceHttp. Offline (the default): templates, connections and requests are real objects (settings, headers, cookies,
/// abort state), every send fails like an unresolvable server (SCE_HTTP_ERROR_NETWORK), and the pure URI/header
/// utilities work as on hardware. With real internet (<c>--net=real</c>) requests go through .NET's HttpClient
/// (Http.Real.cs).
/// </summary>
public sealed partial class Http : HleModule
{
    const int ERR_BEFORE_INIT = unchecked((int)0x80431001), ERR_ALREADY_INITED = unchecked((int)0x80431020),
        ERR_BUSY = unchecked((int)0x80431021), ERR_OUT_OF_MEMORY = unchecked((int)0x80431022), ERR_NOT_FOUND = unchecked((int)0x80431025),
        ERR_NETWORK = unchecked((int)0x80431063), ERR_BEFORE_SEND = unchecked((int)0x80431065), ERR_AFTER_SEND = unchecked((int)0x80431066),
        ERR_INVALID_VERSION = unchecked((int)0x8043106A), ERR_UNKNOWN_METHOD = unchecked((int)0x8043106B),
        ERR_ABORTED = unchecked((int)0x80431080), ERR_INVALID_ID = unchecked((int)0x80431100), ERR_INVALID_VALUE = unchecked((int)0x804311FE);

    /// <summary>errno recorded for the failed send: SCE_NET_ERROR_RESOLVER_ENODNS & 0xFF.</summary>
    const int ErrnoNoDns = 0xE1;

    static readonly string[] Methods = ["GET", "POST", "HEAD", "OPTIONS", "PUT", "DELETE", "TRACE", "CONNECT"];

    enum Kind { Template, Connection, Request }

    sealed class Settings
    {
        public uint ResolveTimeoutUs = 1_000_000, ConnectTimeoutUs = 30_000_000, SendTimeoutUs = 120_000_000, RecvTimeoutUs = 120_000_000;
        public int ResolveRetry = 5;
        public uint ResponseHeaderMax = 5000;
        public int Nonblock, AutoRedirect = 1, AuthEnabled = 1, CookieEnabled = 1, AcceptGzip, InflateGzip = 1, IcmOption;
        public uint SslFlags;
        public (uint Func, uint Arg) AuthCb, RedirectCb, CookieRecvCb, CookieSendCb, SslCb;
        public int EpollId;
        public Settings Clone() => (Settings)MemberwiseClone();
    }

    sealed class Obj
    {
        public int Id, ParentId;
        public Kind Kind;
        public string UserAgent = "", Url = "";
        public int HttpVer, Method;
        public ulong ContentLength;
        public Settings S = new();
        public readonly List<(string Name, string Value)> Headers = new();
        public bool Aborted, Sent;
        public int LastErrno;
        public uint HeadersBuf, HeadersBufSize;
        public HttpEpoll? Epoll;
        public uint EpollArg;
        /// <summary>Real internet: the exchange in flight or done (Http.Real.cs).</summary>
        public Exchange? Ex;
        public uint RespHeadersBuf, RespHeadersBufSize;
    }

    sealed class HttpEpoll
    {
        public int Id;
        public readonly Queue<(uint Events, int Id, uint Arg)> Pending = new();
        public bool Aborted, Destroyed;
    }

    readonly object _lock = new();
    bool _inited;
    uint _poolSize;
    readonly Dictionary<int, Obj> _objs = new();
    readonly Dictionary<int, HttpEpoll> _epolls = new();
    int _nextId = 1, _nextEpoll = 1;
    /// <summary>SCE_HTTPS_FLAG_*: SERVER_VERIFY 0x01, CLIENT_VERIFY 0x02, CN_CHECK 0x04, NOT_AFTER_CHECK 0x08,
    /// NOT_BEFORE_CHECK 0x10, KNOWN_CA_CHECK 0x20; the library starts with the server checks on.</summary>
    uint _sslFlags = SslDefaultFlags;
    int _defaultGzip;
    readonly CookieJar _cookies = new();
    readonly List<byte[]> _certs = new();

    // ------------------------------------------------------------------ helpers

    static string? ReadL1(uint addr) => addr == 0 ? null : Encoding.Latin1.GetString(ReadBytes(addr));

    static ReadOnlySpan<byte> ReadBytes(uint addr)
    {
        if (addr == 0) return default;
        int n = 0;
        while (GuestMemory.Read<byte>(addr + (uint)n) != 0 && n < 1 << 20) n++;
        return GuestMemory.Span(addr, n);
    }

    /// <summary>Common output protocol of the URI functions: *require = size with NUL; out == NULL only queries.</summary>
    static int WriteOut(uint outAddr, uint requirePtr, uint prepare, ReadOnlySpan<byte> bytes)
    {
        uint need = (uint)bytes.Length + 1;
        if (requirePtr != 0) GuestMemory.W32(requirePtr, need);
        if (outAddr == 0) return 0;
        if (prepare < need) return ERR_OUT_OF_MEMORY;
        bytes.CopyTo(GuestMemory.Span(outAddr, bytes.Length));
        GuestMemory.Write<byte>(outAddr + (uint)bytes.Length, 0);
        return 0;
    }

    int Find(int id, out Obj o, Kind? kind = null)
    {
        o = null!;
        if (!_inited) return ERR_BEFORE_INIT;
        if (!_objs.TryGetValue(id, out var f) || (kind.HasValue && f.Kind != kind.Value)) return ERR_INVALID_ID;
        o = f;
        return 0;
    }

    int Add(Obj o)
    {
        o.Id = _nextId++;
        _objs[o.Id] = o;
        return o.Id;
    }

    int SetSetting(int id, Action<Settings> set)
    {
        lock (_lock)
        {
            int r = Find(id, out var o);
            if (r < 0) return r;
            set(o.S);
            return 0;
        }
    }

    int GetSetting(int id, Func<Settings, int> get, uint ptr)
    {
        lock (_lock)
        {
            int r = Find(id, out var o);
            if (r < 0) return r;
            if (ptr == 0) return ERR_INVALID_VALUE;
            GuestMemory.W32(ptr, (uint)get(o.S));
            return 0;
        }
    }

    void FreeObj(Obj o)
    {
        if (o.HeadersBuf != 0) { try { Emu.Memory.Free(o.HeadersBuf); } catch { } o.HeadersBuf = 0; }
        if (o.RespHeadersBuf != 0) { try { Emu.Memory.Free(o.RespHeadersBuf); } catch { } o.RespHeadersBuf = 0; }
        o.Ex?.Cancel();
        o.Epoll = null;
        o.Aborted = true;
    }

    // ------------------------------------------------------------------ init / pool

    [Hle]
    int sceHttpInit(uint poolSize)
    {
        lock (_lock)
        {
            if (_inited) { Log.Warn("sceHttpInit: already initialised"); return ERR_ALREADY_INITED; }
            if (poolSize == 0) return ERR_INVALID_VALUE;
            _inited = true;
            _poolSize = poolSize;
            return 0;
        }
    }

    [Hle]
    int sceHttpTerm()
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            foreach (var o in _objs.Values) FreeObj(o);
            _objs.Clear();
            foreach (var e in _epolls.Values) e.Destroyed = true;
            _epolls.Clear();
            _cookies.Flush();
            _inited = false;
            Monitor.PulseAll(_lock);
            return 0;
        }
    }

    /// <summary>SceHttpMemoryPoolStats: +0 poolSize, +4 maxInuseSize, +8 currentInuseSize, +C reserved.</summary>
    [Hle]
    int sceHttpGetMemoryPoolStats(uint stats)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (stats == 0) return ERR_INVALID_VALUE;
            GuestMemory.Fill(stats, 0, 16);
            GuestMemory.W32(stats, _poolSize);
            return 0;
        }
    }

    // ------------------------------------------------------------------ templates / connections / requests

    [Hle]
    int sceHttpCreateTemplate(uint userAgent, int httpVer, int autoProxyConf)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (httpVer is not (1 or 2)) return ERR_INVALID_VERSION;
            var o = new Obj { Kind = Kind.Template, UserAgent = ReadL1(userAgent) ?? "", HttpVer = httpVer };
            o.S.AcceptGzip = _defaultGzip;
            o.S.SslFlags = _sslFlags;
            return Add(o);
        }
    }

    int CreateConnection(int tmplId, string url)
    {
        int r = Find(tmplId, out var t, Kind.Template);
        if (r < 0) return r;
        return Add(new Obj { Kind = Kind.Connection, ParentId = tmplId, Url = url, HttpVer = t.HttpVer, UserAgent = t.UserAgent, S = t.S.Clone() });
    }

    [Hle]
    int sceHttpCreateConnection(int tmplId, uint serverName, uint scheme, ushort port, int enableKeepalive)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            string? host = ReadL1(serverName), sch = ReadL1(scheme);
            if (host == null || sch == null) return ERR_INVALID_VALUE;
            if (!sch.Equals("http", StringComparison.OrdinalIgnoreCase) && !sch.Equals("https", StringComparison.OrdinalIgnoreCase))
                return HttpUri.ERR_UNKNOWN_SCHEME;
            return CreateConnection(tmplId, port != 0 ? $"{sch}://{host}:{port}" : $"{sch}://{host}");
        }
    }

    [Hle]
    int sceHttpCreateConnectionWithURL(int tmplId, uint url, int enableKeepalive)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            string? u = ReadL1(url);
            if (u == null) return ERR_INVALID_VALUE;
            int r = HttpUri.ParseStrict(u, out _);
            if (r < 0) return r;
            return CreateConnection(tmplId, u);
        }
    }

    int CreateRequest(int connId, int method, string? path, bool isUrl, ulong contentLength)
    {
        int r = Find(connId, out var c, Kind.Connection);
        if (r < 0) return r;
        if (path == null) return ERR_INVALID_VALUE;
        if (method is < 0 or >= 8) return ERR_UNKNOWN_METHOD;
        string url;
        if (isUrl)
        {
            r = HttpUri.ParseStrict(path, out _);
            if (r < 0) return r;
            url = path;
        }
        else url = c.Url + (path.StartsWith('/') ? path : "/" + path);
        return Add(new Obj
        {
            Kind = Kind.Request, ParentId = connId, Url = url, Method = method, ContentLength = contentLength,
            HttpVer = c.HttpVer, UserAgent = c.UserAgent, S = c.S.Clone(),
        });
    }

    static int MethodIndex(string? m) => m == null ? -2 : Array.IndexOf(Methods, m);

    [Hle]
    int sceHttpCreateRequest(int connId, int method, uint path, ulong contentLength)
    {
        lock (_lock) return !_inited ? ERR_BEFORE_INIT : CreateRequest(connId, method, ReadL1(path), false, contentLength);
    }

    [Hle]
    int sceHttpCreateRequestWithURL(int connId, int method, uint url, ulong contentLength)
    {
        lock (_lock) return !_inited ? ERR_BEFORE_INIT : CreateRequest(connId, method, ReadL1(url), true, contentLength);
    }

    [Hle]
    int sceHttpCreateRequest2(int connId, uint method, uint path, ulong contentLength)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            int m = MethodIndex(ReadL1(method));
            if (m == -2) return ERR_INVALID_VALUE;
            return CreateRequest(connId, m < 0 ? 99 : m, ReadL1(path), false, contentLength);
        }
    }

    [Hle]
    int sceHttpCreateRequestWithURL2(int connId, uint method, uint url, ulong contentLength)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            int m = MethodIndex(ReadL1(method));
            if (m == -2) return ERR_INVALID_VALUE;
            return CreateRequest(connId, m < 0 ? 99 : m, ReadL1(url), true, contentLength);
        }
    }

    int Delete(int id, Kind kind)
    {
        lock (_lock)
        {
            int r = Find(id, out var o, kind);
            if (r < 0) return r;
            _objs.Remove(id);
            FreeObj(o);
            Monitor.PulseAll(_lock);
            return 0;
        }
    }

    [Hle] int sceHttpDeleteTemplate(int tmplId) => Delete(tmplId, Kind.Template);
    [Hle] int sceHttpDeleteConnection(int connId) => Delete(connId, Kind.Connection);
    [Hle] int sceHttpDeleteRequest(int reqId) => Delete(reqId, Kind.Request);

    [Hle("sceHttpAbortRequest")]
    [Hle("sceHttpAbortRequestForce")]
    int sceHttpAbortRequest(int reqId)
    {
        lock (_lock)
        {
            int r = Find(reqId, out var o, Kind.Request);
            if (r < 0) return r;
            o.Aborted = true;
            o.Ex?.Cancel();
            Monitor.PulseAll(_lock);
            return 0;
        }
    }

    [Hle]
    int sceHttpSendRequest(int reqId, uint postData, uint size)
    {
        lock (_lock)
        {
            int r = Find(reqId, out var o, Kind.Request);
            if (r < 0) return r;
            if (o.Aborted) return ERR_ABORTED;
            if (Cfg.Real) return RealSendRequest(o, postData, size);
            // non-blocking request bound to an HTTP epoll: report "in progress" once, then the resolver failure as an event
            if (o.S.Nonblock != 0 && o.Epoll != null && !o.Sent)
            {
                o.Sent = true;
                o.LastErrno = ErrnoNoDns;
                o.Epoll.Pending.Enqueue((0x20000 /* RESOLVER_ERR */, o.Id, o.EpollArg));
                Monitor.PulseAll(_lock);
                return ERR_BUSY;
            }
            o.Sent = true;
            o.LastErrno = ErrnoNoDns;
            Log.Debug($"sceHttpSendRequest({o.Url}): offline");
            return ERR_NETWORK;
        }
    }

    int ResponseState(int reqId)
    {
        int r = Find(reqId, out var o, Kind.Request);
        if (r < 0) return r;
        if (o.Aborted) return ERR_ABORTED;
        return o.Sent ? ERR_NETWORK : ERR_BEFORE_SEND;
    }

    /// <summary>Runs <paramref name="real"/> for a request sent over real internet, else the offline answer.</summary>
    int OnResponse(int reqId, Func<Obj, int> real)
    {
        lock (_lock)
        {
            int r = Find(reqId, out var o, Kind.Request);
            if (r < 0) return r;
            if (o.Ex != null && !o.Aborted) return real(o);
            return ResponseState(reqId);
        }
    }

    [Hle] int sceHttpGetStatusCode(int reqId, uint statusCode) => OnResponse(reqId, o => RealStatusCode(o, statusCode));
    /// <summary>int (int reqId, SceULong64 *contentLength).</summary>
    [Hle] int sceHttpGetResponseContentLength(int reqId, uint contentLength) => OnResponse(reqId, o => RealContentLength(o, contentLength));
    [Hle] int sceHttpReadData(int reqId, uint data, uint size) => OnResponse(reqId, o => RealReadData(o, data, size));
    [Hle] int sceHttpGetAllResponseHeaders(int reqId, uint header, uint headerSize) => OnResponse(reqId, o => RealResponseHeaders(o, header, headerSize));

    [Hle]
    int sceHttpGetLastErrno(int reqId, uint errNum)
    {
        lock (_lock)
        {
            int r = Find(reqId, out var o, Kind.Request);
            if (r < 0) return r;
            if (errNum == 0) return ERR_INVALID_VALUE;
            GuestMemory.W32(errNum, (uint)o.LastErrno);
            return 0;
        }
    }

    [Hle]
    int sceHttpSetRequestContentLength(int id, ulong contentLength)
    {
        lock (_lock)
        {
            int r = Find(id, out var o, Kind.Request);
            if (r < 0) return r;
            if (o.Sent) return ERR_AFTER_SEND;
            o.ContentLength = contentLength;
            return 0;
        }
    }

    // ------------------------------------------------------------------ request headers

    const uint HeaderOverwrite = 0, HeaderAdd = 1;

    static void AddHeader(Obj o, string name, string value, uint mode)
    {
        if (mode == HeaderOverwrite) o.Headers.RemoveAll(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        o.Headers.Add((name, value));
    }

    [Hle]
    int sceHttpAddRequestHeader(int id, uint name, uint value, uint mode)
    {
        lock (_lock)
        {
            int r = Find(id, out var o);
            if (r < 0) return r;
            string? n = ReadL1(name), v = ReadL1(value);
            if (n == null || v == null || mode > HeaderAdd) return ERR_INVALID_VALUE;
            AddHeader(o, n, v, mode);
            return 0;
        }
    }

    /// <summary>Prototype inferred (id, header, headerLen, mode): a preformatted "Name: value" line.</summary>
    [Hle]
    int sceHttpAddRequestHeaderRaw(int id, uint header, uint headerLen, uint mode)
    {
        lock (_lock)
        {
            int r = Find(id, out var o);
            if (r < 0) return r;
            if (header == 0) return ERR_INVALID_VALUE;
            Log.Debug($"sceHttpAddRequestHeaderRaw({id}, 0x{header:X8}, {headerLen}, {mode})");
            var bytes = headerLen is > 0 and < 0x10000 ? GuestMemory.Span(header, (int)headerLen) : ReadBytes(header);
            string line = Encoding.Latin1.GetString(bytes).TrimEnd('\r', '\n', '\0');
            int c = line.IndexOf(':');
            if (c <= 0) return ERR_INVALID_VALUE;
            AddHeader(o, line[..c].Trim(), line[(c + 1)..].Trim(), HeaderAdd);
            return 0;
        }
    }

    [Hle]
    int sceHttpRemoveRequestHeader(int id, uint name)
    {
        lock (_lock)
        {
            int r = Find(id, out var o);
            if (r < 0) return r;
            string? n = ReadL1(name);
            if (n == null) return ERR_INVALID_VALUE;
            o.Headers.RemoveAll(h => h.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            return 0;
        }
    }

    /// <summary>int sceHttpRequestGetAllHeaders(int reqId, char **header, unsigned int *headerSize) (inferred from Vita3K).
    /// The buffer belongs to the library and lives until the request is deleted.</summary>
    [Hle]
    int sceHttpRequestGetAllHeaders(int reqId, uint header, uint headerSize)
    {
        lock (_lock)
        {
            int r = Find(reqId, out var o, Kind.Request);
            if (r < 0) return r;
            if (header == 0 || headerSize == 0) return ERR_INVALID_VALUE;
            var sb = new StringBuilder();
            foreach (var (n, v) in o.Headers) sb.Append(n).Append(": ").Append(v).Append("\r\n");
            var bytes = Encoding.Latin1.GetBytes(sb.ToString());
            uint need = (uint)bytes.Length + 1;
            if (o.HeadersBuf == 0 || o.HeadersBufSize < need)
            {
                if (o.HeadersBuf != 0) Emu.Memory.Free(o.HeadersBuf);
                o.HeadersBufSize = Math.Max(need, 0x100);
                o.HeadersBuf = Emu.Memory.Alloc(o.HeadersBufSize, "http request headers", MemoryRegion.User, 16);
            }
            bytes.CopyTo(GuestMemory.Span(o.HeadersBuf, bytes.Length));
            GuestMemory.Write<byte>(o.HeadersBuf + (uint)bytes.Length, 0);
            GuestMemory.W32(header, o.HeadersBuf);
            GuestMemory.W32(headerSize, (uint)bytes.Length);
            return 0;
        }
    }

    // ------------------------------------------------------------------ settings

    [Hle] int sceHttpSetConnectTimeOut(int id, uint usec) => SetSetting(id, s => s.ConnectTimeoutUs = usec);
    [Hle] int sceHttpSetSendTimeOut(int id, uint usec) => SetSetting(id, s => s.SendTimeoutUs = usec);
    [Hle] int sceHttpSetRecvTimeOut(int id, uint usec) => SetSetting(id, s => s.RecvTimeoutUs = usec);
    [Hle] int sceHttpSetResolveTimeOut(int id, uint usec) => SetSetting(id, s => s.ResolveTimeoutUs = usec);
    [Hle] int sceHttpSetResolveRetry(int id, int retry) => retry < 0 && _inited ? ERR_INVALID_VALUE : SetSetting(id, s => s.ResolveRetry = retry);
    [Hle] int sceHttpSetNonblock(int id, int enable) => SetSetting(id, s => s.Nonblock = enable != 0 ? 1 : 0);
    [Hle] int sceHttpGetNonblock(int id, uint enable) => GetSetting(id, s => s.Nonblock, enable);
    [Hle] int sceHttpSetAuthEnabled(int id, int enable) => SetSetting(id, s => s.AuthEnabled = enable != 0 ? 1 : 0);
    [Hle] int sceHttpGetAuthEnabled(int id, uint enable) => GetSetting(id, s => s.AuthEnabled, enable);
    [Hle] int sceHttpSetAutoRedirect(int id, int enable) => SetSetting(id, s => s.AutoRedirect = enable != 0 ? 1 : 0);
    [Hle] int sceHttpGetAutoRedirect(int id, uint enable) => GetSetting(id, s => s.AutoRedirect, enable);
    [Hle] int sceHttpSetCookieEnabled(int id, int enable) => SetSetting(id, s => s.CookieEnabled = enable != 0 ? 1 : 0);
    [Hle] int sceHttpGetCookieEnabled(int id, uint enable) => GetSetting(id, s => s.CookieEnabled, enable);
    [Hle] int sceHttpSetAcceptEncodingGZIPEnabled(int id, int enable) => SetSetting(id, s => s.AcceptGzip = enable != 0 ? 1 : 0);
    [Hle] int sceHttpGetAcceptEncodingGZIPEnabled(int id, uint enable) => GetSetting(id, s => s.AcceptGzip, enable);
    [Hle] int sceHttpSetInflateGZIPEnabled(int id, int enable) => SetSetting(id, s => s.InflateGzip = enable != 0 ? 1 : 0);
    [Hle] int sceHttpSetIcmOption(int id, int value) { Log.Debug($"sceHttpSetIcmOption({id}, {value})"); return SetSetting(id, s => s.IcmOption = value); }
    [Hle] int sceHttpGetIcmOption(int id, uint value) => GetSetting(id, s => s.IcmOption, value);

    [Hle]
    int sceHttpSetResponseHeaderMaxSize(int id, uint headerSize)
    {
        if (_inited && headerSize > 5000) return ERR_INVALID_VALUE;
        return SetSetting(id, s => s.ResponseHeaderMax = headerSize);
    }

    [Hle]
    int sceHttpSetDefaultAcceptEncodingGZIPEnabled(int enable)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            _defaultGzip = enable != 0 ? 1 : 0;
            return 0;
        }
    }

    // callbacks: stored, never invoked offline (no server response, redirect, auth challenge or cookie)
    [Hle] int sceHttpSetAuthInfoCallback(int id, uint cbfunc, uint userArg) => SetSetting(id, s => s.AuthCb = (cbfunc, userArg));
    [Hle] int sceHttpSetRedirectCallback(int id, uint cbfunc, uint userArg) => SetSetting(id, s => s.RedirectCb = (cbfunc, userArg));
    [Hle] int sceHttpSetCookieRecvCallback(int id, uint cbfunc, uint userArg) => SetSetting(id, s => s.CookieRecvCb = (cbfunc, userArg));
    [Hle] int sceHttpSetCookieSendCallback(int id, uint cbfunc, uint userArg) => SetSetting(id, s => s.CookieSendCb = (cbfunc, userArg));

    // ------------------------------------------------------------------ URI utilities (pure)

    [Hle]
    int sceHttpUriEscape(uint outAddr, uint require, uint prepare, uint input)
    {
        if (input == 0) return ERR_INVALID_VALUE;
        return WriteOut(outAddr, require, prepare, HttpUri.Escape(ReadBytes(input)));
    }

    [Hle]
    int sceHttpUriUnescape(uint outAddr, uint require, uint prepare, uint input)
    {
        if (input == 0) return ERR_INVALID_VALUE;
        return WriteOut(outAddr, require, prepare, HttpUri.Unescape(ReadBytes(input)));
    }

    /// <summary>SceHttpUriElement (0x2C): +0 opaque, +4 scheme, +8 username, +C password, +10 hostname, +14 path,
    /// +18 query, +1C fragment, +20 port (u16), +22 reserved[10].</summary>
    [Hle]
    int sceHttpUriParse(uint outElem, uint srcUrl, uint pool, uint require, uint prepare)
    {
        if (srcUrl == 0) return HttpUri.ERR_INVALID_URL;
        if ((pool == 0 || outElem == 0) && require == 0) return ERR_INVALID_VALUE;
        int r = HttpUri.ParseStrict(ReadL1(srcUrl)!, out var parts);
        if (r < 0) return r;
        var e = HttpUri.ToElement(parts);
        string[] fields = [e.Scheme, e.User, e.Password, e.Host, e.Path, e.Query, e.Fragment];
        uint need = 0;
        foreach (var f in fields) need += (uint)f.Length + 1;
        if (require != 0) GuestMemory.W32(require, need);
        if (pool == 0 || outElem == 0) return 0;
        if (prepare < need) return ERR_OUT_OF_MEMORY;
        GuestMemory.Fill(outElem, 0, 0x2C);
        uint p = pool;
        for (int i = 0; i < fields.Length; i++)
        {
            var b = Encoding.Latin1.GetBytes(fields[i]);
            b.CopyTo(GuestMemory.Span(p, b.Length));
            GuestMemory.Write<byte>(p + (uint)b.Length, 0);
            GuestMemory.W32(outElem + 4 + (uint)i * 4, p);
            p += (uint)b.Length + 1;
        }
        GuestMemory.Write(outElem + 0x20, e.Port);
        return 0;
    }

    [Hle]
    int sceHttpUriBuild(uint outAddr, uint require, uint prepare, uint srcElement, uint option)
    {
        if (srcElement == 0) return ERR_INVALID_VALUE;
        string F(uint off) => ReadL1(GuestMemory.R32(srcElement + off)) ?? "";
        var e = new HttpUri.Element(GuestMemory.R32(srcElement) != 0, F(4), F(8), F(0xC), F(0x10), F(0x14), F(0x18), F(0x1C),
            GuestMemory.Read<ushort>(srcElement + 0x20));
        return WriteOut(outAddr, require, prepare, Encoding.Latin1.GetBytes(HttpUri.Build(e, option)));
    }

    [Hle]
    int sceHttpUriMerge(uint mergedUrl, uint url, uint relativeUrl, uint require, uint prepare, uint option)
    {
        if (url == 0) return HttpUri.ERR_INVALID_URL;
        if (relativeUrl == 0) return ERR_INVALID_VALUE;
        if (option != 0) Log.Debug($"sceHttpUriMerge: option {option}");
        int r = HttpUri.Merge(ReadL1(url)!, ReadL1(relativeUrl)!, out var s);
        if (r < 0) return r;
        return WriteOut(mergedUrl, require, prepare, Encoding.Latin1.GetBytes(s));
    }

    [Hle]
    int sceHttpUriSweepPath(uint dst, uint src, uint srcSize)
    {
        if (srcSize == 0) return 0;
        if (dst == 0 || src == 0) return ERR_INVALID_VALUE;
        var bytes = ReadBytes(src);
        if (bytes.Length > srcSize - 1) bytes = bytes[..(int)(srcSize - 1)];
        var outBytes = Encoding.Latin1.GetBytes(HttpUri.SweepPath(Encoding.Latin1.GetString(bytes)));
        int n = Math.Min(outBytes.Length, (int)srcSize - 1);
        outBytes.AsSpan(0, n).CopyTo(GuestMemory.Span(dst, n));
        GuestMemory.Write<byte>(dst + (uint)n, 0);
        return 0;
    }

    [Hle]
    int sceHttpParseStatusLine(uint statusLine, uint lineLen, uint majorVer, uint minorVer, uint responseCode, uint reasonPhrase, uint phraseLen)
    {
        if (statusLine == 0) return HttpParse.ERR_INVALID_RESPONSE;
        if (majorVer == 0 || minorVer == 0 || responseCode == 0 || reasonPhrase == 0 || phraseLen == 0) return ERR_INVALID_VALUE;
        GuestMemory.W32(majorVer, 0);
        GuestMemory.W32(minorVer, 0);
        int r = HttpParse.StatusLine(GuestMemory.Span(statusLine, (int)Math.Min(lineLen, 1u << 20)), out int maj, out int min, out int code, out int off, out int len);
        if (r < 0) return r;
        GuestMemory.W32(majorVer, (uint)maj);
        GuestMemory.W32(minorVer, (uint)min);
        GuestMemory.W32(responseCode, (uint)code);
        GuestMemory.W32(reasonPhrase, statusLine + (uint)off);
        GuestMemory.W32(phraseLen, (uint)len);
        return 0;
    }

    [Hle]
    int sceHttpParseResponseHeader(uint header, uint headerLen, uint fieldStr, uint fieldValue, uint valueLen)
    {
        if (header == 0 || fieldStr == 0 || fieldValue == 0 || valueLen == 0) return unchecked((int)0x804321FE);
        if (!HttpParse.FindHeader(GuestMemory.Span(header, (int)Math.Min(headerLen, 1u << 20)), ReadBytes(fieldStr), out int off, out int len))
            return HttpParse.ERR_NOT_FOUND;
        GuestMemory.W32(fieldValue, header + (uint)off);
        GuestMemory.W32(valueLen, (uint)len);
        return 0;
    }

    // ------------------------------------------------------------------ cookies

    [Hle]
    int sceHttpAddCookie(uint url, uint cookie, uint cookieLength)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (url == 0 || cookie == 0) return ERR_INVALID_VALUE;
            var c = cookieLength is > 0 and < 0x10000 ? GuestMemory.Span(cookie, (int)cookieLength) : ReadBytes(cookie);
            int z = c.IndexOf((byte)0);
            if (z >= 0) c = c[..z];
            return _cookies.Add(ReadL1(url)!, Encoding.Latin1.GetString(c), DateTime.UtcNow) ? 0 : ERR_INVALID_VALUE;
        }
    }

    [Hle]
    int sceHttpGetCookie(uint url, uint cookie, uint cookieLength, uint prepare, int secure)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (url == 0 || cookieLength == 0) return ERR_INVALID_VALUE;
            string? s = _cookies.Get(ReadL1(url)!, secure != 0, DateTime.UtcNow);
            if (s == null) return ERR_NOT_FOUND;
            return WriteOut(cookie, cookieLength, prepare, Encoding.Latin1.GetBytes(s));
        }
    }

    [Hle] int sceHttpCookieFlush() { lock (_lock) { if (!_inited) return ERR_BEFORE_INIT; _cookies.Flush(); return 0; } }

    /// <summary>int sceHttpCookieExport(void *buffer, unsigned int bufferSize, unsigned int *exportSize) (inferred).</summary>
    [Hle]
    int sceHttpCookieExport(uint buffer, uint bufferSize, uint exportSize)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            var data = _cookies.Export();
            if (exportSize != 0) GuestMemory.W32(exportSize, (uint)data.Length);
            if (buffer == 0) return exportSize == 0 ? ERR_INVALID_VALUE : 0;
            if (bufferSize < data.Length) return ERR_OUT_OF_MEMORY;
            data.CopyTo(GuestMemory.Span(buffer, data.Length));
            return 0;
        }
    }

    [Hle]
    int sceHttpCookieImport(uint buffer, uint bufferSize)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (buffer == 0) return ERR_INVALID_VALUE;
            if (!_cookies.Import(GuestMemory.Span(buffer, (int)Math.Min(bufferSize, 1u << 20))))
                Log.Debug("sceHttpCookieImport: foreign cookie format ignored");
            return 0;
        }
    }

    int SetCookieLimit(uint v, Action<int> set)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (v == 0) return ERR_INVALID_VALUE;
            set((int)Math.Min(v, int.MaxValue));
            return 0;
        }
    }

    [Hle] int sceHttpSetCookieMaxNum(uint num) => SetCookieLimit(num, v => _cookies.MaxNum = v);
    [Hle] int sceHttpSetCookieMaxNumPerDomain(uint num) => SetCookieLimit(num, v => _cookies.MaxPerDomain = v);
    [Hle] int sceHttpSetCookieMaxSize(uint size) => SetCookieLimit(size, v => _cookies.MaxSize = v);
    [Hle] int sceHttpSetCookieTotalMaxSize(uint size) => SetCookieLimit(size, v => _cookies.TotalMaxSize = v);

    // ------------------------------------------------------------------ non-blocking requests (HTTP epoll)

    int FindEpoll(uint handle, out HttpEpoll e)
    {
        e = null!;
        if (!_inited) return ERR_BEFORE_INIT;
        if (!_epolls.TryGetValue((int)handle, out var f)) return ERR_INVALID_ID;
        e = f;
        return 0;
    }

    /// <summary>int sceHttpCreateEpoll(SceHttpEpollHandle *eh) (inferred): the handle is an opaque id.</summary>
    [Hle]
    int sceHttpCreateEpoll(uint eh)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (eh == 0) return ERR_INVALID_VALUE;
            var e = new HttpEpoll { Id = _nextEpoll++ };
            _epolls[e.Id] = e;
            GuestMemory.W32(eh, (uint)e.Id);
            return 0;
        }
    }

    [Hle]
    int sceHttpDestroyEpoll(uint eh)
    {
        lock (_lock)
        {
            int r = FindEpoll(eh, out var e);
            if (r < 0) return r;
            e.Destroyed = true;
            _epolls.Remove(e.Id);
            foreach (var o in _objs.Values) if (o.Epoll == e) o.Epoll = null;
            Monitor.PulseAll(_lock);
            return 0;
        }
    }

    [Hle]
    int sceHttpSetEpoll(int id, uint eh, uint userArg)
    {
        lock (_lock)
        {
            int r = Find(id, out var o, Kind.Request);
            if (r < 0) return r;
            r = FindEpoll(eh, out var e);
            if (r < 0) return r;
            o.Epoll = e;
            o.EpollArg = userArg;
            return 0;
        }
    }

    [Hle]
    int sceHttpGetEpoll(int id, uint eh, uint userArg)
    {
        lock (_lock)
        {
            int r = Find(id, out var o, Kind.Request);
            if (r < 0) return r;
            if (o.Epoll == null) return ERR_NOT_FOUND;
            if (eh != 0) GuestMemory.W32(eh, (uint)o.Epoll.Id);
            if (userArg != 0) GuestMemory.W32(userArg, o.EpollArg);
            return 0;
        }
    }

    [Hle]
    int sceHttpUnsetEpoll(int id)
    {
        lock (_lock)
        {
            int r = Find(id, out var o, Kind.Request);
            if (r < 0) return r;
            o.Epoll = null;
            return 0;
        }
    }

    [Hle] int sceHttpSetEpollId(int id, int epollId) { Log.Debug($"sceHttpSetEpollId({id}, {epollId})"); return SetSetting(id, s => s.EpollId = epollId); }
    [Hle] int sceHttpGetEpollId(int id, uint epollId) => GetSetting(id, s => s.EpollId, epollId);

    /// <summary>SceHttpNBEvent (0x10, inferred): +0 events, +4 eventDetail, +8 id, +C userArg. Timeout in microseconds,
    /// negative = forever.</summary>
    [Hle("sceHttpWaitRequest")]
    [Hle("sceHttpWaitRequestCB")]
    int sceHttpWaitRequest(uint eh, uint nbev, int maxevents, int timeout)
    {
        lock (_lock)
        {
            int r = FindEpoll(eh, out var e);
            if (r < 0) return r;
            if (maxevents > 0 && nbev == 0) return ERR_INVALID_VALUE;
            if (e.Pending.Count == 0 && timeout != 0)
            {
                int w = Kernel.Wait.Until(_lock, () => e.Pending.Count > 0 || e.Aborted || e.Destroyed, timeout < 0 ? null : timeout,
                    what: "http epoll");
                if (w == SceError.WAIT_TIMEOUT) return 0;
            }
            if (e.Aborted) { e.Aborted = false; return ERR_ABORTED; }
            if (e.Destroyed) return ERR_ABORTED;
            int n = 0;
            while (n < maxevents && e.Pending.TryDequeue(out var ev))
            {
                uint p = nbev + (uint)n * 16;
                GuestMemory.W32(p, ev.Events);
                GuestMemory.W32(p + 4, 0);
                GuestMemory.W32(p + 8, (uint)ev.Id);
                GuestMemory.W32(p + 12, ev.Arg);
                n++;
            }
            return n;
        }
    }

    [Hle]
    int sceHttpAbortWaitRequest(uint eh)
    {
        lock (_lock)
        {
            int r = FindEpoll(eh, out var e);
            if (r < 0) return r;
            e.Aborted = true;
            Monitor.PulseAll(_lock);
            return 0;
        }
    }

    // ------------------------------------------------------------------ HTTPS

    const int SslFlagMask = 0x3F;

    [Hle] int sceHttpsEnableOption(uint sslFlags) { lock (_lock) { if (!_inited) return ERR_BEFORE_INIT; _sslFlags |= sslFlags; return 0; } }
    [Hle] int sceHttpsDisableOption(uint sslFlags) { lock (_lock) { if (!_inited) return ERR_BEFORE_INIT; _sslFlags &= ~sslFlags; return 0; } }

    int TemplateSsl(int tmplId, Func<uint, uint> f)
    {
        lock (_lock)
        {
            int r = Find(tmplId, out var o, Kind.Template);
            if (r < 0) return r;
            o.S.SslFlags = f(o.S.SslFlags);
            return 0;
        }
    }

    [Hle] int sceHttpsEnableOption2(int tmplId, uint sslFlags) => TemplateSsl(tmplId, v => v | sslFlags);
    [Hle] int sceHttpsDisableOption2(int tmplId, uint sslFlags) => TemplateSsl(tmplId, v => v & ~sslFlags);
    [Hle] int sceHttpsEnableOptionPrivate(int id, uint flags) { Log.Debug($"sceHttpsEnableOptionPrivate({id}, 0x{flags:X})"); return _inited ? 0 : ERR_BEFORE_INIT; }
    [Hle] int sceHttpsDisableOptionPrivate(int id, uint flags) { Log.Debug($"sceHttpsDisableOptionPrivate({id}, 0x{flags:X})"); return _inited ? 0 : ERR_BEFORE_INIT; }

    /// <summary>SceHttpsData: +0 ptr, +4 size. Blobs are kept for a future real TLS backend.</summary>
    [Hle]
    int sceHttpsLoadCert(int caCertNum, uint caList, uint cert, uint privKey)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_BEFORE_INIT;
            if (caCertNum < 0 || (caCertNum > 0 && caList == 0)) return ERR_INVALID_VALUE;
            _certs.Clear();
            for (int i = 0; i < caCertNum; i++)
            {
                uint d = GuestMemory.R32(caList + (uint)i * 4);
                if (d == 0) continue;
                uint ptr = GuestMemory.R32(d), size = GuestMemory.R32(d + 4);
                if (ptr != 0 && size < 1 << 20) _certs.Add(GuestMemory.Span(ptr, (int)size).ToArray());
            }
            return 0;
        }
    }

    [Hle] int sceHttpsUnloadCert() { lock (_lock) { if (!_inited) return ERR_BEFORE_INIT; _certs.Clear(); return 0; } }

    [Hle]
    int sceHttpsSetSslCallback(int id, uint cbfunc, uint userArg)
    {
        if (_inited && cbfunc == 0) return ERR_INVALID_VALUE;
        return SetSetting(id, s => s.SslCb = (cbfunc, userArg));
    }

    /// <summary>int (int id, int *errNum, unsigned int *detail): the TLS failure of the last exchange (real internet),
    /// SCE_HTTPS_ERROR_CERT with the failed SCE_HTTPS_ERROR_SSL_* checks in detail; 0 when none.</summary>
    [Hle]
    int sceHttpsGetSslError(int id, Ptr<int> err, Ptr<uint> detail)
    {
        lock (_lock)
        {
            int r = Find(id, out var o);
            if (r < 0) return r;
            err.TrySet(o.Ex?.SslError ?? 0);
            detail.TrySet(o.Ex?.SslDetail ?? 0);
            return 0;
        }
    }

    /// <summary>SceHttpsCaList (8): +0 caCerts, +4 caNum. The emulated system has no CA list offline.</summary>
    [Hle]
    int sceHttpsGetCaList(uint caList)
    {
        if (!_inited) return ERR_BEFORE_INIT;
        if (caList == 0) return ERR_INVALID_VALUE;
        GuestMemory.W32(caList, 0);
        GuestMemory.W32(caList + 4, 0);
        return 0;
    }

    [Hle] int sceHttpsFreeCaList(uint caList) => !_inited ? ERR_BEFORE_INIT : caList == 0 ? ERR_INVALID_VALUE : 0;
    /// <summary>1 when the request (real internet) talks TLS, 0 otherwise (offline no TLS context ever exists).</summary>
    [Hle]
    int sceHttpSslIsCtxCreated(int id)
    {
        lock (_lock) return _objs.TryGetValue(id, out var o) && o.Ex != null && o.Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }
    [Hle] int sceHttpAuthCacheFlush() => _inited ? 0 : ERR_BEFORE_INIT;
    [Hle] int sceHttpRedirectCacheFlush() => _inited ? 0 : ERR_BEFORE_INIT;

    // ------------------------------------------------------------------ test hooks

    internal int ObjectCount { get { lock (_lock) return _objs.Count; } }
}

/// <summary>The library-wide cookie jar (RFC 6265 subset): domain/path matching, expiry, secure, limits.</summary>
public sealed class CookieJar
{
    public sealed class Cookie
    {
        public required string Name, Value, Domain, Path;
        public bool Secure, HostOnly;
        public DateTime? ExpiresUtc;
        public long Order;
    }

    readonly List<Cookie> _list = new();
    long _order;
    public int MaxNum = 300, MaxPerDomain = 50, MaxSize = 4096, TotalMaxSize = int.MaxValue;

    public void Flush() => _list.Clear();
    public int Count => _list.Count;

    static (string Host, string Path, bool Secure) UrlInfo(string url)
    {
        var p = HttpUri.Split(url);
        string path = p.Path == "" ? "/" : p.Path;
        return ((p.Host ?? "").ToLowerInvariant(), path, string.Equals(p.Scheme, "https", StringComparison.OrdinalIgnoreCase));
    }

    static string DefaultPath(string path)
    {
        if (!path.StartsWith('/')) return "/";
        int last = path.LastIndexOf('/');
        return last <= 0 ? "/" : path[..last];
    }

    /// <summary>Parses a Set-Cookie value received from <paramref name="url"/>.</summary>
    public bool Add(string url, string setCookie, DateTime now)
    {
        if (setCookie.Length > MaxSize) return false;
        var (host, path, _) = UrlInfo(url);
        var parts = setCookie.Split(';');
        int eq = parts[0].IndexOf('=');
        if (eq <= 0) return false;
        var c = new Cookie { Name = parts[0][..eq].Trim(), Value = parts[0][(eq + 1)..].Trim(), Domain = host, Path = DefaultPath(path), HostOnly = true };
        for (int i = 1; i < parts.Length; i++)
        {
            string a = parts[i].Trim();
            int e = a.IndexOf('=');
            string key = (e < 0 ? a : a[..e]).Trim(), val = e < 0 ? "" : a[(e + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "domain" when val != "": c.Domain = val.TrimStart('.').ToLowerInvariant(); c.HostOnly = false; break;
                case "path" when val.StartsWith('/'): c.Path = val; break;
                case "secure": c.Secure = true; break;
                case "max-age" when long.TryParse(val, out var s): c.ExpiresUtc = now.AddSeconds(s); break;
                case "expires" when c.ExpiresUtc == null && DateTime.TryParse(val, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d):
                    c.ExpiresUtc = d; break;
            }
        }
        _list.RemoveAll(x => x.Name == c.Name && x.Domain == c.Domain && x.Path == c.Path);
        if (c.ExpiresUtc is { } exp && exp <= now) return true; // deletion
        c.Order = _order++;
        _list.Add(c);
        Trim();
        return true;
    }

    void Trim()
    {
        foreach (var g in _list.GroupBy(x => x.Domain).Where(g => g.Count() > MaxPerDomain).ToList())
            foreach (var old in g.OrderBy(x => x.Order).Take(g.Count() - MaxPerDomain).ToList()) _list.Remove(old);
        while (_list.Count > MaxNum || (_list.Count > 0 && _list.Sum(x => x.Name.Length + x.Value.Length) > TotalMaxSize))
            _list.Remove(_list.MinBy(x => x.Order)!);
    }

    static bool DomainMatch(string host, Cookie c) => c.HostOnly ? host == c.Domain : host == c.Domain || host.EndsWith("." + c.Domain, StringComparison.Ordinal);

    static bool PathMatch(string path, string cp) => path == cp || (path.StartsWith(cp, StringComparison.Ordinal) && (cp.EndsWith('/') || path[cp.Length] == '/'));

    /// <summary>"n1=v1; n2=v2" for the cookies sent to <paramref name="url"/>, or null when none match.</summary>
    public string? Get(string url, bool secure, DateTime now)
    {
        var (host, path, _) = UrlInfo(url);
        _list.RemoveAll(c => c.ExpiresUtc is { } e && e <= now);
        var match = _list.Where(c => DomainMatch(host, c) && PathMatch(path, c.Path) && (!c.Secure || secure))
            .OrderByDescending(c => c.Path.Length).ThenBy(c => c.Order).ToList();
        return match.Count == 0 ? null : string.Join("; ", match.Select(c => c.Name + "=" + c.Value));
    }

    static readonly byte[] Magic = "VSCK"u8.ToArray();

    /// <summary>Private VitaSharp format: "VSCK", version 1, count, then length-prefixed fields per cookie.</summary>
    public byte[] Export()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(1); w.Write(_list.Count);
        foreach (var c in _list)
        {
            w.Write(c.Name); w.Write(c.Value); w.Write(c.Domain); w.Write(c.Path); w.Write(c.Secure); w.Write(c.HostOnly);
            w.Write(c.ExpiresUtc?.Ticks ?? 0L);
        }
        return ms.ToArray();
    }

    /// <summary>Restores an <see cref="Export"/> buffer; returns false (and keeps the jar) for an unknown format.</summary>
    public bool Import(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != 1) return false;
        try
        {
            using var r = new BinaryReader(new MemoryStream(data.ToArray()), Encoding.UTF8);
            r.ReadBytes(8);
            int n = r.ReadInt32();
            var list = new List<Cookie>();
            for (int i = 0; i < n; i++)
            {
                var c = new Cookie { Name = r.ReadString(), Value = r.ReadString(), Domain = r.ReadString(), Path = r.ReadString() };
                c.Secure = r.ReadBoolean(); c.HostOnly = r.ReadBoolean();
                long t = r.ReadInt64();
                c.ExpiresUtc = t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
                c.Order = _order++;
                list.Add(c);
            }
            foreach (var c in list)
            {
                _list.RemoveAll(x => x.Name == c.Name && x.Domain == c.Domain && x.Path == c.Path);
                _list.Add(c);
            }
            return true;
        }
        catch (EndOfStreamException) { return false; }
    }
}

/// <summary>SceSsl: library state and pool statistics. No TLS session or certificate exists offline.</summary>
public sealed class Ssl : HleModule
{
    const int ERR_BEFORE_INIT = unchecked((int)0x80435001), ERR_ALREADY_INITED = unchecked((int)0x80435020),
        ERR_INVALID_VALUE = unchecked((int)0x804351FE), ERR_NOT_FOUND = unchecked((int)0x80435025);

    bool _inited;
    uint _poolSize;

    [Hle]
    int sceSslInit(uint poolSize)
    {
        if (_inited) { Log.Warn("sceSslInit: already initialised"); return ERR_ALREADY_INITED; }
        if (poolSize == 0) return ERR_INVALID_VALUE;
        _inited = true;
        _poolSize = poolSize;
        return 0;
    }

    [Hle] int sceSslTerm() { if (!_inited) return ERR_BEFORE_INIT; _inited = false; return 0; }

    /// <summary>SceSslMemoryPoolStats (0x10): +0 poolSize, +4 maxInuseSize, +8 currentInuseSize.</summary>
    [Hle]
    int sceSslGetMemoryPoolStats(uint stats)
    {
        if (!_inited) return ERR_BEFORE_INIT;
        if (stats == 0) return ERR_INVALID_VALUE;
        GuestMemory.Fill(stats, 0, 16);
        GuestMemory.W32(stats, _poolSize);
        return 0;
    }

    [Hle] uint sceSslGetSubjectName(uint cert) => 0;
    [Hle] uint sceSslGetIssuerName(uint cert) => 0;
    [Hle] int sceSslFreeSslCertName(uint certName) => certName == 0 ? ERR_INVALID_VALUE : 0;
    [Hle] int sceSslGetNameEntryCount(uint certName) => certName == 0 ? ERR_NOT_FOUND : 0;
    [Hle] int sceSslGetNameEntryInfo(uint certName, int entry, uint oid, uint maxOid, uint value, uint maxValue, uint valueLen) => ERR_NOT_FOUND;
    [Hle] int sceSslGetNotAfter(uint cert, uint tick) => ERR_NOT_FOUND;
    [Hle] int sceSslGetNotBefore(uint cert, uint tick) => ERR_NOT_FOUND;
    [Hle] int sceSslGetSerialNumber(uint cert, uint data, uint len) => ERR_NOT_FOUND;
}
