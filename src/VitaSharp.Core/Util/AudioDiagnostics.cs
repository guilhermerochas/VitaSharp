using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using VitaSharp.Core.Host;

namespace VitaSharp.Core.Util;

/// <summary>
/// Audio statistics and diagnostics (docs/notes/debug-mode.md, "Audio"). Always counted (a few field writes per
/// sceAudioOutOutput): outputs, gaps between outputs, silent grains and host underruns per port, plus the decoder and
/// NGS streams that exist. Debug mode adds the open/release lines, a snapshot when it is switched on, the periodic
/// <c>[dbg] AUDIO</c> lines (every <see cref="PeriodSeconds"/>) and the end of underrun bursts; a short summary is
/// logged at the end of every session and written into F10 captures (audio.txt).
/// </summary>
public static class AudioDiagnostics
{
    public const double PeriodSeconds = 10;
    /// <summary>Underruns closer together than this belong to one burst.</summary>
    const double BurstQuietSeconds = 1.0;

    /// <summary>Host time without the time the emulation spent paused: a pause is not a gap between outputs.</summary>
    static double Now => EmulationPause.ActiveHostTicks / (double)Stopwatch.Frequency;

    public sealed class PortStats
    {
        public required string Name;
        public int Grain, Freq, Channels;
        public float VolumeL = 1, VolumeR = 1;
        public IAudioStream? Stream;
        public readonly double OpenedAt = Now;
        public double ClosedAt;
        internal readonly object Lock = new();
        // totals
        public long Outputs, Silent, Underruns, Gaps;
        public double GapSum, GapMax, LastOutput, LastGap;
        internal int StreamUnderrunsSeen;
        // current interval (periodic debug lines)
        internal long IOutputs, ISilent, IUnderruns, IGaps;
        internal double IGapSum, IGapMax, IAt = Now;
        // underrun bursts
        internal bool InBurst;
        internal double BurstStart, BurstLast;
        internal long BurstCount, BurstNumber;

        public double ExpectedPerSecond => Grain > 0 ? (double)Freq / Grain : 0;

        public string Describe() =>
            $"{Name} grain {Grain} {Freq} Hz {(Channels == 1 ? "mono" : "stereo")} volume {VolumeL:F2}/{VolumeR:F2}";
    }

    static readonly ConcurrentDictionary<PortStats, byte> Open = new();
    static readonly ConcurrentQueue<PortStats> Closed = new();
    /// <summary>NGS systems and decoder streams (AvPlayer, Atrac, Audiodec): key -> description.</summary>
    static readonly ConcurrentDictionary<string, string> Streams = new();

    /// <summary>Host output device description (backend, mixer rate, update size, buffering, latency).</summary>
    public static Func<string>? DeviceInfo { get; set; }

    static string Frame => $"f{DebugMode.Frame}";

    // ------------------------------------------------------------------ ports

    public static PortStats OpenPort(string name, int grain, int freq, int channels, IAudioStream? stream)
    {
        var p = new PortStats { Name = name, Grain = grain, Freq = freq, Channels = channels, Stream = stream };
        Open[p] = 0;
        if (DebugMode.Enabled)
            Log.Info($"[dbg] {Frame} AUDIO open {p.Describe()}, expected {p.ExpectedPerSecond:F1} outputs/s{(stream == null ? ", no host stream (paced by the clock)" : "")}");
        return p;
    }

    /// <summary>Grain, rate or channels changed (sceAudioOutSetConfig), possibly with a new host stream.</summary>
    public static void Reconfigure(PortStats p, int grain, int freq, int channels, IAudioStream? stream)
    {
        lock (p.Lock)
        {
            p.Grain = grain; p.Freq = freq; p.Channels = channels;
            if (!ReferenceEquals(p.Stream, stream)) { p.Stream = stream; p.StreamUnderrunsSeen = 0; }
        }
        if (DebugMode.Enabled) Log.Info($"[dbg] {Frame} AUDIO config {p.Describe()}");
    }

    public static void ClosePort(PortStats p)
    {
        if (!Open.TryRemove(p, out _)) return;
        p.ClosedAt = Now;
        Closed.Enqueue(p);
        while (Closed.Count > 64 && Closed.TryDequeue(out _)) { }
        if (DebugMode.Enabled) Log.Info($"[dbg] {Frame} AUDIO release {p.Describe()}: {Totals(p)}");
    }

