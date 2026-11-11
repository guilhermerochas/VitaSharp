using System.Globalization;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Hle.Np;

namespace VitaSharp.Desktop.Launcher.Ui;

/// <summary>
/// The launcher's texts. Each property reads the resource key of its own name from the language files
/// (src/VitaSharp.Core/Localization/Texts.resx, English, and Texts.pt-BR.resx); each method formats the text of its
/// name with the numbers and names that go in. The language is <see cref="Loc.Culture"/>.
/// </summary>
public static class Strings
{
    public static CultureInfo Culture => Loc.Culture;

    static string T([CallerMemberName] string key = "") => Loc.Get(key);
    static string F(string format, params object?[] args) => string.Format(Culture, format, args);

    // ------------------------------------------------------------------ navigation and shared
    public static string Library => T();
    public static string VitaSystem => T();
    public static string Trophies => T();
    public static string Captures => T();
    public static string InstallNav => T();
    public static string Settings => T();
    public static string Controls => T();
    public static string Firmware => T();
    public static string OpenVitaSystem => T();
    public static string OpenVitaSystemTip => T();
    public static string ShortcutAnywhere => T();
    public static string InstallFirmwareFirst => T();
    public static string GameRunningShort => T();
    public static string FirmwareInstalledShort(string version) => F(T(), version);
    public static string FirmwareMissingShort => T();
    public static string FirmwareTip => T();
    public static string ThemeSystem => T();
    public static string ThemeDark => T();
    public static string ThemeLight => T();
    public static string RunningNow => T();
    public static string Stop => T();
    public static string ForceStop => T();
    public static string GoToGame => T();
    public static string GoToGameTip => T();
    public static string OpenFileSystem => T();
    public static string OpenFileSystemTip(string path) => F(T(), path);
    public static string OpenGamesFolderTip(string path) => F(T(), path);
    public static string ShowLog => T();
    public static string HideLog => T();
    public static string Ok => T();
    public static string Yes => T();
    public static string No => T();
    public static string Cancel => T();
    public static string Open => T();
    public static string OpenFolder => T();
    public static string Delete => T();
    public static string More => T();
    public static string SeeAll => T();
    public static string AllGames => T();
    public static string AllShort => T();
    public static string Play => T();
    public static string LiveArea => T();
    public static string LiveAreaTip => T();
    public static string OpenLiveArea => T();
    public static string OpenGamePage => T();
    public static string RunningBadge => T();
    public static string NeverPlayed => T();
    public static string StatusReady(int games, string dataRoot) => F(T(), games, dataRoot);
    public static string Running(string title) => F(T(), title);
    public static string AlreadyRunning(string title) => F(T(), title);
    public static string CouldNotStart(string error) => F(T(), error);
    public static string Closing => T();
    public static string Closed(string title) => F(T(), title);
    public static string StoppedWithCode(string title, int code) => F(T(), title, code);

    public static string RegionUs => T();
    public static string RegionEurope => T();
    public static string RegionJapan => T();
    public static string RegionAsia => T();
    public static string RegionOther => T();
    public static string RegionHomebrew => T();

    /// <summary>"12h 40m", "35 min", "under 1 min".</summary>
    public static string Duration(long seconds) => seconds switch
    {
        >= 3600 => F(T("DurationHours"), seconds / 3600, seconds % 3600 / 60),
        >= 60 => F(T("DurationMinutes"), seconds / 60),
        _ => T("DurationUnderMinute"),
    };

