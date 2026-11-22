using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The kernel's thread information of the FW 3.74 threadmgr.skprx (decompiled, study only): the 0x128-byte structure
/// 0x810179bc fills from a thread object (SceUIDThreadClass, 0x200 bytes; the thread's control block, what TPIDRPRW
/// holds while it runs, is the object + 0x28), the info calls by UID and the lists of the debugger.
/// <para>
/// The structure, in words: [0] the size 0x128, [1] UID, [2] process ID (object +0x60), [3..10] the name, [0xB]
/// +0xCC, [0xC] attr (+0xD0), [0xD] the status (the user form for the ForDriver calls, the whole +0xC4 word for the
/// ForKernel ones), [0xE] entry (+0x108), [0xF]/[0x10] the kernel stack and its size (+0xF8/+0xFC), [0x13]/[0x14]
/// the user stack and its size (+0x9C/+0xA0), [0x1E]/[0x1F] the initial and current priority (+0xD4/+0xE0),
/// [0x20]/[0x21] the initial and current CPU affinity (+0xE4/+0xE8), [0x23]/[0x24] the current and last CPU
/// (+0xEC/+0xF0), [0x25] the wait type of a waiting thread (bit 31 when it waits with callbacks, +0x68), [0x26] the
/// UID it waits on (+0x138), [0x27..0x28] the run clocks (+0x1B8), [0x29] the exit status (+0xC8), [0x2A] the debug
/// suspend reasons (+0x98), [0x2B..0x2E] the preemption, release and CPU change counters (+0x1AC..+0x1C0), [0x2F] the
/// notify-callback flag, [0x40] the control block's address.
/// </para>
/// <para>
/// VitaSharp's threads are <see cref="GuestThread"/>s: their fields fill the structure, and each thread a caller asks
/// about gets an object block of the firmware's size in kernel memory, its words at the firmware's offsets refreshed
/// on every call, so the control block address points at the same values. The wait type is the firmware's
/// SCE_KERNEL_WAITTYPE_* of the object the thread waits on.
/// </para>
/// </summary>
public sealed partial class ThreadmgrKernel
{
    const uint ThreadInfoSize = 0x128, ThreadObjectSize = 0x200, ThreadControlBlock = 0x28,
        ThreadNameEntrySize = 0x24;

    /// <summary>The ForDriver form of the info (the user status) and the ForKernel one (the whole status word).</summary>
    const uint ThreadInfoUser = 3, ThreadInfoKernel = 5;

    /// <summary>The object block of each thread a caller asked about, by thread UID.</summary>
    [SnapshotMissing] // states of builds before it: no thread had one
    readonly Dictionary<int, uint> _threadObjects = new();

    /// <summary>The running thread's control block (what TPIDRPRW holds while it runs: its object block + 0x28), or 0
    /// without a current thread.</summary>
    internal uint CurrentControlBlock() => GuestThread.Current is { } current ? ThreadObject(current) + ThreadControlBlock : 0;

    /// <summary>The thread's object block (made on first use) with its words refreshed from the thread.</summary>
    uint ThreadObject(GuestThread thread)
    {
        uint block;
        lock (_threadObjects)
        {
            if (!_threadObjects.TryGetValue(thread.Uid, out block))
            {
                using (ProcessScope.Enter(KernelSpace.KernelPid))
                {
                    block = Mem.Alloc(ThreadObjectSize, "kernel:SceUIDThreadObject", MemoryRegion.Kernel);
                }

                GuestMemory.Fill(block, 0, ThreadObjectSize);
                _threadObjects[thread.Uid] = block;
            }
        }

        uint waitType = WaitTypeOf(thread, out int waitId);
        GuestMemory.W32(block + ThreadControlBlock + 0x08, (uint)thread.Uid);
        GuestMemory.W32(block + 0x60, (uint)thread.Pid);
        GuestMemory.W32(block + 0x68, waitType);
        GuestMemory.W32(block + 0x98, DebugStatus(thread.Uid));
        GuestMemory.W32(block + 0x9C, thread.StackAddr);
        GuestMemory.W32(block + 0xA0, thread.StackSize);
        GuestMemory.W32(block + 0xC4, (uint)thread.Status);
        GuestMemory.W32(block + 0xC8, (uint)thread.ExitStatus);
        GuestMemory.W32(block + 0xD0, thread.Attr);
        GuestMemory.W32(block + 0xD4, (uint)thread.InitPriority);
        GuestMemory.W32(block + 0xE0, (uint)thread.Priority);
        GuestMemory.W32(block + 0xE4, (uint)thread.Affinity);
        GuestMemory.W32(block + 0xE8, (uint)thread.Affinity);
        GuestMemory.W32(block + 0xF8, thread.StackAddr);
        GuestMemory.W32(block + 0xFC, thread.StackSize);
        GuestMemory.W32(block + 0x108, thread.Entry);
        GuestMemory.W32(block + 0x138, (uint)waitId);
        GuestMemory.Write(block + 0x1B8, (ulong)Math.Max(0, thread.CpuTimeTicks));
        return block;
    }

