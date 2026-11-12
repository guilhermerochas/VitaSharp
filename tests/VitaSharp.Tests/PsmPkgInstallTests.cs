using System.IO.Compression;
using System.Text;
using VitaSharp.Core.Package;

namespace VitaSharp.Tests;

/// <summary>PlayStation Mobile packages (content type 0x18) install like the console lays them out: ux0:psm/&lt;id&gt;
/// with RO (the package's content/ items and the license), RW (Documents, Temp, System/content_id, pm.dat).
/// Synthetic package and license only.</summary>
public class PsmPkgInstallTests
{
    private const string ContentId = "EP0000-NPXX99999_00-0000000000000001";
    private const string TitleId = "NPXX99999";

    /// <summary>A synthetic 1024-byte PSM license for <paramref name="contentId"/> (content ID at 0x50).</summary>
    private static byte[] PsmLicense(string contentId)
    {
        byte[] rif = new byte[PsmPkgInstaller.RifSize];
        rif[1] = 1;
        Encoding.ASCII.GetBytes(contentId).CopyTo(rif, PsmPkgInstaller.RifContentIdOffset);
        for (int i = 0; i < 16; i++)
        {
            rif[0x100 + i] = (byte)(0x40 + i);
        }

        return rif;
    }

    /// <summary>The license as a zRIF (zlib without the preset dictionary, which the format allows).</summary>
    private static string ToZrif(byte[] rif)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(rif);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    private static byte[] Package(string contentId = ContentId) => PkgFixture.BuildPsm(contentId,
        ("content", null),
        ("content/Application", null),
        ("content/Application/app.exe", Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray()),
        ("content/Application/app.info", "PSMA"u8.ToArray()),
        ("runtime", null),
        ("runtime/Unity.dll", [1, 2, 3]));

    [Fact]
    public void InstallsUnderPsmLikeTheConsole()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("vitasharp-psm-");
        try
        {
            string pkg = Path.Combine(dir.FullName, "psm.pkg");
            File.WriteAllBytes(pkg, Package());
            string ux0 = Path.Combine(dir.FullName, "ux0");
            Directory.CreateDirectory(ux0);
            File.WriteAllBytes(Path.Combine(ux0, "id.dat"), [1]);
            byte[] rif = PsmLicense(ContentId);

            PkgInfo info = PkgInstaller.Inspect(pkg);
            Assert.Equal(PkgKind.VitaPsm, info.Kind);
            Assert.True(info.NeedsLicense);
            Assert.Equal(TitleId, info.TitleId);

            InstalledTitle t = PkgInstaller.Install(pkg, ToZrif(rif), ux0);

            string root = Path.Combine(ux0, "psm", TitleId);
            Assert.Equal(TitleId, t.TitleId);
            Assert.Equal(PkgContentType.Psm, t.Type);
            Assert.Equal(PkgKind.VitaPsm, t.Kind);
            Assert.Equal(root, t.Path);
            Assert.Equal(Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray(),
                         File.ReadAllBytes(Path.Combine(root, "RO", "Application", "app.exe")));
            Assert.Equal("PSMA"u8.ToArray(), File.ReadAllBytes(Path.Combine(root, "RO", "Application", "app.info")));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(root, "runtime", "Unity.dll")));
            Assert.False(Directory.Exists(Path.Combine(root, "RO", "content")));
            Assert.Equal(rif, File.ReadAllBytes(Path.Combine(root, "RO", "License", PkgInstaller.AccountRifName)));
            Assert.True(Directory.Exists(Path.Combine(root, "RW", "Documents")));
            Assert.True(Directory.Exists(Path.Combine(root, "RW", "Temp")));
            byte[] contentId = File.ReadAllBytes(Path.Combine(root, "RW", "System", "content_id"));
            Assert.Equal(0x30, contentId.Length);
            Assert.Equal(ContentId, Encoding.ASCII.GetString(contentId).TrimEnd('\0'));
            Assert.Equal(1 << 16, new FileInfo(Path.Combine(root, "RW", "System", "pm.dat")).Length);
            // the shell rebuilds its database (and finds ux0:psm) at its next start
            Assert.False(File.Exists(Path.Combine(ux0, "id.dat")));
            // nothing goes to the PS Vita application folders
            Assert.False(Directory.Exists(Path.Combine(ux0, "app")));
            Assert.False(Directory.Exists(Path.Combine(ux0, "license")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void ReinstallKeepsSavedData()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("vitasharp-psm-");
        try
        {
            string pkg = Path.Combine(dir.FullName, "psm.pkg");
            File.WriteAllBytes(pkg, Package());
            string ux0 = Path.Combine(dir.FullName, "ux0");
            byte[] rif = PsmLicense(ContentId);
            PkgInstaller.Install(pkg, rif, ux0);
            string root = Path.Combine(ux0, "psm", TitleId);
            File.WriteAllText(Path.Combine(root, "RW", "Documents", "save.dat"), "progress");
            File.WriteAllText(Path.Combine(root, "RO", "Application", "stale.bin"), "old");

            PkgInstaller.Install(pkg, rif, ux0);

            Assert.Equal("progress", File.ReadAllText(Path.Combine(root, "RW", "Documents", "save.dat")));
            Assert.False(File.Exists(Path.Combine(root, "RO", "Application", "stale.bin")));
            Assert.True(File.Exists(Path.Combine(root, "RO", "Application", "app.exe")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void RefusesAPsVitaLicense()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("vitasharp-psm-");
        try
        {
            string pkg = Path.Combine(dir.FullName, "psm.pkg");
            File.WriteAllBytes(pkg, Package());
            string ux0 = Path.Combine(dir.FullName, "ux0");
            byte[] vitaRif = new byte[Zrif.RifSize];
            Encoding.ASCII.GetBytes(ContentId).CopyTo(vitaRif, 0x10);

            InvalidDataException e = Assert.Throws<InvalidDataException>(() => PkgInstaller.Install(pkg, vitaRif, ux0));

            Assert.Contains("PSM license", e.Message);
            Assert.False(Directory.Exists(Path.Combine(ux0, "psm")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void RefusesTheLicenseOfAnotherTitle()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("vitasharp-psm-");
        try
        {
            string pkg = Path.Combine(dir.FullName, "psm.pkg");
            File.WriteAllBytes(pkg, Package());
            string ux0 = Path.Combine(dir.FullName, "ux0");
            const string Other = "EP0000-NPXX99998_00-0000000000000002";

            InvalidDataException e = Assert.Throws<InvalidDataException>(() => PkgInstaller.Install(pkg, PsmLicense(Other), ux0));

            Assert.Contains(Other, e.Message);
            Assert.False(Directory.Exists(Path.Combine(ux0, "psm")));
            Assert.Throws<LicenseRequiredException>(() => PkgInstaller.InstallWithoutLicense(pkg, ux0));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void RefusesItemNamesThatLeaveTheTitleFolder()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("vitasharp-psm-");
        try
        {
            string pkg = Path.Combine(dir.FullName, "psm.pkg");
            File.WriteAllBytes(pkg, PkgFixture.BuildPsm(ContentId, ("content/../../evil.bin", [1])));
            string ux0 = Path.Combine(dir.FullName, "ux0");

            Assert.Throws<InvalidDataException>(() => PkgInstaller.Install(pkg, PsmLicense(ContentId), ux0));

            Assert.False(File.Exists(Path.Combine(ux0, "evil.bin")));
            Assert.False(File.Exists(Path.Combine(ux0, "psm", "evil.bin")));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
