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
/// T is seconds ("12.5") or presented frames ("f1200"); len uses the same unit (default 0.25 s / 8 frames).
/// Frame timing is deterministic whatever the emulation speed (turbo runs).
/// </summary>
sealed class ScriptedInput : IInputBackend, IMotionSource
{
    enum Kind { Button, Tap, BackTap, LStick, RStick, Tilt }

    sealed record Step(Kind Kind, VitaButtons Button, bool Frames, double Start, double Length, float X, float Y, float X1 = float.NaN, float Y1 = float.NaN);

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
            double start = double.Parse(frames ? parts[0][1..] : parts[0], inv);
            double Len(int i) => parts.Length > i ? double.Parse(parts[i].TrimStart('f'), inv) : frames ? 8 : 0.25;
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
                case "lstick" or "rstick":
                    _steps.Add(new Step(name == "lstick" ? Kind.LStick : Kind.RStick, 0, frames, start, Len(3), float.Parse(parts[1], inv), float.Parse(parts[2], inv)));
                    break;
                default:
                    _steps.Add(new Step(Kind.Button, Enum.Parse<VitaButtons>(name, true), frames, start, Len(1), 0, 0));
                    break;
            }
        }
    }

    bool Active(Step s)
    {
        double now = s.Frames ? VitaSharp.Core.Hle.Modules.Display.FramesPresented : _clock.Elapsed.TotalSeconds;
        return now >= s.Start && now < s.Start + s.Length;
    }

    /// <summary>The touch of a tap, or the point a swipe has reached.</summary>
    TouchPoint Touch(Step s, bool back)
    {
        if (float.IsNaN(s.X1)) return new TouchPoint(back ? 2 : 1, s.X, s.Y);
        double now = s.Frames ? VitaSharp.Core.Hle.Modules.Display.FramesPresented : _clock.Elapsed.TotalSeconds;
        float t = (float)Math.Clamp((now - s.Start) / Math.Max(s.Length, 1e-6), 0, 1);
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
        return st;
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

    public bool HasSensors => _steps.Exists(s => s.Kind == Kind.Tilt);

    public int ReadSamples(Span<MotionSample> dest)
    {
        if (dest.Length == 0 || !HasSensors) return 0;
        lock (_motionLock)
        {
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
