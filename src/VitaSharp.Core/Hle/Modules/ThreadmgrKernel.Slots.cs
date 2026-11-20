using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The six handler slots (2-7) of the thread manager's data block (+0x2260, 0x14 bytes each: in use, busy, and the
/// three words of the registration), as FW 3.74 threadmgr.skprx has them (decompiled, study only): NID_B645C7EF takes
/// the first free slot and answers its number (0x80028081 when all six are taken), NID_8C3C1F78 frees one (0x80028082
/// for a slot that is not taken or out of 2-7, 0x80028083 while it is busy). The firmware's scheduler calls the
/// registered handlers; VitaSharp's thread manager is not that scheduler (see <see cref="Globals"/>), so they are kept
/// as registered.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    const uint GlobalsSlots = 0x2260, SlotSize = 0x14;

    const int NoFreeSlot = unchecked((int)0x80028081), SlotNotTaken = unchecked((int)0x80028082),
        SlotBusy = unchecked((int)0x80028083);

    /// <summary>NID_B645C7EF(a, b, c): takes the first free slot (2-7) for the registration and answers its number.</summary>
    [Hle(Nid = 0xB645C7EF, Library = "SceThreadmgrForDriver")]
    int NID_B645C7EF(uint first, uint second, uint third)
    {
        uint globals = Globals();
        lock (_globalsLock)
        {
            for (uint slot = 2; slot < 8; slot++)
            {
                uint entry = globals + GlobalsSlots + slot * SlotSize + 4;
                if (GuestMemory.R32(entry) == 0)
                {
                    GuestMemory.W32(entry + 0x0C, second);
                    GuestMemory.W32(entry + 0x10, third);
                    GuestMemory.W32(entry, 1);
                    GuestMemory.W32(entry + 4, 0);
                    GuestMemory.W32(entry + 8, first);
                    return (int)slot;
                }
            }
        }

        return NoFreeSlot;
    }

    /// <summary>NID_8C3C1F78(slot): frees a taken slot that is not busy.</summary>
    [Hle(Nid = 0x8C3C1F78, Library = "SceThreadmgrForDriver")]
    int NID_8C3C1F78(int slot)
    {
        if ((uint)(slot - 2) >= 6)
        {
            return SlotNotTaken;
        }

        uint entry = Globals() + GlobalsSlots + (uint)slot * SlotSize + 4;
        lock (_globalsLock)
        {
            if (GuestMemory.R32(entry) == 0)
            {
                return SlotNotTaken;
            }

            if (GuestMemory.R32(entry + 4) != 0)
            {
                return SlotBusy;
            }

            GuestMemory.W32(entry, 0);
            return 0;
        }
    }
}
