using System.Reflection;
using System.Text;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>Local PSN services: TUS persistence, score rankings, Matching2 callbacks, auth tickets and the trophy parser.</summary>
[Collection("guest")]
public sealed class NpLocalTests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vs-np-" + Guid.NewGuid().ToString("N"));
    readonly PsnAccount _savedAccount = PsnAccount.Current;

    public void Dispose()
    {
        PsnAccount.Current = _savedAccount;
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    static int Call<T>(T module, string name, params object[] args) =>
        Convert.ToInt32(typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!);

    uint Alloc(uint size, byte fill = 0)
    {
        uint p = fx.Memory.Alloc(size, "np test", MemoryRegion.User, 16);
        GuestMemory.Fill(p, fill, size);
        return p;
    }

    uint CommId(string id = "NPWR99999")
    {
        uint p = Alloc(12);
        GuestMemory.WriteCString(p, id);
        return p;
    }

    uint NpId(string online)
    {
        uint p = Alloc(0x24);
        NpManager.WriteNpId(p, online);
        return p;
    }

    uint Ints(params int[] v)
    {
        uint p = Alloc((uint)(v.Length * 4));
        for (int i = 0; i < v.Length; i++) GuestMemory.W32(p + (uint)i * 4, (uint)v[i]);
        return p;
    }

    uint Longs(params long[] v)
    {
        uint p = Alloc((uint)(v.Length * 8));
        for (int i = 0; i < v.Length; i++) GuestMemory.Write(p + (uint)i * 8, v[i]);
        return p;
    }

    // ---------------------------------------------------------------- TUS store

    [Fact]
    public void TusStorePersistsVariablesAndData()
    {
        var tus = new TusStore(_root);
        tus.SetVariables("NPWR00001_00", "alice", [(0, 10), (5, -3)], "alice");
        tus.SetVariables("NPWR00001_00", "alice", [(0, 12)], "bob");

        var again = new TusStore(_root); // a new run reads the files
        var v0 = again.GetVariable("NPWR00001_00", "alice", 0)!;
        Assert.Equal(12, v0.Value);
        Assert.Equal(10, v0.OldValue);
        Assert.Equal("bob", v0.Author);
        Assert.True(v0.LastChanged > 0);
        Assert.Equal(-3, again.GetVariable("NPWR00001_00", "alice", 5)!.Value);
        Assert.Null(again.GetVariable("NPWR00001_00", "alice", 1));
        Assert.Null(again.GetVariable("NPWR00001_00", "bob", 0));      // per owner
        Assert.Null(again.GetVariable("NPWR00002_00", "alice", 0));    // per title

        Assert.Equal(17, again.AddAndGet("NPWR00001_00", "alice", 0, 5, "alice").Value);
        Assert.Equal(7, again.AddAndGet("NPWR00001_00", "alice", 9, 7, "alice").Value); // empty slot starts at 0

        // TryAndSet GREATER_THAN keeps a maximum
        var (kept, w1) = again.TryAndSet("NPWR00001_00", "alice", 0, TusOpeType.GreaterThan, 3, null, "alice");
        Assert.False(w1);
        Assert.Equal(17, kept.Value);
        var (set, w2) = again.TryAndSet("NPWR00001_00", "alice", 0, TusOpeType.GreaterThan, 40, null, "alice");
        Assert.True(w2);
        Assert.Equal(40, set.Value);
        Assert.Equal(17, set.OldValue);
        // with a compare value: compare-and-swap
        Assert.True(again.TryAndSet("NPWR00001_00", "alice", 0, TusOpeType.Equal, 41, 40, "alice").Written);
        Assert.False(again.TryAndSet("NPWR00001_00", "alice", 0, TusOpeType.Equal, 42, 40, "alice").Written);

        again.DeleteVariables("NPWR00001_00", "alice", [0, 5]);
        Assert.Null(new TusStore(_root).GetVariable("NPWR00001_00", "alice", 0));

        byte[] blob = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        again.SetData("NPWR00001_00", "alice", 2, blob, [1, 2, 3], "alice");
        var d = new TusStore(_root).GetData("NPWR00001_00", "alice", 2)!.Value;
        Assert.Equal(blob, d.Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, d.Status.Info);
        Assert.Equal(3000, d.Status.Size);
        again.DeleteData("NPWR00001_00", "alice", [2]);
        Assert.Null(again.GetData("NPWR00001_00", "alice", 2));
    }

    [Fact]
    public void SafeNameKeepsOnlineIdsAndEncodesTheRest()
    {
        Assert.Equal("VitaSharp", LocalPsn.SafeName("VitaSharp"));
        Assert.Equal("NPWR11170_00", LocalPsn.SafeName("NPWR11170_00"));
        Assert.StartsWith("x-", LocalPsn.SafeName("a/b"));
        Assert.StartsWith("x-", LocalPsn.SafeName(".."));
        Assert.StartsWith("x-", LocalPsn.SafeName(""));
        Assert.Equal(Path.Combine(Path.GetFullPath("data"), "np"), LocalPsn.DefaultRoot(Path.Combine("data", "fs")));
    }

    // ---------------------------------------------------------------- TUS module

    NpTus NewTus()
    {
        var m = new NpTus { StoreOverride = new TusStore(_root) };
        Assert.Equal(0, Call(m, "sceNpTusInit", 0, 0, 0u));
        return m;
    }

    int TusRequest(NpTus m)
    {
        int ctx = Call(m, "sceNpTusCreateTitleCtx", CommId(), 0u, 0u);
        Assert.True(ctx > 0);
        int req = Call(m, "sceNpTusCreateRequest", ctx);
        Assert.True(req > 0);
        return req;
    }

    [Fact]
    public void TusVariablesThroughTheLibrary()
    {
        PsnAccount.Current = PsnAccount.Create(true, "Alice");
        var m = NewTus();
        uint me = NpId("Alice");

        int req = TusRequest(m);
        Assert.Equal(0, Call(m, "sceNpTusSetMultiSlotVariable", req, me, Ints(1, 3), Longs(100, -7), 2, 0u));
        Assert.Equal(NpCommunity.TRANSACTION_ALREADY_END, Call(m, "sceNpTusSetMultiSlotVariable", req, me, Ints(1), Longs(1), 1, 0u));

        // async get: 0 now, the result (number of slots) from Poll
        int get = TusRequest(m);
        uint vars = Alloc(3 * NpTus.VariableSize, 0xCC), result = Alloc(4);
        Assert.Equal(0, Call(m, "sceNpTusGetMultiSlotVariableAsync", get, me, Ints(1, 2, 3), vars, 3 * NpTus.VariableSize, 3, 0u));
        Assert.Equal(0, Call(m, "sceNpTusPollAsync", get, result));
        Assert.Equal(3u, GuestMemory.R32(result));
        Assert.Equal("Alice", NpCommunity.ReadOnlineId(vars));                        // ownerId
        Assert.Equal(1u, GuestMemory.R32(vars + 36));                                 // hasData
        Assert.True(GuestMemory.Read<ulong>(vars + 40) > 0);                          // lastChangedDate
        Assert.Equal("Alice", NpCommunity.ReadOnlineId(vars + 52));                   // lastChangedAuthorId
        Assert.Equal(100, GuestMemory.Read<long>(vars + 88));                         // variable
        uint slot2 = vars + NpTus.VariableSize;
        Assert.Equal("Alice", NpCommunity.ReadOnlineId(slot2));
        Assert.Equal(0u, GuestMemory.R32(slot2 + 36));                                // empty slot: hasData 0
        Assert.Equal(-7, GuestMemory.Read<long>(vars + 2 * NpTus.VariableSize + 88));

        // wrong array size
        Assert.Equal(NpCommunity.INVALID_ALIGNMENT, Call(m, "sceNpTusGetMultiSlotVariable", TusRequest(m), me, Ints(1), vars, 50u, 1, 0u));

        uint outVar = Alloc(NpTus.VariableSize);
        Assert.Equal(0, Call(m, "sceNpTusAddAndGetVariable", TusRequest(m), me, 1, 5L, outVar, NpTus.VariableSize, 0u));
        Assert.Equal(105, GuestMemory.Read<long>(outVar + 88));
        Assert.Equal(100, GuestMemory.Read<long>(outVar + 96));                       // oldVariable
        Assert.Equal(NpCommunity.SERVER_CONDITIONS_NOT_SATISFIED,
            Call(m, "sceNpTusTryAndSetVariable", TusRequest(m), me, 1, 3 /* GREATER_THAN */, 50L, outVar, NpTus.VariableSize, 0u));
        Assert.Equal(105, GuestMemory.Read<long>(outVar + 88));                       // current value reported

        // persisted: a new library instance (next boot) sees the values
        var m2 = NewTus();
        uint v = Alloc(NpTus.VariableSize);
        Assert.Equal(1, Call(m2, "sceNpTusGetMultiSlotVariable", TusRequest(m2), me, Ints(1), v, NpTus.VariableSize, 1, 0u));
        Assert.Equal(105, GuestMemory.Read<long>(v + 88));

        Assert.Equal(0, Call(m2, "sceNpTusDeleteMultiSlotVariable", TusRequest(m2), me, Ints(1), 1, 0u));
        Assert.Equal(1, Call(m2, "sceNpTusGetMultiSlotVariable", TusRequest(m2), me, Ints(1), v, NpTus.VariableSize, 1, 0u));
        Assert.Equal(0u, GuestMemory.R32(v + 36));
    }

    [Fact]
    public void TusDataInChunks()
    {
        PsnAccount.Current = PsnAccount.Create(true, "Alice");
        var m = NewTus();
        uint me = NpId("Alice");
        byte[] save = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
        uint data = Alloc(1000);
        save.CopyTo(GuestMemory.Span(data, 1000));
        uint info = Alloc(NpTus.DataInfoSize);
        GuestMemory.W32(info, 4);
        GuestMemory.W32(info + 8, 0xDEADBEEF);

        // empty slot
        uint status = Alloc(NpTus.DataStatusSize, 0xCC), recv = Alloc(1000);
        Assert.Equal(NpCommunity.SERVER_USER_STORAGE_DATA_NOT_FOUND,
            Call(m, "sceNpTusGetData", TusRequest(m), me, 4, status, NpTus.DataStatusSize, recv, 1000u, 0u));
        Assert.Equal("Alice", NpCommunity.ReadOnlineId(status));
        Assert.Equal(0u, GuestMemory.R32(status + 36));

        // two chunks on one request (the second call continues the transaction)
        int set = TusRequest(m);
        Assert.Equal(0, Call(m, "sceNpTusSetData", set, me, 4, 1000u, 600u, data, info, NpTus.DataInfoSize, 0u));
        Assert.Null(new TusStore(_root).GetData("NPWR99999_00", "Alice", 4)); // not stored before the last part
        Assert.Equal(0, Call(m, "sceNpTusSetData", set, me, 4, 1000u, 400u, data + 600, info, NpTus.DataInfoSize, 0u));
        Assert.Equal(NpCommunity.TRANSACTION_ALREADY_END, Call(m, "sceNpTusSetData", set, me, 4, 1000u, 400u, data, info, NpTus.DataInfoSize, 0u));

        // read back in two parts: the result is the number of bytes copied
        int get = TusRequest(m);
        Assert.Equal(700, Call(m, "sceNpTusGetData", get, me, 4, status, NpTus.DataStatusSize, recv, 700u, 0u));
        Assert.Equal(1u, GuestMemory.R32(status + 36));            // hasData
        Assert.Equal(recv, GuestMemory.R32(status + 84));          // data pointer
        Assert.Equal(1000u, GuestMemory.R32(status + 88));         // dataSize
        Assert.Equal(4u, GuestMemory.R32(status + 96));            // info.infoSize
        Assert.Equal(0xDEADBEEFu, GuestMemory.R32(status + 104));  // info.data
        Assert.Equal(300, Call(m, "sceNpTusGetData", get, me, 4, status, NpTus.DataStatusSize, recv + 700, 300u, 0u));
        Assert.Equal(save, GuestMemory.Span(recv, 1000).ToArray());

        uint statuses = Alloc(2 * NpTus.DataStatusSize);
        Assert.Equal(2, Call(m, "sceNpTusGetMultiSlotDataStatus", TusRequest(m), me, Ints(4, 5), statuses, 2 * NpTus.DataStatusSize, 2, 0u));
        Assert.Equal(1u, GuestMemory.R32(statuses + 36));
        Assert.Equal(0u, GuestMemory.R32(statuses + NpTus.DataStatusSize + 36));

        Assert.Equal(0, Call(m, "sceNpTusDeleteMultiSlotData", TusRequest(m), me, Ints(4), 1, 0u));
        Assert.Null(new TusStore(_root).GetData("NPWR99999_00", "Alice", 4));
    }

    [Fact]
    public void TusWithoutLocalServicesFailsLikeAnUnreachableServer()
    {
        PsnAccount.Current = PsnAccount.Create(true, "Alice", localServices: false);
        var m = NewTus();
        uint vars = Alloc(NpTus.VariableSize, 0xCC);
        Assert.Equal(PsnAccount.ServerUnreachable,
            Call(m, "sceNpTusGetMultiSlotVariable", TusRequest(m), NpId("Alice"), Ints(1), vars, NpTus.VariableSize, 1, 0u));
        Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(vars)); // untouched
    }

    // ---------------------------------------------------------------- score

    [Fact]
    public void ScoreStoreRanksBestScores()
    {
        var s = new ScoreStore(_root);
        Assert.True(s.Record("T", 1, "alice", 0, 500, "", [], out uint r1));
        Assert.Equal(1u, r1);
        Assert.True(s.Record("T", 1, "bob", 0, 800, "hi", [9], out uint r2));
        Assert.Equal(1u, r2);
        Assert.True(s.Record("T", 1, "carol", 0, 500, "", [], out uint r3));
        Assert.Equal(2u, r3);                                     // ties share the rank
        Assert.False(s.Record("T", 1, "bob", 0, 100, "", [], out uint r4)); // not the best: kept
        Assert.Equal(1u, r4);
        Assert.True(s.Record("T", 1, "bob", 1, 50, "", [], out _)); // another character of bob

        var (rows, total, last) = new ScoreStore(_root).GetRange("T", 1, 1, 10);
        Assert.Equal(4u, total);
        Assert.True(last > 0);
        Assert.Equal(["bob", "alice", "carol", "bob"], rows.Select(r => r.Entry.OnlineId).ToArray());
        Assert.Equal([1u, 2u, 2u, 4u], rows.Select(r => r.Rank).ToArray());
        Assert.Equal([1u, 2u, 3u, 4u], rows.Select(r => r.SerialRank).ToArray());
        Assert.Equal("hi", rows[0].Entry.Comment);
        Assert.Equal(["alice", "carol"], s.GetRange("T", 1, 2, 2).Entries.Select(r => r.Entry.OnlineId).ToArray());

        var (byId, _, _) = s.GetByIds("T", 1, [("carol", 0), ("nobody", 0)]);
        Assert.Equal(2u, byId[0]!.Value.Rank);
        Assert.Null(byId[1]);

        Assert.True(s.RecordGameData("T", 1, "alice", 0, 500, [1, 2, 3]));
        Assert.False(s.RecordGameData("T", 1, "alice", 0, 499, [1]));
        Assert.Equal(new byte[] { 1, 2, 3 }, s.GetGameData("T", 1, "alice", 0));
    }

    [Fact]
    public void ScoreAscendingBoardKeepsTheLowest()
    {
        var s = new ScoreStore(_root);
        s.Record("T", 2, "a", 0, 90, "", [], out _);
        var path = Directory.GetFiles(Path.Combine(_root, "T"), "board_2.json").Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"SortMode\": 0", "\"SortMode\": 1")); // a lap-time board
        Assert.True(s.Record("T", 2, "a", 0, 80, "", [], out _));
        Assert.False(s.Record("T", 2, "a", 0, 85, "", [], out _));
        s.Record("T", 2, "b", 0, 70, "", [], out uint rb);
        Assert.Equal(1u, rb);
        Assert.Equal(80, s.GetByIds("T", 2, [("a", 0)]).Entries[0]!.Value.Entry.Score);
    }

    [Fact]
    public void ScoreThroughTheLibrary()
    {
        PsnAccount.Current = PsnAccount.Create(true, "Alice");
        var m = new NpScore { StoreOverride = new ScoreStore(_root) };
        Assert.Equal(0, Call(m, "sceNpScoreInit", 0, 0, 0u));
        int ctx = Call(m, "sceNpScoreCreateTitleCtx", CommId(), 0u, 0u);
        int Req() => Call(m, "sceNpScoreCreateRequest", ctx);

        // no score yet
        uint ranks = Alloc(4 * 128), total = Alloc(4), lastSort = Alloc(8);
        Assert.Equal(NpCommunity.SERVER_GAME_RANKING_NOT_FOUND,
            Call(m, "sceNpScoreGetRankingByRange", Req(), 3u, 1u, ranks, 4 * 128u, 0u, 0u, 0u, 0u, 4u, lastSort, total, 0u));

        uint comment = Alloc(64);
        GuestMemory.WriteCString(comment, "gg");
        uint info = Alloc(196);
        GuestMemory.W32(info, 2);
        GuestMemory.Write<ushort>(info + 4, 0xBEEF);
        uint tmpRank = Alloc(4, 0xCC);
        Assert.Equal(0, Call(m, "sceNpScoreRecordScore", Req(), 3u, 1234L, comment, info, tmpRank, 0u, 0u));
        Assert.Equal(1u, GuestMemory.R32(tmpRank));
        Assert.Equal(NpCommunity.SERVER_NOT_BEST_SCORE, Call(m, "sceNpScoreRecordScore", Req(), 3u, 10L, 0u, 0u, tmpRank, 0u, 0u));

        // async range query, with comments and variable-size game info
        uint comments = Alloc(4 * 64), infos = Alloc(4 * 196), result = Alloc(4);
        int q = Req();
        Assert.Equal(0, Call(m, "sceNpScoreGetRankingByRangeAsync", q, 3u, 1u, ranks, 4 * 128u, comments, 4 * 64u, infos, 4 * 196u, 4u, lastSort, total, 0u));
        Assert.Equal(0, Call(m, "sceNpScorePollAsync", q, result));
        Assert.Equal(1u, GuestMemory.R32(result));                  // one entry
        Assert.Equal(1u, GuestMemory.R32(total));
        Assert.Equal("Alice", NpCommunity.ReadOnlineId(ranks));     // npId
        Assert.Equal("Alice", GuestMemory.ReadCString(ranks + 36)); // onlineName
        Assert.Equal(1u, GuestMemory.R32(ranks + 92));              // serialRank
        Assert.Equal(1u, GuestMemory.R32(ranks + 96));              // rank
        Assert.Equal(1234, GuestMemory.Read<long>(ranks + 112));    // scoreValue
        Assert.True(GuestMemory.Read<ulong>(ranks + 120) > 0);      // recordDate
        Assert.Equal("gg", GuestMemory.ReadCString(comments));
        Assert.Equal(2u, GuestMemory.R32(infos));
        Assert.Equal(0xBEEF, GuestMemory.Read<ushort>(infos + 4));

        // by NpId: SceNpScorePlayerRankData (hasData + rankData at +8), one per id asked
        uint ids = Alloc(2 * 36);
        NpManager.WriteNpId(ids, "Alice");
        NpManager.WriteNpId(ids + 36, "Bob");
        uint players = Alloc(2 * 136, 0xCC);
        Assert.Equal(2, Call(m, "sceNpScoreGetRankingByNpId", Req(), 3u, ids, 72u, players, 2 * 136u, 0u, 0u, 0u, 0u, 2u, lastSort, total, 0u));
        Assert.Equal(1u, GuestMemory.R32(players));
        Assert.Equal(1234, GuestMemory.Read<long>(players + 8 + 112));
        Assert.Equal(0u, GuestMemory.R32(players + 136));           // Bob has no score

        // friends: none, only the user (includeSelf)
        Assert.Equal(1, Call(m, "sceNpScoreGetFriendsRanking", Req(), 3u, 1, ranks, 4 * 128u, 0u, 0u, 0u, 0u, 4u, lastSort, total, 0u));

        uint board = Alloc(20);
        Assert.Equal(0, Call(m, "sceNpScoreGetBoardInfo", Req(), 3u, board, 0u));
        Assert.Equal(0u, GuestMemory.R32(board + 8)); // sortMode descending
    }

    // ---------------------------------------------------------------- Matching2

    sealed record Cb(uint Func, uint[] Args, uint[] Data);

    (NpMatching2 M, List<Cb> Calls) NewMatching2()
    {
        var m = new NpMatching2 { MemoryOverride = fx.Memory };
        var calls = new List<Cb>();
        // the response buffer is freed after the callback: copy it while the "callback" runs
        m.Callbacks.Sink = c => calls.Add(new Cb(c.Func, c.Args,
            c.Args.Length >= 5 && c.Args[4] != 0 ? Enumerable.Range(0, 4).Select(i => GuestMemory.R32(c.Args[4] + (uint)i * 4)).ToArray() : []));
        Assert.Equal(0, Call(m, "sceNpMatching2Init", 0u, 160, 0, 0u));
        return (m, calls);
    }

    ushort Context(NpMatching2 m)
    {
        uint id = Alloc(4);
        Assert.Equal(0, Call(m, "sceNpMatching2CreateContext", NpId("Alice"), CommId(), 0u, new Ptr<ushort>(id)));
        return GuestMemory.Read<ushort>(id);
    }

    [Fact]
    public void Matching2ContextStartCallsBack()
    {
        PsnAccount.Current = PsnAccount.Default;
        var (m, calls) = NewMatching2();
        ushort ctx = Context(m);
        Assert.Equal(0, Call(m, "sceNpMatching2RegisterContextCallback", (uint)ctx, 0x81000001u, 0x1234u));
        uint reqId = Alloc(4);
        Assert.NotEqual(0, Call(m, "sceNpMatching2SearchRoom", ctx, Alloc(16), 0u, reqId)); // not started yet

        Assert.Equal(0, Call(m, "sceNpMatching2ContextStart", ctx, 0UL));
        var start = Assert.Single(calls);
        Assert.Equal(0x81000001u, start.Func);
        Assert.Equal([ctx, (uint)NpMatching2.CONTEXT_EVENT_START, (uint)NpMatching2.EVENT_CAUSE_CONTEXT_ACTION, 0u, 0x1234u], start.Args);
        Assert.NotEqual(0, Call(m, "sceNpMatching2ContextStart", ctx, 0UL)); // already started

        uint server = Alloc(4);
        Assert.Equal(0, Call(m, "sceNpMatching2GetServerLocal", ctx, server));
        Assert.Equal(NpMatching2.ServerId, GuestMemory.Read<ushort>(server));
        Assert.Equal(1, GuestMemory.Read<byte>(server + 2)); // AVAILABLE

        Assert.Equal(0, Call(m, "sceNpMatching2ContextStop", ctx));
        Assert.Equal((uint)NpMatching2.CONTEXT_EVENT_STOP, calls[^1].Args[1]);
    }

    [Fact]
    public void Matching2RequestsAlwaysAnswer()
    {
        PsnAccount.Current = PsnAccount.Default;
        var (m, calls) = NewMatching2();
        ushort ctx = Context(m);
        Assert.Equal(0, Call(m, "sceNpMatching2ContextStart", ctx, 0UL)); // no context callback registered: nothing called
        Assert.Empty(calls);

        // request option: callback + arg
        uint opt = Alloc(16);
        GuestMemory.W32(opt, 0x81000101u);
        GuestMemory.W32(opt + 4, 0x55u);
        uint reqId = Alloc(4);

        Assert.Equal(0, Call(m, "sceNpMatching2SearchRoom", ctx, Alloc(64), opt, reqId));
        var search = calls[^1];
        Assert.Equal(0x81000101u, search.Func);
        Assert.Equal(GuestMemory.R32(reqId), search.Args[1]);
        Assert.Equal((uint)NpMatching2.EV_SEARCH_ROOM, search.Args[2]);
        Assert.Equal(0u, search.Args[3]);                      // no error
        Assert.NotEqual(0u, search.Args[4]);                   // response
        Assert.Equal(0u, search.Data[1]);                      // range.total = 0 rooms
        Assert.Equal(0u, search.Data[3]);                      // no room data
        Assert.Equal(0x55u, search.Args[5]);

        Assert.Equal(0, Call(m, "sceNpMatching2GetWorldInfoList", ctx, Alloc(4), opt, reqId));
        Assert.Equal(1u, calls[^1].Data[1]);                   // one world

        Assert.Equal(0, Call(m, "sceNpMatching2CreateJoinRoom", ctx, Alloc(64), opt, reqId));
        Assert.Equal(unchecked((uint)NpMatching2.SERVER_SERVICE_UNAVAILABLE), calls[^1].Args[3]);
        Assert.Equal(0u, calls[^1].Args[4]);                   // no response data with an error

        // default request option of the context
        uint def = Alloc(16);
        GuestMemory.W32(def, 0x81000201u);
        Assert.Equal(0, Call(m, "sceNpMatching2SetDefaultRequestOptParam", ctx, def));
        Assert.Equal(0, Call(m, "sceNpMatching2JoinRoom", ctx, Alloc(64), 0u, reqId));
        Assert.Equal(0x81000201u, calls[^1].Func);
        Assert.Equal(unchecked((uint)NpMatching2.SERVER_NO_SUCH_ROOM), calls[^1].Args[3]);
        Assert.True(calls.Select(c => c.Args[1]).Distinct().Count() == calls.Count); // fresh request ids
    }

    [Fact]
    public void Matching2WithoutLocalServicesRefusesToStart()
    {
        PsnAccount.Current = PsnAccount.Create(true, null, localServices: false);
        var (m, calls) = NewMatching2();
        ushort ctx = Context(m);
        Assert.Equal(0, Call(m, "sceNpMatching2RegisterContextCallback", (uint)ctx, 0x81000001u, 0u));
        Assert.Equal(PsnAccount.ServerUnreachable, Call(m, "sceNpMatching2ContextStart", ctx, 0UL));
        Assert.Empty(calls);
    }

    [Fact]
    public void Matching2Ps4StyleContextCallback()
    {
        PsnAccount.Current = PsnAccount.Default;
        var (m, calls) = NewMatching2();
        Assert.Equal(0, Call(m, "sceNpMatching2RegisterContextCallback", 0x81000301u, 0x77u, 0u)); // (cb, arg), no context yet
        ushort ctx = Context(m);
        Assert.Equal(0, Call(m, "sceNpMatching2ContextStart", ctx, 0UL));
        Assert.Equal(0x81000301u, Assert.Single(calls).Func);
        Assert.Equal(0x77u, calls[0].Args[4]);
    }

    // ---------------------------------------------------------------- auth tickets

    [Fact]
    public void TicketRoundTrip()
    {
        var ents = new List<NpTicket.Entitlement> { NpTicket.Entitlement.FromContent("UP0001-PCSE00914_00-DLCPACK000000001", "PCSE00914", "DLC1") };
        Assert.Equal("PCSE00914_00-DLCPACK000000001", ents[0].Id);
        Assert.True(ents[0].Matches("DLCPACK000000001"));
        Assert.True(ents[0].Matches("UP0001-PCSE00914_00-DLCPACK000000001"));
        var bytes = NpTicket.Build("Alice", "UP0001-PCSE00914_00", ents, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0x21u, bytes[0]);
        Assert.Equal((uint)bytes.Length - 8, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));
        var t = NpTicket.Parse(bytes)!;
        Assert.Equal("Alice", t.OnlineId);
        Assert.Equal(20, t.Serial.Length);
        Assert.Equal("UP0001-PCSE00914_00", Encoding.ASCII.GetString(t.ServiceId).TrimEnd('\0'));
        Assert.Equal((ulong)new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), t.IssuedDate);
        Assert.True(t.ExpireDate > t.IssuedDate);
        Assert.Equal(NpTicket.AccountIdOf("Alice"), t.AccountId);
        Assert.Equal("PCSE00914_00-DLCPACK000000001", Assert.Single(t.Entitlements).Id);
        Assert.Null(NpTicket.Parse([1, 2, 3, 4, 5, 6, 7, 8]));

        uint p = Alloc(256);
        Assert.True(t.WriteParam(5, p));
        Assert.Equal("Alice", GuestMemory.ReadCString(p, 32));
        Assert.False(t.WriteParam(99, p));
    }

    [Fact]
    public void AuthTicketThroughTheLibrary()
    {
        PsnAccount.Current = PsnAccount.Create(true, "Alice");
        var np = new NpManager();
        uint param = Alloc(0x24);
        GuestMemory.W32(param, 0x24);
        uint service = Alloc(0x18);
        GuestMemory.WriteCString(service, "UP0001-NPXX00000_00");
        GuestMemory.W32(param + 8, service);
        int req = Call(np, "sceNpAuthCreateStartRequest", param);
        Assert.True(req > 0);
        uint buf = Alloc(1024);
        Assert.Equal(0, Call(np, "sceNpAuthGetTicket", req, buf, 1024u));
        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(GuestMemory.Span(buf + 4, 4)) + 8;
        uint pr = Alloc(256);
        Assert.Equal(0, Call(np, "sceNpAuthGetTicketParam", buf, size, 5, pr));
        Assert.Equal("Alice", GuestMemory.ReadCString(pr, 32));
        Assert.Equal(0, Call(np, "sceNpAuthGetTicketParam", buf, size, 8, pr));
        Assert.Equal("UP0001-NPXX00000_00", GuestMemory.ReadCString(pr, 24));
        Assert.Equal(0, Call(np, "sceNpAuthGetEntitlementIdList", buf, size, Alloc(32), 1u)); // no DLC installed
        Assert.NotEqual(0, Call(np, "sceNpAuthGetEntitlementById", buf, size, "DLC", Alloc(64)));
    }

    // ---------------------------------------------------------------- trophies

    /// <summary>A TRP archive with the given files (big-endian header, 0x40-byte entries).</summary>
    static byte[] BuildTrp(params (string Name, byte[] Data)[] files)
    {
        const int entrySize = 0x40;
        int dataStart = 0x40 + files.Length * entrySize;
        var ms = new MemoryStream();
        var header = new byte[dataStart];
        BitConverter.GetBytes(TrophyPackage.Magic).CopyTo(header, 0);
        header[7] = 3; // version
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x10), (uint)files.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x14), entrySize);
        long off = dataStart;
        for (int i = 0; i < files.Length; i++)
        {
            int e = 0x40 + i * entrySize;
            Encoding.ASCII.GetBytes(files[i].Name).CopyTo(header, e);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(e + 0x20), (ulong)off);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(e + 0x28), (ulong)files[i].Data.Length);
            off += files[i].Data.Length;
        }
        ms.Write(header);
        foreach (var f in files) ms.Write(f.Data);
        return ms.ToArray();
    }

    static byte[] Sfm(string title, string first) => Encoding.UTF8.GetBytes(
        "<!--Sce-Np-Trophy-Signature: 0011223344-->\n" +
        "<trophyconf version=\"1.1\" platform=\"psp2\">\n" +
        $" <npcommid>NPWR00001_00</npcommid>\n <title-name>{title}</title-name>\n <title-detail>Detail</title-detail>\n" +
        " <trophy id=\"000\" hidden=\"no\" ttype=\"P\" pid=\"-1\"><name>All</name><detail>Get all</detail></trophy>\n" +
        $" <trophy id=\"001\" hidden=\"yes\" ttype=\"B\" pid=\"000\"><name>{first}</name><detail>Secret</detail></trophy>\n" +
        " <trophy id=\"002\" hidden=\"no\" ttype=\"G\" pid=\"000\" gid=\"1\"><name>Gold</name><detail>Hard</detail></trophy>\n" +
        " <group id=\"001\"><name>DLC</name><detail>Extra</detail></group>\n" +
        "</trophyconf>\0\0\0");

    [Fact]
    public void TrophyPackageParses()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G'];
        var trp = BuildTrp(("TROPCONF.SFM", Sfm("Conf", "x")), ("TROP.SFM", Sfm("Game", "First")), ("TROP_17.SFM", Sfm("Jogo", "Primeiro")),
            ("ICON0.PNG", png), ("TROP001.PNG", png));
        var p = TrophyPackage.Parse(trp, "NPWR00001_00");
        Assert.Equal("Game", p.Title);
        Assert.Equal("Detail", p.Detail);
        Assert.Equal(3, p.Trophies.Count);
        Assert.Equal(TrophyGrade.Platinum, p.Trophies[0].Grade);
        var first = p.Trophies[1];
        Assert.Equal((1, TrophyGrade.Bronze, true, 0, -1, "First"), (first.Id, first.Grade, first.Hidden, first.PlatinumLink, first.Group, first.Name));
        Assert.Equal((TrophyGrade.Gold, 1), (p.Trophies[2].Grade, p.Trophies[2].Group));
        Assert.Equal("DLC", Assert.Single(p.Groups).Name);
        Assert.Equal(png, p.GameIcon);
        Assert.Equal(png, p.TrophyIcon(1));
        Assert.Null(p.TrophyIcon(2));

        Assert.Equal("Primeiro", TrophyPackage.Parse(trp, "", 17).Trophies[1].Name); // pt-BR translation
        Assert.Equal("First", TrophyPackage.Parse(trp, "", 3).Trophies[1].Name);     // no Spanish: default texts

        var bad = (byte[])trp.Clone();
        bad[0] = 0;
        Assert.Throws<InvalidDataException>(() => TrophyPackage.Parse(bad));
        Assert.Throws<InvalidDataException>(() => TrophyPackage.Parse(BuildTrp(("ICON0.PNG", png))));
    }

    [Fact]
    public void TrophyUnlocksRoundTrip()
    {
        string path = TrophyUnlocks.PathFor(_root, "NPWR00001_00");
        Assert.Empty(TrophyUnlocks.Load(path));
        ulong tick = (ulong)(new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc).Ticks / 10);
        TrophyUnlocks.Save(path, new Dictionary<int, ulong> { [1] = tick, [2] = tick + 1 });
        var back = TrophyUnlocks.Load(path);
        Assert.Equal(tick, back[1]);
        Assert.Equal(new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc), TrophyUnlocks.TickToUtc(back[1]));
        Assert.EndsWith(Path.Combine("ux0", "user", "00", "trophy", "NPWR00001_00.json"), path);
    }
}