    /// <summary>The firmware's wait type (SCE_KERNEL_WAITTYPE_*) of a waiting thread and the UID it waits on.</summary>
    static uint WaitTypeOf(GuestThread thread, out int waitId)
    {
        waitId = -1;
        if (thread.Status != ThreadStatus.Waiting)
        {
            return 0;
        }

        object? sync = thread.CurrentWaitSync;
        if (sync is KernelObject o)
        {
            waitId = o.Uid;
        }

        return sync switch
        {
            GuestThread => 0x2,
            EventFlag => 0x10,
            Semaphore => 0x20,
            Mutex { IsLight: true } => 0x100000,
            Mutex => 0x40,
            RwLock => 0x80,
            CondVar { WorkArea: not 0 } => 0x200000,
            CondVar => 0x100,
            MsgPipe => 0x40000,
            EventObject => 0x10000,
            MultiEventWait => 0x20000,
            _ => 0x1,
        };
    }

    /// <summary>Fills the 0x128-byte info of <paramref name="thread"/> at <paramref name="info"/>.</summary>
    void FillThreadInfo(GuestThread thread, uint kind, uint info)
    {
        uint block = ThreadObject(thread);
        GuestMemory.Fill(info, 0, ThreadInfoSize);
        GuestMemory.W32(info, ThreadInfoSize);
        GuestMemory.W32(info + 0x04, (uint)thread.Uid);
        GuestMemory.W32(info + 0x08, (uint)thread.Pid);
        byte[] name = System.Text.Encoding.UTF8.GetBytes(thread.Name);
        name.AsSpan(0, Math.Min(name.Length, 0x1F)).CopyTo(GuestMemory.Span(info + 0x0C, 0x1F));
        GuestMemory.W32(info + 0x30, thread.Attr);
        GuestMemory.W32(info + 0x34, kind == ThreadInfoKernel ? (uint)thread.Status : (uint)thread.Status & 0xFFFF);
        GuestMemory.W32(info + 0x38, thread.Entry);
        GuestMemory.W32(info + 0x3C, thread.StackAddr);
        GuestMemory.W32(info + 0x40, thread.StackSize);
        GuestMemory.W32(info + 0x4C, thread.StackAddr);
        GuestMemory.W32(info + 0x50, thread.StackSize);
        GuestMemory.W32(info + 0x78, (uint)thread.InitPriority);
        GuestMemory.W32(info + 0x7C, (uint)thread.Priority);
        GuestMemory.W32(info + 0x80, (uint)thread.Affinity);
        GuestMemory.W32(info + 0x84, (uint)thread.Affinity);
        uint waitType = GuestMemory.R32(block + 0x68);
        if (waitType != 0)
        {
            GuestMemory.W32(info + 0x94, thread.CallbackWait ? waitType | 0x80000000 : waitType);
        }

        int waitId = (int)GuestMemory.R32(block + 0x138);
        if (waitId != -1)
        {
            GuestMemory.W32(info + 0x98, (uint)waitId);
        }

        GuestMemory.Write(info + 0x9C, GuestMemory.Read<ulong>(block + 0x1B8));
        GuestMemory.W32(info + 0xA4, (uint)thread.ExitStatus);
        GuestMemory.W32(info + 0xA8, DebugStatus(thread.Uid));
        GuestMemory.W32(info + 0xBC, thread.CallbackWait ? 1u : 0u);
        GuestMemory.W32(info + 0x100, block + ThreadControlBlock);
    }

    /// <summary>The info call by UID (0x81018008 and the debugger's 0x81018070): UID 0 is the current thread (none:
    /// 0x80027101), a size above 0x128 SCE_KERNEL_ERROR_ILLEGAL_SIZE, no such thread SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID;
    /// the caller's size is copied.</summary>
    int ThreadInfoOf(int uid, uint kind, uint info)
    {
        if (uid == 0)
        {
            if (GuestThread.Current is not { } current)
            {
                return IllegalContext;
            }

            uid = current.Uid;
        }

        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        uint size = GuestMemory.R32(info);
        if (size > ThreadInfoSize)
        {
            return SceError.ILLEGAL_SIZE;
        }

        if (K.Objects.Get<GuestThread>(uid) is not { } thread)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        lock (_infoScratchLock)
        {
            uint scratch = InfoScratch();
            FillThreadInfo(thread, kind, scratch);
            GuestMemory.Copy(info, scratch, Math.Min(size, ThreadInfoSize));
        }

        return 0;
    }

