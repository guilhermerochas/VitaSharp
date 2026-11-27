using System.Diagnostics;
using System.Globalization;
using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Cpu;

/// <summary>Where a guest thread's dispatcher turn spent its host time (<see cref="PhaseProfile"/>).</summary>
public enum CpuPhase
{
    /// <summary>Block lookup, dispatch-table republish, exit linking and the loop's own bookkeeping: switching blocks.</summary>
    Dispatch,
    /// <summary>A block run by the interpreter (cold, rejected by every backend, verification aside).</summary>
    Interpret,
    /// <summary>A block run by the interpreter in VFP short-vector mode (every vector element through the managed handler).</summary>
    InterpretVector,
    /// <summary>A block's short-vector expansion run as compiled code.</summary>
    VectorCode,
    /// <summary>Native code (x64 / ARM64), chained blocks and interpreter callouts included, HLE calls from it excluded.</summary>
    Native,
    /// <summary>IL-compiled blocks.</summary>
    Il,
    /// <summary>Differential verification and debugging hooks.</summary>
    Verify,
    /// <summary>HLE calls (in-place SVCs and the ones made from native code); waits included, so not budgeted.</summary>
    Hle,
    /// <summary>Sleeping off CPU pacing (waiting, not cost; not budgeted).</summary>
    Throttle,
    Count,
}

/// <summary>
/// The per-thread phase profile of the CPU dispatcher (<see cref="CpuCore"/>): for every phase the guest cycles it ran
/// (exact, every turn), the dispatcher turns that went through it (exact) and the host time it took. The host time is
/// sampled: one turn in <see cref="SampleEvery"/> is timed and scaled back, so the profile is always on (a clock read
/// per turn was a few percent of a phone core); <c>VITASHARP_PHASE_PROF=1</c> or the debug mode time every turn (exact,
/// and every slow turn is seen), <c>VITASHARP_PHASE_PROF=0</c> turns it off. Rare events are always timed: blocks
/// decoded on the guest thread and blocks compiled on it (a compile on the guest thread is host time its guest clock
/// counts as its own).
/// <para>
/// Every <see cref="WindowCycles"/> guest cycles the thread checks its budget: an alert ("[phase-alert]" in the log)
/// names the phase when the thread spent more than <see cref="BudgetNsPerCycle"/> of host time per guest cycle in the
/// window, when hot blocks were interpreted, when a compile ran on the guest thread for more than
/// <see cref="CompileBudgetTicks"/>, or when a single timed turn took more than <see cref="TurnBudgetTicks"/>. These are
/// the slow paths that hid behind each other in Dead Space's audio (a mixer round over the consumer's 11 ms): VFP
/// short-vector blocks only in the interpreter, a 13 ms compile on the guest thread, the .NET JIT on first use.
/// </para>
/// Counters are written by the owning thread only and read racily by the reports (statistics).
/// </summary>
public sealed class PhaseProfile
{
    private const int N = (int)CpuPhase.Count;

    /// <summary>Layout of a <see cref="Snapshot"/>: cycles, ticks, turns and longest turn per phase, then the events.</summary>
    private const int CyclesAt = 0, TicksAt = N, TurnsAt = 2 * N, MaxAt = 3 * N, EventsAt = 4 * N;
    private const int Builds = EventsAt, BuildTicks = EventsAt + 1, Compiles = EventsAt + 2, CompileTicks = EventsAt + 3,
        CompileMax = EventsAt + 4, HotInterpreted = EventsAt + 5, Alerts = EventsAt + 6, TimedTurns = EventsAt + 7,
        Callouts = EventsAt + 8;
    /// <summary>Length of a <see cref="Snapshot"/>.</summary>
    public const int SnapshotLength = EventsAt + 9;

    /// <summary>Short names of the phases in the reports.</summary>
    private static readonly string[] s_names = ["dispatch", "interp", "interp-vector", "vector-code", "native", "il", "verify", "hle", "throttle"];

    /// <summary><c>VITASHARP_PHASE_PROF</c>: "1" on for the whole run with every turn timed, "0" never on.</summary>
    private static readonly string? s_env = Environment.GetEnvironmentVariable(EnvVars.PhaseProf);

