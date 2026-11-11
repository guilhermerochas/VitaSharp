using VitaSharp.Core.Io;

namespace VitaSharp.Tests;

/// <summary>The Vita's file systems ignore case; on case-sensitive hosts (Linux, Android) the VFS must too.</summary>
public sealed class VfsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-vfs-" + Guid.NewGuid().ToString("N"));

    public VfsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app", "Data", "Sub"));
        File.WriteAllText(Path.Combine(_root, "app", "Data", "Sub", "File.TXT"), "x");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    Vfs NewVfs()
    {
        var vfs = new Vfs(Path.Combine(_root, "fs"));
        vfs.Mount("app0", Path.Combine(_root, "app"));
        return vfs;
    }

    [Fact]
    public void ResolvesExistingFilesIgnoringCase()
    {
        var host = NewVfs().Resolve("app0:data/SUB/file.txt");
        Assert.NotNull(host);
        Assert.True(File.Exists(host));
        Assert.Equal("x", File.ReadAllText(host!));
    }

    [Fact]
    public void MediaDevicesAreMemoryCardFoldersAndOldOnesMoveThere()
    {
        string fs = Path.Combine(_root, "fs");
        Directory.CreateDirectory(Path.Combine(fs, "photo0", "CAMERA"));
        File.WriteAllText(Path.Combine(fs, "photo0", "CAMERA", "a.jpg"), "old");
        Directory.CreateDirectory(Path.Combine(fs, "ux0", "picture"));
        File.WriteAllText(Path.Combine(fs, "ux0", "picture", "b.jpg"), "new");
        var vfs = new Vfs(fs);
        Assert.Equal(Path.Combine(fs, "ux0", "picture", "CAMERA", "a.jpg"), vfs.Resolve("photo0:CAMERA/a.jpg"));
        Assert.True(File.Exists(vfs.Resolve("photo0:b.jpg")));
        Assert.False(Directory.Exists(Path.Combine(fs, "photo0")));
        Assert.Equal(Path.Combine(fs, "ux0", "video"), vfs.Resolve("video0:"));
        Assert.Equal(Path.Combine(fs, "ux0", "music"), vfs.Resolve("music0:"));
    }

    [Fact]
    public void BackslashesAndDotSegmentsAreNormalized()
    {
        var host = NewVfs().Resolve(@"app0:\DATA\.\x\..\sub\FILE.txt");
        Assert.True(File.Exists(host));
    }

    [Fact]
    public void NewFilesKeepTheMatchedDirectoryCase()
    {
        var vfs = NewVfs();
        var host = vfs.Resolve("app0:DATA/sub/NewDir/new.bin", forWrite: true)!;
        // the existing directories resolve to their on-disk spelling on a case-sensitive file system (Linux); where the
        // file system itself ignores case (NTFS, default APFS on macOS) the guest spelling already reaches them and
        // is returned as is. Either way the new components keep the guest's spelling exactly.
        bool caseInsensitiveFs = Directory.Exists(Path.Combine(_root, "APP", "DATA", "SUB"));
        Assert.Equal(Path.Combine(_root, "app", "Data", "Sub", "NewDir", "new.bin"), host, ignoreCase: caseInsensitiveFs);
        Assert.EndsWith(Path.Combine("NewDir", "new.bin"), host, StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, "y");
        // a file created after the directory was listed is found (listings are revalidated)
        Assert.Equal("y", File.ReadAllText(vfs.Resolve("app0:data/sub/newdir/NEW.BIN")!));
    }

    [Fact]
    public void MissingFilesResolveToAPathThatDoesNotExist()
    {
        var host = NewVfs().Resolve("app0:data/nothing/here.dat");
        Assert.NotNull(host);
        Assert.False(File.Exists(host));
    }
}
