using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// User wrappers and small device services of FW 3.74: avcodec_us.suprx and driver_us.suprx forward these to the
// kernel syscalls of the same name with an underscore (or, for the SceAvcodecUser entries, to the same kernel entry),
// arguments unchanged; the kernel side (bootimage SceAVConfig, SceCtrl, SceUdcd, SceHid, SceCodecEngineWrapper)
// answers for a handheld PCH-1000 without HDMI, USB host devices or external controllers.

public sealed partial class Videodec
{
    [Hle("sceAvcdecCsc")] int CscUser(uint dst, uint src) => Csc(dst, src);
    [Hle("sceM4vdecCsc")] int M4vCscUser(uint dst, uint src) => Csc(dst, src);
    [Hle("sceAvcdecDecodeNalAu")] int DecodeNalAuUser(uint ctrl, uint au, uint array) => DecodeNalAu(ctrl, au, array);
    [Hle("sceAvcdecDecodeNalAuWithWorkPicture")] int DecodeNalAuWithWorkUser(uint ctrl, uint au, uint work, uint array) => DecodeNalAu(ctrl, au, array);
    [Hle("sceAvcdecDecodeAuNalAuNongameapp")] int DecodeAuNalAuUser(uint ctrl, uint au, uint ready) => DecodeAuNalAu(ctrl, au, ready);
    [Hle("sceAvcdecDecodeWithWorkPicture")] int DecodeWithWorkUser(uint ctrl, uint au, uint work, uint array) => sceAvcdecDecode(ctrl, au, array);
    [Hle("sceAvcdecDecodeSetUserDataSei1FieldMemSizeNongameapp")] int SetUserDataSeiMemSizeUser(uint ctrl, int size) => SetMode(ctrl, 5, size);
    [Hle("sceAvcdecGetSeiUserDataNongameapp")] int GetSeiUserDataUser(uint ctrl, uint data, uint info) => GetSeiUserData(ctrl, data, info);
    [Hle("sceAvcdecRegisterCallbackNongameapp")] int RegisterCallbackUser(uint ctrl, uint cb, uint arg) => RegisterCallback(ctrl, cb, arg);
    [Hle("sceAvcdecSetRecoveryPointSEIMode")] int SetRecoveryPointSeiModeUser(uint ctrl, int mode) => SetMode(ctrl, 2, mode);
    [Hle("sceAvcdecUnregisterCallbackNongameapp")]
    [Hle("sceAvcdecUnregisterCallbackWithCbidInternal")]
    [Hle("sceAvcdecUnregisterCallbackWithCbidNongameapp")]
    int UnregisterCallbackUser(uint ctrl, uint cbid) => UnregisterCallback(ctrl, cbid);
    [Hle("sceM4vdecCreateDecoder")] int M4vCreateDecoderUser(uint codecType, uint ctrl, uint query) => M4vCreateDecoder(codecType, ctrl, query);
    [Hle("sceM4vdecDecodeStop")] int M4vDecodeStopUser(uint ctrl, uint array) => sceAvcdecDecodeStop(ctrl, array);
    [Hle("sceVideodecQueryInstanceNongameapp")] int QueryInstanceUser(uint query) => QueryInstance(query);
}

public sealed partial class AppMgr
{
    // driver_us.suprx: the same arguments to the syscall (a scratch option block added where the kernel reads one)
    [Hle("sceAppMgrAppMount")] int AppMountUser(uint param) => _sceAppMgrAppMount(param, 0);
    [Hle("sceAppMgrAppUmount")] int AppUmountUser(uint mountPoint) => _sceAppMgrAppUmount(mountPoint, 0);
    [Hle("sceAppMgrUmountByPid")] int UmountByPidUser(uint param) => _sceAppMgrUmountByPid(param);
    [Hle("sceAppMgrGetUserDirPathById")] int GetUserDirPathByIdUser(uint param, uint path) => _sceAppMgrGetUserDirPathById(param, path);
    [Hle("sceAppMgrGetVs0UserDataDrive")] int GetVs0UserDataDriveUser(uint id) => _sceAppMgrGetVs0UserDataDrive(id);
    [Hle("sceAppMgrGetVs0UserModuleDrive")] int GetVs0UserModuleDriveUser(uint id) => _sceAppMgrGetVs0UserDataDrive(id);
    [Hle("sceAppMgrGetSaveDataInfoForSpecialExport")] int GetSaveDataInfoForSpecialExportUser(uint path, uint accountId) => _sceAppMgrGetSaveDataInfoForSpecialExport(path, accountId, 0);
}

