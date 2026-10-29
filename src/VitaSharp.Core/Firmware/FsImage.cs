using System.Buffers.Binary;
using System.Text;

namespace VitaSharp.Core.Firmware;

/// <summary>A file or directory inside a partition image ('/'-separated path relative to the image root).</summary>
public sealed record FsImageEntry(string Path, bool IsDirectory, long Size, uint FirstCluster, bool Contiguous);

/// <summary>
/// Read-only view of a firmware partition image: FAT12/16/32 (os0, vs0, sa0) or exFAT (pd0), as decrypted from a PUP.
/// A volume may start at offset 0 or behind an MBR (its first partition is used).
/// </summary>
public abstract class FsImage
{
    protected readonly Stream Img;
    protected readonly long Base;

    protected FsImage(Stream img, long baseOffset) { Img = img; Base = baseOffset; }

    /// <summary>Every entry, parents before children.</summary>
    public abstract IEnumerable<FsImageEntry> Enumerate();

    /// <summary>Copies a file's bytes to <paramref name="dst"/>.</summary>
    public abstract void CopyTo(FsImageEntry file, Stream dst, CancellationToken ct = default);

    /// <summary>Opens a FAT or exFAT image (detected from its boot sector).</summary>
    public static FsImage Open(Stream img)
    {
        Span<byte> bs = stackalloc byte[512];
        foreach (long baseOff in CandidateOffsets(img))
        {
            img.Position = baseOff;
            if (img.ReadAtLeast(bs, 512, throwOnEndOfStream: false) < 512) continue;
            if (bs.Slice(3, 8).SequenceEqual("EXFAT   "u8)) return new ExFatImage(img, baseOff, bs);
            if (FatImage.LooksLikeFat(bs)) return new FatImage(img, baseOff, bs);
        }
        throw new InvalidDataException("Unknown partition image format (no FAT or exFAT boot sector)");
    }

    static IEnumerable<long> CandidateOffsets(Stream img)
    {
        yield return 0;
        // MBR: first partition entry at 0x1BE (+8 = LBA start)
        var mbr = new byte[512];
        img.Position = 0;
        if (img.ReadAtLeast(mbr, 512, throwOnEndOfStream: false) == 512 && mbr[510] == 0x55 && mbr[511] == 0xAA)
        {
            uint lba = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(0x1BE + 8));
            if (lba != 0 && lba * 512L < img.Length) yield return lba * 512L;
        }
    }

    /// <summary>Validates one name from the image so it cannot escape the destination directory.</summary>
    protected static string? SafeName(string name)
    {
        name = name.TrimEnd('\0');
        if (name.Length == 0 || name == "." || name == "..") return null;
        if (name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(c => c < 0x20)) return null;
        return name;
    }

    protected byte[] ReadAt(long offset, int count)
    {
        var buf = new byte[count];
        Img.Position = Base + offset;
        int n = Img.ReadAtLeast(buf, count, throwOnEndOfStream: false);
        if (n < count) Array.Clear(buf, n, count - n);
        return buf;
    }
}

/// <summary>FAT12/16/32 with long file names (VFAT) and the NT lower-case flags for short names.</summary>
public sealed class FatImage : FsImage
{
    readonly int _bytesPerSector, _sectorsPerCluster, _fatBits;
    readonly long _fatOffset, _rootDirOffset, _dataOffset;
    readonly int _rootEntries;
    readonly uint _rootCluster, _clusterCount;
    readonly uint[] _fat;

    int ClusterSize => _bytesPerSector * _sectorsPerCluster;

    internal static bool LooksLikeFat(ReadOnlySpan<byte> bs)
    {
        if (bs[510] != 0x55 || bs[511] != 0xAA) return false;
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(bs[11..]);
        int spc = bs[13];
        int fats = bs[16];
        return bps is 512 or 1024 or 2048 or 4096 && spc != 0 && (spc & (spc - 1)) == 0 && fats is 1 or 2
               && BinaryPrimitives.ReadUInt16LittleEndian(bs[14..]) != 0;
    }

