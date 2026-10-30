using VitaSharp.Core.Host;

namespace VitaSharp.Core.Input;

/// <summary>
/// The state of one host controller at an instant: the standard gamepad view (when the controller has a mapping in
/// the database) and the raw joystick view. Fixed-size arrays, so polling every frame allocates nothing.
/// </summary>
public sealed class PadSnapshot
{
    public const int MaxJoyAxes = 16, MaxJoyButtons = 64, MaxJoyHats = 8, ButtonCount = (int)PadButton.Touchpad + 1, MaxFingers = 2;

    public bool IsGamepad;
    public readonly bool[] Buttons = new bool[ButtonCount];
    /// <summary>Stick axes in -1..1 (Y down = +1), triggers in 0..1 (released = 0).</summary>
    public readonly float[] Axes = new float[6];
    public readonly float[] JoyAxes = new float[MaxJoyAxes];
    public readonly bool[] JoyButtons = new bool[MaxJoyButtons];
    public readonly byte[] JoyHats = new byte[MaxJoyHats];
    public int JoyAxisCount, JoyButtonCount, JoyHatCount;
    /// <summary>The controller has a touch pad (DualShock 4, DualSense, Steam controllers...).</summary>
    public bool HasTouchpad;
    /// <summary>Fingers on the first touch pad, positions normalised 0..1 (origin top-left).</summary>
    public readonly bool[] FingerDown = new bool[MaxFingers];
    public readonly float[] FingerX = new float[MaxFingers], FingerY = new float[MaxFingers];
    /// <summary>The controller reports a gyroscope and an accelerometer (<see cref="Accel"/>, <see cref="Gyro"/>).</summary>
    public bool HasMotion;
    /// <summary>Latest accelerometer reading in the controller's SDL axes, m/s² (lying flat: +9.8 on y).</summary>
    public System.Numerics.Vector3 Accel;
    /// <summary>Latest gyroscope reading in the controller's SDL axes, rad/s.</summary>
    public System.Numerics.Vector3 Gyro;

    public void CopyTo(PadSnapshot d)
    {
        d.IsGamepad = IsGamepad;
        d.HasMotion = HasMotion; d.Accel = Accel; d.Gyro = Gyro;
        d.HasTouchpad = HasTouchpad;
        FingerDown.CopyTo(d.FingerDown, 0); FingerX.CopyTo(d.FingerX, 0); FingerY.CopyTo(d.FingerY, 0);
        Buttons.CopyTo(d.Buttons, 0);
        Axes.CopyTo(d.Axes, 0);
        JoyAxes.CopyTo(d.JoyAxes, 0);
        JoyButtons.CopyTo(d.JoyButtons, 0);
        JoyHats.CopyTo(d.JoyHats, 0);
        d.JoyAxisCount = JoyAxisCount; d.JoyButtonCount = JoyButtonCount; d.JoyHatCount = JoyHatCount;
    }

    public PadSnapshot Clone() { var c = new PadSnapshot(); CopyTo(c); return c; }

    public void Clear()
    {
        Array.Clear(Buttons); Array.Clear(Axes); Array.Clear(JoyAxes); Array.Clear(JoyButtons); Array.Clear(JoyHats);
        JoyAxisCount = JoyButtonCount = JoyHatCount = 0;
        HasTouchpad = false;
        HasMotion = false; Accel = default; Gyro = default;
        Array.Clear(FingerDown); Array.Clear(FingerX); Array.Clear(FingerY);
    }
}

/// <summary>Up to four touch contacts of one panel, stored inline (no allocation per poll).</summary>
[System.Runtime.CompilerServices.InlineArray(4)]
public struct TouchContacts
{
    TouchPoint _first;
}

/// <summary>Result of mapping one device: the Vita pad state plus the touches it produces.</summary>
public struct MappedInput
{
    public const int MaxContacts = 4;
    /// <summary>Host contact ids (the touch HLE turns each into a fresh report id): the TouchFront/TouchBack point
    /// and the touch pad fingers.</summary>
    public const int PointId = 3, FingerId = 10;

