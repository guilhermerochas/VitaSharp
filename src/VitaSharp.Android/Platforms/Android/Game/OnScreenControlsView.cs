using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using VitaSharp.Core.Input;
using VitaSharp.Mobile.Core.Input;
using Color = Android.Graphics.Color;
using Paint = Android.Graphics.Paint;
using RectF = Android.Graphics.RectF;
using View = Android.Views.View;

namespace VitaSharp.Android.Game;

/// <summary>
/// The translucent on-screen controls of jogo.html over the SurfaceView: draws <see cref="OnScreenPad.Layout"/> (pressed
/// controls in the accent colour, stick knobs following the finger, TRAS lit while rear touch is on) and feeds every
/// MotionEvent pointer to the pad as fractions of the view. Touches on the image that hit no control become front touch
/// (or rear touch with TRAS on) inside the pad.
/// </summary>
public sealed class OnScreenControlsView : View
{
    readonly OnScreenPad _pad;
    readonly Paint _fill = new(PaintFlags.AntiAlias), _stroke = new(PaintFlags.AntiAlias), _text = new(PaintFlags.AntiAlias);
    static readonly Color Accent = Color.ParseColor("#6E9BFF"), Surface = Color.ParseColor("#171B22"), Line = Color.ParseColor("#C9D1DC");
    float _opacity = 0.55f;
    readonly OnScreenHaptics _haptics;
    readonly Vibrator? _vibrator;

    public OnScreenControlsView(Context context, OnScreenPad pad) : base(context)
    {
        _vibrator = VibratorOf(context);
        _haptics = new OnScreenHaptics(pad, Pulse);
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
        set
        {
            _opacity = Math.Clamp(value, 0.1f, 1f);
            Invalidate();
        }
    }

    /// <summary>Vibration on every newly pressed control, also while another finger holds the stick (Ajustes > Controles).</summary>
    public bool Haptics
    {
        get => _haptics.Enabled;
        set => _haptics.Enabled = value;
    }

    /// <summary>Strength of the vibration (OnScreenHaptics.Weak/Medium/Strong).</summary>
    public int HapticStrength
    {
        get => _haptics.Strength;
        set => _haptics.Strength = value;
    }

    static Vibrator? VibratorOf(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return (context.GetSystemService(Context.VibratorManagerService) as VibratorManager)?.DefaultVibrator;
        }
#pragma warning disable CA1422 // the pre-31 way, only on the versions that have no VibratorManager
        return context.GetSystemService(Context.VibratorService) as Vibrator;
#pragma warning restore CA1422
    }

    /// <summary>One pulse: a one-shot vibration with the amplitude when the motor supports it, else the view's key-tap
    /// haptic feedback (which the system may scale or skip by its own touch-feedback setting).</summary>
    void Pulse(int amplitude)
    {
        if (_vibrator is { } vibrator && vibrator.HasVibrator)
        {
            int level = vibrator.HasAmplitudeControl ? amplitude : VibrationEffect.DefaultAmplitude;
            vibrator.Vibrate(VibrationEffect.CreateOneShot(OnScreenHaptics.PulseMilliseconds, level));
            return;
        }
        PerformHapticFeedback(FeedbackConstants.KeyboardTap);
    }

    /// <summary>A finger went down (view coordinates): the game screen shows its auto-hidden chrome again.</summary>
    public event Action<float, float>? FingerDown;

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || Width == 0 || Height == 0)
        {
            return false;
        }

        if (e.ActionMasked is MotionEventActions.Down or MotionEventActions.PointerDown)
        {
            FingerDown?.Invoke(e.GetX(e.ActionIndex), e.GetY(e.ActionIndex));
        }

        int n = e.PointerCount;
        MotionEventActions action = e.ActionMasked;
        int lifted = action is MotionEventActions.Up or MotionEventActions.PointerUp or MotionEventActions.Cancel ? e.ActionIndex : -1;
        int historical = e.HistorySize;
        TouchDiagnostics.Raw(RawKind(action), n, historical);
        // event times are SystemClock.uptimeMillis(): moved onto the pad's clock (the touch history's time base)
        long offsetUs = _pad.NowUs - SystemClock.UptimeMillis() * 1000;
        Span<(int Id, float X, float Y)> fingers = stackalloc (int, float, float)[Math.Min(n, 10)];
        // a move batches the points since the previous frame (MotionEvent history): each one goes to the touch
        // history at its own time, so the driver's per-vblank samples see the path between frames
        if (action == MotionEventActions.Move)
        {
            for (int h = 0; h < historical; h++)
            {
                int count = 0;
                for (int i = 0; i < n && count < fingers.Length; i++)
                {
                    fingers[count++] = (e.GetPointerId(i), e.GetHistoricalX(i, h) / Width, e.GetHistoricalY(i, h) / Height);
                }

                _pad.Update(fingers[..count], e.GetHistoricalEventTime(h) * 1000 + offsetUs);
            }
        }

        int current = 0;
        for (int i = 0; i < n && current < fingers.Length; i++)
        {
            if (i == lifted || action == MotionEventActions.Cancel)
            {
                continue;
            }

            fingers[current++] = (e.GetPointerId(i), e.GetX(i) / Width, e.GetY(i) / Height);
        }

        _pad.Update(fingers[..current], e.EventTime * 1000 + offsetUs); // newly pressed controls vibrate through OnScreenHaptics
        Invalidate();
        return true;
    }

    static TouchDiagnostics.RawKind RawKind(MotionEventActions action) => action switch
    {
        MotionEventActions.Down => TouchDiagnostics.RawKind.Down,
        MotionEventActions.PointerDown => TouchDiagnostics.RawKind.PointerDown,
        MotionEventActions.PointerUp => TouchDiagnostics.RawKind.PointerUp,
        MotionEventActions.Up => TouchDiagnostics.RawKind.Up,
        MotionEventActions.Cancel => TouchDiagnostics.RawKind.Cancel,
        _ => TouchDiagnostics.RawKind.Move,
    };

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (!_pad.Visible)
            return;
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
                    bool round = c.IsRound; // the shape the pad hit-tests (OnScreenControl.HitTest)
                    float corner = round ? Math.Min(r.Width(), r.Height()) / 2 : 12 * density;
                    canvas.DrawRoundRect(r, corner, corner, _fill);
                    canvas.DrawRoundRect(r, corner, corner, _stroke);
                    string label = c.Text;
                    if (label.Length > 0)
                    {
                        _text.Color = pressed ? Color.ParseColor("#0B1220") : new Color(232, 236, 242, Math.Min(255, alpha + 80));
                        _text.TextSize = Math.Min(r.Height() * 0.45f, label.Length > 2 ? r.Width() / label.Length * 1.5f : r.Height() * 0.5f);
                        var fm = _text.GetFontMetrics();
                        canvas.DrawText(label, r.CenterX(), r.CenterY() - (fm!.Ascent + fm.Descent) / 2, _text);
                    }
                    break;
                }
            }
        }
    }
}
