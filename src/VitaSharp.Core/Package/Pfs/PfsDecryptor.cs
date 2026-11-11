using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using VitaSharp.Core.Crypto;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Package.Pfs;

/// <summary>
/// In-place decryption of a gamedata/DLC PFS image (sce_pfs/files.db + sce_pfs/unicv.db) using the
/// klicensee from a NoNpDrm license. Algorithm ported from Vita3K's psvpfsparser (motoharu).
/// Only the read-only "unicv" flavour is supported; that is what retail app/patch/DLC packages use.
/// </summary>
public sealed class PfsDecryptor
{
    private static readonly byte[] s_hmacKey0 = FromHex("E462258B1F3121560745DB62B1436723D2BF80FE");
    private static readonly byte[] s_hmacKey1 = FromHex("AFE656BB3C17256A3C809F6E9BF19FDD5A388543");
    private static readonly byte[] s_iv0 = FromHex("74D20CC39881C213EE770B1010E4BEA7");
    private static readonly byte[] s_contractKey0 = FromHex("E12213B48016B0E99AB81F8EC02AD4A2");

    private const ushort AttrNenc = 0x4000, AttrDir = 0x8000;
    private const int PageSize = 0x400;

    private readonly string _root;
    private readonly byte[] _drvKey = new byte[16]; // klicensee as transformed by F00D key-ring 0

    private uint _filesSalt;
    private ushort _imageSpec;

    private sealed record FileNode(string RelPath, ushort Type, uint Size);
    private sealed class Table
    {
        public uint Salt;
        public uint Version;
        public uint MaxAvail;
        public uint NumSectors;
        public uint SectorSize;
        public byte[] DbSeed = [];
        public List<byte[][]> Blocks = new();
    }

    public Action<string>? Log { get; set; }

    /// <summary>A decryptor for a PFS image whose files.db/unicv.db are given to <see cref="Plan"/> (no title folder).</summary>
    public PfsDecryptor(byte[] klicensee) : this("", klicensee) { }

    public PfsDecryptor(string titleRoot, byte[] klicensee)
    {
        _root = titleRoot;
        AesUtil.EcbDecrypt(s_contractKey0, klicensee, _drvKey);
    }

    public static bool HasPfs(string titleRoot) => File.Exists(Path.Combine(titleRoot, "sce_pfs", "files.db"));

    /// <summary>Decrypts the files of an extracted title in place (sce_pfs/ is read from disk and deleted at the end).</summary>
    public void DecryptInPlace(Action<long, long>? progress = null)
    {
        PfsPlan plan = Plan(File.ReadAllBytes(Path.Combine(_root, "sce_pfs", "files.db")),
                        File.ReadAllBytes(Path.Combine(_root, "sce_pfs", "unicv.db")));
        List<string> realFiles = EnumerateRealFiles();

        // Map unicv tables (salt) -> file by matching the first sector ICV, filtered by sector count.
        var remaining = realFiles.Where(p => new FileInfo(Path.Combine(_root, p)).Length > 0).ToList();
        var bySectors = remaining.GroupBy(p => PfsPlan.SectorCount(new FileInfo(Path.Combine(_root, p)).Length))
                                 .ToDictionary(g => g.Key, g => g.ToList());

        long done = 0;
        foreach (PfsTable t in plan.Tables)
        {
            string? match = null;
            if (bySectors.TryGetValue(t.NumSectors, out List<string>? candidates))
            {
                foreach (string? c in candidates)
                {
                    using FileStream fs = File.OpenRead(Path.Combine(_root, c));
                    byte[] head = new byte[(int)Math.Min(fs.Length, t.SectorSize)];
                    fs.ReadExactly(head);
                    if (t.Matches(head))
                    {
                        match = c;
                        break;
                    }
                }
                if (match != null)
                {
                    candidates.Remove(match);
                }
            }
            if (match == null)
            {
                throw new InvalidDataException($"PFS: no file matches unicv table at page {t.Salt}");
            }

            PfsNode node = plan.Node(match);
            string path = Path.Combine(_root, match);
            if (node.Encrypted)
            {
                Log?.Invoke($"decrypt {match}");
                DecryptFile(path, plan.Cipher(t));
            }
            else
            {
                Log?.Invoke($"plain   {match}");
            }

            // Trim sector padding back to the logical size.
            using (var fs = new FileStream(path, FileMode.Open))
            {
                if (fs.Length != node.Size)
                {
                    fs.SetLength(node.Size);
                }
            }

            done += node.Size;
            progress?.Invoke(done, plan.TotalSize);
        }

        Directory.Delete(Path.Combine(_root, "sce_pfs"), true);
    }

