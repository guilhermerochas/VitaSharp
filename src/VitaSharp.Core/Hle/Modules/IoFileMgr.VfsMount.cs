using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// Guest file systems mounted by kernel drivers (SceIofilemgrForDriver vfsMount 0xB62DE9A6 / vfsUmount 0x9C7E7B76 of
// os0:kd/iofilemgr.skprx 3.74, read in the decompiled module, study only), and the I/O calls on their paths.
//
// The mount request (SceVfsMountParam): +0 the root path ("/host"), +4 the block device path, +8 the file system
// type (low byte), +0xC the mount flags (low byte the kind: 1 over an existing path, fs type 3; 2 a file system root,
// fs type 1 or 2, its device from +4 or from the misc block's +8 / +0xC; 3 a device file system of a driver whose
// +0x10 is 0x10; 4 a no-op with a block device; 5 stacked on an existing mount, fs type 1 or 2, block device needed;
// 6 the root "/", once), +0x10 the driver's name, +0x14 its data, +0x18 the misc block (+0 the assign name "host0:"),
// +0x1C a vnode op table (NULL: the driver's default, its SceVfsInfo +0x14). A NULL request, root path or unknown
// driver is 0x80010016. The module builds the mount (the SceVfsMnt lock, +0x40 the covered vnode, +0x44 the default
// ops, +0x48 0x101 while being mounted, +0x4C the fs type, +0x50 the flags (20 bits), +0x5C the driver, +0x60 one
// reference, +0x7C the misc block, +0x80 the root path (0x40 bytes), +0xC4 the data), a root vnode (vfsGetNewVnode,
// +0x78 0x1002, +0x4C the mount), calls the driver's VFS mount op (+0, { mount, SceVfsPath *device }), then its
// set_root op (+8, { mount, 0, root }), then (without flag 0x1000) its devctl 0x3802 on the assign name, where
// 0x8001001C or success is accepted; it clears the "being mounted" bit and drops the temporary references.
//
// A path then reaches the mount by its assign name ("host0:a/b" becomes the root path plus "/a/b", 0x81004718) or
// by the root path itself; every element is looked up from the root vnode with the driver's lookup op (vop 0xC,
// { dvp, vpp, SceVfsPath { name, length, full path } , flags }) and the call ends in the driver's vnode op (open after
// a file object is allocated, create when O_CREAT finds nothing, read / write / lseek / close on the file, dopen /
// dread / dclose, getstat, mkdir, remove, rmdir). The emulator's own devices stay served from host folders: a guest
// driver's I/O on its block device ("sdstor0:", a file on ux0:) goes through the normal ksceIo* calls to them.
public sealed partial class IoFileMgr
{
    const uint MountSize = 0x100;
    const uint MountExtraSize = 0x10;
    const uint MountBeingMounted = 0x100;
    const uint MountFlagNoDevctl = 0x1000;
    const uint VnodeTypeDirectory = 0x1002;

    /// <summary>The type of a block device vnode (+0x78), the one a device file system's mount (kind 3) makes at its
    /// path and vfsRmdev removes.</summary>
    const uint VnodeTypeBlockDevice = 0x2000;
    const int VFS_ERR_ACCEPTED_DEVCTL = unchecked((int)0x8001001C);
    const int VFS_ERR_NOMEM = unchecked((int)0x8001000C);

    /// <summary>One guest file system mounted on a path.</summary>
    sealed class GuestMount
    {
        public uint Mount;
        public uint Root;
        public uint Driver;
        public required string RootPath;
        public string? AssignName;
    }

    /// <summary>The mounted guest file systems, in mount order (rebuilt from <see cref="_guestMountState"/> after a
    /// load: the root path and the assign name are read back from the mount block and its misc block).</summary>
    readonly List<GuestMount> _guestMounts = new();

    /// <summary>The saved form of the mounts: (mount block, root vnode) per mount.</summary>
    [SnapshotMissing] // states of builds before it: none mounted
    readonly List<uint> _guestMountState = new();

    /// <summary>The saved form of the descriptors open on guest mounts: (fd, 1 for a directory, vnode, file object).</summary>
    [SnapshotMissing] // states of builds before it: none open
    readonly List<uint> _guestDescriptorState = new();

