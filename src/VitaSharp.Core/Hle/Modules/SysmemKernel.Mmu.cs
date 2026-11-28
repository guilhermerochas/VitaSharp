using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The address translation entries of SceCpuForKernel (FW 3.74 SceSysmem): ksceKernelMMUVAtoPA,
/// ksceKernelMMUVAtoPAWithMode and ksceKernelMMUGetMemoryType.
/// <para>
/// How the firmware does it (disassembled module, study only): with the domain access register set to "client" for
/// every domain, it asks the MMU to translate the address for each access kind the mode names, in this order:
/// privileged read (mode bit 0x01, ATS1CPR), privileged write (0x02, ATS1CPW), user read (0x10, ATS1CUR), user write
/// (0x20, ATS1CUW). The first kind that translates gives the physical address (page base | offset) and the result is
/// 1 for a 4 KB page (2 for a 16 MB supersection); when none does, the physical address is cleared and the result is
/// 0x80022007. The memory type tries the four kinds and decodes the inner and outer cache attributes of the first
/// translation: 0x80 non-cacheable, 0x40 inner write-back, 0xD0 write-back inner and outer, 2 strongly ordered,
/// 8 device; no translation is 0x80022007.
/// </para>
/// <para>
/// In VitaSharp the guest address space is the physical one (there is no separate physical map), so a translated
/// address is its own physical address, mapped in pages. What translates, and for which access, comes from the memory
/// block holding the address: the low byte of its type carries the kernel's access (bits 0..2: execute, write, read)
/// and user code's (bits 4..6), bits 12..15 its cache attributes (0xD cached, 0x8 non-cacheable, 0x4 inner cached
/// only). Mapped memory that is not in a block (module segments) is normal cached memory, read and written by the
/// kernel and, when the kernel does not own it, by user code.
/// </para>
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>What the translation entries return when the address does not translate for any asked access.</summary>
    internal const int MmuNoTranslation = unchecked((int)0x80022007);

    /// <summary>The mode ksceKernelMMUVAtoPA asks: any read or write, privileged or user.</summary>
    internal const uint MmuAnyAccess = 0x33;

    /// <summary>A physical vector whose size word is not 0x14 (SCE_KERNEL_ERROR_INVALID_ARGUMENT_SIZE).</summary>
    const int MmuBadVectorSize = unchecked((int)0x80020009);

    /// <summary>A translated 4 KB page.</summary>
    const int MmuPage = 1;

    const uint MmuPrivilegedRead = 0x01;
    const uint MmuPrivilegedWrite = 0x02;
    const uint MmuUserRead = 0x10;
    const uint MmuUserWrite = 0x20;

    /// <summary>The memory types ksceKernelMMUGetMemoryType decodes.</summary>
    internal const int MmuTypeWriteBack = 0xD0;
    internal const int MmuTypeNonCacheable = 0x80;
    internal const int MmuTypeInnerWriteBack = 0x40;

    /// <summary>The accesses (MmuPrivilegedRead...) the MMU allows at <paramref name="address"/>, 0 when it does not
    /// translate.</summary>
    uint MmuAccess(uint address, out uint blockType) => MmuAccess(ProcessScope.CurrentPid, address, out blockType);

    /// <summary>The accesses the MMU allows at <paramref name="address"/> in process <paramref name="pid"/>'s address
    /// space.</summary>
    uint MmuAccess(int pid, uint address, out uint blockType)
    {
        blockType = 0;
        if (!KernelSpace.IsMappedRange(address, 1))
        {
            return 0;
        }

        if (BlockHolding(pid, address, 1) is { } block)
        {
            if (block.MappingRemoved)
            {
                return 0; // ksceKernelUnmapMemBlock: the pages no longer translate
            }

            blockType = block.Type;
            uint kernel = block.Type & 0x7;
            uint user = (block.Type >> 4) & 0x7;
            uint access = 0;
            if (((kernel | user) & 0x4) != 0)
            {
                access |= MmuPrivilegedRead;
            }

            if (((kernel | user) & 0x2) != 0)
            {
                access |= MmuPrivilegedWrite;
            }

            if ((user & 0x4) != 0)
            {
                access |= MmuUserRead;
            }

            if ((user & 0x2) != 0)
            {
                access |= MmuUserWrite;
            }

            return access;
        }

        if (KernelSpace.IsKernelOwned(address))
        {
            return MmuPrivilegedRead | MmuPrivilegedWrite;
        }

        return MmuPrivilegedRead | MmuPrivilegedWrite | MmuUserRead | MmuUserWrite;
    }

    /// <summary>The physical address of a translating <paramref name="address"/> in process <paramref name="pid"/>'s
    /// space: its own, or in a mirror block its source's memory (<see cref="PhysicalOf"/>).</summary>
    uint PhysicalAt(int pid, uint address) => BlockHolding(pid, address, 1) is { } block ? PhysicalOf(block, address) : address;

    /// <summary>The firmware's translation (FUN_810232d4): the first asked access that translates.</summary>
    int MmuTranslate(uint mode, uint address, uint physicalOut, string function) =>
        MmuTranslate(ProcessScope.CurrentPid, mode, address, physicalOut, function);

    int MmuTranslate(int pid, uint mode, uint address, uint physicalOut, string function)
    {
        if (physicalOut == 0)
        {
            throw DataAbort(function, 0);
        }

        uint allowed = MmuAccess(pid, address, out _);
        if ((mode & allowed & (MmuPrivilegedRead | MmuPrivilegedWrite | MmuUserRead | MmuUserWrite)) == 0)
        {
            GuestMemory.W32(physicalOut, 0);
            return MmuNoTranslation;
        }

        GuestMemory.W32(physicalOut, PhysicalAt(pid, address));
        return MmuPage;
    }

    /// <summary>ksceKernelMMUVAtoPA(void *va, uintptr_t *pa): the physical address for any read or write (mode 0x33);
    /// 1 when the address translates (a page), else *pa = 0 and 0x80022007.</summary>
    [Hle(Nid = 0x14E0B0C4, Library = "SceCpuForKernel")]
    int ksceKernelMMUVAtoPA(uint address, uint physicalOut) =>
        MmuTranslate(MmuAnyAccess, address, physicalOut, nameof(ksceKernelMMUVAtoPA));

    /// <summary>ksceKernelMMUVAtoPAWithMode(int mode, void *va, uintptr_t *pa): the same for the accesses of
    /// <paramref name="mode"/> (0x01 privileged read, 0x02 privileged write, 0x10 user read, 0x20 user write; a mode
    /// with none of them never translates).</summary>
    [Hle(Nid = 0x1BD23326, Library = "SceCpuForKernel")]
    int ksceKernelMMUVAtoPAWithMode(uint mode, uint address, uint physicalOut) =>
        MmuTranslate(mode, address, physicalOut, nameof(ksceKernelMMUVAtoPAWithMode));

    /// <summary>ksceKernelMMUGetMemoryType(void *va): the cache type of the memory at <paramref name="address"/>
    /// (0xD0 write-back, 0x80 non-cacheable, 0x40 inner write-back), 0x80022007 when it does not translate.</summary>
    [Hle(Nid = 0x87F7631D, Library = "SceCpuForKernel")]
    int ksceKernelMMUGetMemoryType(uint address)
    {
        if (MmuAccess(address, out uint blockType) == 0)
        {
            return MmuNoTranslation;
        }

        return ((blockType >> 12) & 0xF) switch
        {
            0x8 => MmuTypeNonCacheable,
            0x4 => MmuTypeInnerWriteBack,
            _ => MmuTypeWriteBack,
        };
    }

    // ------------------------------------------------------------------ SceSysmemForDriver on top of the MMU

    /// <summary>ksceKernelVAtoPA(void *va, uintptr_t *pa): ksceKernelMMUVAtoPA after a NULL check (0x80020005).</summary>
    [Hle(Nid = 0x8D160E65, Library = "SceSysmemForDriver")]
    internal int ksceKernelVAtoPA(uint address, uint physicalOut)
    {
        if (physicalOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return MmuTranslate(MmuAnyAccess, address, physicalOut, nameof(ksceKernelVAtoPA));
    }

    /// <summary>ksceKernelProcModeVAtoPA(SceUID pid, int mode, void *va, uintptr_t *pa): the translation in process
    /// <paramref name="pid"/>'s address space (the kernel's for <see cref="KernelSpace.KernelPid"/>).</summary>
    [Hle(Nid = 0x61A67D32, Library = "SceSysmemForDriver")]
    int ksceKernelProcModeVAtoPA(int pid, uint mode, uint address, uint physicalOut)
    {
        if (physicalOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (pid != KernelSpace.KernelPid && ProcessOf(pid) == null)
        {
            return SceError.INVALID_PID;
        }

        return MmuTranslate(pid == 0 ? ProcessScope.CurrentPid : pid, mode, address, physicalOut, nameof(ksceKernelProcModeVAtoPA));
    }

    /// <summary>The page walk of the range functions: every page of [address, address + size) must translate
    /// (0x80022007 otherwise); the physical pages are contiguous, so the range is one physical range.</summary>
    internal int MmuWalk(uint address, uint size)
    {
        while (size != 0)
        {
            if ((MmuAccess(address, out _) & MmuAnyAccess) == 0)
            {
                return MmuNoTranslation;
            }

            uint step = Math.Min(size, (~address & 0xFFF) + 1);
            size -= step;
            address += step;
        }

        return 0;
    }

    /// <summary>ksceKernelVARangeToPARange(const SceKernelAddrPair *va, SceKernelAddrPair *pa): the physical range of a
    /// virtual one (address, size); both NULL-checked (0x80020005). A range of size 0 writes nothing.</summary>
    [Hle(Nid = 0xAE36C775, Library = "SceSysmemForDriver")]
    int ksceKernelVARangeToPARange(uint virtualRange, uint physicalRange)
    {
        if (virtualRange == 0 || physicalRange == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint address = GuestMemory.R32(virtualRange);
        uint size = GuestMemory.R32(virtualRange + 4);
        if (size == 0)
        {
            return 0;
        }

        if ((MmuAccess(address, out _) & MmuAnyAccess) == 0)
        {
            return MmuNoTranslation;
        }

        // the first page sets the output (its address and the whole size), the later ones must follow it
        GuestMemory.W32(physicalRange, PhysicalAt(ProcessScope.CurrentPid, address));
        GuestMemory.W32(physicalRange + 4, size);
        return MmuWalk(address, size);
    }

    /// <summary>
    /// ksceKernelVARangeToPAVector(const SceKernelAddrPair *va, SceKernelPAVector *pv): the physical ranges of a
    /// virtual range. pv is 0x14 bytes (size 0x14, else 0x80020009; capacity, written count, total count, the pair
    /// array, which a nonzero capacity needs, else 0x80020005). The physical pages are contiguous: one range.
    /// </summary>
    [Hle(Nid = 0xE68BEEBD, Library = "SceSysmemForDriver")]
    int ksceKernelVARangeToPAVector(uint virtualRange, uint vector)
    {
        if (virtualRange == 0 || vector == 0)
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

        uint address = GuestMemory.R32(virtualRange);
        uint size = GuestMemory.R32(virtualRange + 4);
        GuestMemory.W32(vector + 8, 0);
        GuestMemory.W32(vector + 0xC, 0);
        uint ranges = 0;
        if (size != 0)
        {
            // the first page starts the range (counted at once), the later ones must translate too
            if ((MmuAccess(address, out _) & MmuAnyAccess) == 0)
            {
                return MmuNoTranslation;
            }

            ranges = 1;
            GuestMemory.W32(vector + 0xC, ranges);
            int walked = MmuWalk(address, size);
            if (walked < 0)
            {
                return walked;
            }
        }

        if (capacity != 0)
        {
            GuestMemory.W32(pairs, size == 0 ? 0 : PhysicalAt(ProcessScope.CurrentPid, address));
            GuestMemory.W32(pairs + 4, size);
        }

        GuestMemory.W32(vector + 8, Math.Min(ranges, capacity));
        return 0;
    }
}
