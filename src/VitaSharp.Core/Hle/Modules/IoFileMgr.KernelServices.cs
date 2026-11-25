using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceIofilemgrForDriver's other kernel entries (os0:kd/iofilemgr.skprx 3.74, read in the module, study only): mount
// points by id, error and mount events, the debugger's fd lists, media types, the process default priority, and the
// helpers the PFS, SD storage and exFAT drivers call. They work on the same open files and priorities as the user
// calls. The emulator's devices are host folders mounted when the emulated unit starts (and the game card while its
// dump is served): a mount or unmount by id of a device it serves succeeds without changing that.
public sealed partial class IoFileMgr
{
    const int IO_KERNEL_ERR_BADF = unchecked((int)0x80010009);
    const int IO_KERNEL_ERR_NO_EVENT = unchecked((int)0x80028002);

    /// <summary>The media type the module answers when the security module cannot classify a path.</summary>
    const uint MediaTypeUnknown = 8;

    /// <summary>The error and mount event handlers registered (id to its callback).</summary>
    [SnapshotMissing] // states of builds before it: none registered
    readonly Dictionary<int, uint> _ioEvents = new();

    [SnapshotMissing]
    int _nextIoEvent;

    /// <summary>The media type of a path's device: 4 the memory card (ux0), 3 the game card, 2 the internal user and
    /// system storage (ur0, vs0), 1 the other system partitions; a device the unit does not serve 8.</summary>
    uint MediaTypeOf(string path)
    {
        int colon = path.IndexOf(':');
        if (colon <= 0 || !Emu.Vfs.IsMounted(path[..colon]))
        {
            return MediaTypeUnknown;
        }

        return path[..colon].ToLowerInvariant() switch
        {
            "ux0" => 4,
            "gro0" or "grw0" => 3,
            "ur0" or "vs0" => 2,
            _ => 1,
        };
    }

    /// <summary>ksceIoGetMediaType(pid, path, ?, type): the media type of the path's device (8 when unknown); 0.</summary>
    [Hle(Nid = 0x9C220246, Library = "SceIofilemgrForDriver")]
    int ksceIoGetMediaType(int pid, uint path, uint unused, uint type)
    {
        if (type == 0)
        {
            return VFS_ERR_INVAL;
        }

        GuestMemory.W32(type, path == 0 ? MediaTypeUnknown : MediaTypeOf(GuestMemory.ReadCString(path, 0x400)));
        return 0;
    }

    /// <summary>ksceIoMount(id, path, permission, ?, 0, 0): id 0 or the last two arguments set 0x80010016; the
    /// device the id names is served from its host folder: 0.</summary>
    [Hle(Nid = 0xD070BC48, Library = "SceIofilemgrForDriver")]
    int ksceIoMount(uint id, uint path, uint permission, uint unused, uint reserved0, uint reserved1)
    {
        if (id == 0 || reserved0 != 0 || reserved1 != 0)
        {
            return VFS_ERR_INVAL;
        }

        NoteMount(Emu, id, (int)permission);
        return 0;
    }

    /// <summary>ksceIoUmount(id, force, ?, ?): id 0 0x80010016; the device stays served: 0.</summary>
    [Hle(Nid = 0x20574100, Library = "SceIofilemgrForDriver")]
    int ksceIoUmount(uint id, uint force, uint first, uint second)
    {
        return id == 0 ? VFS_ERR_INVAL : 0;
    }

    /// <summary>ksceIoGetProcessDefaultPriorityForSystem(): the process's default I/O priority (the user call's).</summary>
    [Hle(Nid = 0xCE397158, Library = "SceIofilemgrForDriver", ReturnsValue = true)]
    int ksceIoGetProcessDefaultPriorityForSystem()
    {
        return sceIoGetProcessDefaultPriorityThroughEntry();
    }

    /// <summary>ksceIoSetProcessDefaultPriorityForSystem(priority): sets it (a negative priority 0x80020066).</summary>
    [Hle(Nid = 0xABE65071, Library = "SceIofilemgrForDriver")]
    int ksceIoSetProcessDefaultPriorityForSystem(int priority)
    {
        return sceIoSetProcessDefaultPriorityThroughEntry(priority);
    }

    /// <summary>ksceIoGetFileInfo(fd, pid, info, ?): NULL info 0x80010016, an fd not open 0x80010009; the file's
    /// information block (+0 its size, 64-bit).</summary>
    [Hle(Nid = 0xA1EF2648, Library = "SceIofilemgrForDriver")]
    int ksceIoGetFileInfo(int fd, int pid, uint info, uint unused)
    {
        if (info == 0)
        {
            return VFS_ERR_INVAL;
        }

        if (!_files.TryGetValue(fd, out OpenFile? file))
        {
            return _dirs.ContainsKey(fd) ? 0 : IO_KERNEL_ERR_BADF;
        }

        GuestMemory.Write(info, file.Stream?.Length ?? 0L);
        return 0;
    }

