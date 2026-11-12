using System.Diagnostics;
using System.Text.Json.Serialization;

namespace VitaSharp.Core.Input;

/// <summary>What moving the mouse does in the game window.</summary>
public enum MouseMode
{
    /// <summary>Mouse look with the middle button held: the Vita's motion sensors (<see cref="VirtualMotion"/>).</summary>
    Motion,
    /// <summary>The captured mouse moves the right stick (camera / aim in most games).</summary>
    RightStick,
    /// <summary>The captured mouse moves the left stick.</summary>
    LeftStick,
    /// <summary>The mouse only touches (left / right buttons).</summary>
    Off,
}

/// <summary>The mouse as an analog stick (keyboard profile, input.json): mode, feel and keys.</summary>
public sealed class MouseSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<MouseMode>))]
    public MouseMode Mode { get; set; } = MouseMode.Motion;
    /// <summary>Gain (0.1..5): 1 = full deflection while the mouse moves ~1500 pixels per second.</summary>
    public float Sensitivity { get; set; } = 1f;
    public bool InvertY { get; set; }
    /// <summary>The game's own stick deadzone to jump over (0..0.5): any mouse movement starts at this deflection, so
    /// small movements are not swallowed by the game.</summary>
    public float Deadzone { get; set; } = 0.15f;
    /// <summary>Response curve exponent (0.3..3): 1 = linear, above 1 finer slow movements, below 1 snappier.</summary>
    public float Curve { get; set; } = 1f;
    /// <summary>How long (ms, 10..1000) the stick takes to come back to the centre once the mouse stops.</summary>
    public float ReturnMs { get; set; } = 80f;
    /// <summary>Key (Silk.NET name) that releases the captured cursor; a click in the window captures it again.</summary>
    public string ReleaseKey { get; set; } = DefaultReleaseKey;
    /// <summary>Key that cycles the mode while a game runs (Motion, right stick, left stick, off); the choice is saved
    /// for that game in <see cref="Games"/>.</summary>
    public string ToggleKey { get; set; } = DefaultToggleKey;
    /// <summary>Title id -> mode name (<see cref="MouseMode"/>): the mode picked for that game, over <see cref="Mode"/>.</summary>
    public Dictionary<string, string> Games { get; set; } = new();

    public const string DefaultReleaseKey = "Escape", DefaultToggleKey = "F4";

    /// <summary>The mode for a game: its own choice, else the profile's.</summary>
    public MouseMode ModeFor(string? titleId)
        => titleId != null && Games.TryGetValue(titleId, out string? s) && Enum.TryParse<MouseMode>(s, true, out MouseMode mouseMode) ? mouseMode : Mode;

    public static MouseMode Next(MouseMode mouseMode) => mouseMode switch
    {
        MouseMode.Motion => MouseMode.RightStick,
        MouseMode.RightStick => MouseMode.LeftStick,
        MouseMode.LeftStick => MouseMode.Off,
        _ => MouseMode.Motion,
    };

    /// <summary>The mode's name for the launcher and the on-screen notice, in the interface language.</summary>
    public static string Label(MouseMode mouseMode) => mouseMode switch
    {
        MouseMode.Motion => Localization.Loc.Get("MouseModeMotion"),
        MouseMode.RightStick => Localization.Loc.Get("MouseModeRight"),
        MouseMode.LeftStick => Localization.Loc.Get("MouseModeLeft"),
        _ => Localization.Loc.Get("MouseModeOff"),
    };

    public MouseSettings Clone() => new()
    {
        Mode = Mode,
        Sensitivity = Sensitivity,
        InvertY = InvertY,
        Deadzone = Deadzone,
        Curve = Curve,
        ReturnMs = ReturnMs,
        ReleaseKey = ReleaseKey,
        ToggleKey = ToggleKey,
        Games = new(Games),
    };
}

