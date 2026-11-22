using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The vnode pool and the vnode lock of SceIofilemgrForDriver as os0:kd/iofilemgr.skprx 3.74 has them (read in the
// decompiled module, study only).
//
// The pool (0x8100ae98) is one block of 0x1000 vnodes of 0x100 bytes; every vnode's lock is set up once there: +0 the
// number of threads waiting for it, +4 its owner (the thread id, -1 when free), +8 the owner's extra depth, +0xC an
// event flag "SceVfsVnode" (attribute 0x1000, one per 32 vnodes) and +0x10 the vnode's bit in it. Only a part of the
// pool is in use at first (0x400 vnodes, 0x81021024): vfsGetNewVnode (0x8100bca8) takes a free vnode while fewer than
// that are in use; past it the I/O manager recycles a cached vnode (0x81007934) and only when none can be recycled it
// lets 0x40 more into use (0x8100bc68), up to the whole pool, after which the call fails with 0x8001000C. vfsFreeVnode
// (0x8100aa10) gives a vnode back to the pool.
//
// The recycling scans the vnodes in use from where the last one stopped (0x8102296c), then from the start: a vnode is
// taken when its lock can be taken without waiting (0x8100a7b0), it has no reference (+0x58), it is active (state bit
// 0, +0x74), it is not a directory or a device (type at +0x78 up to 0x1001), its mount is mounted (+0x48 == 1) without
// flag 0x100000 and its parent (+0x50) can be locked without waiting: the driver's inactive op runs on it, its state
// becomes 0x2000, it leaves its mount's vnode list and is handed out again (the area from +0x40 cleared).
public sealed partial class IoFileMgr
{
    /// <summary>The vnodes in the pool.</summary>
    const int VnodePoolSlots = 0x1000;

    /// <summary>The vnodes in use at first, and how many more each growth lets in.</summary>
    const int VnodeLimitInitial = 0x400;
    const int VnodeLimitStep = 0x40;

    /// <summary>The vnodes that share one event flag of the lock.</summary>
    const int VnodesPerLockFlag = 32;

    /// <summary>The lock owner of a free vnode, and the one an unlock leaves for a waiter it wakes.</summary>
    const uint VnodeLockFree = 0xFFFFFFFF;
    const uint VnodeLockHandedOff = 0xFFFFFFFD;

    /// <summary>The state of a vnode the recycling took (+0x74).</summary>
    const uint VnodeStateRecycled = 0x2000;

    /// <summary>The highest type a vnode can be recycled with (a directory is 0x1002, a device 0x2010).</summary>
    const uint VnodeTypeRecyclableMax = 0x1001;

    /// <summary>The mount flag that keeps its vnodes from being recycled.</summary>
    const uint MountFlagNoRecycle = 0x100000;

    const int VFS_ERR_NO_VNODE = unchecked((int)0x8001000C);

    /// <summary>The pool's block (0: not made yet).</summary>
    [SnapshotMissing] // states of builds before it: vnodes were allocated one by one
    uint _vnodePool;

    /// <summary>The free vnodes of the pool, the next one handed out last.</summary>
    [SnapshotMissing]
    readonly List<uint> _vnodeFree = new();

    /// <summary>The vnodes in use (0x81022a40) and the number allowed (0x81021024).</summary>
    [SnapshotMissing]
    int _vnodesInUse;

    [SnapshotMissing(VnodeLimitInitial)]
    int _vnodeLimit = VnodeLimitInitial;

    /// <summary>Where the next recycling scan starts (0x8102296c).</summary>
    [SnapshotMissing]
    int _vnodeRecycleCursor;

    /// <summary>Guards the lock words of the vnodes (the module's exclusive loads and stores).</summary>
    readonly object _vnodeLockGate = new();

    /// <summary>Makes the pool on first use (0x8100ae98): every vnode's lock free, its event flag and bit.</summary>
    void EnsureVnodePool()
    {
        if (_vnodePool != 0)
        {
            return;
        }

        uint pool = Emu.Memory.Alloc(VnodePoolSlots * VnodeSize, "vfs vnode pool", MemoryRegion.Kernel, 16);
        GuestMemory.Fill(pool, 0, VnodePoolSlots * VnodeSize);
        uint flag = 0;
        for (int slot = 0; slot < VnodePoolSlots; slot++)
        {
            int bit = slot % VnodesPerLockFlag;
            if (bit == 0)
            {
                flag = (uint)NewVnodeLockFlag();
            }

            uint vnode = pool + (uint)slot * VnodeSize;
            GuestMemory.W32(vnode + 0xC, flag);
            GuestMemory.W32(vnode + 0x10, 1u << bit);
            GuestMemory.W32(vnode + 4, VnodeLockFree);
        }

        // the free list hands out the first vnode first
        for (int slot = VnodePoolSlots - 1; slot >= 0; slot--)
        {
            _vnodeFree.Add(pool + (uint)slot * VnodeSize);
        }

        _vnodePool = pool;
    }

    int NewVnodeLockFlag()
    {
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            return K.Objects.Register(new EventFlag { Name = "SceVfsVnode", Attr = EventFlag.AttrMulti });
        }
    }

    bool InVnodePool(uint vnode)
    {
        return _vnodePool != 0 && vnode >= _vnodePool && vnode < _vnodePool + VnodePoolSlots * VnodeSize;
    }

    /// <summary>
    /// vfsGetNewVnode(mount, vops, ?, vpp) as the module has it (0x8100bca8): a vnode from the pool (or a recycled
    /// one, see the file's header) with the area from +0x40 cleared, state 0x8000, +0xD0 the mount's +0x4E, its op
    /// table, its mount, one reference and +0x6C the mount's +0x44, at the head of the mount's vnode list (+0x54, the
    /// number at +0x58); *vpp the vnode and 0. With the whole pool in use and nothing to recycle, 0x8001000C.
    /// </summary>
    [Hle(Nid = 0xD60B5C63, Library = "SceIofilemgrForDriver")]
    int vfsGetNewVnode(uint mount, uint vops, uint unused, uint vpp)
    {
        if (vpp == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint vnode = TakeVnode(mount);
        if (vnode == 0)
        {
            Log.Warn($"vfs: no vnode left ({_vnodesInUse} in use)");
            return VFS_ERR_NO_VNODE;
        }

        GuestMemory.Fill(vnode + 0x40, 0, 0x98);
        GuestMemory.W32(vnode + 0x74, VnodeStateNew);
        GuestMemory.W32(vnode + 0x40, vops);
        GuestMemory.W32(vnode + 0x4C, mount);
        GuestMemory.W32(vnode + 0x58, 1);
        if (mount != 0)
        {
            GuestMemory.W32(vnode + 0xD0, (uint)(int)GuestMemory.Read<short>(mount + 0x4E));
            GuestMemory.W32(vnode + 0x6C, GuestMemory.R32(mount + 0x44));
            GuestMemory.W32(vnode + 0x54, GuestMemory.R32(mount + 0x54));
            GuestMemory.W32(mount + 0x54, vnode);
            GuestMemory.W32(mount + 0x58, GuestMemory.R32(mount + 0x58) + 1);
        }

        lock (_vfsObjects)
        {
            _vfsObjects[vnode] = 1;
        }

        GuestMemory.W32(vpp, vnode);
        return 0;
    }

    /// <summary>A free vnode of the pool while fewer than the limit are in use, else a recycled one, else the limit
    /// grows; 0 when the whole pool is in use and none can be recycled.</summary>
    uint TakeVnode(uint mount)
    {
        while (true)
        {
            lock (_vnodeFree)
            {
                EnsureVnodePool();
                if (_vnodesInUse < _vnodeLimit && _vnodeFree.Count > 0)
                {
                    uint free = _vnodeFree[^1];
                    _vnodeFree.RemoveAt(_vnodeFree.Count - 1);
                    _vnodesInUse++;
                    return free;
                }
            }

            uint recycled = RecycleVnode();
            if (recycled != 0)
            {
                ForgetHostVnode(recycled);
                ClearVnode(recycled);
                return recycled;
            }

            lock (_vnodeFree)
            {
                if (_vnodeLimit >= VnodePoolSlots)
                {
                    return 0;
                }

                _vnodeLimit += VnodeLimitStep;
            }
        }
    }

    /// <summary>The recycling scan (0x81007934, see the file's header): the vnode taken, or 0.</summary>
    uint RecycleVnode()
    {
        int start;
        int limit;
        lock (_vnodeFree)
        {
            start = _vnodeRecycleCursor;
            limit = _vnodeLimit;
        }

        while (true)
        {
            for (int slot = start; slot < limit; slot++)
            {
                uint vnode = _vnodePool + (uint)slot * VnodeSize;
                if (TryRecycle(vnode))
                {
                    lock (_vnodeFree)
                    {
                        _vnodeRecycleCursor = slot + 1 >= limit ? 0 : slot + 1;
                    }

                    return vnode;
                }
            }

            if (start == 0)
            {
                return 0;
            }

            start = 0;
        }
    }

    bool TryRecycle(uint vnode)
    {
        if (TryLockVnode(vnode) != 0)
        {
            return false;
        }

        uint mount = GuestMemory.R32(vnode + 0x4C);
        bool candidate = GuestMemory.R32(vnode + 0x58) == 0
            && (GuestMemory.R32(vnode + 0x74) & 1) != 0
            && GuestMemory.R32(vnode + 0x78) <= VnodeTypeRecyclableMax
            && mount != 0
            && GuestMemory.R32(mount + 0x48) == 1
            && (GuestMemory.R32(mount + 0x50) & MountFlagNoRecycle) == 0;
        if (candidate && (GuestMemory.R32(vnode + 0x78) & 2) != 0)
        {
            // a vnode whose name entry (+0x70) still has entries under it stays
            uint name = GuestMemory.R32(vnode + 0x70);
            candidate = name == 0 || GuestMemory.R32(name + 0xC) == 0;
        }

        uint parent = GuestMemory.R32(vnode + 0x50);
        if (!candidate || parent == 0 || TryLockVnode(parent) != 0)
        {
            vfsUnlockVnode(vnode);
            return false;
        }

        ksceVopInactive(vnode);
        GuestMemory.W32(vnode + 0x74, VnodeStateRecycled);
        vfsUnlockVnode(vnode);
        vfsUnlockVnode(parent);
        UnlinkMountVnode(mount, vnode);
        return true;
    }

    /// <summary>0x8100a1a0: the area from +0x40 cleared; a lock the calling thread holds is released.</summary>
    void ClearVnode(uint vnode)
    {
        GuestMemory.Fill(vnode + 0x40, 0, 0x98);
        if (GuestMemory.R32(vnode + 4) == (uint)CurrentThreadId())
        {
            GuestMemory.W32(vnode + 8, 0);
            vfsUnlockVnode(vnode);
        }
    }

    /// <summary>vfsFreeVnode(vnode) as the module has it (0x8100aa10): off its mount's vnode list (0x8100a9d8),
    /// cleared (0x8100a1a0) and back in the pool; 0. Not a vnode of the I/O manager's: 0x80010016.</summary>
    [Hle(Nid = 0x21D57633, Library = "SceIofilemgrForDriver")]
    int vfsFreeVnode(uint vnode)
    {
        lock (_vfsObjects)
        {
            if (!_vfsObjects.TryGetValue(vnode, out int kind) || kind != 1)
            {
                return VFS_ERR_INVAL;
            }

            _vfsObjects.Remove(vnode);
        }

        UnlinkMountVnode(GuestMemory.R32(vnode + 0x4C), vnode);
        ForgetHostVnode(vnode);
        if (!InVnodePool(vnode))
        {
            // a vnode of a state saved before the pool
            Emu.Memory.Free(vnode);
            return 0;
        }

        ClearVnode(vnode);
        lock (_vnodeFree)
        {
            _vnodeFree.Add(vnode);
            _vnodesInUse--;
        }

        return 0;
    }

    // ---- the vnode lock

    /// <summary>A vnode lock word set up for a vnode from outside the pool (a state saved before it).</summary>
    void EnsureVnodeLock(uint vnode)
    {
        if (GuestMemory.R32(vnode + 0x10) != 0)
        {
            return;
        }

        GuestMemory.W32(vnode + 0xC, (uint)NewVnodeLockFlag());
        GuestMemory.W32(vnode + 0x10, 1);
        GuestMemory.W32(vnode + 4, VnodeLockFree);
    }

    /// <summary>0x8100a7b0: the lock without waiting: free or already the caller's (one more depth) 0, another
    /// thread's 0x80010010.</summary>
    int TryLockVnode(uint vnode)
    {
        uint self = (uint)CurrentThreadId();
        lock (_vnodeLockGate)
        {
            EnsureVnodeLock(vnode);
            uint owner = GuestMemory.R32(vnode + 4);
            if (owner == VnodeLockFree)
            {
                GuestMemory.W32(vnode + 4, self);
                return 0;
            }

            if (owner == self)
            {
                GuestMemory.W32(vnode + 8, GuestMemory.R32(vnode + 8) + 1);
                return 0;
            }

            return VFS_ERR_BUSY;
        }
    }

    /// <summary>
    /// vfsLockVnode(vnode) as the module has it (0x8100a6ec): a free lock is taken (+4 the thread), the owner's own
    /// call adds a depth (+8); another thread counts itself as waiting (+0) and waits on the lock's event flag (+0xC,
    /// its bit +0x10, AND with the bit cleared) until an unlock hands the lock over; 0 or the wait's error.
    /// </summary>
    [Hle(Nid = 0xAA45010B, Library = "SceIofilemgrForDriver", Cost = HleCost.SchedulerWait)]
    int vfsLockVnode(uint vnode)
    {
        if (vnode == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint self = (uint)CurrentThreadId();
        lock (_vnodeLockGate)
        {
            EnsureVnodeLock(vnode);
            uint owner = GuestMemory.R32(vnode + 4);
            if (owner == VnodeLockFree)
            {
                GuestMemory.W32(vnode + 4, self);
                return 0;
            }

            if (owner == self)
            {
                GuestMemory.W32(vnode + 8, GuestMemory.R32(vnode + 8) + 1);
                return 0;
            }

            GuestMemory.W32(vnode, GuestMemory.R32(vnode) + 1);
            if (GuestMemory.R32(vnode + 4) == VnodeLockFree)
            {
                GuestMemory.W32(vnode + 4, self);
                GuestMemory.W32(vnode, GuestMemory.R32(vnode) - 1);
                return 0;
            }
        }

        int result = K.Objects.Get<EventFlag>((int)GuestMemory.R32(vnode + 0xC)) is { } flag
            ? flag.WaitFlag(GuestMemory.R32(vnode + 0x10), EventFlag.WaitAnd | EventFlag.WaitClearPat, default, default)
            : SceError.UNKNOWN_EVF_ID;
        lock (_vnodeLockGate)
        {
            GuestMemory.W32(vnode, GuestMemory.R32(vnode) - 1);
            if (result != 0)
            {
                return result;
            }

            GuestMemory.W32(vnode + 4, self);
        }

        return 0;
    }

    /// <summary>vfsUnlockVnode(vnode) as the module has it (0x8100a7e8): a thread that does not own the lock 0x80010001
    /// (the module stops the system there); a depth is taken off first; the last unlock frees the lock, or with a
    /// thread waiting hands it over (+4 -3) and sets its bit in the event flag; 0.</summary>
    [Hle(Nid = 0x6048F245, Library = "SceIofilemgrForDriver", Cost = HleCost.SchedulerWait)]
    int vfsUnlockVnode(uint vnode)
    {
        if (vnode == 0)
        {
            return VFS_ERR_INVAL;
        }

        bool wake;
        lock (_vnodeLockGate)
        {
            if (GuestMemory.R32(vnode + 4) != (uint)CurrentThreadId())
            {
                return VFS_ERR_PERM;
            }

            uint depth = GuestMemory.R32(vnode + 8);
            if (depth != 0)
            {
                GuestMemory.W32(vnode + 8, depth - 1);
                return 0;
            }

            wake = GuestMemory.R32(vnode) != 0;
            GuestMemory.W32(vnode + 4, wake ? VnodeLockHandedOff : VnodeLockFree);
        }

        if (wake && K.Objects.Get<EventFlag>((int)GuestMemory.R32(vnode + 0xC)) is { } flag)
        {
            flag.Set(GuestMemory.R32(vnode + 0x10));
        }

        return 0;
    }

    /// <summary>vfsIsOwnerVnode(vnode) as the module has it (0x8100a890): 1 when the lock's owner (+4) is the calling
    /// thread.</summary>
    [Hle(Nid = 0xC3AE93A2, Library = "SceIofilemgrForDriver", ReturnsValue = true)]
    int vfsIsOwnerVnode(uint vnode)
    {
        return vnode != 0 && GuestMemory.R32(vnode + 4) == (uint)CurrentThreadId() ? 1 : 0;
    }
}
