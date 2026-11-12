namespace VitaSharp.Core.Io;

/// <summary>What a folder of the firmware's layout holds.</summary>
public enum ContentKind
{
    /// <summary>PS Vita games and applications (eboot.bin, sce_sys/param.sfo).</summary>
    VitaApplication,

    /// <summary>The applications of the installed firmware (NPXS...).</summary>
    SystemApplication,

    /// <summary>The application of the game card in the slot.</summary>
    GameCardApplication,

    /// <summary>PSP games and PSone Classics (EBOOT.PBP), run by the system's PSP emulator (vs0:app/NPXS10028).</summary>
    PspGame,

    /// <summary>The PSP emulator's save data.</summary>
    PspSaveData,

    /// <summary>The licences of PSP and PSone titles (&lt;content id&gt;.rif).</summary>
    PspLicense,

    /// <summary>PSP themes.</summary>
    PspTheme,

    /// <summary>The PSP emulator's own root (the work folder appmgr mounts for it).</summary>
    PspEmulator,

    /// <summary>PlayStation Mobile titles (&lt;id&gt;/RO, &lt;id&gt;/RW), run by the PSM runtime application.</summary>
    PsmTitle,

    /// <summary>Game updates (a patch's files replace the application's).</summary>
    Patch,

    /// <summary>The updates of the game card's application, kept on the card.</summary>
    GameCardPatch,

    /// <summary>Additional content (DLC), &lt;title id&gt;/&lt;content folder&gt;.</summary>
    AddCont,

    /// <summary>The game card application's additional content.</summary>
    GameCardAddCont,

    /// <summary>Themes of the home screen.</summary>
    Theme,

    /// <summary>The root of the licences (its own folders per kind).</summary>
    LicenseRoot,

    /// <summary>Application licences, &lt;title id&gt;/&lt;content id&gt;.rif.</summary>
    ApplicationLicense,

    /// <summary>Additional content licences, &lt;title id&gt;/&lt;content folder&gt;/&lt;content id&gt;.rif.</summary>
    AddContLicense,

    /// <summary>Theme licences.</summary>
    ThemeLicense,

    /// <summary>The installed applications' metadata copy (param.sfo, icon0.png, pic0.png, livearea) the package
    /// installer writes and the shell's database rebuild reads.</summary>
    ApplicationMetadata,

    /// <summary>The metadata the shell itself builds (PSM titles: icon, LiveArea from the runtime's template).</summary>
    SystemMetadata,

    /// <summary>PS Vita save data, &lt;title id&gt;.</summary>
    SaveData,

    /// <summary>The system applications' save data, &lt;title id&gt;.</summary>
    SystemSaveData,

    /// <summary>Music.</summary>
    Music,

    /// <summary>Videos.</summary>
    Video,

    /// <summary>Pictures (screenshots, the camera's photos).</summary>
    Picture,

    /// <summary>The shell's databases (app.db: what the home screen shows).</summary>
    ShellDatabase,
}

/// <summary>A folder of the firmware's layout: a device and the folder on it.</summary>
public sealed record FirmwareFolder(ContentKind Kind, string Device, string Folder)
{
    /// <summary>The guest path of the folder ("ux0:app") or of something in it ("ux0:app/PCSA00001/eboot.bin").</summary>
    public string Guest(params string[] parts)
    {
        string root = $"{Device}:{Folder}";
        return parts.Length == 0 ? root : root + "/" + string.Join('/', parts);
    }

    /// <summary>The path relative to the emulated file system's root, with '/' ("ux0/psm/NPPA00001/RO").</summary>
    public string Relative(params string[] parts)
    {
        string root = $"{Device}/{Folder}";
        return parts.Length == 0 ? root : root + "/" + string.Join('/', parts);
    }

    /// <summary>The host path under the emulated file system's root (the folder holding ux0, vs0...).</summary>
    public string Host(string fsRoot, params string[] parts) => OnDevice(Path.Combine(fsRoot, Device), parts);

    /// <summary>The host path under the host folder of this folder's device (the ux0 folder for an ux0 folder).</summary>
    public string OnDevice(string deviceRoot, params string[] parts) =>
        Path.Combine([deviceRoot, .. Folder.Split('/'), .. parts]);
}

