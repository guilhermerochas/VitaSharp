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
/// The core's view of the block is not in any module the project can read (the PSP side, pcbc and the kermit
/// drivers, is encrypted): <see cref="PspPhysicalBase"/> and the core's registers below mirror the ARM side and are
/// inferred, to be fixed by the boot trace of phase 3. The core writes <c>+n*4</c> to ring line n to the ARM and
/// <c>+0x80 + n*4</c> to acknowledge line n on its side, and reads the lines pending for it at
/// <see cref="PspPendingRegister"/> (the ARM's at <see cref="ArmPendingRegister"/>). Until the PSP's interrupt
/// controller exists (phase 3), any line pending for the core holds its interrupt controller line high.
/// </para>
/// </summary>
public sealed class CompatMailbox
{
    /// <summary>The doorbell lines.</summary>
    public const int Lines = 32;

    /// <summary>The ARM's GIC interrupt of line 0.</summary>
    public const int ArmFirstInterrupt = 0x40;

    /// <summary>The ARM physical address SceCompat maps the block from.</summary>
    public const uint ArmPhysicalBase = 0xE5070000;

    /// <summary>The block in the core's physical address space (inferred, see the class).</summary>
    public const uint PspPhysicalBase = 0x1C500000;

    /// <summary>The block's size in bytes.</summary>
    public const uint Size = 0x1000;

    /// <summary>The acknowledge registers (+0x80 + line * 4) on both sides.</summary>
    public const int AcknowledgeRegisters = 0x80;

    /// <summary>The core's read of the lines pending for it (inferred).</summary>
    public const int PspPendingRegister = 0x100;

    /// <summary>The core's read of the lines pending for the ARM (inferred).</summary>
    public const int ArmPendingRegister = 0x104;

    private readonly object _lock = new();
    private uint _armPending;
    private uint _pspPending;

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

    /// <summary>A read of the core's view (offset in the block).</summary>
    public uint PspRead32(uint offset)
    {
        switch (offset)
        {
            case PspPendingRegister:
                return PspPending;
            case ArmPendingRegister:
                return ArmPending;
            default:
                return 0;
        }
    }

    /// <summary>A write of the core's view (offset in the block): the ring and acknowledge registers.</summary>
    public void PspWrite32(uint offset, uint value)
    {
        if (offset < AcknowledgeRegisters)
        {
            PspRing((int)(offset >> 2));
        }
        else if (offset < AcknowledgeRegisters * 2)
        {
            PspAcknowledge((int)((offset - AcknowledgeRegisters) >> 2));
        }
    }

    /// <summary>Every line idle (a new boot of the core).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _armPending = 0;
            _pspPending = 0;
        }
    }

    private static uint LineBit(int line)
    {
        return 1u << (line & (Lines - 1));
    }
}
