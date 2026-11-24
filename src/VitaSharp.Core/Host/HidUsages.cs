namespace VitaSharp.Core.Host;

/// <summary>
/// USB HID keyboard usages (HID Usage Tables, page 7) of the host's keys, for the paired keyboard (<see cref="HidKeyboard"/>):
/// the desktop's keys by their Silk.NET name, Android's by their KeyEvent key code. 0 = a key with no usage.
/// </summary>
public static class HidUsages
{
    private static readonly Dictionary<string, byte> s_byName = BuildNames();

    /// <summary>The usage of a desktop key (its Silk.NET <c>Key</c> name: "A", "Number1", "Enter", "ShiftLeft"...).</summary>
    public static byte FromKeyName(string name) => s_byName.GetValueOrDefault(name);

    private static Dictionary<string, byte> BuildNames()
    {
        var names = new Dictionary<string, byte>(StringComparer.Ordinal);
        for (int i = 0; i < 26; i++)
        {
            names[((char)('A' + i)).ToString()] = (byte)(0x04 + i);
        }

        // Number1..Number9 are 0x1E..0x26, Number0 0x27
        for (int i = 1; i <= 9; i++)
        {
            names[$"Number{i}"] = (byte)(0x1E + i - 1);
            names[$"Keypad{i}"] = (byte)(0x59 + i - 1);
        }

        names["Number0"] = 0x27;
        names["Keypad0"] = 0x62;
        for (int i = 1; i <= 12; i++)
        {
            names[$"F{i}"] = (byte)(0x3A + i - 1);
        }

        (string Name, byte Usage)[] others =
        [
            ("Enter", 0x28), ("Escape", 0x29), ("Backspace", 0x2A), ("Tab", 0x2B), ("Space", 0x2C), ("Minus", 0x2D),
            ("Equal", 0x2E), ("LeftBracket", 0x2F), ("RightBracket", 0x30), ("BackSlash", 0x31), ("Semicolon", 0x33),
            ("Apostrophe", 0x34), ("GraveAccent", 0x35), ("Comma", 0x36), ("Period", 0x37), ("Slash", 0x38),
            ("CapsLock", 0x39), ("PrintScreen", 0x46), ("ScrollLock", 0x47), ("Pause", 0x48), ("Insert", 0x49),
            ("Home", 0x4A), ("PageUp", 0x4B), ("Delete", 0x4C), ("End", 0x4D), ("PageDown", 0x4E), ("Right", 0x4F),
            ("Left", 0x50), ("Down", 0x51), ("Up", 0x52), ("NumLock", 0x53), ("KeypadDivide", 0x54),
            ("KeypadMultiply", 0x55), ("KeypadSubtract", 0x56), ("KeypadAdd", 0x57), ("KeypadEnter", 0x58),
            ("KeypadDecimal", 0x63), ("Menu", 0x65), ("KeypadEqual", 0x67), ("World1", 0x64),
            ("ControlLeft", 0xE0), ("ShiftLeft", 0xE1), ("AltLeft", 0xE2), ("SuperLeft", 0xE3),
            ("ControlRight", 0xE4), ("ShiftRight", 0xE5), ("AltRight", 0xE6), ("SuperRight", 0xE7),
        ];
        foreach ((string name, byte usage) in others)
        {
            names[name] = usage;
        }

        return names;
    }

    /// <summary>The usage of an Android key (android.view.KeyEvent KEYCODE_*).</summary>
    public static byte FromAndroidKeyCode(int keyCode) => keyCode switch
    {
        >= 29 and <= 54 => (byte)(0x04 + keyCode - 29),       // KEYCODE_A..Z
        >= 8 and <= 16 => (byte)(0x1E + keyCode - 8),          // KEYCODE_1..9
        7 => 0x27,                                             // KEYCODE_0
        >= 131 and <= 142 => (byte)(0x3A + keyCode - 131),     // KEYCODE_F1..F12
        >= 145 and <= 153 => (byte)(0x59 + keyCode - 145),     // KEYCODE_NUMPAD_1..9
        144 => 0x62,                                           // KEYCODE_NUMPAD_0
        66 => 0x28,                                            // ENTER
        111 => 0x29,                                           // ESCAPE
        67 => 0x2A,                                            // DEL (backspace)
        61 => 0x2B,                                            // TAB
        62 => 0x2C,                                            // SPACE
        69 => 0x2D,                                            // MINUS
        70 => 0x2E,                                            // EQUALS
        71 => 0x2F,                                            // LEFT_BRACKET
        72 => 0x30,                                            // RIGHT_BRACKET
        73 => 0x31,                                            // BACKSLASH
        74 => 0x33,                                            // SEMICOLON
        75 => 0x34,                                            // APOSTROPHE
        68 => 0x35,                                            // GRAVE
        55 => 0x36,                                            // COMMA
        56 => 0x37,                                            // PERIOD
        76 => 0x38,                                            // SLASH
        115 => 0x39,                                           // CAPS_LOCK
        120 => 0x46,                                           // SYSRQ
        116 => 0x47,                                           // SCROLL_LOCK
        121 => 0x48,                                           // BREAK
        124 => 0x49,                                           // INSERT
        122 => 0x4A,                                           // MOVE_HOME
        92 => 0x4B,                                            // PAGE_UP
        112 => 0x4C,                                           // FORWARD_DEL
        123 => 0x4D,                                           // MOVE_END
        93 => 0x4E,                                            // PAGE_DOWN
        22 => 0x4F,                                            // DPAD_RIGHT
        21 => 0x50,                                            // DPAD_LEFT
        20 => 0x51,                                            // DPAD_DOWN
        19 => 0x52,                                            // DPAD_UP
        143 => 0x53,                                           // NUM_LOCK
        154 => 0x54,                                           // NUMPAD_DIVIDE
        155 => 0x55,                                           // NUMPAD_MULTIPLY
        156 => 0x56,                                           // NUMPAD_SUBTRACT
        157 => 0x57,                                           // NUMPAD_ADD
        160 => 0x58,                                           // NUMPAD_ENTER
        158 => 0x63,                                           // NUMPAD_DOT
        82 => 0x65,                                            // MENU
        113 => 0xE0,                                           // CTRL_LEFT
        59 => 0xE1,                                            // SHIFT_LEFT
        57 => 0xE2,                                            // ALT_LEFT
        117 => 0xE3,                                           // META_LEFT
        114 => 0xE4,                                           // CTRL_RIGHT
        60 => 0xE5,                                            // SHIFT_RIGHT
        58 => 0xE6,                                            // ALT_RIGHT
        118 => 0xE7,                                           // META_RIGHT
        _ => 0,
    };
}
