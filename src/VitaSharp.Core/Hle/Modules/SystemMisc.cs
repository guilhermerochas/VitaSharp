using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceSysmodule, SceAppMgr, ScePower, SceModulemgr and other small system services.</summary>
public sealed partial class SystemMisc : HleModule
{
    readonly HashSet<uint> _loadedSysmodules = new();
    const int SYSMODULE_ERROR_INVALID_VALUE = unchecked((int)0x805A1000), SYSMODULE_ERROR_UNLOADED = unchecked((int)0x805A1001),
        SYSMODULE_ERROR_FATAL = unchecked((int)0x805A10FF);

    // ------------------------------------------------------------------ SceSysmodule

    /// <summary>The public syscall: the id is cut to 16 bits (it never reaches an internal module) and checked as an
    /// unprivileged request (<see cref="SysmodulePermission"/>).</summary>
    [Hle]
    int sceSysmoduleLoadModule(uint id)
    {
        uint publicId = id & 0xFFFF;
        int denied = SysmodulePermission(publicId, privileged: false);
        return denied < 0 ? denied : LoadSysmodule(publicId, 0, 0);
    }

    /// <summary>The internal syscall: the kernel marks the request privileged (id | 0x40000000).</summary>
    [Hle]
    int sceSysmoduleLoadModuleInternal(uint id)
    {
        int denied = SysmodulePermission(id, privileged: true);
        return denied < 0 ? denied : LoadSysmodule(id, 0, 0);
    }

    /// <summary>SceSysmoduleOpt (0x10): +0 flags, +4 int *result (module_start's result), +8 unused[2].</summary>
    [Hle]
    int sceSysmoduleLoadModuleInternalWithArg(uint id, uint args, uint argp, uint option)
    {
        int denied = SysmodulePermission(id, privileged: true);
        if (denied < 0)
        {
            return denied;
        }

        int r = LoadSysmodule(id, args, argp, option != 0 ? GuestMemory.R32(option + 4) : 0);
        return r;
    }

    /// <summary>What SceSysmodule answers when the caller may not load a module, or the id has no module.</summary>
    internal const int SysmoduleNotPermitted = unchecked((int)0xC05A1000);

    /// <summary>Any caller (permission word bit 0).</summary>
    const ushort SysmoduleAnyCaller = 0x1;
    /// <summary>Unprivileged requests: a system program only.</summary>
    const ushort SysmoduleSystemCaller = 0x2;
    /// <summary>Unprivileged requests: a system or non-game program.</summary>
    const ushort SysmoduleNonGameCaller = 0x4;
    /// <summary>Unprivileged requests: a system or PSM program.</summary>
    const ushort SysmodulePsmCaller = 0x8;
    /// <summary>Every request: a system program.</summary>
    const ushort SysmoduleSystemAlways = 0x10;
    /// <summary>Every request: a development kit.</summary>
    const ushort SysmoduleDevelopmentOnly = 0x20;

    /// <summary>
    /// The permission word of each sysmodule id, from the tables of FW 3.74's SceSysmodule (the bootimage module): public
    /// ids 0-0x55, internal ids 0x80000000 | 0-0x29. Public modules are open to any caller except the development
    /// tools (0x21: libperf, the Razor modules, libsulpha...), the non-game programs' libraries (4: trigger_util,
    /// video/music export, bgapputil, incoming dialog, the IPMI client, the MLN libraries, telephony, DTCP-IP, video
    /// search, the mail API, the player's AVC decoder) and the PSM runtime's (8: libshacccg, libmono_bridge, libmono,
    /// libpsm, libpsm_devagent). The internal modules are all 0x42 (libult 0x2): a system program only for an
    /// unprivileged request, which the public syscall can never make for them.
    /// </summary>
    static ushort SysmodulePermissionWord(uint id)
    {
        if ((id & 0x80000000) != 0)
        {
            uint index = id & 0xFFFF;
            return index switch
            {
                0 => 0,
                0x25 => SysmoduleSystemCaller,
                <= 0x29 => 0x42,
                _ => 0,
            };
        }

        return (id & 0xFFFF) switch
        {
            0 => 0,
            0x05 or 0x09 or 0x0A or 0x0C or 0x22 => SysmoduleDevelopmentOnly | SysmoduleAnyCaller,
            0x20 or 0x32 or (>= 0x34 and <= 0x37) or (>= 0x3A and <= 0x3D) or 0x44 or 0x45 or 0x4E or 0x54 => SysmoduleNonGameCaller,
            >= 0x3E and <= 0x42 => SysmodulePsmCaller,
            <= 0x55 => SysmoduleAnyCaller,
            _ => 0,
        };
    }

    /// <summary>
    /// SceSysmodule's check before a load (FW 3.74): development-only modules need a development kit and
    /// "system always" ones a system program, for every request; then, for an unprivileged request only, the caller
    /// class the word names (system; system or non-game; system or PSM); a privileged one (the internal syscalls) is let
    /// through. Bit 0 lets anyone through. 0 when allowed, else <see cref="SysmoduleNotPermitted"/>.
    /// </summary>
    int SysmodulePermission(uint id, bool privileged)
    {
        ushort word = SysmodulePermissionWord(id);
        Loader.Self.ProgramAccess caller = Emu.Access;
        if ((word & SysmoduleDevelopmentOnly) != 0 && !caller.IsDevelopmentMode)
        {
            return SysmoduleNotPermitted;
        }

        if ((word & SysmoduleSystemAlways) != 0 && !caller.IsSystemProgram)
        {
            return SysmoduleNotPermitted;
        }

        bool allowed;
        if ((word & SysmoduleNonGameCaller) != 0)
        {
            allowed = privileged || caller.IsSystemProgram || caller.IsNonGameProgram;
        }
        else if ((word & SysmodulePsmCaller) != 0)
        {
            allowed = privileged || caller.IsSystemProgram || caller.IsPsmProgram;
        }
        else if ((word & SysmoduleSystemCaller) != 0)
        {
            allowed = privileged || caller.IsSystemProgram;
        }
        else
        {
            allowed = (word & SysmoduleAnyCaller) != 0;
        }

        if (!allowed)
        {
            Log.WarnOnce($"sysmodule-denied-{id:X}", $"sceSysmoduleLoadModule{(privileged ? "Internal" : "")}(0x{id:X}): not permitted for {Emu.Describe()} (permission 0x{word:X})");
        }

        return allowed ? 0 : SysmoduleNotPermitted;
    }

