using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibLocation (psp2/location.h). VitaSharp has no positioning hardware; the coherent state chosen is "location is
/// enabled in the system settings but this application is not allowed to use it" (a state a real user produces by
/// declining the confirmation dialog). Handles and their methods are tracked so the API behaves consistently; every
/// measurement fails with SCE_LOCATION_ERROR_DISABLE_APPLICATION after filling the output with
/// SCE_LOCATION_DATA_INVALID, and the confirmation dialog finishes at once with "disable".
/// </summary>
public sealed class Location : HleModule
{
    internal const int ERR_INVALID_ADDRESS = unchecked((int)0x80101204), ERR_INVALID_HANDLE = unchecked((int)0x80101205),
        ERR_INVALID_LOCATION_METHOD = unchecked((int)0x80101208), ERR_INVALID_HEADING_METHOD = unchecked((int)0x80101209),
        ERR_NOT_RUNNING_CALLBACK = unchecked((int)0x8010120B), ERR_DIALOG_RESULT_NONE = unchecked((int)0x8010120C),
        ERR_DISABLE_APPLICATION = unchecked((int)0x8010120D);

    /// <summary>SCE_LOCATION_DATA_INVALID.</summary>
    internal const double DataInvalid = -9999.0;
    const uint MaxLocationMethod = 5, MaxHeadingMethod = 4; // SCE_LOCATION_LMETHOD_GPS, SCE_LOCATION_HMETHOD_CAMERA
    internal const uint DialogIdle = 0, DialogFinished = 2; // SceLocationDialogStatus
    internal const uint ResultNone = 0, ResultDisable = 1;  // SceLocationDialogResult

    sealed class Handle
    {
        public uint LocateMethod, HeadingMethod;
        public uint ConfirmStatus = DialogIdle, ConfirmResult = ResultNone;
    }

    readonly object _lock = new();
    readonly Dictionary<uint, Handle> _handles = new();
    uint _nextHandle = 1;

    internal int OpenHandles { get { lock (_lock) return _handles.Count; } }

    Handle? Get(uint h) { lock (_lock) return _handles.GetValueOrDefault(h); }

    static int CheckMethods(uint locate, uint heading) =>
        locate > MaxLocationMethod ? ERR_INVALID_LOCATION_METHOD : heading > MaxHeadingMethod ? ERR_INVALID_HEADING_METHOD : 0;

    [Hle] int sceLocationInit() => 0;
    [Hle] int sceLocationTerm() { lock (_lock) _handles.Clear(); return 0; }
    [Hle] int sceLocationSetThreadParameter(uint threadParam) => 0;
    [Hle] int sceLocationDenyApplication() => 0; // the application is already denied

    [Hle]
    int sceLocationSetGpsEmulationFile(uint filename)
    {
        Log.Info($"sceLocationSetGpsEmulationFile({(filename != 0 ? GuestMemory.ReadCString(filename) : "NULL")}): ignored");
        return 0;
    }

    [Hle]
    int sceLocationOpen(Ptr<uint> handle, uint locateMethod, uint headingMethod)
    {
        if (handle.IsNull) return ERR_INVALID_ADDRESS;
        if (CheckMethods(locateMethod, headingMethod) is var err and not 0) return err;
        uint h;
        lock (_lock)
        {
            h = _nextHandle++;
            _handles[h] = new Handle { LocateMethod = locateMethod, HeadingMethod = headingMethod };
        }
        handle.Value = h;
        return 0;
    }

    [Hle] int sceLocationClose(uint handle) { lock (_lock) return _handles.Remove(handle) ? 0 : ERR_INVALID_HANDLE; }

    [Hle]
    int sceLocationReopen(uint handle, uint locateMethod, uint headingMethod)
    {
        var h = Get(handle);
        if (h == null) return ERR_INVALID_HANDLE;
        if (CheckMethods(locateMethod, headingMethod) is var err and not 0) return err;
        lock (_lock) { h.LocateMethod = locateMethod; h.HeadingMethod = headingMethod; }
        return 0;
    }

    [Hle]
    int sceLocationGetMethod(uint handle, Ptr<uint> locateMethod, Ptr<uint> headingMethod)
    {
        var h = Get(handle);
        if (h == null) return ERR_INVALID_HANDLE;
        if (locateMethod.IsNull || headingMethod.IsNull) return ERR_INVALID_ADDRESS;
        locateMethod.Value = h.LocateMethod;
        headingMethod.Value = h.HeadingMethod;
        return 0;
    }

    /// <summary>SceLocationLocationInfo (0x30) with every measurement invalid and the current RTC tick.</summary>
    static void WriteInvalidLocation(uint p)
    {
        GuestMemory.Write(p + 0x00, DataInvalid); // latitude
        GuestMemory.Write(p + 0x08, DataInvalid); // longitude
        GuestMemory.Write(p + 0x10, DataInvalid); // altitude
        GuestMemory.Write(p + 0x18, (float)DataInvalid); // accuracy
        GuestMemory.Write(p + 0x1C, 0f); // reserve
        GuestMemory.Write(p + 0x20, (float)DataInvalid); // direction
        GuestMemory.Write(p + 0x24, (float)DataInvalid); // speed
        GuestMemory.Write(p + 0x28, Rtc.NowTick);
    }

    [Hle] int sceLocationGetLocation(uint handle, uint locationInfo) => GetLocation(handle, locationInfo);

    /// <summary>The timeout (SceUInt64, inferred) is irrelevant: the request fails at once.</summary>
    [Hle] int sceLocationGetLocationWithTimeout(uint handle, uint locationInfo, ulong timeout) => GetLocation(handle, locationInfo);