    /// <summary>SceThreadmgrForDriver 555CA6BB(uid, level, info): the thread's info, the user status.</summary>
    [Hle(Nid = 0x555CA6BB, Library = "SceThreadmgrForDriver")]
    int NID_555CA6BB(int uid, uint level, uint info) => ThreadInfoOf(uid, ThreadInfoUser, info);

    /// <summary>NID_C976719E(uid, level, info): the same with the whole status word (SceThreadmgrForKernel).</summary>
    [Hle(Nid = 0xC976719E, Library = "SceThreadmgrForKernel")]
    int NID_C976719E(int uid, uint level, uint info) => ThreadInfoOf(uid, ThreadInfoKernel, info);

    /// <summary>ksceKernelGetThreadInfoForDebugger(uid, level, info): the info as the debugger reads it (the wait
    /// objects as UIDs).</summary>
    [Hle(Nid = 0xDD8D9429, Library = "SceThreadmgrForDriver")]
    int ksceKernelGetThreadInfoForDebugger(int uid, uint level, uint info) => ThreadInfoOf(uid, ThreadInfoUser, info);

    /// <summary>NID_BFF892B4(uid, level, info): the debugger's info with the whole status word.</summary>
    [Hle(Nid = 0xBFF892B4, Library = "SceThreadmgrForKernel")]
    int NID_BFF892B4(int uid, uint level, uint info) => ThreadInfoOf(uid, ThreadInfoKernel, info);

    ObjectClass ThreadClass(uint kind) => new(ThreadInfoSize, static o => o is GuestThread, (uid, info) => ThreadInfoFill(uid, kind, info));

    int ThreadInfoFill(int uid, uint kind, uint info)
    {
        if (K.Objects.Get<GuestThread>(uid) is not { } thread)
        {
            return SceError.UNKNOWN_THREAD_ID;
        }

        FillThreadInfo(thread, kind, info);
        return 0;
    }

    /// <summary>NID_D7455187(level, buf, size, count): every thread's info (0x128 each).</summary>
    [Hle(Nid = 0xD7455187, Library = "SceThreadmgrForDriver")]
    int NID_D7455187(uint level, uint buffer, uint size, uint count) => ListAll(ThreadClass(ThreadInfoUser), buffer, size, count);

    /// <summary>NID_B011EA69(level, buf, size, count): the same with the whole status word.</summary>
    [Hle(Nid = 0xB011EA69, Library = "SceThreadmgrForKernel")]
    int NID_B011EA69(uint level, uint buffer, uint size, uint count) => ListAll(ThreadClass(ThreadInfoKernel), buffer, size, count);

    /// <summary>NID_F2C9DC97(pid, level, buf, size, count): the infos of a process's threads.</summary>
    [Hle(Nid = 0xF2C9DC97, Library = "SceThreadmgrForDriver")]
    int NID_F2C9DC97(int pid, uint level, uint buffer, uint size, uint count) => ThreadsOfProcess(pid, ThreadInfoUser, buffer, size, count);

    /// <summary>NID_6351A13A(pid, level, buf, size, count): the same with the whole status word.</summary>
    [Hle(Nid = 0x6351A13A, Library = "SceThreadmgrForKernel")]
    int NID_6351A13A(int pid, uint level, uint buffer, uint size, uint count) => ThreadsOfProcess(pid, ThreadInfoKernel, buffer, size, count);

