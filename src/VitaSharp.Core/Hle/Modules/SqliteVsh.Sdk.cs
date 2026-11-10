using System.Text;
using SQLitePCL;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSqlite: the SQLite of the SDK (vs0:sys/external/libSceSqlite.suprx, SCE_SYSMODULE_SQLITE), with the public
/// sqlite3_* names. It is the same engine as the system's SceSqliteVsh, so the same HLE serves both: the entries of
/// the other file run here under the SDK names, and this file adds the rest of the API the SDK library exports (the
/// UTF-16 forms, backups, tables, mutexes, the VFS list, status and the deprecated calls). Firmware fallback only,
/// like SceSqliteVsh: a game that loads the module keeps the firmware's.
/// </summary>
public sealed partial class SqliteVsh
{
    const string Sdk = "SceSqlite";
    const int SQLITE_NOTFOUND = 12, SQLITE_BUSY = 5;

    // ---- the entries shared with SceSqliteVsh

    [Hle("sqlite3_initialize", Library = Sdk)] int SdkInitialize() => SqlInitialize();
    [Hle("sqlite3_shutdown", Library = Sdk)] int SdkShutdown() => Shutdown();
    [Hle("sqlite3_malloc", Library = Sdk)] uint SdkMalloc(int n) => SqlMalloc(n);
    [Hle("sqlite3_realloc", Library = Sdk)] uint SdkRealloc(uint p, int n) => SqlRealloc(p, n);
    [Hle("sqlite3_free", Library = Sdk)] void SdkFree(uint p) => SqlFree(p);
    [Hle("sqlite3_sleep", Library = Sdk)] int SdkSleep(int ms) => Sleep(ms);
    [Hle("sqlite3_randomness", Library = Sdk)] void SdkRandomness(int n, uint buf) => Randomness(n, buf);
    [Hle("sqlite3_strnicmp", Library = Sdk)] int SdkStrnicmp(uint a, uint b, int n) => Strnicmp(a, b, n);
    [Hle("sqlite3_open", Library = Sdk)] int SdkOpen(uint path, uint ppDb) => Open(path, ppDb);
    [Hle("sqlite3_open_v2", Library = Sdk)] int SdkOpenV2(uint path, uint ppDb, int flags, uint vfs) => OpenV2(path, ppDb, flags, vfs);
    [Hle("sqlite3_close", Library = Sdk)] int SdkClose(uint p) => Close(p);
    [Hle("sqlite3_errcode", Library = Sdk)] int SdkErrCode(uint p) => ErrCode(p);
    [Hle("sqlite3_extended_errcode", Library = Sdk)] int SdkExtErrCode(uint p) => ExtErrCode(p);
    [Hle("sqlite3_errmsg", Library = Sdk)] uint SdkErrMsg(uint p) => ErrMsg(p);
    [Hle("sqlite3_changes", Library = Sdk)] int SdkChanges(uint p) => Changes(p);
    [Hle("sqlite3_total_changes", Library = Sdk)] int SdkTotalChanges(uint p) => TotalChanges(p);
    [Hle("sqlite3_last_insert_rowid", Library = Sdk)] long SdkLastInsertRowid(uint p) => LastInsertRowid(p);
    [Hle("sqlite3_get_autocommit", Library = Sdk)] int SdkGetAutocommit(uint p) => GetAutocommit(p);
    [Hle("sqlite3_interrupt", Library = Sdk)] void SdkInterrupt(uint p) => Interrupt(p);
    [Hle("sqlite3_busy_timeout", Library = Sdk)] int SdkBusyTimeout(uint p, int ms) => BusyTimeout(p, ms);
    [Hle("sqlite3_busy_handler", Library = Sdk)] int SdkBusyHandler(uint p, uint cb, uint arg) => BusyHandler(p, cb, arg);
    [Hle("sqlite3_config", Library = Sdk)] int SdkConfig(int op) => Config(op);
    [Hle("sqlite3_extended_result_codes", Library = Sdk)] int SdkExtendedResultCodes(uint p, int on) => ExtendedResultCodes(p, on);
    [Hle("sqlite3_limit", Library = Sdk)] int SdkLimit(uint p, int id, int v) => Limit(p, id, v);
    [Hle("sqlite3_db_status", Library = Sdk)] int SdkDbStatus(uint p, int op, uint cur, uint hi, int reset) => DbStatus(p, op, cur, hi, reset);
    [Hle("sqlite3_exec", Library = Sdk)] int SdkExec(uint p, uint sql, uint cb, uint arg, uint pErr) => Exec(p, sql, cb, arg, pErr);
    [Hle("sqlite3_prepare", Library = Sdk)]
    [Hle("sqlite3_prepare_v2", Library = Sdk)]
    int SdkPrepare(uint p, uint sql, int n, uint ppStmt, uint pTail) => Prepare(p, sql, n, ppStmt, pTail);
    [Hle("sqlite3_step", Library = Sdk)] int SdkStep(uint p) => Step(p);
    [Hle("sqlite3_reset", Library = Sdk)] int SdkReset(uint p) => Reset(p);
    [Hle("sqlite3_finalize", Library = Sdk)] int SdkFinalize(uint p) { lock (_lock) _binds.Remove(p); return StmtFinalize(p); }
    [Hle("sqlite3_clear_bindings", Library = Sdk)] int SdkClearBindings(uint p) { Binds(p)?.Clear(); return ClearBindings(p); }
    [Hle("sqlite3_db_handle", Library = Sdk)] uint SdkDbHandle(uint p) => DbHandle(p);
    [Hle("sqlite3_next_stmt", Library = Sdk)] uint SdkNextStmt(uint p, uint after) => NextStmt(p, after);
    [Hle("sqlite3_bind_int64", Library = Sdk)] int SdkBindInt64(uint p, int i, long v) => Bound(p, i, new Val { Type = 1, I = v }, BindInt64(p, i, v));
    [Hle("sqlite3_bind_int", Library = Sdk)] int SdkBindInt(uint p, int i, int v) => Bound(p, i, new Val { Type = 1, I = v }, BindInt(p, i, v));
    [Hle("sqlite3_bind_double", Library = Sdk)]
    int SdkBindDouble(uint p, int i, HleContext c) => Bound(p, i, new Val { Type = 2, D = HleContext.ArgF64(c, 0) }, BindDouble(p, i, c));
    [Hle("sqlite3_bind_null", Library = Sdk)] int SdkBindNull(uint p, int i) => Bound(p, i, new Val { Type = 5 }, BindNull(p, i));
    [Hle("sqlite3_bind_zeroblob", Library = Sdk)]
    int SdkBindZeroblob(uint p, int i, int n) => Bound(p, i, new Val { Type = 4, Bytes = new byte[Math.Max(n, 0)] }, BindZeroblob(p, i, n));
    [Hle("sqlite3_bind_text", Library = Sdk)]
    int SdkBindText(uint p, int i, uint z, int n, uint del)
    {
        var v = z == 0 ? new Val { Type = 5 } : new Val { Type = 3, Bytes = Encoding.UTF8.GetBytes(StrN(z, n) ?? "") };
        return Bound(p, i, v, BindText(p, i, z, n, del));
    }
    [Hle("sqlite3_bind_blob", Library = Sdk)]
    int SdkBindBlob(uint p, int i, uint data, int n, uint del)
    {
        var v = data == 0 ? new Val { Type = 5 } : new Val { Type = 4, Bytes = GuestMemory.ReadSpan(data, Math.Max(n, 0)).ToArray() };
        return Bound(p, i, v, BindBlob(p, i, data, n, del));
    }
    [Hle("sqlite3_bind_value", Library = Sdk)]
    int SdkBindValue(uint p, int i, uint v)
    {
        var x = V(v);
        return Bound(p, i, x == null ? new Val { Type = 5 } : new Val { Type = x.Type, I = x.I, D = x.D, Bytes = x.Bytes }, BindValue(p, i, v));
    }
    [Hle("sqlite3_bind_parameter_count", Library = Sdk)] int SdkBindParameterCount(uint p) => BindParameterCount(p);
    [Hle("sqlite3_bind_parameter_index", Library = Sdk)] int SdkBindParameterIndex(uint p, uint name) => BindParameterIndex(p, name);
    [Hle("sqlite3_bind_parameter_name", Library = Sdk)] uint SdkBindParameterName(uint p, int i) => BindParameterName(p, i);
    [Hle("sqlite3_column_count", Library = Sdk)] int SdkColumnCount(uint p) => ColumnCount(p);
    [Hle("sqlite3_data_count", Library = Sdk)] int SdkDataCount(uint p) => DataCount(p);
    [Hle("sqlite3_column_type", Library = Sdk)] int SdkColumnType(uint p, int i) => ColumnType(p, i);
    [Hle("sqlite3_column_int", Library = Sdk)]
    [Hle("sqlite3_column_int64", Library = Sdk)]
    long SdkColumnInt64(uint p, int i) => ColumnInt64(p, i);
    [Hle("sqlite3_column_double", Library = Sdk)] double SdkColumnDouble(uint p, int i) => ColumnDouble(p, i);
    [Hle("sqlite3_column_bytes", Library = Sdk)] int SdkColumnBytes(uint p, int i) => ColumnBytes(p, i);
    [Hle("sqlite3_column_text", Library = Sdk)] uint SdkColumnText(uint p, int i) => ColumnText(p, i);
    [Hle("sqlite3_column_blob", Library = Sdk)] uint SdkColumnBlob(uint p, int i) => ColumnBlob(p, i);
    [Hle("sqlite3_column_value", Library = Sdk)] uint SdkColumnValue(uint p, int i) => ColumnValue(p, i);
    [Hle("sqlite3_column_name", Library = Sdk)] uint SdkColumnName(uint p, int i) => ColumnName(p, i);
    [Hle("sqlite3_column_decltype", Library = Sdk)] uint SdkColumnDecltype(uint p, int i) => ColumnDecltype(p, i);
    [Hle("sqlite3_value_type", Library = Sdk)] int SdkValueType(uint p) => ValueType(p);
    [Hle("sqlite3_value_int", Library = Sdk)]
    [Hle("sqlite3_value_int64", Library = Sdk)]
    long SdkValueInt64(uint p) => ValueInt64(p);
    [Hle("sqlite3_value_double", Library = Sdk)] double SdkValueDouble(uint p) => ValueDouble(p);
    [Hle("sqlite3_value_bytes", Library = Sdk)] int SdkValueBytes(uint p) => ValueBytes(p);
    [Hle("sqlite3_value_text", Library = Sdk)] uint SdkValueText(uint p) => ValueText(p);
    [Hle("sqlite3_value_blob", Library = Sdk)] uint SdkValueBlob(uint p) => ValueBlob(p);
    [Hle("sqlite3_create_function", Library = Sdk)]
    int SdkCreateFunction(uint p, uint name, int nArg, int enc, uint app, uint xFunc, uint xStep, uint xFinal) =>
        CreateFunction(p, name, nArg, enc, app, xFunc, xStep, xFinal);
    [Hle("sqlite3_create_collation", Library = Sdk)] int SdkCreateCollation(uint p, uint name, int enc, uint arg, uint cmp) => CreateCollation(p, name, enc, arg, cmp);
    [Hle("sqlite3_user_data", Library = Sdk)] uint SdkUserData(uint p) => UserData(p);
    [Hle("sqlite3_result_null", Library = Sdk)] void SdkResultNull(uint p) => ResultNull(p);
    [Hle("sqlite3_result_int", Library = Sdk)] void SdkResultInt(uint p, int v) => ResultInt(p, v);
    [Hle("sqlite3_result_int64", Library = Sdk)] void SdkResultInt64(uint p, long v) => ResultInt64(p, v);
    [Hle("sqlite3_result_zeroblob", Library = Sdk)] void SdkResultZeroblob(uint p, int n) => ResultZeroblob(p, n);
    [Hle("sqlite3_result_double", Library = Sdk)] void SdkResultDouble(uint p, HleContext hc) => ResultDouble(p, hc);
    [Hle("sqlite3_result_error_nomem", Library = Sdk)] void SdkResultErrorNomem(uint p) => ResultErrorNomem(p);
    [Hle("sqlite3_result_error_toobig", Library = Sdk)] void SdkResultErrorTooBig(uint p) => ResultErrorTooBig(p);
    [Hle("sqlite3_result_error_code", Library = Sdk)] void SdkResultErrorCode(uint p, int code) => ResultErrorCode(p, code);
    [Hle("sqlite3_result_error", Library = Sdk)] void SdkResultError(uint p, uint msg, int n) => ResultError(p, msg, n);
    [Hle("sqlite3_result_text", Library = Sdk)] void SdkResultText(uint p, uint z, int n, uint del) => ResultText(p, z, n, del);
    [Hle("sqlite3_result_blob", Library = Sdk)] void SdkResultBlob(uint p, uint data, int n, uint del) => ResultBlob(p, data, n, del);
    [Hle("sqlite3_result_value", Library = Sdk)] void SdkResultValue(uint p, uint v) => ResultValue(p, v);
    [Hle("sqlite3_progress_handler", Library = Sdk)] void SdkProgressHandler(uint p, int n, uint cb, uint arg) => ProgressHandler(p, n, cb, arg);
    [Hle("sqlite3_commit_hook", Library = Sdk)] uint SdkCommitHook(uint p, uint cb, uint arg) => CommitHook(p, cb, arg);
    [Hle("sqlite3_rollback_hook", Library = Sdk)] uint SdkRollbackHook(uint p, uint cb, uint arg) => RollbackHook(p, cb, arg);
    [Hle("sqlite3_update_hook", Library = Sdk)] uint SdkUpdateHook(uint p, uint cb, uint arg) => UpdateHook(p, cb, arg);
    [Hle("sqlite3_blob_open", Library = Sdk)]
    int SdkBlobOpen(uint p, uint zDb, uint zTable, uint zCol, long row, int flags, uint ppBlob) => BlobOpen(p, zDb, zTable, zCol, row, flags, ppBlob);
    [Hle("sqlite3_blob_bytes", Library = Sdk)] int SdkBlobBytes(uint p) => BlobBytes(p);
    [Hle("sqlite3_blob_read", Library = Sdk)] int SdkBlobRead(uint p, uint buf, int n, int off) => BlobRead(p, buf, n, off);
    [Hle("sqlite3_blob_write", Library = Sdk)] int SdkBlobWrite(uint p, uint data, int n, int off) => BlobWrite(p, data, n, off);
    [Hle("sqlite3_blob_close", Library = Sdk)] int SdkBlobClose(uint p) => BlobClose(p);
    [Hle("sqlite3_mprintf", Library = Sdk)] uint SdkMprintf(uint fmt, VarArgs va) => Mprintf(fmt, va);
    [Hle("sqlite3_vmprintf", Library = Sdk)] uint SdkVmprintf(uint fmt, uint va) => Vmprintf(fmt, va);
    [Hle("sqlite3_snprintf", Library = Sdk)] uint SdkSnprintf(int n, uint buf, uint fmt, VarArgs va) => Snprintf(n, buf, fmt, va);

