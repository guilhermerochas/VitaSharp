using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceSysmemForKernel entries on UID classes, objects and sysmem's own words, as the FW 3.74 sysmem.skprx has them
/// (decompiled, study only): the classes sysmem defines (memory block, heap, DLink) linked under the root class,
/// lookups of a class by name and of objects by class, the field readers kernel code uses on a class or an object
/// it holds, the empty entries of this build (they answer 0 or 1), and the words other modules leave in sysmem.
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>The classes sysmem defines at boot, in kernel memory (0 until first asked): by name.</summary>
    [SnapshotMissing] // states of builds before it: made again on first request
    readonly Dictionary<string, uint> _sysmemClasses = new();

    /// <summary>The words other modules set in sysmem (by the export's offset in sysmem's data).</summary>
    [SnapshotMissing] // states of builds before it: none had been set
    readonly Dictionary<int, uint> _sysmemWords = new();

    /// <summary>The six handler words of NID_B591E49F.</summary>
    [SnapshotMissing] // states of builds before it: none had been set
    readonly uint[] _sysmemHandlers = new uint[6];

    const int ClassNotFound = unchecked((int)0x8002450F);

    /// <summary>A class sysmem defines at boot (SceUIDMemBlockClass 0x40-byte objects, SceUIDHeapClass 0x80-byte,
    /// SceUIDDLinkClass), linked under the root class, made the first time it is asked for.</summary>
    uint SysmemClass(string name, uint itemSize)
    {
        uint root = RootClass();
        lock (_sysmemClasses)
        {
            if (_sysmemClasses.TryGetValue(name, out uint existing))
            {
                return existing;
            }

            uint cls = KernelAlloc(0x50, $"kernel:{name}");
            uint namePointer = cls + 0x30;
            GuestMemory.WriteCString(namePointer, name, 0x20);
            ksceKernelCreateClass(cls, namePointer, root, itemSize, 0, 0);
            _sysmemClasses[name] = cls;
            return cls;
        }
    }

    /// <summary>ksceKernelGetUIDMemBlockClass (0x86681B64)(): the memory block class.</summary>
    [Hle(Nid = 0x86681B64, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint ksceKernelGetUIDMemBlockClass() => SysmemClass("SceUIDMemBlockClass", MemBlockObjectSize);

    /// <summary>ksceKernelGetUIDHeapClass (0x7C878E94)(): the heap class.</summary>
    [Hle(Nid = 0x7C878E94, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint ksceKernelGetUIDHeapClass() => SysmemClass("SceUIDHeapClass", 0x80);

    /// <summary>ksceKernelGetUIDDLinkClass (0xE4193EB6)(): the DLink class.</summary>
    [Hle(Nid = 0xE4193EB6, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint ksceKernelGetUIDDLinkClass() => SysmemClass("SceUIDDLinkClass", 0x20);

    /// <summary>ksceKernelFindClassByName (0x7D87F706)(name, SceClass **cls): the class of the circular class list
    /// (from the root, through +0 next) whose name matches in its first 31 characters; NULL 0x80020005, none
    /// 0x8002450F.</summary>
    [Hle(Nid = 0x7D87F706, Library = "SceSysmemForKernel")]
    int ksceKernelFindClassByName(uint name, uint classOut)
    {
        if (name == 0 || classOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        ksceKernelGetUIDMemBlockClass();
        ksceKernelGetUIDHeapClass();
        ksceKernelGetUIDDLinkClass();
        string wanted = GuestMemory.ReadCString(name, 31);
        uint root = RootClass();
        uint cls = root;
        do
        {
            uint className = GuestMemory.R32(cls + 0xC);
            if (className != 0 && GuestMemory.ReadCString(className, 31) == wanted)
            {
                GuestMemory.W32(classOut, cls);
                return 0;
            }

            cls = GuestMemory.R32(cls);
        }
        while (cls != 0 && cls != root);
        return ClassNotFound;
    }

    /// <summary>NID_7F7D5C4A(SceClass *cls, SceClass *ancestor): 1 when <paramref name="ancestor"/> is the class or one
    /// of its parents (+0x10), else 0.</summary>
    [Hle(Nid = 0x7F7D5C4A, Library = "SceSysmemForKernel")]
    int NID_7F7D5C4A(uint cls, uint ancestor)
    {
        while (cls != 0)
        {
            if (cls == ancestor)
            {
                return 1;
            }

            cls = GuestMemory.R32(cls + 0x10);
        }

        return 0;
    }

    /// <summary>NID_03A2CAF0(SceClass *cls): the class's parent (+0x10).</summary>
    [Hle(Nid = 0x03A2CAF0, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_03A2CAF0(uint cls) => GuestMemory.R32(cls + 0x10);

    /// <summary>NID_17F12DCE(object): its word +0x08.</summary>
    [Hle(Nid = 0x17F12DCE, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_17F12DCE(uint record) => GuestMemory.R32(record + 0x08);

    /// <summary>NID_5FD15C6E(object): its word +0x1C.</summary>
    [Hle(Nid = 0x5FD15C6E, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_5FD15C6E(uint record) => GuestMemory.R32(record + 0x1C);

    /// <summary>NID_797C858B(object): its halfword +0x0E.</summary>
    [Hle(Nid = 0x797C858B, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_797C858B(uint record) => GuestMemory.Read<ushort>(record + 0x0E);

    /// <summary>NID_F3DB2D71(object): its byte +0x0D (a UID entry's open count).</summary>
    [Hle(Nid = 0xF3DB2D71, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_F3DB2D71(uint record) => GuestMemory.Read<byte>(record + 0x0D);

    /// <summary>NID_39D5A3B7(SceKernelMemBlock *block, void **end): the block's base (+0x18) plus its guard (0x1000
    /// or 0x2000 by bits 4..11 of +0x30: 0x10, 0x20); 0.</summary>
    [Hle(Nid = 0x39D5A3B7, Library = "SceSysmemForKernel")]
    int NID_39D5A3B7(uint block, uint endOut)
    {
        uint guard = (GuestMemory.R32(block + 0x30) & 0xFF0) switch
        {
            0x10 => 0x1000u,
            0x20 => 0x2000u,
            _ => 0u,
        };
        GuestMemory.W32(endOut, GuestMemory.R32(block + 0x18) + guard);
        return 0;
    }

    /// <summary>NID_811C452B(object, value): sets its word +0x9C (under its lock at +0x28); 0.</summary>
    [Hle(Nid = 0x811C452B, Library = "SceSysmemForKernel")]
    int NID_811C452B(uint record, uint value)
    {
        GuestMemory.W32(record + 0x9C, value);
        return 0;
    }

    /// <summary>NID_1F84FE44(SceKernelAddressSpace *space, index): partition <paramref name="index"/> of the address
    /// space (+0x1C).</summary>
    [Hle(Nid = 0x1F84FE44, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_1F84FE44(uint space, uint index) => GuestMemory.R32(space + 0x1C + index * 4);

    /// <summary>NID_6D563733(SceKernelAddressSpace *space): adds 1 to the address space's counter (+0x80); the new
    /// value.</summary>
    [Hle(Nid = 0x6D563733, Library = "SceSysmemForKernel", ReturnsValue = true)]
    int NID_6D563733(uint space) => Interlocked.Increment(ref GuestMemory.Ref<int>(space + 0x80));

    /// <summary>ksceGUIDGetObjectWithClass (0x0E43E113)(guid, SceClass *cls, SceObjectBase **obj): the object's body
    /// when it is of the class or a subclass (no reference taken); not a GUID 0x80024501, another class
    /// 0x80024509.</summary>
    [Hle(Nid = 0x0E43E113, Library = "SceSysmemForKernel")]
    int ksceGUIDGetObjectWithClass(int guid, uint cls, uint objOut)
    {
        if (!IsGuidForm(guid))
        {
            return InvalidPuid;
        }

        if (K.Objects.Get<KernelObject>(guid) is not { } kernelObject)
        {
            return SceError.INVALID_UID;
        }

        uint objectClass = NID_368F1C36(guid);
        if (cls != 0 && NID_7F7D5C4A(objectClass, cls) == 0)
        {
            return unchecked((int)0x80024509);
        }

        if (objOut != 0)
        {
            GuestMemory.W32(objOut, BodyOf(kernelObject));
        }

        return 0;
    }

    /// <summary>ksceKernelGetMemBlockType (0xD44FE44B)(uid, uint *type): the type of a memory block; NULL
    /// 0x80020005.</summary>
    [Hle(Nid = 0xD44FE44B, Library = "SceSysmemForKernel")]
    int ksceKernelGetMemBlockType(int uid, uint typeOut)
    {
        if (typeOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        GuestMemory.W32(typeOut, block.Type);
        return 0;
    }

    /// <summary>NID_48D2E408(type): the low byte of the type's attribute word (its access), 0 for an unknown type.</summary>
    [Hle(Nid = 0x48D2E408, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_48D2E408(uint type) => MemBlockTypeInfo(type) & 0xFF;

    /// <summary>NID_2CFD8C9A(kind, offset, void **address): for kind 0x10002 (the main memory window) the address of
    /// the offset (0x40000000 + offset); another kind 0x80024C02.</summary>
    [Hle(Nid = 0x2CFD8C9A, Library = "SceSysmemForKernel")]
    int NID_2CFD8C9A(uint kind, uint offset, uint addressOut)
    {
        if (kind != 0x10002)
        {
            return unchecked((int)0x80024C02);
        }

        GuestMemory.W32(addressOut, offset + 0x40000000);
        return 0;
    }

    /// <summary>NID_B591E49F(index, handler): sets handler <paramref name="index"/> (below 6, else 0x80020005);
    /// 0.</summary>
    [Hle(Nid = 0xB591E49F, Library = "SceSysmemForKernel")]
    int NID_B591E49F(uint index, uint handler)
    {
        if (index >= _sysmemHandlers.Length)
        {
            return SceError.INVALID_ARGUMENT;
        }

        _sysmemHandlers[index] = handler;
        return 0;
    }

    uint SysmemWord(int key)
    {
        lock (_sysmemWords)
        {
            return _sysmemWords.GetValueOrDefault(key);
        }
    }

    void SetSysmemWord(int key, uint value)
    {
        lock (_sysmemWords)
        {
            _sysmemWords[key] = value;
        }
    }

    /// <summary>NID_3753C584(value): sets the process manager's word in sysmem.</summary>
    [Hle(Nid = 0x3753C584, Library = "SceSysmemForKernel")]
    void NID_3753C584(uint value) => SetSysmemWord(0x294, value);

    /// <summary>NID_62800A6B(): the word NID_3753C584 set (0 until then).</summary>
    [Hle(Nid = 0x62800A6B, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_62800A6B() => SysmemWord(0x294);

    /// <summary>NID_DCEF10B2(value): sets sysmem's word +0x2A0 (no export reads it back).</summary>
    [Hle(Nid = 0xDCEF10B2, Library = "SceSysmemForKernel")]
    void NID_DCEF10B2(uint value) => SetSysmemWord(0x2A0, value);

    // ------------------------------------------------------------------ the empty entries of FW 3.74

    /// <summary>NID_2C7B8D65(): 0 (the FW 3.74 entry does nothing else).</summary>
    [Hle(Nid = 0x2C7B8D65, Library = "SceSysmemForKernel")]
    int NID_2C7B8D65() => 0;

    /// <summary>NID_31FA81B5(): 0.</summary>
    [Hle(Nid = 0x31FA81B5, Library = "SceSysmemForKernel")]
    int NID_31FA81B5() => 0;

    /// <summary>NID_320E907F(): 0.</summary>
    [Hle(Nid = 0x320E907F, Library = "SceSysmemForKernel")]
    int NID_320E907F() => 0;

    /// <summary>NID_42834422(): 0.</summary>
    [Hle(Nid = 0x42834422, Library = "SceSysmemForKernel")]
    int NID_42834422() => 0;

    /// <summary>NID_50980298(): 0.</summary>
    [Hle(Nid = 0x50980298, Library = "SceSysmemForKernel")]
    int NID_50980298() => 0;

    /// <summary>NID_666C878F(): 0.</summary>
    [Hle(Nid = 0x666C878F, Library = "SceSysmemForKernel")]
    int NID_666C878F() => 0;

    /// <summary>NID_6DBDA03B(): 0.</summary>
    [Hle(Nid = 0x6DBDA03B, Library = "SceSysmemForKernel")]
    int NID_6DBDA03B() => 0;

    /// <summary>NID_8F0B5536(): 0.</summary>
    [Hle(Nid = 0x8F0B5536, Library = "SceSysmemForKernel")]
    int NID_8F0B5536() => 0;

    /// <summary>NID_93D0A183(): 0.</summary>
    [Hle(Nid = 0x93D0A183, Library = "SceSysmemForKernel")]
    int NID_93D0A183() => 0;

    /// <summary>NID_F57A97A0(): 0.</summary>
    [Hle(Nid = 0xF57A97A0, Library = "SceSysmemForKernel")]
    int NID_F57A97A0() => 0;

    /// <summary>NID_8450BFF7(): 1 (the FW 3.74 entry does nothing else).</summary>
    [Hle(Nid = 0x8450BFF7, Library = "SceSysmemForKernel")]
    int NID_8450BFF7() => 1;

    // ------------------------------------------------------------------ GUID entries

    /// <summary>The object of a GUID (positive and odd; the firmware also wants bit 30 clear, which VitaSharp's single
    /// UID space does not keep apart), or null.</summary>
    KernelObject? GuidObject(int guid) => IsGuidForm(guid) ? K.Objects.Get<KernelObject>(guid) : null;

    /// <summary>NID_1DE4953F(index, SceClass **cls): the class with that index (+0x14) in the class list; NULL
    /// 0x80020005, none 0x8002450F.</summary>
    [Hle(Nid = 0x1DE4953F, Library = "SceSysmemForKernel")]
    int NID_1DE4953F(uint index, uint classOut)
    {
        if (classOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint root = RootClass();
        uint cls = root;
        do
        {
            if (GuestMemory.Read<byte>(cls + 0x14) == index)
            {
                GuestMemory.W32(classOut, cls);
                return 0;
            }

            cls = GuestMemory.R32(cls);
        }
        while (cls != 0 && cls != root);
        return ClassNotFound;
    }

    /// <summary>GUIDGetPID (0xBD42A0FC)(guid): the pid that owns the object (the kernel's 0x10005 for its own); none
    /// 0x80024501.</summary>
    [Hle(Nid = 0xBD42A0FC, Library = "SceSysmemForKernel")]
    int NID_BD42A0FC(int guid)
    {
        if (GuidObject(guid) is not { } kernelObject)
        {
            return InvalidPuid;
        }

        return kernelObject.OwnerPid == 0 ? KernelSpace.KernelPid : kernelObject.OwnerPid;
    }

    /// <summary>GUIDSetPID (0x46DF8C8A)(guid, pid): gives a kernel-owned object to <paramref name="pid"/> (an object a
    /// process already owns keeps its owner); none 0x80024501.</summary>
    [Hle(Nid = 0x46DF8C8A, Library = "SceSysmemForKernel")]
    int NID_46DF8C8A(int guid, int pid)
    {
        if (GuidObject(guid) is not { } kernelObject)
        {
            return InvalidPuid;
        }

        if (kernelObject.OwnerPid == 0 || kernelObject.OwnerPid == KernelSpace.KernelPid || kernelObject.OwnerPid == -1)
        {
            kernelObject.OwnerPid = pid;
        }

        return 0;
    }

    /// <summary>GUIDSetVisibilityLevel (0xE8CC2166)(guid, level): the level (0..7) other processes need to see the
    /// object; VitaSharp's single UID space shows every object, so only the object is checked; 0.</summary>
    [Hle(Nid = 0xE8CC2166, Library = "SceSysmemForKernel")]
    int NID_E8CC2166(int guid, uint level) => GuidObject(guid) is null ? InvalidPuid : 0;

    /// <summary>GUIDFindByObject (0xEC90AA11)(SceObjectBase *obj): the GUID whose body is <paramref name="body"/>; none
    /// 0x80024501.</summary>
    [Hle(Nid = 0xEC90AA11, Library = "SceSysmemForKernel")]
    int NID_EC90AA11(uint body)
    {
        lock (_objectBodies)
        {
            foreach (KeyValuePair<int, uint> entry in _objectBodies)
            {
                if (entry.Value == body && K.Objects.Get<KernelObject>(entry.Key) != null)
                {
                    return entry.Key;
                }
            }
        }

        return InvalidPuid;
    }

    /// <summary>GUIDFindByNameAll (0xF49C0BE8)(name): the GUID of the object with that name, without opening it; NULL
    /// 0x80020005, none the name lookup's error.</summary>
    [Hle(Nid = 0xF49C0BE8, Library = "SceSysmemForKernel")]
    int NID_F49C0BE8(uint name)
    {
        if (name == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        string wanted = GuestMemory.ReadCString(name, 32);
        return K.Objects.FindByName<KernelObject>(wanted)?.Uid ?? SceError.UID_CANNOT_FIND_BY_NAME;
    }

    /// <summary>PUIDGetAttr (0x96BD47D1)(pid, puid, uint *attr): the handle's attribute word (VitaSharp keeps none
    /// beyond the default 0x30000); the kernel's pid or NULL 0x80020005, not a UID 0x80024501 (*attr cleared).</summary>
    [Hle(Nid = 0x96BD47D1, Library = "SceSysmemForKernel")]
    int NID_96BD47D1(int pid, int puid, uint attrOut)
    {
        if (pid == KernelSpace.KernelPid || attrOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (!IsGuidForm(puid) || K.Objects.Get<KernelObject>(puid) is null)
        {
            GuestMemory.W32(attrOut, 0);
            return InvalidPuid;
        }

        GuestMemory.W32(attrOut, 0x30000);
        return 0;
    }

    /// <summary>ksceGUIDOpenByGUID (0x89D33DF0)(guid): one more open of the object; the GUID, or 0x80024501.</summary>
    [Hle(Nid = 0x89D33DF0, Library = "SceSysmemForKernel")]
    int ksceGUIDOpenByGUID(int guid)
    {
        if (GuidObject(guid) is not { } kernelObject)
        {
            return InvalidPuid;
        }

        Interlocked.Increment(ref kernelObject.OpenCount);
        return guid;
    }

    /// <summary>ksceGUIDKernelCreateWithAttr (0x6D26B066)(SceClass *cls, name, attr, SceObjectBase **obj): an object
    /// of the class (NULL name or output 0x80020005), as ksceGUIDKernelCreateWithOpt.</summary>
    [Hle(Nid = 0x6D26B066, Library = "SceSysmemForKernel")]
    int ksceGUIDKernelCreateWithAttr(uint cls, uint name, uint attr, uint objOut)
    {
        if (name == 0 || objOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ksceGUIDKernelCreateWithOpt(cls, name, 0, objOut);
    }

    /// <summary>NID_BA441FD0(uid, info): the memory block's basic information (0x30 bytes: +0 its size, else
    /// 0x80020009; NULL 0x80020005): the first words of SceKernelMemBlockInfoEx.</summary>
    [Hle(Nid = 0xBA441FD0, Library = "SceSysmemForKernel")]
    int NID_BA441FD0(int uid, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0x30)
        {
            return MmuBadVectorSize;
        }

        if (K.Objects.Get<MemBlock>(uid) is not { } block)
        {
            return SceError.INVALID_UID;
        }

        GuestMemory.Span(info + 4, 0x2C).Clear();
        GuestMemory.W32(info + 0x04, block.Type);
        GuestMemory.W32(info + 0x08, (uint)block.Uid);
        GuestMemory.W32(info + 0x0C, NamePointerOf(block));
        GuestMemory.W32(info + 0x10, block.Address);
        GuestMemory.W32(info + 0x14, block.Size);
        GuestMemory.W32(info + 0x18, block.Size);
        GuestMemory.W32(info + 0x28, 0xFFFFFFFF);
        return 0;
    }

    /// <summary>NID_C3F9607E(pid, addr, level, info): SceKernelMemBlockInfoEx (0xB8) of the block holding the address
    /// in process <paramref name="pid"/>; NULL 0x80020005, another size 0x80020009, none 0x80024A03.</summary>
    [Hle(Nid = 0xC3F9607E, Library = "SceSysmemForKernel")]
    int NID_C3F9607E(int pid, uint address, uint level, uint info)
    {
        if (info == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (GuestMemory.R32(info) != 0xB8)
        {
            return MmuBadVectorSize;
        }

        if (BlockHolding(pid == 0 ? ProcessScope.CurrentPid : pid, address, 1) is not { } block)
        {
            return NoPartitionForRange;
        }

        return MemBlockInfoInto(block.Uid, info);
    }

    /// <summary>NID_DBEACE2C(table, out): copies a range table (+8 its count, +0xC its 12-byte entries) into out (+0 its
    /// size, 200, else 0x80020005): +4 the count, then the first two words of each entry; 0.</summary>
    [Hle(Nid = 0xDBEACE2C, Library = "SceSysmemForKernel")]
    int NID_DBEACE2C(uint table, uint output)
    {
        if (GuestMemory.R32(output) != 200)
        {
            return SceError.INVALID_ARGUMENT;
        }

        uint count = GuestMemory.R32(table + 8);
        uint entries = GuestMemory.R32(table + 0xC);
        GuestMemory.W32(output + 4, count);
        for (uint entry = 0; entry < count; entry++)
        {
            GuestMemory.W32(output + 8 + entry * 0xC, GuestMemory.R32(entries + entry * 0xC));
            GuestMemory.W32(output + 0xC + entry * 0xC, GuestMemory.R32(entries + entry * 0xC + 4));
        }

        return 0;
    }

    /// <summary>ksceKernelAddressSpaceUnmap (0x73C38695)(space, ?, ?, va, size): unmaps a 1 MB-aligned range of the
    /// address space (another alignment 0x80020005). VitaSharp's address spaces keep their memory mapped; 0.</summary>
    [Hle(Nid = 0x73C38695, Library = "SceSysmemForKernel")]
    void ksceKernelAddressSpaceUnmap(HleContext context)
    {
        uint address = context.Gpr(3);
        uint size = context.Gpr(4);
        context.Return(((address | size) & 0xFFFFF) == 0 ? 0 : SceError.INVALID_ARGUMENT);
    }

    /// <summary>ksceKernelReallocHeapMemory (0x8EE8B917)(heap, ptr, size): a block of the new size with the old one's
    /// contents (the smaller size), the old block given back; NULL ptr allocates; a heap that does not exist or a
    /// pointer it did not hand out 0.</summary>
    [Hle(Nid = 0x8EE8B917, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint ksceKernelReallocHeapMemory(int heap, uint pointer, uint size)
    {
        if (HeapOf(heap) == null)
        {
            return 0;
        }

        if (pointer == 0)
        {
            return ksceKernelAllocHeapMemory(heap, size);
        }

        uint oldSize;
        lock (_heapAllocations)
        {
            if (!_heapAllocations.TryGetValue(pointer, out int owner) || owner != heap)
            {
                return 0;
            }

            oldSize = _heapAllocationSizes.GetValueOrDefault(pointer);
        }

        uint moved = ksceKernelAllocHeapMemory(heap, size);
        if (moved == 0)
        {
            return 0;
        }

        GuestMemory.Copy(moved, pointer, Math.Min(oldSize, size));
        ksceKernelFreeHeapMemory(heap, pointer);
        return moved;
    }

    // ------------------------------------------------------------------ PUID references and lookups

    /// <summary>The PUID reference of NID_188C8742 / NID_9C23DFF7 / NID_2C15615F: the kernel's pid 0x80020005, a handle
    /// that is not a UID 0x80024501 (*obj cleared), else ksceGUIDReferObjectWithClass on the object behind it (its
    /// body into *obj); the GUID.</summary>
    int PuidReferObject(int pid, int puid, uint cls, uint objOut)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (!IsGuidForm(puid))
        {
            if (objOut != 0)
            {
                GuestMemory.W32(objOut, 0);
            }

            return InvalidPuid;
        }

        int guid = kscePUIDtoGUID(pid, puid);
        if (guid < 0)
        {
            return guid;
        }

        int referred = cls != 0 ? ksceGUIDReferObjectWithClass(guid, cls, objOut) : ksceGUIDReferObject(guid, objOut);
        return referred < 0 ? referred : guid;
    }

    /// <summary>PUIDReferObject (0x188C8742)(pid, puid, SceObjectBase **obj): a reference to the object behind the
    /// process handle (released with ksceGUIDReleaseObject); the GUID.</summary>
    [Hle(Nid = 0x188C8742, Library = "SceSysmemForKernel")]
    int NID_188C8742(int pid, int puid, uint objOut) => PuidReferObject(pid, puid, 0, objOut);

    /// <summary>PUIDReferObjectWithClass (0x9C23DFF7)(pid, puid, SceClass *cls, SceObjectBase **obj): the same for an
    /// object of the class (NULL output 0x80020005).</summary>
    [Hle(Nid = 0x9C23DFF7, Library = "SceSysmemForKernel")]
    int NID_9C23DFF7(int pid, int puid, uint cls, uint objOut)
    {
        if (objOut == 0 && pid != KernelSpace.KernelPid && IsGuidForm(puid))
        {
            return SceError.INVALID_ARGUMENT;
        }

        return PuidReferObject(pid, puid, cls, objOut);
    }

    /// <summary>PUIDReferObject at level 7 (0x2C15615F)(pid, puid, SceObjectBase **obj).</summary>
    [Hle(Nid = 0x2C15615F, Library = "SceSysmemForKernel")]
    int NID_2C15615F(int pid, int puid, uint objOut) => PuidReferObject(pid, puid, 0, objOut);

    /// <summary>PUIDReleaseObject (0xCE561224)(pid, puid): drops a reference taken through the process handle; the
    /// kernel's pid 0x80020005, a handle that is not a UID 0x80024501.</summary>
    [Hle(Nid = 0xCE561224, Library = "SceSysmemForKernel")]
    int NID_CE561224(int pid, int puid)
    {
        if (pid == KernelSpace.KernelPid)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (!IsGuidForm(puid))
        {
            return InvalidPuid;
        }

        int guid = kscePUIDtoGUID(pid, puid);
        return guid < 0 ? guid : ksceGUIDReleaseObject(guid);
    }

    /// <summary>PUIDOpenByName (0x201E970B)(pid, name): kscePUIDOpenByName.</summary>
    [Hle(Nid = 0x201E970B, Library = "SceSysmemForKernel")]
    int NID_201E970B(int pid, uint name) => kscePUIDOpenByName(pid, name);

    /// <summary>GUIDFindByName (0xBDE00106)(name): the GUID of the object with that name (no reference kept); NULL
    /// 0x80020005.</summary>
    [Hle(Nid = 0xBDE00106, Library = "SceSysmemForKernel")]
    int NID_BDE00106(uint name) => NID_F49C0BE8(name);

    /// <summary>NID_4E6D8BC3(): 2 when the running thread's domain access word (its control block's MMU context
    /// +0x44, the DACR) has domain 2 in manager mode (0x30), what sceKernelOpenVMDomain sets and
    /// sceKernelCloseVMDomain takes back; 0 otherwise; no current thread (no control block) 0x80027101.</summary>
    [Hle(Nid = 0x4E6D8BC3, Library = "SceSysmemForKernel")]
    int NID_4E6D8BC3()
    {
        if (Threadmgr.CurrentControlBlock() == 0)
        {
            return unchecked((int)0x80027101);
        }

        return UserSysMem.VmDomainOpen ? 2 : 0;
    }

    /// <summary>ksceKernelAllocPartitionMemBlock (0x6DB46017)(partition, name, type, size, opt): a block of the
    /// partition (size page-aligned, else 0x80020005; an unknown type 0x80024B01). VitaSharp's partitions are the
    /// address spaces' memory, so the block comes from ksceKernelAllocMemBlock.</summary>
    [Hle(Nid = 0x6DB46017, Library = "SceSysmemForKernel")]
    void ksceKernelAllocPartitionMemBlock(HleContext context)
    {
        uint name = context.Gpr(1);
        uint type = context.Gpr(2);
        uint size = context.Gpr(3);
        uint option = context.Gpr(4);
        if ((size & 0xFFF) != 0)
        {
            context.Return(SceError.INVALID_ARGUMENT);
            return;
        }

        if (MemBlockTypeEntry(type) is null)
        {
            context.Return(InvalidMemBlockType);
            return;
        }

        context.Return(ksceKernelAllocMemBlock(name, type, size, option));
    }

    /// <summary>Sets (NID_0B36F7F9) or clears (NID_6E82D5A0) attribute bits of a heap: only 0x10 may be given
    /// (0x80020005 otherwise), a UID that is not a heap 0x80024901; 0.</summary>
    int ChangeHeapAttributes(int heap, uint bits, bool set)
    {
        if ((bits & ~0x10u) != 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (HeapOf(heap) is not { } data)
        {
            return InvalidHeap;
        }

        data.Aux = set ? data.Aux | bits : data.Aux & ~bits;
        return 0;
    }

    /// <summary>NID_0B36F7F9(heap, bits): sets the heap's attribute bits.</summary>
    [Hle(Nid = 0x0B36F7F9, Library = "SceSysmemForKernel")]
    int NID_0B36F7F9(int heap, uint bits) => ChangeHeapAttributes(heap, bits, set: true);

    /// <summary>NID_6E82D5A0(heap, bits): clears the heap's attribute bits.</summary>
    [Hle(Nid = 0x6E82D5A0, Library = "SceSysmemForKernel")]
    int NID_6E82D5A0(int heap, uint bits) => ChangeHeapAttributes(heap, bits, set: false);

    /// <summary>NID_CF3C3F7A(size, SceAllocOpt *opt): a block of one of sysmem's own heaps (the option NULL or 0x14
    /// bytes, else NULL): the kernel's general heap here, given back with ksceKernelFree.</summary>
    [Hle(Nid = 0xCF3C3F7A, Library = "SceSysmemForKernel", ReturnsValue = true)]
    uint NID_CF3C3F7A(uint size, uint option)
    {
        if (option != 0 && GuestMemory.R32(option) != HeapAllocOptSize)
        {
            return 0;
        }

        return ksceKernelAlloc(size);
    }
}
