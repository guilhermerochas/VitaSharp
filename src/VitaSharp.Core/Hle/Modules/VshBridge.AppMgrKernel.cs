using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// SceAppMgrForDriver, the entries the VSH bridge calls (os0:bootimage SceAppMgr 3.74, read in the module, study only):
// the bridge checks the shell, copies the user arguments in and calls these. They run the bridge HLE's own state
// (cloud data registration, local backup targets, CAF and header keys) with the kernel's arguments (pointers and
// sizes instead of the bridge's option blocks), and the kernel side's own argument checks: a NULL or oversized
// argument is 0x80800001 where the module checks it.
public sealed partial class VshBridge
{
    /// <summary>SceAppMgr's kernel side: a NULL or oversized argument (0x80800001).</summary>
    const int APPMGR_KERNEL_ERR_ARGUMENT = unchecked((int)0x80800001);

    /// <summary>SceAppMgr's kernel side: a NULL rif info, or a caller that is not the program the call is for
    /// (0x80800009).</summary>
    const int APPMGR_KERNEL_ERR_CALLER = unchecked((int)0x80800009);

    /// <summary>SceAppMgr's kernel side: a NULL download queue status (0x80801001).</summary>
    const int APPMGR_KERNEL_ERR_QUEUE = unchecked((int)0x80801001);

    /// <summary>The largest cloud data the header covers (1 GiB).</summary>
    const uint CloudDataMaxSize = 0x40000000;

    /// <summary>ksceAppMgrAcInstGetAcdirParam(titleId, addcontId, result): as the bridge's call.</summary>
    [Hle(Nid = 0x474AABDF, Library = "SceAppMgrForDriver")]
    int ksceAppMgrAcInstGetAcdirParam(uint titleId, uint addcontId, uint result)
    {
        return _vshAppMgrAcInstGetAcdirParam(titleId, addcontId, result);
    }

    /// <summary>ksceAppMgrBgdlSetQueueStatus(status): NULL 0x80801001; the 0x700-byte status is taken; 0.</summary>
    [Hle(Nid = 0x5B1BE482, Library = "SceAppMgrForDriver")]
    int ksceAppMgrBgdlSetQueueStatus(uint status)
    {
        return status == 0 ? APPMGR_KERNEL_ERR_QUEUE : 0;
    }

