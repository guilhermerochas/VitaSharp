using VitaSharp.Desktop;

namespace VitaSharp.Tests;

/// <summary>
/// The frame-interval part of the profiler's end-of-run report (<see cref="Sampler.FrameIntervalReport"/>) for every
/// run length. A CI run under coverage instrumentation presented only 31 frames, all above 50 ms, and the report threw
/// "Sequence contains no elements" (an average over no steady interval), taking the headless runner down with it.
/// </summary>
public sealed class SamplerTests
{
    [Fact]
    public void ShortRunWithEveryIntervalAbove50MsReportsNoSteadyFps()
    {
        var intervals = Enumerable.Repeat(100.0, 31).ToList();

        List<string> lines = Sampler.FrameIntervalReport(intervals, []);

        Assert.Contains(lines, l => l.StartsWith($"PROFILE frame interval ms: p10 {100.0:F2}", StringComparison.Ordinal));
        Assert.Contains("PROFILE fps per 50 frames: none (fewer than 51 frames)", lines);
        Assert.Contains("PROFILE steady fps (intervals > 50 ms, i.e. loads, excluded): none (all 31 intervals > 50 ms)", lines);
    }

    [Fact]
    public void NoFramesAtAllReportsNone()
    {
        List<string> lines = Sampler.FrameIntervalReport([], []);

        Assert.Equal(3, lines.Count);
        Assert.Contains("PROFILE frame interval ms: none (no frame intervals)", lines);
        Assert.Contains(lines, l => l.Contains("none (all 0 intervals > 50 ms)", StringComparison.Ordinal));
    }

    [Fact]
    public void ShortRunWithSteadyIntervalsAveragesOnlyThem()
    {
        var intervals = new List<double> { 20, 40, 300 };

        List<string> lines = Sampler.FrameIntervalReport(intervals, []);

        Assert.Contains($"PROFILE steady fps (intervals > 50 ms, i.e. loads, excluded): {1000.0 / 30:F1} over 2 frames", lines);
        Assert.Equal(new List<double> { 20, 40, 300 }, intervals);
    }

    [Fact]
    public void LongRunReportsEveryWindow()
    {
        var intervals = Enumerable.Repeat(1000.0 / 30, 120).ToList();

        List<string> lines = Sampler.FrameIntervalReport(intervals, [30.0, 29.5]);

        Assert.Contains($"PROFILE fps per 50 frames: {30.0:F1} {29.5:F1}", lines);
        Assert.Contains($"PROFILE steady fps (intervals > 50 ms, i.e. loads, excluded): {30.0:F1} over 120 frames", lines);
    }
}
