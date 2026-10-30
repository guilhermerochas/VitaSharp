using System.Globalization;

namespace VitaSharp.Core.Input;

public enum BindingKind : byte
{
    None,
    /// <summary>A keyboard key, by its host key name (Silk.NET <c>Key</c> enum names, e.g. "Space", "ShiftRight").</summary>
    Key,
    /// <summary>A button of the standard gamepad layout (SDL GameController / GLFW gamepad, see <see cref="PadButton"/>).</summary>
    PadButton,
    /// <summary>An axis of the standard gamepad layout (<see cref="PadAxis"/>); triggers report 0..1.</summary>
    PadAxis,
    /// <summary>A raw joystick button, for controllers without a gamepad mapping.</summary>
    JoyButton,
    /// <summary>A raw joystick axis: Dir +1 / -1 = that half, 2 = the full range remapped to 0..1 (raw triggers).</summary>
    JoyAxis,
    /// <summary>A raw joystick hat: Dir is the direction bit (1 up, 2 right, 4 down, 8 left).</summary>
    JoyHat,
}

/// <summary>
/// Standard gamepad buttons in SDL GameController order (positional: South = bottom face button on every pad).
/// Misc1 is Share / Capture / Mic, Touchpad the click of a DualShock 4 / DualSense touch pad.
/// </summary>
public enum PadButton
{
    South, East, West, North, Back, Guide, Start, LeftStick, RightStick, LeftShoulder, RightShoulder,
    DpadUp, DpadDown, DpadLeft, DpadRight, Misc1, Paddle1, Paddle2, Paddle3, Paddle4, Touchpad,
}

/// <summary>Standard gamepad axes in SDL GameController order.</summary>
public enum PadAxis { LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger }

/// <summary>
/// Several host inputs that act only when all are held together, e.g. "pad:back&amp;pad:start" (Select + Start on a
/// controller): how a controller binds an emulator hotkey (<see cref="VitaControl.PauseEmulation"/>) without giving up
/// a button the game uses. Stored in the profile next to the single bindings; <see cref="InputBinding.TryParse"/>
/// rejects it, so code that only knows single bindings skips it.
/// </summary>
public readonly record struct InputCombo(InputBinding[] Parts)
{
    public const char Separator = '&';

    public override string ToString() => string.Join(Separator, Parts.Select(p => p.ToString()));

    public static bool TryParse(string? s, out InputCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(s) || !s.Contains(Separator)) return false;
        var items = s.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parts = new InputBinding[items.Length];
        for (int i = 0; i < items.Length; i++)
            if (!InputBinding.TryParse(items[i], out parts[i])) return false;
        if (parts.Length < 2) return false;
        combo = new InputCombo(parts);
        return true;
    }

    /// <summary>Activation 0..1: the weakest of the parts (all must be held).</summary>
    public float Value(PadSnapshot? pad, IReadOnlySet<string>? keys)
    {
        float v = 1f;
        foreach (var p in Parts) v = Math.Min(v, InputMapper.Value(p, pad, keys));
        return v;
    }

    public string Describe(ControllerKind kind) => string.Join(" + ", Parts.Select(p => p.Describe(kind)));
}

