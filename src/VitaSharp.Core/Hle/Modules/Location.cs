using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibLocation (psp2/location.h). Where the unit's position comes from is the host's location source
/// (<see cref="Host.HostServices.Location"/>: a fixed position, or the host system's location service), off by default.
/// <para>
/// With no source the state is "location is enabled in the system settings but this application is not allowed to use
/// it" (a state a real user produces by declining the confirmation dialog): every measurement fails with
/// SCE_LOCATION_ERROR_DISABLE_APPLICATION after filling the output with SCE_LOCATION_DATA_INVALID, and the
/// confirmation dialog finishes at once with "disable".
/// </para>
/// <para>
/// With a source the state is the one a user who allowed the application produces (Settings &gt; Location Data): the
/// permission says ALLOW, the confirmation dialog finishes with "enable", and sceLocationGetLocation answers the
/// source's position with its accuracy and the RTC tick; while the source has no position yet (no fix, permission
/// refused on the phone), or while "Gravar tudo" withholds it (<see cref="Host.HostServices.LocationWithheld"/>), the
/// answer is SCE_LOCATION_INFO_UNDETERMINED_LOCATION with the output invalid, as a unit that cannot locate itself. The
/// emulated unit has no compass the service uses for a heading: the heading stays undetermined. The position is never
/// logged (it is where the player is).
/// </para>
/// </summary>
public sealed partial class Location : HleModule
{
    // FW 3.74's SceLibLocation (vs0:sys/external/liblocation.suprx) answers its own argument checks with 0x8010124x:
    // 0x80101240 a null pointer, 0x80101241 an unknown handle, 0x80101244/45 a bad location/heading method
    // (sceLocationOpen/Reopen), 0x80101248 a confirmation dialog without a result. Those are not the psp2/location.h
    // values (0x80101204...). What the location service answers (liblocation_provider, the 0x8010120x numbering of
    // psp2/location.h) passes through unchanged: a measurement the application may not make, a callback not running.
    internal const int ERR_INVALID_ADDRESS = unchecked((int)0x80101240);
    internal const int ERR_INVALID_HANDLE = unchecked((int)0x80101241);
    internal const int ERR_INVALID_LOCATION_METHOD = unchecked((int)0x80101244);
    internal const int ERR_INVALID_HEADING_METHOD = unchecked((int)0x80101245);
    internal const int ERR_DIALOG_RESULT_NONE = unchecked((int)0x80101248);
    internal const int ERR_NOT_RUNNING_CALLBACK = unchecked((int)0x8010120B);
    internal const int ERR_DISABLE_APPLICATION = unchecked((int)0x8010120D);
    /// <summary>SCE_LOCATION_INFO_UNDETERMINED_LOCATION: the service has no position (yet).</summary>
    internal const int INFO_UNDETERMINED_LOCATION = unchecked((int)0x80101200);
    /// <summary>SCE_LOCATION_ERROR_MULTIPLE_CALLBACK: a location callback is running on the handle already.</summary>
    internal const int ERR_MULTIPLE_CALLBACK = unchecked((int)0x8010120A);

    /// <summary>SCE_LOCATION_DATA_INVALID.</summary>
    internal const double DataInvalid = -9999.0;
    const uint MaxLocationMethod = 5, MaxHeadingMethod = 4; // SCE_LOCATION_LMETHOD_GPS, SCE_LOCATION_HMETHOD_CAMERA
    internal const uint DialogIdle = 0, DialogFinished = 2; // SceLocationDialogStatus
    internal const uint ResultNone = 0, ResultDisable = 1, ResultEnable = 2;  // SceLocationDialogResult
    /// <summary>SceLocationPermissionApplicationStatus DENY and ALLOW.</summary>
    internal const uint ApplicationDeny = 2, ApplicationAllow = 3;

    [GenerateSnapshot]
    sealed partial class Handle
    {
        public uint LocateMethod, HeadingMethod;
        public uint ConfirmStatus = DialogIdle, ConfirmResult = ResultNone;
    }

    readonly object _lock = new();
    [SnapshotMissing] // states of builds before it were refused while a handle was open: they had none
    readonly Dictionary<uint, Handle> _handles = new();
    uint _nextHandle = 1;