    // ---- bindings kept for sqlite3_transfer_bindings (the engine underneath cannot read them back)

    readonly Dictionary<uint, Dictionary<int, Val>> _binds = new();

    Dictionary<int, Val>? Binds(uint stmt) { lock (_lock) return _binds.GetValueOrDefault(stmt); }

    int Bound(uint stmt, int i, Val v, int rc)
    {
        if (rc == SQLITE_OK && Get<Stmt>(stmt) != null)
            lock (_lock) { if (!_binds.TryGetValue(stmt, out var b)) _binds[stmt] = b = new(); b[i] = v; }
        return rc;
    }

    /// <summary>sqlite3_transfer_bindings(from, to): SQLITE_ERROR unless both have the same parameter count; the values
    /// bound on the first move to the second.</summary>
    [Hle("sqlite3_transfer_bindings", Library = Sdk)]
    int TransferBindings(uint from, uint to)
    {
        if (Get<Stmt>(from) is not { } a || Get<Stmt>(to) is not { } b) return SQLITE_MISUSE;
        if (raw.sqlite3_bind_parameter_count(a.H) != raw.sqlite3_bind_parameter_count(b.H)) return SQLITE_ERROR;
        foreach (var (i, v) in Binds(from) ?? [])
        {
            int rc = v.Type switch
            {
                1 => raw.sqlite3_bind_int64(b.H, i, v.I),
                2 => raw.sqlite3_bind_double(b.H, i, v.D),
                3 => raw.sqlite3_bind_text(b.H, i, Encoding.UTF8.GetString(v.Bytes ?? [])),
                4 => raw.sqlite3_bind_blob(b.H, i, v.Bytes ?? []),
                _ => raw.sqlite3_bind_null(b.H, i),
            };
            if (rc != SQLITE_OK) return rc;
            Bound(to, i, v, rc);
        }
        return SQLITE_OK;
    }

