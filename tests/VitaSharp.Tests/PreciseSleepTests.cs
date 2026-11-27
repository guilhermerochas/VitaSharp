using System.Diagnostics;
using System.Runtime.InteropServices;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="PreciseSleep.Until"/>, the host wait under the HLE's timed waits (sceKernelDelayThread, the syscall
/// wake-up latency, vblank and audio pacing): it must end on time (the Vita's timers are microsecond-precise) and spin
/// only the host timer's own error, not a fixed half millisecond. The policy runs on a simulated host timer here
/// (<see cref="SimulatedHost"/>), so nothing depends on the test machine's clock or load.
/// </summary>
public class PreciseSleepTests
{
    private static readonly long s_frequency = Stopwatch.Frequency;

    private static long Ticks(double microseconds) => (long)(microseconds * s_frequency / 1_000_000);

    private static double Microseconds(long ticks) => ticks * 1_000_000.0 / s_frequency;

    /// <summary>A host whose sleeps end <see cref="Overshoot"/> after the request (plus a repeating jitter), or, with
    /// <see cref="TickMicroseconds"/>, on the first timer tick after it (Windows' high-resolution timer); spins advance
    /// the clock a microsecond and are counted.</summary>
    private sealed class SimulatedHost
    {
        public long Now = Ticks(1_000_000);
        public double Overshoot;
        public double[] Jitter = [0];
        public double TickMicroseconds;
        public long SpinTicks;
        public int Sleeps;

        public void Sleep(long microseconds)
        {
            Sleeps++;
            long end = Now + Ticks(microseconds);
            if (TickMicroseconds > 0)
            {
                long tick = Ticks(TickMicroseconds);
                end = (end / tick + 1) * tick;
            }

            Now = end + Ticks(Overshoot + Jitter[Sleeps % Jitter.Length]);
        }
    }

    private readonly struct SimulatedTimer(SimulatedHost host) : PreciseSleep.ITimer
    {
        public long Now() => host.Now;

        public void Sleep(long microseconds) => host.Sleep(microseconds);

        public void Spin()
        {
            host.Now += Ticks(1);
            host.SpinTicks += Ticks(1);
        }
    }

    /// <summary>Runs <paramref name="count"/> waits of <paramref name="waitUs"/> on <paramref name="host"/> after a
    /// warm-up, returning the spin per wait and the lateness of each (microseconds).</summary>
    private static (double SpinPerWait, double[] Late) Run(SimulatedHost host, ref long estimate, double waitUs, int count, bool allowSpin = true)
    {
        var timer = new SimulatedTimer(host);
        for (int i = 0; i < 200; i++)
        {
            PreciseSleep.UntilCore(host.Now + Ticks(waitUs), allowSpin, ref estimate, timer);
        }

        host.SpinTicks = 0;
        double[] late = new double[count];
        for (int i = 0; i < count; i++)
        {
            long deadline = host.Now + Ticks(waitUs);
            PreciseSleep.UntilCore(deadline, allowSpin, ref estimate, timer);
            late[i] = Microseconds(host.Now - deadline);
        }
        Array.Sort(late);
        return (Microseconds(host.SpinTicks) / count, late);
    }

    private static double P99(double[] sorted) => sorted[(int)(sorted.Length * 0.99)];

