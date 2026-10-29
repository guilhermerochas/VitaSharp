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
