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

    const int ERR_NOT_SHELL = unchecked((int)0x8080201F);

    /// <summary>(SceAppMgrAppMgrState *state): the kernel's 0x80-byte state block (FW 3.74 appmgr 0x81008CDC copies it
    /// under its lock; only the shell may ask). libpaf reads +8 and treats a negative value as "not set".</summary>
    [Hle]
    [Hle("_sceAppMgrGetAppMgrState")]
    int sceAppMgrGetAppMgrState(uint state)
    {
        if (state == 0) return ERR_NULL;
        lock (Emu.Vita.AppMgrState) Emu.Vita.AppMgrState.CopyTo(GuestMemory.Span(state, 0x80));
        return 0;
    }

    void SetAppMgrState(int offset, int value)
    {
        lock (Emu.Vita.AppMgrState) BitConverter.TryWriteBytes(Emu.Vita.AppMgrState.AsSpan(offset), value);
    }

    /// <summary>(int enable): the registry's chat-priority setting the shell passes on; kept in the state block (+0).</summary>
    [Hle] int sceAppMgrEnablePrioritizingSystemChat(int enable) { SetAppMgrState(0, enable); return 0; }

    /// <summary>(int state): kept in the state block (+0x14) for libpaf's next sceAppMgrGetAppMgrState.</summary>
    [Hle] int sceAppMgrSetSystemImposeState2(int state) { SetAppMgrState(0x14, state); return 0; }

    /// <summary>(int appId): the application id an id stands for. Ids >= 0 are themselves while that application
    /// exists; the kernel's pseudo ids (appmgr 0x81002BD8): -2 the caller's own application, -4 the application in the
    /// foreground (entry state 2). The shell's music player asks for -4 and -2. -1 when there is none; only the shell may
    /// ask (0x8080201F).</summary>
    [Hle]
    int sceAppMgrGetAppIdByAppId(int appId)
    {
        if (!Emu.IsShell) return ERR_NOT_SHELL;
        Emulator? p = appId switch
        {
            -2 => Emu,
            -4 => Emu.Vita.Foreground,
            >= 0 => Emu.Vita.ByAppId(appId),
            _ => null,
        };
        return p?.AppId ?? -1;
    }

    /// <summary>(SceAppMgrBgmState *state, 0x20 bytes; FW 3.74 appmgr 0x81007844): +0 the application holding the BGM
    /// port, +4/+8 its port settings, +0xC that application's process (both -1 when no application holds it).</summary>
    [Hle]
    [Hle("_sceAppMgrGetCurrentBgmState2")]
    int sceAppMgrGetCurrentBgmState2(uint state)
    {
        if (state == 0) return ERR_NULL;
        GuestMemory.Fill(state, 0, 0x20);
        var owner = Emu.Vita.BgmPortOwner >= 0 ? Emu.Vita.ByAppId(Emu.Vita.BgmPortOwner) : null;
        GuestMemory.W32(state, (uint)(owner?.AppId ?? -1));
        GuestMemory.W32(state + 0xC, (uint)(owner?.Pid ?? -1));
        return 0;
    }

    /// <summary>(int priority, ...): the shell's music player takes the BGM port; 0 = granted (nothing else plays BGM).</summary>
    [Hle] int sceAppMgrAcquireBgmPortWithPriority(int priority) { Emu.Vita.BgmPortOwner = Emu.AppId; return 0; }

    /// <summary>The shell restores its own BGM settings after an application gave the port back.</summary>
    [Hle] int sceAppMgrRestoreBgmSettingForShell() => 0;

    /// <summary>(int enable, int keepScreen): the shell's power-save (dimming) mode; the emulated unit has no panel to dim.</summary>
    [Hle] int sceAppMgrSetPowerSaveMode(int enable, int keepScreen) => 0;

    /// <summary>(const char *name): ends an application by title id; NULL ends the calling application. The first-boot
    /// setup (initialsetup.self, NPXS10062) ends itself this way once the user finishes it: the shell then gets its
    /// application-ended event and takes the screen back. Without it the setup process stayed in front, no longer
    /// drawing, and the screen stayed black.</summary>
    [Hle]
    int sceAppMgrDestroyAppByName(uint name)
    {
        Emulator? p = Emu;
        if (name != 0)
        {
            string tid = GuestMemory.ReadCString(name, 0x20);
            p = Emu.Vita.Processes.FirstOrDefault(e => string.Equals(e.TitleId, tid, StringComparison.OrdinalIgnoreCase));
        }
        if (p == null) return ERR_NAME;
        p.Terminate(p == Emu ? "ended itself (sceAppMgrDestroyAppByName)" : $"ended by {Emu.Describe()}");
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

    /// <summary>(int flags, const char *name, const char *param): starts an application by title id in the background
    /// (the shell starts NPXS10079, the background download service, with flags 0x20000 and "RemoteDownload"). It runs
    /// as its own process without taking the screen. Returns its application id.</summary>
    [Hle]
    int sceAppMgrLaunchAppByName(int flags, uint name, uint param)
    {
        if (name == 0) return ERR_NULL;
        string tid = GuestMemory.ReadCString(name, 0x20);
        var running = Emu.Vita.Processes.FirstOrDefault(p => string.Equals(p.TitleId, tid, StringComparison.OrdinalIgnoreCase));
        if (running != null) return running.AppId;
        string? dir = null;
        foreach (var root in new[] { "vs0:app/", "ux0:app/" })
            if (Emu.Vfs.Resolve(root + tid) is { } d && File.Exists(Path.Combine(d, "eboot.bin"))) { dir = d; break; }
        if (dir == null) return unchecked((int)0x80800006);
        var app = Emu.SpawnProcess(dir, "eboot.bin", [], background: true);
        // the parameter reaches the application as its launch parameter (sceAppMgrGetAppParam)
        if (param != 0) app.Hle.Modules.OfType<AppMgr>().First().LaunchParam = GuestMemory.ReadCString(param, 0x400);
        Log.Info($"sceAppMgrLaunchAppByName(0x{flags:X}, {tid}): background process {app.Describe()}");
        return app.AppId;
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

    /// <summary>(int appId, SceDisplayFrameBuf *dst): copies what the application shows (its last framebuffer) into the
    /// caller's buffer, scaled to its size (IFTU, the display's scaler) or 1:1 (DMAC). The shell captures the application
    /// it suspends this way for the gate of its LiveArea (on failure it clears the thumbnail: black). Application ids as
    /// in GetAppIdByAppId (-2 the caller, -4 the application in front).</summary>
    [Hle]
    [Hle("_sceAppMgrCaptureFrameBufIFTUByAppId")]
    int sceAppMgrCaptureFrameBufIFTUByAppId(int appId, uint dst) => Capture(appId, dst, scale: true);

    [Hle]
    [Hle("_sceAppMgrCaptureFrameBufDMACByAppId")]
    int sceAppMgrCaptureFrameBufDMACByAppId(int appId, uint dst) => Capture(appId, dst, scale: false);

    int Capture(int appId, uint dst, bool scale)
    {
        if (dst == 0) return ERR_NULL;
        Emulator? p = appId switch { -2 => Emu, < 0 => Emu.Vita.Foreground, _ => Emu.Vita.ByAppId(appId) };
        if (p == null) return ERR_NOT_FOUND;
        if (GuestMemory.R32(dst) is not (0x18 or 0x1C)) return ERR_NULL;
        uint dBase = GuestMemory.R32(dst + 4), dPitch = GuestMemory.R32(dst + 8), dFmt = GuestMemory.R32(dst + 12);
        uint dW = GuestMemory.R32(dst + 16), dH = GuestMemory.R32(dst + 20);
        if (dBase == 0 || dW == 0 || dH == 0 || dPitch < dW) return ERR_NULL;
        if (p.Hle.Modules.OfType<Display>().FirstOrDefault()?.Current is not { } src) return ERR_NOT_FOUND;
        uint w = scale ? dW : Math.Min(dW, src.Width), h = scale ? dH : Math.Min(dH, src.Height);
        // the framebuffer is in the application's address space (the GPU writes a rendered surface back there before the
        // callback), the thumbnail in the caller's
        var callerSpace = Emu.Space;
        long seq = Emu.Gpu.Submit(new Gpu.CallbackCommand
        {
            Space = p.Space,
            ReadAddress = src.Address, ReadSize = src.Pitch * src.Height * 4,
            WriteAddress = dBase, WriteSize = dPitch * dH * 4,
            Action = () =>
            {
                for (uint y = 0; y < h; y++)
                {
                    uint sy = scale ? y * src.Height / dH : y;
                    for (uint x = 0; x < w; x++)
                    {
                        uint sx = scale ? x * src.Width / dW : x;
                        uint v = GuestMemory.R32(src.Address + (sy * src.Pitch + sx) * 4);
                        if (src.Format == Gpu.DisplayPixelFormat.A2B10G10R10) // 10-bit channels to 8 bits
                            v = ((v >> 2) & 0xFF) | (((v >> 12) & 0xFF) << 8) | (((v >> 22) & 0xFF) << 16) | 0xFF000000;
                        if (dFmt == (uint)Gpu.DisplayPixelFormat.A2B10G10R10)
                            v = ((v & 0xFF) << 2) | (((v >> 8) & 0xFF) << 12) | (((v >> 16) & 0xFF) << 22) | 0xC0000000;
                        using (AddressSpace.Use(callerSpace)) GuestMemory.W32(dBase + (y * dPitch + x) * 4, v);
                    }
                }
            },
        });
        Emu.Gpu.WaitFor(seq);
        return 0;
    }
}
