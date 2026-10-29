using System.Diagnostics;
using VitaSharp.Core;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Host;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>Boots a title in a window (default) or headless (--headless, for automated testing).</summary>
public static class Runner
{
    static string? Opt(string[] options, string name) =>
        options.FirstOrDefault(o => o.StartsWith(name + "="))?[(name.Length + 1)..];

    /// <summary>--name=on|off (also 1/0, true/false, yes/no); null when absent or not recognised.</summary>
    static bool? OnOff(string[] options, string name)
    {
        var v = Opt(options, name);
        if (v == null) return null;
        switch (v.Trim().ToLowerInvariant())
        {
            case "on" or "1" or "true" or "yes": return true;
            case "off" or "0" or "false" or "no": return false;
            default: Log.Warn($"{name}={v} not recognised (use on or off): using the default"); return null;
        }
    }

    /// <summary>The title-bar notice of a trophy unlock ("Troféu desbloqueado: <name> (Ouro)").</summary>
    internal static string TrophyNotice(VitaSharp.Core.Hle.Np.TrophyUnlockedEvent e) =>
        $"Troféu desbloqueado: {e.Trophy.Name} ({TrophyToast.GradeName(e.Trophy.Grade)})";

    public static int Run(string appPath, string[] options)
    {
        // 1 ms system timer resolution while a game runs (guest wait timeouts and audio/vsync pacing rely on it)
        if (OperatingSystem.IsWindows()) timeBeginPeriod(1);
        try { return RunCore(appPath, options); }
        finally { if (OperatingSystem.IsWindows()) timeEndPeriod(1); }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    static int RunCore(string appPath, string[] options)
    {
        // watchpoints are compiled out unless enabled before the CPU helpers are first used
        if (options.Any(o => o.StartsWith("--watch"))) Environment.SetEnvironmentVariable(EnvVars.Watch, "1");
        bool trace = options.Contains("--trace-hle");
        bool noJit = options.Contains("--no-jit");
        bool headless = options.Contains("--headless") || Opt(options, "--seconds") != null;
        if (options.Contains("--debug")) Log.Level = LogLevel.Debug;
        // debug mode (launcher "Modo de depuração"): rate-limited diagnostic logging, see docs/notes/debug-mode.md
        if (options.Contains("--debug-mode") || Environment.GetEnvironmentVariable(EnvVars.DebugMode) is "1" or "on" or "true") DebugMode.Enable();
        // logs/last.log for this session, the previous sessions kept as logs/<time>_<title>.log
        if (Opt(options, "--log") is { } logPath) Log.OpenFile(logPath);
        else LogFiles.OpenSession(Path.Combine(Program.DataRoot, "logs"), LogFiles.TitleOf(appPath));
        if (DebugMode.Enabled) Log.Info("[dbg] debug mode on (first-use logging, HLE errors, anomaly checks, periodic PERF, deduplicated warnings)");
        InstallCrashLogging();

        // --video-decoder=auto|sw|<FFmpeg hwaccel device> (default: VITASHARP_VIDEO_HW, else auto = hardware with software fallback)
        if (Opt(options, "--video-decoder") is { } vdec && VitaSharp.Core.Media.VideoDecoders.Backend is VitaSharp.Core.Media.FFmpegVideoBackend ffv)
            ffv.Configure(vdec);
        // --psn=on|off: emulated PSN account signed in (default) or signed out; --psn-id=<online id> names the account
        // (launcher: Configurações > Geral > Rede e PSN). Nothing is ever sent to Sony's servers.
        string? psnId = Opt(options, "--psn-id");
        if (psnId != null && !PsnAccount.IsValidOnlineId(psnId))
            Log.Warn($"--psn-id={psnId} is not a valid online ID (3-16 letters, digits, '-' or '_', starting with a letter): using {PsnAccount.DefaultOnlineId}");
        // --psn-local=on|off: local PSN services (TUS / Cross-Save, rankings, tickets, matching, in <data>/np; default on)
        // or every PSN server request failing as unreachable
        var psn = PsnAccount.Create(OnOff(options, "--psn") ?? PsnAccount.Default.SignedIn, psnId, OnOff(options, "--psn-local") ?? true);
        // --lle-modules=libsas,libatrac: also load these from the installed firmware although VitaSharp has HLE for
        // them; --hle-modules=libult,...: keep these on HLE even when the game or the firmware has the module
        // (defaults: VITASHARP_LLE_MODULES / VITASHARP_HLE_MODULES)
        static string[]? ModuleListOpt(string[] o, string name) => Opt(o, name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (FirmwareInfo.Read(Program.FsRoot) is { } fw) Log.Info($"Firmware {fw.Display} installed ({string.Join(", ", fw.Partitions)})");
        else Log.Info("No firmware installed: system libraries without HLE (Json, Xml, Sqlite...) are unavailable");
        // --cpu=native|il|interp selects the CPU backend (default: VITASHARP_CPU, else the best one the host supports)
        using var emu = new Emulator(new EmulatorOptions
        {
            FsRoot = Program.FsRoot, EnableJit = !noJit, CpuBackend = Opt(options, "--cpu"), TraceHle = trace,
            PatchDir = Path.Combine(Program.DataRoot, "patches"), Psn = psn, NpRoot = Path.Combine(Program.DataRoot, "np"),
            LleModules = ModuleListOpt(options, "--lle-modules"), HleModules = ModuleListOpt(options, "--hle-modules"),
        });
        var fatal = new ManualResetEventSlim();
        emu.Fatal += (t, e) => fatal.Set();

        var rendererOptions = new VulkanRendererOptions
        {
            ShaderCacheDir = Opt(options, "--shader-cache") ?? Path.Combine(Program.DataRoot, "shadercache"),
            Validation = options.Contains("--validation"),
            VSync = !options.Contains("--no-vsync"),
            // stutter options (launcher: Configurações > Geral > Desempenho); VITASHARP_SHADERC_OPT, VITASHARP_ASYNC_SHADERS,
            // VITASHARP_ASYNC_PIPELINES and VITASHARP_SHADER_WORKERS override them
            ShaderOptimization = Opt(options, "--shaderc-opt"),
            AsyncShaders = OnOff(options, "--async-shaders") ?? true,
            AsyncPipelines = OnOff(options, "--async-pipelines") ?? true,
            ShaderWorkers = int.TryParse(Opt(options, "--shader-workers"), out var shaderWorkers) && shaderWorkers > 0 ? shaderWorkers : 0,
            // --gpu-profile=adreno|mali|desktop: behave as that GPU class (test the mobile paths on a desktop GPU)
            GpuProfile = Opt(options, "--gpu-profile"),
        };
        if (rendererOptions.ShaderOptimization is { } so && ShaderCompiler.ParseOptLevel(so) == null)
            Log.Warn($"--shaderc-opt={so} not recognised (use zero, size or perf): using the default");

        if (headless) return RunHeadless(emu, appPath, options, rendererOptions, fatal);

        using var window = new WindowHost("VitaSharp", Opt(options, "--scale") is { } s ? int.Parse(s) : 1, options.Contains("--fullscreen"));
        using var audio = OpenAlAudio.TryCreate(Opt(options, "--audio-device"));
        // scripted input also works in a window (automated checks of the windowed path: swapchain, LoadExec)
        var scripted = Opt(options, "--input") is { } windowScript ? new ScriptedInput(windowScript) : null;
        emu.Host.Input = scripted ?? (IInputBackend)window;
        // motion sensors: the controller's gyro (SDL), or the script's tilt steps
        emu.Host.Motion = scripted ?? window.Motion;
        emu.Host.Audio = audio;
        // webcam for SceCamera: --camera=<name or index> picks a device, --camera=none disables it (grey frames)
        emu.Host.Camera = FlashCapCamera.TryCreate(Opt(options, "--camera"));
        VulkanRenderer? renderer = null;
        var clock = Stopwatch.StartNew();
        long lastFrames = 0, titleTicks = 0;
        double fps = 0;
        // short feedback in the title bar (diagnostic captures), shown for a few seconds instead of the FPS line
        string? status = null;
        long statusUntil = 0;
        void ShowTitle()
        {
            string? st = Environment.TickCount64 < Interlocked.Read(ref statusUntil) ? Volatile.Read(ref status) : null;
            try { window.Title = st != null ? $"VitaSharp | {st}" : $"VitaSharp | {emu.TitleId} {emu.Title} | {fps:F0} FPS | {renderer?.DeviceName}"; } catch { }
        }
        var titleTimer = new System.Threading.Timer(_ =>
        {
            long frames = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
            fps = (frames - lastFrames) / 1.0;
            lastFrames = frames;
            if (++titleTicks % 5 == 0) Log.Info($"FPS frame {frames}: {fps:F0} frames/s");
            ShowTitle();
        }, null, 1000, 1000);
        var diag = new Diagnostics(emu, () => renderer, options)
        {
            Status = s => { Volatile.Write(ref status, s); Interlocked.Exchange(ref statusUntil, Environment.TickCount64 + 6000); ShowTitle(); },
            HostInfo = () => $"Window: framebuffer {window.FramebufferSize.Width}x{window.FramebufferSize.Height}, scale option {Opt(options, "--scale") ?? "1"}, fullscreen at start {options.Contains("--fullscreen")}",
        };
        // trophy unlocks: a card in the top-right corner of the window (renderer overlay) and a notice in the title bar,
        // like the console's pop-up
        using var trophyToast = new TrophyToast((bgra, w, h) => renderer?.SetOverlay(bgra, w, h), () => window.FramebufferSize.Height);
        VitaSharp.Core.Hle.Np.TrophyNotifications.Unlocked += e => { trophyToast.Enqueue(e); diag.Status?.Invoke(TrophyNotice(e)); };
        // --trophy-toast-test=<seconds>: shows the notification of the title's trophy 1 (nothing is unlocked), to check it
        if (int.TryParse(Opt(options, "--trophy-toast-test"), out int toastAt))
            _ = Task.Delay(TimeSpan.FromSeconds(toastAt)).ContinueWith(_ =>
            {
                var set = VitaSharp.Core.Hle.Np.TrophyPackage.FindSets(appPath).FirstOrDefault();
                var pkg = set.TrpPath != null ? VitaSharp.Core.Hle.Np.TrophyPackage.Load(set.TrpPath, set.CommId) : null;
                if (pkg?.Trophies.Count > 1)
                    trophyToast.Enqueue(new VitaSharp.Core.Hle.Np.TrophyUnlockedEvent(set.CommId, pkg.Title, pkg.Trophies[1], pkg.TrophyIcon(pkg.Trophies[1].Id)));
            });
        window.DiagnosticRequested += () => diag.Capture("F10");
        // F8: GPU dump recording (docs/notes/gpu-dump.md)
        window.GpuDumpToggled += () => GpuDumpControl.Toggle(emu, () => renderer, options, diag.Status, diag.HostInfo);
        window.DebugModeToggled += () =>
        {
            if (DebugMode.Enabled) DebugMode.Disable(); else DebugMode.Enable();
            Log.Info($"[dbg] debug mode {(DebugMode.Enabled ? "on" : "off")} (F9) at guest frame {DebugMode.Frame}");
            diag.Status?.Invoke(DebugMode.Enabled ? "Modo de depuração ligado (F9 desliga, F10 captura, F8 grava dump da GPU)" : "Modo de depuração desligado");
        };
        using var watchdog = new Watchdog(emu, () => renderer);
        StartScheduledCaptures(options, diag);
        StartDebugModeToggles(options);
        GpuDumpControl.StartScheduled(emu, () => renderer, options);

        window.Loaded += () =>
        {
            rendererOptions.Surface = window;
            renderer = new VulkanRenderer(rendererOptions);
            renderer.AnomalyDetected += diag.OnAnomaly;
            emu.Gpu.Backend = renderer;
            emu.LoadTitle(appPath);
            emu.Start();
        };
        // sceAppMgrLoadExec: restart the process with the requested executable (new renderer: all GPU state is stale)
        emu.ExecRequested += () => Task.Run(() =>
        {
            try { emu.PerformExec(() => { var r = new VulkanRenderer(rendererOptions); r.AnomalyDetected += diag.OnAnomaly; return renderer = r; }); }
            catch (Exception e) { Log.Error($"LoadExec failed: {e}"); fatal.Set(); }
        });
        window.Resized += () => renderer?.NotifyResized();
        window.Closing += () => emu.Stop();
        emu.Fatal += (t, e) => Log.Error($"Fatal error in thread {t.Name}: {e.Message}");
        window.Run();
        GpuDumpControl.StopAtExit();
        titleTimer.Dispose();
        emu.Stop();
        diag.WaitIdle(20000);
        PrintStats(emu);
        return 0;
    }

    /// <summary>--debug-mode-at=FRAME[,FRAME...]: toggles debug mode like F9 when the guest frame is reached (tests).</summary>
    static void StartDebugModeToggles(string[] options)
    {
        if (Opt(options, "--debug-mode-at") is not { } spec) return;
        var frames = new SortedSet<long>(spec.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.Parse(s.TrimStart('f'))));
        new Thread(() =>
        {
            while (frames.Count > 0)
            {
                if (VitaSharp.Core.Hle.Modules.Display.FramesPresented >= frames.Min)
                {
                    frames.Remove(frames.Min);
                    if (DebugMode.Enabled) DebugMode.Disable(); else DebugMode.Enable();
                    Log.Info($"[dbg] debug mode {(DebugMode.Enabled ? "on" : "off")} (--debug-mode-at) at guest frame {DebugMode.Frame}");
                }
                Thread.Sleep(5);
            }
        }) { IsBackground = true, Name = "debug-mode-at" }.Start();
    }

    /// <summary>--diag-at=FRAME[,FRAME...]: the F10 capture, triggered when the guest frame is reached (tests).</summary>
    static void StartScheduledCaptures(string[] options, Diagnostics diag)
    {
        if (Opt(options, "--diag-at") is not { } spec) return;
        var frames = new SortedSet<long>(spec.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.Parse(s.TrimStart('f'))));
        new Thread(() =>
        {
            while (frames.Count > 0)
            {
                if (VitaSharp.Core.Hle.Modules.Display.FramesPresented >= frames.Min)
                {
                    frames.Remove(frames.Min);
                    diag.Capture("--diag-at");
                }
                Thread.Sleep(5);
            }
        }) { IsBackground = true, Name = "diag-at" }.Start();
    }

