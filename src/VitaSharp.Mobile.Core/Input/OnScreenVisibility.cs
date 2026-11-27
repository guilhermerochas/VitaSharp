using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Mobile.Core.Input;

/// <summary>
/// Whether the game screen's on-screen controls show: the player's choice (<see cref="AppPreferences.ShowOnScreenControls"/>,
/// one for every game, like the controls' opacity and "hide with a controller") unless a connected controller hides
/// them (<see cref="AppPreferences.HideOnScreenWithGamepad"/>). The button by the fps counter and the pause menu's
/// "Controles na tela" both go through <see cref="Toggle"/>, so they always agree and the choice is saved.
/// </summary>
public static class OnScreenVisibility
{
    /// <summary>Whether the controls show with these preferences and controllers.</summary>
    public static bool Shown(AppPreferences prefs, bool gamepadConnected) =>
        prefs.ShowOnScreenControls && !(prefs.HideOnScreenWithGamepad && gamepadConnected);

    /// <summary>Applies <see cref="Shown"/> to the pad (the screen opened, a controller came or went).</summary>
    public static void Apply(OnScreenPad pad, AppPreferences prefs, bool gamepadConnected)
    {
        pad.Visible = Shown(prefs, gamepadConnected);
    }

    /// <summary>
    /// Sets the player's choice to <paramref name="shown"/> and applies it to the pad at once (also over a connected
    /// controller: the player asked for them); the caller saves the preferences. Fingers held on a control are let
    /// go, so a hidden control is not left pressed.
    /// </summary>
    public static void Set(OnScreenPad pad, AppPreferences prefs, bool shown)
    {
        prefs.ShowOnScreenControls = shown;
        if (pad.Visible != shown)
        {
            pad.Reset();
        }

        pad.Visible = shown;
    }

    /// <summary>Shows the controls if hidden, hides them if shown (<see cref="Set"/>); returns whether they show now.</summary>
    public static bool Toggle(OnScreenPad pad, AppPreferences prefs)
    {
        bool shown = !pad.Visible;
        Set(pad, prefs, shown);
        return shown;
    }
}
