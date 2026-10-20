using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceAppMgrUser (exported by SceDriverUser): the old-style application manager API without the trailing opt struct.
/// Mount functions register a short synthetic device ("am100:") in the VFS and write its name in the caller's 16-byte
/// mount point buffer; save data functions share AppUtil's helpers. Older entries live in <see cref="SystemMisc"/>.
/// </summary>
public sealed class AppMgr : HleModule
{
    const int ERR_PARAM = unchecked((int)0x80802002), ERR_NOT_FOUND = unchecked((int)0x80802012),
        ERR_APP_PARAM_READ = unchecked((int)0x80802015), ERR_NULL_POINTER = unchecked((int)0x80802016),
        ERR_NULL = unchecked((int)0x80800001), ERR_INVALID_VALUE = unchecked((int)0x80801000);
    const int ERRNO_ENOENT = unchecked((int)0x80010002), ERRNO_EINVAL = unchecked((int)0x80010016);

    /// <summary>The process id returned by sceKernelGetProcessId.</summary>
    const int ProcessId = 0x40010003;

    readonly HashSet<string> _mounts = new(StringComparer.OrdinalIgnoreCase);

    AppUtil AppUtil => Emu.Hle.Modules.OfType<AppUtil>().First();

    /// <summary>Launch parameter (URI/string from whoever launched the app); only the first read returns it.</summary>
    public string? LaunchParam { get; set; }

    int MountAlias(string device, string hostRel, uint mountPoint)
    {
        if (mountPoint == 0) return ERR_NULL_POINTER;
        Emu.Vfs.Mount(device, Path.Combine(Emu.Options.FsRoot, hostRel.Replace('/', Path.DirectorySeparatorChar)));
        lock (_mounts) _mounts.Add(device);
        GuestMemory.Fill(mountPoint, 0, 16);
        GuestMemory.WriteCString(mountPoint, device + ":", 16);
        return 0;
    }

    // ------------------------------------------------------------------ mounts

    [Hle]
    int sceAppMgrAppDataMount(int id, uint mountPoint)
    {
        string? rel = id switch
        {
            100 => "ux0/picture", 101 => "ur0/user/00/psnfriend", 102 => "ur0/user/00/psnmsg", 103 => "ur0/user/00/near",
            105 => "ux0/music", 108 => "ux0/calendar", 109 => "ux0/video", _ => null,
        };
        if (rel == null) return ERR_PARAM;
        return MountAlias($"am{id}", rel, mountPoint);
    }

    [Hle]
    int sceAppMgrWorkDirMount(int id, uint mountPoint)
    {
        string? rel = id switch
        {
            200 => "ur0/temp/sqlite", 201 => "ur0/temp/attach", 202 => "ux0/pspemu", 204 => "ur0/temp/checkout",
            206 => "ur0/temp/webbrowser", _ => null,
        };
        if (rel == null) return ERR_PARAM;
        return MountAlias($"am{id}", rel, mountPoint);
    }

    [Hle]
    int sceAppMgrWorkDirMountById(int id, string titleId, uint mountPoint)
    {
        if (titleId.Length != 9) return ERR_PARAM;
        string? rel = id switch { 205 => $"ux0/cache/{titleId}", 207 => $"ux0/temp/app_work/{titleId}/rec", _ => null };
        if (rel == null) return ERR_PARAM;
        return MountAlias($"am{id}", rel, mountPoint);
    }

    [Hle]
    int sceAppMgrUmount(uint mountPoint)
    {
        if (mountPoint == 0) return ERR_NULL_POINTER;
        string dev = GuestMemory.ReadCString(mountPoint, 16).TrimEnd(':');
        lock (_mounts) if (!_mounts.Remove(dev)) return ERR_NOT_FOUND;
        Emu.Vfs.Unmount(dev);
        return 0;
    }

