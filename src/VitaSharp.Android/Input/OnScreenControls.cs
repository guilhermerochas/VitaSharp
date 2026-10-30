using VitaSharp.Core.Host;
using VitaSharp.Core.Input;

namespace VitaSharp.Android.Input;

public enum OnScreenShape { Button, Stick, Toggle }

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
/// SKELETON (no Android front-end yet; platform-neutral). The translucent controls over the game view
/// (docs/android/mockups/jogo.html) and the PS button of the Vita system view (sistema.html), turned into the core's
/// <see cref="IInputBackend"/>: buttons and sticks from touches on the controls, front touch from touches on the
/// image, rear touch while the TRÁS toggle is held.
/// <para>
/// The PS button is a normal input (<see cref="VitaButtons.PsButton"/>): when the firmware's shell runs, the press
/// reaches the shell through the kernel (Display's control read calls VitaSystem.OnPsButton) and the shell shows the
/// running game's LiveArea, as on the console. When a game runs on its own there is no shell to go back to, so
/// <see cref="PsPressed"/> lets the front-end open its pause menu instead.
/// </para>
/// Remaining work: merge with the Bluetooth controller (InputMapper) per port, multi-touch on a stick that started
/// elsewhere, haptics, a layout editor (move/resize, opacity) stored with the controller profiles.
/// </summary>
public sealed class OnScreenPad : IInputBackend
{
    /// <summary>The default landscape layout, from jogo.html (844x390 dp reference screen).</summary>
    public static IReadOnlyList<OnScreenControl> DefaultLayout { get; } = BuildDefault();

    /// <summary>The Vita system view shows only the PS button (the shell is driven by front touch); the controls
    /// button of sistema.html switches to <see cref="DefaultLayout"/>.</summary>
    public static IReadOnlyList<OnScreenControl> SystemLayout { get; } =
        [new(VitaControl.PS, "PS", R(11, 0), R(308, 1), R(56, 0), R(56, 1))];

    /// <summary>Where the 960x544 image sits on the screen (fractions), set by the front-end when the surface changes.
    /// Touches inside it that hit no control become front-touch points.</summary>
    public (float X, float Y, float Width, float Height) Image { get; set; } = (R(78, 0), 0, R(688, 0), 1);

    public IReadOnlyList<OnScreenControl> Layout { get; set; } = DefaultLayout;
    public bool Visible { get; set; } = true;

    /// <summary>Raised on the UI thread on a PS button press (pressed edge).</summary>
    public event Action? PsPressed;

    readonly object _lock = new();
    ControllerState _state = ControllerState.Neutral;
    TouchPoint[] _front = [], _back = [];

    /// <summary>Feeds the current fingers (MotionEvent pointers, in fractions of the screen) from the UI thread.</summary>
    public void Update(ReadOnlySpan<(int Id, float X, float Y)> fingers)
    {
        var st = ControllerState.Neutral;
        var front = new List<TouchPoint>();
        bool psBefore, psNow, rear = false;
        lock (_lock)
        {
            psBefore = (_state.Buttons & VitaButtons.PsButton) != 0;
            foreach (var f in fingers)
            {
                var hit = Visible ? Layout.FirstOrDefault(c => c.Contains(f.X, f.Y)) : null;
                if (hit == null)
                {
                    var im = Image;
                    if (f.X >= im.X && f.X < im.X + im.Width && f.Y >= im.Y && f.Y < im.Y + im.Height)
                        front.Add(new TouchPoint(f.Id, (f.X - im.X) / im.Width, (f.Y - im.Y) / im.Height));
                    continue;
                }
                if (hit.Shape == OnScreenShape.Stick) Stick(ref st, hit, f.X, f.Y);
                else if (hit.Control == VitaControl.TouchBack) rear = true;
                else st.Buttons |= VitaControls.Button(hit.Control);
            }
            psNow = (st.Buttons & VitaButtons.PsButton) != 0;
            _state = st;
            // while TRÁS is held, image touches go to the rear touch pad instead of the screen
            if (rear) { _back = [.. front]; _front = []; }
            else { _front = [.. front]; _back = []; }
        }
        if (psNow && !psBefore) PsPressed?.Invoke();
    }

    static void Stick(ref ControllerState st, OnScreenControl c, float x, float y)
    {
        float dx = (x - (c.X + c.Width / 2)) / (c.Width / 2), dy = (y - (c.Y + c.Height / 2)) / (c.Height / 2);
        byte Axis(float v) => (byte)Math.Clamp(128 + v * 127, 0, 255);
        bool left = c.Control is VitaControl.LStickUp or VitaControl.LStickDown or VitaControl.LStickLeft or VitaControl.LStickRight;
        if (left) { st.Lx = Axis(dx); st.Ly = Axis(dy); }
        else { st.Rx = Axis(dx); st.Ry = Axis(dy); }
    }

    public ControllerState GetController(int port)
    {
        lock (_lock) return port == 0 ? _state : ControllerState.Neutral;
    }

    public IReadOnlyList<TouchPoint> GetTouches(bool back)
    {
        lock (_lock) return back ? _back : _front;
    }

    // dp of the 844x390 reference screen to a fraction (axis 0 = x, 1 = y)
    static float R(float dp, int axis) => dp / (axis == 0 ? 844f : 390f);

    static IReadOnlyList<OnScreenControl> BuildDefault() =>
    [
        new(VitaControl.L, "L", R(18, 0), R(12, 1), R(108, 0), R(40, 1)),
        new(VitaControl.R, "R", R(718, 0), R(12, 1), R(108, 0), R(40, 1)),
        new(VitaControl.Up, "▲", R(70, 0), R(92, 1), R(40, 0), R(44, 1)),
        new(VitaControl.Down, "▼", R(70, 0), R(168, 1), R(40, 0), R(44, 1)),
        new(VitaControl.Left, "◀", R(30, 0), R(132, 1), R(44, 0), R(40, 1)),
        new(VitaControl.Right, "▶", R(106, 0), R(132, 1), R(44, 0), R(40, 1)),
        new(VitaControl.Triangle, "△", R(714, 0), R(84, 1), R(50, 0), R(50, 1)),
        new(VitaControl.Square, "□", R(664, 0), R(134, 1), R(50, 0), R(50, 1)),
        new(VitaControl.Circle, "○", R(764, 0), R(134, 1), R(50, 0), R(50, 1)),
        new(VitaControl.Cross, "✕", R(714, 0), R(184, 1), R(50, 0), R(50, 1)),
        new(VitaControl.LStickUp, "", R(112, 0), R(236, 1), R(124, 0), R(124, 1), OnScreenShape.Stick),
        new(VitaControl.RStickUp, "", R(608, 0), R(236, 1), R(124, 0), R(124, 1), OnScreenShape.Stick),
        new(VitaControl.Select, "SELECT", R(322, 0), R(346, 1), R(76, 0), R(30, 1)),
        new(VitaControl.PS, "PS", R(410, 0), R(346, 1), R(30, 0), R(30, 1)),
        new(VitaControl.Start, "START", R(452, 0), R(346, 1), R(70, 0), R(30, 1)),
        new(VitaControl.TouchBack, "TRÁS", R(22, 0), R(320, 1), R(52, 0), R(36, 1), OnScreenShape.Toggle),
    ];
}
