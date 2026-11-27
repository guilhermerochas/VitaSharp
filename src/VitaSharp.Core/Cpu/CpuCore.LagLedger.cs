using System.Collections.Concurrent;
using System.Diagnostics;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Cpu;

/// <summary>
/// The lag ledger of a guest thread (see <see cref="GuestClock.LeaveOutLag"/>): from the moment the thread takes an
/// emulated core (<see cref="ResetThrottle"/>) to the moment it gives it away (<see cref="SettleThrottle"/>), its host
/// time on that core (without the host's GC pauses) against the time its instructions and charged HLE cycles take on
/// the emulated CPU. A thread whose host time is ahead by more than the allowance asks the clock to stand still for the
/// difference. A blocked thread holds no core and has no ledger, so only the threads the console would be running
/// count; the threads that wait for a host core or translate cold code while holding an emulated one are the late ones.
/// </summary>
public sealed unsafe partial class CpuCore
{
    /// <summary>Host timestamp when the thread took its core; 0 while it holds none (read by other threads).</summary>
    private long _lagStart;
    /// <summary>The guest clock as waits measure it (<see cref="GuestClock.WaitTicks"/>) when the thread took its core.</summary>
    private long _lagGuestStart;

    /// <summary>Instructions and charged cycles since the thread took its core (the pacing ledger restarts on its own
    /// rules; this one only at the core).</summary>
    private long _lagInstructions;

    /// <summary>Host ticks of the ledger spent in host work: HLE calls and the time outside <see cref="Run"/> (the
    /// caller's own SVC handling, callbacks set up): on the console that is firmware code with its own cost, which the
    /// ledger already charges in cycles (<see cref="HleCallCost"/>); its host time is not the guest CPU falling behind.</summary>
    private long _lagHostWork;

    /// <summary>Host timestamp the current host work began (0 = running guest code).</summary>
    private long _lagHostWorkSince;

    private int _lagSlot = -1;

    /// <summary>Time the clock left out at this thread's ask, ticks, and its asks the budget cut short (diagnostics:
    /// the threads named in the PERF lag clock line).</summary>
    private long _lagLeftOut;
    private long _lagCutAsks;

    private static readonly CpuCore?[] s_lagLedgers = new CpuCore?[4096];
    private static int s_lagLedgerCount;
    private static readonly ConcurrentStack<int> s_freeLagSlots = new();

    private void RegisterLagLedger()
    {
        EnterHostWork(); // until its first Run the thread is not in guest code
        if (!s_freeLagSlots.TryPop(out int slot))
        {
            slot = Interlocked.Increment(ref s_lagLedgerCount) - 1;
            if (slot >= s_lagLedgers.Length)
            {
                return; // more cores than slots: this one still reports itself, it is only not looked at by the waiters
            }
        }

        _lagSlot = slot;
        Volatile.Write(ref s_lagLedgers[slot], this);
    }

    private void UnregisterLagLedger()
    {
        if (_lagSlot < 0)
        {
            return;
        }

        Volatile.Write(ref s_lagLedgers[_lagSlot], null);
        s_freeLagSlots.Push(_lagSlot);
        _lagSlot = -1;
    }

    private void BeginLagLedger()
    {
        _lagInstructions = 0;
        _lagHostWork = 0;
        _lagGuestStart = GuestClock.WaitTicks;
        Volatile.Write(ref _lagStart, GuestClock.HostNow());
    }

    private void EndLagLedger(long pendingInstructions)
    {
        ReportLag(pendingInstructions);
        Volatile.Write(ref _lagStart, 0);
    }

    /// <summary>The thread leaves guest code for host work (an HLE call, the end of <see cref="Run"/>).</summary>
    internal void EnterHostWork()
    {
        Volatile.Write(ref _lagHostWorkSince, GuestClock.HostNow());
    }

    /// <summary>The thread is back in guest code: the host work since <see cref="EnterHostWork"/> (from the ledger's
    /// start, when the core was taken inside it) leaves the ledger's host time.</summary>
    internal void LeaveHostWork()
    {
        long since = Volatile.Read(ref _lagHostWorkSince);
        if (since == 0)
        {
            return;
        }

        long start = Volatile.Read(ref _lagStart);
        if (start != 0)
        {
            _lagHostWork += GuestClock.HostNow() - Math.Max(since, start);
        }

        Volatile.Write(ref _lagHostWorkSince, 0);
    }

