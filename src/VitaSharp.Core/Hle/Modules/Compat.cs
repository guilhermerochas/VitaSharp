using VitaSharp.Core.Compat;
using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Psp;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Semaphore = VitaSharp.Core.Kernel.Semaphore;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceCompat (os0:kd/bootimage.skprx, FW 3.74): the kernel service of the PSP emulator (vs0:app/NPXS10028, pspemu).
/// The firmware gives the emulator a memory window (sceCompatAllocCdramWithHole), then sceCompatInitEx points the
/// compat core's four RAM banks at it, loads the PSP compatibility firmware into it through SceSblFwLoader (pcff at
/// window + 0x3000000, the boot code pcbc at window + 0x600000, the random seed at + 0x3FFF00) and takes the core;
/// sceCompatStart releases it from reset; sceCompatWaitSpecialRequest(1) runs the boot handshake on the word at
/// window + 0x3D78000 (core 1, ARM 2, core 3, ARM 0). From then on the core and the emulator talk through the shared
/// SRAM (PSP 0xBFC00000) and the mailbox: the core posts commands (IRQ 0x44..0x46) the emulator takes per channel
/// (sceCompatWaitAndGetRequest) and answers (sceCompatReturnValueEx), raises answer interrupts
/// (sceCompatWaitIntr) and gets virtual interrupts (sceCompatInterrupt), the controller words
/// (sceCompatWriteSharedCtrl) and the shared registers. The core is the Allegrex (VitaSharp.Core.Compat), run by
/// <see cref="CompatCoreRunner"/>; docs/notes/psp-compat.md has the layout. The secure processor's two commands
/// (0x10006 when the core is taken, 0x30006 at the handshake) give no data back: status 0.
/// Everything that does not need the compat core behaves as in the module (memory window, framebuffer, display
/// configuration and LCDC sync, rif name, title list, ID storage, secure tick, update state, peripheral state).
/// Every entry is for the PSP emulator or a system program (0x80010058 otherwise).
/// </summary>
public sealed class Compat : HleModule, IDisposable
{
    const int ERR_PERM = unchecked((int)0x80010058), ERR_NOT_STARTED = unchecked((int)0x80027205),
        ERR_NOT_OWNER = unchecked((int)0x8002F001), ERR_ARG = unchecked((int)0x80020005), ERR_INVAL = unchecked((int)0x80010016),
        ERR_EXISTS = unchecked((int)0x80010078), ERR_SIZE = unchecked((int)0x8002000B), ERR_RANGE = unchecked((int)0x80020006),
        ERR_TIMEOUT = unchecked((int)0x80028005), ERR_NO_CORE = unchecked((int)0x80010086), ERR_NO_RESUME = unchecked((int)0x8001008D),
        ERR_LEN = unchecked((int)0x8002000B), ERR_IDSTORAGE_KEY = unchecked((int)0x80000100), ERR_DAT_KIND = unchecked((int)0x80000100);

    /// <summary>The owner went away during a handshake.</summary>
    const int ERR_OWNER_GONE = unchecked((int)0x80028007);

    /// <summary>sceCompatStart: the running title's license does not pass (a retail unit).</summary>
    const int ERR_LICENSE = unchecked((int)0x8001000D);

    /// <summary>The compat core is not emulated (no core implementation).</summary>
    const int ERR_UNSUP = unchecked((int)0x80020004);

    const uint WindowSize = 0x4000000, DatSize = 0x1038, LoaderScratchSize = 0x110;

    /// <summary>The start path's arguments: the activation data, then the license (0x200) and the key query's outputs.</summary>
    const uint StartLicenseOffset = 0x1040, LicenseMaxSize = 0x200, StartScratchSize = StartLicenseOffset + LicenseMaxSize + 0x40;

    /// <summary>The window's layout (window + offset = PSP 0x88000000 + offset).</summary>
    internal const uint PcbcOffset = 0x600000, PcbcMaxSize = 0xA00000, PcffOffset = 0x3000000, PcffMaxSize = 0x2000000,
        SeedOffset = 0x3FFF00, HandshakeOffset = 0x3D78000;

    /// <summary>Where the core starts: the boot code (PSP 0x88600000). The reset vector of the core (0xBFC00000, the
    /// shared SRAM) is prepared by the secure side; custom boot code (VitaMips) placed at 0x88600000 runs from
    /// sceCompatStart, so the reset leads there.</summary>
    internal const uint BootEntry = 0x88600000;

