using System.Security.Cryptography;

namespace VitaSharp.Core.Package.Pfs;

/// <summary>A file listed in files.db: its logical size and whether it is encrypted (no NENC attribute).</summary>
public sealed record PfsNode(string RelPath, uint Size, bool Encrypted);

/// <summary>One unicv.db table (one encrypted or plain file of the image) with its derived keys.</summary>
public sealed class PfsTable(uint salt, uint numSectors, uint sectorSize, byte[] sigKey, byte[] firstIcv, byte[] tweakKey)
{
    public uint Salt { get; } = salt;
    public uint NumSectors { get; } = numSectors;
    public uint SectorSize { get; } = sectorSize;
    internal byte[] TweakKey { get; } = tweakKey;

    /// <summary>The file's first sector (still encrypted; up to <see cref="SectorSize"/> bytes) matches this table's first ICV.</summary>
    public bool Matches(ReadOnlySpan<byte> head)
    {
        Span<byte> mac = stackalloc byte[20];
        HMACSHA1.HashData(sigKey, head, mac);
        return mac.SequenceEqual(firstIcv);
    }
}

/// <summary>
/// The parsed PFS image of a title (files.db + unicv.db): which file each unicv table belongs to is found from the
/// file's sector count and the HMAC of its first sector, as psvpfsparser does.
/// </summary>
public sealed class PfsPlan
{
    /// <summary>Chunk size used to decrypt and write files (a multiple of every power-of-two sector size up to it).</summary>
    public const int ChunkBytes = 4 << 20;

    readonly Dictionary<string, PfsNode> _nodes;
    readonly byte[] _drvKey;
    readonly List<PfsTable> _unmatched;
    Aes? _aes;

    public IReadOnlyList<PfsTable> Tables
    {
        get;
    }
    /// <summary>Sum of the logical sizes of the files in files.db.</summary>
    public long TotalSize
    {
        get;
    }

    internal PfsPlan(List<PfsTable> tables, Dictionary<string, PfsNode> nodes, long totalSize, byte[] drvKey)
    {
        Tables = tables;
        _unmatched = new List<PfsTable>(tables);
        _nodes = nodes;
        TotalSize = totalSize;
        _drvKey = drvKey;
    }

    /// <summary>Files of the image that may be listed in unicv.db (not sce_pfs/ itself nor sce_sys/package/).</summary>
    public static bool IsCandidatePath(string relPath) =>
        !relPath.StartsWith("sce_pfs/", StringComparison.OrdinalIgnoreCase) &&
        !relPath.StartsWith("sce_sys/package/", StringComparison.OrdinalIgnoreCase) &&
        !relPath.Equals("sce_sys/package", StringComparison.OrdinalIgnoreCase);

    public static uint SectorCount(long size) => (uint)((size + 0x8000 - 1) / 0x8000);

    /// <summary>Largest sector size among the tables not matched yet (how much of a file <see cref="Match"/> needs).</summary>
    public int HeadBytes => _unmatched.Count == 0 ? 0 : (int)_unmatched.Max(t => t.SectorSize);

    /// <summary>
    /// The unmatched table of the file with this stored size and first bytes (<paramref name="head"/>: at least
    /// min(size, <see cref="HeadBytes"/>) bytes); null when no table describes it (the file is copied as is).
    /// </summary>
    public PfsTable? Match(long size, ReadOnlySpan<byte> head)
    {
        if (size <= 0)
        {
            return null;
        }

        uint sectors = SectorCount(size);
        foreach (var t in _unmatched)
        {
            if (t.NumSectors != sectors)
            {
                continue;
            }

            if (t.Matches(head[..(int)Math.Min(size, t.SectorSize)]))
            {
                _unmatched.Remove(t);
                return t;
            }
        }
        return null;
    }

    /// <summary>Throws when a table has no file (the same check the in-place decryptor makes).</summary>
    public void EnsureAllMatched()
    {
        if (_unmatched.Count > 0)
        {
            throw new InvalidDataException($"PFS: no file matches unicv table at page {_unmatched[0].Salt}");
        }
    }

    public PfsNode Node(string relPath) =>
        _nodes.TryGetValue(relPath.ToUpperInvariant(), out var n) ? n : throw new InvalidDataException($"PFS: {relPath} is not listed in files.db");

    public PfsSectorCipher Cipher(PfsTable t)
    {
        if (_aes == null)
        {
            _aes = Aes.Create();
            _aes.Key = _drvKey;
        }
        return new PfsSectorCipher(_aes, t.TweakKey, t.SectorSize);
    }
}

/// <summary>Sector decryption of one PFS file: AES-CBC per sector with an IV made from the sector offset and the table's tweak key.</summary>
public sealed class PfsSectorCipher
{
    readonly Aes _aes;
    readonly byte[] _tweakKey;
    readonly uint _sector;

    internal PfsSectorCipher(Aes aes, byte[] tweakKey, uint sectorSize)
    {
        _aes = aes;
        _tweakKey = tweakKey;
        _sector = sectorSize;
    }

    /// <summary>The largest multiple of the sector size that is at most <paramref name="max"/> (at least one sector).</summary>
    public int ChunkSize(int max) => (int)Math.Max(_sector, max / _sector * _sector);

    /// <summary>
    /// Decrypts <paramref name="data"/> in place; it starts at byte <paramref name="fileOffset"/> of the file (a sector
    /// boundary) and holds whole sectors, except at the end of the file.
    /// </summary>
    public void Decrypt(Span<byte> data, long fileOffset)
    {
        Span<byte> iv = stackalloc byte[16];
        Span<byte> next = stackalloc byte[16];
        Span<byte> ks = stackalloc byte[16];
        long sectorIndex = fileOffset / _sector;
        for (int pos = 0; pos < data.Length; pos += (int)_sector, sectorIndex++)
        {
            var buf = data.Slice(pos, (int)Math.Min(_sector, data.Length - pos));
            ulong tweak = (ulong)_sector * (ulong)sectorIndex;
            BitConverter.TryWriteBytes(iv[..8], tweak);
            iv[8..].Clear();
            for (int i = 0; i < 16; i++)
            {
                iv[i] ^= _tweakKey[i];
            }

            int aligned = buf.Length & ~15;
            if (aligned > 0)
            {
                buf.Slice(aligned - 16, 16).CopyTo(next);
                _aes.DecryptCbc(buf[..aligned], iv, buf[..aligned], PaddingMode.None);
                next.CopyTo(iv);
            }
            if (buf.Length > aligned)
            {
                _aes.EncryptEcb(iv, ks, PaddingMode.None);
                for (int i = aligned; i < buf.Length; i++)
                {
                    buf[i] ^= ks[i - aligned];
                }
            }
        }
    }
}
