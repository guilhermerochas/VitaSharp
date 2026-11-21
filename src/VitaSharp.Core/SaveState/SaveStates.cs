using System.Diagnostics;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.SaveState;

/// <summary>
/// Saves and loads the whole emulated system: guest memory, CPU state of every thread, kernel objects, the HLE function
/// table and the state of every HLE module the programs used. A game run directly is one process; under the Vita shell
/// the system's processes (the shell and the applications it opened) are saved together, each with its own address
/// space, HLE state and threads, around the one kernel they share. GPU caches are not saved: the load gives the GPU a
/// new backend that rebuilds them from guest memory, as after a LoadExec.
/// See docs/notes/save-states.md.
/// </summary>
public static class SaveStates
{
    private const string SysSection = "system", FunctionsSection = "hle.functions", ObjectsSection = "kernel.objects",
        BlocksSection = "memory.blocks", MemPrefix = "mem:", WordPagesMemPrefix = "memw:", ModulePrefix = "hle:", LayoutsSection = "layouts",
        KernelModulePrefix = "kernel.hle:", ProcessesSection = "processes", SpacesSection = "memory.spaces",
        SysconSection = "syscon";

    /// <summary>The prefix of the sections of the system's process number <paramref name="index"/> (0, the first
    /// process, has none: a state of one process has the layout of the builds before several processes were saved).</summary>
    private static string ProcessPrefix(int index) => index == 0 ? "" : $"process{index}/";

    /// <summary>A memory block's section name: its address, and the process whose address space holds it when that is
    /// not the first process's (CDRAM and kernel blocks, in every space, name none).</summary>
    private static string MemorySection(bool wordPages, uint address, int spacePid, int firstPid) =>
        (wordPages ? WordPagesMemPrefix : MemPrefix) + address.ToString("X8") + (spacePid != 0 && spacePid != firstPid ? "@" + spacePid.ToString("X") : "");

    /// <summary>The system's processes, the first one first (the order of the process sections in a state).</summary>
    internal static List<Emulator> SystemProcesses(Emulator emu)
    {
        var processes = new List<Emulator> { emu };
        foreach (Emulator process in emu.Vita.Processes)
        {
            if (!ReferenceEquals(process, emu))
            {
                processes.Add(process);
            }
        }

        return processes;
    }

    /// <summary>The thread of any process of the system with a GPU scene open, or null.</summary>
    private static GuestThread? OpenScene(Emulator emu)
    {
        foreach (Emulator process in emu.Vita.Processes)
        {
            if (process.OpenSceneThread is { } thread)
            {
                return thread;
            }
        }

        return emu.OpenSceneThread;
    }

    /// <summary>Raised after a state was loaded (front-ends refresh what they show of the game).</summary>
    public static event Action<Emulator>? Loaded;

    /// <summary>Folder of a title's states: &lt;data&gt;/savestates/&lt;TITLEID&gt;.</summary>
    public static string Folder(string dataRoot, string titleId) => Path.Combine(dataRoot, "savestates", titleId);

    /// <summary>File of slot <paramref name="slot"/> (0 = the quick save slot).</summary>
    public static string SlotPath(string dataRoot, string titleId, int slot) =>
        Path.Combine(Folder(dataRoot, titleId), slot == 0 ? "quick.vss" : $"slot{slot}.vss");