    internal int OpenHandles { get { lock (_lock) return _handles.Count; } }

    /// <summary>The handles whose location callback runs (sceLocationStartLocationCallback with a source).</summary>
    [SnapshotMissing] // states of builds before it: no callback ran
    readonly HashSet<uint> _locationCallbacks = new();

    /// <summary>The application may use the location: the host has a source (the user allowed it in the settings).</summary>
    bool Allowed => Emu?.Host.Location != null; // a module made without a process (tests) has no host

    /// <summary>The source's position now, or null (no source, no fix yet, withheld by a capture).</summary>
    Host.LocationFix? CurrentFix => Emu?.Host is not { } host || host.LocationWithheld ? null : host.Location?.Current;

    Handle? Get(uint h) { lock (_lock) return _handles.GetValueOrDefault(h); }

    static int CheckMethods(uint locate, uint heading) =>
        locate > MaxLocationMethod ? ERR_INVALID_LOCATION_METHOD : heading > MaxHeadingMethod ? ERR_INVALID_HEADING_METHOD : 0;

    [Hle] int sceLocationInit() => 0;
    [Hle]
    int sceLocationTerm()
    {
        lock (_lock)
        {
            _handles.Clear();
            _locationCallbacks.Clear();
        }

        return 0;
    }
    [Hle] int sceLocationSetThreadParameter(uint threadParam) => 0;
    [Hle] int sceLocationDenyApplication() => 0; // the application is denied without a source; with one, the user's setting allows it

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

    [Hle]
    int sceLocationClose(uint handle)
    {
        lock (_lock)
        {
            _locationCallbacks.Remove(handle);
            return _handles.Remove(handle) ? 0 : ERR_INVALID_HANDLE;
        }
    }

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

    /// <summary>sceLocationGetLocationWithTimeout(handle, timeout, locationInfo): FW 3.74's SceLibLocation takes the timeout
    /// (a 32-bit value) second and the output third, and passes them to the same request as sceLocationGetLocation, which
    /// uses a fixed timeout. The timeout is irrelevant here: the request fails at once. (Reading the second argument as
    /// the output wrote the 0x30-byte invalid location at the timeout's value, a low address, and left the caller's
    /// structure unwritten: Assassin's Creed III Liberation's geolocation thread passes 10.)</summary>
    [Hle] int sceLocationGetLocationWithTimeout(uint handle, uint timeout, uint locationInfo) => GetLocation(handle, locationInfo);

    int GetLocation(uint handle, uint info)
    {
        if (Get(handle) == null)
        {
            return ERR_INVALID_HANDLE;
        }

        if (info == 0)
        {
            return ERR_INVALID_ADDRESS;
        }

        if (!Allowed)
        {
            WriteInvalidLocation(info);
            return ERR_DISABLE_APPLICATION;
        }

        if (CurrentFix is not { } fix)
        {
            WriteInvalidLocation(info);
            return INFO_UNDETERMINED_LOCATION;
        }

        WriteLocation(info, fix);
        return 0;
    }

    /// <summary>SceLocationLocationInfo (0x30) of <paramref name="fix"/>: what the source does not know is
    /// SCE_LOCATION_DATA_INVALID; the timestamp is the current RTC tick.</summary>
    static void WriteLocation(uint p, Host.LocationFix fix)
    {
        GuestMemory.Write(p + 0x00, fix.Latitude);
        GuestMemory.Write(p + 0x08, fix.Longitude);
        GuestMemory.Write(p + 0x10, double.IsFinite(fix.Altitude) ? fix.Altitude : DataInvalid);
        GuestMemory.Write(p + 0x18, float.IsFinite(fix.Accuracy) ? fix.Accuracy : (float)DataInvalid);
        GuestMemory.Write(p + 0x1C, 0f);
        GuestMemory.Write(p + 0x20, float.IsFinite(fix.Direction) ? fix.Direction : (float)DataInvalid);
        GuestMemory.Write(p + 0x24, float.IsFinite(fix.Speed) ? fix.Speed : (float)DataInvalid);
        GuestMemory.Write(p + 0x28, Rtc.NowTick);
    }

    [Hle] int sceLocationCancelGetLocation(uint handle) => Get(handle) == null ? ERR_INVALID_HANDLE : 0;

