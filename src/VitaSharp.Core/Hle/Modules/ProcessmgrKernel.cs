using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The process side of the firmware's kernel for kernel modules: SceProcessmgrForDriver (process local storage, heap
/// memory in another process), SceProcEventForDriver (process event handlers), SceExcpmgrForKernel (exception handler
/// registration), SceThreadmgrForDriver's thread creation, SceSblACMgrForDriver and ScePowerForDriver's clock.
/// </summary>
public sealed partial class ProcessmgrKernel : HleModule
{
    /// <summary>SCE_EXCP_* kinds the exception manager keeps handler chains for (reset .. FIQ).</summary>
    const uint ExceptionKinds = 8;

    // process local storage: key -> size, and (pid << 32 | key) -> its block
    readonly Dictionary<int, uint> _plsSizes = new();
    readonly Dictionary<ulong, uint> _plsBlocks = new();
    int _nextPlsKey;

    // heap memory allocated in a process by kernel code: address -> pid
    readonly Dictionary<uint, int> _remoteHeap = new();

    // exception handlers registered with the exception manager: handler address -> (kind << 16 | priority)
    readonly Dictionary<uint, uint> _exceptionHandlers = new();
    readonly Dictionary<uint, uint> _exceptionChainHeads = new();

    ThreadMgr Threads => Emu.Hle.Modules.OfType<ThreadMgr>().First();

