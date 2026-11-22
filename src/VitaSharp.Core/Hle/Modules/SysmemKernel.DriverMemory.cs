using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The rest of SceSysmemForDriver, as the FW 3.74 sysmem.skprx has it (decompiled, study only): the memory block
/// type table and the queries on it, block information and physical ranges, attribute changes of a block's pages,
/// remapping a block to a type the firmware allows, user mappings into the kernel, range locks, the uncached heap, the
/// remaining user copies of another process, the *BySW/*ByHW translations, heap checks and the PUID calls by name.
/// <para>
/// VitaSharp's memory is one flat map shared by every address space: a virtual address is its physical one, pages
/// keep no read-only or locked state, and a process handle (PUID) is the object's own UID. The calls check their
/// arguments and the objects as the firmware does and answer from that model.
/// </para>
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>The firmware's memory block type table (FW 3.74 sysmem): each type, the attribute word the block
    /// keeps for it (its memory type in bits 8..15, its access in the low bits) and its flags (bit 5: only with the
    /// debug switch 0xD2).</summary>
    static readonly (uint Type, uint Info, uint Flags)[] MemBlockTypes =
    [
        (0x0320D060, 0x0AB0D060, 0x03100001), (0x0320D040, 0x0AB0D040, 0x03100001), (0x0320D050, 0x0AB0D050, 0x03100001),
        (0x0390D040, 0x0A90D040, 0x03100001), (0x0390D050, 0x0A90D050, 0x03100001), (0x0390D060, 0x0A90D060, 0x03100001),
        (0x03A0D040, 0x0AA0D040, 0x03100001), (0x03A0D050, 0x0AA0D050, 0x03100001), (0x03A0D060, 0x0AA0D060, 0x03100001),
        (0x03F0D040, 0x0AF0D040, 0x02500021), (0x03F0D050, 0x0AF0D050, 0x02500021), (0x03F0D060, 0x0AF0D060, 0x02500021),
        (0x0720D006, 0x07B0D006, 0x03100001), (0x08F0D060, 0x08F0D060, 0x03100001), (0x08F0D050, 0x08F0D050, 0x03100001),
        (0x08F0D040, 0x08F0D040, 0x03100001), (0x08F0D006, 0x08F0D006, 0x03100000), (0x09408060, 0x09C08060, 0x09100081),
        (0x09408040, 0x09C08040, 0x09100081), (0x09208060, 0x09A08060, 0x09100081), (0x09208040, 0x09A08040, 0x09100081),
        (0x09F08060, 0x09F08060, 0x09100081), (0x09F08040, 0x09F08040, 0x09100081), (0x0B100860, 0x0B100860, 0x0B100081),
        (0x0B100840, 0x0B100840, 0x0B100081), (0x0B100060, 0x0B100060, 0x0B100081), (0x0B100040, 0x0B100040, 0x0B100081),
        (0x0B10D060, 0x0B10D060, 0x0B100081), (0x0B10D040, 0x0B10D040, 0x0B100081), (0x0C20D060, 0x0CB0D060, 0x03100001),
        (0x0C20D040, 0x0CB0D040, 0x03100001), (0x0C20D050, 0x0CB0D050, 0x03100001), (0x0C208060, 0x0CB08060, 0x03100001),
        (0x0C208040, 0x0CB08040, 0x03100001), (0x0C50D060, 0x0C50D060, 0x02500001), (0x0C50D040, 0x0C50D040, 0x02500001),
        (0x0C50D050, 0x0C50D050, 0x02500001), (0x0C80D060, 0x0C80D060, 0x0C800000), (0x0C80D040, 0x0C80D040, 0x0C800000),
        (0x0CA0D060, 0x0CA0D060, 0x02500001), (0x0CA0D040, 0x0CA0D040, 0x02500001), (0x0CA08060, 0x0CA08060, 0x02500001),
        (0x0CA08040, 0x0CA08040, 0x02500001), (0x0CF0D060, 0x0CF0D060, 0x02500001), (0x0CF0D040, 0x0CF0D040, 0x02500001),
        (0x0D808060, 0x0D808060, 0x0D800000), (0x0D808040, 0x0D808040, 0x0D800000), (0x0E20D060, 0x0EB0D060, 0x03100001),
        (0x0E20D040, 0x0EB0D040, 0x03100001), (0x0E208060, 0x0EB08060, 0x03100001), (0x0E208040, 0x0EB08040, 0x03100001),
        (0x1020D006, 0x1060D006, 0x02201000), (0x1020D004, 0x1060D004, 0x23001000), (0x1020D005, 0x1060D005, 0x03001000),
        (0x10208006, 0x30608006, 0x03001000), (0x10208004, 0x30608004, 0x03001000), (0x10200206, 0x10600206, 0x03001000),
        (0x10200806, 0x10600806, 0x03001000), (0x10C0D006, 0x10C0D006, 0x0C800000), (0x10C0D004, 0x10C0D004, 0x0C800000),
        (0x10C08006, 0x10C08006, 0x0C800000), (0x10C08004, 0x10C08004, 0x0C800000), (0x10F0D006, 0x14F0D006, 0x02420020),
        (0x10F0D005, 0x14F0D005, 0x22420020), (0x10F0D004, 0x14F0D004, 0x02420020), (0x10F08006, 0x14F08006, 0x02420020),
        (0x10F08004, 0x14F08004, 0x02420020), (0x1050D006, 0x1050D006, 0x02500000), (0x1050D004, 0x1050D004, 0x02500000),
        (0x1080D006, 0x1080D006, 0x0D800000), (0x1080D004, 0x1080D004, 0x0D800000), (0x10208026, 0x30608026, 0x03001000),
        (0x10208024, 0x30608024, 0x03001000), (0x10208066, 0x30608066, 0x03001000), (0x10208064, 0x30608064, 0x03001000),
        (0x1020D026, 0x1060D026, 0x03001000), (0x1020D024, 0x1060D024, 0x03001000), (0x1020D066, 0x1060D066, 0x03001000),
        (0x1020D064, 0x1060D064, 0x03001000), (0x1320D006, 0x1320D006, 0x02500000), (0x1320D004, 0x1320D004, 0x02500000),
        (0x6020D004, 0x6020D004, 0x00000000), (0x6020D006, 0x6020D006, 0x00000000), (0x60208014, 0x60208014, 0x00000000),
        (0x60208016, 0x60208016, 0x00000000), (0x60208004, 0x60208004, 0x00000000), (0x60208006, 0x60208006, 0x00000000),
        (0x6050D006, 0x6050D006, 0x00000000), (0xC050D006, 0xC050D006, 0x00000000), (0xC0F0D006, 0xC0F0D006, 0x00000000),
        (0x1220D006, 0x1220D006, 0x00000000),
    ];

    /// <summary>The remaps ksceKernelRemapMemBlock allows (from the block's attribute word to the new type's): the
    /// kernel's read-write-execute text to read-execute or read-only, the debug text (with the switch 0xD2) to
    /// read-execute, and a user read-write block to the same type.</summary>
    static readonly (uint From, uint To, bool Debug)[] MemBlockRemaps =
    [
        (0x1060D006, 0x1060D005, false), (0x1060D006, 0x1060D004, false), (0x14F0D006, 0x14F0D005, true),
        (0x0CB0D060, 0x0CB0D060, false),
    ];

    const int InvalidMemBlockType = unchecked((int)0x80024B01);
    const int RemapNotAllowed = unchecked((int)0x80024B02);
    const int NoSuchMemBlock = unchecked((int)0x80024701);
    const int InvalidHeap = unchecked((int)0x80024901);
    const int HeapChunkNotFound = unchecked((int)0x80024904);
    const int PhysicalRangeOutside = unchecked((int)0x80024004);
    const int NoMemoryRegion = unchecked((int)0x80024300);
    const int InvalidPuid = unchecked((int)0x80024501);
    const int DebugSwitchAllTypes = 0xD2;

    /// <summary>The blocks ksceKernelUserMap made (UID -> the user range they map).</summary>
    [SnapshotMissing] // states of builds before it: no user range had been mapped
    readonly Dictionary<int, uint> _userMaps = new();

    bool DebugSwitch(uint bit)
    {
        return Emu.Hle.Modules.OfType<SystemServicesVsh>().FirstOrDefault() is { } services
            && ((services.DipswWord(bit >> 5) >> (int)(bit & 31)) & 1) != 0;
    }

    /// <summary>The table entry of a type (the debug-only ones only with the switch 0xD2), or null.</summary>
    (uint Type, uint Info, uint Flags)? MemBlockTypeEntry(uint type)
    {
        foreach ((uint Type, uint Info, uint Flags) entry in MemBlockTypes)
        {
            if (entry.Type == type)
            {
                if ((entry.Flags & 0x20) != 0 && !DebugSwitch(DebugSwitchAllTypes))
                {
                    return null;
                }

                return entry;
            }
        }

        return null;
    }

    /// <summary>The attribute word of a type (0 when the table has no such type).</summary>
    uint MemBlockTypeInfo(uint type) => MemBlockTypeEntry(type)?.Info ?? 0;

    // ------------------------------------------------------------------ the type table

    /// <summary>ksceKernelMemBlockType2Memtype (0x20C811FA)(type): the memory type (bits 8..15 of the type's attribute
    /// word); an unknown type 0x80024B01.</summary>
    [Hle(Nid = 0x20C811FA, Library = "SceSysmemForDriver")]
    int ksceKernelMemBlockType2Memtype(uint type)
    {
        if (MemBlockTypeEntry(type) is not { } entry)
        {
            return InvalidMemBlockType;
        }

        return (int)(entry.Info & 0xFF00);
    }

    /// <summary>ksceKernelMemBlockTypeGetPrivileges (0x6A0792A3)(type): the access of the type's attribute word as
    /// privilege bits: kernel execute 1, write 2, read 4, user execute 0x10, write 0x20, read 0x40 (0 for an unknown
    /// type).</summary>
    [Hle(Nid = 0x6A0792A3, Library = "SceSysmemForDriver", ReturnsValue = true)]
    uint ksceKernelMemBlockTypeGetPrivileges(uint type)
    {
        uint info = MemBlockTypeInfo(type);
        uint privileges = (info & 7) >> 2;
        if ((info & 2) != 0)
        {
            privileges |= 2;
        }

        if ((info & 1) != 0)
        {
            privileges |= 4;
        }

        if ((info & 0x40) != 0)
        {
            privileges |= 0x10;
        }

        if ((info & 0x20) != 0)
        {
            privileges |= 0x20;
        }

        if ((info & 0x10) != 0)
        {
            privileges |= 0x40;
        }

        return privileges;
    }

    /// <summary>NID_CB0F3A33(type): the physical memory kind of the type (bits 20..23 of its attribute word): the main
    /// memory kinds 0x5, 0x6, 0x8, 0x9, 0xA, 0xB, 0xF give 0x200000, 0x4 and 0xC give 0x400000, anything else its own
    /// bits (0 for an unknown type).</summary>
    [Hle(Nid = 0xCB0F3A33, Library = "SceSysmemForDriver", ReturnsValue = true)]
    uint NID_CB0F3A33(uint type)
    {
        uint kind = MemBlockTypeInfo(type) & 0xF00000;
        return kind switch
        {
            0x400000 or 0xC00000 => 0x400000,
            0x500000 or 0x600000 or 0x800000 or 0x900000 or 0xA00000 or 0xB00000 or 0xF00000 => 0x200000,
            _ => kind,
        };
    }

    /// <summary>ksceKernelGetMemBlockMemtypeByAddr (0xF3BBE2E1)(addr): the memory type of the block holding the
    /// address in the caller's address space; none 0x80024A03.</summary>
    [Hle(Nid = 0xF3BBE2E1, Library = "SceSysmemForDriver")]
    int ksceKernelGetMemBlockMemtypeByAddr(uint address)
    {
        if (BlockHolding(ProcessScope.CurrentPid, address, 1) is not { } block)
        {
            return NoPartitionForRange;
        }

        return (int)(MemBlockTypeInfo(block.Type) & 0xFF00);
    }

    // ------------------------------------------------------------------ block information and physical ranges

    /// <summary>ksceKernelGetMemBlockAllocMapSize (0x78337B62)(uid, SceSize *size): the block's mapped size; NULL
    /// 0x80020005, no block 0x80024701.</summary>
    [Hle(Nid = 0x78337B62, Library = "SceSysmemForDriver")]
    int ksceKernelGetMemBlockAllocMapSize(int uid, uint sizeOut)
    {
        if (sizeOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return NoSuchMemBlock;
        }

        GuestMemory.W32(sizeOut, block.Size);
        return 0;
    }

    /// <summary>
    /// Fills SceKernelMemBlockInfoEx (0xB8 bytes; +0 its size, else 0x80020009; NULL 0x80020005): +4 the type, +8 the
    /// UID, +0xC its name, +0x10 the mapped base, +0x14 and +0x18 the size, +0x2C the partition (none: 0), +0x30/+0x34
    /// the physical range count (1), +0x38 the first physical address and +0x78 its size.
    /// </summary>
    int MemBlockInfoInto(int uid, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0xB8)
        {
            return MmuBadVectorSize;
        }

        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        GuestMemory.Span(info + 4, 0xB4).Clear();
        GuestMemory.W32(info + 0x04, block.Type);
        GuestMemory.W32(info + 0x08, (uint)block.Uid);
        GuestMemory.W32(info + 0x0C, NamePointerOf(block));
        GuestMemory.W32(info + 0x10, block.Address);
        GuestMemory.W32(info + 0x14, block.Size);
        GuestMemory.W32(info + 0x18, block.Size);
        GuestMemory.W32(info + 0x28, 0xFFFFFFFF);
        GuestMemory.W32(info + 0x30, 1);
        GuestMemory.W32(info + 0x34, 1);
        GuestMemory.W32(info + 0x38, block.Address);
        GuestMemory.W32(info + 0x78, block.Size);
        return 0;
    }

    /// <summary>ksceKernelMemBlockGetInfoEx (0x24A99FFF)(uid, SceKernelMemBlockInfoEx *info): see
    /// <see cref="MemBlockInfoInto"/>.</summary>
    [Hle(Nid = 0x24A99FFF, Library = "SceSysmemForDriver")]
    int ksceKernelMemBlockGetInfoEx(int uid, uint info) => MemBlockInfoInto(uid, info);

    /// <summary>ksceKernelGetMemBlockInfo (0xA73CFFEF)(uid, level, SceKernelMemBlockInfoEx *info): the same at a
    /// reference level below 8 (0x80020005 otherwise).</summary>
    [Hle(Nid = 0xA73CFFEF, Library = "SceSysmemForDriver")]
    int ksceKernelGetMemBlockInfo(int uid, uint level, uint info)
    {
        if (level >= 8)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return MemBlockInfoInto(uid, info);
    }

    /// <summary>ksceKernelGetMemBlockPARange (0x98C15666)(uid, SceKernelAddrPair *pa): the block's
    /// physical range (one: the memory is contiguous); NULL 0x80020005.</summary>
    [Hle(Nid = 0x98C15666, Library = "SceSysmemForDriver")]
    int ksceKernelGetMemBlockPARange(int uid, uint physicalRange)
    {
        if (physicalRange == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        GuestMemory.W32(physicalRange, block.Address);
        GuestMemory.W32(physicalRange + 4, block.Size);
        return 0;
    }

    /// <summary>NID_64DBE472(uid, SceKernelAddrPair *pa): the block's single physical range (a block of several
    /// ranges 0x80024003; the memory here is contiguous).</summary>
    [Hle(Nid = 0x64DBE472, Library = "SceSysmemForDriver")]
    int NID_64DBE472(int uid, uint physicalRange) => ksceKernelGetMemBlockPARange(uid, physicalRange);

    /// <summary>Writes the block's physical range into a SceKernelPAVector (0x14 bytes: size, capacity, written,
    /// total, pairs; a nonzero capacity needs the pairs).</summary>
    int MemBlockVectorInto(int uid, uint vector)
    {
        if (vector == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(vector) != 0x14)
        {
            return MmuBadVectorSize;
        }

        uint capacity = GuestMemory.R32(vector + 4);
        uint pairs = GuestMemory.R32(vector + 0x10);
        if (capacity != 0 && pairs == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.W32(vector + 8, 0);
        GuestMemory.W32(vector + 0xC, 0);
        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        if (capacity != 0)
        {
            GuestMemory.W32(pairs, block.Address);
            GuestMemory.W32(pairs + 4, block.Size);
        }

        GuestMemory.W32(vector + 0xC, 1);
        GuestMemory.W32(vector + 8, Math.Min(1u, capacity));
        return 0;
    }

    /// <summary>ksceKernelGetMemBlockPAVector (0x19A51AC7)(uid, SceKernelPAVector *pv).</summary>
    [Hle(Nid = 0x19A51AC7, Library = "SceSysmemForDriver")]
    int ksceKernelGetMemBlockPAVector(int uid, uint vector) => MemBlockVectorInto(uid, vector);

    /// <summary>NID_987EE587(uid, SceKernelPAVector *pv): the same vector, its written count the smaller of the
    /// capacity and the total.</summary>
    [Hle(Nid = 0x987EE587, Library = "SceSysmemForDriver")]
    int NID_987EE587(int uid, uint vector) => MemBlockVectorInto(uid, vector);

    /// <summary>NID_1EFC96EA(uid): the id of the address space the block is in (its owner process; the kernel's
    /// for a kernel block).</summary>
    [Hle(Nid = 0x1EFC96EA, Library = "SceSysmemForDriver")]
    int NID_1EFC96EA(int uid)
    {
        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        return block.OwnerPid == 0 ? KernelSpace.KernelPid : block.OwnerPid;
    }

    /// <summary>ksceKernelGetPhysicalMemoryType (0x0AAA4FDD)(va): the kind of physical memory behind the address: 5
    /// CDRAM (0x20000000..), 1 main memory (0x40000000..), 3 (0x80000000..), 7 otherwise; an address that does not
    /// translate gives the translation's error.</summary>
    [Hle(Nid = 0x0AAA4FDD, Library = "SceSysmemForDriver")]
    int ksceKernelGetPhysicalMemoryType(uint address)
    {
        if ((MmuAccess(address, out _) & MmuAnyAccess) == 0)
        {
            return MmuNoTranslation;
        }

        uint physical = address;
        if (physical - 0x20000000u < 0x10000000u)
        {
            return 5;
        }

        if (physical - 0x40000000u < 0x40000000u)
        {
            return 1;
        }

        return physical - 0x80000000u < 0x40000000u ? 3 : 7;
    }

    /// <summary>The physical range NID_A7C0D1FC checks (0x80000000, 512 MB; with the debug switch 0xD5 0x78000000,
    /// 128 MB).</summary>
    (uint Base, uint Size) CheckedPhysicalRange => DebugSwitch(0xD5) ? (0x78000000u, 0x08000000u) : (0x80000000u, 0x20000000u);

    /// <summary>NID_C9928F5E(uint32 *base, SceSize *size): the range of <see cref="CheckedPhysicalRange"/>; 0.</summary>
    [Hle(Nid = 0xC9928F5E, Library = "SceSysmemForDriver")]
    int NID_C9928F5E(uint baseOut, uint sizeOut)
    {
        (uint rangeBase, uint rangeSize) = CheckedPhysicalRange;
        GuestMemory.W32(baseOut, rangeBase);
        GuestMemory.W32(sizeOut, rangeSize);
        return 0;
    }

    /// <summary>NID_A7C0D1FC(va): 1 when the address translates into <see cref="CheckedPhysicalRange"/>, 0x80024004
    /// when it translates elsewhere, the translation's error otherwise.</summary>
    [Hle(Nid = 0xA7C0D1FC, Library = "SceSysmemForDriver")]
    int NID_A7C0D1FC(uint address)
    {
        if ((MmuAccess(address, out _) & MmuAnyAccess) == 0)
        {
            return MmuNoTranslation;
        }

        (uint rangeBase, uint rangeSize) = CheckedPhysicalRange;
        return address >= rangeBase && address - rangeBase < rangeSize ? 1 : PhysicalRangeOutside;
    }

    /// <summary>NID_89475192(kind, info): a physical region (info: +0 its size, 0xC, else 0x80020005; +4 base, +8
    /// size): kind 1 the first chunk of the kernel's physical partition (ScePhyMemPartKD), kinds 8 and 9 the regions
    /// of the boot's sysmem context (+0x90, +0x94: {page, size}); a kind without its region 0x80024300.</summary>
    [Hle(Nid = 0x89475192, Library = "SceSysmemForDriver")]
    int NID_89475192(uint kind, uint info)
    {
        if (info == 0 || GuestMemory.R32(info) != 0xC)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (kind is 8 or 9)
        {
            uint region = GuestMemory.R32(SysmemContext() + (kind == 8 ? 0x90u : 0x94u));
            if (region == 0)
            {
                return NoMemoryRegion;
            }

            GuestMemory.W32(info + 4, GuestMemory.R32(region) << 12);
            GuestMemory.W32(info + 8, GuestMemory.R32(region + 4));
            return 0;
        }

        uint partition = kind == 1 ? GuestMemory.R32(SysmemContext() + SysmemContextKernelPartition) : 0;
        uint chunk = partition == 0 ? 0 : GuestMemory.R32(partition + 0x7C);
        if (chunk == 0)
        {
            return NoMemoryRegion;
        }

        GuestMemory.W32(info + 4, GuestMemory.R32(chunk + 4));
        GuestMemory.W32(info + 8, GuestMemory.R32(chunk + 8));
        return 0;
    }

    // ------------------------------------------------------------------ attributes of a block's pages, maps, remaps

    /// <summary>The page attribute change of [addr, addr + size) inside block <paramref name="uid"/> (NID_13805CA8 and
    /// its siblings): the block must allow it (attribute 0x2000), the range be page-aligned and inside it (0x80020005
    /// otherwise). VitaSharp's pages keep no attributes, so the change is accepted as is.</summary>
    int ChangeBlockPages(int uid, uint address, uint size)
    {
        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        bool aligned = ((address | size) & (GuestMemory.PageSize - 1)) == 0;
        bool inside = address >= block.Address && (ulong)address + size <= (ulong)block.Address + block.Size;
        return aligned && inside ? 0 : SceError.INVALID_ARGUMENT;
    }

    /// <summary>NID_16713BE8(uid, addr, size, flags): flags 0 or 2 (0x80020005 otherwise); see
    /// <see cref="ChangeBlockPages"/>.</summary>
    [Hle(Nid = 0x16713BE8, Library = "SceSysmemForDriver")]
    int NID_16713BE8(int uid, uint address, uint size, uint flags) => (flags & ~2u) == 0 ? ChangeBlockPages(uid, address, size) : SceError.INVALID_ARGUMENT;

    /// <summary>NID_8C43B052(uid, addr, size, flags): flags with low 6 bits 0 or 1.</summary>
    [Hle(Nid = 0x8C43B052, Library = "SceSysmemForDriver")]
    int NID_8C43B052(int uid, uint address, uint size, uint flags) => (flags & 0x3F) < 2 ? ChangeBlockPages(uid, address, size) : SceError.INVALID_ARGUMENT;

    /// <summary>NID_13805CA8(uid, addr, size, flags): flags with low 6 bits 0, 1 or 8.</summary>
    [Hle(Nid = 0x13805CA8, Library = "SceSysmemForDriver")]
    int NID_13805CA8(int uid, uint address, uint size, uint flags)
    {
        uint low = flags & 0x3F;
        return low < 9 && ((1u << (int)low) & 0x103) != 0 ? ChangeBlockPages(uid, address, size) : SceError.INVALID_ARGUMENT;
    }

    /// <summary>NID_6C76AD89(uid, addr, size, flags): flags with low 6 bits 0 or 4.</summary>
    [Hle(Nid = 0x6C76AD89, Library = "SceSysmemForDriver")]
    int NID_6C76AD89(int uid, uint address, uint size, uint flags) => (flags & 0x3F) is 0 or 4 ? ChangeBlockPages(uid, address, size) : SceError.INVALID_ARGUMENT;

    /// <summary>NID_4C584B29(uid, addr, size, mode): mode 0x1000, 0x1010, 0x2000 or 0x2020.</summary>
    [Hle(Nid = 0x4C584B29, Library = "SceSysmemForDriver")]
    int NID_4C584B29(int uid, uint address, uint size, uint mode) => mode is 0x1000 or 0x1010 or 0x2000 or 0x2020 ? ChangeBlockPages(uid, address, size) : SceError.INVALID_ARGUMENT;

    /// <summary>ksceKernelMapMemBlockWithFlag (0x04059C4B)(uid, flag): maps the block (flag 0 or 1, 0x80020005
    /// otherwise) as ksceKernelMapMemBlock.</summary>
    [Hle(Nid = 0x04059C4B, Library = "SceSysmemForDriver")]
    int ksceKernelMapMemBlockWithFlag(int uid, uint flag)
    {
        if (K.Objects.Get<MemBlock>(uid) is null)
        {
            return SceError.INVALID_UID;
        }

        return (flag & ~1u) == 0 ? ksceKernelMapBlockUserVisible(uid) : SceError.INVALID_ARGUMENT;
    }

    /// <summary>ksceKernelUnmapMemBlock (0xFFCD9B60)(uid): the block is no longer reachable from user code.</summary>
    [Hle(Nid = 0xFFCD9B60, Library = "SceSysmemForDriver")]
    int ksceKernelUnmapMemBlock(int uid)
    {
        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        lock (_userVisibleBlocks)
        {
            _userVisibleBlocks.Remove(block.Address);
        }

        return 0;
    }

    /// <summary>ksceKernelRemapMemBlock (0xDFE2C8CB)(uid, type): gives the block a new type when the firmware allows
    /// that remap (<see cref="MemBlockRemaps"/>); an unknown type 0x80024B01, another remap 0x80024B02.</summary>
    [Hle(Nid = 0xDFE2C8CB, Library = "SceSysmemForDriver")]
    int ksceKernelRemapMemBlock(int uid, uint type)
    {
        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        if (MemBlockTypeEntry(type) is not { } target)
        {
            return InvalidMemBlockType;
        }

        uint from = MemBlockTypeInfo(block.Type);
        foreach ((uint From, uint To, bool Debug) remap in MemBlockRemaps)
        {
            if (remap.From == from && remap.To == target.Info && (!remap.Debug || DebugSwitch(DebugSwitchAllTypes)))
            {
                block.Type = type;
                return 0;
            }
        }

        return RemapNotAllowed;
    }

    /// <summary>ksceKernelAllocMemBlockWithInfo (0xD44F464D)(name, type, size, info): ksceKernelAllocMemBlock with
    /// the allocation info (0x30 or 0x58 bytes, else 0x80020005; the same fields as the kernel's alloc option).</summary>
    [Hle(Nid = 0xD44F464D, Library = "SceSysmemForDriver")]
    int ksceKernelAllocMemBlockWithInfo(uint name, uint type, uint size, uint info)
    {
        if (info != 0 && GuestMemory.R32(info) is not (0x30 or 0x58))
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (MemBlockTypeEntry(type) is null)
        {
            return InvalidMemBlockType;
        }

        return ksceKernelAllocMemBlock(name, type, size, info);
    }

    // ------------------------------------------------------------------ user mappings into the kernel

    /// <summary>
    /// The user range mapping of ksceKernelUserMap and its siblings: <paramref name="flags"/> may only hold 0x1 and
    /// 0x10, the buffer may not be kernel memory and the three outputs must be given (0x80020005 otherwise); a range
    /// the process cannot reach 0x80020001. The mapping is a kernel block over the buffer's pages (type 0x6020D004,
    /// or 0x6020D006 for a writable one): *page its first page, *size the pages' size, *offset where the buffer
    /// starts in the first page; returns the block's UID (VitaSharp's kernel sees user memory where it is).
    /// </summary>
    int UserMap(int pid, uint name, uint permission, uint flags, uint buffer, uint size, uint pageOut, uint sizeOut, uint offsetOut)
    {
        if ((flags & ~0x11u) != 0 || buffer - 0x80000000u < 0x1000000u || pageOut == 0 || sizeOut == 0 || offsetOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        Emulator? process = pid == 0 ? null : ProcessOf(pid);
        if (pid != 0 && process == null)
        {
            return SceError.INVALID_PID;
        }

        bool reachable = process == null ? IsUserAccessible(buffer, size) : InProcess(process, () => IsUserAccessible(buffer, size));
        if (!reachable)
        {
            return SceError.ERROR;
        }

        uint page = buffer & ~(GuestMemory.PageSize - 1);
        uint pages = GuestMemory.AlignUp(buffer + size, GuestMemory.PageSize) - page;
        string blockName = name != 0 ? GuestMemory.ReadCString(name, 32) : "SceKernelUserMap";
        int uid;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uid = K.Objects.Register(new MemBlock { Name = blockName, Address = page, Size = pages, Type = permission == 1 ? 0x6020D004u : 0x6020D006u });
        }

        lock (_userMaps)
        {
            _userMaps[uid] = buffer;
        }

        GuestMemory.W32(pageOut, page);
        GuestMemory.W32(sizeOut, pages);
        GuestMemory.W32(offsetOut, buffer & (GuestMemory.PageSize - 1));
        return uid;
    }

    /// <summary>ksceKernelUserMap (0x278BC201)(name, permission, buffer, size, *page, *size, *offset): see
    /// <see cref="UserMap"/>, in the caller's address space.</summary>
    [Hle(Nid = 0x278BC201, Library = "SceSysmemForDriver")]
    void ksceKernelUserMap(HleContext context)
    {
        context.Return(UserMap(0, context.Gpr(0), context.Gpr(1), 0, context.Gpr(2), context.Gpr(3), context.Gpr(4), context.Gpr(5), context.Gpr(6)));
    }

    /// <summary>ksceKernelUserMapWithFlags (0x7D4F8B5F)(name, permission, flags, buffer, size, *page, *size, *offset).</summary>
    [Hle(Nid = 0x7D4F8B5F, Library = "SceSysmemForDriver")]
    void ksceKernelUserMapWithFlags(HleContext context)
    {
        context.Return(UserMap(0, context.Gpr(0), context.Gpr(1), context.Gpr(2), context.Gpr(3), context.Gpr(4), context.Gpr(5), context.Gpr(6), context.Gpr(7)));
    }

    /// <summary>ksceKernelProcUserMap (0x0091D74D)(pid, name, permission, flags, buffer, size, *page, *size, *offset):
    /// in process <paramref name="context"/>'s r0.</summary>
    [Hle(Nid = 0x0091D74D, Library = "SceSysmemForDriver")]
    void ksceKernelProcUserMap(HleContext context)
    {
        context.Return(UserMap((int)context.Gpr(0), context.Gpr(1), context.Gpr(2), context.Gpr(3), context.Gpr(4), context.Gpr(5), context.Gpr(6), context.Gpr(7), context.Gpr(8)));
    }

    /// <summary>ksceKernelUserUnmap (0x00575B00)(uid): removes a mapping made by ksceKernelUserMap.</summary>
    [Hle(Nid = 0x00575B00, Library = "SceSysmemForDriver")]
    int ksceKernelUserUnmap(int uid)
    {
        lock (_userMaps)
        {
            if (!_userMaps.Remove(uid))
            {
                return K.Objects.Get<MemBlock>(uid) is null ? SceError.INVALID_UID : SceError.INVALID_ARGUMENT;
            }
        }

        K.Objects.Remove(uid);
        return 0;
    }

    // ------------------------------------------------------------------ range locks

    /// <summary>A range lock: the range must translate (pages stay where they are here, so nothing else is kept).</summary>
    int LockRangeIn(int pid, uint address, uint size)
    {
        if (pid != 0 && pid != KernelSpace.KernelPid)
        {
            if (ProcessOf(pid) is not { } process)
            {
                return SceError.INVALID_PID;
            }

            return InProcess(process, () => MmuWalk(address, size));
        }

        return MmuWalk(address, size);
    }

    /// <summary>ksceKernelLockRange (0x59A4402F)(addr, size): 1 for a range of main memory (0x40000000..0x60000000),
    /// which never moves; otherwise locks the range (0, or the translation's error).</summary>
    [Hle(Nid = 0x59A4402F, Library = "SceSysmemForDriver")]
    int ksceKernelLockRange(uint address, uint size)
    {
        if (address >= 0x40000000 && (ulong)address + size - 0x40000000 <= 0x20000000)
        {
            return 1;
        }

        return LockRangeIn(0, address, size);
    }

    /// <summary>ksceKernelUnlockRange (0x75C70DE0)(addr, size).</summary>
    [Hle(Nid = 0x75C70DE0, Library = "SceSysmemForDriver")]
    int ksceKernelUnlockRange(uint address, uint size) => LockRangeIn(0, address, size);

    /// <summary>ksceKernelLockRangeWithMode (0xBC0A1D60)(mode, addr, size).</summary>
    [Hle(Nid = 0xBC0A1D60, Library = "SceSysmemForDriver")]
    int ksceKernelLockRangeWithMode(uint mode, uint address, uint size) => LockRangeIn(0, address, size);

    /// <summary>ksceKernelUnlockRangeWithMode (0x22CBE925)(mode, addr, size).</summary>
    [Hle(Nid = 0x22CBE925, Library = "SceSysmemForDriver")]
    int ksceKernelUnlockRangeWithMode(uint mode, uint address, uint size) => LockRangeIn(0, address, size);

    /// <summary>ksceKernelLockRangeProc (0x659586BF)(pid, addr, size).</summary>
    [Hle(Nid = 0x659586BF, Library = "SceSysmemForDriver")]
    int ksceKernelLockRangeProc(int pid, uint address, uint size) => LockRangeIn(pid, address, size);

    /// <summary>ksceKernelUnlockRangeProc (0xA8525B06)(pid, addr, size).</summary>
    [Hle(Nid = 0xA8525B06, Library = "SceSysmemForDriver")]
    int ksceKernelUnlockRangeProc(int pid, uint address, uint size) => LockRangeIn(pid, address, size);

    /// <summary>ksceKernelIsEqualAccessibleRangeProcBySW (0xF4AD89D8)(pid, mode, addr, size): 0 when every page of
    /// the range allows the access in process <paramref name="pid"/>; size 0 0x80020005, a page that does not
    /// 0x80022007.</summary>
    [Hle(Nid = 0xF4AD89D8, Library = "SceSysmemForDriver")]
    int ksceKernelIsEqualAccessibleRangeProcBySW(int pid, uint mode, uint address, uint size)
    {
        if (size == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ksceKernelIsAccessibleRangeProc(pid, mode, address, size);
    }

    // ------------------------------------------------------------------ translations by software / hardware

    /// <summary>ksceKernelVAtoPABySW (0x65419BD3)(va, uintptr_t *pa): ksceKernelVAtoPA by the page tables.</summary>
    [Hle(Nid = 0x65419BD3, Library = "SceSysmemForDriver")]
    int ksceKernelVAtoPABySW(uint address, uint physicalOut) => ksceKernelVAtoPA(address, physicalOut);

    /// <summary>ksceKernelVARangeToPARangeBySW (0x32257A24)(va range, pa range).</summary>
    [Hle(Nid = 0x32257A24, Library = "SceSysmemForDriver")]
    int ksceKernelVARangeToPARangeBySW(uint virtualRange, uint physicalRange) => ksceKernelVARangeToPARange(virtualRange, physicalRange);

    /// <summary>ksceKernelVARangeToPARangeByHW (0xB3575090)(va range, pa range).</summary>
    [Hle(Nid = 0xB3575090, Library = "SceSysmemForDriver")]
    int ksceKernelVARangeToPARangeByHW(uint virtualRange, uint physicalRange) => ksceKernelVARangeToPARange(virtualRange, physicalRange);

    /// <summary>ksceKernelVARangeToPAVectorBySW (0x08A8A7E8)(va range, SceKernelPAVector *pv).</summary>
    [Hle(Nid = 0x08A8A7E8, Library = "SceSysmemForDriver")]
    int ksceKernelVARangeToPAVectorBySW(uint virtualRange, uint vector) => ksceKernelVARangeToPAVector(virtualRange, vector);

    /// <summary>ksceKernelVARangeToPAVectorByHW (0x16844CE6)(va range, SceKernelPAVector *pv).</summary>
    [Hle(Nid = 0x16844CE6, Library = "SceSysmemForDriver")]
    int ksceKernelVARangeToPAVectorByHW(uint virtualRange, uint vector) => ksceKernelVARangeToPAVector(virtualRange, vector);

    /// <summary>ksceKernelProcessSwitchContext (0x2D711589)(const SceKernelProcessContext *ctx, SceKernelProcessContext
    /// *previous): saves the current context into <paramref name="previous"/> (when given) and switches. VitaSharp's
    /// address spaces are host mappings chosen by the running thread, so the saved context is the current record
    /// (zeroed) and the switch keeps the thread where it is; 0.</summary>
    [Hle(Nid = 0x2D711589, Library = "SceSysmemForDriver")]
    int ksceKernelProcessSwitchContext(uint context, uint previous)
    {
        if (previous != 0)
        {
            GuestMemory.Span(previous, 12).Clear();
        }

        return 0;
    }

    // ------------------------------------------------------------------ heaps

    /// <summary>ksceKernelAllocHeapMemoryWithOpt (0xB415B5A8)(heap, size, opt): ksceKernelAllocHeapMemoryWithOption.</summary>
    [Hle(Nid = 0xB415B5A8, Library = "SceSysmemForDriver", ReturnsValue = true)]
    uint ksceKernelAllocHeapMemoryWithOpt(int heap, uint size, uint option) => ksceKernelAllocHeapMemoryWithOption(heap, size, option);

    /// <summary>ksceKernelAllocUncacheHeapMemory (0x7750CEA7)(size): a block of sysmem's uncached heap (the kernel's
    /// general heap here: no cache to keep apart).</summary>
    [Hle(Nid = 0x7750CEA7, Library = "SceSysmemForDriver", ReturnsValue = true)]
    uint ksceKernelAllocUncacheHeapMemory(uint size) => ksceKernelAlloc(size);

    /// <summary>ksceKernelAllocUncacheHeapMemoryWithOption (0x0B4ED16A)(size, SceAllocOpt *opt): the same with the
    /// option block of ksceKernelAllocHeapMemoryWithOption (NULL or 0x14 bytes, else NULL).</summary>
    [Hle(Nid = 0x0B4ED16A, Library = "SceSysmemForDriver", ReturnsValue = true)]
    uint ksceKernelAllocUncacheHeapMemoryWithOption(uint size, uint option)
    {
        if (option != 0 && GuestMemory.R32(option) != HeapAllocOptSize)
        {
            return 0;
        }

        return ksceKernelAlloc(size);
    }

    /// <summary>ksceKernelFreeUncacheHeapMemory (0xFB817A59)(ptr): gives an uncached heap block back; 0.</summary>
    [Hle(Nid = 0xFB817A59, Library = "SceSysmemForDriver")]
    int ksceKernelFreeUncacheHeapMemory(uint pointer) => ksceKernelFree(pointer);

    /// <summary>NID_856FA2E3(heap): gives the heap's unused memory back to the system (0x80024901 for a UID that is
    /// not a heap). Every allocation here is its own block, so there is nothing to give back; 0.</summary>
    [Hle(Nid = 0x856FA2E3, Library = "SceSysmemForDriver")]
    int NID_856FA2E3(int heap) => HeapOf(heap) == null ? InvalidHeap : 0;

    /// <summary>NID_C50A9C0D(heap, ptr): checks the heap: the number of blocks in use, or 0x80024904 when
    /// <paramref name="ptr"/> (nonzero) is not one of them; 0x80024901 for a UID that is not a heap.</summary>
    [Hle(Nid = 0xC50A9C0D, Library = "SceSysmemForDriver")]
    int NID_C50A9C0D(int heap, uint pointer)
    {
        if (HeapOf(heap) == null)
        {
            return InvalidHeap;
        }

        int used = 0;
        bool found = false;
        lock (_heapAllocations)
        {
            foreach (KeyValuePair<uint, int> allocation in _heapAllocations)
            {
                if (allocation.Value == heap)
                {
                    used++;
                    found |= allocation.Key == pointer;
                }
            }
        }

        return pointer != 0 && !found ? HeapChunkNotFound : used;
    }

    // ------------------------------------------------------------------ user copies

    /// <summary>ksceKernelCopyFromToUserProc (0x8E086C33)(pid, dst, src, len): ksceKernelCopyFromToUser in process
    /// <paramref name="pid"/>'s address space.</summary>
    [Hle(Nid = 0x8E086C33, Library = "SceSysmemForDriver")]
    int ksceKernelCopyFromToUserProc(int pid, uint destination, uint source, uint length)
    {
        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        return InProcess(process, () => ksceKernelCopyFromToUser(destination, source, length));
    }

    /// <summary>ksceKernelStrncpyToUserProc (0xFED82F2D)(pid, dst, src, len): ksceKernelStrncpyToUser in process
    /// <paramref name="pid"/>'s address space.</summary>
    [Hle(Nid = 0xFED82F2D, Library = "SceSysmemForDriver")]
    int ksceKernelStrncpyToUserProc(int pid, uint destination, uint source, uint length)
    {
        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        return InProcess(process, () => ksceKernelStrncpyToUser(destination, source, length));
    }

    /// <summary>ksceKernelStrnlenFromUserProc (0x9929EB07)(pid, s, max): ksceKernelStrnlenFromUser in process
    /// <paramref name="pid"/>'s address space.</summary>
    [Hle(Nid = 0x9929EB07, Library = "SceSysmemForDriver")]
    int ksceKernelStrnlenFromUserProc(int pid, uint text, uint max)
    {
        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        return InProcess(process, () => ksceKernelStrnlenFromUser(text, max));
    }

    /// <summary>How many bytes from <paramref name="pointer"/>, <paramref name="width"/> at a time, are units equal
    /// to <paramref name="value"/> (stops at the first other one); a unit user code cannot read ends it with the fault
    /// error.</summary>
    int CountFillValue(uint pointer, ulong value, uint length, uint width)
    {
        uint counted = 0;
        while (counted < length)
        {
            if (!IsUserAccessible(pointer + counted, width))
            {
                return KernelSpace.UserCopyFault;
            }

            ulong unit = width == 8 ? GuestMemory.Read<ulong>(pointer + counted) : GuestMemory.R32(pointer + counted);
            if (unit != value)
            {
                break;
            }

            counted += width;
        }

        return (int)counted;
    }

    /// <summary>ksceKernelCountFillValueFromUser (0xBDA6E42B)(ptr, value, len): in the caller's address space.</summary>
    [Hle(Nid = 0xBDA6E42B, Library = "SceSysmemForDriver")]
    int ksceKernelCountFillValueFromUser(uint pointer, uint value, uint length) => CountFillValue(pointer, value, length, 4);

    /// <summary>ksceKernelCountFillValue64FromUser (0xBB3B02C2)(ptr, uint64 value, len): 8 bytes at a time (value in
    /// r2:r3, len on the stack).</summary>
    [Hle(Nid = 0xBB3B02C2, Library = "SceSysmemForDriver")]
    void ksceKernelCountFillValue64FromUser(HleContext context)
    {
        ulong value = context.Gpr(2) | ((ulong)context.Gpr(3) << 32);
        context.Return(CountFillValue(context.Gpr(0), value, context.Gpr(4), 8));
    }

    /// <summary>ksceKernelCountFillValue64FromUserProc (0xE83855FD)(pid, ptr, uint64 value, len): in process pid.</summary>
    [Hle(Nid = 0xE83855FD, Library = "SceSysmemForDriver")]
    void ksceKernelCountFillValue64FromUserProc(HleContext context)
    {
        uint pointer = context.Gpr(1);
        ulong value = context.Gpr(2) | ((ulong)context.Gpr(3) << 32);
        uint length = context.Gpr(4);
        if (ProcessOf((int)context.Gpr(0)) is not { } process)
        {
            context.Return(SceError.INVALID_PID);
            return;
        }

        context.Return(InProcess(process, () => CountFillValue(pointer, value, length, 8)));
    }

    // ------------------------------------------------------------------ objects

    /// <summary>ksceGUIDCreate (0x89A44858)(SceClass *cls, name, attr, SceObjectBase **obj): a new object of the
    /// class (NULL name or output 0x80020005), as ksceGUIDKernelCreateWithOpt.</summary>
    [Hle(Nid = 0x89A44858, Library = "SceSysmemForDriver")]
    int ksceGUIDCreate(uint cls, uint name, uint attr, uint objOut)
    {
        if (name == 0 || objOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ksceGUIDKernelCreateWithOpt(cls, name, 0, objOut);
    }

    /// <summary>ksceGUIDReferObjectWithLevel (0xF6DB54BA)(guid, level, SceObjectBase **obj): a GUID (positive and odd,
    /// else 0x80024501) referred at a level below 8 (0x80020005 otherwise).</summary>
    [Hle(Nid = 0xF6DB54BA, Library = "SceSysmemForDriver")]
    int ksceGUIDReferObjectWithLevel(int guid, uint level, uint objOut)
    {
        if (!IsGuidForm(guid))
        {
            return InvalidPuid;
        }

        if (level >= 8)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ksceGUIDReferObject(guid, objOut);
    }

    /// <summary>The PUID checks of the by-name calls: not the kernel's pid (0x80020005), a handle of the UID form
    /// (0x80024501).</summary>
    static int PuidCheck(int pid, int puid)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return IsGuidForm(puid) ? 0 : InvalidPuid;
    }

    /// <summary>kscePUIDGetName (0x09896EB7)(pid, puid, char **name): the object's name; NULL 0x80020005.</summary>
    [Hle(Nid = 0x09896EB7, Library = "SceSysmemForDriver")]
    int kscePUIDGetName(int pid, int puid, uint nameOut)
    {
        int check = PuidCheck(pid, puid);
        if (check != 0)
        {
            return check;
        }

        return nameOut == 0 ? SceError.INVALID_ARGUMENT : ksceGUIDGetName(puid, nameOut);
    }

    /// <summary>kscePUIDSetName (0x12624884)(pid, puid, name): renames the object; NULL 0x80020005.</summary>
    [Hle(Nid = 0x12624884, Library = "SceSysmemForDriver")]
    int kscePUIDSetName(int pid, int puid, uint name)
    {
        int check = PuidCheck(pid, puid);
        if (check != 0)
        {
            return check;
        }

        return name == 0 ? SceError.INVALID_ARGUMENT : ksceGUIDSetName(puid, name);
    }

    /// <summary>kscePUIDOpenByName (0x513B9DDD)(pid, name): a handle of the process for the object with that name
    /// (the kernel's pid or a NULL name 0x80020005).</summary>
    [Hle(Nid = 0x513B9DDD, Library = "SceSysmemForDriver")]
    int kscePUIDOpenByName(int pid, uint name) => kscePUIDOpenByNameWithClass(pid, name, 0);

    /// <summary>kscePUIDOpenByNameWithClass (0x8DA0BCA5)(pid, name, SceClass *cls): the same for an object of the
    /// class (0x80024509 for another).</summary>
    [Hle(Nid = 0x8DA0BCA5, Library = "SceSysmemForDriver")]
    int kscePUIDOpenByNameWithClass(int pid, uint name, uint cls)
    {
        if (pid == KernelSpace.KernelPid || name == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int guid = ksceGUIDOpenByName(name);
        if (guid < 0)
        {
            return guid;
        }

        if (cls != 0 && NID_368F1C36(guid) != cls)
        {
            return unchecked((int)0x80024509);
        }

        return kscePUIDOpenByGUID(pid, guid);
    }

    /// <summary>kscePUIDtoGUIDWithClass (0x184172B1)(pid, puid, SceClass *cls, SceObjectBase **obj): the object behind
    /// the handle when it is of the class (0x80024509 otherwise), its body into *obj; the kernel's pid 0x80020005.</summary>
    [Hle(Nid = 0x184172B1, Library = "SceSysmemForDriver")]
    int kscePUIDtoGUIDWithClass(int pid, int puid, uint cls, uint objOut)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int guid = kscePUIDtoGUID(pid, puid);
        if (guid < 0)
        {
            return guid;
        }

        if (cls != 0 && NID_368F1C36(guid) != cls)
        {
            return unchecked((int)0x80024509);
        }

        if (objOut != 0)
        {
            GuestMemory.W32(objOut, NID_FA6C098C(guid));
        }

        return guid;
    }
}
