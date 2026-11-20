using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceUlobjMgr (os0:bootimage SceUlobjMgr 3.74, read in the module, study only): the kernel's support of user-level
// objects (the libult threads, mutexes, condition variables and the rest a process keeps in its own memory) for the
// debugger and the core dump module. A process registers its libult protocol revision (at least 0x1800000 with a
// non-zero low half, else 0x80020005) and starts the support (a revision above 3.74's 0x3740000 0x80020004, a table
// above 32 MiB 0x8002710B); stopping without having started is 0x80029001. The kernel entries (SceUlobjMgrForDriver)
// find a process's record (none: 0x80010002), and read the objects of the process's memory by their addresses; the
// emulator's libult runs as the title's own code (LLE) and keeps no kernel-side record of its objects, so a lookup of
// an object finds no kernel object (0x8002000C) after the module's argument checks (0x80020005).
public sealed partial class DebugPerf
{
    const int ULOBJ_ERR_ARGUMENT = unchecked((int)0x80020005);
    const int ULOBJ_ERR_REVISION = unchecked((int)0x80020004);
    const int ULOBJ_ERR_TOO_LARGE = unchecked((int)0x8002710B);
    const int ULOBJ_ERR_NOT_STARTED = unchecked((int)0x80029001);
    const int ULOBJ_ERR_NO_RECORD = unchecked((int)0x80010002);
    const int ULOBJ_ERR_NO_OBJECT = unchecked((int)0x8002000C);

    /// <summary>The newest libult protocol the module accepts (3.74).</summary>
    const uint UlobjMaxRevision = 0x3740000;

    /// <summary>The largest object table a process may ask for (32 MiB).</summary>
    const uint UlobjMaxTable = 0x2000000;

    /// <summary>The size of a process record in kernel memory.</summary>
    const uint UlobjRecordSize = 0x40;

    /// <summary>The process started the support (its record exists).</summary>
    [SnapshotMissing] // states of builds before it: not started
    bool _ulobjStarted;

    /// <summary>The revision and the table size it started with.</summary>
    [SnapshotMissing]
    uint _ulobjRevision;

    [SnapshotMissing]
    uint _ulobjTableSize;

    /// <summary>The process record in kernel memory (0 until first asked for).</summary>
    [SnapshotMissing]
    uint _ulobjRecord;

    int UlobjRegisterRevision(uint revision)
    {
        return revision < 0x1800000 || (revision & 0xFFFF) == 0 ? ULOBJ_ERR_ARGUMENT : 0;
    }

    int UlobjStart(uint size, uint revision)
    {
        if (revision > UlobjMaxRevision)
        {
            return ULOBJ_ERR_REVISION;
        }

        if (size > UlobjMaxTable)
        {
            return ULOBJ_ERR_TOO_LARGE;
        }

        _ulobjStarted = true;
        _ulobjRevision = revision;
        _ulobjTableSize = size;
        return 0;
    }

    int UlobjStop()
    {
        if (!_ulobjStarted)
        {
            return ULOBJ_ERR_NOT_STARTED;
        }

        _ulobjStarted = false;
        return 0;
    }

    /// <summary>The DebugPerf of process <paramref name="pid"/> (0: the caller), or null.</summary>
    DebugPerf? UlobjProcess(int pid)
    {
        Emulator? process = pid == 0 || pid == Emu.Pid ? Emu : Emu.Vita.ByPid(pid);
        return process?.Hle.Modules.OfType<DebugPerf>().FirstOrDefault();
    }

