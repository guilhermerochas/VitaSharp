using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceAppMgr (the kernel appmgr in os0:kd/bootimage.skprx, FW 3.74) and the SceAppMgrUser wrappers of
// driver_us.suprx in front of its syscalls. The wrappers pass their arguments through and add a scratch option block
// (an 8/0xC/0x10/0x14-byte frame of the wrapper whose first word the kernel reads as a size); _sceAppMgrX(args, opt)
// and sceAppMgrX(args) therefore share one implementation. The kernel's checks are kept: shell-only calls refuse
// other processes with 0x8080201C (0x8080201F for "system programs only"), a NULL output 0x80802016, an unknown
// application 0x80802012, a path or name without its terminator inside the kernel buffer 0x8002710B.

public sealed partial class AppMgrShell
{
    const int ERR_SHELL_ONLY = unchecked((int)0x8080201C), ERR_TOO_LONG = unchecked((int)0x8002710B),
        ERR_PARAM = unchecked((int)0x80802002);

    static string? KernelString(uint p, int max)
    {
        if (p == 0) return null;
        var s = GuestMemory.ReadCString(p, max);
        return s.Length >= max ? null : s;
    }

    // ---- ending applications
    /// <summary>(SceUID appId): a system program ends an application (the kernel looks the entry up and destroys it
    /// with reason 1; 0x8080201F for other callers, 0x80802012 for an unknown id). Content Manager and the
    /// PS Store end the game they are about to change this way.</summary>
    [Hle]
    int sceAppMgrQuitApp(int appId)
    {
        if (!Emu.IsSystemProgram) return ERR_NOT_SHELL;
        if (Emu.Vita.ByAppId(appId) is not { } p) return ERR_NOT_FOUND;
        p.Terminate($"quit by {Emu.Describe()} (sceAppMgrQuitApp)");
        return 0;
    }

    /// <summary>Ends every application except the shell and the caller (the game that must run alone, the system
    /// update): the shell's LiveArea "close" for all. Only system programs (0x8080201F).</summary>
    [Hle]
    int sceAppMgrDestroyOtherApp()
    {
        if (!Emu.IsSystemProgram) return ERR_NOT_SHELL;
        foreach (var p in Emu.Vita.Processes)
            if (!p.IsShell && p != Emu) p.Terminate($"ended by {Emu.Describe()} (sceAppMgrDestroyOtherApp)");
        return 0;
    }

