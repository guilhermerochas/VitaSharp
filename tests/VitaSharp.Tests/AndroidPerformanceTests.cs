using VitaSharp.Android.Logging;
using VitaSharp.Android.Performance;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>The Android app's speed layer (src/VitaSharp.Android.Common/Performance): CPU clusters from sysfs, the
/// thread placement, the game's frame rate and the per-frame work for ADPF, the logcat level during gameplay.</summary>
public sealed class AndroidPerformanceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-android-perf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    // Helio G99 (MT6789): 6 x Cortex-A55 at 2.0 GHz + 2 x Cortex-A76 at 2.2 GHz
    static CpuTopology HelioG99() => CpuTopology.Build(Enumerable.Range(0, 8).Select(i => i < 6 ? (i, 2_000_000L, 382) : (i, 2_200_000L, 1024)));

    // Snapdragon 8 Gen 1 style: 4 x A510 + 3 x A710 + 1 x X2
    static CpuTopology OnePlusThreePlusFour() => CpuTopology.Build(Enumerable.Range(0, 8).Select(i =>
        i < 4 ? (i, 1_800_000L, 300) : i < 7 ? (i, 2_500_000L, 800) : (i, 3_000_000L, 1024)));

    // ---------------------------------------------------------------- topology

    [Fact]
    public void TopologyGroupsTheHelioG99()
    {
        var t = HelioG99();
        Assert.Equal(2, t.Clusters.Count);
        Assert.Equal([6, 7], t.Big!.Cpus);
        Assert.Null(t.Mid);
        Assert.Equal(0xC0UL, t.Big.Mask);
        Assert.Equal(0x3FUL, t.NotBigMask);
        Assert.Equal("0-5 @2.0GHz, 6-7 @2.2GHz", t.Describe());
    }

    [Fact]
    public void TopologyReadsSysfs()
    {
        // cpu_capacity tells cores apart even at the same frequency; folders such as cpufreq/ and cpuidle/ are skipped
        for (int i = 0; i < 4; i++)
        {
            var d = Path.Combine(_root, $"cpu{i}");
            Directory.CreateDirectory(Path.Combine(d, "cpufreq"));
            File.WriteAllText(Path.Combine(d, "cpufreq", "cpuinfo_max_freq"), "2000000\n");
            File.WriteAllText(Path.Combine(d, "cpu_capacity"), i < 2 ? "400\n" : "1024\n");
        }
        Directory.CreateDirectory(Path.Combine(_root, "cpufreq"));
        Directory.CreateDirectory(Path.Combine(_root, "cpuidle"));
        var t = CpuTopology.FromSysfs(_root);
        Assert.Equal("0-1 @2.0GHz, 2-3 @2.0GHz", t.Describe());
        Assert.Equal([2, 3], t.Big!.Cpus);
    }

    [Fact]
    public void TopologyWithoutCapacityRanksByFrequency()
    {
        var t = CpuTopology.Build([(0, 1_800_000, 0), (1, 1_800_000, 0), (2, 2_400_000, 0), (3, 2_400_000, 0)]);
        Assert.Equal([2, 3], t.Big!.Cpus);
        var single = CpuTopology.Build([(0, 2_000_000, 0), (1, 2_000_000, 0)]);
        Assert.Null(single.Big);
        Assert.False(new ThreadPlacementPlanner(single).CanPlace);
        Assert.Same(PlacementPlan.None, new ThreadPlacementPlanner(single).Plan([new(1, ThreadKind.Guest, "main", 1, 1)]));
    }

    [Fact]
    public void RangesAreCompact()
    {
        Assert.Equal("0-5", CpuTopology.Ranges(0x3FUL));
        Assert.Equal("0,2,4", CpuTopology.Ranges(0x15UL));
        Assert.Equal("1-2,7", CpuTopology.Ranges(0x86UL));
        Assert.Equal("none", CpuTopology.Ranges(0UL));
    }

    // ---------------------------------------------------------------- placement

    static ThreadSample Guest(int tid, double load) => new(tid, ThreadKind.Guest, $"g{tid}", load, tid);

    [Fact]
    public void MainGuestAndGpuGoToTheFastCores()
    {
        var p = new ThreadPlacementPlanner(HelioG99());
        var plan = p.Plan([Guest(10, 0.9), Guest(11, 0.5), new(20, ThreadKind.Gpu, "GPU worker", 0.6, 20),
            new(30, ThreadKind.JitWorker, "JIT worker", 0.2, 30), new(40, ThreadKind.ShaderWorker, "Shader worker", 0.1, 40),
            new(50, ThreadKind.Other, "main", 0.1, 50)]);
        Assert.Equal(10, plan.MainTid);
        Assert.Equal([10, 20], plan.HotTids);
        Assert.Equal(Placement.Big, plan.Placements[10]);
        Assert.Equal(Placement.Big, plan.Placements[20]);
        Assert.Equal(Placement.NotBig, plan.Placements[11]); // two fast cores, both taken
        Assert.Equal(Placement.NotBig, plan.Placements[30]);
        Assert.Equal(Placement.NotBig, plan.Placements[40]); // the hot threads use 1.5 cores of 2: not idle enough
        Assert.False(plan.Placements.ContainsKey(50)); // other threads are left alone
        Assert.Equal(0xC0UL, p.MaskOf(Placement.Big));
        Assert.Equal(0x3FUL, p.MaskOf(Placement.NotBig));
    }

    [Fact]
    public void FirstGuestThreadIsMainUntilThereAreLoads()
    {
        var p = new ThreadPlacementPlanner(HelioG99());
        Assert.Equal(12, p.Plan([Guest(15, 0), Guest(12, 0)]).MainTid);
    }

    [Fact]
    public void MainThreadMovesOnlyWhenAnotherIsClearlyBusier()
    {
        var p = new ThreadPlacementPlanner(HelioG99());
        Assert.Equal(10, p.Plan([Guest(10, 0.8), Guest(11, 0.7)]).MainTid);
        Assert.Equal(10, p.Plan([Guest(10, 0.7), Guest(11, 0.8)]).MainTid); // similar: keeps the current one
        Assert.Equal(11, p.Plan([Guest(10, 0.3), Guest(11, 0.9)]).MainTid);
        Assert.Equal(12, p.Plan([Guest(12, 0.2)]).MainTid); // the main thread ended
    }

    [Fact]
    public void ExtraBusyGuestThreadsUseSpareFastCores()
    {
        // 4 + 4 (A73 + A53): main, GPU and two more busy guest threads; a quiet one stays on the slow cores
        var t = CpuTopology.Build(Enumerable.Range(0, 8).Select(i => i < 4 ? (i, 1_800_000L, 0) : (i, 2_400_000L, 0)));
        var plan = new ThreadPlacementPlanner(t).Plan([Guest(1, 0.9), Guest(2, 0.6), Guest(3, 0.4), Guest(4, 0.1),
            new(9, ThreadKind.Gpu, "GPU worker", 0.5, 9)]);
        Assert.Equal([1, 9, 2, 3], plan.HotTids);
        Assert.Equal(Placement.NotBig, plan.Placements[4]);
    }

    [Fact]
    public void OneFastCoreKeepsItForTheMainThreadAndTheGpuGoesToTheMidCores()
    {
        var p = new ThreadPlacementPlanner(OnePlusThreePlusFour());
        var plan = p.Plan([Guest(1, 0.9), Guest(2, 0.6), new(9, ThreadKind.Gpu, "GPU worker", 0.5, 9)]);
        Assert.Equal(Placement.Big, plan.Placements[1]);
        Assert.Equal(Placement.Mid, plan.Placements[9]);
        Assert.Equal(Placement.NotBig, plan.Placements[2]);
        Assert.Equal(0x80UL, p.MaskOf(Placement.Big));
        Assert.Equal(0x70UL, p.MaskOf(Placement.Mid));
        Assert.Equal(0x7FUL, p.MaskOf(Placement.NotBig));
        // 1 + 7 (no mid cluster): the GPU worker shares the slow cores
        var oneSeven = new ThreadPlacementPlanner(CpuTopology.Build(Enumerable.Range(0, 8).Select(i => (i, i == 7 ? 2_800_000L : 2_000_000L, 0))));
        Assert.Equal(0x7FUL, oneSeven.MaskOf(Placement.Mid));
    }

    [Fact]
    public void ShaderWorkersUseTheFastCoresWhileTheyAreIdle()
    {
        var p = new ThreadPlacementPlanner(HelioG99());
        ThreadSample[] Threads(double main, double gpu) =>
            [Guest(1, main), new(9, ThreadKind.Gpu, "GPU worker", gpu, 9), new(5, ThreadKind.ShaderWorker, "Shader worker", 0.9, 5)];
        Assert.Equal(Placement.All, p.Plan(Threads(0.6, 0.3)).Placements[5]);    // 0.9 of 2 cores: idle enough
        Assert.Equal(Placement.All, p.Plan(Threads(0.9, 0.7)).Placements[5]);    // 1.6: hysteresis keeps it
        Assert.Equal(Placement.NotBig, p.Plan(Threads(1.0, 0.9)).Placements[5]); // 1.9: the game needs them
        Assert.Equal(Placement.NotBig, p.Plan(Threads(0.9, 0.7)).Placements[5]);
        Assert.Equal(Placement.All, p.Plan(Threads(0.5, 0.2)).Placements[5]);
    }

    [Theory]
    [InlineData(NativeThread.Role.Guest, "SceFiosIO", ThreadKind.Guest)]
    [InlineData(NativeThread.Role.Gpu, "GPU worker", ThreadKind.Gpu)]
    [InlineData(NativeThread.Role.JitWorker, "JIT worker", ThreadKind.JitWorker)]
    [InlineData(NativeThread.Role.Other, "Shader worker", ThreadKind.ShaderWorker)]
    [InlineData(NativeThread.Role.Other, "JIT worker", ThreadKind.JitWorker)]
    [InlineData(NativeThread.Role.Other, "VBlank notifier", ThreadKind.Other)]
    public void ThreadsAreClassifiedByRoleThenName(NativeThread.Role role, string name, ThreadKind expected) =>
        Assert.Equal(expected, ThreadPlacementPlanner.Classify(role, name));

    // ---------------------------------------------------------------- frame rate and work

    const long Freq = 1_000_000; // ticks per second in these tests

    static long Ticks(double vblanks) => (long)(vblanks * FrameRateEstimator.VblankSeconds * Freq);

    [Fact]
    public void FrameRateFollowsTheGamesOwnVblankWaits()
    {
        var e = new FrameRateEstimator();
        Assert.Equal(1, e.Vblanks);
        Assert.InRange(e.TargetNanos, 16_683_000, 16_684_000);
        // a 30 fps game with headroom: two vblanks per frame, each frame waiting longer than a vblank period
        long t = 0;
        bool changed = false;
        for (int i = 0; i < 10; i++) changed |= e.OnFrame(t += Ticks(2), Freq, longWait: true);
        Assert.True(changed);
        Assert.Equal(2, e.Vblanks);
        Assert.Equal(30f, e.RefreshRate);
        // falling behind (3 vblanks per frame, no long wait) does not change the target
        for (int i = 0; i < 100; i++) e.OnFrame(t += Ticks(3), Freq, longWait: false);
        Assert.Equal(2, e.Vblanks);
        // a load hitch is not a rate
        e.OnFrame(t += Ticks(40), Freq, true);
        Assert.Equal(2, e.Vblanks);
        // back to 60: one frame on time at one vblank is enough
        Assert.True(e.OnFrame(t += Ticks(1.05), Freq, false));
        Assert.Equal(1, e.Vblanks);
        Assert.Equal(60f, e.RefreshRate);
    }

    [Fact]
    public void SlowSixtyFpsGameKeepsTheSixtyFpsTarget()
    {
        // 20 ms of work, then a wait for the next vblank: every frame takes 2 vblanks, but no wait was longer than one
        var e = new FrameRateEstimator();
        long t = 0;
        for (int i = 0; i < 200; i++) e.OnFrame(t += Ticks(i % 3 == 0 ? 1 : 2), Freq, longWait: false);
        Assert.Equal(1, e.Vblanks);
        e = new FrameRateEstimator();
        for (int i = 0; i < 200; i++) e.OnFrame(t += Ticks(2), Freq, longWait: false);
        Assert.Equal(1, e.Vblanks);
    }

    [Fact]
    public void FrameWorkIsTheBusiestThreadsCpuTime()
    {
        var m = new FrameWorkMeter();
        m.Reset([1, 2], [1_000_000, 5_000_000], 0);
        // main thread worked 9 ms, GPU worker 12 ms of a 16.7 ms frame
        Assert.Equal(12_000_000, m.OnFrame([10_000_000, 17_000_000], 16_700_000));
        // never more than the frame's wall time; an unreadable thread is skipped
        Assert.Equal(10_000_000, m.OnFrame([40_000_000, -1], 26_700_000));
        Assert.Equal(-1, m.OnFrame([-1, -1], 30_000_000));
    }

    // ---------------------------------------------------------------- logcat

    [Theory]
    [InlineData(null, LogLevel.Warn)]
    [InlineData("", LogLevel.Warn)]
    [InlineData("info", LogLevel.Info)]
    [InlineData(" INFO ", LogLevel.Info)]
    [InlineData("debug", LogLevel.Trace)]
    [InlineData("all", LogLevel.Trace)]
    [InlineData("warn", LogLevel.Warn)]
    public void LogcatGameplayLevel(string? setting, LogLevel expected) => Assert.Equal(expected, LogcatFormat.GameplayLevel(setting));
}
