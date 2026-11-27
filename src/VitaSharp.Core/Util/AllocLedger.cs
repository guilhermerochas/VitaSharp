using System.Runtime.CompilerServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Who allocates managed memory, always on: the places that run every frame add the bytes the current thread allocated
/// while they ran (<see cref="GC.GetAllocatedBytesForCurrentThread"/>, a read of the thread's allocation context): each
/// HLE function (GuestThread.DispatchHle), the guest code between HLE calls (the CPU backend, compilation on the
/// guest thread) and each GPU command type on the GPU worker. <see cref="Line"/> is the "PERF allocators" line of the
/// periodic summary: the top sites per frame since the previous line, and what no site accounts for (other threads).
/// The phone has no allocation profiler: Killzone's ~450 KB per frame (a young GC every 2 s, 12-19 ms each) needed
/// this to be found.
/// </summary>
public static class AllocLedger
{
    private static readonly object s_lock = new();
    private static readonly Dictionary<string, StrongBox<long>> s_sites = new();
    private static readonly Dictionary<string, long> s_previous = new();
    private static long s_previousTotal = -1, s_previousFrame;

    /// <summary>The counter of <paramref name="site"/> (callers keep it: adding to it is one interlocked add).</summary>
    public static StrongBox<long> Site(string site)
    {
        lock (s_lock)
        {
            if (!s_sites.TryGetValue(site, out StrongBox<long>? box))
            {
                box = new StrongBox<long>();
                s_sites[site] = box;
            }

            return box;
        }
    }

    public static void Add(StrongBox<long> site, long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref site.Value, bytes);
        }
    }

    /// <summary>
    /// "PERF allocators": KB per frame of the busiest sites since the previous call, with <paramref name="extra"/>
    /// sites counted elsewhere (the HLE functions keep their own counters); null before a frame was presented.
    /// </summary>
    public static string? Line(long frame, IEnumerable<(string Site, long Bytes)> extra, int top = 8)
    {
        long total = GC.GetTotalAllocatedBytes();
        var now = new Dictionary<string, long>();
        lock (s_lock)
        {
            foreach (KeyValuePair<string, StrongBox<long>> kv in s_sites)
            {
                now[kv.Key] = Interlocked.Read(ref kv.Value.Value);
            }
        }

        foreach ((string site, long bytes) in extra)
        {
            now[site] = now.GetValueOrDefault(site) + bytes;
        }

        string? line = null;
        long frames = frame - s_previousFrame;
        if (s_previousTotal >= 0 && frames > 0)
        {
            line = Format(frames, total - s_previousTotal, now, s_previous, top);
        }

        s_previous.Clear();
        foreach (KeyValuePair<string, long> kv in now)
        {
            s_previous[kv.Key] = kv.Value;
        }

        s_previousTotal = total;
        s_previousFrame = frame;
        return line;
    }

    /// <summary>The line from two snapshots (pure, unit-tested).</summary>
    internal static string Format(long frames, long totalBytes, IReadOnlyDictionary<string, long> now, IReadOnlyDictionary<string, long> before, int top)
    {
        var deltas = new List<(string Site, long Bytes)>();
        long attributed = 0;
        foreach (KeyValuePair<string, long> kv in now)
        {
            long d = kv.Value - before.GetValueOrDefault(kv.Key);
            if (d > 0)
            {
                deltas.Add((kv.Key, d));
                attributed += d;
            }
        }

        deltas.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        var sb = new System.Text.StringBuilder(256);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append(inv, $"PERF allocators: {totalBytes / 1024.0 / frames:F1} KB/frame over {frames} frames;");
        for (int i = 0; i < deltas.Count && i < top; i++)
        {
            sb.Append(inv, $" {deltas[i].Site} {deltas[i].Bytes / 1024.0 / frames:F1}");
            sb.Append(i + 1 < Math.Min(deltas.Count, top) ? ',' : ';');
        }

        sb.Append(inv, $" not attributed (other threads) {Math.Max(0, totalBytes - attributed) / 1024.0 / frames:F1}");
        return sb.ToString();
    }
}
