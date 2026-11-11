using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceCompat (os0:kd/bootimage.skprx, FW 3.74): the kernel service of the PSP emulator (vs0:app/NPXS10028, pspemu).
/// The firmware gives the emulator a memory window (sceCompatAllocCdramWithHole), then sceCompatInitEx loads the PSP
/// compatibility firmware (vs0:app/NPXS10028/pcff.skprx and os0:kd/pcbc.skprx) into it and grabs the compat core,
/// sceCompatStart starts it, and from then on the core and the emulator talk through a shared block: requests the
/// emulator waits for per channel, interrupts it raises, 32-bit shared registers, return values, the "dat" block.
/// Everything that does not need the compat core behaves as in the module (memory window, framebuffer, display
/// configuration and LCDC sync, rif name, title list, ID storage, secure tick, update state, peripheral state);
/// the compat core itself is not emulated yet (docs/roadmap.md: PSP games through the real pspemu), so starting it is a
/// default, and the calls that need a running core get the module's own "not started" answer (0x80027205).
/// Every entry is for the PSP emulator or a system program (0x80010058 otherwise).
/// </summary>
public sealed class Compat : HleModule
{
    const int ERR_PERM = unchecked((int)0x80010058), ERR_NOT_STARTED = unchecked((int)0x80027205),
        ERR_NOT_OWNER = unchecked((int)0x8002F001), ERR_ARG = unchecked((int)0x80020005), ERR_INVAL = unchecked((int)0x80010016),
        ERR_EXISTS = unchecked((int)0x80010078), ERR_SIZE = unchecked((int)0x8002000B), ERR_RANGE = unchecked((int)0x80020006),
        ERR_TIMEOUT = unchecked((int)0x80028005), ERR_NO_CORE = unchecked((int)0x80010086), ERR_NO_RESUME = unchecked((int)0x8001008D),
        ERR_LEN = unchecked((int)0x8002000B), ERR_IDSTORAGE_KEY = unchecked((int)0x80000100), ERR_DAT_KIND = unchecked((int)0x80000100);

    const uint WindowSize = 0x4000000, DatSize = 0x1038;

    int _owner = -1;          // the process that took the memory window
    uint _window;             // the 64 MB window (the hole at +0x2000000..+0x3000000 is not the emulator's)
    bool _coreRunning;        // never: the compat core is not emulated
    int _status;              // 0 after InitEx, 1 stopped
    int _fbUid;               // the framebuffer memory block (sceCompatFrameBufferInit)
    ulong _updateTick;        // next update check (sceCompatSetUpdateState: now + 1 day)
    string _rif = "";
    readonly byte[] _dat = new byte[DatSize];
    readonly int[] _intrSema = [-1, -1, -1];
    readonly int[] _requestSema = Enumerable.Repeat(-1, 16).ToArray();
    uint _fbParam;

    bool Allowed => Emu.IsSystemProgram;
    bool Owner => _owner == Emu.Pid;
    ThreadMgr Tm => Emu.Hle.Modules.OfType<ThreadMgr>().First();

    int Sema(int[] table, int i, string name)
    {
        if (table[i] < 0) table[i] = Tm.sceKernelCreateSema($"{name}{i}", 0, 0, 1, 0);
        return table[i];
    }

    // ---- memory window

    /// <summary>(SceUIntPtr out[2]): the emulator's memory window: [0] its cached base, [1] the uncached view (the same
    /// memory here). Once per boot: 0x80010078 after.</summary>
    [Hle]
    int sceCompatAllocCdramWithHole(uint outBases)
    {
        if (!Allowed) return ERR_PERM;
        if (_owner >= 0) return ERR_EXISTS;
        _owner = Emu.Pid;
        _window = Mem.Alloc(WindowSize, "SceCompat window", MemoryRegion.User, 0x100000);
        if (outBases != 0)
        {
            GuestMemory.W32(outBases, _window);
            GuestMemory.W32(outBases + 4, _window);
        }
        return 0;
    }

