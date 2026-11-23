using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSysmemForKernel's address spaces, partitions, physical memory partitions and physical page pools, as the FW
/// 3.74 sysmem.skprx has them (decompiled, study only), on the records VitaSharp already keeps for each process (the
/// 0x170-byte address space and 0x80-byte partition of <see cref="AddressSpaceOf"/>): the processmgr's calls give
/// those records a UID of SceUIDAddressSpaceClass, and the physical memory partitions are objects of
/// SceUIDPhyMemPartClass laid out as the firmware's (0xAC bytes): +0x0C bit 0 valid, +0x10 its UID, +0x14 its flags
/// (the kind in 0xF0000: 0x10000 a list of physical chunks), +0x1C a 0x80-byte page record, +0x20 its free-page list
/// (empty), +0x7C its chunk list (0x24-byte nodes: +0 next, +4 physical address, +8 size), +0x80 the reference count,
/// +0x84 the bytes it holds, +0x88 the bytes in use, +0x8C its name. VitaSharp has no physical pages behind the
/// emulated memory; the pools hand out ranges of the retail PCH-1000 physical map (LPDDR2 at 0x40000000, 512 MB;
/// CDRAM at 0x20000000, 128 MB) so the addresses kernel code reads back are the console's, and the MMU stays as it is
/// (an address space switch, a translation table walk and the domain access changes have nothing to change).
/// </summary>
public sealed partial class SysmemKernel
{
    const uint PhyMemPartSize = 0xAC, PhyMemPartChunkSize = 0x24;
    const uint PhyMemPartValid = 1, PhyMemPartChunkList = 0x10000, PhyMemPartKindMask = 0xF0000, PhyMemPartFixed = 0x30000;
    const int PhyMemPartInvalid = unchecked((int)0x8002430B);
    const int PhyMemPartNoMemory = unchecked((int)0x80024308);
    const int UnknownPhysicalPool = unchecked((int)0x80024C02);
    const uint MainMemoryPhysicalBase = 0x40000000, MainMemoryPhysicalSize = 0x20000000;
    const uint CdramPhysicalBase = 0x20000000, CdramPhysicalSize = 0x08000000;

    /// <summary>The physical ranges the pools handed out: physical address -> size.</summary>
    [SnapshotMissing] // states of builds before it: nothing had been handed out
    readonly Dictionary<uint, uint> _physicalRanges = new();

    /// <summary>Sysmem's own records made on first request (the KD physical partition, the kernel address space's
    /// UID, the fixed heaps, the shared UID entry heap): by role -> address or UID.</summary>
    [SnapshotMissing] // states of builds before it: made again on first request
    readonly Dictionary<string, uint> _sysmemRecords = new();

    /// <summary>The bytes NID_36FDA794 added to the partition processmgr registered with NID_3753C584.</summary>
    [SnapshotMissing] // states of builds before it: none had been added
    uint _gamePartitionAddedBytes;

    uint SysmemRecord(string role, Func<uint> make)
    {
        lock (_sysmemRecords)
        {
            if (_sysmemRecords.TryGetValue(role, out uint existing))
            {
                return existing;
            }

            uint made = make();
            _sysmemRecords[role] = made;
            return made;
        }
    }

    // ------------------------------------------------------------------ physical page pools

    /// <summary>The physical range of a pool kind (0x10001 and 0x10002: the main memory, 0x20001: CDRAM), or null.</summary>
    static (uint Base, uint Size)? PhysicalPool(uint kind)
    {
        return kind switch
        {
            0x10001 or 0x10002 => (MainMemoryPhysicalBase, MainMemoryPhysicalSize),
            0x20001 => (CdramPhysicalBase, CdramPhysicalSize),
            _ => null,
        };
    }

    /// <summary>The first free range of <paramref name="size"/> bytes (whole pages) aligned to
    /// <paramref name="alignment"/> in the pool, taken; 0 when none.</summary>
    uint AllocPhysical(uint kind, uint size, uint alignment)
    {
        if (PhysicalPool(kind) is not { } pool || size == 0)
        {
            return 0;
        }

        size = (size + 0xFFF) & ~0xFFFu;
        alignment = Math.Max(alignment, 0x1000u);
        lock (_physicalRanges)
        {
            List<KeyValuePair<uint, uint>> taken = new(_physicalRanges);
            taken.Sort((left, right) => left.Key.CompareTo(right.Key));
            ulong candidate = pool.Base;
            foreach (KeyValuePair<uint, uint> range in taken)
            {
                candidate = (candidate + alignment - 1) / alignment * alignment;
                if (range.Key + (ulong)range.Value <= candidate)
                {
                    continue;
                }

                if (candidate + size <= range.Key)
                {
                    break;
                }

                candidate = range.Key + (ulong)range.Value;
            }

            candidate = (candidate + alignment - 1) / alignment * alignment;
            if (candidate + size > pool.Base + (ulong)pool.Size)
            {
                return 0;
            }

            _physicalRanges[(uint)candidate] = size;
            return (uint)candidate;
        }
    }

    bool FreePhysical(uint address)
    {
        lock (_physicalRanges)
        {
            return _physicalRanges.Remove(address);
        }
    }

    uint FreePhysicalBytes(uint kind)
    {
        if (PhysicalPool(kind) is not { } pool)
        {
            return 0;
        }

        ulong used = 0;
        lock (_physicalRanges)
        {
            foreach (KeyValuePair<uint, uint> range in _physicalRanges)
            {
                if (range.Key >= pool.Base && range.Key < pool.Base + (ulong)pool.Size)
                {
                    used += range.Value;
                }
            }
        }

        return (uint)(pool.Size - Math.Min(used, pool.Size));
    }

