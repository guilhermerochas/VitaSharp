using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceVshBridge and the small kernel services next to it (SceSblQafMgr, SceSblPmMgr, SceAVConfig) as the shell uses them:
/// a retail PS Vita (PCH-1000, CEX, no debug/test/tool flags, no 3G, not a PS TV) that has nothing to report. Most of
/// these register a callback or set a hardware mode the emulator has no counterpart for; they succeed. Queries answer
/// what such a unit answers.
/// </summary>
public sealed partial class VshBridge : HleModule
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
    [Hle] int vshSysconIsIduMode() => _iduMode ? 1 : 0;
    [Hle] int vshSysconHasWWAN() => 0;
    /// <summary>A store demo (show mode) unit: no.</summary>
    [Hle] int vshSysconIsShowMode() => 0;
    [Hle] int vshMotionNoiseFilterIsAvailable() => 0;

    /// <summary>(SceKernelPscode *pscode): the unit's 8-byte product code through the bridge (ksceSblAimgrGetPscode):
    /// company 1 (SCE), product 0x104 (CEX, North America: the region the registry manager reports, UC2), sub code
    /// 0x10 (PCH-1000), factory 0. Settings reads it for System Information.</summary>
    [Hle]
    int _vshSblAimgrGetPscode(uint pscode)
    {
        if (pscode == 0) return unchecked((int)0x80010016); // EINVAL: nothing to copy the code to
        GuestMemory.Write(pscode, (ushort)1);
        GuestMemory.Write(pscode + 2, (ushort)0x104);
        GuestMemory.Write(pscode + 4, (ushort)0x10);
        GuestMemory.Write(pscode + 6, (ushort)0);
        return 0;
    }

    /// <summary>(const char *path, int flags, const void *param 8 bytes): the shell registers an application path
    /// with appmgr (a path of 0x40 characters or more: 0x8002710B, like the bridge). Nothing to track here.</summary>
    [Hle]
    int _vshAppMgrRegisterPath(uint path, int flags, uint param)
    {
        if (path != 0 && GuestMemory.ReadCString(path, 0x41).Length >= 0x40) return unchecked((int)0x8002710B);
        return 0;
    }

    /// <summary>(const SceSblSsAppKeyParam *in 0x24, SceSblSsAppKey *out 0x24): a key the security processor derives
    /// from the console's secrets and the request (the Browser and WebKit key their local storage with it). The
    /// unit's secrets do not exist here: the key is derived the same way every time from the request (SHA-256 over
    /// it), so what was stored with it can be read back; the first word (the request kind) is echoed.</summary>
    [Hle]
    int _vshSblSsGenerateAppKey(uint input, uint output)
    {
        if (input == 0 || output == 0) return unchecked((int)0x80010016);
        var req = GuestMemory.Span(input, 0x24).ToArray();
        var key = System.Security.Cryptography.SHA256.HashData([.. req, .. "VitaSharp app key"u8]);
        GuestMemory.W32(output, BitConverter.ToUInt32(req, 0));
        key.CopyTo(GuestMemory.Span(output + 4, 0x20));
        return 0;
    }

    /// <summary>
    /// (int capability): bit <paramref name="capability"/> of the model capability word SceSysroot keeps (sysroot +0x84,
    /// ksceSysrootCheckModelCapability in os0:kd/sysmem.skprx: (word >> cap) &amp; 1). libpaf asks for 1, 2, 5..10 and
    /// keeps the answers as flags (ScePaf+0x1E4BE8); for a game program it does not ask and uses 0x4A, i.e.
    /// capabilities 2, 7 and 10, the handheld's set. A PCH-1000 answers that set too.
    /// </summary>
    [Hle] int vshKernelCheckModelCapability(int capability) => capability is 2 or 7 or 10 ? 1 : 0;

    /// <summary>(int capability): the same sysroot capability word through SceSysmem for user modules (the PS Vita TV
    /// teleport client asks for 10 and fails with 0x8010840A unless the answer is 1).</summary>
    [Hle] int sceKernelCheckModelCapability(int capability) => vshKernelCheckModelCapability(capability);

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
    /// <summary>The "near" (location sharing) test servers are a QA flag: off (NPXS10000 and near_profile ask).</summary>
    [Hle] int vshSblQafMgrIsAllowNearTest() => 0;
    /// <summary>Debugging system applications is a QA flag: off (Content Manager asks at start).</summary>
    [Hle] int vshSblQafMgrIsAllowSystemAppDebug() => 0;
    /// <summary>Changing the liblocation model is a QA flag: off (the shell skips its location debug settings).</summary>
    [Hle] int vshSblQafMgrIsAllowLiblocationChangeModel() => 0;
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
    /// <summary>np_common compares it with 1 to use the NP full-test environment: off on a retail unit.</summary>
    [Hle] int sceSblQafMgrIsAllowNpFullTest() => 0;
    /// <summary>The settings app shows its debug menus (all, limited or minimum) only with these QA flags: off.</summary>
    [Hle] int sceSblQafMgrIsAllowAllDebugMenuDisplay() => 0;
    [Hle] int sceSblQafMgrIsAllowLimitedDebugMenuDisplay() => 0;
    [Hle] int sceSblQafMgrIsAllowMinimumDebugMenuDisplay() => 0;
    /// <summary>Installing a non-QA (unsigned/internal) update file is a QA flag: off.</summary>
    [Hle] int sceSblQafMgrIsAllowNonQAPup() => 0;
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
        GuestMemory.Write(tick, Rtc.TrophyServerTick(Emu) ?? (ulong)(DateTime.UtcNow.Ticks / 10));
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
    /// <summary>(int *volume): the system volume, 0..30: the last sceAVConfigSetSystemVol, else the registry's
    /// /CONFIG/SOUND/main_volume, else the handheld's default 20.</summary>
    [Hle] int sceAVConfigGetSystemVol(Ptr<int> volume) { volume.TrySet(CurrentSystemVolume()); return 0; }
}
