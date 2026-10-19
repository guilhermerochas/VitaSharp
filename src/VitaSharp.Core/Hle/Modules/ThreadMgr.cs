using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceThreadmgr + the thread/synchronisation parts of SceLibKernel.</summary>
public sealed class ThreadMgr : HleModule
{
    const int SCE_KERNEL_THREAD_ID_SELF = 0;

    GuestThread Self => GuestThread.Current!;
    T? Obj<T>(int uid) where T : KernelObject => K.Objects.Get<T>(uid);

    // ------------------------------------------------------------------ threads

    /// <summary>
    /// SCE_KERNEL_DEFAULT_PRIORITY (0x10000100) means "the default user priority" and games pass it as
    /// "default + n" / "default - n"; taken literally it is the lowest priority there is, so the strict-priority
    /// scheduler would starve such threads (God of War's Bink video threads, auto-load threads).
    /// </summary>
    public static int ResolvePriority(int priority) => Kernel.Kernel.ResolvePriority(priority);

    [Hle]
    int sceKernelCreateThread(string name, uint entry, int initPriority, int stackSize, uint attr, int cpuAffinityMask, uint option)
    {
        if (entry == 0) return SceError.INVALID_ARGUMENT;
        int prio = ResolvePriority(initPriority);
        var t = K.CreateThread(name, entry, prio, (uint)Math.Max(stackSize, 0x1000), attr, cpuAffinityMask);
        return t.Uid;
    }

    [Hle]
    int sceKernelStartThread(int thid, uint argSize, uint argp)
    {
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        return t.Start(argSize, argp);
    }

    [Hle] void sceKernelExitThread(int status) => throw new GuestThreadExitException(status, false);
    [Hle] void sceKernelExitDeleteThread(int status) => throw new GuestThreadExitException(status, true);

    [Hle]
    int sceKernelDeleteThread(int thid)
    {
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if (t.Status != ThreadStatus.Dormant) return SceError.NOT_DORMANT;
        K.DeleteThread(t);
        return 0;
    }

    [Hle("sceKernelWaitThreadEnd")]
    [Hle("sceKernelWaitThreadEndCB")]
    int sceKernelWaitThreadEnd(int thid, Ptr<int> stat, Ptr<uint> timeout)
    {
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if (t == Self) return SceError.ILLEGAL_THREAD_ID;
        int r;
        lock (t.WaitLock)
        {
            r = Kernel.Wait.Until(t.WaitLock, () => t.Status == ThreadStatus.Dormant, timeout, null, $"thread end {t.Name}");
        }
        if (r == SceError.OK) stat.TrySet(t.ExitStatus);
        return r == SceError.OK ? t.ExitStatus : r;
    }

    // ------------------------------------------------------------------ thread signals (libult parks/unparks workers with these)

    const int ERR_ALREADY_SENT = unchecked((int)0x8002804C); // SCE_KERNEL_ERROR_ALREADY_SENT (value unverified; callers only test != 0)

    /// <summary>sceKernelWaitSignal(unk, delay, timeout): blocks until another thread sent this thread a signal, then consumes it.</summary>
    [Hle("sceKernelWaitSignal")]
    [Hle("sceKernelWaitSignalCB")]
    [Hle("_sceKernelWaitSignal")]
    [Hle("_sceKernelWaitSignalCB")]
    int sceKernelWaitSignal(uint unk, uint delay, Ptr<uint> timeout)
    {
        var t = Self;
        lock (t.SignalLock)
        {
            int r = Kernel.Wait.Until(t.SignalLock, t, static th => th.SignalPending, timeout, null, "signal");
            if (r == SceError.OK) t.SignalPending = false;
            return r;
        }
    }

    [Hle]
    int sceKernelSendSignal(int thid)
    {
        var t = K.GetThread(thid);
        if (t == null) { Log.Warn($"sceKernelSendSignal(0x{thid:X}): unknown thread"); return SceError.UNKNOWN_THREAD_ID; }
        lock (t.SignalLock)
        {
            if (t.SignalPending) { Log.Debug($"sceKernelSendSignal({t.Name}): already sent"); return ERR_ALREADY_SENT; }
            t.SignalPending = true;
            Monitor.PulseAll(t.SignalLock);
        }
        return 0;
    }

