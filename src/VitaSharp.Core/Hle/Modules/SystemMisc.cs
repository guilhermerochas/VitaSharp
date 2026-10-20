using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceSysmodule, SceAppMgr, ScePower, SceModulemgr and other small system services.</summary>
public sealed class SystemMisc : HleModule
{
    readonly HashSet<uint> _loadedSysmodules = new();
    const int SYSMODULE_ERROR_INVALID_VALUE = unchecked((int)0x805A1000), SYSMODULE_ERROR_UNLOADED = unchecked((int)0x805A1001),
        SYSMODULE_ERROR_FATAL = unchecked((int)0x805A10FF);

    // ------------------------------------------------------------------ SceSysmodule

    [Hle("sceSysmoduleLoadModule")]
    [Hle("sceSysmoduleLoadModuleInternal")]
    int sceSysmoduleLoadModule(uint id) => LoadSysmodule(id, 0, 0);

    /// <summary>SceSysmoduleOpt (0x10): +0 flags, +4 int *result (module_start's result), +8 unused[2].</summary>
    [Hle]
    int sceSysmoduleLoadModuleInternalWithArg(uint id, uint args, uint argp, uint option)
    {
        int r = LoadSysmodule(id, args, argp, option != 0 ? GuestMemory.R32(option + 4) : 0);
        return r;
    }

    int LoadSysmodule(uint id, uint args, uint argp, uint resultPtr = 0)
    {
        lock (_loadedSysmodules)
            if (_loadedSysmodules.Contains(id)) { if (resultPtr != 0) GuestMemory.W32(resultPtr, 0); return 0; }
        int start = 0;
        if (LleSysmodules.TryGetValue(id, out var lib))
        {
            int r = TryLoadModuleLle(lib, args, argp, out start);
            if (r < 0) return r;
        }
        else Log.Debug($"sceSysmoduleLoadModule(0x{id:X}) -> HLE");
        if (resultPtr != 0) GuestMemory.W32(resultPtr, (uint)start);
        lock (_loadedSysmodules) _loadedSysmodules.Add(id);
        return 0;
    }

    /// <summary>
    /// Sysmodules whose implementation is the firmware/game module itself (no HLE, or LLE preferred): the game may ship
    /// them in app0:sce_module (early-SDK titles bundle libult/libfiber/libface/libsmart) or the user may provide the
    /// firmware's vs0:sys/external. When present they are loaded and started LLE, which rebinds the game's
    /// already-linked HLE import stubs. File names follow Vita3K's load_module table.
    /// </summary>
    static readonly Dictionary<uint, string> LleSysmodules = new()
    {
        [0x0006] = "libfiber",     // FIBER (app0 only: Fiber.cs is the fallback)
        [0x0007] = "libult",       // ULT
        [0x0017] = "libSceXml",    // XML
        [0x001F] = "libSceSqlite", // SQLITE
        [0x0038] = "libface",      // FACE
        [0x0039] = "libsmart",     // SMART
        [0x0055] = "libSceJson",   // JSON
    };

