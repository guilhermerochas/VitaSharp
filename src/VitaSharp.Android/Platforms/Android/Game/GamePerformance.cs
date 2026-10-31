using System.Diagnostics;
using System.Runtime.InteropServices;
using Android.Content;
using Android.OS;
using VitaSharp.Android.Performance;
using VitaSharp.Android.Services;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Util;

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
/// 16.7 ms at 60 fps, 33.3 ms at 30), so the system raises the clocks before the game falls behind.</item>
/// <item><b>Display rate</b> (API 30+, always on): the game's frame rate goes to <c>Surface.setFrameRate</c> through
/// <see cref="FrameRateChanged"/>, so a 90/120 Hz panel switches to a rate the frames divide evenly.</item>
/// <item>hot threads get a 1 µs timer slack (sleeps end on time without spinning: <see cref="HostTuning.AvoidBusyWaits"/>).</item>
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

    /// <summary>The game's frame rate changed (on the presenting guest thread): the display refresh rate to ask for.</summary>
    public event Action<float>? FrameRateChanged;

    /// <summary>The current display refresh rate wish (60 until the game shows a lower rate).</summary>
    public float RefreshRate => _rate.RefreshRate;

    public GamePerformance(Context context)
    {
        _context = context;
        _enabled = !IsOff(System.Environment.GetEnvironmentVariable(EnvVars.AndroidAffinity));
        HostTuning.AvoidBusyWaits = true;
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
        if (!_enabled) return;
        _timer ??= new Timer(_ => Step(), null, 500, 1000);
    }

    // ------------------------------------------------------------------ threads

    /// <summary>On the registering thread: guest threads and the GPU worker sleep with a 1 µs timer slack (Linux's
    /// default 50 µs makes every short nanosleep late, and these threads no longer spin to make up for it).</summary>
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
            if (_hints == null) _hints = HintSession.Create(_context, _hot, _rate.TargetNanos);
            else if (!_hints.SetThreads(_hot)) { _hints.Dispose(); _hints = HintSession.Create(_context, _hot, _rate.TargetNanos); }
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
                long work = _meter.OnFrame(cpu, NowNanos());
                if (work > 0) _hints.Report(work);
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

        public void Report(long nanos)
        {
            if (_session == null || !OperatingSystem.IsAndroidVersionAtLeast(31)) return;
            _session.ReportActualWorkDuration(nanos);
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
