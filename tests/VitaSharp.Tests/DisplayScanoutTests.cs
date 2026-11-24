using VitaSharp.Core.Capture;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The display's scanout of a CPU framebuffer. On the Vita, sceDisplaySetFrameBuf hands the display engine the
/// framebuffer's address and the engine reads that memory at every vblank: a program that sets its framebuffer once and
/// then draws into it with the CPU (an installer writing its text, then waiting for a button) has what it drew on the
/// panel at the next vblank. The emulator showed a frame only at a flip, so such a program stayed black (Adrenaline's
/// first screen: one present, then the main thread in sceCtrlReadBufferPositive, 0 fps, a recording with no frames).
/// Guest time is the test's (<see cref="ControlledGuestClock"/>): which vblank the worker looks at is never decided by
/// how fast the host runs.
/// </summary>
[Collection("guest")]
public sealed class DisplayScanoutTests(GuestMemoryFixture fx)
{
    private const uint W = 64, H = 64;
    /// <summary>A little more than one vblank period (16 683 us).</summary>
    private const long VblankUs = 16_700;
    private const uint Red = 0xFF0000FF, Blue = 0xFFFF0000;

    private static DisplayFrame Frame(uint data) => new(data, W, W, H, DisplayPixelFormat.A8B8G8R8);

    private static void Fill(uint data, uint abgr)
    {
        for (uint i = 0; i < W * H; i++)
        {
            GuestMemory.W32(data + i * 4, abgr);
        }
    }

