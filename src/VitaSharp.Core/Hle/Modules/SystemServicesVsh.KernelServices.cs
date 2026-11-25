using System.Security.Cryptography;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// More kernel services the system software calls (FW 3.74 kernel modules, read in the modules' code): the system
// update manager, Bluetooth with nothing paired, the USB PSP-communication device and the virtual USB storage with no
// USB host attached, Wi-Fi/HID/controller/USB host registrations, the debug switches, the region string conversion,
// process time of another process, the kernel random number generator, the package-install notifications of the DRM
// manager. Each follows its module's checks; where the module talks to hardware the emulated console does not have, the
// answer is the one the module gives without it.

public sealed partial class SystemServicesVsh
{
    /// <summary>SceRegistryMgr: a NULL buffer or one too small (0x800D0004).</summary>
    const int REGMGR_ERR_INVALID = unchecked((int)0x800D0004);
    /// <summary>SceRegistryMgr: a region index outside the table (1..13) (0x800D0003).</summary>
    const int REGMGR_ERR_REGION_INDEX = unchecked((int)0x800D0003);

    const int KERNEL_ERR_ILLEGAL_ADDR = unchecked((int)0x80020006), KERNEL_ERR_UNSUP = unchecked((int)0x80020004),
        KERNEL_ERR_INVALID_ARGUMENT = unchecked((int)0x80020005);
    const string UpdLib = "SceSblSsUpdateMgr";

    // ------------------------------------------------------------------ SceSblSsUpdateMgr (bootimage SceSblUpdateMgr)
    // Every call is for system programs only (others: 0x800F022C, which the HLE's callers never are). VitaSharp does not
    // update the installed system software: the queries answer for the installed 3.74 in normal mode, the update
    // notifications are accepted, and the operations that would verify, unpack or write an update package report the
    // operation unsupported.

    [SnapshotMissing] // states of builds before it: the call did not exist, so it was never set
    byte _updateMode;

    /// <summary>(uint8_t *mode): the update mode, 0 (normal) unless set.</summary>
    [Hle("sceSblUsGetUpdateMode", Library = UpdLib, Default = true)]
    int sceSblUsGetUpdateMode(uint mode)
    {
        if (mode == 0)
        {
            return KERNEL_ERR_ILLEGAL_ADDR;
        }

        GuestMemory.Write(mode, _updateMode);
        return 0;
    }

    [Hle("sceSblUsSetUpdateMode", Library = UpdLib, Default = true)]
    int sceSblUsSetUpdateMode(int mode)
    {
        _updateMode = (byte)mode;
        return 0;
    }
    /// <summary>(void): the system partitions are intact.</summary>
    [Hle("sceSblUsCheckSystemIntegrity", Library = UpdLib, Default = true)] int sceSblUsCheckSystemIntegrity() => 0;
    /// <summary>(int what, int on): power hold during an update (nothing to hold).</summary>
    [Hle("sceSblUsPowerControl", Library = UpdLib, Default = true)] int sceSblUsPowerControl(int what, int on) => 0;

    /// <summary>(int kind, uint32_t *version): the version an update must be newer than: the installed 3.74.</summary>
    [Hle("sceSblUsGetApplicableVersion", Library = UpdLib, Default = true)]
    int sceSblUsGetApplicableVersion(int kind, uint version)
    {
        if (version == 0)
        {
            return KERNEL_ERR_ILLEGAL_ADDR;
        }

        GuestMemory.W32(version, 0x03740011);
        return 0;
    }

    /// <summary>(int a, int b, void *status, uint size): the update status block (nothing in progress: all zero).</summary>
    [Hle("sceSblUsGetStatus", Library = UpdLib, Default = true)]
    int sceSblUsGetStatus(uint a, uint b, uint status)
    {
        if (status == 0)
        {
            return KERNEL_ERR_ILLEGAL_ADDR;
        }

        GuestMemory.Fill(status, 0, 0x10);
        return 0;
    }

