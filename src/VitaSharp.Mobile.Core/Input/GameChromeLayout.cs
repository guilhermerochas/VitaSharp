namespace VitaSharp.Mobile.Core.Input;

/// <summary>The game screen's top chips.</summary>
public enum GameChromeItem
{
    /// <summary>The fps counter.</summary>
    Fps,

    /// <summary>The pause chip (opens the pause menu).</summary>
    Pause,

    /// <summary>The chip that shows/hides the on-screen controls (<see cref="OnScreenVisibility"/>).</summary>
    Controls,
}

/// <summary>
/// The game screen's top row: the chips sit together in one group at the top centre of the screen, with the fps counter
/// in the middle of the group, the on-screen controls chip on its left and the pause chip on its right. Sizes, style
/// and auto-hide (<see cref="ChromeAutoHide"/>) are the chips' own.
/// </summary>
public static class GameChromeLayout
{
    /// <summary>The space between two chips of the group (dp).</summary>
    public const int GapDp = 8;

    /// <summary>The group's distance from the screen's top edge (dp).</summary>
    public const int TopMarginDp = 6;

    /// <summary>The chips from left to right; the group is centred on the screen.</summary>
    public static IReadOnlyList<GameChromeItem> Order { get; } = [GameChromeItem.Controls, GameChromeItem.Fps, GameChromeItem.Pause];
}
