using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Sub-millisecond sleeping. Thread.Sleep only takes whole milliseconds (a 0.5 ms request becomes a yield), and
/// Windows' default Sleep granularity is ~15.6 ms, which makes guest delays and vblank waits overshoot whole frames.
/// Windows uses a high-resolution waitable timer (one per thread); Linux, macOS and Android use nanosleep, which is
/// precise to a few tens of microseconds there.
/// </summary>
public static class PreciseSleep
{
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static nint t_timer;
    private static readonly bool s_windows = OperatingSystem.IsWindows();
    private static bool s_posixFailed;

    public static void Microseconds(long us)
    {
        if (us <= 0)
        {
            return;
        }

        if (s_windows)
        {
            if (TryWindows(us))
            {
                return;
            }
        }
        else if (!s_posixFailed && TryPosix(us))
        {
            return;
        }

        Thread.Sleep(TimeSpan.FromTicks(us * 10));
    }

    /// <summary>Sleeps most of the way to <paramref name="deadlineTicks"/> (Stopwatch ticks) and spins the rest.</summary>
    /// <param name="allowSpin">false: only sleep (may wake up to ~0.5 ms late, but never burns a core); used by the
    /// CPU pacing, where accuracy matters less than battery. <see cref="HostTuning.AvoidBusyWaits"/> (phones) turns
    /// spinning off everywhere.</param>
    public static void Until(long deadlineTicks, bool allowSpin = true)
    {
        if (HostTuning.AvoidBusyWaits)
        {
            allowSpin = false;
        }

        while (true)
        {
            long left = deadlineTicks - Stopwatch.GetTimestamp();
            if (left <= 0)
            {
                return;
            }

            long us = left * 1_000_000 / Stopwatch.Frequency;
            if (!allowSpin)
            {
                Microseconds(us);
                return;
            }
            if (us > 600)
            {
                Microseconds(us - 500);
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    private static bool TryWindows(long us)
    {
        if (t_timer == 0)
        {
            t_timer = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (t_timer == 0)
            {
                return false;
            }
        }
        long due = -us * 10; // relative, 100 ns units
        if (!SetWaitableTimerEx(t_timer, ref due, 0, 0, 0, 0, 0))
        {
            return false;
        }

        WaitForSingleObject(t_timer, 0xFFFFFFFF);
        return true;
    }

    private const int EINTR = 4; // same value on Linux, macOS and Android

    private static unsafe bool TryPosix(long us)
    {
        try
        {
            // struct timespec { time_t tv_sec; long tv_nsec; }: both are pointer-sized on every supported ABI
            nint* req = stackalloc nint[2];
            nint* rem = stackalloc nint[2];
            req[0] = (nint)(us / 1_000_000);
            req[1] = (nint)(us % 1_000_000 * 1000);
            while (nanosleep(req, rem) != 0)
            {
                if (Marshal.GetLastPInvokeError() != EINTR)
                {
                    return false;
                }

                req[0] = rem[0];
                req[1] = rem[1]; // interrupted by a signal: sleep the remainder
            }
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            s_posixFailed = true;
            return false;
        }
    }

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    private static extern bool SetWaitableTimerEx(nint timer, ref long dueTime, int period, nint completion, nint arg, nint wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint ms);

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int nanosleep(nint* req, nint* rem);
}