    /// <summary>(int op 1 invalidate / 2 clean / 3 clean+invalidate, addr, size): a cache operation on the window
    /// (64-byte lines). The HLE's memory is coherent: only the range checks remain.</summary>
    [Hle]
    int sceCompatCache(int op, uint addr, uint size)
    {
        if (!Allowed) return ERR_PERM;
        if (size - 1 > 0x3FFFFFF) return ERR_SIZE;
        if (addr - _window > 0x3FFFFFF || _window + WindowSize < addr + size) return ERR_RANGE;
        return 0;
    }

    // ---- the compat core

    /// <summary>(int bootParam): loads the PSP compatibility firmware into the window and grabs the compat core.
    /// Without the window 0x80027205; the compat core is not emulated, so the load stops there too.</summary>
    [Hle(Default = true)]
    int sceCompatInitEx(int bootParam)
    {
        if (!Allowed) return ERR_PERM;
        if (!Owner) return ERR_NOT_OWNER;
        Array.Clear(_dat);
        _status = 0;
        Log.Info($"SceCompat: InitEx({bootParam}): the PSP compat core is not emulated");
        return ERR_NOT_STARTED;
    }

    /// <summary>Starts the compat core (not emulated): the module's stop path runs, as when the core fails to start.</summary>
    [Hle(Default = true)]
    int sceCompatStart()
    {
        if (!Allowed) return ERR_PERM;
        if (!Owner) return ERR_NOT_OWNER;
        StopCore();
        return ERR_NOT_STARTED;
    }

    void StopCore()
    {
        _coreRunning = false;
        _status = 1;
        Array.Clear(_dat);
    }

    [Hle]
    int sceCompatStop()
    {
        if (!Allowed) return ERR_PERM;
        if (!Owner) return ERR_NOT_OWNER;
        StopCore();
        return 0;
    }

    /// <summary>Releases the framebuffer block reference.</summary>
    [Hle]
    int sceCompatUninit()
    {
        if (!Allowed) return ERR_PERM;
        _fbUid = 0;
        return 0;
    }

    /// <summary>The state: 0 initialized, 1 stopped (2 a debugger attached).</summary>
    [Hle] int sceCompatGetStatus() => Allowed ? _status : ERR_PERM;

    /// <summary>(int resume): 1 resumes a suspended compat core; nothing was suspended: 0x8001008D.</summary>
    [Hle]
    int sceCompatSuspendResume(int resume)
    {
        if (!Allowed) return ERR_PERM;
        return resume == 1 && _status == 1 ? ERR_NO_RESUME : 0;
    }

    /// <summary>(int channel &lt; 16, int kind &lt; 3): waits for the next request the core posts on the channel and
    /// returns it; nothing posts without the core, so the caller waits (as for a core with no request).</summary>
    [Hle]
    int sceCompatWaitAndGetRequest(uint channel, uint kind)
    {
        if (!Allowed) return ERR_PERM;
        if (channel >= 16 || kind >= 3) return ERR_ARG;
        int r = Tm.sceKernelWaitSema(Sema(_requestSema, (int)channel, "SceCompatRequest"), 1, new Ptr<uint>(0));
        return r < 0 ? r : _coreRunning ? 0 : ERR_INVAL;
    }

    /// <summary>(int which 1..3): waits for the core's special handshake (boot, suspend, resume) through the shared
    /// block, 5 s at most for the first: without a core, the timeout 0x80028005. Others: 0.</summary>
    [Hle]
    int sceCompatWaitSpecialRequest(int which)
    {
        if (!Allowed) return ERR_PERM;
        if (which is < 1 or > 3) return 0;
        Tm.sceKernelDelayThread(which == 1 ? 5_000_000u : 30_000_000u);
        return ERR_TIMEOUT;
    }

    /// <summary>(int which &lt; 3): waits for an interrupt from the core.</summary>
    [Hle]
    int sceCompatWaitIntr(uint which)
    {
        if (!Allowed) return ERR_PERM;
        if (which >= 3) return ERR_ARG;
        return Tm.sceKernelWaitSema(Sema(_intrSema, (int)which, "SceCompatIntr"), 1, new Ptr<uint>(0));
    }

