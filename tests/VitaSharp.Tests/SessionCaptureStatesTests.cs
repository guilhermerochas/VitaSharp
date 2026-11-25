using VitaSharp.Core;
using VitaSharp.Core.Capture;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// The save states of a "Gravar tudo" capture besides the start's: the pre-roll keeps a ring of states in memory
/// (one every <see cref="SessionCaptureOptions.PreRollStateInterval"/>, from the newest one at or before the pre-roll's
/// start on, within <see cref="SessionCaptureOptions.PreRollStateMaxBytes"/>; nothing on disk), so a recording
/// starts with a state at most one interval before its pre-roll; and the player's mark (Shift+F8 / the pause menu)
/// takes a state of that moment into the recording (state_mark_N.vss, session.txt mark-state-N, the report's
/// "marked by the player" line naming rerun --from=markN). A synthetic title runs a counting thread, so the states
/// are real ones taken at safe points.
/// </summary>
[Collection("guest")]
public sealed class SessionCaptureStatesTests(GuestMemoryFixture fixture)
{
    private static SessionCapture NewCapture(Emulator emu, string root, Action<SessionCaptureOptions>? configure = null)
    {
        var options = new SessionCaptureOptions
        {
            Root = root,
            SetPresentTap = _ => { },
            StartState = false,
            GpuDump = false,
            Zip = false,
            Threads = () => "no threads",
            Info = () => "test info",
            PreRollStateInterval = 0.2,
            PreRollSlowStateInterval = 0.2,
        };
        configure?.Invoke(options);
        return new SessionCapture(emu, options);
    }

