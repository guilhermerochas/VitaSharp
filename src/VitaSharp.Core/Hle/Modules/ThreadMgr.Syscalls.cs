using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The SceThreadmgr syscalls (<c>_sceKernel*</c>) behind the SceLibKernel wrappers. Firmware modules loaded LLE that
/// call the kernel directly import these instead of the public names. Argument packing read from the 3.74 libkernel
/// wrappers: functions with more than four arguments pass the extra ones in a small struct on the caller's stack (the
/// fourth argument), and the Get*Info calls pass <c>{info->size}</c> as an 8-byte opt whose size the kernel uses.
/// </summary>
public sealed partial class ThreadMgr
{
    static uint OptWord(uint opt, int index) => opt == 0 ? 0 : GuestMemory.R32(opt + (uint)index * 4);

    /// <summary>Get*Info syscall form: the copy size comes from <c>opt->size</c>; the result is written as the
    /// public call writes it.</summary>
    static int InfoWithOpt(uint info, uint opt, Func<int> body)
    {
        if (info != 0 && opt != 0) GuestMemory.W32(info, GuestMemory.R32(opt));
        return body();
    }

    // ---- threads
    [Hle] int _sceKernelStartThread(int thid, uint argSize, uint argp) => sceKernelStartThread(thid, argSize, argp);
    [Hle("_sceKernelWaitThreadEnd")]
    [Hle("_sceKernelWaitThreadEndCB")]
    int _sceKernelWaitThreadEnd(int thid, Ptr<int> stat, Ptr<uint> timeout) => sceKernelWaitThreadEnd(thid, stat, timeout);
    [Hle] int _sceKernelGetThreadExitStatus(int thid, Ptr<int> status) => sceKernelGetThreadExitStatus(thid, status);
    [Hle] int _sceKernelGetThreadCpuAffinityMask(int thid) => sceKernelGetThreadCpuAffinityMask(thid);
    [Hle] int _sceKernelGetThreadInfo(int thid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetThreadInfo(thid, info));
    [Hle] int _sceKernelGetSystemInfo(uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetSystemInfo(info));
    [Hle] int _sceKernelGetSystemTime(Ptr<ulong> clock) => sceKernelGetSystemTime(clock);

    /// <summary>(name, threadMask, eventMask, opt {handler, common}).</summary>
    [Hle]
    int _sceKernelRegisterThreadEventHandler(string name, int thid, uint mask, uint opt)
        => sceKernelRegisterThreadEventHandler(name, thid, mask, OptWord(opt, 0), OptWord(opt, 1));

