using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceFios2 files, directories and archives.
public sealed partial class Fios2
{
    // ------------------------------------------------------------------ PSARC archives

    /// <summary>A PSARC archive (big-endian header "PSAR": version, compression, TOC length, entry size, file count,
    /// block size, flags; 30-byte entries md5/block index/u40 size/u40 offset; then the block-size table). Entry 0 is the
    /// manifest: the names of entries 1.. separated by newlines.</summary>
    internal sealed class Psarc
    {
        public required string HostPath;
        public required Dictionary<string, (long Size, long Offset, uint Block)> Files;
        public required uint BlockSize;
        public required uint[] BlockSizes;
        public required bool Lzma;
        public uint TocLength, FileCount;

        public static Psarc? Open(string host, out int error)
        {
            error = ERR_BAD_ARCHIVE;
            using var f = File.OpenRead(host);
            var hdr = new byte[32];
            if (f.Read(hdr, 0, 32) != 32 || Encoding.ASCII.GetString(hdr, 0, 4) != "PSAR") return null;
            string comp = Encoding.ASCII.GetString(hdr, 8, 4);
            uint tocLen = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(12)), entrySize = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(16));
            uint count = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(20)), blockSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(24));
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(28));
            if (tocLen < 32 || entrySize < 30 || count == 0 || blockSize == 0 || tocLen > f.Length || (ulong)count * entrySize > tocLen - 32) return null;
            var toc = new byte[tocLen - 32];
            if (f.Read(toc, 0, toc.Length) != toc.Length) return null;
            var entries = new (long Size, long Offset, uint Block)[count];
            for (int i = 0; i < count; i++)
            {
                var e = toc.AsSpan((int)(i * entrySize));
                uint block = BinaryPrimitives.ReadUInt32BigEndian(e[16..]);
                long size = ((long)e[20] << 32) | BinaryPrimitives.ReadUInt32BigEndian(e[21..]);
                long off = ((long)e[25] << 32) | BinaryPrimitives.ReadUInt32BigEndian(e[26..]);
                entries[i] = (size, off, block);
            }
            int width = blockSize <= 0x100 ? 1 : blockSize <= 0x10000 ? 2 : blockSize <= 0x1000000 ? 3 : 4;
            int tableStart = (int)(count * entrySize), n = (toc.Length - tableStart) / width;
            var sizes = new uint[n];
            for (int i = 0; i < n; i++)
            {
                uint v = 0;
                for (int b = 0; b < width; b++) v = (v << 8) | toc[tableStart + i * width + b];
                sizes[i] = v;
            }
            var arc = new Psarc { HostPath = host, Files = new(StringComparer.OrdinalIgnoreCase), BlockSize = blockSize, BlockSizes = sizes, Lzma = comp == "lzma", TocLength = tocLen, FileCount = count };
            if (comp is not ("zlib" or "lzma")) return null;
            byte[]? manifest = arc.Read(entries[0]);
            if (manifest == null) { error = ERR_DECOMPRESSION; return null; }
            var names = Encoding.UTF8.GetString(manifest).Split('\n');
            var cmp = (flags & 1) != 0 ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            arc = new Psarc { HostPath = host, Files = new(cmp), BlockSize = blockSize, BlockSizes = sizes, Lzma = arc.Lzma, TocLength = tocLen, FileCount = count };
            for (int i = 1; i < count && i - 1 < names.Length; i++) arc.Files[names[i - 1].TrimStart('/')] = entries[i];
            error = 0;
            return arc;
        }

        /// <summary>The decompressed bytes of an entry; null when a block cannot be decoded (LZMA is not supported).</summary>
        public byte[]? Read((long Size, long Offset, uint Block) e)
        {
            if (e.Size > int.MaxValue) return null;
            var output = new byte[e.Size];
            using var f = File.OpenRead(HostPath);
            f.Position = e.Offset;
            long done = 0;
            for (uint b = e.Block; done < e.Size; b++)
            {
                if (b >= BlockSizes.Length) return null;
                uint want = (uint)Math.Min(BlockSize, e.Size - done);
                uint stored = BlockSizes[b] == 0 ? BlockSize : BlockSizes[b];
                var raw = new byte[stored];
                if (f.Read(raw, 0, raw.Length) != raw.Length) return null;
                if (stored == want || (stored == BlockSize && BlockSizes[b] == 0)) raw.AsSpan(0, (int)want).CopyTo(output.AsSpan((int)done));
                else
                {
                    if (Lzma) return null;
                    try
                    {
                        using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                        int got = 0;
                        while (got < want) { int r = z.Read(output, (int)done + got, (int)want - got); if (r <= 0) break; got += r; }
                        if (got != want) return null;
                    }
                    catch (InvalidDataException) { return null; }
                }
                done += want;
            }
            return output;
        }
    }

    /// <summary>The archive and entry a path names inside a mounted archive, if any.</summary>
    (Psarc Archive, (long Size, long Offset, uint Block) Entry, bool IsDir)? InArchive(string path)
    {
        string p = Normalize(path);
        lock (_lock)
            foreach (var (mount, arc) in _mounts)
            {
                if (!p.StartsWith(mount, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Length != mount.Length && p[mount.Length] != '/') continue;
                string rel = p[mount.Length..].TrimStart('/');
                if (arc.Files.TryGetValue(rel, out var e)) return (arc, e, false);
                if (rel.Length == 0 || arc.Files.Keys.Any(k => k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase))) return (arc, default, true);
            }
        return null;
    }

    /// <summary>sceFiosArchiveGetMountBufferSize(Sync)(attr, path, openParams): the memory a mount needs here (the
    /// archive's table of contents plus the name index), or a negative error.</summary>
    [Hle]
    long sceFiosArchiveGetMountBufferSizeSync(uint attr, string path, uint openParams)
    {
        var host = Io.HostPathFor(path);
        if (host == null || !File.Exists(host)) return ERR_BAD_PATH;
        try
        {
            var arc = Psarc.Open(host, out int err);
            if (arc == null) return err;
            return (arc.TocLength + arc.FileCount * 16 + 0xFFF) & ~0xFFFL;
        }
        catch (IOException) { return ERR_BAD_ARCHIVE; }
    }

    [Hle]
    int sceFiosArchiveGetMountBufferSize(uint attr, string path, uint openParams)
    {
        long r = sceFiosArchiveGetMountBufferSizeSync(attr, path, openParams);
        return Complete(attr, r < 0 ? (int)r : 0, r < 0 ? 0 : r, path: path);
    }

    /// <summary>sceFiosArchiveMountSync(attr, SceFiosFH *outFH, archivePath, mountPoint, SceFiosBuffer mountBuffer,
    /// openParams): paths under mountPoint then read the archive's files.</summary>
    [Hle]
    int sceFiosArchiveMountSync(uint attr, uint outFh, string archivePath, string mountPoint, uint bufPtr, uint bufLen, uint openParams)
    {
        if (outFh == 0) return ERR_BAD_PTR;
        var host = Io.HostPathFor(archivePath);
        if (host == null || !File.Exists(host)) return ERR_BAD_PATH;
        Psarc? arc;
        int err;
        try { arc = Psarc.Open(host, out err); }
        catch (IOException) { return ERR_BAD_ARCHIVE; }
        if (arc == null) return err;
        long need = (arc.TocLength + arc.FileCount * 16 + 0xFFF) & ~0xFFFL;
        if (bufPtr == 0 || bufLen < need) return ERR_BAD_SIZE;
        int fh;
        lock (_lock)
        {
            fh = NewHandle(TypeFh);
            _files[fh] = new FileH { Path = archivePath, MountedArchive = arc, Read = true, PathCopy = GuestString(archivePath) };
            _mounts.Add((Normalize(mountPoint), arc));
        }
        GuestMemory.W32(outFh, (uint)fh);
        Util.Log.Info($"SceFios2 (HLE): {archivePath} mounted on {mountPoint} ({arc.Files.Count} files)");
        return 0;
    }

    [Hle]
    int sceFiosArchiveMount(uint attr, uint outFh, string archivePath, string mountPoint, uint bufPtr, uint bufLen, uint openParams)
        => Complete(attr, sceFiosArchiveMountSync(attr, outFh, archivePath, mountPoint, bufPtr, bufLen, openParams), path: archivePath);

    [Hle]
    int sceFiosArchiveUnmountSync(uint attr, int fh)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(fh, out var f) || f.MountedArchive == null) return ERR_BAD_FH;
            _mounts.RemoveAll(m => m.Archive == f.MountedArchive);
            _files.Remove(fh);
        }
        return 0;
    }

    [Hle] int sceFiosArchiveUnmount(uint attr, int fh) => Complete(attr, sceFiosArchiveUnmountSync(attr, fh));

    int _decompressorThreads = 1;
    [Hle] int sceFiosArchiveGetDecompressorThreadCount() => _decompressorThreads;
    [Hle] int sceFiosArchiveSetDecompressorThreadCount(int n) { int old = _decompressorThreads; if (n > 0) _decompressorThreads = n; return old; }

    // I/O filters: the dearchiver and cache filters are callbacks the module calls on every request; archives are
    // read here directly, so a filter only takes its slot (index 0..7).
    readonly uint[] _filters = new uint[8];

    [Hle]
    int sceFiosIOFilterAdd(int index, uint callback, uint context)
    {
        if (index is < 0 or >= 8) return ERR_BAD_INDEX;
        lock (_lock) { if (_filters[index] != 0) return ERR_BUSY; _filters[index] = callback; }
        return 0;
    }

    [Hle]
    int sceFiosIOFilterRemove(int index)
    {
        if (index is < 0 or >= 8) return ERR_BAD_INDEX;
        lock (_lock) _filters[index] = 0;
        return 0;
    }

    /// <summary>The filter callbacks themselves: called by the module's I/O path, never by the application;
    /// requests reaching them here are passed on unchanged (0 = not handled by this filter).</summary>
    [Hle] int sceFiosIOFilterPsarcDearchiver(uint context, int ev, uint op) => 0;
    [Hle] int sceFiosIOFilterCache(uint context, int ev, uint op) => 0;

    // ------------------------------------------------------------------ file handles

    FileH? GetFile(int fh) { lock (_lock) return _files.GetValueOrDefault(fh); }

    /// <summary>Opens a file for SceFiosOpenParams.openFlags (READ 1, WRITE 2, APPEND 4, CREAT 8, TRUNC 0x10; no
    /// params = read only).</summary>
    int OpenFile(uint outFh, string path, uint openParams, int nativeMode)
    {
        if (outFh == 0) return ERR_BAD_PTR;
        int r = Open(path, openParams != 0 ? GuestMemory.R32(openParams) & 0xFFFF : O_READ, openParams, nativeMode, out int fh);
        if (r == 0) GuestMemory.W32(outFh, (uint)fh);
        return r;
    }

    int Open(string path, uint flags, uint openParams, int nativeMode, out int fh)
    {
        fh = 0;
        if (Encoding.UTF8.GetByteCount(path) >= PathMax) return ERR_PATH_TOO_LONG;
        if ((flags & (O_READ | O_WRITE)) == 0) flags |= O_READ;
        var f = new FileH { Path = path, Read = (flags & O_READ) != 0, Write = (flags & O_WRITE) != 0, Append = (flags & O_APPEND) != 0 };
        if (InArchive(path) is { } a)
        {
            if (a.IsDir) return ERR_NOT_A_FILE;
            if (f.Write) return ERR_READ_ONLY;
            f.ArchiveData = a.Archive.Read(a.Entry);
            if (f.ArchiveData == null) return ERR_DECOMPRESSION;
        }
        else
        {
            int sflags = (f.Read && f.Write ? 3 : f.Write ? 2 : 1) | ((flags & O_APPEND) != 0 ? 0x100 : 0) | ((flags & O_CREAT) != 0 ? 0x200 : 0) | ((flags & O_TRUNC) != 0 ? 0x400 : 0);
            int fd = Io.sceIoOpen(path, sflags, nativeMode);
            if (fd < 0) return FromIo(fd);
            f.Fd = fd;
        }
        f.PathCopy = GuestString(path);
        f.OpenParams = LibcM.StaticAlloc(16);
        if (openParams != 0) GuestMemory.Copy(f.OpenParams, openParams, 16);
        else GuestMemory.W32(f.OpenParams, flags);
        lock (_lock) { fh = NewHandle(TypeFh); _files[fh] = f; }
        return 0;
    }

    [Hle] int sceFiosFHOpenSync(uint attr, uint outFh, string path, uint openParams) => OpenFile(outFh, path, openParams, 0x1B6);
    [Hle] int sceFiosFHOpenWithModeSync(uint attr, uint outFh, string path, uint openParams, int mode) => OpenFile(outFh, path, openParams, mode);
    [Hle] int sceFiosFHOpen(uint attr, uint outFh, string path, uint openParams) => Complete(attr, OpenFile(outFh, path, openParams, 0x1B6), path: path);
    [Hle] int sceFiosFHOpenWithMode(uint attr, uint outFh, string path, uint openParams, int mode) => Complete(attr, OpenFile(outFh, path, openParams, mode), path: path);

    int CloseFile(int fh)
    {
        FileH? f;
        lock (_lock) { if (!_files.Remove(fh, out f)) return ERR_BAD_FH; }
        if (f.MountedArchive != null) lock (_lock) _mounts.RemoveAll(m => m.Archive == f.MountedArchive);
        if (f.Fd >= 0) Io.sceIoClose(f.Fd);
        return 0;
    }

    [Hle] int sceFiosFHCloseSync(uint attr, int fh) => CloseFile(fh);
    [Hle] int sceFiosFHClose(uint attr, int fh) => Complete(attr, CloseFile(fh));

    /// <summary>Reads at an offset (the position is not touched); 0 bytes past the end is SCE_FIOS_ERROR_EOF.</summary>
    long ReadAt(FileH f, uint buf, long len, long offset)
    {
        if (!f.Read) return ERR_WRITE_ONLY;
        if (len < 0) return ERR_BAD_SIZE;
        if (offset < 0) return ERR_BAD_OFFSET;
        if (len > 0 && buf == 0) return ERR_BAD_PTR;
        if (len == 0) return 0;
        if (f.ArchiveData != null)
        {
            if (offset >= f.ArchiveData.Length) return ERR_EOF;
            int n = (int)Math.Min(len, f.ArchiveData.Length - offset);
            f.ArchiveData.AsSpan((int)offset, n).CopyTo(GuestMemory.Span(buf, n));
            return n;
        }
        long total = 0;
        while (total < len)
        {
            uint chunk = (uint)Math.Min(len - total, 0x40000000);
            int r = Io.sceIoPread(f.Fd, buf + (uint)total, chunk, offset + total);
            if (r < 0) return total > 0 ? total : FromIo(r);
            if (r == 0) break;
            total += r;
        }
        return total == 0 ? ERR_EOF : total;
    }

    long WriteAt(FileH f, uint buf, long len, long offset)
    {
        if (!f.Write) return ERR_READ_ONLY;
        if (len < 0) return ERR_BAD_SIZE;
        if (offset < 0) return ERR_BAD_OFFSET;
        if (len > 0 && buf == 0) return ERR_BAD_PTR;
        if (len == 0) return 0;
        if (f.Append) offset = Io.Length(f.Fd);
        long total = 0;
        while (total < len)
        {
            uint chunk = (uint)Math.Min(len - total, 0x40000000);
            int r = Io.sceIoPwrite(f.Fd, buf + (uint)total, chunk, offset + total);
            if (r < 0) return total > 0 ? total : FromIo(r);
            if (r == 0) break;
            total += r;
        }
        return total;
    }

    long FileSize(FileH f) => f.ArchiveData?.Length ?? (f.MountedArchive != null ? new FileInfo(f.MountedArchive.HostPath).Length : Io.Length(f.Fd));

    long Read(int fh, uint buf, long len)
    {
        var f = GetFile(fh);
        if (f == null) return ERR_BAD_FH;
        long r = ReadAt(f, buf, len, f.Pos);
        if (r > 0) f.Pos += r;
        return r;
    }

    long Write(int fh, uint buf, long len)
    {
        var f = GetFile(fh);
        if (f == null) return ERR_BAD_FH;
        long r = WriteAt(f, buf, len, f.Pos);
        if (r > 0) f.Pos = f.Append ? FileSize(f) : f.Pos + r;
        return r;
    }

    long PRead(int fh, uint buf, long len, long off) => GetFile(fh) is { } f ? ReadAt(f, buf, len, off) : ERR_BAD_FH;
    long PWrite(int fh, uint buf, long len, long off) => GetFile(fh) is { } f ? WriteAt(f, buf, len, off) : ERR_BAD_FH;

    int AsyncIo(uint attr, long r, long len, uint buf, long off) => Complete(attr, r < 0 ? (int)r : 0, r < 0 ? 0 : r, len, buf, off);

    [Hle] long sceFiosFHReadSync(uint attr, int fh, uint buf, long len) => Read(fh, buf, len);
    [Hle] long sceFiosFHWriteSync(uint attr, int fh, uint buf, long len) => Write(fh, buf, len);
    [Hle] long sceFiosFHPreadSync(uint attr, int fh, uint buf, long len, long off) => PRead(fh, buf, len, off);
    [Hle] long sceFiosFHPwriteSync(uint attr, int fh, uint buf, long len, long off) => PWrite(fh, buf, len, off);
    [Hle] int sceFiosFHRead(uint attr, int fh, uint buf, long len) { long p = GetFile(fh)?.Pos ?? 0; return AsyncIo(attr, Read(fh, buf, len), len, buf, p); }
    [Hle] int sceFiosFHWrite(uint attr, int fh, uint buf, long len) { long p = GetFile(fh)?.Pos ?? 0; return AsyncIo(attr, Write(fh, buf, len), len, buf, p); }
    [Hle] int sceFiosFHPread(uint attr, int fh, uint buf, long len, long off) => AsyncIo(attr, PRead(fh, buf, len, off), len, buf, off);
    [Hle] int sceFiosFHPwrite(uint attr, int fh, uint buf, long len, long off) => AsyncIo(attr, PWrite(fh, buf, len, off), len, buf, off);

    /// <summary>Scatter/gather over SceFiosIOV { void *ptr; size_t length; } entries (1..1024 of them).</summary>
    long Vector(int fh, uint iov, int count, long? offset, bool write)
    {
        if (count is <= 0 or > 1024) return ERR_BAD_IOVCNT;
        if (iov == 0) return ERR_BAD_PTR;
        var f = GetFile(fh);
        if (f == null) return ERR_BAD_FH;
        long pos = offset ?? f.Pos, total = 0;
        for (int i = 0; i < count; i++)
        {
            uint p = GuestMemory.R32(iov + (uint)i * 8), n = GuestMemory.R32(iov + (uint)i * 8 + 4);
            long r = write ? WriteAt(f, p, n, pos + total) : ReadAt(f, p, n, pos + total);
            if (r == ERR_EOF) break;
            if (r < 0) return total > 0 ? total : r;
            total += r;
            if (r < n) break;
        }
        if (offset == null) f.Pos += total;
        return total == 0 && !write ? ERR_EOF : total;
    }

    [Hle] long sceFiosFHReadvSync(uint attr, int fh, uint iov, int count) => Vector(fh, iov, count, null, false);
    [Hle] long sceFiosFHWritevSync(uint attr, int fh, uint iov, int count) => Vector(fh, iov, count, null, true);
    [Hle] long sceFiosFHPreadvSync(uint attr, int fh, uint iov, int count, long off) => Vector(fh, iov, count, off, false);
    [Hle] long sceFiosFHPwritevSync(uint attr, int fh, uint iov, int count, long off) => Vector(fh, iov, count, off, true);
    [Hle] int sceFiosFHReadv(uint attr, int fh, uint iov, int count) => AsyncIo(attr, Vector(fh, iov, count, null, false), 0, iov, 0);
    [Hle] int sceFiosFHWritev(uint attr, int fh, uint iov, int count) => AsyncIo(attr, Vector(fh, iov, count, null, true), 0, iov, 0);
    [Hle] int sceFiosFHPreadv(uint attr, int fh, uint iov, int count, long off) => AsyncIo(attr, Vector(fh, iov, count, off, false), 0, iov, off);
    [Hle] int sceFiosFHPwritev(uint attr, int fh, uint iov, int count, long off) => AsyncIo(attr, Vector(fh, iov, count, off, true), 0, iov, off);

    /// <summary>sceFiosFHSeek(fh, offset, whence): SEEK_SET 0, CUR 1, END 2; returns the new position or an error.</summary>
    [Hle]
    long sceFiosFHSeek(int fh, long off, int whence)
    {
        var f = GetFile(fh);
        if (f == null) return ERR_BAD_FH;
        long b = whence switch { 0 => 0, 1 => f.Pos, 2 => FileSize(f), _ => -1 };
        if (b < 0 || b + off < 0) return ERR_BAD_OFFSET;
        return f.Pos = b + off;
    }

    [Hle] long sceFiosFHTell(int fh) => GetFile(fh)?.Pos ?? ERR_BAD_FH;
    [Hle] long sceFiosFHGetSize(int fh) => GetFile(fh) is { } f ? FileSize(f) : ERR_BAD_FH;
    [Hle] uint sceFiosFHGetPath(int fh) => GetFile(fh)?.PathCopy ?? 0;
    [Hle] uint sceFiosFHGetOpenParams(int fh) => GetFile(fh)?.OpenParams ?? 0;
    [Hle] int sceFiosFHToFileno(int fh) => GetFile(fh)?.Fd ?? -1;

    /// <summary>sceFiosFilenoToFH(fd): a handle over an existing descriptor (closing it closes the descriptor).</summary>
    [Hle]
    int sceFiosFilenoToFH(int fd)
    {
        if (HandleOfFd(fd) is var h and >= 0) return h;
        if (Io.Length(fd) < 0) return ERR_BAD_FH;
        lock (_lock)
        {
            int fh = NewHandle(TypeFh);
            _files[fh] = new FileH { Fd = fd, Path = $"fd:{fd}", Read = true, Write = true, PathCopy = GuestString($"fd:{fd}") };
            return fh;
        }
    }

    /// <summary>A pass-through handle wraps a descriptor the application opened itself: forget it without closing.</summary>
    [Hle]
    void sceFiosDeallocatePassthruFH(int fh) { lock (_lock) _files.Remove(fh); }

    [Hle]
    int sceFiosFHStatSync(uint attr, int fh, uint stat)
    {
        var f = GetFile(fh);
        if (f == null) return ERR_BAD_FH;
        if (stat == 0) return ERR_BAD_PTR;
        if (f.ArchiveData != null) { WriteStat(stat, f.ArchiveData.Length, false, false, DateTime.UtcNow); return 0; }
        return StatPath(f.MountedArchive?.HostPath != null ? f.Path : f.Path, stat);
    }

    [Hle] int sceFiosFHStat(uint attr, int fh, uint stat) => Complete(attr, sceFiosFHStatSync(attr, fh, stat));

    [Hle]
    int sceFiosFHTruncateSync(uint attr, int fh, long len)
    {
        var f = GetFile(fh);
        if (f == null) return ERR_BAD_FH;
        if (!f.Write) return ERR_READ_ONLY;
        return FromIo(Io.Truncate(f.Fd, len));
    }

    [Hle] int sceFiosFHTruncate(uint attr, int fh, long len) => Complete(attr, sceFiosFHTruncateSync(attr, fh, len));
    [Hle] int sceFiosFHSyncSync(uint attr, int fh) => GetFile(fh) is { } f ? (f.Fd >= 0 ? FromIo(Math.Min(0, Io.sceIoSyncByFd(f.Fd, 0))) : 0) : ERR_BAD_FH;
    [Hle] int sceFiosFHSync(uint attr, int fh) => Complete(attr, sceFiosFHSyncSync(attr, fh));
    [Hle] int sceFiosSyncSync(uint attr, string path, int flag) => 0;
    [Hle] int sceFiosSync(uint attr, string path, int flag) => Complete(attr, 0, path: path);

    [Hle]
    int sceFiosFHIoctlSync(uint attr, int fh, int cmd, uint arg, uint argLen, uint buf, uint bufLen)
        => GetFile(fh) is { Fd: >= 0 } f ? FromIo(Io.Ioctl(f.Fd, cmd, arg, argLen, buf, bufLen)) : ERR_BAD_FH;

    [Hle] int sceFiosFHIoctl(uint attr, int fh, int cmd, uint arg, uint argLen, uint buf, uint bufLen) => Complete(attr, sceFiosFHIoctlSync(attr, fh, cmd, arg, argLen, buf, bufLen));
    [Hle] int sceFiosDevctlSync(uint attr, string dev, int cmd, uint arg, uint argLen, uint buf, uint bufLen) => FromIo(Io.Devctl(dev, cmd, arg, argLen, buf, bufLen));
    [Hle] int sceFiosDevctl(uint attr, string dev, int cmd, uint arg, uint argLen, uint buf, uint bufLen) => Complete(attr, sceFiosDevctlSync(attr, dev, cmd, arg, argLen, buf, bufLen));

    // ------------------------------------------------------------------ paths: stat, exists, delete, rename, size

    /// <summary>SceFiosStat: i64 size, access/modification/creation SceFiosDate, u32 statFlags, u32 reserved, i64
    /// uid, gid, dev, ino, mode.</summary>
    static void WriteStat(uint stat, long size, bool dir, bool writable, DateTime mtime, DateTime? atime = null, DateTime? ctime = null)
    {
        GuestMemory.Span(stat, 0x58).Clear();
        GuestMemory.Write(stat, size);
        GuestMemory.Write(stat + 8, DateOf(atime ?? mtime));
        GuestMemory.Write(stat + 16, DateOf(mtime));
        GuestMemory.Write(stat + 24, DateOf(ctime ?? mtime));
        GuestMemory.W32(stat + 32, (dir ? STATUS_DIRECTORY : 0) | STATUS_READABLE | (writable ? STATUS_WRITABLE : 0));
        GuestMemory.Write(stat + 80, (long)((dir ? 0x1000 : 0x2000) | (writable ? 0x1B6 : 0x124)));
    }

    int StatPath(string path, uint stat)
    {
        if (InArchive(path) is { } a)
        {
            WriteStat(stat, a.IsDir ? 0 : a.Entry.Size, a.IsDir, false, File.GetLastWriteTimeUtc(a.Archive.HostPath));
            return 0;
        }
        var host = Io.HostPathFor(path);
        if (host == null) return ERR_BAD_PATH;
        if (Directory.Exists(host)) { var d = new DirectoryInfo(host); WriteStat(stat, 0, true, true, d.LastWriteTimeUtc, d.LastAccessTimeUtc, d.CreationTimeUtc); return 0; }
        if (File.Exists(host)) { var fi = new FileInfo(host); WriteStat(stat, fi.Length, false, !fi.IsReadOnly, fi.LastWriteTimeUtc, fi.LastAccessTimeUtc, fi.CreationTimeUtc); return 0; }
        return ERR_BAD_PATH;
    }

    [Hle] int sceFiosStatSync(uint attr, string path, uint stat) => stat == 0 ? ERR_BAD_PTR : StatPath(path, stat);
    [Hle] int sceFiosStat(uint attr, string path, uint stat) => Complete(attr, sceFiosStatSync(attr, path, stat), path: path);

    /// <summary>(kind) 0 any, 1 file, 2 directory: whether the path names one.</summary>
    bool Exists(string path, int kind)
    {
        if (InArchive(path) is { } a) return kind == 0 || (kind == 2) == a.IsDir;
        var host = Io.HostPathFor(path);
        if (host == null) return false;
        return kind switch { 1 => File.Exists(host), 2 => Directory.Exists(host), _ => File.Exists(host) || Directory.Exists(host) };
    }

    int ExistsTo(string path, uint outExists, int kind)
    {
        if (outExists == 0) return ERR_BAD_PTR;
        GuestMemory.Write(outExists, Exists(path, kind) ? (byte)1 : (byte)0);
        return 0;
    }

    [Hle] int sceFiosExistsSync(uint attr, string path, uint outExists) => ExistsTo(path, outExists, 0);
    [Hle] int sceFiosFileExistsSync(uint attr, string path, uint outExists) => ExistsTo(path, outExists, 1);
    [Hle] int sceFiosDirectoryExistsSync(uint attr, string path, uint outExists) => ExistsTo(path, outExists, 2);
    [Hle] int sceFiosExists(uint attr, string path, uint outExists) => Complete(attr, ExistsTo(path, outExists, 0), path: path);
    [Hle] int sceFiosFileExists(uint attr, string path, uint outExists) => Complete(attr, ExistsTo(path, outExists, 1), path: path);
    [Hle] int sceFiosDirectoryExists(uint attr, string path, uint outExists) => Complete(attr, ExistsTo(path, outExists, 2), path: path);

    int Delete(string path, int kind)
    {
        if (InArchive(path) != null) return ERR_READ_ONLY;
        var host = Io.HostPathFor(path);
        if (host == null) return ERR_BAD_PATH;
        bool dir = Directory.Exists(host);
        if (!dir && !File.Exists(host)) return ERR_BAD_PATH;
        if (kind == 1 && dir) return ERR_NOT_A_FILE;
        if (kind == 2 && !dir) return ERR_NOT_A_DIRECTORY;
        return FromIo(Math.Min(0, dir ? Io.sceIoRmdir(path) : Io.sceIoRemove(path)));
    }

    [Hle] int sceFiosDeleteSync(uint attr, string path) => Delete(path, 0);
    [Hle] int sceFiosFileDeleteSync(uint attr, string path) => Delete(path, 1);
    [Hle] int sceFiosDirectoryDeleteSync(uint attr, string path) => Delete(path, 2);
    [Hle] int sceFiosDelete(uint attr, string path) => Complete(attr, Delete(path, 0), path: path);
    [Hle] int sceFiosFileDelete(uint attr, string path) => Complete(attr, Delete(path, 1), path: path);
    [Hle] int sceFiosDirectoryDelete(uint attr, string path) => Complete(attr, Delete(path, 2), path: path);

    [Hle] int sceFiosDirectoryCreateSync(uint attr, string path) => FromIo(Math.Min(0, Io.sceIoMkdir(path, 0x1FF)));
    [Hle] int sceFiosDirectoryCreateWithModeSync(uint attr, string path, int mode) => FromIo(Math.Min(0, Io.sceIoMkdir(path, mode)));
    [Hle] int sceFiosDirectoryCreate(uint attr, string path) => Complete(attr, sceFiosDirectoryCreateSync(attr, path), path: path);
    [Hle] int sceFiosDirectoryCreateWithMode(uint attr, string path, int mode) => Complete(attr, sceFiosDirectoryCreateWithModeSync(attr, path, mode), path: path);

    [Hle] int sceFiosRenameSync(uint attr, string from, string to) => InArchive(from) != null ? ERR_READ_ONLY : FromIo(Math.Min(0, Io.sceIoRename(from, to)));
    [Hle] int sceFiosRename(uint attr, string from, string to) => Complete(attr, sceFiosRenameSync(attr, from, to), path: from);

    [Hle]
    long sceFiosFileGetSizeSync(uint attr, string path)
    {
        if (InArchive(path) is { } a) return a.IsDir ? ERR_NOT_A_FILE : a.Entry.Size;
        var host = Io.HostPathFor(path);
        if (host == null) return ERR_BAD_PATH;
        if (Directory.Exists(host)) return ERR_NOT_A_FILE;
        return File.Exists(host) ? new FileInfo(host).Length : ERR_BAD_PATH;
    }

    /// <summary>sceFiosFileGetSize(attr, path, SceFiosSize *outSize): async form with the size stored.</summary>
    [Hle]
    int sceFiosFileGetSize(uint attr, string path, uint outSize)
    {
        long r = sceFiosFileGetSizeSync(attr, path);
        if (r >= 0 && outSize != 0) GuestMemory.Write(outSize, r);
        return Complete(attr, r < 0 ? (int)r : 0, r < 0 ? 0 : r, path: path);
    }

    /// <summary>Whole-file helpers: open, transfer at an offset, close.</summary>
    long FileTransfer(string path, uint buf, long len, long off, bool write)
    {
        int r = Open(path, write ? O_WRITE | O_CREAT : O_READ, 0, 0x1B6, out int fh);
        if (r < 0) return r;
        long n = write ? PWrite(fh, buf, len, off) : PRead(fh, buf, len, off);
        CloseFile(fh);
        return n;
    }

    [Hle] long sceFiosFileReadSync(uint attr, string path, uint buf, long len, long off) => FileTransfer(path, buf, len, off, false);
    [Hle] long sceFiosFileWriteSync(uint attr, string path, uint buf, long len, long off) => FileTransfer(path, buf, len, off, true);
    [Hle] int sceFiosFileRead(uint attr, string path, uint buf, long len, long off) => AsyncIo(attr, FileTransfer(path, buf, len, off, false), len, buf, off);
    [Hle] int sceFiosFileWrite(uint attr, string path, uint buf, long len, long off) => AsyncIo(attr, FileTransfer(path, buf, len, off, true), len, buf, off);

    [Hle]
    int sceFiosFileTruncateSync(uint attr, string path, long len)
    {
        if (InArchive(path) != null) return ERR_READ_ONLY;
        int fd = Io.sceIoOpen(path, 2, 0);
        if (fd < 0) return FromIo(fd);
        int r = Io.Truncate(fd, len);
        Io.sceIoClose(fd);
        return FromIo(r);
    }

    [Hle] int sceFiosFileTruncate(uint attr, string path, long len) => Complete(attr, sceFiosFileTruncateSync(attr, path, len), path: path);

    // ------------------------------------------------------------------ cache (no FIOS cache layer here)

    /// <summary>The prefetch/flush calls of the cache filter: there is no cache, so a prefetch completes at once
    /// (the data is read from the medium when asked for) and nothing is ever resident.</summary>
    [Hle] int sceFiosCacheContainsFileSync(uint attr, string path) => 0;
    [Hle] int sceFiosCacheContainsFileRangeSync(uint attr, string path, long off, long len) => 0;
    [Hle] int sceFiosCacheFlushSync(uint attr) => 0;
    [Hle] int sceFiosCacheFlushFileSync(uint attr, string path) => 0;
    [Hle] int sceFiosCacheFlushFileRangeSync(uint attr, string path, long off, long len) => 0;
    [Hle] int sceFiosCachePrefetchFHSync(uint attr, int fh) => GetFile(fh) != null ? 0 : ERR_BAD_FH;
    [Hle] int sceFiosCachePrefetchFHRangeSync(uint attr, int fh, long off, long len) => GetFile(fh) != null ? 0 : ERR_BAD_FH;
    [Hle] int sceFiosCachePrefetchFH(uint attr, int fh) => Complete(attr, sceFiosCachePrefetchFHSync(attr, fh));
    [Hle] int sceFiosCachePrefetchFHRange(uint attr, int fh, long off, long len) => Complete(attr, sceFiosCachePrefetchFHRangeSync(attr, fh, off, len));
    [Hle] int sceFiosCachePrefetchFile(uint attr, string path) => Complete(attr, Exists(path, 1) ? 0 : ERR_BAD_PATH, path: path);
    [Hle] int sceFiosCachePrefetchFileRange(uint attr, string path, long off, long len) => Complete(attr, Exists(path, 1) ? 0 : ERR_BAD_PATH, path: path);

    // ------------------------------------------------------------------ directory handles

    /// <summary>sceFiosDHOpenSync(attr, SceFiosDH *out, path, SceFiosBuffer buf): the listing is taken at open.</summary>
    [Hle]
    int sceFiosDHOpenSync(uint attr, uint outDh, string path, uint bufPtr, uint bufLen)
    {
        if (outDh == 0) return ERR_BAD_PTR;
        var list = new List<(string, bool, long)>();
        if (InArchive(path) is { } a)
        {
            if (!a.IsDir) return ERR_NOT_A_DIRECTORY;
            string p = Normalize(path);
            string mount;
            lock (_lock) mount = _mounts.First(m => m.Archive == a.Archive).MountPoint;
            string rel = p[mount.Length..].Trim('/');
            string prefix = rel.Length == 0 ? "" : rel + "/";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, e) in a.Archive.Files)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = name[prefix.Length..];
                int slash = rest.IndexOf('/');
                string child = slash < 0 ? rest : rest[..slash];
                if (child.Length == 0 || !seen.Add(child)) continue;
                list.Add((child, slash >= 0, slash < 0 ? e.Size : 0));
            }
        }
        else
        {
            var host = Io.HostPathFor(path);
            if (host == null || !Directory.Exists(host)) return host != null && File.Exists(host) ? ERR_NOT_A_DIRECTORY : ERR_BAD_PATH;
            foreach (var e in new DirectoryInfo(host).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                list.Add((e.Name, e is DirectoryInfo, e is FileInfo fi ? fi.Length : 0));
        }
        int dh;
        lock (_lock) { dh = NewHandle(TypeDh); _dirs[dh] = new DirH { Path = Normalize(path), Entries = list, PathCopy = GuestString(path) }; }
        GuestMemory.W32(outDh, (uint)dh);
        return 0;
    }

    [Hle] int sceFiosDHOpen(uint attr, uint outDh, string path, uint bufPtr, uint bufLen) => Complete(attr, sceFiosDHOpenSync(attr, outDh, path, bufPtr, bufLen), path: path);

    /// <summary>SceFiosDirEntry: i64 fileSize, u32 statFlags, u16 nameLength, u16 fullPathLength, u16 offsetToName,
    /// u16 reserved[3], char fullPath[1024]. Past the last entry: SCE_FIOS_ERROR_EOF.</summary>
    [Hle]
    int sceFiosDHReadSync(uint attr, int dh, uint entry)
    {
        DirH? d;
        lock (_lock) d = _dirs.GetValueOrDefault(dh);
        if (d == null) return ERR_BAD_DH;
        if (entry == 0) return ERR_BAD_PTR;
        if (d.Index >= d.Entries.Count) return ERR_EOF;
        var (name, dir, size) = d.Entries[d.Index++];
        string full = d.Path + "/" + name;
        GuestMemory.Span(entry, 0x18).Clear();
        GuestMemory.Write(entry, size);
        GuestMemory.W32(entry + 8, (dir ? STATUS_DIRECTORY : 0) | STATUS_READABLE | STATUS_WRITABLE);
        GuestMemory.Write(entry + 12, (ushort)Encoding.UTF8.GetByteCount(name));
        GuestMemory.Write(entry + 14, (ushort)Encoding.UTF8.GetByteCount(full));
        GuestMemory.Write(entry + 16, (ushort)Encoding.UTF8.GetByteCount(d.Path + "/"));
        GuestMemory.WriteCString(entry + 0x18, full, PathMax);
        return 0;
    }

    [Hle] int sceFiosDHRead(uint attr, int dh, uint entry) => Complete(attr, sceFiosDHReadSync(attr, dh, entry));

    [Hle] int sceFiosDHCloseSync(uint attr, int dh) { lock (_lock) return _dirs.Remove(dh) ? 0 : ERR_BAD_DH; }
    [Hle] int sceFiosDHClose(uint attr, int dh) => Complete(attr, sceFiosDHCloseSync(attr, dh));
    [Hle] uint sceFiosDHGetPath(int dh) { lock (_lock) return _dirs.TryGetValue(dh, out var d) ? d.PathCopy : 0; }


}
