using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceNpSignaling (P2P NAT traversal): contexts and options work locally, no peer ever connects.</summary>
public sealed partial class NpSignaling : HleModule
{
    const int ERR_NOT_INITIALIZED = NpManager.ERR_NOT_INITIALIZED, ERR_INVALID_ARGUMENT = NpManager.ERR_INVALID_ARGUMENT;

    readonly object _lock = new();
    bool _inited;
    int _nextCtx = 1;
    readonly HashSet<int> _ctx = new();
    readonly Dictionary<(int Ctx, int Opt), int> _opts = new();

    [Hle] int sceNpSignalingInit(uint poolSize, int prio, int affinity, uint stackSize) { lock (_lock) _inited = true; return 0; }
    [Hle] int sceNpSignalingTerm() { lock (_lock) { _inited = false; _ctx.Clear(); _opts.Clear(); } return 0; }

    [Hle]
    int sceNpSignalingCreateCtx(uint npId, uint handler, uint arg, Ptr<int> ctxId)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (ctxId.IsNull) return ERR_INVALID_ARGUMENT;
            int id = _nextCtx++;
            _ctx.Add(id);
            ctxId.Value = id;
            return 0;
        }
    }

    [Hle]
    int sceNpSignalingDestroyCtx(int ctxId)
    {
        lock (_lock)
        {
            if (!_ctx.Remove(ctxId)) return ERR_INVALID_ARGUMENT;
            foreach (var k in _opts.Keys.Where(k => k.Ctx == ctxId).ToList()) _opts.Remove(k);
            return 0;
        }
    }

    [Hle] int sceNpSignalingSetCtxOpt(int ctxId, int opt, int value) { lock (_lock) { if (!_ctx.Contains(ctxId)) return ERR_INVALID_ARGUMENT; _opts[(ctxId, opt)] = value; } return 0; }

    [Hle]
    int sceNpSignalingGetCtxOpt(int ctxId, int opt, Ptr<int> value)
    {
        lock (_lock)
        {
            if (!_ctx.Contains(ctxId) || value.IsNull) return ERR_INVALID_ARGUMENT;
            value.Value = _opts.GetValueOrDefault((ctxId, opt));
            return 0;
        }
    }

    [Hle] int sceNpSignalingActivateConnection(int ctxId, uint npId, uint connId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpSignalingDeactivateConnection(int ctxId, int connId) => 0;
    [Hle] int sceNpSignalingTerminateConnection(int ctxId, int connId) => 0;
    [Hle] int sceNpSignalingGetConnectionStatus(int ctxId, int connId, Ptr<int> status) { status.TrySet(0); return PsnAccount.Current.ServerError; } // INACTIVE
    [Hle] int sceNpSignalingGetConnectionInfo(int ctxId, int connId, int code, uint info) => PsnAccount.Current.ServerError;
    [Hle] int sceNpSignalingGetConnectionFromNpId(int ctxId, uint npId, uint connId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpSignalingGetConnectionFromPeerAddress(int ctxId, uint addr, uint port, uint connId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpSignalingGetLocalNetInfo(int ctxId, uint info) => PsnAccount.Current.ServerError;
    [Hle] int sceNpSignalingGetPeerNetInfo(int ctxId, uint npId, uint reqId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpSignalingCancelPeerNetInfo(int ctxId, int reqId) => 0;
    [Hle] int sceNpSignalingGetPeerNetInfoResult(int ctxId, int reqId, uint info) => PsnAccount.Current.ServerError;
    [Hle] int sceNpSignalingGetMemoryInfo(uint info) { if (info != 0) GuestMemory.Fill(info, 0, 12); return 0; }
}

/// <summary>SceNpWebApi (PSN REST client): local handles/requests/filters work, sending fails while signed out and push
/// callbacks never fire.</summary>
public sealed partial class NpWebApi : HleModule
{
    const int ERR_INVALID_ARGUMENT = NpManager.ERR_INVALID_ARGUMENT;

    int _nextId = 1;
    long _nextReq = 1;
    readonly byte[] _titleId = new byte[16];
    readonly Dictionary<long, int> _parts = new();

    int NewId() => Interlocked.Increment(ref _nextId);

    [Hle] int sceNpWebApiInitialize(int libHttpCtxId, uint poolSize) => NewId();
    [Hle] int sceNpWebApiTerminate(int libCtxId) { lock (_parts) _parts.Clear(); return 0; }

    [Hle]
    int sceNpWebApiSetNpTitleId(uint titleId, uint secret)
    {
        if (titleId == 0) return ERR_INVALID_ARGUMENT;
        lock (_titleId) GuestMemory.Span(titleId, 16).CopyTo(_titleId);
        return 0;
    }

    [Hle]
    int sceNpWebApiGetNpTitleId(uint titleId)
    {
        if (titleId == 0) return ERR_INVALID_ARGUMENT;
        lock (_titleId) _titleId.CopyTo(GuestMemory.Span(titleId, 16));
        return 0;
    }

    // ---- system-software NIDs of np_webapi.suprx (FW 3.74): the shell and Messages set the library up for
    // themselves. The library's argument checks (0x80552902), then the same local objects as above.
    const int ERR_WEBAPI_ARG = unchecked((int)0x80552902);

    /// <summary>(const SceNpTitleId *titleId, const SceNpTitleSecret *secret, ...): the title id the system
    /// software's requests carry ("NPXS19999_00" for the shell): both are required.</summary>
    [Hle("sceNpWebApi_4699EF17", Nid = 0x4699EF17, Library = "SceNpWebApi")]
    int sceNpWebApiSetNpTitleIdForSystem(uint titleId, uint secret) => titleId == 0 || secret == 0 ? ERR_WEBAPI_ARG : sceNpWebApiSetNpTitleId(titleId, secret);

    /// <summary>(uint poolSize, const void *param, ...): a library context with its own pool (0 is refused); its id.</summary>
    [Hle("sceNpWebApi_E8FBA9B8", Nid = 0xE8FBA9B8, Library = "SceNpWebApi")]
    int sceNpWebApiCreateContextForSystem(uint poolSize, uint param) => poolSize == 0 ? ERR_WEBAPI_ARG : NewId();

    /// <summary>(void *obj): hands an object to the library's internal manager when the library is set up (else 0).
    /// Nothing is ever sent, so there is nothing to keep.</summary>
    [Hle("sceNpWebApi_E83CE7F1", Nid = 0xE83CE7F1, Library = "SceNpWebApi")]
    int sceNpWebApiRegisterForSystem(uint obj) => 0;

    /// <summary>(const void *a, const void *b): a library context for the system software (the library's busy check,
    /// 0x80559F02, cannot trigger here); its id. Messages and the PS4 Link set themselves up with it.</summary>
    [Hle("sceNpWebApi_A11FBA1A", Nid = 0xA11FBA1A, Library = "SceNpWebApi")]
    int sceNpWebApiInitializeForSystem(uint a, uint b, uint c) => a == 0 ? ERR_WEBAPI_ARG : NewId();

    /// <summary>(int libCtxId, const void *a, const void *b): a push event filter for the system software (both
    /// required); its id.</summary>
    [Hle("sceNpWebApi_19062C5A", Nid = 0x19062C5A, Library = "SceNpWebApi")]
    int sceNpWebApiCreatePushEventFilterForSystem(int libCtxId, uint a, uint b) => a == 0 || b == 0 ? ERR_WEBAPI_ARG : NewId();

    /// <summary>(int libCtxId, a, b, c, void *callback, void *arg): a push event callback (callback and arg required);
    /// its id. Push events never arrive offline.</summary>
    [Hle("sceNpWebApi_159396F0", Nid = 0x159396F0, Library = "SceNpWebApi")]
    int sceNpWebApiRegisterPushEventCallbackForSystem(int libCtxId, uint a, uint b, uint c, uint callback, uint arg) =>
        callback == 0 || arg == 0 ? ERR_WEBAPI_ARG : NewId();

    /// <summary>(int libCtxId, int filterId, void *callback, void *arg): service / extended push event callbacks
    /// (NID_83E94928, NID_1BDB76C0): the callback is required; an id.</summary>
    [Hle("sceNpWebApi_83E94928", Nid = 0x83E94928, Library = "SceNpWebApi")]
    int sceNpWebApiRegisterServicePushEventCallbackForSystem(int libCtxId, int filterId, uint callback, uint arg) => callback == 0 ? ERR_WEBAPI_ARG : NewId();

    [Hle("sceNpWebApi_1BDB76C0", Nid = 0x1BDB76C0, Library = "SceNpWebApi")]
    int sceNpWebApiRegisterExtdPushEventCallbackForSystem(int libCtxId, int filterId, uint callback, uint arg) => callback == 0 ? ERR_WEBAPI_ARG : NewId();

    /// <summary>(int id): drops a system push event object; nothing is kept.</summary>
    [Hle("sceNpWebApi_BED99F66", Nid = 0xBED99F66, Library = "SceNpWebApi")]
    int sceNpWebApiUnregisterForSystem(int id) => 0;

    [Hle] int sceNpWebApiCreateHandle(int libCtxId) => NewId();
    [Hle] int sceNpWebApiDeleteHandle(int libCtxId, int handleId) => 0;
    [Hle] int sceNpWebApiAbortHandle(int libCtxId, int handleId) => 0;

    int NewRequest(uint requestId, string group, string path)
    {
        if (requestId == 0) return ERR_INVALID_ARGUMENT;
        long id = Interlocked.Increment(ref _nextReq);
        GuestMemory.Write(requestId, id);
        Log.Debug($"sceNpWebApi request {id}: {group}{path} (offline)");
        return 0;
    }

    [Hle] int sceNpWebApiCreateRequest(int libCtxId, string apiGroup, string path, int method, uint content, uint requestId) => NewRequest(requestId, apiGroup, path);
    [Hle] int sceNpWebApiCreateMultipartRequest(int libCtxId, string apiGroup, string path, int method, uint requestId) => NewRequest(requestId, apiGroup, path);
    [Hle] int sceNpWebApiDeleteRequest(long requestId) { lock (_parts) _parts.Remove(requestId); return 0; }
    [Hle] int sceNpWebApiAbortRequest(long requestId) => 0;
    [Hle] int sceNpWebApiAddHttpRequestHeader(long requestId, uint name, uint value) => 0;

    [Hle]
    int sceNpWebApiAddMultipartPart(long requestId, uint param, Ptr<int> index)
    {
        int i;
        lock (_parts) { _parts.TryGetValue(requestId, out i); _parts[requestId] = i + 1; }
        index.TrySet(i);
        return 0;
    }

    [Hle] int sceNpWebApiSetMultipartContentType(long requestId, uint type, uint boundary) => 0;
    [Hle] int sceNpWebApiSendRequest(long requestId, uint data, uint size) => PsnAccount.Current.ServerError;
    [Hle] int sceNpWebApiSendRequest2(long requestId, uint data, uint size, uint respInfo) => PsnAccount.Current.ServerError;
    [Hle] int sceNpWebApiSendMultipartRequest(long requestId, int partIndex, uint data, uint size) => PsnAccount.Current.ServerError;
    [Hle] int sceNpWebApiSendMultipartRequest2(long requestId, int partIndex, uint data, uint size) => PsnAccount.Current.ServerError;
    [Hle] int sceNpWebApiReadData(long requestId, uint data, uint size) => PsnAccount.Current.ServerError;
    [Hle] int sceNpWebApiGetHttpStatusCode(long requestId, uint status) => PsnAccount.Current.ServerError;
    [Hle] int sceNpWebApiGetHttpResponseHeaderValueLength(long requestId, uint name, uint len) => PsnAccount.Current.ServerError;
    [Hle] int sceNpWebApiGetHttpResponseHeaderValue(long requestId, uint name, uint value, uint len) => PsnAccount.Current.ServerError;
    [Hle] int sceNpWebApiGetErrorCode(long requestId, uint err) => PsnAccount.Current.ServerError;
    [Hle] int sceNpWebApiGetMemoryPoolStats(int libCtxId, uint stats) { if (stats != 0) GuestMemory.Fill(stats, 0, 16); return 0; }
    [Hle] int sceNpWebApiCheckCallback(int libCtxId) => 0;

    [Hle] int sceNpWebApiCreatePushEventFilter(int libCtxId) => NewId();
    [Hle] int sceNpWebApiDeletePushEventFilter(int libCtxId, int filterId) => 0;
    [Hle] int sceNpWebApiCreateServicePushEventFilter(int libCtxId) => NewId();
    [Hle] int sceNpWebApiDeleteServicePushEventFilter(int libCtxId, int filterId) => 0;
    [Hle] int sceNpWebApiCreateExtdPushEventFilter(int libCtxId) => NewId();
    [Hle] int sceNpWebApiDeleteExtdPushEventFilter(int libCtxId, int filterId) => 0;
    [Hle] int sceNpWebApiRegisterPushEventCallback(int libCtxId) => NewId();
    [Hle] int sceNpWebApiUnregisterPushEventCallback(int libCtxId, int cbId) => 0;
    [Hle] int sceNpWebApiRegisterServicePushEventCallback(int libCtxId) => NewId();
    [Hle] int sceNpWebApiUnregisterServicePushEventCallback(int libCtxId, int cbId) => 0;
    [Hle] int sceNpWebApiRegisterExtdPushEventCallback(int libCtxId) => NewId();
    [Hle] int sceNpWebApiUnregisterExtdPushEventCallback(int libCtxId, int cbId) => 0;

    /// <summary>The WebAPI npId field is Base64 of "onlineId@suffix": the online id becomes a valid SceNpId (the
    /// suffix-to-opt mapping is unconfirmed and left zero).</summary>
    [Hle]
    int sceNpWebApiUtilityParseNpId(string json, uint npId)
    {
        if (npId == 0) return ERR_INVALID_ARGUMENT;
        string text;
        try { text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(json.Trim().Trim('"'))); }
        catch (FormatException) { return ERR_INVALID_ARGUMENT; }
        int at = text.IndexOf('@');
        string online = at >= 0 ? text[..at] : text;
        GuestMemory.Fill(npId, 0, 0x24);
        var bytes = System.Text.Encoding.UTF8.GetBytes(online);
        bytes.AsSpan(0, Math.Min(16, bytes.Length)).CopyTo(GuestMemory.Span(npId, 16));
        GuestMemory.Write(npId + 0x1C, (byte)1);
        return 0;
    }
}

/// <summary>SceNpWebApiCommonDialog: invitation and game custom data dialogs. Without an account they finish at once
/// with SCE_COMMON_DIALOG_RESULT_ABORTED (only the leading result field is written: the layout is unconfirmed).</summary>
public sealed partial class NpWebApiCommonDialog : HleModule
{
    const int ERR_PARAM = unchecked((int)0x80020401), ERR_BUSY = unchecked((int)0x80020404);
    const int StatusNone = 0, StatusFinished = 2, ResultAborted = 2;
    int _invitation, _customData;

    static int Init(ref int status, uint param)
    {
        if (param == 0) return ERR_PARAM;
        if (status != StatusNone) return ERR_BUSY;
        status = StatusFinished;
        return 0;
    }

    static int Result(uint result)
    {
        if (result == 0) return ERR_PARAM;
        GuestMemory.W32(result, ResultAborted);
        return 0;
    }

    [Hle] int sceInvitationDialogInit(uint param) => Init(ref _invitation, param);
    [Hle] int sceInvitationDialogGetStatus() => _invitation;
    [Hle] int sceInvitationDialogGetResult(uint result) => Result(result);
    [Hle] int sceInvitationDialogAbort() { if (_invitation != StatusNone) _invitation = StatusFinished; return 0; }
    [Hle] int sceInvitationDialogTerm() { _invitation = StatusNone; return 0; }

    [Hle] int sceGameCustomDataDialogInit(uint param) => Init(ref _customData, param);
    [Hle] int sceGameCustomDataDialogGetStatus() => _customData;
    [Hle] int sceGameCustomDataDialogGetResult(uint result) => Result(result);
    [Hle] int sceGameCustomDataDialogAbort() { if (_customData != StatusNone) _customData = StatusFinished; return 0; }
    [Hle] int sceGameCustomDataDialogTerm() { _customData = StatusNone; return 0; }
}

/// <summary>SceNpCommerce2 (in-game PS Store): contexts and requests are local; every *Start fails while signed out,
/// so no result buffer ever exists and the parsers only reject their input.</summary>
public sealed partial class NpCommerce2 : HleModule
{
    const int ERR_NOT_INITIALIZED = NpManager.ERR_NOT_INITIALIZED, ERR_INVALID_ARGUMENT = NpManager.ERR_INVALID_ARGUMENT;

    readonly object _lock = new();
    bool _inited;
    uint _nextId = 1;
    readonly HashSet<uint> _ctx = new();
    readonly Dictionary<uint, uint> _reqs = new(); // request -> ctx

    // ---- np_commerce2.suprx's system-side NIDs (FW 3.74), used by Sign Up (NPXS10018) for its PSN store session:
    // settings, a session object with a callback and a work buffer, then requests to the store servers. Their errors are
    // the library's own (0x80550F01 not initialised, 0x80550F03 invalid argument, 0x80550F96 out of range). The
    // settings and objects are local and work; every request that needs the store fails like an unreachable server.
    const int ERR_SYS_INVALID = unchecked((int)0x80550F03), ERR_SYS_RANGE = unchecked((int)0x80550F96);

    /// <summary>Settings (id, value, size...): NID_25C58D33 (ptr, n), NID_AA090F5E / NID_BC89C4D3 (id 2 / 3..5, ptr, 7),
    /// NID_8ECAE666 (0, index &lt; 9, 7).</summary>
    [Hle("sceNpCommerce2_25C58D33", Nid = 0x25C58D33, Library = "SceNpCommerce2")] int sceNpCommerce2SysSetParam0(uint p, int n) => 0;
    [Hle("sceNpCommerce2_AA090F5E", Nid = 0xAA090F5E, Library = "SceNpCommerce2")] int sceNpCommerce2SysSetParam1(int id, uint p, int size) => 0;
    [Hle("sceNpCommerce2_BC89C4D3", Nid = 0xBC89C4D3, Library = "SceNpCommerce2")] int sceNpCommerce2SysSetParam2(int id, uint p, int size) => 0;
    [Hle("sceNpCommerce2_8ECAE666", Nid = 0x8ECAE666, Library = "SceNpCommerce2")]
    int sceNpCommerce2SysSetParam3(int id, uint index, int size) => index >= 9 ? ERR_SYS_RANGE : 0;

    uint NewSysObject(uint outId)
    {
        if (outId == 0) return unchecked((uint)ERR_SYS_INVALID);
        lock (_lock) GuestMemory.W32(outId, _nextId++);
        return 0;
    }

    /// <summary>(int, uint *id): a session object. (int, void *callback): its callback. (int, void *buf, uint size,
    /// uint *id): a work object over the caller's buffer.</summary>
    [Hle("sceNpCommerce2_749C8571", Nid = 0x749C8571, Library = "SceNpCommerce2")] int sceNpCommerce2SysCreateSession(int a, uint outId) => (int)NewSysObject(outId);
    [Hle("sceNpCommerce2_D09A38DA", Nid = 0xD09A38DA, Library = "SceNpCommerce2")] int sceNpCommerce2SysSetCallback(int a, uint cb) => 0;
    [Hle("sceNpCommerce2_45ACE466", Nid = 0x45ACE466, Library = "SceNpCommerce2")]
    int sceNpCommerce2SysCreateWork(int a, uint buf, uint size, uint outId) => buf == 0 || size == 0 ? ERR_SYS_INVALID : (int)NewSysObject(outId);

    /// <summary>The requests to the store (NID_86405E79 start, NID_4C406B2A, NID_5AB115CE, NID_5D8B5B59, NID_BFDC7B9F,
    /// NID_C7B04A45): the servers cannot be reached.</summary>
    [Hle("sceNpCommerce2_86405E79", Nid = 0x86405E79, Library = "SceNpCommerce2")] int sceNpCommerce2SysStart(uint a, uint b) => PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_4C406B2A", Nid = 0x4C406B2A, Library = "SceNpCommerce2")] int sceNpCommerce2SysRequest1(uint a, uint b) => PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_5AB115CE", Nid = 0x5AB115CE, Library = "SceNpCommerce2")] int sceNpCommerce2SysRequest2(uint a, uint b) => PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_5D8B5B59", Nid = 0x5D8B5B59, Library = "SceNpCommerce2")] int sceNpCommerce2SysRequest3(uint a, uint b) => PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_BFDC7B9F", Nid = 0xBFDC7B9F, Library = "SceNpCommerce2")] int sceNpCommerce2SysRequest4(uint a, uint b) => PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_C7B04A45", Nid = 0xC7B04A45, Library = "SceNpCommerce2")] int sceNpCommerce2SysRequest5(uint a, uint b) => PsnAccount.Current.ServerError;

    // ---- the PS4 Link's NIDs (np_commerce2.suprx FW 3.74). Two go to the shell's store service (NID_624B7A50 (void),
    // NID_145137BE (int)): the service is there and accepts. Six act on a store session object by id (requests to the
    // servers): unreachable. Six parse a server result into the caller's record: no result ever exists, so they only
    // check their arguments and reject the input. NID_71ADF5A6 reads a session's state word: none yet (0).
    [Hle("sceNpCommerce2_624B7A50", Nid = 0x624B7A50, Library = "SceNpCommerce2")] int sceNpCommerce2SysServiceNotify0() => 0;
    [Hle("sceNpCommerce2_145137BE", Nid = 0x145137BE, Library = "SceNpCommerce2")] int sceNpCommerce2SysServiceNotify1(int a) => 0;

    [Hle("sceNpCommerce2_2DC2A0F9", Nid = 0x2DC2A0F9, Library = "SceNpCommerce2")] int sceNpCommerce2SysSessionRequest0(uint id) => PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_C737A929", Nid = 0xC737A929, Library = "SceNpCommerce2")]
    int sceNpCommerce2SysSessionRequest1(uint id, uint p) => p == 0 ? ERR_SYS_INVALID : PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_4D00AA32", Nid = 0x4D00AA32, Library = "SceNpCommerce2")]
    int sceNpCommerce2SysSessionRequest2(uint id, uint p) => p == 0 ? ERR_SYS_INVALID : PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_34194245", Nid = 0x34194245, Library = "SceNpCommerce2")]
    int sceNpCommerce2SysSessionRequest3(uint id, uint a, uint b, uint c, uint d, uint e) =>
        a == 0 || b == 0 || c == 0 || d == 0 || e == 0 ? ERR_SYS_INVALID : PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_4329A980", Nid = 0x4329A980, Library = "SceNpCommerce2")]
    int sceNpCommerce2SysSessionRequest4(uint a, uint b, uint c, uint d) => PsnAccount.Current.ServerError;
    [Hle("sceNpCommerce2_7E3C9196", Nid = 0x7E3C9196, Library = "SceNpCommerce2")]
    int sceNpCommerce2SysSessionRequest5(uint a, uint b, uint c, uint d) => PsnAccount.Current.ServerError;

    [Hle("sceNpCommerce2_71ADF5A6", Nid = 0x71ADF5A6, Library = "SceNpCommerce2")]
    int sceNpCommerce2SysGetSessionState(uint id, uint state) { if (state != 0) GuestMemory.W32(state, 0); return 0; }

    [Hle("sceNpCommerce2_1BFF9C73", Nid = 0x1BFF9C73, Library = "SceNpCommerce2")]
    int sceNpCommerce2SysParse0(uint rec, uint result, uint size) => ERR_SYS_INVALID; // all three required, and no result exists
    [Hle("sceNpCommerce2_64E27331", Nid = 0x64E27331, Library = "SceNpCommerce2")] int sceNpCommerce2SysParse1(uint rec, uint a, uint result) => ERR_SYS_INVALID;
    [Hle("sceNpCommerce2_499732CA", Nid = 0x499732CA, Library = "SceNpCommerce2")] int sceNpCommerce2SysParse2(uint result, uint rec) => ERR_SYS_INVALID;
    [Hle("sceNpCommerce2_1A53A505", Nid = 0x1A53A505, Library = "SceNpCommerce2")] int sceNpCommerce2SysParse3(uint result, uint rec) => ERR_SYS_INVALID;
    [Hle("sceNpCommerce2_C1542070", Nid = 0xC1542070, Library = "SceNpCommerce2")] int sceNpCommerce2SysParse4(uint result, uint rec) => ERR_SYS_INVALID;
    [Hle("sceNpCommerce2_9B444B7C", Nid = 0x9B444B7C, Library = "SceNpCommerce2")] int sceNpCommerce2SysParse5(uint a, uint b, uint c, uint d) => ERR_SYS_INVALID;

    [Hle] int sceNpCommerce2Init() { lock (_lock) _inited = true; return 0; }
    [Hle] int sceNpCommerce2Term() { lock (_lock) { _inited = false; _ctx.Clear(); _reqs.Clear(); } return 0; }

    // two arguments (MotorStorm RC: r0 = version 2, r1 = &ctxId, r2 left over from the previous call); reading the
    // id pointer from the stack wrote the new id over a live object of the caller (its vtable became 1)
    [Hle]
    int sceNpCommerce2CreateCtx(uint version, Ptr<uint> ctxId)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (ctxId.IsNull) return ERR_INVALID_ARGUMENT;
            uint id = _nextId++;
            _ctx.Add(id);
            ctxId.Value = id;
            return 0;
        }
    }

    [Hle]
    int sceNpCommerce2DestroyCtx(uint ctxId)
    {
        lock (_lock)
        {
            if (!_ctx.Remove(ctxId)) return ERR_INVALID_ARGUMENT;
            foreach (var r in _reqs.Where(kv => kv.Value == ctxId).Select(kv => kv.Key).ToList()) _reqs.Remove(r);
            return 0;
        }
    }

    int CreateReq(uint ctxId, Ptr<uint> reqId)
    {
        lock (_lock)
        {
            if (!_inited) return ERR_NOT_INITIALIZED;
            if (!_ctx.Contains(ctxId) || reqId.IsNull) return ERR_INVALID_ARGUMENT;
            uint id = _nextId++;
            _reqs[id] = ctxId;
            reqId.Value = id;
            return 0;
        }
    }

    [Hle] int sceNpCommerce2CreateSessionCreateReq(uint ctxId, Ptr<uint> reqId) => CreateReq(ctxId, reqId);
    [Hle] int sceNpCommerce2GetCategoryContentsCreateReq(uint ctxId, Ptr<uint> reqId) => CreateReq(ctxId, reqId);
    [Hle] int sceNpCommerce2GetProductInfoCreateReq(uint ctxId, Ptr<uint> reqId) => CreateReq(ctxId, reqId);
    [Hle] int sceNpCommerce2GetProductInfoListCreateReq(uint ctxId, Ptr<uint> reqId) => CreateReq(ctxId, reqId);
    [Hle(Nid = 0x9DF3CBCC)] int sceNpCommerce2GetDrmContentInfoCreateReq(uint ctxId, Ptr<uint> reqId) => CreateReq(ctxId, reqId);
    [Hle] int sceNpCommerce2AbortReq(uint reqId) => 0;
    [Hle] int sceNpCommerce2DestroyReq(uint reqId) { lock (_lock) _reqs.Remove(reqId); return 0; }

    [Hle] int sceNpCommerce2CreateSessionStart(uint reqId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2CreateSessionGetResult(uint reqId, uint info) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2GetSessionInfo(uint ctxId, uint info) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2GetCategoryContentsStart(uint reqId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2GetCategoryContentsGetResult(uint reqId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2GetProductInfoStart(uint reqId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2GetProductInfoGetResult(uint reqId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2GetProductInfoListStart(uint reqId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2GetProductInfoListGetResult(uint reqId) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2GetPrice(uint ctxId, uint buf, uint len, uint price) => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2StartEmptyStoreCheck() => PsnAccount.Current.ServerError;
    [Hle] int sceNpCommerce2StopEmptyStoreCheck() => 0;

    // parsers over server data: unreachable offline (no data), so they reject their input
    [Hle] int sceNpCommerce2InitGetCategoryContentsResult(uint result, uint data, uint size) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2DestroyGetCategoryContentsResult(uint result) => 0;
    [Hle] int sceNpCommerce2GetCategoryInfo(uint result, uint info) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2GetContentInfo(uint result, uint index, uint info) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2GetCategoryInfoFromContentInfo(uint content, uint info) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2GetGameProductInfoFromContentInfo(uint content, uint info) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2InitGetProductInfoResult(uint result, uint data, uint size) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2DestroyGetProductInfoResult(uint result) => 0;
    [Hle] int sceNpCommerce2GetGameProductInfo(uint result, uint info) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2InitGetProductInfoListResult(uint result, uint data, uint size) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2DestroyGetProductInfoListResult(uint result) => 0;
    [Hle] int sceNpCommerce2GetGameProductInfoFromGetProductInfoListResult(uint result, uint index, uint info) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2GetGameSkuInfoFromGameProductInfo(uint product, uint index, uint info) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2GetContentRatingInfoFromGameProductInfo(uint product, uint info) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2GetContentRatingInfoFromCategoryInfo(uint category, uint info) => ERR_INVALID_ARGUMENT;
    [Hle] int sceNpCommerce2GetContentRatingDescriptor(uint rating, uint index, uint desc) => ERR_INVALID_ARGUMENT;

    [Hle] int sceNpCommerce2ShowPsStoreIcon(int pos) => 0;
    [Hle] int sceNpCommerce2HidePsStoreIcon() => 0;
}

/// <summary>SceNpDrmPackage: the running title was installed from a package.</summary>
public sealed partial class NpDrmPackage : HleModule
{
    [Hle] int sceNpDrmPackageIsGameExist() => 1;
}

/// <summary>SceNpSnsFacebook (the Facebook link of the system software, FW 3.74): the library sets itself up locally;
/// without a linked Facebook account (none can be linked offline) the access tokens are never there.</summary>
public sealed partial class NpSnsFacebook : HleModule
{
    const int ERR_INVALID_ARGUMENT = unchecked((int)0x80553003), ERR_NOT_SIGNED_IN = unchecked((int)0x80553002);
    bool _inited;
    int _nextReq = 1;

    [Hle] int sceNpSnsFacebookInit(uint param) { _inited = true; return 0; }
    [Hle] int sceNpSnsFacebookTerm() { _inited = false; return 0; }
    [Hle] int sceNpSnsFacebookCreateRequest() => Interlocked.Increment(ref _nextReq);
    [Hle] int sceNpSnsFacebookDeleteRequest(int req) => 0;
    [Hle] int sceNpSnsFacebookAbortRequest(int req) => 0;
    [Hle] int sceNpSnsFacebookGetAccessToken(int req, uint param, uint result) => param == 0 || result == 0 ? ERR_INVALID_ARGUMENT : ERR_NOT_SIGNED_IN;
    [Hle] int sceNpSnsFacebookGetLongAccessToken(int req, uint param, uint result) => param == 0 || result == 0 ? ERR_INVALID_ARGUMENT : ERR_NOT_SIGNED_IN;
}