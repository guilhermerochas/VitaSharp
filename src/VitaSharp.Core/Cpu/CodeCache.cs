using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Cpu;

/// <summary>A translated block of a module, by where it is in the module: the segment and the offset from the
/// segment's load address, so the entry holds wherever the loader places the module.</summary>
public readonly record struct CodeCacheEntry(int Segment, uint Offset, bool Thumb);

/// <summary>
/// The on-disk file of <see cref="CodeCache"/>: the blocks one module's code ran with compiled code, in the order they
/// first ran, for one module image and one translator configuration. Layout (little endian): magic "VSJC", format version, configuration key
/// (<see cref="ConfigurationKey"/>), module image hash, entry count, then per entry the offset (u32), the segment (u8)
/// and the instruction set (u8, 1 = Thumb), and last the XxHash64 of everything before it. A file of another format,
/// configuration or image, a truncated or corrupted one, is ignored (and rewritten at the next save).
/// </summary>
public static class CodeCacheFile
{
    public const uint Magic = 0x434A5356; // "VSJC"

    /// <summary>The file layout and meaning; bump when either changes (2: only blocks that ran, in first-use order).</summary>
    public const int FormatVersion = 2;

    /// <summary>
    /// What makes the recorded block starts valid besides the guest code itself: where the block decoder splits blocks
    /// (<see cref="BlockBuilder.MaxInstructions"/>, its end-of-block rules: bump <see cref="TranslatorVersion"/> when
    /// they change), the host architecture and the CPU backend the blocks were translated for.
    /// </summary>
    public const int TranslatorVersion = 1;

    private const int HeaderSize = 4 + 4 + 8 + 8 + 4;
    private const int EntrySize = 6;

    /// <summary>The configuration this build and host translate with (the key every file must carry).</summary>
    public static ulong ConfigurationKey => KeyOf(Jit.Mode, RuntimeInformation.ProcessArchitecture);

    /// <summary>The configuration key of a translator version, backend and host architecture.</summary>
    public static ulong KeyOf(string backend, Architecture host, int translatorVersion = TranslatorVersion) =>
        XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(
            $"{FormatVersion}|{translatorVersion}|{BlockBuilder.MaxInstructions}|{backend}|{host}"));

    /// <summary>The file of a module image in <paramref name="directory"/>.</summary>
    public static string PathFor(string directory, ulong imageHash) => Path.Combine(directory, $"{imageHash:x16}.vsjc");

    public static byte[] Serialize(ulong imageHash, ulong configurationKey, IReadOnlyCollection<CodeCacheEntry> entries)
    {
        byte[] data = new byte[HeaderSize + EntrySize * entries.Count + 8];
        Span<byte> span = data;
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], FormatVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], configurationKey);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], imageHash);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], entries.Count);
        int at = HeaderSize;
        foreach (CodeCacheEntry entry in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], entry.Offset);
            span[at + 4] = (byte)entry.Segment;
            span[at + 5] = entry.Thumb ? (byte)1 : (byte)0;
            at += EntrySize;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(span[at..], XxHash64.HashToUInt64(span[..at]));
        return data;
    }

    /// <summary>The entries of a file's bytes, or null when they are not a valid file for this image and configuration.</summary>
    public static List<CodeCacheEntry>? Deserialize(ReadOnlySpan<byte> data, ulong imageHash, ulong configurationKey)
    {
        if (data.Length < HeaderSize + 8
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != FormatVersion
            || BinaryPrimitives.ReadUInt64LittleEndian(data[8..]) != configurationKey
            || BinaryPrimitives.ReadUInt64LittleEndian(data[16..]) != imageHash)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
        if (count < 0 || (long)HeaderSize + (long)EntrySize * count + 8 != data.Length)
        {
            return null;
        }

        int end = HeaderSize + EntrySize * count;
        if (XxHash64.HashToUInt64(data[..end]) != BinaryPrimitives.ReadUInt64LittleEndian(data[end..]))
        {
            return null;
        }

        var entries = new List<CodeCacheEntry>(count);
        for (int at = HeaderSize; at < end; at += EntrySize)
        {
            entries.Add(new CodeCacheEntry(data[at + 4], BinaryPrimitives.ReadUInt32LittleEndian(data[at..]), data[at + 5] != 0));
        }

        return entries;
    }

    /// <summary>Reads a module's file; null when there is none or it is not valid (<see cref="Deserialize"/>).</summary>
    public static List<CodeCacheEntry>? Read(string directory, ulong imageHash, ulong configurationKey)
    {
        string path = PathFor(directory, imageHash);
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllBytes(path), imageHash, configurationKey) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a module's file (through a temporary file: a crash never leaves half a file behind).</summary>
    public static void Write(string directory, ulong imageHash, ulong configurationKey, IReadOnlyCollection<CodeCacheEntry> entries)
    {
        Directory.CreateDirectory(directory);
        string path = PathFor(directory, imageHash);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Serialize(imageHash, configurationKey, entries));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Progress of <see cref="CodeCache.PrecompilePending"/>: blocks translated so far, out of those queued.</summary>