    /// <summary>Unhandled exceptions (any thread) and unobserved task failures go to the log, which is flushed.</summary>
    static void InstallCrashLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error($"UNHANDLED EXCEPTION (terminating: {e.IsTerminating}) on thread '{Thread.CurrentThread.Name}' at guest frame {VitaSharp.Core.Hle.Modules.Display.FramesPresented}: {e.ExceptionObject}");
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error($"Unobserved task exception: {e.Exception}");
            Log.Flush();
        };
    }

    static VulkanRenderer NewHeadlessRenderer(VulkanRendererOptions ro, Diagnostics diag)
    {
        var r = new VulkanRenderer(ro);
        r.AnomalyDetected += diag.OnAnomaly;
        return r;
    }

    static int RunHeadless(Emulator emu, string appPath, string[] options, VulkanRendererOptions ro, ManualResetEventSlim fatal)
    {
        var diag = new Diagnostics(emu, () => emu.Gpu.Backend as VulkanRenderer, options);
        var dumpOpt = options.FirstOrDefault(o => o.StartsWith("--dump-frames"));
        string? dumpDir = null;
        List<(long Frame, byte[] Rgba)>? thumbs = null;
        if (dumpOpt != null || options.Contains("--vulkan") || Opt(options, "--trace-draws") != null)
        {
            var dir = dumpOpt?.Contains('=') == true ? dumpOpt[(dumpOpt.IndexOf('=') + 1)..] : Path.Combine(Program.DataRoot, "frames");
            if (dumpOpt != null && Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
            ro.DumpFramesDir = dumpOpt != null ? dir : null;
            ro.DumpEvery = int.TryParse(Opt(options, "--dump-every"), out var de) ? de : 60;
            ro.DumpFrom = long.TryParse(Opt(options, "--dump-from"), out var dfr) ? dfr : 0;
            ro.TraceDrawsFrame = long.TryParse(Opt(options, "--trace-draws"), out var tdf) ? tdf : -1;
            if (dumpOpt != null) { dumpDir = dir; ro.Thumbnails = thumbs = new(); ro.InspectTextures = !options.Contains("--no-inspect"); }
            emu.Gpu.Backend = NewHeadlessRenderer(ro, diag);
        }
        // turbo: no vblank/audio pacing, guest time jumps one frame per frame (automated tests)
        VitaSharp.Core.Util.GuestClock.Turbo = options.Contains("--turbo");
        if (int.TryParse(Opt(options, "--cores"), out var cores)) VitaSharp.Core.Kernel.Scheduler.Cores = cores; // 0 = unlimited (old behaviour)
        if (long.TryParse(Opt(options, "--cpu-mips"), out var mips)) VitaSharp.Core.Cpu.CpuCore.InstructionsPerSecond = mips * 1_000_000;
        if (long.TryParse(Opt(options, "--cpu-mhz"), out var cpuMhz)) VitaSharp.Core.Cpu.CpuCore.InstructionsPerSecond = cpuMhz * 1_000_000; // 0 = unpaced
        if (long.TryParse(Opt(options, "--jit-threshold"), out var jitT)) VitaSharp.Core.Cpu.Jit.HotInstructions = jitT; // executed instructions before a block is compiled
        if (int.TryParse(Opt(options, "--bt-depth"), out var btd)) VitaSharp.Core.Kernel.GuestThread.BacktraceDepth = btd;
        if (int.TryParse(Opt(options, "--frame-sleep"), out var fsl)) VitaSharp.Core.Hle.Modules.Display.FrameSleepMs = fsl;
        if (double.TryParse(Opt(options, "--io-speed"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ioMBps))
            VitaSharp.Core.Util.IoTiming.BytesPerSecond = ioMBps * 1e6; // 0 = instant reads
        if (double.TryParse(Opt(options, "--speed"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var speed) && speed > 0) VitaSharp.Core.Util.GuestClock.Speed = speed;
        if (Opt(options, "--input") is { } script) { var si = new ScriptedInput(script); emu.Host.Input = si; emu.Host.Motion = si; }
        if (options.Contains("--audio")) emu.Host.Audio = OpenAlAudio.TryCreate(Opt(options, "--audio-device")); // real audio device, as in the window mode
        if (Opt(options, "--audio-dump") is { } adump) VitaSharp.Core.Util.AudioDump.Enable(adump); // per-port WAVs of what the guest output
        if (Opt(options, "--camera") is { } cam) emu.Host.Camera = FlashCapCamera.TryCreate(cam); // headless: webcam only on request
        emu.BacktraceHle = Opt(options, "--bt");
        if (Opt(options, "--trace-hle-only") is { } tho) emu.TraceHleOnly = tho.Split('+');
        if (Opt(options, "--trace-hle-threads") is { } tht) emu.TraceHleThreads = tht.Split('+');
        if (Opt(options, "--trace-hle-frames") is { } thf)
        {
            var p = thf.Split(':');
            emu.TraceHleFrames = (long.Parse(p[0]), long.Parse(p[1]));
            if (p.Length > 2) emu.TraceHleExclude = p[2] == "all" ? [] : p[2].Split('+');
        }
        if (Opt(options, "--probe-frames") is { } pf) { var q = pf.Split(':'); ProbeFrom = long.Parse(q[0]); ProbeTo = long.Parse(q[1]); ProbeAll = true; }
        if (Opt(options, "--probe") is { } probes) InstallProbes(probes);
#if LOCKSTEP
        Lockstep.Install();
#endif
        if (Opt(options, "--poke") is { } poke) InstallPokes(poke);
        if (Opt(options, "--watch") is { } watch) InstallWatch(watch);
        if (Opt(options, "--watch-read") is { } watchRead) InstallReadWatch(watchRead);
        if (Opt(options, "--dump-mem") is { } dm) StartMemoryDumps(dm, Path.Combine(Program.DataRoot, "memdump"));
        if (Opt(options, "--peek") is { } pk) StartPeeks(pk);
        using var watchdog = new Watchdog(emu, () => emu.Gpu.Backend as VulkanRenderer);
        StartScheduledCaptures(options, diag);
        StartDebugModeToggles(options);
        GpuDumpControl.StartScheduled(emu, () => emu.Gpu.Backend as VulkanRenderer, options);
        var wall = Stopwatch.StartNew();
        emu.LoadTitle(appPath);
        emu.Start();
        int seconds = int.TryParse(Opt(options, "--seconds"), out var sec) ? sec : 30;
        long frameLimit = long.TryParse(Opt(options, "--frames"), out var fl) ? fl : -1;
        bool profileHle = options.Contains("--profile-hle");
        Sampler? sampler = options.Contains("--profile") || profileHle || Opt(options, "--profile-from") != null ? new Sampler(emu, long.TryParse(Opt(options, "--profile-from"), out var pfrom) ? pfrom : 0, profileHle) : null;
        if (Opt(options, "--seconds") == null && frameLimit > 0) seconds = 1800; // frames decide; seconds is a safety timeout
        long fpsFrames = 0; double fpsAt = 0;
        var allocMarks = new List<AllocMark>();
        while (!fatal.Wait(50) && (frameLimit <= 0 || VitaSharp.Core.Hle.Modules.Display.FramesPresented < frameLimit) && wall.Elapsed.TotalSeconds < seconds)
        {
            double now = wall.Elapsed.TotalSeconds;
            if (now - fpsAt >= 5) // periodic frame rate, so boot and gameplay can be told apart
            {
                long f = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
                Log.Info($"FPS frame {f}: {(f - fpsFrames) / (now - fpsAt):F1} frames/s");
                LogAllocWindow(allocMarks, f);
                fpsFrames = f; fpsAt = now;
            }
            if (emu.ExecRequest != null)
            {
                try { emu.PerformExec(emu.Gpu.Backend != null ? () => NewHeadlessRenderer(ro, diag) : null); }
                catch (Exception e) { Log.Error($"LoadExec failed: {e}"); break; }
            }
        }
        GpuDumpControl.StopAtExit();
        diag.WaitIdle();
        sampler?.Report();
        PrintStats(emu);
        long frames = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
        LogSteadyAllocations(allocMarks, frames);
        emu.Stop();
        Log.Info($"Run finished: {frames} frames in {wall.Elapsed.TotalSeconds:F1} s ({frames / Math.Max(wall.Elapsed.TotalSeconds, 0.001):F1} frames/s)");
        if (dumpDir != null && thumbs != null) WriteContactSheet(dumpDir, thumbs);
        return 0;
    }

    readonly record struct AllocMark(long Frame, long Bytes, int Gen0, int Gen1, int Gen2);

    static AllocMark TakeAllocMark(long frame) =>
        new(frame, GC.GetTotalAllocatedBytes(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    /// <summary>ALLOC line: managed allocations since the previous periodic mark (allocation rate per stretch of the run).</summary>
    static void LogAllocWindow(List<AllocMark> marks, long frame)
    {
        var m = TakeAllocMark(frame);
        if (marks.Count > 0 && marks[^1] is var p && m.Frame > p.Frame)
            Log.Info($"ALLOC frames {p.Frame}-{m.Frame}: {(m.Bytes - p.Bytes) / 1024.0 / (m.Frame - p.Frame):F1} KB/frame, GC +{m.Gen0 - p.Gen0}/+{m.Gen1 - p.Gen1}/+{m.Gen2 - p.Gen2}, working set {Environment.WorkingSet / 1048576.0:F0} MB");
        marks.Add(m);
    }

    /// <summary>PERF line for the second half of the run (boot excluded): the steady-state allocation rate.</summary>
    static void LogSteadyAllocations(List<AllocMark> marks, long frames)
    {
        int i = marks.FindIndex(m => m.Frame >= frames / 2);
        if (i < 0) return;
        var p = marks[i];
        var m = TakeAllocMark(frames);
        if (m.Frame <= p.Frame) return;
        Log.Info($"PERF steady allocated (frames {p.Frame}-{m.Frame}): {(m.Bytes - p.Bytes) / 1024.0 / (m.Frame - p.Frame):F1} KB/frame, GC gen0 +{m.Gen0 - p.Gen0} gen1 +{m.Gen1 - p.Gen1} gen2 +{m.Gen2 - p.Gen2}");
    }

    /// <summary>Dumped frames as contact sheets (5 per row, 240x136 each, 30 per sheet: sheet.png, sheet_2.png, ...)
    /// with the frame number burnt in.</summary>
    internal static void WriteContactSheet(string dir, List<(long Frame, byte[] Rgba)> thumbs)
    {
        foreach (var old in Directory.GetFiles(dir, "sheet*.png")) File.Delete(old);
        var all = thumbs.OrderBy(t => t.Frame).ToList();
        for (int i = 0; i < all.Count; i += 30)
            WriteSheet(Path.Combine(dir, i == 0 ? "sheet.png" : $"sheet_{i / 30 + 1}.png"), all.GetRange(i, Math.Min(30, all.Count - i)));
        WriteFrameMetrics(dir, all);
    }

    /// <summary>
    /// Automatic checks on every dumped frame, so problems are found without reading the images: mean luminance,
    /// fraction of saturated white / black pixels (blown-out or black output), and runs of identical frames (a
    /// hang or a stuck screen). Written to metrics.csv; the findings go to the log as METRICS lines.
    /// </summary>
    static void WriteFrameMetrics(string dir, List<(long Frame, byte[] Rgba)> all)
    {
        var sb = new System.Text.StringBuilder("frame,mean,white,black,hash\n");
        var over = new List<long>(); var black = new List<long>(); var flicker = new List<long>(); var magenta = new List<long>();
        long frozenStart = -1, frozenLen = 0, longestFrozen = 0, longestFrozenStart = -1;
        ulong prevHash = 0;
        float prevMean = -1; bool prevBlack = false;
        foreach (var (frame, px) in all)
        {
            long sum = 0; int white = 0, dark = 0, pink = 0, n = px.Length / 4;
            ulong hash = 14695981039346656037;
            for (int i = 0; i < px.Length; i += 4)
            {
                int r = px[i], g = px[i + 1], b = px[i + 2];
                int lum = (r * 54 + g * 183 + b * 19) >> 8;
                sum += lum;
                if (r > 235 && g > 235 && b > 235) white++;
                if (r < 10 && g < 10 && b < 10) dark++;
                if (r > 200 && b > 200 && g < 80) pink++; // magenta: the classic missing/garbage texture colour
                hash = (hash ^ (ulong)((r >> 2) | (g >> 2) << 8 | (b >> 2) << 16)) * 1099511628211;
            }
            float mean = sum / (255f * n), fw = white / (float)n, fb = dark / (float)n;
            sb.Append($"{frame},{mean:F3},{fw:F3},{fb:F3},{hash:X16}\n");
            if (fw > 0.35f || mean > 0.85f) over.Add(frame);
            if (fb > 0.97f) black.Add(frame);
            if (pink / (float)n > 0.05f) magenta.Add(frame);
            // exposure/brightness flicker: a big swing between consecutive dumps that is not a fade through black
            bool isBlack = fb > 0.97f;
            if (prevMean >= 0 && !isBlack && !prevBlack && MathF.Abs(mean - prevMean) > 0.35f) flicker.Add(frame);
            prevMean = mean; prevBlack = isBlack;
            if (hash == prevHash) { if (frozenLen == 0) frozenStart = frame; frozenLen++; if (frozenLen > longestFrozen) { longestFrozen = frozenLen; longestFrozenStart = frozenStart; } }
            else frozenLen = 0;
            prevHash = hash;
        }
        File.WriteAllText(Path.Combine(dir, "metrics.csv"), sb.ToString());
        string Range(List<long> l) => l.Count == 0 ? "none" : $"{l.Count} frames ({l[0]}..{l[^1]})";
        Log.Info($"METRICS blown-out (>35% saturated white or mean >0.85): {Range(over)}");
        Log.Info($"METRICS black (>97% black): {Range(black)}");
        Log.Info($"METRICS flicker (brightness swing >0.35 between dumps): {Range(flicker)}");
        Log.Info($"METRICS magenta (>5% magenta pixels, missing textures): {Range(magenta)}");
        Log.Info($"METRICS frozen: {(longestFrozen >= 2 ? $"longest run of identical dumps {longestFrozen + 1} from frame {longestFrozenStart}" : "none")}");
    }

    static void WriteSheet(string path, List<(long Frame, byte[] Rgba)> list)
    {
        const int tw = 240, th = 136, cols = 5;
        int rows = (list.Count + cols - 1) / cols, w = cols * tw, h = rows * th;
        var sheet = new byte[w * h * 4];
        for (int i = 0; i < list.Count; i++)
        {
            int ox = (i % cols) * tw, oy = (i / cols) * th;
            for (int y = 0; y < th; y++)
                Buffer.BlockCopy(list[i].Rgba, y * tw * 4, sheet, ((oy + y) * w + ox) * 4, tw * 4);
            DrawNumber(sheet, w, ox + 3, oy + 3, list[i].Frame);
        }
        Png.Write(path, sheet, w, h);
        Log.Info($"Contact sheet: {path} (frames {list[0].Frame}-{list[^1].Frame})");
    }

    /// <summary>Tiny 3x5 digit font, scaled 2x, yellow on black.</summary>
    static void DrawNumber(byte[] img, int stride, int x, int y, long value)
    {
        string[] font = ["111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
                         "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111"];
        string s = value.ToString();
        for (int py = -1; py < 11; py++)
            for (int px = -1; px < s.Length * 8; px++) Set(x + px, y + py, 0, 0, 0);
        for (int c = 0; c < s.Length; c++)
            for (int r = 0; r < 5; r++)
                for (int k = 0; k < 3; k++)
                    if (font[s[c] - '0'][r * 3 + k] == '1')
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++) Set(x + c * 8 + k * 2 + dx, y + r * 2 + dy, 255, 230, 0);
        void Set(int px, int py, byte r, byte g, byte b)
        {
            int o = (py * stride + px) * 4;
            if (px < 0 || py < 0 || px >= stride || o + 3 >= img.Length) return;
            img[o] = r; img[o + 1] = g; img[o + 2] = b; img[o + 3] = 255;
        }
    }

    /// <summary>Maximum number of watch hits logged (env VITASHARP_WATCH_MAX, default 4000).</summary>
    static readonly int WatchMax = int.TryParse(Environment.GetEnvironmentVariable(EnvVars.WatchMax), out var wm) ? wm : 4000;

    /// <summary>--watch=ADDR:LEN[:FROM:TO] logs every CPU store into the range (optionally only in a frame window).</summary>
    static unsafe void InstallWatch(string spec)
    {
        var p = spec.Split(':');
        uint len = Convert.ToUInt32(p[1], 16);
        long from = p.Length > 3 ? long.Parse(p[2]) : 0, to = p.Length > 3 ? long.Parse(p[3]) : long.MaxValue;
        int hits = 0;
        ResolveWatchAddress(p[0], from, addr => VitaSharp.Core.Cpu.WriteWatch.Set(addr, len));
        VitaSharp.Core.Cpu.WriteWatch.OnHit = (a, size, v) =>
        {
            long f = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
            if (f < from || f >= to || Interlocked.Increment(ref hits) > WatchMax) return;
            var t = VitaSharp.Core.Kernel.GuestThread.Current;
            if (t == null) return;
            var s = t.Core.State;
            float fv = size == 4 ? BitConverter.UInt32BitsToSingle((uint)v) : float.NaN;
            Log.Info($"WATCH f{f} [{t.Name}] {a:X8} <- {v:X} ({fv}) size {size} blockEnd={s->R[15]:X8} lr={s->R[14]:X8} {t.Backtrace(s)}");
        };
    }

    /// <summary>"ADDR" or "@BASE,OFF1,...,OFFn": a pointer chain (deref BASE, add OFF1, deref, ..., add OFFn) resolved once
    /// the frame window starts, so watches can follow heap objects reached from a global.</summary>
    static void ResolveWatchAddress(string spec, long fromFrame, Action<uint> apply)
    {
        if (!spec.StartsWith('@')) { apply(Convert.ToUInt32(spec, 16)); return; }
        var parts = spec[1..].Split(',').Select(s => Convert.ToUInt32(s, 16)).ToArray();
        new Thread(() =>
        {
            while (VitaSharp.Core.Hle.Modules.Display.FramesPresented < Math.Max(fromFrame - 5, 1)) Thread.Sleep(5);
            uint a;
            while (true)
            {
                // retry until every pointer in the chain exists (objects created later, e.g. at level load)
                a = parts[0];
                bool ok = true;
                for (int i = 1; i < parts.Length && ok; i++)
                {
                    uint ptr = VitaSharp.Core.Memory.GuestMemory.R32(a);
                    ok = ptr != 0;
                    a = ptr + parts[i];
                }
                if (ok) break;
                Thread.Sleep(5);
            }
            Log.Info($"Watch {spec} resolved to 0x{a:X8}");
            apply(a);
        }) { IsBackground = true }.Start();
    }

    /// <summary>--poke=ADDR:HEXVALUE@fN,...: writes a 32-bit value into guest memory when frame N is presented (experiments).</summary>
    static void InstallPokes(string spec)
    {
        var pokes = spec.Split(',').Select(p => { var a = p.Split(':', '@'); return (Addr: Convert.ToUInt32(a[0], 16), Value: Convert.ToUInt32(a[1], 16), Frame: long.Parse(a[2].TrimStart('f'))); }).OrderBy(p => p.Frame).ToList();
        new Thread(() =>
        {
            foreach (var p in pokes)
            {
                while (VitaSharp.Core.Hle.Modules.Display.FramesPresented < p.Frame) Thread.Sleep(2);
                VitaSharp.Core.Memory.GuestMemory.W32(p.Addr, p.Value);
                Log.Info($"POKE f{p.Frame} {p.Addr:X8} <- {p.Value:X8}");
            }
        }) { IsBackground = true }.Start();
    }

    /// <summary>--watch-read=ADDR:LEN[:FROM:TO] logs every 32/64-bit CPU load from the range.</summary>
    static unsafe void InstallReadWatch(string spec)
    {
        var p = spec.Split(':');
        uint addr = Convert.ToUInt32(p[0], 16), len = Convert.ToUInt32(p[1], 16);
        long from = p.Length > 3 ? long.Parse(p[2]) : 0, to = p.Length > 3 ? long.Parse(p[3]) : long.MaxValue;
        int hits = 0;
        VitaSharp.Core.Cpu.WriteWatch.OnRead = (a, size) =>
        {
            long f = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
            if (f < from || f >= to || Interlocked.Increment(ref hits) > WatchMax) return;
            var t = VitaSharp.Core.Kernel.GuestThread.Current;
            if (t == null) return;
            var s = t.Core.State;
            Log.Info($"RWATCH f{f} [{t.Name}] {a:X8} = {VitaSharp.Core.Memory.GuestMemory.R32(a):X8} size {size} blockEnd={s->R[15]:X8} lr={s->R[14]:X8} {t.Backtrace(s)}");
        };
        VitaSharp.Core.Cpu.WriteWatch.SetRead(addr, len);
    }

    /// <summary>--dump-mem=F1,F2,... writes every allocated guest block to memdump/mem_F.bin when frame F is presented
    /// (records of [u32 address][u32 size][bytes]); compare two dumps with the memdiff command.</summary>
    /// <summary>--peek=ADDR:LEN@F[,ADDR:LEN@F...] logs a hex dump of guest memory when frame F is presented.</summary>
    static void StartPeeks(string spec)
    {
        var items = spec.Split(',').Select(s => { var a = s.Split(':', '@'); return (Addr: Convert.ToUInt32(a[0], 16), Len: Convert.ToInt32(a[1], 16), Frame: long.Parse(a[2])); }).ToList();
        new Thread(() =>
        {
            while (items.Count > 0)
            {
                long f = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
                foreach (var it in items.Where(i => f >= i.Frame).ToList())
                {
                    var sb = new System.Text.StringBuilder($"PEEK f{f} 0x{it.Addr:X8}:");
                    for (int o = 0; o < it.Len; o += 4) sb.Append(o % 32 == 0 ? $"\n  +{o:X3}: " : " ").Append(VitaSharp.Core.Memory.GuestMemory.R32(it.Addr + (uint)o).ToString("X8"));
                    Log.Info(sb.ToString());
                    items.Remove(it);
                }
                Thread.Sleep(5);
            }
        }) { IsBackground = true, Name = "peek" }.Start();
    }

    static void StartMemoryDumps(string spec, string dir)
    {
        var frames = new SortedSet<long>(spec.Split(',').Select(long.Parse));
        Directory.CreateDirectory(dir);
        new Thread(() =>
        {
            while (frames.Count > 0)
            {
                long f = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
                if (f >= frames.Min)
                {
                    frames.Remove(frames.Min);
                    var path = Path.Combine(dir, $"mem_{f}.bin");
                    using var w = new BinaryWriter(File.Create(path));
                    foreach (var b in VitaSharp.Core.Memory.GuestMemory.Instance!.Blocks().Where(b => b.Region == VitaSharp.Core.Memory.MemoryRegion.User))
                    {
                        w.Write(b.Address); w.Write(b.Size);
                        w.Write(VitaSharp.Core.Memory.GuestMemory.ReadSpan(b.Address, (int)b.Size));
                    }
                    Log.Info($"Memory dump {path}");
                }
                Thread.Sleep(2);
            }
        }) { IsBackground = true, Name = "memdump" }.Start();
    }

    /// <summary>--probe=ADDR[?rC][:WORDS@rN],... logs (only when rC != 0) registers (and optionally memory at a register) when a block starts at ADDR.</summary>
    static long ProbeFrom, ProbeTo = long.MaxValue;
    static bool ProbeAll;

    static unsafe void InstallProbes(string spec)
    {
        var probes = new Dictionary<uint, (int Reg, int Words, int When)>();
        foreach (var p in spec.Split(','))
        {
            var parts = p.Split(':');
            int reg = -1, words = 0, when = -1;
            if (parts[0].Split('?') is [var a, var c]) { parts[0] = a; when = int.Parse(c.TrimStart('r')); }
            if (parts.Length > 1 && parts[1].Split('@') is [var w, var r]) { words = int.Parse(w); reg = int.Parse(r.TrimStart('r')); }
            probes[Convert.ToUInt32(parts[0], 16) & ~1u] = (reg, words, when);
        }
        var hits = new Dictionary<uint, int>();
        VitaSharp.Core.Cpu.CpuCore.Probes = probes.Keys.ToHashSet();
        VitaSharp.Core.Cpu.CpuCore.ProbeHit = (core, s) =>
        {
            uint pc = s->R[15];
            if (probes[pc].When is >= 0 and var wr && s->R[wr] == 0) return;
            long frame = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
            if (frame < ProbeFrom || frame >= ProbeTo) return;
            lock (hits)
            {
                hits[pc] = hits.GetValueOrDefault(pc) + 1;
                int n = hits[pc];
                if (!ProbeAll && n > 8 && n % 300 != 0) return;
            }
            var sb = new System.Text.StringBuilder($"PROBE f{VitaSharp.Core.Hle.Modules.Display.FramesPresented} {pc:X8} #{hits[pc]} cyc={core.BlockCycles} ins={core.InstructionsExecuted}:");
            for (int i = 0; i < 15; i++) sb.Append($" r{i}={s->R[i]:X}");
            var (reg, words, _) = probes[pc];
            if (reg >= 0)
            {
                sb.Append($"\n   [r{reg}]:");
                for (int i = 0; i < words; i++) sb.Append($" {VitaSharp.Core.Memory.GuestMemory.R32(s->R[reg] + (uint)i * 4):X8}");
            }
            Log.Info(sb.ToString());
        };
    }

    static void PrintStats(Emulator emu)
    {
        Log.Info($"Stopping. {VitaSharp.Core.Cpu.Jit.Describe()}");
        if (VitaSharp.Core.Cpu.Native.NativeVerifier.Enabled)
            Log.Info($"CPU backend report: native verify: {VitaSharp.Core.Cpu.Native.NativeVerifier.Verified} block runs verified, {VitaSharp.Core.Cpu.Native.NativeVerifier.Mismatches} mismatches ({VitaSharp.Core.Cpu.Native.NativeVerifier.SimdMismatches} in blocks with NEON/VFP ops), {VitaSharp.Core.Cpu.Native.NativeVerifier.Skipped} interpreted (unloggable stores)");
        if (VitaSharp.Core.Cpu.Jit.Chain.Any(c => c is VitaSharp.Core.Cpu.Backends.NativeX64Compiler or VitaSharp.Core.Cpu.Backends.NativeArm64Compiler))
            Log.Info($"CPU backend report: {VitaSharp.Core.Cpu.Backends.NativeX64Compiler.Report()}");
        foreach (var f in emu.Hle.Functions.Where(f => f.CallCount > 0).OrderByDescending(f => f.CallCount).Take(40))
            Log.Info($"  {f.CallCount,8} x {f.Name}{(f.Implemented ? "" : " (unimplemented)")}");
        foreach (var t in emu.Kernel.Threads) Log.Info("  thread " + t.Describe());
        {
            long fr = Math.Max(1, VitaSharp.Core.Hle.Modules.Display.FramesPresented);
            foreach (var t in emu.Kernel.Threads.Where(t => t.Core.BlockCycles + t.Core.ChargedCycles > 0).OrderByDescending(t => t.Core.BlockCycles + t.Core.ChargedCycles).Take(6))
                Log.Info($"PERF cycles {t.Name}: guest code {t.Core.BlockCycles / 1e6 / fr:F2} M/frame ({t.Core.BlocksExecuted / 1e3 / fr:F0} K blocks), charged HLE {t.Core.ChargedCycles / 1e6 / fr:F2} M/frame");
        }
        // VITASHARP_PEEK_END=ADDR:LEN[,ADDR:LEN...] hex-dumps guest memory when the run stops (state of a hang)
        if (Environment.GetEnvironmentVariable(EnvVars.PeekEnd) is { } peekEnd)
            foreach (var spec in peekEnd.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var pp = spec.Split(':');
                uint pa = Convert.ToUInt32(pp[0], 16), pl = Convert.ToUInt32(pp[1], 16);
                var sb = new System.Text.StringBuilder($"  peek-end {pa:X8}:");
                for (uint i = 0; i < pl; i += 4) { if (i % 32 == 0) sb.Append($"\n   {pa + i:X8}:"); sb.Append($" {VitaSharp.Core.Memory.GuestMemory.R32(pa + i):X8}"); }
                Log.Info(sb.ToString());
            }
        // held mutexes with waiters: the first thing to look at when threads sit in "wait=[mutex ...]" (deadlocks)
        foreach (var m in emu.Kernel.Objects.All<VitaSharp.Core.Kernel.Mutex>())
            if (m.Owner != null && m.Waiters > 0)
                Log.Info($"  mutex '{m.Name}' held by '{m.Owner.Name}' ({m.Owner.Status}) count {m.LockCount}, {m.Waiters} waiting");
        if (emu.Gpu.Backend?.Report() is { } gpuReport) Log.Info(gpuReport);
        {
            long hwPics = VitaSharp.Core.Media.FFmpegVideoDecoder.HardwarePictures, swPics = VitaSharp.Core.Media.FFmpegVideoDecoder.SoftwarePictures;
            if (hwPics + swPics > 0)
                Log.Info($"PERF video decode: {hwPics} pictures in hardware, {swPics} in software, {VitaSharp.Core.Media.FFmpegVideoDecoder.Fallbacks} hardware fallbacks, {VitaSharp.Core.Media.FFmpegVideoDecoder.DecodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms decoding ({VitaSharp.Core.Media.FFmpegVideoDecoder.DecodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / (hwPics + swPics):F2} ms/picture)");
        }
        Log.Info($"  GPU commands: submitted {emu.Gpu.Submitted}, completed {emu.Gpu.Completed}; frames presented {VitaSharp.Core.Hle.Modules.Display.FramesPresented}");
        foreach (var l in AudioDiagnostics.SummaryLines()) Log.Info(l); // always: ports, outputs/s, underruns per minute
        Watchdog.LogSummaries();
        long frames = Math.Max(1, VitaSharp.Core.Hle.Modules.Display.FramesPresented);
        var gcInfo = GC.GetGCMemoryInfo();
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        Log.Info($"PERF cpu {proc.TotalProcessorTime.TotalSeconds:F1} s ({proc.TotalProcessorTime.TotalMilliseconds / frames:F1} ms/frame, user {proc.UserProcessorTime.TotalSeconds:F1} s, kernel {proc.PrivilegedProcessorTime.TotalSeconds:F1} s), threads {proc.Threads.Count}");
        var byCpu = new List<(string Name, double Ms)>();
        foreach (System.Diagnostics.ProcessThread t in proc.Threads)
        {
            try { byCpu.Add((VitaSharp.Core.Util.NativeThread.Names.GetValueOrDefault(t.Id, $"host#{t.Id}"), t.TotalProcessorTime.TotalMilliseconds)); }
            catch (InvalidOperationException) { }
        }
        foreach (var (name, ms) in byCpu.OrderByDescending(x => x.Ms).Take(12))
            Log.Info($"PERF thread {name}: {ms / 1000:F1} s cpu ({ms / frames:F2} ms/frame)");
        Log.Info($"PERF allocated {GC.GetTotalAllocatedBytes() / 1048576.0:F0} MB total ({GC.GetTotalAllocatedBytes() / 1024.0 / frames:F0} KB/frame), GC gen0 {GC.CollectionCount(0)} gen1 {GC.CollectionCount(1)} gen2 {GC.CollectionCount(2)}, pause {gcInfo.PauseTimePercentage:F1}%, heap {gcInfo.HeapSizeBytes / 1048576.0:F0} MB, working set {Environment.WorkingSet / 1048576.0:F0} MB");
    }
}

