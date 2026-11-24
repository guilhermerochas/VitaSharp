using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The rest of SceProcessmgrForKernel/ForDriver as the FW 3.74 processmgr.skprx has it (decompiled, study only), on
/// VitaSharp's process model (every process is an <see cref="Emulator"/>, made by <see cref="Emulator.SpawnProcess"/>
/// as the shell's launches make them):
/// <list type="bullet">
/// <item>the life of a process: create (ksceKernelCreateProcess: a title ID, a type of 0x1000000, 0x2000000 or
/// 0x4000000, a path and an option of 0x40 bytes; at most 16 processes), start (NID_026F3E87, NID_5137B783: a created
/// process only, else 0x80029004), the exit and kill paths (NID_1400A1F8 / NID_4232517A, NID_EFBCF15B /
/// NID_8FB7FD50, NID_F388F05C / NID_148E23E3), the launch that creates and starts (NID_1E5B38E5 / NID_5A57766C), and
/// the waits on the process's event flag
/// for its end (NID_0B9BE51B, with the exit status), for an event (NID_0F23A5ED: 2, 0x10, 0x20 or 0x100) and for
/// its stop (NID_BC110987);</item>
/// <item>the process list lock (NID_8321B827, NID_D5DF895D) and the notice the system root gets (NID_344DDE0B);</item>
/// <item>the debugging pairs the debugger sets (NID_597E6D2C, NID_B2421F93, NID_9DF739C7, NID_A2EE1B7F; dipsw 0xE4),
/// the coredump handler of a process (NID_21A6F0EC registers, NID_33B3D026 unregisters, NID_B559410A runs it), the
/// process time of a system time (NID_8CB01675), a word written into the process (NID_6599E5D9), the three words
/// NID_B80DBD53 reads, whether any process is being debugged (NID_C715591F);</item>
/// <item>the process budgets (SceUIDProcBudgetClass): the budget values (NID_923AF3CC), a slot of the budget table
/// (NID_C5CF15ED: 32 slots of 0x20 bytes and a word, kept in processmgr's data, which the firmware fills at run
/// time), and the budget objects' calls (create NID_DC18FCEF, delete NID_459A223B, a word NID_48DD3348, the info
/// NID_CF9EC344 / NID_23EF9D8E, the process's NID_46221964, the default NID_56657302).</item>
/// </list>
/// </summary>
public sealed partial class ProcessmgrKernel
{
    const int TooManyProcesses = unchecked((int)0x80010082), IllegalAffinityMask = unchecked((int)0x80022004),
        IllegalOptionSize = unchecked((int)0x80020009), ProcessIsCaller = unchecked((int)0x80024001),
        CoredumpRegistered = unchecked((int)0x800A0002), CoredumpNotRegistered = unchecked((int)0x800A0001),
        UnknownBudget = unchecked((int)0x80029006);

    /// <summary>The state byte of a process that was created and not started yet.</summary>
    const uint CreatedState = 1;

    /// <summary>The words of the coredump handler in the process object: the handler, its stack size, its argument,
    /// the stack block.</summary>
    const uint CoredumpHandlerWord = 0x3F0, CoredumpStackWord = 0x3F4, CoredumpArgumentWord = 0x3F8, CoredumpBlockWord = 0x3FC;

    /// <summary>The processes this module created (ksceKernelCreateProcess), by UID, and the exit statuses.</summary>
    [SnapshotMissing] // states of builds before it: no process was created here
    readonly Dictionary<int, int> _exitStatuses = new();

    /// <summary>The budget table processmgr keeps (32 slots of 8 words, then a word per slot) and its values 4-7.</summary>
    [SnapshotMissing] // states of builds before it: never filled
    readonly uint[] _budgetTable = new uint[32 * 9];

    [SnapshotMissing] // states of builds before it: never filled
    readonly uint[] _budgetValues = new uint[8];

    /// <summary>The process list lock (a semaphore of 1).</summary>
    [SnapshotIgnore]
    readonly SemaphoreSlim _processListLock = new(1, 1);

