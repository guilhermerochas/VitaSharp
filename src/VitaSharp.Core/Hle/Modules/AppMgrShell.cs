using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The application manager services the shell uses to run applications (SceAppMgr kernel, FW 3.74): the application
/// records (one per process, its UID the application id), their status, bringing one to the front, ending it, and the
/// queue of events SceAppMgr sends the shell. Layouts are read from the firmware's own appmgr (embedded in
/// os0:kd/bootimage.skprx): SceAppMgrAppStatus is 0x80 bytes filled from the kernel's application entry, a shell
/// event is 0x40 bytes with its type first.
/// </summary>
public sealed class AppMgrShell : HleModule
{
    const int ERR_NOT_FOUND = unchecked((int)0x80802012), ERR_NULL = unchecked((int)0x80802016),
        ERR_NO_EVENT = unchecked((int)0x80802013), ERR_NAME = unchecked((int)0x8080201C);

    /// <summary>
    /// SceAppMgrAppStatus (0x80) as the kernel fills it (FW 3.74 appmgr, the function shared by GetStatusByAppId and
    /// GetStatusByName): +0x0C the application name (title id, 0x20), +0x30 application id, +0x34 process id, +0x38
    /// "in the foreground" (the entry's state is 2), +0x3C running, +0x44 the application id again (the shell stores this
    /// one and compares it with later statuses), +0x50 the title id again, +0x71 the application
    /// type byte and +0x74 its flags. libipmi only lets a process create an IPC server when +0x74 bit 0 is set or
    /// the type is 2: the shell's.
    /// </summary>
    internal static void WriteStatus(Emulator p, uint st)
    {
        GuestMemory.Fill(st, 0, 0x80);
        GuestMemory.WriteCString(st + 0x0C, p.TitleId, 0x20);
        GuestMemory.W32(st + 0x30, (uint)p.AppId);
        GuestMemory.W32(st + 0x34, (uint)p.Pid);
        GuestMemory.W32(st + 0x38, p.IsForeground ? 1u : 0u);
        GuestMemory.W32(st + 0x3C, p.Running ? 1u : 0u);
        GuestMemory.W32(st + 0x44, (uint)p.AppId); // the id the shell keeps for the running application (and compares)
        GuestMemory.WriteCString(st + 0x50, p.TitleId, 0x20);
        if (p.IsShell)
        {
            GuestMemory.Write(st + 0x71, (byte)2);
            GuestMemory.W32(st + 0x74, 1);
        }
    }

    int Status(Emulator? p, uint st)
    {
        if (st == 0) return ERR_NULL;
        if (p == null) return ERR_NOT_FOUND;
        WriteStatus(p, st);
        return 0;
    }

    /// <summary>(const char *name, SceAppMgrAppStatus *status): name is a title id, NULL for the calling process.</summary>
    [Hle]
    [Hle("_sceAppMgrGetStatusByName")]
    int sceAppMgrGetStatusByName(uint name, uint status)
    {
        if (name == 0) return Status(Emu, status);
        string tid = GuestMemory.ReadCString(name, 0x20);
        var p = Emu.Vita.Processes.FirstOrDefault(e => string.Equals(e.TitleId, tid, StringComparison.OrdinalIgnoreCase));
        return p == null && status != 0 ? ERR_NAME : Status(p, status);
    }

    /// <summary>(SceUID appId, SceAppMgrAppStatus *status).</summary>
    [Hle]
    [Hle("_sceAppMgrGetStatusByAppId")]
    int sceAppMgrGetStatusByAppId(int appId, uint status) => Status(Emu.Vita.ByAppId(appId), status);

    /// <summary>(SceUID pid, SceAppMgrAppStatus *status).</summary>
    [Hle]
    [Hle("_sceAppMgrGetStatusById")]
    int sceAppMgrGetStatusById(int pid, uint status) => Status(Emu.Vita.ByPid(pid), status);

    /// <summary>The process of an application, by its application id.</summary>
    [Hle]
    int sceAppMgrGetProcessIdByAppIdForShell(int appId) => Emu.Vita.ByAppId(appId)?.Pid ?? ERR_NOT_FOUND;

