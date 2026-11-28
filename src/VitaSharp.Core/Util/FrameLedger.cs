using System.Diagnostics;

namespace VitaSharp.Core.Util;

/// <summary>
/// Hitch attribution: host time spent per cause, sampled per presented frame. The sites are coarse (one shader, one
/// pipeline, one texture, one guest wait) so the cost is two timestamps and an interlocked add per event; the per-frame
/// rows are only kept once <see cref="Enabled"/> is set (--profile), the totals always.
/// </summary>
public static class FrameLedger
{
    public enum Cat
    {
        /// <summary>GXP to GLSL translation (GPU thread).</summary>
        ShaderTranslate,
        /// <summary>shaderc GLSL to SPIR-V (any thread).</summary>
        ShaderCompile,
        /// <summary>shader pack read + vkCreateShaderModule on the GPU thread.</summary>
        ShaderPackLoad,
        /// <summary>vkCreateGraphicsPipelines on the GPU thread.</summary>
        PipelineCreate,
        /// <summary>texture CPU decode + staging copy (GPU thread).</summary>
        TextureUpload,
        /// <summary>GPU thread waiting for a frame fence (writeback / results publication / finish).</summary>
        FenceWait,
        /// <summary>render-target writeback copies into guest memory (GPU thread, after the fence).</summary>
        Writeback,
        /// <summary>guest threads blocked on the GPU (sceGxmFinish, notification wait, display queue).</summary>
        GuestGpuWait,
        /// <summary>guest file reads (sceIoRead/sceIoPread and friends).</summary>
        FileIo,
        /// <summary>JIT block compilation (background workers: reported for reference).</summary>
        JitCompile,
        /// <summary>GPU worker idle time (waiting for commands): busy = frame interval - idle.</summary>
        GpuIdle,
        /// <summary>CPU pacing sleeps (from CpuCore.SleepStats, filled at the snapshot).</summary>
        PacingSleep,
        /// <summary>GC pause time (GC.GetTotalPauseDuration, filled at the snapshot).</summary>
        GcPause,
        /// <summary>GPU thread waiting for a shader queued to the background workers.</summary>
        ShaderWait,
        /// <summary>pipeline creation on the background workers (reference).</summary>
        BgPipeline,
        /// <summary>shader translation + compilation on the background workers (reference).</summary>
        BgShader,
        Count,
    }

    public static readonly string[] Names = ["shTrans", "shaderc", "shPack", "pipeline", "texUpload", "fence", "writeback", "guestGpuWait", "fileIo", "jit", "gpuIdle", "pacing", "gc", "shWait", "bgPipeline", "bgShader"];

    private static readonly long[] s_totals = new long[(int)Cat.Count];
    private const int Ring = 8192;
    private static long[]? s_rows; // per frame deltas, Ring x Count
    private static readonly long[] s_last = new long[(int)Cat.Count];
    public static bool Enabled { get; private set; }


    public static void Enable()
    {
        s_rows ??= new long[Ring * (int)Cat.Count];
        Enabled = true;
    }

    public static long Now() => Stopwatch.GetTimestamp();

    public static void Add(Cat c, long ticks) => Interlocked.Add(ref s_totals[(int)c], ticks);

    public static void AddSince(Cat c, long t0) => Interlocked.Add(ref s_totals[(int)c], Stopwatch.GetTimestamp() - t0);

    /// <summary>A timed site. Guest waits on the GPU count only the time the emulation ran: a wait that spans a pause
    /// (pause menu, save state) is not the GPU's, and charged whole it made the capture blame the GPU for the pause.</summary>
    public readonly struct Scope(Cat c) : IDisposable
    {
        private readonly long _start = Stamp(c);
        public void Dispose() => Interlocked.Add(ref s_totals[(int)c], Stamp(c) - _start);
    }

    /// <summary>Host ticks for <paramref name="c"/>: <see cref="EmulationPause.ActiveHostTicks"/> (standing still while
    /// paused) for guest waits, the plain timestamp for the host-side work.</summary>
    private static long Stamp(Cat c) => c == Cat.GuestGpuWait ? EmulationPause.ActiveHostTicks : Stopwatch.GetTimestamp();

    public static Scope Time(Cat c) => new(c);

    public static long Total(Cat c) => Interlocked.Read(ref s_totals[(int)c]);