    /// <summary>(SceUID pid): the shell ends an application by process id (0x8080201C for other callers, 0x80802012
    /// when no application runs in that process).</summary>
    [Hle]
    int sceAppMgrDestroyOtherAppByPidForShell(int pid)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        if (Emu.Vita.ByPid(pid) is not { IsShell: false } p) return ERR_NOT_FOUND;
        p.Terminate("ended by the shell (sceAppMgrDestroyOtherAppByPidForShell)");
        return 0;
    }

    /// <summary>A game leaves for its LiveArea (the "exit" a game offers): the shell gets event 0x10 for it, as for the
    /// PS button. Only a game program (the kernel asks ksceSblACMgrIsNonGameProgram and refuses it).</summary>
    [Hle]
    int sceAppMgrExitToLiveboardForGameApp()
    {
        if (Emu.IsShell || Emu.Vita.Shell == null) return ERR_NOT_FOUND;
        Emu.Vita.PostShellEvent(Kernel.ShellEvents.ShowLiveArea, Emu.AppId);
        return 0;
    }

    // ---- background applications (the shell suspends and resumes them)
    /// <summary>(SceUID appId): the shell suspends a background application (music player, downloads).</summary>
    [Hle]
    int sceAppMgrSuspendBgAppByShell(int appId)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        if (Emu.Vita.ByAppId(appId) is not { } p) return ERR_NOT_FOUND;
        if (p != Emu) p.Suspend();
        return 0;
    }

    [Hle]
    int sceAppMgrResumeBgAppByShell(int appId)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        if (Emu.Vita.ByAppId(appId) is not { } p) return ERR_NOT_FOUND;
        p.Resume();
        return 0;
    }

    /// <summary>(int mode): the shell tells appmgr whether the LiveArea (liveboard) is up; the kernel captures the
    /// front application's framebuffer for the LiveArea picture when it is. The shell already draws that picture from
    /// sceAppMgrCaptureFrameBufIFTUByAppId; the mode is kept.</summary>
    [Hle]
    int sceAppMgrNotifyLiveBoardModeForShell(int mode)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        _liveBoardMode = mode;
        return 0;
    }
    int _liveBoardMode;

    /// <summary>(SceUID appId, int result): the shell answers a LiveArea operation an application asked for (the kernel
    /// signals the waiting application). Nothing waits on it here: the answer is accepted for a known application.</summary>
    [Hle]
    int sceAppMgrReturnLiveAreaOperationResultForShell(int appId, int result)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        return Emu.Vita.ByAppId(appId) == null ? ERR_NOT_FOUND : 0;
    }

    // ---- process lists
    /// <summary>(SceUID *pids, int count[, opt: size = count*4 &lt;= 0x50]): the processes of the running applications,
    /// the shell first; returns how many were written (only the shell may ask).</summary>
    [Hle("sceAppMgrGetPidListForShell")]
    [Hle("_sceAppMgrGetPidListForShell")]
    int GetPidListForShell(uint pids, int count, uint opt)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        if ((uint)count * 4 > 0x50) return ERR_TOO_LONG;
        if (pids == 0) return ERR_NULL;
        int n = 0;
        foreach (var p in Emu.Vita.Processes)
        {
            if (n >= count) break;
            GuestMemory.W32(pids + (uint)n++ * 4, (uint)p.Pid);
        }
        return n;
    }

    /// <summary>(SceUID pid, char path[0x124]): where an application's app0: comes from, by process id.</summary>
    [Hle]
    int _sceAppMgrGetRawPathOfApp0ByPidForShell(int pid, uint path)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        if (path == 0) return ERR_NULL;
        if (Emu.Vita.ByPid(pid) is not { AppPath.Length: > 0 } p) return ERR_NOT_FOUND;
        GuestMemory.WriteCString(path, GuestPath(p.AppPath), 0x124);
        return 0;
    }

    /// <summary>
    /// SceAppMgrAppInfo (0x80): the application entry of an application by name (title id), as GetAppInfo/GetFgAppInfo
    /// copy it: the same fields as the status block (+0x0C name, +0x30 application id, +0x34 process id, +0x38 in
    /// front, +0x3C running, +0x50 title id).
    /// </summary>
    [Hle("sceAppMgrGetAppInfo")]
    [Hle("_sceAppMgrGetAppInfo")]
    int GetAppInfo(uint name, uint info, uint opt)
    {
        var tid = KernelString(name, 0x20);
        if (name != 0 && tid == null) return ERR_TOO_LONG;
        if (info == 0) return ERR_NULL;
        var p = tid == null ? Emu : Emu.Vita.Processes.FirstOrDefault(e => string.Equals(e.TitleId, tid, StringComparison.OrdinalIgnoreCase));
        if (p == null) { GuestMemory.Fill(info, 0, 0x80); return ERR_NOT_FOUND; }
        WriteStatus(p, info);
        return 0;
    }

    /// <summary>(SceAppMgrAppInfo *info 0x80): the entry of the application in front (the shell when none is).</summary>
    [Hle]
    int _sceAppMgrGetFgAppInfo(uint info)
    {
        if (info == 0) return ERR_NULL;
        WriteStatus(Emu.Vita.Foreground ?? Emu.Vita.Shell ?? Emu, info);
        return 0;
    }

    /// <summary>(SceAppMgrCoredumpState *state 0x428): what the kernel knows about a core dump in progress (the
    /// crash reporter's progress bar). No process ever dumps here: the block is zero (no dump, no progress).</summary>
    [Hle("sceAppMgrGetCoredumpStateForShell")]
    [Hle("_sceAppMgrGetCoredumpStateForShell")]
    int GetCoredumpStateForShell(uint state)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        if (state == 0) return ERR_NULL;
        GuestMemory.Fill(state, 0, 0x428);
        return 0;
    }

    /// <summary>(SceUID pid): the shell lets appmgr finish a crashed process's dump (none here: 0x80802012).</summary>
    [Hle]
    int sceAppMgrFinishCoredumpForShell(int pid)
    {
        if (!Emu.IsShell) return ERR_SHELL_ONLY;
        return ERR_NOT_FOUND;
    }

    // ---- launching from the shell
    /// <summary>(const char *name, const char *param, SceAppMgrLaunchAppOptParam *opt[, kernel opt 0x10]): the
    /// shell starts a helper application in the background (the email/calendar background services): the same as
    /// sceAppMgrLaunchAppByName from the shell, the flags in opt (+4).</summary>
    [Hle("sceAppMgrLaunchAppByNameForShell")]
    [Hle("_sceAppMgrLaunchAppByNameForShell")]
    int LaunchAppByNameForShell(uint name, uint param, uint optParam)
    {
        if (!Emu.IsShell) return ERR_NOT_SHELL;
        if (name == 0) return ERR_NULL;
        if (KernelString(name, 0x20) == null || (param != 0 && KernelString(param, 0x400) == null)) return ERR_TOO_LONG;
        int flags = optParam != 0 ? (int)GuestMemory.R32(optParam + 4) : 0;
        return sceAppMgrLaunchAppByName(flags, name, param);
    }

    /// <summary>(const char *name, const char *param, SceAppMgrLaunchParam *launchParam 0x40[, opt]): the second form
    /// the shell uses (LaunchAppByName2 with the shell's rights): returns the application id.</summary>
    [Hle("sceAppMgrLaunchAppByName2ForShell")]
    [Hle("_sceAppMgrLaunchAppByName2ForShell")]
    int LaunchAppByName2ForShell(uint name, uint param, uint launchParam)
    {
        if (!Emu.IsShell) return ERR_NOT_SHELL;
        if (name == 0) return ERR_NULL;
        if (KernelString(name, 0x20) == null || (param != 0 && KernelString(param, 0x400) == null)) return ERR_TOO_LONG;
        int flags = launchParam != 0 ? (int)GuestMemory.R32(launchParam + 4) : 0;
        return sceAppMgrLaunchAppByName(flags, name, param);
    }

    /// <summary>(const void *extra, SceSize size, opt 0x40): starts the PS Vita TV video streaming application (PS Now /
    /// Remote Play client) from a system program. This unit has no video streaming application (it is a PS TV
    /// feature): 0x80800006, the "application not found" of the launch path.</summary>
    [Hle]
    int _sceAppMgrLaunchVideoStreamingApp(uint extra, uint size, uint opt)
    {
        if (opt == 0) return ERR_NULL;
        return unchecked((int)0x80800006);
    }

    /// <summary>Whether the caller is a PS Now client application: no PS Now here.</summary>
    [Hle] int sceAppMgrIsPsNowClient() => 0;

    // ---- events and parameters between applications
    /// <summary>(const SceAppMgrNotificationRequest *req 0x40): an application asks the shell to show a notification
    /// (a download finished, a friend request); the kernel queues it and the shell picks it up with
    /// sceAppMgrReceiveNotificationRequestForShell.</summary>
    [Hle("sceAppMgrSendNotificationRequest")]
    [Hle("_sceAppMgrSendNotificationRequest")]
    int SendNotificationRequest(uint req)
    {
        if (req == 0) return ERR_NULL;
        var data = GuestMemory.ReadSpan(req, 0x40).ToArray();
        var q = Emu.Vita.NotificationRequests;
        lock (q)
        {
            if (q.Count >= 16) return unchecked((int)0x80028102); // the kernel's queue semaphore is full
            q.Enqueue(data);
        }
        return 0;
    }

    /// <summary>(SceAppMgrNotificationRequest *request 0x40): the next request an application sent
    /// (sceAppMgrSendNotificationRequest), 0x80802013 when there is none.</summary>
    internal int TakeNotification(uint request)
    {
        if (request == 0) return ERR_NULL;
        byte[]? d;
        var q = Emu.Vita.NotificationRequests;
        lock (q) d = q.Count > 0 ? q.Dequeue() : null;
        if (d == null) return ERR_NO_EVENT;
        d.CopyTo(GuestMemory.Span(request, 0x40));
        return 0;
    }

    /// <summary>(int event, const char *name 0x20[, opt]): a system program sends a system event to an application
    /// by title id (resume, cancel...): the application's next sceAppMgrReceiveSystemEvent gets it.</summary>
    [Hle("sceAppMgrSendSystemEvent")]
    [Hle("_sceAppMgrSendSystemEvent")]
    int SendSystemEvent(int ev, uint name, uint opt) => SendSystemEventTo(ev, name, 0);

    /// <summary>(int event, const char *name 0x20, const char *data 0xD[, opt]): the same with a 12-character payload.</summary>
    [Hle("sceAppMgrSendSystemEvent2")]
    [Hle("_sceAppMgrSendSystemEvent2")]
    int SendSystemEvent2(int ev, uint name, uint data, uint opt)
    {
        if (data != 0 && KernelString(data, 0xD) == null) return ERR_TOO_LONG;
        return SendSystemEventTo(ev, name, data);
    }

    int SendSystemEventTo(int ev, uint name, uint data)
    {
        if (!Emu.IsSystemProgram) return ERR_NOT_SHELL;
        var tid = KernelString(name, 0x20);
        if (name != 0 && tid == null) return ERR_TOO_LONG;
        var p = tid == null ? null : Emu.Vita.Processes.FirstOrDefault(e => string.Equals(e.TitleId, tid, StringComparison.OrdinalIgnoreCase));
        if (p == null) return ERR_NOT_FOUND;
        p.Hle.Modules.OfType<SystemMisc>().FirstOrDefault()?.PostSystemEvent(ev, data != 0 ? GuestMemory.ReadCString(data, 0xD) : null);
        return 0;
    }

    /// <summary>(int flags, const char *name 0x20, const char *param 0x400, const void *extra 0x80[, opt]): a system
    /// program hands launch parameters to an application by title id (an already running one gets them as its
    /// next sceAppMgrGetAppParam; the kernel's "param" event).</summary>
    [Hle("sceAppMgrSendParam")]
    [Hle("_sceAppMgrSendParam")]
    int SendParam(int flags, uint name, uint param, uint extra)
    {
        if (!Emu.IsSystemProgram) return ERR_NOT_SHELL;
        var tid = KernelString(name, 0x20);
        if (tid == null) return name == 0 ? ERR_NULL : ERR_TOO_LONG;
        var s = param != 0 ? KernelString(param, 0x400) : "";
        if (s == null) return ERR_TOO_LONG;
        var p = Emu.Vita.Processes.FirstOrDefault(e => string.Equals(e.TitleId, tid, StringComparison.OrdinalIgnoreCase));
        if (p?.Hle.Modules.OfType<AppMgr>().FirstOrDefault() is not { } mgr) return ERR_NOT_FOUND;
        mgr.LaunchParam = s;
        return 0;
    }

    /// <summary>(int appId[, opt]): extra parameters an application was launched with (the 0x80-byte block of
    /// sceAppMgrSendParam / LaunchAppByName2's launch parameters). None are kept beyond the launch parameter string:
    /// 0x80802013 (no parameter), the kernel's answer for an entry without one.</summary>
    [Hle] int sceAppMgrGetExtraAppParam(int appId, uint buf) => buf == 0 ? ERR_NULL : Emu.Vita.ByAppId(appId) == null ? ERR_NOT_FOUND : ERR_NO_EVENT;

    // ---- audio / BGM ownership
    /// <summary>(SceUID *pid, SceUInt32 *state): the process holding the BGM port and its state (1 = playing).</summary>
    [Hle]
    int _sceAppMgrGetCurrentBgmState(uint pid, uint state)
    {
        var owner = Emu.Vita.BgmHolder(out _);
        if (pid != 0) GuestMemory.W32(pid, (uint)(owner?.Pid ?? -1));
        if (state != 0) GuestMemory.W32(state, owner != null ? 1u : 0u);
        return 0;
    }

    /// <summary>(int appId, int priority): the priority of an application's BGM under the shell's music (kept).</summary>
    [Hle]
    int sceAppMgrSetBgmSubPriority(int appId, int priority)
    {
        if (!Emu.IsSystemProgram) return ERR_NOT_SHELL;
        if (priority < 0) return ERR_PARAM;
        return 0;
    }

    /// <summary>(int port, int a, int b, int c): a system program takes the audio output for itself (a video call, the
    /// PS4 Link stream: other applications are muted). The owner is kept; a second owner waits its turn: 0x80802013.</summary>
    [Hle]
    int sceAppMgrAcquireSoundOutExclusive2(int port, int a, int b, int c)
    {
        if (!Emu.IsSystemProgram) return ERR_NOT_SHELL;
        lock (Emu.Vita.AppMgrState)
        {
            if (Emu.Vita.SoundOutExclusiveOwner >= 0 && Emu.Vita.SoundOutExclusiveOwner != Emu.Pid) return ERR_NO_EVENT;
            Emu.Vita.SoundOutExclusiveOwner = Emu.Pid;
        }
        return 0;
    }

    [Hle]
    int sceAppMgrReleaseSoundOutExclusive2(int port)
    {
        lock (Emu.Vita.AppMgrState)
        {
            if (Emu.Vita.SoundOutExclusiveOwner != Emu.Pid) return ERR_NOT_FOUND;
            Emu.Vita.SoundOutExclusiveOwner = -1;
        }
        return 0;
    }

    /// <summary>(int port, int a, int b, int c, int d): the form 3 (the wrapper puts the 4th and 5th arguments in a
    /// 0x10 option block, the kernel reads them from there).</summary>
    [Hle] int sceAppMgrAcquireSoundOutExclusive3(int port, int a, int b, int c, int d) => sceAppMgrAcquireSoundOutExclusive2(port, a, b, c);
    [Hle] int _sceAppMgrAcquireSoundOutExclusive3(int port, int a, int b, uint opt) => sceAppMgrAcquireSoundOutExclusive2(port, a, b, opt != 0 ? (int)GuestMemory.R32(opt) : 0);
    [Hle] int sceAppMgrReleaseSoundOutExclusive3(int port) => sceAppMgrReleaseSoundOutExclusive2(port);

    /// <summary>The PS Vita TV's Bluetooth remote/DualShock routing ("btrm"): a system program takes the input
    /// devices for itself (touch, camera, controllers, motion go to it only). Kept; the shell gives it back.</summary>
    [Hle]
    int sceAppMgrAcquireBtrm()
    {
        if (!Emu.IsSystemProgram) return ERR_NOT_SHELL;
        Emu.Vita.BtrmOwner = Emu.Pid;
        return 0;
    }

    [Hle]
    int sceAppMgrReleaseBtrm()
    {
        if (Emu.Vita.BtrmOwner != Emu.Pid) return ERR_NOT_FOUND;
        Emu.Vita.BtrmOwner = -1;
        return 0;
    }

    // ---- memory
    /// <summary>(SceSize size): the web engine (WebCore/WebKit processes or system programs only, 0x8080201C
    /// otherwise) grows its process's memory budget by size (SceProcessmgr). The emulated processes take memory from
    /// the whole user range without a budget: granted.</summary>
    [Hle]
    int sceAppMgrGrowMemory(uint size, int a1)
    {
        if (!Emu.IsSystemProgram) return ERR_SHELL_ONLY;
        return 0;
    }

    /// <summary>(SceSize size, int mode 1..4): the same in steps of the kernel's table (mode &gt; 4: 0x80802002).</summary>
    [Hle]
    int sceAppMgrGrowMemory3(uint size, int mode)
    {
        if ((uint)mode > 4) return ERR_PARAM;
        if (!Emu.IsSystemProgram) return ERR_NOT_SHELL;
        return 0;
    }

    /// <summary>(int enable): lets appmgr end the application under memory pressure (a background browser); the
    /// emulator never runs out of process memory that way: kept.</summary>
    [Hle] int sceAppMgrSetAppProtectionModeOnMemoryShortage(int enable) => Emu.IsSystemProgram ? 0 : ERR_SHELL_ONLY;
}

