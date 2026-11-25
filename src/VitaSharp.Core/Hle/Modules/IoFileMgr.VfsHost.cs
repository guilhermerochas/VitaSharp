using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// The emulator's own devices (ux0:, ur0:, ...) seen as vnodes, for a guest file system stacked over one of them. On the
// Vita a mount of kind 1 (fs type 3, vfsMount 0xB62DE9A6 of iofilemgr 3.74, read in the decompiled module, study only)
// walks the path in its second word (+4) to the vnode already there (0x81004718, 0x81004bb8, 0x81004340): its mount
// must not be a device file system (fs type 3: 0x80010010), it becomes the new mount's covered vnode (+0x40), and the
// new root vnode's parent (+0x50) is that vnode's parent. A stacked driver (PFS is one) then makes its vnodes over the
// covered ones: it calls their vops (lookup, open, read, write, lseek, close, getstat, dopen / dread / dclose, mkdir,
// remove, rmdir, create) with ksceVop*, and copies their type (+0x78), size (+0x80) and times.
//
// Here the devices are host folders, not exFAT vnodes: a path on them gets a vnode of the I/O manager's own (the pool's,
// state 1, type 0x1002 for a folder or 0x1001 for a file, +0x80 the size, +0x50 its folder's vnode) on a mount block of
// the device (+0x48 1, fs type 1, +0x7C a misc block with the assign name), whose op table is host code over the
// emulator's files: the I/O the stacked driver does through them is the emulator's normal I/O on that path. The
// position of an open file is the file object's (+8), as an exFAT vnode's is.
public sealed partial class IoFileMgr
{
    const uint VnodeTypeFile = 0x1001;
    const uint VnodeStateActive = 1;

    /// <summary>The host vnodes (vnode to the Vita path it stands for).</summary>
    [SnapshotMissing] // states of builds before it: none
    readonly Dictionary<uint, string> _hostVnodes = new();

    /// <summary>The host vnode of each Vita path (the reverse of <see cref="_hostVnodes"/>).</summary>
    [SnapshotMissing]
    readonly Dictionary<string, uint> _hostVnodeByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mount block of each device the host vnodes are on (its assign name to the block).</summary>
    [SnapshotMissing]
    readonly Dictionary<string, uint> _hostMounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The emulator descriptor behind each file object open on a host vnode.</summary>
    [SnapshotMissing]
    readonly Dictionary<uint, int> _hostVnodeFiles = new();

    /// <summary>Set while a host vnode op runs: its paths are the emulator's own, never a guest mount's (a guest
    /// file system stacked over the same device name would otherwise get the call back).</summary>
    [ThreadStatic]
    static bool t_inHostVop;

    /// <summary>The host functions behind the op table are this process's: a loaded state builds the table again
    /// (<see cref="AfterHostVnodeLoad"/>).</summary>
    [SnapshotIgnore]
    HleGuestStubs? _hostVopStubs;

    [SnapshotIgnore]
    uint _hostVops;

    /// <summary>After a load the host vnodes get this process's op table.</summary>
    void AfterHostVnodeLoad()
    {
        uint[] vnodes;
        lock (_hostVnodes)
        {
            vnodes = [.. _hostVnodes.Keys];
        }

        foreach (uint vnode in vnodes)
        {
            GuestMemory.W32(vnode + 0x40, HostVops());
        }
    }

    /// <summary>"ux0:a/b/" and "ux0:/a/b" name the same path "ux0:a/b".</summary>
    static string NormalizeHostPath(string path)
    {
        int colon = path.IndexOf(':');
        if (colon < 0)
        {
            return path.Trim('/');
        }

        string rest = path[(colon + 1)..].Trim('/');
        while (rest.Contains("//", StringComparison.Ordinal))
        {
            rest = rest.Replace("//", "/", StringComparison.Ordinal);
        }

        return path[..(colon + 1)] + rest;
    }

