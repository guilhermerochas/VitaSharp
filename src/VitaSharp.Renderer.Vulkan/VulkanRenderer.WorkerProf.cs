using System.Diagnostics;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Developer builds, VITASHARP_WORKER_PROF=1: CPU time of the GPU worker inside the renderer per presented frame, the
/// draws split by phase (PERF worker line). The calls compile away in user builds ([Conditional]).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    internal enum ProfPhase { Shader, TexSets, Texture, Pipeline, Geometry, Uniform, Begin, Record, Count }

#if VITASHARP_DEVELOPER
    static readonly bool WorkerProf = Environment.GetEnvironmentVariable("VITASHARP_WORKER_PROF") == "1";
#else
    static readonly bool WorkerProf = false;
#endif
    readonly long[] _profTicks = new long[(int)ProfPhase.Count];
    long _profT, _profExecTicks, _profDrawTicks;
    readonly Dictionary<Type, long> _profByType = new();
    const int ProfWarmup = 30;

    [Conditional("VITASHARP_DEVELOPER")]
    void ProfStart() { if (WorkerProf) _profT = Stopwatch.GetTimestamp(); }

    /// <summary>Charges the time since the last mark to <paramref name="p"/>.</summary>
    [Conditional("VITASHARP_DEVELOPER")]
    void ProfMark(ProfPhase p)
    {
        if (!WorkerProf) return;
        long now = Stopwatch.GetTimestamp();
        _profTicks[(int)p] += now - _profT;
        _profT = now;
    }

    void ExecuteProfiled(GpuCommand command)
    {
        long t0 = Stopwatch.GetTimestamp();
        long fence0 = Core.Util.FrameLedger.Total(Core.Util.FrameLedger.Cat.FenceWait);
        ExecuteCommand(command);
        long dt = Stopwatch.GetTimestamp() - t0 - (Core.Util.FrameLedger.Total(Core.Util.FrameLedger.Cat.FenceWait) - fence0);
        _profExecTicks += dt;
        if (command is DrawCommand) _profDrawTicks += dt;
        else
        {
            var type = command.GetType();
            _profByType[type] = _profByType.GetValueOrDefault(type) + dt;
        }
        // the first frames compile shaders and pipelines: the steady state is measured after them
        if (command is PresentCommand && _workFrames == ProfWarmup) { _profExecTicks = _profDrawTicks = 0; Array.Clear(_profTicks); _profByType.Clear(); }
    }

    string WorkerProfReport()
    {
        if (!WorkerProf) return "";
        double f = Math.Max(1, _workFrames - ProfWarmup);
        var sb = new System.Text.StringBuilder($"\nPERF worker: {Ms(_profExecTicks) / f:F3} ms/frame in Execute without fence waits (after {ProfWarmup} frames), draws {Ms(_profDrawTicks) / f:F3} (");
        for (int i = 0; i < (int)ProfPhase.Count; i++) sb.Append($"{(i > 0 ? " " : "")}{(ProfPhase)i} {Ms(_profTicks[i]) / f:F3}");
        sb.Append("); other commands:");
        foreach (var (t, ticks) in _profByType.OrderByDescending(kv => kv.Value)) sb.Append($" {t.Name.Replace("Command", "")} {Ms(ticks) / f:F3}");
        return sb.ToString();
    }
}