    public ControllerState State;
    public TouchContacts Front, Back;
    public int FrontCount, BackCount;
    /// <summary>Stick positions after deadzone/sensitivity, -1..1 (for the live preview).</summary>
    public float Lx, Ly, Rx, Ry;

    public static MappedInput Neutral => new() { State = ControllerState.Neutral };

    public void AddTouch(bool back, TouchPoint t)
    {
        if (back) { if (BackCount < MaxContacts) Back[BackCount++] = t; }
        else if (FrontCount < MaxContacts) Front[FrontCount++] = t;
    }

    public readonly int CopyTouches(bool back, Span<TouchPoint> dest)
    {
        int n = Math.Min(back ? BackCount : FrontCount, dest.Length);
        for (int i = 0; i < n; i++) dest[i] = back ? Back[i] : Front[i];
        return n;
    }
}

/// <summary>Analog stick response: radial deadzone, gain, clamping and conversion to the Vita's 0..255 bytes.</summary>
public static class StickMath
{
    /// <summary>Applies the deadzone (radially, so diagonals are not snapped to the axes), the sensitivity and the
    /// Y inversion. The output keeps the input direction; each component is clamped to -1..1.</summary>
    public static (float X, float Y) Apply(float x, float y, StickSettings s)
    {
        if (s.InvertY) y = -y;
        float mag = MathF.Sqrt(x * x + y * y);
        float dz = Math.Clamp(s.Deadzone, 0f, 0.95f);
        if (mag <= dz || mag == 0) return (0, 0);
        float scaled = (mag - dz) / (1f - dz) * Math.Max(0f, s.Sensitivity);
        float k = scaled / mag;
        return (Math.Clamp(x * k, -1f, 1f), Math.Clamp(y * k, -1f, 1f));
    }

    /// <summary>-1..1 to the Vita's 0..255 (128 = centre exactly).</summary>
    public static byte ToByte(float v) => v == 0 ? (byte)128 : (byte)Math.Clamp((int)((v + 1) * 127.5f), 0, 255);
}

/// <summary>A profile with its bindings parsed once, ready for per-frame mapping.</summary>
public sealed class CompiledProfile
{
    public ControllerProfile Profile { get; }
    readonly InputBinding[][] _bindings = new InputBinding[VitaControls.Count][];

    readonly InputCombo[] _pauseCombos;

    public CompiledProfile(ControllerProfile p)
    {
        Profile = p;
        foreach (var c in VitaControls.All) _bindings[(int)c] = p.Get(c).ToArray();
        _pauseCombos = p.GetCombos(VitaControl.PauseEmulation).ToArray();
        RearModifier = InputBinding.TryParse(p.TouchpadRearModifier, out var rm) ? rm : null;
        BothModifier = InputBinding.TryParse(p.TouchpadBothModifier, out var bm) ? bm : null;
        foreach (var c in VitaControls.All)
            if (VitaControls.IsMotion(c) && _bindings[(int)c].Length > 0) HasMotionBindings = true;
    }

    /// <summary>Some virtual motion control (tilt, twist, re-centre) is bound.</summary>
    public bool HasMotionBindings { get; }

    public InputBinding? RearModifier { get; }
    public InputBinding? BothModifier { get; }

    public ReadOnlySpan<InputBinding> this[VitaControl c] => _bindings[(int)c];

    /// <summary>The button combinations of a hotkey (<see cref="VitaControls.IsHotkey"/>); none for other controls.</summary>
    public ReadOnlySpan<InputCombo> Combos(VitaControl c) => c == VitaControl.PauseEmulation ? _pauseCombos : [];
}

