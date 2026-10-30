using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;

namespace VitaSharp.Desktop;

/// <summary>
/// Input script for automated runs, comma separated steps:
///   button@T[:len]          press a button (cross, circle, square, triangle, start, select, up, down, left, right,
///                           ltrigger, rtrigger)
///   tap@T:x:y[:len]         touch the front panel at normalised x, y (btap: the rear touch panel)
///   swipe@T:x0:y0:x1:y1[:len]  drag a finger on the front panel from x0, y0 to x1, y1 (default 0.5 s / 30 frames)
///   lstick@T:x:y[:len]      hold the left stick at x, y in -1..1 (rstick for the right stick)
///   tilt@T:pitch:roll[:len] motion sensors: the Vita (lying flat, screen up, by default) tilted pitch degrees about
///                           x (positive raises the top edge) then roll degrees about y (positive lowers the right
///                           edge); it turns there at up to 180 degrees/s and back to flat afterwards. Any tilt step
///                           makes the script the SceMotion source.
///   key@T:Name[:len]        hold a keyboard key (Silk.NET key name: "Keypad8", "T", "Space"...) through the default
///                           keyboard profile: Vita buttons and sticks, and the virtual motion's tilt keys
///                           (<see cref="VirtualMotion"/>, which then becomes the SceMotion source when no tilt step
///                           exists)
///   mlook@T:dx:dy[:len]     mouse look: the mouse moves dx, dy pixels over len with the middle button held
/// T is seconds ("12.5") or presented frames ("f1200"); len uses the same unit (default 0.25 s / 8 frames).
/// Frame timing is deterministic whatever the emulation speed (turbo runs). "f1200+3" is 3 seconds of wall time after
/// frame 1200 was presented (len in seconds): steps that must happen while the emulation is paused, when no frame
/// comes. A key step on the pause hotkey (P by default) pauses or resumes the emulation once, when it starts, and one
/// on F10 takes a diagnostic capture (<see cref="TakeHostKeys"/>), e.g. "key@f300:P,key@f300+3:F10,key@f300+5:P"
/// pauses at frame 300, captures the paused frame and resumes 5 seconds after the pause.
/// </summary>
sealed class ScriptedInput : IInputBackend, IMotionSource
{
    enum Kind { Button, Tap, BackTap, LStick, RStick, Tilt, Key, MouseLook }

    /// <param name="AfterSeconds">"fN+S": the step starts S wall-clock seconds after frame N (NaN: plain timing).</param>
    sealed record Step(Kind Kind, VitaButtons Button, bool Frames, double Start, double Length, float X, float Y, float X1 = float.NaN, float Y1 = float.NaN, string? KeyName = null)
    {
        public double AfterSeconds { get; init; } = double.NaN;
    }

    readonly HashSet<Step> _hostKeysFired = new();
    /// <summary>"fN+S" steps: the script clock when frame N was first seen.</summary>
    readonly Dictionary<Step, double> _frameSeenAt = new();

    readonly CompiledProfile _keyboard = new(DefaultMappings.Keyboard());
    readonly VirtualMotion _virtual = new();
    readonly Dictionary<Step, float> _mouseDone = new();

    readonly List<Step> _steps = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();

