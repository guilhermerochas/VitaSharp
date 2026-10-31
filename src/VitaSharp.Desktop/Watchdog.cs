using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>
/// Hang/stall watchdog (always on; one check per second, nothing logged while frames keep coming): when no frame is
/// presented for <see cref="StallSeconds"/> or the GPU worker stops completing submitted commands, it logs the guest
/// threads (status, wait object, backtrace, recent HLE calls in debug mode), held mutexes with waiters, the GPU queue
/// (submitted/completed, the command being executed) and the renderer state. Repeated at 30 s and 120 s of the same stall.
/// Debug mode also logs a periodic PERF line and the warning/HLE-error summaries from here.
/// </summary>
public sealed class Watchdog : IDisposable
{
    const double StallSeconds = 5;
    readonly Emulator _emu;
    readonly Func<VulkanRenderer?> _renderer;
    readonly Timer _timer;
    long _lastFrames = -1, _lastCompleted = -1;
    DateTime _frameAt = DateTime.Now, _gpuAt = DateTime.Now;
    int _reportsThisStall;
    // debug mode periodic lines
    DateTime _perfAt = DateTime.Now, _summaryAt = DateTime.Now;
    DateTime _audioAt = DateTime.Now;
    bool _debugWas;
    long _perfFrames;
    TimeSpan _perfCpu;
    long _perfAlloc;

    public Watchdog(Emulator emu, Func<VulkanRenderer?> renderer)
    {
        _emu = emu;
        _renderer = renderer;
        _perfCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    void Tick()
    {
        try
        {
            if (!_emu.Running) return;
            var now = DateTime.Now;
            if (EmulationPause.IsPaused)
            {
                // a paused emulation presents nothing on purpose: no stall, and the stall timers restart at the resume
                _frameAt = _gpuAt = now;
                _reportsThisStall = 0;
                return;
            }
            long frames = Core.Hle.Modules.Display.FramesPresented;
            long completed = _emu.Gpu.Completed, submitted = _emu.Gpu.Submitted;
            if (frames != _lastFrames) { _lastFrames = frames; _frameAt = now; _reportsThisStall = 0; }
            if (completed != _lastCompleted || submitted == completed) { _lastCompleted = completed; _gpuAt = now; }
            double frameStall = (now - _frameAt).TotalSeconds, gpuStall = (now - _gpuAt).TotalSeconds;
            // only once the game presents (boot and loading before the first frame can take long)
            double stall = Math.Max(frames > 0 ? frameStall : 0, gpuStall);
            double[] thresholds = [StallSeconds, 30, 120];
            if (_reportsThisStall < thresholds.Length && stall >= thresholds[_reportsThisStall])
            {
                _reportsThisStall++;
                string what = gpuStall >= stall ? $"GPU worker made no progress for {gpuStall:F0} s ({submitted - completed} commands pending)" : $"no frame presented for {frameStall:F0} s";
                Log.Warn($"[watchdog] STALL: {what} (frame {frames})\n{DescribeState(_emu, _renderer())}");
                Log.Flush();
            }
            // F9 turned debug mode on: the periodic audio lines count from now (AudioDiagnostics logged a snapshot)
            if (DebugMode.Enabled && !_debugWas) _audioAt = now;
            _debugWas = DebugMode.Enabled;
            if (DebugMode.Enabled) Periodic(now, frames);
        }
        catch (Exception e) { Log.Warn($"[watchdog] check failed: {e.Message}"); }
    }

    void Periodic(DateTime now, long frames)
    {
        if ((now - _perfAt).TotalSeconds >= 60)
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var cpu = proc.TotalProcessorTime;
            long alloc = GC.GetTotalAllocatedBytes();
            double secs = (now - _perfAt).TotalSeconds;
            long df = Math.Max(1, frames - _perfFrames);
            Log.Info($"PERF periodic f{frames}: {(frames - _perfFrames) / secs:F1} fps, process cpu {(cpu - _perfCpu).TotalMilliseconds / df:F1} ms/frame, " +
                     $"allocated {(alloc - _perfAlloc) / 1024.0 / df:F0} KB/frame, GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, " +
                     $"working set {Environment.WorkingSet / 1048576.0:F0} MB, GPU queue pending {_emu.Gpu.Pending}, threads {proc.Threads.Count}");
            if (_renderer() is { } r) Log.Info($"PERF periodic f{frames} {r.VramSummary()}");
            _perfAt = now; _perfFrames = frames; _perfCpu = cpu; _perfAlloc = alloc;
        }
        if ((now - _audioAt).TotalSeconds >= AudioDiagnostics.PeriodSeconds)
        {
            _audioAt = now;
            AudioDiagnostics.Periodic();
        }
        if ((now - _summaryAt).TotalMinutes >= 10)
        {
            _summaryAt = now;
            LogSummaries();
        }
    }

