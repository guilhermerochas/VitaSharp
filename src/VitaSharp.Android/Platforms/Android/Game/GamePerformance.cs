using System.Diagnostics;
using System.Runtime.InteropServices;
using Android.Content;
using Android.OS;
using VitaSharp.Android.Services;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Performance;

namespace VitaSharp.Android.Game;

/// <summary>
/// Speed of the game process on a phone (docs/android/README.md, "CPU cores and ADPF"):
/// <list type="bullet">
/// <item><b>Placement</b>: about once a second the emulator's registered threads (<see cref="NativeThread.Roles"/>) are
/// sampled (CPU time through their thread CPU clocks) and placed with <c>sched_setaffinity</c> by
/// <see cref="ThreadPlacementPlanner"/>: main guest thread and GPU worker on the fastest cluster, the other guest
/// threads and the JIT workers off it, shader workers off it unless the fast cores are idle.</item>
/// <item><b>ADPF</b> (API 31+): a <c>PerformanceHintManager</c> session for the hot threads; each presented guest frame
/// reports its work (<see cref="FrameWorkMeter"/>) against the game's frame time (<see cref="FrameRateEstimator"/>:
/// 16.7 ms at 60 fps, 33.3 ms at 30), so the system raises the clocks before the game falls behind.
/// <c>VITASHARP_ANDROID_ADPF=gpu|wall</c> (API 35+, A/B) reports a WorkDuration with the frame's GPU time too
/// (<see cref="GpuBusy"/>), for systems that scale the GPU from hint sessions.</item>
/// <item><b>Game APIs</b> (<see cref="NativeGameState"/>): the game mode and thermal headroom in the log;
/// <c>VITASHARP_ANDROID_GAME_STATE=1</c> adds GameManager.setGameState (loading / gameplay) and the battery mode's
/// power-efficiency wish.</item>
/// <item><b>Display rate</b> (API 30+, always on): the game's frame rate goes to <c>Surface.setFrameRate</c> through
/// <see cref="FrameRateChanged"/>, so a 90/120 Hz panel switches to a rate the frames divide evenly.</item>
/// <item>hot threads get a 1 us timer slack (sleeps end on time without spinning: <see cref="HostTuning.AvoidBusyWaits"/>).</item>
/// </list>
/// <c>VITASHARP_ANDROID_AFFINITY=off</c> turns placement and ADPF off (the display rate and timer slack stay), to
/// compare. Everything here is best effort: a failing call is logged once and the game goes on.
/// </summary>
public sealed partial class GamePerformance : IDisposable
{
    readonly object _lock = new();
    readonly bool _enabled;
    readonly ThreadPlacementPlanner? _planner;
    readonly Context _context;
    readonly FrameRateEstimator _rate = new();
    readonly FrameWorkMeter _meter = new();
    readonly Dictionary<int, ulong> _applied = new();
    readonly Dictionary<int, long> _lastCpu = new();
    readonly HashSet<string> _failures = new();
    Timer? _timer;
    HintSession? _hints;
    long _lastSample = -1, _lastLongWaits;
    int[] _hot = [];
    bool _reported, _firstFrame;
    readonly AdpfReport _report;
    readonly NativeGameState _gameState;
    long _lastGpuBusy = -1, _lastFrameStart = -1;
    int _steps;

    /// <summary>The game's frame rate changed (on the presenting guest thread): the display refresh rate to ask for.</summary>
    public event Action<float>? FrameRateChanged;

    /// <summary>The current display refresh rate wish (60 until the game shows a lower rate).</summary>
    public float RefreshRate => _rate.RefreshRate;

    public GamePerformance(Context context)
    {
        _context = context;
        _enabled = !IsOff(System.Environment.GetEnvironmentVariable(EnvVars.AndroidAffinity));
        HostTuning.AvoidBusyWaits = true;
        _report = ParseReport(System.Environment.GetEnvironmentVariable(EnvVars.AndroidAdpf));
        _gameState = new NativeGameState(context);
        _gameState.SetLoading(true); // the boot, until the first presented frame
        NativeThread.Registered = OnRegistered;
        Display.Presented = OnFrame;
        if (!_enabled)
        {
            LogcatSink.Notice($"CPU placement and ADPF off ({EnvVars.AndroidAffinity}=off)");
            return;
        }
        var topology = CpuTopology.FromSysfs();
        _planner = new ThreadPlacementPlanner(topology);
        if (!_planner.CanPlace)
            LogcatSink.Notice($"CPU clusters: {topology.Describe()}: one kind of core, threads are not pinned");
    }

    static bool IsOff(string? v) => v?.Trim().ToLowerInvariant() is "off" or "0" or "false" or "no";

    /// <summary>Starts the placement steps (call once the emulator exists; its GPU worker may already run).</summary>
    public void Start()
    {
        _timer ??= new Timer(_ => Step(), null, 500, 1000);
    }

