using VitaSharp.Core.Io;

namespace VitaSharp.Core.Package;

/// <summary>
/// Earlier versions kept a title's licences in ux0:license/&lt;title id&gt; (Vita3K's folder); the firmware keeps them in
/// ux0:license/app/&lt;title id&gt; (an additional content's in ux0:license/addcont/&lt;title id&gt;/&lt;content folder&gt;),
/// <see cref="FirmwareLayout.ApplicationLicenses"/>. <see cref="Run"/> moves the old ones there once: every .rif of an
/// old folder goes to the folder of what it licenses (an add-on whose content folder is installed in ux0:addcont, else
/// the application). A file whose place already has the same file is dropped; one whose place has a different file stays
/// where it was (logged), as does anything that is not a .rif, so nothing is lost. An emptied old folder is removed.
/// Linked folders (another data folder's, shared by a test data folder) are not touched.
/// </summary>
public static class LicenseMigration
{
    private static readonly object s_lock = new();

    /// <summary>The folders of ux0:license that are the firmware's own, not an old per-title folder.</summary>
    private static readonly string[] s_firmwareFolders = [Leaf(FirmwareLayout.ApplicationLicenses), Leaf(FirmwareLayout.AddContLicenses), Leaf(FirmwareLayout.ThemeLicenses)];

    private static string Leaf(FirmwareFolder f) => f.Folder[(f.Folder.LastIndexOf('/') + 1)..];

    /// <summary>The offset of the content ID in a PS Vita licence.</summary>
    private const int RifContentIdOffset = 0x10;

    /// <summary>Moves the licences of the old layout under <paramref name="fsRoot"/>; returns how many moved.</summary>
    public static int Run(string fsRoot, Action<string>? log = null)
    {
        string root = FirmwareLayout.Licenses.Host(fsRoot);
        lock (s_lock)
        {
            // a linked folder is another data folder's (a test data folder sharing the user's): left alone
            if (!Directory.Exists(root) || IsLink(root) || IsLink(Path.GetDirectoryName(root)!))
            {
                return 0;
            }

            int moved = 0;
            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
            foreach (string dir in folders)
            {
                string titleId = Path.GetFileName(dir);
                if (s_firmwareFolders.Contains(titleId, StringComparer.OrdinalIgnoreCase) || IsLink(dir))
                {
                    continue;
                }

                moved += MoveFolder(fsRoot, dir, titleId, log);
            }
            return moved;
        }
    }

    private static int MoveFolder(string fsRoot, string dir, string titleId, Action<string>? log)
    {
        int moved = 0;
        foreach (string rif in Directory.GetFiles(dir, "*.rif"))
        {
            string target = Path.Combine(TargetFolder(fsRoot, titleId, rif), Path.GetFileName(rif));
            try
            {
                if (File.Exists(target))
                {
                    if (File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(rif)))
                    {
                        File.Delete(rif);
                    }
                    else
                    {
                        log?.Invoke($"licence {Path.GetRelativePath(fsRoot, rif)} kept: {Path.GetRelativePath(fsRoot, target)} differs");
                    }
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(rif, target);
                moved++;
                log?.Invoke($"licence {Path.GetRelativePath(fsRoot, rif)} moved to {Path.GetRelativePath(fsRoot, target)}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"licence {Path.GetRelativePath(fsRoot, rif)} not moved ({e.Message})");
            }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"licence folder {Path.GetRelativePath(fsRoot, dir)} not removed ({e.Message})");
        }
        return moved;
    }

    /// <summary>Where a licence of <paramref name="titleId"/> belongs: its add-on's folder when its content label names
    /// an installed add-on (ux0:addcont/&lt;id&gt;/&lt;label&gt;), else the application's.</summary>
    private static string TargetFolder(string fsRoot, string titleId, string rif)
    {
        string contentId = ContentId(rif);
        // "XXYYYY-TITLEID_00-LABEL": the label names the add-on's folder
        if (contentId.Length > 20)
        {
            string label = contentId[20..];
            if (Directory.Exists(FirmwareLayout.AddCont.Host(fsRoot, titleId, label)))
            {
                return FirmwareLayout.AddContLicenses.Host(fsRoot, titleId, label);
            }
        }
        return FirmwareLayout.ApplicationLicenses.Host(fsRoot, titleId);
    }

    private static bool IsLink(string dir) => new DirectoryInfo(dir).LinkTarget != null;

    private static string ContentId(string rif)
    {
        try
        {
            using FileStream stream = File.OpenRead(rif);
            byte[] head = new byte[RifContentIdOffset + 0x30];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
            {
                return "";
            }

            return System.Text.Encoding.ASCII.GetString(head, RifContentIdOffset, 0x30).TrimEnd('\0');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