    /// <summary>sqlite3_expired(stmt): never, the engine re-prepares a statement whose schema changed.</summary>
    [Hle("sqlite3_expired", Library = Sdk)] int Expired(uint p) => 0;

    // ---- UTF-16 (native order: little-endian)

    static string? Str16(uint p, int nBytes = -1)
    {
        if (p == 0) return null;
        if (nBytes >= 0) return Encoding.Unicode.GetString(GuestMemory.ReadSpan(p, nBytes & ~1)).Split('\0')[0];
        var sb = new StringBuilder();
        for (uint a = p; ; a += 2)
        {
            char ch = (char)GuestMemory.Read<ushort>(a);
            if (ch == 0 || sb.Length >= 1 << 20) break;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    uint GuestString16(string? s, bool bigEndian = false)
    {
        if (s == null) return 0;
        byte[] b = (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetBytes(s + "\0");
        return GuestBytes(b, false);
    }

    /// <summary>Runs <paramref name="f"/> with a UTF-8 guest copy of a UTF-16 string.</summary>
    T With8<T>(uint p16, Func<uint, T> f)
    {
        uint p8 = GuestString(Str16(p16));
        try { return f(p8); }
        finally { Free(p8); }
    }

    [Hle("sqlite3_open16", Library = Sdk)] int Open16(uint path, uint ppDb) => With8(path, p => OpenV2(p, ppDb, 6, 0));

    readonly Dictionary<Db, uint> _err16 = new();

    /// <summary>sqlite3_errmsg16: as sqlite3_errmsg, in UTF-16.</summary>
    [Hle("sqlite3_errmsg16", Library = Sdk)]
    uint ErrMsg16(uint p)
    {
        if (Get<Db>(p) is not { } db) return GuestString16(p == 0 ? "out of memory" : "library routine called out of sequence");
        lock (_lock)
        {
            if (_err16.Remove(db, out uint old)) Free(old);
            return _err16[db] = GuestString16(raw.sqlite3_errmsg(db.H).utf8_to_string());
        }
    }

    /// <summary>sqlite3_prepare16 / sqlite3_prepare16_v2(db, sql16, nBytes, stmt**, const void **tail).</summary>
    [Hle("sqlite3_prepare16", Library = Sdk)]
    [Hle("sqlite3_prepare16_v2", Library = Sdk)]
    int Prepare16(uint p, uint sql, int n, uint ppStmt, uint pTail)
    {
        string s = Str16(sql, n) ?? "";
        uint p8 = GuestString(s), tail8 = pTail == 0 ? 0 : Malloc(4);
        try
        {
            int rc = Prepare(p, p8, -1, ppStmt, tail8);
            if (pTail != 0)
            {
                int used8 = (int)(GuestMemory.R32(tail8) - p8);
                string done = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s), 0, Math.Clamp(used8, 0, Encoding.UTF8.GetByteCount(s)));
                GuestMemory.W32(pTail, sql + (uint)done.Length * 2);
            }
            return rc;
        }
        finally { Free(p8); Free(tail8); }
    }

    [Hle("sqlite3_bind_text16", Library = Sdk)]
    int BindText16(uint p, int i, uint z, int n, uint del)
    {
        string? t = Str16(z, n);
        int rc = StOp(p, s => t == null ? raw.sqlite3_bind_null(s.H, i) : raw.sqlite3_bind_text(s.H, i, t));
        RunDestructor(del, z);
        return Bound(p, i, t == null ? new Val { Type = 5 } : new Val { Type = 3, Bytes = Encoding.UTF8.GetBytes(t) }, rc);
    }

    string? ColumnString(uint p, int i) => Get<Stmt>(p) is { } s && raw.sqlite3_column_type(s.H, i) != 5
        ? Encoding.UTF8.GetString(raw.sqlite3_column_blob(s.H, i)) : null;

    /// <summary>sqlite3_column_text16: UTF-16, valid until the statement steps, resets or finalizes.</summary>
    [Hle("sqlite3_column_text16", Library = Sdk)]
    uint ColumnText16(uint p, int i)
    {
        if (Get<Stmt>(p) is not { } s || ColumnString(p, i) is not { } t) return 0;
        return StmtBuf(s, 10, i, () => GuestString16(t));
    }

    [Hle("sqlite3_column_bytes16", Library = Sdk)] int ColumnBytes16(uint p, int i) => (ColumnString(p, i)?.Length ?? 0) * 2;

    uint ColumnName16Of(uint p, int i, int kind, Func<sqlite3_stmt, int, utf8z> get)
    {
        if (Get<Stmt>(p) is not { } s) return 0;
        string? n = get(s.H, i).utf8_to_string();
        return n == null ? 0 : StmtBuf(s, kind, i, () => GuestString16(n));
    }

    [Hle("sqlite3_column_name16", Library = Sdk)] uint ColumnName16(uint p, int i) => ColumnName16Of(p, i, 11, raw.sqlite3_column_name);
    [Hle("sqlite3_column_decltype16", Library = Sdk)] uint ColumnDecltype16(uint p, int i) => ColumnName16Of(p, i, 12, raw.sqlite3_column_decltype);

    /// <summary>sqlite3_value_text16 / 16le / 16be: the value as UTF-16 text, kept with the value.</summary>
    [Hle("sqlite3_value_text16", Library = Sdk)]
    [Hle("sqlite3_value_text16le", Library = Sdk)]
    uint ValueText16(uint p)
    {
        if (V(p) is not { } v || v.Type == 5) return 0;
        if (v.Text16 == 0) v.Text16 = GuestString16(ValueTextString(v));
        return v.Text16;
    }

    [Hle("sqlite3_value_text16be", Library = Sdk)]
    uint ValueText16Be(uint p)
    {
        if (V(p) is not { } v || v.Type == 5) return 0;
        if (v.Text16Be == 0) v.Text16Be = GuestString16(ValueTextString(v), bigEndian: true);
        return v.Text16Be;
    }

    [Hle("sqlite3_value_bytes16", Library = Sdk)] int ValueBytes16(uint p) => V(p) is { Type: not 5 } v ? ValueTextString(v).Length * 2 : 0;

    /// <summary>sqlite3_value_numeric_type: the type after the numeric affinity (text that reads as a number becomes
    /// an integer or a float; the value keeps that conversion).</summary>
    [Hle("sqlite3_value_numeric_type", Library = Sdk)]
    int ValueNumericType(uint p)
    {
        if (V(p) is not { } v) return 5;
        if (v.Type != 3) return v.Type;
        string t = Encoding.UTF8.GetString(v.Bytes!).Trim();
        if (long.TryParse(t, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long l)) { v.Type = 1; v.I = l; }
        else if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) { v.Type = 2; v.D = d; }
        return v.Type;
    }

