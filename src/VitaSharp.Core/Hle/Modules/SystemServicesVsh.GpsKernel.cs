using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceGpsForDriver (os0:kd/bootimage.skprx SceGps 3.74, read in the module and its data, study only): the interface a
// GPS driver (the 3G modem's, SceBbmc) registers with. The module keeps up to four drivers by type (1..4), the
// primary one (the lowest type registered), and a state in its context that the SceGpsd thread drives: 1 once the
// thread runs with no driver, 2 with a driver; only the user's start of a GPS session (sceGps open/start) takes it
// further (3..8). Each driver entry answers from a per-state table in the module's data: registering is accepted in
// states 1..2 (and moves 1 to 2), the position data, the extra data and the almanac entries only from state 5 on
// (0x80390002 in state 1, 0x80390004 in state 2), unregistering from state 2 (0x80390002 before). The emulated unit
// has no GPS hardware and nothing starts a GPS session in the kernel, so a driver that registers stays in state 2,
// where the module's tables refuse its data.
public sealed partial class SystemServicesVsh
{
    const int GpsDriverTypes = 4;

    /// <summary>SceGps: a NULL descriptor or pointer, a type out of 1..4, a type that is not the primary (0x80390020).</summary>
    const int GPS_ERR_INVALID_ARG = unchecked((int)0x80390020);

    /// <summary>SceGps: no driver of the type registered; also the per-state answer of state 1 (0x80390002).</summary>
    const int GPS_ERR_NO_DRIVER = unchecked((int)0x80390002);

    /// <summary>SceGps: the driver's type is registered already (0x80390003).</summary>
    const int GPS_ERR_REGISTERED = unchecked((int)0x80390003);

    /// <summary>SceGps: the per-state answer of state 2, a driver and no session (0x80390004).</summary>
    const int GPS_ERR_NOT_STARTED = unchecked((int)0x80390004);

    /// <summary>SceGps: the driver buffer to free does not exist (0x8039000B).</summary>
    const int GPS_ERR_NO_BUFFER = unchecked((int)0x8039000B);

    /// <summary>The module's states the emulated unit reaches: the SceGpsd thread with no driver, with one.</summary>
    const int GpsStateNoDriver = 1, GpsStateDriver = 2;

    [SnapshotMissing] // states of builds before it: no GPS driver registered
    readonly uint[] _gpsDrivers = new uint[GpsDriverTypes * 6];

    [SnapshotMissing]
    int _gpsPrimary;

    /// <summary>The module's state: 2 while a driver is registered, else 1.</summary>
    int GpsState
    {
        get
        {
            for (int slot = 0; slot < GpsDriverTypes; slot++)
            {
                if (_gpsDrivers[slot * 6] != 0)
                {
                    return GpsStateDriver;
                }
            }

            return GpsStateNoDriver;
        }
    }

    /// <summary>The per-state answer of the data entries (the module's table at 0x81004104), for states 1 and 2.</summary>
    int GpsDataAnswer => GpsState == GpsStateNoDriver ? GPS_ERR_NO_DRIVER : GPS_ERR_NOT_STARTED;

    /// <summary>The primary driver type and the descriptor words of a type, for the tests.</summary>
    internal (int Primary, int State) GpsKernelValues => (_gpsPrimary, GpsState);

    /// <summary>NID_884A5867 (SceGpsForDriver)(const SceGpsDriver *driver): registers the driver of type driver->type
    /// (1..4; the 3rd and 4th words, its functions, not NULL); the first registered becomes the primary.</summary>
    [Hle("NID_884A5867", Nid = 0x884A5867, Library = "SceGpsForDriver")]
    int GpsRegisterDriver(uint driver)
    {
        if (driver == 0)
        {
            return GPS_ERR_INVALID_ARG;
        }

        uint type = GuestMemory.R32(driver);
        if (GuestMemory.R32(driver + 8) == 0 || GuestMemory.R32(driver + 12) == 0 || type - 1 >= GpsDriverTypes)
        {
            return GPS_ERR_INVALID_ARG;
        }

        int slot = (int)type - 1;
        if (_gpsDrivers[slot * 6] != 0)
        {
            return GPS_ERR_REGISTERED;
        }

        for (int word = 0; word < 6; word++)
        {
            _gpsDrivers[slot * 6 + word] = GuestMemory.R32(driver + (uint)word * 4);
        }

        if (_gpsPrimary == 0)
        {
            _gpsPrimary = (int)type;
        }

        return 0; // states 1 and 2 accept; state 1 moves to 2 (GpsState)
    }