public static class InputMapper
{
    /// <summary>Activation 0..1 of one host input.</summary>
    public static float Value(in InputBinding b, PadSnapshot? pad, IReadOnlySet<string>? keys)
    {
        switch (b.Kind)
        {
            case BindingKind.Key:
                return keys != null && b.Key != null && keys.Contains(b.Key) ? 1f : 0f;
            case BindingKind.PadButton:
                return pad is { IsGamepad: true } && (uint)b.Index < (uint)pad.Buttons.Length && pad.Buttons[b.Index] ? 1f : 0f;
            case BindingKind.PadAxis:
                if (pad is not { IsGamepad: true } || (uint)b.Index >= 6) return 0f;
                return b.Index >= (int)PadAxis.LeftTrigger ? Math.Clamp(pad.Axes[b.Index], 0f, 1f) : Math.Max(0f, pad.Axes[b.Index] * b.Dir);
            case BindingKind.JoyButton:
                return pad != null && b.Index >= 0 && b.Index < pad.JoyButtonCount && pad.JoyButtons[b.Index] ? 1f : 0f;
            case BindingKind.JoyAxis:
            {
                if (pad == null || b.Index < 0 || b.Index >= pad.JoyAxisCount) return 0f;
                float v = pad.JoyAxes[b.Index];
                return b.Dir == 2 ? Math.Clamp((v + 1f) * 0.5f, 0f, 1f) : Math.Clamp(v * b.Dir, 0f, 1f);
            }
            case BindingKind.JoyHat:
                return pad != null && b.Index >= 0 && b.Index < pad.JoyHatCount && (pad.JoyHats[b.Index] & b.Dir) != 0 ? 1f : 0f;
            default:
                return 0f;
        }
    }

    public static float Value(CompiledProfile p, VitaControl c, PadSnapshot? pad, IReadOnlySet<string>? keys)
        => Value(p, c, pad, keys, null, null);

    /// <summary>Whether a hotkey (<see cref="VitaControl.PauseEmulation"/>) is held: one of its single bindings, or
    /// every input of one of its combinations, at the profile's button threshold.</summary>
    public static bool HotkeyHeld(CompiledProfile p, VitaControl c, PadSnapshot? pad, IReadOnlySet<string>? keys)
    {
        float threshold = p.Profile.ButtonThreshold is > 0 and <= 1 ? p.Profile.ButtonThreshold : 0.5f;
        if (Value(p, c, pad, keys) >= threshold) return true;
        foreach (ref readonly var combo in p.Combos(c))
            if (combo.Value(pad, keys) >= threshold) return true;
        return false;
    }

    /// <summary>Activation of a control, ignoring the bindings <paramref name="skipA"/>/<paramref name="skipB"/>
    /// (touch pad layer modifiers that are in use).</summary>
    static float Value(CompiledProfile p, VitaControl c, PadSnapshot? pad, IReadOnlySet<string>? keys, InputBinding? skipA, InputBinding? skipB)
    {
        float v = 0;
        foreach (ref readonly var b in p[c])
        {
            if (b == skipA || b == skipB) continue;
            v = Math.Max(v, Value(b, pad, keys));
        }
        return v;
    }

