using System.Collections.Concurrent;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceIofilemgr (and the SceLibKernel I/O wrappers) over the virtual filesystem.</summary>
public sealed class IoFileMgr : HleModule
{
    const int O_RDONLY = 1, O_WRONLY = 2, O_RDWR = 3, O_APPEND = 0x100, O_CREAT = 0x200, O_TRUNC = 0x400, O_EXCL = 0x800;
    const uint S_IFDIR = 0x1000, S_IFREG = 0x2000, S_RWX = 0x01FF;

    sealed class OpenFile
    {
        public required string VitaPath;
        public FileStream? Stream;
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
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
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
    int sceIoClose(int fd)
    {
        if (fd is >= 0x10 and <= 0x12) return 0;
        if (!_files.TryRemove(fd, out var f)) return SceError.IO_EBADF;
        f.Stream?.Dispose();
        return 0;
    }

    [Hle]
    int sceIoRead(int fd, uint buf, uint size)
    {
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        int n;
        lock (f)
        {
            n = f.Stream.Read(GuestMemory.Span(buf, (int)size));
            Interlocked.Add(ref BytesRead, n);
        }
        IoTiming.Read(n);
        return n;
    }

    [Hle]
    int sceIoWrite(int fd, uint buf, uint size)
    {
        if (fd is 0x11 or 0x12)
        {
            Log.Info("[guest stdout] " + System.Text.Encoding.UTF8.GetString(GuestMemory.Span(buf, (int)size)).TrimEnd('\n'));
            return (int)size;
        }
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        lock (f)
        {
            f.Stream.Write(GuestMemory.Span(buf, (int)size));
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
        int n = RandomAccess.Read(f.Stream.SafeFileHandle, GuestMemory.Span(buf, (int)size), offset);
        Interlocked.Add(ref BytesRead, n);
        IoTiming.Read(n);
        return n;
    }

    [Hle]
    int sceIoPwrite(int fd, uint buf, uint size, long offset)
    {
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        RandomAccess.Write(f.Stream.SafeFileHandle, GuestMemory.Span(buf, (int)size), offset);
        return (int)size;
    }

    [Hle("sceIoSync")] int sceIoSync(string dev, int flag) => 0;
    [Hle] int sceIoSyncByFd(int fd, int flag) { if (_files.TryGetValue(fd, out var f)) f.Stream?.Flush(); return 0; }

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

    [Hle] int sceIoChstat(string path, uint stat, int bits) => 0;
    [Hle] int sceIoChstatByFd(int fd, uint stat, int bits) => 0;

    [Hle]
    int sceIoDevctl(string dev, int cmd, uint arg, uint argLen, uint buf, uint bufLen)
    {
        // 0x3001: free space query
        if (cmd == 0x3001 && buf != 0 && bufLen >= 16)
        {
            GuestMemory.Write(buf, 8UL * 1024 * 1024 * 1024);
            GuestMemory.Write(buf + 8, 4UL * 1024 * 1024 * 1024);
        }
        return 0;
    }

    [Hle] int sceIoIoctl(int fd, int cmd, uint arg, uint argLen, uint buf, uint bufLen) => 0;
}
