namespace VitaSharp.Core.Util;

/// <summary>
/// Storage timing model: reads complete at the speed of the Vita's game card / memory card instead of instantly.
/// Some games' streaming code has only ever run against real device latency (data arriving within the same frame
/// it was requested changes their order of events), so reads queue on one device timeline: each costs a fixed
/// access latency plus size / throughput, in guest time.
/// A read that misses also brings in the rest of a read-ahead window of the file (<see cref="ReadAheadBytes"/>, like
/// a block device's read-ahead and the file system's cluster cache): later reads inside that window cost no device
/// time. Without it every small sequential read paid the full access latency: Sonic Mania's loader reads its 200 MB
/// Data.rsdk in thousands of small pieces (18,600 reads in the `mania_boot` run), 0.5 ms each, and stood still for
/// seconds on each stage load. Vita3K has no storage timing at all (every read instant).
/// </summary>
public static class IoTiming
{
    /// <summary>
    /// Sustained read throughput in bytes per second; 0 = instant reads. The Vita memory card (where installed games
    /// live) reads at 6.3-7.6 MB/s (Digital Foundry's measurement of the official cards, 2012). At the earlier 25 MB/s
    /// Silent Hill: Book of Memories read its 139 MB of boot data in 5.5 s and finished loading before its fixed-length
    /// splash sequence ended (on a Vita the loading screen after the splash stays ~9 s; YouTube BXrVtH4HP2w): the
    /// splash and the game's render jobs then drew in the same GXM context and the game asserted. At 7 MB/s the splash
    /// takes 18.8 s and the loading screen after it 10 s, as on the console.
    /// </summary>
    public static double BytesPerSecond { get; set; } = 7e6;
    /// <summary>Per-request access latency in seconds.</summary>
    public static double LatencySeconds { get; set; } = 0.0005;
    /// <summary>Bytes of a file a missing read leaves cached after its own range; 0 = no read-ahead.</summary>
    public static long ReadAheadBytes { get; set; } = 64 * 1024;

    static long _deviceFreeAt; // guest clock ticks
    static readonly object Lock = new();

    /// <summary>The file range the last device read of a file brought in (guarded by <see cref="Lock"/>).</summary>
    sealed class Window { public long Start = -1, End = -1; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Window> Windows = new();

    /// <summary>Blocks the calling thread until a read of <paramref name="bytes"/> issued now would have completed.</summary>
    public static void Read(long bytes) => Read(null, 0, bytes);

    /// <summary>Same as <see cref="Read(long)"/> for a read of <paramref name="file"/> at <paramref name="offset"/>: free
    /// when the range is inside the read-ahead window of the file's last device read.</summary>
    public static void Read(object? file, long offset, long bytes)
    {
        if (BytesPerSecond <= 0 || bytes <= 0) return;
        long done;
        lock (Lock)
        {
            if (file != null && ReadAheadBytes > 0)
            {
                var w = Windows.GetOrCreateValue(file);
                if (offset >= w.Start && offset + bytes <= w.End) return;
                w.Start = offset;
                w.End = offset + Math.Max(bytes, ReadAheadBytes);
            }
            long now = GuestClock.Ticks;
            long start = Math.Max(now, _deviceFreeAt);
            done = start + GuestClock.FromSeconds(LatencySeconds + bytes / BytesPerSecond);
            _deviceFreeAt = done;
        }
        if (GuestClock.Ticks < done) { using var blocked = Kernel.Scheduler.Block(); GuestClock.WaitUntil(done); }
    }
}
