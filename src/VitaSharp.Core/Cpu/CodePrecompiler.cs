using System.Collections.Concurrent;
using VitaSharp.Core.Cpu.Native;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// Translates guest code before it runs, on several threads: the given blocks and, when a code range is given, the code
/// reachable from them by static control flow (each block's static exits, and the return point after a call), which is
/// how the precompilation finds code no earlier session ran (RPCS3's PPU analyser walks functions from the module's
/// entry points the same way). Whatever the analysis misses, or reaches only through computed branches (jump tables,
/// function pointers), the JIT compiles when the game gets there.
/// <para>
/// Work comes in three priorities: <see cref="Priority.Cached"/> (blocks earlier sessions ran, in the order they first
/// ran), <see cref="Priority.Frontier"/> (code near what the game runs: the whole functions executed or cached blocks
/// are in, including the cases of their switches, the functions they reference through pointers, and the functions
/// they call, a few calls deep, <see cref="MaxFrontierDepth"/>) and <see cref="Priority.Flow"/>
/// (everything reached from the modules' entry points). <see cref="Run"/> returns once the priorities up to the one it
/// waits for are done; a few threads then stay in the background, translating the rest and the frontier the game opens
/// as it runs (<see cref="Expand"/>), until <see cref="Stop"/>.
/// </para>
/// </summary>
public sealed class CodePrecompiler
{
    /// <summary>Work priorities, most urgent first; <see cref="Index"/> only classifies entry points (the exception
    /// index names function starts; they are not translated wholesale).</summary>
    public enum Priority
    {
        Cached,
        Running,
        Frontier,
        Flow,
        Index,
    }

    private const int Levels = 4;

    /// <summary>How many calls away from executed code the frontier goes: the functions a running function calls,
    /// not their callees. Measured in Assassin's Creed III: Liberation (first session, 128 MiB budget): 3 calls deep
    /// the frontier translated 258k blocks of which 26% ran and the budget ran out (23.8k blocks compiled while the
    /// game ran); 1 call deep, 13.2k.</summary>
    public const int MaxFrontierDepth = 1;

    /// <summary>The boot step of a session with a code cache waits for about this many recorded blocks (the first the
    /// game ran, in order); the rest follow at the same priority while the game starts. Assassin's Creed III:
    /// Liberation records ~160k blocks: waiting for all of them took 2.4 s, for this many 1.1 s, and 195 blocks were
    /// then compiled while the game ran (7 ms) instead of 43.</summary>
    public const int BootCachedBlocks = 65536;

    /// <summary>Whether the boot step stops waiting: the work up to the priority it waits for is done, or, waiting
    /// for the code cache, <see cref="BootCachedBlocks"/> blocks are.</summary>
    public static bool BootStepDone(Priority waitFor, bool doneUpTo, int done) =>
        doneUpTo || (waitFor == Priority.Cached && done >= BootCachedBlocks);

    /// <summary>Statistics so far.</summary>
    public readonly record struct Result(int Translated, int Visited, int Found, bool Cancelled, bool ArenaLimit, int Evicted = 0);

    private readonly BlockCache _cache;
    private readonly AddressSpace? _space;
    private readonly Func<uint, bool>? _follow;
    private readonly Func<uint, uint>? _functionStart;
    private readonly Action<uint, List<(uint Pc, bool Thumb)>>? _pointersOf;
    private readonly ConcurrentDictionary<uint, byte> _expandedFunctions = new();
    private readonly HashSet<uint> _recordedFunctions = [];
    private readonly ConcurrentDictionary<ulong, byte> _seen = new();
    /// <summary>The work of each priority. The cached blocks keep the order they first ran in and the flow is breadth
    /// first (both queues); the work around running code (<see cref="Priority.Running"/>, <see cref="Priority.Frontier"/>)
    /// is newest first (stacks), like Ryujinx's translator queue of rejit requests (a stack: the code the guest reached
    /// last is the code it runs next). Measured in Killzone: Mercenary (first session): with a queue, 974 of the 1,102
    /// blocks the JIT compiled while the game ran had been queued here, behind older work, when the game reached them.</summary>
    private readonly IProducerConsumerCollection<(uint Pc, bool Thumb, int Depth)>[] _queues = NewQueues();

    /// <summary>Whether the work of <paramref name="priority"/> is taken newest first (see <see cref="_queues"/>).</summary>
    public static bool NewestFirst(Priority priority) => priority is Priority.Running or Priority.Frontier;

