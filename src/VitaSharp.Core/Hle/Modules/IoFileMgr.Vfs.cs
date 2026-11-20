using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// The VFS layer of SceIofilemgrForDriver (os0:kd/iofilemgr.skprx 3.74, read in the module, study only): the
// interface kernel file system drivers (exFAT, PFS, the SD storage, the UART tty) register with and work through.
// A driver registers a SceVfsInfo (+0 its VFS op table, +4 its name, compared on 0x20 bytes, +0xC its reference count,
// +0x14 its default vnode op table, +0x1C the next registered) with ksceVfsAddVfs; the I/O manager hands it vnodes
// (+0x40 the vnode op table, +0x4C the mount, +0x58 the reference count, +0x6C the driver's data, +0x74 the state, a
// lock word at +0 and its owner at +4) and file objects. The ksceVop* calls are the I/O manager's way into a vnode's
// operations: each builds the operation's argument block (the vnode first, then the call's arguments) on the stack
// and calls entry N of the vnode's op table (open 0, create 4, close 8, lookup 0xC, read 0x10, write 0x14, lseek 0x18,
// ioctl 0x1C, remove 0x20, mkdir 0x24, rmdir 0x28, dopen 0x2C, dclose 0x30, dread 0x34, getstat 0x38, chstat 0x3C,
// rename 0x40, pread 0x48, pwrite 0x4C, inactive 0x50, link 0x54, unlink 0x58, sync 0x5C, fgetstat 0x60, fchstat
// 0x64, whiteout 0x68, cleanup 0x6C, zerofill 0x70); a NULL entry is 0x80010030, a NULL vnode 0x80010016.
// The emulator's own devices (ux0:, app0:, savedata0:...) are served from host folders by IoFileMgr, not by guest
// drivers: a driver's registration, its vnodes, files and locks work, and its operations run through ksceVop*, but
// mounting a guest file system on a path (vfsMount, the PFS mounts, removing a block device) is not wired into the
// emulator's path resolution and answers 0x80010030 (the operation is not supported) after its argument checks.
public sealed partial class IoFileMgr
{
    const int VFS_ERR_PERM = unchecked((int)0x80010001);
    const int VFS_ERR_NOENT = unchecked((int)0x80010002);
    const int VFS_ERR_BUSY = unchecked((int)0x80010010);
    const int VFS_ERR_EXIST = unchecked((int)0x80010011);
    const int VFS_ERR_INVAL = unchecked((int)0x80010016);
    const int VFS_ERR_UNSUP = unchecked((int)0x80010030);
    const int VFS_ERR_UNMOUNTING = unchecked((int)0x80010013);

    /// <summary>The state word of a mount being unmounted (+0x48).</summary>
    const uint MountStateUnmounting = 0x10;

    /// <summary>The size of a vnode the I/O manager hands out.</summary>
    const uint VnodeSize = 0x100;

    /// <summary>The size of a file object.</summary>
    const uint VfsFileSize = 0x80;

    /// <summary>The state of a fresh vnode (+0x74).</summary>
    const uint VnodeStateNew = 0x8000;

    /// <summary>The registered drivers (their SceVfsInfo addresses, newest first, as the module links them).</summary>
    [SnapshotMissing] // states of builds before it: none registered
    readonly List<uint> _vfsDrivers = new();

    /// <summary>The vnodes and file objects handed out (address to kind: 1 vnode, 2 file).</summary>
    [SnapshotMissing]
    readonly Dictionary<uint, int> _vfsObjects = new();

    /// <summary>The locked mounts (address to the owning thread's id).</summary>
    [SnapshotMissing]
    readonly Dictionary<uint, int> _vfsLockedMounts = new();

    static int CurrentThreadId()
    {
        return GuestThread.Current?.Uid ?? 0;
    }