    int LoadSysmodule(uint id, uint args, uint argp, uint resultPtr = 0)
    {
        lock (_loadedSysmodules)
            if (_loadedSysmodules.Contains(id)) { if (resultPtr != 0) GuestMemory.W32(resultPtr, 0); return 0; }
        int start = 0;
        // an internal module the permission check let through: the firmware's own module, as the console loads it;
        // outside the system software only when the HLE emulates none of its libraries (as for the public modules)
        if (InternalSysmodules.TryGetValue(id, out var internalLib) && (Emu.IsSystemProgram || FirmwareOnly(internalLib)))
        {
            int r = TryLoadModuleLle(internalLib, args, argp, out start);
            if (r < 0) return r;
        }
        else if (LleSysmodules.TryGetValue(id, out var lib))
        {
            int r = TryLoadModuleLle(lib, args, argp, out start);
            if (r < 0) return r;
        }
        else if (PublicSysmodules.TryGetValue(id, out var files) && files.Where(FirmwareOnly).ToArray() is { Length: > 0 } fwOnly)
        {
            // libraries the HLE knows nothing of: the firmware's module, as the Vita loads it (HLE first, then the real one)
            foreach (var f in fwOnly)
            {
                int r = TryLoadModuleLle(f, args, argp, out start);
                if (r < 0) return r;
            }
        }
        else Log.Debug($"sceSysmoduleLoadModule(0x{id:X}) -> HLE");
        if (resultPtr != 0) GuestMemory.W32(resultPtr, (uint)start);
        lock (_loadedSysmodules) _loadedSysmodules.Add(id);
        return 0;
    }

    /// <summary>
    /// Sysmodules whose implementation can be the firmware/game module itself: the game may ship them in
    /// app0:sce_module (early-SDK titles bundle libult/libfiber/libface/libsmart) or the installed firmware has them in
    /// vs0:sys/external. When present they are loaded and started LLE, which rebinds the game's already-linked HLE
    /// import stubs. File names follow Vita3K's load_module table.
    /// </summary>
    static readonly Dictionary<uint, string> LleSysmodules = new()
    {
        [0x0006] = "libfiber",     // FIBER
        [0x0007] = "libult",       // ULT
        [0x000D] = "libsas",       // SAS
        [0x0030] = "libatrac",     // ATRAC (LLE uses sceAudiodecPartlyDecode)
        [0x0017] = "libSceXml",    // XML
        [0x001F] = "libSceSqlite", // SQLITE
        [0x0038] = "libface",      // FACE
        [0x0039] = "libsmart",     // SMART
        [0x0055] = "libSceJson",   // JSON
    };

    /// <summary>
    /// The public sysmodule ids and the vs0:sys/external modules each one loads (psp2/sysmodule.h ids; module lists as
    /// in Vita3K's sysmodule_paths). The ids in <see cref="LleSysmodules"/> keep their own rules; for the others a module
    /// is loaded only when the HLE has no function of any library it exports (<see cref="FirmwareOnly"/>): what the HLE
    /// emulates stays on HLE, and a library it does not know comes from the real module instead of staying unimplemented.
    /// </summary>
    static readonly Dictionary<uint, string[]> PublicSysmodules = new()
    {
        [0x0001] = ["libnet", "libnetctl"], [0x0002] = ["libhttp"], [0x0003] = ["libssl"], [0x0004] = ["libhttp", "libssl"],
        [0x0005] = ["libperf"], [0x000B] = ["libngs"], [0x000C] = ["libsulpha"], [0x000E] = ["libpgf"],
        [0x000F] = ["apputil"], [0x0010] = ["libfios2"], [0x0011] = ["libime"], [0x0012] = ["np_basic"],
        [0x0013] = ["libsystemgesture"], [0x0014] = ["liblocation"], [0x0015] = ["np_common", "np_manager", "np_basic"],
        [0x0016] = ["libScePhotoExport"], [0x0018] = ["np_commerce2"], [0x0019] = ["np_utility"], [0x001A] = ["libvoice"],
        [0x001B] = ["libvoiceqos"], [0x001C] = ["np_matching2"], [0x001D] = ["libSceScreenShot"], [0x001E] = ["np_ranking"],
        [0x0020] = ["trigger_util"], [0x0021] = ["librudp"], [0x0023] = ["livearea_util"], [0x0024] = ["np_activity_sdk"],
        [0x0025] = ["np_trophy"], [0x0026] = ["np_message_padding", "np_message"], [0x0027] = ["libSceShutterSound"],
        [0x0028] = ["libclipboard"], [0x0029] = ["np_party"], [0x002A] = ["adhoc_matching"], [0x002C] = ["np_tus"],
        [0x002D] = ["libscemp4"], [0x002E] = ["libnaac"], [0x002F] = ["libhandwriting"], [0x0031] = ["np_sns_facebook"],
        [0x0032] = ["libSceVideoExport"], [0x0033] = ["notification_util"], [0x0034] = ["bgapputil"],
        [0x0035] = ["incoming_dialog"], [0x0036] = ["libipmi_nongame"], [0x003A] = ["libmln"], [0x003B] = ["libmlndownloader"],
        [0x003C] = ["libmlnapplib"], [0x003D] = ["libSceTelephonyUtil"], [0x0043] = ["pspnet_adhoc"],
        [0x0044] = ["libSceDtcpIp"], [0x0045] = ["libSceVideoSearchEmpr"], [0x0046] = ["np_signaling"],
        [0x0047] = ["libSceBeisobmf"], [0x0048] = ["libSceBemp2sys"], [0x0049] = ["libSceMusicExport"],
        [0x004A] = ["libSceNearDialogUtil"], [0x004B] = ["liblocation_extension"], [0x004C] = ["libsceavplayer", "libscemp4"],
        [0x004E] = ["mail_api_for_local_libc"], [0x004F] = ["libSceTeleportClient"], [0x0050] = ["libSceTeleportServer"],
        [0x0051] = ["libSceMp4Rec"], [0x0052] = ["apputil_ext"], [0x0053] = ["np_webapi"], [0x0054] = ["avcdec_for_player"],
    };

    /// <summary>The installed firmware has <paramref name="file"/> and the HLE has no function of any library it exports.</summary>
    internal bool FirmwareOnly(string file)
    {
        var mod = Firmware.FirmwareLibraries.For(Emu.Options.FsRoot).Modules
            .FirstOrDefault(m => string.Equals(Path.GetFileNameWithoutExtension(m.VitaPath), file, StringComparison.OrdinalIgnoreCase));
        return mod != null && mod.Libraries.Count > 0 && mod.Libraries.All(l => !Emu.Hle.KnowsLibrary(l.Library));
    }

