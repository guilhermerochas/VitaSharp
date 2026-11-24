using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The rest of SceSysmemForDriver's object (GUID) calls, as the FW 3.74 SceSysmem has them: a GUID must be positive and
/// odd (SCE_KERNEL_ERROR_INVALID_UID otherwise; VitaSharp has one UID space for kernel and processes, so the bit the
/// firmware keeps for process-local IDs is not checked), a missing output pointer is SCE_KERNEL_ERROR_INVALID_ARGUMENT,
/// and the name of an object is a string in kernel memory the caller may read.
/// </summary>
public sealed partial class SysmemKernel
{
    /// <summary>Each object's name as a kernel string (made on first request): UID -> address.</summary>
    [SnapshotMissing] // states of builds before it: made again on first request
    readonly Dictionary<int, uint> _namePointers = new();

    /// <summary>The firmware's check of a GUID's form.</summary>
    static bool IsGuidForm(int guid) => guid > 0 && (guid & 1) != 0;

    uint NamePointerOf(KernelObject kernelObject)
    {
        lock (_namePointers)
        {
            if (_namePointers.TryGetValue(kernelObject.Uid, out uint name))
            {
                return name;
            }

            name = KernelAlloc(0x20, $"kobject-name:{kernelObject.Name}", 4);
            GuestMemory.Fill(name, 0, 0x20);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(kernelObject.Name);
            bytes.AsSpan(0, Math.Min(bytes.Length, 0x1F)).CopyTo(GuestMemory.Span(name, 0x1F));
            _namePointers[kernelObject.Uid] = name;
            return name;
        }
    }

    /// <summary>ksceGUIDKernelCreate(SceClass *cls, name, SceObjectBase **obj): ksceGUIDKernelCreateWithOpt with no
    /// option.</summary>
    [Hle(Nid = 0x56A13E90, Library = "SceSysmemForDriver")]
    int ksceGUIDKernelCreate(uint cls, uint name, uint objOut) => ksceGUIDKernelCreateWithOptThroughEntry(cls, name, 0, objOut);

    /// <summary>ksceGUIDGetClass(guid, SceClass **cls): the object's class (the root class for the HLE's own
    /// objects).</summary>
    [Hle(Nid = 0xC74B0152, Library = "SceSysmemForDriver")]
    int ksceGUIDGetClass(int guid, uint classOut)
    {
        if (!IsGuidForm(guid))
        {
            return SceError.INVALID_UID;
        }

        if (K.Objects.Get<KernelObject>(guid) is not { } kernelObject)
        {
            return SceError.INVALID_UID;
        }

        uint cls = ClassOf(kernelObject);
        if (classOut != 0)
        {
            GuestMemory.W32(classOut, cls);
        }

        return 0;
    }

    /// <summary>ksceGUIDGetObject(guid, SceObjectBase **obj): the object's body, with no reference taken.</summary>
    [Hle(Nid = 0x0FC24464, Library = "SceSysmemForDriver")]
    int ksceGUIDGetObject(int guid, uint objOut)
    {
        if (!IsGuidForm(guid))
        {
            return SceError.INVALID_UID;
        }

        if (objOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<KernelObject>(guid) is not { } kernelObject)
        {
            return SceError.INVALID_UID;
        }

        GuestMemory.W32(objOut, BodyOf(kernelObject));
        return 0;
    }

    /// <summary>ksceGUIDGetName(guid, char **name): a pointer to the object's name.</summary>
    [Hle(Nid = 0xA78755EB, Library = "SceSysmemForDriver")]
    int ksceGUIDGetName(int guid, uint nameOut)
    {
        if (!IsGuidForm(guid))
        {
            return SceError.INVALID_UID;
        }

        if (nameOut == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<KernelObject>(guid) is not { } kernelObject)
        {
            return SceError.INVALID_UID;
        }

        GuestMemory.W32(nameOut, NamePointerOf(kernelObject));
        return 0;
    }

