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
//
// Each mount is pfsmgr's mount entry in kernel memory, the vfs mount's data (+0xC4) as on the console: +0xC the source
// path (0x40 bytes), +0x4C the root path "/<mount point>", +0x6E the assign name "<mount point>0:", +0x92 the mode
// (16 bits), +0x94 its flags (1 no key, 4 not approved yet, 0x20 mode 3, 0x40 modes 7 and 9 and a mode 10 without a
// key), +0x96 the key (16 bytes), +0xAC the quota, +0xB0 the image's header value (0 here: no image), +0x170 the AC
// list (AC modes; its nodes' payload at +0xC), +0x178 the authority IDs (16 pairs of words, an empty one all ones, kept
// packed), +0x1F8 the mount's misc block (+0 the assign name) and +0x20C the table's size (16). The device's mount
// block (fs type 3, the storage type of the mount the source is on in +0x4E, the entry in +0xC4) is the one the
// device's vnodes have.
public sealed partial class IoFileMgr
{
    const int PFS_ERR_NOT_MOUNTED = unchecked((int)0x80010013);

    /// <summary>The PFS mounts: mount point to its source path.</summary>
    [SnapshotMissing] // states of builds before it: none
    readonly Dictionary<string, string> _pfsMounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mount entry of each PFS mount (in kernel memory).</summary>
    [SnapshotMissing] // states of builds before it: none
    readonly Dictionary<string, uint> _pfsEntries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fields of pfsmgr 3.74's mount entry (see the file's header).</summary>
    const uint PfsEntrySize = 0x230, PfsEntrySource = 0xC, PfsEntryRoot = 0x4C, PfsEntryAssign = 0x6E, PfsEntryMode = 0x92;
    const uint PfsEntryFlags = 0x94, PfsEntryKey = 0x96, PfsEntryQuota = 0xAC, PfsEntryImageValue = 0xB0;
    const uint PfsEntryAcList = 0x170, PfsEntryAuthorities = 0x178, PfsEntryMisc = 0x1F8, PfsEntryAuthorityCount = 0x20C;

    /// <summary>The entry's flags: no key, not approved yet, mode 3, modes 7 / 9 / 10 without a key.</summary>
    const ushort PfsFlagNoKey = 1, PfsFlagNotApproved = 4, PfsFlagMode3 = 0x20, PfsFlagPlain = 0x40;

    /// <summary>The fs type of a PFS mount (the request's +8, a mount of kind 1 over the source).</summary>
    const uint PfsFsType = 3;

    /// <summary>How many program authority IDs pfsmgr approves on one mount.</summary>
    const int PfsAuthoritiesPerMount = 16;

    /// <summary>The size of NID_4C148288's information block.</summary>
    internal const uint PfsMountInfoSize = 0x68;

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

            uint entry = NewPfsEntry(point, sourcePath, key, mode);
            _pfsMounts[point] = sourcePath;
            _pfsEntries[point] = entry;
            PfsDeviceMount(point, entry);
        }

        string device = PfsDevice(point);
        if (!device.Contains('/') && !device.Contains(':') && !Emu.Vfs.IsMounted(device))
        {
            Emu.Vfs.Mount(device, host);
        }

        return 0;
    }

    /// <summary>pfsmgr's new mount entry (0x81000b84 and Mount2's flag, see the file's header), "not approved".</summary>
    uint NewPfsEntry(string point, string sourcePath, uint key, uint mode)
    {
        uint entry = Emu.Memory.Alloc(PfsEntrySize, "pfs mount entry", MemoryRegion.Kernel, 16);
        GuestMemory.Fill(entry, 0, PfsEntrySize);
        GuestMemory.WriteCString(entry + PfsEntrySource, sourcePath, 0x40);
        GuestMemory.WriteCString(entry + PfsEntryRoot, "/" + point, 0x21);
        GuestMemory.WriteCString(entry + PfsEntryAssign, PfsDevice(point) + ":", 0x22);
        GuestMemory.Write(entry + PfsEntryMode, (ushort)mode);
        ushort flags = 0;
        if (key == 0)
        {
            flags |= PfsFlagNoKey;
        }
        else
        {
            GuestMemory.Copy(entry + PfsEntryKey, key, 0x10);
        }

        if (mode == 3)
        {
            flags |= PfsFlagMode3;
        }
        else if ((mode == 10 && flags == PfsFlagNoKey) || mode is 7 or 9)
        {
            flags |= PfsFlagPlain;
        }

        if ((mode & ~8u) == 4 || mode == 8)
        {
            // the AC pseudo drives' list, empty
            GuestMemory.W32(entry + PfsEntryAcList, entry + PfsEntryAcList);
            GuestMemory.W32(entry + PfsEntryAcList + 4, entry + PfsEntryAcList);
        }

        GuestMemory.Fill(entry + PfsEntryAuthorities, 0xFF, PfsAuthoritiesPerMount * 8);
        GuestMemory.W32(entry + PfsEntryMisc, entry + PfsEntryAssign);
        GuestMemory.W32(entry + PfsEntryAuthorityCount, PfsAuthoritiesPerMount);
        GuestMemory.Write(entry + PfsEntryFlags, (ushort)(flags | PfsFlagNotApproved));
        return entry;
    }

    /// <summary>The authority table's slot holding <paramref name="authority"/>, searched as pfsmgr 3.74 does
    /// (0x81000fec: up to the first empty slot, the table kept packed); -1 when it is not there.</summary>
    static int PfsAuthoritySlot(uint entry, ulong authority)
    {
        int count = (int)GuestMemory.R32(entry + PfsEntryAuthorityCount);
        for (int slot = 0; slot < count; slot++)
        {
            ulong value = GuestMemory.Read<ulong>(entry + PfsEntryAuthorities + (uint)slot * 8);
            if (value == authority)
            {
                return slot;
            }

            if (value == ulong.MaxValue)
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>The mount entry of a mount point (the caller holds the table's lock), or 0.</summary>
    uint PfsEntryOf(string point)
    {
        return _pfsEntries.TryGetValue(point, out uint entry) ? entry : 0;
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
                uint entry = PfsEntryOf(point);
                return entry != 0 && PfsAuthoritySlot(entry, authority) >= 0 ? GuestMemory.Read<ushort>(entry + PfsEntryMode) : PFS_ERR_NOT_MOUNTED;
            }
        }

        int result = kscePfsMount2ThroughEntry(source, mountPoint, key, mode);
        return result != 0 ? result : kscePfsApproveThroughEntry(mountPoint, 0, authorityLow, authorityHigh);
    }

    /// <summary>kscePfsUnmount(mountPoint): unknown 0x80010013; the mount point no longer served, its entry freed.</summary>
    [Hle(Nid = 0x680BC384, Library = "ScePfsMgrForKernel")]
    int kscePfsUnmount(uint mountPoint)
    {
        if (PfsMountPoint(mountPoint) is not { } point)
        {
            return VFS_ERR_INVAL;
        }

        uint entry;
        lock (_pfsMounts)
        {
            if (!_pfsMounts.Remove(point))
            {
                return PFS_ERR_NOT_MOUNTED;
            }

            _pfsEntries.Remove(point, out entry);
        }

        string device = PfsDevice(point);
        ForgetHostMount(device + ":");
        if (entry != 0)
        {
            Emu.Memory.Free(entry);
        }

        if (!device.Contains('/') && !device.Contains(':'))
        {
            Emu.Vfs.Unmount(device);
        }

        return 0;
    }

    /// <summary>
    /// kscePfsApprove(mountPoint, program authority ID) as pfsmgr 3.74 makes it (the ID is a 64-bit argument, in the
    /// third and fourth words; 0x81000df0): a bad mount point 0x80010016, unknown 0x80010013, an ID already approved
    /// 0x80010011, a 17th ID 0x8001001C; the ID in the first empty slot of the entry's table and the entry's "not
    /// approved" flag cleared.
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
            uint entry = PfsEntryOf(point);
            if (entry == 0)
            {
                return PFS_ERR_NOT_MOUNTED;
            }

            if (PfsAuthoritySlot(entry, authority) >= 0)
            {
                return VFS_ERR_EXIST;
            }

            int empty = PfsAuthoritySlot(entry, ulong.MaxValue);
            if (empty < 0)
            {
                return PFS_ERR_AUTHORITIES_FULL;
            }

            GuestMemory.Write(entry + PfsEntryAuthorities + (uint)empty * 8, authority);
            ushort flags = GuestMemory.Read<ushort>(entry + PfsEntryFlags);
            GuestMemory.Write(entry + PfsEntryFlags, (ushort)(flags & ~PfsFlagNotApproved));
        }

        return 0;
    }

    /// <summary>
    /// kscePfsDisapprove(mountPoint, program authority ID) as pfsmgr 3.74 makes it (0x81000ed0): a bad mount point
    /// 0x80010016, unknown 0x80010013, an ID not approved 0x80010002; the ID removed and the ones after it moved down
    /// one slot (the mount stays approved).
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
            uint entry = PfsEntryOf(point);
            if (entry == 0)
            {
                return PFS_ERR_NOT_MOUNTED;
            }

            int slot = PfsAuthoritySlot(entry, authority);
            if (slot < 0)
            {
                return VFS_ERR_NOENT;
            }

            uint table = entry + PfsEntryAuthorities;
            int count = (int)GuestMemory.R32(entry + PfsEntryAuthorityCount);
            for (int next = slot + 1; next < count; next++)
            {
                GuestMemory.Write(table + (uint)(next - 1) * 8, GuestMemory.Read<ulong>(table + (uint)next * 8));
            }

            GuestMemory.Write(table + (uint)(count - 1) * 8, ulong.MaxValue);
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

    /// <summary>
    /// NID_4C148288(mount, info) as pfsmgr 3.74 has it (0x81000000): the information of a vfs mount that is a PFS
    /// mount (fs type 3 in the mount's +0x4C byte, else 0x80010016, nothing written), from its entry (the mount's data,
    /// +0xC4), into a block of 0x68 bytes: +0 the mount, +4 the entry's source path, +8 its root path, +0xC its assign
    /// name, +0x10 the mode (16 bits), +0x14 the quota (+0xAC), +0x18 the image's header value (+0xB0), +0x1C the
    /// authority table's size (+0x20C), +0x20 the table (+0x178), +0x24 the number of AC list nodes and +0x28 their
    /// payloads (up to 16; zeros, and 0 nodes, for a mode that is not an AC one: 4, 8, 12); 0. A NULL mount is a data
    /// abort on the console: 0x80010016 here.
    /// </summary>
    [Hle(Nid = 0x4C148288, Library = "ScePfsMgrForKernel")]
    int kscePfs_4C148288(uint mount, uint info)
    {
        if (mount == 0 || GuestMemory.Read<byte>(mount + 0x4C) != PfsFsType)
        {
            return VFS_ERR_INVAL;
        }

        uint entry = GuestMemory.R32(mount + 0xC4);
        ushort mode = GuestMemory.Read<ushort>(entry + PfsEntryMode);
        GuestMemory.W32(info + 0x00, mount);
        GuestMemory.W32(info + 0x04, entry + PfsEntrySource);
        GuestMemory.W32(info + 0x08, entry + PfsEntryRoot);
        GuestMemory.W32(info + 0x0C, entry + PfsEntryAssign);
        GuestMemory.Write(info + 0x10, mode);
        GuestMemory.W32(info + 0x14, GuestMemory.R32(entry + PfsEntryQuota));
        GuestMemory.W32(info + 0x18, GuestMemory.R32(entry + PfsEntryImageValue));
        GuestMemory.W32(info + 0x1C, GuestMemory.R32(entry + PfsEntryAuthorityCount));
        GuestMemory.W32(info + 0x20, entry + PfsEntryAuthorities);
        GuestMemory.W32(info + 0x24, 0);
        GuestMemory.Fill(info + 0x28, 0, 0x40);
        if ((mode & 0xFFF7) != 4 && mode != 8)
        {
            return 0;
        }

        uint head = entry + PfsEntryAcList;
        uint count = 0;
        for (uint node = GuestMemory.R32(head); node != head && count < 0x10; node = GuestMemory.R32(node))
        {
            GuestMemory.W32(info + 0x28 + count * 4, node + 0xC);
            count++;
        }

        GuestMemory.W32(info + 0x24, count);
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
