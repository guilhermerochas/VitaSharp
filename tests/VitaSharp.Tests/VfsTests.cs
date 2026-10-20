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
        try { Directory.Delete(_root, true); } catch { }
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
        // the existing directories resolve to their on-disk spelling; the new components keep the guest's
        // (Windows returns the guest spelling: its file system ignores case anyway)
        Assert.Equal(Path.Combine(_root, "app", "Data", "Sub", "NewDir", "new.bin"), host, ignoreCase: OperatingSystem.IsWindows());
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
