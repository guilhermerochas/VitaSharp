using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Io;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// A directory of app0 with an installed update (appmgr mounts ux0:patch/&lt;id&gt; over ux0:app/&lt;id&gt;; Vita3K merges
/// the update into the application the same way): an update carries only the files it changes, so sceIoDopen lists
/// the application's entries and the update's, each name once (any case), the update's file standing for a name both
/// have, and a directory that only one layer has still opens. Without an update nothing changes. Synthetic content.
/// </summary>
[Collection("guest")]
public sealed class App0UpdateListingTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NoEnt = unchecked((int)0x80010002);

    private readonly HleHarness _h = new(fixture, "app0-update");

    public void Dispose() => _h.Dispose();

    private IoFileMgr Io => _h.Module<IoFileMgr>();

    private static void Write(string dir, string relative, string text)
    {
        string path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>The application PCSE99901 in ux0:app with data/a.txt, data/b.txt, data/sub/c.txt and base/only.txt,
    /// and its update with data/B.TXT (another case), data/new.txt and upd/only.txt; app0 shows both.</summary>
    private (string App, string Patch) Install()
    {
        string app = FirmwareLayout.Applications.Host(_h.Root, "PCSE99901");
        string patch = FirmwareLayout.Patches.Host(_h.Root, "PCSE99901");
        Write(app, "data/a.txt", "a");
        Write(app, "data/b.txt", "older b");
        Write(app, "data/sub/c.txt", "c");
        Write(app, "base/only.txt", "base");
        Write(patch, "data/B.TXT", "new b");
        Write(patch, "data/new.txt", "new");
        Write(patch, "upd/only.txt", "upd");
        _h.Emu.Vfs.Mount("app0", app);
        _h.Emu.Vfs.OverlayApp0Patch(app, "PCSE99901");
        return (app, patch);
    }

    private List<(string Name, long Size)> List(string path)
    {
        int dir = Io.sceIoDopen(path);
        Assert.True(dir > 0, $"sceIoDopen({path}) = 0x{dir:X8}");
        uint dirent = _h.Alloc(352);
        var listed = new List<(string, long)>();
        while (Io.sceIoDread(dir, dirent) > 0)
        {
            listed.Add((GuestMemory.ReadCString(dirent + 88), GuestMemory.Read<long>(dirent + 8)));
        }

        Assert.Equal(0, Io.sceIoDclose(dir));
        listed.Sort((x, y) => string.Compare(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase));
        return listed;
    }

    [Fact]
    public void ADirectoryOfBothLayersListsTheApplicationsAndTheUpdatesEntriesOnce()
    {
        Install();

        List<(string Name, long Size)> listed = List("app0:data");

        // b.txt once, as the update has it (its name and its 5 bytes); a.txt and sub from the application
        Assert.Equal([("a.txt", 1L), ("B.TXT", 5L), ("new.txt", 3L), ("sub", 0L)], listed);
    }

    [Fact]
    public void TheRootAndADirectoryOfOneLayerOnlyOpen()
    {
        Install();

        Assert.Equal(["base", "data", "upd"], List("app0:").Select(e => e.Name));
        Assert.Equal([("only.txt", 4L)], List("app0:base"));
        Assert.Equal([("only.txt", 3L)], List("app0:/upd/"));
        Assert.Equal([("c.txt", 1L)], List("app0:data/sub"));
        Assert.Equal(NoEnt, Io.sceIoDopen("app0:none"));
        // a file is no directory, in either layer
        Assert.Equal(NoEnt, Io.sceIoDopen("app0:data/new.txt"));
        Assert.Equal(NoEnt, Io.sceIoDopen("app0:data/a.txt"));
    }

    [Fact]
    public void WithoutAnUpdateApp0ListsTheApplicationAlone()
    {
        string app = FirmwareLayout.Applications.Host(_h.Root, "PCSE99902");
        Write(app, "data/a.txt", "a");
        _h.Emu.Vfs.Mount("app0", app);
        Assert.Null(_h.Emu.Vfs.OverlayApp0Patch(app, "PCSE99902"));

        Assert.Equal([("a.txt", 1L)], List("app0:data"));
        Assert.Equal(NoEnt, Io.sceIoDopen("app0:none"));
    }

    [Fact]
    public void OtherDevicesKeepTheirOwnFolder()
    {
        (string app, _) = Install();
        // ux0:app/<id> is the application's folder as it is, without the update
        Assert.Equal([("a.txt", 1L), ("b.txt", 7L), ("sub", 0L)], List("ux0:app/PCSE99901/data"));
        Assert.True(Directory.Exists(app));
    }
}
