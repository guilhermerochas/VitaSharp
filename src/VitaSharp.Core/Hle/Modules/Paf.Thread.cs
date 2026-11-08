using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::thread: the locks are the kernel's, in the object. Mutex and RMutex are an SceKernelLwMutexWork (0x20 bytes;
/// option bit 0 clear makes them openable, attribute 0x2000; RMutex adds the recursive attribute), Cond is { Mutex*,
/// SceKernelLwCondWork }, Semaphore and RWLock hold a kernel UID (RWLock an event flag with pattern 3 and a counter),
/// SyncCall an event flag and the pending call. libpaf stops the program when the kernel refuses a lock call; the HLE
/// logs it. Thread is { vtable, SceUID +4, started +8, alive +9, canceled +0xA }; its kernel thread runs the object's
/// EntryFunction (virtual slot 2) and clears "alive" when it returns.
/// </summary>
public sealed partial class Paf
{
    ThreadMgr? _tm;
    ThreadMgr Tm => _tm ??= Emu.Hle.Modules.OfType<ThreadMgr>().First();
    const int KernelTimeout = unchecked((int)0x80028005), LwMutexFailedToOwn = unchecked((int)0x80028185);

    static void Check(int r, string what) { if (r < 0) Util.Log.Warn($"libpaf {what}: kernel error 0x{r:X8}"); }

    uint TimeoutPtr(int ms)
    {
        if (ms <= 0) return 0;
        uint p = Alloc(4);
        U(p, (uint)ms * 1000);
        return p;
    }

    // ---- Mutex / RMutex
    [Hle("_ZN3paf6thread5MutexC1EPKci")]
    [Hle("_ZN3paf6thread5MutexC2EPKci")]
    uint MutexCtor(uint me, uint name, int option)
    {
        Check(Tm.sceKernelCreateLwMutex(me, CRead(name), (option & 1) != 0 ? 0u : 0x2000u, 0, 0), "Mutex");
        return me;
    }

    [Hle("_ZN3paf6thread6RMutexC1EPKci")]
    [Hle("_ZN3paf6thread6RMutexC2EPKci")]
    uint RMutexCtor(uint me, uint name, int option)
    {
        Check(Tm.sceKernelCreateLwMutex(me, CRead(name), ((option & 1) != 0 ? 0u : 0x2000u) | 2, 0, 0), "RMutex");
        return me;
    }

    [Hle("_ZN3paf6thread5MutexD1Ev")]
    [Hle("_ZN3paf6thread5MutexD2Ev")]
    [Hle("_ZN3paf6thread6RMutexD1Ev")]
    [Hle("_ZN3paf6thread6RMutexD2Ev")]
    uint MutexDtor(uint me) { Check(Tm.sceKernelDeleteLwMutex(me), "~Mutex"); return me; }

    [Hle("_ZN3paf6thread5Mutex4LockEv")]
    [Hle("_ZN3paf6thread6RMutex4LockEv")]
    void MutexLock(uint me) => Check(Tm.sceKernelLockLwMutex(me, 1, new Ptr<uint>(0)), "Mutex::Lock");

    [Hle("_ZN3paf6thread5Mutex6UnlockEv")]
    [Hle("_ZN3paf6thread6RMutex6UnlockEv")]
    void MutexUnlock(uint me) => Check(Tm.sceKernelUnlockLwMutex(me, 1), "Mutex::Unlock");

    /// <summary>libpaf 3.74 returns 1 when the mutex is held by another thread (the kernel's "failed to own") and 0 when
    /// it took it.</summary>
    [Hle("_ZN3paf6thread5Mutex7TryLockEv")]
    [Hle("_ZN3paf6thread6RMutex7TryLockEv")]
    bool MutexTryLock(uint me)
    {
        int r = Tm.sceKernelTryLockLwMutex(me, 1);
        if (r == LwMutexFailedToOwn) return true;
        Check(r, "Mutex::TryLock");
        return false;
    }

    // ---- Cond
    [Hle("_ZN3paf6thread4CondC1ERNS0_5MutexEPKci")]
    [Hle("_ZN3paf6thread4CondC2ERNS0_5MutexEPKci")]
    uint CondCtor(uint me, uint mutex, uint name, int option)
    {
        U(me, mutex);
        Check(Tm.sceKernelCreateLwCond(me + 4, CRead(name), (option & 1) != 0 ? 0u : 0x2000u, mutex, 0), "Cond");
        return me;
    }

    [Hle("_ZN3paf6thread4CondD1Ev")]
    [Hle("_ZN3paf6thread4CondD2Ev")]
    uint CondDtor(uint me) { Check(Tm.sceKernelDeleteLwCond(me + 4), "~Cond"); return me; }

