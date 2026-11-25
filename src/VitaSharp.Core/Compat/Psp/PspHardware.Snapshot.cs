using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// Save states of the PSP-side hardware (docs/notes/save-states.md): the clock, then every block that has state, by its
/// physical base (the set is fixed by <see cref="CreateStandard"/>, so a load finds each block of the saving build at
/// the same base), then the GE. The blocks' wiring (the interrupt controller and clock they drive, the core's bus, the
/// callbacks) is the running session's and is not saved.
/// </summary>
[GenerateSnapshot, SnapshotIgnoreFields("_devices", "_timed", "Interrupts", "Ge", "Display", "Lcdc")]
public sealed partial class PspHardware
{
    private void SaveExtra(SnapshotWriter w)
    {
        IPspMmioDevice[] devices = _devices;
        int count = 0;
        foreach (IPspMmioDevice device in devices)
        {
            if (device is ISnapshotState)
            {
                count++;
            }
        }

        w.Write(count);
        foreach (IPspMmioDevice device in devices)
        {
            if (device is ISnapshotState state)
            {
                w.Write(device.PhysicalBase);
                w.Write(state);
            }
        }

        w.Write(Ge);
    }

    private void LoadExtra(SnapshotReader r)
    {
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            uint physicalBase = r.ReadUInt32();
            IPspMmioDevice? device = Find(physicalBase);
            if (device is ISnapshotState state && device.PhysicalBase == physicalBase)
            {
                r.ReadInto(state);
            }
            else if (r.ReadBool())
            {
                r.ReadBlock(); // a block this build does not have at that base
            }
        }

        if (Ge != null)
        {
            r.ReadInto(Ge);
        }
        else if (r.ReadBool())
        {
            r.ReadBlock();
        }
    }
}

/// <summary>Save states: the hardware's time.</summary>
[GenerateSnapshot]
public sealed partial class PspHardwareClock
{
}

/// <summary>Save states: the lines, their latches, the enable mask and the output (the callback is the session's).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("OutputChanged")]
public sealed partial class PspInterruptController
{
}

/// <summary>Save states: the registers and the fuse values (the reset callback is the session's).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("CpuResetRequested")]
public sealed partial class PspSystemControl
{
}

/// <summary>Save states: the CPU and bus clock registers.</summary>
[GenerateSnapshot]
public sealed partial class PspClockFrequency
{
}

/// <summary>Save states: the four timers (the clock and the interrupt controller are the hardware's).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_clock", "_interrupts")]
public sealed partial class PspHardwareTimers
{
}

/// <summary>Save states: the system time's count offset, the alarm and its line.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_clock", "_interrupts")]
public sealed partial class PspSystemTime
{
}

/// <summary>Save states: the channels (with a transfer in progress), the requests and the status registers.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_interrupts")]
public sealed partial class PspDmac
{
}

/// <summary>Save states: the DDR controller's registers.</summary>
[GenerateSnapshot]
public sealed partial class PspDdrController
{
}

/// <summary>Save states: the scan-out channel's registers.</summary>
[GenerateSnapshot]
public sealed partial class PspLcdc
{
}

/// <summary>Save states: the display controller's registers, its start time and the blanks raised (the blank
/// callback is the session's).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_clock", "_interrupts", "VblankStarted")]
public sealed partial class PspDisplayController
{
}