/// <summary>
/// Turns relative mouse movement into a stick deflection: every pixel pushes the stick (<see cref="PixelGain"/> per
/// pixel times the sensitivity) and the stick decays back to the centre with a time constant of a third of
/// <see cref="MouseSettings.ReturnMs"/>, so a steady mouse speed holds a steady deflection proportional to it whatever
/// the poll rate, and the stick is back at rest ~ReturnMs after the mouse stops. Then the deadzone jump and the curve.
/// Thread-safe: the window thread adds movement, the emulator polls.
/// </summary>
public sealed class MouseStick
{
    private readonly object _lock = new();
    private float _ax, _ay, _x, _y;
    private long _last;

    public MouseSettings Settings { get; set; }

    /// <summary>Deflection per pixel at sensitivity 1 with ReturnMs 80: 1500 px/s * 0.0267 s * gain ~ 1.</summary>
    public const float PixelGain = 0.025f;

    /// <summary>Below this the stick reads centred: the deadzone jump would otherwise hold it off-centre while it decays.</summary>
    public const float RestThreshold = 0.02f;

    public MouseStick(MouseSettings? settings = null) => Settings = settings ?? new MouseSettings();

    public void Add(float dx, float dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        lock (_lock)
        {
            _ax += dx;
            _ay += dy;
        }
    }

    /// <summary>Drops the motion (the cursor was released, the mode changed).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _ax = _ay = _x = _y = 0;
        }
    }

    /// <summary>The deflection now as the input of the stick <paramref name="mode"/> moves (neutral in the other
    /// modes), to combine with the keyboard and controllers (<see cref="InputMapper.Combine"/>).</summary>
    public MappedInput Mapped(MouseMode mode, long nowTicks = 0)
    {
        MappedInput mappedInput = MappedInput.Neutral;
        if (mode is not (MouseMode.RightStick or MouseMode.LeftStick))
        {
            return mappedInput;
        }

        (float x, float y) = Sample(nowTicks);
        if (mode == MouseMode.RightStick)
        {
            mappedInput.Rx = x;
            mappedInput.Ry = y;
            mappedInput.State.Rx = StickMath.ToByte(x);
            mappedInput.State.Ry = StickMath.ToByte(y);
        }
        else
        {
            mappedInput.Lx = x;
            mappedInput.Ly = y;
            mappedInput.State.Lx = StickMath.ToByte(x);
            mappedInput.State.Ly = StickMath.ToByte(y);
        }
        return mappedInput;
    }

    /// <summary>The deflection now, -1..1 per axis, y down (the Vita's stick convention: up is negative).</summary>
    public (float X, float Y) Sample(long nowTicks = 0)
    {
        if (nowTicks == 0)
        {
            nowTicks = Stopwatch.GetTimestamp();
        }

        MouseSettings mouseSettings = Settings;
        lock (_lock)
        {
            double dt = _last == 0 ? 0 : Math.Max(0, (nowTicks - _last) / (double)Stopwatch.Frequency);
            _last = nowTicks;
            float tau = Math.Clamp(mouseSettings.ReturnMs, 10, 1000) / 3000f;
            float decay = (float)Math.Exp(-dt / tau);
            _x *= decay;
            _y *= decay;
            float k = PixelGain * Math.Clamp(mouseSettings.Sensitivity, 0.1f, 5f);
            _x += _ax * k;
            _y += (mouseSettings.InvertY ? -_ay : _ay) * k;
            _ax = _ay = 0;
            float mag = MathF.Sqrt(_x * _x + _y * _y);
            if (mag > 1)
            {
                _x /= mag;
                _y /= mag;
                mag = 1;
            }
            if (mag < RestThreshold)
            {
                return (0, 0); // at rest (and below ~30 px/s of jitter)
            }

            float dz = Math.Clamp(mouseSettings.Deadzone, 0, 0.5f);
            float shaped = dz + (1 - dz) * MathF.Pow(mag, Math.Clamp(mouseSettings.Curve, 0.3f, 3f));
            return (_x / mag * shaped, _y / mag * shaped);
        }
    }
}
