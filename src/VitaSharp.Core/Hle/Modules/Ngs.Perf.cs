using System.Diagnostics;
using VitaSharp.Core.Hle.Audio;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// "PERF ngs" in the periodic summary: what one sceNgsSystemUpdate costs on the host, split by module kind (the NGS
/// mixing runs on the game's own audio thread, so its cost shows up as that guest thread's CPU time). Always on:
/// two timestamps per module and update.
/// </summary>
public sealed partial class Ngs
{
    static long s_updates, s_voices, s_updateTicks, s_deliverTicks, s_at9DecodeTicks, s_at9Superframes, s_callbackTicks;
    static readonly long[] s_kindTicks = new long[Enum.GetValues<NgsModuleKind>().Length];
    static readonly long[] s_kindCount = new long[s_kindTicks.Length];

    static void CountModule(NgsModuleKind kind, long ticks)
    {
        Interlocked.Add(ref s_kindTicks[(int)kind], ticks);
        Interlocked.Increment(ref s_kindCount[(int)kind]);
    }

    /// <summary>One line, empty before the first update.</summary>
    public static IEnumerable<string> PerfLines()
    {
        long n = Interlocked.Read(ref s_updates);
        if (n == 0) yield break;
        double us(long t) => t * 1e6 / Stopwatch.Frequency / n;
        var kinds = Enumerable.Range(0, s_kindTicks.Length).Where(i => s_kindCount[i] > 0)
            .Select(i => $"{(NgsModuleKind)i} {us(s_kindTicks[i]):F0} ({s_kindCount[i] / (double)n:F1}/update)");
        long frames = Math.Max(1, Display.FramesPresented);
        yield return $"PERF ngs: {n} updates ({n / (double)frames:F1}/frame), {s_voices / (double)n:F1} voices/update; us per update: total {us(s_updateTicks):F0}, " +
                     $"modules {string.Join(", ", kinds)}; ATRAC9 decode {us(s_at9DecodeTicks):F0} ({s_at9Superframes / (double)n:F2} superframes/update), " +
                     $"mix to patches {us(s_deliverTicks):F0}, callbacks {us(s_callbackTicks):F0}; ATRAC9 stages " +
                     string.Join(", ", VitaSharp.Core.Hle.Audio.Atrac9.Atrac9Decoder.StageNames.Select((name, i) => $"{name} {us(VitaSharp.Core.Hle.Audio.Atrac9.Atrac9Decoder.StageTicks[i]):F0}"));
    }
}
