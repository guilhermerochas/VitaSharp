using System.Security.Cryptography;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// More kernel services the system software calls (FW 3.74 kernel modules, read in the modules' code): the system
// update manager, Bluetooth with nothing paired, the USB PSP-communication device and the virtual USB storage with no
// USB host attached, Wi-Fi/HID/controller/USB host registrations, the debug switches, the region string conversion,
// process time of another process, the kernel random number generator, the package-install notifications of the DRM
// manager. Each follows its module's checks; where the module talks to hardware the emulated console does not have, the
// answer is the one the module gives without it.

public sealed partial class SystemServicesVsh
{
    const int KERNEL_ERR_ILLEGAL_ADDR = unchecked((int)0x80020006), KERNEL_ERR_UNSUP = unchecked((int)0x80020004),
        KERNEL_ERR_INVALID_ARGUMENT = unchecked((int)0x80020005);
    const string UpdLib = "SceSblSsUpdateMgr";

    // ------------------------------------------------------------------ SceSblSsUpdateMgr (bootimage SceSblUpdateMgr)
    // Every call is for system programs only (others: 0x800F022C, which the HLE's callers never are). VitaSharp does not
    // update the installed system software: the queries answer for the installed 3.74 in normal mode, the update
    // notifications are accepted, and the operations that would verify, unpack or write an update package report the
    // operation unsupported.

    byte _updateMode;

    /// <summary>(uint8_t *mode): the update mode, 0 (normal) unless set.</summary>
    [Hle("sceSblUsGetUpdateMode", Library = UpdLib)]
    int sceSblUsGetUpdateMode(uint mode)
    {
        if (mode == 0) return KERNEL_ERR_ILLEGAL_ADDR;
        GuestMemory.Write(mode, _updateMode);
        return 0;
    }

    [Hle("sceSblUsSetUpdateMode", Library = UpdLib)] int sceSblUsSetUpdateMode(int mode) { _updateMode = (byte)mode; return 0; }
    /// <summary>(void): the system partitions are intact.</summary>
    [Hle("sceSblUsCheckSystemIntegrity", Library = UpdLib)] int sceSblUsCheckSystemIntegrity() => 0;
    /// <summary>(int what, int on): power hold during an update (nothing to hold).</summary>
    [Hle("sceSblUsPowerControl", Library = UpdLib)] int sceSblUsPowerControl(int what, int on) => 0;

    /// <summary>(int kind, uint32_t *version): the version an update must be newer than: the installed 3.74.</summary>
    [Hle("sceSblUsGetApplicableVersion", Library = UpdLib)]
    int sceSblUsGetApplicableVersion(int kind, uint version)
    {
        if (version == 0) return KERNEL_ERR_ILLEGAL_ADDR;
        GuestMemory.W32(version, 0x03740011);
        return 0;
    }

    /// <summary>(int a, int b, void *status, uint size): the update status block (nothing in progress: all zero).</summary>
    [Hle("sceSblUsGetStatus", Library = UpdLib)]
    int sceSblUsGetStatus(uint a, uint b, uint status)
    {
        if (status == 0) return KERNEL_ERR_ILLEGAL_ADDR;
        GuestMemory.Fill(status, 0, 0x10);
        return 0;
    }

    // notifications from the updater about its progress and the software information it records
    [Hle("sceSblUsInformUpdateStarted", Library = UpdLib)] int sceSblUsInformUpdateStarted(uint a, uint b, uint c, uint d) => 0;
    [Hle("sceSblUsInformUpdateOngoing", Library = UpdLib)] int sceSblUsInformUpdateOngoing(uint a, uint b) => 0;
    [Hle("sceSblUsInformUpdateFinished", Library = UpdLib)] int sceSblUsInformUpdateFinished(uint a, uint b, uint c) => 0;
    [Hle("sceSblUsSetSwInfoInt", Library = UpdLib)] int sceSblUsSetSwInfoInt(uint name, uint len, uint value) => name == 0 ? KERNEL_ERR_ILLEGAL_ADDR : 0;
    [Hle("sceSblUsSetSwInfoStr", Library = UpdLib)] int sceSblUsSetSwInfoStr(uint name, uint len, uint str, uint size) => name == 0 ? KERNEL_ERR_ILLEGAL_ADDR : 0;
    [Hle("sceSblUsSetSwInfoBin", Library = UpdLib)] int sceSblUsSetSwInfoBin(uint name, uint len, uint data, uint size) => name == 0 ? KERNEL_ERR_ILLEGAL_ADDR : 0;

