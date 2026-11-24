namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// The PSP's four hardware timers (PSP 0xBC500000, one every 0x10 bytes, interrupts
/// <see cref="PspInterrupt.SystemTimer0"/>..<see cref="PspInterrupt.SystemTimer3"/>), the systimer of the PSP kernel
/// (<c>sceSTimer*</c>). Each timer:
/// <list type="table">
/// <item><term>+0x00 status</term><description>bits 0..21 the cycle R (the count of one period), bit 22 the
/// interrupt enable, bit 23 counting; bit 31 written with the 0→1 edge of bit 23 resets the overflow count (it is not
/// stored). Reads give bits 0..23 as written and, in bits 24..31, the periods completed since the last read (at most
/// 255). The read is the acknowledge: it consumes those periods and drops the interrupt.</description></item>
/// <item><term>+0x04 count</term><description>what is left of the current period: R − (ticks mod R), with the same
/// overflow byte; read only.</description></item>
/// <item><term>+0x08, +0x0C prescaler</term><description>numerator N and denominator D: the timer counts
/// 48 MHz × N / D (the kernel writes −1/−1 at init: 48 MHz).</description></item>
/// <item><term>+0x100 + timer × 0x10</term><description>the status without the acknowledge (the kernel's
/// <c>nowData</c>).</description></item>
/// </list>
/// The interrupt line is high while the timer counts with its interrupt enabled and a period completed since the
/// last status read. Sources: uOFW <c>systimer.c</c> (the register struct, the modes, start/stop/cycle/handler
/// writes, the count = now − base the kernel computes, −1 prescalers at init) and Jpcsp <c>MMIOHandlerTimer</c> (the
/// 48 MHz base, the count and overflow byte, the read that acknowledges, the read-only view at +0x100).
/// </summary>
public sealed class PspHardwareTimers : IPspMmioDevice, IPspTimedDevice
{
    /// <summary>PSP physical 0xBC500000.</summary>
    public const uint Base = 0x1C500000;

    /// <summary>Up to the last read-only view (0x100 + 3 × 0x10 + 0x10).</summary>
    public const uint BlockSize = 0x140;

    public const int TimerCount = 4;

    /// <summary>The bytes between two timers.</summary>
    public const uint Stride = 0x10;

    /// <summary>The read-only views of the status.</summary>
    public const uint ReadOnlyStatus = 0x100;

    /// <summary>The base clock in MHz.</summary>
    public const uint BaseClockMhz = 48;

    public const uint CycleMask = 0x003FFFFF;
    public const uint InterruptEnable = 0x00400000;
    public const uint Counting = 0x00800000;
    public const uint ResetOverflow = 0x80000000;

    private readonly PspHardwareClock _clock;
    private readonly PspInterruptController _interrupts;
    private readonly Timer[] _timers = new Timer[TimerCount];

    public PspHardwareTimers(PspHardwareClock clock, PspInterruptController interrupts)
    {
        _clock = clock;
        _interrupts = interrupts;
        Reset();
    }

    public uint PhysicalBase => Base;

    public uint Size => BlockSize;

    public long NextEventNs
    {
        get
        {
            long next = long.MaxValue;
            for (int i = 0; i < TimerCount; i++)
            {
                next = Math.Min(next, NextOverflowNs(ref _timers[i]));
            }

            return next;
        }
    }

    public uint Read32(uint offset)
    {
        long now = _clock.Nanoseconds;
        if (offset >= ReadOnlyStatus)
        {
            int viewed = (int)((offset - ReadOnlyStatus) / Stride);
            if ((offset & (Stride - 1)) != 0 || viewed >= TimerCount)
            {
                return 0;
            }

            return StatusValue(ref _timers[viewed], now);
        }

        int index = (int)(offset / Stride);
        if (index >= TimerCount)
        {
            return 0;
        }

        ref Timer timer = ref _timers[index];
        uint value;
        switch (offset & (Stride - 1))
        {
            case 0x0:
                value = StatusValue(ref timer, now);
                timer.OverflowRead = Overflows(ref timer, now);
                break;
            case 0x4:
                value = CountValue(ref timer, now);
                break;
            case 0x8:
                value = timer.Numerator;
                break;
            default:
                value = timer.Denominator;
                break;
        }

        UpdateLine(index, now);
        return value;
    }

