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
}