    private static IProducerConsumerCollection<(uint Pc, bool Thumb, int Depth)>[] NewQueues()
    {
        var queues = new IProducerConsumerCollection<(uint Pc, bool Thumb, int Depth)>[Levels];
        for (int i = 0; i < Levels; i++)
        {
            queues[i] = NewestFirst((Priority)i)
                ? new ConcurrentStack<(uint Pc, bool Thumb, int Depth)>()
                : new ConcurrentQueue<(uint Pc, bool Thumb, int Depth)>();
        }

        return queues;
    }
    private readonly ConcurrentQueue<(Block Block, bool Interpreted)> _executed = new();
    private readonly int[] _outstanding = new int[Levels + 1]; // queued or being worked on, per priority (+ executed blocks)
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _stop = new();
    private int _known, _done, _translated, _found, _arenaLimit, _foreground, _evicted;

    /// <summary>Background threads that keep translating after <see cref="Run"/> returns.</summary>
    public static int BackgroundWorkers => Math.Clamp(Environment.ProcessorCount / 4, 1, 3);

    private CodePrecompiler(BlockCache cache, AddressSpace? space, Func<uint, bool>? follow, Func<uint, uint>? functionStart,
        Action<uint, List<(uint Pc, bool Thumb)>>? pointersOf)
    {
        _cache = cache;
        _space = space;
        _follow = follow;
        _functionStart = functionStart;
        _pointersOf = pointersOf;
    }

    public Result Statistics => new(Volatile.Read(ref _translated), Volatile.Read(ref _done), Volatile.Read(ref _found), _stop.IsCancellationRequested,
        Volatile.Read(ref _arenaLimit) != 0, Volatile.Read(ref _evicted));