    /// <summary>The launches the system is creating through this function's entry (<see cref="CreateLaunchedProcess"/>),
    /// by the kernel thread making the call: a host-side launch that is already loading its executable.</summary>
    [SnapshotIgnore]
    readonly Dictionary<GuestThread, Emulator> _launches = new();

    /// <summary>
    /// The creation of a launch's process as SceAppMgr 3.74 makes it (decompiled, study only): appmgr calls
    /// ksceKernelCreateProcess(title ID, type, executable path, 0x40-byte option) by its address on its own thread, and
    /// launches nothing when it fails. In VitaSharp the launch (<paramref name="launched"/>) is made on the host, so the
    /// call is made through the entry only when kernel code hooked it: <paramref name="call"/> runs on a kernel thread,
    /// the hook first, and the original it continues to answers with the launch's own process instead of making a
    /// second one (its argument checks are the same). Returns what the call returned: the pid, or an error.
    /// </summary>
    internal int CreateLaunchedProcess(Emulator launched, Func<int> call)
    {
        GuestThread thread = GuestThread.Current ?? throw new InvalidOperationException("the launch's process is created on a kernel thread");
        lock (_launches)
        {
            _launches[thread] = launched;
        }

        try
        {
            return call();
        }
        finally
        {
            lock (_launches)
            {
                _launches.Remove(thread);
            }
        }
    }

    /// <summary>The launch the calling thread is creating through the entry, if it is making one.</summary>
    Emulator? LaunchOfCaller()
    {
        if (GuestThread.Current is not { } thread)
        {
            return null;
        }

        lock (_launches)
        {
            return _launches.TryGetValue(thread, out Emulator? launched) ? launched : null;
        }
    }

    // ------------------------------------------------------------------ create, start, exit