    /// <summary>Loads app0:sce_module/&lt;lib&gt;.suprx, else vs0:sys/external/&lt;lib&gt;.suprx, and runs module_start.
    /// Returns 0 (also when no file exists: the HLE fallback stays in place) or a negative error.</summary>
    int TryLoadModuleLle(string lib, uint args, uint argp, out int startResult)
    {
        startResult = 0;
        string[] candidates = lib == "libfiber" ? [$"app0:sce_module/{lib}.suprx"] : [$"app0:sce_module/{lib}.suprx", $"vs0:sys/external/{lib}.suprx"];
        foreach (var path in candidates)
        {
            var host = Emu.Vfs.Resolve(path);
            if (host == null || !File.Exists(host)) continue;
            if (K.Modules.Values.Any(m => m.Path == path)) return 0;
            try
            {
                var m = Emu.LoadModule(path);
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
        Log.WarnOnce("sysmodule-" + lib, $"sceSysmoduleLoadModule: {lib}.suprx not found in app0:sce_module or vs0:sys/external; using HLE");
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

    [Hle]
    int sceKernelLoadStartModule(string path, uint args, uint argp, uint flags, uint opt, Ptr<int> status)
    {
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
            return unchecked((int)0x8002D01C);
        }
    }

    [Hle]
    int sceKernelLoadModule(string path, uint flags, uint opt)
    {
        try { return Emu.LoadModule(path).Uid; }
        catch (FileNotFoundException) { return unchecked((int)0x8002D01C); }
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
        if (!K.Modules.TryGetValue(uid, out var m)) return unchecked((int)0x8002D011);
        int r = m.StopEntry != 0 ? (int)GuestThread.Current!.CallGuest(m.StopEntry, args, argp) : 0;
        status.TrySet(r);
        return 0;
    }

    /// <summary>Runs module_stop(args, argp) and stores its result; the module stays loaded.</summary>
    [Hle]
    int sceKernelStopModule(int uid, uint args, uint argp, uint flags, uint opt, Ptr<int> status)
    {
        if (!K.Modules.TryGetValue(uid, out var m)) return SceError.MODULEMGR_NO_MOD;
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
    /// Library exported by a module. SceKernelLibraryInfo has no documented body; the layout used is
    /// SceKernelModuleLibraryInfo (0x120, a confirmar): +4 library_id, +8 libnid, +0xC version, +0xE flags,
    /// +0x10 function count, +0x12 variable count, +0x18 library_name[0x100], +0x118 client_count, +0x11C owner module.
    /// </summary>
    [Hle]
    int sceKernelGetLibraryInfoByNID(int modid, uint libNid, uint info)
    {
        if (!K.Modules.TryGetValue(modid, out var m)) return SceError.MODULEMGR_NO_MOD;
        int idx = m.Exports.FindIndex(e => e.LibNid == libNid);
        if (idx < 0) return unchecked((int)0x8002D003); // MODULEMGR_NO_LIB (a confirmar)
        var e = m.Exports[idx];
        Span<byte> f = stackalloc byte[0x120];
        f.Clear();
        KernelInfo.Put(f, 0, 0x120);
        KernelInfo.Put(f, 4, modid);
        KernelInfo.Put(f, 8, libNid);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(f[0x10..], (ushort)e.FuncNids.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(f[0x12..], (ushort)e.VarNids.Length);
        var name = System.Text.Encoding.UTF8.GetBytes(e.Library);
        name.AsSpan(0, Math.Min(name.Length, 0xFF)).CopyTo(f[0x18..]);
        KernelInfo.Put(f, 0x11C, modid);
        return KernelInfo.Write(info, f);
    }

    [Hle]
    int sceSblACMgrIsGameProgram(Ptr<int> result)
    {
        if (result.IsNull) return SceError.ILLEGAL_ADDR;
        result.Value = 1;
        return 0;
    }

    [Hle] int sceKernelCallModuleExit() => 0;

    [Hle]
    int sceKernelGetModuleInfoByAddr(uint addr, uint info)
    {
        foreach (var m in K.Modules.Values)
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
        if (!K.Modules.TryGetValue(uid, out var m)) return unchecked((int)0x8002D011);
        return WriteModuleInfo(m, info);
    }

    /// <summary>SceKernelModuleInfo (0x1B8 bytes).</summary>
    static int WriteModuleInfo(Loader.LoadedModule m, uint info)
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
        var list = K.Modules.Keys.OrderBy(k => k).ToArray();
        int n = (int)Math.Min((uint)list.Length, count.Value);
        for (int i = 0; i < n; i++) GuestMemory.W32(uids + (uint)i * 4, (uint)list[i]);
        count.Value = (uint)n;
        return 0;
    }

    [Hle]
    int sceKernelGetModuleIdByAddr(uint addr)
    {
        foreach (var m in K.Modules.Values)
            foreach (var s in m.Segments.Values)
                if (addr >= s.Address && addr < s.Address + s.Size) return m.Uid;
        return unchecked((int)0x8002D082);
    }

    // ------------------------------------------------------------------ SceAppMgr

    [Hle] int sceAppMgrAcquireBgmPort() => 0;
    [Hle] int sceAppMgrReleaseBgmPort() => 0;
    [Hle] int sceAppMgrIsGameProgram() => 1;
    [Hle] int sceAppMgrSetInfobarState(int visible, int color, int transparency) => 0;
    [Hle] int sceAppMgrSetNetworkDisconnectionWarningDialogState(int state) => 0;
    [Hle] int sceAppMgrReceiveSystemEvent(uint ev) => unchecked((int)0x80801000);
    [Hle] int sceAppMgrReceiveSystemEventNum(Ptr<int> num) { num.TrySet(0); return 0; }

    [Hle("_sceAppMgrGetAppState")]
    int _sceAppMgrGetAppState(uint state, uint size, uint version)
    {
        GuestMemory.Span(state, (int)Math.Min(size, 0x100)).Clear();
        return 0;
    }

    [Hle("sceAppMgrGetAppState")]
    int sceAppMgrGetAppState(uint state) { GuestMemory.Span(state, 0x40).Clear(); return 0; }

    [Hle]
    int sceAppMgrAppParamGetString(int pid, int param, uint buf, uint size)
    {
        const int ERR_NULL = unchecked((int)0x80800001), ERR_VALUE = unchecked((int)0x80801000), ERR_INVALID = unchecked((int)0x8080201A);
        if (buf == 0) return ERR_NULL;
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

    [Hle("sceAppMgrAppParamGetInt")]
    int sceAppMgrAppParamGetInt(int pid, int param, Ptr<int> value) { value.TrySet(0); return 0; }

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
    [Hle] int sceAppMgrGetBudgetInfo(uint info) { GuestMemory.Span(info + 4, 0x60).Clear(); return 0; }
    [Hle] int sceAppMgrGetRunningAppIdListForShell(uint list, int count) => 0;

    // ------------------------------------------------------------------ ScePower

    [Hle] int scePowerIsLowBattery() => 0;
    [Hle] int scePowerIsBatteryCharging() => 1;
    [Hle] int scePowerIsPowerOnline() => 1;
    [Hle] int scePowerGetBatteryLifePercent() => 100;
    [Hle] int scePowerGetBatteryLifeTime() => 300;
    [Hle] int scePowerGetBatteryRemainCapacity() => 2200;
    [Hle] int scePowerGetBatteryFullCapacity() => 2200;
    [Hle] int scePowerIsBatteryExist() => 1;
    [Hle] int scePowerSetConfigurationMode(int mode) => 0;
    [Hle] int scePowerGetConfigurationMode() => 0x80;
    // games may raise the CPU clock (333 default, 444 max for applications): the emulated CPU pacing follows it
    [Hle] int scePowerSetArmClockFrequency(int f)
    {
        if (f < 41 || f > 444) return POWER_ERROR_INVALID_VALUE;
        if (Cpu.CpuCore.InstructionsPerSecond > 0) Cpu.CpuCore.InstructionsPerSecond = f * 1_000_000L;
        Log.Info($"scePowerSetArmClockFrequency({f} MHz)");
        return 0;
    }
    [Hle] int scePowerSetBusClockFrequency(int f) => 0;
    [Hle] int scePowerSetGpuClockFrequency(int f) => 0;
    [Hle] int scePowerSetGpuXbarClockFrequency(int f) => 0;
    [Hle] int scePowerGetArmClockFrequency() => Cpu.CpuCore.InstructionsPerSecond > 0 ? (int)(Cpu.CpuCore.InstructionsPerSecond / 1_000_000) : 444;
    [Hle] int scePowerGetBusClockFrequency() => 222;
    [Hle] int scePowerGetGpuClockFrequency() => 222;
    [Hle] int scePowerGetGpuXbarClockFrequency() => 166;
    [Hle] int scePowerRegisterCallback(int cb) => 0;
    [Hle] int scePowerUnregisterCallback(int cb) => 0;
    [Hle] int scePowerRequestDisplayOn() => 0;
    [Hle] int scePowerRequestDisplayOff() => 0;

    // Virtual battery telemetry: fixed values chosen for a full 3.7 V Li-ion pack on AC (not measured).
    const int POWER_ERROR_INVALID_VALUE = unchecked((int)0x802B0000);
    [Hle] int scePowerGetBatteryTemp() => 3000;              // degrees C x 100
    [Hle] int scePowerGetBatteryVolt() => 4150;              // mV
    [Hle] int scePowerGetBatterySOH() => 100;                // %
    [Hle] int scePowerGetBatteryCycleCount() => 10;
    [Hle] int scePowerGetBatteryRemainLevel() => 4;          // 75-100 %
    [Hle] int scePowerGetBatteryRemainMaxLevel() => 4;
    [Hle] int scePowerGetBatteryElec() => 0;                 // full: no charge current
    [Hle] int scePowerGetCaseTemp() => 3000;
    [Hle] int scePowerGetBatteryChargingStatus() => 0;       // enum unconfirmed
    [Hle] int scePowerIsLowBatteryInhibitUpdateDownload() => 0;
    [Hle] int scePowerIsLowBatteryInhibitUpdateReboot() => 0;
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
    [Hle] int scePowerRequestSuspend() { Log.Info("scePowerRequestSuspend (ignored)"); return 0; }
    [Hle] int scePowerRequestStandby() { Log.Info("scePowerRequestStandby (ignored)"); return 0; }
    [Hle] int scePowerRequestColdReset() { Log.Warn("scePowerRequestColdReset (ignored)"); return 0; }
    [Hle] int scePowerIsSuspendRequired() => 0;
    [Hle] int scePowerIsRequest() => 0;
    [Hle] int scePowerCancelRequest() => 0;
    [Hle] int scePowerSetIdleTimerCount(int count) => 0;

    // ------------------------------------------------------------------ misc system

    [Hle] int sceShutterSoundOutput(int type) => 0;
    /// <summary>Firmware reported to games (3.74), shared with sceKernelGetAllowedSdkVersionOnSystem.</summary>
    internal const uint SystemSwVersion = 0x03740011;

    /// <summary>SceKernelSystemSwVersion (0x28): +0 size, +4 versionString[0x1C], +0x20 version, +0x24 unk_24.
    /// The caller's size field bounds how many bytes are written.</summary>
    [Hle]
    int sceKernelGetSystemSwVersion(uint data)
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
    [Hle] int sceKernelCheckDipsw(int bit) => 0;
}
