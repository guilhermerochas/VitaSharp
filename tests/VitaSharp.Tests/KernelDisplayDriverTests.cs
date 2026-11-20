using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// SceDisplayForDriver: the heads and planes on the user display state and the framebuffer capture. (FW 3.74)
/// </summary>
[Collection("guest")]
public sealed class KernelDisplayDriverTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint InvalidHead = 0x80290000, DisplayInvalidValue = 0x80290001;

    private readonly HleHarness _harness = new(fixture, "displaydri");

    public void Dispose() => _harness.Dispose();

    private unsafe uint R0(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        uint stack = thread.Core.State->R[13];
        thread.Core.State->R[13] = stack - 0x40;
        for (int i = 4; i < args.Length; i++)
        {
            GuestMemory.W32(stack - 0x40 + (uint)(i - 4) * 4, args[i]);
        }

        implementation(thread.Hle);
        thread.Core.State->R[13] = stack;
        return thread.Core.State->R[0];
    }

    private static unsafe void SetS0(GuestThread thread, float value) => ((float*)thread.Core.State->D)[0] = value;

    private uint Sized(uint size)
    {
        uint record = _harness.Alloc(size);
        GuestMemory.W32(record, size);
        return record;
    }

    [Fact]
    public void DisplayHeadsKeepTheirSettingsAndCheckTheirRanges()
    {
        _harness.OnGuest(t =>
        {
            uint type = _harness.Word(0xFFFF);
            Assert.Equal(1u, R0(t, 0x8D9A1CCE, 0, type)); // the OLED panel
            Assert.Equal(0x103u, GuestMemory.R32(type));
            Assert.Equal(0u, R0(t, 0x8D9A1CCE, 1, type)); // nothing on HDMI
            Assert.Equal(InvalidHead, R0(t, 0x8D9A1CCE, 3, type));

            uint rate = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0x7911958E, 0, rate, 0));
            Assert.Equal(59.94f, BitConverter.UInt32BitsToSingle(GuestMemory.R32(rate)));
            Assert.Equal(1u, R0(t, 0x332C5410, 0));
            Assert.Equal(0u, R0(t, 0x43347565, 0)); // disable head 0
            Assert.Equal(0u, R0(t, 0x332C5410, 0));
            Assert.Equal(InvalidHead, R0(t, 0x43347565, 2));

            Assert.Equal(DisplayInvalidValue, R0(t, 0xAF5EE5BE, 0, 0, 0x1234));
            Assert.Equal(0u, R0(t, 0xAF5EE5BE, 0, 7, 0x100000));
            uint mode = _harness.Word(0);
            uint extra = _harness.Word(0);
            Assert.Equal(0u, R0(t, 0xD2CED235, 0, mode, extra));
            Assert.Equal(0x100000u, GuestMemory.R32(mode));
            Assert.Equal(7u, GuestMemory.R32(extra));

            Assert.Equal(DisplayInvalidValue, R0(t, 0x6B198052, 0, 1, 0x101));
            Assert.Equal(0u, R0(t, 0x6B198052, 0, 1, 0x100));

            SetS0(t, 1.5f);
            Assert.Equal(DisplayInvalidValue, R0(t, 0xEB390A76, 0, 0, 1));
            SetS0(t, 1.1f);
            Assert.Equal(0u, R0(t, 0xEB390A76, 0, 0, 1));
            SetS0(t, float.NaN);
            Assert.Equal(DisplayInvalidValue, R0(t, 0xEB390A76, 0, 0, 0));
            Assert.Equal(InvalidHead, R0(t, 0xEB390A76, 2, 0, 0));

            uint viewport = Sized(0x14);
            uint actual = Sized(0x14);
            GuestMemory.W32(viewport + 4, 10);
            GuestMemory.W32(viewport + 8, 20);
            GuestMemory.W32(viewport + 0xC, 480);
            GuestMemory.W32(viewport + 0x10, 272);
            Assert.Equal(0u, R0(t, 0xEE5EB52D, 0, 0, viewport));
            Assert.Equal(0u, R0(t, 0x40ACFE51, 0, 0, actual));
            Assert.Equal(0u, GuestMemory.R32(actual + 0xC)); // nobody owns the plane yet
            Assert.Equal(0u, R0(t, 0xB54962A1, 0, 0, (uint)t.Pid));
            Assert.Equal(0u, R0(t, 0x40ACFE51, 0, 0, actual));
            Assert.Equal(10u, GuestMemory.R32(actual + 4));
            Assert.Equal(480u, GuestMemory.R32(actual + 0xC));
            Assert.Equal(DisplayInvalidValue, R0(t, 0x40ACFE51, 0, 0, _harness.Alloc(0x14)));
        });
    }

    [Fact]
    public void DisplayCaptureScalesTheShownFrameBuffer()
    {
        _harness.OnGuest(t =>
        {
            uint source = _harness.Alloc(4 * 4 * 4);
            for (uint i = 0; i < 16; i++)
            {
                GuestMemory.W32(source + i * 4, 0xFF000000 | i);
            }

            uint frame = Sized(0x18);
            GuestMemory.W32(frame + 4, source);
            GuestMemory.W32(frame + 8, 4);
            GuestMemory.W32(frame + 0xC, 0);
            GuestMemory.W32(frame + 0x10, 4);
            GuestMemory.W32(frame + 0x14, 4);
            uint capture = Sized(0x18);
            uint target = _harness.Alloc(0x200) + 0xFF & ~0xFFu;
            GuestMemory.W32(capture + 4, target);
            GuestMemory.W32(capture + 8, 2);
            GuestMemory.W32(capture + 0xC, 0);
            GuestMemory.W32(capture + 0x10, 2);
            GuestMemory.W32(capture + 0x14, 2);
            Assert.Equal(0x80290008u, R0(t, 0xB0CED8BC, 0, capture)); // nothing set yet
            Assert.Equal(0u, R0(t, 0x7A410B64, frame, 0)); // sceDisplaySetFrameBuf
            Assert.Equal(0u, R0(t, 0xB0CED8BC, 0, capture));
            Assert.Equal(0xFF000000u, GuestMemory.R32(target));
            Assert.Equal(0xFF000002u, GuestMemory.R32(target + 4));
            Assert.Equal(0xFF00000Au, GuestMemory.R32(target + 12));
            GuestMemory.W32(capture + 0xC, 0x1234);
            Assert.Equal(0x80290003u, R0(t, 0xF116D0B4, 0, capture));
            Assert.Equal(InvalidHead, R0(t, 0xD4C812E5, 0, 2, 0, capture));
        });
    }
}