    /// <summary>psp2/appmgr.h: <c>int (int id, const char *titleid, char *mount_point)</c>; the syscall adds an ignored
    /// opt. FW 3.60 accepts 104 (exact <c>ABCD12345_67</c> id), 106/107 (<c>ux0:psm/&lt;id&gt;/RO</c>, <c>/RW</c>) and
    /// 110; the targets of 104 and 110 are not documented, so they are rejected like an unknown id.</summary>
    [Hle]
    [Hle("_sceAppMgrAppDataMountById")]
    int sceAppMgrAppDataMountById(int id, uint titleId, uint mountPoint)
    {
        if (titleId == 0 || mountPoint == 0) return ERR_NULL_POINTER;
        string tid = GuestMemory.ReadCString(titleId, 32);
        string? rel = id switch
        {
            106 when tid.Length > 0 => $"ux0/psm/{tid}/RO",
            107 when tid.Length > 0 => $"ux0/psm/{tid}/RW",
            _ => null,
        };
        if (rel == null)
        {
            Log.WarnOnce($"appdatamountbyid-{id}", $"sceAppMgrAppDataMountById({id}, {tid}): unsupported id");
            return ERR_PARAM;
        }
        if (!Directory.Exists(Path.Combine(Emu.Options.FsRoot, rel.Replace('/', Path.DirectorySeparatorChar)))) return ERR_NOT_FOUND;
        return MountAlias($"am{id}", rel, mountPoint);
    }

    int _gameDataMounts;

    /// <summary>psp2/appmgr.h: <c>int (const char *app_path, const char *patch_path, const char *rif_path, char
    /// *mount_point)</c>. Installed applications are stored decrypted, so the app directory is mounted as is (a patch
    /// directory is not overlaid; the rif is not checked).</summary>
    [Hle]
    int sceAppMgrGameDataMount(uint appPath, uint patchPath, uint rifPath, uint mountPoint)
    {
        if (appPath == 0 || mountPoint == 0) return ERR_NULL_POINTER;
        string app = GuestMemory.ReadCString(appPath, 0x400);
        var host = Emu.Vfs.Resolve(app);
        if (host == null || !Directory.Exists(host)) return ERR_NOT_FOUND;
        if (patchPath != 0 && GuestMemory.ReadCString(patchPath, 0x400) is { Length: > 0 } patch)
            Log.Info($"sceAppMgrGameDataMount: patch {patch} not overlaid");
        string dev = $"gd{Interlocked.Increment(ref _gameDataMounts)}";
        Emu.Vfs.Mount(dev, host);
        lock (_mounts) _mounts.Add(dev);
        GuestMemory.Fill(mountPoint, 0, 16);
        GuestMemory.WriteCString(mountPoint, dev + ":", 16);
        return 0;
    }

    /// <summary>Syscall form: the 16-byte mount point goes to <c>SceAppMgrGameDataMountOpt.mount_point</c> (+0).</summary>
    [Hle]
    int _sceAppMgrGameDataMount(uint appPath, uint patchPath, uint rifPath, uint opt)
        => opt == 0 ? ERR_NULL_POINTER : sceAppMgrGameDataMount(appPath, patchPath, rifPath, GuestMemory.R32(opt));

    // SceAppMgrGenericMountContext (0x40): +0x00 title_id[16], +0x10 pass_code[32], +0x30 mount_drive[16]. The
    // passcode is not checked (no PFS keystone here).

    /// <summary>Additional content directory <c>addcont0:&lt;title_id&gt;</c> must exist; addcont0: itself is mounted at
    /// boot, so nothing else changes.</summary>
    int AddContMount(uint ctx)
    {
        if (ctx == 0) return ERR_NULL_POINTER;
        string id = GuestMemory.ReadCString(ctx, 16);
        var host = id.Length == 0 ? null : Emu.Vfs.Resolve("addcont0:" + id);
        return host != null && Directory.Exists(host) ? 0 : ERR_NOT_FOUND;
    }

    /// <summary>psp2/appmgr.h (_sceAppMgrAddContMount): <c>int (ScePID process_id, const SceAppMgrGenericMountContext *ctx)</c>.</summary>
    [Hle]
    [Hle("_sceAppMgrAddContMount")]
    int sceAppMgrAddContMount(int pid, uint ctx) => AddContMount(ctx);

    /// <summary>psp2/appmgr.h (_sceAppMgrAddContAddMount): <c>int (const SceAppMgrGenericMountContext *ctx, int force_mount_ux)</c>.</summary>
    [Hle]
    [Hle("_sceAppMgrAddContAddMount")]
    int sceAppMgrAddContAddMount(uint ctx, int forceMountUx) => AddContMount(ctx);

