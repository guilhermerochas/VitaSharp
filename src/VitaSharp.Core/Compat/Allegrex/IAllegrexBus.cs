namespace VitaSharp.Core.Compat.Allegrex;

/// <summary>
/// The physical bus the Allegrex (the PSP-compatible MIPS core of the Vita SoC) sees. Addresses are PSP physical
/// addresses: the core removes the segment bits (kuseg, kseg0 cached and kseg1 uncached mirrors, the 0x4 uncached user
/// mirror) with <c>&amp; 0x1FFFFFFF</c> before calling, so main RAM (the SceCompat window, PSP 0x88000000) arrives at
/// 0x08000000 and the shared SRAM (PSP 0xBFC00000) at 0x1FC00000; the bus masks again on its side. 16- and 32-bit
/// accesses are always naturally aligned (the core raises the address error exception for the others).
/// </summary>
public interface IAllegrexBus
{
    byte Read8(uint address);

    ushort Read16(uint address);

    uint Read32(uint address);

    void Write8(uint address, byte value);

    void Write16(uint address, ushort value);

    void Write32(uint address, uint value);
}