    [Hle] int sceKernelGetThreadId() => Self.Uid;

    [Hle]
    [Hle("ksceKernelGetThreadCurrentPriority")] // same NID exported under the kernel name by early firmware
    int sceKernelGetThreadCurrentPriority() => Self.Priority;

    [Hle]
    [Hle("sceKernelChangeThreadPriority2")]
    int sceKernelChangeThreadPriority(int thid, int priority)
    {
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if (priority == 0) priority = t.InitPriority;
        t.Priority = ResolvePriority(priority);
        return 0;
    }

    [Hle]
    [Hle("sceKernelChangeThreadCpuAffinityMask2")]
    int sceKernelChangeThreadCpuAffinityMask(int thid, int mask)
    {
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        int old = t.Affinity;
        t.Affinity = mask;
        return old;
    }

    [Hle("sceKernelGetThreadCpuAffinityMask")]
    [Hle("sceKernelGetThreadCpuAffinityMask2")]
    int sceKernelGetThreadCpuAffinityMask(int thid)
    {
        var t = K.GetThread(thid);
        return t == null ? SceError.UNKNOWN_THREAD_ID : t.Affinity;
    }

    [Hle] int sceKernelChangeThreadVfpException(int clear, int set) => 0;
    [Hle] int sceKernelGetProcessId() => 0x40010003;
    [Hle] int sceKernelGetThreadmgrUIDClass(int uid) => 0;

    [Hle("sceKernelDelayThread")]
    [Hle("sceKernelDelayThreadCB")]
    int sceKernelDelayThread(uint usec)
    {
        Kernel.Wait.Sleep(usec);
        return 0;
    }

    [Hle] int sceKernelDelayThread200(uint usec) { Kernel.Wait.Sleep(Math.Max(usec, 200u)); return 0; }

    static long _getThreadInfoCalls;

    [Hle]
    int sceKernelGetThreadInfo(int thid, uint info)
    {
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if ((++_getThreadInfoCalls & 0x3FFFFF) == 0) // debug: games poll this to join threads; show what they wait for
            Log.Info($"sceKernelGetThreadInfo polled {_getThreadInfoCalls} times, now '{t.Name}' ({t.Status}, wait {t.WaitingOn}) from {Self.Name}");
        uint size = GuestMemory.R32(info);
        var buf = new byte[Math.Max(size, 0x80)];
        var w = new BinaryWriter(new MemoryStream(buf));
        w.Write(size);
        w.Write(0x40010003);                               // processId
        var name = new byte[32];
        System.Text.Encoding.UTF8.GetBytes(t.Name).AsSpan(0, Math.Min(31, System.Text.Encoding.UTF8.GetByteCount(t.Name))).CopyTo(name);
        w.Write(name);
        w.Write(t.Attr);
        w.Write((uint)t.Status);
        w.Write(t.Entry);
        w.Write(t.StackAddr);
        w.Write((int)t.StackSize);
        w.Write(t.InitPriority);
        w.Write(t.Priority);
        w.Write(t.Affinity);
        w.Write(t.Affinity);
        w.Write(0); w.Write(0);                            // cpu ids
        w.Write(0u); w.Write(0);                           // waitType / waitId
        w.Write(t.ExitStatus);
        w.Write(0ul);                                      // runClocks
        buf.AsSpan(0, (int)Math.Min(size, (uint)buf.Length)).CopyTo(GuestMemory.Span(info, (int)size));
        return 0;
    }

    [Hle]
    int sceKernelGetThreadStackFreeSize(int thid)
    {
        var t = K.GetThread(thid);
        return t == null ? 0 : (int)(t.StackSize / 2);
    }

    [Hle]
    uint sceKernelGetTLSAddr(int key)
    {
        if (key < 0 || key >= 0x100) return 0;
        return Self.TlsBase + (uint)key * 4;
    }

