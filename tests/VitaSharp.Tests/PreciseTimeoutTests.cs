using System.Diagnostics;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// Timed waits on a sync object (sceKernelWaitSema/WaitEventFlag/... timeouts) end at their microsecond deadline, as
/// threadmgr's timer does on the Vita, not at Monitor.Wait's next whole millisecond: a 500 us timeout used to last
/// 1.1 ms (up to 2 ms), a 1.5 ms one 2.1 ms. <see cref="HostTimeoutTimer"/> pulses the waiter's monitor from a
/// high-resolution host timer (timerfd on Linux and Android). The bounds are far from both the old behaviour (at
/// least a whole millisecond more) and the host timer's error (tens of microseconds), so a slow or busy runner does
/// not decide them (the median decides: a loaded runner's preemptions reach the p99); Windows' timer
/// fires on a 0.5 ms tick and macOS has no timer object, so the precision bounds are
/// checked where the timer is precise (Linux).
/// </summary>
[Collection("guest")]
public class PreciseTimeoutTests
{
    private static double[] TimedOutWaits(long timeoutUs, int count)
    {
        object sync = new();
        double[] elapsed = new double[count];
        for (int i = 0; i < count + 10; i++)
        {
            long start = Stopwatch.GetTimestamp();
            int result;
            lock (sync)
            {
                result = Wait.Until(sync, static () => false, timeoutUs, what: "test timeout");
            }

            Assert.Equal(SceError.WAIT_TIMEOUT, result);
            if (i >= 10)
            {
                elapsed[i - 10] = (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;
            }
        }
        Array.Sort(elapsed);
        return elapsed;
    }

    [Theory]
    [InlineData(500, 900)]
    [InlineData(1500, 1950)]
    public void A_timeout_ends_at_its_microsecond_deadline(long timeoutUs, double maxP50)
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // see the class summary: the precise host timer is checked on Linux
        }

        double[] elapsed = TimedOutWaits(timeoutUs, 200);
        double p50 = elapsed[elapsed.Length / 2], p99 = elapsed[(int)(elapsed.Length * 0.99)];

        Assert.True(elapsed[0] >= timeoutUs, $"a {timeoutUs} us timeout ended early: {elapsed[0]:F0} us");
        Assert.True(p50 <= maxP50, $"{timeoutUs} us timeout: p50 {p50:F0} us, p99 {p99:F0} us");
    }

    /// <summary>A wait that is signalled before its timeout still returns at the signal (the armed deadline is left
    /// behind and fires once for nothing), and a later wait on another monitor is not ended by it.</summary>
    [Fact]
    public void A_signalled_wait_returns_at_the_signal_and_leaves_no_stray_pulse()
    {
        object sync = new();
        bool ready = false;
        var signaller = new Thread(() =>
        {
            lock (sync)
            {
                ready = true;
                Monitor.PulseAll(sync);
            }
        });
        int result;
        lock (sync)
        {
            signaller.Start();
            result = Wait.Until(sync, () => ready, 10_000_000, what: "test signal");
        }

        signaller.Join();
        Assert.Equal(SceError.OK, result);

        object other = new();
        int second;
        lock (other)
        {
            second = Wait.Until(other, static () => false, 2000, what: "test timeout");
        }

        Assert.Equal(SceError.WAIT_TIMEOUT, second);
    }
}
