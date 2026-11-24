using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// ScePfsMgrForKernel (os0:bootimage ScePfsMgr 3.74) and ScePfsFacadeForKernel (iofilemgr.skprx 3.74; read in the
// modules, study only): the protected file system's mounts. pfsmgr keeps a table of mounts by mount point (a name of
// 1 to 30 bytes, else 0x80010016): Mount2(source, mount point, keys, type) adds one (a mount point already in use
// 0x80010011) and mounts the source's PFS image on it through the I/O manager; Approve/Disapprove set or clear its
// approval, Unmount removes it (unknown 0x80010013). The emulator installs content decrypted into host folders, so a
// PFS mount is the source folder served at the mount point (the device of the mount point maps to the source's host
// folder, as the console's PFS shows the image's plain files there). The ACID directory calls run an ioctl on the
// directory (it must exist: the I/O manager's error), the devctl calls on the mount's PFS device. The facade entries
// are the I/O manager's own stand-ins for an absent pfsmgr: each answers -1.
public sealed partial class IoFileMgr
{
    const int PFS_ERR_NOT_MOUNTED = unchecked((int)0x80010013);

    /// <summary>The PFS mounts: mount point to its source path.</summary>
    [SnapshotMissing] // states of builds before it: none
    readonly Dictionary<string, string> _pfsMounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The type each PFS mount was made with.</summary>
    [SnapshotMissing]
    readonly Dictionary<string, int> _pfsTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The approved PFS mounts.</summary>
    [SnapshotMissing]
    readonly HashSet<string> _pfsApproved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The launches mounting app0 through kscePfsMount's entry (<see cref="MountLaunchedApp0"/>), by the kernel
    /// thread making the call: the launch's own mount.</summary>
    [SnapshotIgnore]
    readonly Dictionary<GuestThread, Func<int>> _launchMounts = new();

    /// <summary>
    /// app0's mount at a launch as SceAppMgr 3.74 makes it (decompiled, study only): appmgr calls kscePfsMount of
    /// ScePfsMgrForKernel by its address on its own thread with the application's path, a mount point it names, the
    /// process's program authority ID and the title key. In VitaSharp the launch mounts the image itself, so the call is
    /// made through the entry only when kernel code hooked it: <paramref name="call"/> runs on a kernel thread, the hook
    /// first, and the original it continues to answers with the launch's own mount (<paramref name="mount"/>, the same
    /// as without a hook). Returns what the call returned.
    /// </summary>
    internal int MountLaunchedApp0(Func<int> mount, Func<int> call)
    {
        GuestThread thread = GuestThread.Current ?? throw new InvalidOperationException("the launch's app0 is mounted on a kernel thread");
        lock (_launchMounts)
        {
            _launchMounts[thread] = mount;
        }

        try
        {
            return call();
        }
        finally
        {
            lock (_launchMounts)
            {
                _launchMounts.Remove(thread);
            }
        }
    }

    /// <summary>The launch mount the calling thread is making through the entry, if it is making one.</summary>
    Func<int>? LaunchMountOfCaller()
    {
        if (GuestThread.Current is not { } thread)
        {
            return null;
        }

        lock (_launchMounts)
        {
            return _launchMounts.TryGetValue(thread, out Func<int>? mount) ? mount : null;
        }
    }

    /// <summary>A mount point the module takes: 1 to 30 bytes; the name, or null.</summary>
    static string? PfsMountPoint(uint mountPoint)
    {
        if (mountPoint == 0)
        {
            return null;
        }

        string text = GuestMemory.ReadCString(mountPoint, 0x20);
        return text.Length is >= 1 and <= 0x1E ? text : null;
    }

    /// <summary>kscePfsMount2(source, mountPoint, keys, type): NULL source or a bad mount point 0x80010016, a mount
    /// point in use 0x80010011, a source that does not exist 0x80010002; the source served at the mount point.</summary>
    [Hle(Nid = 0x2D48AEA2, Library = "ScePfsMgrForKernel")]
    int kscePfsMount2(uint source, uint mountPoint, uint keys, uint type)
    {
        if (source == 0 || PfsMountPoint(mountPoint) is not { } point)
        {
            return VFS_ERR_INVAL;
        }

        string sourcePath = GuestMemory.ReadCString(source, 0x400);
        string? host = Emu.Vfs.Resolve(sourcePath);
        if (host == null || !Directory.Exists(host))
        {
            return VFS_ERR_NOENT;
        }

        lock (_pfsMounts)
        {
            if (_pfsMounts.ContainsKey(point))
            {
                return VFS_ERR_EXIST;
            }

            _pfsMounts[point] = sourcePath;
            _pfsTypes[point] = (int)type;
        }

        string device = point.TrimEnd(':');
        if (!device.Contains('/') && !Emu.Vfs.IsMounted(device))
        {
            Emu.Vfs.Mount(device, host);
        }

        return 0;
    }

