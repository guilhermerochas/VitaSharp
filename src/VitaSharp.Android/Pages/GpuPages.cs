using VitaSharp.Core.Localization;
using MauiReactor;
using VitaSharp.Android.Game;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Gpu;
using VitaSharp.Mobile.Core.Storage;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Android.Pages;

/// <summary>
/// The GPU as the renderer sees it, probed once per UI process: a VkCore without a surface (instance, device, the
/// capability and quirk selection of docs/notes/mobile-gpu.md), destroyed right away. The game process probes again
/// with the driver it really loads.
/// </summary>
static class GpuProbe
{
    static Task<GpuCapabilities?>? _probe;
    public static string? Error { get; private set; }

    public static Task<GpuCapabilities?> Get() => _probe ??= Task.Run(() =>
    {
        try
        {
            using var core = new VkCore(null, false, AppServices.Prefs.GpuProfile.Length > 0 ? AppServices.Prefs.GpuProfile : null);
            return (GpuCapabilities?)core.Caps;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Warn($"GPU probe: {e}");
            return null;
        }
    });
}

public sealed class GpuDriverState
{
    public GpuDriverScreen? Model { get; set; }
    public int Revision { get; set; }
    public bool Installing { get; set; }
}

/// <summary>
/// Driver da GPU (driver-gpu.html): the system driver or an installed Turnip/Adreno package (zip with meta.json) loaded
/// through libadrenotools, "Instalar driver (.zip)", and the BCeNabler, turbo and file-redirect options. A change applies
/// to the next start of a game (each game runs in a fresh process, and a process loads one driver): when a game is
/// running with another configuration (GameProcessMarker) the screen offers to restart it. Custom drivers are offered on
/// Adreno GPUs only.
/// </summary>
public sealed class GpuDriverPage : Screen<GpuDriverState>
{
    protected override async void OnMounted()
    {
        base.OnMounted();
        var caps = await GpuProbe.Get();
        var model = await Task.Run(() => new GpuDriverScreen(AppServices.Data, caps));
        SetState(s => s.Model = model);
    }

    public override VisualNode Render()
    {
        if (State.Model is not { } m)
            return ContentPage(ActivityIndicator().IsRunning(true).Center()).HasNavigationBar(false).BackgroundColor(Palette.Background);
        var options = m.Options();
        return Page(Loc.Get("MGpuDriver"),
            VStack(
                SectionTitle(Loc.Get("MDriver")),
                Card(options.SelectMany((o, i) => new VisualNode?[]
                {
                    i > 0 ? Divider() : null,
                    Row(o.Title, o.Selected ? Loc.Get("MInUse") : null, () => Select(o), detail: o.Detail, valueColor: Palette.Accent),
                }).ToArray()),
                m.CustomDriversSupported
                    ? VStack(
                        PrimaryButton(State.Installing ? Loc.Get("MInstallingEllipsis") : Loc.Get("MInstallDriverZip"), Install, !State.Installing)
                    ).Padding(20, 4, 20, 8)
                    : Note(Loc.Get("MCustomDriversAdrenoOnly")),
                SectionTitle(Loc.Get("MAdrenoToolsOptions")),
                Card(
                    ToggleRow(Loc.Get("MEnableBcn"), m.Settings.PatchBcn, v => Option(() => m.SetOptions(patchBcn: v)),
                        detail: Loc.Get("MEnableBcnDetail")),
                    Divider(),
                    ToggleRow(Loc.Get("MGpuTurbo"), m.Settings.Turbo, v => Option(() => m.SetOptions(turbo: v)),
                        detail: Loc.Get("MGpuTurboDetail")),
                    Divider(),
                    ToggleRow(Loc.Get("MFileRedirect"), m.Settings.FileRedirect, v => Option(() => m.SetOptions(fileRedirect: v)),
                        detail: Loc.Get("MFileRedirectDetail"))
                ),
                Note(Loc.Get("MDriverNote"))
            ), kicker: Loc.Get("MSettings"));
    }

    async void Select(GpuDriverOption o)
    {
        if (State.Model is not { } m)
            return;
        if (o.Removable && o.Selected)
        {
            if (await Confirm(o.Title, Loc.Get("MRemoveDriverConfirm"), Loc.Get("MRemove")))
                Option(() => m.Remove(o.Id));
            return;
        }
        Option(() => m.Select(o.Id));
    }

    void Option(Action change)
    {
        try
        {
            change();
        }
        catch (Exception e)
        {
            _ = Alert(Loc.Get("MGpuDriver"), e.Message);
            return;
        }
        SetState(s => s.Revision++);
        _ = OfferRestartAsync();
    }

