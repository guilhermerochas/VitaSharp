using Silk.NET.Input;
using System.Reflection;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Host;

namespace VitaSharp.Tests;

/// <summary>
/// The game window's overlays (message dialog, IME text entry, save state picker, trophy card, pause badge) driven as
/// the window drives them: keys, clicks and controller edges on a <see cref="WindowHost"/> that is created but never
/// shown (its native window only appears on Run), with the panels they hand to the renderer checked on their pixels and
/// the answers they give the game. The window's input profiles come from the test's own data folder.
/// </summary>
[Collection("launcher")]
public sealed class GameOverlayTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-overlay-" + Guid.NewGuid().ToString("N"));
    readonly string? _savedData = Environment.GetEnvironmentVariable(EnvVars.Data);
    readonly WindowHost _window;
    /// <summary>The last panel the overlay showed (null: hidden) and how many times it was drawn.</summary>
    (byte[]? Bgra, int W, int H) _shown;
    int _draws;
    ControllerState _pad = ControllerState.Neutral;

    public GameOverlayTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(EnvVars.Data, _root);
        _window = new WindowHost("overlay test");
    }

    public void Dispose()
    {
        _window.Dispose();
        Environment.SetEnvironmentVariable(EnvVars.Data, _savedData);
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void Show(byte[]? bgra, int w, int h)
    {
        _shown = (bgra, w, h);
        _draws++;
    }

    /// <summary>One update of the window loop (the overlays poll the controller there).</summary>
    void Tick()
    {
        var f = typeof(WindowHost).GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Action?)f.GetValue(_window))?.Invoke();
    }

    /// <summary>Presses and releases <paramref name="b"/> on the controller, with a window update at each edge.</summary>
    void PadPress(VitaButtons b)
    {
        _pad = ControllerState.Neutral with { Buttons = b };
        Tick();
        _pad = ControllerState.Neutral;
        Tick();
    }

    static (byte B, byte G, byte R) Px(byte[] bgra, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    /// <summary>Pixels that are neither the panel background nor its accent bar: the drawn text, boxes and images.</summary>
    static int Ink(byte[] bgra)
    {
        int n = 0;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0x27 || bgra[i + 1] != 0x1F || bgra[i + 2] != 0x1B)
            {
                n++;
            }
        }
        return n;
    }

    // ---------------------------------------------------------------- message dialog

    [Fact]
    public void MessageDialogAnswersWithTheKeyboardMouseAndController()
    {
        var overlay = new MessageOverlay(_window, Show, () => _pad);
        var buttons = new[] { new MessageDialogButton("Yes", 1), new MessageDialogButton("No", 2, IsCancel: true) };
        int answer = -1;

        // the panel: accent bar on top, background below, the buttons drawn inside it
        overlay.Open(new MessageDialogRequest("Save the game?", buttons), i => answer = i);
        Assert.True(overlay.IsOpen);
        Assert.Same(overlay, _window.TextCapture);
        var (bgra, w, h) = _shown;
        Assert.NotNull(bgra);
        Assert.Equal(600, w); // 1x: the window has no framebuffer yet
        Assert.Equal((0xFD, 0x8B, 0x3D), Px(bgra!, w, w / 2, 1));
        Assert.Equal((0x27, 0x1F, 0x1B), Px(bgra!, w, 2, h - 2));
        Assert.True(Ink(bgra!) > 1000);
        var first = bgra!;

        // arrows move the selection (the panel changes), Enter presses the selected button
        overlay.OnKey(Key.Right);
        Assert.False(first.AsSpan().SequenceEqual(_shown.Bgra));
        overlay.OnKey(Key.Enter);
        Assert.Equal(1, answer);
        Assert.False(overlay.IsOpen);
        Assert.Null(_window.TextCapture);
        Assert.Null(_shown.Bgra);

        // Escape is the cancel button
        overlay.Open(new MessageDialogRequest("Again?", buttons), i => answer = i);
        overlay.OnKey(Key.Escape);
        Assert.Equal(1, answer);

        // a click on the first button (no framebuffer: the panel sits at the origin)
        overlay.Open(new MessageDialogRequest("Click", buttons) { DefaultButton = 1 }, i => answer = i);
        var rects = overlay.GetType().GetField("_buttonRects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay) as SkiaSharp.SKRect[];
        Assert.Equal(2, rects!.Length);
        Assert.True(rects[0].Right <= rects[1].Left, "the buttons are side by side");
        overlay.OnClick(-100, -100); // outside: nothing
        Assert.True(overlay.IsOpen);
        overlay.OnClick((int)rects[0].MidX, (int)rects[0].MidY);
        Assert.Equal(0, answer);

        // the controller: the button held when the dialog opened is ignored until released, then Cross presses
        overlay.Open(new MessageDialogRequest("Pad", buttons), i => answer = i);
        answer = -1;
        _pad = ControllerState.Neutral with { Buttons = VitaButtons.Cross };
        Tick();
        Tick();
        Assert.Equal(-1, answer);
        _pad = ControllerState.Neutral;
        Tick();
        PadPress(VitaButtons.Right);
        PadPress(VitaButtons.Cross);
        Assert.Equal(1, answer);

        // Japanese layout: Circle confirms, Cross cancels
        overlay.Open(new MessageDialogRequest("Circle", buttons) { CircleConfirms = true }, i => answer = i);
        Tick();
        PadPress(VitaButtons.Cross);
        Assert.Equal(1, answer);
        overlay.Open(new MessageDialogRequest("Circle", buttons) { CircleConfirms = true }, i => answer = i);
        Tick();
        PadPress(VitaButtons.Circle);
        Assert.Equal(0, answer);

        // a progress message without buttons: the bar follows SetProgress, no key answers it, Close hides it
        overlay.Open(new MessageDialogRequest("Saving...", [], Progress: 0), i => answer = 99);
        var empty = _shown.Bgra!.ToArray();
        overlay.SetProgress(80, "Saving... 80%");
        Assert.True(Ink(_shown.Bgra!) > Ink(empty), "the bar filled");
        overlay.OnKey(Key.Enter);
        overlay.OnKey(Key.Escape);
        Assert.True(overlay.IsOpen);
        overlay.Close();
        Assert.False(overlay.IsOpen);
        Assert.NotEqual(99, answer);
    }

    [Fact]
    public void MessagePanelWrapsLongTextAndGrowsWithTheScale()
    {
        var one = MessageOverlay.Render("short", ["OK"], 0, null, 1f);
        var many = MessageOverlay.Render(string.Join(' ', Enumerable.Repeat("a rather long message", 40)), ["OK"], 0, null, 1f);
        var big = MessageOverlay.Render("short", ["OK"], 0, null, 2f);
        Assert.True(many.Height > one.Height + 100, $"{many.Height} vs {one.Height}");
        Assert.Equal(one.Width * 2, big.Width);
        Assert.InRange(big.Height, one.Height * 2 - 4, one.Height * 2 + 4);
        Assert.Single(one.Buttons);
        Assert.True(one.Buttons[0].Bottom <= one.Height && one.Buttons[0].Left >= 0);
        // the progress bar adds a row
        Assert.True(MessageOverlay.Render("short", ["OK"], 0, 50, 1f).Height > one.Height);
    }

    // ---------------------------------------------------------------- IME

    [Fact]
    public void ImeDialogEditsTextAndReturnsItOrNothing()
    {
        var overlay = new ImeOverlay(_window, Show, () => _pad);
        string? result = "unset";
        overlay.Open(new ImeDialogRequest("Name", "ab", 8, 0, false, 0), s => result = s);
        Assert.Same(overlay, _window.TextCapture);
        int before = Ink(_shown.Bgra!);
        foreach (char c in "cdef")
        {
            overlay.OnChar(c);
        }

        Assert.True(Ink(_shown.Bgra!) > before, "the typed text is drawn");
        overlay.OnKey(Key.Backspace);               // abcde
        overlay.OnKey(Key.Home);
        overlay.OnKey(Key.Delete);                  // bcde
        overlay.OnKey(Key.End);
        overlay.OnKey(Key.Left);
        overlay.OnChar('X');                        // bcdXe
        overlay.OnKey(Key.Right);
        foreach (char c in "123456")
        {
            overlay.OnChar(c); // up to the 8 characters allowed
        }

        overlay.OnKey(Key.Enter);
        Assert.Equal("bcdXe123", result);
        Assert.Null(_window.TextCapture);
        Assert.Null(_shown.Bgra);

        overlay.Open(new ImeDialogRequest("Name", "keep", 8, 0, false, 0), s => result = s);
        overlay.OnKey(Key.Escape);
        Assert.Null(result);

        // the controller: Start confirms, Circle cancels (after the opening press is released)
        overlay.Open(new ImeDialogRequest("Name", "pad", 8, 0, false, 0), s => result = s);
        Tick();
        PadPress(VitaButtons.Start);
        Assert.Equal("pad", result);
        overlay.Open(new ImeDialogRequest("Name", "pad", 8, 0, false, 0), s => result = s);
        Tick();
        PadPress(VitaButtons.Circle);
        Assert.Null(result);
    }

    [Fact]
    public void SceImeSessionReportsEditsAndLeavesClosingToTheGame()
    {
        var overlay = new ImeOverlay(_window, Show, () => _pad);
        var edits = new List<(string, int)>();
        int enters = 0, closes = 0;
        overlay.Begin("hi", 10, 0, false, (t, c) => edits.Add((t, c)), () => enters++, () => closes++);
        overlay.OnChar('!');
        overlay.OnKey(Key.Left);  // a caret move alone is not reported
        overlay.OnKey(Key.Enter); // the game is told, the entry stays open
        overlay.OnKey(Key.Escape);
        Assert.Equal([("hi!", 3)], edits);
        Assert.Equal((1, 1), (enters, closes));
        Assert.Same(overlay, _window.TextCapture);
        overlay.SetText("other", 2);
        overlay.OnChar('_');
        Assert.Equal(("ot_her", 3), edits[^1]);
        overlay.End();
        Assert.Null(_window.TextCapture);
    }

    [Fact]
    public void ImePanelMasksPasswordsAndScrollsMultilineText()
    {
        var plain = ImeOverlay.Render(new TextEntry("Password", "secret", 16, 0, false, false), 1f);
        var masked = ImeOverlay.Render(new TextEntry("Password", "secret", 16, 0, false, true), 1f);
        Assert.Equal((plain.Width, plain.Height), (masked.Width, masked.Height));
        Assert.False(plain.Bgra.AsSpan().SequenceEqual(masked.Bgra));
        // the box grows with the lines up to five
        int h1 = ImeOverlay.Render(new TextEntry("", "one", 64, 0, true, false), 1f).Height;
        int h3 = ImeOverlay.Render(new TextEntry("", "one\ntwo\nthree", 64, 0, true, false), 1f).Height;
        int h9 = ImeOverlay.Render(new TextEntry("", string.Join('\n', Enumerable.Range(0, 9)), 64, 0, true, false), 1f).Height;
        Assert.True(h3 > h1);
        Assert.Equal(h3 + 2 * 24, h9);
    }

    // ---------------------------------------------------------------- save states

    [Fact]
    public void SaveStatePickerSavesLoadsAndDeletesSlots()
    {
        string dir = Path.Combine(_root, "states");
        Directory.CreateDirectory(dir);
        string PathOf(int slot) => Path.Combine(dir, $"slot{slot}.vss");
        var overlay = new SaveStateOverlay(_window, Show, PathOf, _ => null, () => _pad);
        var saved = new List<int>();
        var loaded = new List<int>();
        var deleted = new List<int>();
        int opened = 0, closed = 0;
        overlay.SaveRequested += saved.Add;
        overlay.LoadRequested += loaded.Add;
        overlay.Deleted += deleted.Add;
        overlay.Opened += () => opened++;
        overlay.Closed += () => closed++;

        // an empty slot saves at once
        Assert.True(overlay.Open(1));
        Assert.Same(overlay, _window.TextCapture);
        Assert.NotNull(_shown.Bgra);
        overlay.OnKey(Key.Right);  // slot 2
        overlay.OnKey(Key.Enter);
        Assert.Equal([2], saved);
        Assert.False(overlay.IsOpen);
        Assert.Equal((1, 1), (opened, closed));

        // a slot holding a file that is no save state: it exists, cannot be loaded (nothing happens), asks to be
        // overwritten and can be deleted
        File.WriteAllBytes(PathOf(3), [1, 2, 3, 4]);
        overlay.Open(3);
        overlay.OnKey(Key.Tab);    // the load tab
        overlay.OnKey(Key.Enter);
        Assert.Empty(loaded);
        Assert.True(overlay.IsOpen);
        overlay.OnKey(Key.F5);     // save: overwrite? (No is selected)
        overlay.OnKey(Key.Enter);
        Assert.Equal([2], saved);
        overlay.OnKey(Key.F5);
        overlay.OnKey(Key.Left);   // Yes
        overlay.OnKey(Key.Enter);
        Assert.Equal([2, 3], saved);

        overlay.Open(3);
        overlay.OnKey(Key.Delete); // delete? Escape says no
        overlay.OnKey(Key.Escape);
        Assert.True(File.Exists(PathOf(3)));
        overlay.OnKey(Key.Delete);
        overlay.OnKey(Key.Tab);
        overlay.OnKey(Key.Enter);
        Assert.False(File.Exists(PathOf(3)));
        Assert.Equal([3], deleted);
        overlay.OnKey(Key.Escape);
        Assert.False(overlay.IsOpen);

        // the controller: down a row, Cross on an empty slot saves; another dialog open: the picker does not open
        overlay.Open(0);
        Tick();
        PadPress(VitaButtons.Down);  // slot 5
        PadPress(VitaButtons.Cross);
        Assert.Equal([2, 3, 5], saved);
        overlay.Open(0);
        Tick();
        PadPress(VitaButtons.Circle);
        Assert.False(overlay.IsOpen);
        var other = new MessageOverlay(_window, Show, () => _pad);
        other.Open(new MessageDialogRequest("busy", [new MessageDialogButton("OK", 0)]), _ => { });
        Assert.False(overlay.Open(1));
        other.Close();
    }

    [Fact]
    public void SaveStateNamesAndPlayTimes()
    {
        Assert.Equal("0:00", SaveStateOverlay.PlayTime(-5));
        Assert.Equal("1:05", SaveStateOverlay.PlayTime(65));
        Assert.Equal("2:03:04", SaveStateOverlay.PlayTime(2 * 3600 + 3 * 60 + 4));
        Assert.NotEqual(SaveStateOverlay.SlotName(0), SaveStateOverlay.SlotName(1));
        Assert.Contains("7", SaveStateOverlay.SlotName(7));
    }

    // ---------------------------------------------------------------- trophies and badges

    [Fact]
    public void TrophyCardsQueueAndEachOneIsShown()
    {
        var shown = new List<(byte[]? Bgra, int W, int H)>();
        int chimes = 0;
        using var toast = new TrophyToast((b, w, h) =>
        {
            lock (shown)
            {
                shown.Add((b, w, h));
            }
        }, () => 1088, () => chimes++);
        var gold = new TrophyInfo(1, TrophyGrade.Gold, false, 0, -1, "First", "");
        var bronze = new TrophyInfo(2, TrophyGrade.Bronze, false, 0, -1, "Second", "");
        toast.Enqueue(new TrophyUnlockedEvent("", "", gold, null));
        toast.Enqueue(new TrophyUnlockedEvent("", "", bronze, null)); // waits for the first card
        lock (shown)
        {
            Assert.Single(shown);
        }

        Assert.Equal(1, chimes);
        var card = shown[0];
        Assert.NotNull(card.Bgra);
        // 2x at 1088 lines
        Assert.Equal(TrophyToast.Render(new TrophyUnlockedEvent("", "", gold, null), 2f).Width, card.W);
        Assert.NotEqual(TrophyToast.GradeName(TrophyGrade.Gold), TrophyToast.GradeName(TrophyGrade.Bronze));
        Assert.False(TrophyToast.Render(new TrophyUnlockedEvent("", "", gold, null), 1f).Bgra.AsSpan()
            .SequenceEqual(TrophyToast.Render(new TrophyUnlockedEvent("", "", bronze, null), 1f).Bgra));
    }

    [Fact]
    public void PauseBadgeHasItsSignAndScales()
    {
        Assert.NotEqual(PauseBadge.Text(""), PauseBadge.Text("P"));
        Assert.Contains("P", PauseBadge.Text("P"));
        var badge = PauseBadge.Render("Paused (P)", 544)!.Value;
        var big = PauseBadge.Render("Paused (P)", 1088)!.Value;
        var plain = PauseBadge.Render("Paused (P)", 544, pauseSign: false)!.Value;
        var rec = PauseBadge.Render("REC", 544, recDot: true)!.Value;
        Assert.Equal(34, badge.Height);
        Assert.Equal(68, big.Height);
        Assert.True(plain.Width < badge.Width, "no sign: narrower");
        // the pause sign's orange bars, the recording's red dot
        static bool Has(byte[] bgra, byte r, byte g, byte b)
        {
            for (int i = 0; i < bgra.Length; i += 4)
            {
                if (bgra[i] == b && bgra[i + 1] == g && bgra[i + 2] == r)
                {
                    return true;
                }
            }
            return false;
        }
        Assert.True(Has(badge.Bgra, 0xFF, 0xB5, 0x47));
        Assert.False(Has(plain.Bgra, 0xFF, 0xB5, 0x47));
        Assert.True(Has(rec.Bgra, 0xE5, 0x2B, 0x2B));
    }
}
