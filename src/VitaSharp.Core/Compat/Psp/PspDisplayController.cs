namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// The PSP's display controller (PSP 0xBE740000, 0x28 bytes): the scan timing of the LCD and its vertical blank, the
/// PSP's interrupt 30 (<see cref="PspInterrupt.Vblank"/>, latched by the interrupt controller and acknowledged by
/// writing its bit to 0xBC300000, as the PSP kernel does).
/// <list type="table">
/// <item><term>+0x04</term><description>read: the line being scanned (0..285); write: the frame timing starts again
/// from this moment.</description></item>
/// <item><term>+0x08</term><description>read: the vertical blanks since the timing started.</description></item>
/// <item><term>+0x20</term><description>a status the IPL polls for its busy bits: 0 (never busy).</description></item>
/// <item><term>+0x00, +0x0C..+0x1C, +0x24</term><description>the control words the boot writes (0x20201 to
/// +0x00, 1 to +0x24...), kept as written.</description></item>
/// </list>
/// Timing: the PSP LCD's 9 MHz pixel clock over 525 pixels a line (480 + porches 2 + 2 + sync 41) and 286 lines a
/// frame (272 + 2 + 2 + 10): 58.3 µs a line, 16.68 ms a frame (59.94 Hz); the blank starts after line 271. The scan
/// runs from the reset (the Vita's screen is always scanning; Jpcsp starts its interrupts at the first write of
/// +0x04, which the PSP's display driver does at once). Sources: Jpcsp <c>MMIOHandlerDisplayController</c> (offsets,
/// 286 lines, the vblank interrupt) and <c>MMIOHandlerLcdc</c> (the reset timings), the PSP IPL SDK
/// (<c>display.c</c>: the boot writes and the +0x20 polls).
/// </summary>
public sealed partial class PspDisplayController : IPspMmioDevice, IPspTimedDevice
{
    /// <summary>PSP physical 0xBE740000.</summary>
    public const uint Base = 0x1E740000;

    public const uint BlockSize = 0x28;

    /// <summary>The pixel clock in MHz, the pixels of a line and the lines of a frame.</summary>
    public const long PixelClockMhz = 9;

    public const long PixelsPerLine = 525;

    public const long LinesPerFrame = 286;

    /// <summary>The first line of the vertical blank (the 272 visible lines come first).</summary>
    public const long VblankLine = 272;

    private readonly PspHardwareClock _clock;
    private readonly PspInterruptController _interrupts;
    private readonly uint[] _words = new uint[BlockSize / 4];
    private long _startNs;
    private long _vblanks;      // the vertical blanks raised so far

    public PspDisplayController(PspHardwareClock clock, PspInterruptController interrupts)
    {
        _clock = clock;
        _interrupts = interrupts;
        Reset();
    }

    public uint PhysicalBase => Base;

    public uint Size => BlockSize;

    /// <summary>A vertical blank started (after its interrupt was raised): the compat LCD DMA copies the frame. On the
    /// thread that moves the hardware (the core's).</summary>
    public Action? VblankStarted { get; set; }

    /// <summary>The vertical blanks so far.</summary>
    public long Vblanks => _vblanks;

    public long NextEventNs => _startNs + LineStartNs((_vblanks * LinesPerFrame) + VblankLine);

    public uint Read32(uint offset)
    {
        switch (offset)
        {
            case 0x04:
                return (uint)(LinesSince(_clock.Nanoseconds) % LinesPerFrame);
            case 0x08:
                return (uint)_vblanks;
            case 0x20:
                return 0;
            default:
                return _words[offset / 4];
        }
    }

    public void Write32(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x04:
                _startNs = _clock.Nanoseconds;
                _vblanks = 0;
                break;
            case 0x08:
            case 0x20:
                break;
            default:
                _words[offset / 4] = value;
                break;
        }
    }

    public void Update()
    {
        long now = _clock.Nanoseconds;
        long lines = LinesSince(now);
        long frames = lines < VblankLine ? 0 : ((lines - VblankLine) / LinesPerFrame) + 1;
        if (_vblanks < frames)
        {
            // one interrupt for the frames the clock jumped over (the line is latched: it is pending or not)
            _vblanks = frames;
            _interrupts.Trigger(PspInterrupt.Vblank);
            VblankStarted?.Invoke();
        }
    }

    /// <summary>The control words 0, the timing from the reset.</summary>
    public void Reset()
    {
        Array.Clear(_words);
        _startNs = _clock.Nanoseconds;
        _vblanks = 0;
    }

    private long LinesSince(long nanoseconds)
    {
        long elapsed = Math.Max(0, nanoseconds - _startNs);
        return (long)((UInt128)(ulong)elapsed * (ulong)PixelClockMhz / 1000 / (ulong)PixelsPerLine);
    }

    /// <summary>The time (ns from the start) the line <paramref name="line"/> starts, rounded up.</summary>
    private static long LineStartNs(long line)
    {
        UInt128 pixels = (UInt128)(ulong)line * (ulong)PixelsPerLine;
        return (long)(((pixels * 1000) + (ulong)PixelClockMhz - 1) / (ulong)PixelClockMhz);
    }
}