    /// <summary>(timeout in ms, 0 = forever): 1 when it timed out, else 0.</summary>
    [Hle("_ZN3paf6thread4Cond4WaitEi")]
    int CondWait(uint me, int ms)
    {
        uint t = TimeoutPtr(ms);
        try
        {
            int r = Tm.sceKernelWaitLwCond(me + 4, new Ptr<uint>(t));
            if (r == KernelTimeout) return 1;
            Check(r, "Cond::Wait");
            return 0;
        }
        finally { Free(t); }
    }

    [Hle("_ZN3paf6thread4Cond6NotifyEv")] void CondNotify(uint me) => Check(Tm.sceKernelSignalLwCond(me + 4), "Cond::Notify");
    [Hle("_ZN3paf6thread4Cond9NotifyAllEv")] void CondNotifyAll(uint me) => Check(Tm.sceKernelSignalLwCondAll(me + 4), "Cond::NotifyAll");

    // ---- Semaphore
    [Hle("_ZN3paf6thread9SemaphoreC1EPKciii")]
    [Hle("_ZN3paf6thread9SemaphoreC2EPKciii")]
    uint SemaCtor(uint me, uint name, int init, int max, int option)
    {
        int uid = Tm.sceKernelCreateSema(CRead(name), 0x2000, init, max, 0);
        U(me, (uint)uid);
        Check(uid, "Semaphore");
        return me;
    }

    [Hle("_ZN3paf6thread9SemaphoreD1Ev")]
    [Hle("_ZN3paf6thread9SemaphoreD2Ev")]
    uint SemaDtor(uint me) { Check(Tm.sceKernelDeleteSema((int)U(me)), "~Semaphore"); return me; }

    [Hle("_ZN3paf6thread9Semaphore7AcquireEv")] void SemaAcquire(uint me) => Check(Tm.sceKernelWaitSema((int)U(me), 1, new Ptr<uint>(0)), "Semaphore::Acquire");
    [Hle("_ZN3paf6thread9Semaphore7ReleaseEv")] void SemaRelease(uint me) => Check(Tm.sceKernelSignalSema((int)U(me), 1), "Semaphore::Release");

    // ---- RWLock: { event flag, readers }
    [Hle("_ZN3paf6thread6RWLockC1EPKci")]
    [Hle("_ZN3paf6thread6RWLockC2EPKci")]
    uint RwCtor(uint me, uint name, int option)
    {
        U(me + 4, 0);
        U(me, (uint)Tm.sceKernelCreateEventFlag(CRead(name), 0x1000, 3, 0));
        return me;
    }

    [Hle("_ZN3paf6thread6RWLockD1Ev")]
    [Hle("_ZN3paf6thread6RWLockD2Ev")]
    uint RwDtor(uint me) { Tm.sceKernelDeleteEventFlag((int)U(me)); return me; }

    [Hle("_ZN3paf6thread5SleepEi")] void ThreadSleep(int ms) => Tm.sceKernelDelayThread((uint)ms * 1000);

    // ---- Thread
    /// <summary>The option libpaf starts from: attribute 0, affinity 0, stack memory type 0x0C20D060.</summary>
    [Hle("_ZN3paf6thread6Thread6OptionC1Ev")]
    [Hle("_ZN3paf6thread6Thread6OptionC2Ev")]
    uint ThreadOptionCtor(uint me) { U(me, 0); U(me + 4, 0); U(me + 8, 0x0C20D060); return me; }

    uint ThreadEntry => HostStub("Thread.entry", c =>
    {
        uint self = c.Gpr(1) != 0 ? U(c.Gpr(1)) : 0;
        if (self != 0)
        {
            c.Thread.CallGuest(U(U(self) + 8), self); // EntryFunction
            B(self + 9, 0);
        }
        c.Return(0);
    });

    /// <summary>(priority, stack size, name, option or NULL): creates the kernel thread (not started). A priority in
    /// the application range 0xC0..0xFB with no affinity runs on the application cores (0xF0000), as libpaf does.</summary>
    [Hle("_ZN3paf6thread6ThreadC1EijPKcPKNS1_6OptionE")]
    [Hle("_ZN3paf6thread6ThreadC2EijPKcPKNS1_6OptionE")]
    uint ThreadCtor(uint me, int prio, uint stack, uint name, uint option)
    {
        U(me, ThreadVtableBase(me));
        B(me + 8, 0); B(me + 9, 0); B(me + 0xA, 0);
        uint attr = option != 0 ? U(option) : 0;
        int affinity = option != 0 ? (int)U(option + 4) : 0;
        if (affinity == 0 && prio is >= 0xC0 and <= 0xFB) affinity = 0xF0000;
        int thid = Tm.sceKernelCreateThread(CRead(name), ThreadEntry, prio, (int)stack, attr, affinity, 0);
        U(me + 4, (uint)thid);
        Check(thid, "Thread");
        return me;
    }

