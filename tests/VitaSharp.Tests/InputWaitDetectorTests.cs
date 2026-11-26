using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The "waiting for input" detector: a still picture (identical, blinking between a few pictures, or a small spinner)
/// with input reads every frame and no loading is WAITING FOR INPUT (the line names the reads per source); with file
/// I/O, module loads or compilation it is LOADING; with neither, BUSY; a changing picture is ACTIVE. The synthetic
/// title reads the real HLE functions (sceTouchPeek, sceCtrlPeekBufferPositive, sceIoOpen/sceIoRead) and the counters
/// they feed. The guest time is passed explicitly: nothing depends on the host clock.
/// </summary>
[Collection("guest")]
public sealed class InputWaitDetectorTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int W = 64, H = 36;
    private const long FrameUs = 16_667;

    private sealed class ScriptedInput : IInputBackend
    {
        public ControllerState GetController(int port) => ControllerState.Neutral;
        public IReadOnlyList<TouchPoint> GetTouches(bool back) => [];
    }

    private HleHarness? _harness;

    public void Dispose() => _harness?.Dispose();

    /// <summary>A picture of grey <paramref name="level"/> with a bright box at (<paramref name="boxX"/>, 8) of
    /// <paramref name="boxSize"/> pixels.</summary>
    private static byte[] Picture(byte level, int boxX = -1, int boxSize = 4)
    {
        byte[] px = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            px[i * 4] = level;
            px[i * 4 + 1] = level;
            px[i * 4 + 2] = level;
            px[i * 4 + 3] = 255;
        }

        if (boxX >= 0)
        {
            for (int y = 8; y < 8 + boxSize; y++)
            {
                for (int x = boxX; x < boxX + boxSize && x < W; x++)
                {
                    int p = (y * W + x) * 4;
                    px[p] = 255;
                    px[p + 1] = 255;
                    px[p + 2] = 255;
                }
            }
        }

        return px;
    }

    /// <summary>Feeds <paramref name="frames"/> frames; <paramref name="step"/> gives frame i's picture and adds to the
    /// counters what the game did during it.</summary>
    private static InputWaitDetector Run(int frames, Func<int, ActivityCounters, (byte[] Picture, ActivityCounters Counters)> step, out List<ScreenActivity> changes)
    {
        var detector = new InputWaitDetector(60);
        var counters = default(ActivityCounters);
        changes = [];
        for (int i = 0; i < frames; i++)
        {
            (byte[] picture, counters) = step(i, counters);
            if (detector.Feed(i, 1000 + i, i * FrameUs, picture, W, H, counters))
            {
                changes.Add(detector.State);
            }
        }

        return detector;
    }

    [Fact]
    public void AStillPictureWithTouchReadsEveryFrameIsWaitingForInput()
    {
        byte[] still = Picture(40, boxX: 20);
        InputWaitDetector d = Run(200, (_, c) => (still, c with { FrontTouchReads = c.FrontTouchReads + 1, CtrlReads = c.CtrlReads + 1 }), out List<ScreenActivity> changes);
        Assert.Equal(ScreenActivity.WaitingForInput, d.State);
        Assert.Equal([ScreenActivity.WaitingForInput], changes);
        string line = d.Line("PCSX00000 'Synthetic'");
        Assert.StartsWith("WAITING FOR INPUT: PCSX00000 'Synthetic' — reads front touch 60/s, ctrl buttons 60/s, rear touch 0/s", line);
        Assert.EndsWith("screen static since frame 1000", line);
    }

    [Fact]
    public void BlinkingTextAndASmallSpinnerStayStill()
    {
        // "Press X" blinking every 15 frames (two pictures), then a spinner box moving over a few cells
        byte[] on = Picture(40, boxX: 20), off = Picture(40);
        InputWaitDetector blink = Run(200, (i, c) => ((i / 15) % 2 == 0 ? on : off, c with { CtrlReads = c.CtrlReads + 1 }), out _);
        Assert.Equal(ScreenActivity.WaitingForInput, blink.State);
        byte[][] spinner = [Picture(40, 2), Picture(40, 6), Picture(40, 10), Picture(40, 14)];
        InputWaitDetector spin = Run(200, (i, c) => (spinner[i % 4], c with { RearTouchReads = c.RearTouchReads + 1 }), out _);
        Assert.Equal(ScreenActivity.WaitingForInput, spin.State);
        Assert.Contains("rear touch 60/s", spin.Line("t"));
    }

    [Fact]
    public void FileReadsOrCompilationOnAStillPictureAreLoading()
    {
        byte[] still = Picture(40, boxX: 20);
        // 64 KB per frame = 3.75 MB/s while the game also polls the pad
        InputWaitDetector io = Run(200, (_, c) => (still, c with { FileBytes = c.FileBytes + 65536, CtrlReads = c.CtrlReads + 1 }), out _);
        Assert.Equal(ScreenActivity.Loading, io.State);
        Assert.StartsWith("LOADING: t — file reads 3840 KB/s", io.Line("t"));
        InputWaitDetector shaders = Run(200, (i, c) => (still, c with { ShaderCompiles = c.ShaderCompiles + (i % 10 == 0 ? 1 : 0), CtrlReads = c.CtrlReads + 1 }), out _);
        Assert.Equal(ScreenActivity.Loading, shaders.State);
        InputWaitDetector modules = Run(200, (i, c) => (still, c with { ModuleLoads = c.ModuleLoads + (i % 30 == 0 ? 1 : 0) }), out _);
        Assert.Equal(ScreenActivity.Loading, modules.State);
        // streamed music (~40 KB/s) is not loading
        InputWaitDetector music = Run(200, (i, c) => (still, c with { FileBytes = c.FileBytes + 700, CtrlReads = c.CtrlReads + 1 }), out _);
        Assert.Equal(ScreenActivity.WaitingForInput, music.State);
    }

    [Fact]
    public void AStillPictureWithoutReadsOrLoadingIsBusyAndAMovingOneIsActive()
    {
        byte[] still = Picture(40, boxX: 20);
        InputWaitDetector busy = Run(200, (_, c) => (still, c), out _);
        Assert.Equal(ScreenActivity.Busy, busy.State);
        Assert.StartsWith("BUSY: t — no input read", busy.Line("t"));
        InputWaitDetector moving = Run(200, (i, c) => (Picture((byte)(i * 7 % 256)), c with { CtrlReads = c.CtrlReads + 1 }), out _);
        Assert.Equal(ScreenActivity.Active, moving.State);
    }

    [Fact]
    public void AChangeIsReportedOnlyOnceItHolds()
    {
        // still for 150 frames, then one different frame every 40 frames: no flapping back and forth
        byte[] still = Picture(40, boxX: 20), flash = Picture(200);
        InputWaitDetector d = Run(400, (i, c) => (i >= 150 && i % 40 == 0 ? flash : still, c with { CtrlReads = c.CtrlReads + 1 }), out List<ScreenActivity> changes);
        Assert.Equal(ScreenActivity.WaitingForInput, changes[0]);
        Assert.True(changes.Count <= 3, string.Join(",", changes));
        Assert.Equal(-1, new InputWaitDetector().StillSinceFrame);
        Assert.Equal(ScreenActivity.Unknown, new InputWaitDetector().State);
        Assert.NotEqual(ScreenActivity.Unknown, d.State);
    }

    /// <summary>The synthetic title: every frame it peeks the front panel and the pad through the real HLE functions and
    /// presents the same picture → WAITING FOR INPUT with the front touch and ctrl rates; then it reads a file every frame
    /// → LOADING. The counters come from the modules' hooks (<see cref="InputWaitMonitor.Counters"/>).</summary>
    [Fact]
    public void ASyntheticTitleThatPeeksTheTouchPanelWaitsAndOneThatReadsFilesLoads()
    {
        _harness = new HleHarness(fixture, "inputwait");
        _harness.Emu.Host.Input = new ScriptedInput();
        Touch touch = _harness.Module<Touch>();
        Ctrl ctrl = _harness.Module<Ctrl>();
        IoFileMgr io = _harness.Module<IoFileMgr>();
        string host = _harness.Emu.Vfs.Resolve("ux0:inputwait.bin", true)!;
        File.WriteAllBytes(host, new byte[1 << 20]);
        uint touchData = _harness.Alloc(0x90 * 4);
        uint pad = _harness.Alloc(0x40);
        uint buffer = _harness.Alloc(0x10000);
        Assert.Equal(0, Call(touch, "sceTouchSetSamplingState", 0, 1u));
        byte[] still = Picture(40, boxX: 20);
        InputWaitMonitor.CaptureStarted();
        try
        {
            Assert.True(InputWaitMonitor.Active);
            var detector = new InputWaitDetector(60);
            for (int i = 0; i < 120; i++)
            {
                Assert.True(Call(touch, "sceTouchPeek", 0, touchData, 1u) >= 0);
                Assert.Equal(1, Call(ctrl, "sceCtrlPeekBufferPositive", 0, pad, 1u));
                // the JIT's blocks are process-wide (other tests compile): left out of this title's counters
                detector.Feed(i, i, i * FrameUs, still, W, H, InputWaitMonitor.Counters(0) with { CodeBlocks = 0 });
            }

            Assert.Equal(ScreenActivity.WaitingForInput, detector.State);
            string line = detector.Line("PCSX00000");
            Assert.Contains("reads front touch 60/s, ctrl buttons 60/s", line);

            int fd = Call(io, "sceIoOpen", "ux0:inputwait.bin", 1, 0);
            Assert.True(fd > 0, $"0x{fd:X8}");
            for (int i = 120; i < 240; i++)
            {
                Assert.Equal(0x10000, Call(io, "sceIoRead", fd, buffer, 0x10000u));
                if (i % 15 == 14)
                {
                    Assert.Equal(0, Call(io, "sceIoLseek32", fd, 0, 0));
                }

                Assert.True(Call(touch, "sceTouchPeek", 0, touchData, 1u) >= 0);
                detector.Feed(i, i, i * FrameUs, still, W, H, InputWaitMonitor.Counters(0) with { CodeBlocks = 0 });
            }

            Assert.Equal(ScreenActivity.Loading, detector.State);
            // reported once it held a quarter of the window: the reads cover part of it by then
            Assert.Matches(@"^LOADING: PCSX00000 — file reads \d{3,} KB/s", detector.Line("PCSX00000"));
            Assert.Equal(0, Call(io, "sceIoClose", fd));
        }
        finally
        {
            InputWaitMonitor.CaptureStopped();
        }
    }

    /// <summary>The F8 report's "Screen state": the context.txt "screen:" line at the stop and the last [input-wait]
    /// changes of log.txt.</summary>
    [Fact]
    public void TheCaptureReportHasTheScreenState()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-inputwait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "context.txt"), ["[start of the recording]", "screen: ACTIVE: A — the picture changes", "",
                "[stop of the recording]", "where: game A in front", "screen: WAITING FOR INPUT: A — reads front touch 60/s, ctrl buttons 0/s, rear touch 0/s; screen static since frame 77"]);
            File.WriteAllLines(Path.Combine(dir, "log.txt"), ["12:00:00.000 [I] [input-wait] LOADING: A — file reads 900 KB/s", "other line",
                "12:00:01.000 [I] [input-wait] WAITING FOR INPUT: A — reads front touch 60/s"]);
            List<string> section = Core.Capture.CaptureAnalyzer.ScreenStateSection(dir, Core.Capture.CaptureAnalyzer.ContextSection(dir, stop: true));
            Assert.Equal("Screen state (waiting for input / loading / busy):", section[0]);
            Assert.Equal("  at the stop: WAITING FOR INPUT: A — reads front touch 60/s, ctrl buttons 0/s, rear touch 0/s; screen static since frame 77", section[1]);
            Assert.Equal(["    LOADING: A — file reads 900 KB/s", "    WAITING FOR INPUT: A — reads front touch 60/s"], section.Skip(3).Take(2));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The process-wide monitor: its frames make the state a navigation script waits for (waitinput) and the
    /// line it logs; <see cref="InputWaitMonitor.WaitFor"/> returns at once when the state is already there.</summary>
    [Fact]
    public void TheMonitorReportsTheStateScriptsWaitFor()
    {
        InputWaitMonitor.Reset(16);
        InputWaitMonitor.Retain();
        try
        {
            byte[] still = Picture(40, boxX: 20);
            var counters = default(ActivityCounters);
            for (int i = 0; i < 40; i++)
            {
                counters = counters with { FrontTouchReads = counters.FrontTouchReads + 1 };
                InputWaitMonitor.OnFrame(i, i, still, W, H, counters, i * FrameUs);
            }

            Assert.Equal(ScreenActivity.WaitingForInput, InputWaitMonitor.State);
            Assert.True(InputWaitMonitor.WaitFor(ScreenActivity.WaitingForInput, TimeSpan.Zero));
            Assert.StartsWith("WAITING FOR INPUT: ", InputWaitMonitor.LastLine);
            Assert.Contains("reads front touch 60/s", InputWaitMonitor.Describe());
        }
        finally
        {
            InputWaitMonitor.Release();
            InputWaitMonitor.Reset();
        }
    }

    /// <summary>Off (no debug mode, no capture, no VITASHARP_INPUT_WAIT) the hooks count nothing.</summary>
    [Fact]
    public void OffTheHooksCountNothing()
    {
        if (InputWaitMonitor.Active)
        {
            return; // debug mode or VITASHARP_INPUT_WAIT=1 in this process: the contract below does not apply
        }

        _harness = new HleHarness(fixture, "inputwaitoff");
        _harness.Emu.Host.Input = new ScriptedInput();
        Ctrl ctrl = _harness.Module<Ctrl>();
        uint pad = _harness.Alloc(0x40);
        long before = InputWaitMonitor.Counters(0).CtrlReads;
        Assert.Equal(1, Call(ctrl, "sceCtrlPeekBufferPositive", 0, pad, 1u));
        Assert.Equal(before, InputWaitMonitor.Counters(0).CtrlReads);
        Assert.StartsWith("UNKNOWN: the detector is off", InputWaitMonitor.Describe());
    }
}
