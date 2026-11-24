namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// The PSP's system time (PSP 0xBC600000): the 1 MHz counter the kernel's <c>sceKernelGetSystemTimeLow</c> and the
/// thread manager read, and its alarm, the thread manager's timer interrupt (<see cref="PspInterrupt.Thread0"/>).
/// <list type="table">
/// <item><term>+0x00</term><description>the count in microseconds (32 bits, wraps); a write sets it.</description></item>
/// <item><term>+0x04</term><description>the alarm: when the count reaches it, the interrupt line goes high and
/// stays high until the next write of the alarm (the write is the acknowledge and arms the next one). An alarm the
/// count has already passed (less than 2^31 µs ago) fires at once.</description></item>
/// <item><term>+0x08, +0x0C, +0x10</term><description>registers the kernel writes at boot (0x30, 1 and 0) whose
/// function is not known; they read back what was written.</description></item>
/// </list>
/// Sources: Jpcsp <c>MMIOHandlerSystemTime</c> (offsets, the alarm and its interrupt, the alarm write clearing the
/// previous one, the boot writes) and <c>SystemTimeManager</c> (microseconds). Writing the count is inferred (Jpcsp
/// ignores the boot's write of 0, which this model also leaves at 0).
/// </summary>
public sealed partial class PspSystemTime : IPspMmioDevice, IPspTimedDevice
{
    /// <summary>PSP physical 0xBC600000.</summary>
    public const uint Base = 0x1C600000;

    public const uint BlockSize = 0x14;

    private readonly PspHardwareClock _clock;
    private readonly PspInterruptController _interrupts;
    private readonly uint[] _other = new uint[3];
    private uint _countOffset;   // count = clock µs + offset
    private uint _alarm;
    private bool _armed;
    private bool _lineHigh;

    public PspSystemTime(PspHardwareClock clock, PspInterruptController interrupts)
    {
        _clock = clock;
        _interrupts = interrupts;
        Reset();
    }

    public uint PhysicalBase => Base;

    public uint Size => BlockSize;

    /// <summary>The count now (µs).</summary>
    public uint Count => CountAt(_clock.Nanoseconds);

    public long NextEventNs
    {
        get
        {
            if (!_armed || _lineHigh)
            {
                return long.MaxValue;
            }

            long now = _clock.Nanoseconds;
            uint left = _alarm - CountAt(now);
            return ((now / 1000) + left) * 1000;
        }
    }

    public uint Read32(uint offset)
    {
        switch (offset)
        {
            case 0x00:
                return Count;
            case 0x04:
                return _alarm;
            case 0x08:
            case 0x0C:
            case 0x10:
                return _other[(offset - 0x08) / 4];
            default:
                return 0;
        }
    }

    public void Write32(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                _countOffset = value - (uint)(_clock.Nanoseconds / 1000);
                break;
            case 0x04:
                _alarm = value;
                _armed = true;
                SetLine(false);
                break;
            case 0x08:
            case 0x0C:
            case 0x10:
                _other[(offset - 0x08) / 4] = value;
                break;
            default:
                return;
        }

        Update();
    }

    public void Update()
    {
        if (_armed && !_lineHigh && (int)(CountAt(_clock.Nanoseconds) - _alarm) >= 0)
        {
            SetLine(true);
        }
    }

    /// <summary>No alarm, the count on the clock (0 after the hardware reset, which resets the clock), the other
    /// registers 0.</summary>
    public void Reset()
    {
        _countOffset = 0;
        _alarm = 0;
        _armed = false;
        Array.Clear(_other);
        _lineHigh = true;
        SetLine(false);
    }

    private uint CountAt(long nanoseconds)
    {
        return (uint)(nanoseconds / 1000) + _countOffset;
    }

    private void SetLine(bool high)
    {
        if (high != _lineHigh)
        {
            _lineHigh = high;
            _interrupts.SetLine(PspInterrupt.Thread0, high);
        }
    }
}