    /// <summary>
    /// The profile counts: while the detailed log is on (an F8 capture recording or keeping its pre-roll,
    /// <c>--debug-mode</c>: <see cref="SetDetailed"/>) or with <c>VITASHARP_PHASE_PROF=1</c>. Off by default: measured
    /// on x64, the counting and the sampled clock reads cost 1.3% of a native-code loop and 26% of an interpreted one
    /// (blocks of two instructions), over the 1% an always-on diagnostic may take.
    /// </summary>
    public static bool Enabled { get; set; } = s_env == "1";

    /// <summary>One dispatcher turn in this many is timed (1: every turn, <c>VITASHARP_PHASE_PROF=1</c>). An F8 capture
    /// keeps the sampling: timing every turn doubles the host time of interpreted code, which the guest clock counts,
    /// so the recording would no longer show the timing it was made to diagnose.</summary>
    public static int SampleEvery { get; set; } = s_env == "1" ? 1 : DefaultSampleEvery;

    /// <summary>The sampling period when not every turn is timed (prime: a loop of a power-of-two period does not alias).</summary>
    public const int DefaultSampleEvery = 31;

    /// <summary>The detailed log turned on or off (an F8 capture recording or keeping its pre-roll, --debug-mode): the
    /// profile counts while it is on, unless <c>VITASHARP_PHASE_PROF</c> decides for the whole run.</summary>
    public static void SetDetailed(bool on)
    {
        if (s_env is "0" or "1")
        {
            return;
        }

        Enabled = on;
    }

    /// <summary>Host time per guest cycle above which a window raises an alert: 70 ns, about 200 host cycles at the
    /// 3 GHz of a desktop core or a phone's big core (<c>VITASHARP_PHASE_BUDGET_NS</c>). Native code runs at 1-5 ns
    /// per guest cycle and the interpreter at 10-40; a VFP vector instruction interpreted takes ~600.</summary>
    public static double BudgetNsPerCycle { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable(EnvVars.PhaseBudgetNs), NumberStyles.Float, CultureInfo.InvariantCulture, out double ns) && ns > 0 ? ns : 70;

    /// <summary>A compile on the guest thread longer than this raises an alert (1 ms).</summary>
    public static long CompileBudgetTicks { get; set; } = Stopwatch.Frequency / 1000;

    /// <summary>A single timed turn of a budgeted phase longer than this raises an alert (2 ms).</summary>
    public static long TurnBudgetTicks { get; set; } = Stopwatch.Frequency / 500;

    /// <summary>Guest cycles of a budget window (~0.8 ms of guest time at 333 MHz).</summary>
    public const long WindowCycles = 256 * 1024;

    /// <summary>Timed turns a window needs before its average is trusted (sampled mode).</summary>
    public const int MinWindowSamples = 8;

    /// <summary>Executions after which an interpreted block counts as hot (<see cref="HotInterpretedCycles"/>): well
    /// past every backend's threshold, so only blocks the JIT never took count.</summary>
    public const int HotExecutions = 1024;

    /// <summary>Alerts written to the log per thread and kind before they are only counted (then one in 64).</summary>
    private const int AlertsLogged = 3;

    private readonly long[] _c = new long[SnapshotLength];
    private readonly long[] _window = new long[SnapshotLength];
    private readonly int[] _alertsByKind = new int[4];
    private long _windowStart;

    /// <summary>The thread's name in alerts and reports.</summary>
    public string ThreadName { get; set; } = "";

    /// <summary>Alerts raised so far (logged or not).</summary>
    public long AlertCount => Volatile.Read(ref _c[Alerts]);

    /// <summary>Guest cycles of a phase so far (exact).</summary>
    public long CyclesOf(CpuPhase phase) => Volatile.Read(ref _c[CyclesAt + (int)phase]);

    /// <summary>Dispatcher turns through a phase so far (exact).</summary>
    public long TurnsOf(CpuPhase phase) => Volatile.Read(ref _c[TurnsAt + (int)phase]);

    /// <summary>Host ticks of a phase so far (scaled back from the timed turns).</summary>
    public long TicksOf(CpuPhase phase) => Volatile.Read(ref _c[TicksAt + (int)phase]);

