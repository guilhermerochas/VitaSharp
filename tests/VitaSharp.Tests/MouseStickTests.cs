using System.Diagnostics;
using VitaSharp.Core.Input;

namespace VitaSharp.Tests;

public class MouseStickTests
{
    static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    /// <summary>Feeds a steady mouse speed (pixels per second) at a poll rate and returns the last deflection.</summary>
    static (float X, float Y) Steady(MouseStick s, float pxPerSecond, double pollHz, double seconds = 0.5)
    {
        long t = Ms(1000);
        s.Sample(t);
        (float X, float Y) last = default;
        double dt = 1000 / pollHz;
        for (double ms = 0; ms < seconds * 1000; ms += dt)
        {
            s.Add((float)(pxPerSecond * dt / 1000), 0);
            t += Ms(dt);
            last = s.Sample(t);
        }
        return last;
    }

    [Fact]
    public void SteadySpeedGivesAProportionalDeflectionWhateverThePollRate()
    {
        var slow = Steady(new MouseStick(new MouseSettings { Deadzone = 0 }), 300, 60);
        var fast = Steady(new MouseStick(new MouseSettings { Deadzone = 0 }), 300, 240);
        Assert.InRange(slow.X, 0.1f, 0.5f);
        Assert.InRange(fast.X / slow.X, 0.8f, 1.25f);
        var faster = Steady(new MouseStick(new MouseSettings { Deadzone = 0 }), 600, 60);
        Assert.InRange(faster.X / slow.X, 1.7f, 2.3f);
        Assert.Equal(0f, slow.Y);
    }

    [Fact]
    public void FullSpeedSaturatesAndTheStickReturnsToTheCentre()
    {
        var s = new MouseStick(new MouseSettings { ReturnMs = 80 });
        var (x, _) = Steady(s, 20000, 60);
        Assert.Equal(1f, x, 3);
        // the mouse stops: back at rest within ~ReturnMs
        long t = Ms(1000 + 500 + 150);
        Assert.Equal((0f, 0f), s.Sample(t));
    }

    [Fact]
    public void DeadzoneJumpCurveAndInvertY()
    {
        var s = new MouseStick(new MouseSettings { Deadzone = 0.3f, InvertY = true });
        long t = Ms(1000);
        s.Sample(t);
        s.Add(0, 2); // a tiny movement down
        var (x, y) = s.Sample(t + Ms(1));
        Assert.Equal(0f, x);
        Assert.InRange(y, -0.4f, -0.3f); // inverted, and at least the game's deadzone
        // curve 2: a half deflection reads as a quarter (after the deadzone mapping)
        var c = new MouseStick(new MouseSettings { Deadzone = 0, Curve = 2 });
        c.Sample(t);
        c.Add(0.5f / MouseStick.PixelGain, 0);
        Assert.Equal(0.25f, c.Sample(t + 1).X, 2);
    }

    [Fact]
    public void ModesMapOntoTheRightStick()
    {
        var s = new MouseStick(new MouseSettings { Deadzone = 0 });
        long t = Ms(1000);
        s.Sample(t);
        s.Add(-1f / MouseStick.PixelGain, 0);
        var right = s.Mapped(MouseMode.RightStick, t + 1);
        Assert.True(right.State.Rx < 10);
        Assert.Equal(128, right.State.Lx);
        Assert.Equal(128, s.Mapped(MouseMode.Motion, t + 2).State.Rx);
    }

    [Fact]
    public void PerGameModeAndCycle()
    {
        var m = new MouseSettings { Mode = MouseMode.Motion };
        m.Games["PCSA00029"] = nameof(MouseMode.RightStick);
        Assert.Equal(MouseMode.RightStick, m.ModeFor("PCSA00029"));
        Assert.Equal(MouseMode.Motion, m.ModeFor("PCSB01240"));
        Assert.Equal(MouseMode.LeftStick, MouseSettings.Next(MouseMode.RightStick));
        Assert.Equal(MouseMode.Motion, MouseSettings.Next(MouseMode.Off));
        // the settings survive input.json
        var cfg = new InputConfig { Keyboard = new ControllerProfile { Id = ControllerProfile.KeyboardId, Mouse = m } };
        var back = InputProfileStore.Deserialize(InputProfileStore.Serialize(cfg))!;
        Assert.Equal(MouseMode.RightStick, back.Keyboard!.Mouse.ModeFor("PCSA00029"));
    }
}
