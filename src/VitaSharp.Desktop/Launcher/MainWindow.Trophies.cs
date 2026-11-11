using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Trofeus: every game's trophy progress, and one game's list with grades and unlock dates (hidden trophies keep
/// their text secret until unlocked, as on the console).</summary>
public sealed partial class MainWindow
{
    string? _trophyGame;
    string _trophyFilter = "all";
    string _trophyQuery = "";

    Control TrophiesPage()
    {
        var games = _all.Select(g => (Game: g, Sets: TrophiesOf(g))).Where(x => x.Sets.Count > 0)
            .Select(x => (x.Game, x.Sets, Got: x.Sets.Sum(s => s.UnlockedCount), Total: x.Sets.Sum(s => s.Package.Trophies.Count)))
            .OrderByDescending(x => x.Got).ThenBy(x => x.Game.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        int got = games.Sum(x => x.Got), total = games.Sum(x => x.Total);

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(36, 28, 36, 0) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        header.Children.Add(_k.VStack(2, _k.Heading(S.Trophies, 30), _k.Text(S.TrophiesSubtitle(got, total, games.Count), 13.5, color: P.Muted)));
        var counts = _k.HStack(18);
        foreach (var grade in new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze })
        {
            int n = games.Sum(x => x.Sets.Sum(s => s.Package.Trophies.Count(t => t.Grade == grade && s.Unlocked.ContainsKey(t.Id))));
            counts.Children.Add(_k.HStack(7, _k.Dot(GradeColor(grade), 10), _k.Text(n.ToString(S.Culture), 15, FontWeight.SemiBold)).Also(c => ToolTip.SetTip(c, S.Grade(grade))));
        }
        counts.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(Kit.At(counts, col: 1));
        page.Children.Add(header);

        if (games.Count == 0)
        {
            page.Children.Add(Kit.At(_k.Card(_k.Wrap(S.NoTrophyGames, 13.5), 18, new Thickness(20)).Also(c => c.VerticalAlignment = VerticalAlignment.Top), row: 1));
            return page;
        }
        var selected = games.FirstOrDefault(x => x.Game.TitleId == _trophyGame);
        if (selected.Game == null)
        {
            selected = games[0];
        }

        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("390,24,*") };
        var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 24) };
        var search = _k.Field(_trophyQuery, q => { _trophyQuery = q; RebuildTrophyList(list, games, selected.Game); }, 390, S.SearchGame);
        search.Margin = new Thickness(0, 0, 0, 6);
        var left = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search);
        left.Children.Add(new ScrollViewer { Content = list });
        RebuildTrophyList(list, games, selected.Game);
        cols.Children.Add(left);
        cols.Children.Add(Kit.At(new ScrollViewer { Content = TrophyDetail(selected.Game, selected.Sets).Also(d => d.Margin = new Thickness(0, 0, 0, 24)) }, col: 2));
        page.Children.Add(Kit.At(cols, row: 1));
        return page;
    }

    void RebuildTrophyList(StackPanel list, List<(GameEntry Game, List<TrophySetView> Sets, int Got, int Total)> games, GameEntry selected)
    {
        list.Children.Clear();
        foreach (var (g, _, got, total) in games.Where(x => _trophyQuery.Length == 0 || x.Game.Title.Contains(_trophyQuery, StringComparison.CurrentCultureIgnoreCase)
                                                         || x.Game.TitleId.Contains(_trophyQuery, StringComparison.OrdinalIgnoreCase)))
        {
            bool sel = g.TitleId == selected.TitleId;
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            r.Children.Add(_k.Picture(g.IconPath, 44, 44, 11, g.Initials, 128));
            r.Children.Add(Kit.At(_k.VStack(5, _k.Text(g.Title, 13.5, FontWeight.SemiBold), _k.Bar(got / (double)Math.Max(1, total), 4))
                .Also(t => { t.Margin = new Thickness(12, 0); t.VerticalAlignment = VerticalAlignment.Center; }), col: 1));
            r.Children.Add(Kit.At(_k.Text($"{got}/{total}", 12.5, FontWeight.Medium, P.Muted), col: 2));
            list.Children.Add(Kit.Click(new Border
            {
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(14),
                Child = r,
                Background = Kit.B(sel ? P.Highlight : P.Surface),
                BorderBrush = Kit.B(sel ? P.HighlightBorder : P.Border),
                BorderThickness = new Thickness(1),
            }, () => { _trophyGame = g.TitleId; ShowPage(); }));
        }
    }

    Control TrophyDetail(GameEntry g, List<TrophySetView> sets)
    {
        var trophies = sets.SelectMany(s => s.Package.Trophies.Select(t => (Set: s, Trophy: t))).ToList();
        int got = trophies.Count(t => t.Set.Unlocked.ContainsKey(t.Trophy.Id)), total = trophies.Count;
        var body = new StackPanel();
        var banner = new Grid { Height = 120 };
        banner.Children.Add(new Border { CornerRadius = new CornerRadius(18, 18, 0, 0), Background = Kit.Image(g.Pic0Path, 960) });
        banner.Children.Add(new Border { CornerRadius = new CornerRadius(18, 18, 0, 0), Background = Kit.Fade(P.Surface, Kit.Rel(0, 0), Kit.Rel(0, 1), 0.25, 1) });
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20, 0, 20, 4) };
        head.Children.Add(Kit.Click(_k.Picture(g.IconPath, 64, 64, 16, g.Initials, 128), () => Navigate(PageGame, g), S.OpenGamePage));
        var dots = _k.HStack(14);
        foreach (var grade in new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze })
        {
            int all = trophies.Count(t => t.Trophy.Grade == grade);
            if (all == 0)
            {
                continue;
            }

            int have = trophies.Count(t => t.Trophy.Grade == grade && t.Set.Unlocked.ContainsKey(t.Trophy.Id));
            dots.Children.Add(_k.HStack(6, _k.Dot(GradeColor(grade), 9), _k.Text($"{have}/{all}", 12.5, FontWeight.Medium, P.TextSoft)).Also(x => ToolTip.SetTip(x, S.Grade(grade))));
        }
        head.Children.Add(Kit.At(_k.VStack(4, _k.Heading(g.Title, 20), dots).Also(x => { x.Margin = new Thickness(14, 0, 0, 0); x.VerticalAlignment = VerticalAlignment.Center; }), col: 1));
        head.Children.Add(Kit.At(_k.Heading($"{got * 100 / Math.Max(1, total)}%", 24).Also(x => x.Foreground = Kit.B(P.Accent)), col: 2));
        banner.Children.Add(head);
        body.Children.Add(banner);

        var filters = _k.HStack(8,
            _k.Chip(S.FilterAllShort, _trophyFilter == "all", () => { _trophyFilter = "all"; ShowPage(); }),
            _k.Chip(S.TrophiesGot(got), _trophyFilter == "got", () => { _trophyFilter = "got"; ShowPage(); }),
            _k.Chip(S.TrophiesMissing(total - got), _trophyFilter == "missing", () => { _trophyFilter = "missing"; ShowPage(); }));
        filters.Margin = new Thickness(20, 14, 20, 8);
        body.Children.Add(filters);

        var shown = trophies.Where(t => _trophyFilter switch
            {
                "got" => t.Set.Unlocked.ContainsKey(t.Trophy.Id),
                "missing" => !t.Set.Unlocked.ContainsKey(t.Trophy.Id),
                _ => true,
            })
            .OrderByDescending(t => TrophyTime(t.Set, t.Trophy.Id) ?? DateTime.MinValue).ThenBy(t => t.Trophy.Id).ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            var (set, t) = shown[i];
            var when = TrophyTime(set, t.Id);
            bool unlocked = set.Unlocked.ContainsKey(t.Id);
            bool hidden = t.Hidden && !unlocked;
            var r = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(20, 9) };
            r.Children.Add(TrophyIcon(set, t, 48, unlocked));
            r.Children.Add(Kit.At(_k.VStack(3, _k.Text(hidden ? S.HiddenTrophy : t.Name, 14, FontWeight.SemiBold, unlocked ? P.Text : P.TextSoft),
                _k.Wrap(hidden ? S.HiddenTrophyText : t.Detail, 12.5)).Also(x => { x.Margin = new Thickness(14, 0, 14, 0); x.VerticalAlignment = VerticalAlignment.Center; }), col: 1));
            var meta = _k.VStack(4, _k.HStack(6, _k.Dot(GradeColor(t.Grade), 8), _k.Text(S.Grade(t.Grade), 12.5, FontWeight.Medium, P.TextSoft)).Also(x => x.HorizontalAlignment = HorizontalAlignment.Right),
                _k.Text(when is { } w ? S.When(w) : unlocked ? S.Unlocked : S.Locked, 12, color: unlocked ? P.Muted : P.Faint).Also(x => x.HorizontalAlignment = HorizontalAlignment.Right));
            meta.VerticalAlignment = VerticalAlignment.Center;
            r.Children.Add(Kit.At(meta, col: 2));
            if (i > 0)
            {
                body.Children.Add(_k.Divider(20));
            }

            body.Children.Add(r);
        }
        return _k.Card(body, 18, new Thickness(0, 0, 0, 8));
    }
}