    // ------------------------------------------------------------------ threads

    /// <summary>On the registering thread: guest threads and the GPU worker sleep with a 1 us timer slack (Linux's
    /// default 50 us makes every short nanosleep late, and these threads no longer spin to make up for it).</summary>
    void OnRegistered(int tid, NativeThread.Role role)
    {
        if (role is not (NativeThread.Role.Guest or NativeThread.Role.Gpu)) return;
        try
        {
            if (prctl(PR_SET_TIMERSLACK, 1000, 0, 0, 0) != 0) Fail("timerslack", $"prctl(PR_SET_TIMERSLACK) failed: errno {Marshal.GetLastPInvokeError()}");
        }
        catch (Exception e) { Fail("timerslack", $"prctl: {e.Message}"); }
    }

    /// <summary>One placement step: sample the threads, plan, apply the masks, update the ADPF session's threads.</summary>
    void Step()
    {
        // every 5 s: the Android game mode (the user or the OEM game service may switch it while the game runs);
        // every 30 s: the thermal headroom, for the record (logs/last.log, next to the PERF lines)
        int step = _steps++;
        try
        {
            if (step % 5 == 0 && _gameState.PollMode())
                lock (_lock) _hints?.PreferPowerEfficiency(_gameState.PreferPowerEfficiency);
            if (step % 30 == 0) _gameState.LogThermal();
        }
        catch (Exception e) { Fail("gamestate", $"game mode / thermal step failed: {e.Message}"); }
        if (!_enabled) return;
        try
        {
            var planner = _planner!;
            long now = Stopwatch.GetTimestamp();
            double seconds = _lastSample < 0 ? 0 : (now - _lastSample) / (double)Stopwatch.Frequency;
            _lastSample = now;
            var samples = new List<ThreadSample>();
            foreach (var (tid, name) in NativeThread.Names)
            {
                long cpu = ThreadCpuNanos(tid);
                if (cpu < 0) { _lastCpu.Remove(tid); _applied.Remove(tid); continue; } // the thread is gone
                NativeThread.Roles.TryGetValue(tid, out var role);
                double load = seconds > 0 && _lastCpu.TryGetValue(tid, out long before) ? Math.Max(0, cpu - before) / 1e9 / seconds : 0;
                _lastCpu[tid] = cpu;
                samples.Add(new ThreadSample(tid, ThreadPlacementPlanner.Classify(role, name), name, load, tid));
            }
            var plan = planner.Plan(samples);
            foreach (var (tid, placement) in plan.Placements)
            {
                ulong mask = planner.MaskOf(placement);
                if (mask == 0 || _applied.TryGetValue(tid, out ulong had) && had == mask) continue;
                if (SetAffinity(tid, mask)) _applied[tid] = mask;
            }
            if (!plan.HotTids.SequenceEqual(_hot)) UpdateHotThreads(plan);
            if (!_reported && plan.MainTid != 0)
            {
                _reported = true;
                LogcatSink.Notice(Describe(planner, plan, samples));
            }
        }
        catch (Exception e) { Fail("step", $"CPU placement step failed: {e}"); }
    }

    string Describe(ThreadPlacementPlanner planner, PlacementPlan plan, List<ThreadSample> samples)
    {
        var topo = planner.Topology;
        string Where(int tid) => plan.Placements.TryGetValue(tid, out var p) ? CpuTopology.Ranges(planner.MaskOf(p)) : "?";
        string main = samples.FirstOrDefault(s => s.Tid == plan.MainTid).Name ?? "?";
        string gpu = plan.GpuTid != 0 ? $" + gpu to {Where(plan.GpuTid)}" : " (no GPU worker yet)";
        int extra = plan.HotTids.Length - 1 - (plan.GpuTid != 0 ? 1 : 0);
        return $"CPU clusters: {topo.Describe()}; pinned guest main '{main}' to {Where(plan.MainTid)}{gpu}"
            + (extra > 0 ? $" + {extra} more busy guest thread(s) to {CpuTopology.Ranges(topo.Big!.Mask)}" : "")
            + $"; other guest threads and JIT workers to {CpuTopology.Ranges(topo.NotBigMask)}; shader workers to "
            + $"{CpuTopology.Ranges(topo.NotBigMask)} (all cores while the fast ones are idle); "
            + (_hints != null ? $"ADPF session on (target {_rate.TargetNanos / 1e6:F2} ms)" : $"ADPF session off ({HintSession.Unavailable})");
    }

