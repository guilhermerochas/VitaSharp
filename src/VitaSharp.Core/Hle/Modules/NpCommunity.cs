using System.Text;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Shared state machine of the NP "community" libraries (Score, Tus/Tss, Lookup, WordFilter): title context ->
/// request (transaction) -> sync call, or async call + poll/wait/abort -> delete. With the local PSN services
/// (<see cref="PsnAccount.UsesLocalServices"/>) a transaction is answered at once by <see cref="LocalPsn"/>: a synchronous
/// call returns the server's result, an asynchronous one returns 0 and Poll/Wait report it (never RUNNING). Without
/// them (or signed out) every transaction completes with <see cref="OfflineResult"/>.
/// Error codes: Vita3K SceNpTus.cpp (client 0x805507xx, server 0x805508xx, same low byte as the PS3 0x8002a1xx/0x8002a4xx).
/// </summary>
public sealed class NpCommunity(string name)
{
    public const int ALREADY_INITIALIZED = unchecked((int)0x80550701), NOT_INITIALIZED = unchecked((int)0x80550702),
        INVALID_ARGUMENT = unchecked((int)0x80550704), NO_LOGIN = unchecked((int)0x80550705),
        TOO_MANY_OBJECTS = unchecked((int)0x80550706), ABORTED = unchecked((int)0x80550707),
        INSUFFICIENT_ARGUMENT = unchecked((int)0x8055070C), INVALID_ID = unchecked((int)0x8055070E),
        INVALID_ONLINE_ID = unchecked((int)0x8055070F), TRANSACTION_ALREADY_END = unchecked((int)0x80550712),
        INVALID_ALIGNMENT = unchecked((int)0x80550714), NO_RESOURCE = unchecked((int)0x80550716);

    // server errors (what the local server answers)
    public const int SERVER_GAME_RANKING_NOT_FOUND = unchecked((int)0x80550812), SERVER_NOT_BEST_SCORE = unchecked((int)0x80550815),
        SERVER_RANKING_GAME_DATA_MASTER_NOT_FOUND = unchecked((int)0x80550818), SERVER_INVALID_SCORE = unchecked((int)0x80550823),
        SERVER_GAME_DATA_ALREADY_EXISTS = unchecked((int)0x8055082C),
        SERVER_USER_STORAGE_TITLE_MASTER_NOT_FOUND = unchecked((int)0x80550848),
        SERVER_USER_STORAGE_DATA_NOT_FOUND = unchecked((int)0x8055084A), SERVER_CONDITIONS_NOT_SATISFIED = unchecked((int)0x80550873);

    /// <summary>Result of every server transaction without local services: signed in, the server is unreachable
    /// (SCE_HTTP_ERROR_NETWORK, see PsnAccount); signed out, NO_LOGIN (the community "no login" error is the family's
    /// equivalent of SCE_NP_ERROR_SIGNED_OUT).</summary>
    public static int OfflineResult => PsnAccount.Current.SignedIn ? PsnAccount.ServerUnreachable : NO_LOGIN;

    /// <summary>True when transactions are answered by the local PSN services.</summary>
    public static bool Local => PsnAccount.Current.UsesLocalServices;

    public const int MaxObjects = 32;

    /// <summary>A request (transaction id). <see cref="Open"/>: a multi-part transfer (TUS SetData/GetData in chunks) that
    /// accepts more calls; <see cref="State"/> holds its buffer.</summary>
    public sealed class Transaction
    {
        public int Ctx;
        public string CommId = "";
        public int? Result;
        public bool Open;
        public object? State;
    }

    readonly object _lock = new();
    bool _inited;
    int _nextId = 1;
    readonly Dictionary<int, string> _ctx = new();
    readonly Dictionary<int, Transaction> _reqs = new();

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

    /// <summary>SceNpCommunicationId at <paramref name="commId"/> as "NPWR12345_00".</summary>
    public static string ReadCommId(uint commId) => $"{GuestMemory.ReadCString(commId, 9)}_{GuestMemory.Read<byte>(commId + 10):D2}";

    public int CreateTitleCtx(uint commId)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            if (commId == 0) return INSUFFICIENT_ARGUMENT;
            if (_ctx.Count >= MaxObjects) return TOO_MANY_OBJECTS;
            int id = _nextId++;
            string comm = ReadCommId(commId);
            _ctx[id] = comm;
            Log.Info($"{name}: title context {id} for {comm} ({(Local ? "local PSN services" : "offline")})");
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
            if (!_ctx.TryGetValue(ctxId, out var comm)) return INVALID_ID;
            if (_reqs.Count >= MaxObjects) return TOO_MANY_OBJECTS;
            int id = _nextId++;
            _reqs[id] = new Transaction { Ctx = ctxId, CommId = comm };
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
            if (r.Result == null || r.Open) { r.Result = ABORTED; r.Open = false; r.State = null; }
            return 0;
        }
    }

    /// <summary>Set*Timeout / SetPlayerCharacterId: accepts a title context or a request id.</summary>
    public int ValidateId(int id)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            return _ctx.ContainsKey(id) || _reqs.ContainsKey(id) ? 0 : INVALID_ID;
        }
    }

    /// <summary>
    /// Runs one server transaction on request <paramref name="reqId"/>. Local services: <paramref name="work"/> is the
    /// server's answer (it may set <see cref="Transaction.Open"/> to accept another part); otherwise the transaction
    /// fails with <see cref="OfflineResult"/> and the output buffers are not touched. Sync calls return the result,
    /// async calls 0 (the result then comes from <see cref="Poll"/>).
    /// </summary>
    public int Run(int reqId, bool async, Func<Transaction, int> work)
    {
        lock (_lock)
        {
            if (!_inited) return NOT_INITIALIZED;
            if (!_reqs.TryGetValue(reqId, out var t)) return INVALID_ID;
            if (t.Result != null && !t.Open) return TRANSACTION_ALREADY_END;
            int result;
            if (!Local) result = OfflineResult;
            else
            {
                t.Open = false;
                try { result = work(t); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"{name}: local server error: {e.Message}");
                    result = PsnAccount.ServerUnreachable;
                }
            }
            t.Result = result;
            if (result < 0) { t.Open = false; t.State = null; }
            return async ? 0 : result;
        }
    }

    /// <summary>Synchronous transaction answered with <paramref name="localResult"/> by the local server.</summary>
    public int Sync(int reqId, int localResult = 0) => Run(reqId, false, _ => localResult);

    /// <summary>Asynchronous transaction answered with <paramref name="localResult"/> by the local server.</summary>
    public int StartAsync(int reqId, int localResult = 0) => Run(reqId, true, _ => localResult);

    /// <summary>PollAsync/WaitAsync: 0 = finished with *result written. Never RUNNING (the local server answers at once).</summary>
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

    // ---------------------------------------------------------------- shared structure helpers

    /// <summary>The online id of a SceNpId / SceNpOnlineId (data[16] at +0).</summary>
    public static string ReadOnlineId(uint addr)
    {
        var s = GuestMemory.Span(addr, 16);
        int n = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(s[..(n < 0 ? 16 : n)]);
    }

    /// <summary>SceNpOnlineName (52 bytes: data[48], term, dummy[3]).</summary>
    public static void WriteOnlineName(uint addr, string name)
    {
        GuestMemory.Fill(addr, 0, 52);
        var b = Encoding.UTF8.GetBytes(name);
        b.AsSpan(0, Math.Min(b.Length, 48)).CopyTo(GuestMemory.Span(addr, 48));
    }

    /// <summary>Element stride of an output array from its byte size and count; 0 when too small for <paramref name="min"/>.</summary>
    public static uint Stride(uint arraySize, int arrayNum, uint expected, uint min, string what)
    {
        if (arrayNum <= 0) return 0;
        uint stride = arraySize / (uint)arrayNum;
        if (stride < min) return 0;
        if (stride != expected) Log.DebugOnce("np-stride-" + what, $"{what}: element size {stride} (expected {expected})");
        return stride;
    }

    public static string Me => PsnAccount.Current.OnlineId;
}

