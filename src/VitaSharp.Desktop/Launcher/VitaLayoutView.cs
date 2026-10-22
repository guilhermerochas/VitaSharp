using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Live drawing of the PS Vita's controls (vector shapes, no artwork): pressed buttons light up, each stick's dot
/// follows the analog position inside its deadzone ring, and touches show as dots on the front screen and the rear
/// pad. Clicking a control raises <see cref="ControlClicked"/> (a stick raises its four directions' first one).
/// </summary>
public sealed class VitaLayoutView : Control
{
    const double DesignW = 1000, DesignH = 640;

    // geometry in design units
    static readonly Rect Body = new(40, 60, 920, 380);
    static readonly Rect Screen = new(290, 104, 420, 236);
    static readonly Rect RearPad = new(300, 478, 400, 140);
    static readonly Rect LButton = new(92, 40, 170, 30), RButton = new(738, 40, 170, 30);
    static readonly Point DpadCenter = new(170, 190), FaceCenter = new(830, 190);
    static readonly Point LStick = new(222, 330), RStick = new(778, 330);
    const double StickBase = 46, StickTravel = 30, StickKnob = 18;
    static readonly Point PsButton = new(150, 398);
    static readonly Rect SelectButton = new(770, 388, 42, 18), StartButton = new(832, 388, 42, 18);

    static readonly IBrush BodyFill = new SolidColorBrush(Color.Parse("#232833"));
    static readonly IBrush BodyStroke = new SolidColorBrush(Color.Parse("#3A4150"));
    static readonly IBrush ScreenFill = new SolidColorBrush(Color.Parse("#0E1117"));
    static readonly IBrush ControlFill = new SolidColorBrush(Color.Parse("#323946"));
    static readonly IBrush ControlStroke = new SolidColorBrush(Color.Parse("#4A5263"));
    static readonly IBrush Pressed = new SolidColorBrush(Color.Parse("#3D7BFF"));
    static readonly IBrush PressedGlow = new SolidColorBrush(Color.Parse("#553D7BFF"));
    static readonly IBrush Listening = new SolidColorBrush(Color.Parse("#FFB547"));
    static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#9DBBFF"));
    static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA3B2"));
    static readonly IBrush TouchDot = new SolidColorBrush(Color.Parse("#E0FF6B81"));
    static readonly IBrush Triangle = new SolidColorBrush(Color.Parse("#40D9A8")), Circle = new SolidColorBrush(Color.Parse("#FF6B81"));
    static readonly IBrush Cross = new SolidColorBrush(Color.Parse("#7DA7FF")), Square = new SolidColorBrush(Color.Parse("#E891D6"));
    static readonly Typeface Font = new(FontFamily.Default);

    MappedInput _state = MappedInput.Neutral;
    (float X, float Y) _rawL, _rawR;
    float _dzL, _dzR;
    VitaControl? _selected, _listening;
    bool _blink;

    /// <summary>Raised on a click: the control, whether the right button was used, and for the touch areas the
    /// normalised position clicked.</summary>
    public event Action<VitaControl, bool, Point>? ControlClicked;

    public VitaLayoutView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Updates the live state: mapped output, stick positions before the deadzone and the deadzones.</summary>
    public void SetState(in MappedInput state, (float X, float Y) rawLeft, (float X, float Y) rawRight, float deadzoneLeft, float deadzoneRight)
    {
        _state = state;
        _rawL = rawLeft; _rawR = rawRight;
        _dzL = deadzoneLeft; _dzR = deadzoneRight;
        InvalidateVisual();
    }

    public VitaControl? SelectedControl { get => _selected; set { _selected = value; InvalidateVisual(); } }
    public VitaControl? ListeningControl { get => _listening; set { _listening = value; InvalidateVisual(); } }
    public void Blink() { _blink = !_blink; if (_listening != null) InvalidateVisual(); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? DesignW * 0.6 : availableSize.Width;
        return new Size(w, w * DesignH / DesignW);
    }

    (double Scale, double Ox, double Oy) Fit()
    {
        double s = Math.Min(Bounds.Width / DesignW, Bounds.Height / DesignH);
        return (s, (Bounds.Width - DesignW * s) / 2, (Bounds.Height - DesignH * s) / 2);
    }

