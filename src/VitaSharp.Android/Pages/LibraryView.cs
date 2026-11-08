using VitaSharp.Core.Localization;
using MauiReactor;
using MauiReactor.Shapes;
using VitaSharp.Android.Game;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Firmware;
using VitaSharp.Mobile.Core.Library;

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
    /// <summary>"42% - 80 MB/s - 0:12 restantes" under the install bar.</summary>
    public string InstallDetail { get; set; } = "";
    public FirmwareStatus? Firmware { get; set; }
}

/// <summary>
/// Biblioteca (docs/android/mockups/biblioteca.html): search, filters, "Continuar jogando" with the last game, the
/// "Sistema do Vita" entry, the grid of installed games with trophies earned/total, and "Instalar jogo" (header chip,
/// floating button, empty-library button and Ajustes): a .vpk, or a .pkg with its work.bin/.rif/zRIF license, picked
/// with the Storage Access Framework, read in place and extracted into &lt;data&gt;/fs/ux0.
/// </summary>
public sealed class LibraryView : Screen<LibraryState>
{
    /// <summary>Set by another screen (Ajustes > Instalar jogo) before it switches to the library tab.</summary>
    public static bool InstallRequested;

    protected override void OnMounted()
    {
        MainActivity.Resumed += Reload;
        Reload();
        base.OnMounted();
        if (InstallRequested) { InstallRequested = false; InstallGame(); }
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
                            Label(Loc.Get("MLibrary")).FontFamily(Fonts.SoraBold).FontSize(28)
                        ).Spacing(2),
                        HStack(
                            Chip(Loc.Get("MInstallChip"), true, InstallGame),
                            IconButton("\u2699", () => RootPage.Show(RootTab.Settings)).VCenter()
                        ).Spacing(10).GridColumn(1).VCenter()
                    ).Padding(20, 20, 20, 8),
                    SearchBox(),
                    HStack(
                        Chip(Loc.Format("MAllCount", State.Games.Count), State.Filter == GameLibrary.Filter.All, () => SetState(s => s.Filter = GameLibrary.Filter.All)),
                        Chip(Loc.Get("MRecent"), State.Filter == GameLibrary.Filter.Recent, () => SetState(s => s.Filter = GameLibrary.Filter.Recent)),
                        Chip(Loc.Get("MPlayable"), State.Filter == GameLibrary.Filter.Playable, () => SetState(s => s.Filter = GameLibrary.Filter.Playable))
                    ).Spacing(8).Padding(20, 14, 20, 4),
                    last != null && State.Search.Length == 0 ? ContinueCard(last) : null,
                    SystemCard(),
                    State.Installing != null ? InstallCard() : null,
                    SectionTitle(State.Filter switch
                    {
                        GameLibrary.Filter.Recent => Loc.Get("MRecentlyPlayed"),
                        GameLibrary.Filter.Playable => Loc.Get("MPlayable"),
                        _ => Loc.Get("MAllGames"),
                    }, "A\u2013Z"),
                    State.Loading ? ActivityIndicator().IsRunning(true).HCenter().Margin(0, 24)
                        : shown.Count == 0 ? EmptyLibrary()
                        : GameGrid(shown),
                    BoxView().HeightRequest(96).Color(Colors.Transparent)
                ).Spacing(0)),
            Button(Loc.Get("MInstallGamePlus")).OnClicked(InstallGame).HEnd().VEnd().Margin(20).HeightRequest(56)
                .CornerRadius(22).IsEnabled(State.Installing == null)
        );
    }

    VisualNode SearchBox() =>
        Border(
            Grid("Auto", "Auto,*",
                Label("\u2315").FontSize(18).TextColor(Palette.Muted).VCenter(),
                Entry().Placeholder(Loc.Get("MSearchGames")).Text(State.Search).OnTextChanged(t => SetState(s => s.Search = t ?? ""))
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
                        Label(Loc.Get("MContinuePlaying")).FontSize(11).TextColor(Palette.AccentSoft).CharacterSpacing(1),
                        Label(g.Title).FontFamily(Fonts.SoraSemiBold).FontSize(17).MaxLines(2).LineBreakMode(LineBreakMode.TailTruncation),
                        Label($"{GameLibrary.PlayTime(settings.PlaySeconds)} \u00B7 {GameLibrary.LastPlayed(settings.LastPlayedUtc, DateTime.UtcNow).ToLowerInvariant()}")
                            .FontSize(12).TextColor(Palette.Muted),
                        TrophyLine(t, Palette.Track)
                    ).Spacing(4).GridColumn(1).Margin(14, 0).VCenter(),
                    Border(Label("\u25B6").FontSize(20).TextColor(Palette.OnAccent).Center())
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
        string detail = fw is not { Shell: true } ? Loc.Get("MInstallFirmwareFirst")
            : Loc.Format("MShellFullscreen", fw.Installed?.Version ?? "");
        return Border(
                Grid("Auto", "Auto,*,Auto",
                    Border(Label("\u25AD").FontSize(22).TextColor(Palette.AccentSoft).Center())
                        .StrokeShape(new RoundRectangle().CornerRadius(12)).StrokeThickness(0).BackgroundColor(Palette.SurfaceHigh)
                        .WidthRequest(40).HeightRequest(40),
                    VStack(
                        Label(Loc.Get("MVitaSystem")).FontFamily(Fonts.InterSemiBold).FontSize(14),
                        Label(detail).FontSize(12).TextColor(Palette.Muted)
                    ).Spacing(2).GridColumn(1).Margin(12, 0).VCenter(),
                    Label("\u203A").FontSize(20).TextColor(Palette.Faint).GridColumn(2).VCenter()
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
                Label(Loc.Format("MInstalling", State.Installing)).FontFamily(Fonts.InterSemiBold).FontSize(14),
                ProgressBar().Progress(State.InstallProgress).Margin(0, 8, 0, 0),
                Label(State.InstallDetail).FontSize(12).Margin(0, 6, 0, 0)
            ).Padding(16)
        ).Margin(20, 12, 20, 0);

    VisualNode EmptyLibrary() =>
        VStack(
            Label(Loc.Get("MNoGames")).FontFamily(Fonts.SoraSemiBold).FontSize(16).HCenter(),
            Label(State.Games.Count == 0
                    ? Loc.Get("MNoGamesDetail")
                    : Loc.Get("MNoMatch"))
                .FontSize(13).TextColor(Palette.Muted).HorizontalTextAlignment(TextAlignment.Center),
            State.Games.Count == 0 ? PrimaryButton(Loc.Get("MInstallGamePlus"), InstallGame, State.Installing == null).Margin(0, 12, 0, 0) : null
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
                        Border(Label(playable ? Loc.Get("MPlayableOne") : g.Category).FontFamily(Fonts.InterSemiBold).FontSize(11).TextColor(Color.FromArgb("#8FB2FF")).Center())
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
            Label("\U0001F3C6").FontSize(11).TextColor(Palette.Gold).VCenter(),
            Label(t.Text).FontSize(11).TextColor(Palette.TextSoft).GridColumn(1).Margin(6, 0).VCenter(),
            t.HasTrophies ? Bar(t.Fraction, track).GridColumn(2) : null
        );

    void OpenGame(GameEntry g) => Navigation?.PushAsync<GamePage, GameProps>(p => p.TitleId = g.TitleId);

    static void Play(GameEntry g) => GameActivity.Launch(MainActivity.Current!, g.TitleId);

    async void InstallGame()
    {
        if (State.Installing != null) return;
        var kind = await Choose(Loc.Get("MInstallGame"), [
            new Mobile.Core.Settings.Choice("vpk", Loc.Get("MVpkFile")),
            new Mobile.Core.Settings.Choice("pkg", Loc.Get("MPkgFile")),
        ]);
        if (kind == "vpk") await InstallVpk();
        else if (kind == "pkg") await InstallPkg();
    }

    static string ImportCache => System.IO.Path.Combine(AppServices.AppContext.CacheDir!.AbsolutePath, "imports");

    /// <summary>A retail .pkg with its NoNpDrm license: the work.bin/.rif file, or the zRIF text pasted in.</summary>
    async Task InstallPkg()
    {
        var pkg = await Saf.PickAsync("application/octet-stream");
        if (pkg == null) return;
        if (!pkg.DisplayName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)
            && !await Confirm(Loc.Get("MInstallPkg"), Loc.Format("MNotPkgConfirm", pkg.DisplayName), Loc.Get("MTry")))
            return;
        var source = await Choose(Loc.Get("MGameLicense"), [
            new Mobile.Core.Settings.Choice("file", Loc.Get("MLicenseFile")),
            new Mobile.Core.Settings.Choice("zrif", Loc.Get("MPasteZrif")),
        ]);
        byte[]? license = null;
        if (source == "file")
        {
            var lic = await Saf.PickAsync("application/octet-stream");
            if (lic == null) return;
            await Run(Loc.Get("MLicense"), async () => license = await Task.Run(() =>
            {
                using var s = lic.OpenSeekable(ImportCache);
                var bytes = new byte[Math.Min(s.Length, 4096)];
                s.ReadExactly(bytes);
                return bytes;
            }));
        }
        else if (source == "zrif")
        {
            var text = await Prompt("zRIF", Loc.Get("MPasteZrifDetail"));
            if (string.IsNullOrWhiteSpace(text)) return;
            try { license = Zrif.Decode(text); }
            catch (InvalidDataException e) { await Alert(Loc.Get("MInvalidZrif"), e.Message); return; }
        }
        if (license == null) return;

        SetState(s => { s.Installing = pkg.DisplayName; s.InstallProgress = 0; s.InstallDetail = ""; });
        await Run(Loc.Get("MInstallPkg"), async () =>
        {
            try
            {
                var t = await Task.Run(() => PkgInstaller.Install(pkg.OpenSeekable(ImportCache), license, AppServices.Data.Ux0,
                    m => Log.Info($"PKG: {m}"), ShowProgress));
                await Alert(Loc.Get("MInstallPkg"), Loc.Format("MInstalledOne", t.TitleId, t.Title, t.Type));
            }
            finally
            {
                SetState(s => s.Installing = null);
                Reload();
            }
        });
    }

    void ShowProgress(InstallProgress p) => SetState(s =>
    {
        s.InstallProgress = p.Fraction;
        s.InstallDetail = p.Describe(Loc.Get("MRemaining"));
    });

    async Task InstallVpk()
    {
        var doc = await Saf.PickAsync("application/zip", "application/octet-stream", "application/vnd.android.package-archive");
        if (doc == null) return;
        SetState(s => { s.Installing = doc.DisplayName; s.InstallProgress = 0; s.InstallDetail = ""; });
        await Run(Loc.Get("MInstallVpk"), async () =>
        {
            try
            {
                var results = await Task.Run(() =>
                {
                    using var stream = doc.OpenSeekable(ImportCache);
                    return VpkInstaller.Install(stream, AppServices.Data.Ux0, new VpkInstallOptions
                    {
                        Progress = ShowProgress,
                        Log = m => Log.Info($"VPK: {m}"),
                    });
                });
                var installed = results.Where(r => r.Installed).ToList();
                await Alert(Loc.Get("MInstallVpk"), installed.Count == 0 ? Loc.Get("MNothingInstalled")
                    : string.Join("\n", installed.Select(r => $"{r.Content.TitleId} \u00B7 {r.Content.Title} ({r.Content.Kind})")) + Loc.Get("MInstalledSuffix"));
            }
            finally
            {
                SetState(s => s.Installing = null);
                Reload();
            }
        });
    }
}
