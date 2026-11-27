using VitaSharp.Core.Localization;
using MauiReactor;
using MauiReactor.Shapes;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Mobile.Core.Library;

namespace VitaSharp.Android.Pages;

public sealed class TrophiesState
{
    public TrophyList? List { get; set; }
    public TrophyList.Filter Filter { get; set; }
    /// <summary>The rows of the current filter (sorted once, not at every render).</summary>
    public IReadOnlyList<TrophyRow> Rows { get; set; } = [];
}

/// <summary>
/// Trofeus (trofeus.html): the game's trophy set from its TROPHY.TRP and the unlocks in ux0:user/00/trophy: progress,
/// counts per grade, filters and the list (icon, grade, description, unlock date); locked and hidden trophies dimmed.
/// </summary>
public sealed class TrophiesPage : Screen<TrophiesState, GameProps>
{
    protected override void OnMounted()
    {
        _ = Task.Run(() =>
        {
            var game = AppServices.FindGame(Props.TitleId);
            TrophyListCache cache = AppServices.TrophyLists;
            int trpReads = cache.TrpReads;
            int extractions = cache.IconExtractions;
            int diskHits = cache.DiskHits;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // pt-BR texts when the set has them (TROP_17.SFM); texts and pictures from the disk cache when unchanged
            var list = game != null ? cache.Load(game, AppServices.Data, language: 17) : null;
            watch.Stop();
            // in the app log (and so in the F8 capture): how long the list took and what it had to read
            VitaSharp.Core.Util.Log.Info($"Trophies: {Props.TitleId}: {list?.Rows.Count ?? 0} rows in {watch.ElapsedMilliseconds} ms " +
                $"(TRP reads {cache.TrpReads - trpReads}, picture extractions {cache.IconExtractions - extractions}, sets from disk {cache.DiskHits - diskHits})");
            var rows = list?.Filtered(TrophyList.Filter.All) ?? [];
            SetState(s =>
            {
                s.List = list;
                s.Rows = rows;
            });
        });
        base.OnMounted();
    }

    void SetFilter(TrophyList.Filter filter)
    {
        if (State.List is not { } list)
            return;
        var rows = list.Filtered(filter);
        SetState(s =>
        {
            s.Filter = filter;
            s.Rows = rows;
        });
    }

    public override VisualNode Render()
    {
        if (State.List is not { } list)
            return ContentPage(ActivityIndicator().IsRunning(true).Center()).HasNavigationBar(false).BackgroundColor(Palette.Background);
        var p = list.Progress;
        // a CollectionView: only the rows on screen are built and laid out, and they are recycled while scrolling (a
        // long list in a stack built every row and loaded every picture before the screen showed)
        return ContentPage(
            Grid("Auto,*", "*",
                Header(list.Title, back: true, kicker: Loc.Get("MTrophies")),
                CollectionView()
                    .ItemsSource(State.Rows, TrophyRowView)
                    .Header(VStack(
                        Border(
                            VStack(
                                Grid("Auto", "*,Auto",
                                    Label(Loc.Format("MTrophiesOf", p.Earned, p.Total)).FontFamily(Fonts.SoraSemiBold).FontSize(17),
                                    Label($"{p.Percent}%").FontFamily(Fonts.SoraBold).FontSize(17).TextColor(Palette.Accent).GridColumn(1)
                                ),
                                Bar(p.Fraction).Margin(0, 12, 0, 12),
                                Grid("Auto", "*,*,*,*",
                                    GradeCount(TrophyGrade.Platinum, p, 0), GradeCount(TrophyGrade.Gold, p, 1),
                                    GradeCount(TrophyGrade.Silver, p, 2), GradeCount(TrophyGrade.Bronze, p, 3))
                            ).Padding(16))
                        .StrokeShape(new RoundRectangle().CornerRadius(20)).StrokeThickness(0).BackgroundColor(Palette.Surface).Margin(20, 8, 20, 0),
                        ChipRow(
                            Chip(Loc.Get("MAll"), State.Filter == TrophyList.Filter.All, () => SetFilter(TrophyList.Filter.All)),
                            Chip(Loc.Get("MEarned"), State.Filter == TrophyList.Filter.Earned, () => SetFilter(TrophyList.Filter.Earned)),
                            Chip(Loc.Get("MMissingTrophies"), State.Filter == TrophyList.Filter.Missing, () => SetFilter(TrophyList.Filter.Missing))
                        ).Padding(20, 16, 20, 10)))
                    .Footer(BoxView().HeightRequest(24).Color(Colors.Transparent))
                    .ItemsLayout(new VerticalLinearItemsLayout().ItemSpacing(10))
                    .GridRow(1)
            )).HasNavigationBar(false).BackgroundColor(Palette.Background);
    }

    VisualNode GradeCount(TrophyGrade g, TrophyProgress p, int column)
    {
        var (earned, total) = p.ByGrade.GetValueOrDefault(g);
        return VStack(
                Label(TrophyList.GradeName(g)).FontSize(12).TextColor(Color.FromArgb(TrophyList.GradeColor(g))),
                Label($"{earned}/{total}").FontFamily(Fonts.InterSemiBold).FontSize(15)
            ).Spacing(2).GridColumn(column);
    }

    VisualNode TrophyRowView(TrophyRow r)
    {
        double opacity = r.Unlocked ? 1 : 0.55;
        // the picture extracted by the cache loads as a file (decoded on a worker thread at the view's size, kept in the
        // image memory cache); a row without one reads it from the TRP when the image loads
        var icon = r.HasIcon && (r.Unlocked || !r.Hidden)
            ? Image().Source(r.IconFile != null
                    ? ImageSource.FromFile(r.IconFile)
                    : ImageSource.FromStream(() => r.LoadIcon() is { } bytes ? new MemoryStream(bytes) : Stream.Null))
                .Aspect(Aspect.AspectFill)
            : (VisualNode)Label(r.Hidden ? "?" : "\U0001F3C6").FontSize(20).TextColor(Palette.Muted).Center();
        return Border(
                Grid("Auto", "Auto,*",
                    Border(icon).StrokeShape(new RoundRectangle().CornerRadius(12)).StrokeThickness(0)
                        .BackgroundColor(Palette.SurfaceHigh).WidthRequest(52).HeightRequest(52),
                    VStack(
                        Grid("Auto", "*,Auto",
                            Label(r.ShownName).FontFamily(Fonts.InterSemiBold).FontSize(14).LineBreakMode(LineBreakMode.WordWrap),
                            Label(TrophyList.GradeName(r.Grade)).FontSize(11).TextColor(Color.FromArgb(TrophyList.GradeColor(r.Grade))).GridColumn(1).Margin(8, 0, 0, 0)
                        ),
                        Label(r.ShownDetail).FontSize(12).TextColor(Palette.Muted).MaxLines(3),
                        r.UnlockedUtc is { } d ? Label(Loc.Format("MEarnedOn", d.ToLocalTime())).FontSize(11).TextColor(Palette.AccentSoft) : null
                    ).Spacing(3).GridColumn(1).Margin(12, 0, 0, 0).VCenter()
                ).Padding(12))
            .StrokeShape(new RoundRectangle().CornerRadius(16)).StrokeThickness(0).BackgroundColor(Palette.Surface)
            .Opacity(opacity).Margin(20, 0);
    }
}
