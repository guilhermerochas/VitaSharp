using System.Diagnostics;
using System.Globalization;
using VitaSharp.Core.Host;

namespace VitaSharp.Desktop;

/// <summary>
/// Input script for automated runs, comma separated steps:
///   button@T[:len]          press a button (cross, circle, square, triangle, start, select, up, down, left, right,
///                           ltrigger, rtrigger)
///   tap@T:x:y[:len]         touch the front panel at normalised x, y (btap: the rear touch panel)
///   lstick@T:x:y[:len]      hold the left stick at x, y in -1..1 (rstick for the right stick)
/// T is seconds ("12.5") or presented frames ("f1200"); len uses the same unit (default 0.25 s / 8 frames).
/// Frame timing is deterministic whatever the emulation speed (turbo runs).
/// </summary>
sealed class ScriptedInput : IInputBackend
{
    enum Kind { Button, Tap, BackTap, LStick, RStick }

    sealed record Step(Kind Kind, VitaButtons Button, bool Frames, double Start, double Length, float X, float Y);

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
            if (s.Kind == kind && Active(s)) return [new TouchPoint(back ? 2 : 1, s.X, s.Y)];
        return [];
    }
}