public readonly record struct CodePrecompileProgress(int Done, int Total, bool Finished);

/// <summary>
/// The ARM code precompilation of a process (like RPCS3's "Compiling PPU modules"). The blocks a module's code really
/// ran in earlier sessions are recorded per module image, in the order they first ran (<see cref="CodeCacheFile"/>);
/// when the module is loaded again, at any address, the boot step translates them first, on every core the process may
/// use, and lets the process start as soon as they are ready (<see cref="PrecompilePending"/>). The static analysis of
/// the modules (<see cref="CodePrecompiler"/>) then goes on in the background on a few low-priority threads; in a first
/// session, with nothing recorded, the boot step waits for the code reached from the modules' entry points instead.
/// <para>
/// A precompiled block is published (made reachable from the dispatcher and from other blocks' exits) the first time
/// the guest reaches it (<see cref="Jit.ActivateAhead"/>), which is how the cache learns which of them the game runs.
/// Precompiled blocks are ordinary blocks of the process's <see cref="BlockCache"/>: rewriting their code invalidates
/// them like any other translation, and the block decoder reads the module's memory as it is now (its relocations
/// applied), so an entry only says where code starts. The translated machine code itself is not stored: it embeds
/// per-process addresses (link slots, runtime stubs, the dispatch table, the constant pool) that would need a
/// relocation table in both native emitters.
/// </para>
/// </summary>
public sealed class CodeCache
{
    private readonly string? _directory;
    private readonly BlockCache _code;
    private readonly ulong _configurationKey;
    private readonly object _lock = new();
    private readonly List<ModuleRecord> _modules = [];
    private readonly List<(uint Pc, bool Thumb)> _pending = [];
    private readonly List<CodePrecompiler> _runs = [];
    private CodePrecompiler[] _active = [];
    private bool _started;
    private bool _stopped;

    /// <summary>A module's recorded blocks: the order matters (first use), the set avoids duplicates.</summary>
    private sealed class ModuleRecord(LoadedModule module, List<CodeCacheEntry> known)
    {
        public LoadedModule Module { get; } = module;
        public List<CodeCacheEntry> Known { get; } = known;
        public HashSet<CodeCacheEntry> Set { get; } = [.. known];
        public int SavedCount = known.Count;
    }

    /// <param name="directory">Folder of the cache files; null: no cache (only the static analysis).</param>
    /// <param name="code">The process's translation cache.</param>
    /// <param name="configurationKey">Key the files must carry (tests); null: <see cref="CodeCacheFile.ConfigurationKey"/>.</param>
    public CodeCache(string? directory, BlockCache code, ulong? configurationKey = null)
    {
        _directory = directory;
        _code = code;
        _configurationKey = configurationKey ?? CodeCacheFile.ConfigurationKey;
        code.FirstUse = OnFirstUse;
    }

    /// <summary>A block's compiled code ran for the first time: the runs widen their frontier around it.</summary>
    private void OnFirstUse(Block block)
    {
        foreach (CodePrecompiler run in Volatile.Read(ref _active))
        {
            run.Expand(block);
        }
    }

    /// <summary>Recorded blocks waiting for <see cref="PrecompilePending"/>.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Recorded blocks the last <see cref="PrecompilePending"/> started from.</summary>
    public int CachedSeeds { get; private set; }

