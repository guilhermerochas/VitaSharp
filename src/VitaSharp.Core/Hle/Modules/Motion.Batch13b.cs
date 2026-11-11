namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The SceMotion sampling switches of the system's other motion clients (driver_us.suprx 3.74): besides
/// sceMotionStartSampling the library keeps one started flag per client (the location service's, NID_ACEFA121 /
/// NID_BC5C86C5, and a second internal one, NID_0D9FA7EA / NID_D88B731D) over a shared count that starts and stops
/// the motion device; starting a started client is 0x80360207, stopping a stopped one 0x80360206. The HLE's device
/// runs whenever it is asked, so only the per-client flags are kept.
/// </summary>
public sealed partial class Sensors
{
    bool _locationSampling, _internalSampling;

    [Hle("NID_ACEFA121", Nid = 0xACEFA121, Library = "SceMotion")]
    int MotionLocationStart() { if (_locationSampling) return ERR_ALREADY_SAMPLING; _locationSampling = true; return 0; }

    [Hle("NID_BC5C86C5", Nid = 0xBC5C86C5, Library = "SceMotion")]
    int MotionLocationStop() { if (!_locationSampling) return ERR_NOT_SAMPLING; _locationSampling = false; return 0; }

    [Hle("NID_0D9FA7EA", Nid = 0x0D9FA7EA, Library = "SceMotion")]
    int MotionInternalStart() { if (_internalSampling) return ERR_ALREADY_SAMPLING; _internalSampling = true; return 0; }

    [Hle("NID_D88B731D", Nid = 0xD88B731D, Library = "SceMotion")]
    int MotionInternalStop() { if (!_internalSampling) return ERR_NOT_SAMPLING; _internalSampling = false; return 0; }
}