    /// <summary>psp2/appmgr.h (_sceAppMgrSaveDataAddMount): <c>int (const SceAppMgrGenericMountContext *ctx)</c>. Mounts
    /// the title's save data as savedata1:, like sceAppUtilSaveDataMount.</summary>
    [Hle]
    [Hle("_sceAppMgrSaveDataAddMount")]
    int sceAppMgrSaveDataAddMount(uint ctx)
    {
        if (ctx == 0) return ERR_NULL_POINTER;
        string tid = GuestMemory.ReadCString(ctx, 16);
        string dir = Path.Combine(Emu.Options.FsRoot, "ux0", "user", "00", "savedata", tid);
        if (tid.Length == 0 || !Directory.Exists(dir)) return ERR_NOT_FOUND;
        Emu.Vfs.Mount("savedata1", dir);
        return 0;
    }

    /// <summary>
    /// SceAppMgrBootParam (0x2C): +0 size, +4 reserved04[2] (zeroed on FW 3.60), +0xC reserved_0C[8] (left as is).
    /// The syscall copies <c>opt->boot_param_size</c> bytes (above 0x2C → PARAM) and ignores the selector. Only the
    /// syscall is registered: whether the public wrapper's third argument is the size or the opt is not documented.
    /// </summary>
    [Hle]
    int _sceAppMgrGetBootParam(uint selector, uint bootParam, uint opt)
    {
        if (bootParam == 0 || opt == 0) return ERR_NULL_POINTER;
        uint size = GuestMemory.R32(opt);
        if (size > 0x2C) return ERR_PARAM;
        for (uint o = 4; o < Math.Min(size, 0xCu); o++) GuestMemory.Write(bootParam + o, (byte)0);
        return 0;
    }