    /// <summary>TLS slot address of another thread (0 = the calling thread); NULL for an unknown thread or key.</summary>
    [Hle]
    uint sceKernelGetThreadTLSAddr(int thid, int key)
    {
        if (key < 0 || key >= 0x100) return 0;
        var t = K.GetThread(thid);
        return t == null ? 0 : t.TlsBase + (uint)key * 4;
    }

    [Hle] int sceKernelCheckWaitableStatus() => 0;
    [Hle] int sceKernelGetThreadExitStatus(int thid, Ptr<int> status) { var t = K.GetThread(thid); if (t == null) return SceError.UNKNOWN_THREAD_ID; if (t.Status != ThreadStatus.Dormant) return SceError.NOT_DORMANT; status.TrySet(t.ExitStatus); return 0; }

    [Hle]
    int sceKernelRegisterThreadEventHandler(string name, int thid, uint mask, uint handler, uint common)
    {
        var o = new SimpleEvent { Name = name };
        return K.Objects.Register(o);
    }

    [Hle] int sceKernelUnregisterThreadEventHandler(int uid) { K.Objects.Remove(uid); return 0; }

    // ------------------------------------------------------------------ time

    // debug bisect: VITASHARP_TSCALE_PROC applies only to calls from thread VITASHARP_TSCALE_PROC_THREAD and/or from LR in
    // VITASHARP_TSCALE_PROC_LR (comma separated hex) when those are set
    static readonly string? ScaleThread = Environment.GetEnvironmentVariable("VITASHARP_TSCALE_PROC_THREAD");
    static readonly HashSet<uint>? ScaleLr = Environment.GetEnvironmentVariable("VITASHARP_TSCALE_PROC_LR") is { Length: > 0 } s ? s.Split(',').Select(x => Convert.ToUInt32(x, 16)).ToHashSet() : null;
    unsafe ulong ProcTime()
    {
        ulong t = K.ProcessTimeUs;
        if (ScaleThread == null && ScaleLr == null) return t;
        bool match = (ScaleThread == null || Self.Name == ScaleThread) && (ScaleLr == null || ScaleLr.Contains(Self.Core.State->R[14]));
        return match ? t : (ulong)(t / Util.TimeScale.Proc);
    }
    [Hle] uint sceKernelGetProcessTimeLow() => (uint)ProcTime();
    [Hle] ulong sceKernelGetProcessTimeWide() => ProcTime();
    [Hle] int sceKernelGetProcessTime(Ptr<ulong> clock) { clock.TrySet(K.ProcessTimeUs); return 0; }
    [Hle] ulong sceKernelGetSystemTimeWide() => K.ProcessTimeUs;
    [Hle] uint sceKernelGetSystemTimeLow() => (uint)K.ProcessTimeUs;
    [Hle] int sceKernelGetSystemTime(Ptr<ulong> clock) { clock.TrySet(K.ProcessTimeUs); return 0; }

    // ------------------------------------------------------------------ semaphores

    // firmware 1.6x export used by launch-era titles: same arguments
    [Hle("sceKernelCreateSema_16XX")]
    int sceKernelCreateSema_16XX(string name, uint attr, int initVal, int maxVal, uint opt) => sceKernelCreateSema(name, attr, initVal, maxVal, opt);

    [Hle]
    int sceKernelCreateSema(string name, uint attr, int initVal, int maxVal, uint opt)
    {
        if (initVal < 0 || maxVal <= 0 || initVal > maxVal) return SceError.INVALID_ARGUMENT;
        var s = new Semaphore { Name = name, Attr = attr, InitCount = initVal, Count = initVal, MaxCount = maxVal };
        return K.Objects.Register(s);
    }

    [Hle]
    int sceKernelDeleteSema(int uid)
    {
        var s = Obj<Semaphore>(uid);
        if (s == null) return SceError.UNKNOWN_SEMA_ID;
        s.Delete();
        K.Objects.Remove(uid);
        return 0;
    }

    [Hle] int sceKernelOpenSema(string name) => K.Objects.FindByName<Semaphore>(name)?.Uid ?? SceError.UNKNOWN_SEMA_ID;
    [Hle] int sceKernelCloseSema(int uid) => 0;
    [Hle] int sceKernelSignalSema(int uid, int signal) => Obj<Semaphore>(uid)?.Signal(signal) ?? SceError.UNKNOWN_SEMA_ID;

