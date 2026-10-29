using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSharedFb: the framebuffer the system software draws into. On the Vita the shell creates it
/// (sceSharedFbCreate) and composites it; libpaf in every system program opens it (_sceSharedFbOpen), takes the
/// back buffer with sceSharedFbBegin, renders into it with GXM and hands it back with sceSharedFbEnd. Until the
/// shell's compositing runs (sceSharedFbUpdateProcess*), End shows the finished buffer directly. Layout of
/// SceSharedFbInfo (0x58) and the 960x544 ABGR8888 buffers as in Vita3K (SceAppMgr/SceSharedFb.cpp).
/// </summary>
public sealed class SharedFb : HleModule
{
    /// <summary>SCE_KERNEL_ERROR_INVALID_ARGUMENT (inferred: the firmware's own code is not known).</summary>
    const int ERR_INVALID = unchecked((int)0x80020003);
    const uint InfoSize = 0x58, Width = 960, Height = 544, Pitch = 960, ColorFormatAbgr8888 = 0x80001000;
    const uint BufferSize = Pitch * Height * 4;

    sealed class Fb
    {
        public required int Uid;
        public required uint Base1, Base2;
        public int Opens;
    }

    readonly object _lock = new();
    readonly Dictionary<int, Fb> _byIndex = new();
    uint _frameBufDesc;

    Fb Create(int index)
    {
        lock (_lock)
        {
            if (_byIndex.TryGetValue(index, out var fb)) return fb;
            uint mem = Emu.Memory.Alloc(BufferSize * 2, $"SceSharedFb{index}", MemoryRegion.Cdram, 0x40000);
            GuestMemory.Fill(mem, 0, BufferSize * 2);
            fb = new Fb { Uid = 0x00F00001 + index, Base1 = mem, Base2 = mem + BufferSize };
            _byIndex[index] = fb;
            Log.Info($"SceSharedFb {index}: 2 x {Width}x{Height} buffers at 0x{mem:X8}");
            return fb;
        }
    }

    Fb? ById(int uid)
    {
        lock (_lock) return _byIndex.Values.FirstOrDefault(f => f.Uid == uid);
    }

    void WriteInfo(Fb fb, uint info)
    {
        GuestMemory.Fill(info, 0, InfoSize);
        GuestMemory.W32(info + 0x00, fb.Base1);
        GuestMemory.W32(info + 0x04, BufferSize * 2);
        GuestMemory.W32(info + 0x08, fb.Base2);
        GuestMemory.W32(info + 0x24, Pitch);
        GuestMemory.W32(info + 0x28, Width);
        GuestMemory.W32(info + 0x2C, Height);
        GuestMemory.W32(info + 0x30, ColorFormatAbgr8888);
        GuestMemory.W32(info + 0x34, 0); // curbuf: base1 is on screen, libpaf draws into base2
        GuestMemory.W32(info + 0x48, 1); // vsync
    }

    /// <summary>(int index): the shell creates shared framebuffer 0 at boot. Returns its UID.</summary>
    [Hle] int sceSharedFbCreate(int index) => Create(index).Uid;

    /// <summary>(int index, int sysver). Opens the shell's framebuffer; created here when no shell process made it.</summary>
    [Hle]
    int _sceSharedFbOpen(int index, uint sysver)
    {
        var fb = Create(index);
        Interlocked.Increment(ref fb.Opens);
        return fb.Uid;
    }

    [Hle]
    int sceSharedFbClose(int uid)
    {
        if (ById(uid) is not { } fb) return ERR_INVALID;
        Interlocked.Decrement(ref fb.Opens);
        return 0;
    }
    [Hle] int sceSharedFbDelete(int uid) => ById(uid) != null ? 0 : ERR_INVALID;

    [Hle]
    int sceSharedFbGetInfo(int uid, uint info)
    {
        if (ById(uid) is not { } fb || info == 0) return ERR_INVALID;
        WriteInfo(fb, info);
        return 0;
    }

    /// <summary>Takes the buffer for drawing: libpaf draws into the one info.curbuf (+0x34) does not name.</summary>
    [Hle]
    int sceSharedFbBegin(int uid, uint info)
    {
        if (ById(uid) is not { } fb) return ERR_INVALID;
        if (info != 0) WriteInfo(fb, info);
        return 0;
    }

    /// <summary>The frame is complete: it goes on screen (after the GPU work queued before it). info.curbuf names the
    /// buffer on screen and libpaf draws into the other one: Trophy Collection (FW 3.74) renders every frame into base2
    /// with curbuf 0. Showing base1/base2 alternately (a swap per End) put its undrawn buffer on screen every other
    /// frame (black flicker), so curbuf stays and End shows the buffer libpaf draws.</summary>
    [Hle]
    int sceSharedFbEnd(int uid)
    {
        if (ById(uid) is not { } fb) return ERR_INVALID;
        uint shown = fb.Base2;
        if (_frameBufDesc == 0) _frameBufDesc = Emu.Memory.Alloc(0x20, "SceSharedFb display desc");
        GuestMemory.W32(_frameBufDesc + 0x00, 0x18);
        GuestMemory.W32(_frameBufDesc + 0x04, shown);
        GuestMemory.W32(_frameBufDesc + 0x08, Pitch);
        GuestMemory.W32(_frameBufDesc + 0x0C, 0); // SCE_DISPLAY_PIXELFORMAT_A8B8G8R8
        GuestMemory.W32(_frameBufDesc + 0x10, Width);
        GuestMemory.W32(_frameBufDesc + 0x14, Height);
        Emu.Hle.Modules.OfType<Display>().First().SetFrameBuf(_frameBufDesc, 1);
        return 0;
    }

    /// <summary>The shell's compositor side: it brackets its frame with these. VitaSharp composites by process (the
    /// display shows the foreground process: <see cref="Display.SetFrameBuf"/>), so there is nothing to merge here.</summary>
    [Hle] int sceSharedFbUpdateProcessBegin(int uid) => 0;
    [Hle] int sceSharedFbUpdateProcessEnd(int uid) => 0;
    [Hle] int sceSharedFbUpdateProcess(int uid) => 0;

    /// <summary>The render port the shell draws its overlay into while an application is on screen: port 0.</summary>
    [Hle] int sceSharedFbGetShellRenderPort() => 0;

    /// <summary>(SceUID pid): whether the process has started showing frames. The shell polls it after launching an
    /// application and keeps its launch screen until the application's first frame (0 = it renders).</summary>
    [Hle]
    int sceSharedFbGetRenderingInfo(int pid)
    {
        if (Emu.Vita.ByPid(pid) is not { } p) return ERR_INVALID;
        return p.Hle.Modules.OfType<Display>().Any(d => d.HasFrame) ? 0 : unchecked((int)0x80010016);
    }
}
