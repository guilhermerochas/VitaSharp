using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The kernel libraries of the firmware's SceSysmem module (SceSysmemForDriver, SceSysmemForKernel, SceCpuForDriver,
/// SceCpuForKernel, SceSysrootForKernel, SceDebugForDriver) for kernel modules the user installs (plugins): memory
/// blocks and heaps of the kernel, the UID classes and objects kernel code defines, user/kernel copies with the
/// firmware's permission rule, caches and interrupts. Behaviour follows the firmware (FW 3.74 SceSysmem, studied in
/// the decompiled module; the SDK headers for the prototypes); where the emulator has no hardware behind a call
/// (interrupt masking, the data cache) the call keeps its state and returns what the firmware returns.
/// </summary>
public sealed partial class SysmemKernel : HleModule
{
    /// <summary>SceKernelAllocMemBlockKernelOpt attr bits (psp2kern/kernel/sysmem.h).</summary>
    const uint AttrHasVbase = 0x1, AttrHasAlignment = 0x4, AttrHasPid = 0x80;

    /// <summary>The SceClass magic word (ksceUIDClassInitClass writes it; ksceGUIDKernelCreateWithOpt checks it).</summary>
    const uint ClassMagic = 0xABCE9DA5;

    /// <summary>The CPSR I bit: what ksceKernelCpuSuspendIntr / ksceKernelCpuDisableInterrupts return when interrupts
    /// were already masked.</summary>
    const int InterruptsMasked = 0x80;

    // kernel heaps: allocation address -> heap UID and size (every allocation is its own kernel block)
    readonly Dictionary<uint, int> _heapAllocations = new();
    readonly Dictionary<uint, uint> _heapAllocationSizes = new();

    // kernel blocks ksceKernelMapBlockUserVisible made reachable from user code: their addresses
    readonly HashSet<uint> _userVisibleBlocks = new();

    // the guest-visible body of an HLE kernel object handed to ksceGUIDReferObject: UID -> address
    readonly Dictionary<int, uint> _objectBodies = new();

    // the interrupt mask of each thread (CPSR I bit) and the per-process context / address space records
    readonly Dictionary<int, int> _interruptMasks = new();
    readonly Dictionary<int, uint> _pidContexts = new();
    readonly Dictionary<int, uint> _addressSpaces = new();

    // the root UID class ksceKernelGetUidClass returns, in kernel memory (0 until first asked)
    uint _rootClass;

    SysMem UserSysMem => Emu.Hle.Modules.OfType<SysMem>().First();