    void UpdateHotThreads(PlacementPlan plan)
    {
        lock (_lock)
        {
            _hot = plan.HotTids;
            _meter.Reset(_hot, _hot.Select(ThreadCpuNanos).ToArray(), NowNanos());
            if (_hot.Length == 0) return;
            if (_hints == null) { _hints = HintSession.Create(_context, _hot, _rate.TargetNanos); _hints?.PreferPowerEfficiency(_gameState.PreferPowerEfficiency); }
            else if (!_hints.SetThreads(_hot)) { _hints.Dispose(); _hints = HintSession.Create(_context, _hot, _rate.TargetNanos); _hints?.PreferPowerEfficiency(_gameState.PreferPowerEfficiency); }
        }
        Log.Info($"ADPF/placement: hot threads {string.Join(", ", plan.HotTids.Select(t => $"{t} '{NativeThread.Names.GetValueOrDefault(t)}'"))}");
    }

    // ------------------------------------------------------------------ frames

    /// <summary>Display.Presented, on the presenting guest thread: frame rate, ADPF work report.</summary>
    void OnFrame()
    {
        try
        {
            if (!_firstFrame) { _firstFrame = true; LogcatSink.EnterGameplay(); }
            _gameState.SetLoading(false);
            long longWaits = Volatile.Read(ref VBlank.LongWaits);
            bool longWait = longWaits != _lastLongWaits;
            _lastLongWaits = longWaits;
            if (_rate.OnFrame(Stopwatch.GetTimestamp(), Stopwatch.Frequency, longWait))
            {
                Log.Info($"Game frame rate: {60.0 / _rate.Vblanks:F0} fps ({_rate.TargetNanos / 1e6:F2} ms per frame)");
                lock (_lock) _hints?.UpdateTarget(_rate.TargetNanos);
                FrameRateChanged?.Invoke(_rate.RefreshRate);
            }
            if (!_enabled) return;
            lock (_lock)
            {
                if (_hints == null || _meter.Tids.Length == 0) return;
                var cpu = new long[_meter.Tids.Length];
                for (int i = 0; i < cpu.Length; i++) cpu[i] = ThreadCpuNanos(_meter.Tids[i]);
                long now = NowNanos();
                long work = _meter.OnFrame(cpu, now);
                long gpuBusy = GpuBusy.Nanos;
                long gpu = _lastGpuBusy < 0 ? -1 : gpuBusy - _lastGpuBusy;
                long wall = _lastFrameStart < 0 ? -1 : now - _lastFrameStart;
                long start = _lastFrameStart;
                _lastGpuBusy = gpuBusy;
                _lastFrameStart = now;
                if (work > 0) _hints.Report(_report, start, work, gpu, wall);
            }
        }
        catch (Exception e) { Fail("frame", $"frame report failed: {e.Message}"); }
    }

