using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceGpuEs4ForDriver, the PowerVR services of the SGX driver gpu_es4.skprx (FW 3.74, decompiled, study only) that the
/// GPU init module (SceGpuEs4Init), the GPU core dump and SceCoredump call: the OS layer (kernel heap, memory copy,
/// the current process), the services' device list, per-process data and init server state, device memory, the
/// miscellaneous information, the GPU MMU mappings and the core dump readers. VitaSharp draws the GXM command
/// streams itself, so the SGX device the services list is a record kernel code can read (device type 1, SGX, index
/// 0) with nothing behind its registers, the MMU mappings are kept as a list (the emulated GPU reads guest memory
/// directly), the GPU never locks up (no register dump, no lockup events) and the init server state is the one the
/// console has after its boot (ran, successful). The services' errors are their small PVRSRV_ERROR numbers (3
/// invalid parameters, 4 the device was not found); the driver's own are 0x804C00xx.
/// </summary>
public sealed partial class GpuEs4
{
    const int PvrDeviceNotFound = 4, PvrInvalidState = 0xA2;
    const uint SgxDeviceType = 1, SgxDeviceIndex = 0, SgxDeviceClass = 0;

    /// <summary>The services' init server states (0 running, 1 ran, 2 successful), kept in the kernel process's module,
    /// the one copy every process's bridge reads (<see cref="InitServerStates"/>). On the console SceGpuEs4Init runs the
    /// init server while the system boots, before the shell or any game, and it succeeds; VitaSharp does not run the
    /// firmware's kernel modules, so the end state of that boot step is the state the services start in.</summary>
    readonly uint[] _initServerStates = [0, 1, 1];

    /// <summary>The init server states of the services: the kernel process's module's (this one's without a kernel
    /// process), so what the kernel marks is what every process's bridge sees.</summary>
    uint[] InitServerStates
    {
        get
        {
            if (Emu?.Vita.KernelProcess is not { } kernel)
            {
                return _initServerStates;
            }

            return (kernel.Hle.Modules.OfType<GpuEs4>().FirstOrDefault() ?? this)._initServerStates;
        }
    }

    /// <summary>The driver's gate of a bridge call from a process that is not the init server (FW 3.74 gpu_es4
    /// dispatch): before the init server ran only the connect/disconnect calls (0x0C, 0x0D, 0x48, 0x49) while it is not
    /// running; after it ran only when it was successful.</summary>
    internal bool BridgeAllowedByInitServer(uint index)
    {
        uint[] states = InitServerStates;
        if (states[1] == 0)
        {
            return states[0] == 0 && (index is 0x0C or 0x0D or 0x48 or 0x49);
        }

        return states[2] != 0;
    }

    /// <summary>The SGX device node and its device info (made on first request).</summary>
    uint _deviceNode;

    /// <summary>The per-process data of each connected process: pid -> record (0x21C bytes; +0 the pid, +0x18 the
    /// connections).</summary>
    readonly Dictionary<int, uint> _perProcessData = new();

    /// <summary>The GPU MMU mappings: device address -> (CPU address, size).</summary>
    readonly Dictionary<uint, (uint Cpu, uint Size)> _gpuMappings = new();

    /// <summary>The words NID_26B03DCE sets for the GPU core dump.</summary>
    uint _coreDumpWord0, _coreDumpWord1;

