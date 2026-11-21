using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSyscon's event handlers are called when the chip changes (syscon.skprx 3.74, decompiled, study only): a change
/// of an event bit of the chip's status word calls that event's handler (the bit's value, the argument) in interrupt
/// context; an unchanged write calls nothing; the status byte's AC adapter bit calls event 36's; the first report
/// tells the multi connector's events (5 unknown until then, 37 with 0x4FF00 for nothing plugged); the chip's RTC
/// alarm bit reaches event 3's handler. The chip's interrupt is an HLE source of the interrupt scheduler, fired here
/// by hand at the time the test names.
/// </summary>
[Collection("guest")]
public sealed class SysconEventTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "sysconev");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private SysconKernel Syscon => _harness.Module<SysconKernel>();

    private SysconChip Chip => _harness.Emu.Vita.Syscon;

    /// <summary>ARM code that stores r0 and r1 after the word at <paramref name="target"/>, then adds 1 to it; returns 0.</summary>
    private uint Counter(uint target)
    {
        uint code = _harness.Alloc(48);
        GuestMemory.W32(code, 0xE59FC01C); // ldr r12, [pc, #28]  (the word at +36)
        GuestMemory.W32(code + 4, 0xE58C0004); // str r0, [r12, #4]
        GuestMemory.W32(code + 8, 0xE58C1008); // str r1, [r12, #8]
        GuestMemory.W32(code + 12, 0xE59C2000); // ldr r2, [r12]
        GuestMemory.W32(code + 16, 0xE2822001); // add r2, r2, #1
        GuestMemory.W32(code + 20, 0xE58C2000); // str r2, [r12]
        GuestMemory.W32(code + 24, 0xE3A00000); // mov r0, #0
        GuestMemory.W32(code + 28, 0xE12FFF1E); // bx lr
        GuestMemory.W32(code + 32, 0xE12FFF1E);
        GuestMemory.W32(code + 36, target);
        return code;
    }

    private ulong? Pending => Services.IntrSchedule(SystemServicesVsh.HleInterruptSyscon).Deadline;

    /// <summary>The chip's interrupt, if it is armed, taken now.</summary>
    private void TakeInterrupt()
    {
        Assert.NotNull(Pending);
        ulong now = Services.IntrNowUs;
        _harness.OnGuest(_ => Services.IntrDispatchDue(now));
        Assert.Null(Pending);
    }

    private (uint Count, uint Value, uint Argument) Seen(uint seen) => (GuestMemory.R32(seen), GuestMemory.R32(seen + 4), GuestMemory.R32(seen + 8));

    [Fact]
    public void AChangedEventBitCallsItsHandler()
    {
        using var clock = ControlledGuestClock.Stop();
        Services.IntrDispatchByHand();
        uint alarm = _harness.Alloc(16), multi = _harness.Alloc(16), connector = _harness.Alloc(16), ac = _harness.Alloc(16);
        Assert.Equal(0, Call(Syscon, "NID_8351526D", Counter(alarm), 0x33u)); // event 3
        Assert.Equal(0, Call(Syscon, "NID_35E1689F", Counter(multi), 0x55u)); // event 5
        Assert.Equal(0, Call(Syscon, "NID_A26586B2", Counter(connector), 0x37u)); // event 37
        Assert.Equal(0, Call(Syscon, "ksceSysconSetAlarmCallback", Counter(ac), 0x36u)); // event 36
        Assert.Null(Pending);

        Chip.SetStatusBit(3, true);
        TakeInterrupt();
        Assert.Equal((1u, 1u, 0x33u), Seen(alarm));
        // the first report tells the multi connector (its state was unknown): nothing plugged
        Assert.Equal((1u, 0u, 0x55u), Seen(multi));
        Assert.Equal((1u, SysconChip.NothingOnMultiConnector, 0x37u), Seen(connector));

        Chip.SetStatusBit(3, true);
        Assert.Null(Pending);
        Chip.SetStatusBit(3, false);
        TakeInterrupt();
        Assert.Equal((2u, 0u, 0x33u), Seen(alarm));
        Assert.Equal(1u, Seen(multi).Count);
        Assert.Equal(0u, Seen(ac).Count);

        Chip.AcAdapter = true;
        TakeInterrupt();
        Assert.Equal((1u, 1u, 0x36u), Seen(ac));
        Assert.Equal(2u, Seen(alarm).Count);
    }

    /// <summary>The RTC alarm ringing raises the chip's alarm bit, which reaches event 3's handler.</summary>
    [Fact]
    public void TheRtcAlarmReachesTheSysconHandler()
    {
        using var clock = ControlledGuestClock.Stop();
        Services.IntrDispatchByHand();
        uint seen = _harness.Alloc(16);
        Assert.Equal(0, Call(Syscon, "NID_8351526D", Counter(seen), 0x44u));
        RtcKernel rtc = _harness.Module<RtcKernel>();
        uint tick = _harness.Alloc(8);
        GuestMemory.Write(tick, RtcKernel.ClockTick(_harness.Emu.Vita.RtcClocks, RtcClocks.Main) + 2_000_000);
        Assert.Equal(0, Call(rtc, "ksceRtcSetAlarmTick", tick, 0u));
        clock.Advance(3_000_000);
        Until(() => Chip.StatusBit(3) && Pending != null, "the alarm rings and the chip reports it");
        TakeInterrupt();
        Assert.Equal((1u, 1u, 0x44u), Seen(seen));
    }
}
