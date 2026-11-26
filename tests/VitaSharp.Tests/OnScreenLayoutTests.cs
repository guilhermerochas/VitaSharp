using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Mobile.Core.Input;

namespace VitaSharp.Tests;

/// <summary>
/// The default on-screen layout against the game's image. The Vita has no virtual controls (its whole panel is the
/// game's) and games put touch icons at the edges of the screen: on a 2400x1080 phone the right stick of the old
/// overlay layout covered two thirds of the tab that closes Uncharted's "Tutorial: Journal" (x 0.93-0.99, y 0.72-0.84
/// of the image), so a tap there moved the stick instead of reaching the game. Where the bars beside the 16:9 image
/// are wide enough, the controls now sit in them; where they are not, a finger that starts on the image outside a
/// control's drawn shape is the game's.
/// </summary>
public sealed class OnScreenLayoutTests
{
    // phones and a tablet in landscape, and whether their bars beside the image take the controls
    public static TheoryData<int, int, bool> Screens => new()
    {
        { 2400, 1080, true },  // 20:9 (the Redmi Note 14)
        { 2340, 1080, true },  // 19.5:9
        { 3200, 1440, true },  // 20:9 at 1440p
        { 1920, 1080, false }, // 16:9: no bars
        { 2160, 1080, false }, // 18:9: bars too narrow
        { 2560, 1600, false }, // 16:10 tablet: bars above and below the image
    };

    // the image placed as the game screen places it (GameActivity.UpdateImageRect)
    private static OnScreenPad PadFor(int width, int height)
    {
        float scale = Math.Min(width / 960f, height / 544f);
        float iw = 960 * scale / width, ih = 544 * scale / height;
        return new OnScreenPad { Aspect = width / (float)height, Image = ((1 - iw) / 2, (1 - ih) / 2, iw, ih) };
    }

    // a finger at (x, y) of the image (0..1): whether the game gets it as a front touch at that point
    private static bool ReachesTheGame(OnScreenPad pad, float x, float y)
    {
        (float X, float Y, float Width, float Height) image = pad.Image;
        pad.Update([(1, image.X + x * image.Width, image.Y + y * image.Height)]);
        IReadOnlyList<TouchPoint> touches = pad.GetTouches(false);
        bool reached = touches.Count == 1 && Math.Abs(touches[0].X - x) < 1e-3f && Math.Abs(touches[0].Y - y) < 1e-3f
            && pad.GetController(0).Equals(ControllerState.Neutral);
        pad.Update([]);
        return reached;
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void UnchartedJournalTabReachesTheGame(int width, int height, bool _)
    {
        OnScreenPad pad = PadFor(width, height);
        Assert.True(ReachesTheGame(pad, 0.98f, 0.80f));
        // the whole tab, not only its right edge: the old right stick took its centre (0.96, 0.78)
        for (float y = 0.72f; y <= 0.84f; y += 0.02f)
        {
            for (float x = 0.93f; x <= 0.99f; x += 0.01f)
            {
                Assert.True(ReachesTheGame(pad, x, y), $"{width}x{height}: tab point ({x:F2}, {y:F2}) did not reach the game");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void NoDefaultControlIsOverTheImageWhenTheBarsTakeThem(int width, int height, bool barsTakeControls)
    {
        OnScreenPad pad = PadFor(width, height);
        (float X, float Y, float Width, float Height) image = pad.Image;
        if (!barsTakeControls)
        {
            Assert.Same(OnScreenPad.DefaultLayout, pad.Layout);
            return;
        }

        Assert.NotSame(OnScreenPad.DefaultLayout, pad.Layout);
        foreach (OnScreenControl control in pad.Layout)
        {
            Assert.True(control.X + control.Width <= image.X || control.X >= image.X + image.Width,
                $"{width}x{height}: {control.Control} is drawn over the image");
            Assert.True(control.X >= 0 && control.X + control.Width <= 1 && control.Y >= 0 && control.Y + control.Height <= 1,
                $"{width}x{height}: {control.Control} is off the screen");
        }

        // and none takes a finger on the image, the round controls' hit margin included: every point is the game's
        for (int row = 0; row <= 40; row++)
        {
            for (int column = 0; column <= 40; column++)
            {
                float x = column / 40f * 0.9999f, y = row / 40f * 0.9999f;
                Assert.True(ReachesTheGame(pad, x, y), $"{width}x{height}: image point ({x:F3}, {y:F3}) did not reach the game");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheDefaultLayoutHasEveryControlOnceAndTheyDoNotOverlap(int width, int height, bool _)
    {
        OnScreenPad pad = PadFor(width, height);
        VitaControl[] controls = pad.Layout.Select(c => c.Control).ToArray();
        Assert.Equal(OnScreenPad.DefaultLayout.Select(c => c.Control).OrderBy(c => c), controls.OrderBy(c => c));
        Assert.Equal(controls.Length, controls.Distinct().Count());
        // the centre of each control presses it (no other control is in front of it)
        foreach (OnScreenControl control in pad.Layout.Where(c => c.Shape == OnScreenShape.Button))
        {
            pad.Update([(1, control.X + control.Width / 2, control.Y + control.Height / 2)]);
            Assert.Equal(VitaControls.Button(control.Control), pad.GetController(0).Buttons);
            pad.Update([]);
        }
        // sticks large enough for a thumb, round on the screen
        foreach (OnScreenControl stick in pad.Layout.Where(c => c.Shape == OnScreenShape.Stick))
        {
            Assert.InRange(Math.Min(stick.Width * pad.Aspect, stick.Height), OnScreenPad.MinColumn, 0.33f);
        }
    }

    [Fact]
    public void TheDefaultLayoutFollowsTheScreenButASetLayoutIsKept()
    {
        OnScreenPad pad = PadFor(2400, 1080);
        IReadOnlyList<OnScreenControl> columns = pad.Layout;
        Assert.True(pad.UsesDefaultLayout);

        // the surface changes (rotation, split screen, a 16:9 external display): the default layout is rebuilt
        float iw = 1, ih = 1;
        pad.Aspect = 1920f / 1080f;
        pad.Image = (0, 0, iw, ih);
        Assert.Same(OnScreenPad.DefaultLayout, pad.Layout);

        // a layout the player arranged (or the system view's) is never replaced by the default one
        OnScreenControl[] custom = [new(VitaControl.Cross, "X", 0.4f, 0.4f, 0.1f, 0.1f)];
        pad.Layout = custom;
        Assert.False(pad.UsesDefaultLayout);
        pad.Aspect = 2400f / 1080f;
        pad.Image = PadFor(2400, 1080).Image;
        Assert.Same(custom, pad.Layout);

        pad.UseDefaultLayout();
        Assert.True(pad.UsesDefaultLayout);
        Assert.Equal(columns, pad.Layout);
    }
}
