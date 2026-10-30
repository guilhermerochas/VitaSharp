using System.Globalization;
using VitaSharp.Core.Input;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Portuguese names shown by the launcher's Controls screen for Vita controls, host bindings and controller
/// families (Core keeps neutral English labels, shared with other front ends).
/// </summary>
static class ControlNames
{
    public static string Label(VitaControl c) => c switch
    {
        VitaControl.Up => "Direcional ↑", VitaControl.Down => "Direcional ↓",
        VitaControl.Left => "Direcional ←", VitaControl.Right => "Direcional →",
        VitaControl.Cross => "Cruz", VitaControl.Circle => "Círculo", VitaControl.Square => "Quadrado", VitaControl.Triangle => "Triângulo",
        VitaControl.L => "L", VitaControl.R => "R", VitaControl.Start => "Start", VitaControl.Select => "Select", VitaControl.PS => "Botão PS",
        VitaControl.L3 => "L3 (PS TV)", VitaControl.R3 => "R3 (PS TV)",
        VitaControl.LStickUp => "Analógico esq. ↑", VitaControl.LStickDown => "Analógico esq. ↓",
        VitaControl.LStickLeft => "Analógico esq. ←", VitaControl.LStickRight => "Analógico esq. →",
        VitaControl.RStickUp => "Analógico dir. ↑", VitaControl.RStickDown => "Analógico dir. ↓",
        VitaControl.RStickLeft => "Analógico dir. ←", VitaControl.RStickRight => "Analógico dir. →",
        VitaControl.TouchFront => "Toque frontal (ponto)", VitaControl.TouchBack => "Toque traseiro (ponto)",
        VitaControl.TiltForward => "Inclinar para frente", VitaControl.TiltBack => "Inclinar para trás",
        VitaControl.TiltLeft => "Inclinar para esquerda", VitaControl.TiltRight => "Inclinar para direita",
        VitaControl.TwistLeft => "Girar para esquerda", VitaControl.TwistRight => "Girar para direita",
        VitaControl.MotionRecenter => "Recentralizar",
        _ => c.ToString(),
    };

    public static string Kind(ControllerKind k) => k switch
    {
        ControllerKind.Xbox => "Xbox",
        ControllerKind.PlayStation => "PlayStation (DualShock / DualSense)",
        ControllerKind.Nintendo => "Nintendo (Switch Pro / Joy-Con)",
        ControllerKind.Keyboard => "Teclado",
        _ => "Controle genérico",
    };

    public static string Model(string model) => model switch
    {
        "standard gamepad" => "gamepad padrão",
        "joystick" => "joystick (sem mapeamento)",
        "virtual" => "virtual",
        _ => model,
    };

    /// <summary>A host binding with the names printed on that kind of controller.</summary>
    public static string Describe(InputBinding b, ControllerKind kind)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (b.Kind)
        {
            case BindingKind.Key: return Key(b.Key ?? "");
            case BindingKind.PadButton:
                return (PadButton)b.Index switch
                {
                    PadButton.DpadUp => "Direcional ↑", PadButton.DpadDown => "Direcional ↓",
                    PadButton.DpadLeft => "Direcional ←", PadButton.DpadRight => "Direcional →",
                    PadButton.South when kind == ControllerKind.PlayStation => "Cruz",
                    PadButton.East when kind == ControllerKind.PlayStation => "Círculo",
                    PadButton.West when kind == ControllerKind.PlayStation => "Quadrado",
                    PadButton.North when kind == ControllerKind.PlayStation => "Triângulo",
                    PadButton.Touchpad => "Clique do touchpad",
                    _ => b.Describe(kind),
                };
            case BindingKind.PadAxis:
            {
                var a = (PadAxis)b.Index;
                if (a >= PadAxis.LeftTrigger) return b.Describe(kind);
                string stick = a <= PadAxis.LeftY ? "Analógico esq." : "Analógico dir.";
                bool x = a is PadAxis.LeftX or PadAxis.RightX;
                return $"{stick} {(x ? (b.Dir < 0 ? "←" : "→") : (b.Dir < 0 ? "↑" : "↓"))}";
            }
            case BindingKind.JoyButton: return string.Create(inv, $"Botão {b.Index}");
            case BindingKind.JoyAxis: return string.Create(inv, $"Eixo {b.Index}{(b.Dir == 2 ? "" : b.Dir < 0 ? "-" : "+")}");
            case BindingKind.JoyHat:
                return string.Create(inv, $"Hat {b.Index} {b.Dir switch { 1 => "↑", 2 => "→", 4 => "↓", 8 => "←", _ => b.Dir.ToString(inv) }}");
            default: return "";
        }
    }

    /// <summary>Short label for a stored key name ("Number1" -> "1", "ShiftRight" -> "Shift dir.").</summary>
    public static string Key(string name) => name switch
    {
        _ when name.StartsWith("Number", StringComparison.Ordinal) && name.Length == 7 => name[6..],
        _ when name.StartsWith("Keypad", StringComparison.Ordinal) => "Num " + name[6..],
        "ShiftLeft" => "Shift esq.", "ShiftRight" => "Shift dir.", "ControlLeft" => "Ctrl esq.", "ControlRight" => "Ctrl dir.",
        "AltLeft" => "Alt esq.", "AltRight" => "Alt dir.", "SuperLeft" => "Win esq.", "SuperRight" => "Win dir.",
        "Up" => "↑", "Down" => "↓", "Left" => "←", "Right" => "→",
        "Space" => "Espaço", "Backspace" => "Backspace", "Escape" => "Esc", "Delete" => "Delete",
        "GraveAccent" => "`", "Apostrophe" => "'", "LeftBracket" => "[", "RightBracket" => "]",
        "BackSlash" => "\\", "Comma" => ",", "Period" => ".", "Slash" => "/", "Semicolon" => ";", "Minus" => "-", "Equal" => "=",
        _ => name,
    };
}
