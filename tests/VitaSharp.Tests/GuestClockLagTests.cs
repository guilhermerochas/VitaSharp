using System.Diagnostics;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Util;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The guest clock leaves out the time a guest thread holding an emulated core falls behind its emulated CPU
/// (Util/GuestClock.Lag.cs, Cpu/CpuCore.LagLedger.cs). On the console the clock and the CPU's work move together: a
/// mixer worker running 35K instructions takes ~0.1 ms of process time, so the 11 ms sleep of Dead Space's audio thread
/// always ends after the mixer's round. On the host that burst took 28 ms (cold code), and with the clock in real time
/// the sleep ended in the middle of the round and the port's audio stopped for good. The host timestamp and the pause
/// total are the clock's test seams: nothing here depends on the host's speed.
/// </summary>
[Collection("guest")]
public sealed unsafe class GuestClockLagTests : IDisposable
{
    private static long Ms => Stopwatch.Frequency / 1000;
    private long _timestamp = 1_000_000_000;
    private readonly bool _wasEnabled = GuestClock.LagClockEnabled;
    private readonly long _rate = CpuCore.InstructionsPerSecond;

    public GuestClockLagTests()
    {
        GuestClock.HostTimestampForTest = () => Volatile.Read(ref _timestamp);
        GuestClock.HostPauseForTest = () => 0;
        GuestClock.LagClockEnabled = true;
        CpuCore.InstructionsPerSecond = CpuCore.DefaultClockHz;
        GuestClock.ResetLagForTest();
    }

    public void Dispose()
    {
        GuestClock.ResetLagForTest();
        GuestClock.LagClockEnabled = _wasEnabled;
        CpuCore.InstructionsPerSecond = _rate;
        GuestClock.HostTimestampForTest = null;
        GuestClock.HostPauseForTest = null;
    }

    private void Advance(long ticks) => Volatile.Write(ref _timestamp, _timestamp + ticks);

    /// <summary>A core taken and running guest code (out of host work), as after Scheduler.Acquire and Run.</summary>
    private static CpuCore RunningCore()
    {
        var core = new CpuCore(new BlockCache());
        core.ResetThrottle();
        core.LeaveHostWork();
        return core;
    }

    [Fact]
    public void ATimedWaitDoesNotEndInTheMiddleOfAnotherThreadsLateBurst()
    {
        using CpuCore worker = RunningCore();
        long waitStart = GuestClock.Ticks;
        Advance(28 * Ms); // the cold job: 28 ms of host time on its core for 35K instructions
        worker.ChargeInstructions(35_000);
        CpuCore.SettleLagClock(); // what the sleeper does when its 11 ms deadline comes

        long waited = GuestClock.WaitTicks - waitStart;
        long expected = 35_000 * Stopwatch.Frequency / CpuCore.DefaultClockHz + GuestClock.LagAllowanceTicks;
        Assert.InRange(waited, expected - 2, expected + 2);
        Assert.False(VitaSharp.Core.Kernel.Wait.Expired(waitStart, 11_000), "an 11 ms wait ended inside a 0.1 ms burst");
        Assert.True(GuestClock.Ticks >= waitStart + 28 * Ms - GuestClock.LagTicks, "the clock went back");
    }

    [Fact]
    public void WithTheLagClockOffTheWaitEndsInsideTheBurst()
    {
        GuestClock.LagClockEnabled = false;
        using CpuCore worker = RunningCore();
        long waitStart = GuestClock.Ticks;
        Advance(28 * Ms);
        worker.ChargeInstructions(35_000);
        CpuCore.SettleLagClock();
        Assert.True(VitaSharp.Core.Kernel.Wait.Expired(waitStart, 11_000));
    }

    [Fact]
    public void ReadingsNeverGoBackWhenTimeIsLeftOut()
    {
        using CpuCore worker = RunningCore();
        Advance(10 * Ms);
        long before = GuestClock.Ticks;
        CpuCore.SettleLagClock();
        Assert.True(GuestClock.LagTicks > 0);
        Assert.Equal(before, GuestClock.Ticks); // stands at the reading it had
        Assert.True(GuestClock.WaitTicks < before);
        Advance(GuestClock.LagTicks + Ms);
        Assert.Equal(before + Ms, GuestClock.Ticks); // real time caught up: it goes on from there
    }

    [Fact]
    public void HostWorkOnTheCoreIsNotLateness()
    {
        using CpuCore worker = RunningCore();
        worker.EnterHostWork(); // an HLE call that waits on the host for 40 ms while holding the core
        Advance(40 * Ms);
        worker.LeaveHostWork();
        CpuCore.SettleLagClock();
        Assert.Equal(0, GuestClock.LagTicks);

        worker.EnterHostWork(); // and one still in progress when another thread looks
        Advance(40 * Ms);
        CpuCore.SettleLagClock();
        Assert.Equal(0, GuestClock.LagTicks);
    }

    [Fact]
    public void AThreadWithoutACoreIsNotLate()
    {
        using CpuCore blocked = RunningCore();
        blocked.SettleThrottle(0); // gives the core away (a blocking wait)
        Advance(500 * Ms);
        CpuCore.SettleLagClock();
        Assert.Equal(0, GuestClock.LagTicks);
    }

    [Fact]
    public void ThreadsLateOverTheSameStretchLeaveItOutOnce()
    {
        using CpuCore first = RunningCore();
        using CpuCore second = RunningCore();
        Advance(20 * Ms);
        CpuCore.SettleLagClock();
        Assert.Equal(20 * Ms - GuestClock.LagAllowanceTicks, GuestClock.LagTicks);
    }

    [Fact]
    public void TheBudgetIsABurstThatRefillsAtATenthOfRealTime()
    {
        using CpuCore worker = RunningCore();
        Advance(500 * Ms); // a thread 500 ms behind: only the burst is left out
        CpuCore.SettleLagClock();
        Assert.Equal(GuestClock.LagBurstTicks, GuestClock.LagTicks);
        Assert.True(GuestClock.LagRefusals > 0);

        long leftOut = GuestClock.LagTicks;
        Advance(100 * Ms); // 100 ms more behind: 10 ms of budget came back
        CpuCore.SettleLagClock();
        Assert.Equal(leftOut + 10 * Ms, GuestClock.LagTicks);
    }

    [Fact]
    public void LatenessWithinTheAllowanceIsNotLeftOut()
    {
        using CpuCore worker = RunningCore();
        Advance(GuestClock.LagAllowanceTicks);
        CpuCore.SettleLagClock();
        Assert.Equal(0, GuestClock.LagTicks);
    }

    [Fact]
    public void AThreadKeepingUpWithItsEmulatedCpuIsNotLate()
    {
        using CpuCore worker = RunningCore();
        Advance(10 * Ms);
        worker.ChargeInstructions(CpuCore.DefaultClockHz / 100); // 10 ms of instructions in 10 ms
        CpuCore.SettleLagClock();
        Assert.Equal(0, GuestClock.LagTicks);
    }
}