    /// <summary>Whether every queue is empty and nothing is being worked on.</summary>
    public bool Finished
    {
        get
        {
            for (int i = 0; i <= Levels; i++)
            {
                if (Volatile.Read(ref _outstanding[i]) != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Whether the block at (<paramref name="pc"/>, <paramref name="thumb"/>) was queued by this run
    /// (diagnostics: the analysis found it).</summary>
    public bool Queued(uint pc, bool thumb) => _seen.ContainsKey(Key(pc, thumb)) || _seen.ContainsKey(Key(pc, thumb) | NarrowKey);

    /// <summary>Stops the background translation (process end); blocks already translated stay.</summary>
    public void Stop()
    {
        _stop.Cancel();
        _work.Release(64);
    }

    private static ulong Key(uint pc, bool thumb) => pc | (thumb ? 1ul << 32 : 0);

    /// <summary>Marks work queued with <see cref="NoFollow"/> apart (<see cref="Add"/>): a narrow guess does not stop
    /// the same block from being queued later to be followed (once the game compiles code next to it).</summary>
    private const ulong NarrowKey = 1ul << 33;

    private bool Add((uint Pc, bool Thumb) target, Priority priority, int depth = 0)
    {
        ulong key = Key(target.Pc, target.Thumb);
        if (depth == NoFollow ? _seen.ContainsKey(key) || !_seen.TryAdd(key | NarrowKey, 0) : !_seen.TryAdd(key, 0))
        {
            return false;
        }

        Interlocked.Increment(ref _outstanding[(int)priority]);
        Interlocked.Increment(ref _known);
        _queues[(int)priority].TryAdd((target.Pc, target.Thumb, depth));
        _work.Release();
        return true;
    }

    /// <summary>The guest ran <paramref name="block"/> for the first time with compiled code (the JIT compiled it, or a
    /// precompiled block was reached): the code around it joins the frontier.</summary>
    public void Expand(Block block) => Expand(block, interpreted: false);

    /// <summary><see cref="Expand(Block)"/>; <paramref name="interpreted"/>: the interpreter ran it for the first time
    /// (it has no compiled code yet).</summary>
    public void Expand(Block block, bool interpreted)
    {
        if (_follow == null || _stop.IsCancellationRequested)
        {
            return;
        }

        Interlocked.Increment(ref _outstanding[Levels]);
        _executed.Enqueue((block, interpreted));
        _work.Release();
    }

    /// <summary>
    /// Translates <paramref name="seeds"/> (with their priorities) and, with <paramref name="follow"/>, the blocks
    /// reachable from them whose start it accepts, in the space <paramref name="space"/>. Waits, on <paramref name="workers"/>
    /// threads (0: every core the process may use but one), until the work up to <paramref name="waitFor"/> is done (or
    /// <paramref name="cancel"/>), reporting (done, known so far) about every 100 ms on the calling thread; then
    /// <see cref="BackgroundWorkers"/> of them stay, at a low priority, for the rest and the frontier (null
    /// <paramref name="waitFor"/>: nothing is waited for). <paramref name="functionStart"/> gives the start of the function
    /// containing an address (0: unknown), for <see cref="Expand"/>; <paramref name="pointersOf"/> adds the functions a
    /// function references through pointers (<see cref="ModuleCodeMap.PointersOfFunction"/>), which join the frontier
    /// the first time code of that function runs.
    /// </summary>
    public static CodePrecompiler Run(BlockCache cache, AddressSpace? space, IEnumerable<((uint Pc, bool Thumb) Target, Priority Priority)> seeds,
        Func<uint, bool>? follow, Priority? waitFor, int workers, Action<int, int>? progress, CancellationToken cancel, Func<uint, uint>? functionStart = null,
        Action<uint, List<(uint Pc, bool Thumb)>>? pointersOf = null)
    {
        var run = new CodePrecompiler(cache, space, follow, functionStart, pointersOf);
        foreach (((uint Pc, bool Thumb) target, Priority priority) in seeds)
        {
            if (priority == Priority.Cached && functionStart?.Invoke(target.Pc) is { } function and not 0)
            {
                run._recordedFunctions.Add(function); // read-only once the threads start
            }
        }

        foreach (((uint Pc, bool Thumb) target, Priority priority) in seeds)
        {
            if (priority != Priority.Index)
            {
                run.Add(target, priority);
            }
        }

        if (workers <= 0)
        {
            workers = Math.Max(1, Environment.ProcessorCount - 1);
        }

        int keep = Math.Min(workers, BackgroundWorkers);
        run._foreground = waitFor != null ? 1 : 0;
        var threads = new Thread[waitFor != null ? Math.Max(workers, keep) : keep];
        for (int i = 0; i < threads.Length; i++)
        {
            bool stays = i < keep;
            threads[i] = new Thread(() => run.Work(stays))
            {
                IsBackground = true,
                Name = $"JIT precompile {i}",
                Priority = waitFor == null ? ThreadPriority.Lowest : ThreadPriority.Normal,
            };
            threads[i].Start();
        }

        if (waitFor is { } level)
        {
            using CancellationTokenRegistration registration = cancel.Register(() => Volatile.Write(ref run._foreground, 0));
            while (Volatile.Read(ref run._foreground) != 0 && !BootStepDone(level, run.DoneUpTo(level), Volatile.Read(ref run._done)))
            {
                Thread.Sleep(100);
                progress?.Invoke(Volatile.Read(ref run._done), Volatile.Read(ref run._known));
            }

            Volatile.Write(ref run._foreground, 0);
            run._work.Release(threads.Length); // the threads that do not stay notice and leave
            // the threads that stay go on below the JIT workers (BelowNormal): what the game needs now compiles first
            for (int i = 0; i < keep; i++)
            {
                try
                {
                    threads[i].Priority = ThreadPriority.Lowest;
                }
                catch (ThreadStateException)
                {
                    // it ended already
                }
            }

            progress?.Invoke(Volatile.Read(ref run._done), Volatile.Read(ref run._known));
        }

        return run;
    }

    /// <summary>Waits until the queued work is done (tests).</summary>
    public bool WaitFinished(TimeSpan timeout) => SpinWait.SpinUntil(() => Finished || _stop.IsCancellationRequested, timeout);

    private bool DoneUpTo(Priority level)
    {
        for (int i = 0; i <= (int)level && i < Levels; i++)
        {
            if (Volatile.Read(ref _outstanding[i]) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private void Work(bool stays)
    {
        Util.NativeThread.Register("JIT precompile", Util.NativeThread.Role.JitWorker);
        using AddressSpace.Activation activation = AddressSpace.Use(_space);
        var exits = new List<(uint Pc, bool Thumb)>();
        Jit.StaticExits = exits;
        Jit.ReuseFreedCode = true;
        while (!_stop.IsCancellationRequested && (stays || Volatile.Read(ref _foreground) != 0))
        {
            if (_executed.TryDequeue(out (Block Block, bool Interpreted) executed))
            {
                try
                {
                    ExpandNow(executed.Block, executed.Interpreted, exits);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[JIT] precompile frontier {executed.Block.Start:X8}: {e.Message}");
                }
                finally
                {
                    Interlocked.Decrement(ref _outstanding[Levels]);
                }

                continue;
            }

            if (!TryTake(out (uint Pc, bool Thumb, int Depth) target, out int level))
            {
                _work.Wait(100);
                continue;
            }

            try
            {
                Translate(target, level, exits);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[JIT] precompile {target.Pc:X8}: {e.Message}");
            }
            finally
            {
                Interlocked.Increment(ref _done);
                Interlocked.Decrement(ref _outstanding[level]);
            }
        }

        Jit.StaticExits = null;
        Jit.ReuseFreedCode = false;
    }

    private bool TryTake(out (uint Pc, bool Thumb, int Depth) target, out int level)
    {
        for (level = 0; level < Levels; level++)
        {
            if (_queues[level].TryTake(out target))
            {
                return true;
            }
        }

        target = default;
        return false;
    }

    private void Translate((uint Pc, bool Thumb, int Depth) target, int level, List<(uint Pc, bool Thumb)> exits)
    {
        // what earlier sessions ran, and what this one reached first, is no guess: it never counts against the
        // speculative budget and is never evicted
        bool speculative = level != (int)Priority.Cached && target.Depth != Reached;
        MakeRoom();
        if (!Jit.ArenaHasRoomForAhead())
        {
            Volatile.Write(ref _arenaLimit, 1);
            return;
        }

        exits.Clear();
        Block block = _cache.Get(target.Pc, target.Thumb);
        if (Jit.CompileAheadOne(block, (byte)level, speculative))
        {
            Interlocked.Increment(ref _translated);
            if (speculative)
            {
                s_speculative[level].TryAdd((block, this));
            }
        }
        else if (_follow != null)
        {
            StaticExitsOf(block, exits); // compiled already (an earlier priority, or the game): its exits still count
        }

        if (_follow == null || !speculative || target.Depth == NoFollow || target.Depth == Reached || InRecordedFunction(block, (Priority)level))
        {
            // the code cache says which blocks of these functions ran (all of a function's paths are seldom taken: in
            // Assassin's Creed III: Liberation the rest of the recorded functions was 140k blocks, of which 5% ran);
            // the frontier only reaches past them from what this session runs (ExpandNow), one block at a time
            return;
        }

        // the flow from the entry points goes everywhere it leads; cached and frontier work only a few calls further
        // (Follow), but all through the function they are in
        Priority next = level == (int)Priority.Flow ? Priority.Flow : Priority.Frontier;
        int depth = level == (int)Priority.Flow ? 0 : target.Depth;
        Follow(block, exits, next, depth, (Priority)level);
    }

    private void ExpandNow(Block block, bool interpreted, List<(uint Pc, bool Thumb)> exits)
    {
        exits.Clear();
        StaticExitsOf(block, exits);
        // narrow, its successors only: in a function the code cache recorded (it says which blocks ran), and around
        // code the game only interprets, which is mostly cold (the whole functions of interpreted code were 28k
        // blocks of which 3.5% ran in Killzone: Mercenary with a code cache, and made the speculative budget churn
        // in Assassin's Creed III: Liberation without one)
        if (InRecordedFunction(block, Priority.Running) || interpreted)
        {
            if (interpreted && block.JitState == 0)
            {
                TranslateNext(block);
            }

            AddSuccessors(block, exits);
            return;
        }

        Follow(block, exits, Priority.Frontier, 0, Priority.Running);

        // the rest of the function it is in (the exception index names the function starts)
        if (_functionStart?.Invoke(block.Start) is not { } start || start == 0)
        {
            return;
        }

        if (start != block.Start && _follow!(start))
        {
            Add((start, block.Thumb), Priority.Running, 0);
        }

        // the functions it references through pointers (vtables it stores, callbacks it registers): computed branches
        // the flow never follows, likely to run soon
        if (_pointersOf != null && _expandedFunctions.TryAdd(start, 0))
        {
            exits.Clear();
            _pointersOf(start, exits);
            foreach ((uint Pc, bool Thumb) target in exits)
            {
                if (_follow!(target.Pc) && Add(target, Priority.Frontier, 0))
                {
                    Interlocked.Increment(ref _found);
                }
            }
        }
    }

    /// <summary>Whether the code cache recorded blocks of the function <paramref name="block"/> is in (frontier work
    /// only: the flow from the entry points is not narrowed).</summary>
    private bool InRecordedFunction(Block block, Priority level) =>
        level != Priority.Flow && _recordedFunctions.Count > 0 && _functionStart != null && _recordedFunctions.Contains(_functionStart(block.Start));

    /// <summary>The speculative blocks every run translated so far (<see cref="MakeRoom"/>; the arena is one per
    /// process, and so is its budget): the flow from the entry points, newest on top; the frontier (callees and
    /// functions referenced by pointer) and the rest of running functions, each oldest first.</summary>
    private static readonly IProducerConsumerCollection<(Block Block, CodePrecompiler Run)>[] s_speculative =
    [
        new ConcurrentQueue<(Block Block, CodePrecompiler Run)>(), // Cached: not speculative, never used
        new ConcurrentQueue<(Block Block, CodePrecompiler Run)>(),
        new ConcurrentQueue<(Block Block, CodePrecompiler Run)>(),
        new ConcurrentStack<(Block Block, CodePrecompiler Run)>(),
    ];

    /// <summary>The order <see cref="MakeRoom"/> evicts speculative work in: the weakest guesses first.</summary>
    public static readonly Priority[] EvictionOrder = [Priority.Flow, Priority.Frontier, Priority.Running];

    /// <summary>
    /// Keeps the speculative code waiting to run within <see cref="Jit.SpeculativeBudget"/> (and all the code
    /// translated ahead within <see cref="Jit.AheadBudget"/>) by evicting speculative blocks the guest has not run;
    /// what ran is never evicted (it is skipped: running publishes a block, which takes it out of the guesses). The
    /// weakest guesses go first: the flow from the entry points, the deepest first (the code farthest from where the
    /// game starts), then the frontier, then the rest of running functions, each the oldest first (code next to what
    /// the game ran long ago and has not needed since; how an LRU cache evicts, without bookkeeping on every use). In
    /// Assassin's Creed III: Liberation 17% of the flow ran, 32% of the frontier and 67% of the rest of running
    /// functions; one queue for the last two evicted good guesses before the game reached them. An evicted block is not translated ahead again (the JIT compiles it if it gets hot):
    /// translating it again whenever the frontier reached it made the eviction churn (Assassin's Creed III:
    /// Liberation, first session: 1.2M translations, 927k evictions, for 174k blocks that ran).
    /// </summary>
    private static void MakeRoom()
    {
        while (Volatile.Read(ref Jit.SpeculativeBytes) > Jit.SpeculativeBudget || !Jit.ArenaHasRoomForAhead())
        {
            if (!TakeWeakestGuess(out (Block Block, CodePrecompiler Run) guess))
            {
                return;
            }

            if (Jit.EvictAhead(guess.Block))
            {
                Interlocked.Increment(ref guess.Run._evicted);
            }
        }
    }

    /// <summary>The next speculative block to evict, in <see cref="EvictionOrder"/>; false when none is left.</summary>
    private static bool TakeWeakestGuess(out (Block Block, CodePrecompiler Run) guess)
    {
        foreach (Priority priority in EvictionOrder)
        {
            if (s_speculative[(int)priority].TryTake(out guess))
            {
                return true;
            }
        }

        guess = default;
        return false;
    }

    /// <summary>Queues the blocks right after <paramref name="block"/> (its static exits, the return point of a call,
    /// the cases of a switch) to be translated without following them further (<see cref="NoFollow"/>).</summary>
    private void AddSuccessors(Block block, List<(uint Pc, bool Thumb)> exits)
    {
        if (block.Insts.Length > 0 && ReturnsAfter(in block.Insts[^1]))
        {
            exits.Add((block.End, block.Thumb));
        }

        if (block.Insts.Length > 0)
        {
            JumpTableTargets(in block.Insts[^1], exits);
        }

        foreach ((uint Pc, bool Thumb) exit in exits)
        {
            if (_follow!(exit.Pc) && Add(exit, Priority.Running, NoFollow))
            {
                Interlocked.Increment(ref _found);
            }
        }
    }

    /// <summary>
    /// The game reached <paramref name="block"/> first (the interpreter ran it, with no compiled code): it goes on top
    /// of the running work, whether or not it was queued already, like a rejit request on top of Ryujinx's translator
    /// stack, instead of waiting in a lower priority or getting hot (<see cref="Jit.MinExecutions"/> runs) for the JIT;
    /// translated (as code that ran, never evicted), it is published the next time the game runs it. Measured in Assassin's Creed III: Liberation (first
    /// session, 4500 frames, 6 runs each): 94% of the blocks still compiled while the game ran had been reached by the
    /// game before the precompilation got to them; with this, 3.1-3.9k blocks were compiled while running instead of
    /// 12.5-13.8k (93% -> 98.5% precompiled). Running this work at the JIT workers' priority as well was measured and
    /// dropped: no better, and once it took the cores from the game (87 slow frames in the first minute).
    /// </summary>
    private void TranslateNext(Block block)
    {
        _seen.TryAdd(Key(block.Start, block.Thumb) | NarrowKey, 0);
        Interlocked.Increment(ref _outstanding[(int)Priority.Running]);
        Interlocked.Increment(ref _known);
        _queues[(int)Priority.Running].TryAdd((block.Start, block.Thumb, Reached));
        _work.Release();
    }

    /// <summary>The depth of work whose exits are not followed (<see cref="AddSuccessors"/>).</summary>
    private const int NoFollow = -1;

    /// <summary>The depth of a block the game reached first (<see cref="TranslateNext"/>): not followed, and not a
    /// guess (it ran), so not evicted either; a block evicted as a guess and then reached is translated once more.</summary>
    private const int Reached = -2;

    private void Follow(Block block, List<(uint Pc, bool Thumb)> exits, Priority priority, int depth, Priority source)
    {
        if (block.Insts.Length > 0 && ReturnsAfter(in block.Insts[^1]))
        {
            exits.Add((block.End, block.Thumb));
        }

        if (block.Insts.Length > 0)
        {
            JumpTableTargets(in block.Insts[^1], exits);
        }

        // frontier: an exit that stays in the block's function (a branch, the return point after a call) keeps its
        // depth, so the whole function gets translated; one that leaves it (a call) goes one deeper
        bool bounded = priority != Priority.Flow;
        uint function = bounded && _functionStart != null ? _functionStart(block.Start) : 0;
        // the rest of a function the game is running (or ran in an earlier session) comes before the other frontier
        // work: the game is in that function now, and a block it reaches before its translation is compiled by the JIT
        bool running = depth == 0 && priority == Priority.Frontier && (source is Priority.Cached or Priority.Running);

        foreach ((uint Pc, bool Thumb) exit in exits)
        {
            int exitDepth = depth;
            Priority exitPriority = priority;
            if (bounded && (function == 0 || _functionStart!(exit.Pc) != function))
            {
                exitDepth = depth + 1;
                if (exitDepth > MaxFrontierDepth)
                {
                    continue;
                }
            }
            else if (running)
            {
                exitPriority = Priority.Running;
            }

            if (_follow!(exit.Pc) && Add(exit, exitPriority, exitDepth))
            {
                Interlocked.Increment(ref _found);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static ArmFrontend? t_frontend;

    /// <summary>The static exits of a block without compiling it (the frontend's translation alone).</summary>
    private static void StaticExitsOf(Block block, List<(uint Pc, bool Thumb)> exits)
    {
        if (block.Insts.Length == 0)
        {
            return;
        }

        IrBlock ir = (t_frontend ??= Backends.NativeArm64Compiler.NewFrontend(reuseIr: true)).Translate(block);
        foreach (IrInst x in ir.Insts)
        {
            if (x.Op == IrOp.ExitStatic)
            {
                exits.Add((x.Imm, x.Imm2 != 0));
            }
        }
    }

    /// <summary>The most entries a jump table may have for <see cref="JumpTableTargets"/> (compilers bound a switch's
    /// table by its case range; a larger bound is more likely a misread than a real table).</summary>
    public const int MaxJumpTableEntries = 1024;

    /// <summary>
    /// The case targets of a Thumb-2 table branch (TBB/TBH [PC, Rm], a compiled switch): the table follows the
    /// instruction, and its size comes from the bound check compilers put right before it, CMP Rm, #n then BHI (n + 1
    /// entries) or BHS/BCS (n entries) to the default case. Any other shape (a table elsewhere, no bound check) is
    /// left to the JIT; a misread only costs compile time (blocks are decoded from the bytes that are there).
    /// </summary>
    public static void JumpTableTargets(in DecodedInst last, List<(uint Pc, bool Thumb)> exits)
    {
        if (last.Op != Op.T32_Tbb || ((last.Raw >> 16) & 0xF) != 15)
        {
            return;
        }

        uint index = last.Raw & 0xF;
        bool halfwords = (last.Raw & 0x10) != 0;
        int count = TableBound(last.Address, index);
        uint table = last.Address + 4;
        for (int i = 0; i < count; i++)
        {
            uint entry = halfwords ? (uint)ReadHalf(table + 2 * (uint)i) : ReadByte(table + (uint)i);
            if (entry == uint.MaxValue)
            {
                return;
            }

            exits.Add((table + 2 * entry, true));
        }
    }

    /// <summary>How many instructions the bound check of a table branch may be spread over: compilers schedule
    /// independent loads, stores and VFP moves between the CMP, the conditional branch and the TBB/TBH.</summary>
    public const int MaxTableBoundDistance = 8;

    /// <summary>
    /// Entries of the table of a TBB/TBH at <paramref name="address"/> indexed by <paramref name="index"/>, from the
    /// CMP + conditional branch before it; 0 when they are not there. Going back from the table branch, instructions
    /// that neither set the flags nor write the index (<see cref="Neutral"/>) may sit between it, the branch to the
    /// default case and the CMP (in Killzone: Mercenary, CMP r3, #9; LDRB r2; LDRB r1; BHI; TBB [PC, r3]). This is
    /// how RPCS3's PPU analyser finds a jump table's size: it follows the index register back through the block to the
    /// compare that bounds it.
    /// </summary>
    private static int TableBound(uint address, uint index)
    {
        uint at = address;
        int condition = -1; // the branch to the default case, once found
        for (int step = 0; step < MaxTableBoundDistance; step++)
        {
            if (!PreviousThumb(at, out uint previous, out int first, out int second))
            {
                return 0;
            }

            at = previous;
            if (condition < 0)
            {
                // B<c> (16 bits) or B<c>.W (32 bits)
                if (second < 0 && (first & 0xF000) == 0xD000 && (first & 0x0E00) != 0x0E00)
                {
                    condition = (first >> 8) & 0xF;
                }
                else if (second >= 0 && (first & 0xF800) == 0xF000 && (second & 0xD000) == 0x8000 && ((first >> 6) & 0xE) != 0xE)
                {
                    condition = (first >> 6) & 0xF;
                }
                else if (Neutral(first, second, index))
                {
                    continue;
                }
                else
                {
                    return 0;
                }

                if (condition is not (8 or 2))
                {
                    return 0; // HI (index > n) or HS/CS (index >= n)
                }

                continue;
            }

            // CMP Rm, #imm8 (16 bits) or CMP.W Rm, #imm8 (32 bits, an unrotated immediate)
            int bound = -1;
            if (second < 0 && (first & 0xF800) == 0x2800 && ((uint)(first >> 8) & 7) == index)
            {
                bound = first & 0xFF;
            }
            else if (second >= 0 && (first & 0xFBF0) == 0xF1B0 && (first & 0xF) == index
                && (second & 0x8F00) == 0x0F00 && (second & 0x7000) == 0 && (first & 0x400) == 0)
            {
                bound = second & 0xFF;
            }
            else if (Neutral(first, second, index))
            {
                continue;
            }

            if (bound < 0)
            {
                return 0;
            }

            int count = condition == 8 ? bound + 1 : bound;
            return Math.Min(count, MaxJumpTableEntries);
        }

        return 0;
    }

    /// <summary>The Thumb instruction that ends at <paramref name="end"/>: 32 bits when the halfword 4 bytes back is a
    /// 32-bit prefix (<paramref name="second"/> its second halfword), else 16 bits (<paramref name="second"/> -1).</summary>
    private static bool PreviousThumb(uint end, out uint start, out int first, out int second)
    {
        int wide = ReadHalf(end - 4);
        int last = ReadHalf(end - 2);
        start = end - 2;
        first = last;
        second = -1;
        if (last < 0)
        {
            return false;
        }

        if (wide >= 0 && Decoder.IsThumb32((ushort)wide))
        {
            start = end - 4;
            first = wide;
            second = last;
        }

        return true;
    }

    /// <summary>
    /// Whether a Thumb instruction can sit inside a table branch's bound check: it does not set the flags, does not
    /// write the index register <paramref name="index"/> or PC, and does not branch. Accepted: loads and stores of
    /// another register (16 bits: immediate, register and SP-relative forms, the literal load; 32 bits: single
    /// loads and stores), the high-register ADD and MOV, ADR / ADD Rd, SP / ADD SP, and VFP/NEON instructions other
    /// than a move into the index register or into the flags (VMRS APSR_nzcv). Anything else ends the search.
    /// </summary>
    public static bool Neutral(int first, int second, uint index)
    {
        if (second >= 0)
        {
            if ((first & 0xEC00) == 0xEC00)
            {
                // coprocessor (VFP/NEON): not a VLDM/VSTM writing back the index, not a VMOV/VMRS into the index or
                // the flags (VMOV Rt, Sn / VMRS: Rt in the second halfword; VMOV Rt, Rt2, Dm: Rt2 in the first)
                if ((first & 0xFE00) == 0xEC00 && (first & 0x0020) != 0 && (uint)(first & 0xF) == index)
                {
                    return false;
                }

                bool toCore = ((first & 0xFF10) == 0xEE10 && (second & 0x0F10) == 0x0A10) || (first & 0xFFF0) == 0xEC50;
                bool pair = (first & 0xFFF0) == 0xEC50;
                return !toCore || ((uint)(second >> 12) != index && (second >> 12) != 15 && (!pair || (uint)(first & 0xF) != index));
            }

            if ((first & 0xFE00) == 0xF800 && (first & 0x0060) != 0x0060)
            {
                // single data load/store (LDR/STR/LDRB/STRB/LDRH/STRH and signed loads), not writing back the index
                bool writeBack = (first & 0x0080) == 0 && (second & 0x0900) == 0x0900;
                return (uint)(second >> 12) != index && (second >> 12) != 15 && !(writeBack && (uint)(first & 0xF) == index);
            }

            return false;
        }

        uint low = (uint)first & 7;
        uint high = (uint)(first >> 8) & 7;
        return (first & 0xF000) switch
        {
            0x5000 or 0x6000 or 0x7000 or 0x8000 => low != index, // LDR/STR (register, immediate, byte, halfword)
            0x9000 => high != index, // LDR/STR Rt, [SP, #imm]
            0x4000 => (first & 0x0F00) switch
            {
                0x0800 or 0x0900 or 0x0A00 or 0x0B00 or 0x0C00 or 0x0D00 or 0x0E00 or 0x0F00 => high != index, // LDR Rt, [PC, #imm]
                0x0400 or 0x0600 => ((((uint)first >> 4) & 8) | low) is var rd && rd != index && rd != 15, // ADD / MOV (high registers)
                _ => false,
            },
            0xA000 => high != index, // ADR / ADD Rd, SP, #imm
            0xB000 => (first & 0x0F00) == 0x0000, // ADD/SUB SP, SP, #imm
            _ => false,
        };
    }

    private static int ReadHalf(uint address) => GuestMemory.IsMapped(address) && GuestMemory.IsMapped(address + 1) ? GuestMemory.Read<ushort>(address) : -1;

    private static uint ReadByte(uint address) => GuestMemory.IsMapped(address) ? GuestMemory.Read<byte>(address) : uint.MaxValue;

    /// <summary>Whether execution continues after the block's last instruction once it returns: calls (BL, BLX) and
    /// supervisor calls come back to the next instruction, which the block's static exits do not name.</summary>
    public static bool ReturnsAfter(in DecodedInst last) => last.Op switch
    {
        Op.A32_Bl or Op.A32_BlxImm or Op.A32_BlxReg or Op.T32_Bl or Op.T32_BlxImm or Op.A32_Svc or Op.T16_Svc => true,
        Op.T16_BxBlx => (last.Raw & 0x80) != 0, // BLX Rm (BX Rm has the L bit clear)
        _ => false,
    };

    /// <summary>
    /// The entry points of a loaded module, with their priority: module_start/stop/exit and the exported functions
    /// (<see cref="Priority.Flow"/>: the analysis starts there), and the function starts of its exception index table
    /// (.ARM.exidx: one entry per function with unwind information, which compilers emit for nearly every function;
    /// <see cref="Priority.Index"/>: they say where functions start, for <see cref="Expand"/>, and are not translated
    /// wholesale). Bit 0 of an address selects Thumb; index entries carry no instruction set, so they take the module's
    /// (the one most of its explicit entry points use).
    /// </summary>
    public static List<((uint Pc, bool Thumb) Target, Priority Priority)> ModuleEntryPoints(LoadedModule module)
    {
        var entries = new List<((uint Pc, bool Thumb), Priority)>();
        int thumbVotes = 0;
        void AddEntry(uint address)
        {
            if (address == 0 || address == 0xFFFFFFFF)
            {
                return;
            }

            bool thumb = (address & 1) != 0;
            thumbVotes += thumb ? 1 : -1;
            entries.Add(((address & ~1u, thumb), Priority.Flow));
        }

        AddEntry(module.StartEntry);
        AddEntry(module.StopEntry);
        AddEntry(module.ExitEntry);
        foreach (uint entry in module.ExportedFunctionEntries)
        {
            AddEntry(entry);
        }

        bool moduleThumb = thumbVotes >= 0;
        if (module.ExidxTop != 0 && module.ExidxEnd > module.ExidxTop && module.ExidxEnd - module.ExidxTop < 64u << 20)
        {
            for (uint at = module.ExidxTop; at + 8 <= module.ExidxEnd; at += 8)
            {
                if (!GuestMemory.IsMapped(at))
                {
                    break;
                }

                uint word = GuestMemory.R32(at);
                if ((word & 0x80000000) != 0)
                {
                    continue; // not a prel31 function offset
                }

                uint function = at + (uint)((int)(word << 1) >> 1);
                entries.Add(((function & ~1u, (function & 1) != 0 || moduleThumb), Priority.Index));
            }
        }

        return entries;
    }

    /// <summary>The sorted function starts of a module's exception index (<see cref="ModuleEntryPoints"/>).</summary>
    public static uint[] FunctionStarts(LoadedModule module)
    {
        var starts = new List<uint>();
        foreach (((uint Pc, bool Thumb) target, Priority priority) in ModuleEntryPoints(module))
        {
            if (priority == Priority.Index)
            {
                starts.Add(target.Pc);
            }
        }

        starts.Sort();
        return [.. starts];
    }

    /// <summary>The largest of the sorted <paramref name="starts"/> at or below <paramref name="address"/>, or 0.</summary>
    public static uint FunctionContaining(uint[] starts, uint address)
    {
        int i = Array.BinarySearch(starts, address);
        if (i < 0)
        {
            i = ~i - 1;
        }

        return i >= 0 ? starts[i] : 0;
    }

    /// <summary>Whether <paramref name="address"/> is in an executable segment of <paramref name="module"/>.</summary>
    public static bool InCode(LoadedModule module, uint address)
    {
        foreach (KeyValuePair<int, SegmentInfo> segment in module.Segments)
        {
            if (module.SegmentFlags.TryGetValue(segment.Key, out (uint Flags, uint MemSz, uint FileSz) flags) && (flags.Flags & 1) == 0)
            {
                continue; // PF_X clear: data
            }

            if (address >= segment.Value.Address && address - segment.Value.Address < segment.Value.Size)
            {
                return true;
            }
        }

        return false;
    }
}