    /// <summary>Blocks compiled on this guest thread, and their host ticks.</summary>
    public long GuestThreadCompiles => Volatile.Read(ref _c[Compiles]);

    /// <summary>Host ticks of the compiles on this guest thread.</summary>
    public long GuestThreadCompileTicks => Volatile.Read(ref _c[CompileTicks]);

    /// <summary>Blocks decoded on this guest thread.</summary>
    public long BlocksBuilt => Volatile.Read(ref _c[Builds]);

    /// <summary>Guest cycles interpreted in blocks that ran at least <see cref="HotExecutions"/> times.</summary>
    public long HotInterpretedCycles => Volatile.Read(ref _c[HotInterpreted]);

    /// <summary>Interpreter callouts run from this thread's native code (instructions the backend does not translate).</summary>
    public long InterpreterCallouts => Volatile.Read(ref _c[Callouts]);

    /// <summary>Native code called the interpreter for one instruction (<see cref="Native.NativeRuntime"/>).</summary>
    internal void CalloutRan() => _c[Callouts]++;

    /// <summary>Phase bookkeeping of one dispatcher turn (exact counts).</summary>
    internal void Count(CpuPhase phase, long cycles)
    {
        _c[CyclesAt + (int)phase] += cycles;
        _c[TurnsAt + (int)phase]++;
    }

    /// <summary>A timed turn: <paramref name="execTicks"/> in <paramref name="phase"/>, <paramref name="dispatchTicks"/>
    /// switching blocks, <paramref name="hleTicks"/> in HLE calls made from native code; scaled by <paramref name="scale"/>.</summary>
    internal void AddTimed(CpuPhase phase, long execTicks, long dispatchTicks, long hleTicks, int scale)
    {
        execTicks = Math.Max(0, execTicks - hleTicks);
        _c[TicksAt + (int)phase] += execTicks * scale;
        _c[TicksAt + (int)CpuPhase.Dispatch] += dispatchTicks * scale;
        _c[TicksAt + (int)CpuPhase.Hle] += hleTicks * scale;
        _c[TimedTurns]++;
        if (execTicks > _c[MaxAt + (int)phase])
        {
            _c[MaxAt + (int)phase] = execTicks;
            if (execTicks > TurnBudgetTicks && phase != CpuPhase.Hle && phase != CpuPhase.Throttle)
            {
                Alert(1, $"one {s_names[(int)phase]} turn took {Ms(execTicks):F2} ms of host time (budget {Ms(TurnBudgetTicks):F1} ms): a .NET JIT compile or a GC pause on first use, a block compiled or decoded inline, or a long run without a pacing check");
            }
        }
    }

    /// <summary>A timed turn of a phase that is only time (HLE call, pacing sleep), scaled by <paramref name="scale"/>.</summary>
    internal void AddTime(CpuPhase phase, long ticks, int scale)
    {
        _c[TicksAt + (int)phase] += ticks * scale;
        if (ticks > _c[MaxAt + (int)phase])
        {
            _c[MaxAt + (int)phase] = ticks;
        }
    }

    /// <summary>Guest cycles interpreted in a block that ran <paramref name="executions"/> times.</summary>
    internal void Interpreted(int executions, long cycles)
    {
        if (executions >= HotExecutions)
        {
            _c[HotInterpreted] += cycles;
        }
    }

    /// <summary>A block decoded on this guest thread (always timed: decoding is rare and costs microseconds).</summary>
    internal void BlockBuilt(long ticks)
    {
        _c[Builds]++;
        _c[BuildTicks] += ticks;
    }

    /// <summary>A block compiled on this guest thread (always timed).</summary>
    internal void CompiledOnGuestThread(long ticks, uint pc)
    {
        _c[Compiles]++;
        _c[CompileTicks] += ticks;
        if (ticks > _c[CompileMax])
        {
            _c[CompileMax] = ticks;
        }

        if (ticks > CompileBudgetTicks)
        {
            Alert(2, $"block 0x{pc:X8} was compiled on the guest thread in {Ms(ticks):F2} ms (budget {Ms(CompileBudgetTicks):F1} ms): its guest clock counts the compile as the thread's own time");
        }
    }