    /// <summary>
    /// SCE_SYSMODULE_INTERNAL_* (0x80000000 | n): the system software's own modules, loaded LLE from the firmware for
    /// system programs (vs0:app, the shell). File names from Vita3K's sysmodule_internal_paths and the vs0 layout.
    /// Any other program may load them too (sceSysmoduleLoadModuleInternal is privileged, <see cref="SysmodulePermission"/>):
    /// the PSM runtime loads psmkdc; there the firmware's module is used when the HLE emulates none of its libraries.
    /// </summary>
    static readonly Dictionary<uint, string> InternalSysmodules = new()
    {
        [0x80000001] = "libscejpegencarm", [0x80000002] = "audiocodec", [0x80000003] = "libscejpegarm",
        [0x80000004] = "libg729", [0x80000005] = "bXCe", [0x80000006] = "ini_file_processor",
        [0x80000007] = "np_activity", [0x80000008] = "libpaf", [0x80000009] = "sqlite", [0x8000000A] = "dbutil",
        [0x8000000B] = "activity_db", [0x8000000C] = "common_gui_dialog", [0x8000000D] = "store_checkout_plugin",
        [0x8000000E] = "libcdlg_ime", [0x8000000F] = "libcdlg_photoimport", [0x80000010] = "libcdlg_photoreview",
        [0x80000011] = "libcdlg_checkout", [0x80000012] = "libcdlg_main", [0x80000013] = "libcdlg_msg",
        [0x80000014] = "libcdlg_netcheck", [0x80000015] = "libcdlg_savedata", [0x80000016] = "libcdlg_np_message",
        [0x80000017] = "libcdlg_np_trophy_setup", [0x80000018] = "libcdlg_friendlist", [0x8000001A] = "near_profile",
        [0x8000001B] = "np_friend_privacylevel", [0x8000001D] = "np_commerce2", [0x8000001E] = "np_kdc",
        [0x8000001F] = "libSceMusicExport", [0x80000020] = "libSceVideoExport", [0x80000021] = "np_message_dialog_impl",
        [0x80000022] = "np_message_contacts", [0x80000023] = "dbrecovery_utility", [0x80000024] = "libScePromoterUtil",
        [0x80000025] = "libult", [0x80000026] = "party_member_list", [0x80000027] = "psmkdc",
        [0x80000028] = "liblocation_internal", [0x80000029] = "liblocation_factory",
    };

    /// <summary>
    /// Libraries VitaSharp implements in HLE (Fiber.cs, Ult.cs, Sas.cs, Atrac.cs). A copy the game ships is still
    /// preferred (the title was tested with it), but the firmware's copy is only used when asked for
    /// (EmulatorOptions.LleModules / VITASHARP_LLE_MODULES), so installing the firmware does not move titles that
    /// work on these HLE modules onto different code.
    /// </summary>
    static readonly HashSet<string> HleImplemented = new(StringComparer.OrdinalIgnoreCase) { "libfiber", "libult", "libsas", "libatrac" };

    /// <summary>
    /// Libraries with an HLE replacement that only runs without the firmware module: the firmware's copy stays the
    /// default when installed, and the HLE (Json.cs) takes over when it is missing or when the library is listed in
    /// the HLE modules option (Json.cs, Xml.cs). Sqlite, face and smart have no HLE.
    /// </summary>
    static readonly HashSet<string> HleFallback = new(StringComparer.OrdinalIgnoreCase) { "libSceJson", "libSceXml" };

    HashSet<string>? _lleModules, _hleModules;