    public ScriptedInput(string script)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var raw in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = raw.Split('@');
            if (at.Length != 2) throw new FormatException($"bad input step '{raw}'");
            var parts = at[1].Split(':');
            bool frames = parts[0].StartsWith('f');
            string when = parts[0];
            double after = double.NaN;
            if (frames && when.IndexOf('+') is > 0 and var plus)
            {
                after = double.Parse(when[(plus + 1)..], inv);
                when = when[..plus];
            }
            double start = double.Parse(frames ? when[1..] : when, inv);
            bool lenFrames = frames && double.IsNaN(after);
            double Len(int i) => parts.Length > i ? double.Parse(parts[i].TrimStart('f'), inv) : lenFrames ? 8 : 0.25;
            string name = at[0].ToLowerInvariant();
            switch (name)
            {
                case "tap" or "btap":
                    _steps.Add(new Step(name == "tap" ? Kind.Tap : Kind.BackTap, 0, frames, start, Len(3), float.Parse(parts[1], inv), float.Parse(parts[2], inv)));
                    break;
                case "swipe":
                    _steps.Add(new Step(Kind.Tap, 0, frames, start, parts.Length > 5 ? Len(5) : frames ? 30 : 0.5, float.Parse(parts[1], inv), float.Parse(parts[2], inv),
                        float.Parse(parts[3], inv), float.Parse(parts[4], inv)));
                    break;
                case "tilt":
                    _steps.Add(new Step(Kind.Tilt, 0, frames, start, Len(3), float.Parse(parts[1], inv), float.Parse(parts[2], inv)));
                    break;
                case "key":
                    _steps.Add(new Step(Kind.Key, 0, frames, start, Len(2), 0, 0, KeyName: parts[1]));
                    break;
                case "mlook":
                    _steps.Add(new Step(Kind.MouseLook, 0, frames, start, Len(3), float.Parse(parts[1], inv), float.Parse(parts[2], inv)));
                    break;
                case "lstick" or "rstick":
                    _steps.Add(new Step(name == "lstick" ? Kind.LStick : Kind.RStick, 0, frames, start, Len(3), float.Parse(parts[1], inv), float.Parse(parts[2], inv)));
                    break;
                default:
                    _steps.Add(new Step(Kind.Button, Enum.Parse<VitaButtons>(name, true), frames, start, Len(1), 0, 0));
                    break;
            }
            if (!double.IsNaN(after)) _steps[^1] = _steps[^1] with { AfterSeconds = after };
        }
    }

    /// <summary>Where the step's timeline is now and where it starts, in the step's unit.</summary>
    (double Now, double Start) Timeline(Step s)
    {
        if (double.IsNaN(s.AfterSeconds))
            return (s.Frames ? VitaSharp.Core.Hle.Modules.Display.FramesPresented : _clock.Elapsed.TotalSeconds, s.Start);
        double wall = _clock.Elapsed.TotalSeconds;
        double seen;
        lock (_frameSeenAt)
        {
            if (!_frameSeenAt.TryGetValue(s, out seen))
            {
                if (VitaSharp.Core.Hle.Modules.Display.FramesPresented < s.Start) return (wall, double.PositiveInfinity);
                _frameSeenAt[s] = seen = wall;
            }
        }
        return (wall, seen + s.AfterSeconds);
    }

    bool Active(Step s)
    {
        var (now, start) = Timeline(s);
        return now >= start && now < start + s.Length;
    }

    /// <summary>Name <see cref="TakeHostKeys"/> reports for a step on the pause hotkey.</summary>
    public const string PauseKey = "Pause";

    /// <summary>
    /// Emulator keys of the script that have started since the last call, in order: <see cref="PauseKey"/> for a step on
    /// the pause hotkey of the default keyboard profile (each toggles the pause once), "F10" for the diagnostic capture.
    /// The front-end polls this, also while the paused guest polls no input.
    /// </summary>
    public List<string>? TakeHostKeys()
    {
        List<string>? keys = null;
        foreach (var s in _steps)
        {
            if (s.Kind != Kind.Key || !IsHostKey(s.KeyName)) continue;
            var (now, start) = Timeline(s);
            if (now >= start && _hostKeysFired.Add(s)) (keys ??= new()).Add(IsPauseKey(s.KeyName) ? PauseKey : s.KeyName!);
        }
        return keys;
    }

    bool IsHostKey(string? key) => key == "F10" || IsPauseKey(key);

    bool IsPauseKey(string? key)
    {
        foreach (ref readonly var b in _keyboard[VitaControl.PauseEmulation])
            if (b.Kind == BindingKind.Key && b.Key == key) return true;
        return false;
    }

    /// <summary>The script has emulator key steps (the front-end then polls <see cref="TakeHostKeys"/>).</summary>
    public bool HasHostKeySteps => _steps.Exists(s => s.Kind == Kind.Key && IsHostKey(s.KeyName));

    /// <summary>The touch of a tap, or the point a swipe has reached.</summary>
    TouchPoint Touch(Step s, bool back)
    {
        if (float.IsNaN(s.X1)) return new TouchPoint(back ? 2 : 1, s.X, s.Y);
        var (now, start) = Timeline(s);
        float t = (float)Math.Clamp((now - start) / Math.Max(s.Length, 1e-6), 0, 1);
        return new TouchPoint(back ? 2 : 1, s.X + (s.X1 - s.X) * t, s.Y + (s.Y1 - s.Y) * t);
    }

    static byte Axis(float v) => (byte)Math.Clamp((int)MathF.Round((v + 1) * 127.5f), 0, 255);

    public ControllerState GetController(int port)
    {
        var st = ControllerState.Neutral;
        foreach (var s in _steps)
        {
            if (!Active(s)) continue;
            switch (s.Kind)
            {
                case Kind.Button: st.Buttons |= s.Button; break;
                case Kind.LStick: st.Lx = Axis(s.X); st.Ly = Axis(s.Y); break;
                case Kind.RStick: st.Rx = Axis(s.X); st.Ry = Axis(s.Y); break;
            }
        }
        if (HeldKeys() is { } keys)
        {
            var m = InputMapper.Map(_keyboard, null, keys);
            st.Buttons |= m.State.Buttons;
            if (m.Lx != 0 || m.Ly != 0) { st.Lx = m.State.Lx; st.Ly = m.State.Ly; }
            if (m.Rx != 0 || m.Ry != 0) { st.Rx = m.State.Rx; st.Ry = m.State.Ry; }
        }
        return st;
    }

    /// <summary>Keys of the active key steps; null when none is held.</summary>
    HashSet<string>? HeldKeys()
    {
        HashSet<string>? keys = null;
        foreach (var s in _steps)
            if (s.Kind == Kind.Key && Active(s)) (keys ??= new()).Add(s.KeyName!);
        return keys;
    }

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        var kind = back ? Kind.BackTap : Kind.Tap;
        foreach (var s in _steps)
            if (s.Kind == kind && Active(s)) return [Touch(s, back)];
        return [];
    }

    // ------------------------------------------------------------------ motion

    readonly object _motionLock = new();
    long _motionLastUs = -1;
    float _pitch, _roll; // radians, current (ramped towards the active step)

    bool HasTiltSteps => _steps.Exists(s => s.Kind == Kind.Tilt);

    public bool HasSensors
    {
        get
        {
            if (HasTiltSteps) return true;
            lock (_motionLock) { UpdateVirtual(); return _virtual.Used; }
        }
    }

    /// <summary>Feeds the held keys and the mouse-look steps' progress to the virtual motion. Under _motionLock.</summary>
    void UpdateVirtual()
    {
        _virtual.SetControls(_keyboard, null, HeldKeys());
        foreach (var s in _steps)
        {
            if (s.Kind != Kind.MouseLook) continue;
            var (now, start) = Timeline(s);
            float t = (float)Math.Clamp((now - start) / Math.Max(s.Length, 1e-6), 0, 1);
            float done = _mouseDone.GetValueOrDefault(s);
            if (t <= done) continue;
            _virtual.AddMouse(s.X * (t - done), s.Y * (t - done));
            _mouseDone[s] = t;
        }
    }

    public int ReadSamples(Span<MotionSample> dest)
    {
        if (dest.Length == 0 || !HasSensors) return 0;
        lock (_motionLock)
        {
            if (!HasTiltSteps)
            {
                UpdateVirtual();
                return _virtual.Generate((long)(_clock.Elapsed.TotalMilliseconds * 1000), dest);
            }
            long now = (long)(_clock.Elapsed.TotalMilliseconds * 1000);
            if (_motionLastUs >= 0 && now - _motionLastUs < 2000) return 0; // ~500 Hz at most
            float dt = _motionLastUs < 0 ? 0 : (now - _motionLastUs) / 1e6f;
            _motionLastUs = now;
            float tp = 0, tr = 0;
            foreach (var s in _steps)
                if (s.Kind == Kind.Tilt && Active(s)) { tp = s.X * MathF.PI / 180; tr = s.Y * MathF.PI / 180; }
            float max = MathF.PI * dt; // 180 degrees/s
            float np = _pitch + Math.Clamp(tp - _pitch, -max, max), nr = _roll + Math.Clamp(tr - _roll, -max, max);
            var gyro = dt > 0 ? new Vector3((np - _pitch) / dt, (nr - _roll) / dt, 0) : Vector3.Zero;
            _pitch = np;
            _roll = nr;
            dest[0] = new MotionSample((ulong)now + 1, MotionAxes.TiltGravity(_pitch, _roll), gyro);
            return 1;
        }
    }

    public int GetTouches(bool back, Span<TouchPoint> dest)
    {
        var kind = back ? Kind.BackTap : Kind.Tap;
        if (dest.Length == 0) return 0;
        foreach (var s in _steps)
            if (s.Kind == kind && Active(s)) { dest[0] = Touch(s, back); return 1; }
        return 0;
    }
}
