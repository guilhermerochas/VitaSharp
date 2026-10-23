using System.Collections.Concurrent;
using VitaSharp.Core.Cpu.Backends;

namespace VitaSharp.Core.Cpu;

public unsafe delegate void JitBlockFn(CpuState* s);

/// <summary>
/// The CPU backend chain. Blocks start interpreted; once a block has executed enough instructions for the next backend
/// in <see cref="Chain"/> it is queued for compilation on background workers. A backend that cannot compile the block
/// returns null and the block moves on to the following backend (or stays interpreted).
/// Selection: --cpu=native|il|interp, VITASHARP_CPU, the launcher setting; default = best supported on the host.
/// </summary>
public static unsafe class Jit
{
    /// <summary>Every backend known to this build, best first (the interpreter is implicit and always last).</summary>
    public static IReadOnlyList<IBlockCompiler> All { get; } = [NativeX64Compiler.Instance, IlJitCompiler.Instance];

    /// <summary>Active backends in the order blocks are offered to them.</summary>
    public static IBlockCompiler[] Chain
    {
        get => _chain;
        private set { _chain = value; InitialThreshold = value.Length > 0 ? value[0].HotInstructions : long.MaxValue; }
    }
    static IBlockCompiler[] _chain = [];

    /// <summary>Threshold of the first backend: new blocks start with it (<see cref="Block.HotAt"/>).</summary>
    public static long InitialThreshold { get; private set; } = long.MaxValue;

    /// <summary>Name of the selected mode: a backend name, or "interp".</summary>
    public static string Mode { get; private set; } = "interp";

    static Jit() => Select(null);

    /// <summary>False runs everything in the interpreter (--no-jit, lockstep debugging).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Threshold of the IL backend (--jit-threshold), kept for compatibility.</summary>
    public static long HotInstructions
    {
        get => IlJitCompiler.Instance.HotInstructions;
        set { IlJitCompiler.Instance.HotInstructions = value; Chain = Chain; }
    }

    /// <summary>A block is never compiled before this many executions (whatever its length).</summary>
    public const int MinExecutions = 64;

    static IBlockCompiler[] DefaultChain() => All.Where(b => b.IsSupported).ToArray();

    /// <summary>
    /// Selects the backend: "native", "il" or "interp" (null/""/"auto" = best supported). A backend falls back to the
    /// ones after it in <see cref="All"/> for blocks it cannot compile. Returns false for an unknown or unsupported name.
    /// </summary>
    public static bool Select(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            Chain = DefaultChain();
            Mode = Chain.Length > 0 ? Chain[0].Name : "interp";
            return true;
        }
        if (name.Equals("interp", StringComparison.OrdinalIgnoreCase) || name.Equals("interpreter", StringComparison.OrdinalIgnoreCase))
        {
            Chain = [];
            Mode = "interp";
            return true;
        }
        int idx = -1;
        for (int i = 0; i < All.Count; i++) if (All[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) idx = i;
        if (idx < 0 || !All[idx].IsSupported) return false;
        Chain = All.Skip(idx).Where(b => b.IsSupported).ToArray();
        Mode = All[idx].Name;
        return true;
    }

