using System.Buffers.Binary;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceUlobjMgr (os0:bootimage SceUlobjMgr 3.74, read in the module, study only): the kernel's support of user-level
// objects (the libult threads, mutexes, condition variables and the rest a process keeps in its own memory) for the
// debugger and the core dump module.
//
// The start (_sceUlobjMgrStartSupportingUserlevelObject(count, revision, &table)) checks the revision (above 3.74's
// 0x3740000: 0x80020004) and the count (above 0x2000000: 0x8002710B), takes the process's record (one per process,
// a second start 0x80028003), maps a table of `count` words after a 0x10-byte header, user writable, into the process
// and the kernel (one address here: kernel memory every space sees) and hands its address to libult. The header holds
// +8 = 0 and +0xC the debugger's value for the process (no debugger handler: 0); the words start as a free list (word
// i = (i + 1) * 8, the last 0xFFFFFFF8). libult then puts each object it creates in a word: its address (8-byte
// aligned) with bit 2 set and the low two bits the kind of entry (0 a ulthread, 1 a ulthread runtime, 2 a
// synchronisation object). The record is +0 pid, +4/+8 the ids of the two mappings (0 here: one block), +0xC the
// revision, +0x10 the count and +0x14 the table.
//
// The kernel entries read the objects from the process's memory (SceSysmemForDriver_605275F8: an inaccessible range is
// the copy's error) in libult's layout: the name in +0..+0x1B, +0x1C the attributes (24 bits), +0x20 the object's size
// (16 bits, at least the size the kernel reads) and +0x22 its kind (1 mutex, 2 condition variable, 3 semaphore,
// 4 reader/writer lock, 5 queue, 6 waiting-thread pool, 7 data pool); another kind or a smaller size is 0x8002000C.
// The waiters of an object are a chain of 12-byte nodes (+0 flags, +4 next, +8 the waiting thread's record) after a
// head node, bounded by the pool's thread count (one more: 0x80022005); a record's word +0 is the thread's id
// (bit 0 set: id << 1 | 1, else the address of the id) and +8 the object it waits on.
public sealed partial class DebugPerf
{
    const int ULOBJ_ERR_ARGUMENT = unchecked((int)0x80020005);
    const int ULOBJ_ERR_REVISION = unchecked((int)0x80020004);
    const int ULOBJ_ERR_TOO_LARGE = unchecked((int)0x8002710B);
    const int ULOBJ_ERR_NOT_STARTED = unchecked((int)0x80029001);
    const int ULOBJ_ERR_ALREADY_STARTED = unchecked((int)0x80028003);
    const int ULOBJ_ERR_NO_RECORD = unchecked((int)0x80010002);
    const int ULOBJ_ERR_NO_OBJECT = unchecked((int)0x8002000C);
    const int ULOBJ_ERR_TOO_MANY = unchecked((int)0x80022005);

    /// <summary>The newest libult protocol the module accepts (3.74).</summary>
    const uint UlobjMaxRevision = 0x3740000;

    /// <summary>The most table words a process may ask for.</summary>
    const uint UlobjMaxTable = 0x2000000;

    /// <summary>The size of a process record in kernel memory.</summary>
    const uint UlobjRecordSize = 0x40;

    /// <summary>The table's header before the words.</summary>
    const uint UlobjTableHeader = 0x10;

    // The kinds of libult object at +0x22 and the sizes the kernel reads of each.
    const byte UlobjMutex = 1;
    const byte UlobjCond = 2;
    const byte UlobjSema = 3;
    const byte UlobjRwLock = 4;
    const byte UlobjQueue = 5;
    const byte UlobjWaitingPool = 6;
    const byte UlobjDataPool = 7;
    const int UlobjMutexSize = 0x64;
    const int UlobjCondSize = 0x44;
    const int UlobjSemaSize = 0x6C;
    const int UlobjRwLockSize = 0x6C;
    const int UlobjQueueSize = 0x94;
    const int UlobjWaitingPoolSize = 0x50;
    const int UlobjDataPoolSize = 0xAC;
    const int UlobjRuntimeSize = 0x34;
    const int UlobjThreadSize = 0xB8;
    const int UlobjContextSize = 0x80;
    const int UlobjContextExtraSize = 0x70;

    /// <summary>The most threads a waiting-thread pool may declare.</summary>
    const uint UlobjMaxPoolThreads = 0xAAB2A;

    /// <summary>The thread states the debugger reports for libult's states 0..5 (6 for any other).</summary>
    static readonly uint[] s_ulobjThreadStates = [0, 1, 4, 5, 3, 2];

    /// <summary>The XOR keys of a ulthread context's name and its words +0x30/+0x34.</summary>
    static readonly uint[] s_ulobjContextKeys =
        [0x66665A5A, 0x96669999, 0xA555AAAA, 0xA5AA55AA, 0x33CCC333, 0x5555C3CC, 0x96996699, 0xCCCC3333];

    /// <summary>The process started the support (its record exists).</summary>
    [SnapshotMissing] // states of builds before it: not started
    bool _ulobjStarted;

    /// <summary>The revision and the number of table words it started with.</summary>
    [SnapshotMissing]
    uint _ulobjRevision;

    [SnapshotMissing]
    uint _ulobjTableSize;

    /// <summary>The process record in kernel memory (0 until first asked for).</summary>
    [SnapshotMissing]
    uint _ulobjRecord;

    /// <summary>The object table libult fills (0 while not started).</summary>
    [SnapshotMissing]
    uint _ulobjTable;

    int UlobjRegisterRevision(uint revision)
    {
        return revision < 0x1800000 || (revision & 0xFFFF) == 0 ? ULOBJ_ERR_ARGUMENT : 0;
    }

    int UlobjStart(uint count, uint revision, uint result)
    {
        if (revision > UlobjMaxRevision)
        {
            return ULOBJ_ERR_REVISION;
        }

        if (count > UlobjMaxTable)
        {
            return ULOBJ_ERR_TOO_LARGE;
        }

        if (_ulobjStarted)
        {
            return ULOBJ_ERR_ALREADY_STARTED;
        }

        uint size = (count * 4 + 0x100F) & 0xFFFFF000;
        uint table;
        try
        {
            table = Emu.Memory.Alloc(size, "SceUlobjMgrUser", MemoryRegion.Kernel);
        }
        catch (OutOfMemoryException)
        {
            return ULOBJ_ERR_TOO_LARGE;
        }

        GuestMemory.Fill(table, 0, size);
        UlobjInitTable(table, count);
        if (!KernelSpace.IsUserRange(result, 4))
        {
            Emu.Memory.Free(table);
            return KernelSpace.UserCopyFault & unchecked((int)0xBFFFFFFF);
        }

        GuestMemory.W32(result, table);
        _ulobjStarted = true;
        _ulobjRevision = revision;
        _ulobjTableSize = count;
        _ulobjTable = table;
        return 0;
    }

