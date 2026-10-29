using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceHttp with real internet (<see cref="NetConfig"/>): a request's exchange runs on a host task with .NET's HttpClient
/// (the template/connection/request headers, user agent, cookies, HTTP version, gzip and redirect settings applied; the
/// response body is streamed into a buffer, chunked transfer decoded by HttpClient). Guest calls wait cooperatively on
/// the module lock (<see cref="Kernel.Wait.Until(object, Func{bool}, long?, Func{int}?, string)"/>); in non-blocking
/// mode they return SCE_HTTP_ERROR_EAGAIN until the step is done and the HTTP epoll receives SCE_HTTP_NB_EVENT_IN (or
/// SOCK_ERR / RESOLVER_ERR) when headers or data arrive. Every destination, redirects included, goes through
/// <see cref="NetConfig.Permits(string, out string)"/>: a refused one fails exactly like the offline profile.
/// Error codes: vita-headers psp2/net/http.h (EAGAIN 0x80431082 as in the PS4 library).
/// </summary>
public sealed partial class Http
{
    const int ERR_EAGAIN = unchecked((int)0x80431082), ERR_TIMEOUT = unchecked((int)0x80431068),
        ERR_BAD_RESPONSE = unchecked((int)0x80431064), ERR_NO_CONTENT_LENGTH = unchecked((int)0x80431071),
        ERR_TOO_LARGE_RESPONSE_HEADER = unchecked((int)0x80431073), ERR_SSL = unchecked((int)0x80431075),
        ERR_UNKNOWN = unchecked((int)0x80431081);

    /// <summary>SCE_HTTPS_ERROR_CERT and the SCE_HTTPS_ERROR_SSL_* detail bits of sceHttpsGetSslError.</summary>
    const int HTTPS_ERROR_CERT = unchecked((int)0x80435060);
    const uint SSL_ERR_INTERNAL = 0x01, SSL_ERR_INVALID_CERT = 0x02, SSL_ERR_CN_CHECK = 0x04, SSL_ERR_NOT_AFTER = 0x08,
        SSL_ERR_NOT_BEFORE = 0x10, SSL_ERR_UNKNOWN_CA = 0x20;

    const uint SslServerVerify = 0x01, SslCnCheck = 0x04, SslNotAfter = 0x08, SslNotBefore = 0x10, SslKnownCa = 0x20;
    const uint SslDefaultFlags = SslServerVerify | SslCnCheck | SslNotAfter | SslNotBefore | SslKnownCa;

    /// <summary>SCE_HTTP_NB_EVENT_* (PS4 values; the Vita library shares them).</summary>
    const uint NB_EVENT_IN = 0x1, NB_EVENT_SOCK_ERR = 0x8, NB_EVENT_RESOLVER_ERR = 0x20000;

    // errno values (low byte of SCE_NET_ERROR_*) reported by sceHttpGetLastErrno
    const int ErrnoTimedOut = 60, ErrnoConnRefused = 61, ErrnoConnReset = 54, ErrnoHostUnreach = 65, ErrnoNoHost = 0xE6;

    internal NetConfig? ConfigOverride;
    NetConfig Cfg => ConfigOverride ?? NetConfig.Current;

    /// <summary>One request/response over the host network.</summary>
    internal sealed class Exchange
    {
        public readonly CancellationTokenSource Cts = new();
        public bool HeadersDone, BodyDone, EventPending, Head;
        /// <summary>SCE error of the exchange (0 = fine).</summary>
        public int Error;
        public int StatusCode;
        public long? ContentLength;
        public byte[] HeaderBlob = [];
        public readonly Queue<byte[]> Chunks = new();
        public int ChunkOffset;
        public int SslError;
        public uint SslDetail;
        public void Cancel() { try { Cts.Cancel(); } catch (ObjectDisposedException) { } }
    }

    /// <summary>What the exchange needs, captured from the guest objects when the request is sent.</summary>
    sealed record Plan(string Url, string Method, Version Version, List<(string Name, string Value)> Headers, byte[]? Body,
        bool AutoRedirect, bool Cookies, bool AcceptGzip, bool Inflate, uint SslFlags, uint ResponseHeaderMax,
        TimeSpan ConnectTimeout, TimeSpan RecvTimeout);

    HttpMessageInvoker? _client;

    HttpMessageInvoker Client => _client ??= new HttpMessageInvoker(new HttpClientHandler
    {
        AllowAutoRedirect = false, // the library follows redirects itself (each hop is checked by NetConfig)
        UseCookies = false,        // SceHttp's own cookie jar
        AutomaticDecompression = DecompressionMethods.None,
        ServerCertificateCustomValidationCallback = ValidateCertificate,
        CheckCertificateRevocationList = false,
    });