    /// <summary>(int line 1..15): raises an interrupt on the core; no core running: 0x80010086.</summary>
    [Hle]
    int sceCompatInterrupt(int line)
    {
        if (!Allowed) return ERR_PERM;
        if ((uint)(line - 1) >= 15) return ERR_INVAL;
        return _coreRunning ? 0 : ERR_NO_CORE;
    }

    /// <summary>(int channel &lt; 3, const u32 value[2]): the return value of a request, for the core.</summary>
    [Hle]
    int sceCompatReturnValueEx(uint channel, uint value)
    {
        if (!Allowed) return ERR_PERM;
        if (channel >= 3) return ERR_ARG;
        return _coreRunning ? 0 : ERR_NOT_STARTED;
    }

    /// <summary>(int reg 0..7, u32 *value): a shared register the core writes.</summary>
    [Hle]
    int sceCompatReadShared32(int reg, uint value)
    {
        if (!Allowed) return ERR_PERM;
        if (!_coreRunning) return ERR_NOT_STARTED;
        if (reg is < 0 or > 7 or 3) return ERR_ARG;
        GuestMemory.W32(value, 0);
        return 0;
    }

    /// <summary>(int reg 0x80..0x88, u32 value): a shared register for the core.</summary>
    [Hle]
    int sceCompatWriteShared32(int reg, uint value)
    {
        if (!Allowed) return ERR_PERM;
        if (!_coreRunning) return ERR_NOT_STARTED;
        return reg is >= 0x80 and <= 0x88 ? 0 : ERR_ARG;
    }

    /// <summary>(const u32 ctrl[3]): the controller state for the PSP side.</summary>
    [Hle]
    int sceCompatWriteSharedCtrl(uint ctrl)
    {
        if (!Allowed) return ERR_PERM;
        return _coreRunning ? 0 : ERR_NOT_STARTED;
    }

    /// <summary>(int kind 0, offset, void *buf, size): reads the "dat" block the core fills (0x1038 bytes), in 0x800 pieces.</summary>
    [Hle]
    int sceCompatDatRead(int kind, uint offset, uint buf, uint size)
    {
        if (!Allowed) return ERR_PERM;
        if (kind != 0) return ERR_DAT_KIND;
        if (size > DatSize || offset >= DatSize || offset + size > DatSize) return ERR_LEN;
        if (!Owner) return ERR_NOT_OWNER;
        _dat.AsSpan((int)offset, (int)size).CopyTo(GuestMemory.Span(buf, (int)size));
        return 0;
    }

    // ---- display

    /// <summary>(SceUID block): the memory block the PSP framebuffer lives in (once; 0x80010078 after).</summary>
    [Hle]
    int sceCompatFrameBufferInit(int uid)
    {
        if (!Allowed) return ERR_PERM;
        if (_fbUid != 0) return ERR_EXISTS;
        if (K.Objects.Get<MemBlock>(uid) == null) return SceError.INVALID_UID;
        _fbUid = uid;
        return 0;
    }

    /// <summary>(int wide, int sync): shows the PSP framebuffer (480x272, pitch 512, A8B8G8R8) on the screen.</summary>
    [Hle]
    int sceCompatSetDisplayConfig(int wide, uint sync)
    {
        if (!Allowed) return ERR_PERM;
        if (_fbUid <= 0 || K.Objects.Get<MemBlock>(_fbUid) is not { } fb) return ERR_NOT_STARTED;
        if (_fbParam == 0) _fbParam = Mem.Alloc(0x20, "SceCompat framebuffer param");
        GuestMemory.W32(_fbParam, 0x1C);
        GuestMemory.W32(_fbParam + 4, fb.Address);
        GuestMemory.W32(_fbParam + 8, 0x200);
        GuestMemory.W32(_fbParam + 12, 0);
        GuestMemory.W32(_fbParam + 16, 480);
        GuestMemory.W32(_fbParam + 20, 272);
        GuestMemory.W32(_fbParam + 24, wide != 0 ? 0x100000u : 0);
        return Emu.Hle.Modules.OfType<Display>().First().SetFrameBuf(_fbParam, 1);
    }

