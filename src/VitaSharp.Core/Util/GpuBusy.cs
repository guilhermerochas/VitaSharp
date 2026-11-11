namespace VitaSharp.Core.Util;

/// <summary>
/// The host GPU's execution time so far, from the renderer's command-buffer timestamps (the PERF gpu line's source):
/// the Android front end reports its per-frame delta to the system's performance hint session (ADPF GPU work
/// durations), so the system can see a GPU-bound frame. 0 while the renderer has no timestamps.
/// </summary>
public static class GpuBusy
{
    private static long s_nanos;

    /// <summary>Total GPU nanoseconds of the retired command buffers.</summary>
    public static long Nanos => Volatile.Read(ref s_nanos);

    /// <summary>A command buffer retired after <paramref name="nanos"/> of GPU time (the renderer's GPU worker).</summary>
    public static void Add(long nanos) => Interlocked.Add(ref s_nanos, nanos);
}