    /// <summary>Asks the clock to leave out this thread's lateness so far (<paramref name="pendingInstructions"/>: the
    /// ones executed since the last ledger check, when the caller knows them).</summary>
    private void ReportLag(long pendingInstructions)
    {
        long start = Volatile.Read(ref _lagStart);
        long rate = InstructionsPerSecond;
        if (start == 0 || rate <= 0 || !GuestClock.LagClockEnabled)
        {
            return;
        }

        // guest time since the core was taken, as the waits see it: the time already left out (for this thread or
        // another one) and the GC pauses are not in it, so a stall never feeds itself (a thread that slept off its
        // pacing while the clock stood still is not late for it), and threads late over the same stretch ask once
        long now = GuestClock.HostNow();
        long since = Volatile.Read(ref _lagHostWorkSince);
        long hostWork = _lagHostWork + (since != 0 ? now - Math.Max(since, start) : 0);
        double speed = GuestClock.Speed;
        long guestTicks = GuestClock.WaitTicks - _lagGuestStart - (speed == 1.0 ? hostWork : (long)(hostWork * speed));
        long late = LagBeyondAllowance(guestTicks, _lagInstructions + pendingInstructions, rate);
        if (late > 0)
        {
            long grown = GuestClock.LeaveOutLag(GuestClock.LagTicks + late, out bool cut);
            if (grown > 0)
            {
                Interlocked.Add(ref _lagLeftOut, grown);
            }

            if (cut)
            {
                Interlocked.Increment(ref _lagCutAsks);
            }
        }
    }

    /// <summary>How far <paramref name="guestTicks"/> on a core are ahead of the emulated time of
    /// <paramref name="instructions"/> at <paramref name="rate"/> instructions per second, beyond the allowance (guest
    /// clock ticks; zero or less = not late).</summary>
    internal static long LagBeyondAllowance(long guestTicks, long instructions, long rate)
    {
        long emulatedTicks = instructions * Stopwatch.Frequency / rate;
        return guestTicks - emulatedTicks - GuestClock.LagAllowanceTicks;
    }

    /// <summary>The threads the clock left the most time out for (" by: name ms (cut asks), ..."), the live ones; empty
    /// when none. A thread that keeps asking while it runs no guest code is a ledger bug to fix, not a late
    /// thread.</summary>
    internal static string LagTop()
    {
        int count = Math.Min(Volatile.Read(ref s_lagLedgerCount), s_lagLedgers.Length);
        var top = new List<(long LeftOut, long Cut, string Name)>();
        for (int i = 0; i < count; i++)
        {
            CpuCore? core = Volatile.Read(ref s_lagLedgers[i]);
            if (core != null && (core._lagLeftOut > 0 || core._lagCutAsks > 0))
            {
                top.Add((core._lagLeftOut, core._lagCutAsks, core.Phases.ThreadName ?? "?"));
            }
        }

        if (top.Count == 0)
        {
            return "";
        }

        top.Sort((a, b) => b.LeftOut.CompareTo(a.LeftOut));
        double ms = 1000.0 / Stopwatch.Frequency;
        var sb = new System.Text.StringBuilder("; by:");
        for (int i = 0; i < Math.Min(8, top.Count); i++)
        {
            sb.Append($" {top[i].Name} {top[i].LeftOut * ms:F1} ms ({top[i].Cut} cut)");
        }

        return sb.ToString();
    }

    /// <summary>
    /// A thread about to end a timed wait looks at the threads holding a core first: one may be late in the middle of
    /// a burst that has not reached its next ledger check (a cold 35K-instruction job runs 28 ms without one), and on
    /// the console the wait would only end after that burst. Their instructions since their last check are not known
    /// here and count as zero, which overstates their lateness by at most one pacing window (~0.25 ms).
    /// </summary>
    public static void SettleLagClock()
    {
        if (!GuestClock.LagClockEnabled || InstructionsPerSecond <= 0 || GuestClock.Turbo)
        {
            return;
        }

        int count = Math.Min(Volatile.Read(ref s_lagLedgerCount), s_lagLedgers.Length);
        for (int i = 0; i < count; i++)
        {
            Volatile.Read(ref s_lagLedgers[i])?.ReportLag(0);
        }
    }
}