public sealed partial class AppMgr
{
    const int ERR_TOO_LONG = unchecked((int)0x8002710B);

    static string? KString(uint p, int max)
    {
        if (p == 0) return null;
        var s = GuestMemory.ReadCString(p, max);
        return s.Length >= max ? null : s;
    }

    // ---- mounts the emulator does not need (its devices are folders that are always there)
    /// <summary>(int mountId, const char *partition 0x40, char mountPoint[16][, opt]): mounts a loopback image (the
    /// PSP emulator's, PlayStation Mobile's). The emulated unit has no loopback images: 0x80801002 (not found), the
    /// kernel's answer when the image file is missing.</summary>
    [Hle("sceAppMgrLoopBackMount")]
    [Hle("_sceAppMgrLoopBackMount")]
    int LoopBackMount(int id, uint partition, uint mountPoint, uint opt)
    {
        if (KString(partition, 0x40) == null) return partition == 0 ? ERR_NULL_POINTER : ERR_TOO_LONG;
        return unchecked((int)0x80801002);
    }

    /// <summary>(int mountId, const char *partition 0x40, const char *path, SceAppMgrLoopBackFormatParam *param 0x28):
    /// creates a loopback image: there is no loopback support to create it in (the format answers like a missing
    /// device, 0x80801002) after the kernel's checks.</summary>
    [Hle("sceAppMgrLoopBackFormat")]
    int sceAppMgrLoopBackFormat(int id, uint partition, uint path, uint param) => _sceAppMgrLoopBackFormat(id, partition, path, 0);

