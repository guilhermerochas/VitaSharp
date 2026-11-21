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
// drivers. A driver's registration, its vnodes, files and locks work, its operations run through ksceVop*, and a file
// system it mounts with vfsMount (or vfsMountForPFS) serves the I/O on its paths (IoFileMgr.VfsMount.cs); removing a
// block device answers 0x80010030 (the operation is not supported) after its argument check.
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
            // the mount's vnode list (0x8100bca8): +0x54 the newest vnode, +0x58 their number, vnode +0x54 the next
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

    /// <summary>vfsFreeVnode(vnode): takes it off its mount's vnode list (0x8100a9d8) and returns it; 0.</summary>
    [Hle(Nid = 0x21D57633, Library = "SceIofilemgrForDriver")]
    int vfsFreeVnode(uint vnode)
    {
        bool known;
        lock (_vfsObjects)
        {
            known = _vfsObjects.TryGetValue(vnode, out int kind) && kind == 1;
        }

        if (known)
        {
            UnlinkMountVnode(GuestMemory.R32(vnode + 0x4C), vnode);
        }

        return VfsFree(vnode, 1);
    }

    /// <summary>Takes <paramref name="vnode"/> off the vnode list of <paramref name="mount"/> (+0x54, its number at
    /// +0x58), as 0x8100a9d8 does.</summary>
    static void UnlinkMountVnode(uint mount, uint vnode)
    {
        if (mount == 0 || GuestMemory.R32(mount + 0x54) == 0)
        {
            return;
        }

        uint at = GuestMemory.R32(mount + 0x54);
        if (at == vnode)
        {
            GuestMemory.W32(mount + 0x54, GuestMemory.R32(vnode + 0x54));
        }
        else
        {
            while (at != 0 && GuestMemory.R32(at + 0x54) != vnode)
            {
                at = GuestMemory.R32(at + 0x54);
            }

            if (at == 0)
            {
                return;
            }

            GuestMemory.W32(at + 0x54, GuestMemory.R32(vnode + 0x54));
        }

        GuestMemory.W32(vnode + 0x54, 0);
        GuestMemory.W32(mount + 0x58, GuestMemory.R32(mount + 0x58) - 1);
    }

    /// <summary>One more reference to a vnode (+0x58).</summary>
    static void HoldVnode(uint vnode)
    {
        if (vnode != 0)
        {
            GuestMemory.W32(vnode + 0x58, GuestMemory.R32(vnode + 0x58) + 1);
        }
    }

    /// <summary>A reference dropped (+0x58; the I/O manager's `vnode->refs--` after a lookup's use). A vnode left
    /// with none stays on its mount's list, cached for the next lookup, until the unmount inactivates it.</summary>
    static void ReleaseVnode(uint vnode)
    {
        if (vnode == 0)
        {
            return;
        }

        uint references = GuestMemory.R32(vnode + 0x58);
        if (references > 0)
        {
            GuestMemory.W32(vnode + 0x58, references - 1);
        }
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

    /// <summary>The size of one vfsGetMntList entry.</summary>
    const uint MntListEntrySize = 0x34;

    /// <summary>
    /// vfsGetMntInfo(mount, info) as iofilemgr 3.74 has it (0x81009c18): a mount block that is not in the mount list
    /// is 0x80010002; otherwise the info block gets the mount's fields: +0 the mount, +4 the misc block's +0x10, +8 the
    /// assign name (0x20 bytes), +0x28 mount+0x64, +0x2C its state (+0x48), +0x30 its flags (+0x50), +0x34 its fs type
    /// (+0x4C), +0x38 the mount, +0x3C the covered vnode (+0x40), +0x40 the default ops (+0x44), +0x44 the references
    /// (+0x60), +0x48..+0x54 mount+0x58 / +0x54 / +0x70 / +0x74, +0x58 / +0x5C mount+0xC0 / its data (+0xC4), +0x60..+0x70
    /// the block at +0xC8 (its address, then its first four words), +0x74..+0x80 the block at +0xCC (its address, its
    /// +4 and what its +8 and +0xC point to), +0x84 the address of mount+0x7C, +0x88 the root path (0x400 bytes),
    /// +0x488 the misc block, and its strings at +4, +8 and +0xC (0x40 bytes each, empty when NULL). Only the guest
    /// mounts are in the list: the emulator's own devices are served from host folders, not by mount blocks.
    /// </summary>
    [Hle(Nid = 0xB987450D, Library = "SceIofilemgrForDriver")]
    int vfsGetMntInfo(uint mount, uint info)
    {
        GuestMount? found = FindMountByBlock(mount);
        if (found == null || info == 0)
        {
            return VFS_ERR_NOENT;
        }

        uint misc = GuestMemory.R32(mount + 0x7C);
        uint extra = GuestMemory.R32(mount + 0xCC);
        uint driverBlock = GuestMemory.R32(mount + 0xC8);
        GuestMemory.W32(info + 0x00, mount);
        GuestMemory.W32(info + 0x04, misc == 0 ? 0 : GuestMemory.R32(misc + 0x10));
        CopyGuestString(info + 0x08, misc == 0 ? 0 : GuestMemory.R32(misc), 0x20);
        GuestMemory.W32(info + 0x28, GuestMemory.R32(mount + 0x64));
        GuestMemory.W32(info + 0x2C, GuestMemory.R32(mount + 0x48));
        GuestMemory.W32(info + 0x30, GuestMemory.R32(mount + 0x50));
        GuestMemory.W32(info + 0x34, GuestMemory.R32(mount + 0x4C));
        GuestMemory.W32(info + 0x38, mount);
        GuestMemory.W32(info + 0x3C, GuestMemory.R32(mount + 0x40));
        GuestMemory.W32(info + 0x40, GuestMemory.R32(mount + 0x44));
        GuestMemory.W32(info + 0x44, GuestMemory.R32(mount + 0x60));
        GuestMemory.W32(info + 0x48, GuestMemory.R32(mount + 0x58));
        GuestMemory.W32(info + 0x4C, GuestMemory.R32(mount + 0x54));
        GuestMemory.W32(info + 0x50, GuestMemory.R32(mount + 0x70));
        GuestMemory.W32(info + 0x54, GuestMemory.R32(mount + 0x74));
        GuestMemory.W32(info + 0x58, GuestMemory.R32(mount + 0xC0));
        GuestMemory.W32(info + 0x5C, GuestMemory.R32(mount + 0xC4));
        GuestMemory.W32(info + 0x60, driverBlock);
        for (uint word = 0; word < 4; word++)
        {
            GuestMemory.W32(info + 0x64 + word * 4, driverBlock == 0 ? 0 : GuestMemory.R32(driverBlock + word * 4));
        }

        GuestMemory.W32(info + 0x74, extra);
        GuestMemory.W32(info + 0x78, extra == 0 ? 0 : GuestMemory.R32(extra + 4));
        GuestMemory.W32(info + 0x7C, DereferenceOrZero(extra == 0 ? 0 : GuestMemory.R32(extra + 8)));
        GuestMemory.W32(info + 0x80, DereferenceOrZero(extra == 0 ? 0 : GuestMemory.R32(extra + 0xC)));
        GuestMemory.W32(info + 0x84, mount + 0x7C);
        CopyGuestString(info + 0x88, mount + 0x80, 0x400);
        GuestMemory.W32(info + 0x488, misc);
        CopyGuestString(info + 0x48C, misc == 0 ? 0 : GuestMemory.R32(misc + 4), 0x40);
        CopyGuestString(info + 0x4CC, misc == 0 ? 0 : GuestMemory.R32(misc + 8), 0x40);
        CopyGuestString(info + 0x50C, misc == 0 ? 0 : GuestMemory.R32(misc + 0xC), 0x40);
        return 0;
    }

    /// <summary>
    /// vfsGetMntList(list, max, count out) as iofilemgr 3.74 has it (0x81009b6c): a NULL list returns the number of
    /// mounts; otherwise up to <paramref name="max"/> entries of 0x34 bytes, in the list's order ({ +0 the mount, +4 the
    /// misc block's +0x10 (-1 for a device file system, fs type 3), +8 the assign name (0x20 bytes), +0x28 mount+0x64,
    /// +0x2C 1 for a device file system, +0x30 the driver's data (+0xC4) }), the number written in *count, and 0.
    /// </summary>
    [Hle(Nid = 0xC20C621C, Library = "SceIofilemgrForDriver")]
    int vfsGetMntList(uint list, uint max, uint count)
    {
        uint[] mounts;
        lock (_guestMounts)
        {
            mounts = new uint[_guestMounts.Count];
            for (int i = 0; i < mounts.Length; i++)
            {
                mounts[i] = _guestMounts[i].Mount;
            }
        }

        if (list == 0)
        {
            return mounts.Length;
        }

        int written = 0;
        if ((int)max > 0)
        {
            while (written < mounts.Length && written < (int)max)
            {
                uint mount = mounts[written];
                uint entry = list + (uint)written * MntListEntrySize;
                uint misc = GuestMemory.R32(mount + 0x7C);
                bool deviceFileSystem = (GuestMemory.R32(mount + 0x4C) & 0xFF) == 3;
                GuestMemory.W32(entry, mount);
                GuestMemory.W32(entry + 4, deviceFileSystem ? 0xFFFFFFFF : misc == 0 ? 0 : GuestMemory.R32(misc + 0x10));
                CopyGuestString(entry + 8, misc == 0 ? 0 : GuestMemory.R32(misc), 0x20);
                GuestMemory.W32(entry + 0x28, GuestMemory.R32(mount + 0x64));
                GuestMemory.W32(entry + 0x2C, deviceFileSystem ? 1u : 0u);
                GuestMemory.W32(entry + 0x30, GuestMemory.R32(mount + 0xC4));
                written++;
            }
        }

        if (count != 0)
        {
            GuestMemory.W32(count, (uint)written);
        }

        return 0;
    }

    /// <summary>strncpy: the string at <paramref name="source"/> (NULL: empty) and zeros up to <paramref name="size"/>.</summary>
    static void CopyGuestString(uint destination, uint source, uint size)
    {
        string text = source == 0 ? "" : GuestMemory.ReadCString(source, (int)size);
        GuestMemory.Fill(destination, 0, size);
        GuestMemory.WriteCString(destination, text, (int)size);
    }

    static uint DereferenceOrZero(uint address)
    {
        return address == 0 ? 0 : GuestMemory.R32(address);
    }

    /// <summary>A mount request of a guest file system: NULL 0x80010016; not wired into the emulator's paths
    /// (0x80010030).</summary>
    static int VfsMountRequest(uint argument)
    {
        return argument == 0 ? VFS_ERR_INVAL : VFS_ERR_UNSUP;
    }

    /// <summary>
    /// vfsMountForPFS(param, 0, 0) as iofilemgr 3.74 has it (0x8101960c): a second or third argument that is not 0 is
    /// 0x80010016; otherwise the I/O manager's request thread (request kind 3, 0x810182e8) copies the SceVfsMountParam
    /// (8 words) and runs vfsMount on it in the caller's process, and its result is the call's. Here vfsMount runs on
    /// the calling thread (the request thread only serialises the mounts; the guest sees the same result).
    /// </summary>
    [Hle(Nid = 0xFEEE44A9, Library = "SceIofilemgrForDriver")]
    int vfsMountForPFS(uint param, uint second, uint third)
    {
        if (second != 0 || third != 0)
        {
            return VFS_ERR_INVAL;
        }

        return vfsMount(param);
    }

    /// <summary>vfsUmountForPFS(param { path, flags }, 0, 0): the same checks, then vfsUmount through the request
    /// thread (request kind 4, 0x810184b8).</summary>
    [Hle(Nid = 0xD220539D, Library = "SceIofilemgrForDriver")]
    int vfsUmountForPFS(uint param, uint second, uint third)
    {
        if (second != 0 || third != 0)
        {
            return VFS_ERR_INVAL;
        }

        return vfsUmount(param);
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

    /// <summary>
    /// The read / write family (0x570388A5 and its neighbours): the driver's op gets { vnode, file, buffer, size[,
    /// offset] } and returns the byte count or an error; the wrapper writes that into *result and returns 0 or the
    /// error. A NULL vnode 0x80010016, a NULL op 0x80010030 (nothing written); a size of 0 writes 0 without the op.
    /// </summary>
    static int VopTransfer(uint vnode, uint offset, ReadOnlySpan<uint> words, uint size, uint result)
    {
        if (vnode == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint table = GuestMemory.R32(vnode + 0x40);
        if (table == 0 || GuestMemory.R32(table + offset) == 0)
        {
            return VFS_ERR_UNSUP;
        }

        if (size == 0)
        {
            if (result != 0)
            {
                GuestMemory.W32(result, 0);
            }

            return 0;
        }

        int count = VfsCallOp(table, offset, words);
        if (result != 0)
        {
            GuestMemory.W32(result, (uint)count);
        }

        return Math.Min(count, 0);
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
        return VopTransfer(vnode, 0x10, [vnode, file, buffer, size], size, result);
    }

    /// <summary>ksceVopWrite(vnode, file, buffer, size, result): op 0x14.</summary>
    [Hle(Nid = 0x9A68378D, Library = "SceIofilemgrForDriver")]
    int ksceVopWrite(uint vnode, uint file, uint buffer, uint size, uint result)
    {
        return VopTransfer(vnode, 0x14, [vnode, file, buffer, size], size, result);
    }

    /// <summary>
    /// ksceVopLseek(vnode, file, offset (64-bit, r2:r3), whence) as iofilemgr 3.74 has it (0x8100e938): the position
    /// it asks for (from 0, the file's position at +8 for SEEK_CUR, the vnode's size at +0x80 for SEEK_END) is checked
    /// first: another whence, a negative position or the largest one is 0x80010016; then op 0x18 with { vnode, file,
    /// offset (64-bit at +8), whence (+0x10) }, whose 64-bit result (r0:r1, the new position or an error) is the
    /// call's. A NULL op 0x80010030.
    /// </summary>
    [Hle(Nid = 0xB2B13818, Library = "SceIofilemgrForDriver")]
    long ksceVopLseek(uint vnode, uint file, uint offsetLow, uint offsetHigh, uint whence)
    {
        if (vnode == 0)
        {
            return VFS_ERR_INVAL;
        }

        long offset = (long)(((ulong)offsetHigh << 32) | offsetLow);
        long origin;
        switch (whence)
        {
            case 0:
                origin = 0;
                break;
            case 1:
                origin = file == 0 ? 0 : GuestMemory.Read<long>(file + 8);
                break;
            case 2:
                origin = GuestMemory.Read<long>(vnode + 0x80);
                break;
            default:
                return VFS_ERR_INVAL;
        }

        long position = unchecked(origin + offset);
        if (position < 0 || position == long.MaxValue)
        {
            return VFS_ERR_INVAL;
        }

        uint table = GuestMemory.R32(vnode + 0x40);
        if (table == 0 || GuestMemory.R32(table + 0x18) == 0 || GuestThread.Current is not { } thread)
        {
            return VFS_ERR_UNSUP;
        }

        uint low = (uint)VfsCallOp(table, 0x18, [vnode, file, offsetLow, offsetHigh, whence]);
        return (long)(((ulong)thread.LastCallR1 << 32) | low);
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
        return VopTransfer(vnode, 0x48, [vnode, file, buffer, size, offsetLow, offsetHigh], size, result);
    }

    /// <summary>ksceVopPwrite(vnode, file, buffer, size, offset (64-bit), result): op 0x4C.</summary>
    [Hle(Nid = 0xA53C040D, Library = "SceIofilemgrForDriver")]
    int ksceVopPwrite(uint vnode, uint file, uint buffer, uint size, uint offsetLow, uint offsetHigh, uint result)
    {
        return VopTransfer(vnode, 0x4C, [vnode, file, buffer, size, offsetLow, offsetHigh], size, result);
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