    uint KernelAlloc(uint size, string name)
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return Mem.Alloc(size, name, MemoryRegion.Kernel);
        }
    }

    Emulator? ProcessOf(int pid) => KernelSpace.ProcessOf(Emu, pid);


    // ------------------------------------------------------------------ process local storage

    /// <summary>ksceKernelCreateProcessLocalStorage(name, size): a per-process block of <paramref name="size"/> bytes
    /// every process gets on request; returns its key (0, 1, ...).</summary>
    [Hle]
    int ksceKernelCreateProcessLocalStorage(uint namePtr, uint size)
    {
        if (namePtr == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (size == 0 || size > 0x10000)
        {
            return SceError.ILLEGAL_SIZE;
        }

        lock (_plsSizes)
        {
            int key = _nextPlsKey++;
            _plsSizes[key] = size;
            return key;
        }
    }

    /// <summary>
    /// ksceKernelGetProcessLocalStorageAddrForPid(pid, key, void **addr, create): the block of key for process
    /// <paramref name="pid"/> (0: the caller's), zeroed kernel memory made on the first request with
    /// <paramref name="create"/> set. Without create a block that does not exist yet is NOT_FOUND (*addr NULL).
    /// </summary>
    [Hle]
    int ksceKernelGetProcessLocalStorageAddrForPid(int pid, int key, uint addrOut, int create)
    {
        if (addrOut == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        var process = ProcessOf(pid);
        if (process == null)
        {
            return SceError.INVALID_PID;
        }

        lock (_plsSizes)
        {
            if (!_plsSizes.TryGetValue(key, out uint size))
            {
                return SceError.INVALID_ARGUMENT;
            }

            ulong slot = ((ulong)(uint)process.Pid << 32) | (uint)key;
            if (!_plsBlocks.TryGetValue(slot, out uint block))
            {
                if (create == 0)
                {
                    GuestMemory.W32(addrOut, 0);
                    return SceError.NO_PROCESS_DATA;
                }

                block = KernelAlloc(size, $"kernel:pls:{process.Pid:X}:{key}");
                _plsBlocks[slot] = block;
            }

            GuestMemory.W32(addrOut, block);
            return 0;
        }
    }

    /// <summary>ksceKernelAllocRemoteProcessHeap(pid, size, opt): user memory in process <paramref name="pid"/>
    /// (NULL when the process does not exist or memory is exhausted).</summary>
    [Hle(ReturnsValue = true)]
    uint ksceKernelAllocRemoteProcessHeap(int pid, uint size, uint opt)
    {
        if (size == 0 || ProcessOf(pid) is not { } process)
        {
            return 0;
        }

        uint addr;
        try
        {
            using (ProcessScope.Enter(process))
            {
                addr = Mem.Alloc(size, "kernel:remoteheap", MemoryRegion.User);
            }
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }

        lock (_remoteHeap)
        {
            _remoteHeap[addr] = process.Pid;
        }

        return addr;
    }

    /// <summary>ksceKernelFreeRemoteProcessHeap(pid, ptr).</summary>
    [Hle]
    void ksceKernelFreeRemoteProcessHeap(int pid, uint ptr)
    {
        int owner;
        lock (_remoteHeap)
        {
            if (!_remoteHeap.Remove(ptr, out owner))
            {
                return;
            }
        }

        if (ProcessOf(owner) is { } process)
        {
            using (ProcessScope.Enter(process))
            {
                Mem.Free(ptr);
            }
        }
    }

    // ------------------------------------------------------------------ process events

    /// <summary>
    /// ksceKernelRegisterProcEventHandler(name, const SceProcEventHandler *handler, a3): the handler table (0x1C bytes:
    /// size, create, exit, kill, stop, start, switch) is copied (callers pass one on their stack). Returns the handler's
    /// UID. The kernel modules of a session live with its process (docs/roadmap.md, kernel plugins): they end together,
    /// so no process event reaches a handler yet.
    /// </summary>
    [Hle]
    int ksceKernelRegisterProcEventHandler(uint namePtr, uint handler, int a3)
    {
        if (namePtr == 0 || handler == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        uint size = GuestMemory.R32(handler);
        if (size < 8 || size > 0x1C)
        {
            return SceError.ILLEGAL_SIZE;
        }

        uint table = KernelAlloc(0x1C, "kernel:procevent");
        GuestMemory.Copy(table, handler, size);
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return K.Objects.Register(new KernelData { Name = GuestMemory.ReadCString(namePtr, 32), Kind = KernelData.ProcEventHandler, Address = table, Size = size });
        }
    }

    /// <summary>ksceKernelUnregisterProcEventHandler(uid).</summary>
    [Hle]
    int ksceKernelUnregisterProcEventHandler(int uid)
    {
        if (K.Objects.Get<KernelData>(uid) is not { Kind: KernelData.ProcEventHandler } h)
        {
            return SceError.INVALID_UID;
        }

        K.Objects.Remove(uid);
        Mem.Free(h.Address);
        return 0;
    }

    // ------------------------------------------------------------------ exceptions

    /// <summary>
    /// sceKernelRegisterExceptionHandler (SceExcpmgrForKernel; ksceExcpmgrRegisterHandler on 3.63+)(kind, priority,
    /// SceKernelExceptionHandler *handler): adds a kernel handler to the chain of one exception kind (the handler's
    /// first word links to the next one). VitaSharp delivers no CPU exceptions to guest code: a guest fault is reported
    /// as the emulator's crash, and what usually traps on hardware (VFP short vectors) is executed directly, so the
    /// chains are kept but never entered.
    /// </summary>
    [Hle]
    int sceKernelRegisterExceptionHandler(uint kind, uint priority, uint handler)
    {
        if (kind >= ExceptionKinds || priority > 0xFF)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (handler == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        lock (_exceptionHandlers)
        {
            uint head = _exceptionChainHeads.GetValueOrDefault(kind);
            GuestMemory.W32(handler, head);
            _exceptionChainHeads[kind] = handler;
            _exceptionHandlers[handler] = (kind << 16) | priority;
        }

        return 0;
    }

    /// <summary>The kernel handlers registered for exception <paramref name="kind"/>, newest first.</summary>
    internal List<uint> ExceptionHandlers(uint kind)
    {
        lock (_exceptionHandlers)
        {
            return _exceptionHandlers.Where(kv => kv.Value >> 16 == kind).Select(kv => kv.Key).Reverse().ToList();
        }
    }

    // ------------------------------------------------------------------ threads

    /// <summary>ksceKernelCreateThread(name, entry, priority, stack size, attr, affinity, opt): a kernel thread (it
    /// works for the kernel process: see <see cref="GuestThread.KernelMode"/>).</summary>
    [Hle]
    int ksceKernelCreateThread(string name, uint entry, int initPriority, int stackSize, uint attr, int cpuAffinityMask, uint option)
    {
        int uid = Threads.sceKernelCreateThread(name, entry, initPriority, stackSize, attr, cpuAffinityMask, option);
        if (uid > 0 && K.GetThread(uid) is { } t)
        {
            t.KernelMode = true;
        }

        return uid;
    }

    /// <summary>ksceKernelRunWithStack(stack size, int (*func)(void *), void *arg): runs func(arg) on a fresh stack and
    /// returns its result. The calling thread's stack is guest memory without the kernel's small fixed stacks, so func
    /// runs on it.</summary>
    [Hle(ReturnsValue = true)]
    int ksceKernelRunWithStack(uint stackSize, uint func, uint arg)
    {
        if (func == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        return (int)GuestThread.Current!.CallGuest(func, arg);
    }

    // ------------------------------------------------------------------ access control and power

    /// <summary>ksceSblACMgrIsSystemProgram (ksceSblACMgrIsShell)(pid): 1 for a system program (it runs from vs0:),
    /// 0 for a game or application.</summary>
    [Hle(ReturnsValue = true)]
    int ksceSblACMgrIsSystemProgram(int pid)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return 1;
        }

        return ProcessOf(pid) is { IsSystemProgram: true } ? 1 : 0;
    }

    /// <summary>kscePowerGetSysClockFrequency(): the ARM clock in MHz (the emulated CPU pacing).</summary>
    [Hle(ReturnsValue = true)]
    int kscePowerGetSysClockFrequency() => (int)((Cpu.CpuCore.InstructionsPerSecond > 0 ? Cpu.CpuCore.InstructionsPerSecond : Cpu.CpuCore.DefaultClockHz) / 1_000_000);

    /// <summary>kscePowerSetSysClockFrequency(MHz): the kernel may set the ARM clock past the applications' 444 MHz
    /// (the overclocking plugins ask for 500); the emulated CPU pacing follows it.</summary>
    [Hle]
    int kscePowerSetSysClockFrequency(int mhz)
    {
        if (mhz < 1 || mhz > 500)
        {
            return SystemMisc.POWER_ERROR_INVALID_VALUE;
        }

        if (Cpu.CpuCore.InstructionsPerSecond > 0)
        {
            Cpu.CpuCore.InstructionsPerSecond = mhz * 1_000_000L;
        }

        Log.Info($"kscePowerSetSysClockFrequency({mhz} MHz)");
        return 0;
    }
}
