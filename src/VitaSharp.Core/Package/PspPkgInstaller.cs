using System.Security.Cryptography;
using static VitaSharp.Core.Util.BinaryHelpers;

namespace VitaSharp.Core.Package;

/// <summary>Options of a PSP/PSone package install.</summary>
public sealed record PspInstallOptions
{
    /// <summary>The user's own license for the title (the .rif from their console's ux0:pspemu/PSP/LICENSE), installed as
    /// ux0:pspemu/PSP/LICENSE/&lt;content id&gt;.rif; its content id must be the title's. Without one the title installs and
    /// the system shows its normal "no license" message when it is started.</summary>
    public byte[]? License { get; init; }

    /// <summary>The user's console activation data (tm0:npdrm/act.dat from their console), installed as tm0:npdrm/act.dat.</summary>
    public byte[]? ActDat { get; init; }
}

/// <summary>
/// Installs a PSP or PSone Classic .pkg of the PS Store. The package layer opens with fixed keys, so the package needs no
/// license to be unpacked; the game files inside stay as sold (NPDRM, still encrypted: nothing of the game is
/// decrypted). The files go where a PS Vita keeps a PSP/PSone title it downloaded (and where the shell's database
/// rebuild, vs0:vsh/common/dbrecovery_plugin, looks for them: ux0:pspemu/PSP/GAME):
/// <list type="bullet">
/// <item>PSP game: ux0:pspemu/PSP/GAME/&lt;DISC_ID&gt;/EBOOT.PBP (the NPDRM eboot as sold, with its PARAM.SFO, icons and
/// DATA.PSAR inside) plus the other USRDIR/CONTENT files (DOCUMENT.DAT, DOCINFO.EDAT, PSP-KEY.EDAT, CONTENT.DAT...) and
/// __sce_ebootpbp (the eboot signature, <see cref="PspEbootSignature"/>).</item>
/// <item>PSP add-on: its files into ux0:pspemu/PSP/GAME/&lt;install folder&gt;.</item>
/// <item>PSP theme: ux0:pspemu/PSP/THEME.</item>
/// <item>PSone Classic: ux0:pspemu/PSP/GAME/&lt;DISC_ID&gt;/EBOOT.PBP, DOCUMENT.DAT and __sce_ebootpbp; texture.enc
/// (PS3 upscaling data) in ux0:ps1emu.</item>
/// </list>
/// The shell reads the title's license from ux0:pspemu/PSP/LICENSE/&lt;content id&gt;.rif before it starts it (the content id
/// the eboot carries at DATA.PSP + 0x560): the user's own license and act.dat go there when given
/// (<see cref="PspInstallOptions.License"/>, <see cref="PspInstallOptions.ActDat"/>).
/// </summary>
public static class PspPkgInstaller
{
    private const string Content = "USRDIR/CONTENT/";