public sealed partial class AppMgrShell
{
    [Hle("sceAppMgrGetCurrentBgmState")] int GetCurrentBgmStateUser(uint pid, uint state) => _sceAppMgrGetCurrentBgmState(pid, state);
    [Hle("sceAppMgrGetFgAppInfo")] int GetFgAppInfoUser(uint info) => _sceAppMgrGetFgAppInfo(info);
    [Hle("sceAppMgrGetRawPathOfApp0ByPidForShell")] int GetRawPathOfApp0ByPidUser(int pid, uint path) => _sceAppMgrGetRawPathOfApp0ByPidForShell(pid, path);
    [Hle("sceAppMgrLaunchVideoStreamingApp")] int LaunchVideoStreamingAppUser(uint extra, uint size, uint opt) => _sceAppMgrLaunchVideoStreamingApp(extra, size, opt);

    /// <summary>(SceUID appId): the shell ends an application by application id (as DestroyAppByAppId, shell only).</summary>
    [Hle]
    int sceAppMgrDestroyOtherAppByAppIdForShell(int appId)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        if (Emu.Vita.ByAppId(appId) is not { IsShell: false } p) return ERR_NOT_FOUND;
        p.Terminate("ended by the shell (sceAppMgrDestroyOtherAppByAppIdForShell)");
        return 0;
    }

    /// <summary>Whether an application other than the shell and the caller runs.</summary>
    [Hle] int sceAppMgrIsOtherAppPresent() => Emu.Vita.Processes.Any(p => !p.IsShell && p != Emu) ? 1 : 0;

    /// <summary>(int state): the form 1 of the impose (system menu) state, kept in the state block like form 2.</summary>
    [Hle] int sceAppMgrSetSystemImposeState(int state) { SetAppMgrState(0x14, state); return 0; }

    /// <summary>(int port, int a, int b): form 1 of taking the audio output exclusively.</summary>
    [Hle] int sceAppMgrAcquireSoundOutExclusive(int port, int a, int b) => sceAppMgrAcquireSoundOutExclusive2(port, a, b, 0);
    [Hle] int sceAppMgrReleaseSoundOutExclusive(int port) => sceAppMgrReleaseSoundOutExclusive2(port);

    /// <summary>(int priority): the music player takes the BGM port (the shell's playback).</summary>
    [Hle] int sceAppMgrAcquireBgmPortForMusicPlayer(int priority) { Emu.BgmPriority = 0x82; return 0; } // fixed 0x82 (appmgr 0x81007248)

    /// <summary>(int appId, int priority): BGM priority under a system chat (kept like SetBgmSubPriority).</summary>
    [Hle] int sceAppMgrSetBgmSubPriorityForSystemChat(int appId, int priority) => sceAppMgrSetBgmSubPriority(appId, priority);

    /// <summary>(int mode): the shell tells appmgr the LiveArea mode (form of NotifyLiveBoardModeForShell).</summary>
    [Hle] int sceAppMgrSendLiveBoardMode(int mode) => sceAppMgrNotifyLiveBoardModeForShell(mode);

    /// <summary>(SceUID appId, int state): the info bar state of another application, set by the shell.</summary>
    [Hle]
    int sceAppMgrSetInfobarStateForShellByAppId(int appId, int visible, int color, int transparency)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        return Emu.Vita.ByAppId(appId) == null ? ERR_NOT_FOUND : 0;
    }
}

