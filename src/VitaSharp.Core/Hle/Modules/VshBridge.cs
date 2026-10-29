using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceVshBridge and the small kernel services next to it (SceSblQafMgr, SceSblPmMgr, SceAVConfig) as the shell uses them:
/// a retail PS Vita (PCH-1000, CEX, no debug/test/tool flags, no 3G, not a PS TV) that has nothing to report. Most of
/// these register a callback or set a hardware mode the emulator has no counterpart for; they succeed. Queries answer
/// what such a unit answers.
/// </summary>
public sealed class VshBridge : HleModule
{
    // ---- unit identity (SceSblAIMgr through the bridge): a retail CEX handheld
    [Hle] int vshSblAimgrIsCEX() => 1;
    [Hle] int vshSblAimgrIsDEX() => 0;
    [Hle] int vshSblAimgrIsTest() => 0;
    [Hle] int vshSblAimgrIsTool() => 0;
    [Hle] int vshSblAimgrIsDolce() => 0;
    [Hle] int vshSblAimgrIsGenuineDolce() => 0;
    [Hle] int vshSblSsIsDevelopmentMode() => 0;
    [Hle] int vshSblQafMgrIsAllowLoadMagicGate() => 0;
    [Hle] int vshSblUtMgrHasStoreFlag() => 0;
    [Hle] int vshSblUtMgrHasComTestFlag() => 0;
    [Hle] int vshSysconIsIduMode() => 0;
    [Hle] int vshSysconHasWWAN() => 0;
    [Hle] int vshMotionNoiseFilterIsAvailable() => 0;

    /// <summary>
    /// (int capability): bit <paramref name="capability"/> of the model capability word SceSysroot keeps (sysroot +0x84,
    /// ksceSysrootCheckModelCapability in os0:kd/sysmem.skprx: (word >> cap) &amp; 1). libpaf asks for 1, 2, 5..10 and
    /// keeps the answers as flags (ScePaf+0x1E4BE8); for a game program it does not ask and uses 0x4A, i.e.
    /// capabilities 2, 7 and 10, the handheld's set. A PCH-1000 answers that set too.
    /// </summary>
    [Hle] int vshKernelCheckModelCapability(int capability) => capability is 2 or 7 or 10 ? 1 : 0;

    /// <summary>(int event): the shell tells SceSysroot it is up (event 0, vitasdk vshbridge.h); nothing waits on it here.</summary>
    [Hle] int vshKernelSendSysEvent(int ev) => 0;

    /// <summary>
    /// (int leaf, int offset, void *buf, int size): reads part of an ID storage leaf (the bridge checks size and offset
    /// &lt; 0x200 and calls ksceIdStorageLookup). The shell reads 4 bytes of leaf 0x116 at boot and uses them only when
    /// byte 0 is non-zero; on the emulated unit the leaf holds no settings, so it reads as zeros.
    /// </summary>
    [Hle]
    int vshIdStorageLookup(int leaf, int offset, uint buf, int size)
    {
        if ((uint)size >= 0x200 || (uint)offset >= 0x200) return unchecked((int)0x80010016);
        if (buf != 0 && size > 0) GuestMemory.Fill(buf, 0, (uint)size);
        return 0;
    }

    // ---- QA / DRM / syscon flags of a retail unit
    /// <summary>Retail units keep no core dumps (no QA flag); the shell then skips its core-file housekeeping.</summary>
    [Hle] int vshSblQafMgrIsAllowKeepCoreFile() => 0;
    /// <summary>Account binding is strict on a retail unit (the shell compares with 1).</summary>
    [Hle] int vshNpDrmIsLooseAccountBind() => 0;
    /// <summary>No memory-card emulation hardware (a PCH-1000 has the physical card slot).</summary>
    [Hle] int vshSysconIsMCEmuCapable() => 0;

    // ---- input idle detection, RTC alarms, clock: no hardware counterpart, the calls succeed
    /// <summary>(uint keys, uint keys2, int, int): buttons that cancel the auto-dim timer.</summary>
    [Hle] int vshCtrlSetIdleCancelKey(uint keys, uint keys2, int a, int b) => 0;
    /// <summary>(int leftStick, int rightStick): stick travel that cancels the auto-dim timer.</summary>
    [Hle] int vshCtrlSetIdleCancelThreshold(int left, int right) => 0;
    /// <summary>(int id): the shell drops a secure-clock alarm it registered (0, or 0x80251004 "not registered", both fine).</summary>
    [Hle] int vshRtcUnregisterSecureAlarmCallback(int id) => 0;
    /// <summary>(SceRtcTick *tick): setting the clock (date and time settings). The emulated clock follows the host's.</summary>
    [Hle] int _vshRtcSetCurrentTick(uint tick) => tick == 0 ? unchecked((int)0x80251000) : 0;
    /// <summary>(SceUID fd, int op): an advisory file lock between processes; VitaSharp's processes share one host
    /// file system and the shell only uses it around its own database files.</summary>
    [Hle] int vshIoFlock(int fd, int op) => 0;

