using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceAVConfig (os0:kd/bootimage.skprx, FW 3.74): the audio/video settings the shell changes. Every entry is for system
// programs only (0x80010058 otherwise). The HDMI calls and the master volume belong to the PS Vita TV: on a handheld
// (ksceSblAimgrIsDolce = 0) they answer 0x803A0109. A failed copy out is 0x803A0102. The volume keys, mute and output
// changes are posted to the module's event flag for its worker; here they change the state the getters report.
public sealed partial class VshBridge
{
    const int AVC_ERR_NOT_DOLCE = unchecked((int)0x803A0109), AVC_ERR_COPY = unchecked((int)0x803A0102),
        AVC_ERR_PERM = unchecked((int)0x80010058), AVC_ERR_RANGE = unchecked((int)0x803A0103);

    int _shutterVol = 1, _btVol, _btVolChanged, _brightness = 0x10000, _autoSuspend, _dimming, _connectedDevice;
    int _lastVolKey, _soundOutput, _muteRequests;

    bool AvcAllowed => Emu.IsSystemProgram;

    /// <summary>(int *device): the audio devices plugged in (bits: 4 headphones, 2 the dock, 0x400 a microphone, 0x100
    /// the handheld speaker); the emulated unit plays through its own speaker: 0x100.</summary>
    [Hle]
    int sceAVConfigGetConnectedAudioDevice(uint device)
    {
        if (device == 0) return AVC_ERR_COPY;
        GuestMemory.W32(device, (uint)(_connectedDevice | 0x100));
        return 0;
    }

    // ---- HDMI (PS TV only)
    [Hle] int sceAVConfigHdmiSetOutScalingRatio(uint ratio) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigHdmiGetMonitorInfo(uint info) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigHdmiSetResolution(int resolution, int mode) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigHdmiSetRgbRangeMode(int mode, int a) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigHdmiCecCmdOneTouchPlay(int a) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigHdmiCecDisable() => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigHdmiCecEnable() => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigHdmiClearCecInfo() => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigHdmiGetCecInfo(uint info) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;

    // ---- the PS TV master volume (handheld: 0x803A0109)
    [Hle] int sceAVConfigGetMasterVol(uint vol) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigSetMasterVol(int vol) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
    [Hle] int sceAVConfigWriteMasterVol() => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;

    // ---- volumes
    /// <summary>The camera shutter sound volume the module keeps (+0x13C; no permission check, plain read).</summary>
    [Hle] int sceAVConfigGetShutterVol() => _shutterVol;

    /// <summary>(int *vol, int *changed): the Bluetooth headset volume and whether it changed since the last read
    /// (the module clears that flag as it reads it).</summary>
    [Hle]
    int sceAVConfigGetBtVol(uint vol, uint changed)
    {
        if (!AvcAllowed) return AVC_ERR_PERM;
        int v = _btVol, c = _btVolChanged;
        _btVolChanged = 0;
        if (vol == 0) return AVC_ERR_COPY;
        GuestMemory.W32(vol, (uint)v);
        if (changed == 0) return AVC_ERR_COPY;
        GuestMemory.W32(changed, (uint)c);
        return 0;
    }

    /// <summary>(int volume, ...): the system volume 0..30 (the shell's volume bar); kept for GetSystemVol.</summary>
    [Hle]
    int sceAVConfigSetSystemVol(int volume, int a1, int a2)
    {
        if (!AvcAllowed) return AVC_ERR_PERM;
        if ((uint)volume > 30) return unchecked((int)0x803A0105);
        _systemVolume = volume;
        return 0;
    }
    int _systemVolume = -1;

    /// <summary>(int volume): stores the system volume in the registry (/CONFIG/SOUND/main_volume) so it survives a
    /// restart.</summary>
    [Hle]
    int sceAVConfigWriteRegSystemVol(int volume)
    {
        if (!AvcAllowed) return AVC_ERR_PERM;
        Firmware.Registry.For(Emu.Options.FsRoot).SetInt("/CONFIG/SOUND", "main_volume", volume);
        return 0;
    }