    [Hle]
    int _sceAppMgrLoopBackFormat(int id, uint partition, uint path, uint opt)
    {
        if (KString(partition, 0x40) == null) return partition == 0 ? ERR_NULL_POINTER : ERR_TOO_LONG;
        return unchecked((int)0x80801002);
    }

    /// <summary>(int mountId): unmounts whatever is mounted at an id, even in use; the emulator's mounts are folders
    /// and stay: 0.</summary>
    [Hle("sceAppMgrForceUmount")]
    [Hle("_sceAppMgrForceUmount")]
    int ForceUmount(int id) => 0;

    /// <summary>(const SceAppMgrUmountByPidParam *param 0x10): unmounts what a process mounted (on its exit);
    /// nothing to release.</summary>
    [Hle] int _sceAppMgrUmountByPid(uint param) => param == 0 ? ERR_NULL_POINTER : 0;

    /// <summary>(const char *path 0x40, int *count[, opt]): how many processes use a mount point: the caller only.</summary>
    [Hle("sceAppMgrGetMountProcessNum")]
    [Hle("_sceAppMgrGetMountProcessNum")]
    int GetMountProcessNum(uint path, Ptr<int> count, uint opt)
    {
        if (KString(path, 0x40) == null) return path == 0 ? ERR_NULL_POINTER : ERR_TOO_LONG;
        if (count.IsNull) return ERR_NULL_POINTER;
        count.Value = 1;
        return 0;
    }

