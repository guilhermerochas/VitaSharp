using System.Security.Cryptography;
using VitaSharp.Core.Package;
using Item = VitaSharp.Tests.PspPkgFixture.Item;

namespace VitaSharp.Tests;

/// <summary>PSP / PSone Classic packages: detection, license-free extraction and the ux0:pspemu layout.</summary>
public class PspPkgInstallTests
{
    const string PspContent = "UP0001-NPUZ00001_00-0000000000000001";
    const string PsxContent = "UP9000-NPUJ00001_00-0000000000000002";

    static readonly byte[] Icon = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    static (byte[] Pkg, byte[] Eboot) PspGame(uint contentType = 7, string category = "EG")
    {
        var eboot = PspPkgFixture.Pbp("ULUS99999", "Fixture Game", PspPkgFixture.Psar("NPUMDIMG"));
        var pkg = PspPkgFixture.Build(PspContent, contentType,
        [
            new Item("PARAM.SFO", PspPkgFixture.OuterSfo("Fixture Game", "NPUZ00001", category), 0),
            new Item("ICON0.PNG", Icon, 0),
            new Item("USRDIR", null, 0),
            new Item("USRDIR/CONTENT", null, 0x90),
            new Item("USRDIR/CONTENT/EBOOT.PBP", eboot),
            new Item("USRDIR/CONTENT/DOCUMENT.DAT", [1, 2, 3, 4, 5]),
            new Item("USRDIR/CONTENT/PSP-KEY.EDAT", [9, 8, 7]),
        ]);
        return (pkg, eboot);
    }

    static (DirectoryInfo Dir, string Pkg, string Ux0) Write(byte[] pkg)
    {
        var dir = Directory.CreateTempSubdirectory("vitasharp-psppkg-");
        var path = Path.Combine(dir.FullName, "game.pkg");
        File.WriteAllBytes(path, pkg);
        return (dir, path, Path.Combine(dir.FullName, "ux0"));
    }

    [Theory]
    [InlineData(6u, "ME", null, PkgKind.Psx)]
    [InlineData(7u, "EG", null, PkgKind.Psp)]
    [InlineData(7u, "HG", null, PkgKind.PspPcEngine)]
    [InlineData(7u, "EG", "ULUS99999", PkgKind.PspDlc)]
    [InlineData(9u, "EG", null, PkgKind.PspTheme)]
    [InlineData(0xEu, "EG", null, PkgKind.PspGo)]
    [InlineData(0xFu, "EG", null, PkgKind.PspMini)]
    [InlineData(0x10u, "EG", null, PkgKind.PspNeoGeo)]
    public void DetectsPspAndPsoneKinds(uint contentType, string category, string? installDir, PkgKind expected)
    {
        var pkg = PspPkgFixture.Build(PspContent, contentType,
            [new Item("PARAM.SFO", PspPkgFixture.OuterSfo("Kind Test", "NPUZ00001", category), 0)], installDir);
        var info = PkgInstaller.Inspect(new MemoryStream(pkg));
        Assert.Equal(expected, info.Kind);
        Assert.False(info.NeedsLicense);
        Assert.True(expected.IsPspEmu());
        Assert.Equal("Kind Test", info.Title);
        Assert.Equal(PspContent, info.ContentId);
    }

    [Fact]
    public void DetectsVitaKindsAndTheyNeedALicense()
    {
        var info = PkgInstaller.Inspect(new MemoryStream(PkgFixture.Build("EP0001-PCSE00000_00-0000000000000001", "Vita", ("eboot.bin", new byte[16]))));
        Assert.Equal(PkgKind.VitaApp, info.Kind);
        Assert.True(info.NeedsLicense);
        foreach (var k in new[] { PkgKind.VitaApp, PkgKind.VitaPatch, PkgKind.VitaDlc, PkgKind.VitaTheme, PkgKind.VitaPsm })
        {
            Assert.True(k.NeedsLicense());
        }
    }

