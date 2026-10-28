using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Shared state machine of the NP "community" libraries (Score, Tus/Tss, Lookup, WordFilter): title context ->
/// request -> transaction (sync or async) -> poll/wait/abort -> delete. No PSN server is ever reachable, so every
/// transaction completes immediately with <see cref="OfflineResult"/>: synchronous calls return it, asynchronous calls
/// return 0 and report it through Poll/Wait (which never report RUNNING).
/// </summary>
public sealed class NpCommunity(string name)
{
    public const int ALREADY_INITIALIZED = unchecked((int)0x80550701), NOT_INITIALIZED = unchecked((int)0x80550702),
        INVALID_ARGUMENT = unchecked((int)0x80550704), NO_LOGIN = unchecked((int)0x80550705),
        TOO_MANY_OBJECTS = unchecked((int)0x80550706), ABORTED = unchecked((int)0x80550707),
        INSUFFICIENT_ARGUMENT = unchecked((int)0x8055070C), INVALID_ID = unchecked((int)0x8055070E),
        TRANSACTION_ALREADY_END = unchecked((int)0x80550712), NO_RESOURCE = unchecked((int)0x80550716);

    /// <summary>Result of every server transaction: signed in, the server is unreachable (SCE_HTTP_ERROR_NETWORK, see
    /// PsnAccount); signed out, NO_LOGIN (the exact firmware code is unconfirmed; the
    /// community "no login" error is the family's equivalent of SCE_NP_ERROR_SIGNED_OUT).</summary>
    public static int OfflineResult => PsnAccount.Current.SignedIn ? PsnAccount.ServerUnreachable : NO_LOGIN;

    public const int MaxObjects = 32;

    sealed class Req { public int Ctx; public int? Result; }

    readonly object _lock = new();
    bool _inited;
    int _nextId = 1;
    readonly HashSet<int> _ctx = new();
    readonly Dictionary<int, Req> _reqs = new();

    public bool Inited { get { lock (_lock) return _inited; } }

    public int Init()
    {
        lock (_lock)
        {
            if (_inited) return ALREADY_INITIALIZED;
            _inited = true;
            _ctx.Clear();
            _reqs.Clear();
            return 0;
        }
    }

    public int Term()
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            _ctx.Clear();
            _reqs.Clear();
            _inited = false;
            return 0;
        }
    }

    public int CreateTitleCtx(uint commId)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            if (commId == 0) return INSUFFICIENT_ARGUMENT;
            if (_ctx.Count >= MaxObjects) return TOO_MANY_OBJECTS;
            int id = _nextId++;
            _ctx.Add(id);
            Log.Info($"{name}: title context {id} for {GuestMemory.ReadCString(commId, 9)}_{GuestMemory.Read<byte>(commId + 10):D2} (offline)");
            return id;
        }
    }

    public int DeleteTitleCtx(int id)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            if (!_ctx.Remove(id)) return INVALID_ID;
            foreach (var r in _reqs.Where(kv => kv.Value.Ctx == id).Select(kv => kv.Key).ToList()) _reqs.Remove(r);
            return 0;
        }
    }

    public int CreateRequest(int ctxId)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            if (!_ctx.Contains(ctxId)) return INVALID_ID;
            if (_reqs.Count >= MaxObjects) return TOO_MANY_OBJECTS;
            int id = _nextId++;
            _reqs[id] = new Req { Ctx = ctxId };
            return id;
        }
    }

    public int DeleteRequest(int id)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            return _reqs.Remove(id) ? 0 : INVALID_ID;
        }
    }

    public int Abort(int id)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            if (!_reqs.TryGetValue(id, out var r)) return INVALID_ID;
            r.Result ??= ABORTED;
            return 0;
        }
    }

    /// <summary>Set*Timeout / SetPlayerCharacterId: accepts a title context or a request id.</summary>
    public int ValidateId(int id)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            return _ctx.Contains(id) || _reqs.ContainsKey(id) ? 0 : INVALID_ID;
        }
    }

    int Begin(int reqId)
    {
        if (!_inited) return NOT_INITIALIZED;
        if (!_reqs.TryGetValue(reqId, out var r)) return INVALID_ID;
        if (r.Result != null) return TRANSACTION_ALREADY_END;
        r.Result = OfflineResult;
        return 0;
    }

    /// <summary>Synchronous transaction: fails with the offline result (output buffers are not touched).</summary>
    public int Sync(int reqId)
    {
        lock (_lock)
        {
            int e = Begin(reqId);
            return e != 0 ? e : OfflineResult;
        }
    }

    /// <summary>Asynchronous transaction: started (0); Poll/Wait report the offline result.</summary>
    public int StartAsync(int reqId)
    {
        lock (_lock) return Begin(reqId);
    }

    /// <summary>PollAsync/WaitAsync: 0 = finished with *result written. Never RUNNING.</summary>
    public int Poll(int reqId, uint result)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            if (!_reqs.TryGetValue(reqId, out var r)) return INVALID_ID;
            if (result == 0 || r.Result == null) return INVALID_ARGUMENT;
            GuestMemory.W32(result, (uint)r.Result.Value);
            return 0;
        }
    }
}

