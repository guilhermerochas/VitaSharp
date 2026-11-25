using VitaSharp.Core.Host;
using VitaSharp.Core.Localization;

namespace VitaSharp.Mobile.Core.Settings;

/// <summary>
/// "Microfone" in Ajustes (<see cref="AppPreferences.Microphone"/>), the desktop launcher's option of the same name with
/// the desktop's values (<see cref="MicrophoneSpec"/>): the phone's microphone (Android picks the headset's when one is
/// connected), off (silence) or the 440 Hz test tone.
/// </summary>
public static class MicrophoneSettings
{
    /// <summary>The picker's choices.</summary>
    public static IReadOnlyList<Choice> Choices =>
    [
        new("", Loc.Get("MicrophoneDefault")),
        new(MicrophoneSpec.None.Value, Loc.Get("MicrophoneNone")),
        new("tone", Loc.Get("MicrophoneTone")),
    ];

    /// <summary>The row's text for a setting.</summary>
    public static string Label(string? value)
    {
        return MicrophoneSpec.Parse(value).Kind switch
        {
            MicrophoneSpecKind.None => Loc.Get("MicrophoneNone"),
            MicrophoneSpecKind.Tone => Loc.Get("MicrophoneTone"),
            _ => Loc.Get("MicrophoneDefault"),
        };
    }
}