    /// <summary>The table as the module lays it out: +8 0, +0xC the debugger's value (no handler: 0), the words a
    /// free list of their next index times 8, the last 0xFFFFFFF8.</summary>
    static void UlobjInitTable(uint table, uint count)
    {
        GuestMemory.W32(table + 8, 0);
        GuestMemory.W32(table + 0xC, 0);
        for (uint index = 0; index + 1 < count; index++)
        {
            GuestMemory.W32(table + UlobjTableHeader + index * 4, (index + 1) * 8);
        }

        GuestMemory.W32(table + (count + 3) * 4, 0xFFFFFFF8);
    }

    int UlobjStop()
    {
        if (!_ulobjStarted)
        {
            return ULOBJ_ERR_NOT_STARTED;
        }

        _ulobjStarted = false;
        if (_ulobjTable != 0)
        {
            Emu.Memory.Free(_ulobjTable);
            _ulobjTable = 0;
        }

        return 0;
    }

    /// <summary>The DebugPerf of process <paramref name="pid"/> (0: the caller), or null.</summary>
    DebugPerf? UlobjProcess(int pid)
    {
        Emulator? process = pid == 0 || pid == Emu.Pid ? Emu : Emu.Vita.ByPid(pid);
        return process?.Hle.Modules.OfType<DebugPerf>().FirstOrDefault();
    }

    /// <summary>The record of a process that started the support, or null.</summary>
    DebugPerf? UlobjStarted(int pid)
    {
        return UlobjProcess(pid) is { _ulobjStarted: true } target ? target : null;
    }

