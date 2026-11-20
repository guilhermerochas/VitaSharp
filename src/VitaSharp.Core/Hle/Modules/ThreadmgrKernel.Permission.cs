using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Per-thread words of the FW 3.74 thread control block read by SceThreadmgrForDriver (threadmgr.skprx and
/// processmgr.skprx decompiled, study only). +0x2C is the thread's permission: a new thread takes its process's
/// (process object +0xA0), which is 0x80 for the kernel process; for a user process the process manager takes it from
/// the program's authentication information (the security side, not modelled), so a user thread starts at 0 here
/// (logged once). Kernel code raises it with ksceKernelSetPermission and puts the earlier value back. +0xA8 holds the
/// thread's attributes as created: bit 31 a user thread, bit 14 the attribute NID_1096042E reports.
/// </summary>
public sealed partial class ThreadmgrKernel
{
    /// <summary>The permission of the kernel process (processmgr: process object +0xA0 = 0x80 at boot).</summary>
    const int KernelPermission = 0x80;

    /// <summary>NID_1096042E's answer for a UID that is not a thread.</summary>
    const uint NotAThreadAttribute = 0x40000000;

    /// <summary>The +0x2C word of each thread that changed it (by thread UID); the others hold their process's.</summary>
    [SnapshotMissing] // states of builds before it: no thread had changed it
    readonly Dictionary<int, int> _permissions = new();


    [SnapshotIgnore]
    bool _userPermissionLogged;

    int InitialPermission(GuestThread thread)
    {
        if (thread.KernelMode)
        {
            return KernelPermission;
        }

        if (!_userPermissionLogged)
        {
            _userPermissionLogged = true;
            Log.Info("threadmgr: a user thread's permission starts at 0 (its process's comes from the program's authentication information)");
        }

        return 0;
    }

    /// <summary>The current thread's permission (other modules' checks: 0x40 and 0x80 are the kernel's).</summary>
    internal int CurrentPermission() => NID_332E127C();

    /// <summary>NID_332E127C(): the current thread's permission (+0x2C); 0x80027101 without a current thread.</summary>
    [Hle(Nid = 0x332E127C, Library = "SceThreadmgrForDriver")]
    int NID_332E127C()
    {
        if (GuestThread.Current is not { } thread)
        {
            return IllegalContext;
        }

        lock (_permissions)
        {
            return _permissions.TryGetValue(thread.Uid, out int permission) ? permission : InitialPermission(thread);
        }
    }

    /// <summary>ksceKernelSetPermission(int value): sets the current thread's permission and returns the earlier one;
    /// a negative value is SCE_KERNEL_ERROR_INVALID_ARGUMENT (checked first), no current thread 0x80027101.</summary>
    [Hle(Nid = 0x02EEDF17, Library = "SceThreadmgrForDriver")]
    int ksceKernelSetPermission(int value)
    {
        if (value < 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestThread.Current is not { } thread)
        {
            return IllegalContext;
        }

        lock (_permissions)
        {
            int earlier = _permissions.TryGetValue(thread.Uid, out int permission) ? permission : InitialPermission(thread);
            _permissions[thread.Uid] = value;
            return earlier;
        }
    }

    /// <summary>NID_91382762(): 1 when the current thread is a user thread (attribute bit 31), 0 for a kernel thread or
    /// without a current thread.</summary>
    [Hle(Nid = 0x91382762, Library = "SceThreadmgrForDriver")]
    int NID_91382762() => GuestThread.Current is { KernelMode: false } ? 1 : 0;

    /// <summary>NID_1096042E(SceUID thid): bit 14 of the thread's attributes (thid 0: the current thread, 0 without
    /// one); a UID that is not a thread gives 0x40000000.</summary>
    [Hle(Nid = 0x1096042E, Library = "SceThreadmgrForDriver")]
    uint NID_1096042E(int thid)
    {
        if (thid == 0)
        {
            return GuestThread.Current is { } current ? (current.Attr >> 14) & 1 : 0;
        }

        return K.Objects.Get<GuestThread>(thid) is { } thread ? (thread.Attr >> 14) & 1 : NotAThreadAttribute;
    }
}
