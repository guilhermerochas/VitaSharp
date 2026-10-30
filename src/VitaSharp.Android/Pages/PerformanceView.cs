using MauiReactor;
using VitaSharp.Android.Library;
using VitaSharp.Android.Services;
using VitaSharp.Android.Settings;
using VitaSharp.Android.Ui;

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
        PageBody("Desempenho",
            VStack(
                SectionTitle("Este aparelho"),
                Card(
                    Row(".NET", RuntimeReport.IsMono ? "Mono" : "CoreCLR", detail: RuntimeReport.Runtime),
                    Divider(),
                    Row("Código dinâmico", RuntimeReport.DynamicCodeSupported ? "sim" : "não",
                        detail: RuntimeReport.DynamicCodeCompiled ? "compilado pelo JIT do runtime" : "interpretado"),
                    Divider(),
                    Row("CPU padrão", SettingsChoices.LabelOf(CpuChoices.Available(), AppServices.Prefs.CpuBackend)),
                    Divider(),
                    Row("Backends de CPU", string.Join(", ", CpuChoices.Available().Skip(1).Select(c => c.Label)))
                ),
                Card(
                    Row("Informações da GPU", null, () => Navigation?.PushAsync<GpuInfoPage>()),
                    Divider(),
                    Row("Driver da GPU", null, () => Navigation?.PushAsync<GpuDriverPage>())
                ),
                SectionTitle("Jogos"),
                Card(State.Played.Count == 0
                    ? [Row("Nenhum jogo jogado ainda", null)]
                    : State.Played.SelectMany((p, i) => new VisualNode?[]
                    {
                        i > 0 ? Divider() : null,
                        Row(p.Game.Title, p.Stats.AverageFps > 0 ? $"{p.Stats.AverageFps:0} FPS" : "—",
                            () => Navigation?.PushAsync<GamePage, GameProps>(x => x.TitleId = p.Game.TitleId),
                            detail: $"{GameLibrary.PlayTime(p.Stats.PlaySeconds)} · {GameLibrary.LastPlayed(p.Stats.LastPlayedUtc, DateTime.UtcNow)}"),
                    }).ToArray())
            ));
}
