using VitaSharp.Core.Localization;
using MauiReactor;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Firmware;
using VitaSharp.Mobile.Core.Firmware;

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
        return Page(Loc.Get("MFirmware"),
            VStack(
                Card(
                    Row(Loc.Get("MVersion"), fw?.Version ?? Loc.Get("MNotInstalled"), valueColor: fw != null ? Palette.Ok : Palette.Muted),
                    fw != null ? Divider() : null,
                    fw != null ? Row(Loc.Get("MSource"), fw.Source) : null,
                    fw?.InstalledUtc != null ? Divider() : null,
                    fw?.InstalledUtc is { } w ? Row(Loc.Get("MInstalledOn"), w.ToLocalTime().ToString("dd/MM/yyyy HH:mm")) : null
                ).Margin(20, 8, 20, 12),
                SectionTitle(Loc.Get("MPartitions")),
                Card(
                    Row("os0", Has("os0"), detail: Loc.Get("MOs0Detail")),
                    Divider(),
                    Row("vs0", st?.Shell == true ? Loc.Get("MWithShell") : Has("vs0"), detail: Loc.Get("MVs0Detail")),
                    Divider(),
                    Row("pd0", st?.Pd0 == true ? Loc.Get("MInstalled") : Loc.Get("MMissing"), detail: Loc.Get("MPd0Detail")),
                    Divider(),
                    Row(Loc.Get("MSa0Fonts"), st?.SystemFonts == true ? Loc.Get("MInstalled") : Loc.Get("MMissing"), detail: Loc.Get("MSa0Detail"))
                ),
                State.Installing != null ? InstallProgress() : null,
                VStack(
                    PrimaryButton(Loc.Get("MInstallFirmware"), () => Install("PSVUPDAT.PUP"), State.Installing == null),
                    SecondaryButton(Loc.Get("MInstallFonts"), () => Install("PSP2UPDAT fontes"), State.Installing == null)
                ).Spacing(10).Padding(20, 8, 20, 0),
                Note(Loc.Get("MFirmwareNote"))
            ), kicker: Loc.Get("MSettings"));
    }

    string Has(string partition) =>
        State.Status?.Installed?.Partitions.Contains(partition) == true ? Loc.Get("MInstalled") : Loc.Get("MMissing");

    VisualNode InstallProgress() =>
        Card(
            VStack(
                Label(Loc.Format("MInstalling", State.Installing)).FontFamily(Fonts.InterSemiBold).FontSize(14),
                Label(State.Progress.Stage + (State.Progress.Detail is { } d ? $" \u00B7 {d}" : "")).FontSize(12).TextColor(Palette.Muted)
                    .LineBreakMode(LineBreakMode.TailTruncation),
                ProgressBar().Progress(State.Progress.Fraction),
                SecondaryButton(Loc.Get("MCancel"), () => _cts?.Cancel())
            ).Spacing(8).Padding(16)
        ).Margin(20, 12, 20, 0);

    async void Install(string what)
    {
        var doc = await Saf.PickAsync(FirmwareSetup.PickerMimeType);
        if (doc == null) return;
        _cts = new CancellationTokenSource();
        SetState(s => { s.Installing = doc.DisplayName; s.Progress = new(Loc.Get("MOpening"), 0); });
        DeviceDisplay.Current.KeepScreenOn = true;
        await Run(Loc.Format("MInstallWhat", what), async () =>
        {
            try
            {
                string cache = System.IO.Path.Combine(AppServices.AppContext.CacheDir!.AbsolutePath, "imports");
                var progress = new Progress<FirmwareProgress>(p => SetState(s => s.Progress = p));
                var info = await FirmwareSetup.InstallAsync(() => doc.OpenSeekable(cache), doc.DisplayName, AppServices.Data.Fs, progress, _cts.Token);
                await Alert(Loc.Get("MFirmware"), Loc.Format("MInstalledWhat", info.Display));
            }
            catch (OperationCanceledException)
            {
                await Alert(Loc.Get("MFirmware"), Loc.Get("MFirmwareCanceled"));
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