    uint KernelAllocation(uint size, string name)
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uint address = Mem.Alloc(Math.Max(size, 4u), name, MemoryRegion.Kernel, 8);
            GuestMemory.Fill(address, 0, Math.Max(size, 4u));
            return address;
        }
    }

    void KernelFree(uint address)
    {
        if (address == 0)
        {
            return;
        }

        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            Mem.Free(address);
        }
    }

    /// <summary>The SGX device node (0x60 bytes: +0 the device type, +4 its class, +8 its index, +0x54 its device info
    /// of 0x900 bytes).</summary>
    uint DeviceNode()
    {
        lock (_lock)
        {
            if (_deviceNode == 0)
            {
                _deviceNode = KernelAllocation(0x60, "kernel:sgx:devicenode");
                GuestMemory.W32(_deviceNode + 0x00, SgxDeviceType);
                GuestMemory.W32(_deviceNode + 0x04, SgxDeviceClass);
                GuestMemory.W32(_deviceNode + 0x08, SgxDeviceIndex);
                GuestMemory.W32(_deviceNode + 0x54, KernelAllocation(0x900, "kernel:sgx:deviceinfo"));
            }

            return _deviceNode;
        }
    }

    // ------------------------------------------------------------------ the OS layer

    /// <summary>OSAllocMem (0x68815236)(size): kernel heap memory of the kernel process (NULL when none).</summary>
    [Hle(Nid = 0x68815236, Library = "SceGpuEs4ForDriver", ReturnsValue = true)]
    uint OSAllocMem(uint size)
    {
        try
        {
            return KernelAllocation(size, "kernel:sgx:osalloc");
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }
    }

    /// <summary>OSFreeMem (0x0BB49287)(ptr): gives an OSAllocMem block back; 0.</summary>
    [Hle(Nid = 0x0BB49287, Library = "SceGpuEs4ForDriver")]
    int OSFreeMem(uint pointer)
    {
        KernelFree(pointer);
        return 0;
    }

    /// <summary>OSGetCurrentProcessIDKM (0x6C6F2BEA)(): the calling thread's process.</summary>
    [Hle(Nid = 0x6C6F2BEA, Library = "SceGpuEs4ForDriver")]
    int OSGetCurrentProcessIDKM() => ProcessScope.CurrentPid;

    /// <summary>OSMemCopy (0x771EC61E)(dst, src, len): memcpy.</summary>
    [Hle(Nid = 0x771EC61E, Library = "SceGpuEs4ForDriver", ReturnsValue = true)]
    uint OSMemCopy(uint destination, uint source, uint length)
    {
        GuestMemory.Copy(destination, source, length);
        return destination;
    }

    /// <summary>OSMemSet (0x50494916)(dst, value, len): memset.</summary>
    [Hle(Nid = 0x50494916, Library = "SceGpuEs4ForDriver", ReturnsValue = true)]
    uint OSMemSet(uint destination, uint value, uint length)
    {
        GuestMemory.Fill(destination, (byte)value, length);
        return destination;
    }

    /// <summary>OSProcHasPrivSrvInit (0x302C7C5F)(): every caller may run the init server: 1.</summary>
    [Hle(Nid = 0x302C7C5F, Library = "SceGpuEs4ForDriver")]
    int OSProcHasPrivSrvInit() => 1;

    // ------------------------------------------------------------------ devices and the init server

    /// <summary>PVRSRVEnumerateDevicesKM (0xEC5163B0)(uint *count, PVRSRV_DEVICE_IDENTIFIER *ids): the SGX device
    /// ({type, class, index}); NULL 3.</summary>
    [Hle(Nid = 0xEC5163B0, Library = "SceGpuEs4ForDriver")]
    int PVRSRVEnumerateDevicesKM(uint countOut, uint identifiers)
    {
        if (countOut == 0 || identifiers == 0)
        {
            return PvrInvalidParams;
        }

        GuestMemory.W32(identifiers + 0, SgxDeviceType);
        GuestMemory.W32(identifiers + 4, SgxDeviceClass);
        GuestMemory.W32(identifiers + 8, SgxDeviceIndex);
        GuestMemory.W32(countOut, 1);
        return 0;
    }

    /// <summary>PVRSRVAcquireDeviceDataKM (0xE6B866FD)(index, type, void **node): the device node of the SGX device
    /// (index 0, or type SGX for the unknown index 0xFFFFFFFF); another device 4.</summary>
    [Hle(Nid = 0xE6B866FD, Library = "SceGpuEs4ForDriver")]
    int PVRSRVAcquireDeviceDataKM(uint index, uint type, uint nodeOut)
    {
        bool sgx = index == SgxDeviceIndex || (index == 0xFFFFFFFF && type == SgxDeviceType);
        if (!sgx)
        {
            return PvrDeviceNotFound;
        }

        if (nodeOut != 0)
        {
            GuestMemory.W32(nodeOut, DeviceNode());
        }

        return 0;
    }

    /// <summary>PVRSRVGetInitServerState (0x3AF6FE80)(state): the state's flag (0 running, 1 ran, 2 successful); another
    /// state 0.</summary>
    [Hle(Nid = 0x3AF6FE80, Library = "SceGpuEs4ForDriver")]
    uint PVRSRVGetInitServerState(uint state) => state < 3 ? InitServerStates[state] : 0;

    /// <summary>PVRSRVSetInitServerState (0xDCD78852)(state, value); another state 0xA2.</summary>
    [Hle(Nid = 0xDCD78852, Library = "SceGpuEs4ForDriver")]
    int PVRSRVSetInitServerState(uint state, uint value)
    {
        if (state >= 3)
        {
            return PvrInvalidState;
        }

        InitServerStates[state] = value;
        return 0;
    }

    /// <summary>PVRSRVFinaliseSystem (0x399BC738)(successful): the end of the init server's run; the devices' finalise
    /// steps have no hardware to start.</summary>
    [Hle(Nid = 0x399BC738, Library = "SceGpuEs4ForDriver")]
    void PVRSRVFinaliseSystem(uint successful)
    {
    }

    /// <summary>SGXGetInfoForSrvinitKM (0x668B1A60)(node, info): +0 the device info's +0x30 (the SGX's MMU page
    /// directory), then its heap information (none set: zeros).</summary>
    [Hle(Nid = 0x668B1A60, Library = "SceGpuEs4ForDriver")]
    int SGXGetInfoForSrvinitKM(uint node, uint info)
    {
        uint deviceInfo = GuestMemory.R32(node + 0x54);
        GuestMemory.W32(info, GuestMemory.R32(deviceInfo + 0x30));
        return 0;
    }

    /// <summary>DevInitSGXPart2KM (0x49AD869F)(?, node, initInfo): the init server's second SGX step: the init data
    /// (0x6C0 bytes from +0x12C) into the device info (+0x108) and the kernel memory blocks it names; 0.</summary>
    [Hle(Nid = 0x49AD869F, Library = "SceGpuEs4ForDriver")]
    int DevInitSGXPart2KM(uint unused, uint node, uint initInfo)
    {
        if (node == 0 || initInfo == 0)
        {
            return PvrInvalidParams;
        }

        uint deviceInfo = GuestMemory.R32(node + 0x54);
        GuestMemory.Copy(deviceInfo + 0x108, initInfo + 0x12C, 0x6C0);
        GuestMemory.W32(deviceInfo + 0x38, GuestMemory.R32(initInfo));
        GuestMemory.W32(deviceInfo + 0x44, GuestMemory.R32(initInfo + 0xC));
        GuestMemory.W32(deviceInfo + 0x4C, GuestMemory.R32(initInfo + 8));
        return 0;
    }

    // ------------------------------------------------------------------ per-process data

    /// <summary>PVRSRVPerProcessData (0x3DAB1F7F)(pid): the process's per-process data, or NULL.</summary>
    [Hle(Nid = 0x3DAB1F7F, Library = "SceGpuEs4ForDriver", ReturnsValue = true)]
    uint PVRSRVPerProcessData(int pid)
    {
        lock (_lock)
        {
            return _perProcessData.GetValueOrDefault(pid);
        }
    }

    /// <summary>PVRSRVPerProcessDataConnect (0x1A53327D)(pid): the process's per-process data, made on the first
    /// connection (+0 the pid, +0x18 the connections); 0.</summary>
    [Hle(Nid = 0x1A53327D, Library = "SceGpuEs4ForDriver")]
    int PVRSRVPerProcessDataConnect(int pid)
    {
        lock (_lock)
        {
            if (!_perProcessData.TryGetValue(pid, out uint record))
            {
                record = KernelAllocation(0x21C, "kernel:sgx:perprocess");
                GuestMemory.W32(record, (uint)pid);
                _perProcessData[pid] = record;
            }

            GuestMemory.W32(record + 0x18, GuestMemory.R32(record + 0x18) + 1);
        }

        return 0;
    }

    /// <summary>PVRSRVPerProcessDataDisconnect (0xB4A2B60F)(pid): drops a connection; the last one frees the data.</summary>
    [Hle(Nid = 0xB4A2B60F, Library = "SceGpuEs4ForDriver")]
    void PVRSRVPerProcessDataDisconnect(int pid)
    {
        lock (_lock)
        {
            if (!_perProcessData.TryGetValue(pid, out uint record))
            {
                return;
            }

            uint connections = GuestMemory.R32(record + 0x18) - 1;
            GuestMemory.W32(record + 0x18, connections);
            if (connections == 0)
            {
                _perProcessData.Remove(pid);
                KernelFree(record);
            }
        }
    }

    // ------------------------------------------------------------------ device memory

    /// <summary>PVRSRVAllocDeviceMemKM (0x623E50D0)(?, perProcess, heap, flags, size, alignment, ?, info **out): a
    /// kernel memory info (0x3C bytes: +0 the CPU address, +4 the device address, +0xC the flags, +0x10 the size) over
    /// <paramref name="size"/> bytes; a NULL heap, size or output, or flags without bit 0x400000 (a kernel allocation)
    /// 3.</summary>
    [Hle(Nid = 0x623E50D0, Library = "SceGpuEs4ForDriver")]
    int PVRSRVAllocDeviceMemKM(uint unused, uint perProcess, uint heap, uint flags, uint size, uint alignment, uint unused2, uint infoOut)
    {
        if (heap == 0 || size == 0 || infoOut == 0 || (flags & 0x400000) == 0)
        {
            return PvrInvalidParams;
        }

        uint memory = KernelAllocation(size, "kernel:sgx:devicemem");
        uint info = KernelAllocation(0x3C, "kernel:sgx:meminfo");
        GuestMemory.W32(info + 0x00, memory);
        GuestMemory.W32(info + 0x04, memory);
        GuestMemory.W32(info + 0x0C, flags);
        GuestMemory.W32(info + 0x10, size);
        GuestMemory.W32(infoOut, info);
        return 0;
    }

    /// <summary>PVRSRVFreeDeviceMemKM (0xD9CE787A)(?, info): frees a PVRSRVAllocDeviceMemKM allocation; NULL 3.</summary>
    [Hle(Nid = 0xD9CE787A, Library = "SceGpuEs4ForDriver")]
    int PVRSRVFreeDeviceMemKM(uint unused, uint info)
    {
        if (info == 0)
        {
            return PvrInvalidParams;
        }

        KernelFree(GuestMemory.R32(info));
        KernelFree(info);
        return 0;
    }

    /// <summary>PVRSRVGetMiscInfoKM (0x8FB57186)(info): the requests of +0 (only bits 0x98 known, else 3): 0x10 the
    /// DDK version 1.7.17.5029 (+0x88.. its four numbers, the "1.7.17.5029" string into the +8 buffer of +0xC bytes);
    /// +4 the requests answered; NULL 3.</summary>
    [Hle(Nid = 0x8FB57186, Library = "SceGpuEs4ForDriver")]
    int PVRSRVGetMiscInfoKM(uint info)
    {
        if (info == 0)
        {
            return PvrInvalidParams;
        }

        GuestMemory.W32(info + 4, 0);
        uint requests = GuestMemory.R32(info);
        if ((requests & 0xFFFFFF67) != 0)
        {
            return PvrInvalidParams;
        }

        uint buffer = GuestMemory.R32(info + 8);
        if ((requests & 0x10) != 0 && buffer != 0)
        {
            const string Version = "1.7.17.5029";
            if (GuestMemory.R32(info + 0xC) < Version.Length + 1)
            {
                return PvrInvalidParams;
            }

            GuestMemory.W32(info + 4, 0x10);
            GuestMemory.W32(info + 0x88, 1);
            GuestMemory.W32(info + 0x8C, 7);
            GuestMemory.W32(info + 0x90, 0x11);
            GuestMemory.W32(info + 0x94, 0x13A5);
            GuestMemory.WriteCString(buffer, Version, Version.Length + 1);
        }

        return 0;
    }

    // ------------------------------------------------------------------ the GPU MMU

    /// <summary>ksceGpuMmuMapMemory (0xEF4B16D3)(context, devAddr, cpuAddr, size, flags): maps CPU pages for the GPU
    /// (page-aligned, else 0x804C0000); kept as a mapping, the emulated GPU reading guest memory.</summary>
    [Hle(Nid = 0xEF4B16D3, Library = "SceGpuEs4ForDriver")]
    int ksceGpuMmuMapMemory(uint context, uint deviceAddress, uint cpuAddress, uint size, uint flags)
    {
        if (context == 0 || ((deviceAddress | cpuAddress | size) & 0xFFF) != 0)
        {
            return ERR_INVALID;
        }

        lock (_lock)
        {
            _gpuMappings[deviceAddress] = (cpuAddress, size);
        }

        return 0;
    }

    /// <summary>ksceGpuMmuUnmapMemory (0x9AAAD91C)(context, devAddr, size): removes a mapping (page-aligned, else
    /// 0x804C0000).</summary>
    [Hle(Nid = 0x9AAAD91C, Library = "SceGpuEs4ForDriver")]
    int ksceGpuMmuUnmapMemory(uint context, uint deviceAddress, uint size)
    {
        if (context == 0 || ((deviceAddress | size) & 0xFFF) != 0)
        {
            return ERR_INVALID;
        }

        lock (_lock)
        {
            _gpuMappings.Remove(deviceAddress);
        }

        return 0;
    }

    // ------------------------------------------------------------------ lockups and the core dump

    /// <summary>ksceGpuSignalWaitLockup (0x40E292D7)(): sceGpuSignalWaitLockup (the 100 ms wait; no lockup).</summary>
    [Hle(Nid = 0x40E292D7, Library = "SceGpuEs4ForDriver", Cost = HleCost.SchedulerWait)]
    int ksceGpuSignalWaitLockup() => sceGpuSignalWaitLockupThroughEntry();

    /// <summary>ksceGpuGetRegisterDump (0x14ECCFCD)(buffer, size): clears the buffer; the dump of a locked-up GPU, which
    /// the emulated one never is: 0x804C0000.</summary>
    [Hle(Nid = 0x14ECCFCD, Library = "SceGpuEs4ForDriver")]
    int ksceGpuGetRegisterDump(uint buffer, uint size)
    {
        if (buffer != 0)
        {
            GuestMemory.Fill(buffer, 0, size);
        }

        return ERR_INVALID;
    }

    /// <summary>NID_395F7689(): the register dump's size (0x810).</summary>
    [Hle(Nid = 0x395F7689, Library = "SceGpuEs4ForDriver")]
    int NID_395F7689() => 0x810;

    /// <summary>NID_18C90B42(): the lockup count the core dump reads (never locked up: 0).</summary>
    [Hle(Nid = 0x18C90B42, Library = "SceGpuEs4ForDriver")]
    int NID_18C90B42() => 0;

    /// <summary>NID_F001AE90(): the hardware recovery count the core dump reads (0).</summary>
    [Hle(Nid = 0xF001AE90, Library = "SceGpuEs4ForDriver")]
    int NID_F001AE90() => 0;

    /// <summary>NID_26B03DCE(a, b): sets the two words the GPU core dump module hands the driver.</summary>
    [Hle(Nid = 0x26B03DCE, Library = "SceGpuEs4ForDriver")]
    void NID_26B03DCE(uint first, uint second)
    {
        _coreDumpWord0 = first;
        _coreDumpWord1 = second;
    }

    /// <summary>NID_7AA79658(context, SceUID *pid): the process of a GPU memory context (the kernel's for the
    /// services' own, the connected processes' by their per-process data); none 0x804C0005, NULL 0x804C0000.</summary>
    [Hle(Nid = 0x7AA79658, Library = "SceGpuEs4ForDriver")]
    int NID_7AA79658(uint context, uint pidOut)
    {
        if (context == 0 || pidOut == 0)
        {
            return ERR_INVALID;
        }

        lock (_lock)
        {
            foreach (KeyValuePair<int, uint> entry in _perProcessData)
            {
                if (entry.Value == context)
                {
                    GuestMemory.W32(pidOut, (uint)entry.Key);
                    return 0;
                }
            }
        }

        if (context == _deviceNode)
        {
            GuestMemory.W32(pidOut, (uint)KernelSpace.KernelPid);
            return 0;
        }

        return unchecked((int)0x804C0005);
    }

    /// <summary>NID_8A3929D2(buffer, size): the last commands the GPU ran, for the core dump (none recorded: the
    /// buffer cleared); 0.</summary>
    [Hle(Nid = 0x8A3929D2, Library = "SceGpuEs4ForDriver")]
    int NID_8A3929D2(uint buffer, uint size)
    {
        if (buffer != 0 && size != 0)
        {
            GuestMemory.Fill(buffer, 0, size);
        }

        return 0;
    }

    /// <summary>NID_990338F6(info, ?, ?): the GPU's clock information for the core dump; the caller must be the system
    /// (kernel code is); NULL 3; 0 with the buffer's first word the clock register (no SoC: 0).</summary>
    [Hle(Nid = 0x990338F6, Library = "SceGpuEs4ForDriver")]
    int NID_990338F6(uint info, uint unused1, uint unused2)
    {
        if (info == 0)
        {
            return PvrInvalidParams;
        }

        GuestMemory.Fill(info, 0, 0x20);
        return 0;
    }

    /// <summary>NID_C0C73C1B(buffer): the processes of the GPU's memory contexts for the core dump (0x120 bytes: per
    /// context its pid and name), zeroed when there are none; NULL 3.</summary>
    [Hle(Nid = 0xC0C73C1B, Library = "SceGpuEs4ForDriver")]
    int NID_C0C73C1B(uint buffer)
    {
        if (buffer == 0)
        {
            return PvrInvalidParams;
        }

        GuestMemory.Fill(buffer, 0, 0x120);
        return 0;
    }
}