    /// <summary>NID_05822B82(kind, SceSize *free): the free bytes of physical pool 0x10001; another kind
    /// 0x80024C02.</summary>
    [Hle(Nid = 0x05822B82, Library = "SceSysmemForKernel")]
    int NID_05822B82(uint kind, uint freeOut)
    {
        if (kind != 0x10001)
        {
            return UnknownPhysicalPool;
        }

        GuestMemory.W32(freeOut, FreePhysicalBytes(kind));
        return 0;
    }

    /// <summary>NID_EA98CCE1(kind, size, alignment, paddr *out): a physically contiguous range of the pool (0x10001,
    /// 0x10002, 0x20001; another kind 0x80024C02; none left 0x80024308).</summary>
    [Hle(Nid = 0xEA98CCE1, Library = "SceSysmemForKernel")]
    int NID_EA98CCE1(uint kind, uint size, uint alignment, uint physicalOut)
    {
        if (PhysicalPool(kind) is null)
        {
            return UnknownPhysicalPool;
        }

        uint address = AllocPhysical(kind, size, alignment);
        if (address == 0)
        {
            return PhyMemPartNoMemory;
        }

        if (physicalOut != 0)
        {
            GuestMemory.W32(physicalOut, address);
        }

        return 0;
    }

    /// <summary>NID_844DF3DA(kind, paddr, size): gives a range back to the pool (another kind 0x80024C02; a range
    /// outside the pool or not page-aligned 0x80020005); 0.</summary>
    [Hle(Nid = 0x844DF3DA, Library = "SceSysmemForKernel")]
    int NID_844DF3DA(uint kind, uint address, uint size)
    {
        if (PhysicalPool(kind) is not { } pool)
        {
            return UnknownPhysicalPool;
        }

        if (address < pool.Base || ((address | size) & 0xFFF) != 0 || (ulong)address + size > pool.Base + (ulong)pool.Size)
        {
            return SceError.INVALID_ARGUMENT;
        }

        FreePhysical(address);
        return 0;
    }

    // ------------------------------------------------------------------ physical memory partitions

    /// <summary>ScePhyMemPartKD (NID_0164D817): the kernel's physical partition, one chunk at the start of main memory
    /// as large as VitaSharp's kernel region.</summary>
    uint KernelPhyMemPart()
    {
        return SysmemRecord("ScePhyMemPartKD", () =>
        {
            uint size = GuestMemory.KernelEnd - GuestMemory.KernelStart;
            uint chunkAddress = AllocPhysical(0x10001, size, 0x1000);
            return MakePhyMemPart("ScePhyMemPartKD", PhyMemPartChunkList, chunkAddress, size);
        });
    }

    /// <summary>Lays out a physical partition (with one chunk when <paramref name="size"/> is not 0) and gives it a
    /// UID: the record.</summary>
    uint MakePhyMemPart(string name, uint flags, uint chunkAddress, uint size)
    {
        uint cls = SysmemClass("SceUIDPhyMemPartClass", PhyMemPartSize);
        uint record = KernelAlloc(PhyMemPartSize, $"kernel:phymempart:{name}", 0x10);
        GuestMemory.Span(record, (int)PhyMemPartSize).Clear();
        uint freeList = KernelAlloc(0x10, "kernel:phymempart:free", 0x10);
        GuestMemory.W32(freeList + 0x0C, freeList);
        GuestMemory.W32(record + 0x0C, PhyMemPartValid);
        GuestMemory.W32(record + 0x14, flags);
        GuestMemory.W32(record + 0x1C, KernelAlloc(0x80, "kernel:phymempart:pages", 0x10));
        GuestMemory.W32(record + 0x20, freeList);
        GuestMemory.WriteCString(record + 0x8C, name, 0x20);
        if (size != 0)
        {
            AddPhyMemPartChunk(record, chunkAddress, size);
        }

        int uid = RegisterExistingObject(cls, name, record, PhyMemPartSize);
        GuestMemory.W32(record + 0x10, (uint)uid);
        return record;
    }

    void AddPhyMemPartChunk(uint record, uint address, uint size)
    {
        uint chunk = KernelAlloc(PhyMemPartChunkSize, "kernel:phymempart:chunk", 0x10);
        GuestMemory.Span(chunk, (int)PhyMemPartChunkSize).Clear();
        GuestMemory.W32(chunk + 0x04, address);
        GuestMemory.W32(chunk + 0x08, size);
        GuestMemory.W32(chunk + 0x18, address >> 12);
        GuestMemory.W32(chunk + 0x1C, (address + size) >> 12);
        uint link = record + 0x7C;
        while (GuestMemory.R32(link) != 0)
        {
            link = GuestMemory.R32(link);
        }

        GuestMemory.W32(link, chunk);
        GuestMemory.W32(record + 0x84, GuestMemory.R32(record + 0x84) + size);
    }

    /// <summary>The physical partition behind a UID, or 0.</summary>
    uint PhyMemPartOf(int uid)
    {
        uint cls = SysmemClass("SceUIDPhyMemPartClass", PhyMemPartSize);
        return K.Objects.Get<KernelData>(uid) is { Kind: KernelData.ClassObject } data && data.Aux == cls ? data.Address : 0;
    }

    static bool PhyMemPartIsValid(uint record) => (GuestMemory.R32(record + 0x0C) & PhyMemPartValid) != 0;

