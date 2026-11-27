using System.Diagnostics;

namespace VitaSharp.Core.Util;

/// <summary>
/// The guest clock leaves out the time the emulated machine fell behind its own CPU. On the console the clock and
/// the CPU's work move together: a thread that runs 140K instructions takes about 0.4 ms of process time on a 333 MHz
/// core, whatever the other threads do. On the host the same burst can take 28 ms (cold code being translated, the
/// host thread descheduled by a loaded host), and a clock that kept real time let every timed wait of the other
/// threads end in the middle of that burst: Dead Space's audio thread woke from its 11 ms sleep while the mixer was
/// still in its first round and the port's audio stopped for good (it plays with --speed=0.4).
/// <para>
/// A guest thread holding an emulated core reports how far its host time on that core is ahead of the time its
/// instructions take on the emulated CPU (<see cref="Cpu.CpuCore"/> lag ledger). Past an allowance of a pacing
/// window, the clock stands still for the difference: the next readings stay where the clock was until real time
/// has made up the time left out, as with the GC pauses (<see cref="HostElapsed"/>). Several threads late over the
/// same stretch leave it out once (each one measures its lateness in guest time, which no longer has what the first
/// one had left out).
/// The time left out has a budget, a burst of 100 ms that refills at a tenth of real time: a cold start or a host
/// stall is absorbed whole, and a game that is slower than the console throughout still sees its clock at no less than
/// ~90% of real time. The time left out is given back at a tenth of real time while it is not growing (the clock runs at
/// up to 110%): a late burst still finds the clock waiting for it, but over a run the clock keeps real time, so the
/// audio a game paces by its sleeps still comes out at the device's rate (a phone whose code ran bursts at ~160 MHz left
/// 2 s a minute out and played Dead Space's audio at 89% of its rate, with underruns). VITASHARP_LAG_CLOCK=0 turns it
/// off (A/B).
/// </para>
/// </summary>
public static partial class GuestClock
{
    /// <summary>Ticks of real time left out for threads that fell behind, as of <see cref="s_lagGrewAt"/>; given back
    /// since then at <see cref="LagRepayDivisor"/> (<see cref="LagAt"/>).</summary>
    private static long s_lag;

    /// <summary>The real time (<see cref="RealElapsed"/>) when the time left out last grew.</summary>
    private static long s_lagGrewAt;

    /// <summary>Real time left out in all, ticks (diagnostics; the part given back is this minus the current lag).</summary>
    private static long s_lagTotal;

    /// <summary>The time left out is given back at real time divided by this (a tenth: the clock runs at up to 110%).</summary>
    internal const int LagRepayDivisor = 10;

    /// <summary>The reading when the time left out last grew: the clock reads no less than this (monotonic) and stands
    /// still there until real time minus the new total passes it.</summary>
    private static long s_lagFloor = long.MinValue;

    private static readonly object s_lagLock = new();
    private static long s_lagTokens = LagBurstTicks;
    private static long s_lagRefilledAt;

    /// <summary>Most time left out at once (the burst), ticks.</summary>
    internal static long LagBurstTicks => Stopwatch.Frequency / 10;

    /// <summary>The budget refills at real time divided by this (a tenth).</summary>
    internal const int LagRefillDivisor = 10;

    /// <summary>How late a thread may be before the clock waits for it (the CPU pacing lets a thread run 1 ms ahead of
    /// real time; the same margin behind is normal jitter, not a stall).</summary>
    internal static long LagAllowanceTicks => Stopwatch.Frequency / 1000;

    internal static bool LagClockEnabled { get; set; } = Environment.GetEnvironmentVariable(EnvVars.LagClock) != "0";

    /// <summary>The time left out now (after what was given back), ticks.</summary>
    public static long LagTicks => LagAt(RealElapsed);

    /// <summary>Real time left out in all so far, ticks (diagnostics: the end-of-run line and the F8 report).</summary>
    public static long LagTotalTicks => Volatile.Read(ref s_lagTotal);

    /// <summary>The time left out at real time <paramref name="realElapsed"/>: what it was when it last grew, minus a
    /// tenth of the real time since then, never below zero.</summary>
    private static long LagAt(long realElapsed)
    {
        long lag = Volatile.Read(ref s_lag);
        if (lag == 0)
        {
            return 0;
        }

        long since = realElapsed - Volatile.Read(ref s_lagGrewAt);
        long repaid = since > 0 ? since / LagRepayDivisor : 0;
        return repaid >= lag ? 0 : lag - repaid;
    }

    /// <summary>How many times the time left out grew (diagnostics).</summary>
    public static long LagEvents;

    /// <summary>Asks the budget cut short (diagnostics: a thread late for longer than the budget allows).</summary>
    public static long LagRefusals;

