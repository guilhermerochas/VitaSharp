using VitaSharp.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Desktop.Input;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Configuracoes: sections on the left (Geral, Sistema do Vita, Graficos, Controles, Emulacao, Rede e PSN, Avancado),
/// grouped rows on the right. Every change is saved right away (no Save button); the controller page keeps its own
/// save (<see cref="SaveControls"/>, called when the page is left).
/// </summary>
public sealed class SettingsPage : UserControl
{
    public const string SectionGeneral = "general", SectionGraphics = "graphics", SectionControls = "controls",
        SectionEmulation = "emulation", SectionNetwork = "network", SectionAdvanced = "advanced", SectionSystem = "system";

    readonly LauncherSettings _settings;
    readonly Action _save;
    readonly Kit _k;
    readonly SdlGamepads? _pads;
    readonly Border _body = new();
    readonly StackPanel _nav = new() { Spacing = 2 };
    readonly Dictionary<string, Control> _anchors = new();
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    string _section = SectionGeneral;

    /// <summary>The theme was picked (the launcher rebuilds itself).</summary>
    public event Action<string>? ThemeChanged;
    /// <summary>"Sistema do Vita" was picked in the list (it has its own page).</summary>
    public event Action? OpenSystem;
    /// <summary>"Procurar agora" was pressed (the launcher checks the releases and shows the update dialog).</summary>
    public event Action? CheckUpdatesNow;
    /// <summary>The interface language was picked (the launcher rebuilds itself in it).</summary>
    public event Action? LanguageChanged;

    /// <summary>The controller page (created when first shown, so SDL only starts when needed).</summary>
    public ControllerSettingsPanel? Controls { get; private set; }

    Palette P => _k.P;

