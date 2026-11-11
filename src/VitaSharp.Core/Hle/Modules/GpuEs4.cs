using VitaSharp.Core.Gpu;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceGpuEs4ForUser (the SGX driver gpu_es4.skprx 3.74, syscalls), the calls that map onto VitaSharp's GPU model:
/// <list type="bullet">
/// <item>sceGpuSignalWait(uint32 *count, timeout): the driver keeps a counter of GPU signals; when the caller's value
/// is the current one it waits on the driver's condition (with the timeout when one is given), then stores the
/// current counter. Here the counter is the GPU queue's notification signals (<see cref="GpuQueue.SignalCount"/>), the
/// same events sceGxmNotificationWait wakes on.</item>
/// <item>sceGpuSignalWaitLockup(): waits on the driver's lockup condition with its fixed 100 ms timeout; the
/// emulated GPU never locks up, so it answers the kernel's wait timeout after 100 ms.</item>
/// <item>sceGpu(Un)RegisterSalvage(SceUID): one salvage (crash dump) buffer registration; -1 is refused
/// (0x804C0000), a second registration is 0x804C000E, unregistering another UID 0x804C0000.</item>
/// </list>
/// The PVRSRV bridge (PVRSRVOpen/Release/BridgeDispatchKM) needs the services model of the PowerVR driver, which the
/// emulator (GXM in HLE) does not have.
/// </summary>
public sealed class GpuEs4 : HleModule
{
    const int ERR_INVALID = unchecked((int)0x804C0000), ERR_ALREADY = unchecked((int)0x804C000E), KernelWaitTimeout = unchecked((int)0x80028005);
    const uint LockupTimeoutUs = 100_000;

    readonly object _lock = new();
    int _salvage = -1;

    ThreadMgr? _tm;
    ThreadMgr Tm => _tm ??= Emu.Hle.Modules.OfType<ThreadMgr>().First();

    [Hle("sceGpuSignalWait")]
    int sceGpuSignalWait(uint count, uint timeoutUs)
    {
        if (count == 0) return ERR_INVALID;
        uint seen = GuestMemory.R32(count);
        lock (GpuQueue.NotificationSync)
        {
            if (seen == GpuQueue.SignalCount)
            {
                using var blocked = Scheduler.Block();
                long deadline = timeoutUs == 0 ? long.MaxValue : Environment.TickCount64 + Math.Max(1, timeoutUs / 1000);
                while (seen == GpuQueue.SignalCount && Emu.Running)
                {
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0) return KernelWaitTimeout;
                    Monitor.Wait(GpuQueue.NotificationSync, (int)Math.Min(left, 50));
                }
            }
            GuestMemory.W32(count, GpuQueue.SignalCount);
        }
        return 0;
    }

    [Hle("sceGpuSignalWaitLockup")]
    int sceGpuSignalWaitLockup()
    {
        Tm.sceKernelDelayThread(LockupTimeoutUs);
        return KernelWaitTimeout;
    }

    [Hle("sceGpuRegisterSalvage")]
    int sceGpuRegisterSalvage(int uid)
    {
        if (uid == -1) return ERR_INVALID;
        lock (_lock)
        {
            if (_salvage != -1) return ERR_ALREADY;
            _salvage = uid;
        }
        return 0;
    }

    [Hle("sceGpuUnregisterSalvage")]
    int sceGpuUnregisterSalvage(int uid)
    {
        if (uid == -1) return ERR_INVALID;
        lock (_lock)
        {
            if (_salvage != uid) return ERR_INVALID;
            _salvage = -1;
        }
        return 0;
    }
}
