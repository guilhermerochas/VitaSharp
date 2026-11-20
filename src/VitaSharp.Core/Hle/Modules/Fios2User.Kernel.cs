using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceFios2KernelForDriver (os0:bootimage SceFios2Kernel 3.74, read in the module, study only): the overlay table's
// kernel entries, called by the application manager (an application's overlays when it starts and ends) and by the
// I/O manager (every path it opens is resolved, and a merged overlay directory is read through a directory handle).
// They take kernel pointers directly (no option structure): a NULL output is 0x80820006. The table is the one the
// user calls (sceFiosOverlay*, the 02 syscalls) use: 600-byte entries sorted by order, at most 64 application
// overlays (order below 0x80) a process, ids counting up from 1. An overlay is checked as the module does: a type
// above 3 is 0x8082001A, a path without its terminator inside 292 bytes 0x80820018. An unknown id is 0x8082001A.
public sealed partial class Fios2User
{
    /// <summary>SceFios2Kernel: a NULL pointer argument (0x80820006).</summary>
    const int FIOS_ERR_NULL = unchecked((int)0x80820006);

    /// <summary>SceFios2Kernel: no overlay with that id, or an overlay type above 3 (0x8082001A).</summary>
    const int FIOS_ERR_NOT_FOUND = unchecked((int)0x8082001A);

    /// <summary>SceFios2Kernel: a path without its terminator inside the buffer (0x80820018).</summary>
    const int FIOS_ERR_PATH_TOO_LONG = unchecked((int)0x80820018);

    /// <summary>SceFios2Kernel: the process already has its 64 application overlays (0x80820019).</summary>
    const int FIOS_ERR_TABLE_FULL = unchecked((int)0x80820019);

    /// <summary>SceFios2Kernel: a resolve buffer above 0x400 bytes (0x80820008).</summary>
    const int FIOS_ERR_BUFFER_TOO_LARGE = unchecked((int)0x80820008);

    /// <summary>SceFios2Kernel: a resolve range whose lowest order is above its highest (0x8082001B).</summary>
    const int FIOS_ERR_BAD_RANGE = unchecked((int)0x8082001B);

    /// <summary>SceFios2Kernel: not an overlay directory handle (0x8082000C).</summary>
    const int FIOS_ERR_BAD_HANDLE = unchecked((int)0x8082000C);

    /// <summary>SceFios2Kernel: no free directory handle slot (64 a process) (0x80820003).</summary>
    const int FIOS_ERR_NO_HANDLE = unchecked((int)0x80820003);

    /// <summary>SceFios2Kernel: a directory handle without a backing directory (0x80820015).</summary>
    const int FIOS_ERR_NO_BACKING = unchecked((int)0x80820015);

    /// <summary>The application overlays (order below 0x80) a process may have.</summary>
    const int MaxApplicationOverlays = 64;

    /// <summary>The directory handles a process may hold.</summary>
    const int MaxDirectoryHandles = 64;

    /// <summary>The longest overlay path, terminator included (292 bytes).</summary>
    const int OverlayPathField = 0x124;

    /// <summary>The largest resolve buffer.</summary>
    const uint MaxResolveBuffer = 0x400;

    /// <summary>The stat of a directory handle with no directory behind it: zeroed, mode 0x1186 (a directory).</summary>
    const uint DirectoryHandleMode = 0x1186;

    /// <summary>The overlay directory handles open (handle to the I/O manager's directory fd).</summary>
    [SnapshotMissing] // states of builds before it: no handle open
    readonly Dictionary<int, int> _directoryHandles = new();

    [SnapshotMissing] // states of builds before it: no handle handed out
    int _nextDirectoryHandle;

    IoFileMgr? IoManager => Emu.Hle.Modules.OfType<IoFileMgr>().FirstOrDefault();

    /// <summary>The module's checks of an overlay given in: its type, and both paths terminated inside 292 bytes.</summary>
    static int CheckOverlay(uint overlay)
    {
        if (GuestMemory.Read<byte>(overlay) > 3)
        {
            return FIOS_ERR_NOT_FOUND;
        }

        if (!Terminated(overlay + 0x10, OverlayPathField) || !Terminated(overlay + 0x134, OverlayPathField))
        {
            return FIOS_ERR_PATH_TOO_LONG;
        }

        return 0;
    }

