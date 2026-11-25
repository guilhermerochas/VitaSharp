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
    [SaveState.SnapshotMissing] uint _defaultExceptionHandler;

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
    /// ksceKernelRegisterProcEventHandler(name, const SceProcEventHandler *handler, argument): the handler table (0x1C
    /// bytes: size, create, exit, kill, stop, start, switch) is copied (callers pass one on their stack), the entries
    /// past its size left 0; <paramref name="a3"/> is the last argument every call of the handler gets. Returns the
    /// handler's UID. The kernel process calls the handlers as processes are created, exit and are killed
    /// (<see cref="ProcessEvents"/>).
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
        GuestMemory.Fill(table, 0, 0x1C);
        GuestMemory.Copy(table, handler, size);
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return K.Objects.Register(new KernelData
            {
                Name = GuestMemory.ReadCString(namePtr, 32),
                Kind = KernelData.ProcEventHandler,
                Address = table,
                Size = size,
                Aux = (uint)a3,
            });
        }
    }

    /// <summary>
    /// ksceKernelInvokeProcEventHandler(pid, event_id, event_type, param, int *failed_uid, stop_at): calls the
    /// registered handlers for one event on the calling thread (<see cref="ProcessEvents.Run"/>), up to handler
    /// <paramref name="stopAt"/>; with <paramref name="failedUidOut"/>, up to the first that fails, whose UID it
    /// receives. Returns what the last handler returned.
    /// </summary>
    [Hle]
    int ksceKernelInvokeProcEventHandler(int pid, int eventId, int eventType, uint param, uint failedUidOut, int stopAt)
    {
        if (GuestThread.Current is not { } thread)
        {
            return SceError.ERROR; // HLE calls come from guest threads
        }

        int result = ProcessEvents.Run(thread, ProcessEvents.Handlers(K.Objects), pid, eventId, eventType, param, failedUidOut != 0, stopAt, out int failedUid);
        if (failedUid != 0)
        {
            GuestMemory.W32(failedUidOut, (uint)failedUid);
        }

        return result;
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
    /// SceKernelExceptionHandler *handler), as the FW 3.74 SceExcpmgr does it: a handler is {next, reserved, code...}
    /// (its first two words must be zero), the chain of each kind is kept in priority order (0 first; among equal
    /// priorities the newest first) and ends at the firmware's default handler, and the exception vector of the kind
    /// enters the first handler's code at +8 in the exception's mode (<see cref="ExceptionVector"/>). Each handler
    /// passes an exception it does not take on by jumping to its next handler's code (+8).
    /// </summary>
    [Hle]
    int sceKernelRegisterExceptionHandler(uint kind, uint priority, uint handler)
    {
        if (handler == 0 || (handler & 2) != 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        uint record = handler & ~3u;
        if (GuestMemory.R32(record) != 0 || GuestMemory.R32(record + 4) != 0)
        {
            return SceError.ILLEGAL_EXCPHANDLER;
        }

        if (kind >= ExceptionKinds)
        {
            return SceError.ILLEGAL_EXCPCODE;
        }

        if (priority >= 8)
        {
            return SceError.ILLEGAL_PRIORITY;
        }

        lock (_exceptionHandlers)
        {
            uint terminal = ChainEnd();
            // before the first handler of a priority not below this one
            uint previous = 0;
            uint next = _exceptionChainHeads.GetValueOrDefault(kind);
            while (next != 0 && next != terminal && _exceptionHandlers.TryGetValue(next, out uint entry) && (entry & 0xFFFF) < priority)
            {
                previous = next;
                next = GuestMemory.R32(next & ~3u);
            }

            GuestMemory.W32(record, next == 0 ? terminal : next);
            if (previous == 0)
            {
                _exceptionChainHeads[kind] = handler;
            }
            else
            {
                GuestMemory.W32(previous & ~3u, handler);
            }

            _exceptionHandlers[handler] = (kind << 16) | priority;
        }

        if (kind == GuestThread.ExceptionDataAbort)
        {
            // guest accesses to unmapped pages raise data aborts from now on (without a handler they read zeros)
            GuestThread.ArmDataAborts(Emu);
        }

        return 0;
    }

    /// <summary>The kernel handlers registered for exception <paramref name="kind"/>, in the order the exception
    /// enters them.</summary>
    internal List<uint> ExceptionHandlers(uint kind)
    {
        lock (_exceptionHandlers)
        {
            var chain = new List<uint>();
            uint at = _exceptionChainHeads.GetValueOrDefault(kind);
            while (at != 0 && _exceptionHandlers.ContainsKey(at) && chain.Count < _exceptionHandlers.Count)
            {
                chain.Add(at);
                at = GuestMemory.R32(at & ~3u);
            }

            return chain;
        }
    }

    /// <summary>
    /// Where exception <paramref name="kind"/> enters the kernel: the code (+8) of the first handler of its chain, or 0
    /// when no handler is registered (VitaSharp then reports the fault itself, as before exception delivery existed).
    /// </summary>
    internal uint ExceptionVector(uint kind)
    {
        lock (_exceptionHandlers)
        {
            uint head = _exceptionChainHeads.GetValueOrDefault(kind);
            if (head == 0)
            {
                head = _registeredDefaultHandler;
            }

            return head == 0 ? 0 : (head & ~3u) + 8;
        }
    }

    /// <summary>The code of the default handler every chain ends at (see <see cref="DefaultExceptionHandler"/>).</summary>
    internal uint DefaultExceptionHandlerCode
    {
        get
        {
            lock (_exceptionHandlers)
            {
                return _defaultExceptionHandler == 0 ? 0 : _defaultExceptionHandler + 8;
            }
        }
    }

    /// <summary>
    /// The handler at the end of every chain (the firmware's own default handler, which ends the faulting process):
    /// a record in kernel memory whose code is an undefined instruction, so reaching it gives the fault back to
    /// VitaSharp, which reports it the way it reports a fault no handler takes.
    /// </summary>
    uint DefaultExceptionHandler()
    {
        if (_defaultExceptionHandler == 0)
        {
            uint record = KernelAlloc(16, "SceExcpmgrDefaultHandler");
            GuestMemory.W32(record, 0);
            GuestMemory.W32(record + 4, 0);
            GuestMemory.W32(record + 8, DefaultHandlerInstruction);
            GuestMemory.W32(record + 12, DefaultHandlerInstruction);
            _defaultExceptionHandler = record;
        }

        return _defaultExceptionHandler;
    }

    /// <summary>The abort and undefined mode stacks of a thread that takes an exception (kernel memory).</summary>
    internal uint AllocateExceptionStack(uint size, string threadName) => KernelAlloc(size, $"SceExcpStack:{threadName}");

    internal void FreeExceptionStack(uint address)
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            Mem.Free(address);
        }
    }

    /// <summary>UDF #0 (ARM): the default handler's code.</summary>
    const uint DefaultHandlerInstruction = 0xE7F000F0;

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

    /// <summary>ksceSblACMgrIsSystemProgram (ksceSblACMgrIsShell)(pid): capability bit 1 of the process's auth info, as
    /// its SceSblACMgrForKernel twin answers it (<see cref="Loader.Self.ProgramAccess"/>): 1 for a system program (it
    /// runs from vs0:) and for homebrew with the system class (an unsafe fake SELF), 0 for a game or application, safe
    /// homebrew and the kernel process (its own auth info has bit 1 clear).</summary>
    [Hle(ReturnsValue = true)]
    int ksceSblACMgrIsSystemProgram(int pid)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return SysmemKernel.KernelAccess.IsSystemProgram ? 1 : 0;
        }

        return ProcessOf(pid) is { Access.IsSystemProgram: true } ? 1 : 0;
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
