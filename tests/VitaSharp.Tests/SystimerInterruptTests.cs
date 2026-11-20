using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The Systimer's interrupts in guest time (systimer.skprx 3.74, study only): the twelve lines its module_start registers
/// (0x80 + n word, 0x88 + n long-range), enabled by Alloc and disabled by Free; a timer with an interval raising its
/// line at each period (the counter starting again from 0), the line handler adding the interval to the offset and
/// calling the driver's handler (id, argument) in interrupt context; a word timer without an interval raising at its
/// 32-bit wrap (the offset grows by 2^32); a disabled line left pending until enabled; late periods caught up; nothing
/// armed and no dispatcher thread without a counting timer; the setters refused inside a handler (0x80027101).
/// Guest time is a controlled clock; the due lines fire at the time the test names.
/// </summary>
[Collection("guest")]
public sealed class SystimerInterruptTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InInterrupt = unchecked((int)0x80027101), Registered = unchecked((int)0x80027109);
    private const uint LongRange0 = 0x10000, Word0 = 0x20000;
    private const uint LongRangeLine0 = 0x88, WordLine0 = 0x80;

    private readonly HleHarness _harness = new(fixture, "systintr");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    /// <summary>ARM code that adds 1 to the word at <paramref name="target"/>, stores r0 and r1 after it, returns 0.</summary>
    private uint Counter(uint target)
    {
        uint code = _harness.Alloc(48);
        GuestMemory.W32(code, 0xE59FC01C); // ldr r12, [pc, #28]  (the word at +36)
        GuestMemory.W32(code + 4, 0xE59C2000); // ldr r2, [r12]
        GuestMemory.W32(code + 8, 0xE2822001); // add r2, r2, #1
        GuestMemory.W32(code + 12, 0xE58C2000); // str r2, [r12]
        GuestMemory.W32(code + 16, 0xE58C0004); // str r0, [r12, #4]
        GuestMemory.W32(code + 20, 0xE58C1008); // str r1, [r12, #8]
        GuestMemory.W32(code + 24, 0xE3A00000); // mov r0, #0
        GuestMemory.W32(code + 28, 0xE12FFF1E); // bx lr
        GuestMemory.W32(code + 32, 0xE12FFF1E);
        GuestMemory.W32(code + 36, target);
        return code;
    }

    private ulong Read64(uint id, string name)
    {
        uint value = _harness.Alloc(8);
        Assert.Equal(0, Call(Services, name, id, value));
        return GuestMemory.Read<ulong>(value);
    }

    /// <summary>The due lines fire only when the test says (no dispatcher thread racing it).</summary>
    private void ByHand() => Services.IntrDispatchByHand();

    private int DispatchAt(ulong nowUs)
    {
        int fired = 0;
        _harness.OnGuest(_ => fired = Services.IntrDispatchDue(nowUs));
        return fired;
    }

    private ulong Deadline(uint line) => Services.IntrSchedule(line).Deadline ?? throw new Xunit.Sdk.XunitException($"line 0x{line:X} not armed");

    /// <summary>A long-range timer with <paramref name="interval"/>, the counter handler and argument 0x77, started.</summary>
    private (uint Id, uint Seen) StartedLongRange(ulong interval)
    {
        uint id = (uint)Call(Services, "ksceKernelSysTimerAlloc", 1u);
        uint seen = _harness.Alloc(16);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetInterval", id, 0u, (uint)interval, (uint)(interval >> 32)));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetHandler", id, Counter(seen), 0xFu, 0x77u));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStartCount", id));
        return (id, seen);
    }

    [Fact]
    public void TheModuleRegistersTheTimerLinesAtBoot()
    {
        for (uint n = 0; n < 7; n++)
        {
            Assert.NotEqual(0u, Services.IntrState(WordLine0 + n).Handler);
            Assert.Equal(0x80u, Services.IntrState(WordLine0 + n).Priority);
        }

        for (uint n = 0; n < 5; n++)
        {
            Assert.NotEqual(0u, Services.IntrState(LongRangeLine0 + n).Handler);
        }

        Assert.Equal(0u, Services.IntrState(0x87).Handler);
        Assert.Equal(Registered, Call(Services, "ksceKernelRegisterIntrHandler", 0x80u, 0u, 0u, 0x81000001u, 0u, 0x80u, 1u, 0u));
        Assert.False(Services.IntrState(LongRangeLine0).Enabled);
        uint id = (uint)Call(Services, "ksceKernelSysTimerAlloc", 1u);
        Assert.True(Services.IntrState(LongRangeLine0).Enabled);
        Assert.Equal(0xFu, Services.IntrState(LongRangeLine0).Target);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetHandler", id, 0x81000001u, 0x20000u, 0u));
        Assert.Equal(2u, Services.IntrState(LongRangeLine0).Target);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerFree", id));
        Assert.False(Services.IntrState(LongRangeLine0).Enabled);
    }

    [Fact]
    public void NothingIsArmedWithoutACountingTimerWithAPeriod()
    {
        using var clock = ControlledGuestClock.Stop();
        uint id = (uint)Call(Services, "ksceKernelSysTimerAlloc", 1u);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStartCount", id));
        Assert.Equal((null, false), Services.IntrSchedule(LongRangeLine0));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStopCount", id));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetInterval", id, 0u, 500u, 0u));
        Assert.Equal((null, false), Services.IntrSchedule(LongRangeLine0));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStartCount", id));
        Assert.NotNull(Services.IntrSchedule(LongRangeLine0).Deadline);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStopCount", id));
        Assert.Null(Services.IntrSchedule(LongRangeLine0).Deadline);
    }

    [Fact]
    public void AnIntervalRaisesTheLineAtEachPeriodAndCallsTheHandler()
    {
        using var clock = ControlledGuestClock.Stop();
        ByHand();
        (uint id, uint seen) = StartedLongRange(1000);
        ulong first = Deadline(LongRangeLine0);
        Assert.Equal(0, DispatchAt(first - 1));
        clock.Advance(1000);
        Assert.Equal(1, DispatchAt(first));
        Assert.Equal(new uint[] { 1, id, 0x77 }, Words(seen, 3));
        Assert.Equal(first + 1000, Deadline(LongRangeLine0));
        // the counter started again from 0 and the offset holds the period
        Assert.Equal(0UL, Read64(id, "ksceKernelSysTimerGetCount"));
        Assert.Equal(1000UL, Read64(id, "ksceKernelSysTimerGetCountWithOffset"));
        clock.Advance(250);
        Assert.Equal(250UL, Read64(id, "ksceKernelSysTimerGetCount"));
        Assert.Equal(1250UL, Read64(id, "ksceKernelSysTimerGetCountWithOffset"));
        Assert.Equal(0, Call(Services, "ksceKernelIsIntrPending", LongRangeLine0));
    }

    [Fact]
    public void ThePrescalerStretchesThePeriod()
    {
        using var clock = ControlledGuestClock.Stop();
        ByHand();
        uint id = (uint)Call(Services, "ksceKernelSysTimerAlloc", 2u);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetClockSource", id, 0u, 3u));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetInterval", id, 0u, 100u, 0u));
        ulong before = _harness.Emu.Kernel.ProcessTimeUs;
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStartCount", id));
        Assert.Equal(400UL, Deadline(WordLine0) - before);
    }

    [Fact]
    public void LatePeriodsAreCaughtUp()
    {
        using var clock = ControlledGuestClock.Stop();
        ByHand();
        (uint id, uint seen) = StartedLongRange(1000);
        ulong first = Deadline(LongRangeLine0);
        clock.Advance(3500);
        Assert.Equal(3, DispatchAt(first + 2500));
        Assert.Equal(3u, GuestMemory.R32(seen));
        Assert.Equal(3000UL + 500, Read64(id, "ksceKernelSysTimerGetCountWithOffset"));
        Assert.Equal(first + 3000, Deadline(LongRangeLine0));
    }

    [Fact]
    public void ADisabledLineStaysPendingUntilEnabled()
    {
        using var clock = ControlledGuestClock.Stop();
        ByHand();
        (_, uint seen) = StartedLongRange(1000);
        ulong first = Deadline(LongRangeLine0);
        Assert.Equal(0, Call(Services, "ksceKernelDisableIntr", LongRangeLine0));
        clock.Advance(1000);
        Assert.Equal(1, DispatchAt(first));
        Assert.Equal(0u, GuestMemory.R32(seen));
        Assert.Equal(1, Call(Services, "ksceKernelIsIntrPending", LongRangeLine0));
        _harness.OnGuest(_ => Assert.Equal(0, Call(Services, "ksceKernelEnableIntr", LongRangeLine0)));
        Assert.Equal(1u, GuestMemory.R32(seen));
        Assert.Equal(0, Call(Services, "ksceKernelIsIntrPending", LongRangeLine0));
    }

    [Fact]
    public void AWordTimerWithoutAnIntervalRaisesAtItsWrap()
    {
        using var clock = ControlledGuestClock.Stop();
        ByHand();
        uint id = (uint)Call(Services, "ksceKernelSysTimerAlloc", 2u);
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerSetCount", id, 0u, 0xFFFFFF00u, 0u));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStartCount", id));
        ulong wrap = Deadline(WordLine0);
        clock.Advance(0xFF);
        ulong before = Read64(id, "ksceKernelSysTimerGetCountWithOffset");
        clock.Advance(1);
        Assert.Equal(1, DispatchAt(wrap));
        Assert.Equal(0UL, Read64(id, "ksceKernelSysTimerGetCount"));
        Assert.Equal(before + 1, Read64(id, "ksceKernelSysTimerGetCountWithOffset"));
        Assert.Equal(wrap + 0x1_0000_0000UL, Deadline(WordLine0));
    }

    [Fact]
    public void TheDispatcherThreadFiresInGuestTime()
    {
        using var clock = ControlledGuestClock.Stop();
        (uint id, uint seen) = StartedLongRange(1000);
        Assert.True(Services.IntrSchedule(LongRangeLine0).Dispatcher);
        Assert.Equal(0u, GuestMemory.R32(seen));
        clock.Advance(1000);
        Until(() => GuestMemory.R32(seen) == 1, "the dispatcher raising the timer's line");
        Assert.Equal(id, GuestMemory.R32(seen + 4));
        Assert.Equal(0, Call(Services, "ksceKernelSysTimerStopCount", id));
    }

    [Fact]
    public void TheSettersAreRefusedInsideAHandler()
    {
        uint id = (uint)Call(Services, "ksceKernelSysTimerAlloc", 1u);
        uint count = _harness.Alloc(8);
        FieldInfo depth = typeof(SystemServicesVsh).GetField("t_intrDepth", BindingFlags.Static | BindingFlags.NonPublic)!;
        depth.SetValue(null, 1);
        try
        {
            Assert.Equal(InInterrupt, Call(Services, "ksceKernelSysTimerAlloc", 1u));
            Assert.Equal(InInterrupt, Call(Services, "ksceKernelSysTimerFree", id));
            Assert.Equal(InInterrupt, Call(Services, "ksceKernelSysTimerSetClockSource", id, 2u, 0u));
            Assert.Equal(InInterrupt, Call(Services, "ksceKernelSysTimerSetInterval", id, 0u, 1u, 0u));
            Assert.Equal(InInterrupt, Call(Services, "ksceKernelSysTimerSetHandler", id, 0u, 0u, 0u));
            Assert.Equal(InInterrupt, Call(Services, "ksceKernelSysTimerResetCount", id));
            Assert.Equal(InInterrupt, Call(Services, "ksceKernelSysTimerSetCount", id, 0u, 1u, 0u));
            Assert.Equal(InInterrupt, Call(Services, "ksceKernelSysTimerSetCaptureMode", id, 0u, 0u));
            Assert.Equal(InInterrupt, Call(Services, "ksceKernelSysTimerSetOutputMode", id, 0u));
            // the counting and the reads work in a handler
            Assert.Equal(0, Call(Services, "ksceKernelSysTimerStartCount", id));
            Assert.Equal(0, Call(Services, "ksceKernelSysTimerGetCount", id, count));
            Assert.Equal(0, Call(Services, "ksceKernelSysTimerStopCount", id));
        }
        finally
        {
            depth.SetValue(null, 0);
        }
    }

    [Fact]
    public void InterruptContextIsTheHandlersThreadOnly()
    {
        FieldInfo depth = typeof(SystemServicesVsh).GetField("t_intrDepth", BindingFlags.Static | BindingFlags.NonPublic)!;
        depth.SetValue(null, 1);
        try
        {
            int other = -1;
            var thread = new Thread(() => other = Call(Services, "ksceKernelIsIntrContext"));
            thread.Start();
            thread.Join();
            Assert.Equal(0, other);
            Assert.Equal(1, Call(Services, "ksceKernelIsIntrContext"));
        }
        finally
        {
            depth.SetValue(null, 0);
        }
    }

    private static uint[] Words(uint address, int count)
    {
        return Enumerable.Range(0, count).Select(i => GuestMemory.R32(address + (uint)(i * 4))).ToArray();
    }
}
