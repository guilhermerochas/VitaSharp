using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

public sealed class MemBlock : KernelObject
{
    public uint Address, Size, Type;
    /// <summary>Allocated by sceKernelAllocMemBlockForVM.</summary>
    public bool IsVm;
}

/// <summary>SceSysmem: memory blocks.</summary>
public sealed class SysMem : HleModule
{
    public const uint TypeUserRw = 0x0C20D060, TypeUserRwUncache = 0x0C208060, TypeUserCdramRw = 0x09408060,
        TypeUserMainPhyContRw = 0x0C80D060, TypeUserMainPhyContNcRw = 0x0D808060, TypeUserMainCdialogRw = 0x0CA0D060,
        TypeUserMainCdialogNcRw = 0x0CA08060, TypeUserRx = 0x0C20D050;

    /// <summary>SceKernelAllocMemBlockOpt attr: the block may be opened by other processes (access flags at +0x14).</summary>
    const uint ShareAttr = 0x4000;
    const long UserBudget =365L * 1024 * 1024, CdramBudget = 112L * 1024 * 1024, PhyContBudget = 26L * 1024 * 1024;

    [Hle]
    int sceKernelAllocMemBlock(string name, uint type, uint size, uint opt)
    {
        if (size == 0) return SceError.ILLEGAL_SIZE;
        bool cdram = type == TypeUserCdramRw;
        uint align = cdram ? 0x40000u : (type == TypeUserMainPhyContRw || type == TypeUserMainPhyContNcRw) ? 0x100000u : 0x1000u;
        bool shareable = false;
        if (opt != 0)
        {
            uint optSize = GuestMemory.R32(opt), attr = GuestMemory.R32(opt + 4);
            if ((attr & 4) != 0 && optSize >= 12) align = Math.Max(align, GuestMemory.R32(opt + 8)); // ALIGNMENT attr
            // attr 0x4000 with the access other processes get at +0x14 (0x10 read, 0x30 read/write: what their
            // sceKernelOpenMemBlock asks for): a block meant to be opened by other processes, mapped in theirs too. The
            // shell's libpaf shares its common resources this way (ScePafCRSurfacePoolOffset, ScePafCRSoundDataStorage:
            // FW 3.74, opt 0x40 bytes, attr 0x4020); the libpaf of every other system program opens them.
            shareable = (attr & ShareAttr) != 0 && optSize >= 0x18 && GuestMemory.R32(opt + 0x14) != 0;
        }
        if ((size & (align - 1)) != 0 && !cdram && (size & 0xFFF) != 0) return SceError.ILLEGAL_SIZE;
        try
        {
            // a shareable block lives in memory every address space maps (as the CDRAM ones)
            uint addr = Mem.Alloc(size, $"memblock:{name}", cdram ? MemoryRegion.Cdram : shareable ? MemoryRegion.Kernel : MemoryRegion.User, align);
            var b = new MemBlock { Name = name, Address = addr, Size = GuestMemory.AlignUp(size, 0x1000), Type = type };
            return K.Objects.Register(b);
        }
        catch (OutOfMemoryException)
        {
            Log.Error($"sceKernelAllocMemBlock({name}, 0x{type:X}, 0x{size:X}) out of memory");
            return SceError.NO_MEMORY;
        }
    }

    const uint VmBlockGranule = 0x100000, VmBudget = 0x1000000;
    bool _vmDomainOpen;

    /// <summary>Executable blocks for JITs (Mono/PSM): size a multiple of 1 MiB, at most 16 MiB in all (Vita3K).</summary>
    [Hle]
    int sceKernelAllocMemBlockForVM(string name, uint size)
    {
        if (size == 0 || (size & (VmBlockGranule - 1)) != 0) return SceError.ILLEGAL_MEMBLOCK_SIZE;
        long used = K.Objects.Owned<MemBlock>(Emu.Pid).Where(b => b.IsVm).Sum(b => (long)b.Size);
        if (used + size > VmBudget) return SceError.MEMBLOCK_OVERFLOW;
        int uid = sceKernelAllocMemBlock(name, TypeUserRx, size, 0);
        if (uid > 0 && K.Objects.Get<MemBlock>(uid) is { } blk) blk.IsVm = true;
        return uid;
    }

    [Hle]
    int sceKernelFreeMemBlockForVM(int uid)
    {
        var b = K.Objects.Get<MemBlock>(uid);
        if (b == null || !b.IsVm) return SceError.INVALID_ARGUMENT;
        return sceKernelFreeMemBlock(uid);
    }

    /// <summary>Makes the VM blocks writable (open) or executable again (close); nested calls fail with ENOSYS.</summary>
    [Hle]
    int sceKernelOpenVMDomain()
    {
        lock (this) { if (_vmDomainOpen) return SceError.ERRNO_ENOSYS; _vmDomainOpen = true; }
        return 0;
    }

    [Hle]
    int sceKernelCloseVMDomain()
    {
        lock (this) { if (!_vmDomainOpen) return SceError.ERRNO_ENOSYS; _vmDomainOpen = false; }
        return 0;
    }

