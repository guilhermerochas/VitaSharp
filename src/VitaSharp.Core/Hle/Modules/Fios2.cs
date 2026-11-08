using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceFios2 (vs0:sys/external/libfios2.suprx, FW 3.74): the FIOS2 file API. Games ship and run the firmware module
/// (app0:sce_module/libfios2.suprx); this serves a process that does not load it. Handles follow the module's encoding
/// (bits 14-15 type: 1 op, 2 file, 3 directory; bits 16-30 a generation, never 0x7FFF which marks pass-through handles).
/// File handles sit on sceIo descriptors, paths go through the process overlays (SceFios2User) like any sceIo path.
/// Every operation runs to completion when it is issued: an asynchronous call returns an op that is already done
/// (its completion callback, if any, has run), so sceFiosOpWait/IsDone report the result at once. Times are
/// SceFiosTime microseconds (sceKernelGetProcessTimeWide) and SceFiosDate nanoseconds since 1970 (RTC).
/// PSARC archives mounted with sceFiosArchiveMount(Sync) are read through their table of contents (zlib blocks).
/// </summary>
public sealed partial class Fios2 : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    // SCE_FIOS_ERROR_* (libfios2.suprx error-name table)
    internal const int OK = 0, IN_PROGRESS = 1;
    internal const int ERR_UNIMPLEMENTED = unchecked((int)0x80820000), ERR_CANT_ALLOCATE_OP = unchecked((int)0x80820001),
        ERR_BAD_PATH = unchecked((int)0x80820005), ERR_BAD_PTR = unchecked((int)0x80820006), ERR_BAD_OFFSET = unchecked((int)0x80820007),
        ERR_BAD_SIZE = unchecked((int)0x80820008), ERR_BAD_IOVCNT = unchecked((int)0x80820009), ERR_BAD_OP = unchecked((int)0x8082000A),
        ERR_BAD_FH = unchecked((int)0x8082000B), ERR_BAD_DH = unchecked((int)0x8082000C), ERR_NOT_A_FILE = unchecked((int)0x8082000E),
        ERR_NOT_A_DIRECTORY = unchecked((int)0x8082000F), ERR_EOF = unchecked((int)0x80820010), ERR_CANCELLED = unchecked((int)0x80820012),
        ERR_ACCESS = unchecked((int)0x80820013), ERR_DECOMPRESSION = unchecked((int)0x80820014), ERR_READ_ONLY = unchecked((int)0x80820015),
        ERR_WRITE_ONLY = unchecked((int)0x80820016), ERR_PATH_TOO_LONG = unchecked((int)0x80820018), ERR_BAD_INDEX = unchecked((int)0x8082001C),
        ERR_BUSY = unchecked((int)0x8082001E), ERR_BAD_ARCHIVE = unchecked((int)0x8082001F);

    const int TypeOp = 1, TypeFh = 2, TypeDh = 3;
    const int PathMax = 1024;

    // SceFiosOpenParams.openFlags
    const uint O_READ = 1, O_WRITE = 2, O_APPEND = 4, O_CREAT = 8, O_TRUNC = 0x10;
    // SceFiosStat/SceFiosDirEntry statFlags
    const uint STATUS_DIRECTORY = 1, STATUS_READABLE = 2, STATUS_WRITABLE = 4;

    sealed class FileH
    {
        public int Fd = -1;
        public required string Path;
        public long Pos;
        public bool Read, Write, Append;
        public uint OpenParams; // guest copy of the SceFiosOpenParams (sceFiosFHGetOpenParams)
        public uint PathCopy;   // guest copy of the path (sceFiosFHGetPath)
        public byte[]? ArchiveData; // a file inside a mounted archive: its decompressed bytes
        public Psarc? MountedArchive; // the handle a mount returned
    }

    sealed class DirH
    {
        public required string Path;
        public required List<(string Name, bool Dir, long Size)> Entries;
        public int Index;
        public uint PathCopy;
    }

    sealed class Op
    {
        public int Err;
        public long Actual, Request, Offset;
        public uint Buffer, PathCopy, Attr;
    }

    readonly Dictionary<int, FileH> _files = new();
    readonly Dictionary<int, DirH> _dirs = new();
    readonly Dictionary<int, Op> _ops = new();
    readonly Dictionary<int, int> _fhToFdCache = new();
    readonly object _lock = new();
    int _gen = 1, _index;
    bool _initialized;
    uint _params; // guest copy of the SceFiosParams given to sceFiosInitialize
    uint _defaultAttr; // global default SceFiosOpAttr (0x20 bytes)
    int _suspend;
    readonly List<(string MountPoint, Psarc Archive)> _mounts = new();

    IoFileMgr? _io;
    IoFileMgr Io => _io ??= Emu.Hle.Modules.OfType<IoFileMgr>().First();
    Libc? _libc;
    Libc LibcM => _libc ??= Emu.Hle.Modules.OfType<Libc>().First();

    int NewHandle(int type)
    {
        int index = Interlocked.Increment(ref _index) & 0x3FFF;
        int gen = _gen;
        _gen = _gen % 0x7FFE + 1;
        return (gen << 16) | (type << 14) | index;
    }

    static int TypeOf(int h) => (h >> 14) & 3;

    /// <summary>The sceIo descriptor behind a FIOS2 file handle, -1 when unknown.</summary>
    internal int FdOfHandle(int fh)
    {
        lock (_lock) return _files.TryGetValue(fh, out var f) ? f.Fd : -1;
    }

    /// <summary>The FIOS2 file handle of a descriptor, -1 when none.</summary>
    internal int HandleOfFd(int fd)
    {
        lock (_lock)
            foreach (var (h, f) in _files) if (f.Fd == fd && fd >= 0) return h;
        return -1;
    }

    uint GuestString(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        uint p = LibcM.StaticAlloc((uint)b.Length + 1);
        b.CopyTo(GuestMemory.Span(p, b.Length));
        return p;
    }

    static int FromIo(int r) => r >= 0 ? r : (r & 0xFFFF) switch
    {
        2 => ERR_BAD_PATH,      // ENOENT
        9 => ERR_BAD_FH,        // EBADF
        13 or 1 => ERR_ACCESS,  // EACCES / EPERM
        20 => ERR_NOT_A_DIRECTORY,
        21 => ERR_NOT_A_FILE,
        30 => ERR_READ_ONLY,    // EROFS
        91 => ERR_PATH_TOO_LONG,
        _ => r,                 // SCE_ERROR_ERRNO_* pass through (EEXIST, ENOTEMPTY, ENOSPC...)
    };

    // ------------------------------------------------------------------ operations

    /// <summary>Records a finished operation and runs the attribute's completion callback (event 1 = complete).</summary>
    int Complete(uint attr, int err, long actual = 0, long request = 0, uint buffer = 0, long offset = 0, string? path = null)
    {
        var op = new Op { Err = err, Actual = actual, Request = request, Buffer = buffer, Offset = offset, Attr = attr, PathCopy = path != null ? GuestString(path) : 0 };
        int id;
        lock (_lock) { id = NewHandle(TypeOp); _ops[id] = op; }
        uint cb = attr != 0 ? GuestMemory.R32(attr + 8) : 0;
        if (cb != 0 && GuestThread.Current is { } t) t.CallGuest(cb, GuestMemory.R32(attr + 12), (uint)id, 1, (uint)err);
        return id;
    }

    Op? GetOp(int op) { lock (_lock) return _ops.GetValueOrDefault(op); }

    [Hle] int sceFiosOpWait(int op) => GetOp(op) is { } o ? o.Err : ERR_BAD_OP;
    [Hle] int sceFiosOpWaitUntil(int op, long deadline) => sceFiosOpWait(op);
    /// <summary>Waits, deletes the op and returns its count, or its error (SCE_FIOS_ERROR_EOF counts as success, as in
    /// libfios2.suprx: a sync read at the end of a file returns the bytes it got, 0 at the very end).</summary>
    [Hle] long sceFiosOpSyncWait(int op) { var o = GetOp(op); if (o == null) return ERR_BAD_OP; long r = o.Err < 0 && o.Err != ERR_EOF ? o.Err : o.Actual; sceFiosOpDelete(op); return r; }
    [Hle] long sceFiosOpSyncWaitForIO(int op) => sceFiosOpSyncWait(op);
    [Hle] int sceFiosOpIsDone(int op) => GetOp(op) != null ? 1 : 0;
    /// <summary>Operations finish when issued, so none is ever cancelled.</summary>
    [Hle] int sceFiosOpIsCancelled(int op) => 0;
    [Hle] int sceFiosOpGetError(int op) => GetOp(op) is { } o ? o.Err : ERR_BAD_OP;
    [Hle] long sceFiosOpGetActualCount(int op) => GetOp(op)?.Actual ?? 0;
    [Hle] long sceFiosOpGetRequestCount(int op) => GetOp(op)?.Request ?? 0;
    [Hle] long sceFiosOpGetOffset(int op) => GetOp(op)?.Offset ?? 0;
    [Hle] uint sceFiosOpGetBuffer(int op) => GetOp(op)?.Buffer ?? 0;
    [Hle] uint sceFiosOpGetPath(int op) => GetOp(op)?.PathCopy ?? 0;
    [Hle] uint sceFiosOpGetAttr(int op) => GetOp(op)?.Attr ?? 0;
    /// <summary>Cancelling a finished operation changes nothing (it already completed).</summary>
    [Hle] void sceFiosOpCancel(int op) { }
    [Hle] void sceFiosOpReschedule(int op, long deadline) { }
    [Hle] void sceFiosOpRescheduleWithPriority(int op, long deadline, int priority) { }

    [Hle]
    void sceFiosOpDelete(int op)
    {
        Op? o;
        lock (_lock) { if (!_ops.Remove(op, out o)) return; }
        uint cb = o.Attr != 0 ? GuestMemory.R32(o.Attr + 8) : 0;
        if (cb != 0 && GuestThread.Current is { } t) t.CallGuest(cb, GuestMemory.R32(o.Attr + 12), (uint)op, 2, 0); // event 2 = delete
    }

    [Hle] void sceFiosCancelAllOps() { }
    [Hle] int sceFiosIsIdle() => 1;

    [Hle]
    int sceFiosGetAllOps(uint outOps, int max)
    {
        lock (_lock)
        {
            int i = 0;
            foreach (var id in _ops.Keys) { if (outOps != 0 && i < max) GuestMemory.W32(outOps + (uint)i * 4, (uint)id); i++; }
            return i;
        }
    }

    [Hle]
    int sceFiosGetAllFHs(uint outFh, int max)
    {
        lock (_lock)
        {
            int i = 0;
            foreach (var id in _files.Keys) { if (outFh != 0 && i < max) GuestMemory.W32(outFh + (uint)i * 4, (uint)id); i++; }
            return i;
        }
    }

    [Hle]
    int sceFiosGetAllDHs(uint outDh, int max)
    {
        lock (_lock)
        {
            int i = 0;
            foreach (var id in _dirs.Keys) { if (outDh != 0 && i < max) GuestMemory.W32(outDh + (uint)i * 4, (uint)id); i++; }
            return i;
        }
    }

    [Hle]
    int sceFiosIsValidHandle(int h)
    {
        lock (_lock)
            return TypeOf(h) switch { TypeOp => _ops.ContainsKey(h), TypeFh => _files.ContainsKey(h), TypeDh => _dirs.ContainsKey(h), _ => false } ? 1 : 0;
    }

    // ------------------------------------------------------------------ init / parameters

    /// <summary>sceFiosInitialize(params): params->paramsSize (bits 1-15 of the first word) must cover the struct.</summary>
    [Hle]
    int sceFiosInitialize(uint p)
    {
        if (p == 0) return ERR_BAD_PTR;
        lock (_lock)
        {
            if (_initialized) return 0;
            uint size = (GuestMemory.R32(p) >> 1) & 0x7FFF;
            _params = LibcM.StaticAlloc(Math.Max(size, 0x80u));
            GuestMemory.Copy(_params, p, Math.Min(size, 0x80u));
            GuestMemory.W32(_params, GuestMemory.R32(_params) | 1); // initialized bit
            _initialized = true;
        }
        Log.Info("SceFios2 (HLE): initialized");
        return 0;
    }

    [Hle]
    void sceFiosTerminate()
    {
        sceFiosCloseAllFiles();
        lock (_lock) { _initialized = false; _ops.Clear(); _dirs.Clear(); _mounts.Clear(); }
    }

    [Hle]
    int sceFiosIsInitialized(uint outParams)
    {
        lock (_lock)
        {
            if (!_initialized) return 0;
            if (outParams != 0)
            {
                uint size = (GuestMemory.R32(_params) >> 1) & 0x7FFF;
                GuestMemory.Copy(outParams, _params, Math.Min(Math.Max(size, 4u), 0x80u));
            }
            return 1;
        }
    }

    [Hle]
    void sceFiosUpdateParameters(uint p)
    {
        if (p == 0) return;
        lock (_lock)
        {
            if (_params == 0) return;
            uint size = Math.Min((GuestMemory.R32(p) >> 1) & 0x7FFF, 0x80u);
            GuestMemory.Copy(_params, p, size);
            GuestMemory.W32(_params, GuestMemory.R32(_params) | (_initialized ? 1u : 0));
        }
    }

    uint DefaultAttr() { lock (_lock) return _defaultAttr != 0 ? _defaultAttr : _defaultAttr = LibcM.StaticAlloc(0x20); }

    [Hle] int sceFiosGetGlobalDefaultOpAttr(uint outAttr) { if (outAttr == 0) return 0; GuestMemory.Copy(outAttr, DefaultAttr(), 0x20); return 1; }
    [Hle] int sceFiosSetGlobalDefaultOpAttr(uint attr) { if (attr == 0) return 0; GuestMemory.Copy(DefaultAttr(), attr, 0x20); return 1; }
    [Hle] int sceFiosGetDefaultOpAttr(uint outAttr) => sceFiosGetGlobalDefaultOpAttr(outAttr);

    [Hle] void sceFiosSuspend() { lock (_lock) _suspend++; }
    [Hle] void sceFiosResume() { lock (_lock) if (_suspend > 0) _suspend--; }
    [Hle] int sceFiosIsSuspended() { lock (_lock) return _suspend > 0 ? 1 : 0; }
    [Hle] uint sceFiosGetSuspendCount() { lock (_lock) return (uint)_suspend; }
    [Hle] void sceFiosShutdownAndCancelOps() { sceFiosCancelAllOps(); sceFiosCloseAllFiles(); }

    [Hle]
    void sceFiosCloseAllFiles()
    {
        List<int> all;
        lock (_lock) all = _files.Keys.ToList();
        foreach (var fh in all) CloseFile(fh);
    }

    // ------------------------------------------------------------------ time and date

    [Hle] long sceFiosTimeGetCurrent() => (long)K.ProcessTimeUs;
    [Hle] long sceFiosTimeIntervalToNanoseconds(long t) => t * 1000;
    [Hle] long sceFiosTimeIntervalFromNanoseconds(long ns) => (ns + 999) / 1000;
    [Hle] long sceFiosDateGetCurrent() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;

    static long DateOf(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) * 100;
    static DateTime FromDate(long ns) => DateTime.UnixEpoch.AddTicks(ns / 100);

    /// <summary>sceFiosDateToComponents(date, struct tm *out): UTC fields, year since 1900, month from 0.</summary>
    [Hle]
    uint sceFiosDateToComponents(long date, uint tm)
    {
        if (tm == 0) return 0;
        var d = FromDate(date);
        GuestMemory.W32(tm, (uint)d.Second);
        GuestMemory.W32(tm + 4, (uint)d.Minute);
        GuestMemory.W32(tm + 8, (uint)d.Hour);
        GuestMemory.W32(tm + 12, (uint)d.Day);
        GuestMemory.W32(tm + 16, (uint)(d.Month - 1));
        GuestMemory.W32(tm + 20, (uint)(d.Year - 1900));
        GuestMemory.W32(tm + 24, (uint)d.DayOfWeek);
        GuestMemory.W32(tm + 28, (uint)(d.DayOfYear - 1));
        GuestMemory.W32(tm + 32, 0);
        return tm;
    }

    [Hle]
    long sceFiosDateFromComponents(uint tm)
    {
        if (tm == 0) return 0;
        try
        {
            int I(uint o) => (int)GuestMemory.R32(tm + o);
            var d = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddYears(I(20)).AddMonths(I(16)).AddDays(I(12) - 1).AddHours(I(8)).AddMinutes(I(4)).AddSeconds(I(0));
            return DateOf(d);
        }
        catch (ArgumentOutOfRangeException) { return 0; }
    }

    /// <summary>SceDateTime: u16 year, month, day, hour, minute, second, u32 microsecond.</summary>
    [Hle]
    uint sceFiosDateToSceDateTime(long date, uint dt)
    {
        if (dt == 0) return 0;
        var d = FromDate(date);
        GuestMemory.Write(dt, (ushort)d.Year);
        GuestMemory.Write(dt + 2, (ushort)d.Month);
        GuestMemory.Write(dt + 4, (ushort)d.Day);
        GuestMemory.Write(dt + 6, (ushort)d.Hour);
        GuestMemory.Write(dt + 8, (ushort)d.Minute);
        GuestMemory.Write(dt + 10, (ushort)d.Second);
        GuestMemory.W32(dt + 12, (uint)(d.Ticks % TimeSpan.TicksPerSecond / 10));
        return dt;
    }

    [Hle]
    long sceFiosDateFromSceDateTime(uint dt)
    {
        if (dt == 0) return 0;
        try
        {
            var d = new DateTime(GuestMemory.Read<ushort>(dt), GuestMemory.Read<ushort>(dt + 2), GuestMemory.Read<ushort>(dt + 4),
                GuestMemory.Read<ushort>(dt + 6), GuestMemory.Read<ushort>(dt + 8), GuestMemory.Read<ushort>(dt + 10), DateTimeKind.Utc).AddTicks(GuestMemory.R32(dt + 12) * 10L);
            return DateOf(d);
        }
        catch (ArgumentOutOfRangeException) { return 0; }
    }

    // ------------------------------------------------------------------ paths

    static string Normalize(string p)
    {
        p = p.Replace('\\', '/');
        var sb = new StringBuilder();
        foreach (char c in p) { if (c == '/' && sb.Length > 0 && sb[^1] == '/') continue; sb.Append(c); }
        string r = sb.ToString();
        return r.Length > 1 && r.EndsWith('/') ? r[..^1] : r;
    }

    /// <summary>sceFiosPathNormalize(path): in place; repeated and trailing slashes collapse, backslashes become slashes.</summary>
    [Hle] uint sceFiosPathNormalize(uint path) { if (path != 0) GuestMemory.WriteCString(path, Normalize(GuestMemory.ReadCString(path, PathMax)), PathMax); return path; }

    /// <summary>sceFiosPathcmp: compares paths case-insensitively with '/' and '\' equal.</summary>
    [Hle] int sceFiosPathcmp(string a, string b) => PathCmp(a, b, int.MaxValue);
    [Hle] int sceFiosPathncmp(string a, string b, uint n) => PathCmp(a, b, (int)Math.Min(n, int.MaxValue));

    static int PathCmp(string a, string b, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int ca = i < a.Length ? Fold(a[i]) : 0, cb = i < b.Length ? Fold(b[i]) : 0;
            if (ca != cb) return ca - cb;
            if (ca == 0) return 0;
        }
        return 0;
        static int Fold(char c) => c == '\\' ? '/' : char.ToLowerInvariant(c);
    }

    /// <summary>sceFiosResolveSync(attr, const SceFiosTuple *in, SceFiosTuple *out): the overlay-resolved form of
    /// the tuple's path (SceFiosTuple: i64 offset, i64 size, char path[1024]); offset and size are copied.</summary>
    [Hle]
    int sceFiosResolveSync(uint attr, uint inTuple, uint outTuple)
    {
        if (inTuple == 0 || outTuple == 0) return ERR_BAD_PTR;
        string path = GuestMemory.ReadCString(inTuple + 16, PathMax);
        string r = Emu.Hle.Modules.OfType<Fios2User>().FirstOrDefault()?.Resolve(path) ?? path;
        if (Encoding.UTF8.GetByteCount(r) + 1 > PathMax) return ERR_PATH_TOO_LONG;
        if (outTuple != inTuple) GuestMemory.Copy(outTuple, inTuple, 16);
        GuestMemory.WriteCString(outTuple + 16, r, PathMax);
        return 0;
    }

    [Hle] int sceFiosResolve(uint attr, uint inTuple, uint outTuple) => Complete(attr, sceFiosResolveSync(attr, inTuple, outTuple));

    // ------------------------------------------------------------------ overlays (the user-side wrappers of SceFios2User)

    Fios2User Overlays => Emu.Hle.Modules.OfType<Fios2User>().First();
    [Hle] int sceFiosOverlayAdd(uint overlay, uint outId) => Overlays.AddOverlay(overlay, outId);
    [Hle] int sceFiosOverlayRemove(int id) => Overlays.RemoveOverlay(id);
    [Hle] int sceFiosOverlayGetInfo(int id, uint outOverlay) => Overlays.GetOverlayInfo(id, outOverlay);
    [Hle] int sceFiosOverlayModify(int id, uint overlay) => Overlays.ModifyOverlay(id, overlay);
    [Hle] int sceFiosOverlayGetList(uint outIds, uint max, uint actual) => Overlays.GetOverlayList(outIds, max, actual);
    [Hle] int sceFiosOverlayResolveSync(int mode, string path, uint outPath, uint max) => Overlays.ResolveOverlay(mode, path, outPath, max);

    // ------------------------------------------------------------------ printing / debug

    [Hle] int sceFiosPrintf(string fmt, VarArgs va) { string s = CFormat.Format(fmt, va); Log.Info("[guest fios] " + s.TrimEnd('\n')); return s.Length; }

    [Hle]
    int sceFiosVprintf(string fmt, uint vaList)
    {
        string s = CFormat.Format(fmt, new VarArgs(new WordList(vaList), 0));
        Log.Info("[guest fios] " + s.TrimEnd('\n'));
        return s.Length;
    }

    sealed class WordList(uint p) : IVaSource
    {
        uint _p = p;
        public uint Next() { uint v = GuestMemory.R32(_p); _p += 4; return v; }
        public uint? Address => _p;
    }

    static string ErrorName(int e) => e switch
    {
        0 => "SCE_FIOS_OK", 1 => "SCE_FIOS_IN_PROGRESS",
        _ when (uint)e >= 0x80820000 && (uint)e <= 0x8082001F => ErrorNames[(uint)e - 0x80820000],
        _ => $"0x{e:X8}",
    };

    static readonly string[] ErrorNames =
    [
        "UNIMPLEMENTED", "CANT_ALLOCATE_OP", "CANT_ALLOCATE_FH", "CANT_ALLOCATE_DH", "CANT_ALLOCATE_CHUNK", "BAD_PATH", "BAD_PTR",
        "BAD_OFFSET", "BAD_SIZE", "BAD_IOVCNT", "BAD_OP", "BAD_FH", "BAD_DH", "BAD_ALIGNMENT", "NOT_A_FILE", "NOT_A_DIRECTORY", "EOF",
        "TIMEOUT", "CANCELLED", "ACCESS", "DECOMPRESSION", "READ_ONLY", "WRITE_ONLY", "MEDIA_GONE", "PATH_TOO_LONG",
        "TOO_MANY_OVERLAYS", "BAD_OVERLAY", "BAD_ORDER", "BAD_INDEX", "EVENT_NOT_HANDLED", "BUSY", "BAD_ARCHIVE",
    ];

    [Hle] void sceFiosDebugDumpOp(int op) => Log.Info(GetOp(op) is { } o ? $"[fios] op 0x{op:X8}: err {ErrorName(o.Err)} actual {o.Actual} of {o.Request}" : $"[fios] op 0x{op:X8}: invalid");

    [Hle]
    void sceFiosDebugDumpFH(int fh)
    {
        lock (_lock) Log.Info(_files.TryGetValue(fh, out var f) ? $"[fios] fh 0x{fh:X8}: {f.Path} at {f.Pos}" : $"[fios] fh 0x{fh:X8}: invalid");
    }

    [Hle]
    void sceFiosDebugDumpDH(int dh)
    {
        lock (_lock) Log.Info(_dirs.TryGetValue(dh, out var d) ? $"[fios] dh 0x{dh:X8}: {d.Path} entry {d.Index}/{d.Entries.Count}" : $"[fios] dh 0x{dh:X8}: invalid");
    }
}
