using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceNpSignaling (P2P NAT traversal): contexts and options work locally, no peer ever connects.</summary>
public sealed class NpSignaling : HleModule
{
    const int ERR_NOT_INITIALIZED = NpManager.ERR_NOT_INITIALIZED, ERR_INVALID_ARGUMENT = NpManager.ERR_INVALID_ARGUMENT,
        ERR_SIGNED_OUT = NpManager.ERR_SIGNED_OUT;

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

    [Hle] int sceNpSignalingActivateConnection(int ctxId, uint npId, uint connId) => ERR_SIGNED_OUT;
    [Hle] int sceNpSignalingDeactivateConnection(int ctxId, int connId) => 0;
    [Hle] int sceNpSignalingTerminateConnection(int ctxId, int connId) => 0;
    [Hle] int sceNpSignalingGetConnectionStatus(int ctxId, int connId, Ptr<int> status) { status.TrySet(0); return ERR_SIGNED_OUT; } // INACTIVE
    [Hle] int sceNpSignalingGetConnectionInfo(int ctxId, int connId, int code, uint info) => ERR_SIGNED_OUT;
    [Hle] int sceNpSignalingGetConnectionFromNpId(int ctxId, uint npId, uint connId) => ERR_SIGNED_OUT;
    [Hle] int sceNpSignalingGetConnectionFromPeerAddress(int ctxId, uint addr, uint port, uint connId) => ERR_SIGNED_OUT;
    [Hle] int sceNpSignalingGetLocalNetInfo(int ctxId, uint info) => ERR_SIGNED_OUT;
    [Hle] int sceNpSignalingGetPeerNetInfo(int ctxId, uint npId, uint reqId) => ERR_SIGNED_OUT;
    [Hle] int sceNpSignalingCancelPeerNetInfo(int ctxId, int reqId) => 0;
    [Hle] int sceNpSignalingGetPeerNetInfoResult(int ctxId, int reqId, uint info) => ERR_SIGNED_OUT;
    [Hle] int sceNpSignalingGetMemoryInfo(uint info) { if (info != 0) GuestMemory.Fill(info, 0, 12); return 0; }
}

/// <summary>SceNpWebApi (PSN REST client): local handles/requests/filters work, sending fails while signed out and push
/// callbacks never fire.</summary>
public sealed class NpWebApi : HleModule
{
    const int ERR_INVALID_ARGUMENT = NpManager.ERR_INVALID_ARGUMENT, ERR_SIGNED_OUT = NpManager.ERR_SIGNED_OUT;

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
    [Hle] int sceNpWebApiSendRequest(long requestId, uint data, uint size) => ERR_SIGNED_OUT;
    [Hle] int sceNpWebApiSendRequest2(long requestId, uint data, uint size, uint respInfo) => ERR_SIGNED_OUT;
    [Hle] int sceNpWebApiSendMultipartRequest(long requestId, int partIndex, uint data, uint size) => ERR_SIGNED_OUT;
    [Hle] int sceNpWebApiSendMultipartRequest2(long requestId, int partIndex, uint data, uint size) => ERR_SIGNED_OUT;
    [Hle] int sceNpWebApiReadData(long requestId, uint data, uint size) => ERR_SIGNED_OUT;
    [Hle] int sceNpWebApiGetHttpStatusCode(long requestId, uint status) => ERR_SIGNED_OUT;
    [Hle] int sceNpWebApiGetHttpResponseHeaderValueLength(long requestId, uint name, uint len) => ERR_SIGNED_OUT;
    [Hle] int sceNpWebApiGetHttpResponseHeaderValue(long requestId, uint name, uint value, uint len) => ERR_SIGNED_OUT;
    [Hle] int sceNpWebApiGetErrorCode(long requestId, uint err) => ERR_SIGNED_OUT;
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
public sealed class NpWebApiCommonDialog : HleModule
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
public sealed class NpCommerce2 : HleModule
{
    const int ERR_NOT_INITIALIZED = NpManager.ERR_NOT_INITIALIZED, ERR_INVALID_ARGUMENT = NpManager.ERR_INVALID_ARGUMENT,
        ERR_SIGNED_OUT = NpManager.ERR_SIGNED_OUT;

    readonly object _lock = new();
    bool _inited;
    uint _nextId = 1;
    readonly HashSet<uint> _ctx = new();
    readonly Dictionary<uint, uint> _reqs = new(); // request -> ctx

    [Hle] int sceNpCommerce2Init() { lock (_lock) _inited = true; return 0; }
    [Hle] int sceNpCommerce2Term() { lock (_lock) { _inited = false; _ctx.Clear(); _reqs.Clear(); } return 0; }

    [Hle]
    int sceNpCommerce2CreateCtx(uint version, uint npId, uint handler, uint arg, Ptr<uint> ctxId)
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

    [Hle] int sceNpCommerce2CreateSessionStart(uint reqId) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2CreateSessionGetResult(uint reqId, uint info) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2GetSessionInfo(uint ctxId, uint info) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2GetCategoryContentsStart(uint reqId) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2GetCategoryContentsGetResult(uint reqId) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2GetProductInfoStart(uint reqId) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2GetProductInfoGetResult(uint reqId) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2GetProductInfoListStart(uint reqId) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2GetProductInfoListGetResult(uint reqId) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2GetPrice(uint ctxId, uint buf, uint len, uint price) => ERR_SIGNED_OUT;
    [Hle] int sceNpCommerce2StartEmptyStoreCheck() => ERR_SIGNED_OUT;
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
public sealed class NpDrmPackage : HleModule
{
    [Hle] int sceNpDrmPackageIsGameExist() => 1;
}
