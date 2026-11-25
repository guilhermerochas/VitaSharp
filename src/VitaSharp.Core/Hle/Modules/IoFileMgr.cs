using System.Collections.Concurrent;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceIofilemgr (and the SceLibKernel I/O wrappers) over the virtual filesystem.</summary>
public sealed partial class IoFileMgr : HleModule
{
    /// <summary>The user-mode sceIo* calls wait in the thread manager for the I/O to complete; the _sceIo* syscalls and
    /// the kernel exports declare a kernel entry.</summary>
    public override HleCost DefaultCost => HleCost.SchedulerWait;

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
        /// <summary>A file on a guest driver's mount: its vnode and file object (0: a host file).</summary>
        public uint GuestVnode, GuestFile;
    }

    sealed class OpenDir
    {
        public required string[] Entries;
        public int Index;
        /// <summary>A directory on a guest driver's mount: its vnode and file object (0: a host folder).</summary>
        public uint GuestVnode, GuestFile;
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

    /// <summary>
    /// The shell loads a title's LiveArea page from its contents (vs0:app/ID/sce_sys/livearea/contents/... for system
    /// apps, ur0:appmeta/ID/livearea/contents/... for installed titles) when it opens that page: the title becomes the
    /// shell's current LiveArea for the diagnostics (<see cref="Kernel.VitaSystem.ShellLiveArea"/>, Gravar tudo's
    /// "where"). Only the path is looked at; the title's name is read from its param.sfo once per change.
    /// </summary>
    void NoteShellLiveArea(string path)
    {
        int i = path.IndexOf("/livearea/contents/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return;
        string head = path[..i];
        if (head.EndsWith("/sce_sys", StringComparison.OrdinalIgnoreCase)) head = head[..^8];
        string id = head[(head.LastIndexOfAny(['/', ':']) + 1)..];
        if (id.Length != 9 || Emu.Vita.ShellLiveArea?.TitleId == id) return;
        string title = Package.InstalledContent.TitleName(p => Emu.Vfs.Resolve(p), id);
        Emu.Vita.NoteShellLiveArea(id, title);
    }

    [Hle("sceIoOpen")]
    [Hle("ksceIoOpen", Cost = HleCost.KernelEntry)]
    internal int sceIoOpen(string path, int flags, int mode)
    {
        // the libc opens tty0:/tty1: as stdin/stdout/stderr; on retail the open succeeds and output is discarded
        if (Io.Vfs.IsTty(path, out int tty))
        {
            int tfd = Interlocked.Increment(ref _nextFd);
            _files[tfd] = new OpenFile { VitaPath = path, Tty = tty };
            return tfd;
        }
        if (ResolveGuestMount(path, out string guestPath) is { } guestMount)
        {
            return GuestOpen(guestMount, path, guestPath, flags, mode);
        }

        // the walk the vfs makes before the open (a hook on ksceVopLookup sees it)
        int walk = HostWalk(ref path);
        if (walk < 0)
        {
            return walk;
        }

        if ((flags & (O_WRONLY | O_CREAT | O_TRUNC | O_APPEND)) != 0 && IsReadOnlyDevice(path))
        {
            return IO_EROFS;
        }

        if (Emu.IsShell) NoteShellLiveArea(path);
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
            if (fm != FileMode.Open && fm != FileMode.Truncate && !Directory.Exists(Path.GetDirectoryName(host)))
            {
                // the Vita creates no directories on open: a file in a missing folder is ENOENT. The Email app
                // relies on it (it writes ux0:email/app/email_param.ini before checking its database; with the folder
                // made here it then found ux0:email without its email_id.dat and reported a database "from another PS
                // Vita"). The device root and its top-level folders (ux0:data, savedata0:sce_sys...) exist on a
                // unit but not always on the host yet: those are still made.
                if (Io.Vfs.Split(path).rel.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 2)
                    return SceError.IO_ENOENT;
                Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            }
            var fs = new FileStream(host, fm, write ? ((flags & O_RDONLY) != 0 ? FileAccess.ReadWrite : FileAccess.Write) : FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess); // per-open buffer: games open files continuously (larger reads bypass it)
            if ((flags & O_APPEND) != 0) fs.Seek(0, SeekOrigin.End);
            int fd = Interlocked.Increment(ref _nextFd);
            _files[fd] = new OpenFile { VitaPath = path, Stream = fs };
            Capture.CaptureHooks.Files?.Opened(Emu, path, host);
            Log.Debug($"sceIoOpen({path}, 0x{flags:X}) = {fd}");
            return fd;
        }
        catch (FileNotFoundException) { Log.Debug($"sceIoOpen({path}): not found"); return SceError.IO_ENOENT; }
        catch (DirectoryNotFoundException) { Log.Debug($"sceIoOpen({path}): not found"); return SceError.IO_ENOENT; }
        catch (IOException) when (fm == FileMode.CreateNew && File.Exists(host)) { return SceError.IO_EEXIST; }
        catch (UnauthorizedAccessException) { return SceError.IO_EACCES; }
        catch (IOException e)
        {
            // a guest path the host cannot represent (e.g. a second "dev:" inside the path, which Windows rejects as
            // invalid syntax) names no file on the Vita either: fail the open instead of crashing the thread
            Log.Debug($"sceIoOpen({path}): host error {e.Message}");
            return SceError.IO_ENOENT;
        }
    }

    [Hle]
    [Hle("sceIoClose2")] // SceLibKernel "v2" wrapper: no header, assumed to take the same argument
    [Hle("ksceIoClose", Cost = HleCost.KernelEntry)]
    internal int sceIoClose(int fd)
    {
        if (fd is >= 0x10 and <= 0x12) return 0;
        if (!_files.TryRemove(fd, out var f)) return SceError.IO_EBADF;
        if (f.GuestFile != 0)
        {
            return GuestClose(f);
        }

        f.Stream?.Dispose();
        return 0;
    }

    [Hle]
    [Hle("sceIoRead2")] // SceLibKernel "v2" wrapper: no header, assumed to take the same arguments
    [Hle("ksceIoRead", Cost = HleCost.KernelEntry)]
    internal int sceIoRead(int fd, uint buf, uint size)
    {
        if (fd is >= 0x10 and <= 0x12) return 0; // no console input: EOF
        if (_files.TryGetValue(fd, out var t) && t.Tty >= 0) return 0;
        if (t is { GuestFile: not 0 })
        {
            return GuestTransfer(t, 0x10, buf, size);
        }

        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        int n;
        long pos;
        // the hitch ledger counts the host read only: the device model's wait below (IoTiming) blocks this thread off
        // its core, as a read on the Vita does, and the other threads go on; counted, a streaming thread's modelled
        // reads were the "cause" of every frame and hid the real stall (a 98 ms shader stall shown as fileIo 134 ms)
        using (FrameLedger.Time(FrameLedger.Cat.FileIo))
        {
            lock (f)
            {
                pos = f.Stream.Position;
                n = f.Stream.Read(GuestMemory.Span(buf, (int)size));
                Capture.CaptureHooks.Files?.Read(Emu, f.VitaPath, f.Stream.Length, pos, GuestMemory.Span(buf, Math.Max(n, 0)));
                DirtyPages.MarkRange(buf, (uint)Math.Max(n, 0)); // again after the data landed: a long read can span a dirty-page sweep
                Interlocked.Add(ref BytesRead, n);
            }
        }
        IoTiming.Read(f, pos, n);
        return n;
    }

    [Hle]
    [Hle("sceIoWrite2")] // SceLibKernel "v2" wrapper: no header, assumed to take the same arguments
    internal int sceIoWrite(int fd, uint buf, uint size)
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
        if (tf is { GuestFile: not 0 })
        {
            return GuestTransfer(tf, 0x14, buf, size);
        }

        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        lock (f)
        {
            f.Stream.Write(GuestMemory.ReadSpan(buf, (int)size));
            return (int)size;
        }
    }

    [Hle]
    [Hle("ksceIoLseek", Cost = HleCost.KernelEntry)]
    internal long sceIoLseek(
int fd, long offset, int whence)
    {
        if (_files.TryGetValue(fd, out var guest) && guest.GuestFile != 0)
        {
            return ksceVopLseekThroughEntry(guest.GuestVnode, guest.GuestFile, (uint)offset, (uint)(offset >> 32), (uint)whence);
        }

        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        lock (f) return f.Stream.Seek(offset, (SeekOrigin)whence);
    }

    [Hle] int sceIoLseek32(int fd, int offset, int whence) => (int)sceIoLseekThroughEntry(fd, offset, whence);

    [Hle]
    internal int sceIoPread(int fd, uint buf, uint size, long offset)
    {
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        int n;
        // the host read only, as in sceIoRead: the device model's wait is not a hitch of the frame
        using (FrameLedger.Time(FrameLedger.Cat.FileIo))
        {
            n = RandomAccess.Read(f.Stream.SafeFileHandle, GuestMemory.Span(buf, (int)size), offset);
            Capture.CaptureHooks.Files?.Read(Emu, f.VitaPath, f.Stream.Length, offset, GuestMemory.Span(buf, Math.Max(n, 0)));
            DirtyPages.MarkRange(buf, (uint)Math.Max(n, 0)); // again after the data landed: a long read can span a dirty-page sweep
            Interlocked.Add(ref BytesRead, n);
        }
        IoTiming.Read(f, offset, n);
        return n;
    }

    [Hle]
    internal int sceIoPwrite(int fd, uint buf, uint size, long offset)
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
    internal int sceIoSyncByFd(int fd, int flag)
    {
        if (!_files.TryGetValue(fd, out var f)) return IsSpecialFd(fd) ? 0 : SceError.IO_EBADF;
        if (f.Stream != null) lock (f) f.Stream.Flush(true);
        return 0;
    }

    /// <summary>stdio fds (0x10-0x12) handed out by sceKernelGetStdin/out/err.</summary>
    static bool IsSpecialFd(int fd) => fd is >= 0x10 and <= 0x12;

    [Hle]
    internal int sceIoRemove(string path)
    {
        if (ResolveGuestMount(path, out string guestPath) is { } guestMount)
        {
            return GuestRemove(guestMount, guestPath, directoryToo: false);
        }

        int walk = HostWalk(ref path);
        if (walk < 0)
        {
            return walk;
        }

        if (IsReadOnlyDevice(path))
        {
            return IO_EROFS;
        }

        var host = HostPath(path, true);
        if (host == null || !File.Exists(host)) return SceError.IO_ENOENT;
        File.Delete(host);
        return 0;
    }

    [Hle]
    internal int sceIoMkdir(string path, int mode)
    {
        if (ResolveGuestMount(path, out string guestPath) is { } guestMount)
        {
            return GuestMkdir(guestMount, guestPath, mode);
        }

        int walk = HostWalk(ref path);
        if (walk < 0)
        {
            return walk;
        }

        if (IsReadOnlyDevice(path))
        {
            return IO_EROFS;
        }

        var host = HostPath(path, true);
        if (host == null) return SceError.IO_ENOENT;
        if (Directory.Exists(host)) return SceError.IO_EEXIST;
        Directory.CreateDirectory(host);
        return 0;
    }

    [Hle]
    internal int sceIoRmdir(string path)
    {
        if (ResolveGuestMount(path, out string guestPath) is { } guestMount)
        {
            return GuestRemove(guestMount, guestPath, directoryToo: true);
        }

        int walk = HostWalk(ref path);
        if (walk < 0)
        {
            return walk;
        }

        if (IsReadOnlyDevice(path))
        {
            return IO_EROFS;
        }

        var host = HostPath(path, true);
        if (host == null || !Directory.Exists(host)) return SceError.IO_ENOENT;
        try { Directory.Delete(host); return 0; }
        catch (IOException) { return SceError.IO_ENOTEMPTY; }
    }

    [Hle]
    internal int sceIoRename(string oldPath, string newPath)
    {
        // iofilemgr walks the old path, then the new one
        int walk = HostWalk(ref oldPath);
        if (walk < 0)
        {
            return walk;
        }

        walk = HostWalk(ref newPath);
        if (walk < 0)
        {
            return walk;
        }

        if (IsReadOnlyDevice(oldPath) || IsReadOnlyDevice(newPath))
        {
            return IO_EROFS;
        }

        var a = HostPath(oldPath, true);
        var b = HostPath(newPath, true);
        if (a == null || b == null) return SceError.IO_ENOENT;
        if (File.Exists(a)) File.Move(a, b, true);
        else if (Directory.Exists(a)) Directory.Move(a, b);
        else return SceError.IO_ENOENT;
        return 0;
    }

    [Hle]
    internal int sceIoDopen(string path)
    {
        if (ResolveGuestMount(path, out string guestPath) is { } guestMount)
        {
            return GuestDopen(guestMount, guestPath);
        }

        int walk = HostWalk(ref path);
        if (walk < 0)
        {
            return walk;
        }

        _fios ??= Emu.Hle.Modules.OfType<Fios2User>().FirstOrDefault();
        string resolved = _fios != null ? _fios.Resolve(path) : path;
        // app0 with an update lists both layers (Vfs.DirectoryEntries)
        if (Emu.Vfs.DirectoryEntries(resolved) is not { } entries)
        {
            if (Emu.Vfs.Resolve(resolved) == null)
            {
                Log.Warn($"IO: unmapped path '{resolved}'");
            }

            return SceError.IO_ENOENT;
        }

        int fd = Interlocked.Increment(ref _nextFd);
        _dirs[fd] = new OpenDir { Entries = entries };
        // the capture lists the folder the path resolves to (with an update, the update's layer)
        if (Emu.Vfs.Resolve(resolved) is { } host && Directory.Exists(host))
        {
            Capture.CaptureHooks.Files?.Listed(Emu, path, host);
        }
        return fd;
    }

    [Hle]
    internal int sceIoDread(int fd, uint dirent)
    {
        if (!_dirs.TryGetValue(fd, out var d)) return SceError.IO_EBADF;
        if (d.GuestFile != 0)
        {
            return ksceVopDreadThroughEntry(d.GuestVnode, d.GuestFile, dirent);
        }

        if (d.Index >= d.Entries.Length) return 0;
        var e = d.Entries[d.Index++];
        GuestMemory.Span(dirent, 352).Clear();
        WriteStat(dirent, e);
        GuestMemory.WriteCString(dirent + 88, Path.GetFileName(e), 256);
        return 1;
    }

    [Hle]
    internal int sceIoDclose(int fd)
    {
        if (!_dirs.TryRemove(fd, out OpenDir? directory))
        {
            return SceError.IO_EBADF;
        }

        return directory.GuestFile != 0 ? GuestDclose(directory) : 0;
    }

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
    internal int sceIoGetstat(string path, uint stat)
    {
        if (ResolveGuestMount(path, out string guestPath) is { } guestMount)
        {
            return GuestGetstat(guestMount, guestPath, stat);
        }

        int walk = HostWalk(ref path);
        if (walk < 0)
        {
            return walk;
        }

        var host = HostPath(path);
        if (host == null || (!File.Exists(host) && !Directory.Exists(host))) return SceError.IO_ENOENT;
        GuestMemory.Span(stat, 88).Clear();
        WriteStat(stat, host);
        Capture.CaptureHooks.Files?.Opened(Emu, path, host);
        return 0;
    }

    [Hle]
    internal int sceIoGetstatByFd(int fd, uint stat)
    {
        if (_files.TryGetValue(fd, out var guest) && guest.GuestFile != 0)
        {
            return ksceVopFgetstatThroughEntry(guest.GuestVnode, guest.GuestFile, stat);
        }

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

    /// <summary>The vsh mount ids of the system partitions a mount with read-write permission makes writable.</summary>
    internal const uint MountIdOs0 = 0x200, MountIdVs0 = 0x300;

    /// <summary>A mount with read-write permission (2): the system partition it names takes writes from then on;
    /// another permission puts the read-only default back.</summary>
    internal const int MountPermissionReadWrite = 2;

    /// <summary>Notes the permission a system partition was mounted with (_vshIoMount, ksceIoMount).</summary>
    internal static void NoteMount(Emulator emu, uint id, int permission)
    {
        if (id is MountIdOs0 or MountIdVs0)
        {
            emu.Vita.WritableMounts[id] = permission == MountPermissionReadWrite;
        }
    }

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

    /// <summary>
    /// Whether <paramref name="path"/> is on a read-only mount: app0 (the application's image), and the system
    /// partitions os0 and vs0 unless mounted again read-write. FW 3.74 iofilemgr answers EROFS (0x8001001E) to an open
    /// for writing, a create, remove, mkdir, rmdir, rename or chstat there (the mount point's read-only flag).
    /// </summary>
    bool IsReadOnlyDevice(string path)
    {
        string device = Io.Vfs.Split(path).device;
        if (!ReadOnlyDevices.Contains(device, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        uint id = device.Equals("vs0", StringComparison.OrdinalIgnoreCase) ? MountIdVs0
            : device.Equals("os0", StringComparison.OrdinalIgnoreCase) ? MountIdOs0 : 0;
        return id == 0 || Emu?.Vita is not { } vita || !vita.WritableMounts.TryGetValue(id, out bool writable) || !writable;
    }

    [Hle]
    internal int sceIoChstat(string path, uint stat, int bits)
    {
        if (stat == 0) return SceError.IO_EINVAL;
        if (IsReadOnlyDevice(path)) return IO_EROFS;
        int walk = HostWalk(ref path);
        if (walk < 0)
        {
            return walk;
        }

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
        // 0x3001: the device information, SceIoDevInfo { u64 capacity; u64 free; u32 cluster size; u32 0 }: the
        // served device's drive, from the one source sceAppMgrGetDevInfo also answers from.
        if (cmd == 0x3001 && buf != 0 && bufLen >= 16)
        {
            DeviceCapacity.WriteDevInfo(buf, bufLen, DeviceRoot(dev));
        }
        return 0;
    }

    /// <summary>The host folder of a device name ("ux0:", "ux0:/" or "ux0"), null when the device is not served.</summary>
    internal string? DeviceRoot(string dev)
    {
        string name = dev.Contains(':') ? dev[..(dev.IndexOf(':') + 1)] : dev + ":";
        return Emu.Vfs.Resolve(name);
    }

    /// <summary>Driver-specific command on an open fd. No command is known for regular files; validate the fd, log
    /// the command once and answer with zeroed output.</summary>
    [Hle]
    int sceIoIoctl(int fd, int cmd, uint arg, uint argLen, uint buf, uint bufLen)
    {
        string path;
        if (_files.TryGetValue(fd, out var f)) path = f.VitaPath;
        else if (IsSpecialFd(fd)) path = "stdio";
        else if (_dirs.ContainsKey(fd)) path = "directory"; // the shell's trophy server sends 0x1802 to the data folder it lists
        else return SceError.IO_EBADF;
        Log.WarnOnce($"ioctl{cmd:X}", $"sceIoIoctl fd=0x{fd:X} cmd=0x{cmd:X} in={argLen} out={bufLen} path={path}");
        if (buf != 0 && (int)bufLen > 0) GuestMemory.Span(buf, (int)bufLen).Clear();
        return 0;
    }

    // ------------------------------------------------------------------ for the other HLE libraries (SceFios2, SceLibc)

    /// <summary>The host path of a guest path (overlays applied), null when unmapped.</summary>
    internal string? HostPathFor(string path, bool write = false) => HostPath(path, write);

    internal int Devctl(string dev, int cmd, uint arg, uint argLen, uint buf, uint bufLen) => sceIoDevctl(dev, cmd, arg, argLen, buf, bufLen);
    internal int Ioctl(int fd, int cmd, uint arg, uint argLen, uint buf, uint bufLen) => sceIoIoctlThroughEntry(fd, cmd, arg, argLen, buf, bufLen);
    internal int Chstat(string path, uint stat, int bits) => sceIoChstat(path, stat, bits);

    /// <summary>Sets the length of an open file (ftruncate); a sceIo error code when it cannot.</summary>
    internal int Truncate(int fd, long length)
    {
        if (!_files.TryGetValue(fd, out var f) || f.Stream == null) return SceError.IO_EBADF;
        if (length < 0) return SceError.IO_EINVAL;
        try { lock (f) f.Stream.SetLength(length); return 0; }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException) { return SceError.IO_EBADF; }
    }

    /// <summary>The length of an open file, or a sceIo error code.</summary>
    internal long Length(int fd) => _files.TryGetValue(fd, out var f) && f.Stream != null ? f.Stream.Length : SceError.IO_EBADF;
}
