using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Sistema do Vita (the extras; it starts from the sidebar button or Ctrl+H): the firmware (install, state,
/// modules kept on HLE), the recreated home screen's options and the trophy import. Also hosts the settings page.</summary>
public sealed partial class MainWindow
{
    FirmwarePanel? _firmwarePanel;
    SettingsPage? _settingsPage;

    Control SystemPage()
    {
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 32) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        header.Children.Add(_k.VStack(2, _k.Heading(S.VitaSystem, 30), _k.Text(S.VitaSystemSubtitle, 13.5, color: static p => p.Muted)));
        // the emulated memory card and firmware partitions (ux0, ur0, os0, vs0...) in the file manager
        var openFs = _k.Secondary(S.OpenFileSystem, "folder", () => { Directory.CreateDirectory(Program.FsRoot); OpenPath(Program.FsRoot); }, 46, tip: S.OpenFileSystemTip(Program.FsRoot));
        openFs.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(Kit.At(openFs, col: 1));
        page.Children.Add(header);

        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
        var left = new StackPanel { Spacing = 10 };
        left.Children.Add(_k.GroupTitle(S.Firmware));
        // the firmware panel keeps running an installation when the page changes: reuse it
        if (_firmwarePanel == null)
        {
            _firmwarePanel = new FirmwarePanel();
            _firmwarePanel.Finished += () => { Refresh(rebuild: false); RebuildSidebar(); };
        }
        Detach(_firmwarePanel);
        left.Children.Add(_k.Card(_firmwarePanel, 18, new Thickness(18, 14)));
        var fw = _fw;
        bool fonts = VitaSharp.Core.Firmware.FirmwareInfo.HasSystemFonts(Program.FsRoot);
        bool pd0 = Directory.Exists(Path.Combine(Program.FsRoot, "pd0", "data"));
        left.Children.Add(_k.Group(
            _k.Row("cpu", S.FirmwareVersionRow(fw?.Version), fw == null ? S.FirmwareMissingDetail : S.FirmwareDetail(fw.Build, fw.Source, fw.InstalledUtc),
                fw != null ? _k.Badge(S.InstalledBadge, static p => p.Ok) : _k.Badge(S.MissingBadge, static p => p.Warn)),
            _k.Row("vita", "shell.self", S.ShellDetail, ShellInstalled ? _k.Badge(S.OkBadge, static p => p.Ok) : _k.Badge(S.MissingBadge, static p => p.Warn)),
            _k.Row("volume", "pd0", S.Pd0Detail, pd0 ? _k.Badge(S.InstalledBadge, static p => p.Ok) : _k.Badge(S.MissingBadge, static p => p.Warn)),
            _k.Row("layers", S.SystemFonts, fonts ? S.FontsInstalled : S.FontsMissing, fonts ? _k.Badge(S.InstalledBadge, static p => p.Ok) : _k.Badge(S.MissingBadge, static p => p.Warn))));
        left.Children.Add(_k.GroupTitle(S.HleModulesTitle));
        left.Children.Add(_k.Group(
            _k.Row("box", S.HleModules, S.HleModulesDetail, _k.Field(_settings.HleModules, t => { _settings.HleModules = t.Trim(); SaveSettings(); }, 240, S.HleModulesWatermark))));
        cols.Children.Add(left);

        var right = new StackPanel { Spacing = 10 };
        right.Children.Add(_k.GroupTitle(S.RecreatedHomeTitle));
        var shellAssets = new VitaSharp.Core.Shell.ShellAssets(Program.FsRoot);
        var themes = shellAssets.Themes(VitaSharp.Core.Shell.LiveAreaTemplate.VitaLanguage(System.Globalization.CultureInfo.CurrentUICulture));
        bool bgmAvailable = themes.Any(t => t.BgmPath != null) || shellAssets.SystemBgm != null;
        right.Children.Add(_k.Group(
            _k.Row("library", S.RecreatedHome, S.RecreatedHomeDetail, _k.Switch(_settings.RecreatedHome, v =>
            {
                _settings.RecreatedHome = v;
                if (!v && _settings.View == LauncherSettings.ViewHome)
                {
                    _settings.View = LauncherSettings.ViewGrid;
                }

                SaveSettings();
            })),
            themes.Count > 1 ? _k.Row("image", S.HomeTheme, null, _k.Dropdown(themes.Select(t => t.Title).ToArray(), Math.Max(0, themes.FindIndex(t => t.Id == _settings.Theme)), i =>
            {
                _settings.Theme = themes[i].Id == VitaSharp.Core.Shell.ShellTheme.DefaultId ? "" : themes[i].Id;
                SaveSettings();
                _shell?.ApplySettings();
            }, 220)) : null,
            _k.Row("volume", S.HomeBgm, bgmAvailable ? S.HomeBgmDetail : S.HomeBgmMissing, _k.Switch(_settings.HomeBgm, v => { _settings.HomeBgm = v; SaveSettings(); _shell?.ApplySettings(); }, bgmAvailable)),
            _k.Row("volume", S.SystemSounds, S.SystemSoundsDetail, _k.Switch(_settings.SystemSounds, v => { _settings.SystemSounds = v; SaveSettings(); }, shellAssets.HasFirmware))));
        right.Children.Add(_k.GroupTitle(S.TrophyImportTitle));
        right.Children.Add(_k.Card(new TrophyImportPanel(), 18, new Thickness(18, 14)));
        cols.Children.Add(Kit.At(right, col: 2));
        page.Children.Add(cols);
        return new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    Control SettingsPageHost()
    {
        _settingsPage = new SettingsPage(_settings, SaveSettings);
        // the palette change re-colours the window; the sidebar's theme control is drawn again with the new choice
        _settingsPage.ThemeChanged += SetTheme;
        _settingsPage.OpenSystem += () => Navigate(PageSystem);
        _settingsPage.LanguageChanged += () => RebuildAll();
        _settingsPage.CheckUpdatesNow += () => _ = CheckForUpdates(manual: true);
        return _settingsPage;
    }
}
