using System.Reflection;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Io;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Package;
using VitaSharp.Core.Util;

namespace VitaSharp.Core;

public sealed class EmulatorOptions
{
    /// <summary>Root of the virtual Vita filesystem (contains ux0/, ur0/, ...).</summary>
    public required string FsRoot { get; init; }
    public bool EnableJit { get; init; } = true;
    /// <summary>CPU backend: "native", "il", "interp" or null/"auto" (then VITASHARP_CPU, else the best one the host supports).</summary>
    public string? CpuBackend { get; init; }
    public bool TraceHle { get; init; }
    public string Language { get; init; } = "en";
    /// <summary>Directory with per-title code patch files (TITLEID.txt); null = built-in patches only.</summary>
    public string? PatchDir { get; init; }
    /// <summary>The emulated PSN account (signed in by default, see <see cref="PsnAccount"/>).</summary>
    public PsnAccount Psn { get; init; } = PsnAccount.Default;
    /// <summary>Folder of the local PSN service data (TUS / Cross-Save, rankings: <see cref="Hle.Np.LocalPsn"/>); null =
    /// &lt;DataRoot&gt;/np, next to <see cref="FsRoot"/>.</summary>
    public string? NpRoot { get; init; }
    /// <summary>Sysmodule libraries ("libsas", "libatrac", ...) to load LLE from the installed firmware
    /// (vs0:sys/external) even though VitaSharp has an HLE implementation of them. Null: VITASHARP_LLE_MODULES.</summary>
    public IReadOnlyCollection<string>? LleModules { get; init; }
    /// <summary>Sysmodule libraries to keep on HLE even when the game or the firmware provides the module
    /// ("libult", "libSceJson", or the short names "ult", "json", ...). Null: VITASHARP_HLE_MODULES.</summary>
    public IReadOnlyCollection<string>? HleModules { get; init; }
    /// <summary>An existing guest address space to run in (there is one per process: unit tests that boot an
    /// emulator share the test fixture's). Null: the emulator creates it.</summary>
    public GuestMemory? Memory { get; init; }
}

/// <summary>
/// The platform-independent emulator: memory, CPU translation cache, kernel, HLE modules and loader.
/// Front-ends (desktop now, Android later) supply the host services in <see cref="Host"/>.
/// </summary>
public sealed class Emulator : IDisposable
{
    public EmulatorOptions Options { get; }
    public GuestMemory Memory { get; }
    public BlockCache Code { get; } = new();
    public Kernel.Kernel Kernel { get; private set; }
    public HleRegistry Hle { get; private set; }
    public Linker Linker { get; private set; }
    public Vfs Vfs { get; }
    public Host.HostServices Host { get; } = new();
    public Gpu.GpuQueue Gpu { get; } = new();
    public bool TraceHle { get; set; }
    /// <summary>Debug: trace HLE calls only while the presented frame count is in [From, To), skipping noisy functions
    /// whose names start with one of <see cref="TraceHleExclude"/>.</summary>
    public (long From, long To)? TraceHleFrames { get; set; }
    /// <summary>Debug: when set, only HLE calls whose name starts with one of these prefixes are traced (any frame).</summary>
    public string[]? TraceHleOnly { get; set; }
    /// <summary>Debug: restrict the HLE trace to threads whose name contains one of these.</summary>
    public string[]? TraceHleThreads { get; set; }
    public string[] TraceHleExclude { get; set; } = ["sceGxm", "sceKernelGetTLSAddr", "sceKernelLockLwMutex", "sceKernelUnlockLwMutex",
        "sceKernelTryLockLwMutex", "sceNgs", "sceKernelGetProcessTime", "sceKernelGetThreadId", "sceKernelGetSystemTime", "sceFiber"];
    /// <summary>Debug: log a heuristic guest backtrace for every call of this HLE function.</summary>
    public string? BacktraceHle { get; set; }

