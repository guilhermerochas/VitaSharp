using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The process object of the FW 3.74 processmgr.skprx (decompiled, study only) and the SceProcessmgrForDriver /
/// SceProcessmgrForKernel entries that read and write its words. The firmware keeps one per process in kernel memory
/// (ksceKernelGetProcessKernelBuf answers its address); VitaSharp keeps a block of the same layout per process, made on
/// first use, so every word other kernel code sets is answered back at the firmware's offset. The words VitaSharp's
/// model knows are filled when the block is made: +0x3C the state (2, a running process: the byte the start sets),
/// +0x64 the process's UID, +0x6C its main thread, +0xB8 the SELF auth info (0x90 bytes: the program authority ID
/// and the system/non-game capability bits, as <see cref="Loader.Self.ProgramAccess"/> answers them).
/// <para>
/// The process is looked up as the firmware's does it: 0 is the current thread's process (none without a current
/// thread), 0x10005 the kernel's, anything else a process UID; no process is SCE_KERNEL_ERROR_INVALID_PID. Several
/// words are only for a debugging console: their calls check the dipsw bit (0xE4, or 0xD2) and answer
/// SCE_KERNEL_ERROR_NOT_ALLOWED (0x80029008) on a retail console, as the emulated one is unless the bit is set.
/// </para>
/// </summary>
public sealed partial class ProcessmgrKernel
{
    /// <summary>The size of the firmware's process object (the highest word read is +0x430), and after it the tables
    /// the object points at (see ProcessmgrKernel.Tables).</summary>
    const uint ProcessObjectSize = 0x4A0;

    const uint StateWord = 0x3C, UidWord = 0x64, MainThreadWord = 0x6C, AuthInfoOffset = 0xB8, AuthInfoSize = 0x90;

    /// <summary>The state byte of a running process.</summary>
    const uint RunningState = 2;

    /// <summary>A call from an interrupt handler where the firmware refuses it.</summary>
    const int IllegalContext = unchecked((int)0x80027101);

    /// <summary>A debugging-console call on a console without the dipsw bit.</summary>
    const int NotAllowed = unchecked((int)0x80029008);

    /// <summary>The dipsw bits the debugging words check.</summary>
    const uint DebugDipsw = 0xE4, HeapDebugDipsw = 0xD2;

    /// <summary>The process objects made so far, by process UID.</summary>
    [SnapshotMissing] // states of builds before it: no call had made one
    readonly Dictionary<int, uint> _processObjects = new();

    /// <summary>The two tables of (word, word) pairs the debugging console sets: five, and four.</summary>
    [SnapshotMissing] // states of builds before it: never set
    readonly uint[] _debugPairs5 = new uint[10];

    [SnapshotMissing] // states of builds before it: never set
    readonly uint[] _debugPairs4 = new uint[8];

    /// <summary>The operations table NID_4140D633 registers once (+0x1C and +0x20 are called by NID_D231AE5B and
    /// NID_3EBC93BB), and the word NID_5E882B60 sets.</summary>
    [SnapshotMissing] // states of builds before it: never set
    uint _operations;

    [SnapshotMissing] // states of builds before it: never set
    uint _word64C;

    /// <summary>The word NID_5E882B60 keeps (no 3.74 export reads it back).</summary>
    internal uint Word64C => _word64C;

    SystemServicesVsh Services => Emu.Hle.Modules.OfType<SystemServicesVsh>().First();

    bool DebugConsole(uint bit) => Services.ksceKernelCheckDipsw(bit) != 0;

    /// <summary>The process UID <paramref name="pid"/> stands for (0: the current thread's), or 0 when there is none.</summary>
    int ResolvePid(int pid)
    {
        if (pid == 0)
        {
            return GuestThread.Current is { } thread ? (thread.KernelMode ? KernelSpace.KernelPid : thread.Pid) : 0;
        }

        if (pid == KernelSpace.KernelPid)
        {
            return pid;
        }

        return ProcessOf(pid) != null ? pid : 0;
    }

