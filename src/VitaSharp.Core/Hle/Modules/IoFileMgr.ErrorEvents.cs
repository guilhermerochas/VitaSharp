using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// I/O error events and media-change notices (os0:kd/iofilemgr.skprx 3.74, read in the decompiled module, study only).
// The module keeps a table of 32 mount points made at its start (FUN_8101888c): each with its id, the internal root path
// of its file system ("/ux/exfat") and its device ("ux0:"). ksceIoCreateErrorEvent (0x3C0343DB: mount id, type 1, a
// 0x1C-byte record of the caller's) makes a kernel object of the module's class "SceUIDIoErrorEventClass" (0x50
// bytes: an event flag) named "SceIoErrorEvent" plus the device without its colon, first letter upper case
// ("SceIoErrorEventUx0"), keeping the mount id (+0x38), the caller's process (+0x3C), the event mask (+0x40, 1), whether
// it was notified (+0x44) and the record's address (+0x48). A driver reporting an error on a mount calls NID_DD46CD63
// (mount id, mask, info): every such event of that mount whose mask meets the given one and that was not notified yet
// gets a 0x1C-byte record in its process ({ mount id, info +4, 0, 0, info +0x10, 0, 0 }), its flag set with the mask,
// and is marked notified until ksceIoClearErrorEvent (0x40B933C7) clears the mark; ksceIoDeleteErrorEvent (0xC6158F8D)
// deletes it. NID_5B7E5AB8 (mount id) marks the mount's media as changed: the mount found at the id's root path (a
// guest file system mounted there: vfsMount) gets its presence word (the extra block's +8 points at it) set to 1 and
// its wait flag (the extra block's +0xC points at its UID) set with bit 1. The emulator's own mount points are host folders without a guest mount: there is no
// mount to mark (0x80010016, as the module answers a path that is no mount root).
public sealed partial class IoFileMgr
{
    /// <summary>The mount points of FW 3.74 iofilemgr's start (its table at 0x81022b30, filled by FUN_8101888c): id, the
    /// internal root path of the file system, the device. Fixed by the firmware (the reason a fixed table is the
    /// model here).</summary>
    internal static readonly (uint Id, string RootPath, string Device)[] MountPoints =
    [
        (0x1, "", "sdstor0:"), (0x2, "", "md0:"), (0x100, "/sd/exfat", "sd0:"), (0x200, "/os/exfat", "os0:"),
        (0x300, "/vs/exfat", "vs0:"), (0xB00, "/sa/exfat", "sa0:"), (0x400, "/vd/exfat", "vd0:"),
        (0x500, "/tm/exfat", "tm0:"), (0x600, "/ur/exfat", "ur0:"), (0xC00, "/pd/exfat", "pd0:"),
        (0x700, "/ud/exfat", "ud0:"), (0x800, "/ux/exfat", "ux0:"), (0x900, "/gro/exfat", "gro0:"),
        (0xA00, "/grw/exfat", "grw0:"), (0xD00, "/imc/exfat", "imc0:"), (0xE00, "/xmc/exfat", "xmc0:"),
        (0x50000, "/mfa/exfat", "mfa0:"), (0x60000, "/mfb/exfat", "mfb0:"), (0x10000, "/lma/exfat", "lma0:"),
        (0x20000, "/lmb/exfat", "lmb0:"), (0xF00, "/uma/exfat", "uma0:"),
    ];

    /// <summary>An I/O error event (the module's SceUIDIoErrorEventClass object around its event flag).</summary>
    [GenerateSnapshot]
    sealed partial class IoErrorEvent
    {
        public uint MountId, Mask, Record;
        public int Pid;
        public bool Notified;
    }

    [SnapshotMissing] // states of builds before it: no error event was made with its fields
    readonly Dictionary<int, IoErrorEvent> _errorEvents = new();

    const uint ErrorRecordSize = 0x1C;

    static int MountPointIndex(uint id) => Array.FindIndex(MountPoints, m => m.Id == id);

