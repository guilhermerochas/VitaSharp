using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceCodecEngineWrapperForDriver (os0:bootimage SceCodecEngineWrapper 3.74, read in the module, study only): the
// kernel side of the codec engine (the Toshiba media processor that runs the decoders' firmware), which SceAvcodec,
// SceAudioin and SceNgs call. The entries fall in five groups:
//  - per-process bookkeeping the module keeps in its own memory: the use count every user entry takes and drops, the
//    codec instance's settings (mode, level, flag word, work size), the 1 MiB-aligned window bases, the callbacks;
//  - the unmap memblocks a process opened with sceCodecEngineOpenUnmapMemBlock (the same table as the user side):
//    whether a range lies in one of them and the translation between its views (ARM virtual +8, physical +0xC,
//    codec engine +0x10). The emulated memory is flat, so the three views of an address are the address itself;
//  - transfer lists (the entries SceAvcodec builds for a decode): appending and flagging entries is plain memory work;
//  - a DMA copy between two ARM ranges (SceDmacmgr's channel): a copy here;
//  - what needs the engine itself: the RPCs to its firmware, the engine's memory windows (mapped when its firmware
//    boots), the bounce buffers in them, the performance counters and the ops table behind the codec instance. The
//    engine is not emulated (the HLE decoders do its work through FFmpeg at the user level): the windows map nothing
//    (a translation is 0, as the module answers for an address outside them), an RPC is taken (0), the counters are
//    0 and the ops entries answer 0x80620108 (no instance to run on), as the module does without its instance. Those
//    are defaults.
public sealed partial class CodecEngine
{
    const int CE_ERR_NOT_FOUND = unchecked((int)0x80600000);
    const int CE_ERR_INVALID_SIZE = unchecked((int)0x80600001);
    const int CE_ERR_INVALID_ADDRESS = unchecked((int)0x80600002);
    const int CE_ERR_SLOTS_FULL = unchecked((int)0x80600007);
    const int CE_ERR_INSTANCE_VALUE = unchecked((int)0x80620101);
    const int CE_ERR_INSTANCE_BUSY = unchecked((int)0x80620106);
    const int CE_ERR_NO_INSTANCE = unchecked((int)0x80620108);
    const int CE_ERR_NOT_DECODER_MODE = unchecked((int)0x8001005F);
    const int CE_ERR_DMA_ARGUMENT = unchecked((int)0x80010016);

    /// <summary>The callback tables' slots (two tables of four).</summary>
    const int CallbackSlots = 4;

    /// <summary>One entry of a transfer list: 0x20 bytes from +0xC of the list (+4 holds the entry count).</summary>
    const uint ListEntries = 0xC, ListEntrySize = 0x20;

    readonly object _kernelLock = new();

    /// <summary>Per process: how many codec engine calls are running (the module refuses to suspend while above 0).</summary>
    [SnapshotMissing] // states of builds before it: no call running
    readonly Dictionary<int, int> _useCounts = new();

    /// <summary>Per process: suspend requests that found a call running.</summary>
    [SnapshotMissing]
    readonly Dictionary<int, int> _busySuspends = new();

    /// <summary>Per process: the codec instance's settings (mode, level, flag word, work size).</summary>
    [SnapshotMissing] // states of builds before it: every instance as created
    readonly Dictionary<int, Instance> _instances = new();

    /// <summary>The window bases 0x18197103 stores: inside 0x20000000-0x2FFFFFFF, and elsewhere.</summary>
    [SnapshotMissing]
    uint _windowLow, _windowHigh;

    /// <summary>The two four-slot callback tables (EA88ABE5 and 0E38B133).</summary>
    [SnapshotMissing]
    readonly uint[] _callbacksA = new uint[CallbackSlots], _callbacksB = new uint[CallbackSlots];

    [GenerateSnapshot]
    sealed partial class Instance
    {
        /// <summary>+4: 0 or 1 (the decoder mode; 0x86861877 sets it).</summary>
        public uint Mode;

        /// <summary>+0xC: 1-6 (0x2B7CC150).</summary>
        public uint Level;

        /// <summary>+0: the flag word (0x62996F94).</summary>
        public uint Flags;

