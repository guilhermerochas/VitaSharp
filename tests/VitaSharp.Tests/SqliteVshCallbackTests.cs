using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSqliteVsh (the shell's SQLite) where it calls back into the guest, as the SQLite C API defines it: sqlite3_exec's
/// row callback (values and column names, a non-zero return aborts with SQLITE_ABORT), user scalar and aggregate
/// functions (sqlite3_create_function with sqlite3_value_* / sqlite3_result_* / sqlite3_user_data), a collation the
/// update / commit / rollback hooks (the previous argument comes back) and sqlite3_mprintf's %q / %Q / %w.
/// </summary>
[Collection("guest")]
public sealed class SqliteVshCallbackTests(GuestMemoryFixture fx) : IDisposable
{
    const int SQLITE_OK = 0, SQLITE_ABORT = 4, SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_INSERT = 18, SQLITE_DELETE = 9;
    readonly HleHarness _h = new(fx, "sqlitecb");
    uint _stubs, _next;

    public void Dispose() => _h.Dispose();

    SqliteVsh Sq => _h.Module<SqliteVsh>();

    uint HostStub(string name, Action<HleContext> body)
    {
        if (_stubs == 0) _stubs = _next = _h.Emu.Memory.Alloc(0x1000, "sqlite cb stubs");
        uint a = _next; _next += 16;
        Linker.WriteHleStub(a, _h.Emu.Hle.RegisterHostFunction(name, body), 0);
        _h.Emu.Code.Invalidate(a, 16);
        return a;
    }

    uint OpenDb()
    {
        uint pp = _h.Alloc(4);
        Assert.Equal(SQLITE_OK, Call(Sq, "Open", _h.Str(":memory:"), pp));
        return GuestMemory.R32(pp);
    }

    /// <summary>The single integer the query returns.</summary>
    long Scalar(uint db, string sql)
    {
        uint pp = _h.Alloc(4);
        Assert.Equal(SQLITE_OK, Call(Sq, "Prepare", db, _h.Str(sql), -1, pp, 0u));
        uint st = GuestMemory.R32(pp);
        Assert.Equal(SQLITE_ROW, Call(Sq, "Step", st));
        long v = (long)Invoke(Sq, "ColumnInt64", st, 0)!;
        Assert.Equal(SQLITE_DONE, Call(Sq, "Step", st));
        Assert.Equal(SQLITE_OK, Call(Sq, "StmtFinalize", st));
        return v;
    }

    [Fact]
    public void ExecPassesEachRowToTheCallback()
    {
        var rows = new List<string>();
        int stopAfter = int.MaxValue;
        uint cb = HostStub("sqlitecb:row", c =>
        {
            Assert.Equal(0x1234u, c.Gpr(0));
            int n = (int)c.Gpr(1);
            uint argv = c.Gpr(2), cols = c.Gpr(3);
            rows.Add(string.Join(",", Enumerable.Range(0, n).Select(i =>
                $"{GuestMemory.ReadCString(GuestMemory.R32(cols + (uint)i * 4))}={(GuestMemory.R32(argv + (uint)i * 4) is var v and not 0 ? GuestMemory.ReadCString(v) : "NULL")}")));
            c.Return(rows.Count >= stopAfter ? 1 : 0);
        });
        _h.OnGuest(_ =>
        {
            uint db = OpenDb(), err = _h.Alloc(4);
            Assert.Equal(SQLITE_OK, Call(Sq, "Exec", db, _h.Str("CREATE TABLE t(a, b); INSERT INTO t VALUES(1, 'x'), (2, NULL), (3, 'z');"), 0u, 0u, err));
            Assert.Equal(0u, GuestMemory.R32(err));
            Assert.Equal(SQLITE_OK, Call(Sq, "Exec", db, _h.Str("SELECT a, b FROM t ORDER BY a"), cb, 0x1234u, err));
            Assert.Equal(["a=1,b=x", "a=2,b=NULL", "a=3,b=z"], rows);
            // a non-zero return stops the statement: SQLITE_ABORT
            rows.Clear();
            stopAfter = 1;
            Assert.Equal(SQLITE_ABORT, Call(Sq, "Exec", db, _h.Str("SELECT a, b FROM t ORDER BY a"), cb, 0x1234u, err));
            Assert.Single(rows);
            // an error: its message through *errmsg
            Assert.NotEqual(SQLITE_OK, Call(Sq, "Exec", db, _h.Str("SELECT nope FROM t"), 0u, 0u, err));
            Assert.Contains("nope", GuestMemory.ReadCString(GuestMemory.R32(err)));
            Assert.Equal(SQLITE_OK, Call(Sq, "Close", db));
        });
    }

    [Fact]
    public void UserFunctionsAndCollationRunOnTheGuest()
    {
        uint twice = 0, sum = 0, fin = 0, rev = 0;
        long acc = 0;
        twice = HostStub("sqlitecb:twice", c =>
        {
            uint ctx = c.Gpr(0), argv = c.Gpr(2);
            Assert.Equal(1u, c.Gpr(1));
            Assert.Equal(0x77u, Convert.ToUInt32(Invoke(Sq, "UserData", ctx)));
            long x = (long)Invoke(Sq, "ValueInt64", GuestMemory.R32(argv))!;
            Invoke(Sq, "ResultInt64", ctx, x * 2);
        });
        sum = HostStub("sqlitecb:step", c => acc += (long)Invoke(Sq, "ValueInt64", GuestMemory.R32(c.Gpr(2)))!);
        fin = HostStub("sqlitecb:final", c => { Invoke(Sq, "ResultInt64", c.Gpr(0), acc * 10); acc = 0; });
        // compares the strings from their last character
        rev = HostStub("sqlitecb:rev", c =>
        {
            string a = new string(GuestMemory.ReadCString(c.Gpr(2), (int)c.Gpr(1)).Reverse().ToArray());
            string b = new string(GuestMemory.ReadCString(c.Gpr(4), (int)c.Gpr(3)).Reverse().ToArray());
            c.Return(string.CompareOrdinal(a, b));
        });
        _h.OnGuest(_ =>
        {
            uint db = OpenDb();
            Assert.Equal(SQLITE_OK, Call(Sq, "CreateFunction", db, _h.Str("twice"), 1, 1, 0x77u, twice, 0u, 0u));
            Assert.Equal(42L, Scalar(db, "SELECT twice(21)"));
            Assert.Equal(SQLITE_OK, Call(Sq, "CreateFunction", db, _h.Str("sum10"), 1, 1, 0u, 0u, sum, fin));
            Call(Sq, "Exec", db, _h.Str("CREATE TABLE n(v); INSERT INTO n VALUES(1), (2), (3), (4);"), 0u, 0u, 0u);
            Assert.Equal(100L, Scalar(db, "SELECT sum10(v) FROM n"));
            // a function with the wrong argument count is not found
            uint pp = _h.Alloc(4);
            Assert.NotEqual(SQLITE_OK, Call(Sq, "Prepare", db, _h.Str("SELECT twice(1, 2)"), -1, pp, 0u));
            Assert.Equal(SQLITE_OK, Call(Sq, "CreateCollation", db, _h.Str("rev"), 1, 0u, rev));
            Call(Sq, "Exec", db, _h.Str("CREATE TABLE w(s); INSERT INTO w VALUES('ab'), ('ba'), ('ca');"), 0u, 0u, 0u);
            Assert.Equal(SQLITE_OK, Call(Sq, "Prepare", db, _h.Str("SELECT s FROM w ORDER BY s COLLATE rev"), -1, pp, 0u));
            uint st = GuestMemory.R32(pp);
            var order = new List<string>();
            while (Call(Sq, "Step", st) == SQLITE_ROW) order.Add(GuestMemory.ReadCString(Convert.ToUInt32(Invoke(Sq, "ColumnText", st, 0))));
            Assert.Equal(["ba", "ca", "ab"], order); // "ab", "ac", "ba" reversed
            Call(Sq, "StmtFinalize", st);
            Call(Sq, "Close", db);
        });
    }

    [Fact]
    public void HooksReportChangesAndReturnThePreviousArgument()
    {
        var changes = new List<string>();
        int commits = 0, rollbacks = 0, veto = 0;
        uint update = HostStub("sqlitecb:update", c =>
            changes.Add($"{c.Gpr(0):X} {c.Gpr(1)} {GuestMemory.ReadCString(c.Gpr(2))}.{GuestMemory.ReadCString(c.Gpr(3))} {c.Gpr(4)}"));
        uint commit = HostStub("sqlitecb:commit", c => { commits++; c.Return(veto); });
        uint rollback = HostStub("sqlitecb:rollback", c => rollbacks++);
        _h.OnGuest(_ =>
        {
            uint db = OpenDb();
            Call(Sq, "Exec", db, _h.Str("CREATE TABLE t(a);"), 0u, 0u, 0u);
            Assert.Equal(0u, Convert.ToUInt32(Invoke(Sq, "UpdateHook", db, update, 0xA1u)));
            Assert.Equal(0u, Convert.ToUInt32(Invoke(Sq, "CommitHook", db, commit, 0xC1u)));
            Assert.Equal(0u, Convert.ToUInt32(Invoke(Sq, "RollbackHook", db, rollback, 0xB1u)));
            Assert.Equal(SQLITE_OK, Call(Sq, "Exec", db, _h.Str("INSERT INTO t VALUES(5); DELETE FROM t WHERE a = 5;"), 0u, 0u, 0u));
            Assert.Equal(2, changes.Count);
            Assert.StartsWith($"A1 {SQLITE_INSERT} main.t", changes[0]);
            Assert.StartsWith($"A1 {SQLITE_DELETE} main.t", changes[1]);
            Assert.Equal(2, commits);
            // a commit hook that returns non-zero turns the commit into a rollback
            veto = 1;
            Assert.NotEqual(SQLITE_OK, Call(Sq, "Exec", db, _h.Str("INSERT INTO t VALUES(6);"), 0u, 0u, 0u));
            Assert.Equal(1, rollbacks);
            // the row change was reported before the commit was refused
            Assert.StartsWith($"A1 {SQLITE_INSERT} main.t", changes[2]);
            Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM t"));
            // replacing a hook hands back the previous argument; NULL removes it
            Assert.Equal(0xA1u, Convert.ToUInt32(Invoke(Sq, "UpdateHook", db, 0u, 0u)));
            Assert.Equal(0xC1u, Convert.ToUInt32(Invoke(Sq, "CommitHook", db, 0u, 0u)));
            Assert.Equal(0xB1u, Convert.ToUInt32(Invoke(Sq, "RollbackHook", db, 0u, 0u)));
            veto = 0;
            Assert.Equal(SQLITE_OK, Call(Sq, "Exec", db, _h.Str("INSERT INTO t VALUES(7);"), 0u, 0u, 0u));
            Assert.Equal(3, changes.Count);
            Call(Sq, "Close", db);
        });
    }
}
