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
        // inside Geometry: index min/max scan, index upload, vertex streams; inside Record: pipeline bind, descriptor
        // sets, vertex/index buffer binds, dynamic state + push constants, the draw call
        GeoMinMax, GeoIndex, GeoStreams, RecPipeline, RecSets, RecBuffers, RecDynamic, RecDraw,
        Count,
    }

#if VITASHARP_DEVELOPER
    private static readonly bool s_workerProf = Environment.GetEnvironmentVariable("VITASHARP_WORKER_PROF") == "1";
#else
    private static readonly bool s_workerProf = false;
#endif
    private readonly long[] _profTicks = new long[(int)ProfPhase.Count];
    /// <summary>Managed bytes allocated per phase (same marks as <see cref="_profTicks"/>).</summary>
    private readonly long[] _profPhaseAlloc = new long[(int)ProfPhase.Count];
    private long _profA;
    /// <summary>Texture units resolved: memory, colour target, other.</summary>
    private readonly long[] _profUnits = new long[4];

    [Conditional("VITASHARP_DEVELOPER")]
    private void ProfUnit(TexSource src)
    {
        if (!s_workerProf)
        {
            return;
        }

        TexSource kind = src & TexSource.KindMask;
        int i = (src & TexSource.Memo) != 0 ? 3 : kind is TexSource.Memory or TexSource.MemoryCube ? 0 : kind is TexSource.RenderTarget or TexSource.Region or TexSource.Reinterpret or TexSource.RtMipmapped ? 1 : 2;
        _profUnits[i]++;
        ProfMark(i == 3 ? ProfPhase.TexUnitMemo : i == 0 ? ProfPhase.TexUnitMem : i == 1 ? ProfPhase.TexUnitRt : ProfPhase.TexUnitOther);
    }
    private long _profT, _profExecTicks, _profDrawTicks;
    private readonly Dictionary<Type, long> _profByType = new();
    private long _profQueueSubmitTicks, _profQueuePresentTicks;
    /// <summary>Frames before the measurement starts (VITASHARP_WORKER_PROF_FROM, default 30): a later start leaves the
    /// loading screens' texture uploads out of a gameplay measurement.</summary>
    private static readonly int s_profWarmup = int.TryParse(Environment.GetEnvironmentVariable(Core.Util.EnvVars.WorkerProfFrom), out int pf) && pf > 0 ? pf : 30;

    [Conditional("VITASHARP_DEVELOPER")]
    private void ProfStart()
    {
        if (s_workerProf)
        {
            _profT = Stopwatch.GetTimestamp();
            _profA = GC.GetAllocatedBytesForCurrentThread();
        }
    }

    /// <summary>Charges the time since the last mark to <paramref name="profPhase"/>.</summary>
    [Conditional("VITASHARP_DEVELOPER")]
    private void ProfMark(ProfPhase profPhase)
    {
        if (!s_workerProf)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        _profTicks[(int)profPhase] += now - _profT;
        _profT = now;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        _profPhaseAlloc[(int)profPhase] += allocated - _profA;
        _profA = allocated;
    }

    private void ExecuteProfiled(GpuCommand command)
    {
        long t0 = Stopwatch.GetTimestamp();
        long cpu0 = ThreadCpu();
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        long fence0 = Core.Util.FrameLedger.Total(Core.Util.FrameLedger.Cat.FenceWait);
        ExecuteCommand(command);
        _profCpu += ThreadCpu() - cpu0;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        _profAlloc += allocated;
        if (command is DrawCommand)
        {
            _profDrawAlloc += allocated;
        }
        long dt = Stopwatch.GetTimestamp() - t0 - (Core.Util.FrameLedger.Total(Core.Util.FrameLedger.Cat.FenceWait) - fence0);
        _profExecTicks += dt;
        if (command is DrawCommand)
        {
            _profDrawTicks += dt;
        }
        else
        {
            Type type = command.GetType();
            _profByType[type] = _profByType.GetValueOrDefault(type) + dt;
        }
        // the first frames compile shaders and pipelines: the steady state is measured after them
        if (command is PresentCommand && _workFrames == s_profWarmup)
        {
            _profCpu = 0;
            _profAlloc = _profDrawAlloc = 0;
            _profExecTicks = _profDrawTicks = 0;
            Array.Clear(_profTicks);
            Array.Clear(_profPhaseAlloc);
            Array.Clear(_profUnits);
            _profByType.Clear();
            _profQueueSubmitTicks = _profQueuePresentTicks = 0;
        }
    }

    /// <summary>The worker thread's own CPU spent in Execute since the warmup (<see cref="ThreadCpu"/> units): unlike the
    /// Execute ticks it leaves out the time the thread was preempted (a busy desktop) and the blocking waits.</summary>
    private long _profCpu;
    /// <summary>Managed bytes the worker allocated in Execute since the warmup, all commands and draws only.</summary>
    private long _profAlloc, _profDrawAlloc;

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern nint GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern int QueryThreadCycleTime(nint thread, out ulong cycles);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out TimeSpec time);

    private struct TimeSpec
    {
        public nint Seconds;
        public nint Nanoseconds;
    }

    private static bool s_threadCpuMissing;

    /// <summary>The calling thread's own CPU so far: Windows CPU cycles (QueryThreadCycleTime: precise, unlike its 15.6 ms
    /// thread times), elsewhere ns (CLOCK_THREAD_CPUTIME_ID); 0 when neither is available.</summary>
    private static long ThreadCpu()
    {
        if (s_threadCpuMissing)
        {
            return 0;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return QueryThreadCycleTime(GetCurrentThread(), out ulong cycles) != 0 ? (long)cycles : 0;
            }

            int clock = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? 16 : 3;
            return ClockGetTime(clock, out TimeSpec time) == 0 ? (long)time.Seconds * 1_000_000_000 + time.Nanoseconds : 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            s_threadCpuMissing = true;
            return 0;
        }
    }

    private string WorkerProfReport()
    {
        if (!s_workerProf)
        {
            return "";
        }

        double f = Math.Max(1, _workFrames - s_profWarmup);
        string cpu = OperatingSystem.IsWindows() ? $"{_profCpu / 1e6 / f:F3} M cycles" : $"{_profCpu / 1e6 / f:F3} ms";
        var sb = new System.Text.StringBuilder($"\nPERF worker: {Ms(_profExecTicks) / f:F3} ms/frame in Execute without fence waits (after {s_profWarmup} frames), thread CPU {cpu}/frame, allocated {_profAlloc / 1024.0 / f:F1} KB/frame (draws {_profDrawAlloc / 1024.0 / f:F1}), draws {Ms(_profDrawTicks) / f:F3} (");
        for (int i = 0; i < (int)ProfPhase.Record + 1; i++)
        {
            sb.Append($"{(i > 0 ? " " : "")}{(ProfPhase)i} {Ms(_profTicks[i]) / f:F3}");
        }

        sb.Append("); other commands:");
        foreach ((Type? t, long ticks) in _profByType.OrderByDescending(kv => kv.Value))
        {
            sb.Append($" {t.Name.Replace("Command", "")} {Ms(ticks) / f:F3}");
        }

        sb.Append("; sub-phases:");
        for (int i = (int)ProfPhase.Record + 1; i < (int)ProfPhase.Count; i++)
        {
            sb.Append($" {(ProfPhase)i} {Ms(_profTicks[i]) / f:F3}");
        }

        sb.Append($" vkQueueSubmit {Ms(_profQueueSubmitTicks) / f:F3} vkQueuePresent {Ms(_profQueuePresentTicks) / f:F3}");
        sb.Append("; KB allocated/frame by phase:");
        for (int i = 0; i < (int)ProfPhase.Count; i++)
        {
            if (_profPhaseAlloc[i] / f >= 102.4)
            {
                sb.Append($" {(ProfPhase)i} {_profPhaseAlloc[i] / 1024.0 / f:F1}");
            }
        }

        sb.Append($"; units/frame mem {_profUnits[0] / f:F0} rt {_profUnits[1] / f:F0} other {_profUnits[2] / f:F0} memo {_profUnits[3] / f:F0}");
        return sb.ToString();
    }
}
