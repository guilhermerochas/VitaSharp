using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Localization;
using VitaSharp.Core.SaveState;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Pagina do jogo: pic0 behind the top, start buttons, play time, save states with their pictures, trophies,
/// captures and the game's own settings (internal resolution, frame rate).</summary>
public sealed partial class MainWindow
{
    Button? _gameFolders;

    Control GamePage(GameEntry g)
    {
        var root = new Grid();
        root.Children.Add(new Border { Height = 380, VerticalAlignment = VerticalAlignment.Top, Background = Kit.Image(g.Pic0Path, 960, ay: AlignmentY.Top) });
        root.Children.Add(new Border
        {
            Height = 381,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush
            {
                StartPoint = Kit.Rel(0, 0),
                EndPoint = Kit.Rel(0, 1),
                GradientStops =
                {
                    Kit.Stop(static p => p.Scrim, 70, 0),
                    Kit.Stop(static p => p.Scrim, 150, 0.45),
                    Kit.Stop(static p => p.Background, 255, 1),
                },
            },
        });

        var page = new StackPanel { Margin = new Thickness(36, 24, 36, 32) };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        top.Children.Add(GlassButton("chevron-left", S.Library, () => Navigate(PageLibrary)));
        var folders = GlassButton("folder", S.GameFolders, () => { }, S.GameFoldersTip);
        folders.Flyout = GameFoldersMenu(g);
        _gameFolders = folders;
        top.Children.Add(Kit.At(folders, col: 2));
        var more = GlassButton("more", null, () => { }, S.More);
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        void Item(string text, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            flyout.Items.Add(mi);
        }
        Item(S.Uninstall, () => _ = Uninstall(g), _runningGame?.TitleId != g.TitleId);
        more.Flyout = flyout;
        more.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(Kit.At(more, col: 3));
        page.Children.Add(top);

        // identity
        var id = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 110, 0, 0) };
        var icon = _k.Picture(g.IconPath, 148, 148, 34, g.Initials);
        icon.BoxShadow = BoxShadows.Parse("0 12 36 0 #66000000");
        icon.VerticalAlignment = VerticalAlignment.Bottom;
        // the running game's icon goes to its window, like "Ir para o jogo"
        id.Children.Add(IsRunningGame(g) ? Kit.Click(icon, GoToGame, S.GoToGameTip).Also(b => b.VerticalAlignment = VerticalAlignment.Bottom) : icon);
        var info = new StackPanel { Spacing = 8, Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        var badges = _k.HStack(8, _k.Badge(g.Category == "gd" ? S.CategoryGame : g.CategoryText, static p => p.Accent), _k.Badge(g.Region, static p => p.TextSoft));
        if (_runningGame?.TitleId == g.TitleId)
        {
            badges.Children.Add(_k.Badge(S.RunningBadge, static p => p.Ok));
        }

        info.Children.Add(badges);
        info.Children.Add(_k.Heading(g.Title, 34).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.MaxHeight = 90; }));
        string installed = Directory.Exists(g.Path) ? S.InstalledOn(Directory.GetCreationTime(g.Path)) : "";
        info.Children.Add(_k.Text(string.Join("  \u00B7  ", new[] { g.TitleId, g.Version.Length > 0 ? "v" + g.Version : "", g.SizeText, installed }.Where(x => x.Length > 0)), 13.5, color: static p => p.Muted));
        var buttons = _k.HStack(10, PlayButton(g, 50, 170), _k.Secondary(S.OpenLiveArea, "vita", () => OpenLiveArea(g), 50, tip: S.LiveAreaTip));
        if (LatestState(g) is { } latest)
        {
            buttons.Children.Add(_k.Secondary(S.LoadLatestState, "save", () => LoadState(g, latest), 50, enabled: _running == null));
        }

        buttons.Margin = new Thickness(0, 10, 0, 0);
        info.Children.Add(buttons);
        id.Children.Add(Kit.At(info, col: 1));
        page.Children.Add(id);

        // numbers
        var play = PlayOf(g.TitleId);
        var (got, total) = TrophyCount(g);
        var states = StatesOf(g);
        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 26, -14, 0) };
        stats.Children.Add(Stat("clock", S.TimePlayed, play != null ? S.Duration(play.Seconds) : S.NeverPlayed));
        stats.Children.Add(Stat("zap", S.LastPlayed, play != null ? S.When(play.LastPlayedUtc.ToLocalTime()) : "\u2014"));
        stats.Children.Add(Stat("trophy", S.Trophies, total > 0 ? $"{got} / {total}" : S.NoTrophies, static p => p.Gold));
        stats.Children.Add(Stat("save", S.SaveStatesTitle, states.Count.ToString(S.Culture)));
        page.Children.Add(stats);

        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,460"), Margin = new Thickness(0, 28, 0, 0) };
        var left = new StackPanel { Spacing = 26 };
        left.Children.Add(StatesSection(g, states));
        if (total > 0)
        {
            left.Children.Add(TrophySummary(g));
        }

        left.Children.Add(GameCapturesSection(g));
        cols.Children.Add(left);
        cols.Children.Add(Kit.At(GameSettingsSection(g), col: 2));
        page.Children.Add(cols);

        root.Children.Add(page);
        return new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>
    /// "Pastas": the game's folders on this PC, opened in the file manager (the game page's header button and the
    /// library cover's right-click menu): app (ux0:app/ID), save data, patch and add-on content when present, save
    /// states, and the newest capture and log of the game (selected in their folders).
    /// </summary>
    MenuFlyout GameFoldersMenu(GameEntry g)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        void Item(string text, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            flyout.Items.Add(mi);
        }
        string id = g.TitleId;
        string savedata = VitaSharp.Core.Io.FirmwareLayout.SaveData.Host(Program.FsRoot, id);
        string patch = VitaSharp.Core.Io.FirmwareLayout.Patches.Host(Program.FsRoot, id);
        string addcont = VitaSharp.Core.Io.FirmwareLayout.AddCont.Host(Program.FsRoot, id);
        var states = SaveStates.Folder(Program.DataRoot, id);
        Item(S.FolderApp(VitaSharp.Core.Io.FirmwareLayout.HostToGuest(Program.FsRoot, g.Path) ?? g.Path), () => OpenPath(g.Path), Directory.Exists(g.Path));
        Item(S.FolderSaveData(id), () => OpenPath(savedata), Directory.Exists(savedata));
        if (Directory.Exists(patch))
        {
            Item(S.FolderPatch(id), () => OpenPath(patch));
        }

        if (Directory.Exists(addcont))
        {
            Item(S.FolderAddcont(id), () => OpenPath(addcont));
        }

        Item(S.FolderStates(id), () => OpenPath(states), Directory.Exists(states));
        flyout.Items.Add(new Separator());
        var capture = CaptureFiles().FirstOrDefault(c => c.TitleId == id);
        Item(S.FolderCaptures, () => Reveal(capture!.Path), capture != null);
        var log = NewestLog(id);
        Item(S.FolderLogs, () => Reveal(log!), log != null);
        return flyout;
    }

    /// <summary>The newest session log of a title (logs/&lt;date&gt;_&lt;time&gt;_&lt;title id&gt;.log), or null.</summary>
    static string? NewestLog(string titleId)
    {
        var dir = Path.Combine(Program.DataRoot, "logs");
        try
        {
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*_" + titleId + ".log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    Button GlassButton(string icon, string? text, Action click, string? tip = null)
    {
        var row = _k.HStack(6, _k.Icon(icon, 18, static _ => OverCoverText), text != null ? _k.Text(text, 13.5, FontWeight.Medium, static _ => OverCoverText) : null);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.VerticalAlignment = VerticalAlignment.Center;
        return Kit.Click(new Border
        {
            Height = 40,
            MinWidth = 40,
            Padding = new Thickness(text != null ? 12 : 0, 0, text != null ? 16 : 0, 0),
            CornerRadius = new CornerRadius(13),
            Background = Kit.Fixed(OverCoverShade, 0.55),
            BorderBrush = Kit.Fixed(Colors.White, 0.12),
            BorderThickness = new Thickness(1),
            Child = row,
        }, click, tip);
    }

    Control Stat(string icon, string label, string value, Func<Palette, Color>? iconColor = null)
    {
        var s = _k.VStack(6, _k.HStack(7, _k.Icon(icon, 15, iconColor ?? (static p => p.Muted)), _k.Text(label, 12.5, color: static p => p.Muted)), _k.Heading(value, 20));
        var card = _k.Card(s, 18, new Thickness(18, 14));
        card.Margin = new Thickness(0, 0, 14, 0);
        return card;
    }

    // ------------------------------------------------------------------ save states

    sealed record StateFile(string Path, string Name, DateTime When, SaveStateHeader? Header, string? Problem);

    List<StateFile> StatesOf(GameEntry g)
    {
        var dir = SaveStates.Folder(Program.DataRoot, g.TitleId);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var list = new List<StateFile>();
        foreach (var f in Directory.GetFiles(dir, "*.vss").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            SaveStateHeader? h = null;
            try
            {
                h = SaveStateFile.ReadHeader(f);
            }
            catch (Exception e) when (e is SnapshotException or IOException) { }
            string name = System.IO.Path.GetFileNameWithoutExtension(f) switch
            {
                "quick" => S.QuickState,
                var n when n.StartsWith("slot", StringComparison.Ordinal) => S.Slot(n[4..]),
                var n => n,
            };
            list.Add(new StateFile(f, name, File.GetLastWriteTime(f), h, h == null ? S.StateUnreadable : SaveStateFile.Incompatibility(h, g.TitleId)));
        }
        return list;
    }

    string? LatestState(GameEntry g) => StatesOf(g).FirstOrDefault(s => s.Problem == null)?.Path;

    Control StatesSection(GameEntry g, List<StateFile> states)
    {
        var s = new StackPanel();
        s.Children.Add(SectionHeader(S.SaveStatesTitle, states.Count > 0 ? S.OpenFolder : null, () => OpenPath(SaveStates.Folder(Program.DataRoot, g.TitleId))));
        if (states.Count == 0)
        {
            s.Children.Add(_k.Card(_k.Wrap(S.NoStates, 13), 16, new Thickness(18, 14)));
            return s;
        }
        var row = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -14, 0) };
        foreach (var st in states.Take(8))
        {
            var thumb = new Grid { Height = 92 };
            Bitmap? bmp = null;
            if (st.Header?.Thumbnail is { Length: > 0 } png)
            {
                try
                {
                    bmp = new Bitmap(new MemoryStream(png));
                }
                catch (Exception) { }
            }

            thumb.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = bmp != null ? Kit.Image(bmp) : Kit.Image(g.Pic0Path, 480),
                Opacity = bmp != null ? 1 : 0.35,
            });
            thumb.Children.Add(new Border { CornerRadius = new CornerRadius(14), BorderBrush = Kit.B(static p => p.Border), BorderThickness = new Thickness(1) });
            if (st.Problem != null)
            {
                thumb.Children.Add(new Border { Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Top, Child = _k.Badge(S.Incompatible, static p => p.Warn, static _ => OverCoverShade) });
            }

            var c = _k.VStack(6, thumb, _k.Text(st.Name, 13.5, FontWeight.SemiBold), _k.Text(S.When(st.When), 12, color: static p => p.Muted));
            // the play time, as in the in-game picker (F6); a state of another version has the badge instead
            if (st.Header is { } h && st.Problem == null)
            {
                c.Children.Add(_k.Text(Loc.Format("SsPlayTime", Host.SaveStateOverlay.PlayTime(h.GuestSeconds)), 11.5, color: static p => p.Faint));
            }

            c.Margin = new Thickness(0, 0, 14, 0);
            bool can = st.Problem == null && _running == null;
            row.Children.Add(Kit.Click(c, () => LoadState(g, st.Path), st.Problem ?? S.LoadStateTip, can));
        }
        s.Children.Add(row);
        return s;
    }

    // ------------------------------------------------------------------ trophies

    Control TrophySummary(GameEntry g)
    {
        var sets = TrophiesOf(g);
        var trophies = sets.SelectMany(t => t.Package.Trophies.Select(x => (Set: t, Trophy: x))).ToList();
        int got = trophies.Count(t => t.Set.Unlocked.ContainsKey(t.Trophy.Id)), total = trophies.Count;
        var s = new StackPanel();
        s.Children.Add(SectionHeader(S.Trophies, S.SeeAllTrophies, () => { _trophyGame = g.TitleId; Navigate(PageTrophies); }));
        var body = new StackPanel { Spacing = 14 };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var dots = _k.HStack(16);
        foreach (var grade in new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze })
        {
            int all = trophies.Count(t => t.Trophy.Grade == grade);
            if (all == 0)
            {
                continue;
            }

            int have = trophies.Count(t => t.Trophy.Grade == grade && t.Set.Unlocked.ContainsKey(t.Trophy.Id));
            dots.Children.Add(_k.HStack(6, _k.Dot(GradeColor(grade), 9), _k.Text($"{have}/{all}", 13, FontWeight.Medium, static p => p.TextSoft)).Also(x => ToolTip.SetTip(x, S.Grade(grade))));
        }
        top.Children.Add(dots);
        top.Children.Add(Kit.At(_k.Text($"{got * 100 / Math.Max(1, total)}%", 14, FontWeight.SemiBold, static p => p.Accent), col: 1));
        body.Children.Add(top);
        body.Children.Add(_k.Bar(got / (double)Math.Max(1, total), 6));
        var recent = trophies.Where(t => t.Set.Unlocked.ContainsKey(t.Trophy.Id)).OrderByDescending(t => t.Set.Unlocked[t.Trophy.Id]).Take(3).ToList();
        if (recent.Count > 0)
        {
            var row = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var (set, t) in recent)
            {
                var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 12, 0) };
                r.Children.Add(TrophyIcon(set, t, 44, true));
                r.Children.Add(Kit.At(_k.VStack(2, _k.Text(t.Name, 13, FontWeight.SemiBold), _k.Text($"{S.Grade(t.Grade)} \u00B7 {S.When(TrophyTime(set, t.Id)!.Value)}", 11.5, color: static p => p.Muted))
                    .Also(x => { x.Margin = new Thickness(10, 0, 0, 0); x.VerticalAlignment = VerticalAlignment.Center; }), col: 1));
                row.Children.Add(r);
            }
            body.Children.Add(row);
        }
        else
        {
            body.Children.Add(_k.Text(S.NoTrophyYet, 12.5, color: static p => p.Muted));
        }

        s.Children.Add(_k.Card(body, 18, new Thickness(20, 18)));
        return s;
    }

    Control TrophyIcon(TrophySetView set, TrophyInfo t, double size, bool unlocked)
    {
        var pic = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.22), Background = Kit.B(static p => p.SurfaceHigh), Opacity = unlocked ? 1 : 0.45 };
        bool hidden = t.Hidden && !unlocked;
        if (!hidden)
        {
            try
            {
                if (set.Package.TrophyIcon(t.Id) is { } png)
                {
                    pic.Background = new ImageBrush(new Bitmap(new MemoryStream(png))) { Stretch = Stretch.UniformToFill };
                }
            }
            catch (Exception) { }
        }
        else
        {
            pic.Child = _k.Icon("x", size * 0.38, static p => p.Faint);
        }

        return pic;
    }

    /// <summary>Unlock time: microseconds since 0001-01-01 UTC (SceRtcTick).</summary>
    static DateTime? TrophyTime(TrophySetView set, int id) =>
        set.Unlocked.TryGetValue(id, out var us) && us > 0 && us < (ulong)DateTime.MaxValue.Ticks / 10 ? new DateTime((long)us * 10, DateTimeKind.Utc).ToLocalTime() : null;

    static Func<Palette, Color> GradeColor(TrophyGrade g) => g switch
    {
        TrophyGrade.Platinum => static p => p.Platinum,
        TrophyGrade.Gold => static p => p.Gold,
        TrophyGrade.Silver => static p => p.Silver,
        _ => static p => p.Bronze,
    };

    // ------------------------------------------------------------------ captures of this game

    Control GameCapturesSection(GameEntry g)
    {
        var mine = CaptureFiles().Where(c => c.TitleId == g.TitleId).ToList();
        var s = new StackPanel();
        s.Children.Add(SectionHeader(S.Captures, mine.Count > 0 ? S.SeeAll : null, () => { _captureGame = g.TitleId; Navigate(PageCaptures); }));
        if (mine.Count == 0)
        {
            s.Children.Add(_k.Card(_k.Wrap(S.NoGameCaptures, 13), 16, new Thickness(18, 14)));
            return s;
        }
        var rows = new StackPanel();
        foreach (var c in mine.Take(3))
        {
            if (rows.Children.Count > 0)
            {
                rows.Children.Add(_k.Divider(14));
            }

            rows.Children.Add(CaptureRow(c, compact: true));
        }
        s.Children.Add(_k.Card(rows, 18, new Thickness(0, 4)));
        return s;
    }

    // ------------------------------------------------------------------ the game's own settings

    /// <summary>"Ajustes deste jogo" (<see cref="LauncherSettings.Games"/>): used when the game starts.</summary>
    Control GameSettingsSection(GameEntry g)
    {
        var opts = _settings.Games.GetValueOrDefault(g.TitleId) ?? new GameOptions();
        void Store()
        {
            if (opts.IsDefault)
            {
                _settings.Games.Remove(g.TitleId);
            }
            else
            {
                _settings.Games[g.TitleId] = opts;
            }

            SaveSettings();
        }
        var s = new StackPanel();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(_k.Text(S.GameSettings, 16, FontWeight.SemiBold));
        if (!opts.IsDefault)
        {
            head.Children.Add(Kit.At(Kit.Click(_k.Text(S.UseGlobal, 13, FontWeight.SemiBold, static p => p.Accent), () =>
            {
                _settings.Games.Remove(g.TitleId);
                SaveSettings();
                ShowPage();
            }, S.UseGlobalTip), col: 1));
        }

        s.Children.Add(head);
        var res = GameOptions.ResScales;
        var rates = GameOptions.FrameRates;
        var fast = GameOptions.FastLoadings(_settings.FastLoading);
        s.Children.Add(_k.Group(
            _k.Row("monitor", S.InternalResolution, S.InternalResolutionDetail,
                _k.Dropdown(res.Select(c => c.Label).ToArray(), Math.Max(0, Array.FindIndex(res, c => c.Factor == opts.ResScale)), i => { opts.ResScale = res[i].Factor; Store(); }, 176),
                tip: S.InternalResolutionTip),
            _k.Row("gauge", S.FrameRate, S.FrameRateDetail,
                _k.Dropdown(rates.Select(c => c.Label).ToArray(), Math.Max(0, Array.FindIndex(rates, c => c.Value == opts.FrameRate)), i => { opts.FrameRate = rates[i].Value; Store(); }, 176),
                tip: S.FrameRateTip),
            _k.Row("zap", S.FastLoading, S.FastLoadingDetail,
                _k.Dropdown(fast.Select(c => c.Label).ToArray(), Math.Max(0, Array.FindIndex(fast, c => c.Value == opts.FastLoading)), i => { opts.FastLoading = fast[i].Value; Store(); }, 176),
                tip: S.FastLoadingTip),
            _k.Row("gamepad", S.Controls, S.ControlsDetail, open: () => { Navigate(PageSettings); _settingsPage?.ShowSection(SettingsPage.SectionControls); })));
        s.Children.Add(_k.Text(_runningGame?.TitleId == g.TitleId ? S.AppliesNextStart : S.AppliesOnStart, 12, color: static p => p.Muted).Also(t => t.Margin = new Thickness(6, 10, 0, 0)));
        return s;
    }
}
