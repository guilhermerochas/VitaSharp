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
    public void StartupCountsOnlyTheFirstMinuteAndTheSlowFramesWithJit()
    {
        // 30 fps for 50 s, then a 120 ms frame with 8 ms of JIT, a 60 ms frame without, then 30 fps past the minute
        // and one more slow frame after it, which is not counted
        var intervals = new List<double>();
        var jit = new List<double>();
        for (int i = 0; i < 1500; i++)
        {
            intervals.Add(1000.0 / 30);
            jit.Add(0);
        }

        intervals.Add(120);
        jit.Add(8);
        intervals.Add(60);
        jit.Add(0.5);
        for (int i = 0; i < 400; i++)
        {
            intervals.Add(1000.0 / 30);
            jit.Add(0);
        }

        intervals.Add(500);
        jit.Add(50);

        string line = Sampler.StartupReport(12.34, intervals, jit);

        // 50 s + 0.18 s, then 295 frames of 33.3 ms reach 60 s
        Assert.Equal($"PROFILE startup: first frame {12.3:F1} s after the process started; first 60 s: 1797 frames, 2 over 50 ms "
            + $"(1 over 100 ms, {120 - 1000.0 / 30 + 60 - 1000.0 / 30:F0} ms above 33.3 ms), 2 of them with JIT compilation over 5 ms, JIT compiling {8.5:F0} ms", line);
    }

    [Fact]
    public void StartupWithoutTheLedgerCountsNoJit()
    {
        string line = Sampler.StartupReport(1, [200, 20], []);

        Assert.Contains("2 frames, 1 over 50 ms (1 over 100 ms", line, StringComparison.Ordinal);
        Assert.Contains("0 of them with JIT compilation over 5 ms, JIT compiling 0 ms", line, StringComparison.Ordinal);
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
