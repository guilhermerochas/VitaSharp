using System.Buffers.Binary;
using System.IO.Compression;
using VitaSharp.Core.Crypto;
using VitaSharp.Core.Loader.Self;

namespace VitaSharp.Core.Firmware;

/// <summary>One file of a PUP: what it is (<see cref="Type"/>), where it lives, and the digest the table lists for it.</summary>
public sealed record PupEntry(ulong Type, long Offset, long Length, ulong Flags, byte[]? Digest)
{
    /// <summary>The name of the well-known plain files (version.txt, license.xml, the updater SELFs), else null.</summary>
    public string? KnownName => Pup.KnownNames.GetValueOrDefault(Type);
}

/// <summary>
/// A PlayStation Vita update package ("SCEUF" container, *.PUP), following Vita3K packages/src/pup.cpp and
/// TeamMolecule's sceutils:
/// <code>
///   0x00 "SCEUF\0\0" + version byte    0x08 u64 package version
///   0x10 u32 firmware version (0x03740000 = 3.74), 0x14 u32 build number
///   0x18 u64 file count                 0x20 u64 header length (0x1000)    0x28 u64 package length
///   0x80 file table, 0x20 per file:   u64 type, u64 offset, u64 length, u64 flags
///   then the digest table, 0x40 per file: u64 index, 32-byte digest (keyed: not a plain SHA-256), padding
///   then a 32-byte header digest and the signature, up to the header length
/// </code>
/// Files of type 0x100 (version.txt), 0x101 (license.xml), 0x200.. (updater SELFs) are plain. Every other file is an
/// SCE "SPKG" container (<see cref="Spkg"/>) holding one part of a partition image (os0, vs0, sa0, pd0...).
/// </summary>
public sealed class Pup : IDisposable
{
    public const int HeaderSize = 0x80, EntrySize = 0x20, DigestEntrySize = 0x40;
    static ReadOnlySpan<byte> Magic => "SCEUF"u8;

    internal static readonly Dictionary<ulong, string> KnownNames = new()
    {
        [0x100] = "version.txt",
        [0x101] = "license.xml",
        [0x200] = "psp2swu.self",
        [0x204] = "cui_setupper.self",
        [0x400] = "package_scewm.wm",
        [0x401] = "package_sceas.as",
        [0x2005] = "UpdaterES1.CpUp",
        [0x2006] = "UpdaterES2.CpUp",
    };

    readonly Stream _stream;
    readonly bool _ownsStream;

    public ulong PackageVersion { get; }
    /// <summary>Firmware version as stored in the header, e.g. 0x03740000 for 3.74.</summary>
    public uint FirmwareVersion { get; }
    public uint BuildNumber { get; }
    public long HeaderLength { get; }
    public long PackageLength { get; }
    public IReadOnlyList<PupEntry> Entries { get; }

    /// <summary>"3.74" from <see cref="FirmwareVersion"/> (major.minor, each a BCD byte).</summary>
    public string FirmwareVersionString => FormatVersion(FirmwareVersion);

    public static string FormatVersion(uint v) => $"{(v >> 24) & 0xFF:X}.{(v >> 16) & 0xFF:X2}";

    public Pup(Stream stream, bool ownsStream = false)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Span<byte> h = stackalloc byte[HeaderSize];
        stream.Position = 0;
        if (stream.ReadAtLeast(h, HeaderSize, throwOnEndOfStream: false) < HeaderSize || !h[..5].SequenceEqual(Magic))
            throw new InvalidDataException("Not a PUP file (missing SCEUF header)");
        PackageVersion = BinaryPrimitives.ReadUInt64LittleEndian(h[0x08..]);
        FirmwareVersion = BinaryPrimitives.ReadUInt32LittleEndian(h[0x10..]);
        BuildNumber = BinaryPrimitives.ReadUInt32LittleEndian(h[0x14..]);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(h[0x18..]);
        HeaderLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[0x20..]);
        PackageLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[0x28..]);
        long tableEnd = HeaderSize + (long)count * EntrySize;
        if (count == 0 || count > 4096 || tableEnd > stream.Length)
            throw new InvalidDataException($"PUP file table is invalid ({count} files)");

        var table = new byte[(int)count * EntrySize];
        stream.ReadExactly(table);
        // digest table right after the file table (absent in synthetic/truncated headers)
        byte[]? digests = null;
        long digestsEnd = tableEnd + (long)count * DigestEntrySize;
        if (digestsEnd <= HeaderLength && digestsEnd <= stream.Length)
        {
            digests = new byte[(int)count * DigestEntrySize];
            stream.ReadExactly(digests);
        }

        var entries = new List<PupEntry>((int)count);
        for (int i = 0; i < (int)count; i++)
        {
            var r = table.AsSpan(i * EntrySize, EntrySize);
            ulong type = BinaryPrimitives.ReadUInt64LittleEndian(r);
            long off = (long)BinaryPrimitives.ReadUInt64LittleEndian(r[8..]);
            long len = (long)BinaryPrimitives.ReadUInt64LittleEndian(r[16..]);
            ulong flags = BinaryPrimitives.ReadUInt64LittleEndian(r[24..]);
            if (off < 0 || len < 0 || off + len > stream.Length)
                throw new InvalidDataException($"PUP file {i} (type 0x{type:X}) lies outside the file (truncated download?)");
            byte[]? digest = null;
            if (digests != null)
            {
                var d = digests.AsSpan(i * DigestEntrySize, DigestEntrySize);
                if (BinaryPrimitives.ReadUInt64LittleEndian(d) == (ulong)i) digest = d.Slice(8, 32).ToArray();
            }
            entries.Add(new PupEntry(type, off, len, flags, digest));
        }
        Entries = entries;
    }

    public static Pup Open(string path) => new(File.OpenRead(path), ownsStream: true);

    public byte[] Read(PupEntry e)
    {
        if (e.Length > Array.MaxLength) throw new InvalidDataException("PUP entry too large");
        var buf = new byte[e.Length];
        lock (_stream)
        {
            _stream.Position = e.Offset;
            _stream.ReadExactly(buf);
        }
        return buf;
    }

    /// <summary>The first line of version.txt (e.g. "3.74"), or null when the package has none.</summary>
    public string? ReadVersionText()
    {
        var e = Entries.FirstOrDefault(x => x.Type == 0x100);
        if (e == null) return null;
        var text = System.Text.Encoding.ASCII.GetString(Read(e)).TrimEnd('\0');
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 0 ? line : null;
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}