    /// <summary>The debugger's fd list: the open file and directory fds of the process, up to <paramref name="max"/>
    /// into <paramref name="list"/>; *count the number written. NULL count 0x80010016.</summary>
    int FdList(uint list, uint max, uint count)
    {
        if (count == 0)
        {
            return VFS_ERR_INVAL;
        }

        List<int> fds = [.. _files.Keys, .. _dirs.Keys];
        fds.Sort();
        uint written = 0;
        foreach (int fd in fds)
        {
            if (list == 0 || written >= max)
            {
                break;
            }

            GuestMemory.W32(list + written * 4, (uint)fd);
            written++;
        }

        GuestMemory.W32(count, written);
        return 0;
    }

    /// <summary>ksceIoGetGUIDFdListForDebugger(pid, list, max, count): the process's fds.</summary>
    [Hle(Nid = 0xDCF75F6D, Library = "SceIofilemgrForDriver")]
    int ksceIoGetGUIDFdListForDebugger(int pid, uint list, uint max, uint count)
    {
        return FdList(list, max, count);
    }

    /// <summary>ksceIoGetPUIDFdListForDebugger(pid, list, max, count): the same list by the process's own ids.</summary>
    [Hle(Nid = 0x0D493274, Library = "SceIofilemgrForDriver")]
    int ksceIoGetPUIDFdListForDebugger(int pid, uint list, uint max, uint count)
    {
        return FdList(list, max, count);
    }

    /// <summary>ksceIoGetRemoteKPLSData(pid, data): the I/O manager's per-process block of another process: an
    /// unknown process 0x80010002; NULL 0x80010016; the block's first word (0).</summary>
    [Hle(Nid = 0x44EDCE57, Library = "SceIofilemgrForDriver")]
    int ksceIoGetRemoteKPLSData(int pid, uint data)
    {
        if (data == 0)
        {
            return VFS_ERR_INVAL;
        }

        if (pid != 0 && pid != Emu.Pid && Emu.Vita.ByPid(pid) == null)
        {
            return VFS_ERR_NOENT;
        }

        GuestMemory.W32(data, 0);
        return 0;
    }

    int AddIoEvent(uint callback)
    {
        if (callback == 0)
        {
            return VFS_ERR_INVAL;
        }

        lock (_ioEvents)
        {
            int id = ++_nextIoEvent;
            _ioEvents[id] = callback;
            return id;
        }
    }

    int RemoveIoEvent(int id)
    {
        lock (_ioEvents)
        {
            return _ioEvents.Remove(id) ? 0 : IO_KERNEL_ERR_NO_EVENT;
        }
    }

    /// <summary>ksceIoCreateErrorEvent(callback, arg, ?): an id for the handler of I/O errors (NULL 0x80010016).</summary>
    [Hle(Nid = 0x3C0343DB, Library = "SceIofilemgrForDriver", ReturnsValue = true)]
    int ksceIoCreateErrorEvent(uint callback, uint argument, uint flags)
    {
        return AddIoEvent(callback);
    }

    /// <summary>ksceIoDeleteErrorEvent(id): unknown 0x80028002.</summary>
    [Hle(Nid = 0xC6158F8D, Library = "SceIofilemgrForDriver")]
    int ksceIoDeleteErrorEvent(int id)
    {
        return RemoveIoEvent(id);
    }

    /// <summary>ksceIoClearErrorEvent(id): the handler's pending errors cleared: none; 0.</summary>
    [Hle(Nid = 0x40B933C7, Library = "SceIofilemgrForDriver")]
    int ksceIoClearErrorEvent(int id)
    {
        return 0;
    }

    /// <summary>ksceIoCreateMountEvent(callback, mask): an id for the handler of mounts and unmounts (NULL 0x80010016).</summary>
    [Hle(Nid = 0x2DFA192F, Library = "SceIofilemgrForDriver", ReturnsValue = true)]
    int ksceIoCreateMountEvent(uint callback, uint mask)
    {
        return AddIoEvent(callback);
    }

    /// <summary>ksceIoDeleteMountEvent(id): unknown 0x80028002.</summary>
    [Hle(Nid = 0x43DB0AE4, Library = "SceIofilemgrForDriver")]
    int ksceIoDeleteMountEvent(int id)
    {
        return RemoveIoEvent(id);
    }

    // ---- the helpers of the file system drivers

    /// <summary>NID_12F8D58E(): the caller's I/O manager process word (+8 of its block): 0.</summary>
    [Hle(Nid = 0x12F8D58E, Library = "SceIofilemgrForDriver", ReturnsValue = true)]
    int ksceIofilemgr_12F8D58E()
    {
        return 0;
    }