    internal FatImage(Stream img, long baseOffset, ReadOnlySpan<byte> bs) : base(img, baseOffset)
    {
        _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bs[11..]);
        _sectorsPerCluster = bs[13];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(bs[14..]);
        int fats = bs[16];
        _rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(bs[17..]);
        uint totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(bs[19..]);
        if (totalSectors == 0) totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(bs[32..]);
        uint fatSize = BinaryPrimitives.ReadUInt16LittleEndian(bs[22..]);
        if (fatSize == 0) fatSize = BinaryPrimitives.ReadUInt32LittleEndian(bs[36..]);
        long rootDirSectors = (_rootEntries * 32L + _bytesPerSector - 1) / _bytesPerSector;
        long dataSectors = totalSectors - (reserved + fats * (long)fatSize + rootDirSectors);
        _clusterCount = (uint)Math.Max(0, dataSectors / _sectorsPerCluster);
        _fatBits = _clusterCount < 4085 ? 12 : _clusterCount < 65525 ? 16 : 32;
        _fatOffset = reserved * (long)_bytesPerSector;
        _rootDirOffset = _fatOffset + fats * (long)fatSize * _bytesPerSector;
        _dataOffset = _rootDirOffset + rootDirSectors * _bytesPerSector;
        _rootCluster = _fatBits == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(bs[44..]) : 0;

        long fatBytes = (long)fatSize * _bytesPerSector;
        if (fatBytes > 64 << 20) throw new InvalidDataException("FAT too large");
        var raw = ReadAt(_fatOffset, (int)fatBytes);
        uint n = _clusterCount + 2;
        _fat = new uint[n];
        for (uint c = 0; c < n; c++)
        {
            _fat[c] = _fatBits switch
            {
                12 => (c * 3 / 2 + 1 < raw.Length)
                    ? ((c & 1) == 0 ? (uint)(raw[c * 3 / 2] | ((raw[c * 3 / 2 + 1] & 0x0F) << 8)) : (uint)((raw[c * 3 / 2] >> 4) | (raw[c * 3 / 2 + 1] << 4)))
                    : 0xFFF,
                16 => c * 2 + 1 < raw.Length ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan((int)c * 2)) : 0xFFFFu,
                _ => c * 4 + 3 < raw.Length ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)c * 4)) & 0x0FFFFFFF : 0x0FFFFFFFu,
            };
        }
    }

    public int FatBits => _fatBits;

    bool IsEnd(uint next) => next < 2 || next >= _clusterCount + 2 || _fatBits switch { 12 => next >= 0xFF7, 16 => next >= 0xFFF7, _ => next >= 0x0FFFFFF7 };

    IEnumerable<uint> Chain(uint first)
    {
        var seen = new HashSet<uint>();
        for (uint c = first; c >= 2 && c < _clusterCount + 2 && seen.Add(c);)
        {
            yield return c;
            uint next = _fat[c];
            if (IsEnd(next)) yield break;
            c = next;
        }
    }

    long ClusterOffset(uint c) => _dataOffset + (c - 2L) * ClusterSize;

    byte[] ReadDirectory(uint firstCluster)
    {
        if (firstCluster == 0 && _fatBits != 32) return ReadAt(_rootDirOffset, _rootEntries * 32);
        using var ms = new MemoryStream();
        foreach (var c in Chain(firstCluster == 0 ? _rootCluster : firstCluster))
        {
            ms.Write(ReadAt(ClusterOffset(c), ClusterSize));
            if (ms.Length > 64 << 20) break;
        }
        return ms.ToArray();
    }

    public override IEnumerable<FsImageEntry> Enumerate()
    {
        var stack = new Stack<(uint cluster, string path, int depth)>();
        stack.Push((0, "", 0));
        var visited = new HashSet<uint>();
        while (stack.Count > 0)
        {
            var (cluster, dirPath, depth) = stack.Pop();
            var subdirs = new List<(uint, string)>();
            foreach (var (name, attr, first, size) in ParseDirectory(ReadDirectory(cluster)))
            {
                if ((attr & 0x08) != 0) continue; // volume label
                string path = dirPath.Length == 0 ? name : dirPath + "/" + name;
                if ((attr & 0x10) != 0)
                {
                    yield return new FsImageEntry(path, true, 0, first, false);
                    if (first >= 2 && depth < 64 && visited.Add(first)) subdirs.Add((first, path));
                }
                else yield return new FsImageEntry(path, false, size, first, false);
            }
            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push((subdirs[i].Item1, subdirs[i].Item2, depth + 1));
        }
    }

    static IEnumerable<(string name, byte attr, uint first, uint size)> ParseDirectory(byte[] dir)
    {
        var lfn = new char[260];
        int lfnLen = 0;
        byte lfnSum = 0;
        var result = new List<(string, byte, uint, uint)>();
        for (int o = 0; o + 32 <= dir.Length; o += 32)
        {
            var e = dir.AsSpan(o, 32);
            if (e[0] == 0x00) break;
            if (e[0] == 0xE5) { lfnLen = 0; continue; }
            byte attr = e[11];
            if ((attr & 0x3F) == 0x0F)
            {
                int ord = e[0] & 0x1F;
                if (ord == 0 || ord > 20) { lfnLen = 0; continue; }
                if ((e[0] & 0x40) != 0) { lfnLen = ord * 13; lfnSum = e[13]; Array.Fill(lfn, '￿', 0, lfnLen); }
                else if (lfnLen == 0 || e[13] != lfnSum) { lfnLen = 0; continue; }
                int at = (ord - 1) * 13;
                ReadOnlySpan<int> offs = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
                for (int k = 0; k < 13 && at + k < lfnLen; k++) lfn[at + k] = (char)BinaryPrimitives.ReadUInt16LittleEndian(e[offs[k]..]);
                continue;
            }
            string? name = null;
            if (lfnLen > 0 && Checksum(e[..11]) == lfnSum)
            {
                int len = 0;
                while (len < lfnLen && lfn[len] != '\0' && lfn[len] != '￿') len++;
                name = new string(lfn, 0, len);
            }
            lfnLen = 0;
            name ??= ShortName(e);
            var safe = SafeName(name);
            if (safe == null) continue;
            uint first = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(e[20..]) << 16 | BinaryPrimitives.ReadUInt16LittleEndian(e[26..]));
            result.Add((safe, attr, first, BinaryPrimitives.ReadUInt32LittleEndian(e[28..])));
        }
        return result;
    }

    static byte Checksum(ReadOnlySpan<byte> shortName)
    {
        byte sum = 0;
        foreach (var b in shortName) sum = (byte)((((sum & 1) << 7) | (sum >> 1)) + b);
        return sum;
    }

    static string ShortName(ReadOnlySpan<byte> e)
    {
        var b = e[..11].ToArray();
        if (b[0] == 0x05) b[0] = 0xE5;
        string baseName = Encoding.Latin1.GetString(b, 0, 8).TrimEnd(' ');
        string ext = Encoding.Latin1.GetString(b, 8, 3).TrimEnd(' ');
        byte nt = e[12];
        if ((nt & 0x08) != 0) baseName = baseName.ToLowerInvariant();
        if ((nt & 0x10) != 0) ext = ext.ToLowerInvariant();
        return ext.Length > 0 ? baseName + "." + ext : baseName;
    }

    public override void CopyTo(FsImageEntry file, Stream dst, CancellationToken ct = default)
    {
        long left = file.Size;
        if (left == 0) return;
        foreach (var c in Chain(file.FirstCluster))
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(left, ClusterSize);
            dst.Write(ReadAt(ClusterOffset(c), n));
            left -= n;
            if (left == 0) return;
        }
        throw new InvalidDataException($"{file.Path}: cluster chain ends {left} bytes early");
    }
}