    /// <summary>Waits for the LCD controller's next frame (the PSP's vsync).</summary>
    [Hle]
    int sceCompatLCDCSync()
    {
        if (!Allowed) return ERR_PERM;
        if (!FrameRate.NoWaits) VBlank.WaitNext();
        return 0;
    }

    /// <summary>The primary display head (0: the handheld's screen).</summary>
    [Hle] int sceCompatGetPrimaryHead() => Allowed ? 0 : ERR_PERM;

    /// <summary>Whether the display head offers the colour space setting (an HDMI output with both ranges): not on the
    /// handheld's screen.</summary>
    [Hle] int sceCompatAvailableColorSpaceSetting() => 0;

    // ---- system information

    /// <summary>The PSP system software the emulator runs: 6.61 (0x295). The shell asks it through SceCompatForVsh before
    /// it starts a PSP/PSone title (the title's PARAM.SFO PSP_SYSTEM_VER must not be newer; without the export it showed
    /// "update the system software to version -").</summary>
    [Hle]
    [Hle("SceCompatForVsh_9C107CC9", Nid = 0x9C107CC9, Library = "SceCompatForVsh")]
    int sceCompatGetPspSystemSoftwareVersion() => Allowed ? 0x295 : ERR_PERM;

    /// <summary>(int which): 1 the headphone remote (none), 2 battery percent, 3 battery minutes, 4 a suspend request.</summary>
    [Hle]
    int sceCompatGetPeripheralState(int which)
    {
        if (!Allowed) return ERR_PERM;
        return which switch
        {
            1 => 0,
            2 => 100,
            3 => 300,
            4 => 0,
            _ => ERR_INVAL,
        };
    }

    /// <summary>(u64 *tick): the secure RTC tick.</summary>
    [Hle]
    int sceCompatGetCurrentSecureTick(Ptr<ulong> tick)
    {
        if (!Allowed) return ERR_PERM;
        if (tick.IsNull) return SceError.INVALID_ARGUMENT;
        tick.TrySet(Rtc.NowTick);
        return 0;
    }

    /// <summary>1 when the PSP system software update check is due (none stored, or the stored time has passed).</summary>
    [Hle]
    int sceCompatGetUpdateState()
    {
        if (!Allowed) return ERR_PERM;
        return _updateTick == 0 || Rtc.NowTick > _updateTick ? 1 : 0;
    }

    /// <summary>Stores the next update check: now plus one day.</summary>
    [Hle]
    int sceCompatSetUpdateState()
    {
        if (!Allowed) return ERR_PERM;
        _updateTick = Rtc.NowTick + 86_400_000_000UL;
        return 0;
    }

    /// <summary>(SceUID suspend, SceUID resume): the semaphores the module signals around a suspend.</summary>
    [Hle]
    int sceCompatSetSuspendSema(int suspend, int resume)
    {
        if (!Allowed) return ERR_PERM;
        if (suspend != 0 && K.Objects.Get<KernelObject>(suspend) == null) return SceError.INVALID_UID;
        if (resume != 0 && K.Objects.Get<KernelObject>(resume) == null) return SceError.INVALID_UID;
        return 0;
    }

    /// <summary>(const char contentId[0x24]): the license of the running PSP title: ux0:pspemu/PSP/LICENSE/&lt;id&gt;.rif
    /// (an empty id clears it).</summary>
    [Hle]
    int sceCompatSetRif(uint contentId)
    {
        if (!Allowed) return ERR_PERM;
        if (!Owner) return ERR_NOT_OWNER;
        var id = GuestMemory.ReadSpan(contentId, 0x24);
        _rif = id.IndexOf((byte)0) >= 0 ? "" : $"ux0:pspemu/PSP/LICENSE/{System.Text.Encoding.ASCII.GetString(id)}.rif";
        Log.Info($"SceCompat: rif {(_rif.Length == 0 ? "(none)" : _rif)}");
        return 0;
    }

