using MauiReactor;
using MauiReactor.Shapes;
using VitaSharp.Android.Firmware;
using VitaSharp.Android.Game;
using VitaSharp.Android.Library;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Pages;

public sealed class LibraryState
{
    public List<GameEntry> Games { get; set; } = [];
    public Dictionary<string, TrophyProgress> Trophies { get; set; } = new();
    public string Search { get; set; } = "";
    public GameLibrary.Filter Filter { get; set; }
    public bool Loading { get; set; } = true;
    public string? Installing { get; set; }
    public double InstallProgress { get; set; }
    public FirmwareStatus? Firmware { get; set; }
}

/// <summary>
/// Biblioteca (docs/android/mockups/biblioteca.html): search, filters, "Continuar jogando" with the last game, the
/// "Sistema do Vita" entry, the grid of installed games with trophies earned/total, and "Instalar VPK" (Storage Access
/// Framework picker; the VPK is read in place and extracted into &lt;data&gt;/fs/ux0).
/// </summary>
public sealed class LibraryView : Screen<LibraryState>
{
    protected override void OnMounted()
    {
        MainActivity.Resumed += Reload;
        Reload();
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        MainActivity.Resumed -= Reload;
        base.OnWillUnmount();
    }

    void Reload() => _ = Task.Run(() =>
    {
        var data = AppServices.Data;
        var games = GameLibrary.Scan(data);
        var trophies = games.ToDictionary(g => g.TitleId, g => GameLibrary.Trophies(g, data));
        var fw = FirmwareStatus.Read(data.Fs);
        SetState(s =>
        {
            s.Games = games;
            s.Trophies = trophies;
            s.Firmware = fw;
            s.Loading = false;
        });
    });

    public override VisualNode Render()
    {
        var shown = GameLibrary.Query(State.Games, State.Search, State.Filter, g => AppServices.GameSettingsFor(g.TitleId));
        var last = State.Games.FirstOrDefault(g => g.TitleId == AppServices.Prefs.LastPlayed);
        return Grid("*", "*",
            VScrollView(
                VStack(
                    Grid("Auto", "*,Auto",
                        VStack(
                            Label("VITASHARP").FontSize(12).TextColor(Palette.Muted).CharacterSpacing(1),
                            Label("Biblioteca").FontFamily(Fonts.SoraBold).FontSize(28)
                        ).Spacing(2),
                        IconButton("⚙", () => RootPage.Show(RootTab.Settings)).GridColumn(1).VCenter()
                    ).Padding(20, 20, 20, 8),
                    SearchBox(),
                    HStack(
                        Chip($"Todos · {State.Games.Count}", State.Filter == GameLibrary.Filter.All, () => SetState(s => s.Filter = GameLibrary.Filter.All)),
                        Chip("Recentes", State.Filter == GameLibrary.Filter.Recent, () => SetState(s => s.Filter = GameLibrary.Filter.Recent)),
                        Chip("Jogáveis", State.Filter == GameLibrary.Filter.Playable, () => SetState(s => s.Filter = GameLibrary.Filter.Playable))
                    ).Spacing(8).Padding(20, 14, 20, 4),
                    last != null && State.Search.Length == 0 ? ContinueCard(last) : null,
                    SystemCard(),
                    State.Installing != null ? InstallCard() : null,
                    SectionTitle(State.Filter switch
                    {
                        GameLibrary.Filter.Recent => "Jogados recentemente",
                        GameLibrary.Filter.Playable => "Jogáveis",
                        _ => "Todos os jogos",
                    }, "A–Z"),
                    State.Loading ? ActivityIndicator().IsRunning(true).HCenter().Margin(0, 24)
                        : shown.Count == 0 ? EmptyLibrary()
                        : GameGrid(shown),
                    BoxView().HeightRequest(96).Color(Colors.Transparent)
                ).Spacing(0)),
            Button("+  Instalar VPK").OnClicked(InstallVpk).HEnd().VEnd().Margin(20).HeightRequest(56)
                .CornerRadius(22).IsEnabled(State.Installing == null)
        );
    }