    /// <summary>(const char *name): the shell announces itself ("NPXS19999"); SceAppMgr's shell events go to it.</summary>
    [Hle]
    int sceAppMgrDeclareShellProcess2(uint name)
    {
        Emu.IsShell = true;
        Log.Info($"{Emu.Describe()} is the shell ({GuestMemory.ReadCString(name, 0x20)})");
        return 0;
    }

    /// <summary>(SceUID appId): the shell brings an application to the front (after launching it, or from its LiveArea).</summary>
    [Hle]
    int sceAppMgrActivateApp(int appId)
    {
        if (Emu.Vita.ByAppId(appId) is not { } p) return ERR_NOT_FOUND;
        p.Resume();
        Emu.Vita.SetForeground(p);
        return 0;
    }

    /// <summary>(SceUID appId): the shell sends an application to the background (after the PS button: its LiveArea
    /// comes up): the application is suspended and the shell takes the screen and the input.</summary>
    [Hle]
    int sceAppMgrDeactivateApp(int appId)
    {
        if (Emu.Vita.ByAppId(appId) is not { } p) return ERR_NOT_FOUND;
        if (p != Emu)
        {
            p.Suspend();
            if (p.IsForeground) Emu.Vita.SetForeground(Emu);
        }
        return 0;
    }

    /// <summary>(SceUID appId): ends an application (the shell closing it from its LiveArea).</summary>
    [Hle]
    int sceAppMgrDestroyAppByAppId(int appId)
    {
        if (Emu.Vita.ByAppId(appId) is not { } p) return ERR_NOT_FOUND;
        p.Terminate("closed by the shell");
        return 0;
    }

    /// <summary>(SceUID appId, char path[0x100]): where an application's app0: is mounted from ("ux0:app/TITLEID").</summary>
    [Hle]
    int sceAppMgrGetRawPathOfApp0ByAppIdForShell(int appId, uint path)
    {
        if (path == 0) return ERR_NULL;
        if (Emu.Vita.ByAppId(appId) is not { AppPath.Length: > 0 } p) return ERR_NOT_FOUND;
        GuestMemory.WriteCString(path, GuestPath(p.AppPath), 0x100);
        return 0;
    }

    /// <summary>The guest path ("ux0:app/X") of a host folder inside the emulated filesystem.</summary>
    string GuestPath(string host)
    {
        string root = Path.GetFullPath(Emu.Options.FsRoot), full = Path.GetFullPath(host);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "ux0:app/" + Path.GetFileName(full);
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "" : parts[0] + ":" + string.Join('/', parts.Skip(1));
    }

    /// <summary>(void *request): requests from applications for the shell to show a notification. The shell polls it
    /// every frame and handles a request whenever the call succeeds, so with none queued it must fail.</summary>
    [Hle] int sceAppMgrReceiveNotificationRequestForShell(uint request) => ERR_NO_EVENT;

    // display composition settings the shell gives for itself: VitaSharp shows the foreground process whole
    [Hle] int sceAppMgrSetDisplayMergeConf(int a, int b) => 0;
    [Hle] int sceAppMgrRestoreDisplaySettingForShell() => 0;
    [Hle] int sceAppMgrSetShellScreenOrientation(int a, int b) => 0;

    /// <summary>Number of queued shell events (returned directly).</summary>
    [Hle] int sceAppMgrReceiveShellEventNum() => Emu.IsShell ? Emu.Vita.ShellEventCount : 0;

    /// <summary>(SceAppMgrShellEvent *event, 0x40 bytes): takes the oldest shell event.</summary>
    [Hle]
    [Hle("_sceAppMgrReceiveShellEvent")]
    int sceAppMgrReceiveShellEvent(uint ev)
    {
        if (ev == 0) return ERR_NULL;
        if (!Emu.IsShell || Emu.Vita.TakeShellEvent() is not { } e) return ERR_NO_EVENT;
        e.CopyTo(GuestMemory.Span(ev, e.Length));
        return 0;
    }
}
