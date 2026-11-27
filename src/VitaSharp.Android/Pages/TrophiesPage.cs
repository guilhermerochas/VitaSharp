using VitaSharp.Core.Localization;
using MauiReactor;
using MauiReactor.Shapes;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Util;
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
    // the tap that opened the screen (GamePage): the phases of the opening are measured from it
    static long s_openedAt;

    /// <summary>The variant of the opening (<see cref="EnvVars.AndroidTrophiesOpen"/>): async (worker thread behind a
    /// spinner) or the default (from the cache on the UI thread); "noanim" pushes without the transition.</summary>
    static string OpenVariant => System.Environment.GetEnvironmentVariable(EnvVars.AndroidTrophiesOpen) ?? "";

    /// <summary>Opens the screen for a game, measured from now.</summary>
    public static Task Open(Microsoft.Maui.Controls.INavigation? navigation, string titleId)
    {
        if (navigation == null)
            return Task.CompletedTask;
        s_openedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        return navigation.PushAsync<TrophiesPage, GameProps>(!OpenVariant.Contains("noanim", StringComparison.Ordinal), p => p.TitleId = titleId);
    }

    OpenPhases _phases = new(System.Diagnostics.Stopwatch.GetTimestamp());
    string _loadInfo = "";

    protected override void OnMounted()
    {
        _phases = new OpenPhases(s_openedAt != 0 ? s_openedAt : System.Diagnostics.Stopwatch.GetTimestamp());
        s_openedAt = 0;
        _phases.Mark("mount");
        var game = AppServices.FindGame(Props.TitleId, withSize: false);
        TrophyListCache cache = AppServices.TrophyLists;
        // pt-BR texts when the set has them (TROP_17.SFM)
        const int Language = 17;
        if (game != null && !OpenVariant.Contains("async", StringComparison.Ordinal) && cache.IsLoaded(game, Language))
        {
            // opened before: the list is in memory (a Load only checks the files' dates), so the first render already
            // shows it, without a spinner page built and replaced
            Load(game, cache, Language);
        }
        else
        {
            _ = Task.Run(() => Load(game, cache, Language));
        }
        base.OnMounted();
    }

    void Load(GameEntry? game, TrophyListCache cache, int language)
    {
        int trpReads = cache.TrpReads;
        int extractions = cache.IconExtractions;
        int diskHits = cache.DiskHits;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // texts and pictures from the disk cache when unchanged
        var list = game != null ? cache.Load(game, AppServices.Data, language) : null;
        watch.Stop();
        _loadInfo = $"{list?.Rows.Count ?? 0} rows in {watch.ElapsedMilliseconds} ms (TRP reads {cache.TrpReads - trpReads}, " +
            $"picture extractions {cache.IconExtractions - extractions}, sets from disk {cache.DiskHits - diskHits})";
        var rows = list?.Filtered(TrophyList.Filter.All) ?? [];
        _phases.Mark("data");
        SetState(s =>
        {
            s.List = list;
            s.Rows = rows;
        });
    }

    /// <summary>After the render that shows the list: marks the first frame (its layout starts) and the next one (the
    /// list was drawn), then writes the "Trophies:" line to the app log (and so to the F8 capture).</summary>
    void MeasureDrawn()
    {
        OpenPhases phases = _phases;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            phases.Mark("built");
            global::Android.Views.Choreographer.Instance?.PostFrameCallback(new FrameCallback(() =>
            {
                phases.Mark("frame");
                global::Android.Views.Choreographer.Instance?.PostFrameCallback(new FrameCallback(() =>
                {
                    phases.Mark("drawn");
                    string variant = OpenVariant.Length > 0 ? $" [{OpenVariant}]" : "";
                    VitaSharp.Core.Util.Log.Info($"Trophies: {Props.TitleId}: {_loadInfo}; from the tap{variant}: {phases.Format()}");
                }));
            }));
        });
    }

    sealed class FrameCallback(Action action) : Java.Lang.Object, global::Android.Views.Choreographer.IFrameCallback
    {
        public void DoFrame(long frameTimeNanos) => action();
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
        {
            _phases.Mark("spinner");
            return ContentPage(ActivityIndicator().IsRunning(true).Center()).HasNavigationBar(false).BackgroundColor(Palette.Background)
                .OnAppearing(() => _phases.Mark("appearing"));
        }
        if (!_phases.Reached("render"))
        {
            _phases.Mark("render");
            MeasureDrawn();
        }
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
            )).HasNavigationBar(false).BackgroundColor(Palette.Background)
            .OnAppearing(() => _phases.Mark("appearing"));
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
