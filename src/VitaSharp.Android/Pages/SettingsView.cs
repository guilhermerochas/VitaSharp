using VitaSharp.Core.Localization;
using MauiReactor;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Firmware;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Android.Pages;

public sealed class SettingsState
{
    public FirmwareStatus? Firmware { get; set; }
    public int Revision { get; set; }
}

/// <summary>
/// Ajustes (ajustes.html): Sistema (firmware status, Sistema do Vita), Desempenho (the default CPU backend, clock and
/// GPU profile of every game, VSync, FPS, GPU driver and GPU information), Rede e PSN, Armazenamento (where the data
/// folder is) and Depuração (debug logging, the log files).
/// </summary>
public sealed class SettingsView : Screen<SettingsState>
{
    protected override void OnMounted()
    {
        MainActivity.Resumed += Refresh;
        Refresh();
        base.OnMounted();
    }

    protected override void OnWillUnmount()
    {
        MainActivity.Resumed -= Refresh;
        base.OnWillUnmount();
    }

    void Refresh() => _ = Task.Run(() =>
    {
        var fw = FirmwareStatus.Read(AppServices.Data.Fs);
        SetState(s => s.Firmware = fw);
    });

    public override VisualNode Render()
    {
        var p = AppServices.Prefs;
        var fw = State.Firmware;
        return PageBody(Loc.Get("MSettings"),
            VStack(
                SectionTitle(Loc.Get("MGames")),
                Card(
                    Row(Loc.Get("MInstallGame"), null, () =>
                    {
                        LibraryView.InstallRequested = true;
                        RootPage.Show(RootTab.Library);
                    }, detail: Loc.Get("MInstallGameDetail"))
                ),
                SectionTitle(Loc.Get("MSystem")),
                Card(
                    Row(Loc.Get("MFirmware"), fw?.Summary ?? "…", () => Navigation?.PushAsync<FirmwarePage>(),
                        valueColor: fw?.Installed != null ? Palette.Ok : Palette.Muted),
                    Divider(),
                    Row(Loc.Get("MVitaSystem"), fw is { Shell: true } ? Loc.Get("MReady") : Loc.Get("MNeedsFirmware"), () =>
                    {
                        if (fw is { Shell: true }) Game.GameActivity.Launch(MainActivity.Current!, Game.GameActivity.SystemTitleId);
                        else Navigation?.PushAsync<FirmwarePage>();
                    }, detail: Loc.Get("MShellDetail"))
                ),
                SectionTitle(Loc.Get("MPerformance")),
                Card(
                    Row("CPU", SettingsChoices.LabelOf(CpuChoices.Available(), p.CpuBackend),
                        () => Pick("CPU", CpuChoices.Available(), v => p.CpuBackend = v), detail: Loc.Get("MDefaultAllGames")),
                    Divider(),
                    Row(Loc.Get("MCpuClock"), SettingsChoices.LabelOf(SettingsChoices.Clocks, p.CpuMhz.ToString()),
                        () => Pick(Loc.Get("MCpuClock"), SettingsChoices.Clocks, v => p.CpuMhz = int.Parse(v))),
                    Divider(),
                    Row(Loc.Get("MGpuProfile"), SettingsChoices.LabelOf(SettingsChoices.GpuProfiles, p.GpuProfile),
                        () => Pick(Loc.Get("MGpuProfile"), SettingsChoices.GpuProfiles, v => p.GpuProfile = v),
                        detail: Loc.Get("MGpuProfileDetail")),
                    Divider(),
                    ToggleRow("VSync", p.VSync, v => Change(x => x.VSync = v)),
                    Divider(),
                    ToggleRow(Loc.Get("MShowFps"), p.ShowFps, v => Change(x => x.ShowFps = v)),
                    Divider(),
                    ToggleRow(Loc.Get("MTrophySound"), p.TrophySound, v => Change(x => x.TrophySound = v),
                        detail: Loc.Get("MTrophySoundDetail")),
                    Divider(),
                    Row(Loc.Get("MGpuDriver"), null, () => Navigation?.PushAsync<GpuDriverPage>()),
                    Divider(),
                    Row(Loc.Get("MGpuInfo"), null, () => Navigation?.PushAsync<GpuInfoPage>())
                ),
                SectionTitle(Loc.Get("MNetworkPsn")),
                Card(
                    Row(Loc.Get("MPsnAndNetwork"), p.Network.PsnSignedIn ? p.Network.PsnOnlineId : Loc.Get("MSignedOut"),
                        () => Navigation?.PushAsync<PsnPage>(),
                        detail: p.Network.RealInternet ? Loc.Get("MRealInternetOn") : Loc.Get("MNothingLeaves"))
                ),
                SectionTitle(Loc.Get("MStorage")),
                Card(
                    Row(Loc.Get("MDataFolder"), null, Copy(AppServices.Data.Root), detail: AppServices.Data.Root),
                    Divider(),
                    Row(Loc.Get("MInstalledGames"), null, Copy(AppServices.Data.Apps), detail: Loc.Get("MInstalledGamesDetail"))
                ),
                Note(Loc.Get("MStorageNote")),
                SectionTitle(Loc.Get("MDebugging")),
                Card(
                    ToggleRow(Loc.Get("MDebugMode"), p.DebugMode, v => Change(x => x.DebugMode = v),
                        detail: Loc.Get("MDebugModeDetail")),
                    Divider(),
                    Row(Loc.Get("MLastGameLog"), null, Copy(System.IO.Path.Combine(AppServices.Data.Logs, "last.log")), detail: "logs/last.log"),
                    Divider(),
                    Row(Loc.Get("MShareLog"), null, ShareLog)
#if VITASHARP_DEVELOPER
                    , Divider(),
                    Row(Loc.Get("MDeveloperOptions"), null, () => Navigation?.PushAsync<DeveloperPage>(), detail: Loc.Get("MDeveloperOptionsDetail"))
#endif
                ),

                Note($"VitaSharp · {RuntimeReport.Runtime}")
            ));
    }

    async void Pick(string title, IReadOnlyList<Choice> choices, Action<string> apply)
    {
        var v = await Choose(title, choices);
        if (v != null) Change(_ => apply(v));
    }

    void Change(Action<AppPreferences> change)
    {
        change(AppServices.Prefs);
        AppServices.SavePrefs();
        if (AppServices.Prefs.DebugMode) { Log.Level = LogLevel.Debug; DebugMode.Enable(); }
        else { Log.Level = LogLevel.Info; DebugMode.Disable(); }
        SetState(s => s.Revision++);
    }

    Action Copy(string text) => async () =>
    {
        await Clipboard.Default.SetTextAsync(text);
        await Alert(Loc.Get("MCopied"), text);
    };

    async void ShareLog()
    {
        string path = System.IO.Path.Combine(AppServices.Data.Logs, "last.log");
        if (!File.Exists(path)) path = System.IO.Path.Combine(AppServices.Data.Logs, "app.log");
        if (!File.Exists(path)) { await Alert("Log", Loc.Get("MNoLogYet")); return; }
        await Share.Default.RequestAsync(new ShareFileRequest(Loc.Get("MVitaSharpLog"), new ShareFile(path, "text/plain")));
    }
}