    /// <summary>The synthetic title's fixed range, freed from an earlier test of the shared guest memory (as
    /// CompatSaveStateTests does).</summary>
    private void FreeTitleRange()
    {
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }
    }

    private Emulator StartTitle(HleHarness h)
    {
        FreeTitleRange();
        Emulator emu = h.Emu;
        emu.LoadTitle(SyntheticTitle.Install(h.Root, "VSTS00002"));
        emu.Start();
        uint counter = SaveStateBootFromStateTests.StartCounter(emu, "VsTestCaptureCounter");
        HleHarness.Until(() => SaveStateBootFromStateTests.Read(emu, counter) > 5, "the first counts");
        return emu;
    }

    [Fact]
    public void ThePreRollKeepsItsStatesInMemoryAndTheRecordingStartsFromOne()
    {
        using var h = new HleHarness(fixture, "capstates");
        Emulator emu = StartTitle(h);
        string root = Path.Combine(h.Root, "captures");
        using SessionCapture cap = NewCapture(emu, root);
        try
        {
            cap.SetPreRoll(0.5);
            // several states taken: the ring holds those since the newest one at or before the pre-roll's start
            HleHarness.Until(() => cap.PreRollStatesTaken >= 4, "four pre-roll states");
            (int count, long bytes, long oldestNs) = cap.PreRollStates;
            Assert.True(bytes > 0);
            // only the newest state at or before the pre-roll's start and the newer ones (0.5 s of them) are held
            Assert.True(count is >= 1 and <= 4, $"{count} states held of {cap.PreRollStatesTaken} taken");
            Assert.False(Directory.Exists(Path.Combine(root, ".preroll")), "the pre-roll's states are in memory, not on disk");
            Assert.True(cap.Start("test"));
            HleHarness.Until(() => cap.IsRecording, "the recording to start");
            Assert.True(cap.Stop("test end"));
            Assert.True(cap.Idle.Wait(120_000), "the recording was not saved");
            string state = Path.Combine(cap.Dir!, "state_preroll.vss");
            Assert.True(File.Exists(state));
            Assert.True(SaveStates.CanBootFromState(state), "the in-memory state is a complete state file");
            string session = File.ReadAllText(Path.Combine(cap.Dir!, "session.txt"));
            Assert.Contains("preroll-state: state_preroll.vss guest-us ", session, StringComparison.Ordinal);
            Assert.True(oldestNs > 0);
        }
        finally
        {
            cap.SetPreRoll(0);
            emu.Stop();
        }
    }

    /// <summary>The ring never holds more than its memory limit (the newest state always stays).</summary>
    [Fact]
    public void ThePreRollsStatesStayWithinTheirMemoryLimit()
    {
        using var h = new HleHarness(fixture, "caplimit");
        Emulator emu = StartTitle(h);
        using SessionCapture cap = NewCapture(emu, Path.Combine(h.Root, "captures"));
        try
        {
            cap.SetPreRoll(60);
            HleHarness.Until(() => cap.PreRollStates.Count >= 1, "one pre-roll state");
            long limit = cap.PreRollStates.Bytes * 2 + cap.PreRollStates.Bytes / 2; // room for two states, not three
            cap.Options.PreRollStateMaxBytes = limit;
            int taken = cap.PreRollStatesTaken;
            // every state is inside the pre-roll: only the memory limit lets them go
            HleHarness.Until(() => cap.PreRollStatesTaken >= taken + 4, "four more pre-roll states taken");
            (int count, long bytes, _) = cap.PreRollStates;
            Assert.True(count is >= 1 and <= 2 && bytes <= limit, $"{count} states held ({bytes} bytes, limit {limit})");
        }
        finally
        {
            cap.SetPreRoll(0);
            emu.Stop();
        }
    }

    [Fact]
    public void AMarkTakesAStateOfThatMomentIntoTheRecording()
    {
        using var h = new HleHarness(fixture, "capmark");
        Emulator emu = StartTitle(h);
        string root = Path.Combine(h.Root, "captures");
        var status = new List<string>();
        using SessionCapture cap = NewCapture(emu, root, o => o.Status = s =>
        {
            lock (status)
            {
                status.Add(s);
            }
        });
        try
        {
            Assert.False(cap.MarkBug("test")); // nothing records yet
            Assert.True(cap.Start("test"));
            HleHarness.Until(() => cap.IsRecording, "the recording to start");
            Assert.True(cap.MarkBug("Shift+F8"));
            Assert.True(cap.Stop("test end")); // the stop waits for the mark's state
            Assert.True(cap.Idle.Wait(120_000), "the recording was not saved");
            string state = Path.Combine(cap.Dir!, "state_mark_1.vss");
            Assert.True(File.Exists(state));
            Assert.True(SaveStates.CanBootFromState(state));
            Assert.Contains("mark-state-1: state_mark_1.vss guest-us ", File.ReadAllText(Path.Combine(cap.Dir!, "session.txt")), StringComparison.Ordinal);
            Assert.Contains("\tbug 1 Shift+F8", File.ReadAllText(Path.Combine(cap.Dir!, "input.tsv")), StringComparison.Ordinal);
            string report = CaptureAnalyzer.Analyze(cap.Dir!, quiet: true);
            Assert.Contains("marked by the player at ", report, StringComparison.Ordinal);
            Assert.Contains("state_mark_1.vss, re-run from it with rerun --from=mark1", report, StringComparison.Ordinal);
        }
        finally
        {
            emu.Stop();
        }
    }

    /// <summary>The pre-roll takes its states at the short interval only once three of them stopped the game less
    /// than the budget (a frame), at the slow one before and when they stop it longer.</summary>
    [Theory]
    [InlineData(1e9, 0.2)]
    [InlineData(0.0, 0.6)]
    public void TheShortIntervalIsUsedOnlyWhileTheStatesStopTheGameLessThanAFrame(double budgetMs, double expected)
    {
        using var h = new HleHarness(fixture, "capinterval");
        Emulator emu = StartTitle(h);
        using SessionCapture cap = NewCapture(emu, Path.Combine(h.Root, "captures"), o =>
        {
            o.PreRollSlowStateInterval = 0.6;
            o.PreRollStateStopBudgetMs = budgetMs;
        });
        try
        {
            Assert.Equal(0.6, cap.CurrentStateInterval); // nothing measured yet
            cap.SetPreRoll(60);
            HleHarness.Until(() => cap.PreRollStatesTaken >= 3, "three pre-roll states");
            Assert.Equal(expected, cap.CurrentStateInterval);
        }
        finally
        {
            cap.SetPreRoll(0);
            emu.Stop();
        }
    }

    [Fact]
    public void StateNamesFilesAndSessionKeys()
    {
        Assert.Equal(("state_preroll.vss", "preroll-state"), (CaptureStates.FileName("preroll"), CaptureStates.SessionKey("preroll")));
        Assert.Equal(("state_start.vss", "start-state"), (CaptureStates.FileName("start"), CaptureStates.SessionKey("start")));
        Assert.Equal(("state_mark_3.vss", "mark-state-3"), (CaptureStates.FileName(CaptureStates.Mark(3)), CaptureStates.SessionKey("mark3")));
        Assert.False(CaptureStates.IsValid("mark0"));
        Assert.False(CaptureStates.IsValid("mark"));
        Assert.False(CaptureStates.IsValid("../x"));
        Assert.True(CaptureStates.IsValid("mark12"));
    }
}