    /// <summary>The handshake word's states.</summary>
    internal const uint HandshakeReady = 1, HandshakeAcknowledged = 2, HandshakeRunning = 3, HandshakeIdle = 0;

    static readonly string PcffPath = Io.FirmwareLayout.SystemApplications.Guest("NPXS10028", "pcff.skprx");
    static readonly string PcbcPath = Io.FirmwareLayout.KernelModules.Guest("pcbc.skprx");

    /// <summary>The request channels, the entries of a channel's ring, the command / answer slots.</summary>
    const int Channels = 16, RingEntries = 16, Slots = 3;

    /// <summary>The mailbox lines: commands 4 + slot, answers 7 + slot, virtual interrupts 0x10 + line; the lines the
    /// handshake leaves alone.</summary>
    const int CommandLine = 4, AnswerLine = 7, VirtualLine = 0x10, HandshakeKeptLine1 = 1, HandshakeKeptLine2 = 0xE;

    /// <summary>The compat clock SceCompat sets for the core (ScePowerForDriver_FFC84E69(0x14D)).</summary>
    const int CompatClockMhz = 333;

    /// <summary>SceCompat's limits on the core: the boot handshake, the suspend handshakes, a virtual interrupt slot.</summary>
    const ulong HandshakeTimeoutUs = 5_000_000, SuspendTimeoutUs = 30_000_000, VirtualSlotTimeoutUs = 1_000_000;

    int _owner = -1;          // the process that took the memory window
    uint _window;             // the 64 MB window (the hole at +0x2000000..+0x3000000 is not the emulator's)
    int _status;              // 0 after InitEx, 2 running, 1 stopped
    uint _coreReady;          // the handshake's state 1 once the boot handshake passed: the core takes answers
    uint _seed;               // the random word for the core (window + 0x3FFF00)
    int _fbUid;               // the framebuffer memory block (sceCompatFrameBufferInit)
    ulong _updateTick;        // next update check (sceCompatSetUpdateState: now + 1 day)
    string _rif = "";
    readonly byte[] _dat = new byte[DatSize];
    readonly int[] _intrSema = [-1, -1, -1];
    readonly int[] _requestSema = Enumerable.Repeat(-1, Channels).ToArray();
    readonly Semaphore?[] _intrSemaphores = new Semaphore?[Slots];
    readonly Semaphore?[] _requestSemaphores = new Semaphore?[Channels];
    uint _fbParam;
    uint _loaderScratch;      // the loader's arguments: the loaded size, then the path
    uint _startScratch;       // the start path's arguments (StartScratchSize)
    int _displayColorSpaceMode; // ksceDisplaySetColorSpaceMode's mode SceAVConfig last set (0 at boot)

    // SceCompat's own lock (the "SceCompat" event flag every core entry takes), and the request rings' (interrupts off)
    readonly object _apiLock = new();
    readonly object _ringLock = new();
    readonly RequestRing[] _rings = CreateRings();

    readonly CompatSharedSram _sram = new();
    readonly CompatMailbox _mailbox = new();
    readonly PspBus _bus;
    readonly PspHardware _hardware;
    readonly CompatLcdDma _lcdDma;
    ICompatCore? _core;
    CompatCoreRunner? _runner;

    public Compat()
    {
        _bus = new PspBus(_sram, _mailbox);
        _hardware = PspHardware.CreateStandard(_bus);
        _bus.Hardware = _hardware;
        _mailbox.ArmInterrupt = ArmInterrupt;

        // the doorbells hold the core's IP2 beside the PSP interrupt controller's lines (their PSP interrupt number is
        // not known yet), and the controller's output is the core's IP2
        _mailbox.PspInterruptLevel = _hardware.Interrupts.SetExternalLevel;
        _hardware.Interrupts.OutputChanged = PspInterruptLevel;

        // the LCD DMA copies the PSP's picture into the framebuffer block at every PSP vertical blank
        _lcdDma = new CompatLcdDma(_bus, _hardware.Lcdc!);
        _lcdDma.BeforeRead = SynchronizeGe;
        _hardware.Display!.VblankStarted = _lcdDma.OnVblank;
    }

    /// <summary>One channel's ring of request addresses (16 entries; full: the overflow flag, the core's entry left in
    /// the shared SRAM until a request is taken).</summary>
    sealed class RequestRing
    {
        public readonly uint[] Entries = new uint[RingEntries];
        public int Read;
        public int Write;
        public int Count;
        public bool Overflow;

