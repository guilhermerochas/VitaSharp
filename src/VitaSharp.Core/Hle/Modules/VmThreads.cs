using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Thread suspension and context access used by managed runtimes (Mono/IL2CPP garbage collectors).</summary>
public sealed unsafe class VmThreads : HleModule
{
    const int ERR_NOT_SUSPENDED = SceError.NOT_SUSPENDED, ERR_ILLEGAL_SIZE = SceError.ILLEGAL_SIZE;

    GuestThread? Target(int uid) => uid == 0 ? null : K.GetThread(uid);

    [Hle("sceKernelSuspendThreadForMono")]
    [Hle("sceKernelSuspendThreadForVM")]
    int Suspend(int uid)
    {
        var t = Target(uid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if (t == GuestThread.Current) return SceError.ILLEGAL_THREAD_ID;
        t.SuspendForVm();
        return 0;
    }

    [Hle("sceKernelResumeThreadForMono")]
    [Hle("sceKernelResumeThreadForVM")]
    int Resume(int uid)
    {
        var t = Target(uid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        return t.ResumeForVm() ? 0 : ERR_NOT_SUSPENDED;
    }

    [Hle("sceKernelGetThreadContextForMono")]
    [Hle("_sceKernelGetThreadContextForVM")]
    int GetContext(int uid, uint cpuInfo, uint vfpInfo)
    {
        var t = Target(uid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if (t.SuspendCount == 0) return ERR_NOT_SUSPENDED;
        var s = t.Core.State;
        if (cpuInfo != 0)
        {
            if (GuestMemory.R32(cpuInfo) < 0x58) return ERR_ILLEGAL_SIZE;
            GuestMemory.W32(cpuInfo + 4, (s->Cpsr & ~0x1Fu) | 0x10);
            for (int i = 0; i < 16; i++) GuestMemory.W32(cpuInfo + 8 + (uint)i * 4, s->R[i]);
            GuestMemory.W32(cpuInfo + 72, s->TpidrUrw);
            GuestMemory.W32(cpuInfo + 76, 100000);
            GuestMemory.W32(cpuInfo + 80, t.StackAddr + t.StackSize);
            GuestMemory.W32(cpuInfo + 84, t.StackAddr);
        }
        if (vfpInfo != 0)
        {
            if (GuestMemory.R32(vfpInfo) < 0x108) return ERR_ILLEGAL_SIZE;
            GuestMemory.W32(vfpInfo + 4, s->Fpscr);
            new ReadOnlySpan<byte>(s->D, 256).CopyTo(GuestMemory.Span(vfpInfo + 8, 256));
        }
        return 0;
    }

    [Hle("sceKernelSetThreadContextForMono")]
    [Hle("_sceKernelSetThreadContextForVM")]
    int SetContext(int uid, uint cpuInfo, uint vfpInfo)
    {
        var t = Target(uid);
        if (t == null) return SceError.UNKNOWN_THREAD_ID;
        if (t.SuspendCount == 0) return ERR_NOT_SUSPENDED;
        var s = t.Core.State;
        if (cpuInfo != 0)
            for (int i = 0; i < 16; i++) s->R[i] = GuestMemory.R32(cpuInfo + 8 + (uint)i * 4);
        if (vfpInfo != 0)
        {
            s->Fpscr = GuestMemory.R32(vfpInfo + 4);
            GuestMemory.Span(vfpInfo + 8, 256).CopyTo(new Span<byte>(s->D, 256));
        }
        return 0;
    }

    [Hle("sceKernelWaitExceptionForMono")]
    int WaitException()
    {
        var self = GuestThread.Current!;
        lock (self.WaitLock) Wait.Until(self.WaitLock, () => false, default, what: "exception (forever)");
        return 0;
    }
}
