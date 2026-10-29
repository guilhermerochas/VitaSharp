namespace VitaSharp.Core.Hle.Np.Rpcn;

/// <summary>SCE_NP_COMMUNITY_SERVER_ERROR_* codes RPCN answers map to (Vita 0x805508xx, low byte of the PS3 0x8002a4xx).</summary>
static class ServerErrors
{
    public const int FORBIDDEN = unchecked((int)0x80550806), RANKING_STORE_NOT_FOUND = unchecked((int)0x80550814),
        NOT_BEST_SCORE = unchecked((int)0x80550815), RANKING_BOARD_MASTER_NOT_FOUND = unchecked((int)0x80550817),
        INVALID_SCORE = unchecked((int)0x80550823), USER_NOT_ASSIGNED = unchecked((int)0x8055082B),
        GAME_DATA_ALREADY_EXISTS = unchecked((int)0x8055082C), CONDITIONS_NOT_SATISFIED = unchecked((int)0x80550873);

    /// <summary>TUS replies (RPCS3 handle_tus_no_data / handle_TusVarResponse).</summary>
    public static void ThrowTus(RpcnCommand cmd, RpcnError e)
    {
        switch (e)
        {
            case RpcnError.NoError: return;
            case RpcnError.NotFound: throw new NpServerException(USER_NOT_ASSIGNED, $"RPCN {cmd}: not found");
            case RpcnError.Unauthorized: throw new NpServerException(FORBIDDEN, $"RPCN {cmd}: unauthorized");
            case RpcnError.CondFail: throw new NpServerException(CONDITIONS_NOT_SATISFIED, $"RPCN {cmd}: condition not satisfied");
            default: throw new IOException($"RPCN {cmd}: unexpected error {e}");
        }
    }
}

/// <summary>
/// TUS on an RPCN server (RPCS3 rpcn_client tus_*). The owner is a real online id (virtual users are not sent as such:
/// the VUser variants use the same requests). Each variable or status read is one request.
/// </summary>
public sealed class RpcnTusService(RpcnClient client) : ITusService
{
    static ProtoWriter User(string owner) => new ProtoWriter().Bool(1, false).String(2, owner);

    static TusVariable? ToVariable(ProtoReader v) =>
        !v.Bool(2) ? null : new TusVariable { LastChanged = v.UInt64(3), Author = v.String(4), Value = v.Int64(5), OldValue = v.Int64(6) };

    static TusDataStatus? ToStatus(ProtoReader s, int size) =>
        !s.Bool(2) ? null : new TusDataStatus { LastChanged = s.UInt64(3), Author = s.String(4), Info = s.Bytes(5).ToArray(), Size = size };

    List<ProtoReader> Vars(RpcnCommand cmd, string commId, ProtoWriter req)
    {
        var (err, payload) = client.Request(cmd, commId, req);
        ServerErrors.ThrowTus(cmd, err);
        return new RpcnReader(payload).Proto().Messages(1);
    }

    public TusVariable? GetVariable(string commId, string owner, int slot)
    {
        var vars = Vars(RpcnCommand.TusGetMultiSlotVariable, commId, new ProtoWriter().Message(1, User(owner)).Packed(2, [slot]));
        return vars.Count > 0 ? ToVariable(vars[0]) : null;
    }

    public void SetVariables(string commId, string owner, IReadOnlyList<(int Slot, long Value)> values, string author)
    {
        var req = new ProtoWriter().Message(1, User(owner)).Packed(2, values.Select(v => (long)v.Slot)).Packed(3, values.Select(v => v.Value));
        var (err, _) = client.Request(RpcnCommand.TusSetMultiSlotVariable, commId, req);
        ServerErrors.ThrowTus(RpcnCommand.TusSetMultiSlotVariable, err);
    }

    public TusVariable AddAndGet(string commId, string owner, int slot, long add, string author)
    {
        var req = new ProtoWriter().Message(1, User(owner)).Int32(2, slot).Int64(3, add);
        var (err, payload) = client.Request(RpcnCommand.TusAddAndGetVariable, commId, req);
        ServerErrors.ThrowTus(RpcnCommand.TusAddAndGetVariable, err);
        return ToVariable(new RpcnReader(payload).Proto()) ?? new TusVariable { Value = add };
    }

