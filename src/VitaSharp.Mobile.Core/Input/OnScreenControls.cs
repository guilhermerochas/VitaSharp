using System.Runtime.InteropServices;
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
    /// <summary>The key of a translated label (<see cref="Loc"/>): the text is looked up when drawn, so it follows the
    /// app's language, also after it changes (the layouts are built once per process).</summary>
    public string? LabelKey { get; init; }

    /// <summary>The label to draw: the translated <see cref="LabelKey"/> in the current language, else
    /// <see cref="Label"/> (symbols and names that are the same in every language).</summary>
    public string Text => LabelKey is { } key ? Loc.Get(key) : Label;

    public bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

    /// <summary>How far past its drawn edge a round control still takes a finger: a tenth of its radius.</summary>
    public const float RoundMargin = 0.1f;

    /// <summary>Drawn round (OnScreenControlsView): the sticks, the face buttons and PS. The others are rectangles.</summary>
    public bool IsRound => Shape == OnScreenShape.Stick
        || Control is VitaControl.Cross or VitaControl.Circle or VitaControl.Square or VitaControl.Triangle or VitaControl.PS;

    /// <summary>
    /// Whether a finger at (x, y) (fractions of the screen) is on the control as it is drawn, on a screen
    /// <paramref name="aspect"/> times wider than tall. A stick is the circle of the smaller side of its rectangle and a
    /// round button the rounded rectangle with that radius, each <see cref="RoundMargin"/> of the radius larger; the
    /// corners of the rectangle around them are the game's (a front touch), not the control's. Rectangular controls
    /// take their whole rectangle.
    /// </summary>
    public bool HitTest(float x, float y, float aspect)
    {
        if (!IsRound)
        {
            return Contains(x, y);
        }

        // in units of the screen's height, so a circle on the screen is a circle here
        float width = Width * aspect, height = Height;
        float px = (x - X) * aspect, py = y - Y;
        float radius = Math.Min(width, height) / 2;
        float cx = Math.Clamp(px, radius, width - radius), cy = Math.Clamp(py, radius, height - radius);
        float dx = px - cx, dy = py - cy;
        float reach = radius * (1 + RoundMargin);
        return dx * dx + dy * dy <= reach * reach;
    }
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
    /// <summary>The overlay layout of jogo.html (844x390 dp reference screen, in fractions of the screen): the default
    /// on screens whose bars beside the image are too narrow for the controls (16:9, 16:10, 18:9), where some
    /// controls have to be over the image (<see cref="DefaultLayoutFor"/>).</summary>
    public static IReadOnlyList<OnScreenControl> DefaultLayout { get; } = BuildDefault();

    /// <summary>The Vita system view shows only the PS button (the shell is driven by front touch); the controls
    /// button of sistema.html switches to <see cref="DefaultLayout"/>.</summary>
    public static IReadOnlyList<OnScreenControl> SystemLayout { get; } =
        [new(VitaControl.PS, "PS", DpFraction(11, 0), DpFraction(308, 1), DpFraction(56, 0), DpFraction(56, 1))];

    /// <summary>Where the 960x544 image sits on the screen (fractions), set by the front-end when the surface changes.
    /// Touches inside it that hit no control become front-touch points. While the pad uses the default layout, the
    /// layout follows it (<see cref="DefaultLayoutFor"/>).</summary>
    public (float X, float Y, float Width, float Height) Image
    {
        get => _image;
        set
        {
            _image = value;
            RefreshDefaultLayout();
        }
    }

    /// <summary>The screen's width over its height (set by the front-end with <see cref="Image"/>): the round controls
    /// are hit as the circles they are drawn as (<see cref="OnScreenControl.HitTest"/>).</summary>
    public float Aspect
    {
        get => _aspect;
        set
        {
            _aspect = value;
            RefreshDefaultLayout();
        }
    }

    /// <summary>The controls drawn and hit. Until a layout is set here the pad uses the default one, rebuilt for the
    /// screen whenever <see cref="Image"/> or <see cref="Aspect"/> change; a layout set here (the Vita system view's,
    /// or one the player arranged) is kept as it is on every screen until <see cref="UseDefaultLayout"/>.</summary>
    public IReadOnlyList<OnScreenControl> Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            _defaultLayout = false;
        }
    }

    /// <summary>Whether <see cref="Layout"/> is the default layout that follows the screen.</summary>
    public bool UsesDefaultLayout => _defaultLayout;

    /// <summary>Back to the default layout for the current screen (<see cref="DefaultLayoutFor"/>).</summary>
    public void UseDefaultLayout()
    {
        _defaultLayout = true;
        RefreshDefaultLayout();
    }

    private (float X, float Y, float Width, float Height) _image = (DpFraction(78, 0), 0, DpFraction(688, 0), 1);
    private float _aspect = 844f / 390f;
    private IReadOnlyList<OnScreenControl> _layout = DefaultLayoutFor(844f / 390f, (DpFraction(78, 0), 0, DpFraction(688, 0), 1));
    private bool _defaultLayout = true;
    private (float, float, float, float, float) _defaultLayoutScreen = (DpFraction(78, 0), 0, DpFraction(688, 0), 1, 844f / 390f);

    private void RefreshDefaultLayout()
    {
        (float, float, float, float, float) screen = (_image.X, _image.Y, _image.Width, _image.Height, _aspect);
        if (!_defaultLayout || screen == _defaultLayoutScreen)
        {
            return;
        }

        _defaultLayoutScreen = screen;
        _layout = DefaultLayoutFor(_aspect, _image);
    }
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
    // the image touches over the last moments, sampled by the touch driver at the panel's rate (TouchSampler)
    private readonly TouchSampler _front, _back;
    private bool _rear;
    // what each finger started on: a control, or null for the image / nothing
    private readonly Dictionary<int, OnScreenControl?> _captured = new();
    private readonly HashSet<OnScreenControl> _pressed = new();
    private readonly Dictionary<OnScreenControl, (float X, float Y)> _knobs = new();

    /// <summary>A pad whose touch history runs on the host's monotonic clock, or on <paramref name="clockUs"/>
    /// (tests).</summary>
    public OnScreenPad(Func<long>? clockUs = null)
    {
        _front = new TouchSampler(clockUs);
        _back = new TouchSampler(clockUs);
    }

    /// <summary>Now on the clock of the touch history (microseconds): the time base of <see cref="Update"/>'s event times.</summary>
    public long NowUs => _front.NowUs;

    /// <summary>Changes of the image touches recorded so far (the touch diagnostics).</summary>
    public long TouchChanges => _front.Changes + _back.Changes;

    /// <summary>Controls held right now, and TRAS while it is on (drawn in the accent colour).</summary>
    public bool IsPressed(OnScreenControl onScreenControl)
    {
        lock (_lock)
        {
            return _pressed.Contains(onScreenControl) || onScreenControl.Control == VitaControl.TouchBack && _rear;
        }
    }

    /// <summary>Offset of a stick's knob from its centre, -1..1 per axis (0,0 when released).</summary>
    public (float X, float Y) Knob(OnScreenControl onScreenControl)
    {
        lock (_lock)
        {
            return _knobs.GetValueOrDefault(onScreenControl);
        }
    }

    /// <summary>Feeds the fingers (MotionEvent pointers, in fractions of the screen) from the UI thread, as of
    /// <paramref name="timeUs"/> on <see cref="NowUs"/>'s clock (the event's time; null: now). A MotionEvent's
    /// historical points come first, each with its own time, so the touch history keeps them.</summary>
    public void Update(ReadOnlySpan<(int Id, float X, float Y)> fingers, long? timeUs = null)
    {
        ControllerState controllerState = ControllerState.Neutral;
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
                    _captured[f.Id] = hit = Visible ? HitControl(f.X, f.Y) : null;
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
                if (hit.Shape != OnScreenShape.Stick && !hit.HitTest(f.X, f.Y, Aspect))
                {
                    continue;
                }

                _pressed.Add(hit);
                if (hit.Shape == OnScreenShape.Stick)
                {
                    _knobs[hit] = Stick(ref controllerState, hit, f.X, f.Y);
                }
                else if (hit.Shape == OnScreenShape.Button)
                {
                    controllerState.Buttons |= VitaControls.Button(hit.Control);
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
            psNow = (controllerState.Buttons & VitaButtons.PsButton) != 0;
            _state = controllerState;
            Span<TouchPoint> none = stackalloc TouchPoint[1];
            TouchSampler active = _rear ? _back : _front, inactive = _rear ? _front : _back;
            active.Record(CollectionsMarshal.AsSpan(image), timeUs);
            // the other panel only when a finger was on it (TRAS switched with a finger on the image)
            if (inactive.Current(none) > 0)
            {
                inactive.Record([], timeUs);
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

    /// <summary>The control under a finger as the controls are drawn; null: the image (a front touch) or nothing.</summary>
    private OnScreenControl? HitControl(float x, float y)
    {
        foreach (OnScreenControl c in Layout)
        {
            if (c.HitTest(x, y, Aspect))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>Releases every finger (the activity lost focus or the overlay was hidden).</summary>
    public void Reset() => Update([]);

    private static (float, float) Stick(ref ControllerState controllerState, OnScreenControl onScreenControl, float x, float y)
    {
        float dx = (x - (onScreenControl.X + onScreenControl.Width / 2)) / (onScreenControl.Width / 2), dy = (y - (onScreenControl.Y + onScreenControl.Height / 2)) / (onScreenControl.Height / 2);
        // a finger outside the ring pushes the stick to its edge in that direction
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len > 1)
        {
            dx /= len;
            dy /= len;
        }
        static byte Axis(float v) => (byte)Math.Clamp(128 + v * 127, 0, 255);
        bool left = onScreenControl.Control is VitaControl.LStickUp or VitaControl.LStickDown or VitaControl.LStickLeft or VitaControl.LStickRight;
        if (left)
        {
            controllerState.Lx = Axis(dx);
            controllerState.Ly = Axis(dy);
        }
        else
        {
            controllerState.Rx = Axis(dx);
            controllerState.Ry = Axis(dy);
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
        Span<TouchPoint> touches = stackalloc TouchPoint[TouchSampler.MaxContacts];
        int n = CopyTouches(back, touches);
        return touches[..n].ToArray();
    }

    /// <summary>The image touches of a panel now, without allocating.</summary>
    public int CopyTouches(bool back, Span<TouchPoint> dest) => (back ? _back : _front).Current(dest);

    /// <summary>The image touches of a panel <paramref name="ageUs"/> microseconds ago (<see cref="TouchSampler.At"/>).</summary>
    public int TouchesAgo(bool back, long ageUs, Span<TouchPoint> dest) => (back ? _back : _front).At(ageUs, dest);

    // dp of the 844x390 reference screen to a fraction (axis 0 = x, 1 = y)
    private static float DpFraction(float dp, int axis) => dp / (axis == 0 ? 844f : 390f);

    /// <summary>The narrowest column beside the image (in screen heights) that still takes a stick, the face buttons
    /// and the D-pad at a size a thumb can use: below it <see cref="DefaultLayoutFor"/> keeps the overlay
    /// (<see cref="DefaultLayout"/>).</summary>
    public const float MinColumn = 0.15f;

    // the widest column used (the stick of the reference layout, 124 dp of 390), so very wide screens do not get huge controls
    private const float MaxColumn = 124f / 390f;

    // how much wider than the column the bar must be: room for the round controls' hit margin (RoundMargin) and a gap
    private const float BarPerColumn = 1.12f;

    /// <summary>
    /// The default layout for a screen <paramref name="aspect"/> times wider than tall with the game's image at
    /// <paramref name="image"/> (fractions of the screen). The Vita has no virtual controls: the whole panel is the
    /// game's, and games put touch icons up to its edges. So when the black bars beside the image are wide enough
    /// (<see cref="MinColumn"/>, e.g. a 20:9 or 19.5:9 phone) every control goes in them, one column a side, laid out
    /// like the console's halves (left: L, D-pad, left stick, PS and TRAS at the bottom; right: R, face buttons, right
    /// stick, SELECT and START), and no control covers the image. On narrower bars (16:9, 16:10 tablets, 18:9) the
    /// overlay layout of <see cref="DefaultLayout"/> is used; there a finger that starts on the image outside a
    /// control's drawn shape (<see cref="OnScreenControl.HitTest"/>) is still the game's front touch.
    /// </summary>
    public static IReadOnlyList<OnScreenControl> DefaultLayoutFor(float aspect, (float X, float Y, float Width, float Height) image)
    {
        // the narrower of the two bars, in screen heights
        float bar = Math.Min(image.X, 1 - image.X - image.Width) * aspect;
        float column = Math.Min(bar / BarPerColumn, MaxColumn);
        if (aspect <= 0 || column < MinColumn)
        {
            return DefaultLayout;
        }

        float leftCentre = image.X * aspect / 2;
        float rightCentre = aspect - (1 - image.X - image.Width) * aspect / 2;
        return BuildColumns(aspect, leftCentre, rightCentre, column);
    }

    // the two side columns, centred on leftCentre/rightCentre (screen heights from the left edge), each column wide
    private static IReadOnlyList<OnScreenControl> BuildColumns(float aspect, float leftCentre, float rightCentre, float column)
    {
        const float ShoulderTop = 0.03f, ShoulderHeight = 0.09f;
        const float ButtonsCentre = 0.35f, StickCentre = 0.71f;
        const float BottomTop = 0.89f, BottomHeight = 0.08f;
        // face buttons: a diamond as wide as the column, the diagonal neighbours just apart
        float face = column / 2.5f;
        float faceStep = face * 0.75f;
        // D-pad: a plus of three cells across the column
        float cell = column / 3;
        float half = column / 2;

        // (x, y, width, height) in screen heights to a control in fractions of the screen
        OnScreenControl Control(VitaControl control, string label, float x, float y, float width, float height,
            OnScreenShape shape = OnScreenShape.Button) => new(control, label, x / aspect, y, width / aspect, height, shape);

        OnScreenControl FaceButton(VitaControl control, string label, float centreX, float centreY) =>
            Control(control, label, centreX - face / 2, centreY - face / 2, face, face);

        return
        [
            Control(VitaControl.L, "L", leftCentre - half, ShoulderTop, column, ShoulderHeight),
            Control(VitaControl.R, "R", rightCentre - half, ShoulderTop, column, ShoulderHeight),
            Control(VitaControl.Up, "\u25B2", leftCentre - cell / 2, ButtonsCentre - cell * 1.5f, cell, cell),
            Control(VitaControl.Down, "\u25BC", leftCentre - cell / 2, ButtonsCentre + cell / 2, cell, cell),
            Control(VitaControl.Left, "\u25C0", leftCentre - cell * 1.5f, ButtonsCentre - cell / 2, cell, cell),
            Control(VitaControl.Right, "\u25B6", leftCentre + cell / 2, ButtonsCentre - cell / 2, cell, cell),
            FaceButton(VitaControl.Triangle, "\u25B3", rightCentre, ButtonsCentre - faceStep),
            FaceButton(VitaControl.Square, "\u25A1", rightCentre - faceStep, ButtonsCentre),
            FaceButton(VitaControl.Circle, "\u25CB", rightCentre + faceStep, ButtonsCentre),
            FaceButton(VitaControl.Cross, "\u2715", rightCentre, ButtonsCentre + faceStep),
            Control(VitaControl.LStickUp, "", leftCentre - half, StickCentre - half, column, column, OnScreenShape.Stick),
            Control(VitaControl.RStickUp, "", rightCentre - half, StickCentre - half, column, column, OnScreenShape.Stick),
            Control(VitaControl.Select, "SELECT", rightCentre - half, BottomTop, column * 0.48f, BottomHeight),
            Control(VitaControl.PS, "PS", leftCentre - half, BottomTop, column * 0.4f, BottomHeight),
            Control(VitaControl.Start, "START", rightCentre + column * 0.02f, BottomTop, column * 0.48f, BottomHeight),
            Control(VitaControl.TouchBack, "", leftCentre - half + column * 0.45f, BottomTop, column * 0.55f, BottomHeight, OnScreenShape.Toggle) with { LabelKey = "MRearButton" },
        ];
    }

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
        new(VitaControl.TouchBack, "", DpFraction(22, 0), DpFraction(320, 1), DpFraction(52, 0), DpFraction(36, 1), OnScreenShape.Toggle) { LabelKey = "MRearButton" },
    ];
}