    /// <summary>(int key): a volume key press from the shell (up/down/mute): the module's worker steps the volume.</summary>
    [Hle]
    int sceAVConfigSendVolKey(int key)
    {
        if (!AvcAllowed) return AVC_ERR_PERM;
        _lastVolKey = key;
        int v = CurrentSystemVolume();
        if ((key & 1) != 0 && v < 30) _systemVolume = v + 1;
        if ((key & 2) != 0 && v > 0) _systemVolume = v - 1;
        return 0;
    }

    /// <summary>Mutes the output (the PS button + volume, or a call): event 4 to the worker; counted.</summary>
    [Hle] int sceAVConfigMuteOn() { if (!AvcAllowed) return AVC_ERR_PERM; _muteRequests++; return 0; }

    /// <summary>(int what, int value): a registry setting changed that the module follows (0: the sound output
    /// 0..1, 1/2/4: speaker mute and outputs 0..1, 3: a delay up to 0x119BF us); out of range 0x80010016.</summary>
    [Hle]
    int sceAVConfigChangeReg(int what, int value)
    {
        if (!AvcAllowed) return AVC_ERR_PERM;
        if (what is 0 or 1 or 2 or 4 && (uint)value > 1) return unchecked((int)0x80010016);
        if (what is < 0 or > 4) return unchecked((int)0x80010016);
        if (what == 0) _soundOutput = value;
        return 0;
    }

    /// <summary>(int callback): drops the callback sceAVConfigRegisterCallback registered (which keeps none).</summary>
    [Hle] int sceAVConfigUnRegisterCallback(int cb) => AvcAllowed ? 0 : AVC_ERR_PERM;

    int CurrentSystemVolume()
    {
        if (_systemVolume >= 0) return _systemVolume;
        return Firmware.Registry.For(Emu.Options.FsRoot).TryGetInt("/CONFIG/SOUND", "main_volume", out int v) && (uint)v <= 30 ? v : 20;
    }

    // ---- display brightness and the idle timers
    /// <summary>(int *max): the panel's maximum brightness (kscePowerGetDisplayMaxBrightness): 0x10000 on the OLED.</summary>
    [Hle]
    int sceAVConfigGetDisplayMaxBrightness(uint max)
    {
        if (!AvcAllowed) return AVC_ERR_PERM;
        if (max == 0) return AVC_ERR_COPY;
        GuestMemory.W32(max, 0x10000);
        return 0;
    }

    /// <summary>(int level): the screen brightness, capped at the maximum, kept (+0x154) and set on the panel.</summary>
    [Hle]
    int sceAVConfigSetDisplayBrightness(int level)
    {
        if (!AvcAllowed) return AVC_ERR_PERM;
        _brightness = (int)Math.Min((uint)level, 0x10000u);
        return 0;
    }

    /// <summary>(int seconds): the auto standby time (0 = off, 60 = the kernel's fixed long table entry); the idle
    /// timers of SceSysmem/ScePower are set from it. VitaSharp never suspends the guest: kept.</summary>
    [Hle] int sceAVConfigSetAutoSuspend(int seconds) { if (!AvcAllowed) return AVC_ERR_PERM; _autoSuspend = seconds; return 0; }

    /// <summary>(int seconds): the screen auto-dim time; PS TV only on 3.74 (handheld: 0x803A0109).</summary>
    [Hle] int sceAVConfigSetAutoDisplayDimming(int seconds) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
}

// SceBbmc: the 3G modem (baseband) manager. Its kernel module starts its state only on a unit with a modem; on the
// Wi-Fi PCH-1000 that state pointer stays NULL and every entry answers 0x80330003 (not initialized). Callers that are
// not system programs (and have no QA/test flag) get 0x80010058 first.
public sealed partial class SystemServicesVsh
{
    const int BBMC_ERR_NO_MODEM = unchecked((int)0x80330003), BBMC_ERR_PERM = unchecked((int)0x80010058);