    /// <summary>VITASHARP_HITCH_MS: a present this long after the previous one (emulation time, pauses excluded) logs a
    /// <c>[hitch]</c> line with the per-cause host time of the interval (default 100 ms; 0 turns it off).</summary>
    private static readonly double s_hitchMs = double.TryParse(Environment.GetEnvironmentVariable(EnvVars.HitchMs), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double hitchMs) ? hitchMs : 100;
    private static readonly long[] s_hitchLast = new long[(int)Cat.Count];
    private static long s_hitchPresentTicks, s_hitchGcCount;
    /// <summary>Texture levels served by the ASTC disk cache from a file read on the caller's (GPU) thread, and from
    /// the title-start read-ahead (TranscodeDiskCache): the [hitch] line counts them next to the texture uploads.</summary>
    public static long AstcDiskFileReads, AstcDiskReadAheadHits;
    /// <summary>Where a texture upload's time goes (the [hitch] line splits texUpload by them): ASTC encodes run by
    /// the caller (levels and ticks, including its wait for the transcoder threads), the ASTC disk cache's file
    /// lookups on the caller's thread, and the levels handed to the cache's background writer (with the writer's own
    /// file time, which is not the GPU thread's).</summary>
    public static long AstcEncodedLevels, AstcEncodeTicks, AstcDiskIoTicks, AstcDiskWritesQueued, AstcDiskWriteTicks;
    private static long s_hitchDiskFile, s_hitchDiskAhead;
    private static TexUploadDetail s_hitchTex;

    /// <summary>The texture-upload counters at one point (<see cref="AstcEncodedLevels"/> and the others).</summary>
    internal readonly record struct TexUploadDetail(long EncodedLevels, long EncodeTicks, long DiskIoTicks, long WritesQueued, long WriteTicks)
    {
        public static TexUploadDetail Now() => new(Interlocked.Read(ref AstcEncodedLevels), Interlocked.Read(ref AstcEncodeTicks),
            Interlocked.Read(ref AstcDiskIoTicks), Interlocked.Read(ref AstcDiskWritesQueued), Interlocked.Read(ref AstcDiskWriteTicks));

        public TexUploadDetail Since(TexUploadDetail earlier) => new(EncodedLevels - earlier.EncodedLevels, EncodeTicks - earlier.EncodeTicks,
            DiskIoTicks - earlier.DiskIoTicks, WritesQueued - earlier.WritesQueued, WriteTicks - earlier.WriteTicks);
    }
    private static int s_hitchLogged;

    /// <summary>
    /// Every present (guest thread), whether or not per-frame rows are kept: a present that comes more than
    /// <see cref="s_hitchMs"/> after the previous one logs what the host did in between, by cause (texture uploads,
    /// shader and pipeline work, the GC, JIT compilation, file reads, the guest waiting for the GPU), so a hitch the
    /// player sees is in the log without a capture. Costs one timestamp per present; a line only on a hitch.
    /// </summary>
    private static void NoteHitch(long frame)
    {
        if (s_hitchMs <= 0)
        {
            return;
        }

        long now = EmulationPause.ActiveHostTicks;
        long prev = s_hitchPresentTicks;
        s_hitchPresentTicks = now;
        double ms = (now - prev) * 1000.0 / Stopwatch.Frequency;
        if (prev == 0 || ms < s_hitchMs)
        {
            SnapshotHitch();
            return;
        }

        s_totals[(int)Cat.GcPause] = GC.GetTotalPauseDuration().Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        s_totals[(int)Cat.PacingSleep] = Cpu.CpuCore.SleepStats[0].Actual + Cpu.CpuCore.SleepStats[1].Actual;
        int gcs = GC.CollectionCount(0) - (int)s_hitchGcCount;
        string? line = HitchLine(frame, ms, s_totals, s_hitchLast, gcs,
            (int)(Interlocked.Read(ref AstcDiskFileReads) - s_hitchDiskFile), (int)(Interlocked.Read(ref AstcDiskReadAheadHits) - s_hitchDiskAhead),
            TexUploadDetail.Now().Since(s_hitchTex));
        SnapshotHitch();
        // a long run of them (a load) is summarised by the periodic report: the first 200 only
        if (line != null && Interlocked.Increment(ref s_hitchLogged) <= 200)
        {
            Log.Info(line);
        }
    }

    private static void SnapshotHitch()
    {
        s_totals[(int)Cat.GcPause] = GC.GetTotalPauseDuration().Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        s_totals[(int)Cat.PacingSleep] = Cpu.CpuCore.SleepStats[0].Actual + Cpu.CpuCore.SleepStats[1].Actual;
        for (int i = 0; i < (int)Cat.Count; i++)
        {
            s_hitchLast[i] = Interlocked.Read(ref s_totals[i]);
        }

        s_hitchGcCount = GC.CollectionCount(0);
        s_hitchDiskFile = Interlocked.Read(ref AstcDiskFileReads);
        s_hitchDiskAhead = Interlocked.Read(ref AstcDiskReadAheadHits);
        s_hitchTex = TexUploadDetail.Now();
    }

