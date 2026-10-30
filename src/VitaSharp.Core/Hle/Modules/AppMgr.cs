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

    /// <summary>
    /// Trophy data mounts, used by the shell's trophy server (shell.self, FW 3.74; the HLE SceNpTrophy keeps its own
    /// files). Targets as the server uses them: 300 = the application of a process (it opens
    /// trophy_sys0:sce_sys/trophy/&lt;NPCOMMID&gt;/TROPHY.TRP), 302 = ur0:user/00/trophy/data/sce_trop (TRPUSER.DAT),
    /// 303/304 = a title's data folder (TRPTITLE.DAT, TRPTRANS.DAT) and its _BK backup (title id "ABCD12345_67", an NP
    /// communication id).
    /// </summary>
    int TrophyMount(string device, string? hostDir, uint mountPoint)
    {
        if (mountPoint == 0) return ERR_NULL_POINTER;
        if (hostDir == null) return ERR_NOT_FOUND;
        Directory.CreateDirectory(hostDir);
        // each mount gets its own device (trophy_dat0:, trophy_dat1:...): the shell's trophy server keeps the user's
        // file (302) and a title's data (303) mounted at the same time and opens files through the names it got
        lock (_mounts)
        {
            string prefix = device.TrimEnd('0');
            for (int i = 0; _mounts.Contains(device); i++) device = $"{prefix}{i}";
            _mounts.Add(device);
        }
        Emu.Vfs.Mount(device, hostDir);
        GuestMemory.Fill(mountPoint, 0, 16);
        GuestMemory.WriteCString(mountPoint, device + ":", 16);
        return 0;
    }

    string TrophyData(string rel) => Path.Combine(Emu.Options.FsRoot, "ur0", "user", "00", "trophy", "data", rel);

    /// <summary>psp2/appmgr.h (_sceAppMgrTrophyMount, which the wrapper calls directly): <c>int (int id, ScePID pid, char mount_point[16])</c>.</summary>
    [Hle]
    [Hle("_sceAppMgrTrophyMount")]
    int sceAppMgrTrophyMount(int id, int pid, uint mountPoint) => id switch
    {
        // the application of process pid (-1: the caller), read only for its trophy configuration: the shell's trophy
        // server (FW 3.74) installs a set from trophy_sys0:sce_sys/trophy/<NPCOMMID>/TROPHY.TRP
        300 => (pid > 0 ? Emu.Vita.ByPid(pid) ?? Emu : Emu).Vfs.Resolve("app0:sce_sys/trophy") is { } conf && Directory.Exists(conf)
            ? TrophyMount("trophy_sys0", Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(conf))), mountPoint)
            : mountPoint == 0 ? ERR_NULL_POINTER : ERR_NOT_FOUND,
        // the user's trophy file: the shell's trophy server (FW 3.74) mounts 302 and opens trophy_dat0:TRPUSER.DAT, and
        // checks ur0:user/00/trophy/data/sce_trop by path when it is missing
        302 => TrophyMount("trophy_dat0", TrophyData("sce_trop"), mountPoint),
        _ => ERR_PARAM,
    };

    /// <summary>psp2/appmgr.h: <c>int (int id, const char *title_id, char mount_point[16])</c>; the syscall adds an ignored opt.</summary>
    [Hle]
    int sceAppMgrTrophyMountById(int id, uint titleId, uint mountPoint)
    {
        if (titleId == 0) return ERR_NULL_POINTER;
        string tid = GuestMemory.ReadCString(titleId, 16);
        if (tid.Length != 12 || tid[9] != '_') return ERR_PARAM;
        return id switch
        {
            303 => TrophyMount("trophy_dat0", TrophyData(tid), mountPoint),
            304 => TrophyMount("trophy_dbk0", TrophyData(tid + "_BK"), mountPoint),
            _ => ERR_PARAM,
        };
    }

    [Hle] int _sceAppMgrTrophyMountById(int id, uint titleId, uint mountPoint, uint opt) => sceAppMgrTrophyMountById(id, titleId, mountPoint);

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
    /// 110 (<c>ux0:appmeta/&lt;id&gt;</c>, mounted by the shell's database rebuild for each folder it lists there); the
    /// target of 104 is not documented, so it is rejected like an unknown id.</summary>
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
            // an installed application's metadata copy (param.sfo, icon, LiveArea) that the shell's database rebuild
            // (dbrecovery_plugin) reads for every entry of ux0:appmeta
            110 when tid.Length == 9 => $"ux0/appmeta/{tid}",
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
    /// The syscall copies <c>opt->boot_param_size</c> bytes (above 0x2C → PARAM) and ignores the selector.
    /// </summary>
    [Hle]
    int _sceAppMgrGetBootParam(uint selector, uint bootParam, uint opt)
    {
        if (bootParam == 0 || opt == 0) return ERR_NULL_POINTER;
        return BootParam(bootParam, GuestMemory.R32(opt));
    }

    /// <summary>Public wrapper (FW 3.60 SceDriverUser): <c>int (SceUInt32 selector, SceAppMgrBootParam *bootParam)</c>,
    /// the opt's boot_param_size taken from <c>bootParam->size</c>.</summary>
    [Hle]
    int sceAppMgrGetBootParam(uint selector, uint bootParam)
    {
        if (bootParam == 0) return ERR_NULL_POINTER;
        return BootParam(bootParam, GuestMemory.R32(bootParam));
    }

    static int BootParam(uint bootParam, uint size)
    {
        if (size > 0x2C) return ERR_PARAM;
        for (uint o = 4; o < Math.Min(size, 0xCu); o++) GuestMemory.Write(bootParam + o, (byte)0);
        return 0;
    }

    /// <summary>Public wrapper (FW 3.60 SceDriverUser): <c>int (const char *path, const char *title_id, int
    /// force_mount_ux, SceAppMgrGetSaveDataInfoResult *result)</c>, the result pointer moved into the opt.</summary>
    [Hle]
    int sceAppMgrGetSaveDataInfo(uint path, uint titleId, int forceMountUx, uint result)
    {
        if (result == 0) return ERR_NULL_POINTER;
        return SaveDataInfo(titleId, result);
    }

    /// <summary>
    /// psp2/appmgr.h: <c>int _sceAppMgrGetSaveDataInfo(const char *path, const char *title_id, int force_mount_ux,
    /// const SceAppMgrGetSaveDataInfoOpt *opt)</c>, result in <c>opt->result</c> (SceAppMgrGetSaveDataInfoResult,
    /// 0x1C).
    /// </summary>
    [Hle]
    int _sceAppMgrGetSaveDataInfo(uint path, uint titleId, int forceMountUx, uint opt)
    {
        if (titleId == 0 || opt == 0) return ERR_NULL_POINTER;
        uint result = GuestMemory.R32(opt);
        if (result == 0) return ERR_NULL_POINTER;
        return SaveDataInfo(titleId, result);
    }

    int SaveDataInfo(uint titleId, uint result)
    {
        if (titleId == 0) return ERR_NULL_POINTER;
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

    /// <summary>FW 3.60 kernel: <c>int (void *param)</c> filling a 0x440-byte block (the 0x400 launch parameter at +0,
    /// then launch bookkeeping, left zero here); same results as GetAppParam. The kernel gates it behind a permission
    /// check whose outcome for a game process is not known (NOEXEC 0x8080201F when denied).</summary>
    [Hle]
    int sceAppMgrGetAppParam2(uint param)
    {
        if (param == 0) return ERR_NULL_POINTER;
        GuestMemory.Fill(param, 0, 0x440);
        string? p = LaunchParam;
        LaunchParam = null;
        if (p == null) return ERR_APP_PARAM_READ;
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

    // Application events (FW 3.74 kernel appmgr): one pending flag per event in the application's control block, not a
    // queue. Posting an event sets its flag (ACTIVATE and DEACTIVATE cancel each other), ReceiveEventNum counts the flags
    // set and ReceiveEvent takes them in a fixed order, writing {event, -1, 0x38 zero bytes}; with none it fails with
    // 0x80802020. The launch posts PARAM_NOTIFY: libpaf-based system applications (Settings, Friends) build their first
    // page only when it arrives, reading the launch parameter with sceAppMgrGetAppParam.
    public const uint EventActivate = 0x10000001, EventDeactivate = 0x10000002, EventResume = 0x10000003,
        EventRequestQuit = 0x20000001, EventParamNotify = 0x20000004;
    const int ERR_NO_APP_EVENT = unchecked((int)0x80802020);
    static readonly uint[] EventOrder = [EventActivate, EventDeactivate, EventResume, EventRequestQuit, EventParamNotify, 0x10000101, 0x10000102, 0x20000010];
    readonly HashSet<uint> _pendingEvents = new();

    /// <summary>Sets an application event's pending flag (kernel appmgr 0x81012C00).</summary>
    public void PostEvent(uint ev)
    {
        lock (_pendingEvents)
        {
            // an activate pending when the application is deactivated again (or the reverse) is dropped with it
            if (ev == EventActivate && _pendingEvents.Remove(EventDeactivate)) return;
            if (ev == EventDeactivate && _pendingEvents.Remove(EventActivate)) return;
            if (ev == 0x10000101) _pendingEvents.Remove(0x10000102);
            if (ev == 0x10000102) _pendingEvents.Remove(0x10000101);
            if (Array.IndexOf(EventOrder, ev) >= 0) _pendingEvents.Add(ev);
        }
    }

    /// <summary>(void *buf, SceSize size, SceOff offset): the application's safe memory (64 KiB kept across runs), the
    /// kernel side of sceAppUtilLoad/SaveSafeMemory and the same storage. Never written: zeros. The Browser restores its
    /// tabs from it at start; unimplemented, the buffer kept whatever was in it and the Browser freed garbage pointers.</summary>
    [Hle] int sceAppMgrLoadSafeMemory(uint buf, uint size, long offset) => AppUtil.sceAppUtilLoadSafeMemory(buf, size, offset);
    [Hle] int sceAppMgrSaveSafeMemory(uint buf, uint size, long offset) => AppUtil.sceAppUtilSaveSafeMemory(buf, size, offset);

    /// <summary>(SceAppMgrEvent *event, 0x40 bytes): takes the first pending application event.</summary>
    [Hle]
    int sceAppMgrReceiveEvent(uint appEvent)
    {
        if (appEvent == 0) return ERR_NULL_POINTER;
        uint ev;
        lock (_pendingEvents)
        {
            ev = EventOrder.FirstOrDefault(_pendingEvents.Contains);
            if (ev == 0) return ERR_NO_APP_EVENT;
            _pendingEvents.Remove(ev);
        }
        GuestMemory.W32(appEvent, ev);
        GuestMemory.W32(appEvent + 4, 0xFFFFFFFF);
        GuestMemory.Fill(appEvent + 8, 0, 0x38);
        return 0;
    }

    [Hle]
    int sceAppMgrReceiveEventNum(Ptr<int> eventNum)
    {
        if (eventNum.IsNull) return ERR_NULL_POINTER;
        lock (_pendingEvents) eventNum.Value = _pendingEvents.Count;
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

    /// <summary>
    /// <c>int (SceAppMgrDateTimeConf *conf)</c>, 0x14 bytes kept by the kernel for the shell (FW 3.60): +0 date format,
    /// +4 time format, +8 unknown (written 0), +0xC time zone (minutes), +0x10 summer time; libSceAppUtil reads
    /// system params 4..7 from these offsets. NULL → 0x80801001.
    /// </summary>
    [Hle]
    [Hle("_sceAppMgrSystemParamDateTimeGetConf")]
    int sceAppMgrSystemParamDateTimeGetConf(uint conf)
    {
        if (conf == 0) return unchecked((int)0x80801001);
        GuestMemory.W32(conf + 0x00, (uint)AppUtil.SystemParamInt(4));
        GuestMemory.W32(conf + 0x04, (uint)AppUtil.SystemParamInt(5));
        GuestMemory.W32(conf + 0x08, 0);
        GuestMemory.W32(conf + 0x0C, (uint)AppUtil.SystemParamInt(6));
        GuestMemory.W32(conf + 0x10, (uint)AppUtil.SystemParamInt(7));
        return 0;
    }

    /// <summary>
    /// <c>int (const char drive[16], int *mediaType)</c> (FW 3.60 kernel): looks the drive up in the process's mount
    /// table and classifies its target: ux0 = 4, gro0/grw0 (game card) = 3, ur0/vs0 = 2, anything else 1. A drive that
    /// is not one of the process's mounts (a raw "ux0:", say) is 0x80800002. Here every app mount lives under the
    /// emulated ux0 except the system partitions, so the host path decides.
    /// </summary>
    [Hle]
    int sceAppMgrGetMediaTypeFromDrive(uint drive, Ptr<int> mediaType)
    {
        if (drive == 0 || mediaType.IsNull) return ERR_NULL;
        string dev = GuestMemory.ReadCString(drive, 16);
        int colon = dev.IndexOf(':');
        if (colon >= 0) dev = dev[..colon];
        bool processMount = dev.ToLowerInvariant() is "app0" or "savedata0" or "savedata1" or "addcont0" or "photo0"
            || dev.StartsWith("trophy_", StringComparison.OrdinalIgnoreCase);
        lock (_mounts) processMount |= _mounts.Contains(dev);
        var host = processMount ? Emu.Vfs.Resolve(dev + ":") : null;
        if (host == null) return unchecked((int)0x80800002);
        string root = Path.GetFullPath(Emu.Options.FsRoot), full = Path.GetFullPath(host);
        string first = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0].ToLowerInvariant()
            : "ux0"; // titles run from a folder outside the data root are installed apps too
        mediaType.Value = first switch { "ux0" => 4, "gro0" or "grw0" => 3, "ur0" or "vs0" => 2, _ => 1 };
        return 0;
    }

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

    /// <summary>The process running the application <paramref name="name"/> (title id), among the system's processes.</summary>
    [Hle]
    int sceAppMgrGetIdByName(Ptr<int> pid, uint name)
    {
        if (pid.IsNull || name == 0) return ERR_NULL_POINTER;
        string tid = GuestMemory.ReadCString(name, 32);
        var p = Emu.Vita.Processes.FirstOrDefault(e => string.Equals(e.TitleId, tid, StringComparison.OrdinalIgnoreCase));
        if (p == null) return ERR_NOT_FOUND;
        pid.Value = p.Pid;
        return 0;
    }

    [Hle]
    int sceAppMgrGetNameById(int pid, uint name)
    {
        if (name == 0) return ERR_NULL_POINTER;
        if (Emu.Vita.ByPid(pid) is not { } p) return ERR_NOT_FOUND;
        GuestMemory.WriteCString(name, p.TitleId, 32);
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

    // FW 3.60 SceDriverUser wrappers below hand their struct to the syscall with an 8-byte opt holding the struct's own
    // size (+0); the kernel rejects a size above the struct (NO_MEMORY 0x8002710B) and its core a size other than the
    // exact one (0x80800001). Layouts come from the FW 3.60 SceAppMgr kernel module and its user, libSceAppUtil.

    const int ERR_SIZE = unchecked((int)0x8002710B);
    const int ERR_SLOT_NOT_FOUND = unchecked((int)0x8080108B), ERR_SLOT_RANGE = unchecked((int)0x80801084);
    const uint SlotMax = 256; // SCE_APPUTIL_SAVEDATA_SLOT_MAX

    static int CheckSize(uint data, uint size, bool exact = true)
    {
        if (data == 0) return ERR_NULL;
        uint s = GuestMemory.R32(data);
        if (s > size) return ERR_SIZE;
        return exact && s != size ? ERR_NULL : 0;
    }

    /// <summary>Same SceAppMgrSaveDataData (0x4C) as DataSave; the kernel shares the code and only adds a permission
    /// check for the "2" variant.</summary>
    [Hle]
    int sceAppMgrSaveDataDataSave2(uint data)
    {
        int r = CheckSize(data, 0x4C);
        return r < 0 ? r : sceAppMgrSaveDataDataSave(data);
    }

    /// <summary>Same arguments as DataRemove (SceAppMgrSaveDataDataDelete, 0x44); see DataSave2.</summary>
    [Hle]
    int sceAppMgrSaveDataDataRemove2(uint data)
    {
        int r = CheckSize(data, 0x44);
        return r < 0 ? r : sceAppMgrSaveDataDataRemove(data);
    }

    /// <summary>SceAppMgrSaveDataQuota (0x1C, from libSceAppUtil's GetQuota): +0 size, +4 quota KiB (out), +8 used KiB
    /// (out), +0xC mount point[16].</summary>
    [Hle]
    int sceAppMgrSaveDataGetQuota(uint data)
    {
        int r = CheckSize(data, 0x1C);
        if (r < 0) return r;
        return AppUtil.sceAppUtilSaveDataGetQuota(new Ptr<uint>(data + 4), new Ptr<uint>(data + 8), data + 0xC);
    }

    /// <summary>SceAppMgrSaveDataSlotStatus (0x1C): +0 size, +4 slotId, +8 status (the first word of the slot's
    /// SceAppUtilSaveDataSlotParam), +0xC mount point[16]. A missing slot is 0x8080108B, an id past the slot table
    /// 0x80801084.</summary>
    int SlotStatusCheck(uint data, out uint slot)
    {
        slot = 0;
        int r = CheckSize(data, 0x1C);
        if (r < 0) return r;
        slot = GuestMemory.R32(data + 4);
        if (slot >= SlotMax) return ERR_SLOT_RANGE;
        return AppUtil.SlotExists(slot) ? 0 : ERR_SLOT_NOT_FOUND;
    }

    [Hle]
    int sceAppMgrSaveDataSlotGetStatus(uint data)
    {
        int r = SlotStatusCheck(data, out uint slot);
        if (r < 0) return r;
        var bytes = File.ReadAllBytes(AppUtil.SlotPath(slot));
        GuestMemory.W32(data + 8, bytes.Length >= 4 ? BitConverter.ToUInt32(bytes, 0) : 0);
        return 0;
    }

    [Hle]
    int sceAppMgrSaveDataSlotSetStatus(uint data)
    {
        int r = SlotStatusCheck(data, out uint slot);
        if (r < 0) return r;
        var path = AppUtil.SlotPath(slot);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4) Array.Resize(ref bytes, AppUtil.SlotParamSize);
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), GuestMemory.R32(data + 8));
        File.WriteAllBytes(path, bytes);
        return 0;
    }

    /// <summary><c>int (unsigned slotId, void *buf)</c> (libSceAppUtil's slot search): the kernel always writes a
    /// 0x400-byte buffer whose start is the slot's SceAppUtilSaveDataSlotParam (0x34C).</summary>
    [Hle]
    int sceAppMgrSaveDataSlotFileGetParam(uint slot, uint buf)
    {
        if (buf == 0) return ERR_NULL;
        if (slot >= SlotMax) return ERR_SLOT_RANGE;
        GuestMemory.Fill(buf, 0, 0x400);
        if (!AppUtil.SlotExists(slot)) return ERR_SLOT_NOT_FOUND;
        AppUtil.ReadSlotParam(slot, buf);
        return 0;
    }

    /// <summary>SceAppMgrSaveDataMount (0x3C): +0 size, +4 16 bytes (title id), +0x14 mount point name ("savedata0"),
    /// rest unknown. The process's own savedata0: is mounted at boot, so this only validates the struct.</summary>
    [Hle]
    int sceAppMgrSaveDataMount(uint data)
    {
        int r = CheckSize(data, 0x3C);
        if (r < 0) return r;
        if (!Emu.Vfs.IsMounted("savedata0"))
            Emu.Vfs.Mount("savedata0", Path.Combine(Emu.Options.FsRoot, "ux0", "user", "00", "savedata", Emu.TitleId));
        return 0;
    }

    /// <summary>SceAppMgrSaveDataUmount (0x14): +0 size, +4 mount point[16]. savedata0: stays mounted (games and the HLE
    /// AppUtil keep using it); savedata1: and the aliases mounted here are removed.</summary>
    [Hle]
    int sceAppMgrSaveDataUmount(uint data)
    {
        int r = CheckSize(data, 0x14);
        if (r < 0) return r;
        string dev = GuestMemory.ReadCString(data + 4, 16).TrimEnd(':');
        if (dev.Equals("savedata0", StringComparison.OrdinalIgnoreCase)) return 0;
        if (dev.Equals("savedata1", StringComparison.OrdinalIgnoreCase)) return Emu.Vfs.Unmount("savedata1") ? 0 : ERR_NOT_FOUND;
        lock (_mounts) if (!_mounts.Remove(dev)) return ERR_NOT_FOUND;
        Emu.Vfs.Unmount(dev);
        return 0;
    }

    // the slot file is one file per slot here: nothing to initialise, open or close
    [Hle] int sceAppMgrSaveDataSlotInit() => 0;
    [Hle] int sceAppMgrSaveDataSlotFileOpen() => 0;
    [Hle] int sceAppMgrSaveDataSlotFileClose() => 0;
    [Hle] int sceAppMgrUpdateSaveDataParam(uint path) => 0; // save data param.sfo is not generated

    [Hle] int sceAppMgrDrmOpen(uint param) { Log.Info("sceAppMgrDrmOpen"); return 0; }
    [Hle] int sceAppMgrDrmClose(uint param) => 0;
}
