using VitaSharp.Core.Firmware;

namespace VitaSharp.Tests;

/// <summary>
/// The system fonts (sa0:data/font/pgf and pvf, the firmware font package): what the installation state says about
/// them follows what is on the disk. On the console sa0 is written at the factory and no system update carries it;
/// a firmware installed from PSVUPDAT.PUP alone has no fonts, and the Vita system (libpaf through libpvf) and the
/// libpgf/libpvf games then draw no text (the Android report: "ScePvf: font file sa0:data/font/pvf/jpn0.pvf not
/// found", no text in the shell and its first-time setup).
/// </summary>
public class FirmwareFontsTests
{
    private static string NewTempRoot()
    {
        string d = Path.Combine(Path.GetTempPath(), "vitasharp-fwfonts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Write(string root, string relative, string content = "x")
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteMetadata(string fsRoot, bool fontPackage) =>
        Write(fsRoot, FirmwareInfo.MetadataFile,
            "{\"version\": \"3.74\", \"build\": \"1\", \"source\": \"PSVUPDAT.PUP\", \"partitions\": \"os0,vs0\", \"font_package\": \""
            + (fontPackage ? "true" : "false") + "\"}");

    [Fact]
    public void OnlyThePgfFontsAreNotTheSystemFonts()
    {
        // the phone's sa0: no pvf, so libpvf (the shell's text) has no font even if some pgf were there
        string tmp = NewTempRoot();
        try
        {
            Write(tmp, "sa0/data/font/pgf/ltn0.pgf");
            Assert.False(FirmwareInfo.HasSystemFonts(tmp));
            Write(tmp, "sa0/data/font/pvf/jpn0.pvf");
            Assert.True(FirmwareInfo.HasSystemFonts(tmp));
            File.Delete(Path.Combine(tmp, "sa0", "data", "font", "pgf", "ltn0.pgf"));
            Assert.False(FirmwareInfo.HasSystemFonts(tmp));
        }
        finally
        {
            FirmwareInstaller.DeleteTree(tmp);
        }
    }

    [Fact]
    public void FontPackageFollowsTheDiskNotTheMetadata()
    {
        string tmp = NewTempRoot();
        try
        {
            Write(tmp, "vs0/sys/external/libSceXml.suprx");
            // fonts copied into sa0 after a system-update install: the metadata still says no font package
            WriteMetadata(tmp, fontPackage: false);
            Write(tmp, "sa0/data/font/pgf/ltn0.pgf");
            Write(tmp, "sa0/data/font/pvf/ltn0.pvf");
            Assert.True(FirmwareInfo.Read(tmp)!.FontPackage);

            // metadata that says "font package" over an sa0 without fonts (removed, or never there)
            WriteMetadata(tmp, fontPackage: true);
            FirmwareInstaller.DeleteTree(Path.Combine(tmp, "sa0"));
            Assert.False(FirmwareInfo.Read(tmp)!.FontPackage);
        }
        finally
        {
            FirmwareInstaller.DeleteTree(tmp);
        }
    }

    [Fact]
    public void CopyingAnotherInstallationRecordsItsFonts()
    {
        string tmp = NewTempRoot();
        try
        {
            string vita3k = Path.Combine(tmp, "vita3k"), fsRoot = Path.Combine(tmp, "fs");
            Write(vita3k, "vs0/sys/external/libSceXml.suprx");
            Write(vita3k, "sa0/data/font/pgf/ltn0.pgf");
            Write(vita3k, "sa0/data/font/pvf/ltn0.pvf");
            FirmwareInfo info = FirmwareInstaller.ImportFrom(vita3k, fsRoot, "Vita3K");
            Assert.True(info.FontPackage);
            Assert.True(FirmwareInfo.Read(fsRoot)!.FontPackage);
            Assert.Contains("\"font_package\": \"true\"", File.ReadAllText(Path.Combine(fsRoot, FirmwareInfo.MetadataFile)));

            // another installation without fonts: recorded as such
            string bare = Path.Combine(tmp, "bare"), fsRoot2 = Path.Combine(tmp, "fs2");
            Write(bare, "vs0/sys/external/libSceXml.suprx");
            Assert.False(FirmwareInstaller.ImportFrom(bare, fsRoot2, "bare").FontPackage);
            Assert.Contains("\"font_package\": \"false\"", File.ReadAllText(Path.Combine(fsRoot2, FirmwareInfo.MetadataFile)));
        }
        finally
        {
            FirmwareInstaller.DeleteTree(tmp);
        }
    }
}