    /// <summary>NID_C722DF35(entry, ...): adds an entry to the PFS driver's list: 0.</summary>
    [Hle(Nid = 0xC722DF35, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_C722DF35(uint entry, uint first, uint second, uint third)
    {
        return 0;
    }

    /// <summary>NID_3675ECB9(entry, ...): removes an entry from it: 0.</summary>
    [Hle(Nid = 0x3675ECB9, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_3675ECB9(uint entry, uint first, uint second, uint third)
    {
        return 0;
    }

    /// <summary>NID_15C17487(entry, ...): looks an entry up in it: 0.</summary>
    [Hle(Nid = 0x15C17487, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_15C17487(uint entry, uint first, uint second, uint third)
    {
        return 0;
    }

    /// <summary>NID_9FCDCE62(priority, ...): the system variant of the thread default priority: as
    /// ksceIoSetThreadDefaultPriorityForSystem.</summary>
    [Hle(Nid = 0x9FCDCE62, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_9FCDCE62(int priority, uint first, uint second, uint third)
    {
        return sceIoSetThreadDefaultPriorityThroughEntry(priority);
    }

    /// <summary>NID_CCE94599(fd, priority, ...): the system variant of an fd's priority: as sceIoSetPriority.</summary>
    [Hle(Nid = 0xCCE94599, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_CCE94599(int fd, int priority, uint first, uint second)
    {
        if (!_files.TryGetValue(fd, out OpenFile? file))
        {
            return SceError.IO_EBADF;
        }

        if (priority < 0)
        {
            return SceError.ILLEGAL_PRIORITY;
        }

        file.Priority = priority;
        return 0;
    }

    /// <summary>NID_F4F8C59A(?): an empty entry of the module: 0.</summary>
    [Hle(Nid = 0xF4F8C59A, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_F4F8C59A(uint unused)
    {
        return 0;
    }

    /// <summary>NID_03F6A684(pid, ?): the I/O manager's per-process block of a process: none to change; 0.</summary>
    [Hle(Nid = 0x03F6A684, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_03F6A684(int pid, uint value)
    {
        return 0;
    }

    /// <summary>NID_AC0E9AAA(pid, ?): the same block's second word: 0.</summary>
    [Hle(Nid = 0xAC0E9AAA, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_AC0E9AAA(int pid, uint value)
    {
        return 0;
    }

    /// <summary>NID_2EFDFA12(?, ?): a module setting: 0.</summary>
    [Hle(Nid = 0x2EFDFA12, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_2EFDFA12(uint first, uint second)
    {
        return 0;
    }

    /// <summary>NID_39ABDB9E(device, flags, ...): the SD storage's device notice: NULL device 0x80010016; 0.</summary>
    [Hle(Nid = 0x39ABDB9E, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_39ABDB9E(uint device, uint flags, uint first, uint second)
    {
        return device == 0 ? VFS_ERR_INVAL : 0;
    }

    /// <summary>NID_3A79FAC9(param): NULL 0x80010016; 0.</summary>
    [Hle(Nid = 0x3A79FAC9, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_3A79FAC9(uint param)
    {
        return param == 0 ? VFS_ERR_INVAL : 0;
    }

    /// <summary>NID_6BEEDDB4(?, param): NULL param 0x80010016; 0.</summary>
    [Hle(Nid = 0x6BEEDDB4, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_6BEEDDB4(uint first, uint param)
    {
        return param == 0 ? VFS_ERR_INVAL : 0;
    }

    /// <summary>NID_8F0DE34D(?): a module object's release: 0.</summary>
    [Hle(Nid = 0x8F0DE34D, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_8F0DE34D(uint first)
    {
        return 0;
    }

    /// <summary>NID_B499287E(out, ?, out): NULLs 0x80010016; 0 with no object.</summary>
    [Hle(Nid = 0xB499287E, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_B499287E(uint first, uint second, uint third)
    {
        if (first == 0 || third == 0)
        {
            return VFS_ERR_INVAL;
        }

        GuestMemory.W32(first, 0);
        return 0;
    }

    /// <summary>NID_5B7E5AB8(): the SD storage's mount lookup: no guest mount (0x80010016).</summary>
    [Hle(Nid = 0x5B7E5AB8, Library = "SceIofilemgrForDriver", Default = true)]
    int ksceIofilemgr_5B7E5AB8()
    {
        return VFS_ERR_INVAL;
    }

    /// <summary>NID_A39A9CA7(path, ?, 0, 0): vfsRmdev of <paramref name="path"/> with flags 1 (iofilemgr 3.74
    /// 0x81019440 builds { path, 1 }); a third or fourth argument that is not 0 is 0x80010016.</summary>
    [Hle(Nid = 0xA39A9CA7, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_A39A9CA7(uint path, uint second, uint third, uint fourth)
    {
        if (third != 0 || fourth != 0)
        {
            return VFS_ERR_INVAL;
        }

        uint param = Emu.Memory.Alloc(8, "kernel:vfsRmdevParam", MemoryRegion.Kernel);
        try
        {
            GuestMemory.W32(param, path);
            GuestMemory.W32(param + 4, 1);
            return vfsRmdevThroughEntry(param);
        }
        finally
        {
            Emu.Memory.Free(param);
        }
    }

    /// <summary>NID_DD46CD63(mount, ?, ?): the exFAT driver's mount lookup: no guest mount (0x8001000C).</summary>
    [Hle(Nid = 0xDD46CD63, Library = "SceIofilemgrForDriver", Default = true)]
    int ksceIofilemgr_DD46CD63(uint mount, uint first, uint second)
    {
        return unchecked((int)0x8001000C);
    }
}
