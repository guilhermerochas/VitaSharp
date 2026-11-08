using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibc (vs0:sys/external/libc.suprx, FW 3.74: the Dinkumware C library). Every process normally runs the firmware
/// module (games ship it in app0:sce_module, system programs get the vs0 one: <see cref="HleModule.FirmwareFallbackOnly"/>),
/// so these serve a process that does not load it. Behaviour follows the 3.74 module: errno in TLS slot 0x88
/// (sceKernelGetTLSAddr), 16-bit wchar_t, the C locale, its strerror texts, the Annex K constraint handler that by
/// default prints the message to stderr and returns ERANGE (34).
/// </summary>
public sealed partial class Libc : HleModule
{
    public override bool FirmwareFallbackOnly => true;

    // errno values of the SCE libc (errno.h; the strerror table of libc.suprx)
    internal const int EPERM = 1, ENOENT = 2, EINTR = 4, EIO = 5, EBADF = 9, EAGAIN = 11, ENOMEM = 12, EACCES = 13,
        EBUSY = 16, EEXIST = 17, ENOTDIR = 20, EISDIR = 21, EINVAL = 22, EMFILE = 24, ENOSPC = 28, ESPIPE = 29,
        EDOM = 33, ERANGE = 34, ENOSYS = 88, ENOTEMPTY = 90, ENAMETOOLONG = 91, EILSEQ = 138, EFPOS = 152;

    const int ErrnoTlsSlot = 0x88;

    IoFileMgr? _io;
    IoFileMgr Io => _io ??= Emu.Hle.Modules.OfType<IoFileMgr>().First();
    Clib? _clib;
    Clib ClibM => _clib ??= Emu.Hle.Modules.OfType<Clib>().First();

    /// <summary>The calling thread's errno word (TLS slot 0x88, as _sceLibcErrnoLoc returns it).</summary>
    static uint ErrnoAddr() => GuestThread.Current is { } t ? t.TlsBase + ErrnoTlsSlot * 4 : 0;

    internal static void SetErrno(int e) { if (ErrnoAddr() is var a and not 0) GuestMemory.W32(a, (uint)e); }
    static int GetErrno() => ErrnoAddr() is var a and not 0 ? (int)GuestMemory.R32(a) : 0;

    [Hle] uint _sceLibcErrnoLoc() => ErrnoAddr();

    /// <summary>errno from an sceIo* result (0x8001xxxx: the low half is the errno number).</summary>
    static int IoErrno(int r) => (r & 0xFFFF0000) == unchecked((int)0x80010000) ? r & 0xFFFF : EIO;

    // ------------------------------------------------------------------ helpers

    static byte B(uint a) => GuestMemory.Read<byte>(a);
    static ushort W(uint a) => GuestMemory.Read<ushort>(a);
    static uint StrLen(uint s) { uint n = 0; while (B(s + n) != 0) n++; return n; }
    static uint WcsLen(uint s) { uint n = 0; while (W(s + n * 2) != 0) n++; return n; }

    static string ReadLatin1(uint s, uint n)
    {
        var sb = new StringBuilder((int)n);
        for (uint i = 0; i < n; i++) sb.Append((char)B(s + i));
        return sb.ToString();
    }