    /// <summary>ksceVfsAddVfs(info): registers a driver; a name already registered 0x80010011.</summary>
    [Hle(Nid = 0x673D2FCD, Library = "SceIofilemgrForDriver")]
    int ksceVfsAddVfs(uint info)
    {
        if (info == 0)
        {
            return VFS_ERR_INVAL;
        }

        string name = GuestMemory.ReadCString(GuestMemory.R32(info + 4), 0x20);
        lock (_vfsDrivers)
        {
            foreach (uint driver in _vfsDrivers)
            {
                if (GuestMemory.ReadCString(GuestMemory.R32(driver + 4), 0x20) == name)
                {
                    return VFS_ERR_EXIST;
                }
            }

            GuestMemory.W32(info + 0x1C, _vfsDrivers.Count > 0 ? _vfsDrivers[0] : 0);
            GuestMemory.W32(info + 0xC, 0);
            _vfsDrivers.Insert(0, info);
        }

        return 0;
    }

    /// <summary>ksceVfsDeleteVfs(name, info out): unregisters a driver: unknown 0x80010002, still referenced
    /// 0x80010010; *info the driver's block.</summary>
    [Hle(Nid = 0x9CBFA725, Library = "SceIofilemgrForDriver")]
    int ksceVfsDeleteVfs(uint name, uint info)
    {
        if (name == 0)
        {
            return VFS_ERR_INVAL;
        }

        string text = GuestMemory.ReadCString(name, 0x20);
        lock (_vfsDrivers)
        {
            int index = _vfsDrivers.FindIndex(driver => GuestMemory.ReadCString(GuestMemory.R32(driver + 4), 0x20) == text);
            if (index < 0)
            {
                return VFS_ERR_NOENT;
            }

            uint driverInfo = _vfsDrivers[index];
            if (GuestMemory.R32(driverInfo + 0xC) != 0)
            {
                return VFS_ERR_BUSY;
            }

            _vfsDrivers.RemoveAt(index);
            if (info != 0)
            {
                GuestMemory.W32(info, driverInfo);
            }
        }

        return 0;
    }

