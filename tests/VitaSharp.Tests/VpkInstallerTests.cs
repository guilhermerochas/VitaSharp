using System.IO.Compression;
using System.Text;
using VitaSharp.Core.Package;

namespace VitaSharp.Tests;

/// <summary>Builds PARAM.SFO files (the format read by <see cref="Sfo"/>).</summary>
static class SfoWriter
{
    public static byte[] Build(IReadOnlyDictionary<string, object> values)
    {
        var keys = values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var keyTable = new MemoryStream();
        var dataTable = new MemoryStream();
        var index = new List<(ushort keyOff, ushort fmt, uint len, uint max, uint dataOff)>();
        foreach (var k in keys)
        {
            ushort keyOff = (ushort)keyTable.Length;
            keyTable.Write(Encoding.UTF8.GetBytes(k + "\0"));
            uint dataOff = (uint)dataTable.Length;
            if (values[k] is uint u)
            {
                dataTable.Write(BitConverter.GetBytes(u));
                index.Add((keyOff, 0x0404, 4, 4, dataOff));
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(values[k] + "\0");
                uint max = (uint)((bytes.Length + 3) & ~3);
                dataTable.Write(bytes);
                dataTable.Write(new byte[max - bytes.Length]);
                index.Add((keyOff, 0x0204, (uint)bytes.Length, max, dataOff));
            }
        }
        while (keyTable.Length % 4 != 0) keyTable.WriteByte(0);

        uint keyTableOff = (uint)(0x14 + 16 * index.Count);
        uint dataTableOff = keyTableOff + (uint)keyTable.Length;
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(0x46535000u); // "\0PSF"
        w.Write(0x00000101u);
        w.Write(keyTableOff);
        w.Write(dataTableOff);
        w.Write((uint)index.Count);
        foreach (var e in index)
        {
            w.Write(e.keyOff); w.Write(e.fmt); w.Write(e.len); w.Write(e.max); w.Write(e.dataOff);
        }
        w.Write(keyTable.ToArray());
        w.Write(dataTable.ToArray());
        return o.ToArray();
    }

    public static byte[] Make(string titleId, string category, string title = "Test Game", string? contentId = null, string appVer = "01.00")
        => Build(new Dictionary<string, object>
        {
            ["TITLE_ID"] = titleId,
            ["CATEGORY"] = category,
            ["TITLE"] = title,
            ["APP_VER"] = appVer,
            ["CONTENT_ID"] = contentId ?? $"EP0000-{titleId}_00-0000000000000000",
            ["ATTRIBUTE"] = 0u,
        });
}