    /// <summary>(void *buf, int size 100): the title list the shell left for the emulator in
    /// ur0:emu_list/title_list.bin (read once: a valid list, 95 characters, is deleted after).</summary>
    [Hle]
    [Hle("SceCompatForVsh_4915DEE7", Nid = 0x4915DEE7, Library = "SceCompatForVsh")]
    int sceCompatGetTitleList(uint buf, int size)
    {
        if (!Allowed) return ERR_PERM;
        if (size != 100) return unchecked((int)0x8002000B);
        string? host = Emu.Vfs.Resolve("ur0:emu_list/title_list.bin");
        if (host == null || !File.Exists(host)) return unchecked((int)0x80010002);
        byte[] b = File.ReadAllBytes(host);
        if (b.Length < 100) return unchecked((int)0x80010005);
        b.AsSpan(0, 100).CopyTo(GuestMemory.Span(buf, 100));
        int len = Array.IndexOf(b, (byte)0, 0, 100);
        if ((len < 0 ? 100 : len) != 0x5F) return unchecked((int)0x80010002);
        File.Delete(host);
        return 0;
    }

    /// <summary>(int key 0x100..0x13F, offset, void *buf, size &lt;= 0x200): an ID storage leaf of the PSP range. The
    /// emulated unit's leaves hold no data (zeros, as vshIdStorageLookup reads).</summary>
    [Hle]
    int sceCompatIdStorageLookup(int key, uint offset, uint buf, uint size)
    {
        if (!Allowed) return ERR_PERM;
        size = Math.Min(size, 0x200);
        if (offset >= 0x200 || offset + size > 0x200) return ERR_LEN;
        if (!Owner) return ERR_NOT_OWNER;
        if ((ushort)(key - 0x100) > 0x3F) return ERR_IDSTORAGE_KEY;
        GuestMemory.Fill(buf, 0, size);
        return 0;
    }

    /// <summary>(SceIoDevInfo *info, 0x18 bytes): the free space of ux0: (devctl 0x3001).</summary>
    [Hle]
    int sceCompatGetDevInf(uint info)
    {
        if (!Allowed) return ERR_PERM;
        if (!Owner) return ERR_NOT_OWNER;
        if (info == 0) return SceError.INVALID_ARGUMENT;
        GuestMemory.Fill(info, 0, 0x18);
        GuestMemory.Write(info, 8UL * 1024 * 1024 * 1024);
        GuestMemory.Write(info + 8, 4UL * 1024 * 1024 * 1024);
        GuestMemory.W32(info + 16, 4096);
        return 0;
    }

    /// <summary>Whether the PocketStation application is licensed (its rif under ux0:pspemu/PSP/LICENSE).</summary>
    [Hle]
    int sceCompatCheckPocketStation()
    {
        if (!Allowed) return ERR_PERM;
        string? host = Emu.Vfs.Resolve("ux0:pspemu/PSP/LICENSE/JA0003-PCSC80018_00-POCKETSTATION001.rif");
        if (host == null || !File.Exists(host)) return 0;
        byte[] b = File.ReadAllBytes(host);
        if (b.Length is 0 or > 0x200) return 0;
        return System.Text.Encoding.ASCII.GetString(b).Contains("JA0003-PCSC80018_00-POCKETSTATION001") ? 1 : 0;
    }

    /// <summary>(const char titleId[10]): whether a PSP title works with the PocketStation; the module checks a hashed
    /// list of title ids it carries. The list is not part of the HLE: no title is in it.</summary>
    [Hle(Default = true)]
    [Hle("SceCompatForVsh_70997F92", Nid = 0x70997F92, Library = "SceCompatForVsh", Default = true)]
    int sceCompatIsPocketStationTitle(uint titleId)
    {
        if (!Allowed) return ERR_PERM;
        if (titleId == 0) return SceError.INVALID_ARGUMENT;
        return GuestMemory.Read<byte>(titleId + 9) != 0 ? unchecked((int)0x80010016) : 0;
    }
}