    [Fact]
    public void VitaPackageWithoutLicenseIsRefused()
    {
        var (dir, pkg, ux0) = Write(PkgFixture.Build("EP0001-PCSE00000_00-0000000000000001", "Vita", ("eboot.bin", new byte[16])));
        try
        {
            var e = Assert.Throws<LicenseRequiredException>(() => PkgInstaller.InstallWithoutLicense(pkg, ux0));
            Assert.Contains("license", e.Message);
            Assert.False(Directory.Exists(ux0));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void InstallsPspGameWithoutLicense()
    {
        var (bytes, eboot) = PspGame();
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            Directory.CreateDirectory(ux0);
            File.WriteAllBytes(Path.Combine(ux0, "id.dat"), [1]);
            var t = PkgInstaller.InstallWithoutLicense(pkg, ux0);

            Assert.Equal("ULUS99999", t.TitleId); // the EBOOT's DISC_ID names the folder
            Assert.Equal(PkgKind.Psp, t.Kind);
            Assert.Equal(PkgContentType.Psp, t.Type);
            string game = Path.Combine(ux0, "pspemu", "PSP", "GAME", "ULUS99999");
            Assert.Equal(game, t.Path);
            Assert.Equal(eboot, File.ReadAllBytes(Path.Combine(game, "EBOOT.PBP")));
            Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(game, "DOCUMENT.DAT")));
            Assert.Equal([9, 8, 7], File.ReadAllBytes(Path.Combine(game, "PSP-KEY.EDAT")));
            Assert.False(File.Exists(Path.Combine(game, "PARAM.SFO"))); // the PS3 wrapper stays out
            Assert.False(Directory.Exists(Path.Combine(ux0, "license")));
            Assert.False(Directory.Exists(Path.Combine(ux0, "pspemu", "PSP", "LICENSE")));

            var sig = File.ReadAllBytes(Path.Combine(game, "__sce_ebootpbp"));
            Assert.Equal(PspEbootSignature.Size, sig.Length);
            Assert.Equal(PspEbootSignature.KindPsp, PspEbootSignature.Verify("ux0:pspemu/PSP/GAME/ULUS99999/EBOOT.PBP", sig));
            Assert.Equal(SHA256.HashData(eboot), sig.AsSpan(0x30, 0x20).ToArray());

            Assert.False(Directory.Exists(Path.Combine(ux0, "pspemu", "ISO"))); // nothing of the game is decrypted
            Assert.False(File.Exists(Path.Combine(ux0, "id.dat"))); // the shell rebuilds its database and lists the title
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void MinisInstallTheSameWay()
    {
        var (bytes, eboot) = PspGame(contentType: 0xF);
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            var t = PkgInstaller.InstallWithoutLicense(pkg, ux0);
            Assert.Equal(PkgKind.PspMini, t.Kind);
            Assert.Equal(eboot, File.ReadAllBytes(Path.Combine(ux0, "pspemu", "PSP", "GAME", "ULUS99999", "EBOOT.PBP")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    /// <summary>A license-shaped test file (a header and the content id at 0x10; not a real license).</summary>
    static byte[] TestRif(string contentId)
    {
        var rif = new byte[0x98];
        System.Text.Encoding.ASCII.GetBytes(contentId).CopyTo(rif, 0x10);
        for (int i = 0x40; i < rif.Length; i++)
        {
            rif[i] = (byte)i;
        }

        return rif;
    }

    [Fact]
    public void InstallsTheUsersOwnLicenseAndActivation()
    {
        var (bytes, _) = PspGame();
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            var rif = TestRif(PspContent);
            var act = Enumerable.Range(0, 0x1040).Select(i => (byte)i).ToArray();
            PkgInstaller.InstallWithoutLicense(pkg, ux0, new PspInstallOptions { License = rif, ActDat = act });
            // named after the eboot's content id (the fixture's eboot has none: the package's)
            Assert.Equal(rif, File.ReadAllBytes(Path.Combine(ux0, "pspemu", "PSP", "LICENSE", PspContent + ".rif")));
            Assert.Equal(act, File.ReadAllBytes(Path.Combine(dir.FullName, "tm0", "npdrm", "act.dat")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void LicenseOfAnotherTitleIsRefused()
    {
        var (bytes, _) = PspGame();
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            var e = Assert.Throws<InvalidDataException>(() => PkgInstaller.InstallWithoutLicense(pkg, ux0,
                new PspInstallOptions { License = TestRif("UP0001-NPUZ99999_00-0000000000000009") }));
            Assert.Contains(PspContent, e.Message);
            Assert.False(Directory.Exists(Path.Combine(ux0, "pspemu", "PSP", "GAME")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void PspPackageGivenALicenseIgnoresIt()
    {
        var (bytes, eboot) = PspGame();
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            var t = PkgInstaller.Install(pkg, "KO5ifR1dg/Fh7nENQDbVAGy/ASowxOc/AUEhYRFRMXEJSSlpGVk5+dEYH9wAAJu/CQk=", ux0);
            Assert.Equal(PkgKind.Psp, t.Kind);
            Assert.Equal(eboot, File.ReadAllBytes(Path.Combine(t.Path, "EBOOT.PBP")));
            Assert.False(Directory.Exists(Path.Combine(ux0, "license")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void InstallsPsoneClassicAsShipped()
    {
        var eboot = PspPkgFixture.Pbp("SLUS99999", "Fixture One", PspPkgFixture.Psar("PSISOIMG0000"), "ME");
        var texture = new byte[] { 7, 7, 7 };
        var bytes = PspPkgFixture.Build(PsxContent, 6,
        [
            new Item("PARAM.SFO", PspPkgFixture.OuterSfo("Fixture One", "NPUJ00001", "ME"), 0),
            new Item("USRDIR/CONTENT/EBOOT.PBP", eboot),
            new Item("USRDIR/CONTENT/DOCUMENT.DAT", [4, 4]),
            new Item("USRDIR/CONTENT/texture.enc", texture),
        ]);
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            var t = PkgInstaller.InstallWithoutLicense(pkg, ux0);
            Assert.Equal(PkgKind.Psx, t.Kind);
            Assert.Equal("SLUS99999", t.TitleId);
            string game = Path.Combine(ux0, "pspemu", "PSP", "GAME", "SLUS99999");
            Assert.Equal(eboot, File.ReadAllBytes(Path.Combine(game, "EBOOT.PBP")));
            Assert.Equal([4, 4], File.ReadAllBytes(Path.Combine(game, "DOCUMENT.DAT")));
            Assert.False(File.Exists(Path.Combine(game, "KEYS.BIN"))); // no key is derived from the game
            Assert.Equal(texture, File.ReadAllBytes(Path.Combine(ux0, "ps1emu", "SLUS99999", "texture.enc")));
            Assert.False(File.Exists(Path.Combine(game, "texture.enc")));
            Assert.Equal(PspEbootSignature.KindPs1, PspEbootSignature.Verify("EBOOT.PBP", File.ReadAllBytes(Path.Combine(game, "__sce_ebootpbp"))));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void InstallsPsoneClassicInThePs3PackageFormat()
    {
        var eboot = PspPkgFixture.Pbp("SCUS99998", "Fixture PS3 Format", PspPkgFixture.Psar("PSISOIMG0000"), "ME");
        // content type 1 with the files under <ID>/ and no PARAM.SFO of its own (lusid1/pkg2zip's "PS3" PSone packages)
        var bytes = PspPkgFixture.Build(PsxContent, 1,
        [
            new Item("SCUS99998", null, 0x80),
            new Item("SCUS99998/EBOOT.PBP", eboot, 0x80),
            new Item("SCUS99998/DOCUMENT.DAT", [3, 3], 0x80),
        ], ps3Format: true);
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            var info = PkgInstaller.Inspect(pkg);
            Assert.Equal(PkgKind.Psx, info.Kind);
            Assert.Equal("Fixture PS3 Format", info.Title);
            var t = PkgInstaller.InstallWithoutLicense(pkg, ux0);
            Assert.Equal("SCUS99998", t.TitleId);
            Assert.Equal("Fixture PS3 Format", t.Title);
            Assert.Equal(eboot, File.ReadAllBytes(Path.Combine(t.Path, "EBOOT.PBP")));
            Assert.Equal([3, 3], File.ReadAllBytes(Path.Combine(t.Path, "DOCUMENT.DAT")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void InstallsPspAddOnIntoItsGameFolder()
    {
        var bytes = PspPkgFixture.Build("UP0001-NPUZ00002_00-DLC0000000000001", 7,
        [
            new Item("PARAM.SFO", PspPkgFixture.OuterSfo("Fixture DLC", "NPUZ00002"), 0),
            new Item("USRDIR/CONTENT/EXTRA", null),
            new Item("USRDIR/CONTENT/EXTRA/PACK1.EDAT", [5, 6]),
        ], installDir: "ULUS99999");
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            var t = PkgInstaller.InstallWithoutLicense(pkg, ux0);
            Assert.Equal(PkgKind.PspDlc, t.Kind);
            Assert.Equal("ULUS99999", t.TitleId);
            string game = Path.Combine(ux0, "pspemu", "PSP", "GAME", "ULUS99999");
            Assert.Equal([5, 6], File.ReadAllBytes(Path.Combine(game, "EXTRA", "PACK1.EDAT")));
            Assert.False(File.Exists(Path.Combine(game, "__sce_ebootpbp")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void ItemNamesThatLeaveTheGameFolderAreRefused()
    {
        var bytes = PspPkgFixture.Build(PspContent, 7,
        [
            new Item("USRDIR/CONTENT/EBOOT.PBP", PspPkgFixture.Pbp("ULUS99999", "Bad", new byte[16])),
            new Item("USRDIR/CONTENT/../../evil.txt", [1]),
        ]);
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            Assert.Throws<InvalidDataException>(() => PkgInstaller.InstallWithoutLicense(pkg, ux0));
            Assert.False(File.Exists(Path.Combine(ux0, "pspemu", "evil.txt")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void PspGameWithoutEbootIsAClearError()
    {
        var bytes = PspPkgFixture.Build(PspContent, 7, [new Item("PARAM.SFO", PspPkgFixture.OuterSfo("Empty", "NPUZ00001"), 0)]);
        var (dir, pkg, ux0) = Write(bytes);
        try
        {
            var e = Assert.Throws<InvalidDataException>(() => PkgInstaller.InstallWithoutLicense(pkg, ux0));
            Assert.Contains("EBOOT.PBP", e.Message);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void NotAPackageIsAClearError()
    {
        var e = Assert.Throws<InvalidDataException>(() => PkgInstaller.Inspect(new MemoryStream(new byte[0x200])));
        Assert.Contains("Not a PKG", e.Message);
    }
}
