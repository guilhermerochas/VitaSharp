using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ksceKernelEnqueueWorkQueue as the FW 3.74 threadmgr.skprx has it (decompiled, study only): kernel code hands a
/// function and its argument to a work queue, the queue's worker thread calls them one after the other, in the order
/// they came, and the call answers the work task's UID. The queue kernel modules use is the system's global one, whose
/// UID is the fixed 0x10023 (sysroot's, the same on every console: a fixed value of the firmware, which is why it is
/// matched as a number); any other UID is not a work queue here (SCE_KERNEL_ERROR_UNKNOWN_WORK_QUEUE_ID, 0x80028301),
/// since nothing VitaSharp runs creates another one. The worker is a kernel thread of the kernel process, made with the
/// first task; a task runs in that thread's context (the firmware's worker is a kernel thread too). A waiting task is
/// a kernel data object of the kernel process (its function and argument), so the queue is part of the object table a
/// save state keeps: the worker takes the oldest one (the lowest UID) each time.
/// </summary>
public sealed partial class ThreadmgrKernel : IServiceThreadOwner
{
    /// <summary>The UID of the system's global work queue (fixed by the firmware).</summary>
    const int GlobalWorkQueue = 0x10023;

    /// <summary>The kind of the kernel data object of a work task (Address the function, Aux its argument, Size 1 once the
    /// worker took it).</summary>
    const string WorkTaskKind = "worktask";

    /// <summary>The worker thread's name.</summary>
    const string WorkWorkerName = "SceKernelGlobalWorkQueue";

    /// <summary>Guards the worker's creation.</summary>
    [SnapshotIgnore]
    readonly object _workTasks = new();

    [SnapshotIgnore]
    GuestThread? _workWorker;

    /// <summary>The worker's wake-ups (tests: an idle worker sleeps until a task, a load or a stop wakes it).</summary>
    [SnapshotIgnore]
    int _workWakeups;

    internal int WorkWakeups => Volatile.Read(ref _workWakeups);

    /// <summary>The tasks run so far (tests).</summary>
    [SnapshotIgnore]
    int _workTasksRun;

    internal int WorkTasksRun => Volatile.Read(ref _workTasksRun);

    /// <summary>ksceKernelEnqueueWorkQueue(SceUID queue, const char *name, function, argument): queues the call and
    /// answers the work task's UID; the global work queue is the only one.</summary>
    [Hle(Nid = 0xE50E1185, Library = "SceThreadmgrForDriver")]
    int ksceKernelEnqueueWorkQueue(int queue, string name, uint function, uint argument)
    {
        if (!IsWorkQueue(queue))
        {
            return UnknownWorkQueueId;
        }

        var task = new KernelData { Name = name, Kind = WorkTaskKind, Address = function, Aux = argument };
        int uid;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uid = K.Objects.Register(task);
        }

        WakeWorkWorker();
        return uid;
    }

    /// <summary>A task is waiting (enqueued, or restored by a state load): the worker is made, or woken. The signal goes
    /// through the worker thread's wait lock, which a stop request pulses too, so the worker needs no timed look.</summary>
    void WakeWorkWorker()
    {
        GuestThread worker;
        lock (_workTasks)
        {
            if (_workWorker == null)
            {
                StartWorkWorker();
            }

            worker = _workWorker!;
        }

        lock (worker.WaitLock)
        {
            Monitor.PulseAll(worker.WaitLock);
        }
    }

    void StartWorkWorker()
    {
        Emulator kernel = Emu.Vita.KernelProcess ?? Emu;
        GuestThread worker = kernel.Kernel.CreateThread(WorkWorkerName, 0, 0x10000100, 0x4000, 0, 0);
        worker.KernelMode = true;
        worker.HostBody = RunWorkTasks;
        _workWorker = worker;
        worker.Start(0, 0);
    }

    /// <summary>Save states (<see cref="IServiceThreadOwner"/>): the saved worker this session does not run is this
    /// module's again (the kernel's instance, where the worker lives), with its saved UID.</summary>
    bool IServiceThreadOwner.AdoptServiceThread(GuestThread thread)
    {
        if (!IsWorkWorker(thread))
        {
            return AdoptProcessWorker(thread);
        }

        lock (_workTasks)
        {
            if (_workWorker != null && _workWorker.IsLive)
            {
                return false;
            }

            thread.KernelMode = true;
            thread.HostBody = RunWorkTasks;
            _workWorker = thread;
        }

        return true;
    }

    /// <summary>Save states: a worker the loaded state does not have is forgotten (the next task makes a new one).</summary>
    bool IServiceThreadOwner.ReleaseServiceThread(GuestThread thread)
    {
        lock (_workTasks)
        {
            if (!ReferenceEquals(thread, _workWorker))
            {
                return ReleaseProcessWorker(thread);
            }

            _workWorker = null;
        }

        return true;
    }

    /// <summary>The worker's thread, held by this module when it is the kernel's instance. Matched by name: the name is
    /// the fixed one the worker is made with, and it is how a save state names a service thread (IServiceThreadOwner).</summary>
    bool IsWorkWorker(GuestThread thread)
    {
        Emulator kernel = Emu.Vita.KernelProcess ?? Emu;
        return ReferenceEquals(Emu, kernel) && ReferenceEquals(thread.Process, kernel) && thread.Name == WorkWorkerName;
    }

    /// <summary>The worker: takes the tasks in order and calls each function with its argument.</summary>
    void RunWorkTasks(GuestThread self)
    {
        while (!self.Core.StopRequested)
        {
            KernelData? task;
            using (Scheduler.Block())
            {
                lock (self.WaitLock)
                {
                    // a worker a state load let go (ReleaseServiceThread) takes no more tasks: the loaded table's are
                    // the next worker's
                    if (self.Core.StopRequested)
                    {
                        break;
                    }

                    task = OldestWorkTask();
                    if (task == null)
                    {
                        if (!self.Core.StopRequested)
                        {
                            // no timeout: a task (WakeWorkWorker) or the stop request (RequestStop) pulses this lock
                            Monitor.Wait(self.WaitLock);
                            Interlocked.Increment(ref _workWakeups);
                        }

                        continue;
                    }

                    // taken (Size 1): not seen again by the next look
                    task.Size = 1;
                }
            }

            try
            {
                self.CallGuest(task.Address, task.Aux);
            }
            finally
            {
                K.Objects.Remove(task.Uid);
                Interlocked.Increment(ref _workTasksRun);
                WorkTaskFinished(task.Uid);
            }
        }
    }

    /// <summary>A state load: the tasks waiting in the loaded object table are run (the worker sleeps until signalled,
    /// and the load put them there without an enqueue).</summary>
    void RunLoadedWorkTasks()
    {
        if (OldestWorkTask() != null)
        {
            WakeWorkWorker();
        }
    }

    KernelData? OldestWorkTask()
    {
        KernelData? oldest = null;
        foreach (KernelData data in K.Objects.All<KernelData>())
        {
            if (data.Kind == WorkTaskKind && data.Size == 0 && (oldest == null || data.Uid < oldest.Uid))
            {
                oldest = data;
            }
        }

        return oldest;
    }
}