    /// <summary>ksceAppMgrCheckContentInstallPeriod(pid, ...): as the bridge's call.</summary>
    [Hle(Nid = 0x193784A9, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCheckContentInstallPeriod(int pid)
    {
        return vshAppMgrCheckContentInstallPeriod();
    }

    /// <summary>ksceAppMgrCheckPfsMounted(path): NULL or a path of 0x124 bytes or more 0x80800001; nothing is
    /// PFS-mounted (the emulator decrypts content on install): 0.</summary>
    [Hle(Nid = 0x0B1BEEE0, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCheckPfsMounted(uint path)
    {
        if (path == 0 || GuestMemory.ReadCString(path, 0x125).Length >= 0x124)
        {
            return APPMGR_KERNEL_ERR_ARGUMENT;
        }

        return 0;
    }

    /// <summary>ksceAppMgrCloudDataClearMcId(?): as the bridge's call.</summary>
    [Hle(Nid = 0xEC408FEF, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCloudDataClearMcId(int unused)
    {
        return vshAppMgrCloudDataClearMcId();
    }

    /// <summary>ksceAppMgrCloudDataCreateHeader(setupKey, dataSize, digest, header): NULLs or more than 1 GiB
    /// 0x80800001; then as the bridge's call.</summary>
    [Hle(Nid = 0xED87F191, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCloudDataCreateHeader(uint setupKey, uint dataSize, uint digest, uint header)
    {
        if (setupKey == 0 || digest == 0 || header == 0 || dataSize > CloudDataMaxSize)
        {
            return APPMGR_KERNEL_ERR_ARGUMENT;
        }

        return _vshAppMgrCloudDataCreateHeader(setupKey, dataSize, digest, header);
    }

    /// <summary>ksceAppMgrCloudDataDstCreateMount(param, result): as the bridge's call.</summary>
    [Hle(Nid = 0xB3816B42, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCloudDataDstCreateMount(uint param, uint result)
    {
        return param == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : _vshAppMgrCloudDataDstCreateMount(param, result);
    }

    /// <summary>ksceAppMgrCloudDataGetMcId(?, mcId): as the bridge's call.</summary>
    [Hle(Nid = 0x1286AF64, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCloudDataGetMcId(int unused, uint mcId)
    {
        if (mcId != 0)
        {
            GuestMemory.Write(mcId, _cloudMcId);
        }

        return 0;
    }

    /// <summary>ksceAppMgrCloudDataSetMcId(?, mcId): as the bridge's call.</summary>
    [Hle(Nid = 0x0B94BB90, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCloudDataSetMcId(int unused, uint mcId)
    {
        return _vshAppMgrCloudDataSetMcId(unused, mcId);
    }

    /// <summary>ksceAppMgrCloudDataSetupKey(key): as the bridge's call.</summary>
    [Hle(Nid = 0x2FAA6ABD, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCloudDataSetupKey(uint key)
    {
        return key == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : _vshAppMgrCloudDataSetupKey(key);
    }

    /// <summary>ksceAppMgrCloudDataSrcMount(param, path, result): NULL path or result 0x80800001; online storage needs
    /// PSN (0x80802014).</summary>
    [Hle(Nid = 0x6D4D6FFE, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCloudDataSrcMount(uint param, uint path, uint result)
    {
        return path == 0 || result == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : APPMGR_ERR_ONLINE_STORAGE;
    }

    /// <summary>ksceAppMgrCloudDataVerifyHeader(header): as the bridge's call.</summary>
    [Hle(Nid = 0x55D48190, Library = "SceAppMgrForDriver")]
    int ksceAppMgrCloudDataVerifyHeader(uint header)
    {
        return header == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : _vshAppMgrCloudDataVerifyHeader(header);
    }

    /// <summary>ksceAppMgrDebugSettingNotifyUpdate(?): the module rereads the debug settings: 0.</summary>
    [Hle(Nid = 0x7554330F, Library = "SceAppMgrForDriver")]
    int ksceAppMgrDebugSettingNotifyUpdate(int unused)
    {
        return 0;
    }

    /// <summary>ksceAppMgrFakeSaveDataCreateMount(path, result): as the bridge's call.</summary>
    [Hle(Nid = 0x12FC3FA8, Library = "SceAppMgrForDriver")]
    int ksceAppMgrFakeSaveDataCreateMount(uint path, uint result)
    {
        return path == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : _vshAppMgrFakeSaveDataCreateMount(path, 0, result);
    }

    /// <summary>ksceAppMgrGetPfsProcessStatus(): as the bridge's call.</summary>
    [Hle(Nid = 0xE72D2A4A, Library = "SceAppMgrForDriver")]
    int ksceAppMgrGetPfsProcessStatus()
    {
        return vshAppMgrGetPfsProcessStatus();
    }

    /// <summary>ksceAppMgrIsExclusiveProcessRunning(param): as the bridge's call.</summary>
    [Hle(Nid = 0xA888C2DC, Library = "SceAppMgrForDriver")]
    int ksceAppMgrIsExclusiveProcessRunning(uint param)
    {
        return _vshAppMgrIsExclusiveProcessRunning(param);
    }

    /// <summary>ksceAppMgrLocalBackupGetOfflineId(offlineId, type): as the bridge's call.</summary>
    [Hle(Nid = 0x61B13981, Library = "SceAppMgrForDriver")]
    int ksceAppMgrLocalBackupGetOfflineId(uint offlineId, uint type)
    {
        return _vshAppMgrLocalBackupGetOfflineId(offlineId, type);
    }

    /// <summary>ksceAppMgrLocalBackupVerifyOfflineHeader(header): as the bridge's call.</summary>
    [Hle(Nid = 0x62E98C42, Library = "SceAppMgrForDriver")]
    int ksceAppMgrLocalBackupVerifyOfflineHeader(uint header)
    {
        return header == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : _vshAppMgrLocalBackupVerifyOfflineHeader(header);
    }

    /// <summary>ksceAppMgrRegisterPath(path, flags): NULL 0x80800001; as the bridge's call.</summary>
    [Hle(Nid = 0xB53FB98E, Library = "SceAppMgrForDriver")]
    int ksceAppMgrRegisterPath(uint path, int flags)
    {
        return path == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : _vshAppMgrRegisterPath(path, flags, 0);
    }

    /// <summary>ksceAppMgrSaveDataLocalBackupTargetGetList(list): as the bridge's call.</summary>
    [Hle(Nid = 0x9B3F7D66, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataLocalBackupTargetGetList(uint list)
    {
        return list == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : _vshAppMgrSaveDataLocalBackupTargetGetList(list);
    }

    /// <summary>ksceAppMgrSaveDataLocalBackupTargetRemoveItem(item): as the bridge's call.</summary>
    [Hle(Nid = 0xC4EF95BB, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataLocalBackupTargetRemoveItem(uint item)
    {
        return item == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : _vshAppMgrSaveDataLocalBackupTargetRemoveItem(item);
    }

    /// <summary>ksceAppMgrSaveDataNotifyBackupFinished(param): as the bridge's call.</summary>
    [Hle(Nid = 0x6989B258, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataNotifyBackupFinished(uint param)
    {
        return param == 0 ? APPMGR_KERNEL_ERR_ARGUMENT : _vshAppMgrSaveDataNotifyBackupFinished(param);
    }

    /// <summary>ksceAppMgrSystemParamDateTimeSetConf(conf): as the bridge's call.</summary>
    [Hle(Nid = 0xA9AA3A68, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSystemParamDateTimeSetConf(uint conf)
    {
        return _vshAppMgrSystemParamDateTimeSetConf(conf);
    }

    /// <summary>ksceAppMgrUpdateRifInfo(info): NULL 0x80800009; as the bridge's call.</summary>
    [Hle(Nid = 0x894BDCB8, Library = "SceAppMgrForDriver")]
    int ksceAppMgrUpdateRifInfo(uint info)
    {
        return info == 0 ? APPMGR_KERNEL_ERR_CALLER : _vshAppMgrUpdateRifInfo(info);
    }
}

public sealed partial class AppMgr
{
    /// <summary>NID_DCADB9FE(param): the photo mount's undo: a block whose first word is 4 unmounts photo0:, any other
    /// 0x80800001.</summary>
    [Hle(Nid = 0xDCADB9FE, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_DCADB9FE(uint param)
    {
        if (param == 0 || GuestMemory.R32(param) != 4)
        {
            return unchecked((int)0x80800001);
        }

        return sceAppMgrPhotoUmount();
    }

    /// <summary>NID_0813AFCA(type, path, size): the signed-in account's NP folder: a NULL or short (below 0x20)
    /// buffer 0x80800001; no account is signed in on the unit's NP: an empty path.</summary>
    [Hle(Nid = 0x0813AFCA, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_0813AFCA(int type, uint path, uint size)
    {
        if (path == 0 || size < 0x20)
        {
            return unchecked((int)0x80800001);
        }

        GuestMemory.Write(path, (byte)0);
        return 0;
    }

    /// <summary>NID_1574E1A4(id, buffer, size, result): a system path by id (the 0x6A/0x6B, 0xCD, 600 and 900 ids
    /// of the system programs): NULL buffer or result 0x80800001; none is a path the emulator serves apart
    /// (0x80800007).</summary>
    [Hle(Nid = 0x1574E1A4, Library = "SceAppMgrForDriver", Default = true)]
    int ksceAppMgr_1574E1A4(uint id, uint buffer, uint size, uint result)
    {
        return buffer == 0 || result == 0 ? unchecked((int)0x80800001) : unchecked((int)0x80800007);
    }

    /// <summary>
    /// NID_324DD34E(titleid, type, path, arg size, args, option): launches a process through the process manager
    /// (SceAppMgr 3.74 0x81002794): the option is copied onto the stack (its size word says how many bytes, at most
    /// 0x40; none: a zeroed one of size 0x40), a type of 0x4000000 adds flags 0x180000 and the words 0x2E and 0xD at
    /// +0x2C / +0x30, and SceProcessmgrForKernel NID_5A57766C creates and starts it with the GXM debugging setting of
    /// the registry as the last argument (/DEVENV/TOOL vsh_uses_gxmdbg or game_uses_gxmdbg, absent on a retail
    /// unit: 0). Its result: the new process's ID or the process manager's error.
    /// </summary>
    [Hle(Nid = 0x324DD34E, Library = "SceAppMgrForDriver", Cost = HleCost.SchedulerWait)]
    int ksceAppMgr_324DD34E(uint titleId, uint type, uint path, uint argSize, uint args, uint option)
    {
        Span<byte> options = stackalloc byte[ProcessmgrKernel.ProcessOptionSize];
        options.Clear();
        if (option == 0)
        {
            BitConverter.TryWriteBytes(options, ProcessmgrKernel.ProcessOptionSize);
        }
        else
        {
            uint size = GuestMemory.R32(option);
            int copied = size > 0x3F ? ProcessmgrKernel.ProcessOptionSize : (int)size;
            GuestMemory.Span(option, copied).CopyTo(options);
            // the creation takes only a size of 0x40, which the copy keeps
            BitConverter.TryWriteBytes(options, size);
        }

        if (type == 0x4000000)
        {
            BitConverter.TryWriteBytes(options[0x2C..], 0x2Eu);
            BitConverter.TryWriteBytes(options[0x30..], 0xDu);
            BitConverter.TryWriteBytes(options[4..], BitConverter.ToUInt32(options[4..]) | 0x180000);
        }

        ProcessmgrKernel processmgr = Emu.Hle.Modules.OfType<ProcessmgrKernel>().First();
        return processmgr.LaunchProcess(titleId, type, path, argSize, args, options, 0);
    }

    /// <summary>
    /// NID_E8DB7942(mount point): takes away an application mount by its 16-byte mount point (SceAppMgr 3.74
    /// 0x81018864): only a caller with the kernel's program authority (0x2808000000000001; others 0x80800009); NULL
    /// 0x80800001; a mount point no mount has 0x80800002; else the mount is unmounted and dropped from the list, 0.
    /// </summary>
    [Hle(Nid = 0xE8DB7942, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_E8DB7942(uint mountPoint)
    {
        if (CallerAuthorityId() != KernelAuthorityId)
        {
            return unchecked((int)0x80800009);
        }

        if (mountPoint == 0)
        {
            return unchecked((int)0x80800001);
        }

        string device = GuestMemory.ReadCString(mountPoint, 16).TrimEnd(':');
        lock (_mounts)
        {
            if (device.Length == 0 || !_mounts.Contains(device))
            {
                return unchecked((int)0x80800002);
            }
        }

        return Umount(device) == 0 ? 0 : unchecked((int)0x80800002);
    }

    /// <summary>The program authority of the calling thread's process (the kernel's for a kernel thread), as
    /// ksceKernelGetProcessSelfAuthInfo(0) gives it.</summary>
    ulong CallerAuthorityId()
    {
        if (GuestThread.Current is not { KernelMode: false } thread)
        {
            return KernelAuthorityId;
        }

        return KernelSpace.ProcessOf(Emu, thread.Pid)?.Access.AuthorityId ?? 0;
    }

    /// <summary>The kernel's program authority (SceSysmem's system root data).</summary>
    const ulong KernelAuthorityId = 0x2808000000000001UL;

    /// <summary>NID_504023C8(...): the PSP emulator's launch: only from the shell (0x8080201C).</summary>
    [Hle(Nid = 0x504023C8, Library = "SceAppMgrForDriver", Default = true)]
    int ksceAppMgr_504023C8(uint path, uint first, uint second, uint third)
    {
        return Emu.IsShell ? unchecked((int)0x80800007) : unchecked((int)0x8080201C);
    }

    /// <summary>NID_3B4F305B(?, out): the module's lock state: none held, 0.</summary>
    [Hle(Nid = 0x3B4F305B, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_3B4F305B(uint first, uint output)
    {
        return 0;
    }

    /// <summary>NID_DD5BEEE3(out): the module's lock state: none held, 0.</summary>
    [Hle(Nid = 0xDD5BEEE3, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_DD5BEEE3(uint output)
    {
        return 0;
    }

    /// <summary>NID_30947DF4(param, ?, ?, ?): the save data mount of a process at boot: as sceAppMgrSaveDataMount.</summary>
    [Hle(Nid = 0x30947DF4, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_30947DF4(uint param, uint first, uint second, uint third)
    {
        return param == 0 ? ERR_NULL_POINTER : 0;
    }

    /// <summary>NID_EBFE1BE2(param, ?, ?, ?): its undo at exit.</summary>
    [Hle(Nid = 0xEBFE1BE2, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_EBFE1BE2(uint param, uint first, uint second, uint third)
    {
        return param == 0 ? ERR_NULL_POINTER : 0;
    }
}
