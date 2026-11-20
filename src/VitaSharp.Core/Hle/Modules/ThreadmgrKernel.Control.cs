using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// More of the FW 3.74 threadmgr.skprx's kernel entries (decompiled, study only), on VitaSharp's model:
/// <list type="bullet">
/// <item>the performance monitor per thread: the counters a thread counts with (NID_1D2A6815 disables, NID_7F831213
/// enables; mask bits 0-5 and 31), the event of a counter (NID_FFB9CD24), its value (NID_170F69D6 reads, NID_7B3368F1
/// writes; counter 31 the cycle counter) and the process-wide PMCR/PMUSERENR words (NID_1AAFA818, NID_5053B005). The
/// firmware switches the CP15 registers with the thread; VitaSharp's CPU has no performance monitor, so the per-thread
/// words are kept and read back as written;</item>
/// <item>work: a work task's info (NID_0B604A3C), the waits for a task (NID_12FC0FAB, NID_B247EC4B with callbacks)
/// and for the global queue to run what it holds (NID_154F2C2A, NID_48C06CD6);</item>
/// <item>calls on a CPU (NID_0788C9BC, NID_DF59DF99: core 0-3, not with interrupts masked) and the scheduler's
/// inter-processor requests (NID_8D53CA9B, NID_16D1961A): VitaSharp's guest threads run on host threads, so the call
/// runs on the caller and the requests have nothing to wake;</item>
/// <item>the per-thread areas of the handler slots (NID_89CA5698 and NID_3A72C6D8 for the caller), calling a function on
/// a new stack (NID_A2C801A5, NID_4BAA5F3B), starting a thread with arguments (NID_CBADD6B3), the delays that report
/// what is left (NID_E9A99C50, NID_2B60B793 with callbacks) and the wait for a thread to be suspended (NID_392599F1,
/// NID_614BF2BE with callbacks).</item>
/// </list>
/// </summary>
public sealed partial class ThreadmgrKernel
{
    const int UnknownWorkTaskId = unchecked((int)0x80028302), ThreadIsDead = unchecked((int)0x8002802A),
        IllegalStackSize = unchecked((int)0x80028024);

    /// <summary>The performance counters a thread may name: 0-5 and the cycle counter (31).</summary>
    const uint PmuCounterMask = 0x8000003F;

    const uint WorkTaskInfoSize = 0x34;

    /// <summary>The performance monitor words of each thread that set them, by thread UID.</summary>
    [SnapshotMissing] // states of builds before it: no thread had set them
    readonly Dictionary<int, uint[]> _pmu = new();

    /// <summary>PMCR and PMUSERENR of each process that set them, by process ID.</summary>
    [SnapshotMissing] // states of builds before it: no process had set them
    readonly Dictionary<int, (uint Control, uint UserEnable)> _processPmu = new();

    /// <summary>The work tasks that ran to the end and were not waited for yet, by UID.</summary>
    [SnapshotMissing] // states of builds before it: nothing waited for a task
    readonly HashSet<int> _finishedWorkTasks = new();

    /// <summary>A thread's words: [0] the enabled counters, [1..6] the events, [7..12] the counters, [13] the cycles.</summary>
    const int PmuEnabled = 0, PmuEvents = 1, PmuCounters = 7, PmuCycles = 13, PmuWordCount = 14;

    uint[] PmuOf(GuestThread thread)
    {
        lock (_pmu)
        {
            if (!_pmu.TryGetValue(thread.Uid, out uint[]? words))
            {
                words = new uint[PmuWordCount];
                _pmu[thread.Uid] = words;
            }

            return words;
        }
    }

    /// <summary>The thread a performance monitor call names: 0 is the caller (none inside an interrupt handler:
    /// 0x80027101), no thread SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID.</summary>
    int PmuTarget(int thid, out GuestThread? thread)
    {
        thread = null;
        if (thid == 0)
        {
            if (SystemServicesVsh.InInterrupt || GuestThread.Current == null)
            {
                return IllegalContext;
            }

            thread = GuestThread.Current;
            return 0;
        }

        thread = K.Objects.Get<GuestThread>(thid);
        return thread == null ? SceError.UNKNOWN_THREAD_ID : 0;
    }

    /// <summary>NID_1D2A6815(thid, mask): the thread stops counting with the counters of the mask.</summary>
    [Hle(Nid = 0x1D2A6815, Library = "SceThreadmgrForKernel")]
    int NID_1D2A6815(int thid, uint mask) => EnablePmu(thid, mask, false);