    /// <summary>The live reading: real time (without host pauses), the turbo/resume offset, minus the time left out,
    /// never below the reading the clock stood at when the time left out last grew.</summary>
    private static long Live()
    {
        long realElapsed = RealElapsed;
        long reading = realElapsed + Interlocked.Read(ref s_offset) - LagAt(realElapsed);
        long floor = Volatile.Read(ref s_lagFloor);
        return reading > floor ? reading : floor;
    }

    /// <summary>
    /// The clock as timed waits measure it: the live reading without the floor. Right after the time left out grew, the
    /// readings stand at the floor (a guest never sees its clock go back), but a wait whose deadline the floor already
    /// passed has not lived through that time: measured against the floor it would end in the middle of the late
    /// thread's burst, which is what the time left out is for. Equal to <see cref="Ticks"/> once real time caught up.
    /// </summary>
    public static long WaitTicks
    {
        get
        {
            long f = Volatile.Read(ref s_frozen);
            if (f != NotFrozen)
            {
                return f;
            }

            long realElapsed = RealElapsed;
            return realElapsed + Interlocked.Read(ref s_offset) - LagAt(realElapsed);
        }
    }

    /// <summary>The host timestamp the clock reads (Stopwatch ticks; the test seam <see cref="HostTimestampForTest"/>
    /// when set), for the lag ledgers.</summary>
    internal static long HostNow() => HostTimestamp();

    /// <summary>
    /// A thread late on its emulated CPU asks the clock to have left out at least <paramref name="target"/> ticks in
    /// all (the total so far plus its lateness beyond the allowance, measured in guest time). Grows the total toward it within
    /// the budget; the clock then stands at its current reading until real time catches up. Returns how much it grew
    /// (<paramref name="cut"/>: the budget left part of the ask out).
    /// </summary>
    internal static long LeaveOutLag(long target, out bool cut)
    {
        cut = false;
        if (!LagClockEnabled || Turbo || Frozen || target <= LagTicks)
        {
            return 0;
        }

        lock (s_lagLock)
        {
            long realElapsed = RealElapsed;
            long lag = LagAt(realElapsed); // what was given back since the last growth is folded in first
            if (target <= lag)
            {
                return 0;
            }

            long now = HostTimestamp();
            if (s_lagRefilledAt == 0)
            {
                s_lagRefilledAt = now;
            }

            s_lagTokens = Math.Min(LagBurstTicks, s_lagTokens + (now - s_lagRefilledAt) / LagRefillDivisor);
            s_lagRefilledAt = now;
            long wanted = target - lag;
            long grow = Math.Min(wanted, s_lagTokens);
            if (grow < wanted)
            {
                LagRefusals++;
                cut = true;
            }

            if (grow <= 0)
            {
                return 0;
            }

            s_lagTokens -= grow;
            long reading = Live();
            if (reading > s_lagFloor)
            {
                Volatile.Write(ref s_lagFloor, reading);
            }

            Volatile.Write(ref s_lagGrewAt, realElapsed);
            Volatile.Write(ref s_lag, lag + grow);
            s_lagTotal += grow;
            LagEvents++;
            return grow;
        }
    }

    /// <summary>The "PERF lag clock" line (end of run, the periodic summary, an F8 capture's context.txt): how much real
    /// time the clock left out for late threads, in how many steps, and how much it refused over the budget.</summary>
    public static string LagLine()
    {
        double ms = 1000.0 / Stopwatch.Frequency;
        string state = LagClockEnabled ? "" : " (off: VITASHARP_LAG_CLOCK=0)";
        return $"PERF lag clock: {LagTotalTicks * ms:F1} ms left out for guest threads behind their emulated CPU ({LagTicks * ms:F1} ms not given back yet), in {Volatile.Read(ref LagEvents)} steps; {Volatile.Read(ref LagRefusals)} asks cut by the budget{state}{Cpu.CpuCore.LagTop()}";
    }

    /// <summary>Tests: starts the lag accounting over (no time left out, a full budget).</summary>
    internal static void ResetLagForTest()
    {
        lock (s_lagLock)
        {
            // the offset takes the time left out over, so the reading goes on from where it is
            Interlocked.Exchange(ref s_offset, Interlocked.Read(ref s_offset) - LagTicks);
            Volatile.Write(ref s_lag, 0);
            Volatile.Write(ref s_lagGrewAt, 0);
            s_lagTotal = 0;
            Volatile.Write(ref s_lagFloor, long.MinValue);
            s_lagTokens = LagBurstTicks;
            s_lagRefilledAt = 0;
            LagEvents = 0;
            LagRefusals = 0;
        }
    }
}