    /// <summary>A game running with the previous configuration only picks the new one up in a new process: offer to
    /// end it and start it again.</summary>
    async Task OfferRestartAsync()
    {
        if (State.Model is not { } m)
            return;
        var running = await Task.Run(() => GameProcessMarker.Read(AppServices.Data.GameProcessFile, GameProcessMarker.IsGameProcess));
        if (running == null || !m.RestartNeeded(running))
            return;
        if (await Confirm(Loc.Get("MRestartGame"), Loc.Format("MRestartGameConfirm", running.Title), Loc.Get("MRestart"), Loc.Get("MLater")))
            await GameActivity.RestartAsync(MainActivity.Current!, running);
    }

    async void Install()
    {
        if (State.Model is not { } m)
            return;
        var doc = await Saf.PickAsync("application/zip", "application/octet-stream");
        if (doc == null)
            return;
        SetState(s => s.Installing = true);
        await Run(Loc.Get("MInstallDriver"), async () =>
        {
            try
            {
                var installed = await Task.Run(() =>
                {
                    using var stream = AppServices.AppContext.ContentResolver!.OpenInputStream(doc.Uri)
                        ?? throw new IOException(Loc.Format("MCannotOpen", doc.DisplayName));
                    return m.Install(stream, (int)global::Android.OS.Build.VERSION.SdkInt);
                });
                if (await Confirm(Loc.Get("MGpuDriver"), Loc.Format("MDriverInstalledUse", installed.Meta.Name, installed.Meta.DriverVersion), Loc.Get("MUse"), Loc.Get("MNotNow")))
                    Option(() => m.Select(installed.Id));
            }
            finally { SetState(s => { s.Installing = false; s.Revision++; }); }
        });
    }
}

public sealed class GpuInfoState
{
    public GpuCapabilities? Caps { get; set; }
    public bool Loaded { get; set; }
}

/// <summary>
/// Informacoes da GPU (gpu-info.html): what the renderer finds on this device: GPU, driver, Vulkan version,
/// tile-based / unified memory, the features it uses, the active quirk workarounds and the quirks that change nothing.
/// "Compartilhar" sends the same block as the startup log / info.txt (GpuCapabilities.Describe).
/// </summary>
public sealed class GpuInfoPage : Screen<GpuInfoState>
{
    protected override async void OnMounted()
    {
        base.OnMounted();
        var caps = await GpuProbe.Get();
        SetState(s => { s.Caps = caps; s.Loaded = true; });
    }

    public override VisualNode Render()
    {
        if (!State.Loaded)
            return ContentPage(ActivityIndicator().IsRunning(true).Center()).HasNavigationBar(false).BackgroundColor(Palette.Background);
        if (State.Caps is not { } caps)
            return Page(Loc.Get("MGpuInfo"), Note(Loc.Format("MVulkanFailed", GpuProbe.Error)), kicker: Loc.Get("MSettings"));
        var quirks = GpuInfoView.Quirks(caps);
        return Page(Loc.Get("MGpuInfo"),
            VStack(
                Card(GpuInfoView.Rows(caps).SelectMany((r, i) => new VisualNode?[] { i > 0 ? Divider() : null, Row(r.Label, r.Value) }).ToArray()),
                SectionTitle(Loc.Get("MFeatures")),
                Card(GpuInfoView.Features(caps).SelectMany((f, i) => new VisualNode?[]
                {
                    i > 0 ? Divider() : null,
                    Row(f.Name, f.Present ? f.Note ?? Loc.Get("MYesLower") : Loc.Get("MNoLower"), valueColor: f.Present ? Palette.Ok : Palette.Faint),
                }).ToArray()),
                SectionTitle(Loc.Get("MActiveWorkarounds")),
                Card(quirks.Where(q => q.Workaround).Select(q => Row(q.Quirk.ToString(), null, detail: q.Description)).DefaultIfEmpty(Row(Loc.Get("MNone"), null)).ToArray()),
                SectionTitle(Loc.Get("MObservedNoWorkaround")),
                Card(quirks.Where(q => !q.Workaround).Select(q => Row(q.Quirk.ToString(), null, detail: q.Description)).DefaultIfEmpty(Row(Loc.Get("MNone"), null)).ToArray()),
                VStack(SecondaryButton(Loc.Get("MShare"), () => _ = Share.Default.RequestAsync(new ShareTextRequest(caps.Describe(), Loc.Get("MVitaSharpGpu"))))).Padding(20, 8)
            ), kicker: Loc.Get("MSettings"));
    }
}