/// <summary>
/// One host input bound to a Vita control. Serialised as a short string: "key:Space", "pad:a", "pad:leftx-",
/// "pad:lefttrigger", "joy:b3", "joy:a2+", "joy:a5~" (full-range axis), "joy:h0.4" — the gamepad names are those of
/// the SDL GameControllerDB format.
/// </summary>
public readonly record struct InputBinding(BindingKind Kind, int Index, int Dir = 1, string? Key = null)
{
    public static InputBinding OfKey(string key) => new(BindingKind.Key, 0, 1, key);
    public static InputBinding Of(PadButton b) => new(BindingKind.PadButton, (int)b);
    public static InputBinding Of(PadAxis a, int dir = 1) => new(BindingKind.PadAxis, (int)a, a >= PadAxis.LeftTrigger ? 1 : dir);
    public static InputBinding JoyButton(int i) => new(BindingKind.JoyButton, i);
    public static InputBinding JoyAxis(int i, int dir) => new(BindingKind.JoyAxis, i, dir);
    public static InputBinding JoyHat(int i, int bit) => new(BindingKind.JoyHat, i, bit);

    public bool IsGamepad => Kind is BindingKind.PadButton or BindingKind.PadAxis;
    public bool IsJoystick => Kind is BindingKind.JoyButton or BindingKind.JoyAxis or BindingKind.JoyHat;

    static readonly string[] ButtonNames =
        ["a", "b", "x", "y", "back", "guide", "start", "leftstick", "rightstick", "leftshoulder", "rightshoulder", "dpup", "dpdown", "dpleft", "dpright",
         "misc1", "paddle1", "paddle2", "paddle3", "paddle4", "touchpad"];
    static readonly string[] AxisNames = ["leftx", "lefty", "rightx", "righty", "lefttrigger", "righttrigger"];

    public override string ToString() => Kind switch
    {
        BindingKind.Key => "key:" + Key,
        BindingKind.PadButton => "pad:" + ButtonNames[Index],
        BindingKind.PadAxis => "pad:" + AxisNames[Index] + (Index >= (int)PadAxis.LeftTrigger ? "" : Dir < 0 ? "-" : "+"),
        BindingKind.JoyButton => $"joy:b{Index}",
        BindingKind.JoyAxis => $"joy:a{Index}{(Dir == 2 ? "~" : Dir < 0 ? "-" : "+")}",
        BindingKind.JoyHat => $"joy:h{Index}.{Dir}",
        _ => "",
    };

    public static bool TryParse(string? s, out InputBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        int colon = s.IndexOf(':');
        if (colon <= 0 || colon == s.Length - 1) return false;
        string kind = s[..colon], v = s[(colon + 1)..];
        var inv = CultureInfo.InvariantCulture;
        switch (kind)
        {
            case "key":
                binding = OfKey(v);
                return true;
            case "pad":
            {
                int b = Array.IndexOf(ButtonNames, v);
                if (b >= 0) { binding = Of((PadButton)b); return true; }
                int dir = 1;
                if (v.EndsWith('+')) v = v[..^1];
                else if (v.EndsWith('-')) { v = v[..^1]; dir = -1; }
                int a = Array.IndexOf(AxisNames, v);
                if (a < 0) return false;
                binding = Of((PadAxis)a, dir);
                return true;
            }
            case "joy" when v.Length >= 2:
            {
                char t = v[0];
                string rest = v[1..];
                if (t == 'b' && int.TryParse(rest, NumberStyles.None, inv, out int bi)) { binding = JoyButton(bi); return true; }
                if (t == 'a')
                {
                    int dir = rest[^1] switch { '-' => -1, '~' => 2, _ => 1 };
                    if (rest[^1] is '+' or '-' or '~') rest = rest[..^1];
                    if (!int.TryParse(rest, NumberStyles.None, inv, out int ai)) return false;
                    binding = JoyAxis(ai, dir);
                    return true;
                }
                if (t == 'h')
                {
                    var p = rest.Split('.');
                    if (p.Length != 2 || !int.TryParse(p[0], NumberStyles.None, inv, out int hi) || !int.TryParse(p[1], NumberStyles.None, inv, out int bit)) return false;
                    binding = JoyHat(hi, bit);
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    /// <summary>A label for the configuration screen that uses the names printed on that kind of controller.</summary>
    public string Describe(ControllerKind kind)
    {
        switch (Kind)
        {
            case BindingKind.Key: return Key ?? "";
            case BindingKind.PadButton: return ButtonLabel((PadButton)Index, kind);
            case BindingKind.PadAxis:
            {
                var a = (PadAxis)Index;
                if (a == PadAxis.LeftTrigger) return kind switch { ControllerKind.PlayStation => "L2", ControllerKind.Nintendo => "ZL", _ => "LT" };
                if (a == PadAxis.RightTrigger) return kind switch { ControllerKind.PlayStation => "R2", ControllerKind.Nintendo => "ZR", _ => "RT" };
                string stick = a <= PadAxis.LeftY ? "Left stick" : "Right stick";
                bool x = a is PadAxis.LeftX or PadAxis.RightX;
                return $"{stick} {(x ? (Dir < 0 ? "←" : "→") : (Dir < 0 ? "↑" : "↓"))}";
            }
            case BindingKind.JoyButton: return $"Button {Index}";
            case BindingKind.JoyAxis: return $"Axis {Index}{(Dir == 2 ? "" : Dir < 0 ? "-" : "+")}";
            case BindingKind.JoyHat: return $"Hat {Index} {Dir switch { 1 => "↑", 2 => "→", 4 => "↓", 8 => "←", _ => Dir.ToString(CultureInfo.InvariantCulture) }}";
            default: return "";
        }
    }

    public static string ButtonLabel(PadButton b, ControllerKind kind) => (b, kind) switch
    {
        (PadButton.South, ControllerKind.PlayStation) => "Cross",
        (PadButton.East, ControllerKind.PlayStation) => "Circle",
        (PadButton.West, ControllerKind.PlayStation) => "Square",
        (PadButton.North, ControllerKind.PlayStation) => "Triangle",
        (PadButton.LeftShoulder, ControllerKind.PlayStation) => "L1",
        (PadButton.RightShoulder, ControllerKind.PlayStation) => "R1",
        (PadButton.Back, ControllerKind.PlayStation) => "Share",
        (PadButton.Start, ControllerKind.PlayStation) => "Options",
        (PadButton.Guide, ControllerKind.PlayStation) => "PS",
        (PadButton.LeftStick, ControllerKind.PlayStation) => "L3",
        (PadButton.RightStick, ControllerKind.PlayStation) => "R3",
        // Nintendo prints A on the right and B at the bottom: the positional mapping keeps the bottom one as South
        (PadButton.South, ControllerKind.Nintendo) => "B",
        (PadButton.East, ControllerKind.Nintendo) => "A",
        (PadButton.West, ControllerKind.Nintendo) => "Y",
        (PadButton.North, ControllerKind.Nintendo) => "X",
        (PadButton.LeftShoulder, ControllerKind.Nintendo) => "L",
        (PadButton.RightShoulder, ControllerKind.Nintendo) => "R",
        (PadButton.Back, ControllerKind.Nintendo) => "-",
        (PadButton.Start, ControllerKind.Nintendo) => "+",
        (PadButton.Guide, ControllerKind.Nintendo) => "Home",
        (PadButton.South, _) => "A",
        (PadButton.East, _) => "B",
        (PadButton.West, _) => "X",
        (PadButton.North, _) => "Y",
        (PadButton.LeftShoulder, _) => "LB",
        (PadButton.RightShoulder, _) => "RB",
        (PadButton.Back, _) => "View",
        (PadButton.Start, _) => "Menu",
        (PadButton.Guide, _) => "Guide",
        (PadButton.LeftStick, _) => "LS",
        (PadButton.RightStick, _) => "RS",
        (PadButton.DpadUp, _) => "D-pad ↑",
        (PadButton.DpadRight, _) => "D-pad →",
        (PadButton.DpadDown, _) => "D-pad ↓",
        (PadButton.DpadLeft, _) => "D-pad ←",
        (PadButton.Misc1, ControllerKind.PlayStation) => "Mic",
        (PadButton.Misc1, ControllerKind.Nintendo) => "Capture",
        (PadButton.Misc1, _) => "Share",
        (PadButton.Paddle1, _) => "Paddle 1",
        (PadButton.Paddle2, _) => "Paddle 2",
        (PadButton.Paddle3, _) => "Paddle 3",
        (PadButton.Paddle4, _) => "Paddle 4",
        (PadButton.Touchpad, _) => "Touchpad click",
        _ => b.ToString(),
    };
}
