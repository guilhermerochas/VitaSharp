using VitaSharp.Core.Util;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The free space of an emulated device comes from the host file system that holds its folder, not from the root of
/// the folder's path. On Android the app's storage (/storage/emulated, FUSE, 100+ GB free) sits under a full, read-only
/// system root ("/": 0 bytes free); asking for "/" made every device report no free space, and the shell's database
/// rebuild (dbrecovery's promoter checks the space with sceIoDevctl 0x3001) then left installed titles without their
/// ur0:appmeta icon and LiveArea. The file system query is replaced here: the root reports nothing free, any other
/// folder plenty, as on the phone.
/// </summary>
public sealed class HostDiskTests : IDisposable
{
    private const ulong PhoneStorageFree = 107UL << 30;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-hostdisk-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _asked = new();

    public HostDiskTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "fs", "ur0"));
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    /// <summary>A file system where the path's root is the full system partition and every other folder is the
    /// storage mount.</summary>
    private (ulong Total, ulong Free) PhoneFileSystem(string path)
    {
        _asked.Add(path);
        bool systemRoot = string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), StringComparison.Ordinal);
        return systemRoot ? (902UL << 20, 0UL) : (229UL << 30, PhoneStorageFree);
    }

    [Fact]
    public void AUnixHostAsksTheFileSystemOfTheFolderNotTheRootOfItsPath()
    {
        string ur0 = Path.Combine(_root, "fs", "ur0");

        (ulong total, ulong free) = HostDisk.Of(ur0, windows: false, PhoneFileSystem);

        Assert.Equal(PhoneStorageFree, free);
        Assert.Equal(229UL << 30, total);
        Assert.Equal([Path.GetFullPath(ur0)], _asked);
    }

    [Fact]
    public void AFolderNotCreatedYetIsAnsweredByItsNearestExistingParent()
    {
        string missing = Path.Combine(_root, "fs", "ux0", "app", "NEW00001");

        (_, ulong free) = HostDisk.Of(missing, windows: false, PhoneFileSystem);

        Assert.Equal(PhoneStorageFree, free);
        Assert.Equal([Path.GetFullPath(_root) + Path.DirectorySeparatorChar + "fs"], _asked);
    }

    [Fact]
    public void WindowsAsksTheDriveOfTheFolder()
    {
        string ur0 = Path.Combine(_root, "fs", "ur0");

        HostDisk.Of(ur0, windows: true, path =>
        {
            _asked.Add(path);
            return (1, 1);
        });

        Assert.Equal([Path.GetPathRoot(Path.GetFullPath(ur0))!], _asked);
    }

    [Fact]
    public void TheRealHostReportsFreeSpaceForTheTemporaryFolder()
    {
        (ulong total, ulong free) = HostDisk.Of(_root);

        Assert.True(total > 0);
        Assert.True(free > 0);
        Assert.True(free <= total);
    }
}
