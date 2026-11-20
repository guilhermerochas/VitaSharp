using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSysmemForKernel entries the other kernel modules import, as the FW 3.74 sysmem.skprx has them (decompiled, study
/// only): object lookups that answer 0 instead of an error, the kernel's general heap (ksceKernelAlloc/Free), and the
/// copies into the user domain the module manager makes (the firmware opens the user domains for the copy; the text
/// variant also cleans the data cache over the copied range so the new code is what runs).
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>The blocks ksceKernelAlloc handed out (address -> size), for ksceKernelFree.</summary>
    [SnapshotMissing] // states of builds before it: none had been handed out
    readonly Dictionary<uint, uint> _kernelAllocations = new();

    /// <summary>NID_FA6C098C(guid): the object's body (ksceGUIDGetObject), 0 when there is none.</summary>
    [Hle(Nid = 0xFA6C098C, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_FA6C098C(int guid)
    {
        if (!IsGuidForm(guid) || K.Objects.Get<KernelObject>(guid) is not { } kernelObject)
        {
            return 0;
        }

        return BodyOf(kernelObject);
    }

    /// <summary>NID_368F1C36(guid): the object's class (ksceGUIDGetClass), 0 when there is none.</summary>
    [Hle(Nid = 0x368F1C36, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_368F1C36(int guid)
    {
        if (!IsGuidForm(guid) || K.Objects.Get<KernelObject>(guid) is not { } kernelObject)
        {
            return 0;
        }

        return ClassOf(kernelObject);
    }

    /// <summary>ksceKernelAlloc(size): a block of the kernel's general heap (NULL for 0 bytes or when memory is
    /// exhausted).</summary>
    [Hle(Nid = 0x85571907, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint ksceKernelAlloc(uint size)
    {
        if (size == 0)
        {
            return 0;
        }

        uint address;
        try
        {
            address = KernelAlloc(size, "kernel:alloc", 8);
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }

        lock (_kernelAllocations)
        {
            _kernelAllocations[address] = size;
        }

        return address;
    }

    /// <summary>ksceKernelFree(ptr): gives a ksceKernelAlloc block back; 0 (NULL and a pointer the heap did not hand out
    /// are not checked by the firmware either).</summary>
    [Hle(Nid = 0x4233C16D, Library = "SceSysmemForKernel")]
    int ksceKernelFree(uint pointer)
    {
        bool owned;
        lock (_kernelAllocations)
        {
            owned = _kernelAllocations.Remove(pointer);
        }

        if (owned)
        {
            Mem.Free(pointer);
        }

        return 0;
    }

    /// <summary>ksceKernelCopyToUserDomain(dst, src, len): copies into user memory; 0.</summary>
    [Hle(Nid = 0x499A9FFF, Library = "SceSysmemForKernel")]
    int ksceKernelCopyToUserDomain(uint destination, uint source, uint length)
    {
        GuestMemory.Copy(destination, source, length);
        return 0;
    }

    /// <summary>ksceKernelCopyToUserTextDomain(dst, src, len): copies code into user memory and makes it the code that
    /// runs there (the firmware cleans the data cache over the range); 0.</summary>
    [Hle(Nid = 0x5EF1DAAF, Library = "SceSysmemForKernel")]
    int ksceKernelCopyToUserTextDomain(uint destination, uint source, uint length)
    {
        GuestMemory.Copy(destination, source, length);
        Emu.Code.Invalidate(destination, length);
        return 0;
    }
}
