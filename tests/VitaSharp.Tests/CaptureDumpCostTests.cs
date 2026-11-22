using VitaSharp.Core.Capture;
using VitaSharp.Core.Gpu;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The capture report says what the GPU dump's recorder cost the GPU worker per frame (the dump's end summary) and, from
/// <see cref="CaptureAnalyzer.DumpCostWarnMs"/>, that the recording's fps is lowered by the dump itself: Assassin's
/// Creed III Liberation ran at 30 fps in play and showed 21-27 fps in its "Gravar tudo" captures, whose recorder took
/// 21.2 ms per frame of the GPU worker, and the report read as the game's own pace.
/// </summary>
public sealed class CaptureDumpCostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-dumpcost-" + Guid.NewGuid().ToString("N"));

    public CaptureDumpCostTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static GpuDumpHeader Summary(long frames, double workerMs)
    {
        var summary = new GpuDumpHeader();
        summary["frames"] = frames.ToString(System.Globalization.CultureInfo.InvariantCulture);
        summary["recorder-worker-ms"] = workerMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        return summary;
    }

    [Fact]
    public void ACostlyRecorderIsReportedAsPartOfTheRecordingsPace()
    {
        // the 21/11 capture: 464 frames, 9846 ms of recorder time, median interval 34.9 ms
        string? note = CaptureAnalyzer.DumpCostNote(Summary(464, 9846.1), 34.9);
        Assert.NotNull(note);
        Assert.Contains("dump recorder: 21.2 ms/frame on the GPU worker", note);
        Assert.Contains("61% of the median interval", note);
        Assert.Contains("not only the game's", note);
    }

    [Fact]
    public void ACheapRecorderIsOnlyListed()
    {
        string? note = CaptureAnalyzer.DumpCostNote(Summary(300, 600), 16.7);
        Assert.Equal("  dump recorder: 2.0 ms/frame on the GPU worker", note);
    }

    [Theory]
    [InlineData(null, "100.0")]
    [InlineData("0", "100.0")]
    [InlineData("10", null)]
    [InlineData("ten", "100.0")]
    public void ADumpWithoutAUsableSummaryHasNoNote(string? frames, string? workerMs)
    {
        var summary = new GpuDumpHeader();
        summary["frames"] = frames;
        summary["recorder-worker-ms"] = workerMs;
        Assert.Null(CaptureAnalyzer.DumpCostNote(summary, 33.3));
        Assert.Null(CaptureAnalyzer.DumpCostNote(null, 33.3));
    }

    /// <summary>The whole path: a capture with frames and a dump whose end summary has the recorder's time.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheReportReadsTheRecorderCostFromTheDumpsSummary(bool withSummary)
    {
        const int Frames = 60;
        const long FrameNs = 33_333_333;
        string dir = Path.Combine(_dir, withSummary ? "summary" : "cut");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.txt"), "title-id: TEST00000\ntitle: Test\nstart-ns: 0\n");
        using (FileStream frames = File.Create(Path.Combine(dir, "frames.vsfr")))
        {
            FramesFile.WriteHeader(frames);
            var encoder = new FramesFile.Encoder();
            for (int i = 0; i < Frames; i++)
            {
                byte[] picture = new byte[16 * 16 * 4];
                Array.Fill(picture, (byte)(40 + i));
                (byte[]? data, byte flags) = encoder.Encode(picture, 16, 16);
                FramesFile.WriteRecord(frames, new PresentInfo { Present = i + 1, Flip = i, PresentNs = (i + 1) * FrameNs, IntervalMs = 33.3f, Hitch = "" }, flags, data);
            }
        }
        using (FileStream dump = File.Create(Path.Combine(dir, "gpu" + GpuDumpFormat.Extension)))
        {
            GpuDumpCodec.WriteFileHeader(dump, new GpuDumpHeader { ["title-id"] = "TEST00000" });
            var records = new DumpBuffer(1 << 12);
            for (int i = 0; i < Frames; i++)
            {
                records.U8((byte)GpuDumpRecord.Present);
                records.U32(0x60000000);
                records.U32(960);
                records.U32(960);
                records.U32(544);
                records.U32(0);
                GpuDumpCodec.WritePresentInfo(records, i * 33_333, i, i + 1);
            }
            dump.Write(GpuDumpCodec.EncodeChunk(GpuDumpFormat.ChunkRecords, records.Buffer.AsSpan(0, records.Length), 1));
            if (withSummary)
            {
                byte[] text = System.Text.Encoding.UTF8.GetBytes(Summary(Frames, Frames * 21.2).Text);
                dump.Write(GpuDumpCodec.EncodeChunk(GpuDumpFormat.ChunkEnd, text, 1));
            }
        }

        string report = CaptureAnalyzer.Analyze(dir, quiet: true);

        Assert.Contains($"GPU dump: {Frames} frames, presents 1-{Frames}", report);
        if (withSummary)
        {
            Assert.Contains("dump recorder: 21.2 ms/frame on the GPU worker (64% of the median interval): the fps and stalls of this recording include the dump's own cost", report);
        }
        else
        {
            Assert.DoesNotContain("dump recorder:", report);
        }
    }
}
