using VitaSharp.Core.Crypto;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Package;

public enum PkgContentType
{
    App, Dlc, Patch, Theme, Psp, PspDlc, Psx, PspTheme
}

/// <summary>What a .pkg holds, from its content type (info block 2), PARAM.SFO and DLC install folder (pkg2zip's
/// table, psdevwiki "PKG files").</summary>
public enum PkgKind
{
    VitaApp, VitaPatch, VitaDlc, VitaTheme, VitaPsm,
    /// <summary>A PSP game (content type 7).</summary>
    Psp,
    /// <summary>A PC Engine game for the PSP (content type 7, category HG).</summary>
    PspPcEngine,
    /// <summary>A PSP Go game (content type 0xE).</summary>
    PspGo,
    /// <summary>A PSP minis game (content type 0xF).</summary>
    PspMini,
    /// <summary>A NEOGEO Station game for the PSP (content type 0x10).</summary>
    PspNeoGeo,
    /// <summary>PSP add-on content (content type 7 with an install folder: EDATs for an installed game).</summary>
    PspDlc,
    /// <summary>A PSP theme (content type 9).</summary>
    PspTheme,
    /// <summary>A PSone Classic (content type 6).</summary>
    Psx,
}

public static class PkgKindExtensions
{
    /// <summary>PSP and PSone packages open without a license (their keys are fixed); Vita ones need the rif (an update
    /// too: VitaSharp decrypts the PFS layer at install time with the game's klicensee).</summary>
    public static bool NeedsLicense(this PkgKind k) => !k.IsPspEmu();

    /// <summary>A PSP or PSone package (installed under ux0:pspemu for the system's PSP emulator).</summary>
    public static bool IsPspEmu(this PkgKind k) => k >= PkgKind.Psp;

    public static string Describe(this PkgKind k) => k switch
    {
        PkgKind.VitaApp => "PS Vita game",
        PkgKind.VitaPatch => "PS Vita update",
        PkgKind.VitaDlc => "PS Vita add-on (DLC)",
        PkgKind.VitaTheme => "PS Vita theme",
        PkgKind.VitaPsm => "PlayStation Mobile",
        PkgKind.Psp => "PSP game",
        PkgKind.PspPcEngine => "PSP game (PC Engine)",
        PkgKind.PspGo => "PSP game (PSP Go)",
        PkgKind.PspMini => "PSP minis",
        PkgKind.PspNeoGeo => "PSP game (NEOGEO)",
        PkgKind.PspDlc => "PSP add-on (DLC)",
        PkgKind.PspTheme => "PSP theme",
        PkgKind.Psx => "PSone Classic",
        _ => k.ToString(),
    };
}

/// <summary>An item of the package. <paramref name="PspType"/> is byte 24 of the item record: in a PSP/PSone package
/// 0x90 marks the PSP content (PSP key), the rest is the PS3 wrapper (PS3 key).</summary>
public sealed record PkgEntry(string Name, ulong DataOffset, ulong DataSize, bool IsDirectory, byte PspType = 0);

/// <summary>
/// Reader for retail .pkg files (outer AES-128-CTR layer): PS Vita packages and the PSP/PSone packages of the PS Store
/// (key type 1). Format credits: mmozeiko/pkg2zip, lusid1/pkg2zip. The files of a Vita package are still PFS-encrypted
/// (see <see cref="Pfs.PfsDecryptor"/>); those of a PSP package are still NPDRM (the game stays encrypted as sold).
/// </summary>
public sealed class PkgReader : IDisposable
{
    private static readonly byte[] s_pkgPs3 = FromHex("2E7B71D7C9C9A14EA3221F188828B8F8");
    private static readonly byte[] s_pkgPsp = FromHex("07F2C68290B50D2C33818D709B60E62B");
    private static readonly byte[] s_pkgVita2 = FromHex("E31A70C9CE1DD72BF3C0622963F2ECCB");
    private static readonly byte[] s_pkgVita3 = FromHex("423ACA3A2BD5649F9686ABAD6FD8801F");
    private static readonly byte[] s_pkgVita4 = FromHex("AF07FD59652527BAF13389668B17D9EA");