    VisualNode SearchBox() =>
        Border(
            Grid("Auto", "Auto,*",
                Label("⌕").FontSize(18).TextColor(Palette.Muted).VCenter(),
                Entry().Placeholder("Buscar jogos").Text(State.Search).OnTextChanged(t => SetState(s => s.Search = t ?? ""))
                    .BackgroundColor(Colors.Transparent).GridColumn(1).Margin(8, 0, 0, 0)
            ).Padding(16, 0))
        .StrokeShape(new RoundRectangle().CornerRadius(16)).Stroke(Palette.BorderStrong).StrokeThickness(1)
        .BackgroundColor(Palette.Surface).HeightRequest(48).Margin(20, 8, 20, 0);

    VisualNode ContinueCard(GameEntry g)
    {
        var settings = AppServices.GameSettingsFor(g.TitleId);
        var t = State.Trophies.GetValueOrDefault(g.TitleId) ?? TrophyProgress.None;
        return Border(
                Grid("Auto", "Auto,*,Auto",
                    Cover(g.TitleId, g.Initials, g.IconPath, 84, 16, 22),
                    VStack(
                        Label("CONTINUAR JOGANDO").FontSize(11).TextColor(Palette.AccentSoft).CharacterSpacing(1),
                        Label(g.Title).FontFamily(Fonts.SoraSemiBold).FontSize(17).MaxLines(2).LineBreakMode(LineBreakMode.TailTruncation),
                        Label($"{GameLibrary.PlayTime(settings.PlaySeconds)} · {GameLibrary.LastPlayed(settings.LastPlayedUtc, DateTime.UtcNow).ToLowerInvariant()}")
                            .FontSize(12).TextColor(Palette.Muted),
                        TrophyLine(t, Palette.Track)
                    ).Spacing(4).GridColumn(1).Margin(14, 0).VCenter(),
                    Border(Label("▶").FontSize(20).TextColor(Palette.OnAccent).Center())
                        .StrokeShape(new RoundRectangle().CornerRadius(26)).StrokeThickness(0).BackgroundColor(Palette.Accent)
                        .WidthRequest(52).HeightRequest(52).GridColumn(2).VCenter()
                        .OnTapped(() => Play(g))
                ).Padding(16))
            .StrokeShape(new RoundRectangle().CornerRadius(22)).Stroke(Palette.HighlightBorder).StrokeThickness(1)
            .BackgroundColor(Palette.Highlight).Margin(20, 12, 20, 0)
            .OnTapped(() => OpenGame(g));
    }

    VisualNode SystemCard()
    {
        var fw = State.Firmware;
        string detail = fw is not { Shell: true } ? "Instale o firmware em Ajustes para usar"
            : $"O shell do firmware {fw.Installed?.Version ?? ""} em tela cheia";
        return Border(
                Grid("Auto", "Auto,*,Auto",
                    Border(Label("▭").FontSize(22).TextColor(Palette.AccentSoft).Center())
                        .StrokeShape(new RoundRectangle().CornerRadius(12)).StrokeThickness(0).BackgroundColor(Palette.SurfaceHigh)
                        .WidthRequest(40).HeightRequest(40),
                    VStack(
                        Label("Sistema do Vita").FontFamily(Fonts.InterSemiBold).FontSize(14),
                        Label(detail).FontSize(12).TextColor(Palette.Muted)
                    ).Spacing(2).GridColumn(1).Margin(12, 0).VCenter(),
                    Label("›").FontSize(20).TextColor(Palette.Faint).GridColumn(2).VCenter()
                ).Padding(14, 12))
            .StrokeShape(new RoundRectangle().CornerRadius(18)).Stroke(Palette.Border).StrokeThickness(1)
            .BackgroundColor(Palette.Surface).Margin(20, 12, 20, 0)
            .OnTapped(() =>
            {
                if (State.Firmware is { Shell: true }) GameActivity.Launch(MainActivity.Current!, GameActivity.SystemTitleId);
                else Navigation?.PushAsync<FirmwarePage>();
            });
    }

    VisualNode InstallCard() =>
        Card(
            VStack(
                Label($"Instalando {State.Installing}").FontFamily(Fonts.InterSemiBold).FontSize(14),
                ProgressBar().Progress(State.InstallProgress).Margin(0, 8, 0, 0)
            ).Padding(16)
        ).Margin(20, 12, 20, 0);

