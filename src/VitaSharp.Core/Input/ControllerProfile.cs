using System.Text.Json.Serialization;

namespace VitaSharp.Core.Input;

/// <summary>Response curve of one analog stick.</summary>
public sealed class StickSettings
{
    /// <summary>Radial deadzone as a fraction of the full deflection (0..0.9).</summary>
    public float Deadzone { get; set; } = DefaultDeadzone;
    /// <summary>Output gain after the deadzone (1 = unchanged); values above 1 reach full deflection earlier.</summary>
    public float Sensitivity { get; set; } = 1f;
    public bool InvertY { get; set; }

    public const float DefaultDeadzone = 0.10f;

    public StickSettings Clone() => new() { Deadzone = Deadzone, Sensitivity = Sensitivity, InvertY = InvertY };
}

/// <summary>
/// Mapping of one host input device (the keyboard, or a controller identified by its GUID and name) onto the Vita's
/// controls. Bindings are kept as strings (see <see cref="InputBinding"/>) so the JSON stays readable and editable.
/// </summary>
public sealed class ControllerProfile
{
    /// <summary>"keyboard", or the controller GUID (SDL format, as reported by GLFW).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<ControllerKind>))]
    public ControllerKind Kind { get; set; }
    /// <summary>Control name (<see cref="VitaControl"/>) -> host inputs; any of them activates the control.</summary>
    public Dictionary<string, List<string>> Bindings { get; set; } = new();
    public StickSettings LeftStick { get; set; } = new();
    public StickSettings RightStick { get; set; } = new();
    /// <summary>Exchanges Cross and Circle (for players used to Circle = confirm, as on Japanese consoles).</summary>
    public bool SwapConfirm { get; set; }
    /// <summary>Analog value (0..1) at which an axis bound to a button counts as pressed.</summary>
    public float ButtonThreshold { get; set; } = 0.5f;
    /// <summary>Normalised point (0..1) touched by the TouchFront / TouchBack controls.</summary>
    public float TouchFrontX { get; set; } = 0.5f;
    public float TouchFrontY { get; set; } = 0.5f;
    public float TouchBackX { get; set; } = 0.5f;
    public float TouchBackY { get; set; } = 0.5f;
    /// <summary>The controller's touch pad (DualShock 4, DualSense...) drives the Vita's touch panels: front by
    /// default, rear while <see cref="TouchpadRearModifier"/> is held, both while <see cref="TouchpadBothModifier"/> is.</summary>
    public bool TouchpadEnabled { get; set; } = true;
    /// <summary>Binding (InputBinding syntax) held to send touch pad fingers to the rear pad; "" = none.</summary>
    public string TouchpadRearModifier { get; set; } = DefaultRearModifier;
    /// <summary>Binding held to send touch pad fingers to both panels; "" = none.</summary>
    public string TouchpadBothModifier { get; set; } = DefaultBothModifier;

    public const string DefaultRearModifier = "pad:lefttrigger", DefaultBothModifier = "pad:righttrigger";

    public const string KeyboardId = "keyboard";

    public IEnumerable<InputBinding> Get(VitaControl c)
    {
        if (!Bindings.TryGetValue(c.ToString(), out var list)) yield break;
        foreach (var s in list)
            if (InputBinding.TryParse(s, out var b)) yield return b;
    }

    /// <summary>Replaces every binding of <paramref name="c"/> with <paramref name="b"/> (null = unbound).</summary>
    public void Set(VitaControl c, InputBinding? b)
    {
        if (b is { } x) Bindings[c.ToString()] = [x.ToString()];
        else Bindings.Remove(c.ToString());
    }

    public void Add(VitaControl c, InputBinding b)
    {
        var key = c.ToString();
        if (!Bindings.TryGetValue(key, out var list)) Bindings[key] = list = new();
        var s = b.ToString();
        if (!list.Contains(s)) list.Add(s);
    }

    public void Remove(VitaControl c, InputBinding b)
    {
        var key = c.ToString();
        if (Bindings.TryGetValue(key, out var list) && list.Remove(b.ToString()) && list.Count == 0) Bindings.Remove(key);
    }

    public bool Has(VitaControl c, InputBinding b) => Bindings.TryGetValue(c.ToString(), out var list) && list.Contains(b.ToString());

    public ControllerProfile Clone() => new()
    {
        Id = Id, Name = Name, Kind = Kind,
        Bindings = Bindings.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        LeftStick = LeftStick.Clone(), RightStick = RightStick.Clone(), SwapConfirm = SwapConfirm, ButtonThreshold = ButtonThreshold,
        TouchFrontX = TouchFrontX, TouchFrontY = TouchFrontY, TouchBackX = TouchBackX, TouchBackY = TouchBackY,
        TouchpadEnabled = TouchpadEnabled, TouchpadRearModifier = TouchpadRearModifier, TouchpadBothModifier = TouchpadBothModifier,
    };
}

/// <summary>Contents of input.json: the keyboard profile plus one profile per controller that was customised.</summary>
public sealed class InputConfig
{
    public int Version { get; set; } = 1;
    public ControllerProfile? Keyboard { get; set; }
    public List<ControllerProfile> Controllers { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InputConfig))]
internal sealed partial class InputJson : JsonSerializerContext;
