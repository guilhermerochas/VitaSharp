using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSharedFb: the framebuffer of the system software (the kernel side lives in SceAppMgr; FW 3.74 bootimage).
/// The shell's libpaf creates it (sceSharedFbCreate(1)) and maps it as its own display buffers; the libpaf of every
/// other system program (Trophy Collection, Settings, ...) opens it (_sceSharedFbOpen(1)) and becomes one of its 16
/// client processes. A client never shows a frame itself: each shell frame, sceSharedFbUpdateProcessBegin hands the
/// kernel the shell's render target (the buffer it is about to draw, its size and its sync object) and gets the
/// number of clients that render this frame; for each, sceSharedFbUpdateProcess wakes the client (event flag bit of
/// its slot), whose sceSharedFbBegin returns with that render target, and waits for its sceSharedFbEnd. The shell then
/// draws its own layers (status bar, notifications) over the client's picture in the same buffer and displays it. So
/// the client's frame and the shell's overlay share one buffer, drawn one after the other.
/// <para>A system program run without the shell opens a buffer of its own (no compositor): End shows it directly.</para>
/// Layout of SceSharedFbInfo (0x58): +0 base1 (start of the memory), +4 memory size, +8 the buffer to draw (the shell's
/// render target; libpaf draws there), +0x24 stride, +0x28 width, +0x2C height, +0x30 colour format, +0x34 the
/// shell's sync object, +0x48 vsync (Vita3K's SceSharedFb.cpp names +8 base2 and +0x34 curbuf).
/// </summary>
public sealed class SharedFb : HleModule
{
    /// <summary>Kernel errors (FW 3.74 appmgr): unknown framebuffer, missing argument, the client is not rendering this
    /// frame (Begin) or the client did not finish its frame (UpdateProcess), not a system program.</summary>
    const int ERR_NOT_FOUND = unchecked((int)0x80200004), ERR_ARGUMENT = unchecked((int)0x80200007),
        ERR_NOT_RENDERING = unchecked((int)0x80200008), ERR_INVALID = unchecked((int)0x80020003);
    const uint InfoSize = 0x58, Width = 960, Height = 544, Pitch = 960, ColorFormatAbgr8888 = 0x80001000;
    const uint BufferSize = Pitch * Height * 4;
    /// <summary>How long a client's Begin waits for its turn before it checks that it still renders (kernel: 160 ms),
    /// and how long the shell waits for a client's frame (kernel: 20 s).</summary>
    const int ClientWaitMs = 160, ShellWaitMs = 20_000, MaxClients = 16;

    uint _frameBufDesc;

    Registry Fbs => Emu.Vita.SharedFbs;

    /// <summary>Creates framebuffer <paramref name="index"/> (or returns the one already there).</summary>
    SharedFbObject Create(int index, bool compositor)
    {
        lock (Fbs)
        {
            if (Fbs.ByIndex(index) is { } fb && Emu.Vita.ByPid(fb.CreatorPid) != null) return fb;
            uint mem = Emu.Memory.Alloc(BufferSize * 2, $"SceSharedFb{index}", MemoryRegion.Cdram, 0x40000);
            GuestMemory.Fill(mem, 0, BufferSize * 2);
            fb = new SharedFbObject
            {
                Index = index, Uid = 0x00F00001 + index, Base1 = mem, Base2 = mem + BufferSize, CreatorPid = Emu.Pid,
                Compositor = compositor,
            };
            Fbs.Set(fb);
            Log.Info($"SceSharedFb {index}: 2 x {Width}x{Height} buffers at 0x{mem:X8} ({(compositor ? "the shell composites" : "no shell: shown directly")})");
            return fb;
        }
    }

    SharedFbObject? ById(int uid) { lock (Fbs) return Fbs.ByUid(uid); }

    static void WriteInfo(SharedFbObject fb, uint info)
    {
        GuestMemory.Fill(info, 0, InfoSize);
        GuestMemory.W32(info + 0x00, fb.Base1);
        GuestMemory.W32(info + 0x04, BufferSize * 2);
        GuestMemory.W32(info + 0x08, fb.Base2);
        GuestMemory.W32(info + 0x24, Pitch);
        GuestMemory.W32(info + 0x28, Width);
        GuestMemory.W32(info + 0x2C, Height);
        GuestMemory.W32(info + 0x30, ColorFormatAbgr8888);
        GuestMemory.W32(info + 0x48, 1); // vsync
    }

    /// <summary>(int index, param): the shell's libpaf creates framebuffer 1 and composites it. Returns its UID.</summary>
    [Hle] int sceSharedFbCreate(int index) => Create(index, compositor: true).Uid;

