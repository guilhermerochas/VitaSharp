using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The object lists of SceThreadmgrForDriver that the debugger (SceCoredump) reads, as the FW 3.74 threadmgr.skprx has
/// them (decompiled, study only). For each class of thread manager object there is a list of every object of the
/// class (ksceGUIDGetUIDVectorByClass), a list of the objects one process holds (the PUID vector of the process),
/// and for some classes an info call by UID. Every entry of a list is the class's info structure, filled as the info
/// call fills it, at a fixed stride.
/// <para>
/// The lists: <c>(level, buf, size, int *count)</c> and <c>(pid, level, buf, size, int *count)</c>. A buffer of a
/// stride or more at null is SCE_KERNEL_ERROR_INVALID_ARGUMENT; the kernel's pid too (the process list). With no object
/// of the class the answer is 0 and *count is left alone. Otherwise each object whose info can be read counts; while
/// its entry fits in <c>size</c> it is copied, and the first one that does not fit ends the walk, counting the rest
/// as if they could be read. *count is the number copied and the answer the number counted. The level bounds the
/// objects' UID level (every VitaSharp object is level 0, so it never leaves one out). In a process's list the UID of
/// each entry is the process's handle (the same UID here). The class lists include the subclasses: the event class
/// holds the simple events, the message pipes and the timers.
/// </para>
/// <para>
/// Work queues and work tasks are not listed here: VitaSharp's one queue is the global work queue (see
/// ksceKernelEnqueueWorkQueue), not an object of the class, so their lists are empty and their info calls answer the
/// unknown-ID error of the class. Alarms are listed (see ksceKernelRegisterTimer).
/// </para>
/// </summary>
public sealed partial class ThreadmgrKernel
{
    const int UnknownAlarmId = unchecked((int)0x800280C1), UnknownWorkQueueId = unchecked((int)0x80028301);

    /// <summary>Kernel memory one info structure is filled in before it is copied (the firmware's stack buffer).</summary>
    [SnapshotIgnore]
    uint _infoScratch;

    [SnapshotIgnore]
    readonly object _infoScratchLock = new();

    /// <summary>One class of objects as its debugger lists see it: which objects belong to it, the stride of its info
    /// structure and the call that fills one (uid, info with its size set; 0 on success).</summary>
    readonly record struct ObjectClass(uint Stride, Func<KernelObject, bool> Member, Func<int, uint, int> Info);

    ObjectClass SemaphoreClass => new(0x3C, static o => o is Semaphore, UserThreadmgr.sceKernelGetSemaInfo);

    ObjectClass EventFlagClass => new(0x38, static o => o is EventFlag, UserThreadmgr.sceKernelGetEventFlagInfo);

    ObjectClass MutexClass => new(0x40, IsPlainMutex, MutexInfo);

    ObjectClass CondClass => new(0x34, static o => o is CondVar { WorkArea: 0 }, UserThreadmgr.sceKernelGetCondInfo);

    ObjectClass MsgPipeClass => new(0x3C, static o => o is MsgPipe, UserThreadmgr.sceKernelGetMsgPipeInfo);

    ObjectClass RwLockClass => new(0x3C, static o => o is RwLock, UserThreadmgr.sceKernelGetRWLockInfo);

    ObjectClass CallbackClass => new(0x44, static o => o is Callback, UserThreadmgr.sceKernelGetCallbackInfo);

    ObjectClass TimerClass => new(0x60, static o => o is TimerObject, UserThreadmgr.sceKernelGetTimerInfo);

    ObjectClass ThreadEventClass => new(0x40, static o => o is ThreadEventHandler, UserThreadmgr.sceKernelGetThreadEventInfo);

    ObjectClass LwMutexClass => new(0x40, static o => o is Mutex { IsLight: true }, ksceKernelGetLwMutexInfo);

    ObjectClass LwCondClass => new(0x38, static o => o is CondVar { WorkArea: not 0 }, ksceKernelGetLwCondInfo);