    /// <summary>ksceKernelCreateProcess(titleid, type, path, opt): a process for the executable at the path, left
    /// created (its threads wait for the start).</summary>
    [Hle(Nid = 0x68068618, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int ksceKernelCreateProcess(uint titleId, uint type, uint path, uint option)
    {
        if (option == 0)
        {
            return CreateProcess(titleId, type, path, []);
        }

        Span<byte> options = stackalloc byte[ProcessOptionSize];
        GuestMemory.Span(option, ProcessOptionSize).CopyTo(options);
        return CreateProcess(titleId, type, path, options);
    }

    /// <summary>The size of the option of a process creation (the first word of a valid one).</summary>
    internal const int ProcessOptionSize = 0x40;

    /// <summary><see cref="ksceKernelCreateProcess"/> with the option's bytes (empty: none), for the kernel callers
    /// that build it on their stack (SceAppMgrForDriver NID_324DD34E).</summary>
    internal int CreateProcess(uint titleId, uint type, uint path, ReadOnlySpan<byte> option)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        if (titleId == 0 || path == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (Emu.Vita.Processes.Count > 0xF)
        {
            return TooManyProcesses;
        }

        if ((type & 0x1F000000) is not (0x1000000 or 0x2000000 or 0x4000000))
        {
            return KernelProcessRefused;
        }

        if (!option.IsEmpty)
        {
            if (BitConverter.ToUInt32(option) != ProcessOptionSize)
            {
                return IllegalOptionSize;
            }

            uint flags = BitConverter.ToUInt32(option[4..]);
            uint affinity = BitConverter.ToUInt32(option[8..]);
            if ((flags & 0x1000) != 0 && affinity != 0 && ((affinity & 0xFFF0FFF0) != 0 || ((affinity & 0xF0000) != 0 && (affinity & 0xF) != 0)))
            {
                return IllegalAffinityMask;
            }
        }

        if (LaunchOfCaller() is { } launched)
        {
            // the system's launch calling through the entry: its process is the one being launched
            return launched.Pid;
        }

        string guestPath = GuestMemory.ReadCString(path, 0x400);
        string? host = Emu.Vfs.Resolve(guestPath);
        if (host == null || !File.Exists(host) || Path.GetDirectoryName(host) is not { } directory)
        {
            return SceError.MODULEMGR_NOENT;
        }

        Emulator process;
        try
        {
            process = Emu.SpawnProcess(directory, Path.GetFileName(host), [], background: true, createdByProcessmgr: true);
        }
        catch (ProcessLoadException e)
        {
            return e.Error;
        }

        process.Suspend();
        int pid = process.Pid;
        uint block = ProcessObject(pid);
        uint state = GuestMemory.R32(block + StateWord);
        GuestMemory.W32(block + StateWord, (state & 0xFFFFFF00) | CreatedState);
        GuestMemory.W32(block + 0x40, type & 0x1F000000);
        return pid;
    }

    /// <summary>NID_026F3E87(pid, unused, arg size, args, flags): starts a created process (its threads run; the state
    /// becomes running).</summary>
    [Hle(Nid = 0x026F3E87, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_026F3E87(int pid, uint unused, uint argSize, uint args, uint flags) => StartProcess(pid);

    /// <summary>NID_5137B783(pid, unused, arg size, args): the same without flags.</summary>
    [Hle(Nid = 0x5137B783, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_5137B783(int pid, uint unused, uint argSize, uint args) => StartProcess(pid);

    int StartProcess(int pid)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        int uid = ResolvePid(pid);
        if (uid == 0 || ProcessOf(uid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        uint block = ProcessObject(uid);
        uint state = GuestMemory.R32(block + StateWord);
        if ((state & 0xFF) != CreatedState)
        {
            return ProcessStateError;
        }

        GuestMemory.W32(block + StateWord, (state & 0xFFFFFF00) | RunningState);
        process.Resume();
        return 0;
    }

    /// <summary>The checks the exit and kill paths share (0x81002c94 and its family): the kernel's process is
    /// 0x80029002, the caller's own 0x80024001, a process created or ended (state 0, 1 or 4) 0x80029004.</summary>
    int EndableProcess(int pid, out Emulator? process, out uint block)
    {
        process = null;
        block = 0;
        int uid = ResolvePid(pid);
        if (uid == KernelSpace.KernelPid)
        {
            return KernelProcessRefused;
        }

        if (uid == 0 || ProcessOf(uid) is not { } found)
        {
            return SceError.INVALID_PID;
        }

        if (GuestThread.Current is { KernelMode: false } current && current.Pid == uid)
        {
            return ProcessIsCaller;
        }

        block = ProcessObject(uid);
        uint state = GuestMemory.R32(block + StateWord) & 0xFF;
        if (state is 0 or CreatedState or 4)
        {
            return ProcessStateError;
        }

        process = found;
        return 0;
    }

    /// <summary>Ends a process with an exit status: its state becomes 4 (ended) and the emulator's process stops.</summary>
    int EndProcess(int pid, int status)
    {
        int check = EndableProcess(pid, out Emulator? process, out uint block);
        if (check < 0)
        {
            return check;
        }

        lock (_exitStatuses)
        {
            _exitStatuses[process!.Pid] = status;
        }

        GuestMemory.W32(block + 0x94, (uint)status);
        GuestMemory.W32(block + StateWord, (GuestMemory.R32(block + StateWord) & 0xFFFFFF00) | 4);
        process.Kernel.StopAllThreads();
        return 0;
    }

    /// <summary>NID_1400A1F8(pid, flags, timeout): kills a process (flags bits 0-1 only, else
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT; not from an interrupt handler).</summary>
    [Hle(Nid = 0x1400A1F8, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_1400A1F8(int pid, uint flags, uint timeout)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        if ((flags & ~3u) != 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return EndProcess(pid, unchecked((int)0x80029004));
    }

    /// <summary>NID_4232517A(pid): the same without flags.</summary>
    [Hle(Nid = 0x4232517A, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_4232517A(int pid) => NID_1400A1F8ThroughEntry(pid, 0, 0);

    /// <summary>NID_EFBCF15B(pid): terminates a process (not from an interrupt handler).</summary>
    [Hle(Nid = 0xEFBCF15B, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_EFBCF15B(int pid) => SystemServicesVsh.InInterrupt ? IllegalContext : EndProcess(pid, 0);

    /// <summary>NID_8FB7FD50(pid): the same.</summary>
    [Hle(Nid = 0x8FB7FD50, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_8FB7FD50(int pid) => EndProcess(pid, 0);

    /// <summary>NID_F388F05C(pid, mode): the exit of a process (mode 1: exit, 2: exit and spawn; not from an interrupt
    /// handler or an exception).</summary>
    [Hle(Nid = 0xF388F05C, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_F388F05C(int pid, int mode) => SystemServicesVsh.InInterrupt || NID_B615A7DAThroughEntry() != 0 ? IllegalContext : EndProcess(pid, 0);

    /// <summary>NID_148E23E3(pid, mode): the same.</summary>
    [Hle(Nid = 0x148E23E3, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_148E23E3(int pid, int mode) => EndProcess(pid, 0);

    /// <summary>NID_1E5B38E5(titleid, type, path, arg size, args, option, flags): launches a process (processmgr
    /// 3.74 0x81004b48): not from an interrupt handler; creates it (ksceKernelCreateProcess with the type's low 31
    /// bits and the option) and starts it (NID_026F3E87 with the type's bit 31, the arguments and the flags); a start
    /// that fails ends the created process (NID_F388F05C) and gives the start's error. The new process's ID.</summary>
    [Hle(Nid = 0x1E5B38E5, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_1E5B38E5(uint titleId, uint type, uint path, uint argSize, uint args, uint option, uint flags)
    {
        if (option == 0)
        {
            return LaunchProcess(titleId, type, path, argSize, args, [], flags);
        }

        Span<byte> options = stackalloc byte[ProcessOptionSize];
        GuestMemory.Span(option, ProcessOptionSize).CopyTo(options);
        return LaunchProcess(titleId, type, path, argSize, args, options, flags);
    }

    // VSHLE010: 0x81004b48 is processmgr's own function; both exports and LaunchProcess call it (and its create, start and
    // terminate steps) inside the module, by its internal address, not through the exports' entries, so a hook on those
    // entries does not see these calls on the Vita either
#pragma warning disable VSHLE010
    /// <summary>NID_5A57766C(titleid, type, path, arg size, args, option, flags): the same entry (both run
    /// 0x81004b48).</summary>
    [Hle(Nid = 0x5A57766C, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_5A57766C(uint titleId, uint type, uint path, uint argSize, uint args, uint option, uint flags)
        => NID_1E5B38E5(titleId, type, path, argSize, args, option, flags);

    /// <summary>The launch of <see cref="NID_1E5B38E5"/> with the option's bytes (empty: none).</summary>
    internal int LaunchProcess(uint titleId, uint type, uint path, uint argSize, uint args, ReadOnlySpan<byte> option, uint flags)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        int pid = CreateProcess(titleId, type & 0x7FFFFFFF, path, option);
        if (pid < 0)
        {
            return pid;
        }

        int started = NID_026F3E87(pid, type & 0x80000000, argSize, args, flags);
        if (started < 0)
        {
            NID_F388F05C(pid, 0);
            return started;
        }

        return pid;
    }
#pragma warning restore VSHLE010

    /// <summary>NID_0B9BE51B(pid, int *status, timeout): waits until the process ended and gives its exit status.</summary>
    [Hle(Nid = 0x0B9BE51B, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_0B9BE51B(int pid, uint status, Ptr<uint> timeout)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        int uid = ResolvePid(pid);
        if (uid == 0)
        {
            return SceError.INVALID_PID;
        }

        uint block = ProcessObject(uid);
        int result = Wait.Until(_exitStatuses, () => (GuestMemory.R32(block + StateWord) & 0xFF) == 4, timeout, what: "process end");
        if (result < 0)
        {
            return result;
        }

        if (status != 0)
        {
            GuestMemory.W32(status, GuestMemory.R32(block + 0x94));
        }

        return 0;
    }

    /// <summary>NID_0F23A5ED(pid, event, timeout): waits for a process event (2 started, 0x10 stopped, 0x20 exited,
    /// 0x100 ended; any other is SCE_KERNEL_ERROR_INVALID_ARGUMENT).</summary>
    [Hle(Nid = 0x0F23A5ED, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_0F23A5ED(int pid, int processEvent, Ptr<uint> timeout)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        if (processEvent is not (2 or 0x10 or 0x20 or 0x100))
        {
            return SceError.INVALID_ARGUMENT;
        }

        int uid = ResolvePid(pid);
        if (uid == 0 || ProcessOf(uid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        uint block = ProcessObject(uid);
        bool Reached()
        {
            uint state = GuestMemory.R32(block + StateWord) & 0xFF;
            return processEvent switch
            {
                2 => state is RunningState or 4,
                0x10 => process.Suspended || state == 4,
                _ => state == 4,
            };
        }

        return Wait.Until(_exitStatuses, Reached, timeout, what: "process event");
    }

    /// <summary>NID_BC110987(pid, event): waits for the process's stop (event 0x10000 only, else
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT); a process already past its exit is 0x80029004.</summary>
    [Hle(Nid = 0xBC110987, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_BC110987(int pid, int processEvent)
    {
        if (processEvent != 0x10000)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int uid = ResolvePid(pid);
        if (uid == 0 || ProcessOf(uid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        uint block = ProcessObject(uid);
        Wait.Until(_exitStatuses, () => process.Suspended || (GuestMemory.R32(block + StateWord) & 0xFF) == 4, default(Ptr<uint>), what: "process stop");
        return (GuestMemory.R32(block + StateWord) & 0x1000000) != 0 ? ProcessStateError : 0;
    }

    // ------------------------------------------------------------------ the process list and the system root

    /// <summary>NID_8321B827(timeout): takes the process list lock (processmgr's "SceKernelProcessmgrProcLock").</summary>
    [Hle(Nid = 0x8321B827, Library = "SceProcessmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_8321B827(uint timeout)
    {
        using (VitaSharp.Core.Kernel.Scheduler.Block())
        {
            _processListLock.Wait();
        }

        return 0;
    }

    /// <summary>NID_D5DF895D(): lets the process list lock go.</summary>
    [Hle(Nid = 0xD5DF895D, Library = "SceProcessmgrForKernel")]
    int NID_D5DF895D()
    {
        if (_processListLock.CurrentCount == 0)
        {
            _processListLock.Release();
        }

        return 0;
    }

    /// <summary>NID_344DDE0B(pid, kind): tells the system root about the process (with the time); nothing for
    /// VitaSharp's system root to keep.</summary>
    [Hle(Nid = 0x344DDE0B, Library = "SceProcessmgrForKernel")]
    void NID_344DDE0B(int pid, uint kind)
    {
    }

    // ------------------------------------------------------------------ the debugging pairs

    /// <summary>NID_597E6D2C(pid, index, a, b): sets pair <paramref name="index"/> (below 5) of the +0xC table.</summary>
    [Hle(Nid = 0x597E6D2C, Library = "SceProcessmgrForKernel")]
    int NID_597E6D2C(int pid, uint index, uint first, uint second) => WriteTablePair(pid, 0xC, 4, index, first, second, 0);

    /// <summary>NID_B2421F93(pid, index, a, b): sets pair <paramref name="index"/> (below 4) of the +0x10 table.</summary>
    [Hle(Nid = 0xB2421F93, Library = "SceProcessmgrForKernel")]
    int NID_B2421F93(int pid, uint index, uint first, uint second) => WriteTablePair(pid, 0x10, 3, index, first, second, 0);

    /// <summary>NID_9DF739C7(pid, index): sets bit 0 of the second word of pair <paramref name="index"/> (+0x10 table).</summary>
    [Hle(Nid = 0x9DF739C7, Library = "SceProcessmgrForKernel")]
    int NID_9DF739C7(int pid, uint index) => WriteTablePair(pid, 0x10, 3, index, 0, 0, 1);

    /// <summary>NID_A2EE1B7F(pid, index): clears that bit.</summary>
    [Hle(Nid = 0xA2EE1B7F, Library = "SceProcessmgrForKernel")]
    int NID_A2EE1B7F(int pid, uint index) => WriteTablePair(pid, 0x10, 3, index, 0, 0, 2);

    /// <summary>Writes a debugging pair (<paramref name="bitMode"/> 0: both words, 1: sets bit 0 of the second, 2:
    /// clears it); an index above <paramref name="lastIndex"/> is SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    int WriteTablePair(int pid, uint pointerWord, uint lastIndex, uint index, uint first, uint second, int bitMode)
    {
        if (!DebugConsole(DebugDipsw))
        {
            return NotAllowed;
        }

        if (index > lastIndex)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return WithProcess(pid, block =>
        {
            uint table = GuestMemory.R32(block + pointerWord);
            uint pair = table + index * 8;
            if (bitMode == 0)
            {
                GuestMemory.W32(pair + 8, first);
                GuestMemory.W32(pair + 0xC, second);
            }
            else if (bitMode == 1)
            {
                GuestMemory.W32(pair + 0xC, GuestMemory.R32(pair + 0xC) | 1);
            }
            else
            {
                GuestMemory.W32(pair + 0xC, GuestMemory.R32(pair + 0xC) & ~1u);
            }

            return 0;
        });
    }

    // ------------------------------------------------------------------ the coredump handler

    /// <summary>NID_21A6F0EC(handler, stack size, uint *arg): registers the caller's process's coredump handler (a
    /// stack of at least 0x1000 bytes, else SCE_KERNEL_ERROR_ILLEGAL_SIZE; one only: 0x800A0002).</summary>
    [Hle(Nid = 0x21A6F0EC, Library = "SceProcessmgrForDriver")]
    int NID_21A6F0EC(uint handler, uint stackSize, uint argument)
    {
        if (handler == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (stackSize < 0x1000)
        {
            return SceError.ILLEGAL_SIZE;
        }

        return WithProcess(0, block =>
        {
            if (GuestMemory.R32(block + CoredumpHandlerWord) != 0)
            {
                return CoredumpRegistered;
            }

            GuestMemory.W32(block + CoredumpHandlerWord, handler);
            GuestMemory.W32(block + CoredumpStackWord, stackSize);
            GuestMemory.W32(block + CoredumpArgumentWord, argument);
            GuestMemory.W32(block + CoredumpBlockWord, 0);
            return 0;
        });
    }

    /// <summary>NID_33B3D026(): unregisters it (none: 0x800A0001; not from a thread with attribute bit 23).</summary>
    [Hle(Nid = 0x33B3D026, Library = "SceProcessmgrForDriver")]
    int NID_33B3D026()
    {
        if (GuestThread.Current is { } current && (current.Attr & 0x800000) != 0)
        {
            return IllegalContext;
        }

        return WithProcess(0, block =>
        {
            if (GuestMemory.R32(block + CoredumpHandlerWord) == 0)
            {
                return CoredumpNotRegistered;
            }

            GuestMemory.W32(block + CoredumpHandlerWord, 0);
            GuestMemory.W32(block + CoredumpStackWord, 0);
            GuestMemory.W32(block + CoredumpArgumentWord, 0);
            return 0;
        });
    }

    /// <summary>NID_B559410A(pid, a, b): runs the process's coredump handler on a thread of the process
    /// ("SceKernelCoredumpHandler") with its argument; no handler is 0x800A0001.</summary>
    [Hle(Nid = 0xB559410A, Library = "SceProcessmgrForDriver")]
    int NID_B559410A(int pid, uint first, uint second)
    {
        int uid = ResolvePid(pid);
        if (uid == 0 || ProcessOf(uid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        uint block = ProcessObject(uid);
        uint handler = GuestMemory.R32(block + CoredumpHandlerWord);
        if (handler == 0)
        {
            return CoredumpNotRegistered;
        }

        ThreadMgr threads = process.Hle.Modules.OfType<ThreadMgr>().First();
        int thread;
        using (ProcessScope.Enter(uid))
        {
            thread = threads.sceKernelCreateThread("SceKernelCoredumpHandler", handler, 0x40, (int)GuestMemory.R32(block + CoredumpStackWord), 0x800000, 0, 0);
        }

        if (thread < 0)
        {
            return thread;
        }

        return process.Kernel.GetThread(thread) is { } created ? created.Start(0, GuestMemory.R32(block + CoredumpArgumentWord)) : SceError.UNKNOWN_THREAD_ID;
    }

    // ------------------------------------------------------------------ more process words

    /// <summary>NID_8CB01675(pid, SceUInt64 *out, SceUInt64 time): the process time of a system time (pid 0: the
    /// caller's process). VitaSharp's processes count their time from the system's, so it is the same value.</summary>
    [Hle(Nid = 0x8CB01675, Library = "SceProcessmgrForDriver")]
    int NID_8CB01675(int pid, uint output, ulong time)
    {
        if (output == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        return WithProcess(pid, _ =>
        {
            GuestMemory.Write(output, time);
            return 0;
        });
    }

    /// <summary>NID_6599E5D9(pid, value): writes the word into the process at the address its object keeps (+0x1F8).</summary>
    [Hle(Nid = 0x6599E5D9, Library = "SceProcessmgrForDriver")]
    int NID_6599E5D9(int pid, uint value)
    {
        return WithProcess(pid, block =>
        {
            uint address = GuestMemory.R32(block + 0x1F8);
            if (address == 0 || !GuestMemory.IsMapped(address))
            {
                return SceError.ILLEGAL_ADDR;
            }

            GuestMemory.W32(address, value);
            return 0;
        });
    }

    /// <summary>NID_B80DBD53(pid, info): {size 0x10, +0x42C, +0x430, 0}; another size is SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0xB80DBD53, Library = "SceProcessmgrForDriver")]
    int NID_B80DBD53(int pid, uint info)
    {
        if (GuestMemory.R32(info) != 0x10)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return WithProcess(pid, block =>
        {
            GuestMemory.W32(info + 4, GuestMemory.R32(block + 0x42C));
            GuestMemory.W32(info + 8, GuestMemory.R32(block + 0x430));
            GuestMemory.W32(info + 0xC, 0);
            return 0;
        });
    }

    /// <summary>NID_C715591F(): 1 when a process is being debugged (bit 4 of its object's +0x4 word), else 0.</summary>
    [Hle(Nid = 0xC715591F, Library = "SceProcessmgrForDriver")]
    int NID_C715591F()
    {
        lock (_processObjects)
        {
            foreach (uint block in _processObjects.Values)
            {
                if ((GuestMemory.R32(block + 4) & 0x10) != 0)
                {
                    return 1;
                }
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ budgets

    /// <summary>NID_923AF3CC(index): budget value 1-7 (1-3 the system memory's, 4-7 processmgr's); 0 otherwise.</summary>
    [Hle(Nid = 0x923AF3CC, Library = "SceProcessmgrForKernel", ReturnsValue = true)]
    uint NID_923AF3CC(uint index) => index is >= 1 and <= 7 ? _budgetValues[index] : 0;

    /// <summary>NID_C5CF15ED(slot, info): slot <paramref name="slot"/> (below 32) of the budget table into an info of
    /// 0x28 bytes (8 words and the slot's word); anything else is SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0xC5CF15ED, Library = "SceProcessmgrForDriver")]
    int NID_C5CF15ED(uint slot, uint info)
    {
        if (slot >= 0x20 || GuestMemory.R32(info) != 0x28)
        {
            return SceError.INVALID_ARGUMENT;
        }

        for (uint i = 0; i < 8; i++)
        {
            GuestMemory.W32(info + 4 + i * 4, _budgetTable[slot * 8 + i]);
        }

        GuestMemory.W32(info + 0x24, _budgetTable[0x100 + slot]);
        return 0;
    }

    /// <summary>NID_56657302(): the default budget's UID (none is made by VitaSharp's boot: 0).</summary>
    [Hle(Nid = 0x56657302, Library = "SceProcessmgrForKernel", ReturnsValue = true)]
    uint NID_56657302() => _budgetValues[4];

    /// <summary>The budget calls refused to a thread without the kernel's permissions: SCE_ERRNO_ENOSYS.</summary>
    bool KernelPermission() => Emu.Hle.Modules.OfType<ThreadmgrKernel>().First().CurrentPermission() is 0x40 or 0x80;

    /// <summary>NID_DC18FCEF(name, type, info): creates a budget object (kernel permissions only; info of 0x18 bytes).</summary>
    [Hle(Nid = 0xDC18FCEF, Library = "SceProcessmgrForKernel")]
    int NID_DC18FCEF(uint name, uint type, uint info)
    {
        if (!KernelPermission())
        {
            return SceError.ERRNO_ENOSYS;
        }

        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        if (info == 0 || name == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0x18)
        {
            return IllegalOptionSize;
        }

        var budget = new KernelData { Name = GuestMemory.ReadCString(name, 0x20), Kind = BudgetKind, Address = type, Aux = info };
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return K.Objects.Register(budget);
        }
    }

    const string BudgetKind = "procbudget";

    /// <summary>NID_459A223B(uid): deletes a budget object (kernel permissions only; UIDs 0-3 and a budget in use
    /// 0x80029006).</summary>
    [Hle(Nid = 0x459A223B, Library = "SceProcessmgrForKernel")]
    int NID_459A223B(int uid)
    {
        if (!KernelPermission())
        {
            return SceError.ERRNO_ENOSYS;
        }

        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        if ((uint)uid < 4 || K.Objects.Get<KernelData>(uid) is not { Kind: BudgetKind })
        {
            return UnknownBudget;
        }

        K.Objects.Remove(uid);
        return 0;
    }

    /// <summary>NID_48DD3348(uid, which, *out): word 0, 1, 2 or 5 of a budget object (another is
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT, a null out the same).</summary>
    [Hle(Nid = 0x48DD3348, Library = "SceProcessmgrForKernel")]
    int NID_48DD3348(int uid, uint which, uint output)
    {
        if (output == 0 || which is not (0 or 1 or 2 or 5))
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<KernelData>(uid) is not { Kind: BudgetKind } budget)
        {
            return UnknownBudget;
        }

        GuestMemory.W32(output, which == 0 ? budget.Address : 0);
        return 0;
    }

    /// <summary>NID_CF9EC344(uid, level, info): a budget object's info (0x480 bytes: its UID, name, type).</summary>
    [Hle(Nid = 0xCF9EC344, Library = "SceProcessmgrForKernel")]
    int NID_CF9EC344(int uid, uint level, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0x480)
        {
            return IllegalOptionSize;
        }

        if (K.Objects.Get<KernelData>(uid) is not { Kind: BudgetKind } budget)
        {
            return UnknownBudget;
        }

        GuestMemory.Fill(info + 4, 0, 0x47C);
        GuestMemory.W32(info + 4, (uint)uid);
        byte[] name = System.Text.Encoding.UTF8.GetBytes(budget.Name);
        name.AsSpan(0, Math.Min(name.Length, 0x1F)).CopyTo(GuestMemory.Span(info + 8, 0x1F));
        GuestMemory.W32(info + 0x28, budget.Address);
        return 0;
    }

    /// <summary>NID_23EF9D8E(level, buf, size, int *count): the infos of every budget object (0x480 each).</summary>
    [Hle(Nid = 0x23EF9D8E, Library = "SceProcessmgrForKernel")]
    int NID_23EF9D8E(uint level, uint buffer, uint size, uint count)
    {
        if (count == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.W32(count, 0);
        List<int> budgets = new();
        foreach (KernelData data in K.Objects.All<KernelData>())
        {
            if (data.Kind == BudgetKind)
            {
                budgets.Add(data.Uid);
            }
        }

        budgets.Sort();
        uint fits = Math.Min((uint)budgets.Count, size / 0x480);
        for (uint i = 0; i < fits; i++)
        {
            GuestMemory.W32(buffer + i * 0x480, 0x480);
            NID_CF9EC344ThroughEntry(budgets[(int)i], level, buffer + i * 0x480);
        }

        GuestMemory.W32(count, fits);
        return 0;
    }

    /// <summary>NID_46221964(pid, info): the process's budget info (0x88 bytes; pid 0 the caller's process, which must be
    /// a game or system application); a null info is SCE_KERNEL_ERROR_INVALID_ARGUMENT, another size 0x80020009.</summary>
    [Hle(Nid = 0x46221964, Library = "SceProcessmgrForDriver")]
    int NID_46221964(int pid, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0x88)
        {
            return IllegalOptionSize;
        }

        return WithProcess(pid, block =>
        {
            GuestMemory.Fill(info + 8, 0, 0x80);
            GuestMemory.W32(info + 4, GuestMemory.R32(block + 0x40));
            return 0;
        });
    }
}
