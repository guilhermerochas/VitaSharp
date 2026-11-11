using VitaSharp.Core.Util;
using static VitaSharp.Core.Util.GameWindowFocus;

namespace VitaSharp.Tests;

/// <summary>"Ir para o jogo": which window of the game process is activated, what happens to it first, and the
/// Linux/macOS activation commands.</summary>
public class GameWindowFocusTests
{
    [Fact]
    public void PicksTheGameWindowOverInputMethodHelpers()
    {
        Candidate[] windows = new[]
        {
            new Candidate(1, "IME", "Default IME", false, false),
            new Candidate(2, "MSCTFIME UI", "MSCTFIME UI", false, false),
            new Candidate(3, "GLFW30", "VitaSharp - Uncharted 60 fps", true, false),
        };
        Assert.Equal(3, Pick(windows));
    }

    [Fact]
    public void PrefersAVisibleWindowAndSkipsOwnedOrUntitledOnes()
    {
        Candidate[] windows = new[]
        {
            new Candidate(4, "GLFW30", "", true, false),          // no title
            new Candidate(5, "#32770", "Dialog", true, true),     // owned (a dialog of the game window)
            new Candidate(6, "GLFW30", "VitaSharp", false, false), // hidden
            new Candidate(7, "GLFW30", "VitaSharp", true, false),
        };
        Assert.Equal(7, Pick(windows));
    }

    [Fact]
    public void FallsBackToTheHiddenWindowAndToNone()
    {
        Assert.Equal(6, Pick([new Candidate(6, "GLFW30", "VitaSharp", false, false)]));
        Assert.Equal(0, Pick([new Candidate(1, "IME", "Default IME", true, false)]));
        Assert.Equal(0, Pick([]));
    }

    [Theory]
    [InlineData(true, true, Step.Restore)]
    [InlineData(true, false, Step.Restore)]
    [InlineData(false, true, Step.Front)]
    [InlineData(false, false, Step.KeepHidden)]
    public void PlansTheWindowStep(bool minimized, bool visible, Step expected) => Assert.Equal(expected, Plan(minimized, visible));

    [Fact]
    public void BuildsTheActivationCommandPerSystem()
    {
        (string File, string[] Args) linux = ActivateCommand(1234, linux: true, macOS: false)!.Value;
        Assert.Equal("xdotool", linux.File);
        Assert.Equal(["search", "--onlyvisible", "--pid", "1234", "windowactivate"], linux.Args);
        (string File, string[] Args) mac = ActivateCommand(1234, linux: false, macOS: true)!.Value;
        Assert.Equal("osascript", mac.File);
        Assert.Contains("unix id is 1234", mac.Args[1]);
        Assert.Null(ActivateCommand(1234, linux: false, macOS: false));
    }
}
