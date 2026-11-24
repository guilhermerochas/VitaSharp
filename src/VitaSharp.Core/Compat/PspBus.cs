using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Compat;

/// <summary>
/// The compat core's physical bus over the Vita's memory, as FW 3.74 SceCompat sets it up:
/// <list type="bullet">
/// <item>Main RAM, PSP physical 0x08000000..0x0C000000 (0x88000000 cached, 0xA8000000 uncached): four 16 MB banks,
/// each pointed by <c>SceGrabForDriver_E9C25A28(bank, paddr | 1)</c> at Vita memory. SceCompat points bank 0 at the
/// window, banks 1 and 2 at window + 0x1000000 (bank 2, the "hole" PSP 0x8A000000, is a mirror of bank 1 unless
/// someone points it elsewhere) and bank 3 at window + 0x3000000 (pcff, the handshake word, the top of RAM).</item>
/// <item>The shared SRAM, PSP 0xBFC00000 (<see cref="CompatSharedSram"/>).</item>
/// <item>The mailbox's core side (<see cref="CompatMailbox"/>, address inferred).</item>
/// </list>
/// Anything else is hardware of phase 3 (interrupt controller, timers, DMA, GE, KIRK): reads give 0, writes are
/// dropped, and the first accesses of each address are logged ("unknown MMIO"), which is how the boot trace finds
/// what the firmware touches.
/// </summary>
public sealed class PspBus(CompatSharedSram sram, CompatMailbox mailbox) : IAllegrexBus
{
    /// <summary>The main RAM banks: PSP physical <see cref="RamBase"/> + bank * <see cref="BankSize"/>.</summary>
    public const int Banks = 4;

    public const uint RamBase = 0x08000000;

    public const uint BankSize = 0x1000000;

    /// <summary>The different unknown addresses logged before the log goes quiet.</summary>
    public const int UnknownLogLimit = 64;

    private readonly uint[] _banks = new uint[Banks];
    private readonly HashSet<uint> _unknownLogged = [];
    private int _unknownAccesses;

    /// <summary>The unknown accesses so far (logged or not).</summary>
    public int UnknownAccesses => Volatile.Read(ref _unknownAccesses);

    /// <summary>SceGrabForDriver_E9C25A28(bank, address | 1): the Vita memory a 16 MB bank of the core's RAM is
    /// (bit 0 clear: the bank unmapped).</summary>
    public void SetBank(int bank, uint vitaAddressAndEnable)
    {
        if ((uint)bank >= Banks)
        {
            return;
        }

        uint address = (vitaAddressAndEnable & 1) != 0 ? vitaAddressAndEnable & ~1u : 0;
        Volatile.Write(ref _banks[bank], address);
    }

    /// <summary>The Vita memory of a bank (0: unmapped).</summary>
    public uint Bank(int bank)
    {
        return Volatile.Read(ref _banks[bank]);
    }

    /// <summary>Every bank unmapped (SceGrab's compat stop).</summary>
    public void UnmapBanks()
    {
        for (int i = 0; i < Banks; i++)
        {
            Volatile.Write(ref _banks[i], 0);
        }
    }

    /// <summary>The Vita address of a PSP address in main RAM (any segment), or 0 when it is not mapped RAM.</summary>
    public uint ToVita(uint pspAddress)
    {
        uint physical = pspAddress & AllegrexCore.PhysicalMask;
        uint offset = physical - RamBase;
        if (offset >= Banks * BankSize)
        {
            return 0;
        }

        uint bank = Volatile.Read(ref _banks[offset / BankSize]);
        return bank == 0 ? 0 : bank + (offset % BankSize);
    }

    public byte Read8(uint address)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        uint vita = ToVita(physical);
        if (vita != 0)
        {
            return GuestMemory.Read<byte>(vita);
        }

        if (physical - CompatSharedSram.PspPhysicalBase < CompatSharedSram.Size)
        {
            return sram.Read8((int)(physical - CompatSharedSram.PspPhysicalBase));
        }

        Unknown(physical, "read8");
        return 0;
    }

    public ushort Read16(uint address)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        uint vita = ToVita(physical);
        if (vita != 0)
        {
            return GuestMemory.Read<ushort>(vita);
        }

        if (physical - CompatSharedSram.PspPhysicalBase < CompatSharedSram.Size)
        {
            return sram.Read16((int)(physical - CompatSharedSram.PspPhysicalBase));
        }

        Unknown(physical, "read16");
        return 0;
    }

    public uint Read32(uint address)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        uint vita = ToVita(physical);
        if (vita != 0)
        {
            return GuestMemory.Read<uint>(vita);
        }

        if (physical - CompatSharedSram.PspPhysicalBase < CompatSharedSram.Size)
        {
            return sram.Read32((int)(physical - CompatSharedSram.PspPhysicalBase));
        }

        if (physical - CompatMailbox.PspPhysicalBase < CompatMailbox.Size)
        {
            return mailbox.PspRead32(physical - CompatMailbox.PspPhysicalBase);
        }

        Unknown(physical, "read32");
        return 0;
    }

    public void Write8(uint address, byte value)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        uint vita = ToVita(physical);
        if (vita != 0)
        {
            GuestMemory.Write(vita, value);
            return;
        }

        if (physical - CompatSharedSram.PspPhysicalBase < CompatSharedSram.Size)
        {
            sram.Write8((int)(physical - CompatSharedSram.PspPhysicalBase), value);
            return;
        }

        Unknown(physical, "write8");
    }

    public void Write16(uint address, ushort value)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        uint vita = ToVita(physical);
        if (vita != 0)
        {
            GuestMemory.Write(vita, value);
            return;
        }

        if (physical - CompatSharedSram.PspPhysicalBase < CompatSharedSram.Size)
        {
            sram.Write16((int)(physical - CompatSharedSram.PspPhysicalBase), value);
            return;
        }

        Unknown(physical, "write16");
    }

    public void Write32(uint address, uint value)
    {
        uint physical = address & AllegrexCore.PhysicalMask;
        uint vita = ToVita(physical);
        if (vita != 0)
        {
            GuestMemory.Write(vita, value);
            return;
        }

        if (physical - CompatSharedSram.PspPhysicalBase < CompatSharedSram.Size)
        {
            sram.Write32((int)(physical - CompatSharedSram.PspPhysicalBase), value);
            return;
        }

        if (physical - CompatMailbox.PspPhysicalBase < CompatMailbox.Size)
        {
            mailbox.PspWrite32(physical - CompatMailbox.PspPhysicalBase, value);
            return;
        }

        Unknown(physical, "write32");
    }

    private void Unknown(uint physical, string access)
    {
        Interlocked.Increment(ref _unknownAccesses);
        lock (_unknownLogged)
        {
            if (_unknownLogged.Count >= UnknownLogLimit || !_unknownLogged.Add(physical))
            {
                return;
            }
        }

        Log.Warn($"SceCompat core: unknown MMIO {access} at PSP physical 0x{physical:X8}");
    }
}
