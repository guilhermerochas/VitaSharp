using VitaSharp.Core.Package.Pfs;

namespace VitaSharp.Core.Package;

public sealed record InstalledTitle(string TitleId, string Title, string Path, PkgContentType Type)
{
    /// <summary>The kind of package installed (null for VPKs).</summary>
    public PkgKind? Kind { get; init; }
    /// <summary>The decrypted UMD image a PSP install also wrote (ux0:pspemu/ISO), if any.</summary>
    public string? IsoPath { get; init; }
}

/// <summary>What a .pkg is, read from its header before installing (the UIs ask for a license only when it needs one).</summary>
public sealed record PkgInfo(PkgKind Kind, string ContentId, string TitleId, string Title)
{
    public bool NeedsLicense => Kind.NeedsLicense();
    public string Description => Kind.Describe();
}

/// <summary>A PS Vita package was given without its license.</summary>
public sealed class LicenseRequiredException(string message) : InvalidOperationException(message);

/// <summary>Installs a retail .pkg into the virtual ux0: a PS Vita package with its NoNpDrm license (work.bin / .rif /
/// zRIF), or a PSP/PSone package, which needs none (<see cref="PspPkgInstaller"/>).</summary>
public static class PkgInstaller
{
    /// <summary>Reads what <paramref name="pkgPath"/> is (kind, IDs, title) without installing it.</summary>
    public static PkgInfo Inspect(string pkgPath) => Inspect(new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));

    /// <summary>Reads what the package in <paramref name="pkgStream"/> is (seekable; disposed here).</summary>
    public static PkgInfo Inspect(Stream pkgStream)
    {
        using var pkg = new PkgReader(pkgStream);
        var kind = pkg.Kind;
        var sfo = pkg.ParamSfo.Length > 0 ? new Sfo(pkg.ParamSfo) : null;
        string titleId = sfo?.TitleId is { Length: > 0 } t ? t : pkg.TitleId;
        return new PkgInfo(kind, pkg.ContentId, titleId, sfo?.Title is { Length: > 0 } title ? title : pkg.ContentId);
    }

    /// <summary>Installs a PSP/PSone package, which needs no license (a Vita package is refused with
    /// <see cref="LicenseRequiredException"/>).</summary>
    public static InstalledTitle InstallWithoutLicense(string pkgPath, string ux0Root, PspInstallOptions? options = null,
                                                       Action<string>? log = null, Action<InstallProgress>? progress = null,
                                                       CancellationToken cancellationToken = default) =>
        InstallWithoutLicense(new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20), ux0Root, options, log, progress, cancellationToken);

    /// <summary>Installs the PSP/PSone package read from <paramref name="pkgStream"/> (seekable; disposed here).</summary>
    public static InstalledTitle InstallWithoutLicense(Stream pkgStream, string ux0Root, PspInstallOptions? options = null,
                                                       Action<string>? log = null, Action<InstallProgress>? progress = null,
                                                       CancellationToken cancellationToken = default)
    {
        var pkg = new PkgReader(pkgStream);
        PkgKind kind;
        try { kind = pkg.Kind; }
        catch { pkg.Dispose(); throw; }
        if (kind.NeedsLicense())
        {
            pkg.Dispose();
            throw new LicenseRequiredException($"This is a {kind.Describe()} package ({pkg.ContentId}): it needs its license (work.bin, .rif or zRIF)");
        }
        return PspPkgInstaller.Install(pkg, ux0Root, options, log, progress, cancellationToken);
    }

    public static byte[] ReadKlicensee(string licensePath)
    {
        var rif = File.ReadAllBytes(licensePath);
        if (rif.Length < 0x60) throw new InvalidDataException("License file is too small");
        return rif.AsSpan(0x50, 16).ToArray();
    }

    /// <summary>Reads a license given as a .rif/work.bin path or as a zRIF string.</summary>
    public static byte[] ReadLicense(string licenseOrZrif)
    {
        if (File.Exists(licenseOrZrif)) return File.ReadAllBytes(licenseOrZrif);
        if (Zrif.LooksLikeZrif(licenseOrZrif)) return Zrif.Decode(licenseOrZrif);
        throw new FileNotFoundException($"License not found (not a file nor a zRIF): {licenseOrZrif}");
    }

    public static InstalledTitle Install(string pkgPath, string licensePath, string ux0Root,
                                         Action<string>? log = null, Action<InstallProgress>? progress = null,
                                         CancellationToken cancellationToken = default) =>
        Install(pkgPath, ReadLicense(licensePath), ux0Root, log, progress, cancellationToken);

    /// <summary>Installs <paramref name="pkgPath"/> with the license bytes (.rif/work.bin contents or a decoded zRIF).</summary>
    public static InstalledTitle Install(string pkgPath, byte[] license, string ux0Root,
                                         Action<string>? log = null, Action<InstallProgress>? progress = null,
                                         CancellationToken cancellationToken = default) =>
        Install(new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20), license, ux0Root, log, progress, cancellationToken);

    /// <summary>
    /// Installs the package read from <paramref name="pkgStream"/> (seekable; disposed here). Each file is read,
    /// decrypted (the package's AES-CTR layer, then the PFS layer with the license's klicensee) and written once; the
    /// writes run on a second thread while the next chunk is read, both below normal priority.
    /// </summary>
    public static InstalledTitle Install(Stream pkgStream, byte[] license, string ux0Root,
                                         Action<string>? log = null, Action<InstallProgress>? progress = null,
                                         CancellationToken cancellationToken = default)
    {
        var reader = new PkgReader(pkgStream);
        PkgKind kind;
        try { kind = reader.Kind; }
        catch { reader.Dispose(); throw; }
        if (kind.IsPspEmu())
        {
            log?.Invoke($"{kind.Describe()} package: it needs no license, the one given is not used");
            return PspPkgInstaller.Install(reader, ux0Root, null, log, progress, cancellationToken);
        }
        if (kind == PkgKind.VitaPsm) { reader.Dispose(); throw new NotSupportedException("PlayStation Mobile packages are not supported yet"); }
        if (license.Length < 0x60) { reader.Dispose(); throw new InvalidDataException("License file is too small"); }
        using var pkg = reader;
        string licContent = Zrif.ContentId(license);
        if (licContent.Length == 36 && licContent != pkg.ContentId)
            throw new InvalidDataException($"The license is for {licContent}, the package is {pkg.ContentId}");
        var sfo = new Sfo(pkg.ParamSfo);
        var type = pkg.ContentType;
        if (type == PkgContentType.App && sfo.Category == "gp") type = PkgContentType.Patch;

        string titleId = sfo.TitleId.Length > 0 ? sfo.TitleId : pkg.TitleId;
        string dest = type switch
        {
            PkgContentType.App => Path.Combine(ux0Root, "app", titleId),
            PkgContentType.Patch => Path.Combine(ux0Root, "patch", titleId),
            PkgContentType.Dlc => Path.Combine(ux0Root, "addcont", titleId, pkg.ContentId[20..]),
            _ => Path.Combine(ux0Root, "theme", pkg.ContentId),
        };

        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal; // the reader/decryptor; the writer thread is below normal too
        try
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);
            try
            {
                Extract(pkg, license.AsSpan(0x50, 16).ToArray(), dest, log, progress, cancellationToken);
            }
            catch
            {
                try { Directory.Delete(dest, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
        finally { thread.Priority = priority; }

        var licDir = Path.Combine(ux0Root, "license", titleId);
        Directory.CreateDirectory(licDir);
        File.WriteAllBytes(Path.Combine(licDir, pkg.ContentId + ".rif"), license);
        Directory.CreateDirectory(Path.Combine(dest, "sce_sys", "package"));
        File.WriteAllBytes(Path.Combine(dest, "sce_sys", "package", "work.bin"), license);
        if (type is PkgContentType.App or PkgContentType.Patch) AppMeta.Installed(ux0Root, titleId, log);
        return new InstalledTitle(titleId, sfo.Title, dest, type) { Kind = kind };
    }

    static PkgEntry? FindEntry(PkgReader pkg, string name) =>
        pkg.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static byte[] ReadEntry(PkgReader pkg, PkgEntry e)
    {
        var ms = new MemoryStream((int)e.DataSize);
        pkg.Extract(e, ms);
        return ms.ToArray();
    }

    static void Extract(PkgReader pkg, byte[] klicensee, string dest, Action<string>? log,
                        Action<InstallProgress>? progress, CancellationToken ct)
    {
        // The PFS image (sce_pfs/files.db + unicv.db) is read first, so the files can be decrypted as they are written.
        PfsPlan? pfs = null;
        if (FindEntry(pkg, "sce_pfs/files.db") is { } filesDb)
        {
            var unicv = FindEntry(pkg, "sce_pfs/unicv.db")
                ?? throw new InvalidDataException("PFS: the package has sce_pfs/files.db but no sce_pfs/unicv.db");
            pfs = new PfsDecryptor(klicensee).Plan(ReadEntry(pkg, filesDb), ReadEntry(pkg, unicv));
        }

        bool Skipped(PkgEntry e) => pfs != null && (e.Name.StartsWith("sce_pfs/", StringComparison.OrdinalIgnoreCase)
                                                   || e.Name.Equals("sce_pfs", StringComparison.OrdinalIgnoreCase));
        var files = pkg.Entries.Where(e => !e.IsDirectory && !Skipped(e)).ToList();
        long total = files.Sum(e => (long)e.DataSize);
        var reporter = new InstallProgressReporter(progress, total);
        reporter.Flush();

        TimeSpan readTime = default, ctrTime = default, pfsTime = default;
        int decrypted = 0, plain = 0;
        using var ctr = pkg.CreateCipher();
        using (var writer = new FileWritePipeline(PfsPlan.ChunkBytes, 4, n => reporter.Add(n), ct))
        {
            foreach (var e in pkg.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (Skipped(e)) continue;
                var target = Path.Combine(dest, e.Name.Replace('/', Path.DirectorySeparatorChar));
                if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }

                long size = (long)e.DataSize;
                PfsTable? table = null;
                PfsNode? node = null;
                if (pfs != null && size > 0 && PfsPlan.IsCandidatePath(e.Name) && pfs.HeadBytes > 0)
                {
                    var head = writer.Rent();
                    try
                    {
                        var h = head.AsSpan(0, (int)Math.Min(size, Math.Min(pfs.HeadBytes, head.Length)));
                        var (r, d) = pkg.ReadData(e, 0, h, ctr);
                        readTime += r; ctrTime += d;
                        table = pfs.Match(size, h);
                    }
                    finally { writer.Return(head); }
                    if (table != null) node = pfs.Node(e.Name);
                }
                var cipher = table != null && node!.Encrypted ? pfs!.Cipher(table) : null;
                if (cipher != null) { decrypted++; log?.Invoke($"decrypt {e.Name}"); }
                else if (table != null) { plain++; log?.Invoke($"plain   {e.Name}"); }

                // Trim the sector padding of PFS files back to their logical size (files.db).
                long keep = node != null ? Math.Min(size, node.Size) : size;
                writer.Open(target, keep);
                int chunk = cipher?.ChunkSize(writer.BufferSize) ?? writer.BufferSize;
                if (chunk > writer.BufferSize) throw new NotSupportedException($"PFS sector size {table!.SectorSize} is too large");
                for (long pos = 0; pos < size; pos += chunk)
                {
                    int n = (int)Math.Min(chunk, size - pos);
                    var buf = writer.Rent();
                    var span = buf.AsSpan(0, n);
                    var (r, d) = pkg.ReadData(e, pos, span, ctr);
                    readTime += r; ctrTime += d;
                    if (cipher != null)
                    {
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        cipher.Decrypt(span, pos);
                        pfsTime += System.Diagnostics.Stopwatch.GetElapsedTime(t0);
                    }
                    int write = (int)Math.Clamp(keep - pos, 0, n);
                    if (write > 0) writer.Write(buf, write);
                    else writer.Return(buf);
                    ct.ThrowIfCancellationRequested();
                }
                writer.Close(node != null ? node.Size : null);
            }
            pfs?.EnsureAllMatched();
            writer.Complete();

            double mb = writer.BytesWritten / (1024.0 * 1024);
            string Rate(TimeSpan t) => t.TotalSeconds > 0 ? $"{mb / t.TotalSeconds:N0} MB/s" : "-";
            log?.Invoke($"Extracted {pkg.Entries.Count} entries to {dest}" +
                        (pfs != null ? $" ({decrypted} PFS-decrypted, {plain} plain)" : ""));
            log?.Invoke($"Install: {mb:N0} MB in {reporter.Elapsed.TotalSeconds:N1} s ({Rate(reporter.Elapsed)}); " +
                        $"read {readTime.TotalSeconds:N1} s ({Rate(readTime)}), AES-CTR {ctrTime.TotalSeconds:N1} s ({Rate(ctrTime)}), " +
                        $"PFS {pfsTime.TotalSeconds:N1} s ({Rate(pfsTime)}), write {writer.WriteTime.TotalSeconds:N1} s ({Rate(writer.WriteTime)})");
        }
        reporter.Finish();
    }
}