    /// <summary>The waits of a guest thread that times out on a semaphore or delays in a loop (Dead Space's mixer
    /// workers: WaitSema(500 us) thousands of times a second): on a host whose sleeps overshoot by 40-60 us (Linux or
    /// macOS), a 500 us wait spins about that much, not its whole last half millisecond (the old fixed spin took the
    /// whole wait: 100% of a core for a thread that only waits).</summary>
    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(16_667)]
    public void A_wait_spins_only_the_host_timers_error(double waitUs)
    {
        var host = new SimulatedHost { Overshoot = 40, Jitter = [0, 5, 20, 2, 10, 0, 15, 3] };
        long estimate = Ticks(PreciseSleep.InitialOvershootMicroseconds(windows: false));

        (double spin, double[] late) = Run(host, ref estimate, waitUs, 2000);

        Assert.True(spin <= 100, $"{waitUs} us wait spun {spin:F0} us on average (estimate {Microseconds(estimate):F0} us)");
        Assert.True(late.Average() <= 5, $"late on average {late.Average():F1} us");
        Assert.True(P99(late) <= 25, $"p99 late {P99(late):F1} us");
    }

    /// <summary>Windows' high-resolution timer fires on a 0.5 ms tick: the estimate rises from the Linux start to the
    /// tick, so waits still end on time (the spin covers the tick), and never past the old fixed spin.</summary>
    [Fact]
    public void A_coarse_timer_raises_the_spin_and_keeps_the_waits_on_time()
    {
        var host = new SimulatedHost { TickMicroseconds = 500, Overshoot = 30, Jitter = [0, 10, 40, 5] };
        long estimate = Ticks(PreciseSleep.InitialOvershootMicroseconds(windows: false));

        (double spin, double[] late) = Run(host, ref estimate, 2000, 2000);

        Assert.True(spin <= PreciseSleep.MaxSpinMicroseconds, $"spun {spin:F0} us on average");
        Assert.True(late.Average() <= 10, $"late on average {late.Average():F1} us");
        Assert.True(P99(late) <= 60, $"p99 late {P99(late):F1} us");
    }

    /// <summary>Phones (<see cref="HostTuning.AvoidBusyWaits"/>) and the CPU pacing never spin: one host sleep for the
    /// whole wait.</summary>
    [Fact]
    public void A_wait_without_spin_sleeps_once_and_never_spins()
    {
        var host = new SimulatedHost { Overshoot = 40 };
        long estimate = Ticks(50);

        (double spin, _) = Run(host, ref estimate, 500, 100, allowSpin: false);

        Assert.Equal(0, spin);
        Assert.Equal(300, host.Sleeps);
    }

    /// <summary>A wait shorter than the timer's error (the 30 us syscall wake-up latency on Windows) is spun whole, as
    /// before: a sleep would end it a tick late.</summary>
    [Fact]
    public void A_wait_shorter_than_the_timers_error_is_spun()
    {
        var host = new SimulatedHost { TickMicroseconds = 500 };
        long estimate = Ticks(PreciseSleep.InitialOvershootMicroseconds(windows: true));

        (double spin, double[] late) = Run(host, ref estimate, 30, 100);

        Assert.Equal(0, host.Sleeps);
        Assert.InRange(spin, 29, 31);
        Assert.True(late.Max() <= 1, $"late {late.Max():F1} us");
    }

    /// <summary>The estimate ignores a thread being preempted (a 2 ms+ late wake-up is not the timer) and never spins
    /// more than the old fixed half millisecond.</summary>
    [Fact]
    public void The_estimate_ignores_preemption_and_is_capped()
    {
        long estimate = Ticks(50);

        Assert.Equal(estimate, PreciseSleep.NextOvershootEstimate(estimate, Ticks(5000), s_frequency));
        long high = estimate;
        for (int i = 0; i < 1000; i++)
        {
            high = PreciseSleep.NextOvershootEstimate(high, Ticks(1900), s_frequency);
        }

        Assert.Equal(Ticks(PreciseSleep.MaxSpinMicroseconds), high);
        Assert.Equal(estimate - (estimate / 64), PreciseSleep.NextOvershootEstimate(estimate, -Ticks(10), s_frequency));
    }

    private const int PR_GET_TIMERSLACK = 30;

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nint arg2, nint arg3, nint arg4, nint arg5);

    /// <summary>Linux: a thread that sleeps here runs with a 1 us timer slack, so a short sleep is not ended up to the
    /// default 50 us late (the lateness the spin used to cover).</summary>
    [Fact]
    public void Linux_sleeping_threads_get_a_microsecond_timer_slack()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // timer slack is a Linux (and Android) setting
        }

        int slack = -1;
        var thread = new Thread(() =>
        {
            PreciseSleep.Microseconds(1);
            slack = prctl(PR_GET_TIMERSLACK, 0, 0, 0, 0);
        });
        thread.Start();
        thread.Join();

        Assert.Equal(1000, slack);
    }
}