    /// <summary>The runs started so far (the boot step first; then one per module loaded while the process runs).</summary>
    public IReadOnlyList<CodePrecompiler> Runs
    {
        get
        {
            lock (_lock)
            {
                return [.. _runs];
            }
        }
    }

    /// <summary>Whether the static analysis runs (boot step and later module loads); off: only the recorded blocks.</summary>
    public bool Analyze { get; set; } = true;

    private List<(uint Pc, bool Thumb)> Targets(LoadedModule module, List<CodeCacheEntry> entries)
    {
        var targets = new List<(uint Pc, bool Thumb)>(entries.Count);
        foreach (CodeCacheEntry entry in entries)
        {
            if (module.Segments.TryGetValue(entry.Segment, out SegmentInfo segment) && entry.Offset < segment.Size)
            {
                targets.Add((segment.Address + entry.Offset, entry.Thumb));
            }
        }

        return targets;
    }

    /// <summary>A module was loaded (its image hash set, its segments placed and relocated): its recorded blocks wait for
    /// the boot step or, once the process runs, are translated in the background with the module's analysis.</summary>
    public void ModuleLoaded(LoadedModule module)
    {
        List<CodeCacheEntry> entries = (_directory != null ? CodeCacheFile.Read(_directory, module.ImageHash, _configurationKey) : null) ?? [];
        List<(uint Pc, bool Thumb)> targets = Targets(module, entries);
        bool started;
        lock (_lock)
        {
            _modules.Add(new ModuleRecord(module, entries));
            started = _started;
            if (!started)
            {
                _pending.AddRange(targets);
            }
        }

        if (started)
        {
            Start(targets, [module], waitFor: null, null, CancellationToken.None, 0);
        }
    }

    /// <summary>
    /// Code outside every module was written into the process's memory at [<paramref name="address"/>,
    /// <paramref name="address"/> + <paramref name="size"/>) (the HLE routines written as guest code): it is recorded
    /// and precompiled like a module with one code segment, identified by the hash of its bytes, so the same code in a
    /// later session (the same build) finds what it ran, wherever it is placed. Code that changes between sessions gets
    /// another hash and simply finds nothing.
    /// </summary>
    public LoadedModule CodeRegionPlaced(string name, uint address, uint size)
    {
        var region = new LoadedModule
        {
            Name = name,
            ImageHash = System.IO.Hashing.XxHash64.HashToUInt64(GuestMemory.ReadSpan(address, (int)size)),
            Segments = { [0] = new SegmentInfo(address, address, size) },
            SegmentFlags = { [0] = (5, size, size) }, // R X
        };
        ModuleLoaded(region);
        return region;
    }

    /// <summary>A module is about to be unloaded: what it ran is recorded first.</summary>
    public void ModuleUnloaded(LoadedModule module)
    {
        ModuleRecord? record;
        lock (_lock)
        {
            record = _modules.Find(r => r.Module == module);
            if (record != null)
            {
                _modules.Remove(record);
            }
        }

        if (record != null)
        {
            Collect([record]);
            SaveRecord(record);
        }
    }

    /// <summary>
    /// The boot step. With <paramref name="wait"/>, translates the recorded blocks (in the order they first ran) before
    /// returning, or, when nothing is recorded (a first session), the code the analysis reaches from the modules' entry
    /// points; the rest of the analysis (<see cref="Analyze"/>) continues in the background. Without, everything runs in
    /// the background at once (the cached blocks first). Progress is reported on the calling thread; cancelling lets the
    /// process start at once.
    /// </summary>
    public CodePrecompiler.Result PrecompilePending(Action<CodePrecompileProgress>? progress, CancellationToken cancel, bool wait = true, int workers = 0)
    {
        (uint Pc, bool Thumb)[] targets;
        LoadedModule[] modules;
        lock (_lock)
        {
            targets = [.. _pending];
            _pending.Clear();
            _started = true;
            modules = new LoadedModule[_modules.Count];
            for (int i = 0; i < modules.Length; i++)
            {
                modules[i] = _modules[i].Module;
            }
        }

        CachedSeeds = targets.Length;
        CodePrecompiler.Priority? waitFor = !wait ? null : targets.Length > 0 || !Analyze ? CodePrecompiler.Priority.Cached : CodePrecompiler.Priority.Flow;
        CodePrecompiler? run = Start(targets, modules, waitFor, progress == null ? null : (done, known) => progress(new CodePrecompileProgress(done, known, false)), cancel, workers);
        CodePrecompiler.Result result = run?.Statistics ?? default;
        progress?.Invoke(new CodePrecompileProgress(result.Visited, result.Visited, true));
        return result;
    }

