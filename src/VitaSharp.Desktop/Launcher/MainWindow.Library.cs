using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Biblioteca: search, filters, the "continue playing" card and the games as large covers, a list or the
/// recreated home screen; the first-run page when nothing is installed.</summary>
public sealed partial class MainWindow
{
    public const string FilterAll = "all", FilterRecent = "recent", FilterTrophies = "trophies", FilterPlayable = "playable";

    TextBox? _search;
    string _filter = FilterAll;
    Border _filtersHost = new();
    Border _resultsHost = new();

    Control LibraryPage()
    {
        if (_all.Count == 0 && _jobs.Count == 0)
        {
            return WelcomePage();
        }

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(36, 28, 36, 0) };
        _filtersHost = new Border();
        _resultsHost = new Border();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(_k.VStack(2, _k.Heading(S.Library, 30), _k.Text(S.LibrarySubtitle(_all.Count, _all.Count(g => g.Category == "gd")), 13.5, color: P.Muted)));
        _search = new TextBox
        {
            Text = _search?.Text ?? "",
            Watermark = S.SearchGames,
            Width = 340,
            Height = 46,
            CornerRadius = new CornerRadius(14),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(40, 0, 12, 0),
            FontSize = 14,
            Background = Kit.B(P.Surface),
            BorderBrush = Kit.B(P.BorderStrong),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _search.TextChanged += (_, _) => RebuildResults();
        var searchBox = new Grid { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        searchBox.Children.Add(_search);
        searchBox.Children.Add(new Border { Margin = new Thickness(14, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, Child = _k.Icon("search", 18, P.Muted) });
        searchBox.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false, Child = _k.Kbd("Ctrl F") });
        header.Children.Add(Kit.At(searchBox, col: 1));
        var refresh = _k.Secondary("", "refresh", () => Refresh(), 46, tip: S.RefreshTip);
        refresh.Margin = new Thickness(0, 0, 12, 0);
        refresh.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(Kit.At(refresh, col: 2));
        // ux0:app, where the installed games are
        string gamesFolder = VitaSharp.Core.Io.FirmwareLayout.Applications.Host(Program.FsRoot);
        var openGames = _k.Secondary("", "folder", () => { Directory.CreateDirectory(gamesFolder); OpenPath(gamesFolder); }, 46, tip: S.OpenGamesFolderTip(gamesFolder));
        openGames.Margin = new Thickness(0, 0, 12, 0);
        openGames.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(Kit.At(openGames, col: 3));
        var add = InstallMenu(46);
        add.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(Kit.At(add, col: 4));
        page.Children.Add(header);

        page.Children.Add(Kit.At(_filtersHost, row: 1));
        page.Children.Add(Kit.At(_resultsHost, row: 2));
        RebuildFilters();
        RebuildResults();
        return page;
    }

    /// <summary>"Instalar" with its choices: VPK / zip, PKG + license, a decrypted game folder.</summary>
    Button InstallMenu(double height) => _k.PrimaryMenu(S.Install, "plus", height,
        (S.InstallVpkMenu, () => _ = InstallVpk()), (S.InstallPkgMenu, () => _ = InstallPkg()), (S.AddFolderMenu, () => _ = AddFolder()));