public sealed partial class SystemServicesVsh
{
    // ---- SceDrmBridge (driver_us user entries and the kernel bridge): PS Vita TV / Remote Play DRM helpers
    /// <summary>(SceRtcTick *tick): the trusted clock (as _vshRtcGetCurrentSecureTick).</summary>
    [Hle("sceDrmBridgeGetCurrentSecureTick")]
    [Hle("_sceDrmBridgeGetCurrentSecureTick")]
    int DrmBridgeGetCurrentSecureTick(uint tick)
    {
        if (tick == 0) return unchecked((int)0x80251000);
        GuestMemory.Write(tick, Rtc.NowTick);
        return 0;
    }

    /// <summary>The Remote Play debug QA flag: off on a retail unit.</summary>
    [Hle("sceDrmBridgeIsAllowRemotePlayDebug")]
    [Hle("_sceDrmBridgeIsAllowRemotePlayDebug")]
    int DrmBridgeIsAllowRemotePlayDebug() => 0;

    /// <summary>The "mlnpsnl" authentication with the game card's security chip: no game card in the slot
    /// (0x80870001, as _vshSblGcAuthMgrMlnpsnlAuth1).</summary>
    [Hle("sceDrmBridgeMlnpsnlAuth1")]
    [Hle("_sceDrmBridgeMlnpsnlAuth1")]
    [Hle("sceDrmBridgeMlnpsnlAuth2")]
    [Hle("_sceDrmBridgeMlnpsnlAuth2")]
    int DrmBridgeMlnpsnlAuth(uint request, uint tick, uint response, uint opt) => unchecked((int)0x80870001);

    // ---- SceHid: consumer-control (media keys) and mouse hints; nothing is attached
    [Hle("sceHidConsumerControlEnumerate")] int HidConsumerEnumerate(uint handles, int count) => 0;
    [Hle("sceHidConsumerControlRead")] int HidConsumerRead(uint handle, uint reports, int n) => 0;
    [Hle("sceHidConsumerControlRegisterEnumHintCallback")]
    [Hle("sceHidConsumerControlRegisterReadHintCallback")]
    [Hle("sceHidConsumerControlUnregisterEnumHintCallback")]
    [Hle("sceHidConsumerControlUnregisterReadHintCallback")]
    [Hle("sceHidMouseRegisterEnumHintCallback")]
    [Hle("sceHidMouseRegisterReadHintCallback")]
    [Hle("sceHidMouseUnregisterEnumHintCallback")]
    [Hle("sceHidMouseUnregisterReadHintCallback")]
    int HidHintCallback(int cb) => 0;

    /// <summary>(int *intercept): whether keyboard input goes to the caller (sceHidKeyboardSetIntercept), 0 by default.</summary>
    [Hle]
    int sceHidKeyboardGetIntercept(uint intercept)
    {
        if (intercept == 0) return unchecked((int)0x80010016);
        GuestMemory.W32(intercept, 0);
        return 0;
    }

    // ---- SceUdcd: the USB device side (cable unplugged); system programs only (0x80010058)
    /// <summary>(SceUdcdDeviceInfo *info 0x40): the info of the active driver: none is active (0x8024300A).</summary>
    [Hle] int sceUdcdGetDeviceInfo(uint info) => Emu.IsSystemProgram ? unchecked((int)0x8024300A) : unchecked((int)0x80010058);
    /// <summary>(const char *driverName): the state of a USB function driver: not registered/active (0x8024300A).</summary>
    [Hle] int sceUdcdGetDrvState(uint name) => Emu.IsSystemProgram ? unchecked((int)0x8024300A) : unchecked((int)0x80010058);
}

public sealed partial class VshBridge
{
    /// <summary>(int *online): the AC adapter is connected (scePowerIsPowerOnline): yes.</summary>
    [Hle]
    int sceAVConfigGetAcStatus(uint online)
    {
        if (!AvcAllowed) return AVC_ERR_PERM;
        if (online == 0) return AVC_ERR_COPY;
        GuestMemory.W32(online, 1);
        return 0;
    }

    [Hle] int sceAVConfigHdmiGetOutScalingRatio(uint ratio) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;

    /// <summary>(int mode): the colour space of the output (PS TV HDMI only on 3.74: 0x803A0109 on a handheld).</summary>
    [Hle] int sceAVConfigSetDisplayColorSpaceMode(int mode) => AvcAllowed ? AVC_ERR_NOT_DOLCE : AVC_ERR_PERM;
}