    /// <summary>The process object of <paramref name="pid"/> (made on first use), or 0 when there is no such process.</summary>
    uint ProcessObject(int pid)
    {
        int uid = ResolvePid(pid);
        if (uid == 0)
        {
            return 0;
        }

        lock (_processObjects)
        {
            if (_processObjects.TryGetValue(uid, out uint existing))
            {
                return existing;
            }

            uint block = KernelAlloc(ProcessObjectSize, "kernel:SceUIDProcessObject");
            GuestMemory.Fill(block, 0, ProcessObjectSize);
            GuestMemory.W32(block + StateWord, RunningState);
            GuestMemory.W32(block + UidWord, (uint)uid);
            GuestMemory.W32(block + 0xC, block + 0x440);
            GuestMemory.W32(block + 0x10, block + 0x470);
            GuestMemory.W32(block + 0x288, block + 0x498);
            if (uid == KernelSpace.KernelPid)
            {
                GuestMemory.W32(block + 0x40, KernelProcessType);
            }
            if (uid != KernelSpace.KernelPid && ProcessOf(uid) is { } process)
            {
                Loader.Self.ProgramAccess access = process.Access;
                GuestMemory.Write(block + AuthInfoOffset, access.AuthorityId);
                byte capability = 0;
                if (access.IsSystemProgram)
                {
                    capability |= 0x40; // bit 1, numbered from the most significant bit of the first byte
                }

                if (access.IsNonGameProgram)
                {
                    capability |= 0x10; // bit 3
                }

                GuestMemory.Write(block + AuthInfoOffset + 0x10, capability);
            }

            _processObjects[uid] = block;
            return block;
        }
    }

    /// <summary>Runs <paramref name="read"/> on the process object of <paramref name="pid"/>; INVALID_PID without one.</summary>
    int WithProcess(int pid, Func<uint, int> read)
    {
        uint block = ProcessObject(pid);
        return block == 0 ? SceError.INVALID_PID : read(block);
    }

    int SetWord(int pid, uint offset, uint value)
    {
        return WithProcess(pid, block =>
        {
            GuestMemory.W32(block + offset, value);
            return 0;
        });
    }

    int GetWord(int pid, uint offset) => WithProcess(pid, block => (int)GuestMemory.R32(block + offset));

    // ------------------------------------------------------------------ the process object itself

    /// <summary>ksceKernelGetProcessKernelBuf(pid): the process object's address (0 without such a process).</summary>
    [Hle(Nid = 0xD991C85E, Library = "SceProcessmgrForKernel", ReturnsValue = true)]
    uint ksceKernelGetProcessKernelBuf(int pid) => ProcessObject(pid);

    /// <summary>ksceKernelGetUIDProcessClass(): the UID class of process objects. VitaSharp's processes belong to the
    /// root UID class (ksceKernelGetUidClass).</summary>
    [Hle(Nid = 0x98AE4BC8, Library = "SceProcessmgrForKernel", ReturnsValue = true)]
    void ksceKernelGetUIDProcessClass(HleContext context)
    {
        if (Emu.Hle.ImplOf(0x85336A1C) is { } uidClass)
        {
            uidClass(context);
        }
    }

    /// <summary>ksceKernelGetProcessStatus(pid, int *status): the state byte (+0x3C).</summary>
    [Hle(Nid = 0x65B120B8, Library = "SceProcessmgrForDriver")]
    int ksceKernelGetProcessStatus(int pid, uint status)
    {
        return WithProcess(pid, block =>
        {
            GuestMemory.W32(status, GuestMemory.R32(block + StateWord) & 0xFF);
            return 0;
        });
    }

