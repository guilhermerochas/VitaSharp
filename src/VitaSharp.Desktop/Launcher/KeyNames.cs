using Avalonia.Input;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Converts Avalonia keys to the key names stored in input.json, which are Silk.NET <c>Key</c> names (what the GLFW
/// game window reports). Both sides use physical key positions (US layout names), so a binding made in the launcher
/// is the same key in game whatever the keyboard layout.
/// </summary>
public static class KeyNames
{
    public static string? FromAvalonia(PhysicalKey physical, Key key)
    {
        if (physical != PhysicalKey.None && FromPhysical(physical) is { } p) return p;
        return key switch
        {
            Key.None => null,
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => "Number" + (key - Key.D0),
            >= Key.NumPad0 and <= Key.NumPad9 => "Keypad" + (key - Key.NumPad0),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.Up or Key.Down or Key.Left or Key.Right or Key.Space or Key.Tab or Key.Escape or Key.Insert or Key.Delete
                or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.CapsLock => key.ToString(),
            Key.Enter => "Enter",
            Key.Back => "Backspace",
            Key.LeftShift => "ShiftLeft", Key.RightShift => "ShiftRight",
            Key.LeftCtrl => "ControlLeft", Key.RightCtrl => "ControlRight",
            Key.LeftAlt => "AltLeft", Key.RightAlt => "AltRight",
            Key.LWin => "SuperLeft", Key.RWin => "SuperRight",
            _ => null,
        };
    }

    static string? FromPhysical(PhysicalKey k) => k switch
    {
        >= PhysicalKey.A and <= PhysicalKey.Z => k.ToString(),
        >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9 => "Number" + (k - PhysicalKey.Digit0),
        >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9 => "Keypad" + (k - PhysicalKey.NumPad0),
        >= PhysicalKey.F1 and <= PhysicalKey.F24 => k.ToString(),
        PhysicalKey.ArrowUp => "Up", PhysicalKey.ArrowDown => "Down", PhysicalKey.ArrowLeft => "Left", PhysicalKey.ArrowRight => "Right",
        PhysicalKey.NumPadAdd => "KeypadAdd", PhysicalKey.NumPadSubtract => "KeypadSubtract", PhysicalKey.NumPadMultiply => "KeypadMultiply",
        PhysicalKey.NumPadDivide => "KeypadDivide", PhysicalKey.NumPadDecimal => "KeypadDecimal", PhysicalKey.NumPadEnter => "KeypadEnter",
        PhysicalKey.NumPadEqual => "KeypadEqual",
        PhysicalKey.Quote => "Apostrophe", PhysicalKey.BracketLeft => "LeftBracket", PhysicalKey.BracketRight => "RightBracket",
        PhysicalKey.Backslash => "BackSlash", PhysicalKey.Backquote => "GraveAccent",
        PhysicalKey.MetaLeft => "SuperLeft", PhysicalKey.MetaRight => "SuperRight", PhysicalKey.ContextMenu => "Menu",
        PhysicalKey.Comma or PhysicalKey.Period or PhysicalKey.Slash or PhysicalKey.Semicolon or PhysicalKey.Minus or PhysicalKey.Equal
            or PhysicalKey.Enter or PhysicalKey.Space or PhysicalKey.Tab or PhysicalKey.Escape or PhysicalKey.Backspace or PhysicalKey.Insert
            or PhysicalKey.Delete or PhysicalKey.Home or PhysicalKey.End or PhysicalKey.PageUp or PhysicalKey.PageDown or PhysicalKey.CapsLock
            or PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight or PhysicalKey.ControlLeft or PhysicalKey.ControlRight
            or PhysicalKey.AltLeft or PhysicalKey.AltRight or PhysicalKey.NumLock or PhysicalKey.ScrollLock or PhysicalKey.PrintScreen
            or PhysicalKey.Pause => k.ToString(),
        _ => null,
    };
}