    private CodePrecompiler? Start(IReadOnlyList<(uint Pc, bool Thumb)> targets, LoadedModule[] modules, CodePrecompiler.Priority? waitFor,
        Action<int, int>? progress, CancellationToken cancel, int workers)
    {
        if (!Jit.Enabled || Jit.Chain.Length == 0)
        {
            return null;
        }

        var seeds = new List<((uint Pc, bool Thumb) Target, CodePrecompiler.Priority Priority)>(targets.Count);
        foreach ((uint Pc, bool Thumb) target in targets)
        {
            seeds.Add((target, CodePrecompiler.Priority.Cached));
        }

        Func<uint, bool>? follow = null;
        Func<uint, uint>? functionStart = null;
        Action<uint, List<(uint Pc, bool Thumb)>>? pointersOf = null;
        if (Analyze && modules.Length > 0)
        {
            var maps = new ModuleCodeMap[modules.Length];
            for (int i = 0; i < modules.Length; i++)
            {
                // the exception index only says where functions start (the frontier fills in the functions the game
                // runs); the analysis itself starts from the module's entry points
                seeds.AddRange(CodePrecompiler.ModuleEntryPoints(modules[i]).Where(e => e.Priority != CodePrecompiler.Priority.Index));
                maps[i] = ModuleCodeMap.Build(modules[i]);
            }

            follow = pc => Array.Exists(maps, map => map.InCode(pc));
            functionStart = pc => Array.Find(maps, map => map.InCode(pc))?.FunctionContaining(pc) ?? 0;
            pointersOf = (function, targets) => Array.Find(maps, map => map.InCode(function))?.PointersOfFunction(function, targets);
        }
        if (seeds.Count == 0)
        {
            return null;
        }

        lock (_lock)
        {
            if (_stopped)
            {
                return null;
            }
        }

        var run = CodePrecompiler.Run(_code, AddressSpace.Current, seeds, follow, waitFor, workers, progress, cancel, functionStart, pointersOf);
        bool stopNow;
        lock (_lock)
        {
            _runs.Add(run);
            Volatile.Write(ref _active, [.. _runs]);
            stopNow = _stopped;
        }

        if (stopNow)
        {
            run.Stop();
        }

        return run;
    }

    /// <summary>The process ends: the background translation stops.</summary>
    public void Stop()
    {
        CodePrecompiler[] runs;
        lock (_lock)
        {
            _stopped = true;
            runs = [.. _runs];
            Volatile.Write(ref _active, []);
        }

        if (_code.FirstUse == OnFirstUse)
        {
            _code.FirstUse = null;
        }

        foreach (CodePrecompiler run in runs)
        {
            run.Stop();
        }
    }

    /// <summary>Adds the blocks the process ran so far to the records, in the order they first ran (before the
    /// translation cache is cleared, and before every save).</summary>
    public void Collect()
    {
        ModuleRecord[] records;
        lock (_lock)
        {
            records = [.. _modules];
        }

        Collect(records);
    }

