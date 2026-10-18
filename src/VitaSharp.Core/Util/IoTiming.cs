namespace VitaSharp.Core.Util;

/// <summary>
/// Storage timing model: reads complete at the speed of the Vita's game card / memory card instead of instantly.
/// Some games' streaming code has only ever run against real device latency (data arriving within the same frame
/// it was requested changes their order of events), so reads queue on one device timeline: each costs a fixed
/// access latency plus size / throughput, in guest time.
/// </summary>
public static class IoTiming
{
    /// <summary>Sustained read throughput in bytes per second; 0 = instant reads.</summary>
    public static double BytesPerSecond { get; set; } = 25e6;
    /// <summary>Per-request access latency in seconds.</summary>
    public static double LatencySeconds { get; set; } = 0.0005;

    static long _deviceFreeAt; // guest clock ticks
    static readonly object Lock = new();

    /// <summary>Blocks the calling thread until a read of <paramref name="bytes"/> issued now would have completed.</summary>
    public static void Read(long bytes)
    {
        if (BytesPerSecond <= 0 || bytes <= 0) return;
        long done;
        lock (Lock)
        {
            long now = GuestClock.Ticks;
            long start = Math.Max(now, _deviceFreeAt);
            done = start + GuestClock.FromSeconds(LatencySeconds + bytes / BytesPerSecond);
            _deviceFreeAt = done;
        }
        if (GuestClock.Ticks < done) { using var blocked = Kernel.Scheduler.Block(); GuestClock.WaitUntil(done); }
    }
}
