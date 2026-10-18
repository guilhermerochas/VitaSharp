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
    static readonly byte[] HmacKey0 = FromHex("E462258B1F3121560745DB62B1436723D2BF80FE");
    static readonly byte[] HmacKey1 = FromHex("AFE656BB3C17256A3C809F6E9BF19FDD5A388543");
    static readonly byte[] Iv0 = FromHex("74D20CC39881C213EE770B1010E4BEA7");
    static readonly byte[] ContractKey0 = FromHex("E12213B48016B0E99AB81F8EC02AD4A2");

    const ushort AttrNenc = 0x4000, AttrDir = 0x8000;
    const int PageSize = 0x400;

    readonly string _root;
    readonly byte[] _klicensee;
    readonly byte[] _drvKey = new byte[16]; // klicensee as transformed by F00D key-ring 0

    uint _filesSalt;
    ushort _imageSpec;

    sealed record FileNode(string RelPath, ushort Type, uint Size);
    sealed class Table
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

    public PfsDecryptor(string titleRoot, byte[] klicensee)
    {
        _root = titleRoot;
        _klicensee = klicensee;
        AesUtil.EcbDecrypt(ContractKey0, klicensee, _drvKey);
    }

    public static bool HasPfs(string titleRoot) => File.Exists(Path.Combine(titleRoot, "sce_pfs", "files.db"));

    public void DecryptInPlace(Action<long, long>? progress = null)
    {
        var files = ParseFilesDb(out var dirs);
        if (_imageSpec != 1 && _imageSpec != 4)
            throw new NotSupportedException($"PFS image spec {_imageSpec} (icv.db / savedata) is not supported for installs");

        var tables = ParseUnicvDb();
        var byPath = files.ToDictionary(f => f.RelPath.ToUpperInvariant());
        var realFiles = EnumerateRealFiles();

        // Map unicv tables (salt) -> file by matching the first sector ICV, filtered by sector count.
        var remaining = realFiles.Where(p => new FileInfo(Path.Combine(_root, p)).Length > 0).ToList();
        var bySectors = remaining.GroupBy(p => SectorCount(new FileInfo(Path.Combine(_root, p)).Length, 0x8000))
                                 .ToDictionary(g => g.Key, g => g.ToList());

        long total = files.Where(f => !IsDir(f.Type)).Sum(f => (long)f.Size), done = 0;
        foreach (var t in tables)
        {
            if (t.NumSectors == 0) continue;
            byte[] secret = MakeSecret(t.Salt);
            byte[] sigKey = HMACSHA1.HashData(secret, BitConverter.GetBytes(0));
            byte[] want = t.Blocks[0][0];

            string? match = null;
            if (bySectors.TryGetValue(t.NumSectors, out var candidates))
            {
                foreach (var c in candidates)
                {
                    using var fs = File.OpenRead(Path.Combine(_root, c));
                    var head = new byte[(int)Math.Min(fs.Length, t.SectorSize)];
                    fs.ReadExactly(head);
                    if (HMACSHA1.HashData(sigKey, head).AsSpan().SequenceEqual(want)) { match = c; break; }
                }
                if (match != null) candidates.Remove(match);
            }
            if (match == null)
                throw new InvalidDataException($"PFS: no file matches unicv table at page {t.Salt}");

            if (!byPath.TryGetValue(match.ToUpperInvariant(), out var node))
                throw new InvalidDataException($"PFS: {match} is not listed in files.db");

            if ((node.Type & AttrNenc) == 0)
                DecryptFile(Path.Combine(_root, match), node, t);
            else
                Log?.Invoke($"plain   {match}");

            // Trim sector padding back to the logical size.
            using (var fs = new FileStream(Path.Combine(_root, match), FileMode.Open))
                if (fs.Length != node.Size) fs.SetLength(node.Size);

            done += node.Size;
            progress?.Invoke(done, total);
        }

        Directory.Delete(Path.Combine(_root, "sce_pfs"), true);
    }

    static uint SectorCount(long size, uint sectorSize) => (uint)((size + sectorSize - 1) / sectorSize);

    static bool IsDir(ushort type) => (type & AttrDir) != 0;

    List<string> EnumerateRealFiles()
    {
        var list = new List<string>();
        var pkgDir = Path.GetFullPath(Path.Combine(_root, "sce_sys", "package"));
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_root, f).Replace('\\', '/');
            if (rel.StartsWith("sce_pfs/", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFullPath(f).StartsWith(pkgDir, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(rel);
        }
        return list;
    }

    // ---- key derivation -------------------------------------------------------------------

    byte[] SaltBytes(uint icvSalt)
    {
        if (_filesSalt == 0) return BitConverter.GetBytes(icvSalt);
        var b = new byte[8];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), _filesSalt);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), icvSalt);
        return b;
    }

    /// <summary>generate_secret_np: AES-CBC "with keygen" over HMAC(hmac_key1, salts).</summary>
    byte[] MakeSecret(uint icvSalt)
    {
        byte[] combo = HMACSHA1.HashData(HmacKey1, SaltBytes(icvSalt));
        var secret = new byte[20];
        AesUtil.CbcEncrypt(_drvKey, Iv0, combo.AsSpan(0, 16), secret.AsSpan(0, 16));
        var ks = new byte[16];
        AesUtil.EcbEncrypt(_drvKey, secret.AsSpan(0, 16), ks); // iv is now the last ciphertext block
        for (int i = 0; i < 4; i++) secret[16 + i] = (byte)(combo[16 + i] ^ ks[i]);
        return secret;
    }

    byte[] MakeTweakKey(Table t)
    {
        if (t.Version > 1) // has dbseed (SCEIFTBL_RO, icv version > 1)
            return HMACSHA1.HashData(HmacKey0, t.DbSeed)[..16];
        return HMACSHA1.HashData(HmacKey0, SaltBytes(t.Salt))[..16];
    }

    void DecryptFile(string path, FileNode node, Table t)
    {
        Log?.Invoke($"decrypt {Path.GetRelativePath(_root, path)}");
        byte[] tweakKey = MakeTweakKey(t);
        uint sector = t.SectorSize;

        using var aes = Aes.Create();
        aes.Key = _drvKey;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        long length = fs.Length;
        var buf = new byte[sector];
        var iv = new byte[16];
        var ks = new byte[16];
        long sectorIndex = 0;
        for (long pos = 0; pos < length; pos += sector, sectorIndex++)
        {
            int n = (int)Math.Min(sector, length - pos);
            fs.Position = pos;
            fs.ReadExactly(buf, 0, n);

            ulong tweak = (ulong)sector * (ulong)sectorIndex;
            BitConverter.TryWriteBytes(iv.AsSpan(0, 8), tweak);
            Array.Clear(iv, 8, 8);
            for (int i = 0; i < 16; i++) iv[i] ^= tweakKey[i];

            int aligned = n & ~15;
            if (aligned > 0)
            {
                var lastCipher = buf.AsSpan(aligned - 16, 16).ToArray();
                aes.DecryptCbc(buf.AsSpan(0, aligned), iv, buf.AsSpan(0, aligned), PaddingMode.None);
                lastCipher.CopyTo(iv);
            }
            if (n > aligned)
            {
                aes.EncryptEcb(iv, ks, PaddingMode.None);
                for (int i = aligned; i < n; i++) buf[i] ^= ks[i - aligned];
            }
            fs.Position = pos;
            fs.Write(buf, 0, n);
        }
    }

    // ---- files.db ---------------------------------------------------------------------------

    List<FileNode> ParseFilesDb(out List<string> dirs)
    {
        byte[] db = File.ReadAllBytes(Path.Combine(_root, "sce_pfs", "files.db"));
        if (CString(db.AsSpan(0, 8)) != "SCENGPFS") throw new InvalidDataException("files.db: bad magic");
        _imageSpec = U16LE(db, 0xC);
        uint pageSize = U32LE(db, 0x10);
        _filesSalt = U32LE(db, 0x1C);

        var dirParent = new Dictionary<uint, uint>();
        var names = new Dictionary<(uint idx, bool dir), (string name, uint parent, ushort type, uint size)>();
        for (int off = (int)pageSize; off + pageSize <= db.Length; off += (int)pageSize)
        {
            var page = db.AsSpan(off, (int)pageSize);
            uint nFiles = U32LE(page, 8);
            if (nFiles > 9) continue;
            for (int i = 0; i < nFiles; i++)
            {
                int fh = 16 + i * 72;
                uint parent = U32LE(page, fh);
                string name = CString(page.Slice(fh + 4, 68));
                int fi = 16 + 9 * 72 + i * 16;
                uint idx = U32LE(page, fi);
                ushort type = U16LE(page, fi + 4);
                uint size = U32LE(page, fi + 8);
                if (type == 0 && size == 0) continue; // unexisting
                bool isDir = IsDir(type);
                if (isDir) dirParent[idx] = parent;
                names[(idx, isDir)] = (name, parent, type, size);
            }
        }

        string PathOf(uint parent, string leaf)
        {
            var parts = new List<string> { leaf };
            int guard = 0;
            while (parent != 0 && guard++ < 256)
            {
                var d = names[(parent, true)];
                parts.Add(d.name);
                parent = d.parent;
            }
            parts.Reverse();
            return string.Join('/', parts);
        }

        var files = new List<FileNode>();
        dirs = new List<string>();
        foreach (var ((idx, isDir), v) in names)
        {
            var p = PathOf(v.parent, v.name);
            if (isDir) dirs.Add(p);
            else files.Add(new FileNode(p, v.type, v.size));
        }
        return files;
    }

    // ---- unicv.db ---------------------------------------------------------------------------

    List<Table> ParseUnicvDb()
    {
        byte[] db = File.ReadAllBytes(Path.Combine(_root, "sce_pfs", "unicv.db"));
        if (CString(db.AsSpan(0, 8)) != "SCEIRODB") throw new InvalidDataException("unicv.db: bad magic");
        uint blockSize = U32LE(db, 0xC);
        var tables = new List<Table>();
        int off = (int)blockSize;
        while (off + blockSize <= db.Length)
        {
            var page = db.AsSpan(off, (int)blockSize);
            if (IsZero(page)) { off += (int)blockSize; continue; }
            if (CString(page[..8]) != "SCEIFTBL") throw new InvalidDataException($"unicv.db: unexpected page at 0x{off:X}");
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
                var sp = db.AsSpan(off, (int)blockSize);
                uint nSig = U32LE(sp, 8);
                var sigs = new byte[nSig][];
                for (int i = 0; i < nSig; i++) sigs[i] = sp.Slice(16 + i * 20, 20).ToArray();
                t.Blocks.Add(sigs);
                left -= Math.Min(left, nSig);
                off += (int)blockSize;
            }
            tables.Add(t);
        }
        return tables;
    }
}
