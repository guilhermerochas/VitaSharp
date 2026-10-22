using System.Collections.Concurrent;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceIofilemgr (and the SceLibKernel I/O wrappers) over the virtual filesystem.</summary>
public sealed partial class IoFileMgr : HleModule
{
    const int O_RDONLY = 1, O_WRONLY = 2, O_RDWR = 3, O_APPEND = 0x100, O_CREAT = 0x200, O_TRUNC = 0x400, O_EXCL = 0x800;
    const uint S_IFDIR = 0x1000, S_IFREG = 0x2000, S_RWX = 0x01FF;

    sealed class OpenFile
    {
        public required string VitaPath;
        public FileStream? Stream;
        /// <summary>Debug console device index ("ttyN:"), -1 for files.</summary>
        public int Tty = -1;
        /// <summary>I/O priority set with sceIoSetPriority, -1 = inherit the thread/process default.</summary>
        public int Priority = -1;
    }

    sealed class OpenDir
    {
        public required string[] Entries;
        public int Index;
    }

    readonly ConcurrentDictionary<int, OpenFile> _files = new();
    readonly ConcurrentDictionary<int, OpenDir> _dirs = new();
    int _nextFd = 0x100;

    public long BytesRead;

    Fios2User? _fios;

    string? HostPath(string path, bool write = false)
    {
        _fios ??= Emu.Hle.Modules.OfType<Fios2User>().FirstOrDefault();
        if (_fios != null) path = _fios.Resolve(path);
        var r = Emu.Vfs.Resolve(path, write);
        if (r == null) Log.Warn($"IO: unmapped path '{path}'");
        return r;
    }

    [Hle("sceIoOpen")]
    int sceIoOpen(string path, int flags, int mode)
    {
        // the libc opens tty0:/tty1: as stdin/stdout/stderr; on retail the open succeeds and output is discarded
        if (Io.Vfs.IsTty(path, out int tty))
        {
            int tfd = Interlocked.Increment(ref _nextFd);
            _files[tfd] = new OpenFile { VitaPath = path, Tty = tty };
            return tfd;
        }
        var host = HostPath(path, (flags & O_WRONLY) != 0);
        if (host == null) return SceError.IO_ENOENT;
        if (Directory.Exists(host)) return SceError.IO_EISDIR;
        bool write = (flags & O_WRONLY) != 0;
        FileMode fm;
        if ((flags & O_CREAT) != 0)
            fm = (flags & O_EXCL) != 0 ? FileMode.CreateNew : (flags & O_TRUNC) != 0 ? FileMode.Create : FileMode.OpenOrCreate;
        else fm = (flags & O_TRUNC) != 0 ? FileMode.Truncate : FileMode.Open;
        try
        {
            if (fm != FileMode.Open && fm != FileMode.Truncate) Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            var fs = new FileStream(host, fm, write ? ((flags & O_RDONLY) != 0 ? FileAccess.ReadWrite : FileAccess.Write) : FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess); // per-open buffer: games open files continuously (larger reads bypass it)
            if ((flags & O_APPEND) != 0) fs.Seek(0, SeekOrigin.End);
            int fd = Interlocked.Increment(ref _nextFd);
            _files[fd] = new OpenFile { VitaPath = path, Stream = fs };
            Log.Debug($"sceIoOpen({path}, 0x{flags:X}) = {fd}");
            return fd;
        }
        catch (FileNotFoundException) { Log.Debug($"sceIoOpen({path}): not found"); return SceError.IO_ENOENT; }
        catch (DirectoryNotFoundException) { Log.Debug($"sceIoOpen({path}): not found"); return SceError.IO_ENOENT; }
        catch (IOException) when (fm == FileMode.CreateNew) { return SceError.IO_EEXIST; }
        catch (UnauthorizedAccessException) { return SceError.IO_EACCES; }
    }

    [Hle]
    [Hle("sceIoClose2")] // SceLibKernel "v2" wrapper: no header, assumed to take the same argument
    int sceIoClose(int fd)
    {
        if (fd is >= 0x10 and <= 0x12) return 0;
        if (!_files.TryRemove(fd, out var f)) return SceError.IO_EBADF;
        f.Stream?.Dispose();
        return 0;
    }

