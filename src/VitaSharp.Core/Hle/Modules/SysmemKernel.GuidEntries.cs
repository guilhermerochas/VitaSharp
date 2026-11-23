using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSysmemForKernel's walks over the UID entries, as the FW 3.74 sysmem.skprx has them (decompiled, study only):
/// the UID vectors of a class (all objects, a process's handles, the handles of one object, the objects of one
/// owner), the 0x24-byte entry information records, the single-entry getters, the GUID set/create calls that put an
/// existing object behind a UID, and the module's hash. VitaSharp keeps one UID space for the kernel and the
/// processes, so a process's PUIDs are the objects it owns (kscePUIDOpenByGUID gives the object to the process) and a
/// PUID entry is the object's own entry. An entry, as the firmware lays it out (0x20 bytes): +0x00 the object, +0x04
/// the owner pid, +0x08 the flags (0x100000 a GUID, bits 16..18 its visibility level, 0 here), +0x0D the open count,
/// +0x0E the reference count, +0x10 the UID, +0x18 the GUID behind a PUID, +0x1C the name.
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>An entry's flag for a GUID (sysmem's 0x300000 mask: a GUID or a PUID).</summary>
    const uint GuidEntryFlag = 0x100000;

    /// <summary>The size of one entry information record (GUIDGetEntryInfo*).</summary>
    const uint EntryInfoSize = 0x24;

    /// <summary>Each object's entry record in kernel memory (made on first request): UID -> address.</summary>
    [SnapshotMissing] // states of builds before it: made again on first request
    readonly Dictionary<int, uint> _guidEntries = new();

    /// <summary>The class of an object: the class kernel code gave it, the memory block and heap classes for the
    /// HLE's blocks and heaps (what sysmem's own objects have), the root class for the rest.</summary>
    uint ClassOf(KernelObject kernelObject)
    {
        return kernelObject switch
        {
            KernelData { Kind: KernelData.ClassObject } data => data.Aux,
            KernelData { Kind: KernelData.Heap } => ksceKernelGetUIDHeapClassThroughEntry(),
            MemBlock => ksceKernelGetUIDMemBlockClassThroughEntry(),
            _ => RootClass(),
        };
    }

    /// <summary>The objects whose class is <paramref name="cls"/> or derives from it (none for a NULL class), by
    /// UID.</summary>
    List<KernelObject> ObjectsOfClass(uint cls, Func<KernelObject, bool>? filter = null)
    {
        List<KernelObject> found = new();
        if (cls == 0)
        {
            return found;
        }

        foreach (KernelObject kernelObject in K.Objects.All<KernelObject>())
        {
            if (DerivesFrom(ClassOf(kernelObject), cls) && (filter == null || filter(kernelObject)))
            {
                found.Add(kernelObject);
            }
        }

        found.Sort((left, right) => left.Uid.CompareTo(right.Uid));
        return found;
    }

    /// <summary>The pid that owns an object (the kernel's for its own).</summary>
    static int OwnerOf(KernelObject kernelObject)
    {
        return kernelObject.OwnerPid == 0 || kernelObject.OwnerPid == -1 ? KernelSpace.KernelPid : kernelObject.OwnerPid;
    }

    /// <summary>Writes the UIDs of <paramref name="objects"/> into the vector (at most <paramref name="max"/>); the count
    /// written into <paramref name="countOut"/> when given.</summary>
    static void WriteUidVector(List<KernelObject> objects, uint vector, uint max, uint countOut)
    {
        uint written = 0;
        if (vector != 0)
        {
            foreach (KernelObject kernelObject in objects)
            {
                if (written >= max)
                {
                    break;
                }

                GuestMemory.W32(vector + written * 4, (uint)kernelObject.Uid);
                written++;
            }
        }

        if (countOut != 0)
        {
            GuestMemory.W32(countOut, written);
        }
    }

    /// <summary>The object's entry record, its fields brought up to date.</summary>
    uint GuidEntryOf(KernelObject kernelObject)
    {
        uint entry;
        lock (_guidEntries)
        {
            if (!_guidEntries.TryGetValue(kernelObject.Uid, out entry))
            {
                entry = KernelAlloc(0x20, "kernel:guidentry", 0x10);
                _guidEntries[kernelObject.Uid] = entry;
            }
        }

        GuestMemory.W32(entry + 0x00, BodyOf(kernelObject));
        GuestMemory.W32(entry + 0x04, (uint)OwnerOf(kernelObject));
        GuestMemory.W32(entry + 0x08, GuidEntryFlag);
        GuestMemory.Write(entry + 0x0D, (byte)Math.Min(Volatile.Read(ref kernelObject.OpenCount), 0xF0));
        int references = kernelObject is KernelData data ? Volatile.Read(ref data.References) : 0;
        GuestMemory.Write(entry + 0x0E, (ushort)Math.Min(references, 0x400));
        GuestMemory.W32(entry + 0x10, (uint)kernelObject.Uid);
        GuestMemory.W32(entry + 0x18, (uint)kernelObject.Uid);
        GuestMemory.W32(entry + 0x1C, NamePointerOf(kernelObject));
        return entry;
    }

    /// <summary>One entry information record (0x24 bytes): +0 its size, +4 the UID, +8 the object, +0xC its class, +0x10
    /// the GUID behind it, +0x14 the open count, +0x16 the reference count, +0x18 the flags, +0x1C the name.</summary>
    void WriteEntryInfo(uint info, KernelObject kernelObject)
    {
        uint entry = GuidEntryOf(kernelObject);
        GuestMemory.W32(info + 0x00, EntryInfoSize);
        GuestMemory.W32(info + 0x04, (uint)kernelObject.Uid);
        GuestMemory.W32(info + 0x08, GuestMemory.R32(entry));
        GuestMemory.W32(info + 0x0C, ClassOf(kernelObject));
        GuestMemory.W32(info + 0x10, GuestMemory.R32(entry + 0x18));
        GuestMemory.Write(info + 0x14, (ushort)GuestMemory.Read<byte>(entry + 0x0D));
        GuestMemory.Write(info + 0x16, GuestMemory.Read<ushort>(entry + 0x0E));
        GuestMemory.W32(info + 0x18, GuestMemory.R32(entry + 0x08));
        GuestMemory.W32(info + 0x1C, GuestMemory.R32(entry + 0x1C));
    }

    /// <summary>Writes entry information records of <paramref name="objects"/> (at most <paramref name="max"/>); the count
    /// written into <paramref name="countOut"/> when given.</summary>
    void WriteEntryInfos(List<KernelObject> objects, uint infos, uint max, uint countOut)
    {
        uint written = 0;
        if (infos != 0)
        {
            foreach (KernelObject kernelObject in objects)
            {
                if (written >= max)
                {
                    break;
                }

                WriteEntryInfo(infos + written * EntryInfoSize, kernelObject);
                written++;
            }
        }

        if (countOut != 0)
        {
            GuestMemory.W32(countOut, written);
        }
    }

    /// <summary>The process checks of the PUID walks (sysmem's lookup of a process's entry heap): the kernel's pid
    /// 0x80020005, a pid that is no process 0x80029001.</summary>
    int ProcessWalkCheck(int pid)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ProcessOf(pid) == null ? SceError.INVALID_PID : 0;
    }

    /// <summary>ksceGUIDGetUIDVectorByClass (0x52137FA3)(SceClass *cls, level, SceUID *vector, max, SceSize *count):
    /// the GUIDs of the class and its subclasses visible at the level (every level sees VitaSharp's objects); 0.</summary>
    [Hle(Nid = 0x52137FA3, Library = "SceSysmemForKernel")]
    int ksceGUIDGetUIDVectorByClass(uint cls, uint level, uint vector, uint max, uint countOut)
    {
        WriteUidVector(ObjectsOfClass(cls), vector, max, countOut);
        return 0;
    }

    /// <summary>GUIDGetUIDVectorWeakByClass (0xE9957329)(cls, level, vector, max, count): the same walk, made 16 entries
    /// at a time without holding the table's lock; 0.</summary>
    [Hle(Nid = 0xE9957329, Library = "SceSysmemForKernel")]
    int NID_E9957329(uint cls, uint level, uint vector, uint max, uint countOut) => ksceGUIDGetUIDVectorByClassThroughEntry(cls, level, vector, max, countOut);

    /// <summary>PUIDGetUIDVectorByClass (0x08C05493)(pid, cls, level, vector, max, count): the handles process
    /// <paramref name="pid"/> holds on objects of the class.</summary>
    [Hle(Nid = 0x08C05493, Library = "SceSysmemForKernel")]
    int NID_08C05493(int pid, uint cls, uint level, uint vector, uint max, uint countOut)
    {
        int check = ProcessWalkCheck(pid);
        if (check != 0)
        {
            return check;
        }

        WriteUidVector(ObjectsOfClass(cls, kernelObject => kernelObject.OwnerPid == pid), vector, max, countOut);
        return 0;
    }

    /// <summary>GUIDGetEntryInfoClass (0x71E934DB)(cls, level, flags, info[], max, count): entry information of the
    /// class's GUIDs; a negative max, or entries wanted with no buffer, 0x80020005.</summary>
    [Hle(Nid = 0x71E934DB, Library = "SceSysmemForKernel")]
    int NID_71E934DB(uint cls, uint level, uint flags, uint infos, int max, uint countOut)
    {
        if (max < 0 || (max > 0 && infos == 0))
        {
            return SceError.INVALID_ARGUMENT;
        }

        WriteEntryInfos(ObjectsOfClass(cls), infos, (uint)max, countOut);
        return 0;
    }

    /// <summary>PUIDGetEntryInfoAll (0x3C3005EE)(pid, level, flags, info[], max, count): entry information of every
    /// handle of the process; the same checks.</summary>
    [Hle(Nid = 0x3C3005EE, Library = "SceSysmemForKernel")]
    int NID_3C3005EE(int pid, uint level, uint flags, uint infos, int max, uint countOut)
    {
        if (max < 0 || (max > 0 && infos == 0))
        {
            return SceError.INVALID_ARGUMENT;
        }

        int check = ProcessWalkCheck(pid);
        if (check != 0)
        {
            return check;
        }

        WriteEntryInfos(ObjectsOfClass(RootClass(), kernelObject => kernelObject.OwnerPid == pid), infos, (uint)max, countOut);
        return 0;
    }

    /// <summary>NID_3BF0DE65(?, kinds, info[], bufferBytes, count): entry information of every entry of the kinds asked
    /// (bit 0: GUIDs; bit 1: process handles, which are the same entries here), as many as fit the buffer; a buffer
    /// size with no buffer 0x80020005.</summary>
    [Hle(Nid = 0x3BF0DE65, Library = "SceSysmemForKernel")]
    int NID_3BF0DE65(uint unused, uint kinds, uint infos, uint bufferBytes, uint countOut)
    {
        if (infos == 0 && bufferBytes != 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        List<KernelObject> objects = (kinds & 3) != 0 ? ObjectsOfClass(RootClass()) : new List<KernelObject>();
        WriteEntryInfos(objects, infos, (bufferBytes + EntryInfoSize - 1) / EntryInfoSize, countOut);
        return 0;
    }

    /// <summary>NID_66489B4B(pid, ?, SceUID *vector, vectorBytes, count): the GUIDs the process owns, as many as fit
    /// the vector.</summary>
    [Hle(Nid = 0x66489B4B, Library = "SceSysmemForKernel")]
    int NID_66489B4B(int pid, uint unused, uint vector, uint vectorBytes, uint countOut)
    {
        WriteUidVector(ObjectsOfClass(RootClass(), kernelObject => OwnerOf(kernelObject) == pid), vector, (vectorBytes + 3) / 4, countOut);
        return 0;
    }

    /// <summary>NID_CE16AD91(pid, guid, SceUID *vector, vectorBytes, ?, count): the handles process
    /// <paramref name="pid"/> holds on object <paramref name="guid"/> (the object's own UID here, once the process
    /// opened it); the kernel's pid 0x80020005.</summary>
    [Hle(Nid = 0xCE16AD91, Library = "SceSysmemForKernel")]
    int NID_CE16AD91(int pid, int guid, uint vector, uint vectorBytes, uint unused, uint countOut)
    {
        int check = ProcessWalkCheck(pid);
        if (check != 0)
        {
            return check;
        }

        List<KernelObject> objects = ObjectsOfClass(RootClass(), kernelObject => kernelObject.Uid == guid && kernelObject.OwnerPid == pid);
        WriteUidVector(objects, vector, (vectorBytes + 3) / 4, countOut);
        return 0;
    }

    /// <summary>scePUIDGetInfo (0x857701D4)(pid, puid, info): the handle's entry (0x14 bytes: +4 its UID, +8 its
    /// owner, +0xC its flags, +0x10 the reserved byte, +0x11 the open count, +0x12 the reference count); not a UID
    /// 0x80024501, NULL 0x80020005, another size 0x80020009.</summary>
    [Hle(Nid = 0x857701D4, Library = "SceSysmemForKernel")]
    int NID_857701D4(int pid, int puid, uint info)
    {
        if (!IsGuidForm(puid))
        {
            return InvalidPuid;
        }

        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0x14)
        {
            return MmuBadVectorSize;
        }

        int check = ProcessWalkCheck(pid);
        if (check != 0)
        {
            return check;
        }

        if (K.Objects.Get<KernelObject>(puid) is not { } kernelObject)
        {
            return InvalidPuid;
        }

        uint entry = GuidEntryOf(kernelObject);
        GuestMemory.W32(info + 0x04, GuestMemory.R32(entry + 0x10));
        GuestMemory.W32(info + 0x08, GuestMemory.R32(entry + 0x04));
        GuestMemory.W32(info + 0x0C, GuestMemory.R32(entry + 0x08));
        GuestMemory.Write(info + 0x10, GuestMemory.Read<byte>(entry + 0x0C));
        GuestMemory.Write(info + 0x11, GuestMemory.Read<byte>(entry + 0x0D));
        GuestMemory.Write(info + 0x12, GuestMemory.Read<ushort>(entry + 0x0E));
        return 0;
    }

    /// <summary>sceGUIDGetEntry (0xFF464A49)(guid, entry **out): the GUID's entry record; none 0x80024501.</summary>
    [Hle(Nid = 0xFF464A49, Library = "SceSysmemForKernel")]
    int NID_FF464A49(int guid, uint entryOut)
    {
        if (GuidObject(guid) is not { } kernelObject)
        {
            return InvalidPuid;
        }

        GuestMemory.W32(entryOut, GuidEntryOf(kernelObject));
        return 0;
    }

    /// <summary>NID_1FF2749A(pid, guid, opt): a handle of the process for the GUID (kscePUIDOpenByGUID; the option's
    /// attribute and name choose what the firmware's handle records, which VitaSharp's single UID space does not
    /// keep); the kernel's pid 0x80020005, not a GUID 0x80024501.</summary>
    [Hle(Nid = 0x1FF2749A, Library = "SceSysmemForKernel")]
    int NID_1FF2749A(int pid, int guid, uint option)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (!IsGuidForm(guid) || (guid & 0x40000000) != 0)
        {
            return InvalidPuid;
        }

        return kscePUIDOpenByGUIDThroughEntry(pid, guid);
    }

    /// <summary>GUIDSet (0xBF275C5A)(guid, cls, name, object): puts an existing object behind a GUID sysmem made at
    /// boot (the class written into the object's +4); not a GUID 0x80024501. VitaSharp's objects carry their class
    /// on the object, so a kernel-defined object is the one changed.</summary>
    [Hle(Nid = 0xBF275C5A, Library = "SceSysmemForKernel")]
    int NID_BF275C5A(int guid, uint cls, uint name, uint body)
    {
        if (!IsGuidForm(guid) || (guid & 0x40000000) != 0)
        {
            return InvalidPuid;
        }

        if (K.Objects.Get<KernelObject>(guid) is not { } kernelObject)
        {
            return InvalidPuid;
        }

        SetGuidObject(kernelObject, cls, name, body);
        return 0;
    }

    /// <summary>GUIDSetCNOA (0x5EDDEEB5)(guid, cls, name, object, attr): the same with the attribute word, which must
    /// have bit 0x1000 (0x80020005 otherwise).</summary>
    [Hle(Nid = 0x5EDDEEB5, Library = "SceSysmemForKernel")]
    int NID_5EDDEEB5(int guid, uint cls, uint name, uint body, uint attr)
    {
        if (!IsGuidForm(guid) || (guid & 0x40000000) != 0)
        {
            return InvalidPuid;
        }

        if (K.Objects.Get<KernelObject>(guid) is not { } kernelObject)
        {
            return InvalidPuid;
        }

        if ((attr & 0x1000) == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        SetGuidObject(kernelObject, cls, name, body);
        return 0;
    }

    void SetGuidObject(KernelObject kernelObject, uint cls, uint name, uint body)
    {
        if (name != 0)
        {
            ksceGUIDSetNameThroughEntry(kernelObject.Uid, name);
        }

        if (body == 0 || kernelObject is not KernelData { Kind: KernelData.ClassObject } data)
        {
            return;
        }

        data.Address = body;
        if (cls != 0)
        {
            GuestMemory.W32(body + 4, cls);
            data.Aux = cls;
        }
    }

    /// <summary>GUIDCreate with an object (0x6DB44A42)(cls, name, object): a new GUID for an object kernel code already
    /// laid out (its +0 the object, +4 the class written); NULL name 0x80020005; the GUID.</summary>
    [Hle(Nid = 0x6DB44A42, Library = "SceSysmemForKernel")]
    int NID_6DB44A42(uint cls, uint name, uint body)
    {
        if (name == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return RegisterExistingObject(cls, GuestMemory.ReadCString(name, 31), body, cls != 0 ? GuestMemory.Read<ushort>(cls + 0x1A) : 0u);
    }

    /// <summary>Registers kernel memory kernel code (or sysmem itself) laid out as an object of the class: the UID.</summary>
    int RegisterExistingObject(uint cls, string name, uint body, uint size)
    {
        if (body != 0)
        {
            GuestMemory.W32(body, body);
            GuestMemory.W32(body + 4, cls);
        }

        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return K.Objects.Register(new KernelData { Name = name, Kind = KernelData.ClassObject, Address = body, Size = size, Aux = cls });
        }
    }

    /// <summary>NID_EB48682E(data, length, seed): sysmem's 32-bit MurmurHash3 of the bytes (whole words read as
    /// words; the tail bytes rotated by 16 instead of 15, as the FW 3.74 code does).</summary>
    [Hle(Nid = 0xEB48682E, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_EB48682E(uint data, int length, uint seed) => SysmemHash(data, length, seed);

    internal static uint SysmemHash(uint data, int length, uint seed)
    {
        const uint C1 = 0xCC9E2D51, C2 = 0x1B873593;
        uint hash = seed;
        int blocks = length / 4;
        for (int i = 0; i < blocks; i++)
        {
            uint word = GuestMemory.R32(data + (uint)i * 4) * C1;
            word = ((word >> 17) | (word << 15)) * C2;
            hash ^= word;
            hash = ((hash >> 19) | (hash << 13)) * 5 + 0xE6546B64;
        }

        uint tailAddress = data + (uint)blocks * 4;
        int tailLength = length & 3;
        if (tailLength != 0)
        {
            uint tail = 0;
            if (tailLength == 3)
            {
                tail |= (uint)GuestMemory.Read<byte>(tailAddress + 2) << 16;
            }

            if (tailLength >= 2)
            {
                tail |= (uint)GuestMemory.Read<byte>(tailAddress + 1) << 8;
            }

            tail ^= GuestMemory.Read<byte>(tailAddress);
            tail *= C1;
            hash ^= ((tail >> 16) | (tail << 16)) * C2;
        }

        hash ^= (uint)length;
        hash ^= hash >> 16;
        hash *= 0x85EBCA6B;
        hash ^= hash >> 13;
        hash *= 0xC2B2AE35;
        return hash ^ (hash >> 16);
    }
}
