using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using VitaSharp.Core.Io;
using VitaSharp.Core.Package;

namespace VitaSharp.Tests;

/// <summary>
/// Where the firmware keeps what is installed is described once (<see cref="FirmwareLayout"/>) and every part of
/// VitaSharp goes through it (docs/agents/regras.md, "Sem listas soltas de pastas ou caminhos do sistema"): one item of
/// each kind is listed and opened by the same path, and no code outside the layout names those folders. Synthetic
/// content only.
/// </summary>
public sealed partial class FirmwareLayoutTests : IDisposable
{
    private readonly string _root;
    private readonly string _fs;

    public FirmwareLayoutTests()
    {
        // the data folder: fs/ inside it (a PSM title's icon is cached next to fs/)
        _root = Directory.CreateTempSubdirectory("vitasharp-layout-").FullName;
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

    // ------------------------------------------------------------------ the single source

    /// <summary>The layout file itself, the only place that names the folders.</summary>
    private static readonly string[] s_allowed = [Path.Combine("src", "VitaSharp.Core", "Io", "FirmwareLayout.cs")];

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    /// <summary>One argument of a call: a string literal, or anything else up to the next comma.</summary>
    [GeneratedRegex(@"Path\.Combine\(\s*([^,()]+?)\s*,((?:\s*""[^""]*""\s*,?)+)")]
    private static partial Regex CombineUnderDevice();

    private static string? RepoRoot([CallerFilePath] string source = "")
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
        {
            for (DirectoryInfo? d = start.Length > 0 ? new DirectoryInfo(start) : null; d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
                {
                    return d.FullName;
                }
            }
        }
        return null;
    }

