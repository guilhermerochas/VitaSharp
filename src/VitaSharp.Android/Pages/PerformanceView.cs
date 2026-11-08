using VitaSharp.Core.Localization;
using MauiReactor;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Mobile.Core.Library;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Android.Pages;

public sealed class PerformanceState
{
    public List<(GameEntry Game, GameSettings Stats)> Played { get; set; } = [];
}

/// <summary>
/// Desempenho: what this build runs on (.NET runtime, dynamic code, the CPU backends the device has), the GPU pages,
/// and the play statistics of the games (play time, average FPS of the last session).
/// </summary>
public sealed class PerformanceView : Screen<PerformanceState>
{
    protected override void OnMounted()
    {
        base.OnMounted();
        _ = Task.Run(() =>
        {
            var played = GameLibrary.Scan(AppServices.Data)
                .Select(g => (g, AppServices.GameSettingsFor(g.TitleId)))
                .Where(x => x.Item2.PlaySeconds > 0)
                .OrderByDescending(x => x.Item2.LastPlayedUtc).ToList();
            SetState(s => s.Played = played);
        });
    }

    public override VisualNode Render() =>
        PageBody(Loc.Get("MPerformance"),
            VStack(
                SectionTitle(Loc.Get("MThisDevice")),
                Card(
                    Row(".NET", RuntimeReport.IsMono ? "Mono" : "CoreCLR", detail: RuntimeReport.Runtime),
                    Divider(),
                    Row(Loc.Get("MDynamicCode"), RuntimeReport.DynamicCodeSupported ? Loc.Get("MYesLower") : Loc.Get("MNoLower"),
                        detail: RuntimeReport.DynamicCodeCompiled ? Loc.Get("MJitCompiled") : Loc.Get("MInterpreted")),
                    Divider(),
                    Row(Loc.Get("MDefaultCpu"), SettingsChoices.LabelOf(CpuChoices.Available(), AppServices.Prefs.CpuBackend)),
                    Divider(),
                    Row(Loc.Get("MCpuBackends"), string.Join(", ", CpuChoices.Available().Skip(1).Select(c => c.Label)))
                ),
                Card(
                    Row(Loc.Get("MGpuInfo"), null, () => Navigation?.PushAsync<GpuInfoPage>()),
                    Divider(),
                    Row(Loc.Get("MGpuDriver"), null, () => Navigation?.PushAsync<GpuDriverPage>())
                ),
                SectionTitle(Loc.Get("MGames")),
                Card(State.Played.Count == 0
                    ? [Row(Loc.Get("MNoGamePlayed"), null)]
                    : State.Played.SelectMany((p, i) => new VisualNode?[]
                    {
                        i > 0 ? Divider() : null,
                        Row(p.Game.Title, p.Stats.AverageFps > 0 ? $"{p.Stats.AverageFps:0} FPS" : "\u2014",
                            () => Navigation?.PushAsync<GamePage, GameProps>(x => x.TitleId = p.Game.TitleId),
                            detail: $"{GameLibrary.PlayTime(p.Stats.PlaySeconds)} \u00B7 {GameLibrary.LastPlayed(p.Stats.LastPlayedUtc, DateTime.UtcNow)}"),
                    }).ToArray())
            ));
}