/// <summary>
/// Where the PS Vita's firmware keeps what is installed, the one description every part of VitaSharp uses (HLE modules,
/// the shell support, the installers, the launchers). Nothing else names these folders. Sources: the firmware itself
/// (FW 3.74 shell.self, vs0:vsh/common/dbrecovery_plugin.suprx, vs0:vsh/shell/livespace_db.suprx: the paths they open),
/// psvitadevwiki ("Partitions", "PlayStation Mobile"), vita-headers (appmgr.h, promoterutil.h). Vita3K keeps the same
/// folders except the licences (it uses ux0:license/&lt;id&gt;; the firmware's promoter and shell use ux0:license/app).
/// </summary>
public static class FirmwareLayout
{
    public static readonly FirmwareFolder Applications = new(ContentKind.VitaApplication, "ux0", "app");
    public static readonly FirmwareFolder SystemApplications = new(ContentKind.SystemApplication, "vs0", "app");
    public static readonly FirmwareFolder GameCardApplications = new(ContentKind.GameCardApplication, "gro0", "app");
    public static readonly FirmwareFolder PspEmulator = new(ContentKind.PspEmulator, "ux0", "pspemu");
    public static readonly FirmwareFolder PspGames = new(ContentKind.PspGame, "ux0", "pspemu/PSP/GAME");
    public static readonly FirmwareFolder PspSaveData = new(ContentKind.PspSaveData, "ux0", "pspemu/PSP/SAVEDATA");
    public static readonly FirmwareFolder PspLicenses = new(ContentKind.PspLicense, "ux0", "pspemu/PSP/LICENSE");
    public static readonly FirmwareFolder PspThemes = new(ContentKind.PspTheme, "ux0", "pspemu/PSP/THEME");
    public static readonly FirmwareFolder PsmTitles = new(ContentKind.PsmTitle, "ux0", "psm");
    public static readonly FirmwareFolder Patches = new(ContentKind.Patch, "ux0", "patch");
    public static readonly FirmwareFolder GameCardPatches = new(ContentKind.GameCardPatch, "grw0", "patch");
    public static readonly FirmwareFolder AddCont = new(ContentKind.AddCont, "ux0", "addcont");
    public static readonly FirmwareFolder GameCardAddCont = new(ContentKind.GameCardAddCont, "grw0", "addcont");
    public static readonly FirmwareFolder Themes = new(ContentKind.Theme, "ux0", "theme");
    public static readonly FirmwareFolder Licenses = new(ContentKind.LicenseRoot, "ux0", "license");
    public static readonly FirmwareFolder ApplicationLicenses = new(ContentKind.ApplicationLicense, "ux0", "license/app");
    public static readonly FirmwareFolder AddContLicenses = new(ContentKind.AddContLicense, "ux0", "license/addcont");
    public static readonly FirmwareFolder ThemeLicenses = new(ContentKind.ThemeLicense, "ux0", "license/theme");
    public static readonly FirmwareFolder ApplicationMetadata = new(ContentKind.ApplicationMetadata, "ux0", "appmeta");
    public static readonly FirmwareFolder SystemMetadata = new(ContentKind.SystemMetadata, "ur0", "appmeta");
    public static readonly FirmwareFolder SaveData = new(ContentKind.SaveData, "ux0", "user/00/savedata");
    public static readonly FirmwareFolder SystemSaveData = new(ContentKind.SystemSaveData, "ur0", "user/00/savedata");
    public static readonly FirmwareFolder Music = new(ContentKind.Music, "ux0", "music");
    public static readonly FirmwareFolder Video = new(ContentKind.Video, "ux0", "video");
    public static readonly FirmwareFolder Picture = new(ContentKind.Picture, "ux0", "picture");
    public static readonly FirmwareFolder ShellDatabase = new(ContentKind.ShellDatabase, "ur0", "shell/db");

    /// <summary>Every folder of the layout.</summary>
    public static readonly IReadOnlyList<FirmwareFolder> All =
    [
        Applications, SystemApplications, GameCardApplications, PspEmulator, PspGames, PspSaveData, PspLicenses, PspThemes,
        PsmTitles, Patches, GameCardPatches, AddCont, GameCardAddCont, Themes, Licenses, ApplicationLicenses,
        AddContLicenses, ThemeLicenses, ApplicationMetadata, SystemMetadata, SaveData, SystemSaveData, Music, Video,
        Picture, ShellDatabase,
    ];

    /// <summary>
    /// The folders appmgr starts an application from by title ID (sceAppMgrLaunchAppByName and the like), in the order
    /// it looks: the firmware's own applications, the installed ones, the game card's.
    /// </summary>
    public static readonly IReadOnlyList<FirmwareFolder> LaunchableApplications = [SystemApplications, Applications, GameCardApplications];

    /// <summary>The folders whose entries are titles one can start, by title ID: the applications (in appmgr's order)
    /// and the PSM titles (which the PSM runtime runs).</summary>
    public static readonly IReadOnlyList<FirmwareFolder> Titles = [.. LaunchableApplications, PsmTitles];

