using VitaSharp.Core.Io;
using VitaSharp.Core.Package;

namespace VitaSharp.Tests;

/// <summary>
/// How the console handles an installed update (FW 3.74 appmgr, the mount of app0 and its patch; shell.self): the
/// update lives in ux0:patch/&lt;id&gt; for an installed application and grw0:patch/&lt;id&gt; for a game card's, the
/// firmware's own applications take none, and its files (sce_sys/param.sfo too) replace the application's on app0, so
/// the installed version is the update's APP_VER everywhere it is read. A Unity PSM title's own runtime lives in
/// ux0:psm/&lt;id&gt;/runtime with its licence in ux0:license/app/&lt;id&gt;. Synthetic content only.
/// </summary>
public sealed class UpdateModelTests : IDisposable
{
    private readonly string _root;
    private readonly string _fs;

    public UpdateModelTests()
    {
        _root = Directory.CreateTempSubdirectory("vitasharp-update-").FullName;
        _fs = Path.Combine(_root, "fs");
        Directory.CreateDirectory(_fs);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>An application folder (eboot.bin, its param.sfo and icon) of <paramref name="folder"/>.</summary>
    private string App(FirmwareFolder folder, string id, string title, string version, string category = "gd")
    {
        string dir = folder.Host(_fs, id);
        Directory.CreateDirectory(Path.Combine(dir, "sce_sys"));
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), [0x7F, (byte)'E', (byte)'L', (byte)'F', 1]);
        DesktopLogicTests.WriteSfo(Path.Combine(dir, "sce_sys", "param.sfo"), ("APP_VER", version), ("CATEGORY", category), ("TITLE", title), ("TITLE_ID", id));
        File.WriteAllBytes(Path.Combine(dir, "sce_sys", "icon0.png"), [0x89, (byte)'P', (byte)'N', (byte)'G', 1]);
        return dir;
    }

    /// <summary>An update of <paramref name="id"/> in <paramref name="folder"/> (CATEGORY gp, a new eboot.bin).</summary>
    private string Update(FirmwareFolder folder, string id, string title, string version)
    {
        string dir = App(folder, id, title, version, "gp");
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), [0x7F, (byte)'E', (byte)'L', (byte)'F', 2]);
        File.WriteAllBytes(Path.Combine(dir, "sce_sys", "icon0.png"), [0x89, (byte)'P', (byte)'N', (byte)'G', 2]);
        return dir;
    }

    [Theory]
    [InlineData("ux0:app/PCSE99901", "ux0:patch/PCSE99901")]
    [InlineData("gro0:app/PCSE99901", "grw0:patch/PCSE99901")]
    [InlineData("vs0:app/PCSE99901", null)]
    [InlineData("ux0:data/PCSE99901", null)]
    [InlineData("host0:PCSE99901", null)]
    [InlineData("ux0:psm/PCSE99901/runtime", null)]
    public void TheUpdateFolderIsWhereAppmgrTakesIt(string app, string? patch) =>
        Assert.Equal(patch, FirmwareLayout.PatchFolderOf(app, "PCSE99901"));

    [Fact]
    public void App0ShowsTheUpdateOverTheApplication()
    {
        string app = App(FirmwareLayout.Applications, "PCSE99901", "Game", "01.00");
        File.WriteAllText(Path.Combine(app, "data.txt"), "own");
        string patch = Update(FirmwareLayout.Patches, "PCSE99901", "Game v2", "02.01");
        var vfs = new Vfs(_fs);
        vfs.Mount("app0", app);

        Sfo? shown = vfs.OverlayApp0Patch(app, "PCSE99901");

        Assert.NotNull(shown);
        Assert.Equal("02.01", shown.AppVersion);
        Assert.Equal(Path.Combine(patch, "eboot.bin"), vfs.Resolve("app0:eboot.bin"));
        Assert.Equal(Path.Combine(patch, "sce_sys", "param.sfo"), vfs.Resolve("app0:sce_sys/param.sfo"));
        // what the update does not have still comes from the application
        Assert.Equal(Path.Combine(app, "data.txt"), vfs.Resolve("app0:data.txt"));
    }

    [Fact]
    public void AGameCardApplicationTakesItsUpdateFromTheCardsWritableArea()
    {
        string app = App(FirmwareLayout.GameCardApplications, "PCSE99902", "Card Game", "01.00");
        // an update in ux0:patch is not the card application's
        Update(FirmwareLayout.Patches, "PCSE99902", "Wrong", "09.00");
        string patch = Update(FirmwareLayout.GameCardPatches, "PCSE99902", "Card Game", "01.05");
        var vfs = new Vfs(_fs);
        vfs.Mount("app0", app);

        Assert.Equal("01.05", vfs.OverlayApp0Patch(app, "PCSE99902")?.AppVersion);
        Assert.Equal(Path.Combine(patch, "eboot.bin"), vfs.Resolve("app0:eboot.bin"));
    }

    [Fact]
    public void TheFirmwaresOwnApplicationsTakeNoUpdate()
    {
        string app = App(FirmwareLayout.SystemApplications, "NPXS99903", "System App", "01.00");
        Update(FirmwareLayout.Patches, "NPXS99903", "System App", "02.00");
        var vfs = new Vfs(_fs);
        vfs.Mount("app0", app);

        Assert.Null(vfs.OverlayApp0Patch(app, "NPXS99903"));
        Assert.Equal(Path.Combine(app, "eboot.bin"), vfs.Resolve("app0:eboot.bin"));
    }

    [Fact]
    public void TheLibraryShowsTheUpdatesVersionAndIconButTheApplicationsKind()
    {
        App(FirmwareLayout.Applications, "PCSE99904", "Game", "01.00");
        string patch = Update(FirmwareLayout.Patches, "PCSE99904", "Game Remastered", "02.01");

        LibraryTitle title = Assert.Single(InstalledContent.Scan(_fs));

        Assert.Equal("2.01", title.Version);
        Assert.Equal("Game Remastered", title.Title);
        Assert.Equal("gd", title.Category);
        Assert.Equal(Path.Combine(patch, "sce_sys", "icon0.png"), title.IconPath);
    }

    [Fact]
    public void ATitlesNameComesFromItsUpdateFirst()
    {
        App(FirmwareLayout.Applications, "PCSE99905", "Old Name", "01.00");
        Update(FirmwareLayout.Patches, "PCSE99905", "New Name", "01.01");

        Assert.Equal("New Name", InstalledContent.TitleName(guest => FirmwareLayout.GuestToHost(_fs, guest), "PCSE99905"));
    }

    // ------------------------------------------------------------------ a Unity PSM title's own runtime

    /// <summary>A Unity PSM title as earlier installers left it: its runtime (with its licence) inside RO.</summary>
    private string UnityTitleWithRuntimeInRo(string id, byte[] runtimeLicence)
    {
        string title = FirmwareLayout.PsmTitles.Host(_fs, id);
        string runtime = Path.Combine(title, FirmwareLayout.PsmReadOnly, FirmwareLayout.PsmOwnRuntime);
        Directory.CreateDirectory(Path.Combine(runtime, "sce_sys", "package"));
        Directory.CreateDirectory(Path.Combine(title, FirmwareLayout.PsmReadOnly, "Application"));
        File.WriteAllBytes(Path.Combine(runtime, "eboot.bin"), [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        DesktopLogicTests.WriteSfo(Path.Combine(runtime, "sce_sys", "param.sfo"), ("APP_VER", "01.02"), ("CATEGORY", "gd"), ("TITLE_ID", "PCSI99910"));
        File.WriteAllBytes(Path.Combine(runtime, FirmwareLayout.PsmOwnRuntimeLicense), runtimeLicence);
        return title;
    }

    [Fact]
    public void AUnityTitlesRuntimeLeftInRoMovesWhereTheConsoleKeepsIt()
    {
        byte[] licence = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        string title = UnityTitleWithRuntimeInRo("NPXX99911", licence);
        string ux0 = Path.Combine(_fs, "ux0");
        File.WriteAllBytes(Path.Combine(ux0, FirmwareLayout.CardIdFile), [1]);

        Assert.Equal(1, PsmLayoutMigration.Run(_fs));

        string runtime = Path.Combine(title, FirmwareLayout.PsmOwnRuntime);
        Assert.True(File.Exists(Path.Combine(runtime, "sce_sys", "param.sfo")));
        Assert.False(Directory.Exists(Path.Combine(title, FirmwareLayout.PsmReadOnly, FirmwareLayout.PsmOwnRuntime)));
        Assert.Equal(licence, File.ReadAllBytes(FirmwareLayout.ApplicationLicenses.Host(_fs, "NPXX99911", PkgInstaller.AccountRifName)));
        // the shell registers the title again (now as a Unity one) at its next start
        Assert.False(File.Exists(Path.Combine(ux0, FirmwareLayout.CardIdFile)));
        // the bubble starts the title's own runtime, whose licence is the PSM title's
        Assert.Equal((runtime, "eboot.bin", "ux0:psm/NPXX99911"), InstalledContent.PsmLaunch(_fs, title));
        Assert.Equal("NPXX99911", FirmwareLayout.PsmTitleOfOwnRuntime(_fs, runtime));
        // once: nothing left to move
        Assert.Equal(0, PsmLayoutMigration.Run(_fs));
    }

    [Fact]
    public void AStoredRuntimeLicenceIsKept()
    {
        string title = UnityTitleWithRuntimeInRo("NPXX99912", [1, 2, 3]);
        string stored = FirmwareLayout.ApplicationLicenses.Host(_fs, "NPXX99912", PkgInstaller.AccountRifName);
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        File.WriteAllBytes(stored, [9]);

        Assert.Equal(1, PsmLayoutMigration.Run(_fs));

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(stored));
        Assert.True(Directory.Exists(Path.Combine(title, FirmwareLayout.PsmOwnRuntime)));
    }

    [Fact]
    public void OnlyAPsmTitlesRuntimeFolderIsItsOwnRuntime()
    {
        Assert.Null(FirmwareLayout.PsmTitleOfOwnRuntime(_fs, FirmwareLayout.Applications.Host(_fs, "PCSE99901")));
        Assert.Null(FirmwareLayout.PsmTitleOfOwnRuntime(_fs, FirmwareLayout.PsmTitles.Host(_fs, "NPXX99913")));
        Assert.Null(FirmwareLayout.PsmTitleOfOwnRuntime(_fs, Path.Combine(FirmwareLayout.Applications.Host(_fs, "PCSE99901"), FirmwareLayout.PsmOwnRuntime)));
        Assert.Equal("NPXX99913", FirmwareLayout.PsmTitleOfOwnRuntime(_fs, FirmwareLayout.PsmTitles.Host(_fs, "NPXX99913", FirmwareLayout.PsmOwnRuntime)));
    }
}
