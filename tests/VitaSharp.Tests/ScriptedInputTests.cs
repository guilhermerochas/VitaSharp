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
        ControllerState c = s.GetController(0);
        Assert.Equal(VitaButtons.Cross, c.Buttons & (VitaButtons.Cross | VitaButtons.Circle)); // circle starts at 500 s
        Assert.Equal((byte)255, c.Lx);
        Assert.Equal((byte)0, c.Ly);
        Assert.Equal((byte)128, c.Rx);
        IReadOnlyList<TouchPoint> front = s.GetTouches(false);
        Assert.Equal(new TouchPoint(1, 0.5f, 0.25f), Assert.Single(front));
        IReadOnlyList<TouchPoint> back = s.GetTouches(true);
        Assert.Equal(new TouchPoint(2, 0.1f, 0.9f), Assert.Single(back));
        Assert.False(s.HasHostKeySteps);
    }

    [Fact]
    public void FinishedAndFutureStepsAreIdle()
    {
        var s = new ScriptedInput("square@1000,rstick@1000:0:1:2");
        ControllerState c = s.GetController(0);
        Assert.Equal((VitaButtons)0, c.Buttons & VitaButtons.Square);
        Assert.Equal((byte)128, c.Ry);
        Assert.Empty(s.GetTouches(false));
    }

    [Fact]
    public void HostKeysAreReportedOnce()
    {
        var s = new ScriptedInput("key@0:F10,key@0:F8,key@1000:F11");
        Assert.True(s.HasHostKeySteps);
        List<string>? keys = s.TakeHostKeys();
        Assert.Equal(["F10", "F8"], keys);
        Assert.Null(s.TakeHostKeys()); // each fires once
    }

    [Fact]
    public void ASwipeStartsAtItsFirstPointAndMovesAlongTheLine()
    {
        // 600 s from (0.2, 0.8) to (0.8, 0.2): right after the start the finger is still at the first point, and it
        // is always on the segment between the two (x + y = 1)
        var s = new ScriptedInput("swipe@0:0.2:0.8:0.8:0.2:600");
        TouchPoint p = Assert.Single(s.GetTouches(false));
        Assert.Equal(1, p.Id);
        Assert.InRange(p.X, 0.2f, 0.21f);
        Assert.InRange(p.Y, 0.79f, 0.8f);
        Assert.Equal(1f, p.X + p.Y, 3);
        Assert.Empty(s.GetTouches(true));
        // the span form used by the guest-facing touch path gives the same point
        Span<TouchPoint> one = stackalloc TouchPoint[1];
        Assert.Equal(1, s.GetTouches(false, one));
        Assert.InRange(one[0].X, 0.2f, 0.21f);
        Assert.Equal(0, s.GetTouches(true, one));
        Assert.Equal(0, s.GetTouches(false, Span<TouchPoint>.Empty));
    }

    [Fact]
    public void KeyStepsGoThroughTheDefaultKeyboardProfile()
    {
        // X is Cross and W is the left stick up in the default keyboard profile
        var s = new ScriptedInput("key@0:X:600,key@0:W:600");
        ControllerState c = s.GetController(0);
        Assert.Equal(VitaButtons.Cross, c.Buttons & VitaButtons.Cross);
        Assert.Equal((byte)0, c.Ly);
        Assert.Equal((byte)128, c.Lx);
        // a game key is not an emulator key
        Assert.False(s.HasHostKeySteps);
        Assert.Null(s.TakeHostKeys());
    }

    [Fact]
    public void ThePauseHotkeyIsReportedAsPause()
    {
        // P is the pause key of the default keyboard profile; "f..+S" waits for a frame that has not been presented
        var s = new ScriptedInput("key@0:P,key@f999999999+1:P");
        Assert.True(s.HasHostKeySteps);
        Assert.Equal([ScriptedInput.PauseKey], s.TakeHostKeys());
        Assert.Null(s.TakeHostKeys());
    }

    /// <summary>A heading step is the PC's magnetometer test source: the script becomes the motion source and its
    /// samples carry the Earth's field for that heading; without one they carry none.</summary>
    [Fact]
    public void AHeadingStepGivesTheSamplesTheEarthsField()
    {
        var s = new ScriptedInput("heading@0:90:600");
        Assert.True(s.HasSensors);
        var samples = new MotionSample[4];
        Assert.Equal(1, s.ReadSamples(samples));
        System.Numerics.Vector3 expected = VitaSharp.Core.Input.MotionAxes.EarthField(90, 0, 0);
        Assert.True(System.Numerics.Vector3.Distance(expected, samples[0].MagneticField) < 1e-3f, $"{samples[0].MagneticField}");
        Assert.Equal(-1f, samples[0].Acceleration.Z, 4);

        var tiltOnly = new ScriptedInput("tilt@0:0:0:600");
        Assert.Equal(1, tiltOnly.ReadSamples(samples));
        Assert.Equal(System.Numerics.Vector3.Zero, samples[0].MagneticField);
    }

    [Fact]
    public void ATiltStepMakesTheScriptTheMotionSourceAndTurnsAtMost180DegreesPerSecond()
    {
        var idle = new ScriptedInput("cross@0:1");
        Assert.False(idle.HasSensors);
        Assert.Equal(0, idle.ReadSamples(new MotionSample[4]));

        var s = new ScriptedInput("tilt@0:30:0:600");
        Assert.True(s.HasSensors);
        var samples = new MotionSample[4];
        Assert.Equal(0, s.ReadSamples(Span<MotionSample>.Empty));
        // the first sample is the Vita lying flat: gravity straight down the screen's -z, no rotation yet
        Assert.Equal(1, s.ReadSamples(samples));
        Assert.Equal(0f, samples[0].Acceleration.X, 4);
        Assert.Equal(0f, samples[0].Acceleration.Y, 4);
        Assert.Equal(-1f, samples[0].Acceleration.Z, 4);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Thread.Sleep(50);
        int n = s.ReadSamples(samples);
        double elapsed = clock.Elapsed.TotalSeconds;
        Assert.Equal(1, n);
        // raising the top edge (pitch about x): gravity leaves -z, the rotation rate is about +x and bounded by 180
        // degrees/s, so after ~50 ms the Vita is part of the way to 30 degrees, not there yet
        MotionSample m = samples[0];
        float pitch = MathF.Atan2(MathF.Abs(m.Acceleration.Y), -m.Acceleration.Z);
        Assert.InRange(pitch, 0.001f, (float)(Math.PI * (elapsed + 0.01)));
        Assert.True(pitch < 30 * MathF.PI / 180 || elapsed > 0.16, $"pitch {pitch} after {elapsed} s");
        Assert.True(m.AngularVelocity.X > 0, $"gyro {m.AngularVelocity}");
        Assert.InRange(m.AngularVelocity.X, 0f, MathF.PI * 1.001f);
        Assert.Equal(0f, m.AngularVelocity.Y, 4);
        Assert.Equal(1f, m.Acceleration.Length(), 3);
    }

    [Fact]
    public void ADialogOverTheGameHidesTheScriptFromIt()
    {
        var s = new ScriptedInput("cross@0:600,tap@0:0.5:0.5:600");
        bool open = true;
        var gated = new DialogGatedInput(s, () => open);
        Assert.Equal(ControllerState.Neutral.Buttons, gated.GetController(0).Buttons);
        Assert.Empty(gated.GetTouches(false));
        Span<TouchPoint> one = stackalloc TouchPoint[1];
        Assert.Equal(0, gated.GetTouches(false, one));
        open = false;
        Assert.Equal(VitaButtons.Cross, gated.GetController(0).Buttons & VitaButtons.Cross);
        Assert.Single(gated.GetTouches(false));
        Assert.Equal(1, gated.GetTouches(false, one));
    }

    [Theory]
    [InlineData("circle@52,5")]       // a decimal comma
    [InlineData("circle")]            // no time
    [InlineData("circle@1@2")]        // two '@'
    [InlineData("nosuchbutton@1")]    // not a button
    [InlineData("tap@1:x:0.5")]       // not a number
    public void MalformedStepsAreRefused(string script)
    {
        FormatException e = Assert.Throws<FormatException>(() => new ScriptedInput(script));
        Assert.Contains("input step", e.Message);
    }
}
