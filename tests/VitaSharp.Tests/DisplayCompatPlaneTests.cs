using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// sceDisplaySetFrameBufForCompat (x, y, width, height, pParam, opt) and its syscall (x, y, width, args), as FW 3.74
/// display.skprx checks them: system programs only, the SceDisplayFrameBuf's size, base alignment, pitch and pixel
/// format, null removing the plane, the syscall's argument block and its size limits.
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
}
