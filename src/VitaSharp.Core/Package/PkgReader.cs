using VitaSharp.Core.Crypto;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Package;

public enum PkgContentType { App, Dlc, Patch, Theme }

public sealed record PkgEntry(string Name, ulong DataOffset, ulong DataSize, bool IsDirectory);

/// <summary>
/// Reader for retail PS Vita .pkg files (outer AES-128-CTR layer). Format credits: mmozeiko/pkg2zip.
/// The files it yields are still PFS-encrypted; see <see cref="Pfs.PfsDecryptor"/>.
/// </summary>
public sealed class PkgReader : IDisposable
{
    static readonly byte[] PkgVita2 = FromHex("E31A70C9CE1DD72BF3C0622963F2ECCB");
    static readonly byte[] PkgVita3 = FromHex("423ACA3A2BD5649F9686ABAD6FD8801F");
    static readonly byte[] PkgVita4 = FromHex("AF07FD59652527BAF13389668B17D9EA");

    readonly FileStream _fs;
    readonly byte[] _iv = new byte[16];
    readonly byte[] _key = new byte[16];
    readonly ulong _dataOffset;

    public string ContentId { get; }
    public string TitleId => ContentId.Length >= 16 ? ContentId.Substring(7, 9) : "";
    public uint ContentTypeRaw { get; }
    public byte[] ParamSfo { get; }
    public IReadOnlyList<PkgEntry> Entries { get; }

    public PkgReader(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        Span<byte> hdr = stackalloc byte[0x100];
        _fs.ReadExactly(hdr);
        if (U32BE(hdr, 0) != 0x7F504B47)
            throw new InvalidDataException("Not a PKG file");

        uint infoOffset = U32BE(hdr, 8);
        uint infoCount = U32BE(hdr, 0xC);
        uint fileCount = U32BE(hdr, 0x14);
        _dataOffset = U64BE(hdr, 0x20);
        ContentId = CString(hdr.Slice(0x30, 0x30));
        hdr.Slice(0x70, 16).CopyTo(_iv);

        int keyType = (int)(U32BE(hdr, 0xE4) & 7);
        byte[] baseKey = keyType switch
        {
            2 => PkgVita2,
            3 => PkgVita3,
            4 => PkgVita4,
            _ => throw new NotSupportedException($"Unsupported PKG key type {keyType} (PSP/PS3 packages are not Vita titles)")
        };
        AesUtil.EcbEncrypt(baseKey, _iv, _key);

        uint itemsOffset = 0, sfoOffset = 0, sfoSize = 0;
        Span<byte> block = stackalloc byte[16];
        long off = infoOffset;
        for (uint i = 0; i < infoCount; i++)
        {
            _fs.Position = off;
            _fs.ReadExactly(block);
            uint type = U32BE(block, 0), size = U32BE(block, 4);
            switch (type)
            {
                case 2: ContentTypeRaw = U32BE(block, 8); break;
                case 13: itemsOffset = U32BE(block, 8); break;
                case 14: sfoOffset = U32BE(block, 8); sfoSize = U32BE(block, 12); break;
            }
            off += 8 + size;
        }

        ParamSfo = new byte[sfoSize];
        _fs.Position = sfoOffset;
        _fs.ReadExactly(ParamSfo);

        var entries = new List<PkgEntry>((int)fileCount);
        var raw = new byte[32];
        for (uint i = 0; i < fileCount; i++)
        {
            ulong rel = itemsOffset + i * 32UL;
            ReadDecrypted(rel, raw);
            uint nameOff = U32BE(raw, 0), nameSize = U32BE(raw, 4);
            ulong dataOff = U64BE(raw, 8), dataSize = U64BE(raw, 16);
            uint flags = U32BE(raw, 24);
            var name = new byte[nameSize];
            ReadDecrypted(nameOff, name);
            int t = (int)(flags & 0xFF);
            entries.Add(new PkgEntry(System.Text.Encoding.UTF8.GetString(name), dataOff, dataSize, t == 4 || t == 18));
        }
        Entries = entries;
    }

    public PkgContentType ContentType => ContentTypeRaw switch
    {
        0x15 => PkgContentType.App,
        0x16 => PkgContentType.Dlc,
        0x1F => PkgContentType.Theme,
        _ => throw new NotSupportedException($"Unsupported PKG content type 0x{ContentTypeRaw:X}")
    };

    /// <summary>Reads and decrypts bytes at an offset relative to the encrypted data area (must be 16-aligned).</summary>
    void ReadDecrypted(ulong rel, Span<byte> dst)
    {
        ulong aligned = rel & ~15UL;
        int skip = (int)(rel - aligned);
        var tmp = new byte[(skip + dst.Length + 15) & ~15];
        _fs.Position = (long)(_dataOffset + aligned);
        _fs.ReadExactly(tmp);
        using var ctr = new AesUtil.Ctr(_key);
        ctr.Transform(tmp, AesUtil.CounterAt(_iv, aligned / 16));
        tmp.AsSpan(skip, dst.Length).CopyTo(dst);
    }

    public void Extract(PkgEntry e, Stream output, Action<long>? progress = null)
    {
        if (e.IsDirectory) return;
        using var ctr = new AesUtil.Ctr(_key);
        var counter = AesUtil.CounterAt(_iv, e.DataOffset / 16);
        _fs.Position = (long)(_dataOffset + e.DataOffset);
        var buf = new byte[1 << 20];
        ulong left = e.DataSize;
        while (left > 0)
        {
            int n = (int)Math.Min(left, (ulong)buf.Length);
            _fs.ReadExactly(buf, 0, n);
            ctr.Transform(buf.AsSpan(0, n), counter);
            output.Write(buf, 0, n);
            left -= (ulong)n;
            progress?.Invoke(n);
        }
    }

    public void Dispose() => _fs.Dispose();
}
