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

    static readonly long[] Totals = new long[(int)Cat.Count];
    const int Ring = 8192;
    static long[]? _rows; // per frame deltas, Ring x Count
    static readonly long[] Last = new long[(int)Cat.Count];
    public static bool Enabled { get; private set; }


    public static void Enable()
    {
        _rows ??= new long[Ring * (int)Cat.Count];
        Enabled = true;
    }

    public static long Now() => Stopwatch.GetTimestamp();

    public static void Add(Cat c, long ticks) => Interlocked.Add(ref Totals[(int)c], ticks);

    public static void AddSince(Cat c, long t0) => Interlocked.Add(ref Totals[(int)c], Stopwatch.GetTimestamp() - t0);

    public readonly struct Scope(Cat c) : IDisposable
    {
        readonly long _t0 = Stopwatch.GetTimestamp();
        public void Dispose() => Interlocked.Add(ref Totals[(int)c], Stopwatch.GetTimestamp() - _t0);
    }

    public static Scope Time(Cat c) => new(c);

    public static long Total(Cat c) => Interlocked.Read(ref Totals[(int)c]);

    /// <summary>Called at each present (guest thread): stores the per-cause deltas of the frame that just ended.</summary>
    public static void OnPresent(long frame)
    {
        var rows = _rows;
        if (rows == null) return;
        Totals[(int)Cat.GcPause] = GC.GetTotalPauseDuration().Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        Totals[(int)Cat.PacingSleep] = Cpu.CpuCore.SleepStats[0].Actual + Cpu.CpuCore.SleepStats[1].Actual;
        int baseIdx = (int)(frame & (Ring - 1)) * (int)Cat.Count;
        for (int i = 0; i < (int)Cat.Count; i++)
        {
            long t = Interlocked.Read(ref Totals[i]);
            rows[baseIdx + i] = t - Last[i];
            Last[i] = t;
        }
    }

    /// <summary>Milliseconds per cause for frames [from, to] (inclusive; must be within the last 8192 frames).</summary>
    public static double[] Window(long from, long to)
    {
        var r = new double[(int)Cat.Count];
        var rows = _rows;
        if (rows == null) return r;
        for (long f = from; f <= to; f++)
        {
            int b = (int)(f & (Ring - 1)) * (int)Cat.Count;
            for (int i = 0; i < r.Length; i++) r[i] += rows[b + i] * 1000.0 / Stopwatch.Frequency;
        }
        return r;
    }

    public static string Format(double[] ms, double minMs = 0.5) =>
        string.Join(" ", Enumerable.Range(0, ms.Length).Where(i => ms[i] >= minMs).Select(i => $"{Names[i]}={ms[i]:F0}"));
}
