using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceCpuForDriver and SceCpuForKernel (FW 3.74 SceSysmem): caches, interrupt masking, the spinlocks and
/// reader-writer spinlocks kept in a guest word, the special atomics, the core lock, the user lock words and the CPU
/// registers kernel code reads. Each function follows the firmware's code (studied in the disassembled module).
/// Guest memory has no data cache: a data-cache operation changes nothing, but a by-address operation on an unmapped
/// address faults as the MMU makes it fault (and the operations that translate each page to a physical address panic
/// as the firmware does). An instruction-cache operation drops the translated code of its range.
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>The L1 cache line of the Cortex-A9: the by-address loops of the firmware step by it.</summary>
    const uint CacheLine = 0x20;

    /// <summary>What a reader-writer spinlock word holds while it is write-locked (a reader count is positive).</summary>
    const int WriteLockedWord = unchecked((int)0x80000000);

    /// <summary>What the try-lock functions return when the lock is held.</summary>
    const int TryLockBusy = -1;

    /// <summary>What the user lock word functions return when user code may not access the word (their fault
    /// handler's result).</summary>
    const int UserLockWordFault = unchecked((int)0xC0022005);

    /// <summary>The SCTLR the kernel runs with before ksceKernelCpuUpdateSCTLR: the Cortex-A9 reset value with the MMU,
    /// the data cache, branch prediction and the instruction cache on (the boot loader turns them on).</summary>
    const uint BootSctlr = 0x00C5187D;

    /// <summary>The bits ksceKernelCpuUpdateSCTLR sets: alignment check, data cache, branch prediction, instruction
    /// cache.</summary>
    const uint UpdatedSctlrBits = 0x1806;

    /// <summary>The cores of the Vita's CPU.</summary>
    const int CoreCount = 4;

    [VitaSharp.Core.SaveState.SnapshotMissing(BootSctlr)] // states of builds before it: the register was not kept (the boot value)
    uint _sctlr = BootSctlr;

    // the ASID (CONTEXTIDR low byte) of each process: the kernel's is 0, the others are handed out in order
    [VitaSharp.Core.SaveState.SnapshotMissing] // states of builds before it: no process had asked yet
    readonly Dictionary<int, int> _asids = new();
    [VitaSharp.Core.SaveState.SnapshotMissing(1)]
    int _nextAsid = 1;

    // ------------------------------------------------------------------ memory access helpers

    /// <summary>A kernel access to <paramref name="addr"/> that the MMU refuses: the firmware's data abort handler
    /// stops the system.</summary>
    static GuestFaultException DataAbort(string function, uint addr)
    {
        return new GuestFaultException($"data abort in {function}: 0x{addr:X8} is not mapped (kernel panic)");
    }

    /// <summary>The 32-bit lock word at <paramref name="addr"/>: LDREX/STREX fault on an unmapped or unaligned
    /// word.</summary>
    static ref int LockWord(uint addr, string function)
    {
        if ((addr & 3) != 0 || !KernelSpace.IsMappedRange(addr, 4))
        {
            throw DataAbort(function, addr);
        }

        return ref GuestMemory.Ref<int>(addr);
    }

    /// <summary>Waits as the firmware's WFE loops do: the other holder is another guest thread on its own host
    /// thread. A thread the emulator stops leaves the wait.</summary>
    static void WaitForEvent(ref SpinWait spin)
    {
        if (GuestThread.Current is { } thread && thread.Core.StopRequested)
        {
            throw new GuestThreadExitException(0, false);
        }

        spin.SpinOnce();
    }

    /// <summary>The number of cache lines a by-address loop of the firmware runs for [addr, addr + len):
    /// ((addr + len - 1) &gt;&gt; 5) - (addr &gt;&gt; 5) + 1, in 32 bits (a length of 0 at the start of a line wraps
    /// to 2^32 lines).</summary>
    static ulong LinesOf(uint addr, uint len)
    {
        uint lastLine = unchecked(addr + len - 1) >> 5;
        return (ulong)unchecked(lastLine - (addr >> 5)) + 1;
    }

    /// <summary>The address a by-address loop leaves in r0: <paramref name="addr"/> plus one line per line it ran.</summary>
    static uint LineLoopEnd(uint addr, uint len)
    {
        return unchecked((uint)(addr + LinesOf(addr, len) * CacheLine));
    }

    /// <summary>The lines a by-address loop touches must be mapped (a cache operation by address takes the
    /// translation fault of an unmapped one).</summary>
    static void RequireLinesMapped(uint addr, uint len, string function)
    {
        ulong bytes = LinesOf(addr, len) * CacheLine;
        uint start = addr & ~(CacheLine - 1);
        if (bytes > 0x1_0000_0000ul - start || !KernelSpace.IsMappedRange(start, (uint)bytes))
        {
            throw DataAbort(function, start);
        }
    }

    /// <summary>The operations that clean or invalidate the outer cache translate each page with
    /// ksceKernelMMUVAtoPA and panic when a page has no translation.</summary>
    static void RequirePagesTranslated(uint addr, uint len, string function)
    {
        if ((ulong)addr + len > 0x1_0000_0000ul || !KernelSpace.IsMappedRange(addr, len))
        {
            throw new GuestFaultException($"kernel panic in {function}: no physical address for 0x{addr:X8} (+0x{len:X})");
        }
    }

    /// <summary>Drops the translated code of [start, start + size) in every process (kernel code is mapped in every
    /// address space, and the firmware's maintenance operations reach every core).</summary>
    void InvalidateCodeEverywhere(uint start, ulong size)
    {
        uint length = (uint)Math.Min(Math.Min(size, 0x1_0000_0000ul - start), uint.MaxValue);
        Emu.Code.Invalidate(start, length);
        foreach (Emulator process in Emu.Vita.Processes)
        {
            if (!ReferenceEquals(process, Emu))
            {
                process.Code.Invalidate(start, length);
            }
        }
    }

    // ------------------------------------------------------------------ caches

    /// <summary>ksceKernelCpuUnrestrictedMemcpy(dst, src, len): a copy that ignores page protection (writes into code or
    /// read-only data). Like the hardware it leaves the instruction cache alone: callers flush it after patching code.</summary>
    [Hle]
    int ksceKernelCpuUnrestrictedMemcpy(uint dst, uint src, uint len)
    {
        if (!KernelSpace.IsMappedRange(dst, len) || !KernelSpace.IsMappedRange(src, len))
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.Copy(dst, src, len);
        return 0;
    }

    /// <summary>ksceKernelCpuDcacheWritebackInvalidateRange(ptr, len) (the FW 3.60 NID): guest memory has no data cache
    /// to clean.</summary>
    [Hle]
    void ksceKernelCpuDcacheWritebackInvalidateRange(uint ptr, uint len)
    {
    }

    /// <summary>ksceKernelCpuIcacheInvalidateRange(ptr, len) (the FW 3.60 NID): the translated code of the range is
    /// dropped, so code written there runs as written.</summary>
    [Hle]
    void ksceKernelCpuIcacheInvalidateRange(uint ptr, uint len) => InvalidateCode(ptr, len);

    /// <summary>ksceKernelCpuIcacheAndL2WritebackInvalidateRange(ptr, len) (the FW 3.60 NID): the same for the
    /// instruction side.</summary>
    [Hle]
    void ksceKernelCpuIcacheAndL2WritebackInvalidateRange(uint ptr, uint len) => InvalidateCode(ptr, len);

    void InvalidateCode(uint ptr, uint len)
    {
        if (len == 0)
        {
            return;
        }

        uint start = ptr & ~(CacheLine - 1);
        ulong end = Math.Min(((ulong)ptr + len + CacheLine - 1) & ~(ulong)(CacheLine - 1), 0x1_0000_0000ul);
        InvalidateCodeEverywhere(start, end - start);
    }

    /// <summary>
    /// The SceCpuForDriver data-cache range functions: nothing for a length of 0; the variants that also reach the
    /// outer cache (flag 1) skip the regions the firmware leaves alone ([0x20000000, 0x40000000) and
    /// [0x60000000, 0x81000000)); otherwise the L1 loop runs on the range (a fault on an unmapped line) and the outer
    /// variants translate each page (a panic without one).
    /// </summary>
    static void DcacheRange(uint addr, uint len, bool outer, string function)
    {
        if (len == 0)
        {
            return;
        }

        if (outer && (addr - 0x20000000u < 0x20000000u || addr - 0x60000000u < 0x21000000u))
        {
            return;
        }

        RequireLinesMapped(addr, len, function);
        if (outer)
        {
            RequirePagesTranslated(addr, len, function);
        }
    }

    /// <summary>ksceKernelDcacheCleanRange(addr, len): L1 and outer cache clean.</summary>
    [Hle(Nid = 0x103872A5, Library = "SceCpuForDriver")]
    void ksceKernelDcacheCleanRange(uint addr, uint len) => DcacheRange(addr, len, true, nameof(ksceKernelDcacheCleanRange));

    /// <summary>ksceKernelDcacheCleanRangeForL1WBWA(addr, len): L1 clean only.</summary>
    [Hle(Nid = 0x9CB9F0CE, Library = "SceCpuForDriver")]
    void ksceKernelDcacheCleanRangeForL1WBWA(uint addr, uint len) =>
        DcacheRange(addr, len, false, nameof(ksceKernelDcacheCleanRangeForL1WBWA));

    /// <summary>ksceKernelDcacheInvalidateRange(addr, len): L1 and outer cache invalidate.</summary>
    [Hle(Nid = 0x02796361, Library = "SceCpuForDriver")]
    void ksceKernelDcacheInvalidateRange(uint addr, uint len) =>
        DcacheRange(addr, len, true, nameof(ksceKernelDcacheInvalidateRange));

    /// <summary>ksceKernelDcacheInvalidateRangeForL1WBWA(addr, len): L1 invalidate only.</summary>
    [Hle(Nid = 0x8B4C26DF, Library = "SceCpuForDriver")]
    void ksceKernelDcacheInvalidateRangeForL1WBWA(uint addr, uint len) =>
        DcacheRange(addr, len, false, nameof(ksceKernelDcacheInvalidateRangeForL1WBWA));

    /// <summary>ksceKernelDcacheCleanInvalidateRange(addr, len): L1 and outer cache clean and invalidate.</summary>
    [Hle(Nid = 0x364E68A4, Library = "SceCpuForDriver")]
    void ksceKernelDcacheCleanInvalidateRange(uint addr, uint len) =>
        DcacheRange(addr, len, true, nameof(ksceKernelDcacheCleanInvalidateRange));

    /// <summary>ksceKernelDcacheCleanInvalidateRangeForL1WBWA(addr, len): L1 clean and invalidate only.</summary>
    [Hle(Nid = 0xE551F99B, Library = "SceCpuForDriver")]
    void ksceKernelDcacheCleanInvalidateRangeForL1WBWA(uint addr, uint len) =>
        DcacheRange(addr, len, false, nameof(ksceKernelDcacheCleanInvalidateRangeForL1WBWA));

    /// <summary>SceCpuForDriver 0xE813EBB2: a DSB and an outer-cache sync; nothing to wait for here.</summary>
    [Hle(Nid = 0xE813EBB2, Library = "SceCpuForDriver")]
    void ksceKernelCpuOuterCacheSync()
    {
    }

    /// <summary>ksceKernelL1DcacheCleanRange(addr, len): DCCMVAC on each line; returns the address after the last
    /// line (r0 of the firmware's loop).</summary>
    [Hle(Nid = 0x70350360, Library = "SceCpuForKernel", ReturnsValue = true)]
    uint ksceKernelL1DcacheCleanRange(uint addr, uint len)
    {
        RequireLinesMapped(addr, len, nameof(ksceKernelL1DcacheCleanRange));
        return LineLoopEnd(addr, len);
    }

    /// <summary>ksceKernelL1DcacheInvalidateRange(addr, len): DCIMVAC on each line.</summary>
    [Hle(Nid = 0x4236E06A, Library = "SceCpuForKernel", ReturnsValue = true)]
    uint ksceKernelL1DcacheInvalidateRange(uint addr, uint len)
    {
        RequireLinesMapped(addr, len, nameof(ksceKernelL1DcacheInvalidateRange));
        return LineLoopEnd(addr, len);
    }

    /// <summary>ksceKernelL1DcacheCleanInvalidateRange(addr, len): DCCIMVAC on each line.</summary>
    [Hle(Nid = 0x4F442396, Library = "SceCpuForKernel", ReturnsValue = true)]
    uint ksceKernelL1DcacheCleanInvalidateRange(uint addr, uint len)
    {
        RequireLinesMapped(addr, len, nameof(ksceKernelL1DcacheCleanInvalidateRange));
        return LineLoopEnd(addr, len);
    }

    /// <summary>ksceKernelL1DcacheClean(addr): DCCMVAC on one line.</summary>
    [Hle(Nid = 0x74B7F445, Library = "SceCpuForKernel")]
    void ksceKernelL1DcacheClean(uint addr) => RequireLinesMapped(addr, 1, nameof(ksceKernelL1DcacheClean));

    /// <summary>ksceKernelL1DcacheInvalidate(addr): DCIMVAC on one line.</summary>
    [Hle(Nid = 0x7ED856FE, Library = "SceCpuForKernel")]
    void ksceKernelL1DcacheInvalidate(uint addr) => RequireLinesMapped(addr, 1, nameof(ksceKernelL1DcacheInvalidate));

    /// <summary>ksceKernelL1DcacheCleanInvalidate(addr): DCCIMVAC on one line.</summary>
    [Hle(Nid = 0x597426D2, Library = "SceCpuForKernel")]
    void ksceKernelL1DcacheCleanInvalidate(uint addr) =>
        RequireLinesMapped(addr, 1, nameof(ksceKernelL1DcacheCleanInvalidate));

    /// <summary>ksceKernelL1DcacheCleanAll(): by set and way, no address to fault on.</summary>
    [Hle(Nid = 0x49180814, Library = "SceCpuForKernel")]
    void ksceKernelL1DcacheCleanAll()
    {
    }

    /// <summary>ksceKernelL1DcacheInvalidateAll(): by set and way.</summary>
    [Hle(Nid = 0xF9B0B171, Library = "SceCpuForKernel")]
    void ksceKernelL1DcacheInvalidateAll()
    {
    }

    /// <summary>ksceKernelL1DcacheCleanInvalidateAll(): by set and way.</summary>
    [Hle(Nid = 0x4BBA5C82, Library = "SceCpuForKernel")]
    void ksceKernelL1DcacheCleanInvalidateAll()
    {
    }

    /// <summary>ksceKernelL1IcacheInvalidateRange(addr, len): ICIMVAU on each line of the firmware's loop (a length of
    /// 0 at the start of a line runs over the whole address space, as the loop does), then the translated code of those
    /// lines is dropped. Returns the address after the last line.</summary>
    [Hle(Nid = 0x2E637B1D, Library = "SceCpuForKernel", ReturnsValue = true)]
    uint ksceKernelL1IcacheInvalidateRange(uint addr, uint len)
    {
        RequireLinesMapped(addr, len, nameof(ksceKernelL1IcacheInvalidateRange));
        InvalidateCodeEverywhere(addr & ~(CacheLine - 1), LinesOf(addr, len) * CacheLine);
        return LineLoopEnd(addr, len);
    }

    /// <summary>ksceKernelIcacheInvalidateRange(addr, len): translates each page (a panic without a physical address),
    /// cleans the outer cache and invalidates the L1 instruction cache of the range.</summary>
    [Hle(Nid = 0x73E895EA, Library = "SceCpuForKernel")]
    void ksceKernelIcacheInvalidateRange(uint addr, uint len)
    {
        if (len == 0)
        {
            return;
        }

        RequirePagesTranslated(addr, len, nameof(ksceKernelIcacheInvalidateRange));
        InvalidateCodeEverywhere(addr & ~(CacheLine - 1), LinesOf(addr, len) * CacheLine);
    }

    /// <summary>ksceKernelL1IcacheInvalidateEntire(): ICIALLU, this core: the calling process's translated code.</summary>
    [Hle(Nid = 0x15A48AB4, Library = "SceCpuForKernel")]
    int ksceKernelL1IcacheInvalidateEntire()
    {
        Emu.Code.Invalidate(0, uint.MaxValue);
        return 0;
    }

    /// <summary>ksceKernelL1IcacheInvalidateEntireAllCore(): ICIALLUIS, every core: every process's translated
    /// code.</summary>
    [Hle(Nid = 0x803C84BF, Library = "SceCpuForKernel")]
    int ksceKernelL1IcacheInvalidateEntireAllCore()
    {
        InvalidateCodeEverywhere(0, 0x1_0000_0000ul);
        return 0;
    }

    /// <summary>ksceKernelCpuBranchPredictorInvalidateAll(): BPIALL; the emulated CPU predicts nothing.</summary>
    [Hle(Nid = 0x050B8EF6, Library = "SceCpuForKernel")]
    int ksceKernelCpuBranchPredictorInvalidateAll() => 0;

    /// <summary>ksceKernelCpuBranchPredictorInvalidateAllIS(): BPIALLIS.</summary>
    [Hle(Nid = 0x7B0643E2, Library = "SceCpuForKernel")]
    int ksceKernelCpuBranchPredictorInvalidateAllIS() => 0;

    /// <summary>ksceKernelPleFlushRequest(): stops a running preload-engine transfer; there is none.</summary>
    [Hle(Nid = 0xA0A70166, Library = "SceCpuForKernel")]
    void ksceKernelPleFlushRequest()
    {
    }

    /// <summary>SceCpuForKernel 0xB331AB16: an empty function in FW 3.74.</summary>
    [Hle(Nid = 0xB331AB16, Library = "SceCpuForKernel")]
    void ksceKernelCpuNop()
    {
    }

    /// <summary>ksceKernelGetVmaccessRange(start*, end*): the code range of the firmware's user-memory copy routines,
    /// whose faults the exception manager turns into an error result. VitaSharp's copies run in the host, so the
    /// range is empty.</summary>
    [Hle(Nid = 0xC32687D0, Library = "SceCpuForKernel")]
    void ksceKernelGetVmaccessRange(uint startOut, uint endOut)
    {
        GuestMemory.W32(startOut, 0);
        GuestMemory.W32(endOut, 0);
    }

    // ------------------------------------------------------------------ CPU registers

    /// <summary>ksceKernelCpuUpdateSCTLR(): sets the alignment check, cache and branch prediction bits of SCTLR and
    /// returns the new value (the emulated CPU keeps the register as state).</summary>
    [Hle(Nid = 0xC6426313, Library = "SceCpuForKernel", ReturnsValue = true)]
    uint ksceKernelCpuUpdateSCTLR()
    {
        return Interlocked.Or(ref _sctlr, UpdatedSctlrBits) | UpdatedSctlrBits;
    }

    /// <summary>ksceKernelCpuGetCONTEXTIDR(): the ASID of the running address space (CONTEXTIDR &amp; 0xFF): 0 for the
    /// kernel process, one per user process in the order they first ask.</summary>
    [Hle(Nid = 0xFD54A91D, Library = "SceCpuForKernel", ReturnsValue = true)]
    int ksceKernelCpuGetCONTEXTIDR()
    {
        if (ProcessScope.CurrentPid == KernelSpace.KernelPid)
        {
            return 0;
        }

        lock (_asids)
        {
            if (!_asids.TryGetValue(Emu.Pid, out int asid))
            {
                asid = _nextAsid;
                _nextAsid = _nextAsid == 0xFF ? 1 : _nextAsid + 1;
                _asids[Emu.Pid] = asid;
            }

            return asid;
        }
    }

    /// <summary>SceCpuForKernel 0x9818A20B: the translation table base with the firmware's walk attributes
    /// (TTBR | 0x4A: inner and outer write-back write-allocate, shareable).</summary>
    [Hle(Nid = 0x9818A20B, Library = "SceCpuForKernel", ReturnsValue = true)]
    uint ksceKernelCpuTtbrWithAttributes(uint tableBase) => tableBase | 0x4A;

    // ------------------------------------------------------------------ interrupts

    int ThreadKey => GuestThread.Current?.Uid ?? 0;

    /// <summary>The calling thread masked interrupts (ksceKernelCpuSuspendIntr): it may not wait.</summary>
    internal bool InterruptsMaskedForCurrentThread
    {
        get
        {
            lock (_interruptMasks)
            {
                return (_interruptMasks.GetValueOrDefault(ThreadKey) & InterruptsMasked) != 0;
            }
        }
    }

    /// <summary>Masks interrupts for the calling thread and returns the previous mask (0 or the CPSR I bit).
    /// VitaSharp has no interrupt controller: the mask is state only (the HLE devices do not interrupt guest code).</summary>
    int MaskInterrupts()
    {
        lock (_interruptMasks)
        {
            int previous = _interruptMasks.GetValueOrDefault(ThreadKey);
            _interruptMasks[ThreadKey] = InterruptsMasked;
            return previous;
        }
    }

    /// <summary>Puts back the exact interrupt state <paramref name="previous"/> (MSR CPSR_c of a failed try-lock).</summary>
    void RestoreInterrupts(int previous)
    {
        lock (_interruptMasks)
        {
            if ((previous & InterruptsMasked) != 0)
            {
                _interruptMasks[ThreadKey] = InterruptsMasked;
            }
            else
            {
                _interruptMasks.Remove(ThreadKey);
            }
        }
    }

    /// <summary>CPSIE I: interrupts are on again for the calling thread.</summary>
    void UnmaskInterrupts()
    {
        lock (_interruptMasks)
        {
            _interruptMasks.Remove(ThreadKey);
        }
    }

    /// <summary>ksceKernelCpuSuspendIntr(): masks interrupts and returns the previous CPSR I bit (0 or 0x80).</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelCpuSuspendIntr() => MaskInterrupts() & InterruptsMasked;

    /// <summary>ksceKernelCpuResumeIntr(prev): interrupts come back on when bit 7 of <paramref name="previous"/> is clear
    /// (a set bit leaves the state as it is); returns prev &gt;&gt; 8, what the firmware's LSRS leaves in r0.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelCpuResumeIntr(int previous)
    {
        if ((previous & InterruptsMasked) == 0)
        {
            UnmaskInterrupts();
        }

        return (int)((uint)previous >> 8);
    }

    /// <summary>The unlock-and-resume functions turn interrupts back on only when the whole saved value is 0.</summary>
    void ResumeAfterUnlock(int previous)
    {
        if (previous == 0)
        {
            UnmaskInterrupts();
        }
    }

    // ------------------------------------------------------------------ spinlocks (0 = free, the holder's LR when held)

    /// <summary>Takes the spinlock word for <paramref name="owner"/> (waits while it is held).</summary>
    static void AcquireSpinlock(uint lockAddr, int owner, string function)
    {
        ref int word = ref LockWord(lockAddr, function);
        var spin = new SpinWait();
        while (Interlocked.CompareExchange(ref word, owner, 0) != 0)
        {
            WaitForEvent(ref spin);
        }
    }

    /// <summary>Stores 0 in the lock word (DMB, STR, DSB, SEV).</summary>
    static void ReleaseLockWord(uint lockAddr, string function)
    {
        Volatile.Write(ref LockWord(lockAddr, function), 0);
    }

    static unsafe int CallerLr(HleContext context) => (int)context.Cpu->R[14];

    /// <summary>ksceKernelSpinlockLowLock(int *lock): takes the spinlock; the word holds the caller's return
    /// address.</summary>
    [Hle]
    void ksceKernelSpinlockLowLock(HleContext context)
    {
        AcquireSpinlock(context.Gpr(0), CallerLr(context), nameof(ksceKernelSpinlockLowLock));
    }

    /// <summary>ksceKernelSpinlockLowTrylock(int *lock): 0 and the lock taken, or -1 when it is held.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelSpinlockLowTrylock(HleContext context)
    {
        ref int word = ref LockWord(context.Gpr(0), nameof(ksceKernelSpinlockLowTrylock));
        return Interlocked.CompareExchange(ref word, CallerLr(context), 0) == 0 ? 0 : TryLockBusy;
    }

    /// <summary>ksceKernelSpinlockLowUnlock(int *lock): releases the spinlock.</summary>
    [Hle]
    void ksceKernelSpinlockLowUnlock(uint lockAddr) => ReleaseLockWord(lockAddr, nameof(ksceKernelSpinlockLowUnlock));

    /// <summary>ksceKernelSpinlockLowLockCpuSuspendIntr(int *lock): masks interrupts, takes the spinlock (the caller's
    /// return address in the word) and returns the previous CPSR I bit.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelSpinlockLowLockCpuSuspendIntr(HleContext context)
    {
        int previous = MaskInterrupts() & InterruptsMasked;
        AcquireSpinlock(context.Gpr(0), CallerLr(context), nameof(ksceKernelSpinlockLowLockCpuSuspendIntr));
        return previous;
    }

    /// <summary>ksceKernelSpinlockLowTrylockCpuSuspendIntr(int *lock): the previous I bit with the lock taken, or -1
    /// with the interrupt state put back when it is held.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelSpinlockLowTrylockCpuSuspendIntr(HleContext context)
    {
        int previous = MaskInterrupts();
        ref int word = ref LockWord(context.Gpr(0), nameof(ksceKernelSpinlockLowTrylockCpuSuspendIntr));
        if (Interlocked.CompareExchange(ref word, CallerLr(context), 0) != 0)
        {
            RestoreInterrupts(previous);
            return TryLockBusy;
        }

        return previous & InterruptsMasked;
    }

    /// <summary>ksceKernelSpinlockLowUnlockCpuResumeIntr(int *lock, prev): releases the spinlock; interrupts come back
    /// on when prev is 0.</summary>
    [Hle]
    void ksceKernelSpinlockLowUnlockCpuResumeIntr(uint lockAddr, int previous)
    {
        ReleaseLockWord(lockAddr, nameof(ksceKernelSpinlockLowUnlockCpuResumeIntr));
        ResumeAfterUnlock(previous);
    }

    // ------------------------------------------------------------------ reader-writer spinlocks (readers count up,
    // a writer holds 0x80000000)

    /// <summary>Adds a reader: waits while the word plus one is negative (a writer holds it).</summary>
    static void AcquireRead(uint lockAddr, string function)
    {
        ref int word = ref LockWord(lockAddr, function);
        var spin = new SpinWait();
        while (true)
        {
            int old = Volatile.Read(ref word);
            int readers = unchecked(old + 1);
            if (readers >= 0 && Interlocked.CompareExchange(ref word, readers, old) == old)
            {
                return;
            }

            WaitForEvent(ref spin);
        }
    }

    /// <summary>Adds a reader unless the word plus one is negative; false then.</summary>
    static bool TryAcquireRead(uint lockAddr, string function)
    {
        ref int word = ref LockWord(lockAddr, function);
        while (true)
        {
            int old = Volatile.Read(ref word);
            int readers = unchecked(old + 1);
            if (readers < 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref word, readers, old) == old)
            {
                return true;
            }
        }
    }

    static void ReleaseRead(uint lockAddr, string function)
    {
        Interlocked.Decrement(ref LockWord(lockAddr, function));
    }

    /// <summary>ksceKernelRWSpinlockLowReadLock(int *lock): adds a reader.</summary>
    [Hle]
    void ksceKernelRWSpinlockLowReadLock(uint lockAddr) => AcquireRead(lockAddr, nameof(ksceKernelRWSpinlockLowReadLock));

    /// <summary>
    /// ksceKernelRWSpinlockLowTryReadLock(int *lock) as FW 3.74 has it: -1 when a writer holds the lock; otherwise
    /// its loop goes on while the new count (not the STREX status) is not 0, so it adds readers until the count passes
    /// 0x7FFFFFFF and then returns -1 with the word at 0x7FFFFFFF. Only a word of -1 (0 after the add) returns 0.
    /// The end state is reached in one step.
    /// </summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelRWSpinlockLowTryReadLock(uint lockAddr)
    {
        ref int word = ref LockWord(lockAddr, nameof(ksceKernelRWSpinlockLowTryReadLock));
        while (true)
        {
            int old = Volatile.Read(ref word);
            int readers = unchecked(old + 1);
            if (readers < 0)
            {
                return TryLockBusy;
            }

            int final = readers == 0 ? 0 : int.MaxValue;
            if (Interlocked.CompareExchange(ref word, final, old) == old)
            {
                return final == 0 ? 0 : TryLockBusy;
            }
        }
    }

    /// <summary>ksceKernelRWSpinlockLowReadUnlock(int *lock): removes a reader.</summary>
    [Hle]
    void ksceKernelRWSpinlockLowReadUnlock(uint lockAddr) => ReleaseRead(lockAddr, nameof(ksceKernelRWSpinlockLowReadUnlock));

    /// <summary>ksceKernelRWSpinlockLowReadLockCpuSuspendIntr(int *lock): masks interrupts, adds a reader, returns
    /// the previous I bit.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelRWSpinlockLowReadLockCpuSuspendIntr(uint lockAddr)
    {
        int previous = MaskInterrupts() & InterruptsMasked;
        AcquireRead(lockAddr, nameof(ksceKernelRWSpinlockLowReadLockCpuSuspendIntr));
        return previous;
    }

    /// <summary>ksceKernelRWSpinlockLowTryReadLockCpuSuspendIntr(int *lock): the previous I bit with a reader added,
    /// or -1 with the interrupt state put back when a writer holds the lock.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelRWSpinlockLowTryReadLockCpuSuspendIntr(uint lockAddr)
    {
        int previous = MaskInterrupts();
        if (!TryAcquireRead(lockAddr, nameof(ksceKernelRWSpinlockLowTryReadLockCpuSuspendIntr)))
        {
            RestoreInterrupts(previous);
            return TryLockBusy;
        }

        return previous & InterruptsMasked;
    }

    /// <summary>ksceKernelRWSpinlockLowReadUnlockCpuResumeIntr(int *lock, prev): removes a reader; interrupts come
    /// back on when prev is 0.</summary>
    [Hle]
    void ksceKernelRWSpinlockLowReadUnlockCpuResumeIntr(uint lockAddr, int previous)
    {
        ReleaseRead(lockAddr, nameof(ksceKernelRWSpinlockLowReadUnlockCpuResumeIntr));
        ResumeAfterUnlock(previous);
    }

    /// <summary>
    /// ksceKernelRWSpinlockLowWriteLock(int *lock) as FW 3.74 has it: its loop branches back after a STREX that
    /// succeeds (the test is inverted), so the caller takes the lock (0x80000000) and then waits on its own word; it
    /// can only leave when a STREX fails after another thread freed the word, which an emulated STREX never does. The
    /// calling thread waits here until the emulator stops it.
    /// </summary>
    [Hle]
    void ksceKernelRWSpinlockLowWriteLock(uint lockAddr)
    {
        while (true)
        {
            AcquireSpinlock(lockAddr, WriteLockedWord, nameof(ksceKernelRWSpinlockLowWriteLock));
        }
    }

    /// <summary>ksceKernelRWSpinlockLowTryWriteLock(int *lock): 0 with the lock taken, -1 when it has readers or a
    /// writer.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelRWSpinlockLowTryWriteLock(uint lockAddr)
    {
        ref int word = ref LockWord(lockAddr, nameof(ksceKernelRWSpinlockLowTryWriteLock));
        return Interlocked.CompareExchange(ref word, WriteLockedWord, 0) == 0 ? 0 : TryLockBusy;
    }

    /// <summary>ksceKernelRWSpinlockLowWriteUnlock(int *lock): releases the write lock.</summary>
    [Hle]
    void ksceKernelRWSpinlockLowWriteUnlock(uint lockAddr) =>
        ReleaseLockWord(lockAddr, nameof(ksceKernelRWSpinlockLowWriteUnlock));

    /// <summary>ksceKernelRWSpinlockLowWriteLockCpuSuspendIntr(int *lock): masks interrupts, waits for the word to be
    /// free, writes 0x80000000 and returns the previous I bit.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelRWSpinlockLowWriteLockCpuSuspendIntr(uint lockAddr)
    {
        int previous = MaskInterrupts() & InterruptsMasked;
        AcquireSpinlock(lockAddr, WriteLockedWord, nameof(ksceKernelRWSpinlockLowWriteLockCpuSuspendIntr));
        return previous;
    }

    /// <summary>ksceKernelRWSpinlockLowTryWriteLockCpuSuspendIntr(int *lock): the previous I bit with the lock taken,
    /// or -1 with the interrupt state put back when the word is not free.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelRWSpinlockLowTryWriteLockCpuSuspendIntr(uint lockAddr)
    {
        int previous = MaskInterrupts();
        ref int word = ref LockWord(lockAddr, nameof(ksceKernelRWSpinlockLowTryWriteLockCpuSuspendIntr));
        if (Interlocked.CompareExchange(ref word, WriteLockedWord, 0) != 0)
        {
            RestoreInterrupts(previous);
            return TryLockBusy;
        }

        return previous & InterruptsMasked;
    }

    /// <summary>ksceKernelRWSpinlockLowWriteUnlockCpuResumeIntr(int *lock, prev): releases the write lock; interrupts
    /// come back on when prev is 0.</summary>
    [Hle]
    void ksceKernelRWSpinlockLowWriteUnlockCpuResumeIntr(uint lockAddr, int previous)
    {
        ReleaseLockWord(lockAddr, nameof(ksceKernelRWSpinlockLowWriteUnlockCpuResumeIntr));
        ResumeAfterUnlock(previous);
    }

    // ------------------------------------------------------------------ core lock (one turn per core, in core order)

    // the syscall's C# body, which this kernel function shares (on the Vita another function): a hook on
    // the syscall export does not see it
