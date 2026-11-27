using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Mobile.Core.Input;
using VitaSharp.Mobile.Core.Settings;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// The default on-screen layout (the jogo.html overlay on every screen, with sticks about 15% smaller than jogo.html's
/// around the same centres) against the game's image, and the button that hides the controls
/// (<see cref="OnScreenVisibility"/>). Games put touch icons at the edges of the Vita's screen: with jogo.html's
/// 124 dp sticks the right one took the centre of the tab that closes Uncharted's "Tutorial: Journal" (x 0.93-0.99,
/// y 0.72-0.84 of the image) on a 2400x1080 phone.
/// </summary>
public sealed class OnScreenLayoutTests
{
    private readonly ITestOutputHelper _output;

    public OnScreenLayoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // phones and a tablet in landscape
    public static TheoryData<int, int> Screens => new()
    {
        { 2400, 1080 }, // 20:9 (the Redmi Note 14)
        { 2340, 1080 }, // 19.5:9
        { 1920, 1080 }, // 16:9
        { 2160, 1080 }, // 18:9
        { 2560, 1600 }, // 16:10 tablet
    };

    // jogo.html's controls (844x390 dp reference screen) other than the sticks: x, y, width, height in dp
    private static readonly (VitaControl Control, float X, float Y, float Width, float Height)[] s_jogoHtml =
    [
        (VitaControl.L, 18, 12, 108, 40),
        (VitaControl.R, 718, 12, 108, 40),
        (VitaControl.Up, 70, 92, 40, 44),
        (VitaControl.Down, 70, 168, 40, 44),
        (VitaControl.Left, 30, 132, 44, 40),
        (VitaControl.Right, 106, 132, 44, 40),
        (VitaControl.Triangle, 714, 84, 50, 50),
        (VitaControl.Square, 664, 134, 50, 50),
        (VitaControl.Circle, 764, 134, 50, 50),
        (VitaControl.Cross, 714, 184, 50, 50),
        (VitaControl.Select, 322, 346, 76, 30),
        (VitaControl.PS, 410, 346, 30, 30),
        (VitaControl.Start, 452, 346, 70, 30),
        (VitaControl.TouchBack, 22, 320, 52, 36),
    ];

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
    public void DefaultLayoutIsTheJogoHtmlOverlayOnEveryScreen(int width, int height)
    {
        OnScreenPad pad = PadFor(width, height);
        Assert.Same(OnScreenPad.DefaultLayout, pad.Layout);
        Assert.Equal(s_jogoHtml.Length + 2, pad.Layout.Count);
        foreach ((VitaControl control, float x, float y, float controlWidth, float controlHeight) in s_jogoHtml)
        {
            OnScreenControl drawn = pad.Layout.Single(c => c.Control == control);
            Assert.Equal((x / 844f, y / 390f, controlWidth / 844f, controlHeight / 390f), (drawn.X, drawn.Y, drawn.Width, drawn.Height));
        }
    }

    [Fact]
    public void SticksAreAboutFifteenPercentSmallerAroundJogoHtmlsCentres()
    {
        // jogo.html: 124 dp sticks at (112, 236) and (608, 236), centres (174, 298) and (670, 298)
        foreach ((VitaControl control, float centreX) in new[] { (VitaControl.LStickUp, 174f), (VitaControl.RStickUp, 670f) })
        {
            OnScreenControl stick = OnScreenPad.DefaultLayout.Single(c => c.Control == control);
            Assert.Equal(OnScreenShape.Stick, stick.Shape);
            Assert.Equal(centreX / 844f, stick.X + stick.Width / 2, 5);
            Assert.Equal(298f / 390f, stick.Y + stick.Height / 2, 5);
            float shrink = 1 - stick.Width * 844f / 124f;
            Assert.InRange(shrink, 0.13f, 0.20f);
            Assert.Equal(stick.Width * 844f, stick.Height * 390f, 3);
        }
    }

