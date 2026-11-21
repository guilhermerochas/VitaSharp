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
        uint fsType = GuestMemory.R32(param + 8) & 0xFF;
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
            3 => GuestMemory.R32(driver + 0x10) == 0x10,
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
        GuestMemory.W32(mount + 0x4C, fsType);
        GuestMemory.W32(mount + 0x50, flags & 0xFFFFF);
        GuestMemory.W32(mount + 0x48, 0x100 | 1);
        GuestMemory.W32(mount + 0xCC, extra);

        uint vpp = Emu.Memory.Alloc(0x10, "vfs mount root", MemoryRegion.Kernel, 16);
        int result = vfsGetNewVnode(mount, vops != 0 ? vops : GuestMemory.R32(driver + 0x14), 0, vpp);
        uint root = GuestMemory.R32(vpp);
        Emu.Memory.Free(vpp);
        if (result < 0)
        {
            FreeMount(mount, driver);
            return result;
        }

        GuestMemory.W32(root + 0x78, VnodeTypeDirectory);
        GuestMemory.W32(root + 0x4C, mount);
        if (kind == 3)
        {
            GuestMemory.W32(mount + 0x40, root);
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
            vfsFreeVnode(root);
            FreeMount(mount, driver);
            return result;
        }

        if (result >= 0 && (flags & MountFlagNoDevctl) == 0 && kind != 3)
        {
            uint assign = misc == 0 ? 0 : GuestMemory.R32(misc);
            int devctl = ksceVfsOpDevctl(mount, assign, 0x3802, 0, 0, 0, 0);
            if (devctl < 0 && devctl != VFS_ERR_ACCEPTED_DEVCTL)
            {
                // the driver refused the device: its unmount op (+4) undoes the mount
                VfsOpCall(mount, 0x04, [mount, 0]);
                result = devctl;
            }
        }

        if (result < 0)
        {
            vfsFreeVnode(root);
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

    void FreeMount(uint mount, uint driver)
    {
        Emu.Memory.Free(GuestMemory.R32(mount + 0xCC));
        Emu.Memory.Free(mount);
        ReleaseVfsDriver(driver);
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

        GuestMemory.W32(mount.Mount + 0x48, MountStateUnmounting);
        int result = VfsOpCall(mount.Mount, 0x04, [mount.Mount, GuestMemory.R32(param + 4)]);
        if (result < 0)
        {
            GuestMemory.W32(mount.Mount + 0x48, 1);
            return result;
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

        vfsFreeVnode(mount.Root);
        FreeMount(mount.Mount, mount.Driver);
        return 0;
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
        if (_guestMounts.Count == 0)
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

        int walk = parentOnly ? elements.Count - 1 : elements.Count;
        for (int i = 0; i < walk; i++)
        {
            path.Element(elements[i].Start, elements[i].Length);
            GuestMemory.W32(path.VnodeOut, 0);
            int result = ksceVopLookup(vnode, path.VnodeOut, path.VfsPath, 0);
            if (result < 0)
            {
                return result;
            }

            uint next = GuestMemory.R32(path.VnodeOut);
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

    /// <summary>A file object for <paramref name="vnode"/> (vfsAllocateFile).</summary>
    uint GuestAllocateFile(uint vnode, in GuestPathBlock path)
    {
        return vfsAllocateFile(vnode, path.Scratch) < 0 ? 0 : GuestMemory.R32(path.Scratch);
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
            result = ksceVopLookup(directory, path.VnodeOut, path.VfsPath, 0);
            if (result >= 0 && (flags & O_CREAT) != 0 && (flags & O_EXCL) != 0)
            {
                return SceError.IO_EEXIST;
            }

            if (result < 0 && (flags & O_CREAT) != 0)
            {
                GuestMemory.W32(path.VnodeOut, 0);
                result = ksceVopCreate(directory, path.VnodeOut, path.VfsPath, (uint)flags, (uint)mode);
            }

            if (result < 0)
            {
                return result;
            }

            vnode = GuestMemory.R32(path.VnodeOut);
        }

        uint file = GuestAllocateFile(vnode, path);
        if (file == 0)
        {
            return VFS_ERR_NOMEM;
        }

        result = ksceVopOpen(vnode, path.VfsPath, (uint)flags, file);
        if (result < 0)
        {
            vfsFreeFile(vnode, file);
            return result;
        }

        int fd = Interlocked.Increment(ref _nextFd);
        _files[fd] = new OpenFile { VitaPath = vitaPath, GuestVnode = vnode, GuestFile = file };
        NoteGuestDescriptor(fd, false, vnode, file);
        return fd;
    }

    int GuestClose(OpenFile file)
    {
        int result = ksceVopClose(file.GuestVnode, file.GuestFile);
        ForgetGuestDescriptor(file.GuestFile);
        vfsFreeFile(file.GuestVnode, file.GuestFile);
        return result;
    }

    int GuestTransfer(OpenFile file, uint offset, uint buffer, uint size)
    {
        uint count = Emu.Memory.Alloc(0x10, "vfs result", MemoryRegion.Kernel, 16);
        try
        {
            GuestMemory.W32(count, 0);
            int result = offset == 0x10
                ? ksceVopRead(file.GuestVnode, file.GuestFile, buffer, size, count)
                : ksceVopWrite(file.GuestVnode, file.GuestFile, buffer, size, count);
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

        uint file = GuestAllocateFile(vnode, path);
        if (file == 0)
        {
            return VFS_ERR_NOMEM;
        }

        result = ksceVopDopen(vnode, path.VfsPath, file);
        if (result < 0)
        {
            vfsFreeFile(vnode, file);
            return result;
        }

        int fd = Interlocked.Increment(ref _nextFd);
        _dirs[fd] = new OpenDir { Entries = [], GuestVnode = vnode, GuestFile = file };
        NoteGuestDescriptor(fd, true, vnode, file);
        return fd;
    }

    int GuestDclose(OpenDir directory)
    {
        int result = ksceVopDclose(directory.GuestVnode, directory.GuestFile);
        ForgetGuestDescriptor(directory.GuestFile);
        vfsFreeFile(directory.GuestVnode, directory.GuestFile);
        return result;
    }

    int GuestGetstat(GuestMount mount, string fullPath, uint stat)
    {
        using var path = new GuestPathBlock(this, fullPath);
        int result = GuestWalk(mount, path, fullPath, parentOnly: false, out uint vnode, out _);
        return result < 0 ? result : ksceVopGetstat(vnode, path.VfsPath, stat);
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
            return SceError.IO_EEXIST;
        }

        GuestMemory.W32(path.VnodeOut, 0);
        return ksceVopMkdir(directory, path.VnodeOut, path.VfsPath, (uint)mode);
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
            return VFS_ERR_BUSY;
        }

        GuestMemory.W32(path.VnodeOut, 0);
        result = ksceVopLookup(directory, path.VnodeOut, path.VfsPath, 0);
        if (result < 0)
        {
            return result;
        }

        uint target = GuestMemory.R32(path.VnodeOut);
        return directoryToo
            ? ksceVopRmdir(directory, target, path.VfsPath, 0)
            : ksceVopRemove(directory, target, path.VfsPath, 0);
    }
}