    // ------------------------------------------------------------------ drawing

    public override void Render(DrawingContext ctx)
    {
        var (s, ox, oy) = Fit();
        if (s <= 0) return;
        using var _ = ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy));
        var b = _state.State.Buttons;

        // shoulder buttons sit behind the top edge of the body
        DrawButton(ctx, VitaControl.L, new RoundedRect(LButton, 14), b.HasFlag(VitaButtons.LTrigger));
        DrawButton(ctx, VitaControl.R, new RoundedRect(RButton, 14), b.HasFlag(VitaButtons.RTrigger));
        Label(ctx, "L", LButton.Center, 15, Muted);
        Label(ctx, "R", RButton.Center, 15, Muted);

        ctx.DrawRectangle(BodyFill, new Pen(BodyStroke, 2), new RoundedRect(Body, Body.Height / 2));

        // front touch screen
        bool frontTouched = _state.FrontCount > 0;
        ctx.DrawRectangle(ScreenFill, new Pen(Outline(VitaControl.TouchFront) ?? (frontTouched ? Pressed : BodyStroke), Outline(VitaControl.TouchFront) != null ? 3 : 2), new RoundedRect(Screen, 6));
        Label(ctx, "Front touch screen", new Point(Screen.Center.X, Screen.Bottom - 16), 13, Muted);
        for (int i = 0; i < _state.FrontCount; i++) DrawTouch(ctx, Screen, _state.Front[i]);

        // D-pad
        DrawArm(ctx, VitaControl.Up, new Rect(DpadCenter.X - 17, DpadCenter.Y - 58, 34, 42), b.HasFlag(VitaButtons.Up));
        DrawArm(ctx, VitaControl.Down, new Rect(DpadCenter.X - 17, DpadCenter.Y + 16, 34, 42), b.HasFlag(VitaButtons.Down));
        DrawArm(ctx, VitaControl.Left, new Rect(DpadCenter.X - 58, DpadCenter.Y - 17, 42, 34), b.HasFlag(VitaButtons.Left));
        DrawArm(ctx, VitaControl.Right, new Rect(DpadCenter.X + 16, DpadCenter.Y - 17, 42, 34), b.HasFlag(VitaButtons.Right));
        ctx.DrawRectangle(ControlFill, null, new Rect(DpadCenter.X - 17, DpadCenter.Y - 17, 34, 34));

        // face buttons with their symbols
        DrawFace(ctx, VitaControl.Triangle, FacePos(VitaControl.Triangle), b.HasFlag(VitaButtons.Triangle), Triangle);
        DrawFace(ctx, VitaControl.Circle, FacePos(VitaControl.Circle), b.HasFlag(VitaButtons.Circle), Circle);
        DrawFace(ctx, VitaControl.Cross, FacePos(VitaControl.Cross), b.HasFlag(VitaButtons.Cross), Cross);
        DrawFace(ctx, VitaControl.Square, FacePos(VitaControl.Square), b.HasFlag(VitaButtons.Square), Square);

        // sticks
        DrawStick(ctx, LStick, true, _state.Lx, _state.Ly, _rawL, _dzL, b.HasFlag(VitaButtons.L3), _state.State.Lx, _state.State.Ly);
        DrawStick(ctx, RStick, false, _state.Rx, _state.Ry, _rawR, _dzR, b.HasFlag(VitaButtons.R3), _state.State.Rx, _state.State.Ry);

        // PS, Select, Start
        DrawButton(ctx, VitaControl.PS, null, b.HasFlag(VitaButtons.PsButton), new EllipseGeometry(new Rect(PsButton.X - 16, PsButton.Y - 16, 32, 32)));
        Label(ctx, "PS", PsButton, 12, b.HasFlag(VitaButtons.PsButton) ? Brushes.White : Muted);
        DrawButton(ctx, VitaControl.Select, new RoundedRect(SelectButton, 9), b.HasFlag(VitaButtons.Select));
        DrawButton(ctx, VitaControl.Start, new RoundedRect(StartButton, 9), b.HasFlag(VitaButtons.Start));
        Label(ctx, "SELECT", new Point(SelectButton.Center.X, SelectButton.Bottom + 14), 11, Muted);
        Label(ctx, "START", new Point(StartButton.Center.X, StartButton.Bottom + 14), 11, Muted);

        // rear touch pad (on the back of the console)
        bool backTouched = _state.BackCount > 0;
        var rearOutline = Outline(VitaControl.TouchBack);
        var dashed = new Pen(rearOutline ?? (backTouched ? Pressed : ControlStroke), rearOutline != null ? 3 : 2) { DashStyle = new DashStyle([6, 4], 0) };
        ctx.DrawRectangle(backTouched ? new SolidColorBrush(Color.Parse("#1A2440")) : BodyFill, dashed, new RoundedRect(RearPad, 26));
        Label(ctx, "Rear touch pad (back of the console)", new Point(RearPad.Center.X, RearPad.Bottom - 16), 13, Muted);
        for (int i = 0; i < _state.BackCount; i++) DrawTouch(ctx, RearPad, _state.Back[i]);
    }

    IBrush? Outline(VitaControl c) => _listening == c ? (_blink ? Listening : Brushes.White) : _selected == c ? Selected : null;

    static Point FacePos(VitaControl c) => c switch
    {
        VitaControl.Triangle => new(FaceCenter.X, FaceCenter.Y - 48),
        VitaControl.Circle => new(FaceCenter.X + 48, FaceCenter.Y),
        VitaControl.Cross => new(FaceCenter.X, FaceCenter.Y + 48),
        _ => new(FaceCenter.X - 48, FaceCenter.Y),
    };

    void DrawButton(DrawingContext ctx, VitaControl c, RoundedRect? rect, bool down, Geometry? geometry = null)
    {
        var outline = Outline(c);
        var pen = new Pen(outline ?? (down ? Pressed : ControlStroke), outline != null ? 3 : 1.5);
        var fill = down ? Pressed : ControlFill;
        if (geometry != null) ctx.DrawGeometry(fill, pen, geometry);
        else if (rect is { } r) ctx.DrawRectangle(fill, pen, r);
    }

    void DrawArm(DrawingContext ctx, VitaControl c, Rect r, bool down) => DrawButton(ctx, c, new RoundedRect(r, 6), down);

    void DrawFace(DrawingContext ctx, VitaControl c, Point p, bool down, IBrush symbol)
    {
        const double R = 22;
        if (down) ctx.DrawEllipse(PressedGlow, null, p, R + 8, R + 8);
        DrawButton(ctx, c, null, down, new EllipseGeometry(new Rect(p.X - R, p.Y - R, 2 * R, 2 * R)));
        var pen = new Pen(down ? Brushes.White : symbol, 3);
        switch (c)
        {
            case VitaControl.Triangle:
            {
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(new Point(p.X, p.Y - 10), false);
                    gc.LineTo(new Point(p.X + 10, p.Y + 7));
                    gc.LineTo(new Point(p.X - 10, p.Y + 7));
                    gc.EndFigure(true);
                }
                ctx.DrawGeometry(null, pen, g);
                break;
            }
            case VitaControl.Circle: ctx.DrawEllipse(null, pen, p, 9, 9); break;
            case VitaControl.Cross:
                ctx.DrawLine(pen, new Point(p.X - 8, p.Y - 8), new Point(p.X + 8, p.Y + 8));
                ctx.DrawLine(pen, new Point(p.X + 8, p.Y - 8), new Point(p.X - 8, p.Y + 8));
                break;
            default: ctx.DrawRectangle(null, pen, new Rect(p.X - 8, p.Y - 8, 16, 16)); break;
        }
    }

    void DrawStick(DrawingContext ctx, Point c, bool left, float x, float y, (float X, float Y) raw, float deadzone, bool clicked, byte bx, byte by)
    {
        var first = left ? VitaControl.LStickUp : VitaControl.RStickUp;
        IBrush? outline = null;
        for (var d = first; d <= first + 3; d++) outline ??= Outline(d);
        outline ??= Outline(left ? VitaControl.L3 : VitaControl.R3);
        bool moved = x != 0 || y != 0;
        ctx.DrawEllipse(ScreenFill, new Pen(outline ?? (clicked ? Pressed : ControlStroke), outline != null ? 3 : 2), c, StickBase, StickBase);
        // travel limit and deadzone ring
        ctx.DrawEllipse(null, new Pen(ControlStroke, 1), c, StickTravel, StickTravel);
        if (deadzone > 0)
            ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#22FFB547")), new Pen(new SolidColorBrush(Color.Parse("#88FFB547")), 1) { DashStyle = new DashStyle([3, 3], 0) },
                c, Math.Max(2, deadzone * StickTravel), Math.Max(2, deadzone * StickTravel));
        // raw position (before the deadzone) as a ring, mapped output as the knob
        var rawP = new Point(c.X + Math.Clamp(raw.X, -1, 1) * StickTravel, c.Y + Math.Clamp(raw.Y, -1, 1) * StickTravel);
        var knob = new Point(c.X + x * StickTravel, c.Y + y * StickTravel);
        if (moved) ctx.DrawLine(new Pen(Pressed, 2), c, knob);
        ctx.DrawEllipse(clicked || moved ? Pressed : ControlFill, new Pen(clicked ? Brushes.White : ControlStroke, 2), knob, StickKnob, StickKnob);
        ctx.DrawEllipse(null, new Pen(Listening, 1.5), rawP, 5, 5);
        Label(ctx, $"{bx},{by}", new Point(c.X, c.Y + StickBase + 14), 12, Muted);
        Label(ctx, left ? "Left stick" : "Right stick", new Point(c.X, c.Y - StickBase - 12), 12, Muted);
    }

    static void DrawTouch(DrawingContext ctx, Rect area, TouchPoint t)
    {
        var p = new Point(area.X + t.X * area.Width, area.Y + t.Y * area.Height);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#40FF6B81")), null, p, 18, 18);
        ctx.DrawEllipse(TouchDot, new Pen(Brushes.White, 1.5), p, 8, 8);
    }

    static void Label(DrawingContext ctx, string text, Point center, double size, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush);
        ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    // ------------------------------------------------------------------ hit testing

    /// <summary>The control under a point in design units, with the normalised position for the touch areas.</summary>
    public static (VitaControl Control, Point Normalised)? HitTest(Point p)
    {
        static bool InCircle(Point p, Point c, double r) => (p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y) <= r * r;
        if (LButton.Inflate(4).Contains(p)) return (VitaControl.L, default);
        if (RButton.Inflate(4).Contains(p)) return (VitaControl.R, default);
        foreach (var c in new[] { VitaControl.Triangle, VitaControl.Circle, VitaControl.Cross, VitaControl.Square })
            if (InCircle(p, FacePos(c), 26)) return (c, default);
        if (InCircle(p, LStick, StickBase + 4)) return (VitaControl.LStickUp, default);
        if (InCircle(p, RStick, StickBase + 4)) return (VitaControl.RStickUp, default);
        if (InCircle(p, PsButton, 20)) return (VitaControl.PS, default);
        if (SelectButton.Inflate(6).Contains(p)) return (VitaControl.Select, default);
        if (StartButton.Inflate(6).Contains(p)) return (VitaControl.Start, default);
        double dx = p.X - DpadCenter.X, dy = p.Y - DpadCenter.Y;
        if (Math.Abs(dx) <= 60 && Math.Abs(dy) <= 60 && (Math.Abs(dx) <= 20 || Math.Abs(dy) <= 20) && (Math.Abs(dx) > 8 || Math.Abs(dy) > 8))
            return (Math.Abs(dx) > Math.Abs(dy) ? (dx < 0 ? VitaControl.Left : VitaControl.Right) : (dy < 0 ? VitaControl.Up : VitaControl.Down), default);
        if (Screen.Contains(p)) return (VitaControl.TouchFront, new Point((p.X - Screen.X) / Screen.Width, (p.Y - Screen.Y) / Screen.Height));
        if (RearPad.Contains(p)) return (VitaControl.TouchBack, new Point((p.X - RearPad.X) / RearPad.Width, (p.Y - RearPad.Y) / RearPad.Height));
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var (s, ox, oy) = Fit();
        if (s <= 0) return;
        var pos = e.GetPosition(this);
        var hit = HitTest(new Point((pos.X - ox) / s, (pos.Y - oy) / s));
        if (hit is not { } h) return;
        bool right = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        ControlClicked?.Invoke(h.Control, right, h.Normalised);
        e.Handled = true;
    }
}
