using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Memory;
using VitaSharp.Mobile.Core.Input;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The touch path of the phone, end to end: fingers fed to the on-screen pad as the app's MotionEvents give them (the
/// pointer ids Android reuses, the historical points of a move, the event times) and read back through SceTouch the
/// way libpaf (FW 3.74) reads it: sceTouchPeekRegion with 16 buffers every frame, every sample newer than the last
/// one it saw processed in order, press/move/release decided by the report id. The Vita's driver samples the panel at
/// every vblank into that ring, so a tap shorter than one of the shell's frames still reaches it.
/// </summary>
[Collection("guest")]
public sealed class AndroidTouchPathTests : IDisposable
{
    private const int Buffers = 16, BufferSize = 0x90;

    private readonly HleHarness _harness;
    private readonly ControlledGuestClock _clock;
    private long _hostUs = 1_000_000;
    private readonly OnScreenPad _pad;
    private readonly Touch _touch;
    private readonly uint _data;
    // libpaf's state: the newest timestamp it processed and the report ids down
    private ulong _lastStamp;
    private readonly HashSet<byte> _down = [];
    private readonly List<string> _events = [];

    public AndroidTouchPathTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "andtouch");
        _clock = ControlledGuestClock.Stop();
        _pad = new OnScreenPad(() => _hostUs);
        _harness.Emu.Host.Input = new AndroidInput(_pad, new AndroidGamepads());
        _touch = _harness.Module<Touch>();
        _data = _harness.Alloc(BufferSize * Buffers);
        TouchDiagnostics.Reset();
        Assert.Equal(0, Call(_touch, "sceTouchSetSamplingState", 0, 1u));
    }

    public void Dispose()
    {
        _clock.Dispose();
        _harness.Dispose();
    }

    /// <summary>Host and guest time move together (the guest runs at real speed).</summary>
    private void Wait(long ms)
    {
        _hostUs += ms * 1000;
        _clock.Advance(ms * 1000);
    }

    /// <summary>A finger at (x, y) of the image (0..1), pointer id <paramref name="pointer"/>, as of now.</summary>
    private (int, float, float) Finger(int pointer, float x, float y)
    {
        (float X, float Y, float Width, float Height) image = _pad.Image;
        return (pointer, image.X + x * image.Width, image.Y + y * image.Height);
    }

    private void Down(int pointer, float x, float y) => _pad.Update([Finger(pointer, x, y)], _hostUs);

    private void Up() => _pad.Update([], _hostUs);

    /// <summary>One libpaf frame: peek 16 buffers, process the samples newer than the last one seen. Returns how
    /// many new samples there were.</summary>
    private int Frame()
    {
        int buffers = Call(_touch, "sceTouchPeekRegion", 0, _data, (uint)Buffers, 0);
        Assert.InRange(buffers, 1, Buffers);
        int fresh = 0;
        for (int b = 0; b < buffers; b++)
        {
            uint buffer = _data + (uint)(b * BufferSize);
            ulong stamp = GuestMemory.Read<ulong>(buffer);
            if (stamp <= _lastStamp)
            {
                continue;
            }

            _lastStamp = stamp;
            fresh++;
            int reports = (int)GuestMemory.R32(buffer + 12);
            var now = new HashSet<byte>();
            for (int r = 0; r < reports; r++)
            {
                uint report = buffer + 16 + (uint)(r * 16);
                byte id = GuestMemory.Read<byte>(report);
                ushort x = GuestMemory.Read<ushort>(report + 2);
                now.Add(id);
                _events.Add(_down.Contains(id) ? $"move {id} {(int)Math.Round(x / 191.9)}" : $"press {id} {(int)Math.Round(x / 191.9)}");
            }

            foreach (byte id in _down)
            {
                if (!now.Contains(id))
                {
                    _events.Add($"release {id}");
                }
            }

            _down.Clear();
            _down.UnionWith(now);
        }

        return fresh;
    }

    private string[] Kinds() => [.. _events.Select(e => e.Split(' ')[0])];

    [Fact]
    public void ATapShorterThanTheShellsFrameIsAPressAndARelease()
    {
        // the shell at 8 fps (125 ms a frame); a 50 ms tap between two of its frames
        Frame();
        Wait(30);
        Down(0, 0.5f, 0.5f);
        Wait(50);
        Up();
        Wait(45);
        Assert.True(Frame() >= 2);
        Assert.Equal(["press", "release"], Kinds().Where(k => k != "move"));
        Assert.StartsWith("press ", _events[0]);
        Assert.Equal("5", _events[0].Split(' ')[2]); // x 0.5 of the panel (tenths)
        Frame();
        Assert.Empty(_down);
    }

    [Fact]
    public void TwoQuickTapsWithTheSamePointerIdAreTwoContacts()
    {
        // Android gives each first finger pointer id 0: two taps in one shell frame, on the left and on the right
        Frame();
        Wait(20);
        Down(0, 0.2f, 0.5f);
        Wait(40);
        Up();
        Wait(20);
        Down(0, 0.8f, 0.5f);
        Wait(40);
        Up();
        Wait(20);
        Frame();
        Assert.Equal(["press", "release", "press", "release"], Kinds().Where(k => k != "move"));
        string[] presses = [.. _events.Where(e => e.StartsWith("press ", StringComparison.Ordinal))];
        Assert.NotEqual(presses[0].Split(' ')[1], presses[1].Split(' ')[1]); // a new report id for the second tap
        Assert.Equal("2", presses[0].Split(' ')[2]);
        Assert.Equal("8", presses[1].Split(' ')[2]);
        // never one finger sliding from the first tap to the second
        Assert.DoesNotContain(_events, e => e.StartsWith("move ", StringComparison.Ordinal) && e.EndsWith(" 8", StringComparison.Ordinal) && e.Contains(presses[0].Split(' ')[1] + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void ADragKeepsThePointsBetweenFramesInOrder()
    {
        // a swipe of 120 ms from left to right in one MotionEvent batch per frame: the historical points carry it
        Frame();
        Down(0, 0.1f, 0.5f);
        Frame();
        long start = _hostUs;
        for (int step = 1; step <= 6; step++)
        {
            Wait(20);
            _pad.Update([Finger(0, 0.1f + step * 0.13f, 0.5f)], start + step * 20_000);
        }

        Frame();
        string[] moves = [.. _events.Where(e => e.StartsWith("move ", StringComparison.Ordinal))];
        Assert.True(moves.Length >= 5, string.Join(", ", _events));
        int[] xs = [.. moves.Select(m => int.Parse(m.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture))];
        Assert.Equal(xs.OrderBy(x => x), xs); // left to right, never back
        Assert.Single(moves.Select(m => m.Split(' ')[1]).Distinct()); // one contact
        Up();
        Wait(20);
        Frame();
        Assert.Equal("release", Kinds()[^1]);
    }

    [Fact]
    public void TimestampsRiseAndAPeekOfOneGetsTheNewest()
    {
        Frame();
        Wait(50);
        Down(0, 0.5f, 0.5f);
        Wait(50);
        int buffers = Call(_touch, "sceTouchPeekRegion", 0, _data, (uint)Buffers, 0);
        ulong previous = 0;
        for (int b = 0; b < buffers; b++)
        {
            ulong stamp = GuestMemory.Read<ulong>(_data + (uint)(b * BufferSize));
            Assert.True(stamp > previous, $"buffer {b}: {stamp} after {previous}");
            previous = stamp;
        }

        // a game that peeks one buffer sees the finger of now
        Assert.Equal(1, Call(_touch, "sceTouchPeek", 0, _data, 1u));
        Assert.Equal(1u, GuestMemory.R32(_data + 12));
        Assert.True(GuestMemory.Read<ulong>(_data) > previous);
    }

    [Fact]
    public void TheGuestPausedSamplesNothingInBetween()
    {
        // host time passes while the guest clock stands (the emulation paused): no samples for that time
        Frame();
        _hostUs += 500_000;
        Down(0, 0.5f, 0.5f);
        _hostUs += 100_000;
        Up();
        Assert.Equal(1, Frame()); // only the read itself
        Assert.Empty(_events);
    }

    [Fact]
    public void ReadHandsOverOnlyWhatItHasNotRead()
    {
        int first = 0, second = 0, third = 0;
        _harness.OnGuest(_ =>
        {
            first = Call(_touch, "sceTouchRead", 0, _data, (uint)Buffers);
            Wait(100);
            second = Call(_touch, "sceTouchRead", 0, _data, (uint)Buffers);
            Wait(20);
            third = Call(_touch, "sceTouchRead", 0, _data, 2u);
        });
        Assert.Equal(1, first);
        Assert.InRange(second, 5, 7); // the six vblanks of 100 ms and the read
        Assert.InRange(third, 1, 2);
        Assert.Contains("front reads=3", TouchDiagnostics.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanelsReportSixContactsOnTheFrontAndFourOnTheRear()
    {
        // SceTouch 3.74 configures the front panel for 6 contacts and the rear one for 4
        (int, float, float)[] fingers = [.. Enumerable.Range(0, 7).Select(i => Finger(i, 0.1f + i * 0.1f, 0.5f))];
        _pad.Update(fingers, _hostUs);
        Assert.Equal(1, Call(_touch, "sceTouchPeek", 0, _data, 1u));
        Assert.Equal(6u, GuestMemory.R32(_data + 12));
        _pad.Update([], _hostUs);
        _pad.RearTouch = true;
        _pad.Update(fingers[..5], _hostUs);
        Assert.Equal(0, Call(_touch, "sceTouchSetSamplingState", 1, 1u));
        Assert.Equal(1, Call(_touch, "sceTouchPeek", 1, _data, 1u));
        Assert.Equal(4u, GuestMemory.R32(_data + 12));
        Assert.Equal(1, Call(_touch, "sceTouchPeek", 0, _data, 1u));
        Assert.Equal(0u, GuestMemory.R32(_data + 12));
    }

    private sealed class EightFingers : IInputBackend
    {
        public ControllerState GetController(int port) => ControllerState.Neutral;

        public IReadOnlyList<TouchPoint> GetTouches(bool back) => [.. Enumerable.Range(1, 8).Select(i => new TouchPoint(i, i / 10f, 0.5f))];
    }

    [Fact]
    public void TheDriverReportsNoMoreContactsThanThePanelWhateverTheHostGives()
    {
        _harness.Emu.Host.Input = new EightFingers();
        Assert.Equal(1, Call(_touch, "sceTouchPeek", 0, _data, 1u));
        Assert.Equal(6u, GuestMemory.R32(_data + 12));
        Assert.Equal(0, Call(_touch, "sceTouchSetSamplingState", 1, 1u));
        Assert.Equal(1, Call(_touch, "sceTouchPeek", 1, _data, 1u));
        Assert.Equal(4u, GuestMemory.R32(_data + 12));
    }

    [Fact]
    public void ThePauseChipHidesAndATouchOnItsPlaceStaysTheGames()
    {
        var chrome = new ChromeAutoHide(1000);
        Assert.True(chrome.Visible(1000));
        Assert.False(chrome.Visible(1000 + ChromeAutoHide.HideAfterMs)); // left alone: hidden, the bar is the game's
        long now = 1000 + ChromeAutoHide.HideAfterMs + 500;
        chrome.FingerDown(now, onChip: true); // a tap on the Vita's status bar where the chip was
        Assert.False(chrome.Visible(now));
        chrome.FingerDown(now, onChip: false); // a touch elsewhere brings it back
        Assert.True(chrome.Visible(now + ChromeAutoHide.HideAfterMs - 1));
        chrome.FingerDown(now + 100, onChip: true); // shown: a tap on it is the chip's, and keeps it
        Assert.Equal(now + 100 + ChromeAutoHide.HideAfterMs, chrome.HidesAtMs);
        Assert.False(chrome.Visible(now + 100 + ChromeAutoHide.HideAfterMs));
    }

    [Fact]
    public void TheSamplerGivesEachContactItsOwnIdAndForgetsTheOldest()
    {
        long now = 0;
        var sampler = new TouchSampler(() => now);
        Span<TouchPoint> dest = stackalloc TouchPoint[8];
        sampler.Record([new TouchPoint(0, 0.1f, 0.1f)]);
        Assert.Equal(1, sampler.Current(dest));
        int first = dest[0].Id;
        now += 10;
        sampler.Record([new TouchPoint(0, 0.2f, 0.1f)]);
        sampler.Current(dest);
        Assert.Equal(first, dest[0].Id); // the same finger moving
        now += 10;
        sampler.Record([]);
        now += 10;
        sampler.Record([new TouchPoint(0, 0.9f, 0.9f)]);
        sampler.Current(dest);
        Assert.NotEqual(first, dest[0].Id); // pointer id 0 again, a new contact
        // the past: 15 us ago the finger was up; before the first change nothing was down
        Assert.Equal(0, sampler.At(5, dest));
        Assert.Equal(1, sampler.At(25, dest));
        Assert.Equal(0, sampler.At(1000, dest));
        for (int i = 0; i < TouchSampler.Capacity; i++)
        {
            now += 10;
            sampler.Record([new TouchPoint(1, 0.5f, 0.5f)]);
        }

        Assert.Equal(-1, sampler.At(TouchSampler.Capacity * 10 + 100, dest)); // older than the history kept
    }
}