    private readonly Stream _fs;
    private readonly byte[] _iv = new byte[16];
    private readonly byte[] _key = new byte[16];
    private readonly ulong _dataOffset;

    public string ContentId { get; }
    public string TitleId => ContentId.Length >= 16 ? ContentId.Substring(7, 9) : "";
    public uint ContentTypeRaw { get; }
    /// <summary>The key type (header 0xE7 &amp; 7): 1 PSP/PSone, 2-4 PS Vita; 0 for a package in the PS3 format (header
    /// type 1, as some PSone Classics are sold), whose items all use the PS3 key.</summary>
    public int KeyType { get; }
    /// <summary>The PARAM.SFO of the package (info block 14 for Vita, the PARAM.SFO item for PSP/PSone; empty if none).</summary>
    public byte[] ParamSfo { get; }
    /// <summary>The install folder of a PSP add-on (info block 10), empty otherwise.</summary>
    public string InstallDirectory { get; }
    public IReadOnlyList<PkgEntry> Entries { get; }

    public PkgReader(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) { }

    /// <summary>Reads the package from a seekable stream (e.g. an Android document); the reader owns it.</summary>
    public PkgReader(Stream stream)
    {
        _fs = stream;
        try
        {
            Span<byte> hdr = stackalloc byte[0x100];
            if (_fs.Length < hdr.Length)
            {
                throw new InvalidDataException("Not a PKG file (too short)");
            }

            _fs.ReadExactly(hdr);
            if (U32BE(hdr, 0) != 0x7F504B47)
            {
                throw new InvalidDataException("Not a PKG file");
            }

            uint infoOffset = U32BE(hdr, 8);
            uint infoCount = U32BE(hdr, 0xC);
            uint fileCount = U32BE(hdr, 0x14);
            _dataOffset = U64BE(hdr, 0x20);
            ContentId = CString(hdr.Slice(0x30, 0x30));
            hdr.Slice(0x70, 16).CopyTo(_iv);

            // a PS3-format package (header type 1) has its metadata where the PSP/Vita extended header would be, and all
            // of it under the PS3 key (lusid1/pkg2zip)
            KeyType = (U32BE(hdr, 4) & 0xFFFF) == 1 ? 0 : (int)(U32BE(hdr, 0xE4) & 7);
            switch (KeyType)
            {
                case 0:
                    s_pkgPs3.CopyTo(_key, 0);
                    break;
                case 1:
                    s_pkgPsp.CopyTo(_key, 0);
                    break; // PSP/PSone: the PSP key as is (the PS3 parts use PkgPs3)
                case 2:
                    AesUtil.EcbEncrypt(s_pkgVita2, _iv, _key);
                    break;
                case 3:
                    AesUtil.EcbEncrypt(s_pkgVita3, _iv, _key);
                    break;
                case 4:
                    AesUtil.EcbEncrypt(s_pkgVita4, _iv, _key);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported PKG key type {KeyType} (a PS3 package?)");
            }

            uint itemsOffset = 0, sfoOffset = 0, sfoSize = 0;
            string installDir = "";
            Span<byte> block = stackalloc byte[16];
            long off = infoOffset;
            for (uint i = 0; i < infoCount; i++)
            {
                _fs.Position = off;
                _fs.ReadExactly(block);
                uint type = U32BE(block, 0), size = U32BE(block, 4);
                switch (type)
                {
                    case 2:
                        ContentTypeRaw = U32BE(block, 8);
                        break;
                    case 10 when size > 8:
                    {
                        // the PSP add-on's install folder: 8 bytes in, a NUL-terminated name (lusid1/pkg2zip)
                        byte[] dir = new byte[Math.Min(0x28, size - 8)];
                        _fs.Position = off + 16;
                        _fs.ReadExactly(dir);
                        installDir = CString(dir);
                        break;
                    }
                    case 13:
                        itemsOffset = U32BE(block, 8);
                        break;
                    case 14:
                        sfoOffset = U32BE(block, 8);
                        sfoSize = U32BE(block, 12);
                        break;
                }
                off += 8 + size;
            }
            InstallDirectory = installDir;

            var entries = new List<PkgEntry>((int)fileCount);
            byte[] raw = new byte[32];
            for (uint i = 0; i < fileCount; i++)
            {
                ulong rel = itemsOffset + i * 32UL;
                ReadDecrypted(rel, raw, _key);
                uint nameOff = U32BE(raw, 0), nameSize = U32BE(raw, 4);
                ulong dataOff = U64BE(raw, 8), dataSize = U64BE(raw, 16);
                byte pspType = raw[24];
                int t = raw[27];
                if (nameSize > 0x1000 || _dataOffset + dataOff + dataSize > (ulong)_fs.Length)
                {
                    throw new InvalidDataException("The PKG file is truncated or corrupted (an item lies past its end)");
                }

                byte[] name = new byte[nameSize];
                ReadDecrypted(nameOff, name, KeyFor(pspType));
                entries.Add(new PkgEntry(System.Text.Encoding.UTF8.GetString(name), dataOff, dataSize, t == 4 || t == 18, pspType));
            }
            Entries = entries;

            if (sfoSize > 0)
            {
                ParamSfo = new byte[sfoSize];
                _fs.Position = sfoOffset;
                _fs.ReadExactly(ParamSfo);
            }
            else if (entries.FirstOrDefault(e => !e.IsDirectory && e.Name == "PARAM.SFO" && e.DataSize <= 0x10000) is { } sfoItem)
            {
                var ms = new MemoryStream((int)sfoItem.DataSize);
                Extract(sfoItem, ms);
                ParamSfo = ms.ToArray();
            }
            else
            {
                ParamSfo = [];
            }
        }
        catch
        {
            _fs.Dispose();
            throw;
        }
    }

