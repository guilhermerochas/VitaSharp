using System.Diagnostics;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.SaveState;

/// <summary>
/// Saves and loads the whole emulated system of a game run directly (one process): guest memory, CPU state of every
/// thread, kernel objects, the HLE function table and the state of every HLE module the game used. GPU caches are not
/// saved: the load gives the GPU a new backend that rebuilds them from guest memory, as after a LoadExec.
/// See docs/notes/save-states.md.
/// </summary>
public static class SaveStates
{
    const string SysSection = "system", FunctionsSection = "hle.functions", ObjectsSection = "kernel.objects",
        BlocksSection = "memory.blocks", MemPrefix = "mem:", ModulePrefix = "hle:";

    /// <summary>Raised after a state was loaded (front-ends refresh what they show of the game).</summary>
    public static event Action<Emulator>? Loaded;

    /// <summary>Folder of a title's states: &lt;data&gt;/savestates/&lt;TITLEID&gt;.</summary>
    public static string Folder(string dataRoot, string titleId) => Path.Combine(dataRoot, "savestates", titleId);

    /// <summary>File of slot <paramref name="slot"/> (0 = the quick save slot).</summary>
    public static string SlotPath(string dataRoot, string titleId, int slot) =>
        Path.Combine(Folder(dataRoot, titleId), slot == 0 ? "quick.vss" : $"slot{slot}.vss");

