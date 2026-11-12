namespace VitaSharp.Core.Package;

/// <summary>
/// Installs a PlayStation Mobile package (content type 0x18, or 0x1D for a PSM title built with Unity) the way the
/// console lays it out, so the PSM runtime finds it (sources: pkg2zip's PSM unpacker, mmozeiko/pkg2zip; the
/// "Decompiling PlayStation Mobile games" notes of retroreversing.com; psvitadevwiki "PlayStation Mobile"):
/// <code>
/// ux0:psm/&lt;TITLEID&gt;/RO/...                 the package's "contents/..." items (Application/app.exe, app.info, ...)
/// ux0:psm/&lt;TITLEID&gt;/RO/License/&lt;name&gt;.rif   the user's license (the 1024-byte PSM .rif of their account)
/// ux0:psm/&lt;TITLEID&gt;/RW/Documents, RW/Temp  the title's writable folders (saved data, scratch)
/// ux0:psm/&lt;TITLEID&gt;/RW/System/content_id   the package's content ID (0x30 bytes, as in the package header)
/// ux0:psm/&lt;TITLEID&gt;/RW/System/pm.dat       64 KiB, zero when new (the runtime's own data)
/// ux0:psm/&lt;TITLEID&gt;/&lt;other&gt;               items outside "content/" keep their place (a Unity title's runtime/)
/// </code>
/// Only the package's own AES-CTR layer is removed: the files stay as sold (the game's PSSE protection is the runtime's
/// to open with the license at run time, as on the console). A reinstall replaces RO and keeps RW (the saved data).
/// The shell finds PSM titles when it rebuilds its database, which the install requests (ux0:id.dat reset): its rebuild
/// (dbrecovery_plugin) lists ux0:psm and the shell reads each title from RO/Application/app.info.
/// The folders are <see cref="Io.FirmwareLayout"/>'s.
/// </summary>
public static class PsmPkgInstaller
{
    /// <summary>The size of a PlayStation Mobile license (.rif); a PS Vita one has 512 bytes.</summary>
    public const int RifSize = 1024;

    /// <summary>Where a PSM license keeps its content ID (a PS Vita license has it at 0x10).</summary>
    public const int RifContentIdOffset = 0x50;

    // the package's read-only items, which go to RO: "contents/Application/app.exe" in a real store package (NPPA00120
    // installed with only "content/" taken left ux0:psm/<id>/contents/Application/app.exe; the shell's own PSM promote
    // works on "ux0:temp/promote" + "/contents"); pkg2zip skips the first 8 characters of every item, which takes
    // "content/" and leaves "/Application/..." of "contents/", so both spellings go to RO
    private static readonly string[] s_contentFolders = ["contents", "content"];
    private const int PmDatSize = 1 << 16;

    /// <summary>The content ID a PSM license is for, or "" when <paramref name="rif"/> is not one.</summary>
    public static string LicenseContentId(ReadOnlySpan<byte> rif)
    {
        if (rif.Length < RifSize)
        {
            return "";
        }

        return System.Text.Encoding.ASCII.GetString(rif.Slice(RifContentIdOffset, 36)).TrimEnd('\0');
    }

    /// <summary>The folder of an installed PSM title.</summary>
    public static string TitleRoot(string ux0Root, string titleId) => Io.FirmwareLayout.PsmTitles.OnDevice(ux0Root, titleId);