    // the update package operations (a PUP path or a package buffer)
    [Hle("sceSblUsVerifyPup", Library = UpdLib)] int sceSblUsVerifyPup(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupHeader", Library = UpdLib)] int sceSblUsVerifyPupHeader(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupAdditionalSign", Library = UpdLib)] int sceSblUsVerifyPupAdditionalSign(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupWatermark", Library = UpdLib)] int sceSblUsVerifyPupWatermark(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupSegment", Library = UpdLib)] int sceSblUsVerifyPupSegment(uint path, uint a, uint b, uint c) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupSegmentById", Library = UpdLib)] int sceSblUsVerifyPupSegmentById(uint path, uint a, uint b, uint c) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsInspectSpackage", Library = UpdLib)] int sceSblUsInspectSpackage(uint a, uint buf, uint size) => buf == 0 || size == 0 ? KERNEL_ERR_INVALID_ARGUMENT : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsUpdateSpackage", Library = UpdLib)] int sceSblUsUpdateSpackage(uint a, uint buf, uint size) => buf == 0 || size == 0 ? KERNEL_ERR_INVALID_ARGUMENT : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsExtractSpackage", Library = UpdLib)] int sceSblUsExtractSpackage(uint a, uint buf, uint size) => buf == 0 || size == 0 ? KERNEL_ERR_INVALID_ARGUMENT : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsGetExtractSpackage", Library = UpdLib)] int sceSblUsGetExtractSpackage(uint a, uint b, uint info) => info == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsAllocateBuffer", Library = UpdLib)] int sceSblUsAllocateBuffer(uint size, uint outp) => KERNEL_ERR_UNSUP;
    [Hle("sceSblUsReleaseBuffer", Library = UpdLib)] int sceSblUsReleaseBuffer(uint uid) => KERNEL_ERR_UNSUP;

    // ------------------------------------------------------------------ SceBt (bootimage SceBt): the stack is up, idle
    // (state 0x1C), no device was ever paired or is connected, nothing is ever found by an inquiry.
    const int BT_ERR_INVALID_ARG = unchecked((int)0x802F1202), BT_ERR_NO_DEVICE = unchecked((int)0x802F0109),
        BT_ERR_NOT_FOUND = unchecked((int)0x802F0C01);

    /// <summary>(void *events, int max): the events queued for the caller: none.</summary>
    [Hle] int sceBtReadEvent(uint events, int max) => 0;
    [Hle] int sceBtUnregisterCallback(int cb) => 0;
    /// <summary>(void): the stack's last error, none.</summary>
    [Hle] int sceBtGetLastError() => 0;
    /// <summary>(int status 0..4): the AVRCP play status sent to a connected remote (none: only checked).</summary>
    [Hle] int sceBtAvrcpSetPlayStatus(int status) => (uint)status > 4 ? BT_ERR_INVALID_ARG : 0;
    /// <summary>(const char *title, uint len &lt;= 0x400): the AVRCP title.</summary>
    [Hle] int sceBtAvrcpSetTitle(uint title, uint len) => len > 0x400 ? BT_ERR_INVALID_ARG : 0;
    /// <summary>(void): an inquiry starts from the idle state; no device answers.</summary>
    [Hle] int sceBtStartInquiry() => 0;
    [Hle] int sceBtStopInquiry() => 0;
    /// <summary>(uint mac0, uint mac1, char *name): the name of a known device: none is known.</summary>
    [Hle] int sceBtGetDeviceName(uint mac0, uint mac1, uint name) => BT_ERR_NOT_FOUND;
    [Hle] int sceBtReplyPinCode(uint mac0, uint mac1, uint pin, uint len) => BT_ERR_NOT_FOUND;
    [Hle] int sceBtReplyUserConfirmation(uint mac0, uint mac1, int accept) => BT_ERR_NOT_FOUND;
    /// <summary>(uint mac0, uint mac1): the registered (paired) device: none.</summary>
    [Hle] int sceBtDeleteRegisteredInfo(uint mac0, uint mac1) => BT_ERR_NO_DEVICE;
    [Hle] int sceBtGetConnectingInfo(uint mac0, uint mac1) => BT_ERR_NO_DEVICE;
    [Hle] int sceBtStartConnect(uint mac0, uint mac1) => BT_ERR_NO_DEVICE;
    [Hle] int sceBtStartDisconnect(uint mac0, uint mac1) => BT_ERR_NO_DEVICE;

    // ------------------------------------------------------------------ SceUsbPspcm (bootimage SceUsbPspcm)
    // The USB PSP-communication function (the PS Vita as a PSP-compatible USB device): every operation needs the device
    // running (its flags +0x98 & 0xC) and answers 0x8024450D otherwise. No USB host is ever attached, so the device never
    // comes up: starting it is accepted, everything else reports it not running.
    const int PSPCM_ERR_NOT_RUNNING = unchecked((int)0x8024450D);

    [Hle] int sceUsbPspcmStartDevice(uint a) => 0;
    [Hle] int sceUsbPspcmStopDevice() => 0;
    [Hle] int sceUsbPspcmSetTitle(uint title) => 0;
    [Hle] int sceUsbPspcmOpen(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle] int sceUsbPspcmClose(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle] int sceUsbPspcmRead(uint a, uint buf, uint size) => PSPCM_ERR_NOT_RUNNING;
    [Hle] int sceUsbPspcmWrite(uint a, uint buf, uint size) => PSPCM_ERR_NOT_RUNNING;
    [Hle] int sceUsbPspcmDevctl(uint a, uint cmd, uint arg, uint size) => PSPCM_ERR_NOT_RUNNING;
    [Hle] int sceUsbPspcmIoctlReset(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle] int sceUsbPspcmIoctlWaitData(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle] int sceUsbPspcmIoctlPollData(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle] int sceUsbPspcmIoctlCancelWaitData(uint a) => PSPCM_ERR_NOT_RUNNING;

    // ------------------------------------------------------------------ SceUsbstorVStor (virtual USB mass storage)
    // The image is served to a USB host; none is attached, so the device is configured and "started" without a host.
    [Hle] int sceUsbstorVStorSetImgFilePath(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : 0;
    [Hle] int sceUsbstorVStorSetDeviceInfo(uint name, uint version) => 0;
    [Hle] int sceUsbstorVStorStart(int type) => 0;
    [Hle] int sceUsbstorVStorStop() => 0;

    // ------------------------------------------------------------------ registrations for devices nothing reports
    int _wlanCallbacks;
    /// <summary>(SceUID cb): Wi-Fi events; returns the registration id. No Wi-Fi chip raises them.</summary>
    [Hle] int sceWlanRegisterCallback(int cb) => ++_wlanCallbacks;
    [Hle] int sceWlanUnregisterCallback(int id) => 0;
    /// <summary>(SceUID *pid): the process woken by Wake-on-WLAN: none.</summary>
    [Hle] int sceWlanGetWakeOnTargetProcess(uint pid) { if (pid != 0) GuestMemory.W32(pid, 0); return 0; }
    [Hle] int sceUsbdUnregisterCallback(int id) => 0;
    [Hle] int sceCtrlUnregisterBdRMCCallback(int cb) => 0;
    /// <summary>(int port): disconnects an external controller (PS TV); none is connected on the handheld.</summary>
    [Hle] int sceCtrlDisconnect(int port) => 0;
    /// <summary>(SceUID pid, uint *status): the controller-related state of a process (nothing intercepted: 0).</summary>
    [Hle] int sceCtrlGetProcessStatus(int pid, uint status) { if (status == 0) return KERNEL_ERR_ILLEGAL_ADDR; GuestMemory.W32(status, 0); return 0; }
    /// <summary>(int handle): clears the input buffer of a keyboard: no keyboard, nothing buffered.</summary>
    [Hle] int sceHidKeyboardClear(int handle) => 0;
    /// <summary>(SceUID callback, SceUID event): undoes sceKernelRegisterCallbackToEvent (and the All form, every
    /// event of the callback).</summary>
    [Hle] int sceKernelUnregisterCallbackFromEvent(int callback, int ev) => 0;
    [Hle] int sceKernelUnregisterCallbackFromEventAll(int callback) => 0;
    /// <summary>(int mask): the CPUs the process may run on; the emulator schedules guest threads itself.</summary>
    [Hle] int sceKernelChangeActiveCpuMask(int mask) => mask == 0 ? KERNEL_ERR_INVALID_ARGUMENT : 0;

    // SceDebugLed (sysmem): the development kit's debug LEDs; a retail unit has none to light.
    [Hle] int sceDebugLedInvokeHandle0(uint a, uint b, uint c) => 0;
    [Hle] int sceDebugLedInvokeHandle1(uint a, uint b, uint c) => 0;

    // ------------------------------------------------------------------ SceDipsw (sysmem): the debug switches
    readonly uint[] _dipsw = new uint[4];
    [Hle] int sceKernelSetDipsw(uint bit) { if (bit < 128) _dipsw[bit >> 5] |= 1u << (int)(bit & 31); return 0; }
    [Hle] int sceKernelClearDipsw(uint bit) { if (bit < 128) _dipsw[bit >> 5] &= ~(1u << (int)(bit & 31)); return 0; }

    // ------------------------------------------------------------------ SceRegMgrService
    static readonly string[] RegionCodes = ["J1", "UC2", "AU3", "CEK", "CEL", "KR2", "E12", "TW1", "RU3", "MX2", "TOL", "BR2", "CN9"];

    /// <summary>(int index 1..13, char *buf, int size): the region code of an index of the registry manager's table (as
    /// sceRegMgrSrvGetRegionStr returns it for the unit).</summary>
    [Hle]
    int sceRegMgrSrvCnvRegionStr(int index, uint buf, int size)
    {
        if (buf == 0 || size <= 3) return unchecked((int)0x800D0004);
        if (index < 1 || index > RegionCodes.Length) return unchecked((int)0x800D0003);
        GuestMemory.WriteCString(buf, RegionCodes[index - 1], size);
        return 0;
    }

    // ------------------------------------------------------------------ SceProcessmgr / SceSblRng
    /// <summary>(SceUID pid, SceUInt64 *time): the run time of a process in microseconds (one emulated process clock).</summary>
    [Hle]
    int sceKernelGetRemoteProcessTime(int pid, uint time)
    {
        if (time == 0) return KERNEL_ERR_ILLEGAL_ADDR;
        GuestMemory.Write(time, K.ProcessTimeUs);
        return 0;
    }

    /// <summary>(const void *addresses): libkernel tells the process manager where its own entry points are (for the
    /// kernel's user-mode callbacks); the HLE kernel does not call into libkernel.</summary>
    [Hle] int _sceKernelRegisterLibkernelAddresses(uint addresses) => 0;

    /// <summary>(void *buf, uint size &lt;= 64): bytes from the kernel's random number generator.</summary>
    [Hle]
    int _sceKernelGetRandomNumber(uint buf, uint size)
    {
        if (buf == 0) return KERNEL_ERR_ILLEGAL_ADDR;
        if (size > 64) return KERNEL_ERR_INVALID_ARGUMENT;
        RandomNumberGenerator.Fill(GuestMemory.Span(buf, (int)size));
        return 0;
    }

    // ------------------------------------------------------------------ SceNpDrmPackage (bootimage SceNpDrm)
    // The package installer (NPXS10031) tells the DRM manager when it installs, removes or formats content, so that the
    // manager can hold its license state; VitaSharp installs packages on the host side and keeps no such state.
    [Hle] int _sceNpDrmPackageInstallStarted(uint a, uint b) => 0;
    [Hle] int _sceNpDrmPackageInstallFinished(uint a, uint b) => 0;
    [Hle] int sceNpDrmPackageInstallOngoing(uint a, uint b) => 0;
    [Hle] int _sceNpDrmPackageUninstallStarted(uint a, uint b) => 0;
    [Hle] int _sceNpDrmPackageUninstallFinished(uint a, uint b) => 0;
    [Hle] int sceNpDrmPackageUninstallOngoing(uint a, uint b) => 0;
    [Hle] int _sceNpDrmSaveDataInstallStarted(uint a, uint b) => 0;
    [Hle] int _sceNpDrmSaveDataInstallFinished(uint a, uint b) => 0;
    [Hle] int sceNpDrmSaveDataInstallOngoing(uint a, uint b) => 0;
    [Hle] int _sceNpDrmSaveDataFormatStarted(uint a, uint b) => 0;
    [Hle] int _sceNpDrmSaveDataFormatFinished(uint a, uint b) => 0;
    [Hle] int sceNpDrmSaveDataFormatOngoing(uint a, uint b) => 0;
}