/// <summary>exFAT (the pd0 partition): file/stream/name entry sets, FAT chains and the NoFatChain (contiguous) flag.</summary>
public sealed class ExFatImage : FsImage
{
    readonly int _sectorBits, _clusterBits;
    readonly long _fatOffset, _heapOffset;
    readonly uint _clusterCount, _rootCluster;
    readonly uint[] _fat;

    long ClusterSize => 1L << _clusterBits;

    internal ExFatImage(Stream img, long baseOffset, ReadOnlySpan<byte> bs) : base(img, baseOffset)
    {
        _sectorBits = bs[0x6C];
        _clusterBits = _sectorBits + bs[0x6D];
        if (_sectorBits is < 9 or > 12 || _clusterBits > 25) throw new InvalidDataException("exFAT boot sector is invalid");
        _fatOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bs[0x50..]) << _sectorBits;
        uint fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(bs[0x54..]);
        _heapOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(bs[0x58..]) << _sectorBits;
        _clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(bs[0x5C..]);
        _rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(bs[0x60..]);
        long fatBytes = Math.Min((long)fatSectors << _sectorBits, (_clusterCount + 2L) * 4);
        if (fatBytes > 64 << 20) throw new InvalidDataException("exFAT FAT too large");
        var raw = ReadAt(_fatOffset, (int)fatBytes);
        _fat = new uint[raw.Length / 4];
        for (int i = 0; i < _fat.Length; i++) _fat[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
    }

