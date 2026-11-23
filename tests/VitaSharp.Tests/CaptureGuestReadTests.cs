using VitaSharp.Core.Capture;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;

namespace VitaSharp.Tests;

/// <summary>
/// A "Gravar tudo" re-run hands each input change to the same read of the guest that first saw it while recording
/// (<see cref="IGuestReadInput"/>: the controller and touch drivers number their reads, and the count is in the save
/// state), not to the read at the same guest time: the guest clock follows the host, so under load the re-run's reads
/// come at other guest times and a change landed one frame early or late (the CI's "99% look the same"). Captures
/// without read numbers still play back by guest time.
/// </summary>
public sealed class CaptureGuestReadTests
{
    private static readonly ControllerState s_cross = new() { Buttons = VitaButtons.Cross, Lx = 128, Ly = 128, Rx = 128, Ry = 128 };

    private sealed class FakeInput : IInputBackend
    {
        public ControllerState State = ControllerState.Neutral;
        public readonly List<TouchPoint> Touches = new();
        public ControllerState GetController(int port) => State;
        public IReadOnlyList<TouchPoint> GetTouches(bool back) => back ? [] : Touches;
    }

    [Fact]
    public void TheRecorderNotesTheGuestReadThatFirstSawEachChange()
    {
        var log = new List<InputEvent>();
        var source = new FakeInput();
        var recorder = new InputRecorder(source, null, log.Add);
        recorder.GetController(0, 41);
        recorder.GetController(0, 42);
        source.State = s_cross;
        // the system's own poll (power and PS button) sees the change first: it is not the guest's read
        recorder.GetController(0);
        recorder.GetController(0, 43);
        recorder.GetController(0, 44);
        source.Touches.Add(new TouchPoint(2, 0.5f, 0.25f));
        Span<TouchPoint> touches = stackalloc TouchPoint[8];
        Assert.Equal(1, recorder.GetTouches(false, touches, 7));
        recorder.GetTouches(false, touches, 8);
        // the plain read, after the guest's: no event of its own
        recorder.GetTouches(false, touches);

        Assert.Equal(["pad", "pad", "touch"], log.Select(e => e.Kind));
        Assert.EndsWith("00000000 128 128 128 128 read=41", log[0].Data);
        Assert.EndsWith("00004000 128 128 128 128 read=43", log[1].Data);
        Assert.Equal("front 2:0.5000:0.2500 read=7", log[2].Data);
    }

    [Fact]
    public void TheReRunHandsAChangeToTheSameReadWhateverTheGuestTime()
    {
        // recorded: idle from read 41, cross pressed at read 43 (guest time 1.5 s), a touch at read 7
        InputEvent[] events =
        [
            new("pad", 1, 1_000_000, 10, InputRecorder.FormatPad(0, ControllerState.Neutral) + " read=41"),
            new("pad", 2, 1_500_000, 12, InputRecorder.FormatPad(0, s_cross) + " read=43"),
            new("touch", 3, 1_600_000, 12, "front 2:0.5000:0.2500 read=7"),
        ];
        var playback = new InputPlayback(events.Select(e => InputEvent.Parse(e.ToString())!.Value));
        Assert.Equal(VitaButtons.None, playback.GetController(0, 43).Buttons); // inactive until the state is loaded
        playback.Activate(1_000_000);

        // whatever the guest clock reads now (this test's host time), the reads decide
        Assert.Equal(VitaButtons.None, playback.GetController(0, 42).Buttons);
        Assert.Equal(VitaButtons.Cross, playback.GetController(0, 43).Buttons);
        Assert.Equal(VitaButtons.Cross, playback.GetController(0, 500).Buttons);
        Span<TouchPoint> touches = stackalloc TouchPoint[8];
        Assert.Equal(0, playback.GetTouches(false, touches, 6));
        Assert.Equal(1, playback.GetTouches(false, touches, 7));
        Assert.Equal(new TouchPoint(2, 0.5f, 0.25f), touches[0]);
        Assert.Equal(0, playback.GetTouches(true, touches, 7));
    }

    /// <summary>The re-run records itself: its recorder wraps the playback and must pass the read number on (it asked
    /// the playback by guest clock, found no change there and the re-run never pressed the button: 69% of the frames).</summary>
    [Fact]
    public void TheReRunsOwnRecordingPassesTheReadNumberOnToThePlayback()
    {
        InputEvent[] events = [new("pad", 2, long.MaxValue / 2, 12, InputRecorder.FormatPad(0, s_cross) + " read=43")];
        var playback = new InputPlayback(events);
        playback.Activate(0);
        var log = new List<InputEvent>();
        var recorder = new InputRecorder(playback, null, log.Add);
        Assert.Equal(VitaButtons.None, recorder.GetController(0, 42).Buttons);
        Assert.Equal(VitaButtons.Cross, recorder.GetController(0, 43).Buttons);
        Assert.EndsWith("00004000 128 128 128 128 read=43", log[^1].Data);
    }

    [Fact]
    public void ACaptureWithoutReadNumbersStillPlaysByGuestTime()
    {
        InputEvent[] events = [new("pad", 1, 0, 10, InputRecorder.FormatPad(0, s_cross))];
        var playback = new InputPlayback(events);
        playback.Activate(0);
        // guest time 0 has passed: the change applies to any read
        Assert.Equal(VitaButtons.Cross, playback.GetController(0, 1).Buttons);
        Assert.Equal(VitaButtons.Cross, playback.GetController(0).Buttons);
    }

    [Fact]
    public void TheReadCountOfAStateFromAnEarlierBuildStartsAgain()
    {
        // a state saved before the count existed loads it as an empty array
        long[] reads = [];
        Assert.Equal(1, Ctrl.NextRead(ref reads, 0));
        Assert.Equal(2, Ctrl.NextRead(ref reads, 0));
        Assert.Equal(1, Ctrl.NextRead(ref reads, 1));
        Assert.True(reads.Length >= 2);
    }
}
