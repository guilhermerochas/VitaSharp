using System.Globalization;
using VitaSharp.Core.Input;
using VitaSharp.Core.Localization;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Names shown by the launcher's Controls screen for Vita controls, host bindings and controller families, in the
/// interface language (Core keeps neutral English labels, shared with other front ends).
/// </summary>
static class ControlNames
{
    const string ArrowUp = "\u2191", ArrowDown = "\u2193", ArrowLeft = "\u2190", ArrowRight = "\u2192";

    static string T(string key) => Loc.Get(key);
    static string Dpad(string arrow) => $"{T("CtlDpad")} {arrow}";
    static string Stick(bool left, string arrow) => $"{T(left ? "CtlLeftStick" : "CtlRightStick")} {arrow}";

    public static string Label(VitaControl c) => c switch
    {
        VitaControl.Up => Dpad(ArrowUp),
        VitaControl.Down => Dpad(ArrowDown),
        VitaControl.Left => Dpad(ArrowLeft),
        VitaControl.Right => Dpad(ArrowRight),
        VitaControl.Cross => T("CtlCross"),
        VitaControl.Circle => T("CtlCircle"),
        VitaControl.Square => T("CtlSquare"),
        VitaControl.Triangle => T("CtlTriangle"),
        VitaControl.L => "L",
        VitaControl.R => "R",
        VitaControl.Start => "Start",
        VitaControl.Select => "Select",
        VitaControl.PS => T("CtlPsButton"),
        VitaControl.L3 => "L3 (PS TV)",
        VitaControl.R3 => "R3 (PS TV)",
        VitaControl.LStickUp => Stick(true, ArrowUp),
        VitaControl.LStickDown => Stick(true, ArrowDown),
        VitaControl.LStickLeft => Stick(true, ArrowLeft),
        VitaControl.LStickRight => Stick(true, ArrowRight),
        VitaControl.RStickUp => Stick(false, ArrowUp),
        VitaControl.RStickDown => Stick(false, ArrowDown),
        VitaControl.RStickLeft => Stick(false, ArrowLeft),
        VitaControl.RStickRight => Stick(false, ArrowRight),
        VitaControl.TouchFront => T("CtlTouchFront"),
        VitaControl.TouchBack => T("CtlTouchBack"),
        VitaControl.TiltForward => T("CtlTiltForward"),
        VitaControl.TiltBack => T("CtlTiltBack"),
        VitaControl.TiltLeft => T("CtlTiltLeft"),
        VitaControl.TiltRight => T("CtlTiltRight"),
        VitaControl.TwistLeft => T("CtlTwistLeft"),
        VitaControl.TwistRight => T("CtlTwistRight"),
        VitaControl.MotionRecenter => T("CtlRecenter"),
        VitaControl.PauseEmulation => T("CtlPauseEmulation"),
        _ => c.ToString(),
    };

    /// <summary>A button combination ("Select + Start").</summary>
    public static string Describe(InputCombo combo, ControllerKind kind) => string.Join(" + ", combo.Parts.Select(p => Describe(p, kind)));

    public static string Kind(ControllerKind k) => k switch
    {
        ControllerKind.Xbox => "Xbox",
        ControllerKind.PlayStation => "PlayStation (DualShock / DualSense)",
        ControllerKind.Nintendo => "Nintendo (Switch Pro / Joy-Con)",
        ControllerKind.Keyboard => T("CtlKeyboard"),
        _ => T("CtlGenericController"),
    };

    public static string Model(string model) => model switch
    {
        "standard gamepad" => T("CtlStandardGamepad"),
        "joystick" => T("CtlRawJoystick"),
        "virtual" => "virtual",
        _ => model,
    };

