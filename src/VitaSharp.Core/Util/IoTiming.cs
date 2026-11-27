using VitaSharp.Core.Kernel;

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
/// A read takes the device's time, not the host's lateness in waking the thread after it: the time a thread's wait
/// ran past the device's completion (up to <see cref="MaxLateCreditSeconds"/>) is credited to its next read, which is
/// issued that much earlier on the device timeline (never before the previous read completed). Without it a loaded
/// host added its wake-up latency to every read: Dead Space's boot waited 18.3 s for 4.7 s of device time.
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
    public static double BytesPerSecond { get; set; } = ParseSpeed(Environment.GetEnvironmentVariable(EnvVars.IoSpeed)) ?? VitaBytesPerSecond;

    /// <summary>The Vita memory card's sustained read throughput, bytes per second.</summary>
    public const double VitaBytesPerSecond = 7e6;

    /// <summary>The speed of a session without "Carregamento rapido": VITASHARP_IO_SPEED when set, else the Vita's.</summary>
    public static double DefaultBytesPerSecond { get; } = ParseSpeed(Environment.GetEnvironmentVariable(EnvVars.IoSpeed)) ?? VitaBytesPerSecond;

    /// <summary>"Carregamento rapido" (fast loading, per game or the launchers' global default; desktop --fast-loading):
    /// reads complete as fast as the host storage allows, with no device latency or throughput limit. Loads finish
    /// sooner than on a Vita, so a game whose order of events depends on the real loading time (Silent Hill: Book of
    /// Memories' boot splash, see <see cref="BytesPerSecond"/>) can fail; off by default. Turning it off goes back to
    /// <see cref="DefaultBytesPerSecond"/>.</summary>
    public static bool FastLoading
    {
        get => BytesPerSecond <= 0;
        set => BytesPerSecond = value ? 0 : DefaultBytesPerSecond > 0 ? DefaultBytesPerSecond : VitaBytesPerSecond;
    }

    /// <summary>A storage speed setting in MB/s: "vita" (or "real", "default") = <see cref="VitaBytesPerSecond"/>, "fast"
    /// (or "unlimited", "instant") = 0, a number >= 0 = that many MB/s; null when empty or not recognised.</summary>
    public static double? ParseSpeed(string? s)
    {
        s = s?.Trim().ToLowerInvariant();
        return s switch
        {
            null or "" => null,
            "vita" or "real" or "default" => VitaBytesPerSecond,
            "fast" or "unlimited" or "instant" => 0,
            _ => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mb) && mb >= 0 && double.IsFinite(mb)
                ? mb * 1e6 : null,
        };
    }

    /// <summary>Reads that went to the device model, and the guest time the device spent on them (session report).</summary>
    public static long TimedReads, DeviceTicks;

    /// <summary>Per-request access latency in seconds.</summary>
    public static double LatencySeconds { get; set; } = 0.0005;
    /// <summary>Bytes of a file a missing read leaves cached after its own range; 0 = no read-ahead.</summary>
    public static long ReadAheadBytes { get; set; } = 64 * 1024;

    private static long s_deviceFreeAt; // guest clock ticks
    private static readonly object s_lock = new();

    /// <summary>The file range the last device read of a file brought in (guarded by <see cref="s_lock"/>).</summary>
    private sealed class Window
    {
        public long Start = -1, End = -1;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Window> s_windows = new();

    /// <summary>Blocks the calling thread until a read of <paramref name="bytes"/> issued now would have completed.</summary>
    public static void Read(long bytes) => Read(null, 0, bytes);

    /// <summary>Same as <see cref="Read(long)"/> for a read of <paramref name="file"/> at <paramref name="offset"/>: free
    /// when the range is inside the read-ahead window of the file's last device read.</summary>
    public static void Read(object? file, long offset, long bytes)
    {
        if (BytesPerSecond <= 0 || bytes <= 0)
        {
            return;
        }

        long done;
        long cost = GuestClock.FromSeconds(LatencySeconds + bytes / BytesPerSecond);
        long t0 = GuestClock.Ticks;
        lock (s_lock)
        {
            if (file != null && ReadAheadBytes > 0)
            {
                Window w = s_windows.GetOrCreateValue(file);
                if (offset >= w.Start && offset + bytes <= w.End)
                {
                    return;
                }

                w.Start = offset;
                w.End = offset + Math.Max(bytes, ReadAheadBytes);
            }
            long now = t_deferring ? t_deferFrom : t0;
            long issued = now;
            if (!t_deferring)
            {
                // the host woke this thread late from its previous read (t_lateCredit): on the Vita that time never
                // passed between the two reads, so this one is issued that much earlier on the device timeline, never
                // before the previous one completed (the guest code between the two keeps its time)
                issued = Math.Max(now - t_lateCredit, Math.Min(t_lastDone, now));
            }

            long start = Math.Max(issued, s_deviceFreeAt);
            done = start + cost;
            s_deviceFreeAt = done;
            TimedReads++;
            DeviceTicks += done - start;
            if (t_deferring)
            {
                // an asynchronous request: nobody waits here, the request completes when the device is done
                t_deferDone = Math.Max(t_deferDone, done);
                Account(AsyncRequests, done - now, bytes, 0, done - start);
                return;
            }
        }
        long woke = t0;
        if (t0 < done)
        {
            Scheduler.Blocked blocked = Kernel.Scheduler.Block();
            if (WaitUntilForTest is { } wait)
            {
                wait(done);
            }
            else
            {
                GuestClock.WaitUntil(done);
            }

            woke = GuestClock.Ticks;
            blocked.Dispose();
        }
        long end = GuestClock.Ticks;
        // what the host added past the device's completion, for the thread's next read (bounded: a long host stall is
        // not paid back by reads that are far apart)
        t_lateCredit = Math.Clamp(end - done, 0, GuestClock.FromSeconds(MaxLateCreditSeconds));
        t_lastDone = done;
        string who = (Kernel.GuestThread.Current?.Name ?? "host") + (InAsyncRequest ? " (async)" : "");
        lock (s_lock)
        {
            Account(who, end - t0, bytes, end - woke, cost);
        }
    }

    /// <summary>The most host lateness one read hands to the thread's next read, in seconds.</summary>
    internal const double MaxLateCreditSeconds = 0.1;

    /// <summary>Tests: replaces the host wait of a read (the deadline in guest clock ticks), to model a host that wakes
    /// the thread late.</summary>
    internal static Action<long>? WaitUntilForTest { get; set; }

    /// <summary>Tests: forgets the calling thread's lateness credit and last completion.</summary>
    internal static void ResetThreadTimeline()
    {
        t_lateCredit = 0;
        t_lastDone = 0;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static long t_lateCredit;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static long t_lastDone;

    /// <summary>Adds one read to <paramref name="who"/>'s totals (the caller holds <see cref="s_lock"/>).</summary>
    private static void Account(string who, long waitedTicks, long bytes, long regainTicks, long deviceTicks)
    {
        ReadTotals s = s_waitsByThread.GetValueOrDefault(who);
        s_waitsByThread[who] = new ReadTotals(s.Reads + 1, s.Ticks + waitedTicks, s.Bytes + bytes, s.Regain + regainTicks, s.Device + deviceTicks);
    }

    private readonly record struct ReadTotals(long Reads, long Ticks, long Bytes, long Regain, long Device);

    /// <summary>Set while an sceIoXxxAsync request runs (session report attribution).</summary>
    [ThreadStatic] public static bool InAsyncRequest;

    /// <summary>The session report's line for the device time of the asynchronous requests (nobody blocks for it).</summary>
    private const string AsyncRequests = "asynchronous requests (I/O scheduler, no thread blocked)";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static bool t_deferring;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static long t_deferFrom;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static long t_deferDone;

    /// <summary>
    /// The calling thread's reads until <see cref="EndDeferred"/> are an asynchronous request's (the I/O scheduler):
    /// they take their place on the device timeline as if issued at <paramref name="submittedTicks"/> (the guest time
    /// the request was submitted) but do not block; <see cref="EndDeferred"/> says when the device finishes them.
    /// </summary>
    public static void BeginDeferred(long submittedTicks)
    {
        t_deferring = true;
        t_deferFrom = submittedTicks;
        t_deferDone = submittedTicks;
    }

    /// <summary>Ends <see cref="BeginDeferred"/>: the guest clock tick the device finishes the request's reads at
    /// (the submission time when it read nothing from the device).</summary>
    public static long EndDeferred()
    {
        t_deferring = false;
        return t_deferDone;
    }

    /// <summary>Tests: the device is idle from now on (device time other tests reserved does not delay this one's).</summary>
    internal static void ResetDeviceTimeline()
    {
        lock (s_lock)
        {
            s_deviceFreeAt = 0;
        }

        ResetThreadTimeline();
    }

    /// <summary>Per guest thread: device reads, guest time spent blocked in them, bytes (session report).</summary>
    private static readonly Dictionary<string, ReadTotals> s_waitsByThread = new();

    /// <summary>The threads that waited longest for the device, longest first (session report, F8 context.txt): the
    /// time waited against the device time of the same reads (waiting well past it: the host woke the thread late).</summary>
    public static IEnumerable<string> WaitLines(int max = 6)
    {
        List<KeyValuePair<string, ReadTotals>> all;
        lock (s_lock)
        {
            all = s_waitsByThread.ToList();
        }

        double frequency = System.Diagnostics.Stopwatch.Frequency;
        foreach ((string name, ReadTotals s) in all.OrderByDescending(e => e.Value.Ticks).Take(max))
        {
            yield return FormattableString.Invariant($"    {name}: {s.Ticks / frequency:0.0} s waiting for {s.Device / frequency:0.0} s of device time, {s.Reads} reads, {s.Bytes / 1e6:0.0} MB, {s.Regain / frequency:0.0} s of it getting a core back");
        }
    }

    /// <summary>The storage line and the per-thread wait lines (F8 context.txt, end of run).</summary>
    public static IEnumerable<string> SummaryLines()
    {
        if (FastLoading)
        {
            yield return "storage: fast loading (no read speed limit)";
            yield break;
        }

        yield return FormattableString.Invariant($"storage: {BytesPerSecond / 1e6:0.#} MB/s, {TimedReads} device reads, {DeviceTicks / (double)System.Diagnostics.Stopwatch.Frequency:0.0} s of device time");
        foreach (string line in WaitLines())
        {
            yield return line;
        }
    }
}
