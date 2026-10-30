using VitaSharp.Core.Host;
using VitaSharp.Core.Input;

namespace VitaSharp.Android.Input;

/// <summary>Android key codes and motion axes of game controllers (android.view.KeyEvent / MotionEvent constants).</summary>
public static class AndroidKeys
{
    public const int Back = 4, DpadUp = 19, DpadDown = 20, DpadLeft = 21, DpadRight = 22, DpadCenter = 23;
    public const int ButtonA = 96, ButtonB = 97, ButtonC = 98, ButtonX = 99, ButtonY = 100, ButtonZ = 101;
    public const int ButtonL1 = 102, ButtonR1 = 103, ButtonL2 = 104, ButtonR2 = 105, ButtonThumbL = 106, ButtonThumbR = 107;
    public const int ButtonStart = 108, ButtonSelect = 109, ButtonMode = 110, MediaRecord = 130;

    public const int AxisX = 0, AxisY = 1, AxisZ = 11, AxisRx = 12, AxisRy = 13, AxisRz = 14, AxisHatX = 15, AxisHatY = 16;
    public const int AxisLTrigger = 17, AxisRTrigger = 18, AxisGas = 22, AxisBrake = 23;

    /// <summary>The standard-layout button of a key code (positional, like SDL: A = South = Cross), or null.</summary>
    public static PadButton? Button(int keyCode) => keyCode switch
    {
        ButtonA => PadButton.South,
        ButtonB => PadButton.East,
        ButtonX => PadButton.West,
        ButtonY => PadButton.North,
        ButtonL1 => PadButton.LeftShoulder,
        ButtonR1 => PadButton.RightShoulder,
        ButtonThumbL => PadButton.LeftStick,
        ButtonThumbR => PadButton.RightStick,
        ButtonStart => PadButton.Start,
        ButtonSelect or Back => PadButton.Back,
        ButtonMode => PadButton.Guide,
        DpadUp => PadButton.DpadUp,
        DpadDown => PadButton.DpadDown,
        DpadLeft => PadButton.DpadLeft,
        DpadRight => PadButton.DpadRight,
        MediaRecord => PadButton.Misc1,
        _ => null,
    };

    /// <summary>Digital L2/R2 (pads without analog triggers): the trigger axes at 0 or 1.</summary>
    public static PadAxis? TriggerKey(int keyCode) => keyCode switch
    {
        ButtonL2 => PadAxis.LeftTrigger,
        ButtonR2 => PadAxis.RightTrigger,
        _ => null,
    };
}

/// <summary>Which axes a controller reports (InputDevice.GetMotionRange), for the right stick and triggers.</summary>
/// <param name="RightStickOnZ">The right stick is AXIS_Z/AXIS_RZ (the Android standard: DualShock 4, DualSense, most
/// Bluetooth pads); false = AXIS_RX/AXIS_RY (some Xbox/USB pads).</param>
/// <param name="TriggersOnBrakeGas">The triggers report AXIS_BRAKE/AXIS_GAS instead of AXIS_LTRIGGER/RTRIGGER.</param>
public readonly record struct AndroidPadLayout(bool RightStickOnZ = true, bool TriggersOnBrakeGas = false)
{
    public static AndroidPadLayout FromAxes(IReadOnlyCollection<int> axes) => new(
        RightStickOnZ: axes.Contains(AndroidKeys.AxisZ) && axes.Contains(AndroidKeys.AxisRz) || !(axes.Contains(AndroidKeys.AxisRx) && axes.Contains(AndroidKeys.AxisRy)),
        TriggersOnBrakeGas: !axes.Contains(AndroidKeys.AxisLTrigger) && axes.Contains(AndroidKeys.AxisBrake));
}

