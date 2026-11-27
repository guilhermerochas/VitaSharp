using VitaSharp.Mobile.Core.Input;

namespace VitaSharp.Tests;

/// <summary>The game screen's top chips stay together in one centred group, with the fps counter in its middle
/// (user, 27/11/2026: not spread to the screen corners).</summary>
public sealed class GameChromeLayoutTests
{
    [Fact]
    public void TheFpsCounterSitsBetweenTheControlsAndThePauseChips()
    {
        Assert.Equal([GameChromeItem.Controls, GameChromeItem.Fps, GameChromeItem.Pause], GameChromeLayout.Order);
    }

    [Fact]
    public void EveryChipIsInTheGroupOnce()
    {
        Assert.Equal(Enum.GetValues<GameChromeItem>().Order(), GameChromeLayout.Order.Order());
    }
}
