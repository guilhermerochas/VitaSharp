using System.Reflection;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>HLE batch 11: the SDK's SQLite (SceSqlite) on the SceSqliteVsh engine and the default entries.</summary>
[Collection("guest")]
public sealed class HleBatch11Tests(GuestMemoryFixture fx) : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hle11-" + Guid.NewGuid().ToString("N"));
    Emulator? _emu;

    Emulator Emu()
    {
        if (_emu != null) return _emu;
        Directory.CreateDirectory(_root);
        return _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
    }

    T Module<T>() => Emu().Hle.Modules.OfType<T>().Single();

    static object? Invoke(object module, string name, params object?[] args) =>
        module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);

    static int Call(object module, string name, params object?[] args) => Convert.ToInt32(Invoke(module, name, args));

    uint Alloc(uint size) { uint p = fx.Memory.Alloc(size, "hle11"); GuestMemory.Fill(p, 0, size); return p; }

    uint Str(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s + "\0");
        uint p = Alloc((uint)b.Length);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    uint Str16(string s)
    {
        byte[] b = Encoding.Unicode.GetBytes(s + "\0");
        uint p = Alloc((uint)b.Length);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    static string Read16(uint p)
    {
        var sb = new StringBuilder();
        for (; GuestMemory.Read<ushort>(p) != 0; p += 2) sb.Append((char)GuestMemory.Read<ushort>(p));
        return sb.ToString();
    }

    public void Dispose()
    {
        _emu?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void SdkSqliteRunsTheUtf16AndTableApi()
    {
        var sq = Module<SqliteVsh>();
        uint ppDb = Alloc(4), ppStmt = Alloc(4), tail = Alloc(4), res = Alloc(4), rows = Alloc(4), cols = Alloc(4), err = Alloc(4);
        Assert.Equal(0, Call(sq, "SdkInitialize"));
        Assert.Equal(0, Call(sq, "Open16", Str16(":memory:"), ppDb));
        uint db = GuestMemory.R32(ppDb);
        Assert.Equal(0, Call(sq, "SdkExec", db, Str("CREATE TABLE t(a INTEGER, b TEXT); INSERT INTO t VALUES(1, 'um'), (2, 'dois');"), 0u, 0u, err));

        // get_table: names first, then the rows; free_table takes it back
        Assert.Equal(0, Call(sq, "GetTable", db, Str("SELECT a, b FROM t ORDER BY a"), res, rows, cols, err));
        Assert.Equal((2u, 2u), (GuestMemory.R32(rows), GuestMemory.R32(cols)));
        uint table = GuestMemory.R32(res);
        string Cell(int i) => GuestMemory.ReadCString(GuestMemory.R32(table + (uint)i * 4));
        Assert.Equal(["a", "b", "1", "um", "2", "dois"], Enumerable.Range(0, 6).Select(Cell));
        Invoke(sq, "FreeTable", table);

        // prepare16 with a tail in UTF-16 units, bind_text16, column_text16 / name16
        uint sql = Str16("SELECT b FROM t WHERE b = ?; SELECT 1");
        Assert.Equal(0, Call(sq, "Prepare16", db, sql, -1, ppStmt, tail));
        Assert.Equal(sql + (uint)"SELECT b FROM t WHERE b = ?;".Length * 2, GuestMemory.R32(tail));
        uint st = GuestMemory.R32(ppStmt);
        Assert.Equal(0, Call(sq, "BindText16", st, 1, Str16("dois"), -1, 0u));
        Assert.Equal(100, Call(sq, "SdkStep", st));
        Assert.Equal("dois", Read16(Convert.ToUInt32(Invoke(sq, "ColumnText16", st, 0))));
        Assert.Equal(8, Call(sq, "ColumnBytes16", st, 0));
        Assert.Equal("b", Read16(Convert.ToUInt32(Invoke(sq, "ColumnName16", st, 0))));
        Assert.Equal("SELECT b FROM t WHERE b = ?;", GuestMemory.ReadCString(Convert.ToUInt32(Invoke(sq, "Sql", st))));

        // transfer_bindings: the bound text moves to a statement with the same parameter count
        Assert.Equal(0, Call(sq, "SdkPrepare", db, Str("SELECT a FROM t WHERE b = ?"), -1, ppStmt, 0u));
        uint st2 = GuestMemory.R32(ppStmt);
        Assert.Equal(0, Call(sq, "TransferBindings", st, st2));
        Assert.Equal(100, Call(sq, "SdkStep", st2));
        Assert.Equal(2L, (long)Invoke(sq, "SdkColumnInt64", st2, 0)!);
        Assert.Equal(0, Call(sq, "SdkFinalize", st2));
        Assert.Equal(0, Call(sq, "SdkFinalize", st));

        // complete, errmsg16, the VFS list
        Assert.Equal(1, Call(sq, "Complete", Str("SELECT 1;")));
        Assert.Equal(0, Call(sq, "Complete16", Str16("SELECT 1")));
        Assert.Equal(1, Call(sq, "SdkExec", db, Str("SELECT * FROM missing"), 0u, 0u, err));
        Assert.Contains("no such table", Read16(Convert.ToUInt32(Invoke(sq, "ErrMsg16", db))));
        uint vfs = Convert.ToUInt32(Invoke(sq, "VfsFind", 0u));
        Assert.Equal("psp2", GuestMemory.ReadCString(GuestMemory.R32(vfs + 0x10)));
        Assert.Equal(vfs, Convert.ToUInt32(Invoke(sq, "VfsFind", Str("psp2"))));

        // backup to a second connection
        Assert.Equal(0, Call(sq, "SdkOpen", Str(":memory:"), ppDb));
        uint db2 = GuestMemory.R32(ppDb);
        uint bk = Convert.ToUInt32(Invoke(sq, "BackupInit", db2, Str("main"), db, Str("main")));
        Assert.NotEqual(0u, bk);
        Assert.Equal(101, Call(sq, "BackupStep", bk, -1)); // SQLITE_DONE
        Assert.Equal(0, Call(sq, "BackupFinish", bk));
        Assert.Equal(0, Call(sq, "GetTable", db2, Str("SELECT count(*) FROM t"), res, rows, cols, err));
        Assert.Equal("2", GuestMemory.ReadCString(GuestMemory.R32(GuestMemory.R32(res) + 4)));
        Invoke(sq, "FreeTable", GuestMemory.R32(res));
        Assert.Equal(0, Call(sq, "SdkClose", db2));
        Assert.Equal(0, Call(sq, "SdkClose", db));
    }

    [Fact]
    public void DefaultEntriesAreMarkedAndRealOnesAreNot()
    {
        var bindings = VitaSharp.Core.Hle.HleBindings.Modules.SelectMany(m => m.Bindings()).ToList();
        bool IsDefault(string name) => bindings.Where(b => (b.Name ?? b.Method) == name).All(b => b.Default) && bindings.Any(b => (b.Name ?? b.Method) == name);
        Assert.True(IsDefault("sceBtStartAudio"));
        Assert.True(IsDefault("sceCompatInitEx"));
        Assert.True(IsDefault("_sceSblGcAuthMgrPkgVry"));
        Assert.False(IsDefault("sqlite3_open"));
        Assert.False(IsDefault("sceCompatLCDCSync"));
    }

    [Fact]
    public void CompatIsForTheEmulatorAndSystemPrograms()
    {
        var c = Module<Compat>();
        // the test process is not a system program: every entry refuses as the module does for other callers
        Assert.Equal(unchecked((int)0x80010058), Call(c, "sceCompatGetPspSystemSoftwareVersion"));
        Assert.Equal(unchecked((int)0x80010058), Call(c, "sceCompatAllocCdramWithHole", 0u));
        Assert.Equal(0, Call(c, "sceCompatAvailableColorSpaceSetting"));
    }
}
