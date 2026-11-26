using VitaSharp.Core.Firmware;
using VitaSharp.Mobile.Core.Firmware;

namespace VitaSharp.Tests;

/// <summary>
/// A new installation from the system update alone (no sa0, as every PSVUPDAT.PUP) would boot the Vita system into
/// a first-time setup without text: the state reports the missing fonts, so the front-ends (desktop launcher, Android)
/// warn and offer the font package (or open it anyway); once it is installed sa0:data/font/pgf and pvf are there
/// before the first boot.
/// </summary>
public class FirmwareFontsBootTests
{
    private static byte[] Bytes(int n, int seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 31 + seed)).ToArray();

    private static string NewTempRoot()
    {
        string d = Path.Combine(Path.GetTempPath(), "vitasharp-fwboot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A system update as Sony ships it: os0 and vs0 (with the shell), no sa0.</summary>
    private static byte[] SystemUpdatePup() => FirmwareTests.BuildPup(
        (0x100, "3.74\n"u8.ToArray()),
        (0x303, FirmwareTests.BuildSpkg(SpkgType.Os0, FirmwareTests.BuildFat12(new()
        {
            ["psp2bootconfig.skprx"] = Bytes(700, 1)
        }))),
        (0x304, FirmwareTests.BuildSpkg(SpkgType.Vs0, FirmwareTests.BuildFat12(new()
        {
            ["sys/external/libSceXml.suprx"] = Bytes(300, 2),
            ["vsh/shell/shell.self"] = Bytes(500, 3),
        }))));

    /// <summary>The firmware font package: sa0 only, with the PGF and PVF fonts.</summary>
    private static byte[] FontPackagePup() => FirmwareTests.BuildPup(
        (0x306, FirmwareTests.BuildSpkg(SpkgType.Sa0, FirmwareTests.BuildFat12(new()
        {
            ["data/font/pgf/ltn0.pgf"] = Bytes(400, 4),
            ["data/font/pvf/jpn0.pvf"] = Bytes(400, 5),
            ["data/font/pvf/ltn0.pvf"] = Bytes(400, 6),
        }))));

    [Fact]
    public void SystemUpdateAloneAsksForTheFontPackageBeforeTheFirstBoot()
    {
        string tmp = NewTempRoot();
        try
        {
            string fsRoot = Path.Combine(tmp, "fs");
            string update = Path.Combine(tmp, "PSVUPDAT.PUP");
            File.WriteAllBytes(update, SystemUpdatePup());
            FirmwareInfo info = FirmwareInstaller.Install(update, fsRoot);

            Assert.True(File.Exists(Path.Combine(fsRoot, "vs0", "vsh", "shell", "shell.self")));
            Assert.False(info.FontPackage);
            Assert.Equal(["pgf", "pvf"], FirmwareInfo.MissingSystemFonts(fsRoot));
            Assert.False(FirmwareInfo.SystemReady(fsRoot)); // the launchers warn and offer the font package
            var status = FirmwareStatus.Read(fsRoot);
            Assert.True(status.Shell);
            Assert.True(status.FontsNeeded);   // Android: firmware screen note, launch card, post-install question
            Assert.False(status.SystemReady);

            string fonts = Path.Combine(tmp, "FONT.PUP");
            File.WriteAllBytes(fonts, FontPackagePup());
            FirmwareInfo withFonts = FirmwareInstaller.Install(fonts, fsRoot);

            Assert.True(File.Exists(Path.Combine(fsRoot, "sa0", "data", "font", "pvf", "jpn0.pvf")));
            Assert.True(File.Exists(Path.Combine(fsRoot, "sa0", "data", "font", "pgf", "ltn0.pgf")));
            Assert.True(withFonts.FontPackage);
            Assert.Equal("3.74", withFonts.Version);
            Assert.Empty(FirmwareInfo.MissingSystemFonts(fsRoot));
            Assert.True(FirmwareInfo.SystemReady(fsRoot));
            status = FirmwareStatus.Read(fsRoot);
            Assert.False(status.FontsNeeded);
            Assert.True(status.SystemReady);

            // the font package first, the system update after it: the fonts stay (sa0 is merged, never replaced)
            string fsRoot2 = Path.Combine(tmp, "fs2");
            FirmwareInstaller.Install(fonts, fsRoot2);
            Assert.True(FirmwareInstaller.Install(update, fsRoot2).FontPackage);
            Assert.True(FirmwareInfo.SystemReady(fsRoot2));
        }
        finally
        {
            FirmwareInstaller.DeleteTree(tmp);
        }
    }

    [Fact]
    public void ImportSystemFontsCopiesOnlyTheFontsOfAnotherInstallation()
    {
        string tmp = NewTempRoot();
        try
        {
            string vita3k = Path.Combine(tmp, "vita3k"), fsRoot = Path.Combine(tmp, "fs");
            Directory.CreateDirectory(Path.Combine(vita3k, "sa0", "data", "font", "pgf"));
            Directory.CreateDirectory(Path.Combine(vita3k, "sa0", "data", "font", "pvf"));
            Directory.CreateDirectory(Path.Combine(vita3k, "sa0", "data", "other"));
            File.WriteAllBytes(Path.Combine(vita3k, "sa0", "data", "font", "pgf", "ltn0.pgf"), Bytes(40, 1));
            File.WriteAllBytes(Path.Combine(vita3k, "sa0", "data", "font", "pvf", "jpn0.pvf"), Bytes(40, 2));
            File.WriteAllBytes(Path.Combine(vita3k, "sa0", "data", "other", "x.bin"), Bytes(4, 3));

            string update = Path.Combine(tmp, "PSVUPDAT.PUP");
            File.WriteAllBytes(update, SystemUpdatePup());
            FirmwareInstaller.Install(update, fsRoot);
            Directory.CreateDirectory(Path.Combine(fsRoot, "sa0", "data", "font", "pvf"));
            File.WriteAllBytes(Path.Combine(fsRoot, "sa0", "data", "font", "pvf", "mine.pvf"), Bytes(4, 9));

            FirmwareInfo info = FirmwareInstaller.ImportSystemFonts(vita3k, fsRoot);
            Assert.True(info.FontPackage);
            Assert.Equal("3.74", info.Version);
            Assert.Contains("sa0", info.Partitions);
            Assert.Equal(Bytes(40, 2), File.ReadAllBytes(Path.Combine(fsRoot, "sa0", "data", "font", "pvf", "jpn0.pvf")));
            Assert.True(File.Exists(Path.Combine(fsRoot, "sa0", "data", "font", "pvf", "mine.pvf"))); // merged
            Assert.False(Directory.Exists(Path.Combine(fsRoot, "sa0", "data", "other")));             // fonts only
            Assert.True(FirmwareInfo.SystemReady(fsRoot));

            Assert.Throws<InvalidDataException>(() => FirmwareInstaller.ImportSystemFonts(Path.Combine(tmp, "nothing"), fsRoot));
        }
        finally
        {
            FirmwareInstaller.DeleteTree(tmp);
        }
    }
}