/// <summary>
/// Physical controllers on Android (Bluetooth or USB, InputDevice with SOURCE_GAMEPAD/SOURCE_JOYSTICK): key and motion
/// events become a <see cref="PadSnapshot"/> in the standard gamepad layout, mapped through the same controller
/// profiles as the desktop (<see cref="InputProfileStore"/>, defaults from <see cref="DefaultMappings.Gamepad"/>: A/Cross
/// positional, triggers also L/R, Mode = PS). Thread-safe: events come from the UI thread, the guest polls from its own.
/// </summary>
public sealed class AndroidGamepads
{
    sealed class Device(int id, string name, ControllerKind kind, AndroidPadLayout layout, CompiledProfile profile)
    {
        public readonly int Id = id;
        public readonly string Name = name;
        public readonly ControllerKind Kind = kind;
        public readonly AndroidPadLayout Layout = layout;
        public CompiledProfile Profile = profile;
        public readonly PadSnapshot Pad = new() { IsGamepad = true };
        public float HatX, HatY;
        public readonly bool[] KeyDpad = new bool[4];
        // L2/R2 as keys (digital) and as axes (analog); the trigger is the larger of the two
        public readonly float[] KeyTrigger = new float[2], AxisTrigger = new float[2];
    }

    readonly object _lock = new();
    readonly Dictionary<int, Device> _devices = new();
    readonly InputProfileStore? _profiles;

    public AndroidGamepads(InputProfileStore? profiles = null) => _profiles = profiles;

    /// <summary>Raised (on the caller's thread) when a controller connects or disconnects.</summary>
    public event Action? DevicesChanged;

    /// <summary>Connected controllers (id, name, family), in connection order.</summary>
    public IReadOnlyList<(int Id, string Name, ControllerKind Kind)> Devices
    {
        get { lock (_lock) return _devices.Values.Select(d => (d.Id, d.Name, d.Kind)).ToList(); }
    }

    public bool AnyConnected { get { lock (_lock) return _devices.Count > 0; } }

    /// <summary>The device is a connected controller (its key events are controller keys, not keyboard keys).</summary>
    public bool IsConnected(int deviceId) { lock (_lock) return _devices.ContainsKey(deviceId); }

    /// <summary>An SDL 2 style GUID from the USB/Bluetooth vendor and product ids, so <see cref="ControllerClassifier"/>
    /// and saved profiles work like on the desktop.</summary>
    public static string Guid(int vendorId, int productId)
    {
        static string Le16(int v) => $"{v & 0xFF:x2}{(v >> 8) & 0xFF:x2}";
        return $"03000000{Le16(vendorId)}0000{Le16(productId)}000000000000";
    }

    /// <summary>Adds (or refreshes) a controller; <paramref name="axes"/> are the axes InputDevice.MotionRanges lists.</summary>
    public void Connect(int deviceId, string name, int vendorId, int productId, IReadOnlyCollection<int> axes)
    {
        string guid = Guid(vendorId, productId);
        var kind = ControllerClassifier.Classify(guid, name);
        var profile = _profiles?.ProfileFor(guid, name, kind, isGamepad: true) ?? DefaultMappings.Gamepad(guid, name, kind);
        lock (_lock) _devices[deviceId] = new Device(deviceId, name, kind, AndroidPadLayout.FromAxes(axes), new CompiledProfile(profile));
        DevicesChanged?.Invoke();
    }

    public void Disconnect(int deviceId)
    {
        bool removed;
        lock (_lock) removed = _devices.Remove(deviceId);
        if (removed) DevicesChanged?.Invoke();
    }

    /// <summary>Uses <paramref name="profile"/> for every connected controller of that id (after the Controles screen
    /// saved it).</summary>
    public void ApplyProfile(ControllerProfile profile)
    {
        lock (_lock)
            foreach (var d in _devices.Values)
                d.Profile = new CompiledProfile(profile);
    }