    private byte[] KeyFor(byte pspType) => KeyType == 1 && pspType != 0x90 ? s_pkgPs3 : _key;

    /// <summary>The kind of package (content type, PARAM.SFO category, install folder).</summary>
    public PkgKind Kind
    {
        get
        {
            string category = ParamSfo.Length > 0 ? new Sfo(ParamSfo).Category : "";
            return ContentTypeRaw switch
            {
                6 => PkgKind.Psx,
                // a PS3-format PSone Classic: "<ID>/EBOOT.PBP" (lusid1/pkg2zip); any other type-1 package is a PS3 one
                1 when Entries.Any(e => !e.IsDirectory && PspPkgInstaller.IsTopLevelEboot(e.Name)) => PkgKind.Psx,
                7 when InstallDirectory.Length > 0 => PkgKind.PspDlc,
                7 when category == "HG" => PkgKind.PspPcEngine,
                7 => PkgKind.Psp,
                9 => PkgKind.PspTheme,
                0xE => PkgKind.PspGo,
                0xF => PkgKind.PspMini,
                0x10 => PkgKind.PspNeoGeo,
                0x15 => category == "gp" ? PkgKind.VitaPatch : PkgKind.VitaApp,
                0x16 => PkgKind.VitaDlc,
                0x18 or 0x1D => PkgKind.VitaPsm,
                0x1F => PkgKind.VitaTheme,
                _ => throw new NotSupportedException($"Unsupported PKG content type 0x{ContentTypeRaw:X} (not a PS Vita, PSP or PSone package)")
            };
        }
    }