    /// <summary>NID_7F831213(thid, mask): the thread counts with the counters of the mask.</summary>
    [Hle(Nid = 0x7F831213, Library = "SceThreadmgrForKernel")]
    int NID_7F831213(int thid, uint mask) => EnablePmu(thid, mask, true);

    int EnablePmu(int thid, uint mask, bool enable)
    {
        int check = PmuTarget(thid, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        uint[] words = PmuOf(thread!);
        lock (words)
        {
            words[PmuEnabled] = enable ? words[PmuEnabled] | (mask & PmuCounterMask) : words[PmuEnabled] & ~(mask & PmuCounterMask);
        }

        return 0;
    }

    /// <summary>NID_FFB9CD24(thid, counter, event): the event counter 0-5 counts (above 5 SCE_KERNEL_ERROR_INVALID_ARGUMENT).</summary>
    [Hle(Nid = 0xFFB9CD24, Library = "SceThreadmgrForKernel")]
    int NID_FFB9CD24(int thid, uint counter, uint eventType)
    {
        if (counter > 5)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int check = PmuTarget(thid, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        uint[] words = PmuOf(thread!);
        lock (words)
        {
            words[PmuEvents + counter] = eventType;
        }

        return 0;
    }

    static bool IsPmuCounter(uint counter) => counter < 0x20 && ((PmuCounterMask >> (int)counter) & 1) != 0;

    /// <summary>NID_170F69D6(thid, counter, uint *value): a counter's value (counter 0-5 or 31).</summary>
    [Hle(Nid = 0x170F69D6, Library = "SceThreadmgrForKernel")]
    int NID_170F69D6(int thid, uint counter, uint value)
    {
        if (!IsPmuCounter(counter))
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (value == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        int check = PmuTarget(thid, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        uint[] words = PmuOf(thread!);
        lock (words)
        {
            GuestMemory.W32(value, counter == 31 ? words[PmuCycles] : words[PmuCounters + counter]);
        }

        return 0;
    }

    /// <summary>NID_7B3368F1(thid, counter, value): sets a counter's value (counter 0-5 or 31).</summary>
    [Hle(Nid = 0x7B3368F1, Library = "SceThreadmgrForKernel")]
    int NID_7B3368F1(int thid, uint counter, uint value)
    {
        if (!IsPmuCounter(counter))
        {
            return SceError.INVALID_ARGUMENT;
        }

        int check = PmuTarget(thid, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        uint[] words = PmuOf(thread!);
        lock (words)
        {
            if (counter == 31)
            {
                words[PmuCycles] = value;
            }
            else
            {
                words[PmuCounters + counter] = value;
            }
        }

        return 0;
    }

    /// <summary>NID_1AAFA818(pid, value): PMCR of every thread of the process (pid 0: the caller's).</summary>
    [Hle(Nid = 0x1AAFA818, Library = "SceThreadmgrForDriver")]
    int NID_1AAFA818(int pid, uint value) => SetProcessPmu(pid, value, true);

    /// <summary>NID_5053B005(pid, value): PMUSERENR of every thread of the process (pid 0: the caller's).</summary>
    [Hle(Nid = 0x5053B005, Library = "SceThreadmgrForDriver")]
    int NID_5053B005(int pid, uint value) => SetProcessPmu(pid, value, false);

    int SetProcessPmu(int pid, uint value, bool control)
    {
        if (GuestThread.Current is not { } current)
        {
            return IllegalContext;
        }

        if (pid == 0)
        {
            pid = current.Pid;
        }
        else if (KernelSpace.ProcessOf(Emu, pid) is null)
        {
            return SceError.INVALID_PID;
        }

        lock (_processPmu)
        {
            (uint Control, uint UserEnable) words = _processPmu.GetValueOrDefault(pid);
            _processPmu[pid] = control ? (value, words.UserEnable) : (words.Control, value);
        }

        return 0;
    }

    /// <summary>PMCR or PMUSERENR of a process (tests).</summary>
    internal uint ProcessPmu(int pid, bool control)
    {
        lock (_processPmu)
        {
            (uint Control, uint UserEnable) words = _processPmu.GetValueOrDefault(pid);
            return control ? words.Control : words.UserEnable;
        }
    }

    // ------------------------------------------------------------------ work tasks and the queue

    /// <summary>NID_0B604A3C(uid, info): SceKernelWorkTaskInfo (0x34: size, UID, name[0x20], the function, its
    /// argument, the queue); a larger size is SCE_KERNEL_ERROR_ILLEGAL_SIZE, no work task 0x80028302.</summary>
    [Hle(Nid = 0x0B604A3C, Library = "SceThreadmgrForDriver")]
    int NID_0B604A3C(int uid, uint info)
    {
        if (GuestMemory.R32(info) > WorkTaskInfoSize)
        {
            return SceError.ILLEGAL_SIZE;
        }

        if (K.Objects.Get<KernelData>(uid) is not { Kind: WorkTaskKind } task)
        {
            return UnknownWorkTaskId;
        }

        Span<byte> fields = stackalloc byte[(int)WorkTaskInfoSize];
        fields.Clear();
        KernelInfo.Put(fields, 0, (int)WorkTaskInfoSize);
        KernelInfo.Put(fields, 4, uid);
        byte[] name = System.Text.Encoding.UTF8.GetBytes(task.Name);
        name.AsSpan(0, Math.Min(name.Length, 0x1F)).CopyTo(fields[8..]);
        KernelInfo.Put(fields, 0x28, (int)task.Address);
        KernelInfo.Put(fields, 0x2C, (int)task.Aux);
        KernelInfo.Put(fields, 0x30, GlobalWorkQueue);
        uint size = GuestMemory.R32(info);
        fields[..(int)size].CopyTo(GuestMemory.Span(info, (int)size));
        return 0;
    }

    /// <summary>NID_12FC0FAB(uid, timeout): waits until the work task ran.</summary>
    [Hle(Nid = 0x12FC0FAB, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    int NID_12FC0FAB(int uid, Ptr<uint> timeout) => WaitWorkTask(uid, timeout);

    /// <summary>NID_B247EC4B(uid, timeout): the same, running callbacks while it waits.</summary>
    [Hle(Nid = 0xB247EC4B, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    int NID_B247EC4B(int uid, Ptr<uint> timeout) => WaitWorkTask(uid, timeout);

    int WaitWorkTask(int uid, Ptr<uint> timeout)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        if (Sysmem.InterruptsMaskedForCurrentThread)
        {
            return SceError.CAN_NOT_WAIT;
        }

        lock (_workTasks)
        {
            if (_finishedWorkTasks.Remove(uid))
            {
                return 0;
            }

            if (K.Objects.Get<KernelData>(uid) is not { Kind: WorkTaskKind })
            {
                return UnknownWorkTaskId;
            }

            int result = Wait.Until(_workTasks, () => _finishedWorkTasks.Contains(uid), timeout, what: "work task");
            _finishedWorkTasks.Remove(uid);
            return result;
        }
    }

    /// <summary>NID_154F2C2A(queue, timeout): waits until the queue ran every task it held when the call came.</summary>
    [Hle(Nid = 0x154F2C2A, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    int NID_154F2C2A(int queue, Ptr<uint> timeout) => WaitWorkQueue(queue, timeout);

    /// <summary>NID_48C06CD6(queue, timeout): the same, running callbacks while it waits.</summary>
    [Hle(Nid = 0x48C06CD6, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    int NID_48C06CD6(int queue, Ptr<uint> timeout) => WaitWorkQueue(queue, timeout);

    int WaitWorkQueue(int queue, Ptr<uint> timeout)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            return IllegalContext;
        }

        if (!IsWorkQueue(queue))
        {
            return UnknownWorkQueueId;
        }

        lock (_workTasks)
        {
            int last = 0;
            foreach (KernelData data in K.Objects.All<KernelData>())
            {
                if (data.Kind == WorkTaskKind)
                {
                    last = Math.Max(last, data.Uid);
                }
            }

            if (last == 0)
            {
                return 0;
            }

            return Wait.Until(_workTasks, () => !PendingWorkTaskUpTo(last), timeout, what: "work queue");
        }
    }

    bool PendingWorkTaskUpTo(int last)
    {
        foreach (KernelData data in K.Objects.All<KernelData>())
        {
            if (data.Kind == WorkTaskKind && data.Uid <= last)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The worker ran <paramref name="uid"/>: a wait for it (or for the queue) returns.</summary>
    void WorkTaskFinished(int uid)
    {
        lock (_workTasks)
        {
            _finishedWorkTasks.Add(uid);
            Monitor.PulseAll(_workTasks);
        }
    }

    // ------------------------------------------------------------------ CPUs

    /// <summary>NID_0788C9BC(core, func, arg): runs func(arg) on a core (0-3; else SCE_KERNEL_ERROR_INVALID_ARGUMENT),
    /// not with interrupts masked (0x80027101).</summary>
    [Hle(Nid = 0x0788C9BC, Library = "SceThreadmgrForKernel")]
    int NID_0788C9BC(uint core, uint func, uint arg) => RunOnCore(core, func, arg);

    /// <summary>NID_DF59DF99(core, func, arg): the same, waiting for the call to end.</summary>
    [Hle(Nid = 0xDF59DF99, Library = "SceThreadmgrForKernel")]
    int NID_DF59DF99(uint core, uint func, uint arg) => RunOnCore(core, func, arg);

    int RunOnCore(uint core, uint func, uint arg)
    {
        if (core >= 4)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (Sysmem.InterruptsMaskedForCurrentThread || GuestThread.Current is not { } current)
        {
            return IllegalContext;
        }

        current.CallGuest(func, arg);
        return 0;
    }

    /// <summary>NID_8D53CA9B(core): asks the core to look at its run queue again.</summary>
    [Hle(Nid = 0x8D53CA9B, Library = "SceThreadmgrForKernel")]
    void NID_8D53CA9B(uint core)
    {
    }

    /// <summary>NID_16D1961A(): asks every other core to look at its run queue again; 0.</summary>
    [Hle(Nid = 0x16D1961A, Library = "SceThreadmgrForKernel")]
    int NID_16D1961A() => 0;

    // ------------------------------------------------------------------ handler slot areas, stacks, starts, delays

    /// <summary>NID_89CA5698(slot, thid, allocate): the thread's area of handler slot 2-7 (thid 0: the caller's area
    /// when it has one, else the caller), made (zeroed, of the slot's registered size) when <paramref name="allocate"/>
    /// is set and the slot is taken; 0 when there is none, inside an interrupt handler, or for a thread that ended.</summary>
    [Hle(Nid = 0x89CA5698, Library = "SceThreadmgrForDriver", ReturnsValue = true)]
    uint NID_89CA5698(int slot, int thid, int allocate) => SlotArea(slot, thid, allocate != 0);

    /// <summary>NID_3A72C6D8(slot): the caller's area of a handler slot, made when the slot is taken.</summary>
    [Hle(Nid = 0x3A72C6D8, Library = "SceThreadmgrForDriver", ReturnsValue = true)]
    uint NID_3A72C6D8(int slot) => SlotArea(slot, 0, true);

    uint SlotArea(int slot, int thid, bool allocate)
    {
        if (SystemServicesVsh.InInterrupt || (uint)(slot - 2) >= 6)
        {
            return 0;
        }

        GuestThread? thread = thid == 0 ? GuestThread.Current : K.Objects.Get<GuestThread>(thid);
        if (thread == null || thread.Status == ThreadStatus.Dead)
        {
            return 0;
        }

        uint block = ThreadObject(thread);
        uint pointer = block + 0x70 + (uint)slot * 4;
        uint area = GuestMemory.R32(pointer);
        if (area != 0 || !allocate)
        {
            return area;
        }

        uint entry = Globals() + GlobalsSlots + (uint)slot * SlotSize + 4;
        lock (_globalsLock)
        {
            uint size = GuestMemory.R32(entry + 8);
            if (GuestMemory.R32(entry) == 0 || size == 0)
            {
                return 0;
            }

            using (ProcessScope.Enter(KernelSpace.KernelPid))
            {
                area = Mem.Alloc(size, "kernel:threadmgr-slot", MemoryRegion.Kernel);
            }

            GuestMemory.Fill(area, 0, size);
            GuestMemory.W32(entry + 4, GuestMemory.R32(entry + 4) + 1);
            GuestMemory.W32(pointer, area);
            return area;
        }
    }

    /// <summary>NID_A2C801A5(stack size, arg, func): func(arg) on a stack of at least the size (below 0x200
    /// SCE_KERNEL_ERROR_ILLEGAL_STACK_SIZE; not inside an interrupt handler); its 64-bit result.</summary>
    [Hle(Nid = 0xA2C801A5, Library = "SceThreadmgrForDriver", ReturnsValue = true)]
    long NID_A2C801A5(uint stackSize, uint arg, uint func) => RunWithStack(stackSize, arg, func);

    /// <summary>NID_4BAA5F3B(stack size, arg, func): the SceThreadmgrForKernel entry of the same.</summary>
    [Hle(Nid = 0x4BAA5F3B, Library = "SceThreadmgrForKernel", ReturnsValue = true)]
    long NID_4BAA5F3B(uint stackSize, uint arg, uint func) => RunWithStack(stackSize, arg, func);

    long RunWithStack(uint stackSize, uint arg, uint func)
    {
        if (SystemServicesVsh.InInterrupt || GuestThread.Current is not { } current)
        {
            return IllegalContext;
        }

        if (stackSize < 0x200)
        {
            return IllegalStackSize;
        }

        uint low = current.CallGuest(func, arg);
        return (long)(low | ((ulong)current.LastCallR1 << 32));
    }

    /// <summary>NID_CBADD6B3(thid, arg size, arg block): starts a thread with its arguments copied from the caller's
    /// memory (ksceKernelStartThread's path for a caller's buffer).</summary>
    [Hle(Nid = 0xCBADD6B3, Library = "SceThreadmgrForKernel")]
    void NID_CBADD6B3(HleContext context) => RunExport(context, 0xF08DE149);

    /// <summary>NID_E9A99C50(usec, int *left): delays the caller (at least 200 us; 0 for a kernel thread is
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT) and writes what was left of the time when it ended early (0 here).</summary>
    [Hle(Nid = 0xE9A99C50, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    void NID_E9A99C50(HleContext context) => DelayWithLeft(context, 0x4B675D05);

    /// <summary>NID_2B60B793(usec, int *left): the same, running callbacks while it waits.</summary>
    [Hle(Nid = 0x2B60B793, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void NID_2B60B793(HleContext context) => DelayWithLeft(context, 0x9C0180E1);

    unsafe void DelayWithLeft(HleContext context, uint nid)
    {
        if (GuestThread.Current is not { } current)
        {
            context.Return(IllegalContext);
            return;
        }

        uint usec = context.Gpr(0);
        uint left = context.Gpr(1);
        if (usec == 0 && current.KernelMode)
        {
            context.Return(SceError.INVALID_ARGUMENT);
            return;
        }

        if (usec - 1 < 199)
        {
            context.Cpu->R[0] = 200;
        }

        RunExport(context, nid);
        if (left != 0 && (int)context.Cpu->R[0] >= 0)
        {
            GuestMemory.W32(left, 0);
        }
    }

    /// <summary>NID_392599F1(thid, timeout): waits until another thread is suspended; one that never ran is
    /// SCE_KERNEL_ERROR_DORMANT, one that ended 0x8002802A, thid 0 or the caller SCE_KERNEL_ERROR_ILLEGAL_THREAD_ID.</summary>
    [Hle(Nid = 0x392599F1, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_392599F1(int thid, Ptr<uint> timeout) => WaitSuspended(thid, timeout);

    /// <summary>NID_614BF2BE(thid, timeout): the same, running callbacks while it waits.</summary>
    [Hle(Nid = 0x614BF2BE, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    int NID_614BF2BE(int thid, Ptr<uint> timeout) => WaitSuspended(thid, timeout);

    int WaitSuspended(int thid, Ptr<uint> timeout)
    {
        if (GuestThread.Current is not { } current)
        {
            return IllegalContext;
        }

        if (thid == 0 || current.Uid == thid)
        {
            return IllegalThreadId;
        }

        if (Sysmem.InterruptsMaskedForCurrentThread)
        {
            return SceError.CAN_NOT_WAIT;
        }

        if (K.Objects.Get<GuestThread>(thid) is not { } thread)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        long? limit = timeout.IsNull ? null : timeout.Value;
        long waited = 0;
        while (true)
        {
            if (thread.SuspendCount > 0 && !(thread.InGuestCode && thread.Status == ThreadStatus.Running))
            {
                return 0;
            }

            if (thread.Status == ThreadStatus.Dormant)
            {
                return SceError.DORMANT;
            }

            if (thread.Status == ThreadStatus.Dead)
            {
                return ThreadIsDead;
            }

            if (limit is long us && waited >= us)
            {
                if (!timeout.IsNull)
                {
                    timeout.Value = 0;
                }

                return SceError.WAIT_TIMEOUT;
            }

            Wait.Sleep(100);
            waited += 100;
        }
    }
}
