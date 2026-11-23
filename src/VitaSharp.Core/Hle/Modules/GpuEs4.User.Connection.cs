using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceGpuEs4User (os0:us/libgpu_es4.suprx 3.74, the SGX services client libgxm_es4 imports; read in its code, study
// only): the connection to the services and the bridge-call wrappers. Each wrapper checks its arguments
// (PVRSRV_ERROR_INVALID_PARAMS = 3), builds its in buffer and runs one bridge call (FUN_810015cc: a PVRSRV_BRIDGE_PACKAGE
// for the SceGpuEs4ForUser syscall with hKernelServices from the services record's +4, retried while the driver answers
// 0x18); a refused call is PVRSRV_ERROR_BRIDGE_CALL_FAILED (0x22), else the handler's eError, the outputs copied only
// on PVRSRV_OK. Three handles lead to the services record: the connection (its +0 points at the record), the device
// data (its +0 is the connection, +4 the device cookie) and, for a few calls, a pointer to the record pointer. The
// driver serves only CONNECT/DISCONNECT_SERVICES before the SGX init server has run on the device, which the emulator
// does not have: every other call is refused (the "default" part).
public sealed partial class GpuEs4
{
    /// <summary>The PVRSRVConnect connection { services record, ..., +0x24 flags } (libgpu's .data, 0x28 bytes) and the
    /// services record { 0, hKernelServices } it points at; 0 until the first connect.</summary>
    uint _userConnection;

    uint _userServices;

    /// <summary>libgpu's "connected" flag: set by a connect that reached the services, cleared by the disconnect.</summary>
    bool _userConnected;

    /// <summary>One bridge call on a services record (its +4 the hKernelServices): 0 = the driver ran it, else
    /// <see cref="PvrBridgeCallFailed"/>.</summary>
    int ServicesBridgeCall(uint services, uint bridgeIndex, uint inSize, uint outSize, out uint error, out uint second)
    {
        uint handle = services != 0 && GuestMemory.IsMapped(services + 4) ? GuestMemory.R32(services + 4) : 0;
        int result = BridgeDispatch(BridgeIdBase + bridgeIndex, inSize, true, outSize, handle, out error, out second);
        if (result != 0)
        {
            Log.InfoOnce($"pvrsrv-user-{bridgeIndex:X}", $"SceGpuEs4User: bridge call 0x{BridgeIdBase + bridgeIndex:X4} refused (no SGX device): PVRSRV_ERROR_BRIDGE_CALL_FAILED");
            return PvrBridgeCallFailed;
        }

        return 0;
    }

    /// <summary>A call's result when it has no output to copy: the bridge's eError, or PVRSRV_ERROR_BRIDGE_CALL_FAILED.</summary>
    int ServicesBridgeResult(uint services, uint bridgeIndex, uint inSize, uint outSize)
    {
        return ServicesBridgeCall(services, bridgeIndex, inSize, outSize, out uint error, out _) != 0 ? PvrBridgeCallFailed : (int)error;
    }

    /// <summary>The services record behind a connection (its +0).</summary>
    static uint ServicesOfConnection(uint connection)
    {
        return connection != 0 && GuestMemory.IsMapped(connection) ? GuestMemory.R32(connection) : 0;
    }

    /// <summary>The services record behind device data (its +0 is the connection).</summary>
    static uint ServicesOfDevice(uint device)
    {
        return ServicesOfConnection(ServicesOfConnection(device));
    }

    /// <summary>
    /// NID_228252A2 PVRSRVConnect(connection **out, uint flags): NULL out 4, already connected 3. The connection is
    /// libgpu's own 0x28-byte record: zeroed, then PVRSRVOpen and CONNECT_SERVICES { flags } fill its services record
    /// (a failure there is 4, still unconnected); then the library counts as connected, keeps the flags (+0x24) and
    /// lists the devices (bridge 0x1000), which the driver refuses before its init server ran: 0x22.
    /// </summary>
    [Hle("NID_228252A2", Nid = 0x228252A2, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserConnect(uint output, uint flags)
    {
        if (output == 0)
        {
            return 4;
        }

        lock (_lock)
        {
            if (_userConnected)
            {
                return PvrInvalidParams;
            }

            if (_userConnection == 0)
            {
                _userConnection = Emu.Memory.Alloc(0x28, "libgpu_es4 connection", MemoryRegion.User, 8);
                _userServices = Emu.Memory.Alloc(8, "libgpu_es4 services", MemoryRegion.User, 8);
            }
        }

        GuestMemory.W32(output, _userConnection);
        GuestMemory.Fill(_userConnection, 0, 0x28);
        PVRSRVOpenThroughEntry();
        if (BridgeDispatch(BridgeIdBase + BridgeConnectServices, 8, true, 8, 0, out _, out uint services) != 0)
        {
            return 4;
        }

        GuestMemory.W32(_userServices, 0);
        GuestMemory.W32(_userServices + 4, services);
        GuestMemory.W32(_userConnection, _userServices);
        lock (_lock)
        {
            _userConnected = true;
        }

        GuestMemory.W32(_userConnection + 0x24, flags);
        return ServicesBridgeResult(_userServices, 0x00, 0, 0x1C);
    }

    /// <summary>NID_A5B14369 PVRSRVDisconnect(connection *connection): NULL or not connected 3; DISCONNECT_SERVICES on
    /// its services record and PVRSRVRelease, then not connected: 0.</summary>
    [Hle("NID_A5B14369", Nid = 0xA5B14369, Library = "SceGpuEs4User")]
    int GpuEs4UserDisconnect(uint connection)
    {
        uint services = ServicesOfConnection(connection);
        lock (_lock)
        {
            if (connection == 0 || !_userConnected || services == 0)
            {
                return PvrInvalidParams;
            }
        }

        BridgeDispatch(BridgeIdBase + BridgeDisconnectServices, 0, true, 8, GuestMemory.R32(services + 4), out _, out _);
        PVRSRVReleaseThroughEntry();
        lock (_lock)
        {
            _userConnected = false;
        }

        return 0;
    }

    /// <summary>NID_4447D217 PVRSRVEnumerateDevices(connection, uint *count, device ids[5]): both outputs needed (3);
    /// bridge 0x1000.</summary>
    [Hle("NID_4447D217", Nid = 0x4447D217, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserEnumerateDevices(uint connection, uint count, uint devices)
    {
        if (count == 0 || devices == 0)
        {
            return PvrInvalidParams;
        }

        return ServicesBridgeResult(ServicesOfConnection(connection), 0x00, 0, 0x1C);
    }

    /// <summary>NID_70800B46 PVRSRVAcquireDeviceData(connection, index, device *out, type): bridge 0x1001 { index, type };
    /// on PVRSRV_OK out = { connection, the device cookie }.</summary>
    [Hle("NID_70800B46", Nid = 0x70800B46, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserAcquireDeviceData(uint connection, uint index, uint output, uint type)
    {
        if (ServicesBridgeCall(ServicesOfConnection(connection), 0x01, 0xC, 8, out uint error, out uint cookie) != 0)
        {
            return PvrBridgeCallFailed;
        }

        if (error == 0)
        {
            GuestMemory.W32(output, connection);
            GuestMemory.W32(output + 4, cookie);
        }

        return (int)error;
    }

    /// <summary>NID_5C3A6DFE (uint value, uint timeoutUs): bridge 0x1057 on the PVRSRVConnect connection; a refused call
    /// is 0x804C0000 (SceGpuEs4's invalid error), an eError 0x804C0006 waits on the event flag the driver names.</summary>
    [Hle("NID_5C3A6DFE", Nid = 0x5C3A6DFE, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserWaitEvent(uint value, uint timeoutUs)
    {
        uint services = _userConnection != 0 ? ServicesOfConnection(_userConnection) : 0;
        if (ServicesBridgeCall(services, 0x57, 8, 0xC, out uint error, out _) != 0)
        {
            return ERR_INVALID;
        }

        return (int)error;
    }

    /// <summary>NID_0D27286F (services **record, value, const uint *words, int count, value2, uint *out1, uint *out2):
    /// a NULL record, services or words 3; bridge 0x104F (count words copied in).</summary>
    [Hle("NID_0D27286F", Nid = 0x0D27286F, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User0D27286F(uint record, uint value, uint words, int count, uint value2, uint output1, uint output2)
    {
        uint services = ServicesOfConnection(record);
        if (record == 0 || services == 0 || words == 0)
        {
            return PvrInvalidParams;
        }

        return ServicesBridgeResult(services, 0x4F, 0x20, 0xC);
    }

    /// <summary>NID_116449D2 (services **record, value): bridge 0x1050 { value }; a NULL record or services 3.</summary>
    [Hle("NID_116449D2", Nid = 0x116449D2, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User116449D2(uint record, uint value)
    {
        uint services = ServicesOfConnection(record);
        return record == 0 || services == 0 ? PvrInvalidParams : ServicesBridgeResult(services, 0x50, 8, 8);
    }

    /// <summary>NID_416C8813 (services **record, uint *out): bridge 0x104D, its word to out on PVRSRV_OK; NULLs 3.</summary>
    [Hle("NID_416C8813", Nid = 0x416C8813, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User416C8813(uint record, uint output)
    {
        uint services = ServicesOfConnection(record);
        if (record == 0 || services == 0 || output == 0)
        {
            return PvrInvalidParams;
        }

        return BridgeWithWord(services, 0x4D, 0, output);
    }

    /// <summary>NID_7DCCBCAF (services **record, value): bridge 0x104E { value }; a NULL record or services 3.</summary>
    [Hle("NID_7DCCBCAF", Nid = 0x7DCCBCAF, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User7DCCBCAF(uint record, uint value)
    {
        uint services = ServicesOfConnection(record);
        return record == 0 || services == 0 ? PvrInvalidParams : ServicesBridgeResult(services, 0x4E, 8, 8);
    }

    /// <summary>NID_73E945A1 (connection, info *info): a NULL info 3, an info whose first word is 0 is 0 without a call;
    /// else bridge 0x102A exchanges the 0x98-byte info.</summary>
    [Hle("NID_73E945A1", Nid = 0x73E945A1, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User73E945A1(uint connection, uint info)
    {
        if (info == 0)
        {
            return PvrInvalidParams;
        }

        if (GuestMemory.R32(info) == 0)
        {
            return 0;
        }

        return ServicesBridgeResult(ServicesOfConnection(connection), 0x2A, 0x9C, 0x9C);
    }

    /// <summary>NID_0162A405 (device, const block[0x44], uint *out): bridge 0x1086, its word to out on PVRSRV_OK.</summary>
    [Hle("NID_0162A405", Nid = 0x0162A405, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User0162A405(uint device, uint block, uint output)
    {
        return BridgeWithWord(ServicesOfDevice(device), 0x86, 0x4C, output);
    }

    /// <summary>NID_071E62F0 (device, heap, uint flags, size, alignment, value, memory **out): NULL device or out 3,
    /// flags without bit 3 0x1D; bridge 0x1006 (the allocation), whose record libgpu copies into a 0x1C-byte block of
    /// its heap.</summary>
    [Hle("NID_071E62F0", Nid = 0x071E62F0, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User071E62F0(uint device, uint heap, uint flags, uint size, uint alignment, uint value, uint output)
    {
        if (output == 0 || device == 0)
        {
            return PvrInvalidParams;
        }

        if ((flags & 8) == 0)
        {
            return 0x1D;
        }

        return ServicesBridgeResult(ServicesOfDevice(device), 0x06, 0x1C, 0x24);
    }

    /// <summary>NID_0BFE1685 (device, value, -, value2): bridge 0x1085.</summary>
    [Hle("NID_0BFE1685", Nid = 0x0BFE1685, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User0BFE1685(uint device, uint value, uint unused, uint value2)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x85, 0xC, 8);
    }

    /// <summary>NID_0EA7458D (device, value, uint *inOut, value2): NULLs or value 0 3; bridge 0x1058, the word back to
    /// inOut on PVRSRV_OK.</summary>
    [Hle("NID_0EA7458D", Nid = 0x0EA7458D, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User0EA7458D(uint device, uint value, uint inOut, uint value2)
    {
        if (value == 0 || device == 0 || inOut == 0)
        {
            return PvrInvalidParams;
        }

        return BridgeWithWord(ServicesOfDevice(device), 0x58, 0x10, inOut);
    }

    /// <summary>NID_156A6B70 (device, value): NULL device or value 0 3; bridge 0x1059.</summary>
    [Hle("NID_156A6B70", Nid = 0x156A6B70, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User156A6B70(uint device, uint value)
    {
        return value == 0 || device == 0 ? PvrInvalidParams : ServicesBridgeResult(ServicesOfDevice(device), 0x59, 0x10, 8);
    }

    /// <summary>NID_1E0A9C4A (device, uint *out, int *count, entries): any NULL 3; bridge 0x1003 (up to 0x30C bytes
    /// back: a word, the count and count 0x18-byte entries).</summary>
    [Hle("NID_1E0A9C4A", Nid = 0x1E0A9C4A, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User1E0A9C4A(uint device, uint output, uint count, uint entries)
    {
        if (output == 0 || device == 0 || entries == 0 || count == 0)
        {
            return PvrInvalidParams;
        }

        return ServicesBridgeResult(ServicesOfDevice(device), 0x03, 8, 0x30C);
    }

    /// <summary>NID_5273D097 (device, const block *block): NULL device, block or connection 3; bridge 0x105F (the
    /// block's word and 0x98 bytes).</summary>
    [Hle("NID_5273D097", Nid = 0x5273D097, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User5273D097(uint device, uint block)
    {
        if (block == 0 || device == 0 || ServicesOfConnection(device) == 0)
        {
            return PvrInvalidParams;
        }

        return ServicesBridgeResult(ServicesOfDevice(device), 0x5F, 0x108, 8);
    }

    /// <summary>NID_5564C1BE (const uint *words, uint count, device, value, a, b, c, const uint *extra): count above
    /// 0x1A is 3; bridge 0x108F (the words, a, b, c and the optional three extra words).</summary>
    [Hle("NID_5564C1BE", Nid = 0x5564C1BE, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User5564C1BE(uint words, uint count, uint device, uint value, uint first, uint second, uint third, uint extra)
    {
        return count > 0x1A ? PvrInvalidParams : ServicesBridgeResult(ServicesOfDevice(device), 0x8F, 0x94, 8);
    }

    /// <summary>NID_56893E98 (device, a, b, c, d, value, e, uint *out): NULL device or value 0 3; bridge 0x101D, its word
    /// to out (when given) whatever the eError.</summary>
    [Hle("NID_56893E98", Nid = 0x56893E98, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User56893E98(uint device, uint first, uint second, uint third, uint fourth, uint value, uint fifth, uint output)
    {
        if (value == 0 || device == 0)
        {
            return PvrInvalidParams;
        }

        if (ServicesBridgeCall(ServicesOfDevice(device), 0x1D, 0x20, 8, out uint error, out uint word) != 0)
        {
            return PvrBridgeCallFailed;
        }

        if (output != 0)
        {
            GuestMemory.W32(output, word);
        }

        return (int)error;
    }

    /// <summary>NID_7E965369 (device, value): bridge 0x1090.</summary>
    [Hle("NID_7E965369", Nid = 0x7E965369, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User7E965369(uint device, uint value)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x90, 8, 8);
    }

    /// <summary>A bridge call whose handler's first word goes to <paramref name="output"/> on PVRSRV_OK.</summary>
    int BridgeWithWord(uint services, uint bridgeIndex, uint inSize, uint output)
    {
        if (ServicesBridgeCall(services, bridgeIndex, inSize, 8, out uint error, out uint word) != 0)
        {
            return PvrBridgeCallFailed;
        }

        if (error == 0)
        {
            GuestMemory.W32(output, word);
        }

        return (int)error;
    }
}