    /// <param name="save">Writes the settings (nothing for previews).</param>
    /// <param name="pads">Controller backend for the controller page (tests / previews); null = its own.</param>
    public SettingsPage(LauncherSettings settings, Action save, SdlGamepads? pads = null)
    {
        _settings = settings;
        _save = save;
        _pads = pads;
        _k = new Kit(Palette.Current);
        var page = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), Margin = new Thickness(36, 28, 36, 0) };
        var left = new DockPanel();
        var title = _k.Heading(S.Settings, 26);
        title.Margin = new Thickness(0, 0, 0, 22);
        DockPanel.SetDock(title, Dock.Top);
        left.Children.Add(title);
        left.Children.Add(_nav);
        page.Children.Add(left);
        page.Children.Add(Kit.At(_body, col: 1));
        _body.Margin = new Thickness(36, 0, 0, 0);
        _scroll.Content = BuildList();
        Content = page;
        ShowSection(SectionGeneral);
    }

    /// <summary>Shows a section: scrolls the list to it, or opens the controller page.</summary>
    public void ShowSection(string section)
    {
        if (section == SectionSystem) { OpenSystem?.Invoke(); return; }
        _section = section;
        BuildNav();
        if (section == SectionControls)
        {
            Controls ??= new ControllerSettingsPanel(Program.DataRoot, _pads) { Margin = new Thickness(0, 0, 0, 12) };
            _body.Child = Controls;
            return;
        }
        if (_body.Child != _scroll) _body.Child = _scroll;
        if (_anchors.TryGetValue(section, out var anchor))
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (section == SectionGeneral) _scroll.Offset = default;
                else anchor.BringIntoView(new Rect(0, 0, 1, _scroll.Viewport.Height));
            }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Saves controller mappings edited on the controller page.</summary>
    public void SaveControls()
    {
        if (Controls?.HasChanges == true) Controls.Save();
    }

    void BuildNav()
    {
        _nav.Children.Clear();
        foreach (var (key, icon, text) in new[]
                 {
                     (SectionGeneral, "settings", S.SectionGeneral), (SectionSystem, "vita", S.VitaSystem), (SectionGraphics, "monitor", S.SectionGraphics),
                     (SectionControls, "gamepad", S.Controls), (SectionEmulation, "cpu", S.SectionEmulation), (SectionNetwork, "user", S.SectionNetwork),
                     (SectionAdvanced, "terminal", S.SectionAdvanced),
                 })
        {
            bool sel = key == _section;
            var g = _k.HStack(12, _k.Icon(icon, 18, sel ? P.Accent : P.Muted), _k.Text(text, 14, sel ? FontWeight.SemiBold : FontWeight.Medium, sel ? P.Text : P.TextSoft));
            g.VerticalAlignment = VerticalAlignment.Center;
            _nav.Children.Add(Kit.Click(new Border
            {
                Height = 40, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(11), Child = g,
                Background = sel ? Kit.B(P.Highlight) : Brushes.Transparent,
            }, () => ShowSection(key)));
        }
    }

    TextBlock Anchor(string section, string title)
    {
        var t = _k.GroupTitle(title);
        _anchors[section] = t;
        return t;
    }

    void Set(Action change)
    {
        change();
        _save();
    }

    Control BuildList()
    {
        var s = new StackPanel { Spacing = 10, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 32) };

        // ---------------------------------------------------------- Geral
        s.Children.Add(Anchor(SectionGeneral, S.StartSection));
        s.Children.Add(_k.Group(
            _k.Row("vita", S.StartupView, S.StartupViewDetail,
                _k.Segmented(_settings.StartupView == LauncherSettings.StartSystem ? 1 : 0,
                    i => Set(() => _settings.StartupView = i == 1 ? LauncherSettings.StartSystem : LauncherSettings.StartLibrary),
                    (S.Library, null, null), (S.VitaSystem, null, null))),
            _k.Row("play", S.GameClick, S.GameClickDetail,
                _k.Segmented(_settings.GameClick == LauncherSettings.ClickLiveArea ? 1 : 0,
                    i => Set(() => _settings.GameClick = i == 1 ? LauncherSettings.ClickLiveArea : LauncherSettings.ClickPlay),
                    (S.PlayDirectly, null, null), (S.OpenLiveArea, null, null))),
            _k.Row("terminal", S.VitaShortcut, S.VitaShortcutDetail, _k.Kbd("Ctrl + H"))));
        s.Children.Add(_k.GroupTitle(S.Appearance));
        int theme = _settings.UiTheme switch { LauncherSettings.ThemeDark => 1, LauncherSettings.ThemeLight => 2, _ => 0 };
        s.Children.Add(_k.Group(
            _k.Row(P.Dark ? "moon" : "sun", S.Theme, S.ThemeDetail,
                _k.Segmented(theme, i =>
                {
                    var t = i switch { 1 => LauncherSettings.ThemeDark, 2 => LauncherSettings.ThemeLight, _ => LauncherSettings.ThemeSystem };
                    Set(() => _settings.UiTheme = t);
                    ThemeChanged?.Invoke(t);
                }, (S.ThemeSystem, null, null), (S.ThemeDark, "moon", null), (S.ThemeLight, "sun", null)))));
        int lang = _settings.Language switch { Loc.English => 1, Loc.Portuguese => 2, _ => 0 };
        s.Children.Add(_k.Group(
            _k.Row("globe", S.Language, S.LanguageDetail,
                _k.Segmented(lang, i =>
                {
                    Set(() => _settings.Language = i switch { 1 => Loc.English, 2 => Loc.Portuguese, _ => Loc.System });
                    Loc.SetLanguage(_settings.Language);
                    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentCulture = Loc.Culture;
                    LanguageChanged?.Invoke();
                }, (S.ThemeSystem, null, null), (S.LanguageEnglish, null, null), (S.LanguagePortuguese, null, null)))));
        s.Children.Add(_k.GroupTitle(S.Updates));
        s.Children.Add(_k.Group(
            _k.Row("download", S.CheckUpdates, S.CheckUpdatesDetail, _k.Switch(_settings.CheckUpdates, v => Set(() => _settings.CheckUpdates = v))),
            _k.Row("layers", S.UpdateChannel, S.UpdateChannelDetail,
                _k.Segmented(_settings.UpdateChannel == "releases" ? 1 : 0, i => Set(() => _settings.UpdateChannel = i == 1 ? "releases" : "builds"),
                    (S.ChannelBuilds, null, null), (S.ChannelReleases, null, null))),
            _k.Row("clock", S.VersionInUse, Diagnostics.BuildVersion() + (Updater.Feed() == null ? " \u00B7 " + S.NoUpdateFeed : ""),
                _k.Secondary(S.CheckNow, null, () => CheckUpdatesNow?.Invoke(), 36))));
        s.Children.Add(_k.GroupTitle(S.Trophies));
        s.Children.Add(_k.Group(
            _k.Row("trophy", S.TrophySound, S.TrophySoundDetail, _k.Switch(_settings.TrophySound, v => Set(() => _settings.TrophySound = v)))));

        // ---------------------------------------------------------- Graficos
        s.Children.Add(Anchor(SectionGraphics, S.SectionGraphics));
        string[] scales = ["1x (960\u00D7544)", "2x (1920\u00D71088)", "3x (2880\u00D71632)"];
        s.Children.Add(_k.Group(
            _k.Row("monitor", S.Fullscreen, S.FullscreenDetail, _k.Switch(_settings.Fullscreen, v => Set(() => _settings.Fullscreen = v))),
            _k.Row("image", S.WindowSize, S.WindowSizeDetail, _k.Dropdown(scales, Math.Clamp(_settings.Scale - 1, 0, 2), i => Set(() => _settings.Scale = i + 1), 180)),
            _k.Row("gauge", S.VSync, S.VSyncDetail, _k.Switch(_settings.VSync, v => Set(() => _settings.VSync = v))),
            _k.Row("zap", S.FastShaders, S.FastShadersDetail, _k.Switch(_settings.FastShaderCompile, v => Set(() => _settings.FastShaderCompile = v))),
            _k.Row("layers", S.AsyncShaders, S.AsyncShadersDetail, _k.Switch(_settings.BackgroundShaderCompile, v => Set(() => _settings.BackgroundShaderCompile = v))),
            _k.Row("alert", S.Validation, S.ValidationDetail, _k.Switch(_settings.Validation, v => Set(() => _settings.Validation = v)))));
        s.Children.Add(_k.Wrap(S.PerGameGraphicsNote, 12.5).Also(t => t.Margin = new Thickness(6, 2, 0, 0)));

        // ---------------------------------------------------------- Emulacao
        s.Children.Add(Anchor(SectionEmulation, S.SectionEmulation));
        var cpuChoices = new (string Value, string Label)[]
        {
            ("", S.CpuAuto),
            ("native", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? S.CpuNativeArm : S.CpuNativeX64),
            ("il", S.CpuIl),
            ("interp", S.CpuInterp),
        }.Where(c => c.Value is "" or "interp" || VitaSharp.Core.Cpu.Jit.All.Any(b => b.Name == c.Value && b.IsSupported)).ToArray();
        var cameraValues = new List<string> { "", "none" };
        var cameraLabels = new List<string> { S.CameraDefault, S.CameraNone };
        foreach (var d in VitaSharp.Desktop.Host.FlashCapCamera.ListDevices()) { cameraValues.Add(d.Name); cameraLabels.Add(d.Name); }
        if (!cameraValues.Contains(_settings.Camera, StringComparer.OrdinalIgnoreCase)) { cameraValues.Add(_settings.Camera); cameraLabels.Add(S.CameraDisconnected(_settings.Camera)); }
        s.Children.Add(_k.Group(
            _k.Row("cpu", S.Cpu, S.CpuDetail, _k.Dropdown(cpuChoices.Select(c => c.Label).ToArray(),
                Math.Max(0, Array.FindIndex(cpuChoices, c => c.Value.Equals(_settings.CpuBackend, StringComparison.OrdinalIgnoreCase))),
                i => Set(() => _settings.CpuBackend = cpuChoices[i].Value), 300)),
            _k.Row("gauge", S.CpuClock, S.CpuClockDetail, _k.Dropdown([S.Clock333, S.Clock444], _settings.CpuMhz == 444 ? 1 : 0,
                i => Set(() => _settings.CpuMhz = i == 1 ? 444 : 0), 300)),
            _k.Row("camera", S.Camera, S.CameraDetail, _k.Dropdown(cameraLabels,
                Math.Max(0, cameraValues.FindIndex(v => v.Equals(_settings.Camera, StringComparison.OrdinalIgnoreCase))),
                i => Set(() => _settings.Camera = cameraValues[i]), 300))));

        // ---------------------------------------------------------- Rede e PSN
        s.Children.Add(Anchor(SectionNetwork, S.SectionNetwork));
        var psnIdError = _k.Wrap(S.PsnIdInvalid, 12, P.Danger).Also(t => { t.IsVisible = false; t.Margin = new Thickness(16, 0, 16, 8); });
        var psnId = _k.Field(_settings.PsnOnlineId, text =>
        {
            string id = text.Trim();
            if (id.Length == 0) id = VitaSharp.Core.Hle.PsnAccount.DefaultOnlineId;
            psnIdError.IsVisible = !VitaSharp.Core.Hle.PsnAccount.IsValidOnlineId(id);
            if (!psnIdError.IsVisible) Set(() => _settings.PsnOnlineId = id);
        }, 220, VitaSharp.Core.Hle.PsnAccount.DefaultOnlineId);
        psnId.MaxLength = 16;
        var rpcnServer = _k.Field(_settings.RpcnServer, t => Set(() => _settings.RpcnServer = t.Trim()), 260, S.RpcnServerWatermark);
        var rpcnUser = _k.Field(_settings.RpcnUser, t => Set(() => _settings.RpcnUser = t.Trim()), 220);
        rpcnUser.MaxLength = 16;
        var rpcnPass = _k.Field(_settings.RpcnPassword, t => Set(() => _settings.RpcnPassword = t), 220, password: '\u2022');
        var rpcnToken = _k.Field(_settings.RpcnToken, t => Set(() => _settings.RpcnToken = t.Trim()), 220, S.RpcnTokenWatermark);
        var psnLocal = _k.Switch(_settings.PsnLocalServices, v => Set(() => _settings.PsnLocalServices = v));
        void SyncPsn()
        {
            psnId.IsEnabled = psnLocal.IsEnabled = _settings.PsnSignedIn;
            rpcnServer.IsEnabled = rpcnUser.IsEnabled = rpcnPass.IsEnabled = rpcnToken.IsEnabled = _settings.RealInternet && _settings.PsnSignedIn;
        }
        var psnSignedIn = _k.Switch(_settings.PsnSignedIn, v => { Set(() => _settings.PsnSignedIn = v); SyncPsn(); });
        var realNet = _k.Switch(_settings.RealInternet, v => { Set(() => _settings.RealInternet = v); SyncPsn(); });
        SyncPsn();
        s.Children.Add(_k.Group(
            _k.Row("user", S.PsnSignedIn, S.PsnSignedInDetail, psnSignedIn),
            _k.VStack(0, _k.Row("user", S.PsnId, S.PsnIdDetail, psnId), psnIdError),
            _k.Row("hdd", S.PsnLocal, S.PsnLocalDetail, psnLocal),
            _k.Row("globe", S.RealInternet, S.RealInternetDetail, realNet),
            _k.Wrap(S.RealInternetPrivacy, 12.5, P.Warn).Also(t => t.Margin = new Thickness(16, 6, 16, 10)),
            _k.Row("globe", S.RpcnServer, S.RpcnServerDetail, rpcnServer),
            _k.Row("user", S.RpcnUser, null, rpcnUser),
            _k.Row("key", S.RpcnPassword, S.RpcnPasswordDetail, rpcnPass),
            _k.Row("key", S.RpcnToken, S.RpcnTokenDetail, rpcnToken)));

        // ---------------------------------------------------------- Avancado
        s.Children.Add(Anchor(SectionAdvanced, S.SectionAdvanced));
        s.Children.Add(_k.Group(
            _k.Row("alert", S.DebugMode, S.DebugModeDetail, _k.Switch(_settings.DebugMode, v => Set(() => _settings.DebugMode = v))),
            _k.Row("camera", S.PreRoll, S.PreRollDetail, _k.Switch(_settings.CapturePreRoll, v => Set(() => _settings.CapturePreRoll = v))),
            _k.Row("folder", S.DiagnosticsFolder, Diagnostics.Root, _k.Secondary(S.Open, null, () =>
            {
                try
                {
                    Directory.CreateDirectory(Diagnostics.Root);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Diagnostics.Root) { UseShellExecute = true });
                }
                catch (Exception) { }
            }, 36)),
            _k.Row("hdd", S.DataFolder, Program.DataRoot, _k.Secondary(S.Open, null, () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Program.DataRoot) { UseShellExecute = true }); }
                catch (Exception) { }
            }, 36))));
        s.Children.Add(_k.GroupTitle(S.Shortcuts));
        s.Children.Add(_k.Card(_k.Wrap(S.ShortcutsText, 13, P.TextSoft), 18, new Thickness(18, 14)));
        return s;
    }
}
