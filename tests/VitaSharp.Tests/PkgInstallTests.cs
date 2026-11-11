using VitaSharp.Core.Package;

namespace VitaSharp.Tests;

public class PkgInstallTests
{
    // the fake license of ZrifTests (content ID EP0001-PCSE00000_00-0000000000000001)
    const string Zrif = "KO5ifR1dg/Fh7nENQDbVAGy/ASowxOc/AUEhYRFRMXEJSSlpGVk5+dEYH9wAAJu/CQk=";
    const string ContentId = "EP0001-PCSE00000_00-0000000000000001";

    [Fact]
    public void InstallsPackageWithZrifLicense()
    {
        var dir = Directory.CreateTempSubdirectory("vitasharp-pkg-");
        try
        {
            var eboot = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
            var pkg = Path.Combine(dir.FullName, "game.pkg");
            File.WriteAllBytes(pkg, PkgFixture.Build(ContentId, "Pkg Test", ("eboot.bin", eboot)));
            var ux0 = Path.Combine(dir.FullName, "ux0");

            var t = PkgInstaller.Install(pkg, Zrif, ux0);

            Assert.Equal("PCSE00000", t.TitleId);
            Assert.Equal("Pkg Test", t.Title);
            Assert.Equal(PkgContentType.App, t.Type);
            Assert.Equal(eboot, File.ReadAllBytes(Path.Combine(ux0, "app", "PCSE00000", "eboot.bin")));
            var rif = Core.Package.Zrif.Decode(Zrif);
            Assert.Equal(rif, File.ReadAllBytes(Path.Combine(ux0, "license", "PCSE00000", ContentId + ".rif")));
            Assert.Equal(rif, File.ReadAllBytes(Path.Combine(ux0, "app", "PCSE00000", "sce_sys", "package", "work.bin")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void RejectsLicenseOfAnotherPackage()
    {
        var dir = Directory.CreateTempSubdirectory("vitasharp-pkg-");
        try
        {
            var pkg = Path.Combine(dir.FullName, "other.pkg");
            File.WriteAllBytes(pkg, PkgFixture.Build("EP0001-PCSE00001_00-0000000000000002", "Other", ("eboot.bin", new byte[16])));
            var e = Assert.Throws<InvalidDataException>(() => PkgInstaller.Install(pkg, Zrif, Path.Combine(dir.FullName, "ux0")));
            Assert.Contains(ContentId, e.Message);
            Assert.False(Directory.Exists(Path.Combine(dir.FullName, "ux0", "license")));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
