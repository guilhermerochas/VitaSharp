using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VitaSharp.Core.Crypto;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>PUP container, SPKG decryption, FAT/exFAT extraction and the installer, on synthetic packages (the
/// real-firmware checks at the end run only when PSVUPDAT.PUP is available).</summary>
public class FirmwareTests
{
    // ------------------------------------------------------------------ builders

    /// <summary>An SPKG the way Sony builds them: SCE header + metadata encrypted with the SPKG metadata key, the plain
    /// SPKG header at the header length, then one AES-128-CTR section (zlib-compressed when asked).</summary>
    internal static byte[] BuildSpkg(SpkgType type, byte[] payload, bool compress = false, ulong partIndex = 1, ulong parts = 1, byte target = 0)
    {
        const int headerLength = 0x400, spkgHeader = 0x80;
        byte[] body = payload;
        if (compress)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(payload);
            body = ms.ToArray();
        }
        var key = Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray();
        var iv = Enumerable.Range(1, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var enc = (byte[])body.Clone();
        using (var ctr = new AesUtil.Ctr(key)) ctr.Transform(enc, iv.ToArray());

        var f = new byte[headerLength + spkgHeader + enc.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(f, 0x00454353);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), 3);
        f[8] = 0x40; // platform
        f[9] = 0;    // key revision
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(10), 3); // SPKG
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(12), 0); // metadata offset
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(16), headerLength);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(24), (ulong)(spkgHeader + enc.Length));

        // metadata info (AES-256-CBC with the SPKG key): key, 16 zero, iv, 16 zero
        var mKey = Enumerable.Range(0, 16).Select(i => (byte)(0x30 + i)).ToArray();
        var mIv = Enumerable.Range(0, 16).Select(i => (byte)(0x60 + i)).ToArray();
        var info = new byte[64];
        mKey.CopyTo(info, 0);
        mIv.CopyTo(info, 32);
        var spkgKey = SelfKeys.Find(SelfKeys.Set0, KeyType.METADATA, SceType.SPKG, 0x37400000000, 0, SelfType.NONE)!;
        AesUtil.CbcEncrypt(BinaryHelpers.FromHex(spkgKey.Key), BinaryHelpers.FromHex(spkgKey.Iv), info, f.AsSpan(48, 64));

        // metadata header + one section + two keys (AES-128-CBC with the info key)
        var rest = new byte[headerLength - 48 - 64];
        BinaryPrimitives.WriteUInt32LittleEndian(rest.AsSpan(12), 1); // sections
        BinaryPrimitives.WriteUInt32LittleEndian(rest.AsSpan(16), 2); // keys
        var s = rest.AsSpan(32, 48);
        BinaryPrimitives.WriteUInt64LittleEndian(s, headerLength + spkgHeader);
        BinaryPrimitives.WriteUInt64LittleEndian(s[8..], (ulong)enc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 3); // AES-128-CTR
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], compress ? 2u : 1u);
        key.CopyTo(rest, 32 + 48);
        iv.CopyTo(rest, 32 + 48 + 16);
        AesUtil.CbcEncrypt(mKey, mIv, rest, f.AsSpan(48 + 64, rest.Length));

        var h = f.AsSpan(headerLength, spkgHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)type);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x10..], 0x03740000);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x60..], partIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(h[0x68..], parts);
        h[0x2C] = target;
        enc.CopyTo(f, headerLength + spkgHeader);
        return f;
    }

    /// <summary>A SCEUF container with the given files (type, data), firmware version 3.74 build 1234.</summary>
    internal static byte[] BuildPup(params (ulong Type, byte[] Data)[] files)
    {
        int n = files.Length;
        long headerLength = 0x1000;
        long cursor = headerLength;
        var offsets = new long[n];
        for (int i = 0; i < n; i++) { offsets[i] = cursor; cursor += (files[i].Data.Length + 0xF) & ~0xF; }
        var pup = new byte[cursor];
        "SCEUF"u8.CopyTo(pup);
        pup[7] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(pup.AsSpan(0x08), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(pup.AsSpan(0x10), 0x03740000);
        BinaryPrimitives.WriteUInt32LittleEndian(pup.AsSpan(0x14), 1234);
        BinaryPrimitives.WriteUInt64LittleEndian(pup.AsSpan(0x18), (ulong)n);
        BinaryPrimitives.WriteUInt64LittleEndian(pup.AsSpan(0x20), (ulong)headerLength);
        BinaryPrimitives.WriteUInt64LittleEndian(pup.AsSpan(0x28), (ulong)cursor);
        for (int i = 0; i < n; i++)
        {
            var r = pup.AsSpan(Pup.HeaderSize + i * Pup.EntrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(r, files[i].Type);
            BinaryPrimitives.WriteUInt64LittleEndian(r[8..], (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(r[16..], (ulong)files[i].Data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(r[24..], 2);
            var d = pup.AsSpan(Pup.HeaderSize + n * Pup.EntrySize + i * Pup.DigestEntrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(d, (ulong)i);
            d[8] = (byte)(0xD0 + i);
            files[i].Data.CopyTo(pup, offsets[i]);
        }
        return pup;
    }

    /// <summary>
    /// Minimal FAT12 volume (512-byte clusters, 1 FAT, 16 root entries). <paramref name="tree"/> maps '/'-separated
    /// file paths to contents (one directory level below the root is supported). Long names get VFAT entries;
    /// <paramref name="fragment"/> leaves a free cluster between the clusters of each file to exercise chains.
    /// </summary>
    internal static byte[] BuildFat12(Dictionary<string, byte[]> tree, bool fragment = true, int totalSectors = 2048)
    {
        const int bps = 512, reserved = 1, rootEntries = 64;
        int clusters = totalSectors;               // upper bound for the FAT size
        int fatSectors = (clusters * 3 / 2 + bps - 1) / bps + 1;
        int rootSectors = rootEntries * 32 / bps;
        int dataStart = reserved + fatSectors + rootSectors;
        var img = new byte[totalSectors * bps];
        img[0] = 0xEB; img[1] = 0x3C; img[2] = 0x90;
        "MSDOS5.0"u8.CopyTo(img.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(11), bps);
        img[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(14), reserved);
        img[16] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(17), rootEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(19), (ushort)totalSectors);
        img[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(22), (ushort)fatSectors);
        img[510] = 0x55; img[511] = 0xAA;
        int fatOff = reserved * bps;
        void SetFat(int c, int v)
        {
            int o = fatOff + c * 3 / 2;
            if ((c & 1) == 0) { img[o] = (byte)v; img[o + 1] = (byte)((img[o + 1] & 0xF0) | ((v >> 8) & 0x0F)); }
            else { img[o] = (byte)((img[o] & 0x0F) | ((v & 0x0F) << 4)); img[o + 1] = (byte)(v >> 4); }
        }
        SetFat(0, 0xFF8); SetFat(1, 0xFFF);
        int next = 2;
        int ClusterOff(int c) => (dataStart + c - 2) * bps;
        int Alloc(int bytes)
        {
            int count = Math.Max(1, (bytes + bps - 1) / bps), first = next, prev = -1;
            for (int i = 0; i < count; i++)
            {
                int c = next;
                next += fragment ? 2 : 1;
                if (prev >= 0) SetFat(prev, c);
                prev = c;
            }
            SetFat(prev, 0xFFF);
            return first;
        }
        void WriteChain(int first, byte[] data)
        {
            int c = first, o = 0;
            while (o < data.Length)
            {
                int n = Math.Min(bps, data.Length - o);
                data.AsSpan(o, n).CopyTo(img.AsSpan(ClusterOff(c)));
                o += n;
                int o2 = fatOff + c * 3 / 2;
                c = (c & 1) == 0 ? img[o2] | ((img[o2 + 1] & 0x0F) << 8) : (img[o2] >> 4) | (img[o2 + 1] << 4);
            }
        }

        var shortCounter = 0;
        List<byte[]> DirEntries(string name, byte attr, int first, int size)
        {
            var list = new List<byte[]>();
            string upper = name.ToUpperInvariant();
            string b = Path.GetFileNameWithoutExtension(upper), x = Path.GetExtension(upper).TrimStart('.');
            bool fits = b.Length <= 8 && x.Length <= 3 && name == upper;
            bool lowerFits = !fits && b.Length <= 8 && x.Length <= 3 && name == name.ToLowerInvariant();
            byte nt = 0;
            string sb = b, sx = x;
            if (lowerFits) nt = 0x18;
            else if (!fits) { sb = (b.Length > 6 ? b[..6] : b) + "~" + (++shortCounter); sx = x.Length > 3 ? x[..3] : x; }
            var sn = Encoding.ASCII.GetBytes(sb.PadRight(8) + sx.PadRight(3));
            if (!fits && !lowerFits)
            {
                byte sum = 0;
                foreach (var c in sn) sum = (byte)((((sum & 1) << 7) | (sum >> 1)) + c);
                var chars = (name + "\0").PadRight(((name.Length + 1 + 12) / 13) * 13, '￿');
                int pieces = chars.Length / 13;
                int[] offs = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
                for (int p = pieces; p >= 1; p--)
                {
                    var e = new byte[32];
                    e[0] = (byte)(p | (p == pieces ? 0x40 : 0));
                    e[11] = 0x0F;
                    e[13] = sum;
                    for (int k = 0; k < 13; k++) BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(offs[k]), chars[(p - 1) * 13 + k]);
                    list.Add(e);
                }
            }
            var se = new byte[32];
            sn.CopyTo(se, 0);
            se[11] = attr;
            se[12] = nt;
            BinaryPrimitives.WriteUInt16LittleEndian(se.AsSpan(20), (ushort)(first >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(se.AsSpan(26), (ushort)first);
            BinaryPrimitives.WriteUInt32LittleEndian(se.AsSpan(28), (uint)size);
            list.Add(se);
            return list;
        }

        // directory contents for "prefix" ("" = root, else "a/b/"), sub-directories first
        List<byte[]> Contents(string prefix)
        {
            var entries = new List<byte[]>();
            if (prefix.Length > 0)
            {
                var dot = new byte[32]; Encoding.ASCII.GetBytes(".          ").CopyTo(dot, 0); dot[11] = 0x10;
                var dotdot = new byte[32]; Encoding.ASCII.GetBytes("..         ").CopyTo(dotdot, 0); dotdot[11] = 0x10;
                entries.Add(dot); entries.Add(dotdot);
            }
            var below = tree.Where(kv => kv.Key.StartsWith(prefix)).Select(kv => (Rel: kv.Key[prefix.Length..], kv.Value)).ToList();
            foreach (var dir in below.Where(b => b.Rel.Contains('/')).Select(b => b.Rel[..b.Rel.IndexOf('/')]).Distinct())
            {
                var dirBytes = Contents(prefix + dir + "/").SelectMany(e => e).ToArray();
                int dc = Alloc(dirBytes.Length);
                WriteChain(dc, dirBytes);
                entries.AddRange(DirEntries(dir, 0x10, dc, 0));
            }
            foreach (var (rel, data) in below.Where(b => !b.Rel.Contains('/')))
            {
                int fc = data.Length == 0 ? 0 : Alloc(data.Length);
                if (fc != 0) WriteChain(fc, data);
                entries.AddRange(DirEntries(rel, 0x20, fc, data.Length));
            }
            return entries;
        }

        var root = new List<byte[]>();
        var label = new byte[32];
        Encoding.ASCII.GetBytes("VOLUMELABEL").CopyTo(label, 0);
        label[11] = 0x08;
        root.Add(label);
        root.AddRange(Contents(""));
        var deleted = new byte[32]; deleted[0] = 0xE5; deleted[11] = 0x20; root.Add(deleted);
        var rootBytes = root.SelectMany(e => e).ToArray();
        Assert.True(rootBytes.Length <= rootEntries * 32);
        rootBytes.CopyTo(img, (reserved + fatSectors) * bps);
        return img;
    }


    /// <summary>A ustar archive with the given regular files ('/' paths) and an explicit directory entry.</summary>
    internal static byte[] BuildTar(params (string Name, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        void Header(string name, long size, byte type)
        {
            var h = new byte[512];
            Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
            Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0') + "\0").CopyTo(h, 124);
            h[156] = type;
            "ustar\0"u8.CopyTo(h.AsSpan(257));
            ms.Write(h);
        }
        Header("app/", 0, (byte)'5');
        foreach (var (name, data) in files)
        {
            Header(name, data.Length, (byte)'0');
            ms.Write(data);
            ms.Write(new byte[(512 - data.Length % 512) % 512]);
        }
        ms.Write(new byte[1024]);
        return ms.ToArray();
    }
    static byte[] Bytes(int n, int seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 31 + seed)).ToArray();

    // ------------------------------------------------------------------ PUP / SPKG

    [Fact]
    public void Pup_ParsesHeaderFileTableAndDigests()
    {
        var spkg = BuildSpkg(SpkgType.Os0, Bytes(100, 1));
        var pupBytes = BuildPup((0x100, "3.74\n"u8.ToArray()), (0x101, "<xml/>"u8.ToArray()), (0x303, spkg));
        using var pup = new Pup(new MemoryStream(pupBytes));
        Assert.Equal(0x03740000u, pup.FirmwareVersion);
        Assert.Equal("3.74", pup.FirmwareVersionString);
        Assert.Equal(1234u, pup.BuildNumber);
        Assert.Equal(2ul, pup.PackageVersion);
        Assert.Equal(0x1000, pup.HeaderLength);
        Assert.Equal(3, pup.Entries.Count);
        Assert.Equal("version.txt", pup.Entries[0].KnownName);
        Assert.Equal("license.xml", pup.Entries[1].KnownName);
        Assert.Null(pup.Entries[2].KnownName);
        Assert.Equal(spkg.Length, pup.Entries[2].Length);
        Assert.Equal(0xD2, pup.Entries[2].Digest![0]);
        Assert.Equal("3.74", pup.ReadVersionText());
        Assert.Equal(spkg, pup.Read(pup.Entries[2]));
    }

    [Fact]
    public void Pup_RejectsNonPupAndTruncatedFiles()
    {
        Assert.Throws<InvalidDataException>(() => new Pup(new MemoryStream(new byte[0x200])));
        var pupBytes = BuildPup((0x100, "3.74\n"u8.ToArray()), (0x303, BuildSpkg(SpkgType.Vs0, Bytes(3000, 2))));
        Assert.Throws<InvalidDataException>(() => new Pup(new MemoryStream(pupBytes[..^100])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Spkg_DecryptsSectionWithMetadataKey(bool compressed)
    {
        var payload = Bytes(5000, 9);
        var spkg = BuildSpkg(SpkgType.Vs0, payload, compressed, partIndex: 2, parts: 3);
        Assert.True(Spkg.TryReadHeader(spkg, out var h));
        Assert.Equal(SpkgType.Vs0, h.Type);
        Assert.Equal(0x03740000ul, h.UpdateVersion);
        Assert.Equal(2ul, h.PartIndex);
        Assert.Equal(3ul, h.TotalParts);
        Assert.Equal(payload, Spkg.Decrypt(spkg));
    }

    [Fact]
    public void Spkg_WrongKeyMaterialIsReported()
    {
        var spkg = BuildSpkg(SpkgType.Os0, Bytes(64, 3));
        spkg[60] ^= 0xFF; // corrupt the encrypted metadata info
        Assert.Throws<InvalidDataException>(() => Spkg.Decrypt(spkg));
        Assert.False(Spkg.TryReadHeader(new byte[64], out _));
    }

    // ------------------------------------------------------------------ FAT / exFAT

    [Fact]
    public void Fat_ReadsLongNamesLowerCaseShortNamesSubdirsAndChains()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["A Long File Name.bin"] = Bytes(1300, 4),
            ["lower.txt"] = "hello"u8.ToArray(),
            ["UPPER.DAT"] = Bytes(512, 5),
            ["empty"] = [],
            ["sys/libSceJson.suprx"] = Bytes(2000, 6),
            ["sys/X.BIN"] = Bytes(10, 7),
        };
        var img = BuildFat12(files);
        var fs = FsImage.Open(new MemoryStream(img));
        var fat = Assert.IsType<FatImage>(fs);
        Assert.Equal(12, fat.FatBits);
        var entries = fs.Enumerate().ToList();
        Assert.Contains(entries, e => e.IsDirectory && e.Path == "sys");
        Assert.DoesNotContain(entries, e => e.Path.Contains("VOLUMELABEL") || e.Path.EndsWith("/.") || e.Path.EndsWith(".."));
        foreach (var (path, data) in files)
        {
            var e = Assert.Single(entries, x => x.Path == path);
            Assert.False(e.IsDirectory);
            var ms = new MemoryStream();
            fs.CopyTo(e, ms);
            Assert.Equal(data, ms.ToArray());
        }
        Assert.Equal(files.Count + 1, entries.Count);
    }

    [Fact]
    public void Fat_VolumeBehindMbrIsFound()
    {
        var vol = BuildFat12(new() { ["f.txt"] = "x"u8.ToArray() }, totalSectors: 256);
        var img = new byte[512 * 8 + vol.Length];
        img[510] = 0x55; img[511] = 0xAA;
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x1BE + 8), 8);
        vol.CopyTo(img, 512 * 8);
        var fs = FsImage.Open(new MemoryStream(img));
        Assert.Equal("f.txt", Assert.Single(fs.Enumerate()).Path);
    }

    /// <summary>exFAT volume: 512-byte sectors and clusters, FAT at sector 1, heap at sector 2, root at cluster 2.</summary>
    static byte[] BuildExFat(out Dictionary<string, byte[]> files)
    {
        const int cs = 512;
        var img = new byte[cs * 40];
        "EXFAT   "u8.CopyTo(img.AsSpan(3));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x50), 1);  // FAT sector
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x54), 1);  // FAT sectors
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x58), 2);  // heap sector
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x5C), 36); // clusters
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x60), 2);  // root cluster
        img[0x6C] = 9; img[0x6D] = 0;
        img[510] = 0x55; img[511] = 0xAA;
        void Fat(uint c, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(cs + (int)c * 4), v);
        int Cl(uint c) => cs * 2 + (int)(c - 2) * cs;
        byte[] Set(string name, bool dir, uint first, long size, bool contiguous)
        {
            int names = (name.Length + 14) / 15;
            var e = new byte[32 * (2 + names)];
            e[0] = 0x85; e[1] = (byte)(1 + names);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), (ushort)(dir ? 0x10 : 0x20));
            e[32] = 0xC0; e[33] = (byte)(0x01 | (contiguous ? 0x02 : 0)); e[35] = (byte)name.Length;
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(32 + 8), (ulong)size);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(32 + 20), first);
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(32 + 24), (ulong)size);
            for (int n = 0; n < names; n++)
            {
                int o = 64 + n * 32;
                e[o] = 0xC1;
                for (int c = 0; c < 15 && n * 15 + c < name.Length; c++) BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(o + 2 + c * 2), name[n * 15 + c]);
            }
            return e;
        }
        files = new() { ["big.dat"] = Bytes(700, 11), ["app/a_long_name_exceeding15.txt"] = "12345"u8.ToArray() };
        // root (cluster 2, chained to 3 so it spans two clusters)
        var bitmap = new byte[32]; bitmap[0] = 0x81;
        var root = bitmap.Concat(Set("app", true, 5, cs, true)).Concat(Set("big.dat", false, 6, 700, false)).ToArray();
        var deleted = Set("gone.txt", false, 9, 3, true); deleted[0] = 0x05; // in-use bit clear
        root = root.Concat(deleted).ToArray();
        // spill the end of the root into the second cluster
        var rootFull = new byte[cs * 2];
        root.CopyTo(rootFull, 0);
        for (int o = root.Length; o < cs; o += 32) rootFull[o] = 0x05; // unused (deleted) slots, not the end marker
        Set("tail.bin", false, 10, 4, true).CopyTo(rootFull, cs);
        files["tail.bin"] = "TAIL"u8.ToArray();
        rootFull.AsSpan(0, cs).CopyTo(img.AsSpan(Cl(2)));
        rootFull.AsSpan(cs, cs).CopyTo(img.AsSpan(Cl(3)));
        Fat(2, 3); Fat(3, 0xFFFFFFFF);
        // app/ (contiguous, cluster 5) with one file in cluster 7
        Set("a_long_name_exceeding15.txt", false, 7, 5, true).CopyTo(img, Cl(5));
        "12345"u8.CopyTo(img.AsSpan(Cl(7)));
        // big.dat: clusters 6 -> 8 (chained)
        files["big.dat"].AsSpan(0, cs).CopyTo(img.AsSpan(Cl(6)));
        files["big.dat"].AsSpan(cs).CopyTo(img.AsSpan(Cl(8)));
        Fat(6, 8); Fat(8, 0xFFFFFFFF);
        "TAIL"u8.CopyTo(img.AsSpan(Cl(10)));
        return img;
    }

    [Fact]
    public void ExFat_ReadsNameSetsChainsAndContiguousFiles()
    {
        var img = BuildExFat(out var files);
        var fs = Assert.IsType<ExFatImage>(FsImage.Open(new MemoryStream(img)));
        var entries = fs.Enumerate().ToList();
        Assert.Contains(entries, e => e.IsDirectory && e.Path == "app");
        Assert.DoesNotContain(entries, e => e.Path.Contains("gone"));
        foreach (var (path, data) in files)
        {
            var e = Assert.Single(entries, x => x.Path == path);
            var ms = new MemoryStream();
            fs.CopyTo(e, ms);
            Assert.Equal(data, ms.ToArray());
        }
    }

    // ------------------------------------------------------------------ installer

    static string NewTempRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "vitasharp-fwtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static string WritePup(string dir, string name, byte[] data)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, data);
        return p;
    }

    /// <summary>A full firmware: version.txt, os0, vs0 split in two parts (joined by the installer), sa0 with a PGF
    /// font, plus a part the installer must ignore (boot_slb2).</summary>
    static byte[] FullFirmwarePup()
    {
        var os0 = BuildFat12(new() { ["psp2bootconfig.skprx"] = Bytes(700, 1), ["kd/sysmodule.skprx"] = Bytes(100, 2) });
        var vs0 = BuildFat12(new()
        {
            ["sys/external/libSceJson.suprx"] = Bytes(3000, 3), ["sys/external/libult.suprx"] = Bytes(1000, 4),
            ["data/external/cdlg/msg.rco"] = Bytes(10, 5),
        });
        var sa0 = BuildFat12(new() { ["data/font/pgf/ltn0.pgf"] = Bytes(900, 6) });
        int half = vs0.Length / 2;
        return BuildPup(
            (0x100, "3.74\n"u8.ToArray()),
            (0x302, BuildSpkg(SpkgType.BootSlb2, Bytes(64, 7))),
            (0x303, BuildSpkg(SpkgType.Os0, os0, compress: true)),
            (0x304, BuildSpkg(SpkgType.Vs0, vs0[..half], compress: true, 1, 2)),
            (0x305, BuildSpkg(SpkgType.Vs0, vs0[half..], compress: true, 2, 2)),
            (0x306, BuildSpkg(SpkgType.Sa0, sa0)),
            // per-model vs0 additions: the handheld one is applied, the PS TV one is not
            (0x313, BuildSpkg(SpkgType.Vs0TarPatch, BuildTar(("app/NPXS10095/stitch_prx.suprx", Bytes(700, 9)), ("../escape.txt", Bytes(3, 1))), target: 1)),
            (0x314, BuildSpkg(SpkgType.Vs0TarPatch, BuildTar(("vsh/shell/liblocation_dolce_provider.suprx", Bytes(10, 2))), target: 2)));
    }

    [Fact]
    public void Installer_ExtractsPartitionsJoinsPartsAndWritesMetadata()
    {
        var tmp = NewTempRoot();
        try
        {
            string fsRoot = Path.Combine(tmp, "fs");
            var pup = WritePup(tmp, "PSVUPDAT.PUP", FullFirmwarePup());
            var reports = new List<FirmwareProgress>();
            var info = FirmwareInstaller.Install(pup, fsRoot, new SyncProgress(reports.Add));
            Assert.Equal("3.74", info.Version);
            Assert.Equal(1234u, info.Build);
            Assert.Equal(["os0", "vs0", "sa0"], info.Partitions);
            Assert.Equal(Bytes(700, 1), File.ReadAllBytes(Path.Combine(fsRoot, "os0", "psp2bootconfig.skprx")));
            Assert.Equal(Bytes(100, 2), File.ReadAllBytes(Path.Combine(fsRoot, "os0", "kd", "sysmodule.skprx")));
            Assert.Equal(Bytes(3000, 3), File.ReadAllBytes(Path.Combine(fsRoot, "vs0", "sys", "external", "libSceJson.suprx")));
            Assert.Equal(Bytes(1000, 4), File.ReadAllBytes(Path.Combine(fsRoot, "vs0", "sys", "external", "libult.suprx")));
            Assert.Equal(Bytes(900, 6), File.ReadAllBytes(Path.Combine(fsRoot, "sa0", "data", "font", "pgf", "ltn0.pgf")));
            Assert.Equal(Bytes(700, 9), File.ReadAllBytes(Path.Combine(fsRoot, "vs0", "app", "NPXS10095", "stitch_prx.suprx")));
            Assert.False(File.Exists(Path.Combine(fsRoot, "vs0", "vsh", "shell", "liblocation_dolce_provider.suprx")));
            Assert.False(File.Exists(Path.Combine(fsRoot, "escape.txt")));
            Assert.False(File.Exists(Path.Combine(tmp, "escape.txt")));
            Assert.False(Directory.Exists(Path.Combine(fsRoot, ".firmware-install")));
            Assert.Equal(1.0, reports[^1].Fraction);
            Assert.True(reports.Select(r => r.Fraction).SequenceEqual(reports.Select(r => r.Fraction).Order()));

            var read = FirmwareInfo.Read(fsRoot)!;
            Assert.Equal("3.74", read.Version);
            Assert.Equal("PSVUPDAT.PUP", read.Source);
            Assert.False(read.FontPackage);

            // a font package (no vs0) merges into sa0 and keeps the installed version
            var font = WritePup(tmp, "FONT.PUP", BuildPup((0x306, BuildSpkg(SpkgType.Sa0, BuildFat12(new() { ["data/font/pvf/jpn0.pvf"] = Bytes(400, 8) })))));
            var after = FirmwareInstaller.Install(font, fsRoot);
            Assert.Equal("3.74", after.Version);
            Assert.True(after.FontPackage);
            Assert.True(File.Exists(Path.Combine(fsRoot, "sa0", "data", "font", "pgf", "ltn0.pgf")));
            Assert.True(File.Exists(Path.Combine(fsRoot, "sa0", "data", "font", "pvf", "jpn0.pvf")));

            // reinstalling the full firmware replaces vs0 (stale files go) but keeps the fonts
            File.WriteAllText(Path.Combine(fsRoot, "vs0", "stale.txt"), "x");
            var again = FirmwareInstaller.Install(pup, fsRoot);
            Assert.False(File.Exists(Path.Combine(fsRoot, "vs0", "stale.txt")));
            Assert.True(File.Exists(Path.Combine(fsRoot, "sa0", "data", "font", "pvf", "jpn0.pvf")));
            Assert.True(again.FontPackage);
        }
        finally { FirmwareInstaller.DeleteTree(tmp); }
    }

    [Fact]
    public void Installer_CancelOrFailureLeavesPreviousFirmware()
    {
        var tmp = NewTempRoot();
        try
        {
            string fsRoot = Path.Combine(tmp, "fs");
            var pup = WritePup(tmp, "PSVUPDAT.PUP", FullFirmwarePup());
            FirmwareInstaller.Install(pup, fsRoot);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => FirmwareInstaller.Install(pup, fsRoot, null, cts.Token));
            Assert.True(File.Exists(Path.Combine(fsRoot, "vs0", "sys", "external", "libSceJson.suprx")));
            Assert.False(Directory.Exists(Path.Combine(fsRoot, ".firmware-install")));

            var notFirmware = WritePup(tmp, "EMPTY.PUP", BuildPup((0x100, "3.74\n"u8.ToArray()), (0x302, BuildSpkg(SpkgType.BootSlb2, Bytes(64, 1)))));
            Assert.Throws<InvalidDataException>(() => FirmwareInstaller.Install(notFirmware, fsRoot));
            Assert.Equal("3.74", FirmwareInfo.Read(fsRoot)!.Version);
        }
        finally { FirmwareInstaller.DeleteTree(tmp); }
    }

    [Fact]
    public void Installer_ImportCopiesAnotherInstallation()
    {
        var tmp = NewTempRoot();
        try
        {
            string other = Path.Combine(tmp, "vita3k"), fsRoot = Path.Combine(tmp, "fs");
            Directory.CreateDirectory(Path.Combine(other, "vs0", "sys", "external"));
            File.WriteAllBytes(Path.Combine(other, "vs0", "sys", "external", "libSceXml.suprx"), Bytes(50, 1));
            Directory.CreateDirectory(Path.Combine(other, "sa0", "data", "font", "pgf"));
            File.WriteAllBytes(Path.Combine(other, "sa0", "data", "font", "pgf", "ltn0.pgf"), Bytes(60, 2));
            Directory.CreateDirectory(Path.Combine(other, "ux0", "app", "GAME"));
            // fonts already copied into sa0 by hand (no firmware.json, no vs0): seen, and merged over, not a failure
            Directory.CreateDirectory(Path.Combine(fsRoot, "sa0", "data", "font", "pgf"));
            File.WriteAllBytes(Path.Combine(fsRoot, "sa0", "data", "font", "pgf", "ltn0.pgf"), Bytes(5, 9));
            File.WriteAllBytes(Path.Combine(fsRoot, "sa0", "data", "font", "pgf", "mine.pgf"), Bytes(5, 8));
            Assert.Null(FirmwareInfo.Read(fsRoot));
            Assert.True(FirmwareInfo.HasSystemFonts(fsRoot));
            var info = FirmwareInstaller.ImportFrom(other, fsRoot, "Vita3K");
            Assert.Equal(["vs0", "sa0"], info.Partitions);
            Assert.Equal("Vita3K", FirmwareInfo.Read(fsRoot)!.Source);
            Assert.True(File.Exists(Path.Combine(fsRoot, "vs0", "sys", "external", "libSceXml.suprx")));
            Assert.Equal(Bytes(60, 2), File.ReadAllBytes(Path.Combine(fsRoot, "sa0", "data", "font", "pgf", "ltn0.pgf"))); // overwritten
            Assert.True(File.Exists(Path.Combine(fsRoot, "sa0", "data", "font", "pgf", "mine.pgf")));                       // kept
            Assert.False(Directory.Exists(Path.Combine(fsRoot, "ux0", "app", "GAME"))); // only firmware partitions
            Assert.True(File.Exists(Path.Combine(other, "vs0", "sys", "external", "libSceXml.suprx")));     // source untouched
            Assert.Throws<InvalidDataException>(() => FirmwareInstaller.ImportFrom(Path.Combine(tmp, "nothing"), fsRoot, "x"));
        }
        finally { FirmwareInstaller.DeleteTree(tmp); }
    }

    [Fact]
    public void FirmwareInfo_AbsentWithoutModules()
    {
        var tmp = NewTempRoot();
        try
        {
            Assert.Null(FirmwareInfo.Read(tmp));
            Assert.False(FirmwareInfo.HasSystemFonts(tmp));
            Directory.CreateDirectory(Path.Combine(tmp, "vs0", "sys", "external")); // empty folders (Vfs creates them)
            Assert.Null(FirmwareInfo.Read(tmp));
            Assert.False(FirmwareInfo.HasSystemFonts(tmp));
        }
        finally { FirmwareInstaller.DeleteTree(tmp); }
    }


    // ------------------------------------------------------------------ sysmodule sources

    static VitaSharp.Core.Hle.Modules.SystemMisc Sysmodules(IReadOnlyCollection<string>? lle, IReadOnlyCollection<string>? hle)
    {
        var emu = (VitaSharp.Core.Emulator)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VitaSharp.Core.Emulator));
        typeof(VitaSharp.Core.Emulator).GetField("<Options>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(emu, new VitaSharp.Core.EmulatorOptions { FsRoot = Path.GetTempPath(), LleModules = lle ?? [], HleModules = hle ?? [] });
        return new VitaSharp.Core.Hle.Modules.SystemMisc { Emu = emu };
    }

    [Fact]
    public void Sysmodules_FirmwareCopyOnlyForLibrariesWithoutHle()
    {
        var m = Sysmodules(null, null);
        // no HLE: the game's copy, else the firmware's
        Assert.Equal(["app0:sce_module/libSceJson.suprx", "vs0:sys/external/libSceJson.suprx"], m.SysmoduleCandidates("libSceJson"));
        Assert.Equal(["app0:sce_module/libSceSqlite.suprx", "vs0:sys/external/libSceSqlite.suprx"], m.SysmoduleCandidates("libSceSqlite"));
        // HLE implemented: the game's copy only (installing the firmware does not move titles off the HLE)
        Assert.Equal(["app0:sce_module/libsas.suprx"], m.SysmoduleCandidates("libsas"));
        Assert.Equal(["app0:sce_module/libult.suprx"], m.SysmoduleCandidates("libult"));
        Assert.Equal(["app0:sce_module/libfiber.suprx"], m.SysmoduleCandidates("libfiber"));

        var opted = Sysmodules(["libsas"], ["libSceJson", "LIBULT"]);
        Assert.Equal(["app0:sce_module/libsas.suprx", "vs0:sys/external/libsas.suprx"], opted.SysmoduleCandidates("libsas"));
        Assert.Empty(opted.SysmoduleCandidates("libSceJson")); // forced HLE, even over a firmware module
        Assert.Empty(opted.SysmoduleCandidates("libult"));     // ... and over the game's own copy
        Assert.Equal(["app0:sce_module/libatrac.suprx"], opted.SysmoduleCandidates("libatrac"));
    }
    sealed class SyncProgress(Action<FirmwareProgress> report) : IProgress<FirmwareProgress>
    {
        public void Report(FirmwareProgress value) => report(value);
    }

    // ------------------------------------------------------------------ real firmware (skipped without the file)

    [RealFirmwareFact]
    public void RealPup_InstallsAndKeyModulesDecrypt()
    {
        var tmp = NewTempRoot();
        try
        {
            string fsRoot = Path.Combine(tmp, "fs");
            var info = FirmwareInstaller.Install(RealFirmware.PupPath!, fsRoot);
            Assert.False(string.IsNullOrEmpty(info.Version));
            Assert.Contains("vs0", info.Partitions);
            Assert.Contains("os0", info.Partitions);
            foreach (var lib in new[] { "libSceJson", "libSceXml", "libSceSqlite", "libult", "libfiber", "libc" })
            {
                var path = Path.Combine(fsRoot, "vs0", "sys", "external", lib + ".suprx");
                Assert.True(File.Exists(path), lib);
                var elf = SelfDecryptor.ToElf(File.ReadAllBytes(path), null);
                Assert.True(SelfDecryptor.IsElf(elf), lib);
            }
            Assert.Equal(info.Version, FirmwareInfo.DetectVersion(fsRoot));
            // the handheld vs0 tar patch (system apps the PS TV does not have) is applied
            Assert.True(File.Exists(Path.Combine(fsRoot, "vs0", "vsh", "shell", "liblocation_provider.suprx")));
            Assert.False(File.Exists(Path.Combine(fsRoot, "vs0", "vsh", "shell", "liblocation_dolce_provider.suprx")));
        }
        finally { FirmwareInstaller.DeleteTree(tmp); }
    }
}