    /// <summary>sceAppMgrLoadExec replaced the process: its ports count as released, its streams are gone.</summary>
    public static void ProcessEnded()
    {
        foreach (var p in Open.Keys) ClosePort(p);
        Streams.Clear();
    }

    /// <summary>One guest output of a grain (called on entry to sceAudioOutOutput, before it blocks).</summary>
    public static void Output(PortStats p, bool silent)
    {
        double now = Now;
        lock (p.Lock)
        {
            if (p.LastOutput > 0)
            {
                double gap = now - p.LastOutput;
                p.LastGap = gap;
                p.Gaps++; p.GapSum += gap; if (gap > p.GapMax) p.GapMax = gap;
                p.IGaps++; p.IGapSum += gap; if (gap > p.IGapMax) p.IGapMax = gap;
            }
            p.LastOutput = now;
            p.Outputs++; p.IOutputs++;
            if (silent) { p.Silent++; p.ISilent++; }
        }
    }

    /// <summary>After the host stream accepted a grain: picks up the underruns it detected.</summary>
    public static void AfterSubmit(PortStats p)
    {
        if (p.Stream is not { } s) return;
        int u = s.Underruns;
        if (u == p.StreamUnderrunsSeen) return;
        int n;
        lock (p.Lock) { n = u - p.StreamUnderrunsSeen; p.StreamUnderrunsSeen = u; }
        if (n > 0) Underrun(p, n);
    }

    static void Underrun(PortStats p, int n)
    {
        double now = Now;
        bool start;
        double sincePrev;
        long burst;
        lock (p.Lock)
        {
            p.Underruns += n; p.IUnderruns += n;
            start = !p.InBurst || now - p.BurstLast > BurstQuietSeconds;
            if (start)
            {
                if (p.InBurst) EndBurstLocked(p);
                p.InBurst = true; p.BurstStart = now; p.BurstCount = 0; p.BurstNumber++;
            }
            p.BurstCount += n;
            p.BurstLast = now;
            sincePrev = p.LastGap;
            burst = p.BurstNumber;
        }
        if (!start) return;
        // one line per burst (debug mode: every burst; otherwise the 1st-3rd, 10th, 100th... burst)
        if (DebugMode.Enabled || DebugMode.ShouldLog((int)Math.Min(burst, int.MaxValue)))
            Log.Warn($"Audio: underrun burst #{burst} on {p.Name} at {Frame}: host queue ran dry, {sincePrev * 1000:F1} ms since the previous output " +
                     $"(grain {p.Grain * 1000.0 / Math.Max(1, p.Freq):F1} ms), host queue {Pending(p)} samples");
    }

    static void EndBurstLocked(PortStats p)
    {
        p.InBurst = false;
        if (DebugMode.Enabled)
            Log.Info($"[dbg] {Frame} AUDIO underrun burst #{p.BurstNumber} on {p.Name} ended: {p.BurstCount} underruns over {(p.BurstLast - p.BurstStart) * 1000:F0} ms");
    }

    static int Pending(PortStats p)
    {
        try { return p.Stream?.PendingSamples ?? 0; } catch { return -1; }
    }

    // ------------------------------------------------------------------ decoder and NGS streams

    public static void StreamOpened(string key, string description)
    {
        Streams[key] = description;
        if (DebugMode.Enabled) Log.Info($"[dbg] {Frame} AUDIO open {description}");
    }

    public static void StreamClosed(string key)
    {
        if (Streams.TryRemove(key, out var d) && DebugMode.Enabled) Log.Info($"[dbg] {Frame} AUDIO release {d}");
    }

    // ------------------------------------------------------------------ reports

    /// <summary>Debug mode was switched on: everything already open, since those open lines were not logged.</summary>
    internal static void OnDebugModeChanged(bool on)
    {
        if (!on) return;
        double now = Now;
        foreach (var p in Open.Keys)
            lock (p.Lock) { p.IOutputs = p.ISilent = p.IUnderruns = p.IGaps = 0; p.IGapSum = p.IGapMax = 0; p.IAt = now; }
        if (Open.IsEmpty && Streams.IsEmpty) return;
        foreach (var l in SnapshotLines()) Log.Info($"[dbg] {Frame} AUDIO snapshot: {l}");
    }