#pragma warning disable VSHLE010
    int CurrentCpuId => UserSysMem.sceKernelGetCpuId();
#pragma warning restore VSHLE010

    /// <summary>ksceKernelCorelockInitialize(lock): word +0 = 1 (the spinlock held), +4 = 4 (cores left), +6 = 4 (no
    /// core's turn).</summary>
    [Hle(Nid = 0xA65F6F14, Library = "SceCpuForKernel")]
    void ksceKernelCorelockInitialize(uint lockAddr)
    {
        GuestMemory.W32(lockAddr, 1);
        GuestMemory.Write(lockAddr + 4, (ushort)CoreCount);
        GuestMemory.Write(lockAddr + 6, (ushort)CoreCount);
    }

    /// <summary>ksceKernelCorelockLock(lock, firstCore): the core <c>firstCore</c> goes first without the spinlock;
    /// every other core waits for its turn (+6) and then takes the spinlock its predecessor released.</summary>
    [Hle(Nid = 0x2FF54320, Library = "SceCpuForKernel")]
    void ksceKernelCorelockLock(HleContext context) => CorelockLock(context.Gpr(0), (int)context.Gpr(1), CallerLr(context));

    /// <summary>See <see cref="ksceKernelCorelockLock"/>; <paramref name="owner"/> is the spinlock's owner word.</summary>
    void CorelockLock(uint lockAddr, int firstCore, int owner)
    {
        int core = CurrentCpuId;
        if (core == firstCore)
        {
            GuestMemory.Write(lockAddr + 6, (ushort)core);
            return;
        }

        ref short turn = ref GuestMemory.Ref<short>(lockAddr + 6);
        var spin = new SpinWait();
        while (Volatile.Read(ref turn) != (short)core)
        {
            WaitForEvent(ref spin);
        }

        AcquireSpinlock(lockAddr, owner, nameof(ksceKernelCorelockLock));
    }

    /// <summary>ksceKernelCorelockUnlock(lock): hands the turn to the next core, counts this core out and releases the
    /// spinlock. The first core out waits for every core and initializes the lock again; the others wait for that.</summary>
    [Hle(Nid = 0xFE2B77A3, Library = "SceCpuForKernel")]
    void ksceKernelCorelockUnlock(uint lockAddr)
    {
        int core = CurrentCpuId;
        int next = core + 1 == CoreCount ? 0 : core + 1;
        GuestMemory.Write(lockAddr + 6, (ushort)next);
        ref int countWord = ref GuestMemory.Ref<int>(lockAddr + 4);
        short before;
        while (true)
        {
            int word = Volatile.Read(ref countWord);
            before = (short)word;
            int updated = (int)(((uint)word & 0xFFFF0000u) | (ushort)(before - 1));
            if (Interlocked.CompareExchange(ref countWord, updated, word) == word)
            {
                break;
            }
        }

        ReleaseLockWord(lockAddr, nameof(ksceKernelCorelockUnlock));
        ref short count = ref GuestMemory.Ref<short>(lockAddr + 4);
        var spin = new SpinWait();
        if (before == CoreCount)
        {
            while (Volatile.Read(ref count) > 0)
            {
                WaitForEvent(ref spin);
            }

            ksceKernelCorelockInitializeThroughEntry(lockAddr);
            return;
        }

        while (Volatile.Read(ref count) != CoreCount)
        {
            WaitForEvent(ref spin);
        }
    }

    // ------------------------------------------------------------------ user lock words (ThreadMgr's, in user memory)

    /// <summary>SceCpuForKernel 0x75A85059 (lock word, owner): reads the 8-byte word in user memory; a free word (low
    /// half 0) takes <paramref name="owner"/> and returns 0; a held one counts a waiter in the high half and returns
    /// 1. A word user code may not read returns 0xC0022005.</summary>
    [Hle(Nid = 0x75A85059, Library = "SceCpuForKernel", ReturnsValue = true)]
    int ksceKernelUserLockWordTryLock(uint wordAddr, int owner)
    {
        if (!IsUserAccessible(wordAddr, 8))
        {
            return UserLockWordFault;
        }

        if ((wordAddr & 7) != 0)
        {
            throw DataAbort(nameof(ksceKernelUserLockWordTryLock), wordAddr);
        }

        ref long word = ref GuestMemory.Ref<long>(wordAddr);
        while (true)
        {
            long old = Volatile.Read(ref word);
            uint holder = (uint)old;
            uint waiters = (uint)((ulong)old >> 32);
            long updated = holder == 0
                ? (long)(((ulong)waiters << 32) | (uint)owner)
                : (long)(((ulong)unchecked(waiters + 1) << 32) | holder);
            if (Interlocked.CompareExchange(ref word, updated, old) == old)
            {
                return holder == 0 ? 0 : 1;
            }
        }
    }

    /// <summary>SceCpuForKernel 0xFE9ED689 (lock word): clears +8, then the holder (+0), as user accesses; 0, or
    /// 0xC0022005 at the first word user code may not write.</summary>
    [Hle(Nid = 0xFE9ED689, Library = "SceCpuForKernel", ReturnsValue = true)]
    int ksceKernelUserLockWordRelease(uint wordAddr)
    {
        if (!IsUserAccessible(wordAddr + 8, 4))
        {
            return UserLockWordFault;
        }

        GuestMemory.W32(wordAddr + 8, 0);
        if (!IsUserAccessible(wordAddr, 4))
        {
            return UserLockWordFault;
        }

        Volatile.Write(ref GuestMemory.Ref<int>(wordAddr), 0);
        return 0;
    }

    /// <summary>SceCpuForKernel 0x9B071FD3 (lock word, low, high, third): checks the word is user readable, writes
    /// <paramref name="third"/> at +8 as a user access, then the 8 bytes (low, high); 0, or 0xC0022005.</summary>
    [Hle(Nid = 0x9B071FD3, Library = "SceCpuForKernel", ReturnsValue = true)]
    int ksceKernelUserLockWordSet(uint wordAddr, uint low, uint high, uint third)
    {
        if (!IsUserAccessible(wordAddr, 4) || !IsUserAccessible(wordAddr + 8, 4))
        {
            return UserLockWordFault;
        }

        GuestMemory.W32(wordAddr + 8, third);
        if (!KernelSpace.IsMappedRange(wordAddr, 8))
        {
            throw DataAbort(nameof(ksceKernelUserLockWordSet), wordAddr);
        }

        GuestMemory.W32(wordAddr, low);
        GuestMemory.W32(wordAddr + 4, high);
        return 0;
    }

    // ------------------------------------------------------------------ special atomics

    /// <summary>The containing aligned word and the shift of a byte or halfword (the 8/16-bit LDREXB/H).</summary>
    static ref int SubWord(uint addr, int bytes, string function, out int shift)
    {
        if ((addr & (uint)(bytes - 1)) != 0 || !KernelSpace.IsMappedRange(addr, (uint)bytes))
        {
            throw DataAbort(function, addr);
        }

        shift = (int)(addr & 3) * 8;
        return ref GuestMemory.Ref<int>(addr & ~3u);
    }

    static int SignExtend(uint value, int bytes)
    {
        if (bytes == 1)
        {
            return (sbyte)value;
        }

        return bytes == 2 ? (short)value : (int)value;
    }

    /// <summary>SetIfGreaterGet: stores <paramref name="value"/> when it is greater than the (sign-extended) old
    /// value; returns the larger of the two.</summary>
    static int SetIfGreater(uint addr, int value, int bytes, string function)
    {
        if (bytes == 4)
        {
            ref int word32 = ref LockWord(addr, function);
            while (true)
            {
                int old = Volatile.Read(ref word32);
                if (value <= old)
                {
                    return old;
                }

                if (Interlocked.CompareExchange(ref word32, value, old) == old)
                {
                    return value;
                }
            }
        }

        ref int word = ref SubWord(addr, bytes, function, out int shift);
        uint mask = (bytes == 1 ? 0xFFu : 0xFFFFu) << shift;
        while (true)
        {
            int current = Volatile.Read(ref word);
            int old = SignExtend((uint)current >> shift, bytes);
            if (value <= old)
            {
                return old;
            }

            int updated = (int)(((uint)current & ~mask) | (((uint)value << shift) & mask));
            if (Interlocked.CompareExchange(ref word, updated, current) == current)
            {
                return value;
            }
        }
    }

    /// <summary>ksceKernelAtomicSetIfGreaterGet8(addr, value).</summary>
    [Hle(Nid = 0xC3868071, Library = "SceCpuForDriver", ReturnsValue = true)]
    int ksceKernelAtomicSetIfGreaterGet8(uint addr, int value) =>
        SetIfGreater(addr, value, 1, nameof(ksceKernelAtomicSetIfGreaterGet8));

    /// <summary>ksceKernelAtomicSetIfGreaterGet16(addr, value).</summary>
    [Hle(Nid = 0x875B094D, Library = "SceCpuForDriver", ReturnsValue = true)]
    int ksceKernelAtomicSetIfGreaterGet16(uint addr, int value) =>
        SetIfGreater(addr, value, 2, nameof(ksceKernelAtomicSetIfGreaterGet16));

    /// <summary>ksceKernelAtomicSetIfGreaterGet32(addr, value).</summary>
    [Hle(Nid = 0x26F71995, Library = "SceCpuForDriver", ReturnsValue = true)]
    int ksceKernelAtomicSetIfGreaterGet32(uint addr, int value) =>
        SetIfGreater(addr, value, 4, nameof(ksceKernelAtomicSetIfGreaterGet32));

    /// <summary>ksceKernelAtomicAddAndGetPositive32InRange(addr, add, limit): 2 when the word is negative (nothing
    /// stored); else stores min(word + add, limit) and returns 1 when the sum reached the limit, 0 below it.</summary>
    [Hle(Nid = 0x8F40D0FD, Library = "SceCpuForKernel", ReturnsValue = true)]
    int ksceKernelAtomicAddAndGetPositive32InRange(uint addr, int add, int limit)
    {
        ref int word = ref LockWord(addr, nameof(ksceKernelAtomicAddAndGetPositive32InRange));
        while (true)
        {
            int old = Volatile.Read(ref word);
            if (old < 0)
            {
                return 2;
            }

            int sum = unchecked(old + add);
            int stored = limit < sum ? limit : sum;
            if (Interlocked.CompareExchange(ref word, stored, old) == old)
            {
                return limit > sum ? 0 : 1;
            }
        }
    }

    /// <summary>ksceKernelAtomicIncrementHighwaterCounter(addr): the low half counts up, the high half keeps the
    /// highest count (it goes up with the low half when the low half passes it); returns the new word.</summary>
    [Hle(Nid = 0x9DA58A9E, Library = "SceCpuForKernel", ReturnsValue = true)]
    uint ksceKernelAtomicIncrementHighwaterCounter(uint addr)
    {
        ref int word = ref LockWord(addr, nameof(ksceKernelAtomicIncrementHighwaterCounter));
        while (true)
        {
            int old = Volatile.Read(ref word);
            uint updated = unchecked((uint)old + 1);
            if ((updated & 0xFFFF) > updated >> 16)
            {
                updated = unchecked(updated + 0x10000);
            }

            if (Interlocked.CompareExchange(ref word, (int)updated, old) == old)
            {
                return updated;
            }
        }
    }

    /// <summary>ksceKernelAtomicDecIfLowPositive32(addr): decrements the word when its low half is not 0; returns the
    /// word after (the old word when nothing changed).</summary>
    [Hle(Nid = 0x0E04BD2E, Library = "SceCpuForKernel", ReturnsValue = true)]
    uint ksceKernelAtomicDecIfLowPositive32(uint addr)
    {
        ref int word = ref LockWord(addr, nameof(ksceKernelAtomicDecIfLowPositive32));
        while (true)
        {
            int old = Volatile.Read(ref word);
            if (((uint)old & 0xFFFF) == 0)
            {
                return (uint)old;
            }

            int updated = unchecked(old - 1);
            if (Interlocked.CompareExchange(ref word, updated, old) == old)
            {
                return (uint)updated;
            }
        }
    }

    /// <summary>The 8-byte word of the 64-bit atomics (LDREXD/STREXD: 8-byte aligned).</summary>
    static ref long DoubleWord(uint addr, string function)
    {
        if ((addr & 7) != 0 || !KernelSpace.IsMappedRange(addr, 8))
        {
            throw DataAbort(function, addr);
        }

        return ref GuestMemory.Ref<long>(addr);
    }

    static long Pack(int low, int high) => (long)(((ulong)(uint)high << 32) | (uint)low);

    /// <summary>ksceKernelAtomicGet32AndSet64(addr, value): both halves of the 8-byte word become
    /// <paramref name="value"/>.</summary>
    [Hle(Nid = 0x59448055, Library = "SceCpuForKernel")]
    void ksceKernelAtomicGet32AndSet64(uint addr, int value)
    {
        Interlocked.Exchange(ref DoubleWord(addr, nameof(ksceKernelAtomicGet32AndSet64)), Pack(value, value));
    }

    /// <summary>ksceKernelAtomicGet32AndSet64_2(addr, value): the same code under a second NID.</summary>
    [Hle(Nid = 0x1A62AD6A, Library = "SceCpuForKernel")]
    void ksceKernelAtomicGet32AndSet64Second(uint addr, int value)
    {
        Interlocked.Exchange(ref DoubleWord(addr, nameof(ksceKernelAtomicGet32AndSet64Second)), Pack(value, value));
    }

    /// <summary>One of the 64-bit range atomics: from the (low, high) halves and the operand, the new halves and the
    /// result.</summary>
    delegate int DoubleWordUpdate(int low, int high, int operand, int limit, out int newLow, out int newHigh);

    /// <summary>Runs <paramref name="update"/> on the (low, high) halves until the STREXD lands; returns its
    /// result.</summary>
    static int UpdateDoubleWord(uint addr, string function, int operand, int limit, DoubleWordUpdate update)
    {
        ref long word = ref DoubleWord(addr, function);
        while (true)
        {
            long old = Volatile.Read(ref word);
            int result = update((int)old, (int)(old >> 32), operand, limit, out int low, out int high);
            if (Interlocked.CompareExchange(ref word, Pack(low, high), old) == old)
            {
                return result;
            }
        }
    }

    static int Add32InRange(int low, int high, int add, int limit, out int newLow, out int newHigh)
    {
        int sum = unchecked(low + add);
        bool reached = limit <= sum;
        newLow = reached ? low : sum;
        newHigh = high;
        return reached ? 1 : 0;
    }

    static int Add32InHiLoRange(int low, int high, int add, int limit, out int newLow, out int newHigh)
    {
        int sum = unchecked(low + add);
        bool reached = limit <= sum;
        newLow = reached ? low : sum;
        newHigh = newLow > high ? sum : high;
        return reached ? 1 : 0;
    }

    static int SubIfEnough(int low, int high, int sub, int unused, out int newLow, out int newHigh)
    {
        bool enough = sub <= low;
        newLow = enough ? unchecked(low - sub) : low;
        newHigh = high;
        return enough ? 0 : 1;
    }

    static int SubAndLimitHigh(int low, int high, int sub, int unused, out int newLow, out int newHigh)
    {
        bool enough = sub <= low;
        newLow = enough ? unchecked(low - sub) : low;
        newHigh = high >= newLow ? newLow : high;
        return enough ? 0 : 1;
    }

    /// <summary>ksceKernelAtomicAdd32AndGet64InRange(addr, add, limit): the low half takes low + add when that stays
    /// below <paramref name="limit"/> (returns 0); otherwise nothing changes and it returns 1.</summary>
    [Hle(Nid = 0xA12D4388, Library = "SceCpuForKernel", ReturnsValue = true)]
    int ksceKernelAtomicAdd32AndGet64InRange(uint addr, int add, int limit) =>
        UpdateDoubleWord(addr, nameof(ksceKernelAtomicAdd32AndGet64InRange), add, limit, Add32InRange);

    /// <summary>ksceKernelAtomicAdd32AndGet64InHiLoRange(addr, add, limit): as ...InRange; then the high half (a high
    /// water mark) takes the sum when the low half is above it.</summary>
    [Hle(Nid = 0x61DDA758, Library = "SceCpuForKernel", ReturnsValue = true)]
    int ksceKernelAtomicAdd32AndGet64InHiLoRange(uint addr, int add, int limit) =>
        UpdateDoubleWord(addr, nameof(ksceKernelAtomicAdd32AndGet64InHiLoRange), add, limit, Add32InHiLoRange);

    /// <summary>ksceKernelAtomicSubIfGreater64(addr, sub): the low half loses <paramref name="sub"/> when it holds at
    /// least that much (returns 0); otherwise 1.</summary>
    [Hle(Nid = 0x3D4FBB32, Library = "SceCpuForKernel", ReturnsValue = true)]
    int ksceKernelAtomicSubIfGreater64(uint addr, int sub) =>
        UpdateDoubleWord(addr, nameof(ksceKernelAtomicSubIfGreater64), sub, 0, SubIfEnough);

    /// <summary>ksceKernelAtomicLimit64(addr, sub): as SubIfGreater64; then the high half (a low water mark) takes the
    /// low half when it is not below it.</summary>
    [Hle(Nid = 0xA3D7140F, Library = "SceCpuForKernel", ReturnsValue = true)]
    int ksceKernelAtomicLimit64(uint addr, int sub) =>
        UpdateDoubleWord(addr, nameof(ksceKernelAtomicLimit64), sub, 0, SubAndLimitHigh);
}