    public void Write32(uint offset, uint value)
    {
        if (offset >= ReadOnlyStatus)
        {
            return;
        }

        int index = (int)(offset / Stride);
        if (index >= TimerCount)
        {
            return;
        }

        long now = _clock.Nanoseconds;
        ref Timer timer = ref _timers[index];
        switch (offset & (Stride - 1))
        {
            case 0x0:
                bool starts = (timer.Status & Counting) == 0 && (value & Counting) != 0;
                timer.Status = value & 0x00FFFFFF;
                if (starts)
                {
                    // the count starts from this write; so do the periods it reports
                    timer.StartNs = now;
                    timer.OverflowRead = 0;
                }

                break;
            case 0x4:
                // the count is read only
                break;
            case 0x8:
                timer.Numerator = value;
                break;
            default:
                timer.Denominator = value;
                break;
        }

        UpdateLine(index, now);
    }

    public void Update()
    {
        long now = _clock.Nanoseconds;
        for (int i = 0; i < TimerCount; i++)
        {
            UpdateLine(i, now);
        }
    }

    /// <summary>Every timer stopped, cycle 0, prescaler 0/0 (no count until the kernel sets it).</summary>
    public void Reset()
    {
        for (int i = 0; i < TimerCount; i++)
        {
            _timers[i] = default;
            _interrupts.SetLine(PspInterrupt.SystemTimer0 + i, false);
        }
    }

    private void UpdateLine(int index, long now)
    {
        ref Timer timer = ref _timers[index];
        bool high = (timer.Status & (Counting | InterruptEnable)) == (Counting | InterruptEnable)
            && Overflows(ref timer, now) > timer.OverflowRead;
        if (high != timer.LineHigh)
        {
            timer.LineHigh = high;
            _interrupts.SetLine(PspInterrupt.SystemTimer0 + index, high);
        }
    }

    private static uint StatusValue(ref Timer timer, long now)
    {
        return (timer.Status & 0x00FFFFFF) | OverflowByte(ref timer, now);
    }

    private static uint CountValue(ref Timer timer, long now)
    {
        uint cycle = timer.Status & CycleMask;
        if ((timer.Status & Counting) == 0 || cycle == 0 || !Ticking(ref timer))
        {
            return 0;
        }

        ulong ticks = Ticks(ref timer, now);
        uint left = cycle - (uint)(ticks % cycle);
        return (left & 0x00FFFFFF) | OverflowByte(ref timer, now);
    }

    private static uint OverflowByte(ref Timer timer, long now)
    {
        ulong pending = Overflows(ref timer, now) - timer.OverflowRead;
        return (uint)Math.Min(pending, 0xFFUL) << 24;
    }

    /// <summary>The periods completed since the count started.</summary>
    private static ulong Overflows(ref Timer timer, long now)
    {
        uint cycle = timer.Status & CycleMask;
        if ((timer.Status & Counting) == 0 || cycle == 0 || !Ticking(ref timer))
        {
            return 0;
        }

        return Ticks(ref timer, now) / cycle;
    }

    private static bool Ticking(ref Timer timer)
    {
        return timer.Numerator != 0 && timer.Denominator != 0;
    }

    /// <summary>Ticks since the start: elapsed ns × 48 × N / (1000 × D).</summary>
    private static ulong Ticks(ref Timer timer, long now)
    {
        ulong elapsed = (ulong)Math.Max(0, now - timer.StartNs);
        UInt128 ticks = (UInt128)elapsed * BaseClockMhz * timer.Numerator / ((UInt128)1000 * timer.Denominator);
        return ticks > ulong.MaxValue ? ulong.MaxValue : (ulong)ticks;
    }

    /// <summary>The time of the next period end that would raise the line, or <see cref="long.MaxValue"/>.</summary>
    private static long NextOverflowNs(ref Timer timer)
    {
        uint cycle = timer.Status & CycleMask;
        bool armed = (timer.Status & (Counting | InterruptEnable)) == (Counting | InterruptEnable);
        if (!armed || cycle == 0 || !Ticking(ref timer) || timer.LineHigh)
        {
            return long.MaxValue;
        }

        // the first tick count past the periods already read: (OverflowRead + 1) × R, rounded up to whole ns
        UInt128 targetTicks = ((UInt128)timer.OverflowRead + 1) * cycle;
        UInt128 numerator = targetTicks * 1000 * timer.Denominator;
        UInt128 denominator = (UInt128)BaseClockMhz * timer.Numerator;
        UInt128 elapsed = (numerator + denominator - 1) / denominator;
        UInt128 at = (UInt128)(ulong)timer.StartNs + elapsed;
        return at >= long.MaxValue ? long.MaxValue : (long)(ulong)at;
    }

    private struct Timer
    {
        public uint Status;
        public uint Numerator;
        public uint Denominator;
        public long StartNs;
        public ulong OverflowRead;
        public bool LineHigh;
    }
}