/// <summary>
/// SceNpScore (online rankings) over <see cref="NpCommunity"/>. With the local PSN services, the boards live in
/// <see cref="ScoreStore"/>: the user's own best score per board and player character is ranked (the boards have no
/// other players), comments, game info and game data are kept. Prototypes follow the PS3/PS4 libraries
/// (RPCS3 sceNp.cpp), structures the PS3 layout (SceNpScoreRankData 128 bytes; element sizes come from the caller's
/// array sizes).
/// </summary>
public sealed class NpScore : HleModule
{
    readonly NpCommunity _lib = new("NpScore");
    internal NpCommunity Lib => _lib;
    internal ScoreStore? StoreOverride;
    ScoreStore Store => StoreOverride ?? Emu.LocalPsn.Score;

    const uint RankDataSize = 128, PlayerRankDataSize = 136, CommentSize = 64, GameInfoSize = 64, VarGameInfoSize = 196;
    const int MaxRange = 100, MaxNpIds = 101;

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

    // ---------------------------------------------------------------- board info

    /// <summary>SceNpScoreBoardInfo (20): rankLimit, updateMode, sortMode, uploadNumLimit, uploadSizeLimit.</summary>
    int GetBoardInfo(int reqId, uint boardId, uint boardInfo, bool async)
    {
        if (boardInfo == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        return _lib.Run(reqId, async, t =>
        {
            var b = Store.GetBoard(t.CommId, boardId);
            uint[] v = [b.RankLimit, b.UpdateMode, b.SortMode, 10, 1 << 20];
            for (int i = 0; i < v.Length; i++) GuestMemory.W32(boardInfo + (uint)i * 4, v[i]);
            return 0;
        });
    }

    [Hle] int sceNpScoreGetBoardInfo(int reqId, uint boardId, uint boardInfo, uint option) => GetBoardInfo(reqId, boardId, boardInfo, false);
    [Hle] int sceNpScoreGetBoardInfoAsync(int reqId, uint boardId, uint boardInfo, uint option) => GetBoardInfo(reqId, boardId, boardInfo, true);

    // ---------------------------------------------------------------- record

    /// <summary>int (reqId, boardId, SceNpScoreValue score, const SceNpScoreComment *comment, const SceNpScoreGameInfo *gameInfo,
    /// SceNpScoreRankNumber *tmpRank, const SceRtcTick *compareDate, void *option). The Vita game info is
    /// {SceSize infoSize; data[189]} (the PS3 one 64 raw bytes: infoSize above 189 means that form).</summary>
    int RecordScore(int reqId, uint boardId, long score, uint comment, uint gameInfo, uint tmpRank, bool async)
    {
        string text = comment != 0 ? GuestMemory.ReadCString(comment, 64) : "";
        byte[] info = [];
        if (gameInfo != 0)
        {
            uint n = GuestMemory.R32(gameInfo);
            info = n <= 189 ? GuestMemory.Span(gameInfo + 4, (int)n).ToArray() : GuestMemory.Span(gameInfo, (int)GameInfoSize).ToArray();
        }
        return _lib.Run(reqId, async, t =>
        {
            bool best = Store.Record(t.CommId, boardId, NpCommunity.Me, 0, score, text, info, out uint rank);
            if (tmpRank != 0) GuestMemory.W32(tmpRank, rank);
            Log.Info($"NpScore: {t.CommId} board {boardId}: score {score} by {NpCommunity.Me} -> rank {rank}{(best ? "" : " (not the best score, kept the old one)")}");
            return best ? 0 : NpCommunity.SERVER_NOT_BEST_SCORE;
        });
    }

    [Hle] int sceNpScoreRecordScore(int reqId, uint boardId, long score, uint comment, uint gameInfo, uint tmpRank, uint compareDate, uint option) =>
        RecordScore(reqId, boardId, score, comment, gameInfo, tmpRank, false);
    [Hle] int sceNpScoreRecordScoreAsync(int reqId, uint boardId, long score, uint comment, uint gameInfo, uint tmpRank, uint compareDate, uint option) =>
        RecordScore(reqId, boardId, score, comment, gameInfo, tmpRank, true);

    /// <summary>int (reqId, boardId, score, SceSize totalSize, SceSize sendSize, const void *data, void *option): the data
    /// may arrive in several calls on the same request.</summary>
    int RecordGameData(int reqId, uint boardId, long score, uint totalSize, uint sendSize, uint data, bool async)
    {
        if (data == 0 && sendSize > 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (sendSize > totalSize) return NpCommunity.INVALID_ARGUMENT;
        byte[] chunk = sendSize > 0 ? GuestMemory.Span(data, (int)sendSize).ToArray() : [];
        return _lib.Run(reqId, async, t =>
        {
            var buf = t.State as List<byte> ?? new List<byte>((int)Math.Min(totalSize, 1u << 24));
            buf.AddRange(chunk);
            if (buf.Count < totalSize) { t.State = buf; t.Open = true; return 0; }
            t.State = null;
            if (Store.GetGameData(t.CommId, boardId, NpCommunity.Me, 0) != null) return NpCommunity.SERVER_GAME_DATA_ALREADY_EXISTS;
            return Store.RecordGameData(t.CommId, boardId, NpCommunity.Me, 0, score, buf.ToArray()) ? 0 : NpCommunity.SERVER_INVALID_SCORE;
        });
    }

    [Hle] int sceNpScoreRecordGameData(int reqId, uint boardId, long score, uint totalSize, uint sendSize, uint data, uint option) =>
        RecordGameData(reqId, boardId, score, totalSize, sendSize, data, false);
    [Hle] int sceNpScoreRecordGameDataAsync(int reqId, uint boardId, long score, uint totalSize, uint sendSize, uint data, uint option) =>
        RecordGameData(reqId, boardId, score, totalSize, sendSize, data, true);

    /// <summary>int (reqId, boardId, const SceNpId *npId, SceSize *totalSize, SceSize recvSize, void *data, void *option):
    /// returns the bytes copied; the rest comes with the next calls on the same request.</summary>
    int GetGameData(int reqId, uint boardId, uint npId, uint totalSize, uint recvSize, uint data, bool async)
    {
        if (npId == 0 || totalSize == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        string owner = NpCommunity.ReadOnlineId(npId);
        return _lib.Run(reqId, async, t =>
        {
            if (t.State is not byte[] rest)
            {
                var blob = Store.GetGameData(t.CommId, boardId, owner, 0);
                if (blob == null) return NpCommunity.SERVER_RANKING_GAME_DATA_MASTER_NOT_FOUND;
                GuestMemory.W32(totalSize, (uint)blob.Length);
                rest = blob;
            }
            int n = (int)Math.Min((uint)rest.Length, recvSize);
            if (n > 0 && data != 0) rest.AsSpan(0, n).CopyTo(GuestMemory.Span(data, n));
            t.State = rest.Length > n ? rest[n..] : null;
            t.Open = t.State != null;
            return n;
        });
    }

    [Hle] int sceNpScoreGetGameData(int reqId, uint boardId, uint npId, uint totalSize, uint recvSize, uint data, uint option) =>
        GetGameData(reqId, boardId, npId, totalSize, recvSize, data, false);
    [Hle] int sceNpScoreGetGameDataAsync(int reqId, uint boardId, uint npId, uint totalSize, uint recvSize, uint data, uint option) =>
        GetGameData(reqId, boardId, npId, totalSize, recvSize, data, true);

    // ---------------------------------------------------------------- rankings

    /// <summary>SceNpScoreRankData (128): npId +0, onlineName +36, pcId +88, serialRank +92, rank +96, highestRank +100,
    /// hasGameData +104, scoreValue +112, recordDate +120.</summary>
    static void WriteRankData(uint p, RankedScore r)
    {
        GuestMemory.Fill(p, 0, RankDataSize);
        NpManager.WriteNpId(p, r.Entry.OnlineId);
        NpCommunity.WriteOnlineName(p + 36, r.Entry.OnlineId);
        GuestMemory.W32(p + 88, (uint)r.Entry.PcId);
        GuestMemory.W32(p + 92, r.SerialRank);
        GuestMemory.W32(p + 96, r.Rank);
        GuestMemory.W32(p + 100, r.Entry.HighestRank == 0 ? r.Rank : Math.Min(r.Entry.HighestRank, r.Rank));
        GuestMemory.W32(p + 104, r.Entry.GameData != null ? 1u : 0u);
        GuestMemory.Write(p + 112, r.Entry.Score);
        GuestMemory.Write(p + 120, r.Entry.RecordDate);
    }

    sealed record RankOut(uint RankArray, uint RankArraySize, uint CommentArray, uint CommentArraySize, uint InfoArray,
        uint InfoArraySize, int ArrayNum, uint LastSortDate, uint TotalRecord);

    /// <summary>Validates the output arrays (sizes must hold arrayNum elements).</summary>
    static int Check(RankOut o, uint elemSize, int max)
    {
        if (o.RankArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (o.ArrayNum <= 0) return NpCommunity.INVALID_ARGUMENT;
        if (o.ArrayNum > max) return NpCommunity.INVALID_ARGUMENT;
        if (NpCommunity.Stride(o.RankArraySize, o.ArrayNum, elemSize, elemSize == PlayerRankDataSize ? 8u + 104 : 104u, "SceNpScoreRankData") == 0)
            return NpCommunity.INVALID_ALIGNMENT;
        return 0;
    }

    /// <summary>Fills rank/comment/info arrays from <paramref name="rows"/> (null rows = no score: zeroed / hasData 0).
    /// Returns the number of rows that have a score.</summary>
    static int Fill(RankOut o, IReadOnlyList<RankedScore?> rows, bool player, uint total, ulong lastSort)
    {
        uint stride = o.RankArraySize / (uint)o.ArrayNum;
        GuestMemory.Fill(o.RankArray, 0, o.RankArraySize);
        uint cstride = o.CommentArray != 0 ? o.CommentArraySize / (uint)o.ArrayNum : 0;
        if (cstride > 0) GuestMemory.Fill(o.CommentArray, 0, o.CommentArraySize);
        uint istride = o.InfoArray != 0 ? o.InfoArraySize / (uint)o.ArrayNum : 0;
        if (istride > 0) GuestMemory.Fill(o.InfoArray, 0, o.InfoArraySize);
        int registered = 0;
        for (int i = 0; i < Math.Min(rows.Count, o.ArrayNum); i++)
        {
            if (rows[i] is not { } r) continue;
            registered++;
            uint p = o.RankArray + (uint)i * stride;
            if (player) { GuestMemory.W32(p, 1); WriteRankData(p + 8, r); }
            else WriteRankData(p, r);
            if (cstride >= 2) GuestMemory.WriteCString(o.CommentArray + (uint)i * cstride, r.Entry.Comment, (int)Math.Min(cstride, CommentSize));
            if (istride > 0)
            {
                uint ip = o.InfoArray + (uint)i * istride;
                var info = r.Entry.GameInfo;
                if (istride >= VarGameInfoSize)
                {
                    int n = Math.Min(info.Length, 189);
                    GuestMemory.W32(ip, (uint)n);
                    info.AsSpan(0, n).CopyTo(GuestMemory.Span(ip + 4, n));
                }
                else
                {
                    int n = Math.Min(info.Length, (int)istride);
                    info.AsSpan(0, n).CopyTo(GuestMemory.Span(ip, n));
                }
            }
        }
        if (o.LastSortDate != 0) GuestMemory.Write(o.LastSortDate, lastSort);
        if (o.TotalRecord != 0) GuestMemory.W32(o.TotalRecord, total);
        return registered;
    }

    /// <summary>int (reqId, boardId, SceNpScoreRankNumber startSerialRank, SceNpScoreRankData *rankArray, SceSize rankArraySize,
    /// SceNpScoreComment *commentArray, SceSize commentArraySize, void *infoArray, SceSize infoArraySize, SceSize arrayNum,
    /// SceRtcTick *lastSortDate, SceNpScoreRankNumber *totalRecord, void *option). Returns the number of entries.</summary>
    int GetRankingByRange(int reqId, uint boardId, uint start, RankOut o, bool async)
    {
        int e = Check(o, RankDataSize, MaxRange);
        if (e != 0) return e;
        if (start == 0) return NpCommunity.INVALID_ARGUMENT;
        return _lib.Run(reqId, async, t =>
        {
            var (rows, total, last) = Store.GetRange(t.CommId, boardId, start, o.ArrayNum);
            int n = Fill(o, rows.Cast<RankedScore?>().ToList(), false, total, last);
            return n > 0 ? n : NpCommunity.SERVER_GAME_RANKING_NOT_FOUND;
        });
    }

    [Hle]
    int sceNpScoreGetRankingByRange(int reqId, uint boardId, uint start, uint rankArray, uint rankArraySize, uint commentArray, uint commentArraySize,
        uint infoArray, uint infoArraySize, uint arrayNum, uint lastSortDate, uint totalRecord, uint option) =>
        GetRankingByRange(reqId, boardId, start, new(rankArray, rankArraySize, commentArray, commentArraySize, infoArray, infoArraySize, (int)arrayNum, lastSortDate, totalRecord), false);

    [Hle]
    int sceNpScoreGetRankingByRangeAsync(int reqId, uint boardId, uint start, uint rankArray, uint rankArraySize, uint commentArray, uint commentArraySize,
        uint infoArray, uint infoArraySize, uint arrayNum, uint lastSortDate, uint totalRecord, uint option) =>
        GetRankingByRange(reqId, boardId, start, new(rankArray, rankArraySize, commentArray, commentArraySize, infoArray, infoArraySize, (int)arrayNum, lastSortDate, totalRecord), true);

    /// <summary>By NpId (SceNpId array, 36 each) or NpId + pcId (SceNpScoreNpIdPcId array, 44 each): one
    /// SceNpScorePlayerRankData (hasData +0, rankData +8) per id asked.</summary>
    int GetRankingByIds(int reqId, uint boardId, uint idArray, uint idArraySize, bool withPcId, RankOut o, bool async)
    {
        if (idArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        int e = Check(o, PlayerRankDataSize, MaxNpIds);
        if (e != 0) return e;
        uint idStride = idArraySize / (uint)o.ArrayNum;
        if (idStride < (withPcId ? 40u : 36u)) return NpCommunity.INVALID_ALIGNMENT;
        var ids = new List<(string, int)>();
        for (int i = 0; i < o.ArrayNum; i++)
        {
            uint p = idArray + (uint)i * idStride;
            ids.Add((NpCommunity.ReadOnlineId(p), withPcId ? (int)GuestMemory.R32(p + 36) : 0));
        }
        return _lib.Run(reqId, async, t =>
        {
            var (rows, total, last) = Store.GetByIds(t.CommId, boardId, ids);
            int n = Fill(o, rows, true, total, last);
            return n > 0 ? o.ArrayNum : NpCommunity.SERVER_GAME_RANKING_NOT_FOUND;
        });
    }

    [Hle]
    int sceNpScoreGetRankingByNpId(int reqId, uint boardId, uint npIdArray, uint npIdArraySize, uint rankArray, uint rankArraySize, uint commentArray,
        uint commentArraySize, uint infoArray, uint infoArraySize, uint arrayNum, uint lastSortDate, uint totalRecord, uint option) =>
        GetRankingByIds(reqId, boardId, npIdArray, npIdArraySize, false, new(rankArray, rankArraySize, commentArray, commentArraySize, infoArray, infoArraySize, (int)arrayNum, lastSortDate, totalRecord), false);

    [Hle]
    int sceNpScoreGetRankingByNpIdAsync(int reqId, uint boardId, uint npIdArray, uint npIdArraySize, uint rankArray, uint rankArraySize, uint commentArray,
        uint commentArraySize, uint infoArray, uint infoArraySize, uint arrayNum, uint lastSortDate, uint totalRecord, uint option) =>
        GetRankingByIds(reqId, boardId, npIdArray, npIdArraySize, false, new(rankArray, rankArraySize, commentArray, commentArraySize, infoArray, infoArraySize, (int)arrayNum, lastSortDate, totalRecord), true);

    [Hle]
    int sceNpScoreGetRankingByNpIdPcId(int reqId, uint boardId, uint idArray, uint idArraySize, uint rankArray, uint rankArraySize, uint commentArray,
        uint commentArraySize, uint infoArray, uint infoArraySize, uint arrayNum, uint lastSortDate, uint totalRecord, uint option) =>
        GetRankingByIds(reqId, boardId, idArray, idArraySize, true, new(rankArray, rankArraySize, commentArray, commentArraySize, infoArray, infoArraySize, (int)arrayNum, lastSortDate, totalRecord), false);

    [Hle]
    int sceNpScoreGetRankingByNpIdPcIdAsync(int reqId, uint boardId, uint idArray, uint idArraySize, uint rankArray, uint rankArraySize, uint commentArray,
        uint commentArraySize, uint infoArray, uint infoArraySize, uint arrayNum, uint lastSortDate, uint totalRecord, uint option) =>
        GetRankingByIds(reqId, boardId, idArray, idArraySize, true, new(rankArray, rankArraySize, commentArray, commentArraySize, infoArray, infoArraySize, (int)arrayNum, lastSortDate, totalRecord), true);

    /// <summary>int (reqId, boardId, int includeSelf, SceNpScoreRankData *rankArray, ...): the friend list is empty, so the
    /// ranking holds the user's own entry when includeSelf is set.</summary>
    int GetFriendsRanking(int reqId, uint boardId, int includeSelf, RankOut o, bool async)
    {
        int e = Check(o, RankDataSize, MaxRange);
        if (e != 0) return e;
        return _lib.Run(reqId, async, t =>
        {
            var (rows, _, last) = Store.GetByIds(t.CommId, boardId, [(NpCommunity.Me, 0)]);
            var list = includeSelf != 0 && rows[0] is { } me ? new List<RankedScore?> { me with { SerialRank = 1, Rank = 1 } } : new List<RankedScore?>();
            int n = Fill(o, list, false, (uint)list.Count, last);
            return n > 0 ? n : NpCommunity.SERVER_GAME_RANKING_NOT_FOUND;
        });
    }

    [Hle]
    int sceNpScoreGetFriendsRanking(int reqId, uint boardId, int includeSelf, uint rankArray, uint rankArraySize, uint commentArray, uint commentArraySize,
        uint infoArray, uint infoArraySize, uint arrayNum, uint lastSortDate, uint totalRecord, uint option) =>
        GetFriendsRanking(reqId, boardId, includeSelf, new(rankArray, rankArraySize, commentArray, commentArraySize, infoArray, infoArraySize, (int)arrayNum, lastSortDate, totalRecord), false);

    [Hle]
    int sceNpScoreGetFriendsRankingAsync(int reqId, uint boardId, int includeSelf, uint rankArray, uint rankArraySize, uint commentArray, uint commentArraySize,
        uint infoArray, uint infoArraySize, uint arrayNum, uint lastSortDate, uint totalRecord, uint option) =>
        GetFriendsRanking(reqId, boardId, includeSelf, new(rankArray, rankArraySize, commentArray, commentArraySize, infoArray, infoArraySize, (int)arrayNum, lastSortDate, totalRecord), true);

    // ---------------------------------------------------------------- comments: nothing is censored locally

    [Hle] int sceNpScoreCensorComment(int reqId, uint comment, uint option) => comment == 0 ? NpCommunity.INSUFFICIENT_ARGUMENT : _lib.Sync(reqId);
    [Hle] int sceNpScoreCensorCommentAsync(int reqId, uint comment, uint option) => comment == 0 ? NpCommunity.INSUFFICIENT_ARGUMENT : _lib.StartAsync(reqId);

    int Sanitize(int reqId, uint comment, uint sanitized, bool async)
    {
        if (comment == 0 || sanitized == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        string text = GuestMemory.ReadCString(comment, 64);
        return _lib.Run(reqId, async, _ => { GuestMemory.WriteCString(sanitized, text, 64); return 0; });
    }

    [Hle] int sceNpScoreSanitizeComment(int reqId, uint comment, uint sanitized, uint option) => Sanitize(reqId, comment, sanitized, false);
    [Hle] int sceNpScoreSanitizeCommentAsync(int reqId, uint comment, uint sanitized, uint option) => Sanitize(reqId, comment, sanitized, true);
}

/// <summary>
/// SceNpTus (title user storage, used for Cross-Save) and SceNpTss (title small storage), sharing one request space.
/// With the local PSN services the slots live in <see cref="TusStore"/> (per title and owner online id). Prototypes:
/// RPCS3 sceNpTus.cpp (the Vita library keeps the PS3 API); structures (PS3 layout, element sizes from the caller):
/// SceNpTusVariable (120): ownerId +0, hasData +36, lastChangedDate +40, lastChangedAuthorId +52, variable +88,
/// oldVariable +96. SceNpTusDataStatus (488): ownerId +0, hasData +36, lastChangedDate +40, lastChangedAuthorId +48,
/// data* +84, dataSize +88, info {infoSize +96, data[384] +104}. SceNpTusDataInfo (392): infoSize +0, data +8.
/// </summary>
public sealed class NpTus : HleModule
{
    readonly NpCommunity _lib = new("NpTus");
    internal NpCommunity Lib => _lib;
    internal TusStore? StoreOverride;
    TusStore Store => StoreOverride ?? Emu.LocalPsn.Tus;

    public const uint VariableSize = 120, DataStatusSize = 488, DataInfoSize = 392;
    const int MaxSlots = 64, MaxUsers = 101, VUserIdSize = 20, NpIdSize = 36;

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

    static string Owner(uint target) => NpCommunity.ReadOnlineId(target);

    static int[]? Slots(uint slotIdArray, int arrayNum)
    {
        var s = new int[arrayNum];
        for (int i = 0; i < arrayNum; i++)
        {
            s[i] = (int)GuestMemory.R32(slotIdArray + (uint)i * 4);
            if (s[i] < 0) return null;
        }
        return s;
    }

    static void WriteOwner(uint p, string owner) => NpManager.WriteNpId(p, owner);

    static void WriteVariable(uint p, uint stride, string owner, TusVariable? v)
    {
        GuestMemory.Fill(p, 0, stride);
        WriteOwner(p, owner);
        if (v == null) return;
        GuestMemory.W32(p + 36, 1);
        GuestMemory.Write(p + 40, v.LastChanged);
        WriteOwner(p + 52, v.Author);
        GuestMemory.Write(p + 88, v.Value);
        GuestMemory.Write(p + 96, v.OldValue);
    }

    static void WriteDataStatus(uint p, uint stride, string owner, TusDataStatus? s, uint data)
    {
        GuestMemory.Fill(p, 0, stride);
        WriteOwner(p, owner);
        if (s == null) return;
        GuestMemory.W32(p + 36, 1);
        GuestMemory.Write(p + 40, s.LastChanged);
        WriteOwner(p + 48, s.Author);
        GuestMemory.W32(p + 84, data);
        GuestMemory.W32(p + 88, (uint)s.Size);
        if (stride >= 104)
        {
            int n = Math.Min(s.Info.Length, (int)Math.Min(TusStore.MaxDataInfoSize, stride - 104));
            GuestMemory.W32(p + 96, (uint)n);
            s.Info.AsSpan(0, n).CopyTo(GuestMemory.Span(p + 104, n));
        }
    }

    // ---------------------------------------------------------------- variables

    /// <summary>int (reqId, const SceNpId *targetNpId, const SceNpTusSlotId *slotIdArray, const SceNpInt64 *variableArray,
    /// int arrayNum, void *option).</summary>
    int SetMultiSlotVariable(int reqId, uint target, uint slotIdArray, uint variableArray, int arrayNum, bool async)
    {
        if (target == 0 || slotIdArray == 0 || variableArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (arrayNum is <= 0 or > MaxSlots) return NpCommunity.INVALID_ARGUMENT;
        var slots = Slots(slotIdArray, arrayNum);
        if (slots == null) return NpCommunity.INVALID_ARGUMENT;
        var values = slots.Select((s, i) => (s, GuestMemory.Read<long>(variableArray + (uint)i * 8))).ToList();
        string owner = Owner(target);
        return _lib.Run(reqId, async, t =>
        {
            Store.SetVariables(t.CommId, owner, values, NpCommunity.Me);
            Log.Debug($"NpTus: {t.CommId}/{owner} set {string.Join(", ", values.Select(v => $"[{v.Item1}]={v.Item2}"))}");
            return 0;
        });
    }

    [Hle] int sceNpTusSetMultiSlotVariable(int reqId, uint target, uint slots, uint vars, int num, uint opt) => SetMultiSlotVariable(reqId, target, slots, vars, num, false);
    [Hle] int sceNpTusSetMultiSlotVariableAsync(int reqId, uint target, uint slots, uint vars, int num, uint opt) => SetMultiSlotVariable(reqId, target, slots, vars, num, true);
    [Hle] int sceNpTusSetMultiSlotVariableVUser(int reqId, uint target, uint slots, uint vars, int num, uint opt) => SetMultiSlotVariable(reqId, target, slots, vars, num, false);
    [Hle] int sceNpTusSetMultiSlotVariableVUserAsync(int reqId, uint target, uint slots, uint vars, int num, uint opt) => SetMultiSlotVariable(reqId, target, slots, vars, num, true);

    /// <summary>int (reqId, const SceNpId *targetNpId, const SceNpTusSlotId *slotIdArray, SceNpTusVariable *variableArray,
    /// SceSize variableArraySize, int arrayNum, void *option). Returns arrayNum; empty slots have hasData 0.</summary>
    int GetMultiSlotVariable(int reqId, uint target, uint slotIdArray, uint variableArray, uint variableArraySize, int arrayNum, bool async)
    {
        if (target == 0 || slotIdArray == 0 || variableArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (arrayNum is <= 0 or > MaxSlots) return NpCommunity.INVALID_ARGUMENT;
        uint stride = NpCommunity.Stride(variableArraySize, arrayNum, VariableSize, 104, "SceNpTusVariable");
        if (stride == 0) return NpCommunity.INVALID_ALIGNMENT;
        var slots = Slots(slotIdArray, arrayNum);
        if (slots == null) return NpCommunity.INVALID_ARGUMENT;
        string owner = Owner(target);
        return _lib.Run(reqId, async, t =>
        {
            for (int i = 0; i < arrayNum; i++)
                WriteVariable(variableArray + (uint)i * stride, stride, owner, Store.GetVariable(t.CommId, owner, slots[i]));
            return arrayNum;
        });
    }

    [Hle] int sceNpTusGetMultiSlotVariable(int reqId, uint target, uint slots, uint vars, uint size, int num, uint opt) => GetMultiSlotVariable(reqId, target, slots, vars, size, num, false);
    [Hle] int sceNpTusGetMultiSlotVariableAsync(int reqId, uint target, uint slots, uint vars, uint size, int num, uint opt) => GetMultiSlotVariable(reqId, target, slots, vars, size, num, true);
    [Hle] int sceNpTusGetMultiSlotVariableVUser(int reqId, uint target, uint slots, uint vars, uint size, int num, uint opt) => GetMultiSlotVariable(reqId, target, slots, vars, size, num, false);
    [Hle] int sceNpTusGetMultiSlotVariableVUserAsync(int reqId, uint target, uint slots, uint vars, uint size, int num, uint opt) => GetMultiSlotVariable(reqId, target, slots, vars, size, num, true);

    /// <summary>int (reqId, const SceNpId *targetNpIdArray, SceNpTusSlotId slotId, SceNpTusVariable *variableArray,
    /// SceSize variableArraySize, int arrayNum, void *option): one slot of several users.</summary>
    int GetMultiUserVariable(int reqId, uint targets, int idSize, int slot, uint variableArray, uint variableArraySize, int arrayNum, bool async)
    {
        if (targets == 0 || variableArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (arrayNum is <= 0 or > MaxUsers || slot < 0) return NpCommunity.INVALID_ARGUMENT;
        uint stride = NpCommunity.Stride(variableArraySize, arrayNum, VariableSize, 104, "SceNpTusVariable");
        if (stride == 0) return NpCommunity.INVALID_ALIGNMENT;
        var owners = Enumerable.Range(0, arrayNum).Select(i => Owner(targets + (uint)(i * idSize))).ToArray();
        return _lib.Run(reqId, async, t =>
        {
            for (int i = 0; i < arrayNum; i++)
                WriteVariable(variableArray + (uint)i * stride, stride, owners[i], Store.GetVariable(t.CommId, owners[i], slot));
            return arrayNum;
        });
    }

    [Hle] int sceNpTusGetMultiUserVariable(int reqId, uint targets, int slot, uint vars, uint size, int num, uint opt) => GetMultiUserVariable(reqId, targets, NpIdSize, slot, vars, size, num, false);
    [Hle] int sceNpTusGetMultiUserVariableAsync(int reqId, uint targets, int slot, uint vars, uint size, int num, uint opt) => GetMultiUserVariable(reqId, targets, NpIdSize, slot, vars, size, num, true);
    [Hle] int sceNpTusGetMultiUserVariableVUser(int reqId, uint targets, int slot, uint vars, uint size, int num, uint opt) => GetMultiUserVariable(reqId, targets, VUserIdSize, slot, vars, size, num, false);
    [Hle] int sceNpTusGetMultiUserVariableVUserAsync(int reqId, uint targets, int slot, uint vars, uint size, int num, uint opt) => GetMultiUserVariable(reqId, targets, VUserIdSize, slot, vars, size, num, true);

    /// <summary>int (reqId, SceNpTusSlotId slotId, int includeSelf, int sortType, SceNpTusVariable *variableArray,
    /// SceSize variableArraySize, int arrayNum, void *option): no friends, so only the user's own slot (includeSelf).</summary>
    int GetFriendsVariable(int reqId, int slot, int includeSelf, uint variableArray, uint variableArraySize, int arrayNum, bool async)
    {
        if (variableArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (arrayNum is <= 0 or > MaxUsers || slot < 0) return NpCommunity.INVALID_ARGUMENT;
        uint stride = NpCommunity.Stride(variableArraySize, arrayNum, VariableSize, 104, "SceNpTusVariable");
        if (stride == 0) return NpCommunity.INVALID_ALIGNMENT;
        return _lib.Run(reqId, async, t =>
        {
            GuestMemory.Fill(variableArray, 0, stride * (uint)arrayNum);
            if (includeSelf == 0) return 0;
            WriteVariable(variableArray, stride, NpCommunity.Me, Store.GetVariable(t.CommId, NpCommunity.Me, slot));
            return 1;
        });
    }

    [Hle] int sceNpTusGetFriendsVariable(int reqId, int slot, int includeSelf, int sortType, uint vars, uint size, int num, uint opt) => GetFriendsVariable(reqId, slot, includeSelf, vars, size, num, false);
    [Hle] int sceNpTusGetFriendsVariableAsync(int reqId, int slot, int includeSelf, int sortType, uint vars, uint size, int num, uint opt) => GetFriendsVariable(reqId, slot, includeSelf, vars, size, num, true);

    /// <summary>int (reqId, const SceNpId *targetNpId, SceNpTusSlotId slotId, SceNpInt64 inVariable, SceNpTusVariable *outVariable,
    /// SceSize outVariableSize, SceNpTusAddAndGetVariableOptParam *option).</summary>
    int AddAndGetVariable(int reqId, uint target, int slot, long add, uint outVar, uint outSize, bool async)
    {
        if (target == 0 || outVar == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (slot < 0) return NpCommunity.INVALID_ARGUMENT;
        if (outSize < 104) return NpCommunity.INVALID_ALIGNMENT;
        string owner = Owner(target);
        return _lib.Run(reqId, async, t =>
        {
            WriteVariable(outVar, outSize, owner, Store.AddAndGet(t.CommId, owner, slot, add, NpCommunity.Me));
            return 0;
        });
    }

    [Hle] int sceNpTusAddAndGetVariable(int reqId, uint target, int slot, long add, uint outVar, uint outSize, uint opt) => AddAndGetVariable(reqId, target, slot, add, outVar, outSize, false);
    [Hle] int sceNpTusAddAndGetVariableAsync(int reqId, uint target, int slot, long add, uint outVar, uint outSize, uint opt) => AddAndGetVariable(reqId, target, slot, add, outVar, outSize, true);
    [Hle] int sceNpTusAddAndGetVariableVUser(int reqId, uint target, int slot, long add, uint outVar, uint outSize, uint opt) => AddAndGetVariable(reqId, target, slot, add, outVar, outSize, false);
    [Hle] int sceNpTusAddAndGetVariableVUserAsync(int reqId, uint target, int slot, long add, uint outVar, uint outSize, uint opt) => AddAndGetVariable(reqId, target, slot, add, outVar, outSize, true);

    /// <summary>int (reqId, const SceNpId *targetNpId, SceNpTusSlotId slotId, int opeType, SceNpInt64 variable,
    /// SceNpTusVariable *resultVariable, SceSize resultVariableSize, SceNpTusTryAndSetVariableOptParam *option)
    /// (option: size, isLastChangedDate*, isLastChangedAuthorId*, compareValue*). When the condition fails the result is
    /// CONDITIONS_NOT_SATISFIED and resultVariable holds the current value.</summary>
    int TryAndSetVariable(int reqId, uint target, int slot, int opeType, long value, uint resultVar, uint resultSize, uint option, bool async)
    {
        if (target == 0 || resultVar == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (slot < 0 || opeType is < 1 or > 6) return NpCommunity.INVALID_ARGUMENT;
        if (resultSize < 104) return NpCommunity.INVALID_ALIGNMENT;
        long? compare = null;
        if (option != 0 && GuestMemory.R32(option) >= 16 && GuestMemory.R32(option + 12) is uint cv && cv != 0) compare = GuestMemory.Read<long>(cv);
        string owner = Owner(target);
        return _lib.Run(reqId, async, t =>
        {
            var (v, written) = Store.TryAndSet(t.CommId, owner, slot, (TusOpeType)opeType, value, compare, NpCommunity.Me);
            WriteVariable(resultVar, resultSize, owner, v);
            return written ? 0 : NpCommunity.SERVER_CONDITIONS_NOT_SATISFIED;
        });
    }

    [Hle] int sceNpTusTryAndSetVariable(int reqId, uint target, int slot, int ope, long value, uint res, uint size, uint opt) => TryAndSetVariable(reqId, target, slot, ope, value, res, size, opt, false);
    [Hle] int sceNpTusTryAndSetVariableAsync(int reqId, uint target, int slot, int ope, long value, uint res, uint size, uint opt) => TryAndSetVariable(reqId, target, slot, ope, value, res, size, opt, true);
    [Hle] int sceNpTusTryAndSetVariableVUser(int reqId, uint target, int slot, int ope, long value, uint res, uint size, uint opt) => TryAndSetVariable(reqId, target, slot, ope, value, res, size, opt, false);
    [Hle] int sceNpTusTryAndSetVariableVUserAsync(int reqId, uint target, int slot, int ope, long value, uint res, uint size, uint opt) => TryAndSetVariable(reqId, target, slot, ope, value, res, size, opt, true);

    /// <summary>int (reqId, const SceNpId *targetNpId, const SceNpTusSlotId *slotIdArray, int arrayNum, void *option).</summary>
    int DeleteMultiSlot(int reqId, uint target, uint slotIdArray, int arrayNum, bool data, bool async)
    {
        if (target == 0 || slotIdArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (arrayNum is <= 0 or > MaxSlots) return NpCommunity.INVALID_ARGUMENT;
        var slots = Slots(slotIdArray, arrayNum);
        if (slots == null) return NpCommunity.INVALID_ARGUMENT;
        string owner = Owner(target);
        return _lib.Run(reqId, async, t =>
        {
            if (data) Store.DeleteData(t.CommId, owner, slots);
            else Store.DeleteVariables(t.CommId, owner, slots);
            return 0;
        });
    }

    [Hle] int sceNpTusDeleteMultiSlotVariable(int reqId, uint target, uint slots, int num, uint opt) => DeleteMultiSlot(reqId, target, slots, num, false, false);
    [Hle] int sceNpTusDeleteMultiSlotVariableAsync(int reqId, uint target, uint slots, int num, uint opt) => DeleteMultiSlot(reqId, target, slots, num, false, true);
    [Hle] int sceNpTusDeleteMultiSlotVariableVUser(int reqId, uint target, uint slots, int num, uint opt) => DeleteMultiSlot(reqId, target, slots, num, false, false);
    [Hle] int sceNpTusDeleteMultiSlotVariableVUserAsync(int reqId, uint target, uint slots, int num, uint opt) => DeleteMultiSlot(reqId, target, slots, num, false, true);
    [Hle] int sceNpTusDeleteMultiSlotData(int reqId, uint target, uint slots, int num, uint opt) => DeleteMultiSlot(reqId, target, slots, num, true, false);
    [Hle] int sceNpTusDeleteMultiSlotDataAsync(int reqId, uint target, uint slots, int num, uint opt) => DeleteMultiSlot(reqId, target, slots, num, true, true);
    [Hle] int sceNpTusDeleteMultiSlotDataVUser(int reqId, uint target, uint slots, int num, uint opt) => DeleteMultiSlot(reqId, target, slots, num, true, false);
    [Hle] int sceNpTusDeleteMultiSlotDataVUserAsync(int reqId, uint target, uint slots, int num, uint opt) => DeleteMultiSlot(reqId, target, slots, num, true, true);

    // ---------------------------------------------------------------- data

    sealed class SetDataState { public required uint Total; public required List<byte> Buffer; public byte[] Info = []; }

    /// <summary>int (reqId, const SceNpId *targetNpId, SceNpTusSlotId slotId, SceSize totalSize, SceSize sendSize,
    /// const void *data, const SceNpTusDataInfo *info, SceSize infoStructSize, SceNpTusSetDataOptParam *option). The data
    /// may arrive in several calls on the same request; it is stored once totalSize bytes arrived.</summary>
    int SetData(int reqId, uint target, int slot, uint totalSize, uint sendSize, uint data, uint info, uint infoSize, bool async)
    {
        if (target == 0 || (data == 0 && sendSize > 0)) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (slot < 0 || sendSize > totalSize) return NpCommunity.INVALID_ARGUMENT;
        byte[] chunk = sendSize > 0 ? GuestMemory.Span(data, (int)sendSize).ToArray() : [];
        byte[] infoBytes = [];
        if (info != 0)
        {
            uint n = Math.Min(GuestMemory.R32(info), TusStore.MaxDataInfoSize);
            infoBytes = GuestMemory.Span(info + 8, (int)n).ToArray();
        }
        string owner = Owner(target);
        return _lib.Run(reqId, async, t =>
        {
            var st = t.State as SetDataState ?? new SetDataState { Total = totalSize, Buffer = new List<byte>((int)Math.Min(totalSize, 1u << 24)), Info = infoBytes };
            st.Buffer.AddRange(chunk);
            if (st.Buffer.Count < st.Total) { t.State = st; t.Open = true; return 0; }
            t.State = null;
            Store.SetData(t.CommId, owner, slot, st.Buffer.ToArray(), st.Info, NpCommunity.Me);
            Log.Info($"NpTus: {t.CommId}/{owner} data slot {slot}: {st.Buffer.Count} bytes stored");
            return 0;
        });
    }

    [Hle] int sceNpTusSetData(int reqId, uint target, int slot, uint total, uint send, uint data, uint info, uint infoSize, uint opt) => SetData(reqId, target, slot, total, send, data, info, infoSize, false);
    [Hle] int sceNpTusSetDataAsync(int reqId, uint target, int slot, uint total, uint send, uint data, uint info, uint infoSize, uint opt) => SetData(reqId, target, slot, total, send, data, info, infoSize, true);
    [Hle] int sceNpTusSetDataVUser(int reqId, uint target, int slot, uint total, uint send, uint data, uint info, uint infoSize, uint opt) => SetData(reqId, target, slot, total, send, data, info, infoSize, false);
    [Hle] int sceNpTusSetDataVUserAsync(int reqId, uint target, int slot, uint total, uint send, uint data, uint info, uint infoSize, uint opt) => SetData(reqId, target, slot, total, send, data, info, infoSize, true);

    /// <summary>int (reqId, const SceNpId *targetNpId, SceNpTusSlotId slotId, SceNpTusDataStatus *dataStatus,
    /// SceSize dataStatusSize, void *data, SceSize recvSize, void *option). Returns the bytes copied (the rest comes with
    /// the next calls on the same request), or USER_STORAGE_DATA_NOT_FOUND for an empty slot.</summary>
    int GetData(int reqId, uint target, int slot, uint dataStatus, uint dataStatusSize, uint data, uint recvSize, bool async)
    {
        if (target == 0 || dataStatus == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (slot < 0) return NpCommunity.INVALID_ARGUMENT;
        if (dataStatusSize < 104) return NpCommunity.INVALID_ALIGNMENT;
        string owner = Owner(target);
        return _lib.Run(reqId, async, t =>
        {
            if (t.State is not byte[] rest)
            {
                var d = Store.GetData(t.CommId, owner, slot);
                WriteDataStatus(dataStatus, dataStatusSize, owner, d?.Status, data);
                if (d == null) return NpCommunity.SERVER_USER_STORAGE_DATA_NOT_FOUND;
                rest = d.Value.Data;
            }
            int n = (int)Math.Min((uint)rest.Length, recvSize);
            if (n > 0 && data != 0) rest.AsSpan(0, n).CopyTo(GuestMemory.Span(data, n));
            t.State = rest.Length > n ? rest[n..] : null;
            t.Open = t.State != null;
            return n;
        });
    }

    [Hle] int sceNpTusGetData(int reqId, uint target, int slot, uint status, uint statusSize, uint data, uint recvSize, uint opt) => GetData(reqId, target, slot, status, statusSize, data, recvSize, false);
    [Hle] int sceNpTusGetDataAsync(int reqId, uint target, int slot, uint status, uint statusSize, uint data, uint recvSize, uint opt) => GetData(reqId, target, slot, status, statusSize, data, recvSize, true);
    [Hle] int sceNpTusGetDataVUser(int reqId, uint target, int slot, uint status, uint statusSize, uint data, uint recvSize, uint opt) => GetData(reqId, target, slot, status, statusSize, data, recvSize, false);
    [Hle] int sceNpTusGetDataVUserAsync(int reqId, uint target, int slot, uint status, uint statusSize, uint data, uint recvSize, uint opt) => GetData(reqId, target, slot, status, statusSize, data, recvSize, true);

    /// <summary>int (reqId, const SceNpId *targetNpId, const SceNpTusSlotId *slotIdArray, SceNpTusDataStatus *statusArray,
    /// SceSize statusArraySize, int arrayNum, void *option).</summary>
    int GetMultiSlotDataStatus(int reqId, uint target, uint slotIdArray, uint statusArray, uint statusArraySize, int arrayNum, bool async)
    {
        if (target == 0 || slotIdArray == 0 || statusArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (arrayNum is <= 0 or > MaxSlots) return NpCommunity.INVALID_ARGUMENT;
        uint stride = NpCommunity.Stride(statusArraySize, arrayNum, DataStatusSize, 96, "SceNpTusDataStatus");
        if (stride == 0) return NpCommunity.INVALID_ALIGNMENT;
        var slots = Slots(slotIdArray, arrayNum);
        if (slots == null) return NpCommunity.INVALID_ARGUMENT;
        string owner = Owner(target);
        return _lib.Run(reqId, async, t =>
        {
            for (int i = 0; i < arrayNum; i++)
                WriteDataStatus(statusArray + (uint)i * stride, stride, owner, Store.GetDataStatus(t.CommId, owner, slots[i]), 0);
            return arrayNum;
        });
    }

    [Hle] int sceNpTusGetMultiSlotDataStatus(int reqId, uint target, uint slots, uint status, uint size, int num, uint opt) => GetMultiSlotDataStatus(reqId, target, slots, status, size, num, false);
    [Hle] int sceNpTusGetMultiSlotDataStatusAsync(int reqId, uint target, uint slots, uint status, uint size, int num, uint opt) => GetMultiSlotDataStatus(reqId, target, slots, status, size, num, true);
    [Hle] int sceNpTusGetMultiSlotDataStatusVUser(int reqId, uint target, uint slots, uint status, uint size, int num, uint opt) => GetMultiSlotDataStatus(reqId, target, slots, status, size, num, false);
    [Hle] int sceNpTusGetMultiSlotDataStatusVUserAsync(int reqId, uint target, uint slots, uint status, uint size, int num, uint opt) => GetMultiSlotDataStatus(reqId, target, slots, status, size, num, true);

    /// <summary>int (reqId, const SceNpId *targetNpIdArray, SceNpTusSlotId slotId, SceNpTusDataStatus *statusArray,
    /// SceSize statusArraySize, int arrayNum, void *option).</summary>
    int GetMultiUserDataStatus(int reqId, uint targets, int idSize, int slot, uint statusArray, uint statusArraySize, int arrayNum, bool async)
    {
        if (targets == 0 || statusArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (arrayNum is <= 0 or > MaxUsers || slot < 0) return NpCommunity.INVALID_ARGUMENT;
        uint stride = NpCommunity.Stride(statusArraySize, arrayNum, DataStatusSize, 96, "SceNpTusDataStatus");
        if (stride == 0) return NpCommunity.INVALID_ALIGNMENT;
        var owners = Enumerable.Range(0, arrayNum).Select(i => Owner(targets + (uint)(i * idSize))).ToArray();
        return _lib.Run(reqId, async, t =>
        {
            for (int i = 0; i < arrayNum; i++)
                WriteDataStatus(statusArray + (uint)i * stride, stride, owners[i], Store.GetDataStatus(t.CommId, owners[i], slot), 0);
            return arrayNum;
        });
    }

    [Hle] int sceNpTusGetMultiUserDataStatus(int reqId, uint targets, int slot, uint status, uint size, int num, uint opt) => GetMultiUserDataStatus(reqId, targets, NpIdSize, slot, status, size, num, false);
    [Hle] int sceNpTusGetMultiUserDataStatusAsync(int reqId, uint targets, int slot, uint status, uint size, int num, uint opt) => GetMultiUserDataStatus(reqId, targets, NpIdSize, slot, status, size, num, true);
    [Hle] int sceNpTusGetMultiUserDataStatusVUser(int reqId, uint targets, int slot, uint status, uint size, int num, uint opt) => GetMultiUserDataStatus(reqId, targets, VUserIdSize, slot, status, size, num, false);
    [Hle] int sceNpTusGetMultiUserDataStatusVUserAsync(int reqId, uint targets, int slot, uint status, uint size, int num, uint opt) => GetMultiUserDataStatus(reqId, targets, VUserIdSize, slot, status, size, num, true);

    /// <summary>int (reqId, SceNpTusSlotId slotId, int includeSelf, int sortType, SceNpTusDataStatus *statusArray,
    /// SceSize statusArraySize, int arrayNum, void *option): no friends, only the user's own slot (includeSelf).</summary>
    int GetFriendsDataStatus(int reqId, int slot, int includeSelf, uint statusArray, uint statusArraySize, int arrayNum, bool async)
    {
        if (statusArray == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (arrayNum is <= 0 or > MaxUsers || slot < 0) return NpCommunity.INVALID_ARGUMENT;
        uint stride = NpCommunity.Stride(statusArraySize, arrayNum, DataStatusSize, 96, "SceNpTusDataStatus");
        if (stride == 0) return NpCommunity.INVALID_ALIGNMENT;
        return _lib.Run(reqId, async, t =>
        {
            GuestMemory.Fill(statusArray, 0, stride * (uint)arrayNum);
            if (includeSelf == 0) return 0;
            WriteDataStatus(statusArray, stride, NpCommunity.Me, Store.GetDataStatus(t.CommId, NpCommunity.Me, slot), 0);
            return 1;
        });
    }

    [Hle] int sceNpTusGetFriendsDataStatus(int reqId, int slot, int includeSelf, int sortType, uint status, uint size, int num, uint opt) => GetFriendsDataStatus(reqId, slot, includeSelf, status, size, num, false);
    [Hle] int sceNpTusGetFriendsDataStatusAsync(int reqId, int slot, int includeSelf, int sortType, uint status, uint size, int num, uint opt) => GetFriendsDataStatus(reqId, slot, includeSelf, status, size, num, true);

    // ---------------------------------------------------------------- TSS (title small storage): data the developer
    // publishes on the server; nothing is published locally, so every slot is empty.

    int TssResult => NpCommunity.SERVER_USER_STORAGE_TITLE_MASTER_NOT_FOUND;

    /// <summary>int (reqId, void *data, SceSize maxSize, SceSize *contentLength, const void *option) (Vita3K).</summary>
    [Hle]
    int sceNpTssGetSmallStorageAsync(int reqId, uint data, uint maxSize, uint contentLength)
    {
        if (data == 0 || contentLength == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        GuestMemory.W32(contentLength, 0);
        return _lib.StartAsync(reqId, TssResult);
    }

    [Hle]
    int sceNpTssGetSmallStorage(int reqId, uint data, uint maxSize, uint contentLength)
    {
        if (data == 0 || contentLength == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        GuestMemory.W32(contentLength, 0);
        return _lib.Sync(reqId, TssResult);
    }

    /// <summary>SceNpTssDataStatus (0x10): +0 lastModified tick, +8 statusCodeType, +0xC contentLength.</summary>
    int TssGetData(int reqId, int slotId, uint dataStatus, uint dataStatusSize, bool async)
    {
        if (dataStatus == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        if (dataStatusSize != 16 || slotId is < 0 or > 15) return NpCommunity.INVALID_ARGUMENT;
        GuestMemory.Fill(dataStatus, 0, 16);
        return async ? _lib.StartAsync(reqId, TssResult) : _lib.Sync(reqId, TssResult);
    }

    [Hle] int sceNpTssGetData(int reqId, int slotId, uint dataStatus, uint dataStatusSize) => TssGetData(reqId, slotId, dataStatus, dataStatusSize, false);
    [Hle] int sceNpTssGetDataAsync(int reqId, int slotId, uint dataStatus, uint dataStatusSize) => TssGetData(reqId, slotId, dataStatus, dataStatusSize, true);
    [Hle] int sceNpTssGetDataNoLimit(int reqId) => _lib.Sync(reqId, TssResult);
    [Hle] int sceNpTssGetDataNoLimitAsync(int reqId) => _lib.StartAsync(reqId, TssResult);
    [Hle] int sceNpTssGetStorage(int reqId) => _lib.Sync(reqId, TssResult);
    [Hle] int sceNpTssGetStorageAsync(int reqId) => _lib.StartAsync(reqId, TssResult);
}

/// <summary>SceNpUtility: Lookup and WordFilter (community model) and BandwidthTest. The lookup server has no other
/// users and the word filter passes every comment.</summary>
public sealed class NpUtility : HleModule
{
    readonly NpCommunity _lookup = new("NpLookup"), _filter = new("NpWordFilter");
    internal NpCommunity Lookup => _lookup;

    const int SERVER_USER_PROFILE_NOT_FOUND = unchecked((int)0x8055080D);

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
    // profiles, ids and avatars of other users: no such user on the local server
    [Hle] int sceNpLookupUserProfileAsync(int id) => _lookup.StartAsync(id, SERVER_USER_PROFILE_NOT_FOUND);
    [Hle] int sceNpLookupUserProfile(int id) => _lookup.Sync(id, SERVER_USER_PROFILE_NOT_FOUND);
    [Hle] int sceNpLookupNpId(int id) => _lookup.Sync(id, SERVER_USER_PROFILE_NOT_FOUND);
    [Hle] int sceNpLookupNpIdAsync(int id) => _lookup.StartAsync(id, SERVER_USER_PROFILE_NOT_FOUND);
    [Hle] int sceNpLookupAvatarImage(int id) => _lookup.Sync(id, SERVER_USER_PROFILE_NOT_FOUND);
    [Hle] int sceNpLookupAvatarImageAsync(int id) => _lookup.StartAsync(id, SERVER_USER_PROFILE_NOT_FOUND);

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

    /// <summary>int (reqId, const char *comment, char *result, void *option) (inferred from the PS4 library): nothing is
    /// censored, the censored and the sanitized comment are the comment itself.</summary>
    [Hle] int sceNpWordFilterCensorComment(int id, uint comment, uint censored) => Censor(id, comment, censored, false);
    [Hle] int sceNpWordFilterCensorCommentAsync(int id, uint comment, uint censored) => Censor(id, comment, censored, true);
    [Hle] int sceNpWordFilterSanitizeComment(int id, uint comment, uint sanitized) => SanitizeWords(id, comment, sanitized, false);
    [Hle] int sceNpWordFilterSanitizeCommentAsync(int id, uint comment, uint sanitized) => SanitizeWords(id, comment, sanitized, true);

    int Censor(int id, uint comment, uint censored, bool async)
    {
        if (comment == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        string text = GuestMemory.ReadCString(comment, 1024);
        return _filter.Run(id, async, _ => { if (censored != 0) GuestMemory.WriteCString(censored, text, 1024); return 0; });
    }

    int SanitizeWords(int id, uint comment, uint sanitized, bool async)
    {
        if (comment == 0 || sanitized == 0) return NpCommunity.INSUFFICIENT_ARGUMENT;
        string text = GuestMemory.ReadCString(comment, 1024);
        return _filter.Run(id, async, _ => { GuestMemory.WriteCString(sanitized, text, 1024); return 0; });
    }

    // BandwidthTest measures bandwidth to Sony's servers: never starts offline.
    [Hle] int sceNpBandwidthTestInitStart() => PsnAccount.Current.ServerError;
    [Hle] int sceNpBandwidthTestGetStatus() => 0;
    [Hle] int sceNpBandwidthTestShutdown(uint result) => 0;
    [Hle] int sceNpBandwidthTestAbort() => 0;
}
