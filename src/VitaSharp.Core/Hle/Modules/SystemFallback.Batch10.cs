using VitaSharp.Core.Memory;
using static VitaSharp.Core.Hle.ShellSvcIpc;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// System libraries (FW 3.74) the system software always loads from the firmware, written from each module's checks
/// for a process that runs without it: libSceTeleportClient (PS4 Remote Play discovery), libsulpha (the NGS/PC
/// profiling agent), libScePromoterUtil (package promotion, an IPC client of the shell) and libshellsvc's event-flag
/// helpers. They never take a library over from the firmware (<see cref="FirmwareFallbackOnly"/>).
/// </summary>
public sealed partial class SystemFallback : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    // ------------------------------------------------------------------ SceTeleportClient
    // The library's errors: 0x80108401 internal, 0x80108402 invalid argument, 0x80108403 not initialised, 0x8010840A
    // unsupported model. Its parameter blocks have reserved bytes that must be zero. Nothing is ever found on the
    // network (no PS4), so the operations on a device answer the library's internal error.
    const int TP_INTERNAL = unchecked((int)0x80108401), TP_INVALID = unchecked((int)0x80108402), TP_NOT_INIT = unchecked((int)0x80108403);
    bool _tpInit;

    static bool Zero(uint p, uint offset, uint count)
    {
        for (uint i = 0; i < count; i++) if (GuestMemory.Read<byte>(p + offset + i) != 0) return false;
        return true;
    }

    /// <summary>(const param *p): +0 and +4 must be set, +8..+0x3F reserved (zero).</summary>
    [Hle("sceTeleportClientInitialize", Library = "SceTeleportClient")]
    int TeleportInit(uint p)
    {
        if (p == 0 || GuestMemory.R32(p) == 0 || GuestMemory.R32(p + 4) == 0 || !Zero(p, 8, 0x38)) return TP_INVALID;
        _tpInit = true;
        return 0;
    }

    [Hle("sceTeleportClientFinalize", Library = "SceTeleportClient")] int TeleportTerm() { if (!_tpInit) return TP_NOT_INIT; _tpInit = false; return 0; }
    [Hle("sceTeleportClientStartSearchDevice", Library = "SceTeleportClient")] int TeleportStartSearch() => _tpInit ? 0 : TP_NOT_INIT;
    [Hle("sceTeleportClientEndSearchDevice", Library = "SceTeleportClient")] int TeleportEndSearch() => _tpInit ? 0 : TP_NOT_INIT;
    [Hle("sceTeleportClientClearCacheInfo", Library = "SceTeleportClient")] int TeleportClearCache() => _tpInit ? 0 : TP_NOT_INIT;
    [Hle("sceTeleportClientRegisterGetDeviceInfoCallback", Library = "SceTeleportClient")]
    int TeleportRegisterCallback(uint cb) => !_tpInit ? TP_NOT_INIT : cb == 0 ? TP_INVALID : 0;

    /// <summary>(info *i): +0x5A.. reserved (0x2A bytes); no device was ever cached.</summary>
    [Hle("sceTeleportClientGetCacheInfo", Library = "SceTeleportClient")]
    int TeleportGetCache(uint info) => !_tpInit ? TP_NOT_INIT : info == 0 || !Zero(info, 0x5A, 0x2A) ? TP_INVALID : TP_INTERNAL;

    /// <summary>(param *p): +0.. reserved (0x40 bytes); no device to wake.</summary>
    [Hle("sceTeleportClientWakeupLatestDevice", Library = "SceTeleportClient")]
    int TeleportWakeup(uint p) => !_tpInit ? TP_NOT_INIT : p == 0 || !Zero(p, 0, 0x40) ? TP_INVALID : TP_INTERNAL;

    /// <summary>(device *d, x *a, ...): the device (+0x26.. 0x16 reserved bytes) is not known.</summary>
    int TeleportOnDevice(uint device, uint a) => !_tpInit ? TP_NOT_INIT : device == 0 || a == 0 || !Zero(device, 0x26, 0x16) ? TP_INVALID : TP_INTERNAL;
    [Hle("sceTeleportClientGetRemoteAppInfoNum", Library = "SceTeleportClient")] int TeleportAppNum(uint device, uint a, uint b) => TeleportOnDevice(device, a);
    [Hle("sceTeleportClientGetDeviceDescription", Library = "SceTeleportClient")] int TeleportDescription(uint device, uint a, uint b) => TeleportOnDevice(device, a);
    [Hle("sceTeleportClientGetRemoteAppInfo", Library = "SceTeleportClient")] int TeleportAppInfo(uint device, uint index, uint a) => TeleportOnDevice(device, a == 0 ? 1u : a);
    [Hle("sceTeleportClientDeleteRemoteApp", Library = "SceTeleportClient")] int TeleportDeleteApp(uint device, uint a, uint b) => TeleportOnDevice(device, a);
    [Hle("sceTeleportClientLaunchRemoteApp", Library = "SceTeleportClient")]
    int TeleportLaunch(uint device, uint a, uint b, uint c) => !_tpInit ? TP_NOT_INIT : device == 0 || a == 0 || b == 0 || c == 0 ? TP_INVALID : TP_INTERNAL;

    // ------------------------------------------------------------------ SceSulpha (libsulpha)
    // 0x804D0004 for a missing argument, 0x804D0005 for a node/event that is not this session's. No PC tool is ever
    // connected and no node or event is ever created here.
    const int SULPHA_INVALID = unchecked((int)0x804D0004), SULPHA_BAD_HANDLE = unchecked((int)0x804D0005);
    int _sulphaLevel;
    internal int SulphaLogLevel => _sulphaLevel;

    /// <summary>(config *c): { 0, 0x115C, 0x80 }.</summary>
    [Hle("sceSulphaGetDefaultConfig", Library = "SceSulpha")]
    int SulphaDefaultConfig(uint c)
    {
        if (c == 0) return SULPHA_INVALID;
        GuestMemory.W32(c, 0); GuestMemory.W32(c + 4, 0x115C); GuestMemory.W32(c + 8, 0x80);
        return 0;
    }

    /// <summary>(int level 0..4): the log level (others are ignored).</summary>
    [Hle("sceSulphaLogSetLevel", Library = "SceSulpha")] void SulphaSetLogLevel(int level) { if ((uint)level <= 4) _sulphaLevel = level; }
    [Hle("sceSulphaInit", Library = "SceSulpha")] int SulphaInit(uint config, uint buf, uint size) => config == 0 || buf == 0 || size == 0 ? SULPHA_INVALID : 0;
    [Hle("sceSulphaShutdown", Library = "SceSulpha")] int SulphaShutdown() => 0;
    [Hle("sceSulphaUpdate", Library = "SceSulpha")] int SulphaUpdate() => 0;
    [Hle("sceSulphaNodeDelete", Library = "SceSulpha")] int SulphaNodeDelete(uint node, uint a) => SULPHA_BAD_HANDLE;
    [Hle("sceSulphaEventAdd", Library = "SceSulpha")] int SulphaEventAdd(uint ev, uint a, uint b, uint c) => SULPHA_BAD_HANDLE;
    [Hle("sceSulphaEventReport", Library = "SceSulpha")] int SulphaEventReport(uint ev, uint a) => SULPHA_BAD_HANDLE;
    [Hle("sceSulphaEventDelete", Library = "SceSulpha")] int SulphaEventDelete(uint ev) => SULPHA_BAD_HANDLE;

    // ------------------------------------------------------------------ ScePromoterUtil (libScePromoterUtil)
    // Each call checks its pointers (0x80AF008F) and asks the shell's service (sceShellSvcGetSvcObj, synchronous method
    // 0x13xxxx); the same request goes to the shell here (ShellSvcIpc), or fails as not available without one.
    const int PROMOTER_INVALID = unchecked((int)0x80AF008F);
    int Promoter(uint method, ReadOnlySpan<Arg> ins, ReadOnlySpan<Arg> outs) => Invoke(Emu, method, ins, outs, PROMOTER_INVALID);
    static Arg Str(uint s) => Buf(s, (uint)GuestMemory.ReadCString(s).Length + 1);

    [Hle("scePromoterUtilityInit", Library = "ScePromoterUtil")] int PromoterInit() { Promoter(0x13000F, [], []); return 0; }
    [Hle("scePromoterUtilityExit", Library = "ScePromoterUtil")] int PromoterExit() { Promoter(0x130010, [], []); return 0; }
    [Hle("scePromoterUtilityGetState", Library = "ScePromoterUtil")] int PromoterState(uint p) => p == 0 ? PROMOTER_INVALID : Promoter(0x130003, [], [Buf(p, 4)]);
    [Hle("scePromoterUtilityGetResult", Library = "ScePromoterUtil")] int PromoterResult(uint p) => p == 0 ? PROMOTER_INVALID : Promoter(0x130004, [], [Buf(p, 4)]);
    [Hle("scePromoterUtility_395739DF", Nid = 0x395739DF, Library = "ScePromoterUtil")] int Promoter130008(uint p) => p == 0 ? PROMOTER_INVALID : Promoter(0x130008, [], [Buf(p, 4)]);
    [Hle("scePromoterUtility_59A6CDAE", Nid = 0x59A6CDAE, Library = "ScePromoterUtil")] int Promoter130011(uint p) => p == 0 ? PROMOTER_INVALID : Promoter(0x130011, [], [Buf(p, 4)]);
    /// <summary>(const char *titleId, int *exists).</summary>
    [Hle("scePromoterUtilityCheckExist", Library = "ScePromoterUtil")]
    int PromoterCheckExist(uint titleId, uint exists) => titleId == 0 || exists == 0 ? PROMOTER_INVALID : Promoter(0x130013, [Str(titleId)], [Buf(exists, 4)]);
    /// <summary>(const void *param 0xB0).</summary>
    [Hle("scePromoterUtility_8494C742", Nid = 0x8494C742, Library = "ScePromoterUtil")]
    int Promoter13000D(uint p) => p == 0 ? PROMOTER_INVALID : Promoter(0x13000D, [Buf(p, 0xB0)], []);

    // ------------------------------------------------------------------ SceShellSvc (libshellsvc): event flags
    // An event number (&lt; 0x80) is a bit of the shell's event flags: word number / 32 + 4, bit number % 32. Without
    // the shell there is no service object and no handler is ever registered.
    [Hle("sceShellSvcGetSvcObj", Library = "SceShellSvc")] uint ShellSvcGetSvcObj() => 0;
    [Hle("sceShellSvc_7CBF442B", Nid = 0x7CBF442B, Library = "SceShellSvc")] int ShellSvcUnregisterEvent(int ev) => (uint)ev < 0x80 ? 0 : unchecked((int)0x80100910);
    [Hle("sceShellSvc_FB03FBC9", Nid = 0xFB03FBC9, Library = "SceShellSvc")] int ShellSvcEventPending(int ev) => unchecked((int)0x80100962);
    /// <summary>(uint out[2], int event): the flag word and bit of an event.</summary>
    [Hle("sceShellSvc_A0B067AC", Nid = 0xA0B067AC, Library = "SceShellSvc")]
    int ShellSvcEventBit(uint outp, int ev)
    {
        if ((uint)ev >= 0x80) return unchecked((int)0x80100970);
        GuestMemory.W32(outp, (uint)(ev / 32 + 4));
        GuestMemory.W32(outp + 4, 1u << (ev % 32));
        return 0;
    }
}