    /// <summary>Maps a device's current state through its profile.</summary>
    public static MappedInput Map(CompiledProfile cp, PadSnapshot? pad, IReadOnlySet<string>? keys)
    {
        var p = cp.Profile;
        var r = MappedInput.Neutral;
        float threshold = p.ButtonThreshold is > 0 and <= 1 ? p.ButtonThreshold : 0.5f;

        // touch pad (DualShock 4 / DualSense...): fingers touch the front screen; holding the rear modifier (L2)
        // moves them to the rear pad, holding the "both" modifier (R2) touches both panels at once. A modifier
        // that is in use while a finger is down does not also press its own Vita control (L/R).
        InputBinding? usedRear = null, usedBoth = null;
        if (p.TouchpadEnabled && pad is { HasTouchpad: true })
        {
            bool any = false;
            for (int f = 0; f < PadSnapshot.MaxFingers; f++) any |= pad.FingerDown[f];
            if (any)
            {
                bool rear = cp.RearModifier is { } rm && Value(rm, pad, keys) >= threshold;
                bool both = cp.BothModifier is { } bm && Value(bm, pad, keys) >= threshold;
                if (rear) usedRear = cp.RearModifier;
                if (both) usedBoth = cp.BothModifier;
                for (int f = 0; f < PadSnapshot.MaxFingers; f++)
                {
                    if (!pad.FingerDown[f]) continue;
                    var t = new TouchPoint(MappedInput.FingerId + f, Math.Clamp(pad.FingerX[f], 0f, 1f), Math.Clamp(pad.FingerY[f], 0f, 1f));
                    if (both || !rear) r.AddTouch(false, t);
                    if (both || rear) r.AddTouch(true, t);
                }
            }
        }

        VitaButtons buttons = 0;
        for (int i = 0; i <= (int)VitaControl.R3; i++)
        {
            var c = (VitaControl)i;
            if (Value(cp, c, pad, keys, usedRear, usedBoth) >= threshold) buttons |= VitaControls.Button(c);
        }
        if (p.SwapConfirm) buttons = SwapCrossCircle(buttons);
        r.State.Buttons = buttons;

        (r.Lx, r.Ly) = StickMath.Apply(
            Value(cp, VitaControl.LStickRight, pad, keys) - Value(cp, VitaControl.LStickLeft, pad, keys),
            Value(cp, VitaControl.LStickDown, pad, keys) - Value(cp, VitaControl.LStickUp, pad, keys), p.LeftStick);
        (r.Rx, r.Ry) = StickMath.Apply(
            Value(cp, VitaControl.RStickRight, pad, keys) - Value(cp, VitaControl.RStickLeft, pad, keys),
            Value(cp, VitaControl.RStickDown, pad, keys) - Value(cp, VitaControl.RStickUp, pad, keys), p.RightStick);
        r.State.Lx = StickMath.ToByte(r.Lx); r.State.Ly = StickMath.ToByte(r.Ly);
        r.State.Rx = StickMath.ToByte(r.Rx); r.State.Ry = StickMath.ToByte(r.Ry);

        if (Value(cp, VitaControl.TouchFront, pad, keys) >= threshold) r.AddTouch(false, new TouchPoint(MappedInput.PointId, p.TouchFrontX, p.TouchFrontY));
        if (Value(cp, VitaControl.TouchBack, pad, keys) >= threshold) r.AddTouch(true, new TouchPoint(MappedInput.PointId + 1, p.TouchBackX, p.TouchBackY));
        return r;
    }

    public static VitaButtons SwapCrossCircle(VitaButtons b)
    {
        bool cross = (b & VitaButtons.Cross) != 0, circle = (b & VitaButtons.Circle) != 0;
        b &= ~(VitaButtons.Cross | VitaButtons.Circle);
        if (cross) b |= VitaButtons.Circle;
        if (circle) b |= VitaButtons.Cross;
        return b;
    }

    /// <summary>Merges two devices driving the same Vita (keyboard + controller): buttons are OR-ed, each stick axis
    /// takes the value further from the centre.</summary>
    public static MappedInput Combine(in MappedInput a, in MappedInput b, int idOffset = 100)
    {
        var r = a;
        r.State.Buttons |= b.State.Buttons;
        r.State.Lx = Further(a.State.Lx, b.State.Lx); r.State.Ly = Further(a.State.Ly, b.State.Ly);
        r.State.Rx = Further(a.State.Rx, b.State.Rx); r.State.Ry = Further(a.State.Ry, b.State.Ry);
        if (Math.Abs(b.Lx) > Math.Abs(r.Lx)) r.Lx = b.Lx;
        if (Math.Abs(b.Ly) > Math.Abs(r.Ly)) r.Ly = b.Ly;
        if (Math.Abs(b.Rx) > Math.Abs(r.Rx)) r.Rx = b.Rx;
        if (Math.Abs(b.Ry) > Math.Abs(r.Ry)) r.Ry = b.Ry;
        // contacts of the second device get distinct host ids so the two never merge into one finger
        for (int i = 0; i < b.FrontCount; i++) r.AddTouch(false, b.Front[i] with { Id = b.Front[i].Id + idOffset });
        for (int i = 0; i < b.BackCount; i++) r.AddTouch(true, b.Back[i] with { Id = b.Back[i].Id + idOffset });
        return r;
    }