    public string TitleId { get; private set; } = "";
    public string Title { get; private set; } = "";
    public Sfo? ParamSfo { get; private set; }
    public byte[]? Klicensee { get; private set; }
    public string AppPath { get; private set; } = "";
    public LoadedModule? MainModule { get; private set; }
    /// <summary>Local PSN services (TUS, rankings) of the emulated account, stored under <see cref="EmulatorOptions.NpRoot"/>.</summary>
    public global::VitaSharp.Core.Hle.Np.LocalPsn LocalPsn { get; }
    string NpRoot => LocalPsn.Root;

    public event Action<GuestThread, Exception>? Fatal;
    public volatile bool Running;

    public Emulator(EmulatorOptions options)
    {
        Options = options;
        Jit.Enabled = options.EnableJit;
        string? backend = options.CpuBackend ?? Environment.GetEnvironmentVariable(EnvVars.Cpu);
        if (!Jit.Select(backend))
        {
            Jit.Select(null);
            Log.Warn($"--cpu={backend}: {Jit.WhyUnavailable(backend)}; falling back to {Jit.Mode}");
        }
        Log.Info(Jit.Enabled ? Jit.Describe() : "CPU backend: interpreter only (JIT disabled)");
        TraceHle = options.TraceHle;
        PsnAccount.Current = options.Psn;
        LocalPsn = new global::VitaSharp.Core.Hle.Np.LocalPsn(options.NpRoot ?? global::VitaSharp.Core.Hle.Np.LocalPsn.DefaultRoot(options.FsRoot));
        Log.Info(options.Psn.SignedIn
            ? $"PSN: emulated account '{options.Psn.OnlineId}' signed in ({(options.Psn.LocalServices ? $"local services in {NpRoot}" : "servers unreachable")})"
            : "PSN: signed out, offline");
        Memory = options.Memory ?? new GuestMemory();
        Vfs = new Vfs(options.FsRoot);
        InitProcess();
    }

    /// <summary>Fresh kernel, linker and HLE state (process start, and again after sceAppMgrLoadExec).</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Kernel), nameof(Hle), nameof(Linker))]
    void InitProcess()
    {
        Kernel = new Kernel.Kernel(this);
        Hle = new HleRegistry(this);
        Linker = new Linker(Hle) { InvalidateCode = Code.Invalidate, Log = Log.Warn };
        Kernel.Initialize();
        // every concrete HleModule of the core (generated table), in full type name order as before
        foreach (var m in HleBindings.Modules.OrderBy(m => m.Type.FullName))
            Hle.RegisterModule(m.Create());
    }

    // ------------------------------------------------------------------ process exec (sceAppMgrLoadExec)

    /// <summary>Set by sceAppMgrLoadExec (same application, <c>AppDir</c> null) or by a process switch
    /// (<see cref="RequestLaunch"/>): the executable (app0: path), its arguments and the application folder.</summary>
    public (string Path, string[] Args, string? AppDir)? ExecRequest { get; private set; }
    public event Action? ExecRequested;

    /// <summary>
    /// The programs waiting to come back when the running one exits, most recent last: the shell launching an
    /// application suspends itself here. VitaSharp runs one guest process at a time (they share one address
    /// space), so a launch replaces the shell's process and the shell is started again when the application ends.
    /// </summary>
    public Stack<(string AppDir, string Self)> SuspendedPrograms { get; } = new();

    /// <summary>Called from the guest: stops the process; the host then completes the exec with <see cref="PerformExec"/>.</summary>
    public void RequestExec(string path, string[] args) => QueueExec(path, args, null);

    /// <summary>Replaces the running program with another application (host folder <paramref name="appDir"/>,
    /// executable <paramref name="self"/> relative to it). With <paramref name="suspendCurrent"/> the current program
    /// is restarted when that application exits (<see cref="SuspendedPrograms"/>).</summary>
    public void RequestLaunch(string appDir, string self, string[] args, bool suspendCurrent)
    {
        if (suspendCurrent && MainModule != null) SuspendedPrograms.Push((AppPath, CurrentSelf));
        QueueExec("app0:" + self, args, appDir);
    }

