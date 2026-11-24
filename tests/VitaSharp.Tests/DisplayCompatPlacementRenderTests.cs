using VitaSharp.Core.Capture;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Memory;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Tests;

/// <summary>
/// The compat framebuffer on the screen (<see cref="PresentCommand.Placement"/>): FW 3.74 display.skprx programs the
/// head's plane 0 IFTU unit with it scaled into a rectangle of the 960x544 screen, black around it, and the shell's
/// overlay (plane 1) is merged over plane 0. Covered: the rectangle over black, the scaling, the 16-bit format, the
/// overlay over the placed picture, the scanout of a CPU framebuffer changing between vblanks placed the same way, a
/// present without a placement right after one with it, and the clipping of a rectangle partly off the screen.
/// </summary>
[Collection("guest")]
public sealed class DisplayCompatPlacementRenderTests(GuestMemoryFixture fixture)
{
    private const uint W = 120, H = 68;
    private const long VblankUs = 16_700;
    private const uint Red = 0xFF0000FF, Green = 0xFF00FF00, Blue = 0xFFFF0000;

    /// <summary>A presented picture as the present tap gets it (the session capture's: at most 480x272, so a 960x544
    /// screen at half size).</summary>
    private sealed record Shot(byte[] Rgba, int Width, int Height)
    {
        /// <summary>The pixel at (x, y) of a 960x544 screen.</summary>
        public (byte R, byte G, byte B) AtScreen(int x, int y) => At(x * Width / 960, y * Height / 544);

        public (byte R, byte G, byte B) At(int x, int y)
        {
            int i = (y * Width + x) * 4;
            return (Rgba[i], Rgba[i + 1], Rgba[i + 2]);
        }
    }

    private sealed class ShotTap(List<Shot> shots) : IPresentTap
    {
        public void OnPresent(in PresentInfo info, ReadOnlySpan<byte> rgba, int w, int h)
        {
            lock (shots)
            {
                shots.Add(new Shot(rgba.ToArray(), w, h));
            }
        }
    }

    private static void Fill32(uint data, uint pitch, uint height, uint abgr)
    {
        for (uint i = 0; i < pitch * height; i++)
        {
            GuestMemory.W32(data + i * 4, abgr);
        }
    }

    private static VulkanRenderer NewRenderer(List<Shot> shots)
    {
        var options = new VulkanRendererOptions { VSync = false, AsyncShaders = false, AsyncPipelines = false };
        options.PresentTap = new ShotTap(shots);
        return new VulkanRenderer(options);
    }

    private static void AssertColor((byte R, byte G, byte B) pixel, bool red, bool green, bool blue, string what)
    {
        bool ok = (red ? pixel.R > 200 : pixel.R < 50) && (green ? pixel.G > 200 : pixel.G < 50) && (blue ? pixel.B > 200 : pixel.B < 50);
        Assert.True(ok, $"{what}: got {pixel}");
    }

    [Fact]
    public void TheClippedRectangleMapsTheSourceSpanItShows()
    {
        var placement = new DisplayPlacement(-480, 0, 1920, 544, Bilinear: true);
        Assert.True(placement.TryClip(480, 272, out (int X0, int Y0, int X1, int Y1) source, out (int X0, int Y0, int X1, int Y1) destination));
        Assert.Equal((0, 0, 960, 544), destination);
        Assert.Equal((120, 0, 360, 272), source); // the middle half of the picture, the rows whole

        Assert.False(new DisplayPlacement(960, 0, 100, 100, false).TryClip(480, 272, out _, out _)); // right of the screen
        Assert.False(new DisplayPlacement(0, 0, 0, 272, false).TryClip(480, 272, out _, out _)); // empty
        Assert.True(new DisplayPlacement(240, 136, 480, 272, false).TryClip(480, 272, out source, out destination));
        Assert.Equal((240, 136, 720, 408), destination);
        Assert.Equal((0, 0, 480, 272), source);
    }