    /// <summary>Checks the budget once <see cref="WindowCycles"/> guest cycles ran since the last check (called at
    /// the dispatcher's pacing checks).</summary>
    internal void CheckWindow()
    {
        long cycles = 0;
        for (int i = 0; i < N; i++)
        {
            cycles += _c[CyclesAt + i];
        }

        if (cycles - _windowStart < WindowCycles)
        {
            return;
        }

        long windowCycles = cycles - _windowStart;
        long samples = _c[TimedTurns] - _window[TimedTurns];
        long hot = _c[HotInterpreted] - _window[HotInterpreted];
        long budgeted = 0;
        long worst = 0;
        int worstPhase = 0;
        for (int i = 0; i < N; i++)
        {
            if (i == (int)CpuPhase.Hle || i == (int)CpuPhase.Throttle)
            {
                continue;
            }

            long ticks = _c[TicksAt + i] - _window[TicksAt + i];
            budgeted += ticks;
            if (ticks > worst)
            {
                worst = ticks;
                worstPhase = i;
            }
        }

        double nsPerCycle = budgeted * 1e9 / Stopwatch.Frequency / windowCycles;
        if (samples >= MinWindowSamples && nsPerCycle > BudgetNsPerCycle)
        {
            long phaseCycles = _c[CyclesAt + worstPhase] - _window[CyclesAt + worstPhase];
            string phaseRate = phaseCycles > 0 ? $"{worst * 1e9 / Stopwatch.Frequency / phaseCycles:F0} ns/cycle over {phaseCycles / 1e3:F0} K cycles" : "no guest cycles of its own";
            Alert(0, $"{nsPerCycle:F0} ns of host time per guest cycle over {windowCycles / 1e3:F0} K cycles (budget {BudgetNsPerCycle:F0} ns, ~200 host cycles): " +
                     $"{s_names[worstPhase]} took {100.0 * worst / Math.Max(1, budgeted):F0}% ({phaseRate})");
        }

        if (hot * 4 >= windowCycles && Jit.Enabled && Jit.Chain.Length > 0) // without a backend every block is interpreted
        {
            Alert(3, $"{100.0 * hot / windowCycles:F0}% of {windowCycles / 1e3:F0} K guest cycles ran interpreted in hot blocks (run {HotExecutions}+ times without compiled code: rejected by every backend, or a mode the code was not compiled for)");
        }

        _windowStart = cycles;
        Array.Copy(_c, TicksAt, _window, TicksAt, N);
        Array.Copy(_c, CyclesAt, _window, CyclesAt, N);
        _window[TimedTurns] = _c[TimedTurns];
        _window[HotInterpreted] = _c[HotInterpreted];
    }