    // notifications from the updater about its progress and the software information it records
    [Hle("sceSblUsInformUpdateStarted", Library = UpdLib, Default = true)] int sceSblUsInformUpdateStarted(uint a, uint b, uint c, uint d) => 0;
    [Hle("sceSblUsInformUpdateOngoing", Library = UpdLib, Default = true)] int sceSblUsInformUpdateOngoing(uint a, uint b) => 0;
    [Hle("sceSblUsInformUpdateFinished", Library = UpdLib, Default = true)] int sceSblUsInformUpdateFinished(uint a, uint b, uint c) => 0;
    [Hle("sceSblUsSetSwInfoInt", Library = UpdLib, Default = true)] int sceSblUsSetSwInfoInt(uint name, uint len, uint value) => name == 0 ? KERNEL_ERR_ILLEGAL_ADDR : 0;
    [Hle("sceSblUsSetSwInfoStr", Library = UpdLib, Default = true)] int sceSblUsSetSwInfoStr(uint name, uint len, uint str, uint size) => name == 0 ? KERNEL_ERR_ILLEGAL_ADDR : 0;
    [Hle("sceSblUsSetSwInfoBin", Library = UpdLib, Default = true)] int sceSblUsSetSwInfoBin(uint name, uint len, uint data, uint size) => name == 0 ? KERNEL_ERR_ILLEGAL_ADDR : 0;