    /// <summary>The host vnode of <paramref name="vitaPath"/> with one more reference, made (with its folders') when
    /// it has none yet; 0 when the path names nothing.</summary>
    internal uint HostVnode(string vitaPath)
    {
        string path = NormalizeHostPath(vitaPath);
        int colon = path.IndexOf(':');
        if (colon < 0)
        {
            return 0;
        }

        lock (_hostVnodes)
        {
            if (_hostVnodeByPath.TryGetValue(path, out uint known))
            {
                HoldVnode(known);
                return known;
            }
        }

        string? host = Emu.Vfs.Resolve(path, false);
        bool directory = host != null && Directory.Exists(host);
        if (host == null || (!directory && !File.Exists(host)))
        {
            return 0;
        }

        uint parent = 0;
        int slash = path.LastIndexOf('/');
        if (slash > colon || path.Length > colon + 1)
        {
            parent = HostVnode(slash > colon ? path[..slash] : path[..(colon + 1)]);
        }

        uint mount = HostMount(path[..(colon + 1)]);
        uint vpp = Emu.Memory.Alloc(0x10, "vfs host vnode", MemoryRegion.Kernel, 16);
        int result = vfsGetNewVnodeThroughEntry(mount, HostVops(), 0, vpp);
        uint vnode = GuestMemory.R32(vpp);
        Emu.Memory.Free(vpp);
        // the parent (+0x50) is not held: a folder's vnode is never recycled
        ReleaseVnode(parent);
        if (result < 0)
        {
            return 0;
        }

        GuestMemory.W32(vnode + 0x50, parent);
        GuestMemory.W32(vnode + 0x74, VnodeStateActive);
        GuestMemory.W32(vnode + 0x78, directory ? VnodeTypeDirectory : VnodeTypeFile);
        GuestMemory.Write(vnode + 0x80, directory ? 0L : new FileInfo(host).Length);
        lock (_hostVnodes)
        {
            _hostVnodes[vnode] = path;
            _hostVnodeByPath[path] = vnode;
        }

        return vnode;
    }

    void ForgetHostVnode(uint vnode)
    {
        string? path;
        lock (_hostVnodes)
        {
            if (!_hostVnodes.Remove(vnode, out path))
            {
                return;
            }

            _hostVnodeByPath.Remove(path);
        }
    }

    /// <summary>The mount block of the device <paramref name="assign"/> ("ux0:").</summary>
    uint HostMount(string assign)
    {
        lock (_hostMounts)
        {
            if (_hostMounts.TryGetValue(assign, out uint known))
            {
                return known;
            }

            uint mount = NewHostMount(assign, 1, 0);
            _hostMounts[assign] = mount;
            return mount;
        }
    }

    /// <summary>A device's mount block: state 1, <paramref name="type"/> in +0x4C (the fs type), the driver's data in
    /// +0xC4, a misc block with the assign name (+0x7C), the assign name as its root path.</summary>
    uint NewHostMount(string assign, uint type, uint data)
    {
        uint mount = Emu.Memory.Alloc(MountSize, "vfs host mount", MemoryRegion.Kernel, 16);
        uint misc = Emu.Memory.Alloc(0x60, "vfs host mount misc", MemoryRegion.Kernel, 16);
        GuestMemory.Fill(mount, 0, MountSize);
        GuestMemory.Fill(misc, 0, 0x60);
        GuestMemory.WriteCString(misc + 0x20, assign, 0x1F);
        GuestMemory.W32(misc, misc + 0x20);
        GuestMemory.W32(mount + 0x48, 1);
        GuestMemory.W32(mount + 0x4C, type);
        GuestMemory.W32(mount + 0x7C, misc);
        GuestMemory.WriteCString(mount + 0x80, assign, 0x3F);
        GuestMemory.W32(mount + 0xC4, data);
        return mount;
    }

    /// <summary>The mount block of a PFS mount's device ("&lt;mount point&gt;0:"): fs type 3 and the mount's entry as
    /// its data, as the I/O manager makes it for pfsmgr's mount request; the device's vnodes are on it.</summary>
    void PfsDeviceMount(string point, uint entry)
    {
        string assign = PfsDevice(point) + ":";
        ForgetHostMount(assign);
        lock (_hostMounts)
        {
            _hostMounts[assign] = NewHostMount(assign, PfsFsType, entry);
        }
    }

    /// <summary>An unmounted device: its mount block and the vnodes of its paths are no longer the ones its paths
    /// get (a later mount of the same name makes new ones).</summary>
    void ForgetHostMount(string assign)
    {
        lock (_hostMounts)
        {
            _hostMounts.Remove(assign);
        }

        lock (_hostVnodes)
        {
            List<string>? stale = null;
            foreach (string path in _hostVnodeByPath.Keys)
            {
                if (path.StartsWith(assign, StringComparison.OrdinalIgnoreCase))
                {
                    stale ??= new List<string>();
                    stale.Add(path);
                }
            }

            if (stale == null)
            {
                return;
            }

            foreach (string path in stale)
            {
                if (_hostVnodeByPath.Remove(path, out uint vnode))
                {
                    _hostVnodes.Remove(vnode);
                }
            }
        }
    }

