using System.Diagnostics;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The <c>[hitch]</c> log line (Util/FrameLedger.cs): a present long after the previous one names the host time of the
/// interval by cause, largest first, causes under 1 ms left out, the GC with its collection count, and says when no
/// measured cause took the time.
/// </summary>
public sealed class FrameLedgerHitchTests
{
    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    [Fact]
    public void TheCausesAreListedLargestFirst()
    {
        long[] last = new long[(int)FrameLedger.Cat.Count];
        long[] totals = new long[(int)FrameLedger.Cat.Count];
        totals[(int)FrameLedger.Cat.TextureUpload] = Ms(180);
        totals[(int)FrameLedger.Cat.GcPause] = Ms(40);
        totals[(int)FrameLedger.Cat.JitCompile] = Ms(0.4);

        string? line = FrameLedger.HitchLine(812, 250, totals, last, 2);

        Assert.Equal("[hitch] present 812: 250 ms since the previous one; texUpload 180 ms, gc 40 ms (2 collections)", line);
    }

    [Fact]
    public void OnlyTheIntervalCounts()
    {
        long[] last = new long[(int)FrameLedger.Cat.Count];
        long[] totals = new long[(int)FrameLedger.Cat.Count];
        last[(int)FrameLedger.Cat.PipelineCreate] = Ms(500);
        totals[(int)FrameLedger.Cat.PipelineCreate] = Ms(530);

        string? line = FrameLedger.HitchLine(9, 120, totals, last, 0);

        Assert.Equal("[hitch] present 9: 120 ms since the previous one; pipeline 30 ms", line);
    }

    [Fact]
    public void NoMeasuredCauseIsSaid()
    {
        long[] zero = new long[(int)FrameLedger.Cat.Count];

        string? line = FrameLedger.HitchLine(3, 400, zero, zero, 0);

        Assert.Equal("[hitch] present 3: 400 ms since the previous one; no measured cause (guest code, GPU, or an unmeasured host wait)", line);
    }

    [Fact]
    public void TheCaptureReportListsTheHitchesLongestFirst()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-hitch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "log.txt"),
            [
                "[20:26:01.000] [Info ] [Guest:killzone_vita] [hitch] present 10: 120 ms since the previous one; gc 30 ms (1 collections)",
                "[20:26:02.000] [Info ] [Guest:killzone_vita] something else",
                "[20:26:03.000] [Info ] [Guest:killzone_vita] [hitch] present 40: 900 ms since the previous one; texUpload 850 ms",
            ]);

            List<string> lines = VitaSharp.Core.Capture.CaptureAnalyzer.HitchLines(dir);

            Assert.Equal(4, lines.Count);
            Assert.StartsWith("Hitches in the log (2,", lines[1]);
            Assert.Equal("  present 40: 900 ms since the previous one; texUpload 850 ms", lines[2]);
            Assert.Equal("  present 10: 120 ms since the previous one; gc 30 ms (1 collections)", lines[3]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NoHitchesNoSection()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-hitch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "log.txt"), "[20:26:01.000] [Info ] [1] nothing
");

            Assert.Empty(VitaSharp.Core.Capture.CaptureAnalyzer.HitchLines(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