    [Hle("sceKernelWaitSema")]
    [Hle("sceKernelWaitSemaCB")]
    int sceKernelWaitSema(int uid, int need, Ptr<uint> timeout) => Obj<Semaphore>(uid)?.WaitSema(need, timeout) ?? SceError.UNKNOWN_SEMA_ID;

    [Hle] int sceKernelPollSema(int uid, int need) => Obj<Semaphore>(uid)?.Poll(need) ?? SceError.UNKNOWN_SEMA_ID;
    [Hle] int sceKernelCancelSema(int uid, int setCount, Ptr<int> numWait) => Obj<Semaphore>(uid)?.Cancel(setCount, numWait) ?? SceError.UNKNOWN_SEMA_ID;

    [Hle]
    int sceKernelGetSemaInfo(int uid, uint info)
    {
        var s = Obj<Semaphore>(uid);
        if (s == null) return SceError.UNKNOWN_SEMA_ID;
        GuestMemory.W32(info + 4, (uint)uid);
        GuestMemory.WriteCString(info + 8, s.Name, 32);
        GuestMemory.W32(info + 40, s.Attr);
        GuestMemory.W32(info + 44, (uint)s.InitCount);
        GuestMemory.W32(info + 48, (uint)s.Count);
        GuestMemory.W32(info + 52, (uint)s.MaxCount);
        GuestMemory.W32(info + 56, (uint)s.Waiters);
        return 0;
    }

    // ------------------------------------------------------------------ event flags

    [Hle]
    int sceKernelCreateEventFlag(string name, uint attr, uint initPattern, uint opt)
        => K.Objects.Register(new EventFlag { Name = name, Attr = attr, InitPattern = initPattern, Pattern = initPattern });

    [Hle]
    int sceKernelDeleteEventFlag(int uid)
    {
        var e = Obj<EventFlag>(uid);
        if (e == null) return SceError.UNKNOWN_EVF_ID;
        e.Delete();
        K.Objects.Remove(uid);
        return 0;
    }

    [Hle] int sceKernelOpenEventFlag(string name) => K.Objects.FindByName<EventFlag>(name)?.Uid ?? SceError.UNKNOWN_EVF_ID;
    [Hle] int sceKernelCloseEventFlag(int uid) => 0;
    [Hle] int sceKernelSetEventFlag(int uid, uint bits) { var e = Obj<EventFlag>(uid); if (e == null) return SceError.UNKNOWN_EVF_ID; e.Set(bits); return 0; }
    [Hle] int sceKernelClearEventFlag(int uid, uint bits) { var e = Obj<EventFlag>(uid); if (e == null) return SceError.UNKNOWN_EVF_ID; e.Clear(bits); return 0; }

    [Hle("sceKernelWaitEventFlag")]
    [Hle("sceKernelWaitEventFlagCB")]
    int sceKernelWaitEventFlag(int uid, uint bits, uint mode, Ptr<uint> outBits, Ptr<uint> timeout)
        => Obj<EventFlag>(uid)?.WaitFlag(bits, mode, outBits, timeout) ?? SceError.UNKNOWN_EVF_ID;

    [Hle] int sceKernelPollEventFlag(int uid, uint bits, uint mode, Ptr<uint> outBits) => Obj<EventFlag>(uid)?.Poll(bits, mode, outBits) ?? SceError.UNKNOWN_EVF_ID;
    [Hle] int sceKernelCancelEventFlag(int uid, uint pattern, Ptr<int> numWait) => Obj<EventFlag>(uid)?.Cancel(pattern, numWait) ?? SceError.UNKNOWN_EVF_ID;

    [Hle]
    int sceKernelGetEventFlagInfo(int uid, uint info)
    {
        var e = Obj<EventFlag>(uid);
        if (e == null) return SceError.UNKNOWN_EVF_ID;
        GuestMemory.W32(info + 4, (uint)uid);
        GuestMemory.WriteCString(info + 8, e.Name, 32);
        GuestMemory.W32(info + 40, e.Attr);
        GuestMemory.W32(info + 44, e.InitPattern);
        GuestMemory.W32(info + 48, e.Pattern);
        GuestMemory.W32(info + 52, (uint)e.Waiters);
        return 0;
    }