    /// <summary>Applies VITASHARP_CPU when set (called once at startup; the command line overrides it).</summary>
    public static void SelectFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("VITASHARP_CPU") is { Length: > 0 } env && !Select(env))
            Console.Error.WriteLine($"VITASHARP_CPU={env}: {WhyUnavailable(env)}, using {Mode}");
    }

    /// <summary>Why <see cref="Select"/> rejected <paramref name="name"/> (for the fallback log line).</summary>
    public static string WhyUnavailable(string? name)
    {
        foreach (var b in All)
            if (b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return $"the {b.Name} backend {b.UnsupportedReason ?? "is not supported on this host"}";
        return $"unknown CPU backend '{name}' (native, il, interp or auto)";
    }

    /// <summary>Instructions the block must have executed before the next backend in the chain takes it.</summary>
    public static long NextThreshold(Block b)
    {
        var chain = Chain;
        return b.Tier < chain.Length ? chain[b.Tier].HotInstructions : long.MaxValue;
    }

    /// <summary>Legacy totals (all backends).</summary>
    public static long CompiledBlocks => Chain.Sum(c => c.Stats.Blocks);

    static readonly BlockingCollection<Block> Queue = new(new ConcurrentQueue<Block>());
    static int _workersStarted;
    static int _inFlight; // queued or compiling

    /// <summary>
    /// Waits until every queued block has been compiled (or rejected); false on timeout. Compilation is asynchronous,
    /// so callers that need a deterministic state - tests comparing backends, diagnostics - wait here instead of
    /// sleeping for a guessed time.
    /// </summary>
    public static bool WaitIdle(TimeSpan timeout) => SpinWait.SpinUntil(() => Volatile.Read(ref _inFlight) == 0, timeout);

    public static void Enqueue(Block b)
    {
        if (!Enabled || Interlocked.CompareExchange(ref b.JitState, 1, 0) != 0) return;
        Interlocked.Increment(ref _inFlight);
        EnsureWorkers();
        Queue.Add(b);
    }

    static void EnsureWorkers()
    {
        if (Interlocked.Exchange(ref _workersStarted, 1) != 0) return;
        int n = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(() =>
            {
                Util.NativeThread.Register("JIT worker");
                foreach (var b in Queue.GetConsumingEnumerable())
                {
                    try { Compile(b); }
                    catch (Exception e) { Console.Error.WriteLine($"[JIT] {b}: {e.Message}"); b.JitState = 3; }
                    finally { Interlocked.Decrement(ref _inFlight); }
                }
            })
            { IsBackground = true, Name = $"JIT worker {i}", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }
    }

    /// <summary>Offers the block to the backend at its tier; on rejection it moves to the next tier.</summary>
    public static void Compile(Block b)
    {
        var chain = Chain;
        if (b.Tier >= chain.Length) { b.JitState = 3; return; }
        var backend = chain[b.Tier];
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var cb = backend.Compile(b);
        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        var st = backend.Stats;
        Interlocked.Add(ref st.Ticks, dt);
        Util.FrameLedger.Add(Util.FrameLedger.Cat.JitCompile, dt);
        if (cb == null)
        {
            Interlocked.Increment(ref st.Rejected);
            b.Tier++;
            b.HotAt = NextThreshold(b);
            b.JitState = b.Tier < chain.Length ? 0 : 3; // the next backend takes it once the block reaches its threshold
            return;
        }
        Install(b, cb);
        Interlocked.Increment(ref st.Blocks);
        Interlocked.Add(ref st.Instructions, b.Insts.Length);
    }

    static void Install(Block b, CompiledBlock cb)
    {
        b.Code = cb;
        if (cb.Managed != null)
        {
            b.Compiled = cb.Managed;
            if (b.NativeEntry != 0) Native.NativeRuntime.Retire(b); // tier-up from native code
        }
        if (cb.NativeEntry != 0) { b.NativeEntry = cb.NativeEntry; Native.NativeRuntime.Instance.Publish(b); }
        b.JitState = 2;
    }

    /// <summary>
    /// A native block asked to be recompiled by the next backend (it is dominated by interpreter callouts the IL
    /// backend inlines): queue it for the IL backend now, whatever the threshold.
    /// </summary>
    public static void TierUp(Block b)
    {
        var chain = Chain;
        int il = Array.IndexOf(chain, IlJitCompiler.Instance);
        if (il <= b.Tier || b.NativeEntry == 0 || b.JitState == 1) return;
        b.Tier = il;
        b.HotAt = 0;
        b.JitState = 0;
        Enqueue(b);
    }

    /// <summary>One line per active backend for logs.</summary>
    public static string Describe() =>
        Chain.Length == 0 ? "CPU backend: interpreter only"
            : $"CPU backend: {Mode} (chain {string.Join(" > ", Chain.Select(c => c.Name))} > interp)" +
              string.Concat(Chain.Select(c => $"; {c.Name}: {c.Stats}"));
}