    VisualNode EmptyLibrary() =>
        VStack(
            Label("Nenhum jogo instalado").FontFamily(Fonts.SoraSemiBold).FontSize(16).HCenter(),
            Label(State.Games.Count == 0
                    ? "Toque em \"Instalar VPK\" e escolha o arquivo do jogo. Os jogos ficam na pasta de dados do app."
                    : "Nada corresponde à busca.")
                .FontSize(13).TextColor(Palette.Muted).HorizontalTextAlignment(TextAlignment.Center)
        ).Spacing(6).Padding(32, 24);

    VisualNode GameGrid(IReadOnlyList<GameEntry> games)
    {
        var rows = new List<VisualNode>();
        for (int i = 0; i < games.Count; i += 2)
            rows.Add(Grid("Auto", "*,*",
                    GameTile(games[i]),
                    i + 1 < games.Count ? GameTile(games[i + 1]).GridColumn(1) : null
                ).ColumnSpacing(14));
        return VStack([.. rows]).Spacing(18).Padding(20, 0);
    }

    VisualNode GameTile(GameEntry g)
    {
        var t = State.Trophies.GetValueOrDefault(g.TitleId) ?? TrophyProgress.None;
        bool playable = g.Category.StartsWith("gd", StringComparison.OrdinalIgnoreCase);
        return VStack(
                Grid("*", "*",
                    g.IconPath != null ? Image().Source(g.IconPath).Aspect(Aspect.AspectFill) : null,
                    Grid("Auto", "*,Auto",
                        g.IconPath == null ? Label(g.Initials).FontFamily(Fonts.SoraBold).FontSize(24).TextColor(Color.FromArgb("#F1F4F9")).VEnd() : null,
                        Border(Label(playable ? "Jogável" : g.Category).FontFamily(Fonts.InterSemiBold).FontSize(11).TextColor(Color.FromArgb("#8FB2FF")).Center())
                            .StrokeShape(new RoundRectangle().CornerRadius(11)).StrokeThickness(0)
                            .BackgroundColor(Color.FromArgb("#B8090C12")).Padding(8, 0).HeightRequest(22).GridColumn(1).VEnd()
                    ).Padding(12).VEnd()
                ).HeightRequest(160).BackgroundColor(Color.FromArgb(GameLibrary.CoverColor(g.TitleId))),
                Label(g.Title).FontFamily(Fonts.InterSemiBold).FontSize(14).LineBreakMode(LineBreakMode.TailTruncation),
                Label(g.TitleId).FontSize(12).TextColor(Palette.Muted),
                TrophyLine(t, Palette.Border)
            ).Spacing(4)
            .OnTapped(() => OpenGame(g));
    }

    VisualNode TrophyLine(TrophyProgress t, Color track) =>
        Grid("Auto", "Auto,Auto,*",
            Label("🏆").FontSize(11).TextColor(Palette.Gold).VCenter(),
            Label(t.Text).FontSize(11).TextColor(Palette.TextSoft).GridColumn(1).Margin(6, 0).VCenter(),
            t.HasTrophies ? Bar(t.Fraction, track).GridColumn(2) : null
        );

    void OpenGame(GameEntry g) => Navigation?.PushAsync<GamePage, GameProps>(p => p.TitleId = g.TitleId);

    static void Play(GameEntry g) => GameActivity.Launch(MainActivity.Current!, g.TitleId);

    async void InstallVpk()
    {
        var doc = await Saf.PickAsync("application/zip", "application/octet-stream", "application/vnd.android.package-archive");
        if (doc == null) return;
        SetState(s => { s.Installing = doc.DisplayName; s.InstallProgress = 0; });
        await Run("Instalar VPK", async () =>
        {
            try
            {
                var results = await Task.Run(() =>
                {
                    using var stream = doc.OpenSeekable(System.IO.Path.Combine(AppServices.AppContext.CacheDir!.AbsolutePath, "imports"));
                    return VpkInstaller.Install(stream, AppServices.Data.Ux0, new VpkInstallOptions
                    {
                        Progress = p => SetState(s => s.InstallProgress = p.Fraction),
                        Log = m => Log.Info($"VPK: {m}"),
                    });
                });
                var installed = results.Where(r => r.Installed).ToList();
                await Alert("Instalar VPK", installed.Count == 0 ? "Nada foi instalado."
                    : string.Join("\n", installed.Select(r => $"{r.Content.TitleId} · {r.Content.Title} ({r.Content.Kind})")) + "\n\nInstalado.");
            }
            finally
            {
                SetState(s => s.Installing = null);
                Reload();
            }
        });
    }
}