    /// <summary>Instruction cache flush after writing JIT code: drops translated blocks of the range.</summary>
    [Hle]
    int sceKernelSyncVMDomain(int uid, uint data, uint size)
    {
        var b = K.Objects.Get<MemBlock>(uid);
        if (b == null) return SceError.ILLEGAL_BLOCK_ID;
        if (data < b.Address || (ulong)data + size > (ulong)b.Address + b.Size) return SceError.MEMBLOCK_RANGE_ERROR;
        Emu.Code.Invalidate(data, size);
        return 0;
    }

    [Hle]
    int sceKernelFreeMemBlock(int uid)
    {
        var b = K.Objects.Get<MemBlock>(uid);
        if (b == null) return SceError.INVALID_UID;
        Mem.Free(b.Address);
        K.Objects.Remove(uid);
        return 0;
    }

    [Hle]
    int sceKernelGetMemBlockBase(int uid, Ptr<uint> basePtr)
    {
        var b = K.Objects.Get<MemBlock>(uid);
        if (b == null) return SceError.INVALID_UID;
        basePtr.TrySet(b.Address);
        return 0;
    }

    [Hle]
    int sceKernelFindMemBlockByAddr(uint addr, uint size)
    {
        foreach (var b in K.Objects.Owned<MemBlock>(Emu.Pid))
            if (addr >= b.Address && addr < b.Address + b.Size) return b.Uid;
        return SceError.BLOCK_ERROR;
    }

    [Hle]
    int sceKernelGetMemBlockInfoByAddr(uint addr, uint info)
    {
        foreach (var b in K.Objects.Owned<MemBlock>(Emu.Pid))
        {
            if (addr < b.Address || addr >= b.Address + b.Size) continue;
            GuestMemory.W32(info + 4, b.Address);
            GuestMemory.W32(info + 8, b.Size);
            GuestMemory.W32(info + 12, b.Type == TypeUserCdramRw ? 0x40u : 0x20u);
            GuestMemory.W32(info + 16, 6);
            GuestMemory.W32(info + 20, b.Type);
            return 0;
        }
        return SceError.BLOCK_ERROR;
    }

    [Hle("sceKernelGetMemBlockInfoByRange")]
    int sceKernelGetMemBlockInfoByRange(uint addr, uint size, uint info) => sceKernelGetMemBlockInfoByAddr(addr, info);

    [Hle]
    int sceKernelGetFreeMemorySize(uint info)
    {
        long used = K.Objects.Owned<MemBlock>(Emu.Pid).Where(b => b.Type != TypeUserCdramRw).Sum(b => (long)b.Size);
        long usedCdram = K.Objects.Owned<MemBlock>(Emu.Pid).Where(b => b.Type == TypeUserCdramRw).Sum(b => (long)b.Size);
        GuestMemory.W32(info + 4, (uint)Math.Max(0, UserBudget - used));
        GuestMemory.W32(info + 8, (uint)Math.Max(0, CdramBudget - usedCdram));
        GuestMemory.W32(info + 12, (uint)PhyContBudget);
        return 0;
    }

    [Hle] int sceKernelGetModel() => 0x10000;          // PS Vita (not PS TV)
    [Hle] int sceKernelGetModelForCDialog() => 0x10000;
    [Hle] int sceKernelIsPSVitaTV() => 0;
    /// <summary>SceDebugLed: sets the development-kit GPO debug LEDs; retail units have none (Vita3K: no-op).</summary>
    [Hle] int sceKernelSetGPO(uint state) => 0;
    /// <summary>SceKernelSubbudgetInfo (0xC): +0 size, +4 total_size, +8 free_size. MAIN (0) follows
    /// sceKernelGetFreeMemorySize; CDLG (1, common dialog memory) size is a confirmar (reported as 0).</summary>
    [Hle]
    int sceKernelGetSubbudgetInfo(int id, uint info)
    {
        if (id is not (0 or 1)) return SceError.INVALID_ARGUMENT;
        Span<byte> f = stackalloc byte[0xC];
        f.Clear();
        KernelInfo.Put(f, 0, 0xC);
        if (id == 0)
        {
            long used = K.Objects.Owned<MemBlock>(Emu.Pid).Where(b => b.Type != TypeUserCdramRw).Sum(b => (long)b.Size);
            KernelInfo.Put(f, 4, (uint)UserBudget);
            KernelInfo.Put(f, 8, (uint)Math.Max(0, UserBudget - used));
        }
        return KernelInfo.Write(info, f);
    }

    [Hle]
    int sceKernelOpenMemBlock(string name, int flags)
    {
        if (K.Objects.FindByName<MemBlock>(name) is not { } b) return SceError.UID_CANNOT_FIND_BY_NAME;
        // CDRAM is in every address space; another process's user memory is not mapped in this one
        if (!GuestMemory.IsSharedAddress(b.Address) && Emu.Vita.ByPid(b.OwnerPid) is { } owner && !ReferenceEquals(owner.Space, Emu.Space))
            Log.WarnOnce($"openmemblock-{name}", $"sceKernelOpenMemBlock({name}): user memory of {owner.Describe()} is not mapped in {Emu.Describe()}'s address space");
        return b.Uid;
    }
    [Hle] int sceKernelCloseMemBlock(int uid) => KernelInfo.CloseHandle<MemBlock>(K.Objects, uid, SceError.INVALID_UID);
}