/// <summary>SceNpScore (online rankings) over <see cref="NpCommunity"/>: offline, every transaction fails.</summary>
public sealed class NpScore : HleModule
{
    readonly NpCommunity _lib = new("NpScore");
    internal NpCommunity Lib => _lib;

    [Hle] int sceNpScoreInit(int prio, int affinity, uint opt) => _lib.Init();
    [Hle] int sceNpScoreTerm() => _lib.Term();
    [Hle] int sceNpScoreCreateTitleCtx(uint commId, uint passphrase, uint selfNpId) => _lib.CreateTitleCtx(commId);
    [Hle] int sceNpScoreDeleteTitleCtx(int id) => _lib.DeleteTitleCtx(id);
    [Hle] int sceNpScoreCreateRequest(int ctxId) => _lib.CreateRequest(ctxId);
    [Hle] int sceNpScoreDeleteRequest(int reqId) => _lib.DeleteRequest(reqId);
    [Hle] int sceNpScoreAbortRequest(int reqId) => _lib.Abort(reqId);
    [Hle] int sceNpScorePollAsync(int reqId, uint result) => _lib.Poll(reqId, result);
    [Hle] int sceNpScoreWaitAsync(int reqId, uint result) => _lib.Poll(reqId, result);
    [Hle] int sceNpScoreSetTimeout(int id) => _lib.ValidateId(id);
    [Hle] int sceNpScoreSetPlayerCharacterId(int id, int pcId) => _lib.ValidateId(id);
    [Hle] int sceNpScoreChangeModeForOtherSaveDataOwners() => _lib.Inited ? 0 : NpCommunity.NOT_INITIALIZED;

    [Hle] int sceNpScoreRecordScoreAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpScoreGetRankingByRangeAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpScoreGetRankingByNpIdPcIdAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpScoreGetBoardInfoAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpScoreRecordGameDataAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpScoreGetGameDataAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpScoreGetRankingByNpIdAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpScoreGetFriendsRankingAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpScoreCensorCommentAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpScoreSanitizeCommentAsync(int reqId) => _lib.StartAsync(reqId);

