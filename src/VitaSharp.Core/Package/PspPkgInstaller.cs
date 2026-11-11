using System.Security.Cryptography;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Package;

/// <summary>Options of a PSP/PSone package install.</summary>
public sealed record PspInstallOptions
{
    /// <summary>Also write the decrypted UMD image of a PSP game to ux0:pspemu/ISO (what pkg2zip produces; the stock
    /// PSP emulator boots the NPDRM EBOOT.PBP instead, so this is a second copy of the game).</summary>
    public bool DecryptedIso { get; init; } = true;
}

/// <summary>
/// Installs a PSP or PSone Classic .pkg of the PS Store. Those packages open with fixed keys (no license), so nothing is
/// asked of the user. The files go where a PS Vita keeps a PSP/PSone title it downloaded (and where the shell's database
/// rebuild, vs0:vsh/common/dbrecovery_plugin, looks for them: ux0:pspemu/PSP/GAME):
/// <list type="bullet">
/// <item>PSP game: ux0:pspemu/PSP/GAME/&lt;DISC_ID&gt;/EBOOT.PBP (the NPDRM eboot as sold, with its PARAM.SFO, icons and
/// DATA.PSAR inside) plus the other USRDIR/CONTENT files (DOCUMENT.DAT, DOCINFO.EDAT, PSP-KEY.EDAT, CONTENT.DAT...) and
/// __sce_ebootpbp (the eboot signature, <see cref="PspEbootSignature"/>); optionally the decrypted UMD image
/// ux0:pspemu/ISO/&lt;title&gt; [&lt;DISC_ID&gt;].iso (pkg2zip's form).</item>
/// <item>PSP add-on: its files into ux0:pspemu/PSP/GAME/&lt;install folder&gt;.</item>
/// <item>PSP theme: ux0:pspemu/PSP/THEME.</item>
/// <item>PSone Classic: ux0:pspemu/PSP/GAME/&lt;DISC_ID&gt;/EBOOT.PBP, DOCUMENT.DAT, KEYS.BIN (the version key, which
/// stands in for the license, lusid1/pkg2zip) and __sce_ebootpbp; texture.enc (PS3 upscaling data) in ux0:ps1emu.</item>
/// </list>
/// No rif is written: the title has none (see docs/roadmap.md, the PSP item, for what the PSP emulator needs).
/// </summary>
public static class PspPkgInstaller
{
    const string Content = "USRDIR/CONTENT/";
    const int IsoSector = 2048;

    /// <summary>Installs <paramref name="pkg"/> (a PSP/PSone package; disposed here).</summary>
    public static InstalledTitle Install(PkgReader pkg, string ux0Root, PspInstallOptions? options = null,
                                         Action<string>? log = null, Action<InstallProgress>? progress = null,
                                         CancellationToken cancellationToken = default)
    {
        using var _ = pkg;
        options ??= new PspInstallOptions();
        var kind = pkg.Kind;
        if (!kind.IsPspEmu()) throw new InvalidOperationException($"{pkg.ContentId} is a {kind.Describe()} package, not a PSP/PSone one");
        var sfo = pkg.ParamSfo.Length > 0 ? new Sfo(pkg.ParamSfo) : null;
        string content = ContentFolder(pkg);
        var eboot = Find(pkg, content + "EBOOT.PBP");
        string title = sfo?.Title is { Length: > 0 } t ? t : eboot != null && EbootSfo(pkg, eboot)?.Title is { Length: > 0 } et ? et : pkg.ContentId;

        string id = kind switch
        {
            PkgKind.PspDlc => SafeId(pkg.InstallDirectory) ?? SafeId(pkg.TitleId),
            PkgKind.PspTheme => SafeId(pkg.TitleId),
            _ => (eboot != null ? SafeId(EbootDiscId(pkg, eboot)) : null) ?? SafeId(sfo?.TitleId ?? "") ?? SafeId(pkg.TitleId),
        } ?? throw new InvalidDataException($"The package has no usable title ID ({pkg.ContentId})");
        if (kind is not (PkgKind.PspDlc or PkgKind.PspTheme) && eboot == null)
            throw new InvalidDataException($"The {kind.Describe()} package {pkg.ContentId} has no USRDIR/CONTENT/EBOOT.PBP");

        string pspemu = Path.Combine(ux0Root, "pspemu");
        string dest = kind == PkgKind.PspTheme ? Path.Combine(pspemu, "PSP", "THEME") : Path.Combine(pspemu, "PSP", "GAME", id);
        log?.Invoke($"{kind.Describe()} {id} \"{title}\" ({pkg.ContentId}): no license needed");

        // what goes where: (entry, path relative to ux0)
        var plan = new List<(PkgEntry Entry, string Target)>();
        foreach (var e in pkg.Entries)
        {
            if (e.IsDirectory) continue;
            if (kind == PkgKind.PspTheme)
            {
                // the theme files (.PTF), wherever the package keeps them (USRDIR/CONTENT or the root)
                if (e.Name.StartsWith(content, StringComparison.OrdinalIgnoreCase) || !e.Name.Contains('/'))
                {
                    string rel = e.Name.StartsWith(content, StringComparison.OrdinalIgnoreCase) ? e.Name[content.Length..] : e.Name;
                    if (rel.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase)) continue;
                    plan.Add((e, Combine(dest, rel)));
                }
                continue;
            }
            if (!e.Name.StartsWith(content, StringComparison.OrdinalIgnoreCase)) continue; // the PS3 wrapper (PARAM.SFO, ICON0...)
            string relPath = e.Name[content.Length..];
            if (kind == PkgKind.Psx && relPath.Equals("texture.enc", StringComparison.OrdinalIgnoreCase))
                plan.Add((e, Combine(Path.Combine(ux0Root, "ps1emu", id), "texture.enc")));
            else plan.Add((e, Combine(dest, relPath)));
        }

