using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// Syscalls (and the SceFios2Kernel02 entries) that the 3.74 user libraries libkernel.suprx and driver_us.suprx only
// forward to, with the caller's registers unchanged (push {r3, lr}; blx; pop): the same implementation as the
// wrapper. Firmware modules loaded LLE call these directly.

public sealed partial class SystemMisc
{
    [Hle] int __sceAppMgrGetAppState(uint state, uint size, uint version) => _sceAppMgrGetAppState(state, size, version);
    [Hle] int _sceAppMgrAppParamGetInt(int pid, int param, Ptr<int> value) => sceAppMgrAppParamGetInt(pid, param, value);
    [Hle] int _sceAppMgrGetBudgetInfo(uint info) => sceAppMgrGetBudgetInfo(info);
    [Hle] int _sceAppMgrReceiveSystemEvent(uint ev) => sceAppMgrReceiveSystemEvent(ev);
    [Hle] int _sceAppMgrSetNetworkDisconnectionWarningDialogState(int state) => sceAppMgrSetNetworkDisconnectionWarningDialogState(state);
    [Hle] int _sceSblACMgrIsGameProgram(Ptr<int> result) => sceSblACMgrIsGameProgram(result);
}

public sealed partial class AppMgr
{
    [Hle] int _sceAppMgrAppDataMount(int id, uint mountPoint) => sceAppMgrAppDataMount(id, mountPoint);
    [Hle] int _sceAppMgrGetAppParam2(uint param) => sceAppMgrGetAppParam2(param);
    [Hle] int _sceAppMgrGetMediaTypeFromDrive(uint drive, Ptr<int> mediaType) => sceAppMgrGetMediaTypeFromDrive(drive, mediaType);
    [Hle] int _sceAppMgrGetRecommendedScreenOrientation(Ptr<uint> orientation) => sceAppMgrGetRecommendedScreenOrientation(orientation);
    [Hle] int _sceAppMgrMmsMount(int id, uint mountPoint) => sceAppMgrMmsMount(id, mountPoint);
    [Hle] int _sceAppMgrReceiveEvent(uint appEvent) => sceAppMgrReceiveEvent(appEvent);
    [Hle] int _sceAppMgrReceiveEventNum(Ptr<int> eventNum) => sceAppMgrReceiveEventNum(eventNum);
    [Hle] int _sceAppMgrSaveDataSlotFileGetParam(uint slot, uint buf) => sceAppMgrSaveDataSlotFileGetParam(slot, buf);
    [Hle] int _sceAppMgrSetBackRenderPortOwner(int appId) => sceAppMgrSetBackRenderPortOwner(appId);
    [Hle] int _sceAppMgrSetRecommendedScreenOrientationForShell(int orientation) => sceAppMgrSetRecommendedScreenOrientationForShell(orientation);
    [Hle] int _sceAppMgrSystemParamGetInt(uint id, Ptr<int> value) => sceAppMgrSystemParamGetInt(id, value);
    [Hle] int _sceAppMgrUmount(uint mountPoint) => sceAppMgrUmount(mountPoint);
    [Hle] int _sceAppMgrWorkDirMount(int id, uint mountPoint) => sceAppMgrWorkDirMount(id, mountPoint);
}

public sealed partial class AppMgrShell
{
    [Hle] int _sceAppMgrGetRawPathOfApp0ByAppIdForShell(int appId, uint path) => sceAppMgrGetRawPathOfApp0ByAppIdForShell(appId, path);
    [Hle] int _sceAppMgrReceiveNotificationRequestForShell(uint request) => sceAppMgrReceiveNotificationRequestForShell(request);
    [Hle] int _sceAppMgrSetPowerSaveMode(int enable, int keepScreen) => sceAppMgrSetPowerSaveMode(enable, keepScreen);
}

public sealed partial class SystemServicesVsh
{
    [Hle] int _sceErrorGetExternalString(uint buf, int error) => sceErrorGetExternalString(buf, error);
    [Hle] int _sceErrorHistoryPostError(uint message, uint titleId, int a2, uint info) => sceErrorHistoryPostError(message, titleId, a2, info);
    [Hle] int _sceErrorHistorySetDefaultFormat(int format) => sceErrorHistorySetDefaultFormat(format);
}

public sealed partial class Fios2User
{
    [Hle] int sceFiosKernelOverlayAddForProcess02(int pid, uint overlay, Ptr<int> outId) => sceFiosOverlayAddForProcess02(pid, overlay, outId);
    [Hle] int sceFiosKernelOverlayGetInfoForProcess02(int pid, int id, uint outOverlay) => sceFiosOverlayGetInfoForProcess02(pid, id, outOverlay);
    [Hle] int sceFiosKernelOverlayModifyForProcess02(int pid, int id, uint overlay) => sceFiosOverlayModifyForProcess02(pid, id, overlay);
    [Hle] int sceFiosKernelOverlayRemoveForProcess02(int pid, int id) => sceFiosOverlayRemoveForProcess02(pid, id);
    [Hle] int sceFiosKernelOverlayThreadIsDisabled02() => sceFiosOverlayThreadIsDisabled02();
    [Hle] int sceFiosKernelOverlayThreadSetDisabled02(int disabled) => sceFiosOverlayThreadSetDisabled02(disabled);
}

public sealed partial class ProcessMgr
{
    [Hle] int _sceKernelGetOpenPsId(uint id) => sceKernelGetOpenPsId(id);
}
