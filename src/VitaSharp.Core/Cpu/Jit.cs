using System.Collections.Concurrent;
using VitaSharp.Core.Cpu.Backends;
using VitaSharp.Core.Util;

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
    public static IReadOnlyList<IBlockCompiler> All { get; } = [NativeX64Compiler.Instance, NativeArm64Compiler.Instance, IlJitCompiler.Instance];

    /// <summary>Active backends in the order blocks are offered to them.</summary>
    public static IBlockCompiler[] Chain
    {
        get => s_chain;
        private set
        {
            s_chain = value;
            InitialThreshold = value.Length > 0 ? value[0].HotInstructions : long.MaxValue;
        }
    }
    private static IBlockCompiler[] s_chain = [];

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
        set
        {
            IlJitCompiler.Instance.HotInstructions = value;
            Chain = Chain;
        }
    }

    /// <summary>A block is never compiled before this many executions (whatever its length).</summary>
    public const int MinExecutions = 64;

    private static IBlockCompiler[] DefaultChain() => All.Where(b => b.IsSupported).ToArray();

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
        // several backends may share a name (native = x64 or ARM64): take the first one this host supports
        for (int i = 0; i < All.Count && idx < 0; i++)
        {
            if (All[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase) && All[i].IsSupported)
            {
                idx = i;
            }
        }

        if (idx < 0)
        {
            for (int i = 0; i < All.Count && idx < 0; i++)
            {
                if (All[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                }
            }
        }

        if (idx < 0 || !All[idx].IsSupported)
        {
            return false;
        }

        Chain = All.Skip(idx).Where(b => b.IsSupported).ToArray();
        Mode = All[idx].Name;
        return true;
    }

    /// <summary>Applies VITASHARP_CPU when set (called once at startup; the command line overrides it).</summary>
    public static void SelectFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable(EnvVars.Cpu) is { Length: > 0 } env && !Select(env))
        {
            Console.Error.WriteLine($"VITASHARP_CPU={env}: {WhyUnavailable(env)}, using {Mode}");
        }
    }

    /// <summary>Why <see cref="Select"/> rejected <paramref name="name"/> (for the fallback log line).</summary>
    public static string WhyUnavailable(string? name)
    {
        foreach (IBlockCompiler blockCompiler in All)
        {
            if (blockCompiler.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return $"the {blockCompiler.Name} backend {blockCompiler.UnsupportedReason ?? "is not supported on this host"}";
            }
        }

        return $"unknown CPU backend '{name}' (native, il, interp or auto)";
    }

    /// <summary>Instructions the block must have executed before the next backend in the chain takes it.</summary>
    public static long NextThreshold(Block b)
    {
        IBlockCompiler[] chain = Chain;
        return b.Tier < chain.Length ? chain[b.Tier].HotInstructions : long.MaxValue;
    }

    /// <summary>Legacy totals (all backends).</summary>
    public static long CompiledBlocks => Chain.Sum(c => c.Stats.Blocks);

    private static readonly BlockingCollection<Block> s_queue = new(new ConcurrentQueue<Block>());
    private static int s_workersStarted;
    private static int s_inFlight; // queued or compiling

    /// <summary>
    /// Waits until every queued block has been compiled (or rejected); false on timeout. Compilation is asynchronous,
    /// so callers that need a deterministic state - tests comparing backends, diagnostics - wait here instead of
    /// sleeping for a guessed time.
    /// </summary>
    public static bool WaitIdle(TimeSpan timeout) => SpinWait.SpinUntil(() => Volatile.Read(ref s_inFlight) == 0, timeout);

    public static void Enqueue(Block b)
    {
        if (!Enabled || Interlocked.CompareExchange(ref b.JitState, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Increment(ref s_inFlight);
        EnsureWorkers();
        s_queue.Add(b);
    }

    /// <summary>Host priority of the compile workers: the guest threads' (never below it, since a guest thread
    /// interprets a hot block until a worker compiled it; see <see cref="Util.HostThreads"/>).</summary>
    internal static ThreadPriority WorkerPriority => Util.HostThreads.PriorityOf(Util.HostThreads.Role.JitWorker);

    private static void EnsureWorkers()
    {
        if (Interlocked.Exchange(ref s_workersStarted, 1) != 0)
        {
            return;
        }

        int n = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(() =>
            {
                Util.NativeThread.Register("JIT worker", Util.NativeThread.Role.JitWorker);
                Util.HostThreads.Apply(Util.HostThreads.Role.JitWorker);
                foreach (Block b in s_queue.GetConsumingEnumerable())
                {
                    try
                    {
                        Compile(b);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"[JIT] {b}: {e.Message}");
                        b.JitState = 3;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref s_inFlight);
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"JIT worker {i}",
                Priority = WorkerPriority
            };
            t.Start();
        }
    }

    /// <summary>Blocks translated ahead of execution (<see cref="CompileAheadOne"/>) and the time spent: kept apart from
    /// the backends' statistics, which then count only what was compiled while the game ran.</summary>
    public static long AheadBlocks, AheadTicks;

    /// <summary>The static exits (guest PC, Thumb) of the blocks the native backends compile on this thread, when set
    /// (<see cref="CodePrecompiler"/> follows them to find more code).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic] sink, set per precompile worker")]
    [ThreadStatic] public static List<(uint Pc, bool Thumb)>? StaticExits;

    /// <summary>Native code bytes translated ahead of execution (<see cref="CompileAheadOne"/>) in the arena now (the
    /// evicted ones, <see cref="EvictAhead"/>, no longer count).</summary>
    public static long AheadBytes;

    /// <summary>Of <see cref="AheadBytes"/>, the speculative code that has not run yet: blocks translated because the
    /// analysis guessed they might run (<see cref="CompileAheadOne"/> with speculative set), not because an earlier
    /// session ran them. Once such a block runs its bytes leave this count.</summary>
    public static long SpeculativeBytes;

    /// <summary>Blocks and bytes given back by <see cref="EvictAhead"/> (statistics).</summary>
    public static long EvictedBlocks, EvictedBytes;

    /// <summary>
    /// The most native code the ahead-of-time translation may keep in the arena (what ran and what waits to run), the
    /// rest being for the code the game compiles while it runs (a full arena would leave every later block
    /// interpreted): 128 MiB on a desktop (Assassin's Creed III: Liberation runs ~150k distinct blocks, ~48 MiB, in its
    /// first minutes), 64 MiB on Android, where memory is shorter.
    /// </summary>
    public static readonly long AheadBudget = DefaultAheadBudget(OperatingSystem.IsAndroid());

    /// <summary>The most speculative code waiting to run (<see cref="SpeculativeBytes"/>): beyond it the
    /// precompilation evicts its oldest speculative blocks that never ran (<see cref="CodePrecompiler"/>), so the arena
    /// holds what runs plus a bounded guess. 48 MiB on a desktop, 24 MiB on Android: the whole flow from a large game's
    /// entry points (Assassin's Creed III: Liberation 24 MiB, Killzone: Mercenary 18 MiB) fits, so the boot step keeps
    /// what the game runs first; with 32 MiB the frontier of Assassin's Creed III: Liberation evicted guesses the game
    /// reached later (24.8k blocks compiled while it ran instead of 9.5k). Tests may lower it.</summary>
    public static long SpeculativeBudget = DefaultSpeculativeBudget(OperatingSystem.IsAndroid());

    public static long DefaultAheadBudget(bool android) => android ? 64L << 20 : 128L << 20;

    public static long DefaultSpeculativeBudget(bool android) => android ? 24L << 20 : 48L << 20;

    /// <summary>Whether ahead-of-time translation may still use the native code arena (<see cref="AheadBudget"/>).</summary>
    public static bool ArenaHasRoomForAhead() => Volatile.Read(ref AheadBytes) < AheadBudget;

    /// <summary>Whether the native backends place the code compiled on this thread in ranges freed by
    /// <see cref="EvictAhead"/> first (the precompilation's threads: an x64 placement then encodes twice).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic] flag, set per precompile worker")]
    [ThreadStatic] public static bool ReuseFreedCode;

    /// <summary>Compiles a block that has not been queued yet with the first backend, on the calling thread; false when
    /// it is already queued or compiled, or no backend takes it. <paramref name="priority"/> is recorded in
    /// <see cref="Block.AheadPriority"/>; a <paramref name="speculative"/> block counts in <see cref="SpeculativeBytes"/>
    /// until it runs.</summary>
    public static bool CompileAheadOne(Block b, byte priority = 0, bool speculative = false)
    {
        if (!Enabled || b.Tier != 0 || Interlocked.CompareExchange(ref b.JitState, 1, 0) != 0)
        {
            return false;
        }

        b.AheadPriority = priority;
        b.Speculative = speculative;
        Interlocked.Increment(ref s_inFlight);
        try
        {
            Compile(b, ahead: true);
        }
        catch (Exception)
        {
            b.JitState = 3;
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref s_inFlight);
        }

        return b.JitState is 2 or AheadPending;
    }

    /// <summary>Native code bytes of a block's translation (0 when it has none).</summary>
    private static int CodeSize(Block b) => b.Code?.Tag is Backends.NativeCodeInfo info ? info.Size : 0;

    /// <summary>
    /// Drops a block translated ahead that the guest has not reached (<see cref="AheadPending"/>): its code goes back
    /// to the arena (<see cref="Native.NativeRuntime.Release"/>; it was never published, so nothing runs it) and the
    /// block leaves its cache, so the guest reaching it later decodes it again and interprets or compiles it as usual.
    /// False when it ran (or is being published) meanwhile: then it stays.
    /// </summary>
    public static bool EvictAhead(Block b)
    {
        if (Interlocked.CompareExchange(ref b.JitState, 3, AheadPending) != AheadPending)
        {
            return false;
        }

        b.Invalidated = true; // a core that looked it up just before interprets it once, then decodes it again
        b.Owner?.Remove(b);
        Backends.CompiledBlock? code = b.Code;
        int size = CodeSize(b);
        b.Code = null;
        if (b.Speculative)
        {
            Interlocked.Add(ref SpeculativeBytes, -size);
        }

        Interlocked.Add(ref AheadBytes, -size);
        Interlocked.Increment(ref EvictedBlocks);
        Interlocked.Add(ref EvictedBytes, size);
        if (code != null && code.NativeEntry != 0 && size > 0)
        {
            Native.NativeRuntime.Instance.Release(code.NativeEntry, size);
        }

        return true;
    }

    /// <summary><see cref="Block.JitState"/> of a block translated ahead that the guest has not reached yet.</summary>
    public const int AheadPending = 4;

    private static long s_uses;

    /// <summary>The guest reached a block translated ahead (<see cref="AheadPending"/>): its code is published (the
    /// dispatcher runs it, exits link to it) and the order of first use recorded (<see cref="Block.FirstUse"/>).</summary>
    public static void ActivateAhead(Block b)
    {
        if (b.Invalidated || Interlocked.CompareExchange(ref b.JitState, 2, AheadPending) != AheadPending)
        {
            return;
        }

        if (b.Speculative)
        {
            Interlocked.Add(ref SpeculativeBytes, -CodeSize(b)); // it ran: no longer a guess
        }

        b.FirstUse = Interlocked.Increment(ref s_uses);
        b.NativeEntry = b.Code!.NativeEntry;
        Native.NativeRuntime.Instance.Publish(b);
        b.Owner?.FirstUse?.Invoke(b);
    }

    /// <summary>Offers the block to the backend at its tier; on rejection it moves to the next tier.</summary>
    public static void Compile(Block b) => Compile(b, ahead: false);

    private static void Compile(Block b, bool ahead)
    {
        IBlockCompiler[] chain = Chain;
        if (b.Tier >= chain.Length)
        {
            b.JitState = 3;
            return;
        }
        IBlockCompiler backend = chain[b.Tier];
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        BackendStats.RejectReason = null;
#if VITASHARP_DEVELOPER
        Util.Developer.DevTrace.Begin("jit compile");
        var cb = backend.Compile(b);
        Util.Developer.DevTrace.End();
#else
        CompiledBlock? cb = backend.Compile(b);
#endif

        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        BackendStats backendStats = backend.Stats;
        if (ahead)
        {
            Interlocked.Add(ref AheadTicks, dt);
        }
        else
        {
            Interlocked.Add(ref backendStats.Ticks, dt);
            Util.FrameLedger.Add(Util.FrameLedger.Cat.JitCompile, dt);
        }

        if (cb == null)
        {
            Interlocked.Increment(ref backendStats.Rejected);
            backendStats.AddReject(BackendStats.RejectReason ?? "unspecified", b.Insts.Length);
            b.Tier++;
            b.HotAt = NextThreshold(b);
            b.JitState = b.Tier < chain.Length ? 0 : 3; // the next backend takes it once the block reaches its threshold
            return;
        }
        if (ahead && cb.NativeEntry != 0 && cb.Managed == null)
        {
            // published when the guest first reaches it (ActivateAhead): until then no exit links to it, and the
            // code cache learns which precompiled blocks the game really runs, and in which order
            b.Code = cb;
            b.Ahead = true;
            Interlocked.Increment(ref AheadBlocks);
            if (cb.Tag is Backends.NativeCodeInfo info)
            {
                Interlocked.Add(ref AheadBytes, info.Size);
                if (b.Speculative)
                {
                    Interlocked.Add(ref SpeculativeBytes, info.Size);
                }
            }

            b.JitState = AheadPending; // last: from here the guest may publish it, or the precompilation evict it
            return;
        }

        Install(b, cb);
        if (ahead)
        {
            b.Ahead = true;
            Interlocked.Increment(ref AheadBlocks);
            return;
        }

        if (b.FirstUse == 0)
        {
            // compiled because it got hot: it ran (a tier-up recompiles a block that already counted)
            b.FirstUse = Interlocked.Increment(ref s_uses);
            b.Owner?.FirstUse?.Invoke(b);
        }

        Interlocked.Increment(ref backendStats.Blocks);
        Interlocked.Add(ref backendStats.Instructions, b.Insts.Length);
    }

    private static void Install(Block b, CompiledBlock cb)
    {
        b.Code = cb;
        if (cb.Managed != null)
        {
            b.Compiled = cb.Managed;
            if (b.NativeEntry != 0)
            {
                Native.NativeRuntime.Retire(b); // tier-up from native code
            }
        }
        // compiled before it is published: once NativeEntry is set the block runs natively and may ask to tier up at
        // once (Jit.TierUp ignores a block still marked queued); publishing can take a while (the first block of a
        // process allocates its indirect-exit table)
        b.JitState = 2;
        if (cb.NativeEntry != 0)
        {
            b.NativeEntry = cb.NativeEntry;
            Native.NativeRuntime.Instance.Publish(b);
        }
    }

    /// <summary>
    /// A native block asked to be recompiled by the next backend (it is dominated by interpreter callouts the IL
    /// backend inlines): queue it for the IL backend now, whatever the threshold.
    /// </summary>
    public static void TierUp(Block b)
    {
        IBlockCompiler[] chain = Chain;
        int il = Array.IndexOf(chain, IlJitCompiler.Instance);
        if (il <= b.Tier || b.NativeEntry == 0 || b.JitState == 1)
        {
            return;
        }

        b.Tier = il;
        b.HotAt = 0;
        b.JitState = 0;
        Enqueue(b);
    }

    /// <summary>One line per active backend for logs.</summary>
    public static string Describe() =>
        Chain.Length == 0 ? "CPU backend: interpreter only"
            : $"CPU backend: {Mode} (chain {string.Join(" > ", Chain.Select(c => c.Name))} > interp)" +
              string.Concat(Chain.Select(c => $"; {c.Name}: {c.Stats}")) +
              (AheadBlocks > 0 ? $"; precompiled before start: {AheadBlocks} blocks ({AheadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms of compile time)" : "");
}