    // ------------------------------------------------------------------ mutexes

    [Hle]
    int sceKernelCreateMutex(string name, uint attr, int initCount, uint opt)
    {
        var m = new Mutex { Name = name, Attr = attr, InitCount = initCount };
        int uid = K.Objects.Register(m);
        m.InitOwner(Self, initCount);
        return uid;
    }

    [Hle] int sceKernelDeleteMutex(int uid) { var m = Obj<Mutex>(uid); if (m == null) return SceError.UNKNOWN_MUTEX_ID; m.Delete(); K.Objects.Remove(uid); return 0; }
    [Hle] int sceKernelOpenMutex(string name) => K.Objects.FindByName<Mutex>(name)?.Uid ?? SceError.UNKNOWN_MUTEX_ID;
    [Hle] int sceKernelCloseMutex(int uid) => 0;

    [Hle("sceKernelLockMutex")]
    [Hle("sceKernelLockMutexCB")]
    int sceKernelLockMutex(int uid, int count, Ptr<uint> timeout) => Obj<Mutex>(uid)?.Lock(Self, count, timeout) ?? SceError.UNKNOWN_MUTEX_ID;

    [Hle] int sceKernelTryLockMutex(int uid, int count) => Obj<Mutex>(uid)?.TryLock(Self, count) ?? SceError.UNKNOWN_MUTEX_ID;
    [Hle] int sceKernelUnlockMutex(int uid, int count) => Obj<Mutex>(uid)?.Unlock(Self, count) ?? SceError.UNKNOWN_MUTEX_ID;

    // ------------------------------------------------------------------ lightweight mutexes (work area in guest memory)

    Mutex? LwMutexOf(uint work) => work == 0 ? null : Obj<Mutex>((int)GuestMemory.R32(work + 16));

    [Hle]
    int sceKernelCreateLwMutex(uint work, string name, uint attr, int initCount, uint opt)
    {
        if (work == 0) return SceError.INVALID_ARGUMENT;
        var m = new Mutex { Name = name, Attr = attr, InitCount = initCount, WorkArea = work };
        int uid = K.Objects.Register(m);
        GuestMemory.Span(work, 32).Clear();
        GuestMemory.W32(work + 12, attr);
        GuestMemory.W32(work + 16, (uint)uid);
        m.InitOwner(Self, initCount);
        return 0;
    }

    [Hle]
    int sceKernelDeleteLwMutex(uint work)
    {
        var m = LwMutexOf(work);
        if (m == null) return SceError.UNKNOWN_LW_MUTEX_ID;
        m.Delete();
        K.Objects.Remove(m.Uid);
        return 0;
    }

    [Hle("sceKernelLockLwMutex")]
    [Hle("sceKernelLockLwMutexCB")]
    int sceKernelLockLwMutex(uint work, int count, Ptr<uint> timeout) => LwMutexOf(work)?.Lock(Self, count, timeout) ?? SceError.UNKNOWN_LW_MUTEX_ID;

    [Hle] int sceKernelTryLockLwMutex(uint work, int count) => LwMutexOf(work)?.TryLock(Self, count) ?? SceError.UNKNOWN_LW_MUTEX_ID;

    [Hle("sceKernelUnlockLwMutex")]
    [Hle("sceKernelUnlockLwMutex2")]
    int sceKernelUnlockLwMutex(uint work, int count) => LwMutexOf(work)?.Unlock(Self, count) ?? SceError.LW_MUTEX_NOT_OWNED;

