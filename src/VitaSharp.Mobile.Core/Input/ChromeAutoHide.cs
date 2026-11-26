namespace VitaSharp.Mobile.Core.Input;

/// <summary>
/// When the game screen's pause chip shows. It sits over the top centre of the image, where the Vita's status bar
/// is; shown it takes the taps on itself, so it hides <see cref="HideAfterMs"/> after the screen was last touched
/// and the game gets every touch there (other emulators' overlay buttons fade the same way). A finger that goes down
/// away from it shows it again for that time; a finger on its place while it is hidden is the game's (the status
/// bar) and does not bring it back, so tapping there never opens the menu by surprise. The menu also opens with
/// Back and the controllers' hotkey.
/// </summary>
public sealed class ChromeAutoHide
{
    /// <summary>How long the chip stays after the last touch that showed it.</summary>
    public const long HideAfterMs = 4000;

    private long _visibleUntilMs;

    /// <summary>Shown from <paramref name="nowMs"/> (the screen opened).</summary>
    public ChromeAutoHide(long nowMs)
    {
        _visibleUntilMs = nowMs + HideAfterMs;
    }

    /// <summary>Whether the chip shows at <paramref name="nowMs"/>.</summary>
    public bool Visible(long nowMs) => nowMs < _visibleUntilMs;

    /// <summary>When it hides, if nothing touches the screen before (ms, the clock of the calls).</summary>
    public long HidesAtMs => _visibleUntilMs;

    /// <summary>A finger went down at <paramref name="nowMs"/>, on the chip's place or away from it.</summary>
    public void FingerDown(long nowMs, bool onChip)
    {
        if (onChip && !Visible(nowMs))
        {
            return;
        }

        _visibleUntilMs = nowMs + HideAfterMs;
    }
}