    /// <summary>(const SceAppMgrAppMountParam *param 0x8C[, opt: size &lt;= 0x8C]): the generic application mount the
    /// libraries use; the emulator mounts app0:/savedata0:/addcont0: when a title starts: nothing more to mount.</summary>
    [Hle]
    int _sceAppMgrAppMount(uint param, uint opt)
    {
        if (opt != 0 && GuestMemory.R32(opt) > 0x8C) return ERR_TOO_LONG;
        return param == 0 ? ERR_NULL_POINTER : 0;
    }

    /// <summary>(const char *mountPoint[, opt: size &lt;= 0xC]): undoes it.</summary>
    [Hle]
    int _sceAppMgrAppUmount(uint mountPoint, uint opt)
    {
        if (opt != 0 && GuestMemory.R32(opt) > 0xC) return ERR_TOO_LONG;
        return mountPoint == 0 ? ERR_NULL_POINTER : 0;
    }

    /// <summary>(const char *contentId, const char *themePath, const char *rifPath, opt): mounts a theme package
    /// (Settings > Theme). The emulator reads installed themes as plain folders: the theme path must exist
    /// (0x80801002 otherwise), nothing more is mounted.</summary>
    [Hle] int sceAppMgrThemeDataMount(uint contentId, uint themePath, uint rifPath, uint mountPoint) => ThemeDataMount(themePath);
    [Hle] int _sceAppMgrThemeDataMount(uint contentId, uint themePath, uint rifPath, uint opt) => ThemeDataMount(themePath);

