using VitaSharp.Core;
using VitaSharp.Core.Util;

namespace VitaSharp.Android.Game;

/// <summary>
/// The session statistics of a game run on Android (<see cref="SessionReport"/>, the desktop Runner's end-of-run
/// lines). Android kills the game process more often than it lets it stop (swiped away, low memory, a crash), so the
/// performance part (<see cref="SessionReport.PeriodicLines"/>: CPU backend report, PERF cycles, PERF gpu / mobile,
/// AUDIO summary, PERF cpu / threads / allocated) also goes to the log file every 30 s
/// (<c>VITASHARP_SESSION_REPORT_SECONDS</c>, 0 = off) and when the game pauses. File only: logcat keeps the gameplay
/// quiet. The whole report is written when the game stops.
/// </summary>
public sealed class SessionStatistics(Emulator emu, Func<bool> paused)
{
    System.Threading.Timer? _timer;
    int _writing;
    volatile bool _stopped;
    volatile bool _running;

    /// <summary>The game started: the periodic report begins.</summary>
    public void Start()
    {
        _running = true;
        string? env = Environment.GetEnvironmentVariable(EnvVars.SessionReportSeconds);
        int seconds = int.TryParse(env, out int s) ? s : 30;
        if (seconds <= 0) return;
        _timer = new System.Threading.Timer(_ => { if (!paused()) WritePeriodic(); }, null, seconds * 1000, seconds * 1000);
    }

    /// <summary>The periodic lines now (the game paused: the last callback Android guarantees before a kill).</summary>
    public void WritePeriodic()
    {
        if (!_running || _stopped || Interlocked.Exchange(ref _writing, 1) != 0) return;
        try { foreach (var line in SessionReport.PeriodicLines(emu)) Log.InfoFileOnly(line); }
        catch (Exception e) { Log.Warn($"Periodic session report: {e.GetType().Name}: {e.Message}"); }
        finally { Volatile.Write(ref _writing, 0); }
    }

    /// <summary>Stops the timer and waits for a report in progress (the emulator is torn down next).</summary>
    public void Stop()
    {
        _stopped = true;
        if (_timer is { } t)
        {
            using var done = new ManualResetEvent(false);
            if (t.Dispose(done)) done.WaitOne(5000);
            _timer = null;
        }
        for (int i = 0; i < 100 && Volatile.Read(ref _writing) != 0; i++) Thread.Sleep(10);
    }

    /// <summary>The end-of-run report, once the emulation stopped.</summary>
    public void WriteFinal()
    {
        try { foreach (var line in SessionReport.Lines(emu)) Log.Info(line); }
        catch (Exception e) { Log.Warn($"Session report: {e.GetType().Name}: {e.Message}"); }
    }
}
