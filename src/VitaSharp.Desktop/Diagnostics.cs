using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>
/// "Capturar diagnóstico" (F10 in the game window, --diag-at=FRAME[,FRAME...], and debug-mode anomalies): the renderer
/// traces the next frame (draw list, render targets, sampled textures, screenshot) into
/// &lt;DataRoot&gt;/diagnostics/&lt;TITLEID&gt;_&lt;yyyyMMdd_HHmmss&gt;/, then this class adds info.txt, the log so far and the
/// emulator state, and zips the folder next to it. One capture at a time; requests during a capture are ignored.
/// See docs/notes/debug-mode.md.
/// </summary>
public sealed class Diagnostics
{
    readonly Emulator _emu;
    readonly Func<VulkanRenderer?> _renderer;
    readonly string[] _options;
    int _busy;
    Task _last = Task.CompletedTask;

    /// <summary>Short user feedback (window title for a few seconds).</summary>
    public Action<string>? Status;
    /// <summary>Extra info.txt lines from the host (window size...).</summary>
    public Func<string>? HostInfo;

    // automatic captures (debug mode): at most one per AutoInterval and AutoMax per session
    static readonly TimeSpan AutoInterval = TimeSpan.FromMinutes(5);
    const int AutoMax = 3;
    DateTime _lastAuto = DateTime.MinValue;
    int _autoCount;

    public Diagnostics(Emulator emu, Func<VulkanRenderer?> renderer, string[] options)
    {
        _emu = emu;
        _renderer = renderer;
        _options = options;
    }

    public static string Root => Path.Combine(Program.DataRoot, "diagnostics");

