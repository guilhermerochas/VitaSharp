using System.Diagnostics;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The F8 thread list reports each guest thread's wake-up latency (signal to running, guest time) and, on Linux and
/// Android, the host core, its clock and the thread's nice value: Dead Space's mixer missed its period on every round on
/// the phone with no CPU phase over budget, so the time went into waits, and these lines say whose.
/// </summary>
public sealed class WakeLatencyStatsTests
{
    private static long Ticks(long us) => us * Stopwatch.Frequency / 1_000_000;

    [Fact]
    public void PercentilesComeFromTheRecordedWakes()
    {
        var stats = new WakeLatencyStats();
        Assert.Null(stats.Describe());
        for (int i = 0; i < 98; i++)
        {
            stats.Record(Ticks(30));
        }

        stats.Record(Ticks(1500));
        stats.Record(Ticks(9000));
        Assert.Equal(100, stats.Count);
        Assert.Equal(32, stats.PercentileUs(0.5)); // 30 us lands in the 17-32 us bucket
        Assert.Equal(2048, stats.PercentileUs(0.99));
        Assert.Equal(9000, stats.PercentileUs(1.0));
        Assert.Contains("100 wakes, p50 <= 32 us, p99 <= 2048 us, max 9000 us", stats.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void HostPlacementReadsCoreAndNiceFromTheStatLine()
    {
        // pid (comm with spaces) state ... field 19 nice = 10, field 39 processor = 6
        var fields = new string[50];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = "0";
        }

        fields[0] = "S";
        fields[16] = "10";
        fields[36] = "6";
        string stat = "1234 (Guest pthread) " + string.Join(' ', fields);
        string? text = HostThreadPlacement.Parse(stat, core => core == 6 ? 2_000_000 : 0);
        Assert.Equal("host: core 6 (max 2000 MHz), nice 10, state S", text);
        Assert.Null(HostThreadPlacement.Parse("1234 (short) S 1 2", _ => 0));
    }
}
