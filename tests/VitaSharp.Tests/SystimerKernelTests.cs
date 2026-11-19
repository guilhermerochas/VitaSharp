using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSystimerForDriver (systimer.skprx 3.74): five long-range and seven word timers handed out in order, the
/// allocation, counting and argument checks, and counters that run on guest time (a controlled clock: one tick per
/// microsecond over the prescaler + 1, word timers wrapping at 32 bits), stop, reset, set and the offset.
/// </summary>
[Collection("guest")]
public sealed class SystimerKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80020005), NoTimer = unchecked((int)0x80027301);
    private const int NotAllocated = unchecked((int)0x80027302), Counting = unchecked((int)0x80027303);
    private const int NotCounting = unchecked((int)0x80027304), NoMask = unchecked((int)0x80027105);

    private readonly HleHarness _harness = new(fixture, "systk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x0450933Bu)]
    [InlineData(0x1B14DFD6u)]
    [InlineData(0x59EBD22Du)]
    [InlineData(0x7DEBA6A5u)]
    [InlineData(0x8358611Fu)]
    [InlineData(0x85AAE0DDu)]
    [InlineData(0x904DF737u)]
    [InlineData(0x963F7C0Du)]
    [InlineData(0xA73351C1u)]
    [InlineData(0xBF8D42B0u)]
    [InlineData(0xC34D7940u)]
    [InlineData(0xCCCD1EE3u)]
    [InlineData(0xE2B9D8E9u)]
    [InlineData(0xFD5F1742u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSystimerForDriver").ToList();
        Assert.Single(bindings);
    }

    [Fact]
    public void TimersAreHandedOutInOrderUntilNoneIsFree()
    {
        Assert.Equal(Invalid, Call(Services, "ksceKernelSysTimerAlloc", 3u));
        for (uint n = 0; n < 5; n++)
        {
            Assert.Equal((int)(0x10000 | n), Call(Services, "ksceKernelSysTimerAlloc", 1u));
        }

        Assert.Equal(NoTimer, Call(Services, "ksceKernelSysTimerAlloc", 1u));
        for (uint n = 0; n < 7; n++)
        {
            Assert.Equal((int)(0x20000 | n), Call(Services, "ksceKernelSysTimerAlloc", 2u));
        }

        Assert.Equal(NoTimer, Call(Services, "ksceKernelSysTimerAlloc", 2u));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerFree", 0x10002u));
        Assert.Equal(NotAllocated, Call(Services, "ksceKernelSysTimerFree", 0x10002u));
        Assert.Equal(0x10002, Call(Services, "ksceKernelSysTimerAlloc", 1u));
    }

    [Fact]
    public void SettingsAreCheckedAndRefusedWhileCounting()
    {
        uint timer = (uint)Call(Services, "ksceKernelSysTimerAlloc", 2u);
        Assert.Equal(NotAllocated, Call(Services, "ksceKernelSysTimerSetClockSource", 0x20006u, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelSysTimerSetClockSource", timer, 2u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelSysTimerSetClockSource", timer, 0xEu, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelSysTimerSetClockSource", timer, 0x16u, 0u));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetClockSource", timer, 0x10u, 3u));
        Assert.Equal(NoMask, Call(Services, "ksceKernelSysTimerSetHandler", timer, 0x81000001u, 0u, 7u));
        Assert.Equal(NoMask, Call(Services, "ksceKernelSysTimerSetHandler", timer, 0x81000001u, 0x1000Fu, 7u));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetHandler", timer, 0x81000001u, 0xFu, 7u));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetInterval", timer, 0u, 1000u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceKernelSysTimerSetCaptureMode", timer, 0xEu, 0u));
        Assert.Equal(NotCounting, Call(Services, "ksceKernelSysTimerStopCount", timer));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStartCount", timer));
        Assert.Equal(Counting, Call(Services, "ksceKernelSysTimerStartCount", timer));
        Assert.Equal(Counting, Call(Services, "ksceKernelSysTimerSetInterval", timer, 0u, 5u, 0u));
        Assert.Equal(Counting, Call(Services, "ksceKernelSysTimerSetOutputMode", timer, 1u));
        Assert.Equal((true, true, 1000UL, 0x81000001u, 0x10u, 3u), Services.SystimerState(timer));
    }

    [Fact]
    public void CountersRunOnGuestTime()
    {
        using var clock = ControlledGuestClock.Stop();
        uint timer = (uint)Call(Services, "ksceKernelSysTimerAlloc", 1u);
        uint count = _harness.Alloc(8);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetClockSource", timer, 0u, 1u));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStartCount", timer));
        clock.Advance(1000);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerGetCount", timer, count));
        Assert.Equal(500UL, GuestMemory.Read<ulong>(count));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStopCount", timer));
        clock.Advance(1000);
        Call(Services, "ksceKernelSysTimerGetCount", timer, count);
        Assert.Equal(500UL, GuestMemory.Read<ulong>(count));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetCount", timer, 0u, 0x10u, 1u));
        Call(Services, "ksceKernelSysTimerGetCount", timer, count);
        Assert.Equal(0x1_0000_0010UL, GuestMemory.Read<ulong>(count));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerGetCountWithOffset", timer, count));
        Assert.Equal(0x2_0000_0020UL, GuestMemory.Read<ulong>(count));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerResetCount", timer));
        Call(Services, "ksceKernelSysTimerGetCountWithOffset", timer, count);
        Assert.Equal(0UL, GuestMemory.Read<ulong>(count));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerGetCapture", timer, count));
        Assert.Equal(NotAllocated, Call(Services, "ksceKernelSysTimerGetCount", 0x10004u, count));
    }

    [Fact]
    public void WordTimersWrapAtThirtyTwoBits()
    {
        using var clock = ControlledGuestClock.Stop();
        uint timer = (uint)Call(Services, "ksceKernelSysTimerAlloc", 2u);
        uint count = _harness.Alloc(8);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetCount", timer, 0u, 0xFFFFFFF0u, 5u));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStartCount", timer));
        clock.Advance(0x20);
        Call(Services, "ksceKernelSysTimerGetCount", timer, count);
        Assert.Equal(0x10UL, GuestMemory.Read<ulong>(count));
    }
}
