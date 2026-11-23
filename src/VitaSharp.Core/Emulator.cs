using System.Reflection;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
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
    /// <summary>The emulator option "System language" of a game (the Vita has one language for the whole system): the
    /// SceSystemParamLang (0..19) its applications read through SceAppUtil, SceRegMgrForGame, SceRegMgr and SceNp instead of
    /// the system registry's /CONFIG/SYSTEM/language, for games without the system's language. The system software
    /// (programs from vs0:) keeps the system's. Null: the system's language.</summary>
    public int? SystemLanguage { get; init; }
    /// <summary>The emulator option "Carregar o cache de shaders antes de jogar": before the game starts, wait for the
    /// shaders and pipelines its earlier sessions recorded (its shader cache) (<see cref="Gpu.IGpuBackend.LoadShaderCacheBeforeStart"/>), with progress in the log
    /// (<see cref="Gpu.ShaderCacheLoad"/>). Off: they build in the background while the game runs.</summary>
    public bool LoadShaderCacheBeforeStart { get; init; }
    /// <summary>The emulator option "Precompilar código ARM": before a process starts, translate its modules' code on
    /// every free core (<see cref="CodeCache.PrecompilePending"/>): the blocks earlier sessions translated (the code
    /// cache in <see cref="CodeCacheDir"/>) and the code the static analysis finds from the modules' entry points. Off:
    /// the cached blocks compile in the background while the game runs and the rest when it gets hot.</summary>
    public bool PrecompileCode { get; init; }
    /// <summary>Whether the boot step of <see cref="PrecompileCode"/> waits for the recorded blocks (or, in a first
    /// session, the code reached from the entry points) before the process starts; false: everything is translated in
    /// the background while the game runs (Android, until its game screen shows the progress).</summary>
    public bool PrecompileCodeWaits { get; init; } = true;
    /// <summary>Folder of the code cache (one file per module image, <see cref="CodeCacheFile"/>); null: no cache.</summary>
    public string? CodeCacheDir { get; init; }
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
    /// ("libult", "libSceJson", or the short names "ult", "json", ...), and the process preloads a game ships ("libc",
    /// "libfios2"...). Null: VITASHARP_HLE_MODULES.</summary>
    public IReadOnlyCollection<string>? HleModules { get; init; }
    /// <summary>The emulator option "Enable Kernel Plugins" (the Vita has no such setting, like pausing or recording
    /// the screen): on, the emulator reads the plugin configuration (ur0:tai / ux0:tai config.txt) and loads the plugins
    /// it lists; off (the default), the pure firmware runs and no plugin loads. Null: VITASHARP_KERNEL_PLUGINS=1.</summary>
    public bool? KernelPlugins { get; init; }
    /// <summary>The emulator's plugin folder (its config.txt and the files it names without a device); null:
    /// ur0:kernel_plugins (&lt;FsRoot&gt;/ur0/kernel_plugins), where the folder of earlier versions
    /// (&lt;DataRoot&gt;/kernel_plugins, next to <see cref="FsRoot"/>) moves once.</summary>
    public string? KernelPluginsDir { get; init; }
    /// <summary>An existing guest address space to run in (there is one per process: unit tests that boot an
    /// emulator share the test fixture's). Null: the emulator creates it.</summary>
    public GuestMemory? Memory { get; init; }
}

/// <summary>Which PlayStation Mobile runtime a process is (<see cref="Emulator.PsmRuntime"/>).</summary>
public enum PsmRuntimeKind
{
    /// <summary>Not a PSM runtime.</summary>
    None,
    /// <summary>The PSM runtime package (PCSI00011, the Mono runtime of PSM titles).</summary>
    Mono,
    /// <summary>A runtime a PSM title carries itself (Unity for PSM, ux0:psm/&lt;id&gt;/runtime).</summary>
    OwnRuntime,
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
    /// <summary>The flags the shell started the application with (sceAppMgrLaunchAppByName2ndStage), 0 otherwise.</summary>
    public int LaunchFlags { get; set; }
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

    public string TitleId { get; internal set; } = "";
    public string Title { get; private set; } = "";
    public Sfo? ParamSfo { get; private set; }
    public byte[]? Klicensee { get; private set; }

    /// <summary>The secure processor's licence verification and key decryption for app0's PFS mount
    /// (<see cref="Package.LicenseTitleKey"/>): the emulated unit has none (null, the step answers 0x80020004); only
    /// the tests set one, with a test key.</summary>
    internal Func<byte[], byte[]?>? SecureProcessorRifKey { get; set; }
    public string AppPath { get; internal set; } = "";
    public LoadedModule? MainModule { get; private set; }
    /// <summary>Local PSN services (TUS, rankings) of the emulated account, stored under <see cref="EmulatorOptions.NpRoot"/>.</summary>
    public global::VitaSharp.Core.Hle.Np.LocalPsn LocalPsn { get; }
    private string NpRoot => LocalPsn.Root;

    public event Action<GuestThread, Exception>? Fatal;
    public volatile bool Running;

    /// <summary>The process-wide JIT state the root emulator replaced (restored by <see cref="Dispose"/>).</summary>
    private (bool Enabled, string Mode)? _jitBefore;

