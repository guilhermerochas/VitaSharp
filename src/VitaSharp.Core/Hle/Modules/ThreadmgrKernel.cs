using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Mutex = VitaSharp.Core.Kernel.Mutex;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The kernel-only parts of the firmware's SceKernelThreadMgr (SceThreadmgrForDriver) that have no user twin, as the
/// FW 3.74 module has them (studied in the decompiled threadmgr.skprx).
/// <para>
/// Fast mutexes: a 0x40-byte work area the caller owns, in kernel memory. The firmware keeps the state there
/// (+0x08 the UID of its kernel object, which carries the name and the waiter queue; +0x14 the number of waiting
/// threads; +0x18 the lock count; +0x1C the owner; +0x20 the ceiling priority; +0x24 the magic 0xA35DB473 that marks it
/// initialized) and finalize clears all 0x40 bytes. Attributes: 0x2 recursive, 0x4 a ceiling priority (from the
/// option, or the caller's priority), 0x8 locked by the caller at once, 0x2000 and 0x8000 as for the other objects;
/// anything else is SCE_KERNEL_ERROR_ILLEGAL_ATTR. The owner word holds the owner's thread UID (the firmware's own
/// thread control block is not modelled; only the thread manager reads it, and the info call answers the UID).
/// </para>
/// </summary>
public sealed partial class ThreadmgrKernel : HleModule
{
    /// <summary>The magic word of an initialized fast mutex (+0x24).</summary>
    const uint FastMutexMagic = 0xA35DB473;

    const uint FastMutexAttrRecursive = 0x2, FastMutexAttrCeiling = 0x4, FastMutexAttrLocked = 0x8;

    /// <summary>The attribute bits ksceKernelInitializeFastMutex accepts.</summary>
    const uint FastMutexAttrAllowed = ~0xFFFF5FF1u;

    const uint FastMutexUid = 0x08, FastMutexWaiters = 0x14, FastMutexCount = 0x18, FastMutexOwner = 0x1C,
        FastMutexCeiling = 0x20, FastMutexMagicOffset = 0x24, FastMutexSize = 0x40;

    /// <summary>The ceiling of a fast mutex without the ceiling attribute: the lowest priority.</summary>
    const int NoCeiling = 0xFE;

    /// <summary>SceKernelFastMutexInfo's size.</summary>
    const uint FastMutexInfoSize = 0x44;

    // the error codes of the firmware's fast mutexes (FW 3.74 threadmgr)
    const int FastMutexRecursive = unchecked((int)0x80028162), FastMutexOverflow = unchecked((int)0x80028163),
        FastMutexFailedToOwn = unchecked((int)0x80028164), FastMutexNotOwned = unchecked((int)0x80028165),
        FastMutexLocked = unchecked((int)0x80028166), FastMutexAlreadyInitialized = unchecked((int)0x80028167),
        FastMutexNotInitialized = unchecked((int)0x80028168), IllegalContext = unchecked((int)0x80027101);

    /// <summary>The priority each fast mutex owner had before a ceiling raised it: <see cref="CeilingKey"/>.</summary>
    readonly Dictionary<ulong, int> _ceilingRestore = new();

    /// <summary>Each fast mutex's attributes as given (its kernel object keeps only the ones the wait queue uses).</summary>
    readonly Dictionary<int, uint> _fastMutexAttributes = new();

    static ulong CeilingKey(uint work, GuestThread owner) => ((ulong)work << 32) | (uint)owner.Uid;

    SysmemKernel Sysmem => Emu.Hle.Modules.OfType<SysmemKernel>().First();

    static GuestThread Self => GuestThread.Current!;

    Mutex? FastMutexOf(uint work)
    {
        if (work == 0 || GuestMemory.R32(work + FastMutexMagicOffset) != FastMutexMagic)
        {
            return null;
        }

        return K.Objects.Get<Mutex>((int)GuestMemory.R32(work + FastMutexUid));
    }

    /// <summary>The work area's mirror of the state (count, owner, waiters), as the firmware keeps it there.</summary>
    static void MirrorFastMutex(uint work, Mutex mutex)
    {
        GuestMemory.W32(work + FastMutexCount, (uint)mutex.LockCount);
        GuestMemory.W32(work + FastMutexOwner, (uint)(mutex.Owner?.Uid ?? 0));
        GuestMemory.W32(work + FastMutexWaiters, (uint)mutex.Waiters);
    }