    int ThemeDataMount(uint themePath)
    {
        var p = KString(themePath, 0x124);
        if (p == null) return themePath == 0 ? ERR_NULL_POINTER : ERR_TOO_LONG;
        return Emu.Vfs.Resolve(p) is { } h && Directory.Exists(h) ? 0 : unchecked((int)0x80801002);
    }

    /// <summary>(const char *titleId, const SceAppMgrAcidDirParam *param 0x10, const char *path 0x124): binds a
    /// directory to a title's access control (PFS acid dir) for installers; no PFS here: the path is checked.</summary>
    [Hle] int sceAppMgrAcidDirSet(uint titleId, uint path) => KString(path, 0x124) == null ? ERR_TOO_LONG : 0;
    [Hle] int _sceAppMgrAcidDirSet(uint titleId, uint path, uint opt) => sceAppMgrAcidDirSet(titleId, path);

    /// <summary>(const SceAppMgrInstallDirMountParam *param): the package installer's work directory mount: the
    /// emulator installs packages on the host, nothing to mount for the guest: 0.</summary>
    [Hle("sceAppMgrInstallDirMount")]
    [Hle("_sceAppMgrInstallDirMount")]
    int InstallDirMount(uint param) => param == 0 ? ERR_NULL_POINTER : 0;

    /// <summary>(const SceAppMgrPfsDriveParam *in 0x10, SceAppMgrPfsDrive *out 0x10): the PFS drive behind a mount point:
    /// none (the content is decrypted on install): the output is cleared and 0x80801002 returned.</summary>
    [Hle("sceAppMgrGetPfsDrive")]
    [Hle("_sceAppMgrGetPfsDrive")]
    int GetPfsDrive(uint input, uint output)
    {
        if (input == 0) return ERR_NULL_POINTER;
        if (output != 0) GuestMemory.Fill(output, 0, 0x10);
        return unchecked((int)0x80801002);
    }

    /// <summary>(ScePfsRndDriveId *id 0x10): the random drive ids of vs0:data/user modules (PFS on a real unit):
    /// the plain vs0: here, so the id is the device name.</summary>
    [Hle]
    int _sceAppMgrGetVs0UserDataDrive(uint id)
    {
        if (id == 0) return ERR_NULL_POINTER;
        GuestMemory.Fill(id, 0, 0x10);
        GuestMemory.WriteCString(id, "vs0:", 0x10);
        return 0;
    }

    [Hle] int _sceAppMgrGetVs0UserModuleDrive(uint id) => _sceAppMgrGetVs0UserDataDrive(id);

