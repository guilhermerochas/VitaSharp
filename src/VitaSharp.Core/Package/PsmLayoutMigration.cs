using VitaSharp.Core.Io;

namespace VitaSharp.Core.Package;

/// <summary>
/// Earlier versions of the PSM installer left a Unity title's own runtime inside RO (ux0:psm/&lt;id&gt;/RO/runtime, the
/// package's "contents/runtime"), where the console's promote never leaves it: it moves it to the title's folder
/// (<see cref="FirmwareLayout.PsmOwnRuntime"/>) and stores the runtime's licence in ux0:license/app/&lt;id&gt;
/// (<see cref="PsmPkgInstaller.PrepareOwnRuntime"/>). Without
/// that the shell took the title for a plain PSM one and asked for a PSM Runtime Package update it can never satisfy.
/// <see cref="Run"/> brings those titles to the console's layout once and, when it changed one, makes the shell
/// rebuild the memory card's part of its database (so the title is registered again, as a Unity one). Linked folders
/// (another data folder's, shared by a test data folder) are not touched.
/// </summary>
public static class PsmLayoutMigration
{
    private static readonly object s_lock = new();

    /// <summary>Moves the misplaced runtimes under <paramref name="fsRoot"/>; returns how many titles changed.</summary>
    public static int Run(string fsRoot, Action<string>? log = null)
    {
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

            string ux0 = Path.Combine(fsRoot, FirmwareLayout.PsmTitles.Device);
            int changed = 0;
            foreach (string title in titles)
            {
                if (IsLink(title))
                {
                    continue;
                }

                try
                {
                    if (MoveRuntime(title, log) | PsmPkgInstaller.PrepareOwnRuntime(ux0, Path.GetFileName(title), log))
                    {
                        changed++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"PSM title {Path.GetFileName(title)}: runtime not prepared ({e.Message})");
                }
            }
            if (changed > 0)
            {
                AppMeta.InvalidateCardId(ux0, log);
            }

            return changed;
        }
    }

    /// <summary>RO/runtime to the title's folder, when the title has no runtime there yet.</summary>
    private static bool MoveRuntime(string title, Action<string>? log)
    {
        string misplaced = Vfs.ResolveCaseInsensitive(Path.Combine(title, FirmwareLayout.PsmReadOnly), FirmwareLayout.PsmOwnRuntime);
        string target = Path.Combine(title, FirmwareLayout.PsmOwnRuntime);
        if (!Directory.Exists(misplaced) || Directory.Exists(Vfs.ResolveCaseInsensitive(title, FirmwareLayout.PsmOwnRuntime)))
        {
            return false;
        }

        Directory.Move(misplaced, target);
        log?.Invoke($"PSM title {Path.GetFileName(title)}: its own runtime moved out of {FirmwareLayout.PsmReadOnly} to {target}");
        return true;
    }

    private static bool IsLink(string dir) => new DirectoryInfo(dir).LinkTarget != null;
}