    public (TusVariable Variable, bool Written) TryAndSet(string commId, string owner, int slot, TusOpeType op, long value, long? compareValue, string author)
    {
        var req = new ProtoWriter().Message(1, User(owner)).Int32(2, slot).Int32(3, (int)op).Int64(4, value);
        if (compareValue is { } cv) req.Packed(7, [cv]);
        var (err, payload) = client.Request(RpcnCommand.TusTryAndSetVariable, commId, req);
        if (err == RpcnError.CondFail) return (GetVariable(commId, owner, slot) ?? new TusVariable(), false);
        ServerErrors.ThrowTus(RpcnCommand.TusTryAndSetVariable, err);
        return (ToVariable(new RpcnReader(payload).Proto()) ?? new TusVariable { Value = value }, true);
    }

    public void DeleteVariables(string commId, string owner, IEnumerable<int> slots)
    {
        var req = new ProtoWriter().Message(1, User(owner)).Packed(2, slots.Select(s => (long)s));
        var (err, _) = client.Request(RpcnCommand.TusDeleteMultiSlotVariable, commId, req);
        ServerErrors.ThrowTus(RpcnCommand.TusDeleteMultiSlotVariable, err);
    }

    public TusDataStatus? GetDataStatus(string commId, string owner, int slot)
    {
        var (err, payload) = client.Request(RpcnCommand.TusGetMultiSlotDataStatus, commId, new ProtoWriter().Message(1, User(owner)).Packed(2, [slot]));
        ServerErrors.ThrowTus(RpcnCommand.TusGetMultiSlotDataStatus, err);
        var st = new RpcnReader(payload).Proto().Messages(1);
        return st.Count > 0 ? ToStatus(st[0], 0) : null; // the status reply has no size; the data read knows it
    }

    public (TusDataStatus Status, byte[] Data)? GetData(string commId, string owner, int slot)
    {
        var (err, payload) = client.Request(RpcnCommand.TusGetData, commId, new ProtoWriter().Message(1, User(owner)).Int32(2, slot));
        ServerErrors.ThrowTus(RpcnCommand.TusGetData, err);
        var d = new RpcnReader(payload).Proto();
        var data = d.Bytes(2).ToArray();
        var st = ToStatus(d.Message(1), data.Length);
        return st == null ? null : (st, data);
    }

    public void SetData(string commId, string owner, int slot, byte[] data, byte[] info, string author)
    {
        var req = new ProtoWriter().Message(1, User(owner)).Int32(2, slot).Bytes(3, data).Bytes(4, info);
        var (err, _) = client.Request(RpcnCommand.TusSetData, commId, req);
        ServerErrors.ThrowTus(RpcnCommand.TusSetData, err);
    }

    public void DeleteData(string commId, string owner, IEnumerable<int> slots)
    {
        var req = new ProtoWriter().Message(1, User(owner)).Packed(2, slots.Select(s => (long)s));
        var (err, _) = client.Request(RpcnCommand.TusDeleteMultiSlotData, commId, req);
        ServerErrors.ThrowTus(RpcnCommand.TusDeleteMultiSlotData, err);
    }
}