    /// <summary>The kernel record of a process that started the support: +0xC the table size, +0x14 the revision.</summary>
    uint UlobjRecord()
    {
        if (_ulobjRecord == 0)
        {
            _ulobjRecord = Emu.Memory.Alloc(UlobjRecordSize, "ulobj record", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(_ulobjRecord, 0, UlobjRecordSize);
        GuestMemory.W32(_ulobjRecord + 0xC, _ulobjTableSize);
        GuestMemory.W32(_ulobjRecord + 0x14, _ulobjRevision);
        return _ulobjRecord;
    }

    /// <summary>NID_A90A564C(pid, revision, table, record): the process's record: NULLs 0x80020005, not started
    /// 0x80010002; its revision, the address of its table entry (+0xC) and the record.</summary>
    [Hle(Nid = 0xA90A564C, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_A90A564C(int pid, uint revision, uint table, uint record)
    {
        if (revision == 0 || table == 0 || record == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        if (UlobjProcess(pid) is not { _ulobjStarted: true } target)
        {
            return ULOBJ_ERR_NO_RECORD;
        }

        uint address = target.UlobjRecord();
        GuestMemory.W32(revision, target._ulobjRevision);
        GuestMemory.W32(table, address + 0xC);
        GuestMemory.W32(record, address);
        return 0;
    }

    /// <summary>NID_A48A5B3C(record): releases a record taken with NID_A90A564C: NULL 0x80020005; 0.</summary>
    [Hle(Nid = 0xA48A5B3C, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_A48A5B3C(uint record)
    {
        return record == 0 ? ULOBJ_ERR_ARGUMENT : 0;
    }

    /// <summary>NID_6F868221(): some process has started the support: 0, else 0x80020004.</summary>
    [Hle(Nid = 0x6F868221, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_6F868221()
    {
        foreach (Emulator process in Emu.Vita.Processes)
        {
            if (process.Hle.Modules.OfType<DebugPerf>().FirstOrDefault() is { _ulobjStarted: true })
            {
                return 0;
            }
        }

        return ULOBJ_ERR_REVISION;
    }

    /// <summary>NID_332F2E58(pid, a, b, c, d): the debugger's request through the sysroot with the process's table:
    /// handled by no debugger (0).</summary>
    [Hle(Nid = 0x332F2E58, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_332F2E58(int pid, uint first, uint second, uint third, uint fourth)
    {
        return 0;
    }

    /// <summary>The object lookups: their pointer checks, then no kernel object for the address.</summary>
    static int UlobjLookup(bool argumentsGiven)
    {
        return argumentsGiven ? ULOBJ_ERR_NO_OBJECT : ULOBJ_ERR_ARGUMENT;
    }

    /// <summary>NID_525A9BDB(pid, object, info): a lwmutex's information: NULL info 0x80020005.</summary>
    [Hle(Nid = 0x525A9BDB, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_525A9BDB(int pid, uint obj, uint info)
    {
        return UlobjLookup(info != 0);
    }

    /// <summary>NID_FFC8C811(pid, object, ?, info): a thread's information: NULL info 0x80020005.</summary>
    [Hle(Nid = 0xFFC8C811, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_FFC8C811(int pid, uint obj, uint unused, uint info)
    {
        return UlobjLookup(info != 0);
    }

    /// <summary>NID_6D2C8928(pid, object, list, max, count): the waiters of an object: NULL list or count, max 0 or a
    /// negative count 0x80020005.</summary>
    [Hle(Nid = 0x6D2C8928, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_6D2C8928(int pid, uint obj, uint list, uint max, uint count)
    {
        return UlobjLookup(list != 0 && max != 0 && count != 0 && (int)GuestMemory.R32(count) >= 0);
    }

    /// <summary>NID_362C9488(pid, object, list, max, count): the waiters of a condition: as NID_6D2C8928.</summary>
    [Hle(Nid = 0x362C9488, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_362C9488(int pid, uint obj, uint list, uint max, uint count)
    {
        return UlobjLookup(list != 0 && max != 0 && count != 0 && (int)GuestMemory.R32(count) >= 0);
    }

    /// <summary>NID_74B9199C(pid, object, list, max, count): the waiters of a semaphore: as NID_6D2C8928.</summary>
    [Hle(Nid = 0x74B9199C, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_74B9199C(int pid, uint obj, uint list, uint max, uint count)
    {
        return UlobjLookup(list != 0 && max != 0 && count != 0 && (int)GuestMemory.R32(count) >= 0);
    }

    /// <summary>NID_6BCB4568(pid, object, info): an event flag's information: NULL info 0x80020005.</summary>
    [Hle(Nid = 0x6BCB4568, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_6BCB4568(int pid, uint obj, uint info)
    {
        return UlobjLookup(info != 0);
    }

    /// <summary>NID_ADAAA961(pid, object, info): a reader/writer lock's information: NULL info 0x80020005.</summary>
    [Hle(Nid = 0xADAAA961, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_ADAAA961(int pid, uint obj, uint info)
    {
        return UlobjLookup(info != 0);
    }

    /// <summary>NID_3685D4FB(pid, object, list, max, ids, count, ?): the waiters with their ids: NULLs 0x80020005.</summary>
    [Hle(Nid = 0x3685D4FB, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_3685D4FB(int pid, uint obj, uint list, uint max, uint ids, uint count, uint total)
    {
        return UlobjLookup(list != 0 && ids != 0 && count != 0 && total != 0);
    }

    /// <summary>NID_0C29AFEC(pid, object, list, count, max, ids, actual, total): as NID_3685D4FB.</summary>
    [Hle(Nid = 0x0C29AFEC, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_0C29AFEC(int pid, uint obj, uint list, uint count, uint max, uint ids, uint actual, uint total)
    {
        return UlobjLookup(list != 0 && count != 0 && ids != 0 && actual != 0 && total != 0);
    }

    /// <summary>NID_5D7A7925(pid, object, list, max, ids, count, actual, total): as NID_3685D4FB.</summary>
    [Hle(Nid = 0x5D7A7925, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_5D7A7925(int pid, uint obj, uint list, uint max, uint ids, uint count, uint actual, uint total)
    {
        return UlobjLookup(list != 0 && ids != 0 && count != 0 && actual != 0 && total != 0);
    }

    /// <summary>NID_3D6E2EF3(pid, object, list, max, ?): a barrier's waiters: no kernel object.</summary>
    [Hle(Nid = 0x3D6E2EF3, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_3D6E2EF3(int pid, uint obj, uint list, uint max, uint count)
    {
        return ULOBJ_ERR_NO_OBJECT;
    }

    /// <summary>NID_8E200387(pid, object, info): a queue's information: NULL info 0x80020005.</summary>
    [Hle(Nid = 0x8E200387, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_8E200387(int pid, uint obj, uint info)
    {
        return UlobjLookup(info != 0);
    }

    /// <summary>NID_BB11EB2F(pid, list, max, count, kind): the process's objects of a kind: none registered with the
    /// kernel (count 0).</summary>
    [Hle(Nid = 0xBB11EB2F, Library = "SceUlobjMgrForDriver")]
    int ksceUlobjMgr_BB11EB2F(int pid, uint list, uint max, uint count, uint kind)
    {
        if (count == 0)
        {
            return ULOBJ_ERR_ARGUMENT;
        }

        GuestMemory.W32(count, 0);
        return 0;
    }

    /// <summary>NID_CDAD91AD(pid, object, kind): the kind of an object: no kernel object.</summary>
    [Hle(Nid = 0xCDAD91AD, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_CDAD91AD(int pid, uint obj, uint kind)
    {
        return ULOBJ_ERR_NO_OBJECT;
    }

    /// <summary>NID_B978DB70(pid, object, ?, info): an object's name block: NULL info 0x80020005.</summary>
    [Hle(Nid = 0xB978DB70, Library = "SceUlobjMgrForDriver", Default = true)]
    int ksceUlobjMgr_B978DB70(int pid, uint obj, uint unused, uint info)
    {
        return UlobjLookup(info != 0);
    }
}
