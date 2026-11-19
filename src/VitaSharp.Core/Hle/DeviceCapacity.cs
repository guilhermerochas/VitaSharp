namespace VitaSharp.Core.Hle;

/// <summary>
/// The capacity and free space of an emulated storage device (a memory card partition): those of the host drive that
/// holds its folder, capped at 64 GiB (the largest memory card); without a readable drive, 64 GiB with half free. The
/// one source for sceAppMgrGetDevInfo and the memory card interface (SceMsif).
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
}
