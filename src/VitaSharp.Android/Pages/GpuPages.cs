using MauiReactor;
using VitaSharp.Android.Gpu;
using VitaSharp.Android.Game;
using VitaSharp.Android.Services;
using VitaSharp.Android.Storage;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
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
        return Page("Driver da GPU",
            VStack(
                SectionTitle("Driver"),
                Card(options.SelectMany((o, i) => new VisualNode?[]
                {
                    i > 0 ? Divider() : null,
                    Row(o.Title, o.Selected ? "em uso" : null, () => Select(o), detail: o.Detail, valueColor: Palette.Accent),
                }).ToArray()),
                m.CustomDriversSupported
                    ? VStack(
                        PrimaryButton(State.Installing ? "Instalando…" : "Instalar driver (.zip)", Install, !State.Installing)
                    ).Padding(20, 4, 20, 8)
                    : Note("Drivers customizados só existem para GPUs Adreno (Qualcomm). Este aparelho usa o driver do sistema."),
                SectionTitle("Opções da libadrenotools"),
                Card(
                    ToggleRow("Habilitar texturas BCn", m.Settings.PatchBcn, v => Option(() => m.SetOptions(patchBcn: v)),
                        detail: "BCeNabler: drivers Adreno que escondem BC1-BC5"),
                    Divider(),
                    ToggleRow("Modo turbo da GPU", m.Settings.Turbo, v => Option(() => m.SetOptions(turbo: v)),
                        detail: "Clock máximo da GPU (mais consumo)"),
                    Divider(),
                    ToggleRow("Redirecionar arquivos do driver", m.Settings.FileRedirect, v => Option(() => m.SetOptions(fileRedirect: v)),
                        detail: "Para drivers que gravam no disco")
                ),
                Note("A troca vale a partir do próximo jogo: cada jogo roda num processo novo e um processo carrega um só driver. " +
                     "Os drivers precisam da libadrenotools empacotada no app (lib/arm64-v8a).")
            ), kicker: "Ajustes");
    }

    async void Select(GpuDriverOption o)
    {
        if (State.Model is not { } m) return;
        if (o.Removable && o.Selected)
        {
            if (await Confirm(o.Title, "Remover este driver? O driver do sistema volta a ser usado.", "Remover"))
                Option(() => m.Remove(o.Id));
            return;
        }
        Option(() => m.Select(o.Id));
    }

    void Option(Action change)
    {
        try { change(); }
        catch (Exception e) { _ = Alert("Driver da GPU", e.Message); return; }
        SetState(s => s.Revision++);
        _ = OfferRestartAsync();
    }

    /// <summary>A game running with the previous configuration only picks the new one up in a new process: offer to
    /// end it and start it again.</summary>
    async Task OfferRestartAsync()
    {
        if (State.Model is not { } m) return;
        var running = await Task.Run(() => GameProcessMarker.Read(AppServices.Data.GameProcessFile, GameProcessMarker.IsGameProcess));
        if (running == null || !m.RestartNeeded(running)) return;
        if (await Confirm("Reiniciar o jogo", $"{running.Title} está aberto com a configuração anterior da GPU. Reiniciar o jogo agora para usar a nova? " +
                          "O progresso que não foi salvo no jogo se perde.", "Reiniciar", "Depois"))
            await GameActivity.RestartAsync(MainActivity.Current!, running);
    }

    async void Install()
    {
        if (State.Model is not { } m) return;
        var doc = await Saf.PickAsync("application/zip", "application/octet-stream");
        if (doc == null) return;
        SetState(s => s.Installing = true);
        await Run("Instalar driver", async () =>
        {
            try
            {
                var installed = await Task.Run(() =>
                {
                    using var stream = AppServices.AppContext.ContentResolver!.OpenInputStream(doc.Uri)
                        ?? throw new IOException($"não foi possível abrir {doc.DisplayName}");
                    return m.Install(stream, (int)global::Android.OS.Build.VERSION.SdkInt);
                });
                if (await Confirm("Driver da GPU", $"Instalado: {installed.Meta.Name} {installed.Meta.DriverVersion}. Usar este driver agora?", "Usar", "Agora não"))
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
/// Informações da GPU (gpu-info.html): what the renderer finds on this device: GPU, driver, Vulkan version,
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
            return Page("Informações da GPU", Note($"Não foi possível abrir o Vulkan neste aparelho: {GpuProbe.Error}"), kicker: "Ajustes");
        var quirks = GpuInfoView.Quirks(caps);
        return Page("Informações da GPU",
            VStack(
                Card(GpuInfoView.Rows(caps).SelectMany((r, i) => new VisualNode?[] { i > 0 ? Divider() : null, Row(r.Label, r.Value) }).ToArray()),
                SectionTitle("Recursos"),
                Card(GpuInfoView.Features(caps).SelectMany((f, i) => new VisualNode?[]
                {
                    i > 0 ? Divider() : null,
                    Row(f.Name, f.Present ? f.Note ?? "sim" : "não", valueColor: f.Present ? Palette.Ok : Palette.Faint),
                }).ToArray()),
                SectionTitle("Contornos ativos"),
                Card(quirks.Where(q => q.Workaround).Select(q => Row(q.Quirk.ToString(), null, detail: q.Description)).DefaultIfEmpty(Row("Nenhum", null)).ToArray()),
                SectionTitle("Observados, sem contorno"),
                Card(quirks.Where(q => !q.Workaround).Select(q => Row(q.Quirk.ToString(), null, detail: q.Description)).DefaultIfEmpty(Row("Nenhum", null)).ToArray()),
                VStack(SecondaryButton("Compartilhar", () => _ = Share.Default.RequestAsync(new ShareTextRequest(caps.Describe(), "GPU do VitaSharp")))).Padding(20, 8)
            ), kicker: "Ajustes");
    }
}
