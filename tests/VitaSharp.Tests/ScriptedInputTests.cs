using VitaSharp.Core.Host;
using VitaSharp.Desktop;

namespace VitaSharp.Tests;

/// <summary>
/// The --input script of automated runs (VitaSharp.Desktop/ScriptedInput): steps timed in seconds from the start are
/// active only in their window, buttons, sticks (-1..1 to 0..255, centre 128) and touches come out as the emulator's
/// input, and malformed steps (a decimal comma, a missing '@', an unknown button) are refused with a message.
/// </summary>
public sealed class ScriptedInputTests
{
    [Fact]
    public void StepsAreActiveInTheirWindow()
    {
        var s = new ScriptedInput("cross@0:600,circle@500:5,lstick@0:1:-1:600,tap@0:0.5:0.25:600,btap@0:0.1:0.9:600");
        var c = s.GetController(0);
        Assert.Equal(VitaButtons.Cross, c.Buttons & (VitaButtons.Cross | VitaButtons.Circle)); // circle starts at 500 s
        Assert.Equal((byte)255, c.Lx);
        Assert.Equal((byte)0, c.Ly);
        Assert.Equal((byte)128, c.Rx);
        var front = s.GetTouches(false);
        Assert.Equal(new TouchPoint(1, 0.5f, 0.25f), Assert.Single(front));
        var back = s.GetTouches(true);
        Assert.Equal(new TouchPoint(2, 0.1f, 0.9f), Assert.Single(back));
        Assert.False(s.HasHostKeySteps);
    }

    [Fact]
    public void FinishedAndFutureStepsAreIdle()
    {
        var s = new ScriptedInput("square@1000,rstick@1000:0:1:2");
        var c = s.GetController(0);
        Assert.Equal((VitaButtons)0, c.Buttons & VitaButtons.Square);
        Assert.Equal((byte)128, c.Ry);
        Assert.Empty(s.GetTouches(false));
    }

    [Fact]
    public void HostKeysAreReportedOnce()
    {
        var s = new ScriptedInput("key@0:F10,key@0:F8,key@1000:F11");
        Assert.True(s.HasHostKeySteps);
        var keys = s.TakeHostKeys();
        Assert.Equal(["F10", "F8"], keys);
        Assert.Null(s.TakeHostKeys()); // each fires once
    }

    [Theory]
    [InlineData("circle@52,5")]       // a decimal comma
    [InlineData("circle")]            // no time
    [InlineData("circle@1@2")]        // two '@'
    [InlineData("nosuchbutton@1")]    // not a button
    [InlineData("tap@1:x:0.5")]       // not a number
    public void MalformedStepsAreRefused(string script)
    {
        var e = Assert.Throws<FormatException>(() => new ScriptedInput(script));
        Assert.Contains("input step", e.Message);
    }
}