    /// <summary>"today, 21:14", "yesterday, 20:02", "2026-11-05 22:47".</summary>
    public static string When(DateTime local)
    {
        var today = DateTime.Today;
        if (local.Date == today) return F(T("WhenToday"), local);
        if (local.Date == today.AddDays(-1)) return F(T("WhenYesterday"), local);
        return F(T("WhenDate"), local);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", Culture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", Culture) + " MB",
        _ => (bytes / 1024.0).ToString("0", Culture) + " KB",
    };

    public static string Grade(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => T("GradePlatinum"), TrophyGrade.Gold => T("GradeGold"), TrophyGrade.Silver => T("GradeSilver"), _ => T("GradeBronze"),
    };

    // ------------------------------------------------------------------ library
    public static string LibrarySubtitle(int games, int playable) => F(T(), games, playable);
    public static string SearchGames => T();
    public static string RefreshTip => T();
    public static string Install => T();
    public static string InstallVpkMenu => T();
    public static string InstallPkgMenu => T();
    public static string AddFolderMenu => T();
    public static string FilterAll(int n) => F(T(), n);
    public static string FilterAllShort => T();
    public static string FilterPlayable(int n) => F(T(), n);
    public static string FilterRecent(int n) => F(T(), n);
    public static string FilterTrophies => T();
    public static string AllRegions => T();
    public static string AllRegionsShort => T();
    public static string RegionChip(string region) => F(T(), region);
    public static string SortChip(string sort) => F(T(), sort);
    public static string SortAz => T();
    public static string SortRecent => T();
    public static string SortTime => T();
    public static string ViewGrid => T();
    public static string ViewList => T();
    public static string ViewHome => T();
    public static string ViewHomeTip => T();
    public static string NoMatch => T();
    public static string ContinuePlaying => T();
    public static string PlayedSummary(PlayRecord? r) => r == null ? NeverPlayed : F("{0} \u00B7 {1}", Duration(r.Seconds), When(r.LastPlayedUtc.ToLocalTime()));
    public static string LoadLatestState => T();
    public static string ColGame => T();
    public static string ColRegion => T();
    public static string ColPlayed => T();
    public static string ColTrophies => T();
    public static string ColSize => T();
    public static string Welcome => T();
    public static string WelcomeSubtitle => T();
    public static string StepFirmware => T();
    public static string StepFirmwareText => T();
    public static string StepFirmwareDone(string version) => F(T(), version);
    public static string InstallFirmware => T();
    public static string StepGames => T();
    public static string StepGamesText => T();
    public static string StepControls => T();
    public static string StepControlsText => T();
    public static string ConfigureControls => T();
    public static string EmptyLibrary => T();
    public static string EmptyLibraryText => T();

    // ------------------------------------------------------------------ game page
    public static string CategoryGame => T();
    public static string InstalledOn(DateTime local) => F(T(), local);
    public static string OpenGameFolder => T();
    public static string OpenSaveData => T();
    public static string GameFolders => T();
    public static string GameFoldersTip => T();
    public static string FolderApp(string id) => F(T(), id);
    public static string FolderSaveData(string id) => F(T(), id);
    public static string FolderPatch(string id) => F(T(), id);
    public static string FolderAddcont(string id) => F(T(), id);
    public static string FolderStates(string id) => F(T(), id);
    public static string FolderCaptures => T();
    public static string FolderLogs => T();
    public static string OpenStatesFolder => T();
    public static string Uninstall => T();
    public static string TimePlayed => T();
    public static string LastPlayed => T();
    public static string NoTrophies => T();
    public static string SaveStatesTitle => T();
    public static string QuickState => T();
    public static string Slot(string n) => F(T(), n);
    public static string StateUnreadable => T();
    public static string Incompatible => T();
    public static string LoadStateTip => T();
    public static string NoStates => T();
    public static string SeeAllTrophies => T();
    public static string NoTrophyYet => T();
    public static string NoGameCaptures => T();
    public static string GameSettings => T();
    public static string UseGlobal => T();
    public static string UseGlobalTip => T();
    public static string InternalResolution => T();
    public static string InternalResolutionDetail => T();
    public static string InternalResolutionTip => T();
    public static string FrameRate => T();
    public static string FrameRateDetail => T();
    public static string FrameRateTip => T();
    public static string FastLoading => T();
    public static string FastLoadingDetail => T();
    public static string FastLoadingGlobalDetail => T();
    public static string FastLoadingTip => T();
    public static string ControlsDetail => T();
    public static string AppliesOnStart => T();
    public static string AppliesNextStart => T();

    // ------------------------------------------------------------------ trophies
    public static string TrophiesSubtitle(int got, int total, int games) => F(T(), got, total, games);
    public static string NoTrophyGames => T();
    public static string SearchGame => T();
    public static string TrophiesGot(int n) => F(T(), n);
    public static string TrophiesMissing(int n) => F(T(), n);
    public static string HiddenTrophy => T();
    public static string HiddenTrophyText => T();
    public static string Locked => T();
    public static string Unlocked => T();

    // ------------------------------------------------------------------ captures
    public static string CapturesSubtitle(int count, string size) => F(T(), count, size);
    public static string DeleteOld(int n) => F(T(), n);
    public static string RecordingsFolder => T();
    public static string GpuDumpsFolder => T();
    public static string DiagnosticsFolder => T();
    public static string Recordings(int n) => F(T(), n);
    public static string GpuDumps(int n) => F(T(), n);
    public static string DiagnosticsChip(int n) => F(T(), n);
    public static string GameChip(string game) => F(T(), game);
    public static string NoCaptures => T();
    public static string RecordingBadge => T();
    public static string GpuDumpBadge => T();
    public static string DiagnosticBadge => T();
    public static string ShowInFolder => T();
    public static string ConfirmDeleteOne(string name) => F(T(), name);
    public static string ConfirmDeleteMany(int n, string size) => F(T(), n, size);
    public static string Deleted(int n) => F(T(), n);

    // ------------------------------------------------------------------ install
    public static string InstallTitle => T();
    public static string InstallSubtitle => T();
    public static string DropHere => T();
    public static string DropHint => T();
    public static string ChooseVpk => T();
    public static string ChoosePkg => T();
    public static string ChooseFolder => T();
    public static string Queue => T();
    public static string ClearFinished => T();
    public static string QueueEmpty => T();
    public static string Installing => T();
    public static string InstallingMore(int waiting) => F(T(), waiting);
    public static string Waiting => T();
    public static string InstalledAt(string detail, DateTime when) => F(T(), detail, When(when));
    public static string FailedBecause(string why) => F(T(), why);
    public static string RemoveFromQueue => T();
    public static string OpenPage => T();
    public static string InstallCancelled => T();
    public static string InstallCancelledOf(string name) => F(T(), name);
    public static string InstallFailed(string error) => F(T(), error);
    public static string InstallFailedOf(string name) => F(T(), name);
    public static string NothingInstalled(string name) => F(T(), name);
    public static string Installed(string title) => F(T(), title);
    public static string Reinstalled(string title) => F(T(), title);
    public static string InstalledPatch(string id, string version) => F(T(), id, version.Length > 0 ? " (v" + version + ")" : "");
    public static string InstalledAddOn(string id) => F(T(), id);
    public static string ReplacePatch(string id, string version) => F(T(), id, version.Length > 0 ? F(T("ReplacePatchVersion"), version) : T("ReplacePatchThisFile"));
    public static string ReplaceAddOn(string id) => F(T(), id);
    public static string ReplaceGame(string title, string id) => F(T(), title, id);
    public static string ReplaceFolder(string id) => F(T(), id);
    public static string NotAGameFolder => T();
    public static string Copying => T();
    public static string Added(string title) => F(T(), title);
    public static string PickVpkTitle => T();
    public static string VpkFileType => T();
    public static string PickPkgTitle => T();
    public static string PkgFileType => T();
    public static string PickFolderTitle => T();
    public static string ZrifWatermark => T();
    public static string ZrifEmpty => T();
    public static string ZrifNot => T();
    public static string ZrifUnreadable(string error) => F(T(), error);
    public static string ChooseLicenseFile => T();
    public static string PickLicenseTitle => T();
    public static string LicenseFileType => T();
    public static string LicenseFor(string name) => F(T(), name);
    public static string PkgKindName(VitaSharp.Core.Package.PkgKind kind) => kind switch
    {
        VitaSharp.Core.Package.PkgKind.VitaApp => T("PkgKindVitaApp"),
        VitaSharp.Core.Package.PkgKind.VitaPatch => T("PkgKindVitaPatch"),
        VitaSharp.Core.Package.PkgKind.VitaDlc => T("PkgKindVitaDlc"),
        VitaSharp.Core.Package.PkgKind.VitaTheme => T("PkgKindVitaTheme"),
        VitaSharp.Core.Package.PkgKind.VitaPsm => T("PkgKindVitaPsm"),
        VitaSharp.Core.Package.PkgKind.Psp => T("PkgKindPsp"),
        VitaSharp.Core.Package.PkgKind.PspPcEngine => T("PkgKindPspPcEngine"),
        VitaSharp.Core.Package.PkgKind.PspGo => T("PkgKindPspGo"),
        VitaSharp.Core.Package.PkgKind.PspMini => T("PkgKindPspMini"),
        VitaSharp.Core.Package.PkgKind.PspNeoGeo => T("PkgKindPspNeoGeo"),
        VitaSharp.Core.Package.PkgKind.PspDlc => T("PkgKindPspDlc"),
        VitaSharp.Core.Package.PkgKind.PspTheme => T("PkgKindPspTheme"),
        VitaSharp.Core.Package.PkgKind.Psx => T("PkgKindPsx"),
        _ => VitaSharp.Core.Package.PkgKindExtensions.Describe(kind),
    };
    public static string PkgNoLicense(VitaSharp.Core.Package.PkgInfo p) => F(T(), PkgKindName(p.Kind), p.Title, p.TitleId);
    public static string PkgNeedsLicense(VitaSharp.Core.Package.PkgInfo p) => F(T(), PkgKindName(p.Kind), p.Title, p.TitleId);
    public static string PkgUnreadable(string error) => F(T(), error);
    public static string InstalledPspEmu(string title, VitaSharp.Core.Package.PkgKind kind) => F(T(), title, PkgKindName(kind));
    public static string LicenseText => T();
    public static string ConfirmUninstall(string title, string id) => F(T(), title, id);
    public static string Uninstalled(string title) => F(T(), title);
    public static string UninstallFailed(string error) => F(T(), error);

    // ------------------------------------------------------------------ Vita system page
    public static string VitaSystemSubtitle => T();
    public static string FirmwareVersionRow(string? version) => version != null ? F("Firmware {0}", version) : "Firmware";
    public static string FirmwareMissingDetail => T();
    public static string FirmwareDetail(uint build, string source, DateTime? installedUtc) =>
        string.Join(" \u00B7 ", new[] { build > 0 ? F(T("FirmwareBuild"), build) : "", source, installedUtc is { } t ? InstalledOn(t.ToLocalTime()) : "" }.Where(s => s.Length > 0));
    public static string InstalledBadge => T();
    public static string MissingBadge => T();
    public static string OkBadge => T();
    public static string ShellDetail => T();
    public static string Pd0Detail => T();
    public static string SystemFonts => T();
    public static string FontsInstalled => T();
    public static string FontsMissing => T();
    public static string HleModulesTitle => T();
    public static string HleModules => T();
    public static string HleModulesDetail => T();
    public static string HleModulesWatermark => T();
    public static string RecreatedHomeTitle => T();
    public static string RecreatedHome => T();
    public static string RecreatedHomeDetail => T();
    public static string HomeTheme => T();
    public static string HomeBgm => T();
    public static string HomeBgmDetail => T();
    public static string HomeBgmMissing => T();
    public static string SystemSounds => T();
    public static string SystemSoundsDetail => T();
    public static string TrophyImportTitle => T();

    // ------------------------------------------------------------------ settings
    public static string SectionGeneral => T();
    public static string SectionGraphics => T();
    public static string SectionEmulation => T();
    public static string SectionNetwork => T();
    public static string SectionAudio => T();
    public static string AudioOutput => T();
    public static string AudioOutputDetail => T();
    public static string AudioOutputDefault => T();
    public static string SectionAdvanced => T();
    public static string StartSection => T();
    public static string StartupView => T();
    public static string StartupViewDetail => T();
    public static string GameClick => T();
    public static string GameClickDetail => T();
    public static string PlayDirectly => T();
    public static string VitaShortcut => T();
    public static string VitaShortcutDetail => T();
    public static string Appearance => T();
    public static string Theme => T();
    public static string ThemeDetail => T();
    public static string Language => T();
    public static string LanguageDetail => T();
    public static string LanguageEnglish => T();
    public static string LanguagePortuguese => T();
    public static string Updates => T();
    public static string CheckUpdates => T();
    public static string CheckUpdatesDetail => T();
    public static string UpdateChannel => T();
    public static string UpdateChannelDetail => T();
    public static string ChannelBuilds => T();
    public static string ChannelReleases => T();
    public static string VersionInUse => T();
    public static string CheckNow => T();
    public static string Checking => T();
    public static string NoUpdateFeed => T();
    public static string UpToDate(string version) => F(T(), version);
    public static string UpdateCheckFailed(string error) => F(T(), error);
    public static string UpdateAvailable => T();
    public static string UpdateFromTo(string current, string next) => F(T(), current, next);
    public static string UpdatePublished(DateTimeOffset when) => F(T(), when.ToLocalTime());
    public static string UpdateNotes => T();
    public static string SkipVersion => T();
    public static string RemindLater => T();
    public static string UpdateNow => T();
    public static string UpdateDownloading(double fraction) => F(T(), fraction);
    public static string UpdateRestarting => T();
    public static string UpdateFailed(string error) => F(T(), error);
    public static string UpdateCloseGame => T();
    public static string UpdateDataSafe => T();
    public static string TrophySound => T();
    public static string TrophySoundDetail => T();
    public static string Fullscreen => T();
    public static string FullscreenDetail => T();
    public static string WindowSize => T();
    public static string WindowSizeDetail => T();
    public static string VSync => T();
    public static string VSyncDetail => T();
    public static string FastShaders => T();
    public static string FastShadersDetail => T();
    public static string AsyncShaders => T();
    public static string AsyncShadersDetail => T();
    public static string Validation => T();
    public static string ValidationDetail => T();
    public static string PerGameGraphicsNote => T();
    public static string Cpu => T();
    public static string CpuDetail => T();
    public static string CpuAuto => T();
    public static string CpuNativeX64 => T();
    public static string CpuNativeArm => T();
    public static string CpuIl => T();
    public static string CpuInterp => T();
    public static string CpuClock => T();
    public static string CpuClockDetail => T();
    public static string Clock333 => T();
    public static string Clock444 => T();
    public static string CameraFront => T();
    public static string CameraFrontDetail => T();
    public static string CameraBack => T();
    public static string CameraBackDetail => T();
    public static string CameraBackDefault => T();
    public static string CameraPickPicture => T();
    public static string CameraPickTitle => T();
    public static string CameraPictureFileType => T();
    public static string CameraPicture(string name) => F(T(), name);
    public static string CameraDefault => T();
    public static string CameraNone => T();
    public static string CameraDisconnected(string name) => F(T(), name);
    public static string PsnSignedIn => T();
    public static string PsnSignedInDetail => T();
    public static string PsnId => T();
    public static string PsnIdDetail => T();
    public static string PsnIdInvalid => T();
    public static string PsnLocal => T();
    public static string PsnLocalDetail => T();
    public static string RealInternet => T();
    public static string RealInternetDetail => T();
    public static string RealInternetPrivacy => T();
    public static string RpcnServer => T();
    public static string RpcnServerDetail => T();
    public static string RpcnServerWatermark => T();
    public static string RpcnUser => T();
    public static string RpcnPassword => T();
    public static string RpcnPasswordDetail => T();
    public static string RpcnToken => T();
    public static string RpcnTokenDetail => T();
    public static string RpcnTokenWatermark => T();
    public static string DebugMode => T();
    public static string DebugModeDetail => T();
    public static string PreRoll => T();
    public static string PreRollDetail => T();
    public static string DataFolder => T();
    public static string Shortcuts => T();
    public static string ShortcutsText => T();
}
