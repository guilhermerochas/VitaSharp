using MauiReactor;
using MauiReactor.Shapes;
using VitaSharp.Android.Game;
using VitaSharp.Android.Library;
using VitaSharp.Android.Services;
using VitaSharp.Android.Settings;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Hle.Np;

namespace VitaSharp.Android.Pages;

public sealed class GameState
{
    public GameEntry? Game { get; set; }
    public TrophyProgress Trophies { get; set; } = TrophyProgress.None;
    public GameSettings Settings { get; set; } = new();
    public (long Bytes, DateTime? Modified) Saves { get; set; }
}

/// <summary>
/// Detalhes do jogo (detalhes.html): cover, play button, play time / average FPS / last played, the trophy card
/// (opens Troféus), the game's own settings (CPU backend, clock 333/444 MHz, GPU profile, VSync, FPS overlay; each
/// "Padrão" follows Ajustes) and its saves (ux0:user/00/savedata/TITLEID).
/// </summary>
public sealed class GamePage : Screen<GameState, GameProps>
{
    protected override void OnMounted()
    {
        MainActivity.Resumed += Load;
        Load();
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        MainActivity.Resumed -= Load;
        base.OnWillUnmount();
    }

    void Load() => _ = Task.Run(() =>
    {
        var data = AppServices.Data;
        var game = AppServices.FindGame(Props.TitleId);
        var trophies = game != null ? GameLibrary.Trophies(game, data) : TrophyProgress.None;
        var settings = GameSettings.Load(data.GameSettingsFile(Props.TitleId));
        var saves = SaveInfo(data.SaveDir(Props.TitleId));
        SetState(s => { s.Game = game; s.Trophies = trophies; s.Settings = settings; s.Saves = saves; });
    });

    static (long, DateTime?) SaveInfo(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return (0, null);
            var files = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            return (files.Sum(f => f.Length), files.Count > 0 ? files.Max(f => f.LastWriteTimeUtc) : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (0, null); }
    }

    public override VisualNode Render()
    {
        if (State.Game is not { } g)
            return ContentPage(ActivityIndicator().IsRunning(true).Center()).HasNavigationBar(false).BackgroundColor(Palette.Background);
        var s = State.Settings;
        var app = AppServices.Prefs;
        var eff = EffectiveSettings.Resolve(app, s);
        return ContentPage(
            VScrollView(
                VStack(
                    Grid("Auto", "Auto,*",
                        IconButton("‹", () => Navigation?.PopAsync()),
                        Label("").GridColumn(1)
                    ).Padding(16, 16, 16, 0),
                    HStack(
                        Cover(g.TitleId, g.Initials, g.IconPath, 112, 24, 34),
                        VStack(
                            Label(g.Title).FontFamily(Fonts.SoraBold).FontSize(24).MaxLines(3).LineBreakMode(LineBreakMode.TailTruncation),
                            Label(g.Subtitle).FontSize(14).TextColor(Palette.Muted)
                        ).Spacing(6).VCenter().MaximumWidthRequest(230)
                    ).Spacing(16).Padding(20, 16, 20, 0),
                    Grid("Auto", "*,Auto",
                        PrimaryButton("▶  Jogar", () => GameActivity.Launch(MainActivity.Current!, g.TitleId)),
                        IconButton("⋯", More, 54).GridColumn(1).Margin(12, 0, 0, 0)
                    ).Padding(20, 18, 20, 0),
                    Grid("Auto", "*,*,*",
                        Stat("Tempo jogado", GameLibrary.PlayTime(s.PlaySeconds), 0),
                        Stat("FPS médio", s.AverageFps > 0 ? $"{s.AverageFps:0}" : "—", 1),
                        Stat("Último", GameLibrary.LastPlayed(s.LastPlayedUtc, DateTime.UtcNow), 2)
                    ).ColumnSpacing(10).Padding(20, 16, 20, 0),
                    TrophyCard(),
                    SectionTitle("Configurações deste jogo"),
                    Card(
                        Row("CPU", ValueText(CpuChoices.Available(), s.CpuBackend, SettingsChoices.LabelOf(CpuChoices.Available(), eff.CpuBackend)),
                            () => Pick("CPU", CpuChoices.Available(), app.CpuBackend, v => s.CpuBackend = v)),
                        Divider(),
                        Row("Clock da CPU", ValueText(SettingsChoices.Clocks, s.CpuMhz?.ToString(), SettingsChoices.LabelOf(SettingsChoices.Clocks, eff.CpuMhz.ToString())),
                            () => Pick("Clock da CPU", SettingsChoices.Clocks, app.CpuMhz.ToString(), v => s.CpuMhz = v == null ? null : int.Parse(v))),
                        Divider(),
                        Row("Perfil da GPU", ValueText(SettingsChoices.GpuProfiles, s.GpuProfile, SettingsChoices.LabelOf(SettingsChoices.GpuProfiles, eff.GpuProfile)),
                            () => Pick("Perfil da GPU", SettingsChoices.GpuProfiles, app.GpuProfile, v => s.GpuProfile = v)),
                        Divider(),
                        ToggleRow("VSync", eff.VSync, v => Save(x => x.VSync = v == app.VSync ? null : v)),
                        Divider(),
                        ToggleRow("Mostrar FPS", eff.ShowFps, v => Save(x => x.ShowFps = v == app.ShowFps ? null : v)),
                        s.HasOverrides ? Divider() : null,
                        s.HasOverrides ? Row("Voltar ao padrão de Ajustes", null, () => Save(x =>
                        {
                            x.CpuBackend = null; x.CpuMhz = null; x.GpuProfile = null; x.VSync = null; x.ShowFps = null;
                        })) : null
                    ),
                    SectionTitle("Saves"),
                    Card(
                        Row(State.Saves.Bytes > 0 ? "Dados salvos" : "Nenhum save ainda",
                            State.Saves.Bytes > 0 ? $"{State.Saves.Bytes / 1024} KB" : null,
                            detail: State.Saves.Modified is { } m ? $"Atualizado {GameLibrary.LastPlayed(m, DateTime.UtcNow).ToLowerInvariant()} · ux0:user/00/savedata/{g.TitleId}" : $"ux0:user/00/savedata/{g.TitleId}"),
                        State.Saves.Bytes > 0 ? Divider() : null,
                        State.Saves.Bytes > 0 ? Row("Apagar saves", null, DeleteSaves, valueColor: Palette.Danger) : null
                    ),
                    BoxView().HeightRequest(24).Color(Colors.Transparent)
                ))
        ).HasNavigationBar(false).BackgroundColor(Palette.Background);
    }