    /// <summary>The process ends (sceKernelExitProcess, the main thread returning): back to the program that launched
    /// it when one is suspended (true), else the emulator stops (false).</summary>
    public bool EndProcess(int status)
    {
        if (SuspendedPrograms.Count == 0) return false;
        var (dir, self) = SuspendedPrograms.Pop();
        Log.Info($"Process exited (0x{status:X}): resuming {dir} ({self})");
        QueueExec("app0:" + self, [], dir);
        return true;
    }

    void QueueExec(string path, string[] args, string? appDir)
    {
        Log.Info($"{(appDir == null ? "LoadExec" : "Launch " + appDir)} {path} [{string.Join(" ", args)}]");
        ExecRequest = (path, args, appDir);
        Running = false;
        Kernel.StopAllThreads();
        ExecRequested?.Invoke();
    }

    /// <summary>The main executable of the running program, relative to its folder.</summary>
    public string CurrentSelf { get; private set; } = "eboot.bin";

    /// <summary>Replaces the process image: waits for the old threads, resets kernel, memory and GPU state and starts
    /// the requested executable in the same address space (like the Vita kernel does for the same application).</summary>
    public void PerformExec(Func<Gpu.IGpuBackend?>? backendFactory)
    {
        var (path, args, appDir) = ExecRequest ?? throw new InvalidOperationException("no exec pending");
        if (!Kernel.WaitAllThreadsExit(10_000)) Log.Warn("LoadExec: some threads did not stop in time");
        Gpu.ReplaceBackend(backendFactory);
        VitaSharp.Core.Util.AudioDiagnostics.ProcessEnded(); // the old process's ports and decoder streams are gone
        Code.Clear();
        Memory.FreeAll();
        VitaSharp.Core.Kernel.Scheduler.Reset();
        InitProcess();
        MainModule = null;
        ExecRequest = null;
        string self = path.StartsWith("app0:", StringComparison.OrdinalIgnoreCase) ? path[5..].TrimStart('/') : path;
        LoadTitle(appDir ?? AppPath, self);
        Start(args);
        Running = true;
    }

