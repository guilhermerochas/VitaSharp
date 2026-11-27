using System.Diagnostics;

namespace VitaSharp.Core.Util;

/// <summary>
/// The guest's monotonic clock (process time, vblanks, media). It follows real time; in turbo mode frame pacing
/// waits are skipped by moving the clock forward to the deadline instead of sleeping, so games still see one
/// vblank period per frame while running as fast as the host allows (used by automated tests).
/// </summary>
public static partial class GuestClock
{
    private static readonly long s_start = Stopwatch.GetTimestamp();

    /// <summary>The host pause total when the clock started: only the pauses after it are left out (the process may
    /// have collected before the clock existed, which would put the reading below zero).</summary>
    private static readonly long s_startPause = RealPauseTicks();
    private static long s_offset; // Stopwatch ticks added to real elapsed time

    /// <summary>Skip frame pacing (vblank and audio-without-device waits).</summary>
    public static bool Turbo { get; set; }

    /// <summary>Guest time runs this many times faster than real time (tests; 1 = normal).</summary>
    public static double Speed { get; set; } = 1.0;

    private static long RealElapsed => Speed == 1.0 ? HostElapsed() : (long)(HostElapsed() * Speed);

    /// <summary>
    /// Host time since the start that the emulated machine lived through: real time minus the host's stop-the-world
    /// pauses (the .NET garbage collector's, <see cref="GC.GetTotalPauseDuration"/>). While the runtime has every thread
    /// stopped no guest thread can run, so on the console's clock that time never passed, as the time spent paused in
    /// the emulator's menu never passes (<see cref="Freeze"/>). Counted, a 9 ms collection in the middle of Dead Space's
    /// audio mixing let the port's audio thread wake from its 11 ms sleep while the mixer, stopped with everything else,
    /// was still in its round, and its audio stopped for good. The pause total is read on both sides of the timestamp,
    /// so a reading never mixes a timestamp from after a collection with the pause total from before it.
    /// VITASHARP_GC_PAUSE_CLOCK=0 counts the pauses again (A/B).
    /// </summary>
    private static long HostElapsed()
    {
        if (!s_excludeHostPauses)
        {
            return HostTimestamp() - s_start;
        }

        while (true)
        {
            long pause = HostPauseTicks();
            long now = HostTimestamp();
            if (HostPauseTicks() == pause)
            {
                return ElapsedWithoutPauses(s_start, s_startPause, now, pause);
            }
        }
    }

    /// <summary>Host time from <paramref name="start"/> to <paramref name="now"/> minus the pauses between them (the
    /// pause totals at both points).</summary>
    internal static long ElapsedWithoutPauses(long start, long startPause, long now, long pause) => now - start - (pause - startPause);

    private static readonly bool s_excludeHostPauses = Environment.GetEnvironmentVariable(EnvVars.GcPauseClock) != "0";

    /// <summary>Test seams: the host timestamp and the host pause total (Stopwatch ticks) the clock reads (null: the real
    /// ones), so a test decides when time passes and when the host pauses instead of measuring the host.</summary>
    internal static Func<long>? HostTimestampForTest { get; set; }
    internal static Func<long>? HostPauseForTest { get; set; }

    private static long HostTimestamp() => HostTimestampForTest?.Invoke() ?? Stopwatch.GetTimestamp();

    private static long HostPauseTicks()
    {
        if (HostPauseForTest is { } pause)
        {
            return pause();
        }

        return RealPauseTicks();
    }

    private static long RealPauseTicks()
    {
        long ticks = GC.GetTotalPauseDuration().Ticks;
        return Stopwatch.Frequency == TimeSpan.TicksPerSecond ? ticks : (long)(ticks * ((double)Stopwatch.Frequency / TimeSpan.TicksPerSecond));
    }

    /// <summary>The clock's reading while the emulation is paused (<see cref="EmulationPause"/>); NotFrozen otherwise.</summary>
    private static long s_frozen = NotFrozen;
    private const long NotFrozen = long.MinValue;

    public static long Ticks
    {
        get
        {
            long f = Volatile.Read(ref s_frozen);
            return f != NotFrozen ? f : RealElapsed + Interlocked.Read(ref s_offset);
        }
    }

    /// <summary>The clock is stopped (the emulation is paused).</summary>
    public static bool Frozen => Volatile.Read(ref s_frozen) != NotFrozen;

    /// <summary>Stops the clock at its current reading (<see cref="EmulationPause.Pause"/>).</summary>
    internal static void Freeze()
    {
        if (Frozen)
        {
            return;
        }

        Volatile.Write(ref s_frozen, RealElapsed + Interlocked.Read(ref s_offset));
    }

    /// <summary>Restarts the clock from the reading it stopped at: the time spent frozen never reaches the guest, so a
    /// game sees no jump on resume. Moves made by <see cref="AdvanceTo"/> while frozen are dropped.</summary>
    internal static void Thaw()
    {
        long f = Volatile.Read(ref s_frozen);
        if (f == NotFrozen)
        {
            return;
        }

        Interlocked.Exchange(ref s_offset, f - RealElapsed);
        Volatile.Write(ref s_frozen, NotFrozen);
    }