    /// <summary>
    /// psp2/appmgr.h: <c>int _sceAppMgrGetSaveDataInfo(const char *path, const char *title_id, int force_mount_ux,
    /// const SceAppMgrGetSaveDataInfoOpt *opt)</c>, result in <c>opt->result</c> (SceAppMgrGetSaveDataInfoResult,
    /// 0x1C). Only the syscall is registered: the public wrapper's arguments are not documented.
    /// </summary>
    [Hle]
    int _sceAppMgrGetSaveDataInfo(uint path, uint titleId, int forceMountUx, uint opt)
    {
        if (titleId == 0 || opt == 0) return ERR_NULL_POINTER;
        uint result = GuestMemory.R32(opt);
        if (result == 0) return ERR_NULL_POINTER;
        string tid = GuestMemory.ReadCString(titleId, 16);
        if (tid.Length != 9) return ERR_PARAM;
        string dir = Path.Combine(Emu.Options.FsRoot, "ux0", "user", "00", "savedata", tid);
        if (!Directory.Exists(dir)) return ERR_NOT_FOUND;
        long bytes = 0;
        try { bytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (IOException) { }
        GuestMemory.Fill(result, 0, 0x1C);
        GuestMemory.W32(result + 0x00, 4); // SCE_APPMGR_SAVE_DATA_STORAGE_USER (ux0:)
        GuestMemory.W32(result + 0x04, string.Equals(tid, Emu.TitleId, StringComparison.OrdinalIgnoreCase) ? 1u : 0u);
        GuestMemory.W32(result + 0x18, (uint)((bytes + 1023) / 1024));
        return 0;
    }

    /// <summary>No camera is ever in use by another process. The prototype is not documented (pointer or direct
    /// result), so nothing is written: the return value 0 reads as "inactive" either way.</summary>
    [Hle]
    [Hle("_sceAppMgrIsCameraActive")]
    int sceAppMgrIsCameraActive() => 0;

    [Hle] int sceAppMgrPhotoMount(uint param) => AppUtil.MountPhoto();
    [Hle] int sceAppMgrPhotoUmount() => AppUtil.UmountPhoto() == 0 ? 0 : ERR_NOT_FOUND;

    // ------------------------------------------------------------------ parameters and events

    /// <summary>SceAppMgrAppParam (0x400): the launch parameter on the first read, then an empty string and APP_PARAM_READ.</summary>
    [Hle]
    int sceAppMgrGetAppParam(uint param)
    {
        if (param == 0) return ERR_NULL_POINTER;
        string? p = LaunchParam;
        LaunchParam = null;
        if (p == null) { GuestMemory.Write(param, (byte)0); return ERR_APP_PARAM_READ; }
        GuestMemory.WriteCString(param, p, 0x400);
        return 0;
    }

    /// <summary>Sets the CONTENT_ID (100) / TITLE (101) overrides returned by sceAppMgrAppParamGetString. The
    /// prototype is inferred; both (param, string) and (pid, param, string) forms are accepted.</summary>
    [Hle]
    int sceAppMgrAppParamSetString(uint a0, uint a1, uint a2)
    {
        int param; uint str;
        if (a0 is 100 or 101) { param = (int)a0; str = a1; }
        else { param = (int)a1; str = a2; }
        if (param is not (100 or 101)) return ERR_INVALID_VALUE;
        if (str == 0) return ERR_NULL;
        var sys = Emu.Hle.Modules.OfType<SystemMisc>().First();
        lock (sys.AppParamOverrides) sys.AppParamOverrides[param] = GuestMemory.ReadCString(str, 0x400);
        return 0;
    }

    /// <summary>No application events are ever queued (no suspend/resume on the host).</summary>
    [Hle] int sceAppMgrReceiveEvent(uint appEvent) => appEvent == 0 ? ERR_NULL_POINTER : ERR_INVALID_VALUE;

    [Hle]
    int sceAppMgrReceiveEventNum(Ptr<int> eventNum)
    {
        if (eventNum.IsNull) return ERR_NULL_POINTER;
        eventNum.Value = 0;
        return 0;
    }

    [Hle]
    int sceAppMgrLaunchAppByUri(int flags, uint uri)
    {
        if (uri == 0) return ERR_PARAM;
        Log.Info($"sceAppMgrLaunchAppByUri(0x{flags:X}, {GuestMemory.ReadCString(uri, 0x822)}): not launched");
        return 0;
    }

    [Hle]
    int sceAppMgrLaunchAppByUri2(uint uri, uint param)
    {
        if (uri == 0) return ERR_PARAM;
        Log.Info($"sceAppMgrLaunchAppByUri2({GuestMemory.ReadCString(uri, 0x822)}): not launched");
        return 0;
    }

    // ------------------------------------------------------------------ system parameters, paths, processes

    [Hle] int sceAppMgrSystemParamGetInt(uint id, Ptr<int> value) => AppUtil.sceAppUtilSystemParamGetInt(id, value);
    [Hle] int sceAppMgrSystemParamGetString(uint id, uint buf, uint size) => AppUtil.sceAppUtilSystemParamGetString(id, buf, size);

    /// <summary>Partition 1 = "ur0:user/00", 2 = "ux0:user/00"; at most 32 bytes are written.</summary>
    [Hle]
    int sceAppMgrGetUserDirPath(int partition, uint path, uint maxLen)
    {
        if (path == 0) return ERR_NULL_POINTER;
        if (partition is not (1 or 2) || maxLen < 32) return ERR_PARAM;
        GuestMemory.WriteCString(path, partition == 1 ? "ur0:user/00" : "ux0:user/00", 32);
        return 0;
    }

    /// <summary>Capacity and free space of the host drive behind a device, capped at 64 GiB like a memory card.</summary>
    [Hle]
    int sceAppMgrGetDevInfo(uint dev, Ptr<ulong> maxSize, Ptr<ulong> freeSize)
    {
        if (dev == 0) return ERRNO_EINVAL;
        string d = GuestMemory.ReadCString(dev, 32);
        if (!d.Contains(':')) d += ":";
        var host = Emu.Vfs.Resolve(d);
        if (host == null) return ERRNO_ENOENT;
        const ulong Cap = 64UL << 30;
        ulong total = Cap, free = Cap / 2;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(host))!);
            total = Math.Min((ulong)drive.TotalSize, Cap);
            free = Math.Min((ulong)drive.AvailableFreeSpace, total);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        maxSize.TrySet(total);
        freeSize.TrySet(free);
        return 0;
    }

    /// <summary>Paths here are already "raw": copied as is (like Vita3K).</summary>
    [Hle]
    int sceAppMgrGetRawPath(uint path, uint resolved, int size)
    {
        if (path == 0 || resolved == 0) return ERR_NULL_POINTER;
        GuestMemory.WriteCString(resolved, GuestMemory.ReadCString(path, Math.Max(size, 1)), size);
        return 0;
    }

    [Hle]
    int sceAppMgrConvertVs0UserDrivePath(uint src, uint dst, int len)
    {
        if (src == 0 || dst == 0) return ERR_NULL_POINTER;
        GuestMemory.WriteCString(dst, GuestMemory.ReadCString(src, Math.Max(len, 1)), len);
        return 0;
    }

    /// <summary>Only the running application exists.</summary>
    [Hle]
    int sceAppMgrGetIdByName(Ptr<int> pid, uint name)
    {
        if (pid.IsNull || name == 0) return ERR_NULL_POINTER;
        if (!string.Equals(GuestMemory.ReadCString(name, 32), Emu.TitleId, StringComparison.OrdinalIgnoreCase)) return ERR_NOT_FOUND;
        pid.Value = ProcessId;
        return 0;
    }

    [Hle]
    int sceAppMgrGetNameById(int pid, uint name)
    {
        if (name == 0) return ERR_NULL_POINTER;
        if (pid != ProcessId) return ERR_NOT_FOUND;
        GuestMemory.WriteCString(name, Emu.TitleId, 32);
        return 0;
    }

    /// <summary>The handheld is always held in landscape (SCE_APPMGR_SCREEN_ORIENTATION_LANDSCAPE = 1).</summary>
    [Hle]
    int sceAppMgrGetRecommendedScreenOrientation(Ptr<uint> orientation)
    {
        if (orientation.IsNull) return ERR_NULL_POINTER;
        orientation.Value = 1;
        return 0;
    }

    // ------------------------------------------------------------------ save data (AppMgr variant)
    // SceAppMgrSaveDataData (0x4C): size, slotId, slotParam*, reserved[32], files* (+0x2C), fileNum (+0x30),
    // mountPoint (+0x34), requiredSizeKB* (+0x44). SceAppMgrSaveDataDataDelete (0x44): same up to mountPoint, items of
    // 0x40 bytes (SceAppUtilSaveDataFile, dataPath* first; Vita3K). SceAppMgrSaveDataSlot (0x418): size, slotId, embedded
    // SceAppUtilSaveDataSlotParam (+8), mountPoint (+0x3C8). SceAppMgrSaveDataSlotDelete (0x18): size, slotId, mountPoint.

    [Hle]
    int sceAppMgrSaveDataDataSave(uint data)
    {
        if (data == 0) return ERR_NULL_POINTER;
        AppUtil.SaveFiles(GuestMemory.R32(data + 0x2C), GuestMemory.R32(data + 0x30));
        uint slotParam = GuestMemory.R32(data + 8);
        if (slotParam != 0) AppUtil.UpdateSlotAfterSave(GuestMemory.R32(data + 4), slotParam);
        uint required = GuestMemory.R32(data + 0x44);
        if (required != 0) GuestMemory.W32(required, 0);
        return 0;
    }

    [Hle]
    int sceAppMgrSaveDataDataRemove(uint data)
    {
        if (data == 0) return ERR_NULL_POINTER;
        AppUtil.RemoveFiles(GuestMemory.R32(data + 0x2C), GuestMemory.R32(data + 0x30), 0x40);
        return 0;
    }

    [Hle]
    int sceAppMgrSaveDataSlotCreate(uint data)
    {
        if (data == 0) return ERR_NULL_POINTER;
        return AppUtil.sceAppUtilSaveDataSlotCreate(GuestMemory.R32(data + 4), data + 8, 0);
    }

    [Hle]
    int sceAppMgrSaveDataSlotDelete(uint data)
    {
        if (data == 0) return ERR_NULL_POINTER;
        return AppUtil.sceAppUtilSaveDataSlotDelete(GuestMemory.R32(data + 4), 0);
    }

    [Hle]
    int sceAppMgrSaveDataSlotGetParam(uint data)
    {
        if (data == 0) return ERR_NULL_POINTER;
        return AppUtil.sceAppUtilSaveDataSlotGetParam(GuestMemory.R32(data + 4), data + 8, 0);
    }

    [Hle]
    int sceAppMgrSaveDataSlotSetParam(uint data)
    {
        if (data == 0) return ERR_NULL_POINTER;
        return AppUtil.sceAppUtilSaveDataSlotSetParam(GuestMemory.R32(data + 4), data + 8, 0);
    }

    // the slot file is one file per slot here: nothing to initialise, open or close
    [Hle] int sceAppMgrSaveDataSlotInit() => 0;
    [Hle] int sceAppMgrSaveDataSlotFileOpen() => 0;
    [Hle] int sceAppMgrSaveDataSlotFileClose() => 0;
    [Hle] int sceAppMgrUpdateSaveDataParam(uint path) => 0; // save data param.sfo is not generated

    [Hle] int sceAppMgrDrmOpen(uint param) { Log.Info("sceAppMgrDrmOpen"); return 0; }
    [Hle] int sceAppMgrDrmClose(uint param) => 0;
}