    static byte Further(byte a, byte b) => Math.Abs(b - 128) > Math.Abs(a - 128) ? b : a;
}

/// <summary>
/// Detects which host input the player moved while a control waits for a binding: compares the controller state
/// against the state when listening started, so axes resting off-centre (raw triggers at -1) are not picked up.
/// </summary>
public static class InputCapture
{
    public const float AxisThreshold = 0.6f;

    /// <summary>Every button (and trigger, hat direction) held now that was not held in <paramref name="baseline"/>:
    /// recording a button combination (<see cref="InputCombo"/>). Sticks are left out: a combination is buttons.</summary>
    public static List<InputBinding> Held(PadSnapshot baseline, PadSnapshot now)
    {
        var held = new List<InputBinding>();
        if (now.IsGamepad)
        {
            for (int i = 0; i < now.Buttons.Length; i++)
                if (now.Buttons[i] && !baseline.Buttons[i]) held.Add(InputBinding.Of((PadButton)i));
            for (int i = (int)PadAxis.LeftTrigger; i <= (int)PadAxis.RightTrigger; i++)
                if (now.Axes[i] >= AxisThreshold && baseline.Axes[i] < AxisThreshold) held.Add(InputBinding.Of((PadAxis)i));
            return held;
        }
        for (int i = 0; i < now.JoyButtonCount; i++)
            if (now.JoyButtons[i] && !baseline.JoyButtons[i]) held.Add(InputBinding.JoyButton(i));
        for (int i = 0; i < now.JoyHatCount; i++)
        {
            int fresh = now.JoyHats[i] & ~baseline.JoyHats[i];
            for (int bit = 1; bit <= 8; bit <<= 1)
                if ((fresh & bit) != 0) held.Add(InputBinding.JoyHat(i, bit));
        }
        return held;
    }

    public static InputBinding? Detect(PadSnapshot baseline, PadSnapshot now)
    {
        if (now.IsGamepad)
        {
            for (int i = 0; i < now.Buttons.Length; i++)
                if (now.Buttons[i] && !baseline.Buttons[i]) return InputBinding.Of((PadButton)i);
            for (int i = 0; i < now.Axes.Length; i++)
            {
                float v = now.Axes[i], d = v - baseline.Axes[i];
                if (i >= (int)PadAxis.LeftTrigger)
                {
                    if (v >= AxisThreshold && d >= 0.4f) return InputBinding.Of((PadAxis)i);
                }
                else if (MathF.Abs(v) >= AxisThreshold && MathF.Abs(d) >= 0.4f) return InputBinding.Of((PadAxis)i, v < 0 ? -1 : 1);
            }
            return null;
        }
        for (int i = 0; i < now.JoyButtonCount; i++)
            if (now.JoyButtons[i] && !baseline.JoyButtons[i]) return InputBinding.JoyButton(i);
        for (int i = 0; i < now.JoyHatCount; i++)
        {
            int fresh = now.JoyHats[i] & ~baseline.JoyHats[i];
            for (int bit = 1; bit <= 8; bit <<= 1)
                if ((fresh & bit) != 0) return InputBinding.JoyHat(i, bit);
        }
        for (int i = 0; i < now.JoyAxisCount; i++)
        {
            float v = now.JoyAxes[i], b = baseline.JoyAxes[i], d = v - b;
            // an axis resting at one end (a trigger at -1) is bound over its full range
            if (b <= -0.8f && v >= 0.2f) return InputBinding.JoyAxis(i, 2);
            if (MathF.Abs(v) >= AxisThreshold && MathF.Abs(d) >= 0.4f) return InputBinding.JoyAxis(i, v < 0 ? -1 : 1);
        }
        return null;
    }
}
