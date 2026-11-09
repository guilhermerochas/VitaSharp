using System.Security.Cryptography;
using System.Text;
using SQLitePCL;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSqliteVsh: the SQLite 3.7.6.3 of the system software (vs0:sys/external/sqlite.suprx, the engine under dbutil, the
/// shell's databases, Music, Photos and Email). The exports have no public names; each was matched to its SQLite API
/// function from what it does in the module (the sqlite3 structure offsets of 3.7.6.3, the misuse checks, the helper
/// it calls). The HLE runs the statements on SQLite itself (e_sqlite3 through SQLitePCLRaw) over the host files the
/// guest paths resolve to. Guest pointers to databases, statements, blobs, values and function contexts are handles
/// (small guest blocks); text and blobs handed to the guest live in the module's own heap until the statement steps,
/// resets or finalizes, as SQLite's own pointers do. Callbacks (exec, collations, functions, hooks) run the guest's
/// function on the calling thread.
/// A system program keeps the firmware's library (firmware fallback only); VITASHARP_HLE_MODULES=SceSqliteVsh puts it
/// on this HLE instead.
/// </summary>
public sealed partial class SqliteVsh : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    const string Lib = "SceSqliteVsh";
    const int SQLITE_OK = 0, SQLITE_ERROR = 1, SQLITE_NOMEM = 7, SQLITE_MISUSE = 21, SQLITE_RANGE = 25;

    static bool _batteries;
    readonly object _lock = new();
    uint _heap;
    readonly Dictionary<uint, object> _handles = new();
    readonly Dictionary<object, uint> _addr = new(ReferenceEqualityComparer.Instance);

    Clib? _clib;
    Clib C => _clib ??= Emu.Hle.Modules.OfType<Clib>().First();

    sealed class Db
    {
        public sqlite3 H = null!;
        public uint ErrMsg;
        public readonly List<Stmt> Stmts = new();
        public readonly List<object> Keep = new(); // delegates SQLite holds on to
    }

    sealed class Stmt
    {
        public sqlite3_stmt H = null!;
        public Db Db = null!;
        public readonly Dictionary<(int Kind, int Col), uint> Bufs = new();
    }

    sealed class Blob { public sqlite3_blob H = null!; public Db Db = null!; }

    /// <summary>A function call's context (valid while the guest function runs).</summary>
    sealed class Ctx { public sqlite3_context H = null!; public uint UserData; public readonly List<uint> Bufs = new(); }

    /// <summary>A value: a function argument (live while the function runs) or a column value (a copy).</summary>
    sealed class Val
    {
        public int Type; // 1 int, 2 float, 3 text, 4 blob, 5 null
        public long I; public double D; public byte[]? Bytes;
        public uint Text, Blob;
    }

    static void Init()
    {
        if (_batteries) return;
        Batteries_V2.Init();
        _batteries = true;
    }

    // ---- handles and the module heap

    uint Heap()
    {
        if (_heap != 0) return _heap;
        uint block = Emu.Memory.Alloc(0x200000, "SceSqliteVsh heap");
        return _heap = C.sceClibMspaceCreate(block, 0x200000);
    }

    uint Malloc(uint size) => C.sceClibMspaceMalloc(Heap(), Math.Max(size, 1));
    void Free(uint p) { if (p != 0) C.sceClibMspaceFree(Heap(), p); }

    uint HandleOf(object o)
    {
        lock (_lock)
        {
            if (_addr.TryGetValue(o, out uint a)) return a;
            a = Malloc(16);
            GuestMemory.Fill(a, 0, 16);
            _handles[a] = o;
            _addr[o] = a;
            return a;
        }
    }

    T? Get<T>(uint a) where T : class { lock (_lock) return _handles.TryGetValue(a, out var o) ? o as T : null; }

    void Drop(object o)
    {
        lock (_lock)
        {
            if (!_addr.Remove(o, out uint a)) return;
            _handles.Remove(a);
            Free(a);
        }
    }

    uint GuestBytes(ReadOnlySpan<byte> b, bool nul)
    {
        uint p = Malloc((uint)b.Length + (nul ? 1u : 0));
        b.CopyTo(GuestMemory.Span(p, b.Length));
        if (nul) GuestMemory.Write<byte>(p + (uint)b.Length, 0);
        return p;
    }

    uint GuestString(string? s) => s == null ? 0 : GuestBytes(Encoding.UTF8.GetBytes(s), true);

    static string? Str(uint p) => p == 0 ? null : GuestMemory.ReadCString(p, 1 << 20);

    static string? StrN(uint p, int n) => p == 0 ? null : n < 0 ? Str(p) : Encoding.UTF8.GetString(GuestMemory.ReadSpan(p, n)).Split('\0')[0];

    static uint Call(uint fn, params ReadOnlySpan<uint> args) => GuestThread.Current!.CallGuest(fn, args);

    /// <summary>A guest path as the host file it is (":memory:" and names SQLite treats itself stay).</summary>
    string HostPath(string path)
    {
        if (path.Length == 0 || path.StartsWith(':')) return path;
        return Emu.Vfs.Resolve(path) is { } h ? h : path;
    }

    // ---- library

    /// <summary>sqlite3_initialize.</summary>
    [Hle("NID_80691B14", Nid = 0x80691B14, Library = Lib)] int SqlInitialize() { Init(); return raw.sqlite3_initialize(); }
    /// <summary>sqlite3_shutdown.</summary>
    [Hle("NID_7285EE28", Nid = 0x7285EE28, Library = Lib)] int Shutdown() => SQLITE_OK;
    /// <summary>sqlite3_malloc.</summary>
    [Hle("NID_490BA4E6", Nid = 0x490BA4E6, Library = Lib)] uint SqlMalloc(int n) => n is <= 0 or > 0x7FFFFEFF ? 0 : Malloc((uint)n);
    /// <summary>sqlite3_realloc.</summary>
    [Hle("NID_9F48395D", Nid = 0x9F48395D, Library = Lib)]
    uint SqlRealloc(uint p, int n) => p == 0 ? SqlMalloc(n) : n <= 0 ? FreeRet(p) : C.sceClibMspaceRealloc(Heap(), p, (uint)n);
    uint FreeRet(uint p) { Free(p); return 0; }
    /// <summary>sqlite3_free.</summary>
    [Hle("NID_F67857AC", Nid = 0xF67857AC, Library = Lib)] void SqlFree(uint p) => Free(p);
    /// <summary>sqlite3_sleep(ms): the milliseconds slept.</summary>
    [Hle("NID_7236786C", Nid = 0x7236786C, Library = Lib)]
    int Sleep(int ms) { Emu.Hle.Modules.OfType<ThreadMgr>().First().sceKernelDelayThread((uint)Math.Max(ms, 0) * 1000); return ms; }
    /// <summary>sqlite3_randomness(n, buf).</summary>
    [Hle("NID_418EA87C", Nid = 0x418EA87C, Library = Lib)]
    void Randomness(int n, uint buf) { if (n > 0 && buf != 0) RandomNumberGenerator.Fill(GuestMemory.Span(buf, n)); }
    /// <summary>sqlite3_strnicmp(a, b, n).</summary>
    [Hle("NID_AAB36B4D", Nid = 0xAAB36B4D, Library = Lib)]
    int Strnicmp(uint a, uint b, int n)
    {
        for (; n > 0; n--, a++, b++)
        {
            int x = char.ToLowerInvariant((char)GuestMemory.Read<byte>(a)), y = char.ToLowerInvariant((char)GuestMemory.Read<byte>(b));
            if (x != y || x == 0) return x - y;
        }
        return 0;
    }

    // ---- connections

    /// <summary>sqlite3_open(path, sqlite3 **db).</summary>
    [Hle("NID_67E5A83C", Nid = 0x67E5A83C, Library = Lib)] int Open(uint path, uint ppDb) => OpenV2(path, ppDb, 6, 0);

    /// <summary>sqlite3_open_v2(path, sqlite3 **db, flags, vfs): the handle is set even on failure (for errmsg).</summary>
    [Hle("NID_3E5BEB88", Nid = 0x3E5BEB88, Library = Lib)]
    int OpenV2(uint path, uint ppDb, int flags, uint vfs)
    {
        Init();
        if (ppDb == 0) return SQLITE_MISUSE;
        GuestMemory.W32(ppDb, 0);
        if ((1 << (flags & 7) & 0x46) == 0) return SQLITE_MISUSE;
        string file = HostPath(Str(path) ?? "");
        int rc = raw.sqlite3_open_v2(file, out sqlite3 h, flags, null);
        Log.Info($"SceSqliteVsh: open {Str(path)} (flags 0x{flags:X}) = {rc}");
        if (h == null) return SQLITE_NOMEM;
        var db = new Db { H = h };
        GuestMemory.W32(ppDb, HandleOf(db));
        return rc;
    }

    /// <summary>sqlite3_close(db): SQLITE_BUSY while statements are open.</summary>
    [Hle("NID_1BEBC7B3", Nid = 0x1BEBC7B3, Library = Lib)]
    int Close(uint p)
    {
        if (p == 0) return SQLITE_OK;
        if (Get<Db>(p) is not { } db) return SQLITE_MISUSE;
        int rc = raw.sqlite3_close(db.H);
        if (rc != SQLITE_OK) return rc;
        Free(db.ErrMsg);
        Drop(db);
        return SQLITE_OK;
    }

    int DbOp(uint p, Func<Db, int> f) => Get<Db>(p) is { } db ? f(db) : SQLITE_MISUSE;

    /// <summary>sqlite3_errcode / sqlite3_extended_errcode.</summary>
    [Hle("NID_9E03B649", Nid = 0x9E03B649, Library = Lib)] int ErrCode(uint p) => p == 0 ? SQLITE_NOMEM : DbOp(p, d => raw.sqlite3_errcode(d.H));
    [Hle("NID_88588393", Nid = 0x88588393, Library = Lib)] int ExtErrCode(uint p) => p == 0 ? SQLITE_NOMEM : DbOp(p, d => raw.sqlite3_extended_errcode(d.H));

    /// <summary>sqlite3_errmsg: the connection's last message (kept until the next call).</summary>
    [Hle("NID_E8098F5A", Nid = 0xE8098F5A, Library = Lib)]
    uint ErrMsg(uint p)
    {
        if (Get<Db>(p) is not { } db) { uint s = GuestString(p == 0 ? "out of memory" : "library routine called out of sequence"); return s; }
        Free(db.ErrMsg);
        return db.ErrMsg = GuestString(raw.sqlite3_errmsg(db.H).utf8_to_string());
    }

    [Hle("NID_11AD1CD1", Nid = 0x11AD1CD1, Library = Lib)] int Changes(uint p) => DbOp(p, d => raw.sqlite3_changes(d.H));
    [Hle("NID_2C34D734", Nid = 0x2C34D734, Library = Lib)] int TotalChanges(uint p) => DbOp(p, d => raw.sqlite3_total_changes(d.H));
    [Hle("NID_28B06314", Nid = 0x28B06314, Library = Lib)] long LastInsertRowid(uint p) => Get<Db>(p) is { } d ? raw.sqlite3_last_insert_rowid(d.H) : 0;
    [Hle("NID_61BFA3AB", Nid = 0x61BFA3AB, Library = Lib)] int GetAutocommit(uint p) => DbOp(p, d => raw.sqlite3_get_autocommit(d.H));
    [Hle("NID_E37D81B5", Nid = 0xE37D81B5, Library = Lib)] void Interrupt(uint p) { if (Get<Db>(p) is { } d) raw.sqlite3_interrupt(d.H); }
    [Hle("NID_3E7EF391", Nid = 0x3E7EF391, Library = Lib)] int BusyTimeout(uint p, int ms) => DbOp(p, d => raw.sqlite3_busy_timeout(d.H, ms));
    /// <summary>sqlite3_extended_result_codes(db, on).</summary>
    [Hle("NID_EDD56591", Nid = 0xEDD56591, Library = Lib)] int ExtendedResultCodes(uint p, int on) => DbOp(p, d => raw.sqlite3_extended_result_codes(d.H, on));
    /// <summary>sqlite3_limit(db, id 0..10, value): the previous value.</summary>
    [Hle("NID_D113958B", Nid = 0xD113958B, Library = Lib)] int Limit(uint p, int id, int v) => id is < 0 or > 10 ? -1 : DbOp(p, d => raw.sqlite3_limit(d.H, id, v));

    /// <summary>sqlite3_db_status(db, op, int *cur, int *hiwtr, reset).</summary>
    [Hle("NID_45E8C34D", Nid = 0x45E8C34D, Library = Lib)]
    int DbStatus(uint p, int op, uint cur, uint hi, int reset) => DbOp(p, d =>
    {
        int rc = raw.sqlite3_db_status(d.H, op, out int c, out int h, reset);
        if (cur != 0) GuestMemory.W32(cur, (uint)c);
        if (hi != 0) GuestMemory.W32(hi, (uint)h);
        return rc;
    });

    /// <summary>sqlite3_table_column_metadata(db, zDb, zTable, zColumn, char **type, char **coll, int *notNull, int *pk,
    /// int *autoinc): the strings live in the module heap with the connection.</summary>
    [Hle("NID_867A7B23", Nid = 0x867A7B23, Library = Lib)]
    int TableColumnMetadata(uint p, uint zDb, uint zTable, uint zCol, uint pType, uint pColl, uint pNotNull, uint pPk, uint pAuto) => DbOp(p, d =>
    {
        int rc = raw.sqlite3_table_column_metadata(d.H, Str(zDb), Str(zTable), Str(zCol), out string? type, out string? coll, out int nn, out int pk, out int ai);
        if (pType != 0) GuestMemory.W32(pType, rc == 0 ? KeepString(d, type) : 0);
        if (pColl != 0) GuestMemory.W32(pColl, rc == 0 ? KeepString(d, coll) : 0);
        if (pNotNull != 0) GuestMemory.W32(pNotNull, (uint)nn);
        if (pPk != 0) GuestMemory.W32(pPk, (uint)pk);
        if (pAuto != 0) GuestMemory.W32(pAuto, (uint)ai);
        return rc;
    });

    readonly Dictionary<(Db, string), uint> _kept = new();
    uint KeepString(Db d, string? s)
    {
        if (s == null) return 0;
        lock (_lock) { if (!_kept.TryGetValue((d, s), out uint a)) _kept[(d, s)] = a = GuestString(s); return a; }
    }

    // ---- exec

    /// <summary>sqlite3_exec(db, sql, int (*cb)(void *, int, char **, char **), arg, char **errmsg).</summary>
    [Hle("NID_14D27083", Nid = 0x14D27083, Library = Lib)]
    int Exec(uint p, uint sql, uint cb, uint arg, uint pErr)
    {
        if (pErr != 0) GuestMemory.W32(pErr, 0);
        if (Get<Db>(p) is not { } db) return SQLITE_MISUSE;
        strdelegate_exec? f = cb == 0 ? null : (object _, string[] values, string[] names) =>
        {
            int n = values.Length;
            uint argv = Malloc((uint)n * 8 + 8), cols = argv + (uint)n * 4 + 4;
            var tmp = new List<uint>();
            for (int i = 0; i < n; i++)
            {
                uint v = GuestString(values[i]), c = GuestString(names[i]);
                if (v != 0) tmp.Add(v);
                tmp.Add(c);
                GuestMemory.W32(argv + (uint)i * 4, v);
                GuestMemory.W32(cols + (uint)i * 4, c);
            }
            int r = (int)Call(cb, arg, (uint)n, argv, cols);
            foreach (uint t in tmp) Free(t);
            Free(argv);
            return r;
        };
        int rc = raw.sqlite3_exec(db.H, Str(sql) ?? "", f, null, out string? err);
        if (pErr != 0 && err != null) GuestMemory.W32(pErr, GuestString(err));
        return rc;
    }

    // ---- statements

    /// <summary>sqlite3_prepare / sqlite3_prepare_v2(db, sql, nBytes, sqlite3_stmt **stmt, const char **tail).</summary>
    [Hle("NID_26CA098C", Nid = 0x26CA098C, Library = Lib)]
    [Hle("NID_93F666E9", Nid = 0x93F666E9, Library = Lib)]
    int Prepare(uint p, uint sql, int n, uint ppStmt, uint pTail)
    {
        if (ppStmt != 0) GuestMemory.W32(ppStmt, 0);
        if (Get<Db>(p) is not { } db || ppStmt == 0) return SQLITE_MISUSE;
        byte[] text = n < 0 ? Encoding.UTF8.GetBytes(Str(sql) ?? "") : GuestMemory.ReadSpan(sql, n).ToArray();
        int nul = Array.IndexOf(text, (byte)0);
        if (nul >= 0) text = text[..nul];
        string s = Encoding.UTF8.GetString(text);
        int rc = raw.sqlite3_prepare_v2(db.H, s, out sqlite3_stmt h, out string? tail);
        if (pTail != 0)
        {
            int used = Encoding.UTF8.GetByteCount(s) - Encoding.UTF8.GetByteCount(tail ?? "");
            GuestMemory.W32(pTail, sql + (uint)used);
        }
        if (rc != SQLITE_OK) Log.Info($"SceSqliteVsh: prepare \"{(s.Length > 120 ? s[..120] + "..." : s)}\" = {rc} ({raw.sqlite3_errmsg(db.H).utf8_to_string()})");
        if (rc != SQLITE_OK || h == null || h.IsInvalid) return rc;
        var st = new Stmt { H = h, Db = db };
        lock (_lock) db.Stmts.Add(st);
        GuestMemory.W32(ppStmt, HandleOf(st));
        return rc;
    }

    void FreeBufs(Stmt st) { foreach (uint b in st.Bufs.Values) Free(b); st.Bufs.Clear(); }

    int StOp(uint p, Func<Stmt, int> f) => Get<Stmt>(p) is { } s ? f(s) : SQLITE_MISUSE;

    /// <summary>sqlite3_step.</summary>
    [Hle("NID_D9992208", Nid = 0xD9992208, Library = Lib)] int Step(uint p) => StOp(p, s => { FreeBufs(s); return raw.sqlite3_step(s.H); });
    /// <summary>sqlite3_reset.</summary>
    [Hle("NID_8B23AB10", Nid = 0x8B23AB10, Library = Lib)] int Reset(uint p) => p == 0 ? SQLITE_OK : StOp(p, s => { FreeBufs(s); return raw.sqlite3_reset(s.H); });
    /// <summary>sqlite3_finalize.</summary>
    [Hle("NID_FE4D6E4D", Nid = 0xFE4D6E4D, Library = Lib)]
    int StmtFinalize(uint p)
    {
        if (p == 0) return SQLITE_OK;
        if (Get<Stmt>(p) is not { } s) return SQLITE_MISUSE;
        FreeBufs(s);
        int rc = raw.sqlite3_finalize(s.H);
        lock (_lock) s.Db.Stmts.Remove(s);
        Drop(s);
        return rc;
    }
    /// <summary>sqlite3_clear_bindings.</summary>
    [Hle("NID_ED10F261", Nid = 0xED10F261, Library = Lib)] int ClearBindings(uint p) => StOp(p, s => raw.sqlite3_clear_bindings(s.H));
    /// <summary>sqlite3_db_handle(stmt).</summary>
    [Hle("NID_7E309542", Nid = 0x7E309542, Library = Lib)] uint DbHandle(uint p) => Get<Stmt>(p) is { } s ? HandleOf(s.Db) : 0;
    /// <summary>sqlite3_next_stmt(db, stmt): the next prepared statement of the connection (the first after NULL).</summary>
    [Hle("NID_4FFC89DE", Nid = 0x4FFC89DE, Library = Lib)]
    uint NextStmt(uint p, uint after)
    {
        if (Get<Db>(p) is not { } db) return 0;
        lock (_lock)
        {
            int i = after == 0 ? 0 : db.Stmts.FindIndex(s => _addr.TryGetValue(s, out uint a) && a == after) + 1;
            return i > 0 || after == 0 ? (i < db.Stmts.Count ? HandleOf(db.Stmts[i]) : 0) : 0;
        }
    }

    // binding: index 1..n

    [Hle("NID_DB8FA6E8", Nid = 0xDB8FA6E8, Library = Lib)] int BindInt64(uint p, int i, long v) => StOp(p, s => raw.sqlite3_bind_int64(s.H, i, v));
    [Hle("NID_756ACC35", Nid = 0x756ACC35, Library = Lib)] int BindInt(uint p, int i, int v) => StOp(p, s => raw.sqlite3_bind_int64(s.H, i, v));
    [Hle("NID_19788AEF", Nid = 0x19788AEF, Library = Lib)] int BindDouble(uint p, int i, HleContext c) => StOp(p, s => raw.sqlite3_bind_double(s.H, i, HleContext.ArgF64(c, 0))); // hard-float: d0
    [Hle("NID_741238E3", Nid = 0x741238E3, Library = Lib)] int BindNull(uint p, int i) => StOp(p, s => raw.sqlite3_bind_null(s.H, i));
    [Hle("NID_41E99C5C", Nid = 0x41E99C5C, Library = Lib)] int BindZeroblob(uint p, int i, int n) => StOp(p, s => raw.sqlite3_bind_zeroblob(s.H, i, n));

    /// <summary>sqlite3_bind_text(stmt, i, z, n, destructor): copied (SQLITE_TRANSIENT); a real destructor runs at once.</summary>
    [Hle("NID_1FF06753", Nid = 0x1FF06753, Library = Lib)]
    int BindText(uint p, int i, uint z, int n, uint del) => StOp(p, s =>
    {
        int rc = z == 0 ? raw.sqlite3_bind_null(s.H, i) : raw.sqlite3_bind_text(s.H, i, StrN(z, n) ?? "");
        RunDestructor(del, z);
        return rc;
    });

    /// <summary>sqlite3_bind_blob(stmt, i, data, n, destructor).</summary>
    [Hle("NID_84C7C7DE", Nid = 0x84C7C7DE, Library = Lib)]
    int BindBlob(uint p, int i, uint data, int n, uint del) => StOp(p, s =>
    {
        int rc = data == 0 ? raw.sqlite3_bind_null(s.H, i) : raw.sqlite3_bind_blob(s.H, i, GuestMemory.ReadSpan(data, Math.Max(n, 0)));
        RunDestructor(del, data);
        return rc;
    });

    /// <summary>A destructor argument: 0 static, -1 transient, otherwise the guest's free function.</summary>
    static void RunDestructor(uint del, uint p) { if (del != 0 && del != uint.MaxValue && p != 0) Call(del, p); }

    /// <summary>sqlite3_bind_value(stmt, i, value).</summary>
    [Hle("NID_00B3BFD9", Nid = 0x00B3BFD9, Library = Lib)]
    int BindValue(uint p, int i, uint v) => StOp(p, s => Get<Val>(v) is not { } x ? raw.sqlite3_bind_null(s.H, i) : x.Type switch
    {
        1 => raw.sqlite3_bind_int64(s.H, i, x.I),
        2 => raw.sqlite3_bind_double(s.H, i, x.D),
        3 => raw.sqlite3_bind_text(s.H, i, Encoding.UTF8.GetString(x.Bytes ?? [])),
        4 => raw.sqlite3_bind_blob(s.H, i, x.Bytes ?? []),
        _ => raw.sqlite3_bind_null(s.H, i),
    });

    [Hle("NID_78ED0DC8", Nid = 0x78ED0DC8, Library = Lib)] int BindParameterCount(uint p) => Get<Stmt>(p) is { } s ? raw.sqlite3_bind_parameter_count(s.H) : 0;
    [Hle("NID_A6F41220", Nid = 0xA6F41220, Library = Lib)] int BindParameterIndex(uint p, uint name) => Get<Stmt>(p) is { } s && name != 0 ? raw.sqlite3_bind_parameter_index(s.H, Str(name)!) : 0;

    /// <summary>sqlite3_bind_parameter_name(stmt, i): the name (kept with the statement), NULL for nameless.</summary>
    [Hle("NID_C82D4AE5", Nid = 0xC82D4AE5, Library = Lib)]
    uint BindParameterName(uint p, int i)
    {
        if (Get<Stmt>(p) is not { } s) return 0;
        string? n = raw.sqlite3_bind_parameter_name(s.H, i).utf8_to_string();
        return n == null ? 0 : KeepString(s.Db, n);
    }

    // columns: index 0..n-1

    [Hle("NID_3839D499", Nid = 0x3839D499, Library = Lib)] int ColumnCount(uint p) => Get<Stmt>(p) is { } s ? raw.sqlite3_column_count(s.H) : 0;
    [Hle("NID_F6A5A258", Nid = 0xF6A5A258, Library = Lib)] int DataCount(uint p) => Get<Stmt>(p) is { } s ? raw.sqlite3_data_count(s.H) : 0;
    [Hle("NID_9EF87FDA", Nid = 0x9EF87FDA, Library = Lib)] int ColumnType(uint p, int i) => Get<Stmt>(p) is { } s ? raw.sqlite3_column_type(s.H, i) : 5;
    /// <summary>sqlite3_column_int and sqlite3_column_int64 (the 64-bit value in r0:r1 serves both).</summary>
    [Hle("NID_4C46682A", Nid = 0x4C46682A, Library = Lib)]
    [Hle("NID_EB65E393", Nid = 0xEB65E393, Library = Lib)]
    long ColumnInt64(uint p, int i) => Get<Stmt>(p) is { } s ? raw.sqlite3_column_int64(s.H, i) : 0;
    [Hle("NID_B5FB7288", Nid = 0xB5FB7288, Library = Lib)] double ColumnDouble(uint p, int i) => Get<Stmt>(p) is { } s ? raw.sqlite3_column_double(s.H, i) : 0;

    /// <summary>sqlite3_column_bytes(stmt, i).</summary>
    [Hle("NID_97B7F328", Nid = 0x97B7F328, Library = Lib)] int ColumnBytes(uint p, int i) => Get<Stmt>(p) is { } s ? raw.sqlite3_column_bytes(s.H, i) : 0;

    uint StmtBuf(Stmt s, int kind, int col, Func<uint> make)
    {
        if (s.Bufs.TryGetValue((kind, col), out uint b)) return b;
        return s.Bufs[(kind, col)] = make();
    }

    /// <summary>sqlite3_column_text: NUL-terminated UTF-8, valid until the statement steps, resets or finalizes.</summary>
    [Hle("NID_7272D729", Nid = 0x7272D729, Library = Lib)]
    uint ColumnText(uint p, int i)
    {
        if (Get<Stmt>(p) is not { } s || raw.sqlite3_column_type(s.H, i) == 5) return 0;
        return StmtBuf(s, 0, i, () => GuestBytes(raw.sqlite3_column_blob(s.H, i), true));
    }

    /// <summary>sqlite3_column_blob.</summary>
    [Hle("NID_344D3122", Nid = 0x344D3122, Library = Lib)]
    uint ColumnBlob(uint p, int i)
    {
        if (Get<Stmt>(p) is not { } s) return 0;
        byte[] b = raw.sqlite3_column_blob(s.H, i).ToArray();
        return b.Length == 0 ? 0 : StmtBuf(s, 1, i, () => GuestBytes(b, false));
    }

    /// <summary>sqlite3_column_value: a copy of the column (a protected value).</summary>
    [Hle("NID_6AF794DB", Nid = 0x6AF794DB, Library = Lib)]
    uint ColumnValue(uint p, int i)
    {
        if (Get<Stmt>(p) is not { } s) return 0;
        var v = new Val { Type = raw.sqlite3_column_type(s.H, i) };
        switch (v.Type)
        {
            case 1: v.I = raw.sqlite3_column_int64(s.H, i); break;
            case 2: v.D = raw.sqlite3_column_double(s.H, i); break;
            case 3: case 4: v.Bytes = raw.sqlite3_column_blob(s.H, i).ToArray(); break;
        }
        return StmtBuf(s, 7, i, () => HandleOf(v));
    }

    uint ColumnNameOf(uint p, int i, int kind, Func<sqlite3_stmt, int, utf8z> get)
    {
        if (Get<Stmt>(p) is not { } s) return 0;
        string? n = get(s.H, i).utf8_to_string();
        return n == null ? 0 : StmtBuf(s, kind, i, () => GuestString(n));
    }

    [Hle("NID_4997F344", Nid = 0x4997F344, Library = Lib)] uint ColumnName(uint p, int i) => ColumnNameOf(p, i, 2, raw.sqlite3_column_name);
    [Hle("NID_A0027D75", Nid = 0xA0027D75, Library = Lib)] uint ColumnDecltype(uint p, int i) => ColumnNameOf(p, i, 3, raw.sqlite3_column_decltype);
    [Hle("NID_26C40C45", Nid = 0x26C40C45, Library = Lib)] uint ColumnDatabaseName(uint p, int i) => ColumnNameOf(p, i, 4, raw.sqlite3_column_database_name);
    [Hle("NID_D666D46D", Nid = 0xD666D46D, Library = Lib)] uint ColumnTableName(uint p, int i) => ColumnNameOf(p, i, 5, raw.sqlite3_column_table_name);
    [Hle("NID_DF4DFF2D", Nid = 0xDF4DFF2D, Library = Lib)] uint ColumnOriginName(uint p, int i) => ColumnNameOf(p, i, 6, raw.sqlite3_column_origin_name);

    // ---- values (function arguments and column values)

    Val? V(uint p) => Get<Val>(p);

    [Hle("NID_F4528464", Nid = 0xF4528464, Library = Lib)] int ValueType(uint p) => V(p)?.Type ?? 5;
    /// <summary>sqlite3_value_int / sqlite3_value_int64.</summary>
    [Hle("NID_85DC034D", Nid = 0x85DC034D, Library = Lib)]
    [Hle("NID_CD1805C0", Nid = 0xCD1805C0, Library = Lib)]
    long ValueInt64(uint p) => V(p) is { } v ? v.Type switch { 1 => v.I, 2 => (long)v.D, 3 => long.TryParse(Encoding.UTF8.GetString(v.Bytes!), out long x) ? x : 0, _ => 0 } : 0;
    [Hle("NID_8D23E9F3", Nid = 0x8D23E9F3, Library = Lib)]
    double ValueDouble(uint p) => V(p) is { } v ? v.Type switch { 1 => v.I, 2 => v.D, 3 => double.TryParse(Encoding.UTF8.GetString(v.Bytes!), System.Globalization.CultureInfo.InvariantCulture, out double x) ? x : 0, _ => 0 } : 0;
    [Hle("NID_21215328", Nid = 0x21215328, Library = Lib)] int ValueBytes(uint p) => V(p) is { } v ? v.Type is 3 or 4 ? v.Bytes!.Length : v.Type == 5 ? 0 : ValueTextString(v).Length : 0;

    static string ValueTextString(Val v) => v.Type switch
    {
        1 => v.I.ToString(System.Globalization.CultureInfo.InvariantCulture),
        2 => v.D.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        3 or 4 => Encoding.UTF8.GetString(v.Bytes!),
        _ => "",
    };

    /// <summary>sqlite3_value_text: UTF-8 kept with the value.</summary>
    [Hle("NID_BA7F8AAA", Nid = 0xBA7F8AAA, Library = Lib)]
    uint ValueText(uint p)
    {
        if (V(p) is not { } v || v.Type == 5) return 0;
        if (v.Text == 0) v.Text = v.Type is 3 or 4 ? GuestBytes(v.Bytes!, true) : GuestString(ValueTextString(v));
        return v.Text;
    }

    /// <summary>sqlite3_value_blob.</summary>
    [Hle("NID_CB0BE217", Nid = 0xCB0BE217, Library = Lib)]
    uint ValueBlob(uint p)
    {
        if (V(p) is not { } v || v.Type == 5) return 0;
        if (v.Type is not (3 or 4)) return ValueText(p);
        if (v.Blob == 0 && v.Bytes!.Length > 0) v.Blob = GuestBytes(v.Bytes, false);
        return v.Blob;
    }

    static Val FromNative(sqlite3_value h)
    {
        var v = new Val { Type = raw.sqlite3_value_type(h) };
        switch (v.Type)
        {
            case 1: v.I = raw.sqlite3_value_int64(h); break;
            case 2: v.D = raw.sqlite3_value_double(h); break;
            case 3: case 4: v.Bytes = raw.sqlite3_value_blob(h).ToArray(); break;
        }
        return v;
    }

    void FreeVal(Val v) { Free(v.Text); Free(v.Blob); Drop(v); }

    // ---- user functions and collations

    /// <summary>sqlite3_create_function(db, name, nArg, enc, pApp, xFunc, xStep, xFinal): xFunc (or xStep/xFinal)
    /// run on the guest with a context handle and an argv of value handles.</summary>
    [Hle("NID_EA35A125", Nid = 0xEA35A125, Library = Lib)]
    int CreateFunction(uint p, uint name, int nArg, int enc, uint app, uint xFunc, uint xStep, uint xFinal) => DbOp(p, d =>
    {
        string n = Str(name) ?? "";
        if (xFunc != 0)
        {
            delegate_function_scalar f = (ctx, _, args) => RunFunction(xFunc, ctx, app, args);
            d.Keep.Add(f);
            return raw.sqlite3_create_function(d.H, n, nArg, enc & 0xF, null, f);
        }
        if (xStep != 0 && xFinal != 0)
        {
            delegate_function_aggregate_step s = (ctx, _, args) => RunFunction(xStep, ctx, app, args);
            delegate_function_aggregate_final fin = (ctx, _) => RunFunction(xFinal, ctx, app, null);
            d.Keep.Add(s); d.Keep.Add(fin);
            return raw.sqlite3_create_function(d.H, n, nArg, enc & 0xF, null, s, fin);
        }
        return raw.sqlite3_create_function(d.H, n, nArg, enc & 0xF, null, (delegate_function_scalar?)null);
    });

    void RunFunction(uint fn, sqlite3_context ctx, uint app, sqlite3_value[]? args)
    {
        var c = new Ctx { H = ctx, UserData = app };
        uint ch = HandleOf(c);
        var vals = (args ?? []).Select(FromNative).ToList();
        uint argv = vals.Count == 0 ? 0 : Malloc((uint)vals.Count * 4);
        for (int i = 0; i < vals.Count; i++) GuestMemory.W32(argv + (uint)i * 4, HandleOf(vals[i]));
        try { Call(fn, ch, (uint)vals.Count, argv); }
        finally
        {
            foreach (var v in vals) FreeVal(v);
            Free(argv);
            foreach (uint b in c.Bufs) Free(b);
            Drop(c);
        }
    }

    /// <summary>sqlite3_create_collation(db, name, enc, pArg, int (*cmp)(void *, int, const void *, int, const void *)).</summary>
    [Hle("NID_2AA39086", Nid = 0x2AA39086, Library = Lib)]
    int CreateCollation(uint p, uint name, int enc, uint arg, uint cmp) => DbOp(p, d =>
    {
        if (cmp == 0) return raw.sqlite3_create_collation(d.H, Str(name) ?? "", null, null);
        strdelegate_collation f = (_, a, b) =>
        {
            byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
            uint px = GuestBytes(x, true), py = GuestBytes(y, true);
            try { return (int)Call(cmp, arg, (uint)x.Length, px, (uint)y.Length, py); }
            finally { Free(px); Free(py); }
        };
        d.Keep.Add(f);
        return raw.sqlite3_create_collation(d.H, Str(name) ?? "", null, f);
    });

    Ctx? Cx(uint p) => Get<Ctx>(p);

    /// <summary>sqlite3_user_data(ctx).</summary>
    [Hle("NID_5C4B62D9", Nid = 0x5C4B62D9, Library = Lib)] uint UserData(uint p) => Cx(p)?.UserData ?? 0;
    [Hle("NID_17321FC0", Nid = 0x17321FC0, Library = Lib)] void ResultNull(uint p) { if (Cx(p) is { } c) raw.sqlite3_result_null(c.H); }
    [Hle("NID_7125360C", Nid = 0x7125360C, Library = Lib)] void ResultInt(uint p, int v) { if (Cx(p) is { } c) raw.sqlite3_result_int64(c.H, v); }
    [Hle("NID_BA2473EA", Nid = 0xBA2473EA, Library = Lib)] void ResultInt64(uint p, long v) { if (Cx(p) is { } c) raw.sqlite3_result_int64(c.H, v); }
    [Hle("NID_BBE27F1E", Nid = 0xBBE27F1E, Library = Lib)] void ResultZeroblob(uint p, int n) { if (Cx(p) is { } c) raw.sqlite3_result_zeroblob(c.H, Math.Max(n, 0)); }
    [Hle("NID_DD07AC9D", Nid = 0xDD07AC9D, Library = Lib)] void ResultDouble(uint p, HleContext hc) { if (Cx(p) is { } c) raw.sqlite3_result_double(c.H, HleContext.ArgF64(hc, 0)); }
    [Hle("NID_4574AB4A", Nid = 0x4574AB4A, Library = Lib)] void ResultErrorNomem(uint p) { if (Cx(p) is { } c) raw.sqlite3_result_error_nomem(c.H); }
    [Hle("NID_DC36F2C5", Nid = 0xDC36F2C5, Library = Lib)] void ResultErrorTooBig(uint p) { if (Cx(p) is { } c) raw.sqlite3_result_error_toobig(c.H); }
    [Hle("NID_DB6B866A", Nid = 0xDB6B866A, Library = Lib)] void ResultErrorCode(uint p, int code) { if (Cx(p) is { } c) raw.sqlite3_result_error_code(c.H, code); }
    [Hle("NID_D87A0866", Nid = 0xD87A0866, Library = Lib)] void ResultError(uint p, uint msg, int n) { if (Cx(p) is { } c) raw.sqlite3_result_error(c.H, StrN(msg, n) ?? ""); }

    /// <summary>sqlite3_result_text(ctx, z, n, destructor).</summary>
    [Hle("NID_0EF04E45", Nid = 0x0EF04E45, Library = Lib)]
    void ResultText(uint p, uint z, int n, uint del)
    {
        if (Cx(p) is { } c) { if (z == 0) raw.sqlite3_result_null(c.H); else raw.sqlite3_result_text(c.H, StrN(z, n) ?? ""); }
        RunDestructor(del, z);
    }

    /// <summary>sqlite3_result_blob(ctx, data, n, destructor).</summary>
    [Hle("NID_58F94B77", Nid = 0x58F94B77, Library = Lib)]
    void ResultBlob(uint p, uint data, int n, uint del)
    {
        if (Cx(p) is { } c) { if (data == 0) raw.sqlite3_result_null(c.H); else raw.sqlite3_result_blob(c.H, GuestMemory.ReadSpan(data, Math.Max(n, 0))); }
        RunDestructor(del, data);
    }

    /// <summary>sqlite3_result_value(ctx, value).</summary>
    [Hle("NID_5A2D6AF5", Nid = 0x5A2D6AF5, Library = Lib)]
    void ResultValue(uint p, uint v)
    {
        if (Cx(p) is not { } c) return;
        switch (V(v))
        {
            case { Type: 1 } x: raw.sqlite3_result_int64(c.H, x.I); break;
            case { Type: 2 } x: raw.sqlite3_result_double(c.H, x.D); break;
            case { Type: 3 } x: raw.sqlite3_result_text(c.H, Encoding.UTF8.GetString(x.Bytes!)); break;
            case { Type: 4 } x: raw.sqlite3_result_blob(c.H, x.Bytes!); break;
            default: raw.sqlite3_result_null(c.H); break;
        }
    }

    // ---- hooks

    /// <summary>sqlite3_progress_handler(db, n, int (*cb)(void *), arg).</summary>
    [Hle("NID_0A016EB3", Nid = 0x0A016EB3, Library = Lib)]
    void ProgressHandler(uint p, int n, uint cb, uint arg)
    {
        if (Get<Db>(p) is not { } d) return;
        if (cb == 0 || n <= 0) { raw.sqlite3_progress_handler(d.H, 0, null, null); return; }
        delegate_progress f = _ => (int)Call(cb, arg);
        d.Keep.Add(f);
        raw.sqlite3_progress_handler(d.H, n, f, null);
    }

    readonly Dictionary<(Db, int), uint> _hookArgs = new();

    uint SwapHookArg(Db d, int kind, uint arg) { lock (_lock) { uint old = _hookArgs.GetValueOrDefault((d, kind)); _hookArgs[(d, kind)] = arg; return old; } }

    /// <summary>sqlite3_commit_hook(db, int (*cb)(void *), arg): the previous argument.</summary>
    [Hle("NID_97971596", Nid = 0x97971596, Library = Lib)]
    uint CommitHook(uint p, uint cb, uint arg)
    {
        if (Get<Db>(p) is not { } d) return 0;
        delegate_commit? f = cb == 0 ? null : _ => (int)Call(cb, arg);
        if (f != null) d.Keep.Add(f);
        raw.sqlite3_commit_hook(d.H, f, null);
        return SwapHookArg(d, 0, cb == 0 ? 0 : arg);
    }

    /// <summary>sqlite3_rollback_hook(db, void (*cb)(void *), arg).</summary>
    [Hle("NID_DED66A48", Nid = 0xDED66A48, Library = Lib)]
    uint RollbackHook(uint p, uint cb, uint arg)
    {
        if (Get<Db>(p) is not { } d) return 0;
        delegate_rollback? f = cb == 0 ? null : _ => Call(cb, arg);
        if (f != null) d.Keep.Add(f);
        raw.sqlite3_rollback_hook(d.H, f, null);
        return SwapHookArg(d, 1, cb == 0 ? 0 : arg);
    }

    /// <summary>sqlite3_update_hook(db, void (*cb)(void *, int op, const char *db, const char *table, sqlite3_int64), arg).</summary>
    [Hle("NID_9957716B", Nid = 0x9957716B, Library = Lib)]
    uint UpdateHook(uint p, uint cb, uint arg)
    {
        if (Get<Db>(p) is not { } d) return 0;
        strdelegate_update? f = cb == 0 ? null : (_, op, dbName, table, rowid) =>
        {
            uint a = GuestString(dbName), b = GuestString(table);
            try { GuestThread.Current!.CallGuest(cb, arg, (uint)op, a, b, (uint)rowid, (uint)(rowid >> 32)); }
            finally { Free(a); Free(b); }
        };
        if (f != null) d.Keep.Add(f);
        raw.sqlite3_update_hook(d.H, f, null);
        return SwapHookArg(d, 2, cb == 0 ? 0 : arg);
    }

    // ---- incremental blob I/O

    /// <summary>sqlite3_blob_open(db, zDb, zTable, zColumn, sqlite3_int64 row, flags, sqlite3_blob **blob).</summary>
    [Hle("NID_07AD8710", Nid = 0x07AD8710, Library = Lib)]
    int BlobOpen(uint p, uint zDb, uint zTable, uint zCol, long row, int flags, uint ppBlob)
    {
        if (ppBlob != 0) GuestMemory.W32(ppBlob, 0);
        if (Get<Db>(p) is not { } d) return SQLITE_MISUSE;
        int rc = raw.sqlite3_blob_open(d.H, Str(zDb) ?? "main", Str(zTable) ?? "", Str(zCol) ?? "", row, flags, out sqlite3_blob h);
        if (rc == SQLITE_OK && ppBlob != 0) GuestMemory.W32(ppBlob, HandleOf(new Blob { H = h, Db = d }));
        return rc;
    }

    [Hle("NID_93D050D4", Nid = 0x93D050D4, Library = Lib)] int BlobBytes(uint p) => Get<Blob>(p) is { } b ? raw.sqlite3_blob_bytes(b.H) : 0;

    /// <summary>sqlite3_blob_read(blob, buf, n, offset).</summary>
    [Hle("NID_CB7A933A", Nid = 0xCB7A933A, Library = Lib)]
    int BlobRead(uint p, uint buf, int n, int off) =>
        Get<Blob>(p) is not { } b ? SQLITE_MISUSE : n < 0 || off < 0 ? SQLITE_ERROR : raw.sqlite3_blob_read(b.H, GuestMemory.Span(buf, n), off);

    /// <summary>sqlite3_blob_write(blob, data, n, offset).</summary>
    [Hle("NID_BE351934", Nid = 0xBE351934, Library = Lib)]
    int BlobWrite(uint p, uint data, int n, int off) =>
        Get<Blob>(p) is not { } b ? SQLITE_MISUSE : n < 0 || off < 0 ? SQLITE_ERROR : raw.sqlite3_blob_write(b.H, GuestMemory.ReadSpan(data, n), off);

    [Hle("NID_DE793774", Nid = 0xDE793774, Library = Lib)]
    int BlobClose(uint p)
    {
        if (p == 0 || Get<Blob>(p) is not { } b) return SQLITE_OK;
        int rc = raw.sqlite3_blob_close(b.H);
        Drop(b);
        return rc;
    }

    // ---- printf with SQLite's %q (quote '), %Q (quoted or NULL), %w (quote "), %z (free after use)

    string Format(uint fmt, VarArgs va)
    {
        string f = Str(fmt) ?? "";
        var sb = new StringBuilder();
        for (int i = 0; i < f.Length; i++)
        {
            if (f[i] != '%') { sb.Append(f[i]); continue; }
            int j = i + 1;
            while (j < f.Length && "-+ #0123456789.lhjzt*".IndexOf(f[j]) >= 0 && !(f[j] == 'z' && (j + 1 >= f.Length || "diouxX".IndexOf(f[j + 1]) < 0))) j++;
            if (j >= f.Length) { sb.Append(f, i, f.Length - i); break; }
            char conv = f[j];
            if (conv is 'q' or 'Q' or 'w' or 'z')
            {
                uint sp = va.NextU32();
                string? s = Str(sp);
                if (conv == 'z') { sb.Append(s); Free(sp); }
                else if (conv == 'Q' && s == null) sb.Append("NULL");
                else
                {
                    char quote = conv == 'w' ? '"' : '\'';
                    string body = (s ?? "(NULL)").Replace(quote.ToString(), new string(quote, 2));
                    sb.Append(conv == 'Q' ? $"'{body}'" : body);
                }
            }
            else sb.Append(CFormat.Format(f.Substring(i, j - i + 1), va));
            i = j;
        }
        return sb.ToString();
    }

    /// <summary>sqlite3_mprintf(fmt, ...): a string from the module heap (sqlite3_free).</summary>
    [Hle("NID_B1D69618", Nid = 0xB1D69618, Library = Lib)] uint Mprintf(uint fmt, VarArgs va) => GuestString(Format(fmt, va));

    /// <summary>sqlite3_vmprintf(fmt, va_list).</summary>
    [Hle("NID_B33ACD23", Nid = 0xB33ACD23, Library = Lib)] uint Vmprintf(uint fmt, uint va) => GuestString(Format(fmt, VaListArgs(va)));

    /// <summary>sqlite3_snprintf(n, buf, fmt, ...): at most n-1 characters and a NUL; returns buf.</summary>
    [Hle("NID_18D86F12", Nid = 0x18D86F12, Library = Lib)]
    uint Snprintf(int n, uint buf, uint fmt, VarArgs va)
    {
        if (n <= 0) return buf;
        byte[] b = Encoding.UTF8.GetBytes(Format(fmt, va));
        int k = Math.Min(b.Length, n - 1);
        b.AsSpan(0, k).CopyTo(GuestMemory.Span(buf, k));
        GuestMemory.Write<byte>(buf + (uint)k, 0);
        return buf;
    }

    sealed class GuestVaList(uint p) : IVaSource
    {
        uint _p = p;
        public uint Next() { uint v = GuestMemory.R32(_p); _p += 4; return v; }
        public uint? Address => _p;
    }

    static VarArgs VaListArgs(uint va) => new(new GuestVaList(va), 0);
}