public sealed partial class Sensors
{
    // ---- SceMotion's Ext forms (driver_us): the same library with a port, 0 the built-in sensors, 1..4 the external
    // controllers of a PS TV (none here: the driver's "no such controller" 0x80360202), above 4 out of range.
    const int ERR_EXT_NO_PORT = unchecked((int)0x80360202);
    static int Port(int port) => port == 0 ? 0 : ERR_EXT_NO_PORT;
    [Hle] int sceMotionInitLibraryExt(int port) => 0;
    [Hle] int sceMotionTermLibraryExt(int port) => 0;
    [Hle] int sceMotionStartSamplingExt(int port) => port == 0 ? sceMotionStartSampling() : Port(port);
    [Hle] int sceMotionStopSamplingExt(int port) => port == 0 ? sceMotionStopSampling() : Port(port);
    [Hle] int sceMotionResetExt(int port) => port == 0 ? sceMotionReset() : Port(port);
    [Hle] int sceMotionSetTiltCorrectionExt(int port, int e) => port == 0 ? sceMotionSetTiltCorrection(e) : Port(port);
    [Hle] int sceMotionSetDeadbandExt(int port, int e) => port == 0 ? sceMotionSetDeadband(e) : Port(port);
    /// <summary>(int port, SceMotionState *state): sceMotionGetState of a port.</summary>
    [Hle("sceMotion_D87E2B64", Nid = 0xD87E2B64, Library = "SceMotion")] int sceMotionGetStateExt(int port, uint state) => port == 0 ? sceMotionGetState(state) : Port(port);
    [Hle] int sceMotionGetStateInternal(uint state) => sceMotionGetState(state);