    /// <summary>NID_7F001760 (SceGpsForDriver)(int type): unregisters the driver; the primary becomes the lowest type
    /// left (none: 0, and the state goes back to 1).</summary>
    [Hle("NID_7F001760", Nid = 0x7F001760, Library = "SceGpsForDriver")]
    int GpsUnregisterDriver(int type)
    {
        if ((uint)(type - 1) >= GpsDriverTypes)
        {
            return GPS_ERR_INVALID_ARG;
        }

        int slot = type - 1;
        if (_gpsDrivers[slot * 6] == 0)
        {
            return GPS_ERR_NO_DRIVER;
        }

        Array.Clear(_gpsDrivers, slot * 6, 6);
        _gpsPrimary = 0;
        for (int other = 0; other < GpsDriverTypes; other++)
        {
            if (_gpsDrivers[other * 6] != 0)
            {
                _gpsPrimary = other + 1;
                break;
            }
        }

        return 0;
    }

    /// <summary>NID_54DBA451 (SceGpsForDriver)(int type): 0 when a driver of the type is registered.</summary>
    [Hle("NID_54DBA451", Nid = 0x54DBA451, Library = "SceGpsForDriver")]
    int GpsIsDriverRegistered(int type)
    {
        if ((uint)(type - 1) >= GpsDriverTypes)
        {
            return GPS_ERR_INVALID_ARG;
        }

        return _gpsDrivers[(type - 1) * 6] == 0 ? GPS_ERR_NO_DRIVER : 0;
    }

    /// <summary>NID_BE3EB4E9 (SceGpsForDriver)(int type, unsigned flags, const void *position): the primary driver's
    /// position fix (flags 0..1 in the low nibble); taken from state 5 on.</summary>
    [Hle("NID_BE3EB4E9", Nid = 0xBE3EB4E9, Library = "SceGpsForDriver")]
    int GpsSubmitPosition(int type, uint flags, uint position)
    {
        if (position == 0 || type != _gpsPrimary || (flags & 0xF) > 1)
        {
            return GPS_ERR_INVALID_ARG;
        }

        return GpsDataAnswer;
    }

    /// <summary>NID_8D5688DA (SceGpsForDriver)(int type, const void *data): the primary driver's 0x104-byte satellite
    /// data; taken from state 5 on.</summary>
    [Hle("NID_8D5688DA", Nid = 0x8D5688DA, Library = "SceGpsForDriver")]
    int GpsSubmitSatellites(int type, uint data)
    {
        if (data == 0 || type != _gpsPrimary)
        {
            return GPS_ERR_INVALID_ARG;
        }

        return GpsDataAnswer;
    }

    /// <summary>NID_092AD9BF (SceGpsForDriver)(int type, const void *data, unsigned size): the primary driver's
    /// assistance data; taken from state 5 on.</summary>
    [Hle("NID_092AD9BF", Nid = 0x092AD9BF, Library = "SceGpsForDriver")]
    int GpsSubmitAssistance(int type, uint data, uint size)
    {
        if (data == 0 || type != _gpsPrimary)
        {
            return GPS_ERR_INVALID_ARG;
        }

        return GpsDataAnswer;
    }

    /// <summary>NID_F2049C02 (SceGpsForDriver)(unsigned source, unsigned event): a driver's event. From source 0 the
    /// power (0x10000) and the mode (0x80000) events are kept for the SceGpsd thread, and 0x60000 frees the driver
    /// buffer (none: 0x8039000B); the others, from sources 0 and 0x10000000..0x40000000, go to the user's GPS session,
    /// none here (0). Other sources are ignored (0).</summary>
    [Hle("NID_F2049C02", Nid = 0xF2049C02, Library = "SceGpsForDriver")]
    int GpsDriverEvent(uint source, uint gpsEvent)
    {
        uint kind = gpsEvent & 0xFFFF0000;
        if (source == 0 && kind == 0x60000)
        {
            return GPS_ERR_NO_BUFFER;
        }

        return 0;
    }
}