    [Hle]
    int sceKernelGetLwMutexInfo(uint work, uint info)
    {
        var m = LwMutexOf(work);
        if (m == null) return SceError.UNKNOWN_LW_MUTEX_ID;
        GuestMemory.W32(info + 4, (uint)m.Uid);
        GuestMemory.WriteCString(info + 8, m.Name, 32);
        GuestMemory.W32(info + 40, m.Attr);
        GuestMemory.W32(info + 44, work);
        GuestMemory.W32(info + 48, (uint)m.InitCount);
        GuestMemory.W32(info + 52, (uint)m.LockCount);
        GuestMemory.W32(info + 56, (uint)(m.Owner?.Uid ?? 0));
        GuestMemory.W32(info + 60, (uint)m.Waiters);
        return 0;
    }

    [Hle] int sceKernelGetLwMutexInfoById(int uid, uint info) => Obj<Mutex>(uid) is { } m ? sceKernelGetLwMutexInfo(m.WorkArea, info) : SceError.UNKNOWN_LW_MUTEX_ID;

    // ------------------------------------------------------------------ condition variables

    [Hle]
    int sceKernelCreateCond(string name, uint attr, int mutexId, uint opt)
    {
        var m = Obj<Mutex>(mutexId);
        if (m == null) return SceError.UNKNOWN_MUTEX_ID;
        return K.Objects.Register(new CondVar { Name = name, Attr = attr, Mutex = m });
    }

    [Hle] int sceKernelDeleteCond(int uid) { var c = Obj<CondVar>(uid); if (c == null) return SceError.UNKNOWN_COND_ID; c.Delete(); K.Objects.Remove(uid); return 0; }

    [Hle("sceKernelWaitCond")]
    [Hle("sceKernelWaitCondCB")]
    int sceKernelWaitCond(int uid, Ptr<uint> timeout) => Obj<CondVar>(uid)?.WaitCond(Self, timeout) ?? SceError.UNKNOWN_COND_ID;

    [Hle] int sceKernelSignalCond(int uid) { var c = Obj<CondVar>(uid); if (c == null) return SceError.UNKNOWN_COND_ID; c.Signal(); return 0; }
    [Hle] int sceKernelSignalCondAll(int uid) { var c = Obj<CondVar>(uid); if (c == null) return SceError.UNKNOWN_COND_ID; c.SignalAll(); return 0; }
    [Hle] int sceKernelSignalCondTo(int uid, int thid) => sceKernelSignalCond(uid);

    CondVar? LwCondOf(uint work) => work == 0 ? null : Obj<CondVar>((int)GuestMemory.R32(work));

    [Hle]
    int sceKernelCreateLwCond(uint work, string name, uint attr, uint workMutex, uint opt)
    {
        var m = LwMutexOf(workMutex);
        if (m == null) return SceError.UNKNOWN_LW_MUTEX_ID;
        int uid = K.Objects.Register(new CondVar { Name = name, Attr = attr, Mutex = m, WorkArea = work });
        GuestMemory.W32(work, (uint)uid);
        GuestMemory.W32(work + 4, (uint)m.Uid);
        return 0;
    }

    [Hle] int sceKernelDeleteLwCond(uint work) { var c = LwCondOf(work); if (c == null) return SceError.UNKNOWN_LW_COND_ID; c.Delete(); K.Objects.Remove(c.Uid); return 0; }

    [Hle("sceKernelWaitLwCond")]
    [Hle("sceKernelWaitLwCondCB")]
    int sceKernelWaitLwCond(uint work, Ptr<uint> timeout) => LwCondOf(work)?.WaitCond(Self, timeout) ?? SceError.UNKNOWN_LW_COND_ID;

    [Hle] int sceKernelSignalLwCond(uint work) { var c = LwCondOf(work); if (c == null) return SceError.UNKNOWN_LW_COND_ID; c.Signal(); return 0; }
    [Hle] int sceKernelSignalLwCondAll(uint work) { var c = LwCondOf(work); if (c == null) return SceError.UNKNOWN_LW_COND_ID; c.SignalAll(); return 0; }
    [Hle] int sceKernelSignalLwCondTo(uint work, int thid) => sceKernelSignalLwCond(work);

    // ------------------------------------------------------------------ message pipes

    [Hle]
    int sceKernelCreateMsgPipe(string name, uint type, uint attr, uint bufSize, uint opt)
        => K.Objects.Register(new MsgPipe { Name = name, Attr = attr, BufferSize = (int)Math.Max(bufSize, 1) });

