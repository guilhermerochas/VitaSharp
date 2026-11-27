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

/// <summary>Where a chip sits along the top edge of the game screen.</summary>
public enum GameChromeSlot
{
    Left,
    Center,
    Right,
}

/// <summary>
/// The game screen's top row: the fps counter in the top centre, the pause chip at the top right and the on-screen
/// controls chip at the top left. Sizes, style and auto-hide (<see cref="ChromeAutoHide"/>) are the chips' own.
/// </summary>
public static class GameChromeLayout
{
    /// <summary>The chips' distance from the screen's left and right edges (dp).</summary>
    public const int EdgeMarginDp = 12;

    /// <summary>The chips' distance from the screen's top edge (dp).</summary>
    public const int TopMarginDp = 6;

    public static GameChromeSlot SlotOf(GameChromeItem item) => item switch
    {
        GameChromeItem.Fps => GameChromeSlot.Center,
        GameChromeItem.Pause => GameChromeSlot.Right,
        _ => GameChromeSlot.Left,
    };
}