    /// <summary>A guest subclass has written its own table over libpaf's before Start, so this only fills the slot
    /// of an object built directly from the base (its EntryFunction is pure: calling it is a program error).</summary>
    uint ThreadVtableBase(uint me) => U(me) != 0 ? U(me) : VTable("Thread", 0x7EA6B501, 0x1013F3C0,
        HostSlot("Thread.pure", c => { Util.Log.Warn("libpaf: pure virtual Thread::EntryFunction called"); c.Return(0); }));

    [Hle("_ZN3paf6thread6ThreadD1Ev")]
    [Hle("_ZN3paf6thread6ThreadD2Ev")]
    uint ThreadDtor(uint me) { if ((int)U(me + 4) >= 0) Tm.sceKernelDeleteThread((int)U(me + 4)); return me; }

    [Hle("_ZN3paf6thread6ThreadD0Ev")] void ThreadDelete(uint me) { ThreadDtor(me); Free(me); }

    /// <summary>Starts the thread once (a second start is 0x80AF0702).</summary>
    [Hle("_ZN3paf6thread6Thread5StartEv")]
    int ThreadStart(uint me)
    {
        if (B(me + 8) != 0) return unchecked((int)0x80AF0702);
        B(me + 9, 1);
        int thid = (int)U(me + 4);
        if (thid < 0) { B(me + 9, 0); return thid; }
        uint arg = Alloc(4);
        U(arg, me);
        int r = Tm.sceKernelStartThread(thid, 4, arg);
        Free(arg);
        if (r != 0) { B(me + 9, 0); Check(r, "Thread::Start"); return r; }
        B(me + 8, 1);
        return 0;
    }

    [Hle("_ZN3paf6thread6Thread4JoinEv")]
    void ThreadJoin(uint me) { if ((int)U(me + 4) >= 0) Tm.sceKernelWaitThreadEnd((int)U(me + 4), new Ptr<int>(0), new Ptr<uint>(0)); }

    [Hle("_ZN3paf6thread6Thread14ChangePriorityEi")]
    void ThreadChangePriority(uint me, int prio) { if ((int)U(me + 4) >= 0) Tm.sceKernelChangeThreadPriority((int)U(me + 4), prio); }

    [Hle("_ZN3paf6thread6Thread5GetIDEv")] int ThreadGetId(uint me) => (int)U(me + 4);
    [Hle("_ZN3paf6thread6Thread7IsAliveEv")] bool ThreadIsAlive(uint me) => B(me + 9) != 0;
    [Hle("_ZN3paf6thread6Thread6CancelEv")] void ThreadCancel(uint me) => B(me + 0xA, 1);
    [Hle("_ZN3paf6thread6Thread10IsCanceledEv")] bool ThreadIsCanceled(uint me) => B(me + 0xA) != 0;

    // ---- ThreadIDCache: the thread id libpaf noted for each role (0 = main)
    readonly int[] _threadIds = new int[8];

    [Hle("_ZN3paf6thread13ThreadIDCache14SetCurrentTypeENS1_4TypeE")]
    void TidSetCurrent(int type) { if ((uint)type < _threadIds.Length) _threadIds[type] = Tm.sceKernelGetThreadId(); }

    [Hle("_ZN3paf6thread13ThreadIDCache3GetENS1_4TypeE")] int TidGet(int type) => (uint)type < _threadIds.Length ? _threadIds[type] : 0;

    [Hle("_ZN3paf6thread13ThreadIDCache5CheckENS1_4TypeE")]
    bool TidCheck(int type) => (uint)type < _threadIds.Length && _threadIds[type] == Tm.sceKernelGetThreadId();

    // ---- CallQueue: { Mutex (0x20), list +0x20, count +0x24 }; the queued calls run on the thread that calls Check
    readonly Dictionary<uint, Queue<(uint Fn, uint Arg)>> _callQueues = new();

    Queue<(uint Fn, uint Arg)> CallQueueOf(uint me)
    {
        lock (_callQueues)
            return _callQueues.TryGetValue(me, out var q) ? q : _callQueues[me] = new();
    }

