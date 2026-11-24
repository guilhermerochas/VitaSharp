namespace VitaSharp.Core.Compat;

/// <summary>
/// SceCompatMailbox (FW 3.74 SceCompat: ARM physical 0xE5070000, 0x1000 bytes): the 32 doorbell lines between the
/// ARM and the compat core. Line n reaches the ARM as GIC interrupt 0x40 + n; SceCompat registers 0x44..0x46 (a
/// command the core posted in slot 0..2), 0x47..0x49 (the core's answer interrupts sceCompatWaitIntr waits for) and
/// 0x4A (the LCD controller's vsync). What SceCompat writes, all 0xFFFFFFFF: <c>+0x80 + n*4</c> acknowledges line n
/// on the ARM side (the IRQ handler after taking a command; the boot handshake for every line but 1 and 0xE before it
/// answers the core) and <c>+n*4</c> rings line n towards the core (the handler's acknowledgement of the slot, answer
/// c on line c + 7, virtual interrupt n on line n + 0x10).
/// <para>
/// The core's side sits in the PSP interrupt controller's block (PSP 0xBC300000), at 0xBC300040..0x5F. Known: the
/// PSP-side kermit driver (as Adrenaline's PSP code calls it) posts a command in the shared SRAM and rings line
/// 4 + slot by pulsing bit 4 + slot of <see cref="RingRegister"/> (0xBC300050: cleared, then set), so a bit going
/// from 0 to 1 rings its line to the ARM. Inferred until the boot trace of phase 3 (the kermit drivers are
/// encrypted): the lines pending for the core at <see cref="PspPendingRegister"/> (0xBC300040), acknowledged by
/// writing their bits to <see cref="PspAcknowledgeRegister"/> (0xBC300044), and the ARM's pending lines at
/// <see cref="ArmPendingRegister"/> (0xBC300048). Until the interrupt controller routes it, any line pending for the
/// core holds the core's interrupt controller line high.
/// </para>
/// </summary>
public sealed partial class CompatMailbox
{
    /// <summary>The doorbell lines.</summary>
    public const int Lines = 32;

    /// <summary>The ARM's GIC interrupt of line 0.</summary>
    public const int ArmFirstInterrupt = 0x40;

    /// <summary>The ARM physical address SceCompat maps the block from.</summary>
    public const uint ArmPhysicalBase = 0xE5070000;

    /// <summary>The core's registers in its physical address space (PSP 0xBC300040, in the interrupt controller's
    /// block).</summary>
    public const uint PspPhysicalBase = 0x1C300040;

    /// <summary>The core's register window in bytes (0xBC300040..0xBC30005F).</summary>
    public const uint Size = 0x20;

    /// <summary>The core's read of the lines pending for it (inferred).</summary>
    public const int PspPendingRegister = 0x00;

    /// <summary>The core's acknowledge: the lines whose bits are written stop pending for it (inferred).</summary>
    public const int PspAcknowledgeRegister = 0x04;

    /// <summary>The core's read of the lines pending for the ARM (inferred).</summary>
    public const int ArmPendingRegister = 0x08;

    /// <summary>The core's ring register (0xBC300050): a bit going from 0 to 1 rings its line to the ARM.</summary>
    public const int RingRegister = 0x10;

    private readonly object _lock = new();
    private uint _armPending;
    private uint _pspPending;
    private uint _ringBits;     // the core's last value of the ring register

    /// <summary>A line became pending for the ARM: its GIC interrupt (0x40 + line) fires. Called on the ringing
    /// side's thread (the core's), outside the mailbox lock.</summary>
    public Action<int>? ArmInterrupt { get; set; }

    /// <summary>The core's interrupt line changed: true when some line is pending for the core, false when none.</summary>
    public Action<bool>? PspInterruptLevel { get; set; }

    /// <summary>The lines pending for the ARM (bit n = line n).</summary>
    public uint ArmPending => Volatile.Read(ref _armPending);

    /// <summary>The lines pending for the core.</summary>
    public uint PspPending => Volatile.Read(ref _pspPending);

    /// <summary>SceCompat's write of <c>+0x80 + line*4</c>: line acknowledged on the ARM side.</summary>
    public void ArmAcknowledge(int line)
    {
        lock (_lock)
        {
            _armPending &= ~LineBit(line);
        }
    }

    /// <summary>SceCompat's write of <c>+line*4</c>: the line rings on the core.</summary>
    public void ArmRing(int line)
    {
        bool raised;
        lock (_lock)
        {
            raised = _pspPending == 0;
            _pspPending |= LineBit(line);
        }

        if (raised)
        {
            PspInterruptLevel?.Invoke(true);
        }
    }

    /// <summary>The core's write of <c>+line*4</c>: the line rings on the ARM (its interrupt fires once while it
    /// stays pending).</summary>
    public void PspRing(int line)
    {
        bool fire;
        lock (_lock)
        {
            uint bit = LineBit(line);
            fire = (_armPending & bit) == 0;
            _armPending |= bit;
        }

        if (fire)
        {
            ArmInterrupt?.Invoke(line);
        }
    }

    /// <summary>The core's write of <c>+0x80 + line*4</c>: line acknowledged on the core's side.</summary>
    public void PspAcknowledge(int line)
    {
        bool lowered;
        lock (_lock)
        {
            uint before = _pspPending;
            _pspPending &= ~LineBit(line);
            lowered = before != 0 && _pspPending == 0;
        }

        if (lowered)
        {
            PspInterruptLevel?.Invoke(false);
        }
    }

    /// <summary>A read of the core's registers (offset from <see cref="PspPhysicalBase"/>).</summary>
    public uint PspRead32(uint offset)
    {
        switch (offset)
        {
            case PspPendingRegister:
                return PspPending;
            case ArmPendingRegister:
                return ArmPending;
            case RingRegister:
                return Volatile.Read(ref _ringBits);
            default:
                return 0;
        }
    }

    /// <summary>A write of the core's registers: the ring register (each bit going from 0 to 1 rings its line, lowest
    /// first) and the acknowledge register (each bit written stops its line pending for the core).</summary>
    public void PspWrite32(uint offset, uint value)
    {
        if (offset == RingRegister)
        {
            uint rising;
            lock (_lock)
            {
                rising = value & ~_ringBits;
                _ringBits = value;
            }

            for (int line = 0; line < Lines && rising != 0; line++)
            {
                if ((rising & 1) != 0)
                {
                    PspRing(line);
                }

                rising >>= 1;
            }
        }
        else if (offset == PspAcknowledgeRegister)
        {
            for (int line = 0; line < Lines; line++)
            {
                if ((value & LineBit(line)) != 0)
                {
                    PspAcknowledge(line);
                }
            }
        }
    }

    /// <summary>Every line idle (a new boot of the core).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _armPending = 0;
            _pspPending = 0;
            _ringBits = 0;
        }
    }

    private static uint LineBit(int line)
    {
        return 1u << (line & (Lines - 1));
    }
}
