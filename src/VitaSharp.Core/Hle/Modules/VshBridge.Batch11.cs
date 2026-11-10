using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// More of SceVshBridge (os0:kd/bootimage.skprx SceVshBridge, FW 3.74): each entry checks that the caller is a system
// program (0x80010058 otherwise), copies its arguments in, calls the kernel function and copies the result out. The
// ones over services the HLE has (files, RTC, the unit's identity) are real; the ones over hardware the emulated unit
// does not have (the syscon microcontroller's logs, show mode and configstorage scripts, the ID storage's factory
// flash, HDMI CEC of the PS TV, the memory-card slot) answer what the kernel function answers with that part idle or
// absent, and are marked default.
public sealed partial class VshBridge
{
    const int VSH_ERR_PERM = unchecked((int)0x80010058), VSH_ERR_EINVAL = unchecked((int)0x80010016),
        VSH_ERR_NOT_DOLCE = unchecked((int)0x8037000E);

    bool Vsh => Emu.IsSystemProgram;
    IoFileMgr Io => Emu.Hle.Modules.OfType<IoFileMgr>().First();

    int VshOut(uint p, int size, Action<uint> fill)
    {
        if (!Vsh) return VSH_ERR_PERM;
        if (p == 0) return ERR_BAD_ADDR;
        GuestMemory.Fill(p, 0, (uint)size);
        fill(p);
        return 0;
    }

    // ---- files (the bridge's copies of sceIoGetstat / sceIoDread for system programs)

    /// <summary>(const char *path, SceIoStat *stat 0x58, void *opt 8): sceIoGetstat.</summary>
    [Hle]
    int _vshIoGetstat(uint path, uint stat, uint opt)
    {
        if (!Vsh) return VSH_ERR_PERM;
        if (path == 0) return ERR_BAD_ADDR;
        return Io.sceIoGetstat(GuestMemory.ReadCString(path, 0x400), stat);
    }

    /// <summary>(SceUID fd, SceIoDirent *dirent 0x160): sceIoDread.</summary>
    [Hle] int _vshIoDread(int fd, uint dirent) => !Vsh ? VSH_ERR_PERM : dirent == 0 ? ERR_BAD_ADDR : Io.sceIoDread(fd, dirent);

    /// <summary>The I/O scheduling priority new processes get (the one the system set, 0 at boot).</summary>
    [Hle] int vshIoGetProcessDefaultPriorityForSystem() => Vsh ? _ioDefaultPriority : VSH_ERR_PERM;
    int _ioDefaultPriority;
    [Hle] int vshIoSetProcessDefaultPriorityForSystem(int priority) { if (!Vsh) return VSH_ERR_PERM; _ioDefaultPriority = priority; return 0; }
    /// <summary>(SceUID evid): clears an I/O error event; events are not raised by the HLE's devices.</summary>
    [Hle] int vshIoClearErrorEvent(int evid, uint a, uint b, uint c) => Vsh ? 0 : VSH_ERR_PERM;

    // ---- RTC

    /// <summary>(SceRtcTick *tick, int *enabled): the wake-up alarm (none set: 0, disabled).</summary>
    [Hle]
    int _vshRtcGetAlarmTick(uint tick, uint enabled)
    {
        if (!Vsh) return VSH_ERR_PERM;
        if (tick != 0) GuestMemory.Write(tick, 0UL);
        if (enabled != 0) GuestMemory.W32(enabled, 0);
        return 0;
    }

    [Hle] int vshRtcIsAlarmed() => Vsh ? 0 : VSH_ERR_PERM;
    /// <summary>(SceRtcTick *tick): the secure alarm _vshRtcSetSecureAlarmTick set.</summary>
    [Hle] int _vshRtcGetSecureAlarmTick(uint tick) => VshOut(tick, 8, p => GuestMemory.Write(p, _secureAlarm));

    // ---- the unit's identity

    /// <summary>(SceKernelPscode *pscode): as _vshSblAimgrGetPscode (the secure module's copy).</summary>
    [Hle] int _vshSblAimgrGetPscode2(uint pscode) => !Vsh ? VSH_ERR_PERM : _vshSblAimgrGetPscode(pscode);
    /// <summary>(char visibleId[0x20]): the printed id of the unit (none in the emulated unit's ID storage: zeros).</summary>
    [Hle(Default = true)] int _vshSblAimgrGetVisibleId(uint id) => VshOut(id, 0x20, _ => { });
    /// <summary>(uint *smi): the secure module information word: 0.</summary>
    [Hle(Default = true)] int _vshSblAimgrGetSMI(uint smi) => VshOut(smi, 4, _ => { });
    /// <summary>A genuine PS Vita (not a dev/test kit shell): yes.</summary>
    [Hle] int vshSblAimgrIsGenuineVITA() => Vsh ? 1 : VSH_ERR_PERM;
    /// <summary>The NP test flag of the user token: a retail unit has none.</summary>
    [Hle] int vshSblUtMgrHasNpTestFlag() => Vsh ? 0 : VSH_ERR_PERM;
    /// <summary>(int capability): the caller's capability; the system programs have every capability the shell asks
    /// for (the kernel checks the program's authority id).</summary>
    [Hle] int vshSblACMgrHasCapability(int capability, uint a) => Vsh ? 1 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSblPmMgrSetSdModeOff() => Vsh ? 0 : VSH_ERR_PERM;