    string HostVnodePath(uint vnode)
    {
        lock (_hostVnodes)
        {
            return _hostVnodes.TryGetValue(vnode, out string? path) ? path : "";
        }
    }

    static string ElementOf(uint vfsPath)
    {
        return vfsPath == 0 ? "" : GuestMemory.ReadCString(GuestMemory.R32(vfsPath), (int)GuestMemory.R32(vfsPath + 4));
    }

    static string ChildPath(string directory, string name)
    {
        return directory.EndsWith(':') ? directory + name : directory + "/" + name;
    }

    /// <summary>The host vnodes' op table (built once): the I/O manager's own ops over the emulator's files.</summary>
    uint HostVops()
    {
        if (_hostVops != 0)
        {
            return _hostVops;
        }

        _hostVopStubs ??= new HleGuestStubs(this, "SceIofilemgrHostVnode");
        uint table = Emu.Memory.Alloc(0x80, "vfs host vops", MemoryRegion.Kernel, 16);
        GuestMemory.Fill(table, 0, 0x80);
        GuestMemory.W32(table + 0x00, HostOp("open", HostOpen));
        GuestMemory.W32(table + 0x04, HostOp("create", HostCreate));
        GuestMemory.W32(table + 0x08, HostOp("close", HostClose));
        GuestMemory.W32(table + 0x0C, HostOp("lookup", HostLookup));
        GuestMemory.W32(table + 0x10, HostOp("read", args => HostTransfer(args, true)));
        GuestMemory.W32(table + 0x14, HostOp("write", args => HostTransfer(args, false)));
        GuestMemory.W32(table + 0x18, _hostVopStubs.Host("lseek", context =>
        {
            bool outer = t_inHostVop;
            t_inHostVop = true;
            try
            {
                context.Return(HostLseek(context.Gpr(0)));
            }
            finally
            {
                t_inHostVop = outer;
            }
        }));
        GuestMemory.W32(table + 0x20, HostOp("remove", args => sceIoRemove(ChildPath(HostVnodePath(GuestMemory.R32(args)), ElementOf(GuestMemory.R32(args + 8))))));
        GuestMemory.W32(table + 0x24, HostOp("mkdir", HostMkdir));
        GuestMemory.W32(table + 0x28, HostOp("rmdir", args => sceIoRmdir(ChildPath(HostVnodePath(GuestMemory.R32(args)), ElementOf(GuestMemory.R32(args + 8))))));
        GuestMemory.W32(table + 0x2C, HostOp("dopen", HostDopen));
        GuestMemory.W32(table + 0x30, HostOp("dclose", args => HostCloseDescriptor(GuestMemory.R32(args + 4), true)));
        GuestMemory.W32(table + 0x34, HostOp("dread", args => HostDescriptor(GuestMemory.R32(args + 4)) is int fd ? sceIoDreadThroughEntry(fd, GuestMemory.R32(args + 8)) : VFS_ERR_BADF));
        GuestMemory.W32(table + 0x38, HostOp("getstat", args => sceIoGetstat(HostVnodePath(GuestMemory.R32(args)), GuestMemory.R32(args + 8))));
        GuestMemory.W32(table + 0x50, HostOp("inactive", _ => 0));
        GuestMemory.W32(table + 0x60, HostOp("fgetstat", args => sceIoGetstat(HostVnodePath(GuestMemory.R32(args)), GuestMemory.R32(args + 8))));
        _hostVops = table;
        return table;
    }

    uint HostOp(string name, Func<uint, int> body)
    {
        return _hostVopStubs!.Host(name, context =>
        {
            bool outer = t_inHostVop;
            t_inHostVop = true;
            try
            {
                context.Return(body(context.Gpr(0)));
            }
            finally
            {
                t_inHostVop = outer;
            }
        });
    }

    /// <summary>lookup { dvp, vpp, SceVfsPath, flags }: the host vnode of the element in the folder (one reference for
    /// the caller); nothing there 0x80010002.</summary>
    int HostLookup(uint args)
    {
        string path = ChildPath(HostVnodePath(GuestMemory.R32(args)), ElementOf(GuestMemory.R32(args + 8)));
        uint vnode = HostVnode(path);
        if (vnode == 0)
        {
            return VFS_ERR_NOENT;
        }

        GuestMemory.W32(GuestMemory.R32(args + 4), vnode);
        return 0;
    }