    void RebuildFilters()
    {
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        void SetFilter(string f)
        {
            _filter = f;
            RebuildFilters();
            RebuildResults();
        }
        int recent = _all.Count(g => PlayOf(g.TitleId) != null);
        chips.Children.Add(_k.Chip(S.FilterAll(_all.Count), _filter == FilterAll, () => SetFilter(FilterAll)));
        chips.Children.Add(_k.Chip(S.FilterPlayable(_all.Count(g => g.Category == "gd")), _filter == FilterPlayable, () => SetFilter(FilterPlayable)));
        chips.Children.Add(_k.Chip(S.FilterRecent(recent), _filter == FilterRecent, () => SetFilter(FilterRecent)));
        chips.Children.Add(_k.Chip(S.FilterTrophies, _filter == FilterTrophies, () => SetFilter(FilterTrophies)));
        chips.Children.Add(new Border { Width = 1, Height = 22, Background = Kit.B(P.BorderStrong), Margin = new Thickness(6, 6), VerticalAlignment = VerticalAlignment.Center });
        var regions = _all.Select(g => g.Region).Distinct().OrderBy(r => r).ToList();
        var regionItems = new List<(string, bool, Action)> { (S.AllRegions, _settings.LibraryRegion == "", () => { _settings.LibraryRegion = ""; SaveSettings(); RebuildFilters(); RebuildResults(); }) };
        foreach (var r in regions)
        {
            regionItems.Add((r, _settings.LibraryRegion == r, () => { _settings.LibraryRegion = r; SaveSettings(); RebuildFilters(); RebuildResults(); }
            ));
        }

        chips.Children.Add(_k.ChipMenu(S.RegionChip(_settings.LibraryRegion == "" ? S.AllRegionsShort : _settings.LibraryRegion), "globe", regionItems));
        (string Key, string Label)[] sorts = [("az", S.SortAz), ("recent", S.SortRecent), ("time", S.SortTime)];
        chips.Children.Add(_k.ChipMenu(S.SortChip(sorts.FirstOrDefault(s => s.Key == _settings.LibrarySort).Label ?? S.SortAz), null,
            sorts.Select(s => (s.Label, _settings.LibrarySort == s.Key, (Action)(() => { _settings.LibrarySort = s.Key; SaveSettings(); RebuildFilters(); RebuildResults(); }))).ToList()));
        filters.Children.Add(chips);

        var views = new List<(string Text, string? Icon, string? Tip)> { ("", "grid", S.ViewGrid), ("", "list", S.ViewList) };
        if (_settings.RecreatedHome)
        {
            views.Add((S.ViewHome, "vita", S.ViewHomeTip));
        }

        string[] keys = [LauncherSettings.ViewGrid, LauncherSettings.ViewList, LauncherSettings.ViewHome];
        int sel = Math.Max(0, Array.IndexOf(keys, _settings.View));
        if (sel >= views.Count)
        {
            sel = 0;
        }

        var seg = _k.Segmented(sel, i => { _settings.View = keys[i]; SaveSettings(); RebuildResults(); }, views.ToArray());
        seg.VerticalAlignment = VerticalAlignment.Top;
        filters.Children.Add(Kit.At(seg, col: 1));
        _filtersHost.Child = filters;
    }