    /// <summary>Loads an installed title (folder ux0:app/TITLEID) or a loose folder containing eboot.bin.</summary>
    public void LoadTitle(string appPath, string self = "eboot.bin")
    {
        AppPath = Path.GetFullPath(appPath);
        CurrentSelf = self;
        Vfs.ClearApp0Overlays(); // a process switch brings another application's patch folder
        Vfs.Mount("app0", AppPath);
        var sfoPath = Vfs.ResolveCaseInsensitive(AppPath, "sce_sys/param.sfo");
        if (File.Exists(sfoPath))
        {
            ParamSfo = Sfo.Load(sfoPath);
            TitleId = ParamSfo.TitleId;
            Title = ParamSfo.Title;
        }
        else TitleId = Path.GetFileName(AppPath);

        var patch = Path.Combine(Options.FsRoot, "ux0", "patch", TitleId);
        if (Directory.Exists(patch)) Vfs.AddApp0Overlay(patch);
        var saveDir = Path.Combine(Options.FsRoot, "ux0", "user", "00", "savedata", TitleId);
        Vfs.Mount("savedata0", saveDir);
        Vfs.Mount("addcont0", Path.Combine(Options.FsRoot, "ux0", "addcont", TitleId));

        Klicensee = FindKlicensee();
        string vs0 = Path.GetFullPath(Path.Combine(Options.FsRoot, "vs0")) + Path.DirectorySeparatorChar;
        IsSystemProgram = AppPath.StartsWith(vs0, StringComparison.OrdinalIgnoreCase);
        _startFirst.Clear();
        Log.Info($"Loading {TitleId} \"{Title}\" from {AppPath}");
        // per-title shader/pipeline caches start warming up while the executable loads
        try { Gpu.Backend?.BeginTitle(TitleId); } catch (Exception e) { Log.Warn($"GPU backend title setup failed: {e.Message}"); }

        if (Vfs.Resolve("app0:" + self) is { } selfHost && File.Exists(selfHost))
        {
            using var fs = File.OpenRead(selfHost);
            var head = new byte[Math.Min(fs.Length, 0x1000)];
            fs.ReadExactly(head);
            ProgramAuthorityId = SelfDecryptor.ProgramAuthorityId(head);
        }
        MainModule = LoadModule("app0:" + self);
        if (IsSystemProgram) Log.Info($"System program {MainModule.Name}, authority ID 0x{ProgramAuthorityId:X16}");
        Loader.TitlePatches.Apply(TitleId, MainModule.Name, Options.PatchDir);
        if (MainModule.TlsInit != 0 || MainModule.TlsAreaSize != 0)
        {
            Kernel.TlsInit = MainModule.TlsInit;
            Kernel.TlsInitSize = MainModule.TlsInitSize;
            Kernel.TlsAreaSize = MainModule.TlsAreaSize;
        }
        Kernel.ProcessParam = Linker.ProcessParam;

        // Preloaded system libraries that the game ships itself (LLE).
        uint preloadDisabled = ProcessParamField(0x24);
        foreach (var (bit, name) in new[] { (0x00010000u, "libc"), (0x00200000u, "libfios2"), (0x00800000u, "libSceFt2"), (0x01000000u, "libpvf") })
        {
            if ((preloadDisabled & bit) != 0) continue;
            var host = Vfs.Resolve($"app0:sce_module/{name}.suprx");
            if (host != null && File.Exists(host)) LoadModule($"app0:sce_module/{name}.suprx");
        }
        if (IsSystemProgram) PreloadFirmwareModules(preloadDisabled);
        foreach (uint nid in Linker.UnresolvedVariables())
            Log.Warn($"Unresolved variable import NID {nid:X8} ({NidDatabase.Lookup(nid)?.Name ?? "unknown"})");
    }

    /// <summary>The running executable is part of the system software (vs0:app/NPXS*, vs0:vsh/shell/shell.self...):
    /// it runs with system privileges and uses the firmware's own user-mode libraries (LLE) where VitaSharp's HLE
    /// stops: the process preloads, the sysmodules it loads and the libraries those import.</summary>
    public bool IsSystemProgram { get; private set; }

    /// <summary>Program authority ID of the main executable's SELF (0 for a plain ELF).</summary>
    public ulong ProgramAuthorityId { get; private set; }

    /// <summary>SceSblACMgr's "game program" class: games (authority 0x2F...) and plain/homebrew executables; the
    /// system software (shell, NPXS apps) is not.</summary>
    public bool IsGameProgram => !IsSystemProgram || ProgramAuthorityId >> 56 == 0x2F;

    /// <summary>Modules loaded with the process, started before the others (each after the modules it imports from).</summary>
    readonly List<LoadedModule> _startFirst = new();

    /// <summary>
    /// The modules the Vita's module manager loads into every process before its main thread, unless the process
    /// parameters' preload-disabled bits exclude them (bit, file in vs0:sys/external; same list as Vita3K). Games keep
    /// VitaSharp's HLE for these (only copies shipped in app0:sce_module are loaded); system programs get the firmware's.
    /// </summary>
    static readonly (uint Bit, string Name)[] ProcessPreloads =
    [
        (0x00010000, "libc"), (0x00080000, "libshellsvc"), (0x00100000, "libcdlg"), (0x00200000, "libfios2"),
        (0x00400000, "apputil"), (0x00800000, "libSceFt2"), (0x01000000, "libpvf"),
    ];

