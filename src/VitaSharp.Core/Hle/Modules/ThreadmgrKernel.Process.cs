using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The last SceThreadmgrForKernel/ForDriver entries of the FW 3.74 threadmgr.skprx (decompiled, study only):
/// <list type="bullet">
/// <item>the parts of a process the process manager asks the thread manager for: its thread manager setup
/// (NID_B46502E6: the "tworker" event flag and thread of the process object), the end of its worker
/// (NID_5A23A1BE), the cleanup of its objects (NID_205506FC), its timers held and let go (NID_7425185E,
/// NID_89C17825), its ready threads taken off the run queues (NID_01369E06), the stop event its handler gets
/// (NID_37F51E91), a thread created in it (NID_C8E57BB4) and the thread kind of its process type (NID_D9C097DD);</item>
/// <item>more object calls: create an RW lock (NID_04150799), delete a timer (NID_91A8F38B), cancel a timer's waits
/// (NID_F285C94F), cancel an event's waits and set its pattern (NID_E5CAD3E2), the time left to a timer's event
/// (NID_3634FFE6), a message pipe's word (NID_88206D29), a thread event handler (NID_FD6150D5), a class derived from
/// the event class (NID_DBB77032), a work queue of one's own (NID_771522C6);</item>
/// <item>the scheduler's per-CPU words (NID_419E6736 the system time pair, NID_6C1F092F, NID_BB56D42F,
/// NID_FEF07EC0 the switch at the end of an interrupt, NID_86DAE59B the active CPUs for the kernel's own threads) and
/// the trace hooks (NID_0AE9C713, NID_4F2B45DC).</item>
/// </list>
/// VitaSharp's processes are <see cref="Emulator"/>s: the process object block is the process manager's
/// (<see cref="ProcessmgrKernel"/>), stopping a process's threads is <see cref="Emulator.Suspend"/>, and the
/// scheduler runs guest threads on host threads, so a switch request has nothing to switch.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    const uint UserCreateRWLock = 0x8667951D, UserDeleteTimer = 0xAB1E42C4, UserCancelTimer = 0x9CCF768C,
        UserRegisterThreadEventHandler = 0x6D8C0F13, UserCancelEventWithSetPattern = 0xB01387D5, KernelCreateThread = 0xC6674E7D;

    const int TimerNoEvent = unchecked((int)0x80028242), ErrnoNoSys = unchecked((int)0x80010058);

    /// <summary>The processes whose stop event was sent (the firmware's once flag at +0x34C of the process object).</summary>
    [SnapshotMissing] // states of builds before it: no stop event was sent
    readonly HashSet<int> _processStopSent = new();

    /// <summary>The process workers by thread UID, with the process object each serves (its event flag is at +0x334): a
    /// save state's workers, given back with their saved UIDs, find their process in it.</summary>
    [SnapshotMissing] // states of builds before it: their workers keep the flag they were made with
    readonly Dictionary<int, uint> _processWorkers = new();

    /// <summary>The process workers running in this session (save states: the ones a load lets go).</summary>
    [SnapshotIgnore]
    readonly HashSet<GuestThread> _runningProcessWorkers = new();

    /// <summary>The trace hooks the two setters keep (+0x28A8 and +0x28AC of the data block).</summary>
    const uint GlobalsTraceExit = 0x28A8, GlobalsTraceEntry = 0x28AC;

    // ------------------------------------------------------------------ the process manager's calls

    /// <summary>NID_B46502E6(process object): the thread manager's part of a new process: its lists, an event flag
    /// ("tworker: name") at +0x334 and a worker thread at +0x330; 0, or the error of the event flag or the thread.</summary>
    [Hle(Nid = 0xB46502E6, Library = "SceThreadmgrForKernel")]
    int NID_B46502E6(uint process)
    {
        GuestMemory.W32(process + 0x30, process + 0x30);
        GuestMemory.W32(process + 0x34, process + 0x30);
        GuestMemory.W32(process + 0x2D0, process + 0x2D0);
        GuestMemory.W32(process + 0x2D4, process + 0x2D0);
        GuestMemory.W32(process + 0x38, 0);
        string name = $"tworker: {K.Objects.Get<KernelObject>((int)GuestMemory.R32(process + 0x64))?.Name ?? ""}";
        if (name.Length > 31)
        {
            name = name[..31];
        }

        int flag = UserThreadmgr.sceKernelCreateEventFlag(name, 0, 0, 0);
        GuestMemory.W32(process + 0x334, (uint)flag);
        if (flag < 0)
        {
            return flag;
        }

        GuestThread worker = K.CreateThread(name, 0, 0xFE, 0x1000, 0, 0xF);
        worker.KernelMode = true;
        worker.HostBody = self => ProcessWorker(self, flag);
        GuestMemory.W32(process + 0x330, (uint)worker.Uid);
        lock (_processWorkers)
        {
            _processWorkers[worker.Uid] = process;
            _runningProcessWorkers.Add(worker);
        }

        return worker.Start(0, 0) < 0 ? -1 : 0;
    }

    /// <summary>Save states: a saved process worker this session does not run (the process was made after the moment
    /// this session is at) is a worker again, of the process its loaded record names.</summary>
    bool AdoptProcessWorker(GuestThread thread)
    {
        if (!ReferenceEquals(thread.Process, Emu))
        {
            return false;
        }

        lock (_processWorkers)
        {
            if (!_processWorkers.ContainsKey(thread.Uid))
            {
                return false;
            }

            thread.KernelMode = true;
            thread.HostBody = self => ProcessWorker(self, -1);
            _runningProcessWorkers.Add(thread);
        }

        return true;
    }

    /// <summary>Save states: a process worker the loaded state does not have (its process was made after the save) is
    /// let go: it stops waiting and ends.</summary>
    bool ReleaseProcessWorker(GuestThread thread)
    {
        lock (_processWorkers)
        {
            return _runningProcessWorkers.Remove(thread);
        }
    }

    /// <summary>The event flag the worker waits on: its process's (the loaded one after a state load: the worker's UID
    /// is the saved one), else the one it was made with.</summary>
    int ProcessWorkerFlag(GuestThread self, int madeWith)
    {
        lock (_processWorkers)
        {
            return _processWorkers.TryGetValue(self.Uid, out uint process) ? (int)GuestMemory.R32(process + 0x334) : madeWith;
        }
    }

    /// <summary>The process worker's wake bits: bit 31 ends it (NID_5A23A1BE), bit 0 asks for the process's debug
    /// handler (none in VitaSharp).</summary>
    const uint WorkerEndBit = 0x80000000, WorkerHandlerBit = 1;

    /// <summary>The process worker's wait (threadmgr 3.74 processWorkerThread): 16 ms at most per wait.</summary>
    const uint WorkerWaitUs = 16000;

    /// <summary>The process's worker (threadmgr 3.74 processWorkerThread): waits on its event flag for bit 31 or bit 0
    /// (OR, clearing the pattern) at most 16 ms at a time; a timeout waits again, bit 31 or any other error ends
    /// it.</summary>
    void ProcessWorker(GuestThread self, int flag)
    {
        uint words = Emu.Memory.Alloc(8, "kernel:tworker-wait");
        try
        {
            while (!self.Core.StopRequested)
            {
                GuestMemory.W32(words, 0);
                GuestMemory.W32(words + 4, WorkerWaitUs);
                int wait;
                // blocked in the host while it waits: a save state's safe point (the worker keeps nothing in guest
                // registers; a load gives it back or lets it go, IServiceThreadOwner). Before, a save with a process
                // worker alive was refused ("thread 'tworker: ...' (host) is busy")
                using (Scheduler.Block())
                {
                    wait = UserThreadmgr.sceKernelWaitEventFlag(ProcessWorkerFlag(self, flag), WorkerEndBit | WorkerHandlerBit, EventFlag.WaitOr | EventFlag.WaitClear,
                        new Ptr<uint>(words), new Ptr<uint>(words + 4));
                }

                if (wait == SceError.WAIT_TIMEOUT)
                {
                    continue;
                }

                if (wait < 0 || (GuestMemory.R32(words) & WorkerEndBit) != 0)
                {
                    return;
                }
            }
        }
        finally
        {
            Emu.Memory.Free(words);
        }
    }

    /// <summary>NID_5A23A1BE(process object): ends the process's worker (sets bit 31 of its flag, waits for it and
    /// deletes both).</summary>
    [Hle(Nid = 0x5A23A1BE, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    void NID_5A23A1BE(uint process)
    {
        int flag = (int)GuestMemory.R32(process + 0x334);
        if (flag == -1)
        {
            return;
        }

        UserThreadmgr.sceKernelSetEventFlag(flag, 0x80000000);
        int worker = (int)GuestMemory.R32(process + 0x330);
        if (worker != -1 && K.Objects.Get<GuestThread>(worker) is { } thread)
        {
            thread.WaitForExit(30_000);
            K.Objects.Remove(worker);
            lock (_processWorkers)
            {
                _runningProcessWorkers.Remove(thread);
            }
        }

        lock (_processWorkers)
        {
            _processWorkers.Remove(worker);
        }

        UserThreadmgr.sceKernelDeleteEventFlag(flag);
    }

    /// <summary>NID_205506FC(process object): the cleanup of the process's thread manager objects; the firmware
    /// deletes every object of each class the process holds, VitaSharp's are deleted with the process
    /// (<see cref="Emulator"/>'s end), so nothing is left here.</summary>
    [Hle(Nid = 0x205506FC, Library = "SceThreadmgrForKernel")]
    void NID_205506FC(uint process)
    {
    }

    /// <summary>NID_7425185E(process object): holds the process's timers (they stop counting); 0.</summary>
    [Hle(Nid = 0x7425185E, Library = "SceThreadmgrForKernel")]
    int NID_7425185E(uint process) => HoldTimers(process, hold: true);

    /// <summary>NID_89C17825(process object): lets the process's timers count again; 0.</summary>
    [Hle(Nid = 0x89C17825, Library = "SceThreadmgrForKernel")]
    int NID_89C17825(uint process) => HoldTimers(process, hold: false);

    int HoldTimers(uint process, bool hold)
    {
        int pid = (int)GuestMemory.R32(process + 0x64);
        foreach (TimerObject timer in K.Objects.All<TimerObject>())
        {
            if (timer.OwnerPid != pid)
            {
                continue;
            }

            if (hold)
            {
                timer.Stop();
            }
            else
            {
                timer.Start();
            }
        }

        return 0;
    }

    /// <summary>NID_01369E06(pid): takes the process's threads off the run queues (they stop where they are).</summary>
    [Hle(Nid = 0x01369E06, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_01369E06(int pid)
    {
        if (KernelSpace.ProcessOf(Emu, pid) is { Suspended: false } process)
        {
            process.Suspend();
        }

        return 0;
    }

    /// <summary>NID_37F51E91(pid): sends the process's stop event once: the process event handler the application
    /// manager registered (+0x23E8) is called with (pid, -1, 1, its argument); 0x80027101 without a current thread or
    /// from a thread with attribute bit 31.</summary>
    [Hle(Nid = 0x37F51E91, Library = "SceThreadmgrForDriver")]
    int NID_37F51E91(int pid)
    {
        if (GuestThread.Current is not { } current || (current.Attr & 0x80000000) != 0)
        {
            return IllegalContext;
        }

        if (KernelSpace.ProcessOf(Emu, pid) is null)
        {
            return SceError.INVALID_PID;
        }

        lock (_processStopSent)
        {
            if (!_processStopSent.Add(pid))
            {
                return 0;
            }
        }

        uint handler = GuestMemory.R32(Globals() + GlobalsProcessHandler);
        if (handler != 0)
        {
            current.CallGuest(handler, (uint)pid, 0xFFFFFFFF, 1, GuestMemory.R32(Globals() + GlobalsProcessHandlerArgument));
        }

        return 0;
    }

    /// <summary>NID_C8E57BB4(pid, name, entry, priority, stack size, attr, affinity, opt): a thread of another process;
    /// pid 0 or the kernel's is SCE_KERNEL_ERROR_INVALID_ARGUMENT, an affinity outside the cores
    /// SCE_KERNEL_ERROR_ILLEGAL_CPU_AFFINITY_MASK.</summary>
    [Hle(Nid = 0xC8E57BB4, Library = "SceThreadmgrForDriver")]
    int NID_C8E57BB4(int pid, string name, uint entry, int priority, int stackSize, uint attr, uint affinity, uint option)
    {
        if (pid == 0 || pid == KernelSpace.KernelPid)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (KernelSpace.ProcessOf(Emu, pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        if ((affinity & 0xFFF0FFF0) != 0 || ((affinity & 0xF0000) != 0 && (affinity & 0xF) != 0))
        {
            return SceError.ILLEGAL_CPU_AFFINITY_MASK;
        }

        if ((affinity & 0xF0000) != 0)
        {
            affinity >>= 16;
        }

        ThreadMgr threads = process.Hle.Modules.OfType<ThreadMgr>().First();
        using (ProcessScope.Enter(pid))
        {
            return threads.sceKernelCreateThread(name, entry, priority, stackSize, attr | 0x80000000, (int)affinity, option);
        }
    }

    /// <summary>NID_D9C097DD(process params): the thread kind of a process type (+0x14): 7 for 0x1000000, 8 for
    /// 0x2000000 and 0x4000000, 15 for the kernel's 0x5000000; any other type is a fatal kernel error.</summary>
    [Hle(Nid = 0xD9C097DD, Library = "SceThreadmgrForKernel")]
    int NID_D9C097DD(uint parameters)
    {
        return GuestMemory.R32(parameters + 0x14) switch
        {
            0x1000000 => 7,
            0x2000000 or 0x4000000 => 8,
            0x5000000 => 0xF,
            uint other => throw new InvalidOperationException($"kernel panic: unknown process type 0x{other:X}"),
        };
    }

    // ------------------------------------------------------------------ more object calls

    /// <summary>NID_04150799(name, attr, opt): creates an RW lock.</summary>
    [Hle(Nid = 0x04150799, Library = "SceThreadmgrForDriver")]
    void NID_04150799(HleContext context) => RunExport(context, UserCreateRWLock);

    /// <summary>NID_91A8F38B(uid): deletes a timer (its waits end with the deleted state); not from an interrupt
    /// handler.</summary>
    [Hle(Nid = 0x91A8F38B, Library = "SceThreadmgrForDriver")]
    void NID_91A8F38B(HleContext context) => RunOutsideInterrupt(context, UserDeleteTimer);

    /// <summary>NID_F285C94F(uid, int *waiting): stops the timer's event and cancels its waits.</summary>
    [Hle(Nid = 0xF285C94F, Library = "SceThreadmgrForDriver")]
    void NID_F285C94F(HleContext context) => RunExport(context, UserCancelTimer);

    /// <summary>NID_E5CAD3E2(uid, pattern, SceUInt64 userData, int *waiting): cancels the waits on an event and gives
    /// it the pattern and user data (the user call's code); a pattern with bit 16 (the deleted state) is
    /// SCE_KERNEL_ERROR_ILLEGAL_PATTERN.</summary>
    [Hle(Nid = 0xE5CAD3E2, Library = "SceThreadmgrForDriver")]
    void NID_E5CAD3E2(HleContext context)
    {
        if ((context.Gpr(1) & 0x10000) != 0)
        {
            context.Return(SceError.ILLEGAL_PATTERN);
            return;
        }

        RunExport(context, UserCancelEventWithSetPattern);
    }

    /// <summary>NID_3634FFE6(uid, SceUInt64 *left): the time left until the timer's event; no event set is 0x80028242.</summary>
    [Hle(Nid = 0x3634FFE6, Library = "SceThreadmgrForDriver")]
    int NID_3634FFE6(int uid, uint left)
    {
        if (left == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (K.Objects.Get<TimerObject>(uid) is not { } timer)
        {
            return SceError.UNKNOWN_TIMER_ID;
        }

        lock (timer)
        {
            if (!timer.EventSet || !timer.HasPending)
            {
                return TimerNoEvent;
            }

            ulong count = timer.Count();
            ulong next = Volatile.Read(ref timer.NextEvent);
            GuestMemory.Write(left, next > count ? next - count : 0UL);
        }

        return 0;
    }

    /// <summary>NID_88206D29(uid): the message pipe's +0x64 word (its buffer size as the firmware keeps it); not a
    /// message pipe is SCE_KERNEL_ERROR_UNKNOWN_MSG_PIPE_ID.</summary>
    [Hle(Nid = 0x88206D29, Library = "SceThreadmgrForDriver")]
    int NID_88206D29(int uid) => K.Objects.Get<MsgPipe>(uid) is { } pipe ? pipe.BufferSize : SceError.UNKNOWN_MSG_PIPE_ID;

    /// <summary>NID_FD6150D5(name, target, mask, handler, arg): registers a thread event handler (the kernel's entry,
    /// the user call's checks).</summary>
    [Hle(Nid = 0xFD6150D5, Library = "SceThreadmgrForDriver")]
    void NID_FD6150D5(HleContext context) => RunExport(context, UserRegisterThreadEventHandler);

    /// <summary>NID_DBB77032(cls, name, itemSize, create, destroy): initialises a class derived from the thread
    /// manager's event class (SceUIDEventClass), as asynchronous I/O does; a failure is a fatal kernel error. The class
    /// record is made by the system memory manager (ksceKernelCreateClass) with the event class as its parent.</summary>
    [Hle(Nid = 0xDBB77032, Library = "SceThreadmgrForDriver")]
    unsafe void NID_DBB77032(HleContext context)
    {
        uint cls = context.Gpr(0), name = context.Gpr(1), itemSize = context.Gpr(2), create = context.Gpr(3);
        uint destroy = context.Gpr(4);
        uint parent = EventClassRecord();
        uint r4 = context.Cpu->R[4];
        context.Cpu->R[0] = cls;
        context.Cpu->R[1] = name;
        context.Cpu->R[2] = parent;
        context.Cpu->R[3] = itemSize;
        uint sp = context.Cpu->R[13];
        uint saved0 = GuestMemory.R32(sp), saved1 = GuestMemory.R32(sp + 4);
        GuestMemory.W32(sp, create);
        GuestMemory.W32(sp + 4, destroy);
        RunExport(context, KernelCreateClass);
        GuestMemory.W32(sp, saved0);
        GuestMemory.W32(sp + 4, saved1);
        context.Cpu->R[4] = r4;
        if ((int)context.Cpu->R[0] < 0)
        {
            throw new InvalidOperationException($"kernel panic: event class 0x{cls:X8} not made");
        }
    }

    /// <summary>ksceKernelCreateClass (SceSysmemForKernel).</summary>
    const uint KernelCreateClass = 0x61317102;

    /// <summary>The record of the thread manager's event class (made with the first class derived from it).</summary>
    [SnapshotMissing] // states of builds before it: no class was derived
    uint _eventClass;

    uint EventClassRecord()
    {
        lock (_globalsLock)
        {
            if (_eventClass != 0)
            {
                return _eventClass;
            }

            using (ProcessScope.Enter(KernelSpace.KernelPid))
            {
                _eventClass = Mem.Alloc(0x40, "kernel:SceUIDEventClass", MemoryRegion.Kernel);
                uint name = Mem.Alloc(0x20, "kernel:SceUIDEventClass-name", MemoryRegion.Kernel);
                GuestMemory.WriteCString(name, "SceUIDEventClass");
                GuestMemory.Fill(_eventClass, 0, 0x40);
                GuestMemory.W32(_eventClass + 0x0C, name);
                GuestMemory.Write(_eventClass + 0x18, (ushort)0x38);
            }

            return _eventClass;
        }
    }

    /// <summary>NID_771522C6(name, flags, opt): a work queue of the caller's own; the option (at most 0x14 bytes:
    /// size, worker count up to 4 with flag bit 0, priority with bit 1, stack size with bit 2, CPU mask with bit 3)
    /// is checked as the firmware does; flags outside 0x200F are SCE_KERNEL_ERROR_ILLEGAL_ATTR. VitaSharp's kernel
    /// modules share the global queue, so a new queue runs its tasks on the global queue's worker in order: its UID is
    /// a work queue object the queue calls accept.</summary>
    [Hle(Nid = 0x771522C6, Library = "SceThreadmgrForDriver")]
    int NID_771522C6(string name, uint flags, uint option)
    {
        if (GuestThread.Current == null)
        {
            return IllegalContext;
        }

        if ((flags & 0xFFFFDFF0) != 0)
        {
            return SceError.ILLEGAL_ATTR;
        }

        if (option != 0)
        {
            if (GuestMemory.R32(option) > 0x14)
            {
                return SceError.ILLEGAL_SIZE;
            }

            if ((flags & 1) != 0 && GuestMemory.R32(option + 4) > 4)
            {
                return SceError.ILLEGAL_COUNT;
            }
        }

        var queue = new KernelData { Name = name, Kind = WorkQueueKind };
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return K.Objects.Register(queue);
        }
    }

    /// <summary>The kind of a work queue made by NID_771522C6.</summary>
    const string WorkQueueKind = "workqueue";

    /// <summary>A UID the work queue calls accept: the global queue or one NID_771522C6 made.</summary>
    bool IsWorkQueue(int queue) => queue == GlobalWorkQueue || K.Objects.Get<KernelData>(queue) is { Kind: WorkQueueKind };

    // ------------------------------------------------------------------ per-CPU words and trace hooks

    /// <summary>NID_419E6736(SceUInt64 *time): the system time pair the timer interrupt keeps (read twice until both
    /// halves agree on the hardware); a null pointer is SCE_KERNEL_ERROR_ILLEGAL_ADDR.</summary>
    [Hle(Nid = 0x419E6736, Library = "SceThreadmgrForDriver")]
    int NID_419E6736(uint time)
    {
        if (time == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        GuestMemory.Write(time, K.ProcessTimeUs);
        return 0;
    }

    /// <summary>NID_6C1F092F(uint out[2]): two words of the current CPU's scheduler block (+0xC, +0x10): the current
    /// thread's control block and 0; null is 0x80020005.</summary>
    [Hle(Nid = 0x6C1F092F, Library = "SceThreadmgrForKernel")]
    int NID_6C1F092F(uint output)
    {
        if (output == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.W32(output, CurrentControlBlock());
        GuestMemory.W32(output + 4, 0);
        return 0;
    }

    /// <summary>NID_BB56D42F(): whether the current CPU may switch to another thread now: not while dispatch is
    /// disabled (interrupts masked), and only when a switch is due; VitaSharp's host-threaded scheduler never has one
    /// due, so the answer is false (true only without a current thread).</summary>
    [Hle(Nid = 0xBB56D42F, Library = "SceThreadmgrForKernel", ReturnsValue = true)]
    bool NID_BB56D42F() => GuestThread.Current == null && !Sysmem.InterruptsMaskedForCurrentThread;

    /// <summary>NID_FEF07EC0(a, b): the switch the end of an interrupt makes to the thread the scheduler picked; the
    /// caller goes on (its host thread is its CPU).</summary>
    [Hle(Nid = 0xFEF07EC0, Library = "SceThreadmgrForKernel")]
    void NID_FEF07EC0(uint first, uint second)
    {
        Wait.Sleep(0);
    }

    /// <summary>NID_86DAE59B(): the active CPU mask (the four cores) for a thread with the kernel's permissions (0x40
    /// or 0x80, the control block's +0x2C: see NID_332E127C); any other thread gets SCE_ERRNO_ENOSYS.</summary>
    [Hle(Nid = 0x86DAE59B, Library = "SceThreadmgrForDriver")]
    int NID_86DAE59B()
    {
        int permission = NID_332E127C();
        return permission is 0x40 or KernelPermission ? 0xF : ErrnoNoSys;
    }

    /// <summary>NID_0AE9C713(hook): the entry trace hook (+0x28AC; the firmware also makes its trace heap and marks
    /// every thread). Answers 0.</summary>
    [Hle(Nid = 0x0AE9C713, Library = "SceThreadmgrForKernel")]
    int NID_0AE9C713(uint hook)
    {
        GuestMemory.W32(Globals() + GlobalsTraceEntry, hook);
        return 0;
    }

    /// <summary>NID_4F2B45DC(hook): the exit trace hook (+0x28A8). Answers 0.</summary>
    [Hle(Nid = 0x4F2B45DC, Library = "SceThreadmgrForKernel")]
    int NID_4F2B45DC(uint hook)
    {
        GuestMemory.W32(Globals() + GlobalsTraceExit, hook);
        return 0;
    }
}