public sealed partial class CodecEngine
{
    int _rpcCalls, _clock = 166;
    bool _pmon;

    [Hle("_sceCodecEngineOpenUnmapMemBlock")] int OpenUnmapMemBlockSys(uint memBlock, uint size) => sceCodecEngineOpenUnmapMemBlock(memBlock, size);
    [Hle("_sceCodecEngineCloseUnmapMemBlock")] int CloseUnmapMemBlockSys(int uid) => sceCodecEngineCloseUnmapMemBlock(uid);
    [Hle("_sceCodecEngineAllocMemoryFromUnmapMemBlock")] uint AllocMemorySys(int uid, uint size, uint align) => sceCodecEngineAllocMemoryFromUnmapMemBlock(uid, size, align);
    [Hle("_sceCodecEngineFreeMemoryFromUnmapMemBlock")] int FreeMemorySys(int uid, uint p) => sceCodecEngineFreeMemoryFromUnmapMemBlock(uid, p);

    /// <summary>The codec engine's worker cores (the Vita's media processor has 4): the requested count is accepted
    /// within 1..4 (0x80600009 otherwise); decoding here runs on the host either way.</summary>
    [Hle("_sceCodecEngineChangeNumWorkerCores")]
    int ChangeNumWorkerCores(int n) => n is >= 1 and <= 4 ? 0 : ERR_INVALID_VALUE;
    [Hle("_sceCodecEngineChangeNumWorkerCoresDefault")] int ChangeNumWorkerCoresDefault() => 0;
    [Hle("_sceCodecEngineChangeNumWorkerCoresMax")] int ChangeNumWorkerCoresMax() => 0;

    /// <summary>(int mhz): the media processor clock, kept.</summary>
    [Hle("_sceCodecEngineSetClockFrequency")] int SetClockFrequency(int mhz) { if (mhz <= 0) return ERR_INVALID_VALUE; _clock = mhz; return 0; }

    /// <summary>The RPC calls made to the media processor since the last reset: none (decoding runs on the host).</summary>
    [Hle("_sceCodecEngineGetNumRpcCalled")] int GetNumRpcCalled() => _rpcCalls;
    [Hle("_sceCodecEngineResetNumRpcCalled")] int ResetNumRpcCalled() { _rpcCalls = 0; return 0; }

    /// <summary>(SceCodecEngineMemoryState *state): free/used of the open unmapped blocks (+0 free, +4 total).</summary>
    [Hle("_sceCodecEngineGetMemoryState")]
    int GetMemoryState(uint state)
    {
        if (state == 0) return ERR_INVALID_VALUE;
        uint free = 0, total = 0;
        lock (_blocks)
            foreach (var b in _blocks.Values) { total += b.Size; foreach (var f in b.Free.Values) free += f; }
        GuestMemory.W32(state, free);
        GuestMemory.W32(state + 4, total);
        return 0;
    }

    // the performance monitor of the media processor: no load (the work runs on the host's CPU)
    [Hle("sceCodecEnginePmonStart")][Hle("_sceCodecEnginePmonStart")] int PmonStart() { _pmon = true; return 0; }
    [Hle("sceCodecEnginePmonStop")][Hle("_sceCodecEnginePmonStop")] int PmonStop() { _pmon = false; return 0; }
    [Hle("sceCodecEnginePmonReset")][Hle("_sceCodecEnginePmonReset")] int PmonReset() => 0;

    /// <summary>(SceCodecEnginePmonProcessorLoad *load): the average load of the media processor, 0%.</summary>
    [Hle("sceCodecEnginePmonGetProcessorLoad")]
    [Hle("_sceCodecEnginePmonGetProcessorLoad")]
    [Hle("_sceCodecEngineGetProcessorLoad")]
    int GetProcessorLoad(uint load)
    {
        if (load == 0) return ERR_INVALID_VALUE;
        GuestMemory.W32(load, 8);   // size
        GuestMemory.W32(load + 4, 0); // average load (%)
        return 0;
    }
}
