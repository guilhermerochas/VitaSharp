using System.Buffers.Binary;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle;

/// <summary>
/// The capacity and free space of an emulated storage device (a memory card partition): those of the host drive that
/// holds its folder, capped at 64 GiB (the largest memory card); without a readable drive, 64 GiB with half free. The
/// one source for sceAppMgrGetDevInfo, sceAppUtilCacheGetDevInfo, the device information of sceIoDevctl 0x3001 (and
/// sceCompatGetDevInf, which asks it for ux0:) and the memory card interface (SceMsif).
/// </summary>
public static class DeviceCapacity
{
    /// <summary>The largest memory card: 64 GiB.</summary>
    public const ulong MaxBytes = 64UL << 30;

    /// <summary>The total and free bytes behind <paramref name="hostFolder"/> (a resolved device root).</summary>
    public static (ulong Total, ulong Free) Of(string hostFolder)
    {
        ulong total = MaxBytes;
        ulong free = MaxBytes / 2;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(hostFolder))!);
            total = Math.Min((ulong)drive.TotalSize, MaxBytes);
            free = Math.Min((ulong)drive.AvailableFreeSpace, total);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        return (total, free);
    }

    /// <summary>The SceIoDevInfo block of devctl 0x3001: u64 capacity, u64 free bytes, u32 cluster size, u32 0
    /// (SceAppMgr 3.74 reads the capacity at +0 and the free bytes at +8 for sceAppMgrGetDevInfo, and the cluster size
    /// at +0x10 to round a space check; study only).</summary>
    public const uint DevInfoSize = 0x18;

    /// <summary>The cluster size the block reports: 4 KiB (no source for the memory card driver's value).</summary>
    public const uint ClusterSize = 0x1000;

    /// <summary>Fills the first <paramref name="length"/> bytes (at most <see cref="DevInfoSize"/>) of the SceIoDevInfo
    /// at <paramref name="info"/> for the device served at <paramref name="hostFolder"/> (null: no readable drive).</summary>
    public static void WriteDevInfo(uint info, uint length, string? hostFolder)
    {
        (ulong total, ulong free) = hostFolder == null ? (MaxBytes, MaxBytes / 2) : Of(hostFolder);
        Span<byte> block = stackalloc byte[(int)DevInfoSize];
        block.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(block, total);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], free);
        BinaryPrimitives.WriteUInt32LittleEndian(block[0x10..], ClusterSize);
        int count = (int)Math.Min(length, DevInfoSize);
        block[..count].CopyTo(GuestMemory.Span(info, count));
    }
}