    static void CheckSystem(Emulator emu)
    {
        if (emu.Parent != null || emu.Vita.Processes.Count != 1)
            throw new SnapshotException("save states support a game run directly (one process); running under the Vita shell is not supported yet");
        if (emu.MainModule == null || !emu.Running) throw new SnapshotException("no game is running");
        if (emu.Execing || emu.ExecRequest != null) throw new SnapshotException("the game is restarting (LoadExec)");
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
                if (EmulationPause.IsPaused) EmulationPause.Resume("save state");
                string? why = HoldBetweenScenes(emu, held, sw, timeoutMs);
                if (why != null) throw new SnapshotException($"not at a safe point: {why}");
                EmulationPause.Pause("save state", @internal: true);
                why = SafePointProblems(emu, out var kinds);
                if (why == null)
                {
                    emu.Gpu.WaitIdle();
                    var seq = kinds.Keys.ToDictionary(t => t, t => t.HleSequence);
                    SaveStateHeader? header = null;
                    List<(string Name, byte[] Data)>? sections = null;
                    try { (header, sections) = Capture(emu, kinds, thumbnailPng); }
                    catch (SnapshotRetryException e) { why = e.Message; }
                    if (header != null)
                    {
                        why = SafePointProblems(emu, out var after);
                        if (why == null && seq.Any(p => p.Key.HleSequence != p.Value || after.GetValueOrDefault(p.Key) != kinds[p.Key]))
                            why = "a thread woke up while the state was taken";
                        if (why == null)
                        {
                            long pausedMs = sw.ElapsedMilliseconds;
                            // the game goes on while the file is compressed and written
                            if (!wasPaused) EmulationPause.Resume("save state");
                            foreach (var t in held) t.HoldForSave(false);
                            held.Clear();
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                            using (var f = File.Create(tmp))
                            using (var w = new SaveStateWriter(f, header))
                                foreach (var (name, data) in sections!) w.Section(name, data);
                            File.Move(tmp, path, overwrite: true);
                            Log.Info($"Save state: {path} written in {sw.ElapsedMilliseconds} ms ({pausedMs} ms with the game stopped; {new FileInfo(path).Length / 1024} KB, {kinds.Count} threads)");
                            return header;
                        }
                    }
                }
                if (sw.ElapsedMilliseconds > timeoutMs) throw new SnapshotException($"not at a safe point: {why}");
                Log.Info($"Save state: not at a safe point ({why}), retrying");
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
            // always back to how the player left it, also when the state could not be taken (a hung game): the
            // internal pause never stays
            if (wasPaused) EmulationPause.Pause("save state"); else EmulationPause.Resume("save state");
            foreach (var t in held) t.HoldForSave(false);
        }
    }

    /// <summary>
    /// Holds the threads between two GPU scenes: the hold starts when no scene is open (a thread that opened one may
    /// be waiting for held threads to finish it), and is released and tried again when a scene stays open. Returns
    /// null once every thread is at a safe point (with the emulation still running), else why not.
    /// </summary>
    static string? HoldBetweenScenes(Emulator emu, List<GuestThread> held, Stopwatch sw, int timeoutMs)
    {
        string? why = null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (held.Count == 0)
            {
                for (int i = 0; i < 400 && emu.OpenSceneThread != null; i++) Thread.Sleep(1);
                held.AddRange(Hold(emu));
            }
            why = WaitForSafePoint(emu, 300, out _);
            if (why == null) return null;
            if (emu.OpenSceneThread == null && !why.Contains("GPU scene", StringComparison.Ordinal)) continue;
            // a scene stays open: let every thread run, then hold again between two scenes
            foreach (var t in held) t.HoldForSave(false);
            held.Clear();
            Thread.Sleep(5);
        }
        return why ?? "timed out";
    }

    /// <summary>Holds every guest thread of <paramref name="emu"/> at its next safe point (module service threads run on).</summary>
    static List<GuestThread> Hold(Emulator emu)
    {
        var held = emu.Kernel.Threads.Where(t => t.HostBody == null).ToList();
        foreach (var t in held) t.HoldForSave(true);
        return held;
    }

    /// <summary>Refuses a save when a module the game used holds state that is not saved yet.</summary>
    static void CheckModules(Emulator emu)
    {
        var problems = new List<string>();
        foreach (var m in emu.Hle.Modules)
        {
            string name = m.GetType().Name;
            if (m is ISnapshotState s)
            {
                if (s.CheckSnapshot() is { } problem) problems.Add($"module {name}: {problem}");
            }
            else if (SnapshotSchema.IsStateful(m.GetType()) && emu.Hle.WasUsed(m))
                problems.Add($"module {name}: its state is not supported by save states yet");
        }
        if (problems.Count > 0) throw new SnapshotException(string.Join("; ", problems));
    }

    static string? WaitForSafePoint(Emulator emu, int ms, out Dictionary<GuestThread, GuestThread.SafeKind> kinds)
    {
        var until = Environment.TickCount64 + ms;
        while (true)
        {
            string? why = SafePointProblems(emu, out kinds);
            if (why == null || Environment.TickCount64 > until) return why;
            Thread.Sleep(1);
        }
    }

    static string? SafePointProblems(Emulator emu, out Dictionary<GuestThread, GuestThread.SafeKind> kinds)
    {
        kinds = new();
        if (emu.OpenSceneThread is { } scene) return $"thread '{scene.Name}' is finishing a GPU scene";
        foreach (var t in emu.Kernel.Threads)
        {
            if (t.SafePointProblem(out var k) is { } why) return why;
            kinds[t] = k;
        }
        return null;
    }

    /// <summary>Takes the whole state into memory (the emulation is stopped); the file is written afterwards.</summary>
    static (SaveStateHeader, List<(string, byte[])>) Capture(Emulator emu, Dictionary<GuestThread, GuestThread.SafeKind> kinds, byte[]? thumbnailPng)
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
        sys.Write(emu.Vita.BgmPortOwner);
        list.Add((SysSection, sys.Data.ToArray()));

        var fn = new SnapshotWriter();
        emu.Hle.SaveState(fn);
        list.Add((FunctionsSection, fn.Data.ToArray()));
        list.Add((ObjectsSection, KernelSnapshot.Save(emu, kinds).Data.ToArray()));

        var used = new HashSet<string>();
        foreach (var m in emu.Hle.Modules)
        {
            if (m is not ISnapshotState s) continue;
            string name = ModuleKey(m, used);
            var mw = new SnapshotWriter();
            s.SaveState(mw);
            list.Add((ModulePrefix + name, mw.Data.ToArray()));
        }

        long t0 = Stopwatch.GetTimestamp(), raw = 0;
        var blocks = emu.Memory.SnapshotBlocks();
        var bt = new SnapshotWriter();
        GuestMemory.WriteBlockTable(bt, blocks);
        list.Add((BlocksSection, bt.Data.ToArray()));
        foreach (var b in blocks)
        {
            var data = emu.Memory.CopyContents(b);
            list.Add((MemPrefix + b.Address.ToString("X8"), data));
            raw += data.Length;
        }
        Log.Info($"Save state: memory {blocks.Count} blocks, {raw >> 20} MB of non-zero pages copied in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
        return (header, list);
    }

    /// <summary>A module's section name: its type name (numbered when a type is registered more than once).</summary>
    static string ModuleKey(HleModule m, HashSet<string> used)
    {
        string name = m.GetType().Name;
        for (int i = 2; !used.Add(name); i++) name = $"{m.GetType().Name}#{i}";
        return name;
    }

    // ------------------------------------------------------------------ load

    /// <summary>Reads the header of a state file and says whether it can be loaded into <paramref name="emu"/>.</summary>
    public static string? CheckFile(Emulator emu, string path)
    {
        try { return SaveStateFile.Incompatibility(SaveStateFile.ReadHeader(path), emu.TitleId); }
        catch (SnapshotException e) { return e.Message; }
        catch (IOException e) { return e.Message; }
    }

    /// <summary>
    /// Loads the state in <paramref name="path"/> into the running game. Every guest thread is stopped and replaced by
    /// the saved ones; <paramref name="backendFactory"/> (when given) makes the GPU backend that replaces the current
    /// one, whose caches belong to the old state. Incompatible states are refused before anything changes
    /// (<see cref="SnapshotException"/>); a failure after the threads were stopped leaves the game unusable (the
    /// message says so).
    /// </summary>
    public static void Load(Emulator emu, string path, Func<Gpu.IGpuBackend?>? backendFactory = null)
    {
        CheckSystem(emu);
        var sw = Stopwatch.StartNew();
        SaveStateHeader header;
        var sections = new List<(string Name, byte[] Data)>();
        using (var file = File.OpenRead(path))
        using (var reader = new SaveStateReader(file))
        {
            header = reader.Header;
            if (SaveStateFile.Incompatibility(header, emu.TitleId) is { } why) throw new SaveStateIncompatibleException(why);
            // decompressed while the game still runs
            while (reader.Next(out var name, out _)) sections.Add((name, reader.ReadAll()));
        }
        long readMs = sw.ElapsedMilliseconds;
        foreach (var m in emu.Hle.Modules)
            if (m is not ISnapshotState && SnapshotSchema.IsStateful(m.GetType()) && emu.Hle.WasUsed(m))
                throw new SnapshotException($"module {m.GetType().Name} was used in this session and its state cannot be replaced; restart the game, then load the state");

        bool wasPaused = EmulationPause.IsPaused;
        var held = new List<GuestThread>();
        try
        {
            // module service threads finish what they are doing with the emulation running, then everything stops
            if (wasPaused) EmulationPause.Resume("load state");
            HoldForLoad(emu, held);
            EmulationPause.Pause("load state", @internal: true);
            long t0 = sw.ElapsedMilliseconds;
            StopThreads(emu);
            Log.Info($"Load state: threads stopped in {sw.ElapsedMilliseconds - t0} ms");
            try { StartRestored(LoadCore(emu, sections, backendFactory)); }
            catch (Exception e) when (e is not SnapshotException)
            {
                throw new SnapshotException($"loading the state failed ({e.Message}); restart the game");
            }
            catch (SnapshotException e)
            {
                throw new SnapshotException($"{e.Message}; restart the game");
            }
            Log.Info($"Save state: {path} loaded in {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds - readMs} ms with the game stopped; saved {header.CreatedUtc.ToLocalTime():g}, VitaSharp {header.EmulatorVersion})");
        }
        finally
        {
            foreach (var t in held) t.HoldForSave(false);
            if (!wasPaused) EmulationPause.Resume("load state"); else EmulationPause.Pause("load state");
        }
        Loaded?.Invoke(emu);
    }

    /// <summary>
    /// Holds the guest threads for a load until no GPU scene is open and the module service threads (kept across the
    /// load) are idle. Like a save, the hold starts between two scenes and is released and taken again when a scene
    /// stays open: the thread that opened it may wait for a held thread to finish it (Tearaway's SandPit, whose scenes
    /// are fed by worker threads, kept one open for the whole 3 s and every load at that point was refused).
    /// </summary>
    static void HoldForLoad(Emulator emu, List<GuestThread> held)
    {
        var until = Environment.TickCount64 + 3000;
        while (true)
        {
            if (held.Count == 0)
            {
                for (int i = 0; i < 400 && emu.OpenSceneThread != null; i++) Thread.Sleep(1);
                held.AddRange(Hold(emu));
            }
            string? busy = null;
            var settle = Environment.TickCount64 + 300;
            do
            {
                busy = null;
                foreach (var t in emu.Kernel.Threads)
                    if (t.HostBody != null && t.IsLive && t.SafePointProblem(out _) is { } p) busy = p;
                if (emu.OpenSceneThread is { } scene) busy = $"thread '{scene.Name}' is finishing a GPU scene";
                if (busy == null) return;
                Thread.Sleep(1);
            } while (Environment.TickCount64 < settle);
            if (Environment.TickCount64 > until) throw new SnapshotException($"not at a safe point: {busy}");
            if (emu.OpenSceneThread == null) continue;
            // a scene stays open: let every thread run, then hold again between two scenes
            foreach (var t in held) t.HoldForSave(false);
            held.Clear();
            Thread.Sleep(5);
        }
    }

    static void StopThreads(Emulator emu)
    {
        var stopping = emu.Kernel.Threads.Where(t => t.HostBody == null && t.IsLive).ToList();
        foreach (var t in stopping) { t.RequestStop(); t.WakeFromSuspend(); }
        var deadline = Environment.TickCount64 + 5000;
        foreach (var t in stopping)
            if (!t.WaitForExit((int)Math.Max(1, deadline - Environment.TickCount64)))
                throw new SnapshotException($"thread '{t.Name}' did not stop (in {t.WaitingOn}); restart the game");
    }

    /// <summary>
    /// Starts the restored threads. The ones saved inside a wait issue it again before the others run: in the saved
    /// session they were queued on their object, so a signal sent right after the load (a condition variable keeps
    /// none) must find them waiting. Started all at once, a running thread could signal first and the waiter then
    /// waited forever (Jak 1: main on DMAPut, GOAL on DMADone in 2 of 4 loads).
    /// </summary>
    static void StartRestored(List<GuestThread> restored)
    {
        var waiting = restored.Where(t => t.ReissuesWait).ToList();
        var running = restored.Where(t => !t.ReissuesWait).ToList();
        bool order = waiting.Count > 0 && running.Count > 0;
        if (order) foreach (var t in running) t.HoldForSave(true);
        foreach (var t in restored) t.StartRestored();
        if (!order) return;
        // the emulation is paused here: let only the waiting threads run until their waits are queued
        EmulationPause.Resume("load state: reissuing waits");
        var until = Environment.TickCount64 + 2000;
        while (waiting.Any(t => !t.WaitReissued) && Environment.TickCount64 < until) Thread.Sleep(1);
        if (waiting.FirstOrDefault(t => !t.WaitReissued) is { } late)
            Log.Warn($"Load state: thread '{late.Name}' did not issue its wait again in time");
        EmulationPause.Pause("load state: reissuing waits", @internal: true);
        foreach (var t in running) t.HoldForSave(false);
    }

    static List<GuestThread> LoadCore(Emulator emu, List<(string Name, byte[] Data)> list, Func<Gpu.IGpuBackend?>? backendFactory)
    {
        emu.Gpu.WaitIdle();
        var t0 = Stopwatch.GetTimestamp();
        if (backendFactory != null) emu.Gpu.ReplaceBackend(backendFactory);
        Log.Info($"Load state: GPU backend replaced in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms");
        emu.Code.Clear();
        AudioDiagnostics.ProcessEnded();

        var sections = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Dictionary<uint, GuestMemory.Block>? blocks = null;
        foreach (var (name, data) in list)
        {
            if (name == BlocksSection)
                blocks = emu.Memory.RestoreBlocks(GuestMemory.ReadBlockTable(new SnapshotReader(data))).ToDictionary(b => b.Address);
            else if (name.StartsWith(MemPrefix, StringComparison.Ordinal))
            {
                uint addr = Convert.ToUInt32(name[MemPrefix.Length..], 16);
                if (blocks == null || !blocks.TryGetValue(addr, out var b)) throw new SnapshotException($"memory block 0x{addr:X8} is missing from the block table");
                emu.Memory.RestoreContents(b, data);
            }
            else sections[name] = data;
        }
        if (blocks == null) throw new SnapshotException("the save state has no memory");
        byte[] Need(string n) => sections.GetValueOrDefault(n) ?? throw new SnapshotException($"the save state has no {n} section");

        var sys = new SnapshotReader(Need(SysSection));
        sys.ReadString();
        long clockUs = sys.ReadInt64();
        int pid = sys.ReadInt32(), appId = sys.ReadInt32();
        if (pid != emu.Pid || appId != emu.AppId) throw new SnapshotException($"the process ids differ (saved 0x{pid:X}/0x{appId:X}, here 0x{emu.Pid:X}/0x{emu.AppId:X})");
        int mainUid = sys.ReadInt32();
        (sys.ReadBytes() ?? []).CopyTo(emu.Vita.AppMgrState, 0);
        emu.Vita.BgmPortOwner = sys.ReadInt32();

        emu.Hle.LoadState(new SnapshotReader(Need(FunctionsSection)));
        var restored = KernelSnapshot.Load(emu, new SnapshotReader(Need(ObjectsSection)));
        emu.Kernel.MainThread = emu.Kernel.Objects.Get<GuestThread>(mainUid);
        GuestClock.RestoreMicroseconds(clockUs);

        var used = new HashSet<string>();
        foreach (var m in emu.Hle.Modules)
        {
            if (m is not ISnapshotState s) continue;
            string key = ModuleKey(m, used);
            if (!sections.TryGetValue(ModulePrefix + key, out var data)) throw new SnapshotException($"module {key} has no state in the save state");
            var mr = new SnapshotReader(data) { Objects = emu.Kernel.Objects };
            try { s.LoadState(mr); }
            catch (SnapshotException e) { throw new SnapshotException($"module {key}: {e.Message}"); }
            if (!mr.AtEnd) throw new SnapshotException($"module {key}: {mr.Remaining} bytes of its state were not read (layout mismatch)");
        }
        Scheduler.Reset();
        return restored;
    }
}
