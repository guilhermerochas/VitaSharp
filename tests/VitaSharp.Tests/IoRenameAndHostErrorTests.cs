using VitaSharp.Core.Hle.Modules;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// sceIoRename, sceIoRemove, sceIoMkdir and sceIoRmdir as the FW 3.74 iofilemgr answers them on a memory card or ur0:
/// a rename onto a name that exists already (file or folder) is EEXIST (0x80010011) and replaces nothing; the shell
/// writes its files under a temporary name, renames them over the old ones and, on EEXIST, removes the old one and
/// renames again (shell.self 3.74). Before, a file rename silently replaced the target, and a folder rename onto an
/// existing folder, or any rename or remove the host refused, threw the host's exception and ended the calling guest
/// thread (the shell's, while it copies a title's metadata into ur0:appmeta).
/// </summary>
[Collection("guest")]
public sealed class IoRenameAndHostErrorTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotFound = unchecked((int)0x80010002);
    private const int Exists = unchecked((int)0x80010011);
    private const int NotEmpty = unchecked((int)0x8001005A);

    private readonly HleHarness _harness = new(fixture, "iorename");

    private IoFileMgr Io => _harness.Module<IoFileMgr>();

    public void Dispose() => _harness.Dispose();

    private string Host(string guest) => _harness.Emu.Vfs.Resolve(guest)!;

    private void HostFile(string guest, string text)
    {
        string host = Host(guest);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, text);
    }

    [Fact]
    public void ARenameOntoAnExistingFileIsEexistAndKeepsBothFiles()
    {
        HostFile("ur0:shellrev/new.db", "new");
        HostFile("ur0:shellrev/app.db", "old");

        Assert.Equal(Exists, Call(Io, "sceIoRename", "ur0:shellrev/new.db", "ur0:shellrev/app.db"));

        Assert.Equal("new", File.ReadAllText(Host("ur0:shellrev/new.db")));
        Assert.Equal("old", File.ReadAllText(Host("ur0:shellrev/app.db")));
    }

    [Fact]
    public void TheShellsReplaceSequenceRemovesTheOldFileAndRenamesAgain()
    {
        HostFile("ur0:shellrev/new.db", "new");
        HostFile("ur0:shellrev/app.db", "old");

        int result = Call(Io, "sceIoRename", "ur0:shellrev/new.db", "ur0:shellrev/app.db");
        if (result == Exists && Call(Io, "sceIoRemove", "ur0:shellrev/app.db") >= 0)
        {
            result = Call(Io, "sceIoRename", "ur0:shellrev/new.db", "ur0:shellrev/app.db");
        }

        Assert.Equal(0, result);
        Assert.Equal("new", File.ReadAllText(Host("ur0:shellrev/app.db")));
        Assert.False(File.Exists(Host("ur0:shellrev/new.db")));
    }

    [Fact]
    public void ARenameOntoAnExistingFolderIsEexistInsteadOfEndingTheThread()
    {
        HostFile("ur0:temp/appmeta/PCSE00001/icon0.png", "icon");
        HostFile("ur0:appmeta/PCSE00001/pic0.png", "old picture");

        Assert.Equal(Exists, Call(Io, "sceIoRename", "ur0:temp/appmeta/PCSE00001", "ur0:appmeta/PCSE00001"));
        Assert.Equal(Exists, Call(Io, "sceIoRename", "ur0:temp/appmeta/PCSE00001/icon0.png", "ur0:appmeta/PCSE00001"));

        Assert.True(File.Exists(Host("ur0:temp/appmeta/PCSE00001/icon0.png")));
        Assert.Equal("old picture", File.ReadAllText(Host("ur0:appmeta/PCSE00001/pic0.png")));
    }

    [Fact]
    public void AFolderMovesToANewName()
    {
        HostFile("ur0:temp/appmeta/PCSE00002/icon0.png", "icon");
        Directory.CreateDirectory(Host("ur0:appmeta"));

        Assert.Equal(0, Call(Io, "sceIoRename", "ur0:temp/appmeta/PCSE00002", "ur0:appmeta/PCSE00002"));

        Assert.Equal("icon", File.ReadAllText(Host("ur0:appmeta/PCSE00002/icon0.png")));
        Assert.False(Directory.Exists(Host("ur0:temp/appmeta/PCSE00002")));
    }

    [Fact]
    public void ARenameThatOnlyChangesTheCaseIsTheSameEntry()
    {
        HostFile("ur0:shellrev/case.txt", "x");

        Assert.Equal(0, Call(Io, "sceIoRename", "ur0:shellrev/case.txt", "ur0:shellrev/CASE.txt"));

        Assert.Single(Directory.GetFiles(Host("ur0:shellrev"), "*.txt", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }));
    }

    [Fact]
    public void ARenameIntoAMissingFolderIsEnoentInsteadOfEndingTheThread()
    {
        HostFile("ur0:shellrev/lonely.txt", "x");

        Assert.Equal(NotFound, Call(Io, "sceIoRename", "ur0:shellrev/lonely.txt", "ur0:shellrev/missing/lonely.txt"));
        Assert.Equal(NotFound, Call(Io, "sceIoRename", "ur0:shellrev/absent.txt", "ur0:shellrev/other.txt"));

        Assert.True(File.Exists(Host("ur0:shellrev/lonely.txt")));
    }

    [Fact]
    public void MkdirOnAnExistingNameAndRmdirOfAFullFolderAnswerTheirErrors()
    {
        HostFile("ur0:shellrev/full/file.txt", "x");

        Assert.Equal(Exists, Call(Io, "sceIoMkdir", "ur0:shellrev/full", 0x1FF));
        Assert.Equal(Exists, Call(Io, "sceIoMkdir", "ur0:shellrev/full/file.txt", 0x1FF));
        Assert.Equal(NotEmpty, Call(Io, "sceIoRmdir", "ur0:shellrev/full"));
        Assert.True(File.Exists(Host("ur0:shellrev/full/file.txt")));

        Assert.Equal(0, Call(Io, "sceIoRemove", "ur0:shellrev/full/file.txt"));
        Assert.Equal(0, Call(Io, "sceIoRmdir", "ur0:shellrev/full"));
        Assert.False(Directory.Exists(Host("ur0:shellrev/full")));
    }

    [Fact]
    public void AFileTheHostWillNotLetGoIsAFailedCallNotACrash()
    {
        // a file another program holds without sharing: Windows refuses to move or delete it (IOException); other
        // hosts let it go. Either way the call answers, and the thread keeps running
        HostFile("ur0:shellrev/held.txt", "held");
        using (new FileStream(Host("ur0:shellrev/held.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            int rename = Call(Io, "sceIoRename", "ur0:shellrev/held.txt", "ur0:shellrev/moved.txt");
            Assert.True(rename == 0 || rename < 0);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(rename < 0);
                Assert.True(Call(Io, "sceIoRemove", "ur0:shellrev/held.txt") < 0);
            }
        }
    }
}
