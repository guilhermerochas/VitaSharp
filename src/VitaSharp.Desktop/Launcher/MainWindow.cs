using VitaSharp.Core.Util;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// The launcher (docs/notes/desktop-launcher-design.md): a fixed sidebar (the Vita system in one click, the pages, the
/// install in progress, firmware state, theme) beside the current page: library, game page, Vita system, trophies,
/// captures, install queue or settings. Pages are built from <see cref="Kit"/> in the current <see cref="Palette"/> and
/// rebuilt when the theme changes. Games run as child processes ("run"), the Vita system as "boot-shell".
/// </summary>
public sealed partial class MainWindow : Window
{
    public const string PageLibrary = "library", PageGame = "game", PageSystem = "system", PageTrophies = "trophies",
        PageCaptures = "captures", PageInstall = "install", PageSettings = "settings";

    readonly LauncherSettings _settings;
    readonly bool _saveSettings;
    Kit _k = new(Palette.Current);
    Palette P => _k.P;

    List<GameEntry> _all = new();
    Dictionary<string, PlayRecord> _play = new();

    string _page = PageLibrary;
    GameEntry? _pageGame;

    readonly Grid _root = new() { ColumnDefinitions = new ColumnDefinitions("252,*") };
    readonly Border _sidebarHost = new();
    readonly Border _contentHost = new() { ClipToBounds = true };
    readonly Panel _overlay = new() { IsVisible = false };
    readonly DockPanel _statusBar = new();
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontSize = 12,
        FontFamily = new FontFamily("Consolas, Menlo, monospace"), BorderThickness = new Thickness(0),
    };
    readonly Border _logPanel = new() { Height = 180 };

    Process? _running;
    GameEntry? _runningGame;
    string? _runningTitle, _runningId;
    DateTime _runStartUtc;

    /// <summary>Extra options for every run (ui-render's launch check: --headless --seconds=N).</summary>
    internal string[] ExtraRunOptions = [];

    /// <summary>The recreated home screen and LiveArea (library "Tela inicial" view, "Abrir a LiveArea").</summary>
    Shell.ShellView? _shell;

    public Shell.ShellView? ShellView => _shell;
    public string CurrentPage => _page;
    internal IReadOnlyList<GameEntry> Games => _all;
    internal SettingsPage? SettingsPageForTests => _settingsPage;
    internal bool IsRunning => _running != null;
    internal Process? RunningProcessForTests => _running;
    internal void OpenLiveAreaForTests(GameEntry g) => OpenLiveArea(g);
    /// <summary>Opens the game page's "Pastas" menu (ui-render).</summary>
    internal void ShowGameFoldersForTests() { if (_gameFolders?.Flyout is { } f) f.ShowAt(_gameFolders); }

    /// <summary>Starts a title (or "shell") the way the launcher's buttons do; false when it could not start.</summary>
    internal bool LaunchForTests(string titleId)
    {
        if (titleId == "shell") LaunchSystem();
        else if (_all.FirstOrDefault(g => g.TitleId == titleId) is { } g) Launch(g);
        return _running != null;
    }

    /// <param name="settings">Preferences to use instead of the saved ones (ui-snapshot, ui-render); changes are not saved.</param>
    public MainWindow(LauncherSettings? settings = null)
    {
        _settings = settings ?? LauncherSettings.Load();
        _saveSettings = settings == null;
        Core.Localization.Loc.SetLanguage(_settings.Language);
        // numbers and dates in the launcher follow the interface language too
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentCulture = Core.Localization.Loc.Culture;
        LauncherApp.ApplyTheme(_settings.UiTheme);
        Title = "VitaSharp";
        Width = 1360;
        Height = 860;
        MinWidth = 1000;
        MinHeight = 620;
        FontFamily = Kit.Inter;

        var main = new DockPanel();
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        DockPanel.SetDock(_logPanel, Dock.Bottom);
        main.Children.Add(_statusBar);
        main.Children.Add(_logPanel);
        main.Children.Add(_contentHost);
        _root.Children.Add(_sidebarHost);
        _root.Children.Add(Kit.At(main, col: 1));
        _root.Children.Add(Kit.At(_overlay, colSpan: 2));
        _logPanel.Child = _log;
        _logPanel.IsVisible = _settings.ShowLog;
        Content = _root;

        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        LauncherApp.PaletteChanged += OnPaletteChanged;

        Refresh(rebuild: false);
        RebuildAll();
        SetStatus(S.StatusReady(_all.Count, Program.DataRoot));
        // "Ao abrir o emulador: Sistema do Vita": the shell starts right away, the launcher stays behind it
        if (_saveSettings && _settings.StartupView == LauncherSettings.StartSystem && ShellInstalled)
            Opened += (_, _) => LaunchSystem();
        // "Procurar atualizacoes ao iniciar": the GitHub releases, in the background (quiet when nothing is new)
        if (_saveSettings)
        {
            Updater.CleanDownloads();
            if (_settings.CheckUpdates) Opened += (_, _) => _ = CheckForUpdates(manual: false);
        }
    }

    void SaveSettings()
    {
        if (_saveSettings) _settings.Save();
    }

    void OnPaletteChanged() => Dispatcher.UIThread.Post(RebuildAll);

    /// <summary>Rebuilds every part in the current palette (theme change).</summary>
    void RebuildAll()
    {
        _settingsPage?.SaveControls();
        // panels drawn with the old palette are made again (the firmware panel keeps a running installation)
        if (_firmwarePanel is { Busy: false }) _firmwarePanel = null;
        _k = new Kit(Palette.Current);
        Background = Kit.B(P.Background);
        _sidebarHost.Background = Kit.B(P.Sidebar);
        _sidebarHost.BorderBrush = Kit.B(P.Border);
        _sidebarHost.BorderThickness = new Thickness(0, 0, 1, 0);
        _logPanel.Background = Kit.B(P.Surface);
        _log.Background = Kit.B(P.Surface);
        _log.Foreground = Kit.B(P.TextSoft);
        BuildStatusBar();
        RebuildSidebar();
        ShowPage();
    }

    // ------------------------------------------------------------------ navigation

    /// <summary>Opens a page (<see cref="PageLibrary"/> etc.); <paramref name="game"/> for the game page.</summary>
    public void Navigate(string page, GameEntry? game = null)
    {
        LeavePage();
        _page = page;
        _pageGame = game ?? (page == PageGame ? _pageGame : null);
        RebuildSidebar();
        ShowPage();
    }

    void ShowPage()
    {
        CloseOverlay();
        if (_page == PageGame && _pageGame != null && !_all.Any(g => g.TitleId == _pageGame.TitleId)) _page = PageLibrary;
        _contentHost.Child = _page switch
        {
            PageGame when _pageGame != null => GamePage(_pageGame),
            PageSystem => SystemPage(),
            PageTrophies => TrophiesPage(),
            PageCaptures => CapturesPage(),
            PageInstall => InstallPage(),
            PageSettings => SettingsPageHost(),
            _ => LibraryPage(),
        };
    }

    /// <summary>Saves what the page edits in place (controller mappings) before it is replaced.</summary>
    void LeavePage()
    {
        _settingsPage?.SaveControls();
    }

    // ------------------------------------------------------------------ sidebar

    void RebuildSidebar()
    {
        // no logo or name above the button (the window title names the app): the sidebar starts with the Vita system
        var s = new DockPanel { Margin = new Thickness(16, 22, 16, 18) };

        var launch = LaunchButton();
        launch.Margin = new Thickness(0, 0, 0, 18);
        DockPanel.SetDock(launch, Dock.Top);
        s.Children.Add(launch);

        var bottom = _k.VStack(12);
        if (RunningCard() is { } running) bottom.Children.Add(running);
        if (InstallCard() is { } install) bottom.Children.Add(install);
        bool fw = FirmwareInstalled;
        var fwRow = _k.HStack(8, _k.Dot(fw ? P.Ok : P.Warn), _k.Text(fw ? S.FirmwareInstalledShort(FirmwareVersion) : S.FirmwareMissingShort, 12.5, color: P.Muted));
        fwRow.Margin = new Thickness(6, 2, 0, 0);
        bottom.Children.Add(Kit.Click(fwRow, () => Navigate(PageSystem), S.FirmwareTip));
        int theme = _settings.UiTheme switch { LauncherSettings.ThemeDark => 1, LauncherSettings.ThemeLight => 2, _ => 0 };
        bottom.Children.Add(_k.Segmented(theme, i => SetTheme(i switch { 1 => LauncherSettings.ThemeDark, 2 => LauncherSettings.ThemeLight, _ => LauncherSettings.ThemeSystem }),
            (S.ThemeSystem, "monitor", null), ("", "moon", S.ThemeDark), ("", "sun", S.ThemeLight)));
        DockPanel.SetDock(bottom, Dock.Bottom);
        s.Children.Add(bottom);

        var nav = _k.VStack(4);
        nav.Children.Add(NavItem("library", S.Library, _page is PageLibrary or PageGame, () => Navigate(PageLibrary), _all.Count > 0 ? _all.Count.ToString(S.Culture) : null));
        nav.Children.Add(NavItem("vita", S.VitaSystem, _page == PageSystem, () => Navigate(PageSystem)));
        nav.Children.Add(NavItem("trophy", S.Trophies, _page == PageTrophies, () => Navigate(PageTrophies)));
        nav.Children.Add(NavItem("image", S.Captures, _page == PageCaptures, () => Navigate(PageCaptures)));
        int pending = _jobs.Count(j => j.State is JobState.Waiting or JobState.Running);
        nav.Children.Add(NavItem("download", S.InstallNav, _page == PageInstall, () => Navigate(PageInstall), pending > 0 ? pending.ToString(S.Culture) : null));
        nav.Children.Add(new Border { Height = 1, Background = Kit.B(P.Border), Margin = new Thickness(8, 12) });
        nav.Children.Add(NavItem("settings", S.Settings, _page == PageSettings, () => Navigate(PageSettings)));
        s.Children.Add(new ScrollViewer { Content = nav, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        _sidebarHost.Child = s;
    }

    Control NavItem(string icon, string text, bool selected, Action open, string? count = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        g.Children.Add(new Border { Margin = new Thickness(0, 0, 12, 0), Child = _k.Icon(icon, 19, selected ? P.Accent : P.Muted) });
        g.Children.Add(Kit.At(_k.Text(text, 14, selected ? FontWeight.SemiBold : FontWeight.Medium, selected ? P.Text : P.TextSoft), col: 1));
        if (count != null) g.Children.Add(Kit.At(_k.Text(count, 12, FontWeight.Medium, P.Muted), col: 2));
        return Kit.Click(new Border
        {
            Height = 42, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(12), Child = g,
            Background = selected ? Kit.B(P.Highlight) : Brushes.Transparent,
            BorderBrush = selected ? Kit.B(P.HighlightBorder) : Brushes.Transparent, BorderThickness = new Thickness(1),
        }, open);
    }

    /// <summary>"Abrir Sistema do Vita": the firmware's shell in one click (or Ctrl+H) from any page.</summary>
    Control LaunchButton()
    {
        // while the Vita system runs, the button goes to its window
        bool shellRunning = _running != null && _runningId == "shell";
        bool enabled = ShellInstalled && (_running == null || shellRunning);
        var fg = enabled ? P.OnAccent : P.Faint;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Kit.B(enabled ? P.OnAccent : P.Border, enabled ? 0.16 : 1),
            Child = new Border { Margin = new Thickness(2, 0, 0, 0), Child = _k.Icon("play", 13, fg, fill: true) },
        });
        var texts = _k.VStack(0, _k.Text(S.OpenVitaSystem, 14, FontWeight.SemiBold, fg),
            enabled ? _k.Text(shellRunning ? S.GoToGame : S.ShortcutAnywhere, 11, FontWeight.Medium, P.OnAccent).Also(x => x.Opacity = 0.75)
                : _k.Text(!ShellInstalled ? S.InstallFirmwareFirst : S.GameRunningShort, 11, color: P.Faint));
        texts.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(Kit.At(texts, col: 1));
        var visual = new Border
        {
            Height = 54, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(16), Child = g,
            Background = enabled
                ? new LinearGradientBrush { StartPoint = Kit.Rel(0, 0), EndPoint = Kit.Rel(1, 1), GradientStops = { new GradientStop(P.Accent, 0), new GradientStop(P.Accent2, 1) } }
                : Kit.B(P.Surface),
            BorderBrush = Kit.B(enabled ? Colors.Transparent : P.Border), BorderThickness = new Thickness(1),
            BoxShadow = enabled ? BoxShadows.Parse(P.Dark ? "0 8 24 0 #406E9BFF" : "0 8 20 0 #403767DB") : default,
        };
        return Kit.Click(visual, LaunchSystem, shellRunning ? S.GoToGameTip : S.OpenVitaSystemTip, enabled);
    }

    Control? RunningCard()
    {
        if (_running == null) return null;
        var head = _k.HStack(8, _k.Dot(P.Ok), _k.Text(S.RunningNow, 12.5, FontWeight.SemiBold, P.Ok));
        // the name and "Ir para o jogo" bring the game's window to the front (it runs in its own process and window)
        var name = Kit.Click(_k.Text(_runningTitle ?? "", 13.5, FontWeight.Medium), GoToGame, S.GoToGameTip);
        name.HorizontalAlignment = HorizontalAlignment.Left;
        var go = _k.Primary(S.GoToGame, "monitor", GoToGame, 34, 0, tip: S.GoToGameTip);
        go.HorizontalAlignment = HorizontalAlignment.Stretch;
        var stop = _k.Secondary(_stopAsked ? S.ForceStop : S.Stop, "stop", StopRunning, 34, P.Danger);
        stop.HorizontalAlignment = HorizontalAlignment.Stretch;
        var body = _k.VStack(8, head, name, go, stop);
        return new Border
        {
            Background = Kit.B(P.Surface), BorderBrush = Kit.B(P.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 12), Child = body,
        };
    }

    void SetTheme(string theme)
    {
        _settings.UiTheme = theme;
        SaveSettings();
        LauncherApp.ApplyTheme(theme);
        RebuildAll();
    }

    // ------------------------------------------------------------------ status bar and log

    void BuildStatusBar()
    {
        _statusBar.Children.Clear();
        _statusBar.Background = Kit.B(P.Sidebar);
        _status.Foreground = Kit.B(P.Muted);
        _status.FontSize = 12;
        _status.Margin = new Thickness(16, 0);
        var log = Kit.Click(_k.HStack(6, _k.Icon("terminal", 14, P.Muted), _k.Text(_logPanel.IsVisible ? S.HideLog : S.ShowLog, 12, FontWeight.Medium, P.TextSoft))
            .Also(h => h.VerticalAlignment = VerticalAlignment.Center), ToggleLog);
        log.Margin = new Thickness(0, 7, 16, 7);
        DockPanel.SetDock(log, Dock.Right);
        _statusBar.Children.Add(log);
        _statusBar.Children.Add(_status);
    }

    void ToggleLog()
    {
        _logPanel.IsVisible = !_logPanel.IsVisible;
        _settings.ShowLog = _logPanel.IsVisible;
        SaveSettings();
        BuildStatusBar();
    }

    void SetStatus(string s) => _status.Text = s;

    // Log lines are queued and flushed to the text box a few times per second: games can print hundreds of lines
    // per second, and rebuilding the whole text per line kept a core busy and starved the game process.
    readonly Queue<string> _logLines = new();
    int _logChars;
    bool _logFlushPending;
    const int MaxLogLines = 2000;

    /// <summary>The last lines the game process printed (ui-render's launch check).</summary>
    internal IReadOnlyCollection<string> LogLines => _logLines;

    void AppendLog(string line)
    {
        _logLines.Enqueue(line);
        _logChars += line.Length + 1;
        while (_logLines.Count > MaxLogLines) _logChars -= _logLines.Dequeue().Length + 1;
        if (_logFlushPending) return;
        _logFlushPending = true;
        DispatcherTimer.RunOnce(FlushLog, TimeSpan.FromMilliseconds(250));
    }

    void FlushLog()
    {
        _logFlushPending = false;
        var sb = new System.Text.StringBuilder(_logChars + 16);
        foreach (var l in _logLines) sb.Append(l).Append(Environment.NewLine);
        var text = sb.ToString();
        _log.Text = text;
        _log.CaretIndex = text.Length;
    }

    // ------------------------------------------------------------------ keyboard

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.H) { LaunchSystem(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F) { Navigate(PageLibrary); _search?.Focus(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F5) { Refresh(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Escape && _overlay.IsVisible) { CloseOverlay(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Escape && _page == PageGame) { Navigate(PageLibrary); e.Handled = true; }
    }

    // ------------------------------------------------------------------ library data

    void Refresh(bool rebuild = true)
    {
        _all = GameLibrary.Scan(Program.Ux0);
        _play = PlayHistory.Load();
        try { _fw = VitaSharp.Core.Firmware.FirmwareInfo.Read(Program.FsRoot); } catch (IOException) { _fw = null; }
        _trophies.Clear();
        _shell?.SetApps(_all);
        if (_pageGame != null) _pageGame = _all.FirstOrDefault(g => g.TitleId == _pageGame.TitleId);
        if (rebuild) { RebuildSidebar(); ShowPage(); }
    }

    PlayRecord? PlayOf(string titleId) => _play.GetValueOrDefault(titleId);

    /// <summary>Trophy sets per game folder (TROPHY.TRP + recorded unlocks), read once per refresh.</summary>
    readonly Dictionary<string, List<TrophySetView>> _trophies = new();

    List<TrophySetView> TrophiesOf(GameEntry g)
    {
        if (!_trophies.TryGetValue(g.Path, out var sets))
            _trophies[g.Path] = sets = TrophySetView.ForGame(g.Path, Program.FsRoot, TrophySetView.VitaLanguage(System.Globalization.CultureInfo.CurrentUICulture));
        return sets;
    }

    (int Got, int Total) TrophyCount(GameEntry g)
    {
        var sets = TrophiesOf(g);
        return (sets.Sum(s => s.UnlockedCount), sets.Sum(s => s.Package.Trophies.Count));
    }

    // ------------------------------------------------------------------ firmware state

    /// <summary>The firmware is installed with its shell (needed for <see cref="LaunchSystem"/>).</summary>
    static bool ShellInstalled => File.Exists(Path.Combine(Program.FsRoot, "vs0", "vsh", "shell", "shell.self"));

    /// <summary>firmware.json / detected firmware, read at each refresh.</summary>
    VitaSharp.Core.Firmware.FirmwareInfo? _fw;

    bool FirmwareInstalled => _fw != null || ShellInstalled;

    string FirmwareVersion => _fw?.Version ?? "?";

    // ------------------------------------------------------------------ launching

    /// <summary>Starts <paramref name="g"/> or opens its LiveArea first, as "Ao escolher um jogo" says.</summary>
    void DefaultAction(GameEntry g)
    {
        if (_settings.GameClick == LauncherSettings.ClickLiveArea) OpenLiveArea(g);
        else Launch(g);
    }

    void Launch(GameEntry g) => StartEmulator(g.Title, g.TitleId, ["run", g.Path], g);

    void LoadState(GameEntry g, string file) => StartEmulator(g.Title, g.TitleId, ["run", g.Path, $"--load-state={file}"], g);

    /// <summary>"Sistema do Vita": boots the firmware's own shell (vs0:vsh/shell/shell.self) in the emulator window: its
    /// home screen and LiveArea; the games it starts run as processes of the same emulator session.</summary>
    void LaunchSystem()
    {
        if (!ShellInstalled) { Navigate(PageSystem); return; }
        StartEmulator(S.VitaSystem, "shell", ["boot-shell"], null);
    }

    bool _stopAsked;

    /// <summary>"Ir para o jogo": the running game's window comes to the front with the focus (restored if minimized).</summary>
    internal void GoToGame()
    {
        if (_running is not { } p) return;
        var line = GameWindowActivator.Activate(p);
        AppendLog(line);
        LastFocusResultForTests = line;
    }

    /// <summary>The log line of the last <see cref="GoToGame"/> (ui-render's focus check).</summary>
    internal string? LastFocusResultForTests { get; private set; }

    /// <summary>A click on <paramref name="g"/> while it runs goes to its window instead.</summary>
    bool IsRunningGame(GameEntry g) => _running != null && _runningGame?.TitleId == g.TitleId;

    void StartEmulator(string title, string titleId, string[] command, GameEntry? g)
    {
        // starting what already runs (its "Jogar", the LiveArea's Start, "Abrir Sistema do Vita") goes to its window
        if (_running != null && _runningId == titleId) { GoToGame(); return; }
        if (_running != null) { SetStatus(S.AlreadyRunning(_runningTitle ?? "")); return; }
        CloseOverlay();
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        // a framework-dependent build runs through "dotnet VitaSharp.Desktop.dll"
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]); // the .dll path
        foreach (var c in command) psi.ArgumentList.Add(c);
        foreach (var o in _settings.RunOptions(g?.TitleId)) psi.ArgumentList.Add(o);
        foreach (var o in ExtraRunOptions) psi.ArgumentList.Add(o);
        psi.Environment[EnvVars.Data] = Program.DataRoot;
        // the RPCN password goes in the environment of the game process, not on its command line
        if (_settings.RealInternet && _settings.UsesRpcn) psi.Environment[EnvVars.RpcnPassword] = _settings.RpcnPassword;

        _logLines.Clear(); _logChars = 0; _log.Text = "";
        AppendLog($"> {title} ({titleId})");
        try
        {
            var p = Process.Start(psi)!;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Dispatcher.UIThread.Post(() => AppendLog(e.Data)); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Dispatcher.UIThread.Post(() => AppendLog(e.Data)); };
            p.Exited += (_, _) => Dispatcher.UIThread.Post(() => OnGameExited(p));
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _running = p;
            _runningGame = g;
            _runningTitle = title;
            _runningId = titleId;
            _runStartUtc = DateTime.UtcNow;
            _stopAsked = false;
            if (g != null) _shell?.SetRunning(ShellAppOf(g));
            SetStatus(S.Running(title));
            RebuildSidebar();
            if (_page is PageGame or PageLibrary) ShowPage();
        }
        catch (Exception e)
        {
            SetStatus(S.CouldNotStart(e.Message));
        }
    }

    /// <summary>First click: close the game's window (a "Gravar tudo" recording is saved before it closes); a second
    /// click forces the stop.</summary>
    void StopRunning()
    {
        try
        {
            if (!_stopAsked && _running?.CloseMainWindow() == true) { _stopAsked = true; SetStatus(S.Closing); RebuildSidebar(); }
            else _running?.Kill(true);
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>Raised when the emulator process ended (ui-render's launch check).</summary>
    internal event Action<int>? EmulatorExited;

    void OnGameExited(Process p)
    {
        int code = p.ExitCode;
        var title = _runningTitle;
        if (_runningId != null) _play = PlayHistory.Add(_runningId, _runStartUtc);
        _running = null;
        _runningGame = null;
        _runningTitle = _runningId = null;
        _shell?.SetRunning(null);
        p.Dispose();
        Refresh(); // trophies earned and time played
        SetStatus(code == 0 ? S.Closed(title ?? "") : S.StoppedWithCode(title ?? "", code));
        if (code != 0 && !_logPanel.IsVisible) ToggleLog();
        EmulatorExited?.Invoke(code);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_firmwarePanel is { Busy: true } fw)
        {
            // cancel the firmware installation and close once it has cleaned up its staging folder
            e.Cancel = true;
            fw.Finished += Close;
            fw.CancelRunning();
            return;
        }
        LeavePage();
        // the game closes its own window: a "Gravar tudo" recording is completed first (killing it lost the recording)
        try { if (_running?.CloseMainWindow() != true) _running?.Kill(true); } catch (InvalidOperationException) { }
        foreach (var j in _jobs) j.Cts?.Cancel();
        _shell?.Dispose();
        LauncherApp.PaletteChanged -= OnPaletteChanged;
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ LiveArea and the recreated home screen

    static Shell.ShellApp ShellAppOf(GameEntry g) => new(g.TitleId, g.Title, g.Version, g.Path, g.IconPath, false, g);

    Shell.ShellView EnsureShell()
    {
        if (_shell != null) return _shell;
        _shell = new Shell.ShellView(_settings, Program.FsRoot);
        // a system app runs from its vs0:app folder through the same "run" path as a game
        _shell.LaunchRequested += a => Launch(a.Game ?? new GameEntry(a.TitleId, a.Title, a.Version, "gd", a.Path, a.IconPath, 0));
        _shell.LiveAreaClosed += () => { if (_overlay.IsVisible) CloseOverlay(); };
        _shell.SetApps(_all);
        if (_runningGame != null) _shell.SetRunning(ShellAppOf(_runningGame));
        return _shell;
    }

    static void Detach(Control c)
    {
        switch (c.Parent)
        {
            case Panel p: p.Children.Remove(c); break;
            case Decorator d: d.Child = null; break;
            case ContentControl cc: cc.Content = null; break;
        }
    }

    /// <summary>"Abrir a LiveArea": the game's LiveArea (its sce_sys/livearea template) over the page; Start runs it.</summary>
    void OpenLiveArea(GameEntry g)
    {
        var shell = EnsureShell();
        Detach(shell);
        _overlay.Children.Clear();
        _overlay.Background = Kit.B(Colors.Black, 0.6);
        var frame = new Border
        {
            Child = shell, CornerRadius = new CornerRadius(22), ClipToBounds = true, Margin = new Thickness(60, 40),
            BorderBrush = Kit.B(P.BorderStrong), BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 24 60 0 #66000000"),
        };
        var close = _k.Secondary("", "x", CloseOverlay, 40);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, 16, 16, 0);
        _overlay.Children.Add(frame);
        _overlay.Children.Add(close);
        _overlay.IsVisible = true;
        shell.IsVisible = true;
        shell.OpenLiveArea(ShellAppOf(g));
    }

    void CloseOverlay()
    {
        if (!_overlay.IsVisible) return;
        _overlay.IsVisible = false;
        if (_shell != null && _shell.Parent is Control parent && parent.Parent == _overlay)
        {
            _shell.CloseLiveArea(sound: false);
            Detach(_shell);
        }
        _overlay.Children.Clear();
        if (_page == PageLibrary && _settings.View == LauncherSettings.ViewHome) ShowPage();
    }

    // ------------------------------------------------------------------ dialogs

    async Task ShowMessage(string title, string message)
    {
        var ok = _k.Primary(S.Ok, null, () => { }, 40, 90);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        var panel = _k.VStack(16, _k.Heading(title, 19), _k.Wrap(message, 13.5, P.TextSoft), ok);
        panel.Margin = new Thickness(24);
        var dlg = Dialog(panel, 480);
        ok.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
    }

    async Task<bool> Confirm(string message, string? yesText = null, bool danger = false)
    {
        bool result = false;
        var dlg = (Window?)null;
        var yes = danger ? _k.Secondary(yesText ?? S.Yes, null, () => { result = true; dlg?.Close(); }, 40, P.Danger)
                         : _k.Primary(yesText ?? S.Yes, null, () => { result = true; dlg?.Close(); }, 40, 90);
        var no = _k.Secondary(S.No, null, () => dlg?.Close(), 40);
        var buttons = _k.HStack(8, no, yes);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var panel = _k.VStack(20, _k.Wrap(message, 14, P.Text), buttons);
        panel.Margin = new Thickness(24);
        dlg = Dialog(panel, 440);
        await dlg.ShowDialog(this);
        return result;
    }

    Window Dialog(Control content, double width) => new()
    {
        Title = "VitaSharp", Width = width, SizeToContent = SizeToContent.Height, CanResize = false, Content = content,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Kit.B(P.Surface), FontFamily = Kit.Inter,
        RequestedThemeVariant = P.Dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
    };

    static void OpenPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>Shows a file in the system file manager (selected on Windows), else opens its folder.</summary>
    static void Reveal(string file)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
            else OpenPath(Path.GetDirectoryName(file) ?? file);
        }
        catch (Exception) { }
    }
}