    /// <summary>The shown picture's centre, as the frame sink reads it (R, G, B).</summary>
    private static (byte R, byte G, byte B) Centre(byte[] rgba, int width, int height)
    {
        int i = ((height / 2) * width + width / 2) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    private sealed class CountingTap : IPresentTap
    {
        public int Frames;

        public void OnPresent(in PresentInfo info, ReadOnlySpan<byte> rgba, int w, int h) => Interlocked.Increment(ref Frames);
    }

    // ------------------------------------------------------------------ the watch itself (no GPU)

    [Fact]
    public void TheScanoutIsDueOnlyAtANewVblankAfterTheMemoryChanged()
    {
        using var h = new HleHarness(fx, "scanwatch");
        uint data = h.Alloc(W * H * 4);
        Fill(data, Red);
        var scanout = new DisplayScanout();
        Assert.False(scanout.Due(1)); // nothing shown yet: nothing watched

        scanout.Shown(Frame(data), 10);
        Assert.True(scanout.Armed);
        Assert.False(scanout.Due(11)); // a new vblank, the memory as it was shown: the same picture

        GuestMemory.W32(data + 4 * (W * 10 + 10), Blue);
        Assert.False(scanout.Due(11)); // the vblank already looked at: the next one shows it
        Assert.True(scanout.Due(12));
        scanout.Shown(Frame(data), 12);
        Assert.False(scanout.Due(13)); // shown: unchanged since

        // the last write of a line, the last byte the display reads (pitch x height)
        GuestMemory.W32(data + 4 * (W * H - 1), Blue);
        Assert.True(scanout.Due(14));

        scanout.Disarm(); // a frame the GPU rendered was shown: nothing is watched
        Assert.False(scanout.Armed);
        Assert.False(scanout.Due(15));
    }

    /// <summary>A recording in which nothing reached the screen says so in its report ("frames: 0 presented"), instead
    /// of leaving the frames line out as if it had been lost.</summary>
    [Fact]
    public void TheReportOfARecordingWithNoFrameSaysZeroFramesPresented()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-scanreport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "session.txt"), "title-id: TEST00000\ntitle: Test\nstart-ns: 0\n");
            using (FileStream frames = File.Create(Path.Combine(dir, "frames.vsfr")))
            {
                FramesFile.WriteHeader(frames);
            }

            string report = CaptureAnalyzer.Analyze(dir, quiet: true);
            Assert.Contains("frames: 0 presented", report);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------ the renderer (software Vulkan)

    /// <summary>The CPU framebuffer flipped once and drawn into after the flip: the next vblank shows the drawing (a
    /// frame for the fps counter and for the session capture's recording), an unchanged vblank shows nothing new.</summary>
    [SoftwareVulkanFact]
    public void ACpuFramebufferFlippedOnceShowsWhatTheCpuDrawsIntoItAfterTheFlip()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "scancpu");
        uint data = h.Alloc(W * H * 4);
        Fill(data, Red);
        var shown = new List<(byte R, byte G, byte B)>();
        var tap = new CountingTap();
        var options = new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false };
        options.FrameSink = (_, rgba, width, height) => shown.Add(Centre(rgba, width, height));
        options.PresentTap = tap;
        using var renderer = new VulkanRenderer(options);
        using var clock = ControlledGuestClock.Stop();
        long scannedBefore = Interlocked.Read(ref DisplayScanout.Frames);

        renderer.Execute(new PresentCommand { Frame = Frame(data), Flip = 0 }); // the program's one flip
        renderer.Execute(new FinishCommand());
        Assert.True(renderer.CpuScanoutArmed);
        Assert.Equal(1, renderer.PresentCount);

        // the CPU clears the screen blue after the flip; the display reaches the next vblank
        Fill(data, Blue);
        renderer.Idle();
        Assert.Equal(1, renderer.PresentCount); // the same vblank as the flip: the panel shows it at the next one
        clock.Advance(VblankUs);
        renderer.Idle();
        renderer.Execute(new FinishCommand());
        Assert.Equal(2, renderer.PresentCount);
        Assert.Equal(1, Interlocked.Read(ref DisplayScanout.Frames) - scannedBefore);

        // vblanks with the memory unchanged: no new frame
        clock.Advance(VblankUs);
        renderer.Idle();
        clock.Advance(VblankUs);
        renderer.Idle();
        renderer.Execute(new FinishCommand());
        Assert.Equal(2, renderer.PresentCount);

        Assert.Equal(2, shown.Count);
        Assert.True(shown[0].R > 200 && shown[0].B < 50, $"the flip shows red, got {shown[0]}");
        Assert.True(shown[1].B > 200 && shown[1].R < 50, $"the scanout shows what the CPU drew after the flip (blue), got {shown[1]}");
        Assert.Equal(2, Volatile.Read(ref tap.Frames)); // the recording has the scanned frame
    }

    /// <summary>A framebuffer the GPU renders is never watched: memory written behind its surface is not the picture,
    /// and the scanout costs a GPU game nothing.</summary>
    [SoftwareVulkanFact]
    public void AFramebufferTheGpuRendersIsNotScannedFromMemory()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "scangpu");
        uint data = h.Alloc(W * H * 4);
        using var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false });
        using var clock = ControlledGuestClock.Stop();

        renderer.Execute(new BeginSceneCommand { Width = W, Height = H, Color = new ColorSurfaceInfo(data, W, H, W, 0, 0, false, 0), Pid = 1 });
        renderer.Execute(new EndSceneCommand { Pid = 1 });
        renderer.Execute(new PresentCommand { Frame = Frame(data), Flip = 0 });
        renderer.Execute(new FinishCommand());
        Assert.False(renderer.CpuScanoutArmed);
        Assert.Equal(50, renderer.IdleWakeMs); // the idle worker sleeps as before

        Fill(data, Blue);
        clock.Advance(VblankUs);
        renderer.Idle();
        renderer.Execute(new FinishCommand());
        Assert.Equal(1, renderer.PresentCount);
    }

    /// <summary>The emulator's overlays change while the guest shows nothing new (the recording stops: its "REC" badge
    /// goes): the last frame is shown again with them, two vblanks after the last frame shown, not before.</summary>
    [SoftwareVulkanFact]
    public void AnOverlayThatGoesWhileNoFrameComesLeavesTheScreen()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "scanoverlay");
        uint data = h.Alloc(W * H * 4);
        Fill(data, Red);
        using var renderer = new VulkanRenderer(new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false });
        using var clock = ControlledGuestClock.Stop();
        byte[] badge = new byte[16 * 8 * 4];
        Array.Fill(badge, (byte)0xFF);

        renderer.SetStatusOverlay(badge, 16, 8); // recording: "REC"
        renderer.Execute(new PresentCommand { Frame = Frame(data), Flip = 0 });
        renderer.Execute(new FinishCommand());
        clock.Advance(3 * VblankUs);
        renderer.Idle();
        Assert.Equal(0, renderer.IdleRepresents); // the frame went out with the badge: nothing to show again

        renderer.Execute(new PresentCommand { Frame = Frame(data), Flip = 1 }); // a last flip, then none
        renderer.Execute(new FinishCommand());
        renderer.SetStatusOverlay(null, 0, 0); // the recording stops
        renderer.Idle();
        clock.Advance(VblankUs);
        renderer.Idle();
        Assert.Equal(0, renderer.IdleRepresents); // within two vblanks of the last frame shown: a flip may still come
        clock.Advance(VblankUs);
        renderer.Idle();
        Assert.Equal(1, renderer.IdleRepresents); // shown again without the badge
        clock.Advance(3 * VblankUs);
        renderer.Idle();
        Assert.Equal(1, renderer.IdleRepresents); // once
        renderer.Execute(new FinishCommand());
        Assert.Equal(2, renderer.PresentCount); // not a frame of the guest's
    }

    /// <summary>The whole path: a guest thread sets a CPU framebuffer once (sceDisplaySetFrameBuf) and writes into it
    /// afterwards, the renderer on the GPU queue's worker, which looks at the next vblank on its own while idle.</summary>
    [SoftwareVulkanFact]
    public void AProgramThatSetsItsFramebufferOnceAndDrawsAfterwardsIsShownByTheIdleWorker()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var h = new HleHarness(fx, "scanhle");
        uint data = h.Alloc(W * H * 4);
        uint param = h.Alloc(0x20);
        GuestMemory.W32(param, 0x18);
        GuestMemory.W32(param + 4, data);
        GuestMemory.W32(param + 8, W);
        GuestMemory.W32(param + 12, 0);
        GuestMemory.W32(param + 16, W);
        GuestMemory.W32(param + 20, H);
        Fill(data, Red);
        var shown = new List<(byte R, byte G, byte B)>();
        var options = new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false };
        options.FrameSink = (_, rgba, width, height) =>
        {
            lock (shown)
            {
                shown.Add(Centre(rgba, width, height));
            }
        };
        var renderer = new VulkanRenderer(options);
        h.Emu.Gpu.Backend = renderer;
        try
        {
            using var clock = ControlledGuestClock.Stop();
            Display display = h.Module<Display>();
            long flipsBefore = Display.FramesPresented;
            GuestRun guest = h.Start(_ =>
            {
                Assert.Equal(0, Call(display, "sceDisplaySetFrameBuf", param, 1u));
            });
            h.Join(guest);
            h.Emu.Gpu.WaitIdle();
            Until(() => renderer.CpuScanoutArmed, "the flip's present");

            Fill(data, Blue); // drawn with the CPU after the flip, no flip follows
            clock.Advance(VblankUs);
            Until(() => renderer.PresentCount >= 2, "the scanout of the next vblank");
            h.Emu.Gpu.Submit(new FinishCommand());
            h.Emu.Gpu.WaitIdle();

            Assert.Equal(1, Display.FramesPresented - flipsBefore); // one flip of the guest's: its pacing is its own
            lock (shown)
            {
                Assert.True(shown.Count >= 2, $"{shown.Count} frames shown");
                Assert.True(shown[^1].B > 200 && shown[^1].R < 50, $"the screen shows what the CPU drew after the flip (blue), got {shown[^1]}");
            }
        }
        finally
        {
            h.Emu.Gpu.WaitIdle();
            h.Emu.Gpu.Backend = null;
            renderer.Dispose();
        }
    }
}
