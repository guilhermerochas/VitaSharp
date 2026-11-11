using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Localization;

namespace VitaSharp.Mobile.Core.Input;

public enum OnScreenShape
{
    Button, Stick, Toggle
}

/// <summary>
/// One on-screen control. Rectangles are in fractions of the landscape screen (0..1) so one layout fits every phone;
/// <see cref="Stick"/> controls report the finger's offset from their centre as the stick axes.
/// </summary>
public sealed record OnScreenControl(VitaControl Control, string Label, float X, float Y, float Width, float Height,
    OnScreenShape Shape = OnScreenShape.Button)
{
    public bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// The translucent controls over the game view (docs/android/mockups/jogo.html) and the PS button of the Vita system
/// view (sistema.html), turned into the core's <see cref="IInputBackend"/> (platform-neutral; the app's
/// OnScreenControlsView draws it and feeds it the MotionEvent pointers): buttons and sticks from touches on the controls,
/// front touch from touches on the image, rear touch while the TRAS toggle is on.
/// <para>
/// A finger that starts on a stick keeps driving it wherever it moves (until it lifts), and a finger that starts on
/// the image stays a touch-screen finger, so sliding over a control does not press it.
/// </para>
/// <para>
/// The PS button is a normal input (<see cref="VitaButtons.PsButton"/>): when the firmware's shell runs, the press
/// reaches the shell through the kernel (Display's control read calls VitaSystem.OnPsButton) and the shell shows the
/// running game's LiveArea, as on the console. When a game runs on its own there is no shell to go back to, so
/// <see cref="PsPressed"/> lets the front-end open its pause menu instead.
/// </para>
/// Physical controllers are merged by <see cref="AndroidInput"/>. Remaining work: a layout editor (move/resize)
/// stored with the controller profiles.
/// </summary>
public sealed class OnScreenPad : IInputBackend
{
    /// <summary>The default landscape layout, from jogo.html (844x390 dp reference screen).</summary>
    public static IReadOnlyList<OnScreenControl> DefaultLayout { get; } = BuildDefault();

    /// <summary>The Vita system view shows only the PS button (the shell is driven by front touch); the controls
    /// button of sistema.html switches to <see cref="DefaultLayout"/>.</summary>
    public static IReadOnlyList<OnScreenControl> SystemLayout { get; } =
        [new(VitaControl.PS, "PS", DpFraction(11, 0), DpFraction(308, 1), DpFraction(56, 0), DpFraction(56, 1))];

    /// <summary>Where the 960x544 image sits on the screen (fractions), set by the front-end when the surface changes.
    /// Touches inside it that hit no control become front-touch points.</summary>
    public (float X, float Y, float Width, float Height) Image { get; set; } = (DpFraction(78, 0), 0, DpFraction(688, 0), 1);

    public IReadOnlyList<OnScreenControl> Layout { get; set; } = DefaultLayout;
    public bool Visible { get; set; } = true;

    /// <summary>TRAS: while on, touches on the image go to the rear touch pad instead of the screen.</summary>
    public bool RearTouch
    {
        get
        {
            lock (_lock)
            {
                return _rear;
            }
        }
        set
        {
            lock (_lock)
            {
                _rear = value;
            }
        }
    }

    /// <summary>Raised on the UI thread on a PS button press (pressed edge).</summary>
    public event Action? PsPressed;

    /// <summary>Raised on the UI thread when a control is newly pressed (haptics, redraw).</summary>
    public event Action<OnScreenControl>? ControlPressed;

    private readonly object _lock = new();
    private ControllerState _state = ControllerState.Neutral;
    private TouchPoint[] _front = [], _back = [];
    private bool _rear;
    // what each finger started on: a control, or null for the image / nothing
    private readonly Dictionary<int, OnScreenControl?> _captured = new();
    private readonly HashSet<OnScreenControl> _pressed = new();
    private readonly Dictionary<OnScreenControl, (float X, float Y)> _knobs = new();

    /// <summary>Controls held right now, and TRAS while it is on (drawn in the accent colour).</summary>
    public bool IsPressed(OnScreenControl c)
    {
        lock (_lock)
        {
            return _pressed.Contains(c) || c.Control == VitaControl.TouchBack && _rear;
        }
    }

    /// <summary>Offset of a stick's knob from its centre, -1..1 per axis (0,0 when released).</summary>
    public (float X, float Y) Knob(OnScreenControl c)
    {
        lock (_lock)
        {
            return _knobs.GetValueOrDefault(c);
        }
    }

    /// <summary>Feeds the current fingers (MotionEvent pointers, in fractions of the screen) from the UI thread.</summary>
    public void Update(ReadOnlySpan<(int Id, float X, float Y)> fingers)
    {
        ControllerState st = ControllerState.Neutral;
        var image = new List<TouchPoint>();
        var newlyPressed = new List<OnScreenControl>();
        bool psBefore, psNow;
        lock (_lock)
        {
            psBefore = (_state.Buttons & VitaButtons.PsButton) != 0;
            var before = new HashSet<OnScreenControl>(_pressed);
            _pressed.Clear();
            _knobs.Clear();
            var live = new HashSet<int>();
            foreach ((int Id, float X, float Y) f in fingers)
            {
                live.Add(f.Id);
                if (!_captured.TryGetValue(f.Id, out OnScreenControl? hit))
                {
                    _captured[f.Id] = hit = Visible ? Layout.FirstOrDefault(c => c.Contains(f.X, f.Y)) : null;
                }

                if (hit == null)
                {
                    (float X, float Y, float Width, float Height) im = Image;
                    if (f.X >= im.X && f.X < im.X + im.Width && f.Y >= im.Y && f.Y < im.Y + im.Height)
                    {
                        image.Add(new TouchPoint(f.Id, (f.X - im.X) / im.Width, (f.Y - im.Y) / im.Height));
                    }

                    continue;
                }
                // a button only counts while the finger is still on it; a stick follows its finger anywhere
                if (hit.Shape != OnScreenShape.Stick && !hit.Contains(f.X, f.Y))
                {
                    continue;
                }

                _pressed.Add(hit);
                if (hit.Shape == OnScreenShape.Stick)
                {
                    _knobs[hit] = Stick(ref st, hit, f.X, f.Y);
                }
                else if (hit.Shape == OnScreenShape.Button)
                {
                    st.Buttons |= VitaControls.Button(hit.Control);
                }
            }
            foreach (int id in _captured.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _captured.Remove(id);
            }

            foreach (OnScreenControl c in _pressed)
            {
                if (before.Contains(c))
                {
                    continue;
                }

                newlyPressed.Add(c);
                if (c.Shape == OnScreenShape.Toggle && c.Control == VitaControl.TouchBack)
                {
                    _rear = !_rear;
                }
            }
            psNow = (st.Buttons & VitaButtons.PsButton) != 0;
            _state = st;
            if (_rear)
            {
                _back = [.. image];
                _front = [];
            }
            else
            {
                _front = [.. image];
                _back = [];
            }
        }
        foreach (OnScreenControl c in newlyPressed)
        {
            ControlPressed?.Invoke(c);
        }

        if (psNow && !psBefore)
        {
            PsPressed?.Invoke();
        }
    }

    /// <summary>Releases every finger (the activity lost focus or the overlay was hidden).</summary>
    public void Reset() => Update([]);

    private static (float, float) Stick(ref ControllerState st, OnScreenControl c, float x, float y)
    {
        float dx = (x - (c.X + c.Width / 2)) / (c.Width / 2), dy = (y - (c.Y + c.Height / 2)) / (c.Height / 2);
        // a finger outside the ring pushes the stick to its edge in that direction
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len > 1)
        {
            dx /= len;
            dy /= len;
        }
        static byte Axis(float v) => (byte)Math.Clamp(128 + v * 127, 0, 255);
        bool left = c.Control is VitaControl.LStickUp or VitaControl.LStickDown or VitaControl.LStickLeft or VitaControl.LStickRight;
        if (left)
        {
            st.Lx = Axis(dx);
            st.Ly = Axis(dy);
        }
        else
        {
            st.Rx = Axis(dx);
            st.Ry = Axis(dy);
        }
        return (dx, dy);
    }

    public ControllerState GetController(int port)
    {
        lock (_lock)
        {
            return port == 0 ? _state : ControllerState.Neutral;
        }
    }

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        lock (_lock)
        {
            return back ? _back : _front;
        }
    }

    // dp of the 844x390 reference screen to a fraction (axis 0 = x, 1 = y)
    private static float DpFraction(float dp, int axis) => dp / (axis == 0 ? 844f : 390f);

    private static IReadOnlyList<OnScreenControl> BuildDefault() =>
    [
        new(VitaControl.L, "L", DpFraction(18, 0), DpFraction(12, 1), DpFraction(108, 0), DpFraction(40, 1)),
        new(VitaControl.R, "R", DpFraction(718, 0), DpFraction(12, 1), DpFraction(108, 0), DpFraction(40, 1)),
        new(VitaControl.Up, "\u25B2", DpFraction(70, 0), DpFraction(92, 1), DpFraction(40, 0), DpFraction(44, 1)),
        new(VitaControl.Down, "\u25BC", DpFraction(70, 0), DpFraction(168, 1), DpFraction(40, 0), DpFraction(44, 1)),
        new(VitaControl.Left, "\u25C0", DpFraction(30, 0), DpFraction(132, 1), DpFraction(44, 0), DpFraction(40, 1)),
        new(VitaControl.Right, "\u25B6", DpFraction(106, 0), DpFraction(132, 1), DpFraction(44, 0), DpFraction(40, 1)),
        new(VitaControl.Triangle, "\u25B3", DpFraction(714, 0), DpFraction(84, 1), DpFraction(50, 0), DpFraction(50, 1)),
        new(VitaControl.Square, "\u25A1", DpFraction(664, 0), DpFraction(134, 1), DpFraction(50, 0), DpFraction(50, 1)),
        new(VitaControl.Circle, "\u25CB", DpFraction(764, 0), DpFraction(134, 1), DpFraction(50, 0), DpFraction(50, 1)),
        new(VitaControl.Cross, "\u2715", DpFraction(714, 0), DpFraction(184, 1), DpFraction(50, 0), DpFraction(50, 1)),
        new(VitaControl.LStickUp, "", DpFraction(112, 0), DpFraction(236, 1), DpFraction(124, 0), DpFraction(124, 1), OnScreenShape.Stick),
        new(VitaControl.RStickUp, "", DpFraction(608, 0), DpFraction(236, 1), DpFraction(124, 0), DpFraction(124, 1), OnScreenShape.Stick),
        new(VitaControl.Select, "SELECT", DpFraction(322, 0), DpFraction(346, 1), DpFraction(76, 0), DpFraction(30, 1)),
        new(VitaControl.PS, "PS", DpFraction(410, 0), DpFraction(346, 1), DpFraction(30, 0), DpFraction(30, 1)),
        new(VitaControl.Start, "START", DpFraction(452, 0), DpFraction(346, 1), DpFraction(70, 0), DpFraction(30, 1)),
        new(VitaControl.TouchBack, Loc.Get("MRearButton"), DpFraction(22, 0), DpFraction(320, 1), DpFraction(52, 0), DpFraction(36, 1), OnScreenShape.Toggle),
    ];
}
