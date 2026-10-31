using VitaSharp.Core.Util;

namespace VitaSharp.Android.Performance;

/// <summary>The kinds of threads the placement knows (from <c>NativeThread.Roles</c> and names).</summary>
public enum ThreadKind { Guest, Gpu, JitWorker, ShaderWorker, Other }

/// <summary>One thread at a placement step: its CPU use over the last window (1.0 = one core all the time) and the
/// order it registered in (the first guest thread is the game's main thread until the loads say otherwise).</summary>
public readonly record struct ThreadSample(int Tid, ThreadKind Kind, string Name, double Load, long Order);

/// <summary>Where a thread may run.</summary>
public enum Placement
{
    /// <summary>Left as the system placed it.</summary>
    Unchanged,
    /// <summary>The fastest cluster.</summary>
    Big,
    /// <summary>The second fastest cluster (three-cluster SoCs).</summary>
    Mid,
    /// <summary>Every core but the fastest cluster's.</summary>
    NotBig,
    /// <summary>Every core.</summary>
    All,
}

/// <summary>A placement decision: the cores of each thread and the hot threads (the ADPF session's).</summary>
public sealed record PlacementPlan(IReadOnlyDictionary<int, Placement> Placements, int[] HotTids, int MainTid, int GpuTid, bool ShadersOnBig)
{
    public static readonly PlacementPlan None = new(new Dictionary<int, Placement>(), [], 0, 0, false);
}

/// <summary>
/// Chooses the cores of the emulator's threads on a phone with fast and slow cores (docs/android/README.md, "CPU cores
/// and ADPF"):
/// <list type="bullet">
/// <item>the hot threads go to the fastest cluster: the busiest guest thread (the game's main thread; until there are
/// loads, the first guest thread), the GPU worker, then other guest threads that use at least <see cref="HotLoad"/> of
/// a core, as long as there is a fast core for each;</item>
/// <item>with a single fast core (1+3+4 layouts) the main guest thread gets it and the GPU worker goes to the mid
/// cluster (the slow cores when there is none);</item>
/// <item>the other guest threads (audio, file and job threads) and the JIT compiler workers run on the other cores, so
/// they never take a fast core's time from a hot thread; a guest thread that becomes busy is moved at the next step;</item>
/// <item>shader compile workers run on the other cores while the hot threads keep the fast cores busy, and on every
/// core while they leave at least half a fast core idle (the GPU thread waiting for a shader, a loading screen): a
/// compile the frame waits for then runs at full speed, and one in the background does not slow the game;</item>
/// <item>any other thread (the UI thread, the driver's and the runtime's) is left alone.</item>
/// </list>
/// The main thread changes only when another guest thread is clearly busier (hysteresis), so the pinning does not flap
/// between two similar threads. One planner per process; <see cref="Plan"/> is called about once a second.
/// </summary>
public sealed class ThreadPlacementPlanner
{
    /// <summary>A guest thread using at least this much of a core is hot.</summary>
    public const double HotLoad = 0.3;

    readonly CpuTopology _topology;
    int _main;
    bool _shadersOnBig;

    public ThreadPlacementPlanner(CpuTopology topology) { _topology = topology; }

    public CpuTopology Topology => _topology;

    /// <summary>False when the phone has a single kind of core (nothing to choose).</summary>
    public bool CanPlace => _topology.Big != null;

    /// <summary>The mask of a placement (0 = unchanged).</summary>
    public ulong MaskOf(Placement p) => p switch
    {
        Placement.Big => _topology.Big?.Mask ?? 0,
        Placement.Mid => _topology.Mid?.Mask ?? _topology.NotBigMask,
        Placement.NotBig => _topology.NotBigMask,
        Placement.All => _topology.AllMask,
        _ => 0,
    };

    public PlacementPlan Plan(IReadOnlyList<ThreadSample> threads)
    {
        if (_topology.Big is not { } big) return PlacementPlan.None;
        int slots = big.Cpus.Length;
        var placements = new Dictionary<int, Placement>();
        var guests = threads.Where(t => t.Kind == ThreadKind.Guest).ToList();

        // the main thread: the busiest guest thread, kept until another one is clearly busier
        var current = guests.FirstOrDefault(t => t.Tid == _main);
        var busiest = guests.OrderByDescending(t => t.Load).ThenBy(t => t.Order).FirstOrDefault();
        if (current.Tid == 0) _main = guests.Count == 0 ? 0 : busiest.Load > 0 ? busiest.Tid : guests.MinBy(t => t.Order).Tid;
        else if (busiest.Tid != _main && busiest.Load > current.Load * 1.5 + 0.1) _main = busiest.Tid;

        var hot = new List<int>();
        if (_main != 0) { hot.Add(_main); placements[_main] = Placement.Big; }
        var gpu = threads.FirstOrDefault(t => t.Kind == ThreadKind.Gpu);
        if (gpu.Tid != 0)
        {
            hot.Add(gpu.Tid);
            placements[gpu.Tid] = hot.Count <= slots ? Placement.Big : Placement.Mid;
        }
        foreach (var g in guests.Where(g => g.Tid != _main).OrderByDescending(g => g.Load).ThenBy(g => g.Order))
        {
            if (g.Load >= HotLoad && hot.Count < slots) { hot.Add(g.Tid); placements[g.Tid] = Placement.Big; }
            else placements[g.Tid] = Placement.NotBig;
        }

        // idle fast-core time over the window decides where the shader workers may run (with hysteresis)
        double hotLoad = threads.Where(t => placements.TryGetValue(t.Tid, out var p) && p == Placement.Big).Sum(t => t.Load);
        if (_shadersOnBig && hotLoad > slots - 0.25) _shadersOnBig = false;
        else if (!_shadersOnBig && hotLoad < slots - 0.5) _shadersOnBig = true;

        foreach (var t in threads)
        {
            if (t.Kind == ThreadKind.JitWorker) placements[t.Tid] = Placement.NotBig;
            else if (t.Kind == ThreadKind.ShaderWorker) placements[t.Tid] = _shadersOnBig ? Placement.All : Placement.NotBig;
        }
        return new PlacementPlan(placements, hot.ToArray(), _main, gpu.Tid, _shadersOnBig);
    }

    /// <summary>The thread kind from the core's registered role and the thread's name (the renderer's shader workers
    /// register a name only).</summary>
    public static ThreadKind Classify(NativeThread.Role role, string name) => role switch
    {
        NativeThread.Role.Guest => ThreadKind.Guest,
        NativeThread.Role.Gpu => ThreadKind.Gpu,
        NativeThread.Role.JitWorker => ThreadKind.JitWorker,
        _ => name.StartsWith("Shader worker", StringComparison.Ordinal) || name.StartsWith("Shader prewarm", StringComparison.Ordinal)
             || name.StartsWith("Shader rebuild", StringComparison.Ordinal) ? ThreadKind.ShaderWorker
           : name.StartsWith("JIT worker", StringComparison.Ordinal) ? ThreadKind.JitWorker
           : ThreadKind.Other,
    };
}