    /// <summary>The string at <paramref name="address"/> ends inside <paramref name="size"/> bytes.</summary>
    static bool Terminated(uint address, int size)
    {
        return GuestMemory.ReadSpan(address, size).IndexOf((byte)0) >= 0;
    }

    /// <summary>The kernel add: checks the overlay, refuses a 65th application overlay, inserts it by order.</summary>
    int KernelAddOverlay(uint overlay, uint outId)
    {
        GuestMemory.W32(outId, 0);
        int check = CheckOverlay(overlay);
        if (check != 0)
        {
            return check;
        }

        byte order = GuestMemory.Read<byte>(overlay + 1);
        lock (_overlays)
        {
            if (order < PrivilegedOrder && _overlays.Count(o => o.Order < PrivilegedOrder) >= MaxApplicationOverlays)
            {
                return FIOS_ERR_TABLE_FULL;
            }
        }

        return sceFiosOverlayAddForProcess02(0, overlay, new Ptr<int>(outId));
    }

    /// <summary>ksceFiosKernelOverlayAdd(overlay, outId): for the process the overlay names (+8); NULLs 0x80820006.</summary>
    [Hle(Nid = 0x2607EE4C, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayAdd(uint overlay, uint outId)
    {
        if (overlay == 0 || outId == 0)
        {
            return FIOS_ERR_NULL;
        }

        return KernelAddOverlay(overlay, outId);
    }

    /// <summary>ksceFiosKernelOverlayAddForProcess(pid, overlay, outId): NULLs 0x80820006.</summary>
    [Hle(Nid = 0x17E65A1C, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayAddForProcess(int pid, uint overlay, uint outId)
    {
        if (overlay == 0 || outId == 0)
        {
            return FIOS_ERR_NULL;
        }

        return KernelAddOverlay(overlay, outId);
    }

    /// <summary>ksceFiosKernelOverlayRemove(id): the caller's process; an unknown id 0x8082001A.</summary>
    [Hle(Nid = 0x2368FEB5, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayRemove(int id)
    {
        return ksceFiosKernelOverlayRemoveForProcess(0, id);
    }

    /// <summary>ksceFiosKernelOverlayRemoveForProcess(pid, id): an unknown id 0x8082001A.</summary>
    [Hle(Nid = 0x23247EFB, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayRemoveForProcess(int pid, int id)
    {
        return sceFiosOverlayRemoveForProcess02(pid, id);
    }

    /// <summary>ksceFiosKernelOverlayGetInfo(id, outOverlay): NULL 0x80820006, unknown 0x8082001A.</summary>
    [Hle(Nid = 0x725E6817, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayGetInfo(int id, uint outOverlay)
    {
        return ksceFiosKernelOverlayGetInfoForProcess(0, id, outOverlay);
    }

    /// <summary>ksceFiosKernelOverlayGetInfoForProcess(pid, id, outOverlay): NULL 0x80820006, unknown 0x8082001A.</summary>
    [Hle(Nid = 0xF1762BC2, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayGetInfoForProcess(int pid, int id, uint outOverlay)
    {
        if (outOverlay == 0)
        {
            return FIOS_ERR_NULL;
        }

        return sceFiosOverlayGetInfoForProcess02(pid, id, outOverlay);
    }

    /// <summary>ksceFiosKernelOverlayModify(id, overlay): NULL 0x80820006, the overlay's checks, unknown 0x8082001A.</summary>
    [Hle(Nid = 0x7F8B960C, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayModify(int id, uint overlay)
    {
        return ksceFiosKernelOverlayModifyForProcess(0, id, overlay);
    }

    /// <summary>ksceFiosKernelOverlayModifyForProcess(pid, id, overlay): as ksceFiosKernelOverlayModify.</summary>
    [Hle(Nid = 0x853EA82A, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayModifyForProcess(int pid, int id, uint overlay)
    {
        if (overlay == 0)
        {
            return FIOS_ERR_NULL;
        }

        int check = CheckOverlay(overlay);
        if (check != 0)
        {
            return check;
        }

        return sceFiosOverlayModifyForProcess02(pid, id, overlay);
    }

    /// <summary>ksceFiosKernelOverlayGetList(pid, minOrder, maxOrder, outIds, maxIds, actual): a count without a
    /// buffer 0x80820006; the buffer zeroed (maxIds words), the ids in order with minOrder &lt;= order &lt;= maxOrder,
    /// <paramref name="actual"/> (optional) the number of matches.</summary>
    [Hle(Nid = 0xFF42AAF0, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayGetList(int pid, uint minOrder, uint maxOrder, uint outIds, uint maxIds, uint actual)
    {
        if (maxIds != 0 && outIds == 0)
        {
            return FIOS_ERR_NULL;
        }

        if (outIds != 0)
        {
            GuestMemory.Fill(outIds, 0, maxIds * 4);
        }

        return sceFiosOverlayGetList02(pid, minOrder, maxOrder, outIds, maxIds, new Ptr<uint>(actual));
    }

    /// <summary>ksceFiosKernelOverlayGetRecommendedScheduler(count, path): 1 for a "hostN:" path with more than one
    /// scheduler, else 0.</summary>
    [Hle(Nid = 0x241BF0D6, Library = "SceFios2KernelForDriver", ReturnsValue = true)]
    int ksceFiosKernelOverlayGetRecommendedScheduler(int count, uint path)
    {
        if (path == 0)
        {
            return 0;
        }

        return sceFiosOverlayGetRecommendedScheduler02(count, GuestMemory.ReadCString(path, 16));
    }

    /// <summary>The kernel resolve: NULL in/out 0x80820006; a buffer above 0x400 0x80820008, zero or not holding the
    /// input path 0x80820018; minOrder above maxOrder 0x8082001B. With the calling thread's application overlays
    /// disabled only the privileged ones (order 0x80 and up) apply.</summary>
    int KernelResolve(uint inPath, uint outPath, uint maxPath, uint minOrder, uint maxOrder)
    {
        if (inPath == 0 || outPath == 0)
        {
            return FIOS_ERR_NULL;
        }

        if (maxPath > MaxResolveBuffer)
        {
            return FIOS_ERR_BUFFER_TOO_LARGE;
        }

        if (maxPath == 0 || !Terminated(inPath, (int)maxPath))
        {
            return FIOS_ERR_PATH_TOO_LONG;
        }

        if (ThreadOverlaysDisabled)
        {
            minOrder = PrivilegedOrder;
        }

        if (minOrder > maxOrder)
        {
            return FIOS_ERR_BAD_RANGE;
        }

        string resolved = Resolve(GuestMemory.ReadCString(inPath, (int)maxPath), minOrder, maxOrder);
        GuestMemory.WriteCString(outPath, resolved, (int)maxPath);
        return 0;
    }

    /// <summary>ksceFiosKernelOverlayResolveSync(pid, mode, inPath, outPath, maxPath): every order.</summary>
    [Hle(Nid = 0x0F456345, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayResolveSync(int pid, int mode, uint inPath, uint outPath, uint maxPath)
    {
        return KernelResolve(inPath, outPath, maxPath, 0, 0xFF);
    }

    /// <summary>ksceFiosKernelOverlayResolveWithRangeSync(pid, mode, inPath, outPath, maxPath, minOrder, maxOrder).</summary>
    [Hle(Nid = 0xD3D968FC, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayResolveWithRangeSync(int pid, int mode, uint inPath, uint outPath, uint maxPath, uint minOrder, uint maxOrder)
    {
        return KernelResolve(inPath, outPath, maxPath, minOrder & 0xFF, maxOrder & 0xFF);
    }

    /// <summary>ksceFiosKernelOverlayThreadIsDisabled(): the calling thread's flag (1/0).</summary>
    [Hle(Nid = 0xE71192C5, Library = "SceFios2KernelForDriver", ReturnsValue = true)]
    int ksceFiosKernelOverlayThreadIsDisabled()
    {
        return sceFiosOverlayThreadIsDisabled02();
    }

    /// <summary>ksceFiosKernelOverlayThreadSetDisabled(disabled): sets the calling thread's flag; 0.</summary>
    [Hle(Nid = 0x03727E5E, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayThreadSetDisabled(int disabled)
    {
        return sceFiosOverlayThreadSetDisabled02(disabled);
    }

    // ---- the overlay directory handles: the I/O manager opens a directory under overlays through them. A handle
    // stands for the resolved directory, read through the I/O manager.

    /// <summary>The directory fd behind <paramref name="handle"/>, or -1 when it is no handle.</summary>
    int DirectoryOf(int handle)
    {
        lock (_directoryHandles)
        {
            return _directoryHandles.TryGetValue(handle, out int fd) ? fd : -1;
        }
    }

    /// <summary>DH open (outHandle, path, fromOrder, args): NULLs 0x80820006; the path resolved and opened as a
    /// directory (its error when that fails); 64 handles a process (0x80820003).</summary>
    [Hle(Nid = 0x3B329E86, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayDHOpenSync(uint outHandle, uint path, uint fromOrder, uint args)
    {
        if (outHandle == 0 || path == 0)
        {
            return FIOS_ERR_NULL;
        }

        GuestMemory.W32(outHandle, 0);
        lock (_directoryHandles)
        {
            if (_directoryHandles.Count >= MaxDirectoryHandles)
            {
                return FIOS_ERR_NO_HANDLE;
            }
        }

        if (IoManager is not { } io)
        {
            return FIOS_ERR_NO_BACKING;
        }

        int fd = io.sceIoDopen(GuestMemory.ReadCString(path, (int)MaxResolveBuffer));
        if (fd < 0)
        {
            return fd;
        }

        int handle;
        lock (_directoryHandles)
        {
            handle = ++_nextDirectoryHandle;
            _directoryHandles[handle] = fd;
        }

        GuestMemory.W32(outHandle, (uint)handle);
        return 0;
    }

    /// <summary>DH read (handle, dirent): NULL dirent 0x80820006, not a handle 0x8082000C; the next entry as
    /// ksceIoDread (1, 0 at the end).</summary>
    [Hle(Nid = 0x28E28A58, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayDHReadSync(int handle, uint dirent)
    {
        if (dirent == 0)
        {
            return FIOS_ERR_NULL;
        }

        int fd = DirectoryOf(handle);
        if (fd < 0 || IoManager is not { } io)
        {
            return FIOS_ERR_BAD_HANDLE;
        }

        return io.sceIoDread(fd, dirent);
    }

    /// <summary>DH stat (handle, stat): NULL stat 0x80820006, not a handle 0x8082000C; the stat of a directory
    /// (zeroed, mode 0x1186).</summary>
    [Hle(Nid = 0x2649408B, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayDHStatSync(int handle, uint stat)
    {
        if (stat == 0)
        {
            return FIOS_ERR_NULL;
        }

        if (DirectoryOf(handle) < 0)
        {
            return FIOS_ERR_BAD_HANDLE;
        }

        GuestMemory.Fill(stat, 0, 0x58);
        GuestMemory.W32(stat, DirectoryHandleMode);
        return 0;
    }

    /// <summary>DH chstat (handle, stat, bits): NULL stat 0x80820006, not a handle 0x8082000C; a merged directory
    /// has no single directory to change (0x80820015).</summary>
    [Hle(Nid = 0x35E7E75C, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayDHChstatSync(int handle, uint stat, uint bits)
    {
        if (stat == 0)
        {
            return FIOS_ERR_NULL;
        }

        if (DirectoryOf(handle) < 0)
        {
            return FIOS_ERR_BAD_HANDLE;
        }

        return FIOS_ERR_NO_BACKING;
    }

    /// <summary>DH sync (handle, flag): not a handle 0x8082000C; no file behind a directory (0x80820015).</summary>
    [Hle(Nid = 0x990F46A5, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayDHSyncSync(int handle, int flag)
    {
        if (DirectoryOf(handle) < 0)
        {
            return FIOS_ERR_BAD_HANDLE;
        }

        return FIOS_ERR_NO_BACKING;
    }

    /// <summary>DH close (handle): closes the directory and frees the handle; not a handle 0x8082000C.</summary>
    [Hle(Nid = 0xDD7627EC, Library = "SceFios2KernelForDriver")]
    int ksceFiosKernelOverlayDHCloseSync(int handle)
    {
        int fd;
        lock (_directoryHandles)
        {
            if (!_directoryHandles.Remove(handle, out fd))
            {
                return FIOS_ERR_BAD_HANDLE;
            }
        }

        IoManager?.sceIoDclose(fd);
        return 0;
    }
}
