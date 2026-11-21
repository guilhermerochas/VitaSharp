using VitaSharp.Core.Io;

namespace VitaSharp.Core.Package;

/// <summary>
/// The PSM license of an installed title is ux0:psm/&lt;id&gt;/RO/License/&lt;name&gt;, where &lt;name&gt; is what the
/// firmware's scePsmDrmGetRifName gives for the license's account (the AES name of SceNpDrm,
/// <see cref="PsmPkgInstaller.LicenseFileName"/>). Earlier versions of the PSM installer stored every license under the
/// fixed name of the PS Vita licenses (<see cref="PkgInstaller.AccountRifName"/>). <see cref="Run"/> gives those files
/// the firmware's name once (idempotent): the license is copied to its name, the copy is compared with the original,
/// and only then is the original removed; a different file already under the new name keeps both (logged). Files with
/// other names (a plugin's own, for example) are not touched, nor are linked folders (another data folder's, shared by
/// a test data folder). Without the installed firmware the name cannot be computed and nothing changes.
/// </summary>
public static class PsmLicenseNameMigration
{
    private static readonly object s_lock = new();

    /// <summary>Renames the licenses under <paramref name="fsRoot"/>; returns how many got the firmware's name.</summary>
    public static int Run(string fsRoot, Action<string>? log = null) =>
        Run(fsRoot, Hle.Modules.SystemServicesVsh.RifNameKeyFor(fsRoot), log);

    /// <summary>The same with the SceNpDrm license name key <paramref name="key"/> (null: no firmware, nothing done).</summary>
    internal static int Run(string fsRoot, byte[]? key, Action<string>? log)
    {
        if (key == null)
        {
            return 0;
        }

        string psmRoot = FirmwareLayout.PsmTitles.Host(fsRoot);
        lock (s_lock)
        {
            if (!Directory.Exists(psmRoot) || IsLink(psmRoot))
            {
                return 0;
            }

            string[] titles;
            try
            {
                titles = Directory.GetDirectories(psmRoot);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return 0;
            }

            int renamed = 0;
            foreach (string title in titles)
            {
                if (IsLink(title))
                {
                    continue;
                }

                string old = Path.Combine(title, FirmwareLayout.PsmReadOnly, FirmwareLayout.PsmLicense, PkgInstaller.AccountRifName);
                try
                {
                    if (File.Exists(old) && Rename(fsRoot, key, old, log))
                    {
                        renamed++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"PSM license {Path.GetRelativePath(fsRoot, old)} not renamed ({e.Message})");
                }
            }

            return renamed;
        }
    }

    private static bool Rename(string fsRoot, byte[] key, string old, Action<string>? log)
    {
        byte[] license = File.ReadAllBytes(old);
        if (PsmPkgInstaller.LicenseContentId(license).Length == 0)
        {
            return false;
        }

        if (Hle.Modules.SystemServicesVsh.PsmRifFileName(key, license) is not { } name
            || string.Equals(name, PkgInstaller.AccountRifName, StringComparison.Ordinal))
        {
            return false;
        }

        string target = Path.Combine(Path.GetDirectoryName(old)!, name);
        if (File.Exists(target))
        {
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(license))
            {
                File.Delete(old);
                log?.Invoke($"PSM license {Path.GetRelativePath(fsRoot, old)} removed: {name} has the same license");
                return true;
            }

            log?.Invoke($"PSM license {Path.GetRelativePath(fsRoot, old)} kept: {name} is a different license");
            return false;
        }

        File.Copy(old, target);
        if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(license))
        {
            File.Delete(target);
            log?.Invoke($"PSM license {Path.GetRelativePath(fsRoot, old)} kept: its copy to {name} did not match");
            return false;
        }

        File.Delete(old);
        log?.Invoke($"PSM license {Path.GetRelativePath(fsRoot, old)} renamed to {name} (the firmware's name)");
        return true;
    }

    private static bool IsLink(string dir) => new DirectoryInfo(dir).LinkTarget != null;
}
