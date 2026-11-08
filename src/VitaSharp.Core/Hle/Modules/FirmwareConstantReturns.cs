namespace VitaSharp.Core.Hle.Modules;

// Functions whose FW 3.74 implementation (the kernel modules SceHid, SceBt, SceMotionDev, SceBbmc, SceAppMgr and SceNgs,
// libssl.suprx) is a constant return or a stored word: the same here.

public sealed partial class Hid
{
    const int ERR_NOT_SUPPORTED = unchecked((int)0x803B0004);

    /// <summary>Game controllers over USB/Bluetooth HID: not supported by the 3.74 SceHid at all.</summary>
    [Hle("sceHidControllerEnumerate")]
    [Hle("sceHidControllerRead")]
    [Hle("sceHidControllerRegisterEnumHintCallback")]
    [Hle("sceHidControllerRegisterReadHintCallback")]
    [Hle("sceHidControllerUnregisterEnumHintCallback")]
    [Hle("sceHidControllerUnregisterReadHintCallback")]
    int sceHidControllerUnsupported() => ERR_NOT_SUPPORTED;
}

public sealed partial class SystemServicesVsh
{
    /// <summary>Bluetooth HID reports and out-of-band pairing: refused by the 3.74 SceBt (0x802F090A).</summary>
    [Hle("sceBtHidTransfer")]
    [Hle("sceBtHidGetReportDescriptor")]
    [Hle("sceBtPairingOOB")]
    int sceBtUnsupported() => unchecked((int)0x802F090A);

    /// <summary>Object push (OPP) and the hands-free profile's phone calls: not available on the 3.74 SceBt.</summary>
    [Hle("sceBtPushOpp")]
    [Hle("sceBtRecvOpp")]
    int sceBtOppUnsupported() => unchecked((int)0x802F1008);
    [Hle("sceBtHfpRequest")]
    [Hle("sceBtHfpGetCurrentPhoneNumber")]
    int sceBtHfpUnsupported() => unchecked((int)0x802F1301);

    /// <summary>(int mode): content protection for Bluetooth audio, 1 or 2 kept, any other value clears it.</summary>
    [Hle]
    int sceBtSetContentProtection(int mode)
    {
        _btContentProtection = mode is 1 or 2 ? (byte)mode : (byte)0;
        return 0;
    }
    byte _btContentProtection;

    /// <summary>(a, b, c): three words SceAppMgr keeps for a settings workaround (bug 51800); always 0.</summary>
    [Hle]
    int sceAppMgrSetFakeSettingBug51800(uint a, uint b, uint c)
    {
        _fakeSettingBug51800 = [a, b, c];
        return 0;
    }
    uint[] _fakeSettingBug51800 = [0, 0, 0];

    /// <summary>Debug output of the modem controller: the retail SceBbmc does nothing.</summary>
    [Hle("sceBbmcDebugOutString")]
    [Hle("sceBbmcDebugSelectOutMedia")]
    int sceBbmcDebugNop() => 0;
}

public sealed partial class Sensors
{
    /// <summary>The "2"/"3" forms and the gyro feedback of SceMotionDev: no-ops returning 0 in the 3.74 driver.</summary>
    [Hle("sceMotionDevGetAccCalibData2")]
    [Hle("sceMotionDevGetGyroBias2")]
    [Hle("sceMotionDevGetGyroCalibData2")]
    [Hle("sceMotionDevGetDs3CalibData")]
    [Hle("sceMotionDevRead2")]
    [Hle("sceMotionDevRead3")]
    [Hle("sceMotionDevSamplingStart2")]
    [Hle("sceMotionDevSamplingStop2")]
    [Hle("sceMotionDevSetGyroFeedBack")]
    int sceMotionDevNop() => 0;
}

public sealed partial class Ssl
{
    /// <summary>0 once sceSslInit has run, else SCE_SSL_ERROR_BEFORE_INIT (libssl.suprx FW 3.74).</summary>
    [Hle] int sceSslInternalIsInitalized() => _inited ? 0 : ERR_BEFORE_INIT;
}

public sealed partial class Ngs
{
    /// <summary>Returns 0 (no preset) for any definition in the 3.74 SceNgs.</summary>
    [Hle] uint sceNgsVoiceDefinitionGetPresetInternal(uint definition, uint index) => 0;

    /// <summary>(rack): the system handle the rack was created in, 0 for an unknown rack (SceNgs FW 3.74).</summary>
    [Hle] uint sceNgsSystemGetSysHandleFromRack(uint rack) => _racks.TryGetValue(rack, out var r) ? r.System.Handle : 0;

    /// <summary>(system): 1 when the program was built with SDK 3.55 or later (the system keeps the SDK version it was
    /// initialised with; libngs switches its rack release and update paths on it), 0 for older ones or an unknown handle.</summary>
    [Hle]
    int sceNgsSystemIsFixForBugzilla89940(uint system)
    {
        if (!_systems.ContainsKey(system)) return 0;
        uint pp = Emu.Linker.ProcessParam;
        uint sdk = pp == 0 || Memory.GuestMemory.R32(pp + 4) != 0x32505350 ? SystemMisc.SystemSwVersion : Memory.GuestMemory.R32(pp + 0xC);
        return sdk > 0x0354FFFF ? 1 : 0;
    }
}