    /// <summary>Starts a capture unless one is running. Never blocks the caller (window/input thread).</summary>
    public bool Capture(string reason)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Log.Info($"[diag] {reason}: ignored, a capture is in progress");
            return false;
        }
        string dir;
        try
        {
            string title = SafeName(string.IsNullOrEmpty(_emu.TitleId) ? "NOTITLE" : _emu.TitleId);
            string baseName = $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}";
            dir = Path.Combine(Root, baseName);
            for (int i = 2; Directory.Exists(dir) || File.Exists(dir + ".zip"); i++) dir = Path.Combine(Root, $"{baseName}_{i}");
            Directory.CreateDirectory(dir);
        }
        catch (Exception e)
        {
            Log.Warn($"[diag] cannot create the capture folder: {e.Message}");
            Volatile.Write(ref _busy, 0);
            return false;
        }
        long guestFrame = DebugMode.Frame;
        Log.Info($"[diag] capture requested ({reason}) at guest frame {guestFrame}: {dir}");
        Status?.Invoke("Capturando diagnóstico...");
        var renderer = _renderer();
        var tcs = new TaskCompletionSource<DiagnosticCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool requested = renderer != null && renderer.RequestDiagnosticCapture(dir, r => tcs.TrySetResult(r));
        _last = Task.Run(async () =>
        {
            string? note = null;
            DiagnosticCaptureResult? res = null;
            try
            {
                if (!requested) note = renderer == null ? "no renderer: GPU part skipped" : "renderer busy with another capture";
                else if (await Task.WhenAny(tcs.Task, Task.Delay(15000)) == tcs.Task) res = tcs.Task.Result;
                else if (renderer!.CancelDiagnosticCapture()) note = "no frame was presented within 15 s (hang?): GPU part skipped, see state.txt";
                else
                {
                    // armed but not finished: the frame is still being traced (very slow frame); wait a bit more
                    if (await Task.WhenAny(tcs.Task, Task.Delay(60000)) == tcs.Task) res = tcs.Task.Result;
                    else note = "the traced frame did not complete within 75 s (hang?)";
                }
                Finish(dir, reason, guestFrame, renderer, res, note);
            }
            catch (Exception e) { Log.Warn($"[diag] capture failed: {e}"); Status?.Invoke("Falha ao salvar o diagnóstico (veja o log)"); }
            finally { Volatile.Write(ref _busy, 0); }
        });
        return true;
    }

    /// <summary>Debug mode: an anomaly on the presented image. Rate-limited automatic capture.</summary>
    public void OnAnomaly(string kind, string detail)
    {
        lock (this)
        {
            if (_autoCount >= AutoMax) { Log.Info($"[diag] anomaly '{kind}': automatic capture limit ({AutoMax} per session) reached"); return; }
            if (DateTime.Now - _lastAuto < AutoInterval) { Log.Info($"[diag] anomaly '{kind}': last automatic capture less than {AutoInterval.TotalMinutes:F0} min ago, skipped"); return; }
            if (!Capture($"auto: {kind} - {detail}")) return;
            _lastAuto = DateTime.Now;
            _autoCount++;
        }
    }

    /// <summary>Waits for a running capture (end of headless runs).</summary>
    public void WaitIdle(int ms = 120000) { try { _last.Wait(ms); } catch { } }

    void Finish(string dir, string reason, long guestFrame, VulkanRenderer? renderer, DiagnosticCaptureResult? res, string? note)
    {
        // emulator state: threads, held mutexes, recent HLE calls (debug mode), GPU queue
        File.WriteAllText(Path.Combine(dir, "state.txt"), Watchdog.DescribeState(_emu, renderer, 16));
        if (res == null && renderer != null) File.WriteAllText(Path.Combine(dir, "frames.txt"), renderer.RecentFrames());
        File.WriteAllText(Path.Combine(dir, "info.txt"), BuildInfo(reason, guestFrame, renderer, res, note));
        string zip = dir + ".zip";
        Log.Info($"[diag] saved{(note != null ? $" ({note})" : "")}: {dir} and {zip}" +
                 (res != null ? $" - {res.Draws} draws, {res.Targets} render targets, {res.Textures} textures traced" : ""));
        Log.Flush();
        CopyLog(Path.Combine(dir, "log.txt"));
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        Status?.Invoke($"Diagnóstico salvo: {zip}");
    }

    static void CopyLog(string dest)
    {
        if (Log.FilePath is not { } src || !File.Exists(src)) { File.WriteAllText(dest, "(no log file: console logging only)\n"); return; }
        // the log is open for writing (shared read/write): copy what is there now
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(dest);
        input.CopyTo(output);
    }

    string BuildInfo(string reason, long guestFrame, VulkanRenderer? renderer, DiagnosticCaptureResult? res, string? note)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VitaSharp diagnostic capture");
        sb.AppendLine($"Reason: {reason}");
        if (note != null) sb.AppendLine($"NOTE: {note}");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local), {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Title: {_emu.TitleId} {_emu.Title}");
        sb.AppendLine($"App path: {_emu.AppPath}");
        sb.AppendLine($"Guest frame when requested: {guestFrame} (frames presented by the display)");
        if (res != null)
        {
            sb.AppendLine($"Traced frame: between renderer presents #{res.Present} and #{res.Present + 1}; {res.Draws} draws, {res.Targets} render targets dumped, {res.Textures} textures sampled ({res.Milliseconds:F0} ms)");
            sb.AppendLine($"Presented frame: {res.FrameWidth}x{res.FrameHeight}{(res.Screenshot ? "" : " (no screenshot)")}");
            if (res.Error != null) sb.AppendLine($"Renderer error: {res.Error}");
        }
        sb.AppendLine($"Build: {BuildVersion()}");
        sb.AppendLine($"Runtime: .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} logical CPUs");
        sb.AppendLine($"Data root: {Program.DataRoot}");
        sb.AppendLine($"Command line: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        sb.AppendLine(Core.Cpu.Jit.Describe());
        sb.AppendLine($"Debug mode: {(DebugMode.Enabled ? "on" : "off")}");
        if (HostInfo?.Invoke() is { Length: > 0 } host) sb.AppendLine(host);
        if (renderer != null) sb.AppendLine(renderer.DescribeForDiagnostics());
        sb.AppendLine($"GPU queue: submitted {_emu.Gpu.Submitted}, completed {_emu.Gpu.Completed}");
        sb.AppendLine("Environment overrides:");
        int env = 0;
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && k.StartsWith("VITASHARP_", StringComparison.OrdinalIgnoreCase)) { sb.AppendLine($"  {k}={e.Value}"); env++; }
        if (env == 0) sb.AppendLine("  (none)");
        sb.AppendLine();
        sb.AppendLine("Files:");
        sb.AppendLine("  screenshot.png      the presented frame at the end of the traced frame");
        sb.AppendLine("  draws.txt           every scene and draw of the traced frame (programs, targets, textures, MSAA, state)");
        sb.AppendLine("  shaders/            GLSL of every program drawn (<gxp hash>_<hint key>.vert/.frag)");
        sb.AppendLine("  targets/            every render target as PNG + targets.txt (per-channel min/max/mean, NaN/Inf, guest copy)");
        sb.AppendLine("  textures/           every texture sampled in the frame as PNG + textures.txt (noise/seam checks, mips)");
        sb.AppendLine("  frames.txt          the last 120 frames: draws, scenes, new shaders/pipelines/targets, biggest hitch cause");
        sb.AppendLine("  renderer.txt        GPU/driver/settings, renderer state and statistics so far");
        sb.AppendLine("  state.txt           guest threads (wait state, backtrace, recent HLE calls in debug mode), mutexes, GPU queue");
        sb.AppendLine("  log.txt             the session log up to the capture");
        return sb.ToString();
    }

    public static string BuildVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(Diagnostics).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string when = "";
        try { when = $", built {File.GetLastWriteTime(Path.Combine(AppContext.BaseDirectory, "VitaSharp.Core.dll")):yyyy-MM-dd HH:mm}"; } catch { }
        return $"VitaSharp {info ?? asm.GetName().Version?.ToString() ?? "?"}{when}";
    }

    static string SafeName(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}