    static HashSet<string> ModuleList(IReadOnlyCollection<string>? option, string envVar) =>
        new(option ?? (Environment.GetEnvironmentVariable(envVar) ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The module options accept the file name ("libSceJson") or the short name ("json", "ult").</summary>
    static bool Listed(HashSet<string> set, string lib) =>
        set.Contains(lib) || (lib.StartsWith("libSce", StringComparison.OrdinalIgnoreCase) && set.Contains(lib[6..]))
        || (lib.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && set.Contains(lib[3..]));

    /// <summary>Where <paramref name="lib"/> may be loaded from, in order (empty: stay on HLE).</summary>
    internal string[] SysmoduleCandidates(string lib)
    {
        _lleModules ??= ModuleList(Emu.Options.LleModules, EnvVars.LleModules);
        _hleModules ??= ModuleList(Emu.Options.HleModules, EnvVars.HleModules);
        if (Listed(_hleModules, lib)) return [];
        string app = $"app0:sce_module/{lib}.suprx";
        // the system software's modules: whichever vs0 module folder holds the file
        string fw = Firmware.FirmwareLibraries.For(Emu.Options.FsRoot).Modules
            .FirstOrDefault(m => string.Equals(Path.GetFileNameWithoutExtension(m.VitaPath), lib, StringComparison.OrdinalIgnoreCase))?.VitaPath
            ?? $"vs0:sys/external/{lib}.suprx";
        if (Emu.IsSystemProgram) return [fw];
        return !HleImplemented.Contains(lib) || Listed(_lleModules, lib) ? [app, fw] : [app];
    }

    /// <summary>Loads app0:sce_module/&lt;lib&gt;.suprx, else (see <see cref="SysmoduleCandidates"/>)
    /// vs0:sys/external/&lt;lib&gt;.suprx, and runs module_start.
    /// Returns 0 (also when no file exists: the HLE fallback stays in place) or a negative error.</summary>
    int TryLoadModuleLle(string lib, uint args, uint argp, out int startResult)
    {
        startResult = 0;
        string[] candidates = SysmoduleCandidates(lib);
        if (candidates.Length == 0)
        {
            Log.Info($"sceSysmoduleLoadModule: {lib} kept on HLE (HLE modules option)");
            return 0;
        }
        foreach (var path in candidates)
        {
            var host = Emu.Vfs.Resolve(path);
            if (host == null || !File.Exists(host)) continue;
            if (K.Modules.Values.Any(m => m.Path == path)) return 0;
            try
            {
                var m = Emu.LoadModule(path);
                // the libraries it imports that nothing provides yet (sysmodule's dependency lists), loaded and started
                // first; a game takes from the firmware only libraries the HLE knows nothing of
                foreach (var dep in Emu.LoadFirmwareDependencies(m))
                    if (dep.StartEntry != 0 && (int)GuestThread.Current!.CallGuest(dep.StartEntry, 0, 0) is var dr and < 0)
                        Log.Warn($"sceSysmoduleLoadModule: dependency {dep.Path} module_start = 0x{dr:X8}");
                if (m.StartEntry != 0) startResult = (int)GuestThread.Current!.CallGuest(m.StartEntry, args, argp);
            }
            catch (Exception e) when (e is not GuestThreadExitException)
            {
                Log.Warn($"sceSysmoduleLoadModule: loading {path} failed: {e.Message}");
                return SYSMODULE_ERROR_FATAL;
            }
            if (startResult < 0) { Log.Warn($"sceSysmoduleLoadModule: {path} module_start = 0x{startResult:X8}"); return startResult; }
            Log.Info($"sceSysmoduleLoadModule: {lib} loaded from {path} (LLE)");
            return 0;
        }
        if (HleImplemented.Contains(lib)) Log.Info($"sceSysmoduleLoadModule: {lib} -> HLE");
        else if (HleFallback.Contains(lib)) Log.Info($"sceSysmoduleLoadModule: {lib}.suprx not installed (no firmware) -> HLE");
        else Log.WarnOnce("sysmodule-" + lib, $"sceSysmoduleLoadModule: {lib}.suprx not found in app0:sce_module or vs0:sys/external " +
                                              "(install the firmware to provide it); its functions stay unimplemented");
        return 0;
    }

    [Hle("sceSysmoduleUnloadModule")]
    [Hle("sceSysmoduleUnloadModuleInternal")]
    int sceSysmoduleUnloadModule(uint id)
    {
        // modules stay resident: the linker cannot undo bindings, and a later load reuses them
        lock (_loadedSysmodules) _loadedSysmodules.Remove(id);
        return 0;
    }

    [Hle]
    int sceSysmoduleUnloadModuleInternalWithArg(uint id, uint args, uint argp, uint option)
    {
        lock (_loadedSysmodules) if (!_loadedSysmodules.Remove(id)) return SYSMODULE_ERROR_UNLOADED;
        if (option != 0 && GuestMemory.R32(option + 4) is var res and not 0) GuestMemory.W32(res, 0);
        return 0;
    }

    [Hle]
    int sceSysmoduleIsLoaded(uint id)
    {
        if (id is 0 or > 0x55) return SYSMODULE_ERROR_INVALID_VALUE;
        lock (_loadedSysmodules) return _loadedSysmodules.Contains(id) ? 0 : SYSMODULE_ERROR_UNLOADED;
    }

    [Hle]
    int sceSysmoduleIsLoadedInternal(uint id)
    {
        lock (_loadedSysmodules) return _loadedSysmodules.Contains(id) ? 0 : SYSMODULE_ERROR_UNLOADED;
    }

    // ------------------------------------------------------------------ module manager

    /// <summary>A system module (vs0:) the process already has loaded: the firmware's shared modules exist once per
    /// process, so loading one again gives the loaded module (PS4 Link loads libfios2 and libc, which the process
    /// preload already brought: a second libc started again on its main thread and ended it there).
    /// Each such load counts a reference that an unload drops (the module stays for the others).</summary>
    /// <summary>
    /// A module the program loads from a file that is not there. Outside vs0: and app0: it is a file the user puts in
    /// the data folder (a module copied from their own console, such as a runtime library a homebrew needs), so the log
    /// says where the emulator looked for it.
    /// </summary>
    void WarnMissingModule(string path)
    {
        if (path.StartsWith("vs0:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("app0:", StringComparison.OrdinalIgnoreCase)) return;
        Log.WarnOnce("missing-module:" + path, $"{path} is missing: the program loads a module that is not in the emulator's data folder " +
            $"({Emu.Vfs.Resolve(path) ?? path}). A module that is not part of the firmware is copied there from your own PS Vita.");
    }

    Loader.LoadedModule? LoadedSystemModule(string path)
    {
        if (!path.StartsWith("vs0:", StringComparison.OrdinalIgnoreCase)) return null;
        var m = K.Modules.Values.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        if (m != null) lock (_openCounts) _openCounts[m.Uid] = _openCounts.GetValueOrDefault(m.Uid, 1) + 1;
        return m;
    }

    /// <summary>An unload of a module loaded more than once only drops one reference.</summary>
    bool DropReference(int uid)
    {
        lock (_openCounts)
        {
            if (_openCounts.GetValueOrDefault(uid, 1) <= 1) return false;
            _openCounts[uid]--;
            return true;
        }
    }

    [Hle]
    int sceKernelLoadStartModule(string path, uint args, uint argp, uint flags, uint opt, Ptr<int> status)
    {
        if (LoadedSystemModule(path) is { } loaded)
        {
            status.TrySet(0);
            return loaded.Uid;
        }
        try
        {
            var m = Emu.LoadModule(path);
            int r = 0;
            if (m.StartEntry != 0)
                r = (int)GuestThread.Current!.CallGuest(m.StartEntry, args, argp);
            status.TrySet(r);
            return m.Uid;
        }
        catch (FileNotFoundException)
        {
            Log.Warn($"sceKernelLoadStartModule: {path} not found");
            WarnMissingModule(path);
            return unchecked((int)0x8002D01C);
        }
        catch (Exception e) when (Emulator.IsModuleLoadFailure(e))
        {
            // the loader refuses it (no SELF, encrypted without the title's license): the module manager's error
            Log.Warn($"sceKernelLoadStartModule: {path}: {e.Message}");
            return Emulator.ModuleLoadError(e);
        }
    }

    [Hle]
    int sceKernelLoadModule(string path, uint flags, uint opt)
    {
        if (LoadedSystemModule(path) is { } loaded) return loaded.Uid;
        try
        {
            return Emu.LoadModule(path).Uid;
        }
        catch (FileNotFoundException)
        {
            WarnMissingModule(path);
            return unchecked((int)0x8002D01C);
        }
        catch (Exception e) when (Emulator.IsModuleLoadFailure(e))
        {
            Log.Warn($"sceKernelLoadModule: {path}: {e.Message}");
            return Emulator.ModuleLoadError(e);
        }
    }

    [Hle]
    int sceKernelStartModule(int uid, uint args, uint argp, uint flags, uint opt, Ptr<int> status)
    {
        if (!K.Modules.TryGetValue(uid, out var m)) return unchecked((int)0x8002D011);
        int r = m.StartEntry != 0 ? (int)GuestThread.Current!.CallGuest(m.StartEntry, args, argp) : 0;
        status.TrySet(r);
        return 0;
    }

    [Hle]
    int sceKernelStopUnloadModule(int uid, uint args, uint argp, uint flags, uint opt, Ptr<int> status)
    {
        if (!K.Modules.TryGetValue(uid, out var m)) return SceError.MODULEMGR_NO_MOD;
        if (DropReference(uid)) { status.TrySet(0); return 0; }
        int r = m.StopEntry != 0 ? (int)GuestThread.Current!.CallGuest(m.StopEntry, args, argp) : 0;
        status.TrySet(r);
        return UnloadModule(m);
    }

    /// <summary>psp2/kernel/modulemgr.h: int (SceUID modid, int flags, SceKernelULMOption *option). Like Vita3K, no
    /// "still started" check: the module is unlinked and its memory freed.</summary>
    [Hle]
    [Hle("_sceKernelUnloadModule")]
    int sceKernelUnloadModule(int uid, uint flags, uint opt)
        => K.Modules.TryGetValue(uid, out var m) ? (DropReference(uid) ? 0 : UnloadModule(m)) : SceError.MODULEMGR_NO_MOD;

    int UnloadModule(Loader.LoadedModule m)
    {
        if (m == Emu.MainModule) return SceError.MODULEMGR_NO_MOD; // the executable is not a loadable module
        lock (_openCounts) _openCounts.Remove(m.Uid);
        Emu.UnloadModule(m);
        return 0;
    }

    readonly Dictionary<int, int> _openCounts = new();

    /// <summary>
    /// sceKernelOpenModule / _sceKernelOpenModule: shared load-and-start. Only the syscall prototype is in the header
    /// (<c>SceUID (const char *path, SceSize args, const void *argp, SceUInt32 flags)</c>); the SceLibKernel wrapper
    /// shares those four arguments, so the trailing option/status words are not touched. Opening a path that is
    /// already loaded returns the same UID and counts one more reference.
    /// </summary>
    [Hle("sceKernelOpenModule")]
    [Hle("_sceKernelOpenModule")]
    int sceKernelOpenModule(string path, uint args, uint argp, uint flags)
    {
        var existing = K.Modules.Values.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            lock (_openCounts) _openCounts[existing.Uid] = _openCounts.GetValueOrDefault(existing.Uid, 1) + 1;
            return existing.Uid;
        }
        Loader.LoadedModule mod;
        try
        {
            mod = Emu.LoadModule(path);
        }
        catch (FileNotFoundException)
        {
            WarnMissingModule(path);
            return unchecked((int)0x8002D01C);
        }
        catch (Exception e) when (Emulator.IsModuleLoadFailure(e))
        {
            Log.Warn($"sceKernelOpenModule: {path}: {e.Message}");
            return Emulator.ModuleLoadError(e);
        }
        // same as sceKernelLoadStartModule: the module stays loaded whatever module_start returns
        if (mod.StartEntry != 0) GuestThread.Current!.CallGuest(mod.StartEntry, args, argp);
        lock (_openCounts) _openCounts[mod.Uid] = 1;
        return mod.Uid;
    }

    /// <summary>sceKernelCloseModule / _sceKernelCloseModule (<c>(SceUID modid, SceSize args, const void *argp,
    /// SceUInt32 flags)</c>): drops one reference; the last one runs module_stop and unloads.</summary>
    [Hle("sceKernelCloseModule")]
    [Hle("_sceKernelCloseModule")]
    int sceKernelCloseModule(int uid, uint args, uint argp, uint flags)
    {
        if (!K.Modules.TryGetValue(uid, out var m)) return SceError.MODULEMGR_NO_MOD;
        lock (_openCounts)
        {
            int n = _openCounts.GetValueOrDefault(uid, 1) - 1;
            if (n > 0) { _openCounts[uid] = n; return 0; }
        }
        if (m.StopEntry != 0) GuestThread.Current!.CallGuest(m.StopEntry, args, argp);
        return UnloadModule(m);
    }

    /// <summary>Runs module_stop(args, argp) and stores its result; the module stays loaded.</summary>
    [Hle]
    int sceKernelStopModule(int uid, uint args, uint argp, uint flags, uint opt, Ptr<int> status)
    {
        if (!K.Modules.TryGetValue(uid, out var m)) return SceError.MODULEMGR_NO_MOD;
        bool shared;
        lock (_openCounts) shared = _openCounts.GetValueOrDefault(uid, 1) > 1;
        if (shared) { status.TrySet(0); return 0; } // still in use by another load: not stopped
        int r = m.StopEntry != 0 ? (int)GuestThread.Current!.CallGuest(m.StopEntry, args, argp) : 0;
        status.TrySet(r);
        return 0;
    }

    /// <summary>Highest SDK version the firmware accepts (same firmware as sceKernelGetSystemSwVersion).</summary>
    [Hle] uint sceKernelGetAllowedSdkVersionOnSystem() => SystemSwVersion;

    /// <summary>1 when <paramref name="lr"/> is inside a firmware module (loaded from vs0:/os0:), 0 for game modules
    /// (sce_module/*.suprx shipped in app0: count as the game's).</summary>
    [Hle]
    int sceKernelIsCalledFromSysModule(uint lr)
    {
        foreach (var m in K.Modules.Values)
            foreach (var s in m.Segments.Values)
                if (lr >= s.Address && lr < s.Address + s.Size)
                    return m.Path.StartsWith("vs0:", StringComparison.OrdinalIgnoreCase) || m.Path.StartsWith("os0:", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return 0;
    }

    /// <summary>
    /// SceKernelLibraryInfo (0x1C bytes) of a library a module exports: +4 the module, +8 libnid, +0xC version, +0xE
    /// attributes, +0x10 function count, +0x12 variable count, +0x14 the NID table and +0x18 the entry table (functions
    /// first, then variables), as libkernel 3.74 reads it back (NID_0A4DF821 looks a variable up in those two tables).
    /// The size word sets how much is written (<see cref="KernelInfo.Write"/>).
    /// </summary>
    [Hle]
    int sceKernelGetLibraryInfoByNID(int modid, uint libNid, uint info)
    {
        if (!K.Modules.TryGetValue(modid, out Loader.LoadedModule? m))
        {
            return SceError.MODULEMGR_NO_MOD;
        }

        if (!FindExportEntry(m, libNid, out ExportEntry entry))
        {
            return unchecked((int)0x8002D003); // MODULEMGR_NO_LIB (a confirmar)
        }

        Span<byte> fields = stackalloc byte[LibraryInfoSize];
        fields.Clear();
        KernelInfo.Put(fields, 0, LibraryInfoSize);
        KernelInfo.Put(fields, 4, modid);
        KernelInfo.Put(fields, 8, libNid);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(fields[0xC..], entry.Version);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(fields[0xE..], entry.Attributes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(fields[0x10..], entry.FunctionCount);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(fields[0x12..], entry.VariableCount);
        KernelInfo.Put(fields, 0x14, entry.NidTable);
        KernelInfo.Put(fields, 0x18, entry.EntryTable);
        return KernelInfo.Write(info, fields);
    }

    [Hle]
    int sceSblACMgrIsGameProgram(Ptr<int> result)
    {
        if (result.IsNull) return SceError.ILLEGAL_ADDR;
        result.Value = Emu.IsGameProgram ? 1 : 0;
        return 0;
    }

    [Hle] int sceKernelCallModuleExit() => 0;

    [Hle]
    int sceKernelGetModuleInfoByAddr(uint addr, uint info)
    {
        foreach (var m in UserModules)
        {
            foreach (var s in m.Segments.Values)
            {
                if (addr < s.Address || addr >= s.Address + s.Size) continue;
                return WriteModuleInfo(m, info);
            }
        }
        return unchecked((int)0x8002D082);
    }

    [Hle]
    int sceKernelGetModuleInfo(int uid, uint info)
    {
        if (!K.Modules.TryGetValue(uid, out var m) || m.IsKernel)
        {
            return unchecked((int)0x8002D011);
        }

        return WriteModuleInfo(m, info);
    }

    /// <summary>The process's own modules: user code does not see the kernel's (its module calls are syscalls into the
    /// process's module list).</summary>
    IEnumerable<Loader.LoadedModule> UserModules => K.Modules.Values.Where(m => !m.IsKernel);

    /// <summary>SceKernelModuleInfo (0x1B8 bytes).</summary>
    internal static int WriteModuleInfo(Loader.LoadedModule m, uint info)
    {
        GuestMemory.Span(info + 4, 0x1B4).Clear();
        GuestMemory.W32(info + 0x00, 0x1B8);
        GuestMemory.W32(info + 0x04, (uint)m.Uid);
        GuestMemory.Write(info + 0x08, m.Attributes);
        GuestMemory.Write(info + 0x0A, m.Version);
        GuestMemory.WriteCString(info + 0x0C, m.Name, 28);
        GuestMemory.W32(info + 0x2C, m.StartEntry);
        GuestMemory.W32(info + 0x30, m.StopEntry);
        GuestMemory.W32(info + 0x34, m.ExitEntry);
        GuestMemory.W32(info + 0x38, m.ExidxTop);
        GuestMemory.W32(info + 0x3C, m.ExidxEnd);
        GuestMemory.W32(info + 0x40, m.ExtabTop);
        GuestMemory.W32(info + 0x44, m.ExtabEnd);
        GuestMemory.W32(info + 0x48, m.TlsInit);
        GuestMemory.W32(info + 0x4C, m.TlsInitSize);
        GuestMemory.W32(info + 0x50, m.TlsAreaSize);
        GuestMemory.WriteCString(info + 0x54, m.Path, 256);
        int i = 0;
        foreach (var (idx, s) in m.Segments.OrderBy(k => k.Key))
        {
            if (i >= 4) break;
            uint p = info + 0x154 + (uint)i * 24;
            var f = m.SegmentFlags[idx];
            GuestMemory.W32(p, 24);
            GuestMemory.W32(p + 4, f.Flags & 7);
            GuestMemory.W32(p + 8, s.Address);
            GuestMemory.W32(p + 12, f.MemSz);
            GuestMemory.W32(p + 16, f.FileSz);
            i++;
        }
        return 0;
    }

    [Hle]
    int sceKernelGetModuleList(int flags, uint uids, Ptr<uint> count)
    {
        var list = UserModules.Select(m => m.Uid).OrderBy(k => k).ToArray();
        int n = (int)Math.Min((uint)list.Length, count.Value);
        for (int i = 0; i < n; i++) GuestMemory.W32(uids + (uint)i * 4, (uint)list[i]);
        count.Value = (uint)n;
        return 0;
    }

    [Hle]
    int sceKernelGetModuleIdByAddr(uint addr)
    {
        foreach (var m in UserModules)

            foreach (var s in m.Segments.Values)
                if (addr >= s.Address && addr < s.Address + s.Size) return m.Uid;
        return unchecked((int)0x8002D082);
    }

    // ------------------------------------------------------------------ SceAppMgr

    /// <summary>An application takes the BGM (games do at boot): BGM priority 0x100 in the kernel's routing
    /// (FW 3.74 appmgr 0x81007154; <see cref="Kernel.VitaSystem.BgmHolder"/>).</summary>
    [Hle] int sceAppMgrAcquireBgmPort() { Emu.BgmPriority = 0x100; return 0; }
    /// <summary>The caller gives the BGM up: priority 0 (appmgr 0x8100735C).</summary>
    [Hle] int sceAppMgrReleaseBgmPort() { Emu.BgmPriority = 0; return 0; }
    [Hle] int sceAppMgrIsGameProgram() => Emu.IsGameProgram ? 1 : 0;
    /// <summary>Third-party applications that are not games (store "apps"); libpaf asserts that the system software
    /// is neither a game nor a non-game program.</summary>
    [Hle] int sceAppMgrIsNonGameProgram() => 0;
    /// <summary>(visibility, colour, transparency): the application's info bar, reported to the shell in its status.</summary>
    [Hle]
    int sceAppMgrSetInfobarState(int visible, int color, int transparency)
    {
        Emu.Hle.Modules.OfType<AppMgr>().FirstOrDefault()?.SetInfobar(visible, color, transparency);
        return 0;
    }
    [Hle] int sceAppMgrSetNetworkDisconnectionWarningDialogState(int state) => 0;
    readonly Queue<(int Event, string? Data)> _systemEvents = new();

    /// <summary>A system event another system program sent this application (sceAppMgrSendSystemEvent/2).</summary>
    internal void PostSystemEvent(int ev, string? data) { lock (_systemEvents) _systemEvents.Enqueue((ev, data)); }

    /// <summary>(SceAppMgrSystemEvent *ev 0x40): the next system event (+0 the event, +4 the 12-character payload of
    /// SendSystemEvent2); 0x80801000 when none is queued.</summary>
    [Hle]
    int sceAppMgrReceiveSystemEvent(uint ev)
    {
        (int Event, string? Data) e;
        lock (_systemEvents) { if (!_systemEvents.TryDequeue(out e)) return unchecked((int)0x80801000); }
        if (ev == 0) return unchecked((int)0x80800001);
        GuestMemory.Fill(ev, 0, 0x40);
        GuestMemory.W32(ev, (uint)e.Event);
        if (e.Data != null) GuestMemory.WriteCString(ev + 4, e.Data, 0xD);
        return 0;
    }

    [Hle("_sceAppMgrGetAppState")]
    int _sceAppMgrGetAppState(uint state, uint size, uint version)
    {
        GuestMemory.Span(state, (int)Math.Min(size, 0x100)).Clear();
        return 0;
    }

    [Hle]
    int sceAppMgrAppParamGetString(int pid, int param, uint buf, uint size)
    {
        const int ERR_NULL = unchecked((int)0x80800001), ERR_VALUE = unchecked((int)0x80801000), ERR_INVALID = unchecked((int)0x8080201A);
        if (buf == 0) return ERR_NULL;
        // pid 0 = the caller; the shell asks about the processes it launched (CATEGORY of a system app it starts)
        var proc = pid == 0 || pid == Emu.Pid ? Emu : Emu.Vita.ByPid(pid);
        if (proc == null) return ERR_INVALID;
        if (!ReferenceEquals(proc, Emu))
            return proc.Hle.Modules.OfType<SystemMisc>().FirstOrDefault() is { } other ? other.sceAppMgrAppParamGetString(0, param, buf, size) : ERR_INVALID;
        // 100/101: CONTENT_ID/TITLE overrides set by sceAppMgrAppParamSetString, falling back to param.sfo
        if (param is 100 or 101)
        {
            lock (AppParamOverrides)
                if (AppParamOverrides.TryGetValue(param, out var ov)) return CopyAppParam(ov, buf, size, ERR_VALUE);
            param = param == 100 ? 6 : 9;
        }
        if (param is < 6 or > 14) return ERR_VALUE;
        string? key = param switch { 6 => "CONTENT_ID", 7 => "NP_COMMUNICATION_ID", 8 => "CATEGORY", 9 => "TITLE", 10 => "STITLE", 12 => "TITLE_ID", _ => null };
        if (key == null) return ERR_INVALID;
        string? v = Emu.ParamSfo?.Values.TryGetValue(key, out var o) == true ? o as string : null;
        if (v == null && param == 12 && !string.IsNullOrEmpty(Emu.TitleId)) v = Emu.TitleId;
        if (v == null) return ERR_INVALID;
        return CopyAppParam(v, buf, size, ERR_VALUE);
    }

    /// <summary>Values set by sceAppMgrAppParamSetString (100 = CONTENT_ID, 101 = TITLE).</summary>
    internal readonly Dictionary<int, string> AppParamOverrides = new();

    static int CopyAppParam(string v, uint buf, uint size, int errTooSmall)
    {
        size = Math.Min(size, 0x400u);
        var bytes = System.Text.Encoding.UTF8.GetBytes(v);
        if ((uint)bytes.Length >= size) return errTooSmall; // needs room for the terminator (Vita3K)
        GuestMemory.WriteCString(buf, v, (int)size);
        return 0;
    }

    /// <summary>(pid, param, int *value). Param 0x10 is the process's enter button (0 circle, 1 cross): the shell's
    /// ime_plugin (FW 3.74, 0x81002B4C) compares it with libpaf's button assignment and, when they differ, swaps the
    /// meaning of circle and cross on its keyboard (circle then confirmed the focused key instead of closing the
    /// keyboard). Every process uses the system setting here. The others are 0 until known.</summary>
    [Hle("sceAppMgrAppParamGetInt")]
    int sceAppMgrAppParamGetInt(int pid, int param, Ptr<int> value)
    {
        value.TrySet(param == 0x10 ? AppUtil.SystemParamInt(2, AppUtil.SystemRegistry(Emu)) : 0);
        return 0;
    }

    [Hle]
    int sceAppMgrLoadExec(string path, uint argv, uint opt)
    {
        // argv: NULL-terminated array of C string pointers
        var args = new List<string>();
        for (uint p = argv; p != 0 && args.Count < 64; p += 4)
        {
            uint s = GuestMemory.R32(p);
            if (s == 0) break;
            args.Add(GuestMemory.ReadCString(s));
        }
        Emu.RequestExec(path, args.ToArray());
        throw new GuestThreadExitException(0, false); // this thread ends; the host restarts the process
    }
    /// <summary>
    /// The memory budget of a system application (SceAppMgrBudgetInfo, 0x88 bytes). Only the system software gets
    /// it; an ordinary application gets an error (Vita3K: "not system mode"). vitaGL calls it to detect a system app
    /// and, when it succeeds, skips its own CDRAM/RAM pools and puts every texture on the newlib heap, which then fills
    /// up on a game (a homebrew port stayed black with "malloc failed, texture skipped"). The exact error code is
    /// not confirmed; callers only test for non-zero.
    /// </summary>
    [Hle]
    int sceAppMgrGetBudgetInfo(uint info)
    {
        if (!Emu.IsSystemProgram) return unchecked((int)0x8080201A); // SCE_APPMGR_ERROR_INVALID (code a confirmar)
        GuestMemory.Span(info + 4, 0x60).Clear();
        return 0;
    }
    [Hle] int sceAppMgrGetRunningAppIdListForShell(uint list, int count) => GetRunningAppIdListForShell(list, count, 0);

    // ------------------------------------------------------------------ ScePower

    // ScePower 3.74 (power.skprx, read in the module, study only): the battery values are what the module last polled
    // from the syscon chip (Emu.Vita.Syscon: present, charged, unplugged by default), or the battery fake status the
    // system set (kscePowerSetBatteryFakeStatus); without a battery the reads answer 0x802B0100. The kernel exports
    // with the same NIDs (kscePower...) are these functions.

    /// <summary>ScePower: no battery (or none polled yet).</summary>
    internal const int POWER_ERROR_NO_BATTERY = unchecked((int)0x802B0100);

    Kernel.SysconChip Chip => Emu.Vita.Syscon;

    Kernel.ScePowerState PowerState => Emu.Vita.PowerState;

    Kernel.SysconBattery Battery => Emu.Vita.Syscon.Battery;

    /// <summary>A battery value: the value with a battery in, else 0x802B0100.</summary>
    int BatteryValue(int value) => Battery.Present ? value : POWER_ERROR_NO_BATTERY;

    /// <summary>The fake status's time left (µs), counting from when it was set (0 when it ran out).</summary>
    long FakeTimeLeftUs()
    {
        long left = PowerState.FakeLifeTimeUs - (GuestClock.Microseconds - PowerState.FakeSetUs);
        return Math.Max(left, 0);
    }

    /// <summary>Low battery: the fake status ran out, else the chip's status bit 17.</summary>
    [Hle]
    int scePowerIsLowBattery()
    {
        if (PowerState.FakeBattery)
        {
            return PowerKernel.FakeRanOut(PowerState, 0) ? 1 : 0;
        }

        return Battery.Present && Chip.StatusBit(Kernel.SysconChip.BitLowBattery) ? 1 : 0;
    }

    /// <summary>The battery charges: on the AC adapter, the chip's charging bit, unless charging is forbidden; not
    /// under the fake status.</summary>
    [Hle]
    int scePowerIsBatteryCharging()
    {
        if (PowerState.FakeBattery)
        {
            return 0;
        }

        if (!Battery.Present)
        {
            return POWER_ERROR_NO_BATTERY;
        }

        if (PowerState.ChargingForbidden != 0 || !Chip.AcAdapter)
        {
            return 0;
        }

        return (Chip.BatteryStatusWord & Kernel.SysconChip.BatteryChargingBit) != 0 ? 1 : 0;
    }

    /// <summary>The charging status: 2 forbidden, on the AC adapter 1 charging / 0 not, off it 3 while USB charges.
    /// </summary>
    [Hle]
    int scePowerGetBatteryChargingStatus()
    {
        if (PowerState.FakeBattery)
        {
            return 0;
        }

        if (!Battery.Present)
        {
            return POWER_ERROR_NO_BATTERY;
        }

        if (PowerState.ChargingForbidden != 0)
        {
            return 2;
        }

        uint status = Chip.BatteryStatusWord;
        if (Chip.AcAdapter)
        {
            return (int)((status & 3) >> 1);
        }

        return PowerState.UsbCharging && (status & 0x18) != 0 ? 3 : 0;
    }

    /// <summary>The AC adapter is plugged (the chip's status byte; never under the fake status).</summary>
    [Hle]
    int scePowerIsPowerOnline() => !PowerState.FakeBattery && Chip.AcAdapter ? 1 : 0;

    [Hle]
    int scePowerGetBatteryLifePercent()
    {
        if (PowerState.FakeBattery)
        {
            return (int)(scePowerGetBatteryRemainCapacity() * 100L / Math.Max(1u, PowerState.FakeFullCapacity));
        }

        return BatteryValue(Battery.LifePercent);
    }

    /// <summary>The time left on the battery (minutes; under the fake status, its seconds by the module's division):
    /// 0 on the AC adapter, at least 1 off it.</summary>
    [Hle]
    int scePowerGetBatteryLifeTime()
    {
        if (PowerState.FakeBattery)
        {
            return (int)(FakeTimeLeftUs() / 1_000_000);
        }

        if (Chip.AcAdapter)
        {
            return 0;
        }

        return Math.Max(Battery.LifeTimeMinutes, 1);
    }

    [Hle]
    int scePowerGetBatteryRemainCapacity()
    {
        if (PowerState.FakeBattery)
        {
            if (PowerState.FakeLifeTimeUs == 0)
            {
                return (int)PowerState.FakeRemainCapacity;
            }

            return (int)(FakeTimeLeftUs() * PowerState.FakeRemainCapacity / PowerState.FakeLifeTimeUs);
        }

        return BatteryValue(Battery.RemainCapacity);
    }

    [Hle]
    int scePowerGetBatteryFullCapacity() => PowerState.FakeBattery ? (int)PowerState.FakeFullCapacity : BatteryValue(Battery.FullCapacity);

    [Hle]
    int scePowerIsBatteryExist() => PowerState.FakeBattery || Battery.Present ? 1 : 0;

    [Hle] int scePowerSetConfigurationMode(int mode) => 0;
    // games may raise the CPU clock (333 default, 444 max for applications): the emulated CPU pacing follows it
    [Hle] internal int scePowerSetArmClockFrequency(int f)
    {
        if (f < 41 || f > 444) return POWER_ERROR_INVALID_VALUE;
        if (Cpu.CpuCore.InstructionsPerSecond > 0) Cpu.CpuCore.InstructionsPerSecond = f * 1_000_000L;
        Log.Info($"scePowerSetArmClockFrequency({f} MHz)");
        return 0;
    }

    /// <summary>The bus clock (1..222 MHz), kept in the system's power state.</summary>
    [Hle]
    internal int scePowerSetBusClockFrequency(int mhz)
    {
        if (mhz < 1 || mhz > 222)
        {
            return POWER_ERROR_INVALID_VALUE;
        }

        PowerState.BusClock = mhz;
        return 0;
    }

    /// <summary>The GPU clock (1..222 MHz).</summary>
    [Hle]
    int scePowerSetGpuClockFrequency(int mhz)
    {
        if (mhz < 1 || mhz > 222)
        {
            return POWER_ERROR_INVALID_VALUE;
        }

        PowerState.GpuClock = mhz;
        return 0;
    }

    /// <summary>The GPU crossbar clock (1..166 MHz).</summary>
    [Hle]
    internal int scePowerSetGpuXbarClockFrequency(int mhz)
    {
        if (mhz < 1 || mhz > 166)
        {
            return POWER_ERROR_INVALID_VALUE;
        }

        PowerState.GpuXbarClock = mhz;
        return 0;
    }

    [Hle] int scePowerGetArmClockFrequency() => (int)((Cpu.CpuCore.InstructionsPerSecond > 0 ? Cpu.CpuCore.InstructionsPerSecond : Cpu.CpuCore.DefaultClockHz) / 1_000_000);

    [Hle]
    int scePowerGetBusClockFrequency() => PowerState.BusClock;

    [Hle]
    int scePowerGetGpuClockFrequency() => PowerState.GpuClock;

    [Hle]
    int scePowerGetGpuXbarClockFrequency() => PowerState.GpuXbarClock;
    /// <summary>The power callbacks of the system (<see cref="Kernel.PowerCallbacks"/>, as ScePower 3.74).</summary>
    [Hle]
    int scePowerRegisterCallback(int cb) => Emu.Vita.Power.Callbacks.Register(Emu, cb);

    [Hle]
    int scePowerUnregisterCallback(int cb) => Emu.Vita.Power.Callbacks.Unregister(Emu, cb);
    [Hle] int scePowerRequestDisplayOn() => 0;
    [Hle] int scePowerRequestDisplayOff() => 0;

    internal const int POWER_ERROR_INVALID_VALUE = unchecked((int)0x802B0000);

    [Hle]
    int scePowerGetBatteryTemp() => BatteryValue(Battery.Temperature);

    [Hle]
    int scePowerGetBatteryVolt() => BatteryValue(Battery.Voltage);

    [Hle]
    int scePowerGetBatterySOH() => BatteryValue(Battery.StateOfHealth);

    [Hle]
    int scePowerGetBatteryCycleCount() => BatteryValue(Battery.CycleCount);

    [Hle]
    int scePowerGetBatteryRemainLevel() => BatteryValue(Battery.Level);

    [Hle]
    int scePowerGetBatteryRemainMaxLevel() => BatteryValue(Battery.MaxLevel);

    /// <summary>(int *elec): the battery current; no battery 0x802B0100.</summary>
    [Hle]
    int scePowerGetBatteryElec(uint elec)
    {
        if (!Battery.Present)
        {
            return POWER_ERROR_NO_BATTERY;
        }

        GuestMemory.W32(elec, (uint)Battery.Current);
        return 0;
    }

    /// <summary>The case temperature: ScePower answers the battery's.</summary>
    [Hle]
    int scePowerGetCaseTemp() => BatteryValue(Battery.Temperature);

    /// <summary>The chip's low battery flags for an update (status bits 29 and 28).</summary>
    [Hle]
    int scePowerIsLowBatteryInhibitUpdateDownload() => Chip.StatusBit(29) ? 1 : 0;

    [Hle]
    int scePowerIsLowBatteryInhibitUpdateReboot() => Chip.StatusBit(28) ? 1 : 0;

    [Hle] int scePowerBatteryUpdateInfo() => 0;

    int _usingWireless;
    [Hle]
    int scePowerSetUsingWireless(int enabled)
    {
        if (enabled is not (0 or 1)) return POWER_ERROR_INVALID_VALUE;
        _usingWireless = enabled;
        return 0;
    }
    [Hle] int scePowerGetUsingWireless() => _usingWireless;

    // power requests: VitaSharp never suspends or restarts
    /// <summary>Standby (the console's suspend; the shell's reaction to a POWER press): the emulation stops until POWER
    /// is pressed again (<see cref="Kernel.SystemPower"/>).</summary>
    [Hle]
    int scePowerRequestSuspend()
    {
        Log.Info("scePowerRequestSuspend");
        Emu.Vita.Power.Suspend();
        return 0;
    }

    /// <summary>"Standby" is ScePower's name for turning the system off (the shell's Power Off): the session ends.</summary>
    [Hle]
    int scePowerRequestStandby()
    {
        Log.Info("scePowerRequestStandby: power off");
        Emu.Vita.Power.PowerOff();
        return 0;
    }
    [Hle] int scePowerRequestColdReset() { Log.Warn("scePowerRequestColdReset (ignored)"); return 0; }
    /// <summary>A suspend is required: under the battery fake status, a minute after its time ran out (the module's
    /// rule); a real battery never asks here (the chip's low battery event goes to the shell).</summary>
    [Hle]
    int scePowerIsSuspendRequired() => PowerKernel.FakeRanOut(PowerState, 60_000_000) ? 1 : 0;
    [Hle] int scePowerIsRequest() => 0;
    [Hle] int scePowerCancelRequest() => 0;
    [Hle] int scePowerSetIdleTimerCount(int count) => 0;

    // ------------------------------------------------------------------ misc system

    /// <summary>Firmware reported to games (3.74), shared with sceKernelGetAllowedSdkVersionOnSystem.</summary>
    internal const uint SystemSwVersion = 0x03740011;

    /// <summary>SceKernelSystemSwVersion (0x28): +0 size, +4 versionString[0x1C], +0x20 version, +0x24 unk_24.
    /// The caller's size field bounds how many bytes are written.</summary>
    [Hle]
    int sceKernelGetSystemSwVersion(uint data) => WriteSystemSwVersion(data);

    /// <summary>The SceKernelSystemSwVersion of <see cref="sceKernelGetSystemSwVersion"/> (also _vshSblGetSystemSwVersion).</summary>
    internal static int WriteSystemSwVersion(uint data)
    {
        if (data == 0) return SceError.ILLEGAL_ADDR;
        Span<byte> full = stackalloc byte[0x28];
        full.Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(full, 0x28);
        "3.74"u8.CopyTo(full[4..]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(full[0x20..], SystemSwVersion);
        uint size = GuestMemory.R32(data);
        if (size == 0 || size > 0x28) size = 0x28;
        full[..(int)size].CopyTo(GuestMemory.Span(data, (int)size));
        GuestMemory.W32(data, size);
        return 0;
    }
}
