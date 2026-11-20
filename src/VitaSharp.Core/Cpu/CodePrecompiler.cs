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
        Frontier,
        Flow,
        Index,
    }

    private const int Levels = 3;

    /// <summary>How many blocks away from executed code the frontier goes.</summary>
    public const int MaxFrontierDepth = 3;

    /// <summary>Statistics so far.</summary>
    public readonly record struct Result(int Translated, int Visited, int Found, bool Cancelled, bool ArenaLimit);

    private readonly BlockCache _cache;
    private readonly AddressSpace? _space;
    private readonly Func<uint, bool>? _follow;
    private readonly Func<uint, uint>? _functionStart;
    private readonly Action<uint, List<(uint Pc, bool Thumb)>>? _pointersOf;
    private readonly ConcurrentDictionary<uint, byte> _expandedFunctions = new();
    private readonly ConcurrentDictionary<ulong, byte> _seen = new();
    private readonly ConcurrentQueue<(uint Pc, bool Thumb, int Depth)>[] _queues = [new(), new(), new()];
    private readonly ConcurrentQueue<Block> _executed = new();
    private readonly int[] _outstanding = new int[Levels + 1]; // queued or being worked on, per priority (+ executed blocks)
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _stop = new();
    private int _known, _done, _translated, _found, _arenaLimit, _foreground;

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
        Volatile.Read(ref _arenaLimit) != 0);

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

    /// <summary>Stops the background translation (process end); blocks already translated stay.</summary>
    public void Stop()
    {
        _stop.Cancel();
        _work.Release(64);
    }

    private bool Add((uint Pc, bool Thumb) target, Priority priority, int depth = 0)
    {
        if (!_seen.TryAdd(target.Pc | (target.Thumb ? 1ul << 32 : 0), 0))
        {
            return false;
        }

        Interlocked.Increment(ref _outstanding[(int)priority]);
        Interlocked.Increment(ref _known);
        _queues[(int)priority].Enqueue((target.Pc, target.Thumb, depth));
        _work.Release();
        return true;
    }

    /// <summary>The guest ran <paramref name="block"/> for the first time with compiled code (the JIT compiled it, or a
    /// precompiled block was reached): the code around it joins the frontier.</summary>
    public void Expand(Block block)
    {
        if (_follow == null || _stop.IsCancellationRequested)
        {
            return;
        }

        Interlocked.Increment(ref _outstanding[Levels]);
        _executed.Enqueue(block);
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
            while (Volatile.Read(ref run._foreground) != 0 && !run.DoneUpTo(level))
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
        while (!_stop.IsCancellationRequested && (stays || Volatile.Read(ref _foreground) != 0))
        {
            if (_executed.TryDequeue(out Block? executed))
            {
                try
                {
                    ExpandNow(executed, exits);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[JIT] precompile frontier {executed.Start:X8}: {e.Message}");
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
    }

    private bool TryTake(out (uint Pc, bool Thumb, int Depth) target, out int level)
    {
        for (level = 0; level < Levels; level++)
        {
            if (_queues[level].TryDequeue(out target))
            {
                return true;
            }
        }

        target = default;
        return false;
    }

    private void Translate((uint Pc, bool Thumb, int Depth) target, int level, List<(uint Pc, bool Thumb)> exits)
    {
        if (!Jit.ArenaHasRoomForAhead())
        {
            Volatile.Write(ref _arenaLimit, 1);
            return;
        }

        exits.Clear();
        Block block = _cache.Get(target.Pc, target.Thumb);
        if (Jit.CompileAheadOne(block))
        {
            Interlocked.Increment(ref _translated);
        }
        else if (_follow != null)
        {
            StaticExitsOf(block, exits); // compiled already (an earlier priority, or the game): its exits still count
        }

        if (_follow == null)
        {
            return;
        }

        // the flow from the entry points goes everywhere it leads; cached and frontier work only a few blocks further
        // (Follow), but all through the function they are in
        Priority next = level == (int)Priority.Flow ? Priority.Flow : Priority.Frontier;
        int depth = level == (int)Priority.Flow ? 0 : target.Depth;
        Follow(block, exits, next, depth);
    }

    private void ExpandNow(Block block, List<(uint Pc, bool Thumb)> exits)
    {
        exits.Clear();
        StaticExitsOf(block, exits);
        Follow(block, exits, Priority.Frontier, 0);
        // the rest of the function it is in (the exception index names the function starts)
        if (_functionStart?.Invoke(block.Start) is not { } start || start == 0)
        {
            return;
        }

        if (start != block.Start && _follow!(start))
        {
            Add((start, block.Thumb), Priority.Frontier, 0);
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

    private void Follow(Block block, List<(uint Pc, bool Thumb)> exits, Priority priority, int depth)
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
        // depth, so the whole function of executed code gets translated; one that leaves it (a call) goes one deeper
        uint function = priority == Priority.Frontier && _functionStart != null ? _functionStart(block.Start) : 0;
        foreach ((uint Pc, bool Thumb) exit in exits)
        {
            int exitDepth = depth;
            if (priority == Priority.Frontier && (function == 0 || _functionStart!(exit.Pc) != function))
            {
                exitDepth = depth + 1;
                if (exitDepth > MaxFrontierDepth)
                {
                    continue;
                }
            }

            if (_follow!(exit.Pc) && Add(exit, priority, exitDepth))
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

    /// <summary>Entries of the table of a TBB/TBH at <paramref name="address"/> indexed by <paramref name="index"/>,
    /// from the CMP + conditional branch before it; 0 when they are not there.</summary>
    private static int TableBound(uint address, uint index)
    {
        // the branch to the default case: B<c> (16 bits) or B<c>.W (32 bits)
        uint condition;
        uint compareEnd;
        int branch = ReadHalf(address - 2);
        int wideFirst = ReadHalf(address - 4);
        if (branch >= 0 && (branch & 0xF000) == 0xD000)
        {
            condition = (uint)(branch >> 8) & 0xF;
            compareEnd = address - 2;
        }
        else if (wideFirst >= 0 && branch >= 0 && (wideFirst & 0xF800) == 0xF000 && (branch & 0xD000) == 0x8000)
        {
            condition = (uint)(wideFirst >> 6) & 0xF;
            compareEnd = address - 4;
        }
        else
        {
            return 0;
        }

        if (condition is not (8 or 2))
        {
            return 0; // HI (index > n) or HS/CS (index >= n)
        }

        // CMP Rm, #imm8 (16 bits) or CMP.W Rm, #imm8 (32 bits, an unrotated immediate)
        int bound = -1;
        int compare = ReadHalf(compareEnd - 2);
        int wideCompare = ReadHalf(compareEnd - 4);
        if (compare >= 0 && (compare & 0xF800) == 0x2800 && ((uint)(compare >> 8) & 7) == index)
        {
            bound = compare & 0xFF;
        }
        else if (wideCompare >= 0 && compare >= 0 && (wideCompare & 0xFBF0) == 0xF1B0 && (wideCompare & 0xF) == index
            && (compare & 0x8F00) == 0x0F00 && (compare & 0x7000) == 0 && (wideCompare & 0x400) == 0)
        {
            bound = compare & 0xFF;
        }

        if (bound < 0)
        {
            return 0;
        }

        int count = condition == 8 ? bound + 1 : bound;
        return Math.Min(count, MaxJumpTableEntries);
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