    [Hle] int sceKernelDeleteMsgPipe(int uid) { var p = Obj<MsgPipe>(uid); if (p == null) return SceError.UNKNOWN_MSG_PIPE_ID; p.Delete(); K.Objects.Remove(uid); return 0; }

    [Hle("sceKernelSendMsgPipe")]
    [Hle("sceKernelSendMsgPipeCB")]
    int sceKernelSendMsgPipe(int uid, uint buf, uint size, uint mode, Ptr<uint> result, Ptr<uint> timeout)
        => Obj<MsgPipe>(uid)?.Send(buf, (int)size, mode, result, timeout, false) ?? SceError.UNKNOWN_MSG_PIPE_ID;

    [Hle] int sceKernelTrySendMsgPipe(int uid, uint buf, uint size, uint mode, Ptr<uint> result)
        => Obj<MsgPipe>(uid)?.Send(buf, (int)size, mode, result, default, true) ?? SceError.UNKNOWN_MSG_PIPE_ID;

    [Hle("sceKernelReceiveMsgPipe")]
    [Hle("sceKernelReceiveMsgPipeCB")]
    int sceKernelReceiveMsgPipe(int uid, uint buf, uint size, uint mode, Ptr<uint> result, Ptr<uint> timeout)
        => Obj<MsgPipe>(uid)?.Receive(buf, (int)size, mode, result, timeout, false) ?? SceError.UNKNOWN_MSG_PIPE_ID;

    [Hle] int sceKernelTryReceiveMsgPipe(int uid, uint buf, uint size, uint mode, Ptr<uint> result)
        => Obj<MsgPipe>(uid)?.Receive(buf, (int)size, mode, result, default, true) ?? SceError.UNKNOWN_MSG_PIPE_ID;

    [Hle] int sceKernelCancelMsgPipe(int uid, Ptr<int> numSend, Ptr<int> numRecv) => Obj<MsgPipe>(uid)?.Cancel(numSend, numRecv) ?? SceError.UNKNOWN_MSG_PIPE_ID;

    // ------------------------------------------------------------------ rw locks

    [Hle] int sceKernelCreateRWLock(string name, uint attr, uint opt) => K.Objects.Register(new RwLock { Name = name, Attr = attr });
    [Hle] int sceKernelDeleteRWLock(int uid) { var l = Obj<RwLock>(uid); if (l == null) return SceError.UNKNOWN_RW_LOCK_ID; l.Delete(); K.Objects.Remove(uid); return 0; }

    [Hle("sceKernelLockReadRWLock")]
    [Hle("sceKernelLockReadRWLockCB")]
    int sceKernelLockReadRWLock(int uid, Ptr<uint> timeout) => Obj<RwLock>(uid)?.LockRead(Self, timeout) ?? SceError.UNKNOWN_RW_LOCK_ID;

    [Hle("sceKernelLockWriteRWLock")]
    [Hle("sceKernelLockWriteRWLockCB")]
    int sceKernelLockWriteRWLock(int uid, Ptr<uint> timeout) => Obj<RwLock>(uid)?.LockWrite(Self, timeout) ?? SceError.UNKNOWN_RW_LOCK_ID;

    [Hle] int sceKernelTryLockReadRWLock(int uid) => Obj<RwLock>(uid)?.TryLockRead(Self) ?? SceError.UNKNOWN_RW_LOCK_ID;
    [Hle] int sceKernelTryLockWriteRWLock(int uid) => Obj<RwLock>(uid)?.TryLockWrite(Self) ?? SceError.UNKNOWN_RW_LOCK_ID;
    [Hle] int sceKernelUnlockReadRWLock(int uid) => Obj<RwLock>(uid)?.UnlockRead() ?? SceError.UNKNOWN_RW_LOCK_ID;
    [Hle] int sceKernelUnlockWriteRWLock(int uid) => Obj<RwLock>(uid)?.UnlockWrite(Self) ?? SceError.UNKNOWN_RW_LOCK_ID;

