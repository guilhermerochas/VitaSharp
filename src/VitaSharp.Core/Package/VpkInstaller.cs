using System.IO.Compression;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Package.Pfs;

namespace VitaSharp.Core.Package;

/// <summary>Where a piece of content from a VPK/zip goes in ux0, decided by its param.sfo CATEGORY.</summary>
public enum VpkContentKind
{
    /// <summary>"gd" (and any other non patch/add-on category): ux0:app/TITLEID.</summary>
    App,
    /// <summary>"gp": ux0:patch/TITLEID.</summary>
    Patch,
    /// <summary>"ac": ux0:addcont/TITLEID/&lt;content label&gt;.</summary>
    AddCont,
}

/// <summary>One installable content found inside an archive (a folder holding sce_sys/param.sfo).</summary>
public sealed record VpkContentInfo(
    string TitleId, string Title, string Category, string Version, VpkContentKind Kind,
    string ArchivePrefix, string DestinationPath)
{
    public bool AlreadyInstalled => Directory.Exists(DestinationPath);
}

public sealed record VpkInstallResult(VpkContentInfo Content, bool Installed, bool Replaced)
{
    public string TitleId => Content.TitleId;
    public string InstallPath => Content.DestinationPath;
}

public sealed class VpkInstallOptions
{
    /// <summary>
    /// Called (on the installing thread) when the destination already exists. Return true to replace it,
    /// false to skip this content. When null, existing content is replaced.
    /// </summary>
    public Func<VpkContentInfo, bool>? ConfirmOverwrite { get; init; }
    /// <summary>Called on the installing threads, at most every 100 ms.</summary>
    public Action<InstallProgress>? Progress { get; init; }
    public Action<string>? Log { get; init; }
    /// <summary>Optional NoNpDrm license (work.bin / .rif) for encrypted content that does not ship sce_sys/package/work.bin.</summary>
    public string? LicensePath { get; init; }
}

/// <summary>The archive (or one of its contents) is encrypted and no license to decrypt it was found.</summary>
public sealed class VpkLicenseException(string message) : Exception(message);

/// <summary>
/// Installs a .vpk (or any .zip) holding PS Vita content into the virtual ux0, following Vita3K's archive install:
/// every folder containing sce_sys/param.sfo is a content (the root, a nested subfolder, or a TITLEID folder
/// inside a "content" zip), routed by CATEGORY to ux0:app, ux0:patch or ux0:addcont. Each content is extracted
/// into a temporary folder under ux0 and moved into place only once complete.
/// </summary>
public static class VpkInstaller
{
    private const string SfoSuffix = "sce_sys/param.sfo";

    /// <summary>Lists the contents of an archive and where they would be installed, without extracting anything.</summary>
    public static IReadOnlyList<VpkContentInfo> Inspect(string archivePath, string ux0Root)
    {
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        return Inspect(zip, ux0Root);
    }