    /// <summary>vfsGetNewVnode(mount, vops, ?, vpp): a fresh vnode (the area from +0x40 zeroed, state 0x8000, its
    /// op table, mount and the mount's driver data, one reference); *vpp the vnode; 0.</summary>
    [Hle(Nid = 0xD60B5C63, Library = "SceIofilemgrForDriver")]
    int vfsGetNewVnode(uint mount, uint vops, uint unused, uint vpp)
    {
        if (vpp == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint vnode = Emu.Memory.Alloc(VnodeSize, "vfs vnode", MemoryRegion.Kernel);
        GuestMemory.Fill(vnode, 0, VnodeSize);
        GuestMemory.W32(vnode + 0x40, vops);
        GuestMemory.W32(vnode + 0x4C, mount);
        GuestMemory.W32(vnode + 0x58, 1);
        GuestMemory.W32(vnode + 0x74, VnodeStateNew);
        if (mount != 0)
        {
            GuestMemory.W32(vnode + 0x6C, GuestMemory.R32(mount + 0x44));
            GuestMemory.W32(vnode + 0xD0, (uint)(int)GuestMemory.Read<short>(mount + 0x4E));
        }

        lock (_vfsObjects)
        {
            _vfsObjects[vnode] = 1;
        }

        GuestMemory.W32(vpp, vnode);
        return 0;
    }

    /// <summary>vfsFreeVnode(vnode): returns it; 0.</summary>
    [Hle(Nid = 0x21D57633, Library = "SceIofilemgrForDriver")]
    int vfsFreeVnode(uint vnode)
    {
        return VfsFree(vnode, 1);
    }

    int VfsFree(uint address, int kind)
    {
        lock (_vfsObjects)
        {
            if (!_vfsObjects.TryGetValue(address, out int known) || known != kind)
            {
                return VFS_ERR_INVAL;
            }

            _vfsObjects.Remove(address);
        }

        Emu.Memory.Free(address);
        return 0;
    }

    /// <summary>vfsAllocateFile(vnode, file out, ...): a file object for the vnode (+0 the vnode); 0.</summary>
    [Hle(Nid = 0xBBAC1751, Library = "SceIofilemgrForDriver")]
    int vfsAllocateFile(uint vnode, uint file)
    {
        if (file == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint block = Emu.Memory.Alloc(VfsFileSize, "vfs file", MemoryRegion.Kernel);
        GuestMemory.Fill(block, 0, VfsFileSize);
        GuestMemory.W32(block, vnode);
        lock (_vfsObjects)
        {
            _vfsObjects[block] = 2;
        }

        GuestMemory.W32(file, block);
        return 0;
    }

    /// <summary>vfsAllocateFileDummy(vnode, file out, ?): the same kind of file object, without the driver's open.</summary>
    [Hle(Nid = 0xCDF3EF52, Library = "SceIofilemgrForDriver")]
    int vfsAllocateFileDummy(uint vnode, uint file, uint unused)
    {
        return vfsAllocateFile(vnode, file);
    }

    /// <summary>vfsFreeFile(vnode, file): returns it.</summary>
    [Hle(Nid = 0xC85E33A2, Library = "SceIofilemgrForDriver")]
    int vfsFreeFile(uint vnode, uint file)
    {
        return VfsFree(file, 2);
    }

    /// <summary>vfsLockVnode(vnode): the vnode's lock for the calling thread (recursive: +8 the depth); 0.</summary>
    [Hle(Nid = 0xAA45010B, Library = "SceIofilemgrForDriver")]
    int vfsLockVnode(uint vnode)
    {
        if (vnode == 0)
        {
            return VFS_ERR_INVAL;
        }

        GuestMemory.W32(vnode, 1);
        GuestMemory.W32(vnode + 4, (uint)CurrentThreadId());
        GuestMemory.W32(vnode + 8, GuestMemory.R32(vnode + 8) + 1);
        return 0;
    }

    /// <summary>vfsUnlockVnode(vnode): a thread that does not own the lock 0x80010001; the last unlock frees it.</summary>
    [Hle(Nid = 0x6048F245, Library = "SceIofilemgrForDriver")]
    int vfsUnlockVnode(uint vnode)
    {
        if (vnode == 0)
        {
            return VFS_ERR_INVAL;
        }

        if (GuestMemory.R32(vnode) == 0 || GuestMemory.R32(vnode + 4) != (uint)CurrentThreadId())
        {
            return VFS_ERR_PERM;
        }

        uint depth = GuestMemory.R32(vnode + 8) - 1;
        GuestMemory.W32(vnode + 8, depth);
        if (depth == 0)
        {
            GuestMemory.W32(vnode, 0);
            GuestMemory.W32(vnode + 4, 0);
        }

        return 0;
    }

    /// <summary>vfsIsOwnerVnode(vnode): 1 when the calling thread holds the vnode's lock.</summary>
    [Hle(Nid = 0xC3AE93A2, Library = "SceIofilemgrForDriver", ReturnsValue = true)]
    int vfsIsOwnerVnode(uint vnode)
    {
        return vnode != 0 && GuestMemory.R32(vnode) != 0 && GuestMemory.R32(vnode + 4) == (uint)CurrentThreadId() ? 1 : 0;
    }

    /// <summary>vfsLockMnt(mount): the mount's lock for the calling thread; 0.</summary>
    [Hle(Nid = 0x6B3CA9F7, Library = "SceIofilemgrForDriver")]
    int vfsLockMnt(uint mount)
    {
        lock (_vfsLockedMounts)
        {
            _vfsLockedMounts[mount] = CurrentThreadId();
        }

        return 0;
    }

    /// <summary>vfsUnlockMnt(mount): releases it; 0.</summary>
    [Hle(Nid = 0xDC2D8BCE, Library = "SceIofilemgrForDriver")]
    int vfsUnlockMnt(uint mount)
    {
        lock (_vfsLockedMounts)
        {
            _vfsLockedMounts.Remove(mount);
        }

        return 0;
    }

    /// <summary>vfsIsLockedMnt(mount): 1 when the calling thread holds the mount's lock.</summary>
    [Hle(Nid = 0xC468B5EF, Library = "SceIofilemgrForDriver", ReturnsValue = true)]
    int vfsIsLockedMnt(uint mount)
    {
        lock (_vfsLockedMounts)
        {
            return _vfsLockedMounts.TryGetValue(mount, out int owner) && owner == CurrentThreadId() ? 1 : 0;
        }
    }

    /// <summary>vfsGetVnodeName(vnode, buffer, size): the vnode's name the module keeps: none, an empty string; 0.</summary>
    [Hle(Nid = 0xAA253B68, Library = "SceIofilemgrForDriver")]
    int vfsGetVnodeName(uint vnode, uint buffer, uint size)
    {
        if (buffer != 0 && size != 0)
        {
            GuestMemory.Write(buffer, (byte)0);
        }

        return 0;
    }

    /// <summary>vfsGetMntInfo(path, info): the mount behind a path: the emulator's devices are not guest mounts
    /// (0x80010002).</summary>
    [Hle(Nid = 0xB987450D, Library = "SceIofilemgrForDriver")]
    int vfsGetMntInfo(uint path, uint info)
    {
        return VFS_ERR_NOENT;
    }

    /// <summary>vfsGetMntList(list, count): the guest mounts: none (count 0); 0.</summary>
    [Hle(Nid = 0xC20C621C, Library = "SceIofilemgrForDriver")]
    int vfsGetMntList(uint list, uint count)
    {
        if (count != 0)
        {
            GuestMemory.W32(count, 0);
        }

        return 0;
    }

    /// <summary>A mount request of a guest file system: NULL 0x80010016; not wired into the emulator's paths
    /// (0x80010030).</summary>
    static int VfsMountRequest(uint argument)
    {
        return argument == 0 ? VFS_ERR_INVAL : VFS_ERR_UNSUP;
    }

    /// <summary>vfsMount(param): mounts a registered driver on a path.</summary>
    [Hle(Nid = 0xB62DE9A6, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsMount(uint param)
    {
        return VfsMountRequest(param);
    }

    /// <summary>vfsUmount(param): unmounts it.</summary>
    [Hle(Nid = 0x9C7E7B76, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsUmount(uint param)
    {
        return VfsMountRequest(param);
    }

    /// <summary>vfsMountForPFS(?, path, param): the PFS overlay mount.</summary>
    [Hle(Nid = 0xFEEE44A9, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsMountForPFS(uint unused, uint path, uint param)
    {
        return path == 0 || param == 0 ? VFS_ERR_INVAL : VFS_ERR_UNSUP;
    }

    /// <summary>vfsUmountForPFS(?, path, param): its undo.</summary>
    [Hle(Nid = 0xD220539D, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsUmountForPFS(uint unused, uint path, uint param)
    {
        return path == 0 || param == 0 ? VFS_ERR_INVAL : VFS_ERR_UNSUP;
    }

    /// <summary>vfsRmdev(param): removes a block device's mounts.</summary>
    [Hle(Nid = 0x69A7E076, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsRmdev(uint param)
    {
        return VfsMountRequest(param);
    }

    /// <summary>The I/O manager's file operations on a guest file system's file (one argument block): NULL
    /// 0x80010016; guest file systems are not mounted (0x80010030).</summary>
    [Hle(Nid = 0x0E518FA9, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsOpen(uint args)
    {
        return VfsMountRequest(args);
    }

    /// <summary>vfsClose(args).</summary>
    [Hle(Nid = 0x1AE14011, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsClose(uint args)
    {
        return VfsMountRequest(args);
    }

    /// <summary>vfsRead(args).</summary>
    [Hle(Nid = 0x809892C1, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsRead(uint args)
    {
        return VfsMountRequest(args);
    }

    /// <summary>vfsWrite(args).</summary>
    [Hle(Nid = 0x8598ADC3, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsWrite(uint args)
    {
        return VfsMountRequest(args);
    }

    /// <summary>vfsPread(args).</summary>
    [Hle(Nid = 0x0B54F9E0, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsPread(uint args)
    {
        return VfsMountRequest(args);
    }

    /// <summary>vfsPwrite(args).</summary>
    [Hle(Nid = 0xE5DEA6B7, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsPwrite(uint args)
    {
        return VfsMountRequest(args);
    }

    /// <summary>vfsIoctl(args).</summary>
    [Hle(Nid = 0xC1DD4317, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsIoctl(uint args)
    {
        return VfsMountRequest(args);
    }

    /// <summary>vfsChstat(args).</summary>
    [Hle(Nid = 0xBCE4865B, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsChstat(uint args)
    {
        return VfsMountRequest(args);
    }

    /// <summary>vfsChstatByFd(args).</summary>
    [Hle(Nid = 0x7517FE29, Library = "SceIofilemgrForDriver", Default = true)]
    int vfsChstatByFd(uint args)
    {
        return VfsMountRequest(args);
    }

    /// <summary>vfsBuffCacheCtl(vnode, ?, offset, size, buffer): the buffer cache of a guest file system's vnode:
    /// NULL vnode 0x80010016; the I/O manager keeps no buffer cache for it: 0.</summary>
    [Hle(Nid = 0xA7020B0D, Library = "SceIofilemgrForDriver")]
    int vfsBuffCacheCtl(uint vnode, uint command, uint offset, uint size, uint buffer)
    {
        return vnode == 0 ? VFS_ERR_INVAL : 0;
    }

    /// <summary>vfsBuffCacheFree(vnode, ?, ?, ?): frees its cached blocks: NULL 0x80010016; none, 0.</summary>
    [Hle(Nid = 0xD6AB5E4B, Library = "SceIofilemgrForDriver")]
    int vfsBuffCacheFree(uint vnode, uint first, uint second, uint third)
    {
        return vnode == 0 ? VFS_ERR_INVAL : 0;
    }

    // ---- the vnode operations

    /// <summary>Calls entry <paramref name="offset"/> of the op table at <paramref name="table"/> with an argument
    /// block of <paramref name="words"/> built on the calling thread's stack: NULL entry 0x80010030.</summary>
    static unsafe int VfsCallOp(uint table, uint offset, ReadOnlySpan<uint> words)
    {
        uint entry = table == 0 ? 0 : GuestMemory.R32(table + offset);
        if (entry == 0)
        {
            return VFS_ERR_UNSUP;
        }

        if (GuestThread.Current is not { } thread)
        {
            return VFS_ERR_UNSUP;
        }

        uint stackPointer = thread.Core.State->R[13];
        uint block = (stackPointer - 0x80 - (uint)words.Length * 4) & ~7u;
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(block + (uint)i * 4, words[i]);
        }

        return (int)thread.CallGuestOnStack(entry, block, block);
    }

    /// <summary>A vnode operation: NULL vnode 0x80010016; entry <paramref name="offset"/> of its op table (+0x40).</summary>
    static int VopCall(uint vnode, uint offset, ReadOnlySpan<uint> words)
    {
        if (vnode == 0)
        {
            return VFS_ERR_INVAL;
        }

        return VfsCallOp(GuestMemory.R32(vnode + 0x40), offset, words);
    }

    /// <summary>ksceVopOpen(vnode, path, flags, file): op 0.</summary>
    [Hle(Nid = 0x76B79BEC, Library = "SceIofilemgrForDriver")]
    int ksceVopOpen(uint vnode, uint path, uint flags, uint file)
    {
        return VopCall(vnode, 0x00, [vnode, path, flags, file]);
    }

    /// <summary>ksceVopCreate(dvnode, vpp, path, flags, mode): op 4.</summary>
    [Hle(Nid = 0x9E347C7D, Library = "SceIofilemgrForDriver")]
    int ksceVopCreate(uint vnode, uint vpp, uint path, uint flags, uint mode)
    {
        return VopCall(vnode, 0x04, [vnode, vpp, path, flags, mode]);
    }

    /// <summary>ksceVopClose(vnode, file): op 8.</summary>
    [Hle(Nid = 0x40944C2E, Library = "SceIofilemgrForDriver")]
    int ksceVopClose(uint vnode, uint file)
    {
        return VopCall(vnode, 0x08, [vnode, file]);
    }

    /// <summary>ksceVopLookup(dvnode, vpp, path, flags): op 0xC.</summary>
    [Hle(Nid = 0xA5A6A55C, Library = "SceIofilemgrForDriver")]
    int ksceVopLookup(uint vnode, uint vpp, uint path, uint flags)
    {
        return VopCall(vnode, 0x0C, [vnode, vpp, path, flags]);
    }

    /// <summary>ksceVopRead(vnode, file, buffer, size, result): op 0x10.</summary>
    [Hle(Nid = 0x570388A5, Library = "SceIofilemgrForDriver")]
    int ksceVopRead(uint vnode, uint file, uint buffer, uint size, uint result)
    {
        return VopCall(vnode, 0x10, [vnode, file, buffer, size, result]);
    }

    /// <summary>ksceVopWrite(vnode, file, buffer, size, result): op 0x14.</summary>
    [Hle(Nid = 0x9A68378D, Library = "SceIofilemgrForDriver")]
    int ksceVopWrite(uint vnode, uint file, uint buffer, uint size, uint result)
    {
        return VopCall(vnode, 0x14, [vnode, file, buffer, size, result]);
    }

    /// <summary>ksceVopLseek(vnode, file, offset (64-bit), whence): op 0x18 with { vnode, file, offset, whence }.</summary>
    [Hle(Nid = 0xB2B13818, Library = "SceIofilemgrForDriver")]
    int ksceVopLseek(uint vnode, uint file, uint offsetLow, uint offsetHigh, uint whence)
    {
        return VopCall(vnode, 0x18, [vnode, file, offsetLow, offsetHigh, whence]);
    }

    /// <summary>ksceVopIoctl(vnode, file, command, input, inputSize, output, outputSize): op 0x1C.</summary>
    [Hle(Nid = 0x333C904D, Library = "SceIofilemgrForDriver")]
    int ksceVopIoctl(uint vnode, uint file, uint command, uint input, uint inputSize, uint output, uint outputSize)
    {
        return VopCall(vnode, 0x1C, [vnode, file, command, input, inputSize, output, outputSize]);
    }

    /// <summary>ksceVopRemove(dvnode, vnode, path, flags): op 0x20.</summary>
    [Hle(Nid = 0xDC1E7EE4, Library = "SceIofilemgrForDriver")]
    int ksceVopRemove(uint vnode, uint target, uint path, uint flags)
    {
        return VopCall(vnode, 0x20, [vnode, target, path, flags]);
    }

    /// <summary>ksceVopMkdir(dvnode, vpp, path, mode): op 0x24.</summary>
    [Hle(Nid = 0x2F3F8C70, Library = "SceIofilemgrForDriver")]
    int ksceVopMkdir(uint vnode, uint vpp, uint path, uint mode)
    {
        return VopCall(vnode, 0x24, [vnode, vpp, path, mode]);
    }

    /// <summary>ksceVopRmdir(dvnode, vnode, path, flags): op 0x28.</summary>
    [Hle(Nid = 0x1D551105, Library = "SceIofilemgrForDriver")]
    int ksceVopRmdir(uint vnode, uint target, uint path, uint flags)
    {
        return VopCall(vnode, 0x28, [vnode, target, path, flags]);
    }

    /// <summary>ksceVopDopen(vnode, path, file): op 0x2C.</summary>
    [Hle(Nid = 0x00C9C2DD, Library = "SceIofilemgrForDriver")]
    int ksceVopDopen(uint vnode, uint path, uint file)
    {
        return VopCall(vnode, 0x2C, [vnode, path, file]);
    }

    /// <summary>ksceVopDclose(vnode, file): op 0x30.</summary>
    [Hle(Nid = 0x1350F5C7, Library = "SceIofilemgrForDriver")]
    int ksceVopDclose(uint vnode, uint file)
    {
        return VopCall(vnode, 0x30, [vnode, file]);
    }

    /// <summary>ksceVopDread(vnode, file, dirent): NULL dirent 0x80010016; op 0x34.</summary>
    [Hle(Nid = 0x77584C8F, Library = "SceIofilemgrForDriver")]
    int ksceVopDread(uint vnode, uint file, uint dirent)
    {
        return dirent == 0 ? VFS_ERR_INVAL : VopCall(vnode, 0x34, [vnode, file, dirent]);
    }

    /// <summary>ksceVopGetstat(vnode, path, stat): NULL stat 0x80010016; op 0x38.</summary>
    [Hle(Nid = 0x50A63ACF, Library = "SceIofilemgrForDriver")]
    int ksceVopGetstat(uint vnode, uint path, uint stat)
    {
        return stat == 0 ? VFS_ERR_INVAL : VopCall(vnode, 0x38, [vnode, path, stat]);
    }

    /// <summary>ksceVopChstat(vnode, path, stat, bits): NULL stat 0x80010016; op 0x3C.</summary>
    [Hle(Nid = 0x1974FA92, Library = "SceIofilemgrForDriver")]
    int ksceVopChstat(uint vnode, uint path, uint stat, uint bits)
    {
        return stat == 0 ? VFS_ERR_INVAL : VopCall(vnode, 0x3C, [vnode, path, stat, bits]);
    }

    /// <summary>ksceVopRename(odvnode, ovnode, opath, ndvnode, nvpp, npath): op 0x40.</summary>
    [Hle(Nid = 0x36A794C7, Library = "SceIofilemgrForDriver")]
    int ksceVopRename(uint vnode, uint oldVnode, uint oldPath, uint newDirectory, uint newVpp, uint newPath)
    {
        return VopCall(vnode, 0x40, [vnode, oldVnode, oldPath, newDirectory, newVpp, newPath]);
    }

    /// <summary>ksceVopPread(vnode, file, buffer, size, offset (64-bit), result): op 0x48.</summary>
    [Hle(Nid = 0xABBC80E3, Library = "SceIofilemgrForDriver")]
    int ksceVopPread(uint vnode, uint file, uint buffer, uint size, uint offsetLow, uint offsetHigh, uint result)
    {
        return VopCall(vnode, 0x48, [vnode, file, buffer, size, offsetLow, offsetHigh, result]);
    }

    /// <summary>ksceVopPwrite(vnode, file, buffer, size, offset (64-bit), result): op 0x4C.</summary>
    [Hle(Nid = 0xA53C040D, Library = "SceIofilemgrForDriver")]
    int ksceVopPwrite(uint vnode, uint file, uint buffer, uint size, uint offsetLow, uint offsetHigh, uint result)
    {
        return VopCall(vnode, 0x4C, [vnode, file, buffer, size, offsetLow, offsetHigh, result]);
    }

    /// <summary>ksceVopInactive(vnode): op 0x50.</summary>
    [Hle(Nid = 0x8FB94521, Library = "SceIofilemgrForDriver")]
    int ksceVopInactive(uint vnode)
    {
        return VopCall(vnode, 0x50, [vnode]);
    }

    /// <summary>ksceVopLink(from, to): op 0x54.</summary>
    [Hle(Nid = 0x942AA61F, Library = "SceIofilemgrForDriver")]
    int ksceVopLink(uint vnode, uint target)
    {
        return VopCall(vnode, 0x54, [vnode, target]);
    }

    /// <summary>ksceVopUnlink(from, to): op 0x58.</summary>
    [Hle(Nid = 0x0D8A806E, Library = "SceIofilemgrForDriver")]
    int ksceVopUnlink(uint vnode, uint target)
    {
        return VopCall(vnode, 0x58, [vnode, target]);
    }

    /// <summary>ksceVopSync(vnode, file, flags): op 0x5C.</summary>
    [Hle(Nid = 0x9CD96406, Library = "SceIofilemgrForDriver")]
    int ksceVopSync(uint vnode, uint file, uint flags)
    {
        return VopCall(vnode, 0x5C, [vnode, file, flags]);
    }

    /// <summary>ksceVopFgetstat(vnode, file, stat): NULL stat 0x80010016; op 0x60.</summary>
    [Hle(Nid = 0x1DBCBB01, Library = "SceIofilemgrForDriver")]
    int ksceVopFgetstat(uint vnode, uint file, uint stat)
    {
        return stat == 0 ? VFS_ERR_INVAL : VopCall(vnode, 0x60, [vnode, file, stat]);
    }

    /// <summary>ksceVopFchstat(vnode, file, stat, bits): NULL stat 0x80010016; op 0x64.</summary>
    [Hle(Nid = 0x082AFD7F, Library = "SceIofilemgrForDriver")]
    int ksceVopFchstat(uint vnode, uint file, uint stat, uint bits)
    {
        return stat == 0 ? VFS_ERR_INVAL : VopCall(vnode, 0x64, [vnode, file, stat, bits]);
    }

    /// <summary>ksceVopWhiteout(dvnode, vnode, oldPath, newPath): op 0x68.</summary>
    [Hle(Nid = 0xF53399BC, Library = "SceIofilemgrForDriver")]
    int ksceVopWhiteout(uint vnode, uint target, uint oldPath, uint newPath)
    {
        return VopCall(vnode, 0x68, [vnode, target, oldPath, newPath]);
    }

    /// <summary>ksceVopCleanup(vnode, file): op 0x6C.</summary>
    [Hle(Nid = 0x0F7E1718, Library = "SceIofilemgrForDriver")]
    int ksceVopCleanup(uint vnode, uint file)
    {
        return VopCall(vnode, 0x6C, [vnode, file]);
    }

    /// <summary>ksceVopZerofill(vnode, ?, ?, ?, ?): op 0x70.</summary>
    [Hle(Nid = 0xEEAE8B51, Library = "SceIofilemgrForDriver")]
    int ksceVopZerofill(uint vnode, uint first, uint second, uint third, uint fourth)
    {
        return VopCall(vnode, 0x70, [vnode, first, second, third, fourth]);
    }

    /// <summary>The VFS operation of a mount (+0x5C its driver's SceVfsInfo, whose +0 is the VFS op table): entry
    /// <paramref name="offset"/>; NULL mount 0x80010016.</summary>
    static int VfsOpCall(uint mount, uint offset, ReadOnlySpan<uint> words)
    {
        if (mount == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint info = GuestMemory.R32(mount + 0x5C);
        uint table = info == 0 ? 0 : GuestMemory.R32(info);
        return VfsCallOp(table, offset, words);
    }

    /// <summary>ksceVfsOpDevctl(mount, device, command, input, inputSize, output, outputSize): a mount being unmounted
    /// 0x80010013; the driver's devctl (VFS op 0x2C).</summary>
    [Hle(Nid = 0xB07B307D, Library = "SceIofilemgrForDriver")]
    int ksceVfsOpDevctl(uint mount, uint device, uint command, uint input, uint inputSize, uint output, uint outputSize)
    {
        if (mount != 0 && GuestMemory.R32(mount + 0x48) == MountStateUnmounting)
        {
            return VFS_ERR_UNMOUNTING;
        }

        return VfsOpCall(mount, 0x2C, [mount, device, command, input, inputSize, output, outputSize]);
    }

    /// <summary>ksceVfsOpDecodePathElem(mount, path, path2, path3, buffer, size, decoded): the driver's path element
    /// decoder (VFS op 0x30).</summary>
    [Hle(Nid = 0xF7DAC0F5, Library = "SceIofilemgrForDriver")]
    int ksceVfsOpDecodePathElem(uint mount, uint path, uint second, uint third, uint buffer, uint size, uint decoded)
    {
        return VfsOpCall(mount, 0x30, [mount, path, second, third, buffer, size, decoded]);
    }
}
