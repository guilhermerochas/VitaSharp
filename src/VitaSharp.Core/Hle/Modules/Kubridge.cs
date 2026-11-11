using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// kubridge (ur0:tai/kubridge.skprx, bythos14/kubridge 0.3+): the kernel plugin that gives user homebrew kernel-only
/// memory services. The Android .so loader ports (so_util: Dead Space, Max Payne, GTA, ...) allocate the library's
/// text and data at the addresses it was linked for, write relocations and patches into executable memory and flush the
/// caches through it; VFPVector registers an undefined-instruction handler with it. The plugin is kernel code that
/// edits page tables and exception vectors, so (as Vita3K overrides it) its user API is implemented here. Guest memory
/// has no per-page protection: protection changes are validated and accepted, writes to executable blocks just work.
/// </summary>
public sealed partial class Kubridge : HleModule
{
    /// <summary>The plugin's module name (module info), which homebrew searches for before using it.</summary>
    public override string? KernelModuleName => "kubridge";

    // SceKernelAllocMemBlockKernelOpt attr bits
    const uint AttrHasVbase = 0x1, AttrHasAlignment = 0x4;
    const uint ProtRead = 0x40, ProtWrite = 0x20, ProtExec = 0x10;
    const uint CommitAttrHasBase = 0x1;
    /// <summary>KU_KERNEL_EXCEPTION_TYPE_DATA_ABORT (0), _PREFETCH_ABORT (1), _UNDEFINED_INSTRUCTION (2).</summary>
    const int ExceptionTypes = 3;

    /// <summary>The guest handler registered for each exception type (0 = none).</summary>
    readonly uint[] _handlers = new uint[ExceptionTypes];

    /// <summary>The registered handler of an exception type, 0 when none.</summary>
    public uint HandlerFor(int type) => (uint)type < ExceptionTypes ? Volatile.Read(ref _handlers[type]) : 0;

    SysMem SysMemModule => Emu.Hle.Modules.OfType<SysMem>().First();

    /// <summary>
    /// kuKernelAllocMemBlock(name, type, size, SceKernelAllocMemBlockKernelOpt *opt): ksceKernelAllocMemBlock for the
    /// calling process. opt = { size, field_4, attr, field_C (virtual base with HAS_VBASE), paddr, alignment, ... }; any
    /// type is accepted (SCE_KERNEL_MEMBLOCK_TYPE_USER_RX for the loaded code). The uid works with the user memblock
    /// calls (sceKernelGetMemBlockBase, sceKernelFreeMemBlock).
    /// </summary>
    [Hle]
    int kuKernelAllocMemBlock(string name, uint type, uint size, uint opt)
    {
        if (size == 0) return SceError.ILLEGAL_SIZE;
        uint attr = 0, vbase = 0, align = 0x1000;
        if (opt != 0)
        {
            uint optSize = GuestMemory.R32(opt);
            if (optSize >= 0xC) attr = GuestMemory.R32(opt + 8);
            if ((attr & AttrHasVbase) != 0 && optSize >= 0x10) vbase = GuestMemory.R32(opt + 0xC);
            if ((attr & AttrHasAlignment) != 0 && optSize >= 0x18) align = Math.Max(align, GuestMemory.R32(opt + 0x14));
        }
        if (vbase == 0) return SysMemModule.AllocBlock(name, type, size, align);
        if ((vbase & 0xFFF) != 0) return SceError.INVALID_ARGUMENT;
        uint alloc = GuestMemory.AlignUp(size, 0x1000);
        if (Mem.TryAllocAt(vbase, alloc, $"memblock:{name}") == 0)
        {
            Log.Warn($"kuKernelAllocMemBlock({name}, 0x{type:X}, 0x{size:X}): 0x{vbase:X8} is not free");
            return SceError.NO_MEMORY;
        }
        return K.Objects.Register(new MemBlock { Name = name, Address = vbase, Size = alloc, Type = type });
    }

    /// <summary>kuKernelFlushCaches(ptr, len): data cache write-back and instruction cache invalidation of the range
    /// (32-byte lines): the translated code of the range is dropped, so rewritten code runs as written.</summary>
    [Hle]
    void kuKernelFlushCaches(uint ptr, uint len)
    {
        if (len == 0) return;
        uint start = ptr & ~0x1Fu;
        ulong end = Math.Min(((ulong)ptr + len + 0x1F) & ~0x1Ful, 0x1_0000_0000ul);
        Emu.Code.Invalidate(start, (uint)(end - start));
    }