/// <summary>Score rankings on an RPCN server (RPCS3 rpcn_client get_board_infos / record_score / get_score_*).</summary>
public sealed class RpcnScoreService(RpcnClient client) : IScoreService
{
    public ScoreBoard GetBoard(string commId, uint board)
    {
        var body = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, board);
        var id = RpcnClient.CommIdBytes(commId);
        var (err, payload) = client.Request(RpcnCommand.GetBoardInfos, [.. id, .. body]);
        if (err == RpcnError.NotFound) throw new NpServerException(ServerErrors.RANKING_BOARD_MASTER_NOT_FOUND, $"RPCN: board {board} of {commId} not found");
        if (err != RpcnError.NoError) throw new IOException($"RPCN GetBoardInfos: unexpected error {err}");
        var b = new RpcnReader(payload).Proto();
        return new ScoreBoard { RankLimit = b.UInt32(1), UpdateMode = b.UInt32(2), SortMode = b.UInt32(3) };
    }

    public bool Record(string commId, uint board, string onlineId, int pcId, long score, string comment, byte[] gameInfo, out uint tmpRank)
    {
        var req = new ProtoWriter().UInt32(1, board).Int32(2, pcId).Int64(3, score).String(4, comment).Bytes(5, gameInfo);
        var (err, payload) = client.Request(RpcnCommand.RecordScore, commId, req);
        tmpRank = 0;
        if (err == RpcnError.ScoreNotBest) return false;
        if (err != RpcnError.NoError) throw new IOException($"RPCN RecordScore: unexpected error {err}");
        tmpRank = new RpcnReader(payload).U32();
        return true;
    }

    static (List<RankedScore?> Rows, uint Total, ulong Last) ParseScores(byte[] payload, uint firstSerial)
    {
        var r = new RpcnReader(payload).Proto();
        var ranks = r.Messages(1);
        var comments = r.Strings(2);
        var infos = r.Messages(3);
        var rows = new List<RankedScore?>(ranks.Count);
        for (int i = 0; i < ranks.Count; i++)
        {
            var x = ranks[i];
            string npId = x.String(1);
            if (npId.Length == 0 || x.UInt64(7) == 0) { rows.Add(null); continue; } // no score for that player
            var e = new ScoreEntry
            {
                OnlineId = npId, PcId = x.Int32(3), Score = x.Int64(5), RecordDate = x.UInt64(7), HighestRank = x.UInt32(4),
                Comment = i < comments.Count ? comments[i] : "", GameInfo = i < infos.Count ? infos[i].Bytes(1).ToArray() : [],
                GameData = x.Bool(6) ? [] : null,
            };
            rows.Add(new RankedScore(e, firstSerial + (uint)i, x.UInt32(4)));
        }
        return (rows, r.UInt32(5), r.UInt64(4));
    }

    public (List<RankedScore> Entries, uint Total, ulong LastSortDate) GetRange(string commId, uint board, uint startSerialRank, int count)
    {
        uint start = Math.Max(startSerialRank, 1);
        var req = new ProtoWriter().UInt32(1, board).UInt32(2, start).UInt32(3, (uint)Math.Max(count, 0)).Bool(4, true).Bool(5, true);
        var (err, payload) = client.Request(RpcnCommand.GetScoreRange, commId, req);
        if (err != RpcnError.NoError) throw new IOException($"RPCN GetScoreRange: unexpected error {err}");
        var (rows, total, last) = ParseScores(payload, start);
        return (rows.Where(x => x != null).Select(x => x!.Value).ToList(), total, last);
    }

    public (List<RankedScore?> Entries, uint Total, ulong LastSortDate) GetByIds(string commId, uint board, IReadOnlyList<(string OnlineId, int PcId)> ids)
    {
        var req = new ProtoWriter().UInt32(1, board);
        foreach (var (id, pc) in ids) req.Message(2, new ProtoWriter().String(1, id).Int32(2, pc));
        req.Bool(3, true).Bool(4, true);
        var (err, payload) = client.Request(RpcnCommand.GetScoreNpid, commId, req);
        if (err != RpcnError.NoError) throw new IOException($"RPCN GetScoreNpid: unexpected error {err}");
        var (rows, total, last) = ParseScores(payload, 0);
        // the serial rank of a looked-up entry is its rank
        for (int i = 0; i < rows.Count; i++)
            if (rows[i] is { } v) rows[i] = v with { SerialRank = v.Rank };
        while (rows.Count < ids.Count) rows.Add(null);
        return (rows, total, last);
    }

    public bool RecordGameData(string commId, uint board, string onlineId, int pcId, long score, byte[] data)
    {
        var req = new ProtoWriter().UInt32(1, board).Int32(2, pcId).Int64(3, score).ToArray();
        var (err, _) = client.Request(RpcnCommand.RecordScoreData, RpcnClient.WithCommId(commId, req, data, trailerSized: true));
        return err switch
        {
            RpcnError.NoError => true,
            RpcnError.ScoreInvalid => false,
            RpcnError.ScoreHasData => throw new NpServerException(ServerErrors.GAME_DATA_ALREADY_EXISTS, "RPCN: the score already has game data"),
            RpcnError.NotFound => throw new NpServerException(ServerErrors.RANKING_STORE_NOT_FOUND, "RPCN: no such score"),
            _ => throw new IOException($"RPCN RecordScoreData: unexpected error {err}"),
        };
    }

    public byte[]? GetGameData(string commId, uint board, string onlineId, int pcId)
    {
        var req = new ProtoWriter().UInt32(1, board).String(2, onlineId).Int32(3, pcId);
        var (err, payload) = client.Request(RpcnCommand.GetScoreData, commId, req);
        if (err == RpcnError.NotFound) return null;
        if (err != RpcnError.NoError) throw new IOException($"RPCN GetScoreData: unexpected error {err}");
        var r = new RpcnReader(payload);
        var d = r.Raw();
        return r.Error ? null : d;
    }
}
