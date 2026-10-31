using Android.Views;
using MauiReactor;
using VitaSharp.Android.Input;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Input;

namespace VitaSharp.Android.Pages;

public sealed class ControlsState
{
    public List<(string Id, string Name, ControllerKind Kind)> Controllers { get; set; } = [];
    public int Selected { get; set; }
    public int Revision { get; set; }
}

/// <summary>
/// Controles (controles.html): the connected controllers, the mapping of the selected one (the core's controller
/// profile, shared with the desktop: positional A = Cross, triggers also L/R, PS on the Home/Mode button), the O/X
/// confirm swap and the stick deadzone, plus the on-screen controls (opacity, vibration, hidden while a controller is
/// connected). Profiles are stored in &lt;data&gt;/config/input.json.
/// </summary>
public sealed class ControlsView : Screen<ControlsState>
{
    protected override void OnMounted()
    {
        base.OnMounted();
        Scan();
    }

    void Scan()
    {
        var list = new List<(string, string, ControllerKind)>();
        foreach (int id in InputDevice.GetDeviceIds() ?? [])
        {
            var d = InputDevice.GetDevice(id);
            if (d == null || d.IsVirtual) continue;
            bool pad = (d.Sources & InputSourceType.Gamepad) == InputSourceType.Gamepad || (d.Sources & InputSourceType.Joystick) == InputSourceType.Joystick;
            if (!pad) continue;
            string guid = AndroidGamepads.Guid(d.VendorId, d.ProductId);
            list.Add((guid, d.Name ?? "Controle", ControllerClassifier.Classify(guid, d.Name)));
        }
        SetState(s => { s.Controllers = list; s.Selected = Math.Min(s.Selected, Math.Max(0, list.Count - 1)); });
    }

    ControllerProfile? Profile()
    {
        if (State.Controllers.Count == 0) return null;
        var (id, name, kind) = State.Controllers[State.Selected];
        return AppServices.InputProfiles.ProfileFor(id, name, kind, isGamepad: true);
    }

    public override VisualNode Render()
    {
        var prefs = AppServices.Prefs;
        var profile = Profile();
        var kind = State.Controllers.Count > 0 ? State.Controllers[State.Selected].Kind : ControllerKind.Generic;
        return PageBody("Controles",
            VStack(
                SectionTitle("Controle conectado", State.Controllers.Count > 0 ? $"{State.Controllers.Count}" : null),
                Card(State.Controllers.Count == 0
                    ? [Row("Nenhum controle", "Atualizar", Scan, detail: "Conecte um controle Bluetooth ou USB")]
                    : State.Controllers.SelectMany((c, i) => new VisualNode?[]
                    {
                        i > 0 ? Divider() : null,
                        Row(c.Name, i == State.Selected ? "selecionado" : null, () => SetState(s => s.Selected = i),
                            detail: KindName(c.Kind), valueColor: Palette.Accent),
                    }).ToArray()),
                profile != null ? SectionTitle("Mapeamento") : null,
                profile != null ? Card(Mapping(profile, kind)) : null,
                profile != null ? Card(
                    ToggleRow("Trocar ○ e ✕", profile.SwapConfirm, v => SaveProfile(p => p.SwapConfirm = v),
                        detail: "Confirmar com ○, como nos jogos japoneses"),
                    Divider(),
                    Row("Zona morta dos analógicos", $"{profile.LeftStick.Deadzone * 100:0}%", () => Deadzone(profile))
                ) : null,
                SectionTitle("Controles na tela"),
                Card(
                    Row("Opacidade", $"{prefs.OnScreenOpacity * 100:0}%", Opacity),
                    Divider(),
                    ToggleRow("Vibrar ao tocar", prefs.Haptics, v => { prefs.Haptics = v; AppServices.SavePrefs(); SetState(s => s.Revision++); }),
                    Divider(),
                    ToggleRow("Esconder com controle conectado", prefs.HideOnScreenWithGamepad,
                        v => { prefs.HideOnScreenWithGamepad = v; AppServices.SavePrefs(); SetState(s => s.Revision++); })
                ),
                SectionTitle("Movimento"),
                Card(Row("Sensores de movimento", AndroidMotionModes.Label(AndroidMotionModes.Resolve(prefs.Motion, PhoneHasMotionSensors())), ChooseMotion,
                    detail: "Inclinação e giroscópio do Vita (SceMotion); Recentralizar fica no menu de pausa")),
                Note("No jogo: TRÁS liga o toque traseiro (os toques na imagem vão para o painel de trás); PS abre o menu de pausa " +
                     "quando o jogo roda sem o sistema. O botão Home/PS do controle faz o mesmo.")
            ));
    }