    [Hle] int sceNpScoreRecordScore(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpScoreGetBoardInfo(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpScoreRecordGameData(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpScoreGetGameData(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpScoreGetRankingByNpId(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpScoreGetRankingByNpIdPcId(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpScoreGetRankingByRange(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpScoreGetFriendsRanking(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpScoreCensorComment(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpScoreSanitizeComment(int reqId) => _lib.Sync(reqId);
}

/// <summary>SceNpTus (title user storage) and SceNpTss (title small storage), sharing one request space.</summary>
public sealed class NpTus : HleModule
{
    readonly NpCommunity _lib = new("NpTus");
    internal NpCommunity Lib => _lib;

    [Hle] int sceNpTusInit(int prio, int affinity, uint opt) => _lib.Init();
    [Hle] int sceNpTusTerm() => _lib.Term();
    [Hle] int sceNpTusCreateTitleCtx(uint commId, uint passphrase, uint selfNpId) => _lib.CreateTitleCtx(commId);
    [Hle] int sceNpTusDeleteTitleCtx(int id) => _lib.DeleteTitleCtx(id);
    [Hle] int sceNpTusCreateRequest(int ctxId) => _lib.CreateRequest(ctxId);
    [Hle] int sceNpTusDeleteRequest(int reqId) => _lib.DeleteRequest(reqId);
    [Hle] int sceNpTusAbortRequest(int reqId) => _lib.Abort(reqId);
    [Hle] int sceNpTusPollAsync(int reqId, uint result) => _lib.Poll(reqId, result);
    [Hle] int sceNpTusWaitAsync(int reqId, uint result) => _lib.Poll(reqId, result);
    [Hle] int sceNpTusSetTimeout(int id) => _lib.ValidateId(id);
    [Hle] int sceNpTusChangeModeForOtherSaveDataOwners() => _lib.Inited ? 0 : NpCommunity.NOT_INITIALIZED;

    [Hle] int sceNpTusGetDataAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusSetDataAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetMultiSlotDataStatusAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusDeleteMultiSlotDataAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusSetMultiSlotVariableAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusSetMultiSlotVariableVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetMultiSlotVariableAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetMultiSlotVariableVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetMultiUserVariableAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetMultiUserVariableVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetFriendsVariableAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusAddAndGetVariableAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusAddAndGetVariableVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusTryAndSetVariableAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusTryAndSetVariableVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusDeleteMultiSlotVariableAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusDeleteMultiSlotVariableVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusSetDataVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetDataVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetMultiSlotDataStatusVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetMultiUserDataStatusAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetMultiUserDataStatusVUserAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusGetFriendsDataStatusAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTusDeleteMultiSlotDataVUserAsync(int reqId) => _lib.StartAsync(reqId);

    [Hle] int sceNpTusSetMultiSlotVariable(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusSetMultiSlotVariableVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetMultiSlotVariable(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetMultiSlotVariableVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetMultiUserVariable(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetMultiUserVariableVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetFriendsVariable(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusAddAndGetVariable(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusAddAndGetVariableVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusTryAndSetVariable(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusTryAndSetVariableVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusDeleteMultiSlotVariable(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusDeleteMultiSlotVariableVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusSetData(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusSetDataVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetData(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetDataVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetMultiSlotDataStatus(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetMultiSlotDataStatusVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetMultiUserDataStatus(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetMultiUserDataStatusVUser(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusGetFriendsDataStatus(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusDeleteMultiSlotData(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTusDeleteMultiSlotDataVUser(int reqId) => _lib.Sync(reqId);

    // ---------------------------------------------------------------- TSS (title small storage)

    /// <summary>int (reqId, void *data, SceSize maxSize, SceSize *contentLength, const void *option) (Vita3K).</summary>
    [Hle]
    int sceNpTssGetSmallStorageAsync(int reqId, uint data, uint maxSize, uint contentLength)
    {
        if (data == 0 || contentLength == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        GuestMemory.W32(contentLength, 0);
        return _lib.StartAsync(reqId);
    }

    [Hle]
    int sceNpTssGetSmallStorage(int reqId, uint data, uint maxSize, uint contentLength)
    {
        if (data == 0 || contentLength == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        GuestMemory.W32(contentLength, 0);
        return _lib.Sync(reqId);
    }

    /// <summary>SceNpTssDataStatus (0x10): +0 lastModified tick, +8 statusCodeType, +0xC contentLength.</summary>
    int TssGetData(int reqId, int slotId, uint dataStatus, uint dataStatusSize, bool async)
    {
        if (dataStatus == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (dataStatusSize != 16 || slotId is < 0 or > 15) return NpCommunity.INVALID_ARGUMENT;
        GuestMemory.Fill(dataStatus, 0, 16);
        return async ? _lib.StartAsync(reqId) : _lib.Sync(reqId);
    }

    [Hle] int sceNpTssGetData(int reqId, int slotId, uint dataStatus, uint dataStatusSize) => TssGetData(reqId, slotId, dataStatus, dataStatusSize, false);
    [Hle] int sceNpTssGetDataAsync(int reqId, int slotId, uint dataStatus, uint dataStatusSize) => TssGetData(reqId, slotId, dataStatus, dataStatusSize, true);
    [Hle] int sceNpTssGetDataNoLimit(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTssGetDataNoLimitAsync(int reqId) => _lib.StartAsync(reqId);
    [Hle] int sceNpTssGetStorage(int reqId) => _lib.Sync(reqId);
    [Hle] int sceNpTssGetStorageAsync(int reqId) => _lib.StartAsync(reqId);
}

/// <summary>SceNpUtility: Lookup and WordFilter (community model) and BandwidthTest.</summary>
public sealed class NpUtility : HleModule
{
    readonly NpCommunity _lookup = new("NpLookup"), _filter = new("NpWordFilter");
    internal NpCommunity Lookup => _lookup;

    [Hle] int sceNpLookupInit(int usesAsync, int threadPrio, int cpuAffinity, uint opt) => _lookup.Init();
    [Hle] int sceNpLookupTerm() => _lookup.Term();
    [Hle] int sceNpLookupCreateTitleCtx(uint commId, uint npId) => _lookup.CreateTitleCtx(commId);
    [Hle] int sceNpLookupDeleteTitleCtx(int id) => _lookup.DeleteTitleCtx(id);
    [Hle] int sceNpLookupCreateRequest(int ctx) => _lookup.CreateRequest(ctx);
    [Hle] int sceNpLookupDeleteRequest(int id) => _lookup.DeleteRequest(id);
    [Hle] int sceNpLookupAbortRequest(int id) => _lookup.Abort(id);
    [Hle] int sceNpLookupSetTimeout(int id) => _lookup.ValidateId(id);
    [Hle] int sceNpLookupPollAsync(int id, uint result) => _lookup.Poll(id, result);
    [Hle] int sceNpLookupWaitAsync(int id, uint result) => _lookup.Poll(id, result);
    [Hle] int sceNpLookupUserProfileAsync(int id) => _lookup.StartAsync(id);
    [Hle] int sceNpLookupUserProfile(int id) => _lookup.Sync(id);
    [Hle] int sceNpLookupNpId(int id) => _lookup.Sync(id);
    [Hle] int sceNpLookupNpIdAsync(int id) => _lookup.StartAsync(id);
    [Hle] int sceNpLookupAvatarImage(int id) => _lookup.Sync(id);
    [Hle] int sceNpLookupAvatarImageAsync(int id) => _lookup.StartAsync(id);

    [Hle] int sceNpWordFilterInit(int prio, int affinity, uint opt) => _filter.Init();
    [Hle] int sceNpWordFilterTerm() => _filter.Term();
    [Hle] int sceNpWordFilterCreateTitleCtx(uint commId, uint passphrase, uint selfNpId) => _filter.CreateTitleCtx(commId);
    [Hle] int sceNpWordFilterDeleteTitleCtx(int id) => _filter.DeleteTitleCtx(id);
    [Hle] int sceNpWordFilterCreateRequest(int ctx) => _filter.CreateRequest(ctx);
    [Hle] int sceNpWordFilterDeleteRequest(int id) => _filter.DeleteRequest(id);
    [Hle] int sceNpWordFilterAbortRequest(int id) => _filter.Abort(id);
    [Hle] int sceNpWordFilterSetTimeout(int id) => _filter.ValidateId(id);
    [Hle] int sceNpWordFilterPollAsync(int id, uint result) => _filter.Poll(id, result);
    [Hle] int sceNpWordFilterWaitAsync(int id, uint result) => _filter.Poll(id, result);
    [Hle] int sceNpWordFilterCensorComment(int id) => _filter.Sync(id);
    [Hle] int sceNpWordFilterCensorCommentAsync(int id) => _filter.StartAsync(id);
    [Hle] int sceNpWordFilterSanitizeComment(int id) => _filter.Sync(id);
    [Hle] int sceNpWordFilterSanitizeCommentAsync(int id) => _filter.StartAsync(id);

    // BandwidthTest measures bandwidth to Sony's servers: never starts offline.
    [Hle] int sceNpBandwidthTestInitStart() => PsnAccount.Current.ServerError;
    [Hle] int sceNpBandwidthTestGetStatus() => 0;
    [Hle] int sceNpBandwidthTestShutdown(uint result) => 0;
    [Hle] int sceNpBandwidthTestAbort() => 0;
}