    /// <summary>A new owner takes the ceiling priority when it is higher (a lower number) than its own.</summary>
    void ApplyCeiling(uint work, GuestThread owner)
    {
        int ceiling = (int)GuestMemory.R32(work + FastMutexCeiling);
        if (ceiling >= NoCeiling || ceiling >= owner.Priority)
        {
            return;
        }

        lock (_ceilingRestore)
        {
            _ceilingRestore[CeilingKey(work, owner)] = owner.Priority;
        }

        owner.Priority = ceiling;
    }

    void RestoreCeiling(uint work, GuestThread owner)
    {
        lock (_ceilingRestore)
        {
            if (_ceilingRestore.Remove(CeilingKey(work, owner), out int previous))
            {
                owner.Priority = previous;
            }
        }
    }

    /// <summary>
    /// ksceKernelInitializeFastMutex(work, name, attr, opt): checks the attributes, the option (its size at most 8;
    /// +4 the ceiling priority, 0 for the caller's) and that the work area is not initialized already, creates the
    /// kernel object and fills the work area; with attribute 0x8 the caller owns it at once.
    /// </summary>
    [Hle(Library = "SceThreadmgrForDriver")]
    int ksceKernelInitializeFastMutex(uint work, string name, uint attr, uint opt)
    {
        if (GuestThread.Current == null)
        {
            return IllegalContext;
        }

        if ((attr & ~FastMutexAttrAllowed) != 0)
        {
            return SceError.ILLEGAL_ATTR;
        }

        if (opt != 0 && GuestMemory.R32(opt) > 8)
        {
            return SceError.ILLEGAL_SIZE;
        }

        if (GuestMemory.R32(work + FastMutexMagicOffset) == FastMutexMagic)
        {
            return FastMutexAlreadyInitialized;
        }

        int ceiling = NoCeiling;
        if ((attr & FastMutexAttrCeiling) != 0)
        {
            if (opt == 0)
            {
                return SceError.INVALID_ARGUMENT;
            }

            ceiling = (int)GuestMemory.R32(opt + 4);
            if (ceiling == 0)
            {
                ceiling = Self.Priority;
            }

            if ((uint)(ceiling - 1) >= 0xFE || ((attr & FastMutexAttrLocked) != 0 && ceiling > Self.Priority))
            {
                return SceError.ILLEGAL_PRIORITY;
            }
        }

        var mutex = new Mutex { Name = name, Attr = attr & (FastMutexAttrRecursive | 0x2000) };
        int uid = K.Objects.Register(mutex);
        lock (_fastMutexAttributes)
        {
            _fastMutexAttributes[uid] = attr;
            _fastMutexWork[uid] = work;
        }

        GuestMemory.Fill(work, 0, FastMutexSize);
        GuestMemory.W32(work, work);
        GuestMemory.W32(work + 4, work);
        GuestMemory.W32(work + FastMutexUid, (uint)uid);
        GuestMemory.W32(work + FastMutexCeiling, (uint)ceiling);
        if ((attr & FastMutexAttrLocked) != 0)
        {
            mutex.InitOwner(Self, 1);
            ApplyCeiling(work, Self);
        }

        MirrorFastMutex(work, mutex);
        GuestMemory.W32(work + FastMutexMagicOffset, FastMutexMagic);
        return 0;
    }

    /// <summary>ksceKernelFinalizeFastMutex(work): deletes the kernel object and clears the work area; a locked fast
    /// mutex is not finalized.</summary>
    [Hle(Nid = 0x11FE84A1, Library = "SceThreadmgrForDriver")] // ksceKernelDeleteFastMutex in the NID list
    int ksceKernelFinalizeFastMutex(uint work)
    {
        if (FastMutexOf(work) is not { } mutex)
        {
            return FastMutexNotInitialized;
        }

        if (mutex.Owner != null)
        {
            return FastMutexLocked;
        }

        mutex.Delete();
        K.Objects.Delete(mutex);
        lock (_fastMutexAttributes)
        {
            _fastMutexAttributes.Remove(mutex.Uid);
            _fastMutexWork.Remove(mutex.Uid);
        }

        GuestMemory.Fill(work, 0, FastMutexSize);
        return 0;
    }

    /// <summary>
    /// ksceKernelLockFastMutex(work): takes the fast mutex, waiting while another thread owns it; the owner locks again
    /// only when it is recursive. Not with interrupts masked (SCE_KERNEL_ERROR_CAN_NOT_WAIT).
    /// </summary>
    [Hle(Library = "SceThreadmgrForDriver", Cost = HleCost.SchedulerWait)]
    int ksceKernelLockFastMutex(uint work) => LockFastMutex(work, wait: true);