    void PreloadFirmwareModules(uint preloadDisabled)
    {
        foreach (var (bit, name) in ProcessPreloads)
        {
            if ((preloadDisabled & bit) != 0 || KeepOnHle(name)) continue;
            string path = $"vs0:sys/external/{name}.suprx";
            if (Vfs.Resolve(path) is not { } host || !File.Exists(host)) continue;
            if (Kernel.Modules.Values.Any(m => string.Equals(m.Path, $"app0:sce_module/{name}.suprx", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var m = LoadModule(path);
            _startFirst.AddRange(LoadFirmwareDependencies(m));
            _startFirst.Add(m);
        }
    }

    HashSet<string>? _keepHle;

    /// <summary>A library (or module file name) the HLE-modules option keeps on HLE.</summary>
    internal bool KeepOnHle(string name)
    {
        _keepHle ??= new HashSet<string>(Options.HleModules ?? (Environment.GetEnvironmentVariable(EnvVars.HleModules) ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        return _keepHle.Contains(name);
    }

    /// <summary>Firmware modules a process loads only on request (sceSysmoduleLoadModuleInternal*: libpaf takes its
    /// heap sizes as module_start arguments), so they are never pulled in as another module's dependency.</summary>
    public static readonly HashSet<string> OnRequestModules = new(StringComparer.OrdinalIgnoreCase) { "libpaf" };

    /// <summary>
    /// Loads (without starting them) the firmware modules that provide the libraries <paramref name="root"/> imports,
    /// and theirs in turn, the way sysmodule loads a module's dependencies. Returns them in start order (each after
    /// its own dependencies). A library is taken from the firmware when the HLE does not implement every function
    /// imported from it and no loaded module exports it; libraries named in the HLE-modules option stay on HLE, and
    /// <see cref="OnRequestModules"/> wait for the program to load them.
    /// </summary>
    public List<LoadedModule> LoadFirmwareDependencies(LoadedModule root)
    {
        var index = Firmware.FirmwareLibraries.For(Options.FsRoot);
        var order = new List<LoadedModule>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(LoadedModule m)
        {
            foreach (var (lib, _, nids) in m.Imports)
            {
                if (index.Provider(lib) is not { } provider) continue;
                string file = Path.GetFileNameWithoutExtension(provider.VitaPath);
                if (OnRequestModules.Contains(file) || KeepOnHle(lib) || KeepOnHle(file)) continue;
                if (Kernel.Modules.Values.Any(x => string.Equals(x.Path, provider.VitaPath, StringComparison.OrdinalIgnoreCase))) continue;
                // a library VitaSharp emulates in HLE (even partly: NP, AppUtil, Http...) stays on HLE, so every
                // process sees the same emulated services; only libraries the HLE knows nothing of come from the firmware
                if (Hle.ImplementsLibrary(lib) || nids.All(n => Linker.TryGetFunctionExport(n, out _))) continue;
                if (!visiting.Add(provider.VitaPath)) continue;
                LoadedModule dep;
                try { dep = LoadModule(provider.VitaPath); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Log.Warn($"Firmware dependency {provider.VitaPath} (for {lib}) failed to load: {e.Message}");
                    continue;
                }
                Log.Info($"  {provider.VitaPath} provides {lib} for {m.Name} (LLE)");
                Visit(dep);
                order.Add(dep);
            }
        }
        Visit(root);
        return order;
    }

    uint ProcessParamField(int offset)
    {
        uint pp = Linker.ProcessParam;
        if (pp == 0) return 0;
        uint size = GuestMemory.R32(pp);
        if (offset + 4 > size) return 0;
        return GuestMemory.R32(pp + (uint)offset);
    }

    byte[]? FindKlicensee()
    {
        foreach (var p in new[]
                 {
                     Vfs.ResolveCaseInsensitive(AppPath, "sce_sys/package/work.bin"),
                 }.Concat(Directory.Exists(Path.Combine(Options.FsRoot, "ux0", "license", TitleId))
                     ? Directory.GetFiles(Path.Combine(Options.FsRoot, "ux0", "license", TitleId), "*.rif") : []))
        {
            if (File.Exists(p)) return PkgInstaller.ReadKlicensee(p);
        }
        return null;
    }

    public LoadedModule LoadModule(string vitaPath)
    {
        var host = Vfs.Resolve(vitaPath) ?? throw new FileNotFoundException(vitaPath);
        var elf = SelfDecryptor.ToElf(File.ReadAllBytes(host), Klicensee);
        var m = ModuleLoader.Load(elf, vitaPath, Memory, Linker, 0);
        m.Uid = Kernel.Objects.Register(new ModuleObject(m));
        Kernel.Modules[m.Uid] = m;
        Code.Invalidate(0, uint.MaxValue);
        Log.Info($"Loaded module {m.Name} ({vitaPath}) start=0x{m.StartEntry:X8} imports={m.Imports.Sum(i => i.FuncNids.Length)}");
        return m;
    }

    /// <summary>Removes a loaded module (sceKernelUnloadModule): imports that pointed at its exports go back to the next
    /// provider or to HLE, its segments are freed and the translated code of their range is dropped.</summary>
    public void UnloadModule(LoadedModule m)
    {
        int rebound = Linker.UnregisterModule(m);
        foreach (var s in m.Segments.Values)
        {
            Code.Invalidate(s.Address, s.Size);
            Memory.Free(s.Address);
        }
        Kernel.Modules.TryRemove(m.Uid, out _);
        Kernel.Objects.Remove(m.Uid);
        Log.Info($"Unloaded module {m.Name} ({m.Path}): {rebound} import stub(s) rebound");
    }

    /// <summary>Runs library module_start functions, then starts the main thread at the eboot entry.</summary>
    public void Start(string[]? args = null)
    {
        if (MainModule == null) throw new InvalidOperationException("No title loaded");
        Running = true;
        int priority = (int)(ProcessParamPtrValue(0x14) ?? 0x10000100);
        uint stackSize = ProcessParamPtrValue(0x18) ?? 256 * 1024;
        int affinity = (int)(ProcessParamPtrValue(0x28) ?? 0);
        if (affinity == 0) affinity = 0x70000;
        string name = ProcessParamString(0x10) ?? "main";

        var main = Kernel.CreateThread(name, MainModule.StartEntry, priority, stackSize, 0, affinity);
        Kernel.MainThread = main;

        // Library module_start functions run (in load order) before the main thread.
        // (firmware dependencies of a system program first, each after the modules it imports from)
        foreach (var m in _startFirst.Concat(Kernel.Modules.Values.Where(m => !_startFirst.Contains(m)).OrderBy(m => m.Uid))
                     .Where(m => m != MainModule && m.StartEntry != 0).ToList())
            RunOnNewThread($"start:{m.Name}", m.StartEntry);
        if (args is { Length: > 0 })
        {
            // argv as the kernel passes it: the strings back to back, NUL terminated
            var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\0", args) + "\0");
            uint argp = Memory.Alloc((uint)Math.Max(bytes.Length, 16), "process args");
            bytes.CopyTo(GuestMemory.Span(argp, bytes.Length));
            main.Start((uint)bytes.Length, argp);
        }
        else main.Start(0, 0);
    }

    void RunOnNewThread(string name, uint entry)
    {
        var t = Kernel.CreateThread(name, entry, 0x10000100, 256 * 1024, 0, 0x70000);
        t.Start(0, 0);
        if (!t.WaitForExit(30_000)) Log.Warn($"{name} did not finish in time");
    }

    uint? ProcessParamPtrValue(int offset)
    {
        uint ptr = ProcessParamField(offset);
        return ptr == 0 ? null : GuestMemory.R32(ptr);
    }

    string? ProcessParamString(int offset)
    {
        uint ptr = ProcessParamField(offset);
        return ptr == 0 ? null : GuestMemory.ReadCString(ptr);
    }

    public void ReportFatal(GuestThread t, Exception e) => Fatal?.Invoke(t, e);

    public void Stop()
    {
        Running = false;
        Kernel.StopAllThreads();
    }

    public void Dispose()
    {
        Stop();
        Gpu.Dispose();
        Gpu.Backend?.Dispose();
        Host.Dispose();
    }
}

public sealed class ModuleObject(LoadedModule module) : KernelObject
{
    public LoadedModule Module { get; } = module;
}