    [Hle("_ZN3paf6thread9CallQueueC1EPKc")]
    [Hle("_ZN3paf6thread9CallQueueC2EPKc")]
    uint CallQueueCtor(uint me, uint name)
    {
        MutexCtor(me, name, 0);
        U(me + 0x20, me + 0x20); U(me + 0x24, 0);
        lock (_callQueues) _callQueues[me] = new();
        return me;
    }

    [Hle("_ZN3paf6thread9CallQueueD1Ev")]
    [Hle("_ZN3paf6thread9CallQueueD2Ev")]
    uint CallQueueDtor(uint me)
    {
        lock (_callQueues) _callQueues.Remove(me);
        U(me + 0x20, 0xFFFFFFFF); U(me + 0x24, 0xFFFFFFFF);
        MutexDtor(me);
        return me;
    }

    [Hle("_ZN3paf6thread9CallQueue7EnqueueEPKcPFvPvES4_")]
    void CallQueueEnqueue(uint me, uint name, uint fn, uint arg)
    {
        var q = CallQueueOf(me);
        lock (q) { q.Enqueue((fn, arg)); U(me + 0x24, (uint)q.Count); }
    }

    /// <summary>Runs every queued call, in order, outside the queue's lock.</summary>
    [Hle("_ZN3paf6thread9CallQueue5CheckEv")]
    void CallQueueCheck(uint me)
    {
        var q = CallQueueOf(me);
        while (true)
        {
            (uint Fn, uint Arg) item;
            lock (q)
            {
                if (q.Count == 0) return;
                item = q.Dequeue();
                U(me + 0x24, (uint)q.Count);
            }
            if (item.Fn != 0 && Kernel.GuestThread.Current is { } t) t.CallGuest(item.Fn, item.Arg);
        }
    }

    [Hle("_ZN3paf6thread9CallQueue8NumItemsEv")] int CallQueueCount(uint me) { var q = CallQueueOf(me); lock (q) return q.Count; }

    /// <summary>Sleeps 10 ms at a time until another thread has run the queue empty.</summary>
    [Hle("_ZN3paf6thread9CallQueue9WaitEmptyEv")]
    void CallQueueWaitEmpty(uint me) { while (CallQueueCount(me) > 0) ThreadSleep(10); }

    // ---- SyncCall: { event flag, function +4, argument +8, result +0xC }
    [Hle("_ZN3paf6thread8SyncCallC1EPKc")]
    [Hle("_ZN3paf6thread8SyncCallC2EPKc")]
    uint SyncCallCtor(uint me, uint name)
    {
        U(me + 4, 0); U(me + 8, 0); U(me + 0xC, 0);
        U(me, (uint)Tm.sceKernelCreateEventFlag(CRead(name), 0x1000, 4, 0));
        return me;
    }

    [Hle("_ZN3paf6thread8SyncCallD1Ev")]
    [Hle("_ZN3paf6thread8SyncCallD2Ev")]
    uint SyncCallDtor(uint me) { Tm.sceKernelDeleteEventFlag((int)U(me)); return me; }

    /// <summary>On the main thread the function runs at once; another thread hands it to the main thread's Check and
    /// waits for its result (event flag bits: 4 free, 1 posted, 2 done).</summary>
    [Hle("_ZN3paf6thread8SyncCall4CallEPFiPvES2_")]
    int SyncCallCall(uint me, uint fn, uint arg)
    {
        if (Kernel.GuestThread.Current is not { } t) return 0;
        if (TidCheck(0)) return (int)t.CallGuest(fn, arg);
        int evf = (int)U(me);
        Tm.sceKernelWaitEventFlag(evf, 4, 4 /* AND, clear the bits */, new Ptr<uint>(0), new Ptr<uint>(0));
        U(me + 4, fn); U(me + 8, arg); U(me + 0xC, 0);
        Tm.sceKernelSetEventFlag(evf, 1);
        Tm.sceKernelWaitEventFlag(evf, 2, 4, new Ptr<uint>(0), new Ptr<uint>(0));
        int r = (int)U(me + 0xC);
        Tm.sceKernelSetEventFlag(evf, 4);
        return r;
    }

    [Hle("_ZN3paf6thread8SyncCall5CheckEv")]
    void SyncCallCheck(uint me)
    {
        int evf = (int)U(me);
        if (U(me + 4) == 0 || Kernel.GuestThread.Current is not { } t) return;
        Tm.sceKernelClearEventFlag(evf, ~1u);
        U(me + 0xC, t.CallGuest(U(me + 4), U(me + 8)));
        U(me + 8, 0); U(me + 4, 0);
        Tm.sceKernelSetEventFlag(evf, 2);
    }
}
