using System.Diagnostics;
using System.Globalization;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;

namespace VitaSharp.Mobile.Core.Input;

/// <summary>
/// Scripted controller input for unattended device runs (VITASHARP_ANDROID_INPUT in env.txt; a phone may refuse
/// <c>adb shell input</c> without a developer security setting). Comma-separated steps, the desktop --input syntax
/// for buttons and sticks: <c>button@T[:len]</c> (cross, circle, square, triangle, start, select, up, down, left, right,
/// ltrigger, rtrigger), <c>lstick@T:x:y[:len]</c> / <c>rstick@...</c> (x, y in -1..1) and <c>tap@T:x:y[:len]</c> /
/// <c>btap@...</c> (front/rear touch at normalised x, y). T and len are seconds since
/// the game first polled the pad (len 0.25 s by default). Combined with the on-screen pad and controllers.
/// <c>savestate@T:slot</c> / <c>loadstate@T:slot</c> raise <see cref="StateRequested"/> once (the pause menu's
/// "Salvar estado" / "Carregar estado" for unattended save state checks).
/// <see cref="LivePath"/> adds steps while the game runs.
/// </summary>
public sealed class ScriptedPad
{
    sealed record Step(VitaButtons Button, int Stick, double Start, double Length, float X, float Y);

    List<Step> _steps = [];
    List<(bool Save, int Slot, double At)> _states = [];

    /// <summary>A <c>savestate</c>/<c>loadstate</c> step is due: (save, slot). Raised on the thread that polls the pad.</summary>
    public event Action<bool, int>? StateRequested;
    Stopwatch? _clock;
    double _nextPoll;

    /// <summary>A file polled while the game runs (VITASHARP_ANDROID_INPUT_LIVE): its steps, in the same syntax with T
    /// relative to the moment it is read, are added and the file is deleted. Lets a script on the PC react to the
    /// screen (adb push) where the phone refuses <c>adb shell input</c>.</summary>
    public string? LivePath { get; init; }

    public ScriptedPad(string script) => _steps = Parse(script, 0, _states);

    static List<Step> Parse(string script, double offset, List<(bool, int, double)> states)
    {
        var steps = new List<Step>();
        var inv = CultureInfo.InvariantCulture;
        foreach (var raw in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = raw.Split('@');
            if (at.Length != 2)
            {
                throw new FormatException($"bad input step '{raw}'");
            }

            var p = at[1].Split(':');
            double start = offset + double.Parse(p[0], inv);
            string name = at[0].ToLowerInvariant();
            if (name is "savestate" or "loadstate")
            {
                states.Add((name == "savestate", p.Length > 1 ? int.Parse(p[1], inv) : 1, start));
                continue;
            }
            if (name is "tap" or "btap")
            {
                if (p.Length < 3)
                {
                    throw new FormatException($"bad tap step '{raw}'");
                }

                steps.Add(new Step(0, name == "tap" ? 3 : 4, start, p.Length > 3 ? double.Parse(p[3], inv) : 0.25,
                    float.Parse(p[1], inv), float.Parse(p[2], inv)));
                continue;
            }
            if (name is "lstick" or "rstick")
            {
                if (p.Length < 3)
                {
                    throw new FormatException($"bad stick step '{raw}'");
                }

                steps.Add(new Step(0, name == "lstick" ? 1 : 2, start, p.Length > 3 ? double.Parse(p[3], inv) : 0.25,
                    float.Parse(p[1], inv), float.Parse(p[2], inv)));
                continue;
            }
            var button = name switch
            {
                "cross" => VitaButtons.Cross,
                "circle" => VitaButtons.Circle,
                "square" => VitaButtons.Square,
                "triangle" => VitaButtons.Triangle,
                "start" => VitaButtons.Start,
                "select" => VitaButtons.Select,
                "up" => VitaButtons.Up,
                "down" => VitaButtons.Down,
                "left" => VitaButtons.Left,
                "right" => VitaButtons.Right,
                "ltrigger" or "l" => VitaButtons.LTrigger,
                "rtrigger" or "r" => VitaButtons.RTrigger,
                _ => throw new FormatException($"unknown button '{at[0]}'"),
            };
            steps.Add(new Step(button, 0, start, p.Length > 1 ? double.Parse(p[1], inv) : 0.25, 0, 0));
        }
        return steps;
    }

    public int Count => _steps.Count;

    void PollLive(double t)
    {
        _nextPoll = t + 0.25;
        try
        {
            if (!File.Exists(LivePath))
            {
                return;
            }

            string text = File.ReadAllText(LivePath!);
            File.Delete(LivePath!);
            // copy on write: AddTouches may run on another thread
            var steps = new List<Step>(_steps);
            var states = new List<(bool, int, double)>(_states);
            steps.AddRange(Parse(text.Replace('\n', ',').Replace('\r', ','), t, states));
            _steps = steps;
            _states = states;
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { }
    }

    /// <summary>Applies the steps active now to <paramref name="s"/> (buttons OR-ed, sticks replaced).</summary>
    public ControllerState Apply(ControllerState s)
    {
        _clock ??= Stopwatch.StartNew();
        double t = _clock.Elapsed.TotalSeconds;
        if (LivePath != null && t >= _nextPoll)
        {
            PollLive(t);
        }

        if (_states.Count > 0)
        {
            FireStates(t);
        }

        foreach (var st in _steps)
        {
            if (t < st.Start || t >= st.Start + st.Length)
            {
                continue;
            }

            s.Buttons |= st.Button;
            static byte ax(float v) => (byte)Math.Clamp((int)MathF.Round(128 + v * 127), 0, 255);
            if (st.Stick == 1)
            {
                s.Lx = ax(st.X);
                s.Ly = ax(st.Y);
            }
            else if (st.Stick == 2)
            {
                s.Rx = ax(st.X);
                s.Ry = ax(st.Y);
            }
        }
        return s;
    }

    void FireStates(double t)
    {
        var due = _states.FindAll(s => s.At <= t);
        if (due.Count == 0)
        {
            return;
        }

        _states = _states.FindAll(s => s.At > t);
        foreach (var (save, slot, _) in due)
        {
            StateRequested?.Invoke(save, slot);
        }
    }

    /// <summary>Adds the touches of the tap steps active now (front: tap, rear: btap), each step its own contact.</summary>
    public void AddTouches(ref MappedInput m)
    {
        _clock ??= Stopwatch.StartNew();
        double t = _clock.Elapsed.TotalSeconds;
        for (int i = 0; i < _steps.Count; i++)
        {
            var st = _steps[i];
            if (st.Stick < 3 || t < st.Start || t >= st.Start + st.Length)
            {
                continue;
            }

            m.AddTouch(st.Stick == 4, new TouchPoint(TouchIdBase + i, st.X, st.Y));
        }
    }

    /// <summary>Host contact ids of scripted taps (above the on-screen pad's and the gamepads').</summary>
    const int TouchIdBase = 100;
}