    /// <summary>kscePfsMount(source, mountPoint, keys, type, ?, size): with a 0x28-byte argument block, the type of an
    /// existing mount (unknown 0x80010013); otherwise Mount2 then Approve.</summary>
    [Hle(Nid = 0xA772209C, Library = "ScePfsMgrForKernel")]
    int kscePfsMount(uint source, uint mountPoint, uint keys, uint type, uint unused, uint size)
    {
        if (LaunchMountOfCaller() is { } launchMount)
        {
            // the system's launch mounting app0 through the entry: its own mount answers (the arguments are checked
            // as any mount's)
            return source == 0 || PfsMountPoint(mountPoint) == null ? VFS_ERR_INVAL : launchMount();
        }

        if (size == 0x28)
        {
            if (PfsMountPoint(mountPoint) is not { } point)
            {
                return VFS_ERR_INVAL;
            }

            lock (_pfsMounts)
            {
                return _pfsTypes.TryGetValue(point, out int mountType) ? mountType : PFS_ERR_NOT_MOUNTED;
            }
        }

        int result = kscePfsMount2ThroughEntry(source, mountPoint, keys, type);
        return result != 0 ? result : kscePfsApproveThroughEntry(mountPoint, keys, type, 0);
    }

    /// <summary>kscePfsUnmount(mountPoint): unknown 0x80010013; the mount point no longer served.</summary>
    [Hle(Nid = 0x680BC384, Library = "ScePfsMgrForKernel")]
    int kscePfsUnmount(uint mountPoint)
    {
        if (PfsMountPoint(mountPoint) is not { } point)
        {
            return VFS_ERR_INVAL;
        }

        lock (_pfsMounts)
        {
            if (!_pfsMounts.Remove(point))
            {
                return PFS_ERR_NOT_MOUNTED;
            }

            _pfsApproved.Remove(point);
            _pfsTypes.Remove(point);
        }

        string device = point.TrimEnd(':');
        if (!device.Contains('/'))
        {
            Emu.Vfs.Unmount(device);
        }

        return 0;
    }

    int PfsSetApproval(uint mountPoint, bool approved)
    {
        if (PfsMountPoint(mountPoint) is not { } point)
        {
            return VFS_ERR_INVAL;
        }

        lock (_pfsMounts)
        {
            if (!_pfsMounts.ContainsKey(point))
            {
                return PFS_ERR_NOT_MOUNTED;
            }

            if (approved)
            {
                _pfsApproved.Add(point);
            }
            else
            {
                _pfsApproved.Remove(point);
            }
        }

        return 0;
    }

    /// <summary>kscePfsApprove(mountPoint, keys, ?, ?): unknown 0x80010013; the mount approved.</summary>
    [Hle(Nid = 0xD8D0FEE5, Library = "ScePfsMgrForKernel")]
    int kscePfsApprove(uint mountPoint, uint keys, uint first, uint second)
    {
        return PfsSetApproval(mountPoint, true);
    }

    /// <summary>kscePfsDisapprove(mountPoint, keys, ?, ?): unknown 0x80010013; the approval cleared.</summary>
    [Hle(Nid = 0x2D67D8CA, Library = "ScePfsMgrForKernel")]
    int kscePfsDisapprove(uint mountPoint, uint keys, uint first, uint second)
    {
        return PfsSetApproval(mountPoint, false);
    }

    /// <summary>The ACID directory calls: NULL path 0x80010016; the directory opened and its ioctl run (a missing
    /// directory: the open's error); 0.</summary>
    int PfsAcidDirectory(uint path)
    {
        if (path == 0)
        {
            return VFS_ERR_INVAL;
        }

        int fd = sceIoDopen(GuestMemory.ReadCString(path, 0x400));
        if (fd < 0)
        {
            return fd;
        }

        sceIoDcloseThroughEntry(fd);
        return 0;
    }

    /// <summary>kscePfsAcidDirSet(path, titleId).</summary>
    [Hle(Nid = 0xB0CC0A1A, Library = "ScePfsMgrForKernel")]
    int kscePfsAcidDirSet(uint path, uint titleId)
    {
        return titleId == 0 ? VFS_ERR_INVAL : PfsAcidDirectory(path);
    }

    /// <summary>kscePfsAcidDirApprove(path, keys).</summary>
    [Hle(Nid = 0x8CDA249A, Library = "ScePfsMgrForKernel")]
    int kscePfsAcidDirApprove(uint path, uint keys)
    {
        return keys == 0 ? VFS_ERR_INVAL : PfsAcidDirectory(path);
    }

