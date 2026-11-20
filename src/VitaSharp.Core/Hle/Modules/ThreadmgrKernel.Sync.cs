using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Mutex = VitaSharp.Core.Kernel.Mutex;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The SceThreadmgrForDriver/SceThreadmgrForKernel entries of the synchronisation objects that the FW 3.74
/// threadmgr.skprx (decompiled, study only) builds on the same internal call as the user one, with the wait's
/// callback flag fixed by the entry (the user syscall picks it by its CB name):
/// <list type="bullet">
/// <item>the waits: semaphores (0x81011bc0: uid, count, timeout, cb), mutexes (0x8100d79c), RW locks (0x81024bb0:
/// uid, timeout, write, cb), each entry a thin wrapper that sets the last two words;</item>
/// <item>cancel (semaphore 8760C8ED, event flag EA211225, RW lock 7A4EF925, event B6FA8305), poll an event
/// (D08C71C6), the timers (create 90656C98; time, base, set time, set event), create a semaphore (F9364623) or a
/// mutex (1E4FF787), signal (21B25367) and unlock (59D924C1);</item>
/// <item>the deletes that check the class first (semaphore A5ACE750, mutex CA760728, RW lock 32452017: another
/// object is SCE_KERNEL_ERROR_DIFFERENT_UID_CLASS, inside an interrupt 0x80027101), and the simple event's
/// (2BDE3B40), which sets the event with the deleted state before the UID goes away;</item>
/// <item>the waits for several events (36F8E58A, 8C48E53B with callbacks): at most 64 events (else
/// SCE_KERNEL_ERROR_ILLEGAL_SIZE), a mode of bits 0-1 only (else SCE_KERNEL_ERROR_ILLEGAL_MODE).</item>
/// </list>
/// These run the user implementation (the same code in the firmware) on the caller's registers.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    const uint UserWaitSema = 0x0C7B834B, UserWaitSemaCB = 0x174692B4, UserLockMutex = 0x1D8D7945,
        UserUnlockMutex = 0x1A372EC8, UserSignalSema = 0xE6B761D1, UserCreateSema = 0x1BD67366,
        UserCreateMutex = 0xED53334A, UserLockReadRWLock = 0xFA670F0F, UserLockReadRWLockCB = 0x2D4A62B7,
        UserLockWriteRWLock = 0x67A187BB, UserLockWriteRWLockCB = 0xA4777082, UserCancelSema = 0x66D6BF05,
        UserCancelEventFlag = 0x2A12D9B7, UserCancelRWLock = 0x190CA94B, UserCancelEvent = 0x603AB770,
        UserPollEvent = 0x241F3634, UserCreateTimer = 0x2255B2A5, UserGetTimerTime = 0x381DC300,
        UserGetTimerBase = 0x1F59E04D, UserSetTimerTime = 0xFFAD717F, UserSetTimerEvent = 0x621D293B,
        UserWaitMultipleEvents = 0x10586418, UserWaitMultipleEventsCB = 0x4263DBC9, UserDeleteSema = 0xDB32948A,
        UserDeleteMutex = 0xCB78710D, UserDeleteRWLock = 0xE73649CA, UserDeleteSimpleEvent = 0x208CFE28;

    /// <summary>The simple event's unknown-ID error of the kernel calls (its class, SceUIDSimpleEventClass).</summary>
    const int UnknownSimpleEventId = unchecked((int)0x80028261);

    /// <summary>A wait for several events takes at most this many.</summary>
    const uint MaxMultipleEvents = 0x40;

    /// <summary>Runs <paramref name="nid"/> unless the call comes from an interrupt handler (0x80027101).</summary>
    void RunOutsideInterrupt(HleContext context, uint nid)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            context.Return(IllegalContext);
            return;
        }

        RunExport(context, nid);
    }

    // ------------------------------------------------------------------ waits

    /// <summary>NID_3F5E1D56(uid, timeout): locks an RW lock for reading.</summary>
    [Hle(Nid = 0x3F5E1D56, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    void NID_3F5E1D56(HleContext context) => RunExport(context, UserLockReadRWLock);

    /// <summary>NID_CFA2FFAF(uid, timeout): locks an RW lock for reading, running callbacks while it waits.</summary>
    [Hle(Nid = 0xCFA2FFAF, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void NID_CFA2FFAF(HleContext context) => RunExport(context, UserLockReadRWLockCB);

    /// <summary>NID_A64B00F6(uid, timeout): locks an RW lock for writing.</summary>
    [Hle(Nid = 0xA64B00F6, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    void NID_A64B00F6(HleContext context) => RunExport(context, UserLockWriteRWLock);

    /// <summary>NID_54566860(uid, timeout): locks an RW lock for writing, running callbacks while it waits.</summary>
    [Hle(Nid = 0x54566860, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void NID_54566860(HleContext context) => RunExport(context, UserLockWriteRWLockCB);

    /// <summary>NID_F55E4D86(uid, count, timeout): waits on a semaphore, running callbacks while it waits.</summary>
    [Hle(Nid = 0xF55E4D86, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void NID_F55E4D86(HleContext context) => RunExport(context, UserWaitSemaCB);

    /// <summary>NID_EFD7439D(uid, count, timeout): waits on a semaphore.</summary>
    [Hle(Nid = 0xEFD7439D, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    void NID_EFD7439D(HleContext context) => RunExport(context, UserWaitSema);

    /// <summary>NID_1059CCDD(uid, count, timeout): locks a mutex.</summary>
    [Hle(Nid = 0x1059CCDD, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    void NID_1059CCDD(HleContext context) => RunExport(context, UserLockMutex);

    /// <summary>NID_59D924C1(uid, count): unlocks a mutex (0x80027101 without a current thread).</summary>
    [Hle(Nid = 0x59D924C1, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    void NID_59D924C1(HleContext context)
    {
        if (GuestThread.Current == null)
        {
            context.Return(IllegalContext);
            return;
        }

        RunExport(context, UserUnlockMutex);
    }

    /// <summary>NID_21B25367(uid, count): signals a semaphore.</summary>
    [Hle(Nid = 0x21B25367, Library = "SceThreadmgrForKernel", Cost = HleCost.SchedulerWait)]
    void NID_21B25367(HleContext context) => RunExport(context, UserSignalSema);

    /// <summary>NID_36F8E58A(events, count, mode, results, timeout): waits for several events.</summary>
    [Hle(Nid = 0x36F8E58A, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    void NID_36F8E58A(HleContext context) => WaitMultiple(context, UserWaitMultipleEvents);

    /// <summary>NID_8C48E53B(events, count, mode, results, timeout): the same, running callbacks while it waits.</summary>
    [Hle(Nid = 0x8C48E53B, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    void NID_8C48E53B(HleContext context) => WaitMultiple(context, UserWaitMultipleEventsCB);

    void WaitMultiple(HleContext context, uint nid)
    {
        if (context.Gpr(0) == 0)
        {
            context.Return(SceError.ILLEGAL_ADDR);
            return;
        }

        if (context.Gpr(1) > MaxMultipleEvents)
        {
            context.Return(SceError.ILLEGAL_SIZE);
            return;
        }

        if ((context.Gpr(2) & ~3u) != 0)
        {
            context.Return(SceError.ILLEGAL_MODE);
            return;
        }

        RunExport(context, nid);
    }

    // ------------------------------------------------------------------ create, cancel, poll

    /// <summary>NID_F9364623(name, attr, init, max, opt): creates a semaphore.</summary>
    [Hle(Nid = 0xF9364623, Library = "SceThreadmgrForKernel")]
    void NID_F9364623(HleContext context) => RunExport(context, UserCreateSema);

    /// <summary>NID_1E4FF787(name, attr, init, opt): creates a mutex.</summary>
    [Hle(Nid = 0x1E4FF787, Library = "SceThreadmgrForKernel")]
    void NID_1E4FF787(HleContext context) => RunExport(context, UserCreateMutex);

    /// <summary>NID_90656C98(name, attr, opt): creates a timer.</summary>
    [Hle(Nid = 0x90656C98, Library = "SceThreadmgrForDriver")]
    void NID_90656C98(HleContext context) => RunExport(context, UserCreateTimer);

    /// <summary>NID_8760C8ED(uid, count, int *waiting): cancels the waits on a semaphore.</summary>
    [Hle(Nid = 0x8760C8ED, Library = "SceThreadmgrForDriver")]
    void NID_8760C8ED(HleContext context) => RunExport(context, UserCancelSema);

    /// <summary>NID_EA211225(uid, pattern, int *waiting): cancels the waits on an event flag.</summary>
    [Hle(Nid = 0xEA211225, Library = "SceThreadmgrForDriver")]
    void NID_EA211225(HleContext context) => RunExport(context, UserCancelEventFlag);

    /// <summary>NID_7A4EF925(uid, int *readers, int *writers, flag): cancels the waits on an RW lock (flag 1: the
    /// caller then holds it for writing, which an interrupt handler cannot).</summary>
    [Hle(Nid = 0x7A4EF925, Library = "SceThreadmgrForDriver")]
    void NID_7A4EF925(HleContext context)
    {
        if (GuestThread.Current == null && context.Gpr(3) == 1)
        {
            context.Return(IllegalContext);
            return;
        }

        RunExport(context, UserCancelRWLock);
    }

    /// <summary>NID_B6FA8305(uid, int *waiting): cancels the waits on an event.</summary>
    [Hle(Nid = 0xB6FA8305, Library = "SceThreadmgrForDriver")]
    void NID_B6FA8305(HleContext context) => RunExport(context, UserCancelEvent);

    /// <summary>NID_D08C71C6(uid, pattern, *result, *userData): polls an event; pattern 0 is
    /// SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0xD08C71C6, Library = "SceThreadmgrForDriver")]
    void NID_D08C71C6(HleContext context)
    {
        if (context.Gpr(1) == 0)
        {
            context.Return(SceError.INVALID_ARGUMENT);
            return;
        }

        RunExport(context, UserPollEvent);
    }

    // ------------------------------------------------------------------ timers

    /// <summary>NID_CB795E13(uid, SceUInt64 *time): the timer's time.</summary>
    [Hle(Nid = 0xCB795E13, Library = "SceThreadmgrForDriver")]
    void NID_CB795E13(HleContext context) => RunWithPointer(context, UserGetTimerTime);

    /// <summary>NID_D41F53D4(uid, SceUInt64 *base): the timer's base time (0 while it is stopped).</summary>
    [Hle(Nid = 0xD41F53D4, Library = "SceThreadmgrForDriver")]
    void NID_D41F53D4(HleContext context) => RunWithPointer(context, UserGetTimerBase);

    /// <summary>NID_D978D16F(uid, SceUInt64 *time): sets the timer's time (the earlier one comes back); not from an
    /// interrupt handler.</summary>
    [Hle(Nid = 0xD978D16F, Library = "SceThreadmgrForDriver")]
    void NID_D978D16F(HleContext context)
    {
        if (context.Gpr(1) != 0 && SystemServicesVsh.InInterrupt)
        {
            context.Return(IllegalContext);
            return;
        }

        RunWithPointer(context, UserSetTimerTime);
    }

    /// <summary>NID_92A3DCDD(uid, type, SceUInt64 *interval, repeat): sets the timer's event; a type above 1 is
    /// SCE_KERNEL_ERROR_ILLEGAL_TYPE, a null interval SCE_KERNEL_ERROR_ILLEGAL_ADDR.</summary>
    [Hle(Nid = 0x92A3DCDD, Library = "SceThreadmgrForDriver")]
    void NID_92A3DCDD(HleContext context)
    {
        if (SystemServicesVsh.InInterrupt)
        {
            context.Return(IllegalContext);
            return;
        }

        if (context.Gpr(1) > 1)
        {
            context.Return(SceError.ILLEGAL_TYPE);
            return;
        }

        if (context.Gpr(2) == 0)
        {
            context.Return(SceError.ILLEGAL_ADDR);
            return;
        }

        RunExport(context, UserSetTimerEvent);
    }

    /// <summary>A timer call with a result pointer: null is SCE_KERNEL_ERROR_ILLEGAL_ADDR.</summary>
    void RunWithPointer(HleContext context, uint nid)
    {
        if (context.Gpr(1) == 0)
        {
            context.Return(SceError.ILLEGAL_ADDR);
            return;
        }

        RunExport(context, nid);
    }

    // ------------------------------------------------------------------ deletes

    /// <summary>NID_A5ACE750(uid): deletes a semaphore.</summary>
    [Hle(Nid = 0xA5ACE750, Library = "SceThreadmgrForKernel")]
    void NID_A5ACE750(HleContext context) => DeleteOfClass<Semaphore>(context, UserDeleteSema);

    /// <summary>NID_CA760728(uid): deletes a mutex.</summary>
    [Hle(Nid = 0xCA760728, Library = "SceThreadmgrForKernel")]
    void NID_CA760728(HleContext context) => DeleteOfClass<Mutex>(context, UserDeleteMutex);

    /// <summary>NID_32452017(uid): deletes an RW lock (0x80027101 without a current thread).</summary>
    [Hle(Nid = 0x32452017, Library = "SceThreadmgrForDriver")]
    void NID_32452017(HleContext context)
    {
        if (GuestThread.Current == null)
        {
            context.Return(IllegalContext);
            return;
        }

        DeleteOfClass<RwLock>(context, UserDeleteRWLock);
    }

    void DeleteOfClass<T>(HleContext context, uint nid) where T : KernelObject
    {
        if (SystemServicesVsh.InInterrupt)
        {
            context.Return(IllegalContext);
            return;
        }

        if (K.Objects.Get<KernelObject>((int)context.Gpr(0)) is not T)
        {
            context.Return(SceError.DIFFERENT_UID_CLASS);
            return;
        }

        RunExport(context, nid);
    }

    /// <summary>NID_2BDE3B40(uid): deletes a simple event. The firmware sets the event once more with the deleted
    /// state (its waiters wake with SCE_KERNEL_ERROR_WAIT_DELETE) and drops the UID; 0x80027101 without a current
    /// thread, another object 0x80028261.</summary>
    [Hle(Nid = 0x2BDE3B40, Library = "SceThreadmgrForDriver")]
    void NID_2BDE3B40(HleContext context)
    {
        if (GuestThread.Current == null)
        {
            context.Return(IllegalContext);
            return;
        }

        if (K.Objects.Get<KernelObject>((int)context.Gpr(0)) is not SimpleEvent)
        {
            context.Return(UnknownSimpleEventId);
            return;
        }

        RunExport(context, UserDeleteSimpleEvent);
        context.Return(0);
    }

    // ------------------------------------------------------------------ the event class's pattern

    /// <summary>NID_0C6BCB8C(uid, uint *pattern): the current pattern of an event-class object (simple event, I/O
    /// operation); a null pointer is SCE_KERNEL_ERROR_ILLEGAL_ADDR, another object SCE_KERNEL_ERROR_UNKNOWN_EVENT_ID.</summary>
    [Hle(Nid = 0x0C6BCB8C, Library = "SceThreadmgrForDriver")]
    int NID_0C6BCB8C(int uid, uint pattern)
    {
        if (pattern == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        switch (K.Objects.Get<KernelObject>(uid))
        {
            case SimpleEvent simple:
                lock (simple)
                {
                    GuestMemory.W32(pattern, simple.Pattern);
                }

                return 0;
            case IoAsyncOp operation:
                lock (operation)
                {
                    GuestMemory.W32(pattern, operation.Pattern);
                }

                return 0;
            default:
                return SceError.UNKNOWN_EVENT_ID;
        }
    }

    /// <summary>NID_25AB8F6B(uid, attrUnused, userDataLow, userDataHigh, pattern): sets the pattern and user data of an
    /// event-class object without waking anything (a pattern with bit 16, the deleted state, is
    /// SCE_KERNEL_ERROR_ILLEGAL_PATTERN).</summary>
    [Hle(Nid = 0x25AB8F6B, Library = "SceThreadmgrForDriver")]
    int NID_25AB8F6B(int uid, uint unused, uint userDataLow, uint userDataHigh, uint pattern)
    {
        if ((pattern & 0x10000) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        ulong userData = userDataLow | ((ulong)userDataHigh << 32);
        switch (K.Objects.Get<KernelObject>(uid))
        {
            case SimpleEvent simple:
                lock (simple)
                {
                    simple.Pattern = pattern;
                    simple.LastUserData = userData;
                }

                return 0;
            case IoAsyncOp operation:
                lock (operation)
                {
                    operation.Pattern = pattern;
                    operation.UserData = userData;
                }

                return 0;
            default:
                return SceError.UNKNOWN_EVENT_ID;
        }
    }

    /// <summary>NID_B7DF3EDF(uid, pattern, userDataLow, userDataHigh, notifyArg): the event class's set (0x8101dcf8):
    /// adds the pattern bits, wakes the waits they satisfy and notifies the registered callbacks; pattern 0 does
    /// nothing.</summary>
    [Hle(Nid = 0xB7DF3EDF, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    int NID_B7DF3EDF(int uid, uint pattern, uint userDataLow, uint userDataHigh, uint notifyArg)
    {
        KernelObject? o = K.Objects.Get<KernelObject>(uid);
        if (o is not (SimpleEvent or IoAsyncOp))
        {
            return SceError.UNKNOWN_EVENT_ID;
        }

        if (pattern == 0)
        {
            return 0;
        }

        ulong userData = userDataLow | ((ulong)userDataHigh << 32);
        if (o is SimpleEvent simple)
        {
            return simple.SetOrPulse(pattern, userData, false);
        }

        var operation = (IoAsyncOp)o;
        lock (operation)
        {
            operation.Pattern |= pattern;
            operation.UserData = userData;
            Monitor.PulseAll(operation);
            operation.PokeWatchers();
        }

        return 0;
    }
}
