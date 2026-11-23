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
/// <item>The PowerVR services bridge: PVRSRVOpen connects the calling process to the services (its per-process data,
/// counted per open) and PVRSRVRelease drops one connection; PVRSRV_BridgeDispatchKM(?, package) takes a 0x1C-byte
/// PVRSRV_BRIDGE_PACKAGE { bridgeId (0x1000 + index), size, in, inSize, out, outSize, hKernelServices }.</item>
/// </list>
/// </summary>
public sealed partial class GpuEs4 : HleModule
{
    const int ERR_INVALID = unchecked((int)0x804C0000), ERR_ALREADY = unchecked((int)0x804C000E), KernelWaitTimeout = unchecked((int)0x80028005);
    const uint LockupTimeoutUs = 100_000;

    readonly object _lock = new();
    int _salvage = -1;

    ThreadMgr? _tm;
    ThreadMgr Tm => _tm ??= Emu.Hle.Modules.OfType<ThreadMgr>().First();

    /// <summary>The driver's answer when it cannot copy the package or the buffers, the caller has no services
    /// connection, or the bridge call is refused (-14, 0xFFFFFFF2).</summary>
    const int BridgeFault = -14;
    const uint BridgeIdBase = 0x1000, BridgePackageSize = 0x1C, BridgeCount = 0x94, BridgeBufferMax = 0x1000;
    const uint BridgeConnectServices = 0x0C, BridgeDisconnectServices = 0x0D;

    /// <summary>The handle of the process's per-process data (hKernelServices) that CONNECT_SERVICES hands out and the
    /// other bridge calls name.</summary>
    public const uint KernelServicesHandle = 1;

    /// <summary>PVRSRVOpen calls not yet released: the process has per-process data while it is above 0.</summary>
    int _servicesConnections;

    /// <summary>PVRSRVOpen(): connects the process to the services (PVRSRVPerProcessDataConnect, then a GPU power
    /// request); 0, or -1 when the connection fails.</summary>
    [Hle("PVRSRVOpen")]
    int PVRSRVOpen()
    {
        lock (_lock)
        {
            _servicesConnections++;
        }

        return 0;
    }

    /// <summary>PVRSRVRelease(): drops one connection of the process (PVRSRVPerProcessDataDisconnect); always 0.</summary>
    [Hle("PVRSRVRelease")]
    int PVRSRVRelease()
    {
        lock (_lock)
        {
            if (_servicesConnections > 0)
            {
                _servicesConnections--;
            }
        }

        return 0;
    }

    /// <summary>
    /// PVRSRV_BridgeDispatchKM(?, package): copies the package from the caller (-14 when it cannot); CONNECT_SERVICES
    /// needs the process's per-process data (PVRSRVOpen), every other call a hKernelServices that is the process's;
    /// then the in/out buffers must be at most 0x1000 bytes and the index below 0x94. Before the services' init
    /// server has run (its state is the one the kernel keeps with PVRSRVSetInitServerState; until kernel code marks it, it has not run) the driver only accepts
    /// CONNECT/DISCONNECT_SERVICES and the init server's own connect calls, and refuses the rest with -14; the init
    /// server calls themselves need the SGX device and are refused too (the "default" part). CONNECT_SERVICES writes
    /// { eError = PVRSRV_OK, hKernelServices }, DISCONNECT_SERVICES { eError = PVRSRV_OK }.
    /// </summary>
    [Hle("PVRSRV_BridgeDispatchKM", Default = true)]
    int PVRSRV_BridgeDispatchKM(uint unused, uint package)
    {
        if (package == 0 || !GuestMemory.IsMapped(package) || !GuestMemory.IsMapped(package + BridgePackageSize - 1))
        {
            return BridgeFault;
        }

        uint bridgeId = GuestMemory.R32(package);
        uint inBuffer = GuestMemory.R32(package + 8);
        uint inSize = GuestMemory.R32(package + 12);
        uint outBuffer = GuestMemory.R32(package + 16);
        uint outSize = GuestMemory.R32(package + 20);
        uint kernelServices = GuestMemory.R32(package + 24);
        bool inReadable = inSize == 0 || (inBuffer != 0 && GuestMemory.IsMapped(inBuffer) && GuestMemory.IsMapped(inBuffer + inSize - 1));
        int result = BridgeDispatch(bridgeId, inSize, inReadable, outSize, kernelServices, out uint error, out uint second);
        if (result == 0)
        {
            WriteBridgeOut(outBuffer, outSize, error, second);
        }

        return result;
    }

    /// <summary>
    /// The driver's checks and handlers of one bridge call (the part of PVRSRV_BridgeDispatchKM after the package is
    /// copied): 0 with the handler's eError and first output word, or -14. <paramref name="inReadable"/> = the caller's
    /// in buffer can be copied (the user library's wrappers build it on their own stack).
    /// </summary>
    int BridgeDispatch(uint bridgeId, uint inSize, bool inReadable, uint outSize, uint kernelServices, out uint error, out uint second)
    {
        error = 0;
        second = 0;
        bool connected = Volatile.Read(ref _servicesConnections) > 0;
        if (!connected || (bridgeId != BridgeIdBase + BridgeConnectServices && kernelServices != KernelServicesHandle))
        {
            return BridgeFault;
        }

        uint index = bridgeId - BridgeIdBase;
        if (inSize > BridgeBufferMax || outSize > BridgeBufferMax || index >= BridgeCount)
        {
            return BridgeFault;
        }

        if (!inReadable)
        {
            return BridgeFault;
        }

        if (!BridgeAllowedByInitServer(index))
        {
            return BridgeFault;
        }

        if (index == BridgeConnectServices)
        {
            second = KernelServicesHandle;
            return 0;
        }

        if (index == BridgeDisconnectServices)
        {
            return 0;
        }

        return BridgeFault;
    }

    /// <summary>The driver copies the handler's output (eError, then the call's words) back into the caller's out
    /// buffer, as many bytes as the package says.</summary>
    static void WriteBridgeOut(uint outBuffer, uint outSize, uint error, uint second)
    {
        if (outBuffer == 0 || outSize == 0 || !GuestMemory.IsMapped(outBuffer) || !GuestMemory.IsMapped(outBuffer + outSize - 1))
        {
            return;
        }

        if (outSize >= 4)
        {
            GuestMemory.W32(outBuffer, error);
        }

        if (outSize >= 8)
        {
            GuestMemory.W32(outBuffer + 4, second);
        }
    }

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
        Tm.sceKernelDelayThreadThroughEntry(LockupTimeoutUs);
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