    /// <summary>The code of a line without its comment (a "//" outside a string literal).</summary>
    private static string Code(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString && c == '\\')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return line[..i];
            }
        }
        return line;
    }

    /// <summary>The folders the layout describes, in the forms code would spell them: "ux0:app" (a guest path),
    /// "ux0/app" (relative to the file system's root, or joined Path.Combine arguments).</summary>
    private static IEnumerable<string> Spellings(FirmwareFolder f)
    {
        yield return $"{f.Device}:{f.Folder}";
        yield return $"{f.Device}:/{f.Folder}";
        yield return $"{f.Device}/{f.Folder}";
    }

    /// <summary>The places in <paramref name="line"/> (code, not comments; with <paramref name="text"/>, a line of a
    /// resource file's texts, all of it) that name a folder of the layout.</summary>
    internal static IEnumerable<string> Hits(string line, bool text = false)
    {
        string code = text ? line : Code(line);
        if (!text && code.TrimStart().StartsWith("///", StringComparison.Ordinal) || code.Trim().Length == 0)
        {
            yield break;
        }

        List<string> literals = text ? [code] : StringLiteral().Matches(code).Select(m => m.Value[1..^1]).ToList();
        // consecutive literal arguments joined: Path.Combine(fs, "ux0", "app", id)
        string joined = string.Join("/", literals);
        foreach (FirmwareFolder f in FirmwareLayout.All)
        {
            foreach (string s in Spellings(f))
            {
                if (literals.Any(l => Contains(l, s)) || Contains(joined, s))
                {
                    yield return s;
                }
            }
        }
        // a folder under a device's own host folder: Path.Combine(ux0Root, "app"), Path.Combine(Program.Ux0, "patch", id)
        foreach (Match m in CombineUnderDevice().Matches(code))
        {
            string first = m.Groups[1].Value;
            string rest = string.Join("/", StringLiteral().Matches(m.Groups[2].Value).Select(x => x.Value[1..^1]));
            foreach (FirmwareFolder f in FirmwareLayout.All)
            {
                if (first.Contains(f.Device, StringComparison.OrdinalIgnoreCase) && (rest + "/").StartsWith(f.Folder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"{first}+{f.Folder}";
                }
            }
        }
    }

    /// <summary>Whether <paramref name="text"/> names the folder <paramref name="spelling"/> (not a longer name that
    /// starts like it, as ux0:appmeta after ux0:app: that one is checked on its own).</summary>
    private static bool Contains(string text, string spelling)
    {
        for (int i = text.IndexOf(spelling, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(spelling, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = i + spelling.Length;
            bool startOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            bool endOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (startOk && endOk)
            {
                return true;
            }
        }
        return false;
    }

    [Theory]
    [InlineData("string d = Path.Combine(fsRoot, \"ux0\", \"app\", id);", true)]
    [InlineData("foreach (var root in new[] { \"vs0:app/\", \"ux0:app/\" })", true)]
    [InlineData("var p = Path.Combine(Program.Ux0, \"patch\", id);", true)]
    [InlineData("string rel = $\"ux0/psm/{tid}/RO\";", true)]
    [InlineData("// the shell lists ux0:app and vs0:app", false)]
    [InlineData("string s = \"http://x\"; // ux0:app", false)]
    [InlineData("var m = Path.Combine(dir, \"sce_sys\", \"param.sfo\");", false)]
    [InlineData("Mount(\"ux0\", Path.Combine(fsRoot, \"ux0\"));", false)]
    public void TheScanFindsHardCodedFolders(string line, bool hit) => Assert.Equal(hit, Hits(line).Any());

    [Theory]
    [InlineData("    <value>Save data (ux0:user/00/savedata/{0})</value>", true)]
    [InlineData("    <value>{0} ({1}) installed in {2}.</value>", false)]
    public void TheScanFindsFoldersInTexts(string line, bool hit) => Assert.Equal(hit, Hits(line, text: true).Any());

    [Fact]
    public void NoCodeOutsideTheLayoutNamesTheFirmwareFolders()
    {
        string? root = RepoRoot();
        Assert.NotNull(root);
        var bad = new List<string>();
        string sep = Path.DirectorySeparatorChar.ToString();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(root!, "src"), "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root!, f);
            if (Path.GetExtension(f) is not (".cs" or ".resx") || rel.Contains(sep + "bin" + sep) || rel.Contains(sep + "obj" + sep)
                || rel.Contains(sep + "obj-dev" + sep) || s_allowed.Contains(rel))
            {
                continue;
            }

            int n = 0;
            foreach (string line in File.ReadLines(f))
            {
                n++;
                foreach (string hit in Hits(line, text: Path.GetExtension(f) == ".resx").Distinct())
                {
                    bad.Add($"{rel}:{n}: {hit}");
                }
            }
        }
        Assert.True(bad.Count == 0, "firmware folders named outside FirmwareLayout (use FirmwareLayout.<folder>):\n" + string.Join("\n", bad));
    }

    // ------------------------------------------------------------------ every kind, listed and opened

    [Fact]
    public void EveryFolderOfTheLayoutIsWhereTheProcessesOpenIt()
    {
        // a card in the slot (gro0:app present) mounts the card's devices too
        Directory.CreateDirectory(FirmwareLayout.GameCardApplications.Host(_fs));
        foreach (FirmwareFolder f in FirmwareLayout.All)
        {
            string host = f.Host(_fs, "probe.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            File.WriteAllText(host, f.Kind.ToString());
        }
        var vfs = new Vfs(_fs);
        foreach (FirmwareFolder f in FirmwareLayout.All)
        {
            string? opened = vfs.Resolve(f.Guest("probe.txt"));
            Assert.Equal(f.Host(_fs, "probe.txt"), opened);
            Assert.Equal(f.Kind.ToString(), File.ReadAllText(opened!));
            Assert.Equal(f.Guest("probe.txt"), FirmwareLayout.HostToGuest(_fs, opened!));
            Assert.Equal(opened, FirmwareLayout.GuestToHost(_fs, f.Guest("probe.txt")));
        }
    }

    private void App(FirmwareFolder folder, string id, string title)
    {
        string dir = folder.Host(_fs, id);
        Directory.CreateDirectory(Path.Combine(dir, "sce_sys"));
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        DesktopLogicTests.WriteSfo(Path.Combine(dir, "sce_sys", "param.sfo"), ("APP_VER", "01.00"), ("CATEGORY", "gd"), ("TITLE", title), ("TITLE_ID", id));
        File.WriteAllBytes(Path.Combine(dir, "sce_sys", "icon0.png"), [0x89, (byte)'P', (byte)'N', (byte)'G']);
    }

    [Fact]
    public void OneTitleOfEachKindIsListedAndOpenedByTheSamePath()
    {
        App(FirmwareLayout.Applications, "PCSE99901", "Installed Game");
        App(FirmwareLayout.GameCardApplications, "PCSE99902", "Card Game");
        App(FirmwareLayout.SystemApplications, "NPXS99903", "System App");
        // a PSM title as its installer leaves it (a synthetic package with the real package's contents/ items)
        string pkg = Path.Combine(_fs, "psm.pkg");
        File.WriteAllBytes(pkg, PsmPkgInstallTests.Package());
        byte[] rif = new byte[PsmPkgInstaller.RifSize];
        Encoding.ASCII.GetBytes("EP0000-NPXX99999_00-0000000000000001").CopyTo(rif, PsmPkgInstaller.RifContentIdOffset);
        InstalledTitle psm = PkgInstaller.Install(pkg, rif, Path.Combine(_fs, "ux0"));
        // a PSP game (the shell's rebuild lists it; the PSP emulator runs it)
        string psp = FirmwareLayout.PspGames.Host(_fs, "ULUS99904");
        Directory.CreateDirectory(psp);
        File.WriteAllBytes(Path.Combine(psp, "EBOOT.PBP"), [0, (byte)'P', (byte)'B', (byte)'P']);

        List<LibraryTitle> listed = InstalledContent.Scan(_fs);

        // the launcher's library: the installed and card applications and the PSM title (the system's own apps are the
        // home screen's), each with its name and icon from its own metadata
        Assert.Equal(["PCSE99901", "PCSE99902", "NPXX99999"], listed.Select(t => t.TitleId));
        LibraryTitle game = listed[0];
        Assert.Equal((ContentKind.VitaApplication, "Installed Game"), (game.Kind, game.Title));
        LibraryTitle psmTitle = listed[2];
        Assert.Equal((ContentKind.PsmTitle, "Synthetic PSM", psm.Path), (psmTitle.Kind, psmTitle.Title, psmTitle.Dir));
        Assert.Equal("Synthetic JP", InstalledContent.ReadPsm(_fs, psmTitle.Dir, "ja-JP")!.Title);
        Assert.NotNull(psmTitle.IconPath);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3], File.ReadAllBytes(psmTitle.IconPath!));

        // the same paths open them: appmgr's lookup by title ID through a process's file system, the launcher's
        var vfs = new Vfs(_fs);
        foreach ((string id, FirmwareFolder folder) in new[] { ("PCSE99901", FirmwareLayout.Applications), ("PCSE99902", FirmwareLayout.GameCardApplications), ("NPXS99903", FirmwareLayout.SystemApplications) })
        {
            Assert.Equal(folder.Host(_fs, id), FirmwareLayout.FindApplication(g => vfs.Resolve(g), id));
            Assert.Equal(folder.Host(_fs, id), FirmwareLayout.FindTitle(_fs, id)?.Dir);
        }
        Assert.Equal(game.Dir, FirmwareLayout.FindApplication(_fs, "PCSE99901"));
        Assert.Equal((FirmwareLayout.PsmTitles, psmTitle.Dir), FirmwareLayout.FindTitle(_fs, "NPXX99999")!.Value);
        // what the PSM runtime opens of the title (appmgr's mount 106 = RO): its program and description
        Assert.True(File.Exists(vfs.Resolve(FirmwareLayout.PsmTitles.Guest("NPXX99999", FirmwareLayout.PsmReadOnly, "Application/app.exe"))));
        Assert.Equal("Synthetic PSM", InstalledContent.TitleName(g => vfs.Resolve(g), "NPXX99999"));
        Assert.Equal("Installed Game", InstalledContent.TitleName(g => vfs.Resolve(g), "PCSE99901"));
        // the shell's database rebuild registers PSP and PSM titles from their own folders
        Assert.Contains(FirmwareLayout.PspGames, FirmwareLayout.ShellRebuildSources);
        Assert.Contains(FirmwareLayout.PsmTitles, FirmwareLayout.ShellRebuildSources);
        Assert.True(File.Exists(vfs.Resolve(FirmwareLayout.PspGames.Guest("ULUS99904", "EBOOT.PBP"))));
        Assert.Null(FirmwareLayout.FindApplication(_fs, "PCSE99999"));
    }

    [Fact]
    public void APsmTitleStartsThroughItsRuntimeWithItsFolderAsTheParameter()
    {
        string title = FirmwareLayout.PsmTitles.Host(_fs, "NPXX99998");
        Directory.CreateDirectory(Path.Combine(title, "RO", "Application"));
        // without the PSM runtime there is nothing to run it
        Assert.True(InstalledContent.IsPsmTitleFolder(_fs, title));
        Assert.Null(InstalledContent.PsmLaunch(_fs, title));

        App(FirmwareLayout.Applications, "PCSE99906", "Another Game");
        Assert.Null(InstalledContent.PsmLaunch(_fs, title));
        App(FirmwareLayout.Applications, "PCSE99907", "PSM Runtime Package");
        File.WriteAllText(FirmwareLayout.Applications.Host(_fs, "PCSE99907", FirmwareLayout.PsmRuntimeVersion), "2.01");
        string runtime = FirmwareLayout.Applications.Host(_fs, "PCSE99907");
        Assert.Equal(runtime, InstalledContent.FindPsmRuntime(_fs));

        // as the shell starts a PSM bubble: the runtime's eboot.bin with the title's folder ("ux0:psm/<id>")
        Assert.Equal((runtime, "eboot.bin", "ux0:psm/NPXX99998"),
                     InstalledContent.PsmLaunch(_fs, title));
        // a title with its own runtime (Unity: ux0:psm/<id>/runtime/eboot.bin) runs that one
        string own = Path.Combine(title, "runtime");
        Directory.CreateDirectory(own);
        File.WriteAllBytes(Path.Combine(own, "eboot.bin"), [1]);
        Assert.Equal((own, "eboot.bin", "ux0:psm/NPXX99998"), InstalledContent.PsmLaunch(_fs, title));
        // anything else is no PSM title
        Assert.False(InstalledContent.IsPsmTitleFolder(_fs, runtime));
        Assert.Null(InstalledContent.PsmLaunch(_fs, runtime));
    }

    // ------------------------------------------------------------------ licences

    private static byte[] Rif(string contentId, byte fill)
    {
        byte[] rif = new byte[Zrif.RifSize];
        rif[0x100] = fill;
        Encoding.ASCII.GetBytes(contentId).CopyTo(rif, 0x10);
        return rif;
    }

    [Fact]
    public void OldLicenceFoldersMoveToTheFirmwaresOnce()
    {
        string old = FirmwareLayout.Licenses.Host(_fs, "PCSE99901");
        Directory.CreateDirectory(old);
        byte[] app = Rif("EP0000-PCSE99901_00-GAME000000000000", 1);
        byte[] dlc = Rif("EP0000-PCSE99901_00-EXTRAMAP00000001", 2);
        File.WriteAllBytes(Path.Combine(old, "app.rif"), app);
        File.WriteAllBytes(Path.Combine(old, "dlc.rif"), dlc);
        File.WriteAllText(Path.Combine(old, "notes.txt"), "kept");
        Directory.CreateDirectory(FirmwareLayout.AddCont.Host(_fs, "PCSE99901", "EXTRAMAP00000001"));
        // a title whose firmware folder already has a different licence: its old one stays
        string other = FirmwareLayout.Licenses.Host(_fs, "PCSE99902");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "a.rif"), Rif("EP0000-PCSE99902_00-GAME000000000000", 3));
        Directory.CreateDirectory(FirmwareLayout.ApplicationLicenses.Host(_fs, "PCSE99902"));
        File.WriteAllBytes(FirmwareLayout.ApplicationLicenses.Host(_fs, "PCSE99902", "a.rif"), Rif("EP0000-PCSE99902_00-GAME000000000000", 4));
        var log = new List<string>();

        Assert.Equal(2, LicenseMigration.Run(_fs, log.Add));

        Assert.Equal(app, File.ReadAllBytes(FirmwareLayout.ApplicationLicenses.Host(_fs, "PCSE99901", "app.rif")));
        Assert.Equal(dlc, File.ReadAllBytes(FirmwareLayout.AddContLicenses.Host(_fs, "PCSE99901", "EXTRAMAP00000001", "dlc.rif")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(old, "notes.txt")));
        Assert.False(File.Exists(Path.Combine(old, "app.rif")));
        Assert.True(File.Exists(Path.Combine(other, "a.rif")));
        Assert.Equal(4, File.ReadAllBytes(FirmwareLayout.ApplicationLicenses.Host(_fs, "PCSE99902", "a.rif"))[0x100]);
        Assert.Contains(log, l => l.Contains("differs", StringComparison.Ordinal));
        Assert.Equal([FirmwareLayout.ApplicationLicenses.Host(_fs, "PCSE99901", "app.rif")], FirmwareLayout.ApplicationLicenseFiles(_fs, "PCSE99901"));
        // again: nothing left to move, nothing touched
        Assert.Equal(0, LicenseMigration.Run(_fs));
        Assert.True(File.Exists(Path.Combine(other, "a.rif")));
    }

    [Fact]
    public void AnOldFolderWithOnlyLicencesIsRemovedAndTheSameLicenceIsDropped()
    {
        string old = FirmwareLayout.Licenses.Host(_fs, "PCSE99903");
        Directory.CreateDirectory(old);
        byte[] rif = Rif("EP0000-PCSE99903_00-GAME000000000000", 5);
        File.WriteAllBytes(Path.Combine(old, "x.rif"), rif);
        Directory.CreateDirectory(FirmwareLayout.ApplicationLicenses.Host(_fs, "PCSE99903"));
        File.WriteAllBytes(FirmwareLayout.ApplicationLicenses.Host(_fs, "PCSE99903", "x.rif"), rif);

        // opening the file system (a process starting) runs the move
        _ = new Vfs(_fs);

        Assert.False(Directory.Exists(old));
        Assert.Equal(rif, File.ReadAllBytes(FirmwareLayout.ApplicationLicenses.Host(_fs, "PCSE99903", "x.rif")));
    }

    // ------------------------------------------------------------------ the PSM description

    [Fact]
    public void PsmAppInfoGivesTheNameByLocaleAndTheIcon()
    {
        byte[] small = [1], big = [2, 2], huge = [3, 3, 3];
        byte[] data = PsmAppInfoFixture.Build(new Dictionary<string, string> { ["en-US"] = "Name US", ["pt-BR"] = "Nome BR", ["ja-JP"] = "JP" },
            defaultLocale: "ja-JP", version: "1.13",
            images: new Dictionary<string, byte[]> { ["splash_854x480"] = [9], ["icon_128x128"] = small, ["icon_256x256"] = big, ["icon_512x512"] = huge });

        var info = PsmAppInfo.Parse(data);

        Assert.NotNull(info);
        Assert.Equal("JP", info!.Name());
        Assert.Equal("Nome BR", info.Name("pt-BR"));
        Assert.Equal("Nome BR", info.Name("pt-PT"));
        Assert.Equal("JP", info.Name("de-DE"));
        Assert.Equal(("1.13", "1.21", "ja-JP"), (info.Version, info.RuntimeVersion, info.DefaultLocale));
        Assert.Equal(big, info.Icon());
        Assert.Equal(small, info.Icon(200));
        Assert.Equal(small, info.Icon(64));
        Assert.Null(PsmAppInfo.Parse("PSMA"u8.ToArray()));
        Assert.Null(PsmAppInfo.Parse(new byte[0x100]));
        // a damaged file: no description, never an exception
        byte[] broken = (byte[])data.Clone();
        BitConverterWrite(broken, 0x0C, 0x7FFFFFF0);
        Assert.Null(PsmAppInfo.Parse(broken));
    }

    private static void BitConverterWrite(byte[] b, int offset, int value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), value);
}
