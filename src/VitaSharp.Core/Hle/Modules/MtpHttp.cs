namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibMtpHttp (vs0:vsh/common/libmtphttp.suprx): libhttp and libssl built a second time over the MTP host link,
/// so a system program can reach the internet through the PC that Content Manager runs on (system update and
/// license downloads over USB). Its exports have no public names; each is the libhttp/libssl function at the same
/// place of libmtphttp_wrapper's function tables (the wrapper's own strings name them), with the same arguments,
/// checks and error codes (0x8043xxxx, 0x8043 5xxx for SSL). Its state is its own: it has its own init flag, pool and
/// objects, separate from SceHttp's.
/// The HLE runs these on a private, offline instance of <see cref="Http"/> and <see cref="Ssl"/>: there is never an
/// MTP host (see <see cref="MtpIf"/>), so every send fails like an unreachable server while templates, connections,
/// requests, headers and settings behave as in the library. Start/Stop (the MTP client the module keeps for its
/// sockets) are the two exports the wrapper calls after loading and before unloading the module.
/// A system program keeps the firmware's library.
/// </summary>
public sealed partial class MtpHttp : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    const string Lib = "SceLibMtpHttp";
    const int MTP_CLIENT_ALREADY_STARTED = unchecked((int)0x80410110), MTP_CLIENT_NOT_STARTED = unchecked((int)0x804101C8);
    const int HTTP_ERROR_BEFORE_INIT = unchecked((int)0x80431001);

    HleInstance<Http>? _http;
    HleInstance<Ssl>? _ssl;
    bool _clientStarted;

    HleInstance<Http> H => _http ??= new HleInstance<Http>(Emu, h => h.ConfigOverride = NetConfig.Off);
    HleInstance<Ssl> S => _ssl ??= new HleInstance<Ssl>(Emu);

    internal int StartClient() => sceMtpHttpStart();
    internal int StopClient() => sceMtpHttpStop();

    /// <summary>(void): creates the module's MTP client (the IPC link to the shell's SceMtpService its sockets use).</summary>
    [Hle("sceMtpHttpStart", Nid = 0x8D80C874, Library = Lib)]
    int sceMtpHttpStart()
    {
        lock (this)
        {
            if (_clientStarted) return MTP_CLIENT_ALREADY_STARTED;
            _clientStarted = true;
            return 0;
        }
    }

    /// <summary>(void): disconnects and frees the MTP client.</summary>
    [Hle("sceMtpHttpStop", Nid = 0xD92810E0, Library = Lib)]
    int sceMtpHttpStop()
    {
        lock (this)
        {
            if (!_clientStarted) return MTP_CLIENT_NOT_STARTED;
            _clientStarted = false;
            return 0;
        }
    }

    // ---- libssl over MTP: (SceSize poolSize), (void), (SceSslMemoryPoolStats *)
    [Hle("sceMtpSslInit", Nid = 0xD063DA59, Library = Lib)] void sceMtpSslInit(HleContext c) => S.Call(c, 0x3C733316, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpSslTerm", Nid = 0xD35D8424, Library = Lib)] void sceMtpSslTerm(HleContext c) => S.Call(c, 0x03CE6E3A, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpSslGetMemoryPoolStats", Nid = 0x3E17312F, Library = Lib)] void sceMtpSslGetMemoryPoolStats(HleContext c) => S.Call(c, 0xBD203262, HTTP_ERROR_BEFORE_INIT);

    // ---- libhttp over MTP, in the order of the wrapper's table (same prototypes as the SceHttp functions)
    [Hle("sceMtpHttpInit", Nid = 0xD82F7256, Library = Lib)] void sceMtpHttpInit(HleContext c) => H.Call(c, 0x214926D9, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpTerm", Nid = 0xDF9A48DA, Library = Lib)] void sceMtpHttpTerm(HleContext c) => H.Call(c, 0xC9076666, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpGetMemoryPoolStats", Nid = 0x5B31E373, Library = Lib)] void sceMtpHttpGetMemoryPoolStats(HleContext c) => H.Call(c, 0xF98CDFA9, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpCreateTemplate", Nid = 0x505331F6, Library = Lib)] void sceMtpHttpCreateTemplate(HleContext c) => H.Call(c, 0x62241DAB, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpDeleteTemplate", Nid = 0x314CE923, Library = Lib)] void sceMtpHttpDeleteTemplate(HleContext c) => H.Call(c, 0xEC85ECFB, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpCreateConnectionWithURL", Nid = 0x20FB4AF7, Library = Lib)] void sceMtpHttpCreateConnectionWithURL(HleContext c) => H.Call(c, 0xC616C200, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpDeleteConnection", Nid = 0x54F6A581, Library = Lib)] void sceMtpHttpDeleteConnection(HleContext c) => H.Call(c, 0xF0F65C15, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpCreateRequestWithURL", Nid = 0x6DB1F55B, Library = Lib)] void sceMtpHttpCreateRequestWithURL(HleContext c) => H.Call(c, 0xBD5DA1D0, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpDeleteRequest", Nid = 0x72684CF8, Library = Lib)] void sceMtpHttpDeleteRequest(HleContext c) => H.Call(c, 0x3D3D29AD, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpSendRequest", Nid = 0xD2904E5A, Library = Lib)] void sceMtpHttpSendRequest(HleContext c) => H.Call(c, 0x9CA58B99, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpAbortRequest", Nid = 0x1D6F97E9, Library = Lib)] void sceMtpHttpAbortRequest(HleContext c) => H.Call(c, 0xEA61662F, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpGetResponseContentLength", Nid = 0x7BA117AA, Library = Lib)] void sceMtpHttpGetResponseContentLength(HleContext c) => H.Call(c, 0xF580D304, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpGetStatusCode", Nid = 0xE6895A35, Library = Lib)] void sceMtpHttpGetStatusCode(HleContext c) => H.Call(c, 0x27071691, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpReadData", Nid = 0x601596DC, Library = Lib)] void sceMtpHttpReadData(HleContext c) => H.Call(c, 0x7EDE3979, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpAddRequestHeader", Nid = 0x0E6897BC, Library = Lib)] void sceMtpHttpAddRequestHeader(HleContext c) => H.Call(c, 0x7B51B122, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpRemoveRequestHeader", Nid = 0x6A8BF9D5, Library = Lib)] void sceMtpHttpRemoveRequestHeader(HleContext c) => H.Call(c, 0x5EB5F548, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpSetAuthInfoCallback", Nid = 0x94B3A908, Library = Lib)] void sceMtpHttpSetAuthInfoCallback(HleContext c) => H.Call(c, 0xE0A3A88D, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpSetConnectTimeOut", Nid = 0xF42C62FF, Library = Lib)] void sceMtpHttpSetConnectTimeOut(HleContext c) => H.Call(c, 0x237CA86E, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpSetSendTimeOut", Nid = 0xCE45213C, Library = Lib)] void sceMtpHttpSetSendTimeOut(HleContext c) => H.Call(c, 0x8AE3F008, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpSetRecvTimeOut", Nid = 0x76947F49, Library = Lib)] void sceMtpHttpSetRecvTimeOut(HleContext c) => H.Call(c, 0x94BF196E, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpsEnableOptionPrivate", Nid = 0x04A1183A, Library = Lib)] void sceMtpHttpsEnableOptionPrivate(HleContext c) => H.Call(c, 0x72CB0741, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpsDisableOptionPrivate", Nid = 0xA6A60DCF, Library = Lib)] void sceMtpHttpsDisableOptionPrivate(HleContext c) => H.Call(c, 0x00659635, HTTP_ERROR_BEFORE_INIT);
    [Hle("sceMtpHttpSetProxy", Nid = 0x8DF9F8BF, Library = Lib)] void sceMtpHttpSetProxy(HleContext c) => H.Call(c, 0x53B722F2, HTTP_ERROR_BEFORE_INIT);
}
