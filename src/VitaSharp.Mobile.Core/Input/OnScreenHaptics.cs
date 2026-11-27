namespace VitaSharp.Mobile.Core.Input;

/// <summary>What one pulse plays: a predefined effect of the phone (tuned by its maker for its motor) or a one-shot.</summary>
public enum HapticKind
{
    /// <summary>VibrationEffect.EFFECT_TICK.</summary>
    Tick,
    /// <summary>VibrationEffect.EFFECT_CLICK.</summary>
    Click,
    /// <summary>VibrationEffect.EFFECT_HEAVY_CLICK.</summary>
    HeavyClick,
    /// <summary>VibrationEffect.createOneShot(<see cref="HapticPulse.Milliseconds"/>, <see cref="HapticPulse.Amplitude"/>).</summary>
    OneShot,
}

/// <summary>One pulse of the on-screen controls (<see cref="OnScreenHaptics.PulseFor"/>).</summary>
/// <param name="Kind">The effect.</param>
/// <param name="Milliseconds">Length of a <see cref="HapticKind.OneShot"/>; 0 for the predefined effects.</param>
/// <param name="Amplitude">Amplitude of a <see cref="HapticKind.OneShot"/>, 1..255, or
/// <see cref="OnScreenHaptics.DefaultAmplitude"/> when the motor has no amplitude control; 0 for the predefined
/// effects.</param>
public readonly record struct HapticPulse(HapticKind Kind, int Milliseconds, int Amplitude);

/// <summary>
/// The vibration of the on-screen controls (Ajustes > Controles > "Vibracao"): one short pulse for every control a
/// finger newly presses (<see cref="OnScreenPad.ControlPressed"/>), also while another finger holds a stick or a
/// button. The old rule vibrated only when no control was held before the touch, so a button pressed with the thumb
/// on the analog stick gave no feedback.
/// <para>
/// What a pulse plays (<see cref="PulseFor"/>): an 18 ms one-shot at the default amplitude was played by the phone's
/// vibrator service but not felt on a linear motor (Redmi Note 14), and the option's strength never reached it (the
/// motor reports no amplitude control). From Android 10 (API 29) a pulse is a predefined effect, which the maker tunes
/// for its motor and which the strength picks: weak = tick, medium = click, strong = heavy click. Before that, a
/// one-shot whose length grows with the strength, at the strength's amplitude when the motor can set it. The
/// front-end plays it with the media/game vibration usage (games' haptics in Android's VibrationAttributes), not the
/// touch-feedback usage that the system's own touch-vibration switch and intensity silence.
/// </para>
/// </summary>
public sealed class OnScreenHaptics
{
    /// <summary>The strengths of the option: 1 = weak, 2 = medium (the default), 3 = strong.</summary>
    public const int Weak = 1;
    public const int Medium = 2;
    public const int Strong = 3;

    /// <summary>Android's VibrationEffect.DEFAULT_AMPLITUDE.</summary>
    public const int DefaultAmplitude = -1;

    /// <summary>The first Android API level with the predefined effects (VibrationEffect.createPredefined).</summary>
    public const int PredefinedEffectsApi = 29;

    private readonly Action<HapticPulse> _vibrate;
    private readonly int _apiLevel;
    private readonly bool _hasAmplitudeControl;

    /// <param name="pad">The on-screen pad whose presses vibrate.</param>
    /// <param name="vibrate">Plays one pulse.</param>
    /// <param name="apiLevel">The phone's Android API level.</param>
    /// <param name="hasAmplitudeControl">Whether the motor sets the amplitude of a one-shot (Vibrator.hasAmplitudeControl).</param>
    public OnScreenHaptics(OnScreenPad pad, Action<HapticPulse> vibrate, int apiLevel, bool hasAmplitudeControl)
    {
        _vibrate = vibrate;
        _apiLevel = apiLevel;
        _hasAmplitudeControl = hasAmplitudeControl;
        pad.ControlPressed += OnPressed;
    }

    /// <summary>Off = no pulses (the "Vibracao" toggle).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><see cref="Weak"/>, <see cref="Medium"/> or <see cref="Strong"/>; other values count as medium.</summary>
    public int Strength { get; set; } = Medium;

    /// <summary>The amplitude of a one-shot for <paramref name="strength"/> (1..255).</summary>
    public static int Amplitude(int strength)
    {
        return strength switch
        {
            Weak => 60,
            Strong => 255,
            _ => 150,
        };
    }

    /// <summary>The pulse for <paramref name="strength"/> on a phone with <paramref name="apiLevel"/> and a motor
    /// with or without amplitude control (see the class remarks).</summary>
    public static HapticPulse PulseFor(int strength, int apiLevel, bool hasAmplitudeControl)
    {
        if (apiLevel >= PredefinedEffectsApi)
        {
            HapticKind kind = strength switch
            {
                Weak => HapticKind.Tick,
                Strong => HapticKind.HeavyClick,
                _ => HapticKind.Click,
            };
            return new HapticPulse(kind, 0, 0);
        }

        int milliseconds = strength switch
        {
            Weak => 20,
            Strong => 45,
            _ => 30,
        };
        return new HapticPulse(HapticKind.OneShot, milliseconds, hasAmplitudeControl ? Amplitude(strength) : DefaultAmplitude);
    }

    private void OnPressed(OnScreenControl control)
    {
        if (!Enabled)
        {
            return;
        }

        _vibrate(PulseFor(Strength, _apiLevel, _hasAmplitudeControl));
    }
}
