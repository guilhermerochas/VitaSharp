using VitaSharp.Core.Compat.Allegrex;

namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// The PSP-side hardware of the Vita's compat core: the register blocks the PSP kernel in pcff reaches on the
/// Allegrex's bus, each an <see cref="IPspMmioDevice"/> of its region, the interrupt controller they drive, and the
/// clock that moves the timed ones. <see cref="PspBus"/> sends every access outside RAM, the shared SRAM and the
/// mailbox here (<see cref="TryRead32"/>/<see cref="TryWrite32"/>).
/// <para>
/// Time: whoever runs the core calls <see cref="Advance"/> with the core's own time after each slice (its cycles at
/// the compat clock plus the time it spent halted), and wakes a halted core no later than <see cref="NextEventNs"/>.
/// </para>
/// <para>
/// 8- and 16-bit accesses: the blocks are 32-bit register files. A narrow read returns the lane of the 32-bit read; a
/// narrow write writes the 32-bit register with the value in its lane and the other lanes 0 (the PSP kernel only
/// uses 32-bit accesses there; the "unknown MMIO" log of the boot trace shows any other).
/// </para>
/// </summary>
public sealed class PspHardware
{
    private IPspMmioDevice[] _devices = [];
    private IPspTimedDevice[] _timed = [];

    public PspHardware()
    {
        Clock = new PspHardwareClock();
        Interrupts = new PspInterruptController();
        Add(Interrupts);
    }

    /// <summary>The hardware's time.</summary>
    public PspHardwareClock Clock { get; }

    /// <summary>The interrupt controller (0xBC300000), the core's IP2.</summary>
    public PspInterruptController Interrupts { get; }

    /// <summary>The blocks, in the order they were added.</summary>
    public IReadOnlyList<IPspMmioDevice> Devices => _devices;

    /// <summary>
    /// The compat core's standard set: the interrupt controller, the system control registers, the CPU/bus clocks,
    /// the four hardware timers, the system time, the two DMA controllers (over <paramref name="memory"/>, the core's
    /// bus) and the DDR controller.
    /// </summary>
    public static PspHardware CreateStandard(IAllegrexBus memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        PspHardware hardware = new();
        hardware.Add(new PspHardwareTimers(hardware.Clock, hardware.Interrupts));
        hardware.Add(new PspSystemTime(hardware.Clock, hardware.Interrupts));
        hardware.Add(new PspDmac(PspDmac.Base0, PspInterrupt.Dma0, hardware.Interrupts, memory));
        hardware.Add(new PspDmac(PspDmac.Base1, PspInterrupt.Dma1, hardware.Interrupts, memory));
        return hardware;
    }

    /// <summary>Adds a block (before the core runs). Blocks must not overlap.</summary>
    public void Add(IPspMmioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        foreach (IPspMmioDevice existing in _devices)
        {
            bool overlaps = device.PhysicalBase < existing.PhysicalBase + existing.Size
                && existing.PhysicalBase < device.PhysicalBase + device.Size;
            if (overlaps)
            {
                throw new ArgumentException(
                    $"PSP hardware block at 0x{device.PhysicalBase:X8} overlaps the one at 0x{existing.PhysicalBase:X8}",
                    nameof(device));
            }
        }

        _devices = [.. _devices, device];
        if (device is IPspTimedDevice timed)
        {
            _timed = [.. _timed, timed];
        }
    }

    /// <summary>The block of a PSP physical address, or null.</summary>
    public IPspMmioDevice? Find(uint physical)
    {
        IPspMmioDevice[] devices = _devices;
        for (int i = 0; i < devices.Length; i++)
        {
            IPspMmioDevice device = devices[i];
            if (physical - device.PhysicalBase < device.Size)
            {
                return device;
            }
        }

        return null;
    }

    /// <summary>A 32-bit read of a register (PSP physical, word aligned); false when no block is there.</summary>
    public bool TryRead32(uint physical, out uint value)
    {
        IPspMmioDevice? device = Find(physical);
        if (device == null)
        {
            value = 0;
            return false;
        }

        value = device.Read32((physical - device.PhysicalBase) & ~3u);
        return true;
    }

    /// <summary>A 32-bit write of a register; false when no block is there.</summary>
    public bool TryWrite32(uint physical, uint value)
    {
        IPspMmioDevice? device = Find(physical);
        if (device == null)
        {
            return false;
        }

        device.Write32((physical - device.PhysicalBase) & ~3u, value);
        return true;
    }

    /// <summary>A narrow read (<paramref name="bytes"/> = 1 or 2): the lane of the 32-bit register.</summary>
    public bool TryReadNarrow(uint physical, int bytes, out uint value)
    {
        if (!TryRead32(physical & ~3u, out uint word))
        {
            value = 0;
            return false;
        }

        int shift = (int)(physical & 3) * 8;
        uint mask = bytes == 1 ? 0xFFu : 0xFFFFu;
        value = (word >> shift) & mask;
        return true;
    }

    /// <summary>A narrow write: the 32-bit register gets the value in its lane, the other lanes 0.</summary>
    public bool TryWriteNarrow(uint physical, int bytes, uint value)
    {
        int shift = (int)(physical & 3) * 8;
        uint mask = bytes == 1 ? 0xFFu : 0xFFFFu;
        return TryWrite32(physical & ~3u, (value & mask) << shift);
    }

    /// <summary>Moves the hardware to the core's time <paramref name="nanoseconds"/>: timers, alarms and DMA catch
    /// up and drive their interrupt lines.</summary>
    public void Advance(long nanoseconds)
    {
        Clock.AdvanceTo(nanoseconds);
        IPspTimedDevice[] timed = _timed;
        for (int i = 0; i < timed.Length; i++)
        {
            timed[i].Update();
        }
    }

    /// <summary>The time (ns) of the next change of a timed block, or <see cref="long.MaxValue"/>.</summary>
    public long NextEventNs
    {
        get
        {
            long next = long.MaxValue;
            IPspTimedDevice[] timed = _timed;
            for (int i = 0; i < timed.Length; i++)
            {
                next = Math.Min(next, timed[i].NextEventNs);
            }

            return next;
        }
    }

    /// <summary>The hardware reset of a new boot: the clock back to 0, every block in its reset state.</summary>
    public void Reset()
    {
        Clock.Reset();
        IPspMmioDevice[] devices = _devices;
        for (int i = 0; i < devices.Length; i++)
        {
            devices[i].Reset();
        }
    }
}
