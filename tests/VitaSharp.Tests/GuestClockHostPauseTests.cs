using VitaSharp.Core.Util;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The guest clock leaves out the host's stop-the-world pauses (the .NET garbage collector's): while the runtime has
/// every thread stopped, no guest thread runs, so on the console's clock that time never passed. Dead Space's audio
/// died at boot because a 9 ms collection in the middle of its mixer's first round let the port's audio thread wake
/// from its 11 ms sleep, find the mixer still busy and signal it then, which the port's pthread semaphores turn into a
/// lost wake-up. The host timestamp and the pause total are test seams, so nothing here depends on the host's speed.
/// </summary>
[Collection("guest")]
public sealed class GuestClockHostPauseTests : IDisposable
{
    private long _timestamp = 1_000_000;
    private long _pause;

    public GuestClockHostPauseTests()
    {
        GuestClock.HostTimestampForTest = () => Volatile.Read(ref _timestamp);
        GuestClock.HostPauseForTest = () => Volatile.Read(ref _pause);
    }

    public void Dispose()
    {
        GuestClock.HostTimestampForTest = null;
        GuestClock.HostPauseForTest = null;
    }

    [Fact]
    public void HostPauseTimeDoesNotPassOnTheGuestClock()
    {
        long before = GuestClock.Ticks;
        _timestamp += 5_000; // 5000 ticks of host time, 3000 of them with the runtime stopped
        _pause += 3_000;
        long after = GuestClock.Ticks;
        Assert.Equal(2_000, after - before);
    }

    /// <summary>The process may collect before the clock starts (a test run, the launcher): those pauses are not the
    /// guest's, or the clock reads below zero at its start and "guest time 0" has not passed yet.</summary>
    [Fact]
    public void PausesFromBeforeTheClockStartedAreNotSubtracted()
    {
        Assert.Equal(5_000, GuestClock.ElapsedWithoutPauses(start: 1_000, startPause: 40_000, now: 6_000, pause: 40_000));
        Assert.Equal(2_000, GuestClock.ElapsedWithoutPauses(start: 1_000, startPause: 40_000, now: 6_000, pause: 43_000));
    }

    [Fact]
    public void TimeWithoutPausesPassesInFull()
    {
        long before = GuestClock.Ticks;
        _timestamp += 7_000;
        Assert.Equal(7_000, GuestClock.Ticks - before);
    }

    [Fact]
    public void AReadingNeverMixesATimestampAndAPauseTotalFromEitherSideOfACollection()
    {
        long before = GuestClock.Ticks;
        _timestamp += 10_000;
        _pause += 4_000;
        // the pause total changes once while the clock is read (a collection ends between its reads): the reading is
        // taken again from a consistent pair, never "timestamp after the collection, pause total before it"
        int reads = 0;
        GuestClock.HostPauseForTest = () =>
        {
            reads++;
            return reads == 1 ? _pause - 4_000 : _pause;
        };
        long after = GuestClock.Ticks;
        Assert.Equal(6_000, after - before);
        Assert.True(reads >= 3, $"the pause total was read {reads} times");
    }

    [Fact]
    public void FrozenClockIgnoresPausesAndResumesWithoutAJump()
    {
        using (ControlledGuestClock.Stop())
        {
            long frozen = GuestClock.Ticks;
            _timestamp += 9_000;
            _pause += 9_000;
            Assert.Equal(frozen, GuestClock.Ticks);
        }

        long resumed = GuestClock.Ticks;
        _timestamp += 1_000;
        Assert.Equal(1_000, GuestClock.Ticks - resumed);
    }

    /// <summary>A collection while a thread sleeps toward a deadline: the guest clock lives through less than the
    /// sleep, and the wait goes on until the clock really is at the deadline (a camera read waiting for the next frame
    /// woke short of it on a loaded CI host and handed back the previous frame as already read).</summary>
    [Fact]
    public void AWaitOutlivesAHostPauseInTheMiddleOfIt()
    {
        long pauseTicks = System.Diagnostics.Stopwatch.Frequency / 50; // 20 ms with the runtime stopped
        HostPauseDuringTheNextWait pause = HostPauseDuringTheNextWait.Arm(pauseTicks);
        long deadline = GuestClock.Ticks + System.Diagnostics.Stopwatch.Frequency / 25; // 40 ms ahead
        pause.Start();
        GuestClock.WaitUntil(deadline);
        long reached = GuestClock.Ticks;
        Assert.True(pause.Happened, "the pause was not injected");
        Assert.True(reached >= deadline, $"woke {deadline - reached} ticks short of the deadline");
    }

    /// <summary>A frozen clock (a test owns it) is not waited for: the wait sleeps once and returns, as before.</summary>
    [Fact]
    public void AWaitOnAFrozenClockReturns()
    {
        using (ControlledGuestClock.Stop())
        {
            GuestClock.WaitUntil(GuestClock.Ticks + (System.Diagnostics.Stopwatch.Frequency / 1000));
        }
    }
}

/// <summary>Test seam: the real host clock, with one host pause of a given length that starts 1 ms after
/// <see cref="Start"/> (inside the wait that follows), as a garbage collection the guest clock leaves out.</summary>
internal sealed class HostPauseDuringTheNextWait
{
    private readonly long _length;
    private long _startedAt = long.MaxValue;

    private HostPauseDuringTheNextWait(long length)
    {
        _length = length;
    }

    public bool Happened { get; private set; }

    /// <summary>Installs the seams (real timestamp, real pause total plus the injected pause); the caller removes them
    /// (<see cref="GuestClock.HostTimestampForTest"/> = null) when done.</summary>
    public static HostPauseDuringTheNextWait Arm(long lengthTicks)
    {
        var pause = new HostPauseDuringTheNextWait(lengthTicks);
        GuestClock.HostTimestampForTest = System.Diagnostics.Stopwatch.GetTimestamp;
        GuestClock.HostPauseForTest = pause.PauseTotal;
        return pause;
    }

    public void Start() => Volatile.Write(ref _startedAt, System.Diagnostics.Stopwatch.GetTimestamp());

    private long PauseTotal()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long startedAt = Volatile.Read(ref _startedAt);
        // a step, not a ramp: the clock reads the pause total on both sides of its timestamp and retries while they differ
        bool paused = startedAt != long.MaxValue && now - startedAt >= System.Diagnostics.Stopwatch.Frequency / 1000;
        if (paused)
        {
            Happened = true;
        }

        return RealPause() + (paused ? _length : 0);
    }

    private static long RealPause()
    {
        long ticks = GC.GetTotalPauseDuration().Ticks;
        return (long)(ticks * ((double)System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond));
    }
}
