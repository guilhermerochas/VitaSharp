using System.Diagnostics;
using VitaSharp.Core.Firmware;
using VitaSharp.Mobile.Core.Firmware;

namespace VitaSharp.Tests;

/// <summary>
/// "Uninstall firmware" (FirmwareInstaller.Uninstall, shared by the desktop launcher, the CLI and Mobile.Core): it
/// removes only what an installation created (os0, vs0, sa0, pd0, a staging folder left behind, firmware.json and
/// firmware-libraries.txt), keeps the memory card and every user partition, removes a junction or symbolic link as a
/// link (never its target), leaves the data folder in the "no firmware" state, and a new install works after it.
/// </summary>
public sealed class FirmwareUninstallTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"vs_fwuninstall_{Guid.NewGuid():N}");

    private string FsRoot => Path.Combine(_work, "fs");

    public void Dispose()
    {
        // links first, so no deletion ever walks into a target
        foreach (string dir in Directory.Exists(_work) ? Directory.EnumerateDirectories(_work, "*", SearchOption.AllDirectories).ToArray() : [])
        {
            if (Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget != null)
            {
                Directory.Delete(dir, recursive: false);
            }
        }

        try
        {
            Directory.Delete(_work, true);
        }
        catch (IOException)
        {
        }
    }

    private static void Write(string root, string relative, string text = "x")
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A directory link: a junction on Windows (no privilege needed), a symbolic link elsewhere.</summary>
    private static void Link(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using Process process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    /// <summary>A data folder with a firmware installed (its partitions and index files) and the user's data.</summary>
    private void InstalledFirmwareWithUserData()
    {
        Write(FsRoot, "os0/kd/registry.db0");
        Write(FsRoot, "vs0/sys/external/libSceXml.suprx");
        Write(FsRoot, "vs0/vsh/shell/shell.self");
        Write(FsRoot, "sa0/data/font/pvf/ltn0.pvf");
        Write(FsRoot, "pd0/data/bgm.at9");
        Write(FsRoot, ".firmware-install/vs0/leftover.bin");
        Write(FsRoot, FirmwareInfo.MetadataFile, "{\"version\": \"3.74\"}");
        Write(FsRoot, FirmwareLibraries.CacheFileName);
        Write(FsRoot, "ux0/app/ABCD12345/eboot.bin");
        Write(FsRoot, "ux0/user/00/savedata/ABCD12345/SAVE.BIN");
        Write(FsRoot, "ur0/appmeta/ABCD12345/icon0.png");
        Write(FsRoot, "uma0/data/file.bin");
        Write(FsRoot, "imc0/data/file.bin");
        Write(FsRoot, "grw0/data/file.bin");
        Write(FsRoot, "vd0/registry/vitasharp-registry.txt");
    }

    private static readonly string[] s_userFiles =
    [
        "ux0/app/ABCD12345/eboot.bin", "ux0/user/00/savedata/ABCD12345/SAVE.BIN", "ur0/appmeta/ABCD12345/icon0.png",
        "uma0/data/file.bin", "imc0/data/file.bin", "grw0/data/file.bin", "vd0/registry/vitasharp-registry.txt",
    ];

    [Fact]
    public void UninstallRemovesOnlyTheFirmwareAndKeepsTheUserData()
    {
        InstalledFirmwareWithUserData();
        Assert.NotNull(FirmwareInfo.Read(FsRoot));

        IReadOnlyList<string> removed = FirmwareInstaller.Uninstall(FsRoot);

        Assert.Equal(["os0", "vs0", "sa0", "pd0", ".firmware-install", FirmwareInfo.MetadataFile, FirmwareLibraries.CacheFileName], removed);
        foreach (string name in FirmwareInfo.PartitionNames.Append(".firmware-install"))
        {
            Assert.False(Directory.Exists(Path.Combine(FsRoot, name)), name);
        }

        Assert.False(File.Exists(Path.Combine(FsRoot, FirmwareInfo.MetadataFile)));
        Assert.False(File.Exists(Path.Combine(FsRoot, FirmwareLibraries.CacheFileName)));
        foreach (string file in s_userFiles)
        {
            Assert.True(File.Exists(Path.Combine(FsRoot, file.Replace('/', Path.DirectorySeparatorChar))), file);
        }

        // the launcher's "no firmware" state, the same as before the first install
        Assert.Null(FirmwareInfo.Read(FsRoot));
        Assert.False(FirmwareInfo.HasSystemFonts(FsRoot));
        Assert.Empty(FirmwareLibraries.For(FsRoot).Modules);
        Assert.Empty(FirmwareInstaller.Uninstall(FsRoot)); // nothing left to remove
    }

    [Fact]
    public void ALinkedPartitionLosesOnlyItsLink()
    {
        InstalledFirmwareWithUserData();
        string otherFirmware = Path.Combine(_work, "other-emulator");
        Write(otherFirmware, "vs0/sys/external/libSceSqlite.suprx");
        Write(otherFirmware, "fonts/ltn1.pvf");
        FirmwareInstaller.DeleteTree(Path.Combine(FsRoot, "vs0"));
        Link(Path.Combine(FsRoot, "vs0"), Path.Combine(otherFirmware, "vs0"));
        // a link inside a partition too (a font folder shared from elsewhere)
        Link(Path.Combine(FsRoot, "sa0", "data", "font", "shared"), Path.Combine(otherFirmware, "fonts"));

        FirmwareInstaller.Uninstall(FsRoot);

        Assert.False(Directory.Exists(Path.Combine(FsRoot, "vs0")));
        Assert.False(Directory.Exists(Path.Combine(FsRoot, "sa0")));
        Assert.True(File.Exists(Path.Combine(otherFirmware, "vs0", "sys", "external", "libSceSqlite.suprx")));
        Assert.True(File.Exists(Path.Combine(otherFirmware, "fonts", "ltn1.pvf")));
    }

    [Fact]
    public async Task InstallingAgainAfterAnUninstallWorks()
    {
        InstalledFirmwareWithUserData();
        string source = Path.Combine(_work, "source-fs");
        Write(source, "os0/kd/registry.db0");
        Write(source, "vs0/sys/external/libSceXml.suprx");
        Write(source, "sa0/data/font/pvf/ltn0.pvf");
        Write(source, "sa0/data/font/pgf/ltn0.pgf");

        IReadOnlyList<string> removed = await FirmwareSetup.UninstallAsync(FsRoot); // the shared service (Mobile.Core)
        Assert.Contains("vs0", removed);
        Assert.Null(FirmwareInfo.Read(FsRoot));

        FirmwareInfo info = FirmwareInstaller.ImportFrom(source, FsRoot, "test");

        Assert.Equal(["os0", "vs0", "sa0"], info.Partitions);
        Assert.NotNull(FirmwareInfo.Read(FsRoot));
        Assert.True(File.Exists(Path.Combine(FsRoot, "vs0", "sys", "external", "libSceXml.suprx")));
        Assert.True(FirmwareInfo.HasSystemFonts(FsRoot));
        foreach (string file in s_userFiles)
        {
            Assert.True(File.Exists(Path.Combine(FsRoot, file.Replace('/', Path.DirectorySeparatorChar))), file);
        }
    }
}
