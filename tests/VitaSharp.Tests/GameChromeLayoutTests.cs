using VitaSharp.Mobile.Core.Input;

namespace VitaSharp.Tests;

/// <summary>The Android game screen's top row: fps counter in the centre, pause chip on the right, on-screen controls
/// chip on the left (GameChromeLayout, used by GameActivity.TopBar for every chip).</summary>
public sealed class GameChromeLayoutTests
{
    [Fact]
    public void EachChipHasItsPlace()
    {
        Assert.Equal(GameChromeSlot.Center, GameChromeLayout.SlotOf(GameChromeItem.Fps));
        Assert.Equal(GameChromeSlot.Right, GameChromeLayout.SlotOf(GameChromeItem.Pause));
        Assert.Equal(GameChromeSlot.Left, GameChromeLayout.SlotOf(GameChromeItem.Controls));
    }

    /// <summary>The game screen places all three chips through the layout, in a bar as wide as the screen.</summary>
    [Fact]
    public void TheGameScreenPlacesEveryChipByTheLayout()
    {
        string source = GameActivitySource();
        Assert.Contains("bar.AddView(_fps, ChromeSlot(GameChromeItem.Fps));", source);
        Assert.Contains("bar.AddView(pause, ChromeSlot(GameChromeItem.Pause));", source);
        Assert.Contains("bar.AddView(controlsChip, ChromeSlot(GameChromeItem.Controls));", source);
        Assert.Contains("root.AddView(TopBar(), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent", source);
    }

    private static string GameActivitySource([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        string dir = Path.GetDirectoryName(source)!;
        while (!File.Exists(Path.Combine(dir, "VitaSharp.sln")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("repository root");
        }

        return File.ReadAllText(Path.Combine(dir, "src", "VitaSharp.Android", "Platforms", "Android", "Game", "GameActivity.cs"));
    }
}
