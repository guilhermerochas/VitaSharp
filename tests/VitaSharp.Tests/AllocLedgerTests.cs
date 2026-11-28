using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>The "PERF allocators" line (Util.AllocLedger): who allocates managed memory per frame, on the phone too.</summary>
public sealed class AllocLedgerTests
{
    [Fact]
    public void TheBusiestSitesComeFirstPerFrameWithTheRestUnattributed()
    {
        var before = new Dictionary<string, long> { ["HLE sceGxmDraw"] = 1000, ["guest code (CPU backend)"] = 0 };
        var now = new Dictionary<string, long> { ["HLE sceGxmDraw"] = 1000 + 10 * 2048, ["guest code (CPU backend)"] = 10 * 4096, ["GPU worker DrawCommand"] = 10 * 1024 };

        string line = AllocLedger.Format(10, 10 * 10240, now, before, 8);

        Assert.Equal("PERF allocators: 10.0 KB/frame over 10 frames; guest code (CPU backend) 4.0, HLE sceGxmDraw 2.0, GPU worker DrawCommand 1.0; not attributed (other threads) 3.0", line);
    }

    [Fact]
    public void ASiteKeepsWhatItsThreadAllocates()
    {
        System.Runtime.CompilerServices.StrongBox<long> site = AllocLedger.Site("test site " + Guid.NewGuid());
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[10_000]);
        AllocLedger.Add(site, GC.GetAllocatedBytesForCurrentThread() - a0);

        Assert.InRange(site.Value, 10_000, 11_000);
    }

    /// <summary>"PERF gc window": the stretch since the previous report, so a log's first reports (the boot's
    /// allocations and pauses) do not stand for the gameplay, and the longest pause inside one frame (the stutter).</summary>
    [Fact]
    public void GcWindowReportsTheStretchSinceThePreviousReport()
    {
        long second = System.Diagnostics.Stopwatch.Frequency;
        var before = new GcWindow.Mark(Frame: 600, Timestamp: 10 * second, Bytes: 800L << 20, Gen0: 120, Gen1: 40, Gen2: 10, PauseMs: 3400);
        var now = new GcWindow.Mark(Frame: 2400, Timestamp: 40 * second, Bytes: (800L << 20) + 1800 * 50 * 1024, Gen0: 144, Gen1: 46, Gen2: 10, PauseMs: 3808);

        string line = GcWindow.Format(before, now, 29.4);

        Assert.Equal("PERF gc window (frames 600-2400, 30.0 s): 50.0 KB/frame, GC gen0 +24 gen1 +6 gen2 +0, pause 408 ms (1.4% of the time, 17.0 ms per GC), longest pause in one frame 29.4 ms", line);
    }

    [Fact]
    public void TheGcWindowSampleOfAFrameAllocatesNothing()
    {
        GcWindow.OnPresent();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            GcWindow.OnPresent();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