    static long NowNanos() => (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));

    void Fail(string key, string message)
    {
        lock (_failures) if (!_failures.Add(key)) return;
        Log.Warn(message);
    }

    /// <summary>A load (boot, load state) starts or the game shows frames again: GameManager.setGameState, so the
    /// system may boost the load and knows gameplay from loading screens.</summary>
    public void SetLoading(bool loading) => _gameState.SetLoading(loading);

    /// <summary>What a frame's ADPF report says (<see cref="EnvVars.AndroidAdpf"/>).</summary>
    public enum AdpfReport
    {
        /// <summary>The busiest hot thread's CPU time (the default).</summary>
        Cpu,
        /// <summary>API 35+: a WorkDuration with the CPU time and the GPU time of the frame (renderer timestamps);
        /// total = the larger of the two. Older systems: as <see cref="Cpu"/>.</summary>
        Gpu,
        /// <summary>API 35+: as <see cref="Gpu"/>, total = the frame's wall time (a frame that misses its target says so
        /// even when neither processor was busy the whole time: the serial game -> GPU chain).</summary>
        Wall,
    }

    static AdpfReport ParseReport(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "gpu" => AdpfReport.Gpu,
        "wall" => AdpfReport.Wall,
        _ => AdpfReport.Cpu,
    };

    public void Dispose()
    {
        if (Display.Presented == OnFrame) Display.Presented = null;
        if (NativeThread.Registered == OnRegistered) NativeThread.Registered = null;
        _timer?.Dispose();
        _timer = null;
        lock (_lock) { _hints?.Dispose(); _hints = null; }
    }

    // ------------------------------------------------------------------ libc

    bool SetAffinity(int tid, ulong mask)
    {
        try
        {
            Span<ulong> set = stackalloc ulong[16]; // cpu_set_t: 1024 bits
            set.Clear();
            set[0] = mask;
            unsafe
            {
                fixed (ulong* p = set)
                    if (sched_setaffinity(tid, (nuint)(set.Length * sizeof(ulong)), p) == 0) return true;
            }
            int errno = Marshal.GetLastPInvokeError();
            if (errno != ESRCH) Fail("affinity-" + errno, $"sched_setaffinity({tid}, {CpuTopology.Ranges(mask)}) failed: errno {errno}");
        }
        catch (Exception e) { Fail("affinity", $"sched_setaffinity: {e.Message}"); }
        return false;
    }

    /// <summary>CPU time of thread <paramref name="tid"/> of this process (its CPU clock: MAKE_THREAD_CPUCLOCK(tid,
    /// CPUCLOCK_SCHED)), nanoseconds; -1 when the thread does not exist.</summary>
    static unsafe long ThreadCpuNanos(int tid)
    {
        long* ts = stackalloc long[2];
        try
        {
            if (clock_gettime((~tid << 3) | 6, ts) != 0) return -1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return -1; }
        return ts[0] * 1_000_000_000 + ts[1];
    }

    const int PR_SET_TIMERSLACK = 29, ESRCH = 3;

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int sched_setaffinity(int pid, nuint size, ulong* mask);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int clock_gettime(int clock, long* ts);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, nuint a2, nuint a3, nuint a4, nuint a5);

    // ------------------------------------------------------------------ ADPF

    /// <summary>A PerformanceHintManager session (API 31+; the device may still have none).</summary>
    sealed class HintSession : IDisposable
    {
        public static string Unavailable = "not created yet";
        PerformanceHintManager.Session? _session;

        HintSession(PerformanceHintManager.Session s) { _session = s; }

        public static HintSession? Create(Context context, int[] tids, long targetNanos)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(31)) { Unavailable = "needs Android 12"; return null; }
            try
            {
                if (context.GetSystemService(Context.PerformanceHintService) is not PerformanceHintManager mgr) { Unavailable = "no PerformanceHintManager"; return null; }
                if (mgr.CreateHintSession(tids, targetNanos) is not { } s) { Unavailable = "the device does not support hint sessions"; return null; }
                Log.Info($"ADPF: hint session for threads {string.Join(",", tids)}, target {targetNanos / 1e6:F2} ms, preferred update rate {mgr.PreferredUpdateRateNanos / 1e6:F1} ms");
                return new HintSession(s);
            }
            catch (Exception e) { Unavailable = e.Message; Log.Warn($"ADPF: no hint session: {e.Message}"); return null; }
        }

        /// <summary>Changes the session's threads (API 34+); false = recreate the session.</summary>
        public bool SetThreads(int[] tids)
        {
            if (_session == null || !OperatingSystem.IsAndroidVersionAtLeast(34)) return false;
            try { _session.SetThreads(tids); return true; }
            catch (Exception e) { Log.Warn($"ADPF: setThreads failed: {e.Message}"); return false; }
        }

        bool _workDurationFailed;

        /// <summary>One frame: <paramref name="cpu"/> = the busiest hot thread's CPU time, <paramref name="gpu"/> = the
        /// GPU time retired since the previous frame (-1 unknown), <paramref name="wall"/> = the frame interval (-1 for
        /// the first), <paramref name="start"/> = when the interval began (CLOCK_MONOTONIC ns).</summary>
        public void Report(AdpfReport mode, long start, long cpu, long gpu, long wall)
        {
            if (_session == null || !OperatingSystem.IsAndroidVersionAtLeast(31)) return;
            if (mode != AdpfReport.Cpu && !_workDurationFailed && start > 0 && gpu >= 0 && wall > 0 && OperatingSystem.IsAndroidVersionAtLeast(35))
            {
                long total = mode == AdpfReport.Wall ? wall : Math.Clamp(Math.Max(cpu, gpu), 1, wall);
                try
                {
                    using var d = new WorkDuration
                    {
                        WorkPeriodStartTimestampNanos = start,
                        ActualTotalDurationNanos = total,
                        ActualCpuDurationNanos = Math.Min(cpu, total),
                        ActualGpuDurationNanos = Math.Min(gpu, total),
                    };
                    _session.ReportActualWorkDuration(d);
                    return;
                }
                catch (Exception e) { _workDurationFailed = true; Log.Warn($"ADPF: WorkDuration report failed ({e.Message}): CPU time only from now"); }
            }
            _session.ReportActualWorkDuration(cpu);
        }

        /// <summary>API 35+: the game mode's wish (battery: efficient cores and clocks first; else speed first).</summary>
        public void PreferPowerEfficiency(bool on)
        {
            if (_session == null || !OperatingSystem.IsAndroidVersionAtLeast(35)) return;
            try { _session.SetPreferPowerEfficiency(on); }
            catch (Exception e) { Log.Warn($"ADPF: setPreferPowerEfficiency failed: {e.Message}"); }
        }

        public void UpdateTarget(long nanos)
        {
            if (_session == null || !OperatingSystem.IsAndroidVersionAtLeast(31)) return;
            _session.UpdateTargetWorkDuration(nanos);
        }

        public void Dispose()
        {
            if (_session != null && OperatingSystem.IsAndroidVersionAtLeast(31)) { try { _session.Close(); } catch (Exception) { } }
            _session = null;
        }
    }
}
