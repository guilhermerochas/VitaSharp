using System.Text;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceLibc stdio: FILE streams over sceIo file descriptors (stdin/stdout/stderr are the kernel's 0x10/0x11/0x12, whose
// output goes to the log), the printf/scanf families (narrow and wide) and remove/rename. A FILE* is a small guest
// object owned by the module; its state (descriptor, mode, end-of-file and error flags, pushed-back bytes,
// orientation) is kept here. Streams are unbuffered: every write reaches the descriptor at once, which only changes
// when data becomes visible, never what is written.
public sealed partial class Libc
{
    const int EOF = -1;
    const uint FileObjectSize = 0x40;

    sealed class Stream
    {
        public uint Addr;
        public int Fd;
        public bool CanRead, CanWrite, Append, Eof, Error;
        public readonly Stack<byte> Pushback = new();
        public int Orientation; // fwide: <0 byte, >0 wide, 0 not set
        public bool Std;
    }

    readonly Dictionary<uint, Stream> _streams = new();
    readonly Stack<uint> _freeFiles = new();
    Stream? _stdin, _stdout, _stderr;

    Stream StdStream(ref Stream? s, int fd, bool read)
    {
        lock (_streams)
        {
            if (s != null) return s;
            s = new Stream { Addr = StaticAlloc(FileObjectSize), Fd = fd, CanRead = read, CanWrite = !read, Std = true };
            GuestMemory.W32(s.Addr, (uint)fd);
            _streams[s.Addr] = s;
            return s;
        }
    }

    Stream StdinFile() => StdStream(ref _stdin, 0x10, true);
    Stream StdoutFile() => StdStream(ref _stdout, 0x11, false);
    Stream StderrFile() => StdStream(ref _stderr, 0x12, false);

    Stream? Get(uint file)
    {
        lock (_streams) return _streams.GetValueOrDefault(file);
    }

    Stream NewStream(int fd, bool read, bool write, bool append)
    {
        lock (_streams)
        {
            uint a = _freeFiles.Count > 0 ? _freeFiles.Pop() : StaticAlloc(FileObjectSize);
            GuestMemory.Fill(a, 0, FileObjectSize);
            GuestMemory.W32(a, (uint)fd);
            var s = new Stream { Addr = a, Fd = fd, CanRead = read, CanWrite = write, Append = append };
            _streams[a] = s;
            return s;
        }
    }

    /// <summary>fopen mode → sceIo flags: r, w, a, optional '+', 'b' ignored, 'x' exclusive create (C11).</summary>
    static bool ParseMode(string mode, out int flags, out bool read, out bool write, out bool append)
    {
        const int O_RDONLY = 1, O_WRONLY = 2, O_RDWR = 3, O_APPEND = 0x100, O_CREAT = 0x200, O_TRUNC = 0x400, O_EXCL = 0x800;
        flags = 0; read = write = append = false;
        if (mode.Length == 0) return false;
        bool plus = mode.IndexOf('+', 1) > 0, excl = mode.IndexOf('x', 1) > 0;
        switch (mode[0])
        {
            case 'r': flags = plus ? O_RDWR : O_RDONLY; read = true; write = plus; break;
            case 'w': flags = (plus ? O_RDWR : O_WRONLY) | O_CREAT | O_TRUNC | (excl ? O_EXCL : 0); write = true; read = plus; break;
            case 'a': flags = (plus ? O_RDWR : O_WRONLY) | O_CREAT | O_APPEND; write = true; read = plus; append = true; break;
            default: return false;
        }
        foreach (char c in mode.AsSpan(1)) if (c is not ('+' or 'b' or 'x' or 't')) return false;
        return true;
    }

    uint Open(string path, string mode)
    {
        if (!ParseMode(mode, out int flags, out bool r, out bool w, out bool a)) { SetErrno(EINVAL); return 0; }
        int fd = Io.sceIoOpen(path, flags, 0x1B6);
        if (fd < 0) { SetErrno(IoErrno(fd)); return 0; }
        return NewStream(fd, r, w, a).Addr;
    }

    [Hle] uint fopen(string path, string mode) => Open(path, mode);

    [Hle]
    int fopen_s(uint result, uint path, uint mode)
    {
        if (result == 0) return Constraint("fopen_s: bad argument");
        if (path == 0 || mode == 0) { GuestMemory.W32(result, 0); return Constraint("fopen_s: bad argument"); }
        uint f = Open(GuestMemory.ReadCString(path), GuestMemory.ReadCString(mode));
        GuestMemory.W32(result, f);
        return f != 0 ? 0 : GetErrno();
    }

    [Hle]
    uint fdopen(int fd, string mode)
    {
        if (!ParseMode(mode, out _, out bool r, out bool w, out bool a)) { SetErrno(EINVAL); return 0; }
        return NewStream(fd, r, w, a).Addr;
    }

    [Hle] uint sceLibcFopenWithFD(int fd, string mode) => fdopen(fd, mode);

    /// <summary>sceLibcFopenWithFH(SceFiosFH, mode): a stream over a FIOS2 file handle (the descriptor behind it).</summary>
    [Hle]
    uint sceLibcFopenWithFH(int fh, string mode)
    {
        int fd = Emu.Hle.Modules.OfType<Fios2>().FirstOrDefault()?.FdOfHandle(fh) ?? -1;
        if (fd < 0) { SetErrno(EBADF); return 0; }
        return fdopen(fd, mode);
    }

    [Hle] int sceLibcGetFD(uint file) => Get(file) is { } s ? s.Fd : -1;
    [Hle] int sceLibcGetFH(uint file) => Get(file) is { } s ? Emu.Hle.Modules.OfType<Fios2>().FirstOrDefault()?.HandleOfFd(s.Fd) ?? -1 : -1;

    [Hle] int fileno(uint file) => Get(file) is { } s ? s.Fd : SetErrnoRet(EBADF);

    static int SetErrnoRet(int e) { SetErrno(e); return -1; }

    int Close(Stream s)
    {
        int r = 0;
        if (!s.Std) r = Io.sceIoClose(s.Fd) < 0 ? EOF : 0;
        lock (_streams)
        {
            if (s.Std) return r;
            _streams.Remove(s.Addr);
            _freeFiles.Push(s.Addr);
        }
        return r;
    }

    [Hle] int fclose(uint file) => Get(file) is { } s ? Close(s) : EOF;

    void CloseAllStreams()
    {
        List<Stream> all;
        lock (_streams) all = _streams.Values.Where(s => !s.Std).ToList();
        foreach (var s in all) Close(s);
    }

    [Hle]
    uint freopen(uint path, string mode, uint file)
    {
        var s = Get(file);
        if (s == null) { SetErrno(EBADF); return 0; }
        if (path == 0) { SetErrno(EINVAL); return 0; } // mode change of the same file: not supported by the module either
        if (!ParseMode(mode, out int flags, out bool r, out bool w, out bool a)) { SetErrno(EINVAL); return 0; }
        if (!s.Std) Io.sceIoClose(s.Fd);
        int fd = Io.sceIoOpen(GuestMemory.ReadCString(path), flags, 0x1B6);
        if (fd < 0) { SetErrno(IoErrno(fd)); if (!s.Std) Close(s); return 0; }
        s.Fd = fd; s.CanRead = r; s.CanWrite = w; s.Append = a; s.Eof = s.Error = false; s.Pushback.Clear(); s.Orientation = 0; s.Std = false;
        GuestMemory.W32(s.Addr, (uint)fd);
        return s.Addr;
    }

    [Hle]
    int freopen_s(uint result, uint path, uint mode, uint file)
    {
        if (result == 0 || mode == 0 || file == 0) return Constraint("freopen_s: bad argument");
        uint f = freopen(path, GuestMemory.ReadCString(mode), file);
        GuestMemory.W32(result, f);
        return f != 0 ? 0 : GetErrno();
    }

    // ------------------------------------------------------------------ byte I/O

    uint _ioScratch;
    readonly object _ioLock = new();

    /// <summary>Writes host bytes to a stream; false (error flag set) when the descriptor refuses them.</summary>
    bool WriteStream(Stream s, ReadOnlySpan<byte> data)
    {
        if (!s.CanWrite) { s.Error = true; SetErrno(EBADF); return false; }
        if (data.Length == 0) return true;
        lock (_ioLock)
        {
            const uint chunk = 0x1000;
            if (_ioScratch == 0) _ioScratch = StaticAlloc(chunk);
            if (s.Append && !s.Std) Io.sceIoLseek(s.Fd, 0, 2);
            for (int off = 0; off < data.Length; off += (int)chunk)
            {
                int n = Math.Min((int)chunk, data.Length - off);
                data.Slice(off, n).CopyTo(GuestMemory.Span(_ioScratch, n));
                int r = Io.sceIoWrite(s.Fd, _ioScratch, (uint)n);
                if (r < n) { s.Error = true; if (r < 0) SetErrno(IoErrno(r)); return false; }
            }
        }
        s.Pushback.Clear();
        return true;
    }

    /// <summary>Reads into guest memory, pushed-back bytes first. Sets eof/error as fread does.</summary>
    uint ReadStream(Stream s, uint buf, uint size)
    {
        if (!s.CanRead) { s.Error = true; SetErrno(EBADF); return 0; }
        uint n = 0;
        while (n < size && s.Pushback.Count > 0) GuestMemory.Write(buf + n++, s.Pushback.Pop());
        while (n < size)
        {
            int r = Io.sceIoRead(s.Fd, buf + n, size - n);
            if (r < 0) { s.Error = true; SetErrno(IoErrno(r)); break; }
            if (r == 0) { s.Eof = true; break; }
            n += (uint)r;
        }
        return n;
    }

    int GetByte(Stream s)
    {
        if (s.Pushback.Count > 0) return s.Pushback.Pop();
        if (!s.CanRead) { s.Error = true; return EOF; }
        lock (_ioLock)
        {
            if (_ioScratch == 0) _ioScratch = StaticAlloc(0x1000);
            int r = Io.sceIoRead(s.Fd, _ioScratch, 1);
            if (r < 0) { s.Error = true; SetErrno(IoErrno(r)); return EOF; }
            if (r == 0) { s.Eof = true; return EOF; }
            return B(_ioScratch);
        }
    }

    [Hle]
    uint fread(uint buf, uint size, uint count, uint file)
    {
        var s = Get(file);
        if (s == null || size == 0 || count == 0) return 0;
        ulong total = (ulong)size * count;
        if (total > int.MaxValue) total = int.MaxValue;
        return ReadStream(s, buf, (uint)total) / size;
    }

    [Hle]
    uint fwrite(uint buf, uint size, uint count, uint file)
    {
        var s = Get(file);
        if (s == null || size == 0 || count == 0) return 0;
        ulong total = (ulong)size * count;
        if (total > int.MaxValue) return 0;
        return WriteStream(s, GuestMemory.ReadSpan(buf, (int)total)) ? count : 0;
    }

    [Hle("fgetc")]
    [Hle("getc")]
    int fgetc(uint file) => Get(file) is { } s ? GetByte(s) : EOF;

    [Hle] int getchar() => GetByte(StdinFile());

    [Hle("fputc")]
    [Hle("putc")]
    int fputc(int c, uint file) => Get(file) is { } s && WriteStream(s, [(byte)c]) ? (byte)c : EOF;

    [Hle] int putchar(int c) => WriteStream(StdoutFile(), [(byte)c]) ? (byte)c : EOF;

    [Hle]
    int fputs(uint str, uint file)
    {
        var s = Get(file);
        if (s == null) return EOF;
        return WriteStream(s, GuestMemory.ReadSpan(str, (int)StrLen(str))) ? 0 : EOF;
    }

    [Hle]
    int puts(uint str)
    {
        var s = StdoutFile();
        return WriteStream(s, GuestMemory.ReadSpan(str, (int)StrLen(str))) && WriteStream(s, "\n"u8) ? 0 : EOF;
    }

    [Hle]
    uint fgets(uint buf, int n, uint file)
    {
        var s = Get(file);
        if (s == null || n <= 0) return 0;
        int i = 0;
        while (i < n - 1)
        {
            int c = GetByte(s);
            if (c == EOF) break;
            GuestMemory.Write(buf + (uint)i++, (byte)c);
            if (c == '\n') break;
        }
        if (i == 0 || s.Error) return 0;
        GuestMemory.Write(buf + (uint)i, (byte)0);
        return buf;
    }

    [Hle]
    uint gets(uint buf)
    {
        var s = StdinFile();
        int i = 0, c;
        while ((c = GetByte(s)) != EOF && c != '\n') GuestMemory.Write(buf + (uint)i++, (byte)c);
        if (c == EOF && i == 0) return 0;
        GuestMemory.Write(buf + (uint)i, (byte)0);
        return buf;
    }

    [Hle]
    uint gets_s(uint buf, uint n)
    {
        if (buf == 0 || BadSize(n)) { Constraint("gets_s: bad argument"); return 0; }
        var s = StdinFile();
        uint i = 0;
        int c;
        while ((c = GetByte(s)) != EOF && c != '\n')
        {
            if (i + 1 >= n) { while ((c = GetByte(s)) != EOF && c != '\n') { } GuestMemory.Write(buf, (byte)0); Constraint("gets_s: line too long"); return 0; }
            GuestMemory.Write(buf + i++, (byte)c);
        }
        if (c == EOF && i == 0) { GuestMemory.Write(buf, (byte)0); return 0; }
        GuestMemory.Write(buf + i, (byte)0);
        return buf;
    }

    [Hle]
    int ungetc(int c, uint file)
    {
        var s = Get(file);
        if (s == null || c == EOF) return EOF;
        s.Pushback.Push((byte)c);
        s.Eof = false;
        return (byte)c;
    }

    // ------------------------------------------------------------------ positioning and state

    long Tell(Stream s)
    {
        if (s.Std) { SetErrno(ESPIPE); return -1; }
        long p = Io.sceIoLseek(s.Fd, 0, 1);
        if (p < 0) { SetErrno(IoErrno((int)p)); return -1; }
        return Math.Max(0, p - s.Pushback.Count);
    }

    int Seek(Stream s, long off, int whence)
    {
        if (whence is < 0 or > 2) { SetErrno(EINVAL); return -1; }
        if (s.Std) { SetErrno(ESPIPE); return -1; }
        if (whence == 1) off -= s.Pushback.Count;
        long r = Io.sceIoLseek(s.Fd, off, whence);
        if (r < 0) { SetErrno(IoErrno((int)r)); return -1; }
        s.Pushback.Clear();
        s.Eof = false;
        return 0;
    }

    [Hle] int fseek(uint file, int off, int whence) => Get(file) is { } s ? Seek(s, off, whence) : SetErrnoRet(EBADF);
    [Hle] int ftell(uint file) { if (Get(file) is not { } s) return SetErrnoRet(EBADF); long p = Tell(s); return p > int.MaxValue ? SetErrnoRet(EINVAL) : (int)p; }

    /// <summary>fpos_t: the 64-bit offset then the conversion state word.</summary>
    [Hle]
    int fgetpos(uint file, uint pos)
    {
        if (Get(file) is not { } s) return SetErrnoRet(EBADF);
        long p = Tell(s);
        if (p < 0) return -1;
        GuestMemory.Write(pos, p);
        GuestMemory.W32(pos + 8, 0);
        return 0;
    }

    [Hle] int fsetpos(uint file, uint pos) => Get(file) is { } s ? Seek(s, GuestMemory.Read<long>(pos), 0) : SetErrnoRet(EBADF);

    [Hle] void rewind(uint file) { if (Get(file) is { } s) { Seek(s, 0, 0); s.Error = false; } }
    [Hle] void clearerr(uint file) { if (Get(file) is { } s) s.Eof = s.Error = false; }
    [Hle] int feof(uint file) => Get(file) is { Eof: true } ? 1 : 0;
    [Hle] int ferror(uint file) => Get(file) is { Error: true } ? 1 : 0;

    /// <summary>fflush: streams are unbuffered, so there is nothing pending; NULL flushes all. A read stream's
    /// pushed-back bytes are discarded as the module does on a flush.</summary>
    [Hle]
    int fflush(uint file)
    {
        if (file == 0) return 0;
        if (Get(file) is not { } s) { SetErrno(EBADF); return EOF; }
        if (!s.Std && s.CanWrite) Io.sceIoSyncByFd(s.Fd, 0);
        return 0;
    }

    /// <summary>setvbuf(file, buf, mode, size): mode _IOFBF 0, _IOLBF 1, _IONBF 2; any other mode fails.</summary>
    [Hle] int setvbuf(uint file, uint buf, int mode, uint size) => Get(file) != null && mode is >= 0 and <= 2 ? 0 : -1;
    [Hle] void setbuf(uint file, uint buf) => setvbuf(file, buf, buf == 0 ? 2 : 0, 0x200);

    [Hle]
    int fwide(uint file, int mode)
    {
        if (Get(file) is not { } s) return 0;
        if (s.Orientation == 0 && mode != 0) s.Orientation = mode > 0 ? 1 : -1;
        return s.Orientation;
    }

    [Hle] int remove(string path) { int r = Io.sceIoRemove(path); if (r < 0) { r = Io.sceIoRmdir(path); } return r < 0 ? SetErrnoRet(IoErrno(r)) : 0; }
    [Hle] int rename(string from, string to) { int r = Io.sceIoRename(from, to); return r < 0 ? SetErrnoRet(IoErrno(r)) : 0; }

    // ------------------------------------------------------------------ printf family (narrow)

    sealed class VaList(uint p) : IVaSource
    {
        uint _p = p;
        public uint Next() { uint v = GuestMemory.R32(_p); _p += 4; return v; }
    }

    static VarArgs Va(uint vaList) => new(new VaList(vaList), 0);
    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    int PrintTo(Stream? s, string text)
    {
        if (s == null) { SetErrno(EBADF); return -1; }
        var b = Bytes(text);
        return WriteStream(s, b) ? b.Length : -1;
    }

    [Hle] int printf(string fmt, VarArgs va) => PrintTo(StdoutFile(), CFormat.Format(fmt, va));
    [Hle] int vprintf(string fmt, uint va) => PrintTo(StdoutFile(), CFormat.Format(fmt, Va(va)));
    [Hle] int fprintf(uint file, string fmt, VarArgs va) => PrintTo(Get(file), CFormat.Format(fmt, va));
    [Hle] int vfprintf(uint file, string fmt, uint va) => PrintTo(Get(file), CFormat.Format(fmt, Va(va)));

    [Hle] int printf_s(uint fmt, VarArgs va) => fmt == 0 ? -Constraint("printf_s: bad format") : PrintTo(StdoutFile(), CFormat.Format(GuestMemory.ReadCString(fmt), va));
    [Hle] int vprintf_s(uint fmt, uint va) => fmt == 0 ? -Constraint("vprintf_s: bad format") : PrintTo(StdoutFile(), CFormat.Format(GuestMemory.ReadCString(fmt), Va(va)));
    [Hle] int fprintf_s(uint file, uint fmt, VarArgs va) => fmt == 0 || file == 0 ? -Constraint("fprintf_s: bad argument") : PrintTo(Get(file), CFormat.Format(GuestMemory.ReadCString(fmt), va));
    [Hle] int vfprintf_s(uint file, uint fmt, uint va) => fmt == 0 || file == 0 ? -Constraint("vfprintf_s: bad argument") : PrintTo(Get(file), CFormat.Format(GuestMemory.ReadCString(fmt), Va(va)));

    /// <summary>snprintf semantics: at most n-1 bytes and a NUL; returns the full length.</summary>
    static int ToBuffer(uint buf, uint n, string text)
    {
        var b = Bytes(text);
        if (n > 0 && buf != 0)
        {
            int k = (int)Math.Min((uint)b.Length, n - 1);
            b.AsSpan(0, k).CopyTo(GuestMemory.Span(buf, k));
            GuestMemory.Write(buf + (uint)k, (byte)0);
        }
        return b.Length;
    }

    [Hle] int sprintf(uint buf, string fmt, VarArgs va) => ToBuffer(buf, int.MaxValue, CFormat.Format(fmt, va));
    [Hle] int vsprintf(uint buf, string fmt, uint va) => ToBuffer(buf, int.MaxValue, CFormat.Format(fmt, Va(va)));
    [Hle] int snprintf(uint buf, uint n, string fmt, VarArgs va) => ToBuffer(buf, n, CFormat.Format(fmt, va));
    [Hle] int vsnprintf(uint buf, uint n, string fmt, uint va) => ToBuffer(buf, n, CFormat.Format(fmt, Va(va)));

    /// <summary>sprintf_s: a result that does not fit is a constraint violation (empty buffer, 0 returned).</summary>
    int ToBufferS(string fn, uint buf, uint n, uint fmt, Func<string, string> format, bool truncate)
    {
        if (buf == 0 || BadSize(n) || fmt == 0)
        {
            if (buf != 0 && !BadSize(n)) GuestMemory.Write(buf, (byte)0);
            Constraint($"{fn}: bad argument");
            return -1;
        }
        string text = format(GuestMemory.ReadCString(fmt));
        int len = Encoding.UTF8.GetByteCount(text);
        if (len >= n && !truncate) { GuestMemory.Write(buf, (byte)0); Constraint($"{fn}: buffer too small"); return 0; }
        ToBuffer(buf, n, text);
        return len;
    }

    [Hle] int sprintf_s(uint buf, uint n, uint fmt, VarArgs va) => ToBufferS("sprintf_s", buf, n, fmt, f => CFormat.Format(f, va), false);
    [Hle] int vsprintf_s(uint buf, uint n, uint fmt, uint va) => ToBufferS("vsprintf_s", buf, n, fmt, f => CFormat.Format(f, Va(va)), false);
    [Hle] int snprintf_s(uint buf, uint n, uint fmt, VarArgs va) => ToBufferS("snprintf_s", buf, n, fmt, f => CFormat.Format(f, va), true);
    [Hle] int vsnprintf_s(uint buf, uint n, uint fmt, uint va) => ToBufferS("vsnprintf_s", buf, n, fmt, f => CFormat.Format(f, Va(va)), true);

    // ------------------------------------------------------------------ printf family (wide: UTF-16 format and output)

    static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    int WPrintTo(Stream? s, string text)
    {
        if (s == null) { SetErrno(EBADF); return -1; }
        if (s.Orientation == 0) s.Orientation = 1;
        // a wide stream writes the multibyte form of the characters (C locale: one byte each, UTF-8 beyond)
        return WriteStream(s, Bytes(text)) ? text.Length : -1;
    }

    [Hle] int wprintf(uint fmt, VarArgs va) => WPrintTo(StdoutFile(), CFormat.Format(ReadWide(fmt), va));
    [Hle] int vwprintf(uint fmt, uint va) => WPrintTo(StdoutFile(), CFormat.Format(ReadWide(fmt), Va(va)));
    [Hle] int fwprintf(uint file, uint fmt, VarArgs va) => WPrintTo(Get(file), CFormat.Format(ReadWide(fmt), va));
    [Hle] int vfwprintf(uint file, uint fmt, uint va) => WPrintTo(Get(file), CFormat.Format(ReadWide(fmt), Va(va)));
    [Hle] int wprintf_s(uint fmt, VarArgs va) => fmt == 0 ? -Constraint("wprintf_s: bad format") : wprintf(fmt, va);
    [Hle] int vwprintf_s(uint fmt, uint va) => fmt == 0 ? -Constraint("vwprintf_s: bad format") : vwprintf(fmt, va);
    [Hle] int fwprintf_s(uint file, uint fmt, VarArgs va) => fmt == 0 || file == 0 ? -Constraint("fwprintf_s: bad argument") : fwprintf(file, fmt, va);
    [Hle] int vfwprintf_s(uint file, uint fmt, uint va) => fmt == 0 || file == 0 ? -Constraint("vfwprintf_s: bad argument") : vfwprintf(file, fmt, va);

    /// <summary>swprintf(buf, n, fmt, ...): at most n wide characters including the NUL; -1 when the result does
    /// not fit (C99), the buffer then holding the truncated text.</summary>
    static int ToWideBuffer(uint buf, uint n, string text)
    {
        if (n == 0 || buf == 0) return text.Length == 0 ? 0 : -1;
        int k = (int)Math.Min((uint)text.Length, n - 1);
        for (int i = 0; i < k; i++) GuestMemory.Write(buf + (uint)i * 2, (ushort)text[i]);
        GuestMemory.Write(buf + (uint)k * 2, (ushort)0);
        return text.Length >= n ? -1 : text.Length;
    }

    [Hle] int swprintf(uint buf, uint n, uint fmt, VarArgs va) => ToWideBuffer(buf, n, CFormat.Format(ReadWide(fmt), va));
    [Hle] int vswprintf(uint buf, uint n, uint fmt, uint va) => ToWideBuffer(buf, n, CFormat.Format(ReadWide(fmt), Va(va)));

    int ToWideBufferS(string fn, uint buf, uint n, uint fmt, Func<string, string> format, bool truncate)
    {
        if (buf == 0 || BadSize(n) || fmt == 0)
        {
            if (buf != 0 && !BadSize(n)) GuestMemory.Write(buf, (ushort)0);
            Constraint($"{fn}: bad argument");
            return -1;
        }
        string text = format(ReadWide(fmt));
        if (text.Length >= n && !truncate) { GuestMemory.Write(buf, (ushort)0); Constraint($"{fn}: buffer too small"); return 0; }
        ToWideBuffer(buf, n, text);
        return text.Length;
    }

    [Hle] int swprintf_s(uint buf, uint n, uint fmt, VarArgs va) => ToWideBufferS("swprintf_s", buf, n, fmt, f => CFormat.Format(f, va), false);
    [Hle] int vswprintf_s(uint buf, uint n, uint fmt, uint va) => ToWideBufferS("vswprintf_s", buf, n, fmt, f => CFormat.Format(f, Va(va)), false);
    [Hle] int snwprintf_s(uint buf, uint n, uint fmt, VarArgs va) => ToWideBufferS("snwprintf_s", buf, n, fmt, f => CFormat.Format(f, va), true);
    [Hle] int vsnwprintf_s(uint buf, uint n, uint fmt, uint va) => ToWideBufferS("vsnwprintf_s", buf, n, fmt, f => CFormat.Format(f, Va(va)), true);

    // ------------------------------------------------------------------ wide character I/O

    const int WEOF = 0xFFFF;

    /// <summary>One wide character from a stream (its multibyte form: a UTF-8 sequence, single bytes in the C locale).</summary>
    int GetWide(Stream s)
    {
        int c = GetByte(s);
        if (c == EOF) return WEOF;
        if (c < 0x80) return c;
        int extra = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : 0;
        int v = c & (0x3F >> extra);
        for (int i = 0; i < extra; i++)
        {
            int n = GetByte(s);
            if (n == EOF || (n & 0xC0) != 0x80) { SetErrno(EILSEQ); s.Error = true; return WEOF; }
            v = (v << 6) | (n & 0x3F);
        }
        return v & 0xFFFF;
    }

    bool PutWide(Stream s, int c) => WriteStream(s, Bytes(((char)c).ToString()));

    [Hle("fgetwc")]
    [Hle("getwc")]
    int fgetwc(uint file) => Get(file) is { } s ? GetWide(s) : WEOF;
    [Hle] int getwchar() => GetWide(StdinFile());

    [Hle("fputwc")]
    [Hle("putwc")]
    int fputwc(int c, uint file) => Get(file) is { } s && PutWide(s, c) ? c & 0xFFFF : WEOF;
    [Hle] int putwchar(int c) => PutWide(StdoutFile(), c) ? c & 0xFFFF : WEOF;

    [Hle]
    int fputws(uint str, uint file) => Get(file) is { } s && WriteStream(s, Bytes(ReadWide(str))) ? 0 : EOF;

    [Hle]
    uint fgetws(uint buf, int n, uint file)
    {
        var s = Get(file);
        if (s == null || n <= 0) return 0;
        int i = 0;
        while (i < n - 1)
        {
            int c = GetWide(s);
            if (c == WEOF) break;
            GuestMemory.Write(buf + (uint)i++ * 2, (ushort)c);
            if (c == '\n') break;
        }
        if (i == 0 || s.Error) return 0;
        GuestMemory.Write(buf + (uint)i * 2, (ushort)0);
        return buf;
    }

    [Hle]
    int ungetwc(int c, uint file)
    {
        var s = Get(file);
        if (s == null || c == WEOF) return WEOF;
        var b = Bytes(((char)c).ToString());
        for (int i = b.Length - 1; i >= 0; i--) s.Pushback.Push(b[i]);
        s.Eof = false;
        return c & 0xFFFF;
    }
}