    // ---- power and clocks
    [Hle] int vshKernelEnableAutoClockDown() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle] int vshKernelDisableAutoClockDown() => Vsh ? 0 : VSH_ERR_PERM;
    /// <summary>Requests hibernation (deep standby); the emulated unit does not power down.</summary>
    [Hle(Default = true)] int vshPowerRequestHibernate() => Vsh ? 0 : VSH_ERR_PERM;

    // ---- the syscon microcontroller (no syscon in the emulated unit: its logs are empty, its modes off)
    [Hle(Default = true)] int vshSysconEnableHibernateIO(int on) => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSysconGetLogInfo(uint info) => VshOut(info, 8, _ => { });
    [Hle(Default = true)] int vshSysconLogStart() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSysconLogStartWaiting() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSysconShowModeSet() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSysconShowModeClear() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSysconBeginConfigstorageTransaction() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSysconEndConfigstorageTransaction() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSysconCommitConfigstorageTransaction() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSysconLoadConfigstorageScript(uint script, uint size) => script == 0 ? SceError.INVALID_ARGUMENT : Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshSysconVerifyConfigstorageScript(uint script, uint size) => script == 0 ? SceError.INVALID_ARGUMENT : Vsh ? 0 : VSH_ERR_PERM;
    /// <summary>(int *a, int *b, int *c, int *d): the battery calibration words: 0.</summary>
    [Hle(Default = true)]
    int vshSysconGetBatteryCalibData(uint a, uint b, uint c, uint d)
    {
        if (!Vsh) return VSH_ERR_PERM;
        foreach (uint p in new[] { a, b, c, d }) { if (p == 0) return ERR_BAD_ADDR; GuestMemory.W32(p, 0); }
        return 0;
    }
    [Hle(Default = true)] int _vshSysconGetManufacturesStatus(uint status) => VshOut(status, 4, _ => { });
    [Hle(Default = true)] int _vshSysconGetManualChargeMode(uint mode) => VshOut(mode, 4, _ => { });
    [Hle(Default = true)] int _vshSysconCtrlManualChargeMode(int mode) => Vsh ? 0 : VSH_ERR_PERM;
    /// <summary>(uint *status): the USB cable detection: nothing plugged.</summary>
    [Hle(Default = true)] int _vshSysconGetUsbDetStatus(uint status) => VshOut(status, 4, _ => { });
    [Hle(Default = true)] int _vshSysconGetHardwareInfo(uint info) => VshOut(info, 4, _ => { });
    /// <summary>(void *info, const SceSize *size): the extended hardware information (0x10 bytes): zeros.</summary>
    [Hle(Default = true)]
    int _vshSysconGetHardwareInfo2(uint info, uint size)
    {
        if (!Vsh) return VSH_ERR_PERM;
        if (size == 0) return ERR_BAD_ADDR;
        uint n = Math.Min(GuestMemory.R32(size), 0x10u);
        if (info == 0) return VSH_ERR_EINVAL;
        GuestMemory.Fill(info, 0, n);
        return 0;
    }

    // ---- ID storage (the factory flash: formatted, writable, its leaves empty; writes are accepted and dropped)
    [Hle(Default = true)] int vshIdStorageIsFormatted() => Vsh ? 1 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageIsReadOnly() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageIsDirty() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageGetLeafSize() => Vsh ? 0x200 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageGetFreeLeaves() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageFormat() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageUnformat() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageFlush() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageRestart(int a) => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageCreateLeaf(int leaf) => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageDeleteLeaf(int leaf) => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageWriteLeaf(int leaf, uint buf) => buf == 0 ? ERR_BAD_ADDR : Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshIdStorageUpdate(int leaf, uint offset, uint buf, uint size) => buf == 0 ? ERR_BAD_ADDR : Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int _vshIdStorageCreateAtomicLeaves(uint leaves, int count) => Vsh ? 0 : VSH_ERR_PERM;

    // ---- HDMI CEC (PS TV only)
    [Hle(Default = true)] int vshHdmiCecCmdForcedPollingMsg(int a) => Vsh ? VSH_ERR_NOT_DOLCE : VSH_ERR_PERM;
    [Hle(Default = true)] int vshHdmiEnableCec() => Vsh ? VSH_ERR_NOT_DOLCE : VSH_ERR_PERM;
    [Hle(Default = true)] int vshHdmiDisableCec() => Vsh ? VSH_ERR_NOT_DOLCE : VSH_ERR_PERM;

    // ---- memory card slot
    /// <summary>(int slot): whether a memory card is inserted: the emulated unit has its storage mounted as ux0: and no
    /// card in the slot.</summary>
    [Hle(Default = true)] int vshMemoryCardGetCardInsertState(int slot) => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshRemovableMemoryGetCardInsertState() => Vsh ? 0 : VSH_ERR_PERM;
    [Hle(Default = true)] int vshMemoryCardEnableSlowMode() => 0;
    /// <summary>(int slot, void *info 0x68): the SD/MMC card information: no card.</summary>
    [Hle(Default = true)] int vshSdGetCardInfo(int slot, uint info) => Vsh ? unchecked((int)0x80010013) : VSH_ERR_PERM;

    // ---- the rest
    /// <summary>Tells the application manager the debug settings changed (it rereads them).</summary>
    [Hle] int vshAppMgrDebugSettingNotifyUpdate(int a) => Vsh ? 0 : VSH_ERR_PERM;
    [Hle("_vshCoredumpCafCreateIv", Default = true)]
    [Hle("_vshSblSsEncryptWithPortability", Default = true)]
    [Hle("_vshSblGcAuthMgrMlnpsnlAuth2", Default = true)]
    [Hle("_vshNpDrmEbootSigConvert", Default = true)]
    [Hle("_vshNpDrmPspEbootSigGen", Default = true)]
    [Hle("_vshSDfCtlSetBootParameter", Default = true)]
    int vshSecureService() => Vsh ? unchecked((int)0x80020004) : VSH_ERR_PERM;
}