    [Hle]
    [Hle("sceIoRead2")] // SceLibKernel "v2" wrapper: no header, assumed to take the same arguments
    int sceIoRead(int fd, uint buf, uint size)
    {
        if (fd is >= 0x10 and <= 0x12) return 0; // no console input: EOF
        if (_files.TryGetValue(fd, out var t) && t.Tty >= 0) return 0;
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        int n;
        using var ledger = FrameLedger.Time(FrameLedger.Cat.FileIo);
        lock (f)
        {
            n = f.Stream.Read(GuestMemory.Span(buf, (int)size));
            DirtyPages.MarkRange(buf, (uint)Math.Max(n, 0)); // again after the data landed: a long read can span a dirty-page sweep
            Interlocked.Add(ref BytesRead, n);
        }
        IoTiming.Read(n);
        return n;
    }

    [Hle]
    [Hle("sceIoWrite2")] // SceLibKernel "v2" wrapper: no header, assumed to take the same arguments
    int sceIoWrite(int fd, uint buf, uint size)
    {
        if (fd is 0x11 or 0x12)
        {
            Log.Info("[guest stdout] " + System.Text.Encoding.UTF8.GetString(GuestMemory.ReadSpan(buf, (int)size)).TrimEnd('\n'));
            return (int)size;
        }
        if (_files.TryGetValue(fd, out var tf) && tf.Tty >= 0)
        {
            Log.Info((tf.Tty == 1 ? "[guest stderr] " : "[guest stdout] ") + System.Text.Encoding.UTF8.GetString(GuestMemory.ReadSpan(buf, (int)size)).TrimEnd('\n'));
            return (int)size;
        }
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        lock (f)
        {
            f.Stream.Write(GuestMemory.ReadSpan(buf, (int)size));
            return (int)size;
        }
    }

    [Hle]
    long sceIoLseek(int fd, long offset, int whence)
    {
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        lock (f) return f.Stream.Seek(offset, (SeekOrigin)whence);
    }

    [Hle] int sceIoLseek32(int fd, int offset, int whence) => (int)sceIoLseek(fd, offset, whence);

    [Hle]
    int sceIoPread(int fd, uint buf, uint size, long offset)
    {
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        using var ledger = FrameLedger.Time(FrameLedger.Cat.FileIo);
        int n = RandomAccess.Read(f.Stream.SafeFileHandle, GuestMemory.Span(buf, (int)size), offset);
        DirtyPages.MarkRange(buf, (uint)Math.Max(n, 0)); // again after the data landed: a long read can span a dirty-page sweep
        Interlocked.Add(ref BytesRead, n);
        IoTiming.Read(n);
        return n;
    }

    [Hle]
    int sceIoPwrite(int fd, uint buf, uint size, long offset)
    {
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        RandomAccess.Write(f.Stream.SafeFileHandle, GuestMemory.ReadSpan(buf, (int)size), offset);
        return (int)size;
    }

    /// <summary>Flushes every file open on the device ("savedata0:", "ux0:") to the host disk; flags are ignored.</summary>
    [Hle("sceIoSync")]
    int sceIoSync(uint device, int flag)
    {
        if (device == 0) return SceError.IO_EINVAL;
        return DoSync(GuestMemory.ReadCString(device));
    }

    int DoSync(string device)
    {
        var (dev, _) = Io.Vfs.Split(device);
        if (dev.Length == 0) dev = device.TrimEnd(':');
        foreach (var f in _files.Values)
        {
            if (f.Stream == null || !Io.Vfs.Split(f.VitaPath).device.Equals(dev, StringComparison.OrdinalIgnoreCase)) continue;
            lock (f) { try { f.Stream.Flush(true); } catch (ObjectDisposedException) { } }
        }
        return 0;
    }

    [Hle]
    int sceIoSyncByFd(int fd, int flag)
    {
        if (!_files.TryGetValue(fd, out var f)) return IsSpecialFd(fd) ? 0 : SceError.IO_EBADF;
        if (f.Stream != null) lock (f) f.Stream.Flush(true);
        return 0;
    }

    /// <summary>stdio fds (0x10-0x12) handed out by sceKernelGetStdin/out/err.</summary>
    static bool IsSpecialFd(int fd) => fd is >= 0x10 and <= 0x12;

    [Hle]
    int sceIoRemove(string path)
    {
        var host = HostPath(path, true);
        if (host == null || !File.Exists(host)) return SceError.IO_ENOENT;
        File.Delete(host);
        return 0;
    }

    [Hle]
    int sceIoMkdir(string path, int mode)
    {
        var host = HostPath(path, true);
        if (host == null) return SceError.IO_ENOENT;
        if (Directory.Exists(host)) return SceError.IO_EEXIST;
        Directory.CreateDirectory(host);
        return 0;
    }

    [Hle]
    int sceIoRmdir(string path)
    {
        var host = HostPath(path, true);
        if (host == null || !Directory.Exists(host)) return SceError.IO_ENOENT;
        try { Directory.Delete(host); return 0; }
        catch (IOException) { return SceError.IO_ENOTEMPTY; }
    }

