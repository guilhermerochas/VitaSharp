using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The storage model under a host that wakes the reading thread late. On the Vita a read takes the device's time; in
/// the emulator the thread waits for the device's completion on the host, and a loaded host wakes it later than that
/// (Dead Space's boot, 1710 reads: 18.3 s waited for 4.7 s of device time, the first frame past 30 s). The lateness of
/// one read is the thread's credit for its next: that read is issued as early on the device timeline as it would have
/// been without the lateness, never before the previous read completed and never finishing before its own device time,
/// so the lateness does not add up over a series of reads. Guest time is the test's (<see cref="ControlledGuestClock"/>)
/// and the host's late wake-up is the test's wait, so nothing depends on the host's clock.
/// </summary>
[Collection("guest")]
public sealed class IoTimingHostLatenessTests : IDisposable
{
    private const int Reads = 10;
    // 7000 bytes at 7 MB/s = 1 ms, plus the 0.5 ms access latency
    private const long ReadBytes = 7000;
    private const long CostUs = 1500;

    private readonly double _savedSpeed = IoTiming.BytesPerSecond;
    private readonly double _savedLatency = IoTiming.LatencySeconds;
    private readonly ControlledGuestClock _clock = ControlledGuestClock.Stop();
    private readonly List<long> _deadlines = [];
    private long _lateUs;

    public IoTimingHostLatenessTests()
    {
        IoTiming.BytesPerSecond = 7e6;
        IoTiming.LatencySeconds = 0.0005;
        IoTiming.ResetDeviceTimeline();
        IoTiming.ResetThreadTimeline();
        // the host wait: guest time reaches the deadline, plus the host's lateness in waking the thread
        IoTiming.WaitUntilForTest = deadline =>
        {
            _deadlines.Add(deadline);
            long leftUs = GuestClock.ToMicroseconds(deadline - GuestClock.Ticks) + 1;
            _clock.Advance(Math.Max(0, leftUs) + _lateUs);
        };
    }

    public void Dispose()
    {
        IoTiming.WaitUntilForTest = null;
        IoTiming.ResetThreadTimeline();
        IoTiming.ResetDeviceTimeline();
        IoTiming.BytesPerSecond = _savedSpeed;
        IoTiming.LatencySeconds = _savedLatency;
        _clock.Dispose();
    }

    /// <summary>Guest microseconds the reads took, with <paramref name="workUs"/> of guest code between two reads.</summary>
    private long ReadSeries(int reads, long workUs = 0)
    {
        long start = GuestClock.Ticks;
        for (int i = 0; i < reads; i++)
        {
            if (i > 0 && workUs > 0)
            {
                _clock.Advance(workUs);
            }

            IoTiming.Read(ReadBytes);
        }

        return GuestClock.ToMicroseconds(GuestClock.Ticks - start);
    }

    /// <summary>8 ms of host lateness on every wait: the series takes its device time plus at most one lateness, not
    /// ten times the lateness (the reads the credit covers do not wait at all).</summary>
    [Fact]
    public void HostLatenessDoesNotAddUpOverASeriesOfReads()
    {
        _lateUs = 8000;
        long elapsedUs = ReadSeries(Reads);
        Assert.InRange(elapsedUs, Reads * CostUs, Reads * CostUs + _lateUs + Reads * 3);
    }

    /// <summary>Without lateness the series takes exactly its device time; with it, each read that waits still waits
    /// for a completion at least a whole read's device time after the previous one.</summary>
    [Fact]
    public void AReadNeverCompletesBeforeItsDeviceTime()
    {
        long elapsedUs = ReadSeries(Reads);
        Assert.InRange(elapsedUs, Reads * CostUs, Reads * CostUs + Reads * 3);

        _deadlines.Clear();
        _lateUs = 8000;
        ReadSeries(Reads);
        Assert.NotEmpty(_deadlines);
        for (int i = 1; i < _deadlines.Count; i++)
        {
            Assert.True(GuestClock.ToMicroseconds(_deadlines[i] - _deadlines[i - 1]) >= CostUs - 1, $"read {i} completed before its device time");
        }
    }

    /// <summary>The guest code between two reads keeps its time: the credit only takes back the host's lateness.</summary>
    [Fact]
    public void GuestTimeBetweenReadsIsKept()
    {
        const long WorkUs = 3000;
        _lateUs = 8000;
        long elapsedUs = ReadSeries(Reads, WorkUs);
        long deviceAndWorkUs = Reads * CostUs + (Reads - 1) * WorkUs;
        Assert.InRange(elapsedUs, deviceAndWorkUs, deviceAndWorkUs + _lateUs + Reads * 3);
    }

    /// <summary>A host stall of 1 s hands at most <see cref="IoTiming.MaxLateCreditSeconds"/> to the reads after it: 80
    /// reads (120 ms of device time) then take 20 ms, not nothing.</summary>
    [Fact]
    public void TheCreditOfALongHostStallIsBounded()
    {
        _lateUs = 1_000_000;
        IoTiming.Read(ReadBytes);
        _lateUs = 0;
        long elapsedUs = ReadSeries(80);
        long creditUs = (long)(IoTiming.MaxLateCreditSeconds * 1e6);
        Assert.InRange(elapsedUs, 80 * CostUs - creditUs, 80 * CostUs - creditUs + 80 * 3);
    }
}
