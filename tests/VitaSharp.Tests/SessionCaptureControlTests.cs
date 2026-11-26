using System.Reflection;
using VitaSharp.Core;
using VitaSharp.Core.Capture;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;
using VitaSharp.Desktop;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The desktop's side of "Gravar tudo" (VitaSharp.Desktop <see cref="SessionCaptureControl"/>,
/// docs/notes/session-capture.md): the options it reads (--capture-preroll[=S], --capture-max=S), the pre-roll that
/// is armed only when set (the detailed log does not arm it), the notes and GPU dump header it gives the capture, and the re-run of a
/// capture (<c>rerun DIR</c>, --capture-playback=DIR@start): its refusals for a folder that is not a capture, a missing
/// state or a game that is not installed, and the playback that replaces the host's input from the state load on and
/// records the re-run into DIR/reruns/.
/// </summary>
[Collection("guest")]
public sealed class SessionCaptureControlTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "capctl");

    public void Dispose() => _harness.Dispose();

    /// <summary>The pre-roll: --capture-preroll alone is the default 10 s, =SECONDS sets it (negative is none), the
    /// option wins over VITASHARP_CAPTURE_PREROLL, and without either there is none (the detailed log, explicit or
    /// not, no longer arms it). A re-run's own recording never has one, whatever the options say.</summary>
    [Fact]
    public void ThePreRollFollowsItsOptionOnly()
    {
        Assert.Equal(SessionCaptureControl.DefaultPreRoll, SessionCaptureControl.ExplicitPreRoll(["--capture-preroll"], null));
        Assert.Equal(3.5, SessionCaptureControl.ExplicitPreRoll(["--cpu=interp", "--capture-preroll=3.5"], null));
        Assert.Equal(0, SessionCaptureControl.ExplicitPreRoll(["--capture-preroll=-2"], null));
        Assert.Equal(7, SessionCaptureControl.ExplicitPreRoll([], "7"));
        Assert.Equal(2, SessionCaptureControl.ExplicitPreRoll(["--capture-preroll=2"], "7"));
        Assert.Null(SessionCaptureControl.ExplicitPreRoll([], null));
        Assert.Null(SessionCaptureControl.ExplicitPreRoll(["--capture-preroll=soon"], null));
        Assert.Null(SessionCaptureControl.ExplicitPreRoll(["--capture-prerollx=4"], null));

        Assert.Equal(0, SessionCaptureControl.PreRollFor(null));
        Assert.Equal(0, SessionCaptureControl.PreRollFor(0));
        Assert.Equal(3.5, SessionCaptureControl.PreRollFor(3.5));

        using var rerun = new SessionCaptureControl(_harness.Emu, new VulkanRendererOptions(), () => null, ["--capture-preroll=5"], null, null, Options());
        Assert.False(rerun.Capture.Armed);
        rerun.UpdatePreRoll();
        Assert.False(rerun.Capture.Armed);
    }

    /// <summary>The control fills the capture's hooks: the present tap goes to the renderer options (the renderer
    /// hands each presented frame to it), the run options and the longest recording come from the command line, and
    /// the notes say the build, the runtime and the host.</summary>
    [Fact]
    public void TheCaptureGetsTheRunsOptionsAndNotes()
    {
        var ro = new VulkanRendererOptions();
        string[] options = ["--capture-max=25", "--cpu=interp"];
        using (var unlimited = new SessionCaptureControl(_harness.Emu, ro, () => null, ["--capture-max=-5"], null, null, Options()))
        {
            Assert.Equal(0, unlimited.Capture.Options.MaxSeconds); // a negative limit is no limit, as VITASHARP_CAPTURE_MAX_SECONDS=0
        }

        using (var garbled = new SessionCaptureControl(_harness.Emu, ro, () => null, ["--capture-max=soon"], null, null, Options()))
        {
            Assert.Equal(SessionCaptureOptions.DefaultMaxSeconds, garbled.Capture.Options.MaxSeconds);
        }

        using var control = new SessionCaptureControl(_harness.Emu, ro, () => null, options, null, () => "test window 960x544", Options());
        SessionCaptureOptions o = control.Capture.Options;
        Assert.Equal("desktop", o.Platform);
        Assert.Equal(25, o.MaxSeconds);
        Assert.Same(options, o.RunOptions);
        var tap = new NullTap();
        o.SetPresentTap(tap);
        Assert.Same(tap, ro.PresentTap);
        o.SetPresentTap(null);
        Assert.Null(ro.PresentTap);

        string info = o.Info!();
        Assert.Contains("build: ", info);
        Assert.Contains($"{Environment.ProcessorCount} logical CPUs", info);
        Assert.Contains("debug-mode: " + (DebugMode.Enabled ? "on" : "off"), info);
        Assert.Contains("host: test window 960x544", info);
        Assert.DoesNotContain("renderer: ", info); // no renderer: a headless run without frames
        Assert.NotNull(o.Threads!());
        Assert.Equal(Log.FilePath, o.LogFile!());
        Core.Gpu.GpuDumpHeader header = o.DumpHeader!();
        Assert.Contains(header.Entries, e => e.Key == "debug-mode" && e.Value == (DebugMode.Enabled ? "on" : "off"));
        Assert.Contains(header.Entries, e => e.Key == "build");
        Assert.DoesNotContain(header.Entries, e => e.Key == "gpu"); // no renderer

    }

    /// <summary><c>rerun DIR</c> refuses (exit 1, nothing started) a folder that is not a capture, a capture without
    /// the save state it starts from, and a capture whose state cannot boot by itself (an earlier build's) and whose
    /// game is not installed in this data folder (a path recorded in the capture is never opened).</summary>
    [Fact]
    public void ARerunNeedsACaptureItsStateAndItsGame()
    {
        string dir = Path.Combine(_harness.Root, "not-a-capture");
        Directory.CreateDirectory(dir);
        Assert.Equal(1, SessionCaptureControl.Rerun(dir, []));

        File.WriteAllText(Path.Combine(dir, "session.txt"), "title-id: ZZZZ00000\napp-path: " + Path.Combine(_harness.Root, "no-such-game") + "\n");
        Assert.Equal(1, SessionCaptureControl.Rerun(dir, [])); // no state_preroll.vss nor state_start.vss
        Assert.Equal(1, SessionCaptureControl.Rerun(dir, ["--from=start"]));

        File.WriteAllBytes(Path.Combine(dir, "state_preroll.vss"), [0]);
        Assert.Equal(1, SessionCaptureControl.Rerun(dir, [])); // the game is not installed
        Assert.Equal(1, SessionCaptureControl.Rerun(dir, ["--from=start"])); // the preroll state is there, the start one not
    }

    /// <summary>--capture-playback without the option, or for a capture without the state it names, installs nothing
    /// and leaves the host's input alone.</summary>
    [Fact]
    public void PlaybackNeedsTheStateItStartsFrom()
    {
        var ro = new VulkanRendererOptions();
        Core.Host.IInputBackend? input = _harness.Emu.Host.Input;
        Assert.Null(SessionCaptureControl.Playback.Install(_harness.Emu, ro, () => null, ["--cpu=interp"]));
        string dir = Path.Combine(_harness.Root, "capture-without-states");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.txt"), "title-id: ZZZZ00000\nstart-state: none\npreroll-state: none (pre-roll off)\n");
        Assert.Null(SessionCaptureControl.Playback.Install(_harness.Emu, ro, () => null, [$"--capture-playback={dir}@start"]));
        Assert.Null(SessionCaptureControl.Playback.Install(_harness.Emu, ro, () => null, [$"--capture-playback={dir}"]));
        Assert.Same(input, _harness.Emu.Host.Input);
    }

    /// <summary>
    /// A re-run end to end on an emulator without a game: a capture is recorded (frames, input), the playback is
    /// installed from its start state's guest time, and the state load (<see cref="SaveStates.Loaded"/>) starts it:
    /// the recorded pad input replaces the host's from that guest time on, the re-run is recorded into DIR/reruns/
    /// and compared with the original, and the playback says it is done.
    /// </summary>
    [Fact]
    public void APlaybackReplaysTheCapturesInputAndRecordsTheRerun()
    {
        string original = RecordOriginal();
        long startUs = GuestClock.Microseconds;
        // the start state F8 saves (this capture was made without one): the guest time the playback starts from
        string session = Path.Combine(original, "session.txt");
        Assert.Contains("start-state: none", File.ReadAllText(session));
        File.WriteAllText(session, File.ReadAllText(session).Replace("start-state: none", $"start-state: state_start.vss guest-us {startUs} flip 0 ns 0 (1 ms to save)"));
        var pad = new InputEvent("pad", 0, startUs, 1, "0 00004000 128 128 128 128"); // cross held
        File.AppendAllText(Path.Combine(original, "input.tsv"), pad + "\n");

        var ro = new VulkanRendererOptions();
        Core.Host.IInputBackend? hostInput = _harness.Emu.Host.Input;
        IRandomHook? random = CaptureHooks.Random;
        EventInfo loaded = typeof(SaveStates).GetEvent(nameof(SaveStates.Loaded))!;
        Delegate[] before = LoadedHandlers();
        SessionCaptureControl.Playback? pb = null;
        try
        {
            pb = SessionCaptureControl.Playback.Install(_harness.Emu, ro, () => null, [$"--capture-playback={original}@start", "--capture-preroll=5"]);
            Assert.NotNull(pb);
            // the host's input is the playback, under the re-run's own recorder
            InputRecorder recorder = Assert.IsType<InputRecorder>(_harness.Emu.Host.Input);
            Assert.Same(pb!.Input, recorder.Inner);
            Assert.Same(pb.Input, CaptureHooks.Random);
            Assert.True(pb.Input.Events >= 1);
            Assert.False(pb.Input.Active); // nothing plays before the state is loaded
            Assert.Equal(Core.Host.ControllerState.Neutral, pb.Input.GetController(0));

            foreach (Delegate d in LoadedHandlers().Except(before))
            {
                ((Action<Emulator>)d)(_harness.Emu); // what SaveStates.Load raises once the state is in
            }

            Assert.True(pb.Input.Active);
            HleHarness.Until(() => pb.Done, "the re-run to end");
            Assert.Equal(Core.Host.VitaButtons.Cross, _harness.Emu.Host.Input!.GetController(0).Buttons & Core.Host.VitaButtons.Cross); // what the guest reads
            string reruns = Path.Combine(original, "reruns");
            string rerun = Assert.Single(Directory.GetDirectories(reruns));
            Assert.True(File.Exists(Path.Combine(rerun, "session.txt")));
            Assert.Empty(Directory.GetFiles(reruns, "*.zip")); // a re-run's recording is not zipped
            Assert.False(File.Exists(Path.Combine(rerun, "state_start.vss"))); // nor saves a state
        }
        finally
        {
            foreach (Delegate d in LoadedHandlers().Except(before))
            {
                loaded.RemoveEventHandler(null, d);
            }

            _harness.Emu.Host.Input = hostInput;
            CaptureHooks.Random = random;
        }
    }

    private static Delegate[] LoadedHandlers() =>
        (typeof(SaveStates).GetField(nameof(SaveStates.Loaded), BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as Delegate)?.GetInvocationList() ?? [];

    /// <summary>A capture of five presented frames, as F8 leaves it (without states).</summary>
    private string RecordOriginal()
    {
        IPresentTap? tap = null;
        var cap = new SessionCapture(_harness.Emu, new SessionCaptureOptions
        {
            Root = Path.Combine(_harness.Root, "original"),
            SetPresentTap = t => tap = t,
            StartState = false,
            GpuDump = false,
            Zip = false,
            Threads = () => "no threads",
            Info = () => "test info",
        });
        try
        {
            Assert.True(cap.Start("test"));
            HleHarness.Until(() => cap.IsRecording && tap != null, "the recording to start");
            const int Width = 32, Height = 16;
            byte[] px = new byte[Width * Height * 4];
            for (int f = 0; f < 5; f++)
            {
                Array.Fill(px, (byte)(f * 40));
                tap!.OnPresent(new PresentInfo { Present = f + 1, Flip = f, VBlank = f * 2, PresentNs = CaptureClock.NowNs, IntervalMs = 16.7f, Hitch = "" }, px, Width, Height);
            }

            Assert.True(cap.Stop("test end"));
            Assert.True(cap.Idle.Wait(120_000), "the recording was not saved");
            Assert.True(File.Exists(Path.Combine(cap.Dir!, "frames.vsfr")));
            return cap.Dir!;
        }
        finally
        {
            cap.Dispose();
        }
    }

    /// <summary>--capture-at=START:STOP is taken on the guest's frames, not on the host's time: the recording records
    /// from the moment the guest presents START (the guest waits there for it to begin) and is stopped when it
    /// presents STOP, however late the host's other threads run. A host thread polling the frame count started it
    /// late on a loaded machine, past STOP, and the capture had no HLE call (SessionCaptureRerunTests under the suite).
    /// The frames are presented here by hand, back to back, as a guest that runs far ahead of the host.</summary>
    [Fact]
    public void AScheduledCaptureRecordsExactlyTheGuestFramesItNames()
    {
        long framesBefore = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
        Action? presentedBefore = VitaSharp.Core.Hle.Modules.Display.Presented;
        try
        {
            VitaSharp.Core.Hle.Modules.Display.FramesPresented = 0;
            using var control = new SessionCaptureControl(_harness.Emu, new VulkanRendererOptions(), () => null, [], null, null, Options());
            control.StartScheduled(["--capture-at=3:6"]);
            for (long frame = 1; frame <= 6; frame++)
            {
                VitaSharp.Core.Hle.Modules.Display.FramesPresented = frame;
                VitaSharp.Core.Hle.Modules.Display.Presented?.Invoke();
                if (frame < 3)
                {
                    Assert.False(control.Capture.IsRecording, $"recording before the start frame (frame {frame})");
                }
                else if (frame < 6)
                {
                    Assert.True(control.Capture.IsRecording, $"not recording at guest frame {frame}");
                    Assert.True(HleCallTrace.Active, $"no HLE call trace at guest frame {frame}");
                }
            }

            Assert.True(control.Capture.Idle.Wait(120_000), "the recording was not saved");
            Assert.False(control.Capture.IsRecording);
            Assert.True(File.Exists(Path.Combine(control.Capture.Dir!, "hle_calls.tsv")), control.Capture.Dir);
        }
        finally
        {
            VitaSharp.Core.Hle.Modules.Display.FramesPresented = framesBefore;
            VitaSharp.Core.Hle.Modules.Display.Presented = presentedBefore;
        }
    }

    private SessionCaptureOptions Options() =>
        new() { Root = Path.Combine(_harness.Root, "captures"), SetPresentTap = null!, StartState = false, GpuDump = false, Zip = false };

    private sealed class NullTap : IPresentTap
    {
        public void OnPresent(in PresentInfo info, ReadOnlySpan<byte> rgba, int w, int h)
        {
        }
    }
}