    [Hle]
    int sceIoRename(string oldPath, string newPath)
    {
        var a = HostPath(oldPath, true);
        var b = HostPath(newPath, true);
        if (a == null || b == null) return SceError.IO_ENOENT;
        if (File.Exists(a)) File.Move(a, b, true);
        else if (Directory.Exists(a)) Directory.Move(a, b);
        else return SceError.IO_ENOENT;
        return 0;
    }

    [Hle]
    int sceIoDopen(string path)
    {
        var host = HostPath(path);
        if (host == null || !Directory.Exists(host)) return SceError.IO_ENOENT;
        int fd = Interlocked.Increment(ref _nextFd);
        _dirs[fd] = new OpenDir { Entries = Directory.GetFileSystemEntries(host) };
        return fd;
    }

    [Hle]
    int sceIoDread(int fd, uint dirent)
    {
        if (!_dirs.TryGetValue(fd, out var d)) return SceError.IO_EBADF;
        if (d.Index >= d.Entries.Length) return 0;
        var e = d.Entries[d.Index++];
        GuestMemory.Span(dirent, 352).Clear();
        WriteStat(dirent, e);
        GuestMemory.WriteCString(dirent + 88, Path.GetFileName(e), 256);
        return 1;
    }

    [Hle] int sceIoDclose(int fd) => _dirs.TryRemove(fd, out _) ? 0 : SceError.IO_EBADF;

    static void WriteDateTime(uint p, DateTime t)
    {
        GuestMemory.Write(p, (ushort)t.Year);
        GuestMemory.Write(p + 2, (ushort)t.Month);
        GuestMemory.Write(p + 4, (ushort)t.Day);
        GuestMemory.Write(p + 6, (ushort)t.Hour);
        GuestMemory.Write(p + 8, (ushort)t.Minute);
        GuestMemory.Write(p + 10, (ushort)t.Second);
        GuestMemory.W32(p + 12, (uint)(t.Ticks % TimeSpan.TicksPerSecond / 10));
    }

    static void WriteStat(uint stat, string host)
    {
        bool dir = Directory.Exists(host);
        var info = dir ? (FileSystemInfo)new DirectoryInfo(host) : new FileInfo(host);
        GuestMemory.W32(stat, (dir ? S_IFDIR : S_IFREG) | S_RWX);
        GuestMemory.W32(stat + 4, dir ? 0x10u : 0x20u);
        GuestMemory.Write(stat + 8, dir ? 0L : ((FileInfo)info).Length);
        WriteDateTime(stat + 16, info.CreationTime);
        WriteDateTime(stat + 32, info.LastAccessTime);
        WriteDateTime(stat + 48, info.LastWriteTime);
    }

    [Hle]
    int sceIoGetstat(string path, uint stat)
    {
        var host = HostPath(path);
        if (host == null || (!File.Exists(host) && !Directory.Exists(host))) return SceError.IO_ENOENT;
        GuestMemory.Span(stat, 88).Clear();
        WriteStat(stat, host);
        return 0;
    }

    [Hle]
    int sceIoGetstatByFd(int fd, uint stat)
    {
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        GuestMemory.Span(stat, 88).Clear();
        WriteStat(stat, f.Stream.Name);
        return 0;
    }

    const int SCE_CST_MODE = 0x01, SCE_CST_SIZE = 0x04, SCE_CST_CT = 0x08, SCE_CST_AT = 0x10, SCE_CST_MT = 0x20;
    const uint SCE_S_IWUSR = 0x80; // 0200
    /// <summary>EROFS (newlib errno 30), used for read-only mounts.</summary>
    const int IO_EROFS = unchecked((int)0x8001001E);
    static readonly string[] ReadOnlyDevices = ["app0", "vs0", "os0"];

    /// <summary>Inverse of WriteDateTime (same local-time convention as SceIoStat writes).</summary>
    static DateTime ReadDateTime(uint p)
    {
        int y = GuestMemory.Read<ushort>(p), mo = GuestMemory.Read<ushort>(p + 2), d = GuestMemory.Read<ushort>(p + 4);
        int h = GuestMemory.Read<ushort>(p + 6), mi = GuestMemory.Read<ushort>(p + 8), s = GuestMemory.Read<ushort>(p + 10);
        uint us = GuestMemory.R32(p + 12);
        y = Math.Clamp(y, 1, 9999); mo = Math.Clamp(mo, 1, 12); d = Math.Clamp(d, 1, DateTime.DaysInMonth(y, mo));
        return new DateTime(y, mo, d, Math.Clamp(h, 0, 23), Math.Clamp(mi, 0, 59), Math.Clamp(s, 0, 59), DateTimeKind.Local)
            .AddTicks(Math.Min(us, 999_999u) * 10L);
    }

