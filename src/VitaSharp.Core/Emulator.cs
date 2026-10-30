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
    /// <summary>Real internet for SceNet/SceHttp (off by default, see <see cref="NetConfig"/>).</summary>
    public NetConfig Net { get; init; } = NetConfig.Off;
    /// <summary>An RPCN server for Score/TUS (used only with real internet and a signed-in account); null = the local
    /// PSN services.</summary>
    public Hle.Np.Rpcn.RpcnConfig? Rpcn { get; init; }
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
    /// <summary>What this process shares with the other processes of the emulated system (object table, display).</summary>
    public VitaSystem Vita { get; }
    /// <summary>The process that started this one (the shell, for an application it launched); null for the first.</summary>
    public Emulator? Parent { get; }
    /// <summary>Process id (the UID of its kernel process object; 0x40010003 for the first process).</summary>
    public int Pid { get; }
    /// <summary>SceAppMgr application id of the process (what the shell's launch call returns and its later calls name).</summary>
    public int AppId { get; }
    /// <summary>The process declared itself the shell (sceAppMgrDeclareShellProcess2): SceAppMgr's shell events go to it.</summary>
    public bool IsShell { get; set; }
    /// <summary>The system's guest memory (allocator, shared regions), the same object for every process.</summary>
    public GuestMemory Memory { get; }
    /// <summary>The process's own address space (<see cref="AddressSpace"/>): its threads, and host work done for it,
    /// read and write guest memory there.</summary>
    public AddressSpace Space { get; }
    public BlockCache Code { get; } = new();
    public Kernel.Kernel Kernel { get; private set; }
    public HleRegistry Hle { get; private set; }
    public Linker Linker { get; private set; }
    public Vfs Vfs { get; }
    public Host.HostServices Host { get; }
    public Gpu.GpuQueue Gpu { get; }
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
        NetConfig.Current = options.Net;
        LocalPsn = new global::VitaSharp.Core.Hle.Np.LocalPsn(options.NpRoot ?? global::VitaSharp.Core.Hle.Np.LocalPsn.DefaultRoot(options.FsRoot));
        if (options.Net.Real)
            Log.Warn(options.Net.Allow == null
                ? "Network: real internet (--net=real): game traffic leaves this PC; Sony's servers stay blocked"
                : $"Network: real internet limited to {string.Join(", ", options.Net.Allow)} (--net-allow); everything else is blocked and logged");
        if (options.Rpcn is { } rpcn)
        {
            if (!options.Net.Real) Log.Warn($"RPCN server {rpcn.Host} ignored: real internet is off (--net=real)");
            else if (!options.Psn.SignedIn) Log.Warn($"RPCN server {rpcn.Host} ignored: the PSN account is signed out (--psn=on)");
            else
            {
                LocalPsn.UseRpcn(new global::VitaSharp.Core.Hle.Np.Rpcn.RpcnClient(rpcn, options.Net));
                PsnAccount.Current = options.Psn with { Server = $"{rpcn.Host}:{rpcn.Port}" };
            }
        }
        Log.Info(options.Psn.SignedIn
            ? $"PSN: emulated account '{options.Psn.OnlineId}' signed in ({(LocalPsn.Rpcn is { } rc ? $"Score/TUS on RPCN server {rc.Config.Host}:{rc.Config.Port}" : options.Psn.LocalServices ? $"local services in {NpRoot}" : "servers unreachable")})"
            : "PSN: signed out, offline");
        Memory = options.Memory ?? new GuestMemory();
        Space = Memory.Primary;
        Host = new Host.HostServices();
        Gpu = new Gpu.GpuQueue();
        Vita = new VitaSystem();
        Pid = Vita.Register(this);
        AppId = Vita.RegisterApp(this);
        Vfs = new Vfs(options.FsRoot);
        InitProcess();
        EmulationPause.Changed += OnPauseChanged;
    }

    /// <summary>A further process of the same system (<see cref="SpawnProcess"/>): shares the guest memory's shared regions
    /// (CDRAM, kernel), GPU, host devices and kernel object table of <paramref name="parent"/>, with its own address
    /// space, kernel state, HLE modules and mounts.</summary>
    Emulator(Emulator parent)
    {
        Parent = parent;
        Options = parent.Options;
        Vita = parent.Vita;
        Memory = parent.Memory;
        Space = Memory.CreateSpace("process"); // its own user address space (CDRAM and the kernel region are shared)
        Host = parent.Host;
        Gpu = parent.Gpu;
        LocalPsn = parent.LocalPsn;
        TraceHle = parent.TraceHle;
        TraceHleFrames = parent.TraceHleFrames;
        TraceHleOnly = parent.TraceHleOnly;
        TraceHleThreads = parent.TraceHleThreads;
        TraceHleExclude = parent.TraceHleExclude;
        BacktraceHle = parent.BacktraceHle;
        Pid = Vita.Register(this);
        AppId = Vita.RegisterApp(this);
        Vfs = new Vfs(Options.FsRoot);
        InitProcess();
    }

    /// <summary>Fresh kernel, linker and HLE state (process start, and again after sceAppMgrLoadExec).</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Kernel), nameof(Hle), nameof(Linker))]
    void InitProcess()
    {
        using var scope = ProcessScope.Enter(this);
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
    /// application suspends itself here, in the one-process-at-a-time model (VITASHARP_SINGLE_PROCESS=1, or an
    /// application that cannot be placed next to the shell): a launch replaces the shell's process and the shell is
    /// started again when the application ends.
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

    /// <summary>The process ends (sceKernelExitProcess, the main thread returning): true when the system keeps running
    /// (a process the shell started ends and the shell takes the display back, or a suspended program is resumed),
    /// false when this was the last process (the emulator stops).</summary>
    public bool EndProcess(int status)
    {
        if (Parent != null)
        {
            _exitStatus = status;
            Terminate($"exited (0x{status:X})");
            return true;
        }
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
        if (Parent != null)
        {
            // a process of a multi-process system re-executes on its own: the host front-end only drives the first
            Task.Run(() =>
            {
                try { PerformExec(null); }
                catch (Exception e) when (e is not OutOfMemoryException) { Log.Error($"{Describe()}: LoadExec failed: {e}"); Terminate("LoadExec failed"); }
            });
            return;
        }
        ExecRequested?.Invoke();
    }

    // ------------------------------------------------------------------ several processes (the shell and an application)

    /// <summary>
    /// Creates a new process next to this one for the executable <paramref name="self"/> of the application in
    /// <paramref name="appDir"/> (the shell launching a game from its LiveArea) and boots it on a host thread: loading
    /// the executable and running its module_start functions takes a while. The process (with its application id) exists
    /// at once; it takes the display when the shell activates it (sceAppMgrActivateApp), or with
    /// <paramref name="activate"/>. It runs in its own address space (<see cref="Memory.GuestMemory.CreateSpace"/>), so an
    /// executable linked at the shell's addresses runs too; only when every process shares one space
    /// (VITASHARP_SHARED_ADDRESS_SPACE=1, or a host that cannot map memory twice) can such an executable not be placed:
    /// then it ends and <paramref name="cannotPlace"/> runs instead.
    /// </summary>
    public Emulator SpawnProcess(string appDir, string self, string[] args, bool activate = false, Action? cannotPlace = null, bool background = false)
    {
        Gpu.SharedByProcesses = true; // from now on GPU scenes are arbitrated between the processes
        var child = new Emulator(this) { _background = background };
        _children.Add(child);
        child.TitleId = Path.GetFileName(Path.TrimEndingDirectorySeparator(appDir));
        if (!ReferenceEquals(child.Space, Space)) child.Space.Label = child.Describe();
        child.Running = true;
        // SceAppMgr tells the shell about the new application process: event 2 {pid, application id}
        if (Vita.Shell != null) Vita.PostShellEvent(ShellEvents.AppCreated, child.Pid, child.AppId);
        Task.Run(() =>
        {
            try
            {
                child.LoadTitle(appDir, self);
                Log.Info($"Process {child.Describe()} started by {Describe()}");
                child.Start(args);
                // the launch finished: event 0xA {application id}; the shell then activates it
                if (Vita.Shell != null && !background) Vita.PostShellEvent(ShellEvents.AppLaunched, child.AppId);
                if (activate) Vita.SetForeground(child);
            }
            catch (InvalidOperationException e) when (e.Message.Contains("cannot map fixed segment"))
            {
                Log.Warn($"{child.Describe()}: {e.Message}; it cannot run next to {Describe()}");
                child.Terminate("could not be placed");
                cannotPlace?.Invoke();
                if (cannotPlace == null && Vita.Shell != null) Vita.PostShellEvent(ShellEvents.AppLaunched, -1);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Error($"Process {child.Describe()} could not start: {e}");
                child.Terminate("failed to start");
                if (Vita.Shell != null) Vita.PostShellEvent(ShellEvents.AppLaunched, -1);
            }
        });
        return child;
    }

    readonly List<Emulator> _children = new();
    int _terminated, _exitStatus;
    bool _background;

    /// <summary>Ends this process (exit, crash, or killed by the shell): stops its threads, then frees its memory and kernel
    /// objects and gives the display back. The threads are joined on a host thread, so this can be called from one of them.</summary>
    public void Terminate(string why)
    {
        if (Parent == null) { Stop(); return; }
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        Log.Info($"Process {Describe()} {why}");
        Running = false;
        // the screen goes back to the parent (the shell) at once, not after the threads are gone (that can take seconds)
        if (Parent is { } parent && ReferenceEquals(Vita.Foreground, this)) Vita.SetForeground(parent);
        Kernel.StopAllThreads();
        foreach (var c in _children.ToArray()) c.Terminate("ended with its parent");
        Task.Run(() =>
        {
            if (!Kernel.WaitAllThreadsExit(10_000)) Log.Warn($"{Describe()}: some threads did not stop in time");
            Release();
        });
    }

    /// <summary>Gives everything the process holds back to the system.</summary>
    void Release()
    {
        // SceAppMgr tells the shell the application is gone: event 4 {+4 pid, +0x14 application id}. Not for a background
        // service: NPXS10079 (daily checker) exits at once here, where on the Vita it stays resident, and the shell would
        // start it again on every end
        if (Vita.Shell is { } shell && shell != this && !_background) Vita.PostShellEvent(ShellEvents.AppEnded, Pid, 0, 0, 0, AppId);
        Vita.Unregister(this);
        Parent?._children.Remove(this);
        Gpu.ReleaseScene(Pid);
        int objects = Vita.Objects.RemoveOwnedBy(Pid);
        // the GPU finishes the process's commands before its memory goes (bounded: a stuck GPU must not keep the space)
        if (!Task.Run(Gpu.WaitIdle).Wait(5000)) Log.Warn($"{Describe()}: the GPU did not finish its work in time");
        long bytes = Memory.FreeOwnedBy(Pid);
        Code.Clear();
        Memory.ReleaseSpace(Space);
        Log.Info($"Process {Describe()} released: {objects} kernel objects, {bytes / 1024} KB of memory");
    }

    /// <summary>The process is suspended: its threads stop at their next safe point (see <see cref="GuestThread"/>).</summary>
    public volatile bool Suspended;

    /// <summary>The thread of this process that submitted a GPU BeginScene not yet followed by its EndScene (any thread
    /// of the process may end it). While the emulation is paused that thread runs on until the scene is closed, so the
    /// GPU finishes the frame (<see cref="EmulationPause"/>).</summary>
    public GuestThread? OpenSceneThread
    {
        get => Volatile.Read(ref _openSceneThread);
        set
        {
            var old = Interlocked.Exchange(ref _openSceneThread, value);
            // the scene is closed during a pause: that thread parks at its next block boundary too
            if (old != null && old != value && EmulationPause.IsPaused) old.Core.InterruptRequested = true;
        }
    }
    GuestThread? _openSceneThread;

    /// <summary>The emulation was paused or resumed: the host audio output follows (first process only: the devices are
    /// its own).</summary>
    void OnPauseChanged(bool paused)
    {
        try { Host.Audio?.SetPaused(paused); }
        catch (Exception e) { Log.Warn($"Audio: could not {(paused ? "pause" : "resume")} the output: {e.Message}"); }
    }

    /// <summary>Suspends the process (the shell took the screen back: the application waits in the background).</summary>
    public void Suspend()
    {
        if (Suspended) return;
        Suspended = true;
        foreach (var t in Kernel.Threads) t.Core.InterruptRequested = true;
        Log.Info($"Process {Describe()} suspended");
    }

    public void Resume()
    {
        if (!Suspended) return;
        Suspended = false;
        foreach (var t in Kernel.Threads) t.WakeFromSuspend();
        Log.Info($"Process {Describe()} resumed");
    }

    /// <summary>"TITLEID (pid 0x...)" for logs.</summary>
    public string Describe() => $"{(TitleId.Length > 0 ? TitleId : "process")} (pid 0x{Pid:X})";

    /// <summary>The system made this process the foreground one: what it last displayed goes back on screen.</summary>
    internal void OnForeground()
    {
        // called on the thread that switched the foreground (the shell's): the frame is in this process's memory
        using var space = AddressSpace.Use(Space);
        foreach (var d in Hle.Modules.OfType<Hle.Modules.Display>()) d.Represent();
    }

    /// <summary>This process is the one on screen (or the only one): its frames are shown and it gets the input.</summary>
    public bool IsForeground => Vita.IsForeground(this);

    /// <summary>The shell's executable in vs0:vsh/shell.</summary>
    public const string ShellSelf = "shell.self";

    /// <summary>The main executable of the running program, relative to its folder.</summary>
    public string CurrentSelf { get; private set; } = "eboot.bin";

    /// <summary>Replaces the process image: waits for the old threads, resets kernel, memory and GPU state and starts
    /// the requested executable in the same address space (like the Vita kernel does for the same application).</summary>
    public void PerformExec(Func<Gpu.IGpuBackend?>? backendFactory)
    {
        var (path, args, appDir) = ExecRequest ?? throw new InvalidOperationException("no exec pending");
        if (!Kernel.WaitAllThreadsExit(10_000)) Log.Warn("LoadExec: some threads did not stop in time");
        bool alone = Vita.Processes.Count == 1;
        if (Parent == null) Gpu.ReplaceBackend(backendFactory); // the GPU is the system's: only the front-end's process replaces it
        if (alone) VitaSharp.Core.Util.AudioDiagnostics.ProcessEnded(); // the old process's ports and decoder streams are gone
        Code.Clear();
        if (alone)
        {
            Memory.FreeAll();
            Vita.Objects.RemoveOwnedBy(0);
            VitaSharp.Core.Kernel.Scheduler.Reset();
        }
        else Memory.FreeOwnedBy(Pid);
        Vita.Objects.RemoveOwnedBy(Pid);
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
        using var scope = ProcessScope.Enter(this);
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
        RunsOnSystemCore = IsSystemProgram && string.Equals(self, ShellSelf, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.TrimEndingDirectorySeparator(AppPath)).Equals("shell", StringComparison.OrdinalIgnoreCase);
        // the shell starting = the system software coming up with the memory card inserted: titles VitaSharp installed
        // (or that were copied into ux0:app) get the appmeta the Vita's installer leaves, for the shell's database
        if (RunsOnSystemCore)
            Package.AppMeta.Sync(Path.Combine(Options.FsRoot, "ux0"), Log.Info);
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
        if (IsSystemProgram)
        {
            PreloadFirmwareModules(preloadDisabled);
            // the executable's own imports from firmware libraries nothing loaded provides (the shell calls
            // SceNpCommonPs4 in its first initialisation, before it loads any module that would bring it). Modules
            // that need an on-request module (libpaf) wait for the program's own sysmodule load, as before.
            _startFirst.AddRange(LoadFirmwareDependencies(MainModule, withoutOnRequest: true));
        }
        // a system program's variable imports from firmware modules it loads itself later through sysmodule (ScePaf*
        // from libpaf, SceCommonGuiDialog's class tables...) are bound when that module loads; only the others are missing
        var firmware = IsSystemProgram ? Firmware.FirmwareLibraries.For(Options.FsRoot) : null;
        int later = 0;
        foreach (uint nid in Linker.UnresolvedVariables())
        {
            string source = Linker.VariableImportSource(nid), lib = source.Split(' ')[0];
            if (firmware?.Provider(lib) != null) { later++; continue; }
            Log.Warn($"Unresolved variable import NID {nid:X8} ({NidDatabase.Lookup(nid)?.Name ?? "unknown"}) from {source}");
        }
        if (later > 0) Log.Info($"{later} variable imports wait for firmware modules the program loads itself (libpaf, common_gui_dialog...)");
    }

    /// <summary>The running executable is part of the system software (vs0:app/NPXS*, vs0:vsh/shell/shell.self...):
    /// it runs with system privileges and uses the firmware's own user-mode libraries (LLE) where VitaSharp's HLE
    /// stops: the process preloads, the sysmodules it loads and the libraries those import.</summary>
    public bool IsSystemProgram { get; private set; }

    /// <summary>The process is the shell (vs0:vsh/shell/shell.self): its threads run on the Vita's 4th core, the one
    /// the system keeps for itself, instead of the three application cores (<see cref="Kernel.Scheduler"/>).</summary>
    public bool RunsOnSystemCore { get; private set; }

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
    public List<LoadedModule> LoadFirmwareDependencies(LoadedModule root, bool withoutOnRequest = false)
    {
        var index = Firmware.FirmwareLibraries.For(Options.FsRoot);
        var order = new List<LoadedModule>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needsOnRequest = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // whether a firmware module imports (itself or through the firmware modules it would bring) a library of an
        // on-request module, read from its image without loading it
        bool NeedsOnRequest(string vitaPath)
        {
            if (needsOnRequest.TryGetValue(vitaPath, out bool known)) return known;
            needsOnRequest[vitaPath] = false; // cycles
            bool result = false;
            try
            {
                if (Vfs.Resolve(vitaPath) is { } host && File.Exists(host))
                {
                    var img = ModuleImage.Read(Loader.Self.SelfDecryptor.ToElf(File.ReadAllBytes(host), null));
                    foreach (var (lib, _, _) in img.Imports)
                    {
                        if (index.Provider(lib) is not { } p || Hle.ImplementsLibrary(lib)) continue;
                        if (OnRequestModules.Contains(Path.GetFileNameWithoutExtension(p.VitaPath))
                            || (!string.Equals(p.VitaPath, vitaPath, StringComparison.OrdinalIgnoreCase) && NeedsOnRequest(p.VitaPath)))
                        { result = true; break; }
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { result = true; }
            return needsOnRequest[vitaPath] = result;
        }

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
                if (withoutOnRequest && NeedsOnRequest(provider.VitaPath)) continue;
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
        using var scope = ProcessScope.Enter(this);
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
        // the launch posts the application's PARAM_NOTIFY event (FW 3.74 kernel appmgr, its launch path at 0x8100EC52):
        // Settings and Friends wait for it to open their first page and then read the launch parameter, which a launch
        // without one (the shell starting an application from its bubble passes NULL) leaves empty: the first
        // sceAppMgrGetAppParam succeeds with "". (Not the shell itself: the kernel starts it, it is no launched application.)
        if (!RunsOnSystemCore)
        {
            var appMgr = Hle.Modules.OfType<VitaSharp.Core.Hle.Modules.AppMgr>().First();
            appMgr.LaunchParam ??= "";
            appMgr.PostEvent(VitaSharp.Core.Hle.Modules.AppMgr.EventParamNotify);
        }
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

    /// <summary>A guest thread crashed. In a process the shell started, the process ends (the shell carries on, as on
    /// the Vita); in the first process it is the front-end's fatal error.</summary>
    public void ReportFatal(GuestThread t, Exception e)
    {
        if (Parent != null && Fatal == null)
        {
            var root = Parent;
            while (root.Parent != null) root = root.Parent;
            if (!root.Running || !Running) return; // the emulator is shutting down
            Log.Error($"Process {Describe()} crashed in thread '{t.Name}': {e.Message}");
            Terminate("crashed");
            return;
        }
        Fatal?.Invoke(t, e);
    }

    /// <summary>Stops this process's threads, and those of the processes it started.</summary>
    public void Stop()
    {
        Running = false;
        foreach (var c in _children.ToArray()) c.Stop();
        Kernel.StopAllThreads();
    }

    public void Dispose()
    {
        Stop();
        if (Parent != null) return; // the GPU and host devices are the first process's
        EmulationPause.Changed -= OnPauseChanged;
        Gpu.Dispose();
        Gpu.Backend?.Dispose();
        Host.Dispose();
    }
}

public sealed class ModuleObject(LoadedModule module) : KernelObject
{
    public LoadedModule Module { get; } = module;
}
