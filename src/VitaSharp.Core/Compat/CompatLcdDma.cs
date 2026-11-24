using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Ge;
using VitaSharp.Core.Compat.Psp;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Compat;

/// <summary>
/// SceCompatLCDDMA (FW 3.74 SceCompat: ARM physical 0xE5071000): the engine that copies the PSP's picture into the
/// Vita framebuffer block ScePspemu shows (sceCompatFrameBufferInit's block, shown by sceCompatSetDisplayConfig).
/// <para>
/// What sceCompatStart programs (when a framebuffer block exists): +0x24 = 0xFF0000, +0x28 = 0, +0xC0 = 0, then, with
/// the engine idle (+0x30 bits 0..1 clear), the destination side: +0x34 the width (480), +0x3C the height (272),
/// +0x4C the block's physical address, +0x50 its pitch in bytes (0x800: 512 pixels of 4 bytes), +0x94 = 2, +0x98 = 1,
/// +0xC0 = 0x1000000, and +4 = 1 starts it. The suspend path asks the frame to end (+0xBC = 0x1000000, waits for its
/// bit 24), stops it (+0 = 1, waits for +8 to read 0). Its frame interrupt is the ARM's IRQ 0x4A ("SceCompatLcdc").
/// </para>
/// <para>
/// The source side is not programmed by SceCompat: it is the PSP's picture, which the PSP's display driver sets in
/// the DMACplus scan-out channel (<see cref="PspLcdc"/>, inferred). At every PSP vertical blank
/// (<see cref="PspDisplayController.VblankStarted"/>) a running engine copies the visible 480x272 into the block and
/// converts the PSP pixel format to the Vita framebuffer's A8B8G8R8 (8888 is copied as is; 565, 5551 and 4444 are
/// widened, alpha opaque for 565).
/// </para>
/// </summary>
public sealed class CompatLcdDma(IAllegrexBus source, PspLcdc lcdc)
{
    /// <summary>The ARM physical address SceCompat maps the block from.</summary>
    public const uint ArmPhysicalBase = 0xE5071000;

    /// <summary>The frame interrupt on the ARM side.</summary>
    public const int ArmInterrupt = 0x4A;

    private uint _destination;
    private int _width;
    private int _height;
    private int _pitchBytes;
    private bool _running;
    private long _frames;

    /// <summary>The engine copies at every PSP vertical blank.</summary>
    public bool Running => Volatile.Read(ref _running);

    /// <summary>The frames copied (each one an IRQ 0x4A on the ARM side).</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>A frame was copied: the ARM's IRQ 0x4A. On the thread that moves the PSP hardware.</summary>
    public Action? FrameDone { get; set; }

    /// <summary>
    /// Called right before the engine reads the PSP picture, with its PSP physical address and its size in bytes (the
    /// stride times the lines): whoever keeps PSP memory elsewhere (the GE's render target on the host GPU) writes it
    /// back to PSP memory here. On the thread that moves the PSP hardware.
    /// </summary>
    public Action<uint, int>? BeforeRead { get; set; }

    /// <summary>sceCompatStart's destination side: the block (Vita address), its size and pitch.</summary>
    public void Configure(uint destination, int width, int height, int pitchBytes)
    {
        _destination = destination;
        _width = width;
        _height = height;
        _pitchBytes = pitchBytes;
    }

    /// <summary>+4 = 1: the engine runs.</summary>
    public void Start()
    {
        Volatile.Write(ref _running, _destination != 0 && _width > 0 && _height > 0);
    }

    /// <summary>+0 = 1: the engine stops (+8 reads 0).</summary>
    public void Stop()
    {
        Volatile.Write(ref _running, false);
    }

    /// <summary>A PSP vertical blank: copies the picture when the engine runs and the PSP scans one out.</summary>
    public void OnVblank()
    {
        if (!Running || !lcdc.ScanningOut || lcdc.FramebufferAddress == 0)
        {
            return;
        }

        int bytesPerPixel = lcdc.PixelFormat == 3 ? 4 : 2;
        int stride = (int)(lcdc.Stride != 0 ? lcdc.Stride : lcdc.Width);
        int width = _width;
        BeforeRead?.Invoke(lcdc.FramebufferAddress, stride * bytesPerPixel * _height);
        IGeMemory? spans = source as IGeMemory;
        for (int row = 0; row < _height; row++)
        {
            uint from = lcdc.FramebufferAddress + (uint)(row * stride * bytesPerPixel);
            Span<uint> to = MemoryMarshal.Cast<byte, uint>(GuestMemory.Span(_destination + (uint)(row * _pitchBytes), width * 4));
            Span<byte> line = spans != null ? spans.Span(from, width * bytesPerPixel) : [];
            if (line.Length == width * bytesPerPixel)
            {
                ConvertLine(line, to, lcdc.PixelFormat);
            }
            else
            {
                ConvertLineByWords(from, to, lcdc.PixelFormat, bytesPerPixel);
            }
        }

        Interlocked.Increment(ref _frames);
        FrameDone?.Invoke();
    }

    /// <summary>A line of PSP pixels to A8B8G8R8.</summary>
    public static void ConvertLine(ReadOnlySpan<byte> line, Span<uint> to, uint format)
    {
        if (format == 3)
        {
            MemoryMarshal.Cast<byte, uint>(line).CopyTo(to);
            return;
        }

        for (int x = 0; x < to.Length; x++)
        {
            to[x] = Widen(BinaryPrimitives.ReadUInt16LittleEndian(line.Slice(x * 2, 2)), format);
        }
    }

    /// <summary>A 16-bit PSP pixel (format 0 BGR565, 1 ABGR1555, 2 ABGR4444) as A8B8G8R8.</summary>
    public static uint Widen(ushort pixel, uint format)
    {
        uint r;
        uint g;
        uint b;
        uint a;
        switch (format)
        {
            case 0:
                r = Expand((uint)pixel & 0x1F, 5);
                g = Expand(((uint)pixel >> 5) & 0x3F, 6);
                b = Expand(((uint)pixel >> 11) & 0x1F, 5);
                a = 0xFF;
                break;
            case 1:
                r = Expand((uint)pixel & 0x1F, 5);
                g = Expand(((uint)pixel >> 5) & 0x1F, 5);
                b = Expand(((uint)pixel >> 10) & 0x1F, 5);
                a = (pixel & 0x8000) != 0 ? 0xFFu : 0;
                break;
            default:
                r = Expand((uint)pixel & 0xF, 4);
                g = Expand(((uint)pixel >> 4) & 0xF, 4);
                b = Expand(((uint)pixel >> 8) & 0xF, 4);
                a = Expand(((uint)pixel >> 12) & 0xF, 4);
                break;
        }

        return r | (g << 8) | (b << 16) | (a << 24);
    }

    private void ConvertLineByWords(uint from, Span<uint> to, uint format, int bytesPerPixel)
    {
        for (int x = 0; x < to.Length; x++)
        {
            uint address = from + (uint)(x * bytesPerPixel);
            to[x] = format == 3 ? source.Read32(address) : Widen(source.Read16(address), format);
        }
    }

    private static uint Expand(uint value, int bits)
    {
        return (value << (8 - bits)) | (value >> ((2 * bits) - 8));
    }
}
