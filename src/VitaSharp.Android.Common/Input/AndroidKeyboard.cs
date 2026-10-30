using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Input;

/// <summary>
/// Android key codes (android.view.KeyEvent KEYCODE_*) of a hardware or Bluetooth keyboard as the key names input.json
/// stores: Silk.NET <c>Key</c> names, what the desktop's game window reports and its launcher writes
/// (VitaSharp.Desktop KeyNames), so one keyboard profile means the same keys on both. Android reports the key the
/// keyboard layout maps (letters follow an AZERTY/QWERTZ layout chosen in the system settings; the desktop names
/// physical US positions).
/// </summary>
public static class AndroidKeyNames
{
    public const int A = 29, Z = 54, Digit0 = 7, Digit9 = 16, Numpad0 = 144, Numpad9 = 153, F1 = 131, F12 = 142;
    public const int Space = 62, Enter = 66, Del = 67, ForwardDel = 112, Tab = 61, Escape = 111;
    public const int ShiftLeft = 59, ShiftRight = 60, CtrlLeft = 113, CtrlRight = 114, AltLeft = 57, AltRight = 58;
    public const int MetaLeft = 117, MetaRight = 118, Menu = 82, MoveHome = 122, MoveEnd = 123, PageUp = 92, PageDown = 93;
    public const int Insert = 124, CapsLock = 115, ScrollLock = 116, NumLock = 143, Sysrq = 120, Break = 121;
    public const int NumpadDivide = 154, NumpadMultiply = 155, NumpadSubtract = 156, NumpadAdd = 157, NumpadDot = 158,
        NumpadEnter = 160, NumpadEquals = 161;
    public const int Grave = 68, Minus = 69, EqualsKey = 70, LeftBracket = 71, RightBracket = 72, Backslash = 73,
        Semicolon = 74, Apostrophe = 75, Slash = 76, Comma = 55, Period = 56;

    /// <summary>The input.json name of a key code, or null for keys a keyboard profile cannot bind (volume, media,
    /// the system's Back/Home).</summary>
    public static string? Name(int keyCode) => keyCode switch
    {
        >= A and <= Z => ((char)('A' + keyCode - A)).ToString(),
        >= Digit0 and <= Digit9 => "Number" + (keyCode - Digit0),
        >= Numpad0 and <= Numpad9 => "Keypad" + (keyCode - Numpad0),
        >= F1 and <= F12 => "F" + (keyCode - F1 + 1),
        AndroidKeys.DpadUp => "Up", AndroidKeys.DpadDown => "Down", AndroidKeys.DpadLeft => "Left", AndroidKeys.DpadRight => "Right",
        Space => "Space", Enter => "Enter", Del => "Backspace", ForwardDel => "Delete", Tab => "Tab", Escape => "Escape",
        ShiftLeft => "ShiftLeft", ShiftRight => "ShiftRight", CtrlLeft => "ControlLeft", CtrlRight => "ControlRight",
        AltLeft => "AltLeft", AltRight => "AltRight", MetaLeft => "SuperLeft", MetaRight => "SuperRight", Menu => "Menu",
        MoveHome => "Home", MoveEnd => "End", PageUp => "PageUp", PageDown => "PageDown", Insert => "Insert",
        CapsLock => "CapsLock", ScrollLock => "ScrollLock", NumLock => "NumLock", Sysrq => "PrintScreen", Break => "Pause",
        NumpadDivide => "KeypadDivide", NumpadMultiply => "KeypadMultiply", NumpadSubtract => "KeypadSubtract",
        NumpadAdd => "KeypadAdd", NumpadDot => "KeypadDecimal", NumpadEnter => "KeypadEnter", NumpadEquals => "KeypadEqual",
        Grave => "GraveAccent", Minus => "Minus", EqualsKey => "Equal", LeftBracket => "LeftBracket", RightBracket => "RightBracket",
        Backslash => "BackSlash", Semicolon => "Semicolon", Apostrophe => "Apostrophe", Slash => "Slash", Comma => "Comma", Period => "Period",
        _ => null,
    };
}

/// <summary>What the activity does with a keyboard key event.</summary>
[Flags]
public enum KeyboardKeyAction
{
    /// <summary>Not a key of the keyboard profile: leave the event to Android (volume, Back, Escape...).</summary>
    None = 0,
    /// <summary>The key belongs to the emulator: consume the event.</summary>
    Handled = 1,
    /// <summary>The pause hotkey (<see cref="VitaControl.PauseEmulation"/>, P by default) went down: open the pause menu.
    /// The key never reaches the game.</summary>
    Pause = 2,
    /// <summary>A key bound to the PS button went down (Home by default): in a game without the shell, the pause menu
    /// too, like a controller's PS button.</summary>
    PsButton = 4,
}