    /// <summary>Decrypts one file in place, a few MB at a time.</summary>
    private static void DecryptFile(string path, PfsSectorCipher cipher)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long length = RandomAccess.GetLength(handle);
        int chunk = cipher.ChunkSize(PfsPlan.ChunkBytes);
        byte[] buf = new byte[(int)Math.Min(chunk, Math.Max(length, 1))];
        for (long pos = 0; pos < length; pos += chunk)
        {
            int n = (int)Math.Min(chunk, length - pos);
            Span<byte> span = buf.AsSpan(0, n);
            int got = 0;
            while (got < n)
            {
                int r = RandomAccess.Read(handle, span[got..], pos + got);
                if (r <= 0)
                {
                    throw new EndOfStreamException(path);
                }

                got += r;
            }
            cipher.Decrypt(span, pos);
            RandomAccess.Write(handle, (ReadOnlySpan<byte>)span, pos);
        }
    }

    /// <summary>
    /// Parses files.db + unicv.db and derives the per-table keys, so files can be decrypted while they are written
    /// (see <see cref="PkgInstaller"/>) instead of extracted first and rewritten in place.
    /// </summary>
    public PfsPlan Plan(byte[] filesDb, byte[] unicvDb)
    {
        List<FileNode> files = ParseFilesDb(filesDb, out _);
        if (_imageSpec != 1 && _imageSpec != 4)
        {
            throw new NotSupportedException($"PFS image spec {_imageSpec} (icv.db / savedata) is not supported for installs");
        }

        var tables = new List<PfsTable>();
        foreach (Table t in ParseUnicvDb(unicvDb))
        {
            if (t.NumSectors == 0)
            {
                continue;
            }

            byte[] secret = MakeSecret(t.Salt);
            tables.Add(new PfsTable(t.Salt, t.NumSectors, t.SectorSize, HMACSHA1.HashData(secret, BitConverter.GetBytes(0)),
                                    t.Blocks[0][0], MakeTweakKey(t)));
        }
        var nodes = new Dictionary<string, PfsNode>();
        foreach (FileNode f in files)
        {
            nodes[f.RelPath.ToUpperInvariant()] = new PfsNode(f.RelPath, f.Size, (f.Type & AttrNenc) == 0);
        }

        return new PfsPlan(tables, nodes, files.Where(f => !IsDir(f.Type)).Sum(f => (long)f.Size), _drvKey);
    }

    private static bool IsDir(ushort type) => (type & AttrDir) != 0;

    private List<string> EnumerateRealFiles()
    {
        var list = new List<string>();
        foreach (string f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(_root, f).Replace('\\', '/');
            if (PfsPlan.IsCandidatePath(rel))
            {
                list.Add(rel);
            }
        }
        return list;
    }

    // ---- key derivation -------------------------------------------------------------------

    private byte[] SaltBytes(uint icvSalt)
    {
        if (_filesSalt == 0)
        {
            return BitConverter.GetBytes(icvSalt);
        }

        byte[] b = new byte[8];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), _filesSalt);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), icvSalt);
        return b;
    }

    /// <summary>generate_secret_np: AES-CBC "with keygen" over HMAC(hmac_key1, salts).</summary>
    private byte[] MakeSecret(uint icvSalt)
    {
        byte[] combo = HMACSHA1.HashData(s_hmacKey1, SaltBytes(icvSalt));
        byte[] secret = new byte[20];
        AesUtil.CbcEncrypt(_drvKey, s_iv0, combo.AsSpan(0, 16), secret.AsSpan(0, 16));
        byte[] ks = new byte[16];
        AesUtil.EcbEncrypt(_drvKey, secret.AsSpan(0, 16), ks); // iv is now the last ciphertext block
        for (int i = 0; i < 4; i++)
        {
            secret[16 + i] = (byte)(combo[16 + i] ^ ks[i]);
        }

        return secret;
    }

    private byte[] MakeTweakKey(Table t)
    {
        if (t.Version > 1) // has dbseed (SCEIFTBL_RO, icv version > 1)
        {
            return HMACSHA1.HashData(s_hmacKey0, t.DbSeed)[..16];
        }

        return HMACSHA1.HashData(s_hmacKey0, SaltBytes(t.Salt))[..16];
    }

    // ---- files.db ---------------------------------------------------------------------------

    private List<FileNode> ParseFilesDb(byte[] db, out List<string> dirs)
    {
        if (CString(db.AsSpan(0, 8)) != "SCENGPFS")
        {
            throw new InvalidDataException("files.db: bad magic");
        }

        _imageSpec = U16LE(db, 0xC);
        uint pageSize = U32LE(db, 0x10);
        _filesSalt = U32LE(db, 0x1C);

        var dirParent = new Dictionary<uint, uint>();
        var names = new Dictionary<(uint idx, bool dir), (string name, uint parent, ushort type, uint size)>();
        for (int off = (int)pageSize; off + pageSize <= db.Length; off += (int)pageSize)
        {
            Span<byte> page = db.AsSpan(off, (int)pageSize);
            uint nFiles = U32LE(page, 8);
            if (nFiles > 9)
            {
                continue;
            }

            for (int i = 0; i < nFiles; i++)
            {
                int fh = 16 + i * 72;
                uint parent = U32LE(page, fh);
                string name = CString(page.Slice(fh + 4, 68));
                int fi = 16 + 9 * 72 + i * 16;
                uint idx = U32LE(page, fi);
                ushort type = U16LE(page, fi + 4);
                uint size = U32LE(page, fi + 8);
                if (type == 0 && size == 0)
                {
                    continue; // unexisting
                }

                bool isDir = IsDir(type);
                if (isDir)
                {
                    dirParent[idx] = parent;
                }

                names[(idx, isDir)] = (name, parent, type, size);
            }
        }

        string PathOf(uint parent, string leaf)
        {
            var parts = new List<string> { leaf };
            int guard = 0;
            while (parent != 0 && guard++ < 256)
            {
                (string name, uint parent, ushort type, uint size) d = names[(parent, true)];
                parts.Add(d.name);
                parent = d.parent;
            }
            parts.Reverse();
            return string.Join('/', parts);
        }

        var files = new List<FileNode>();
        dirs = new List<string>();
        foreach (((uint idx, bool isDir), (string name, uint parent, ushort type, uint size) v) in names)
        {
            string p = PathOf(v.parent, v.name);
            if (isDir)
            {
                dirs.Add(p);
            }
            else
            {
                files.Add(new FileNode(p, v.type, v.size));
            }
        }
        return files;
    }

    // ---- unicv.db ---------------------------------------------------------------------------

    private static List<Table> ParseUnicvDb(byte[] db)
    {
        if (CString(db.AsSpan(0, 8)) != "SCEIRODB")
        {
            throw new InvalidDataException("unicv.db: bad magic");
        }

        uint blockSize = U32LE(db, 0xC);
        var tables = new List<Table>();
        int off = (int)blockSize;
        while (off + blockSize <= db.Length)
        {
            Span<byte> page = db.AsSpan(off, (int)blockSize);
            if (IsZero(page))
            {
                off += (int)blockSize;
                continue;
            }
            if (CString(page[..8]) != "SCEIFTBL")
            {
                throw new InvalidDataException($"unicv.db: unexpected page at 0x{off:X}");
            }

            var t = new Table
            {
                Version = U32LE(page, 8),
                MaxAvail = U32LE(page, 0x10),
                NumSectors = U32LE(page, 0x14),
                SectorSize = U32LE(page, 0x18),
                DbSeed = page.Slice(0x34, 20).ToArray(),
                Salt = (uint)(off / (int)blockSize),
            };
            off += (int)blockSize;
            uint left = t.NumSectors;
            while (left > 0)
            {
                Span<byte> sp = db.AsSpan(off, (int)blockSize);
                uint nSig = U32LE(sp, 8);
                byte[][] sigs = new byte[nSig][];
                for (int i = 0; i < nSig; i++)
                {
                    sigs[i] = sp.Slice(16 + i * 20, 20).ToArray();
                }

                t.Blocks.Add(sigs);
                left -= Math.Min(left, nSig);
                off += (int)blockSize;
            }
            tables.Add(t);
        }
        return tables;
    }
}
