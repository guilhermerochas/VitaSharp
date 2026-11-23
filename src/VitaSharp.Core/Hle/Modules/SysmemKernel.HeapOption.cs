using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// ksceKernelAllocHeapMemoryWithOption (SceSysmem 3.74) and the two physical-vector helpers ss_mgr exports for the DMA
// engines (SceSblSsMgrForDriver NID_E0B13BA7 / NID_C38D0CEA, read in the modules, study only): ss_mgr describes a
// virtual range by its physical ranges (ksceKernelVARangeToPAVector) in a pair list it allocates from the caller's
// heap, 0x20-aligned, and frees it again.
public sealed partial class SysmemKernel
{
    /// <summary>The size of SceAllocOpt that ksceKernelAllocHeapMemoryWithOption takes: 0x14.</summary>
    const uint HeapAllocOptSize = 0x14;

    /// <summary>ss_mgr: the pair list could not be allocated (0x800F000C).</summary>
    const int SSMGR_ERR_NO_MEMORY = unchecked((int)0x800F000C);

    /// <summary>ss_mgr: a vector that does not hold every range, or a free that fails (0x800F0016).</summary>
    const int SSMGR_ERR_INVALID_ARGUMENT = unchecked((int)0x800F0016);

    /// <summary>ksceKernelAllocHeapMemoryWithOption(SceUID heap, SceSize size, SceAllocOpt *opt): as
    /// ksceKernelAllocHeapMemory, with an option block (NULL, or of size 0x14: else NULL) whose +8 is the alignment
    /// (every allocation here starts a page, which any alignment up to a page meets). The block's report words
    /// (+4, +0xC, +0x10) are cleared.</summary>
    [Hle(Nid = 0x49D4DD9B, Library = "SceSysmemForDriver")]
    uint ksceKernelAllocHeapMemoryWithOption(int heap, uint size, uint option)
    {
        if (option != 0 && GuestMemory.R32(option) != HeapAllocOptSize)
        {
            return 0;
        }

        uint address = ksceKernelAllocHeapMemoryThroughEntry(heap, size);
        if (option != 0)
        {
            GuestMemory.W32(option + 4, 0);
            GuestMemory.W32(option + 0xC, 0);
            GuestMemory.W32(option + 0x10, 0);
        }

        return address;
    }

    /// <summary>NID_E0B13BA7 (SceSblSsMgrForDriver)(void *address, SceSize size, SceUID heap, SceKernelPAVector *pv):
    /// the physical ranges of a virtual range into a pair list allocated from the heap (0x800F000C when it cannot be);
    /// the vector's own errors pass through; 0x800F0016 when the list does not hold every range.</summary>
    [Hle("NID_E0B13BA7", Nid = 0xE0B13BA7, Library = "SceSblSsMgrForDriver")]
    int SsMgrBuildPaVector(uint address, uint size, int heap, uint vector)
    {
        uint range = KernelAlloc(8, "ssmgr:range");
        try
        {
            GuestMemory.W32(range, address);
            GuestMemory.W32(range + 4, size);
            GuestMemory.W32(vector, 0x14);
            GuestMemory.W32(vector + 4, 0);
            GuestMemory.W32(vector + 0x10, 0);
            int result = ksceKernelVARangeToPAVectorThroughEntry(range, vector);
            if (result < 0)
            {
                return result;
            }

            uint count = GuestMemory.R32(vector + 0xC);
            uint pairs = ksceKernelAllocHeapMemoryThroughEntry(heap, (count * 8 + 0x1F) & ~0x1Fu);
            if (pairs == 0)
            {
                return SSMGR_ERR_NO_MEMORY;
            }

            GuestMemory.W32(vector + 0x10, pairs);
            GuestMemory.W32(vector + 4, count);
            result = ksceKernelVARangeToPAVectorThroughEntry(range, vector);
            if (result < 0)
            {
                return result;
            }

            return GuestMemory.R32(vector + 8) == GuestMemory.R32(vector + 0xC) ? 0 : SSMGR_ERR_INVALID_ARGUMENT;
        }
        finally
        {
            Mem.Free(range);
        }
    }

    /// <summary>NID_C38D0CEA (SceSblSsMgrForDriver)(SceUID heap, SceKernelPAVector *pv): frees the pair list
    /// NID_E0B13BA7 allocated; 0x800F0016 when the heap refuses it.</summary>
    [Hle("NID_C38D0CEA", Nid = 0xC38D0CEA, Library = "SceSblSsMgrForDriver")]
    int SsMgrFreePaVector(int heap, uint vector)
    {
        return ksceKernelFreeHeapMemoryThroughEntry(heap, GuestMemory.R32(vector + 0x10)) == 0 ? 0 : SSMGR_ERR_INVALID_ARGUMENT;
    }
}