    // ---- application services
    /// <summary>(const SceVshAppMgrBgdlQueueStatus *status): the background download queue table (0x700 bytes, copied
    /// by the kernel). No downloads run, so there is nothing to act on.</summary>
    [Hle] int _vshAppMgrBgdlSetQueueStatus(uint status) => status == 0 ? unchecked((int)0x80802016) : 0;
    /// <summary>The PFS (protected file system) process status the daily checker polls: 0, no PFS work pending.</summary>
    [Hle] int vshAppMgrGetPfsProcessStatus() => 0;
    /// <summary>(int, SceUInt64 *mcId): the memory card's cloud-data (PS Plus online storage) id, 8 bytes the kernel
    /// copies out (bridge 0x81004F30). The emulated card was never registered for online storage: 0.</summary>
    [Hle] int _vshAppMgrCloudDataGetMcId(int a, uint mcId) { if (mcId != 0) GuestMemory.Write(mcId, 0UL); return 0; }

    // ---- QA flags (SceSblQafMgr): none set on a retail unit
    [Hle] int sceSblQafMgrIsAllowRemoteSysmoduleLoad() => 0;
    [Hle] int sceSblQafMgrIsAllowNpTest() => 0;
    [Hle] int sceSblQafMgrIsAllowScreenShotAlways() => 0;

    /// <summary>(int *mode): the product mode of the security processor; 0 = production.</summary>
    [Hle] int sceSblPmMgrGetCurrentMode(Ptr<int> mode) { mode.TrySet(0); return 0; }

    // ---- power, RTC, display, LEDs: modes and callbacks without an emulated counterpart
    [Hle] int vshPowerSetPowerSwMode(int mode, int time) => 0;
    [Hle] int vshPowerSetPsButtonPushTime(int time) => 0;
    /// <summary>Why the unit woke up: 0, a cold boot.</summary>
    [Hle] int vshPowerGetWakeupFactor() => 0;
    [Hle] int _vshRtcSetAlarmTick(uint tick, int enable) => 0;
    [Hle] int vshRtcSetConf(int a, int b) => 0;
    [Hle] int vshRtcRegisterCallback(int cb) => 0;
    [Hle] int _vshAppMgrSystemParamDateTimeSetConf(uint conf) => 0;
    [Hle] int vshDisplaySetInvertColors(int head, int invert) => 0;
    [Hle] int vshDisplayRegisterFrameBufCallback(int cb) => 0;
    [Hle] int _vshLedSetMode(int led, int mode, uint conf) => 0;
    [Hle] int vshCtrlRegisterNotifyCallBack(int cb) => 0;

    /// <summary>(SceRtcTick *tick): the trusted clock, here the host's UTC time in microseconds since year 1.</summary>
    [Hle]
    int _vshRtcGetCurrentSecureTick(uint tick)
    {
        if (tick == 0) return unchecked((int)0x80251000);
        GuestMemory.Write(tick, (ulong)(DateTime.UtcNow.Ticks / 10));
        return 0;
    }

    // ---- storage events: the memory card is always inserted, the game card slot empty; nothing ever fires
    [Hle] int vshIoCreateMountEvent(int mountId, int eventType, uint callback, uint arg) => 0;
    [Hle] int vshIoCreateErrorEvent(int errorType, int a1, uint callback, uint arg) => 0;

    // ---- audio/video configuration (SceAVConfig)
    [Hle] int sceAVConfigDisplayOn() => 0;
    [Hle] int sceAVConfigRegisterCallback(int cb) => 0;
    [Hle] int sceAVConfigSetAutoSuspend2(int a) => 0;
    [Hle] int sceAVConfigClearAutoSuspend2(int a) => 0;
    /// <summary>(int *enable, ...): the volume buttons work.</summary>
    [Hle] int sceAVConfigGetVolCtrlEnable(Ptr<int> enable, Ptr<int> unk) { enable.TrySet(1); unk.TrySet(0); return 0; }
    /// <summary>(int *volume): the system volume, 0..30 (the handheld's default 20 until the registry says otherwise).</summary>
    [Hle] int sceAVConfigGetSystemVol(Ptr<int> volume) { volume.TrySet(20); return 0; }
}
