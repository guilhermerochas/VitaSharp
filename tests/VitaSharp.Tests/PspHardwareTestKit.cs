using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Ge;
using VitaSharp.Core.Compat.Psp;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>The test RAM of <see cref="AllegrexTestBus"/> with the PSP-side register blocks in front of it; also the
/// GE's memory, word by word (no contiguous views: the tests' lists do not draw).</summary>
public sealed class PspTestBus(AllegrexTestBus memory) : IAllegrexBus, IGeMemory
{
    public AllegrexTestBus Memory => memory;

    public Span<byte> Span(uint physical, int length)
    {
        return [];
    }

    public PspHardware? Hardware { get; set; }

    public byte Read8(uint address)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        if (Hardware != null && Hardware.TryReadNarrow(physical, 1, out uint value))
        {
            return (byte)value;
        }

        return memory.Read8(physical);
    }

    public ushort Read16(uint address)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        if (Hardware != null && Hardware.TryReadNarrow(physical, 2, out uint value))
        {
            return (ushort)value;
        }

        return memory.Read16(physical);
    }

    public uint Read32(uint address)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        if (Hardware != null && Hardware.TryRead32(physical, out uint value))
        {
            return value;
        }

        return memory.Read32(physical);
    }

    public void Write8(uint address, byte value)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        if (Hardware == null || !Hardware.TryWriteNarrow(physical, 1, value))
        {
            memory.Write8(physical, value);
        }
    }

    public void Write16(uint address, ushort value)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        if (Hardware == null || !Hardware.TryWriteNarrow(physical, 2, value))
        {
            memory.Write16(physical, value);
        }
    }

    public void Write32(uint address, uint value)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        if (Hardware == null || !Hardware.TryWrite32(physical, value))
        {
            memory.Write32(physical, value);
        }
    }
}

/// <summary>
/// An Allegrex core on the test RAM with the standard PSP-side hardware, its interrupt controller wired to IP2. Time
/// is the core's own: <see cref="NsPerInstruction"/> per instruction (333 MHz), and a halted core jumps to the
/// hardware's next event, as the core's runner does. Code at PSP 0x88000000, the exception handler at 0x88001000,
/// data at 0x88010000.
/// </summary>
public sealed class PspHardwareRig
{
    public const uint CodeBase = AllegrexRig.CodeBase;
    public const uint ExceptionBase = AllegrexRig.ExceptionBase;
    public const uint DataBase = 0x88010000;

    /// <summary>One instruction at the compat clock (333 MHz), rounded.</summary>
    public const long NsPerInstruction = 3;

    /// <summary>Status.IE with IP2 unmasked.</summary>
    public const int InterruptsOn = 0x401;

    public PspHardwareRig(uint[] code, uint[]? handler = null)
    {
        Memory = new AllegrexTestBus();
        Bus = new PspTestBus(Memory);
        Hardware = PspHardware.CreateStandard(Bus);
        Bus.Hardware = Hardware;
        Memory.Load(CodeBase, code);
        if (handler != null)
        {
            Memory.Load(ExceptionBase, handler);
        }

        Core = new AllegrexCore(Bus);
        Core.Reset(CodeBase);
        Core.Status = 0;
        Core.EBase = ExceptionBase;
        Hardware.Interrupts.OutputChanged = high =>
        {
            if (high)
            {
                Core.RaiseInterrupt(AllegrexCore.InterruptControllerLine);
            }
            else
            {
                Core.ClearInterrupt(AllegrexCore.InterruptControllerLine);
            }
        };
    }

    public AllegrexTestBus Memory { get; }

    public PspTestBus Bus { get; }

    public PspHardware Hardware { get; }

    public AllegrexCore Core { get; }

    /// <summary>The core's time in ns.</summary>
    public long TimeNs { get; private set; }

    /// <summary>A word of the data area.</summary>
    public uint Data(int index)
    {
        return Memory.Read32(DataBase + (uint)(index * 4));
    }

    /// <summary>
    /// Runs for <paramref name="instructions"/> instructions of core time in slices of <paramref name="slice"/>; a
    /// halted core waits (its time passes) until the hardware's next event. Stops early when the core is halted with
    /// no event to come.
    /// </summary>
    public void Run(long instructions, int slice = 16)
    {
        long end = TimeNs + (instructions * NsPerInstruction);
        while (TimeNs < end)
        {
            int budget = (int)Math.Min(slice, (end - TimeNs + NsPerInstruction - 1) / NsPerInstruction);
            int ran = Core.Run(budget);
            TimeNs += ran * NsPerInstruction;
            Hardware.Advance(TimeNs);
            if (Core.Halted)
            {
                long next = Hardware.NextEventNs;
                if (next <= TimeNs)
                {
                    // the hardware has nothing that would wake the core now: it stays halted
                    return;
                }

                if (next == long.MaxValue || next > end)
                {
                    if (next != long.MaxValue)
                    {
                        TimeNs = end;
                        Hardware.Advance(TimeNs);
                    }

                    return;
                }

                TimeNs = Math.Max(TimeNs, next);
                Hardware.Advance(TimeNs);
            }
        }
    }

    /// <summary>lui + ori: <paramref name="register"/> = <paramref name="value"/>.</summary>
    public static uint[] LoadImmediate(int register, uint value)
    {
        return [Lui(register, (int)(value >> 16)), Ori(register, register, (int)(value & 0xFFFF))];
    }

    /// <summary>halt in a loop (the core sleeps between interrupts).</summary>
    public static uint[] IdleLoop()
    {
        return [Halt, Beq(Zero, Zero, -2), Nop];
    }
}
