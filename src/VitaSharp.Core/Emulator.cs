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

    public event Action<GuestThread, Exception>? Fatal;
    public volatile bool Running;

    public Emulator(EmulatorOptions options)
    {
        Options = options;
        Jit.Enabled = options.EnableJit;
        string? backend = options.CpuBackend ?? Environment.GetEnvironmentVariable("VITASHARP_CPU");
        if (!Jit.Select(backend)) { Log.Warn($"CPU backend '{backend}' is unknown or not supported on this host"); Jit.Select(null); }
        Log.Info(Jit.Enabled ? Jit.Describe() : "CPU backend: interpreter only (JIT disabled)");
        TraceHle = options.TraceHle;
        Memory = new GuestMemory();
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
        foreach (var t in typeof(Emulator).Assembly.GetTypes()
                     .Where(t => t.IsSubclassOf(typeof(HleModule)) && !t.IsAbstract)
                     .OrderBy(t => t.FullName))
            Hle.RegisterModule((HleModule)Activator.CreateInstance(t)!);
    }

    // ------------------------------------------------------------------ process exec (sceAppMgrLoadExec)

    /// <summary>Set by sceAppMgrLoadExec: the executable (app0: path) and arguments to replace the process with.</summary>
    public (string Path, string[] Args)? ExecRequest { get; private set; }
    public event Action? ExecRequested;

    /// <summary>Called from the guest: stops the process; the host then completes the exec with <see cref="PerformExec"/>.</summary>
    public void RequestExec(string path, string[] args)
    {
        Log.Info($"LoadExec {path} [{string.Join(" ", args)}]");
        ExecRequest = (path, args);
        Running = false;
        Kernel.StopAllThreads();
        ExecRequested?.Invoke();
    }

    /// <summary>Replaces the process image: waits for the old threads, resets kernel, memory and GPU state and starts
    /// the requested executable in the same address space (like the Vita kernel does for the same application).</summary>
    public void PerformExec(Func<Gpu.IGpuBackend?>? backendFactory)
    {
        var (path, args) = ExecRequest ?? throw new InvalidOperationException("no exec pending");
        if (!Kernel.WaitAllThreadsExit(10_000)) Log.Warn("LoadExec: some threads did not stop in time");
        Gpu.ReplaceBackend(backendFactory);
        Code.Clear();
        Memory.FreeAll();
        VitaSharp.Core.Kernel.Scheduler.Reset();
        InitProcess();
        MainModule = null;
        ExecRequest = null;
        string self = path.StartsWith("app0:", StringComparison.OrdinalIgnoreCase) ? path[5..].TrimStart('/') : path;
        LoadTitle(AppPath, self);
        Start(args);
        Running = true;
    }

    /// <summary>Loads an installed title (folder ux0:app/TITLEID) or a loose folder containing eboot.bin.</summary>
    public void LoadTitle(string appPath, string self = "eboot.bin")
    {
        AppPath = Path.GetFullPath(appPath);
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
        Log.Info($"Loading {TitleId} \"{Title}\" from {AppPath}");
        // per-title shader/pipeline caches start warming up while the executable loads
        try { Gpu.Backend?.BeginTitle(TitleId); } catch (Exception e) { Log.Warn($"GPU backend title setup failed: {e.Message}"); }

        MainModule = LoadModule("app0:" + self);
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
        foreach (uint nid in Linker.UnresolvedVariables())
            Log.Warn($"Unresolved variable import NID {nid:X8} ({NidDatabase.Lookup(nid)?.Name ?? "unknown"})");
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
        foreach (var m in Kernel.Modules.Values.Where(m => m != MainModule && m.StartEntry != 0).OrderBy(m => m.Uid))
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