    /// <summary>ksceKernelTryLockFastMutex(work): takes the fast mutex when it is free (or recursively by its owner);
    /// otherwise SCE_KERNEL_ERROR_FAST_MUTEX_FAILED_TO_OWN.</summary>
    [Hle(Library = "SceThreadmgrForDriver")]
    int ksceKernelTryLockFastMutex(uint work) => LockFastMutex(work, wait: false);

    int LockFastMutex(uint work, bool wait)
    {
        if (FastMutexOf(work) is not { } mutex)
        {
            return FastMutexNotInitialized;
        }

        if (wait && Sysmem.InterruptsMaskedForCurrentThread)
        {
            return SceError.CAN_NOT_WAIT;
        }

        GuestThread self = Self;
        if (mutex.Owner == self)
        {
            if ((mutex.Attr & FastMutexAttrRecursive) == 0)
            {
                return FastMutexRecursive;
            }

            if (mutex.LockCount == int.MaxValue)
            {
                return FastMutexOverflow;
            }
        }

        int result = wait ? mutex.Lock(self, 1, default) : mutex.TryLock(self, 1);
        if (result < 0)
        {
            return wait ? result : FastMutexFailedToOwn;
        }

        if (mutex.LockCount == 1)
        {
            ApplyCeiling(work, self);
        }

        MirrorFastMutex(work, mutex);
        return 0;
    }

    /// <summary>ksceKernelUnlockFastMutex(work): the owner unlocks once; at 0 the mutex goes to the next waiter and the
    /// owner's priority comes back from the ceiling.</summary>
    [Hle(Library = "SceThreadmgrForDriver")]
    int ksceKernelUnlockFastMutex(uint work)
    {
        if (FastMutexOf(work) is not { } mutex)
        {
            return FastMutexNotInitialized;
        }

        GuestThread self = Self;
        if (mutex.Owner != self)
        {
            return FastMutexNotOwned;
        }

        bool last = mutex.LockCount == 1;
        int result = mutex.Unlock(self, 1);
        if (result < 0)
        {
            return result;
        }

        if (last)
        {
            RestoreCeiling(work, self);
        }

        MirrorFastMutex(work, mutex);
        return 0;
    }

    /// <summary>
    /// ksceKernelGetFastMutexInfo(work, info): SceKernelFastMutexInfo (0x44: +0 size, +4 UID, +8 name[32], +0x28 attr,
    /// +0x2C the work area, +0x30 lock count, +0x34 owner thread UID, +0x38 ceiling priority, +0x3C and +0x40 the
    /// waiting threads); copies at most the caller's size (more than 0x44 is SCE_KERNEL_ERROR_ILLEGAL_SIZE).
    /// </summary>
    [Hle(Library = "SceThreadmgrForDriver")]
    int ksceKernelGetFastMutexInfo(uint work, uint info)
    {
        if (FastMutexOf(work) is not { } mutex)
        {
            return FastMutexNotInitialized;
        }

        if (info == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        uint size = GuestMemory.R32(info);
        if (size > FastMutexInfoSize)
        {
            return SceError.ILLEGAL_SIZE;
        }

        Span<byte> fields = stackalloc byte[(int)FastMutexInfoSize];
        fields.Clear();
        lock (mutex)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fields, FastMutexInfoSize);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(fields[4..], mutex.Uid);
            // the name as strncpy(name, 0x1F) copies it
            byte[] name = System.Text.Encoding.UTF8.GetBytes(mutex.Name);
            name.AsSpan(0, Math.Min(name.Length, 0x1F)).CopyTo(fields[8..]);
            uint attr;
            lock (_fastMutexAttributes)
            {
                attr = _fastMutexAttributes.GetValueOrDefault(mutex.Uid);
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fields[0x28..], attr);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fields[0x2C..], work);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(fields[0x30..], mutex.LockCount);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(fields[0x34..], mutex.Owner?.Uid ?? 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fields[0x38..], GuestMemory.R32(work + FastMutexCeiling));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(fields[0x3C..], mutex.Waiters);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(fields[0x40..], mutex.Waiters);
        }

        fields[..(int)size].CopyTo(GuestMemory.Span(info, (int)size));
        return 0;
    }
}