    /// <summary>Installs <paramref name="pkg"/> (a PSM package; disposed here) with the user's PSM license.</summary>
    internal static InstalledTitle Install(PkgReader pkg, byte[] license, string ux0Root, Action<string>? log,
                                           Action<InstallProgress>? progress, CancellationToken ct)
    {
        using (pkg)
        {
            CheckLicense(pkg, license);
            Sfo? sfo = pkg.ParamSfo.Length > 0 ? new Sfo(pkg.ParamSfo) : null;
            string titleId = PspPkgInstaller.SafeId(sfo?.TitleId is { Length: > 0 } t ? t : pkg.TitleId)
                ?? throw new InvalidDataException($"The package has no usable title ID ({pkg.ContentId})");
            string title = sfo?.Title is { Length: > 0 } name ? name : pkg.ContentId;
            string root = TitleRoot(ux0Root, titleId);
            string ro = Path.Combine(root, Io.FirmwareLayout.PsmReadOnly);

            Thread thread = Thread.CurrentThread;
            ThreadPriority priority = thread.Priority;
            thread.Priority = ThreadPriority.BelowNormal;
            try
            {
                if (Directory.Exists(ro))
                {
                    Directory.Delete(ro, true);
                }

                Directory.CreateDirectory(ro);
                try
                {
                    Extract(pkg, root, ro, log, progress, ct);
                }
                catch
                {
                    try
                    {
                        Directory.Delete(ro, true);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    throw;
                }
            }
            finally
            {
                thread.Priority = priority;
            }

            string licenseDir = Path.Combine(ro, Io.FirmwareLayout.PsmLicense);
            Directory.CreateDirectory(licenseDir);
            File.WriteAllBytes(Path.Combine(licenseDir, PkgInstaller.AccountRifName), license);

            string rw = Path.Combine(root, Io.FirmwareLayout.PsmWritable);
            string system = Path.Combine(rw, "System");
            Directory.CreateDirectory(Path.Combine(rw, "Documents"));
            Directory.CreateDirectory(Path.Combine(rw, "Temp"));
            Directory.CreateDirectory(system);
            byte[] contentId = new byte[0x30];
            System.Text.Encoding.ASCII.GetBytes(pkg.ContentId).AsSpan(0, Math.Min(pkg.ContentId.Length, 0x30)).CopyTo(contentId);
            File.WriteAllBytes(Path.Combine(system, "content_id"), contentId);
            string pmDat = Path.Combine(system, "pm.dat");
            if (!File.Exists(pmDat))
            {
                File.WriteAllBytes(pmDat, new byte[PmDatSize]);
            }

            // the package's param.sfo has no title for a PSM title: its name is in its own description, as the shell
            // shows it
            if (sfo?.Title is not { Length: > 0 } && PsmAppInfo.Load(Path.Combine(ro, Io.FirmwareLayout.PsmAppInfo))?.Name() is { Length: > 0 } appName)
            {
                title = appName;
            }

            AppMeta.InvalidateCardId(ux0Root, log);
            log?.Invoke($"PlayStation Mobile title {titleId} installed to {root}");
            return new InstalledTitle(titleId, title, root, PkgContentType.Psm) { Kind = PkgKind.VitaPsm };
        }
    }

    /// <summary>Refuses anything but the PSM license of this package (a PS Vita 512-byte license, or another title's).</summary>
    private static void CheckLicense(PkgReader pkg, byte[] license)
    {
        if (license.Length < RifSize)
        {
            throw new InvalidDataException($"A PlayStation Mobile package needs its PSM license (a {RifSize}-byte .rif or its zRIF from " +
                                           $"the user's account); the one given has {license.Length} bytes");
        }

        string licContent = LicenseContentId(license);
        if (licContent != pkg.ContentId)
        {
            throw new InvalidDataException($"The license is for {(licContent.Length > 0 ? licContent : "an unknown content ID")}, the package is {pkg.ContentId}");
        }
    }

    /// <summary>The install path of a package item: "contents/..." (or "content/...") under RO, anything else under the
    /// title's folder.</summary>
    private static string Target(string root, string ro, string name)
    {
        foreach (string folder in s_contentFolders)
        {
            if (name.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                return ro;
            }

            if (name.Length > folder.Length && name[folder.Length] == '/' && name.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                return PspPkgInstaller.Combine(ro, name[(folder.Length + 1)..]);
            }
        }

        return PspPkgInstaller.Combine(root, name);
    }

    private static void Extract(PkgReader pkg, string root, string ro, Action<string>? log, Action<InstallProgress>? progress,
                                CancellationToken ct)
    {
        var files = pkg.Entries.Where(e => !e.IsDirectory).ToList();
        InstallProgressReporter reporter = new(progress, files.Sum(e => (long)e.DataSize));
        reporter.Flush();
        foreach (PkgEntry e in pkg.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string target = Target(root, ro, e.Name);
            if (e.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                pkg.Extract(e, output, n => reporter.Add(n, e.Name));
            }
        }
        log?.Invoke($"Extracted {files.Count} files to {root}");
        reporter.Finish();
    }
}