    private void Collect(ModuleRecord[] records)
    {
        if (records.Length == 0)
        {
            return;
        }

        var ran = new List<Block>();
        foreach (Block block in _code.Blocks)
        {
            // compiled and run: a block compiled while running got hot; a precompiled one is published (state 2) only
            // once the guest reaches it
            if (block.JitState == 2 && !block.Invalidated)
            {
                ran.Add(block);
            }
        }

        ran.Sort(static (a, b) => a.FirstUse.CompareTo(b.FirstUse));
        foreach (Block block in ran)
        {
            foreach (ModuleRecord record in records)
            {
                foreach (KeyValuePair<int, SegmentInfo> segment in record.Module.Segments)
                {
                    uint offset = block.Start - segment.Value.Address;
                    if (block.Start >= segment.Value.Address && offset < segment.Value.Size)
                    {
                        var entry = new CodeCacheEntry(segment.Key, offset, block.Thumb);
                        lock (record.Known)
                        {
                            if (record.Set.Add(entry))
                            {
                                record.Known.Add(entry);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// What the precompilation covered (session end): of the blocks the process ran with compiled code, how many were
    /// precompiled and how many the JIT compiled while it ran (by module, or outside every module: code the HLE placed,
    /// heap code), with a sample of where the latter start and the instruction before them (a call or a conditional
    /// branch means plain control flow the analysis did not reach; anything else, a computed branch target).
    /// </summary>
    public string? DescribeMisses(int samples = 24)
    {
        LoadedModule[] modules;
        lock (_lock)
        {
            if (!_started)
            {
                return null;
            }

            modules = new LoadedModule[_modules.Count];
            for (int i = 0; i < modules.Length; i++)
            {
                modules[i] = _modules[i].Module;
            }
        }

        var byModule = new Dictionary<string, int>();
        var lines = new List<string>();
        int used = 0;
        int unused = 0;
        int missed = 0;
        int levels = Enum.GetValues<CodePrecompiler.Priority>().Length;
        int[] aheadBy = new int[levels];
        int[] ranBy = new int[levels];
        long[] bytesBy = new long[levels];
        foreach (Block block in _code.Blocks)
        {
            if (block.Ahead && block.AheadPriority < levels)
            {
                aheadBy[block.AheadPriority]++;
                ranBy[block.AheadPriority] += block.JitState == 2 ? 1 : 0;
                bytesBy[block.AheadPriority] += block.Code?.Tag is Backends.NativeCodeInfo info ? info.Size : 0;
            }

            if (block.JitState == Jit.AheadPending)
            {
                unused++;
                continue;
            }

            if (block.JitState != 2)
            {
                continue;
            }

            if (block.Ahead)
            {
                used++;
                continue;
            }

            missed++;
            LoadedModule? owner = Array.Find(modules, m => CodePrecompiler.InCode(m, block.Start));
            string name = owner?.Name ?? $"(outside the modules: {OutsideName(block)})";
            byModule[name] = byModule.GetValueOrDefault(name) + 1;
            if (lines.Count < samples)
            {
                uint baseAddress = owner != null ? owner.Segments.Values.Where(s => block.Start >= s.Address && block.Start - s.Address < s.Size).Select(s => s.Address).FirstOrDefault() : 0;
                lines.Add($"{name}+0x{block.Start - baseAddress:X} {(block.Thumb ? "T" : "A")} after {PreviousInstruction(block)}");
            }
        }

        double coverage = 100.0 * used / Math.Max(1, used + missed);
        return $"ARM code precompilation: {used} precompiled blocks ran, {missed} compiled while running ({coverage:F1}% of the run blocks were precompiled), "
            + $"{unused} precompiled and not reached, {Jit.AheadBytes / (1024.0 * 1024):F1} MB of precompiled code in the arena (budget {Jit.AheadBudget >> 20} MB); while running: "
            + string.Join(", ", byModule.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")) + "; " + DescribeMissKinds(modules)
            + "; precompiled by priority (translated/ran/MB): " + string.Join(", ", Enumerable.Range(0, levels).Where(i => aheadBy[i] > 0)
                .Select(i => $"{(CodePrecompiler.Priority)i} {aheadBy[i]}/{ranBy[i]}/{bytesBy[i] / (1024.0 * 1024):F1}"))
            + "; samples: " + string.Join("; ", lines);
    }

    /// <summary>
    /// Why the blocks the JIT compiled while the process ran were not precompiled, by the function (exception index)
    /// they are in: the function had other blocks precompiled (the analysis reached it, but not this path, or not in
    /// time), the precompilation never reached the function, or the block is in no known function; and when they first
    /// ran, by tenths of the order in which the process's blocks first ran (late misses point at a full arena or a
    /// frontier that lags behind the game).
    /// </summary>
    private string DescribeMissKinds(LoadedModule[] modules)
    {
        var maps = new ModuleCodeMap[modules.Length];
        for (int i = 0; i < modules.Length; i++)
        {
            maps[i] = ModuleCodeMap.Build(modules[i]);
        }

        var reachedFunctions = new HashSet<uint>();
        var missedBlocks = new List<uint>();
        int outside = 0;
        var ranUses = new List<long>();
        var missedUses = new List<long>();
        foreach (Block block in _code.Blocks)
        {
            bool missed = block.JitState == 2 && !block.Ahead;
            if (block.JitState == 2 && block.FirstUse != 0)
            {
                ranUses.Add(block.FirstUse);
                if (missed)
                {
                    missedUses.Add(block.FirstUse);
                }
            }

            if (!missed && !block.Ahead)
            {
                continue;
            }

            ModuleCodeMap? map = Array.Find(maps, m => m.InCode(block.Start));
            if (map == null)
            {
                outside += missed ? 1 : 0;
                continue;
            }

            uint function = map.FunctionContaining(block.Start);
            if (missed)
            {
                missedBlocks.Add(function);
            }
            else if (function != 0)
            {
                reachedFunctions.Add(function);
            }
        }

        int partial = 0;
        int notReached = 0;
        int noFunction = 0;
        foreach (uint function in missedBlocks)
        {
            if (function == 0)
            {
                noFunction++;
            }
            else if (reachedFunctions.Contains(function))
            {
                partial++;
            }
            else
            {
                notReached++;
            }
        }

        // when they first ran, by tenths of the order in which the process's blocks first ran
        ranUses.Sort();
        int[] tenths = new int[10];
        foreach (long use in missedUses)
        {
            int rank = ranUses.BinarySearch(use);
            tenths[Math.Clamp((int)(10L * Math.Max(0, rank) / Math.Max(1, ranUses.Count)), 0, 9)]++;
        }

        return $"compiled while running, by function: {partial} in functions with precompiled blocks, {notReached} in functions the precompilation never reached, "
            + $"{noFunction} in no indexed function, {outside} outside the modules; "
            + $"by tenth of the run order: {string.Join(" ", tenths)}";
    }

    /// <summary>The guest memory block holding code outside every module (diagnostics).</summary>
    private static string OutsideName(Block block)
    {
        using AddressSpace.Activation activation = AddressSpace.Use(block.Owner?.Space ?? AddressSpace.Current);
        return GuestMemory.Instance?.FindBlock(block.Start)?.Name ?? "?";
    }

    private static string PreviousInstruction(Block block)
    {
        try
        {
            using AddressSpace.Activation activation = AddressSpace.Use(block.Owner?.Space ?? AddressSpace.Current);
            if (!block.Thumb)
            {
                return GuestMemory.IsMapped(block.Start - 4) ? Decoder.DecodeArm(GuestMemory.R32(block.Start - 4), block.Start - 4).Op.ToString() : "?";
            }

            uint at = block.Start - 4;
            if (GuestMemory.IsMapped(at) && Decoder.IsThumb32(GuestMemory.Read<ushort>(at)))
            {
                uint word = ((uint)GuestMemory.Read<ushort>(at) << 16) | GuestMemory.Read<ushort>(at + 2);
                return Decoder.DecodeThumb32(word, at, false, 14).Op + "?";
            }

            return GuestMemory.IsMapped(block.Start - 2) ? Decoder.DecodeThumb16(GuestMemory.Read<ushort>(block.Start - 2), block.Start - 2, false, 14).Op.ToString() : "?";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return "?";
        }
    }

    /// <summary>Writes the files of the modules whose records grew (session end, and periodically while it runs).</summary>
    public void Save()
    {
        Collect();
        ModuleRecord[] records;
        lock (_lock)
        {
            records = [.. _modules];
        }

        foreach (ModuleRecord record in records)
        {
            SaveRecord(record);
        }
    }

    private void SaveRecord(ModuleRecord record)
    {
        if (_directory == null)
        {
            return;
        }

        CodeCacheEntry[] entries;
        lock (record.Known)
        {
            if (record.Known.Count == record.SavedCount)
            {
                return;
            }

            entries = [.. record.Known];
            record.SavedCount = entries.Length;
        }

        try
        {
            CodeCacheFile.Write(_directory, record.Module.ImageHash, _configurationKey, entries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Util.Log.Warn($"Code cache: could not save {record.Module.Name}: {e.Message}");
        }
    }
}
