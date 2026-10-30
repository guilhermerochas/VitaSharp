using Android.Content;
using Android.Graphics;
using Android.Views;
using VitaSharp.Android.Input;
using VitaSharp.Core.Input;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using RectF = Android.Graphics.RectF;
using View = Android.Views.View;

namespace VitaSharp.Android.Game;

/// <summary>
/// The translucent on-screen controls of jogo.html over the SurfaceView: draws <see cref="OnScreenPad.Layout"/> (pressed
/// controls in the accent colour, stick knobs following the finger, TRÁS lit while rear touch is on) and feeds every
/// MotionEvent pointer to the pad as fractions of the view. Touches on the image that hit no control become front touch
/// (or rear touch with TRÁS on) inside the pad.
/// </summary>
public sealed class OnScreenControlsView : View
{
    readonly OnScreenPad _pad;
    readonly Paint _fill = new(PaintFlags.AntiAlias), _stroke = new(PaintFlags.AntiAlias), _text = new(PaintFlags.AntiAlias);
    static readonly Color Accent = Color.ParseColor("#6E9BFF"), Surface = Color.ParseColor("#171B22"), Line = Color.ParseColor("#C9D1DC");
    float _opacity = 0.55f;

    public OnScreenControlsView(Context context, OnScreenPad pad) : base(context)
    {
        _pad = pad;
        _stroke.SetStyle(Paint.Style.Stroke);
        _text.TextAlign = Paint.Align.Center;
        _text.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        _pad.ControlPressed += _ => PostInvalidate();
        HapticFeedbackEnabled = true;
    }

    /// <summary>Opacity of the controls, 0.1..1 (Ajustes > Controles).</summary>
    public float ControlsOpacity
    {
        get => _opacity;
        set { _opacity = Math.Clamp(value, 0.1f, 1f); Invalidate(); }
    }

    public bool Haptics { get; set; } = true;

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || Width == 0 || Height == 0) return false;
        int n = e.PointerCount;
        var action = e.ActionMasked;
        int lifted = action is MotionEventActions.Up or MotionEventActions.PointerUp or MotionEventActions.Cancel ? e.ActionIndex : -1;
        Span<(int Id, float X, float Y)> fingers = stackalloc (int, float, float)[Math.Min(n, 10)];
        int count = 0;
        for (int i = 0; i < n && count < fingers.Length; i++)
        {
            if (i == lifted || action == MotionEventActions.Cancel) continue;
            fingers[count++] = (e.GetPointerId(i), e.GetX(i) / Width, e.GetY(i) / Height);
        }
        bool before = AnyPressed();
        _pad.Update(fingers[..count]);
        if (Haptics && action is MotionEventActions.Down or MotionEventActions.PointerDown && AnyPressed() && !before)
            PerformHapticFeedback(FeedbackConstants.KeyboardTap);
        Invalidate();
        return true;
    }

    bool AnyPressed() => _pad.Layout.Any(_pad.IsPressed);

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (!_pad.Visible) return;
        float w = Width, h = Height;
        int alpha = (int)(_opacity * 255);
        float density = Resources?.DisplayMetrics?.Density ?? 2f;
        _stroke.StrokeWidth = 1.5f * density;
        foreach (var c in _pad.Layout)
        {
            bool pressed = _pad.IsPressed(c);
            var r = new RectF(c.X * w, c.Y * h, (c.X + c.Width) * w, (c.Y + c.Height) * h);
            var fill = pressed ? Accent : Surface;
            _fill.Color = new Color(fill.R, fill.G, fill.B, pressed ? Math.Min(255, alpha + 60) : alpha);
            _stroke.Color = new Color(Line.R, Line.G, Line.B, alpha / 2);
            switch (c.Shape)
            {
                case OnScreenShape.Stick:
                {
                    float cx = r.CenterX(), cy = r.CenterY(), radius = Math.Min(r.Width(), r.Height()) / 2;
                    _fill.Color = new Color(Surface.R, Surface.G, Surface.B, alpha);
                    canvas.DrawCircle(cx, cy, radius, _fill);
                    canvas.DrawCircle(cx, cy, radius, _stroke);
                    var (kx, ky) = _pad.Knob(c);
                    var knob = pressed ? Accent : Line;
                    _fill.Color = new Color(knob.R, knob.G, knob.B, Math.Min(255, alpha + 40));
                    canvas.DrawCircle(cx + kx * radius * 0.6f, cy + ky * radius * 0.6f, radius * 0.42f, _fill);
                    break;
                }
                default:
                {
                    bool round = c.Control is VitaControl.Cross or VitaControl.Circle or VitaControl.Square or VitaControl.Triangle or VitaControl.PS;
                    float corner = round ? Math.Min(r.Width(), r.Height()) / 2 : 12 * density;
                    canvas.DrawRoundRect(r, corner, corner, _fill);
                    canvas.DrawRoundRect(r, corner, corner, _stroke);
                    if (c.Label.Length > 0)
                    {
                        _text.Color = pressed ? Color.ParseColor("#0B1220") : new Color(232, 236, 242, Math.Min(255, alpha + 80));
                        _text.TextSize = Math.Min(r.Height() * 0.45f, c.Label.Length > 2 ? r.Width() / c.Label.Length * 1.5f : r.Height() * 0.5f);
                        var fm = _text.GetFontMetrics();
                        canvas.DrawText(c.Label, r.CenterX(), r.CenterY() - (fm!.Ascent + fm.Descent) / 2, _text);
                    }
                    break;
                }
            }
        }
    }
}
