using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceNetInternal's runtime helpers as libnet.suprx 3.74 has them (read in its code, study only): its allocator, its
// thread and delay wrappers, its two sync objects, the process time, its random numbers (SFMT19937), its debug print,
// the interface names and the per-thread network event handlers. The emulated stack never posts an event (no network
// interface), so a handler is registered and polled but never called.
public sealed partial class Net
{
    const string LibnetThreadEntryName = "SceNet:libnet thread entry";

    /// <summary>libnet's allocations: the size of each block (its free poisons the block with 0xDD first).</summary>
    [SnapshotMissing]
    readonly Dictionary<uint, int> _libnetBlocks = new();

    /// <summary>The "SceNetDhcpSync" sync object { event flag, bits } NID_F6291476 hands out (libnet's .data).</summary>
    [SnapshotMissing]
    uint _dhcpSync;

    /// <summary>The thread entry of NID_225C4801's threads (host code; every session registers it).</summary>
    [SnapshotMissing]
    uint _libnetThreadEntry;

    /// <summary>libnet's SFMT19937 state (624 words) and the index of the next word; seeded on first use.</summary>
    [SnapshotMissing]
    readonly uint[] _randomState = new uint[624];

    [SnapshotMissing]
    int _randomIndex;

    [SnapshotMissing]
    bool _randomSeeded;

    /// <summary>One network event handler (NID_EAC33599): a thread's callback for an interface (-1 the stack).</summary>
    [GenerateSnapshot]
    sealed partial class LibnetEventHandler
    {
        public int Thread, IfIndex;
        public uint Callback, Argument;

        /// <summary>The kernel target the handler is attached to; -2 while the interface does not exist yet.</summary>
        public int Target;

        /// <summary>The last event's value (NID_B01ABD0A reads it) and the argument of the last poll (NID_04E6136D),
        /// which the handler's callback would get.</summary>
        public uint LastValue, PollArgument;
    }

    [SnapshotMissing]
    readonly List<LibnetEventHandler> _eventHandlers = new();

    ThreadMgr? _threadManager;
    ThreadMgr Threads => _threadManager ??= Emu.Hle.Modules.OfType<ThreadMgr>().First();

    public override void Initialize()
    {
        Emu.Hle.RegisterHostFunction(LibnetThreadEntryName, LibnetThreadEntry);
    }

    static int CurrentThreadUid => GuestThread.Current?.Uid ?? -1;

    /// <summary>libnet's result: errno set for an error only.</summary>
    int NetResult(int result)
    {
        return result < 0 ? NetErr(result) : result;
    }

    // ------------------------------------------------------------------ allocator

    /// <summary>NID_A2C2BC5F (int size, uint flags): libnet's allocator; flags bit 1 zeroes the block. NULL for a size
    /// below 1 or before sceNetInit (the pool comes with it).</summary>
    [Hle("NID_A2C2BC5F", Nid = 0xA2C2BC5F, Library = "SceNetInternal")]
    uint NetInternalAlloc(int size, uint flags)
    {
        if (!Initialized)
        {
            return 0;
        }

        return LibnetAlloc(size, (flags & 2) != 0);
    }

    /// <summary>NID_E62A2FAA (void *block): frees a libnet block (NULL: nothing), poisoning it with 0xDD.</summary>
    [Hle("NID_E62A2FAA", Nid = 0xE62A2FAA, Library = "SceNetInternal")]
    void NetInternalFree(uint block)
    {
        LibnetFree(block);
    }