public sealed class VpkInstallerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "VitaSharpVpkTests", Guid.NewGuid().ToString("N"));
    string Ux0 => Path.Combine(_root, "ux0");

    public VpkInstallerTests() => Directory.CreateDirectory(Ux0);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static readonly byte[] FakeEboot = Encoding.ASCII.GetBytes("\u007fELF placeholder eboot");

    static MemoryStream Zip(params (string name, byte[] data)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name);
                if (name.EndsWith('/')) continue;
                using var s = e.Open();
                s.Write(data);
            }
        ms.Position = 0;
        return ms;
    }

    static (string, byte[])[] AppEntries(string prefix, string titleId, string category = "gd", byte[]? eboot = null) =>
    [
        (prefix + "eboot.bin", eboot ?? FakeEboot),
        (prefix + "sce_sys/param.sfo", SfoWriter.Make(titleId, category)),
        (prefix + "sce_sys/icon0.png", [0x89, 0x50, 0x4E, 0x47]),
        (prefix + "sce_module/libc.suprx", [1, 2, 3]),
        (prefix + "data/", []),
        (prefix + "data/level1.dat", Encoding.ASCII.GetBytes("level data")),
    ];

    [Fact]
    public void SfoWriter_RoundTrips()
    {
        var sfo = new Sfo(SfoWriter.Make("PCSE00123", "gd", "Hello"));
        Assert.Equal("PCSE00123", sfo.TitleId);
        Assert.Equal("gd", sfo.Category);
        Assert.Equal("Hello", sfo.Title);
        Assert.Equal(0u, sfo.GetUInt("ATTRIBUTE", 99));
    }

    [Fact]
    public void App_AtRoot_GoesToUx0App()
    {
        var progress = new List<VpkProgress>();
        var results = VpkInstaller.Install(Zip(AppEntries("", "PCSE00001")), Ux0, new VpkInstallOptions { Progress = progress.Add });

        var r = Assert.Single(results);
        Assert.True(r.Installed);
        Assert.False(r.Replaced);
        Assert.Equal(VpkContentKind.App, r.Content.Kind);
        Assert.Equal("PCSE00001", r.TitleId);
        var dir = Path.Combine(Ux0, "app", "PCSE00001");
        Assert.Equal(Path.GetFullPath(dir), r.InstallPath);
        Assert.Equal(FakeEboot, File.ReadAllBytes(Path.Combine(dir, "eboot.bin")));
        Assert.True(File.Exists(Path.Combine(dir, "sce_sys", "param.sfo")));
        Assert.True(File.Exists(Path.Combine(dir, "sce_sys", "icon0.png")));
        Assert.True(File.Exists(Path.Combine(dir, "sce_module", "libc.suprx")));
        Assert.Equal("level data", File.ReadAllText(Path.Combine(dir, "data", "level1.dat")));
        Assert.False(Directory.Exists(Path.Combine(Ux0, ".vpk-tmp")));
        Assert.NotEmpty(progress);
        Assert.Equal(progress[^1].BytesTotal, progress[^1].BytesDone);
    }

    [Fact]
    public void App_NestedInSubfolder_IsDetected()
    {
        var results = VpkInstaller.Install(Zip(AppEntries("My Game (USA) v1.00/", "PCSA00002")), Ux0);
        Assert.Equal("PCSA00002", Assert.Single(results).TitleId);
        var dir = Path.Combine(Ux0, "app", "PCSA00002");
        Assert.True(File.Exists(Path.Combine(dir, "eboot.bin")));
        Assert.True(File.Exists(Path.Combine(dir, "data", "level1.dat")));
        Assert.False(Directory.Exists(Path.Combine(dir, "My Game (USA) v1.00")));
    }

    [Fact]
    public void ContentZip_WithTitleIdFolders_InstallsAppAndPatch()
    {
        var entries = AppEntries("PCSB00003/", "PCSB00003")
            .Concat(AppEntries("patch/PCSB00003/", "PCSB00003", "gp"))
            .Append(("readme.txt", Encoding.ASCII.GetBytes("ignored")))
            .ToArray();
        var results = VpkInstaller.Install(Zip(entries), Ux0);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Content.Kind == VpkContentKind.App);
        Assert.Contains(results, r => r.Content.Kind == VpkContentKind.Patch);
        Assert.True(File.Exists(Path.Combine(Ux0, "app", "PCSB00003", "eboot.bin")));
        Assert.True(File.Exists(Path.Combine(Ux0, "patch", "PCSB00003", "eboot.bin")));
        Assert.False(File.Exists(Path.Combine(Ux0, "app", "PCSB00003", "readme.txt")));
    }

    [Fact]
    public void Patch_GoesToUx0Patch()
    {
        var r = Assert.Single(VpkInstaller.Install(Zip(AppEntries("", "PCSE00004", "gp")), Ux0));
        Assert.Equal(VpkContentKind.Patch, r.Content.Kind);
        Assert.True(File.Exists(Path.Combine(Ux0, "patch", "PCSE00004", "eboot.bin")));
        Assert.False(Directory.Exists(Path.Combine(Ux0, "app", "PCSE00004")));
    }

    [Fact]
    public void AddCont_GoesToUx0AddcontLabelFolder()
    {
        var zip = Zip(
            ("sce_sys/param.sfo", SfoWriter.Make("PCSE00005", "ac", "DLC Pack", "EP0000-PCSE00005_00-DLCPACK000000001")),
            ("dlc/costume.dat", [7, 7, 7]));
        var r = Assert.Single(VpkInstaller.Install(zip, Ux0));
        Assert.Equal(VpkContentKind.AddCont, r.Content.Kind);
        var dir = Path.Combine(Ux0, "addcont", "PCSE00005", "DLCPACK000000001");
        Assert.Equal(Path.GetFullPath(dir), r.InstallPath);
        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(Path.Combine(dir, "dlc", "costume.dat")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("data/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/evil.txt")]
    public void ZipSlip_IsRejected(string badName)
    {
        var entries = AppEntries("", "PCSE00006").Append((badName, Encoding.ASCII.GetBytes("pwned"))).ToArray();
        Assert.Throws<InvalidDataException>(() => VpkInstaller.Install(Zip(entries), Ux0));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Ux0, "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(Ux0, "app", "PCSE00006")));
    }

    [Fact]
    public void MissingParamSfo_IsRejected()
    {
        var zip = Zip(("eboot.bin", FakeEboot), ("sce_sys/icon0.png", [1]));
        var e = Assert.Throws<InvalidDataException>(() => VpkInstaller.Install(zip, Ux0));
        Assert.Contains("param.sfo", e.Message);
    }

    [Fact]
    public void AppWithoutEboot_IsRejected()
    {
        var zip = Zip(("sce_sys/param.sfo", SfoWriter.Make("PCSE00007", "gd")));
        Assert.Throws<InvalidDataException>(() => VpkInstaller.Install(zip, Ux0));
    }

    [Fact]
    public void Overwrite_ConfirmHookDecides()
    {
        VpkInstaller.Install(Zip(AppEntries("", "PCSE00008").Append(("old.txt", new byte[] { 1 })).ToArray()), Ux0);
        var dir = Path.Combine(Ux0, "app", "PCSE00008");
        Assert.True(File.Exists(Path.Combine(dir, "old.txt")));

        VpkInstallResult skipped = Assert.Single(VpkInstaller.Install(Zip(AppEntries("", "PCSE00008")), Ux0,
            new VpkInstallOptions { ConfirmOverwrite = _ => false }));
        Assert.False(skipped.Installed);
        Assert.True(File.Exists(Path.Combine(dir, "old.txt")));

        VpkContentInfo? asked = null;
        var replaced = Assert.Single(VpkInstaller.Install(Zip(AppEntries("", "PCSE00008")), Ux0,
            new VpkInstallOptions { ConfirmOverwrite = c => { asked = c; return true; } }));
        Assert.Equal("PCSE00008", asked?.TitleId);
        Assert.True(replaced.Installed && replaced.Replaced);
        Assert.False(File.Exists(Path.Combine(dir, "old.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "eboot.bin")));
        Assert.False(Directory.Exists(Path.Combine(Ux0, ".vpk-tmp")));
    }

    [Fact]
    public void Cancellation_LeavesNothingBehind()
    {
        using var cts = new CancellationTokenSource();
        var entries = AppEntries("", "PCSE00009").Append(("big.bin", new byte[1 << 20])).ToArray();
        Assert.ThrowsAny<OperationCanceledException>(() => VpkInstaller.Install(Zip(entries), Ux0,
            new VpkInstallOptions { Progress = p => { if (p.BytesDone > 0) cts.Cancel(); } }, cts.Token));
        Assert.False(Directory.Exists(Path.Combine(Ux0, "app", "PCSE00009")));
        Assert.False(Directory.Exists(Path.Combine(Ux0, ".vpk-tmp")));
    }

    /// <summary>Minimal SCE header: an APP SELF with one segment, encrypted or plaintext (fake SELF).</summary>
    static byte[] SelfHeader(bool encrypted)
    {
        var f = new byte[0x200];
        BitConverter.TryWriteBytes(f.AsSpan(0), 0x00454353u);   // "SCE\0"
        BitConverter.TryWriteBytes(f.AsSpan(32 + 24), 0x80ul);   // appinfo
        BitConverter.TryWriteBytes(f.AsSpan(32 + 32), 0xA0ul);   // elf header
        BitConverter.TryWriteBytes(f.AsSpan(32 + 40), 0xE0ul);   // phdrs
        BitConverter.TryWriteBytes(f.AsSpan(32 + 56), 0x100ul);  // segment infos
        BitConverter.TryWriteBytes(f.AsSpan(0x80 + 12), 8u);     // self type APP
        BitConverter.TryWriteBytes(f.AsSpan(0xA0 + 44), (ushort)1);
        BitConverter.TryWriteBytes(f.AsSpan(0x100 + 24), encrypted ? 1u : 2u);
        return f;
    }

    [Fact]
    public void EncryptedEboot_WithoutLicense_FailsWithClearError()
    {
        var zip = Zip(AppEntries("", "PCSE00010", eboot: SelfHeader(encrypted: true)));
        var e = Assert.Throws<VpkLicenseException>(() => VpkInstaller.Install(zip, Ux0));
        Assert.Contains("work.bin", e.Message);
        Assert.False(Directory.Exists(Path.Combine(Ux0, "app", "PCSE00010")));
    }

    [Fact]
    public void EncryptedEboot_WithWorkBin_Installs()
    {
        var entries = AppEntries("", "PCSE00011", eboot: SelfHeader(encrypted: true))
            .Append(("sce_sys/package/work.bin", new byte[0x200])).ToArray();
        Assert.True(Assert.Single(VpkInstaller.Install(Zip(entries), Ux0)).Installed);
        Assert.True(File.Exists(Path.Combine(Ux0, "app", "PCSE00011", "sce_sys", "package", "work.bin")));
    }

    [Fact]
    public void EncryptedEboot_WithUserLicense_CopiesItAsWorkBin()
    {
        var lic = Path.Combine(_root, "license.rif");
        File.WriteAllBytes(lic, new byte[0x200]);
        var zip = Zip(AppEntries("", "PCSE00012", eboot: SelfHeader(encrypted: true)));
        VpkInstaller.Install(zip, Ux0, new VpkInstallOptions { LicensePath = lic });
        Assert.True(File.Exists(Path.Combine(Ux0, "app", "PCSE00012", "sce_sys", "package", "work.bin")));
    }

    [Fact]
    public void FakeSelfHomebrew_NeedsNoLicense()
    {
        var zip = Zip(AppEntries("", "VITASHELL", eboot: SelfHeader(encrypted: false)));
        Assert.True(Assert.Single(VpkInstaller.Install(zip, Ux0)).Installed);
    }

    [Fact]
    public void Inspect_ReportsDestinationsWithoutExtracting()
    {
        var c = Assert.Single(VpkInstaller.Inspect(new ZipArchive(Zip(AppEntries("x/", "PCSE00013"))), Ux0));
        Assert.Equal("x/", c.ArchivePrefix);
        Assert.Equal(VpkContentKind.App, c.Kind);
        Assert.False(c.AlreadyInstalled);
        Assert.False(Directory.Exists(Path.Combine(Ux0, "app")));
    }
}