    // ---- semaphores: (name, attr, initCount, opt {maxCount, option})
    [Hle("_sceKernelCreateSema")]
    [Hle("_sceKernelCreateSema_16XX")]
    int _sceKernelCreateSema(string name, uint attr, int initVal, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceKernelCreateSema(name, attr, initVal, (int)OptWord(opt, 0), OptWord(opt, 1));
    [Hle("_sceKernelWaitSema")]
    [Hle("_sceKernelWaitSemaCB")]
    int _sceKernelWaitSema(int uid, int need, Ptr<uint> timeout) => sceKernelWaitSema(uid, need, timeout);
    [Hle] int _sceKernelCancelSema(int uid, int setCount, Ptr<int> numWait) => sceKernelCancelSema(uid, setCount, numWait);
    [Hle] int _sceKernelGetSemaInfo(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetSemaInfo(uid, info));

    // ---- event flags
    [Hle] int _sceKernelCreateEventFlag(string name, uint attr, uint initPattern, uint opt) => sceKernelCreateEventFlag(name, attr, initPattern, opt);
    /// <summary>(uid, bits, mode, opt {outBits*, timeout*}).</summary>
    [Hle("_sceKernelWaitEventFlag")]
    [Hle("_sceKernelWaitEventFlagCB")]
    int _sceKernelWaitEventFlag(int uid, uint bits, uint mode, uint opt)
        => sceKernelWaitEventFlag(uid, bits, mode, new Ptr<uint>(OptWord(opt, 0)), new Ptr<uint>(OptWord(opt, 1)));
    [Hle] int _sceKernelPollEventFlag(int uid, uint bits, uint mode, Ptr<uint> outBits) => sceKernelPollEventFlag(uid, bits, mode, outBits);
    [Hle] int _sceKernelCancelEventFlag(int uid, uint pattern, Ptr<int> numWait) => sceKernelCancelEventFlag(uid, pattern, numWait);
    [Hle] int _sceKernelGetEventFlagInfo(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetEventFlagInfo(uid, info));

    // ---- mutexes
    [Hle] int _sceKernelCreateMutex(string name, uint attr, int initCount, uint opt) => sceKernelCreateMutex(name, attr, initCount, opt);
    [Hle("_sceKernelLockMutex")]
    [Hle("_sceKernelLockMutexCB")]
    int _sceKernelLockMutex(int uid, int count, Ptr<uint> timeout) => sceKernelLockMutex(uid, count, timeout);
    [Hle] int _sceKernelCancelMutex(int uid, int newCount, Ptr<int> numWait) => sceKernelCancelMutex(uid, newCount, numWait);
    [Hle] int _sceKernelGetMutexInfo(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetMutexInfo(uid, info));

    // ---- lightweight mutexes and conds (the slow paths of the user-mode fast paths)
    /// <summary>SceLibKernel's own export: the public arguments; it packs them for the syscall below.</summary>
    [Hle] int _sceKernelCreateLwMutex(uint work, string name, uint attr, int initCount, uint opt) => sceKernelCreateLwMutex(work, name, attr, initCount, opt);
    /// <summary>(work, name, attr, opt {initCount, option}).</summary>
    [Hle] int __sceKernelCreateLwMutex(uint work, string name, uint attr, uint opt) => sceKernelCreateLwMutex(work, name, attr, (int)OptWord(opt, 0), OptWord(opt, 1));
    [Hle] int _sceKernelDeleteLwMutex(uint work) => sceKernelDeleteLwMutex(work);
    [Hle] int _sceKernelLockLwMutex(uint work, int count, Ptr<uint> timeout) => sceKernelLockLwMutex(work, count, timeout);
    [Hle] int _sceKernelUnlockLwMutex(uint work, int count) => sceKernelUnlockLwMutex(work, count);
    [Hle] int _sceKernelGetLwMutexInfoById(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetLwMutexInfoById(uid, info));
    /// <summary>(work, name, attr, opt {lwMutex work, option}).</summary>
    [Hle]
    int _sceKernelCreateLwCond(uint work, string name, uint attr, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceKernelCreateLwCond(work, name, attr, OptWord(opt, 0), OptWord(opt, 1));
    [Hle] int _sceKernelDeleteLwCond(uint work) => sceKernelDeleteLwCond(work);
    [Hle("_sceKernelWaitLwCond")]
    [Hle("_sceKernelWaitLwCondCB")]
    int _sceKernelWaitLwCond(uint work, Ptr<uint> timeout) => sceKernelWaitLwCond(work, timeout);
    [Hle] int _sceKernelSignalLwCond(uint work) => sceKernelSignalLwCond(work);
    [Hle] int _sceKernelSignalLwCondAll(uint work) => sceKernelSignalLwCondAll(work);
    [Hle] int _sceKernelSignalLwCondTo(uint work, int thid) => sceKernelSignalLwCondTo(work, thid);
    [Hle] int _sceKernelGetLwCondInfo(uint work, uint info) => sceKernelGetLwCondInfo(work, info);
    [Hle] int _sceKernelGetLwCondInfoById(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetLwCondInfoById(uid, info));

    // ---- condition variables
    [Hle] int _sceKernelCreateCond(string name, uint attr, int mutexId, uint opt) => sceKernelCreateCond(name, attr, mutexId, opt);
    [Hle("_sceKernelWaitCond")]
    [Hle("_sceKernelWaitCondCB")]
    int _sceKernelWaitCond(int uid, Ptr<uint> timeout) => sceKernelWaitCond(uid, timeout);
    [Hle] int _sceKernelGetCondInfo(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetCondInfo(uid, info));

    // ---- message pipes
    /// <summary>(name, type, attr, opt {bufSize, option, lr}).</summary>
    [Hle]
    int _sceKernelCreateMsgPipeWithLR(string name, uint type, uint attr, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR : sceKernelCreateMsgPipe(name, type, attr, OptWord(opt, 0), OptWord(opt, 1));
    /// <summary>(uid, vector, count, opt {mode, result*, timeout*}).</summary>
    [Hle("_sceKernelSendMsgPipeVector")]
    [Hle("_sceKernelSendMsgPipeVectorCB")]
    int _sceKernelSendMsgPipeVector(int uid, uint vec, uint count, uint opt)
        => MsgPipeVector(uid, vec, count, OptWord(opt, 0), new Ptr<uint>(OptWord(opt, 1)), new Ptr<uint>(OptWord(opt, 2)), true, false);
    [Hle("_sceKernelReceiveMsgPipeVector")]
    [Hle("_sceKernelReceiveMsgPipeVectorCB")]
    int _sceKernelReceiveMsgPipeVector(int uid, uint vec, uint count, uint opt)
        => MsgPipeVector(uid, vec, count, OptWord(opt, 0), new Ptr<uint>(OptWord(opt, 1)), new Ptr<uint>(OptWord(opt, 2)), false, false);
    /// <summary>(uid, vector, count, opt {mode, result*}).</summary>
    [Hle]
    int _sceKernelTrySendMsgPipeVector(int uid, uint vec, uint count, uint opt)
        => MsgPipeVector(uid, vec, count, OptWord(opt, 0), new Ptr<uint>(OptWord(opt, 1)), default, true, true);
    [Hle]
    int _sceKernelTryReceiveMsgPipeVector(int uid, uint vec, uint count, uint opt)
        => MsgPipeVector(uid, vec, count, OptWord(opt, 0), new Ptr<uint>(OptWord(opt, 1)), default, false, true);
    [Hle] int _sceKernelCancelMsgPipe(int uid, Ptr<int> numSend, Ptr<int> numRecv) => sceKernelCancelMsgPipe(uid, numSend, numRecv);
    [Hle] int _sceKernelGetMsgPipeInfo(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetMsgPipeInfo(uid, info));

    // ---- rw locks
    [Hle] int _sceKernelCreateRWLock(string name, uint attr, uint opt) => sceKernelCreateRWLock(name, attr, opt);
    [Hle("_sceKernelLockReadRWLock")]
    [Hle("_sceKernelLockReadRWLockCB")]
    int _sceKernelLockReadRWLock(int uid, Ptr<uint> timeout) => sceKernelLockReadRWLock(uid, timeout);
    [Hle("_sceKernelLockWriteRWLock")]
    [Hle("_sceKernelLockWriteRWLockCB")]
    int _sceKernelLockWriteRWLock(int uid, Ptr<uint> timeout) => sceKernelLockWriteRWLock(uid, timeout);
    [Hle] int _sceKernelCancelRWLock(int uid, Ptr<uint> numRead, Ptr<uint> numWrite, int flag) => sceKernelCancelRWLock(uid, numRead, numWrite, flag);
    [Hle] int _sceKernelGetRWLockInfo(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetRWLockInfo(uid, info));

    // ---- callbacks
    [Hle] int _sceKernelGetCallbackInfo(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetCallbackInfo(uid, info));

    // ---- timers
    [Hle] int _sceKernelCreateTimer(string name, uint attr, uint opt) => sceKernelCreateTimer(name, attr, opt);
    [Hle] int _sceKernelGetTimerBase(int uid, Ptr<ulong> baseTime) => sceKernelGetTimerBase(uid, baseTime);
    [Hle] int _sceKernelGetTimerTime(int uid, Ptr<ulong> time) => sceKernelGetTimerTime(uid, time);
    [Hle] int _sceKernelSetTimerTime(int uid, Ptr<ulong> time) => sceKernelSetTimerTime(uid, time);
    [Hle] int _sceKernelSetTimerEvent(int uid, int type, Ptr<ulong> interval, int repeat) => sceKernelSetTimerEvent(uid, type, interval, repeat);
    [Hle] int _sceKernelCancelTimer(int uid, Ptr<uint> numWait) => sceKernelCancelTimer(uid, numWait);
    [Hle] int _sceKernelGetTimerEventRemainingTime(int uid, Ptr<ulong> remaining) => sceKernelGetTimerEventRemainingTime(uid, remaining);
    [Hle] int _sceKernelGetTimerInfo(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetTimerInfo(uid, info));

    // ---- simple events
    [Hle] int _sceKernelCreateSimpleEvent(uint namePtr, uint attr, uint initPattern, uint opt) => sceKernelCreateSimpleEvent(namePtr, attr, initPattern, opt);
    /// <summary>(uid, waitPattern, resultPattern*, opt {userData*, timeout*}).</summary>
    [Hle("_sceKernelWaitEvent")]
    [Hle("_sceKernelWaitEventCB")]
    int _sceKernelWaitEvent(int uid, uint waitPattern, Ptr<uint> resultPattern, uint opt)
        => sceKernelWaitEvent(uid, waitPattern, resultPattern, new Ptr<ulong>(OptWord(opt, 0)), new Ptr<uint>(OptWord(opt, 1)));
    [Hle] int _sceKernelPollEvent(int uid, uint waitPattern, Ptr<uint> resultPattern, Ptr<ulong> userData) => sceKernelPollEvent(uid, waitPattern, resultPattern, userData);
    /// <summary>(events, count, mode, opt {results*, timeout*}).</summary>
    [Hle("_sceKernelWaitMultipleEvents")]
    [Hle("_sceKernelWaitMultipleEventsCB")]
    int _sceKernelWaitMultipleEvents(uint events, uint count, uint mode, uint opt)
        => sceKernelWaitMultipleEvents(events, count, mode, OptWord(opt, 0), new Ptr<uint>(OptWord(opt, 1)));
    [Hle] int _sceKernelCancelEvent(int uid, Ptr<uint> numWait) => sceKernelCancelEvent(uid, numWait);
    [Hle] int _sceKernelGetEventPattern(int uid, Ptr<uint> pattern) => sceKernelGetEventPattern(uid, pattern);

    /// <summary>SceKernelEventInfo (0x40): +0x28 attr, +0x2C pattern, +0x30 user data (u64), +0x38 numWaitThreads.</summary>
    [Hle]
    int sceKernelGetEventInfo(int uid, uint info)
    {
        var e = Event(uid);
        if (e == null) return SceError.UNKNOWN_EVENT_ID;
        Span<byte> f = stackalloc byte[0x40];
        f.Clear();
        lock (e)
        {
            KernelInfo.Header(f, uid, e.Name, e.Attr);
            KernelInfo.Put(f, 0x2C, e.Pattern);
            KernelInfo.Put64(f, 0x30, e.LastUserData);
        }
        KernelInfo.Put(f, 0x38, e.NumWaiters);
        return KernelInfo.Write(info, f);
    }
    [Hle] int _sceKernelGetEventInfo(int uid, uint info, uint opt) => InfoWithOpt(info, opt, () => sceKernelGetEventInfo(uid, info));

    /// <summary>(uid, setPattern, SceUInt64 userData, SceUInt32 *numWaitThreads): every wait ends with WAIT_CANCEL and
    /// the pattern and user data are replaced; the count reads 0 when the event is unknown.</summary>
    [Hle]
    int sceKernelCancelEventWithSetPattern(int uid, uint setPattern, ulong userData, Ptr<uint> numWait)
    {
        numWait.TrySet(0);
        return Event(uid)?.CancelWithSetPattern(setPattern, userData, numWait) ?? SceError.UNKNOWN_EVENT_ID;
    }
    /// <summary>(uid, setPattern, opt {userData u64, numWaitThreads*}).</summary>
    [Hle]
    int _sceKernelCancelEventWithSetPattern(int uid, uint setPattern, uint opt)
        => opt == 0 ? SceError.ILLEGAL_ADDR
            : sceKernelCancelEventWithSetPattern(uid, setPattern, OptWord(opt, 0) | ((ulong)OptWord(opt, 1) << 32), new Ptr<uint>(OptWord(opt, 2)));

    /// <summary>(SceKernelThreadRunStatus *status, opt {status->size}): the 3.74 wrapper passes only the status.</summary>
    [Hle]
    int _sceKernelGetThreadRunStatus(uint status, uint opt) => InfoWithOpt(status, opt, () => sceKernelGetThreadRunStatus(Self.Uid, status));
}