    // ---- SceMotionDev: sampling switches of the sensor driver (the emulated sensors are always sampled)
    [Hle] int sceMotionDevSamplingStart() => 0;
    [Hle] int sceMotionDevSamplingStop() => 0;
    [Hle] int sceMotionDevMagnSamplingStart() => 0;
    [Hle] int sceMotionDevMagnSamplingStop() => 0;
    /// <summary>(void): whether the driver finished its start-up: always.</summary>
    [Hle] int sceMotionDevIsReady() => 1;
    /// <summary>(const void *calib): stores a new magnetometer calibration; no magnetometer is emulated.</summary>
    [Hle] int sceMotionDevUpdateMagnCalibData(uint calib) => calib == 0 ? ERR_DEV_NULL : 0;
    [Hle] int sceMotionDevUpdateMagnStabilityLevel(int level) => 0;

    /// <summary>(void *loc 0x30): the sensor positions, as sceMotionGetDeviceLocation reports them.</summary>
    [Hle] int sceMotionDevGetDeviceLocation(uint loc) => loc == 0 ? ERR_DEV_NULL : sceMotionGetDeviceLocation(loc);
    /// <summary>(void *bias 0x10): the gyro bias the driver measured at rest: none on the emulated (ideal) gyro.</summary>
    [Hle] int sceMotionDevGetGyroBias(uint bias) { if (bias == 0) return ERR_DEV_NULL; GuestMemory.Fill(bias, 0, 0x10); return 0; }
    /// <summary>(void *calib 0x60): the accelerometer calibration record; empty without factory data.</summary>
    [Hle] int sceMotionDevGetAccCalibData(uint calib) { if (calib == 0) return ERR_DEV_NULL; GuestMemory.Fill(calib, 0, 0x60); return 0; }
    /// <summary>(int port, uint8_t *type): the controller the motion data comes from: 0, the built-in sensors.</summary>
    [Hle] int sceMotionDevGetControllerType(int port, uint type) { if (type == 0) return ERR_DEV_NULL; GuestMemory.Write(type, (byte)0); return 0; }
    /// <summary>(void *buf, int size &lt;= 0x40, int count): raw sensor records, as sceMotionDevReadForMagnCalib.</summary>
    [Hle] int sceMotionDevRead(uint buf, int size, int count) => buf == 0 || count == 0 || size > 0x40 ? ERR_DEV_NULL : sceMotionDevReadForMagnCalib(buf, size, count);
}

