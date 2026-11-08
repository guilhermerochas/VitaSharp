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

// Syscalls that driver_us.suprx 3.74 wraps with a scratch struct as the last argument: the arguments that fit in
// registers go straight through, the others (and for the save data calls, the size word of the parameter struct
// the kernel copies) are stored in the struct (opt+0, +4, +8). Same implementation as the wrapper.

public sealed partial class AppMgr
{
    [Hle] int _sceAppMgrAppParamSetString(uint a0, uint a1, uint a2, uint scratch) => sceAppMgrAppParamSetString(a0, a1, a2);
    [Hle] int _sceAppMgrCheckRifGD(uint appPath, uint info, uint opt, uint scratch) => sceAppMgrCheckRifGD(appPath, info, opt);
    [Hle] int _sceAppMgrConvertVs0UserDrivePath(uint src, uint dst, int len, uint scratch) => sceAppMgrConvertVs0UserDrivePath(src, dst, len);
    [Hle] int _sceAppMgrGetAppParam(uint param, uint scratch) => sceAppMgrGetAppParam(param);
    [Hle] int _sceAppMgrGetDevInfo(uint dev, Ptr<ulong> maxSize, Ptr<ulong> freeSize, uint scratch) => sceAppMgrGetDevInfo(dev, maxSize, freeSize);
    [Hle] int _sceAppMgrGetIdByName(Ptr<int> pid, uint name, uint scratch) => sceAppMgrGetIdByName(pid, name);
    [Hle] int _sceAppMgrGetNameById(int pid, uint name, uint scratch) => sceAppMgrGetNameById(pid, name);
    [Hle] int _sceAppMgrGetRawPath(uint path, uint resolved, int size, uint scratch) => sceAppMgrGetRawPath(path, resolved, size);
    [Hle] int _sceAppMgrGetUserDirPath(int partition, uint path, uint maxLen, uint scratch) => sceAppMgrGetUserDirPath(partition, path, maxLen);
    [Hle] int _sceAppMgrLaunchAppByUri(int flags, uint uri, uint scratch) => sceAppMgrLaunchAppByUri(flags, uri);
    [Hle] int _sceAppMgrLaunchAppByUri2(uint uri, uint param, uint scratch) => sceAppMgrLaunchAppByUri2(uri, param);
    [Hle] int _sceAppMgrSystemParamGetString(uint id, uint buf, uint size, uint scratch) => sceAppMgrSystemParamGetString(id, buf, size);
    [Hle] int _sceAppMgrWorkDirMountById(int id, string titleId, uint mountPoint, uint scratch) => sceAppMgrWorkDirMountById(id, titleId, mountPoint);
    [Hle] int _sceAppMgrPhotoMount(uint param, uint opt) => sceAppMgrPhotoMount(param);
    [Hle] int _sceAppMgrPhotoUmount(uint param, uint opt) => sceAppMgrPhotoUmount();
    [Hle] int _sceAppMgrSaveDataDataRemove(uint data, uint opt) => sceAppMgrSaveDataDataRemove(data);
    [Hle] int _sceAppMgrSaveDataDataRemove2(uint data, uint opt) => sceAppMgrSaveDataDataRemove2(data);
    [Hle] int _sceAppMgrSaveDataDataSave(uint data, uint opt) => sceAppMgrSaveDataDataSave(data);
    [Hle] int _sceAppMgrSaveDataDataSave2(uint data, uint opt) => sceAppMgrSaveDataDataSave2(data);
    [Hle] int _sceAppMgrSaveDataGetQuota(uint data, uint opt) => sceAppMgrSaveDataGetQuota(data);
    [Hle] int _sceAppMgrSaveDataMount(uint data, uint opt) => sceAppMgrSaveDataMount(data);
    [Hle] int _sceAppMgrSaveDataSlotCreate(uint data, uint opt) => sceAppMgrSaveDataSlotCreate(data);
    [Hle] int _sceAppMgrSaveDataSlotDelete(uint data, uint opt) => sceAppMgrSaveDataSlotDelete(data);
    [Hle] int _sceAppMgrSaveDataSlotGetParam(uint data, uint opt) => sceAppMgrSaveDataSlotGetParam(data);
    [Hle] int _sceAppMgrSaveDataSlotGetStatus(uint data, uint opt) => sceAppMgrSaveDataSlotGetStatus(data);
    [Hle] int _sceAppMgrSaveDataSlotSetParam(uint data, uint opt) => sceAppMgrSaveDataSlotSetParam(data);
    [Hle] int _sceAppMgrSaveDataSlotSetStatus(uint data, uint opt) => sceAppMgrSaveDataSlotSetStatus(data);
    [Hle] int _sceAppMgrSaveDataUmount(uint data, uint opt) => sceAppMgrSaveDataUmount(data);
}

public sealed partial class AppMgrShell
{
    [Hle] int _sceAppMgrDeclareShellProcess2(uint name, uint scratch) => sceAppMgrDeclareShellProcess2(name);
    [Hle] int _sceAppMgrDestroyAppByName(uint name, uint scratch) => sceAppMgrDestroyAppByName(name);
    [Hle] int _sceAppMgrLaunchAppByName(int flags, uint name, uint param, uint scratch) => sceAppMgrLaunchAppByName(flags, name, param);
    [Hle] int _sceAppMgrLaunchAppByName2(uint name, uint param, uint launchParam, uint scratch) => sceAppMgrLaunchAppByName2(name, param, launchParam);

    /// <summary>(appId, path, flags, opt {arg3, launchParam}).</summary>
    [Hle]
    int _sceAppMgrLaunchAppByName2ndStage(int appId, uint path, int flags, uint opt)
        => sceAppMgrLaunchAppByName2ndStage(appId, path, flags, (int)OptWord(opt, 0), OptWord(opt, 1));

    static uint OptWord(uint opt, int i) => opt == 0 ? 0 : GuestMemory.R32(opt + (uint)i * 4);
}

public sealed partial class SystemMisc
{
    [Hle] int _sceAppMgrLoadExec(string path, uint argv, uint opt, uint scratch) => sceAppMgrLoadExec(path, argv, opt);

    /// <summary>(pid, param, buf, opt {size}).</summary>
    [Hle] int _sceAppMgrAppParamGetString(int pid, int param, uint buf, uint opt) => sceAppMgrAppParamGetString(pid, param, buf, opt == 0 ? 0 : GuestMemory.R32(opt));
}

public sealed partial class SystemSoftware
{
    /// <summary>(path, titleId, flags, opt {param, arg4, launchParam}).</summary>
    [Hle]
    int _sceAppMgrLaunchAppByPath4(string path, uint titleId, uint flags, uint opt)
        => opt == 0 ? sceAppMgrLaunchAppByPath4(path, titleId, flags, 0, 0, 0)
            : sceAppMgrLaunchAppByPath4(path, titleId, flags, GuestMemory.R32(opt), GuestMemory.R32(opt + 4), GuestMemory.R32(opt + 8));
}