    /// <summary>A per-game value: "Padrão (…)" when it follows Ajustes, else the game's own choice.</summary>
    static string ValueText(IReadOnlyList<Choice> choices, string? own, string effective) =>
        own == null ? $"Padrão · {effective}" : SettingsChoices.LabelOf(choices, own);

    VisualNode Stat(string label, string value, int column) =>
        Border(VStack(
                Label(label).FontSize(12).TextColor(Palette.Muted),
                Label(value).FontFamily(Fonts.SoraBold).FontSize(20).LineBreakMode(LineBreakMode.TailTruncation)
            ).Spacing(4).Padding(14, 12))
        .StrokeShape(new RoundRectangle().CornerRadius(18)).StrokeThickness(0).BackgroundColor(Palette.Surface)
        .GridColumn(column);

    VisualNode TrophyCard()
    {
        var t = State.Trophies;
        var grades = new[] { TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze };
        return Border(
                VStack(
                    Grid("Auto", "Auto,*,Auto,Auto",
                        Label("🏆").FontSize(18).TextColor(Palette.Gold).VCenter(),
                        Label("Troféus").FontFamily(Fonts.SoraSemiBold).FontSize(17).GridColumn(1).Margin(10, 0).VCenter(),
                        Label(t.HasTrophies ? $"{t.Earned} / {t.Total}" : "Sem troféus").FontFamily(Fonts.InterSemiBold).FontSize(15).GridColumn(2).VCenter(),
                        t.HasTrophies ? Label($"  {t.Percent}%").FontSize(15).TextColor(Palette.Accent).GridColumn(3).VCenter() : null
                    ),
                    t.HasTrophies ? Bar(t.Fraction).Margin(0, 12, 0, 0) : null,
                    t.HasTrophies
                        ? HStack(grades.Where(gr => t.ByGrade.ContainsKey(gr)).Select(gr =>
                            (VisualNode)HStack(
                                Label("●").FontSize(12).TextColor(Color.FromArgb(TrophyList.GradeColor(gr))).VCenter(),
                                Label($"{t.ByGrade[gr].Earned}/{t.ByGrade[gr].Total}").FontSize(14).TextColor(Palette.TextSoft).VCenter()
                            ).Spacing(6)).ToArray()).Spacing(18).Margin(0, 12, 0, 0)
                        : null
                ).Padding(16))
            .StrokeShape(new RoundRectangle().CornerRadius(20)).StrokeThickness(0).BackgroundColor(Palette.Surface)
            .Margin(20, 16, 20, 0)
            .OnTapped(() => { if (t.HasTrophies) Navigation?.PushAsync<TrophiesPage, GameProps>(p => p.TitleId = Props.TitleId); });
    }

    async void Pick(string title, IReadOnlyList<Choice> choices, string globalValue, Action<string?> apply)
    {
        var v = await Choose(title, SettingsChoices.WithDefault(choices, globalValue));
        if (v == null) return;
        Save(_ => apply(v == SettingsChoices.DefaultValue ? null : v));
    }

    void Save(Action<GameSettings> change)
    {
        var s = State.Settings;
        change(s);
        AppServices.SaveGameSettings(Props.TitleId, s);
        SetState(st => st.Settings = s);
    }

    async void DeleteSaves()
    {
        if (!await Confirm("Apagar saves", $"Apagar todos os dados salvos de {State.Game?.Title}? Não dá para desfazer.", "Apagar")) return;
        await Run("Apagar saves", async () =>
        {
            await Task.Run(() => Directory.Delete(AppServices.Data.SaveDir(Props.TitleId), true));
            Load();
        });
    }

    async void More()
    {
        if (State.Game is not { } g || ContainerPage is not { } page) return;
        var pick = await page.DisplayActionSheetAsync(g.Title, "Cancelar", "Desinstalar", "Troféus", "Copiar caminho da pasta");
        switch (pick)
        {
            case "Troféus":
                await (Navigation?.PushAsync<TrophiesPage, GameProps>(p => p.TitleId = g.TitleId) ?? Task.CompletedTask);
                break;
            case "Copiar caminho da pasta":
                await Clipboard.Default.SetTextAsync(g.AppDir);
                break;
            case "Desinstalar":
                if (!await Confirm("Desinstalar", $"Remover {g.Title} ({g.TitleId})? Os saves ficam.", "Remover")) return;
                await Run("Desinstalar", async () =>
                {
                    await Task.Run(() => Directory.Delete(g.AppDir, true));
                    await (Navigation?.PopAsync() ?? Task.CompletedTask);
                });
                break;
        }
    }
}