    /// <summary>The kernel record of a process that started the support: +0 pid, +0xC the revision, +0x10 the number
    /// of words, +0x14 the table.</summary>
    uint UlobjRecord()
    {
        if (_ulobjRecord == 0)
        {
            _ulobjRecord = Emu.Memory.Alloc(UlobjRecordSize, "ulobj record", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(_ulobjRecord, 0, UlobjRecordSize);
        GuestMemory.W32(_ulobjRecord, (uint)Emu.Pid);
        GuestMemory.W32(_ulobjRecord + 4, 0);
        GuestMemory.W32(_ulobjRecord + 8, 0);
        GuestMemory.W32(_ulobjRecord + 0xC, _ulobjRevision);
        GuestMemory.W32(_ulobjRecord + 0x10, _ulobjTableSize);
        GuestMemory.W32(_ulobjRecord + 0x14, _ulobjTable);
        return _ulobjRecord;
    }

    /// <summary>NID_A90A564C(pid, table, revision, record): the process's record: NULLs 0x80020005, not started
    /// 0x80010002; the table, the address of the record's revision (+0xC) and the record.</summary>
    [Hle(Nid = 0xA90A564C, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_A90A564C(int pid, uint table, uint revision, uint record)
    {
        if (table == 0 || revision == 0 || record == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        if (UlobjStarted(pid) is not { } target)
        {
            return ULOBJ_ERR_NO_RECORD;
        }

        uint address = target.UlobjRecord();
        GuestMemory.W32(table, target._ulobjTable);
        GuestMemory.W32(revision, address + 0xC);
        GuestMemory.W32(record, address);
        return 0;
    }

    /// <summary>NID_A48A5B3C(record): releases a record taken with NID_A90A564C: NULL 0x80020005; 0.</summary>
    [Hle(Nid = 0xA48A5B3C, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_A48A5B3C(uint record)
    {
        return record == 0 ? ULOBJ_ERR_ARGUMENT : 0;
    }

    /// <summary>NID_6F868221(): some process has started the support: 0, else 0x80020004.</summary>
    [Hle(Nid = 0x6F868221, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_6F868221()
    {
        foreach (Emulator process in Emu.Vita.Processes)
        {
            if (process.Hle.Modules.OfType<DebugPerf>().FirstOrDefault() is { _ulobjStarted: true })
            {
                return 0;
            }
        }

        return ULOBJ_ERR_REVISION;
    }

    /// <summary>NID_332F2E58(pid, a, b, c, d): the debugger's request through the sysroot with the process's table:
    /// handled by no debugger (0).</summary>
    [Hle(Nid = 0x332F2E58, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_332F2E58(int pid, uint first, uint second, uint third, uint fourth)
    {
        return 0;
    }

    // ------------------------------------------------------------------ reading the process's objects

    /// <summary>A copy from the process's memory as the module makes it (SceSysmemForDriver_605275F8): no such process
    /// or a range its user code cannot access is the copy's error.</summary>
    static int UlobjRead(Emulator? process, uint source, Span<byte> destination)
    {
        if (process == null)
        {
            return SceError.INVALID_PID;
        }

        using (AddressSpace.Enter(process.Space))
        {
            if (!KernelSpace.IsUserRange(source, (uint)destination.Length))
            {
                return KernelSpace.UserCopyFault;
            }

            GuestMemory.ReadSpan(source, destination.Length).CopyTo(destination);
        }

        return 0;
    }

    static uint UlobjWord(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    static ushort UlobjHalf(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    }

    /// <summary>Reads an object of a kind: the copy's error, or 0x8002000C for another kind or a smaller size.</summary>
    static int UlobjReadObject(Emulator? process, uint address, Span<byte> data, byte kind)
    {
        int read = UlobjRead(process, address, data);
        if (read != 0)
        {
            return read;
        }

        return UlobjHalf(data, 0x20) < data.Length || data[0x22] != kind ? ULOBJ_ERR_NO_OBJECT : 0;
    }

    /// <summary>The thread count of a waiting-thread pool (at most 0xAAB2A), or an error.</summary>
    static int UlobjPoolThreads(Emulator? process, uint pool, out uint threads)
    {
        Span<byte> data = stackalloc byte[UlobjWaitingPoolSize];
        threads = 0;
        int read = UlobjReadObject(process, pool, data, UlobjWaitingPool);
        if (read != 0)
        {
            return read;
        }

        threads = UlobjWord(data, 0x24);
        return threads > UlobjMaxPoolThreads ? ULOBJ_ERR_NO_OBJECT : 0;
    }

    /// <summary>A thread record's id: bit 0 set, the word shifted; else the word it points to.</summary>
    static int UlobjThreadId(Emulator? process, uint word, out uint id)
    {
        id = word >> 1;
        if ((word & 1) != 0)
        {
            return 0;
        }

        Span<byte> data = stackalloc byte[4];
        int read = UlobjRead(process, word, data);
        id = UlobjWord(data, 0);
        return read;
    }

    /// <summary>
    /// The waiters in a chain (the module's walk): every node after the head whose predecessor's flags have a bit of
    /// <paramref name="mask"/> (0: any) and whose thread waits on <paramref name="match"/> (0: any). More than
    /// <paramref name="max"/> is 0x80022005; the first <paramref name="capacity"/> ids go to
    /// <paramref name="output"/> (0: only counted). Returns the count or an error.
    /// </summary>
    static int UlobjWaiters(Emulator? process, uint head, uint mask, uint match, uint max, uint capacity, uint output)
    {
        Span<byte> node = stackalloc byte[12];
        Span<byte> waiter = stackalloc byte[12];
        uint address = head & 0xFFFFFFF8;
        uint count = 0;
        uint previousFlags = 0;
        bool first = true;
        while (address - 1 < 0xFFFFFFFE)
        {
            int read = UlobjRead(process, address, node);
            if (read != 0)
            {
                return read;
            }

            if (!first)
            {
                read = UlobjRead(process, UlobjWord(node, 8), waiter);
                if (read != 0)
                {
                    return read;
                }

                bool kindMatches = mask == 0 || (previousFlags & mask) != 0;
                bool objectMatches = match == 0 || UlobjWord(waiter, 8) == match;
                if (kindMatches && objectMatches)
                {
                    if (count >= max)
                    {
                        return ULOBJ_ERR_TOO_MANY;
                    }

                    if (output != 0)
                    {
                        read = UlobjThreadId(process, UlobjWord(waiter, 0), out uint id);
                        if (read != 0)
                        {
                            return read;
                        }

                        if (count < capacity)
                        {
                            GuestMemory.W32(output + count * 4, id);
                        }
                    }

                    count++;
                }
            }

            first = false;
            previousFlags = UlobjWord(node, 0);
            address = UlobjWord(node, 4) & 0xFFFFFFF8;
        }

        return (int)count;
    }

    /// <summary>The nodes of a data chain after its head: counted, and with <paramref name="output"/> the
    /// <paramref name="size"/> bytes each non-zero node points to copied every <paramref name="stride"/> bytes (more
    /// than <paramref name="max"/> copies: 0x80022005).</summary>
    static int UlobjDataNodes(Emulator? process, uint head, uint max, uint size, uint stride, uint output)
    {
        Span<byte> node = stackalloc byte[12];
        uint address = head & 0xFFFFFFF8;
        if (address - 1 >= 0xFFFFFFFE)
        {
            return 0;
        }

        byte[]? item = null;
        uint count = 0;
        bool first = true;
        while (true)
        {
            int read = UlobjRead(process, address, node);
            if (read != 0)
            {
                return read;
            }

            if (!first)
            {
                uint data = UlobjWord(node, 8);
                if (data != 0 && output != 0)
                {
                    if (count >= max)
                    {
                        return ULOBJ_ERR_TOO_MANY;
                    }

                    item ??= new byte[size];
                    read = UlobjRead(process, data, item);
                    if (read != 0)
                    {
                        return read;
                    }

                    item.CopyTo(GuestMemory.Span(output, (int)size));
                }

                count++;
                output += stride;
            }

            address = UlobjWord(node, 4) & 0xFFFFFFF8;
            if (address - 1 > 0xFFFFFFFD)
            {
                break;
            }

            first = false;
        }

        return (int)count;
    }

    /// <summary>The nodes of a queue's item chain after its head: counted, and with <paramref name="output"/> the
    /// first <paramref name="capacity"/> non-zero item addresses (more than <paramref name="max"/>: 0x80022005).</summary>
    static int UlobjItemNodes(Emulator? process, uint head, uint max, uint capacity, uint output)
    {
        Span<byte> node = stackalloc byte[12];
        uint address = head & 0xFFFFFFF8;
        if (address - 1 >= 0xFFFFFFFE)
        {
            return 0;
        }

        uint count = 0;
        bool first = true;
        while (true)
        {
            int read = UlobjRead(process, address, node);
            if (read != 0)
            {
                return read;
            }

            if (!first)
            {
                uint item = UlobjWord(node, 8);
                if (output != 0 && item != 0)
                {
                    if (count >= max)
                    {
                        return ULOBJ_ERR_TOO_MANY;
                    }

                    if (count < capacity)
                    {
                        GuestMemory.W32(output + count * 4, item);
                    }
                }

                count++;
            }

            address = UlobjWord(node, 4) & 0xFFFFFFF8;
            if (address - 1 > 0xFFFFFFFD)
            {
                break;
            }

            first = false;
        }

        return (int)count;
    }

    /// <summary>The length of a pool's chain after its head (the next words taken as they are), -1 past
    /// <paramref name="limit"/>.</summary>
    static int UlobjChainLength(Emulator? process, uint head, uint limit, out int length)
    {
        Span<byte> node = stackalloc byte[12];
        length = 0;
        uint address = head & 0xFFFFFFF8;
        if (address - 1 >= 0xFFFFFFFE)
        {
            return 0;
        }

        int visited = 0;
        while (true)
        {
            int read = UlobjRead(process, address, node);
            if (read != 0)
            {
                return read;
            }

            uint next = UlobjWord(node, 4);
            if (next - 1 > 0xFFFFFFFD)
            {
                length = visited;
                return 0;
            }

            address = next;
            visited++;
            if (visited == limit + 1)
            {
                length = -1;
                return 0;
            }
        }
    }

    /// <summary>
    /// The non-zero words of an array of <paramref name="count"/> in the process (a runtime's ulthreads), read 256 at
    /// a time: counted into <paramref name="found"/>, or with <paramref name="output"/> the ones after the first
    /// <paramref name="skip"/> written there plus <paramref name="add"/> until <paramref name="found"/> reaches
    /// <paramref name="max"/>.
    /// </summary>
    static int UlobjArrayEntries(Emulator? process, uint count, uint array, uint max, uint output, uint add,
        ref uint skip, ref uint found)
    {
        Span<byte> chunk = stackalloc byte[256 * 4];
        uint remaining = count;
        uint address = array;
        while (remaining != 0)
        {
            uint entries = Math.Min(remaining, 256u);
            Span<byte> words = chunk[..(int)(entries * 4)];
            int read = UlobjRead(process, address, words);
            if (read != 0)
            {
                return read;
            }

            for (int index = 0; index < entries; index++)
            {
                uint entry = UlobjWord(words, index * 4);
                if (entry == 0)
                {
                    continue;
                }

                if (output == 0)
                {
                    found++;
                    continue;
                }

                if (skip != 0)
                {
                    skip--;
                    continue;
                }

                GuestMemory.W32(output + found * 4, entry + add);
                found++;
                if (found >= max)
                {
                    return 0;
                }
            }

            remaining -= entries;
            address += entries * 4;
        }

        return 0;
    }

    /// <summary>The common part of an object's information: +0 its size, +4 the object, +8..+0x20 the name words,
    /// +0x24 the attributes, +0x28 0.</summary>
    static void UlobjInfoHeader(uint info, uint infoSize, uint obj, ReadOnlySpan<byte> data)
    {
        GuestMemory.W32(info, infoSize);
        GuestMemory.W32(info + 4, obj);
        for (int index = 0; index < 7; index++)
        {
            GuestMemory.W32(info + 8 + (uint)index * 4, UlobjWord(data, index * 4));
        }

        GuestMemory.W32(info + 0x24, UlobjWord(data, 0x1C) & 0xFFFFFF);
        GuestMemory.W32(info + 0x28, 0);
    }

    /// <summary>
    /// A mutex's waiters (at most its pool's threads; the first <paramref name="capacity"/> ids to
    /// <paramref name="list"/>, all of them for <see cref="uint.MaxValue"/>) and, with <paramref name="info"/>, its
    /// information: +0 0x32, +0x28 recursive, +0x2C its pool, +0x30 locked, +0x34 the lock count, +0x38 the owner,
    /// +0x3C the waiters. <paramref name="count"/> (if given) gets the ids written. Returns the waiters or an error.
    /// </summary>
    static int UlobjMutexWaiters(Emulator? process, uint obj, uint info, uint capacity, uint list, uint count)
    {
        Span<byte> mutex = stackalloc byte[UlobjMutexSize];
        int read = UlobjReadObject(process, obj, mutex, UlobjMutex);
        if (read != 0)
        {
            return read;
        }

        read = UlobjPoolThreads(process, UlobjWord(mutex, 0x60), out uint threads);
        if (read != 0)
        {
            return read;
        }

        uint room = capacity == uint.MaxValue ? threads : capacity;
        int waiters = UlobjWaiters(process, UlobjWord(mutex, 0x34), 0, 0, threads, room, list);
        if (waiters < 0)
        {
            return waiters;
        }

        if (count != 0)
        {
            GuestMemory.W32(count, Math.Min((uint)waiters, room));
        }

        if (info == 0)
        {
            return waiters;
        }

        UlobjInfoHeader(info, 0x32, obj, mutex);
        GuestMemory.W32(info + 0x28, UlobjWord(mutex, 0x24) & 1);
        GuestMemory.W32(info + 0x2C, UlobjWord(mutex, 0x60));
        GuestMemory.W32(info + 0x34, UlobjWord(mutex, 0x2C));
        if (UlobjWord(mutex, 0x30) == 0)
        {
            GuestMemory.W32(info + 0x30, 0);
            GuestMemory.W32(info + 0x38, 0);
        }
        else
        {
            read = UlobjThreadId(process, UlobjWord(mutex, 0x28), out uint owner);
            if (read != 0)
            {
                return read;
            }

            GuestMemory.W32(info + 0x38, owner);
            GuestMemory.W32(info + 0x30, 1);
        }

        GuestMemory.W32(info + 0x3C, (uint)waiters);
        return waiters;
    }

    /// <summary>NID_525A9BDB(pid, runtime, info): a ulthread runtime's information (its 0x34 bytes: the name words,
    /// +0x20, the ulthread array's size +0x2C and its live entries): NULL info 0x80020005.</summary>
    [Hle(Nid = 0x525A9BDB, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_525A9BDB(int pid, uint obj, uint info)
    {
        if (info == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        Emulator? process = KernelSpace.ProcessOf(Emu, pid);
        Span<byte> runtime = stackalloc byte[UlobjRuntimeSize];
        int read = UlobjRead(process, obj, runtime);
        if (read != 0)
        {
            return read;
        }

        uint skip = 0;
        uint live = 0;
        read = UlobjArrayEntries(process, UlobjWord(runtime, 0x2C), UlobjWord(runtime, 0x30), 0, 0, 0, ref skip, ref live);
        if (read != 0)
        {
            return read;
        }

        for (int index = 0; index < 7; index++)
        {
            GuestMemory.W32(info + (uint)index * 4, UlobjWord(runtime, index * 4));
        }

        GuestMemory.W32(info + 0x1C, UlobjWord(runtime, 0x1C) & 0xFFFFFF);
        GuestMemory.W32(info + 0x20, obj);
        GuestMemory.W32(info + 0x24, UlobjWord(runtime, 0x20));
        GuestMemory.W32(info + 0x28, UlobjWord(runtime, 0x2C));
        GuestMemory.W32(info + 0x2C, live);
        return 0;
    }

    /// <summary>NID_FFC8C811(pid, ulthread, id, info): a ulthread's information (its 0xB8 bytes, the state +0xA0 as
    /// the debugger's state): NULL info 0x80020005.</summary>
    [Hle(Nid = 0xFFC8C811, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_FFC8C811(int pid, uint obj, uint id, uint info)
    {
        if (info == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        Span<byte> thread = stackalloc byte[UlobjThreadSize];
        int read = UlobjRead(KernelSpace.ProcessOf(Emu, pid), obj, thread);
        if (read != 0)
        {
            return read;
        }

        for (int index = 0; index < 7; index++)
        {
            GuestMemory.W32(info + (uint)index * 4, UlobjWord(thread, index * 4));
        }

        ushort state = UlobjHalf(thread, 0xA0);
        GuestMemory.W32(info + 0x1C, UlobjWord(thread, 0x1C) & 0xFFFFFF);
        GuestMemory.W32(info + 0x20, obj);
        GuestMemory.W32(info + 0x24, id);
        GuestMemory.W32(info + 0x28, obj + 0x20);
        GuestMemory.W32(info + 0x2C, state < s_ulobjThreadStates.Length ? s_ulobjThreadStates[state] : 6u);
        GuestMemory.W32(info + 0x30, UlobjWord(thread, 0xA8));
        GuestMemory.W32(info + 0x34, UlobjWord(thread, 0x24) == 0x5AA55AA5 ? UlobjWord(thread, 0x5C) : 0u);
        GuestMemory.W32(info + 0x38, UlobjWord(thread, 0xAC));
        GuestMemory.W32(info + 0x3C, UlobjWord(thread, 0xB0));
        return 0;
    }

    /// <summary>NID_6D2C8928(pid, mutex, info, list, count): a mutex's information and waiters (at most *count ids):
    /// NULL list, info or count, or a negative count 0x80020005; *count becomes the ids written.</summary>
    [Hle(Nid = 0x6D2C8928, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_6D2C8928(int pid, uint obj, uint info, uint list, uint count)
    {
        if (list == 0 || info == 0 || count == 0 || (int)GuestMemory.R32(count) < 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        int waiters = UlobjMutexWaiters(KernelSpace.ProcessOf(Emu, pid), obj, info, GuestMemory.R32(count), list, count);
        return waiters < 0 ? waiters : 0;
    }

    /// <summary>NID_362C9488(pid, cond, info, list, count): a condition variable's information (+0 0xA2, +0x2C its
    /// mutex, +0x30 the waiters) and waiters: as NID_6D2C8928.</summary>
    [Hle(Nid = 0x362C9488, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_362C9488(int pid, uint obj, uint info, uint list, uint count)
    {
        if (list == 0 || info == 0 || count == 0 || (int)GuestMemory.R32(count) < 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        uint capacity = GuestMemory.R32(count);
        int waiters = UlobjCondWaiters(KernelSpace.ProcessOf(Emu, pid), obj, info, capacity, list, out _);
        if (waiters < 0)
        {
            return waiters;
        }

        GuestMemory.W32(count, Math.Min((uint)waiters, capacity));
        return 0;
    }

    /// <summary>A condition variable's waiters (at most its mutex pool's threads, <paramref name="capacity"/> ids to
    /// <paramref name="list"/>) with its information (+0 0xA2): the waiter count or an error.</summary>
    static int UlobjCondWaiters(Emulator? process, uint obj, uint info, uint capacity, uint list, out uint threads)
    {
        Span<byte> cond = stackalloc byte[UlobjCondSize];
        Span<byte> mutex = stackalloc byte[UlobjMutexSize];
        threads = 0;
        int read = UlobjReadObject(process, obj, cond, UlobjCond);
        if (read != 0)
        {
            return read;
        }

        read = UlobjReadObject(process, UlobjWord(cond, 0x40), mutex, UlobjMutex);
        if (read != 0)
        {
            return read;
        }

        read = UlobjPoolThreads(process, UlobjWord(mutex, 0x60), out threads);
        if (read != 0)
        {
            return read;
        }

        int waiters = UlobjWaiters(process, UlobjWord(cond, 0x2C), 0, 0, threads, capacity == uint.MaxValue ? threads : capacity, list);
        if (waiters < 0 || info == 0)
        {
            return waiters;
        }

        UlobjInfoHeader(info, 0xA2, obj, cond);
        GuestMemory.W32(info + 0x2C, UlobjWord(cond, 0x40));
        GuestMemory.W32(info + 0x30, (uint)waiters);
        return waiters;
    }

    /// <summary>A semaphore's waiters with its information (+0 0x12, +0x2C its pool, +0x30 the count, +0x34 the
    /// waiters): the waiter count or an error.</summary>
    static int UlobjSemaWaiters(Emulator? process, uint obj, uint info, uint capacity, uint list, uint waitersAt)
    {
        Span<byte> sema = stackalloc byte[UlobjSemaSize];
        int read = UlobjReadObject(process, obj, sema, UlobjSema);
        if (read != 0)
        {
            return read;
        }

        read = UlobjPoolThreads(process, UlobjWord(sema, 0x68), out uint threads);
        if (read != 0)
        {
            return read;
        }

        int waiters = UlobjWaiters(process, UlobjWord(sema, 0x2C), 0, 0, threads, capacity == uint.MaxValue ? threads : capacity, list);
        if (waiters < 0 || info == 0)
        {
            return waiters;
        }

        UlobjInfoHeader(info, 0x12, obj, sema);
        GuestMemory.W32(info + 0x2C, UlobjWord(sema, 0x68));
        GuestMemory.W32(info + 0x30, UlobjWord(sema, 0x28));
        GuestMemory.W32(info + waitersAt, (uint)waiters);
        return waiters;
    }

    /// <summary>NID_74B9199C(pid, semaphore, info, list, count): a semaphore's information and waiters: as
    /// NID_6D2C8928.</summary>
    [Hle(Nid = 0x74B9199C, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_74B9199C(int pid, uint obj, uint info, uint list, uint count)
    {
        if (list == 0 || info == 0 || count == 0 || (int)GuestMemory.R32(count) < 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        uint capacity = GuestMemory.R32(count);
        int waiters = UlobjSemaWaiters(KernelSpace.ProcessOf(Emu, pid), obj, info, capacity, list, 0x34);
        if (waiters < 0)
        {
            return waiters;
        }

        GuestMemory.W32(count, Math.Min((uint)waiters, capacity));
        return 0;
    }

    /// <summary>NID_6BCB4568(pid, pool, info): a waiting-thread pool's information: NULL info 0x80020005.</summary>
    [Hle(Nid = 0x6BCB4568, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_6BCB4568(int pid, uint obj, uint info)
    {
        if (info == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        int result = UlobjWaitingPoolInfo(KernelSpace.ProcessOf(Emu, pid), obj, info);
        return result < 0 ? result : 0;
    }

    /// <summary>NID_ADAAA961(pid, pool, info): a data pool's information: NULL info 0x80020005.</summary>
    [Hle(Nid = 0xADAAA961, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_ADAAA961(int pid, uint obj, uint info)
    {
        if (info == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        int result = UlobjDataPoolInfo(KernelSpace.ProcessOf(Emu, pid), obj, info);
        return result < 0 ? result : 0;
    }

    /// <summary>A waiting-thread pool's information (+0 0x43, +0x28 its threads, +0x2C the waiting ones, +0x30 its
    /// synchronisation slots, +0x34 the free ones; -1 for a chain past its bound): 0x38 or an error.</summary>
    static int UlobjWaitingPoolInfo(Emulator? process, uint obj, uint info)
    {
        Span<byte> pool = stackalloc byte[UlobjWaitingPoolSize];
        int read = UlobjReadObject(process, obj, pool, UlobjWaitingPool);
        if (read != 0)
        {
            return read;
        }

        uint threads = Math.Min(UlobjWord(pool, 0x24), UlobjMaxPoolThreads);
        read = UlobjChainLength(process, UlobjWord(pool, 0x34), threads, out int waiting);
        if (read != 0)
        {
            return read;
        }

        uint slots = UlobjWord(pool, 0x28);
        read = UlobjChainLength(process, UlobjWord(pool, 0x4C), Math.Min(slots, 0x100000u), out int used);
        if (read != 0)
        {
            return read;
        }

        UlobjInfoHeader(info, 0x43, obj, pool);
        GuestMemory.W32(info + 0x28, UlobjWord(pool, 0x24));
        GuestMemory.W32(info + 0x2C, (uint)waiting);
        GuestMemory.W32(info + 0x30, slots);
        GuestMemory.W32(info + 0x34, used == -1 ? uint.MaxValue : slots - (uint)used);
        return 0x38;
    }

    /// <summary>A data pool's information (+0 0x42, +0x2C its items, +0x30 the queues on it, +0x34 its +0xA4, +0x38 its
    /// data slots, +0x3C the free ones; -1 past the bound): 0x40 or an error.</summary>
    static int UlobjDataPoolInfo(Emulator? process, uint obj, uint info)
    {
        Span<byte> pool = stackalloc byte[UlobjDataPoolSize];
        int read = UlobjReadObject(process, obj, pool, UlobjDataPool);
        if (read != 0)
        {
            return read;
        }

        uint items = UlobjWord(pool, 0x24);
        int queues = UlobjDataNodes(process, UlobjWord(pool, 0x34), Math.Min(items, 0x1555555u), 0, 0, 0);
        if (queues < 0)
        {
            return queues;
        }

        uint slots = UlobjWord(pool, 0x28);
        read = UlobjChainLength(process, UlobjWord(pool, 0x4C), Math.Min(slots, 0x100000u), out int used);
        if (read != 0)
        {
            return read;
        }

        UlobjInfoHeader(info, 0x42, obj, pool);
        GuestMemory.W32(info + 0x2C, items);
        GuestMemory.W32(info + 0x30, (uint)queues);
        GuestMemory.W32(info + 0x34, UlobjWord(pool, 0xA4));
        GuestMemory.W32(info + 0x38, slots);
        GuestMemory.W32(info + 0x3C, used == -1 ? uint.MaxValue : slots - (uint)used);
        return 0x40;
    }

    /// <summary>
    /// A reader/writer lock's waiting readers and writers (flags 1 and 2 in the chain; with <paramref name="bounded"/>
    /// at most <paramref name="capacity"/> ids in all, the readers first, their counts to <paramref name="readers"/>
    /// and <paramref name="writers"/>; else every id) and, with <paramref name="info"/>, its information: +0 0x92,
    /// +0x2C its pool, +0x30 the state (1 write-locked, 2 read-locked), +0x34 the readers holding it, +0x38/+0x3C the
    /// waiting readers and writers. The writer holding it goes to <paramref name="owner"/> (0 when not write-locked; a
    /// failed read of its id is the error) or, with <paramref name="ownerAfterList"/>, after the ids (a failed read
    /// leaves the word).
    /// </summary>
    static int UlobjRwLockWaiters(Emulator? process, uint obj, uint info, uint capacity, uint list, bool bounded,
        uint readers, uint writers, uint owner, bool ownerAfterList, out uint waitingReaders, out uint waitingWriters)
    {
        Span<byte> rwlock = stackalloc byte[UlobjRwLockSize];
        waitingReaders = 0;
        waitingWriters = 0;
        int read = UlobjReadObject(process, obj, rwlock, UlobjRwLock);
        if (read != 0)
        {
            return read;
        }

        read = UlobjPoolThreads(process, UlobjWord(rwlock, 0x68), out uint threads);
        if (read != 0)
        {
            return read;
        }

        uint room = bounded ? capacity : threads;
        int readerWalk = UlobjWaiters(process, UlobjWord(rwlock, 0x2C), 1, 0, threads, room, list);
        if (readerWalk < 0)
        {
            return readerWalk;
        }

        waitingReaders = (uint)readerWalk;
        uint readersWritten = bounded ? Math.Min(waitingReaders, room) : waitingReaders;
        if (bounded)
        {
            room -= readersWritten;
            GuestMemory.W32(readers, readersWritten);
        }

        uint writerList = list == 0 ? 0 : list + readersWritten * 4;
        int writerWalk = UlobjWaiters(process, UlobjWord(rwlock, 0x2C), 2, 0, threads, room, writerList);
        if (writerWalk < 0)
        {
            return writerWalk;
        }

        waitingWriters = (uint)writerWalk;
        if (bounded)
        {
            GuestMemory.W32(writers, Math.Min(waitingWriters, room));
        }

        if (info == 0)
        {
            return 0;
        }

        bool writeLocked = rwlock[0x28] != 0;
        ushort holders = UlobjHalf(rwlock, 0x2A);
        UlobjInfoHeader(info, 0x92, obj, rwlock);
        GuestMemory.W32(info + 0x2C, UlobjWord(rwlock, 0x68));
        GuestMemory.W32(info + 0x30, (holders != 0 ? 2u : 0u) | (writeLocked ? 1u : 0u));
        GuestMemory.W32(info + 0x34, holders);
        GuestMemory.W32(info + 0x38, waitingReaders);
        GuestMemory.W32(info + 0x3C, waitingWriters);
        uint ownerAt = ownerAfterList ? writerList + waitingWriters * 4 : owner;
        if (ownerAt == 0)
        {
            return 0;
        }

        if (!writeLocked)
        {
            GuestMemory.W32(ownerAt, 0);
            return 0;
        }

        read = UlobjThreadId(process, UlobjWord(rwlock, 0x24), out uint holder);
        if (read != 0)
        {
            return ownerAfterList ? 0 : read;
        }

        GuestMemory.W32(ownerAt, holder);
        return 0;
    }

    /// <summary>NID_3685D4FB(pid, rwlock, info, max, list, readers, writers): a reader/writer lock's information and
    /// its waiting readers then writers (at most max ids): NULLs 0x80020005.</summary>
    [Hle(Nid = 0x3685D4FB, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_3685D4FB(int pid, uint obj, uint info, uint max, uint list, uint readers, uint writers)
    {
        if (list == 0 || info == 0 || writers == 0 || readers == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        return UlobjRwLockWaiters(KernelSpace.ProcessOf(Emu, pid), obj, info, max, list, true, readers, writers, 0,
            false, out _, out _);
    }

    /// <summary>NID_0C29AFEC(pid, rwlock, info, owner, max, list, readers, writers): as NID_3685D4FB with the writer
    /// holding it (0 when not write-locked).</summary>
    [Hle(Nid = 0x0C29AFEC, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_0C29AFEC(int pid, uint obj, uint info, uint owner, uint max, uint list, uint readers, uint writers)
    {
        if (owner == 0 || info == 0 || readers == 0 || list == 0 || writers == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        return UlobjRwLockWaiters(KernelSpace.ProcessOf(Emu, pid), obj, info, max, list, true, readers, writers, owner,
            false, out _, out _);
    }

    /// <summary>A queue's objects: its data pool (+0x88) and that pool's waiting pool (+0xA8), its own waiting pool
    /// (+0x8C).</summary>
    static int UlobjQueueParts(Emulator? process, uint obj, Span<byte> queue, Span<byte> dataPool,
        out uint dataThreads, out uint queueThreads)
    {
        dataThreads = 0;
        queueThreads = 0;
        int read = UlobjReadObject(process, obj, queue, UlobjQueue);
        if (read != 0)
        {
            return read;
        }

        read = UlobjReadObject(process, UlobjWord(queue, 0x88), dataPool, UlobjDataPool);
        if (read != 0)
        {
            return read;
        }

        read = UlobjPoolThreads(process, UlobjWord(dataPool, 0xA8), out dataThreads);
        if (read != 0)
        {
            return read;
        }

        return UlobjPoolThreads(process, UlobjWord(queue, 0x8C), out queueThreads);
    }

    /// <summary>A queue's information: +0 0x52, +0x2C its waiting pool, +0x30 its data pool, +0x34 the item size,
    /// +0x38 the senders waiting in the data pool, +0x3C the receivers, +0x40 the items.</summary>
    static void UlobjQueueInfo(uint info, uint obj, ReadOnlySpan<byte> queue, uint senders, uint receivers, uint items)
    {
        UlobjInfoHeader(info, 0x52, obj, queue);
        GuestMemory.W32(info + 0x2C, UlobjWord(queue, 0x8C));
        GuestMemory.W32(info + 0x30, UlobjWord(queue, 0x88));
        GuestMemory.W32(info + 0x34, UlobjWord(queue, 0x90));
        GuestMemory.W32(info + 0x38, senders);
        GuestMemory.W32(info + 0x3C, receivers);
        GuestMemory.W32(info + 0x40, items);
    }

    /// <summary>NID_5D7A7925(pid, queue, info, max, list, senders, receivers, items): a queue's information with the
    /// threads waiting to send, to receive, then its items (at most max words): NULLs 0x80020005.</summary>
    [Hle(Nid = 0x5D7A7925, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_5D7A7925(int pid, uint obj, uint info, uint max, uint list, uint senders, uint receivers, uint items)
    {
        if (list == 0 || info == 0 || receivers == 0 || senders == 0 || items == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        Emulator? process = KernelSpace.ProcessOf(Emu, pid);
        Span<byte> queue = stackalloc byte[UlobjQueueSize];
        Span<byte> dataPool = stackalloc byte[UlobjDataPoolSize];
        int read = UlobjQueueParts(process, obj, queue, dataPool, out uint dataThreads, out uint queueThreads);
        if (read != 0)
        {
            return read;
        }

        int sending = UlobjWaiters(process, UlobjWord(dataPool, 0x84), 0, obj, dataThreads, max, list);
        if (sending < 0)
        {
            return sending;
        }

        uint written = Math.Min((uint)sending, max);
        list += written * 4;
        max -= written;
        GuestMemory.W32(senders, written);
        int receiving = UlobjWaiters(process, UlobjWord(queue, 0x2C), 0, 0, queueThreads, max, list);
        if (receiving < 0)
        {
            return receiving;
        }

        written = Math.Min((uint)receiving, max);
        max -= written;
        GuestMemory.W32(receivers, written);
        uint itemLimit = Math.Min(UlobjWord(dataPool, 0x24), 0x1555555u);
        int queued = UlobjItemNodes(process, UlobjWord(queue, 0x4C), itemLimit, max, list + written * 4);
        if (queued < 0)
        {
            return queued;
        }

        GuestMemory.W32(items, Math.Min((uint)queued, max));
        UlobjQueueInfo(info, obj, queue, (uint)sending, (uint)receiving, (uint)queued);
        return 0;
    }

    /// <summary>A queue's whole dump (NID_B978DB70 kind 0x15): the information, then from +0x44 the sender and
    /// receiver ids and the items' data (each its size rounded to 4); or only the size without
    /// <paramref name="info"/>.</summary>
    static int UlobjQueueDump(Emulator? process, uint obj, uint info)
    {
        Span<byte> queue = stackalloc byte[UlobjQueueSize];
        Span<byte> dataPool = stackalloc byte[UlobjDataPoolSize];
        int read = UlobjQueueParts(process, obj, queue, dataPool, out uint dataThreads, out uint queueThreads);
        if (read != 0)
        {
            return read;
        }

        uint list = info == 0 ? 0 : info + 0x44;
        int sending = UlobjWaiters(process, UlobjWord(dataPool, 0x84), 0, obj, dataThreads, dataThreads, list);
        if (sending < 0)
        {
            return sending;
        }

        uint receiversAt = list == 0 ? 0 : list + (uint)sending * 4;
        int receiving = UlobjWaiters(process, UlobjWord(queue, 0x2C), 0, 0, queueThreads, queueThreads, receiversAt);
        if (receiving < 0)
        {
            return receiving;
        }

        uint itemSize = UlobjWord(queue, 0x90);
        uint stride = (itemSize + 3) & 0xFFFFFFFC;
        uint itemsAt = list == 0 ? 0 : receiversAt + (uint)receiving * 4;
        uint itemLimit = Math.Min(UlobjWord(dataPool, 0x24), 0x1555555u);
        int queued = UlobjDataNodes(process, UlobjWord(queue, 0x4C), itemLimit, itemSize, stride, itemsAt);
        if (queued < 0)
        {
            return queued;
        }

        if (info != 0)
        {
            UlobjQueueInfo(info, obj, queue, (uint)sending, (uint)receiving, (uint)queued);
        }

        return (int)(stride * (uint)queued + ((uint)sending + 0x11 + (uint)receiving) * 4);
    }

    /// <summary>NID_3D6E2EF3(pid, runtime, list, max, skip): a ulthread runtime's live ulthreads after the first
    /// <paramref name="skip"/> (at most max to the list; NULL list: counted): their count.</summary>
    [Hle(Nid = 0x3D6E2EF3, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_3D6E2EF3(int pid, uint obj, uint list, uint max, uint skip)
    {
        Emulator? process = KernelSpace.ProcessOf(Emu, pid);
        Span<byte> runtime = stackalloc byte[UlobjRuntimeSize];
        int read = UlobjRead(process, obj, runtime);
        if (read != 0)
        {
            return read;
        }

        uint found = 0;
        if (list != 0 && found >= max)
        {
            return 0;
        }

        read = UlobjArrayEntries(process, UlobjWord(runtime, 0x2C), UlobjWord(runtime, 0x30), max, list, 0, ref skip, ref found);
        return read != 0 ? read : (int)found;
    }

    /// <summary>NID_8E200387(pid, context, info): a ulthread's saved context block (0x80 bytes: +0 the XOR of the words
    /// +8..+0x7C, +4 0x5AA55AA5 or 0x5A5A5A5A with +0x38 an extension of 0x70 bytes; the name XOR-keyed at +0x18):
    /// NULL info, a bad magic or checksum 0x80020005.</summary>
    [Hle(Nid = 0x8E200387, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_8E200387(int pid, uint obj, uint info)
    {
        if (info == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        Emulator? process = KernelSpace.ProcessOf(Emu, pid);
        Span<byte> context = stackalloc byte[UlobjContextSize];
        Span<byte> extra = stackalloc byte[UlobjContextExtraSize];
        int read = UlobjRead(process, obj, context);
        if (read != 0)
        {
            return read;
        }

        uint magic = UlobjWord(context, 4);
        uint form;
        if (magic == 0x5A5A5A5A)
        {
            uint checksum = 0;
            for (int offset = 8; offset < UlobjContextSize; offset += 4)
            {
                checksum ^= UlobjWord(context, offset);
            }

            if (checksum != UlobjWord(context, 0))
            {
                return ULOBJ_ERR_ARGUMENT;
            }

            uint extension = UlobjWord(context, 0x38);
            form = 0;
            extra.Clear();
            if (extension != 0)
            {
                read = UlobjRead(process, extension, extra);
                if (read != 0)
                {
                    return read;
                }

                form = 2;
            }
        }
        else if (magic == 0x5AA55AA5)
        {
            form = 1;
            extra.Clear();
        }
        else
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        Span<byte> name = stackalloc byte[32];
        for (int index = 0; index < 6; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(name[(index * 4)..], UlobjWord(context, 0x18 + index * 4) ^ s_ulobjContextKeys[index + 2]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(name[24..], UlobjWord(context, 0x30) ^ s_ulobjContextKeys[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(name[28..], (UlobjWord(context, 0x34) ^ s_ulobjContextKeys[1]) & 0xFFFFFF);
        int length = name.IndexOf((byte)0);
        Span<byte> output = GuestMemory.Span(info, 0x20);
        output.Clear();
        name[..length].CopyTo(output);

        GuestMemory.W32(info + 0x20, obj);
        GuestMemory.W32(info + 0x24, form);
        GuestMemory.W32(info + 0x28, UlobjWord(context, 0x10));
        GuestMemory.W32(info + 0x2C, UlobjWord(context, 0x14));
        GuestMemory.W32(info + 0x30, UlobjWord(context, 8) ^ 0x963C5AA5);
        GuestMemory.W32(info + 0x34, magic == 0x5AA55AA5 ? UlobjWord(context, 0x3C) : 0u);
        GuestMemory.W32(info + 0x38, UlobjWord(extra, 0x68));
        GuestMemory.W32(info + 0x3C, UlobjWord(extra, 4));
        GuestMemory.W32(info + 0x40, UlobjWord(extra, 8));
        GuestMemory.W32(info + 0x44, UlobjWord(extra, 0xC));
        GuestMemory.W32(info + 0x48, UlobjWord(extra, 0x10));
        GuestMemory.W32(info + 0x4C, UlobjWord(extra, 0x14));
        GuestMemory.W32(info + 0x50, UlobjWord(extra, 0x18));
        GuestMemory.W32(info + 0x54, UlobjWord(extra, 0x1C));
        GuestMemory.W32(info + 0x58, UlobjWord(extra, 0x20));
        GuestMemory.W32(info + 0x5C, UlobjWord(extra, 0));
        GuestMemory.W32(info + 0x60, UlobjWord(extra, 0x24));
        for (uint offset = 0; offset < 0x30; offset += 4)
        {
            GuestMemory.W32(info + 0x68 + offset, UlobjWord(extra, 0x28 + (int)offset));
        }

        for (uint offset = 0; offset < 0x10; offset += 4)
        {
            GuestMemory.W32(info + 0x98 + offset, UlobjWord(extra, 0x58 + (int)offset));
        }

        return 0;
    }

    /// <summary>NID_BB11EB2F(pid, list, max, skip, kind): the process's objects of a kind from its table, after the
    /// first <paramref name="skip"/> (NULL list: counted): 1 ulthreads (the table's and each runtime's, +0x20), 2 the
    /// runtimes' entries, 3 the runtimes, 0x11..0x17 the synchronisation objects of kind 1..7; another kind
    /// 0x80020005, no record 0. Returns the count.</summary>
    [Hle(Nid = 0xBB11EB2F, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_BB11EB2F(int pid, uint list, uint max, uint skip, uint kind)
    {
        bool known = kind is 1 or 2 or 3 || kind - 0x11 < 7;
        if (!known)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        if (UlobjStarted(pid) is not { } target)
        {
            return 0;
        }

        Emulator? process = KernelSpace.ProcessOf(Emu, pid);
        Span<byte> runtime = stackalloc byte[UlobjRuntimeSize];
        Span<byte> header = stackalloc byte[0x28];
        uint found = 0;
        for (uint index = 0; index < target._ulobjTableSize; index++)
        {
            uint entry = GuestMemory.R32(target._ulobjTable + UlobjTableHeader + index * 4);
            if ((entry & 4) == 0)
            {
                continue;
            }

            uint address = entry & 0xFFFFFFF8;
            uint tag = entry & 3;
            if (kind == 1 && tag == 0 || kind == 3 && tag == 1)
            {
                if (list == 0)
                {
                    found++;
                    continue;
                }

                if (skip != 0)
                {
                    skip--;
                    continue;
                }

                GuestMemory.W32(list + found * 4, address);
                found++;
                if (found >= max)
                {
                    break;
                }

                continue;
            }

            if (kind is 1 or 2 && tag == 1)
            {
                if (UlobjRead(process, address, runtime) == 0)
                {
                    if (list != 0 && found >= max)
                    {
                        break;
                    }

                    uint add = kind == 1 ? 0x20u : 0u;
                    UlobjArrayEntries(process, UlobjWord(runtime, 0x2C), UlobjWord(runtime, 0x30), max, list, add, ref skip, ref found);
                }

                if (list != 0 && found >= max)
                {
                    break;
                }

                continue;
            }

            if (kind >= 0x11 && tag == 2)
            {
                if (UlobjRead(process, address, header) != 0 || header[0x22] != kind - 0x10)
                {
                    continue;
                }

                if (list == 0)
                {
                    found++;
                    continue;
                }

                if (skip != 0)
                {
                    skip--;
                    continue;
                }

                GuestMemory.W32(list + found * 4, address);
                found++;
                if (found >= max)
                {
                    break;
                }
            }
        }

        return (int)found;
    }

    /// <summary>NID_CDAD91AD(pid, object, kind): the size NID_B978DB70 needs for an object of a kind (0x11..0x17): the
    /// information and the ids and data it lists; another kind or object 0x8002000C.</summary>
    [Hle(Nid = 0xCDAD91AD, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_CDAD91AD(int pid, uint obj, uint kind)
    {
        return UlobjDump(KernelSpace.ProcessOf(Emu, pid), obj, kind, 0);
    }

    /// <summary>NID_B978DB70(pid, object, kind, info): an object's information with its ids (and a queue's data) after
    /// it, by kind 0x11..0x17: the size written; NULL info 0x80020005, another kind or object 0x8002000C.</summary>
    [Hle(Nid = 0xB978DB70, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_B978DB70(int pid, uint obj, uint kind, uint info)
    {
        if (info == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        return UlobjDump(KernelSpace.ProcessOf(Emu, pid), obj, kind, info);
    }

    /// <summary>The dump of one object (NID_B978DB70), or its size alone without <paramref name="info"/>
    /// (NID_CDAD91AD).</summary>
    static int UlobjDump(Emulator? process, uint obj, uint kind, uint info)
    {
        switch (kind)
        {
            case 0x11:
            {
                uint list = info == 0 ? 0 : info + 0x40;
                int waiters = UlobjMutexWaiters(process, obj, info, uint.MaxValue, list, 0);
                return waiters < 0 ? waiters : (waiters + 0x10) * 4;
            }

            case 0x12:
            {
                uint list = info == 0 ? 0 : info + 0x34;
                int waiters = UlobjCondWaiters(process, obj, info, uint.MaxValue, list, out _);
                return waiters < 0 ? waiters : (waiters + 0xD) * 4;
            }

            case 0x13:
            {
                uint list = info == 0 ? 0 : info + 0x38;
                int waiters = UlobjSemaWaiters(process, obj, info, uint.MaxValue, list, 0x34);
                return waiters < 0 ? waiters : (waiters + 0xE) * 4;
            }

            case 0x14:
            {
                uint list = info == 0 ? 0 : info + 0x40;
                int result = UlobjRwLockWaiters(process, obj, info, 0, list, false, 0, 0, 0, true,
                    out uint readers, out uint writers);
                return result < 0 ? result : (int)((readers + 0x11 + writers) * 4);
            }

            case 0x15:
                return UlobjQueueDump(process, obj, info);
            case 0x16:
                return info == 0 ? 0x38 : UlobjWaitingPoolInfo(process, obj, info);
            case 0x17:
                return info == 0 ? 0x40 : UlobjDataPoolInfo(process, obj, info);
            default:
                return ULOBJ_ERR_NO_OBJECT;
        }
    }
}