    /// <summary>(int index, int sysver): a system program's libpaf opens the shell's framebuffer and becomes one of its
    /// clients. Without a shell process it gets a framebuffer of its own.</summary>
    [Hle]
    int _sceSharedFbOpen(int index, uint sysver)
    {
        var fb = Create(index, compositor: false);
        lock (fb)
        {
            if (fb.CreatorPid != Emu.Pid && !fb.Clients.Contains(Emu.Pid))
            {
                fb.Clients.RemoveAll(pid => Emu.Vita.ByPid(pid) == null);
                if (fb.Clients.Count >= MaxClients) return ERR_INVALID;
                fb.Clients.Add(Emu.Pid);
                Log.Info($"SceSharedFb {index}: {Emu.Describe()} renders into the shell's framebuffer");
            }
        }
        return fb.Uid;
    }

    [Hle]
    int sceSharedFbClose(int uid)
    {
        if (ById(uid) is not { } fb) return ERR_NOT_FOUND;
        lock (fb)
        {
            fb.Clients.Remove(Emu.Pid);
            Monitor.PulseAll(fb);
        }
        return 0;
    }

    [Hle]
    int sceSharedFbDelete(int uid)
    {
        if (ById(uid) is not { } fb) return ERR_NOT_FOUND;
        lock (Fbs) if (fb.CreatorPid == Emu.Pid) Fbs.Remove(fb);
        return 0;
    }

    [Hle]
    int sceSharedFbGetInfo(int uid, uint info)
    {
        if (ById(uid) is not { } fb) return ERR_NOT_FOUND;
        if (info == 0) return ERR_ARGUMENT;
        WriteInfo(fb, info);
        return 0;
    }

    bool IsClient(SharedFbObject fb) => fb.Compositor && fb.CreatorPid != Emu.Pid;

    /// <summary>Takes the buffer for drawing. A client of the shell waits for its turn (the shell's UpdateProcess) and
    /// gets the shell's render target; it gives up (0x80200008) when the shell does not render it this frame (it is not
    /// in front), as the kernel does after each 160 ms wait. Without the shell, libpaf draws into base2.</summary>
    [Hle]
    int sceSharedFbBegin(int uid, uint info)
    {
        if (ById(uid) is not { } fb) return ERR_NOT_FOUND;
        if (info == 0) return ERR_ARGUMENT;
        if (!IsClient(fb)) { WriteInfo(fb, info); return 0; }
        int pid = Emu.Pid;
        uint[] target;
        using (Scheduler.Block()) // waiting for the shell: not CPU time
        lock (fb)
        {
            while (fb.Granted != pid)
                if ((!Monitor.Wait(fb, ClientWaitMs) && fb.Granted != pid && !fb.Active.Contains(pid)) || !Emu.Running)
                    return ERR_NOT_RENDERING;
            target = fb.ShellTarget;
        }
        WriteInfo(fb, info);
        for (int i = 0; i < target.Length; i++) GuestMemory.W32(info + 0x08 + (uint)i * 4, target[i]); // the shell's render target
        return 0;
    }

    /// <summary>The frame is complete. A client hands it back to the shell, which draws its layers over it and shows
    /// it. Without the shell it goes on screen (after the GPU work queued before it): the buffer libpaf draws.</summary>
    [Hle]
    int sceSharedFbEnd(int uid)
    {
        if (ById(uid) is not { } fb) return ERR_NOT_FOUND;
        if (IsClient(fb))
        {
            lock (fb)
            {
                fb.Rendered.Add(Emu.Pid);
                if (fb.Granted == Emu.Pid) { fb.Granted = 0; fb.Done = true; Monitor.PulseAll(fb); }
            }
            return 0;
        }
        if (_frameBufDesc == 0) _frameBufDesc = Emu.Memory.Alloc(0x20, "SceSharedFb display desc");
        GuestMemory.W32(_frameBufDesc + 0x00, 0x18);
        GuestMemory.W32(_frameBufDesc + 0x04, fb.Base2);
        GuestMemory.W32(_frameBufDesc + 0x08, Pitch);
        GuestMemory.W32(_frameBufDesc + 0x0C, 0); // SCE_DISPLAY_PIXELFORMAT_A8B8G8R8
        GuestMemory.W32(_frameBufDesc + 0x10, Width);
        GuestMemory.W32(_frameBufDesc + 0x14, Height);
        Emu.Hle.Modules.OfType<Display>().First().SetFrameBuf(_frameBufDesc, 1);
        return 0;
    }

    /// <summary>The clients the shell renders this frame: the application in front, when it draws into this framebuffer
    /// and runs.</summary>
    List<int> ActiveClients(SharedFbObject fb)
    {
        var fg = Emu.Vita.Foreground;
        return fg != null && !fg.Suspended && fb.Clients.Contains(fg.Pid) ? [fg.Pid] : [];
    }

