using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

public sealed class MemBlock : KernelObject
{
    public uint Address, Size, Type;
}

/// <summary>SceSysmem: memory blocks.</summary>
public sealed class SysMem : HleModule
{
    public const uint TypeUserRw = 0x0C20D060, TypeUserRwUncache = 0x0C208060, TypeUserCdramRw = 0x09408060,
        TypeUserMainPhyContRw = 0x0C80D060, TypeUserMainPhyContNcRw = 0x0D808060, TypeUserMainCdialogRw = 0x0CA0D060,
        TypeUserMainCdialogNcRw = 0x0CA08060, TypeUserRx = 0x0C20D050;

    const long UserBudget = 365L * 1024 * 1024, CdramBudget = 112L * 1024 * 1024, PhyContBudget = 26L * 1024 * 1024;

    [Hle]
    int sceKernelAllocMemBlock(string name, uint type, uint size, uint opt)
    {
        if (size == 0) return SceError.ILLEGAL_SIZE;
        bool cdram = type == TypeUserCdramRw;
        uint align = cdram ? 0x40000u : (type == TypeUserMainPhyContRw || type == TypeUserMainPhyContNcRw) ? 0x100000u : 0x1000u;
        if (opt != 0)
        {
            uint optSize = GuestMemory.R32(opt), attr = GuestMemory.R32(opt + 4);
            if ((attr & 4) != 0 && optSize >= 12) align = Math.Max(align, GuestMemory.R32(opt + 8)); // ALIGNMENT attr
        }
        if ((size & (align - 1)) != 0 && !cdram && (size & 0xFFF) != 0) return SceError.ILLEGAL_SIZE;
        try
        {
            uint addr = Mem.Alloc(size, $"memblock:{name}", cdram ? MemoryRegion.Cdram : MemoryRegion.User, align);
            var b = new MemBlock { Name = name, Address = addr, Size = GuestMemory.AlignUp(size, 0x1000), Type = type };
            return K.Objects.Register(b);
        }
        catch (OutOfMemoryException)
        {
            Log.Error($"sceKernelAllocMemBlock({name}, 0x{type:X}, 0x{size:X}) out of memory");
            return SceError.NO_MEMORY;
        }
    }

    [Hle] int sceKernelAllocMemBlockForVM(string name, uint size) => sceKernelAllocMemBlock(name, TypeUserRx, size, 0);

    [Hle]
    int sceKernelFreeMemBlock(int uid)
    {
        var b = K.Objects.Get<MemBlock>(uid);
        if (b == null) return SceError.UNKNOWN_UID;
        Mem.Free(b.Address);
        K.Objects.Remove(uid);
        return 0;
    }

    [Hle]
    int sceKernelGetMemBlockBase(int uid, Ptr<uint> basePtr)
    {
        var b = K.Objects.Get<MemBlock>(uid);
        if (b == null) return SceError.UNKNOWN_UID;
        basePtr.TrySet(b.Address);
        return 0;
    }

    [Hle]
    int sceKernelFindMemBlockByAddr(uint addr, uint size)
    {
        foreach (var b in K.Objects.All<MemBlock>())
            if (addr >= b.Address && addr < b.Address + b.Size) return b.Uid;
        return unchecked((int)0x80020009);
    }

    [Hle]
    int sceKernelGetMemBlockInfoByAddr(uint addr, uint info)
    {
        foreach (var b in K.Objects.All<MemBlock>())
        {
            if (addr < b.Address || addr >= b.Address + b.Size) continue;
            GuestMemory.W32(info + 4, b.Address);
            GuestMemory.W32(info + 8, b.Size);
            GuestMemory.W32(info + 12, b.Type == TypeUserCdramRw ? 0x40u : 0x20u);
            GuestMemory.W32(info + 16, 6);
            GuestMemory.W32(info + 20, b.Type);
            return 0;
        }
        return unchecked((int)0x80020009);
    }

    [Hle("sceKernelGetMemBlockInfoByRange")]
    int sceKernelGetMemBlockInfoByRange(uint addr, uint size, uint info) => sceKernelGetMemBlockInfoByAddr(addr, info);

    [Hle]
    int sceKernelGetFreeMemorySize(uint info)
    {
        long used = K.Objects.All<MemBlock>().Where(b => b.Type != TypeUserCdramRw).Sum(b => (long)b.Size);
        long usedCdram = K.Objects.All<MemBlock>().Where(b => b.Type == TypeUserCdramRw).Sum(b => (long)b.Size);
        GuestMemory.W32(info + 4, (uint)Math.Max(0, UserBudget - used));
        GuestMemory.W32(info + 8, (uint)Math.Max(0, CdramBudget - usedCdram));
        GuestMemory.W32(info + 12, (uint)PhyContBudget);
        return 0;
    }

    [Hle] int sceKernelGetModel() => 0x10000;          // PS Vita (not PS TV)
    [Hle] int sceKernelGetModelForCDialog() => 0x10000;
    [Hle] int sceKernelIsPSVitaTV() => 0;
    [Hle] int sceKernelGetSubbudgetInfo(uint p) => 0;
    [Hle] int sceKernelOpenMemBlock(string name, int flags) => K.Objects.FindByName<MemBlock>(name)?.Uid ?? SceError.UNKNOWN_UID;
    [Hle] int sceKernelCloseMemBlock(int uid) => 0;
}