    /// <summary>Applies the SCE_CST_* bits of a SceIoStat to a host file or directory. Unknown bits are ignored.</summary>
    static int ApplyChstat(string host, uint stat, int bits, FileStream? fs)
    {
        bool dir = Directory.Exists(host);
        if (!dir && !File.Exists(host)) return SceError.IO_ENOENT;
        try
        {
            if ((bits & SCE_CST_MODE) != 0 && !dir)
                new FileInfo(host).IsReadOnly = (GuestMemory.R32(stat) & SCE_S_IWUSR) == 0;
            if ((bits & SCE_CST_SIZE) != 0 && !dir)
            {
                long size = GuestMemory.Read<long>(stat + 8);
                if (size < 0) return SceError.IO_EINVAL;
                if (fs != null) lock (fs) fs.SetLength(size);
                else { using var f = new FileStream(host, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); f.SetLength(size); }
            }
            fs?.Flush(true); // closing the stream later must not overwrite the times set below
            if ((bits & SCE_CST_CT) != 0)
            { var t = ReadDateTime(stat + 16); if (dir) Directory.SetCreationTime(host, t); else File.SetCreationTime(host, t); }
            if ((bits & SCE_CST_AT) != 0)
            { var t = ReadDateTime(stat + 32); if (dir) Directory.SetLastAccessTime(host, t); else File.SetLastAccessTime(host, t); }
            if ((bits & SCE_CST_MT) != 0)
            { var t = ReadDateTime(stat + 48); if (dir) Directory.SetLastWriteTime(host, t); else File.SetLastWriteTime(host, t); }
        }
        catch (UnauthorizedAccessException) { return SceError.IO_EACCES; }
        catch (ArgumentOutOfRangeException) { return SceError.IO_EINVAL; }
        catch (IOException) { return SceError.IO_EACCES; }
        return 0;
    }

    static bool IsReadOnlyDevice(string path) => ReadOnlyDevices.Contains(Io.Vfs.Split(path).device, StringComparer.OrdinalIgnoreCase);

    [Hle]
    int sceIoChstat(string path, uint stat, int bits)
    {
        if (stat == 0) return SceError.IO_EINVAL;
        if (IsReadOnlyDevice(path)) return IO_EROFS;
        var host = HostPath(path, true);
        if (host == null) return SceError.IO_ENOENT;
        return ApplyChstat(host, stat, bits, null);
    }

    [Hle]
    int sceIoChstatByFd(int fd, uint stat, int bits)
    {
        if (!_files.TryGetValue(fd, out var f)) return SceError.IO_EBADF;
        if (f.Stream == null) return 0; // tty: nothing to change
        if (stat == 0) return SceError.IO_EINVAL;
        if (IsReadOnlyDevice(f.VitaPath)) return IO_EROFS;
        return ApplyChstat(f.Stream.Name, stat, bits, f.Stream);
    }

    [Hle]
    int sceIoDevctl(string dev, int cmd, uint arg, uint argLen, uint buf, uint bufLen)
    {
        // 0x3001: free space query, SceIoDevInfo { s64 max_size; s64 free_size; u32 cluster_size; void *unk; }
        if (cmd == 0x3001 && buf != 0 && bufLen >= 16)
        {
            GuestMemory.Write(buf, 8UL * 1024 * 1024 * 1024);
            GuestMemory.Write(buf + 8, 4UL * 1024 * 1024 * 1024);
            if (bufLen >= 20) GuestMemory.W32(buf + 16, 4096);
        }
        return 0;
    }

    /// <summary>Driver-specific command on an open fd. No command is known for regular files; validate the fd, log
    /// the command once and answer with zeroed output.</summary>
    [Hle]
    int sceIoIoctl(int fd, int cmd, uint arg, uint argLen, uint buf, uint bufLen)
    {
        string path;
        if (_files.TryGetValue(fd, out var f)) path = f.VitaPath;
        else if (IsSpecialFd(fd)) path = "stdio";
        else return SceError.IO_EBADF;
        Log.WarnOnce($"ioctl{cmd:X}", $"sceIoIoctl fd=0x{fd:X} cmd=0x{cmd:X} in={argLen} out={bufLen} path={path}");
        if (buf != 0 && (int)bufLen > 0) GuestMemory.Span(buf, (int)bufLen).Clear();
        return 0;
    }
}