        /// <summary>+0x788: a work size of at most 0x1000, in 8-byte units (0x54F39D1D).</summary>
        public uint WorkSize;

        /// <summary>+0x5F4: the mode-0 notification callbacks, four words each (pid, callback, 0, flags).</summary>
        public uint[] Notifications = new uint[NotificationSlots * 4];
    }

    static int Pid(int pid)
    {
        return pid == 0 ? ProcessScope.CurrentPid : pid;
    }

    Instance InstanceOf(int pid)
    {
        int key = Pid(pid);
        if (!_instances.TryGetValue(key, out Instance? instance))
        {
            instance = new Instance();
            _instances[key] = instance;
        }

        return instance;
    }

    /// <summary>The settings of a process's codec instance (tests, debugger).</summary>
    public (uint Mode, uint Level, uint Flags, uint WorkSize) InstanceSettings(int pid)
    {
        lock (_kernelLock)
        {
            Instance instance = InstanceOf(pid);
            return (instance.Mode, instance.Level, instance.Flags, instance.WorkSize);
        }
    }

    /// <summary>The use count of a process (tests, debugger).</summary>
    public int UseCount(int pid)
    {
        lock (_kernelLock)
        {
            return _useCounts.GetValueOrDefault(Pid(pid));
        }
    }

    // ------------------------------------------------------------------ use count

