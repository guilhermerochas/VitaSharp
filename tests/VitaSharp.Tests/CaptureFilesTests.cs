using System.Text;
using VitaSharp.Core.Capture;
using VitaSharp.Core.Io;

namespace VitaSharp.Tests;

/// <summary>
/// The files a "Gravar tudo" capture keeps of what the game read from its own mounts (<see cref="CaptureFileRecorder"/>:
/// files/files.tsv + files/blobs.bin) and how a re-run puts them back (<see cref="CapturedFiles.Materialize"/>): the
/// limit (0 = hashes only), one copy per distinct content, the trim of the pre-roll, which reads of the re-run the
/// recording covers, and that a capture received from someone else cannot write outside the re-run's folder.
/// </summary>
public sealed class CaptureFilesTests : IDisposable
{
    private const string Title = "VSTS00001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-capfiles-" + Guid.NewGuid().ToString("N"));

    public CaptureFilesTests()
    {
        Directory.CreateDirectory(_root);
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
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private string Capture(string name) => Path.Combine(_root, name);

    private static string[] Index(string capture) => File.ReadAllLines(Path.Combine(capture, CaptureFileRecorder.Folder, CaptureFileRecorder.IndexFile));

    private static long BlobLength(string capture) => new FileInfo(Path.Combine(capture, CaptureFileRecorder.Folder, CaptureFileRecorder.BlobFile)).Length;

    [Fact]
    public void ALimitOfZeroKeepsPathsOffsetsAndHashesButNoBytes()
    {
        var recorder = new CaptureFileRecorder(0);
        recorder.RecordRead(Title, "app0:data/level.bin", 4096, 128, Bytes("ABCDEFGH"));
        string capture = Capture("zero");
        CaptureFileRecorder.Summary summary = recorder.Write(capture, long.MinValue);

        Assert.Equal(0, BlobLength(capture));
        Assert.Equal(1, summary.Reads);
        Assert.Equal(1, summary.NotKept);
        Assert.Equal(0, summary.Chunks);
        string read = Assert.Single(Index(capture), l => l.StartsWith("read\t", StringComparison.Ordinal));
        string[] fields = read.Split('\t');
        Assert.Equal(["read", Title, "app0:data/level.bin", "128", "8", ChunkHash.Of(Bytes("ABCDEFGH")).ToString(), "-"], fields[..7]);
        Assert.Contains($"file\t{Title}\tapp0:data/level.bin\t-\t4096\t-\t-\t-", Index(capture));
    }

    [Fact]
    public void TheSameContentReadTwiceIsKeptOnce()
    {
        var recorder = new CaptureFileRecorder(1 << 20);
        recorder.RecordRead(Title, "app0:a.bin", 64, 0, Bytes("SAME"));
        recorder.RecordRead(Title, "app0:a.bin", 64, 32, Bytes("SAME"));
        recorder.RecordRead(Title, "app0:b.bin", 64, 0, Bytes("SAME"));
        recorder.RecordRead(Title, "app0:a.bin", 64, 0, Bytes("SAME"));
        string capture = Capture("dedup");
        CaptureFileRecorder.Summary summary = recorder.Write(capture, long.MinValue);

        Assert.Equal(4, BlobLength(capture));
        Assert.Equal(1, summary.Chunks);
        Assert.Equal(4, summary.Reads);
        Assert.Equal(0, summary.NotKept);
        // every read points at the one copy
        Assert.All(Index(capture).Where(l => l.StartsWith("read\t", StringComparison.Ordinal)), l => Assert.Equal("0", l.Split('\t')[6]));
    }

    [Fact]
    public void ContentsPastTheLimitAreListedWithTheirHashOnly()
    {
        var recorder = new CaptureFileRecorder(6);
        recorder.RecordRead(Title, "app0:a.bin", 64, 0, Bytes("1111"));
        recorder.RecordRead(Title, "app0:a.bin", 64, 4, Bytes("2222"));
        recorder.RecordRead(Title, "app0:a.bin", 64, 8, Bytes("33"));
        string capture = Capture("limit");
        CaptureFileRecorder.Summary summary = recorder.Write(capture, long.MinValue);

        Assert.Equal(6, BlobLength(capture));
        Assert.Equal(2, summary.Chunks);
        Assert.Equal(1, summary.NotKept);
        Assert.Equal(4, summary.NotKeptBytes);
        Assert.Contains("over the limit listed with their hash only", summary.ToString());
    }

    [Fact]
    public void TheTrimOfThePreRollDropsOlderReadsAndTheirContents()
    {
        var recorder = new CaptureFileRecorder(1 << 20);
        recorder.RecordRead(Title, "app0:a.bin", 64, 0, Bytes("OLD!"));
        long between = CaptureClock.NowNs;
        while (CaptureClock.NowNs == between)
        {
            Thread.SpinWait(10);
        }

        between = CaptureClock.NowNs;
        recorder.RecordRead(Title, "app0:a.bin", 64, 4, Bytes("NEW!"));
        recorder.Trim(between);
        string capture = Capture("trim");
        CaptureFileRecorder.Summary summary = recorder.Write(capture, long.MinValue);

        Assert.Equal(1, summary.Reads);
        Assert.Equal(4, BlobLength(capture));
        Assert.Equal("NEW!", File.ReadAllText(Path.Combine(capture, CaptureFileRecorder.Folder, CaptureFileRecorder.BlobFile)));
    }

    [Fact]
    public void ARerunGetsTheFilesBackAtTheirSizeWithTheBytesWhereTheyWereRead()
    {
        var recorder = new CaptureFileRecorder(1 << 20);
        recorder.RecordRead(Title, "app0:dir/a.bin", 4096, 100, Bytes("HELLO"));
        recorder.RecordRead(Title, "app0:/dir/./a.bin", 4096, 105, Bytes("WORLD"));
        recorder.RecordRead(Title, "savedata0:save.dat", 16, 0, Bytes("SAVE"));
        string capture = Capture("roundtrip");
        recorder.Write(capture, long.MinValue);

        CapturedFiles files = CapturedFiles.Materialize(capture, Path.Combine(_root, "out"))!;
        Assert.Equal(2, files.Files);
        Assert.Equal(3, files.Reads);
        Assert.Equal(0, files.MissingContents);
        Assert.Equal(0, files.Skipped);
        string app0 = files.Layers[Title]["app0"];
        byte[] a = File.ReadAllBytes(Path.Combine(app0, "dir", "a.bin"));
        Assert.Equal(4096, a.Length);
        Assert.Equal("HELLOWORLD", Encoding.ASCII.GetString(a, 100, 10));
        Assert.All(a[..100], b => Assert.Equal(0, b));
        byte[] save = File.ReadAllBytes(Path.Combine(files.Layers[Title]["savedata0"], "save.dat"));
        Assert.Equal(16, save.Length);
        Assert.Equal("SAVE", Encoding.ASCII.GetString(save, 0, 4));
    }

    [Fact]
    public void TheRerunCountsTheReadsTheRecordingDidNotMake()
    {
        var recorder = new CaptureFileRecorder(1 << 20);
        recorder.RecordRead(Title, "app0:a.bin", 4096, 0, new byte[100]);
        recorder.RecordRead(Title, "app0:a.bin", 4096, 100, new byte[50]);
        recorder.RecordRead(Title, "app0:a.bin", 4096, 200, new byte[10]);
        string capture = Capture("coverage");
        recorder.Write(capture, long.MinValue);
        CapturedFiles files = CapturedFiles.Materialize(capture, Path.Combine(_root, "out"))!;

        // inside one read, across two reads that touch, another spelling of the path
        Assert.True(files.Check(Title, "app0:a.bin", 10, 20));
        Assert.True(files.Check(Title, "app0:a.bin", 90, 60));
        Assert.True(files.Check(Title, "APP0:/a.bin", 200, 10));
        Assert.Equal(0, files.ReadsOutside);
        // past the end of the recorded range, across the hole at 150..200, another file, another title
        Assert.False(files.Check(Title, "app0:a.bin", 140, 20));
        Assert.False(files.Check(Title, "app0:a.bin", 205, 10));
        Assert.False(files.Check(Title, "app0:b.bin", 0, 1));
        Assert.False(files.Check("VSTS00002", "app0:a.bin", 0, 1));
        Assert.Equal(4, files.ReadsOutside);
    }

    /// <summary>The default (no bytes, hashes only: a capture is posted in public reports): the re-run still knows every
    /// read the recording made, and counts the ones whose bytes it could not put back.</summary>
    [Fact]
    public void ByDefaultTheRerunSaysWhichReadsItCouldNotPutBack()
    {
        var options = new SessionCaptureOptions { Root = _root, SetPresentTap = null! };
        Assert.Equal(0, options.MaxFileBytes);
        var recorder = new CaptureFileRecorder(options.MaxFileBytes);
        recorder.RecordRead(Title, "app0:a.bin", 4096, 0, Bytes("DATA"));
        string capture = Capture("default");
        recorder.Write(capture, long.MinValue);
        Assert.Equal(0, BlobLength(capture));

        CapturedFiles files = CapturedFiles.Materialize(capture, Path.Combine(_root, "out"))!;
        Assert.Equal(1, files.MissingContents);
        Assert.Equal(4096, new FileInfo(Path.Combine(files.Layers[Title]["app0"], "a.bin")).Length);
        Assert.True(files.Check(Title, "app0:a.bin", 0, 4));
        Assert.True(files.Check(Title, "app0:a.bin", 2, 2));
        Assert.Equal(0, files.ReadsOutside);
        Assert.Equal(2, files.ReadsWithoutBytes);
        Assert.DoesNotContain(CaptureFileRecorder.ReportSection(capture), l => l.Contains("NOTE: this capture holds data of the game", StringComparison.Ordinal));
    }

    [Fact]
    public void ACaptureWithoutFilesIsNotARerunWithFiles()
    {
        Assert.Null(CapturedFiles.Materialize(Capture("none"), Path.Combine(_root, "out")));
    }

    /// <summary>
    /// A capture is a file someone else made (a GitHub report): a files.tsv whose path is a drive or root path, goes up
    /// with "..", or names a device that is not a plain name writes nothing outside the re-run's folder, and a line
    /// with numbers that do not parse is skipped instead of ending the re-run.
    /// </summary>
    [Fact]
    public void AReceivedCaptureCannotWriteOutsideTheRerunsFolder()
    {
        string escape = Path.Combine(_root, "escape");
        string capture = Capture("hostile");
        Directory.CreateDirectory(Path.Combine(capture, CaptureFileRecorder.Folder));
        File.WriteAllBytes(Path.Combine(capture, CaptureFileRecorder.Folder, CaptureFileRecorder.BlobFile), Bytes("EVIL"));
        string absolute = Path.Combine(escape, "abs.bin").Replace('\\', '/');
        string[] lines =
        [
            "# kind\ttitle\tpath\toffset\tlength\tsha256\tblob\tguestUs",
            $"file\t{Title}\tapp0:{absolute}\t-\t4\t-\t-\t-",
            $"read\t{Title}\tapp0:{absolute}\t0\t4\t-\t0\t0",
            $"read\t{Title}\tapp0:../../escape/up.bin\t0\t4\t-\t0\t0",
            $"read\t{Title}\t..:escape/dev.bin\t0\t4\t-\t0\t0",
            $"file\t{Title}\tapp0:big.bin\t-\tnot-a-number\t-\t-\t-",
            $"read\t{Title}\tapp0:neg.bin\t-5\t4\t-\t0\t0",
            $"read\t{Title}\tapp0:ok.bin\t0\t4\t-\t0\t0",
        ];
        File.WriteAllLines(Path.Combine(capture, CaptureFileRecorder.Folder, CaptureFileRecorder.IndexFile), lines);

        string outDir = Path.Combine(_root, "out");
        CapturedFiles files = CapturedFiles.Materialize(capture, outDir)!;

        Assert.False(Directory.Exists(escape), "the received capture wrote outside the re-run's folder");
        Assert.All(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories),
            f => Assert.True(f.StartsWith(capture, StringComparison.OrdinalIgnoreCase) || f.StartsWith(outDir, StringComparison.OrdinalIgnoreCase), f));
        Assert.Equal("EVIL", File.ReadAllText(Path.Combine(files.Layers[Title]["app0"], "ok.bin")));
        Assert.True(files.Skipped >= 4, $"skipped {files.Skipped}");
    }

    [Fact]
    public void TheReportSaysWhatWasReadAndWhatLeftWithTheCapture()
    {
        var recorder = new CaptureFileRecorder(4);
        recorder.RecordRead(Title, "app0:big.bin", 1000, 0, Bytes("KEPT"));
        recorder.RecordRead(Title, "app0:big.bin", 1000, 4, Bytes("LOST"));
        string capture = Capture("report");
        recorder.Write(capture, long.MinValue);

        List<string> section = CaptureFileRecorder.ReportSection(capture);
        Assert.Contains(section, l => l.Contains("1 files, 0 folders, 2 reads", StringComparison.Ordinal));
        Assert.Contains(section, l => l.Contains("1 reads (0.0 MB) over the limit", StringComparison.Ordinal));
        Assert.Contains("  app0:big.bin: 2 reads, 8 bytes of 1000", section);
        Assert.Contains(section, l => l.StartsWith("  NOTE: this capture holds data of the game", StringComparison.Ordinal));
        Assert.Empty(CaptureFileRecorder.ReportSection(Capture("none")));
    }

    /// <summary>A save is the player's data: by default its reads leave with their hash only, the re-run does not put
    /// the save back (its own data folder's save is read) and still counts those reads as made by the recording.</summary>
    [Fact]
    public void SaveDataLeavesWithItsHashesOnlyAndIsNotPutBack()
    {
        var recorder = new CaptureFileRecorder(1 << 20);
        recorder.RecordRead(Title, "savedata0:slot0.dat", 64, 0, Bytes("PLAYER-SAVE"), hashOnly: true);
        recorder.RecordRead(Title, "app0:data.bin", 64, 0, Bytes("GAME"));
        string capture = Capture("savedata");
        CaptureFileRecorder.Summary summary = recorder.Write(capture, long.MinValue);

        Assert.Equal(4, BlobLength(capture));
        Assert.Equal(1, summary.SaveDataReads);
        Assert.Contains("save data reads with their hash only", summary.ToString());
        Assert.DoesNotContain("PLAYER-SAVE", File.ReadAllText(Path.Combine(capture, CaptureFileRecorder.Folder, CaptureFileRecorder.BlobFile)));
        Assert.Contains(Index(capture), l => l.StartsWith($"hashread\t{Title}\tsavedata0:slot0.dat\t0\t11\t{ChunkHash.Of(Bytes("PLAYER-SAVE"))}\t-\t", StringComparison.Ordinal));
        Assert.Contains($"hashfile\t{Title}\tsavedata0:slot0.dat\t-\t64\t-\t-\t-", Index(capture));
        Assert.Contains(CaptureFileRecorder.ReportSection(capture), l => l.Contains("1 reads of the save data listed with their hash only", StringComparison.Ordinal));

        CapturedFiles files = CapturedFiles.Materialize(capture, Path.Combine(_root, "out"))!;
        Assert.False(files.Layers[Title].ContainsKey("savedata0"), "the save was put back over the re-run's own");
        Assert.True(files.Check(Title, "savedata0:slot0.dat", 0, 11));
        Assert.Equal(0, files.MissingContents);
    }

    [Fact]
    public void TheSaveDataMountsAreTheTitlesAndKnownAsSaveData()
    {
        var vfs = new Vfs(Path.Combine(_root, "fs"));
        vfs.MountSaveData("savedata0", Path.Combine(_root, "save0"));
        vfs.MountExistingSaveData("savedata1", Path.Combine(_root, "save1"));
        vfs.Mount("addcont0", Path.Combine(_root, "addcont"));

        Assert.True(vfs.IsSaveDataDevice("savedata0:"));
        Assert.True(vfs.IsSaveDataDevice("savedata1"));
        Assert.False(vfs.IsSaveDataDevice("addcont0"));
        Assert.False(vfs.IsSaveDataDevice("app0"));
        Assert.True(vfs.IsTitleDevice("savedata1"));
        Assert.True(vfs.IsTitleDevice("addcont0"));
        Assert.False(vfs.IsTitleDevice("ux0"));
        Assert.False(vfs.IsTitleDevice("vs0"));
    }

    [Fact]
    public void TheReplayLayerIsReadFirstAndAWriteMovesTheFileToTheDevicesFolder()
    {
        string fs = Path.Combine(_root, "fs");
        var vfs = new Vfs(fs);
        string own = Path.Combine(_root, "app");
        Directory.CreateDirectory(own);
        vfs.MountAsIs("savedata0", own);
        string layer = Path.Combine(_root, "layer");
        Directory.CreateDirectory(layer);
        File.WriteAllText(Path.Combine(layer, "Save.dat"), "recorded");
        vfs.AddReplayLayer("savedata0:", layer);

        Assert.True(vfs.IsTitleDevice("savedata0"));
        Assert.False(vfs.IsTitleDevice("ux0"));
        Assert.Equal(Path.Combine(layer, "Save.dat"), vfs.Resolve("savedata0:save.dat"), ignoreCase: true);
        string? write = vfs.Resolve("savedata0:save.dat", forWrite: true);
        Assert.Equal(Path.Combine(own, "save.dat"), write, ignoreCase: true);
        Assert.Equal("recorded", File.ReadAllText(write!));
        Assert.False(File.Exists(Path.Combine(layer, "Save.dat")));
        // from then on the device's copy is the one read
        Assert.Equal(write, vfs.Resolve("savedata0:save.dat"), ignoreCase: true);
    }
}