    /// <summary>The <c>[hitch]</c> line: the interval, then each cause that took at least 1 ms of it, largest first.
    /// Pure, unit-tested (FrameLedgerHitchTests).</summary>
    internal static string? HitchLine(long frame, double intervalMs, ReadOnlySpan<long> totals, ReadOnlySpan<long> last, int gcs, int diskFileReads = 0, int diskReadAhead = 0,
        TexUploadDetail tex = default)
    {
        if (totals.Length < (int)Cat.Count || last.Length < (int)Cat.Count)
        {
            return null;
        }

        Span<double> ms = stackalloc double[(int)Cat.Count];
        for (int i = 0; i < (int)Cat.Count; i++)
        {
            ms[i] = (totals[i] - last[i]) * 1000.0 / Stopwatch.Frequency;
        }

        var sb = new System.Text.StringBuilder(160);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"[hitch] present {frame}: {intervalMs:F0} ms since the previous one;");
        bool any = false;
        Span<bool> done = stackalloc bool[(int)Cat.Count];
        done.Clear(); // the assembly skips locals init: stackalloc is not zeroed
        for (int n = 0; n < (int)Cat.Count; n++)
        {
            int best = -1;
            for (int i = 0; i < (int)Cat.Count; i++)
            {
                if (!done[i] && ms[i] >= 1 && (best < 0 || ms[i] > ms[best]))
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            done[best] = true;
            any = true;
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $" {Names[best]} {ms[best]:F0} ms");
            if (best == (int)Cat.GcPause && gcs > 0)
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $" ({gcs} collections)");
            }

            if (best == (int)Cat.TextureUpload)
            {
                AppendTexUploadDetail(sb, ms[best], diskFileReads, diskReadAhead, tex);
            }

            sb.Append(',');
        }

        if (!any)
        {
            sb.Append(" no measured cause (guest code, GPU, or an unmeasured host wait)");
        }
        else
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    /// <summary>texUpload split by stage: ASTC encodes (and waits for the transcoder threads) on the uploading thread,
    /// the ASTC disk cache's file lookups there, the levels read from the disk cache and its read-ahead, the levels
    /// queued for the background writer (with the writer's own file time), and the rest (decode, staging copy).</summary>
    private static void AppendTexUploadDetail(System.Text.StringBuilder sb, double uploadMs, int diskFileReads, int diskReadAhead, TexUploadDetail tex)
    {
        double encodeMs = tex.EncodeTicks * 1000.0 / Stopwatch.Frequency;
        double diskMs = tex.DiskIoTicks * 1000.0 / Stopwatch.Frequency;
        if (diskFileReads + diskReadAhead == 0 && tex.EncodedLevels == 0 && tex.WritesQueued == 0 && encodeMs < 1 && diskMs < 1)
        {
            return;
        }

        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append(inv, $" (ASTC encode {encodeMs:F0} ms for {tex.EncodedLevels} levels, ASTC disk cache file reads {diskMs:F0} ms on the GPU thread");
        sb.Append(inv, $", {diskFileReads} levels read from the ASTC disk cache on the GPU thread, {diskReadAhead} from its read-ahead");
        if (tex.WritesQueued > 0)
        {
            sb.Append(inv, $", {tex.WritesQueued} levels queued for the disk cache writer (its file writes {tex.WriteTicks * 1000.0 / Stopwatch.Frequency:F0} ms, off the GPU thread)");
        }

        sb.Append(inv, $", decode and staging copy {Math.Max(uploadMs - encodeMs - diskMs, 0):F0} ms)");
    }

    /// <summary>Called at each present (guest thread): stores the per-cause deltas of the frame that just ended.</summary>
    public static void OnPresent(long frame)
    {
        NoteHitch(frame);
        long[]? rows = s_rows;
        if (rows == null)
        {
            return;
        }

        s_totals[(int)Cat.GcPause] = GC.GetTotalPauseDuration().Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        s_totals[(int)Cat.PacingSleep] = Cpu.CpuCore.SleepStats[0].Actual + Cpu.CpuCore.SleepStats[1].Actual;
        int baseIdx = (int)(frame & (Ring - 1)) * (int)Cat.Count;
        for (int i = 0; i < (int)Cat.Count; i++)
        {
            long t = Interlocked.Read(ref s_totals[i]);
            rows[baseIdx + i] = t - s_last[i];
            s_last[i] = t;
        }
    }

    /// <summary>Milliseconds per cause for frames [from, to] (inclusive; must be within the last 8192 frames).</summary>
    public static double[] Window(long from, long to)
    {
        double[] r = new double[(int)Cat.Count];
        long[]? rows = s_rows;
        if (rows == null)
        {
            return r;
        }

        for (long f = from; f <= to; f++)
        {
            int b = (int)(f & (Ring - 1)) * (int)Cat.Count;
            for (int i = 0; i < r.Length; i++)
            {
                r[i] += rows[b + i] * 1000.0 / Stopwatch.Frequency;
            }
        }
        return r;
    }

    public static string Format(double[] ms, double minMs = 0.5) =>
        string.Join(" ", Enumerable.Range(0, ms.Length).Where(i => ms[i] >= minMs).Select(i => $"{Names[i]}={ms[i]:F0}"));
}
