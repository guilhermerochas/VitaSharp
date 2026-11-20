using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSysmemForKernel's heaps as the FW 3.74 sysmem.skprx has them (decompiled, study only): the UID entry heaps (a
/// process's table of handles: SceUIDEntryHeapClass objects of 0xB0 bytes, +0xA8 the pid, +0xAC the heap), the fixed
/// heaps of 0x20..0x200-byte items (SceUIDFixedHeapClass; +0x20 the item size, +0x24 the items in use), the name heap
/// that keeps one copy of each object name (nodes of 0x30 bytes: +4 the references, +0x10 the name), the heap
/// information calls and the "simple" memory blocks, the boot-time initialisers (VitaSharp's sysmem is ready before
/// any kernel code runs) and the sysmem words other modules read. The items of every heap are VitaSharp's kernel
/// allocations (ksceKernelAlloc), so they grow as needed and their sizes are what the information calls report.
/// </summary>
public sealed partial class SysmemKernel
{
    const uint UidEntryHeapSize = 0xB0, FixedHeapSize = 0xA4, NameNodeSize = 0x30;
    const int NameNotFound = unchecked((int)0x80024000);
    const int ReferenceNotFound = unchecked((int)0x80024701);

    /// <summary>The name heap: name -> its node.</summary>
    [SnapshotMissing] // states of builds before it: no name had been kept
    readonly Dictionary<string, uint> _nameNodes = new();

    /// <summary>The items of the fixed heaps: item address -> fixed heap record.</summary>
    [SnapshotMissing] // states of builds before it: none had been handed out
    readonly Dictionary<uint, uint> _fixedHeapItems = new();

    // ------------------------------------------------------------------ UID entry heaps

    uint EntryHeapClass() => SysmemClass("SceUIDEntryHeapClass", UidEntryHeapSize);

    /// <summary>A UID entry heap record for process <paramref name="pid"/> on heap object <paramref name="heap"/>.</summary>
    uint MakeEntryHeapRecord(int pid, uint heap)
    {
        uint record = KernelAlloc(UidEntryHeapSize, "kernel:uidentryheap", 0x10);
        GuestMemory.Span(record, (int)UidEntryHeapSize).Clear();
        GuestMemory.W32(record + 0xA4, 0xFFFFFFFF);
        GuestMemory.W32(record + 0xA8, (uint)pid);
        GuestMemory.W32(record + 0xAC, heap);
        return record;
    }

    /// <summary>The pid of a UID entry heap (by its UID); 0x10003 is the kernel's GUID entry heap.</summary>
    int EntryHeapLookup(int uid, out int pid)
    {
        pid = 0;
        if (uid == 0x10003)
        {
            pid = KernelSpace.KernelPid;
            return 0;
        }

        if (!IsGuidForm(uid) || K.Objects.Get<KernelObject>(uid) is not { } kernelObject)
        {
            return SceError.INVALID_UID;
        }

        uint record = BodyOf(kernelObject);
        pid = (int)GuestMemory.R32(record + 0xA8);
        return 0;
    }