    /// <summary>NID_1D0F3185(pid, state): sets the state byte; a state without bit 4 is SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0x1D0F3185, Library = "SceProcessmgrForDriver")]
    int NID_1D0F3185(int pid, uint state)
    {
        if ((state & 0x10) == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return WithProcess(pid, block =>
        {
            uint word = GuestMemory.R32(block + StateWord);
            GuestMemory.W32(block + StateWord, (word & 0xFFFFFF00) | (state & 0xFF));
            return 0;
        });
    }

    /// <summary>NID_0CC5B30C(pid): bit 20 of the state word (0 or 1).</summary>
    [Hle(Nid = 0x0CC5B30C, Library = "SceProcessmgrForDriver")]
    int NID_0CC5B30C(int pid) => WithProcess(pid, block => (int)((GuestMemory.R32(block + StateWord) >> 20) & 1));

    /// <summary>ksceKernelGetProcessMainThread(pid): the process's main thread (+0x6C), kept up to date from the model.</summary>
    [Hle(Nid = 0xD20553C6, Library = "SceProcessmgrForKernel")]
    int ksceKernelGetProcessMainThread(int pid)
    {
        return WithProcess(pid, block =>
        {
            int uid = (int)GuestMemory.R32(block + UidWord);
            if (uid != KernelSpace.KernelPid && ProcessOf(uid)?.Kernel.MainThread is { } main)
            {
                GuestMemory.W32(block + MainThreadWord, (uint)main.Uid);
            }

            return (int)GuestMemory.R32(block + MainThreadWord);
        });
    }

    /// <summary>ksceKernelGetProcessSelfAuthInfo(pid, SceSelfAuthInfo *info): copies the 0x90 bytes at +0xB8.</summary>
    [Hle(Nid = 0x324F2B20, Library = "SceProcessmgrForKernel")]
    int ksceKernelGetProcessSelfAuthInfo(int pid, uint info)
    {
        return WithProcess(pid, block =>
        {
            GuestMemory.Copy(info, block + AuthInfoOffset, AuthInfoSize);
            return 0;
        });
    }

    /// <summary>NID_72F25C14(pid, info): the process's module information word (+0x9C) that
    /// ksceKernelGetProcessModuleInfo answers.</summary>
    [Hle(Nid = 0x72F25C14, Library = "SceProcessmgrForKernel")]
    int NID_72F25C14(int pid, uint value) => SetWord(pid, 0x9C, value);

    /// <summary>ksceKernelGetProcessModuleInfo(pid): the +0x9C word, 0 without such a process.</summary>
    [Hle(Nid = 0x3AF6B088, Library = "SceProcessmgrForKernel", ReturnsValue = true)]
    uint ksceKernelGetProcessModuleInfo(int pid)
    {
        uint block = ProcessObject(pid);
        return block == 0 ? 0 : GuestMemory.R32(block + 0x9C);
    }

    // ------------------------------------------------------------------ plain words

    /// <summary>NID_24FA97A5(pid, value): sets +0x1FC.</summary>
    [Hle(Nid = 0x24FA97A5, Library = "SceProcessmgrForDriver")]
    int NID_24FA97A5(int pid, uint value) => SetWord(pid, 0x1FC, value);

    /// <summary>NID_54D469E2(uint *out): the +0x1FC word of the current thread's process (the kernel's without one); 0.</summary>
    [Hle(Nid = 0x54D469E2, Library = "SceProcessmgrForDriver")]
    int NID_54D469E2(uint output)
    {
        uint block = ProcessObject(GuestThread.Current == null ? KernelSpace.KernelPid : 0);
        GuestMemory.W32(output, GuestMemory.R32(block + 0x1FC));
        return 0;
    }

    /// <summary>NID_D141C076(pid, uint *out): *out = +0x148 (pid 0: the caller's process); 0.</summary>
    [Hle(Nid = 0xD141C076, Library = "SceProcessmgrForDriver")]
    int NID_D141C076(int pid, uint output)
    {
        return WithProcess(pid, block =>
        {
            GuestMemory.W32(output, GuestMemory.R32(block + 0x148));
            return 0;
        });
    }

    /// <summary>NID_E1A67C86(pid, uint *out): *out = the 16-bit +0x408 word; a null out is SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0xE1A67C86, Library = "SceProcessmgrForDriver")]
    int NID_E1A67C86(int pid, uint output)
    {
        if (output == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return WithProcess(pid, block =>
        {
            GuestMemory.W32(output, GuestMemory.Read<ushort>(block + 0x408));
            return 0;
        });
    }

    /// <summary>NID_879153D9(pid, which): bit 0 (which 0x1xxxx) or bit 1 (0x2xxxx) of the 16-bit +0x40A word; any other
    /// which is SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0x879153D9, Library = "SceProcessmgrForDriver")]
    int NID_879153D9(int pid, uint which)
    {
        return WithProcess(pid, block =>
        {
            uint flags = GuestMemory.Read<ushort>(block + 0x40A);
            return (which & 0xFFFF0000) switch
            {
                0x10000 => (int)(flags & 1),
                0x20000 => (int)((flags >> 1) & 1),
                _ => SceError.INVALID_ARGUMENT,
            };
        });
    }

    /// <summary>NID_A1CF74FA(pid): 1 when the +0x3F0 word is set, else 0.</summary>
    [Hle(Nid = 0xA1CF74FA, Library = "SceProcessmgrForDriver")]
    int NID_A1CF74FA(int pid) => WithProcess(pid, block => GuestMemory.R32(block + 0x3F0) != 0 ? 1 : 0);

    /// <summary>NID_00DBA872(pid, value): sets +0x50; not from an interrupt handler (0x80027101).</summary>
    [Hle(Nid = 0x00DBA872, Library = "SceProcessmgrForKernel")]
    int NID_00DBA872(int pid, uint value) => SystemServicesVsh.InInterrupt ? IllegalContext : SetWord(pid, 0x50, value);

    /// <summary>NID_E5097B4F(pid): the +0x50 word.</summary>
    [Hle(Nid = 0xE5097B4F, Library = "SceProcessmgrForKernel")]
    int NID_E5097B4F(int pid) => GetWord(pid, 0x50);

    /// <summary>NID_F013B7F1(pid, value): sets +0x54 to a value with only bits 0-3 (or 16-19, kept shifted down); any
    /// other bit, or both halves, is 0x80022004.</summary>
    [Hle(Nid = 0xF013B7F1, Library = "SceProcessmgrForKernel")]
    int NID_F013B7F1(int pid, uint value)
    {
        return WithProcess(pid, block =>
        {
            if ((value & 0xFFF0FFF0) != 0 || ((value & 0xF0000) != 0 && (value & 0xF) != 0))
            {
                return unchecked((int)0x80022004);
            }

            GuestMemory.W32(block + 0x54, (value & 0xF0000) != 0 ? value >> 16 : value);
            return 0;
        });
    }

    /// <summary>NID_9BC44974(pid): the +0x5C word.</summary>
    [Hle(Nid = 0x9BC44974, Library = "SceProcessmgrForKernel")]
    int NID_9BC44974(int pid) => GetWord(pid, 0x5C);

    /// <summary>NID_A5E55217(pid): the +0x68 word.</summary>
    [Hle(Nid = 0xA5E55217, Library = "SceProcessmgrForKernel")]
    int NID_A5E55217(int pid) => GetWord(pid, 0x68);

    /// <summary>NID_B631C93B(pid): the +0x40 word.</summary>
    [Hle(Nid = 0xB631C93B, Library = "SceProcessmgrForKernel")]
    int NID_B631C93B(int pid) => GetWord(pid, 0x40);

    // ------------------------------------------------------------------ debugging console words

    /// <summary>NID_B1C3EFCA(pid, value): sets +0x28C (debugging console).</summary>
    [Hle(Nid = 0xB1C3EFCA, Library = "SceProcessmgrForDriver")]
    int NID_B1C3EFCA(int pid, uint value) => DebugConsole(DebugDipsw) ? SetWord(pid, 0x28C, value) : NotAllowed;

    /// <summary>NID_1C70501E(pid): the +0x28C word (debugging console).</summary>
    [Hle(Nid = 0x1C70501E, Library = "SceProcessmgrForDriver")]
    int NID_1C70501E(int pid) => DebugConsole(DebugDipsw) ? GetWord(pid, 0x28C) : NotAllowed;

    /// <summary>NID_61B9B6FA(pid, value): sets +0x290 (debugging console).</summary>
    [Hle(Nid = 0x61B9B6FA, Library = "SceProcessmgrForDriver")]
    int NID_61B9B6FA(int pid, uint value) => DebugConsole(DebugDipsw) ? SetWord(pid, 0x290, value) : NotAllowed;

    /// <summary>NID_594319C7(pid): the +0x290 word (debugging console).</summary>
    [Hle(Nid = 0x594319C7, Library = "SceProcessmgrForDriver")]
    int NID_594319C7(int pid) => DebugConsole(DebugDipsw) ? GetWord(pid, 0x290) : NotAllowed;

    /// <summary>NID_1197DB8A(index, a, b): sets pair <paramref name="index"/> (below 5) of the first debugging table.</summary>
    [Hle(Nid = 0x1197DB8A, Library = "SceProcessmgrForKernel")]
    int NID_1197DB8A(uint index, uint first, uint second) => SetPair(_debugPairs5, 5, index, first, second);

    /// <summary>NID_6DEC45C3(index, *a, *b): reads pair <paramref name="index"/> of the first table (null outs:
    /// SCE_KERNEL_ERROR_ILLEGAL_ADDR).</summary>
    [Hle(Nid = 0x6DEC45C3, Library = "SceProcessmgrForKernel")]
    int NID_6DEC45C3(uint index, uint first, uint second) => GetPair(_debugPairs5, 5, index, first, second);

    /// <summary>NID_DDE282CF(index, a, b): sets pair <paramref name="index"/> (below 4) of the second debugging table.</summary>
    [Hle(Nid = 0xDDE282CF, Library = "SceProcessmgrForKernel")]
    int NID_DDE282CF(uint index, uint first, uint second) => SetPair(_debugPairs4, 4, index, first, second);

    /// <summary>NID_9FA66773(index, *a, *b): reads pair <paramref name="index"/> of the second table.</summary>
    [Hle(Nid = 0x9FA66773, Library = "SceProcessmgrForKernel")]
    int NID_9FA66773(uint index, uint first, uint second) => GetPair(_debugPairs4, 4, index, first, second);

    int SetPair(uint[] table, uint entries, uint index, uint first, uint second)
    {
        if (!DebugConsole(DebugDipsw))
        {
            return NotAllowed;
        }

        if (index >= entries)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (table)
        {
            table[index * 2] = first;
            table[index * 2 + 1] = second;
        }

        return 0;
    }

    int GetPair(uint[] table, uint entries, uint index, uint first, uint second)
    {
        if (!DebugConsole(DebugDipsw))
        {
            return NotAllowed;
        }

        if (index >= entries)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (first == 0 || second == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        lock (table)
        {
            GuestMemory.W32(first, table[index * 2]);
            GuestMemory.W32(second, table[index * 2 + 1]);
        }

        return 0;
    }

    // ------------------------------------------------------------------ operations

    /// <summary>NID_4140D633(table): registers the operations table once (a later call keeps the first); 0.</summary>
    [Hle(Nid = 0x4140D633, Library = "SceProcessmgrForDriver")]
    int NID_4140D633(uint table)
    {
        lock (_processObjects)
        {
            if (_operations == 0)
            {
                _operations = table;
            }
        }

        return 0;
    }

    /// <summary>NID_D231AE5B(): calls the table's +0x1C operation and answers its result;
    /// SCE_KERNEL_ERROR_NOT_IMPLEMENTED when there is none.</summary>
    [Hle(Nid = 0xD231AE5B, Library = "SceProcessmgrForKernel")]
    int NID_D231AE5B() => CallOperation(0x1C);

    /// <summary>NID_3EBC93BB(): the same for the +0x20 operation.</summary>
    [Hle(Nid = 0x3EBC93BB, Library = "SceProcessmgrForKernel")]
    int NID_3EBC93BB() => CallOperation(0x20);

    int CallOperation(uint offset)
    {
        uint table;
        lock (_processObjects)
        {
            table = _operations;
        }

        uint operation = table == 0 ? 0 : GuestMemory.R32(table + offset);
        if (operation == 0 || GuestThread.Current is not { } thread)
        {
            return unchecked((int)0x80020002);
        }

        return (int)thread.CallGuest(operation);
    }

    /// <summary>NID_5E882B60(value): keeps a word; 0 is SCE_KERNEL_ERROR_INVALID_ARGUMENT.</summary>
    [Hle(Nid = 0x5E882B60, Library = "SceProcessmgrForDriver")]
    int NID_5E882B60(uint value)
    {
        if (value == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        _word64C = value;
        return 0;
    }

    // ------------------------------------------------------------------ process heaps

    /// <summary>The blocks of the processes' kernel heaps (the +0x88 heap), by address: the process UID.</summary>
    [SnapshotMissing] // states of builds before it: no call had made one
    readonly Dictionary<uint, int> _processKernelHeap = new();

    int CurrentPid() => GuestThread.Current is { KernelMode: false } thread ? thread.Pid : KernelSpace.KernelPid;

    /// <summary>NID_5468892A(size, opt): a block of the current process's heap (+0x80: what
    /// ksceKernelAllocRemoteProcessHeap gives for that process).</summary>
    [Hle(Nid = 0x5468892A, Library = "SceProcessmgrForDriver", ReturnsValue = true)]
    uint NID_5468892A(uint size, uint opt) => ksceKernelAllocRemoteProcessHeap(CurrentPid(), size, opt);

    /// <summary>NID_FC2A424E(ptr): frees a block of the current process's heap; 0.</summary>
    [Hle(Nid = 0xFC2A424E, Library = "SceProcessmgrForDriver")]
    int NID_FC2A424E(uint ptr)
    {
        ksceKernelFreeRemoteProcessHeap(CurrentPid(), ptr);
        return 0;
    }

    /// <summary>NID_3B33FFBA(size, opt): a block of the current process's kernel heap (+0x88).</summary>
    [Hle(Nid = 0x3B33FFBA, Library = "SceProcessmgrForDriver", ReturnsValue = true)]
    uint NID_3B33FFBA(uint size, uint opt) => AllocProcessKernelHeap(CurrentPid(), size);

    /// <summary>NID_11BEFBBE(ptr): frees a block of the current process's kernel heap.</summary>
    [Hle(Nid = 0x11BEFBBE, Library = "SceProcessmgrForDriver")]
    int NID_11BEFBBE(uint ptr) => FreeProcessKernelHeap(ptr);

    /// <summary>NID_8A85FA28(pid, size, opt): a block of the kernel heap of process <paramref name="pid"/> (the kernel's
    /// own heap for the kernel pids); NULL without such a process.</summary>
    [Hle(Nid = 0x8A85FA28, Library = "SceProcessmgrForDriver", ReturnsValue = true)]
    uint NID_8A85FA28(int pid, uint size, uint opt)
    {
        int uid = ResolvePid(pid);
        return uid == 0 ? 0 : AllocProcessKernelHeap(uid, size);
    }

    /// <summary>NID_DD0A58D1(pid, ptr): frees a block of that kernel heap.</summary>
    [Hle(Nid = 0xDD0A58D1, Library = "SceProcessmgrForDriver")]
    int NID_DD0A58D1(int pid, uint ptr) => ResolvePid(pid) == 0 ? SceError.INVALID_PID : FreeProcessKernelHeap(ptr);

    /// <summary>NID_51AF6FE6(pid, size): a block of the process's debugging heap (+0x8C, debugging console only).
    /// VitaSharp's processes have none (the firmware's -1), so the answer is NULL.</summary>
    [Hle(Nid = 0x51AF6FE6, Library = "SceProcessmgrForKernel", ReturnsValue = true)]
    uint NID_51AF6FE6(int pid, uint size) => 0;

    /// <summary>NID_2755CEDD(pid, ptr): frees a block of the debugging heap: 0x80020004 without one (always here).</summary>
    [Hle(Nid = 0x2755CEDD, Library = "SceProcessmgrForKernel")]
    int NID_2755CEDD(int pid, uint ptr)
    {
        if (!DebugConsole(HeapDebugDipsw))
        {
            return unchecked((int)0x80020004);
        }

        return ResolvePid(pid) == 0 ? SceError.INVALID_PID : unchecked((int)0x80020004);
    }

    uint AllocProcessKernelHeap(int pid, uint size)
    {
        if (size == 0)
        {
            return 0;
        }

        uint address;
        try
        {
            address = KernelAlloc(size, "kernel:processheap");
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }

        lock (_processKernelHeap)
        {
            _processKernelHeap[address] = pid;
        }

        return address;
    }

    int FreeProcessKernelHeap(uint ptr)
    {
        lock (_processKernelHeap)
        {
            if (!_processKernelHeap.Remove(ptr))
            {
                return SceError.ILLEGAL_ADDR;
            }
        }

        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            Mem.Free(ptr);
        }

        return 0;
    }
}