    /// <summary>A host binding with the names printed on that kind of controller.</summary>
    public static string Describe(InputBinding b, ControllerKind kind)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (b.Kind)
        {
            case BindingKind.Key:
                return Key(b.Key ?? "");
            case BindingKind.PadButton:
                return (PadButton)b.Index switch
                {
                    PadButton.DpadUp => Dpad(ArrowUp),
                    PadButton.DpadDown => Dpad(ArrowDown),
                    PadButton.DpadLeft => Dpad(ArrowLeft),
                    PadButton.DpadRight => Dpad(ArrowRight),
                    PadButton.South when kind == ControllerKind.PlayStation => T("CtlCross"),
                    PadButton.East when kind == ControllerKind.PlayStation => T("CtlCircle"),
                    PadButton.West when kind == ControllerKind.PlayStation => T("CtlSquare"),
                    PadButton.North when kind == ControllerKind.PlayStation => T("CtlTriangle"),
                    PadButton.Touchpad => T("CtlTouchpadClick"),
                    _ => b.Describe(kind),
                };
            case BindingKind.PadAxis:
            {
                var a = (PadAxis)b.Index;
                if (a >= PadAxis.LeftTrigger)
                {
                    return b.Describe(kind);
                }

                bool x = a is PadAxis.LeftX or PadAxis.RightX;
                return Stick(a <= PadAxis.LeftY, x ? (b.Dir < 0 ? ArrowLeft : ArrowRight) : (b.Dir < 0 ? ArrowUp : ArrowDown));
            }
            case BindingKind.JoyButton:
                return Loc.Format("CtlJoyButton", b.Index);
            case BindingKind.JoyAxis:
                return Loc.Format("CtlJoyAxis", string.Create(inv, $"{b.Index}{(b.Dir == 2 ? "" : b.Dir < 0 ? "-" : "+")}"));
            case BindingKind.JoyHat:
                return string.Create(inv, $"Hat {b.Index} {b.Dir switch { 1 => ArrowUp, 2 => ArrowRight, 4 => ArrowDown, 8 => ArrowLeft, _ => b.Dir.ToString(inv) }}");
            default:
                return "";
        }
    }

    /// <summary>Short label for a stored key name ("Number1" -> "1", "ShiftRight" -> "Shift dir.").</summary>
    public static string Key(string name) => name switch
    {
        _ when name.StartsWith("Number", StringComparison.Ordinal) && name.Length == 7 => name[6..],
        _ when name.StartsWith("Keypad", StringComparison.Ordinal) => "Num " + name[6..],
        "ShiftLeft" => Loc.Format("CtlKeyLeft", "Shift"),
        "ShiftRight" => Loc.Format("CtlKeyRight", "Shift"),
        "ControlLeft" => Loc.Format("CtlKeyLeft", "Ctrl"),
        "ControlRight" => Loc.Format("CtlKeyRight", "Ctrl"),
        "AltLeft" => Loc.Format("CtlKeyLeft", "Alt"),
        "AltRight" => Loc.Format("CtlKeyRight", "Alt"),
        "SuperLeft" => Loc.Format("CtlKeyLeft", "Win"),
        "SuperRight" => Loc.Format("CtlKeyRight", "Win"),
        "Up" => ArrowUp,
        "Down" => ArrowDown,
        "Left" => ArrowLeft,
        "Right" => ArrowRight,
        "MouseLeft" => T("CtlMouseLeft"),
        "MouseRight" => T("CtlMouseRight"),
        "MouseMiddle" => T("CtlMouseMiddle"),
        "Mouse4" => Loc.Format("CtlMouseButton", 4),
        "Mouse5" => Loc.Format("CtlMouseButton", 5),
        "Space" => T("CtlKeySpace"),
        "Backspace" => "Backspace",
        "Escape" => "Esc",
        "Delete" => "Delete",
        "GraveAccent" => "`",
        "Apostrophe" => "'",
        "LeftBracket" => "[",
        "RightBracket" => "]",
        "BackSlash" => "\\",
        "Comma" => ",",
        "Period" => ".",
        "Slash" => "/",
        "Semicolon" => ";",
        "Minus" => "-",
        "Equal" => "=",
        _ => name,
    };
}
