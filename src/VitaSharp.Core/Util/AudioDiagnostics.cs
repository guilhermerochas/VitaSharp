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
    private const double BurstQuietSeconds = 1.0;

    /// <summary>Host time without the time the emulation spent paused: a pause is not a gap between outputs.</summary>
    private static double Now => EmulationPause.ActiveHostTicks / (double)Stopwatch.Frequency;

    public sealed class PortStats
    {
        public required string Name;
        public int Grain, Freq, Channels;
        /// <summary>The process that opened the port (its title says whose sound it is), the guest thread that opened
        /// it, the port type (0 MAIN, 1 BGM, 2 VOICE; -1 unknown) and its WAV name in a dump (portN_TYPE).</summary>
        public Emulator? Owner;
        public string OwnerThread = "", DumpName = "";
        public int Type = -1;
        public float VolumeL = 1, VolumeR = 1;
        public IAudioStream? Stream;
        public readonly double OpenedAt = Now;
        public double ClosedAt;
        internal readonly object _lock = new();
        // totals
        public long Outputs, Silent, Underruns, Gaps;
        public double GapSum, GapMax, LastOutput, LastGap;
        internal int _streamUnderrunsSeen;
        // current interval (periodic debug lines)
        internal long _iOutputs, _iSilent, _iUnderruns, _iGaps;
        internal double _iGapSum, _iGapMax, _iAt = Now;
        // underrun bursts
        internal bool _inBurst;
        internal double _burstStart, _burstLast;
        internal long _burstCount, _burstNumber;
        /// <summary>Outputs after an idle pause whose host queue had drained (not underruns).</summary>
        public long IdleResumes;

        public double ExpectedPerSecond => Grain > 0 ? (double)Freq / Grain : 0;

        public string Describe() =>
            $"{Name} grain {Grain} {Freq} Hz {(Channels == 1 ? "mono" : "stereo")} volume {VolumeL:F2}/{VolumeR:F2}";
    }

    private static readonly ConcurrentDictionary<PortStats, byte> s_open = new();
    private static readonly ConcurrentQueue<PortStats> s_closed = new();
    /// <summary>NGS systems and decoder streams (AvPlayer, Atrac, Audiodec): key -> description.</summary>
    private static readonly ConcurrentDictionary<string, string> s_streams = new();

    /// <summary>Host output device description (backend, mixer rate, update size, buffering, latency).</summary>
    public static Func<string>? DeviceInfo { get; set; }

    private static string Frame => $"f{DebugMode.Frame}";

    // ------------------------------------------------------------------ ports

    public static PortStats OpenPort(string name, int grain, int freq, int channels, IAudioStream? stream)
    {
        var p = new PortStats { Name = name, Grain = grain, Freq = freq, Channels = channels, Stream = stream };
        s_open[p] = 0;
        if (DebugMode.Enabled)
        {
            Log.Info($"[dbg] {Frame} AUDIO open {p.Describe()}, expected {p.ExpectedPerSecond:F1} outputs/s{(stream == null ? ", no host stream (paced by the clock)" : "")}");
        }

        return p;
    }

    /// <summary>Grain, rate or channels changed (sceAudioOutSetConfig), possibly with a new host stream.</summary>
    public static void Reconfigure(PortStats portStats, int grain, int freq, int channels, IAudioStream? stream)
    {
        lock (portStats._lock)
        {
            portStats.Grain = grain;
            portStats.Freq = freq;
            portStats.Channels = channels;
            if (!ReferenceEquals(portStats.Stream, stream))
            {
                portStats.Stream = stream;
                portStats._streamUnderrunsSeen = 0;
            }
        }
        if (DebugMode.Enabled)
        {
            Log.Info($"[dbg] {Frame} AUDIO config {portStats.Describe()}");
        }
    }

    public static void ClosePort(PortStats portStats)
    {
        if (!s_open.TryRemove(portStats, out _))
        {
            return;
        }

        portStats.ClosedAt = Now;
        s_closed.Enqueue(portStats);
        while (s_closed.Count > 64 && s_closed.TryDequeue(out _))
        {
        }
        if (DebugMode.Enabled)
        {
            Log.Info($"[dbg] {Frame} AUDIO release {portStats.Describe()}: {Totals(portStats)}");
        }
    }

    /// <summary>sceAppMgrLoadExec replaced the process: its ports count as released, its streams are gone.</summary>
    public static void ProcessEnded()
    {
        foreach (PortStats portStats in s_open.Keys)
        {
            ClosePort(portStats);
        }

        s_streams.Clear();
    }

    /// <summary>One guest output of a grain (called on entry to sceAudioOutOutput, before it blocks).</summary>
    public static void Output(PortStats portStats, bool silent)
    {
        double now = Now;
        lock (portStats._lock)
        {
            if (portStats.LastOutput > 0)
            {
                double gap = now - portStats.LastOutput;
                portStats.LastGap = gap;
                portStats.Gaps++;
                portStats.GapSum += gap;
                if (gap > portStats.GapMax)
                {
                    portStats.GapMax = gap;
                }

                portStats._iGaps++;
                portStats._iGapSum += gap;
                if (gap > portStats._iGapMax)
                {
                    portStats._iGapMax = gap;
                }
            }
            portStats.LastOutput = now;
            portStats.Outputs++;
            portStats._iOutputs++;
            if (silent)
            {
                portStats.Silent++;
                portStats._iSilent++;
            }
        }
    }

    /// <summary>After the host stream accepted a grain: picks up the underruns it detected.</summary>
    public static void AfterSubmit(PortStats portStats)
    {
        if (portStats.Stream is not { } s)
        {
            return;
        }

        int u = s.Underruns;
        if (u == portStats._streamUnderrunsSeen)
        {
            return;
        }

        int n;
        bool idle;
        lock (portStats._lock)
        {
            n = u - portStats._streamUnderrunsSeen;
            portStats._streamUnderrunsSeen = u;
            idle = ResumedAfterIdle(portStats);
        }
        if (n <= 0)
        {
            return;
        }
        // a port that was simply idle (UI sounds of the shell: one short sound now and then) drains its host queue
        // between sounds: that is not a stall of the guest's output, so it is counted apart and not logged
        if (idle)
        {
            lock (portStats._lock)
            {
                portStats.IdleResumes++;
            }

            return;
        }
        Underrun(portStats, n);
    }

    /// <summary>The last output came after a pause much longer than the host queue holds (the guest stopped
    /// outputting and started again): the queue ran dry because the port was idle, not because output was late.</summary>
    internal static bool ResumedAfterIdle(PortStats portStats) =>
        portStats.LastGap > Math.Max(IdleGapSeconds, 4.0 * portStats.Grain / Math.Max(1, portStats.Freq));

    /// <summary>Gap between two outputs from which a port counts as having been idle (no sound to play).</summary>
    private const double IdleGapSeconds = 0.25;

    private static void Underrun(PortStats portStats, int n)
    {
        double now = Now;
        bool start;
        double sincePrev;
        long burst;
        lock (portStats._lock)
        {
            portStats.Underruns += n;
            portStats._iUnderruns += n;
            start = !portStats._inBurst || now - portStats._burstLast > BurstQuietSeconds;
            if (start)
            {
                if (portStats._inBurst)
                {
                    EndBurstLocked(portStats);
                }

                portStats._inBurst = true;
                portStats._burstStart = now;
                portStats._burstCount = 0;
                portStats._burstNumber++;
            }
            portStats._burstCount += n;
            portStats._burstLast = now;
            sincePrev = portStats.LastGap;
            burst = portStats._burstNumber;
        }
        if (!start)
        {
            return;
        }
        // one line per burst (debug mode: every burst; otherwise the 1st-3rd, 10th, 100th... burst)
        if (DebugMode.Enabled || DebugMode.ShouldLog((int)Math.Min(burst, int.MaxValue)))
        {
            Log.Warn($"Audio: underrun burst #{burst} on {portStats.Name} at {Frame}: host queue ran dry, {sincePrev * 1000:F1} ms since the previous output " +
                     $"(grain {portStats.Grain * 1000.0 / Math.Max(1, portStats.Freq):F1} ms), host queue {Pending(portStats)} samples");
        }
    }

    private static void EndBurstLocked(PortStats portStats)
    {
        portStats._inBurst = false;
        if (DebugMode.Enabled)
        {
            Log.Info($"[dbg] {Frame} AUDIO underrun burst #{portStats._burstNumber} on {portStats.Name} ended: {portStats._burstCount} underruns over {(portStats._burstLast - portStats._burstStart) * 1000:F0} ms");
        }
    }

    private static int Pending(PortStats portStats)
    {
        try
        {
            return portStats.Stream?.PendingSamples ?? 0;
        }
        catch
        {
            return -1;
        }
    }

    // ------------------------------------------------------------------ decoder and NGS streams

    public static void StreamOpened(string key, string description)
    {
        s_streams[key] = description;
        if (DebugMode.Enabled)
        {
            Log.Info($"[dbg] {Frame} AUDIO open {description}");
        }
    }

    public static void StreamClosed(string key)
    {
        if (s_streams.TryRemove(key, out string? d) && DebugMode.Enabled)
        {
            Log.Info($"[dbg] {Frame} AUDIO release {d}");
        }
    }

    // ------------------------------------------------------------------ reports

    /// <summary>Debug mode was switched on: everything already open, since those open lines were not logged.</summary>
    internal static void OnDebugModeChanged(bool on)
    {
        if (!on)
        {
            return;
        }

        double now = Now;
        foreach (PortStats portStats in s_open.Keys)
        {
            lock (portStats._lock)
            {
                portStats._iOutputs = portStats._iSilent = portStats._iUnderruns = portStats._iGaps = 0;
                portStats._iGapSum = portStats._iGapMax = 0;
                portStats._iAt = now;
            }
        }

        if (s_open.IsEmpty && s_streams.IsEmpty)
        {
            return;
        }

        foreach (string l in SnapshotLines())
        {
            Log.Info($"[dbg] {Frame} AUDIO snapshot: {l}");
        }
    }

    private static IEnumerable<string> SnapshotLines()
    {
        if (DeviceInfo?.Invoke() is { Length: > 0 } dev)
        {
            yield return "device " + dev;
        }

        foreach (PortStats? portStats in s_open.Keys.OrderBy(p => p.Name))
        {
            yield return $"{portStats.Describe()}, host queue {Pending(portStats)} samples, {Totals(portStats)}";
        }

        foreach (KeyValuePair<string, string> s in s_streams.OrderBy(k => k.Key))
        {
            yield return s.Value;
        }
    }

    /// <summary>Debug mode, every <see cref="PeriodSeconds"/>: one line per open port for the last interval.</summary>
    public static void Periodic()
    {
        double now = Now;
        foreach (PortStats? portStats in s_open.Keys.OrderBy(p => p.Name))
        {
            string line;
            lock (portStats._lock)
            {
                if (portStats._inBurst && now - portStats._burstLast > BurstQuietSeconds)
                {
                    EndBurstLocked(portStats);
                }

                double secs = Math.Max(0.001, now - portStats._iAt);
                line = $"[dbg] {Frame} AUDIO {portStats.Name}: {portStats._iOutputs / secs:F1} outputs/s (expected {portStats.ExpectedPerSecond:F1}), " +
                       $"gap avg {(portStats._iGaps > 0 ? portStats._iGapSum / portStats._iGaps * 1000 : 0):F2} ms max {portStats._iGapMax * 1000:F1} ms (grain {portStats.Grain * 1000.0 / Math.Max(1, portStats.Freq):F2} ms), " +
                       $"host queue {Pending(portStats)} samples, underruns {portStats._iUnderruns}, silent {portStats._iSilent}/{portStats._iOutputs} over {secs:F1} s";
                portStats._iOutputs = portStats._iSilent = portStats._iUnderruns = portStats._iGaps = 0;
                portStats._iGapSum = portStats._iGapMax = 0;
                portStats._iAt = now;
            }
            Log.Info(line);
        }
    }

    private static string Totals(PortStats portStats)
    {
        double secs = Math.Max(0.001, (portStats.ClosedAt > 0 ? portStats.ClosedAt : Now) - portStats.OpenedAt);
        double active = portStats.Gaps > 0 ? portStats.GapSum : 0;
        return $"{portStats.Outputs} outputs in {secs:F1} s ({(portStats.Gaps >= 10 && active > 0 ? $"{portStats.Gaps / active:F1}" : "-")}/s while active, expected {portStats.ExpectedPerSecond:F1}), " +
               $"gap avg {(portStats.Gaps > 0 ? portStats.GapSum / portStats.Gaps * 1000 : 0):F2} ms max {portStats.GapMax * 1000:F1} ms, " +
               $"underruns {portStats.Underruns} ({portStats.Underruns / secs * 60:F1}/min) in {portStats._burstNumber} bursts, silent {portStats.Silent}" +
               (portStats.IdleResumes > 0 ? $", {portStats.IdleResumes} starts after an idle pause (not underruns)" : "");
    }

    /// <summary>End of session (always): device and every port of the session.</summary>
    public static IEnumerable<string> SummaryLines()
    {
        var all = s_closed.Concat(s_open.Keys).OrderBy(p => p.OpenedAt).ToList();
        if (all.Count == 0)
        {
            yield break;
        }

        if (DeviceInfo?.Invoke() is { Length: > 0 } dev)
        {
            yield return "AUDIO summary: device " + dev;
        }

        foreach (PortStats? portStats in all)
        {
            yield return $"AUDIO summary: {portStats.Describe()}{(portStats.ClosedAt > 0 ? " (released)" : "")}: {Totals(portStats)}";
        }
    }

    /// <summary>The open ports, then the released ones still remembered (the last 64).</summary>
    public static List<PortStats> AllPorts() => s_open.Keys.OrderBy(p => p.OpenedAt).Concat(s_closed).ToList();

    /// <summary>The decoder and NGS streams that exist now (descriptions).</summary>
    public static List<string> StreamDescriptions() => s_streams.OrderBy(k => k.Key).Select(k => k.Value).ToList();

    /// <summary>Seconds since the port's last output (host time without pauses), -1 when it never output.</summary>
    public static double SecondsSinceOutput(PortStats portStats) => portStats.LastOutput > 0 ? Now - portStats.LastOutput : -1;

    /// <summary>audio.txt of a diagnostic capture.</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"guest frame {DebugMode.Frame}");
        sb.AppendLine("device: " + (DeviceInfo?.Invoke() ?? "(no host audio device: ports are paced by the clock)"));
        sb.AppendLine("open ports:");
        foreach (PortStats? p in s_open.Keys.OrderBy(p => p.Name))
        {
            sb.AppendLine($"  {p.Describe()}, host queue {Pending(p)} samples, {(p.LastOutput > 0 ? $"last output {(Now - p.LastOutput) * 1000:F0} ms ago" : "no output yet")}; {Totals(p)}");
        }

        sb.AppendLine("released ports:");
        foreach (PortStats p in s_closed)
        {
            sb.AppendLine($"  {p.Describe()}: {Totals(p)}");
        }

        sb.AppendLine("NGS systems and decoder streams:");
        foreach (KeyValuePair<string, string> s in s_streams.OrderBy(k => k.Key))
        {
            sb.AppendLine("  " + s.Value);
        }

        return sb.ToString();
    }
}
