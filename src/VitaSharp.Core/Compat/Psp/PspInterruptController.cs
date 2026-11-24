namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// The PSP's interrupt controller (PSP 0xBC300000, registers 0x00..0x2C), whose output is the CPU's Cause.IP2 line
/// (<see cref="Allegrex.AllegrexCore.InterruptControllerLine"/>). 64 interrupt lines (<see cref="PspInterrupt"/>) in
/// three register groups of {masked pending, raw pending, enable}:
/// <list type="table">
/// <item><term>0x00 / 0x04 / 0x08</term><description>lines 0..31.</description></item>
/// <item><term>0x10 / 0x14 / 0x18</term><description>lines 32..63 except 38, 39, 46 and 47 (bits 6, 7, 14 and 15
/// read 0 and are not written).</description></item>
/// <item><term>0x20 / 0x24 / 0x28</term><description>lines 38, 39, 46, 47 in bits 0..3.</description></item>
/// </list>
/// Pending = what the devices drive. Most devices hold their line high until they are acknowledged on the device
/// itself (the timer's status read, the DMA controller's clear register); the controller only reflects them. A few
/// sources are latched by the controller and acknowledged by writing their bit to 0x00 (the PSP kernel does it for
/// the vblank and the ME codec, 30 and 31). The output is high while some pending line is enabled.
/// <para>
/// Sources: uOFW <c>interruptman/start.S</c> (the dispatch reads 0x00 and 0x10 | 0x20, clears bits 0..3 of the low
/// group; <c>sceKernelIsInterruptOccurred</c> reads 0x04/0x14/0x24; the enable save/restore writes 0x08 (bits 0..3
/// and 29 always set), 0x18 and 0x28 with the compact bits; 30 and 31 are acknowledged by writing 1 &lt;&lt; n to 0x00)
/// and Jpcsp <c>MMIOHandlerInterruptMan</c> (the same groups, pending = triggered &amp; enabled, the reset).
/// </para>
/// <para>
/// The mailbox of the Vita's compat core (doorbells from the ARM) also holds the core's IP2 line until its PSP
/// interrupt number is known (<see cref="SetExternalLevel"/>); the doorbell registers themselves sit after this block
/// (0xBC300040..0x5F, <see cref="CompatMailbox"/>).
/// </para>
/// </summary>
public sealed partial class PspInterruptController : IPspMmioDevice
{
    /// <summary>PSP physical 0xBC300000.</summary>
    public const uint Base = 0x1C300000;

    /// <summary>The three groups (0x00..0x2F).</summary>
    public const uint BlockSize = 0x30;

    /// <summary>Lines 32..63 present in the high group (38, 39, 46 and 47 are in the third one).</summary>
    public const uint HighGroupBits = 0xFFFF3F3F;

    private const ulong CompactLines = (3UL << 38) | (3UL << 46);

    private readonly object _lock = new();
    private ulong _levels;    // lines driven by their devices
    private ulong _latched;   // lines latched by the controller (Trigger), acknowledged by a write of 0x00
    private ulong _enabled;
    private bool _external;
    private bool _output;

    public uint PhysicalBase => Base;

    public uint Size => BlockSize;

    /// <summary>
    /// The output (IP2) changed. Called inside the controller's lock on the thread that changed it: the handler only
    /// records the level (drives the core's line), it never calls back into the controller.
    /// </summary>
    public Action<bool>? OutputChanged { get; set; }

    /// <summary>True while some pending line is enabled (or the external level is high).</summary>
    public bool Output
    {
        get
        {
            lock (_lock)
            {
                return _output;
            }
        }
    }

    /// <summary>The lines pending (driven or latched), bit n = line n.</summary>
    public ulong Pending
    {
        get
        {
            lock (_lock)
            {
                return _levels | _latched;
            }
        }
    }

    /// <summary>The enabled lines, bit n = line n.</summary>
    public ulong Enabled
    {
        get
        {
            lock (_lock)
            {
                return _enabled;
            }
        }
    }

    /// <summary>A device drives its line <paramref name="line"/> (0..63) high or low.</summary>
    public void SetLine(int line, bool high)
    {
        ulong bit = LineBit(line);
        lock (_lock)
        {
            _levels = high ? _levels | bit : _levels & ~bit;
            Evaluate();
        }
    }

    /// <summary>A latched source fires: the line stays pending until its bit is written to 0x00.</summary>
    public void Trigger(int line)
    {
        ulong bit = LineBit(line);
        lock (_lock)
        {
            _latched |= bit;
            Evaluate();
        }
    }

    /// <summary>The level of a source outside the controller that also holds IP2 (the compat mailbox's doorbells).</summary>
    public void SetExternalLevel(bool high)
    {
        lock (_lock)
        {
            _external = high;
            Evaluate();
        }
    }

    public uint Read32(uint offset)
    {
        lock (_lock)
        {
            ulong pending = _levels | _latched;
            switch (offset)
            {
                case 0x00:
                    return (uint)(pending & _enabled);
                case 0x04:
                    return (uint)pending;
                case 0x08:
                    return (uint)_enabled;
                case 0x10:
                    return (uint)((pending & _enabled) >> 32) & HighGroupBits;
                case 0x14:
                    return (uint)(pending >> 32) & HighGroupBits;
                case 0x18:
                    return (uint)(_enabled >> 32) & HighGroupBits;
                case 0x20:
                    return Compact(pending & _enabled);
                case 0x24:
                    return Compact(pending);
                case 0x28:
                    return Compact(_enabled);
                default:
                    return 0;
            }
        }
    }

    public void Write32(uint offset, uint value)
    {
        lock (_lock)
        {
            switch (offset)
            {
                case 0x00:
                    _latched &= ~(ulong)value;
                    break;
                case 0x08:
                    _enabled = (_enabled & ~0xFFFFFFFFUL) | value;
                    break;
                case 0x18:
                    _enabled = (_enabled & (0xFFFFFFFFUL | CompactLines)) | ((ulong)(value & HighGroupBits) << 32);
                    break;
                case 0x28:
                    _enabled = (_enabled & ~CompactLines) | Expand(value);
                    break;
                default:
                    // the raw pending registers are read-only; the rest of the block has no register
                    return;
            }

            Evaluate();
        }
    }

    /// <summary>Every line disabled and nothing latched (the devices' own lines are theirs to reset).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _latched = 0;
            _enabled = 0;
            Evaluate();
        }
    }

    private void Evaluate()
    {
        bool output = ((_levels | _latched) & _enabled) != 0 || _external;
        if (output != _output)
        {
            _output = output;
            OutputChanged?.Invoke(output);
        }
    }

    private static uint Compact(ulong lines)
    {
        return (uint)(((lines >> 38) & 3) | (((lines >> 46) & 3) << 2));
    }

    private static ulong Expand(uint value)
    {
        return ((ulong)(value & 3) << 38) | ((ulong)((value >> 2) & 3) << 46);
    }

    private static ulong LineBit(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, PspInterrupt.Count);
        return 1UL << line;
    }
}
