using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// VSHLE010: these kernel entries share the C# body of the user call of the same name; on the Vita they are other
// functions (the user call reaches the kernel by its syscall), so a hook on the user call's export does not see them.
#pragma warning disable VSHLE010

// SceAppMgrForDriver (os0:bootimage SceAppMgr 3.74, read in the module, study only): the application manager's kernel
// entries. Most run the same operation as the user call of the same name after the syscall copied its arguments
// (mounts, save data slots, DRM, boot parameters): here they run the user HLE's implementation on the caller's
// arguments, so both sides share one state. The entries the VSH bridge calls are in VshBridge.AppMgrKernel; the
// process ones (kill, destroy, suspend, resume) work on the processes the emulator runs.
public sealed partial class AppMgr
{
    /// <summary>ksceAppMgrAppDataMount(id, mountPoint): as sceAppMgrAppDataMount.</summary>
    [Hle(Nid = 0xB1D3C287, Library = "SceAppMgrForDriver")]
    int ksceAppMgrAppDataMount(int id, uint mountPoint)
    {
        return sceAppMgrAppDataMount(id, mountPoint);
    }

    /// <summary>ksceAppMgrAppDataMountById(id, titleId, mountPoint): as sceAppMgrAppDataMountById.</summary>
    [Hle(Nid = 0x5E311F71, Library = "SceAppMgrForDriver")]
    int ksceAppMgrAppDataMountById(int id, uint titleId, uint mountPoint)
    {
        return sceAppMgrAppDataMountById(id, titleId, mountPoint);
    }

    /// <summary>ksceAppMgrAppMount(param): as the user call without its option block.</summary>
    [Hle(Nid = 0xEF0C9533, Library = "SceAppMgrForDriver")]
    int ksceAppMgrAppMount(uint param)
    {
        return _sceAppMgrAppMount(param, 0);
    }

    /// <summary>ksceAppMgrAppUmount(mountPoint): as the user call without its option block.</summary>
    [Hle(Nid = 0x1DE3B7C4, Library = "SceAppMgrForDriver")]
    int ksceAppMgrAppUmount(uint mountPoint)
    {
        return _sceAppMgrAppUmount(mountPoint, 0);
    }

    /// <summary>ksceAppMgrGameDataMount(appPath, patchPath, rifPath, mountPoint): as sceAppMgrGameDataMount.</summary>
    [Hle(Nid = 0xCE356B2D, Library = "SceAppMgrForDriver")]
    int ksceAppMgrGameDataMount(uint appPath, uint patchPath, uint rifPath, uint mountPoint)
    {
        return sceAppMgrGameDataMount(appPath, patchPath, rifPath, mountPoint);
    }

    /// <summary>ksceAppMgrGetBootParam(selector, bootParam): as sceAppMgrGetBootParam.</summary>
    [Hle(Nid = 0xD2541B4A, Library = "SceAppMgrForDriver")]
    int ksceAppMgrGetBootParam(uint selector, uint bootParam)
    {
        return sceAppMgrGetBootParam(selector, bootParam);
    }

    /// <summary>ksceAppMgrMmsMount(id, mountPoint): as sceAppMgrMmsMount.</summary>
    [Hle(Nid = 0x13D54107, Library = "SceAppMgrForDriver")]
    int ksceAppMgrMmsMount(int id, uint mountPoint)
    {
        return sceAppMgrMmsMount(id, mountPoint);
    }

    /// <summary>ksceAppMgrPhotoMount(param): as sceAppMgrPhotoMount.</summary>
    [Hle(Nid = 0x43A35729, Library = "SceAppMgrForDriver")]
    int ksceAppMgrPhotoMount(uint param)
    {
        return sceAppMgrPhotoMount(param);
    }

    /// <summary>ksceAppMgrTrophyMount(id, pid, mountPoint): as sceAppMgrTrophyMount.</summary>
    [Hle(Nid = 0xE977A833, Library = "SceAppMgrForDriver")]
    int ksceAppMgrTrophyMount(int id, int pid, uint mountPoint)
    {
        return sceAppMgrTrophyMount(id, pid, mountPoint);
    }