    /// <summary>Debug mode: warnings by count and HLE error/unimplemented summaries (also at the end of a run).</summary>
    public static void LogSummaries()
    {
        foreach (var l in SessionReport.DebugSummaryLines()) Log.Info(l);
    }

    /// <summary>Threads, mutexes, GPU queue and renderer state (hang reports, diagnostic captures).</summary>
    public static string DescribeState(Emulator emu, VulkanRenderer? renderer, int hleCalls = 6)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"guest frame {Core.Hle.Modules.Display.FramesPresented}; GPU queue: submitted {emu.Gpu.Submitted}, completed {emu.Gpu.Completed}, executing {emu.Gpu.Executing?.GetType().Name ?? "nothing"}");
        if (renderer != null) sb.AppendLine(renderer.DebugState());
        try
        {
            foreach (var t in emu.Kernel.Threads.ToArray())
            {
                sb.AppendLine("thread " + t.Describe());
                foreach (var call in t.RecentHleCalls(hleCalls)) sb.AppendLine("    hle " + call);
            }
            // the applications the shell started run as processes of their own (read in their address space)
            foreach (var p in emu.Vita?.Processes.Where(p => !ReferenceEquals(p, emu)) ?? [])
            {
                sb.AppendLine($"process {p.Describe()}{(p.Suspended ? " (suspended)" : "")}{(p.IsForeground ? " (in front)" : "")}:");
                using var space = Core.Memory.AddressSpace.Use(p.Space);
                foreach (var t in p.Kernel.Threads.ToArray())
                {
                    sb.AppendLine("  thread " + t.Describe());
                    foreach (var call in t.RecentHleCalls(hleCalls)) sb.AppendLine("      hle " + call);
                }
            }
            foreach (var m in emu.Kernel.Objects.All<Core.Kernel.Mutex>().ToArray())
                if (m.Owner != null && m.Waiters > 0)
                    sb.AppendLine($"mutex '{m.Name}' held by '{m.Owner.Name}' ({m.Owner.Status}) count {m.LockCount}, {m.Waiters} waiting");
        }
        catch (Exception e) { sb.AppendLine($"(thread list changed while reading: {e.Message})"); }
        if (renderer != null)
        {
            sb.AppendLine("last frames:");
            sb.Append(renderer.RecentFrames(30));
        }
        return sb.ToString().TrimEnd();
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>
/// Session logs: logs/last.log is the current session; the previous one is kept as
/// logs/&lt;yyyyMMdd_HHmmss&gt;_&lt;TITLEID&gt;.log (its start time and title, from its first line), the newest
/// <see cref="Keep"/> of those are kept.
/// </summary>
public static class LogFiles
{
    public const int Keep = 10;
    const string Header = "VitaSharp session ";

    public static void OpenSession(string dir, string titleId)
    {
        string last = Path.Combine(dir, "last.log");
        string? opened = null;
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(last) && new FileInfo(last).Length > 0)
            {
                var (stamp, title) = ReadHeader(last);
                string dest = Path.Combine(dir, $"{stamp}_{title}.log");
                for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{stamp}_{title}_{i}.log");
                File.Move(last, dest);
            }
            Log.OpenFile(last);
            opened = last;
        }
        catch (IOException)
        {
            // another VitaSharp holds last.log: this session gets its own file
            opened = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{titleId}.log");
            Log.OpenFile(opened);
        }
        Log.Info($"{Header}{titleId} {DateTime.Now:yyyyMMdd_HHmmss} {Diagnostics.BuildVersion()}");
        Prune(dir);
    }

    static (string Stamp, string Title) ReadHeader(string path)
    {
        string stamp = File.GetLastWriteTime(path).ToString("yyyyMMdd_HHmmss"), title = "unknown";
        try
        {
            using var r = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            for (int i = 0; i < 5 && r.ReadLine() is { } line; i++)
            {
                int k = line.IndexOf(Header, StringComparison.Ordinal);
                if (k < 0) continue;
                var parts = line[(k + Header.Length)..].Split(' ');
                if (parts.Length >= 2) { title = parts[0]; stamp = parts[1]; }
                break;
            }
        }
        catch (IOException) { }
        return (string.Concat(stamp.Where(c => char.IsLetterOrDigit(c) || c == '_')), string.Concat(title.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-')));
    }

    static void Prune(string dir)
    {
        try
        {
            var old = Directory.GetFiles(dir, "*_*.log").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep);
            foreach (var f in old) { try { f.Delete(); } catch (IOException) { } }
        }
        catch (IOException) { }
    }

    /// <summary>Title id of an app folder (…/ux0/app/PCSC00045), else "unknown".</summary>
    public static string TitleOf(string appPath)
    {
        string name = Path.GetFileName(appPath.TrimEnd('/', '\\'));
        if (name.Length == 9 && name[..4].All(char.IsAsciiLetterUpper) && name[4..].All(char.IsAsciiDigit)) return name;
        return string.IsNullOrEmpty(name) ? "unknown" : string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-'));
    }
}