        bool iso = options.DecryptedIso && eboot != null && kind is not (PkgKind.Psx or PkgKind.PspDlc or PkgKind.PspTheme)
                   && IsUmdImage(pkg, eboot);
        long total = plan.Sum(p => (long)p.Entry.DataSize) + (iso ? (long)eboot!.DataSize : 0);
        var reporter = new InstallProgressReporter(progress, total);
        reporter.Flush();

        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal;
        var written = new List<string>();
        string? isoPath = null;
        try
        {
            byte[]? ebootHash = null;
            foreach (var (e, target) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                written.Add(target);
                using var outFile = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                using var sha = e == eboot ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                pkg.Extract(e, new ProgressStream(outFile, sha, n => { reporter.Add(n, e.Name); cancellationToken.ThrowIfCancellationRequested(); }));
                if (sha != null) ebootHash = sha.GetHashAndReset();
                log?.Invoke($"wrote {Path.GetRelativePath(ux0Root, target)}");
            }

            if (eboot != null && ebootHash != null && kind is not (PkgKind.PspDlc or PkgKind.PspTheme))
            {
                int sigKind = kind == PkgKind.Psx ? PspEbootSignature.KindPs1 : PspEbootSignature.KindPsp;
                var sigPath = Path.Combine(dest, "__sce_ebootpbp");
                written.Add(sigPath);
                File.WriteAllBytes(sigPath, PspEbootSignature.Create("EBOOT.PBP", ebootHash, sigKind));
                log?.Invoke($"wrote {Path.GetRelativePath(ux0Root, sigPath)} (eboot signature)");
            }
            if (kind == PkgKind.Psx && eboot != null && PsxKeys(pkg, eboot) is { } keys)
            {
                var keysPath = Path.Combine(dest, "KEYS.BIN");
                written.Add(keysPath);
                File.WriteAllBytes(keysPath, keys);
                log?.Invoke($"wrote {Path.GetRelativePath(ux0Root, keysPath)} (version key)");
            }
            if (iso)
            {
                isoPath = Path.Combine(pspemu, "ISO", $"{SafeFileName(title)} [{id}].iso");
                Directory.CreateDirectory(Path.GetDirectoryName(isoPath)!);
                written.Add(isoPath);
                long isoBytes = WriteIso(pkg, eboot!, isoPath, n => reporter.Add(n, "ISO"), cancellationToken);
                log?.Invoke($"wrote {Path.GetRelativePath(ux0Root, isoPath)} (decrypted UMD image, {isoBytes / (1024.0 * 1024):N0} MB)");
            }
        }
        catch
        {
            foreach (var f in written)
            {
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally { thread.Priority = priority; }
        reporter.Finish();

        // a new PSP/PSone title: the shell lists ux0:pspemu/PSP/GAME when it rebuilds the card's database
        if (kind is not (PkgKind.PspDlc or PkgKind.PspTheme)) AppMeta.InvalidateCardId(ux0Root, log);
        var type = kind switch { PkgKind.Psx => PkgContentType.Psx, PkgKind.PspDlc => PkgContentType.PspDlc, PkgKind.PspTheme => PkgContentType.PspTheme, _ => PkgContentType.Psp };
        return new InstalledTitle(id, title, dest, type) { Kind = kind, IsoPath = isoPath };
    }

    static PkgEntry? Find(PkgReader pkg, string name) =>
        pkg.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A path under <paramref name="root"/> from a package item name; names that leave it are refused.</summary>
    static string Combine(string root, string rel)
    {
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.Contains(':')))
            throw new InvalidDataException($"The package has an invalid file name: {rel}");
        return Path.Combine([root, .. parts]);
    }

    /// <summary>A title ID usable as a folder name (letters and digits, 4-16 chars), or null.</summary>
    static string? SafeId(string id)
    {
        id = id.Trim();
        return id.Length is >= 4 and <= 16 && id.All(char.IsAsciiLetterOrDigit) ? id.ToUpperInvariant() : null;
    }

    static string SafeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = new string(s.Select(c => bad.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return name.Length == 0 ? "game" : name.Length > 100 ? name[..100].Trim() : name;
    }

    /// <summary>The PBP header of an EBOOT.PBP: (PARAM.SFO offset, ICON0 offset, DATA.PSAR offset).</summary>
    static (uint Sfo, uint Icon0, uint Psar) PbpHeader(PkgReader pkg, PkgEntry eboot)
    {
        if (eboot.DataSize < 0x28) throw new InvalidDataException("EBOOT.PBP is too short");
        Span<byte> h = stackalloc byte[0x28];
        pkg.ReadAt(eboot, 0, h);
        if (U32LE(h, 0) != 0x50425000) throw new InvalidDataException("EBOOT.PBP: wrong PBP signature");
        return (U32LE(h, 8), U32LE(h, 0xC), U32LE(h, 0x24));
    }

    /// <summary>The folder of the package that holds the PSP content: USRDIR/CONTENT/ in a PSP-format package,
    /// &lt;ID&gt;/ in a PS3-format PSone Classic (content type 1: "SLUS00000/EBOOT.PBP").</summary>
    internal static string ContentFolder(PkgReader pkg) =>
        pkg.Entries.FirstOrDefault(e => !e.IsDirectory && IsTopLevelEboot(e.Name)) is { } e ? e.Name[..^"EBOOT.PBP".Length] : Content;

    /// <summary>"&lt;ID&gt;/EBOOT.PBP": the eboot of a PS3-format PSone package.</summary>
    internal static bool IsTopLevelEboot(string name) =>
        name.EndsWith("/EBOOT.PBP", StringComparison.OrdinalIgnoreCase) && name.IndexOf('/') == name.Length - "/EBOOT.PBP".Length
        && !name.StartsWith("USRDIR", StringComparison.OrdinalIgnoreCase);

    /// <summary>The PARAM.SFO inside the package's EBOOT.PBP, or null.</summary>
    internal static Sfo? EbootSfo(PkgReader pkg, PkgEntry eboot)
    {
        try
        {
            var (sfoOff, icon0, _) = PbpHeader(pkg, eboot);
            if (icon0 <= sfoOff || icon0 - sfoOff > 0x10000 || icon0 > eboot.DataSize) return null;
            var sfo = new byte[icon0 - sfoOff];
            pkg.ReadAt(eboot, sfoOff, sfo);
            return new Sfo(sfo);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>The PARAM.SFO of the EBOOT.PBP of a PSP/PSone package (its title and DISC_ID), or null.</summary>
    internal static Sfo? EbootSfo(PkgReader pkg) =>
        pkg.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.Equals(ContentFolder(pkg) + "EBOOT.PBP", StringComparison.OrdinalIgnoreCase)) is { } eboot
            ? EbootSfo(pkg, eboot) : null;

    /// <summary>The DISC_ID of the PARAM.SFO inside the EBOOT.PBP (the folder name the Vita gives the title), or "".
    /// A broken PBP header is an error (the eboot would not run).</summary>
    static string EbootDiscId(PkgReader pkg, PkgEntry eboot)
    {
        PbpHeader(pkg, eboot);
        return EbootSfo(pkg, eboot)?.GetString("DISC_ID") ?? "";
    }

    static bool IsUmdImage(PkgReader pkg, PkgEntry eboot)
    {
        var (_, _, psar) = PbpHeader(pkg, eboot);
        if (psar + 256UL > eboot.DataSize) return false;
        Span<byte> magic = stackalloc byte[8];
        pkg.ReadAt(eboot, psar, magic);
        return magic.SequenceEqual("NPUMDIMG"u8);
    }

    /// <summary>The KEYS.BIN of a PSone eboot (DATA.PSAR "PSISOIMG"/"PSTITLEIMG" and its PGD key header), or null.</summary>
    static byte[]? PsxKeys(PkgReader pkg, PkgEntry eboot)
    {
        var (_, _, psar) = PbpHeader(pkg, eboot);
        if (psar + 16UL > eboot.DataSize) return null;
        Span<byte> magic = stackalloc byte[16];
        pkg.ReadAt(eboot, psar, magic);
        int keyHeaderAt = magic[..7].SequenceEqual("PSTITLE"u8) ? 0x1F0 : magic[..5].SequenceEqual("PSISO"u8) ? 0x3F0 : -1;
        if (keyHeaderAt < 0 || psar + 16UL + (ulong)keyHeaderAt + 0x90 > eboot.DataSize) return null;
        var pgd = new byte[0x90];
        pkg.ReadAt(eboot, psar + 16 + keyHeaderAt, pgd);
        if (!pgd.AsSpan(0, 4).SequenceEqual("\0PGD"u8) || U32LE(pgd, 4) != 1 || U32LE(pgd, 8) != 1) return null;
        return PspCrypto.PsxKeysBin(pgd);
    }

    /// <summary>
    /// Writes the UMD image inside a PSP eboot's DATA.PSAR (NPUMDIMG): the header's key is derived from the header itself
    /// (pkg2zip), then each block of the offset table is decrypted and, when shorter than a full block, LZRC-expanded.
    /// Returns the image size.
    /// </summary>
    static long WriteIso(PkgReader pkg, PkgEntry eboot, string path, Action<long> progress, CancellationToken ct)
    {
        var (_, _, psar) = PbpHeader(pkg, eboot);
        var header = new byte[256];
        pkg.ReadAt(eboot, psar, header);
        if (!header.AsSpan(0, 8).SequenceEqual("NPUMDIMG"u8)) throw new InvalidDataException("EBOOT.PBP: DATA.PSAR is not a UMD image");
        uint blockSectors = U32LE(header, 0xC);
        if (blockSectors is 0 or > 16) throw new InvalidDataException($"NPUMDIMG: unsupported block size {blockSectors}");
        var mac = PspCrypto.HeaderMac(header, 0xC0);
        using var cipher = PspCrypto.SectionCipher(header, mac, eboot: true, keyAt: 0xC0, ivAt: 0xA0);
        cipher.Decrypt(header.AsSpan(0x40, 0x60), 0);
        uint isoStart = U32LE(header, 0x54), isoEnd = U32LE(header, 0x64), table = U32LE(header, 0x6C);
        if (isoEnd <= isoStart) throw new InvalidDataException("NPUMDIMG: empty image");
        uint sectors = isoEnd - isoStart + 1; // the last LBA is inclusive (as PPSSPP's NPDRM reader counts it)
        uint blocks = (sectors + blockSectors - 1) / blockSectors;
        if (psar + (ulong)table + blocks * 32UL > eboot.DataSize) throw new InvalidDataException("NPUMDIMG: offset table past the end");

        int blockBytes = (int)blockSectors * IsoSector;
        var data = new byte[blockBytes + 16];
        var expanded = new byte[blockBytes];
        var entry = new byte[32];
        long size = 0;
        using var outFile = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        for (uint i = 0; i < blocks; i++)
        {
            ct.ThrowIfCancellationRequested();
            pkg.ReadAt(eboot, psar + table + 32L * i, entry);
            uint T(int k) => U32LE(entry, k * 4);
            uint offset = T(4) ^ T(2) ^ T(3), length = T(5) ^ T(1) ^ T(2), flags = T(6) ^ T(0) ^ T(3);
            if (length == 0 || length > blockBytes || psar + (ulong)offset + length > eboot.DataSize)
                throw new InvalidDataException($"NPUMDIMG: block {i} lies outside the eboot");
            int padded = (int)Math.Min((length + 15) & ~15u, eboot.DataSize - psar - offset);
            var span = data.AsSpan(0, (int)((length + 15) & ~15u));
            span.Clear();
            pkg.ReadAt(eboot, psar + offset, data.AsSpan(0, padded));
            if ((flags & 4) == 0) cipher.Decrypt(span, offset / 16);
            if (length == blockBytes) outFile.Write(data, 0, blockBytes);
            else
            {
                int n = PspCrypto.Lzrc(expanded, data.AsSpan(0, (int)length));
                if (n != blockBytes) throw new InvalidDataException($"NPUMDIMG: block {i} expands to {n} bytes, not {blockBytes}");
                outFile.Write(expanded, 0, blockBytes);
            }
            size += blockBytes;
            progress((long)eboot.DataSize * (i + 1) / blocks - (long)eboot.DataSize * i / blocks);
        }
        return size;
    }

    /// <summary>A write-through stream that hashes and counts what passes.</summary>
    sealed class ProgressStream(Stream inner, IncrementalHash? hash, Action<long> written) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            hash?.AppendData(buffer, offset, count);
            written(count);
        }
    }
}
