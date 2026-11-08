using System.Globalization;
using VitaSharp.Core;
using VitaSharp.Core.Capture;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>
/// "Gravar tudo" on the desktop (docs/notes/session-capture.md): F8 starts and stops a session capture into
/// &lt;data&gt;/captures/&lt;TITLEID&gt;_&lt;date&gt;_tudo/ (+ .zip). The pre-roll is armed by --capture-preroll[=SECONDS]
/// (launcher "Pre-gravacao"), VITASHARP_CAPTURE_PREROLL, or debug mode (F9 / --debug-mode: 10 s). --capture-at=A:B
/// records guest frames A..B (tests). The re-run side: <c>rerun DIR</c> and --capture-playback.
/// </summary>
sealed class SessionCaptureControl : IDisposable
{
    public const double DefaultPreRoll = 10;
    public static string Root => Path.Combine(Program.DataRoot, "captures");

    readonly double? _explicitPreRoll;
    public SessionCapture Capture { get; }

    public SessionCaptureControl(Emulator emu, VulkanRendererOptions ro, Func<VulkanRenderer?> renderer, string[] options, Action<string>? status,
        Func<string>? hostInfo, SessionCaptureOptions? overrides = null)
    {
        var o = overrides ?? new SessionCaptureOptions { Root = Root, SetPresentTap = null! };
        o.SetPresentTap = tap => ro.PresentTap = tap;
        o.Platform = "desktop";
        o.DumpHeader = () => GpuDumpControl.BuildHeader(emu, renderer(), options, hostInfo?.Invoke());
        o.Threads = () => Watchdog.DescribeState(emu, renderer(), 16);
        o.LogFile = () => Log.FilePath;
        o.Status = status;
        o.RunOptions = options;
        o.Info = () =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"build: {Diagnostics.BuildVersion()}");
            sb.AppendLine($"runtime: .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} logical CPUs");
            sb.AppendLine($"cpu: {Core.Cpu.Jit.Describe()}, {Core.Cpu.CpuCore.InstructionsPerSecond / 1_000_000} MHz pacing (0 = unpaced)");
            sb.AppendLine($"debug-mode: {(DebugMode.Enabled ? "on" : "off")}");
            if (hostInfo?.Invoke() is { Length: > 0 } h) sb.AppendLine($"host: {h}");
            if (renderer() is { } r) foreach (var line in r.DescribeForDiagnostics().Split('\n')) sb.AppendLine($"renderer: {line.TrimEnd('\r')}");
            return sb.ToString();
        };
        Capture = new SessionCapture(emu, o);
        string? spec = options.FirstOrDefault(x => x == "--capture-preroll" || x.StartsWith("--capture-preroll="))
            is { } opt ? (opt.Contains('=') ? opt[(opt.IndexOf('=') + 1)..] : DefaultPreRoll.ToString(CultureInfo.InvariantCulture))
            : Environment.GetEnvironmentVariable(EnvVars.CapturePreRoll);
        if (spec != null && double.TryParse(spec, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) _explicitPreRoll = Math.Max(0, s);
        if (overrides != null) _explicitPreRoll = 0; // a re-run's own recording has no pre-roll
        UpdatePreRoll();
    }

    /// <summary>The pre-roll follows debug mode unless it was set explicitly.</summary>
    public void UpdatePreRoll() => Capture.SetPreRoll(_explicitPreRoll ?? (DebugMode.Enabled ? DefaultPreRoll : 0));

    /// <summary>--capture-at=START:STOP (guest frames, or VITASHARP_CAPTURE_AT): a recording over those frames.</summary>
    public void StartScheduled(string[] options)
    {
        string? spec = Runner.Opt(options, "--capture-at") ?? Environment.GetEnvironmentVariable(EnvVars.CaptureAt);
        if (spec == null) return;
        var p = spec.Split(':');
        if (p.Length != 2 || !long.TryParse(p[0].TrimStart('f'), out long a) || !long.TryParse(p[1].TrimStart('f'), out long b) || b <= a)
        {
            Log.Warn($"--capture-at={spec} not recognised (use START:STOP guest frames)");
            return;
        }
        new Thread(() =>
        {
            while (Core.Hle.Modules.Display.FramesPresented < a) Thread.Sleep(5);
            Capture.Start("--capture-at");
            while (Core.Hle.Modules.Display.FramesPresented < b) Thread.Sleep(5);
            // the start may still be running (save state); Stop is refused while it is
            for (int i = 0; i < 400 && !Capture.Stop("--capture-at"); i++) Thread.Sleep(25);
        }) { IsBackground = true, Name = "capture-at" }.Start();
    }

    public void Dispose()
    {
        Capture.StopAndWait();
        Capture.Dispose();
    }

    // ------------------------------------------------------------------ re-run

    /// <summary>
    /// <c>rerun DIR [--from=preroll|start] [--window] [--seconds=N] [options]</c>: boots the capture's game with the
    /// capture's options, loads its state (the pre-roll's by default), plays the recorded input back by guest clock and
    /// records the re-run as a capture of its own (no state, no GPU dump unless --rerun-gpu-dump) into
    /// DIR/reruns/, then analyzes it. Headless unless --window.
    /// </summary>
    public static int Rerun(string dir, string[] extra)
    {
        dir = Path.GetFullPath(dir);
        var session = ReadSession(Path.Combine(dir, "session.txt"));
        if (session.Count == 0) { Console.Error.WriteLine($"{dir}: no session.txt (not a Gravar tudo capture)"); return 1; }
        string from = Runner.Opt(extra, "--from") ?? (File.Exists(Path.Combine(dir, "state_preroll.vss")) ? "preroll" : "start");
        string state = Path.Combine(dir, from == "preroll" ? "state_preroll.vss" : "state_start.vss");
        if (!File.Exists(state)) { Console.Error.WriteLine($"{state} not found: this capture has no {from} save state"); return 1; }
        string titleId = session.GetValueOrDefault("title-id", "");
        string app;
        try { app = Program.ResolveApp(titleId); }
        catch (DirectoryNotFoundException) { app = session.GetValueOrDefault("app-path", ""); }
        if (!Directory.Exists(app)) { Console.Error.WriteLine($"game {titleId} not installed (and {app} not found)"); return 1; }
        string[] drop = ["--capture", "--load-state", "--save-state", "--gpu-dump-at", "--diag-at", "--input=", "--nav", "--seconds", "--frames", "--fullscreen", "--headless", "--start-paused", "--debug-mode-at"];
        var original = session.GetValueOrDefault("options", "").Split('\t', StringSplitOptions.RemoveEmptyEntries).Where(o => !drop.Any(o.StartsWith));
        bool window = extra.Contains("--window");
        var opts = original
            .Concat(extra.Where(e => e != "--window" && !e.StartsWith("--from=")))
            .Append($"--load-state={state}")
            .Append($"--capture-playback={dir}@{from}")
            .ToList();
        if (!window) { opts.Add("--headless"); opts.Add("--vulkan"); if (!extra.Any(e => e.StartsWith("--seconds="))) opts.Add("--seconds=3600"); }
        Console.WriteLine($"re-run of {Path.GetFileName(dir)} from {Path.GetFileName(state)}: run {app} {string.Join(' ', opts)}");
        return Runner.Run(app, opts.ToArray());
    }

    static Dictionary<string, string> ReadSession(string path)
    {
        var d = new Dictionary<string, string>();
        if (!File.Exists(path)) return d;
        foreach (var line in File.ReadLines(path))
        {
            int c = line.IndexOf(": ", StringComparison.Ordinal);
            if (c > 0 && !line.StartsWith(' ')) d.TryAdd(line[..c], line[(c + 2)..]);
        }
        return d;
    }

    /// <summary>--capture-playback=DIR@preroll|start: the recorded input replaces the host's from the moment the
    /// state is loaded; the re-run is recorded into DIR/reruns/ and the run ends 2 s after the last recorded event.</summary>
    public sealed class Playback
    {
        public InputPlayback Input { get; }
        readonly long _fromUs;
        readonly string _dir;
        public volatile bool Done;

        Playback(string dir, InputPlayback input, long fromUs) { _dir = dir; Input = input; _fromUs = fromUs; }

        public static Playback? Install(Emulator emu, VulkanRendererOptions ro, Func<VulkanRenderer?> renderer, string[] options)
        {
            if (Runner.Opt(options, "--capture-playback") is not { } spec) return null;
            int at = spec.LastIndexOf('@');
            string dir = at > 1 ? spec[..at] : spec, from = at > 1 ? spec[(at + 1)..] : "preroll";
            var session = ReadSession(Path.Combine(dir, "session.txt"));
            string key = from == "preroll" ? "preroll-state" : "start-state";
            var line = session.GetValueOrDefault(key, "");
            var parts = line.Split(' ');
            int gi = Array.IndexOf(parts, "guest-us");
            if (gi < 0 || gi + 1 >= parts.Length || !long.TryParse(parts[gi + 1], out long fromUs))
            {
                Log.Warn($"--capture-playback: {key} not found in {dir}/session.txt");
                return null;
            }
            var input = InputPlayback.Load(Path.Combine(dir, "input.tsv"));
            emu.Host.Input = input;
            emu.Host.Motion = input;
            CaptureHooks.Random = input;
            var pb = new Playback(dir, input, fromUs);
            var root = Path.Combine(dir, "reruns");
            var control = new SessionCaptureControl(emu, ro, renderer, options.Where(o => !o.StartsWith("--capture-preroll")).ToArray(), null, null,
                new SessionCaptureOptions { Root = root, SetPresentTap = null!, StartState = false, GpuDump = options.Contains("--rerun-gpu-dump"), Zip = false, StopHint = "re-run" });
            SaveStates.Loaded += _ =>
            {
                input.Activate(fromUs);
                control.Capture.Start("re-run");
                new Thread(() =>
                {
                    while (GuestClock.Microseconds < input.LastUs + 2_000_000 && emu.Running) Thread.Sleep(20);
                    for (int i = 0; i < 400 && !control.Capture.Stop("re-run end"); i++) Thread.Sleep(25);
                    control.Capture.Idle.Wait(180000);
                    string cmp = control.Capture.Dir is { } rd ? CaptureAnalyzer.Compare(pb._dir, rd) : "no re-run capture";
                    Log.Info($"[capture] re-run recorded: {control.Capture.Dir} (report.txt there; compare.txt against the original); {cmp}");
                    Console.WriteLine($"re-run capture: {control.Capture.Dir}");
                    Console.WriteLine(cmp);
                    pb.Done = true;
                }) { IsBackground = true, Name = "rerun-end" }.Start();
            };
            Log.Info($"[capture] re-run of {dir} from the {from} state (guest time {fromUs / 1e6:F3} s): {input.Events} recorded events");
            return pb;
        }
    }
}