    public static IReadOnlyList<VpkContentInfo> Inspect(ZipArchive zip, string ux0Root)
    {
        ValidateEntryNames(zip);
        var sfoEntries = zip.Entries
            .Select(e => (entry: e, name: Normalize(e.FullName)))
            .Where(x => x.name.EndsWith(SfoSuffix, StringComparison.OrdinalIgnoreCase)
                        && (x.name.Length == SfoSuffix.Length || x.name[^(SfoSuffix.Length + 1)] == '/'))
            .Select(x => (x.entry, prefix: x.name[..^SfoSuffix.Length]))
            .OrderBy(x => x.prefix.Length)
            .ToList();

        var contents = new List<VpkContentInfo>();
        foreach ((ZipArchiveEntry? entry, string? prefix) in sfoEntries)
        {
            // a param.sfo nested inside content we already picked belongs to that content
            if (contents.Any(c => prefix.StartsWith(c.ArchivePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Sfo sfo;
            try
            {
                sfo = new Sfo(ReadAll(entry));
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                throw new InvalidDataException($"{entry.FullName} is not a valid PARAM.SFO", e);
            }
            contents.Add(Describe(sfo, prefix, ux0Root));
        }
        if (contents.Count == 0)
        {
            throw new InvalidDataException("No sce_sys/param.sfo found in the archive: this is not a PS Vita VPK/content zip");
        }

        foreach (VpkContentInfo? c in contents.Where(c => c.Kind == VpkContentKind.App))
        {
            if (!zip.Entries.Any(e => Normalize(e.FullName).Equals(c.ArchivePrefix + "eboot.bin", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"{c.TitleId}: the application has no eboot.bin");
            }
        }

        return contents;
    }

    private static VpkContentInfo Describe(Sfo sfo, string prefix, string ux0Root)
    {
        string titleId = sfo.TitleId.Trim();
        if (!IsSafeName(titleId))
        {
            throw new InvalidDataException($"param.sfo has an invalid TITLE_ID \"{titleId}\"");
        }

        string category = sfo.Category.Trim();
        VpkContentKind kind = category.Contains("gp", StringComparison.OrdinalIgnoreCase) ? VpkContentKind.Patch
                 : category.Contains("ac", StringComparison.OrdinalIgnoreCase) ? VpkContentKind.AddCont
                 : VpkContentKind.App;
        string dest = kind switch
        {
            VpkContentKind.Patch => Path.Combine(ux0Root, "patch", titleId),
            VpkContentKind.AddCont => Path.Combine(ux0Root, "addcont", titleId, AddContDir(sfo, prefix)),
            _ => Path.Combine(ux0Root, "app", titleId),
        };
        string version = (kind == VpkContentKind.App ? sfo.AppVersion : sfo.GetString("APP_VER", sfo.GetString("VERSION"))).Trim();
        return new VpkContentInfo(titleId, sfo.Title.Replace('\n', ' '), category, version, kind, prefix, Path.GetFullPath(dest));
    }

    /// <summary>Add-on content lives in a folder named after the content label (CONTENT_ID minus "XXYYYY-TITLEID_00-").</summary>
    private static string AddContDir(Sfo sfo, string prefix)
    {
        string cid = sfo.ContentId.Trim();
        if (cid.Length > 20 && IsSafeName(cid[20..]))
        {
            return cid[20..];
        }

        string folder = prefix.TrimEnd('/');
        folder = folder[(folder.LastIndexOf('/') + 1)..];
        if (IsSafeName(folder))
        {
            return folder;
        }

        throw new InvalidDataException($"Add-on content has no usable CONTENT_ID (\"{cid}\")");
    }

    private static bool IsSafeName(string s) => s.Length is > 0 and <= 64 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static string Normalize(string zipName) => zipName.Replace('\\', '/').TrimStart('/');

    /// <summary>Rejects the whole archive when any entry would escape its target folder (zip-slip).</summary>
    private static void ValidateEntryNames(ZipArchive zip)
    {
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            string raw = e.FullName.Replace('\\', '/');
            if (raw.StartsWith('/') || raw.Contains(':') || raw.Contains('\0')
                || raw.Split('/').Any(seg => seg == ".."))
            {
                throw new InvalidDataException($"Unsafe path in archive: \"{e.FullName}\"");
            }
        }
    }

    private static byte[] ReadAll(ZipArchiveEntry e)
    {
        if (e.Length > 16 << 20)
        {
            throw new InvalidDataException($"{e.FullName} is too large");
        }

        using Stream s = e.Open();
        using var ms = new MemoryStream((int)e.Length);
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ install

    public static IReadOnlyList<VpkInstallResult> Install(string archivePath, string ux0Root,
        VpkInstallOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Install(fs, ux0Root, options, cancellationToken);
    }

    public static IReadOnlyList<VpkInstallResult> Install(Stream archive, string ux0Root,
        VpkInstallOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VpkInstallOptions();
        ux0Root = Path.GetFullPath(ux0Root);
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        IReadOnlyList<VpkContentInfo> contents = Inspect(zip, ux0Root);

        // decide what to install before touching the disk
        var plan = new List<(VpkContentInfo info, bool replace)>();
        var results = new List<VpkInstallResult>();
        foreach (VpkContentInfo c in contents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool exists = c.AlreadyInstalled;
            if (exists && options.ConfirmOverwrite != null && !options.ConfirmOverwrite(c))
            {
                results.Add(new VpkInstallResult(c, false, false));
                continue;
            }
            plan.Add((c, exists));
        }

        var entriesOf = plan.ToDictionary(p => p.info, p => EntriesUnder(zip, p.info.ArchivePrefix));
        long total = entriesOf.Values.SelectMany(x => x).Sum(e => e.Length);
        var reporter = new InstallProgressReporter(options.Progress, total);
        reporter.Flush();

        Thread thread = Thread.CurrentThread;
        ThreadPriority priority = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal; // inflating; the writer thread is below normal too
        try
        {
            InstallPlan(plan, entriesOf, ux0Root, options, reporter, results, cancellationToken);
        }
        finally
        {
            thread.Priority = priority;
        }
        reporter.Finish();
        return results;
    }

    private static void InstallPlan(List<(VpkContentInfo info, bool replace)> plan, Dictionary<VpkContentInfo, List<ZipArchiveEntry>> entriesOf,
                            string ux0Root, VpkInstallOptions options, InstallProgressReporter reporter,
                            List<VpkInstallResult> results, CancellationToken cancellationToken)
    {
        foreach ((VpkContentInfo? info, bool replace) in plan)
        {
            string tmpRoot = Path.Combine(ux0Root, ".vpk-tmp");
            string tmp = Path.Combine(tmpRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                Extract(entriesOf[info], info.ArchivePrefix, tmp, reporter, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PrepareLicense(info, tmp, ux0Root, options);
                cancellationToken.ThrowIfCancellationRequested();
                MoveIntoPlace(tmp, info.DestinationPath, tmpRoot, options.Log);
                if (info.Kind != VpkContentKind.AddCont)
                {
                    AppMeta.Installed(ux0Root, info.TitleId, options.Log);
                }

                results.Add(new VpkInstallResult(info, true, replace));
            }
            finally
            {
                TryDelete(tmp);
                TryDeleteIfEmpty(tmpRoot);
            }
        }
    }

    private static List<ZipArchiveEntry> EntriesUnder(ZipArchive zip, string prefix)
        => zip.Entries.Where(e =>
        {
            string n = Normalize(e.FullName);
            return n.Length > prefix.Length && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }).ToList();

    /// <summary>Inflates the entries into <paramref name="dest"/>; the files are written on a second thread meanwhile.</summary>
    private static void Extract(List<ZipArchiveEntry> entries, string prefix, string dest, InstallProgressReporter reporter,
                        CancellationToken ct)
    {
        string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var writer = new FileWritePipeline(1 << 20, 4, n => reporter.Add(n), ct);
        foreach (ZipArchiveEntry e in entries)
        {
            ct.ThrowIfCancellationRequested();
            string rel = Normalize(e.FullName)[prefix.Length..];
            string target = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe path in archive: \"{e.FullName}\"");
            }

            if (rel.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            writer.Open(target, e.Length);
            using (Stream input = e.Open())
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] buf = writer.Rent();
                    int n = input.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
                    if (n > 0)
                    {
                        writer.Write(buf, n);
                    }
                    else
                    {
                        writer.Return(buf);
                    }

                    if (n < buf.Length)
                    {
                        break;
                    }
                }
            }
            writer.Close(lastWrite: e.LastWriteTime.Year > 1980 ? e.LastWriteTime.DateTime : null);
        }
        writer.Complete();
    }

    /// <summary>
    /// Makes sure encrypted content can be used: PFS-encrypted files are decrypted with the license, and an
    /// encrypted NPDRM eboot.bin needs a license the emulator can find. Throws <see cref="VpkLicenseException"/> if none.
    /// </summary>
    private static void PrepareLicense(VpkContentInfo info, string dir, string ux0Root, VpkInstallOptions options)
    {
        string workBin = Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/package/work.bin");
        string? license = FindLicense(info, dir, ux0Root, options.LicensePath);

        string eboot = Path.Combine(dir, "eboot.bin");
        if (PfsDecryptor.HasPfs(dir))
        {
            if (File.Exists(eboot) && !LooksEncrypted(eboot))
            {
                options.Log?.Invoke($"{info.TitleId}: sce_pfs present but files are already decrypted");
            }
            else if (license == null)
            {
                throw new VpkLicenseException(
                    $"{info.TitleId} is PFS-encrypted and no license was found. Include sce_sys/package/work.bin in the VPK " +
                    "(NoNpDrm dump) or provide the work.bin/.rif license (--license=<file>).");
            }
            else
            {
                options.Log?.Invoke($"{info.TitleId}: decrypting PFS with {Path.GetFileName(license)}");
                new PfsDecryptor(dir, PkgInstaller.ReadKlicensee(license)) { Log = options.Log }.DecryptInPlace();
            }
        }

        if (File.Exists(eboot) && EbootNeedsLicense(eboot) && license == null)
        {
            string msg = $"{info.TitleId}: eboot.bin is an encrypted NPDRM executable and no license was found. " +
                         "Use a NoNpDrm dump that includes sce_sys/package/work.bin, or provide the work.bin/.rif license (--license=<file>).";
            if (info.Kind == VpkContentKind.App)
            {
                throw new VpkLicenseException(msg);
            }

            options.Log?.Invoke("Warning: " + msg + " The base game's license will be used when it is installed.");
        }

        // keep a user-supplied license where the loader looks for it (app0:sce_sys/package/work.bin)
        if (license != null && options.LicensePath != null && license == options.LicensePath && !File.Exists(workBin)
            && info.Kind != VpkContentKind.AddCont)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workBin)!);
            File.Copy(license, workBin);
        }
    }

    private static string? FindLicense(VpkContentInfo info, string dir, string ux0Root, string? userLicense)
    {
        if (userLicense != null)
        {
            if (!File.Exists(userLicense))
            {
                throw new FileNotFoundException("License file not found", userLicense);
            }

            return userLicense;
        }
        var candidates = new List<string> { Io.Vfs.ResolveCaseInsensitive(dir, "sce_sys/package/work.bin") };
        if (info.Kind != VpkContentKind.App)
        {
            candidates.Add(Io.Vfs.ResolveCaseInsensitive(Path.Combine(ux0Root, "app", info.TitleId), "sce_sys/package/work.bin"));
        }

        string licDir = Path.Combine(ux0Root, "license", info.TitleId);
        if (Directory.Exists(licDir))
        {
            candidates.AddRange(Directory.GetFiles(licDir, "*.rif"));
        }

        return candidates.FirstOrDefault(p => File.Exists(p) && new FileInfo(p).Length >= 0x60);
    }

    private static byte[] ReadHead(string path, int max)
    {
        using FileStream fs = File.OpenRead(path);
        byte[] buf = new byte[(int)Math.Min(max, fs.Length)];
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>A PFS-encrypted eboot.bin is neither a SELF nor an ELF.</summary>
    private static bool LooksEncrypted(string eboot)
    {
        byte[] h = ReadHead(eboot, 16);
        return !SelfDecryptor.IsSelf(h) && !SelfDecryptor.IsElf(h);
    }

    private static bool EbootNeedsLicense(string eboot) => SelfDecryptor.RequiresKlicensee(ReadHead(eboot, 0x10000));

    /// <summary>Swaps the freshly extracted folder in; the previous install is kept until the move succeeded.</summary>
    private static void MoveIntoPlace(string tmp, string dest, string tmpRoot, Action<string>? log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        string? backup = null;
        if (Directory.Exists(dest))
        {
            backup = Path.Combine(tmpRoot, Guid.NewGuid().ToString("N") + "-old");
            Directory.Move(dest, backup);
        }
        try
        {
            Directory.Move(tmp, dest);
        }
        catch
        {
            if (backup != null)
            {
                Directory.Move(backup, dest);
            }

            throw;
        }
        if (backup != null && !TryDelete(backup))
        {
            log?.Invoke($"Could not remove the previous version at {backup}");
        }
    }

    private static bool TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
