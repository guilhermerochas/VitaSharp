using System.Diagnostics;

namespace VitaSharp.Core.Util;

/// <summary>
/// The guest's monotonic clock (process time, vblanks, media). It follows real time; in turbo mode frame pacing
/// waits are skipped by moving the clock forward to the deadline instead of sleeping, so games still see one
/// vblank period per frame while running as fast as the host allows (used by automated tests).
/// </summary>
public static class GuestClock
{
    static readonly long Start = Stopwatch.GetTimestamp();
    static long _offset; // Stopwatch ticks added to real elapsed time

    /// <summary>Skip frame pacing (vblank and audio-without-device waits).</summary>
    public static bool Turbo { get; set; }

    /// <summary>Guest time runs this many times faster than real time (tests; 1 = normal).</summary>
    public static double Speed { get; set; } = 1.0;

    static long RealElapsed => Speed == 1.0 ? Stopwatch.GetTimestamp() - Start : (long)((Stopwatch.GetTimestamp() - Start) * Speed);

    public static long Ticks => RealElapsed + Interlocked.Read(ref _offset);
    public static long Microseconds => (long)(Ticks * (1_000_000.0 / Stopwatch.Frequency));
    public static double Seconds => Ticks / (double)Stopwatch.Frequency;

    public static long FromSeconds(double s) => (long)(s * Stopwatch.Frequency);

    /// <summary>Waits until the clock reaches <paramref name="deadline"/> (ticks); in turbo mode jumps there.</summary>
    public static void WaitUntil(long deadline, bool allowSpin = true)
    {
        long now = Ticks;
        if (now >= deadline) return;
        if (Turbo) { AdvanceTo(deadline); return; }
        PreciseSleep.Until(Stopwatch.GetTimestamp() + (long)((deadline - now) / Speed), allowSpin);
    }

    /// <summary>Moves the clock forward so that it reads at least <paramref name="deadline"/>. Never moves it back,
    /// and concurrent callers do not add up: the clock ends at the latest deadline.</summary>
    static void AdvanceTo(long deadline)
    {
        while (true)
        {
            long off = Interlocked.Read(ref _offset);
            long needed = deadline - RealElapsed;
            if (needed <= off) return;
            if (Interlocked.CompareExchange(ref _offset, needed, off) == off) return;
        }
    }
}

/// <summary>A stopwatch measuring guest time (follows turbo mode).</summary>
public sealed class GuestStopwatch
{
    long _start, _accumulated;
    public bool IsRunning { get; private set; }

    public void Start() { if (IsRunning) return; _start = GuestClock.Ticks; IsRunning = true; }
    public void Stop() { if (!IsRunning) return; _accumulated += GuestClock.Ticks - _start; IsRunning = false; }
    public void Reset() { _accumulated = 0; IsRunning = false; }
    public long ElapsedTicks => _accumulated + (IsRunning ? GuestClock.Ticks - _start : 0);
    public long ElapsedMicroseconds => (long)(ElapsedTicks * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
}

/// <summary>Debug: per-source time multipliers (VITASHARP_TSCALE_PROC / _VBLANK / _WAIT) to bisect timing-dependent bugs.</summary>
public static class TimeScale
{
    public static readonly double Proc = Read("VITASHARP_TSCALE_PROC"), VBlank = Read("VITASHARP_TSCALE_VBLANK"), Wait = Read("VITASHARP_TSCALE_WAIT");
    static double Read(string name) => double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 1.0;
}