    int Bbmc() => Emu.IsSystemProgram ? BBMC_ERR_NO_MODEM : BBMC_ERR_PERM;

    [Hle("sceBbmcTurnOn", Default = true)]
    [Hle("sceBbmcTurnOff", Default = true)]
    [Hle("sceBbmcClearEvent", Default = true)]
    [Hle("sceBbmcSetLPMMode", Default = true)]
    [Hle("sceBbmcReserveOperationMode", Default = true)]
    [Hle("sceBbmcResumeSubscriberCallback", Default = true)]
    [Hle("sceBbmcUnsubscribeFeature", Default = true)]
    [Hle("sceBbmcSubscribeFeatureId", Default = true)]
    [Hle("sceBbmcNetworkScanStart", Default = true)]
    [Hle("sceBbmcNetworkScanAbort", Default = true)]
    [Hle("sceBbmcNetworkScanDone", Default = true)]
    [Hle("sceBbmcGetNetworkInfo", Default = true)]
    [Hle("sceBbmcNetGetRejectCause", Default = true)]
    [Hle("sceBbmcGetComFirmVersion", Default = true)]
    [Hle("sceBbmcGetDriverInfo", Default = true)]
    [Hle("sceBbmcGetFailureInfo", Default = true)]
    [Hle("sceBbmcIsAbleToRunUnderCurrentTemp", Default = true)]
    [Hle("sceBbmcVerifyPin", Default = true)]
    [Hle("sceBbmcUnblockPin", Default = true)]
    [Hle("sceBbmcGetPinStatus", Default = true)]
    [Hle("sceBbmcGetCKStatus", Default = true)]
    [Hle("sceBbmcDepersonalizeCK", Default = true)]
    [Hle("sceBbmcUnblockCK", Default = true)]
    [Hle("sceBbmcSimGetPLMNModeBit", Default = true)]
    [Hle("sceBbmcSetupGetValueStart", Default = true)]
    [Hle("sceBbmcSetupGetValueDone", Default = true)]
    [Hle("sceBbmcSetupSetValue", Default = true)]
    [Hle("sceBbmcSMSSend", Default = true)]
    [Hle("sceBbmcSMSGetListStart", Default = true)]
    [Hle("sceBbmcSMSGetListDone", Default = true)]
    [Hle("sceBbmcSMSReadStart", Default = true)]
    [Hle("sceBbmcSMSReadDone", Default = true)]
    [Hle("sceBbmcSMSReadAck", Default = true)]
    [Hle("sceBbmcPacketGetLastCallEndReason", Default = true)]
    [Hle("sceBbmcPacketReleasePDPContext", Default = true)]
    [Hle("sceBbmcPacketGetCurrentBearerTech", Default = true)]
    [Hle("sceBbmcPacketIfSetControlFlag", Default = true)]
    [Hle("sceBbmcPacketIfClearControlFlag", Default = true)]
    [Hle("sceBbmcPacketIfGetControlFlag", Default = true)]
    [Hle("sceBbmcGpsCellGetLocationInfoStart", Default = true)]
    [Hle("sceBbmcGpsCellGetLocationInfoDone", Default = true)]
    [Hle("sceBbmcGetFileRefreshDetail", Default = true)]
    [Hle("sceBbmcFileRefreshResponse", Default = true)]
    [Hle("sceBbmcDLUpdaterGetLatestArchiveVersion", Default = true)]
    [Hle("sceBbmcDLUpdaterGetArchiveVersion", Default = true)]
    [Hle("sceBbmcDLUpdaterCheckComVersion", Default = true)]
    int sceBbmcNoModem() => Bbmc();

    /// <summary>The modem's USB interface (for tethering a PC): with no modem state these return 0x80330003 at once
    /// (before any permission check: they are called from the USB settings).</summary>
    [Hle("sceBbmcUsbExtIfStart", Default = true)]
    [Hle("sceBbmcUsbExtIfStop", Default = true)]
    int sceBbmcUsbExtIf(int mode) => BBMC_ERR_NO_MODEM;
}
