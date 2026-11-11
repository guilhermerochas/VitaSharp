namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibLocationExtension (liblocation_extension.suprx 3.74): the location of the places the unit's Wi-Fi history
/// remembers. The module checks the output pointer (0x80101240), resolves the SceLibLocation handle (0x80101241 when it
/// is not open) and asks the location service (requests 0x12340015 get / 0x12340016 query / 0x12340018 cancel). With
/// the emulator's location state (enabled, this application not allowed: see <see cref="Location"/>) the service
/// answers SCE_LOCATION_ERROR_DISABLE_APPLICATION, as for every other measurement; the cancel succeeds.
/// </summary>
public sealed partial class Location
{
    const string ExtLib = "SceLibLocationExtension";
    const int ERR_EXT_INVALID_PARAM = unchecked((int)0x80101240), ERR_EXT_INVALID_HANDLE = unchecked((int)0x80101241);

    /// <summary>sceLocationGetLocationWifiHistory(handle, history param, info*).</summary>
    [Hle("sceLocationGetLocationWifiHistory", Library = ExtLib)]
    int GetLocationWifiHistory(uint handle, uint param, uint info) =>
        info == 0 ? ERR_EXT_INVALID_PARAM : Get(handle) == null ? ERR_EXT_INVALID_HANDLE : ERR_DISABLE_APPLICATION;

    /// <summary>sceLocationQueryLocationWifiHistory(handle, ..., out*): the asynchronous form (sixth argument the output).</summary>
    [Hle("sceLocationQueryLocationWifiHistory", Library = ExtLib)]
    int QueryLocationWifiHistory(HleContext c)
    {
        uint handle = c.Gpr(0), output = c.Gpr(5);
        return output == 0 ? ERR_EXT_INVALID_PARAM : Get(handle) == null ? ERR_EXT_INVALID_HANDLE : ERR_DISABLE_APPLICATION;
    }

    [Hle("sceLocationCancelQueryLocationWifiHistory", Library = ExtLib)]
    int CancelQueryLocationWifiHistory(uint handle) => Get(handle) == null ? ERR_EXT_INVALID_HANDLE : 0;
}