    private void Alert(int kind, string what)
    {
        _c[Alerts]++;
        int n = ++_alertsByKind[kind];
        if (n <= AlertsLogged || n % 64 == 0)
        {
            string repeat = n > AlertsLogged ? $" (alert {n} of this kind on this thread)" : "";
            Log.Warn($"[phase-alert] thread '{ThreadName}': {what}{repeat}");
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>The counters now (racy read: statistics).</summary>
    public long[] Snapshot()
    {
        long[] copy = new long[SnapshotLength];
        for (int i = 0; i < SnapshotLength; i++)
        {
            copy[i] = Volatile.Read(ref _c[i]);
        }

        return copy;
    }

    private long[] _atReport = new long[SnapshotLength];

    /// <summary>The counters since the previous call (the periodic PERF phases lines; the report thread only).</summary>
    public long[] TakeWindow()
    {
        long[] now = Snapshot();
        long[] window = Delta(now, _atReport);
        _atReport = now;
        return window;
    }

    /// <summary>Guest cycles of a <see cref="Snapshot"/> or <see cref="Delta"/>, all phases.</summary>
    public static long TotalCycles(long[] counters)
    {
        long cycles = 0;
        for (int i = 0; i < N; i++)
        {
            cycles += counters[CyclesAt + i];
        }

        return cycles;
    }

    /// <summary><paramref name="now"/> minus <paramref name="before"/>; the longest turn and compile stay <paramref name="now"/>'s.</summary>
    public static long[] Delta(long[] now, long[] before)
    {
        long[] d = new long[SnapshotLength];
        for (int i = 0; i < SnapshotLength; i++)
        {
            bool isMax = (i >= MaxAt && i < MaxAt + N) || i == CompileMax;
            d[i] = isMax ? now[i] : now[i] - before[i];
        }

        return d;
    }

    /// <summary>
    /// One report line: guest cycles, host time and ns per guest cycle of every phase that ran, HLE and pacing time,
    /// the events and the alerts. <paramref name="frequency"/>: host ticks per second of the counters (the capture's,
    /// when the line is formatted on another machine).
    /// </summary>
    public static string Format(string thread, long[] d, long frequency)
    {
        double msPerTick = 1000.0 / frequency;
        long cycles = 0;
        long budgeted = 0;
        for (int i = 0; i < N; i++)
        {
            cycles += d[CyclesAt + i];
            if (i != (int)CpuPhase.Hle && i != (int)CpuPhase.Throttle)
            {
                budgeted += d[TicksAt + i];
            }
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"PERF phases {thread}: {cycles / 1e6:F2} M guest cycles in {budgeted * msPerTick:F1} ms of host time ({budgeted * msPerTick * 1e6 / Math.Max(1, cycles):F1} ns/cycle)");
        string sep = "; ";
        for (int i = 0; i < N; i++)
        {
            if (i == (int)CpuPhase.Hle || i == (int)CpuPhase.Throttle)
            {
                continue;
            }

            long c = d[CyclesAt + i];
            long t = d[TicksAt + i];
            if (c == 0 && t == 0 && d[TurnsAt + i] == 0)
            {
                continue;
            }

            string rate = c > 0 ? $"{t * msPerTick * 1e6 / c:F1} ns/cycle" : "no cycles";
            sb.Append(CultureInfo.InvariantCulture, $"{sep}{s_names[i]} {c / 1e6:F2} M cycles {100.0 * t / Math.Max(1, budgeted):F0}% {rate} (longest {d[MaxAt + i] * msPerTick:F2} ms)");
            sep = ", ";
        }

        sb.Append(CultureInfo.InvariantCulture, $"; hle {d[TicksAt + (int)CpuPhase.Hle] * msPerTick:F1} ms, pacing sleeps {d[TicksAt + (int)CpuPhase.Throttle] * msPerTick:F1} ms");
        sb.Append(CultureInfo.InvariantCulture, $"; blocks decoded on the thread {d[Builds]} ({d[BuildTicks] * msPerTick:F1} ms), compiled on the thread {d[Compiles]} ({d[CompileTicks] * msPerTick:F1} ms, longest {d[CompileMax] * msPerTick:F2} ms)");
        sb.Append(CultureInfo.InvariantCulture, $"; interpreter callouts from native code {d[Callouts]}; hot blocks interpreted {d[HotInterpreted] / 1e3:F0} K cycles; alerts {d[Alerts]}; timed turns {d[TimedTurns]}");
        return sb.ToString();
    }

    /// <summary>The context.txt line of one thread (F8 capture): the host tick frequency, the thread and the counters.</summary>
    public string ContextLine() =>
        $"cpu-phases: {Stopwatch.Frequency}|{ThreadName.Replace('|', '/')}|{string.Join(",", Snapshot())}";

    /// <summary>Parses a <see cref="ContextLine"/> (without the "cpu-phases: " prefix); null when malformed.</summary>
    public static (long Frequency, string Thread, long[] Counters)? ParseContextLine(string value)
    {
        string[] parts = value.Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out long frequency))
        {
            return null;
        }

        string[] numbers = parts[2].Split(',');
        if (numbers.Length != SnapshotLength)
        {
            return null;
        }

        long[] counters = new long[SnapshotLength];
        for (int i = 0; i < SnapshotLength; i++)
        {
            if (!long.TryParse(numbers[i], CultureInfo.InvariantCulture, out counters[i]))
            {
                return null;
            }
        }

        return (frequency, parts[1], counters);
    }
}