    static string KindName(ControllerKind k) => k switch
    {
        ControllerKind.PlayStation => "PlayStation",
        ControllerKind.Xbox => "Xbox",
        ControllerKind.Nintendo => "Nintendo",
        _ => "Genérico",
    };

    VisualNode?[] Mapping(ControllerProfile p, ControllerKind kind)
    {
        VitaControl[] shown =
        [
            VitaControl.Cross, VitaControl.Circle, VitaControl.Square, VitaControl.Triangle, VitaControl.L, VitaControl.R,
            VitaControl.Start, VitaControl.Select, VitaControl.PS, VitaControl.Up, VitaControl.LStickUp, VitaControl.RStickUp,
        ];
        return shown.SelectMany((c, i) => new VisualNode?[]
        {
            i > 0 ? Divider() : null,
            Row(ControlName(c), string.Join(" / ", p.Get(c).Select(b => b.Describe(kind))) is { Length: > 0 } s ? s : "—"),
        }).ToArray();
    }

    static string ControlName(VitaControl c) => c switch
    {
        VitaControl.Cross => "✕", VitaControl.Circle => "○", VitaControl.Square => "□", VitaControl.Triangle => "△",
        VitaControl.PS => "Botão PS", VitaControl.Up => "Direcional", VitaControl.LStickUp => "Analógico esquerdo",
        VitaControl.RStickUp => "Analógico direito", VitaControl.Start => "START", VitaControl.Select => "SELECT",
        _ => c.ToString(),
    };

    void SaveProfile(Action<ControllerProfile> change)
    {
        if (Profile() is not { } p) return;
        change(p);
        AppServices.InputProfiles.Put(p);
        AppServices.InputProfiles.Save();
        SetState(s => s.Revision++);
    }

    async void Deadzone(ControllerProfile p)
    {
        var choices = new[] { 0, 5, 10, 15, 20, 25 }.Select(v => new Settings.Choice(v.ToString(), $"{v}%")).ToList();
        var v = await Choose("Zona morta", choices);
        if (v == null) return;
        float dz = int.Parse(v) / 100f;
        SaveProfile(x => { x.LeftStick.Deadzone = dz; x.RightStick.Deadzone = dz; });
    }

    static bool PhoneHasMotionSensors()
    {
        var sm = (global::Android.Hardware.SensorManager?)AppServices.AppContext.GetSystemService(global::Android.Content.Context.SensorService);
        return sm?.GetDefaultSensor(global::Android.Hardware.SensorType.Accelerometer) != null
            && sm.GetDefaultSensor(global::Android.Hardware.SensorType.Gyroscope) != null;
    }

    /// <summary>"Movimento": the phone's sensors (the default when it has them), a controller's, or off (the desktop's
    /// motion source setting; the hardware keyboard's tilt keys take over while no sensor device feeds).</summary>
    async void ChooseMotion()
    {
        bool phone = PhoneHasMotionSensors();
        var choices = new List<Settings.Choice>
        {
            new(AndroidMotionModes.Phone, AndroidMotionModes.Label(AndroidMotionMode.Phone) + (phone ? "" : " (este aparelho não tem)")),
            new(AndroidMotionModes.Controller, AndroidMotionModes.Label(AndroidMotionMode.Controller) + " (Android 12+)"),
            new(AndroidMotionModes.Off, AndroidMotionModes.Label(AndroidMotionMode.Off)),
        };
        var v = await Choose("Movimento", choices);
        if (v == null) return;
        AppServices.Prefs.Motion = v;
        AppServices.SavePrefs();
        SetState(s => s.Revision++);
    }

    async void Opacity()
    {
        var choices = new[] { 20, 35, 55, 75, 100 }.Select(v => new Settings.Choice(v.ToString(), $"{v}%")).ToList();
        var v = await Choose("Opacidade dos controles", choices);
        if (v == null) return;
        AppServices.Prefs.OnScreenOpacity = int.Parse(v) / 100f;
        AppServices.SavePrefs();
        SetState(s => s.Revision++);
    }
}