/// <summary>Partition kinds an SPKG part belongs to (the "type" at +4 of its SPKG header), names as in Vita3K.</summary>
public enum SpkgType
{
    Unknown0 = 0, Os0 = 1, Vs0Chmod = 4, Pervasive8 = 8, BootSlb2 = 9, Vs0 = 0xA, DevkitCp = 0xB, MotionC = 0xC, Bbmc = 0xD,
    MotionF = 0xF, Touch10 = 0x10, Touch11 = 0x11, Syscon12 = 0x12, Syscon13 = 0x13, Pervasive14 = 0x14, Vs0TarPatch = 0x16,
    Sa0 = 0x17, Pd0 = 0x18, Pervasive19 = 0x19, PspEmuList = 0x1B,
}

/// <summary>
/// An SCE "SPKG" container (SCE header type 3) from a PUP: a plain 0x80-byte SPKG header right after the SCE header
/// (+4 type, +0x10 update version, +0x60 part index, +0x68 part count) and one AES-128-CTR (optionally zlib) section,
/// whose key comes from the metadata, decrypted with the SPKG metadata key (sceutils / Vita3K get_segments).
/// </summary>
public static class Spkg
{
    const uint SceMagic = 0x00454353;

    public readonly record struct Header(SpkgType Type, ulong UpdateVersion, ulong PartIndex, ulong TotalParts);

    /// <summary>Recognises an SPKG and returns its header without decrypting anything.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> file, out Header header)
    {
        header = default;
        if (file.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(file) != SceMagic) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[10..]) != 3) return false; // SceType.SPKG
        long hl = (long)BinaryPrimitives.ReadUInt64LittleEndian(file[16..]);
        if (hl <= 0 || hl + 0x80 > file.Length) return false;
        var s = file.Slice((int)hl, 0x80);
        header = new Header((SpkgType)BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), BinaryPrimitives.ReadUInt64LittleEndian(s[0x10..]),
            BinaryPrimitives.ReadUInt64LittleEndian(s[0x60..]), BinaryPrimitives.ReadUInt64LittleEndian(s[0x68..]));
        return true;
    }

    /// <summary>Decrypts (and inflates) the payload of an SPKG. Several encrypted sections are concatenated in order.</summary>
    public static byte[] Decrypt(byte[] file)
    {
        if (!TryReadHeader(file, out var hdr)) throw new InvalidDataException("Not an SPKG container");
        int keyRev = file[9];
        uint metadataOffset = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        ulong headerLength = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(16));
        var sections = SelfDecryptor.DecryptSections(file, SceType.SPKG, keyRev, metadataOffset, headerLength,
            hdr.UpdateVersion << 16, SelfType.NONE, null);
        using var output = new MemoryStream();
        foreach (var s in sections)
        {
            if (s.Encryption != 3) continue;
            if (s.Offset + s.Size > (ulong)file.Length) throw new InvalidDataException("SPKG section lies outside the container");
            var raw = file.AsSpan((int)s.Offset, (int)s.Size).ToArray();
            using (var ctr = new AesUtil.Ctr(s.Key)) ctr.Transform(raw, s.Iv.ToArray());
            if (s.Compression == 2)
            {
                using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                z.CopyTo(output);
            }
            else output.Write(raw);
        }
        return output.ToArray();
    }
}
