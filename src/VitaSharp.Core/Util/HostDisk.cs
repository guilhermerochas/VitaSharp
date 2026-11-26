namespace VitaSharp.Core.Util;

/// <summary>
/// The size and free space of the host file system that holds a folder. Windows answers by drive (DriveInfo takes the
/// drive's root). Elsewhere DriveInfo asks statvfs for the path it is given, so it is given the folder itself: the root
/// of the path ("/") is the root file system, a different one wherever the data lives on another mount. On Android the
/// app's storage (/storage/emulated, FUSE) sits under a read-only system root that is full ("/": 0 bytes free), so
/// asking for "/" made every emulated device report no free space (the shell's database rebuild then left installed
/// titles without their icon and LiveArea); the same happens on a Linux /home partition or a drive under /media or
/// /Volumes.
/// </summary>
public static class HostDisk
{
    /// <summary>The total and free bytes of the file system that holds <paramref name="folder"/> (or its nearest
    /// existing parent).</summary>
    public static (ulong Total, ulong Free) Of(string folder) => Of(folder, OperatingSystem.IsWindows(), RealFileSystem);

    /// <summary><see cref="Of(string)"/> with the host kind and the file system query given (tests).</summary>
    internal static (ulong Total, ulong Free) Of(string folder, bool windows, Func<string, (ulong Total, ulong Free)> fileSystem)
    {
        return fileSystem(QueryPath(Path.GetFullPath(folder), windows));
    }

    /// <summary>The path the file system is asked about for <paramref name="fullPath"/>: the drive's root on Windows,
    /// else the nearest existing folder of the path (statvfs needs a path that exists).</summary>
    internal static string QueryPath(string fullPath, bool windows)
    {
        if (windows)
        {
            return Path.GetPathRoot(fullPath) is { Length: > 0 } root ? root : fullPath;
        }

        string? current = fullPath;
        while (current != null && !Directory.Exists(current) && !File.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        return current is { Length: > 0 } ? current : fullPath;
    }

    private static (ulong Total, ulong Free) RealFileSystem(string path)
    {
        var drive = new DriveInfo(path);
        return ((ulong)drive.TotalSize, (ulong)drive.AvailableFreeSpace);
    }
}
