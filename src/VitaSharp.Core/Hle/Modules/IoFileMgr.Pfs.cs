using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// ScePfsMgrForKernel (os0:bootimage ScePfsMgr 3.74) and ScePfsFacadeForKernel (iofilemgr.skprx 3.74; read in the
// modules, study only): the protected file system's mounts. pfsmgr keeps a table of mounts by mount point (a name of
// 1 to 31 bytes, else 0x80010016): Mount2(source, mount point, key, mode) adds one (a mount point already in use
// 0x80010011) and mounts the source's PFS image on the device "<mount point>0:" through the I/O manager;
// Approve/Disapprove(mount point, program authority ID) add or remove an authority ID allowed to use it (up to 16),
// Unmount removes it (unknown 0x80010013); Mount(source, mount point, authority ID, key, mode) is Mount2 then Approve,
// or with mode 0x28 the mode of a mount the authority ID is approved on. The emulator installs content decrypted into
// host folders, so a PFS mount is the source folder served on the mount's device (it maps to the source's host
// folder, as the console's PFS shows the image's plain files there; the key is not needed). The ACID directory calls run an ioctl on the
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

    /// <summary>The approved PFS mounts: pfsmgr clears a mount's "not approved" flag at its first approval and never
    /// sets it again.</summary>
    [SnapshotMissing]
    readonly HashSet<string> _pfsApproved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program authority IDs approved on each PFS mount (pfsmgr's table of 16 per mount).</summary>
    [SnapshotMissing] // states of builds before it: no authority approved
    readonly Dictionary<string, List<ulong>> _pfsAuthorities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many program authority IDs pfsmgr approves on one mount.</summary>
    const int PfsAuthoritiesPerMount = 16;

    /// <summary>pfsmgr's "the table is full" (an approval past the 16th authority ID).</summary>
    const int PFS_ERR_AUTHORITIES_FULL = unchecked((int)0x8001001C);

    /// <summary>pfsmgr's error for a mode it does not mount.</summary>
    const int PFS_ERR_BAD_MODE = unchecked((int)0x80142109);

    /// <summary>kscePfsMount's mode that asks for an existing mount's mode instead of mounting.</summary>
    const uint PfsModeQuery = 0x28;

    /// <summary>The modes pfsmgr 3.74 mounts (below 0x18): the game data and save data pseudo drives (2, 3, 5, 6, 7, 9,
    /// 10, 13), the AC pseudo drives (4, 8, 12) and the redirect pseudo drives (0, 20 to 23); any other is
    /// 0x80142109.</summary>
    const uint PfsGameDataModes = 0x26EC, PfsAcModes = 0x1110, PfsRedirectModes = 0xF00001;

    /// <summary>The longest source path pfsmgr takes (62 bytes).</summary>
    const int PfsSourceMaxLength = 0x3E;

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

    /// <summary>A mount point the module takes: 1 to 31 bytes; the name, or null.</summary>
    static string? PfsMountPoint(uint mountPoint)
    {
        if (mountPoint == 0)
        {
            return null;
        }

        string text = GuestMemory.ReadCString(mountPoint, 0x20);
        return text.Length is >= 1 and <= 0x1F ? text : null;
    }

    /// <summary>The device pfsmgr mounts a mount point's image on: the name followed by "0" ("%s0:").</summary>
    static string PfsDevice(string point)
    {
        return point + "0";
    }

    /// <summary>pfsmgr's checks of a new mount's arguments (its mount entry's setup): a source of more than 62 bytes,
    /// mode 10 with a key, or modes 2, 3, 10 and 13 without a key on a retail unit 0x80010016; a mode it does not mount
    /// 0x80142109. 0 when the mount can be made.</summary>
    static int PfsCheckMount(string sourcePath, uint key, uint mode)
    {
        bool needsKeyOnRetail = mode is 2 or 3 or 10 or 13;
        if (sourcePath.Length > PfsSourceMaxLength || (mode == 10 && key != 0)
            || (needsKeyOnRetail && key == 0 && SystemServicesVsh.AimgrIsCex()))
        {
            return VFS_ERR_INVAL;
        }

        if (mode >= 0x18 || ((1u << (int)mode) & (PfsGameDataModes | PfsAcModes | PfsRedirectModes)) == 0)
        {
            return PFS_ERR_BAD_MODE;
        }

        return 0;
    }

    /// <summary>kscePfsMount2(source, mountPoint, key, mode) as pfsmgr 3.74 makes it: NULL source or a bad mount point
    /// 0x80010016, a mount point in use 0x80010011, then the mount's checks (<see cref="PfsCheckMount"/>), a source
    /// that does not exist 0x80010002; the source served on the device "mountPoint0:", not yet approved.</summary>
    [Hle(Nid = 0x2D48AEA2, Library = "ScePfsMgrForKernel")]
    int kscePfsMount2(uint source, uint mountPoint, uint key, uint mode)
    {
        if (source == 0 || PfsMountPoint(mountPoint) is not { } point)
        {
            return VFS_ERR_INVAL;
        }

        lock (_pfsMounts)
        {
            if (_pfsMounts.ContainsKey(point))
            {
                return VFS_ERR_EXIST;
            }
        }

        string sourcePath = GuestMemory.ReadCString(source, PfsSourceMaxLength + 1);
        int check = PfsCheckMount(sourcePath, key, mode);
        if (check != 0)
        {
            return check;
        }

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
            _pfsTypes[point] = (int)mode;
            _pfsAuthorities[point] = new List<ulong>();
        }

        string device = PfsDevice(point);
        if (!device.Contains('/') && !device.Contains(':') && !Emu.Vfs.IsMounted(device))
        {
            Emu.Vfs.Mount(device, host);
        }

        return 0;
    }

    /// <summary>
    /// kscePfsMount(source, mountPoint, program authority ID (two words), key, mode) as pfsmgr 3.74 makes it: mode 0x28
    /// asks for a mount's mode (a NULL or longer than 31 bytes mount point 0x80010016; a mount point not mounted, or
    /// one the authority ID is not approved on, 0x80010013); any other mode is Mount2(source, mountPoint, key, mode)
    /// then Approve(mountPoint, authority ID).
    /// </summary>
    [Hle(Nid = 0xA772209C, Library = "ScePfsMgrForKernel")]
    int kscePfsMount(uint source, uint mountPoint, uint authorityLow, uint authorityHigh, uint key, uint mode)
    {
        if (LaunchMountOfCaller() is { } launchMount)
        {
            // the system's launch mounting app0 through the entry: its own mount answers (the arguments are checked
            // as any mount's)
            if (source == 0 || PfsMountPoint(mountPoint) == null)
            {
                return VFS_ERR_INVAL;
            }

            int check = PfsCheckMount(GuestMemory.ReadCString(source, PfsSourceMaxLength + 1), key, mode);
            return check != 0 ? check : launchMount();
        }

        ulong authority = authorityLow | ((ulong)authorityHigh << 32);
        if (mode == PfsModeQuery)
        {
            if (mountPoint == 0)
            {
                return VFS_ERR_INVAL;
            }

            string point = GuestMemory.ReadCString(mountPoint, 0x20);
            if (point.Length > 0x1F)
            {
                return VFS_ERR_INVAL;
            }

            lock (_pfsMounts)
            {
                bool approved = _pfsAuthorities.TryGetValue(point, out List<ulong>? authorities) && authorities.Contains(authority);
                return approved && _pfsTypes.TryGetValue(point, out int mountMode) ? mountMode : PFS_ERR_NOT_MOUNTED;
            }
        }

        int result = kscePfsMount2ThroughEntry(source, mountPoint, key, mode);
        return result != 0 ? result : kscePfsApproveThroughEntry(mountPoint, 0, authorityLow, authorityHigh);
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
            _pfsAuthorities.Remove(point);
        }

        string device = PfsDevice(point);
        if (!device.Contains('/') && !device.Contains(':'))
        {
            Emu.Vfs.Unmount(device);
        }

        return 0;
    }

    /// <summary>
    /// kscePfsApprove(mountPoint, program authority ID) as pfsmgr 3.74 makes it (the ID is a 64-bit argument, in the
    /// third and fourth words): a bad mount point 0x80010016, unknown 0x80010013, an ID already approved 0x80010011, a
    /// 17th ID 0x8001001C; the ID added and the mount approved.
    /// </summary>
    [Hle(Nid = 0xD8D0FEE5, Library = "ScePfsMgrForKernel")]
    int kscePfsApprove(uint mountPoint, uint padding, uint authorityLow, uint authorityHigh)
    {
        if (PfsMountPoint(mountPoint) is not { } point)
        {
            return VFS_ERR_INVAL;
        }

        ulong authority = authorityLow | ((ulong)authorityHigh << 32);
        lock (_pfsMounts)
        {
            if (!_pfsMounts.ContainsKey(point))
            {
                return PFS_ERR_NOT_MOUNTED;
            }

            if (!_pfsAuthorities.TryGetValue(point, out List<ulong>? authorities))
            {
                authorities = new List<ulong>();
                _pfsAuthorities[point] = authorities;
            }

            if (authorities.Contains(authority))
            {
                return VFS_ERR_EXIST;
            }

            if (authorities.Count >= PfsAuthoritiesPerMount)
            {
                return PFS_ERR_AUTHORITIES_FULL;
            }

            authorities.Add(authority);
            _pfsApproved.Add(point);
        }

        return 0;
    }

    /// <summary>
    /// kscePfsDisapprove(mountPoint, program authority ID) as pfsmgr 3.74 makes it: a bad mount point 0x80010016,
    /// unknown 0x80010013, an ID not approved 0x80010002; the ID removed (the mount stays approved).
    /// </summary>
    [Hle(Nid = 0x2D67D8CA, Library = "ScePfsMgrForKernel")]
    int kscePfsDisapprove(uint mountPoint, uint padding, uint authorityLow, uint authorityHigh)
    {
        if (PfsMountPoint(mountPoint) is not { } point)
        {
            return VFS_ERR_INVAL;
        }

        ulong authority = authorityLow | ((ulong)authorityHigh << 32);
        lock (_pfsMounts)
        {
            if (!_pfsMounts.ContainsKey(point))
            {
                return PFS_ERR_NOT_MOUNTED;
            }

            if (!_pfsAuthorities.TryGetValue(point, out List<ulong>? authorities) || !authorities.Remove(authority))
            {
                return VFS_ERR_NOENT;
            }
        }

        return 0;
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