    /// <summary>Tests that decide guest time themselves: moves the stopped clock (<see cref="Freeze"/>) forward by
    /// <paramref name="microseconds"/>, rounded up to whole ticks, so a timeout passes exactly when the test says and
    /// never because the host was slow.</summary>
    internal static void AdvanceFrozen(long microseconds)
    {
        long f = Volatile.Read(ref s_frozen);
        if (f == NotFrozen)
        {
            throw new InvalidOperationException("only a frozen guest clock is moved by hand");
        }

        long ticks = (microseconds * Stopwatch.Frequency + 999_999) / 1_000_000;
        Volatile.Write(ref s_frozen, f + ticks);
        Kernel.TimerDispatcher.Poke(); // the timers whose event this move reached fire now
    }

    public static long Microseconds => (long)(Ticks * (1_000_000.0 / Stopwatch.Frequency));
    public static double Seconds => Ticks / (double)Stopwatch.Frequency;

    public static long FromSeconds(double s) => (long)(s * Stopwatch.Frequency);

    private static long s_lastObserved;

    /// <summary>
    /// The clock as a guest thread reads it (process/system time): never behind the thread's own emulated CPU
    /// position (<paramref name="threadPosition"/>, 0 = none; under CPU pacing a thread may run up to ~1 ms ahead of
    /// real time before it sleeps), and monotonic across threads. Without this a busy-wait loop on the clock sees
    /// time move in sleep-sized steps and overshoots its deadline by up to a step (frame limiters lose ~1 ms per
    /// frame), and time measured over a short burst of work reads as zero instead of its emulated duration.
    /// </summary>
    public static long Observed(long threadPosition)
    {
        long t = Math.Max(Ticks, threadPosition);
        long last = Volatile.Read(ref s_lastObserved);
        while (t > last)
        {
            long seen = Interlocked.CompareExchange(ref s_lastObserved, t, last);
            if (seen == last)
            {
                return t;
            }

            last = seen;
        }
        return last;
    }

    /// <summary>The latest reading any guest thread got from <see cref="Observed"/>: a thread that starts a timed wait
    /// may already have read the clock this far ahead of real time (another thread's CPU position moved it), so the
    /// wait is measured from here, or the thread would see it end early (a 2 ms delay measured as 1.8 ms).</summary>
    internal static long ObservedFloor => Volatile.Read(ref s_lastObserved);

    public static long ToMicroseconds(long ticks) => (long)(ticks * (1_000_000.0 / Stopwatch.Frequency));

    /// <summary>Guest microseconds as clock ticks (the inverse of <see cref="ToMicroseconds"/>, rounded up so a time
    /// saved in microseconds is never before the moment it was taken from).</summary>
    public static long FromMicroseconds(long microseconds) => (long)Math.Ceiling(microseconds * (Stopwatch.Frequency / 1_000_000.0));

    /// <summary>Waits until the clock reaches <paramref name="deadline"/> (ticks); in turbo mode jumps there. While the
    /// emulation is paused it first waits for the resume (see <see cref="EmulationPause.WaitWhilePaused"/>).</summary>
    public static void WaitUntil(long deadline, bool allowSpin = true)
    {
        if (EmulationPause.IsPaused && EmulationPause.WaitWhilePaused())
        {
            Thread.Sleep(1);
            return;
        } // the thread finishing a GPU scene: the frozen clock never gets there
        long now = Ticks;
        if (now >= deadline)
        {
            return;
        }

        if (Turbo)
        {
            AdvanceTo(deadline);
            return;
        }
        PreciseSleep.Until(Stopwatch.GetTimestamp() + (long)((deadline - now) / Speed), allowSpin);
    }

    /// <summary>Moves the clock forward so that it reads at least <paramref name="deadline"/>. Never moves it back,
    /// and concurrent callers do not add up: the clock ends at the latest deadline.</summary>
    private static void AdvanceTo(long deadline)
    {
        while (true)
        {
            long off = Interlocked.Read(ref s_offset);
            long needed = deadline - RealElapsed;
            if (needed <= off)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref s_offset, needed, off) == off)
            {
                return;
            }
        }
    }
}

/// <summary>A stopwatch measuring guest time (follows turbo mode).</summary>
public sealed class GuestStopwatch
{
    private long _start, _accumulated;
    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _start = GuestClock.Ticks;
        IsRunning = true;
    }
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _accumulated += GuestClock.Ticks - _start;
        IsRunning = false;
    }
    public void Reset()
    {
        _accumulated = 0;
        IsRunning = false;
    }
    public long ElapsedTicks => _accumulated + (IsRunning ? GuestClock.Ticks - _start : 0);
    public long ElapsedMicroseconds => (long)(ElapsedTicks * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
}

/// <summary>Debug: per-source time multipliers (VITASHARP_TSCALE_PROC / _VBLANK / _WAIT) to bisect timing-dependent bugs.</summary>
public static class TimeScale
{
    public static readonly double Proc = Read(EnvVars.TscaleProc), VBlank = Read(EnvVars.TscaleVblank), Wait = Read(EnvVars.TscaleWait);
    private static double Read(string name) => double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 1.0;
}
