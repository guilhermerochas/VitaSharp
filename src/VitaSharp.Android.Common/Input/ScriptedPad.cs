using System.Diagnostics;
using System.Globalization;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;

namespace VitaSharp.Android.Input;

/// <summary>
/// Scripted controller input for unattended device runs (VITASHARP_ANDROID_INPUT in env.txt; a phone may refuse
/// <c>adb shell input</c> without a developer security setting). Comma-separated steps, the desktop --input syntax
/// for buttons and sticks: <c>button@T[:len]</c> (cross, circle, square, triangle, start, select, up, down, left, right,
/// ltrigger, rtrigger), <c>lstick@T:x:y[:len]</c> / <c>rstick@...</c> (x, y in -1..1) and <c>tap@T:x:y[:len]</c> /
/// <c>btap@...</c> (front/rear touch at normalised x, y). T and len are seconds since
/// the game first polled the pad (len 0.25 s by default). Combined with the on-screen pad and controllers.
/// </summary>
public sealed class ScriptedPad
{
    sealed record Step(VitaButtons Button, int Stick, double Start, double Length, float X, float Y);

    readonly List<Step> _steps = [];
    Stopwatch? _clock;

    public ScriptedPad(string script)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var raw in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = raw.Split('@');
            if (at.Length != 2) throw new FormatException($"bad input step '{raw}'");
            var p = at[1].Split(':');
            double start = double.Parse(p[0], inv);
            string name = at[0].ToLowerInvariant();
            if (name is "tap" or "btap")
            {
                if (p.Length < 3) throw new FormatException($"bad tap step '{raw}'");
                _steps.Add(new Step(0, name == "tap" ? 3 : 4, start, p.Length > 3 ? double.Parse(p[3], inv) : 0.25,
                    float.Parse(p[1], inv), float.Parse(p[2], inv)));
                continue;
            }
            if (name is "lstick" or "rstick")
            {
                if (p.Length < 3) throw new FormatException($"bad stick step '{raw}'");
                _steps.Add(new Step(0, name == "lstick" ? 1 : 2, start, p.Length > 3 ? double.Parse(p[3], inv) : 0.25,
                    float.Parse(p[1], inv), float.Parse(p[2], inv)));
                continue;
            }
            var button = name switch
            {
                "cross" => VitaButtons.Cross, "circle" => VitaButtons.Circle, "square" => VitaButtons.Square, "triangle" => VitaButtons.Triangle,
                "start" => VitaButtons.Start, "select" => VitaButtons.Select, "up" => VitaButtons.Up, "down" => VitaButtons.Down,
                "left" => VitaButtons.Left, "right" => VitaButtons.Right, "ltrigger" or "l" => VitaButtons.LTrigger, "rtrigger" or "r" => VitaButtons.RTrigger,
                _ => throw new FormatException($"unknown button '{at[0]}'"),
            };
            _steps.Add(new Step(button, 0, start, p.Length > 1 ? double.Parse(p[1], inv) : 0.25, 0, 0));
        }
    }

    public int Count => _steps.Count;

    /// <summary>Applies the steps active now to <paramref name="s"/> (buttons OR-ed, sticks replaced).</summary>
    public ControllerState Apply(ControllerState s)
    {
        _clock ??= Stopwatch.StartNew();
        double t = _clock.Elapsed.TotalSeconds;
        foreach (var st in _steps)
        {
            if (t < st.Start || t >= st.Start + st.Length) continue;
            s.Buttons |= st.Button;
            byte ax(float v) => (byte)Math.Clamp((int)MathF.Round(128 + v * 127), 0, 255);
            if (st.Stick == 1) { s.Lx = ax(st.X); s.Ly = ax(st.Y); }
            else if (st.Stick == 2) { s.Rx = ax(st.X); s.Ry = ax(st.Y); }
        }
        return s;
    }

    /// <summary>Adds the touches of the tap steps active now (front: tap, rear: btap), each step its own contact.</summary>
    public void AddTouches(ref MappedInput m)
    {
        _clock ??= Stopwatch.StartNew();
        double t = _clock.Elapsed.TotalSeconds;
        for (int i = 0; i < _steps.Count; i++)
        {
            var st = _steps[i];
            if (st.Stick < 3 || t < st.Start || t >= st.Start + st.Length) continue;
            m.AddTouch(st.Stick == 4, new TouchPoint(TouchIdBase + i, st.X, st.Y));
        }
    }

    /// <summary>Host contact ids of scripted taps (above the on-screen pad's and the gamepads').</summary>
    const int TouchIdBase = 100;
}