/// <summary>
/// A hardware or Bluetooth keyboard in the game: key events drive the Vita through the keyboard profile of input.json
/// (the desktop's mapping, <see cref="DefaultMappings.Keyboard"/> when none was saved: arrows, X/C/Z/V, Q/E, Enter,
/// Backspace, WASD/IJKL, P = pause), and its tilt keys (numeric keypad 8/2/4/6/7/9 or T/G/F/H/R/Y, re-centre 5/B)
/// drive SceMotion through <see cref="VirtualMotion"/>, like the desktop's <c>HostInput</c>: the motion source once a
/// tilt key was used, unless the profile's motion source is Off or Controller. Thread-safe: events come from the UI
/// thread, the guest polls from its own.
/// </summary>
public sealed class AndroidKeyboard : IMotionSource
{
    readonly object _lock = new();
    readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    readonly HashSet<string> _pauseHeld = new(StringComparer.Ordinal);
    readonly CompiledProfile _profile;
    readonly HashSet<string> _bound = new(StringComparer.Ordinal), _pauseKeys = new(StringComparer.Ordinal), _psKeys = new(StringComparer.Ordinal);
    readonly VirtualMotion _virtual;
    readonly Func<long> _nowUs;
    bool _virtualLogged;

    /// <param name="profiles">input.json (null: the default keyboard mapping).</param>
    /// <param name="nowUs">Host clock in microseconds for the virtual motion (tests); null = Stopwatch.</param>
    public AndroidKeyboard(InputProfileStore? profiles = null, Func<long>? nowUs = null)
    {
        _profile = new CompiledProfile(profiles?.KeyboardProfile() ?? DefaultMappings.Keyboard());
        _virtual = new VirtualMotion(_profile.Profile.Tilt);
        _nowUs = nowUs ?? (() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency * 1e6));
        foreach (var c in VitaControls.All)
            foreach (ref readonly var b in _profile[c])
            {
                if (b.Kind != BindingKind.Key || b.Key == null) continue;
                (c == VitaControl.PauseEmulation ? _pauseKeys : _bound).Add(b.Key);
                if (c == VitaControl.PS) _psKeys.Add(b.Key);
            }
    }

    public ControllerProfile Profile => _profile.Profile;

    /// <summary>Some mapped key is held (the input backend skips the keyboard otherwise).</summary>
    public bool AnyHeld { get { lock (_lock) return _keys.Count > 0; } }

    /// <summary>
    /// A key event of a keyboard (<paramref name="keyCode"/> = KeyEvent.getKeyCode). <paramref name="repeat"/>: an
    /// auto-repeat of a held key (getRepeatCount &gt; 0), consumed when the key is mapped but changing nothing.
    /// </summary>
    public KeyboardKeyAction OnKey(int keyCode, bool down, bool repeat = false)
    {
        if (AndroidKeyNames.Name(keyCode) is not { } name) return KeyboardKeyAction.None;
        lock (_lock)
        {
            // the pause hotkey never reaches the game; a held key opens the menu once
            if (_pauseKeys.Contains(name))
            {
                if (!down) { _pauseHeld.Remove(name); return KeyboardKeyAction.Handled; }
                return !repeat && _pauseHeld.Add(name) ? KeyboardKeyAction.Handled | KeyboardKeyAction.Pause : KeyboardKeyAction.Handled;
            }
            if (!_bound.Contains(name)) return KeyboardKeyAction.None;
            if (repeat) return KeyboardKeyAction.Handled;
            var action = KeyboardKeyAction.Handled;
            if (down)
            {
                if (_keys.Add(name) && _psKeys.Contains(name)) action |= KeyboardKeyAction.PsButton;
            }
            else _keys.Remove(name);
            // tilt keys take effect (and a quick re-centre tap is seen) even between two SceMotion polls
            if (MotionAllowed) _virtual.SetControls(_profile, null, _keys);
            return action;
        }
    }

    /// <summary>Lets go of every key: the activity lost the window focus (a dialog, the pause menu, the IME dialog took
    /// the key events, so the key-up of a held key never comes back to the game) or went to the background.</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            _keys.Clear();
            _pauseHeld.Clear();
            if (MotionAllowed) _virtual.SetControls(_profile, null, _keys);
        }
    }

    /// <summary>The keyboard's Vita state through the keyboard profile.</summary>
    public MappedInput Map()
    {
        lock (_lock) return InputMapper.Map(_profile, null, _keys);
    }

    // ------------------------------------------------------------------ motion (IMotionSource)

    /// <summary>The tilt keys may drive the Vita's sensors (the profile's motion source is not Off or Controller).</summary>
    bool MotionAllowed => _profile.Profile.MotionSource is not (MotionSourceMode.Off or MotionSourceMode.Controller);

    public VirtualMotion Virtual => _virtual;

    /// <summary>True from the first use of a tilt key: until then the Vita keeps the historic at-rest state.</summary>
    public bool HasSensors => MotionAllowed && _virtual.Used;

    public int ReadSamples(Span<MotionSample> dest)
    {
        if (!HasSensors) return 0;
        if (!_virtualLogged)
        {
            _virtualLogged = true;
            Log.Info($"Input: keyboard tilt keys drive the Vita's motion sensors ({_profile.Profile.MotionSource}, max {_profile.Profile.Tilt.MaxAngle:0} deg)");
        }
        return _virtual.Generate(_nowUs(), dest);
    }
}
