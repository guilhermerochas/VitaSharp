using VitaSharp.Core.Util;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VitaSharp.Core.Package;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Game library: lists installed titles, installs new ones and launches the emulator for the selected game.</summary>
public sealed class MainWindow : Window
{
    static readonly IBrush Accent = LauncherTheme.Accent;
    static readonly IBrush Muted = LauncherTheme.Muted;
    static readonly IBrush Panel = LauncherTheme.Panel;
    static readonly IBrush WindowBackground = LauncherTheme.WindowBackground;

    readonly LauncherSettings _settings;
    readonly ObservableCollection<GameEntry> _visible = new();
    readonly Dictionary<string, Bitmap?> _icons = new();
    List<GameEntry> _all = new();

    readonly TextBox _search;
    readonly ListBox _list;
    readonly TextBlock _status;
    readonly TextBox _log;
    readonly Border _logPanel;
    readonly StackPanel _details;
    readonly TextBlock _emptyHint;
    readonly ProgressBar _progress;
    readonly Button _cancelInstall;
    readonly List<Button> _installButtons = new();
    CancellationTokenSource? _installCts;
    Process? _running;
    GameEntry? _runningGame;

    /// <summary>"Tela inicial": the Vita-style home screen and LiveArea (created when first shown).</summary>
    Shell.ShellView? _shell;
    readonly DockPanel _listBody;
    readonly Panel _bodyHost;
    readonly ToggleButton _viewList, _viewHome;
    readonly StackPanel _viewSwitch;

    public Shell.ShellView? ShellView => _shell;

    /// <param name="settings">Preferences to use instead of the saved ones (ui-snapshot); changes to them are not saved.</param>
    public MainWindow(LauncherSettings? settings = null)
    {
        _settings = settings ?? LauncherSettings.Load();
        _saveSettings = settings == null;
        Title = "VitaSharp";
        Width = 1100;
        Height = 700;
        MinWidth = 760;
        MinHeight = 480;
        Background = WindowBackground;

        _search = new TextBox { Watermark = "Search games", Width = 260, VerticalAlignment = VerticalAlignment.Center };
        _search.TextChanged += (_, _) => ApplyFilter();

        var toolbar = new DockPanel { Margin = new Thickness(16, 12), LastChildFill = false };
        var brand = new TextBlock { Text = "VitaSharp", FontSize = 22, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(brand, Dock.Left);
        toolbar.Children.Add(brand);
        // view switch: the game list or the Vita home screen
        _viewList = new ToggleButton { Content = "Lista", CornerRadius = new CornerRadius(4, 0, 0, 4) };
        _viewHome = new ToggleButton { Content = "Tela inicial", CornerRadius = new CornerRadius(0, 4, 4, 0) };
        ToolTip.SetTip(_viewHome, "Tela inicial do Vita: bolhas, LiveArea e o tema do firmware instalado");
        _viewList.Click += (_, _) => SetView(LauncherSettings.ViewList, save: true);
        _viewHome.Click += (_, _) => SetView(LauncherSettings.ViewHome, save: true);
        // the recreated home screen is provisional until the firmware's own shell.self runs: off by default, the
        // toolbar switch only shows once it was turned on in Configurações
        _viewSwitch = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var viewSwitch = _viewSwitch;
        viewSwitch.Children.Add(_viewList);
        viewSwitch.Children.Add(_viewHome);
        DockPanel.SetDock(viewSwitch, Dock.Left);
        toolbar.Children.Add(viewSwitch);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_search);
        // one "Add game" button with a drop-down (VPK, PKG, folder) instead of three toolbar buttons
        var addGame = new Button { Content = "Add game ▾", VerticalAlignment = VerticalAlignment.Center };
        var addMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var (text, action) in new (string, Func<Task>)[] { ("Install VPK…", InstallVpk), ("Install PKG…", InstallPkg), ("Add game folder…", AddFolder) })
        {
            var item = new MenuItem { Header = text };
            item.Click += async (_, _) => await action();
            addMenu.Items.Add(item);
        }
        addGame.Flyout = addMenu;
        _installButtons.Add(addGame);
        actions.Children.Add(addGame);
        actions.Children.Add(MakeButton("Refresh", () => { Refresh(); return Task.CompletedTask; }));
        actions.Children.Add(MakeButton("Settings", OpenSettings));
        DockPanel.SetDock(actions, Dock.Right);
        toolbar.Children.Add(actions);