    static IEnumerable<string> SnapshotLines()
    {
        if (DeviceInfo?.Invoke() is { Length: > 0 } dev) yield return "device " + dev;
        foreach (var p in Open.Keys.OrderBy(p => p.Name))
            yield return $"{p.Describe()}, host queue {Pending(p)} samples, {Totals(p)}";
        foreach (var s in Streams.OrderBy(k => k.Key)) yield return s.Value;
    }

    /// <summary>Debug mode, every <see cref="PeriodSeconds"/>: one line per open port for the last interval.</summary>
    public static void Periodic()
    {
        double now = Now;
        foreach (var p in Open.Keys.OrderBy(p => p.Name))
        {
            string line;
            lock (p.Lock)
            {
                if (p.InBurst && now - p.BurstLast > BurstQuietSeconds) EndBurstLocked(p);
                double secs = Math.Max(0.001, now - p.IAt);
                line = $"[dbg] {Frame} AUDIO {p.Name}: {p.IOutputs / secs:F1} outputs/s (expected {p.ExpectedPerSecond:F1}), " +
                       $"gap avg {(p.IGaps > 0 ? p.IGapSum / p.IGaps * 1000 : 0):F2} ms max {p.IGapMax * 1000:F1} ms (grain {p.Grain * 1000.0 / Math.Max(1, p.Freq):F2} ms), " +
                       $"host queue {Pending(p)} samples, underruns {p.IUnderruns}, silent {p.ISilent}/{p.IOutputs} over {secs:F1} s";
                p.IOutputs = p.ISilent = p.IUnderruns = p.IGaps = 0; p.IGapSum = p.IGapMax = 0; p.IAt = now;
            }
            Log.Info(line);
        }
    }

    static string Totals(PortStats p)
    {
        double secs = Math.Max(0.001, (p.ClosedAt > 0 ? p.ClosedAt : Now) - p.OpenedAt);
        double active = p.Gaps > 0 ? p.GapSum : 0;
        return $"{p.Outputs} outputs in {secs:F1} s ({(p.Gaps >= 10 && active > 0 ? $"{p.Gaps / active:F1}" : "-")}/s while active, expected {p.ExpectedPerSecond:F1}), " +
               $"gap avg {(p.Gaps > 0 ? p.GapSum / p.Gaps * 1000 : 0):F2} ms max {p.GapMax * 1000:F1} ms, " +
               $"underruns {p.Underruns} ({p.Underruns / secs * 60:F1}/min) in {p.BurstNumber} bursts, silent {p.Silent}";
    }

    /// <summary>End of session (always): device and every port of the session.</summary>
    public static IEnumerable<string> SummaryLines()
    {
        var all = Closed.Concat(Open.Keys).OrderBy(p => p.OpenedAt).ToList();
        if (all.Count == 0) yield break;
        if (DeviceInfo?.Invoke() is { Length: > 0 } dev) yield return "AUDIO summary: device " + dev;
        foreach (var p in all) yield return $"AUDIO summary: {p.Describe()}{(p.ClosedAt > 0 ? " (released)" : "")}: {Totals(p)}";
    }

    /// <summary>audio.txt of a diagnostic capture.</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"guest frame {DebugMode.Frame}");
        sb.AppendLine("device: " + (DeviceInfo?.Invoke() ?? "(no host audio device: ports are paced by the clock)"));
        sb.AppendLine("open ports:");
        foreach (var p in Open.Keys.OrderBy(p => p.Name))
            sb.AppendLine($"  {p.Describe()}, host queue {Pending(p)} samples, {(p.LastOutput > 0 ? $"last output {(Now - p.LastOutput) * 1000:F0} ms ago" : "no output yet")}; {Totals(p)}");
        sb.AppendLine("released ports:");
        foreach (var p in Closed) sb.AppendLine($"  {p.Describe()}: {Totals(p)}");
        sb.AppendLine("NGS systems and decoder streams:");
        foreach (var s in Streams.OrderBy(k => k.Key)) sb.AppendLine("  " + s.Value);
        return sb.ToString();
    }
}