    /// <summary>Installs <paramref name="pkg"/> (a PSP/PSone package; disposed here).</summary>
    public static InstalledTitle Install(PkgReader pkg, string ux0Root, PspInstallOptions? options = null,
                                         Action<string>? log = null, Action<InstallProgress>? progress = null,
                                         CancellationToken cancellationToken = default)
    {
        using PkgReader _ = pkg;
        options ??= new PspInstallOptions();
        PkgKind kind = pkg.Kind;
        if (!kind.IsPspEmu())
        {
            throw new InvalidOperationException($"{pkg.ContentId} is a {kind.Describe()} package, not a PSP/PSone one");
        }

        Sfo? sfo = pkg.ParamSfo.Length > 0 ? new Sfo(pkg.ParamSfo) : null;
        string content = ContentFolder(pkg);
        PkgEntry? eboot = Find(pkg, content + "EBOOT.PBP");
        string title = sfo?.Title is { Length: > 0 } t ? t : eboot != null && EbootSfo(pkg, eboot)?.Title is { Length: > 0 } et ? et : pkg.ContentId;

        string id = kind switch
        {
            PkgKind.PspDlc => SafeId(pkg.InstallDirectory) ?? SafeId(pkg.TitleId),
            PkgKind.PspTheme => SafeId(pkg.TitleId),
            _ => (eboot != null ? SafeId(EbootDiscId(pkg, eboot)) : null) ?? SafeId(sfo?.TitleId ?? "") ?? SafeId(pkg.TitleId),
        } ?? throw new InvalidDataException($"The package has no usable title ID ({pkg.ContentId})");
        if (kind is not (PkgKind.PspDlc or PkgKind.PspTheme) && eboot == null)
        {
            throw new InvalidDataException($"The {kind.Describe()} package {pkg.ContentId} has no USRDIR/CONTENT/EBOOT.PBP");
        }

        // the license is named after the content id the eboot carries (DATA.PSP + 0x560, what the shell reads before it
        // starts the title), which can differ from the package's
        string licenseId = eboot != null && EbootContentId(pkg, eboot) is { } ec ? ec : pkg.ContentId;
        if (options.License is { } lic)
        {
            if (lic.Length < 0x40)
            {
                throw new InvalidDataException("The license file is too small to be a PSP rif");
            }

            string licId = CString(lic.AsSpan(0x10, 0x30));
            if (licId != licenseId)
            {
                throw new InvalidDataException($"The license is for {licId}, the title is {licenseId}");
            }
        }
        string pspemu = Path.Combine(ux0Root, "pspemu");
        string dest = kind == PkgKind.PspTheme ? Path.Combine(pspemu, "PSP", "THEME") : Path.Combine(pspemu, "PSP", "GAME", id);
        log?.Invoke($"{kind.Describe()} {id} \"{title}\" ({pkg.ContentId}): no license needed");

        // what goes where: (entry, path relative to ux0)
        var plan = new List<(PkgEntry Entry, string Target)>();
        foreach (PkgEntry e in pkg.Entries)
        {
            if (e.IsDirectory)
            {
                continue;
            }

            if (kind == PkgKind.PspTheme)
            {
                // the theme files (.PTF), wherever the package keeps them (USRDIR/CONTENT or the root)
                if (e.Name.StartsWith(content, StringComparison.OrdinalIgnoreCase) || !e.Name.Contains('/'))
                {
                    string rel = e.Name.StartsWith(content, StringComparison.OrdinalIgnoreCase) ? e.Name[content.Length..] : e.Name;
                    if (rel.Equals("PARAM.SFO", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    plan.Add((e, Combine(dest, rel)));
                }
                continue;
            }
            if (!e.Name.StartsWith(content, StringComparison.OrdinalIgnoreCase))
            {
                continue; // the PS3 wrapper (PARAM.SFO, ICON0...)
            }

            string relPath = e.Name[content.Length..];
            if (kind == PkgKind.Psx && relPath.Equals("texture.enc", StringComparison.OrdinalIgnoreCase))
            {
                plan.Add((e, Combine(Path.Combine(ux0Root, "ps1emu", id), "texture.enc")));
            }
            else
            {
                plan.Add((e, Combine(dest, relPath)));
            }
        }

        long total = plan.Sum(p => (long)p.Entry.DataSize);
        var reporter = new InstallProgressReporter(progress, total);
        reporter.Flush();

        Thread thread = Thread.CurrentThread;
        ThreadPriority priority = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal;
        var written = new List<string>();
        try
        {
            byte[]? ebootHash = null;
            foreach ((PkgEntry? e, string? target) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                written.Add(target);
                using var outFile = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                using IncrementalHash? incrementalHash = e == eboot ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                pkg.Extract(e, new ProgressStream(outFile, incrementalHash, n =>
                {
                    reporter.Add(n, e.Name);
                    cancellationToken.ThrowIfCancellationRequested();
                }));
                if (incrementalHash != null)
                {
                    ebootHash = incrementalHash.GetHashAndReset();
                }

                log?.Invoke($"wrote {Path.GetRelativePath(ux0Root, target)}");
            }

            if (eboot != null && ebootHash != null && kind is not (PkgKind.PspDlc or PkgKind.PspTheme))
            {
                int sigKind = kind == PkgKind.Psx ? PspEbootSignature.KindPs1 : PspEbootSignature.KindPsp;
                string sigPath = Path.Combine(dest, "__sce_ebootpbp");
                written.Add(sigPath);
                File.WriteAllBytes(sigPath, PspEbootSignature.Create("EBOOT.PBP", ebootHash, sigKind));
                log?.Invoke($"wrote {Path.GetRelativePath(ux0Root, sigPath)} (eboot signature)");
            }
            // the license is named after the content id the eboot carries (DATA.PSP + 0x560, what the shell reads before
            // it starts the title), which can differ from the package's
            if (options.License is { } rif && kind != PkgKind.PspTheme)
            {
                string rifPath = Path.Combine(pspemu, "PSP", "LICENSE", licenseId + ".rif");
                Directory.CreateDirectory(Path.GetDirectoryName(rifPath)!);
                written.Add(rifPath);
                File.WriteAllBytes(rifPath, rif);
                log?.Invoke($"wrote {Path.GetRelativePath(ux0Root, rifPath)} (the user's license)");
            }
            if (options.ActDat is { } act)
            {
                // the console's activation data: tm0:npdrm/act.dat (next to ux0 in the virtual file system)
                string actPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ux0Root).TrimEnd(Path.DirectorySeparatorChar))!, "tm0", "npdrm", "act.dat");
                Directory.CreateDirectory(Path.GetDirectoryName(actPath)!);
                File.WriteAllBytes(actPath, act);
                log?.Invoke("wrote tm0:npdrm/act.dat (the user's activation data)");
            }
            else if (options.License == null && kind is not (PkgKind.PspTheme or PkgKind.PspDlc)
                     && !File.Exists(Path.Combine(pspemu, "PSP", "LICENSE", licenseId + ".rif")))
            {
                log?.Invoke($"no license for {licenseId}: the title is installed, the system will say it has no license");
            }
        }
        catch
        {
            foreach (string f in written)
            {
                try
                {
                    File.Delete(f);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            thread.Priority = priority;
        }
        reporter.Finish();

        // a new PSP/PSone title: the shell lists ux0:pspemu/PSP/GAME when it rebuilds the card's database
        if (kind is not (PkgKind.PspDlc or PkgKind.PspTheme))
        {
            AppMeta.InvalidateCardId(ux0Root, log);
        }

        PkgContentType type = kind switch
        {
            PkgKind.Psx => PkgContentType.Psx,
            PkgKind.PspDlc => PkgContentType.PspDlc,
            PkgKind.PspTheme => PkgContentType.PspTheme,
            _ => PkgContentType.Psp
        };
        return new InstalledTitle(id, title, dest, type) { Kind = kind };
    }

    private static PkgEntry? Find(PkgReader pkg, string name) =>
        pkg.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A path under <paramref name="root"/> from a package item name; names that leave it are refused.</summary>
    private static string Combine(string root, string rel)
    {
        string[] parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.Contains(':')))
        {
            throw new InvalidDataException($"The package has an invalid file name: {rel}");
        }

