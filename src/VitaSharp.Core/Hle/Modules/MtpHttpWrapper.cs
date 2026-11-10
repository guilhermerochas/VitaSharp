namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibMtpHttpWrapper (vs0:vsh/common/libmtphttp_wrapper.suprx): one HTTP API for the system programs that may
/// download either over the network or over the PC's link. sceMtpHttpWrapperLoadStartModules(mode) picks the
/// libraries: 1 HTTP (sysmodule 2), 2 HTTPS (sysmodule 4: SSL and HTTP), 3 HTTP over MTP and 4 HTTPS over MTP (both
/// vs0:vsh/common/libmtphttp.suprx, whose client is started); every other export passes its arguments unchanged to
/// the chosen library's function (libhttp's own prototypes), or fails with "not started" before. The names are the
/// module's own (its log strings).
/// A system program keeps the firmware's library.
/// </summary>
public sealed partial class MtpHttpWrapper : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    const string Lib = "SceLibMtpHttpWrapper";
    const int NOT_STARTED = unchecked((int)0x80546500), ALREADY_STARTED = unchecked((int)0x80546501),
        INVALID_MODE = unchecked((int)0x80546502), LOAD_FAILED = unchecked((int)0x80546503);
    const uint SysmoduleHttp = 2, SysmoduleHttps = 4;

    // the libssl functions (Init, Term, GetMemoryPoolStats) and the libhttp ones, in the wrapper's table order
    static readonly uint[] SslNet = [0x3C733316, 0x03CE6E3A, 0xBD203262];
    static readonly uint[] SslMtp = [0xD063DA59, 0xD35D8424, 0x3E17312F];
    static readonly uint[] HttpNet =
    [
        0x214926D9, 0xC9076666, 0xF98CDFA9, 0x62241DAB, 0xEC85ECFB, 0xC616C200, 0xF0F65C15, 0xBD5DA1D0, 0x3D3D29AD,
        0x9CA58B99, 0xEA61662F, 0xF580D304, 0x27071691, 0x7EDE3979, 0x7B51B122, 0x5EB5F548, 0xE0A3A88D, 0x237CA86E,
        0x8AE3F008, 0x94BF196E, 0x72CB0741, 0x00659635, 0x53B722F2,
    ];
    static readonly uint[] HttpMtp =
    [
        0xD82F7256, 0xDF9A48DA, 0x5B31E373, 0x505331F6, 0x314CE923, 0x20FB4AF7, 0x54F6A581, 0x6DB1F55B, 0x72684CF8,
        0xD2904E5A, 0x1D6F97E9, 0x7BA117AA, 0xE6895A35, 0x601596DC, 0x0E6897BC, 0x6A8BF9D5, 0x94B3A908, 0xF42C62FF,
        0xCE45213C, 0x76947F49, 0x04A1183A, 0xA6A60DCF, 0x8DF9F8BF,
    ];

    readonly object _lock = new();
    uint[]? _ssl, _http;
    int _mode;

    SystemMisc Sys => Emu.Hle.Modules.OfType<SystemMisc>().First();
    MtpHttp Mtp => Emu.Hle.Modules.OfType<MtpHttp>().First();

    /// <summary>(SceUInt16 mode): loads and starts the libraries of <paramref name="mode"/> (1-4).</summary>
    [Hle("sceMtpHttpWrapperLoadStartModules", Nid = 0x2B7DDF1D, Library = Lib)]
    int sceMtpHttpWrapperLoadStartModules(ushort mode)
    {
        lock (_lock)
        {
            if (_ssl != null || _http != null) return ALREADY_STARTED;
            switch (mode)
            {
                case 1:
                    if (Sys.LoadSysmoduleFor(SysmoduleHttp) < 0) return LOAD_FAILED;
                    _http = HttpNet;
                    break;
                case 2:
                    if (Sys.LoadSysmoduleFor(SysmoduleHttps) < 0) return LOAD_FAILED;
                    _ssl = SslNet; _http = HttpNet;
                    break;
                case 3:
                case 4:
                    Mtp.StartClient();
                    if (mode == 4) _ssl = SslMtp;
                    _http = HttpMtp;
                    break;
                default:
                    return INVALID_MODE;
            }
            _mode = mode;
            return 0;
        }
    }

    /// <summary>(void): stops and unloads what LoadStartModules loaded (the unload's result is returned).</summary>
    [Hle("sceMtpHttpWrapperStopUnloadModules", Nid = 0x2054E940, Library = Lib)]
    int sceMtpHttpWrapperStopUnloadModules()
    {
        lock (_lock)
        {
            if (_ssl == null && _http == null) return NOT_STARTED;
            int r = _mode switch
            {
                1 => Sys.UnloadSysmoduleFor(SysmoduleHttp),
                2 => Sys.UnloadSysmoduleFor(SysmoduleHttps),
                _ => StopMtp(), // libmtphttp's own stop result is not checked; the module unload's is returned
            };
            _ssl = _http = null;
            _mode = 0;
            return r;
        }
    }

    int StopMtp() { Mtp.StopClient(); return 0; }

    void Ssl(HleContext c, int i) => Forward(c, _ssl, i);
    void Http(HleContext c, int i) => Forward(c, _http, i);

    void Forward(HleContext c, uint[]? table, int i)
    {
        if (table == null) { c.Return(NOT_STARTED); return; }
        var f = Emu.Hle.ImplOf(table[i]);
        if (f != null) f(c);
        else c.Return(NOT_STARTED);
    }

    [Hle("sceMtpSslWrapperInit", Nid = 0x7B4C41AA, Library = Lib)] void sceMtpSslWrapperInit(HleContext c) => Ssl(c, 0);
    [Hle("sceMtpSslWrapperTerm", Nid = 0xCD375BDC, Library = Lib)] void sceMtpSslWrapperTerm(HleContext c) => Ssl(c, 1);
    [Hle("sceMtpSslWrapperGetMemoryPoolStats", Nid = 0xD1748150, Library = Lib)] void sceMtpSslWrapperGetMemoryPoolStats(HleContext c) => Ssl(c, 2);

    [Hle("sceMtpHttpWrapperInit", Nid = 0x591AE2E0, Library = Lib)] void sceMtpHttpWrapperInit(HleContext c) => Http(c, 0);
    [Hle("sceMtpHttpWrapperTerm", Nid = 0x498DBA74, Library = Lib)] void sceMtpHttpWrapperTerm(HleContext c) => Http(c, 1);
    [Hle("sceMtpHttpWrapperGetMemoryPoolStats", Nid = 0xB0C88BB8, Library = Lib)] void sceMtpHttpWrapperGetMemoryPoolStats(HleContext c) => Http(c, 2);
    [Hle("sceMtpHttpWrapperCreateTemplate", Nid = 0x08087DF4, Library = Lib)] void sceMtpHttpWrapperCreateTemplate(HleContext c) => Http(c, 3);
    [Hle("sceMtpHttpWrapperDeleteTemplate", Nid = 0x37687260, Library = Lib)] void sceMtpHttpWrapperDeleteTemplate(HleContext c) => Http(c, 4);
    [Hle("sceMtpHttpWrapperCreateConnectionWithURL", Nid = 0x40C02EE4, Library = Lib)] void sceMtpHttpWrapperCreateConnectionWithURL(HleContext c) => Http(c, 5);
    [Hle("sceMtpHttpWrapperDeleteConnection", Nid = 0x22694C99, Library = Lib)] void sceMtpHttpWrapperDeleteConnection(HleContext c) => Http(c, 6);
    [Hle("sceMtpHttpWrapperCreateRequestWithURL", Nid = 0x6690720E, Library = Lib)] void sceMtpHttpWrapperCreateRequestWithURL(HleContext c) => Http(c, 7);
    [Hle("sceMtpHttpWrapperDeleteRequest", Nid = 0x8F78F81F, Library = Lib)] void sceMtpHttpWrapperDeleteRequest(HleContext c) => Http(c, 8);
    [Hle("sceMtpHttpWrapperSendRequest", Nid = 0xA90518BF, Library = Lib)] void sceMtpHttpWrapperSendRequest(HleContext c) => Http(c, 9);
    [Hle("sceMtpHttpWrapperAbortRequest", Nid = 0x456A878F, Library = Lib)] void sceMtpHttpWrapperAbortRequest(HleContext c) => Http(c, 10);
    [Hle("sceMtpHttpWrapperGetResponseContentLength", Nid = 0xAF3AA459, Library = Lib)] void sceMtpHttpWrapperGetResponseContentLength(HleContext c) => Http(c, 11);
    [Hle("sceMtpHttpWrapperGetStatusCode", Nid = 0x511EC73A, Library = Lib)] void sceMtpHttpWrapperGetStatusCode(HleContext c) => Http(c, 12);
    [Hle("sceMtpHttpWrapperReadData", Nid = 0xAEAFFD82, Library = Lib)] void sceMtpHttpWrapperReadData(HleContext c) => Http(c, 13);
    [Hle("sceMtpHttpWrapperAddRequestHeader", Nid = 0x0B4CFA83, Library = Lib)] void sceMtpHttpWrapperAddRequestHeader(HleContext c) => Http(c, 14);
    [Hle("sceMtpHttpWrapperRemoveRequestHeader", Nid = 0xED615FAE, Library = Lib)] void sceMtpHttpWrapperRemoveRequestHeader(HleContext c) => Http(c, 15);
    [Hle("sceMtpHttpWrapperSetAuthInfoCallback", Nid = 0x7A309C95, Library = Lib)] void sceMtpHttpWrapperSetAuthInfoCallback(HleContext c) => Http(c, 16);
    [Hle("sceMtpHttpWrapperSetConnectTimeOut", Nid = 0xAEE728EA, Library = Lib)] void sceMtpHttpWrapperSetConnectTimeOut(HleContext c) => Http(c, 17);
    [Hle("sceMtpHttpWrapperSetSendTimeOut", Nid = 0x982867D8, Library = Lib)] void sceMtpHttpWrapperSetSendTimeOut(HleContext c) => Http(c, 18);
    [Hle("sceMtpHttpWrapperSetRecvTimeOut", Nid = 0x3E7BB77D, Library = Lib)] void sceMtpHttpWrapperSetRecvTimeOut(HleContext c) => Http(c, 19);
    [Hle("sceMtpHttpsWrapperEnableOptionPrivate", Nid = 0x4039717B, Library = Lib)] void sceMtpHttpsWrapperEnableOptionPrivate(HleContext c) => Http(c, 20);
    [Hle("sceMtpHttpsWrapperDisableOptionPrivate", Nid = 0xEEB4F73D, Library = Lib)] void sceMtpHttpsWrapperDisableOptionPrivate(HleContext c) => Http(c, 21);
    [Hle("sceMtpHttpWrapperSetProxy", Nid = 0xD5B490C5, Library = Lib)] void sceMtpHttpWrapperSetProxy(HleContext c) => Http(c, 22);
}