    /// <summary>(uid, const info[0x40], int *count): the shell starts a frame. info is its render target (+0 the buffer,
    /// +0x1C stride, +0x20/+0x24 size, +0x28 colour format, +0x2C its sync object), which the clients draw into; count
    /// receives the number of clients that render this frame (one UpdateProcess each).</summary>
    [Hle]
    int sceSharedFbUpdateProcessBegin(int uid, uint info, Ptr<int> count)
    {
        if (ById(uid) is not { } fb) return ERR_NOT_FOUND;
        if (count.Address == 0 || info == 0) return ERR_ARGUMENT;
        lock (fb)
        {
            fb.ShellTarget = Enumerable.Range(0, 0x10).Select(i => GuestMemory.R32(info + (uint)i * 4)).ToArray();
            fb.Active = ActiveClients(fb);
            fb.NextUpdate = 0;
            Fbs.Updating = fb;
            Monitor.PulseAll(fb); // a client no longer rendered stops waiting
            count.Value = fb.Active.Count;
        }
        return 0;
    }

    /// <summary>Lets the next client of this frame draw (its Begin returns) and waits until it hands the frame back.</summary>
    [Hle]
    int sceSharedFbUpdateProcess()
    {
        if (Fbs.Updating is not { } fb || fb.CreatorPid != Emu.Pid) return ERR_NOT_FOUND;
        using (Scheduler.Block()) // waiting for the client's frame: not CPU time
        lock (fb)
        {
            if (fb.NextUpdate >= fb.Active.Count) return ERR_NOT_FOUND;
            int pid = fb.Active[fb.NextUpdate++];
            fb.Granted = pid;
            fb.Done = false;
            Monitor.PulseAll(fb);
            long until = Environment.TickCount64 + ShellWaitMs;
            while (!fb.Done)
            {
                var p = Emu.Vita.ByPid(pid);
                if (p == null || p.Suspended || !fb.Clients.Contains(pid) || Environment.TickCount64 >= until || !Emu.Running)
                {
                    fb.Granted = 0;
                    return ERR_NOT_RENDERING;
                }
                Monitor.Wait(fb, 50);
            }
        }
        return 0;
    }

    [Hle]
    int sceSharedFbUpdateProcessEnd()
    {
        if (Fbs.Updating is { } fb && fb.CreatorPid == Emu.Pid) Fbs.Updating = null;
        return 0;
    }

    /// <summary>The display port the shell renders on while an application is in front: port 0.</summary>
    [Hle] int sceSharedFbGetShellRenderPort() => 0;

    /// <summary>(SceUID pid): whether the process renders through the shared framebuffer (it opened it: the kernel looks
    /// it up in the client slots). The shell polls it after launching a system program and keeps its launch screen
    /// until then. A process that shows its own framebuffer counts as rendering once it shows a frame.</summary>
    [Hle]
    int sceSharedFbGetRenderingInfo(int pid)
    {
        if (Emu.Vita.ByPid(pid) is not { } p) return ERR_INVALID;
        lock (Fbs) if (Fbs.All.Any(f => f.Clients.Contains(pid))) return 0;
        return p.Hle.Modules.OfType<Display>().Any(d => d.HasFrame) ? 0 : unchecked((int)0x80010016);
    }

    /// <summary>Whether <paramref name="shell"/> shows the screen for the process in front: that process draws into the
    /// framebuffer the shell composites.</summary>
    internal static bool ComposesForeground(Emulator shell)
    {
        var fg = shell.Vita.Foreground;
        if (fg == null || ReferenceEquals(fg, shell)) return false;
        var fbs = shell.Vita.SharedFbs;
        lock (fbs) return fbs.All.Any(f => f.Compositor && f.CreatorPid == shell.Pid && f.Clients.Contains(fg.Pid));
    }

    /// <summary>The shared framebuffers of one system (one per index).</summary>
    public sealed class Registry
    {
        readonly List<SharedFbObject> _fbs = new();
        /// <summary>The framebuffer whose shell frame is between UpdateProcessBegin and UpdateProcessEnd.</summary>
        internal volatile SharedFbObject? Updating;
        internal IEnumerable<SharedFbObject> All => _fbs;
        internal SharedFbObject? ByIndex(int index) => _fbs.FirstOrDefault(f => f.Index == index);
        internal SharedFbObject? ByUid(int uid) => _fbs.FirstOrDefault(f => f.Uid == uid);
        internal void Set(SharedFbObject fb) { _fbs.RemoveAll(f => f.Index == fb.Index); _fbs.Add(fb); }
        internal void Remove(SharedFbObject fb) => _fbs.Remove(fb);
    }

    public sealed class SharedFbObject
    {
        public required int Index, Uid, CreatorPid;
        public required uint Base1, Base2;
        /// <summary>Created by the shell (sceSharedFbCreate): its clients draw when the shell lets them.</summary>
        public required bool Compositor;
        internal readonly List<int> Clients = new();
        internal readonly HashSet<int> Rendered = new();
        internal List<int> Active = new();
        internal int NextUpdate, Granted;
        internal bool Done;
        /// <summary>The 0x40 bytes the shell passed to UpdateProcessBegin (SceSharedFbInfo +8..+0x47 of its clients).</summary>
        internal uint[] ShellTarget = new uint[0x10];
    }
}