    /// <summary>NID_857408DA(): the kernel's GUID entry heap (SceGUIDEntryHeap).</summary>
    [Hle(Nid = 0x857408DA, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_857408DA() => SysmemRecord("SceGUIDEntryHeap", () => MakeEntryHeapRecord(KernelSpace.KernelPid, 0));

    /// <summary>NID_791D6205(): the UID entry heap processmgr gives shared processes (on SceKernelHeapForShared).</summary>
    [Hle(Nid = 0x791D6205, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_791D6205() => SysmemRecord("SceKernelHeapForSharedEntryHeap", () => MakeEntryHeapRecord(KernelSpace.KernelPid, 0));

    /// <summary>NID_89BEF058(pid, heapUid): the process's UID entry heap ("SceUIDEntryHeap", attr 0x30000) on the heap;
    /// a pid that is no process 0x80029001, a heap that is no object its lookup error; the UID.</summary>
    [Hle(Nid = 0x89BEF058, Library = "SceSysmemForKernel")]
    int NID_89BEF058(int pid, int heapUid)
    {
        if (ProcessOf(pid) == null)
        {
            return SceError.INVALID_PID;
        }

        if (!IsGuidForm(heapUid) || K.Objects.Get<KernelObject>(heapUid) is not { } heap)
        {
            return SceError.INVALID_UID;
        }

        uint record = MakeEntryHeapRecord(pid, BodyOf(heap));
        return RegisterExistingObject(EntryHeapClass(), "SceUIDEntryHeap", record, UidEntryHeapSize);
    }

    /// <summary>NID_F6D09BD9(uid): deletes a UID entry heap; 0.</summary>
    [Hle(Nid = 0xF6D09BD9, Library = "SceSysmemForKernel")]
    int NID_F6D09BD9(int uid)
    {
        if (!IsGuidForm(uid) || K.Objects.Get<KernelObject>(uid) is null)
        {
            return SceError.INVALID_UID;
        }

        K.Objects.Remove(uid);
        return 0;
    }

    /// <summary>UIDEntryHeapCloseAllPUID (0x133024A5)(uid): closes every handle the entry heap's process holds; 0.</summary>
    [Hle(Nid = 0x133024A5, Library = "SceSysmemForKernel")]
    int NID_133024A5(int uid)
    {
        int lookup = EntryHeapLookup(uid, out int pid);
        if (lookup < 0)
        {
            return lookup;
        }

        if (pid == KernelSpace.KernelPid || ProcessOf(pid) == null)
        {
            return 0;
        }

        List<KernelObject> handles = new(K.Objects.All<KernelObject>().Where(kernelObject => kernelObject.OwnerPid == pid && kernelObject is not ProcessObject && kernelObject is not GuestThread));
        foreach (KernelObject handle in handles)
        {
            kscePUIDClose(pid, handle.Uid);
        }

        return 0;
    }

    /// <summary>ksceKernelUIDEntryHeapGetInfo (0xCCD47B97)(uid, info): the entry heap's information (0x18 bytes: +4 the
    /// entry size, +8 its bytes, +0xC the most entries used, +0x10 the entries in use, +0x14 the free ones); 0x10003 the
    /// kernel's; NULL or another size 0x80020005.</summary>
    [Hle(Nid = 0xCCD47B97, Library = "SceSysmemForKernel")]
    int ksceKernelUIDEntryHeapGetInfo(int uid, uint info)
    {
        int lookup = EntryHeapLookup(uid, out int pid);
        if (lookup < 0)
        {
            return lookup;
        }

        if (info == 0 || GuestMemory.R32(info) != 0x18)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint used = 0;
        foreach (KernelObject kernelObject in K.Objects.All<KernelObject>())
        {
            if (OwnerOf(kernelObject) == pid)
            {
                used++;
            }
        }

        uint bytes = (used * 0x20 + 0xFFF) & ~0xFFFu;
        GuestMemory.W32(info + 0x04, 0x20);
        GuestMemory.W32(info + 0x08, bytes);
        GuestMemory.W32(info + 0x0C, used);
        GuestMemory.W32(info + 0x10, used);
        GuestMemory.W32(info + 0x14, bytes / 0x20 - used);
        return 0;
    }

    /// <summary>NID_9D8F3BE8(pid, partition, allocator, SceUIDEntryHeap **out): a process's UID entry heap record (its
    /// fixed heaps of 0x10..0x200-byte items); a NULL output 0x80020005, a pid that is no process 0x80029001.</summary>
    [Hle(Nid = 0x9D8F3BE8, Library = "SceSysmemForKernel")]
    int NID_9D8F3BE8(int pid, uint partition, uint allocator, uint recordOut)
    {
        if (recordOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (ProcessOf(pid) == null)
        {
            return SceError.INVALID_PID;
        }

        uint record = MakeEntryHeapRecord(pid, 0);
        GuestMemory.W32(record + 0x10, allocator);
        GuestMemory.W32(recordOut, record);
        return 0;
    }

    /// <summary>NID_2F526AB8(SceUIDEntryHeap *record): gives the record's fixed heaps and the record back; 0.</summary>
    [Hle(Nid = 0x2F526AB8, Library = "SceSysmemForKernel")]
    int NID_2F526AB8(uint record)
    {
        List<uint> items;
        lock (_fixedHeapItems)
        {
            items = _fixedHeapItems.Where(item => item.Value == record).Select(item => item.Key).ToList();
            foreach (uint item in items)
            {
                _fixedHeapItems.Remove(item);
            }
        }

        foreach (uint item in items)
        {
            ksceKernelFree(item);
        }

        return 0;
    }

    /// <summary>NID_660DC18A(SceUIDEntryHeap *record, name, heapUid): moves the record's fixed heaps onto the heap
    /// (+0x14); a NULL heap object 0x80020005; 0.</summary>
    [Hle(Nid = 0x660DC18A, Library = "SceSysmemForKernel")]
    int NID_660DC18A(uint record, uint name, int heapUid)
    {
        if (!IsGuidForm(heapUid) || K.Objects.Get<KernelObject>(heapUid) is not { } heap)
        {
            return SceError.INVALID_UID;
        }

        GuestMemory.W32(record + 0x14, BodyOf(heap));
        return 0;
    }

    /// <summary>NID_067F2550(SceUIDEntryHeap *record, count, sizes[], ?): sets the record's size classes from the
    /// {size, count} pairs (each slot's item size into the record's table); 0.</summary>
    [Hle(Nid = 0x067F2550, Library = "SceSysmemForKernel")]
    int NID_067F2550(uint record, uint count, uint pairs, uint unused)
    {
        for (uint i = 0; i < count && i < 6; i++)
        {
            GuestMemory.W32(record + 0x24 + i * 0x1C, GuestMemory.R32(pairs + i * 8));
        }

        return 0;
    }

    /// <summary>NID_DFA1EB57(pid, ?, slot, size, count): NID_067F2550 for one slot of the process's entry heap (the
    /// kernel's for a process without one); 0.</summary>
    [Hle(Nid = 0xDFA1EB57, Library = "SceSysmemForKernel")]
    int NID_DFA1EB57(int pid, uint unused, uint slot, uint size, uint count)
    {
        if (slot < 6)
        {
            GuestMemory.W32(NID_857408DA() + 0x24 + slot * 0x1C, size);
        }

        return 0;
    }

    /// <summary>NID_EBFC8E10(SceUIDEntryHeap *record, size): an item of the smallest size class that holds
    /// <paramref name="size"/> bytes (NULL when none).</summary>
    [Hle(Nid = 0xEBFC8E10, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_EBFC8E10(uint record, uint size)
    {
        if (size == 0 || size > 0x200)
        {
            return 0;
        }

        uint item = ksceKernelAlloc(size);
        if (item != 0)
        {
            lock (_fixedHeapItems)
            {
                _fixedHeapItems[item] = record;
            }
        }

        return item;
    }

    /// <summary>NID_17D3D4BD(SceUIDEntryHeap *record, size, item, ?): gives an NID_EBFC8E10 item back.</summary>
    [Hle(Nid = 0x17D3D4BD, Library = "SceSysmemForKernel")]
    int NID_17D3D4BD(uint record, uint size, uint item, uint unused)
    {
        lock (_fixedHeapItems)
        {
            _fixedHeapItems.Remove(item);
        }

        return ksceKernelFree(item);
    }

    // ------------------------------------------------------------------ fixed heaps

    /// <summary>The fixed heap of <paramref name="itemSize"/>-byte items ("SceKernelFixedHeap&lt;n&gt;B").</summary>
    uint FixedHeap(uint itemSize)
    {
        return SysmemRecord($"SceKernelFixedHeap{itemSize}B", () =>
        {
            uint cls = SysmemClass("SceUIDFixedHeapClass", FixedHeapSize);
            uint record = KernelAlloc(FixedHeapSize, "kernel:fixedheap", 0x10);
            GuestMemory.Span(record, (int)FixedHeapSize).Clear();
            GuestMemory.Write(record + 0x20, (ushort)itemSize);
            RegisterExistingObject(cls, $"SceKernelFixedHeap{itemSize}B", record, FixedHeapSize);
            return record;
        });
    }

    /// <summary>NID_3717C322(size): sysmem's fixed heap of 0x20, 0x30, 0x40, 0x80, 0x100 or 0x200-byte items; another
    /// size NULL.</summary>
    [Hle(Nid = 0x3717C322, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_3717C322(uint size)
    {
        return size is 0x20 or 0x30 or 0x40 or 0x80 or 0x100 or 0x200 ? FixedHeap(size) : 0;
    }

    /// <summary>NID_ABA5313C(SceKernelFixedHeap *heap): one item of the heap (NULL when none).</summary>
    [Hle(Nid = 0xABA5313C, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_ABA5313C(uint heap)
    {
        if (heap == 0)
        {
            return 0;
        }

        uint item = ksceKernelAlloc(Math.Max(GuestMemory.Read<ushort>(heap + 0x20), (ushort)4));
        if (item != 0)
        {
            lock (_fixedHeapItems)
            {
                _fixedHeapItems[item] = heap;
            }

            GuestMemory.Write(heap + 0x24, (ushort)(GuestMemory.Read<ushort>(heap + 0x24) + 1));
        }

        return item;
    }

    /// <summary>NID_93344755(SceKernelFixedHeap *heap, item): gives an item back; 0.</summary>
    [Hle(Nid = 0x93344755, Library = "SceSysmemForKernel")]
    int NID_93344755(uint heap, uint item)
    {
        bool owned;
        lock (_fixedHeapItems)
        {
            owned = _fixedHeapItems.Remove(item);
        }

        if (owned && heap != 0)
        {
            ushort used = GuestMemory.Read<ushort>(heap + 0x24);
            GuestMemory.Write(heap + 0x24, (ushort)(used == 0 ? 0 : used - 1));
        }

        return ksceKernelFree(item);
    }

    /// <summary>The fixed heap information (0x90 bytes, else 0x80020009): +4 the item size, +8 its bytes, +0xC the most
    /// items used, +0x10 the items in use.</summary>
    static int FixedHeapInfo(uint heap, uint info)
    {
        if (GuestMemory.R32(info) != 0x90)
        {
            return MmuBadVectorSize;
        }

        uint itemSize = GuestMemory.Read<ushort>(heap + 0x20);
        uint used = GuestMemory.Read<ushort>(heap + 0x24);
        GuestMemory.Span(info + 4, 0x8C).Clear();
        GuestMemory.W32(info + 0x04, itemSize);
        GuestMemory.W32(info + 0x08, (used * itemSize + 0xFFF) & ~0xFFFu);
        GuestMemory.W32(info + 0x0C, used);
        GuestMemory.W32(info + 0x10, used);
        return 0;
    }

    /// <summary>ksceKernelGetFixedHeapInfoByPointer (0xA24D2C8D)(heap, info): NULL 0x80020005.</summary>
    [Hle(Nid = 0xA24D2C8D, Library = "SceSysmemForKernel")]
    int ksceKernelGetFixedHeapInfoByPointer(uint heap, uint info)
    {
        if (heap == 0 || info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return FixedHeapInfo(heap, info);
    }

    /// <summary>NID_890D9160(uid, info): the same by the heap's UID; a NULL info 0x80020005.</summary>
    [Hle(Nid = 0x890D9160, Library = "SceSysmemForKernel")]
    int NID_890D9160(int uid, uint info)
    {
        uint heap = NID_FA6C098C(uid);
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return heap == 0 ? SceError.INVALID_UID : FixedHeapInfo(heap, info);
    }

    // ------------------------------------------------------------------ the name heap

    /// <summary>The name heap's copy of <paramref name="name"/> (first 31 characters), made when
    /// <paramref name="create"/>; its string (+0x10) or 0.</summary>
    uint NameHeapFind(string name, bool create)
    {
        lock (_nameNodes)
        {
            if (_nameNodes.TryGetValue(name, out uint node))
            {
                if (create)
                {
                    GuestMemory.W32(node + 4, GuestMemory.R32(node + 4) + 1);
                }

                return node + 0x10;
            }

            if (!create)
            {
                return 0;
            }

            node = KernelAlloc(NameNodeSize, "kernel:namenode", 0x10);
            GuestMemory.Span(node, (int)NameNodeSize).Clear();
            GuestMemory.W32(node + 4, 1);
            GuestMemory.WriteCString(node + 0x10, name, 0x20);
            _nameNodes[name] = node;
            return node + 0x10;
        }
    }

    int NameHeapCall(uint name, uint stringOut, bool create)
    {
        if (name == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint copy = NameHeapFind(GuestMemory.ReadCString(name, 0x1F), create);
        GuestMemory.W32(stringOut, copy);
        return copy == 0 ? NameNotFound : 0;
    }

    /// <summary>NID_40965FD1(name, flags, char **out): the name heap's copy of the name, without adding it (none
    /// 0x80024000, *out 0); NULL 0x80020005.</summary>
    [Hle(Nid = 0x40965FD1, Library = "SceSysmemForKernel")]
    int NID_40965FD1(uint name, uint flags, uint stringOut) => NameHeapCall(name, stringOut, false);

    /// <summary>NID_B628B46B(name, flags, char **out): the name heap's copy of the name, added (or one more reference
    /// taken).</summary>
    [Hle(Nid = 0xB628B46B, Library = "SceSysmemForKernel")]
    int NID_B628B46B(uint name, uint flags, uint stringOut) => NameHeapCall(name, stringOut, true);

    /// <summary>NID_9F1A9AA6(name, flags, char **out): the same lookup-or-add of the user table.</summary>
    [Hle(Nid = 0x9F1A9AA6, Library = "SceSysmemForKernel")]
    int NID_9F1A9AA6(uint name, uint flags, uint stringOut) => NameHeapCall(name, stringOut, true);

    /// <summary>NID_634D0D0C(char *copy, flags): drops a reference of a name heap copy (the last one gives it back);
    /// NULL 0x80020005, a string the heap did not hand out 0x80024000.</summary>
    [Hle(Nid = 0x634D0D0C, Library = "SceSysmemForKernel")]
    int NID_634D0D0C(uint copy, uint flags)
    {
        if (copy == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        lock (_nameNodes)
        {
            foreach (KeyValuePair<string, uint> entry in _nameNodes)
            {
                if (entry.Value + 0x10 != copy)
                {
                    continue;
                }

                uint references = GuestMemory.R32(entry.Value + 4);
                if (references <= 1)
                {
                    _nameNodes.Remove(entry.Key);
                    using (ProcessScope.Enter(KernelSpace.KernelPid))
                    {
                        Mem.Free(entry.Value);
                    }
                }
                else
                {
                    GuestMemory.W32(entry.Value + 4, references - 1);
                }

                return 0;
            }
        }

        return NameNotFound;
    }

    /// <summary>ksceKernelNameHeapGetInfo (0x01194C2E)(info): the name heap's counts (0x14 bytes: +4/+8 the kernel
    /// table's names and most names, +0xC/+0x10 the user table's); NULL or another size 0x80020005.</summary>
    [Hle(Nid = 0x01194C2E, Library = "SceSysmemForKernel")]
    int ksceKernelNameHeapGetInfo(uint info)
    {
        if (info == 0 || GuestMemory.R32(info) != 0x14)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint names;
        lock (_nameNodes)
        {
            names = (uint)_nameNodes.Count;
        }

        GuestMemory.W32(info + 0x04, names);
        GuestMemory.W32(info + 0x08, names);
        GuestMemory.W32(info + 0x0C, 0);
        GuestMemory.W32(info + 0x10, 0);
        return 0;
    }

    // ------------------------------------------------------------------ heap information

    /// <summary>The heap whose body is <paramref name="body"/>, or null.</summary>
    KernelData? HeapOfBody(uint body)
    {
        lock (_objectBodies)
        {
            foreach (KeyValuePair<int, uint> entry in _objectBodies)
            {
                if (entry.Value == body && HeapOf(entry.Key) is { } heap)
                {
                    return heap;
                }
            }
        }

        return null;
    }

    /// <summary>The allocations of a heap: address -> size.</summary>
    List<KeyValuePair<uint, uint>> HeapAllocationsOf(int heapUid)
    {
        List<KeyValuePair<uint, uint>> allocations = new();
        lock (_heapAllocations)
        {
            foreach (KeyValuePair<uint, int> entry in _heapAllocations)
            {
                if (entry.Value == heapUid)
                {
                    allocations.Add(new KeyValuePair<uint, uint>(entry.Key, _heapAllocationSizes.GetValueOrDefault(entry.Key)));
                }
            }
        }

        allocations.Sort((left, right) => left.Key.CompareTo(right.Key));
        return allocations;
    }

    /// <summary>ksceKernelGetHeapInfoByPtr (0x80A75187)(heap, info): the heap's information (0x70 bytes: +8 its UID,
    /// +0x10 its attributes, +0x14 its size, +0x1C the bytes in use, +0x20 the allocations, +0x60 the block count,
    /// +0x6C the attribute word); NULL 0x80020005, another size 0x80020009.</summary>
    [Hle(Nid = 0x80A75187, Library = "SceSysmemForKernel")]
    int ksceKernelGetHeapInfoByPtr(uint body, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0x70)
        {
            return MmuBadVectorSize;
        }

        GuestMemory.Span(info + 4, 0x6C).Clear();
        if (HeapOfBody(body) is not { } heap)
        {
            return 0;
        }

        List<KeyValuePair<uint, uint>> allocations = HeapAllocationsOf(heap.Uid);
        uint used = 0;
        foreach (KeyValuePair<uint, uint> allocation in allocations)
        {
            used += allocation.Value;
        }

        GuestMemory.W32(info + 0x08, (uint)heap.Uid);
        GuestMemory.W32(info + 0x10, heap.Aux);
        GuestMemory.W32(info + 0x14, heap.Size);
        GuestMemory.W32(info + 0x1C, used);
        GuestMemory.W32(info + 0x20, (uint)allocations.Count);
        GuestMemory.W32(info + 0x60, (uint)allocations.Count);
        GuestMemory.W32(info + 0x6C, heap.Aux);
        return 0;
    }

    /// <summary>ksceKernelGetHeapInfo (0x17DB1A0A)(uid, level, info): the same by the heap's UID, +0xC its name.</summary>
    [Hle(Nid = 0x17DB1A0A, Library = "SceSysmemForKernel")]
    int ksceKernelGetHeapInfo(int uid, uint level, uint info)
    {
        if (!IsGuidForm(uid))
        {
            return InvalidPuid;
        }

        if (level >= 8)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<KernelObject>(uid) is not { } kernelObject)
        {
            return SceError.INVALID_UID;
        }

        int result = ksceKernelGetHeapInfoByPtr(BodyOf(kernelObject), info);
        if (result >= 0)
        {
            GuestMemory.W32(info + 0xC, NamePointerOf(kernelObject));
        }

        return result;
    }

    /// <summary>NID_1380E144(level, info[], bufferBytes, count): the 0x70-byte information of every heap, as many as
    /// fit; a NULL count 0x80020005.</summary>
    [Hle(Nid = 0x1380E144, Library = "SceSysmemForKernel")]
    int NID_1380E144(uint level, uint infos, uint bufferBytes, uint countOut)
    {
        if (countOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.W32(countOut, 0);
        List<KernelObject> heaps = ObjectsOfClass(ksceKernelGetUIDHeapClass());
        uint written = 0;
        foreach (KernelObject heap in heaps)
        {
            if (written >= bufferBytes / 0x70)
            {
                break;
            }

            uint info = infos + written * 0x70;
            GuestMemory.W32(info, 0x70);
            int result = ksceKernelGetHeapInfo(heap.Uid, level, info);
            if (result < 0)
            {
                return result;
            }

            written++;
        }

        GuestMemory.W32(countOut, written);
        return 0;
    }

    /// <summary>NID_82059651(heapUid): sysmem's answer for the heap (its allocator keeps no limit: 0); not a heap
    /// 0x80024901.</summary>
    [Hle(Nid = 0x82059651, Library = "SceSysmemForKernel")]
    int NID_82059651(int heapUid) => HeapOf(heapUid) == null ? InvalidHeap : 0;

    /// <summary>NID_BF70207B(heapUid, level, max, pairs[]): the heap's allocations as {address, size} pairs (at most
    /// <paramref name="max"/>); pairs wanted with no buffer 0x80020005; the allocation count.</summary>
    [Hle(Nid = 0xBF70207B, Library = "SceSysmemForKernel")]
    int NID_BF70207B(int heapUid, uint level, uint max, uint pairs)
    {
        if (pairs == 0 && max != 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (HeapOf(heapUid) == null)
        {
            return InvalidHeap;
        }

        List<KeyValuePair<uint, uint>> allocations = HeapAllocationsOf(heapUid);
        for (int i = 0; i < allocations.Count && i < max; i++)
        {
            GuestMemory.W32(pairs + (uint)i * 8, allocations[i].Key);
            GuestMemory.W32(pairs + (uint)i * 8 + 4, allocations[i].Value);
        }

        return allocations.Count;
    }

    /// <summary>NID_D124075A(ptr): the usable size of a heap allocation (its size rounded up to 8), 0 for a pointer no
    /// heap handed out.</summary>
    [Hle(Nid = 0xD124075A, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_D124075A(uint pointer)
    {
        uint size = 0;
        lock (_heapAllocations)
        {
            if (_heapAllocationSizes.TryGetValue(pointer, out uint heapSize))
            {
                size = heapSize;
            }
        }

        lock (_kernelAllocations)
        {
            if (size == 0 && _kernelAllocations.TryGetValue(pointer, out uint kernelSize))
            {
                size = kernelSize;
            }
        }

        return (size + 7) & ~7u;
    }

    // ------------------------------------------------------------------ simple memory blocks

    /// <summary>The capacity of sysmem's nine simple-block pools (by the type's low nibble; FW 3.74 table at
    /// SceSysmem+0x28958).</summary>
    static readonly uint[] SimpleBlockPoolCapacity = [0x800, 0, 0xA0, 0, 0x80, 0, 0, 0, 2];

    /// <summary>The simple blocks taken from each pool.</summary>
    [SnapshotMissing] // states of builds before it: none had been taken
    readonly uint[] _simpleBlocksTaken = new uint[9];

    /// <summary>NID_BD625136(info): the simple-block pools (0x70 bytes, else 0x80020009): nine {free, capacity, 0}
    /// entries from +4.</summary>
    [Hle(Nid = 0xBD625136, Library = "SceSysmemForKernel")]
    int NID_BD625136(uint info)
    {
        if (GuestMemory.R32(info) != 0x70)
        {
            return MmuBadVectorSize;
        }

        for (uint i = 0; i < 9; i++)
        {
            uint capacity = SimpleBlockPoolCapacity[i];
            GuestMemory.W32(info + 4 + i * 12, capacity - Math.Min(_simpleBlocksTaken[i], capacity));
            GuestMemory.W32(info + 8 + i * 12, capacity);
            GuestMemory.W32(info + 12 + i * 12, 0);
        }

        return 0;
    }

    /// <summary>NID_814CDCC5(name, type, flags, SceKernelSimpleMemBlock **out): a block of the KD partition whose size is
    /// in the type (bits 12..23; the guard kinds 0x20 one page less, 0x800 one page); flags other than 0x33 or a NULL
    /// output 0x80020005.</summary>
    [Hle(Nid = 0x814CDCC5, Library = "SceSysmemForKernel")]
    int NID_814CDCC5(uint name, uint type, uint flags, uint blockOut)
    {
        if ((flags & 0xFFFFFFCC) != 0 || blockOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint pool = type & 0xF;
        if (pool >= 9 || _simpleBlocksTaken[pool] >= SimpleBlockPoolCapacity[pool])
        {
            GuestMemory.W32(blockOut, 0);
            return SceError.NO_MEMORY;
        }

        uint size = type & 0xFFF000;
        if ((type & 0xFF0) == 0x20)
        {
            size -= 0x2000;
        }
        else if ((type & 0xFF0) == 0x800)
        {
            size = 0x1000;
        }

        int uid = ksceKernelAllocMemBlock(name != 0 ? name : KernelStr("SceSimpleMemBlock"), MemBlockKernelRw, Math.Max(size, 0x1000u), 0);
        if (uid < 0)
        {
            GuestMemory.W32(blockOut, 0);
            return uid;
        }

        _simpleBlocksTaken[pool]++;
        uint block = NID_FA6C098C(uid);
        GuestMemory.W32(block + 0x34, pool);
        GuestMemory.W32(blockOut, block);
        return 0;
    }

    /// <summary>ksceKernelFreeSimpleMemBlock (0xA4DB88E1)(SceKernelSimpleMemBlock *block): frees a block of
    /// NID_814CDCC5 (its UID at +0x3C); 0.</summary>
    [Hle(Nid = 0xA4DB88E1, Library = "SceSysmemForKernel")]
    int ksceKernelFreeSimpleMemBlock(uint block)
    {
        if (block == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int uid = (int)GuestMemory.R32(block + 0x3C);
        if (K.Objects.Get<MemBlock>(uid) is null)
        {
            return SceError.INVALID_UID;
        }

        uint pool = GuestMemory.R32(block + 0x34);
        if (pool < 9 && _simpleBlocksTaken[pool] > 0)
        {
            _simpleBlocksTaken[pool]--;
        }

        return ksceKernelFreeMemBlock(uid);
    }

    /// <summary>A kernel read-write block type (0x1020D006).</summary>
    const uint MemBlockKernelRw = 0x1020D006;

    /// <summary>A name string in kernel memory, made once.</summary>
    uint KernelStr(string text)
    {
        return SysmemRecord("str:" + text, () =>
        {
            uint address = KernelAlloc((uint)text.Length + 1, "kernel:str", 4);
            GuestMemory.WriteCString(address, text, text.Length + 1);
            return address;
        });
    }

    /// <summary>NID_14D3807B(pid, va, size, kind, ?, SceKernelSimpleMemBlock **out): a kernel window on
    /// [va, +size) of a process (page-aligned, 1..8 KB; else 0x80020005): VitaSharp's kernel sees the process's memory
    /// at the same address, so the window's base (+0x18) is <paramref name="address"/>; the record (+0x34 the range,
    /// +0x38 the size).</summary>
    [Hle(Nid = 0x14D3807B, Library = "SceSysmemForKernel")]
    int NID_14D3807B(int pid, uint address, uint size, uint kind, uint unused, uint blockOut)
    {
        if ((address & 0xFFF) != 0 || size - 1 >= 0x2000 || (size & 0xFFF) != 0 || (kind & 0xF) > 1)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint block = KernelAlloc(0x40, "kernel:simplememblock", 0x10);
        uint range = KernelAlloc(0x10, "kernel:simplememblock:range", 0x10);
        GuestMemory.Span(block, 0x40).Clear();
        GuestMemory.W32(range + 0x0, (uint)pid);
        GuestMemory.W32(range + 0x4, address);
        GuestMemory.W32(range + 0x8, size);
        GuestMemory.W32(block + 0x18, address);
        GuestMemory.W32(block + 0x30, kind & 0xF);
        GuestMemory.W32(block + 0x34, range);
        GuestMemory.W32(block + 0x38, size);
        if (blockOut != 0)
        {
            GuestMemory.W32(blockOut, block);
        }

        return 0;
    }

    /// <summary>NID_4EEF69C7(SceKernelSimpleMemBlock *block): closes an NID_14D3807B window; 0.</summary>
    [Hle(Nid = 0x4EEF69C7, Library = "SceSysmemForKernel")]
    int NID_4EEF69C7(uint block)
    {
        GuestMemory.W32(block + 0x38, 0);
        return 0;
    }

    /// <summary>NID_455B0A4C(opt): a memory block from an allocation request (0x94 bytes: +0xC the name, +0x10 the
    /// size, +0x14 the type, +0x18/+0x1C a page-aligned range inside it); NULL or a bad request 0x80020005, another size
    /// 0x80020009; the block's UID.</summary>
    [Hle(Nid = 0x455B0A4C, Library = "SceSysmemForKernel")]
    int NID_455B0A4C(uint request)
    {
        if (request == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(request) != 0x94)
        {
            return MmuBadVectorSize;
        }

        uint name = GuestMemory.R32(request + 0x0C);
        uint size = GuestMemory.R32(request + 0x10);
        uint offset = GuestMemory.R32(request + 0x18);
        uint length = GuestMemory.R32(request + 0x1C);
        if (name == 0 || ((size | offset | length) & 0xFFF) != 0 || offset + length >= size)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ksceKernelAllocMemBlock(name, GuestMemory.R32(request + 0x14), size, 0);
    }

    /// <summary>NID_40870E74(name, size, ?): a kernel read-write block (type 0x1060D006) of sysmem's partition; its
    /// UID.</summary>
    [Hle(Nid = 0x40870E74, Library = "SceSysmemForKernel")]
    int NID_40870E74(uint name, uint size, uint unused) => ksceKernelAllocMemBlock(name, 0x1060D006, size, 0);

    /// <summary>NID_441E6BE9(uid, pages): attaches a physical page list to a paged block (its page +0x14, +0x18 the
    /// list's first word in pages; the block's +0x14 bit 0x40); no block 0x80024701, a list already there
    /// 0x80024700, a block without a page list 0.</summary>
    [Hle(Nid = 0x441E6BE9, Library = "SceSysmemForKernel")]
    int NID_441E6BE9(int uid, uint pages)
    {
        uint block = NID_FA6C098C(uid);
        if (block == 0)
        {
            return ReferenceNotFound;
        }

        if ((GuestMemory.R32(block + 0x0C) & 0xF0000) != PageListPaging)
        {
            return 0;
        }

        uint page = GuestMemory.R32(block + 0x30);
        if (page == 0)
        {
            return 0;
        }

        if (GuestMemory.R32(page + 0x14) != 0)
        {
            return ReferenceCountUnderflow;
        }

        GuestMemory.W32(page + 0x14, pages);
        GuestMemory.W32(page + 0x18, GuestMemory.R32(pages) << 12);
        GuestMemory.W32(block + 0x14, GuestMemory.R32(block + 0x14) | 0x40);
        return 0;
    }

    // ------------------------------------------------------------------ the rest of FW 3.74's entries

    /// <summary>NID_4345C61F(pid, dst, src, len): copies code into process <paramref name="pid"/>'s memory with the
    /// domains opened, cleaning the data cache over it (the copied code is what runs); a pid that is no process
    /// 0x80029001; 0.</summary>
    [Hle(Nid = 0x4345C61F, Library = "SceSysmemForKernel")]
    int NID_4345C61F(int pid, uint destination, uint source, uint length)
    {
        if (ProcessOf(pid) == null)
        {
            return SceError.INVALID_PID;
        }

        GuestMemory.Copy(destination, source, length);
        InvalidateCode(destination, length);
        return 0;
    }

    /// <summary>NID_FCB5745A(): the instruction cache maintenance sysmem runs with the domains opened; the translated
    /// code is dropped by the copies themselves, so nothing is left to do.</summary>
    [Hle(Nid = 0xFCB5745A, Library = "SceSysmemForKernel")]
    void NID_FCB5745A()
    {
    }

    /// <summary>NID_CAB15F74(pid): clears the process's sysmem data slot (its sysroot entry); 0.</summary>
    [Hle(Nid = 0xCAB15F74, Library = "SceSysmemForKernel")]
    int NID_CAB15F74(int pid) => 0;

    /// <summary>NID_D4511A52(guid, value): sets the object's word +0x9C (NID_811C452B by GUID); none 0x80024501.</summary>
    [Hle(Nid = 0xD4511A52, Library = "SceSysmemForKernel")]
    int NID_D4511A52(int guid, uint value)
    {
        if (GuidObject(guid) is not { } kernelObject)
        {
            return InvalidPuid;
        }

        GuestMemory.W32(BodyOf(kernelObject) + 0x9C, value);
        return 0;
    }

    /// <summary>NID_8FD11E02(): a word of sysmem's context (+0x8C) no FW 3.74 module writes; its boot value 0.</summary>
    [Hle(Nid = 0x8FD11E02, Library = "SceSysmemForKernel", ReturnsValue = true, Default = true)]
    uint NID_8FD11E02() => 0;

    /// <summary>NID_7CDB7655(cls, name, parent, itemsize, create, destroy): ksceKernelCreateClass.</summary>
    [Hle(Nid = 0x7CDB7655, Library = "SceSysmemForKernel")]
    int NID_7CDB7655(uint cls, uint name, uint parent, uint itemSize, uint create, uint destroy) => ksceKernelCreateClass(cls, name, parent, itemSize, create, destroy);

    /// <summary>NID_4E74DA48(): sysmem's second boot stage (the SceKernelSysmem object and its heaps); VitaSharp's
    /// sysmem is set up before kernel code runs: 0.</summary>
    [Hle(Nid = 0x4E74DA48, Library = "SceSysmemForKernel")]
    int NID_4E74DA48() => 0;

    /// <summary>NID_4E923101(): the GUID table's boot set-up (done already).</summary>
    [Hle(Nid = 0x4E923101, Library = "SceSysmemForKernel")]
    void NID_4E923101()
    {
    }

    /// <summary>NID_16A54BFE(): the address space boot set-up (done already).</summary>
    [Hle(Nid = 0x16A54BFE, Library = "SceSysmemForKernel")]
    void NID_16A54BFE()
    {
    }

    /// <summary>NID_02DAD732(): the physical pools' boot set-up (done already).</summary>
    [Hle(Nid = 0x02DAD732, Library = "SceSysmemForKernel")]
    void NID_02DAD732()
    {
    }
}