    /// <summary>The event class: every event object (simple events, message pipes, timers) answers the event info.</summary>
    ObjectClass EventClass => new(0x40, static o => o is EventObject, EventInfo);

    ObjectClass SimpleEventClass => new(0x40, static o => o is SimpleEvent, UserThreadmgr.sceKernelGetEventInfo);

    static ObjectClass NoObjects(uint stride) => new(stride, static _ => false, static (_, _) => SceError.INVALID_UID);

    /// <summary>A mutex of the mutex class: neither lightweight nor a fast mutex (a class of its own).</summary>
    bool IsPlainMutex(KernelObject o)
    {
        if (o is not Mutex { IsLight: false })
        {
            return false;
        }

        lock (_fastMutexAttributes)
        {
            return !_fastMutexAttributes.ContainsKey(o.Uid);
        }
    }

    /// <summary>The kernel's SceKernelMutexInfo is the user one (0x3C) and +0x3C the ceiling priority of a mutex with
    /// the ceiling attribute (0x4); VitaSharp's mutexes keep no ceiling, so it reads 0.</summary>
    int MutexInfo(int uid, uint info)
    {
        if (K.Objects.Get<KernelObject>(uid) is not { } o || !IsPlainMutex(o))
        {
            return SceError.UNKNOWN_MUTEX_ID;
        }

        GuestMemory.W32(info, 0x3C);
        // the syscall's C# body, which this kernel function shares (on the Vita another function): a hook on
        // the syscall export does not see it
#pragma warning disable VSHLE010
        int result = UserThreadmgr.sceKernelGetMutexInfo(uid, info);
#pragma warning restore VSHLE010
        GuestMemory.W32(info, 0x40);
        GuestMemory.W32(info + 0x3C, 0);
        return result;
    }

    /// <summary>
    /// SceKernelEventInfo (0x40) of any object of the event class, as the firmware's event info reads it through the
    /// class (a subclass is accepted): a simple event answers the user call's structure; a message pipe or a timer has
    /// the header (UID, name, attributes) and no user event bits (their waits look at their own state, so the
    /// pattern, the user data and the waiting threads of the event part read 0).
    /// </summary>
    int EventInfo(int uid, uint info)
    {
        KernelObject? o = K.Objects.Get<KernelObject>(uid);
        if (o is SimpleEvent)
        {
            // the syscall's C# body, which this kernel function shares (on the Vita another function): a hook on
            // the syscall export does not see it
#pragma warning disable VSHLE010
            return UserThreadmgr.sceKernelGetEventInfo(uid, info);
#pragma warning restore VSHLE010
        }

        uint attr;
        if (o is MsgPipe pipe)
        {
            attr = pipe.Attr;
        }
        else if (o is TimerObject timer)
        {
            attr = timer.Attr;
        }
        else
        {
            return SceError.UNKNOWN_EVENT_ID;
        }

        Span<byte> fields = stackalloc byte[0x40];
        fields.Clear();
        KernelInfo.Header(fields, uid, o.Name, attr);
        return KernelInfo.Write(info, fields);
    }

    uint InfoScratch()
    {
        if (_infoScratch == 0)
        {
            using (ProcessScope.Enter(KernelSpace.KernelPid))
            {
                _infoScratch = Mem.Alloc(0x200, "kernel:threadmgr-info", MemoryRegion.Kernel);
            }
        }

        return _infoScratch;
    }

    /// <summary>Fills the class's info structure of <paramref name="uid"/> in the scratch area (cleared, its size set to
    /// the stride) and copies it to <paramref name="destination"/> on success.</summary>
    int FillInfo(ObjectClass cls, int uid, uint destination)
    {
        lock (_infoScratchLock)
        {
            uint scratch = InfoScratch();
            GuestMemory.Fill(scratch, 0, cls.Stride);
            GuestMemory.W32(scratch, cls.Stride);
            int result = cls.Info(uid, scratch);
            if (result == 0 && destination != 0)
            {
                GuestMemory.Copy(destination, scratch, cls.Stride);
            }

            return result;
        }
    }