    int ThreadsOfProcess(int pid, uint kind, uint buffer, uint size, uint count)
    {
        if (size >= ThreadInfoSize && buffer == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (KernelSpace.ProcessOf(Emu, pid) is null)
        {
            return SceError.INVALID_PID;
        }

        ObjectClass cls = new(ThreadInfoSize, o => o is GuestThread t && t.Pid == pid, (uid, info) => ThreadInfoFill(uid, kind, info));
        return ListObjects(cls, null, buffer, size, count);
    }

    /// <summary>NID_FD87586C(pid, buf, size, count): the UID and name (0x24 bytes each) of a process's threads; a
    /// buffer of an entry or more at null is SCE_KERNEL_ERROR_INVALID_ARGUMENT. Answers the number of the process's
    /// threads, *count the entries copied.</summary>
    [Hle(Nid = 0xFD87586C, Library = "SceThreadmgrForDriver")]
    int NID_FD87586C(int pid, uint buffer, uint size, uint count) => ThreadNames(pid, buffer, size, count);

    /// <summary>NID_A4D270F9(pid, buf, size, count): the SceThreadmgrForKernel entry of the same.</summary>
    [Hle(Nid = 0xA4D270F9, Library = "SceThreadmgrForKernel")]
    int NID_A4D270F9(int pid, uint buffer, uint size, uint count) => ThreadNames(pid, buffer, size, count);

    int ThreadNames(int pid, uint buffer, uint size, uint count)
    {
        if (size >= ThreadNameEntrySize && buffer == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        List<GuestThread> threads = new();
        foreach (GuestThread thread in K.Objects.All<GuestThread>())
        {
            if (thread.Pid == pid)
            {
                threads.Add(thread);
            }
        }

        threads.Sort(static (a, b) => a.Uid.CompareTo(b.Uid));
        uint copied = 0;
        foreach (GuestThread thread in threads)
        {
            if ((copied + 1) * ThreadNameEntrySize > size)
            {
                break;
            }

            uint entry = buffer + copied * ThreadNameEntrySize;
            GuestMemory.Fill(entry, 0, ThreadNameEntrySize);
            GuestMemory.W32(entry, (uint)thread.Uid);
            byte[] name = System.Text.Encoding.UTF8.GetBytes(thread.Name);
            name.AsSpan(0, Math.Min(name.Length, 0x20)).CopyTo(GuestMemory.Span(entry + 4, 0x20));
            copied++;
        }

        if (count != 0)
        {
            GuestMemory.W32(count, copied);
        }

        return threads.Count;
    }

    /// <summary>NID_B5C782A3(uid, buf, size, count): the threads waiting on a waitable object, 16 bytes each (thread
    /// UID, process ID, current priority, 0); a buffer of an entry or more at null is
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT, a UID that is not a waitable object SCE_KERNEL_ERROR_UNKNOWN_UID. Answers the
    /// number of waiting threads, *count the entries copied.</summary>
    [Hle(Nid = 0xB5C782A3, Library = "SceThreadmgrForDriver")]
    int NID_B5C782A3(int uid, uint buffer, uint size, uint count)
    {
        if (size >= 0x10 && buffer == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<KernelObject>(uid) is not { } waitable || waitable is GuestThread || waitable is KernelData)
        {
            return SceError.UNKNOWN_UID;
        }

        List<GuestThread> waiting = new();
        foreach (GuestThread thread in K.Objects.All<GuestThread>())
        {
            if (thread.Status == ThreadStatus.Waiting && ReferenceEquals(thread.CurrentWaitSync, waitable))
            {
                waiting.Add(thread);
            }
        }

        waiting.Sort(static (a, b) => b.Priority != a.Priority ? a.Priority.CompareTo(b.Priority) : a.Uid.CompareTo(b.Uid));
        uint copied = 0;
        foreach (GuestThread thread in waiting)
        {
            if ((copied + 1) * 0x10 > size)
            {
                break;
            }

            uint entry = buffer + copied * 0x10;
            GuestMemory.W32(entry, (uint)thread.Uid);
            GuestMemory.W32(entry + 4, (uint)thread.Pid);
            GuestMemory.W32(entry + 8, (uint)thread.Priority);
            GuestMemory.W32(entry + 12, 0);
            copied++;
        }

        if (count != 0)
        {
            GuestMemory.W32(count, copied);
        }

        return waiting.Count;
    }

    /// <summary>NID_54E1AF04(pid, uid): how many threads of the process wait on the waitable object; a UID that is not
    /// one is SCE_KERNEL_ERROR_UNKNOWN_UID.</summary>
    [Hle(Nid = 0x54E1AF04, Library = "SceThreadmgrForDriver")]
    int NID_54E1AF04(int pid, int uid)
    {
        if (KernelSpace.ProcessOf(Emu, pid) is null)
        {
            return SceError.INVALID_PID;
        }

        if (K.Objects.Get<KernelObject>(uid) is not { } waitable || waitable is GuestThread || waitable is KernelData)
        {
            return SceError.UNKNOWN_UID;
        }

        int waiting = 0;
        foreach (GuestThread thread in K.Objects.All<GuestThread>())
        {
            if (thread.Pid == pid && thread.Status == ThreadStatus.Waiting && ReferenceEquals(thread.CurrentWaitSync, waitable))
            {
                waiting++;
            }
        }

        return waiting;
    }

    // ------------------------------------------------------------------ per-CPU state

    /// <summary>NID_33E85E9E(info): SceKernelThreadRunStatus (0x34) of the kernel: for each of the four cores the
    /// process, the thread and its priority (-1 when the core runs a thread of another process); a size above 0x34
    /// is SCE_KERNEL_ERROR_ILLEGAL_SIZE.</summary>
    [Hle(Nid = 0x33E85E9E, Library = "SceThreadmgrForDriver")]
    int NID_33E85E9E(uint info)
    {
        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (GuestMemory.R32(info) > 0x34)
        {
            return SceError.ILLEGAL_SIZE;
        }

        Span<byte> fields = stackalloc byte[0x34];
        fields.Clear();
        KernelInfo.Put(fields, 0, 0x34);
        int core = 0;
        foreach (GuestThread thread in K.Objects.All<GuestThread>())
        {
            if (core >= 4)
            {
                break;
            }

            if (!thread.HasCore || thread.Status != ThreadStatus.Running)
            {
                continue;
            }

            KernelInfo.Put(fields, 4 + core * 12, thread.Pid);
            KernelInfo.Put(fields, 8 + core * 12, thread.Uid);
            KernelInfo.Put(fields, 12 + core * 12, thread.Priority);
            core++;
        }

        for (; core < 4; core++)
        {
            KernelInfo.Put(fields, 4 + core * 12, -1);
            KernelInfo.Put(fields, 8 + core * 12, -1);
            KernelInfo.Put(fields, 12 + core * 12, -1);
        }

        return KernelInfo.Write(info, fields);
    }

    /// <summary>NID_7E280B69(info): SceKernelSystemInfo (0x48) of the kernel: the active CPU mask (the four cores) and
    /// per core {idle clock, out-of-idle count, thread switches}; VitaSharp keeps no scheduler statistics, they read 0.
    /// A size above 0x48 is SCE_KERNEL_ERROR_ILLEGAL_SIZE.</summary>
    [Hle(Nid = 0x7E280B69, Library = "SceThreadmgrForDriver")]
    int NID_7E280B69(uint info)
    {
        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (GuestMemory.R32(info) > 0x48)
        {
            return SceError.ILLEGAL_SIZE;
        }

        Span<byte> fields = stackalloc byte[0x48];
        fields.Clear();
        KernelInfo.Put(fields, 0, 0x48);
        KernelInfo.Put(fields, 4, 0xF);
        return KernelInfo.Write(info, fields);
    }

    // ------------------------------------------------------------------ fast mutexes by UID

    /// <summary>The work area of each fast mutex, by its UID (the firmware's object keeps it at +0x2C).</summary>
    [SnapshotMissing] // states of builds before it: the UIDs of the fast mutexes they had are not listed
    readonly Dictionary<int, uint> _fastMutexWork = new();

    ObjectClass FastMutexClass => new(FastMutexInfoSize, IsFastMutex, FastMutexInfoByUid);

    bool IsFastMutex(KernelObject o)
    {
        lock (_fastMutexAttributes)
        {
            return o is Mutex && _fastMutexWork.ContainsKey(o.Uid);
        }
    }

    int FastMutexInfoByUid(int uid, uint info)
    {
        uint work;
        lock (_fastMutexAttributes)
        {
            if (!_fastMutexWork.TryGetValue(uid, out work))
            {
                return UnknownFastMutexId;
            }
        }

        return ksceKernelGetFastMutexInfo(work, info);
    }

    /// <summary>The fast mutex class's unknown-ID error.</summary>
    const int UnknownFastMutexId = unchecked((int)0x80028161);

    /// <summary>NID_AF302193(uid, info): a fast mutex's info by its UID (SceKernelFastMutexInfo, 0x44; a larger size is
    /// SCE_KERNEL_ERROR_ILLEGAL_SIZE, no fast mutex 0x80028161).</summary>
    [Hle(Nid = 0xAF302193, Library = "SceThreadmgrForDriver")]
    int NID_AF302193(int uid, uint info)
    {
        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (GuestMemory.R32(info) > FastMutexInfoSize)
        {
            return SceError.ILLEGAL_SIZE;
        }

        return FastMutexInfoByUid(uid, info);
    }

    /// <summary>NID_6D4C1EB7(level, buf, size, count): every fast mutex's info.</summary>
    [Hle(Nid = 0x6D4C1EB7, Library = "SceThreadmgrForDriver")]
    int NID_6D4C1EB7(uint level, uint buffer, uint size, uint count) => ListAll(FastMutexClass, buffer, size, count);
}
