using VitaSharp.Core.Gpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// sceDisplaySetFrameBufForCompat (x, y, width, height, pParam, opt) and its syscall (x, y, width, args), as FW 3.74
/// display.skprx checks them: system programs only, the SceDisplayFrameBuf's size, base alignment, pitch and pixel
/// format, null removing the plane, the syscall's argument block and its size limits. The framebuffer is plane 0's
/// (the record sceDisplaySetFrameBuf writes): shown in its rectangle until a sceDisplaySetFrameBuf shows a framebuffer
/// whole again.
/// </summary>
[Collection("guest")]
public sealed class DisplayCompatPlaneTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotSystem = unchecked((int)0x80010058), InvalidValue = unchecked((int)0x80290001);
    private const int InvalidAddress = unchecked((int)0x80290002), InvalidFormat = unchecked((int)0x80290003);
    private const int InvalidPitch = unchecked((int)0x80290004), TooLarge = unchecked((int)0x8002710B);

    private readonly HleHarness _harness = new(fixture, "dispcompat");

    public void Dispose()
    {
        _harness.Dispose();
    }

    private Display Display => _harness.Module<Display>();

    private uint FrameBuf(uint size, uint baseAddress, uint pitch, uint format, uint width, uint height)
    {
        uint param = _harness.Alloc(0x20);
        GuestMemory.W32(param, size);
        GuestMemory.W32(param + 4, baseAddress);
        GuestMemory.W32(param + 8, pitch);
        GuestMemory.W32(param + 12, format);
        GuestMemory.W32(param + 16, width);
        GuestMemory.W32(param + 20, height);
        return param;
    }

    [Fact]
    public void OnlySystemProgramsSetThePlane()
    {
        Assert.Equal(NotSystem, Call(Display, "sceDisplaySetFrameBufForCompat", 0, 0, 960, 544, 0u, 0u));
    }

    [Fact]
    public void ThePlaneIsKeptAndNullRemovesIt()
    {
        _harness.Emu.IsSystemProgram = true;
        uint frame = 0x81000000;
        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBufForCompat", 240, 136, 480, 272, FrameBuf(0x1C, frame, 512, 0, 480, 272), 0u));
        Assert.Equal((240, 136, 480, 272, frame, 512u, 0u), Display.CompatPlane);
        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBufForCompat", 0, 0, 0, 0, 0u, 0u));
        Assert.Equal(0u, Display.CompatPlane.Base);
        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBufForCompat", 0, 0, 640, 368, FrameBuf(0x1C, frame, 640, Display.CompatPlaneFormatOther, 640, 368), 0u));
        Assert.Equal(Display.CompatPlaneFormatOther, Display.CompatPlane.PixelFormat);
    }

    [Theory]
    [InlineData(0x18u, 0x81000000u, 512u, 0u, 480u, InvalidValue)]
    [InlineData(0x1Cu, 0u, 512u, 0u, 480u, InvalidAddress)]
    [InlineData(0x1Cu, 0x81000080u, 512u, 0u, 480u, InvalidAddress)]
    [InlineData(0x1Cu, 0x81000000u, 512u, 3u, 480u, InvalidFormat)]
    [InlineData(0x1Cu, 0x81000000u, 500u, 0u, 480u, InvalidPitch)]
    [InlineData(0x1Cu, 0x81000000u, 448u, 0u, 480u, InvalidPitch)]
    [InlineData(0x1Cu, 0x81000000u, 2560u, 0u, 480u, InvalidPitch)]
    [InlineData(0x1Cu, 0x81000000u, 4544u, CompatPlaneOther, 480u, 0)]
    public void TheFrameBufferIsChecked(uint size, uint baseAddress, uint pitch, uint format, uint width, int expected)
    {
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(expected, Call(Display, "sceDisplaySetFrameBufForCompat", 0, 0, 480, 272, FrameBuf(size, baseAddress, pitch, format, width, 272), 0u));
    }

    private const uint CompatPlaneOther = Display.CompatPlaneFormatOther;

    [Fact]
    public void TheSyscallTakesItsArgumentBlock()
    {
        _harness.Emu.IsSystemProgram = true;
        uint args = _harness.Alloc(0x18);
        GuestMemory.W32(args, 272);
        GuestMemory.W32(args + 4, FrameBuf(0x1C, 0x81000000, 512, 0, 480, 272));
        GuestMemory.W32(args + 12, 0x1C);
        Assert.Equal(0, Call(Display, "_sceDisplaySetFrameBufForCompat", 10, 20, 480, args));
        Assert.Equal((10, 20, 480, 272, 0x81000000u, 512u, 0u), Display.CompatPlane);

        GuestMemory.W32(args + 12, 0x20);
        Assert.Equal(TooLarge, Call(Display, "_sceDisplaySetFrameBufForCompat", 10, 20, 480, args));
        GuestMemory.W32(args + 12, 0x1C);
        GuestMemory.W32(args + 8, _harness.Alloc(0x20));
        GuestMemory.W32(args + 16, 0x1C);
        Assert.Equal(TooLarge, Call(Display, "_sceDisplaySetFrameBufForCompat", 10, 20, 480, args));
    }

    /// <summary>Records the presents the display gets.</summary>
    private sealed class PresentRecorder : IGpuBackend
    {
        public readonly List<PresentCommand> Presents = [];

        public long PresentCount => 0;

        public byte[]? ExportDumpState() => null;

        public void Execute(GpuCommand command)
        {
            if (command is PresentCommand present)
            {
                lock (Presents)
                {
                    Presents.Add(present);
                }
            }
        }

        public void Dispose()
        {
        }

        public PresentCommand WaitFor(int count)
        {
            Until(() =>
            {
                lock (Presents)
                {
                    return Presents.Count >= count;
                }
            }, $"{count} presents");
            lock (Presents)
            {
                return Presents[count - 1];
            }
        }
    }

    private uint FrameBufWithFlags(uint baseAddress, uint pitch, uint format, uint width, uint height, uint flags)
    {
        uint param = FrameBuf(0x1C, baseAddress, pitch, format, width, height);
        GuestMemory.W32(param + 24, flags);
        return param;
    }

    /// <summary>The compat framebuffer is presented placed in its rectangle, with the filter of the flags' bit 20, and
    /// sceDisplayGetFrameBuf reads it back (it is plane 0's record).</summary>
    [Fact]
    public void TheCompatFramebufferIsPresentedInItsRectangle()
    {
        var recorder = new PresentRecorder();
        _harness.Emu.Gpu.Backend = recorder;
        _harness.Emu.IsSystemProgram = true;
        uint frame = _harness.Alloc(512 * 272 * 2 + 0x100) + 0xFF & ~0xFFu;
        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBufForCompat", 0, 0, 960, 544, FrameBufWithFlags(frame, 512, Display.CompatPlaneFormatOther, 480, 272, 0x100000), 0u));
        PresentCommand present = recorder.WaitFor(1);
        Assert.Equal(new DisplayFrame(frame, 512, 480, 272, DisplayPixelFormat.A1B5G5R5), present.Frame);
        Assert.Equal(new DisplayPlacement(0, 0, 960, 544, Bilinear: true), present.Placement);

        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBufForCompat", 240, 136, 480, 272, FrameBufWithFlags(frame, 512, 0, 480, 272, 0), 0u));
        present = recorder.WaitFor(2);
        Assert.Equal(new DisplayPlacement(240, 136, 480, 272, Bilinear: false), present.Placement);

        uint read = FrameBuf(0x18, 0, 0, 0, 0, 0);
        Assert.Equal(0, Call(Display, "sceDisplayGetFrameBuf", read, 0u));
        Assert.Equal(frame, GuestMemory.R32(read + 4));
        Assert.Equal(512u, GuestMemory.R32(read + 8));
        Assert.Equal(480u, GuestMemory.R32(read + 16));
    }

    /// <summary>A sceDisplaySetFrameBuf after the compat framebuffer shows its framebuffer whole (no placement); null
    /// leaves plane 0 without a framebuffer.</summary>
    [Fact]
    public void ASetFrameBufAfterTheCompatOneShowsItsFramebufferWhole()
    {
        var recorder = new PresentRecorder();
        _harness.Emu.Gpu.Backend = recorder;
        _harness.Emu.IsSystemProgram = true;
        uint compat = _harness.Alloc(512 * 272 * 4 + 0x100) + 0xFF & ~0xFFu;
        uint whole = _harness.Alloc(64 * 64 * 4);
        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBufForCompat", 240, 136, 480, 272, FrameBuf(0x1C, compat, 512, 0, 480, 272), 0u));
        Assert.NotNull(recorder.WaitFor(1).Placement);

        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBuf", FrameBuf(0x18, whole, 64, 0, 64, 64), 1u));
        PresentCommand present = recorder.WaitFor(2);
        Assert.Null(present.Placement);
        Assert.Equal(whole, present.Frame.Address);
        Assert.Equal(0u, Display.CompatPlane.Base);

        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBufForCompat", 240, 136, 480, 272, FrameBuf(0x1C, compat, 512, 0, 480, 272), 0u));
        Assert.NotNull(recorder.WaitFor(3).Placement);
        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBufForCompat", 0, 0, 0, 0, 0u, 0u));
        Assert.Equal(0u, Display.CompatPlane.Base);
        uint read = FrameBuf(0x18, 1, 0, 0, 0, 0);
        Assert.Equal(0, Call(Display, "sceDisplayGetFrameBuf", read, 0u));
        Assert.Equal(0u, GuestMemory.R32(read + 4)); // plane 0's record has no framebuffer
    }

    /// <summary>With opt the IFTU converts into the opt buffer: the screen and plane 0's record are left alone.</summary>
    [Fact]
    public void WithOptTheScreenIsLeftAlone()
    {
        var recorder = new PresentRecorder();
        _harness.Emu.Gpu.Backend = recorder;
        _harness.Emu.IsSystemProgram = true;
        uint compat = _harness.Alloc(512 * 272 * 4 + 0x100) + 0xFF & ~0xFFu;
        Assert.Equal(0, Call(Display, "sceDisplaySetFrameBufForCompat", 0, 0, 480, 272, FrameBuf(0x1C, compat, 512, 0, 480, 272), _harness.Alloc(0x20)));
        Assert.Equal(0u, Display.CompatPlane.Base);
        _harness.Emu.Gpu.WaitFor(_harness.Emu.Gpu.Submit(new FinishCommand()));
        lock (recorder.Presents)
        {
            Assert.Empty(recorder.Presents);
        }
    }
}