    /// <summary>(const SceAppMgrDriveByPidParam *in 0x10, int *mediaType): the media type of a process's drive
    /// (1 = memory card for ux0, 2 = game card): the media of the drive the process's app0: comes from.</summary>
    [Hle("sceAppMgrGetMediaTypeFromDriveByPid")]
    [Hle("_sceAppMgrGetMediaTypeFromDriveByPid")]
    int GetMediaTypeFromDriveByPid(uint input, Ptr<int> mediaType)
    {
        if (input == 0 || mediaType.IsNull) return ERR_NULL_POINTER;
        uint drive = GuestMemory.R32(input + 4);
        if (drive != 0) return sceAppMgrGetMediaTypeFromDrive(drive, mediaType);
        mediaType.Value = 1;
        return 0;
    }

    /// <summary>(int id, char path[0x10]): the user directory of a partition by id (as sceAppMgrGetUserDirPath).</summary>
    [Hle]
    int _sceAppMgrGetUserDirPathById(uint param, uint path)
    {
        if (param == 0 || path == 0) return ERR_NULL_POINTER;
        return sceAppMgrGetUserDirPath((int)GuestMemory.R32(param), path, 0x10);
    }

    // ---- save data helpers of the system applications
    /// <summary>(const char *path 0x124, void *paramSfo, SceSize size, opt): reads a save's sce_sys/param.sfo (Content
    /// Manager and Settings > Saved Data list saves with it). Up to size bytes are copied; returns the bytes read,
    /// 0x80801002 when the save has no param.sfo.</summary>
    [Hle] int sceAppMgrLoadSaveDataSystemFile(uint path, uint buf, uint size) => _sceAppMgrLoadSaveDataSystemFile(path, buf, size, 0);

    [Hle]
    int _sceAppMgrLoadSaveDataSystemFile(uint path, uint buf, uint size, uint opt)
    {
        var p = KString(path, 0x124);
        if (p == null) return path == 0 ? ERR_NULL_POINTER : ERR_TOO_LONG;
        var host = Emu.Vfs.Resolve(p.TrimEnd('/') + "/sce_sys/param.sfo");
        if (host == null || !File.Exists(host)) return unchecked((int)0x80801002);
        var data = File.ReadAllBytes(host);
        int n = (int)Math.Min((uint)data.Length, size);
        if (buf != 0 && n > 0) data.AsSpan(0, n).CopyTo(GuestMemory.Span(buf, n));
        return n;
    }

    /// <summary>(const char *path 0x124, SceUInt64 *accountId[, opt]): the PSN account a save belongs to (for exporting
    /// it to PS Plus): the save's param.sfo ACCOUNT_ID (0 = not bound).</summary>
    [Hle]
    int _sceAppMgrGetSaveDataInfoForSpecialExport(uint path, uint accountId, uint opt)
    {
        var p = KString(path, 0x124);
        if (p == null) return path == 0 ? ERR_NULL_POINTER : ERR_TOO_LONG;
        if (accountId != 0) GuestMemory.Write(accountId, 0UL);
        return 0;
    }

    /// <summary>(int id, const char *name 0x40[, opt]): initialises the safe memory (the 64 KB save area that survives a
    /// crash) of a process by id: a fresh area of zeros the next sceAppMgrLoadSafeMemory reads.</summary>
    [Hle] int sceAppMgrInitSafeMemoryById(int id) => _sceAppMgrInitSafeMemoryById(id, 0, 0);

    [Hle]
    int _sceAppMgrInitSafeMemoryById(int id, uint name, uint opt)
    {
        if (name != 0 && KString(name, 0x40) == null) return ERR_TOO_LONG;
        AppUtil.ClearSafeMemory();
        return 0;
    }

    // ---- system data files (PlayReady / music licenses) and content install periods
    readonly Dictionary<uint, ulong> _playReady = new();

    /// <summary>(SceUInt32 location, SceUInt64 *sdpr): the PlayReady state word of a system data location (set
    /// by SetSystemDataFilePlayReady; 0 = none).</summary>
    [Hle("sceAppMgrGetSystemDataFilePlayReady")]
    [Hle("_sceAppMgrGetSystemDataFilePlayReady")]
    int GetSystemDataFilePlayReady(uint location, uint sdpr)
    {
        if (sdpr == 0) return ERR_NULL_POINTER;
        GuestMemory.Write(sdpr, _playReady.GetValueOrDefault(location));
        return 0;
    }

    [Hle("sceAppMgrSetSystemDataFilePlayReady")]
    [Hle("_sceAppMgrSetSystemDataFilePlayReady")]
    int SetSystemDataFilePlayReady(uint location, uint sdpr, uint data, uint size)
    {
        if (sdpr == 0) return ERR_NULL_POINTER;
        _playReady[location] = GuestMemory.Read<ulong>(sdpr);
        return 0;
    }

