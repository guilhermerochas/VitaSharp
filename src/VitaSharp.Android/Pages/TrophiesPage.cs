using MauiReactor;
using MauiReactor.Shapes;
using VitaSharp.Android.Library;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Hle.Np;

namespace VitaSharp.Android.Pages;

public sealed class TrophiesState
{
    public TrophyList? List { get; set; }
    public TrophyList.Filter Filter { get; set; }
}

/// <summary>
/// Troféus (trofeus.html): the game's trophy set from its TROPHY.TRP and the unlocks in ux0:user/00/trophy: progress,
/// counts per grade, filters and the list (icon, grade, description, unlock date); locked and hidden trophies dimmed.
/// </summary>
public sealed class TrophiesPage : Screen<TrophiesState, GameProps>
{
    protected override void OnMounted()
    {
        _ = Task.Run(() =>
        {
            var game = AppServices.FindGame(Props.TitleId);
            // pt-BR texts when the set has them (TROP_17.SFM)
            var list = game != null ? TrophyList.Load(game, AppServices.Data, language: 17) : null;
            SetState(s => s.List = list);
        });
        base.OnMounted();
    }

    public override VisualNode Render()
    {
        if (State.List is not { } list)
            return ContentPage(ActivityIndicator().IsRunning(true).Center()).HasNavigationBar(false).BackgroundColor(Palette.Background);
        var p = list.Progress;
        var rows = list.Filtered(State.Filter);
        return Page(list.Title,
            VStack(
                Border(
                    VStack(
                        Grid("Auto", "*,Auto",
                            Label($"{p.Earned} de {p.Total} troféus").FontFamily(Fonts.SoraSemiBold).FontSize(17),
                            Label($"{p.Percent}%").FontFamily(Fonts.SoraBold).FontSize(17).TextColor(Palette.Accent).GridColumn(1)
                        ),
                        Bar(p.Fraction).Margin(0, 12, 0, 12),
                        Grid("Auto", "*,*,*,*",
                            GradeCount(TrophyGrade.Platinum, p, 0), GradeCount(TrophyGrade.Gold, p, 1),
                            GradeCount(TrophyGrade.Silver, p, 2), GradeCount(TrophyGrade.Bronze, p, 3))
                    ).Padding(16))
                .StrokeShape(new RoundRectangle().CornerRadius(20)).StrokeThickness(0).BackgroundColor(Palette.Surface).Margin(20, 8, 20, 0),
                HStack(
                    Chip("Todos", State.Filter == TrophyList.Filter.All, () => SetState(s => s.Filter = TrophyList.Filter.All)),
                    Chip("Conquistados", State.Filter == TrophyList.Filter.Earned, () => SetState(s => s.Filter = TrophyList.Filter.Earned)),
                    Chip("Faltam", State.Filter == TrophyList.Filter.Missing, () => SetState(s => s.Filter = TrophyList.Filter.Missing))
                ).Spacing(8).Padding(20, 16, 20, 8),
                VStack(rows.Select(TrophyRowView).ToArray()).Spacing(10).Padding(20, 0)
            ), kicker: "Troféus");
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
        var icon = r.Icon is { } bytes && (r.Unlocked || !r.Hidden)
            ? Image().Source(ImageSource.FromStream(() => new MemoryStream(bytes))).Aspect(Aspect.AspectFill)
            : (VisualNode)Label(r.Hidden ? "?" : "🏆").FontSize(20).TextColor(Palette.Muted).Center();
        return Border(
                Grid("Auto", "Auto,*",
                    Border(icon).StrokeShape(new RoundRectangle().CornerRadius(12)).StrokeThickness(0)
                        .BackgroundColor(Palette.SurfaceHigh).WidthRequest(52).HeightRequest(52),
                    VStack(
                        Grid("Auto", "*,Auto",
                            Label(r.ShownName).FontFamily(Fonts.InterSemiBold).FontSize(14).LineBreakMode(LineBreakMode.TailTruncation),
                            Label(TrophyList.GradeName(r.Grade)).FontSize(11).TextColor(Color.FromArgb(TrophyList.GradeColor(r.Grade))).GridColumn(1).Margin(8, 0, 0, 0)
                        ),
                        Label(r.ShownDetail).FontSize(12).TextColor(Palette.Muted).MaxLines(3),
                        r.UnlockedUtc is { } d ? Label($"Conquistado em {d.ToLocalTime():dd/MM/yyyy HH:mm}").FontSize(11).TextColor(Palette.AccentSoft) : null
                    ).Spacing(3).GridColumn(1).Margin(12, 0, 0, 0).VCenter()
                ).Padding(12))
            .StrokeShape(new RoundRectangle().CornerRadius(16)).StrokeThickness(0).BackgroundColor(Palette.Surface)
            .Opacity(opacity);
    }
}
