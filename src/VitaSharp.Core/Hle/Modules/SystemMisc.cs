using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceSysmodule, SceAppMgr, ScePower, SceModulemgr and other small system services.</summary>
public sealed class SystemMisc : HleModule
{
    readonly HashSet<uint> _loadedSysmodules = new();

    // ------------------------------------------------------------------ SceSysmodule

    [Hle("sceSysmoduleLoadModule")]
    [Hle("sceSysmoduleLoadModuleInternal")]
    [Hle("sceSysmoduleLoadModuleInternalWithArg")]
    int sceSysmoduleLoadModule(uint id)
    {
        bool first;
        lock (_loadedSysmodules) first = _loadedSysmodules.Add(id);
        if (first && LleSysmodules.TryGetValue(id, out var lib) && TryLoadShippedModule(lib)) return 0;
        Log.Debug($"sceSysmoduleLoadModule(0x{id:X}) -> HLE");
        return 0;
    }

    /// <summary>
    /// Sysmodules that a game may ship itself in app0:sce_module (early-SDK titles bundle libult/libfiber/...):
    /// when present they are loaded and started LLE, which rebinds the game's already-linked HLE import stubs.
    /// </summary>
    static readonly Dictionary<uint, string> LleSysmodules = new()
    {
        [0x0006] = "libfiber",
        [0x0007] = "libult",
    };

    bool TryLoadShippedModule(string lib)
    {
        string path = $"app0:sce_module/{lib}.suprx";
        var host = Emu.Vfs.Resolve(path);
        if (host == null || !File.Exists(host)) return false;
        if (K.Modules.Values.Any(m => m.Path == path)) return true;
        var m = Emu.LoadModule(path);
        if (m.StartEntry != 0) GuestThread.Current!.CallGuest(m.StartEntry, 0, 0);
        Log.Info($"sceSysmoduleLoadModule: {lib} loaded from the game (LLE)");
        return true;
    }

    [Hle("sceSysmoduleUnloadModule")]
    [Hle("sceSysmoduleUnloadModuleInternal")]
    int sceSysmoduleUnloadModule(uint id)
    {
        lock (_loadedSysmodules) _loadedSysmodules.Remove(id);
        return 0;
    }

    [Hle("sceSysmoduleIsLoaded")]
    [Hle("sceSysmoduleIsLoadedInternal")]
    int sceSysmoduleIsLoaded(uint id)
    {
        lock (_loadedSysmodules) return _loadedSysmodules.Contains(id) ? 0 : unchecked((int)0x805A1002);
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
        string v = param switch
        {
            9 => Emu.ParamSfo?.Category ?? "gd",
            8 => Emu.ParamSfo?.Title ?? "",
            12 => Emu.TitleId,
            _ => "",
        };
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
        if (f < 41 || f > 444) return unchecked((int)0x802B0002); // SCE_POWER_ERROR_INVALID_VALUE
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

    // ------------------------------------------------------------------ misc system

    [Hle] int sceShutterSoundOutput(int type) => 0;
    [Hle] int sceKernelGetSystemSwVersion(uint data) { GuestMemory.WriteCString(data + 4, "3.74"); GuestMemory.W32(data + 0x14, 0x03740011); return 0; }
    [Hle] int sceKernelCheckDipsw(int bit) => 0;
}
