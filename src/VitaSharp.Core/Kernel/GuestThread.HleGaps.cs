using System.Diagnostics;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The thread's gaps between two HLE calls (roadmap "O log e a captura F8 dizem a causa de uma trava de cara"): a game
/// thread calls the HLE many times a second, so a long stretch without a call is either guest code spinning, the CPU
/// pacing putting the thread to sleep, or the host not running the thread (preempted, or a host too slow). Each gap
/// keeps its host time, the pacing sleep inside it and the guest cycles run in it; what the pacing does not explain
/// is the host's: the guest time those cycles take at the emulated clock against the host time that passed. The
/// watchdog's STALL and the thread descriptions show the gap open now and the longest one. Cost per HLE call: a
/// millisecond tick count at the return and at the next entry and a few field writes; the record is only kept when a
/// gap beats it. A gap that spans a pause of the emulation is left out.
/// </summary>
public sealed unsafe partial class GuestThread
{
    /// <summary>A gap between two HLE calls of the thread: its host time (ms), the pacing sleep in it (ms), the guest
    /// cycles run in it and when it ended (Environment.TickCount64).</summary>
    public readonly record struct HleGap(long Milliseconds, long PacingMilliseconds, long GuestCycles, long EndedAt)
    {
        /// <summary>Host time the pacing and the guest cycles at the emulated clock do not explain: the host did not
        /// run the thread (preempted) or ran it slower than the emulated CPU; -1 when the CPU runs unpaced.</summary>
        public long HostLateMilliseconds
        {
            get
            {
                long rate = Cpu.CpuCore.InstructionsPerSecond;
                return rate <= 0 ? -1 : Math.Max(0, Milliseconds - PacingMilliseconds - GuestCycles * 1000 / rate);
            }
        }

        public override string ToString()
        {
            long late = HostLateMilliseconds;
            string host = late < 0 ? "" : $", host late {late} ms";
            return $"{Milliseconds} ms (pacing {PacingMilliseconds} ms, {GuestCycles / 1000} K guest cycles{host})";
        }
    }

    private long _hleReturnedAt = -1;
    private long _hleReturnedCycles;
    private long _hleReturnedPacing;
    private int _hleReturnedPause;
    private HleGap _longestHleGap;

    /// <summary>The longest gap between two HLE calls since the thread started (none: zero).</summary>
    public HleGap LongestHleGap => _longestHleGap;

    /// <summary>The gap open now (since the thread's last HLE return), or null when it is inside a call, has made none
    /// yet or the emulation paused since.</summary>
    public HleGap? OpenHleGap(long now)
    {
        long returned = Volatile.Read(ref _hleReturnedAt);
        if (returned < 0 || (_hleSeq & 1) == 1 || EmulationPause.Pauses != _hleReturnedPause)
        {
            return null;
        }

        return GapUntil(now, returned);
    }

    /// <summary>At an HLE call's entry (<paramref name="now"/>: Environment.TickCount64): the gap that ends here becomes the
    /// longest when it is.</summary>
    internal void NoteHleEntry(long now)
    {
        long returned = _hleReturnedAt;
        if (returned < 0 || now - returned <= _longestHleGap.Milliseconds || EmulationPause.Pauses != _hleReturnedPause)
        {
            return;
        }

        _longestHleGap = GapUntil(now, returned);
    }

    /// <summary>At an HLE call's return: a new gap starts.</summary>
    internal void NoteHleReturn(long now)
    {
        _hleReturnedCycles = OwnCycles;
        _hleReturnedPacing = Volatile.Read(ref Core.PacingTicks);
        _hleReturnedPause = EmulationPause.Pauses;
        Volatile.Write(ref _hleReturnedAt, now);
    }

    private HleGap GapUntil(long now, long returned)
    {
        long pacingMs = (Volatile.Read(ref Core.PacingTicks) - _hleReturnedPacing) * 1000 / Stopwatch.Frequency;
        return new HleGap(now - returned, pacingMs, OwnCycles - _hleReturnedCycles, now);
    }

    /// <summary>The gaps for a thread description: the open one and the longest.</summary>
    private string DescribeHleGaps()
    {
        string open = OpenHleGap(Environment.TickCount64) is { } gap ? $" since-hle={gap}" : "";
        string longest = _longestHleGap.Milliseconds > 0 ? $" longest-hle-gap={_longestHleGap}" : "";
        return open + longest;
    }
}