    long ClusterOffset(uint c) => _heapOffset + (c - 2L) * ClusterSize;

    /// <summary>Cluster runs holding <paramref name="length"/> bytes from <paramref name="first"/>.</summary>
    IEnumerable<(long offset, long count)> Runs(uint first, long length, bool contiguous)
    {
        if (length <= 0 || first < 2) yield break;
        if (contiguous)
        {
            yield return (ClusterOffset(first), length);
            yield break;
        }
        var seen = new HashSet<uint>();
        long left = length;
        for (uint c = first; left > 0 && c >= 2 && c < _clusterCount + 2 && seen.Add(c);)
        {
            long n = Math.Min(left, ClusterSize);
            yield return (ClusterOffset(c), n);
            left -= n;
            if (c >= _fat.Length) yield break;
            c = _fat[c];
        }
    }

    byte[] ReadRuns(uint first, long length, bool contiguous)
    {
        if (length > 64 << 20) length = 64 << 20;
        using var ms = new MemoryStream();
        foreach (var (off, n) in Runs(first, length, contiguous)) ms.Write(ReadAt(off, (int)n));
        return ms.ToArray();
    }

    byte[] ReadRootDirectory()
    {
        // the root's size is not recorded: follow its chain to the end
        using var ms = new MemoryStream();
        var seen = new HashSet<uint>();
        for (uint c = _rootCluster; c >= 2 && c < _clusterCount + 2 && seen.Add(c) && ms.Length < 64 << 20;)
        {
            ms.Write(ReadAt(ClusterOffset(c), (int)ClusterSize));
            if (c >= _fat.Length) break;
            c = _fat[c];
        }
        return ms.ToArray();
    }

    public override IEnumerable<FsImageEntry> Enumerate()
    {
        var stack = new Stack<(byte[] dir, string path, int depth)>();
        stack.Push((ReadRootDirectory(), "", 0));
        while (stack.Count > 0)
        {
            var (dir, dirPath, depth) = stack.Pop();
            var subdirs = new List<(byte[], string)>();
            foreach (var e in ParseDirectory(dir, dirPath))
            {
                yield return e;
                if (e.IsDirectory && depth < 64) subdirs.Add((ReadRuns(e.FirstCluster, e.Size, e.Contiguous), e.Path));
            }
            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push((subdirs[i].Item1, subdirs[i].Item2, depth + 1));
        }
    }

    static List<FsImageEntry> ParseDirectory(byte[] dir, string dirPath)
    {
        var result = new List<FsImageEntry>();
        for (int o = 0; o + 32 <= dir.Length; o += 32)
        {
            byte type = dir[o];
            if (type == 0x00) break;
            if (type != 0x85) continue; // bitmap, up-case table, label, GUID, deleted or stray secondary entries
            int secondary = dir[o + 1];
            ushort attr = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(o + 4));
            if (secondary < 2 || o + 32 * (secondary + 1) > dir.Length) continue;
            int s = o + 32;
            if (dir[s] != 0xC0) continue;
            byte flags = dir[s + 1];
            int nameLength = dir[s + 3];
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(s + 20));
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(dir.AsSpan(s + 24));
            var name = new StringBuilder(nameLength);
            for (int k = 2; k <= secondary && name.Length < nameLength; k++)
            {
                int n = o + 32 * k;
                if (dir[n] != 0xC1) break;
                for (int c = 0; c < 15 && name.Length < nameLength; c++)
                    name.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(n + 2 + c * 2)));
            }
            o += 32 * secondary;
            var safe = SafeName(name.ToString());
            if (safe == null) continue;
            string path = dirPath.Length == 0 ? safe : dirPath + "/" + safe;
            result.Add(new FsImageEntry(path, (attr & 0x10) != 0, size, first, (flags & 0x02) != 0));
        }
        return result;
    }

    public override void CopyTo(FsImageEntry file, Stream dst, CancellationToken ct = default)
    {
        long done = 0;
        foreach (var (off, n) in Runs(file.FirstCluster, file.Size, file.Contiguous))
        {
            for (long p = 0; p < n; p += 1 << 20)
            {
                ct.ThrowIfCancellationRequested();
                int chunk = (int)Math.Min(1 << 20, n - p);
                dst.Write(ReadAt(off + p, chunk));
                done += chunk;
            }
        }
        if (done != file.Size) throw new InvalidDataException($"{file.Path}: cluster chain ends {file.Size - done} bytes early");
    }
}