    public Emulator(EmulatorOptions options)
    {

        Options = options;
        // the JIT switches are process-wide: Dispose gives them back, so an emulator built with EnableJit = false (the
        // HLE unit tests, the disasm command) does not leave the JIT off for everything that runs after it
        _jitBefore = (Jit.Enabled, Jit.Mode);
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
        {
            Log.Warn(options.Net.Allow == null
                ? "Network: real internet (--net=real): game traffic leaves this PC; Sony's servers stay blocked"
                : $"Network: real internet limited to {string.Join(", ", options.Net.Allow)} (--net-allow); everything else is blocked and logged");
        }

        if (options.Rpcn is { } rpcn)
        {
            if (!options.Net.Real)
            {
                Log.Warn($"RPCN server {rpcn.Host} ignored: real internet is off (--net=real)");
            }
            else if (!options.Psn.SignedIn)
            {
                Log.Warn($"RPCN server {rpcn.Host} ignored: the PSN account is signed out (--psn=on)");
            }
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
        Code.Space = Space;
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
    private Emulator(Emulator parent, AppObject? reservedApp = null)
    {
        Parent = parent;
        Options = parent.Options;
        Vita = parent.Vita;
        Memory = parent.Memory;
        Space = Memory.CreateSpace("process"); // its own user address space (CDRAM and the kernel region are shared)
        Code.Space = Space;
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
        AppId = reservedApp != null ? Vita.ClaimApp(reservedApp, this) : Vita.RegisterApp(this);
        Vfs = new Vfs(Options.FsRoot);
        InitProcess();
    }

    /// <summary>The system's kernel process (<see cref="VitaSystem.KernelProcess"/>, pid <see cref="KernelSpace.KernelPid"/>):
    /// shares the guest memory, GPU, host devices and object table of <paramref name="root"/>, with its own space for
    /// its threads' stacks; it runs no executable and is not one of the system's application processes.</summary>
    private Emulator(bool kernelProcess, Emulator root)
    {
        IsKernelProcess = kernelProcess;
        Parent = root;
        Options = root.Options;
        Vita = root.Vita;
        Memory = root.Memory;
        Space = Memory.CreateSpace("kernel");
        Space.Label = "kernel";
        Code.Space = Space;
        Host = root.Host;
        Gpu = root.Gpu;
        LocalPsn = root.LocalPsn;
        TraceHle = root.TraceHle;
        TraceHleFrames = root.TraceHleFrames;
        TraceHleOnly = root.TraceHleOnly;
        TraceHleThreads = root.TraceHleThreads;
        TraceHleExclude = root.TraceHleExclude;
        BacktraceHle = root.BacktraceHle;
        Pid = KernelSpace.KernelPid;
        Vita.RegisterKernelProcess(this);
        Vfs = new Vfs(Options.FsRoot);
        Running = true;
        InitProcess();
        Log.Info("Kernel process created: the kernel modules of every process live in it");
    }

    /// <summary>The kernel process (<see cref="VitaSystem.KernelProcess"/>): the one kernel every process of the
    /// system shares, where the kernel modules live once. It never runs an executable, never ends before the system
    /// and is not in <see cref="VitaSystem.Processes"/>.</summary>
    public bool IsKernelProcess { get; }

    /// <summary>Creates the system's kernel process (<see cref="VitaSystem.KernelProcessFor"/>).</summary>
    internal Emulator CreateKernelProcess()
    {
        Emulator root = this;
        while (root.Parent != null)
        {
            root = root.Parent;
        }

        return new Emulator(kernelProcess: true, root);
    }

    /// <summary>The system ends: the kernel process's threads stop and what it holds goes back.</summary>
    internal void ReleaseKernelProcess()
    {
        Running = false;
        Kernel.StopAllThreads();
        if (!Kernel.WaitAllThreadsExit(5_000))
        {
            Log.Warn("kernel process: some threads did not stop in time");
        }

        Code.Clear();
        Memory.FreeOwnedBy(Pid);
        Memory.ReleaseSpace(Space);
    }

    /// <summary>Drops the translated code of a range: the kernel process's code (kernel memory) runs on the threads of
    /// every process too (a user thread calling a kernel module's syscall export, kernel code calling a hooked
    /// import), so a change there reaches every process's translation cache.</summary>
    internal void InvalidateCode(uint address, uint size)
    {
        if (!IsKernelProcess)
        {
            Code.Invalidate(address, size);
            return;
        }

        foreach (Emulator process in Vita.ProcessesAndKernel)
        {
            process.Code.Invalidate(address, size);
        }
    }

    /// <summary>Deletes a finished thread the kernel made for itself (module_start, process events) and gives back its
    /// stack and TLS.</summary>
    internal void DeleteFinishedKernelThread(GuestThread thread)
    {
        Kernel.DeleteThread(thread);
        Memory.Free(thread.StackAddr);
        Memory.Free(thread.TlsBase);
    }

    /// <summary>The kernel that holds the kernel modules: the system's kernel process (created now when needed).</summary>
    internal Emulator KernelHost => IsKernelProcess ? this : Vita.KernelProcessFor(this);

    /// <summary>Fresh kernel, linker and HLE state (process start, and again after sceAppMgrLoadExec).</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Kernel), nameof(Hle), nameof(Linker))]
    private void InitProcess()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(this);
        Kernel = new Kernel.Kernel(this);
        Hle = new HleRegistry(this);
        Linker = new Linker(Hle) { InvalidateCode = InvalidateCode, Log = Log.Warn };
        if (!IsKernelProcess)
        {
            // the kernel modules' syscall exports are the system kernel's, seen by every process
            Linker.SystemKernel = () => Vita.KernelProcess?.Linker;
        }

        _codeCache = IsKernelProcess || (Options.CodeCacheDir == null && !Options.PrecompileCode) ? null : new CodeCache(Options.CodeCacheDir, Code);
        if (_codeCache != null)
        {
            HleGuestCode.CodePlaced += OnHleGuestCodePlaced;
        }
        Kernel.Initialize();
        // every concrete HleModule of the core (generated table), in full type name order as before
        foreach (HleModuleEntry? hleModuleEntry in HleBindings.Modules.OrderBy(m => m.Type.FullName))
        {
            Hle.RegisterModule(hleModuleEntry.Create());
        }
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
        if (suspendCurrent && MainModule != null)
        {
            SuspendedPrograms.Push((AppPath, CurrentSelf));
        }

        QueueExec("app0:" + self, args, appDir);
    }

    /// <summary>
    /// Raised once when the last process ends by itself (sceKernelExitProcess, exit()), with its exit status: on the
    /// Vita the system takes the screen back; when no shell runs the session is over, so the host closes the game's
    /// window (desktop), ends the run (headless) or leaves the game screen (Android). Not raised when the shell
    /// launched the process (the shell comes back) or a suspended program resumes. Called on the exiting guest thread.
    /// </summary>
    public event Action<int>? ProcessExited;

    /// <summary>The exit status of the last process once it ended by itself (<see cref="ProcessExited"/>), else null.</summary>
    public int? LastProcessExitStatus { get; private set; }

    private int _lastProcessEnded;

    /// <summary>The process ends (sceKernelExitProcess, exit()): a process the shell started ends and the shell takes the
    /// display back, a suspended program is resumed, or, when this was the last process, the emulator stops and
    /// <see cref="ProcessExited"/> tells the host the session is over.</summary>
    public void EndProcess(int status)
    {
        if (Parent != null)
        {
            _exitStatus = status;
            _exiting = true;
            Terminate($"exited (0x{status:X})");
            return;
        }
        if (SuspendedPrograms.Count > 0)
        {
            (string? dir, string? self) = SuspendedPrograms.Pop();
            Log.Info($"Process exited (0x{status:X}): resuming {dir} ({self})");
            QueueExec("app0:" + self, [], dir);
            return;
        }
        if (Interlocked.Exchange(ref _lastProcessEnded, 1) != 0)
        {
            return; // another thread of the process already ended it
        }

        LastProcessExitStatus = status;
        Log.Info($"Process exited (0x{status:X}): it was the last process, the session ends");
        TellProcessEnd(ProcessEvents.Exit);
        Stop();
        ProcessExited?.Invoke(status);
    }

    private void QueueExec(string path, string[] args, string? appDir)
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
                try
                {
                    PerformExec(null);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Log.Error($"{Describe()}: LoadExec failed: {e}");
                    Terminate("LoadExec failed");
                }
            });
            return;
        }
        ExecRequested?.Invoke();
    }

    // ------------------------------------------------------------------ several processes (the shell and an application)

    /// <summary>
    /// Creates a new process next to this one for the executable <paramref name="self"/> of the application in
    /// <paramref name="appDir"/> (the shell launching a game from its LiveArea). As in the console's appmgr, whose launch
    /// creates the process and loads its executable before it returns (FW 3.74 shell.self: the launch job's error goes to
    /// the shell's launch-error dialog, 0x8129456C), the executable is loaded before this returns: an executable that
    /// cannot be loaded (not a SELF, a missing module, no licence to decrypt it) throws <see cref="ProcessLoadException"/>
    /// with the module manager's error, and no process or application id is left behind and the shell gets no event.
    /// The module_start functions and the main thread then run on a host thread. The process takes the display when the
    /// shell activates it (sceAppMgrActivateApp), or with <paramref name="activate"/>. It runs in its own address space
    /// (<see cref="Memory.GuestMemory.CreateSpace"/>), so an executable linked at the shell's addresses runs too; only
    /// when every process shares one space (VITASHARP_SHARED_ADDRESS_SPACE=1, or a host that cannot map memory twice) can
    /// such an executable not be placed: then it ends and <paramref name="cannotPlace"/> runs instead.
    /// <paramref name="processTitleId"/> is the title the launch names for the process, when it names one
    /// (<see cref="LaunchedTitleId"/>).
    /// </summary>
    public Emulator SpawnProcess(string appDir, string self, string[] args, bool activate = false, Action? cannotPlace = null, bool background = false,
        AppObject? reservedApp = null, Action<Emulator>? started = null, string? processTitleId = null)
    {
        Gpu.SharedByProcesses = true; // from now on GPU scenes are arbitrated between the processes
        var child = new Emulator(this, reservedApp) { _background = background };
        _children.Add(child);
        child.TitleId = Path.GetFileName(Path.TrimEndingDirectorySeparator(appDir));
        if (!string.IsNullOrEmpty(processTitleId) && !processTitleId.Equals(child.TitleId, StringComparison.OrdinalIgnoreCase))
        {
            child.LaunchedTitleId = processTitleId;
        }
        if (!ReferenceEquals(child.Space, Space))
        {
            child.Space.Label = child.Describe();
        }

        child.Running = true;
        // the executable loads on a host thread (its own process scope); the caller's guest thread gives its core back
        // while it waits, as a guest thread blocked in the kernel does
        Exception? loadError = null;
        var load = Task.Run(() =>
        {
            try
            {
                child.LoadTitle(appDir, self);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                loadError = e;
            }
        });
        using (VitaSharp.Core.Kernel.Scheduler.Block())
        {
            load.Wait();
        }

        if (loadError is InvalidOperationException placement && placement.Message.Contains("cannot map fixed segment") && cannotPlace != null)
        {
            Log.Warn($"{child.Describe()}: {placement.Message}; it cannot run next to {Describe()}");
            child.Discard("could not be placed");
            Task.Run(cannotPlace);
            return child;
        }

        if (loadError != null)
        {
            int error = ModuleLoadError(loadError);
            Log.Error($"Process {child.Describe()} could not start (0x{error:X8}): {loadError}");
            child.Discard("failed to load");
            throw new ProcessLoadException(error, loadError);
        }

        // SceAppMgr tells the shell about the new application process: event 2 {pid, application id}
        child._announced = true;
        if (Vita.Shell != null)
        {
            Vita.PostShellEvent(ShellEvents.AppCreated, child.Pid, child.AppId);
        }

        Task.Run(() =>
        {
            try
            {
                Log.Info($"Process {child.Describe()} started by {Describe()}");
                child.Start(args);
                // the launch finished: event 0xA {application id}; the shell then activates it
                if (Vita.Shell != null && !background)
                {
                    Vita.PostShellEvent(ShellEvents.AppLaunched, child.AppId);
                }

                if (activate)
                {
                    Vita.SetForeground(child);
                }

                started?.Invoke(child);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Error($"Process {child.Describe()} could not start: {e}");
                child.Terminate("failed to start");
                if (Vita.Shell != null)
                {
                    Vita.PostShellEvent(ShellEvents.AppLaunched, -1);
                }
            }
        });
        return child;
    }

    /// <summary>
    /// The module manager's error for an executable or module that could not be loaded: a missing file
    /// 0x8002D01C (SCE_KERNEL_ERROR_MODULEMGR_NOENT), a file that is no SELF 0x80025002 (ILLEGAL_SELF_HEADER), anything
    /// else the loader refuses (no licence to decrypt it, a bad ELF) 0x80025001 (ILLEGAL_ELF_HEADER). An application
    /// launched without its license: appmgr's 0x80800004 (<see cref="TitleLicense.NoLicenseError"/>).
    /// </summary>
    public static int ModuleLoadError(Exception e)
    {
        if (e is TitleLicenseMissingException noLicense)
        {
            return noLicense.Error;
        }

        if (e is TitleKeyException noKey)
        {
            return noKey.Error;
        }

        if (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return SceError.MODULEMGR_NOENT;
        }

        if (e is InvalidDataException && e.Message.StartsWith("Not a SELF", StringComparison.Ordinal))
        {
            return SceError.ILLEGAL_SELF_HEADER;
        }

        return SceError.ILLEGAL_ELF_HEADER;
    }

    /// <summary>Whether <paramref name="e"/> is the loader refusing a module (a missing file, a file that is no SELF, an
    /// encrypted module without its license): the module manager returns <see cref="ModuleLoadError"/> for it.</summary>
    public static bool IsModuleLoadFailure(Exception e) =>
        e is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or SelfLicenseMissingException or TitleLicenseMissingException
        or TitleKeyException;

    /// <summary>A process whose executable never ran (its load failed): it goes at once, without the end event a started
    /// application gives the shell (the shell never heard of it).</summary>
    private void Discard(string why)
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0)
        {
            return;
        }

        Log.Info($"Process {Describe()} {why}");
        Running = false;
        Kernel.StopAllThreads();
        Release();
    }

    private readonly List<Emulator> _children = new();
    private int _terminated, _exitStatus;
    private bool _background, _endedInFront;

    /// <summary>The process ends by itself (sceKernelExitProcess): the handlers hear exit, not kill.</summary>
    private bool _exiting;

    /// <summary>The kernel's process event handlers were told the process was created (once per process: a LoadExec
    /// keeps the process).</summary>
    private bool _toldCreated;

    /// <summary>Orders the create event before the end event (processmgr holds the process's lock across both): a
    /// process ended while its create event runs hears the end once the create returned.</summary>
    private readonly object _processEventGate = new();

    /// <summary>
    /// The create event (<see cref="ProcessEvents"/>): processmgr invokes it once the process's main thread exists,
    /// before the process starts. A handler that fails refuses the process: the handlers called before it hear that it
    /// exits, and the process does not start (<see cref="ProcessLoadException"/> with the handler's error).
    /// </summary>
    private void TellProcessCreated(GuestThread main)
    {
        if (IsKernelProcess)
        {
            return;
        }

        lock (_processEventGate)
        {
            if (_toldCreated)
            {
                return;
            }

            int result = InvokeProcessEvent(ProcessEvents.Create, stopAtFailure: true, stopAt: 0, out int refusedBy);
            if (refusedBy != 0)
            {
                InvokeProcessEvent(ProcessEvents.Exit, stopAtFailure: false, stopAt: refusedBy, out _);
                Kernel.DeleteThread(main);
                Log.Warn($"{Describe()}: process event handler 0x{refusedBy:X} refused the process (0x{result:X8})");
                throw new ProcessLoadException(result, new InvalidOperationException($"a kernel process event handler refused the process (0x{result:X8})"));
            }

            _toldCreated = true;
        }
    }

    /// <summary>A stop or start event of type <paramref name="eventType"/>, for a process the handlers were told was
    /// created.</summary>
    private void TellProcessState(int eventId, int eventType)
    {
        lock (_processEventGate)
        {
            if (_toldCreated)
            {
                InvokeProcessEvent(eventId, stopAtFailure: false, stopAt: 0, out _, eventType);
            }
        }
    }

    /// <summary>A switch event (<see cref="ProcessEvents.SwitchIn"/>/<see cref="ProcessEvents.SwitchOut"/>) for an
    /// application process the handlers were told was created; a system program's process sends none.</summary>
    private void TellApplicationSwitch(int eventId, uint state)
    {
        if (IsSystemProgram)
        {
            return;
        }

        lock (_processEventGate)
        {
            if (_toldCreated)
            {
                InvokeProcessEvent(eventId, stopAtFailure: false, stopAt: 0, out _, 0, [0x14, (uint)Pid, state, 0, ProcessEvents.ApplicationType]);
            }
        }
    }

    /// <summary>The exit or kill event, for a process the handlers were told was created.</summary>
    private void TellProcessEnd(int eventId)
    {
        lock (_processEventGate)
        {
            if (!_toldCreated)
            {
                return;
            }

            _toldCreated = false;
            InvokeProcessEvent(eventId, stopAtFailure: false, stopAt: 0, out _);
        }
    }

    /// <summary>
    /// Calls the kernel's process event handlers about this process (<see cref="ProcessEvents.Run"/>), on a kernel
    /// thread of the kernel process, with the parameter block processmgr passes: its size word (0x14 for create, 0x10
    /// otherwise), then fields whose meaning is not known (0 here). Returns what the last handler returned.
    /// </summary>
    internal int InvokeProcessEvent(int eventId, bool stopAtFailure, int stopAt, out int failedUid, int eventType = 0, uint[]? paramWords = null)
    {
        failedUid = 0;
        List<KernelData> handlers = ProcessEvents.Handlers(Vita.Objects);
        if (handlers.Count == 0)
        {
            return 0;
        }

        Emulator kernel = KernelHost;
        uint param;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            param = Memory.Alloc(0x20, "procevent:param", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(param, 0, 0x20);
        GuestMemory.W32(param, eventId == ProcessEvents.Create ? ProcessEvents.CreateParamSize : ProcessEvents.ParamSize);
        if (paramWords != null)
        {
            for (int i = 0; i < paramWords.Length; i++)
            {
                GuestMemory.W32(param + (uint)i * 4, paramWords[i]);
            }
        }
        int result = 0, failed = 0;
        RunOnKernelThread(kernel, "SceProcessEvent", self =>
        {
            result = ProcessEvents.Run(self, handlers, Pid, eventId, eventType, param, stopAtFailure, stopAt, out failed);
        }, $"the process event handlers (event {eventId})");

        Memory.Free(param);
        failedUid = failed;
        return result;
    }

    /// <summary>Runs <paramref name="body"/> on a new kernel thread <paramref name="threadName"/> of the kernel process
    /// <paramref name="kernel"/> (where processmgr runs its calls into other kernel modules) and waits for it; the
    /// thread goes once it returned. <paramref name="what"/> names the call in the warning when it does not return.</summary>
    private void RunOnKernelThread(Emulator kernel, string threadName, Action<GuestThread> body, string what)
    {
        using (ProcessScope.Enter(kernel))
        {
            GuestThread thread = kernel.Kernel.CreateThread(threadName, 0, 0x40, 0x4000, 0, 0x70000);
            thread.KernelMode = true;
            thread.HostBody = body;
            thread.Start(0, 0);
            bool finished;
            using (VitaSharp.Core.Kernel.Scheduler.Block())
            {
                finished = thread.WaitForExit(30_000);
            }

            if (finished)
            {
                kernel.DeleteFinishedKernelThread(thread);
            }
            else
            {
                Log.Warn($"{Describe()}: {what} did not return in time");
            }
        }
    }

    /// <summary>SceModulemgrForKernel's library NID from FW 3.65 on (the emulated firmware is 3.74): a fixed value of
    /// the firmware's export table.</summary>
    internal const uint ModulemgrForKernelLibNid = 0x92C9FFC2;

    /// <summary>ksceKernelStartPreloadingModules(pid) of <see cref="ModulemgrForKernelLibNid"/>.</summary>
    internal const uint StartPreloadingModulesNid = 0x998C7AE9;

    /// <summary>
    /// processmgr 3.74 starts a new process's preloaded modules by calling modulemgr's export
    /// ksceKernelStartPreloadingModules(pid) by its address, once the process's main thread exists and before the
    /// process start event (decompiled processmgr.skprx, study only). VitaSharp starts those modules itself
    /// (<see cref="Start"/>), and the HLE of the export only answers, so the call is made through the export's entry
    /// only when kernel code hooked it (a kernel plugin, as the PSP emulator's loader of a homebrew environment does to
    /// add its module to a process): the hook runs on a kernel thread with the process's pid, its original part
    /// reaching the HLE. Without a hook the call is the HLE's answer, and nothing runs.
    /// </summary>
    private void StartPreloadingModulesThroughModulemgr()
    {
        if (IsKernelProcess || Vita.KernelProcess is not { } kernel)
        {
            return;
        }

        FirmwareKernelModules firmware = kernel.FirmwareKernel;
        if (!firmware.IsPatched(ModulemgrForKernelLibNid, StartPreloadingModulesNid))
        {
            return;
        }

        int result = 0;
        uint pid = (uint)Pid;
        RunOnKernelThread(kernel, "SceProcessStartPreload", _ =>
        {
            result = (int)firmware.Call(ModulemgrForKernelLibNid, StartPreloadingModulesNid, () => 0, pid);
        }, "ksceKernelStartPreloadingModules");
        if (result < 0)
        {
            Log.Warn($"{Describe()}: ksceKernelStartPreloadingModules returned 0x{result:X8}");
        }
    }

    /// <summary>The process's executable loaded and the system was told about it (a process whose load failed never was).</summary>
    private bool _announced;

    /// <summary>Ends this process (exit, crash, or killed by the shell): stops its threads, then frees its memory and kernel
    /// objects and gives the display back. The threads are joined on a host thread, so this can be called from one of them.</summary>
    public void Terminate(string why)
    {
        if (IsKernelProcess)
        {
            return; // the kernel ends only with the system (VitaSystem.EndKernelProcess)
        }

        if (Parent == null)
        {
            Stop();
            return;
        }
        if (Interlocked.Exchange(ref _terminated, 1) != 0)
        {
            return;
        }

        Log.Info($"Process {Describe()} {why}");
        // processmgr tells the kernel's process event handlers before the process's threads go
        TellProcessEnd(_exiting ? ProcessEvents.Exit : ProcessEvents.Kill);
        Running = false;
        Vita.Power.Callbacks.RemoveProcess(this);
        // the screen goes back to the parent (the shell) at once, not after the threads are gone (that can take seconds)
        _endedInFront = ReferenceEquals(Vita.Foreground, this);
        if (Parent is { } parent && _endedInFront)
        {
            Vita.SetForeground(parent);
        }

        Kernel.StopAllThreads();
        ForgetPluginPatches();
        foreach (Emulator c in _children.ToArray())
        {
            c.Terminate("ended with its parent");
        }

        Task.Run(() =>
        {
            if (!Kernel.WaitAllThreadsExit(10_000))
            {
                Log.Warn($"{Describe()}: some threads did not stop in time");
                using (AddressSpace.Use(Space))
                {
                    foreach (GuestThread? t in Kernel.Threads.ToList())
                    {
                        if (!t.WaitForExit(0))
                        {
                            Log.Warn($"  still running: {t.Describe()}");
                        }
                    }
                }
            }
            Release();
        });
    }

    /// <summary>Gives everything the process holds back to the system.</summary>
    private void Release()
    {
        // SceAppMgr tells the shell the application is gone: event 4 {+4 pid, +0x14 application id}. Not for a background
        // service: NPXS10079 (daily checker) exits at once here, where on the Vita it stays resident, and the shell would
        // start it again on every end
        if (Vita.Shell is { } shell && shell != this && !_background && _announced)
        {
            Vita.PostAppEnded(Pid, AppId, _endedInFront ? 2 : 4, TitleId);
        }

        Vita.Unregister(this);
        Parent?._children.Remove(this);
        // what the GPU made of its memory goes with it: its address space and the CDRAM blocks it held (freed below)
        Gpu.ReleaseScene(Pid, Space, Memory.SharedBlocksOwnedBy(Pid));
        // the kernel modules stay: they are the kernel process's, which outlives every process
        int objects = Vita.Objects.RemoveOwnedBy(Pid);

        // the GPU finishes the process's commands before its memory goes (bounded: a stuck GPU must not keep the space)
        if (!Task.Run(Gpu.WaitIdle).Wait(5000))
        {
            Log.Warn($"{Describe()}: the GPU did not finish its work in time");
        }

        long bytes = Memory.FreeOwnedBy(Pid);
        _codeCacheTimer?.Dispose();
        SaveCodeCache(stop: true);
        Code.Clear();
        Memory.ReleaseSpace(Space);
        Log.Info($"Process {Describe()} released: {objects} kernel objects, {bytes / 1024} KB of memory");
        Released?.Invoke();
    }

    /// <summary>The process is gone (its memory and kernel objects released).</summary>
    public event Action? Released;

    /// <summary>The process is suspended: its threads stop at their next safe point (see <see cref="GuestThread"/>).</summary>
    public volatile bool Suspended;

    /// <summary>This process's BGM priority in the kernel's BGM routing (0 = none; <see cref="Kernel.VitaSystem.BgmHolder"/>).</summary>
    public volatile int BgmPriority;

    /// <summary>The thread of this process that submitted a GPU BeginScene not yet followed by its EndScene (any thread
    /// of the process may end it). While the emulation is paused that thread runs on until the scene is closed, so the
    /// GPU finishes the frame (<see cref="EmulationPause"/>).</summary>
    public GuestThread? OpenSceneThread
    {
        get => Volatile.Read(ref _openSceneThread);
        set
        {
            GuestThread? old = Interlocked.Exchange(ref _openSceneThread, value);
            // the scene is closed during a pause: that thread parks at its next block boundary too
            if (old != null && old != value && EmulationPause.IsPaused)
            {
                old.Core.InterruptRequested = true;
            }
        }
    }
    private GuestThread? _openSceneThread;

    /// <summary>The emulation was paused or resumed: the host audio output follows (first process only: the devices are
    /// its own).</summary>
    private void OnPauseChanged(bool paused)
    {
        try
        {
            Host.Audio?.SetPaused(paused);
        }
        catch (Exception e)
        {
            Log.Warn($"Audio: could not {(paused ? "pause" : "resume")} the output: {e.Message}");
        }
    }

    /// <summary>Suspends the process (the shell took the screen back: the application waits in the background).</summary>
    public void Suspend()
    {
        if (Suspended)
        {
            return;
        }

        // processmgr's suspend: stop events before the threads stop (types 0x200 and 0x800) and after (0x1000)
        TellProcessState(ProcessEvents.Stop, ProcessEvents.StopBegin);
        TellProcessState(ProcessEvents.Stop, ProcessEvents.StopThreads);
        Suspended = true;
        foreach (GuestThread t in Kernel.Threads)
        {
            t.Core.InterruptRequested = true;
        }

        TellProcessState(ProcessEvents.Stop, ProcessEvents.StopDone);
        TellApplicationSwitch(ProcessEvents.SwitchOut, ProcessEvents.StateSuspended);
        Log.Info($"Process {Describe()} suspended");
    }

    public void Resume()
    {
        if (!Suspended)
        {
            return;
        }

        // processmgr's resume: start events before the threads run again (type 0x20000) and after (0x40000)
        TellProcessState(ProcessEvents.Start, ProcessEvents.StartBegin);
        Suspended = false;
        foreach (GuestThread t in Kernel.Threads)
        {
            t.WakeFromSuspend();
        }

        TellProcessState(ProcessEvents.Start, ProcessEvents.StartDone);
        TellApplicationSwitch(ProcessEvents.SwitchIn, ProcessEvents.StateRunning);

        Log.Info($"Process {Describe()} resumed");
    }

    /// <summary>"TITLEID (pid 0x...)" for logs.</summary>
    public string Describe() => $"{(TitleId.Length > 0 ? TitleId : "process")} (pid 0x{Pid:X})";

    /// <summary>The system made this process the foreground one: what it last displayed goes back on screen.</summary>
    internal void OnForeground()
    {
        // called on the thread that switched the foreground (the shell's): the frame is in this process's memory
        using AddressSpace.Activation space = AddressSpace.Use(Space);
        foreach (Display d in Hle.Modules.OfType<Hle.Modules.Display>())
        {
            d.Represent();
        }
    }

    /// <summary>This process is the one on screen (or the only one): its frames are shown and it gets the input.</summary>
    public bool IsForeground => Vita.IsForeground(this);

    /// <summary>The shell's executable in vs0:vsh/shell.</summary>
    public const string ShellSelf = "shell.self";

    /// <summary>The main executable of the running program, relative to its folder.</summary>
    public string CurrentSelf { get; private set; } = "eboot.bin";

    /// <summary>The launch parameter the system gives the title loaded by <see cref="LoadTitle"/> (a PSM title's folder
    /// for its runtime), read with sceAppMgrGetAppParam; null for none.</summary>
    private string? _launchParam;

    /// <summary>Replaces the process image: waits for the old threads, resets kernel, memory and GPU state and starts
    /// the requested executable in the same address space (like the Vita kernel does for the same application).</summary>
    public void PerformExec(Func<Gpu.IGpuBackend?>? backendFactory)
    {
        (string? path, string[]? args, string? appDir) = ExecRequest ?? throw new InvalidOperationException("no exec pending");
        Execing = true;
        try
        {
            ExecCore(backendFactory, path, args, appDir);
        }
        finally
        {
            Execing = false;
        }
    }

    /// <summary>A LoadExec is being performed: the old process has stopped (<see cref="Running"/> is false, and
    /// <see cref="ExecRequest"/> is already cleared while the new executable loads and its modules start), but the
    /// program has not ended. Front-ends that watch for the program ending must not take this for it (the Android
    /// game screen showed "O jogo terminou" and paused the emulation during Uncharted's exec).</summary>
    public volatile bool Execing;

    private void ExecCore(Func<Gpu.IGpuBackend?>? backendFactory, string path, string[] args, string? appDir)
    {
        if (!Kernel.WaitAllThreadsExit(10_000))
        {
            Log.Warn("LoadExec: some threads did not stop in time");
            using (AddressSpace.Use(Space))
            {
                foreach (GuestThread? t in Kernel.Threads.ToList())
                {
                    if (!t.WaitForExit(0))
                    {
                        Log.Warn($"  still running (core {t.HasCore}, blocked in host {t.BlockedInHost}, in guest code {t.InGuestCode}): {t.Describe()}");
                    }
                }
            }
        }
        bool alone = Vita.Processes.Count == 1;
        if (Parent == null)
        {
            Gpu.ReplaceBackend(backendFactory); // the GPU is the system's: only the front-end's process replaces it
        }

        if (alone)
        {
            VitaSharp.Core.Util.AudioDiagnostics.ProcessEnded(); // the old process's ports and decoder streams are gone
        }

        SaveCodeCache(stop: true);
        Code.Clear();
        if (alone)
        {
            // the kernel (its modules, memory blocks, heaps and threads) outlives the LoadExec
            Emulator? kernelProcess = Vita.KernelProcess;
            Memory.FreeAllExcept(kernelProcess != null ? KernelSpace.KernelPid : null);
            Vita.Objects.RemoveOwnedBy(0);
            if (kernelProcess == null || !kernelProcess.Kernel.Threads.Any(t => t.IsLive))
            {
                VitaSharp.Core.Kernel.Scheduler.Reset();
            }
        }
        else
        {
            Memory.FreeOwnedBy(Pid);
        }
        // the space now holds another executable: what was cached per use of it is stale (the HLE guest-code routines
        // placed by the old image: Jak and Daxter Collection's menu left them where Jak1.self's code now is, and its
        // sceGxm imports jumped into the middle of a Thumb function)
        Space.Renew();
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
        using ProcessScope.Scope scope = ProcessScope.Enter(this);
        // a PSM title is started as the system starts it: its runtime with the title's folder as the launch parameter
        _launchParam = null;
        if (Package.InstalledContent.IsPsmTitleFolder(Options.FsRoot, appPath))
        {
            (string ProgramDir, string Self, string Param) psm = Package.InstalledContent.PsmLaunch(Options.FsRoot, appPath)
                ?? throw new FileNotFoundException("The PSM title needs the PSM Runtime Package, which is not installed");
            Log.Info($"PSM title {Path.GetFileName(Path.TrimEndingDirectorySeparator(appPath))}: {psm.ProgramDir} ({psm.Self}) with \"{psm.Param}\"");
            appPath = psm.ProgramDir;
            self = psm.Self;
            _launchParam = psm.Param;
        }
        AppPath = Path.GetFullPath(appPath);
        CurrentSelf = self;
        Vfs.ClearApp0Overlays(); // a process switch brings another application's patch folder
        Vfs.Mount("app0", AppPath);
        string sfoPath = Vfs.ResolveCaseInsensitive(AppPath, "sce_sys/param.sfo");
        if (File.Exists(sfoPath))
        {
            ParamSfo = Sfo.Load(sfoPath);
            TitleId = ParamSfo.TitleId;
            Title = ParamSfo.Title;
        }
        else
        {
            TitleId = Path.GetFileName(AppPath);
        }

        // an installed update overlays app0 where appmgr mounts it (ux0:patch for ux0:app, grw0:patch for the game
        // card): every file it has replaces the application's, its sce_sys/param.sfo too, so the application sees the
        // update's version (APP_VER) and everything else the update changed
        if (Vfs.OverlayApp0Patch(AppPath, TitleId) is { } patchedSfo)
        {
            ParamSfo = patchedSfo;
            Title = ParamSfo.Title;
        }

        string saveDir = Io.FirmwareLayout.SaveData.Host(Options.FsRoot, TitleId);
        Vfs.MountSaveData("savedata0", saveDir);
        Vfs.Mount("addcont0", Io.FirmwareLayout.AddCont.Host(Options.FsRoot, TitleId));

        Klicensee = FindKlicensee();
        OpenApp0Image();
        string vs0 = Path.GetFullPath(Path.Combine(Options.FsRoot, "vs0")) + Path.DirectorySeparatorChar;
        IsSystemProgram = AppPath.StartsWith(vs0, StringComparison.OrdinalIgnoreCase);
        _startFirst.Clear();
        RunsOnSystemCore = IsSystemProgram && string.Equals(self, ShellSelf, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.TrimEndingDirectorySeparator(AppPath)).Equals("shell", StringComparison.OrdinalIgnoreCase);
        // the shell starting = the system software coming up with the memory card inserted: titles VitaSharp installed
        // (or that were copied into ux0:app) get the appmeta the Vita's installer leaves, for the shell's database
        if (RunsOnSystemCore)
        {
            Package.AppMeta.Sync(Path.Combine(Options.FsRoot, "ux0"), Log.Info);
        }

        Log.Info($"Loading {TitleId} \"{Title}\" from {AppPath}");
        Boot.BootProgress.Report(Boot.BootStep.Game);
        // per-title shader/pipeline caches start warming up while the executable loads
        try
        {
            if (Gpu.Backend != null)
            {
                // the loading screen's "Carregando o cache de shaders": the driver's pipeline cache and the title's
                // shader cache are read here, before the recorded shaders are built (its own progress)
                Boot.BootProgress.Report(Boot.BootStep.ShaderCache);
            }

            Gpu.Backend?.BeginTitle(TitleId);
            if (Options.LoadShaderCacheBeforeStart)
            {
                WaitForShaderCache();
            }
        }
        catch (Exception e)
        {
            Log.Warn($"GPU backend title setup failed: {e.Message}");
        }

        if (Vfs.Resolve("app0:" + self) is { } selfHost && File.Exists(selfHost))
        {
            using FileStream fs = File.OpenRead(selfHost);
            byte[] head = new byte[Math.Min(fs.Length, 0x1000)];
            fs.ReadExactly(head);
            ProgramAuthorityId = SelfDecryptor.ProgramAuthorityId(head);
            // the capability of the auth info comes from the secure processor, which is not emulated: the access
            // follows what the program is
            Access = ProgramAccess.Of(ProgramAuthorityId, IsSystemProgram, SelfDecryptor.RequiresKlicensee(head));
        }
        else
        {
            Access = ProgramAccess.Of(0, IsSystemProgram, retailApplication: false);
        }
        try
        {
            MainModule = LoadModule("app0:" + self);
        }
        catch (SelfLicenseMissingException noLicense)
        {
            // appmgr reads the title's license before it creates the process: without it the launch fails with its
            // error (the shell's launch-error dialog), the process never starts
            throw new TitleLicenseMissingException(TitleId, noLicense);
        }
        if (IsSystemProgram)
        {
            Log.Info($"System program {MainModule.Name}, authority ID 0x{ProgramAuthorityId:X16}");
        }

        Loader.TitlePatches.Apply(TitleId, Options.PatchDir);
        if (MainModule.TlsInit != 0 || MainModule.TlsAreaSize != 0)
        {
            Kernel.TlsInit = MainModule.TlsInit;
            Kernel.TlsInitSize = MainModule.TlsInitSize;
            Kernel.TlsAreaSize = MainModule.TlsAreaSize;
        }
        Kernel.ProcessParam = Linker.ProcessParam;

        // Preloaded system libraries that the game ships itself (LLE).
        uint preloadDisabled = ProcessParamField(0x24);
        foreach ((uint bit, string? name) in new[] { (0x00010000u, "libc"), (0x00200000u, "libfios2"), (0x00800000u, "libSceFt2"), (0x01000000u, "libpvf") })
        {
            // the HLE-modules option keeps these on HLE too (VITASHARP_HLE_MODULES=libc,libfios2: the C library and
            // FIOS2 of the HLE instead of the game's own copies)
            if ((preloadDisabled & bit) != 0 || KeepOnHle(name))
            {
                continue;
            }

            string? host = Vfs.Resolve($"app0:sce_module/{name}.suprx");
            if (host != null && File.Exists(host))
            {
                LoadModule($"app0:sce_module/{name}.suprx");
            }
        }
        if (!IsSystemProgram)
        {
            PreloadFirmwareModulesForGame(preloadDisabled);
        }

        if (IsSystemProgram)
        {
            PreloadFirmwareModules(preloadDisabled);
            // the executable's own imports from firmware libraries nothing loaded provides (the shell calls
            // SceNpCommonPs4 in its first initialisation, before it loads any module that would bring it). Modules
            // that need an on-request module (libpaf) wait for the program's own sysmodule load, as before.
            _startFirst.AddRange(LoadFirmwareDependencies(MainModule, withoutOnRequest: true));
        }

        // with the emulator option "Enable Kernel Plugins" on, the plugins of every process, the system's too (each
        // process gets its plugins after its preloaded modules, the shell those of *main, a system application those of
        // its NPXS ID; the *KERNEL ones load once, with the first process); off, the pure firmware: no plugin
        LoadPluginsIfEnabled();
        // a system program's variable imports from firmware modules it loads itself later through sysmodule (ScePaf*
        // from libpaf, SceCommonGuiDialog's class tables...) are bound when that module loads; only the others are missing
        FirmwareLibraries? firmware = IsSystemProgram ? Firmware.FirmwareLibraries.For(Options.FsRoot) : null;
        int later = 0;
        lock (_pendingVariables)
        {
            _pendingVariables.Clear();
        }

        var waiting = new SortedSet<string>();
        foreach (uint nid in Linker.UnresolvedVariables())
        {
            string source = Linker.VariableImportSource(nid), lib = LibraryOf(source);
            if (firmware?.Provider(lib) != null)
            {
                later++;
                continue;
            }
            // a library no loaded module exports yet: the program may load its provider itself (Unity's
            // UnityPlayer imports variables of app0:Media/Modules/Il2CppAssemblies.suprx, loaded ~0.6 s later);
            // LoadModule reports what is still missing once a module exporting that library has loaded
            if (!Kernel.Modules.Values.Any(m => m.Exports.Any(e => e.Library == lib)))
            {
                lock (_pendingVariables)
                {
                    _pendingVariables.Add(nid);
                }

                waiting.Add(lib);
                continue;
            }
            WarnUnresolvedVariable(nid, source);
        }
        if (later > 0)
        {
            Log.Info($"{later} variable imports wait for firmware modules the program loads itself (libpaf, common_gui_dialog...)");
        }

        if (waiting.Count > 0)
        {
            Log.Info($"{_pendingVariables.Count} variable imports wait for a module exporting {string.Join(", ", waiting)}: " +
                string.Join(" ", _pendingVariables.Select(n => $"{n:X8}")));
        }
    }

    /// <summary>Variable imports from libraries no module exported when the executable loaded (see LoadTitle).</summary>
    private readonly HashSet<uint> _pendingVariables = new();

    private static string LibraryOf(string source) => source.Split(' ')[0];

    private static void WarnUnresolvedVariable(uint nid, string source) =>
        Log.Warn($"Unresolved variable import NID {nid:X8} ({NidDatabase.Lookup(nid)?.Name ?? "unknown"}) from {source}");

    /// <summary>After <paramref name="loadedModule"/> loaded: the waiting variable imports it resolved are done, and the ones from a
    /// library it exports that it did not resolve are reported.</summary>
    private void CheckPendingVariables(LoadedModule loadedModule)
    {
        lock (_pendingVariables)
        {
            if (_pendingVariables.Count == 0)
            {
                return;
            }

            var unresolved = Linker.UnresolvedVariables().ToHashSet();
            int resolved = 0;
            foreach (uint nid in _pendingVariables.ToArray())
            {
                string source = Linker.VariableImportSource(nid);
                if (!unresolved.Contains(nid))
                {
                    _pendingVariables.Remove(nid);
                    resolved++;
                    continue;
                }
                if (!loadedModule.Exports.Any(e => e.Library == LibraryOf(source)))
                {
                    continue;
                }

                _pendingVariables.Remove(nid);
                WarnUnresolvedVariable(nid, source);
            }
            if (resolved > 0)
            {
                Log.Info($"{loadedModule.Name}: {resolved} waiting variable imports bound");
            }
        }
    }

    /// <summary>The running executable is part of the system software (vs0:app/NPXS*, vs0:vsh/shell/shell.self...):
    /// it runs with system privileges and uses the firmware's own user-mode libraries (LLE) where VitaSharp's HLE
    /// stops: the process preloads, the sysmodules it loads and the libraries those import.</summary>
    public bool IsSystemProgram { get; internal set; }

    /// <summary>The process is the shell (vs0:vsh/shell/shell.self): its threads run on the Vita's 4th core, the one
    /// the system keeps for itself, instead of the three application cores (<see cref="Kernel.Scheduler"/>).</summary>
    public bool RunsOnSystemCore { get; internal set; }

    /// <summary>The title ID the kernel gives the shell's process (ksceKernelGetProcessTitleId; "*main" of the plugin configuration).</summary>
    public const string ShellProcessTitleId = "main";

    /// <summary>The process's title ID as the kernel keeps it (sceKernelGetProcessTitleId, the plugin configuration's
    /// sections): <see cref="ShellProcessTitleId"/> for the shell, the title the launch named when it named one
    /// (<see cref="LaunchedTitleId"/>), the application's own otherwise.</summary>
    public string ProcessTitleId => RunsOnSystemCore ? ShellProcessTitleId : LaunchedTitleId ?? TitleId;

    /// <summary>The title ID the launch gave the process when it differs from the executable's application: the shell
    /// starts the firmware's PSP emulator (vs0:app/NPXS10028/eboot.bin) by sceAppMgrLaunchAppByPath4 with the title of
    /// the bubble it opens, and the kernel knows the emulator's process by that title (a PSP title's ID, or that of
    /// the Vita application whose bubble the shell takes for a PSP one). Null: the application's own.</summary>
    public string? LaunchedTitleId { get; private set; }

    /// <summary>What SceSblACMgr answers about the main executable (system, non-game, PSM program).</summary>
    public ProgramAccess Access { get; internal set; } = ProgramAccess.None;

    /// <summary>Program authority ID of the main executable's SELF (0 for a plain ELF).</summary>
    public ulong ProgramAuthorityId { get; private set; }

    /// <summary>SceSblACMgr's "game program" class: games (authority 0x2F...) and plain/homebrew executables; the
    /// system software (shell, NPXS apps) is not.</summary>
    public bool IsGameProgram => !IsSystemProgram || ProgramAuthorityId >> 56 == 0x2F;

    /// <summary>The process runs a PlayStation Mobile runtime: a PSM program for SceSblACMgr (the PSM runtime package,
    /// by its authority ID: <see cref="ProgramAccess.IsPsmProgram"/>) or the runtime a PSM title carries itself (Unity
    /// for PSM, ux0:psm/&lt;id&gt;/runtime; its SELF stays encrypted in its PFS, so it is recognised by where the system
    /// starts it from).</summary>
    public PsmRuntimeKind PsmRuntime
    {
        get
        {
            if (Access.IsPsmProgram)
            {
                return PsmRuntimeKind.Mono;
            }

            return Io.FirmwareLayout.PsmTitleOfOwnRuntime(Options.FsRoot, AppPath) != null ? PsmRuntimeKind.OwnRuntime : PsmRuntimeKind.None;
        }
    }

    /// <summary>Modules loaded with the process, started before the others (each after the modules it imports from).</summary>
    private readonly List<LoadedModule> _startFirst = new();

    /// <summary>
    /// The modules the Vita's module manager loads into every process before its main thread, unless the process
    /// parameters' preload-disabled bits exclude them (bit, file in vs0:sys/external; same list as Vita3K). System programs
    /// get the firmware's; games keep VitaSharp's HLE for the ones it emulates (and their own app0:sce_module libc,
    /// libfios2, libSceFt2, libpvf), see <see cref="PreloadFirmwareModulesForGame"/>.
    /// </summary>
    private static readonly (uint Bit, string Name)[] s_processPreloads =
    [
        (0x00010000, "libc"), (0x00080000, "libshellsvc"), (0x00100000, "libcdlg"), (0x00200000, "libfios2"),
        (0x00400000, "apputil"), (0x00800000, "libSceFt2"), (0x01000000, "libpvf"),
    ];

    private void PreloadFirmwareModules(uint preloadDisabled)
    {
        foreach ((uint bit, string? name) in s_processPreloads)
        {
            if ((preloadDisabled & bit) != 0 || KeepOnHle(name))
            {
                continue;
            }

            string path = $"vs0:sys/external/{name}.suprx";
            if (Vfs.Resolve(path) is not { } host || !File.Exists(host))
            {
                continue;
            }

            if (Kernel.Modules.Values.Any(m => string.Equals(m.Path, $"app0:sce_module/{name}.suprx", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            LoadedModule loadedModule = LoadModule(path);
            _startFirst.AddRange(LoadFirmwareDependencies(loadedModule));
            _startFirst.Add(loadedModule);
        }
    }

    /// <summary>
    /// The module manager preloads its list into every process, games included, and takes libSceFt2, libpvf,
    /// libshellsvc, libcdlg and apputil only from vs0:sys/external (modulemgr 3.74 has an app0:sce_module path for libc
    /// and libfios2 only). A game keeps VitaSharp's HLE for the ones the HLE emulates; it gets the firmware's module
    /// when <see cref="GameTakesFirmwarePreload"/> says so (the PSM runtime's libpsm calls libSceFt2's FreeType, its
    /// eboot IPMI::Client::create of libshellsvc).
    /// </summary>
    private void PreloadFirmwareModulesForGame(uint preloadDisabled)
    {
        var firmware = Firmware.FirmwareLibraries.For(Options.FsRoot);
        if (MainModule == null)
        {
            return;
        }

        IEnumerable<(string Library, uint[] Nids)> mainImports = MainModule.Imports
            .Where(i => i.Library != null)
            .Select(i => (i.Library, i.FuncNids ?? []));
        foreach ((uint bit, string? name) in s_processPreloads)
        {
            if ((preloadDisabled & bit) != 0 || KeepOnHle(name))
            {
                continue;
            }

            string path = $"vs0:sys/external/{name}.suprx";
            Firmware.FirmwareLibraries.Module? module = firmware.Modules
                .FirstOrDefault(m => string.Equals(m.VitaPath, path, StringComparison.OrdinalIgnoreCase));
            if (module == null || Vfs.Resolve(path) is not { } host || !File.Exists(host))
            {
                continue;
            }

            if (Kernel.Modules.Values.Any(m => string.Equals(m.Path, $"app0:sce_module/{name}.suprx", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!GameTakesFirmwarePreload(module.Libraries.Select(l => l.Library).ToList(), mainImports, Hle.KnowsLibrary,
                    HleServesLibrary, Hle.Implements))
            {
                continue;
            }

            LoadedModule loadedModule = LoadModule(path);
            Log.Info($"  {path} preloaded for {MainModule.Name} (LLE)");
            _startFirst.AddRange(LoadFirmwareDependencies(loadedModule));
            _startFirst.Add(loadedModule);
        }
    }

    /// <summary>
    /// Whether a game gets the firmware's copy of a preloaded module that exports <paramref name="moduleLibraries"/>:
    /// when the HLE knows none of them (only the real module can serve them), or when the program imports, from one
    /// the HLE has only as a firmware fallback (not <paramref name="serves"/>), a function the HLE does not have.
    /// </summary>
    internal static bool GameTakesFirmwarePreload(IReadOnlyList<string> moduleLibraries,
        IEnumerable<(string Library, uint[] Nids)> imports, Func<string, bool> knows, Func<string, bool> serves,
        Func<uint, bool> implements)
    {
        if (moduleLibraries.Count == 0)
        {
            return false;
        }

        if (moduleLibraries.All(library => !knows(library)))
        {
            return true;
        }

        foreach ((string library, uint[] nids) in imports)
        {
            if (moduleLibraries.Contains(library) && !serves(library) && nids.Any(nid => !implements(nid)))
            {
                return true;
            }
        }

        return false;
    }

    private HashSet<string>? _keepHle;

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

    /// <summary>Libraries a system program takes from the firmware although the HLE has some of their functions: their
    /// firmware version is an IPC client of a service in the shell, which does the real work. ScePhotoExport
    /// (libScePhotoExport.suprx) asks the system's SceAvMediaSrv (NPXS10036, started for "avmedia_service:") to store
    /// the picture in photo0: and register it in the system's photo database, which the HLE (a copy into
    /// photo0:/TITLEID) does not; the camera calls internal functions of it (16B1028E...) the HLE never had.</summary>
    public static readonly HashSet<string> FirmwareServiceClients = new(StringComparer.Ordinal) { "ScePhotoExport" };

    /// <summary>The HLE stands in for <paramref name="library"/> when a system program's dependencies are resolved
    /// (libraries the HLE only has as a firmware fallback, <see cref="Hle.HleModule.FirmwareFallbackOnly"/>, do not).</summary>
    private bool HleServesLibrary(string library) => Hle.ImplementsLibrary(library);

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
            if (needsOnRequest.TryGetValue(vitaPath, out bool known))
            {
                return known;
            }

            needsOnRequest[vitaPath] = false; // cycles
            bool result = false;
            try
            {
                if (Vfs.Resolve(vitaPath) is { } host && File.Exists(host))
                {
                    var img = ModuleImage.Read(Loader.Self.SelfDecryptor.ToElf(File.ReadAllBytes(host), null));
                    foreach ((string? lib, uint _, uint[] _) in img.Imports)
                    {
                        if (index.Provider(lib) is not { } p || HleServesLibrary(lib))
                        {
                            continue;
                        }

                        if (OnRequestModules.Contains(Path.GetFileNameWithoutExtension(p.VitaPath))
                            || (!string.Equals(p.VitaPath, vitaPath, StringComparison.OrdinalIgnoreCase) && NeedsOnRequest(p.VitaPath)))
                        {
                            result = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                result = true;
            }
            return needsOnRequest[vitaPath] = result;
        }

        void Visit(LoadedModule m)
        {
            foreach ((string? lib, uint _, uint[]? nids) in m.Imports)
            {
                if (index.Provider(lib) is not { } provider)
                {
                    continue;
                }

                string file = Path.GetFileNameWithoutExtension(provider.VitaPath);
                if (OnRequestModules.Contains(file) || KeepOnHle(lib) || KeepOnHle(file))
                {
                    continue;
                }

                if (Kernel.Modules.Values.Any(x => string.Equals(x.Path, provider.VitaPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                // a library VitaSharp emulates in HLE (even partly: NP, AppUtil, Http...) stays on HLE, so every
                // process sees the same emulated services; only libraries the HLE knows nothing of come from the firmware.
                // Except when the importer is system software (a firmware module, or any module of a system program)
                // and calls functions of the library the HLE does not have (its internals, unnamed in the SDK): then
                // the real module serves the whole library, public functions included, so its internal state stays
                // its own. liblocation_internal / liblocation_extension call only SceLibLocation's internal context
                // functions, so near (NPXS10000) gets the real liblocation (an IPC client of the shell's location
                // provider); the system applications' calls to SceNpUtility/SceNpCommon/SceSslInternal internals
                // (memory pools, the NP HTTP/JSON client) reach the real np_utility, np_common and libssl.
                bool systemImporter = IsSystemProgram || m.Path.StartsWith("vs0:", StringComparison.OrdinalIgnoreCase);
                bool firmwareNeedsInternals = systemImporter && nids.Any(n => !Hle.Implements(n));
                // and for the libraries whose firmware version is a client of a service the system runs, which the HLE
                // only approximates (FirmwareServiceClients): a system program gets the real one
                bool serviceClient = IsSystemProgram && FirmwareServiceClients.Contains(lib);
                // a game keeps every library the HLE has anything of, firmware fallbacks included (its own libc and FIOS2
                // or the HLE ones): only libraries the HLE knows nothing of come from the firmware
                bool hleServes = IsSystemProgram ? HleServesLibrary(lib) : Hle.KnowsLibrary(lib);
                // libraries whose HLE is only a fallback of the firmware module (HleModule.FirmwareForEveryImporter):
                // every importer gets the firmware module
                bool lleProvided = provider.VitaPath.StartsWith("vs0:", StringComparison.OrdinalIgnoreCase) && Hle.FirmwareForEveryImporter(lib);
                if ((hleServes && !firmwareNeedsInternals && !serviceClient && !lleProvided) || nids.All(n => Linker.TryGetFunctionExport(n, out _)))
                {
                    continue;
                }

                if (withoutOnRequest && NeedsOnRequest(provider.VitaPath))
                {
                    continue;
                }

                if (!visiting.Add(provider.VitaPath))
                {
                    continue;
                }

                LoadedModule dep;
                try
                {
                    dep = LoadModule(provider.VitaPath);
                }
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

    private uint ProcessParamField(int offset)
    {
        uint pp = Linker.ProcessParam;
        if (pp == 0)
        {
            return 0;
        }

        uint size = GuestMemory.R32(pp);
        if (offset + 4 > size)
        {
            return 0;
        }

        return GuestMemory.R32(pp + (uint)offset);
    }

    /// <summary>"Carregar o cache de shaders antes de jogar": waits for the title's recorded shaders and pipelines before the game starts, logging the
    /// progress (at most every 250 ms) as <see cref="VitaSharp.Core.Gpu.ShaderCacheLoad"/> lines for the launcher.</summary>
    private void WaitForShaderCache()
    {
        if (Gpu.Backend is { } backend)
        {
            VitaSharp.Core.Gpu.ShaderCacheLoad.Wait(backend);
        }
    }

    private CodeCache? _codeCache;
    private Timer? _codeCacheTimer;

    /// <summary>The HLE routines written as guest code went into this process's space: the code cache records them too.</summary>
    private void OnHleGuestCodePlaced(uint address, uint size)
    {
        if (ReferenceEquals(AddressSpace.Current, Space) && _codeCache is { } codeCache)
        {
            codeCache.CodeRegionPlaced(HleGuestCode.BlockName, address, size);
        }
    }

    /// <summary>The boot step of "Precompilar código ARM" (<see cref="EmulatorOptions.PrecompileCode"/>): before the
    /// process's first thread runs, the blocks its modules ran in earlier sessions (or, in a first session, the code the
    /// analysis reaches from their entry points) are translated, with progress in the log as
    /// <see cref="CodePrecompileLog"/> lines (its own launcher progress bar); the rest of the analysis goes on in the
    /// background. Without <see cref="EmulatorOptions.PrecompileCodeWaits"/> nothing is waited for; without the option,
    /// only the recorded blocks are translated, in the background.</summary>
    private void PrecompileCode()
    {
        if (_codeCache is not { } codeCache)
        {
            return;
        }

        if (Options.CodeCacheDir != null)
        {
            _codeCacheTimer ??= new Timer(_ => SaveCodeCache(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        }

        codeCache.Analyze = Options.PrecompileCode;
        bool wait = Options.PrecompileCode && Options.PrecompileCodeWaits;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        CodePrecompileLog.Reporter? reporter = wait ? new CodePrecompileLog.Reporter() : null;
        if (wait)
        {
            Boot.BootProgress.Report(Boot.BootStep.CodePrecompile);
        }

        CodePrecompiler.Result result = codeCache.PrecompilePending(reporter == null ? null : reporter.Progress, CodePrecompileLog.Token, wait);
        reporter?.End();
        double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        Log.Info($"ARM code precompilation: boot step {seconds:F1} s ({(wait ? "waited for " + (codeCache.CachedSeeds > 0 ? "the code cache" : "the code reached from the entry points") : "nothing waited for")}), "
            + $"{result.Translated} blocks translated of {result.Visited} visited ({codeCache.CachedSeeds} from the code cache, {result.Found} found by the static analysis)"
            + (result.Cancelled ? ", cancelled" : "") + (result.ArenaLimit ? ", stopped at half of the code arena" : "") + (codeCache.Analyze ? "; the analysis continues in the background" : ""));
    }

    /// <summary>Records the blocks this process ran in the code cache (periodically, and before its translations go:
    /// exec, process end, shutdown, when <paramref name="stop"/> also ends the background translation).</summary>
    private void SaveCodeCache(bool stop = false)
    {
        try
        {
            if (stop)
            {
                _codeCache?.Stop();
            }

            _codeCache?.Save();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warn($"Code cache: save failed: {e.Message}");
        }
    }
    /// <summary>
    /// An application folder that is still a PFS image gets app0 as appmgr mounts it: the licence where the firmware
    /// keeps it, its title key by SceNpDrm's checks and the secure processor (<see cref="Package.LicenseTitleKey"/>),
    /// then the PFS layer opened with that key (<see cref="Package.Pfs.PfsApp0View"/>), the key the executable's SELF
    /// is decrypted with too. Without a licence the launch fails as appmgr's does
    /// (<see cref="TitleLicenseMissingException"/>); without a key, or with one that does not open the image, as
    /// <see cref="TitleKeyException"/>. The decrypted view is kept with the emulator's caches, next to the file system.
    /// </summary>
    private void OpenApp0Image()
    {
        if (!Package.Pfs.PfsApp0View.IsImage(AppPath))
        {
            return;
        }

        if (TitleLicense.FindFile(Options.FsRoot, AppPath, TitleId) is not { } licenseFile)
        {
            throw new TitleLicenseMissingException(TitleId, new FileNotFoundException($"{AppPath} is a PFS image and has no licence"));
        }

        string? activationFile = Vfs.Resolve("tm0:npdrm/act.dat");
        byte[] activation = activationFile != null && File.Exists(activationFile) ? File.ReadAllBytes(activationFile) : [];
        int error = Package.LicenseTitleKey.Get(File.ReadAllBytes(licenseFile), activation, SecureProcessorRifKey, out byte[]? key);
        if (error != 0 || key == null)
        {
            Log.Error($"{TitleId}: app0 is a PFS image; the title key of its licence {licenseFile} is not available (0x{error:X8})");
            throw new TitleKeyException(TitleId, error);
        }

        string fsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Options.FsRoot));
        string dataRoot = Path.GetDirectoryName(fsRoot) ?? fsRoot;
        byte[] folderHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPath.ToUpperInvariant()));
        string view = Path.Combine(dataRoot, "cache", "pfs", $"{TitleId}-{Convert.ToHexString(folderHash, 0, 8)}");
        try
        {
            Vfs.AddApp0BaseLayer(Package.Pfs.PfsApp0View.Open(AppPath, key, view, Log.Info));
        }
        catch (Package.Pfs.PfsKeyMismatchException e)
        {
            throw new TitleKeyException(TitleId, Package.LicenseTitleKey.BadLicense, e);
        }

        Klicensee = key;
    }

    private byte[]? FindKlicensee() =>
        TitleLicense.FindFile(Options.FsRoot, AppPath, TitleId) is { } license ? PkgInstaller.ReadKlicensee(license) : null;

    public LoadedModule LoadModule(string vitaPath) => LoadModule(vitaPath, kernel: false);

    /// <summary>
    /// Loads a module from a file. A kernel module (<paramref name="kernel"/>: the kernel module manager's loads and
    /// the <c>*KERNEL</c> plugins) goes to the kernel region and belongs to the kernel process
    /// (<see cref="KernelSpace.KernelPid"/>), as on the Vita, where the kernel and its modules outlive every process.
    /// </summary>
    public LoadedModule LoadModule(string vitaPath, bool kernel) => LoadModule(vitaPath, kernel, null);

    /// <summary>Loads a module known by <paramref name="vitaPath"/> from <paramref name="hostFile"/> (null: the file at
    /// that path of the Vita's file system).</summary>
    internal LoadedModule LoadModule(string vitaPath, bool kernel, string? hostFile)
    {
        // a missing file or folder is the guest's error (the callers return it), never a host exception that ends the thread
        string host = hostFile ?? (Vfs.Resolve(vitaPath) is { } h && File.Exists(h) ? h : throw new FileNotFoundException(vitaPath));
        byte[] elf = SelfDecryptor.ToElf(File.ReadAllBytes(host), Klicensee);
        if (kernel && !IsKernelProcess)
        {
            // the path is the caller's (its app0, its mounts): the module goes to the one kernel every process shares
            return KernelHost.LoadModuleImage(elf, vitaPath, kernel: true);
        }

        return LoadModuleImage(elf, vitaPath, kernel);
    }

    /// <summary>Loads a decrypted module image into this process (a kernel module: into the kernel process).</summary>
    private LoadedModule LoadModuleImage(byte[] elf, string vitaPath, bool kernel)
    {
        using ProcessScope.Scope processScope = ProcessScope.Enter(this);
        if (kernel)
        {
            // the firmware's kernel modules are in the kernel before any other kernel module, as on the Vita: the new
            // module links to their exports and finds them in the module manager's list
            FirmwareKernel.Load();
        }

        LoadedModule loadedModule;
        using (ProcessScope.Enter(kernel ? KernelSpace.KernelPid : ProcessScope.CurrentPid))
        {
            loadedModule = ModuleLoader.Load(elf, vitaPath, Memory, Linker, 0, kernel);
            loadedModule.Uid = Kernel.Objects.Register(new ModuleObject(loadedModule));
        }

        loadedModule.ImageHash = System.IO.Hashing.XxHash64.HashToUInt64(elf);

        Kernel.Modules[loadedModule.Uid] = loadedModule;
        // only the new module's own range holds new code; the stubs of earlier modules that the linker rewrote (and the
        // variable imports it relocated again) dropped their translations as it wrote them
        foreach (SegmentInfo segmentInfo in loadedModule.Segments.Values)
        {
            InvalidateCode(segmentInfo.Address, segmentInfo.Size);
        }

        Log.Info($"Loaded {(kernel ? "kernel " : "")}module {loadedModule.Name} ({vitaPath}) start=0x{loadedModule.StartEntry:X8} imports={loadedModule.Imports.Sum(i => i.FuncNids.Length)}");
        CheckPendingVariables(loadedModule);
        if (!kernel)
        {
            _codeCache?.ModuleLoaded(loadedModule);
        }

        return loadedModule;
    }

    private Hle.FirmwareKernelModules? _firmwareKernel;

    /// <summary>The firmware's kernel modules as kernel code sees them (shown when the first other kernel module loads),
    /// in the kernel process.</summary>
    public Hle.FirmwareKernelModules FirmwareKernel => IsKernelProcess
        ? LazyInitializer.EnsureInitialized(ref _firmwareKernel, () => new Hle.FirmwareKernelModules(this))
        : KernelHost.FirmwareKernel;

    /// <summary>SCE_KERNEL_START_SUCCESS, _NO_RESIDENT and _FAILED: what module_start returns.</summary>
    public const int ModuleStartSuccess = 0, ModuleStartNoResident = 1, ModuleStartFailed = 2;

    /// <summary>
    /// Runs a kernel module's module_start(args, argp) on a new kernel thread of this process and waits for it (as the
    /// kernel module manager runs it before its load call returns). Returns what module_start returned.
    /// </summary>
    public int StartKernelModule(LoadedModule loadedModule, uint args, uint argp)
    {
        if (!IsKernelProcess)
        {
            return KernelHost.StartKernelModule(loadedModule, args, argp);
        }

        loadedModule.Started = true;
        if (loadedModule.StartEntry == 0)
        {
            return ModuleStartSuccess;
        }

        using ProcessScope.Scope scope = ProcessScope.Enter(this);
        GuestThread t = Kernel.CreateThread($"start:{loadedModule.Name}", loadedModule.StartEntry, 0x40, 0x4000, 0, 0x70000);
        t.KernelMode = true;
        t.Start(args, argp);
        if (!t.WaitForExit(30_000))
        {
            Log.Warn($"module_start of kernel module {loadedModule.Name} did not finish in time");
            return ModuleStartFailed;
        }

        // the module manager deletes the thread it ran module_start on
        DeleteFinishedKernelThread(t);
        return t.ExitStatus;
    }

    /// <summary>Removes a loaded module (sceKernelUnloadModule): imports that pointed at its exports go back to the next
    /// provider or to HLE, its segments are freed and the translated code of their range is dropped.</summary>
    public void UnloadModule(LoadedModule loadedModule)
    {
        if (loadedModule.IsKernel && !IsKernelProcess && Vita.KernelProcess is { } kernelProcess)
        {
            kernelProcess.UnloadModule(loadedModule);
            return;
        }

        using ProcessScope.Scope scope = ProcessScope.Enter(this);
        _codeCache?.ModuleUnloaded(loadedModule);
        int rebound = Linker.UnregisterModule(loadedModule);
        foreach (SegmentInfo segmentInfo in loadedModule.Segments.Values)
        {
            InvalidateCode(segmentInfo.Address, segmentInfo.Size);
            Memory.Free(segmentInfo.Address);
        }
        Kernel.Modules.TryRemove(loadedModule.Uid, out _);
        Kernel.Objects.Remove(loadedModule.Uid);
        Log.Info($"Unloaded module {loadedModule.Name} ({loadedModule.Path}): {rebound} import stub(s) rebound");
    }

    /// <summary>The plugins of this process when the emulator option "Enable Kernel Plugins" is on (<see cref="LoadPlugins"/>);
    /// nothing with it off, the default: the pure firmware runs.</summary>
    internal List<string> LoadPluginsIfEnabled()
    {
        if (!KernelPluginsEnabled)
        {
            return [];
        }

        MigratePluginConfiguration();
        return LoadPlugins();
    }

    /// <summary>The emulator's plugin folder: ur0:kernel_plugins, inside the Vita's file system next to ur0:tai, with
    /// the plugin configuration (config.txt) and the files it names without a device (or
    /// <see cref="EmulatorOptions.KernelPluginsDir"/>).</summary>
    public string KernelPluginsFolder => Options.KernelPluginsDir ?? Path.Combine(Options.FsRoot, "ur0", "kernel_plugins");

    /// <summary>The plugin folder of earlier versions, kernel_plugins next to the fs folder of the data folder; only
    /// with the default folder (an explicit <see cref="EmulatorOptions.KernelPluginsDir"/> has no earlier layout).</summary>
    private string? LegacyKernelPluginsFolder => Options.KernelPluginsDir != null ? null
        : Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Options.FsRoot))) ?? Options.FsRoot, "kernel_plugins");

    /// <summary>The configuration in the emulator's plugin folder.</summary>
    private string PluginFolderConfig => Path.Combine(KernelPluginsFolder, "config.txt");

    /// <summary>A configuration line without a device (no ':'): a file of the emulator's plugin folder, relative to it.</summary>
    private static bool InPluginFolder(string path) => !path.Contains(':', StringComparison.Ordinal);

    /// <summary>
    /// With the option on: the first time (no configuration in the emulator's plugin folder yet), moves the Vita's plugin
    /// configuration (ux0:tai, else ur0:tai) into the folder unchanged, keeping a copy as config.txt.bak; its lines keep
    /// the Vita paths, and the plugins stay in ur0:tai / ux0:tai (homebrew checks their files there). A plugin an earlier
    /// version moved into the folder, whose line was rewritten to its file name, goes back to the Vita path config.txt.bak
    /// lists for it, and its line too. Nothing is deleted except by moving.
    /// </summary>
    internal void MigratePluginConfiguration()
    {
        MoveLegacyPluginFolder();
        if (File.Exists(PluginFolderConfig))
        {
            ReturnMovedPlugins();
            return;
        }

        string? source = PluginConfig.Paths.Select(p => Vfs.Resolve(p)).FirstOrDefault(h => h != null && File.Exists(h));
        if (source == null || !PluginConfig.IsValid(File.ReadAllText(source)))
        {
            return;
        }

        Directory.CreateDirectory(KernelPluginsFolder);
        File.Copy(source, PluginFolderConfig + ".bak", overwrite: true);
        File.Move(source, PluginFolderConfig);
        Log.Info($"plugins: moved the plugin configuration to the emulator's plugin folder ({PluginFolderConfig}; a copy is config.txt.bak)");
    }

    /// <summary>The folder of earlier versions (<see cref="LegacyKernelPluginsFolder"/>) moves once into ur0:kernel_plugins:
    /// each of its entries (config.txt, config.txt.bak, files the configuration names without a device) goes there
    /// unless one of the same name is already there, and the old folder is deleted once empty.</summary>
    private void MoveLegacyPluginFolder()
    {
        string? legacy = LegacyKernelPluginsFolder;
        if (legacy == null || !Directory.Exists(legacy))
        {
            return;
        }

        Directory.CreateDirectory(KernelPluginsFolder);
        foreach (string entry in Directory.EnumerateFileSystemEntries(legacy))
        {
            string target = Path.Combine(KernelPluginsFolder, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target))
            {
                Log.Warn($"plugins: {entry} stays where it is, {target} already exists");
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Move(entry, target);
            }
            else
            {
                File.Move(entry, target);
            }

            Log.Info($"plugins: moved {entry} to {target}");
        }

        if (!Directory.EnumerateFileSystemEntries(legacy).Any())
        {
            Directory.Delete(legacy);
        }
    }

    /// <summary>The plugins an earlier version moved into the folder go back to the Vita paths of config.txt.bak, and
    /// their lines name those paths again.</summary>
    private void ReturnMovedPlugins()
    {
        string backup = PluginFolderConfig + ".bak";
        if (!File.Exists(backup))
        {
            return;
        }

        var vitaPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in PluginConfig.ModulesFor(File.ReadAllText(backup), "KERNEL").Where(p => !InPluginFolder(p)))
        {
            vitaPaths.TryAdd(path[(path.LastIndexOfAny(['/', ':']) + 1)..], path);
        }

        string text = File.ReadAllText(PluginFolderConfig);
        bool changed = false;
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] is '#' or '*' || !InPluginFolder(line) || !vitaPaths.TryGetValue(line, out string? vitaPath))
            {
                continue;
            }

            string file = Path.Combine(KernelPluginsFolder, line);
            if (Vfs.Resolve(vitaPath) is { } target && File.Exists(file) && !File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target);
                Log.Info($"plugins: moved {line} back to {vitaPath}");
            }

            lines[i] = vitaPath;
            changed = true;
        }

        if (changed)
        {
            File.WriteAllText(PluginFolderConfig, string.Join("\n", lines));
        }
    }

    /// <summary>The process ended: the plugin framework's hooks and injections in it (made by kernel plugins, kept by
    /// the kernel, and by the process's own user plugins) go with it.</summary>
    private void ForgetPluginPatches()
    {
        if (Vita.KernelProcess is { } kernel && !ReferenceEquals(kernel, this))
        {
            kernel.Hle.Modules.OfType<Hle.Modules.PluginFramework>().FirstOrDefault()?.ForgetProcess(Pid);
        }

        Hle.Modules.OfType<Hle.Modules.PluginFramework>().FirstOrDefault()?.ForgetProcess(Pid);
    }

    private bool ExportsPluginFramework(LoadedModule module) =>
        module.Exports.Any(e => e.FuncNids.Any(Hle.Modules.OfType<Hle.Modules.PluginFramework>().First().Provides));

    /// <summary>The emulator option "Enable Kernel Plugins" (<see cref="EmulatorOptions.KernelPlugins"/>, else
    /// VITASHARP_KERNEL_PLUGINS=1); off by default.</summary>
    public bool KernelPluginsEnabled => Options.KernelPlugins ?? Environment.GetEnvironmentVariable(EnvVars.KernelPlugins) == "1";

    /// <summary>
    /// The plugins of the system's plugin configuration (ur0:tai / ux0:tai config.txt, read with the rules of the scene's plugin loader:
    /// <see cref="PluginConfig"/>): the modules of <c>*KERNEL</c> (kernel modules), then those of <c>*ALL</c> and of the
    /// process's title ID (user modules), all through the same path, the module loader, before the process starts;
    /// their module_start runs before the main thread (as Vita3K does for user plugins). Like the firmware's module
    /// manager, a module whose imports name a library nothing in the system provides (no loaded module exports it and the
    /// HLE has none of its functions) is not started: it is unloaded again and the log names the missing libraries.
    /// Returns what happened to each listed module, for the log and the tests.
    /// </summary>
    internal List<string> LoadPlugins()
    {
        // kernel modules first (each started as it loads, as the plugin loader of the homebrew environment starts
        // them): the user modules may import what they export
        List<string> results = LoadKernelPlugins();
        results.AddRange(LoadUserPlugins(ProcessTitleId));
        return results;
    }

    /// <summary>The plugin configuration's text: the emulator's plugin folder's config.txt, else ux0:tai/config.txt, or
    /// ur0:tai/config.txt only when that does not exist (the plugin loader's order); null without one or when the plugin
    /// loader would refuse it (<see cref="PluginConfig.IsValid"/>).</summary>
    private string? ReadPluginConfig()
    {
        foreach (string path in PluginConfig.Paths.Prepend(PluginFolderConfig))
        {
            string? host = path == PluginFolderConfig ? path : Vfs.Resolve(path);
            if (host == null || !File.Exists(host))
            {
                continue;
            }

            string text = File.ReadAllText(host);
            // the recordings keep which configuration was read, the build and the firmware (PluginHookStats)
            string vitaPath = path == PluginFolderConfig ? "ur0:kernel_plugins/config.txt" : path;
            var firmware = FirmwareInfo.Read(Options.FsRoot);
            string firmwareText = firmware == null ? "not installed" : $"{firmware.Version ?? "unknown"} (build {firmware.Build})";
            PluginHookStats.Of(this).RecordConfig(vitaPath + (PluginConfig.IsValid(text) ? "" : " (not valid: no plugin loads)"), text,
                global::VitaSharp.Core.SaveState.SaveStateFile.EmulatorVersion, firmwareText);
            if (!PluginConfig.IsValid(text))
            {
                Log.Warn($"plugin configuration {path} is not valid (no plugin loads from it)");
                return null;
            }

            return text;
        }

        return null;
    }

    /// <summary>The <c>*KERNEL</c> modules of the plugin configuration, once per session: loaded into the kernel
    /// and started. Returns what happened to each.</summary>
    internal List<string> LoadKernelPlugins()
    {
        var results = new List<string>();
        if (Vita.KernelPluginsLoaded || ReadPluginConfig() is not { } config)
        {
            return results;
        }

        Vita.KernelPluginsLoaded = true;
        foreach (string path in PluginConfig.ModulesFor(config, "KERNEL"))
        {
            if (Vita.KernelProcess is { } kernelProcess
                && kernelProcess.Kernel.Modules.Values.Any(m => m.IsKernel && string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string result = LoadPlugin(path, kernel: true);
            Log.Info($"kernel plugin {path}: {result}");
            results.Add($"{path}: {result}");
        }

        return results;
    }

    /// <summary>
    /// The user modules of the plugin configuration for <paramref name="titleId"/> (<c>*ALL</c> and the section named
    /// exactly as the title ID): loaded into this process; they start with the process. Returns what happened to each.
    /// </summary>
    internal List<string> LoadUserPlugins(string titleId)
    {
        var results = new List<string>();
        if (ReadPluginConfig() is not { } config)
        {
            return results;
        }

        foreach (string path in PluginConfig.ModulesFor(config, titleId))
        {
            string result = LoadPlugin(path, kernel: false);
            Log.Info($"plugin {path}: {result}");
            results.Add($"{path}: {result}");
        }

        return results;
    }

    /// <summary>
    /// Loads one plugin module; it stays only when every library it imports is provided by a loaded module it may see
    /// or by the HLE. A kernel module is started at once on a kernel thread and stays only when its module_start
    /// reports success; a user module starts with the process.
    /// </summary>
    private string LoadPlugin(string path, bool kernel)
    {
        string result = LoadPluginModule(path, kernel, out LoadedModule? loadedModule);
        // the recordings keep each plugin: its file's hash and size, what happened, the imports nothing provides
        string unresolved = loadedModule == null ? "" : string.Join(", ", loadedModule.Imports
            .SelectMany(i => i.FuncNids
                .Where(n => !Linker.TryGetVisibleExport(n, i.LibNid, kernel, out _) && !Hle.Implements(n))
                .Select(n => $"{i.Library}:{NidDatabase.NameOf(n)}")));
        string? hostFile = InPluginFolder(path) ? Path.Combine(KernelPluginsFolder, path) : Vfs.Resolve(path);
        PluginHookStats.Of(this).RecordPlugin(path, kernel, loadedModule?.Name ?? "", hostFile, result, unresolved, Options.FsRoot);
        return result;
    }

    private string LoadPluginModule(string path, bool kernel, out LoadedModule? loaded)
    {
        loaded = null;
        LoadedModule loadedModule;
        try
        {
            loadedModule = LoadModule(path, kernel, InPluginFolder(path) ? Path.Combine(KernelPluginsFolder, path) : null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warn($"plugin {path}: not loaded, it is neither in the emulator's plugin folder nor at its path ({e.Message})");
            return $"not loaded ({e.Message})";
        }

        loaded = loadedModule;
        // the emulator offers the plugin framework libraries itself: a listed module that exports them (the user's copy of
        // the framework) is not loaded
        if (ExportsPluginFramework(loadedModule))
        {
            UnloadModule(loadedModule);
            return "not loaded (the emulator provides its libraries)";
        }

        var missing = loadedModule.Imports
            .Where(i => !i.FuncNids.Any(n => Linker.TryGetVisibleExport(n, i.LibNid, kernel, out _) || Hle.Implements(n)))
            .Select(i => i.Library)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            UnloadModule(loadedModule);
            return $"not started: no module provides {string.Join(", ", missing)}";
        }

        if (!kernel)
        {
            return $"loaded ({loadedModule.Name})";
        }

        int status = StartKernelModule(loadedModule, 0, 0);
        if (status != ModuleStartSuccess)
        {
            // NO_RESIDENT: the module did its work and asks to go; FAILED (or a crash): it is unloaded too
            UnloadModule(loadedModule);
            return status == ModuleStartNoResident ? $"started and unloaded ({loadedModule.Name}: module_start asked not to stay)"
                : $"not started: module_start of {loadedModule.Name} returned 0x{status:X}, unloaded";

        }

        return $"loaded and started ({loadedModule.Name})";
    }

    /// <summary>Runs library module_start functions, then starts the main thread at the eboot entry.</summary>
    public void Start(string[]? args = null)
    {
        if (MainModule == null)
        {
            throw new InvalidOperationException("No title loaded");
        }

        using ProcessScope.Scope scope = ProcessScope.Enter(this);
        Running = true;
        if (Parent == null)
        {
            // the system's POWER button is read from the host's controller from now on, also during standby
            Vita.Power.Start(() => Host.Input);
        }

        int priority = (int)(ProcessParamPtrValue(0x14) ?? 0x10000100);
        uint stackSize = ProcessParamPtrValue(0x18) ?? 256 * 1024;
        int affinity = (int)(ProcessParamPtrValue(0x28) ?? 0);
        if (affinity == 0)
        {
            affinity = 0x70000;
        }

        string name = ProcessParamString(0x10) ?? "main";

        PrecompileCode();
        GuestThread main = Kernel.CreateThread(name, MainModule.StartEntry, priority, stackSize, 0, affinity);
        Kernel.MainThread = main;
        TellProcessCreated(main);

        // Library module_start functions run (in load order) before the main thread.
        // (firmware dependencies of a system program first, each after the modules it imports from)
        foreach (LoadedModule? loadedModule in _startFirst.Concat(Kernel.Modules.Values.Where(m => !_startFirst.Contains(m)).OrderBy(m => m.Uid))
                     .Where(m => m != MainModule && m.StartEntry != 0 && !m.Started && !m.IsKernel).ToList())
        {
            loadedModule.Started = true;
            RunOnNewThread($"start:{loadedModule.Name}", loadedModule.StartEntry);
        }

        // the preloaded modules are started: processmgr's call of modulemgr's export, for a kernel hook on it
        StartPreloadingModulesThroughModulemgr();
        // the launch posts the application's PARAM_NOTIFY event (FW 3.74 kernel appmgr, its launch path at 0x8100EC52):
        // Settings and Friends wait for it to open their first page and then read the launch parameter, which a launch
        // without one (the shell starting an application from its bubble passes NULL) leaves empty: the first
        // sceAppMgrGetAppParam succeeds with "". (Not the shell itself: the kernel starts it, it is no launched application.)
        if (!RunsOnSystemCore)
        {
            AppMgr appMgr = Hle.Modules.OfType<VitaSharp.Core.Hle.Modules.AppMgr>().First();
            if (appMgr.LaunchParam == null && Environment.GetEnvironmentVariable(Util.EnvVars.AppParam) is { } ap
                && ap.IndexOf(':') is > 0 and var colon && ap[..colon].Equals(TitleId, StringComparison.OrdinalIgnoreCase))
            {
                appMgr.LaunchParam = ap[(colon + 1)..];
            }

            appMgr.LaunchParam ??= _launchParam ?? "";
            appMgr.PostEvent(VitaSharp.Core.Hle.Modules.AppMgr.EventParamNotify);
        }
        if (args is { Length: > 0 })
        {
            // argv as the kernel passes it: the strings back to back, NUL terminated
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\0", args) + "\0");
            uint argp = Memory.Alloc((uint)Math.Max(bytes.Length, 16), "process args");
            bytes.CopyTo(GuestMemory.Span(argp, bytes.Length));
            main.Start((uint)bytes.Length, argp);
        }
        else
        {
            main.Start(0, 0);
        }
    }

    private void RunOnNewThread(string name, uint entry)
    {
        GuestThread t = Kernel.CreateThread(name, entry, 0x10000100, 256 * 1024, 0, 0x70000);
        t.Start(0, 0);
        if (!t.WaitForExit(30_000))
        {
            Log.Warn($"{name} did not finish in time");
        }
    }

    private uint? ProcessParamPtrValue(int offset)
    {
        uint ptr = ProcessParamField(offset);
        return ptr == 0 ? null : GuestMemory.R32(ptr);
    }

    private string? ProcessParamString(int offset)
    {
        uint ptr = ProcessParamField(offset);
        return ptr == 0 ? null : GuestMemory.ReadCString(ptr);
    }

    /// <summary>A guest thread crashed. In a process the shell started, the process ends (the shell carries on, as on
    /// the Vita); in the first process it is the front-end's fatal error.</summary>
    public void ReportFatal(GuestThread t, Exception e)
    {
        if (IsKernelProcess && Parent is { } system)
        {
            // a kernel thread's crash is the system's: the first process reports it
            Log.Error($"Kernel thread '{t.Name}' crashed: {e.Message}");
            system.ReportFatal(t, e);
            return;
        }

        if (Parent != null && Fatal == null)
        {
            Emulator root = Parent;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            if (!root.Running || !Running)
            {
                return; // the emulator is shutting down
            }

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
        foreach (Emulator c in _children.ToArray())
        {
            c.Stop();
        }

        Kernel.StopAllThreads();
        if (Parent == null && Vita.KernelProcess is { } kernelProcess)
        {
            // the system stops: the kernel's threads too
            kernelProcess.Kernel.StopAllThreads();
        }
    }

    public void Dispose()
    {
        Stop();
        HleGuestCode.CodePlaced -= OnHleGuestCodePlaced;
        _codeCache?.Stop();
        if (_codeCache?.DescribeMisses() is { } misses)
        {
            Log.Info(misses);
        }

        SaveCodeCache();
        _codeCacheTimer?.Dispose();
        if (Parent != null)
        {
            return; // the GPU and host devices are the first process's
        }

        Vita.EndKernelProcess();

        if (_jitBefore is { } jit)
        {
            Jit.Enabled = jit.Enabled;
            Jit.Select(jit.Mode);
            _jitBefore = null;
        }
        EmulationPause.Changed -= OnPauseChanged;
        VitaSharp.Core.Gxm.GxmProgramInfo.Clear(); // the parsed programs of this system's guest memory
        Vita.Power.Dispose();
        Gpu.Dispose();
        Gpu.Backend?.Dispose();
        Host.Dispose();
    }
}

public sealed class ModuleObject(LoadedModule module) : KernelObject
{
    public LoadedModule Module { get; } = module;
}