    int GetLocation(uint handle, uint info)
    {
        if (Get(handle) == null) return ERR_INVALID_HANDLE;
        if (info == 0) return ERR_INVALID_ADDRESS;
        WriteInvalidLocation(info);
        return ERR_DISABLE_APPLICATION;
    }

    [Hle] int sceLocationCancelGetLocation(uint handle) => Get(handle) == null ? ERR_INVALID_HANDLE : 0;

    [Hle]
    int sceLocationStartLocationCallback(uint handle, uint distance, uint callback, uint userdata) =>
        Get(handle) == null ? ERR_INVALID_HANDLE : callback == 0 ? ERR_INVALID_ADDRESS : ERR_DISABLE_APPLICATION;

    [Hle] int sceLocationStopLocationCallback(uint handle) => Get(handle) == null ? ERR_INVALID_HANDLE : ERR_NOT_RUNNING_CALLBACK;

    [Hle]
    int sceLocationGetHeading(uint handle, uint headingInfo)
    {
        if (Get(handle) == null) return ERR_INVALID_HANDLE;
        if (headingInfo == 0) return ERR_INVALID_ADDRESS;
        // SceLocationHeadingInfo (0x20): trueHeading, headingVectorX/Y/Z, reserve, reserve2, timestamp
        for (uint o = 0; o < 0x10; o += 4) GuestMemory.Write(headingInfo + o, (float)DataInvalid);
        GuestMemory.W32(headingInfo + 0x10, 0);
        GuestMemory.W32(headingInfo + 0x14, 0);
        GuestMemory.Write(headingInfo + 0x18, Rtc.NowTick);
        return ERR_DISABLE_APPLICATION;
    }

    [Hle]
    int sceLocationStartHeadingCallback(uint handle, uint difference, uint callback, uint userdata) =>
        Get(handle) == null ? ERR_INVALID_HANDLE : callback == 0 ? ERR_INVALID_ADDRESS : ERR_DISABLE_APPLICATION;

    [Hle] int sceLocationStopHeadingCallback(uint handle) => Get(handle) == null ? ERR_INVALID_HANDLE : ERR_NOT_RUNNING_CALLBACK;

    /// <summary>The permission dialog "runs" and closes immediately with the user declining.</summary>
    [Hle]
    int sceLocationConfirm(uint handle)
    {
        var h = Get(handle);
        if (h == null) return ERR_INVALID_HANDLE;
        lock (_lock) { h.ConfirmStatus = DialogFinished; h.ConfirmResult = ResultDisable; }
        return 0;
    }

    [Hle]
    int sceLocationConfirmGetStatus(uint handle, Ptr<uint> status)
    {
        var h = Get(handle);
        if (h == null) return ERR_INVALID_HANDLE;
        if (status.IsNull) return ERR_INVALID_ADDRESS;
        status.Value = h.ConfirmStatus;
        return 0;
    }

    [Hle]
    int sceLocationConfirmGetResult(uint handle, Ptr<uint> result)
    {
        var h = Get(handle);
        if (h == null) return ERR_INVALID_HANDLE;
        if (result.IsNull) return ERR_INVALID_ADDRESS;
        if (h.ConfirmStatus != DialogFinished) return ERR_DIALOG_RESULT_NONE;
        result.Value = h.ConfirmResult;
        return 0;
    }

    [Hle] int sceLocationConfirmAbort(uint handle) => Get(handle) == null ? ERR_INVALID_HANDLE : 0;

    /// <summary>SceLocationPermissionInfo (0x14): parental ALLOW, system setting ALLOW, application DENY.</summary>
    [Hle]
    int sceLocationGetPermission(uint handle, uint info)
    {
        if (Get(handle) == null) return ERR_INVALID_HANDLE;
        if (info == 0) return ERR_INVALID_ADDRESS;
        GuestMemory.W32(info + 0x00, 1); // parentalstatus = SCE_LOCATION_PERMISSION_ALLOW
        GuestMemory.W32(info + 0x04, 1); // mainstatus = SCE_LOCATION_PERMISSION_ALLOW
        GuestMemory.W32(info + 0x08, 2); // applicationstatus = SCE_LOCATION_PERMISSION_APPLICATION_DENY
        GuestMemory.W32(info + 0x0C, 0);
        GuestMemory.W32(info + 0x10, 0);
        return 0;
    }
}

/// <summary>
/// SceGps: low-level GPS receiver interface (3G models only). VitaSharp emulates a Wi-Fi model, so there is no
/// device; the device structs have no known layout and are never written.
/// </summary>
public sealed class Gps : HleModule
{
    /// <summary>SCE_ERROR_ERRNO_ENODEV (the real "no receiver" code is unconfirmed).</summary>
    internal const int ERR_NO_DEVICE = unchecked((int)0x80010013);

    [Hle] int _sceGpsIsDevice() => 0;
    [Hle] int _sceGpsOpen(int cbid) => ERR_NO_DEVICE;
    [Hle] int _sceGpsClose() => ERR_NO_DEVICE;
    [Hle] int _sceGpsStart(uint mode) => ERR_NO_DEVICE;
    [Hle] int _sceGpsStop() => ERR_NO_DEVICE;
    [Hle] int _sceGpsGetData(uint pos, uint sat) => ERR_NO_DEVICE;
    [Hle] int _sceGpsGetDeviceInfo(uint devInfo) => ERR_NO_DEVICE;
    [Hle] int _sceGpsGetState(uint state) => ERR_NO_DEVICE;
    [Hle] int _sceGpsIoctl(uint command, uint arg, uint argSize, uint a4) => ERR_NO_DEVICE;
    [Hle] int _sceGpsSelectDevice(uint deviceType) => ERR_NO_DEVICE;
    [Hle] int _sceGpsResumeCallback() => 0;
}