    /// <summary>A key event of a controller. Returns true when the key is a controller key (consume the event).</summary>
    public bool OnKey(int deviceId, int keyCode, bool down)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var d)) return false;
            if (AndroidKeys.TriggerKey(keyCode) is { } t)
            {
                d.KeyTrigger[t - PadAxis.LeftTrigger] = down ? 1f : 0f;
                UpdateTriggers(d);
                return true;
            }
            if (AndroidKeys.Button(keyCode) is not { } b) return false;
            if (b is >= PadButton.DpadUp and <= PadButton.DpadRight) d.KeyDpad[b - PadButton.DpadUp] = down;
            else d.Pad.Buttons[(int)b] = down;
            UpdateDpad(d);
            return true;
        }
    }

    /// <summary>A joystick motion event: <paramref name="axisValue"/> returns MotionEvent.GetAxisValue(axis).</summary>
    public bool OnMotion(int deviceId, Func<int, float> axisValue)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var d)) return false;
            var a = d.Pad.Axes;
            a[(int)PadAxis.LeftX] = axisValue(AndroidKeys.AxisX);
            a[(int)PadAxis.LeftY] = axisValue(AndroidKeys.AxisY);
            a[(int)PadAxis.RightX] = axisValue(d.Layout.RightStickOnZ ? AndroidKeys.AxisZ : AndroidKeys.AxisRx);
            a[(int)PadAxis.RightY] = axisValue(d.Layout.RightStickOnZ ? AndroidKeys.AxisRz : AndroidKeys.AxisRy);
            d.AxisTrigger[0] = axisValue(d.Layout.TriggersOnBrakeGas ? AndroidKeys.AxisBrake : AndroidKeys.AxisLTrigger);
            d.AxisTrigger[1] = axisValue(d.Layout.TriggersOnBrakeGas ? AndroidKeys.AxisGas : AndroidKeys.AxisRTrigger);
            UpdateTriggers(d);
            d.HatX = axisValue(AndroidKeys.AxisHatX);
            d.HatY = axisValue(AndroidKeys.AxisHatY);
            UpdateDpad(d);
            return true;
        }
    }

    // pads with analog triggers often report L2/R2 as keys too: keep whichever is pressed further
    static void UpdateTriggers(Device d)
    {
        d.Pad.Axes[(int)PadAxis.LeftTrigger] = Math.Clamp(Math.Max(d.KeyTrigger[0], d.AxisTrigger[0]), 0f, 1f);
        d.Pad.Axes[(int)PadAxis.RightTrigger] = Math.Clamp(Math.Max(d.KeyTrigger[1], d.AxisTrigger[1]), 0f, 1f);
    }

    static void UpdateDpad(Device d)
    {
        var b = d.Pad.Buttons;
        b[(int)PadButton.DpadUp] = d.KeyDpad[0] || d.HatY < -0.5f;
        b[(int)PadButton.DpadDown] = d.KeyDpad[1] || d.HatY > 0.5f;
        b[(int)PadButton.DpadLeft] = d.KeyDpad[2] || d.HatX < -0.5f;
        b[(int)PadButton.DpadRight] = d.KeyDpad[3] || d.HatX > 0.5f;
    }

    /// <summary>Every connected controller mapped and combined (they all drive the one Vita).</summary>
    public MappedInput Map()
    {
        lock (_lock)
        {
            var r = MappedInput.Neutral;
            bool first = true;
            foreach (var d in _devices.Values)
            {
                var m = InputMapper.Map(d.Profile, d.Pad, null);
                r = first ? m : InputMapper.Combine(r, m);
                first = false;
            }
            return r;
        }
    }
}

/// <summary>
/// The core's <see cref="IInputBackend"/> on Android: the on-screen pad, every physical controller and a hardware
/// keyboard combined (buttons OR-ed, sticks taking the value furthest from the centre, touches of all).
/// </summary>
public sealed class AndroidInput(OnScreenPad pad, AndroidGamepads gamepads, AndroidKeyboard? keyboard = null) : IInputBackend
{
    public OnScreenPad Pad { get; } = pad;
    public AndroidGamepads Gamepads { get; } = gamepads;
    public AndroidKeyboard? Keyboard { get; } = keyboard;

    MappedInput Current()
    {
        var touch = new MappedInput { State = Pad.GetController(0) };
        foreach (var t in Pad.GetTouches(false)) touch.AddTouch(false, t);
        foreach (var t in Pad.GetTouches(true)) touch.AddTouch(true, t);
        var m = Gamepads.AnyConnected ? InputMapper.Combine(touch, Gamepads.Map()) : touch;
        // keyboard contacts (TouchFront/TouchBack keys) get ids of their own
        return Keyboard is { AnyHeld: true } k ? InputMapper.Combine(m, k.Map(), 200) : m;
    }

    public ControllerState GetController(int port) => port == 0 ? Current().State : ControllerState.Neutral;

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        var m = Current();
        var list = new TouchPoint[back ? m.BackCount : m.FrontCount];
        m.CopyTouches(back, list);
        return list;
    }

    public int GetTouches(bool back, Span<TouchPoint> dest) => Current().CopyTouches(back, dest);
}