    [Theory]
    [InlineData(2400, 1080)]
    [InlineData(2340, 1080)]
    [InlineData(1920, 1080)]
    public void UnchartedJournalTabReachesTheGame(int width, int height)
    {
        OnScreenPad pad = PadFor(width, height);
        // the tab's centre and its lower right, where the tutorial's arrow points
        Assert.True(ReachesTheGame(pad, 0.96f, 0.78f), $"{width}x{height}: (0.96, 0.78) did not reach the game");
        Assert.True(ReachesTheGame(pad, 0.98f, 0.80f), $"{width}x{height}: (0.98, 0.80) did not reach the game");

        // how much of the whole tab (x 0.93-0.99, y 0.72-0.84) is the game's: recorded, the sticks did not move
        int free = 0, total = 0;
        for (int iy = 0; iy <= 12; iy++)
        {
            for (int ix = 0; ix <= 6; ix++)
            {
                total++;
                if (ReachesTheGame(pad, 0.93f + ix * 0.01f, 0.72f + iy * 0.01f))
                {
                    free++;
                }
            }
        }

        _output.WriteLine($"{width}x{height}: {free} of {total} points of the tab reach the game ({100.0 * free / total:F0}%)");
    }

    [Fact]
    public void HiddenControlsTakeNoFingerAndTheChoiceIsSaved()
    {
        OnScreenPad pad = PadFor(2400, 1080);
        var prefs = new AppPreferences();
        Assert.True(prefs.ShowOnScreenControls);
        OnScreenVisibility.Apply(pad, prefs, gamepadConnected: false);
        Assert.True(pad.Visible);

        // a finger held on the stick is let go when the controls hide
        OnScreenControl lstick = pad.Layout.Single(c => c.Control == VitaControl.LStickUp);
        pad.Update([(1, lstick.X + lstick.Width * 0.9f, lstick.Y + lstick.Height / 2)]);
        Assert.True(pad.IsPressed(lstick));
        Assert.False(OnScreenVisibility.Toggle(pad, prefs));
        Assert.False(pad.Visible);
        Assert.False(prefs.ShowOnScreenControls);
        Assert.False(pad.IsPressed(lstick));
        Assert.Equal(ControllerState.Neutral, pad.GetController(0));

        // hidden, no control takes a finger: one on each control's centre is a front touch (on the image) or nothing
        (float X, float Y, float Width, float Height) image = pad.Image;
        foreach (OnScreenControl control in pad.Layout)
        {
            float x = control.X + control.Width / 2, y = control.Y + control.Height / 2;
            pad.Update([(2, x, y)]);
            Assert.False(pad.IsPressed(control), $"{control.Control} took a finger while hidden");
            Assert.Equal(ControllerState.Neutral, pad.GetController(0));
            bool onImage = x >= image.X && x < image.X + image.Width && y >= image.Y && y < image.Y + image.Height;
            Assert.Equal(onImage ? 1 : 0, pad.GetTouches(false).Count);
            pad.Update([]);
        }

        // saved: the next game screen opens with them hidden
        string file = Path.Combine(Path.GetTempPath(), "vitasharp-onscreen-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            prefs.Save(file);
            var loaded = AppPreferences.Load(file);
            Assert.False(loaded.ShowOnScreenControls);
            OnScreenPad next = PadFor(2400, 1080);
            OnScreenVisibility.Apply(next, loaded, gamepadConnected: false);
            Assert.False(next.Visible);

            // shown again: saved as shown, and the stick takes its finger again
            Assert.True(OnScreenVisibility.Toggle(next, loaded));
            loaded.Save(file);
            Assert.True(AppPreferences.Load(file).ShowOnScreenControls);
            OnScreenControl stick = next.Layout.Single(c => c.Control == VitaControl.LStickUp);
            next.Update([(3, stick.X + stick.Width / 2, stick.Y + stick.Height / 2)]);
            Assert.True(next.IsPressed(stick));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AConnectedControllerHidesThemUnlessThePlayerShowsThem()
    {
        var prefs = new AppPreferences();
        Assert.True(OnScreenVisibility.Shown(prefs, gamepadConnected: false));
        Assert.False(OnScreenVisibility.Shown(prefs, gamepadConnected: true));
        prefs.HideOnScreenWithGamepad = false;
        Assert.True(OnScreenVisibility.Shown(prefs, gamepadConnected: true));
        prefs.ShowOnScreenControls = false;
        Assert.False(OnScreenVisibility.Shown(prefs, gamepadConnected: false));

        // the button over a connected controller: the player asked for them, they show
        prefs = new AppPreferences();
        OnScreenPad pad = PadFor(2400, 1080);
        OnScreenVisibility.Apply(pad, prefs, gamepadConnected: true);
        Assert.False(pad.Visible);
        Assert.True(OnScreenVisibility.Toggle(pad, prefs));
        Assert.True(pad.Visible);
    }
}