public sealed partial class SystemUtil
{
    /// <summary>The progress notification of a background application (SceNotificationUtilProgress): the
    /// emulator's notification overlay shows only plain notifications, so progress is accepted without one.</summary>
    [Hle] int sceNotificationUtilBgAppInitialize() => 0;
    [Hle] int sceNotificationUtilProgressBegin(uint param) => 0;
    [Hle] int sceNotificationUtilProgressUpdate(uint param) => 0;
    [Hle] int sceNotificationUtilProgressFinish(uint param) => 0;
}

public sealed partial class Fios2
{
    /// <summary>FIOS2 debug output and statistics (the library keeps no I/O statistics here).</summary>
    [Hle] void sceFiosDebugDumpError(int err) => Util.Log.Info($"[fios] error {ErrorName(err)}");
    [Hle] void sceFiosDebugDumpDate(long date) => Util.Log.Info($"[fios] date {date}");
    [Hle] void sceFiosStatisticsReset() { }
    [Hle] void sceFiosStatisticsPrint() { }
}

public sealed partial class Ngs
{
    /// <summary>The Internal forms of the parameter range checks (the HLE keeps every parameter in range, as
    /// sceNgsVoiceGetParamsOutOfRange reports).</summary>
    [Hle] int sceNgsVoiceGetParamsOutOfRangeBufferedInternal(uint voice, uint module, uint buf) => OK;
    [Hle] int sceNgsModuleCheckParamsInRangeInternal(uint module, uint parameters, uint size) => OK;
    // The system-handle side of the kernel NGS (its callback list, data pushed/pulled by the PC tools, the voice dirty
    // flags and bypasses): the HLE NGS keeps no separate kernel state, so there is nothing queued or to clear.
    [Hle]
    int sceNgsSystemGetCallbackListInternal(uint sys, uint list)
    {
        if (sys == 0 || list == 0) return INVALID_ARG;
        GuestMemory.W32(list, 0);
        return OK;
    }
    [Hle] int sceNgsSystemPushDataInternal(uint sys, uint data, uint size) => sys == 0 ? INVALID_ARG : OK;
    [Hle] int sceNgsSystemPullDataInternal(uint sys, uint data, uint size) => sys == 0 ? INVALID_ARG : OK;
    [Hle] int sceNgsVoiceClearDirtyFlagInternal(uint voice, uint module) => voice == 0 ? INVALID_ARG : OK;
    [Hle] int sceNgsVoiceSetAllBypassesInternal(uint voice, uint flags) => voice == 0 ? INVALID_ARG : OK;
}

public sealed partial class Fios2User
{
    // ---- the FIOS2 kernel overlay syscalls of FW 3.x libfios2 (the "02" set): the same overlay table
    [Hle] int sceFiosKernelOverlayGetList02(int pid, uint minOrder, uint maxOrder, uint outIds, uint maxIds, Ptr<uint> actual) =>
        sceFiosOverlayGetList02(pid, minOrder, maxOrder, outIds, maxIds, actual);
    [Hle] int sceFiosKernelOverlayGetRecommendedScheduler02(int param, string path) => sceFiosOverlayGetRecommendedScheduler02(param, path);
    [Hle] int sceFiosKernelOverlayResolveSync02(int pid, int mode, string inPath, uint outPath, uint maxPath) =>
        sceFiosOverlayResolveSync02(pid, mode, inPath, outPath, maxPath);
    [Hle] int sceFiosKernelOverlayResolveWithRangeSync02(int pid, int mode, string inPath, uint outPath, uint maxPath, uint minOrder, uint maxOrder) =>
        sceFiosOverlayResolveWithRangeSync02(pid, mode, inPath, outPath, maxPath, minOrder, maxOrder);
}