    static string ReadWide(uint s)
    {
        var sb = new StringBuilder();
        for (uint p = s; ; p += 2) { ushort c = W(p); if (c == 0) break; sb.Append((char)c); }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ memory

    [Hle] uint memcpy(uint dst, uint src, uint n) { if (n != 0) GuestMemory.Copy(dst, src, n); return dst; }
    [Hle] uint memmove(uint dst, uint src, uint n) { if (n != 0) GuestMemory.Copy(dst, src, n); return dst; }
    [Hle] uint memset(uint dst, int c, uint n) { if (n != 0) GuestMemory.Fill(dst, (byte)c, n); return dst; }

    [Hle]
    int memcmp(uint a, uint b, uint n)
    {
        for (uint i = 0; i < n; i++)
        {
            int d = B(a + i) - B(b + i);
            if (d != 0) return d;
        }
        return 0;
    }

    [Hle]
    uint memchr(uint s, int c, uint n)
    {
        for (uint i = 0; i < n; i++) if (B(s + i) == (byte)c) return s + i;
        return 0;
    }

    // ------------------------------------------------------------------ strings

    [Hle] uint strlen(uint s) => StrLen(s);

    [Hle]
    uint strnlen_s(uint s, uint max)
    {
        if (s == 0) return 0;
        uint n = 0;
        while (n < max && B(s + n) != 0) n++;
        return n;
    }

    [Hle]
    uint strcpy(uint dst, uint src)
    {
        uint n = StrLen(src);
        GuestMemory.Copy(dst, src, n + 1);
        return dst;
    }

    [Hle]
    uint strncpy(uint dst, uint src, uint n)
    {
        uint i = 0;
        for (; i < n; i++) { byte c = B(src + i); GuestMemory.Write(dst + i, c); if (c == 0) break; }
        for (; i < n; i++) GuestMemory.Write(dst + i, (byte)0);
        return dst;
    }

    [Hle]
    uint strcat(uint dst, uint src)
    {
        uint d = dst + StrLen(dst), n = StrLen(src);
        GuestMemory.Copy(d, src, n + 1);
        return dst;
    }

    [Hle]
    uint strncat(uint dst, uint src, uint n)
    {
        uint d = dst + StrLen(dst), i = 0;
        for (; i < n; i++) { byte c = B(src + i); if (c == 0) break; GuestMemory.Write(d + i, c); }
        GuestMemory.Write(d + i, (byte)0);
        return dst;
    }

    [Hle]
    int strcmp(uint a, uint b)
    {
        for (uint i = 0; ; i++)
        {
            int ca = B(a + i), cb = B(b + i);
            if (ca != cb) return ca - cb;
            if (ca == 0) return 0;
        }
    }

    [Hle]
    int strncmp(uint a, uint b, uint n)
    {
        for (uint i = 0; i < n; i++)
        {
            int ca = B(a + i), cb = B(b + i);
            if (ca != cb) return ca - cb;
            if (ca == 0) return 0;
        }
        return 0;
    }

    /// <summary>C locale collation: the byte order (strcoll = strcmp, strxfrm copies).</summary>
    [Hle] int strcoll(uint a, uint b) => strcmp(a, b);

    [Hle]
    uint strxfrm(uint dst, uint src, uint n)
    {
        uint len = StrLen(src);
        if (len < n) GuestMemory.Copy(dst, src, len + 1);
        return len;
    }

    static int Lower(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;

    [Hle]
    int strcasecmp(uint a, uint b)
    {
        for (uint i = 0; ; i++)
        {
            int ca = Lower(B(a + i)), cb = Lower(B(b + i));
            if (ca != cb) return ca - cb;
            if (ca == 0) return 0;
        }
    }

    [Hle]
    int strncasecmp(uint a, uint b, uint n)
    {
        for (uint i = 0; i < n; i++)
        {
            int ca = Lower(B(a + i)), cb = Lower(B(b + i));
            if (ca != cb) return ca - cb;
            if (ca == 0) return 0;
        }
        return 0;
    }

    [Hle]
    uint strchr(uint s, int ch)
    {
        byte c = (byte)ch;
        for (uint p = s; ; p++) { byte b = B(p); if (b == c) return p; if (b == 0) return 0; }
    }

    [Hle]
    uint strrchr(uint s, int ch)
    {
        byte c = (byte)ch;
        uint last = 0;
        for (uint p = s; ; p++) { byte b = B(p); if (b == c) last = p; if (b == 0) return last; }
    }

    [Hle]
    uint strstr(uint s1, uint s2)
    {
        uint n2 = StrLen(s2);
        if (n2 == 0) return s1;
        uint n1 = StrLen(s1);
        if (n1 < n2) return 0;
        int idx = GuestMemory.ReadSpan(s1, (int)n1).IndexOf(GuestMemory.ReadSpan(s2, (int)n2));
        return idx < 0 ? 0 : s1 + (uint)idx;
    }

    static bool InSet(uint set, byte c) { for (uint p = set; ; p++) { byte b = B(p); if (b == 0) return false; if (b == c) return true; } }

    [Hle] uint strspn(uint s, uint set) { uint n = 0; for (byte c; (c = B(s + n)) != 0 && InSet(set, c); n++) { } return n; }
    [Hle] uint strcspn(uint s, uint set) { uint n = 0; for (byte c; (c = B(s + n)) != 0 && !InSet(set, c); n++) { } return n; }

    [Hle]
    uint strpbrk(uint s, uint set)
    {
        for (uint p = s; ; p++) { byte c = B(p); if (c == 0) return 0; if (InSet(set, c)) return p; }
    }

    /// <summary>The tokenizer step shared by strtok/strtok_r: *save holds the resume point.</summary>
    uint Tok(uint s, uint delim, ref uint save)
    {
        if (s == 0) s = save;
        if (s == 0) return 0;
        s += strspn(s, delim);
        if (B(s) == 0) { save = s; return 0; }
        uint end = s + strcspn(s, delim);
        if (B(end) != 0) { GuestMemory.Write(end, (byte)0); save = end + 1; }
        else save = end;
        return s;
    }

    readonly Dictionary<int, uint> _strtokSave = new();

    [Hle]
    uint strtok(uint s, uint delim)
    {
        int tid = GuestThread.Current?.Uid ?? 0;
        lock (_strtokSave)
        {
            _strtokSave.TryGetValue(tid, out uint save);
            uint r = Tok(s, delim, ref save);
            _strtokSave[tid] = save;
            return r;
        }
    }

    [Hle]
    uint strtok_r(uint s, uint delim, uint savePtr)
    {
        uint save = GuestMemory.R32(savePtr);
        uint r = Tok(s, delim, ref save);
        GuestMemory.W32(savePtr, save);
        return r;
    }

    /// <summary>strtok_s(s, &amp;smax, delim, &amp;ptr) (C11 Annex K): smax counts the characters left to scan.</summary>
    [Hle]
    uint strtok_s(uint s, uint smaxPtr, uint delim, uint savePtr)
    {
        if (smaxPtr == 0 || delim == 0 || savePtr == 0 || (s == 0 && GuestMemory.R32(savePtr) == 0))
        {
            Constraint("strtok_s: bad argument");
            return 0;
        }
        uint save = GuestMemory.R32(savePtr), start = s != 0 ? s : save;
        uint r = Tok(s, delim, ref save);
        uint max = GuestMemory.R32(smaxPtr);
        uint used = save - start;
        GuestMemory.W32(smaxPtr, used > max ? 0 : max - used);
        GuestMemory.W32(savePtr, save);
        return r;
    }

    [Hle]
    uint strdup(uint s)
    {
        uint n = StrLen(s) + 1;
        uint p = HeapAlloc(n, 8);
        if (p == 0) { SetErrno(ENOMEM); return 0; }
        GuestMemory.Copy(p, s, n);
        return p;
    }

    // ------------------------------------------------------------------ strerror

    static readonly Dictionary<int, string> ErrnoText = new()
    {
        [0] = "no error", [1] = "operation not permitted", [2] = "no such file or directory", [3] = "no such process",
        [4] = "interrupted function call", [5] = "input/output error", [6] = "no such device or address",
        [7] = "argument list too long", [8] = "exec format error", [9] = "bad file descriptor", [10] = "no child processes",
        [11] = "resource temporarily unavailable", [12] = "not enough space", [13] = "permission denied", [14] = "bad address",
        [16] = "resource busy", [17] = "file exists", [18] = "improper link", [19] = "no such device", [20] = "not a directory",
        [21] = "is a directory", [22] = "invalid argument", [23] = "too many files open in system", [24] = "too many open files",
        [25] = "bad I/O control operation", [27] = "file too large", [28] = "no space left on device", [29] = "invalid seek",
        [30] = "read-only file system", [31] = "too many links", [32] = "broken pipe", [33] = "domain error", [34] = "range error",
        [45] = "resource deadlock avoided", [46] = "no locks available", [77] = "bad message", [88] = "function not implemented",
        [90] = "directory not empty", [91] = "filename too long", [116] = "operation timed out", [119] = "operation in progress",
        [122] = "bad message buffer length", [134] = "not supported", [138] = "multibyte encoding error",
        [140] = "operation canceled", [152] = "file positioning error",
    };

    // errno 1000..1078: the std::errc names (system_error), same table in libc.suprx
    static readonly string[] ErrcText =
    [
        "argument_list_too_long", "permission_denied", "address_in_use", "address_not_available", "address_family_not_supported",
        "resource_unavailable_try_again", "connection_already_in_progress", "bad_file_descriptor", "bad_message",
        "device_or_resource_busy", "operation_canceled", "no_child_process", "connection_aborted", "connection_refused",
        "connection_reset", "resource_deadlock_would_occur", "destination_address_required", "argument_out_of_domain",
        "file_exists", "bad_address", "file_too_large", "host_unreachable", "identifier_removed", "illegal_byte_sequence",
        "operation_in_progress", "interrupted", "invalid_argument", "io_error", "already_connected", "is_a_directory",
        "too_many_synbolic_link_levels", "too_many_files_open", "too_many_links", "message_size", "filename_too_long",
        "network_down", "network_reset", "network_unreachable", "too_many_files_open_in_system", "no_buffer_space",
        "no_message_available", "no_such_device", "no_such_file_or_directory", "executable_format_error", "no_lock_available",
        "no_link", "not_enough_memory", "no_message", "no_protocol_option", "no_space_on_device", "no_stream_resources",
        "not_a_stream", "function_not_supported", "not_connected", "not_a_directory", "directory_not_empty",
        "state_not_recoverable", "not_a_socket", "not_supported", "inappropriate_io_control_operation",
        "no_such_device_or_address", "operation_not_supported", "other", "value_too_large", "owner_dead",
        "operation_not_permitted", "broken_pipe", "protocol_error", "protocol_not_supported", "wrong_protocol_type",
        "result_out_of_range", "read_only_file_system", "invalid_seek", "no_such_process", "stream_timeout", "timed_out",
        "text_file_busy", "operation_would_block", "cross_device_link",
    ];

    internal static string ErrorText(int e) => ErrnoText.TryGetValue(e, out var s) ? s
        : e is >= 1000 and < 1000 + 79 ? ErrcText[e - 1000] : $"error 0x{e:x8}";

    uint _staticBlock, _staticUsed;
    const uint StaticBlockSize = 0x10000;

    /// <summary>Small module-owned guest data (strings, FILE objects, buffers) carved from "SceLibc" pages.</summary>
    internal uint StaticAlloc(uint size, uint align = 8)
    {
        lock (_constStrings)
        {
            uint at = GuestMemory.AlignUp(_staticUsed, align);
            if (_staticBlock == 0 || at + size > StaticBlockSize)
            {
                if (size > StaticBlockSize / 2) return Mem.Alloc(size, "SceLibc", MemoryRegion.User);
                _staticBlock = Mem.Alloc(StaticBlockSize, "SceLibc", MemoryRegion.User);
                at = 0;
            }
            _staticUsed = at + size;
            GuestMemory.Fill(_staticBlock + at, 0, size);
            return _staticBlock + at;
        }
    }

    readonly Dictionary<string, uint> _constStrings = new();

    /// <summary>A guest copy of a constant string, made once (the module's read-only strings).</summary>
    internal uint ConstString(string s)
    {
        lock (_constStrings)
        {
            if (_constStrings.TryGetValue(s, out var p)) return p;
            var bytes = Encoding.Latin1.GetBytes(s);
            p = StaticAlloc((uint)bytes.Length + 1);
            bytes.CopyTo(GuestMemory.Span(p, bytes.Length));
            GuestMemory.Write(p + (uint)bytes.Length, (byte)0);
            return _constStrings[s] = p;
        }
    }

    [Hle] uint strerror(int e) => ConstString(ErrorText(e));
    [Hle] uint strerrorlen_s(int e) => (uint)ErrorText(e).Length;

    [Hle]
    int strerror_s(uint buf, uint size, int e)
    {
        if (buf == 0 || size == 0 || size > int.MaxValue) return Constraint("strerror_s: bad argument");
        string s = ErrorText(e);
        if (s.Length < size) { GuestMemory.WriteCString(buf, s); return 0; }
        // truncated: as much as fits, ending in "..." when there is room for it
        var bytes = Encoding.Latin1.GetBytes(s);
        uint n = size - 1;
        bytes.AsSpan(0, (int)n).CopyTo(GuestMemory.Span(buf, (int)n));
        if (n >= 3) GuestMemory.Fill(buf + n - 3, (byte)'.', 3);
        GuestMemory.Write(buf + n, (byte)0);
        return ERANGE;
    }

    [Hle]
    void perror(uint s)
    {
        string msg = ErrorText(GetErrno());
        string pre = s != 0 && B(s) != 0 ? GuestMemory.ReadCString(s) + ": " : "";
        WriteStream(StderrFile(), Encoding.Latin1.GetBytes(pre + msg + "\n"));
    }

    // ------------------------------------------------------------------ Annex K constraint handler

    uint _constraintHandler; // 0 = the built-in default (print the message to stderr)

    /// <summary>A runtime-constraint violation: the handler gets (msg, NULL, ERANGE); returns ERANGE as the module does.</summary>
    int Constraint(string msg)
    {
        uint h = _constraintHandler;
        if (h != 0) { if (GuestThread.Current is { } t) t.CallGuest(h, ConstString(msg), 0, ERANGE); }
        else WriteStream(StderrFile(), Encoding.Latin1.GetBytes(msg + "\n"));
        return ERANGE;
    }

    /// <summary>Installs a handler and returns the previous one; the default (the message to stderr) is NULL, as in
    /// libc.suprx, so NULL also restores it.</summary>
    [Hle]
    uint set_constraint_handler_s(uint handler)
    {
        uint old = _constraintHandler;
        _constraintHandler = handler;
        return old;
    }

    [Hle]
    void abort_handler_s(uint msg, uint ptr, int error)
    {
        WriteStream(StderrFile(), Encoding.Latin1.GetBytes((msg != 0 ? GuestMemory.ReadCString(msg) : "") + "\n"));
        abort();
    }

    [Hle] void ignore_handler_s(uint msg, uint ptr, int error) { }

    // Annex K string functions: a violation calls the handler and, where a destination exists, leaves it empty

    static bool BadSize(uint n) => n == 0 || n > int.MaxValue;

    [Hle]
    int strcpy_s(uint dst, uint size, uint src)
    {
        if (dst == 0 || BadSize(size)) return Constraint("strcpy_s: bad destination argument");
        uint len = src == 0 ? uint.MaxValue : StrLen(src);
        if (src == 0 || size <= len || Overlap(dst, size, src, len + 1))
        {
            GuestMemory.Write(dst, (byte)0);
            return Constraint("strcpy_s: bad argument");
        }
        GuestMemory.Copy(dst, src, len + 1);
        return 0;
    }

    [Hle]
    int strncpy_s(uint dst, uint size, uint src, uint n)
    {
        if (dst == 0 || BadSize(size)) return Constraint("strncpy_s: bad destination argument");
        if (src == 0 || n > int.MaxValue) { GuestMemory.Write(dst, (byte)0); return Constraint("strncpy_s: bad argument"); }
        uint len = strnlen_s(src, n);
        if (size <= len || Overlap(dst, size, src, len)) { GuestMemory.Write(dst, (byte)0); return Constraint("strncpy_s: bad argument"); }
        GuestMemory.Copy(dst, src, len);
        GuestMemory.Write(dst + len, (byte)0);
        return 0;
    }

    [Hle]
    int strcat_s(uint dst, uint size, uint src)
    {
        if (dst == 0 || BadSize(size)) return Constraint("strcat_s: bad destination argument");
        uint dlen = strnlen_s(dst, size);
        uint slen = src == 0 ? uint.MaxValue : StrLen(src);
        if (src == 0 || dlen == size || size - dlen <= slen) { GuestMemory.Write(dst, (byte)0); return Constraint("strcat_s: bad argument"); }
        GuestMemory.Copy(dst + dlen, src, slen + 1);
        return 0;
    }

    [Hle]
    int strncat_s(uint dst, uint size, uint src, uint n)
    {
        if (dst == 0 || BadSize(size)) return Constraint("strncat_s: bad destination argument");
        uint dlen = strnlen_s(dst, size);
        if (src == 0 || n > int.MaxValue || dlen == size) { GuestMemory.Write(dst, (byte)0); return Constraint("strncat_s: bad argument"); }
        uint slen = strnlen_s(src, n);
        if (size - dlen <= slen) { GuestMemory.Write(dst, (byte)0); return Constraint("strncat_s: bad argument"); }
        GuestMemory.Copy(dst + dlen, src, slen);
        GuestMemory.Write(dst + dlen + slen, (byte)0);
        return 0;
    }

    static bool Overlap(uint a, uint an, uint b, uint bn) => a < b + bn && b < a + an;

    [Hle]
    int memcpy_s(uint dst, uint size, uint src, uint n)
    {
        if (dst == 0 || size > int.MaxValue) return Constraint("memcpy_s: bad destination argument");
        if (src == 0 || n > size || Overlap(dst, n, src, n))
        {
            if (size != 0) GuestMemory.Fill(dst, 0, size);
            return Constraint("memcpy_s: bad argument");
        }
        if (n != 0) GuestMemory.Copy(dst, src, n);
        return 0;
    }

    [Hle]
    int memmove_s(uint dst, uint size, uint src, uint n)
    {
        if (dst == 0 || size > int.MaxValue) return Constraint("memmove_s: bad destination argument");
        if (src == 0 || n > size)
        {
            if (size != 0) GuestMemory.Fill(dst, 0, size);
            return Constraint("memmove_s: bad argument");
        }
        if (n != 0) GuestMemory.Copy(dst, src, n);
        return 0;
    }

    // ------------------------------------------------------------------ ctype (C locale)

    internal static bool IsSpace(int c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
    [Hle] int isalnum(int c) => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? 1 : 0;
    [Hle] int isalpha(int c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? 1 : 0;
    [Hle] int isblank(int c) => c is ' ' or '\t' ? 1 : 0;
    [Hle] int iscntrl(int c) => c is >= 0 and < 0x20 or 0x7F ? 1 : 0;
    [Hle] int isdigit(int c) => c is >= '0' and <= '9' ? 1 : 0;
    [Hle] int isgraph(int c) => c is > 0x20 and < 0x7F ? 1 : 0;
    [Hle] int islower(int c) => c is >= 'a' and <= 'z' ? 1 : 0;
    [Hle] int isprint(int c) => c is >= 0x20 and < 0x7F ? 1 : 0;
    [Hle] int ispunct(int c) => c is > 0x20 and < 0x7F && isalnum(c) == 0 ? 1 : 0;
    [Hle] int isspace(int c) => IsSpace(c) ? 1 : 0;
    [Hle] int isupper(int c) => c is >= 'A' and <= 'Z' ? 1 : 0;
    [Hle] int isxdigit(int c) => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f' ? 1 : 0;
    [Hle] int tolower(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;
    [Hle] int toupper(int c) => c is >= 'a' and <= 'z' ? c - 32 : c;

    uint _ctypeVar, _tolowerVar, _toupperVar;

    /// <summary>A pointer variable to a table of 257 shorts for -1 (EOF) and 0..255; the pointer is to entry 0.</summary>
    uint TableVariable(ref uint var, Func<int, short> entry)
    {
        lock (_constStrings)
        {
            if (var != 0) return var;
            uint t = StaticAlloc(257 * 2, 4);
            for (int c = -1; c < 256; c++) GuestMemory.Write(t + (uint)(c + 1) * 2, entry(c));
            uint v = StaticAlloc(4, 4);
            GuestMemory.W32(v, t + 2);
            return var = v;
        }
    }

    /// <summary>The data the C headers' inline code reads: _Ctype (the classification table of Libc.Streams.cs'
    /// CtypeMask), _Tolotab and _Touptab (case mapping, EOF to itself), the FILE objects _Stdin/_Stdout/_Stderr, and
    /// SceLibstdcxx's _BADOFF (-1) and _Fpz (a zero fpos_t).</summary>
    void RegisterLibcVariables()
    {
        Emu.Hle.RegisterVariable(0x3CE6109D, () => TableVariable(ref _ctypeVar, c => c < 0 ? (short)0 : CtypeMask(c)));
        Emu.Hle.RegisterVariable(0xD662E07C, () => TableVariable(ref _tolowerVar, c => (short)(c < 0 ? -1 : tolower(c))));
        Emu.Hle.RegisterVariable(0x36878958, () => TableVariable(ref _toupperVar, c => (short)(c < 0 ? -1 : toupper(c))));
        Emu.Hle.RegisterVariable(0x66B7406C, () => StdinFile().Addr);
        Emu.Hle.RegisterVariable(0x5D8C1282, () => StdoutFile().Addr);
        Emu.Hle.RegisterVariable(0xDDF9BB09, () => StderrFile().Addr);
        Emu.Hle.RegisterVariable(0x9C449444, () => _badoff != 0 ? _badoff : _badoff = ConstWord(0xFFFFFFFF, 4));
        Emu.Hle.RegisterVariable(0xD9478DCF, () => _fpz != 0 ? _fpz : _fpz = ConstWord(0, 16));
    }

    uint _badoff, _fpz;

    uint ConstWord(uint v, uint size)
    {
        uint p = StaticAlloc(size, 8);
        GuestMemory.W32(p, v);
        return p;
    }

    // ------------------------------------------------------------------ stdlib integer helpers

    [Hle] int abs(int v) => v < 0 ? -v : v;
    [Hle] int labs(int v) => v < 0 ? -v : v;
    [Hle] long llabs(long v) => v < 0 ? -v : v;
    [Hle] long imaxabs(long v) => v < 0 ? -v : v;

    /// <summary>div/ldiv: the {quot, rem} struct goes through the hidden result pointer in r0.</summary>
    [Hle("div")]
    [Hle("ldiv")]
    void div(uint result, int n, int d)
    {
        int q = d == 0 ? 0 : n == int.MinValue && d == -1 ? n : n / d, r = d == 0 || (n == int.MinValue && d == -1) ? 0 : n % d;
        GuestMemory.W32(result, (uint)q);
        GuestMemory.W32(result + 4, (uint)r);
    }

    /// <summary>lldiv/imaxdiv: a 16-byte struct, returned through the hidden result pointer in r0.</summary>
    [Hle("lldiv")]
    [Hle("imaxdiv")]
    uint lldiv(uint result, long n, long d)
    {
        long q = d == 0 ? 0 : n == long.MinValue && d == -1 ? n : n / d, r = d == 0 || (n == long.MinValue && d == -1) ? 0 : n % d;
        GuestMemory.Write(result, q);
        GuestMemory.Write(result + 8, r);
        return result;
    }

    // rand: one process-wide 64-bit LCG (state * 0x5851F42D4C957F2D + 1, bits 32..62 returned; RAND_MAX 0x7FFFFFFF),
    // seeded 1; rand_r: the 32-bit ANSI LCG on the caller's word, bits 16..31 returned (libc.suprx 3.74)
    ulong _randState = 1;
    readonly object _randLock = new();

    [Hle("rand")]
    [Hle("_ZSt14_Random_devicev")] // std::_Random_device: libc.suprx forwards to rand
    int rand()
    {
        lock (_randLock)
        {
            _randState = _randState * 0x5851F42D4C957F2DUL + 1;
            return (int)((_randState >> 32) & 0x7FFFFFFF);
        }
    }

    [Hle] void srand(uint seed) { lock (_randLock) _randState = seed; }

    [Hle]
    uint rand_r(uint seedPtr)
    {
        uint s = GuestMemory.R32(seedPtr) * 0x41C64E6D + 0x3039;
        GuestMemory.W32(seedPtr, s);
        return s >> 16;
    }

    // ------------------------------------------------------------------ sorting and searching (call the guest comparator)

    [Hle]
    void qsort(uint base_, uint n, uint size, uint cmp) => Sort(base_, n, size, (a, b) => (int)GuestThread.Current!.CallGuest(cmp, a, b));

    [Hle]
    int qsort_s(uint base_, uint n, uint size, uint cmp, uint ctx)
    {
        if (n > int.MaxValue || size > int.MaxValue || (n > 0 && (base_ == 0 || cmp == 0))) return Constraint("qsort_s: bad argument");
        Sort(base_, n, size, (a, b) => (int)GuestThread.Current!.CallGuest(cmp, a, b, ctx));
        return 0;
    }

    /// <summary>In-place insertion/merge sort of guest elements through a guest comparator (stable; any order the
    /// comparator accepts is a valid qsort result).</summary>
    static void Sort(uint base_, uint n, uint size, Func<uint, uint, int> cmp)
    {
        if (n < 2 || size == 0) return;
        var items = new byte[n][];
        for (uint i = 0; i < n; i++) items[i] = GuestMemory.ReadSpan(base_ + i * size, (int)size).ToArray();
        // compare through scratch copies in place: sort indices by moving elements back to guest memory per compare
        var idx = Enumerable.Range(0, (int)n).ToArray();
        uint tmpA = base_, tmpB = base_ + size;
        // the comparator must see the elements in guest memory: write the pair into the first two slots
        int Cmp(int x, int y)
        {
            items[x].CopyTo(GuestMemory.Span(tmpA, (int)size));
            items[y].CopyTo(GuestMemory.Span(tmpB, (int)size));
            return cmp(tmpA, tmpB);
        }
        var sorted = MergeSort(idx, Cmp);
        for (uint i = 0; i < n; i++) items[sorted[i]].CopyTo(GuestMemory.Span(base_ + i * size, (int)size));
    }

    static int[] MergeSort(int[] a, Func<int, int, int> cmp)
    {
        if (a.Length < 2) return a;
        var l = MergeSort(a[..(a.Length / 2)], cmp);
        var r = MergeSort(a[(a.Length / 2)..], cmp);
        var o = new int[a.Length];
        int i = 0, j = 0, k = 0;
        while (i < l.Length && j < r.Length) o[k++] = cmp(l[i], r[j]) <= 0 ? l[i++] : r[j++];
        while (i < l.Length) o[k++] = l[i++];
        while (j < r.Length) o[k++] = r[j++];
        return o;
    }

    [Hle]
    uint bsearch(uint key, uint base_, uint n, uint size, uint cmp) => BSearch(base_, n, size, p => (int)GuestThread.Current!.CallGuest(cmp, key, p));

    [Hle]
    uint bsearch_s(uint key, uint base_, uint n, uint size, uint cmp, uint ctx)
    {
        if (n > int.MaxValue || size > int.MaxValue || (n > 0 && (key == 0 || base_ == 0 || cmp == 0))) { Constraint("bsearch_s: bad argument"); return 0; }
        return BSearch(base_, n, size, p => (int)GuestThread.Current!.CallGuest(cmp, key, p, ctx));
    }

    static uint BSearch(uint base_, uint n, uint size, Func<uint, int> cmp)
    {
        uint lo = 0, hi = n;
        while (lo < hi)
        {
            uint mid = lo + (hi - lo) / 2, p = base_ + mid * size;
            int c = cmp(p);
            if (c == 0) return p;
            if (c < 0) hi = mid; else lo = mid + 1;
        }
        return 0;
    }
}
