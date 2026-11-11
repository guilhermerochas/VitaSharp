using System.Diagnostics;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Developer builds, VITASHARP_WORKER_PROF=1: CPU time of the GPU worker inside the renderer per presented frame, the
/// draws split by phase (PERF worker line). The calls compile away in user builds ([Conditional]).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    internal enum ProfPhase
    {
        Shader, TexSets, Texture, Pipeline, Geometry, Uniform, Begin, Record,
        // inside TexSets (BuildTextureSet): before the lookup, a reused set, resolving the units, allocating and writing
        TexPre, TexHit, TexResolve, TexAlloc,
        // inside TexResolve: one unit resolved to guest memory / a colour target / anything else, then its sampler
        TexUnitMem, TexUnitRt, TexUnitOther, TexSampler, TexUnitMemo,
        // inside a guest-memory unit that is not this frame's first check: lookups, layout + dirty check, hash, upload
        TexMemLookup, TexMemCheck, TexMemHash, TexMemUpload,
        // inside Present: frame begin + rendering end, the rest before the present, swapchain acquire, blit, submit, retire
        PresBegin, PresPrep, PresAcquire, PresBlit, PresSubmit, PresRetire,
        // inside BeginScene: retire poll, frame begin, rendering end, colour target lookup, depth target lookup
        BsPoll, BsFrame, BsEnd, BsTarget, BsDepth,
        Count,
    }

#if VITASHARP_DEVELOPER
    static readonly bool WorkerProf = Environment.GetEnvironmentVariable("VITASHARP_WORKER_PROF") == "1";
#else
    static readonly bool WorkerProf = false;
#endif
    readonly long[] _profTicks = new long[(int)ProfPhase.Count];
    /// <summary>Texture units resolved: memory, colour target, other.</summary>
    readonly long[] _profUnits = new long[4];

    [Conditional("VITASHARP_DEVELOPER")]
    void ProfUnit(TexSource src)
    {
        if (!WorkerProf)
        {
            return;
        }

        var kind = src & TexSource.KindMask;
        int i = (src & TexSource.Memo) != 0 ? 3 : kind is TexSource.Memory or TexSource.MemoryCube ? 0 : kind is TexSource.RenderTarget or TexSource.Region or TexSource.Reinterpret or TexSource.RtMipmapped ? 1 : 2;
        _profUnits[i]++;
        ProfMark(i == 3 ? ProfPhase.TexUnitMemo : i == 0 ? ProfPhase.TexUnitMem : i == 1 ? ProfPhase.TexUnitRt : ProfPhase.TexUnitOther);
    }
    long _profT, _profExecTicks, _profDrawTicks;
    readonly Dictionary<Type, long> _profByType = new();
    long _profQueueSubmitTicks, _profQueuePresentTicks;
    /// <summary>Frames before the measurement starts (VITASHARP_WORKER_PROF_FROM, default 30): a later start leaves the
    /// loading screens' texture uploads out of a gameplay measurement.</summary>
    static readonly int ProfWarmup = int.TryParse(Environment.GetEnvironmentVariable(Core.Util.EnvVars.WorkerProfFrom), out int pf) && pf > 0 ? pf : 30;

    [Conditional("VITASHARP_DEVELOPER")]
    void ProfStart()
    {
        if (WorkerProf)
        {
            _profT = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Charges the time since the last mark to <paramref name="p"/>.</summary>
    [Conditional("VITASHARP_DEVELOPER")]
    void ProfMark(ProfPhase p)
    {
        if (!WorkerProf)
        {
            return;
        }

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
        if (command is DrawCommand)
        {
            _profDrawTicks += dt;
        }
        else
        {
            var type = command.GetType();
            _profByType[type] = _profByType.GetValueOrDefault(type) + dt;
        }
        // the first frames compile shaders and pipelines: the steady state is measured after them
        if (command is PresentCommand && _workFrames == ProfWarmup)
        {
            _profExecTicks = _profDrawTicks = 0;
            Array.Clear(_profTicks);
            Array.Clear(_profUnits);
            _profByType.Clear();
            _profQueueSubmitTicks = _profQueuePresentTicks = 0;
        }
    }

    string WorkerProfReport()
    {
        if (!WorkerProf)
        {
            return "";
        }

        double f = Math.Max(1, _workFrames - ProfWarmup);
        var sb = new System.Text.StringBuilder($"\nPERF worker: {Ms(_profExecTicks) / f:F3} ms/frame in Execute without fence waits (after {ProfWarmup} frames), draws {Ms(_profDrawTicks) / f:F3} (");
        for (int i = 0; i < (int)ProfPhase.Record + 1; i++)
        {
            sb.Append($"{(i > 0 ? " " : "")}{(ProfPhase)i} {Ms(_profTicks[i]) / f:F3}");
        }

        sb.Append("); other commands:");
        foreach (var (t, ticks) in _profByType.OrderByDescending(kv => kv.Value))
        {
            sb.Append($" {t.Name.Replace("Command", "")} {Ms(ticks) / f:F3}");
        }

        sb.Append("; sub-phases:");
        for (int i = (int)ProfPhase.Record + 1; i < (int)ProfPhase.Count; i++)
        {
            sb.Append($" {(ProfPhase)i} {Ms(_profTicks[i]) / f:F3}");
        }

        sb.Append($" vkQueueSubmit {Ms(_profQueueSubmitTicks) / f:F3} vkQueuePresent {Ms(_profQueuePresentTicks) / f:F3}");
        sb.Append($"; units/frame mem {_profUnits[0] / f:F0} rt {_profUnits[1] / f:F0} other {_profUnits[2] / f:F0} memo {_profUnits[3] / f:F0}");
        return sb.ToString();
    }
}