    /// <summary>ksceAppMgrUmount(mountPoint): as sceAppMgrUmount.</summary>
    [Hle(Nid = 0xA714BB35, Library = "SceAppMgrForDriver")]
    int ksceAppMgrUmount(uint mountPoint)
    {
        return sceAppMgrUmount(mountPoint);
    }

    /// <summary>ksceAppMgrWorkDirMount(id, mountPoint): as sceAppMgrWorkDirMount.</summary>
    [Hle(Nid = 0x3A0A9B82, Library = "SceAppMgrForDriver")]
    int ksceAppMgrWorkDirMount(int id, uint mountPoint)
    {
        return sceAppMgrWorkDirMount(id, mountPoint);
    }

    /// <summary>ksceAppMgrSaveDataGetQuota(data): as sceAppMgrSaveDataGetQuota.</summary>
    [Hle(Nid = 0xE104C37E, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataGetQuota(uint data)
    {
        return sceAppMgrSaveDataGetQuota(data);
    }

    /// <summary>ksceAppMgrSaveDataMount(data): as sceAppMgrSaveDataMount.</summary>
    [Hle(Nid = 0xE73C9516, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataMount(uint data)
    {
        return sceAppMgrSaveDataMount(data);
    }

    /// <summary>ksceAppMgrSaveDataUmount(data): as sceAppMgrSaveDataUmount.</summary>
    [Hle(Nid = 0x2B080268, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataUmount(uint data)
    {
        return sceAppMgrSaveDataUmount(data);
    }

    /// <summary>ksceAppMgrSaveDataSlotInit(param): as sceAppMgrSaveDataSlotInit.</summary>
    [Hle(Nid = 0x2277F736, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataSlotInit(uint param)
    {
        return sceAppMgrSaveDataSlotInit();
    }

    /// <summary>ksceAppMgrSaveDataSlotCreate(data): as sceAppMgrSaveDataSlotCreate.</summary>
    [Hle(Nid = 0xE6F389B8, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataSlotCreate(uint data)
    {
        return sceAppMgrSaveDataSlotCreate(data);
    }

    /// <summary>ksceAppMgrSaveDataSlotDelete(data): as sceAppMgrSaveDataSlotDelete.</summary>
    [Hle(Nid = 0xB544F2A0, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataSlotDelete(uint data)
    {
        return sceAppMgrSaveDataSlotDelete(data);
    }

    /// <summary>ksceAppMgrSaveDataSlotGetParam(data): as sceAppMgrSaveDataSlotGetParam.</summary>
    [Hle(Nid = 0xD137D486, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataSlotGetParam(uint data)
    {
        return sceAppMgrSaveDataSlotGetParam(data);
    }

    /// <summary>ksceAppMgrSaveDataSlotSetParam(data): as sceAppMgrSaveDataSlotSetParam.</summary>
    [Hle(Nid = 0x7E247E47, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataSlotSetParam(uint data)
    {
        return sceAppMgrSaveDataSlotSetParam(data);
    }

    /// <summary>ksceAppMgrSaveDataSlotGetStatus(data): as sceAppMgrSaveDataSlotGetStatus.</summary>
    [Hle(Nid = 0xEC9D5A35, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataSlotGetStatus(uint data)
    {
        return sceAppMgrSaveDataSlotGetStatus(data);
    }

    /// <summary>ksceAppMgrSaveDataSlotSetStatus(data): as sceAppMgrSaveDataSlotSetStatus.</summary>
    [Hle(Nid = 0x9ADB00FA, Library = "SceAppMgrForDriver")]
    int ksceAppMgrSaveDataSlotSetStatus(uint data)
    {
        return sceAppMgrSaveDataSlotSetStatus(data);
    }

    /// <summary>ksceAppMgrDrmOpen(param): as sceAppMgrDrmOpen.</summary>
    [Hle(Nid = 0xEA75D157, Library = "SceAppMgrForDriver")]
    int ksceAppMgrDrmOpen(uint param)
    {
        return sceAppMgrDrmOpen(param);
    }

    /// <summary>ksceAppMgrDrmClose(param): as sceAppMgrDrmClose.</summary>
    [Hle(Nid = 0x088670A6, Library = "SceAppMgrForDriver")]
    int ksceAppMgrDrmClose(uint param)
    {
        return sceAppMgrDrmClose(param);
    }

    /// <summary>The launch flags the module accepts (+4 of the launch parameters, the other bits 0x80802002).</summary>
    const uint LaunchFlagsRefused = 0x0FD00FFF;

    /// <summary>
    /// ksceAppMgrLaunchAppByPath(path, args, argsSize, ?, launchParam, ?): starts the program at a path as a process
    /// of its own, a development kit's entry (SceAppMgr 3.74 0x8100c504): first ksceSblACMgrIsDevelopmentMode, so a
    /// retail unit answers 0x80020012 (no firmware module imports it; the shell's launches go through the user-side
    /// calls); then refused launch flags 0x80802002, a process being debugged 0x80802025, NULL path 0x80802016, a
    /// path that names no program 0x80800006; the new application's id.
    /// </summary>
    [Hle(Nid = 0xB0A37065, Library = "SceAppMgrForDriver")]
    int ksceAppMgrLaunchAppByPath(uint path, uint args, uint argsSize, uint unused, uint launchParam, uint callback)
    {
        if (Emu.Hle.Modules.OfType<SystemServicesVsh>().First().ksceSblACMgrIsDevelopmentMode() == 0)
        {
            return unchecked((int)0x80020012);
        }

        if (launchParam != 0 && (GuestMemory.R32(launchParam + 4) & LaunchFlagsRefused) != 0)
        {
            return ERR_PARAM;
        }

        if (Emu.Hle.Modules.OfType<ProcessmgrKernel>().First().NID_C715591F() == 1)
        {
            return unchecked((int)0x80802025);
        }

        if (path == 0)
        {
            return ERR_NULL_POINTER;
        }

        return LaunchAtPath(GuestMemory.ReadCString(path, 0x400), []);
    }
}

public sealed partial class AppMgrShell
{
    /// <summary>The module's answer for a process it does not know (0x80802002).</summary>
    const int KERNEL_ERR_NO_PROCESS = unchecked((int)0x80802002);

    /// <summary>The running application process <paramref name="pid"/> (not the shell), or null.</summary>
    Emulator? KernelApplication(int pid)
    {
        return Emu.Vita.ByPid(pid) is { IsShell: false } process ? process : null;
    }

    /// <summary>ksceAppMgrDestroyOtherAppByPid(pid): ends the application of that process (no shell check on the
    /// kernel side); unknown 0x80802012.</summary>
    [Hle(Nid = 0xFC89D33D, Library = "SceAppMgrForDriver")]
    int ksceAppMgrDestroyOtherAppByPid(int pid)
    {
        if (KernelApplication(pid) is not { } process)
        {
            return ERR_NOT_FOUND;
        }

        process.Terminate("ended by ksceAppMgrDestroyOtherAppByPid");
        return 0;
    }

    /// <summary>ksceAppMgrKillProcess(pid): ends the process at once; unknown 0x80802002.</summary>
    [Hle(Nid = 0xD80566DB, Library = "SceAppMgrForDriver")]
    int ksceAppMgrKillProcess(int pid)
    {
        if (KernelApplication(pid) is not { } process)
        {
            return KERNEL_ERR_NO_PROCESS;
        }

        process.Terminate("ended by ksceAppMgrKillProcess");
        return 0;
    }

    /// <summary>NID_0C5D65F1(pid): the same kill for a process the system started; unknown 0x80802002.</summary>
    [Hle(Nid = 0x0C5D65F1, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_0C5D65F1(int pid)
    {
        return ksceAppMgrKillProcess(pid);
    }

    /// <summary>A process call of the kernel's own (the module requires the kernel process as caller): a known
    /// process 0, else 0x80802002.</summary>
    int KernelProcessCall(int pid)
    {
        return KernelApplication(pid) != null ? 0 : KERNEL_ERR_NO_PROCESS;
    }

    /// <summary>NID_29421DCD(pid): the process's suspend request: 0 for a running application.</summary>
    [Hle(Nid = 0x29421DCD, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_29421DCD(int pid)
    {
        return KernelProcessCall(pid);
    }

    /// <summary>NID_F3B1D42E(pid): its second suspend step.</summary>
    [Hle(Nid = 0xF3B1D42E, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_F3B1D42E(int pid)
    {
        return KernelProcessCall(pid);
    }

    /// <summary>NID_F9A61E94(pid): the process's resume request.</summary>
    [Hle(Nid = 0xF9A61E94, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_F9A61E94(int pid)
    {
        return KernelProcessCall(pid);
    }

    /// <summary>NID_EE6047CA(pid): its second resume step.</summary>
    [Hle(Nid = 0xEE6047CA, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_EE6047CA(int pid)
    {
        return KernelProcessCall(pid);
    }

    /// <summary>A process query with an output word: NULL 0x80802016, unknown 0x80802002; the word 0 (no state the
    /// module keeps for it).</summary>
    int KernelProcessQuery(int pid, uint output)
    {
        if (output == 0)
        {
            return ERR_NULL;
        }

        if (KernelApplication(pid) == null)
        {
            return KERNEL_ERR_NO_PROCESS;
        }

        GuestMemory.W32(output, 0);
        return 0;
    }

    /// <summary>NID_21BE167A(pid, state): the process's suspend state.</summary>
    [Hle(Nid = 0x21BE167A, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_21BE167A(int pid, uint state)
    {
        return KernelProcessQuery(pid, state);
    }

    /// <summary>NID_4FA62A46(pid, state): the process's resume state.</summary>
    [Hle(Nid = 0x4FA62A46, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_4FA62A46(int pid, uint state)
    {
        return KernelProcessQuery(pid, state);
    }

    /// <summary>NID_DC13160A(pid, data): hands data to an application's entry: NULL 0x80802016, unknown
    /// 0x80802002; 0.</summary>
    [Hle(Nid = 0xDC13160A, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_DC13160A(int pid, uint data)
    {
        if (data == 0)
        {
            return ERR_NULL;
        }

        return KernelProcessCall(pid);
    }

    /// <summary>NID_2FEACA9F(): the id of the foreground application (the module's current entry), 0 with none.</summary>
    [Hle(Nid = 0x2FEACA9F, Library = "SceAppMgrForDriver", ReturnsValue = true)]
    int ksceAppMgr_2FEACA9F()
    {
        return Emu.Vita.Foreground is { IsShell: false } foreground ? foreground.AppId : 0;
    }

    /// <summary>The kernel entries' answer to a caller that is not the kernel process (0x8080201F).</summary>
    const int KERNEL_ERR_NOT_KERNEL = unchecked((int)0x8080201F);

    /// <summary>An empty title id (0x8080201A).</summary>
    const int KERNEL_ERR_EMPTY_NAME = unchecked((int)0x8080201A);

    /// <summary>The launch flags of the debug launch (NID_1B6C2FED).</summary>
    const int DebugLaunchFlags = 0x20000;

    /// <summary>
    /// The kernel's launch request by title id (SceAppMgr 3.74 0x8100b748, the same as sceAppMgrLaunchAppByName's
    /// 0x8100D89C): the application's entry with the flags, the parameter and the caller's <paramref name="callback"/>,
    /// and shell event 6; the shell's second stage creates the process and notifies the callback with its ID. With no
    /// shell up the application starts at once, as for the user call, and the callback gets the ID once it started. An
    /// error, or 0.
    /// </summary>
    int KernelLaunchRequestByName(string titleId, string? param, int flags, int callback)
    {
        string? dir = Io.FirmwareLayout.FindApplication(guest => Emu.Vfs.Resolve(guest), titleId);
        if (dir == null)
        {
            return unchecked((int)0x80800006);
        }

        if (Emu.Vita.Shell != null)
        {
            PendingLaunch pendingLaunch = Emu.Vita.RequestLaunchByName(Emu, titleId, dir, flags, param);
            pendingLaunch.Callback = callback;
            return 0;
        }

        ObjectTable objects = Emu.Kernel.Objects;
        Emulator app;
        try
        {
            app = Emu.SpawnProcess(dir, "eboot.bin", [], background: true, started: started => objects.Get<Callback>(callback)?.Notify(started.Pid));
        }
        catch (ProcessLoadException e)
        {
            return e.Error;
        }

        if (param != null)
        {
            app.Hle.Modules.OfType<AppMgr>().First().LaunchParam = param;
        }

        return 0;
    }

    /// <summary>NID_99AA37C1(name): the kernel's launch of an application by title id (SceAppMgr 3.74 0x8100dc08):
    /// only from the kernel process (0x8080201F otherwise), NULL 0x80802016, empty 0x8080201A; then the launch request
    /// with no flags, parameter or callback (<see cref="KernelLaunchRequestByName"/>): its error, or 0.</summary>
    [Hle(Nid = 0x99AA37C1, Library = "SceAppMgrForDriver")]
    int ksceAppMgr_99AA37C1(uint name)
    {
        if (ProcessScope.CurrentPid != KernelSpace.KernelPid)
        {
            return KERNEL_ERR_NOT_KERNEL;
        }

        if (name == 0)
        {
            return ERR_NULL;
        }

        if (GuestMemory.Read<byte>(name) == 0)
        {
            return KERNEL_ERR_EMPTY_NAME;
        }

        int result = KernelLaunchRequestByName(GuestMemory.ReadCString(name, 0x20), null, 0, 0);
        return result < 0 ? result : 0;
    }

    /// <summary>
    /// NID_1B6C2FED(name, param): a debugger's launch of an application by title id that returns once the process
    /// exists (SceAppMgr 3.74 0x8100da9c). ksceSblACMgrIsDevelopmentMode first (a retail unit 0x80020012), then only
    /// from the kernel process (0x8080201F). It creates the semaphore "SceAppMgr:semafordbg" (0 of 1) and the callback
    /// "SceAppMgr:cbkfordbg", whose function signals it; NULL 0x80802016 and empty 0x8080201A are the launch's result,
    /// otherwise the launch request with flags 0x20000, the parameter and that callback
    /// (<see cref="KernelLaunchRequestByName"/>). Then, whatever the launch's result, it waits on the semaphore running
    /// callbacks: the shell's second stage notifies the callback with the new process's ID, the callback signals the
    /// semaphore; as on the console a launch that was not made is never signalled, and the call does not return. It
    /// deletes the semaphore and the callback and returns the launch's error, or 0.
    /// </summary>
    [Hle(Nid = 0x1B6C2FED, Library = "SceAppMgrForDriver", Cost = HleCost.SchedulerWait)]
    int ksceAppMgr_1B6C2FED(uint name, uint param)
    {
        if (Emu.Hle.Modules.OfType<SystemServicesVsh>().First().ksceSblACMgrIsDevelopmentMode() == 0)
        {
            return unchecked((int)0x80020012);
        }

        if (ProcessScope.CurrentPid != KernelSpace.KernelPid)
        {
            return KERNEL_ERR_NOT_KERNEL;
        }

        ThreadMgr threads = Emu.Hle.Modules.OfType<ThreadMgr>().First();
        int semaphore = threads.sceKernelCreateSema("SceAppMgr:semafordbg", 0, 0, 1, 0);
        if (semaphore < 0)
        {
            return semaphore;
        }

        // the firmware's callback (0x8100a918) gets the semaphore's UID through its argument and signals it
        uint function = DebugLaunchStubs.Host("cbkfordbg", static context =>
        {
            ThreadMgr owner = context.Emu.Hle.Modules.OfType<ThreadMgr>().First();
            owner.sceKernelSignalSema((int)context.Gpr(3), 1);
            context.Return(0);
        });
        int callback = threads.sceKernelCreateCallback("SceAppMgr:cbkfordbg", 0, function, (uint)semaphore);
        if (callback < 0)
        {
            threads.sceKernelDeleteSema(semaphore);
            return callback;
        }

        int launch;
        if (name == 0)
        {
            launch = ERR_NULL;
        }
        else if (GuestMemory.Read<byte>(name) == 0)
        {
            launch = KERNEL_ERR_EMPTY_NAME;
        }
        else
        {
            string? launchParam = param != 0 ? GuestMemory.ReadCString(param, 0x400) : null;
            launch = KernelLaunchRequestByName(GuestMemory.ReadCString(name, 0x20), launchParam, DebugLaunchFlags, callback);
        }

        // SceThreadmgrForDriver F55E4D86, the semaphore wait that runs the thread's callbacks
        GuestThread? self = GuestThread.Current;
        bool callbackWait = self?.CallbackWait ?? false;
        if (self != null)
        {
            self.CallbackWait = true;
        }

        try
        {
            threads.sceKernelWaitSema(semaphore, 1, default);
        }
        finally
        {
            if (self != null)
            {
                self.CallbackWait = callbackWait;
            }
        }

        threads.sceKernelDeleteSema(semaphore);
        threads.sceKernelDeleteCallback(callback);
        return launch < 0 ? launch : 0;
    }

    // rebuilt on first use after a restore (the stub block is guest memory the snapshot keeps)
    [SnapshotIgnore]
    HleGuestStubs? _debugLaunchStubs;

    /// <summary>The guest address of the debug launch's callback function.</summary>
    HleGuestStubs DebugLaunchStubs => _debugLaunchStubs ??= new HleGuestStubs(this, "SceAppMgrDebugLaunch");

    /// <summary>NID_C4924946(title ID, type, path, arg size, args, option): a development kit's process launch through
    /// the process manager (SceAppMgr 3.74 0x8100282c): ksceSblACMgrIsDevelopmentMode first (a retail unit
    /// 0x80020012), then SceProcessmgrForKernel NID_5A57766C creates and starts the process with the arguments as
    /// given (the option is not copied, unlike NID_324DD34E) and the GXM debugging setting of the registry
    /// (<see cref="AppMgr.GxmDebugging"/>); its result: the new process's ID or the process manager's error.</summary>
    [Hle(Nid = 0xC4924946, Library = "SceAppMgrForDriver", Cost = HleCost.SchedulerWait)]
    int ksceAppMgr_C4924946(uint titleId, uint type, uint path, uint argSize, uint args, uint option)
    {
        if (Emu.Hle.Modules.OfType<SystemServicesVsh>().First().ksceSblACMgrIsDevelopmentMode() == 0)
        {
            return unchecked((int)0x80020012);
        }

        ProcessmgrKernel processmgr = Emu.Hle.Modules.OfType<ProcessmgrKernel>().First();
        return processmgr.NID_5A57766CThroughEntry(titleId, type, path, argSize, args, option, AppMgr.GxmDebugging(Emu, KernelSpace.KernelPid));
    }
}

public sealed partial class SystemMisc
{
    /// <summary>ksceAppMgrAppParamGetInt(pid, param, value, ?): as sceAppMgrAppParamGetInt.</summary>
    [Hle(Nid = 0x12CE29F7, Library = "SceAppMgrForDriver")]
    int ksceAppMgrAppParamGetInt(int pid, int param, uint value, uint unused)
    {
        return sceAppMgrAppParamGetInt(pid, param, new Ptr<int>(value));
    }
}