/// <summary>The official update for the real-firmware tests: VITASHARP_FIRMWARE_PUP, else PSVUPDAT.PUP in the
/// repository root (gitignored, never committed). Absent: those tests are skipped.</summary>
static class RealFirmware
{
    public static readonly string? PupPath = Find();

    static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    static string? Find()
    {
        var env = Environment.GetEnvironmentVariable(EnvVars.FirmwarePup);
        if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
        // the test binaries may be built anywhere (-o): look up from this source file instead
        for (var d = new FileInfo(SourceFile()).Directory; d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
            {
                var p = Path.Combine(d.FullName, "PSVUPDAT.PUP");
                if (File.Exists(p)) return p;
                // a git worktree under <repo>/.claude/worktrees/<name>: the file sits in the main checkout
                var main = d.Parent?.Parent?.Parent;
                if (d.Parent?.Name == "worktrees" && main != null && File.Exists(Path.Combine(main.FullName, "PSVUPDAT.PUP")))
                    return Path.Combine(main.FullName, "PSVUPDAT.PUP");
                return null;
            }
        }
        return null;
    }
}

public sealed class RealFirmwareFactAttribute : FactAttribute
{
    public RealFirmwareFactAttribute()
    {
        if (RealFirmware.PupPath == null) Skip = "PSVUPDAT.PUP not found (set VITASHARP_FIRMWARE_PUP to run the real-firmware tests)";
    }
}