    /// <summary>Rebuilds the mounts and marks the descriptors open on them, from the saved words and the guest memory
    /// the state carries.</summary>
    void AfterSnapshotLoad()
    {
        AfterAsyncLoad();
        AfterHostVnodeLoad();
        lock (_guestMounts)
        {
            _guestMounts.Clear();
            for (int i = 0; i + 1 < _guestMountState.Count; i += 2)
            {
                uint mount = _guestMountState[i];
                uint misc = GuestMemory.R32(mount + 0x7C);
                uint assign = misc == 0 ? 0 : GuestMemory.R32(misc);
                _guestMounts.Add(new GuestMount
                {
                    Mount = mount,
                    Root = _guestMountState[i + 1],
                    Driver = GuestMemory.R32(mount + 0x5C),
                    RootPath = GuestMemory.ReadCString(mount + 0x80, 0x40),
                    AssignName = assign == 0 ? null : GuestMemory.ReadCString(assign, 0x20),
                });
            }
        }

        lock (_guestDescriptorState)
        {
            for (int i = 0; i + 3 < _guestDescriptorState.Count; i += 4)
            {
                int fd = (int)_guestDescriptorState[i];
                if (_guestDescriptorState[i + 1] != 0)
                {
                    _dirs[fd] = new OpenDir { Entries = [], GuestVnode = _guestDescriptorState[i + 2], GuestFile = _guestDescriptorState[i + 3] };
                }
                else if (_files.TryGetValue(fd, out OpenFile? file))
                {
                    file.GuestVnode = _guestDescriptorState[i + 2];
                    file.GuestFile = _guestDescriptorState[i + 3];
                }
            }
        }
    }

    void NoteGuestDescriptor(int fd, bool directory, uint vnode, uint file)
    {
        lock (_guestDescriptorState)
        {
            _guestDescriptorState.AddRange([(uint)fd, directory ? 1u : 0u, vnode, file]);
        }
    }

    void ForgetGuestDescriptor(uint file)
    {
        lock (_guestDescriptorState)
        {
            for (int i = 0; i + 3 < _guestDescriptorState.Count; i += 4)
            {
                if (_guestDescriptorState[i + 3] == file)
                {
                    _guestDescriptorState.RemoveRange(i, 4);
                    return;
                }
            }
        }
    }

    /// <summary>The driver registered under <paramref name="name"/> (one more reference, +0xC, as 0x8100abf0 takes).</summary>
    uint FindVfsDriver(string name)
    {
        lock (_vfsDrivers)
        {
            foreach (uint driver in _vfsDrivers)
            {
                if (GuestMemory.ReadCString(GuestMemory.R32(driver + 4), 0x20) == name)
                {
                    GuestMemory.W32(driver + 0xC, GuestMemory.R32(driver + 0xC) + 1);
                    return driver;
                }
            }
        }

        return 0;
    }

    static void ReleaseVfsDriver(uint driver)
    {
        uint references = GuestMemory.R32(driver + 0xC);
        if (references > 0)
        {
            GuestMemory.W32(driver + 0xC, references - 1);
        }
    }

    /// <summary>A guest copy of a path and the SceVfsPath of its last element, freed with <see cref="Dispose"/>.</summary>
    readonly struct GuestPathBlock : IDisposable
    {
        public readonly uint Block;
        readonly IoFileMgr _owner;

        /// <summary>The full path (0x400 bytes at +0), the SceVfsPath (+0x400), a vnode pointer out (+0x40C).</summary>
        public GuestPathBlock(IoFileMgr owner, string fullPath)
        {
            _owner = owner;
            Block = owner.Emu.Memory.Alloc(0x1000, "vfs path", MemoryRegion.Kernel, 16);
            GuestMemory.Fill(Block, 0, 0x420);
            GuestMemory.WriteCString(Block, fullPath, 0x3FF);
        }

        public uint FullPath => Block;

        public uint VfsPath => Block + 0x400;

        public uint VnodeOut => Block + 0x40C;

        public uint Scratch => Block + 0x410;

        /// <summary>Points the SceVfsPath at the element at <paramref name="start"/> of the full path.</summary>
        public void Element(int start, int length)
        {
            GuestMemory.W32(VfsPath, Block + (uint)start);
            GuestMemory.W32(VfsPath + 4, (uint)length);
            GuestMemory.W32(VfsPath + 8, Block);
        }

        public void Dispose()
        {
            _owner.Emu.Memory.Free(Block);
        }
    }

    // ---- the mount and unmount requests

