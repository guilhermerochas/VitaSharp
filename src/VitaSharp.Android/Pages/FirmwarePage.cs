using MauiReactor;
using VitaSharp.Android.Firmware;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Firmware;

namespace VitaSharp.Android.Pages;

public sealed class FirmwareState
{
    public FirmwareStatus? Status { get; set; }
    public string? Installing { get; set; }
    public FirmwareProgress Progress { get; set; }
}

/// <summary>
/// Firmware (firmware.html): the installed version and source, each partition (os0, vs0 with the shell, pd0, the sa0
/// font package), the install in progress with "Cancelar", and the two install buttons, which open the Storage Access
/// Framework picker. The PUP is read in place (FirmwareSetup); VitaSharp ships no firmware.
/// </summary>
public sealed class FirmwarePage : Screen<FirmwareState>
{
    CancellationTokenSource? _cts;

    protected override void OnMounted()
    {
        Refresh();
        base.OnMounted();
    }

    void Refresh() => _ = Task.Run(() =>
    {
        var st = FirmwareStatus.Read(AppServices.Data.Fs);
        SetState(s => s.Status = st);
    });

    public override VisualNode Render()
    {
        var st = State.Status;
        var fw = st?.Installed;
        return Page("Firmware",
            VStack(
                Card(
                    Row("Versão", fw?.Version ?? "não instalado", valueColor: fw != null ? Palette.Ok : Palette.Muted),
                    fw != null ? Divider() : null,
                    fw != null ? Row("Origem", fw.Source) : null,
                    fw?.InstalledUtc != null ? Divider() : null,
                    fw?.InstalledUtc is { } w ? Row("Instalado em", w.ToLocalTime().ToString("dd/MM/yyyy HH:mm")) : null
                ).Margin(20, 8, 20, 12),
                SectionTitle("Partições"),
                Card(
                    Row("os0", Has("os0"), detail: "kernel e módulos do sistema"),
                    Divider(),
                    Row("vs0", st?.Shell == true ? "com o shell" : Has("vs0"), detail: "bibliotecas e o shell (vsh/shell/shell.self)"),
                    Divider(),
                    Row("pd0", st?.Pd0 == true ? "instalado" : "ausente", detail: "conteúdo de boas-vindas e sons do sistema"),
                    Divider(),
                    Row("sa0 (fontes)", st?.SystemFonts == true ? "instalado" : "ausente", detail: "pacote de fontes do sistema")
                ),
                State.Installing != null ? InstallProgress() : null,
                VStack(
                    PrimaryButton("Instalar firmware (.PUP)", () => Install("PSVUPDAT.PUP"), State.Installing == null),
                    SecondaryButton("Instalar pacote de fontes (.PUP)", () => Install("PSP2UPDAT fontes"), State.Installing == null)
                ).Spacing(10).Padding(20, 8, 20, 0),
                Note("O arquivo .PUP é escolhido pelo seletor do sistema e lido no lugar, sem cópia. O VitaSharp não inclui " +
                     "nenhum firmware: use a atualização oficial do PS Vita (PSVUPDAT.PUP).")
            ), kicker: "Ajustes");
    }

    string Has(string partition) =>
        State.Status?.Installed?.Partitions.Contains(partition) == true ? "instalado" : "ausente";

    VisualNode InstallProgress() =>
        Card(
            VStack(
                Label($"Instalando {State.Installing}").FontFamily(Fonts.InterSemiBold).FontSize(14),
                Label(State.Progress.Stage + (State.Progress.Detail is { } d ? $" · {d}" : "")).FontSize(12).TextColor(Palette.Muted)
                    .LineBreakMode(LineBreakMode.TailTruncation),
                ProgressBar().Progress(State.Progress.Fraction),
                SecondaryButton("Cancelar", () => _cts?.Cancel())
            ).Spacing(8).Padding(16)
        ).Margin(20, 12, 20, 0);

    async void Install(string what)
    {
        var doc = await Saf.PickAsync(FirmwareSetup.PickerMimeType);
        if (doc == null) return;
        _cts = new CancellationTokenSource();
        SetState(s => { s.Installing = doc.DisplayName; s.Progress = new("Abrindo", 0); });
        DeviceDisplay.Current.KeepScreenOn = true;
        await Run($"Instalar {what}", async () =>
        {
            try
            {
                string cache = System.IO.Path.Combine(AppServices.AppContext.CacheDir!.AbsolutePath, "imports");
                var progress = new Progress<FirmwareProgress>(p => SetState(s => s.Progress = p));
                var info = await FirmwareSetup.InstallAsync(() => doc.OpenSeekable(cache), doc.DisplayName, AppServices.Data.Fs, progress, _cts.Token);
                await Alert("Firmware", $"Instalado: {info.Display}");
            }
            catch (OperationCanceledException)
            {
                await Alert("Firmware", "Instalação cancelada. O firmware anterior continua instalado.");
            }
            finally
            {
                DeviceDisplay.Current.KeepScreenOn = false;
                SetState(s => s.Installing = null);
                Refresh();
            }
        });
    }
}
