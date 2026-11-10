using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibMtp (vs0:vsh/common/libmtp.suprx): the shell's MTP stack (Content Manager), both the MTP device the PC talks
/// to over USB or Wi-Fi and the "Begin/End" operations the shell and the system applications run on the PC (copy,
/// import/export, PC folders, NP DRM and Marlin on the PC's behalf, HTTP through the PC, background copy).
/// Behaviour read in the module (FW 3.74); the names are the module's own log strings where it has them.
/// <para>The HLE models the library as on a PS Vita that no PC is connected to (the USB side, <see cref="MtpIf"/>,
/// never sees a cable, and no Wi-Fi PC is registered or discovered): the core goes through its states
/// (Init → Start → Stop → End, with the library's checks and error codes), the device reports "disconnected" once
/// started, and every operation that needs the host stops at the library's connection check with the error the
/// library returns for its state (0x8054001B before the device has run, 0x80540034 disconnected). Context ids
/// therefore never exist: the End/Get calls on an id fail like the library's context lookup. The pieces the library
/// keeps locally work (settings blocks, callbacks, the large transfer buffer, the interface mode, the device id, the
/// list of registered PCs, the background copy callbacks).</para>
/// A system program keeps the firmware's library.
/// </summary>
public sealed partial class Mtp : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    const string Lib = "SceLibMtp";

    // SCE_MTP_ERROR_*: 0x8054_00xx
    const int ERROR = unchecked((int)0x80540001), ALREADY = unchecked((int)0x80540002), INVALID_ARG = unchecked((int)0x80540003),
        NOT_STOPPED = unchecked((int)0x80540004), NO_MEMORY = unchecked((int)0x80540007), NOT_AVAILABLE = unchecked((int)0x80540008),
        NOT_FOUND = unchecked((int)0x80540011), NO_HOST = unchecked((int)0x8054001B), DISCONNECTED = unchecked((int)0x80540034),
        UNSUPPORTED = unchecked((int)0x805400FF), NET_SOCKET = unchecked((int)0x805400D0), NOT_REGISTERED = unchecked((int)0x805400D2),
        BAD_INDEX = unchecked((int)0x805400D3);

    // core states (sceMtpInit / sceMtpStart)
    const int CoreNone = 0, CoreInited = 1, CoreStarted = 2;
    // public status (+0 of the 0x68-byte block sceMtpGetHostInfo copies): 0 disconnected, 1 connecting, 3/4 connected, 7 stopped
    const int StatusDisconnected = 0, StatusStopped = 7;
    const uint StatusSize = 0x68, StatusReason = 0x60;
    // why the device is disconnected (+0x60): 3 = no cable
    const int ReasonNoCable = 3;

    readonly object _lock = new();
    int _core;
    int _status = StatusDisconnected;
    int _reason;
    int _devicePhase;        // 0 idle, 1 started, 2 waiting for a host
    uint _interface;         // the transport sceMtpStart was given: 1 USB, 2 Wi-Fi
    ulong _resetTick;
    int _option0D0;
    uint _queueParam0, _queueParam1;
    uint _ctxCallback;

    // ---- core

    /// <summary>(SceMtpInitParam *param): +0 device thread priority, +4 its CPU affinity, +8/+C the transfer thread's,
    /// +0x14 the utility heap. Creates the device mutex, semaphore, event flag and thread, the transfer, storage and
    /// context managers; the device status starts "stopped".</summary>
    [Hle("sceMtpInit", Nid = 0xF481FE11, Library = Lib)]
    int sceMtpInit(uint param)
    {
        lock (_lock)
        {
            if (_core != CoreNone) return ALREADY;
            _core = CoreInited;
            _status = StatusStopped;
            _reason = 0;
            _devicePhase = 0;
            return 0;
        }
    }

    /// <summary>(void): the core must be initialised and stopped.</summary>
    [Hle("sceMtpEnd", Nid = 0xF134A48F, Library = Lib)]
    int sceMtpEnd()
    {
        lock (_lock)
        {
            if (_core == CoreNone) return ERROR;
            if (_core == CoreStarted) return NOT_STOPPED;
            FreeLargeBuffer();
            _callbacks.Clear();
            _core = CoreNone;
            _status = StatusDisconnected;
            return 0;
        }
    }

    /// <summary>(int interface, int option): starts the device on interface 1 (USB) or 2 (Wi-Fi) and its thread, which
    /// finds no host: the status goes to "disconnected" (no cable).</summary>
    [Hle("sceMtpStart", Nid = 0x128D0D05, Library = Lib)]
    int sceMtpStart(uint iface, uint option)
    {
        lock (_lock)
        {
            if (_core == CoreNone) return ERROR;
            if (_core == CoreStarted) return ALREADY;
            _interface = iface;
            _core = CoreStarted;
            _status = StatusDisconnected;
            _reason = ReasonNoCable;
            _devicePhase = 2;
            return 0;
        }
    }

    [Hle("sceMtpStop", Nid = 0xC7AC05E8, Library = Lib)]
    int sceMtpStop()
    {
        lock (_lock)
        {
            if (_core == CoreNone) return ERROR;
            if (_core != CoreStarted) return ALREADY;
            _core = CoreInited;
            _devicePhase = 0;
            _status = StatusDisconnected;
            return 0;
        }
    }

    /// <summary>(void): stops the USB MTP driver (sceMtpIfStopDriver(1)); the result is the driver's.</summary>
    [Hle("sceMtpSuspend", Nid = 0x8B9F4CC5, Library = Lib)] int sceMtpSuspend() => 0;

    /// <summary>(void): starts the USB MTP driver again.</summary>
    [Hle("sceMtpResume", Nid = 0x7430E5A5, Library = Lib)] int sceMtpResume() => 0;

    /// <summary>(void): restarts the device port and records when (read back by <see cref="sceMtpGetResetTime"/>).</summary>
    [Hle("sceMtpReset", Nid = 0x97C128BD, Library = Lib)]
    int sceMtpReset()
    {
        lock (_lock) _resetTick = Rtc.NowTick;
        return 0;
    }

    /// <summary>(void) → SceUInt64: the tick of the last reset, cleared by the read.</summary>
    [Hle("sceMtpGetResetTime", Nid = 0x38CBDF4A, Library = Lib)]
    ulong sceMtpGetResetTime()
    {
        lock (_lock) { ulong t = _resetTick; _resetTick = 0; return t; }
    }

    /// <summary>(bool *started).</summary>
    [Hle("sceMtpIsStarted", Nid = 0xB7984D4C, Library = Lib)]
    int sceMtpIsStarted(uint started)
    {
        if (started == 0) return INVALID_ARG;
        GuestMemory.W32(started, _core == CoreStarted ? 1u : 0u);
        return 0;
    }

    /// <summary>(int *hostId) → the device thread's phase (0 idle, 1 started, 2 waiting for a host); *hostId the
    /// connected host (-1: none).</summary>
    [Hle("sceMtpGetDevicePhase", Nid = 0x08C00AFC, Library = Lib)]
    int sceMtpGetDevicePhase(uint hostId)
    {
        if (hostId != 0) GuestMemory.W32(hostId, uint.MaxValue);
        return _devicePhase;
    }

    /// <summary>(void) → the interface the device was started on (1 USB, 2 Wi-Fi; 0 before).</summary>
    [Hle("sceMtpGetDeviceInterface", Nid = 0x4C1B7DBB, Library = Lib)] uint sceMtpGetDeviceInterface() => _core == CoreStarted ? _interface : 0;

    /// <summary>(SceMtpNetHost *host): the Wi-Fi host the device is connected to (only over Wi-Fi with a host).</summary>
    [Hle("sceMtpGetConnectedNetHost", Nid = 0xD63692E8, Library = Lib)]
    int sceMtpGetConnectedNetHost(uint host) => host == 0 ? INVALID_ARG : NO_HOST;

    /// <summary>(void): puts the Wi-Fi device thread to sleep (USB: nothing to do).</summary>
    [Hle("sceMtpSleep", Nid = 0xCBE8187A, Library = Lib)] int sceMtpSleep() => 0;

    /// <summary>(void): wakes the Wi-Fi device thread.</summary>
    [Hle("sceMtpWake", Nid = 0x965F1CD0, Library = Lib)] int sceMtpWake() => 0;

    /// <summary>(int ms): how long the Wi-Fi device waits for its host (stored in microseconds).</summary>
    [Hle("sceMtpSetNetWaitTime", Nid = 0x768EBC03, Library = Lib)]
    void sceMtpSetNetWaitTime(int ms) { lock (_lock) _netWaitUs = ms * 1000; Log.Debug($"sceMtpSetNetWaitTime: {_netWaitUs} us"); }
    int _netWaitUs;

    /// <summary>(SceMtpHostInfo *info): a copy of the 0x68-byte public status (+0 state, +0x60 reason).</summary>
    [Hle("sceMtpGetHostInfo", Nid = 0xB6B6C7C0, Library = Lib)]
    int sceMtpGetHostInfo(uint info)
    {
        WriteStatus(info);
        return 0;
    }

    /// <summary>(SceMtpHostInfo *info): the status as Content Manager sees it: disconnected with no cable sets the
    /// reason to 3.</summary>
    [Hle("sceMtpGetHostInfoForApp", Nid = 0xF82B75F5, Library = Lib)]
    int sceMtpGetHostInfoForApp(uint info)
    {
        WriteStatus(info);
        if (info != 0 && GuestMemory.R32(info) == StatusDisconnected) GuestMemory.W32(info + StatusReason, ReasonNoCable);
        return 0;
    }

    void WriteStatus(uint info)
    {
        if (info == 0) return;
        lock (_lock)
        {
            GuestMemory.Fill(info, 0, StatusSize);
            GuestMemory.W32(info, (uint)_status);
            GuestMemory.W32(info + StatusReason, (uint)_reason);
        }
    }

    /// <summary>The library's connection check before any operation on the host.</summary>
    int HostCheck()
    {
        lock (_lock) return _status == StatusDisconnected ? DISCONNECTED : NO_HOST;
    }

    /// <summary>The library's context lookup: no context manager before Init, no queue before Start, and no
    /// operation ever gets an id without a host.</summary>
    int ContextLookup()
    {
        lock (_lock) return _core == CoreNone ? INVALID_ARG : _core == CoreInited ? NOT_AVAILABLE : NOT_FOUND;
    }

    /// <summary>A "Begin" operation: its output id pointer (and the other pointers it needs) must be given, then the
    /// host must be connected.</summary>
    int Begin(params uint[] required)
    {
        foreach (uint p in required) if (p == 0) return INVALID_ARG;
        return HostCheck();
    }

    /// <summary>An "End" (or result) call on an operation id.</summary>
    int End(params uint[] required)
    {
        foreach (uint p in required) if (p == 0) return INVALID_ARG;
        return ContextLookup();
    }

    // ---- operations on the PC (Begin returns an id through the last pointer; End waits for the result)

    [Hle("sceMtpBeginGetNumOfObject", Nid = 0xC92F9BD3, Library = Lib)] int BeginGetNumOfObject(uint a, uint b, uint id) => Begin(id);
    [Hle("sceMtpEndGetNumOfObject", Nid = 0x123CA37E, Library = Lib)] int EndGetNumOfObject(uint id, uint num) => End();
    [Hle("sceMtpBeginGetObjectMetadata", Nid = 0xBD82BC51, Library = Lib)] int BeginGetObjectMetadata(uint a, uint b, uint c, uint d, uint id) => Begin(id);
    [Hle("sceMtpEndGetObjectMetadata", Nid = 0x4F285949, Library = Lib)] int EndGetObjectMetadata(uint id, uint meta) => End();
    [Hle("sceMtpBeginImportObject", Nid = 0xA19ECC66, Library = Lib)] int BeginImportObject(uint a, uint b, uint c, uint id, uint e, uint f) => Begin(id);
    /// <summary>(int id, SceMtpObjectId *object): the imported object's id when it worked.</summary>
    [Hle("sceMtpEndImportObject", Nid = 0xCF92F4CC, Library = Lib)] int EndImportObject(uint id, uint obj) => End();
    [Hle("sceMtpBeginExportObjectWithCheck", Nid = 0xCF7D70A6, Library = Lib)] int BeginExportObjectWithCheck(uint a, uint b, uint c, uint d, uint id) => Begin(id);
    [Hle("sceMtpEndExportObjectWithCheck", Nid = 0x8F3DBF97, Library = Lib)] int EndExportObjectWithCheck(uint id, uint res) => End();
    [Hle("sceMtpBeginCreateHostDir", Nid = 0x7A16D2CF, Library = Lib)] int BeginCreateHostDir(uint a, uint b, uint c, uint id) => Begin(id);
    [Hle("sceMtpEndCreateHostDir", Nid = 0x549279DC, Library = Lib)] int EndCreateHostDir(uint id, uint res) => End();
    [Hle("sceMtpBeginDeleteHostDir", Nid = 0x849AF30D, Library = Lib)] int BeginDeleteHostDir(uint a, uint b, uint c, uint id) => Begin(id);
    [Hle("sceMtpEndDeleteHostDir", Nid = 0xF2A1482E, Library = Lib)] int EndDeleteHostDir(uint id, uint res) => End();
    [Hle("sceMtpBeginMoveHostDir", Nid = 0x77D6FB78, Library = Lib)] int BeginMoveHostDir(uint a, uint b, uint id) => Begin(id);
    [Hle("sceMtpEndMoveHostDir", Nid = 0x6079A84A, Library = Lib)] int EndMoveHostDir(uint id, uint res) => End();
    [Hle("sceMtpBeginCreateHostFile", Nid = 0x9D5EC9F2, Library = Lib)] int BeginCreateHostFile(uint a, uint b, uint c, uint id) => Begin(id);
    [Hle("sceMtpEndCreateHostFile", Nid = 0xF195BEC6, Library = Lib)] int EndCreateHostFile(uint id, uint res) => End();
    [Hle("sceMtpBeginGetObjectStatus2", Nid = 0x2747749A, Library = Lib)] int BeginGetObjectStatus2(uint a, uint b, uint c, uint id) => Begin(id);
    [Hle("sceMtpEndGetObjectStatus2", Nid = 0x7A229158, Library = Lib)] int EndGetObjectStatus2(uint id, uint res) => End();
    [Hle("sceMtpBeginGetObjectThumbnail", Nid = 0xEA291CFC, Library = Lib)] int BeginGetObjectThumbnail(uint a, uint b, uint id) => Begin(id);
    [Hle("sceMtpEndGetObjectThumbnail", Nid = 0x709C09B8, Library = Lib)] int EndGetObjectThumbnail(uint id, uint res) => End();
    [Hle("sceMtpBeginDeleteObject", Nid = 0xAC538B75, Library = Lib)] int BeginDeleteObject(uint a, uint id) => Begin(id);
    [Hle("sceMtpEndDeleteObject", Nid = 0xF988D2EE, Library = Lib)] int EndDeleteObject(uint id, uint res) => End();
    [Hle("sceMtpBeginGetSystemSetting", Nid = 0x231FB847, Library = Lib)] int BeginGetSystemSetting(uint a, uint id) => Begin(id);
    [Hle("sceMtpEndGetSystemSetting", Nid = 0xD5FB089F, Library = Lib)] int EndGetSystemSetting(uint id, uint res) => End();
    [Hle("sceMtpBeginSetSystemSetting", Nid = 0xCFA3A9DA, Library = Lib)] int BeginSetSystemSetting(uint a, uint b, uint id) => Begin(id);
    [Hle("sceMtpEndSetSystemSetting", Nid = 0xEAA968F4, Library = Lib)] int EndSetSystemSetting(uint id, uint res) => End();
    [Hle("sceMtpBeginGetNpAccountInfo", Nid = 0xE1B267DC, Library = Lib)] int BeginGetNpAccountInfo(uint a, uint id) => Begin(id);
    [Hle("sceMtpEndGetNpAccountInfo", Nid = 0xDD5917EF, Library = Lib)] int EndGetNpAccountInfo(uint id, uint res) => End();
    /// <summary>(handle, uid, SceUInt64 offset, SceUInt64 size, int *id): an offset/size of -1 reads the whole object.</summary>
    [Hle("sceMtpBeginReadObjectWithOffset", Nid = 0x94B68669, Library = Lib)]
    int BeginReadObjectWithOffset(uint a, uint b, ulong offset, ulong size, uint id) => Begin(id);
    [Hle("sceMtpEndReadObjectWithOffset", Nid = 0x7FDDA203, Library = Lib)] int EndReadObjectWithOffset(uint id, uint res) => End();
    [Hle("sceMtpBeginWriteObjectWithOffset", Nid = 0xFFCB2181, Library = Lib)]
    int BeginWriteObjectWithOffset(uint a, uint b, ulong offset, ulong size, uint id) => Begin(id);
    [Hle("sceMtpEndWriteObjectWithOffset", Nid = 0xC5E9C54E, Library = Lib)] int EndWriteObjectWithOffset(uint id, uint res) => End();
    [Hle("sceMtpBeginGetHostStorageSize", Nid = 0xF038BA83, Library = Lib)] int BeginGetHostStorageSize(uint a, uint id) => Begin(id);
    [Hle("sceMtpEndGetHostStorageSize", Nid = 0x4CAFB048, Library = Lib)] int EndGetHostStorageSize(uint id, uint res) => End();
    [Hle("sceMtpBeginCheckSameObjectExist", Nid = 0x331DDF07, Library = Lib)] int BeginCheckSameObjectExist(uint a, uint b, uint id) => Begin(id);

    /// <summary>(int id, int *exists): "not the same" (0x80540024) is a result, not an error.</summary>
    [Hle("sceMtpEndCheckSameObjectExist", Nid = 0x905BB9E8, Library = Lib)]
    int EndCheckSameObjectExist(uint id, uint res)
    {
        int r = End();
        return r == unchecked((int)0x80540024) ? 0 : r;
    }

    [Hle("sceMtpBeginGetTotalObjectSize", Nid = 0x46CCEAEC, Library = Lib)] int BeginGetTotalObjectSize(uint list, uint id) => Begin(id, list);
    /// <summary>(int id, SceUInt64 *size, int *count).</summary>
    [Hle("sceMtpEndGetTotalObjectSize", Nid = 0x4239328A, Library = Lib)] int EndGetTotalObjectSize(uint id, uint size, uint count) => End();
    [Hle("sceMtpBeginSpecifiedObjectMetadata", Nid = 0xB26C8078, Library = Lib)] int BeginSpecifiedObjectMetadata(uint a, uint b, uint c, uint id) => Begin(id, b, c);
    [Hle("sceMtpEndSpecifiedObjectMetadata", Nid = 0xD4A132BD, Library = Lib)] int EndSpecifiedObjectMetadata(uint id, uint res) => End();
    [Hle("sceMtpBeginNpDrmGetRtc", Nid = 0xCEAED3C0, Library = Lib)] int BeginNpDrmGetRtc(uint a, uint b, uint id) => Begin(id);
    /// <summary>(int id, SceRtcTick *tick, int *status).</summary>
    [Hle("sceMtpEndNpDrmGetRtc", Nid = 0x2D3DD76D, Library = Lib)] int EndNpDrmGetRtc(uint id, uint tick, uint status) => End(tick, status);
    [Hle("sceMtpBeginNpDrmActivate", Nid = 0x2DDD1CAB, Library = Lib)] int BeginNpDrmActivate(uint a, uint b, uint c, uint id) => Begin(id);
    [Hle("sceMtpEndNpDrmActivate", Nid = 0x48DE8672, Library = Lib)] int EndNpDrmActivate(uint id, uint a, uint b, uint c, uint d) => End(a, b, c, d);
    [Hle("sceMtpBeginNpDrmDeactivate", Nid = 0x13FD6153, Library = Lib)] int BeginNpDrmDeactivate(uint a, uint b, uint c, uint id) => Begin(id);
    [Hle("sceMtpEndNpDrmDeactivate", Nid = 0x923FAB90, Library = Lib)] int EndNpDrmDeactivate(uint id, uint a, uint b, uint c) => End(a, b, c);
    [Hle("sceMtpBeginNpDrmGetLicense", Nid = 0x439C5B59, Library = Lib)] int BeginNpDrmGetLicense(uint a, uint b, uint c, uint id) => Begin(id);
    [Hle("sceMtpEndNpDrmGetLicense", Nid = 0xD9EEE248, Library = Lib)] int EndNpDrmGetLicense(uint id, uint a, uint b) => End(a, b);
    [Hle("sceMtpBeginSearchObject", Nid = 0x7E078352, Library = Lib)] int BeginSearchObject(uint a, uint b, uint c, uint id) => Begin(id);
    [Hle("sceMtpEndSearchObject", Nid = 0xBBCC3F5B, Library = Lib)] int EndSearchObject(uint id, uint a, uint b) => End();
    [Hle("sceMtpBeginHandover", Nid = 0xCFCED21A, Library = Lib)] int BeginHandover(uint a, uint id) => Begin(id);
    [Hle("sceMtpEndHandover", Nid = 0x462A6667, Library = Lib)] int EndHandover(uint id, uint res) => End();
    [Hle("sceMtpBeginResumeExportObject", Nid = 0x85954990, Library = Lib)] int BeginResumeExportObject(uint id) => Begin(id);
    [Hle("sceMtpEndResumeExportObject", Nid = 0x33B08344, Library = Lib)] int EndResumeExportObject(uint id, uint res) => End();
    [Hle("sceMtpBeginResumeImportObject", Nid = 0xD52DCC7C, Library = Lib)]
    int BeginResumeImportObject(uint a, uint b, uint c, uint d, uint e, uint f, uint g, uint id) => Begin(id);
    [Hle("sceMtpEndResumeImportObject", Nid = 0x83242E03, Library = Lib)] int EndResumeImportObject(uint id, uint obj) => End();
    /// <summary>(int id): pauses an export in flight (its data is kept for sceMtpBeginResumeExportObject).</summary>
    [Hle("sceMtpPauseExportObject", Nid = 0xF4CAB5A5, Library = Lib)] int PauseExportObject(uint id) => End();

    /// <summary>HTTP through the PC (only for Settings, NPXS10015, and the update over the PC).</summary>
    [Hle("sceMtpBeginHttpGetDataWithUrl", Nid = 0x6B7800B7, Library = Lib)] int BeginHttpGetDataWithUrl(uint a, uint b, uint c, uint d, uint id) => Begin(id);
    [Hle("sceMtpEndHttpGetDataWithUrl", Nid = 0x520D2696, Library = Lib)] int EndHttpGetDataWithUrl(uint id) => End();
    [Hle("sceMtpBeginHttpGetProperty", Nid = 0xE3727775, Library = Lib)] int BeginHttpGetProperty(uint a, uint b, uint id) => Begin(id);
    [Hle("sceMtpEndHttpGetProperty", Nid = 0x54CDF039, Library = Lib)] int EndHttpGetProperty(uint id, uint a, uint b) => End();
    /// <summary>(int id, offset, size, void *buf, int *received): the data of an HTTP operation in flight.</summary>
    [Hle("sceMtpHttpRecvData", Nid = 0xCA704934, Library = Lib)] int HttpRecvData(uint id, uint a, uint b, uint buf, uint got) => End(buf, got);
    [Hle("sceMtpHttpSendData", Nid = 0xEABA859D, Library = Lib)] int HttpSendData(uint id, uint a, uint b, uint buf, uint sent) => End(buf, sent);
    [Hle("sceMtpHttpNotifyResultResponse", Nid = 0x77793BF2, Library = Lib)] int HttpNotifyResultResponse(uint id, uint res) => End();

    /// <summary>Socket operations the network library runs on the PC (RPC).</summary>
    [Hle("sceMtpBeginRpcNetOperation", Nid = 0xD6C90706, Library = Lib)] int BeginRpcNetOperation(uint a, uint b, uint id) => Begin(id);
    [Hle("sceMtpEndRpcNetOperation", Nid = 0x6E2FE43A, Library = Lib)] int EndRpcNetOperation(uint id, uint a, uint b) => End();
    [Hle("sceMtpBeginRpcNetOperationSend", Nid = 0x8E7F11CB, Library = Lib)] int BeginRpcNetOperationSend(uint a, uint id) => Begin(id);
    [Hle("sceMtpEndRpcNetOperationSend", Nid = 0xD919C89C, Library = Lib)] int EndRpcNetOperationSend(uint id, uint a, uint b) => End();
    [Hle("sceMtpBeginRpcNetOperationRecv", Nid = 0x32985803, Library = Lib)] int BeginRpcNetOperationRecv(uint a, uint b, uint id) => Begin(id);
    [Hle("sceMtpEndRpcNetOperationRecv", Nid = 0x0311F66F, Library = Lib)] int EndRpcNetOperationRecv(uint id, uint res) => End();

    /// <summary>(a, b, c, int *aborted): aborts an RPC socket operation on the PC (needs the host).</summary>
    [Hle("sceMtpRpcNetOperationAbort", Nid = 0xFB181427, Library = Lib)]
    int RpcNetOperationAbort(uint a, uint b, uint c, uint aborted) => HostCheck();

    /// <summary>(int id): cancels an operation (0: the transfer in progress).</summary>
    [Hle("sceMtpCancel", Nid = 0x1021F75F, Library = Lib)]
    int Cancel(uint id) => id == 0 ? 0 : End();

    /// <summary>(void): tells the PC the shell is leaving (an event over the link) and waits for it.</summary>
    [Hle("sceMtpTerminateExecution", Nid = 0x392B8D5E, Library = Lib)]
    int sceMtpTerminateExecution()
    {
        lock (_lock)
        {
            if (_core == CoreNone) return INVALID_ARG;
            if (_core == CoreInited) return NOT_AVAILABLE;
        }
        return HostCheck();
    }

    /// <summary>(int id, callback, void *arg): the callback run when an operation finishes.</summary>
    [Hle("sceMtpSetFinishCallback", Nid = 0xEBB48255, Library = Lib)] int sceMtpSetFinishCallback(uint id, uint cb, uint arg) => End();

    /// <summary>(int id): 0 when the id names an operation that is still valid.</summary>
    [Hle("sceMtpCheckContextValid", Nid = 0x24130489, Library = Lib)] int sceMtpCheckContextValid(uint id) => End();

    /// <summary>(int id, SceMtpProgress *progress): +0..+0x13 of the operation's progress record.</summary>
    [Hle("sceMtpGetProgress", Nid = 0x0497FA87, Library = Lib)]
    int GetProgress(uint id, uint progress)
    {
        if (progress == 0) return INVALID_ARG;
        lock (_lock) return _core == CoreNone ? INVALID_ARG : NOT_FOUND;
    }

    /// <summary>(int option): an option of the transfer queue (stored with the device status).</summary>
    [Hle("sceMtpSetQueueOption", Nid = 0x3AF2F5D8, Library = Lib)] void SetQueueOption(int v) { lock (_lock) _option0D0 = v; Log.Debug($"sceMtpSetQueueOption({_option0D0})"); }

    /// <summary>(void *name32): the 32-byte owner name of the transfer queue (only while started).</summary>
    [Hle("sceMtpSetQueueName", Nid = 0xDE41AE27, Library = Lib)] int SetQueueName(uint name) => 0;

    /// <summary>(callback): the context manager's notification callback.</summary>
    [Hle("sceMtpSetContextCallback", Nid = 0x4E2CF833, Library = Lib)] void SetContextCallback(uint cb) { lock (_lock) _ctxCallback = cb; Log.Debug($"sceMtpSetContextCallback(0x{_ctxCallback:X8})"); }

    [Hle("sceMtpSetQueueParam", Nid = 0x934240B4, Library = Lib)]
    int SetQueueParam(uint a, uint b) { lock (_lock) { _queueParam0 = a; _queueParam1 = b; Log.Debug($"sceMtpSetQueueParam(0x{_queueParam0:X}, 0x{_queueParam1:X})"); } return 0; }

    [Hle("sceMtpClearQueueParam", Nid = 0xE7CB0238, Library = Lib)]
    int ClearQueueParam() { lock (_lock) { _queueParam0 = 0; _queueParam1 = 0; } return 0; }

    /// <summary>(int value): an option of the library kept as is.</summary>
    [Hle("sceMtpSetOption1D8", Nid = 0x6BB1C852, Library = Lib)] int SetOption1D8(int v) { lock (_lock) _option1D8 = v; Log.Debug($"sceMtpSetOption1D8({_option1D8})"); return 0; }
    int _option1D8;

    // ---- Marlin DRM operations the PC runs (a, in, out): both buffers needed, then the host

    int Marlin(uint input, uint output) => input == 0 || output == 0 ? INVALID_ARG : HostCheck();
    [Hle("sceMtpExecMarlinActivation", Nid = 0xC12DE479, Library = Lib)] int MarlinActivation(uint a, uint i, uint o) => Marlin(i, o);
    [Hle("sceMtpExecMarlinDeactivation", Nid = 0x24369599, Library = Lib)] int MarlinDeactivation(uint a, uint i, uint o) => Marlin(i, o);
    [Hle("sceMtpExecMarlinLicenseMove", Nid = 0xA55E6AB6, Library = Lib)] int MarlinLicenseMove(uint a, uint i, uint o) => Marlin(i, o);
    [Hle("sceMtpExecMarlinLicenseCopy", Nid = 0x0BC34F1F, Library = Lib)] int MarlinLicenseCopy(uint a, uint i, uint o) => Marlin(i, o);
    [Hle("sceMtpExecMarlinLicenseCheck", Nid = 0x542F3064, Library = Lib)] int MarlinLicenseCheck(uint a, uint i, uint o) => Marlin(i, o);
    [Hle("sceMtpExecMarlinSharedInfoAcq", Nid = 0xE35C1C83, Library = Lib)] int MarlinSharedInfoAcq(uint a, uint i, uint o) => Marlin(i, o);

    // ---- the Marlin settings blocks: 0 and 1 and 2 are the sizes, 3 and 4 the two buffers they size

    uint _cfg0, _cfgSize1, _cfgSize2, _cfgBuf1, _cfgBuf2;

    /// <summary>(int kind, SceSize size, int offset, void *dst, SceSize *got).</summary>
    [Hle("sceMtpGetMarlinConfig", Nid = 0xC10B3D7C, Library = Lib)]
    int GetMarlinConfig(int kind, uint size, uint offset, uint dst, uint got)
    {
        if (dst == 0 || got == 0) return INVALID_ARG;
        lock (_lock)
        {
            switch (kind)
            {
                case 0: GuestMemory.W32(dst, _cfg0); GuestMemory.W32(got, 4); break;
                case 1: GuestMemory.W32(dst, _cfgSize1); GuestMemory.W32(got, 4); break;
                case 2: GuestMemory.W32(dst, _cfgSize2); GuestMemory.W32(got, 4); break;
                case 3: if (_cfgBuf1 != 0) GuestMemory.Copy(dst, _cfgBuf1 + offset, size); GuestMemory.W32(got, size); break;
                case 4: if (_cfgBuf2 != 0) GuestMemory.Copy(dst, _cfgBuf2 + offset, size); GuestMemory.W32(got, size); break;
            }
        }
        return 0;
    }

    /// <summary>(int kind, int offset, const void *src, SceSize size): setting a size (1, 2) reallocates its buffer.</summary>
    [Hle("sceMtpSetMarlinConfig", Nid = 0x6211F119, Library = Lib)]
    int SetMarlinConfig(int kind, uint offset, uint src, uint size)
    {
        if (src == 0) return INVALID_ARG;
        lock (_lock)
        {
            switch (kind)
            {
                case 0: _cfg0 = GuestMemory.R32(src); break;
                case 1:
                    _cfgSize1 = GuestMemory.R32(src);
                    if (_cfgBuf1 != 0) Emu.Memory.Free(_cfgBuf1);
                    _cfgBuf1 = _cfgSize1 == 0 ? 0 : Emu.Memory.Alloc(_cfgSize1, "mtp marlin 1", align: 16);
                    if (_cfgBuf1 == 0) return NO_MEMORY;
                    break;
                case 2:
                    _cfgSize2 = GuestMemory.R32(src);
                    if (_cfgBuf2 != 0) Emu.Memory.Free(_cfgBuf2);
                    _cfgBuf2 = _cfgSize2 == 0 ? 0 : Emu.Memory.Alloc(_cfgSize2, "mtp marlin 2", align: 16);
                    if (_cfgBuf2 == 0) return NO_MEMORY;
                    break;
                case 3: if (_cfgBuf1 != 0) GuestMemory.Copy(_cfgBuf1 + offset, src, size); break;
                case 4: if (_cfgBuf2 != 0) GuestMemory.Copy(_cfgBuf2 + offset, src, size); break;
            }
        }
        return 0;
    }

    // ---- the large transfer buffer and the library heap

    uint _largeBuffer;
    bool _largeBufferBusy;

    /// <summary>(SceSize size) → a block of at least 4 KiB, 64-byte aligned, from the library's heap.</summary>
    [Hle("sceMtpAlloc", Nid = 0xDA13726B, Library = Lib)]
    uint sceMtpAlloc(uint size) => Emu.Memory.Alloc(Math.Max(size, 0x1000u), "mtp heap", align: 0x40);

    [Hle("sceMtpFree", Nid = 0x492421F7, Library = Lib)]
    void sceMtpFree(uint p) { if (p != 0) try { Emu.Memory.Free(p); } catch (ArgumentException) { } }

    /// <summary>(void): marks the large transfer buffer in use.</summary>
    [Hle("sceMtpLargeBufferBegin", Nid = 0xAF9A2787, Library = Lib)] void LargeBufferBegin() { lock (_lock) _largeBufferBusy = true; }

    /// <summary>The large buffer is marked in use (tests).</summary>
    internal bool LargeBufferBusy { get { lock (_lock) return _largeBufferBusy; } }

    /// <summary>(void): frees the Marlin buffers and gives the large buffer back (none was made: "not available").</summary>
    [Hle("sceMtpLargeBufferEnd", Nid = 0xBB32CD0A, Library = Lib)]
    int LargeBufferEnd()
    {
        lock (_lock)
        {
            if (_cfgBuf2 != 0) { Emu.Memory.Free(_cfgBuf2); _cfgBuf2 = 0; }
            if (_cfgBuf1 != 0) { Emu.Memory.Free(_cfgBuf1); _cfgBuf1 = 0; }
            if (_largeBuffer == 0) return NOT_AVAILABLE;
            FreeLargeBuffer();
            _largeBufferBusy = false;
            return 0;
        }
    }

    void FreeLargeBuffer()
    {
        if (_largeBuffer != 0) { Emu.Memory.Free(_largeBuffer); _largeBuffer = 0; }
    }

    // ---- event callbacks (one slot; events are bits of 0x33F)

    readonly List<(uint Events, uint Cb, uint Arg)> _callbacks = new();

    [Hle("sceMtpRegisterCallback", Nid = 0x5120B22A, Library = Lib)]
    int RegisterCallback(uint events, uint cb, uint arg)
    {
        if ((events & 0xFFFFFCC0) != 0) return UNSUPPORTED;
        lock (_lock)
        {
            if (_callbacks.Count >= 1) return NOT_AVAILABLE;
            _callbacks.Add((events, cb, arg));
            return 0;
        }
    }

    [Hle("sceMtpUnregisterCallback", Nid = 0x39CEBDD1, Library = Lib)]
    int UnregisterCallback(uint events, uint cb)
    {
        if ((events & 0xFFFFFCC0) != 0) return UNSUPPORTED;
        lock (_lock) return _callbacks.RemoveAll(c => c.Cb == cb) > 0 ? 0 : NOT_FOUND;
    }

    // ---- storages: the memory card storage of the MTP session (none is startable without a host)

    /// <summary>(const char *name): creates the MTP object handles of a storage.</summary>
    [Hle("sceMtpStorageCreateHandles", Nid = 0x2EBE47B8, Library = Lib)] int StorageCreateHandles(uint name) => INVALID_ARG;

    [Hle("sceMtpStorageDestroyHandles", Nid = 0xA824A205, Library = Lib)] int StorageDestroyHandles(uint name) => INVALID_ARG;

    /// <summary>(const char *path, SceMtpObjectId *id): +0 unique id, +4 the storage bits of the handle.</summary>
    [Hle("sceMtpStorageRegisterObject", Nid = 0x81BCF297, Library = Lib)]
    int StorageRegisterObject(uint path, uint id) => path == 0 || id == 0 ? INVALID_ARG : NOT_AVAILABLE;

    [Hle("sceMtpStorageGetObjectIdByPath", Nid = 0xE107F90D, Library = Lib)]
    int StorageGetObjectIdByPath(uint path, uint id) =>
        path == 0 || GuestMemory.Read<byte>(path) == 0 || id == 0 ? INVALID_ARG : NOT_AVAILABLE;

    /// <summary>(uniqueId, SceUInt32 handle): the handle's logical storage must be up.</summary>
    [Hle("sceMtpStorageDeleteObjectData", Nid = 0xE2A9E59C, Library = Lib)] int StorageDeleteObjectData(uint uid, uint handle) => NOT_AVAILABLE;

    [Hle("sceMtpStorageGetObjectData", Nid = 0x9310EEEF, Library = Lib)]
    int StorageGetObjectData(uint uid, uint handle, uint data) => data == 0 ? INVALID_ARG : NOT_AVAILABLE;

    [Hle("sceMtpStorageUpdateObjectData", Nid = 0xEA4E8C39, Library = Lib)]
    int StorageUpdateObjectData(uint uid, uint handle, uint data) => data == 0 ? INVALID_ARG : NOT_AVAILABLE;

    // ---- MTP over Wi-Fi: host discovery and the registered PCs (none)

    int _netMode = 1;          // 1 or 2: the Wi-Fi connection mode the user picked
    bool _discovering;

    /// <summary>(void): opens the discovery socket ("net mtp socket") and starts looking for PCs. Without a network
    /// the socket cannot be made.</summary>
    [Hle("sceMtpNetStartHostDiscovery", Nid = 0x79F3EE1B, Library = Lib)]
    int NetStartHostDiscovery()
    {
        lock (_lock) { if (_discovering) return 0; }
        return NET_SOCKET;
    }

    [Hle("sceMtpNetStopHostDiscovery", Nid = 0x71197C62, Library = Lib)]
    int NetStopHostDiscovery() { lock (_lock) _discovering = false; return 0; }

    /// <summary>(int *state): the Wi-Fi device: 0 stopped, 1 discovering, 2 connected.</summary>
    [Hle("sceMtpNetGetDeviceState", Nid = 0x06123E14, Library = Lib)]
    int NetGetDeviceState(uint state)
    {
        if (state == 0) return INVALID_ARG;
        GuestMemory.W32(state, _discovering ? 1u : 0u);
        return 0;
    }

    [Hle("sceMtpNetGetNumDiscoveredHosts", Nid = 0x06714DA7, Library = Lib)]
    int NetGetNumDiscoveredHosts(uint num)
    {
        if (num == 0) return INVALID_ARG;
        GuestMemory.W32(num, 0);
        return 0;
    }

    /// <summary>(int index, SceMtpNetHost *host /*200 bytes*/).</summary>
    [Hle("sceMtpNetGetDiscoveredHost", Nid = 0x2CA2CD16, Library = Lib)]
    int NetGetDiscoveredHost(uint index, uint host) => host == 0 ? INVALID_ARG : BAD_INDEX;

    /// <summary>(SceMtpNetHost *host, int pin): pairing needs the Wi-Fi device running.</summary>
    [Hle("sceMtpNetRegisterSendPin", Nid = 0x66B08C7D, Library = Lib)]
    int NetRegisterSendPin(uint host) => host == 0 ? INVALID_ARG : NOT_AVAILABLE;

    [Hle("sceMtpNetRegisterCancel", Nid = 0x55B66FA0, Library = Lib)] int NetRegisterCancel() => NOT_AVAILABLE;

    /// <summary>(SceMtpNetHost *host, int timeout): connects to a discovered host (none comes).</summary>
    [Hle("sceMtpNetConnectHost", Nid = 0x6DBD4FF7, Library = Lib)]
    int NetConnectHost(uint host, uint timeout) => host == 0 ? INVALID_ARG : NET_SOCKET;

    /// <summary>(SceMtpNetHost *host): makes a registered PC the selected one.</summary>
    [Hle("sceMtpNetAssignHost", Nid = 0x5B4AF4B8, Library = Lib)]
    int NetAssignHost(uint host) => host == 0 ? INVALID_ARG : NOT_REGISTERED;

    [Hle("sceMtpNetGetNumRegisteredHosts", Nid = 0x8E76FE4D, Library = Lib)]
    int NetGetNumRegisteredHosts(uint num)
    {
        if (num == 0) return INVALID_ARG;
        GuestMemory.W32(num, 0);
        return 0;
    }

    [Hle("sceMtpNetGetRegisteredHost", Nid = 0x0898F989, Library = Lib)]
    int NetGetRegisteredHost(uint index, uint host) => index < 0x19 && host != 0 ? BAD_INDEX : INVALID_ARG;

    /// <summary>(SceMtpNetHost *host): unregisters a PC (none is registered).</summary>
    [Hle("sceMtpNetUnregisterHost", Nid = 0x9144D0B4, Library = Lib)]
    int NetUnregisterHost(uint host) => host == 0 ? INVALID_ARG : ALREADY;

    [Hle("sceMtpNetUnregisterAllHosts", Nid = 0x27F8701C, Library = Lib)] int NetUnregisterAllHosts() => 0;

    [Hle("sceMtpNetGetSelectedHost", Nid = 0xED13170C, Library = Lib)]
    int NetGetSelectedHost(uint host) => host == 0 ? INVALID_ARG : NOT_REGISTERED;

    /// <summary>(bool *selected): whether a registered PC is selected.</summary>
    [Hle("sceMtpNetHasSelectedHost", Nid = 0x8CD95681, Library = Lib)]
    int NetHasSelectedHost(uint selected)
    {
        if (selected == 0) return INVALID_ARG;
        GuestMemory.W32(selected, 0);
        return 0;
    }

    [Hle("sceMtpNetSetConnectionMode", Nid = 0xA78FEA82, Library = Lib)]
    int NetSetConnectionMode(int mode)
    {
        if (mode is not (1 or 2)) return INVALID_ARG;
        lock (_lock) _netMode = mode;
        return 0;
    }

    [Hle("sceMtpGetCurrentInterface", Nid = 0xD4E9D95E, Library = Lib)]
    int sceMtpGetCurrentInterface(uint mode)
    {
        if (mode == 0) return INVALID_ARG;
        GuestMemory.W32(mode, (uint)_netMode);
        return 0;
    }

    /// <summary>(SceMtpNetHost *host, bool *online): whether the host was discovered.</summary>
    [Hle("sceMtpNetCheckOnline", Nid = 0x56FA46BB, Library = Lib)]
    int NetCheckOnline(uint host, uint online)
    {
        if (host == 0 || online == 0) return INVALID_ARG;
        GuestMemory.W32(online, 0);
        return 0;
    }

    /// <summary>(SceMtpNetHost *host, bool *registered).</summary>
    [Hle("sceMtpNetCheckRegistered", Nid = 0x389F2FE2, Library = Lib)]
    int NetCheckRegistered(uint host, uint registered)
    {
        if (host == 0 || registered == 0) return INVALID_ARG;
        GuestMemory.W32(registered, 0);
        return 0;
    }

    /// <summary>(SceUInt8 id[16]): the device id PCs pair with: bytes of 1 with the MAC address first.</summary>
    [Hle("sceMtpNetGetDeviceId", Nid = 0x29FAC63B, Library = Lib)]
    int NetGetDeviceId(uint id)
    {
        if (id == 0) return INVALID_ARG;
        GuestMemory.Fill(id, 1, 16);
        Net.MacAddress.CopyTo(GuestMemory.Span(id, 6));
        return 0;
    }

    // ---- background copy (the shell registers its callbacks; with none, nothing runs)

    uint _bgCbFirst, _bgCbStart, _bgCbSuspend, _bgCbResume, _bgCbStatus, _bgCbCopy;
    const uint BgTasks = 0;

    /// <summary>(const SceMtpBgCallbacks *cb): six callbacks (+0..+0x14).</summary>
    [Hle("sceMtpRegisterBgCopyCallbacks", Nid = 0x67DB4C13, Library = Lib)]
    int RegisterBgCopyCallbacks(uint cb)
    {
        lock (_lock)
        {
            _bgCbFirst = GuestMemory.R32(cb); _bgCbStart = GuestMemory.R32(cb + 4); _bgCbSuspend = GuestMemory.R32(cb + 8);
            _bgCbResume = GuestMemory.R32(cb + 0xC); _bgCbStatus = GuestMemory.R32(cb + 0x10); _bgCbCopy = GuestMemory.R32(cb + 0x14);
            Log.Debug($"sceMtpRegisterBgCopyCallbacks: 0x{_bgCbFirst:X8} 0x{_bgCbStart:X8} 0x{_bgCbSuspend:X8} 0x{_bgCbResume:X8} 0x{_bgCbStatus:X8} 0x{_bgCbCopy:X8}");
        }
        return 0;
    }

    /// <summary>(int *running, int *queued, void *info): the background copy tasks (none).</summary>
    [Hle("sceMtpGetBgTaskInfo", Nid = 0x7C5DDDD6, Library = Lib)]
    int sceMtpGetBgTaskInfo(uint running, uint queued, uint info)
    {
        if (running != 0) GuestMemory.W32(running, BgTasks);
        if (queued != 0) GuestMemory.W32(queued, 0);
        return 0;
    }

    [Hle("sceMtpBgCopyStart", Nid = 0xE89AF226, Library = Lib)] int BgCopyStart(uint task, uint arg) => _bgCbStart == 0 ? NOT_AVAILABLE : HostCheck();
    [Hle("sceMtpBgCopyResume", Nid = 0xE7542628, Library = Lib)] int BgCopyResume() => _bgCbResume == 0 ? NOT_AVAILABLE : 0;
    [Hle("sceMtpBgCopyRemove", Nid = 0xB15DA25F, Library = Lib)] int BgCopyRemove(uint task) => 0;
    [Hle("sceMtpBgCopyRemoveEntry", Nid = 0x87D21619, Library = Lib)] int BgCopyRemoveEntry(uint a, uint entry) => 0;
    [Hle("sceMtpBgCopySuspend", Nid = 0x972A378F, Library = Lib)] int BgCopySuspend(uint a, uint b, uint c) => _bgCbCopy == 0 ? NOT_AVAILABLE : 0;
    [Hle("sceMtpBgCopyAddData", Nid = 0xADE08924, Library = Lib)] int BgCopyAddData(uint a, uint b, uint c, uint d, uint e) => _bgCbStart == 0 ? NOT_AVAILABLE : HostCheck();

    /// <summary>(void *arg): runs the "suspend" callback (its result is not returned).</summary>
    [Hle("sceMtpBgCopyNotify", Nid = 0x981E220E, Library = Lib)] int BgCopyNotify(uint arg) => 0;

    /// <summary>(void) → whether a background copy is in progress.</summary>
    [Hle("sceMtpBgCopyIsRunning", Nid = 0xFC6D4772, Library = Lib)] bool BgCopyIsRunning() => false;

    /// <summary>(int task, SceMtpBgCopyStatus *status): +4 the time, +0x10 the progress (at most 100), +0x14 the
    /// step, +0x18 the state (2 idle).</summary>
    [Hle("sceMtpBgCopyGetStatus", Nid = 0xE3EBABB4, Library = Lib)]
    int BgCopyGetStatus(uint task, uint status)
    {
        if (status == 0) return INVALID_ARG;
        GuestMemory.Fill(status + 4, 0, 0x14);
        GuestMemory.W32(status + 0x14, 1);
        GuestMemory.W32(status + 0x18, 2);
        return 0;
    }

    /// <summary>(SceMtpBgCopyParam *param): copies in the background while the shell sleeps (needs a background
    /// context: none without a host).</summary>
    [Hle("sceMtpBgCopyExec", Nid = 0xBF311CC8, Library = Lib)] int BgCopyExec(uint param) => HostCheck();

    /// <summary>(int what, bool *result): whether something is possible now (7: connected to a host).</summary>
    [Hle("sceMtpCheckCapability", Nid = 0x94040962, Library = Lib)]
    int CheckCapability(uint what, uint result)
    {
        if (result == 0) return INVALID_ARG;
        GuestMemory.Write<byte>(result, 0);
        return 0;
    }
}