    /// <summary>NID_CB8703D8(): a codec engine call of the current process starts (its use count + 1); 0.</summary>
    [Hle(Nid = 0xCB8703D8, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineUseBegin()
    {
        lock (_kernelLock)
        {
            int pid = Pid(0);
            _useCounts[pid] = _useCounts.GetValueOrDefault(pid) + 1;
            return 0;
        }
    }

    /// <summary>NID_091B0231(): the call ends (use count - 1); 0.</summary>
    [Hle(Nid = 0x091B0231, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineUseEnd()
    {
        lock (_kernelLock)
        {
            int pid = Pid(0);
            _useCounts[pid] = _useCounts.GetValueOrDefault(pid) - 1;
            return 0;
        }
    }

    /// <summary>NID_7492DE05(pid): whether the process can be suspended: 1 with no call running; else 0 (and the
    /// request is counted).</summary>
    [Hle(Nid = 0x7492DE05, Library = "SceCodecEngineWrapperForDriver", ReturnsValue = true)]
    int ksceCodecEngineCanSuspend(int pid)
    {
        lock (_kernelLock)
        {
            int key = Pid(pid);
            if (_useCounts.GetValueOrDefault(key) == 0)
            {
                return 1;
            }

            _busySuspends[key] = _busySuspends.GetValueOrDefault(key) + 1;
            return 0;
        }
    }

    // ------------------------------------------------------------------ the engine itself (defaults)

    /// <summary>NID_391BD4FD and NID_0D3BB164(command, ...): an RPC to the engine's firmware, and the forms that send
    /// one command each (0x1F0, 0x1F1, 0x1F2, 0x1F3, 0x1F5, 0x1F6): no engine program runs the command; taken (0).</summary>
    [Hle(Nid = 0x391BD4FD, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x0D3BB164, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x2DD83BB0, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x3C5E9CC3, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x91535820, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x3A79A935, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x0F235F44, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x26FAF6EC, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    int ksceCodecEngineRpc()
    {
        return 0;
    }

    /// <summary>NID_F9495589(engineAddress) and NID_C7C150AE(armAddress): the translation through the engine's two
    /// memory windows; no window is mapped (no engine firmware booted): 0, the module's answer outside them.</summary>
    [Hle(Nid = 0xF9495589, Library = "SceCodecEngineWrapperForDriver", Default = true, ReturnsValue = true)]
    [Hle(Nid = 0xC7C150AE, Library = "SceCodecEngineWrapperForDriver", Default = true, ReturnsValue = true)]
    uint ksceCodecEngineWindowTranslate(uint address)
    {
        return 0;
    }

    /// <summary>NID_6A1184EA(index): a performance counter the engine reports; none runs: 0.</summary>
    [Hle(Nid = 0x6A1184EA, Library = "SceCodecEngineWrapperForDriver", Default = true, ReturnsValue = true)]
    uint ksceCodecEnginePerformanceCounter(int index)
    {
        return 0;
    }

    /// <summary>The entries of the codec instance's ops table that run on the engine (the open, the decodes and their
    /// queries are RPCs to the engine's firmware): no engine runs here: 0x80620108, the module's answer without an
    /// instance.</summary>
    [Hle(Nid = 0x2B74706E, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x9D3B1425, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0xB280BA55, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x6A3638A8, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x8DEF9631, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0xBB02EB22, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0xB420ABAC, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x30F6AFD4, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0xBB388700, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x972F0D0F, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0xB48B02A3, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x0D140628, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0x8FE370E6, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    int ksceCodecEngineInstanceOp()
    {
        return CE_ERR_NO_INSTANCE;
    }

    // ------------------------------------------------------------------ the instance's ops that run in the module
    //
    // SceCodecEngineWrapper 3.74 dispatches these exports through the ops table of the instance's mode (+4: 0 the
    // table at 0x81017d34, 1 the one at 0x81017d74). Some ops run entirely in the module: the decoder's memory size
    // (op 1), the end-of-stream mark (op 12) and the notification callbacks (op 14). They answer as the module does with
    // no decoder open: the open (op 0) and the decodes are RPCs to the engine's firmware, which is not emulated.

    const int CE_ERR_INSTANCE_POINTER = unchecked((int)0x80620102);
    const int CE_ERR_NOT_OPEN = unchecked((int)0x80620105);

    /// <summary>The notification callbacks an instance keeps in mode 0 (+0x5F4, 0x10 bytes each: pid, callback, 0,
    /// flags).</summary>
    const int NotificationSlots = 6;

    /// <summary>The notification flags the module accepts (any other bit 0x80620101).</summary>
    const uint NotificationFlagsRefused = 0xFFFFC4F9;

    /// <summary>
    /// The decoder's memory (SceCodecEngineWrapper 3.74 0x8100cf70): for <paramref name="parameters"/> {width 64-1920,
    /// height 64-1088, reference frames at most 16 (0 as 1), streams at most <paramref name="maxStreams"/>} a frame and
    /// reference tier (0x140000/0x80000 up to 0x1BD800 bytes of frames and 0x4A400 per frame, 0x400000/0x100000 up to
    /// 0x697800 and 0x151800, 0x700000/0x200000 up to 0xC00000 of frames), plus 0x80000 and the work size's area rounded
    /// to 256 KiB, each per stream; their sum goes to <paramref name="total"/>. NULL 0x80620102, out of range 0x80620101.
    /// </summary>
    static int DecoderMemorySize(uint maxStreams, uint workSize, uint parameters, uint total)
    {
        if (parameters == 0 || total == 0)
        {
            return CE_ERR_INSTANCE_POINTER;
        }

        uint width = GuestMemory.R32(parameters);
        uint height = GuestMemory.R32(parameters + 4);
        uint references = GuestMemory.R32(parameters + 8);
        uint streams = GuestMemory.R32(parameters + 0xC);
        if (width - 0x40 >= 0x741 || height - 0x40 >= 0x401 || references >= 0x11 || streams > maxStreams)
        {
            return CE_ERR_INSTANCE_VALUE;
        }

        if (references == 0)
        {
            references = 1;
        }

        uint frame = height * width * 3;
        uint half = frame >> 1;
        uint frames = frame * references >> 1;
        uint pictures;
        uint referenceArea;
        if (frames <= 0x1BD800 && half <= 0x4A400)
        {
            pictures = 0x140000;
            referenceArea = 0x80000;
        }
        else if (frames <= 0x697800 && half <= 0x151800)
        {
            pictures = 0x400000;
            referenceArea = 0x100000;
        }
        else if (frames <= 0xC00000)
        {
            pictures = 0x700000;
            referenceArea = 0x200000;
        }
        else
        {
            return CE_ERR_INSTANCE_VALUE;
        }

        uint work = workSize == 0 ? 0 : ((workSize + 7) & ~7u) * 0x29 + 0x10C0;
        uint workArea = ((work + 0x3FFFF) & 0xFFFC0000) + 0x80000;
        GuestMemory.W32(total, streams * workArea + streams * referenceArea + streams * pictures);
        return 0;
    }

    /// <summary>NID_8CD35820(parameters, total): the memory a decoder needs (op 1, <see cref="DecoderMemorySize"/>):
    /// mode 0 one stream and no work area, mode 1 up to four streams with the instance's work size (+0x788). With a
    /// decoder open 0x80620106 (none can be: the open runs on the engine).</summary>
    [Hle(Nid = 0x8CD35820, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineDecoderMemorySize(uint parameters, uint total)
    {
        uint mode;
        uint workSize;
        lock (_kernelLock)
        {
            Instance instance = InstanceOf(0);
            mode = instance.Mode;
            workSize = instance.WorkSize;
        }

        return mode == 1 ? DecoderMemorySize(4, workSize, parameters, total) : DecoderMemorySize(1, 0, parameters, total);
    }

    /// <summary>NID_9EB3D63E(decoder): marks a decoder's stream as ended (op 12): mode 0 only decoder 1 (else
    /// 0x80620101), not open 0x80620105; mode 1 a decoder not open 0x80620101.</summary>
    [Hle(Nid = 0x9EB3D63E, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineDecoderEndOfStream(uint decoder)
    {
        uint mode;
        lock (_kernelLock)
        {
            mode = InstanceOf(0).Mode;
        }

        if (mode == 1)
        {
            return CE_ERR_INSTANCE_VALUE;
        }

        return decoder != 1 ? CE_ERR_INSTANCE_VALUE : CE_ERR_NOT_OPEN;
    }

    /// <summary>
    /// NID_16506603(decoder, callback, flags): registers a notification callback (op 14). Flags outside 0x3B06
    /// 0x80620101. Mode 0: only decoder 1; the callback's UID (kscePUIDtoGUID of the caller's handle) must be a
    /// callback (ksceKernelGetThreadmgrUIDClass 8, else 0x80620101), and goes to the first of six free slots with the
    /// caller's pid and the flags (all six taken 0x80620106). Mode 1 keeps them per open decoder: none open 0x80620101.
    /// </summary>
    [Hle(Nid = 0x16506603, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineDecoderAddNotification(uint decoder, int callback, uint flags)
    {
        uint mode;
        lock (_kernelLock)
        {
            mode = InstanceOf(0).Mode;
        }

        if (mode == 1)
        {
            return CE_ERR_INSTANCE_VALUE;
        }

        if (decoder != 1 || (flags & NotificationFlagsRefused) != 0)
        {
            return CE_ERR_INSTANCE_VALUE;
        }

        int guid = Emu.Hle.Modules.OfType<SysmemKernel>().First().kscePUIDtoGUIDThroughEntry(0, callback);
        if (guid < 0)
        {
            return guid;
        }

        int uidClass = Emu.Hle.Modules.OfType<ThreadMgr>().First().sceKernelGetThreadmgrUIDClassThroughEntry(guid);
        if (uidClass < 0)
        {
            return uidClass;
        }

        if (uidClass != new Callback().UidClass)
        {
            return CE_ERR_INSTANCE_VALUE;
        }

        lock (_kernelLock)
        {
            Instance instance = InstanceOf(0);
            for (int slot = 0; slot < NotificationSlots; slot++)
            {
                if (instance.Notifications[slot * 4 + 1] != 0)
                {
                    continue;
                }

                instance.Notifications[slot * 4] = (uint)ProcessScope.CurrentPid;
                instance.Notifications[slot * 4 + 1] = (uint)guid;
                instance.Notifications[slot * 4 + 2] = 0;
                instance.Notifications[slot * 4 + 3] = flags;
                return 0;
            }
        }

        return CE_ERR_INSTANCE_BUSY;
    }

    /// <summary>The notification callbacks of a process's instance in mode 0 (tests, debugger): pid, callback, flags.</summary>
    public IReadOnlyList<(uint Pid, uint Callback, uint Flags)> Notifications(int pid)
    {
        lock (_kernelLock)
        {
            Instance instance = InstanceOf(pid);
            var slots = new List<(uint Pid, uint Callback, uint Flags)>();
            for (int slot = 0; slot < NotificationSlots; slot++)
            {
                if (instance.Notifications[slot * 4 + 1] != 0)
                {
                    slots.Add((instance.Notifications[slot * 4], instance.Notifications[slot * 4 + 1], instance.Notifications[slot * 4 + 3]));
                }
            }

            return slots;
        }
    }

    // ------------------------------------------------------------------ codec instance settings

    /// <summary>NID_2B7CC150(level): the current process's instance level, 1-6; else 0x80620101.</summary>
    [Hle(Nid = 0x2B7CC150, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineSetLevel(uint level)
    {
        if (level - 1 >= 6)
        {
            return CE_ERR_INSTANCE_VALUE;
        }

        lock (_kernelLock)
        {
            InstanceOf(0).Level = level;
            return 0;
        }
    }

    /// <summary>NID_86861877(mode, enable, pid): the instance's mode: 2 sets decoder mode (1), 1 clears it (0); 0, 3 or
    /// any other bit is 0x80620101. A zero second argument still applies the mode but answers 0x8001005F.</summary>
    [Hle(Nid = 0x86861877, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineSetMode(uint mode, int enable, int pid)
    {
        int result = enable == 0 ? CE_ERR_NOT_DECODER_MODE : 0;
        if (mode == 0 || (mode & ~3u) != 0 || mode == 3)
        {
            return CE_ERR_INSTANCE_VALUE;
        }

        lock (_kernelLock)
        {
            Instance instance = InstanceOf(pid);
            if ((mode & 2) != 0)
            {
                instance.Mode = 1;
            }

            if ((mode & 1) != 0)
            {
                instance.Mode = 0;
            }
        }

        return result;
    }

    /// <summary>NID_62996F94(value, mask): sets the instance flag bits of the mask (0x1060000 in decoder mode, else only
    /// 0x1000000) to the value's; a value bit outside them is 0x80620101.</summary>
    [Hle(Nid = 0x62996F94, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineSetFlags(uint value, uint mask)
    {
        lock (_kernelLock)
        {
            Instance instance = InstanceOf(0);
            uint allowed = mask & (instance.Mode == 1 ? 0x1060000u : 0x1000000u);
            if ((value & ~allowed) != 0)
            {
                return CE_ERR_INSTANCE_VALUE;
            }

            instance.Flags = (instance.Flags & ~allowed) | (value & allowed);
            return 0;
        }
    }

    /// <summary>NID_54F39D1D(size): the instance's work size, at most 0x1000 and a multiple of 8 (else 0x80620101);
    /// outside decoder mode the size is still taken but the answer is 0x8001005F.</summary>
    [Hle(Nid = 0x54F39D1D, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineSetWorkSize(uint size)
    {
        lock (_kernelLock)
        {
            Instance instance = InstanceOf(0);
            int result = instance.Mode != 1 ? CE_ERR_NOT_DECODER_MODE : 0;
            if (size > 0x1000 || (size & 7) != 0)
            {
                return CE_ERR_INSTANCE_VALUE;
            }

            instance.WorkSize = size;
            return result;
        }
    }

    /// <summary>NID_18197103(address): a window base, kept to 1 MiB: inside 0x20000000-0x2FFFFFFF the low one, else
    /// the high one; 0.</summary>
    [Hle(Nid = 0x18197103, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineSetWindowBase(uint address)
    {
        lock (_kernelLock)
        {
            if (address >= 0x20000000 && address < 0x30000000)
            {
                _windowLow = address & 0xFFF00000;
            }
            else
            {
                _windowHigh = address & 0xFFF00000;
            }

            return 0;
        }
    }

    /// <summary>The window bases (tests, debugger).</summary>
    public (uint Low, uint High) WindowBases
    {
        get
        {
            lock (_kernelLock)
            {
                return (_windowLow, _windowHigh);
            }
        }
    }

    /// <summary>NID_EA88ABE5(callback) and NID_0E38B133(callback): a callback in the first free of four slots; all four
    /// taken: 0x80600007.</summary>
    [Hle(Nid = 0xEA88ABE5, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineRegisterCallbackA(uint callback)
    {
        return RegisterCallback(_callbacksA, callback);
    }

    [Hle(Nid = 0x0E38B133, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineRegisterCallbackB(uint callback)
    {
        return RegisterCallback(_callbacksB, callback);
    }

    int RegisterCallback(uint[] table, uint callback)
    {
        lock (_kernelLock)
        {
            int slot = Array.IndexOf(table, 0u);
            if (slot < 0)
            {
                return CE_ERR_SLOTS_FULL;
            }

            table[slot] = callback;
            return 0;
        }
    }

    // ------------------------------------------------------------------ unmap memblocks (the user side's table)

    /// <summary>The open unmap memblock holding <paramref name="address"/>, or null (under _blocks' lock).</summary>
    Block? BlockHolding(uint address)
    {
        foreach (Block block in _blocks.Values)
        {
            if (address >= block.Base && address - block.Base < block.Size)
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>NID_F3F71CEF(pid, address, size): 1 when the range lies inside one of the process's unmap memblocks,
    /// else 0.</summary>
    [Hle(Nid = 0xF3F71CEF, Library = "SceCodecEngineWrapperForDriver", ReturnsValue = true)]
    int ksceCodecEngineIsInUnmapMemBlock(int pid, uint address, uint size)
    {
        lock (_blocks)
        {
            if (BlockHolding(address) is not { } block)
            {
                return 0;
            }

            return (ulong)address + size <= (ulong)block.Base + block.Size && (ulong)address + size > block.Base ? 1 : 0;
        }
    }

    /// <summary>The four translations between the views of an unmap memblock (ARM virtual to engine 6D41287F, engine to
    /// virtual BCDCEA13, virtual to physical 82E378BD, physical to virtual 744D4C3F): (pid, address, uint *out). The
    /// emulated memory is flat, so every view of an address is the address. 0 address or one outside the process's
    /// blocks: 0x80600000; a NULL out only checks.</summary>
    [Hle(Nid = 0x6D41287F, Library = "SceCodecEngineWrapperForDriver")]
    [Hle(Nid = 0xBCDCEA13, Library = "SceCodecEngineWrapperForDriver")]
    [Hle(Nid = 0x82E378BD, Library = "SceCodecEngineWrapperForDriver")]
    [Hle(Nid = 0x744D4C3F, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineTranslateUnmapAddress(int pid, uint address, uint output)
    {
        if (address == 0)
        {
            return CE_ERR_NOT_FOUND;
        }

        lock (_blocks)
        {
            if (BlockHolding(address) is null)
            {
                return CE_ERR_NOT_FOUND;
            }
        }

        if (output != 0)
        {
            GuestMemory.W32(output, address);
        }

        return 0;
    }

    /// <summary>NID_FBD4853E and NID_C5478DD5(pid, address, size): checks on an allocation the process made from its
    /// unmap memblocks (the block's allocator): the address must start one; else 0x80600000.</summary>
    [Hle(Nid = 0xFBD4853E, Library = "SceCodecEngineWrapperForDriver")]
    [Hle(Nid = 0xC5478DD5, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineCheckUnmapAllocation(int pid, uint address, uint size)
    {
        if (address == 0)
        {
            return CE_ERR_NOT_FOUND;
        }

        lock (_blocks)
        {
            return BlockHolding(address) is { } block && block.Used.ContainsKey(address) ? 0 : CE_ERR_NOT_FOUND;
        }
    }

    // ------------------------------------------------------------------ transfer lists

    /// <summary>NID_47AED6FF(list, address, size, const uint options[2]): appends an entry to a transfer list. A range
    /// in one physical piece (any non-NULL range of the flat emulated memory) is used in place (its physical address
    /// kept); a range that is not, or options bit 0 (bounce), goes through a bounce buffer, filled unless options bit
    /// 0x20. Cache maintenance has no effect here.</summary>
    [Hle(Nid = 0x47AED6FF, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineListAdd(uint list, uint address, uint size, uint options)
    {
        uint flags = 0, notContiguous = 0, bounceSize = 0, physical = 0, fill = 0, bounce = 0;
        bool inPlace;
        if (options == 0)
        {
            inPlace = address != 0;
            if (!inPlace)
            {
                notContiguous = 1;
                bounceSize = size;
            }
        }
        else
        {
            flags = GuestMemory.R32(options);
            bounceSize = GuestMemory.R32(options + 4);
            notContiguous = flags & 2;
            if ((flags & 1) == 0)
            {
                inPlace = address != 0;
                if (!inPlace)
                {
                    notContiguous = 1;
                    bounceSize = size;
                }
            }
            else
            {
                inPlace = false;
                if (notContiguous == 0)
                {
                    notContiguous = address != 0 ? 0u : 1u;
                }
            }
        }

        if (inPlace)
        {
            physical = address;
        }
        else
        {
            bounce = 1;
            fill = (flags & 0x20) == 0 && size != 0 ? 1u : 0u;
        }

        uint count = GuestMemory.R32(list + 4);
        uint entry = list + ListEntries + count * ListEntrySize;
        GuestMemory.W32(entry, fill);
        GuestMemory.W32(entry + 4, notContiguous);
        GuestMemory.W32(entry + 8, bounce);
        GuestMemory.W32(entry + 0xC, address);
        GuestMemory.W32(entry + 0x10, physical);
        GuestMemory.W32(entry + 0x14, size);
        GuestMemory.W32(entry + 0x18, bounceSize);
        GuestMemory.W32(entry + 0x1C, flags);
        GuestMemory.W32(list + 4, count + 1);
        return 0;
    }

    /// <summary>NID_07C53B4E(list): marks the entries whose bounce buffer is copied back: bounce, non-zero size and no
    /// options bit 0x10; 0.</summary>
    [Hle(Nid = 0x07C53B4E, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineListMarkCopyBack(uint list)
    {
        uint count = GuestMemory.R32(list + 4);
        for (uint i = 0; i < count; i++)
        {
            uint entry = list + ListEntries + i * ListEntrySize;
            bool copyBack = GuestMemory.R32(entry + 8) != 0 && (GuestMemory.R32(entry + 0x1C) & 0x10) == 0
                && GuestMemory.R32(entry + 0x14) != 0;
            GuestMemory.W32(entry, copyBack ? 1u : 0u);
        }

        return 0;
    }

    /// <summary>NID_03EA6C2A(list): gives every bounce entry its buffer in the engine's memory, translated through the
    /// windows: none is mapped here, so each gets 0. NID_1C2BC9F7(list) frees them and NID_D1F94D9D(list) copies them
    /// to and from the engine: no engine memory, nothing to copy. 0.</summary>
    [Hle(Nid = 0x03EA6C2A, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    int ksceCodecEngineListAllocateBounce(uint list)
    {
        uint count = GuestMemory.R32(list + 4);
        for (uint i = 0; i < count; i++)
        {
            uint entry = list + ListEntries + i * ListEntrySize;
            if (GuestMemory.R32(entry + 8) != 0)
            {
                GuestMemory.W32(entry + 0x10, 0);
            }
        }

        return 0;
    }

    [Hle(Nid = 0x1C2BC9F7, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    [Hle(Nid = 0xD1F94D9D, Library = "SceCodecEngineWrapperForDriver", Default = true)]
    int ksceCodecEngineListBounce(uint list)
    {
        return 0;
    }

    /// <summary>NID_FAF590A6(destination, source, size): a DMA copy between two ARM ranges; a zero size or ranges that
    /// overlap are 0x80010016.</summary>
    [Hle(Nid = 0xFAF590A6, Library = "SceCodecEngineWrapperForDriver")]
    int ksceCodecEngineDmaCopy(uint destination, uint source, uint size)
    {
        if (size == 0)
        {
            return CE_ERR_DMA_ARGUMENT;
        }

        bool apart = destination < source ? (ulong)destination + size < source : (ulong)source + size <= destination;
        if (!apart)
        {
            return CE_ERR_DMA_ARGUMENT;
        }

        GuestMemory.Copy(destination, source, size);
        return 0;
    }
}