        public void Clear()
        {
            Array.Clear(Entries);
            Read = 0;
            Write = 0;
            Count = 0;
            Overflow = false;
        }
    }

    static RequestRing[] CreateRings()
    {
        RequestRing[] rings = new RequestRing[Channels];
        for (int i = 0; i < rings.Length; i++)
        {
            rings[i] = new RequestRing();
        }

        return rings;
    }

    /// <summary>The compat core for the bus (the Allegrex interpreter); tests give their own. Null: no core.</summary>
    internal Func<IAllegrexBus, ICompatCore?> CoreFactory { get; set; } = bus => new AllegrexCompatCore(new AllegrexCore(bus));

    /// <summary>Tests: the core's bus, shared SRAM, mailbox, runner and window.</summary>
    internal PspBus Bus => _bus;

    internal CompatSharedSram SharedSram => _sram;

    internal CompatMailbox Mailbox => _mailbox;

    internal CompatCoreRunner? Runner => _runner;

    internal PspHardware Hardware => _hardware;

    internal CompatLcdDma LcdDma => _lcdDma;

    internal uint Window => _window;

    /// <summary>Tests: the colour space mode NID_6EBD9E01 kept.</summary>
    internal int DisplayColorSpaceMode => _displayColorSpaceMode;

    /// <summary>NID_6EBD9E01 (SceCompatForDriver)(int mode): SceAVConfig tells SceCompat the display colour space mode
    /// it just set (ksceDisplaySetColorSpaceMode(0, mode)); the PSP emulator's display path sets that mode again when
    /// it gives the display back. Always 0.</summary>
    [Hle("NID_6EBD9E01", Nid = 0x6EBD9E01, Library = "SceCompatForDriver")]
    int ksceCompatSetDisplayColorSpaceMode(int mode)
    {
        _displayColorSpaceMode = mode;
        return 0;
    }

    bool Allowed => Emu.IsSystemProgram;
    bool Owner => _owner == Emu.Pid;
    ThreadMgr Tm => Emu.Hle.Modules.OfType<ThreadMgr>().First();
    SystemServicesVsh Services => Emu.Hle.Modules.OfType<SystemServicesVsh>().First();

    /// <summary>The semaphores of module_start: "SceCompatVirtualHandler" (channel 0, count 1 of 1) and
    /// "SceCompatHandler1".."15" (0 of 0x7FFFFFFF) the channels wait on, and the answer interrupts' (1 of 1).</summary>
    void CreateSemaphores()
    {
        for (int channel = 0; channel < Channels; channel++)
        {
            if (_requestSema[channel] < 0)
            {
                string name = channel == 0 ? "SceCompatVirtualHandler" : $"SceCompatHandler{channel}";
                int initial = channel == 0 ? 1 : 0;
                int maximum = channel == 0 ? 1 : 0x7FFFFFFF;
                _requestSema[channel] = Tm.sceKernelCreateSema(name, channel == 0 ? 0xA000u : 0x8000u, initial, maximum, 0);
                _requestSemaphores[channel] = K.Objects.Get<Semaphore>(_requestSema[channel]);
            }
        }

        for (int slot = 0; slot < Slots; slot++)
        {
            if (_intrSema[slot] < 0)
            {
                _intrSema[slot] = Tm.sceKernelCreateSema($"SceCompat{slot}r", 0x8000, 1, 1, 0);
                _intrSemaphores[slot] = K.Objects.Get<Semaphore>(_intrSema[slot]);
            }
        }
    }

    // ---- memory window

    /// <summary>(SceUIntPtr out[2]): the emulator's memory window: [0] its cached base, [1] the uncached view (the same
    /// memory here). Once per boot: 0x80010078 after. The core's random seed is drawn here.</summary>
    [Hle]
    int sceCompatAllocCdramWithHole(uint outBases)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if (_owner >= 0)
        {
            return ERR_EXISTS;
        }

        _owner = Emu.Pid;
        _window = Mem.Alloc(WindowSize, "SceCompat window", MemoryRegion.User, 0x100000);
        _seed = (uint)Random.Shared.NextInt64(0, 1L << 32);
        CreateSemaphores();
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
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if (size - 1 > 0x3FFFFFF)
        {
            return ERR_SIZE;
        }

        if (addr - _window > 0x3FFFFFF || _window + WindowSize < addr + size)
        {
            return ERR_RANGE;
        }