    static readonly HttpRequestOptionsKey<(Http Owner, Exchange Ex, uint Flags)> SslKey = new("vitasharp.ssl");

    /// <summary>HTTPS server checks following the SCE_HTTPS_FLAG_* of the request: without SERVER_VERIFY anything is
    /// accepted; otherwise the host's trust store, or the CA certificates the game loaded with sceHttpsLoadCert, must
    /// validate the chain, with the CN and validity checks the flags keep.</summary>
    static bool ValidateCertificate(HttpRequestMessage req, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (!req.Options.TryGetValue(SslKey, out var ctx)) return errors == SslPolicyErrors.None;
        uint flags = ctx.Flags;
        if ((flags & SslServerVerify) == 0 || errors == SslPolicyErrors.None) return true;
        uint detail = 0;
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 && (flags & SslCnCheck) != 0) detail |= SSL_ERR_CN_CHECK;
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) detail |= SSL_ERR_INVALID_CERT;
        if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0 && cert != null)
        {
            // retry the chain against the game's own CA certificates, ignoring the checks the flags turned off
            using var custom = new X509Chain();
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            List<byte[]> certs;
            lock (ctx.Owner._lock) certs = [.. ctx.Owner._certs];
            foreach (var c in certs)
                try { custom.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(c)); }
                catch (System.Security.Cryptography.CryptographicException) { }
            if (custom.ChainPolicy.CustomTrustStore.Count > 0) custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            if ((flags & (SslNotAfter | SslNotBefore)) != (SslNotAfter | SslNotBefore)) custom.ChainPolicy.VerificationFlags |= X509VerificationFlags.IgnoreNotTimeValid;
            if ((flags & SslKnownCa) == 0) custom.ChainPolicy.VerificationFlags |= X509VerificationFlags.AllowUnknownCertificateAuthority;
            if (!custom.Build(cert))
            {
                foreach (var st in custom.ChainStatus)
                    detail |= st.Status switch
                    {
                        X509ChainStatusFlags.NotTimeValid => DateTime.Now > cert.NotAfter ? SSL_ERR_NOT_AFTER : SSL_ERR_NOT_BEFORE,
                        X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain => SSL_ERR_UNKNOWN_CA,
                        _ => SSL_ERR_INVALID_CERT,
                    };
                if (detail == 0) detail = SSL_ERR_INTERNAL;
            }
        }
        if (detail == 0) return true;
        lock (ctx.Owner._lock) { ctx.Ex.SslError = HTTPS_ERROR_CERT; ctx.Ex.SslDetail = detail; }
        Log.Warn($"SceHttp: TLS certificate of {req.RequestUri?.Host} rejected (SCE_HTTPS_ERROR_SSL detail 0x{detail:X})");
        return false;
    }

    /// <summary>Headers added to the template, then the connection, then the request (later ones win for overwrites).</summary>
    List<(string Name, string Value)> EffectiveHeaders(Obj req)
    {
        var chain = new List<Obj>();
        for (Obj? o = req; o != null; o = o.ParentId != 0 ? _objs.GetValueOrDefault(o.ParentId) : null) chain.Insert(0, o);
        var list = new List<(string, string)>();
        foreach (var o in chain)
            foreach (var h in o.Headers)
            {
                list.RemoveAll(x => x.Item1.Equals(h.Name, StringComparison.OrdinalIgnoreCase) && !h.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
                list.Add(h);
            }
        return list;
    }

    static string MethodName(int m) => m is >= 0 and < 8 ? Methods[m] : "GET";

    int RealSendRequest(Obj o, uint postData, uint size)
    {
        if (o.Ex is { } ex)
        {
            // a non-blocking request polled again, or a blocking one sent twice
            if (!ex.HeadersDone)
            {
                if (o.S.Nonblock != 0) return ERR_EAGAIN;
                return WaitHeaders(o, ex);
            }
            return ex.Error != 0 ? ex.Error : 0;
        }
        var cfg = Cfg;
        var parts = HttpUri.Split(o.Url);
        string host = parts.Host ?? "";
        o.Sent = true;
        if (!cfg.Permits(host, out string why))
        {
            cfg.LogBlocked($"HTTP request {o.Url}", why);
            o.LastErrno = ErrnoNoDns;
            return ERR_NETWORK;
        }
        byte[]? body = null;
        if (postData != 0 && size > 0) body = GuestMemory.Span(postData, (int)Math.Min(size, 64u << 20)).ToArray();
        var headers = EffectiveHeaders(o);
        string url = o.Url;
        var plan = new Plan(url, MethodName(o.Method), o.HttpVer == 1 ? HttpVersion.Version10 : HttpVersion.Version11, headers, body,
            o.S.AutoRedirect != 0, o.S.CookieEnabled != 0, o.S.AcceptGzip != 0, o.S.InflateGzip != 0, o.S.SslFlags,
            o.S.ResponseHeaderMax == 0 ? 5000 : o.S.ResponseHeaderMax,
            TimeSpan.FromMicroseconds(Math.Max(o.S.ConnectTimeoutUs, 1_000_000u) + Math.Max(o.S.SendTimeoutUs, 1_000_000u)),
            TimeSpan.FromMicroseconds(Math.Max(o.S.RecvTimeoutUs, 1_000_000u)));
        ex = o.Ex = new Exchange { Head = plan.Method == "HEAD" };
        if (!string.IsNullOrEmpty(o.UserAgent) && !headers.Any(h => h.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
            headers.Insert(0, ("User-Agent", o.UserAgent));
        cfg.LogContact($"HTTP {plan.Method} {Redact(url)}");
        var exRef = ex;
        int id = o.Id;
        _ = Task.Run(() => RunExchange(id, exRef, plan));
        if (o.S.Nonblock != 0) return ERR_EAGAIN;
        return WaitHeaders(o, ex);
    }

    /// <summary>The URL without its query (the query may carry tokens) for the log.</summary>
    static string Redact(string url)
    {
        int q = url.IndexOf('?');
        return q < 0 ? url : url[..q] + "?…";
    }

    int WaitHeaders(Obj o, Exchange ex)
    {
        Kernel.Wait.Until(_lock, () => ex.HeadersDone || o.Aborted || !_inited, null, what: "http send");
        if (o.Aborted || !_inited) return ERR_ABORTED;
        return ex.Error != 0 ? ex.Error : 0;
    }

    /// <summary>Host task: sends the request (following redirects), then streams the body into the exchange.</summary>
    async Task RunExchange(int id, Exchange ex, Plan plan)
    {
        var cfg = Cfg;
        HttpResponseMessage? resp = null;
        try
        {
            string url = plan.Url, method = plan.Method;
            byte[]? body = plan.Body;
            for (int hop = 0; ; hop++)
            {
                var req = new HttpRequestMessage(new HttpMethod(method), url) { Version = plan.Version, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                req.Options.Set(SslKey, (this, ex, plan.SslFlags));
                HttpContent? content = body != null ? new ByteArrayContent(body) : null;
                bool hasCookie = false;
                foreach (var (n, v) in plan.Headers)
                {
                    if (n.Equals("Host", StringComparison.OrdinalIgnoreCase) || n.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || n.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                        continue;
                    hasCookie |= n.Equals("Cookie", StringComparison.OrdinalIgnoreCase);
                    if (!req.Headers.TryAddWithoutValidation(n, v)) content?.Headers.TryAddWithoutValidation(n, v);
                }
                if (plan.AcceptGzip && !plan.Headers.Any(h => h.Name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
                    req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                if (plan.Cookies && !hasCookie)
                {
                    string? cookie;
                    lock (_lock) cookie = _cookies.Get(url, url.StartsWith("https:", StringComparison.OrdinalIgnoreCase), DateTime.UtcNow);
                    if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
                }
                req.Content = content;
                using var hcts = CancellationTokenSource.CreateLinkedTokenSource(ex.Cts.Token);
                hcts.CancelAfter(plan.ConnectTimeout + plan.RecvTimeout);
                resp = await Client.SendAsync(req, hcts.Token).ConfigureAwait(false);
                if (plan.Cookies && resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    lock (_lock)
                        foreach (var c in setCookies) _cookies.Add(url, c, DateTime.UtcNow);
                int sc = (int)resp.StatusCode;
                if (!plan.AutoRedirect || sc is not (301 or 302 or 303 or 307 or 308) || resp.Headers.Location == null || hop >= 10) break;
                var next = new Uri(new Uri(url), resp.Headers.Location);
                if (!cfg.Permits(next.Host, out string why))
                {
                    cfg.LogBlocked($"HTTP redirect to {Redact(next.ToString())}", why);
                    break; // the guest sees the redirect response itself
                }
                if (sc == 303 || (sc is 301 or 302 && method == "POST")) { method = "GET"; body = null; }
                resp.Dispose();
                resp = null;
                url = next.ToString();
                cfg.LogContact($"HTTP redirect to {Redact(url)}");
            }

            var blob = BuildHeaderBlob(resp);
            Stream stream = await resp.Content.ReadAsStreamAsync(ex.Cts.Token).ConfigureAwait(false);
            long? length = resp.Content.Headers.ContentLength;
            var enc = resp.Content.Headers.ContentEncoding;
            if (plan.Inflate && enc.Any(e => e.Equals("gzip", StringComparison.OrdinalIgnoreCase)))
            {
                stream = new GZipStream(stream, CompressionMode.Decompress);
                length = null;
            }
            else if (plan.Inflate && enc.Any(e => e.Equals("deflate", StringComparison.OrdinalIgnoreCase)))
            {
                stream = new ZLibStream(stream, CompressionMode.Decompress);
                length = null;
            }
            lock (_lock)
            {
                ex.StatusCode = (int)resp.StatusCode;
                ex.ContentLength = length;
                ex.HeaderBlob = blob;
                if (blob.Length > plan.ResponseHeaderMax) ex.Error = ERR_TOO_LARGE_RESPONSE_HEADER;
                ex.HeadersDone = true;
                Signal(id, ex, ex.Error != 0 ? NB_EVENT_SOCK_ERR : NB_EVENT_IN);
            }
            if (ex.Error != 0 || ex.Head) { lock (_lock) { ex.BodyDone = true; Monitor.PulseAll(_lock); } return; }

            var buf = new byte[16384];
            while (true)
            {
                using var rcts = CancellationTokenSource.CreateLinkedTokenSource(ex.Cts.Token);
                rcts.CancelAfter(plan.RecvTimeout);
                int n = await stream.ReadAsync(buf, rcts.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (n == 0) ex.BodyDone = true;
                    else ex.Chunks.Enqueue(buf.AsSpan(0, n).ToArray());
                    Signal(id, ex, NB_EVENT_IN);
                }
                if (n == 0) break;
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or SocketException)
        {
            lock (_lock)
            {
                bool aborted = ex.Cts.IsCancellationRequested;
                int err, errno;
                uint evt = NB_EVENT_SOCK_ERR;
                if (aborted) { err = ERR_ABORTED; errno = 0; }
                else if (ex.SslError != 0) { err = ERR_SSL; errno = 0; }
                else if (e is OperationCanceledException) { err = ERR_TIMEOUT; errno = ErrnoTimedOut; }
                else if (FindSocketError(e) is { } se)
                {
                    errno = se switch
                    {
                        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ErrnoNoHost,
                        SocketError.ConnectionRefused => ErrnoConnRefused,
                        SocketError.ConnectionReset => ErrnoConnReset,
                        SocketError.TimedOut => ErrnoTimedOut,
                        _ => ErrnoHostUnreach,
                    };
                    err = ERR_NETWORK;
                    if (errno == ErrnoNoHost) evt = NB_EVENT_RESOLVER_ERR;
                }
                else if (e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) { err = ERR_SSL; errno = 0; }
                else if (e is HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded }) { err = ERR_BAD_RESPONSE; errno = 0; }
                else { err = ex.HeadersDone ? ERR_NETWORK : ERR_UNKNOWN; errno = ErrnoConnReset; }
                if (_objs.TryGetValue(id, out var o) && errno != 0) o.LastErrno = errno;
                Log.Debug($"SceHttp: {Redact(plan.Url)} failed: {e.GetType().Name}: {e.Message}");
                ex.Error = err;
                ex.HeadersDone = true;
                ex.BodyDone = true;
                Signal(id, ex, evt);
            }
        }
        finally
        {
            resp?.Dispose();
        }
    }

    static SocketError? FindSocketError(Exception e)
    {
        for (Exception? x = e; x != null; x = x.InnerException)
            if (x is SocketException se) return se.SocketErrorCode;
        return null;
    }

    /// <summary>"HTTP/1.1 200 OK\r\n" + every response and content header + "\r\n", what sceHttpGetAllResponseHeaders returns.</summary>
    static byte[] BuildHeaderBlob(HttpResponseMessage resp)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/").Append(resp.Version.Major).Append('.').Append(resp.Version.Minor).Append(' ')
          .Append((int)resp.StatusCode).Append(' ').Append(resp.ReasonPhrase ?? "").Append("\r\n");
        foreach (var h in resp.Headers)
            foreach (var v in h.Value) sb.Append(h.Key).Append(": ").Append(v).Append("\r\n");
        foreach (var h in resp.Content.Headers)
            foreach (var v in h.Value) sb.Append(h.Key).Append(": ").Append(v).Append("\r\n");
        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Wakes waiters and queues one HTTP epoll event for the request (caller holds the lock).</summary>
    void Signal(int id, Exchange ex, uint evt)
    {
        if (_objs.TryGetValue(id, out var o) && o.Ex == ex && o.Epoll is { } ep && !ex.EventPending)
        {
            ex.EventPending = true;
            ep.Pending.Enqueue((evt, id, o.EpollArg));
        }
        Monitor.PulseAll(_lock);
    }

    /// <summary>Blocking: waits for <paramref name="ready"/>; non-blocking: EAGAIN. Returns 0 when ready.</summary>
    int AwaitStep(Obj o, Exchange ex, Func<bool> ready, string what)
    {
        if (ready()) return 0;
        if (o.S.Nonblock != 0) { ex.EventPending = false; return ERR_EAGAIN; }
        Kernel.Wait.Until(_lock, () => ready() || o.Aborted || !_inited, null, what: what);
        return o.Aborted || !_inited ? ERR_ABORTED : 0;
    }

    int RealStatusCode(Obj o, uint statusCode)
    {
        var ex = o.Ex!;
        int r = AwaitStep(o, ex, () => ex.HeadersDone, "http status");
        if (r < 0) return r;
        if (ex.Error != 0) return ex.Error;
        if (statusCode == 0) return ERR_INVALID_VALUE;
        GuestMemory.W32(statusCode, (uint)ex.StatusCode);
        return 0;
    }

    int RealContentLength(Obj o, uint contentLength)
    {
        var ex = o.Ex!;
        int r = AwaitStep(o, ex, () => ex.HeadersDone, "http content length");
        if (r < 0) return r;
        if (ex.Error != 0) return ex.Error;
        if (contentLength == 0) return ERR_INVALID_VALUE;
        if (ex.ContentLength is not { } len) return ERR_NO_CONTENT_LENGTH; // chunked (or inflated) body
        GuestMemory.Write(contentLength, (ulong)len);
        return 0;
    }

    int RealResponseHeaders(Obj o, uint header, uint headerSize)
    {
        var ex = o.Ex!;
        int r = AwaitStep(o, ex, () => ex.HeadersDone, "http headers");
        if (r < 0) return r;
        if (ex.HeaderBlob.Length == 0 && ex.Error != 0) return ex.Error;
        if (header == 0 || headerSize == 0) return ERR_INVALID_VALUE;
        uint need = (uint)ex.HeaderBlob.Length + 1;
        if (o.RespHeadersBuf == 0 || o.RespHeadersBufSize < need)
        {
            if (o.RespHeadersBuf != 0) Emu.Memory.Free(o.RespHeadersBuf);
            o.RespHeadersBufSize = Math.Max(need, 0x100);
            o.RespHeadersBuf = Emu.Memory.Alloc(o.RespHeadersBufSize, "http response headers", MemoryRegion.User, 16);
        }
        ex.HeaderBlob.CopyTo(GuestMemory.Span(o.RespHeadersBuf, ex.HeaderBlob.Length));
        GuestMemory.Write<byte>(o.RespHeadersBuf + (uint)ex.HeaderBlob.Length, 0);
        GuestMemory.W32(header, o.RespHeadersBuf);
        GuestMemory.W32(headerSize, (uint)ex.HeaderBlob.Length);
        return 0;
    }

    /// <summary>Copies buffered body bytes: the count, 0 at the end of the body, or the error.</summary>
    int RealReadData(Obj o, uint data, uint size)
    {
        var ex = o.Ex!;
        if (data == 0 && size != 0) return ERR_INVALID_VALUE;
        int r = AwaitStep(o, ex, () => ex.Chunks.Count > 0 || ex.BodyDone, "http read");
        if (r < 0) return r;
        if (ex.Chunks.Count == 0)
        {
            if (ex.Error != 0) return ex.Error;
            return 0;
        }
        int copied = 0;
        while (copied < size && ex.Chunks.TryPeek(out var c))
        {
            int n = Math.Min(c.Length - ex.ChunkOffset, (int)size - copied);
            c.AsSpan(ex.ChunkOffset, n).CopyTo(GuestMemory.Span(data + (uint)copied, n));
            copied += n;
            ex.ChunkOffset += n;
            if (ex.ChunkOffset == c.Length) { ex.Chunks.Dequeue(); ex.ChunkOffset = 0; }
        }
        // more data (or the end) is announced with a new epoll event once the guest drained this
        ex.EventPending = ex.Chunks.Count > 0;
        return copied;
    }
}
