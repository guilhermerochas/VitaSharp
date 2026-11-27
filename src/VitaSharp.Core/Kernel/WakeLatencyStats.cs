using System.Diagnostics;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// One guest thread's wake-up latency, in guest time: from the signal of the object it waited on to the thread running
/// again (the end of its wait). On the console a wake-up costs tens of microseconds; a host that wakes guest threads
/// late (timer slack, a thread on a slow core, a host sleep inside the wake path) makes a job system miss its period on
/// every round, as Dead Space's mixer did on the phone (each round is four signals and four waits). Counted in power-of
/// two microsecond buckets, so p50 / p99 are the bucket's upper bound (within a factor of two). Written by the owning
/// thread only, read racily by the reports (statistics).
/// </summary>
public sealed class WakeLatencyStats
{
    private const int Buckets = 24; // up to 2^23 us = 8.4 s, the last bucket takes the rest

    private readonly long[] _counts = new long[Buckets];
    private long _total;
    private long _maxUs;

    /// <summary>Wakes recorded.</summary>
    public long Count => Volatile.Read(ref _total);

    /// <summary>Records a wake that ended <paramref name="latencyTicks"/> (guest clock ticks) after the signal.</summary>
    public void Record(long latencyTicks)
    {
        long us = Math.Max(0, latencyTicks * 1_000_000 / Stopwatch.Frequency);
        int bucket = us <= 1 ? 0 : Math.Min(Buckets - 1, 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(us - 1)));
        _counts[bucket]++;
        _total++;
        if (us > _maxUs)
        {
            _maxUs = us;
        }
    }

    /// <summary>The upper bound (us) of the bucket holding the <paramref name="fraction"/> quantile; 0 with no wakes.</summary>
    public long PercentileUs(double fraction)
    {
        long total = Count;
        if (total == 0)
        {
            return 0;
        }

        long rank = (long)Math.Ceiling(total * fraction);
        long seen = 0;
        for (int i = 0; i < Buckets; i++)
        {
            seen += _counts[i];
            if (seen >= rank)
            {
                return i == Buckets - 1 ? _maxUs : Math.Min(1L << i, _maxUs);
            }
        }

        return _maxUs;
    }

    /// <summary>"wake: N, p50 <= A us, p99 <= B us, max C us", or null with no wakes.</summary>
    public string? Describe()
    {
        long total = Count;
        return total == 0 ? null : $"wake latency (signal to running): {total} wakes, p50 <= {PercentileUs(0.5)} us, p99 <= {PercentileUs(0.99)} us, max {Volatile.Read(ref _maxUs)} us";
    }
}
