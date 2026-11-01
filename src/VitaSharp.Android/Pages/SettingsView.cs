using MauiReactor;
using VitaSharp.Android.Firmware;
using VitaSharp.Android.Services;
using VitaSharp.Android.Settings;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Util;

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
        return PageBody("Ajustes",
            VStack(
                SectionTitle("Sistema"),
                Card(
                    Row("Firmware", fw?.Summary ?? "…", () => Navigation?.PushAsync<FirmwarePage>(),
                        valueColor: fw?.Installed != null ? Palette.Ok : Palette.Muted),
                    Divider(),
                    Row("Sistema do Vita", fw is { Shell: true } ? "pronto" : "precisa do firmware", () =>
                    {
                        if (fw is { Shell: true }) Game.GameActivity.Launch(MainActivity.Current!, Game.GameActivity.SystemTitleId);
                        else Navigation?.PushAsync<FirmwarePage>();
                    }, detail: "O shell do firmware (vs0:vsh/shell) em tela cheia")
                ),
                SectionTitle("Desempenho"),
                Card(
                    Row("CPU", SettingsChoices.LabelOf(CpuChoices.Available(), p.CpuBackend),
                        () => Pick("CPU", CpuChoices.Available(), v => p.CpuBackend = v), detail: "Padrão de todos os jogos"),
                    Divider(),
                    Row("Clock da CPU", SettingsChoices.LabelOf(SettingsChoices.Clocks, p.CpuMhz.ToString()),
                        () => Pick("Clock da CPU", SettingsChoices.Clocks, v => p.CpuMhz = int.Parse(v))),
                    Divider(),
                    Row("Perfil da GPU", SettingsChoices.LabelOf(SettingsChoices.GpuProfiles, p.GpuProfile),
                        () => Pick("Perfil da GPU", SettingsChoices.GpuProfiles, v => p.GpuProfile = v),
                        detail: "Contornos de driver (docs/notes/mobile-gpu.md)"),
                    Divider(),
                    ToggleRow("VSync", p.VSync, v => Change(x => x.VSync = v)),
                    Divider(),
                    ToggleRow("Mostrar FPS", p.ShowFps, v => Change(x => x.ShowFps = v)),
                    Divider(),
                    ToggleRow("Som de troféu", p.TrophySound, v => Change(x => x.TrophySound = v),
                        detail: "Som de aviso do firmware ao desbloquear um troféu (ou um som próprio sem firmware)"),
                    Divider(),
                    Row("Driver da GPU", null, () => Navigation?.PushAsync<GpuDriverPage>()),
                    Divider(),
                    Row("Informações da GPU", null, () => Navigation?.PushAsync<GpuInfoPage>())
                ),
                SectionTitle("Rede e PSN"),
                Card(
                    Row("Conta PSN e rede", p.Network.PsnSignedIn ? p.Network.PsnOnlineId : "desconectada",
                        () => Navigation?.PushAsync<PsnPage>(),
                        detail: p.Network.RealInternet ? "Internet real ligada" : "Nada sai do aparelho")
                ),
                SectionTitle("Armazenamento"),
                Card(
                    Row("Pasta de dados", null, Copy(AppServices.Data.Root), detail: AppServices.Data.Root),
                    Divider(),
                    Row("Jogos instalados", null, Copy(AppServices.Data.Apps), detail: "fs/ux0/app · os saves ficam em fs/ux0/user/00/savedata")
                ),
                Note("Os jogos, saves, firmware e logs ficam na pasta do app (armazenamento específico do app): nenhuma " +
                     "permissão de armazenamento é pedida. Arquivos .VPK e .PUP são abertos pelo seletor do sistema e lidos no lugar."),
                SectionTitle("Depuração"),
                Card(
                    ToggleRow("Modo de depuração", p.DebugMode, v => Change(x => x.DebugMode = v),
                        detail: "Log detalhado no próximo jogo (logcat: VitaSharp, VitaSharp-Guest)"),
                    Divider(),
                    Row("Log do último jogo", null, Copy(System.IO.Path.Combine(AppServices.Data.Logs, "last.log")), detail: "logs/last.log"),
                    Divider(),
                    Row("Compartilhar log", null, ShareLog)
#if VITASHARP_DEVELOPER
                    , Divider(),
                    Row("Opções de desenvolvedor", null, () => Navigation?.PushAsync<DeveloperPage>(), detail: "Bench da GPU (build de desenvolvedor)")
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
        await Alert("Copiado", text);
    };

    async void ShareLog()
    {
        string path = System.IO.Path.Combine(AppServices.Data.Logs, "last.log");
        if (!File.Exists(path)) path = System.IO.Path.Combine(AppServices.Data.Logs, "app.log");
        if (!File.Exists(path)) { await Alert("Log", "Ainda não há log."); return; }
        await Share.Default.RequestAsync(new ShareFileRequest("Log do VitaSharp", new ShareFile(path, "text/plain")));
    }
}