    /// <summary>With a source the callback is accepted (one per handle) and runs until stopped; the service has not
    /// delivered a position through it yet (the application reads it with sceLocationGetLocation).</summary>
    [Hle]
    int sceLocationStartLocationCallback(uint handle, uint distance, uint callback, uint userdata)
    {
        if (Get(handle) == null)
        {
            return ERR_INVALID_HANDLE;
        }

        if (callback == 0)
        {
            return ERR_INVALID_ADDRESS;
        }

        if (!Allowed)
        {
            return ERR_DISABLE_APPLICATION;
        }

        lock (_lock)
        {
            return _locationCallbacks.Add(handle) ? 0 : ERR_MULTIPLE_CALLBACK;
        }
    }

    [Hle]
    int sceLocationStopLocationCallback(uint handle)
    {
        if (Get(handle) == null)
        {
            return ERR_INVALID_HANDLE;
        }

        lock (_lock)
        {
            return _locationCallbacks.Remove(handle) ? 0 : ERR_NOT_RUNNING_CALLBACK;
        }
    }

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
        return Allowed ? INFO_UNDETERMINED_LOCATION : ERR_DISABLE_APPLICATION;
    }

    /// <summary>sceLocationStartHeadingCallback(handle, float difference, callback, userdata): FW 3.74's SceLibLocation
    /// takes the difference (degrees, 0 through 180) as a float, so it travels in s0 and the callback and userdata in
    /// r1 and r2. A null callback is refused before the handle is looked up. (Reading four core registers took the
    /// userdata as the callback.)</summary>
    [Hle]
    int sceLocationStartHeadingCallback(uint handle, float difference, uint callback, uint userdata)
    {
        if (callback == 0)
        {
            return ERR_INVALID_ADDRESS;
        }

        if (Get(handle) == null)
        {
            return ERR_INVALID_HANDLE;
        }

        return ERR_DISABLE_APPLICATION;
    }

    [Hle] int sceLocationStopHeadingCallback(uint handle) => Get(handle) == null ? ERR_INVALID_HANDLE : ERR_NOT_RUNNING_CALLBACK;

    /// <summary>The permission dialog "runs" and closes at once: the user declined without a source, allowed with one
    /// (the emulator's location setting is the user's consent).</summary>
    [Hle]
    int sceLocationConfirm(uint handle)
    {
        var h = Get(handle);
        if (h == null) return ERR_INVALID_HANDLE;
        lock (_lock)
        {
            h.ConfirmStatus = DialogFinished;
            h.ConfirmResult = Allowed ? ResultEnable : ResultDisable;
        }
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
        if (h.ConfirmStatus != DialogFinished)
        {
            result.Value = ResultNone; // the firmware clears the output before answering "no result"
            return ERR_DIALOG_RESULT_NONE;
        }

        result.Value = h.ConfirmResult;
        return 0;
    }

    [Hle] int sceLocationConfirmAbort(uint handle) => Get(handle) == null ? ERR_INVALID_HANDLE : 0;

    /// <summary>SceLocationPermissionInfo (0x14): parental ALLOW, system setting ALLOW, application DENY (ALLOW with a
    /// source).</summary>
    [Hle]
    int sceLocationGetPermission(uint handle, uint info)
    {
        if (Get(handle) == null) return ERR_INVALID_HANDLE;
        if (info == 0) return ERR_INVALID_ADDRESS;
        GuestMemory.W32(info + 0x00, 1); // parentalstatus = SCE_LOCATION_PERMISSION_ALLOW
        GuestMemory.W32(info + 0x04, 1); // mainstatus = SCE_LOCATION_PERMISSION_ALLOW
        GuestMemory.W32(info + 0x08, Allowed ? ApplicationAllow : ApplicationDeny); // applicationstatus
        GuestMemory.W32(info + 0x0C, 0);
        GuestMemory.W32(info + 0x10, 0);
        return 0;
    }
}

/// <summary>
/// SceGps: low-level GPS receiver interface (3G models only). VitaSharp emulates a Wi-Fi model, so there is no
/// device; the device structs have no known layout and are never written.
/// </summary>
public sealed partial class Gps : HleModule
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