        return 0;
    }

    // ---- the compat core

    /// <summary>
    /// (int bootParam): clears the request rings, points the core's RAM banks at the window (bank 0 window, 1 and 2
    /// window + 0x1000000, 3 window + 0x3000000), loads pcff (window + 0x3000000, at most 0x2000000) and pcbc (window
    /// + 0x600000, at most 0xA00000) through SceSblFwLoader, writes the seed, sets the compat clock and takes the core;
    /// then the secure processor's command 0x10006 (status 0). Without the window 0x80027205; the loader's error as it
    /// comes (pcbc needs the user's decrypted image, see SceSblFwLoader).
    /// </summary>
    [Hle]
    int sceCompatInitEx(int bootParam)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if (!Owner)
        {
            return ERR_NOT_OWNER;
        }

        StopRunner();
        lock (_ringLock)
        {
            foreach (RequestRing ring in _rings)
            {
                ring.Clear();
            }
        }

        _status = 0;
        if (_window == 0)
        {
            return ERR_NOT_STARTED;
        }

        _bus.SetBank(0, _window | 1);
        _bus.SetBank(1, (_window + PspBus.BankSize) | 1);
        _bus.SetBank(2, (_window + PspBus.BankSize) | 1);
        _bus.SetBank(3, (_window + PcffOffset) | 1);

        int loaded = LoadBlob(PcffPath, _window + PcffOffset, PcffMaxSize);
        if (loaded < 0)
        {
            return loaded;
        }

        loaded = LoadBlob(PcbcPath, _window + PcbcOffset, PcbcMaxSize);
        GuestMemory.W32(_window + SeedOffset, _seed);
        if (loaded < 0)
        {
            return loaded;
        }

        ICompatCore? core = CoreFactory(_bus);
        if (core == null)
        {
            Log.Info($"SceCompat: InitEx({bootParam}): the PSP compat core is not emulated");
            return ERR_UNSUP;
        }

        _core = core;
        _sram.Clear();
        _mailbox.Reset();
        return 0;
    }

    /// <summary>One blob through SceSblFwLoader's exports (lock, load, unlock), as InitEx calls them: the loader's
    /// error, else 0.</summary>
    int LoadBlob(string path, uint buffer, uint size)
    {
        if (_loaderScratch == 0)
        {
            _loaderScratch = Mem.Alloc(LoaderScratchSize, "SceCompat loader arguments");
        }

        GuestMemory.WriteCString(_loaderScratch + 4, path, (int)LoaderScratchSize - 5);
        SystemServicesVsh services = Services;
        int result = services.ksceSblFwLoaderLockThroughEntry(_loaderScratch + 4, 0);
        if (result < 0)
        {
            return result;
        }

        result = services.ksceSblFwLoaderLoadThroughEntry(1, buffer, size, _loaderScratch);
        services.ksceSblFwLoaderUnlockThroughEntry();
        return result < 0 ? result : 0;
    }

    /// <summary>
    /// Releases the compat core from reset, as the module's start path: the activation data into the "dat" block
    /// (ksceNpDrmReadActData; its error unless the unit is a testing kit), the running title's license
    /// (ux0:pspemu/PSP/LICENSE/&lt;content id of sceCompatSetRif&gt;.rif, 1..0x200 bytes) through
    /// ksceNpDrmGetRifPspKey on a kernel stack (anything but 0 and not a testing kit: 0x8001000D), then the core
    /// starts at the boot code and the state is 2. Any failure runs the stop path. The license query is SceNpDrm's as
    /// the HLE has it: the title key is the secure processor's, so the query answers 0x80020004 after its checks and a
    /// retail unit does not start the core here (docs/roadmap.md, the PSP compat core item).
    /// </summary>
    [Hle]
    int sceCompatStart()
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if (!Owner)
        {
            return ERR_NOT_OWNER;
        }

        int result = StartCore();
        if (result < 0)
        {
            StopCore();
        }

        return result;
    }

    int StartCore()
    {
        if (_startScratch == 0)
        {
            _startScratch = Mem.Alloc(StartScratchSize, "SceCompat start arguments");
        }

        SystemServicesVsh services = Services;
        bool testingKit = services.ksceSblAimgrIsDEXThroughEntry() != 0;
        GuestMemory.Fill(_startScratch, 0, StartScratchSize);
        int activation = services.ksceNpDrmReadActDataThroughEntry(_startScratch);
        GuestMemory.ReadSpan(_startScratch, (int)DatSize).CopyTo(_dat);
        if (activation < 0 && !testingKit)
        {
            Log.Info($"SceCompat: Start: no activation data (ksceNpDrmReadActData 0x{activation:X8}): the PSP core is not started");
            return activation;
        }

        int license = LicenseCheck();
        if (license != 0 && !testingKit)
        {
            Log.Info($"SceCompat: Start: the title's license {(_rif.Length == 0 ? "(none set)" : _rif)} does not pass (0x{license:X8}): the PSP core is not started");
            return ERR_LICENSE;
        }

        return ReleaseCore();
    }

    /// <summary>The core out of reset at the boot code on its thread, state 2 (after the start path's checks).</summary>
    int ReleaseCore()
    {
        if (_core == null)
        {
            return ERR_NOT_STARTED;
        }

        lock (_apiLock)
        {
            _status = 2;
            _runner = new CompatCoreRunner(Emu, _core, AllegrexCore.InterruptControllerLine, CompatClockMhz, _hardware);
            StartLcdDma();
            _runner.Start(BootEntry);
        }

        return 0;
    }

    /// <summary>Tests only: releases the core after InitEx without the start path's license checks (which a retail unit
    /// does not pass in the HLE, see sceCompatStart), so the core's protocol can be tested. Not reachable from a guest.</summary>
    internal int ReleaseCoreForTests()
    {
        int result = ReleaseCore();
        if (result < 0)
        {
            StopCore();
        }

        return result;
    }

    /// <summary>The module's license check: the file sceCompatSetRif named (1..0x200 bytes) read and given to
    /// ksceNpDrmGetRifPspKey; its answer (0 passes), or an error when there is no such file.</summary>
    int LicenseCheck()
    {
        string? host = _rif.Length == 0 ? null : Emu.Vfs.Resolve(_rif);
        if (host == null || !File.Exists(host))
        {
            return unchecked((int)0x80010002);
        }

        byte[] license = File.ReadAllBytes(host);
        if (license.Length is 0 or > (int)LicenseMaxSize)
        {
            return ERR_SIZE;
        }

        uint rif = _startScratch + StartLicenseOffset;
        GuestMemory.Fill(rif, 0, LicenseMaxSize);
        license.CopyTo(GuestMemory.Span(rif, license.Length));
        uint outputs = rif + LicenseMaxSize;
        return Services.ksceNpDrmGetRifPspKeyThroughEntry(rif, outputs, outputs + 0x10, outputs + 0x18, outputs + 0x20);
    }

    /// <summary>sceCompatStart's LCD DMA: with a framebuffer block, its destination side (480x272, pitch 0x800) and
    /// the start; without one the engine stays idle.</summary>
    void StartLcdDma()
    {
        if (_fbUid > 0 && K.Objects.Get<MemBlock>(_fbUid) is { } block)
        {
            _lcdDma.Configure(block.Address, 480, 272, 0x800);
            _lcdDma.Start();
        }
    }

    /// <summary>The stop path: the core held in reset (its thread ends), the banks released, the clock off, state 1.</summary>
    void StopCore()
    {
        StopRunner();
        _lcdDma.Stop();
        lock (_apiLock)
        {
            _bus.UnmapBanks();
            _coreReady = 0;
            _status = 1;
            Array.Clear(_dat);
        }
    }

    /// <summary>The core's thread ends and the GE lets its Vulkan device go (a later start makes a new one).</summary>
    void StopRunner()
    {
        CompatCoreRunner? runner = _runner;
        if (runner != null)
        {
            runner.Stop();
            _runner = null;
        }

        _hardware.Ge?.ReleaseRasterizer();
    }

    /// <summary>The LCD DMA reads the PSP's picture: what the GE drew there and still holds on the GPU goes to memory
    /// first (PSP address, any segment).</summary>
    void SynchronizeGe(uint address, int length)
    {
        _hardware.Ge?.SynchronizeMemory(address & AllegrexCore.PhysicalMask, length);
    }

    /// <summary>The process ends (or re-executes): the core stops as the module's process event handler stops it, and
    /// nothing of it outlives the process (the core's thread, the GE's Vulkan device). Safe to call again.</summary>
    public void Dispose()
    {
        StopRunner();
        _hardware.Ge?.Dispose();
    }

    [Hle]
    int sceCompatStop()
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if (!Owner)
        {
            return ERR_NOT_OWNER;
        }

        StopCore();
        return 0;
    }

    /// <summary>Releases the framebuffer block reference.</summary>
    [Hle]
    int sceCompatUninit()
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        _fbUid = 0;
        return 0;
    }

    /// <summary>The state: 0 initialized, 2 running, 1 stopped.</summary>
    [Hle]
    int sceCompatGetStatus()
    {
        return Allowed ? _status : ERR_PERM;
    }

    /// <summary>(int resume): 1 resumes a suspended compat core; nothing was suspended: 0x8001008D.</summary>
    [Hle]
    int sceCompatSuspendResume(int resume)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        return resume == 1 && _status == 1 ? ERR_NO_RESUME : 0;
    }

    // ---- the core's interrupts on the ARM (they run on the core's thread, as interrupt handlers)

    void ArmInterrupt(int line)
    {
        if (line >= CommandLine && line < CommandLine + Slots)
        {
            CommandPosted(line - CommandLine);
        }
        else if (line >= AnswerLine && line < AnswerLine + Slots)
        {
            _intrSemaphores[line - AnswerLine]?.Signal(1);
        }
    }

    /// <summary>IRQ 0x44 + slot ("SceCompat0c".."2c"): the core posted a command in its slot of the shared SRAM. Its
    /// address goes into the channel's ring and the slot is acknowledged (the core may post the next); a full ring
    /// keeps the entry and sets the overflow flag instead. The channel's waiter is woken either way.</summary>
    void CommandPosted(int slot)
    {
        uint header = _sram.Read32(CompatSharedSram.CommandEntries + slot * 8);
        uint channel = header >> 16;
        if (channel >= Channels)
        {
            return;
        }

        lock (_ringLock)
        {
            RequestRing ring = _rings[channel];
            if (ring.Count < RingEntries)
            {
                ring.Entries[ring.Write] = _sram.Read32(CompatSharedSram.CommandEntries + slot * 8 + 4);
                ring.Write = (ring.Write + 1) & (RingEntries - 1);
                ring.Count++;
                _mailbox.ArmAcknowledge(CommandLine + slot);
                _mailbox.ArmRing(CommandLine + slot);
            }
            else
            {
                ring.Overflow = true;
            }
        }

        _requestSemaphores[channel]?.Signal(1);
    }

    void PspInterruptLevel(bool high)
    {
        _runner?.SetInterruptLevel(high);
    }

    // ---- the core's requests and answers

    /// <summary>(int channel &lt; 16, int slot &lt; 3): waits for the next request the core posts on the channel and
    /// returns its address word &gt;&gt; 1 (the low 26 bits of the word locate the 0x80-byte request in the window);
    /// an empty ring 0x80010016. When the ring had overflowed, the core's entry still in the slot goes in and the slot
    /// is acknowledged; as in the 3.74 module, that entry is not added to the ring's count.</summary>
    [Hle]
    int sceCompatWaitAndGetRequest(uint channel, uint slot)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if (channel >= Channels || slot >= Slots)
        {
            return ERR_ARG;
        }

        CreateSemaphores();
        int waited = Tm.sceKernelWaitSemaThroughEntry(_requestSema[channel], 1, new Ptr<uint>(0));
        if (waited < 0)
        {
            return waited;
        }

        lock (_apiLock)
        {
            lock (_ringLock)
            {
                RequestRing ring = _rings[channel];
                uint entry = ring.Entries[ring.Read];
                ring.Read = (ring.Read + 1) & (RingEntries - 1);
                if (ring.Count == 0)
                {
                    return ERR_INVAL;
                }

                ring.Count--;
                if (ring.Overflow)
                {
                    ring.Entries[ring.Write] = _sram.Read32(CompatSharedSram.CommandEntries + (int)slot * 8 + 4);
                    ring.Write = (ring.Write + 1) & (RingEntries - 1);
                    _mailbox.ArmAcknowledge(CommandLine + (int)slot);
                    _mailbox.ArmRing(CommandLine + (int)slot);
                    ring.Overflow = false;
                }

                return (int)(entry >> 1);
            }
        }
    }

    /// <summary>(int which 1..3): the core's special handshakes on the word at window + 0x3D78000. 1 (boot): waits up to
    /// 5 s of the core's time for state 1, runs the secure processor's command 0x30006 (status 0), marks the core
    /// ready, acknowledges every mailbox line but 1 and 0xE, writes 2 and waits up to 5 s for 3, then writes 0.
    /// 2 and 3 (suspend, resume): wait up to 30 s for state 1, then write 0. A timeout is 0x80028005; the owner gone
    /// 0x80028007. Others: 0.</summary>
    [Hle]
    int sceCompatWaitSpecialRequest(int which)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if (which is < 1 or > 3)
        {
            return 0;
        }

        ulong limit = which == 1 ? HandshakeTimeoutUs : SuspendTimeoutUs;
        int reached = WaitHandshake(HandshakeReady, limit);
        if (reached < 0)
        {
            return reached;
        }

        if (which != 1)
        {
            WriteHandshake(HandshakeIdle);
            return 0;
        }

        lock (_apiLock)
        {
            _coreReady = HandshakeReady;
        }

        for (int line = 0; line < CompatMailbox.Lines; line++)
        {
            if (line != HandshakeKeptLine1 && line != HandshakeKeptLine2)
            {
                _mailbox.ArmAcknowledge(line);
            }
        }

        WriteHandshake(HandshakeAcknowledged);
        reached = WaitHandshake(HandshakeRunning, HandshakeTimeoutUs);
        if (reached < 0)
        {
            return reached;
        }

        WriteHandshake(HandshakeIdle);
        return 0;
    }

    uint ReadHandshake()
    {
        return _window == 0 ? 0 : GuestMemory.R32(_window + HandshakeOffset);
    }

    void WriteHandshake(uint state)
    {
        if (_window != 0)
        {
            GuestMemory.W32(_window + HandshakeOffset, state);
        }
    }

    /// <summary>Waits for the handshake word to reach <paramref name="state"/>: 0, the timeout or the owner gone. The
    /// limit is the core's time (it does not run while no core does: the ARM's guest time then).</summary>
    int WaitHandshake(uint state, ulong limitUs)
    {
        if (ReadHandshake() == state)
        {
            return 0;
        }

        CompatCoreRunner? runner = _runner;
        if (runner == null)
        {
            Tm.sceKernelDelayThreadThroughEntry((uint)limitUs);
            return ReadHandshake() == state ? 0 : ERR_TIMEOUT;
        }

        bool reached = runner.WaitUntil(() => ReadHandshake() == state, limitUs, () => _owner < 0);
        if (reached)
        {
            return 0;
        }

        return _owner < 0 ? ERR_OWNER_GONE : ERR_TIMEOUT;
    }

    /// <summary>(int which &lt; 3): waits for an answer interrupt of the core (IRQ 0x47 + which).</summary>
    [Hle]
    int sceCompatWaitIntr(uint which)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if (which >= Slots)
        {
            return ERR_ARG;
        }

        CreateSemaphores();
        return Tm.sceKernelWaitSemaThroughEntry(_intrSema[which], 1, new Ptr<uint>(0));
    }

    /// <summary>(int line 1..15): a virtual interrupt to the core: waits up to 1 s of the core's time for the line's
    /// slot in the shared SRAM to be 0, writes 1 and rings mailbox line 0x10 + line. No core ready: 0x80010086.</summary>
    [Hle]
    int sceCompatInterrupt(int line)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if ((uint)(line - 1) >= 15)
        {
            return ERR_INVAL;
        }

        lock (_apiLock)
        {
            if (_coreReady == 0)
            {
                return ERR_NO_CORE;
            }

            int slot = CompatSharedSram.VirtualInterrupts + line * 8;
            _runner?.WaitUntil(() => _sram.Read32(slot) == 0, VirtualSlotTimeoutUs, () => false);
            _sram.Write32(slot, 1);
            _mailbox.ArmAcknowledge(VirtualLine + line);
            _mailbox.ArmRing(VirtualLine + line);
            return 0;
        }
    }

    /// <summary>(int slot &lt; 3, const u32 info[2], u64 value): the answer to the core's request in the slot: the
    /// shared SRAM block 0x840 + slot * 0x20 gets { value, info[0], info[1] } as 64-bit words, then mailbox line
    /// 7 + slot rings. No core ready: 0x80027205.</summary>
    [Hle]
    int sceCompatReturnValueEx(uint slot, uint info, ulong value)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        if (slot >= Slots)
        {
            return ERR_ARG;
        }

        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        uint first = GuestMemory.R32(info);
        uint second = GuestMemory.R32(info + 4);
        lock (_apiLock)
        {
            if (_coreReady == 0)
            {
                return ERR_NOT_STARTED;
            }

            int block = CompatSharedSram.AnswerBlocks + (int)slot * CompatSharedSram.AnswerBlockSize;
            _sram.Write32(block, (uint)value);
            _sram.Write32(block + 4, (uint)(value >> 32));
            _sram.Write32(block + 8, first);
            _sram.Write32(block + 12, 0);
            _sram.Write32(block + 16, second);
            _sram.Write32(block + 20, 0);
            _mailbox.ArmAcknowledge(AnswerLine + (int)slot);
            _mailbox.ArmRing(AnswerLine + (int)slot);
            return 0;
        }
    }

    /// <summary>The shared SRAM offset of sceCompatReadShared32's register (0 0x100, 1 0x170, 2 0x17C, 4 0x10C,
    /// 5 0x110, 6 0x114, 7 0x188), -1 for the others.</summary>
    static int ReadRegisterOffset(int register)
    {
        return register switch
        {
            0 => 0x100,
            1 => 0x170,
            2 => 0x17C,
            4 => 0x10C,
            5 => 0x110,
            6 => 0x114,
            7 => 0x188,
            _ => -1,
        };
    }

    /// <summary>The shared SRAM offset of sceCompatWriteShared32's register (0x80..0x82 0x100..0x108, 0x83..0x88
    /// 0x170..0x184), -1 for the others.</summary>
    static int WriteRegisterOffset(int register)
    {
        if (register is >= 0x80 and <= 0x82)
        {
            return 0x100 + (register - 0x80) * 4;
        }

        if (register is >= 0x83 and <= 0x88)
        {
            return 0x170 + (register - 0x83) * 4;
        }

        return -1;
    }

    /// <summary>(int reg 0..7 but 3, u32 *value): a shared register the core writes. No core ready: 0x80027205.</summary>
    [Hle]
    int sceCompatReadShared32(int reg, uint value)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        lock (_apiLock)
        {
            if (_coreReady == 0)
            {
                return ERR_NOT_STARTED;
            }

            int offset = ReadRegisterOffset(reg);
            if (offset < 0)
            {
                return ERR_ARG;
            }

            GuestMemory.W32(value, _sram.Read32(offset));
            return 0;
        }
    }

    /// <summary>(int reg 0x80..0x88, u32 value): a shared register for the core. No core ready: 0x80027205.</summary>
    [Hle]
    int sceCompatWriteShared32(int reg, uint value)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        lock (_apiLock)
        {
            if (_coreReady == 0)
            {
                return ERR_NOT_STARTED;
            }

            int offset = WriteRegisterOffset(reg);
            if (offset < 0)
            {
                return ERR_ARG;
            }

            _sram.Write32(offset, value);
            return 0;
        }
    }

    /// <summary>(const u32 ctrl[3]): the controller state for the core: the three words at 0x120..0x128, then the
    /// first again at 0x140 (written last: the core sees new data when 0x140 changes). No core ready: 0x80027205.</summary>
    [Hle]
    int sceCompatWriteSharedCtrl(uint ctrl)
    {
        if (!Allowed)
        {
            return ERR_PERM;
        }

        lock (_apiLock)
        {
            if (_coreReady == 0)
            {
                return ERR_NOT_STARTED;
            }

            uint first = GuestMemory.R32(ctrl);
            _sram.Write32(CompatSharedSram.ControllerWords, first);
            _sram.Write32(CompatSharedSram.ControllerWords + 4, GuestMemory.R32(ctrl + 4));
            _sram.Write32(CompatSharedSram.ControllerWords + 8, GuestMemory.R32(ctrl + 8));
            _sram.Write32(CompatSharedSram.ControllerMark, first);
            return 0;
        }
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
        return Emu.Hle.Modules.OfType<Display>().First().SetFrameBufThroughEntry(_fbParam, 1);
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
        _rif = id.IndexOf((byte)0) >= 0 ? "" : Io.FirmwareLayout.PspLicenses.Guest(System.Text.Encoding.ASCII.GetString(id) + ".rif");
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
        DeviceCapacity.WriteDevInfo(info, DeviceCapacity.DevInfoSize, Emu.Vfs.Resolve("ux0:"));
        return 0;
    }

    /// <summary>Whether the PocketStation application is licensed (its rif under ux0:pspemu/PSP/LICENSE).</summary>
    [Hle]
    int sceCompatCheckPocketStation()
    {
        if (!Allowed) return ERR_PERM;
        string? host = Emu.Vfs.Resolve(Io.FirmwareLayout.PspLicenses.Guest("JA0003-PCSC80018_00-POCKETSTATION001.rif"));
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
