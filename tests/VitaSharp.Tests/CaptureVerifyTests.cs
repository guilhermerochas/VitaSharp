using VitaSharp.Core.Capture;

namespace VitaSharp.Tests;

/// <summary>
/// <c>verify</c> without a renderer: the capture report's picture rules as an API (<see cref="FrameFindings"/>, the
/// same findings as report.txt), the comparison of a recording's findings with a replay's finding by finding
/// (<see cref="CaptureVerify.Compare"/>: corrigido / ainda presente / mudou / novo, and the findings outside the dump),
/// the NaN/Inf anomalies of a log and the kind-by-kind comparison with a re-run's report.
/// <see cref="CaptureVerifyRenderTests"/> runs the whole check over a GPU dump on the software Vulkan device.
/// </summary>
public sealed class CaptureVerifyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-verify-" + Guid.NewGuid().ToString("N"));

    public CaptureVerifyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private const int Width = 96, Height = 54;

    /// <summary>A gradient that moves every 8 frames, or a solid <paramref name="fill"/>; <paramref name="cellFill"/> turns
    /// the blue of one cell of the 4x4 grid up.</summary>
    private static byte[] Picture(int t, (byte R, byte G, byte B)? fill = null, int cellFill = -1)
    {
        byte[] px = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Width + x) * 4;
                bool inCell = cellFill >= 0 && x * 4 / Width == cellFill % 4 && y * 4 / Height == cellFill / 4;
                if (inCell)
                {
                    px[o] = (byte)(x * 2 + t);
                    px[o + 1] = (byte)(y * 3);
                    px[o + 2] = 255; // a part of the picture changes, not enough for the whole picture to flash
                }
                else if (fill is { } f && cellFill < 0)
                {
                    px[o] = f.R;
                    px[o + 1] = f.G;
                    px[o + 2] = f.B;
                }
                else
                {
                    px[o] = (byte)(x * 2 + t);
                    px[o + 1] = (byte)(y * 3);
                    px[o + 2] = 128;
                }

                px[o + 3] = 255;
            }
        }

        return px;
    }

    /// <summary>The same frames through <see cref="FrameFindings"/> and through the capture analyzer (frames.vsfr): the
    /// same findings, with the present number and, for a part-picture flash, its cell.</summary>
    [Fact]
    public void FrameFindingsGivesTheReportsFindingsWithPresentAndCell()
    {
        var findings = new FrameFindings();
        string dir = Path.Combine(_dir, "cap");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "session.txt"), "title-id: TEST00000\ntitle: Test\nstart-ns: 0\n");
        using (FileStream fs = File.Create(Path.Combine(dir, "frames.vsfr")))
        {
            FramesFile.WriteHeader(fs);
            var encoder = new FramesFile.Encoder();
            for (int i = 0; i < 120; i++)
            {
                byte[] px = i == 40 ? Picture(i / 8, (0, 0, 0)) : i == 70 ? Picture(i / 8, (250, 0, 250)) : i == 90 ? Picture(i / 8, cellFill: 6) : Picture(i / 8);
                var info = new PresentInfo { Present = 1000 + i, Flip = i, PresentNs = i * 16_666_667L, IntervalMs = 16.7f, Hitch = "" };
                findings.Add(info, px, Width, Height);
                (byte[]? data, byte flags) = encoder.Encode(px, Width, Height);
                FramesFile.WriteRecord(fs, info, flags, data);
            }
        }

        List<CaptureAnalyzer.Finding> live = findings.Findings();
        Assert.Contains(live, f => f.Kind == "black-flash" && f.Present == 1040);
        Assert.Contains(live, f => f.Kind == "flash" && f.Present == 1070 && f.Cell == -1);
        Assert.Contains(live, f => f.Kind == "flash" && f.Present == 1090 && f.Cell == 6);
        Assert.Equal((1000L, 1119L), findings.Presents);
        // the analyzer's report has exactly these findings
        string report = CaptureAnalyzer.Analyze(dir, quiet: true);
        foreach (CaptureAnalyzer.Finding f in live)
        {
            Assert.Contains($"[{f.Kind}] present {f.Present} ", report);
        }

        List<CaptureAnalyzer.Finding> fromFile = FrameFindings.OfFramesFile(Path.Combine(dir, "frames.vsfr"));
        Assert.Equal(live.Select(f => (f.Kind, f.Present, f.Cell)), fromFile.Select(f => (f.Kind, f.Present, f.Cell)));
    }

    private static CaptureVerify.Item Flash(long present, int cell = -1) => new("flash", present, cell, 1, "test");

    [Fact]
    public void AFindingAtTheSamePresentAndCellIsStillPresent()
    {
        CaptureVerify.Result r = CaptureVerify.Compare([Flash(101, 5)], [Flash(101, 5)], 50, 200);
        Assert.Equal(CaptureVerify.Verdict.StillPresent, r.Verdict);
        Assert.Contains(r.Lines, l => l.Contains("[flash] present 101 cell 1,1: ainda presente", StringComparison.Ordinal));
        Assert.Contains(r.Lines, l => l.Contains("flash: 1 -> 1 (ainda presente)", StringComparison.Ordinal));
        // one present either way, and a whole-picture flash covers every cell
        Assert.Equal(CaptureVerify.Verdict.StillPresent, CaptureVerify.Compare([Flash(101, 5)], [Flash(102, -1)], 50, 200).Verdict);
    }

    [Fact]
    public void ARecordedFindingTheReplayDoesNotShowIsFixed()
    {
        CaptureVerify.Result r = CaptureVerify.Compare([Flash(101), new CaptureVerify.Item("black-flash", 150, -2, 2, "x")], [], 50, 200);
        Assert.Equal(CaptureVerify.Verdict.Fixed, r.Verdict);
        Assert.Equal((2, 0, 2), (r.Recorded, r.StillThere, r.Gone));
        Assert.Contains(r.Lines, l => l.Contains("[black-flash] present 150: corrigido", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherCellANearbyPresentOrANewFindingIsAChange()
    {
        Assert.Equal(CaptureVerify.Verdict.Changed, CaptureVerify.Compare([Flash(101, 5)], [Flash(101, 9)], 50, 200).Verdict);
        Assert.Equal(CaptureVerify.Verdict.Changed, CaptureVerify.Compare([Flash(101)], [Flash(104)], 50, 200).Verdict);
        CaptureVerify.Result fresh = CaptureVerify.Compare([Flash(101)], [Flash(170)], 50, 200);
        Assert.Equal(CaptureVerify.Verdict.Changed, fresh.Verdict);
        Assert.Contains(fresh.Lines, l => l.Contains("[flash] present 170: novo", StringComparison.Ordinal));
    }

    /// <summary>Findings before the dump (the pre-roll) or after it are listed as not checked, never as fixed; the
    /// timing kinds (stalls) are not a replay's.</summary>
    [Fact]
    public void FindingsOutsideTheDumpAreNotCheckedByTheReplay()
    {
        CaptureVerify.Result r = CaptureVerify.Compare([Flash(10), new CaptureVerify.Item("stall", 120, -2, 1, "400 ms")], [], 50, 200);
        Assert.Equal(CaptureVerify.Verdict.NothingToCheck, r.Verdict);
        Assert.Contains(r.Lines, l => l.Contains("fora do dump", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Lines, l => l.Contains("stall", StringComparison.Ordinal));
    }

    [Fact]
    public void FindingsFileRoundTripsAndNanAnomaliesComeFromTheLog()
    {
        string path = Path.Combine(_dir, CaptureVerify.FindingsFileName);
        CaptureVerify.WriteFindings(path, 7, 99, [Flash(12, 3), new CaptureVerify.Item("black-flash", 40, -2, 2, "2 black\tframes")]);
        (long first, long last, List<CaptureVerify.Item> items) = CaptureVerify.ReadFindings(path);
        Assert.Equal((7L, 99L), (first, last));
        Assert.Equal([("flash", 12L, 3, 1), ("black-flash", 40L, -2, 2)], items.Select(i => (i.Kind, i.Present, i.Cell, i.Count)));
        string log = Path.Combine(_dir, "log.txt");
        File.WriteAllLines(log, ["[12:00:00.000] INFO [nan] f3 p40 rt 0x60000000: 12 NaN", "[12:00:00.001] INFO [anomaly] p41 f4 nan: rt 0x60000000 F16x4 12 NaN", "other"]);
        List<CaptureVerify.Item> nan = CaptureVerify.NanEvents(log);
        Assert.Equal([("nan", 41L)], nan.Select(n => (n.Kind, n.Present)));
        Assert.Equal(CaptureVerify.Verdict.Fixed, CaptureVerify.Compare(nan, [], 1, 100).Verdict);
    }

    /// <summary>A/B: a switch whose replay loses the finding removes it; the same findings are the same; a moved or new
    /// one is a change. Every switch is one of the renderer's environment switches (EnvVars), named once.</summary>
    [Fact]
    public void ASwitchRemovesChangesOrKeepsTheFinding()
    {
        CaptureVerify.Item[] baseline = [Flash(101, 5), new CaptureVerify.Item("black-flash", 120, -2, 1, "x")];
        Assert.Equal(CaptureVerify.SwitchEffect.Removes, CaptureVerify.CompareSwitch(baseline, [], 50, 200).Effect);
        Assert.Equal(CaptureVerify.SwitchEffect.Same, CaptureVerify.CompareSwitch(baseline, baseline, 50, 200).Effect);
        Assert.Equal(CaptureVerify.SwitchEffect.Changes, CaptureVerify.CompareSwitch(baseline, [Flash(101, 5)], 50, 200).Effect);
        Assert.Equal(CaptureVerify.SwitchEffect.Changes, CaptureVerify.CompareSwitch(baseline, [.. baseline, Flash(150)], 50, 200).Effect);
        Assert.Equal(CaptureVerify.SwitchEffect.Changes, CaptureVerify.CompareSwitch([Flash(101, 5)], [Flash(101, 9)], 50, 200).Effect);

        var constants = typeof(VitaSharp.Core.Util.EnvVars).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        Assert.All(CaptureVerify.RendererSwitches, s => Assert.Contains(s.Key, constants));
        Assert.Equal(CaptureVerify.RendererSwitches.Length, CaptureVerify.RendererSwitches.Select(s => s.Name).Distinct().Count());
        Assert.Equal(CaptureVerify.RendererSwitches.Length, CaptureVerify.RendererSwitches.Select(s => s.Env).Distinct().Count());
    }

    /// <summary>A re-run is not deterministic: its report is compared kind by kind, with the audio's gaps and clicks.</summary>
    [Fact]
    public void ARerunsReportIsComparedKindByKind()
    {
        string a = Path.Combine(_dir, "a.txt"), b = Path.Combine(_dir, "b.txt");
        File.WriteAllLines(a,
        [
            "Findings (3):",
            "  [stall] present 81 flip 80 at +1.000 s: 400 ms between presents",
            "  [stall] present 99 flip 98 at +1.300 s: 300 ms between presents",
            "  [black-transition] present 151 flip 150 at +2.000 s: (info) black for 40 frames",
            "Audio (2 files):",
            "  audio_mix.wav: 3.0 s at 48000 Hz, sound 3.0 s, peak -3.0 dBFS, rms -20.0 dBFS, 2 silent gaps inside the sound (longest 40 ms; at 1.00 s)",
            "  00_port1_MAIN.wav: 3.0 s at 48000 Hz, sound 3.0 s, peak -3.0 dBFS, rms -20.0 dBFS, 2 silent gaps inside the sound (longest 40 ms; at 1.00 s), 1 clicks (at 2.00 s)",
        ]);
        File.WriteAllLines(b, ["Findings (0):", "  none in the presented frames", "Audio (1 files):", "  00_port1_MAIN.wav: 3.0 s at 48000 Hz, sound 3.0 s, peak -3.0 dBFS, rms -20.0 dBFS"]);
        SortedDictionary<string, int> before = CaptureVerify.ReportCounts(a), after = CaptureVerify.ReportCounts(b);
        Assert.Equal([("audio-click", 1), ("audio-gap", 2), ("stall", 2)], before.Select(kv => (kv.Key, kv.Value)));
        Assert.Empty(after);
        Assert.Equal(CaptureVerify.Verdict.Fixed, CaptureVerify.CompareRerun(before, after).Verdict);
        Assert.Equal(CaptureVerify.Verdict.StillPresent, CaptureVerify.CompareRerun(before, before).Verdict);
    }
}
