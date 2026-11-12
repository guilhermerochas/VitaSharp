using VitaSharp.Core.Io;

namespace VitaSharp.Core.Package;

/// <summary>An installed title as a launcher's library shows it.</summary>
/// <param name="Kind">what it is (where the firmware keeps it)</param>
/// <param name="Dir">its folder (ux0:app/&lt;id&gt;, ux0:psm/&lt;id&gt;...)</param>
/// <param name="IconPath">a PNG of its icon, or null</param>
public sealed record LibraryTitle(ContentKind Kind, string TitleId, string Title, string Version, string Category, string ContentId,
                                  string Dir, string? IconPath);

/// <summary>
/// The titles installed in the emulated file system, read the way the system reads them: the folders of
/// <see cref="FirmwareLayout.Titles"/> (the firmware's own applications aside, which the home screen lists from vs0),
/// each described by its own metadata: a PS Vita application by its sce_sys/param.sfo and icon0.png, a PSM title by
/// its RO/Application/app.info (name and icon inside it).
/// </summary>
public static class InstalledContent
{
    /// <summary>The installed titles under <paramref name="fsRoot"/>, by title. <paramref name="locale"/> picks a PSM
    /// title's name ("pt-BR"; null: its default).</summary>
    public static List<LibraryTitle> Scan(string fsRoot, string? locale = null)
    {
        var list = new List<LibraryTitle>();
        foreach (FirmwareFolder folder in FirmwareLayout.Titles)
        {
            if (folder.Kind == ContentKind.SystemApplication)
            {
                continue;
            }

            string root = folder.Host(fsRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Read(fsRoot, folder, dir, locale) is { } title)
                {
                    list.Add(title);
                }
            }
        }
        return list;
    }

    /// <summary>The title in <paramref name="dir"/>, a folder of <paramref name="folder"/>; null when it is not one.</summary>
    public static LibraryTitle? Read(string fsRoot, FirmwareFolder folder, string dir, string? locale = null) =>
        folder.Kind == ContentKind.PsmTitle ? ReadPsm(fsRoot, dir, locale) : ReadApplication(dir, folder.Kind, UpdateFolder(fsRoot, dir));

    /// <summary>The host folder of the update installed for the application in <paramref name="dir"/> (where appmgr
    /// overlays it, <see cref="FirmwareLayout.PatchFolderOf"/>), or null.</summary>
    public static string? UpdateFolder(string fsRoot, string dir)
    {
        if (FirmwareLayout.HostToGuest(fsRoot, dir) is not { } guest
            || FirmwareLayout.PatchFolderOf(guest, Path.GetFileName(Path.TrimEndingDirectorySeparator(dir))) is not { } patch)
        {
            return null;
        }

        string host = FirmwareLayout.GuestToHost(fsRoot, patch);
        return Directory.Exists(host) ? host : null;
    }

    /// <summary>A PS Vita application folder (eboot.bin, sce_sys/param.sfo); null when it is not one. With an installed
    /// update (<paramref name="updateDir"/>) its param.sfo and icon are the update's, as the application's app0 shows
    /// them (the version is the update's APP_VER).</summary>
    public static LibraryTitle? ReadApplication(string dir, ContentKind kind = ContentKind.VitaApplication, string? updateDir = null)
    {
        if (!File.Exists(Vfs.ResolveCaseInsensitive(dir, "eboot.bin")))
        {
            return null;
        }

        string id = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        string title = id, version = "", category = "gd", contentId = "";
        string? Overlaid(string relative)
        {
            if (updateDir != null && Vfs.ResolveCaseInsensitive(updateDir, relative) is var updated && File.Exists(updated))
            {
                return updated;
            }

            string own = Vfs.ResolveCaseInsensitive(dir, relative);
            return File.Exists(own) ? own : null;
        }
        string ownSfo = Vfs.ResolveCaseInsensitive(dir, "sce_sys/param.sfo");
        string? shownSfo = Overlaid("sce_sys/param.sfo");
        try
        {
            if (File.Exists(ownSfo))
            {
                // what the application is (its ID, kind and content) is its own: an update's CATEGORY is "gp"
                var sfo = Sfo.Load(ownSfo);
                if (sfo.TitleId.Length > 0)
                {
                    id = sfo.TitleId;
                }

                category = sfo.Category;
                contentId = sfo.ContentId;
            }

            if (shownSfo != null)
            {
                var shown = Sfo.Load(shownSfo);
                if (shown.Title.Length > 0)
                {
                    title = shown.Title.Replace('\n', ' ');
                }

                version = DisplayVersion(shown.AppVersion);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException) { }
        return new LibraryTitle(kind, id, title, version, category, contentId, dir, Overlaid("sce_sys/icon0.png"));
    }

    /// <summary>
    /// A PSM title folder (ux0:psm/&lt;id&gt;, its RO/Application/app.info); null when it has no description. Its icon
    /// lives inside app.info, so it is written once to &lt;data&gt;/cache/psm/&lt;id&gt;.png (next to the fs folder)
    /// for the library to show; the category is "gd" (a title one plays, as the library's filters count it).
    /// </summary>
    public static LibraryTitle? ReadPsm(string fsRoot, string dir, string? locale = null)
    {
        string ro = Path.Combine(dir, FirmwareLayout.PsmReadOnly);
        string appInfoPath = Vfs.ResolveCaseInsensitive(ro, FirmwareLayout.PsmAppInfo);
        if (PsmAppInfo.Load(appInfoPath) is not { } info)
        {
            return null;
        }

        string id = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        string name = info.Name(locale);
        string? icon = PsmIcon(fsRoot, id, appInfoPath, info);
        return new LibraryTitle(ContentKind.PsmTitle, id, name.Length > 0 ? name : id, info.Version, "gd", PsmContentId(dir), dir, icon);
    }

    /// <summary>The name of title <paramref name="titleId"/> from its metadata, through <paramref name="resolve"/> (a
    /// process's guest-to-host paths): its param.sfo (<see cref="FirmwareLayout.TitleParamSfoCandidates"/>), else a PSM
    /// title's app.info; "" when none is found.</summary>
    public static string TitleName(Func<string, string?> resolve, string titleId)
    {
        foreach (string sfo in FirmwareLayout.TitleParamSfoCandidates(titleId))
        {
            try
            {
                if (resolve(sfo) is { } host && File.Exists(host))
                {
                    return Sfo.Load(host).Title;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
        }
        string appInfo = FirmwareLayout.PsmTitles.Guest(titleId, FirmwareLayout.PsmReadOnly, FirmwareLayout.PsmAppInfo);
        return resolve(appInfo) is { } h && PsmAppInfo.Load(h) is { } info ? info.Name() : "";
    }

    /// <summary>
    /// How the system starts the PSM title in <paramref name="titleDir"/> (ux0:psm/&lt;id&gt;), as the shell does for a
    /// PSM bubble (FW 3.74 shell.self, its launch by category): a title that carries its own runtime (a Unity one,
    /// ux0:psm/&lt;id&gt;/runtime/eboot.bin) runs that, any other the PSM runtime application
    /// (<see cref="FindPsmRuntime"/>); the launch parameter is the
    /// title's folder ("ux0:psm/NPPA00001"). Null when <paramref name="titleDir"/> is not a PSM title folder, or when
    /// the runtime it needs is not installed.
    /// </summary>
    public static (string ProgramDir, string Self, string Param)? PsmLaunch(string fsRoot, string titleDir)
    {
        if (!IsPsmTitleFolder(fsRoot, titleDir))
        {
            return null;
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(titleDir));
        string id = Path.GetFileName(full);
        string param = FirmwareLayout.PsmTitles.Guest(id);
        string own = Vfs.ResolveCaseInsensitive(full, FirmwareLayout.PsmOwnRuntime + "/eboot.bin");
        if (File.Exists(own))
        {
            return (Path.GetDirectoryName(own)!, Path.GetFileName(own), param);
        }

        return FindPsmRuntime(fsRoot) is { } runtime ? (runtime, "eboot.bin", param) : null;
    }

    /// <summary>The installed PSM runtime: the application (where appmgr finds them, first in title ID order) that
    /// carries <see cref="FirmwareLayout.PsmRuntimeVersion"/> next to its eboot.bin; null when none is installed.</summary>
    public static string? FindPsmRuntime(string fsRoot)
    {
        foreach (FirmwareFolder folder in FirmwareLayout.LaunchableApplications)
        {
            string root = folder.Host(fsRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Vfs.ResolveCaseInsensitive(dir, "eboot.bin")) && File.Exists(Vfs.ResolveCaseInsensitive(dir, FirmwareLayout.PsmRuntimeVersion)))
                {
                    return dir;
                }
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="dir"/> is a folder of ux0:psm (a PSM title's).</summary>
    public static bool IsPsmTitleFolder(string fsRoot, string dir)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        string psmRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(FirmwareLayout.PsmTitles.Host(fsRoot)));
        return string.Equals(Path.GetDirectoryName(full), psmRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayVersion(string appVersion)
    {
        string version = appVersion.TrimStart('0');
        return version.StartsWith('.') ? "0" + version : version;
    }

    /// <summary>The content ID the PSM installer kept (RW/System/content_id), or "".</summary>
    private static string PsmContentId(string dir)
    {
        try
        {
            string path = Path.Combine(dir, FirmwareLayout.PsmWritable, "System", "content_id");
            return File.Exists(path) ? System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)).TrimEnd('\0') : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string? PsmIcon(string fsRoot, string id, string appInfoPath, PsmAppInfo info)
    {
        if (info.Icon() is not { } png)
        {
            return null;
        }

        string dataRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(fsRoot))) ?? fsRoot;
        string path = Path.Combine(dataRoot, "cache", "psm", id + ".png");
        try
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < File.GetLastWriteTimeUtc(appInfoPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, png);
            }
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
