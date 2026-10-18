using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Sub-millisecond sleeping. Windows' default Sleep granularity is ~15.6 ms, which makes guest delays and vblank
/// waits overshoot whole frames; there a high-resolution waitable timer (one per thread) is used. Other platforms'
/// Thread.Sleep (nanosleep) is already precise.
/// </summary>
public static class PreciseSleep
{
    [ThreadStatic] static nint _timer;
    static readonly bool Windows = OperatingSystem.IsWindows();

    public static void Microseconds(long us)
    {
        if (us <= 0) return;
        if (Windows && TryWindows(us)) return;
        Thread.Sleep(TimeSpan.FromTicks(us * 10));
    }

    /// <summary>Sleeps most of the way to <paramref name="deadlineTicks"/> (Stopwatch ticks) and spins the rest.</summary>
    /// <param name="allowSpin">false: only sleep (may wake up to ~0.5 ms late, but never burns a core); used by the
    /// CPU pacing, where accuracy matters less than battery.</param>
    public static void Until(long deadlineTicks, bool allowSpin = true)
    {
        while (true)
        {
            long left = deadlineTicks - Stopwatch.GetTimestamp();
            if (left <= 0) return;
            long us = left * 1_000_000 / Stopwatch.Frequency;
            if (!allowSpin) { Microseconds(us); return; }
            if (us > 600) Microseconds(us - 500);
            else Thread.SpinWait(64);
        }
    }

    static bool TryWindows(long us)
    {
        if (_timer == 0)
        {
            _timer = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (_timer == 0) return false;
        }
        long due = -us * 10; // relative, 100 ns units
        if (!SetWaitableTimerEx(_timer, ref due, 0, 0, 0, 0, 0)) return false;
        WaitForSingleObject(_timer, 0xFFFFFFFF);
        return true;
    }

    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    static extern bool SetWaitableTimerEx(nint timer, ref long dueTime, int period, nint completion, nint arg, nint wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(nint handle, uint ms);
}
