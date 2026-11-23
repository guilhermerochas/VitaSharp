using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The scheduler controls of SceThreadmgrForKernel/SceThreadmgrForDriver the process manager and the debugger use, as
/// the FW 3.74 threadmgr.skprx (decompiled, study only) has them:
/// <list type="bullet">
/// <item>0x810075fc (NID_15D6357C, NID_800ACB9D, NID_9928DFBA: thid, priority): stops another thread for a reason
/// (bit 29, 30 or 31 of its +0x4C word) and gives it a new priority; thid 0 or the caller is
/// SCE_KERNEL_ERROR_ILLEGAL_THREAD_ID, a priority outside 1..254 SCE_KERNEL_ERROR_ILLEGAL_PRIORITY. NID_490DAF14
/// (thid) lets it run again with the priority it had: a dormant thread is SCE_KERNEL_ERROR_DORMANT, one these calls
/// did not stop SCE_KERNEL_ERROR_NOT_SUSPENDED;</item>
/// <item>NID_3857C94E(thid, priority, affinity): changes both (thid 0 the caller, priority 0 the caller's, an affinity
/// 0x40000000 the current CPU); a relative priority past the range is SCE_KERNEL_ERROR_ILLEGAL_PRIORITY, an affinity
/// with bits outside the cores (0xF, or 0xF0000 for the user form) SCE_KERNEL_ERROR_ILLEGAL_CPU_AFFINITY_MASK;</item>
/// <item>the exit status of a dormant thread (NID_70131EA5), whether a debug-suspended thread has stopped
/// (NID_D26BFF26), the caller's attributes (NID_AB977C72) and two debugger words per thread (NID_385831A1,
/// NID_453B764A, only with DIP switch 0xE4: 0x80029008 without it).</item>
/// </list>
/// The stop is VitaSharp's scheduler suspension (<see cref="GuestThread.SuspendForVm"/>): the thread parks at its next
/// block boundary until the last reason goes away; the priority is the thread's scheduling priority.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    const int NotDebugger = unchecked((int)0x80029008);

    /// <summary>The scheduler stop reasons (bits 29-31 of the +0x4C word) of each stopped thread and the priority it
    /// had before, by thread UID.</summary>
    [SnapshotMissing] // states of builds before it: no thread was stopped by these calls
    readonly Dictionary<int, (uint Reasons, int Priority)> _schedulerStops = new();

    /// <summary>The two debugger words of each thread that has them (+0x44, +0x48 of its object).</summary>
    [SnapshotMissing] // states of builds before it: no thread had them
    readonly Dictionary<int, (uint First, uint Second)> _debuggerWords = new();

    /// <summary>NID_15D6357C(thid, priority): stops the thread for reason bit 29 with a new priority.</summary>
    [Hle(Nid = 0x15D6357C, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_15D6357C(int thid, int priority) => StopThread(thid, priority, 0x20000000);

    /// <summary>NID_800ACB9D(thid, priority): the same for reason bit 30.</summary>
    [Hle(Nid = 0x800ACB9D, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_800ACB9D(int thid, int priority) => StopThread(thid, priority, 0x40000000);

    /// <summary>NID_9928DFBA(thid, priority): the same for reason bit 31.</summary>
    [Hle(Nid = 0x9928DFBA, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_9928DFBA(int thid, int priority) => StopThread(thid, priority, 0x80000000);

    int StopThread(int thid, int priority, uint reason)
    {
        if (thid == 0 || GuestThread.Current?.Uid == thid)
        {
            return IllegalThreadId;
        }

        if ((uint)(priority - 1) >= 0xFE)
        {
            return SceError.ILLEGAL_PRIORITY;
        }

        if (K.Objects.Get<GuestThread>(thid) is not { } thread)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        bool first;
        lock (_schedulerStops)
        {
            first = !_schedulerStops.TryGetValue(thid, out (uint Reasons, int Priority) stop);
            if (first)
            {
                stop.Priority = thread.Priority;
            }

            _schedulerStops[thid] = (stop.Reasons | reason, stop.Priority);
        }

        thread.Priority = priority;
        if (first)
        {
            thread.SuspendForVm();
        }

        return 0;
    }

    /// <summary>NID_490DAF14(thid): lets a thread these calls stopped run again with its earlier priority.</summary>
    [Hle(Nid = 0x490DAF14, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    int NID_490DAF14(int thid)
    {
        if (thid == 0 || GuestThread.Current?.Uid == thid)
        {
            return IllegalThreadId;
        }

        if (K.Objects.Get<GuestThread>(thid) is not { } thread)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        if (thread.Status == ThreadStatus.Dormant)
        {
            return SceError.DORMANT;
        }

        (uint Reasons, int Priority) stop;
        lock (_schedulerStops)
        {
            if (!_schedulerStops.Remove(thid, out stop))
            {
                return SceError.NOT_SUSPENDED;
            }
        }

        thread.Priority = stop.Priority;
        thread.ResumeForVm();
        return 0;
    }

    /// <summary>NID_3857C94E(thid, priority, affinity): changes the thread's priority and CPU affinity.</summary>
    [Hle(Nid = 0x3857C94E, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    int NID_3857C94E(int thid, int priority, uint affinity)
    {
        GuestThread? current = GuestThread.Current;
        if (thid == 0)
        {
            if (current == null)
            {
                return IllegalContext;
            }

            thid = current.Uid;
        }

        if (priority == 0)
        {
            if (current == null)
            {
                return IllegalContext;
            }

            priority = current.Priority;
        }

        if ((priority & 0x10000000) != 0 && (uint)(priority - 0x100000E0) > 0x3F)
        {
            return SceError.ILLEGAL_PRIORITY;
        }

        if (affinity != 0x40000000)
        {
            if ((affinity & 0xFFF0FFF0) != 0 || ((affinity & 0xF0000) != 0 && (affinity & 0xF) != 0))
            {
                return SceError.ILLEGAL_CPU_AFFINITY_MASK;
            }

            if ((affinity & 0xF0000) != 0)
            {
                affinity >>= 16;
            }
        }

        if (K.Objects.Get<GuestThread>(thid) is not { } thread)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        if (affinity == 0x40000000)
        {
            affinity = 1;
        }

        thread.Priority = ThreadMgr.ResolvePriority(priority);
        if (affinity != 0)
        {
            thread.Affinity = (int)affinity;
        }

        return 0;
    }

    /// <summary>NID_70131EA5(thid, int *status): the exit status of a thread that ended; thid 0 or the caller is
    /// SCE_KERNEL_ERROR_ILLEGAL_THREAD_ID, a thread that has not ended SCE_KERNEL_ERROR_NOT_DORMANT, one that never ran
    /// SCE_KERNEL_ERROR_DORMANT.</summary>
    [Hle(Nid = 0x70131EA5, Library = "SceThreadmgrForDriver")]
    int NID_70131EA5(int thid, uint status)
    {
        if (thid == 0 || GuestThread.Current?.Uid == thid)
        {
            return IllegalThreadId;
        }

        if (status == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (K.Objects.Get<GuestThread>(thid) is not { } thread)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        if (thread.Status is not (ThreadStatus.Dormant or ThreadStatus.Dead))
        {
            return SceError.NOT_DORMANT;
        }

        if (thread.ExitStatus == SceError.DORMANT)
        {
            return SceError.DORMANT;
        }

        GuestMemory.W32(status, (uint)thread.ExitStatus);
        return 0;
    }

    /// <summary>NID_D26BFF26(thid): 1 when a debug-suspended thread has stopped, 0 while it is still on its way;
    /// a thread that is not debug-suspended is 0x80028030.</summary>
    [Hle(Nid = 0xD26BFF26, Library = "SceThreadmgrForDriver")]
    int NID_D26BFF26(int thid)
    {
        int check = DebugTarget(thid, out GuestThread? thread);
        if (check < 0)
        {
            return check;
        }

        if (DebugStatus(thid) == 0)
        {
            return NotDebugSuspended;
        }

        return thread!.InGuestCode && thread.Status == ThreadStatus.Running ? 0 : 1;
    }

    /// <summary>NID_AB977C72(uint *attr): the calling thread's attributes.</summary>
    [Hle(Nid = 0xAB977C72, Library = "SceThreadmgrForDriver")]
    int NID_AB977C72(uint attr)
    {
        if (attr == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (GuestThread.Current is not { } current)
        {
            return IllegalContext;
        }

        GuestMemory.W32(attr, current.Attr);
        return 0;
    }

    bool DebuggerSwitch() => Emu.Hle.Modules.OfType<SystemServicesVsh>().First().ksceKernelCheckDipswThroughEntry(0xE4) != 0;

    /// <summary>NID_385831A1(thid, kind, first, second): sets the thread's two debugger words (kind 5 only, else
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT).</summary>
    [Hle(Nid = 0x385831A1, Library = "SceThreadmgrForDriver")]
    int NID_385831A1(int thid, int kind, uint first, uint second)
    {
        if (!DebuggerSwitch())
        {
            return NotDebugger;
        }

        if (kind != 5)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<GuestThread>(thid) == null)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        lock (_debuggerWords)
        {
            _debuggerWords[thid] = (first, second);
        }

        return 0;
    }

    /// <summary>NID_453B764A(thid, kind, uint *first, uint *second): reads them back.</summary>
    [Hle(Nid = 0x453B764A, Library = "SceThreadmgrForDriver")]
    int NID_453B764A(int thid, int kind, uint first, uint second)
    {
        if (!DebuggerSwitch())
        {
            return NotDebugger;
        }

        if (kind != 5)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (first == 0 || second == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (K.Objects.Get<GuestThread>(thid) == null)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        (uint First, uint Second) words;
        lock (_debuggerWords)
        {
            words = _debuggerWords.GetValueOrDefault(thid);
        }

        GuestMemory.W32(first, words.First);
        GuestMemory.W32(second, words.Second);
        return 0;
    }
}