    List<GameEntry> VisibleGames()
    {
        string q = _search?.Text?.Trim() ?? "";
        IEnumerable<GameEntry> list = _all.Where(g => q.Length == 0
            || g.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || g.TitleId.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (_settings.LibraryRegion.Length > 0)
        {
            list = list.Where(g => g.Region == _settings.LibraryRegion);
        }

        list = _filter switch
        {
            FilterPlayable => list.Where(g => g.Category == "gd"),
            FilterRecent => list.Where(g => PlayOf(g.TitleId) != null).OrderByDescending(g => PlayOf(g.TitleId)!.LastPlayedUtc),
            FilterTrophies => list.Where(g => TrophyCount(g).Total > 0),
            _ => list,
        };
        if (_filter != FilterRecent)
        {
            list = _settings.LibrarySort switch
            {
                "recent" => list.OrderByDescending(g => PlayOf(g.TitleId)?.LastPlayedUtc ?? DateTime.MinValue),
                "time" => list.OrderByDescending(g => PlayOf(g.TitleId)?.Seconds ?? 0),
                _ => list,
            };
        }

        return list.ToList();
    }

    void RebuildResults()
    {
        var games = VisibleGames();
        if (_settings.View == LauncherSettings.ViewHome && _settings.RecreatedHome)
        {
            var shell = EnsureShell();
            Detach(shell);
            shell.SetApps(games);
            shell.IsVisible = true;
            _resultsHost.Child = new Border
            {
                CornerRadius = new CornerRadius(22),
                ClipToBounds = true,
                Child = shell,
                Margin = new Thickness(0, 0, 0, 24),
                BorderBrush = Kit.B(P.Border),
                BorderThickness = new Thickness(1),
                Background = Brushes.Black,
            };
            shell.Home.Focus();
            return;
        }
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 28) };
        bool plain = (_search?.Text ?? "").Trim().Length == 0 && _filter == FilterAll && _settings.LibraryRegion.Length == 0;
        var last = _all.Where(g => PlayOf(g.TitleId) != null).MaxBy(g => PlayOf(g.TitleId)!.LastPlayedUtc);
        if (plain && last != null && _settings.View == LauncherSettings.ViewGrid)
        {
            body.Children.Add(HeroCard(last));
            body.Children.Add(SectionHeader(S.AllGames, null).Also(h => h.Margin = new Thickness(0, 28, 0, 14)));
        }
        if (games.Count == 0)
        {
            body.Children.Add(_k.Wrap(S.NoMatch, 14).Also(t => t.Margin = new Thickness(4, 30)));
        }
        else if (_settings.View == LauncherSettings.ViewList)
        {
            body.Children.Add(GameList(games));
        }
        else
        {
            body.Children.Add(GameGrid(games));
        }

        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _resultsHost.Child = scroll;
    }

    Control SectionHeader(string title, string? link, Action? open = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        g.Children.Add(_k.Text(title, 17, FontWeight.SemiBold));
        if (link != null && open != null)
        {
            g.Children.Add(Kit.At(Kit.Click(_k.Text(link, 13, FontWeight.SemiBold, P.Accent), open), col: 1));
        }

        return g;
    }

    // ------------------------------------------------------------------ "Continuar jogando"

    Control HeroCard(GameEntry g)
    {
        var card = new Grid { Height = 232 };
        card.Children.Add(new Border { CornerRadius = new CornerRadius(24), Background = Kit.Image(g.Pic0Path, 960) });
        card.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new LinearGradientBrush
            {
                StartPoint = Kit.Rel(0, 0),
                EndPoint = Kit.Rel(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(250, P.Scrim.R, P.Scrim.G, P.Scrim.B), 0),
                    new GradientStop(Color.FromArgb(225, P.Scrim.R, P.Scrim.G, P.Scrim.B), 0.42),
                    new GradientStop(Color.FromArgb(40, P.Scrim.R, P.Scrim.G, P.Scrim.B), 1),
                },
            },
        });
        card.Children.Add(new Border { CornerRadius = new CornerRadius(24), BorderBrush = Kit.B(P.Border), BorderThickness = new Thickness(1), IsHitTestVisible = false });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(28, 0) };
        var icon = _k.Picture(g.IconPath, 132, 132, 30, g.Initials);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.BoxShadow = BoxShadows.Parse("0 10 30 0 #55000000");
        row.Children.Add(Kit.Click(icon, () => OpenOrGoTo(g), IsRunningGame(g) ? S.GoToGameTip : S.OpenGamePage));
        var info = new StackPanel { Spacing = 7, Margin = new Thickness(26, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        info.Children.Add(_k.Kicker(S.ContinuePlaying, P.Accent));
        info.Children.Add(Kit.Click(_k.Heading(g.Title, 26), () => OpenOrGoTo(g), IsRunningGame(g) ? S.GoToGameTip : S.OpenGamePage));
        var meta = _k.HStack(14, IconText("clock", S.PlayedSummary(PlayOf(g.TitleId))));
        var (got, total) = TrophyCount(g);
        if (total > 0)
        {
            meta.Children.Add(IconText("trophy", $"{got}/{total}", P.Gold));
            meta.Children.Add(new Border { Width = 120, Child = _k.Bar(got / (double)total) });
        }
        info.Children.Add(meta);
        var buttons = _k.HStack(10, PlayButton(g, 46, 140));
        if (LatestState(g) is { } state)
        {
            buttons.Children.Add(_k.Secondary(S.LoadLatestState, "save", () => LoadState(g, state), 46, enabled: _running == null));
        }

        buttons.Margin = new Thickness(0, 10, 0, 0);
        info.Children.Add(buttons);
        row.Children.Add(Kit.At(info, col: 1));
        card.Children.Add(row);
        return card;
    }

    /// <summary>"Jogar" (or "Ir para o jogo" and "Parar" for the game that runs now).</summary>
    Control PlayButton(GameEntry g, double height, double minWidth)
    {
        if (IsRunningGame(g))
        {
            return _k.HStack(10, _k.Primary(S.GoToGame, "monitor", GoToGame, height, minWidth, tip: S.GoToGameTip),
                _k.Secondary(_stopAsked ? S.ForceStop : S.Stop, "stop", StopRunning, height, P.Danger));
        }

        return _k.Primary(S.Play, "play", () => Launch(g), height, minWidth, _running == null, _running != null ? S.AlreadyRunning(_runningTitle ?? "") : null);
    }

    /// <summary>A click on a game: its window when it runs, else its page.</summary>
    void OpenOrGoTo(GameEntry g)
    {
        if (IsRunningGame(g))
        {
            GoToGame();
        }
        else
        {
            Navigate(PageGame, g);
        }
    }

    Control IconText(string icon, string text, Color? iconColor = null, double size = 13)
    {
        var s = _k.HStack(6, _k.Icon(icon, size + 2, iconColor ?? P.Muted), _k.Text(text, size, color: P.TextSoft));
        s.VerticalAlignment = VerticalAlignment.Center;
        return s;
    }

    // ------------------------------------------------------------------ grid of covers

    Control GameGrid(List<GameEntry> games)
    {
        // as many columns as fit (cards at least 240 px wide); the gap is each card's right/bottom margin
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -18, 0) };
        foreach (var g in games)
        {
            grid.Children.Add(GameCard(g));
        }

        grid.SizeChanged += (_, e) =>
        {
            int cols = Math.Max(2, (int)((e.NewSize.Width) / (240 + 18)));
            if (cols != grid.Columns)
            {
                grid.Columns = cols;
            }
        };
        return grid;
    }

    Control GameCard(GameEntry g)
    {
        const double ArtH = 150;
        var art = new Grid { Height = ArtH };
        art.Children.Add(new Border { Background = Kit.Image(g.Pic0Path, 480), CornerRadius = new CornerRadius(18, 18, 0, 0) });
        art.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(18, 18, 0, 0),
            IsHitTestVisible = false,
            Background = Kit.Fade(P.Surface, Kit.Rel(0, 0.35), Kit.Rel(0, 1)),
        });
        var icon = _k.Picture(g.IconPath, 64, 64, 16, g.Initials, 128);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Bottom;
        icon.Margin = new Thickness(14, 0, 0, -6);
        icon.BorderBrush = Kit.B(P.Surface);
        icon.BorderThickness = new Thickness(2);
        art.Children.Add(icon);
        art.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10),
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(8),
            Background = Kit.B(Color.Parse("#0B0E13"), 0.62),
            Child = _k.Text(g.Region, 11, FontWeight.SemiBold, Color.Parse("#E8ECF2")),
        });
        bool running = _running != null && _runningGame?.TitleId == g.TitleId;
        if (running)
        {
            art.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10),
                Child = _k.Badge(S.RunningBadge, P.Ok, Color.Parse("#0B0E13")),
            });
        }

        // mouse over: the two ways to start it (the main one from "Ao escolher um jogo" first)
        var actions = HoverActions(g, 40);
        var hover = new Border
        {
            CornerRadius = new CornerRadius(18, 18, 0, 0),
            Background = Kit.B(Color.Parse("#0B0E13"), 0.55),
            IsVisible = false,
            Child = actions,
        };
        art.Children.Add(hover);

        var text = new StackPanel { Spacing = 4, Margin = new Thickness(14, 14, 14, 14) };
        text.Children.Add(_k.Text(g.Title, 14.5, FontWeight.SemiBold));
        text.Children.Add(_k.Text(g.Version.Length > 0 ? $"{g.TitleId} \u00B7 v{g.Version}" : g.TitleId, 12, color: P.Muted));
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        foot.Children.Add(PlayedOrCategory(g));
        var (got, total) = TrophyCount(g);
        if (total > 0)
        {
            foot.Children.Add(Kit.At(IconText("trophy", $"{got}/{total}", P.Gold, 12), col: 2));
        }

        text.Children.Add(foot);

        var stack = new StackPanel();
        stack.Children.Add(art);
        stack.Children.Add(text);
        var card = new Border
        {
            Margin = new Thickness(0, 0, 18, 18),
            CornerRadius = new CornerRadius(18),
            Background = Kit.B(P.Surface),
            ClipToBounds = true,
            BorderBrush = Kit.B(running ? P.Ok : P.Border),
            BorderThickness = new Thickness(1),
            Child = stack,
            Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = P.Dark ? default : BoxShadows.Parse("0 2 8 0 #14000000"),
        };
        HookCard(card, hover, g, running ? P.Ok : P.Border);
        return card;
    }

    /// <summary>Hover shows the start buttons; a click opens the game page; a double click (or Enter) does the main action.</summary>
    void HookCard(Border card, Control hover, GameEntry g, Color border)
    {
        card.PointerEntered += (_, _) => { hover.IsVisible = true; card.BorderBrush = Kit.B(P.Accent); };
        card.PointerExited += (_, _) => { hover.IsVisible = false; card.BorderBrush = Kit.B(border); };
        card.ContextFlyout = GameFoldersMenu(g); // right click: the game's folders
        card.Tapped += (_, e) =>
        {
            if (!FromButton(e))
            {
                OpenOrGoTo(g);
            }
        };
        card.DoubleTapped += (_, e) =>
        {
            if (!FromButton(e) && _running == null)
            {
                DefaultAction(g);
            }
        };
    }

    /// <summary>The event came from a button inside the card (its own action, not the card's).</summary>
    static bool FromButton(Avalonia.Interactivity.RoutedEventArgs e) =>
        e.Source is Avalonia.Visual v && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(v, includeSelf: true) != null;

    Control HoverActions(GameEntry g, double height)
    {
        bool liveAreaFirst = _settings.GameClick == LauncherSettings.ClickLiveArea;
        bool can = _running == null;
        if (IsRunningGame(g))
        {
            // the game that runs: its window, not a second start
            var go = _k.Primary(S.GoToGame, "monitor", GoToGame, height, 0, tip: S.GoToGameTip);
            go.HorizontalAlignment = HorizontalAlignment.Center;
            go.VerticalAlignment = VerticalAlignment.Center;
            go.Margin = new Thickness(0, -20, 0, 0);
            return go;
        }
        var play = liveAreaFirst ? _k.Secondary(S.Play, "play", () => Launch(g), height, enabled: can) : _k.Primary(S.Play, "play", () => Launch(g), height, 0, can);
        var live = liveAreaFirst ? _k.Primary(S.LiveArea, "vita", () => OpenLiveArea(g), height, 0) : _k.Secondary(S.LiveArea, "vita", () => OpenLiveArea(g), height);
        ToolTip.SetTip(live, S.LiveAreaTip);
        var acts = liveAreaFirst ? _k.HStack(8, live, play) : _k.HStack(8, play, live);
        acts.HorizontalAlignment = HorizontalAlignment.Center;
        acts.VerticalAlignment = VerticalAlignment.Center;
        acts.Margin = new Thickness(0, -20, 0, 0);
        return acts;
    }

    Control PlayedOrCategory(GameEntry g)
    {
        if (g.Category != "gd")
        {
            return _k.Badge(g.CategoryText, P.Warn);
        }

        return PlayOf(g.TitleId) is { } r ? _k.Badge(S.Duration(r.Seconds), P.Accent) : _k.Badge(S.NeverPlayed, P.Muted);
    }

    // ------------------------------------------------------------------ list

    Control GameList(List<GameEntry> games)
    {
        const string Cols = "64,*,100,110,150,100,250";
        var s = new StackPanel();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Margin = new Thickness(18, 0, 18, 8) };
        string[] cols = ["", S.ColGame, S.ColRegion, S.ColPlayed, S.ColTrophies, S.ColSize, ""];
        for (int i = 0; i < cols.Length; i++)
        {
            head.Children.Add(Kit.At(_k.Kicker(cols[i]), col: i));
        }

        s.Children.Add(head);
        int n = 0;
        foreach (var g in games)
        {
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Height = 62, Margin = new Thickness(18, 0) };
            r.Children.Add(_k.Picture(g.IconPath, 46, 46, 12, g.Initials, 128).Also(p => p.HorizontalAlignment = HorizontalAlignment.Left));
            r.Children.Add(Kit.At(_k.VStack(2, _k.Text(g.Title, 14, FontWeight.SemiBold), _k.Text(g.TitleId + (g.Version.Length > 0 ? " \u00B7 v" + g.Version : ""), 12, color: P.Muted)).Also(t => t.VerticalAlignment = VerticalAlignment.Center), col: 1));
            r.Children.Add(Kit.At(_k.Text(g.Region, 13, color: P.TextSoft), col: 2));
            r.Children.Add(Kit.At(_k.Text(PlayOf(g.TitleId) is { } pr ? S.Duration(pr.Seconds) : "\u2014", 13, color: P.TextSoft), col: 3));
            var (got, total) = TrophyCount(g);
            if (total > 0)
            {
                var tro = _k.HStack(8, _k.Text($"{got}/{total}", 13, color: P.TextSoft).Also(x => x.Width = 48), new Border { Width = 80, Child = _k.Bar(got / (double)total, 4) });
                tro.VerticalAlignment = VerticalAlignment.Center;
                r.Children.Add(Kit.At(tro, col: 4));
            }
            r.Children.Add(Kit.At(_k.Text(g.SizeText, 13, color: P.TextSoft), col: 5));
            var acts = HoverActions(g, 36);
            acts.Margin = default;
            acts.HorizontalAlignment = HorizontalAlignment.Right;
            acts.IsVisible = false;
            r.Children.Add(Kit.At(acts, col: 6));
            var zebra = n++ % 2 == 0 ? P.Surface : P.Background;
            var row = new Border
            {
                CornerRadius = new CornerRadius(14),
                Child = r,
                Background = Kit.B(zebra),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderBrush = Kit.B(Colors.Transparent),
                BorderThickness = new Thickness(1),
            };
            row.PointerEntered += (_, _) => { acts.IsVisible = true; row.Background = Kit.B(P.Highlight); row.BorderBrush = Kit.B(P.HighlightBorder); };
            row.PointerExited += (_, _) => { acts.IsVisible = false; row.Background = Kit.B(zebra); row.BorderBrush = Kit.B(Colors.Transparent); };
            row.Tapped += (_, e) =>
            {
                if (!FromButton(e))
                {
                    OpenOrGoTo(g);
                }
            };
            row.DoubleTapped += (_, e) =>
            {
                if (!FromButton(e) && _running == null)
                {
                    DefaultAction(g);
                }
            };
            s.Children.Add(row);
        }
        return s;
    }

    // ------------------------------------------------------------------ first run

    Control WelcomePage()
    {
        bool fw = FirmwareInstalled;
        var page = new StackPanel { Margin = new Thickness(36, 28, 36, 0) };
        page.Children.Add(_k.VStack(2, _k.Heading(S.Welcome, 30), _k.Text(S.WelcomeSubtitle, 13.5, color: P.Muted)).Also(h => h.Margin = new Thickness(0, 0, 0, 22)));
        var steps = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -16, 0) };
        steps.Children.Add(Step(1, fw ? "check" : "cpu", S.StepFirmware, fw ? S.StepFirmwareDone(FirmwareVersion) : S.StepFirmwareText,
            fw ? _k.Secondary(S.VitaSystem, "vita", () => Navigate(PageSystem), 42) : _k.Primary(S.InstallFirmware, "folder", () => Navigate(PageSystem), 42, 0), null, !fw));
        steps.Children.Add(Step(2, "download", S.StepGames, S.StepGamesText, InstallMenu(42), null, fw));
        steps.Children.Add(Step(3, "gamepad", S.StepControls, S.StepControlsText,
            _k.Secondary(S.ConfigureControls, null, () => { Navigate(PageSettings); _settingsPage?.ShowSection(SettingsPage.SectionControls); }, 42), null, false));
        page.Children.Add(steps);
        var empty = _k.VStack(8, _k.IconChip("library", 56).Also(c => c.HorizontalAlignment = HorizontalAlignment.Center),
            _k.Text(S.EmptyLibrary, 16, FontWeight.SemiBold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            _k.Text(S.EmptyLibraryText, 13, color: P.Muted).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        page.Children.Add(_k.Dashed(empty, 300).Also(d => d.Margin = new Thickness(0, 24, 0, 0)));
        return new ScrollViewer { Content = page };
    }

    Control Step(int n, string icon, string title, string text, Control action, string? link, bool current)
    {
        var s = new StackPanel { Spacing = 10 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        top.Children.Add(_k.IconChip(icon, 44));
        top.Children.Add(Kit.At(new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = Kit.B(current ? P.Accent : P.Track),
            Child = _k.Text(n.ToString(S.Culture), 12.5, FontWeight.Bold, current ? P.OnAccent : P.TextSoft).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
        }, col: 2));
        s.Children.Add(top);
        s.Children.Add(_k.Text(title, 16, FontWeight.SemiBold).Also(t => t.Margin = new Thickness(0, 6, 0, 0)));
        s.Children.Add(_k.Wrap(text, 13).Also(t => t.MinHeight = 54));
        action.HorizontalAlignment = HorizontalAlignment.Left;
        s.Children.Add(action);
        if (link != null)
        {
            s.Children.Add(_k.Text(link, 13, FontWeight.SemiBold, P.Accent));
        }

        var card = _k.Card(s, 20, new Thickness(20, 18));
        card.Margin = new Thickness(0, 0, 16, 0);
        if (current)
        {
            card.BorderBrush = Kit.B(P.HighlightBorder);
        }

        return card;
    }
}
