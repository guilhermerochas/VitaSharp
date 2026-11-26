using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Sub-millisecond sleeping. Thread.Sleep only takes whole milliseconds (a 0.5 ms request becomes a yield), and
/// Windows' default Sleep granularity is ~15.6 ms, which makes guest delays and vblank waits overshoot whole frames.
/// Windows uses a high-resolution waitable timer (one per thread); Linux, macOS and Android use nanosleep. On Linux and
/// Android every thread that sleeps here first lowers its timer slack to 1 us (prctl PR_SET_TIMERSLACK): the default
/// 50 us lets the kernel end every short sleep up to 50 us late to batch wake-ups, which is what made short waits spin.
/// </summary>
public static class PreciseSleep
{
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static nint t_timer;
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static bool t_slackSet;
    private static readonly bool s_timerSlack = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();
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

    /// <summary>What a host sleep of this process overshoots its request by (Stopwatch ticks): the stretch that
    /// <see cref="Until"/> spins. It starts at what the host's timer is known to do (Windows' high-resolution timer fires
    /// on a 0.5 ms tick; Linux and macOS sleeps with a 1 us slack end within tens of microseconds) and follows the
    /// measured overshoot from there (<see cref="NextOvershootEstimate"/>). It only learns from sleeps, so it starts
    /// low enough that the short waits sleep and measure the timer, and rises at once where the timer is worse.</summary>
    private static long s_overshootTicks = InitialOvershootMicroseconds(s_windows) * Stopwatch.Frequency / 1_000_000;

    internal static long InitialOvershootMicroseconds(bool windows) => windows ? 500 : 50;

    /// <summary>The overshoot estimate <see cref="Until"/> uses now (tests and PERF).</summary>
    internal static long OvershootEstimateTicks => Volatile.Read(ref s_overshootTicks);

    /// <summary>
    /// Sleeps most of the way to <paramref name="deadlineTicks"/> (Stopwatch ticks) and spins the rest. The sleep ends
    /// where this host's sleeps are expected to overshoot to (the timer's measured lateness, about 0.5 ms on Windows'
    /// high-resolution timer, tens of microseconds or less on Linux and macOS), so only that last stretch spins, never
    /// more than <see cref="MaxSpinMicroseconds"/>. Ryujinx and Dolphin pace the same way: a precise host sleep for most
    /// of the wait and a short spin for the timer's measured error.
    /// </summary>
    /// <param name="allowSpin">false: only sleep (may wake up a timer's overshoot late, but never burns a core); used by
    /// the CPU pacing, where accuracy matters less than battery. <see cref="HostTuning.AvoidBusyWaits"/> (phones) turns
    /// spinning off everywhere.</param>
    public static void Until(long deadlineTicks, bool allowSpin = true)
    {
        UntilCore(deadlineTicks, allowSpin && !HostTuning.AvoidBusyWaits, ref s_overshootTicks, default(HostTimer));
    }

    /// <summary>The longest stretch <see cref="Until"/> spins (the estimate is capped here): the old fixed spin.</summary>
    internal const long MaxSpinMicroseconds = 600;

    /// <summary>A sleep shorter than this is not worth a host timer: the rest of the wait is spun.</summary>
    internal const long MinSleepMicroseconds = 20;

    /// <summary>The clock, sleep and spin <see cref="UntilCore"/> runs on: the host's, or a test's simulated one.</summary>
    internal interface ITimer
    {
        long Now();

        void Sleep(long microseconds);

        void Spin();
    }

    private readonly struct HostTimer : ITimer
    {
        public long Now() => Stopwatch.GetTimestamp();

        public void Sleep(long microseconds) => Microseconds(microseconds);

        public void Spin() => Thread.SpinWait(32);
    }

    /// <summary><see cref="Until"/> on <paramref name="timer"/>, with the overshoot estimate in
    /// <paramref name="overshootTicks"/> (shared by every thread: it is the host timer's, and a lost update between two
    /// threads only delays the estimate by one sample).</summary>
    internal static void UntilCore<TTimer>(long deadlineTicks, bool allowSpin, ref long overshootTicks, TTimer timer)
        where TTimer : ITimer
    {
        long frequency = Stopwatch.Frequency;
        long minSleepTicks = MinSleepMicroseconds * frequency / 1_000_000;
        while (true)
        {
            long now = timer.Now();
            long left = deadlineTicks - now;
            if (left <= 0)
            {
                return;
            }

            if (!allowSpin)
            {
                timer.Sleep(left * 1_000_000 / frequency);
                return;
            }

            long margin = Volatile.Read(ref overshootTicks);
            long sleepTicks = left - margin;
            if (sleepTicks < minSleepTicks)
            {
                timer.Spin();
                continue;
            }

            long asked = sleepTicks * 1_000_000 / frequency;
            timer.Sleep(asked);
            long overshoot = timer.Now() - now - asked * frequency / 1_000_000;
            Volatile.Write(ref overshootTicks, NextOvershootEstimate(margin, overshoot, frequency));
        }
    }

    /// <summary>
    /// The overshoot estimate after a sleep that overshot by <paramref name="sampleTicks"/>: it rises to a later wake-up
    /// at once and falls a sixty-fourth of the way to an earlier one, so it sits at the late end of the host timer's
    /// spread (the sleep rarely ends past the deadline: Windows' timer fires anywhere in a 0.5 ms tick) and still comes
    /// down from a start value or a stray late wake-up within a few hundred sleeps. A sample past 2 ms is the thread being preempted, not the timer, and is ignored.
    /// </summary>
    internal static long NextOvershootEstimate(long estimateTicks, long sampleTicks, long frequency)
    {
        long sample = Math.Max(0, sampleTicks);
        if (sample > frequency / 500)
        {
            return estimateTicks;
        }

        long next = sample > estimateTicks
            ? sample
            : estimateTicks - (estimateTicks - sample) / 64;
        return Math.Clamp(next, 0, MaxSpinMicroseconds * frequency / 1_000_000);
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

    private const int PR_SET_TIMERSLACK = 29;

    /// <summary>Timer slack of the threads that sleep here, in nanoseconds (the Android app sets the same on its guest
    /// and GPU threads).</summary>
    private const nint TimerSlackNanoseconds = 1000;

    private static unsafe bool TryPosix(long us)
    {
        try
        {
            if (s_timerSlack && !t_slackSet)
            {
                t_slackSet = true;
                prctl(PR_SET_TIMERSLACK, TimerSlackNanoseconds, 0, 0, 0); // best effort: the default slack only makes sleeps later
            }

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

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nint arg2, nint arg3, nint arg4, nint arg5);
}
