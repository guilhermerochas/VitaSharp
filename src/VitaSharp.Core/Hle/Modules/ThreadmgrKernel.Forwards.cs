using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceThreadmgrForDriver/SceThreadmgrForKernel entries that the FW 3.74 threadmgr.skprx (decompiled, study only)
/// builds on another export: the spinlock entries that only jump to SceCpuForDriver's, the message pipe calls on one
/// buffer that pass a vector of one entry to the vector call, and the small current-thread accessors.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    /// <summary>The thread manager's two stubs answer SCE_KERNEL_ERROR_NOT_IMPLEMENTED.</summary>
    const int NotImplemented = unchecked((int)0x80020002);

    /// <summary>The current thread's +0x100 word, by thread UID, for the threads that changed it (0 from the setup).</summary>
    [SnapshotMissing] // states of builds before it: no thread had changed it
    readonly Dictionary<int, int> _word100 = new();

    ThreadMgr UserThreadmgr => Emu.Hle.Modules.OfType<ThreadMgr>().First();

    /// <summary>Runs the HLE of <paramref name="nid"/> on the caller's registers (its result lands in r0/r1).</summary>
    void RunExport(HleContext context, uint nid)
    {
        if (Emu.Hle.ImplOf(nid) is { } implementation)
        {
            implementation(context);
        }
        else
        {
            context.Return(SceError.ERROR);
        }
    }

    // ------------------------------------------------------------------ spinlocks (SceCpuForDriver's)

    /// <summary>NID_032E8F73(lock): ksceKernelSpinlockLowLock.</summary>
    [Hle(Nid = 0x032E8F73, Library = "SceThreadmgrForDriver")]
    void NID_032E8F73(HleContext context) => RunExport(context, 0xBF82DEB2);

    /// <summary>NID_0A4AD37A(lock): ksceKernelSpinlockLowTrylock.</summary>
    [Hle(Nid = 0x0A4AD37A, Library = "SceThreadmgrForDriver")]
    void NID_0A4AD37A(HleContext context) => RunExport(context, 0x5AC9D394);

    /// <summary>NID_A90B1E01(lock): ksceKernelSpinlockLowLockCpuSuspendIntr.</summary>
    [Hle(Nid = 0xA90B1E01, Library = "SceThreadmgrForDriver")]
    void NID_A90B1E01(HleContext context) => RunExport(context, 0xD32ACE9E);

    /// <summary>NID_C03799E2(lock): ksceKernelSpinlockLowUnlock.</summary>
    [Hle(Nid = 0xC03799E2, Library = "SceThreadmgrForDriver")]
    void NID_C03799E2(HleContext context) => RunExport(context, 0xD6ED0C46);

    /// <summary>NID_F8A6013B(lock): ksceKernelSpinlockLowTrylockCpuSuspendIntr.</summary>
    [Hle(Nid = 0xF8A6013B, Library = "SceThreadmgrForDriver")]
    void NID_F8A6013B(HleContext context) => RunExport(context, 0x27C0B340);

    /// <summary>NID_F96FE0D5(lock, state): ksceKernelSpinlockLowUnlockCpuResumeIntr.</summary>
    [Hle(Nid = 0xF96FE0D5, Library = "SceThreadmgrForDriver")]
    void NID_F96FE0D5(HleContext context) => RunExport(context, 0x7BB9D5DF);

    // ------------------------------------------------------------------ message pipes on one buffer

    /// <summary>NID_F5074173(uid, buf, size, mode, result, timeout): send one buffer (the user sceKernelSendMsgPipe).</summary>
    [Hle(Nid = 0xF5074173, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    void NID_F5074173(HleContext context) => RunExport(context, 0x0CA71EA2);

    /// <summary>NID_E6D80698(uid, buf, size, mode, result, timeout): the same, running callbacks while it waits.</summary>
    [Hle(Nid = 0xE6D80698, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true, CallbackTwinOf = 0xF5074173)]
    void NID_E6D80698(HleContext context) => RunExport(context, 0xA5CA74AC);

    /// <summary>NID_85F4DC8C(uid, buf, size, mode, result): send one buffer without waiting.</summary>
    [Hle(Nid = 0x85F4DC8C, Library = "SceThreadmgrForDriver")]
    void NID_85F4DC8C(HleContext context) => RunExport(context, 0xDFC670E0);

    /// <summary>NID_2F9F75AA(uid, buf, size, mode, result, timeout): receive into one buffer.</summary>
    [Hle(Nid = 0x2F9F75AA, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    void NID_2F9F75AA(HleContext context) => RunExport(context, 0x4E81DD5C);

    /// <summary>NID_D0262C55(uid, buf, size, mode, result, timeout): the same, running callbacks while it waits.</summary>
    [Hle(Nid = 0xD0262C55, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true, CallbackTwinOf = 0x2F9F75AA)]
    void NID_D0262C55(HleContext context) => RunExport(context, 0x33AF829B);

    /// <summary>NID_2E95B628(uid, buf, size, mode, result): receive into one buffer without waiting.</summary>
    [Hle(Nid = 0x2E95B628, Library = "SceThreadmgrForDriver")]
    void NID_2E95B628(HleContext context) => RunExport(context, 0x5615B006);

    /// <summary>NID_2FC0FFF6(uid, vector, count, mode, result, timeout): the vector send that runs callbacks while it
    /// waits (the user sceKernelSendMsgPipeVectorCB; NID_E6D80698 calls it with one entry).</summary>
    [Hle(Nid = 0x2FC0FFF6, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait, CallbackWait = true, CallbackTwinOf = 0x67DD3BAD)]
    void NID_2FC0FFF6(HleContext context) => RunExport(context, 0x9C6F7F79);

    // ------------------------------------------------------------------ threads

    /// <summary>
    /// ksceKernelChangeThreadPriority(thid, priority): as the user call (thid 0 the current thread, priority 0 its
    /// initial one), but on success the kernel entry answers the priority the thread had (its +0xE0 word), not 0.
    /// </summary>
    [Hle(Nid = 0x63DAB420, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    int ksceKernelChangeThreadPriority(int thid, int priority)
    {
        if (K.GetThread(thid) is not { } thread)
        {
            return thid == 0 ? IllegalContext : SceError.UNKNOWN_THREAD_ID;
        }

        int earlier = thread.Priority;
        int result = UserThreadmgr.sceKernelChangeThreadPriority(thid, priority);
        return result < 0 ? result : earlier;
    }

    /// <summary>NID_DA1544D1(thid, priority): ksceKernelChangeThreadPriority with 0 for any success (only an error
    /// comes through).</summary>
    [Hle(Nid = 0xDA1544D1, Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    int NID_DA1544D1(int thid, int priority) => Math.Min(ksceKernelChangeThreadPriority(thid, priority), 0);

    /// <summary>NID_14F8167C(): the current thread's VFP exception mask (+0x14, what
    /// ksceKernelChangeThreadVfpException changes); 0x80027101 without a current thread.</summary>
    [Hle(Nid = 0x14F8167C, Library = "SceThreadmgrForDriver")]
    void NID_14F8167C(HleContext context)
    {
        if (GuestThread.Current == null)
        {
            context.Return(IllegalContext);
            return;
        }

        RunExport(context, 0xA37A6057); // sceKernelGetCurrentThreadVfpException
    }

    /// <summary>NID_9461FC73(int value): ksceKernelSetPermission's SceThreadmgrForKernel entry (the same code: a
    /// negative value is SCE_KERNEL_ERROR_INVALID_ARGUMENT, then the earlier permission).</summary>
    [Hle(Nid = 0x9461FC73, Library = "SceThreadmgrForKernel")]
    int NID_9461FC73(int value) => ksceKernelSetPermission(value);

    /// <summary>NID_84EB1EA4(int value): sets the current thread's +0x100 word (NID_56C18B11 reads it) and returns the
    /// earlier one; a negative value is SCE_KERNEL_ERROR_INVALID_ARGUMENT, no current thread 0x80027101.</summary>
    [Hle(Nid = 0x84EB1EA4, Library = "SceThreadmgrForDriver")]
    int NID_84EB1EA4(int value)
    {
        if (value < 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestThread.Current is not { } thread)
        {
            return IllegalContext;
        }

        lock (_word100)
        {
            int earlier = _word100.GetValueOrDefault(thread.Uid);
            _word100[thread.Uid] = value;
            return earlier;
        }
    }

    /// <summary>NID_D74F66BD(): the free bytes of the current thread's stack (sp minus its base) while sp is inside it;
    /// 0 outside it or without a current thread.</summary>
    [Hle(Nid = 0xD74F66BD, Library = "SceThreadmgrForDriver")]
    unsafe int NID_D74F66BD(HleContext context)
    {
        if (GuestThread.Current is not { } thread)
        {
            return 0;
        }

        uint sp = context.Cpu->R[13];
        if (sp <= thread.StackAddr)
        {
            return 0;
        }

        uint free = sp - thread.StackAddr;
        return free >= thread.StackSize ? 0 : (int)free;
    }

    /// <summary>NID_20C228E4(): bit 15 of the current thread's +0x24 state word (0 without a current thread). The
    /// firmware saves that bit and clears it while the thread runs its callbacks; no path VitaSharp models sets it, so
    /// it reads 0.</summary>
    [Hle(Nid = 0x20C228E4, Library = "SceThreadmgrForDriver")]
    int NID_20C228E4() => 0;

    /// <summary>NID_565BD2DA(): the UID class of thread objects (+0x2330). VitaSharp's thread objects belong to the
    /// root UID class (what ksceKernelGetUidClass answers), so that is the class the GUID calls accept for them.</summary>
    [Hle(Nid = 0x565BD2DA, Library = "SceThreadmgrForKernel", ReturnsValue = true)]
    void NID_565BD2DA(HleContext context) => RunExport(context, 0x85336A1C);

    /// <summary>NID_6DC1BE7A(): a stub of the thread manager: SCE_KERNEL_ERROR_NOT_IMPLEMENTED.</summary>
    [Hle(Nid = 0x6DC1BE7A, Library = "SceThreadmgrForKernel")]
    int NID_6DC1BE7A() => NotImplemented;

    /// <summary>NID_F46783E2(): a stub of the thread manager: SCE_KERNEL_ERROR_NOT_IMPLEMENTED.</summary>
    [Hle(Nid = 0xF46783E2, Library = "SceThreadmgrForKernel")]
    int NID_F46783E2() => NotImplemented;
}