    /// <summary>
    /// The folders the shell's database rebuild (dbrecovery_plugin) registers titles from, after vs0:app, in its order
    /// (its table at 0x8103F11C in FW 3.74): installed applications by their metadata, PSP/PSone titles, PSM titles.
    /// </summary>
    public static readonly IReadOnlyList<FirmwareFolder> ShellRebuildSources = [ApplicationMetadata, PspGames, PsmTitles];

    /// <summary>The shell's application database (ur0:shell/db/app.db).</summary>
    public const string ShellAppDatabase = "app.db";

    /// <summary>The installer's card id file: without it the shell rebuilds the memory card's part of app.db.</summary>
    public const string CardIdFile = "id.dat";

    /// <summary>A PSM title's read-only part (its package's "contents/..."), under ux0:psm/&lt;id&gt;.</summary>
    public const string PsmReadOnly = "RO";

    /// <summary>A PSM title's writable part (Documents, Temp, System).</summary>
    public const string PsmWritable = "RW";

    /// <summary>A PSM title's description (name, icons), under its RO folder.</summary>
    public const string PsmAppInfo = "Application/app.info";

    /// <summary>A PSM title's licence folder, under its RO folder.</summary>
    public const string PsmLicense = "License";

    /// <summary>
    /// The file that marks the PSM runtime, the installed application that runs every PSM title (the "PSM Runtime
    /// Package"): its version, which the shell compares with the firmware's own (vs0:vsh/game/psm/runtime_version.txt)
    /// before it starts a PSM title. The runtime is found by it, not by a title ID.
    /// </summary>
    public const string PsmRuntimeVersion = "runtime_version.txt";

    /// <summary>
    /// The host folder of an installed PS Vita application by title ID, looked up the way appmgr does
    /// (<see cref="LaunchableApplications"/>, a folder with eboot.bin), through <paramref name="resolve"/> (guest path
    /// to host path, null when unmapped: a process's Vfs); null when it is not installed.
    /// </summary>
    public static string? FindApplication(Func<string, string?> resolve, string titleId)
    {
        foreach (FirmwareFolder root in LaunchableApplications)
        {
            if (resolve(root.Guest(titleId)) is { } dir && File.Exists(Vfs.ResolveCaseInsensitive(dir, "eboot.bin")))
            {
                return dir;
            }
        }
        return null;
    }

    /// <summary>The installed title <paramref name="titleId"/> (a folder of one of <see cref="Titles"/>, first match),
    /// or null.</summary>
    public static (FirmwareFolder Folder, string Dir)? FindTitle(string fsRoot, string titleId)
    {
        foreach (FirmwareFolder root in Titles)
        {
            string dir = root.Host(fsRoot, titleId);
            if (Directory.Exists(dir))
            {
                return (root, dir);
            }
        }
        return null;
    }

    /// <summary><see cref="FindApplication(Func{string, string?}, string)"/> on the emulated file system's folders.</summary>
    public static string? FindApplication(string fsRoot, string titleId) =>
        FindApplication(guest => GuestToHost(fsRoot, guest), titleId);

    /// <summary>The host path of a guest path ("ux0:app/X") under the emulated file system's root.</summary>
    public static string GuestToHost(string fsRoot, string guest)
    {
        int colon = guest.IndexOf(':');
        string device = guest[..colon];
        string[] parts = guest[(colon + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([fsRoot, device, .. parts]);
    }

    /// <summary>
    /// The guest path of a host folder under the emulated file system's root ("&lt;fs&gt;/ux0/app/X" = "ux0:app/X"),
    /// or null when it is outside it.
    /// </summary>
    public static string? HostToGuest(string fsRoot, string host)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fsRoot)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(host);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : parts[0] + ":" + string.Join('/', parts.Skip(1));
    }

    /// <summary>The param.sfo files that describe a title, in the order the shell's metadata is found: an installed
    /// application, a system application, its metadata copies.</summary>
    public static IEnumerable<string> TitleParamSfoCandidates(string titleId)
    {
        yield return Applications.Guest(titleId, "sce_sys/param.sfo");
        yield return SystemApplications.Guest(titleId, "sce_sys/param.sfo");
        yield return SystemMetadata.Guest(titleId, "param.sfo");
        yield return ApplicationMetadata.Guest(titleId, "param.sfo");
    }

    /// <summary>
    /// The licences of an installed application (ux0:license/app/&lt;id&gt;/*.rif), as the firmware keeps them; empty
    /// when it has none.
    /// </summary>
    public static IEnumerable<string> ApplicationLicenseFiles(string fsRoot, string titleId)
    {
        string dir = ApplicationLicenses.Host(fsRoot, titleId);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.rif").Order(StringComparer.Ordinal) : [];
    }
}
