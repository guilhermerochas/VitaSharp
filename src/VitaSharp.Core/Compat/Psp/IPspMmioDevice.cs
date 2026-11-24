namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// A PSP-side hardware block the compat core reaches by MMIO on its own bus (the <c>0xBC000000</c>..<c>0xBDFFFFFF</c>
/// register blocks of the PSP's Tachyon, as the PSP kernel in the Vita's pcff uses them). Addresses are PSP physical
/// (<c>&amp; 0x1FFFFFFF</c>: register block 0xBC300000 is 0x1C300000). The registers are 32-bit; the bus turns 8- and
/// 16-bit accesses into 32-bit ones (<see cref="PspHardware"/>).
/// </summary>
public interface IPspMmioDevice
{
    /// <summary>The first PSP physical address of the block.</summary>
    uint PhysicalBase { get; }

    /// <summary>The block's size in bytes.</summary>
    uint Size { get; }

    /// <summary>A 32-bit read at <paramref name="offset"/> (word aligned) in the block.</summary>
    uint Read32(uint offset);

    /// <summary>A 32-bit write at <paramref name="offset"/> (word aligned) in the block.</summary>
    void Write32(uint offset, uint value);

    /// <summary>The block's state after a hardware reset.</summary>
    void Reset();
}

/// <summary>A block whose state moves with time (timers, alarms, DMA transfers in progress).</summary>
public interface IPspTimedDevice
{
    /// <summary>Brings the block's state (and its interrupt lines) to the clock's current time.</summary>
    void Update();

    /// <summary>The clock time (ns) of the block's next state change, or <see cref="long.MaxValue"/> for none.</summary>
    long NextEventNs { get; }
}

/// <summary>
/// The time of the PSP-side hardware, in nanoseconds since the core's reset. It only moves forward, and only when the
/// side that runs the core says so (<see cref="PspHardware.Advance"/>): the devices are deterministic functions of the
/// core's own time, never of the host clock.
/// </summary>
public sealed class PspHardwareClock
{
    private long _nanoseconds;

    /// <summary>The current time in nanoseconds.</summary>
    public long Nanoseconds => Volatile.Read(ref _nanoseconds);

    /// <summary>Moves the clock to <paramref name="nanoseconds"/> (ignored when it is in the past).</summary>
    public void AdvanceTo(long nanoseconds)
    {
        if (nanoseconds > _nanoseconds)
        {
            Volatile.Write(ref _nanoseconds, nanoseconds);
        }
    }

    /// <summary>Back to time 0 (a new boot of the core).</summary>
    public void Reset()
    {
        Volatile.Write(ref _nanoseconds, 0);
    }
}