    /// <summary>"SceIoErrorEvent" plus the device up to its colon (at most 16 characters), the first one upper case.</summary>
    internal static string ErrorEventName(string device)
    {
        int colon = device.IndexOf(':');
        string name = colon < 0 ? device : device[..colon];
        if (name.Length > 16)
        {
            name = name[..16];
        }

        return name.Length == 0 ? "SceIoErrorEvent" : "SceIoErrorEvent" + char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>ksceIoCreateErrorEvent(mount id, type, record): type 1 only, the record's words +8, +0xC, +0x14 and
    /// +0x18 zero and the id one of the mount points (else 0x80010016); the event's UID.</summary>
    [Hle(Nid = 0x3C0343DB, Library = "SceIofilemgrForDriver", ReturnsValue = true)]
    int ksceIoCreateErrorEvent(uint mountId, uint type, uint record)
    {
        if (mountId == 0 || type != 1 || record == 0)
        {
            return VFS_ERR_INVAL;
        }

        bool reserved = GuestMemory.R32(record + 8) != 0 || GuestMemory.R32(record + 0xC) != 0
                        || GuestMemory.R32(record + 0x14) != 0 || GuestMemory.R32(record + 0x18) != 0;
        int index = MountPointIndex(mountId);
        if (reserved || index < 0)
        {
            return VFS_ERR_INVAL;
        }

        int uid = K.Objects.Register(new EventFlag { Name = ErrorEventName(MountPoints[index].Device), Attr = 0 });
        lock (_errorEvents)
        {
            _errorEvents[uid] = new IoErrorEvent { MountId = mountId, Mask = 1, Record = record, Pid = ProcessScope.CurrentPid, Notified = false };
        }

        return uid;
    }

    /// <summary>ksceIoDeleteErrorEvent(uid): an object of another class 0x80028002.</summary>
    [Hle(Nid = 0xC6158F8D, Library = "SceIofilemgrForDriver")]
    int ksceIoDeleteErrorEvent(int uid)
    {
        lock (_errorEvents)
        {
            if (!_errorEvents.Remove(uid))
            {
                return IO_KERNEL_ERR_NO_EVENT;
            }
        }

        K.Objects.Remove(uid);
        return 0;
    }

    /// <summary>ksceIoClearErrorEvent(uid): the event can be notified again; an unknown UID is the kernel's lookup
    /// error.</summary>
    [Hle(Nid = 0x40B933C7, Library = "SceIofilemgrForDriver")]
    int ksceIoClearErrorEvent(int uid)
    {
        lock (_errorEvents)
        {
            if (!_errorEvents.TryGetValue(uid, out IoErrorEvent? errorEvent))
            {
                return SceError.UNKNOWN_UID;
            }

            errorEvent.Notified = false;
            return 0;
        }
    }

    /// <summary>NID_DD46CD63(mount id, mask, info): notifies the error events of the mount (see the file's header); 0.</summary>
    [Hle(Nid = 0xDD46CD63, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_DD46CD63(uint mountId, uint mask, uint info)
    {
        List<(int Uid, IoErrorEvent Event)> due = [];
        lock (_errorEvents)
        {
            foreach ((int uid, IoErrorEvent errorEvent) in _errorEvents)
            {
                if (errorEvent.MountId == mountId && (mask & errorEvent.Mask) != 0 && !errorEvent.Notified)
                {
                    errorEvent.Notified = true;
                    due.Add((uid, errorEvent));
                }
            }
        }

        uint infoWord1 = info == 0 ? 0 : GuestMemory.R32(info + 4);
        uint infoWord4 = info == 0 ? 0 : GuestMemory.R32(info + 0x10);
        foreach ((int uid, IoErrorEvent errorEvent) in due)
        {
            AddressSpace? space = Emu.Vita.ByPid(errorEvent.Pid)?.Space;
            using (AddressSpace.Use(space ?? AddressSpace.Current))
            {
                uint record = errorEvent.Record;
                GuestMemory.Fill(record, 0, ErrorRecordSize);
                GuestMemory.W32(record, mountId);
                GuestMemory.W32(record + 4, infoWord1);
                GuestMemory.W32(record + 0x10, infoWord4);
            }

            K.Objects.Get<EventFlag>(uid)?.Set(mask);
        }

        return 0;
    }

    /// <summary>NID_5B7E5AB8(mount id): marks the media of the guest mount at the id's root path as changed (see the
    /// file's header); an unknown id or a path that is no guest mount's root 0x80010016.</summary>
    [Hle(Nid = 0x5B7E5AB8, Library = "SceIofilemgrForDriver")]
    int ksceIofilemgr_5B7E5AB8(uint mountId)
    {
        int index = MountPointIndex(mountId);
        if (index < 0 || MountPoints[index].RootPath.Length == 0)
        {
            return VFS_ERR_INVAL;
        }

        GuestMount? mount = _guestMounts.Find(m => m.RootPath == MountPoints[index].RootPath);
        if (mount == null)
        {
            return VFS_ERR_INVAL;
        }

        uint extra = GuestMemory.R32(mount.Mount + 0xCC);
        if (extra == 0)
        {
            return VFS_ERR_INVAL;
        }

        // the media is there again: the mount's presence word (+8 points at it) is 1, and its wait flag (+0xC points at
        // its UID) wakes the I/O calls waiting for the media
        uint present = GuestMemory.R32(extra + 8);
        if (present != 0)
        {
            GuestMemory.W32(present, 1);
        }

        uint holder = GuestMemory.R32(extra + 0xC);
        if (holder != 0)
        {
            K.Objects.Get<EventFlag>((int)GuestMemory.R32(holder))?.Set(1);
        }

        return 0;
    }

    // ------------------------------------------------------------------ waiting for the media of a mount
    // FW 3.74 iofilemgr keeps, in its data, a default presence word (1) and wait-flag holder (-1) and one pair per
    // removable device class: the game card (0x201, "SceIofileDevWaitForVC"), the memory card (0x202, "...ForMC") and
    // the SD card (0x203, "...ForSD"), all present (1) and without a flag (-1) at the start. Every mount's extra block
    // (+0xCC) points at the defaults (+8 the presence word, +0xC the holder; FUN_81009fb4). A mount asking to wait for
    // its media (flags bit 17, 0x20000) whose file system type's device class (+0x4E) is one of those gets the class's
    // pair and, the first time, the class's event flag (attribute 0x1000) in its holder (FUN_8100b740, from vfsMount);
    // for another class the bit is cleared. The I/O calls on such a mount wait on that flag while the presence word is
    // not 1; NID_5B7E5AB8 sets the word and the flag.

    /// <summary>The module's media words (the data the 3.74 module has at 0x8102101C..0x81021040), in kernel memory.</summary>
    [SnapshotMissing] // states of builds before it: made again on the next mount
    uint _mediaWords;

    const uint MediaDefaultHolder = 0x0, MediaDefaultPresent = 0x18;

    /// <summary>The removable device classes: class, holder offset, presence offset, flag name.</summary>
    static readonly (uint Class, uint Holder, uint Present, string Name)[] s_mediaClasses =
    [
        (0x201, 0x04, 0x1C, "SceIofileDevWaitForVC"),
        (0x202, 0x14, 0x20, "SceIofileDevWaitForMC"),
        (0x203, 0x0C, 0x10, "SceIofileDevWaitForSD"),
    ];

    uint MediaWords()
    {
        if (_mediaWords != 0)
        {
            return _mediaWords;
        }

        uint words = Emu.Memory.Alloc(0x28, "iofilemgr media words", MemoryRegion.Kernel, 4);
        GuestMemory.Fill(words, 0, 0x28);
        GuestMemory.W32(words + MediaDefaultHolder, 0xFFFFFFFF);
        GuestMemory.W32(words + MediaDefaultPresent, 1);
        foreach ((uint _, uint holder, uint present, string _) in s_mediaClasses)
        {
            GuestMemory.W32(words + holder, 0xFFFFFFFF);
            GuestMemory.W32(words + present, 1);
        }

        _mediaWords = words;
        return words;
    }

    /// <summary>The media words of a new mount (see above).</summary>
    void InitMediaWait(uint mount, uint extra)
    {
        uint words = MediaWords();
        GuestMemory.W32(extra, 0);
        GuestMemory.W32(extra + 4, 0);
        GuestMemory.W32(extra + 8, words + MediaDefaultPresent);
        GuestMemory.W32(extra + 0xC, words + MediaDefaultHolder);
        uint flags = GuestMemory.R32(mount + 0x50);
        if ((flags & 0x20000) == 0)
        {
            return;
        }

        uint deviceClass = GuestMemory.R32(mount + 0x4C) >> 16;
        int index = Array.FindIndex(s_mediaClasses, c => c.Class == deviceClass);
        if ((deviceClass & 0xF00) != 0x200)
        {
            GuestMemory.W32(mount + 0x50, flags & ~0x20000u);
            return;
        }

        if (index < 0)
        {
            return; // another 0x2xx class: no flag of its own (the defaults stay)
        }

        (uint _, uint holderOffset, uint presentOffset, string name) = s_mediaClasses[index];
        uint holder = words + holderOffset;
        if (GuestMemory.R32(holder) == 0xFFFFFFFF)
        {
            GuestMemory.W32(holder, (uint)K.Objects.Register(new EventFlag { Name = name, Attr = EventFlag.AttrMulti }));
        }

        GuestMemory.W32(extra + 8, words + presentOffset);
        GuestMemory.W32(extra + 0xC, holder);
    }
}