    // the update package operations (a PUP path or a package buffer)
    [Hle("sceSblUsVerifyPup", Library = UpdLib, Default = true)] int sceSblUsVerifyPup(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupHeader", Library = UpdLib, Default = true)] int sceSblUsVerifyPupHeader(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupAdditionalSign", Library = UpdLib, Default = true)] int sceSblUsVerifyPupAdditionalSign(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupWatermark", Library = UpdLib, Default = true)] int sceSblUsVerifyPupWatermark(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupSegment", Library = UpdLib, Default = true)] int sceSblUsVerifyPupSegment(uint path, uint a, uint b, uint c) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsVerifyPupSegmentById", Library = UpdLib, Default = true)] int sceSblUsVerifyPupSegmentById(uint path, uint a, uint b, uint c) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsInspectSpackage", Library = UpdLib, Default = true)] int sceSblUsInspectSpackage(uint a, uint buf, uint size) => buf == 0 || size == 0 ? KERNEL_ERR_INVALID_ARGUMENT : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsUpdateSpackage", Library = UpdLib, Default = true)] int sceSblUsUpdateSpackage(uint a, uint buf, uint size) => buf == 0 || size == 0 ? KERNEL_ERR_INVALID_ARGUMENT : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsExtractSpackage", Library = UpdLib, Default = true)] int sceSblUsExtractSpackage(uint a, uint buf, uint size) => buf == 0 || size == 0 ? KERNEL_ERR_INVALID_ARGUMENT : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsGetExtractSpackage", Library = UpdLib, Default = true)] int sceSblUsGetExtractSpackage(uint a, uint b, uint info) => info == 0 ? KERNEL_ERR_ILLEGAL_ADDR : KERNEL_ERR_UNSUP;
    [Hle("sceSblUsAllocateBuffer", Library = UpdLib, Default = true)] int sceSblUsAllocateBuffer(uint size, uint outp) => KERNEL_ERR_UNSUP;
    [Hle("sceSblUsReleaseBuffer", Library = UpdLib, Default = true)] int sceSblUsReleaseBuffer(uint uid) => KERNEL_ERR_UNSUP;

    // ------------------------------------------------------------------ SceBt (bootimage SceBt): the stack is up, idle
    // (state 0x1C), no device was ever paired or is connected, nothing is ever found by an inquiry.
    const int BT_ERR_INVALID_ARG = unchecked((int)0x802F1202), BT_ERR_NO_DEVICE = unchecked((int)0x802F0109),
        BT_ERR_NOT_FOUND = unchecked((int)0x802F0C01);

    /// <summary>(void *events, int max): the events queued for the caller: none.</summary>
    [Hle(Default = true)] int sceBtReadEvent(uint events, int max) => 0;
    [Hle(Default = true)] int sceBtUnregisterCallback(int cb) => 0;
    /// <summary>(void): the stack's last error, none.</summary>
    [Hle(Default = true)] int sceBtGetLastError() => 0;
    /// <summary>(int status 0..4): the AVRCP play status sent to a connected remote (none: only checked).</summary>
    [Hle(Default = true)] int sceBtAvrcpSetPlayStatus(int status) => (uint)status > 4 ? BT_ERR_INVALID_ARG : 0;
    /// <summary>(const char *title, uint len &lt;= 0x400): the AVRCP title.</summary>
    [Hle(Default = true)] int sceBtAvrcpSetTitle(uint title, uint len) => len > 0x400 ? BT_ERR_INVALID_ARG : 0;
    /// <summary>(void): an inquiry starts from the idle state; no device answers.</summary>
    [Hle(Default = true)] int sceBtStartInquiry() => 0;
    [Hle(Default = true)] int sceBtStopInquiry() => 0;
    /// <summary>(uint mac0, uint mac1, char *name): the name of a known device: none is known.</summary>
    [Hle(Default = true)] int sceBtGetDeviceName(uint mac0, uint mac1, uint name) => BT_ERR_NOT_FOUND;
    [Hle(Default = true)] int sceBtReplyPinCode(uint mac0, uint mac1, uint pin, uint len) => BT_ERR_NOT_FOUND;
    [Hle(Default = true)] int sceBtReplyUserConfirmation(uint mac0, uint mac1, int accept) => BT_ERR_NOT_FOUND;
    /// <summary>(uint mac0, uint mac1): the registered (paired) device: none.</summary>
    [Hle(Default = true)] int sceBtDeleteRegisteredInfo(uint mac0, uint mac1) => BT_ERR_NO_DEVICE;
    /// <summary>(uint mac0, uint mac1): the connection state of a device (FW 3.74 SceBt 0xD9B65CAA: 1 not connected,
    /// 2 connected, 3-6 the audio profiles); a paired keyboard or mouse is connected.</summary>
    [Hle]
    int sceBtGetConnectingInfo(uint mac0, uint mac1) =>
        PairedBluetoothDevices().Exists(d => d.Mac0 == mac0 && d.Mac1 == mac1) ? 2 : BT_ERR_NO_DEVICE;
    [Hle(Default = true)] int sceBtStartConnect(uint mac0, uint mac1) => BT_ERR_NO_DEVICE;
    [Hle(Default = true)] int sceBtStartDisconnect(uint mac0, uint mac1) => BT_ERR_NO_DEVICE;

    // ------------------------------------------------------------------ SceUsbPspcm (bootimage SceUsbPspcm)
    // The USB PSP-communication function (the PS Vita as a PSP-compatible USB device): every operation needs the device
    // running (its flags +0x98 & 0xC) and answers 0x8024450D otherwise. No USB host is ever attached, so the device never
    // comes up: starting it is accepted, everything else reports it not running.
    const int PSPCM_ERR_NOT_RUNNING = unchecked((int)0x8024450D);

    [Hle(Default = true)] int sceUsbPspcmStartDevice(uint a) => 0;
    [Hle(Default = true)] int sceUsbPspcmStopDevice() => 0;
    [Hle(Default = true)] int sceUsbPspcmSetTitle(uint title) => 0;
    [Hle(Default = true)] int sceUsbPspcmOpen(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle(Default = true)] int sceUsbPspcmClose(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle(Default = true)] int sceUsbPspcmRead(uint a, uint buf, uint size) => PSPCM_ERR_NOT_RUNNING;
    [Hle(Default = true)] int sceUsbPspcmWrite(uint a, uint buf, uint size) => PSPCM_ERR_NOT_RUNNING;
    [Hle(Default = true)] int sceUsbPspcmDevctl(uint a, uint cmd, uint arg, uint size) => PSPCM_ERR_NOT_RUNNING;
    [Hle(Default = true)] int sceUsbPspcmIoctlReset(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle(Default = true)] int sceUsbPspcmIoctlWaitData(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle(Default = true)] int sceUsbPspcmIoctlPollData(uint a) => PSPCM_ERR_NOT_RUNNING;
    [Hle(Default = true)] int sceUsbPspcmIoctlCancelWaitData(uint a) => PSPCM_ERR_NOT_RUNNING;

    // ------------------------------------------------------------------ SceUsbstorVStor (virtual USB mass storage)
    // The image is served to a USB host; none is attached, so the device is configured and "started" without a host.
    [Hle(Default = true)] int sceUsbstorVStorSetImgFilePath(uint path) => path == 0 ? KERNEL_ERR_ILLEGAL_ADDR : 0;
    [Hle(Default = true)] int sceUsbstorVStorSetDeviceInfo(uint name, uint version) => 0;
    [Hle(Default = true)] int sceUsbstorVStorStart(int type) => 0;
    [Hle(Default = true)] int sceUsbstorVStorStop() => 0;

    // ------------------------------------------------------------------ registrations for devices nothing reports
    [SnapshotMissing] // states of builds before it: the call did not exist, so it was never set
    int _wlanCallbacks;
    /// <summary>(SceUID cb): Wi-Fi events; returns the registration id. No Wi-Fi chip raises them.</summary>
    [Hle] int sceWlanRegisterCallback(int cb) => ++_wlanCallbacks;
    [Hle] int sceWlanUnregisterCallback(int id) => 0;
    /// <summary>(SceUID *pid): the process woken by Wake-on-WLAN: none.</summary>
    [Hle]
    int sceWlanGetWakeOnTargetProcess(uint pid)
    {
        if (pid != 0)
        {
            GuestMemory.W32(pid, 0);
        }
        return 0;
    }
    [Hle] int sceUsbdUnregisterCallback(int id) => 0;
    [Hle(Cost = HleCost.KernelEntry)] int sceCtrlUnregisterBdRMCCallback(int cb) => 0;
    /// <summary>(int port): disconnects an external controller (PS TV); none is connected on the handheld.</summary>
    [Hle(Cost = HleCost.KernelEntry)] int sceCtrlDisconnect(int port) => 0;
    /// <summary>(SceUID pid, uint *status): the controller-related state of a process (nothing intercepted: 0).</summary>
    [Hle(Cost = HleCost.KernelEntry)]
    int sceCtrlGetProcessStatus(int pid, uint status)
    {
        if (status == 0)
        {
            return KERNEL_ERR_ILLEGAL_ADDR;
        }
        GuestMemory.W32(status, 0);
        return 0;
    }
    /// <summary>(int handle): clears the input buffer of a keyboard: no keyboard, nothing buffered.</summary>
    [Hle] int sceHidKeyboardClear(int handle) => 0;
    /// <summary>(int mask): the CPUs the process may run on; the emulator schedules guest threads itself.</summary>
    [Hle] int sceKernelChangeActiveCpuMask(int mask) => mask == 0 ? KERNEL_ERR_INVALID_ARGUMENT : 0;

    // ------------------------------------------------------------------ SceDipsw (sysmem): the debug switches
    [SnapshotMissing] // states of builds before it: the call did not exist, so it was never set
    readonly uint[] _dipsw = new uint[4];

    // ------------------------------------------------------------------ SceRegMgrService
    static readonly string[] RegionCodes = ["J1", "UC2", "AU3", "CEK", "CEL", "KR2", "E12", "TW1", "RU3", "MX2", "TOL", "BR2", "CN9"];

    /// <summary>(int index 1..13, char *buf, int size): the region code of an index of the registry manager's table (as
    /// sceRegMgrSrvGetRegionStr returns it for the unit).</summary>
    [Hle]
    int sceRegMgrSrvCnvRegionStr(int index, uint buf, int size)
    {
        if (buf == 0 || size <= 3)
        {
            return REGMGR_ERR_INVALID;
        }

        if (index < 1 || index > RegionCodes.Length)
        {
            return REGMGR_ERR_REGION_INDEX;
        }

        GuestMemory.WriteCString(buf, RegionCodes[index - 1], size);
        return 0;
    }

    // ------------------------------------------------------------------ SceProcessmgr / SceSblRng
    /// <summary>(SceUID pid, SceUInt64 *time): the run time of a process in microseconds (one emulated process clock).</summary>
    [Hle]
    int sceKernelGetRemoteProcessTime(int pid, uint time)
    {
        if (time == 0)
        {
            return KERNEL_ERR_ILLEGAL_ADDR;
        }

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
        if (buf == 0)
        {
            return KERNEL_ERR_ILLEGAL_ADDR;
        }

        if (size > 64)
        {
            return KERNEL_ERR_INVALID_ARGUMENT;
        }

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
    // controllers of a PS TV (none here: the driver's "no such controller" 0x80360202), above 4 out of range
    // (0x8036020B); a negative port passes the range check and finds no controller.
    const int ERR_EXT_NO_PORT = unchecked((int)0x80360202);
    const int ERR_EXT_PORT_RANGE = unchecked((int)0x8036020B);

    static int Port(int port)
    {
        if (port == 0)
        {
            return 0;
        }

        return port > 4 ? ERR_EXT_PORT_RANGE : ERR_EXT_NO_PORT;
    }

    [Hle] int sceMotionInitLibraryExt(int port) => 0;
    [Hle] int sceMotionTermLibraryExt(int port) => 0;
    [Hle] int sceMotionStartSamplingExt(int port) => port == 0 ? sceMotionStartSamplingThroughEntry() : Port(port);
    [Hle] int sceMotionStopSamplingExt(int port) => port == 0 ? sceMotionStopSamplingThroughEntry() : Port(port);
    [Hle] int sceMotionResetExt(int port) => port == 0 ? sceMotionResetThroughEntry() : Port(port);
    [Hle] int sceMotionSetTiltCorrectionExt(int port, int e) => port == 0 ? sceMotionSetTiltCorrectionThroughEntry(e) : Port(port);
    [Hle] int sceMotionSetDeadbandExt(int port, int e) => port == 0 ? sceMotionSetDeadbandThroughEntry(e) : Port(port);
    /// <summary>(int port, SceMotionState *state): sceMotionGetState of a port; for an external port the driver checks
    /// the range (0x8036020B), then the pointer (0x80360209), then the controller (0x80360202).</summary>
    [Hle("sceMotion_D87E2B64", Nid = 0xD87E2B64, Library = "SceMotion")]
    int sceMotionGetStateExt(int port, uint state)
    {
        if (port == 0)
        {
            return sceMotionGetStateThroughEntry(state);
        }

        if (port <= 4 && state == 0)
        {
            return ERR_NULL_PARAMETER;
        }

        return Port(port);
    }
    /// <summary>(SceMotionState *state): the location service's sceMotionGetState: driver_us 3.74 answers it only while
    /// that client's sampling is on (NID_ACEFA121; 0x80360206 before), a NULL state 0x80360209.</summary>
    [Hle]
    int sceMotionGetStateInternal(uint state) => WriteClientState(_locationSampling, state);

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
    [Hle] int sceMotionDevGetDeviceLocation(uint loc) => loc == 0 ? ERR_DEV_NULL : sceMotionGetDeviceLocationThroughEntry(loc);
    /// <summary>(void *bias 0x10): the gyro bias the driver measured at rest: none on the emulated (ideal) gyro.</summary>
    [Hle]
    int sceMotionDevGetGyroBias(uint bias)
    {
        if (bias == 0)
        {
            return ERR_DEV_NULL;
        }
        GuestMemory.Fill(bias, 0, 0x10);
        return 0;
    }
    /// <summary>(void *calib 0x60): the accelerometer calibration record; empty without factory data.</summary>
    [Hle]
    int sceMotionDevGetAccCalibData(uint calib)
    {
        if (calib == 0)
        {
            return ERR_DEV_NULL;
        }
        GuestMemory.Fill(calib, 0, 0x60);
        return 0;
    }
    /// <summary>(int port, uint8_t *type): the controller the motion data comes from: 0, the built-in sensors.</summary>
    [Hle]
    int sceMotionDevGetControllerType(int port, uint type)
    {
        if (type == 0)
        {
            return ERR_DEV_NULL;
        }
        GuestMemory.Write(type, (byte)0);
        return 0;
    }
    /// <summary>(void *buf, int size &lt;= 0x40, int count): raw sensor records, as sceMotionDevReadForMagnCalib.</summary>
    [Hle] int sceMotionDevRead(uint buf, int size, int count) => buf == 0 || count == 0 || size > 0x40 ? ERR_DEV_NULL : sceMotionDevReadForMagnCalibThroughEntry(buf, size, count);
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
        if (sys == 0 || list == 0)
        {
            return INVALID_ARG;
        }

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
    // ---- the FIOS2 kernel overlay syscalls of the "02" set (SceFios2Kernel02, FW 3.74 os0:bootimage SceFios2Kernel):
    // the same overlay table, but the output arguments travel in an option structure (psp2/fios2kernel02.h). The
    // SceFios2User exports of driver_us (sceFiosOverlayGetList02, ...) take the expanded arguments and build that
    // structure on their stack before the syscall; these entries are the syscalls themselves.

    /// <summary>SCE_KERNEL_ERROR: a buffer size above the syscall's kernel buffer, or an input path without its
    /// terminator inside 0x400 bytes.</summary>
    const int ERR_KERNEL_BUFFER = unchecked((int)0x8002710B);
    const uint OverlayListBufferSize = 0x200, OverlayPathBufferSize = 0x400;

    /// <summary>(pid, min_order, max_order, opt): opt (0x18) = { out_ids, max_ids, SceSize *actual_ids,
    /// out_ids_buffer_size, reserved[2] }. The ids are listed into a zeroed 0x200-byte buffer and exactly
    /// out_ids_buffer_size bytes of it are copied to out_ids; actual_ids, when given, gets the number of matches.</summary>
    [Hle]
    int sceFiosKernelOverlayGetList02(int pid, uint minOrder, uint maxOrder, uint opt)
    {
        if (opt == 0)
        {
            return ERR_FIOS;
        }

        uint outIds = GuestMemory.R32(opt);
        uint maxIds = GuestMemory.R32(opt + 4);
        uint actual = GuestMemory.R32(opt + 8);
        uint copySize = GuestMemory.R32(opt + 0xC);
        if (outIds != 0 && copySize > OverlayListBufferSize)
        {
            return ERR_KERNEL_BUFFER;
        }

        Span<byte> ids = stackalloc byte[(int)OverlayListBufferSize];
        ids.Clear();
        uint count = 0;
        lock (_overlays)
        {
            foreach (Overlay overlay in _overlays)
            {
                if (overlay.Order < (minOrder & 0xFF) || overlay.Order > (maxOrder & 0xFF))
                {
                    continue;
                }

                if (count < maxIds && (count + 1) * 4 <= OverlayListBufferSize)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(ids[(int)(count * 4)..], overlay.Id);
                }
                count++;
            }
        }
        if (outIds != 0)
        {
            ids[..(int)copySize].CopyTo(GuestMemory.Span(outIds, (int)copySize));
        }

        if (actual != 0)
        {
            GuestMemory.W32(actual, count);
        }

        return 0;
    }

    /// <summary>(scheduler_count, partially_resolved_path, opt): opt (8 bytes) is copied in and not used.</summary>
    [Hle] int sceFiosKernelOverlayGetRecommendedScheduler02(int param, string path) => sceFiosOverlayGetRecommendedScheduler02(param, path);

    /// <summary>(pid, resolve_for_write, in_path, opt): opt (0x18) = { out_path, max_path, reserved0,
    /// out_path_buffer_size, reserved1[2] }.</summary>
    [Hle]
    int sceFiosKernelOverlayResolveSync02(int pid, int mode, uint inPath, uint opt)
    {
        if (opt == 0)
        {
            return ERR_FIOS;
        }

        return ResolveIntoOption(inPath, GuestMemory.R32(opt), GuestMemory.R32(opt + 4), GuestMemory.R32(opt + 0xC), 0, uint.MaxValue);
    }

    /// <summary>(pid, resolve_for_write, in_path, opt): opt (0x1C) = { out_path, max_path, u8 min_order, u8 max_order,
    /// reserved0[2], reserved1, out_path_buffer_size, reserved2[2] }.</summary>
    [Hle]
    int sceFiosKernelOverlayResolveWithRangeSync02(int pid, int mode, uint inPath, uint opt)
    {
        if (opt == 0)
        {
            return ERR_FIOS;
        }

        uint minOrder = GuestMemory.Read<byte>(opt + 8);
        uint maxOrder = GuestMemory.Read<byte>(opt + 9);
        return ResolveIntoOption(inPath, GuestMemory.R32(opt), GuestMemory.R32(opt + 4), GuestMemory.R32(opt + 0x10), minOrder, maxOrder);
    }

    /// <summary>The 02 resolve syscalls: the input path must end inside 0x400 bytes; the result is built in a zeroed
    /// 0x400-byte buffer (at most max_path bytes, terminator included) and exactly out_path_buffer_size bytes of it
    /// are copied to out_path. A null out_path resolves without output.</summary>
    int ResolveIntoOption(uint inPath, uint outPath, uint maxPath, uint copySize, uint minOrder, uint maxOrder)
    {
        string path = "";
        if (inPath != 0)
        {
            path = GuestMemory.ReadCString(inPath, (int)OverlayPathBufferSize);
            if (path.Length >= OverlayPathBufferSize)
            {
                return ERR_KERNEL_BUFFER;
            }
        }
        if (outPath == 0)
        {
            return 0;
        }

        if (copySize > OverlayPathBufferSize)
        {
            return ERR_KERNEL_BUFFER;
        }

        Span<byte> buffer = stackalloc byte[(int)OverlayPathBufferSize];
        buffer.Clear();
        int limit = (int)Math.Min(maxPath, OverlayPathBufferSize);
        if (limit > 0)
        {
            byte[] resolved = System.Text.Encoding.UTF8.GetBytes(Resolve(path, minOrder, maxOrder));
            resolved.AsSpan(0, Math.Min(resolved.Length, limit - 1)).CopyTo(buffer);
        }
        buffer[..(int)copySize].CopyTo(GuestMemory.Span(outPath, (int)copySize));
        return 0;
    }
}