    int ListObjects(ObjectClass cls, Emulator? process, uint buffer, uint size, uint count)
    {
        if (size >= cls.Stride && buffer == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        List<KernelObject> members = new();
        foreach (KernelObject o in K.Objects.All<KernelObject>())
        {
            if (cls.Member(o) && (process == null || o.OwnerPid == process.Pid))
            {
                members.Add(o);
            }
        }

        if (members.Count == 0)
        {
            return 0;
        }

        members.Sort(static (a, b) => a.Uid.CompareTo(b.Uid));
        int counted = 0;
        int copied = 0;
        for (int i = 0; i < members.Count; i++)
        {
            if (FillInfo(cls, members[i].Uid, 0) != 0)
            {
                continue;
            }

            counted++;
            if (size < (uint)counted * cls.Stride)
            {
                counted += members.Count - i - 1;
                break;
            }

            FillInfo(cls, members[i].Uid, buffer + (uint)copied * cls.Stride);
            copied++;
        }

        if (count != 0)
        {
            GuestMemory.W32(count, (uint)copied);
        }

        return counted;
    }

    int ListAll(ObjectClass cls, uint buffer, uint size, uint count) => ListObjects(cls, null, buffer, size, count);

    int ListOfProcess(ObjectClass cls, int pid, uint buffer, uint size, uint count)
    {
        if ((size >= cls.Stride && buffer == 0) || pid == KernelSpace.KernelPid)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (KernelSpace.ProcessOf(Emu, pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        return ListObjects(cls, process, buffer, size, count);
    }

    /// <summary>The info calls by UID: a size above the structure is SCE_KERNEL_ERROR_ILLEGAL_SIZE (before the UID is
    /// looked up); the caller's size of the structure is copied.</summary>
    int InfoOf(ObjectClass cls, int uid, uint info, int unknown)
    {
        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        uint size = GuestMemory.R32(info);
        if (size > cls.Stride)
        {
            return SceError.ILLEGAL_SIZE;
        }

        lock (_infoScratchLock)
        {
            uint scratch = InfoScratch();
            GuestMemory.Fill(scratch, 0, cls.Stride);
            GuestMemory.W32(scratch, cls.Stride);
            int result = cls.Member(K.Objects.Get<KernelObject>(uid) ?? NotAnObject.Instance) ? cls.Info(uid, scratch) : unknown;
            if (result >= 0)
            {
                GuestMemory.Copy(info, scratch, size);
            }

            return result;
        }
    }

    /// <summary>Stands for a UID without an object in the class checks.</summary>
    [NeverInObjectTable]
    sealed class NotAnObject : KernelObject
    {
        public static readonly NotAnObject Instance = new();
    }

    // ------------------------------------------------------------------ semaphores, event flags, mutexes

    /// <summary>NID_C6741986(level, buf, size, count): every semaphore (SceKernelSemaInfo, 0x3C).</summary>
    [Hle(Nid = 0xC6741986, Library = "SceThreadmgrForDriver")]
    int NID_C6741986(uint level, uint buffer, uint size, uint count) => ListAll(SemaphoreClass, buffer, size, count);

    /// <summary>NID_477F7C8A(pid, level, buf, size, count): the semaphores of a process.</summary>
    [Hle(Nid = 0x477F7C8A, Library = "SceThreadmgrForDriver")]
    int NID_477F7C8A(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(SemaphoreClass, pid, buffer, size, count);

    /// <summary>NID_5870C73A(level, buf, size, count): every event flag (SceKernelEventFlagInfo, 0x38).</summary>
    [Hle(Nid = 0x5870C73A, Library = "SceThreadmgrForDriver")]
    int NID_5870C73A(uint level, uint buffer, uint size, uint count) => ListAll(EventFlagClass, buffer, size, count);

    /// <summary>NID_0EB0ABF0(pid, level, buf, size, count): the event flags of a process.</summary>
    [Hle(Nid = 0x0EB0ABF0, Library = "SceThreadmgrForDriver")]
    int NID_0EB0ABF0(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(EventFlagClass, pid, buffer, size, count);

    /// <summary>NID_B6DA4669(level, buf, size, count): every mutex (the kernel's SceKernelMutexInfo, 0x40).</summary>
    [Hle(Nid = 0xB6DA4669, Library = "SceThreadmgrForDriver")]
    int NID_B6DA4669(uint level, uint buffer, uint size, uint count) => ListAll(MutexClass, buffer, size, count);

    /// <summary>NID_2322A2F3(pid, level, buf, size, count): the mutexes of a process.</summary>
    [Hle(Nid = 0x2322A2F3, Library = "SceThreadmgrForDriver")]
    int NID_2322A2F3(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(MutexClass, pid, buffer, size, count);

    // ------------------------------------------------------------------ condition variables, message pipes, rwlocks

    /// <summary>NID_67022B18(level, buf, size, count): every condition variable (SceKernelCondInfo, 0x34).</summary>
    [Hle(Nid = 0x67022B18, Library = "SceThreadmgrForDriver")]
    int NID_67022B18(uint level, uint buffer, uint size, uint count) => ListAll(CondClass, buffer, size, count);

    /// <summary>NID_CA704239(pid, level, buf, size, count): the condition variables of a process.</summary>
    [Hle(Nid = 0xCA704239, Library = "SceThreadmgrForDriver")]
    int NID_CA704239(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(CondClass, pid, buffer, size, count);

    /// <summary>NID_D1FBDFA6(uid, info): a condition variable's info.</summary>
    [Hle(Nid = 0xD1FBDFA6, Library = "SceThreadmgrForDriver")]
    int NID_D1FBDFA6(int uid, uint info) => InfoOf(CondClass, uid, info, SceError.UNKNOWN_COND_ID);

    /// <summary>NID_C7F5FFE0(level, buf, size, count): every message pipe (SceKernelMppInfo, 0x3C).</summary>
    [Hle(Nid = 0xC7F5FFE0, Library = "SceThreadmgrForDriver")]
    int NID_C7F5FFE0(uint level, uint buffer, uint size, uint count) => ListAll(MsgPipeClass, buffer, size, count);

    /// <summary>NID_E9E50096(pid, level, buf, size, count): the message pipes of a process.</summary>
    [Hle(Nid = 0xE9E50096, Library = "SceThreadmgrForDriver")]
    int NID_E9E50096(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(MsgPipeClass, pid, buffer, size, count);

    /// <summary>NID_E2B57231(level, buf, size, count): every reader/writer lock (SceKernelRWLockInfo, 0x3C).</summary>
    [Hle(Nid = 0xE2B57231, Library = "SceThreadmgrForDriver")]
    int NID_E2B57231(uint level, uint buffer, uint size, uint count) => ListAll(RwLockClass, buffer, size, count);

    /// <summary>NID_6624E612(pid, level, buf, size, count): the reader/writer locks of a process.</summary>
    [Hle(Nid = 0x6624E612, Library = "SceThreadmgrForDriver")]
    int NID_6624E612(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(RwLockClass, pid, buffer, size, count);

    /// <summary>NID_374C3267(uid, info): a reader/writer lock's info.</summary>
    [Hle(Nid = 0x374C3267, Library = "SceThreadmgrForDriver")]
    int NID_374C3267(int uid, uint info) => InfoOf(RwLockClass, uid, info, SceError.UNKNOWN_RW_LOCK_ID);

    // ------------------------------------------------------------------ lightweight mutexes and condition variables

    /// <summary>NID_C2DA6286(level, buf, size, count): every lightweight mutex (SceKernelLwMutexInfo, 0x40).</summary>
    [Hle(Nid = 0xC2DA6286, Library = "SceThreadmgrForDriver")]
    int NID_C2DA6286(uint level, uint buffer, uint size, uint count) => ListAll(LwMutexClass, buffer, size, count);

    /// <summary>NID_069D8A20(pid, level, buf, size, count): the lightweight mutexes of a process.</summary>
    [Hle(Nid = 0x069D8A20, Library = "SceThreadmgrForDriver")]
    int NID_069D8A20(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(LwMutexClass, pid, buffer, size, count);

    /// <summary>NID_738FF63D(level, buf, size, count): every lightweight condition variable (SceKernelLwCondInfo, 0x38).</summary>
    [Hle(Nid = 0x738FF63D, Library = "SceThreadmgrForDriver")]
    int NID_738FF63D(uint level, uint buffer, uint size, uint count) => ListAll(LwCondClass, buffer, size, count);

    /// <summary>NID_09346D1A(pid, level, buf, size, count): the lightweight condition variables of a process.</summary>
    [Hle(Nid = 0x09346D1A, Library = "SceThreadmgrForDriver")]
    int NID_09346D1A(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(LwCondClass, pid, buffer, size, count);

    // ------------------------------------------------------------------ events, simple events, thread events

    /// <summary>NID_995F32E1(level, buf, size, count): every object of the event class (SceKernelEventInfo, 0x40).</summary>
    [Hle(Nid = 0x995F32E1, Library = "SceThreadmgrForDriver")]
    int NID_995F32E1(uint level, uint buffer, uint size, uint count) => ListAll(EventClass, buffer, size, count);

    /// <summary>NID_7D12344A(pid, level, buf, size, count): the event objects of a process.</summary>
    [Hle(Nid = 0x7D12344A, Library = "SceThreadmgrForDriver")]
    int NID_7D12344A(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(EventClass, pid, buffer, size, count);

    /// <summary>NID_CBA7FAAA(uid, info): an event object's info.</summary>
    [Hle(Nid = 0xCBA7FAAA, Library = "SceThreadmgrForDriver")]
    int NID_CBA7FAAA(int uid, uint info) => InfoOf(EventClass, uid, info, SceError.UNKNOWN_EVENT_ID);

    /// <summary>NID_4DE77569(level, buf, size, count): every simple event (their event info).</summary>
    [Hle(Nid = 0x4DE77569, Library = "SceThreadmgrForDriver")]
    int NID_4DE77569(uint level, uint buffer, uint size, uint count) => ListAll(SimpleEventClass, buffer, size, count);

    /// <summary>NID_E0C7BCBB(pid, level, buf, size, count): the simple events of a process.</summary>
    [Hle(Nid = 0xE0C7BCBB, Library = "SceThreadmgrForDriver")]
    int NID_E0C7BCBB(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(SimpleEventClass, pid, buffer, size, count);

    /// <summary>NID_5D8F88B1(level, buf, size, count): every thread event handler (SceKernelThreadEventInfo, 0x40).</summary>
    [Hle(Nid = 0x5D8F88B1, Library = "SceThreadmgrForDriver")]
    int NID_5D8F88B1(uint level, uint buffer, uint size, uint count) => ListAll(ThreadEventClass, buffer, size, count);

    /// <summary>NID_C4456EC9(pid, level, buf, size, count): the thread event handlers of a process.</summary>
    [Hle(Nid = 0xC4456EC9, Library = "SceThreadmgrForDriver")]
    int NID_C4456EC9(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(ThreadEventClass, pid, buffer, size, count);

    /// <summary>NID_6657429E(uid, info): a thread event handler's info.</summary>
    [Hle(Nid = 0x6657429E, Library = "SceThreadmgrForDriver")]
    int NID_6657429E(int uid, uint info) => InfoOf(ThreadEventClass, uid, info, SceError.UNKNOWN_THREAD_EVENT_ID);

    // ------------------------------------------------------------------ callbacks, timers, alarms, work queues

    /// <summary>NID_767CA30C(level, buf, size, count): every callback (SceKernelCallbackInfo, 0x44).</summary>
    [Hle(Nid = 0x767CA30C, Library = "SceThreadmgrForDriver")]
    int NID_767CA30C(uint level, uint buffer, uint size, uint count) => ListAll(CallbackClass, buffer, size, count);

    /// <summary>NID_FF8DA217(pid, level, buf, size, count): the callbacks of a process.</summary>
    [Hle(Nid = 0xFF8DA217, Library = "SceThreadmgrForDriver")]
    int NID_FF8DA217(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(CallbackClass, pid, buffer, size, count);

    /// <summary>NID_FDC044AC(uid, info): a callback's info.</summary>
    [Hle(Nid = 0xFDC044AC, Library = "SceThreadmgrForDriver")]
    int NID_FDC044AC(int uid, uint info) => InfoOf(CallbackClass, uid, info, SceError.UNKNOWN_CALLBACK_ID);

    /// <summary>NID_3C297724(level, buf, size, count): every timer (SceKernelTimerInfo, 0x60).</summary>
    [Hle(Nid = 0x3C297724, Library = "SceThreadmgrForDriver")]
    int NID_3C297724(uint level, uint buffer, uint size, uint count) => ListAll(TimerClass, buffer, size, count);

    /// <summary>NID_C327CA7C(pid, level, buf, size, count): the timers of a process.</summary>
    [Hle(Nid = 0xC327CA7C, Library = "SceThreadmgrForDriver")]
    int NID_C327CA7C(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(TimerClass, pid, buffer, size, count);

    /// <summary>NID_19F3E92A(uid, info): a timer's info.</summary>
    [Hle(Nid = 0x19F3E92A, Library = "SceThreadmgrForDriver")]
    int NID_19F3E92A(int uid, uint info) => InfoOf(TimerClass, uid, info, SceError.UNKNOWN_TIMER_ID);

    /// <summary>NID_2828F884(level, buf, size, count): every alarm (SceKernelAlarmInfo, 0x38).</summary>
    [Hle(Nid = 0x2828F884, Library = "SceThreadmgrForDriver")]
    int NID_2828F884(uint level, uint buffer, uint size, uint count) => ListAll(AlarmClass, buffer, size, count);

    /// <summary>NID_4A038803(uid, info): an alarm's info (0x38).</summary>
    [Hle(Nid = 0x4A038803, Library = "SceThreadmgrForDriver")]
    int NID_4A038803(int uid, uint info) => InfoOf(AlarmClass, uid, info, UnknownAlarmId);

    /// <summary>NID_920EA1CA(level, buf, size, count): every work queue (0x40 or 0x48 each; none in VitaSharp).</summary>
    [Hle(Nid = 0x920EA1CA, Library = "SceThreadmgrForDriver")]
    int NID_920EA1CA(uint level, uint buffer, uint size, uint count) => ListAll(NoObjects(0x40), buffer, size, count);

    /// <summary>NID_8C8A76EF(pid, level, buf, size, count): the work queues of a process.</summary>
    [Hle(Nid = 0x8C8A76EF, Library = "SceThreadmgrForDriver")]
    int NID_8C8A76EF(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(NoObjects(0x40), pid, buffer, size, count);

    /// <summary>NID_B92709C4(uid, info): a work queue's info (0x40).</summary>
    [Hle(Nid = 0xB92709C4, Library = "SceThreadmgrForDriver")]
    int NID_B92709C4(int uid, uint info) => InfoOf(NoObjects(0x40), uid, info, UnknownWorkQueueId);

    /// <summary>NID_E605ED7A(level, buf, size, count): every work task (0x58 each; none in VitaSharp).</summary>
    [Hle(Nid = 0xE605ED7A, Library = "SceThreadmgrForDriver")]
    int NID_E605ED7A(uint level, uint buffer, uint size, uint count) => ListAll(NoObjects(0x58), buffer, size, count);

    /// <summary>NID_B4CED111(pid, level, buf, size, count): the work tasks of a process.</summary>
    [Hle(Nid = 0xB4CED111, Library = "SceThreadmgrForDriver")]
    int NID_B4CED111(int pid, uint level, uint buffer, uint size, uint count) => ListOfProcess(NoObjects(0x58), pid, buffer, size, count);
}