    /// <summary>
    /// kuKernelCpuUnrestrictedMemcpy(dst, src, len): a copy that ignores page protection (writes into executable or
    /// read-only memory). Like the hardware, it does not touch the instruction cache: the loaders call
    /// kuKernelFlushCaches after patching code.
    /// </summary>
    [Hle]
    int kuKernelCpuUnrestrictedMemcpy(uint dst, uint src, uint len)
    {
        if (len == 0) return 0;
        if (!GuestMemory.IsMapped(dst) || !GuestMemory.IsMapped(src) || !GuestMemory.IsMapped(dst + len - 1) || !GuestMemory.IsMapped(src + len - 1))
        {
            Log.WarnOnce($"kucpy-{dst:X8}", $"kuKernelCpuUnrestrictedMemcpy(0x{dst:X8}, 0x{src:X8}, {len}): unmapped range");
            return SceError.INVALID_ARGUMENT;
        }
        GuestMemory.Span(src, (int)len).CopyTo(GuestMemory.Span(dst, (int)len)); // Span copies handle overlap like memmove
        return 0;
    }

    /// <summary>kuPowerGetSysClockFrequency(): kscePowerGetSysClockFrequency, the ARM clock in MHz.</summary>
    [Hle]
    int kuPowerGetSysClockFrequency() => (int)((Cpu.CpuCore.InstructionsPerSecond > 0 ? Cpu.CpuCore.InstructionsPerSecond : Cpu.CpuCore.DefaultClockHz) / 1_000_000);

    /// <summary>kuPowerSetSysClockFrequency(freq): overclocking past the user limit (444 MHz); the emulated clock stays.</summary>
    [Hle]
    int kuPowerSetSysClockFrequency(int freq)
    {
        Log.WarnOnce("kupower", $"kuPowerSetSysClockFrequency({freq}): the emulated CPU clock is not changed");
        return 0;
    }

    /// <summary>The block of the calling process containing <paramref name="addr"/>.</summary>
    MemBlock? BlockAt(uint addr)
    {
        foreach (var b in K.Objects.Owned<MemBlock>(Emu.Pid))
            if (addr >= b.Address && addr - b.Address < b.Size) return b;
        return null;
    }

    /// <summary>Checks a page range for MemProtect/Commit/Decommit: page aligned, at or above 0x40000000, every page in
    /// a memblock of the process (kubridge walks the blocks of the range and fails on a hole).</summary>
    int CheckPages(uint addr, uint len)
    {
        if (addr < 0x40000000 || (addr & 0xFFF) != 0) return SceError.INVALID_ARGUMENT;
        ulong end = (ulong)addr + GuestMemory.AlignUp(len, 0x1000);
        for (ulong a = addr; a < end;)
        {
            var b = BlockAt((uint)a);
            if (b == null) return SceError.BLOCK_ERROR;
            a = (ulong)b.Address + b.Size;
        }
        return 0;
    }

    /// <summary>kuKernelMemProtect(addr, len, prot): prot = KU_KERNEL_PROT_READ 0x40 | WRITE 0x20 | EXEC 0x10.</summary>
    [Hle]
    int kuKernelMemProtect(uint addr, uint len, uint prot)
    {
        if (len == 0) return 0;
        if ((prot & ~(ProtRead | ProtWrite | ProtExec)) != 0) return SceError.INVALID_ARGUMENT;
        int r = CheckPages(addr, len);
        if (r == 0 && (prot & ProtExec) != 0) Emu.Code.Invalidate(addr, GuestMemory.AlignUp(len, 0x1000));
        return r;
    }