    [Fact]
    public void A1B5G5R5PixelsWidenToOpaqueRgba8()
    {
        // red, green, blue, white with alpha 0, a pitch wider than the row
        byte[] source = [0x1F, 0x00, 0xE0, 0x03, 0x00, 0x7C, 0xFF, 0x7F, 0xAA, 0xAA];
        byte[] rgba = new byte[16];
        VulkanRenderer.A1B5G5R5ToRgba8(source, rgba, 4, 1, 5);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 }, rgba);
    }

    /// <summary>A CPU framebuffer placed in the middle of the screen: scaled to its rectangle, black around it.</summary>
    [SoftwareVulkanFact]
    public void APlacedFramebufferIsScaledIntoItsRectangleOverBlack()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var harness = new HleHarness(fixture, "compatrect");
        uint data = harness.Alloc(W * H * 4);
        Fill32(data, W, H, Red);
        var shots = new List<Shot>();
        using VulkanRenderer renderer = NewRenderer(shots);

        renderer.Execute(new PresentCommand
        {
            Frame = new DisplayFrame(data, W, W, H, DisplayPixelFormat.A8B8G8R8),
            Placement = new DisplayPlacement(240, 136, 480, 272, Bilinear: true),
            Flip = 0,
        });
        renderer.Execute(new FinishCommand());

        Shot shot = Assert.Single(shots);
        Assert.Equal((480, 272), (shot.Width, shot.Height)); // the 960x544 screen, at the tap's half size
        AssertColor(shot.AtScreen(480, 272), true, false, false, "the rectangle's centre");
        AssertColor(shot.AtScreen(248, 144), true, false, false, "inside the rectangle's corner (scaled 4x)");
        AssertColor(shot.AtScreen(712, 400), true, false, false, "inside the opposite corner");
        AssertColor(shot.AtScreen(100, 100), false, false, false, "outside the rectangle");
        AssertColor(shot.AtScreen(230, 272), false, false, false, "left of the rectangle");
        AssertColor(shot.AtScreen(480, 418), false, false, false, "below the rectangle");
    }

    /// <summary>The 16-bit compat format, drawn with the CPU, placed full screen.</summary>
    [SoftwareVulkanFact]
    public void TheSixteenBitFormatIsShownPlaced()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var harness = new HleHarness(fixture, "compat16");
        const uint Pitch16 = 128;
        uint data = harness.Alloc(Pitch16 * H * 2);
        for (uint i = 0; i < Pitch16 * H; i++)
        {
            GuestMemory.Write(data + i * 2, (ushort)0x03E0); // green
        }

        var shots = new List<Shot>();
        using VulkanRenderer renderer = NewRenderer(shots);
        renderer.Execute(new PresentCommand
        {
            Frame = new DisplayFrame(data, Pitch16, W, H, DisplayPixelFormat.A1B5G5R5),
            Placement = new DisplayPlacement(0, 0, 960, 544, Bilinear: false),
            Flip = 0,
        });
        renderer.Execute(new FinishCommand());

        Shot shot = Assert.Single(shots);
        AssertColor(shot.AtScreen(480, 272), false, true, false, "the centre");
        AssertColor(shot.AtScreen(4, 4), false, true, false, "the top left");
        AssertColor(shot.AtScreen(954, 538), false, true, false, "the bottom right");
    }

    /// <summary>The shell's overlay (plane 1) is blended over the placed picture (plane 0), where the picture and where
    /// the black around it is.</summary>
    [SoftwareVulkanFact]
    public void TheShellOverlayIsBlendedOverThePlacedPicture()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var harness = new HleHarness(fixture, "compatoverlay");
        uint data = harness.Alloc(W * H * 4);
        Fill32(data, W, H, Red);
        const uint ScreenW = 960, ScreenH = 544;
        uint overlayData = harness.Alloc(ScreenW * ScreenH * 4);
        // the top half opaque blue, the bottom half transparent
        for (uint y = 0; y < ScreenH; y++)
        {
            for (uint x = 0; x < ScreenW; x++)
            {
                GuestMemory.W32(overlayData + (y * ScreenW + x) * 4, y < ScreenH / 2 ? Blue : 0u);
            }
        }

        var shots = new List<Shot>();
        using VulkanRenderer renderer = NewRenderer(shots);
        // the shell's frame is a surface the GPU rendered (what the shell overlay composes)
        renderer.Execute(new BeginSceneCommand { Width = ScreenW, Height = ScreenH, Color = new ColorSurfaceInfo(overlayData, ScreenW, ScreenH, ScreenW, 0, 0, false, 0), Pid = 1 });
        renderer.Execute(new EndSceneCommand { Pid = 1 });
        renderer.Execute(new PresentCommand
        {
            Frame = new DisplayFrame(data, W, W, H, DisplayPixelFormat.A8B8G8R8),
            Placement = new DisplayPlacement(240, 136, 480, 272, Bilinear: true),
            Overlay = new DisplayFrame(overlayData, ScreenW, ScreenW, ScreenH, DisplayPixelFormat.A8B8G8R8),
            Flip = 0,
        });
        renderer.Execute(new FinishCommand());

        Shot shot = Assert.Single(shots);
        AssertColor(shot.AtScreen(480, 200), false, false, true, "the overlay's opaque half over the picture");
        AssertColor(shot.AtScreen(100, 100), false, false, true, "the overlay's opaque half over the black around it");
        AssertColor(shot.AtScreen(480, 350), true, false, false, "the picture under the overlay's transparent half");
        AssertColor(shot.AtScreen(100, 500), false, false, false, "black under the overlay's transparent half");
    }

    /// <summary>A placed CPU framebuffer the CPU draws into between vblanks is scanned out placed the same way; a
    /// present without a placement then fills the screen again.</summary>
    [SoftwareVulkanFact]
    public void TheScanoutOfAPlacedFramebufferKeepsItsPlacementAndANullPlacementFillsTheScreen()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var harness = new HleHarness(fixture, "compatscan");
        uint data = harness.Alloc(W * H * 4);
        Fill32(data, W, H, Red);
        var shots = new List<Shot>();
        using VulkanRenderer renderer = NewRenderer(shots);
        using var clock = ControlledGuestClock.Stop();
        var frame = new DisplayFrame(data, W, W, H, DisplayPixelFormat.A8B8G8R8);

        renderer.Execute(new PresentCommand { Frame = frame, Placement = new DisplayPlacement(240, 136, 480, 272, true), Flip = 0 });
        renderer.Execute(new FinishCommand());
        Assert.True(renderer.CpuScanoutArmed);

        Fill32(data, W, H, Green); // the CPU draws after the set, no new set follows
        clock.Advance(VblankUs);
        renderer.Idle();
        renderer.Execute(new FinishCommand());
        Assert.Equal(2, renderer.PresentCount);

        renderer.Execute(new PresentCommand { Frame = frame, Flip = 1 }); // plane 0 shown whole again
        renderer.Execute(new FinishCommand());

        Assert.Equal(3, shots.Count);
        AssertColor(shots[1].AtScreen(480, 272), false, true, false, "the scanned frame's rectangle");
        AssertColor(shots[1].AtScreen(100, 100), false, false, false, "the scanned frame around the rectangle");
        Assert.Equal((480, 272), (shots[1].Width, shots[1].Height));
        Assert.Equal(((int)W, (int)H), (shots[2].Width, shots[2].Height)); // no placement: the framebuffer itself
        AssertColor(shots[2].At(2, 2), false, true, false, "the whole framebuffer");
    }

    /// <summary>Presents without a placement (every game's) compose nothing: no composite, the frame as it is.</summary>
    [SoftwareVulkanFact]
    public void APresentWithoutAPlacementComposesNothing()
    {
        using IDisposable driver = SoftwareVulkan.Use();
        using var harness = new HleHarness(fixture, "compatnone");
        uint data = harness.Alloc(W * H * 4);
        Fill32(data, W, H, Red);
        var shots = new List<Shot>();
        using VulkanRenderer renderer = NewRenderer(shots);
        using var clock = ControlledGuestClock.Stop();
        var frame = new DisplayFrame(data, W, W, H, DisplayPixelFormat.A8B8G8R8);

        renderer.Execute(new PresentCommand { Frame = frame, Flip = 0 });
        Fill32(data, W, H, Blue);
        clock.Advance(VblankUs);
        renderer.Idle(); // the scanout's frame too
        renderer.Execute(new FinishCommand());

        Assert.Equal(2, shots.Count);
        Assert.False(renderer.PlacementComposed);
        Assert.All(shots, shot => Assert.Equal(((int)W, (int)H), (shot.Width, shot.Height)));
    }
}