    /// <summary>ksceGUIDName(guid): the object's name, or 0 when there is no such object.</summary>
    [Hle(Nid = 0xE655852F, Library = "SceSysmemForDriver", ReturnsValue = true)]
    uint ksceGUIDName(int guid) => K.Objects.Get<KernelObject>(guid) is { } kernelObject ? NamePointerOf(kernelObject) : 0;

    /// <summary>ksceGUIDSetName(guid, name): renames the object (up to 31 characters).</summary>
    [Hle(Nid = 0x4CFA4100, Library = "SceSysmemForDriver")]
    int ksceGUIDSetName(int guid, uint name)
    {
        if (!IsGuidForm(guid))
        {
            return SceError.INVALID_UID;
        }

        if (name == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        if (K.Objects.Get<KernelObject>(guid) is not { } kernelObject)
        {
            return SceError.INVALID_UID;
        }

        kernelObject.Name = GuestMemory.ReadCString(name, 31);
        lock (_namePointers)
        {
            if (_namePointers.Remove(guid, out uint old))
            {
                Mem.Free(old);
            }
        }

        return 0;
    }

    /// <summary>ksceGUIDOpenByName(name): the object with that name (SCE_KERNEL_ERROR_UID_CANNOT_FIND_BY_NAME when
    /// none); VitaSharp's process IDs are the object's UID.</summary>
    [Hle(Nid = 0xD76E7452, Library = "SceSysmemForDriver")]
    int ksceGUIDOpenByName(uint name)
    {
        if (name == 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        string wanted = GuestMemory.ReadCString(name, 32);
        if (K.Objects.FindByName<KernelObject>(wanted) is not { } kernelObject)
        {
            return SceError.UID_CANNOT_FIND_BY_NAME;
        }

        Interlocked.Increment(ref kernelObject.OpenCount);
        return kernelObject.Uid;
    }

    /// <summary>ksceGUIDReferObjectWithClassLevel(guid, cls, level, SceObjectBase **obj): ksceGUIDReferObjectWithClass
    /// for a level below 8 (SCE_KERNEL_ERROR_INVALID_ARGUMENT otherwise).</summary>
    [Hle(Nid = 0x77066FD1, Library = "SceSysmemForDriver")]
    int ksceGUIDReferObjectWithClassLevel(int guid, uint cls, uint level, uint objOut)
    {
        if (!IsGuidForm(guid))
        {
            return SceError.INVALID_UID;
        }

        if (level >= 8)
        {
            return SceError.INVALID_ARGUMENT;
        }

        return ksceGUIDReferObjectWithClassThroughEntry(guid, cls, objOut);
    }

    /// <summary>ksceGUIDReferObjectWithSubclass(guid, cls, SceObjectBase **obj): a reference when the object's class is
    /// <paramref name="cls"/> or has it on its parent chain (0x80024509 otherwise, *obj cleared).</summary>
    [Hle(Nid = 0x72A98D17, Library = "SceSysmemForDriver")]
    int ksceGUIDReferObjectWithSubclass(int guid, uint cls, uint objOut) => ReferObjectOfClass(guid, cls, objOut, true);

    /// <summary>kscePUIDGetClass(pid, puid, SceClass **cls).</summary>
    [Hle(Nid = 0xE9728A12, Library = "SceSysmemForDriver")]
    int kscePUIDGetClass(int pid, int puid, uint classOut) => ksceGUIDGetClassThroughEntry(puid, classOut);

    /// <summary>kscePUIDGetObject(pid, puid, SceObjectBase **obj).</summary>
    [Hle(Nid = 0xFE6D7FAE, Library = "SceSysmemForDriver")]
    int kscePUIDGetObject(int pid, int puid, uint objOut) => ksceGUIDGetObjectThroughEntry(puid, objOut);

    /// <summary>kscePUIDOpenByGUIDWithFlags(pid, guid, flags): kscePUIDOpenByGUID (the flags choose the access the
    /// handle gets, which VitaSharp's single UID space does not separate).</summary>
    [Hle(Nid = 0xCED1547B, Library = "SceSysmemForDriver")]
    int kscePUIDOpenByGUIDWithFlags(int pid, int guid, uint flags) => kscePUIDOpenByGUIDThroughEntry(pid, guid);
}
