using VitaSharp.Core.Localization;
using MauiReactor;
using MauiReactor.Shapes;
using VitaSharp.Android.Game;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Mobile.Core.Library;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Android.Pages;

public sealed class GameState
{
    public GameEntry? Game { get; set; }
    public TrophyProgress Trophies { get; set; } = TrophyProgress.None;
    public GameSettings Settings { get; set; } = new();
    public (long Bytes, DateTime? Modified) Saves { get; set; }
    public IReadOnlyList<StateSlot> Slots { get; set; } = [];
    /// <summary>The slots' pictures, made once per load (a new image source per render would reload them).</summary>
    public IReadOnlyList<ImageSource?> SlotPictures { get; set; } = [];
    public IReadOnlyList<GameRecording> Recordings { get; set; } = [];
    /// <summary>The recording highlighted after another one was deleted (the one that followed it), by path.</summary>
    public string? FocusedRecording { get; set; }
}

/// <summary>
/// Detalhes do jogo (detalhes.html): cover, play button, play time / average FPS / last played, the trophy card
/// (opens Trofeus), the game's own settings (CPU backend, clock 333/444 MHz, GPU profile, VSync, FPS overlay; each
/// "Padrao" follows Ajustes) and its saves (ux0:user/00/savedata/TITLEID).
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
        var trophies = game != null ? GameLibrary.Trophies(game, data, AppServices.TrophyTexts) : TrophyProgress.None;
        var settings = GameSettings.Load(data.GameSettingsFile(Props.TitleId));
        var saves = SaveInfo(data.SaveDir(Props.TitleId));
        var slots = GameStates.Slots(data.Root, Props.TitleId);
        var pictures = new ImageSource?[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Header?.Thumbnail is { Length: > 0 } png)
            {
                pictures[i] = ImageSource.FromStream(() => new MemoryStream(png));
            }
        }

        var recordings = GameStates.Recordings(System.IO.Path.Combine(data.Root, "captures"), Props.TitleId);
        SetState(s =>
        {
            s.Game = game;
            s.Trophies = trophies;
            s.Settings = settings;
            s.Saves = saves;
            s.Slots = slots;
            s.SlotPictures = pictures;
            s.Recordings = recordings;
        });
    });

    static (long, DateTime?) SaveInfo(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return (0, null);
            var files = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            return (files.Sum(f => f.Length), files.Count > 0 ? files.Max(f => f.LastWriteTimeUtc) : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (0, null);
        }
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
                        IconButton("\u2039", () => Navigation?.PopAsync()),
                        Label("").GridColumn(1)
                    ).Padding(16, 16, 16, 0),
                    Grid("Auto", "Auto,*",
                        Cover(g.TitleId, g.Initials, g.IconPath, 112, 24, 34),
                        VStack(
                            Label(g.Title).FontFamily(Fonts.SoraBold).FontSize(24).LineBreakMode(LineBreakMode.WordWrap),
                            Label(g.Subtitle).FontSize(14).TextColor(Palette.Muted).LineBreakMode(LineBreakMode.WordWrap),
                            g.LicenseMissing
                                ? Label(VitaSharp.Core.Package.TitleLicense.Message(g.TitleId)).FontSize(13).TextColor(Palette.Danger).LineBreakMode(LineBreakMode.WordWrap)
                                : null
                        ).Spacing(6).VCenter().GridColumn(1).Margin(16, 0, 0, 0)
                    ).Padding(20, 16, 20, 0),
                    Grid("Auto", "*,Auto",
                        PrimaryButton(Loc.Get("MPlay"), () => GameActivity.Launch(MainActivity.Current!, g.TitleId)),
                        IconButton("\u22EF", More, 54).GridColumn(1).Margin(12, 0, 0, 0)
                    ).Padding(20, 18, 20, 0),
                    Grid("Auto", "*,*,*",
                        Stat(Loc.Get("MPlayTime"), GameLibrary.PlayTime(s.PlaySeconds), 0),
                        Stat(Loc.Get("MAverageFps"), s.AverageFps > 0 ? $"{s.AverageFps:0}" : "\u2014", 1),
                        Stat(Loc.Get("MLast"), GameLibrary.LastPlayed(s.LastPlayedUtc, DateTime.UtcNow), 2)
                    ).ColumnSpacing(10).Padding(20, 16, 20, 0),
                    TrophyCard(),
                    SectionTitle(Loc.Get("SaveStatesTitle"), Loc.Get("MStatesOpenHint")),
                    StatesGrid(g),
                    SectionTitle(Loc.Get("MRecordAll")),
                    RecordingsCard(),
                    SectionTitle(Loc.Get("MGameSettings")),
                    Card(
                        Row("CPU", ValueText(CpuChoices.Available(), s.CpuBackend, SettingsChoices.LabelOf(CpuChoices.Available(), eff.CpuBackend)),
                            () => Pick("CPU", CpuChoices.Available(), app.CpuBackend, v => s.CpuBackend = v)),
                        Divider(),
                        Row(Loc.Get("MCpuClock"), ValueText(SettingsChoices.Clocks, s.CpuMhz?.ToString(), SettingsChoices.LabelOf(SettingsChoices.Clocks, eff.CpuMhz.ToString())),
                            () => Pick(Loc.Get("MCpuClock"), SettingsChoices.Clocks, app.CpuMhz.ToString(), v => s.CpuMhz = v == null ? null : int.Parse(v))),
                        Divider(),
                        Row(Loc.Get("MGpuProfile"), ValueText(SettingsChoices.GpuProfiles, s.GpuProfile, SettingsChoices.LabelOf(SettingsChoices.GpuProfiles, eff.GpuProfile)),
                            () => Pick(Loc.Get("MGpuProfile"), SettingsChoices.GpuProfiles, app.GpuProfile, v => s.GpuProfile = v)),
                        Divider(),
                        Row(Loc.Get("MInternalResolution"), ValueText(SettingsChoices.ResScales, s.ResScale, SettingsChoices.LabelOf(SettingsChoices.ResScales, eff.ResScale)),
                            () => Pick(Loc.Get("MInternalResolution"), SettingsChoices.ResScales, app.ResScale, v => s.ResScale = v),
                            detail: Loc.Get("MInternalResolutionDetail")),
                        Divider(),
                        Row(Loc.Get("MMsaa"), ValueText(SettingsChoices.MsaaModes, s.Msaa, SettingsChoices.LabelOf(SettingsChoices.MsaaModes, eff.Msaa)),
                            () => Pick(Loc.Get("MMsaa"), SettingsChoices.MsaaModes, "", v => s.Msaa = v)),
                        Divider(),
                        Row(Loc.Get("FrameRate"), ValueText(SettingsChoices.FrameRates, s.FrameRate, SettingsChoices.LabelOf(SettingsChoices.FrameRates, eff.FrameRate)),
                            () => Pick(Loc.Get("FrameRate"), SettingsChoices.FrameRates, "", v => s.FrameRate = string.IsNullOrEmpty(v) ? null : v),
                            detail: Loc.Get("MFrameRateDetail")),
                        Divider(),
                        ToggleRow("VSync", eff.VSync, v => Save(x => x.VSync = v == app.VSync ? null : v)),
                        Divider(),
                        ToggleRow(Loc.Get("MShowFps"), eff.ShowFps, v => Save(x => x.ShowFps = v == app.ShowFps ? null : v)),
                        Divider(),
                        ToggleRow(Loc.Get("FastLoading"), eff.FastLoading, v => Save(x => x.FastLoading = v == app.FastLoading ? null : v),
                            detail: Loc.Get("FastLoadingDetail")),
                        Divider(),
                        ToggleRow(Loc.Get("LoadShaderCache"), eff.LoadShaderCacheBeforePlaying,
                            v => Save(x => x.LoadShaderCacheBeforePlaying = v == app.LoadShaderCacheBeforePlaying ? null : v),
                            detail: Loc.Get("LoadShaderCacheDetail")),
                        Divider(),
                        ToggleRow(Loc.Get("PrecompileCode"), eff.PrecompileCode,
                            v => Save(x => x.PrecompileCode = v == app.PrecompileCode ? null : v),
                            detail: Loc.Get("PrecompileCodeDetail")),
                        Divider(),
                        Row(Loc.Get("SystemLanguage"), ValueText(SettingsChoices.SystemLanguages, s.SystemLanguage, SettingsChoices.LabelOf(SettingsChoices.SystemLanguages, eff.SystemLanguage)),
                            () => Pick(Loc.Get("SystemLanguage"), SettingsChoices.SystemLanguages, "", v => s.SystemLanguage = string.IsNullOrEmpty(v) ? null : v),
                            detail: Loc.Get("SystemLanguageDetail")),
                        s.HasOverrides ? Divider() : null,
                        s.HasOverrides ? Row(Loc.Get("MResetToDefaults"), null, () => Save(x =>
                        {
                            x.CpuBackend = null;
                            x.CpuMhz = null;
                            x.GpuProfile = null;
                            x.VSync = null;
                            x.ShowFps = null;
                            x.ResScale = null;
                            x.Msaa = null;
                            x.FastLoading = null;
                            x.LoadShaderCacheBeforePlaying = null;
                            x.PrecompileCode = null;
                            x.FrameRate = null;
                            x.SystemLanguage = null;
                        })) : null
                    ),
                    SectionTitle(Loc.Get("MSaves")),
                    Card(
                        Row(State.Saves.Bytes > 0 ? Loc.Get("MSavedData") : Loc.Get("MNoSaveYet"),
                            State.Saves.Bytes > 0 ? $"{State.Saves.Bytes / 1024} KB" : null,
                            detail: State.Saves.Modified is { } m ? Loc.Format("MSavesUpdated", GameLibrary.LastPlayed(m, DateTime.UtcNow).ToLowerInvariant(), VitaSharp.Core.Io.FirmwareLayout.SaveData.Guest(g.TitleId)) : VitaSharp.Core.Io.FirmwareLayout.SaveData.Guest(g.TitleId)),
                        State.Saves.Bytes > 0 ? Divider() : null,
                        State.Saves.Bytes > 0 ? Row(Loc.Get("MDeleteSaves"), null, DeleteSaves, valueColor: Palette.Danger) : null
                    ),
                    BoxView().HeightRequest(24).Color(Colors.Transparent)
                ))
        ).HasNavigationBar(false).BackgroundColor(Palette.Background);
    }

    /// <summary>A per-game value: "Padrao (...)" when it follows Ajustes, else the game's own choice.</summary>
    static string ValueText(IReadOnlyList<Choice> choices, string? own, string effective) =>
        own == null ? Loc.Format("MDefaultValue", effective) : SettingsChoices.LabelOf(choices, own);

    VisualNode Stat(string label, string value, int column) =>
        Border(VStack(
                Label(label).FontSize(12).TextColor(Palette.Muted),
                Label(value).FontFamily(Fonts.SoraBold).FontSize(20).LineBreakMode(LineBreakMode.WordWrap)
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
                        Label("\U0001F3C6").FontSize(18).TextColor(Palette.Gold).VCenter(),
                        Label(Loc.Get("MTrophies")).FontFamily(Fonts.SoraSemiBold).FontSize(17).GridColumn(1).Margin(10, 0).VCenter(),
                        Label(t.HasTrophies ? $"{t.Earned} / {t.Total}" : Loc.Get("MNoTrophies")).FontFamily(Fonts.InterSemiBold).FontSize(15).GridColumn(2).VCenter(),
                        t.HasTrophies ? Label($"  {t.Percent}%").FontSize(15).TextColor(Palette.Accent).GridColumn(3).VCenter() : null
                    ),
                    t.HasTrophies ? Bar(t.Fraction).Margin(0, 12, 0, 0) : null,
                    t.HasTrophies
                        ? HStack(grades.Where(gr => t.ByGrade.ContainsKey(gr)).Select(gr =>
                            (VisualNode)HStack(
                                Label("\u25CF").FontSize(12).TextColor(Color.FromArgb(TrophyList.GradeColor(gr))).VCenter(),
                                Label($"{t.ByGrade[gr].Earned}/{t.ByGrade[gr].Total}").FontSize(14).TextColor(Palette.TextSoft).VCenter()
                            ).Spacing(6)).ToArray()).Spacing(18).Margin(0, 12, 0, 0)
                        : null
                ).Padding(16))
            .StrokeShape(new RoundRectangle().CornerRadius(20)).StrokeThickness(0).BackgroundColor(Palette.Surface)
            .Margin(20, 16, 20, 0)
            .OnTapped(() => { if (t.HasTrophies) Navigation?.PushAsync<TrophiesPage, GameProps>(p => p.TitleId = Props.TitleId); });
    }

    /// <summary>Slots 1-6 with their picture and date; a tap opens the game already in that state (the desktop's game
    /// page and --load-state). An empty or incompatible slot says so and does nothing.</summary>
    VisualNode StatesGrid(GameEntry g)
    {
        var slots = State.Slots;
        var cells = new List<VisualNode>();
        for (int i = 0; i < slots.Count; i++)
        {
            StateSlot slot = slots[i];
            ImageSource? picture = i < State.SlotPictures.Count ? State.SlotPictures[i] : null;
            string line = slot.Problem ?? (slot.Header is { } h ? h.CreatedUtc.ToLocalTime().ToString("g", Loc.Culture) : Loc.Get("MEmptyLower"));
            var card = Border(
                    VStack(
                        Border(picture != null
                                ? Image().Source(picture).Aspect(Aspect.AspectFill)
                                : BoxView().Color(Color.FromArgb("#0E1116")))
                            .StrokeShape(new RoundRectangle().CornerRadius(12)).StrokeThickness(0).HeightRequest(64),
                        Label(Loc.Format("Slot", slot.Slot)).FontFamily(Fonts.InterSemiBold).FontSize(13)
                            .TextColor(slot.CanLoad ? Palette.Text : Palette.Faint).Margin(0, 8, 0, 0),
                        Label(line).FontSize(11).TextColor(slot.Problem != null ? Palette.Danger : Palette.Muted)
                            .MaxLines(2).LineBreakMode(LineBreakMode.TailTruncation)
                    ).Padding(8))
                .StrokeShape(new RoundRectangle().CornerRadius(18)).Stroke(Palette.Border).StrokeThickness(1)
                .BackgroundColor(Palette.Surface).Opacity(slot.CanLoad ? 1 : 0.6)
                .GridRow(i / 3).GridColumn(i % 3);
            if (slot.CanLoad)
            {
                int number = slot.Slot;
                card = card.OnTapped(() => GameActivity.Launch(MainActivity.Current!, g.TitleId, loadSlot: number));
            }

            cells.Add(card);
        }

        return Grid("Auto,Auto", "*,*,*", cells.ToArray()).ColumnSpacing(10).RowSpacing(10).Margin(20, 0, 20, 12);
    }

    /// <summary>The game's "Gravar tudo" recordings (newest first) with their date and size; a tap shares the zip.</summary>
    VisualNode RecordingsCard()
    {
        var recordings = State.Recordings;
        if (recordings.Count == 0)
        {
            return Card(Row(Loc.Get("MNoRecordingsYet")));
        }

        var rows = new List<VisualNode?>();
        foreach (GameRecording recording in recordings)
        {
            if (rows.Count > 0)
            {
                rows.Add(Divider());
            }

            string when = recording.ModifiedUtc.ToLocalTime().ToString("g", Loc.Culture);
            var row = Row(when, Loc.Get("MShare"), () => RecordingActions(recording),
                detail: Loc.Format("MRecordingDetail", recording.Name, recording.Bytes / (1024.0 * 1024.0)));
            // the recording that followed a deleted one
            rows.Add(recording.Path == State.FocusedRecording ? row.BackgroundColor(Palette.Highlight) : row);
        }

        return Card(rows.ToArray());
    }

    /// <summary>A recording's row: share it, or delete it (the list stays where it was, the next one highlighted).</summary>
    async void RecordingActions(GameRecording recording)
    {
        if (ContainerPage is not { } page)
        {
            return;
        }

        string? pick = await page.DisplayActionSheetAsync(recording.Name, Loc.Get("MCancel"), Loc.Get("MDelete"), Loc.Get("MShare"));
        if (pick == Loc.Get("MShare"))
        {
            ShareRecording(recording);
        }
        else if (pick == Loc.Get("MDelete"))
        {
            DeleteRecording(recording);
        }
    }

    async void DeleteRecording(GameRecording recording)
    {
        if (!await Confirm(Loc.Get("MDelete"), recording.Name, Loc.Get("MDelete")))
        {
            return;
        }

        await Run(Loc.Get("MDelete"), async () =>
        {
            await Task.Run(() => File.Delete(recording.Path));
            List<string> shown = State.Recordings.Select(r => r.Path).ToList();
            (List<GameRecording> remaining, string? focus) = ListRemoval.Remove(State.Recordings, r => r.Path, shown, [recording.Path]);
            SetState(s =>
            {
                s.Recordings = remaining;
                s.FocusedRecording = focus;
            });
        });
    }

    async void ShareRecording(GameRecording recording)
    {
        if (!File.Exists(recording.Path))
        {
            Load();
            return;
        }

        await Run(Loc.Get("MShare"), () => Share.Default.RequestAsync(new ShareFileRequest(recording.Name, new ShareFile(recording.Path, "application/zip"))));
    }

    async void Pick(string title, IReadOnlyList<Choice> choices, string globalValue, Action<string?> apply)
    {
        var v = await Choose(title, SettingsChoices.WithDefault(choices, globalValue));
        if (v == null)
            return;
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
        if (!await Confirm(Loc.Get("MDeleteSaves"), Loc.Format("MDeleteSavesConfirm", State.Game?.Title), Loc.Get("MDelete")))
            return;
        await Run(Loc.Get("MDeleteSaves"), async () =>
        {
            await Task.Run(() => Directory.Delete(AppServices.Data.SaveDir(Props.TitleId), true));
            Load();
        });
    }

    async void More()
    {
        if (State.Game is not { } g || ContainerPage is not { } page)
            return;
        var pick = await page.DisplayActionSheetAsync(g.Title, Loc.Get("MCancel"), Loc.Get("MUninstall"), Loc.Get("MTrophies"), Loc.Get("MCopyFolderPath"));
        switch (pick)
        {
            case var tr when tr == Loc.Get("MTrophies"):
                await (Navigation?.PushAsync<TrophiesPage, GameProps>(p => p.TitleId = g.TitleId) ?? Task.CompletedTask);
                break;
            case var c when c == Loc.Get("MCopyFolderPath"):
                await Clipboard.Default.SetTextAsync(g.AppDir);
                break;
            case var u when u == Loc.Get("MUninstall"):
                if (!await Confirm(Loc.Get("MUninstall"), Loc.Format("MUninstallConfirm", g.Title, g.TitleId), Loc.Get("MRemove")))
                    return;
                await Run(Loc.Get("MUninstall"), async () =>
                {
                    await Task.Run(() => Directory.Delete(g.AppDir, true));
                    // the library drops it in place and highlights the game that followed it
                    LibraryView.RaiseUninstalled(g.TitleId);
                    await (Navigation?.PopAsync() ?? Task.CompletedTask);
                });
                break;
        }
    }
}