    /// <summary>create { dvp, vpp, SceVfsPath, flags, mode }: the file made in the folder, *vpp its vnode.</summary>
    int HostCreate(uint args)
    {
        string path = ChildPath(HostVnodePath(GuestMemory.R32(args)), ElementOf(GuestMemory.R32(args + 8)));
        int fd = sceIoOpen(path, (int)(GuestMemory.R32(args + 0xC) | O_CREAT | O_WRONLY), (int)GuestMemory.R32(args + 0x10));
        if (fd < 0)
        {
            return fd;
        }

        sceIoCloseThroughEntry(fd);
        uint vnode = HostVnode(path);
        if (vnode == 0)
        {
            return VFS_ERR_NOENT;
        }

        GuestMemory.W32(GuestMemory.R32(args + 4), vnode);
        return 0;
    }

    /// <summary>mkdir { dvp, vpp, SceVfsPath, mode }: the folder made, *vpp its vnode.</summary>
    int HostMkdir(uint args)
    {
        string path = ChildPath(HostVnodePath(GuestMemory.R32(args)), ElementOf(GuestMemory.R32(args + 8)));
        int result = sceIoMkdir(path, (int)GuestMemory.R32(args + 0xC));
        if (result < 0)
        {
            return result;
        }

        uint vpp = GuestMemory.R32(args + 4);
        if (vpp != 0)
        {
            GuestMemory.W32(vpp, HostVnode(path));
        }

        return 0;
    }

    /// <summary>open { vp, SceVfsPath, flags, file }: the vnode's file opened with the flags; the descriptor kept for
    /// the file object.</summary>
    int HostOpen(uint args)
    {
        uint vnode = GuestMemory.R32(args);
        uint flags = GuestMemory.R32(args + 8);
        int fd = sceIoOpen(HostVnodePath(vnode), (int)(flags & ~(uint)(O_CREAT | O_EXCL)), 0);
        if (fd < 0)
        {
            return fd;
        }

        if ((flags & O_TRUNC) != 0)
        {
            GuestMemory.Write(vnode + 0x80, 0L);
        }

        lock (_hostVnodeFiles)
        {
            _hostVnodeFiles[GuestMemory.R32(args + 0xC)] = fd;
        }

        return 0;
    }

    /// <summary>dopen { vp, SceVfsPath, file }: the folder's listing opened for the file object.</summary>
    int HostDopen(uint args)
    {
        int fd = sceIoDopen(HostVnodePath(GuestMemory.R32(args)));
        if (fd < 0)
        {
            return fd;
        }

        lock (_hostVnodeFiles)
        {
            _hostVnodeFiles[GuestMemory.R32(args + 8)] = fd;
        }

        return 0;
    }

    int? HostDescriptor(uint file)
    {
        lock (_hostVnodeFiles)
        {
            return _hostVnodeFiles.TryGetValue(file, out int fd) ? fd : null;
        }
    }

    int HostClose(uint args)
    {
        return HostCloseDescriptor(GuestMemory.R32(args + 4), false);
    }

    int HostCloseDescriptor(uint file, bool directory)
    {
        int fd;
        lock (_hostVnodeFiles)
        {
            if (!_hostVnodeFiles.Remove(file, out fd))
            {
                return VFS_ERR_BADF;
            }
        }

        return directory ? sceIoDcloseThroughEntry(fd) : sceIoCloseThroughEntry(fd);
    }

    /// <summary>read / write { vp, file, buffer, size }: at the file object's position (+8), which moves by the count;
    /// a write past the vnode's size grows it (+0x80).</summary>
    int HostTransfer(uint args, bool read)
    {
        uint vnode = GuestMemory.R32(args);
        uint file = GuestMemory.R32(args + 4);
        if (HostDescriptor(file) is not int fd)
        {
            return VFS_ERR_BADF;
        }

        long position = GuestMemory.Read<long>(file + 8);
        uint buffer = GuestMemory.R32(args + 8);
        uint size = GuestMemory.R32(args + 0xC);
        int count = read ? sceIoPreadThroughEntry(fd, buffer, size, position) : sceIoPwriteThroughEntry(fd, buffer, size, position);
        if (count > 0)
        {
            position += count;
            GuestMemory.Write(file + 8, position);
            if (!read && position > GuestMemory.Read<long>(vnode + 0x80))
            {
                GuestMemory.Write(vnode + 0x80, position);
            }
        }

        return count;
    }

