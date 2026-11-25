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

    /// <summary>The consumer-page usage (USB HID page 0x0C) of an Android media key (KEYCODE_*), for the paired
    /// keyboard's consumer-control device (<see cref="HidConsumerControl"/>); 0 = no media key. The PC front-end has
    /// no source: its window library reports no media keys.</summary>
    public static ushort ConsumerFromAndroidKeyCode(int keyCode) => keyCode switch
    {
        85 => 0xCD,                                            // MEDIA_PLAY_PAUSE: Play/Pause
        86 => 0xB7,                                            // MEDIA_STOP: Stop
        87 => 0xB5,                                            // MEDIA_NEXT: Scan Next Track
        88 => 0xB6,                                            // MEDIA_PREVIOUS: Scan Previous Track
        89 => 0xB4,                                            // MEDIA_REWIND: Rewind
        90 => 0xB3,                                            // MEDIA_FAST_FORWARD: Fast Forward
        126 => 0xB0,                                           // MEDIA_PLAY: Play
        127 => 0xB1,                                           // MEDIA_PAUSE: Pause
        130 => 0xB2,                                           // MEDIA_RECORD: Record
        129 => 0xB8,                                           // MEDIA_EJECT: Eject
        24 => 0xE9,                                            // VOLUME_UP: Volume Increment
        25 => 0xEA,                                            // VOLUME_DOWN: Volume Decrement
        164 => 0xE2,                                           // VOLUME_MUTE: Mute
        209 => 0x183,                                          // MUSIC: AL Consumer Control Configuration
        65 => 0x18A,                                           // ENVELOPE: AL Email Reader
        210 => 0x192,                                          // CALCULATOR: AL Calculator
        64 => 0x196,                                           // EXPLORER: AL Internet Browser
        84 => 0x221,                                           // SEARCH: AC Search
        _ => 0,
    };

    /// <summary>
    /// The consumer-page usage of a media key the PC's window library (GLFW) reports with no key token, by its
    /// scancode: on Windows the PS/2 set 1 code with the extended bit (0x100) that the keyboard driver gives each media
    /// key, on Linux (X11) the X keycode (the evdev code + 8). 0 = no media key or another system (macOS sends media
    /// keys as system events the window never gets).
    /// </summary>
    public static ushort ConsumerFromScancode(int scancode)
    {
        if (OperatingSystem.IsWindows())
        {
            return ConsumerFromWindowsScancode(scancode);
        }

        return OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() ? ConsumerFromX11Keycode(scancode) : (ushort)0;
    }

    /// <summary>Windows: the scancode (set 1, extended bit 0x100) of a media key.</summary>
    public static ushort ConsumerFromWindowsScancode(int scancode) => scancode switch
    {
        0x122 => 0xCD,                                         // Play/Pause
        0x124 => 0xB7,                                         // Stop
        0x110 => 0xB6,                                         // Scan Previous Track
        0x119 => 0xB5,                                         // Scan Next Track
        0x120 => 0xE2,                                         // Mute
        0x12E => 0xEA,                                         // Volume Decrement
        0x130 => 0xE9,                                         // Volume Increment
        0x16D => 0x183,                                        // Media Select: AL Consumer Control Configuration
        0x16C => 0x18A,                                        // Mail: AL Email Reader
        0x121 => 0x192,                                        // Calculator: AL Calculator
        0x132 => 0x223,                                        // Browser Home: AC Home
        0x165 => 0x221,                                        // Browser Search: AC Search
        _ => 0,
    };

    /// <summary>
    /// macOS: a media key arrives as an NSSystemDefined event of subtype 8 (NX_SUBTYPE_AUX_CONTROL_BUTTONS) whose
    /// data1 holds the key (bits 16..31, NX_KEYTYPE_*) and its state (bits 8..15: 0x0A down, 0x0B up). The usage, and
    /// whether the key went down; 0 for another event or key.
    /// </summary>
    public static ushort ConsumerFromMacSystemEvent(short subtype, long data1, out bool down)
    {
        down = false;
        if (subtype != 8)
        {
            return 0;
        }

        int state = (int)((data1 >> 8) & 0xFF);
        if (state != 0x0A && state != 0x0B)
        {
            return 0;
        }

        down = state == 0x0A;
        return ((data1 >> 16) & 0xFFFF) switch
        {
            0 => 0xE9,                                         // NX_KEYTYPE_SOUND_UP: Volume Increment
            1 => 0xEA,                                         // NX_KEYTYPE_SOUND_DOWN: Volume Decrement
            7 => 0xE2,                                         // NX_KEYTYPE_MUTE: Mute
            16 => 0xCD,                                        // NX_KEYTYPE_PLAY: Play/Pause
            17 => 0xB5,                                        // NX_KEYTYPE_NEXT: Scan Next Track
            18 => 0xB6,                                        // NX_KEYTYPE_PREVIOUS: Scan Previous Track
            19 => 0xB3,                                        // NX_KEYTYPE_FAST: Fast Forward
            20 => 0xB4,                                        // NX_KEYTYPE_REWIND: Rewind
            _ => 0,
        };
    }

    /// <summary>Linux (X11): the X keycode (evdev KEY_* + 8) of a media key.</summary>
    public static ushort ConsumerFromX11Keycode(int keycode) => keycode switch
    {
        172 => 0xCD,                                           // KEY_PLAYPAUSE: Play/Pause
        174 => 0xB7,                                           // KEY_STOPCD: Stop
        173 => 0xB6,                                           // KEY_PREVIOUSSONG: Scan Previous Track
        171 => 0xB5,                                           // KEY_NEXTSONG: Scan Next Track
        121 => 0xE2,                                           // KEY_MUTE: Mute
        122 => 0xEA,                                           // KEY_VOLUMEDOWN: Volume Decrement
        123 => 0xE9,                                           // KEY_VOLUMEUP: Volume Increment
        208 => 0xB0,                                           // KEY_PLAYCD: Play
        209 => 0xB1,                                           // KEY_PAUSECD: Pause
        176 => 0xB4,                                           // KEY_REWIND: Rewind
        216 => 0xB3,                                           // KEY_FASTFORWARD: Fast Forward
        163 => 0x18A,                                          // KEY_MAIL: AL Email Reader
        148 => 0x192,                                          // KEY_CALC: AL Calculator
        158 => 0x196,                                          // KEY_WWW: AL Internet Browser
        225 => 0x221,                                          // KEY_SEARCH: AC Search
        _ => 0,
    };
}