    [Hle("sqlite3_result_text16", Library = Sdk)]
    [Hle("sqlite3_result_text16le", Library = Sdk)]
    void ResultText16(uint p, uint z, int n, uint del)
    {
        if (Cx(p) is { } c) { if (z == 0) raw.sqlite3_result_null(c.H); else raw.sqlite3_result_text(c.H, Str16(z, n) ?? ""); }
        RunDestructor(del, z);
    }

    [Hle("sqlite3_result_text16be", Library = Sdk)]
    void ResultText16Be(uint p, uint z, int n, uint del)
    {
        if (Cx(p) is { } c)
        {
            if (z == 0) raw.sqlite3_result_null(c.H);
            else
            {
                byte[] b = n >= 0 ? GuestMemory.ReadSpan(z, n & ~1).ToArray() : Encoding.BigEndianUnicode.GetBytes(Str16BeRaw(z));
                raw.sqlite3_result_text(c.H, Encoding.BigEndianUnicode.GetString(b).Split('\0')[0]);
            }
        }
        RunDestructor(del, z);
    }

    static string Str16BeRaw(uint p)
    {
        var sb = new StringBuilder();
        for (uint a = p; sb.Length < 1 << 20; a += 2)
        {
            char ch = (char)(GuestMemory.Read<byte>(a) << 8 | GuestMemory.Read<byte>(a + 1));
            if (ch == 0) break;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    [Hle("sqlite3_result_error16", Library = Sdk)]
    void ResultError16(uint p, uint msg, int n) { if (Cx(p) is { } c) raw.sqlite3_result_error(c.H, Str16(msg, n) ?? ""); }

    [Hle("sqlite3_create_function16", Library = Sdk)]
    int CreateFunction16(uint p, uint name, int nArg, int enc, uint app, uint xFunc, uint xStep, uint xFinal) =>
        With8(name, n => CreateFunction(p, n, nArg, enc, app, xFunc, xStep, xFinal));

    [Hle("sqlite3_create_collation16", Library = Sdk)]
    int CreateCollation16(uint p, uint name, int enc, uint arg, uint cmp) => With8(name, n => CreateCollation(p, n, enc, arg, cmp));

    /// <summary>sqlite3_create_collation_v2(db, name, enc, arg, cmp, destroy): the destructor runs on the argument when
    /// the collation is replaced or the connection closes; here when it is replaced (or at once on failure).</summary>
    [Hle("sqlite3_create_collation_v2", Library = Sdk)]
    int CreateCollationV2(uint p, uint name, int enc, uint arg, uint cmp, uint destroy)
    {
        int rc = CreateCollation(p, name, enc, arg, cmp);
        if (Get<Db>(p) is { } d && Str(name) is { } n)
            lock (_lock)
            {
                if (_collDestroy.Remove((d, n), out var old)) RunDestructor(old.Del, old.Arg);
                if (rc == SQLITE_OK && destroy != 0) _collDestroy[(d, n)] = (destroy, arg);
            }
        if (rc != SQLITE_OK) RunDestructor(destroy, arg);
        return rc;
    }

    readonly Dictionary<(Db, string), (uint Del, uint Arg)> _collDestroy = new();

    /// <summary>sqlite3_collation_needed(db, arg, cb) / 16: the callback a statement needing an unknown collation calls.
    /// The engine underneath reports such a collation as an error at prepare time; the callback is kept.</summary>
    [Hle("sqlite3_collation_needed", Library = Sdk)]
    [Hle("sqlite3_collation_needed16", Library = Sdk)]
    int CollationNeeded(uint p, uint arg, uint cb) => DbOp(p, _ => SQLITE_OK);

    /// <summary>sqlite3_overload_function(db, name, nArg): a placeholder function that fails, unless one exists.</summary>
    [Hle("sqlite3_overload_function", Library = Sdk)]
    int OverloadFunction(uint p, uint name, int nArg) => DbOp(p, d =>
    {
        string n = Str(name) ?? "";
        using (var probe = Probe(d, $"SELECT {n}({string.Join(",", Enumerable.Repeat("NULL", Math.Max(nArg, 0)))})"))
            if (probe.Ok) return SQLITE_OK;
        delegate_function_scalar f = (ctx, _, _) => raw.sqlite3_result_error(ctx, $"unable to use function {n} in the requested context");
        d.Keep.Add(f);
        return raw.sqlite3_create_function(d.H, n, nArg, 1, null, f);
    });

    sealed class ProbeResult(sqlite3_stmt? h) : IDisposable
    {
        public bool Ok => h != null && !h.IsInvalid;
        public void Dispose() { if (Ok) raw.sqlite3_finalize(h!); }
    }

    static ProbeResult Probe(Db d, string sql) => raw.sqlite3_prepare_v2(d.H, sql, out sqlite3_stmt h) == SQLITE_OK ? new(h) : new(null);

    // ---- function contexts

    sealed class AggState { public uint Mem; public int Steps; }

    void CountAggregateStep(sqlite3_context ctx)
    {
        if (ctx.state is not AggState a) ctx.state = a = new AggState();
        a.Steps++;
    }

    void FreeAggregate(sqlite3_context ctx)
    {
        if (ctx.state is AggState a) Free(a.Mem);
        ctx.state = null;
    }

    /// <summary>sqlite3_aggregate_context(ctx, n): zeroed memory that lives from the first step to the final call
    /// (NULL when n &lt;= 0 and nothing was allocated yet).</summary>
    [Hle("sqlite3_aggregate_context", Library = Sdk)]
    uint AggregateContext(uint p, int n)
    {
        if (Cx(p) is not { } c) return 0;
        if (c.H.state is not AggState a) c.H.state = a = new AggState();
        if (a.Mem == 0 && n > 0)
        {
            a.Mem = Malloc((uint)n);
            GuestMemory.Fill(a.Mem, 0, (uint)n);
        }
        return a.Mem;
    }

    /// <summary>sqlite3_aggregate_count(ctx) (deprecated): the steps so far of the aggregate.</summary>
    [Hle("sqlite3_aggregate_count", Library = Sdk)] int AggregateCount(uint p) => Cx(p)?.H.state is AggState a ? a.Steps : 0;

    [Hle("sqlite3_context_db_handle", Library = Sdk)] uint ContextDbHandle(uint p) => Cx(p)?.Db is { } d ? HandleOf(d) : 0;

    /// <summary>sqlite3_get_auxdata: SQLite may drop auxiliary data at any time; this one keeps it only for the call
    /// that set it, so a later call sees NULL and computes it again.</summary>
    [Hle("sqlite3_get_auxdata", Library = Sdk)] uint GetAuxdata(uint p, int arg) => 0;

    [Hle("sqlite3_set_auxdata", Library = Sdk)]
    void SetAuxdata(uint p, int arg, uint data, uint del)
    {
        if (Cx(p) is { } c) c.Aux.Add((del, data));
        else RunDestructor(del, data);
    }

    // ---- table queries

    /// <summary>sqlite3_get_table(db, sql, char ***result, int *rows, int *cols, char **errmsg): column names, then the
    /// rows, as one array of strings from the module heap (the count before it, for sqlite3_free_table).</summary>
    [Hle("sqlite3_get_table", Library = Sdk)]
    int GetTable(uint p, uint sql, uint pResult, uint pRows, uint pCols, uint pErr)
    {
        if (pResult != 0) GuestMemory.W32(pResult, 0);
        if (pRows != 0) GuestMemory.W32(pRows, 0);
        if (pCols != 0) GuestMemory.W32(pCols, 0);
        if (pErr != 0) GuestMemory.W32(pErr, 0);
        if (Get<Db>(p) is not { } db) return SQLITE_MISUSE;
        var cells = new List<string?>();
        int cols = -1, rows = 0;
        string? err = null;
        strdelegate_exec f = (_, values, names) =>
        {
            if (cols < 0) { cols = names.Length; cells.AddRange(names); }
            else if (names.Length != cols) { err = "sqlite3_get_table() called with two or more incompatible queries"; return 1; }
            cells.AddRange(values);
            rows++;
            return 0;
        };
        int rc = raw.sqlite3_exec(db.H, Str(sql) ?? "", f, null, out string? execErr);
        if (err != null) rc = SQLITE_ERROR;
        err ??= execErr;
        if (rc != SQLITE_OK)
        {
            if (pErr != 0 && err != null) GuestMemory.W32(pErr, GuestString(err));
            return rc;
        }
        uint block = Malloc((uint)(cells.Count + 1) * 4);
        GuestMemory.W32(block, (uint)(cells.Count + 1));
        for (int i = 0; i < cells.Count; i++) GuestMemory.W32(block + 4 + (uint)i * 4, GuestString(cells[i]));
        if (pResult != 0) GuestMemory.W32(pResult, block + 4);
        if (pRows != 0) GuestMemory.W32(pRows, (uint)rows);
        if (pCols != 0) GuestMemory.W32(pCols, (uint)Math.Max(cols, 0));
        return SQLITE_OK;
    }

    [Hle("sqlite3_free_table", Library = Sdk)]
    void FreeTable(uint result)
    {
        if (result == 0) return;
        uint block = result - 4, n = GuestMemory.R32(block);
        for (uint i = 1; i < n; i++) Free(GuestMemory.R32(block + i * 4));
        Free(block);
    }

    /// <summary>sqlite3_complete / 16: 1 when the text ends a whole SQL statement.</summary>
    [Hle("sqlite3_complete", Library = Sdk)] int Complete(uint sql) => raw.sqlite3_complete(Str(sql) ?? "");
    [Hle("sqlite3_complete16", Library = Sdk)] int Complete16(uint sql) => raw.sqlite3_complete(Str16(sql) ?? "");

    [Hle("sqlite3_sql", Library = Sdk)]
    uint Sql(uint p) => Get<Stmt>(p) is { } s && raw.sqlite3_sql(s.H).utf8_to_string() is { } t ? StmtBuf(s, 13, 0, () => GuestString(t)) : 0;

    [Hle("sqlite3_stmt_status", Library = Sdk)] int StmtStatus(uint p, int op, int reset) => Get<Stmt>(p) is { } s ? raw.sqlite3_stmt_status(s.H, op, reset) : 0;

    // ---- backups

    sealed class Backup { public sqlite3_backup H = null!; }

    /// <summary>sqlite3_backup_init(dest, destName, src, srcName): NULL on failure (the error on the destination).</summary>
    [Hle("sqlite3_backup_init", Library = Sdk)]
    uint BackupInit(uint dest, uint destName, uint src, uint srcName)
    {
        if (Get<Db>(dest) is not { } d || Get<Db>(src) is not { } s) return 0;
        var h = raw.sqlite3_backup_init(d.H, Str(destName) ?? "main", s.H, Str(srcName) ?? "main");
        return h == null || h.IsInvalid ? 0 : HandleOf(new Backup { H = h });
    }

    [Hle("sqlite3_backup_step", Library = Sdk)] int BackupStep(uint p, int n) => Get<Backup>(p) is { } b ? raw.sqlite3_backup_step(b.H, n) : SQLITE_MISUSE;
    [Hle("sqlite3_backup_remaining", Library = Sdk)] int BackupRemaining(uint p) => Get<Backup>(p) is { } b ? raw.sqlite3_backup_remaining(b.H) : 0;
    [Hle("sqlite3_backup_pagecount", Library = Sdk)] int BackupPagecount(uint p) => Get<Backup>(p) is { } b ? raw.sqlite3_backup_pagecount(b.H) : 0;

    [Hle("sqlite3_backup_finish", Library = Sdk)]
    int BackupFinish(uint p)
    {
        if (p == 0 || Get<Backup>(p) is not { } b) return SQLITE_OK;
        int rc = raw.sqlite3_backup_finish(b.H);
        Drop(b);
        return rc;
    }

    // ---- authorizer, trace, profile

    /// <summary>sqlite3_set_authorizer(db, int (*cb)(void *, int action, const char *, const char *, const char *,
    /// const char *), arg).</summary>
    [Hle("sqlite3_set_authorizer", Library = Sdk)]
    int SetAuthorizer(uint p, uint cb, uint arg) => DbOp(p, d =>
    {
        if (cb == 0) return raw.sqlite3_set_authorizer(d.H, (strdelegate_authorizer?)null, null);
        strdelegate_authorizer f = (_, action, a, b, c, e) =>
        {
            uint[] s = [GuestString(a), GuestString(b), GuestString(c), GuestString(e)];
            try { return (int)Call(cb, arg, (uint)action, s[0], s[1], s[2], s[3]); }
            finally { foreach (uint x in s) Free(x); }
        };
        d.Keep.Add(f);
        return raw.sqlite3_set_authorizer(d.H, f, null);
    });

    readonly Dictionary<(Db, int), uint> _traceArgs = new();

    /// <summary>sqlite3_trace(db, void (*cb)(void *, const char *sql), arg): the previous argument.</summary>
    [Hle("sqlite3_trace", Library = Sdk)]
    uint Trace(uint p, uint cb, uint arg)
    {
        if (Get<Db>(p) is not { } d) return 0;
        strdelegate_trace? f = cb == 0 ? null : (_, sql) =>
        {
            uint s = GuestString(sql);
            try { Call(cb, arg, s); } finally { Free(s); }
        };
        if (f != null) d.Keep.Add(f);
        raw.sqlite3_trace(d.H, f, null);
        return SwapHookArg(d, 3, cb == 0 ? 0 : arg);
    }

    /// <summary>sqlite3_profile(db, void (*cb)(void *, const char *sql, sqlite3_uint64 ns), arg).</summary>
    [Hle("sqlite3_profile", Library = Sdk)]
    uint Profile(uint p, uint cb, uint arg)
    {
        if (Get<Db>(p) is not { } d) return 0;
        strdelegate_profile? f = cb == 0 ? null : (_, sql, ns) =>
        {
            uint s = GuestString(sql);
            try { Call(cb, arg, s, (uint)ns, (uint)((ulong)ns >> 32)); } finally { Free(s); }
        };
        if (f != null) d.Keep.Add(f);
        raw.sqlite3_profile(d.H, f, null);
        return SwapHookArg(d, 4, cb == 0 ? 0 : arg);
    }

    // ---- memory and status

    [Hle("sqlite3_memory_used", Library = Sdk)] long MemoryUsed() => raw.sqlite3_memory_used();
    [Hle("sqlite3_memory_highwater", Library = Sdk)] long MemoryHighwater(int reset) => raw.sqlite3_memory_highwater(reset);
    /// <summary>sqlite3_release_memory(n): the bytes freed; the engine keeps no spare cache to give back.</summary>
    [Hle("sqlite3_release_memory", Library = Sdk)] int ReleaseMemory(int n) => 0;
    [Hle("sqlite3_soft_heap_limit", Library = Sdk)] void SoftHeapLimit(int n) => raw.sqlite3_soft_heap_limit64(n);
    /// <summary>sqlite3_memory_alarm (deprecated): accepted, the engine keeps its own limit.</summary>
    [Hle("sqlite3_memory_alarm", Library = Sdk)] int MemoryAlarm(uint cb, uint arg, long n) => SQLITE_OK;

    [Hle("sqlite3_status", Library = Sdk)]
    int Status(int op, uint cur, uint hi, int reset)
    {
        int rc = raw.sqlite3_status(op, out int c, out int h, reset);
        if (cur != 0) GuestMemory.W32(cur, (uint)c);
        if (hi != 0) GuestMemory.W32(hi, (uint)h);
        return rc;
    }

    /// <summary>sceSqliteConfigMallocMethods(methods): the SDK's hook to give the library the application's allocator.
    /// The engine underneath allocates host memory and the strings it hands out come from the module heap, so the
    /// methods are accepted.</summary>
    [Hle("sceSqliteConfigMallocMethods", Library = Sdk)] int ConfigMallocMethods(uint methods) => methods == 0 ? SQLITE_MISUSE : SQLITE_OK;

    [Hle("sqlite3_os_init", Library = Sdk)] int OsInit() => SQLITE_OK;
    [Hle("sqlite3_enable_shared_cache", Library = Sdk)] int EnableSharedCache(int on) => raw.sqlite3_enable_shared_cache(on);
    /// <summary>sqlite3_test_control: the test hooks are not built into the release library (0).</summary>
    [Hle("sqlite3_test_control", Library = Sdk)] int TestControl(int op) => 0;
    /// <summary>sqlite3_file_control(db, name, op, arg): no VFS of this engine answers the guest's opcodes.</summary>
    [Hle("sqlite3_file_control", Library = Sdk)] int FileControl(uint p, uint name, int op, uint arg) => DbOp(p, _ => SQLITE_NOTFOUND);

    /// <summary>sqlite3_db_config(db, op, ...): lookaside (1001) is accepted; the on/off switches (1002 foreign keys,
    /// 1003 triggers) take (int, int *).</summary>
    [Hle("sqlite3_db_config", Library = Sdk)]
    int DbConfig(uint p, int op, uint a, uint b) => DbOp(p, d =>
    {
        if (op == 1001) return SQLITE_OK;
        if (op is 1002 or 1003)
        {
            int rc = raw.sqlite3_db_config(d.H, op, (int)a, out int result);
            if (b != 0) GuestMemory.W32(b, (uint)result);
            return rc;
        }
        return SQLITE_ERROR;
    });

    // ---- extensions and virtual tables

    readonly List<uint> _autoExtensions = new();

    /// <summary>sqlite3_auto_extension(entry): kept in the list (the engine underneath cannot run guest extensions,
    /// which need the sqlite3_api_routines table of the library).</summary>
    [Hle("sqlite3_auto_extension", Library = Sdk)]
    int AutoExtension(uint entry) { lock (_lock) if (!_autoExtensions.Contains(entry)) _autoExtensions.Add(entry); return SQLITE_OK; }

    [Hle("sqlite3_reset_auto_extension", Library = Sdk)] void ResetAutoExtension() { lock (_lock) _autoExtensions.Clear(); }

    [Hle("sqlite3_enable_load_extension", Library = Sdk)] int EnableLoadExtension(uint p, int on) => DbOp(p, _ => SQLITE_OK);

    /// <summary>sqlite3_load_extension: a PS Vita application cannot load a shared library into SQLite: "not
    /// authorized" (SQLITE_ERROR and the message).</summary>
    [Hle("sqlite3_load_extension", Library = Sdk)]
    int LoadExtension(uint p, uint file, uint proc, uint pErr)
    {
        if (pErr != 0) GuestMemory.W32(pErr, GuestString("not authorized"));
        return Get<Db>(p) == null ? SQLITE_MISUSE : SQLITE_ERROR;
    }

    readonly Dictionary<(Db, string), (uint Module, uint Arg, uint Destroy)> _modules = new();

    /// <summary>sqlite3_create_module / _v2(db, name, module, arg[, destroy]): the module is registered on the
    /// connection; a CREATE VIRTUAL TABLE with it fails in the engine underneath (no guest virtual tables).</summary>
    [Hle("sqlite3_create_module", Library = Sdk)]
    int CreateModule(uint p, uint name, uint module, uint arg) => CreateModuleV2(p, name, module, arg, 0);

    [Hle("sqlite3_create_module_v2", Library = Sdk)]
    int CreateModuleV2(uint p, uint name, uint module, uint arg, uint destroy) => DbOp(p, d =>
    {
        string n = Str(name) ?? "";
        lock (_lock)
        {
            if (_modules.Remove((d, n), out var old)) RunDestructor(old.Destroy, old.Arg);
            _modules[(d, n)] = (module, arg, destroy);
        }
        return SQLITE_OK;
    });

    /// <summary>sqlite3_declare_vtab: only valid inside a module's xCreate/xConnect, which never runs here.</summary>
    [Hle("sqlite3_declare_vtab", Library = Sdk)] int DeclareVtab(uint p, uint sql) => SQLITE_MISUSE;

    // ---- mutexes (SceKernelLwMutex work areas in the module heap)

    ThreadMgr? _tmx;
    ThreadMgr Tmx => _tmx ??= Emu.Hle.Modules.OfType<ThreadMgr>().First();
    readonly Dictionary<int, uint> _staticMutex = new();
    readonly Dictionary<Db, uint> _dbMutex = new();

    uint NewMutex(bool recursive)
    {
        uint m = Malloc(0x20);
        GuestMemory.Fill(m, 0, 0x20);
        Tmx.sceKernelCreateLwMutex(m, "SceSqliteMutex", recursive ? 2u : 0u, 0, 0);
        return m;
    }

    /// <summary>sqlite3_mutex_alloc(type): 0 fast, 1 recursive (new ones), 2.. the static mutexes (one each, shared).</summary>
    [Hle("sqlite3_mutex_alloc", Library = Sdk)]
    uint MutexAlloc(int type)
    {
        if (type is 0 or 1) return NewMutex(type == 1);
        if (type is < 2 or > 8) return 0;
        lock (_lock) return _staticMutex.TryGetValue(type, out uint m) ? m : _staticMutex[type] = NewMutex(false);
    }

    [Hle("sqlite3_mutex_free", Library = Sdk)]
    void MutexFree(uint m)
    {
        if (m == 0) return;
        lock (_lock) if (_staticMutex.ContainsValue(m) || _dbMutex.ContainsValue(m)) return;
        Tmx.sceKernelDeleteLwMutex(m);
        Free(m);
    }

    [Hle("sqlite3_mutex_enter", Library = Sdk)] void MutexEnter(uint m) { if (m != 0) Tmx.sceKernelLockLwMutex(m, 1, new Ptr<uint>(0)); }
    [Hle("sqlite3_mutex_try", Library = Sdk)] int MutexTry(uint m) => m == 0 || Tmx.sceKernelTryLockLwMutex(m, 1) == 0 ? SQLITE_OK : SQLITE_BUSY;
    [Hle("sqlite3_mutex_leave", Library = Sdk)] void MutexLeave(uint m) { if (m != 0) Tmx.sceKernelUnlockLwMutex(m, 1); }

    /// <summary>sqlite3_db_mutex(db): the connection's own (recursive) mutex.</summary>
    [Hle("sqlite3_db_mutex", Library = Sdk)]
    uint DbMutex(uint p)
    {
        if (Get<Db>(p) is not { } d) return 0;
        lock (_lock) return _dbMutex.TryGetValue(d, out uint m) ? m : _dbMutex[d] = NewMutex(true);
    }

    // ---- VFS list

    /// <summary>The VFS objects the guest sees: the default one (a sqlite3_vfs of version 1 named "psp2", 0x44 bytes
    /// with the OS file size and maximum path; its methods are the engine's, not callable) and the ones registered.</summary>
    readonly List<uint> _vfs = new();
    uint _defaultVfs;

    uint DefaultVfs()
    {
        if (_defaultVfs != 0) return _defaultVfs;
        uint v = Malloc(0x44);
        GuestMemory.Fill(v, 0, 0x44);
        GuestMemory.W32(v, 1);            // iVersion
        GuestMemory.W32(v + 4, 0x40);     // szOsFile
        GuestMemory.W32(v + 8, 1024);     // mxPathname
        GuestMemory.W32(v + 0x10, GuestString("psp2")); // zName
        lock (_lock) _vfs.Insert(0, v);
        return _defaultVfs = v;
    }

    /// <summary>sqlite3_vfs_find(name): the VFS of that name, the default for NULL.</summary>
    [Hle("sqlite3_vfs_find", Library = Sdk)]
    uint VfsFind(uint name)
    {
        DefaultVfs();
        string? n = Str(name);
        lock (_lock) return n == null ? _vfs[0] : _vfs.FirstOrDefault(v => Str(GuestMemory.R32(v + 0x10)) == n);
    }

    /// <summary>sqlite3_vfs_register(vfs, makeDefault): listed (first when default). The engine underneath keeps
    /// reading and writing the host files the guest paths resolve to.</summary>
    [Hle("sqlite3_vfs_register", Library = Sdk)]
    int VfsRegister(uint vfs, int makeDefault)
    {
        if (vfs == 0) return SQLITE_MISUSE;
        DefaultVfs();
        lock (_lock)
        {
            _vfs.Remove(vfs);
            if (makeDefault != 0) _vfs.Insert(0, vfs); else _vfs.Add(vfs);
        }
        return SQLITE_OK;
    }

    [Hle("sqlite3_vfs_unregister", Library = Sdk)]
    int VfsUnregister(uint vfs) { lock (_lock) _vfs.Remove(vfs); return SQLITE_OK; }
}