        return Path.Combine([root, .. parts]);
    }

    /// <summary>A title ID usable as a folder name (letters and digits, 4-16 chars), or null.</summary>
    private static string? SafeId(string id)
    {
        id = id.Trim();
        return id.Length is >= 4 and <= 16 && id.All(char.IsAsciiLetterOrDigit) ? id.ToUpperInvariant() : null;
    }

    /// <summary>The PBP header of an EBOOT.PBP: (PARAM.SFO offset, ICON0 offset, DATA.PSAR offset).</summary>
    private static (uint Sfo, uint Icon0, uint Psar) PbpHeader(PkgReader pkg, PkgEntry eboot)
    {
        if (eboot.DataSize < 0x28)
        {
            throw new InvalidDataException("EBOOT.PBP is too short");
        }

        Span<byte> h = stackalloc byte[0x28];
        pkg.ReadAt(eboot, 0, h);
        if (U32LE(h, 0) != 0x50425000)
        {
            throw new InvalidDataException("EBOOT.PBP: wrong PBP signature");
        }

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
            (uint sfoOff, uint icon0, uint _) = PbpHeader(pkg, eboot);
            if (icon0 <= sfoOff || icon0 - sfoOff > 0x10000 || icon0 > eboot.DataSize)
            {
                return null;
            }

            byte[] sfo = new byte[icon0 - sfoOff];
            pkg.ReadAt(eboot, sfoOff, sfo);
            return new Sfo(sfo);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The PARAM.SFO of the EBOOT.PBP of a PSP/PSone package (its title and DISC_ID), or null.</summary>
    internal static Sfo? EbootSfo(PkgReader pkg) =>
        pkg.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.Equals(ContentFolder(pkg) + "EBOOT.PBP", StringComparison.OrdinalIgnoreCase)) is { } eboot
            ? EbootSfo(pkg, eboot) : null;

    /// <summary>The DISC_ID of the PARAM.SFO inside the EBOOT.PBP (the folder name the Vita gives the title), or "".
    /// A broken PBP header is an error (the eboot would not run).</summary>
    private static string EbootDiscId(PkgReader pkg, PkgEntry eboot)
    {
        PbpHeader(pkg, eboot);
        return EbootSfo(pkg, eboot)?.GetString("DISC_ID") ?? "";
    }

    /// <summary>The content id an NPDRM eboot carries at DATA.PSP + 0x560 (PSP and PSone alike), or null.</summary>
    private static string? EbootContentId(PkgReader pkg, PkgEntry eboot)
    {
        if (eboot.DataSize < 0x28)
        {
            return null;
        }

        Span<byte> h = stackalloc byte[0x28];
        pkg.ReadAt(eboot, 0, h);
        uint dataPsp = U32LE(h, 0x20);
        if (U32LE(h, 0) != 0x50425000 || dataPsp + 0x560UL + 0x30 > eboot.DataSize)
        {
            return null;
        }

        byte[] id = new byte[0x30];
        pkg.ReadAt(eboot, dataPsp + 0x560, id);
        string s = CString(id);
        return s.Length == 36 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? s : null;
    }

    /// <summary>A write-through stream that hashes and counts what passes.</summary>
    private sealed class ProgressStream(Stream inner, IncrementalHash? hash, Action<long> written) : Stream
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