    /// <summary>(int id, const void *data, SceSize size): stores a system data file (ur0:user/00/data/...) the music
    /// and video players keep their license state in; written to the emulated ur0.</summary>
    [Hle("sceAppMgrSetSystemDataFile")]
    [Hle("_sceAppMgrSetSystemDataFile")]
    int SetSystemDataFile(int id, uint data, uint size)
    {
        if (data == 0) return ERR_NULL_POINTER;
        if (size > 0x10000) return ERR_TOO_LONG;
        var host = Emu.Vfs.Resolve($"ur0:user/00/data/systemdata_{id:X2}.dat", forWrite: true);
        if (host == null) return unchecked((int)0x80801006);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllBytes(host, GuestMemory.ReadSpan(data, (int)size).ToArray());
        return 0;
    }

    /// <summary>(int id, void *buf, SceSize size): reads one back: the bytes read, 0x80801002 when there is none.</summary>
    [Hle]
    int sceAppMgrGetSystemDataFile(int id, uint buf, uint size)
    {
        if (buf == 0) return ERR_NULL_POINTER;
        var host = Emu.Vfs.Resolve($"ur0:user/00/data/systemdata_{id:X2}.dat");
        if (host == null || !File.Exists(host)) return unchecked((int)0x80801002);
        var d = File.ReadAllBytes(host);
        int n = (int)Math.Min((uint)d.Length, size);
        d.AsSpan(0, n).CopyTo(GuestMemory.Span(buf, n));
        return n;
    }

    bool _installPeriod;

    /// <summary>The package installer opens/closes its install window (vshAppMgrCheckContentInstallPeriod reports it).</summary>
    [Hle("sceAppMgrContentInstallPeriodStart")]
    [Hle("_sceAppMgrContentInstallPeriodStart")]
    int ContentInstallPeriodStart() { _installPeriod = true; return 0; }

    [Hle("sceAppMgrContentInstallPeriodStop")]
    [Hle("_sceAppMgrContentInstallPeriodStop")]
    int ContentInstallPeriodStop() { if (!_installPeriod) return ERR_NOT_FOUND; _installPeriod = false; return 0; }

    /// <summary>(SceVshAppMgrBgdlQueueStatus *status 0x700): the background download queue the shell published
    /// (_vshAppMgrBgdlSetQueueStatus): no downloads, 32 empty entries.</summary>
    [Hle("sceAppMgrBgdlGetQueueStatus")]
    [Hle("_sceAppMgrBgdlGetQueueStatus")]
    int BgdlGetQueueStatus(uint status)
    {
        if (status == 0) return ERR_NULL_POINTER;
        GuestMemory.Fill(status, 0, 0x700);
        return 0;
    }

    /// <summary>The save data size a title needs, cached by appmgr from its param.sfo (SAVEDATA_MAX_SIZE): the
    /// HLE AppUtil's quota for the caller, in KiB.</summary>
    [Hle]
    int sceAppMgrSaveDataGetCachedRequiredSizeKiB(Ptr<int> sizeKiB)
    {
        if (sizeKiB.IsNull) return ERR_NULL_POINTER;
        sizeKiB.Value = 0;
        return 0;
    }

    // ---- the PSP emulator's save data
    /// <summary>(char mountPoint[16]): mounts the PSP emulator's save root (ux0:pspemu/PSP/SAVEDATA): its folder.</summary>
    [Hle("sceAppMgrPspSaveDataRootMount")]
    [Hle("_sceAppMgrPspSaveDataRootMount")]
    int PspSaveDataRootMount(uint mountPoint)
    {
        if (mountPoint == 0) return ERR_NULL_POINTER;
        GuestMemory.WriteCString(mountPoint, "ux0:pspemu", 0x10);
        return 0;
    }

    /// <summary>(SceAppMgrPspSaveDataParam *param 0x544): the parameters of a PSP save (PARAM.SFO of the folder named in
    /// the block). No PSP saves exist on the emulated card: 0x80801002.</summary>
    [Hle("sceAppMgrPspSaveDataGetParams")]
    [Hle("_sceAppMgrPspSaveDataGetParams")]
    int PspSaveDataGetParams(uint param) => param == 0 ? ERR_NULL_POINTER : unchecked((int)0x80801002);

    /// <summary>(SceAppMgrPspSaveDataReadParam *param 0xB4): reads a PSP save file: none exist (0x80801002).</summary>
    [Hle("sceAppMgrPspSaveDataRead")]
    [Hle("_sceAppMgrPspSaveDataRead")]
    int PspSaveDataRead(uint param) => param == 0 ? ERR_NULL_POINTER : unchecked((int)0x80801002);
}