    /// <summary>NID_0164D817(): ScePhyMemPartKD, the kernel's physical partition.</summary>
    [Hle(Nid = 0x0164D817, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_0164D817() => KernelPhyMemPart();

    /// <summary>NID_0B1B4ACA(): ScePhyMemPartTool, the physical partition of a development kit's extra memory: the
    /// word sysmem's boot takes from its context (+0x14); a retail console's boot sets none: 0.</summary>
    [Hle(Nid = 0x0B1B4ACA, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_0B1B4ACA() => GuestMemory.R32(SysmemContext() + SysmemContextToolPartition);

    /// <summary>NID_BC36755F(): the bytes NID_36FDA794 added to the partition processmgr registered (0 after
    /// NID_9B7E673F empties it).</summary>
    [Hle(Nid = 0xBC36755F, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_BC36755F() => _gamePartitionAddedBytes;

    /// <summary>NID_021053DD(level, SceUID *vector, vectorBytes, SceSize *count): the UIDs of the physical partitions;
    /// a NULL count 0x80020005.</summary>
    [Hle(Nid = 0x021053DD, Library = "SceSysmemForKernel")]
    int NID_021053DD(uint level, uint vector, uint vectorBytes, uint countOut)
    {
        if (countOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        KernelPhyMemPart();
        return ksceGUIDGetUIDVectorByClass(SysmemClass("SceUIDPhyMemPartClass", PhyMemPartSize), level, vector, vectorBytes >> 2, countOut);
    }

    /// <summary>NID_5CC56F87(name, flags, paddr, size, ScePhyMemPart **out): a physical partition: a chunk list
    /// (flags 0x10000) holding [paddr, +size), or <paramref name="size"/> bytes of main memory when paddr is 0 and bit 0
    /// of the flags is clear; a fixed partition (0x30000) of the range; another kind 0x80020001; no memory 0x80024308
    /// (*out cleared); a NULL output 0x80020005.</summary>
    [Hle(Nid = 0x5CC56F87, Library = "SceSysmemForKernel")]
    int NID_5CC56F87(uint name, uint flags, uint address, uint size, uint partitionOut)
    {
        if (partitionOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint kind = flags & PhyMemPartKindMask;
        if (kind != PhyMemPartChunkList && kind != PhyMemPartFixed)
        {
            GuestMemory.W32(partitionOut, 0);
            return SceError.ERROR;
        }

        if (address == 0 && size != 0)
        {
            if (kind != PhyMemPartChunkList || (flags & 1) != 0)
            {
                GuestMemory.W32(partitionOut, 0);
                return PhyMemPartNoMemory;
            }

            address = AllocPhysical(0x10001, size, 0x1000);
            if (address == 0)
            {
                GuestMemory.W32(partitionOut, 0);
                return PhyMemPartNoMemory;
            }
        }

        string partitionName = name != 0 ? GuestMemory.ReadCString(name, 0x1F) : "";
        GuestMemory.W32(partitionOut, MakePhyMemPart(partitionName, flags, address, size));
        return 0;
    }

    /// <summary>NID_A2DFA383(name, flags, size, out): NID_5CC56F87 with no physical address (main memory).</summary>
    [Hle(Nid = 0xA2DFA383, Library = "SceSysmemForKernel")]
    int NID_A2DFA383(uint name, uint flags, uint size, uint partitionOut) => NID_5CC56F87(name, flags, 0, size, partitionOut);

    /// <summary>NID_36FDA794(ScePhyMemPart *part, paddr, size): adds a chunk to a chunk-list partition ([paddr, +size),
    /// or main memory for paddr 0; none 0x80024308); a fixed partition 0; another kind 0x80024300; not valid
    /// 0x8002430B.</summary>
    [Hle(Nid = 0x36FDA794, Library = "SceSysmemForKernel")]
    int NID_36FDA794(uint record, uint address, uint size)
    {
        if (!PhyMemPartIsValid(record))
        {
            return PhyMemPartInvalid;
        }

        uint kind = GuestMemory.R32(record + 0x14) & PhyMemPartKindMask;
        if (kind == PhyMemPartFixed)
        {
            return 0;
        }

        if (kind != PhyMemPartChunkList)
        {
            return NoMemoryRegion;
        }

        if (address == 0)
        {
            address = AllocPhysical(0x10001, size, 0x1000);
            if (address == 0)
            {
                return PhyMemPartNoMemory;
            }
        }

        AddPhyMemPartChunk(record, address, size);
        if (record == SysmemWord(0x294))
        {
            _gamePartitionAddedBytes += size;
        }

        return 0;
    }

    /// <summary>NID_5B434019(part, size): NID_36FDA794 with main memory.</summary>
    [Hle(Nid = 0x5B434019, Library = "SceSysmemForKernel")]
    int NID_5B434019(uint record, uint size) => NID_36FDA794(record, 0, size);

    /// <summary>Drops one reference of the partition (+0x80): true while others remain.</summary>
    static bool PhyMemPartStillReferenced(uint record) => Interlocked.Decrement(ref GuestMemory.Ref<int>(record + 0x80)) >= 1;

    /// <summary>Gives every chunk after the first <paramref name="keep"/> back to the pool.</summary>
    void FreePhyMemPartChunks(uint record, int keep)
    {
        uint link = record + 0x7C;
        for (int i = 0; i < keep && GuestMemory.R32(link) != 0; i++)
        {
            link = GuestMemory.R32(link);
        }

        uint chunk = GuestMemory.R32(link);
        GuestMemory.W32(link, 0);
        while (chunk != 0)
        {
            uint next = GuestMemory.R32(chunk);
            FreePhysical(GuestMemory.R32(chunk + 4));
            GuestMemory.W32(record + 0x84, GuestMemory.R32(record + 0x84) - GuestMemory.R32(chunk + 8));
            chunk = next;
        }
    }

    /// <summary>NID_3F72B2E7(part): drops a reference; the last one gives the chunks back and deletes the partition;
    /// NULL 0x80020005, not valid 0x8002430B; 0.</summary>
    [Hle(Nid = 0x3F72B2E7, Library = "SceSysmemForKernel")]
    int NID_3F72B2E7(uint record)
    {
        if (record == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (PhyMemPartStillReferenced(record))
        {
            return 0;
        }

        if (!PhyMemPartIsValid(record))
        {
            return PhyMemPartInvalid;
        }

        FreePhyMemPartChunks(record, 0);
        GuestMemory.W32(record + 0x0C, GuestMemory.R32(record + 0x0C) & ~PhyMemPartValid);
        K.Objects.Remove((int)GuestMemory.R32(record + 0x10));
        return 0;
    }

    /// <summary>NID_9B7E673F(part): drops a reference; the last one gives back every chunk but the first (the
    /// processmgr's partition counter back to 0); NULL 0x80020005, not valid 0x8002430B; 0.</summary>
    [Hle(Nid = 0x9B7E673F, Library = "SceSysmemForKernel")]
    int NID_9B7E673F(uint record)
    {
        if (record == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (PhyMemPartStillReferenced(record))
        {
            return 0;
        }

        if (!PhyMemPartIsValid(record))
        {
            return PhyMemPartInvalid;
        }

        if ((GuestMemory.R32(record + 0x14) & PhyMemPartKindMask) == PhyMemPartChunkList)
        {
            FreePhyMemPartChunks(record, 1);
        }

        if (record == SysmemWord(0x294))
        {
            _gamePartitionAddedBytes = 0;
        }

        return 0;
    }

    /// <summary>NID_B9B69700(part, info): +0 the lowest chunk address (0xFFFFFFFF for none), +4 the chunks' bytes, +8
    /// the bytes it holds, +0xC the free-list length; NULL 0x80020005, not valid 0x8002430B.</summary>
    [Hle(Nid = 0xB9B69700, Library = "SceSysmemForKernel")]
    int NID_B9B69700(uint record, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (!PhyMemPartIsValid(record))
        {
            return PhyMemPartInvalid;
        }

        uint lowest = 0xFFFFFFFF;
        uint total = 0;
        for (uint chunk = GuestMemory.R32(record + 0x7C); chunk != 0; chunk = GuestMemory.R32(chunk))
        {
            lowest = Math.Min(lowest, GuestMemory.R32(chunk + 4));
            total += GuestMemory.R32(chunk + 8);
        }

        GuestMemory.W32(info + 0x00, lowest);
        GuestMemory.W32(info + 0x04, total);
        GuestMemory.W32(info + 0x08, GuestMemory.R32(record + 0x84));
        GuestMemory.W32(info + 0x0C, 0);
        return 0;
    }

    /// <summary>The 0xB0-byte partition information of NID_73E03A38 / NID_029F5989: +4 its UID, +8 its name, +0xC its
    /// flags, +0x10 the bytes it holds, +0x14 the bytes in use, +0x1C the free-list length, +0x20 the chunk count, +0x24
    /// the first 16 chunks (address, size), +0xA4 the page entries written to the caller's +0xAC buffer (none).</summary>
    static int PhyMemPartInfo(uint record, uint info)
    {
        if (!PhyMemPartIsValid(record))
        {
            return PhyMemPartInvalid;
        }

        GuestMemory.W32(info + 0x04, GuestMemory.R32(record + 0x10));
        GuestMemory.W32(info + 0x08, record + 0x8C);
        GuestMemory.W32(info + 0x0C, GuestMemory.R32(record + 0x14));
        GuestMemory.W32(info + 0x10, GuestMemory.R32(record + 0x84));
        GuestMemory.W32(info + 0x14, GuestMemory.R32(record + 0x88));
        GuestMemory.W32(info + 0x1C, 0);
        uint count = 0;
        for (uint chunk = GuestMemory.R32(record + 0x7C); chunk != 0; chunk = GuestMemory.R32(chunk))
        {
            if (count < 0x10)
            {
                GuestMemory.W32(info + 0x24 + count * 8, GuestMemory.R32(chunk + 4));
                GuestMemory.W32(info + 0x28 + count * 8, GuestMemory.R32(chunk + 8));
            }

            count++;
        }

        GuestMemory.W32(info + 0x20, count);
        GuestMemory.W32(info + 0xA4, 0);
        return 0;
    }

    /// <summary>NID_73E03A38(part, info): the partition's information; NULL 0x80020005, another size
    /// 0x80020009.</summary>
    [Hle(Nid = 0x73E03A38, Library = "SceSysmemForKernel")]
    int NID_73E03A38(uint record, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return GuestMemory.R32(info) != 0xB0 ? MmuBadVectorSize : PhyMemPartInfo(record, info);
    }

    /// <summary>NID_029F5989(uid, info): the same by the partition's UID (its lookup error otherwise).</summary>
    [Hle(Nid = 0x029F5989, Library = "SceSysmemForKernel")]
    int NID_029F5989(int uid, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0xB0)
        {
            return MmuBadVectorSize;
        }

        if (!IsGuidForm(uid) || K.Objects.Get<KernelObject>(uid) is null)
        {
            return SceError.INVALID_UID;
        }

        uint record = PhyMemPartOf(uid);
        return record == 0 ? PhyMemPartInvalid : PhyMemPartInfo(record, info);
    }

    /// <summary>NID_B488C82E(part, size, paddr *out): <paramref name="size"/> bytes of physical memory for the
    /// partition (*out the address, 0 when none: 0x80024308); NULL 0x80020005.</summary>
    [Hle(Nid = 0xB488C82E, Library = "SceSysmemForKernel")]
    int NID_B488C82E(uint record, uint size, uint physicalOut)
    {
        if (record == 0 || physicalOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        GuestMemory.W32(physicalOut, 0);
        uint address = AllocPhysical(0x10001, size, 0x1000);
        if (address == 0)
        {
            return PhyMemPartNoMemory;
        }

        GuestMemory.W32(record + 0x88, GuestMemory.R32(record + 0x88) + ((size + 0xFFF) & ~0xFFFu));
        GuestMemory.W32(physicalOut, address);
        return 0;
    }

    /// <summary>NID_193A9D2F(part, pages): gives physical memory NID_B488C82E handed out back (the page list's first
    /// address); a NULL list 0x80020005; 0.</summary>
    [Hle(Nid = 0x193A9D2F, Library = "SceSysmemForKernel")]
    int NID_193A9D2F(uint record, uint address)
    {
        if (address == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint size;
        lock (_physicalRanges)
        {
            size = _physicalRanges.GetValueOrDefault(address);
        }

        if (FreePhysical(address) && record != 0)
        {
            GuestMemory.W32(record + 0x88, GuestMemory.R32(record + 0x88) - Math.Min(size, GuestMemory.R32(record + 0x88)));
        }

        return 0;
    }

    // ------------------------------------------------------------------ address spaces

    uint AddressSpaceClass() => SysmemClass("SceUIDAddressSpaceClass", AddressSpaceSize);

    uint PartitionClass() => SysmemClass("SceUIDPartitionClass", PartitionSize);

    /// <summary>The address space record behind a UID (one made by ksceKernelCreateAddressSpace or the kernel's), or
    /// 0.</summary>
    uint AddressSpaceRecordOf(int uid)
    {
        return K.Objects.Get<KernelData>(uid) is { Kind: KernelData.ClassObject } data && data.Aux == AddressSpaceClass() ? data.Address : 0;
    }

    /// <summary>The address space's lookup by UID: its record, or the GUID lookup's error.</summary>
    int AddressSpaceLookup(int uid, out uint record)
    {
        record = 0;
        if (!IsGuidForm(uid))
        {
            return SceError.INVALID_UID;
        }

        if (K.Objects.Get<KernelObject>(uid) is not { } kernelObject)
        {
            return SceError.INVALID_UID;
        }

        record = AddressSpaceRecordOf(uid);
        if (record == 0)
        {
            record = BodyOf(kernelObject);
        }

        return 0;
    }

    /// <summary>The UID of the kernel's address space (SceKernelAddressSpace, GUID 0x10007 on the console).</summary>
    int KernelAddressSpaceUid()
    {
        return (int)SysmemRecord("SceKernelAddressSpace", () =>
        {
            uint record = AddressSpaceOf(KernelSpace.KernelPid);
            FillAddressSpaceRecord(record, KernelSpace.KernelPid);
            return (uint)RegisterExistingObject(AddressSpaceClass(), "SceKernelAddressSpace", record, AddressSpaceSize);
        });
    }

    /// <summary>The fields ksceKernelCreateAddressSpace sets: +0x14 the pid, +0x18 the MMU context of its first
    /// partition, +0x9C the partitions' UIDs (none: -1), +0x11C the physical partition slots, +0x15C/+0x160 the user
    /// I/O blocks (none here: -1), +0x164 the I/O timer's physical address.</summary>
    void FillAddressSpaceRecord(uint record, int pid)
    {
        GuestMemory.W32(record + 0x14, (uint)pid);
        uint partition = GuestMemory.R32(record + 0x1C);
        GuestMemory.W32(record + 0x18, partition != 0 ? GuestMemory.R32(partition + 0x1C) : 0);
        for (uint i = 0; i < 0x20; i++)
        {
            GuestMemory.W32(record + 0x9C + i * 4, 0xFFFFFFFF);
        }

        GuestMemory.W32(record + 0x15C, 0xFFFFFFFF);
        GuestMemory.W32(record + 0x160, 0xFFFFFFFF);
        GuestMemory.W32(record + 0x16C, AddressSpaceMagic);
    }

    /// <summary>NID_2E5CC243(): the kernel's address space record.</summary>
    [Hle(Nid = 0x2E5CC243, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_2E5CC243() => AddressSpaceRecordOf(KernelAddressSpaceUid());

    /// <summary>NID_99D3BF76(): SceUIDAddressSpaceClass.</summary>
    [Hle(Nid = 0x99D3BF76, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_99D3BF76() => AddressSpaceClass();

    /// <summary>NID_5C0FC6BE(): SceUIDPartitionClass.</summary>
    [Hle(Nid = 0x5C0FC6BE, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_5C0FC6BE() => PartitionClass();

    /// <summary>ksceKernelCreateAddressSpace (0x8EE89D2C)(pid, name, type, opt): the user address space of process
    /// <paramref name="pid"/> (type 0x10000001; opt 0x10 bytes: +8 the allocator, +0xC the translation table
    /// parameter): VitaSharp's record of the process, given a UID of SceUIDAddressSpaceClass; a NULL name or option,
    /// another option size or type 0x80020005; the UID.</summary>
    [Hle(Nid = 0x8EE89D2C, Library = "SceSysmemForKernel")]
    int ksceKernelCreateAddressSpace(int pid, uint name, uint type, uint option)
    {
        if (name == 0 || option == 0 || GuestMemory.R32(option) != 0x10 || type != UserAddressSpaceFlag)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint record = AddressSpaceOf(pid);
        FillAddressSpaceRecord(record, pid);
        GuestMemory.W32(record + 0x168, GuestMemory.R32(option + 8));
        return RegisterExistingObject(AddressSpaceClass(), GuestMemory.ReadCString(name, 0x1F), record, AddressSpaceSize);
    }

    /// <summary>ksceKernelDeleteAddressSpace (0x4A2E421D)(uid): deletes a user address space's UID (the kernel's
    /// 0x80020001); 0.</summary>
    [Hle(Nid = 0x4A2E421D, Library = "SceSysmemForKernel")]
    int ksceKernelDeleteAddressSpace(int uid)
    {
        int lookup = AddressSpaceLookup(uid, out uint record);
        if (lookup < 0)
        {
            return lookup;
        }

        if (GuestMemory.R32(record + 0x10) != UserAddressSpaceFlag)
        {
            return SceError.ERROR;
        }

        K.Objects.Remove(uid);
        return 0;
    }

    /// <summary>ksceKernelAddressSpaceFreeAllMemBlock (0x36159CF5)(uid): frees every memory block of a user address
    /// space's process (the kernel's address space keeps its blocks); 0.</summary>
    [Hle(Nid = 0x36159CF5, Library = "SceSysmemForKernel")]
    int ksceKernelAddressSpaceFreeAllMemBlock(int uid)
    {
        int lookup = AddressSpaceLookup(uid, out uint record);
        if (lookup < 0)
        {
            return lookup;
        }

        if (GuestMemory.R32(record + 0x10) != UserAddressSpaceFlag)
        {
            return 0;
        }

        int pid = (int)GuestMemory.R32(record + 0x14);
        List<MemBlock> blocks = new(K.Objects.Owned<MemBlock>(pid));
        foreach (MemBlock block in blocks)
        {
            ksceKernelFreeMemBlock(block.Uid);
        }

        return 0;
    }

    /// <summary>ksceKernelAddressSpaceSetPhyMemPart (0x7DF07D60)(uid, index, part): sets physical partition slot
    /// <paramref name="index"/> (+0x11C); 0.</summary>
    [Hle(Nid = 0x7DF07D60, Library = "SceSysmemForKernel")]
    int ksceKernelAddressSpaceSetPhyMemPart(int uid, uint index, uint part)
    {
        int lookup = AddressSpaceLookup(uid, out uint record);
        if (lookup < 0)
        {
            return lookup;
        }

        GuestMemory.W32(record + 0x11C + index * 4, part);
        return 0;
    }

    /// <summary>NID_47C55DCC(index, ScePhyMemPart **out): physical partition slot <paramref name="index"/> of the
    /// kernel's address space; an empty slot 0x80024300 (*out 0).</summary>
    [Hle(Nid = 0x47C55DCC, Library = "SceSysmemForKernel")]
    int NID_47C55DCC(uint index, uint partOut)
    {
        uint record = NID_2E5CC243();
        uint part = GuestMemory.R32(record + 0x11C + index * 4);
        GuestMemory.W32(partOut, part);
        return part != 0 ? 0 : NoMemoryRegion;
    }

    /// <summary>ksceKernelAddressSpaceVAtoPA (0x1BAF8C0B)(uid, mode, va, pa *out): the translation in the address
    /// space (ksceKernelMMUVAtoPAWithMode with it current); NULL 0x80020005.</summary>
    [Hle(Nid = 0x1BAF8C0B, Library = "SceSysmemForKernel")]
    int ksceKernelAddressSpaceVAtoPA(int uid, uint mode, uint address, uint physicalOut)
    {
        if (physicalOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int lookup = AddressSpaceLookup(uid, out _);
        return lookup < 0 ? lookup : ksceKernelMMUVAtoPAWithMode(mode, address, physicalOut);
    }

    /// <summary>NID_A7C91D62(uid, out): the address space's MMU context (+0x18); NULL 0x80020005.</summary>
    [Hle(Nid = 0xA7C91D62, Library = "SceSysmemForKernel")]
    int NID_A7C91D62(int uid, uint contextOut) => ReadAddressSpaceWord(uid, 0x18, contextOut, true);

    /// <summary>NID_FA1A73DD(uid, out): the physical address of the address space's I/O timer page (+0x164).</summary>
    [Hle(Nid = 0xFA1A73DD, Library = "SceSysmemForKernel")]
    int NID_FA1A73DD(int uid, uint valueOut) => ReadAddressSpaceWord(uid, 0x164, valueOut, false);

    int ReadAddressSpaceWord(int uid, uint offset, uint output, bool checkOutput)
    {
        if (checkOutput && output == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        int lookup = AddressSpaceLookup(uid, out uint record);
        if (lookup < 0)
        {
            return lookup;
        }

        GuestMemory.W32(output, GuestMemory.R32(record + offset));
        return 0;
    }

    /// <summary>NID_5E772E19(uid, pid): sets the address space's process (+0x14); 0.</summary>
    [Hle(Nid = 0x5E772E19, Library = "SceSysmemForKernel")]
    int NID_5E772E19(int uid, uint pid)
    {
        int lookup = AddressSpaceLookup(uid, out uint record);
        if (lookup < 0)
        {
            return lookup;
        }

        GuestMemory.W32(record + 0x14, pid);
        return 0;
    }

    /// <summary>NID_CEBC4963(uid, name): renames the address space "padsp: name"; 0.</summary>
    [Hle(Nid = 0xCEBC4963, Library = "SceSysmemForKernel")]
    int NID_CEBC4963(int uid, uint name)
    {
        int lookup = AddressSpaceLookup(uid, out _);
        if (lookup < 0)
        {
            return lookup;
        }

        if (K.Objects.Get<KernelObject>(uid) is { } kernelObject)
        {
            string newName = "padsp: " + (name != 0 ? GuestMemory.ReadCString(name, 0x20) : "");
            kernelObject.Name = newName.Length > 0x1F ? newName[..0x1F] : newName;
            lock (_namePointers)
            {
                _namePointers.Remove(uid);
            }
        }

        return 0;
    }

    /// <summary>NID_20D01AFA(uid): makes the address space current (the translation table base); VitaSharp's address
    /// spaces are the processes' own memory, so only the lookup is made.</summary>
    [Hle(Nid = 0x20D01AFA, Library = "SceSysmemForKernel")]
    int NID_20D01AFA(int uid) => AddressSpaceLookup(uid, out _);

    /// <summary>NID_C3EF4055(uid, info): the address space's information (0x654 bytes: +4 the UID, +8 its ASID byte,
    /// +0xC the partition count and from +0x10 their 0x30-byte information, +0x610 the physical partition count and
    /// from +0x614 the slots); NULL 0x80020005, another size 0x80020009.</summary>
    [Hle(Nid = 0xC3EF4055, Library = "SceSysmemForKernel")]
    int NID_C3EF4055(int uid, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0x654)
        {
            return MmuBadVectorSize;
        }

        int lookup = AddressSpaceLookup(uid, out uint record);
        if (lookup < 0)
        {
            return lookup;
        }

        GuestMemory.W32(info + 4, (uint)uid);
        uint context = GuestMemory.R32(record + 0x18);
        GuestMemory.Write(info + 8, context != 0 ? GuestMemory.Read<byte>(context + 8) : (byte)0);
        GuestMemory.W32(info + 0xC, 0);
        uint count = 0;
        for (uint i = 0; i < 0x20; i++)
        {
            uint partition = GuestMemory.R32(record + 0x1C + i * 4);
            if (partition == 0)
            {
                continue;
            }

            uint entry = info + 0x10 + count * 0x30;
            GuestMemory.W32(entry, 0x30);
            PartitionInfo(partition, entry);
            count++;
        }

        GuestMemory.W32(info + 0xC, count);
        uint parts = 0;
        for (uint i = 0; i < 0x10; i++)
        {
            uint part = GuestMemory.R32(record + 0x11C + i * 4);
            if (part != 0)
            {
                GuestMemory.W32(info + 0x614 + i * 4, part);
                parts++;
            }
        }

        GuestMemory.W32(info + 0x610, parts);
        return 0;
    }

    /// <summary>NID_FA348CB1(SceKernelAddressSpace *space, heaps): the MMU context's page-table heaps from a UID entry
    /// heap's size table (+8 the count, +0xC 12-byte entries {size, heap}): +0x18 the first heap of 0x40 bytes or more,
    /// +0x1C of 0x20 or more, +0x20 the table's +0x18; NULL 0x80020005; 0.</summary>
    [Hle(Nid = 0xFA348CB1, Library = "SceSysmemForKernel")]
    int NID_FA348CB1(uint record, uint heaps)
    {
        if (record == 0 || heaps == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint context = GuestMemory.R32(record + 0x18);
        GuestMemory.W32(context + 0x18, HeapForSize(heaps, 0x40));
        GuestMemory.W32(context + 0x1C, HeapForSize(heaps, 0x20));
        GuestMemory.W32(context + 0x20, GuestMemory.R32(heaps + 0x18));
        return 0;
    }

    /// <summary>The first heap of a size table whose item size is at least <paramref name="size"/>, or 0.</summary>
    static uint HeapForSize(uint table, uint size)
    {
        if (table == 0)
        {
            return 0;
        }

        uint count = GuestMemory.R32(table + 8);
        uint entries = GuestMemory.R32(table + 0xC);
        for (uint i = 0; i < count; i++)
        {
            uint entry = entries + i * 12;
            if (size <= GuestMemory.R32(entry) && GuestMemory.R32(entry + 4) != 0)
            {
                return GuestMemory.R32(entry + 4);
            }
        }

        return 0;
    }

    /// <summary>NID_456ECB54(SceKernelMMUContext *context, va): the coarse page table of the 1 MB section holding the
    /// address: the first-level descriptor of the section (the table at the context's +0x1C record, +0x10, one word
    /// per MB) when its type is a page table (bits 1:0 = 1), without the type bits; any other descriptor 0. A context
    /// without a first-level table (VitaSharp's MMU contexts until kernel code gives them one) answers 0.</summary>
    [Hle(Nid = 0x456ECB54, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_456ECB54(uint context, uint address)
    {
        uint record = context == 0 ? 0 : GuestMemory.R32(context + 0x1C);
        uint table = record == 0 ? 0 : GuestMemory.R32(record + 0x10);
        if (table == 0)
        {
            return 0;
        }

        uint descriptor = GuestMemory.R32(table + (address >> 20) * 4);
        return (descriptor & 3) == 1 ? descriptor & 0xFFFFFFFC : 0;
    }

    /// <summary>NID_0B5A3E93(va): the partition of the kernel's address space that holds the address, or 0.</summary>
    [Hle(Nid = 0x0B5A3E93, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_0B5A3E93(uint address)
    {
        uint record = NID_2E5CC243();
        for (uint i = 0; i < 0x20; i++)
        {
            uint partition = GuestMemory.R32(record + 0x1C + i * 4);
            if (partition == 0)
            {
                continue;
            }

            uint start = GuestMemory.R32(partition + 0x14);
            if (address >= start && address - start < GuestMemory.R32(partition + 0x18))
            {
                return partition;
            }
        }

        return 0;
    }

    /// <summary>NID_A541E0CF(uid, name, opt): a partition of the address space (opt 0x14 bytes: +4 the base, +8 the
    /// size) in its first free slot, of SceUIDPartitionClass; a NULL name or option 0x80020005, another option size
    /// 0x80020009; the partition's UID.</summary>
    [Hle(Nid = 0xA541E0CF, Library = "SceSysmemForKernel")]
    int NID_A541E0CF(int uid, uint name, uint option)
    {
        int lookup = AddressSpaceLookup(uid, out uint record);
        if (lookup < 0)
        {
            return lookup;
        }

        if (name == 0 || option == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(option) != 0x14)
        {
            return MmuBadVectorSize;
        }

        for (uint i = 0; i < 0x20; i++)
        {
            if (GuestMemory.R32(record + 0x1C + i * 4) != 0)
            {
                continue;
            }

            uint partition = KernelAlloc(PartitionSize, "kernel:partition", 0x10);
            GuestMemory.Span(partition, (int)PartitionSize).Clear();
            GuestMemory.W32(partition + 0x14, GuestMemory.R32(option + 4));
            GuestMemory.W32(partition + 0x18, GuestMemory.R32(option + 8));
            GuestMemory.W32(partition + 0x1C, GuestMemory.R32(record + 0x18));
            GuestMemory.W32(partition + 0x34, PartitionMagic);
            GuestMemory.W32(partition + 0x64, GuestMemory.R32(record + 0x14));
            GuestMemory.W32(record + 0x1C + i * 4, partition);
            int partitionUid = RegisterExistingObject(PartitionClass(), GuestMemory.ReadCString(name, 0x1F), partition, PartitionSize);
            GuestMemory.W32(record + 0x9C + i * 4, (uint)partitionUid);
            return partitionUid;
        }

        return NoMemoryRegion;
    }

    /// <summary>The 0x30-byte information of a partition record: +4 the record, +8 its base, +0xC its size, +0x10/+0x1C
    /// the halves of +0x20, +0x14 +0x24, +0x18 +0x08, +0x20.. the halves of +0x78 and +0x7C.</summary>
    static void PartitionInfo(uint partition, uint info)
    {
        GuestMemory.W32(info + 0x04, partition);
        GuestMemory.W32(info + 0x08, GuestMemory.R32(partition + 0x14));
        GuestMemory.W32(info + 0x0C, GuestMemory.R32(partition + 0x18));
        uint word20 = GuestMemory.R32(partition + 0x20);
        GuestMemory.W32(info + 0x10, word20 & 0xFFFF);
        GuestMemory.W32(info + 0x14, GuestMemory.R32(partition + 0x24));
        GuestMemory.W32(info + 0x18, GuestMemory.R32(partition + 0x08));
        GuestMemory.W32(info + 0x1C, word20 >> 16);
        uint word78 = GuestMemory.R32(partition + 0x78);
        uint word7C = GuestMemory.R32(partition + 0x7C);
        GuestMemory.W32(info + 0x20, word78 & 0xFFFF);
        GuestMemory.W32(info + 0x24, word78 >> 16);
        GuestMemory.W32(info + 0x28, word7C & 0xFFFF);
        GuestMemory.W32(info + 0x2C, word7C >> 16);
    }

    /// <summary>NID_FB101B86(uid, info): the 0x30-byte information of a partition by its UID; none 0x80020001, another
    /// size 0x80020009.</summary>
    [Hle(Nid = 0xFB101B86, Library = "SceSysmemForKernel")]
    int NID_FB101B86(int uid, uint info)
    {
        uint partition = NID_FA6C098C(uid);
        if (partition == 0)
        {
            return SceError.ERROR;
        }

        if (GuestMemory.R32(info) != 0x30)
        {
            return MmuBadVectorSize;
        }

        PartitionInfo(partition, info);
        return 0;
    }

    /// <summary>NID_100B4029(info[], max, count): the 0x10-byte entries of sysmem's partition list ({0x10, record, +0x10,
    /// +0x0C}), at most <paramref name="max"/>; entries wanted with no buffer or count 0x80020005; the list's length.</summary>
    [Hle(Nid = 0x100B4029, Library = "SceSysmemForKernel")]
    int NID_100B4029(uint infos, uint max, uint countOut)
    {
        if (max != 0 && (infos == 0 || countOut == 0))
        {
            return SceError.INVALID_ARGUMENT;
        }

        AddressSpaceOf(KernelSpace.KernelPid);
        List<uint> partitions;
        lock (_partitions)
        {
            partitions = new List<uint>(_partitions.Values);
        }

        uint written = 0;
        foreach (uint partition in partitions)
        {
            if (written < max)
            {
                uint entry = infos + written * 0x10;
                GuestMemory.W32(entry, 0x10);
                GuestMemory.W32(entry + 4, partition);
                GuestMemory.W32(entry + 8, GuestMemory.R32(partition + 0x10));
                GuestMemory.W32(entry + 0xC, GuestMemory.R32(partition + 0x0C));
                written++;
            }
        }

        if (countOut != 0)
        {
            GuestMemory.W32(countOut, written);
        }

        return partitions.Count;
    }

    /// <summary>The memory blocks of a partition whose address is in [low, high) (the partition's process: +0x64),
    /// written as 0x30-byte block information while they fit <paramref name="bufferBytes"/>; the count written into
    /// <paramref name="countOut"/>; the number of blocks in the partition.</summary>
    int PartitionBlocks(uint partition, uint low, uint high, int pidFilter, uint infos, uint bufferBytes, uint countOut)
    {
        int pid = (int)GuestMemory.R32(partition + 0x64);
        if (pid == 0)
        {
            pid = KernelSpace.KernelPid;
        }

        List<MemBlock> blocks = new();
        foreach (MemBlock block in K.Objects.All<MemBlock>())
        {
            if (OwnerOf(block) == pid)
            {
                blocks.Add(block);
            }
        }

        blocks.Sort((left, right) => left.Address.CompareTo(right.Address));
        uint written = 0;
        foreach (MemBlock block in blocks)
        {
            bool inRange = block.Address >= low && block.Address < high;
            bool ownerMatches = pidFilter == 0 || OwnerOf(block) == pidFilter;
            if (inRange && ownerMatches && infos != 0 && written < bufferBytes / 0x30)
            {
                uint info = infos + written * 0x30;
                GuestMemory.W32(info, 0x30);
                NID_BA441FD0(block.Uid, info);
                written++;
            }
        }

        GuestMemory.W32(countOut, written);
        return blocks.Count;
    }

    /// <summary>NID_1D19C864(partition, low, high, pid, ?, ?, info[], bufferBytes, count): the partition's blocks in
    /// [low, high) of process <paramref name="pid"/> (0: any); a NULL partition or count 0x80020005.</summary>
    [Hle(Nid = 0x1D19C864, Library = "SceSysmemForKernel")]
    int NID_1D19C864(uint partition, uint low, uint high, int pid, uint unused1, uint unused2, uint infos, uint bufferBytes, uint countOut)
    {
        if (partition == 0 || countOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return PartitionBlocks(partition, low, high, pid, infos, bufferBytes, countOut);
    }

    /// <summary>NID_AD19285A(partition, low, high, ?, info[], bufferBytes, count): the same for any process.</summary>
    [Hle(Nid = 0xAD19285A, Library = "SceSysmemForKernel")]
    int NID_AD19285A(uint partition, uint low, uint high, uint unused, uint infos, uint bufferBytes, uint countOut)
    {
        if (partition == 0 || countOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return PartitionBlocks(partition, low, high, 0, infos, bufferBytes, countOut);
    }
}