    [Hle]
    int sceKernelGetRWLockInfo(int uid, uint info)
    {
        var l = Obj<RwLock>(uid);
        if (l == null) return SceError.UNKNOWN_RW_LOCK_ID;
        GuestMemory.W32(info + 4, (uint)uid);
        GuestMemory.WriteCString(info + 8, l.Name, 32);
        GuestMemory.W32(info + 40, l.Attr);
        GuestMemory.W32(info + 44, (uint)(l.Writer != null ? l.WriteCount : l.Readers));
        GuestMemory.W32(info + 48, (uint)(l.Writer?.Uid ?? 0));
        GuestMemory.W32(info + 52, (uint)l.ReadWaiters);
        GuestMemory.W32(info + 56, (uint)l.WriteWaiters);
        return 0;
    }

    // ------------------------------------------------------------------ callbacks

    [Hle]
    int sceKernelCreateCallback(string name, uint attr, uint func, uint arg)
        => K.Objects.Register(new Callback { Name = name, Attr = attr, Function = func, Arg = arg, Owner = Self });

    [Hle] int sceKernelDeleteCallback(int uid) { K.Objects.Remove(uid); return 0; }

    [Hle]
    int sceKernelNotifyCallback(int uid, int arg)
    {
        var c = Obj<Callback>(uid);
        if (c == null) return SceError.UNKNOWN_CALLBACK_ID;
        c.Notify(arg);
        return 0;
    }

    [Hle]
    int sceKernelCheckCallback() => ProcessCallbacks(Self);

    [Hle]
    int sceKernelGetCallbackCount(int uid) => Obj<Callback>(uid)?.NotifyCount ?? SceError.UNKNOWN_CALLBACK_ID;

    /// <summary>Runs pending callbacks owned by the thread; returns how many ran.</summary>
    public override void Initialize() => GuestThread.CallbackProcessor = ProcessCallbacks;

    public int ProcessCallbacks(GuestThread t)
    {
        int ran = 0;
        Interlocked.Exchange(ref t.PendingCallbacks, 0);
        foreach (var cb in K.Objects.All<Callback>().Where(c => c.Owner == t).ToList())
        {
            int count, arg;
            lock (cb)
            {
                if (cb.NotifyCount == 0) continue;
                count = cb.NotifyCount;
                arg = cb.NotifyArg;
                cb.NotifyCount = 0;
            }
            uint r = t.CallGuest(cb.Function, (uint)cb.Uid, (uint)count, (uint)arg, cb.Arg);
            ran++;
            if (r != 0) K.Objects.Remove(cb.Uid);
        }
        return ran;
    }

    // ------------------------------------------------------------------ timers

    [Hle]
    int sceKernelCreateTimer(string name, uint attr, uint opt) => K.Objects.Register(new TimerObject { Name = name, Attr = attr });

    [Hle] int sceKernelDeleteTimer(int uid) { K.Objects.Remove(uid); return 0; }
    [Hle] int sceKernelStartTimer(int uid) { var t = Obj<TimerObject>(uid); if (t == null) return SceError.UNKNOWN_TIMER_ID; t.Clock.Start(); return 0; }
    [Hle] int sceKernelStopTimer(int uid) { var t = Obj<TimerObject>(uid); if (t == null) return SceError.UNKNOWN_TIMER_ID; t.Clock.Stop(); return 0; }

    [Hle]
    ulong sceKernelGetTimerTimeWide(int uid)
    {
        var t = Obj<TimerObject>(uid);
        return t == null ? 0 : t.BaseUs + (ulong)t.Clock.ElapsedMicroseconds;
    }

    [Hle]
    int sceKernelGetTimerTime(int uid, Ptr<ulong> time)
    {
        time.TrySet(sceKernelGetTimerTimeWide(uid));
        return 0;
    }

    [Hle]
    ulong sceKernelSetTimerTimeWide(int uid, ulong time)
    {
        var t = Obj<TimerObject>(uid);
        if (t == null) return 0;
        ulong old = sceKernelGetTimerTimeWide(uid);
        t.BaseUs = time;
        bool running = t.Clock.IsRunning;
        t.Clock.Reset();
        if (running) t.Clock.Start();
        return old;
    }
}
