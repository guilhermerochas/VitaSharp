using System.Text.RegularExpressions;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// The front-end's game runner (Runner) on <see cref="SyntheticTitle"/>, in a child process (<see cref="DesktopProcess"/>):
/// the executable boots and shows frames, the scripted input reaches the guest's pad reads, the headless renderer on
/// the software Vulkan device presents what the guest drew, and the run ends on its frame limit. Every run has a
/// scratch data folder of its own.
/// </summary>
public sealed class RunnerProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-runner-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;

    public RunnerProcessTests()
    {
        Directory.CreateDirectory(_root);
        _app = SyntheticTitle.Install(Path.Combine(_root, "fs"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static long FramesOf(string output)
    {
        Match m = System.Text.RegularExpressions.Regex.Match(output, @"Run finished: (\d+) frames");
        Assert.True(m.Success, "no 'Run finished' line:\n" + Tail(output));
        return long.Parse(m.Groups[1].Value);
    }

    private static string Tail(string s) => s.Length > 6000 ? s[^6000..] : s;

    [Fact]
    public void HeadlessRunBootsTheTitleAndStopsAtItsFrameLimit()
    {
        DesktopProcess.Result r = DesktopProcess.Run(_root, ["run", _app, "--headless", "--frames=20", "--turbo", "--cpu=interp"]);
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Contains("Loading VSTS00001", r.Output);
        Assert.Contains("framebuffer 0x81001000 480x272", r.Output);
        long frames = FramesOf(r.Output);
        // the runner checks the limit every 50 ms and the turbo test app presents frames faster than that, so the count
        // overshoots by a load-dependent amount (42 and 53 seen); a limit that was ignored would run to the 1800 s timeout
        Assert.InRange(frames, 20, 500);
        // the session log went to the test's data folder
        Assert.True(File.Exists(Path.Combine(_root, "logs", "last.log")));
    }

    [SoftwareVulkanFact]
    public void ScriptedInputChangesWhatTheGuestDrawsInTheDumpedFrames()
    {
        string frames = Path.Combine(_root, "frames");
        // cross held from frame 30 for 30 frames: the guest fills its framebuffer with the "pressed" colour meanwhile
        DesktopProcess.Result r = DesktopProcess.Run(_root, ["run", _app, "--headless", "--vulkan", "--no-vsync", "--frames=90",
            $"--dump-frames={frames}", "--dump-every=1", "--input=cross@f30:30"]);
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        string[] pngs = Directory.GetFiles(frames, "frame_*.png").Order().ToArray();
        Assert.True(pngs.Length >= 60, $"{pngs.Length} frames dumped:\n{Tail(r.Output)}");
        ((byte R, byte G, byte B) Center, int Width, int Height)[] colours = pngs.Select(p => DesktopProcess.Probe(p)).ToArray();
        Assert.All(colours, c => Assert.Equal((SyntheticTitle.Width, SyntheticTitle.Height), (c.Width, c.Height)));
        // I = idle, P = pressed, ? = anything else, in present order
        string seq = string.Concat(colours.Select(c => c.Center == SyntheticTitle.Idle ? 'I' : c.Center == SyntheticTitle.Pressed ? 'P' : '?'));
        // before the press, during it (a frame or two of latency at each end) and after it
        Assert.Matches(@"^I{10,}P{20,40}I{5,}$", seq); // (an instrumented or loaded run drops a present now and then)
        // the contact sheets of the thumbnails (30 a sheet) and the frame metrics: two different pictures in all
        Assert.True(File.Exists(Path.Combine(frames, "sheet.png")));
        Assert.True(File.Exists(Path.Combine(frames, "sheet_2.png")));
        string[][] metrics = File.ReadAllLines(Path.Combine(frames, "metrics.csv")).Skip(1).Select(l => l.Split(',')).ToArray();
        Assert.True(metrics.Length >= 60);
        Assert.Equal(2, metrics.Select(m => m[^1]).Distinct().Count());
    }

    /// <summary>
    /// A test scenario (<c>test &lt;scenario.txt&gt;</c>) whose run follows a screen-driven navigation script: shots of
    /// the frame before and while Cross is held show the guest's two colours, the script's quit ends the run, the
    /// sampling profiler reports, and the scenario writes its report, sheet and frame metrics.
    /// </summary>
    [SoftwareVulkanFact]
    public void ScenarioWithANavigationScriptShotsAndReport()
    {
        string nav = Path.Combine(_root, "steps.nav"), shots = Path.Combine(_root, "shots"), scenario = Path.Combine(_root, "synthetic.txt");
        File.WriteAllLines(nav, ["wait 10", "shot idle", "hold cross", "wait 10", "shot pressed", "hold none", "wait 5", "stable 0.3 20", "log steps done", "quit"]);
        File.WriteAllLines(scenario, ["title=VSTS00001", $"args=--vulkan --no-vsync --dump-every=10 --profile \"--nav={nav}\" \"--nav-out={shots}\""]);
        DesktopProcess.Result r = DesktopProcess.Run(_root, ["test", scenario], timeoutMs: 180_000);
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.Equal(SyntheticTitle.Idle, DesktopProcess.Probe(Path.Combine(shots, "idle.png")).Center);
        Assert.Equal(SyntheticTitle.Pressed, DesktopProcess.Probe(Path.Combine(shots, "pressed.png")).Center);
        string outDir = Path.Combine(_root, "tests", "synthetic");
        string log = File.ReadAllText(Path.Combine(outDir, "log.txt"));
        Assert.Contains("NAV", log);
        Assert.Contains("steps done", log);
        Assert.True(File.Exists(Path.Combine(outDir, "report.txt")));
        Assert.True(File.Exists(Path.Combine(outDir, "sheet.png")));
        Assert.NotEmpty(Directory.GetFiles(outDir, "frame_*.png"));
    }

    /// <summary>
    /// A window run (the window is created hidden: VITASHARP_HIDDEN_WINDOW=1, nothing on screen) driven by the script's
    /// host keys: the swapchain presents the guest's frames (dumped), F5 / F7 save and load the quick state, F6 opens the
    /// save state picker over the game (its panel dumped with --overlay-dump) and Circle closes it, F10 captures, F1
    /// changes the internal resolution (a new renderer on the same window), and "Close" closes the window as its X does.
    /// </summary>
    [HiddenWindowVulkanFact]
    public void HiddenWindowRunPresentsAndAnswersTheHostKeys()
    {
        string frames = Path.Combine(_root, "frames"), overlays = Path.Combine(_root, "overlays");
        DesktopProcess.Result r = DesktopProcess.Run(_root, ["run", _app, "--no-vsync", $"--dump-frames={frames}", "--dump-every=1", $"--overlay-dump={overlays}",
            "--input=cross@f20:10,key@f40:F5,key@f60:F7,key@f80:F6,circle@f80+1:0.3,key@f100:F10,key@f120:F1,key@f160:Close"], timeoutMs: 180_000, systemLoader: true);
        Assert.True(r.ExitCode == 0, Tail(r.Output));
        Assert.True(File.Exists(Path.Combine(_root, "savestates", "VSTS00001", "quick.vss")), Tail(r.Output));
        Assert.Contains("Save state picker: open", r.Output);
        Assert.NotEmpty(Directory.GetFiles(overlays, "overlay_*.png"));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "diagnostics"), "VSTS00001_*"));
        (byte R, byte G, byte B)[] colours = Directory.GetFiles(frames, "frame_*.png").Order().Select(f => DesktopProcess.Probe(f).Center).ToArray();
        Assert.True(colours.Length >= 60, $"{colours.Length} frames:\n{Tail(r.Output)}");
        Assert.Contains(SyntheticTitle.Pressed, colours);
        Assert.Equal(SyntheticTitle.Idle, colours[^1]);
    }

    /// <summary>
    /// The scheduled tools of a headless run: a save state written and loaded back, debug mode toggled on and the F10
    /// capture taken, and a GPU dump of a frame range that replay-gpu then plays in another process: its frames show
    /// what the guest drew (the pressed colour while the button was held).
    /// </summary>
    [SoftwareVulkanFact]
    public void ScheduledSaveStateCaptureAndGpuDumpThatReplays()
    {
        string dump = Path.Combine(_root, "synthetic.vsgd");
        DesktopProcess.Result r = DesktopProcess.Run(_root, ["run", _app, "--headless", "--vulkan", "--no-vsync", "--frames=70",
            "--input=cross@f20:20", "--save-state=f25", "--load-state-at=f45", "--debug-mode-at=f2", "--diag-at=f30",
            $"--gpu-dump-at=10:40:{dump}"], timeoutMs: 180_000);
        Assert.True(r.ExitCode == 0, Tail(r.Output));

        // the save state: written for the title with a picture of the pressed frame, then loaded
        string state = Path.Combine(_root, "savestates", "VSTS00001", "test_f25.vss");
        Assert.True(File.Exists(state), Tail(r.Output));
        SaveStateHeader header = VitaSharp.Core.SaveState.SaveStateFile.ReadHeader(state);
        Assert.Equal("VSTS00001", header.TitleId);
        Assert.NotEmpty(header.Thumbnail);
        // the save at frame 25 or a little later, the load at frame 45 or later (the step waits for the save to finish)
        long[] steps = System.Text.RegularExpressions.Regex.Matches(r.Output, @"Save state step at guest frame (\d+)").Select(m => long.Parse(m.Groups[1].Value)).ToArray();
        Assert.Equal(2, steps.Length);
        Assert.InRange(steps[0], 25, 44);
        Assert.True(steps[1] >= 45, $"the load ran at frame {steps[1]}");
        Assert.True(FramesOf(r.Output) >= 70);

        // the capture of debug mode: its folder with the info, the log and the screenshot of the frame
        string diag = Directory.GetDirectories(Path.Combine(_root, "diagnostics"), "VSTS00001_*").Single();
        Assert.True(File.Exists(Path.Combine(diag, "info.txt")));
        Assert.True(File.Exists(Path.Combine(diag, "screenshot.png")), string.Join(", ", Directory.GetFileSystemEntries(diag).Select(Path.GetFileName)));
        Assert.Equal((SyntheticTitle.Width, SyntheticTitle.Height), (DesktopProcess.Probe(Path.Combine(diag, "screenshot.png")).Width, DesktopProcess.Probe(Path.Combine(diag, "screenshot.png")).Height));

        // the dump: 31 frames, replayed without the game in a process of its own
        Assert.True(new FileInfo(dump).Length > 0);
        string replay = Path.Combine(_root, "replay");
        DesktopProcess.Result p = DesktopProcess.Run(_root, ["replay-gpu", dump, $"--out={replay}"], timeoutMs: 180_000);
        Assert.True(p.ExitCode == 0, Tail(p.Output));
        (byte R, byte G, byte B)[] frames = Directory.GetFiles(replay, "frame_*.png").Order().Select(f => DesktopProcess.Probe(f).Center).ToArray();
        Assert.InRange(frames.Length, 25, 35);
        Assert.Contains(SyntheticTitle.Idle, frames);
        Assert.Contains(SyntheticTitle.Pressed, frames);
        Assert.All(frames, c => Assert.True(c == SyntheticTitle.Idle || c == SyntheticTitle.Pressed, $"unexpected colour {c}"));
        DesktopProcess.Result info = DesktopProcess.Run(_root, ["replay-gpu", dump, "--info"]);
        Assert.Contains("VSTS00001", info.Output);
    }
}