    /// <summary>vfsMount(param): mounts a registered driver on a path (see the file's header).</summary>
    [Hle(Nid = 0xB62DE9A6, Library = "SceIofilemgrForDriver")]
    int vfsMount(uint param)
    {
        if (param == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint rootPathAddress = GuestMemory.R32(param);
        uint device = GuestMemory.R32(param + 4);
        uint fsTypeWord = GuestMemory.R32(param + 8); // the low byte the type, +0x4E of the mount the device class
        uint fsType = fsTypeWord & 0xFF;
        uint flags = GuestMemory.R32(param + 0xC);
        uint nameAddress = GuestMemory.R32(param + 0x10);
        uint data = GuestMemory.R32(param + 0x14);
        uint misc = GuestMemory.R32(param + 0x18);
        uint vops = GuestMemory.R32(param + 0x1C);
        if (rootPathAddress == 0 || nameAddress == 0)
        {
            return VFS_ERR_INVAL;
        }

        uint driver = FindVfsDriver(GuestMemory.ReadCString(nameAddress, 0x20));
        if (driver == 0)
        {
            return VFS_ERR_INVAL;
        }

        string rootPath = NormalizeGuestRoot(GuestMemory.ReadCString(rootPathAddress, 0x40));
        uint kind = flags & 0xFF;
        bool accepted = kind switch
        {
            1 => fsType == 3,
            2 => fsType is 1 or 2,
            3 => GuestMemory.R32(driver + 0x10) == 0x10 && IsDeviceDirectory(GuestMemory.ReadCString(rootPathAddress, 0x40)),
            4 => device != 0,
            5 => fsType is 1 or 2 && device != 0,
            6 => fsType == 4,
            _ => false,
        };
        if (!accepted)
        {
            ReleaseVfsDriver(driver);
            return VFS_ERR_INVAL;
        }

        if (kind == 4)
        {
            ReleaseVfsDriver(driver);
            return 0;
        }

        if (kind == 6 || FindMountByRoot(rootPath) != null)
        {
            // the root is the emulator's own ("/" is always mounted); a path already covered by a mount is busy
            ReleaseVfsDriver(driver);
            return kind == 6 ? VFS_ERR_EXIST : VFS_ERR_BUSY;
        }

        uint covered = 0;
        if (kind == 1)
        {
            // a mount over an existing path: the vnode there is covered (+0x40); a device file system's cannot be
            int found = device == 0 ? VFS_ERR_INVAL : CoveredVnode(GuestMemory.ReadCString(device, 0x3FF), out covered);
            uint coveredMount = covered == 0 ? 0 : GuestMemory.R32(covered + 0x4C);
            if (found >= 0 && coveredMount != 0 && (GuestMemory.R32(coveredMount + 0x4C) & 0xFF) == 3)
            {
                ReleaseVnode(covered);
                found = VFS_ERR_BUSY;
            }

            if (found < 0)
            {
                ReleaseVfsDriver(driver);
                return found;
            }

            if (coveredMount != 0)
            {
                HoldMount(coveredMount);
            }
        }

        uint devicePathAddress = device;
        if (devicePathAddress == 0 && kind == 2 && misc != 0)
        {
            devicePathAddress = GuestMemory.R32(misc + 8) != 0 ? GuestMemory.R32(misc + 8) : GuestMemory.R32(misc + 0xC);
        }

        if (kind == 2 && devicePathAddress == 0)
        {
            ReleaseVfsDriver(driver);
            return VFS_ERR_INVAL;
        }

        uint mount = Emu.Memory.Alloc(MountSize, "vfs mount", MemoryRegion.Kernel, 16);
        uint extra = Emu.Memory.Alloc(MountExtraSize, "vfs mount extra", MemoryRegion.Kernel, 16);
        GuestMemory.Fill(mount, 0, MountSize);
        GuestMemory.Fill(extra, 0, MountExtraSize);
        GuestMemory.W32(mount + 0x5C, driver);
        GuestMemory.W32(mount + 0x60, 1);
        GuestMemory.W32(mount + 0x7C, misc);
        GuestMemory.WriteCString(mount + 0x80, rootPath, 0x3F);
        GuestMemory.W32(mount + 0xC4, data);
        GuestMemory.W32(mount + 0x4C, fsTypeWord); // the whole word, as 3.74 keeps it
        GuestMemory.W32(mount + 0x50, flags & 0xFFFFF);
        GuestMemory.W32(mount + 0x48, 0x100 | 1);
        GuestMemory.W32(mount + 0xCC, extra);
        InitMediaWait(mount, extra);

        uint vpp = Emu.Memory.Alloc(0x10, "vfs mount root", MemoryRegion.Kernel, 16);
        int result = vfsGetNewVnodeThroughEntry(mount, vops != 0 ? vops : GuestMemory.R32(driver + 0x14), 0, vpp);
        uint root = GuestMemory.R32(vpp);
        Emu.Memory.Free(vpp);
        if (result < 0)
        {
            ReleaseCovered(covered);
            FreeMount(mount, driver);
            return result;
        }

        // the root is a directory, active from the start (+0x74 1); over a covered vnode its parent is the covered
        // vnode's (+0x50). A device file system's (kind 3) is the block device vnode at its path (0x2000), which the
        // mount covers (+0x40) and whose ops are the mount's default ones (+0x6C)
        GuestMemory.W32(root + 0x78, kind == 3 ? VnodeTypeBlockDevice : VnodeTypeDirectory);
        GuestMemory.W32(root + 0x74, VnodeStateActive);
        GuestMemory.W32(root + 0x4C, mount);
        if (kind == 3)
        {
            GuestMemory.W32(mount + 0x40, root);
            GuestMemory.W32(root + 0x6C, GuestMemory.R32(mount + 0x44));
        }
        else if (kind == 1)
        {
            GuestMemory.W32(mount + 0x40, covered);
            GuestMemory.W32(root + 0x50, GuestMemory.R32(covered + 0x50));
        }

        string devicePath = devicePathAddress == 0 ? "" : GuestMemory.ReadCString(devicePathAddress, 0x3FF);
        using (var path = new GuestPathBlock(this, devicePath))
        {
            int nameStart = Math.Max(devicePath.LastIndexOf('/') + 1, devicePath.IndexOf(':') + 1);
            path.Element(nameStart, devicePath.Length - nameStart);
            result = VfsOpCall(mount, 0x00, [mount, devicePathAddress == 0 ? 0 : path.VfsPath]);
        }

        if (result == 0)
        {
            result = VfsOpCall(mount, 0x08, [mount, 0, root]);
        }
        else
        {
            vfsFreeVnodeThroughEntry(root);
            ReleaseCovered(covered);
            FreeMount(mount, driver);
            return result;
        }

        if (result >= 0 && (flags & MountFlagNoDevctl) == 0 && kind != 3)
        {
            uint assign = misc == 0 ? 0 : GuestMemory.R32(misc);
            int devctl = ksceVfsOpDevctlThroughEntry(mount, assign, 0x3802, 0, 0, 0, 0);
            if (devctl < 0 && devctl != VFS_ERR_ACCEPTED_DEVCTL)
            {
                // the driver refused the device: its unmount op (+4) undoes the mount
                VfsOpCall(mount, 0x04, [mount, 0]);
                result = devctl;
            }
        }

        if (result < 0)
        {
            vfsFreeVnodeThroughEntry(root);
            ReleaseCovered(covered);
            FreeMount(mount, driver);
            return result;
        }

        GuestMemory.W32(mount + 0x48, GuestMemory.R32(mount + 0x48) & ~MountBeingMounted);
        GuestMemory.W32(mount + 0x60, GuestMemory.R32(mount + 0x60) - 1);
        GuestMemory.W32(root + 0x58, Math.Max(GuestMemory.R32(root + 0x58), 1) - 1);
        string? assignName = misc == 0 || GuestMemory.R32(misc) == 0 ? null : GuestMemory.ReadCString(GuestMemory.R32(misc), 0x20);
        lock (_guestMounts)
        {
            _guestMounts.Add(new GuestMount { Mount = mount, Root = root, Driver = driver, RootPath = rootPath, AssignName = assignName });
            _guestMountState.AddRange([mount, root]);
        }

        Log.Info($"vfs: '{GuestMemory.ReadCString(nameAddress, 0x20)}' mounted on {rootPath}{(assignName == null ? "" : " (" + assignName + ")")}");
        return 0;
    }

    /// <summary>A covered vnode given back: its reference and its mount's.</summary>
    static void ReleaseCovered(uint covered)
    {
        if (covered != 0)
        {
            ReleaseOpenVnode(covered);
        }
    }

    void FreeMount(uint mount, uint driver)
    {
        Emu.Memory.Free(GuestMemory.R32(mount + 0xCC));
        Emu.Memory.Free(mount);
        ReleaseVfsDriver(driver);
    }

    /// <summary>The first element of a device file system's path is one of the device directories ("dev", "tty",
    /// "md", iofilemgr 3.74 0x8101ce94): a name the firmware fixes.</summary>
    static bool IsDeviceDirectory(string rootPath)
    {
        string first = rootPath.TrimStart('/').Split('/')[0];
        return first is "dev" or "tty" or "md";
    }

    /// <summary>
    /// vfsRmdev(param { path, flags }) as iofilemgr 3.74 has it (0x81007f14): the path ("sdstor0:gcd-lp-ign-entire", by
    /// an assign name or a root path) is walked to its parent and its last element looked up there; that vnode must be
    /// a block device's (type bit 0x2000; another vnode 0x80010001; nothing there 0x80010002, as with no game card in
    /// the slot). Only a plain block device vnode (type 0x2000 exactly) goes, and with flag 1 only when no file is open
    /// on it (+0x94; else 0x80010010): its files are synced (ksceVopSync), the parent's driver removes it
    /// (ksceVopRemove), it turns inactive (+0x74 0x100, so the files still open on it lose it) and, with no other
    /// reference, it is inactivated and freed. The block device vnode a device file system's mount (kind 3) put at its
    /// own path takes that mount away with it. 0.
    /// </summary>
    [Hle(Nid = 0x69A7E076, Library = "SceIofilemgrForDriver")]
    int vfsRmdev(uint param)
    {
        if (param == 0 || GuestMemory.R32(param) == 0)
        {
            return VFS_ERR_INVAL;
        }

        string path = GuestMemory.ReadCString(GuestMemory.R32(param), 0x400);
        uint flags = GuestMemory.R32(param + 4);
        if (ResolveGuestMount(path, out string fullPath) is not { } mount)
        {
            return VFS_ERR_NOENT;
        }

        using var block = new GuestPathBlock(this, fullPath);
        int result = GuestWalk(mount, block, fullPath, parentOnly: true, out uint parent, out int lastStart);
        if (result < 0)
        {
            return result;
        }

        if (lastStart < 0)
        {
            // the path is the mount's own: its root vnode (the walk's reference)
            return RemoveDeviceVnode(mount, 0, parent, block.VfsPath, flags);
        }

        GuestMemory.W32(block.VnodeOut, 0);
        result = ksceVopLookupThroughEntry(parent, block.VnodeOut, block.VfsPath, 0);
        uint vnode = result < 0 ? 0 : GuestMemory.R32(block.VnodeOut);
        if (result < 0 || vnode == 0)
        {
            ReleaseVnode(parent);
            return result < 0 ? result : VFS_ERR_NOENT;
        }

        result = RemoveDeviceVnode(mount, parent, vnode, block.VfsPath, flags);
        ReleaseVnode(parent);
        return result;
    }

    /// <summary>The checks and the removal of vfsRmdev on the vnode found (one reference held, given back here; the
    /// parent 0 for a mount's root).</summary>
    int RemoveDeviceVnode(GuestMount mount, uint parent, uint vnode, uint vfsPath, uint flags)
    {
        uint type = GuestMemory.R32(vnode + 0x78);
        if ((type & VnodeTypeBlockDevice) == 0 || type != VnodeTypeBlockDevice
            || ((flags & 1) != 0 && (int)GuestMemory.R32(vnode + 0x94) >= 1))
        {
            ReleaseVnode(vnode);
            return (type & VnodeTypeBlockDevice) == 0 ? VFS_ERR_PERM : VFS_ERR_BUSY;
        }

        if (parent == 0)
        {
            // the device vnode of a device file system's own mount: the mount goes with it
            ReleaseVnode(vnode);
            GuestMemory.W32(vnode + 0x74, VnodeActiveBelow);
            return RemoveGuestMount(mount, flags);
        }

        int result = ksceVopSyncThroughEntry(vnode, GuestMemory.R32(vnode + 0x90), 0);
        if (result >= 0)
        {
            result = ksceVopRemoveThroughEntry(parent, vnode, vfsPath, 0);
        }

        if (result < 0)
        {
            ReleaseVnode(vnode);
            return result;
        }

        GuestMemory.W32(vnode + 0x74, VnodeActiveBelow);
        if (GuestMemory.R32(vnode + 0x58) <= 1)
        {
            GuestMemory.W32(vnode + 0x58, 0);
            ksceVopInactiveThroughEntry(vnode);
            vfsFreeVnodeThroughEntry(vnode);
            return 0;
        }

        ReleaseVnode(vnode);
        return 0;
    }

    /// <summary>vfsUmount(param { path, flags }): the driver's unmount op (+4, { mount, flags }), then the mount goes
    /// away; a path with no guest mount 0x80010002.</summary>
    [Hle(Nid = 0x9C7E7B76, Library = "SceIofilemgrForDriver")]
    int vfsUmount(uint param)
    {
        if (param == 0 || GuestMemory.R32(param) == 0)
        {
            return VFS_ERR_INVAL;
        }

        string path = GuestMemory.ReadCString(GuestMemory.R32(param), 0x400);
        GuestMount? mount = FindMountByRoot(NormalizeGuestRoot(path)) ?? FindMountByAssign(path);
        if (mount == null)
        {
            return VFS_ERR_NOENT;
        }

        uint flags = GuestMemory.R32(param + 4);
        if (GuestMemory.R32(mount.Mount + 0x60) > 0 && (flags & 3) == 0)
        {
            // a descriptor still open on the mount holds a reference to it: only a forced unmount goes on
            return VFS_ERR_BUSY;
        }

        return RemoveGuestMount(mount, flags);
    }

    /// <summary>The end of an unmount: the driver's unmount op (+4, { mount, flags }); when it accepts, the covered
    /// vnode is given back and the mount goes away with its vnodes.</summary>
    int RemoveGuestMount(GuestMount mount, uint flags)
    {
        GuestMemory.W32(mount.Mount + 0x48, MountStateUnmounting);
        int result = VfsOpCall(mount.Mount, 0x04, [mount.Mount, flags]);
        if (result < 0)
        {
            GuestMemory.W32(mount.Mount + 0x48, 1);
            return result;
        }

        if ((GuestMemory.R32(mount.Mount + 0x50) & 0xFF) == 1)
        {
            ReleaseCovered(GuestMemory.R32(mount.Mount + 0x40));
        }

        lock (_guestMounts)
        {
            _guestMounts.Remove(mount);
            int index = _guestMountState.IndexOf(mount.Mount);
            if (index >= 0)
            {
                _guestMountState.RemoveRange(index, 2);
            }
        }

        if (FlushMountVnodes(mount))
        {
            FreeMount(mount.Mount, mount.Driver);
        }
        else
        {
            // vnodes still referenced (a forced unmount over open descriptors): the mount block stays for them
            ReleaseVfsDriver(mount.Driver);
        }

        return 0;
    }

    /// <summary>
    /// The end of an unmount (0x81006e90, then 0x81003bcc / 0x8100aa50 once the mount has no reference left): the root
    /// vnode, then every vnode still on the mount's list (+0x54) that no one references, gets the driver's inactive op
    /// (0x50) and is freed. False when some vnode is still referenced (it stays, and so does the mount block).
    /// </summary>
    bool FlushMountVnodes(GuestMount mount)
    {
        bool flushed = true;
        if (GuestMemory.R32(mount.Root + 0x58) == 0)
        {
            ksceVopInactiveThroughEntry(mount.Root);
            vfsFreeVnodeThroughEntry(mount.Root);
        }
        else
        {
            flushed = false;
        }

        uint vnode = GuestMemory.R32(mount.Mount + 0x54);
        while (vnode != 0)
        {
            uint next = GuestMemory.R32(vnode + 0x54);
            if (GuestMemory.R32(vnode + 0x58) == 0)
            {
                ksceVopInactiveThroughEntry(vnode);
                vfsFreeVnodeThroughEntry(vnode);
            }
            else
            {
                flushed = false;
            }

            vnode = next;
        }

        return flushed;
    }

    /// <summary>"/host/", "host" and "/host" name the same root.</summary>
    static string NormalizeGuestRoot(string path)
    {
        string trimmed = path.Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    GuestMount? FindMountByRoot(string rootPath)
    {
        lock (_guestMounts)
        {
            foreach (GuestMount mount in _guestMounts)
            {
                if (mount.RootPath == rootPath)
                {
                    return mount;
                }
            }
        }

        return null;
    }

    GuestMount? FindMountByBlock(uint block)
    {
        lock (_guestMounts)
        {
            foreach (GuestMount mount in _guestMounts)
            {
                if (mount.Mount == block)
                {
                    return mount;
                }
            }
        }

        return null;
    }

    GuestMount? FindMountByAssign(string path)
    {
        int colon = path.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        string device = path[..(colon + 1)];
        lock (_guestMounts)
        {
            foreach (GuestMount mount in _guestMounts)
            {
                if (mount.AssignName == device)
                {
                    return mount;
                }
            }
        }

        return null;
    }

    /// <summary>The guest mount a path is on and the path the driver sees (its root path plus the rest), or null when
    /// the path is the emulator's own. "host0:a/b" goes by the assign name, "/host/a/b" by the root path.</summary>
    GuestMount? ResolveGuestMount(string path, out string fullPath)
    {
        fullPath = "";
        if (_guestMounts.Count == 0 || t_inHostVop)
        {
            return null;
        }

        GuestMount? byAssign = FindMountByAssign(path);
        if (byAssign != null)
        {
            string rest = path[(path.IndexOf(':') + 1)..].TrimStart('/');
            fullPath = rest.Length == 0 ? byAssign.RootPath : byAssign.RootPath + "/" + rest;
            return byAssign;
        }

        if (!path.StartsWith('/'))
        {
            return null;
        }

        lock (_guestMounts)
        {
            foreach (GuestMount mount in _guestMounts)
            {
                if (path == mount.RootPath || path.StartsWith(mount.RootPath + "/", StringComparison.Ordinal))
                {
                    fullPath = path.TrimEnd('/');
                    return mount;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the elements of <paramref name="fullPath"/> past the mount's root path with the driver's lookup op,
    /// stopping before the last one when <paramref name="parentOnly"/>: the vnode reached (the root for the root path)
    /// and where the last element starts in the path (-1 for the root). A failed lookup's error.
    /// </summary>
    int GuestWalk(GuestMount mount, in GuestPathBlock path, string fullPath, bool parentOnly, out uint vnode, out int lastStart)
    {
        vnode = mount.Root;
        lastStart = -1;
        int position = mount.RootPath.Length;
        var elements = new List<(int Start, int Length)>();
        while (position < fullPath.Length)
        {
            while (position < fullPath.Length && fullPath[position] == '/')
            {
                position++;
            }

            int start = position;
            while (position < fullPath.Length && fullPath[position] != '/')
            {
                position++;
            }

            if (position > start)
            {
                elements.Add((start, position - start));
            }
        }

        // the walk holds a reference to the vnode it is at: the root's, then the one each lookup returns with
        // (the one before is dropped), and the vnode it ends at keeps it for the caller (0x81004bb8)
        HoldVnode(vnode);
        int walk = parentOnly ? elements.Count - 1 : elements.Count;
        for (int i = 0; i < walk; i++)
        {
            path.Element(elements[i].Start, elements[i].Length);
            GuestMemory.W32(path.VnodeOut, 0);
            int result = ksceVopLookupThroughEntry(vnode, path.VnodeOut, path.VfsPath, 0);
            uint next = result < 0 ? 0 : GuestMemory.R32(path.VnodeOut);
            ReleaseVnode(vnode);
            if (result < 0)
            {
                return result;
            }

            if (next == 0)
            {
                return VFS_ERR_NOENT;
            }

            vnode = next;
        }

        if (elements.Count > 0)
        {
            lastStart = elements[^1].Start;
            path.Element(elements[^1].Start, elements[^1].Length);
        }
        else
        {
            path.Element(0, fullPath.Length);
        }

        return 0;
    }

    /// <summary>The UID of each file object the I/O calls on guest mounts opened (the descriptor the module keeps).</summary>
    [SnapshotMissing] // states of builds before it: file objects were not UIDs
    readonly Dictionary<uint, int> _guestFileUids = new();

    /// <summary>A file object for <paramref name="vnode"/> (vfsAllocateFile), +0 1 for a directory and +4 the open
    /// flags, as the module's open and dopen set them before the driver's op.</summary>
    uint GuestAllocateFile(uint vnode, in GuestPathBlock path, bool directory, int flags)
    {
        int uid = vfsAllocateFileThroughEntry(vnode, path.Scratch, 0);
        if (uid < 0)
        {
            return 0;
        }

        uint file = GuestMemory.R32(path.Scratch);
        GuestMemory.W32(file, directory ? 1u : 0u);
        GuestMemory.W32(file + 4, (uint)flags);
        lock (_guestFileUids)
        {
            _guestFileUids[file] = uid;
        }

        return file;
    }

    /// <summary>vfsFreeFile on a file object <see cref="GuestAllocateFile"/> made.</summary>
    void GuestFreeFile(uint vnode, uint file)
    {
        int uid;
        lock (_guestFileUids)
        {
            if (!_guestFileUids.Remove(file, out uid))
            {
                return;
            }
        }

        vfsFreeFileThroughEntry(vnode, (uint)uid);
    }

    // ---- the I/O calls on a guest mount's paths and descriptors

    int GuestOpen(GuestMount mount, string vitaPath, string fullPath, int flags, int mode)
    {
        using var path = new GuestPathBlock(this, fullPath);
        int result = GuestWalk(mount, path, fullPath, parentOnly: true, out uint directory, out int lastStart);
        if (result < 0)
        {
            return result;
        }

        uint vnode = directory;
        if (lastStart >= 0)
        {
            GuestMemory.W32(path.VnodeOut, 0);
            result = ksceVopLookupThroughEntry(directory, path.VnodeOut, path.VfsPath, 0);
            if (result >= 0 && (flags & O_CREAT) != 0 && (flags & O_EXCL) != 0)
            {
                ReleaseVnode(GuestMemory.R32(path.VnodeOut));
                ReleaseVnode(directory);
                return SceError.IO_EEXIST;
            }

            if (result < 0 && (flags & O_CREAT) != 0)
            {
                GuestMemory.W32(path.VnodeOut, 0);
                result = ksceVopCreateThroughEntry(directory, path.VnodeOut, path.VfsPath, (uint)flags, (uint)mode);
            }

            ReleaseVnode(directory);
            if (result < 0)
            {
                return result;
            }

            vnode = GuestMemory.R32(path.VnodeOut);
        }

        uint file = GuestAllocateFile(vnode, path, false, flags);
        if (file == 0)
        {
            ReleaseVnode(vnode);
            return VFS_ERR_NOMEM;
        }

        result = ksceVopOpenThroughEntry(vnode, path.VfsPath, (uint)flags, file);
        if (result < 0)
        {
            GuestFreeFile(vnode, file);
            ReleaseVnode(vnode);
            return result;
        }

        // the open file keeps its vnode's reference and one of the mount (+0x60) until it is closed
        HoldMount(mount.Mount);
        int fd = Interlocked.Increment(ref _nextFd);
        _files[fd] = new OpenFile { VitaPath = vitaPath, GuestVnode = vnode, GuestFile = file };
        NoteGuestDescriptor(fd, false, vnode, file);
        return fd;
    }

    int GuestClose(OpenFile file)
    {
        int result = ksceVopCloseThroughEntry(file.GuestVnode, file.GuestFile);
        ForgetGuestDescriptor(file.GuestFile);
        GuestFreeFile(file.GuestVnode, file.GuestFile);
        ReleaseOpenVnode(file.GuestVnode);
        return result;
    }

    static void HoldMount(uint mount)
    {
        GuestMemory.W32(mount + 0x60, GuestMemory.R32(mount + 0x60) + 1);
    }

    /// <summary>A closed descriptor drops its vnode's reference and its mount's.</summary>
    static void ReleaseOpenVnode(uint vnode)
    {
        uint mount = GuestMemory.R32(vnode + 0x4C);
        ReleaseVnode(vnode);
        if (mount != 0 && GuestMemory.R32(mount + 0x60) > 0)
        {
            GuestMemory.W32(mount + 0x60, GuestMemory.R32(mount + 0x60) - 1);
        }
    }

    int GuestTransfer(OpenFile file, uint offset, uint buffer, uint size)
    {
        uint count = Emu.Memory.Alloc(0x10, "vfs result", MemoryRegion.Kernel, 16);
        try
        {
            GuestMemory.W32(count, 0);
            int result = offset == 0x10
                ? ksceVopReadThroughEntry(file.GuestVnode, file.GuestFile, buffer, size, count)
                : ksceVopWriteThroughEntry(file.GuestVnode, file.GuestFile, buffer, size, count);
            if (offset == 0x10)
            {
                DirtyPages.MarkRange(buffer, size);
            }

            return result < 0 ? result : (int)GuestMemory.R32(count);
        }
        finally
        {
            Emu.Memory.Free(count);
        }
    }

    int GuestDopen(GuestMount mount, string fullPath)
    {
        using var path = new GuestPathBlock(this, fullPath);
        int result = GuestWalk(mount, path, fullPath, parentOnly: false, out uint vnode, out _);
        if (result < 0)
        {
            return result;
        }

        uint file = GuestAllocateFile(vnode, path, true, 0);
        if (file == 0)
        {
            ReleaseVnode(vnode);
            return VFS_ERR_NOMEM;
        }

        result = ksceVopDopenThroughEntry(vnode, path.VfsPath, file);
        if (result < 0)
        {
            GuestFreeFile(vnode, file);
            ReleaseVnode(vnode);
            return result;
        }

        HoldMount(mount.Mount);
        int fd = Interlocked.Increment(ref _nextFd);
        _dirs[fd] = new OpenDir { Entries = [], GuestVnode = vnode, GuestFile = file };
        NoteGuestDescriptor(fd, true, vnode, file);
        return fd;
    }

    int GuestDclose(OpenDir directory)
    {
        int result = ksceVopDcloseThroughEntry(directory.GuestVnode, directory.GuestFile);
        ForgetGuestDescriptor(directory.GuestFile);
        GuestFreeFile(directory.GuestVnode, directory.GuestFile);
        ReleaseOpenVnode(directory.GuestVnode);
        return result;
    }

    int GuestGetstat(GuestMount mount, string fullPath, uint stat)
    {
        using var path = new GuestPathBlock(this, fullPath);
        int result = GuestWalk(mount, path, fullPath, parentOnly: false, out uint vnode, out _);
        if (result < 0)
        {
            return result;
        }

        result = ksceVopGetstatThroughEntry(vnode, path.VfsPath, stat);
        ReleaseVnode(vnode);
        return result;
    }

    int GuestMkdir(GuestMount mount, string fullPath, int mode)
    {
        using var path = new GuestPathBlock(this, fullPath);
        int result = GuestWalk(mount, path, fullPath, parentOnly: true, out uint directory, out int lastStart);
        if (result < 0)
        {
            return result;
        }

        if (lastStart < 0)
        {
            ReleaseVnode(directory);
            return SceError.IO_EEXIST;
        }

        GuestMemory.W32(path.VnodeOut, 0);
        result = ksceVopMkdirThroughEntry(directory, path.VnodeOut, path.VfsPath, (uint)mode);
        if (result >= 0)
        {
            ReleaseVnode(GuestMemory.R32(path.VnodeOut));
        }

        ReleaseVnode(directory);
        return result;
    }

    /// <summary>remove (vop 0x20) or rmdir (vop 0x28): the target looked up in its directory first.</summary>
    int GuestRemove(GuestMount mount, string fullPath, bool directoryToo)
    {
        using var path = new GuestPathBlock(this, fullPath);
        int result = GuestWalk(mount, path, fullPath, parentOnly: true, out uint directory, out int lastStart);
        if (result < 0)
        {
            return result;
        }

        if (lastStart < 0)
        {
            ReleaseVnode(directory);
            return VFS_ERR_BUSY;
        }

        GuestMemory.W32(path.VnodeOut, 0);
        result = ksceVopLookupThroughEntry(directory, path.VnodeOut, path.VfsPath, 0);
        if (result < 0)
        {
            ReleaseVnode(directory);
            return result;
        }

        uint target = GuestMemory.R32(path.VnodeOut);
        result = directoryToo
            ? ksceVopRmdirThroughEntry(directory, target, path.VfsPath, 0)
            : ksceVopRemoveThroughEntry(directory, target, path.VfsPath, 0);
        ReleaseVnode(target);
        ReleaseVnode(directory);
        return result;
    }
}