        _list = new ListBox
        {
            ItemsSource = _visible,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<GameEntry>((g, _) => GameRow(g), true),
        };
        _list.SelectionChanged += (_, _) => ShowDetails(_list.SelectedItem as GameEntry);
        _list.DoubleTapped += (_, _) => { if (_list.SelectedItem is GameEntry g) Launch(g); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && _list.SelectedItem is GameEntry g) Launch(g); };

        _emptyHint = new TextBlock
        {
            Text = "No games installed yet.\nUse \"Add game ▾\": Install VPK… (or drop a .vpk here), Install PKG… (package + work.bin/.rif license) or Add game folder… (a decrypted game folder).",
            Foreground = Muted, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24),
        };
        var listHost = new Panel();
        listHost.Children.Add(_list);
        listHost.Children.Add(_emptyHint);

        _details = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        var detailsPanel = new Border { Background = Panel, Width = 320, Child = new ScrollViewer { Content = _details } };

        _log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12 };
        _logPanel = new Border { Height = 160, Background = Panel, Child = _log, IsVisible = _settings.ShowLog, Margin = new Thickness(0, 1, 0, 0) };

        _status = new TextBlock { Foreground = Muted, Margin = new Thickness(16, 6), VerticalAlignment = VerticalAlignment.Center };
        var logToggle = new CheckBox { Content = "Show log", IsChecked = _settings.ShowLog, Margin = new Thickness(0, 0, 12, 0) };
        logToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.ShowLog = logToggle.IsChecked == true;
            _logPanel.IsVisible = _settings.ShowLog;
            SaveSettings();
        };
        _progress = new ProgressBar { Width = 220, Minimum = 0, Maximum = 1, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _cancelInstall = new Button { Content = "Cancel", IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
        _cancelInstall.Click += (_, _) => _installCts?.Cancel();
        var statusBar = new DockPanel { Background = Panel };
        DockPanel.SetDock(logToggle, Dock.Right);
        DockPanel.SetDock(_cancelInstall, Dock.Right);
        DockPanel.SetDock(_progress, Dock.Right);
        statusBar.Children.Add(logToggle);
        statusBar.Children.Add(_cancelInstall);
        statusBar.Children.Add(_progress);
        statusBar.Children.Add(_status);

        var body = _listBody = new DockPanel();
        DockPanel.SetDock(detailsPanel, Dock.Right);
        body.Children.Add(detailsPanel);
        body.Children.Add(listHost);
        _bodyHost = new Panel();
        _bodyHost.Children.Add(body);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        DockPanel.SetDock(_logPanel, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(statusBar);
        root.Children.Add(_logPanel);
        root.Children.Add(_bodyHost);
        Content = root;

        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        Refresh();
        ShowDetails(_list.SelectedItem as GameEntry);
        SetView(_settings.View, save: false);
    }

    readonly bool _saveSettings;

    void SaveSettings()
    {
        if (_saveSettings) _settings.Save();
    }

    // ------------------------------------------------------------------ views

    /// <summary>Shows the game list or the home screen ("Tela inicial"), and remembers the choice.</summary>
    public void SetView(string view, bool save)
    {
        bool home = view == LauncherSettings.ViewHome;
        _settings.View = home ? LauncherSettings.ViewHome : LauncherSettings.ViewList;
        if (save) SaveSettings();
        _viewList.IsChecked = !home;
        _viewHome.IsChecked = home;
        _viewSwitch.IsVisible = home;
        if (home && _shell == null)
        {
            _shell = new Shell.ShellView(_settings, Program.FsRoot);
            // a system app runs from its vs0:app folder through the same "run" path as a game
            _shell.LaunchRequested += a => Launch(a.Game ?? new GameEntry(a.TitleId, a.Title, a.Version, "gd", a.Path, a.IconPath, 0));
            _shell.SetApps(_visible);
            if (_runningGame != null) _shell.SetRunning(ShellAppOf(_runningGame));
            _bodyHost.Children.Add(_shell);
        }
        _listBody.IsVisible = !home;
        if (_shell != null) _shell.IsVisible = home;
        if (home) _shell!.Home.Focus();
    }

    static Shell.ShellApp ShellAppOf(GameEntry g) => new(g.TitleId, g.Title, g.Version, g.Path, g.IconPath, false, g);

    // ------------------------------------------------------------------ library

    void Refresh()
    {
        string? selected = (_list.SelectedItem as GameEntry)?.TitleId;
        _all = GameLibrary.Scan(Program.Ux0);
        _trophies.Clear();
        ApplyFilter();
        if (selected != null) _list.SelectedItem = _visible.FirstOrDefault(g => g.TitleId == selected);
        if (_list.SelectedItem == null && _visible.Count > 0) _list.SelectedIndex = 0;
        SetStatus(_running != null ? $"Running {_runningGame?.Title}" : $"{_all.Count} game(s) installed — data folder: {Program.DataRoot}");
    }

    void ApplyFilter()
    {
        string q = _search.Text?.Trim() ?? "";
        _visible.Clear();
        foreach (var g in _all.Where(g => q.Length == 0
                     || g.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                     || g.TitleId.Contains(q, StringComparison.OrdinalIgnoreCase)))
            _visible.Add(g);
        _emptyHint.IsVisible = _visible.Count == 0;
        if (_all.Count > 0 && _visible.Count == 0) _emptyHint.Text = "No game matches the search.";
        _shell?.SetApps(_visible);
    }

    Bitmap? GameIcon(GameEntry g)
    {
        if (g.IconPath == null) return null;
        if (_icons.TryGetValue(g.IconPath, out var b)) return b;
        try { b = new Bitmap(g.IconPath); }
        catch (Exception) { b = null; }
        _icons[g.IconPath] = b;
        return b;
    }

    Control GameRow(GameEntry g)
    {
        var icon = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = Panel,
            Child = GameIcon(g) is { } bmp ? new Image { Source = bmp, Stretch = Stretch.UniformToFill } : new TextBlock { Text = "?", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = g.Title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = $"{g.TitleId}   ·   {g.CategoryText}{(g.Version.Length > 0 ? $"   ·   v{g.Version}" : "")}   ·   {g.SizeText}",
            Foreground = Muted, FontSize = 12,
        });
        var row = new DockPanel { Margin = new Thickness(8, 6) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        if (TrophyBadge(g) is { } badge)
        {
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
        }
        row.Children.Add(text);
        return row;
    }

    // ------------------------------------------------------------------ trophies

    /// <summary>Trophy sets per game folder (TROPHY.TRP + recorded unlocks), read once per refresh.</summary>
    readonly Dictionary<string, List<TrophySetView>> _trophies = new();

    List<TrophySetView> TrophiesOf(GameEntry g)
    {
        if (!_trophies.TryGetValue(g.Path, out var sets))
            _trophies[g.Path] = sets = TrophySetView.ForGame(g.Path, Program.FsRoot, TrophyWindow.VitaLanguage(System.Globalization.CultureInfo.CurrentUICulture));
        return sets;
    }

    /// <summary>"Trophies earned/total" with a progress bar beside a game in the list; clicking it opens the trophy list.</summary>
    Control? TrophyBadge(GameEntry g)
    {
        var sets = TrophiesOf(g);
        int total = sets.Sum(s => s.Package.Trophies.Count), got = sets.Sum(s => s.UnlockedCount);
        if (total == 0) return null;
        var panel = new StackPanel { Spacing = 4, Width = 110 };
        panel.Children.Add(new TextBlock { Text = $"Trophies {got}/{total}", FontSize = 12, Foreground = got > 0 ? Brushes.White : Muted, HorizontalAlignment = HorizontalAlignment.Right });
        panel.Children.Add(new ProgressBar { Value = got * 100.0 / total, Maximum = 100, Height = 4, MinWidth = 0 });
        var button = new Button
        {
            Content = panel, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        ToolTip.SetTip(button, "Show the trophy list");
        button.Click += async (_, e) => { e.Handled = true; await new TrophyWindow(g, sets).ShowDialog(this); };
        return button;
    }

    void ShowDetails(GameEntry? g)
    {
        _details.Children.Clear();
        if (g == null)
        {
            _details.Children.Add(new TextBlock { Text = "Select a game", Foreground = Muted });
            return;
        }
        if (GameIcon(g) is { } bmp)
            _details.Children.Add(new Border { Width = 160, Height = 160, CornerRadius = new CornerRadius(18), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill } });
        _details.Children.Add(new TextBlock { Text = g.Title, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _details.Children.Add(InfoLine("Title ID", g.TitleId));
        _details.Children.Add(InfoLine("Type", g.CategoryText));
        if (g.Version.Length > 0) _details.Children.Add(InfoLine("Version", g.Version));
        _details.Children.Add(InfoLine("Size", g.SizeText));

        bool busy = _running != null;
        var play = new Button
        {
            Content = busy && _runningGame == g ? "Running…" : "Play", IsEnabled = !busy, Background = Accent, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 10),
            FontSize = 16, Margin = new Thickness(0, 8, 0, 0),
        };
        play.Click += (_, _) => Launch(g);
        _details.Children.Add(play);
        if (busy && _runningGame == g)
        {
            var stop = new Button { Content = "Stop", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            stop.Click += (_, _) => { try { _running?.Kill(true); } catch (InvalidOperationException) { } };
            _details.Children.Add(stop);
        }
        var folder = new Button { Content = "Open game folder", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        folder.Click += (_, _) => OpenPath(g.Path);
        _details.Children.Add(folder);
        // trophies: the sets the game ships and what the emulator recorded as unlocked
        var trophySets = TrophiesOf(g);
        int trophyCount = trophySets.Sum(s => s.Package.Trophies.Count), trophyUnlocked = trophySets.Sum(s => s.UnlockedCount);
        var trophies = new Button
        {
            Content = trophyCount > 0 ? $"Trophies ({trophyUnlocked}/{trophyCount})" : "No trophies", IsEnabled = trophyCount > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        trophies.Click += async (_, _) => await new TrophyWindow(g, trophySets).ShowDialog(this);
        _details.Children.Add(trophies);
        var saves = Path.Combine(Program.Ux0, "user", "00", "savedata", g.TitleId);
        var saveBtn = new Button { Content = "Open save data", IsEnabled = Directory.Exists(saves), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        saveBtn.Click += (_, _) => OpenPath(saves);
        _details.Children.Add(saveBtn);
        var remove = new Button { Content = "Uninstall…", IsEnabled = !busy, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        remove.Click += async (_, _) => await Uninstall(g);
        _details.Children.Add(remove);
    }

    static Control InfoLine(string label, string value)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(new TextBlock { Text = label, Foreground = Muted, Width = 70 });
        p.Children.Add(new TextBlock { Text = value });
        return p;
    }

    // ------------------------------------------------------------------ launching

    void Launch(GameEntry g)
    {
        if (_running != null) return;
        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        // a framework-dependent build runs through "dotnet VitaSharp.Desktop.dll"
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]); // the .dll path
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(g.Path);
        foreach (var o in _settings.RunOptions()) psi.ArgumentList.Add(o);
        psi.Environment[EnvVars.Data] = Program.DataRoot;
        // the RPCN password goes in the environment of the game process, not on its command line
        if (_settings.RealInternet && _settings.UsesRpcn) psi.Environment[EnvVars.RpcnPassword] = _settings.RpcnPassword;

        _logLines.Clear(); _logChars = 0; _log.Text = "";
        AppendLog($"> {g.Title} ({g.TitleId})");
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
            _shell?.SetRunning(ShellAppOf(g));
            SetStatus($"Running {g.Title}");
            ShowDetails(_list.SelectedItem as GameEntry);
        }
        catch (Exception e)
        {
            SetStatus($"Could not start the emulator: {e.Message}");
        }
    }

    void OnGameExited(Process p)
    {
        int code = p.ExitCode;
        var title = _runningGame?.Title;
        _running = null;
        _runningGame = null;
        _shell?.SetRunning(null);
        p.Dispose();
        Refresh(); // trophies earned while playing
        SetStatus(code == 0 ? $"{title} closed." : $"{title} stopped with exit code {code}. See the log for details.");
        if (code != 0 && !_logPanel.IsVisible) _logPanel.IsVisible = true;
        ShowDetails(_list.SelectedItem as GameEntry);
    }

    // Log lines are queued and flushed to the text box a few times per second: games can print hundreds of lines
    // per second, and rebuilding the whole text per line kept a core busy and starved the game process.
    readonly Queue<string> _logLines = new();
    int _logChars;
    bool _logFlushPending;
    const int MaxLogLines = 2000;

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

    void SetStatus(string s) => _status.Text = s;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        try { _running?.Kill(true); } catch (InvalidOperationException) { }
        _installCts?.Cancel();
        _shell?.Dispose();
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ installing

    async Task InstallVpk()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a PS Vita VPK or content zip",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PS Vita VPK / zip") { Patterns = ["*.vpk", "*.zip"] }, FilePickerFileTypes.All],
        });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } path && !await InstallVpkFile(path)) break;
    }

    /// <summary>Installs one archive; returns false when it failed or was cancelled.</summary>
    async Task<bool> InstallVpkFile(string archive)
    {
        if (_installCts != null) return false;
        string name = Path.GetFileName(archive);
        var cts = new CancellationTokenSource();
        _installCts = cts;
        SetInstalling(true);
        SetStatus($"Installing {name}…");
        AppendLog($"> Installing {name}");
        int lastPermille = -1;
        try
        {
            var results = await Task.Run(() => VpkInstaller.Install(archive, Program.Ux0, new VpkInstallOptions
            {
                Log = msg => Dispatcher.UIThread.Post(() => AppendLog(msg)),
                Progress = p =>
                {
                    int permille = (int)(p.Fraction * 1000);
                    if (permille == lastPermille) return;
                    lastPermille = permille;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _progress.Value = p.Fraction;
                        SetStatus($"Installing {name}… {p.Fraction:P0}");
                    });
                },
                // runs on the install thread: show the dialog on the UI thread and wait for the answer
                ConfirmOverwrite = c => Dispatcher.UIThread.InvokeAsync(() => Confirm(c.Kind switch
                {
                    VpkContentKind.Patch => $"An update for {c.TitleId} is already installed. Replace it with {(c.Version.Length > 0 ? "version " + c.Version : "this one")}?",
                    VpkContentKind.AddCont => $"This add-on for {c.TitleId} is already installed. Replace it?",
                    _ => $"\"{c.Title}\" ({c.TitleId}) is already installed. Replace it? Save data is kept.",
                })).GetAwaiter().GetResult(),
            }, cts.Token), cts.Token);

            foreach (var r in results)
                AppendLog(r.Installed ? $"Installed {r.TitleId} \"{r.Content.Title}\" ({r.Content.Kind}) -> {r.InstallPath}"
                                      : $"Skipped {r.TitleId} ({r.Content.Kind}): already installed");
            Refresh();
            var installed = results.Where(r => r.Installed).ToList();
            if (installed.Count == 0)
            {
                SetStatus($"Nothing installed from {name}.");
                return true;
            }
            var app = installed.FirstOrDefault(r => r.Content.Kind == VpkContentKind.App) ?? installed[0];
            _list.SelectedItem = _visible.FirstOrDefault(g => g.TitleId == app.TitleId) ?? _list.SelectedItem;
            SetStatus(string.Join("  ", installed.Select(r => r.Content.Kind switch
            {
                VpkContentKind.Patch => $"Installed update for {r.TitleId}{(r.Content.Version.Length > 0 ? $" (v{r.Content.Version})" : "")}.",
                VpkContentKind.AddCont => $"Installed add-on for {r.TitleId}.",
                _ => $"{(r.Replaced ? "Reinstalled" : "Installed")} {r.Content.Title}.",
            })));
            return true;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Install cancelled.");
            SetStatus($"Install of {name} cancelled.");
            return false;
        }
        catch (Exception e)
        {
            AppendLog("Install failed: " + e.Message);
            SetStatus("Install failed.");
            await ShowMessage(e is VpkLicenseException ? "License required" : "Install failed",
                $"{name} could not be installed.\n\n{e.Message}");
            return false;
        }
        finally
        {
            _installCts = null;
            cts.Dispose();
            SetInstalling(false);
        }
    }

    void SetInstalling(bool installing)
    {
        _progress.Value = 0;
        _progress.IsVisible = installing;
        _cancelInstall.IsVisible = installing;
        foreach (var b in _installButtons) b.IsEnabled = !installing;
    }

    async Task InstallPkg()
    {
        var pkgs = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a PS Vita package (.pkg)",
            FileTypeFilter = [new FilePickerFileType("PS Vita package") { Patterns = ["*.pkg"] }],
        });
        var pkg = pkgs.FirstOrDefault()?.TryGetLocalPath();
        if (pkg == null) return;
        var lics = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the license for this package (work.bin or .rif)",
            FileTypeFilter = [new FilePickerFileType("License") { Patterns = ["*.bin", "*.rif"] }, FilePickerFileTypes.All],
        });
        var lic = lics.FirstOrDefault()?.TryGetLocalPath();
        if (lic == null) return;
        await InstallPkgFiles(pkg, lic);
    }

    async Task InstallPkgFiles(string pkg, string lic)
    {
        SetStatus($"Installing {Path.GetFileName(pkg)}…");
        _logPanel.IsVisible = true;
        try
        {
            var t = await Task.Run(() => PkgInstaller.Install(pkg, lic, Program.Ux0,
                msg => Dispatcher.UIThread.Post(() => AppendLog(msg)),
                p => Dispatcher.UIThread.Post(() => SetStatus($"Installing {Path.GetFileName(pkg)}… {p:P0}"))));
            AppendLog($"Installed {t.TitleId} \"{t.Title}\"");
            Refresh();
            _list.SelectedItem = _visible.FirstOrDefault(g => g.TitleId == t.TitleId);
            SetStatus($"Installed {t.Title}.");
        }
        catch (Exception e)
        {
            AppendLog("Install failed: " + e.Message);
            SetStatus("Install failed: " + e.Message);
        }
    }

    async Task AddFolder()
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select a decrypted game folder (contains eboot.bin and sce_sys)" });
        var dir = dirs.FirstOrDefault()?.TryGetLocalPath();
        if (dir != null) await AddFolderPath(dir);
    }

    async Task AddFolderPath(string dir)
    {
        var entry = GameLibrary.Read(dir);
        if (entry == null)
        {
            SetStatus("That folder is not a game: eboot.bin was not found.");
            return;
        }
        var dest = Path.Combine(Program.Ux0, "app", entry.TitleId);
        if (Path.GetFullPath(dir).TrimEnd('\\', '/').Equals(Path.GetFullPath(dest).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            Refresh();
            return;
        }
        if (Directory.Exists(dest) && !await Confirm($"{entry.TitleId} is already installed. Replace it?")) return;
        SetStatus($"Copying {entry.Title}…");
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                CopyDirectory(dir, dest);
            });
            Refresh();
            _list.SelectedItem = _visible.FirstOrDefault(g => g.TitleId == entry.TitleId);
            SetStatus($"Added {entry.Title}.");
        }
        catch (Exception e)
        {
            SetStatus("Copy failed: " + e.Message);
        }
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList() ?? [];
        var pkg = items.FirstOrDefault(p => p.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase));
        var lic = items.FirstOrDefault(p => p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".rif", StringComparison.OrdinalIgnoreCase));
        var archives = items.Where(p => File.Exists(p) && (p.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))).ToList();
        if (archives.Count > 0)
        {
            foreach (var a in archives)
                if (!await InstallVpkFile(a)) break;
        }
        else if (pkg != null && lic != null) await InstallPkgFiles(pkg, lic);
        else if (pkg != null) SetStatus("Drop the .pkg together with its work.bin/.rif license, or use Install PKG…");
        else foreach (var dir in items.Where(Directory.Exists)) await AddFolderPath(dir);
    }

    async Task Uninstall(GameEntry g)
    {
        if (!await Confirm($"Uninstall \"{g.Title}\" ({g.TitleId})? Save data is kept.")) return;
        try
        {
            await Task.Run(() => Directory.Delete(g.Path, true));
            SetStatus($"Uninstalled {g.Title}.");
        }
        catch (Exception e) { SetStatus("Uninstall failed: " + e.Message); }
        Refresh();
    }

    // ------------------------------------------------------------------ dialogs

    async Task OpenSettings()
    {
        _shell?.ReleaseController();
        await new SettingsDialog(_settings).ShowDialog(this);
        SetView(_settings.View, save: false);
        _shell?.ApplySettings();
    }

    async Task ShowMessage(string title, string message)
    {
        var ok = new Button { Content = "OK", Background = Accent, Foreground = Brushes.White, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(ok);
        var dlg = new Window { Title = "VitaSharp", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = WindowBackground };
        ok.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
    }

    async Task<bool> Confirm(string message)
    {
        bool result = false;
        var yes = new Button { Content = "Yes", Background = Accent, Foreground = Brushes.White, IsDefault = true };
        var no = new Button { Content = "No", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        var dlg = new Window { Title = "VitaSharp", Width = 400, SizeToContent = SizeToContent.Height, CanResize = false, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = WindowBackground };
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    static Button MakeButton(string text, Func<Task> action)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        b.Click += async (_, _) => await action();
        return b;
    }

    static void OpenPath(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { }
    }
}