    public PkgContentType ContentType => ContentTypeRaw switch
    {
        0x15 => PkgContentType.App,
        0x16 => PkgContentType.Dlc,
        0x1F => PkgContentType.Theme,
        6 => PkgContentType.Psx,
        9 => PkgContentType.PspTheme,
        7 when InstallDirectory.Length > 0 => PkgContentType.PspDlc,
        7 or 0xE or 0xF or 0x10 => PkgContentType.Psp,
        _ => throw new NotSupportedException($"Unsupported PKG content type 0x{ContentTypeRaw:X}")
    };

    /// <summary>Reads and decrypts bytes at an offset relative to the encrypted data area (must be 16-aligned).</summary>
    private void ReadDecrypted(ulong rel, Span<byte> dst, byte[] key)
    {
        ulong aligned = rel & ~15UL;
        int skip = (int)(rel - aligned);
        byte[] tmp = new byte[(skip + dst.Length + 15) & ~15];
        _fs.Position = (long)(_dataOffset + aligned);
        _fs.ReadExactly(tmp);
        using var ctr = new AesUtil.Ctr(key);
        ctr.Transform(tmp, AesUtil.CounterAt(_iv, aligned / 16));
        tmp.AsSpan(skip, dst.Length).CopyTo(dst);
    }

    /// <summary>A cipher for <see cref="ReadData"/> (reuse it across calls; not thread-safe). In a PSP/PSone package the
    /// key depends on the entry: use <see cref="CreateCipher(PkgEntry)"/>.</summary>
    public AesUtil.Ctr CreateCipher() => new(_key);

    /// <summary>A cipher for the entries that share <paramref name="pkgEntry"/>'s key.</summary>
    public AesUtil.Ctr CreateCipher(PkgEntry pkgEntry) => new(KeyFor(pkgEntry.PspType));

    /// <summary>
    /// Reads <paramref name="dst"/>.Length decrypted bytes of an entry starting at <paramref name="offset"/> (a multiple
    /// of 16) into <paramref name="dst"/>; returns the time spent reading and decrypting.
    /// </summary>
    public (TimeSpan read, TimeSpan decrypt) ReadData(PkgEntry pkgEntry, long offset, Span<byte> dst, AesUtil.Ctr ctr)
    {
        if ((offset & 15) != 0)
        {
            throw new ArgumentException("offset must be 16-aligned", nameof(offset));
        }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _fs.Position = (long)(_dataOffset + pkgEntry.DataOffset) + offset;
        _fs.ReadExactly(dst);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Span<byte> counter = stackalloc byte[16];
        AesUtil.CounterAt(_iv, (pkgEntry.DataOffset + (ulong)offset) / 16).CopyTo(counter);
        ctr.Transform(dst, counter);
        return (System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1), System.Diagnostics.Stopwatch.GetElapsedTime(t1));
    }

    /// <summary>Reads <paramref name="dst"/>.Length decrypted bytes of <paramref name="pkgEntry"/> at any
    /// <paramref name="offset"/> (no alignment needed; small reads such as headers).</summary>
    public void ReadAt(PkgEntry pkgEntry, long offset, Span<byte> dst)
    {
        if (offset < 0 || (ulong)offset + (ulong)dst.Length > pkgEntry.DataSize)
        {
            throw new InvalidDataException($"{pkgEntry.Name}: read past the end of the file (corrupted package?)");
        }

        ReadDecrypted(pkgEntry.DataOffset + (ulong)offset, dst, KeyFor(pkgEntry.PspType));
    }

    public void Extract(PkgEntry pkgEntry, Stream output, Action<long>? progress = null)
    {
        if (pkgEntry.IsDirectory)
        {
            return;
        }

        using var ctr = new AesUtil.Ctr(KeyFor(pkgEntry.PspType));
        byte[] counter = AesUtil.CounterAt(_iv, pkgEntry.DataOffset / 16);
        _fs.Position = (long)(_dataOffset + pkgEntry.DataOffset);
        byte[] buf = new byte[1 << 20];
        ulong left = pkgEntry.DataSize;
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