    /// <summary>NID_CF49E9C4 (route *routes): frees what NID_5B0E370C handed out; needs sceNetInit; 0.</summary>
    [Hle("NID_CF49E9C4", Nid = 0xCF49E9C4, Library = "SceNetInternal")]
    int NetInternalFreeRoutes(uint routes)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        LibnetFree(routes);
        return 0;
    }

    // ------------------------------------------------------------------ threads and time

    /// <summary>NID_104E4820 (int usec): sceKernelDelayThread; a negative delay is EINVAL.</summary>
    [Hle("NID_104E4820", Nid = 0x104E4820, Library = "SceNetInternal", Cost = HleCost.SchedulerWait)]
    int NetInternalDelay(int usec)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (usec < 0)
        {
            return NetErr(EINVAL);
        }

        return Threads.sceKernelDelayThreadThroughEntry((uint)usec);
    }

    /// <summary>
    /// NID_225C4801 (SceUID *thread, const attr *attr, const char *name): a libnet thread. attr is { entry, argument,
    /// priority, stack size, flags } (0x14 bytes, NULL is EINVAL): the thread is created with the priority and stack
    /// size, started with a copy of attr, runs entry(argument) and, with flags bit 0, exits right after. The thread's
    /// id is stored only once it started; the kernel's error otherwise.
    /// </summary>
    [Hle("NID_225C4801", Nid = 0x225C4801, Library = "SceNetInternal", Cost = HleCost.SchedulerWait)]
    int NetInternalCreateThread(uint thread, uint attr, string name)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (attr == 0)
        {
            return NetErr(EINVAL);
        }

        int uid = Threads.sceKernelCreateThread(name, LibnetThreadEntryStub(), (int)GuestMemory.R32(attr + 8), (int)GuestMemory.R32(attr + 12), 0, 0, 0);
        if (uid < 0)
        {
            return NetErr(uid);
        }

        int started = Threads.sceKernelStartThreadThroughEntry(uid, 0x14, attr);
        if (started >= 0)
        {
            GuestMemory.W32(thread, (uint)uid);
        }

        return NetResult(started);
    }

    uint LibnetThreadEntryStub()
    {
        if (_libnetThreadEntry != 0)
        {
            return _libnetThreadEntry;
        }

        IReadOnlyList<HleFunction> functions = Emu.Hle.Functions;
        int svc = -1;
        for (int i = functions.Count - 1; i > 0 && svc < 0; i--)
        {
            if (functions[i].Nid == 0 && functions[i].Library == "host" && functions[i].Name == LibnetThreadEntryName)
            {
                svc = i;
            }
        }

        if (svc < 0)
        {
            svc = (int)Emu.Hle.RegisterHostFunction(LibnetThreadEntryName, LibnetThreadEntry);
        }

        uint stub = Emu.Memory.Alloc(0x10, "libnet thread entry");
        Loader.Linker.WriteHleStub(stub, (uint)svc, 0);
        Emu.Code.Invalidate(stub, 0x10);
        return _libnetThreadEntry = stub;
    }

    /// <summary>The thread's entry (argument size, the attr copy): entry(argument), then the exit when flags bit 0.</summary>
    void LibnetThreadEntry(HleContext context)
    {
        uint attr = context.Gpr(1);
        context.Thread.CallGuest(GuestMemory.R32(attr), GuestMemory.R32(attr + 4));
        if ((GuestMemory.R32(attr + 16) & 1) != 0)
        {
            throw new GuestThreadExitException(0, false);
        }

        context.Return(0);
    }

    /// <summary>NID_3B5F94B7 (SceUID thread): waits for the thread's end and deletes it; the exit status, or the wait's
    /// error.</summary>
    [Hle("NID_3B5F94B7", Nid = 0x3B5F94B7, Library = "SceNetInternal", Cost = HleCost.SchedulerWait)]
    int NetInternalJoinThread(int thread)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        uint status = Emu.Memory.Alloc(4, "libnet thread status", MemoryRegion.User, 4);
        try
        {
            int result = Threads.sceKernelWaitThreadEndThroughEntry(thread, new Ptr<int>(status), new Ptr<uint>(0));
            int exitStatus = result < 0 ? result : (int)GuestMemory.R32(status);
            Threads.sceKernelDeleteThreadThroughEntry(thread);
            return NetResult(exitStatus);
        }
        finally
        {
            Emu.Memory.Free(status);
        }
    }

    /// <summary>NID_4EF96CD8 (): sceKernelExitThread(0).</summary>
    [Hle("NID_4EF96CD8", Nid = 0x4EF96CD8, Library = "SceNetInternal", Cost = HleCost.SchedulerWait)]
    void NetInternalExitThread()
    {
        throw new GuestThreadExitException(0, false);
    }

    /// <summary>NID_2FEC8B74 (uint64 *time): the process time (sceKernelGetProcessTimeWide); NULL: nothing.</summary>
    [Hle("NID_2FEC8B74", Nid = 0x2FEC8B74, Library = "SceNetInternal")]
    void NetInternalProcessTime(uint time)
    {
        if (time == 0)
        {
            return;
        }

        GuestMemory.Write(time, Threads.sceKernelGetProcessTimeWideThroughEntry());
    }

    // ------------------------------------------------------------------ sync objects

    /// <summary>NID_F6291476 (int which): libnet's "SceNetDhcpSync" object for 0, NULL otherwise.</summary>
    [Hle("NID_F6291476", Nid = 0xF6291476, Library = "SceNetInternal")]
    uint NetInternalDhcpSync(int which)
    {
        if (which != 0)
        {
            return 0;
        }

        lock (_netLock)
        {
            if (_dhcpSync == 0)
            {
                _dhcpSync = Emu.Memory.Alloc(0x20, "libnet SceNetDhcpSync", MemoryRegion.User, 16);
                GuestMemory.Fill(_dhcpSync, 0, 0x20);
            }

            if (GuestMemory.R32(_dhcpSync) == 0 && _netInited)
            {
                int flag = Threads.sceKernelCreateEventFlag("SceNetDhcpSync", 0x1000, 1, 0);
                if (flag >= 0)
                {
                    GuestMemory.W32(_dhcpSync, (uint)flag);
                    GuestMemory.W32(_dhcpSync + 4, 1);
                }
            }

            return _dhcpSync;
        }
    }

    /// <summary>NID_C2FF615E (sync *sync, uint *timeout): waits for the sync object's event flag (bit 0, cleared) and
    /// takes its bits; on a timeout *timeout = -1 and 0; another error 0.</summary>
    [Hle("NID_C2FF615E", Nid = 0xC2FF615E, Library = "SceNetInternal", Cost = HleCost.SchedulerWait)]
    uint NetInternalSyncWait(uint sync, uint timeout)
    {
        int result = Threads.sceKernelWaitEventFlagThroughEntry((int)GuestMemory.R32(sync), 1, 5, new Ptr<uint>(0), new Ptr<uint>(timeout));
        if (result < 0)
        {
            if (result == unchecked((int)0x80028005))
            {
                GuestMemory.W32(timeout, 0xFFFFFFFF);
            }

            return 0;
        }

        uint bits = GuestMemory.R32(sync + 4);
        GuestMemory.W32(sync + 4, 0);
        return bits;
    }

    /// <summary>NID_9D12D8A4 (sync *sync, uint bits): adds the bits and sets the event flag's bit 0; needs sceNetInit.</summary>
    [Hle("NID_9D12D8A4", Nid = 0x9D12D8A4, Library = "SceNetInternal", Cost = HleCost.SchedulerWait)]
    int NetInternalSyncSignal(uint sync, uint bits)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        GuestMemory.W32(sync + 4, GuestMemory.R32(sync + 4) | bits);
        return NetResult(Threads.sceKernelSetEventFlagThroughEntry((int)GuestMemory.R32(sync), 1));
    }

    // ------------------------------------------------------------------ random numbers, names, print

    /// <summary>NID_689B9D7D (uint *value): the next word of libnet's SFMT19937 generator; NULL is EINVAL.</summary>
    [Hle("NID_689B9D7D", Nid = 0x689B9D7D, Library = "SceNetInternal")]
    int NetInternalRandom(uint value)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (value == 0)
        {
            return NetErr(EINVAL);
        }

        lock (_netLock)
        {
            GuestMemory.W32(value, NextRandom());
        }

        return 0;
    }

    /// <summary>NID_F3917021 (uint index): libnet's interface name (IfNames); an index of 10 or more sets errno EINVAL
    /// and gives "".</summary>
    [Hle("NID_F3917021", Nid = 0xF3917021, Library = "SceNetInternal")]
    uint NetInternalInterfaceName(uint index)
    {
        if (index < IfNames.Length)
        {
            return InterfaceNameString(IfNames[index]);
        }

        NetErr(EINVAL);
        return InterfaceNameString("");
    }

    [SnapshotMissing]
    readonly Dictionary<string, uint> _interfaceNameStrings = new();

    uint InterfaceNameString(string name)
    {
        lock (_netLock)
        {
            if (_interfaceNameStrings.TryGetValue(name, out uint text))
            {
                return text;
            }

            text = Emu.Memory.Alloc((uint)name.Length + 1, "libnet interface name", MemoryRegion.User, 4);
            GuestMemory.WriteCString(text, name, name.Length + 1);
            _interfaceNameStrings[name] = text;
            return text;
        }
    }

    /// <summary>NID_694F8996 (const char *format, ...): libnet's debug print through the callback NID_A64CFBF5 set,
    /// one character at a time (callback(0, c)); without a callback 0. The count of characters.</summary>
    [Hle("NID_694F8996", Nid = 0x694F8996, Library = "SceNetInternal")]
    int NetInternalDebugPrint(string format, VarArgs args)
    {
        uint callback = _libnetWord; // the debug print callback NID_A64CFBF5 sets
        if (callback == 0)
        {
            return 0;
        }

        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        string text = CFormat.Format(format, args);
        foreach (char character in text)
        {
            GuestThread.Current!.CallGuest(callback, 0, character);
        }

        return text.Length;
    }

    /// <summary>SFMT19937 (Saito and Matsumoto's SIMD-oriented Fast Mersenne Twister, the published parameters libnet
    /// uses): the next 32-bit word, the state regenerated every 624 words.</summary>
    uint NextRandom()
    {
        if (!_randomSeeded)
        {
            SeedRandom((uint)Threads.sceKernelGetProcessTimeWideThroughEntry());
        }

        if (_randomIndex >= _randomState.Length)
        {
            RegenerateRandom();
            _randomIndex = 0;
        }

        return _randomState[_randomIndex++];
    }

    static readonly uint[] SfmtMask = [0xDFFFFFEF, 0xDDFECB7F, 0xBFFAFFFF, 0xBFFFFFF6];
    static readonly uint[] SfmtParity = [0x00000001, 0x00000000, 0x00000000, 0x13C9E684];

    void SeedRandom(uint seed)
    {
        _randomState[0] = seed;
        for (int i = 1; i < _randomState.Length; i++)
        {
            _randomState[i] = (uint)((_randomState[i - 1] ^ (_randomState[i - 1] >> 30)) * 1812433253u + (uint)i);
        }

        uint inner = 0;
        for (int i = 0; i < 4; i++)
        {
            inner ^= _randomState[i] & SfmtParity[i];
        }

        for (int shift = 16; shift > 0; shift >>= 1)
        {
            inner ^= inner >> shift;
        }

        if ((inner & 1) == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if (SfmtParity[i] != 0)
                {
                    _randomState[i] ^= SfmtParity[i] & (~SfmtParity[i] + 1);
                    break;
                }
            }
        }

        _randomIndex = _randomState.Length;
        _randomSeeded = true;
    }

    void RegenerateRandom()
    {
        const int Blocks = 156, Pos1 = 122;
        int r1 = Blocks - 2;
        int r2 = Blocks - 1;
        Span<uint> result = stackalloc uint[4];
        for (int i = 0; i < Blocks; i++)
        {
            int b = (i + Pos1) % Blocks;
            SfmtRecursion(result, i, b, r1, r2);
            result.CopyTo(_randomState.AsSpan(i * 4, 4));
            r1 = r2;
            r2 = i;
        }
    }

    /// <summary>One SFMT step: a ^ (a &lt;&lt;128 8) ^ ((b &gt;&gt;32 11) &amp; mask) ^ (c &gt;&gt;128 8) ^ (d &lt;&lt;32 18),
    /// the 128-bit blocks by index.</summary>
    void SfmtRecursion(Span<uint> result, int a, int b, int c, int d)
    {
        ReadOnlySpan<uint> state = _randomState;
        ulong aHigh = ((ulong)state[a * 4 + 3] << 32) | state[a * 4 + 2];
        ulong aLow = ((ulong)state[a * 4 + 1] << 32) | state[a * 4];
        ulong xHigh = (aHigh << 8) | (aLow >> 56);
        ulong xLow = aLow << 8;
        ulong cHigh = ((ulong)state[c * 4 + 3] << 32) | state[c * 4 + 2];
        ulong cLow = ((ulong)state[c * 4 + 1] << 32) | state[c * 4];
        ulong yHigh = cHigh >> 8;
        ulong yLow = (cLow >> 8) | (cHigh << 56);
        Span<uint> x = [(uint)xLow, (uint)(xLow >> 32), (uint)xHigh, (uint)(xHigh >> 32)];
        Span<uint> y = [(uint)yLow, (uint)(yLow >> 32), (uint)yHigh, (uint)(yHigh >> 32)];
        for (int i = 0; i < 4; i++)
        {
            result[i] = state[a * 4 + i] ^ x[i] ^ ((state[b * 4 + i] >> 11) & SfmtMask[i]) ^ y[i] ^ (state[d * 4 + i] << 18);
        }
    }

    // ------------------------------------------------------------------ network event handlers

    /// <summary>
    /// NID_EAC33599 (int ifIndex, callback, void *argument, int reserved): registers the calling thread's network
    /// event handler for the interface (-1 the stack). A NULL callback or reserved not 0: EINVAL; the thread already
    /// has one for the interface, or four threads do: EEXIST. A handler for an interface the kernel does not list
    /// waits (0) until it appears; otherwise libnet attaches an event callback to the stack (command 0x20000003).
    /// </summary>
    [Hle("NID_EAC33599", Nid = 0xEAC33599, Library = "SceNetInternal")]
    int NetInternalAddEventHandler(int ifIndex, uint callback, uint argument, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved != 0 || callback == 0)
        {
            return NetErr(EINVAL);
        }

        int thread = CurrentThreadUid;
        lock (_netLock)
        {
            int sameInterface = 0;
            foreach (LibnetEventHandler existing in _eventHandlers)
            {
                if (existing.IfIndex == ifIndex)
                {
                    sameInterface++;
                    if (existing.Thread == thread || sameInterface > 3)
                    {
                        return NetErr(EEXIST);
                    }
                }
            }

            var handler = new LibnetEventHandler { Thread = thread, IfIndex = ifIndex, Callback = callback, Argument = argument, Target = -2 };
            _eventHandlers.Add(handler);
            int target = ifIndex == -1 ? -1 : LibnetKernelIndex(ifIndex);
            if (target < -1)
            {
                return 0;
            }

            uint block = ControlBlock();
            GuestMemory.W32(block + 4, 1);
            int result = LibnetControl(target, 0x20000003, block, 8);
            if (result < 0)
            {
                _eventHandlers.Remove(handler);
                return result;
            }

            handler.Target = target;
            return 0;
        }
    }

    /// <summary>NID_EDAA3453 (int ifIndex, int reserved): removes the calling thread's handler for the interface
    /// (detached from the stack with command 0x20000007 when it was attached); none or reserved not 0: EINVAL.</summary>
    [Hle("NID_EDAA3453", Nid = 0xEDAA3453, Library = "SceNetInternal")]
    int NetInternalRemoveEventHandler(int ifIndex, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        int thread = CurrentThreadUid;
        lock (_netLock)
        {
            LibnetEventHandler? handler = reserved == 0 ? FindEventHandler(thread, ifIndex) : null;
            if (handler == null)
            {
                return NetErr(EINVAL);
            }

            _eventHandlers.Remove(handler);
            if (handler.Target == -2)
            {
                return 0;
            }

            return LibnetControl(handler.Target, 0x20000007, ControlBlock(), 8);
        }
    }

    /// <summary>NID_B01ABD0A (int ifIndex, uint *value, int reserved): the last event value of the calling thread's
    /// handler for the interface; none, value NULL or reserved not 0: EINVAL.</summary>
    [Hle("NID_B01ABD0A", Nid = 0xB01ABD0A, Library = "SceNetInternal")]
    int NetInternalEventValue(int ifIndex, uint value, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        lock (_netLock)
        {
            LibnetEventHandler? handler = reserved == 0 && value != 0 ? FindEventHandler(CurrentThreadUid, ifIndex) : null;
            if (handler == null)
            {
                return NetErr(EINVAL);
            }

            GuestMemory.W32(value, handler.LastValue);
            return 0;
        }
    }

    /// <summary>
    /// NID_04E6136D (int timeoutUs, uint value, int reserved): polls the calling thread's handlers (each keeps value
    /// for its callback): none is ENOENT. Every attached handler asks the stack for its event (command
    /// 0x10000002), which never has one here (0x80410159); then, with a timeout, the thread waits it out running its
    /// callbacks (sceKernelDelayThreadCB) and gets 0.
    /// </summary>
    [Hle("NID_04E6136D", Nid = 0x04E6136D, Library = "SceNetInternal", Cost = HleCost.SchedulerWait, CallbackWait = true)]
    int NetInternalPollEvents(int timeoutUs, uint value, int reserved)
    {
        if (!Initialized)
        {
            return NetErr(ENOTINIT);
        }

        if (reserved != 0)
        {
            return NetErr(EINVAL);
        }

        int thread = CurrentThreadUid;
        bool polled = false;
        lock (_netLock)
        {
            foreach (LibnetEventHandler handler in _eventHandlers)
            {
                if (handler.Thread == thread && handler.Target != -2)
                {
                    handler.PollArgument = value;
                    polled = true;
                }
            }
        }

        if (!polled)
        {
            return NetErr(ENOENT);
        }

        if (timeoutUs == 0)
        {
            return NetErr(LIBNET_IF_NOT_FOUND);
        }

        if (timeoutUs < 0)
        {
            return NetErr(EINVAL);
        }

        return Threads.sceKernelDelayThreadThroughEntry((uint)timeoutUs);
    }

    LibnetEventHandler? FindEventHandler(int thread, int ifIndex)
    {
        foreach (LibnetEventHandler handler in _eventHandlers)
        {
            if (handler.Thread == thread && handler.IfIndex == ifIndex)
            {
                return handler;
            }
        }

        return null;
    }
}