    /// <summary>
    /// kuKernelMemReserve(void **addr, size, type): a block of address space without pages (*addr in: the wanted base
    /// or NULL; out: the base). Pages exist from the start here, which only differs for reads of never-committed pages.
    /// </summary>
    [Hle]
    int kuKernelMemReserve(uint addrPtr, uint size, uint type)
    {
        if (addrPtr == 0 || size == 0) return SceError.INVALID_ARGUMENT;
        uint want = GuestMemory.R32(addrPtr);
        int uid;
        if (want == 0) uid = SysMemModule.AllocBlock("mem_reserve", type, GuestMemory.AlignUp(size, 0x1000), 0x1000);
        else
        {
            if ((want & 0xFFF) != 0) return SceError.INVALID_ARGUMENT;
            uint alloc = GuestMemory.AlignUp(size, 0x1000);
            if (Mem.TryAllocAt(want, alloc, "memblock:mem_reserve") == 0) return SceError.NO_MEMORY;
            uid = K.Objects.Register(new MemBlock { Name = "mem_reserve", Address = want, Size = alloc, Type = type });
        }
        if (uid < 0) return uid;
        GuestMemory.W32(addrPtr, K.Objects.Get<MemBlock>(uid)!.Address);
        return uid;
    }

    /// <summary>kuKernelMemCommit(addr, len, prot, KuKernelMemCommitOpt *opt): opt = { size, attr, baseBlock,
    /// baseOffset }; HAS_BASE maps the pages of another block (a mirror), which guest memory cannot do.</summary>
    [Hle]
    int kuKernelMemCommit(uint addr, uint len, uint prot, uint opt)
    {
        if (len == 0) return 0;
        if ((prot & ~(ProtRead | ProtWrite | ProtExec)) != 0) return SceError.INVALID_ARGUMENT;
        if (opt != 0 && GuestMemory.R32(opt) >= 8 && (GuestMemory.R32(opt + 4) & CommitAttrHasBase) != 0)
            Log.WarnOnce("kucommit-base", "kuKernelMemCommit: mirrors of another block (HAS_BASE) are not supported");
        int r = CheckPages(addr, len);
        if (r == 0 && (prot & ProtExec) != 0) Emu.Code.Invalidate(addr, GuestMemory.AlignUp(len, 0x1000));
        return r;
    }

    /// <summary>kuKernelMemDecommit(addr, len): the pages go back to the system; a later commit gets fresh (zeroed)
    /// pages, so the range is cleared.</summary>
    [Hle]
    int kuKernelMemDecommit(uint addr, uint len)
    {
        if (len == 0) return 0;
        int r = CheckPages(addr, len);
        if (r != 0) return r;
        uint n = GuestMemory.AlignUp(len, 0x1000);
        GuestMemory.Span(addr, (int)n).Clear();
        Emu.Code.Invalidate(addr, n);
        return 0;
    }

    /// <summary>
    /// kuKernelRegisterExceptionHandler(type, handler, KuKernelExceptionHandler *old, opt): one handler per type,
    /// chained by the caller through *old. VFP short vectors, the usual reason homebrew registers an
    /// undefined-instruction handler (VFPVector), are executed by the emulated CPU directly, so they never trap here.
    /// </summary>
    [Hle]
    int kuKernelRegisterExceptionHandler(uint type, uint handler, Ptr<uint> oldHandler, uint opt)
    {
        if (type >= ExceptionTypes) return SceError.INVALID_ARGUMENT;
        uint old = Interlocked.Exchange(ref _handlers[type], handler);
        oldHandler.TrySet(old);
        Log.Info($"kuKernelRegisterExceptionHandler: type {type} handler 0x{handler:X8} (previous 0x{old:X8})");
        return 0;
    }

    [Hle]
    void kuKernelReleaseExceptionHandler(uint type)
    {
        if (type < ExceptionTypes) Interlocked.Exchange(ref _handlers[type], 0);
    }

    /// <summary>kuKernelRegisterAbortHandler(handler, old, opt) (deprecated): the data and prefetch abort handler.</summary>
    [Hle]
    int kuKernelRegisterAbortHandler(uint handler, Ptr<uint> oldHandler, uint opt)
    {
        uint old = Interlocked.Exchange(ref _handlers[0], handler);
        Interlocked.Exchange(ref _handlers[1], handler);
        oldHandler.TrySet(old);
        return 0;
    }

    [Hle]
    void kuKernelReleaseAbortHandler()
    {
        Interlocked.Exchange(ref _handlers[0], 0);
        Interlocked.Exchange(ref _handlers[1], 0);
    }
}
