using VitaSharp.Core.Compat.Ge;

namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// One of the GE's two register regions on the core's bus: the GE block (PSP 0xBD400000) or its EDRAM controller
/// (0xBD500000), both served by the same <see cref="GeEngine"/> (which takes PSP physical addresses). The GE's
/// interrupt line is the PSP's interrupt 25 (<see cref="PspInterrupt.Ge"/>), wired by
/// <see cref="PspHardware.CreateStandard"/>.
/// </summary>
public sealed class PspGeDevice(GeEngine engine, uint physicalBase) : IPspMmioDevice
{
    /// <summary>The GE's accepted interrupts register.</summary>
    public const uint InterruptEnableRegister = 0x308;

    public uint PhysicalBase => physicalBase;

    public uint Size => GeEngine.BlockSize;

    /// <summary>The engine behind the region.</summary>
    public GeEngine Engine => engine;

    public uint Read32(uint offset)
    {
        return engine.Read32(physicalBase + offset);
    }

    public void Write32(uint offset, uint value)
    {
        engine.Write32(physicalBase + offset, value);
    }

    /// <summary>The GE block's power-on state: its reset (bit 0 of the reset register: no list, no interrupt status)
    /// and no interrupt accepted (+0x308), which drops its line; the EDRAM region has nothing of its own.</summary>
    public void Reset()
    {
        if (physicalBase == GeEngine.PhysicalBase)
        {
            engine.Write32(GeEngine.PhysicalBase, 1);
            engine.Write32(GeEngine.PhysicalBase + InterruptEnableRegister, 0);
        }
    }
}
