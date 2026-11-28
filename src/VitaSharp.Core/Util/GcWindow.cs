using System.Diagnostics;
using System.Globalization;

namespace VitaSharp.Core.Util;

/// <summary>
/// "PERF gc window": allocations and GC pauses since the previous report, and the longest pause inside one presented
/// frame. The PERF allocated line counts from the start of the process, so its first phone reports are the boot's (Dead
/// Space: "1076 KB/frame, pause 10.9%, 30 ms per GC" after 644 frames, while gameplay allocated 25-75 KB per frame and
/// paused ~1.5% of the time); a stutter or an audio drop is one long pause, which a total hides.
/// </summary>
public static class GcWindow
{
    /// <summary>One sample of the process's GC counters.</summary>
    internal readonly record struct Mark(long Frame, long Timestamp, long Bytes, int Gen0, int Gen1, int Gen2, double PauseMs);

    private static Mark? s_previous;
    private static long s_lastPauseTicks = -1;
    private static long s_longestFramePauseTicks;

    /// <summary>Once per presented frame (Display): the GC pause time inside the frame (a read of the runtime's pause
    /// total, no allocation).</summary>
    public static void OnPresent()
    {
        long pause = GC.GetTotalPauseDuration().Ticks;
        long last = s_lastPauseTicks;
        s_lastPauseTicks = pause;
        if (last < 0)
        {
            return;
        }

        long inFrame = pause - last;
        if (inFrame > Volatile.Read(ref s_longestFramePauseTicks))
        {
            Volatile.Write(ref s_longestFramePauseTicks, inFrame);
        }
    }

    private static Mark Take(long frame) =>
        new(frame, Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds);

    /// <summary>The line since the previous call (null on the first one, or when no frame was presented).</summary>
    public static string? Line(long frame)
    {
        Mark now = Take(frame);
        double longestMs = TimeSpan.FromTicks(Interlocked.Exchange(ref s_longestFramePauseTicks, 0)).TotalMilliseconds;
        string? line = s_previous is { } before && now.Frame > before.Frame ? Format(before, now, longestMs) : null;
        s_previous = now;
        return line;
    }

    /// <summary>The line from two samples (pure, unit-tested).</summary>
    internal static string Format(Mark before, Mark now, double longestFramePauseMs)
    {
        long frames = Math.Max(1, now.Frame - before.Frame);
        double seconds = Math.Max(1e-9, (now.Timestamp - before.Timestamp) / (double)Stopwatch.Frequency);
        int collections = now.Gen0 - before.Gen0;
        double pauseMs = now.PauseMs - before.PauseMs;
        CultureInfo inv = CultureInfo.InvariantCulture;
        return string.Format(inv,
            "PERF gc window (frames {0}-{1}, {2:F1} s): {3:F1} KB/frame, GC gen0 +{4} gen1 +{5} gen2 +{6}, pause {7:F0} ms ({8:F1}% of the time, {9:F1} ms per GC), longest pause in one frame {10:F1} ms",
            before.Frame, now.Frame, seconds, (now.Bytes - before.Bytes) / 1024.0 / frames, collections, now.Gen1 - before.Gen1, now.Gen2 - before.Gen2,
            pauseMs, 100.0 * pauseMs / (seconds * 1000.0), collections > 0 ? pauseMs / collections : 0, longestFramePauseMs);
    }
}