    /// <summary>Allocates kernel memory owned by the kernel process.</summary>
    uint KernelAlloc(uint size, string name, uint align = GuestMemory.PageSize)
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return Mem.Alloc(size, name, MemoryRegion.Kernel, align);
        }
    }

    /// <summary>User code may access the range: user memory of the caller, or a kernel block mapped user-visible.</summary>
    bool IsUserAccessible(uint addr, uint length)
    {
        if (KernelSpace.IsUserRange(addr, length))
        {
            return true;
        }

        lock (_userVisibleBlocks)
        {
            foreach (uint start in _userVisibleBlocks)
            {
                if (K.Objects.All<MemBlock>().FirstOrDefault(b => b.Address == start) is { } b
                    && addr >= b.Address && (ulong)addr + length <= (ulong)b.Address + b.Size)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The process with id <paramref name="pid"/> (0 = the caller's), or null.</summary>
    Emulator? ProcessOf(int pid) => KernelSpace.ProcessOf(Emu, pid);


    // ------------------------------------------------------------------ memory blocks

    /// <summary>
    /// ksceKernelAllocMemBlock(name, type, size, SceKernelAllocMemBlockKernelOpt *opt): a memory block of any type.
    /// Kernel types (only kernel permission bits in the low byte) are kernel memory, in the kernel region every
    /// address space maps; user types are memory of the calling process, or of opt->pid with HAS_PID. opt (0x58 bytes on
    /// FW 3.60): +8 attr, +0xC the virtual base with HAS_VBASE, +0x14 alignment with HAS_ALIGNMENT, +0x24 pid with
    /// HAS_PID. The block belongs to the kernel until a process opens it with kscePUIDOpenByGUID.
    /// </summary>
    [Hle]
    int ksceKernelAllocMemBlock(uint namePtr, uint type, uint size, uint opt)
    {
        if (namePtr == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (size == 0)
        {
            return SceError.ILLEGAL_SIZE;
        }

        string name = GuestMemory.ReadCString(namePtr, 32);
        uint attr = 0, vbase = 0, align = GuestMemory.PageSize;
        int pid = 0;
        if (opt != 0)
        {
            uint optSize = GuestMemory.R32(opt);
            attr = optSize >= 0xC ? GuestMemory.R32(opt + 8) : 0;
            if ((attr & AttrHasVbase) != 0 && optSize >= 0x10)
            {
                vbase = GuestMemory.R32(opt + 0xC);
            }

            if ((attr & AttrHasAlignment) != 0 && optSize >= 0x18)
            {
                align = Math.Max(align, GuestMemory.R32(opt + 0x14));
            }

            if ((attr & AttrHasPid) != 0 && optSize >= 0x28)
            {
                pid = (int)GuestMemory.R32(opt + 0x24);
            }
        }

        if ((vbase & 0xFFF) != 0 || (align & (align - 1)) != 0)
        {
            return SceError.INVALID_ARGUMENT;
        }

        bool kernelMemory = (type & 0xF0) == 0;
        bool cdram = ((type >> 20) & 0xF) == 4;
        uint pages = GuestMemory.AlignUp(size, GuestMemory.PageSize);
        var target = ProcessOf(pid);
        if (target == null)
        {
            return SceError.INVALID_PID;
        }

        uint addr;
        try
        {
            if (kernelMemory)
            {
                using (ProcessScope.Enter(KernelSpace.KernelPid))
                {
                    addr = vbase != 0 ? Mem.TryAllocAt(vbase, pages, $"kmemblock:{name}")
                        : Mem.Alloc(pages, $"kmemblock:{name}", cdram ? MemoryRegion.Cdram : MemoryRegion.Kernel, align);
                }
            }
            else
            {
                using (ProcessScope.Enter(target))
                {
                    addr = vbase != 0 ? Mem.TryAllocAt(vbase, pages, $"memblock:{name}")
                        : Mem.Alloc(pages, $"memblock:{name}", cdram ? MemoryRegion.Cdram : MemoryRegion.User, align);
                }
            }
        }
        catch (OutOfMemoryException)
        {
            return SceError.NO_MEMORY;
        }

        if (addr == 0)
        {
            Log.Warn($"ksceKernelAllocMemBlock({name}, 0x{type:X}, 0x{size:X}): 0x{vbase:X8} is not free");
            return SceError.NO_MEMORY;
        }

        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return K.Objects.Register(new MemBlock { Name = name, Address = addr, Size = pages, Type = type });
        }
    }

    /// <summary>ksceKernelFreeMemBlock(uid): the block's memory goes back (in its owner's address space).</summary>
    [Hle]
    int ksceKernelFreeMemBlock(int uid)
    {
        if (K.Objects.Get<MemBlock>(uid) is not { } b)
        {
            return SceError.INVALID_UID;
        }

        Mem.Free(b.Address);
        lock (_userVisibleBlocks)
        {
            _userVisibleBlocks.Remove(b.Address);
        }

        K.Objects.Remove(uid);
        return 0;
    }

    /// <summary>ksceKernelGetMemBlockBase(uid, void **base).</summary>
    [Hle]
    int ksceKernelGetMemBlockBase(int uid, uint basePtr)
    {
        if (basePtr == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        return UserSysMem.sceKernelGetMemBlockBase(uid, new Ptr<uint>(basePtr));
    }

    /// <summary>ksceKernelMapBlockUserVisible(uid): a kernel block becomes reachable from user code (and from the user
    /// side of the copies). A user block already is.</summary>
    [Hle]
    int ksceKernelMapBlockUserVisible(int uid)
    {
        if (K.Objects.Get<MemBlock>(uid) is not { } b)
        {
            return SceError.INVALID_UID;
        }

        lock (_userVisibleBlocks)
        {
            _userVisibleBlocks.Add(b.Address);
        }

        return 0;
    }

    // ------------------------------------------------------------------ heaps

    /// <summary>ksceKernelCreateHeap(name, size, SceKernelHeapCreateOpt *opt): a kernel heap. It grows as needed (the
    /// firmware's heaps take more pages from their partition), so size is only recorded.</summary>
    [Hle]
    int ksceKernelCreateHeap(uint namePtr, uint size, uint opt)
    {
        if (namePtr == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        if (size == 0)
        {
            return SceError.ILLEGAL_SIZE;
        }

        uint attr = opt != 0 && GuestMemory.R32(opt) >= 8 ? GuestMemory.R32(opt + 4) : 0;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return K.Objects.Register(new KernelData { Name = GuestMemory.ReadCString(namePtr, 32), Kind = KernelData.Heap, Size = size, Aux = attr });
        }
    }

    KernelData? HeapOf(int uid)
    {
        return K.Objects.Get<KernelData>(uid) is { Kind: KernelData.Heap } h ? h : null;
    }

    /// <summary>ksceKernelAllocHeapMemory(uid, size): zeroed kernel memory, 8-byte aligned; NULL when the heap does not
    /// exist or memory is exhausted. Each allocation is its own kernel block (whole pages).</summary>
    [Hle]
    uint ksceKernelAllocHeapMemory(int uid, uint size)
    {
        if (HeapOf(uid) is not { } heap || size == 0)
        {
            return 0;
        }

        uint addr;
        try
        {
            addr = KernelAlloc(size, $"kheap:{heap.Name}");
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }

        lock (_heapAllocations)
        {
            _heapAllocations[addr] = uid;
            _heapAllocationSizes[addr] = size;
        }

        return addr;
    }

    /// <summary>ksceKernelFreeHeapMemory(uid, ptr).</summary>
    [Hle]
    int ksceKernelFreeHeapMemory(int uid, uint ptr)
    {
        if (HeapOf(uid) == null)
        {
            return SceError.ILLEGAL_HEAP_ID;
        }

        lock (_heapAllocations)
        {
            if (!_heapAllocations.TryGetValue(ptr, out int owner) || owner != uid)
            {
                return SceError.INVALID_ARGUMENT;
            }

            _heapAllocations.Remove(ptr);
            _heapAllocationSizes.Remove(ptr);
        }

        Mem.Free(ptr);
        return 0;
    }

    /// <summary>ksceKernelDeleteHeap(uid): the heap and everything still allocated from it go.</summary>
    [Hle]
    int ksceKernelDeleteHeap(int uid)
    {
        if (HeapOf(uid) == null)
        {
            return SceError.ILLEGAL_HEAP_ID;
        }

        List<uint> left;
        lock (_heapAllocations)
        {
            left = _heapAllocations.Where(kv => kv.Value == uid).Select(kv => kv.Key).ToList();
            foreach (uint a in left)
            {
                _heapAllocations.Remove(a);
                _heapAllocationSizes.Remove(a);
            }
        }

        foreach (uint a in left)
        {
            Mem.Free(a);
        }

        K.Objects.Remove(uid);
        return 0;
    }

    // ------------------------------------------------------------------ UID classes and objects

    /// <summary>The root UID meta-class (SceUIDClass), created in kernel memory on first use.</summary>
    uint RootClass()
    {
        lock (_heapAllocations)
        {
            if (_rootClass != 0)
            {
                return _rootClass;
            }

            uint cls = KernelAlloc(0x40, "kernel:SceUIDClass");
            uint name = cls + 0x2C;
            GuestMemory.WriteCString(name, "SceUIDClass", 0x14);
            WriteClass(cls, name, 0, 8, 0, 0, cls);
            // the root class heads sysmem's circular list of classes (next/prev point to itself), with index 1
            GuestMemory.Write(cls + 0x14, (byte)1);
            _rootClass = cls;
            return cls;
        }
    }

    /// <summary>The SceClass fields (FW 3.60, 0x2C bytes): next, root, prev, name, parent class, attributes,
    /// itemsize, itemsize aligned to 8, create/destroy callbacks, magic.</summary>
    static void WriteClass(uint cls, uint name, uint parent, uint itemSize, uint create, uint destroy, uint root)
    {
        GuestMemory.Span(cls, 0x2C).Clear();
        GuestMemory.W32(cls + 0x00, cls);
        GuestMemory.W32(cls + 0x04, root);
        GuestMemory.W32(cls + 0x08, cls);
        GuestMemory.W32(cls + 0x0C, name);
        GuestMemory.W32(cls + 0x10, parent);
        GuestMemory.Write(cls + 0x18, (ushort)itemSize);
        GuestMemory.Write(cls + 0x1A, (ushort)((itemSize + 7) & ~7u));
        GuestMemory.W32(cls + 0x20, create);
        GuestMemory.W32(cls + 0x24, destroy);
        GuestMemory.W32(cls + 0x28, ClassMagic);
    }

    /// <summary>ksceKernelGetUidClass(): the root UID class every class derives from.</summary>
    [Hle(ReturnsValue = true)]
    uint ksceKernelGetUidClass() => RootClass();

    /// <summary>
    /// ksceKernelCreateClass (ksceUIDClassInitClass)(SceClass *cls, name, parent, itemsize, create, destroy): fills the
    /// caller's class record as the FW 3.74 sysmem does (decompiled, study only): no argument checks, the parent kept
    /// as given (0 = none), itemsize stored as is and its allocation size rounded up to 4, 8, 16 or a multiple of 32,
    /// then the class is linked at the tail of the circular class list that starts at the root class, with the index
    /// one above the highest in the list (at most 0xFF). A class whose name (first 31 characters) is already in the
    /// list, or that is already linked, is not linked again. Always 0.
    /// </summary>
    [Hle]
    int ksceKernelCreateClass(uint cls, uint name, uint parent, uint itemSize, uint create, uint destroy)
    {
        uint root = RootClass();
        GuestMemory.Write(cls + 0x18, (ushort)itemSize);
        GuestMemory.Write(cls + 0x1A, ClassAllocationSize(itemSize));
        GuestMemory.W32(cls + 0x04, root);
        GuestMemory.W32(cls + 0x0C, name);
        GuestMemory.W32(cls + 0x10, parent);
        GuestMemory.W32(cls + 0x14, 0);
        GuestMemory.W32(cls + 0x1C, 0);
        GuestMemory.W32(cls + 0x20, create);
        GuestMemory.W32(cls + 0x24, destroy);
        GuestMemory.W32(cls + 0x28, ClassMagic);
        LinkClass(root, cls);
        return 0;
    }

    /// <summary>The bytes an object of a class takes (SceClass +0x1A): itemsize rounded up to 4, 8, 16 or a multiple of
    /// 32.</summary>
    static ushort ClassAllocationSize(uint itemSize)
    {
        if (itemSize < 5)
        {
            return 4;
        }

        if (itemSize < 9)
        {
            return 8;
        }

        if (itemSize < 0x11)
        {
            return 0x10;
        }

        return (ushort)((itemSize + 0x1F) & 0xFFE0);
    }

    /// <summary>Links <paramref name="cls"/> before <paramref name="head"/> (at the tail) of the circular class list
    /// (next +0x00, prev +0x08, index byte +0x14), unless a class of the same name is in it or it is already there.</summary>
    static void LinkClass(uint head, uint cls)
    {
        if (cls == head)
        {
            return;
        }

        string name = ClassName(cls);
        uint highestIndex = 0;
        uint current = head;
        do
        {
            if (ClassName(current) == name)
            {
                return;
            }

            highestIndex = Math.Max(highestIndex, GuestMemory.Read<byte>(current + 0x14));
            current = GuestMemory.R32(current);
            if (current == 0)
            {
                return;
            }

            if (current == head)
            {
                uint tail = GuestMemory.R32(head + 0x08);
                GuestMemory.W32(cls + 0x00, head);
                GuestMemory.W32(cls + 0x08, tail);
                if (tail != 0)
                {
                    GuestMemory.W32(tail, cls);
                }

                GuestMemory.W32(head + 0x08, cls);
                GuestMemory.Write(cls + 0x14, (byte)(highestIndex == 0xFF ? 0xFF : highestIndex + 1));
                return;
            }
        }
        while (current != cls);
    }

    /// <summary>The first 31 characters of a class's name (the length the firmware compares), "" for none.</summary>
    static string ClassName(uint cls)
    {
        uint name = GuestMemory.R32(cls + 0x0C);
        return name == 0 ? "" : GuestMemory.ReadCString(name, 0x1F);
    }

    /// <summary>True when <paramref name="cls"/> is <paramref name="wanted"/> or derives from it.</summary>
    static bool DerivesFrom(uint cls, uint wanted)
    {
        for (int depth = 0; cls != 0 && depth < 32; depth++)
        {
            if (cls == wanted)
            {
                return true;
            }

            uint parent = GuestMemory.R32(cls + 0x10);
            if (parent == cls)
            {
                break;
            }

            cls = parent;
        }

        return false;
    }

    /// <summary>
    /// ksceGUIDKernelCreateWithOpt(SceClass *cls, name, opt, SceObjectBase **obj): a new object of a kernel-defined
    /// class: itemsize zeroed bytes of kernel memory starting with the SceObjectBase header { object, class }, the
    /// class's create callback run on it. Returns its GUID.
    /// </summary>
    [Hle]
    int ksceGUIDKernelCreateWithOpt(uint cls, uint namePtr, uint opt, uint objOut)
    {
        if (cls == 0 || GuestMemory.R32(cls + 0x28) != ClassMagic)
        {
            return SceError.ILLEGAL_TYPE;
        }

        uint size = Math.Max(8u, GuestMemory.Read<ushort>(cls + 0x1A));
        string name = namePtr != 0 ? GuestMemory.ReadCString(namePtr, 32) : "";
        uint obj;
        try
        {
            obj = KernelAlloc(size, $"kobject:{name}");
        }
        catch (OutOfMemoryException)
        {
            return SceError.NO_MEMORY;
        }

        GuestMemory.W32(obj, obj);
        GuestMemory.W32(obj + 4, cls);
        int uid;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uid = K.Objects.Register(new KernelData { Name = name, Kind = KernelData.ClassObject, Address = obj, Size = size, Aux = cls });
        }

        uint create = GuestMemory.R32(cls + 0x20);
        if (create != 0 && GuestThread.Current is { } t)
        {
            t.CallGuest(create, obj);
        }

        if (objOut != 0)
        {
            GuestMemory.W32(objOut, obj);
        }

        return uid;
    }

    /// <summary>The guest body of object <paramref name="uid"/>: a kernel-defined object's own memory, or (for the
    /// HLE's objects: memory blocks, threads...) a SceObjectBase-headed record made for kernel code on first use.</summary>
    uint BodyOf(KernelObject o)
    {
        if (o is KernelData { Kind: KernelData.ClassObject } d)
        {
            return d.Address;
        }

        lock (_objectBodies)
        {
            if (_objectBodies.TryGetValue(o.Uid, out uint body))
            {
                return body;
            }

            body = KernelAlloc(0x40, $"kobject:{o.Name}");
            GuestMemory.W32(body, body);
            GuestMemory.W32(body + 4, RootClass());
            if (o is MemBlock block)
            {
                FillMemBlockObject(body, block, block.OwnerPid);
            }

            _objectBodies[o.Uid] = body;
            return body;
        }
    }

    /// <summary>ksceGUIDReferObject(guid, SceObjectBase **obj): a reference to the object; released with
    /// ksceGUIDReleaseObject.</summary>
    [Hle]
    int ksceGUIDReferObject(int guid, uint objOut)
    {
        if (K.Objects.Get<KernelObject>(guid) is not { } o)
        {
            return SceError.INVALID_UID;
        }

        if (o is KernelData d)
        {
            Interlocked.Increment(ref d.References);
        }

        if (objOut != 0)
        {
            GuestMemory.W32(objOut, BodyOf(o));
        }

        return 0;
    }

    /// <summary>ksceGUIDReferObjectWithClass(guid, SceClass *cls, SceObjectBase **obj): the same, only for an object
    /// whose class is exactly <paramref name="cls"/> (FW 3.74 sysmem; a mismatch clears *obj).</summary>
    [Hle]
    int ksceGUIDReferObjectWithClass(int guid, uint cls, uint objOut) => ReferObjectOfClass(guid, cls, objOut, false);

    /// <summary>The class check of the GUID reference calls (sysmem's GUIDReferObjectClassCheckCommon): the object's
    /// class must be <paramref name="cls"/> (0x80024508), or with <paramref name="subclass"/> have it on its parent
    /// chain (0x80024509); on a failure *obj is cleared.</summary>
    int ReferObjectOfClass(int guid, uint cls, uint objOut, bool subclass)
    {
        if (!IsGuidForm(guid))
        {
            return SceError.INVALID_UID;
        }

        if (K.Objects.Get<KernelObject>(guid) is not { } o)
        {
            ClearObjectOut(objOut);
            return SceError.INVALID_UID;
        }

        uint own = ClassOf(o);
        if (!subclass && own != cls)
        {
            ClearObjectOut(objOut);
            return SceError.INVALID_UID_CLASS;
        }

        if (subclass && !DerivesFrom(own, cls))
        {
            ClearObjectOut(objOut);
            return SceError.UID_NOT_OF_SUBCLASS;
        }

        return ksceGUIDReferObject(guid, objOut);
    }

    static void ClearObjectOut(uint objOut)
    {
        if (objOut != 0)
        {
            GuestMemory.W32(objOut, 0);
        }
    }

    /// <summary>ksceGUIDReleaseObject(guid): drops a reference taken by ksceGUIDReferObject.</summary>
    [Hle]
    int ksceGUIDReleaseObject(int guid)
    {
        if (K.Objects.Get<KernelObject>(guid) is not { } o)
        {
            return SceError.INVALID_UID;
        }

        if (o is KernelData d && Interlocked.Decrement(ref d.References) < 0)
        {
            Interlocked.Exchange(ref d.References, 0);
        }

        return 0;
    }

    /// <summary>ksceGUIDClose(guid): deletes a kernel-defined object (its class's destroy callback runs, its memory is
    /// freed); an HLE object is deleted the way its own delete call does (a memory block is freed).</summary>
    [Hle]
    int ksceGUIDClose(int guid)
    {
        switch (K.Objects.Get<KernelObject>(guid))
        {
            case null:
                return SceError.INVALID_UID;
            case KernelData { Kind: KernelData.ClassObject } d:
            {
                uint destroy = GuestMemory.R32(d.Aux + 0x24);
                if (destroy != 0 && GuestThread.Current is { } t)
                {
                    t.CallGuest(destroy, d.Address);
                }

                K.Objects.Remove(guid);
                Mem.Free(d.Address);
                return 0;
            }
            case MemBlock:
                return ksceKernelFreeMemBlock(guid);
            case KernelData { Kind: KernelData.Heap }:
                return ksceKernelDeleteHeap(guid);
            default:
                K.Objects.Remove(guid);
                return 0;
        }
    }

    /// <summary>
    /// kscePUIDOpenByGUID (ksceKernelCreateUserUid)(pid, guid): a handle of process <paramref name="pid"/> for the
    /// object. VitaSharp has one UID space for kernel and processes, so the handle is the object's own UID; the object
    /// now belongs to the process (its user calls find it, and it goes with the process).
    /// </summary>
    [Hle]
    int kscePUIDOpenByGUID(int pid, int guid)
    {
        if (K.Objects.Get<KernelObject>(guid) is not { } o)
        {
            return SceError.INVALID_UID;
        }

        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        o.OwnerPid = process.Pid;
        Interlocked.Increment(ref o.OpenCount);
        return guid;
    }

    /// <summary>kscePUIDtoGUID (ksceKernelKernelUidForUserUid)(pid, puid): the object behind a process handle (the same
    /// UID here).</summary>
    [Hle]
    internal int kscePUIDtoGUID(int pid, int puid)
    {
        if (ProcessOf(pid) == null)
        {
            return SceError.INVALID_PID;
        }

        return K.Objects.Get<KernelObject>(puid) != null ? puid : SceError.INVALID_UID;
    }

    /// <summary>kscePUIDClose (ksceKernelDeleteUserUid)(pid, puid): closes a process handle; the object stays.</summary>
    [Hle]
    int kscePUIDClose(int pid, int puid)
    {
        if (ProcessOf(pid) == null)
        {
            return SceError.INVALID_PID;
        }

        if (K.Objects.Get<KernelObject>(puid) is not { } o)
        {
            return SceError.INVALID_UID;
        }

        if (o.OpenCount > 0)
        {
            Interlocked.Decrement(ref o.OpenCount);
        }

        return 0;
    }

    /// <summary>ksceKernelGetPidContext(pid, SceKernelProcessContext **ctx): the record of a process's MMU context
    /// (TTBR0/TTBR1/CONTEXTIDR on hardware). VitaSharp's address spaces are host mappings: the record is a zeroed
    /// 12-byte block per process, enough for kernel code that saves and restores it around another process's
    /// memory access.</summary>
    [Hle]
    int ksceKernelGetPidContext(int pid, uint ctxOut)
    {
        if (ctxOut == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        var process = pid == KernelSpace.KernelPid ? Emu : ProcessOf(pid);
        if (process == null)
        {
            return SceError.INVALID_PID;
        }

        int key = pid == KernelSpace.KernelPid ? KernelSpace.KernelPid : process.Pid;
        uint ctx;
        lock (_pidContexts)
        {
            if (!_pidContexts.TryGetValue(key, out ctx))
            {
                ctx = KernelAlloc(12, $"kernel:context:{key:X}");
                _pidContexts[key] = ctx;
            }
        }

        GuestMemory.W32(ctxOut, ctx);
        return 0;
    }

    // ------------------------------------------------------------------ user/kernel copies

    /// <summary>ksceKernelMemcpyUserToKernel (ksceKernelCopyFromUser)(dst, src, len): src must be user memory of the
    /// caller.</summary>
    [Hle]
    internal int ksceKernelMemcpyUserToKernel(uint dst, uint src, uint len)
    {
        if (!IsUserAccessible(src, len) || !KernelSpace.IsMappedRange(dst, len))
        {
            return KernelSpace.UserCopyFault;
        }

        GuestMemory.Copy(dst, src, len);
        return 0;
    }

    /// <summary>ksceKernelMemcpyKernelToUser (ksceKernelCopyToUser)(dst, src, len): dst must be user memory of the
    /// caller.</summary>
    [Hle]
    internal int ksceKernelMemcpyKernelToUser(uint dst, uint src, uint len)
    {
        if (!IsUserAccessible(dst, len) || !KernelSpace.IsMappedRange(src, len))
        {
            return KernelSpace.UserCopyFault;
        }

        GuestMemory.Copy(dst, src, len);
        return 0;
    }

    /// <summary>
    /// ksceKernelStrncpyUserToKernel (ksceKernelStrncpyFromUser)(dst, src, len): copies the user string up to its NUL or
    /// len bytes and fills the rest of dst with zeros (the firmware's strncpy); returns the length copied without the
    /// NUL (len when the string does not end within len).
    /// </summary>
    [Hle]
    internal int ksceKernelStrncpyUserToKernel(uint dst, uint src, uint len)
    {
        if (!KernelSpace.IsMappedRange(dst, len))
        {
            return KernelSpace.UserCopyFault;
        }

        uint n = 0;
        while (n < len)
        {
            if (!IsUserAccessible(src + n, 1))
            {
                return KernelSpace.UserCopyFault;
            }

            byte c = GuestMemory.Read<byte>(src + n);
            GuestMemory.Write(dst + n, c);
            if (c == 0)
            {
                GuestMemory.Fill(dst + n, 0, len - n);
                return (int)n;
            }

            n++;
        }

        return (int)len;
    }

    /// <summary>Runs <paramref name="copy"/> with guest memory in process <paramref name="pid"/>'s address space.</summary>
    T InProcess<T>(Emulator process, Func<T> copy)
    {
        using (AddressSpace.Enter(process.Space))
        {
            return copy();
        }
    }

    /// <summary>ksceKernelMemcpyUserToKernelForPid (ksceKernelCopyFromUserProc)(pid, dst, src, len): src is user memory
    /// of process <paramref name="pid"/>.</summary>
    [Hle]
    int ksceKernelMemcpyUserToKernelForPid(int pid, uint dst, uint src, uint len)
    {
        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        if (!KernelSpace.IsMappedRange(dst, len))
        {
            return KernelSpace.UserCopyFault;
        }

        byte[]? data = InProcess(process, () => IsUserAccessible(src, len) ? GuestMemory.ReadSpan(src, (int)len).ToArray() : null);
        if (data == null)
        {
            return KernelSpace.UserCopyFault;
        }

        data.CopyTo(GuestMemory.Span(dst, (int)len));
        return 0;
    }

    /// <summary>
    /// ksceKernelProcCopyToUserRx (3.60 ksceKernelCopyToUserProcTextDomain)(pid, dst, src, len): writes kernel data into
    /// the executable user memory of process <paramref name="pid"/> (code the kernel installs there) and drops the
    /// translated code of the range, as the firmware cleans the caches after the copy.
    /// </summary>
    [Hle]
    int ksceKernelProcCopyToUserRx(int pid, uint dst, uint src, uint len)
    {
        if (ProcessOf(pid) is not { } process)
        {
            return SceError.INVALID_PID;
        }

        if (!KernelSpace.IsMappedRange(src, len))
        {
            return KernelSpace.UserCopyFault;
        }

        byte[] data = GuestMemory.ReadSpan(src, (int)len).ToArray();
        bool done = InProcess(process, () =>
        {
            if (!IsUserAccessible(dst, len))
            {
                return false;
            }

            data.CopyTo(GuestMemory.Span(dst, (int)len));
            return true;
        });
        if (!done)
        {
            return KernelSpace.UserCopyFault;
        }

        process.Code.Invalidate(dst, len);
        return 0;
    }

    // ------------------------------------------------------------------ sysroot and debug

    /// <summary>ksceKernelSysrootGetProcessTitleId(pid, char *titleid, len): the title id of a process (the kernel's:
    /// empty).</summary>
    [Hle]
    int ksceKernelSysrootGetProcessTitleId(int pid, uint buf, uint len)
    {
        if (buf == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        string title;
        if (pid == KernelSpace.KernelPid)
        {
            title = "";
        }
        else if (ProcessOf(pid) is { } process)
        {
            title = process.ProcessTitleId;
        }
        else
        {
            return SceError.INVALID_PID;
        }

        if (len != 0)
        {
            GuestMemory.WriteCString(buf, title, (int)len);
        }

        return 0;
    }

    /// <summary>ksceKernelSysrootGetCurrentAddressSpaceCB(): the address space object of the calling process, stable
    /// for the process, in the FW 3.74 layout (flag, pid, its partition, magic; see SysmemKernel.FirmwareInternals):
    /// the firmware's internal block lookup takes it. The page tables are not reproduced (VitaSharp's address spaces
    /// are host mappings).</summary>
    [Hle(ReturnsValue = true, CallbackWait = true)]
    uint ksceKernelSysrootGetCurrentAddressSpaceCB()
    {
        int key = ProcessScope.CurrentPid == KernelSpace.KernelPid ? KernelSpace.KernelPid : Emu.Pid;
        return AddressSpaceOf(key);
    }

    /// <summary>ksceDebugPrintKernelPanic(regs, lr): kernel code found a fatal error; the system stops (the calling
    /// thread ends with the emulator's fatal error, naming the address).</summary>
    [Hle]
    void ksceDebugPrintKernelPanic(uint regs, uint lr)
    {
        Log.Error($"kernel panic (registers at 0x{regs:X8}, from {K.Symbolize(lr)})");
        throw new InvalidOperationException($"kernel panic from {K.Symbolize(lr)}");
    }
}
