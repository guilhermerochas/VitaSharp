using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The per-thread words of the FW 3.74 thread control block that SceThreadmgrForDriver reads and writes for the current
/// thread (decompiled threadmgr.skprx, study only): +0xFC the process id kernel code works for ("process id in TLS",
/// set from the thread's process when the thread is set up: the kernel's 0x10005 for a kernel thread) and +0x100, set
/// to 0 then. Each entry answers 0x80027101 without a current thread.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    /// <summary>A process id below 0 given to ksceKernelSetProcessIdToTLS.</summary>
    const int InvalidTlsProcessId = unchecked((int)0x80024501);

    /// <summary>The +0xFC word of each thread that changed it (by thread UID); the others hold their process.</summary>
    [SnapshotMissing] // states of builds before it: no thread had changed it
    readonly Dictionary<int, int> _tlsProcessIds = new();

    /// <summary>The process a thread works for when its control block is set up.</summary>
    static int InitialTlsProcessId(GuestThread thread) => thread.KernelMode ? KernelSpace.KernelPid : thread.Pid;

    /// <summary>ksceKernelGetProcessIdFromTLS(): the current thread's +0xFC word.</summary>
    [Hle(Nid = 0xFA54D49A, Library = "SceThreadmgrForDriver")]
    int ksceKernelGetProcessIdFromTLS()
    {
        if (GuestThread.Current is not { } thread)
        {
            return IllegalContext;
        }

        lock (_tlsProcessIds)
        {
            return _tlsProcessIds.TryGetValue(thread.Uid, out int pid) ? pid : InitialTlsProcessId(thread);
        }
    }

    /// <summary>ksceKernelSetProcessIdToTLS(SceUID pid): sets the current thread's +0xFC word and returns the earlier
    /// one; a negative value is 0x80024501.</summary>
    [Hle(Nid = 0x0486F239, Library = "SceThreadmgrForDriver")]
    int ksceKernelSetProcessIdToTLS(int pid)
    {
        if (pid < 0)
        {
            return InvalidTlsProcessId;
        }

        if (GuestThread.Current is not { } thread)
        {
            return IllegalContext;
        }

        lock (_tlsProcessIds)
        {
            int earlier = _tlsProcessIds.TryGetValue(thread.Uid, out int pidNow) ? pidNow : InitialTlsProcessId(thread);
            _tlsProcessIds[thread.Uid] = pid;
            return earlier;
        }
    }

    /// <summary>NID_56C18B11(): the current thread's +0x100 word, 0 from the thread's setup until NID_84EB1EA4 sets it.</summary>
    [Hle(Nid = 0x56C18B11, Library = "SceThreadmgrForDriver")]
    int NID_56C18B11()
    {
        if (GuestThread.Current is not { } thread)
        {
            return IllegalContext;
        }

        lock (_word100)
        {
            return _word100.GetValueOrDefault(thread.Uid);
        }
    }
}
