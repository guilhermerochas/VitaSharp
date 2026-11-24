using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// The kernel event log of SceDebugForDriver (sysmem 3.74, read in the module, study only): 16 records of 0xC0 bytes,
// four per CPU (CPU n writes records 4n..4n+3, round robin). A record is a 0x40-byte head and up to 0x80 bytes of data:
// +0x00 the record size ((data + 0x43) & ~3), +0x04 the version 10, +0x08 the 10-byte name "NPXS19999" the module
// keeps, +0x14/+0x16 two 16-bit codes, +0x18 the process id, +0x1C the thread id, +0x30 the system time (64 bits),
// +0x38 a 32-bit value, +0x3C the data size, +0x40 the data. A put calls the flush handler NID_78027203 of
// SceDebugForKernel sets; that handler is a debug console hook, not called here. The emulated threads run on CPU 0.
public sealed partial class SystemServicesVsh
{
    const int EventLogRecords = 16, EventLogRecordSize = 0xC0, EventLogHeadSize = 0x40, EventLogMaxData = 0x80;
    const int EventLogRecordsPerCpu = 4;

    /// <summary>The 10-byte name every record carries at +0x08 (these 9 bytes and a zero; the head is cleared first): a
    /// constant of sysmem 3.74's read-only data (segment 0, offset 0x26FAC, read in the module, study only), the same
    /// for every process that puts a record, not the title id of a process (the process id is at +0x18). The firmware
    /// module shells VitaSharp shows carry no firmware bytes, so the value is written here, as a firmware constant
    /// (GenericCodeTests.s_firmwareConstants lists it, with this reason).</summary>
    internal static ReadOnlySpan<byte> SysmemEventLogName => "NPXS19999"u8;

    /// <summary>SceDebugForDriver: data longer than 0x80 bytes (0x80010021).</summary>
    const int EVENTLOG_ERR_TOO_LONG = unchecked((int)0x80010021);

    /// <summary>SceDebugForDriver: a NULL buffer, or data without a pointer (0x80010016).</summary>
    const int EVENTLOG_ERR_INVALID = unchecked((int)0x80010016);

    [SnapshotMissing] // states of builds before it: an empty log
    readonly byte[] _eventLog = new byte[EventLogRecords * EventLogRecordSize];

    [SnapshotMissing]
    int _eventLogNextCpu0;

    /// <summary>The 16 records of the log (0xC0 bytes each, in ring order).</summary>
    internal ReadOnlySpan<byte> EventLogRecordsBytes => _eventLog;

    /// <summary>ksceEventLogPut(SceUID pid, SceUID tid, ushort code, ushort subcode, uint value, const void *data,
    /// uint size): writes a record of the current CPU's ring (up to 0x80 bytes of data: 0x80010021 above; data
    /// without a pointer: 0x80010016); 0.</summary>
    [Hle(Nid = 0x912CF2BA, Library = "SceDebugForDriver")]
    int ksceEventLogPut(int pid, int tid, int code, int subcode, uint value, uint data, uint size)
    {
        if (size != 0 && data == 0)
        {
            return EVENTLOG_ERR_INVALID;
        }

        if (size > EventLogMaxData)
        {
            return EVENTLOG_ERR_TOO_LONG;
        }

        ReadOnlySpan<byte> bytes = size == 0 ? ReadOnlySpan<byte>.Empty : GuestMemory.ReadSpan(data, (int)size);
        EventLogWrite(pid, tid, code, subcode, value, bytes);
        return 0;
    }

    /// <summary>ksceEventLogPutForCurrentThread(ushort code, ushort subcode, uint value, const void *data, uint size):
    /// ksceEventLogPut for the kernel process (0x10005) and the calling thread.</summary>
    [Hle(Nid = 0x95B38C6C, Library = "SceDebugForDriver")]
    int ksceEventLogPutForCurrentThread(int code, int subcode, uint value, uint data, uint size)
    {
        return EventLogPutForCurrentThread(code, subcode, value, data, size);
    }

    /// <summary>ksceEventLogPutForCurrentThread for the bridge (_vshEventLogPut).</summary>
    internal int EventLogPutForCurrentThread(int code, int subcode, uint value, uint data, uint size)
    {
        int thread = GuestThread.Current?.Uid ?? 0;
        return ksceEventLogPutThroughEntry(KernelSpace.KernelPid, thread, code, subcode, value, data, size);
    }

    /// <summary>One record into CPU 0's ring (data of at most 0x80 bytes, checked by the caller).</summary>
    void EventLogWrite(int pid, int tid, int code, int subcode, uint value, ReadOnlySpan<byte> data)
    {
        int record = _eventLogNextCpu0;
        _eventLogNextCpu0 = (record + 1) % EventLogRecordsPerCpu;
        Span<byte> entry = _eventLog.AsSpan(record * EventLogRecordSize, EventLogRecordSize);
        entry[..EventLogHeadSize].Clear();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(entry, ((uint)data.Length + 0x43) & ~3u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 10);
        SysmemEventLogName.CopyTo(entry[8..]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(entry[0x14..], (ushort)code);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(entry[0x16..], (ushort)subcode);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(entry[0x18..], pid);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(entry[0x1C..], tid);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry[0x30..], K.ProcessTimeUs);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(entry[0x38..], value);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(entry[0x3C..], (uint)data.Length);
        data.CopyTo(entry[EventLogHeadSize..]);
    }

    /// <summary>ksceEventLogGetInfo(void *buf, uint size, int *count): copies the records written (in ring order)
    /// that fit, each at its own size; the count of records copied (optional); the bytes copied. A NULL buffer is
    /// 0x80010016.</summary>
    [Hle(Nid = 0xCC5365D3, Library = "SceDebugForDriver")]
    int ksceEventLogGetInfo(uint buffer, uint size, uint count)
    {
        if (buffer == 0)
        {
            return EVENTLOG_ERR_INVALID;
        }

        uint left = size;
        uint at = buffer;
        int copied = 0;
        for (int record = 0; record < EventLogRecords; record++)
        {
            ReadOnlySpan<byte> entry = _eventLog.AsSpan(record * EventLogRecordSize, EventLogRecordSize);
            uint recordSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (recordSize - EventLogHeadSize <= EventLogMaxData && recordSize <= left)
            {
                entry[..(int)recordSize].CopyTo(GuestMemory.Span(at, (int)recordSize));
                at += recordSize;
                left -= recordSize;
                copied++;
            }
        }

        if (count != 0)
        {
            GuestMemory.W32(count, (uint)copied);
        }

        return (int)(size - left);
    }
}