    private static void CheckSystem(Emulator emu)
    {
        if (emu.Parent != null)
        {
            throw new SnapshotException("save states are taken from the system's first process");
        }

        if (emu.MainModule == null || !emu.Running)
        {
            throw new SnapshotException("no game is running");
        }

        foreach (Emulator process in SystemProcesses(emu))
        {
            if (process.Execing || process.ExecRequest != null)
            {
                throw new SnapshotException(ReferenceEquals(process, emu) ? "the game is restarting (LoadExec)" : $"{process.Describe()} is restarting (LoadExec)");
            }

            if (!ReferenceEquals(process, emu) && (process.MainModule == null || !process.Running))
            {
                throw new SnapshotException($"{process.Describe()} is starting or ending");
            }
        }
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// Saves the state to <paramref name="path"/> (written to a temporary file first). Every guest thread is first held
    /// at its next safe point while the emulation keeps running (threads in guest callbacks finish them, module
    /// service threads drain their queues); then the emulation is paused, the state written, and the emulation left
    /// paused or running as it was. Throws <see cref="SnapshotException"/> with a message for the user when the state
    /// cannot be saved within <paramref name="timeoutMs"/>.
    /// </summary>
    public static SaveStateHeader Save(Emulator emu, string path, byte[]? thumbnailPng = null, int timeoutMs = 3000)
    {
        lock (s_gate)
        {
            return SaveLocked(emu, path, thumbnailPng, timeoutMs);
        }
    }

    /// <summary>One save or load at a time: the Gravar tudo pre-roll saves every 30 s on its own thread, and one that ran
    /// during a load (Tearaway in debug mode) resumed the emulation the load had paused, so the load failed half done
    /// ("the guest clock is restored only while the emulation is paused") and the pre-roll state was taken from
    /// threads being replaced.</summary>
    private static readonly object s_gate = new();

    private static SaveStateHeader SaveLocked(Emulator emu, string path, byte[]? thumbnailPng, int timeoutMs)
    {
        CheckSystem(emu);
        CheckModules(emu);
        var sw = Stopwatch.StartNew();
        bool wasPaused = EmulationPause.IsPaused;
        string tmp = path + ".tmp";
        var held = new List<GuestThread>();
        try
        {
            while (true)
            {
                if (EmulationPause.IsPaused)
                {
                    EmulationPause.Resume("save state");
                }

                string? why = HoldBetweenScenes(emu, held, sw, timeoutMs);
                if (why != null)
                {
                    throw new SnapshotException($"not at a safe point: {why}");
                }

                EmulationPause.Pause("save state", @internal: true);
                why = SafePointProblems(emu, out Dictionary<GuestThread, GuestThread.SafeKind>? kinds);
                if (why == null)
                {
                    emu.Gpu.WaitIdle();
                    var seq = kinds.Keys.ToDictionary(t => t, t => t.HleSequence);
                    SaveStateHeader? header = null;
                    List<(string Name, byte[] Data)>? sections = null;
                    try
                    {
                        (header, sections) = Capture(emu, kinds, thumbnailPng);
                    }
                    catch (SnapshotRetryException e)
                    {
                        why = e.Message;
                    }
                    if (header != null)
                    {
                        why = SafePointProblems(emu, out Dictionary<GuestThread, GuestThread.SafeKind>? after);
                        if (why == null && seq.Any(p => p.Key.HleSequence != p.Value || after.GetValueOrDefault(p.Key) != kinds[p.Key]))
                        {
                            why = "a thread woke up while the state was taken";
                        }

                        if (why == null)
                        {
                            long pausedMs = sw.ElapsedMilliseconds;
                            // the game goes on while the file is compressed and written
                            if (!wasPaused)
                            {
                                EmulationPause.Resume("save state");
                            }

                            foreach (GuestThread t in held)
                            {
                                t.HoldForSave(false);
                            }

                            held.Clear();
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                            using (FileStream stream = File.Create(tmp))
                            using (var w = new SaveStateWriter(stream, header))
                            {
                                foreach ((string? name, byte[]? data) in sections!)
                                {
                                    w.Section(name, data);
                                }
                            }

                            File.Move(tmp, path, overwrite: true);
                            Log.Info($"Save state: {path} written in {sw.ElapsedMilliseconds} ms ({pausedMs} ms with the game stopped; {new FileInfo(path).Length / 1024} KB, {kinds.Count} threads)");
                            return header;
                        }
                    }
                }
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    throw new SnapshotException($"not at a safe point: {why}");
                }

                Log.Info($"Save state: not at a safe point ({why}), retrying");
            }
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException) { }
            // always back to how the player left it, also when the state could not be taken (a hung game): the
            // internal pause never stays
            if (wasPaused)
            {
                EmulationPause.Pause("save state");
            }
            else
            {
                EmulationPause.Resume("save state");
            }

            foreach (GuestThread t in held)
            {
                t.HoldForSave(false);
            }
        }
    }

    /// <summary>
    /// Holds the threads between two GPU scenes: the hold starts when no scene is open (a thread that opened one may
    /// be waiting for held threads to finish it), and is released and tried again when a scene stays open. Returns
    /// null once every thread is at a safe point (with the emulation still running), else why not.
    /// </summary>
    private static string? HoldBetweenScenes(Emulator emu, List<GuestThread> held, Stopwatch stopwatch, int timeoutMs)
    {
        string? why = null;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (held.Count == 0)
            {
                for (int i = 0; i < 400 && OpenScene(emu) != null; i++)
                {
                    Thread.Sleep(1);
                }

                held.AddRange(Hold(emu));
            }
            why = WaitForSafePoint(emu, 300, out _);
            if (why == null)
            {
                return null;
            }

            if (OpenScene(emu) == null && !why.Contains("GPU scene", StringComparison.Ordinal))
            {
                continue;
            }
            // a scene stays open: let every thread run, then hold again between two scenes
            foreach (GuestThread t in held)
            {
                t.HoldForSave(false);
            }

            held.Clear();
            Thread.Sleep(5);
        }
        return why ?? "timed out";
    }

    /// <summary>
    /// Replaces the GPU backend while the game runs (live graphics options: the internal resolution hotkey). The guest
    /// threads are held between two GPU scenes, like for a save, so the new backend starts on a scene boundary; its
    /// caches are built again from guest memory, as after a state load. Null when done, else why it was not.
    /// </summary>
    public static string? ReplaceBackendBetweenScenes(Emulator emu, Func<Gpu.IGpuBackend?> factory, int timeoutMs = 3000)
    {
        lock (s_gate)
        {
            bool wasPaused = EmulationPause.IsPaused;
            var held = new List<GuestThread>();
            try
            {
                if (wasPaused)
                {
                    EmulationPause.Resume("GPU backend swap");
                }

                if (HoldBetweenScenes(emu, held, Stopwatch.StartNew(), timeoutMs) is { } why)
                {
                    return why;
                }

                EmulationPause.Pause("GPU backend swap", @internal: true);
                if (OpenScene(emu) is { } scene)
                {
                    return $"thread '{scene.Name}' is finishing a GPU scene";
                }

                long t0 = Stopwatch.GetTimestamp();
                emu.Gpu.WaitIdle();
                emu.Gpu.ReplaceBackend(factory, sameTitle: true);
                Log.Info($"GPU backend replaced while running in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
                return null;
            }
            finally
            {
                if (wasPaused)
                {
                    EmulationPause.Pause("GPU backend swap");
                }
                else
                {
                    EmulationPause.Resume("GPU backend swap");
                }

                foreach (GuestThread t in held)
                {
                    t.HoldForSave(false);
                }
            }
        }
    }

    /// <summary>The threads a state holds: every process's (the shell's and the applications' under the Vita shell)
    /// and the kernel process's (the system's one kernel, whose threads the kernel modules made), when it exists.</summary>
    internal static IEnumerable<GuestThread> SessionThreads(Emulator emu)
    {
        IEnumerable<GuestThread> threads = SystemProcesses(emu).SelectMany(process => process.Kernel.Threads);
        return emu.Vita.KernelProcess is { } kernelProcess ? threads.Concat(kernelProcess.Kernel.Threads) : threads;
    }

    /// <summary>Holds every guest thread of <paramref name="emu"/> at its next safe point (module service threads run on).</summary>
    private static List<GuestThread> Hold(Emulator emu)
    {
        var held = SessionThreads(emu).Where(t => t.HostBody == null).ToList();
        foreach (GuestThread? t in held)
        {
            t.HoldForSave(true);
        }

        return held;
    }

    /// <summary>Refuses a save when a module the game used holds state that is not saved yet.</summary>
    private static void CheckModules(Emulator emu)
    {
        var problems = new List<string>();
        foreach (Emulator process in SystemProcesses(emu))
        {
            CheckModules(process, problems, ReferenceEquals(process, emu) ? "" : process.Describe() + " ");
        }

        if (emu.Vita.KernelProcess is { } kernelProcess)
        {
            CheckModules(kernelProcess, problems, "kernel ");
        }

        if (problems.Count > 0)
        {
            throw new SnapshotException(string.Join("; ", problems));
        }
    }

    private static void CheckModules(Emulator emu, List<string> problems, string prefix)
    {
        foreach (HleModule hleModule in emu.Hle.Modules)
        {
            string name = prefix + hleModule.GetType().Name;
            if (hleModule is ISnapshotState s)
            {
                if (s.CheckSnapshot() is { } problem)
                {
                    problems.Add($"module {name}: {problem}");
                }
            }
            else if (SnapshotSchema.IsStateful(hleModule.GetType()) && emu.Hle.WasUsed(hleModule))
            {
                problems.Add($"module {name}: its state is not supported by save states yet");
            }
        }
    }

    private static string? WaitForSafePoint(Emulator emu, int ms, out Dictionary<GuestThread, GuestThread.SafeKind> kinds)
    {
        long until = Environment.TickCount64 + ms;
        while (true)
        {
            string? why = SafePointProblems(emu, out kinds);
            if (why == null || Environment.TickCount64 > until)
            {
                return why;
            }

            Thread.Sleep(1);
        }
    }

    private static string? SafePointProblems(Emulator emu, out Dictionary<GuestThread, GuestThread.SafeKind> kinds)
    {
        kinds = new();
        if (OpenScene(emu) is { } scene)
        {
            return $"thread '{scene.Name}' is finishing a GPU scene";
        }

        foreach (GuestThread t in SessionThreads(emu))
        {
            if (t.SafePointProblem(out GuestThread.SafeKind k) is { } why)
            {
                return why;
            }

            kinds[t] = k;
        }
        return null;
    }

    /// <summary>Takes the whole state into memory (the emulation is stopped); the file is written afterwards.</summary>
    private static (SaveStateHeader, List<(string, byte[])>) Capture(Emulator emu, Dictionary<GuestThread, GuestThread.SafeKind> kinds, byte[]? thumbnailPng)
    {
        var header = new SaveStateHeader
        {
            FormatVersion = SaveStateFile.FormatVersion,
            EmulatorVersion = SaveStateFile.EmulatorVersion,
            Schema = SnapshotSchema.Hash,
            TitleId = emu.TitleId,
            Title = emu.Title,
            AppVersion = emu.ParamSfo?.GetString("APP_VER") ?? "",
            CreatedUtc = DateTime.UtcNow,
            GuestSeconds = GuestClock.Seconds,
            Thumbnail = thumbnailPng ?? [],
        };
        var list = new List<(string, byte[])>();

        var sys = new SnapshotWriter();
        sys.Write(emu.TitleId);
        sys.Write(GuestClock.SnapshotMicroseconds);
        sys.Write(emu.Pid);
        sys.Write(emu.AppId);
        sys.WriteKernelObject(emu.Kernel.MainThread);
        sys.Write(emu.Vita.AppMgrState);
        sys.Write(emu.BgmPriority); // the BGM priority of the process (was the BGM port owner: same slot)
        list.Add((SysSection, sys.Data.ToArray()));

        var fn = new SnapshotWriter();
        emu.Hle.SaveState(fn);
        list.Add((FunctionsSection, fn.Data.ToArray()));
        list.Add((ObjectsSection, KernelSnapshot.Save(emu, kinds).Data.ToArray()));

        var layouts = new HashSet<string>(StringComparer.Ordinal);
        CaptureModules(emu, "", layouts, list);

        // the other processes of the system (under the Vita shell: the applications the shell opened), each in sections
        // of its own; the processes list names them all, the first one first
        List<Emulator> processes = SystemProcesses(emu);
        var processList = new SnapshotWriter();
        processList.Write(processes.Count);
        for (int index = 0; index < processes.Count; index++)
        {
            Emulator process = processes[index];
            processList.Write(process.Pid);
            processList.Write(process.AppId);
            processList.Write(process.TitleId);
            processList.Write(process.Suspended);
            processList.Write(ReferenceEquals(emu.Vita.Foreground, process));
            if (index == 0)
            {
                continue;
            }

            string prefix = ProcessPrefix(index);
            using Kernel.ProcessScope.Scope processScope = Kernel.ProcessScope.Enter(process);
            var processSystem = new SnapshotWriter();
            processSystem.Write(process.TitleId);
            processSystem.Write(process.Pid);
            processSystem.Write(process.AppId);
            processSystem.WriteKernelObject(process.Kernel.MainThread);
            processSystem.Write(process.BgmPriority);
            list.Add((prefix + SysSection, processSystem.Data.ToArray()));
            var processFunctions = new SnapshotWriter();
            process.Hle.SaveState(processFunctions);
            list.Add((prefix + FunctionsSection, processFunctions.Data.ToArray()));
            CaptureModules(process, prefix, layouts, list);
        }

        list.Add((ProcessesSection, processList.Data.ToArray()));

        // the kernel process's modules (the HLE state its kernel threads made), in sections of their own
        if (emu.Vita.KernelProcess is { } kernelProcess)
        {
            using Kernel.ProcessScope.Scope scope = Kernel.ProcessScope.Enter(kernelProcess);
            var kernelUsed = new HashSet<string>();
            foreach (HleModule hleModule in kernelProcess.Hle.Modules)
            {
                if (hleModule is not ISnapshotState s)
                {
                    continue;
                }

                string name = ModuleKey(hleModule, kernelUsed);
                var mw = new SnapshotWriter { UsedLayouts = layouts };
                s.SaveState(mw);
                list.Add((KernelModulePrefix + name, mw.Data.ToArray()));
            }
        }

        // the system's syscon chip (one for every process); states of builds before it have no section
        var syscon = new SnapshotWriter { UsedLayouts = layouts };
        syscon.Write(emu.Vita.Syscon);
        syscon.Write(emu.Vita.PowerState);
        syscon.Write(emu.Vita.RtcClocks);
        list.Add((SysconSection, syscon.Data.ToArray()));

        // first in the file: a slot list checks it without reading the rest
        var lw = new SnapshotWriter();
        SnapshotLayouts.Write(lw, layouts);
        list.Insert(0, (LayoutsSection, lw.Data.ToArray()));

        long t0 = Stopwatch.GetTimestamp(), raw = 0;
        List<GuestMemory.Block> blocks = emu.Memory.SnapshotBlocks();
        // the process whose address space holds each private block (0: CDRAM and kernel blocks, in every space); read
        // before the block table on load
        var spaceOwners = new Dictionary<AddressSpace, int>();
        foreach (Emulator process in emu.Vita.ProcessesAndKernel.Prepend(emu))
        {
            spaceOwners.TryAdd(process.Space, process.Pid);
        }

        // blocks a process that ended left in its space belong to no process of the state (a load frees them)
        int orphans = blocks.RemoveAll(block => block.Space != null && !spaceOwners.ContainsKey(block.Space));
        if (orphans > 0)
        {
            Log.Warn($"Save state: {orphans} memory blocks of processes that ended are not saved");
        }

        var spaces = new SnapshotWriter();
        int[] blockSpacePids = new int[blocks.Count];
        spaces.Write(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            GuestMemory.Block block = blocks[i];
            int spacePid = block.Space == null ? 0 : spaceOwners[block.Space];
            blockSpacePids[i] = spacePid;
            spaces.Write(spacePid);
        }

        list.Add((SpacesSection, spaces.Data.ToArray()));
        var bt = new SnapshotWriter();
        GuestMemory.WriteBlockTable(bt, blocks);
        list.Add((BlocksSection, bt.Data.ToArray()));
        for (int i = 0; i < blocks.Count; i++)
        {
            GuestMemory.Block b = blocks[i];
            byte[] data = emu.Memory.CopyContents(b, out bool wordPages);
            list.Add((MemorySection(wordPages, b.Address, blockSpacePids[i], emu.Pid), data));
            raw += data.Length;
        }
        Log.Info($"Save state: memory {blocks.Count} blocks, {raw >> 20} MB of non-zero pages copied in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
        return (header, list);
    }

    /// <summary>The sections of the HLE modules of <paramref name="process"/> with save state support.</summary>
    private static void CaptureModules(Emulator process, string prefix, HashSet<string> layouts, List<(string, byte[])> list)
    {
        var used = new HashSet<string>();
        foreach (HleModule hleModule in process.Hle.Modules)
        {
            if (hleModule is not ISnapshotState s)
            {
                continue;
            }

            string name = ModuleKey(hleModule, used);
            var mw = new SnapshotWriter { UsedLayouts = layouts };
            s.SaveState(mw);
            list.Add((prefix + ModulePrefix + name, mw.Data.ToArray()));
        }
    }

    /// <summary>A module's section name: its type name (numbered when a type is registered more than once).</summary>
    private static string ModuleKey(HleModule hleModule, HashSet<string> used)
    {
        string name = hleModule.GetType().Name;
        for (int i = 2; !used.Add(name); i++)
        {
            name = $"{hleModule.GetType().Name}#{i}";
        }

        return name;
    }

    // ------------------------------------------------------------------ load

    /// <summary>Reads the header of a state file and says whether it can be loaded into <paramref name="emu"/>.</summary>
    public static string? CheckFile(Emulator emu, string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            SaveStateHeader header = SaveStateFile.ReadHeader(file);
            if (SaveStateFile.Incompatibility(header, emu.TitleId) is { } why)
            {
                return why;
            }

            if (SaveStateFile.IsLegacy(header))
            {
                return LayoutProblem(header, SnapshotLayouts.Legacy(header.Schema)!, positional: true);
            }

            file.Position = 0;
            using var reader = new SaveStateReader(file);
            if (!reader.Next(out string? name, out _) || name != LayoutsSection)
            {
                return Localization.Loc.Format("StateModuleChanged", SaveStateFile.ShortVersion(header.EmulatorVersion), "the state has no layout table");
            }

            return LayoutProblem(header, SnapshotLayouts.Read(new SnapshotReader(reader.ReadAll())));
        }
        catch (SnapshotException e)
        {
            return e.Message;
        }
        catch (IOException e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// The executable the state was saved in: its main module ("app0:Jak1.self" after the Jak and Daxter Collection's
    /// LoadExec), the first module of its kernel object table (the program loads it before anything else). Read from
    /// the sections before the memory, so it is quick. Null when the file cannot be read.
    /// </summary>
    public static string? ProgramOf(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            using var reader = new SaveStateReader(file);
            while (reader.Next(out string? name, out _))
            {
                if (name != ObjectsSection)
                {
                    continue;
                }

                var r = new SnapshotReader(reader.ReadAll());
                r.ReadInt32();
                int n = r.ReadInt32();
                (int Uid, string Path)? first = null;
                for (int i = 0; i < n; i++)
                {
                    string kind = r.ReadStringNotNull();
                    int uid = r.ReadInt32();
                    r.ReadStringNotNull();
                    r.ReadInt32();
                    r.ReadInt32();
                    SnapshotReader b = r.ReadBlock();
                    if (kind == KernelSnapshot.Module && (first == null || uid < first.Value.Uid))
                    {
                        first = (uid, b.ReadStringNotNull());
                    }
                }
                return first?.Path;
            }
        }
        catch (Exception e) when (e is SnapshotException or IOException) { }
        return null;
    }

    /// <summary>
    /// Presented frames a state loaded when the game starts waits for (the desktop's --load-state, a slot picked on
    /// the Android game page): the game's modules and service threads exist by then.
    /// </summary>
    public const int StartLoadFrames = 120;

    /// <summary>
    /// The executable (relative to app0:) to boot so that the state in <paramref name="path"/> loads into the program it
    /// was saved in: the program a LoadExec started ("Jak1.self" of the Jak and Daxter Collection) is booted directly.
    /// "eboot.bin" when the state was saved in it, outside app0: or cannot be read.
    /// </summary>
    public static string BootExecutable(string path)
    {
        if (ProgramOf(path) is { } program && program.StartsWith("app0:", StringComparison.OrdinalIgnoreCase))
        {
            string exe = program[5..].TrimStart('/');
            if (exe.Length > 0)
            {
                return exe;
            }
        }

        return "eboot.bin";
    }

    /// <summary>
    /// When the state in <paramref name="path"/> was saved in another executable of the running game than the one
    /// running now (the program a LoadExec started: Jak 1 of the Jak and Daxter Collection, saved and then loaded after
    /// the game was opened again, which boots the collection's menu), performs that LoadExec first and waits until the
    /// new program has presented <paramref name="frames"/> frames (its modules and service threads exist, as for a
    /// load at start). Returns false when nothing had to be done; throws <see cref="SnapshotException"/> when the
    /// program did not come up in time.
    /// </summary>
    public static bool SwitchProgramFor(Emulator emu, string path, Action<string>? status = null, int frames = StartLoadFrames, int timeoutMs = 60_000)
    {
        if (ProgramOf(path) is not { } program || emu.MainModule == null
            || string.Equals(program, emu.MainModule.Path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!program.StartsWith("app0:", StringComparison.OrdinalIgnoreCase))
        {
            throw new SnapshotException($"the state was saved in {program}, which cannot be started here");
        }

        CheckSystem(emu);
        if (SaveStateFile.Incompatibility(SaveStateFile.ReadHeader(path), emu.TitleId) is { } why)
        {
            throw new SaveStateIncompatibleException(why);
        }

        Log.Info($"Load state: the state was saved in {program}, {emu.MainModule.Path} is running: LoadExec {program} first");
        status?.Invoke($"Abrindo {program[5..].TrimStart('/')} para carregar o estado...");
        emu.RequestExec(program, []);
        long until = Environment.TickCount64 + timeoutMs;
        while (emu.ExecRequest != null || emu.Execing || !emu.Running || emu.MainModule == null)
        {
            if (Environment.TickCount64 > until)
            {
                throw new SnapshotException($"{program} did not start in time");
            }

            Thread.Sleep(10);
        }
        long start = Hle.Modules.Display.FramesPresented;
        while (Hle.Modules.Display.FramesPresented < start + frames)
        {
            if (Environment.TickCount64 > until)
            {
                throw new SnapshotException($"{program} did not present {frames} frames in time");
            }

            Thread.Sleep(10);
        }
        return true;
    }

    /// <summary>
    /// Loads the state in <paramref name="path"/> into the running game. Every guest thread is stopped and replaced by
    /// the saved ones; <paramref name="backendFactory"/> (when given) makes the GPU backend that replaces the current
    /// one, whose caches belong to the old state. Incompatible states are refused before anything changes
    /// (<see cref="SnapshotException"/>); a failure after the threads were stopped restores the session from before the
    /// load, kept in memory when the threads were held (<see cref="UndoState"/>), and only when that is not possible
    /// leaves the game unusable (the message says which).
    /// </summary>
    public static void Load(Emulator emu, string path, Func<Gpu.IGpuBackend?>? backendFactory = null)
    {
        lock (s_gate)
        {
            LoadLocked(emu, path, backendFactory);
        }

        Loaded?.Invoke(emu);
    }

    private static void LoadLocked(Emulator emu, string path, Func<Gpu.IGpuBackend?>? backendFactory)
    {
        CheckSystem(emu);
        var sw = Stopwatch.StartNew();
        SaveStateHeader header;
        var sections = new List<(string Name, byte[] Data)>();
        using (FileStream file = File.OpenRead(path))
        using (var reader = new SaveStateReader(file))
        {
            header = reader.Header;
            if (SaveStateFile.Incompatibility(header, emu.TitleId) is { } why)
            {
                throw new SaveStateIncompatibleException(why);
            }
            // decompressed while the game still runs
            while (reader.Next(out string? name, out _))
            {
                sections.Add((name, reader.ReadAll()));
            }
        }
        long readMs = sw.ElapsedMilliseconds;
        foreach (Emulator process in SystemProcesses(emu))
        {
            foreach (HleModule hleModule in process.Hle.Modules)
            {
                if (hleModule is not ISnapshotState && SnapshotSchema.IsStateful(hleModule.GetType()) && process.Hle.WasUsed(hleModule))
                {
                    throw new SnapshotException($"module {hleModule.GetType().Name} was used in this session and its state cannot be replaced; restart the game, then load the state");
                }
            }
        }

        // whether this build can take every part of the state is decided here, with the game still running
        (IReadOnlyDictionary<string, SnapshotLayout> layouts, bool positional) = CheckSections(emu, header, sections);

        bool wasPaused = EmulationPause.IsPaused;
        var held = new List<GuestThread>();
        try
        {
            // module service threads finish what they are doing with the emulation running, then everything stops
            if (wasPaused)
            {
                EmulationPause.Resume("load state");
            }

            HoldForLoad(emu, held);
            WaitForUndoPoint(emu, UndoPointTimeoutMs);
            EmulationPause.Pause("load state", @internal: true);
            UndoState? undo = TakeUndoState(emu);
            long t0 = sw.ElapsedMilliseconds;
            try
            {
                StopThreads(emu, tolerateStuck: false);
                Log.Info($"Load state: threads stopped in {sw.ElapsedMilliseconds - t0} ms");
                StartRestored(LoadCore(emu, sections, layouts, positional, backendFactory));
            }
            catch (Exception e)
            {
                string reason = e is SnapshotException ? e.Message : $"loading the state failed ({e.Message})";
                if (undo != null && RollBack(emu, undo, backendFactory))
                {
                    throw new SnapshotException($"{reason}; the game went back to where it was before the load");
                }

                throw new SnapshotException($"{reason}; restart the game");
            }
            Log.Info($"Save state: {path} loaded in {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds - readMs} ms with the game stopped; saved {header.CreatedUtc.ToLocalTime():g}, VitaSharp {header.EmulatorVersion})");
        }
        finally
        {
            foreach (GuestThread t in held)
            {
                t.HoldForSave(false);
            }

            if (!wasPaused)
            {
                EmulationPause.Resume("load state");
            }
            else
            {
                EmulationPause.Pause("load state");
            }
        }
    }

    /// <summary>
    /// Holds the guest threads for a load until no GPU scene is open and the module service threads (kept across the
    /// load) are idle. Like a save, the hold starts between two scenes and is released and taken again when a scene
    /// stays open: the thread that opened it may wait for a held thread to finish it (Tearaway's SandPit, whose scenes
    /// are fed by worker threads, kept one open for the whole 3 s and every load at that point was refused).
    /// </summary>
    private static void HoldForLoad(Emulator emu, List<GuestThread> held)
    {
        long until = Environment.TickCount64 + 3000;
        while (true)
        {
            if (held.Count == 0)
            {
                for (int i = 0; i < 400 && OpenScene(emu) != null; i++)
                {
                    Thread.Sleep(1);
                }

                held.AddRange(Hold(emu));
            }
            string? busy = null;
            long settle = Environment.TickCount64 + 300;
            do
            {
                busy = null;
                foreach (GuestThread t in SessionThreads(emu))
                {
                    if (t.HostBody != null && t.IsLive && t.SafePointProblem(out _) is { } p)
                    {
                        busy = p;
                    }
                }

                if (OpenScene(emu) is { } scene)
                {
                    busy = $"thread '{scene.Name}' is finishing a GPU scene";
                }

                busy ??= ModulesBusyForLoad(emu);
                if (busy == null)
                {
                    return;
                }

                Thread.Sleep(1);
            } while (Environment.TickCount64 < settle);
            if (Environment.TickCount64 > until)
            {
                throw new SnapshotException($"not at a safe point: {busy}");
            }

            if (OpenScene(emu) == null)
            {
                continue;
            }
            // a scene stays open: let every thread run, then hold again between two scenes
            foreach (GuestThread t in held)
            {
                t.HoldForSave(false);
            }

            held.Clear();
            Thread.Sleep(5);
        }
    }

    /// <summary>How long a load waits for the held threads to reach their safe points (<see cref="WaitForUndoPoint"/>),
    /// as long as a save gives them by default.</summary>
    private const int UndoPointTimeoutMs = 3000;

    /// <summary>Test seam: called with the reason each time a load finds a held thread not yet at its safe point
    /// (<see cref="WaitForUndoPoint"/>). Null in normal runs.</summary>
    internal static Action<string>? LoadWaitingForSafePoint { get; set; }

    /// <summary>
    /// Waits, with the emulation still running, until every held thread is at its safe point, as a save does, so the
    /// running session can be kept to go back to (<see cref="TakeUndoState"/>). The hold only asks the threads to stop
    /// at their next safe point: one still running guest code (its next HLE call not reached yet, the usual case on a
    /// loaded host) made the session "cannot be kept", and a load that then failed left the game stopped. After
    /// <paramref name="timeoutMs"/> the load goes on without the way back, as for a thread that never gets there.
    /// </summary>
    private static void WaitForUndoPoint(Emulator emu, int timeoutMs)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (SafePointProblems(emu, out _) is { } why)
        {
            if (Environment.TickCount64 > until)
            {
                return;
            }

            LoadWaitingForSafePoint?.Invoke(why);
            Thread.Sleep(1);
        }
    }

    /// <summary>The first module whose running-session work a load would cut off (<see cref="ISnapshotState.CheckLoad"/>),
    /// with the reason, or null.</summary>
    internal static string? ModulesBusyForLoad(Emulator emu)
    {
        foreach (Emulator process in SystemProcesses(emu))
        {
            foreach (HleModule hleModule in process.Hle.Modules)
            {
                if (hleModule is ISnapshotState state && state.CheckLoad() is { } why)
                {
                    return $"module {hleModule.GetType().Name}: {why}";
                }
            }
        }

        return null;
    }

    /// <summary>Stops every guest thread of the session. <paramref name="tolerateStuck"/> (the rollback of a failed load):
    /// a thread that does not stop in time keeps its stop request and is left behind with a warning (it exits at its
    /// next stop check) instead of failing.</summary>
    private static void StopThreads(Emulator emu, bool tolerateStuck)
    {
        var stopping = SessionThreads(emu).Where(t => t.HostBody == null && t.IsLive).ToList();
        foreach (GuestThread? t in stopping)
        {
            t.RequestStop();
            t.WakeFromSuspend();
        }
        long deadline = Environment.TickCount64 + 5000;
        foreach (GuestThread? t in stopping)
        {
            if (t.WaitForExit((int)Math.Max(1, deadline - Environment.TickCount64)))
            {
                continue;
            }

            if (!tolerateStuck)
            {
                throw new SnapshotException($"thread '{t.Name}' did not stop (in {t.WaitingOn})");
            }

            Log.Warn($"Load state: thread '{t.Name}' did not stop (in {t.WaitingOn}); the session is restored without it");
        }
    }

    /// <summary>The running session as a load found it (taken in memory with its threads held, like a save), to go back
    /// to when the load fails after the threads were stopped: Dolphin's "undo load state".</summary>
    private sealed class UndoState(List<(string Name, byte[] Data)> sections, IReadOnlyDictionary<string, SnapshotLayout> layouts)
    {
        public List<(string Name, byte[] Data)> Sections { get; } = sections;
        public IReadOnlyDictionary<string, SnapshotLayout> Layouts { get; } = layouts;
    }

    /// <summary>Takes the <see cref="UndoState"/> (the emulation is paused and the threads are held for the load), or
    /// null when the session cannot be taken right now (the load then goes on without a way back, as before).</summary>
    private static UndoState? TakeUndoState(Emulator emu)
    {
        if (SafePointProblems(emu, out Dictionary<GuestThread, GuestThread.SafeKind>? kinds) is { } why)
        {
            Log.Warn($"Load state: the running session cannot be kept to go back to ({why})");
            return null;
        }

        try
        {
            emu.Gpu.WaitIdle();
            long t0 = Stopwatch.GetTimestamp();
            (SaveStateHeader header, List<(string Name, byte[] Data)> sections) = Capture(emu, kinds, null);
            (IReadOnlyDictionary<string, SnapshotLayout> layouts, _) = CheckSections(emu, header, sections);
            Log.Info($"Load state: running session kept in memory in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
            return new UndoState(sections, layouts);
        }
        catch (Exception e) when (e is SnapshotException or InvalidOperationException or IOException)
        {
            Log.Warn($"Load state: the running session cannot be kept to go back to ({e.Message})");
            return null;
        }
    }

    /// <summary>Restores the <see cref="UndoState"/> after a failed load; false when that failed too.</summary>
    private static bool RollBack(Emulator emu, UndoState undo, Func<Gpu.IGpuBackend?>? backendFactory)
    {
        try
        {
            StopThreads(emu, tolerateStuck: true);
            StartRestored(LoadCore(emu, undo.Sections, undo.Layouts, positional: false, backendFactory));
            Log.Info("Load state: the load failed; the session from before it was restored");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Load state: going back to the session from before the load failed too ({e.Message})");
            return false;
        }
    }

    /// <summary>
    /// Starts the restored threads. The ones saved inside a wait issue it again before the others run: in the saved
    /// session they were queued on their object, so a signal sent right after the load (a condition variable keeps
    /// none) must find them waiting. Started all at once, a running thread could signal first and the waiter then
    /// waited forever (Jak 1: main on DMAPut, GOAL on DMADone in 2 of 4 loads).
    /// </summary>
    private static void StartRestored(List<GuestThread> restored)
    {
        var waiting = restored.Where(t => t._reissuesWait).ToList();
        var running = restored.Where(t => !t._reissuesWait).ToList();
        bool order = waiting.Count > 0 && running.Count > 0;
        if (order)
        {
            foreach (GuestThread? t in running)
            {
                t.HoldForSave(true);
            }
        }

        foreach (GuestThread t in restored)
        {
            t.StartRestored();
        }

        if (!order)
        {
            return;
        }
        // the emulation is paused here: let only the waiting threads run until their waits are queued
        EmulationPause.Resume("load state: reissuing waits");
        long until = Environment.TickCount64 + 2000;
        while (waiting.Any(t => !t.WaitReissued) && Environment.TickCount64 < until)
        {
            Thread.Sleep(1);
        }

        if (waiting.FirstOrDefault(t => !t.WaitReissued) is { } late)
        {
            Log.Warn($"Load state: thread '{late.Name}' did not issue its wait again in time");
        }

        EmulationPause.Pause("load state: reissuing waits", @internal: true);
        foreach (GuestThread? t in running)
        {
            t.HoldForSave(false);
        }
    }

    /// <summary>Why a state whose header this build accepts still cannot be loaded (a [GenerateSnapshot] type whose
    /// fields or hooks changed in a way this build cannot map), or null.</summary>
    private static string? LayoutProblem(SaveStateHeader header, IReadOnlyDictionary<string, SnapshotLayout> layouts, bool positional = false)
    {
        return SnapshotLayouts.Incompatibility(layouts, positional) is { } detail
            ? Localization.Loc.Format("StateModuleChanged", SaveStateFile.ShortVersion(header.EmulatorVersion), detail)
            : null;
    }

    /// <summary>
    /// Checks, before anything stops, that every part of the state can be loaded into this build: the saved layouts map
    /// to this build's (<see cref="SnapshotLayouts.Incompatibility"/>), and every module with save state support that
    /// the game used in this session has a section (a module the saving build did not have keeps its state when the
    /// game has not used it yet). Throws <see cref="SaveStateIncompatibleException"/> naming the module.
    /// </summary>
    internal static (IReadOnlyDictionary<string, SnapshotLayout> Layouts, bool Positional) CheckSections(Emulator emu, SaveStateHeader header, List<(string Name, byte[] Data)> sections)
    {
        string version = SaveStateFile.ShortVersion(header.EmulatorVersion);
        bool positional = SaveStateFile.IsLegacy(header);
        Dictionary<string, SnapshotLayout> layouts;
        if (positional)
        {
            layouts = SnapshotLayouts.Legacy(header.Schema)!;
        }
        else
        {
            byte[]? table = sections.FirstOrDefault(s => s.Name == LayoutsSection).Data;
            if (table == null)
            {
                throw new SaveStateIncompatibleException(Localization.Loc.Format("StateModuleChanged", version, "the state has no layout table"));
            }

            layouts = SnapshotLayouts.Read(new SnapshotReader(table));
        }

        if (LayoutProblem(header, layouts, positional) is { } why)
        {
            throw new SaveStateIncompatibleException(why);
        }

        var names = new HashSet<string>(sections.Select(s => s.Name), StringComparer.Ordinal);
        List<Emulator> processes = SavedProcesses(emu, sections);
        for (int index = 0; index < processes.Count; index++)
        {
            Emulator process = processes[index];
            string prefix = ProcessPrefix(index);
            var used = new HashSet<string>();
            foreach (HleModule hleModule in process.Hle.Modules)
            {
                if (hleModule is not ISnapshotState)
                {
                    continue;
                }

                string key = ModuleKey(hleModule, used);
                if (!names.Contains(prefix + ModulePrefix + key) && process.Hle.WasUsed(hleModule))
                {
                    throw new SaveStateIncompatibleException(Localization.Loc.Format("StateModuleChanged", version,
                        $"module {key} has no state in it and was used in this session (restart the game, then load the state)"));
                }
            }
        }

        return (layouts, positional);
    }

    /// <summary>
    /// The running processes in the order of the state's process sections, after checking that they are the ones the
    /// state was saved with: the same process and application ids and titles (the shell and the applications it had
    /// opened; open the same ones, then load), each suspended or not and on screen or not as when it was saved. A state
    /// without a process list holds the first process only. Throws <see cref="SnapshotException"/> naming the difference.
    /// </summary>
    internal static List<Emulator> SavedProcesses(Emulator emu, List<(string Name, byte[] Data)> sections)
    {
        List<Emulator> running = SystemProcesses(emu);
        byte[]? list = sections.FirstOrDefault(s => s.Name == ProcessesSection).Data;
        if (list == null)
        {
            if (running.Count > 1)
            {
                throw new SnapshotException($"the state holds {emu.Describe()} alone and {running.Count - 1} more processes run here; close them, then load the state");
            }

            return running;
        }

        var r = new SnapshotReader(list);
        int count = r.ReadInt32();
        var saved = new List<(int Pid, int AppId, string TitleId, bool Suspended, bool Foreground)>(count);
        for (int i = 0; i < count; i++)
        {
            saved.Add((r.ReadInt32(), r.ReadInt32(), r.ReadStringNotNull(), r.ReadBool(), r.ReadBool()));
        }

        if (count != running.Count || saved.Any(p => !running.Any(e => e.Pid == p.Pid)))
        {
            string savedNames = string.Join(", ", saved.Select(p => $"{p.TitleId} (pid 0x{p.Pid:X})"));
            string runningNames = string.Join(", ", running.Select(e => e.Describe()));
            throw new SnapshotException($"the state was saved with {savedNames} running, here {runningNames} run; open the same applications, then load the state");
        }

        var ordered = new List<Emulator>(count);
        foreach ((int pid, int appId, string titleId, bool suspended, bool foreground) in saved)
        {
            Emulator process = running.First(e => e.Pid == pid);
            if (process.AppId != appId || !string.Equals(process.TitleId, titleId, StringComparison.OrdinalIgnoreCase))
            {
                throw new SnapshotException($"process 0x{pid:X} was {titleId} (application 0x{appId:X}) when the state was saved, here it is {process.Describe()} (application 0x{process.AppId:X})");
            }

            if (process.Suspended != suspended)
            {
                string what = suspended ? "suspended" : "running";
                throw new SnapshotException($"{process.Describe()} was {what} when the state was saved; switch to the same screen, then load the state");
            }

            if (foreground != ReferenceEquals(emu.Vita.Foreground, process))
            {
                string what = foreground ? "on screen" : "in the background";
                throw new SnapshotException($"{process.Describe()} was {what} when the state was saved; switch to the same screen, then load the state");
            }

            ordered.Add(process);
        }

        if (!ReferenceEquals(ordered[0], emu))
        {
            throw new SnapshotException("the state's first process is not the system's first process");
        }

        return ordered;
    }

    private static List<GuestThread> LoadCore(Emulator emu, List<(string Name, byte[] Data)> list, IReadOnlyDictionary<string, SnapshotLayout> layouts,
        bool positional, Func<Gpu.IGpuBackend?>? backendFactory)
    {
        emu.Gpu.WaitIdle();
        long t0 = Stopwatch.GetTimestamp();
        if (backendFactory != null)
        {
            emu.Gpu.ReplaceBackend(backendFactory, sameTitle: true);
        }

        Log.Info($"Load state: GPU backend replaced in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
        // the kernel process's translations too: the kernel code a plugin's hook patched is the state's again
        List<Emulator> processes = SystemProcesses(emu);
        if (emu.Vita.KernelProcess is { } kernelProcess)
        {
            processes.Add(kernelProcess);
        }

        if (!s_keepCodeOnLoad)
        {
            foreach (Emulator process in processes)
            {
                process.Code.Clear();
            }
        }

        try
        {
            List<GuestThread> restored = LoadSections(emu, list, layouts, positional);
            foreach (Emulator process in processes)
            {
                RevalidateCode(process.Code);
            }

            return restored;
        }
        catch
        {
            foreach (Emulator process in processes)
            {
                process.Code.Clear(); // a half-restored memory: no translation can be trusted
            }

            throw;
        }
    }

    /// <summary><see cref="EnvVars.LoadKeepCode"/>: keep the translations of unchanged guest code across a load.</summary>
    private static readonly bool s_keepCodeOnLoad = Environment.GetEnvironmentVariable(EnvVars.LoadKeepCode) == "1";

    /// <summary>After the state's memory and the HLE routines are in place: with <see cref="s_keepCodeOnLoad"/>, drops only
    /// the blocks whose guest code changed (the whole cache was dropped before the load otherwise).</summary>
    internal static void RevalidateCode(Cpu.BlockCache code)
    {
        if (!s_keepCodeOnLoad)
        {
            return;
        }

        long t0 = Stopwatch.GetTimestamp();
        (int kept, int dropped) = code.Revalidate();
        Log.Info($"Load state: kept {kept} translated blocks, dropped {dropped} whose code changed ({Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms)");
    }

    private static List<GuestThread> LoadSections(Emulator emu, List<(string Name, byte[] Data)> list,
        IReadOnlyDictionary<string, SnapshotLayout> layouts, bool positional)
    {
        AudioDiagnostics.ProcessEnded();

        List<Emulator> processes = SavedProcesses(emu, list);
        var sections = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // blocks by address and the process whose address space holds them (0: the first process's space, or CDRAM and
        // kernel memory, in every space)
        Dictionary<(uint Address, int SpacePid), GuestMemory.Block>? blocks = null;
        int[]? spacePids = null;
        foreach ((string? name, byte[]? data) in list)
        {
            if (name == SpacesSection)
            {
                var spaces = new SnapshotReader(data);
                spacePids = new int[spaces.ReadInt32()];
                for (int i = 0; i < spacePids.Length; i++)
                {
                    int spacePid = spaces.ReadInt32();
                    spacePids[i] = spacePid == emu.Pid ? 0 : spacePid;
                }
            }
            else if (name == BlocksSection)
            {
                blocks = RestoreBlocks(emu, GuestMemory.ReadBlockTable(new SnapshotReader(data)), spacePids);
            }
            else if (name.StartsWith(MemPrefix, StringComparison.Ordinal) || name.StartsWith(WordPagesMemPrefix, StringComparison.Ordinal))
            {
                bool wordPages = name.StartsWith(WordPagesMemPrefix, StringComparison.Ordinal);
                string key = name[(wordPages ? WordPagesMemPrefix : MemPrefix).Length..];
                int at = key.IndexOf('@', StringComparison.Ordinal);
                uint addr = Convert.ToUInt32(at < 0 ? key : key[..at], 16);
                int spacePid = at < 0 ? 0 : Convert.ToInt32(key[(at + 1)..], 16);
                if (blocks == null || !blocks.TryGetValue((addr, spacePid), out GuestMemory.Block? b))
                {
                    throw new SnapshotException($"memory block 0x{addr:X8} is missing from the block table");
                }

                emu.Memory.RestoreContents(b, data, wordPages);
            }
            else
            {
                sections[name] = data;
            }
        }
        if (blocks == null)
        {
            throw new SnapshotException("the save state has no memory");
        }

        byte[] Need(string n) => sections.GetValueOrDefault(n) ?? throw new SnapshotException($"the save state has no {n} section");

        var sys = new SnapshotReader(Need(SysSection));
        sys.ReadString();
        long clockUs = sys.ReadInt64();
        int pid = sys.ReadInt32(), appId = sys.ReadInt32();
        if (pid != emu.Pid || appId != emu.AppId)
        {
            throw new SnapshotException($"the process ids differ (saved 0x{pid:X}/0x{appId:X}, here 0x{emu.Pid:X}/0x{emu.AppId:X})");
        }

        int mainUid = sys.ReadInt32();
        (sys.ReadBytes() ?? []).CopyTo(emu.Vita.AppMgrState, 0);
        emu.BgmPriority = Math.Max(0, sys.ReadInt32());

        // the syscon chip: a state of a build before it keeps the chip as it is (an unplugged unit, empty scratchpad)
        if (sections.TryGetValue(SysconSection, out byte[]? sysconData))
        {
            var sysconReader = new SnapshotReader(sysconData) { Layouts = layouts, Positional = positional };
            sysconReader.ReadInto(emu.Vita.Syscon);
            sysconReader.ReadInto(emu.Vita.PowerState);
            sysconReader.ReadInto(emu.Vita.RtcClocks);
        }

        // the other processes' own records: the ids were checked (SavedProcesses), the main thread follows the objects
        int[] mainThreads = new int[processes.Count];
        mainThreads[0] = mainUid;
        for (int index = 1; index < processes.Count; index++)
        {
            Emulator process = processes[index];
            var processSystem = new SnapshotReader(Need(ProcessPrefix(index) + SysSection));
            processSystem.ReadString();
            int processPid = processSystem.ReadInt32(), processAppId = processSystem.ReadInt32();
            if (processPid != process.Pid || processAppId != process.AppId)
            {
                throw new SnapshotException($"the process ids of {process.Describe()} differ (saved 0x{processPid:X}/0x{processAppId:X})");
            }

            mainThreads[index] = processSystem.ReadInt32();
            process.BgmPriority = Math.Max(0, processSystem.ReadInt32());
        }

        for (int index = 0; index < processes.Count; index++)
        {
            Emulator process = processes[index];
            process.Hle.LoadState(new SnapshotReader(Need(ProcessPrefix(index) + FunctionsSection)));
            // this build's guest-code routines and SVCs, not the saving build's (Hle/HleGuestCode)
            using (Kernel.ProcessScope.Enter(process))
            {
                int rebound = process.Linker.RebindHleStubs(process.Hle.ReplaceGuestCode());
                Log.Info($"Load state: {rebound} HLE import stubs of {process.Describe()} bound to this build's routines");
            }
        }

        List<GuestThread> restored = KernelSnapshot.Load(emu, new SnapshotReader(Need(ObjectsSection)));
        for (int index = 0; index < processes.Count; index++)
        {
            processes[index].Kernel.MainThread = emu.Kernel.Objects.Get<GuestThread>(mainThreads[index]);
        }

        GuestClock.RestoreMicroseconds(clockUs);
        for (int index = 0; index < processes.Count; index++)
        {
            if (index == 0)
            {
                LoadModules(processes[index], "", sections, layouts, positional);
                continue;
            }

            using Kernel.ProcessScope.Scope processScope = Kernel.ProcessScope.Enter(processes[index]);
            LoadModules(processes[index], ProcessPrefix(index), sections, layouts, positional);
        }

        LoadKernelModules(emu, sections, layouts, positional);
        RearmTimers(emu, processes);
        Scheduler.Reset();
        return restored;
    }

    /// <summary>The guest-time sources of the loaded state armed again (<see cref="ISnapshotTimers"/>): first every
    /// module's arms of the running session dropped, then every module's loaded schedule armed.</summary>
    private static void RearmTimers(Emulator emu, List<Emulator> processes)
    {
        var owners = new List<(Emulator Process, ISnapshotTimers Timers)>();
        IEnumerable<Emulator> all = emu.Vita.KernelProcess is { } kernelProcess ? processes.Append(kernelProcess) : processes;
        foreach (Emulator process in all.Distinct())
        {
            foreach (HleModule hleModule in process.Hle.Modules)
            {
                if (hleModule is ISnapshotTimers timers)
                {
                    owners.Add((process, timers));
                }
            }
        }

        foreach ((Emulator process, ISnapshotTimers timers) in owners)
        {
            using Kernel.ProcessScope.Scope scope = Kernel.ProcessScope.Enter(process);
            timers.ClearTimers();
        }

        foreach ((Emulator process, ISnapshotTimers timers) in owners)
        {
            using Kernel.ProcessScope.Scope scope = Kernel.ProcessScope.Enter(process);
            timers.RearmTimers();
        }
    }

    /// <summary>
    /// Makes the block table the saved one, each private block in the address space of the process that held it
    /// (<paramref name="spacePids"/>, by block: 0 for the first process's space and for CDRAM and kernel memory; null in
    /// a state of one process). Returns the blocks by address and that process.
    /// </summary>
    private static Dictionary<(uint Address, int SpacePid), GuestMemory.Block> RestoreBlocks(Emulator emu, List<GuestMemory.Block> table, int[]? spacePids)
    {
        if (spacePids == null)
        {
            return emu.Memory.RestoreBlocks(table).ToDictionary(b => (b.Address, 0));
        }

        if (spacePids.Length != table.Count)
        {
            throw new SnapshotException($"the memory table names the space of {spacePids.Length} blocks, it has {table.Count}");
        }

        var spaceOf = new Dictionary<int, AddressSpace>();
        foreach (Emulator process in emu.Vita.ProcessesAndKernel)
        {
            spaceOf.TryAdd(process.Pid, process.Space);
        }

        var spaces = new AddressSpace?[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            if (spacePids[i] == 0)
            {
                continue;
            }

            spaces[i] = spaceOf.GetValueOrDefault(spacePids[i])
                ?? throw new SnapshotException($"memory block {table[i].Name} belongs to process 0x{spacePids[i]:X}, which does not run here");
        }

        List<GuestMemory.Block> restored = emu.Memory.RestoreBlocks(table, spaces);
        var blocks = new Dictionary<(uint Address, int SpacePid), GuestMemory.Block>(restored.Count);
        for (int i = 0; i < restored.Count; i++)
        {
            blocks[(restored[i].Address, spacePids[i])] = restored[i];
        }

        return blocks;
    }

    /// <summary>The states of the HLE modules of <paramref name="process"/> with save state support, from the sections
    /// named with <paramref name="prefix"/>.</summary>
    private static void LoadModules(Emulator process, string prefix, Dictionary<string, byte[]> sections, IReadOnlyDictionary<string, SnapshotLayout> layouts, bool positional)
    {
        var used = new HashSet<string>();
        foreach (HleModule hleModule in process.Hle.Modules)
        {
            if (hleModule is not ISnapshotState s)
            {
                continue;
            }

            string key = ModuleKey(hleModule, used);
            if (!sections.TryGetValue(prefix + ModulePrefix + key, out byte[]? data))
            {
                if (!process.Hle.WasUsed(hleModule))
                {
                    continue; // a module the saving build did not have, unused here: it keeps its initial state
                }

                throw new SnapshotException($"module {key} has no state in the save state");
            }

            var mr = new SnapshotReader(data) { Objects = process.Kernel.Objects, Layouts = layouts, Positional = positional };
            try
            {
                s.LoadState(mr);
            }
            catch (SnapshotException e)
            {
                throw new SnapshotException($"module {key}: {e.Message}");
            }

            if (!mr.AtEnd)
            {
                throw new SnapshotException($"module {key}: {mr.Remaining} bytes of its state were not read (layout mismatch)");
            }
        }
    }

    /// <summary>The kernel process's module states (a state of a build before it has none: the kernel process keeps
    /// what it has).</summary>
    private static void LoadKernelModules(Emulator emu, Dictionary<string, byte[]> sections, IReadOnlyDictionary<string, SnapshotLayout> layouts, bool positional)
    {
        if (emu.Vita.KernelProcess is not { } kernelProcess)
        {
            return;
        }

        using Kernel.ProcessScope.Scope scope = Kernel.ProcessScope.Enter(kernelProcess);
        var used = new HashSet<string>();
        foreach (HleModule hleModule in kernelProcess.Hle.Modules)
        {
            if (hleModule is not ISnapshotState s)
            {
                continue;
            }

            string key = ModuleKey(hleModule, used);
            if (!sections.TryGetValue(KernelModulePrefix + key, out byte[]? data))
            {
                continue;
            }

            var mr = new SnapshotReader(data) { Objects = kernelProcess.Kernel.Objects, Layouts = layouts, Positional = positional };
            try
            {
                s.LoadState(mr);
            }
            catch (SnapshotException e)
            {
                throw new SnapshotException($"kernel module {key}: {e.Message}");
            }

            if (!mr.AtEnd)
            {
                throw new SnapshotException($"kernel module {key}: {mr.Remaining} bytes of its state were not read (layout mismatch)");
            }
        }
    }
}