    /// <summary>lseek { vp, file, offset (64-bit at +8), whence (+0x10) }: the file object's new position (from 0, its
    /// position or the vnode's size), returned as 64 bits.</summary>
    static long HostLseek(uint args)
    {
        uint vnode = GuestMemory.R32(args);
        uint file = GuestMemory.R32(args + 4);
        long offset = GuestMemory.Read<long>(args + 8);
        long origin = GuestMemory.R32(args + 0x10) switch
        {
            1 => GuestMemory.Read<long>(file + 8),
            2 => GuestMemory.Read<long>(vnode + 0x80),
            _ => 0,
        };
        long position = origin + offset;
        GuestMemory.Write(file + 8, position);
        return position;
    }

    /// <summary>
    /// The path walk of an open, a directory open or a stat on the emulator's own devices (ux0:, app0:, ...), as
    /// iofilemgr 3.74 makes it for every path (0x81004bb8, read in the decompiled module, study only): from the
    /// device's root vnode, one ksceVopLookup per element by the function's address, each returning the next vnode
    /// with a reference and dropping the one before. The emulator's files answer the same without the walk, so it is
    /// made only when a hook took ksceVopLookup (a kernel plugin's, which on the Vita sees every path walk of every
    /// open): the hook runs on the calling thread with the device's vnodes and the element, and may continue to the
    /// original. What it answers counts as it would on the Vita: an error fails the call with it (a "not found" only
    /// for a path the device has; a missing file is the call's own answer, so an open that creates still creates), and
    /// a vnode of these devices other than the one the path names sends the call to that vnode's path. Never inside a
    /// host vnode op (a stacked driver walked its own path to the vnode already). Returns 0 or the error.
    /// </summary>
    int HostWalk(ref string path)
    {
        if (t_inHostVop || !HleEntryCalls.IsTaken(this, s_hleEntry_ksceVopLookup))
        {
            return 0;
        }

        string normalized = NormalizeHostPath(path);
        int colon = normalized.IndexOf(':');
        if (colon <= 0)
        {
            return 0;
        }

        uint vnode = HostVnode(normalized[..(colon + 1)]);
        if (vnode == 0)
        {
            // a device the emulator does not have: the call itself answers
            return 0;
        }

        using var block = new GuestPathBlock(this, normalized);
        int position = colon + 1;
        while (position < normalized.Length)
        {
            int start = position;
            while (position < normalized.Length && normalized[position] != '/')
            {
                position++;
            }

            block.Element(start, position - start);
            GuestMemory.W32(block.VnodeOut, 0);
            int result = ksceVopLookupThroughEntry(vnode, block.VnodeOut, block.VfsPath, 0);
            uint next = result < 0 ? 0 : GuestMemory.R32(block.VnodeOut);
            ReleaseVnode(vnode);
            if (result < 0 || next == 0)
            {
                int error = result < 0 ? result : VFS_ERR_NOENT;
                bool present = HostPathExists(normalized[..position]);
                return error == VFS_ERR_NOENT && !present ? 0 : error;
            }

            vnode = next;
            position++;
        }

        string reached = HostVnodePath(vnode);
        ReleaseVnode(vnode);
        if (reached.Length > 0 && !reached.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            path = reached;
        }

        return 0;
    }

    /// <summary>Whether the emulator's devices have a file or folder at the Vita path.</summary>
    bool HostPathExists(string vitaPath)
    {
        string? host = Emu.Vfs.Resolve(vitaPath, false);
        return host != null && (File.Exists(host) || Directory.Exists(host));
    }

    /// <summary>The vnode at <paramref name="path"/> a kind 1 mount covers (one reference): on a guest mount the
    /// walk through its driver, on the emulator's devices the host vnode; 0x80010002 when nothing is there.</summary>
    int CoveredVnode(string path, out uint vnode)
    {
        vnode = 0;
        if (ResolveGuestMount(path, out string fullPath) is { } guest)
        {
            using var block = new GuestPathBlock(this, fullPath);
            // vfsMount's own lookup (the module's kind 0x6000) does not wait for the media
            return GuestWalk(guest, block, fullPath, parentOnly: false, out vnode, out _, waitForMedia: false);
        }

        vnode = HostVnode(path);
        return vnode == 0 ? VFS_ERR_NOENT : 0;
    }
}
