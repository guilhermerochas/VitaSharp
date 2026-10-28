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

    /// <summary>sceKernelWaitSignal(unk, delay, timeout): blocks until another thread sent this thread a signal, then consumes it.
    /// A pending signal is consumed at once without touching timeout; delay is ignored (as Vita3K).</summary>
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
        if (thid == SCE_KERNEL_THREAD_ID_SELF) return SceError.ILLEGAL_THREAD_ID; // Vita3K
        var t = K.GetThread(thid);
        if (t == null) { Log.Warn($"sceKernelSendSignal(0x{thid:X}): unknown thread"); return SceError.UNKNOWN_THREAD_ID; }
        lock (t.SignalLock)
        {
            if (t.SignalPending) { Log.Debug($"sceKernelSendSignal({t.Name}): already sent"); return SceError.ALREADY_SENT; }
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

    // ------------------------------------------------------------------ VFP exception mask (kernel TLS word 4)

    const uint VfpExceptionBits = 0x0800009F; // IOC DZC OFC UFC IXC IDC QC

    ref int VfpExceptionWord(GuestThread t) => ref GuestMemory.Ref<int>(t.TlsBase + GuestThread.TlsVfpException * 4);

    /// <summary>mask = set | (mask &amp; ~clear). VitaSharp raises no VFP traps; the mask is only kept for the getter.</summary>
    [Hle]
    int sceKernelChangeThreadVfpException(uint clear, uint set)
    {
        if (((clear | set) & ~VfpExceptionBits) != 0 || (clear & set) != 0) return SceError.INVALID_ARGUMENT;
        ref int w = ref VfpExceptionWord(Self);
        w = (int)(set | ((uint)w & ~clear));
        return 0;
    }

    [Hle] int sceKernelGetCurrentThreadVfpException() => VfpExceptionWord(Self);

    [Hle] int sceKernelGetProcessId() => 0x40010003;

    /// <summary>Class of the threadmgr object behind a UID (thread 1 ... simple event 13; see KernelObject.UidClass).</summary>
    [Hle]
    int sceKernelGetThreadmgrUIDClass(int uid)
    {
        if (K.GetThread(uid) != null) return 1;
        var o = K.Objects.Get<KernelObject>(uid);
        return o == null || o.UidClass == 0 ? SceError.INVALID_UID : o.UidClass;
    }

    /// <summary>Only NOUSE_VFP (0x01000000) and USER (0x80000000) may change; nothing visible changes (Vita3K).</summary>
    [Hle]
    int sceKernelChangeCurrentThreadAttr(uint clearAttr, uint setAttr)
        => ((clearAttr | setAttr) & ~0x81000000u) != 0 ? SceError.ILLEGAL_ATTR : 0;

    /// <summary>Free bytes left on the calling thread's stack (sp - stack base), 0 if sp is outside it.</summary>
    [Hle]
    unsafe int sceKernelCheckThreadStack()
    {
        var t = Self;
        uint sp = t.Core.State->R[13];
        return sp >= t.StackAddr && sp <= t.StackAddr + t.StackSize ? (int)(sp - t.StackAddr) : 0;
    }

    /// <summary>SceKernelThreadRunStatus (0x34): size, then cpuInfo[4] {processId, threadId, priority}: the threads
    /// holding the three user cores; the fourth (system) core reports nothing.</summary>
    [Hle]
    int sceKernelGetThreadRunStatus(int thid, uint status)
    {
        if (K.GetThread(thid) == null) return SceError.UNKNOWN_THREAD_ID;
        Span<byte> full = stackalloc byte[0x34];
        full.Clear();
        KernelInfo.Put(full, 0, 0x34);
        int i = 0;
        foreach (var t in K.Threads)
        {
            if (i >= 3) break;
            if (!t.HasCore) continue;
            KernelInfo.Put(full, 4 + i * 12, 0x40010003);
            KernelInfo.Put(full, 8 + i * 12, t.Uid);
            KernelInfo.Put(full, 12 + i * 12, t.Priority);
            i++;
        }
        return KernelInfo.Write(status, full);
    }

    /// <summary>SceKernelSystemInfo (0x48): size, activeCpuMask, cpuInfo[4] {idleClock u64, comesOutOfIdleCount,
    /// threadSwitchCount}. Three user cores active; no scheduler statistics are kept, so counters are 0.</summary>
    [Hle]
    int sceKernelGetSystemInfo(uint info)
    {
        Span<byte> full = stackalloc byte[0x48];
        full.Clear();
        KernelInfo.Put(full, 0, 0x48);
        KernelInfo.Put(full, 4, 0x7);
        return KernelInfo.Write(info, full);
    }

    /// <summary>Runs <paramref name="f"/> as a callback-processing wait (for *CB functions whose name does not end in
    /// "CB", which the dispatcher would otherwise not recognise).</summary>
    int WithCallbacks(Func<int> f)
    {
        var t = Self;
        bool prev = t.CallbackWait;
        t.CallbackWait = true;
        try
        {
            if (t.PendingCallbacks > 0) ProcessCallbacks(t);
            return f();
        }
        finally { t.CallbackWait = prev; }
    }

    /// <summary>sceKernelCreateThreadForUser opt (0x18): +0 size, +4 stack_size, +8 attr, +0xC cpu_affinity_mask,
    /// +0x10 opt_param, +0x14 caller_address.</summary>
    [Hle]
    int sceKernelCreateThreadForUser(uint namePtr, uint entry, int initPriority, uint opt)
    {
        if (namePtr == 0) return unchecked((int)0x80022005); // SCE_KERNEL_ERROR_INVALID_MEMORY_ACCESS
        if (entry == 0) return SceError.ILLEGAL_ADDR;
        if (opt == 0) return SceError.ILLEGAL_ADDR;
        int stackSize = (int)GuestMemory.R32(opt + 4);
        uint attr = GuestMemory.R32(opt + 8);
        int affinity = (int)GuestMemory.R32(opt + 0xC);
        if (stackSize <= 0) return SceError.ILLEGAL_STACK_SIZE;
        if ((affinity & ~0x70000) != 0) return SceError.ILLEGAL_CPU_AFFINITY_MASK;
        if ((attr & ~(0x2000u | 0x01000000u | 0x04000000u | 0x80000000u)) != 0) return SceError.ILLEGAL_ATTR;
        int prio = initPriority == 0 ? Self.Priority : ResolvePriority(initPriority);
        if (prio is < 64 or > 191) return SceError.ILLEGAL_PRIORITY;
        var t = K.CreateThread(GuestMemory.ReadCString(namePtr), entry, prio, (uint)Math.Max(stackSize, 0x1000), attr, affinity);
        return t.Uid;
    }

    [Hle("sceKernelDelayThread")]
    [Hle("sceKernelDelayThreadCB")]
    int sceKernelDelayThread(uint usec)
    {
        Kernel.Wait.Sleep(usec);
        return 0;
    }

    [Hle] int sceKernelDelayThread200(uint usec) { Kernel.Wait.Sleep(Math.Max(usec, 200u)); return 0; }

    /// <summary>DelayThreadCB with the 200 us minimum; runs callbacks that arrive (name does not end in "CB").</summary>
    [Hle]
    int sceKernelDelayThreadCB200(uint usec)
    {
        WithCallbacks(() => { Kernel.Wait.Sleep(Math.Max(usec, 200u)); return 0; });
        if (Self.PendingCallbacks > 0) ProcessCallbacks(Self);
        return 0;
    }

    // firmware 0.89x NIDs of sceKernelWaitThreadEnd(CB)
    [Hle] int sceKernelWaitThreadEnd_089(int thid, Ptr<int> stat, Ptr<uint> timeout) => sceKernelWaitThreadEnd(thid, stat, timeout);
    [Hle] int sceKernelWaitThreadEndCB_089(int thid, Ptr<int> stat, Ptr<uint> timeout) => WithCallbacks(() => sceKernelWaitThreadEnd(thid, stat, timeout));

    static long _getThreadInfoCalls;

    [Hle]
    int sceKernelGetThreadInfo(int thid, uint info)
    {
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if ((++_getThreadInfoCalls & 0x3FFFFF) == 0) // debug: games poll this to join threads; show what they wait for
            Log.Info($"sceKernelGetThreadInfo polled {_getThreadInfoCalls} times, now '{t.Name}' ({t.Status}, wait {t.WaitingOn}) from {Self.Name}");
        uint size = GuestMemory.R32(info);
        // SceKernelThreadInfo built on the stack (games poll this every frame to join threads)
        int len = (int)Math.Max(size, 0x80);
        Span<byte> buf = len <= 512 ? stackalloc byte[512] : new byte[len];
        buf = buf[..len];
        buf.Clear();
        int o = 0;
        void U32(Span<byte> b, uint v) { System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v); o += 4; }
        U32(buf, size);
        U32(buf, 0x40010003);                              // processId
        var nameField = buf.Slice(o, 32);
        int nameLen = Math.Min(31, System.Text.Encoding.UTF8.GetByteCount(t.Name));
        Span<byte> nameBytes = stackalloc byte[System.Text.Encoding.UTF8.GetMaxByteCount(31)];
        if (System.Text.Encoding.UTF8.GetByteCount(t.Name) <= nameBytes.Length) System.Text.Encoding.UTF8.GetBytes(t.Name, nameBytes);
        else System.Text.Encoding.UTF8.GetBytes(t.Name).AsSpan(0, nameLen).CopyTo(nameBytes);
        nameBytes[..nameLen].CopyTo(nameField);
        o += 32;
        U32(buf, t.Attr);
        U32(buf, (uint)t.Status);
        U32(buf, t.Entry);
        U32(buf, t.StackAddr);
        U32(buf, t.StackSize);
        U32(buf, (uint)t.InitPriority);
        U32(buf, (uint)t.Priority);
        U32(buf, (uint)t.Affinity);
        U32(buf, (uint)t.Affinity);
        U32(buf, 0); U32(buf, 0);                          // cpu ids
        U32(buf, 0); U32(buf, 0);                          // waitType / waitId
        U32(buf, (uint)t.ExitStatus);
        U32(buf, 0); U32(buf, 0);                          // runClocks
        buf[..(int)Math.Min(size, (uint)buf.Length)].CopyTo(GuestMemory.Span(info, (int)size));
        return 0;
    }

    [Hle]
    unsafe int sceKernelGetThreadStackFreeSize(int thid)
    {
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if (t.Status == ThreadStatus.Dormant) return (int)t.StackSize;
        uint sp = t.Core.State->R[13]; // Vita3K: sp - stack base
        return sp > t.StackAddr && sp <= t.StackAddr + t.StackSize ? (int)(sp - t.StackAddr) : 0;
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
        // the handler is recorded but not called: VitaSharp emits no thread start/exit events (no installed game needs them)
        // thid 0 targets the calling thread, recorded by UID like the kernel does
        var o = new ThreadEventHandler { Name = name, ThreadMask = thid == SCE_KERNEL_THREAD_ID_SELF ? Self.Uid : thid, EventMask = mask, Handler = handler, Common = common };
        return K.Objects.Register(o);
    }

    /// <summary>
    /// SceKernelThreadEventInfo (0x40, layout from the FW 3.60 threadmgr): +0 size, +4 uid, +8 name[32], +0x28 attr,
    /// +0x2C event mask, +0x30 target thread (UID or the SCE_KERNEL_THREAD_ID_USER/... group value), +0x34 -1 for a
    /// specific target or 0 for a thread group (the kernel's internal process field, approximated), +0x38 handler,
    /// +0x3C common. The SceLibKernel wrapper passes <c>info->size</c> to the syscall in an 8-byte opt.
    /// </summary>
    [Hle]
    int sceKernelGetThreadEventInfo(int uid, uint info)
    {
        if (info != 0 && GuestMemory.R32(info) > 0x40) return SceError.NO_MEMORY;
        if (Obj<ThreadEventHandler>(uid) is not { } h) return SceError.UNKNOWN_THREAD_EVENT_ID;
        Span<byte> full = stackalloc byte[0x40];
        full.Clear();
        KernelInfo.Header(full, uid, h.Name, 0);
        KernelInfo.Put(full, 0x2C, h.EventMask);
        KernelInfo.Put(full, 0x30, h.ThreadMask);
        KernelInfo.Put(full, 0x34, h.ThreadMask == 0x10027 ? 0 : -1);
        KernelInfo.Put(full, 0x38, h.Handler);
        KernelInfo.Put(full, 0x3C, h.Common);
        return KernelInfo.Write(info, full);
    }

    /// <summary>Syscall form: the copy size comes from <c>opt->size</c> (8-byte opt) instead of <c>info->size</c>.</summary>
    [Hle]
    int _sceKernelGetThreadEventInfo(int uid, uint info, uint opt)
    {
        if (info != 0 && opt != 0 && GuestMemory.R32(opt) > 0x40) return SceError.NO_MEMORY;
        if (info != 0 && opt != 0) GuestMemory.W32(info, GuestMemory.R32(opt));
        return sceKernelGetThreadEventInfo(uid, info);
    }

    /// <summary>
    /// <c>int sceKernelCallWithChangeStack(void *stackBase, SceSize stackSize, int (*func)(void *), void *arg)</c>
    /// (FW 3.60 SceLibKernel): runs <c>func(arg)</c> with sp = stackBase + stackSize, the kernel TLS stack bounds
    /// (+8 top, +0xC bottom) switched to the new stack for the duration, and returns func's result. The firmware
    /// probes the first and last word of the stack (a bad stack faults); here an empty one is rejected instead.
    /// </summary>
    [Hle]
    int sceKernelCallWithChangeStack(uint stackBase, uint stackSize, uint func, uint arg)
    {
        if (stackBase == 0 || stackSize < 8 || func == 0) return SceError.ILLEGAL_ADDR;
        var self = Self;
        var tls = GuestMemory.Span<int>(self.TlsBase, (int)(GuestThread.KernelTlsSize / 4));
        int top = tls[GuestThread.TlsSpTop], bottom = tls[GuestThread.TlsSpBottom];
        uint newTop = stackBase + stackSize;
        tls[GuestThread.TlsSpTop] = (int)newTop;
        tls[GuestThread.TlsSpBottom] = (int)stackBase;
        try { return (int)self.CallGuestOnStack(func, newTop, arg, 0xDEADBEEF, 0xDEADBEEF, 0xDEADBEEF); }
        finally
        {
            tls = GuestMemory.Span<int>(self.TlsBase, (int)(GuestThread.KernelTlsSize / 4));
            tls[GuestThread.TlsSpTop] = top;
            tls[GuestThread.TlsSpBottom] = bottom;
        }
    }

    [Hle]
    int sceKernelUnregisterThreadEventHandler(int uid)
    {
        if (K.Objects.Get<ThreadEventHandler>(uid) == null) return SceError.UNKNOWN_THREAD_EVENT_ID;
        K.Objects.Remove(uid);
        return 0;
    }

    // ------------------------------------------------------------------ time

    // debug bisect: VITASHARP_TSCALE_PROC applies only to calls from thread VITASHARP_TSCALE_PROC_THREAD and/or from LR in
    // VITASHARP_TSCALE_PROC_LR (comma separated hex) when those are set
    static readonly string? ScaleThread = Environment.GetEnvironmentVariable(EnvVars.TscaleProcThread);
    static readonly HashSet<uint>? ScaleLr = Environment.GetEnvironmentVariable(EnvVars.TscaleProcLr) is { Length: > 0 } s ? s.Split(',').Select(x => Convert.ToUInt32(x, 16)).ToHashSet() : null;
    unsafe ulong ProcTime()
    {
        ulong t = K.ProcessTimeUs;
        if (ScaleThread == null && ScaleLr == null) return t;
        bool match = (ScaleThread == null || Self.Name == ScaleThread) && (ScaleLr == null || ScaleLr.Contains(Self.Core.State->R[14]));
        return match ? t : (ulong)(t / Util.TimeScale.Proc);
    }
    // the *Core forms are the SceProcessmgr syscalls behind the SceLibKernel wrappers
    [Hle]
    [Hle("sceKernelGetProcessTimeLowCore")]
    uint sceKernelGetProcessTimeLow() => (uint)ProcTime();
    [Hle]
    [Hle("sceKernelGetProcessTimeWideCore")]
    ulong sceKernelGetProcessTimeWide() => ProcTime();
    [Hle] int sceKernelGetProcessTime(Ptr<ulong> clock) { clock.TrySet(ProcTime()); return 0; }
    /// <summary>Copies the 8-byte clock to user memory; a NULL pointer fails the copy (error code approximated).</summary>
    [Hle]
    int sceKernelGetProcessTimeCore(Ptr<ulong> clock)
    {
        if (clock.IsNull) return SceError.ILLEGAL_ADDR;
        clock.Value = ProcTime();
        return 0;
    }
    [Hle] ulong sceKernelGetSystemTimeWide() => K.ProcessTimeUs;
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

    [Hle] int sceKernelOpenSema(uint name) => KernelInfo.OpenByName<Semaphore>(K.Objects, name);
    [Hle] int sceKernelCloseSema(int uid) => KernelInfo.CloseHandle<Semaphore>(K.Objects, uid, SceError.UNKNOWN_SEMA_ID);
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
        Span<byte> f = stackalloc byte[0x3C]; // SceKernelSemaInfo
        f.Clear();
        lock (s)
        {
            KernelInfo.Header(f, uid, s.Name, s.Attr);
            KernelInfo.Put(f, 0x2C, s.InitCount);
            KernelInfo.Put(f, 0x30, s.Count);
            KernelInfo.Put(f, 0x34, s.MaxCount);
            KernelInfo.Put(f, 0x38, s.Waiters);
        }
        return KernelInfo.Write(info, f);
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

    [Hle] int sceKernelOpenEventFlag(uint name) => KernelInfo.OpenByName<EventFlag>(K.Objects, name);
    [Hle] int sceKernelCloseEventFlag(int uid) => KernelInfo.CloseHandle<EventFlag>(K.Objects, uid, SceError.UNKNOWN_EVF_ID);
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
        Span<byte> f = stackalloc byte[0x38]; // SceKernelEventFlagInfo
        f.Clear();
        lock (e)
        {
            KernelInfo.Header(f, uid, e.Name, e.Attr);
            KernelInfo.Put(f, 0x2C, e.InitPattern);
            KernelInfo.Put(f, 0x30, e.Pattern);
            KernelInfo.Put(f, 0x34, e.Waiters);
        }
        return KernelInfo.Write(info, f);
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
    [Hle]
    [Hle("sceKernelOpenMutex_089")]
    int sceKernelOpenMutex(uint name) => KernelInfo.OpenByName<Mutex>(K.Objects, name, static m => !m.IsLight);

    [Hle]
    [Hle("sceKernelCloseMutex_089")]
    int sceKernelCloseMutex(int uid) => KernelInfo.CloseHandle<Mutex>(K.Objects, uid, SceError.UNKNOWN_MUTEX_ID, static m => !m.IsLight);

    /// <summary>SceKernelMutexInfo (0x3C): +0x2C initCount, +0x30 currentCount, +0x34 currentOwnerId, +0x38 numWaitThreads.
    /// The pointer is checked before the UID (Vita3K).</summary>
    [Hle]
    int sceKernelGetMutexInfo(int uid, uint info)
    {
        if (info == 0) return SceError.INVALID_ARGUMENT;
        if (GuestMemory.R32(info) > 0x3C) return SceError.NO_MEMORY;
        var m = Obj<Mutex>(uid);
        if (m == null || m.IsLight) return SceError.UNKNOWN_MUTEX_ID;
        Span<byte> f = stackalloc byte[0x3C];
        f.Clear();
        lock (m)
        {
            KernelInfo.Header(f, uid, m.Name, m.Attr);
            KernelInfo.Put(f, 0x2C, m.InitCount);
            KernelInfo.Put(f, 0x30, m.LockCount);
            KernelInfo.Put(f, 0x34, m.Owner?.Uid ?? 0);
            KernelInfo.Put(f, 0x38, m.Waiters);
        }
        return KernelInfo.Write(info, f);
    }

    [Hle("sceKernelLockMutex")]
    [Hle("sceKernelLockMutexCB")]
    int sceKernelLockMutex(int uid, int count, Ptr<uint> timeout) => Obj<Mutex>(uid)?.Lock(Self, count, timeout) ?? SceError.UNKNOWN_MUTEX_ID;

    [Hle] int sceKernelTryLockMutex(int uid, int count) => Obj<Mutex>(uid)?.TryLock(Self, count) ?? SceError.UNKNOWN_MUTEX_ID;
    [Hle] int sceKernelUnlockMutex(int uid, int count) => Obj<Mutex>(uid)?.Unlock(Self, count) ?? SceError.UNKNOWN_MUTEX_ID;

    /// <summary>psp2/kernel/threadmgr/mutex.h: int (SceUID mutexid, int newCount, int *numWaitThreads).</summary>
    [Hle]
    int sceKernelCancelMutex(int uid, int newCount, Ptr<int> numWait)
        => Obj<Mutex>(uid) is { IsLight: false } m ? m.Cancel(Self, newCount, numWait) : SceError.UNKNOWN_MUTEX_ID;

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
        Span<byte> f = stackalloc byte[0x40]; // SceKernelLwMutexInfo
        f.Clear();
        lock (m)
        {
            KernelInfo.Header(f, m.Uid, m.Name, m.Attr);
            KernelInfo.Put(f, 0x2C, work);
            KernelInfo.Put(f, 0x30, m.InitCount);
            KernelInfo.Put(f, 0x34, m.LockCount);
            KernelInfo.Put(f, 0x38, m.Owner?.Uid ?? 0);
            KernelInfo.Put(f, 0x3C, m.Waiters);
        }
        return KernelInfo.Write(info, f);
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
    /// <summary>Wakes the given thread if it waits on the cond; ILLEGAL_THREAD_ID when it does not (Vita3K).</summary>
    [Hle]
    int sceKernelSignalCondTo(int uid, int thid)
    {
        var c = Obj<CondVar>(uid);
        if (c == null || c.WorkArea != 0) return SceError.UNKNOWN_COND_ID;
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        return c.SignalTo(t) ? 0 : SceError.ILLEGAL_THREAD_ID;
    }

    [Hle] int sceKernelOpenCond(uint name) => KernelInfo.OpenByName<CondVar>(K.Objects, name, static c => c.WorkArea == 0);
    [Hle] int sceKernelCloseCond(int uid) => KernelInfo.CloseHandle<CondVar>(K.Objects, uid, SceError.UNKNOWN_COND_ID, static c => c.WorkArea == 0);

    /// <summary>SceKernelCondInfo (0x34): +0x2C mutexId, +0x30 numWaitThreads.</summary>
    [Hle]
    int sceKernelGetCondInfo(int uid, uint info)
    {
        var c = Obj<CondVar>(uid);
        if (c == null || c.WorkArea != 0) return SceError.UNKNOWN_COND_ID;
        Span<byte> f = stackalloc byte[0x34];
        f.Clear();
        lock (c)
        {
            KernelInfo.Header(f, uid, c.Name, c.Attr);
            KernelInfo.Put(f, 0x2C, c.Mutex.Uid);
            KernelInfo.Put(f, 0x30, c.Waiters);
        }
        return KernelInfo.Write(info, f);
    }

    /// <summary>SceKernelLwCondInfo (0x38): +0x2C work area, +0x30 lw mutex work area, +0x34 numWaitThreads.</summary>
    [Hle]
    int sceKernelGetLwCondInfoById(int uid, uint info)
    {
        var c = Obj<CondVar>(uid);
        if (c == null || c.WorkArea == 0) return SceError.UNKNOWN_LW_COND_ID;
        Span<byte> f = stackalloc byte[0x38];
        f.Clear();
        lock (c)
        {
            KernelInfo.Header(f, uid, c.Name, c.Attr);
            KernelInfo.Put(f, 0x2C, c.WorkArea);
            KernelInfo.Put(f, 0x30, c.Mutex.WorkArea);
            KernelInfo.Put(f, 0x34, c.Waiters);
        }
        return KernelInfo.Write(info, f);
    }

    [Hle]
    int sceKernelGetLwCondInfo(uint work, uint info) => LwCondOf(work) is { } c ? sceKernelGetLwCondInfoById(c.Uid, info) : SceError.UNKNOWN_LW_COND_ID;

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
    [Hle]
    int sceKernelSignalLwCondTo(uint work, int thid)
    {
        var c = LwCondOf(work);
        if (c == null) return SceError.UNKNOWN_LW_COND_ID;
        var t = K.GetThread(thid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        return c.SignalTo(t) ? 0 : SceError.ILLEGAL_THREAD_ID;
    }

    // ------------------------------------------------------------------ message pipes

    [Hle]
    int sceKernelCreateMsgPipe(string name, uint type, uint attr, uint bufSize, uint opt)
        => K.Objects.Register(new MsgPipe { Name = name, Attr = attr, BufferSize = (int)Math.Max(bufSize, 1), CreatorThreadUid = Self.Uid });

    /// <summary>Same arguments as sceKernelCreateMsgPipe; the "LR" (caller address kept for debugging) is not used.</summary>
    [Hle] int sceKernelCreateMsgPipeWithLR(string name, uint type, uint attr, uint bufSize, uint opt) => sceKernelCreateMsgPipe(name, type, attr, bufSize, opt);

    [Hle] int sceKernelGetMsgPipeCreatorId(int uid) => Obj<MsgPipe>(uid)?.CreatorThreadUid ?? SceError.UNKNOWN_MSG_PIPE_ID;
    [Hle] int sceKernelOpenMsgPipe(uint name) => KernelInfo.OpenByName<MsgPipe>(K.Objects, name);
    [Hle] int sceKernelCloseMsgPipe(int uid) => KernelInfo.CloseHandle<MsgPipe>(K.Objects, uid, SceError.UNKNOWN_MSG_PIPE_ID);

    /// <summary>SceKernelMppInfo (0x3C): +0x2C bufSize, +0x30 freeSize, +0x34 numSendWaitThreads, +0x38 numReceiveWaitThreads.</summary>
    [Hle]
    int sceKernelGetMsgPipeInfo(int uid, uint info)
    {
        var p = Obj<MsgPipe>(uid);
        if (p == null) return SceError.UNKNOWN_MSG_PIPE_ID;
        Span<byte> f = stackalloc byte[0x3C];
        f.Clear();
        lock (p)
        {
            KernelInfo.Header(f, uid, p.Name, p.Attr);
            KernelInfo.Put(f, 0x2C, p.BufferSize);
            KernelInfo.Put(f, 0x30, p.FreeSize);
            KernelInfo.Put(f, 0x34, p.SendWaiters);
            KernelInfo.Put(f, 0x38, p.RecvWaiters);
        }
        return KernelInfo.Write(info, f);
    }

    /// <summary>Reads a MsgPipeVector array ({void *base; SceSize size;} x count). NULL array or any NULL base →
    /// ILLEGAL_ADDR, count 0 → ILLEGAL_COUNT.</summary>
    static int ReadVector(uint vec, uint count, out MsgPipe.Segments segs)
    {
        segs = default;
        if (vec == 0) return SceError.ILLEGAL_ADDR;
        if (count == 0) return SceError.ILLEGAL_COUNT;
        var addrs = new uint[count];
        var sizes = new int[count];
        for (uint i = 0; i < count; i++)
        {
            addrs[i] = GuestMemory.R32(vec + i * 8);
            sizes[i] = (int)GuestMemory.R32(vec + i * 8 + 4);
            if (addrs[i] == 0) return SceError.ILLEGAL_ADDR;
        }
        segs = new MsgPipe.Segments(addrs, sizes);
        return 0;
    }

    int MsgPipeVector(int uid, uint vec, uint count, uint mode, Ptr<uint> result, Ptr<uint> timeout, bool send, bool poll)
    {
        var p = Obj<MsgPipe>(uid);
        if (p == null) { if (poll) result.TrySet(0); return SceError.UNKNOWN_MSG_PIPE_ID; }
        int e = ReadVector(vec, count, out var segs);
        if (e != 0) { if (poll) result.TrySet(0); return e; }
        if (segs.Total == 0) { result.TrySet(0); return 0; }
        int r = send ? p.Send(segs, mode, result, timeout, poll) : p.Receive(segs, mode, result, timeout, poll);
        if (r < 0 && (poll || r is SceError.WAIT_TIMEOUT or SceError.WAIT_DELETE or SceError.WAIT_CANCEL)) result.TrySet(0);
        return r;
    }

    [Hle("sceKernelSendMsgPipeVector")]
    [Hle("sceKernelSendMsgPipeVectorCB")]
    int sceKernelSendMsgPipeVector(int uid, uint vec, uint count, uint mode, Ptr<uint> result, Ptr<uint> timeout)
        => MsgPipeVector(uid, vec, count, mode, result, timeout, true, false);

    [Hle] int sceKernelTrySendMsgPipeVector(int uid, uint vec, uint count, uint mode, Ptr<uint> result)
        => MsgPipeVector(uid, vec, count, mode, result, default, true, true);

    [Hle("sceKernelReceiveMsgPipeVector")]
    [Hle("sceKernelReceiveMsgPipeVectorCB")]
    int sceKernelReceiveMsgPipeVector(int uid, uint vec, uint count, uint mode, Ptr<uint> result, Ptr<uint> timeout)
        => MsgPipeVector(uid, vec, count, mode, result, timeout, false, false);

    [Hle] int sceKernelTryReceiveMsgPipeVector(int uid, uint vec, uint count, uint mode, Ptr<uint> result)
        => MsgPipeVector(uid, vec, count, mode, result, default, false, true);

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
        Span<byte> f = stackalloc byte[0x3C]; // SceKernelRWLockInfo
        f.Clear();
        lock (l)
        {
            KernelInfo.Header(f, uid, l.Name, l.Attr);
            KernelInfo.Put(f, 0x2C, l.Writer != null ? l.WriteCount : l.Readers);
            KernelInfo.Put(f, 0x30, l.Writer?.Uid ?? 0);
            KernelInfo.Put(f, 0x34, l.ReadWaiters);
            KernelInfo.Put(f, 0x38, l.WriteWaiters);
        }
        return KernelInfo.Write(info, f);
    }

    [Hle] int sceKernelOpenRWLock(uint name) => KernelInfo.OpenByName<RwLock>(K.Objects, name);
    [Hle] int sceKernelCloseRWLock(int uid) => KernelInfo.CloseHandle<RwLock>(K.Objects, uid, SceError.UNKNOWN_RW_LOCK_ID);

    /// <summary>Wakes all readers and writers with WAIT_CANCEL; flag 1 (CANCEL_WITH_WRITE_LOCK) leaves the caller owning
    /// the lock for writing.</summary>
    [Hle]
    int sceKernelCancelRWLock(int uid, Ptr<uint> numRead, Ptr<uint> numWrite, int flag)
        => Obj<RwLock>(uid)?.Cancel(Self, numRead, numWrite, (flag & 1) != 0) ?? SceError.UNKNOWN_RW_LOCK_ID;

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

    /// <summary>
    /// SceKernelCallbackInfo, Vita3K layout (0x44): +0x28 attr, +0x2C threadId, +0x30 callbackFunc, +0x34 notifyId,
    /// +0x38 notifyCount, +0x3C notifyArg, +0x40 pCommon. The SDK header (0x3C, "needs confirmation") lacks attr and
    /// notifyId; a caller passing size 0x3C gets the first 0x3C bytes of this layout.
    /// </summary>
    [Hle]
    int sceKernelGetCallbackInfo(int uid, uint info)
    {
        var c = Obj<Callback>(uid);
        if (c == null) return SceError.UNKNOWN_CALLBACK_ID;
        Span<byte> f = stackalloc byte[0x44];
        f.Clear();
        lock (c)
        {
            KernelInfo.Header(f, uid, c.Name, c.Attr);
            KernelInfo.Put(f, 0x2C, c.Owner?.Uid ?? 0);
            KernelInfo.Put(f, 0x30, c.Function);
            KernelInfo.Put(f, 0x34, c.NotifyId);
            KernelInfo.Put(f, 0x38, c.NotifyCount);
            KernelInfo.Put(f, 0x3C, c.NotifyArg);
            KernelInfo.Put(f, 0x40, c.Arg);
        }
        return KernelInfo.Write(info, f);
    }

    /// <summary>Drops pending notifications without running the callback.</summary>
    [Hle]
    int sceKernelCancelCallback(int uid)
    {
        var c = Obj<Callback>(uid);
        if (c == null) return SceError.UNKNOWN_CALLBACK_ID;
        lock (c)
        {
            if (c.NotifyCount > 0 && c.Owner != null)
            {
                int p;
                while ((p = Volatile.Read(ref c.Owner.PendingCallbacks)) > 0 && Interlocked.CompareExchange(ref c.Owner.PendingCallbacks, p - 1, p) != p) { }
            }
            c.NotifyCount = 0;
            c.NotifyArg = 0;
        }
        return 0;
    }

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

    TimerObject? Timer(int uid) => Obj<TimerObject>(uid);

    [Hle]
    int sceKernelCreateTimer(string name, uint attr, uint opt) => K.Objects.Register(new TimerObject { Name = name, Attr = attr, Clock = () => K.ProcessTimeUs });

    [Hle]
    int sceKernelDeleteTimer(int uid)
    {
        var t = Timer(uid);
        if (t == null) return SceError.UNKNOWN_TIMER_ID;
        t.Delete();
        K.Objects.Remove(uid);
        return 0;
    }

    /// <summary>0 when it started the timer, 1 when it was already running.</summary>
    [Hle] int sceKernelStartTimer(int uid) => Timer(uid)?.Start() ?? SceError.UNKNOWN_TIMER_ID;

    /// <summary>1 when it stopped a running timer, 0 when it was already stopped.</summary>
    [Hle] int sceKernelStopTimer(int uid) => Timer(uid)?.Stop() ?? SceError.UNKNOWN_TIMER_ID;

    /// <summary>Count in microseconds; an unknown timer gives all ones (an error code could pass for a time).</summary>
    [Hle] ulong sceKernelGetTimerTimeWide(int uid) => Timer(uid)?.Count() ?? ulong.MaxValue;

    [Hle]
    int sceKernelGetTimerTime(int uid, Ptr<ulong> time)
    {
        var t = Timer(uid);
        if (t == null) return SceError.UNKNOWN_TIMER_ID;
        if (time.IsNull) return SceError.ILLEGAL_ADDR;
        time.Value = t.Count();
        return 0;
    }

    [Hle] ulong sceKernelSetTimerTimeWide(int uid, ulong time) => Timer(uid)?.SetTime(time) ?? ulong.MaxValue;

    /// <summary>In/out: writes the new count and returns the old one through the same pointer.</summary>
    [Hle]
    int sceKernelSetTimerTime(int uid, Ptr<ulong> time)
    {
        var t = Timer(uid);
        if (t == null) return SceError.UNKNOWN_TIMER_ID;
        if (time.IsNull) return SceError.ILLEGAL_ADDR;
        time.Value = t.SetTime(time.Value);
        return 0;
    }

    /// <summary>Process time the count runs from (0 if the timer never started).</summary>
    [Hle] ulong sceKernelGetTimerBaseWide(int uid) => Timer(uid) is { } t ? (t.Started || t.StoppedCount != 0 ? t.Base : 0) : ulong.MaxValue;

    [Hle]
    int sceKernelGetTimerBase(int uid, Ptr<ulong> baseTime)
    {
        var t = Timer(uid);
        if (t == null) return SceError.UNKNOWN_TIMER_ID;
        if (baseTime.IsNull) return SceError.ILLEGAL_ADDR;
        baseTime.Value = sceKernelGetTimerBaseWide(uid);
        return 0;
    }

    /// <summary>type 0 = SET_EVENT, 1 = PULSE_EVENT; when running the next event is now + interval.</summary>
    [Hle]
    int sceKernelSetTimerEvent(int uid, int type, Ptr<ulong> interval, int repeat)
    {
        var t = Timer(uid);
        if (t == null) return SceError.UNKNOWN_TIMER_ID;
        if (type is not (0 or 1)) return SceError.ILLEGAL_TYPE;
        if (interval.IsNull) return SceError.ILLEGAL_ADDR;
        return t.SetEvent(type, interval.Value, repeat != 0);
    }

    [Hle] int sceKernelCancelTimer(int uid, Ptr<uint> numWait) => Timer(uid)?.Cancel(numWait) ?? SceError.UNKNOWN_TIMER_ID;

    /// <summary>Time until the armed event; EVENT_NOT_SET (0x80028242, a confirmar) when none is armed.</summary>
    [Hle]
    int sceKernelGetTimerEventRemainingTime(int uid, Ptr<ulong> remaining)
    {
        var t = Timer(uid);
        if (t == null) return SceError.UNKNOWN_TIMER_ID;
        if (remaining.IsNull) return SceError.ILLEGAL_ADDR;
        lock (t)
        {
            if (t.NextEvent == ulong.MaxValue) return unchecked((int)0x80028242);
            ulong now = K.ProcessTimeUs;
            remaining.Value = t.NextEvent > now ? t.NextEvent - now : 0;
        }
        return 0;
    }

    /// <summary>SceKernelTimerInfo (0x60): +0x2C active, +0x30 base_time (count when stopped), +0x38 current_time,
    /// +0x40 schedule (next event in timer time, 0 = none), +0x48 interval, +0x50 type, +0x54 repeat,
    /// +0x58 num_wait_threads.</summary>
    [Hle]
    int sceKernelGetTimerInfo(int uid, uint info)
    {
        var t = Timer(uid);
        if (t == null) return SceError.UNKNOWN_TIMER_ID;
        Span<byte> f = stackalloc byte[0x60];
        f.Clear();
        lock (t)
        {
            KernelInfo.Header(f, uid, t.Name, t.Attr);
            KernelInfo.Put(f, 0x2C, t.Started ? 1 : 0);
            KernelInfo.Put64(f, 0x30, t.Started ? t.Base : t.StoppedCount);
            KernelInfo.Put64(f, 0x38, t.Count());
            KernelInfo.Put64(f, 0x40, t.NextEvent == ulong.MaxValue ? 0 : t.NextEvent - t.Base);
            KernelInfo.Put64(f, 0x48, t.Interval);
            KernelInfo.Put(f, 0x50, t.Pulse ? 1 : 0);
            KernelInfo.Put(f, 0x54, t.Repeat ? 1 : 0);
            KernelInfo.Put(f, 0x58, t.NumWaiters);
        }
        return KernelInfo.Write(info, f);
    }

    [Hle] int sceKernelOpenTimer(uint name) => KernelInfo.OpenByName<TimerObject>(K.Objects, name);
    [Hle] int sceKernelCloseTimer(int uid) => KernelInfo.CloseHandle<TimerObject>(K.Objects, uid, SceError.UNKNOWN_TIMER_ID);

    // ------------------------------------------------------------------ simple events (sceKernelWaitEvent & co also accept timers)

    SimpleEvent? Event(int uid) => Obj<SimpleEvent>(uid);

    [Hle]
    int sceKernelCreateSimpleEvent(uint namePtr, uint attr, uint initPattern, uint opt)
    {
        if (namePtr == 0) return SceError.ILLEGAL_ADDR;
        string name = GuestMemory.ReadCString(namePtr, 256);
        if ((attr & EventBits.AttrOpenable) != 0 && System.Text.Encoding.UTF8.GetByteCount(name) > 31) return SceError.UID_NAME_TOO_LONG;
        if ((attr & ~(EventBits.AttrAutoReset | EventBits.AttrThPrio | EventBits.AttrOpenable)) != 0) return SceError.ILLEGAL_ATTR;
        if ((initPattern & ~EventBits.UserMask) != 0) return SceError.ILLEGAL_PATTERN;
        return K.Objects.Register(new SimpleEvent { Name = name, Attr = attr, Pattern = initPattern | EventBits.Create });
    }

    [Hle]
    int sceKernelDeleteSimpleEvent(int uid)
    {
        var e = Event(uid);
        if (e == null) return SceError.UNKNOWN_EVENT_ID;
        e.Delete();
        K.Objects.Remove(uid);
        return 0;
    }

    [Hle] int sceKernelOpenSimpleEvent(uint name) => KernelInfo.OpenByName<SimpleEvent>(K.Objects, name, static e => (e.Attr & EventBits.AttrOpenable) != 0);
    [Hle] int sceKernelCloseSimpleEvent(int uid) => KernelInfo.CloseHandle<SimpleEvent>(K.Objects, uid, SceError.UNKNOWN_EVENT_ID);

    [Hle] int sceKernelSetEvent(int uid, uint setPattern, ulong userData) => Event(uid)?.SetOrPulse(setPattern, userData, false) ?? SceError.UNKNOWN_EVENT_ID;
    [Hle] int sceKernelPulseEvent(int uid, uint setPattern, ulong userData) => Event(uid)?.SetOrPulse(setPattern, userData, true) ?? SceError.UNKNOWN_EVENT_ID;

    /// <summary>Keeps only the given bits; on a timer it clears the event state.</summary>
    [Hle]
    int sceKernelClearEvent(int uid, uint keepPattern)
    {
        if (Event(uid) is { } e) return e.Clear(keepPattern);
        if (Timer(uid) is { } t) return t.Clear();
        return SceError.UNKNOWN_EVENT_ID;
    }

    [Hle("sceKernelWaitEvent")]
    [Hle("sceKernelWaitEventCB")]
    int sceKernelWaitEvent(int uid, uint waitPattern, Ptr<uint> resultPattern, Ptr<ulong> userData, Ptr<uint> timeout)
    {
        if (Event(uid) is { } e) return e.Wait(Self, waitPattern, resultPattern, userData, timeout);
        if (Timer(uid) is { } t) return t.Wait(resultPattern, userData, timeout);
        return SceError.UNKNOWN_EVENT_ID;
    }

    [Hle]
    int sceKernelPollEvent(int uid, uint waitPattern, Ptr<uint> resultPattern, Ptr<ulong> userData)
    {
        if (Event(uid) is { } e) return e.Poll(waitPattern, resultPattern, userData);
        if (Timer(uid) is { } t) return t.Poll(resultPattern, userData);
        return SceError.UNKNOWN_EVENT_ID;
    }

    [Hle]
    int sceKernelCancelEvent(int uid, Ptr<uint> numWait)
    {
        if (Event(uid) is { } e) return e.Cancel(numWait);
        if (Timer(uid) is { } t) return t.Cancel(numWait);
        return SceError.UNKNOWN_EVENT_ID;
    }

    [Hle]
    int sceKernelGetEventPattern(int uid, Ptr<uint> pattern)
    {
        var e = Event(uid);
        if (e == null) return SceError.UNKNOWN_EVENT_ID;
        if (pattern.IsNull) return SceError.ILLEGAL_ADDR;
        lock (e) pattern.Value = e.Pattern;
        return 0;
    }
}
