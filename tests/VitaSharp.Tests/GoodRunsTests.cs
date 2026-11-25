using VitaSharp.Core.Util;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// "It worked before" (Core.Util.GoodRuns): per title, the last version that ran it without a stall is kept in
/// &lt;data&gt;/good-runs.json, and the line a stalled session logs names it (or says none is known, or that it is this
/// version). Before, a STALL did not say which build last ran the title, and a bisection had to ask the user.
/// </summary>
public sealed class GoodRunsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-goodruns-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AStalledSessionNamesTheVersionThatLastRanTheTitle()
    {
        Assert.Equal("PCSE00020: no version is known to have run it without a stall", GoodRuns.Describe(_root, "PCSE00020", "1.2.0+bbbb"));
        GoodRuns.Record(_root, "PCSE00020", "1.1.0+ebec3997", new DateTime(2026, 11, 20, 18, 30, 0, DateTimeKind.Utc));
        GoodRuns.Record(_root, "PCSB00245", "1.2.0+bbbb", new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal("PCSE00020 ran without a stall on version 1.1.0+ebec3997, 2026-11-20 18:30 UTC (this one is 1.2.0+bbbb)",
            GoodRuns.Describe(_root, "PCSE00020", "1.2.0+bbbb"));
        Assert.Equal("PCSB00245 ran without a stall on version 1.2.0+bbbb, 2026-11-24 09:00 UTC (this version)",
            GoodRuns.Describe(_root, "PCSB00245", "1.2.0+bbbb"));

        // a newer good run replaces the title's, the others stay
        GoodRuns.Record(_root, "PCSE00020", "1.2.0+bbbb", new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc));
        Dictionary<string, GoodRun> all = GoodRuns.Load(_root);
        Assert.Equal(2, all.Count);
        Assert.Equal("1.2.0+bbbb", all["PCSE00020"].Version);
        Assert.Equal("1.2.0+bbbb", all["PCSB00245"].Version);
    }

    /// <summary>The session tracker every front end uses (Android's timer, the desktop watchdog): past the frames
    /// without a stall the run is recorded once; no frame for 5 s once the game presents is a stall, reported once with
    /// the good-run line, and that session records nothing; a pause is not a stall; before the first frame nothing is.</summary>
    [Fact]
    public void TheTrackerRecordsAGoodRunAndReportsAStallOnce()
    {
        DateTime start = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        GoodRunTracker good = new(_root, () => "PCSE00020", "1.1.0");
        Assert.Null(good.Tick(0, start, paused: false));
        Assert.Null(good.Tick(0, start.AddSeconds(60), paused: false)); // the boot, before any frame
        Assert.Null(good.Tick(GoodRuns.Frames, start.AddSeconds(61), paused: false));
        Assert.Equal("1.1.0", GoodRuns.Load(_root)["PCSE00020"].Version);

        GoodRunTracker stalled = new(_root, () => "PCSE00020", "1.2.0");
        Assert.Null(stalled.Tick(100, start, paused: false));
        Assert.Null(stalled.Tick(100, start.AddSeconds(30), paused: true)); // paused: the clock restarts
        Assert.Null(stalled.Tick(100, start.AddSeconds(34), paused: false));
        string? line = stalled.Tick(100, start.AddSeconds(36), paused: false);
        Assert.NotNull(line);
        Assert.StartsWith("STALL: no frame presented for 6 s (frame 100)", line);
        Assert.Contains("PCSE00020 ran without a stall on version 1.1.0", line);
        Assert.Null(stalled.Tick(100, start.AddSeconds(40), paused: false)); // once per stall
        Assert.True(stalled.Stalled);
        Assert.Null(stalled.Tick(GoodRuns.Frames * 2, start.AddSeconds(41), paused: false));
        Assert.Equal("1.1.0", GoodRuns.Load(_root)["PCSE00020"].Version); // a session that stalled is no good run
    }

    [Fact]
    public void AnUnreadableFileIsNoRecord()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(GoodRuns.FilePath(_root), "{ not json");
        Assert.Empty(GoodRuns.Load(_root));
        GoodRuns.Record(_root, "PCSE00020", "1.2.0", new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc));
        Assert.Equal("1.2.0", GoodRuns.Load(_root)["PCSE00020"].Version);
    }
}