    /// <summary>kscePfsAcidDirMount(path, keys, ?).</summary>
    [Hle(Nid = 0x4A4A8243, Library = "ScePfsMgrForKernel")]
    int kscePfsAcidDirMount(uint path, uint keys, uint flags)
    {
        return keys == 0 ? VFS_ERR_INVAL : PfsAcidDirectory(path);
    }

    /// <summary>kscePfsAcidDirUnmount(path): the directory's ACID mount removed.</summary>
    [Hle(Nid = 0x00C8AF80, Library = "ScePfsMgrForKernel")]
    int kscePfsAcidDirUnmount(uint path)
    {
        return PfsAcidDirectory(path);
    }

    /// <summary>The devctl calls on a mount's PFS device: NULL mount point 0x80010016; 0.</summary>
    int PfsDevctl(uint mountPoint)
    {
        return mountPoint == 0 ? VFS_ERR_INVAL : 0;
    }

    /// <summary>NID_27FB7618(mountPoint, result, size): the mount's quota through its devctl.</summary>
    [Hle(Nid = 0x27FB7618, Library = "ScePfsMgrForKernel")]
    int kscePfs_27FB7618(uint mountPoint, uint result, uint size)
    {
        if (result != 0)
        {
            GuestMemory.W32(result, 0);
        }

        return PfsDevctl(mountPoint);
    }

    /// <summary>NID_7E4B76E6(mountPoint, value): a devctl setting of the mount.</summary>
    [Hle(Nid = 0x7E4B76E6, Library = "ScePfsMgrForKernel")]
    int kscePfs_7E4B76E6(uint mountPoint, uint value)
    {
        return PfsDevctl(mountPoint);
    }

    /// <summary>NID_806D2EA1(mountPoint, value): another devctl setting of the mount.</summary>
    [Hle(Nid = 0x806D2EA1, Library = "ScePfsMgrForKernel")]
    int kscePfs_806D2EA1(uint mountPoint, uint value)
    {
        return PfsDevctl(mountPoint);
    }

    /// <summary>NID_E9D672AB(mountPoint): the mount's sync through its devctl.</summary>
    [Hle(Nid = 0xE9D672AB, Library = "ScePfsMgrForKernel")]
    int kscePfs_E9D672AB(uint mountPoint)
    {
        return PfsDevctl(mountPoint);
    }

    /// <summary>NID_4C148288(mountPoint, state): NULLs 0x80010016; *state 1 when the mount is approved, 0 when it is
    /// not or not mounted; 0.</summary>
    [Hle(Nid = 0x4C148288, Library = "ScePfsMgrForKernel")]
    int kscePfs_4C148288(uint mountPoint, uint state)
    {
        if (state == 0 || PfsMountPoint(mountPoint) is not { } point)
        {
            return VFS_ERR_INVAL;
        }

        lock (_pfsMounts)
        {
            GuestMemory.W32(state, _pfsApproved.Contains(point) ? 1u : 0u);
        }

        return 0;
    }

    /// <summary>NID_04352D52: the I/O manager's stand-in for pfsmgr: -1.</summary>
    [Hle(Nid = 0x04352D52, Library = "ScePfsFacadeForKernel", ReturnsValue = true)]
    int kscePfsFacade_04352D52()
    {
        return -1;
    }

    /// <summary>NID_4238D2D2: the I/O manager's stand-in for pfsmgr: -1.</summary>
    [Hle(Nid = 0x4238D2D2, Library = "ScePfsFacadeForKernel", ReturnsValue = true)]
    int kscePfsFacade_4238D2D2()
    {
        return -1;
    }

    /// <summary>NID_58E643C5: the I/O manager's stand-in for pfsmgr: -1.</summary>
    [Hle(Nid = 0x58E643C5, Library = "ScePfsFacadeForKernel", ReturnsValue = true)]
    int kscePfsFacade_58E643C5()
    {
        return -1;
    }

    /// <summary>NID_7F9F4E49: the I/O manager's stand-in for pfsmgr: -1.</summary>
    [Hle(Nid = 0x7F9F4E49, Library = "ScePfsFacadeForKernel", ReturnsValue = true)]
    int kscePfsFacade_7F9F4E49()
    {
        return -1;
    }

    /// <summary>NID_BD5B21F6: the I/O manager's stand-in for pfsmgr: -1.</summary>
    [Hle(Nid = 0xBD5B21F6, Library = "ScePfsFacadeForKernel", ReturnsValue = true)]
    int kscePfsFacade_BD5B21F6()
    {
        return -1;
    }

    /// <summary>NID_CB622FFE: the I/O manager's stand-in for pfsmgr: -1.</summary>
    [Hle(Nid = 0xCB622FFE, Library = "ScePfsFacadeForKernel", ReturnsValue = true)]
    int kscePfsFacade_CB622FFE()
    {
        return -1;
    }
}
