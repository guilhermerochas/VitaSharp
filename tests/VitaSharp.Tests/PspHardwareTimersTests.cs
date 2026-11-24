using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Psp;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// The PSP hardware timers (0xBC500000): the 48 MHz count through the prescaler, the cycle, the overflow byte, the
/// status read that acknowledges, the read-only view, the interrupt line, and a MIPS program that programs timer 0,
/// takes its interrupt through the controller and acknowledges it.
/// </summary>
public sealed class PspHardwareTimersTests
{
    private const uint Status = PspHardwareTimers.Counting | PspHardwareTimers.InterruptEnable;

    private static PspHardwareTimers Timers(out PspHardwareClock clock, out PspInterruptController interrupts)
    {
        clock = new PspHardwareClock();
        interrupts = new PspInterruptController();
        interrupts.Write32(0x08, 0xFu << PspInterrupt.SystemTimer0);
        return new PspHardwareTimers(clock, interrupts);
    }

    private static void Start(PspHardwareTimers timers, int timer, uint cycle, uint numerator, uint denominator, uint mode)
    {
        uint offset = (uint)timer * PspHardwareTimers.Stride;
        timers.Write32(offset + 0x8, numerator);
        timers.Write32(offset + 0xC, denominator);
        timers.Write32(offset, PspHardwareTimers.ResetOverflow | mode | cycle);
    }

    [Fact]
    public void ResetStateIsStoppedAndZero()
    {
        PspHardwareTimers timers = Timers(out _, out _);
        for (uint offset = 0; offset < PspHardwareTimers.BlockSize; offset += 4)
        {
            Assert.Equal(0u, timers.Read32(offset));
        }

        Assert.Equal(long.MaxValue, timers.NextEventNs);
    }

    [Fact]
    public void CountsDownTheCycleAt48MhzAndReportsThePeriods()
    {
        PspHardwareTimers timers = Timers(out PspHardwareClock clock, out _);
        Start(timers, 1, 48, 0xFFFFFFFF, 0xFFFFFFFF, PspHardwareTimers.Counting);
        Assert.Equal(48u, timers.Read32(0x14));

        clock.AdvanceTo(2500); // 2.5 µs = 120 ticks: two periods and 24 ticks into the third
        Assert.Equal((2u << 24) | 24u, timers.Read32(0x14));
        Assert.Equal((2u << 24) | PspHardwareTimers.Counting | 48u, timers.Read32(0x110));
    }

    [Fact]
    public void StatusReadConsumesThePeriodsTheReadOnlyViewDoesNot()
    {
        PspHardwareTimers timers = Timers(out PspHardwareClock clock, out _);
        Start(timers, 0, 48, 1, 1, PspHardwareTimers.Counting);
        clock.AdvanceTo(3000);
        Assert.Equal(3u << 24, timers.Read32(0x100) & 0xFF000000);
        Assert.Equal(3u << 24, timers.Read32(0x100) & 0xFF000000);
        Assert.Equal(3u << 24, timers.Read32(0x000) & 0xFF000000);
        Assert.Equal(0u, timers.Read32(0x000) & 0xFF000000);
        Assert.Equal(0u, timers.Read32(0x004) & 0xFF000000);
        clock.AdvanceTo(4000);
        Assert.Equal(1u << 24, timers.Read32(0x000) & 0xFF000000);
    }

    [Fact]
    public void OverflowByteSaturatesAt255()
    {
        PspHardwareTimers timers = Timers(out PspHardwareClock clock, out _);
        Start(timers, 2, 48, 1, 1, PspHardwareTimers.Counting);
        clock.AdvanceTo(1_000_000);
        Assert.Equal(0xFFu << 24, timers.Read32(0x20) & 0xFF000000);
    }

    [Fact]
    public void PrescalerDividesTheBaseClock()
    {
        PspHardwareTimers timers = Timers(out PspHardwareClock clock, out _);
        Start(timers, 0, 48, 1, 4, PspHardwareTimers.Counting); // 12 MHz: one period every 4 µs
        clock.AdvanceTo(3999);
        Assert.Equal(0u, timers.Read32(0x100) >> 24);
        clock.AdvanceTo(4000);
        Assert.Equal(1u, timers.Read32(0x100) >> 24);
        Assert.Equal(48u, timers.Read32(0x04) & 0xFFFFFF);
    }

    [Fact]
    public void ZeroPrescalerDoesNotCount()
    {
        PspHardwareTimers timers = Timers(out PspHardwareClock clock, out PspInterruptController interrupts);
        Start(timers, 0, 48, 0, 0, Status);
        clock.AdvanceTo(1_000_000);
        timers.Update();
        Assert.Equal(0u, timers.Read32(0x04));
        Assert.False(interrupts.Output);
        Assert.Equal(long.MaxValue, timers.NextEventNs);
    }

    [Fact]
    public void InterruptRisesAtEachPeriodUntilTheStatusRead()
    {
        PspHardwareTimers timers = Timers(out PspHardwareClock clock, out PspInterruptController interrupts);
        Start(timers, 3, 4800, 1, 1, Status); // 100 µs
        Assert.Equal(100_000, timers.NextEventNs);
        clock.AdvanceTo(99_999);
        timers.Update();
        Assert.False(interrupts.Output);
        clock.AdvanceTo(100_000);
        timers.Update();
        Assert.True(interrupts.Output);
        Assert.Equal(1u << PspInterrupt.SystemTimer3, interrupts.Read32(0x00));
        Assert.Equal(long.MaxValue, timers.NextEventNs);

        timers.Read32(0x100); // the view does not acknowledge
        Assert.True(interrupts.Output);
        timers.Read32(0x30);
        Assert.False(interrupts.Output);
        Assert.Equal(200_000, timers.NextEventNs);
    }

    [Fact]
    public void WithoutTheInterruptEnableTheLineStaysLow()
    {
        PspHardwareTimers timers = Timers(out PspHardwareClock clock, out PspInterruptController interrupts);
        Start(timers, 0, 48, 1, 1, PspHardwareTimers.Counting);
        clock.AdvanceTo(10_000);
        timers.Update();
        Assert.False(interrupts.Output);
        Assert.Equal(long.MaxValue, timers.NextEventNs);
    }

    [Fact]
    public void StoppingTheTimerDropsItsLineAndRestartingCountsAgain()
    {
        PspHardwareTimers timers = Timers(out PspHardwareClock clock, out PspInterruptController interrupts);
        Start(timers, 0, 48, 1, 1, Status);
        clock.AdvanceTo(1000);
        timers.Update();
        Assert.True(interrupts.Output);
        timers.Write32(0x00, PspHardwareTimers.InterruptEnable | 48);
        Assert.False(interrupts.Output);
        Assert.Equal(0u, timers.Read32(0x04));

        clock.AdvanceTo(1500);
        timers.Write32(0x00, Status | 48);
        Assert.Equal(2500, timers.NextEventNs);
        Assert.Equal(48u, timers.Read32(0x04));
    }

    [Fact]
    public void BitThirtyOneIsNotStored()
    {
        PspHardwareTimers timers = Timers(out _, out _);
        timers.Write32(0x00, PspHardwareTimers.ResetOverflow | 0x123);
        Assert.Equal(0x123u, timers.Read32(0x100));
    }

    [Fact]
    public void StandardHardwareHasTheTimersOnTheirLines()
    {
        PspHardware hardware = PspHardware.CreateStandard(new AllegrexTestBus());
        hardware.Interrupts.Write32(0x08, 1u << PspInterrupt.SystemTimer2);
        Assert.True(hardware.TryWrite32(0x1C500028, 1));
        Assert.True(hardware.TryWrite32(0x1C50002C, 1));
        Assert.True(hardware.TryWrite32(0x1C500020, Status | 480));
        Assert.Equal(10_000, hardware.NextEventNs);
        hardware.Advance(10_000);
        Assert.True(hardware.Interrupts.Output);
        hardware.Reset();
        Assert.False(hardware.Interrupts.Output);
        Assert.True(hardware.TryRead32(0x1C500120, out uint status));
        Assert.Equal(0u, status);
    }

    /// <summary>
    /// Timer 0 with a 100 µs cycle at 48 MHz (prescaler −1/−1 as the kernel writes it), its interrupt enabled in the
    /// controller, then halt in a loop. The handler reads the timer's status (the acknowledge, when
    /// <paramref name="acknowledge"/>), counts in data[0], keeps the status in data[1].
    /// </summary>
    private static PspHardwareRig TimerProgram(bool acknowledge)
    {
        uint[] code =
        [
            Lui(T0, 0xBC50),
            Addiu(T1, Zero, -1),
            Sw(T1, T0, 8),
            Sw(T1, T0, 12),
            .. PspHardwareRig.LoadImmediate(T1, PspHardwareTimers.ResetOverflow | Status | 4800),
            Sw(T1, T0, 0),
            Lui(T0, 0xBC30),
            Ori(T1, Zero, 1 << PspInterrupt.SystemTimer0),
            Sw(T1, T0, 8),
            Ori(T2, Zero, PspHardwareRig.InterruptsOn),
            Mtc0(T2, AllegrexCore.Cop0Status),
            .. PspHardwareRig.IdleLoop(),
        ];
        List<uint> handler = [Lui(K0, 0xBC50)];
        handler.Add(acknowledge ? Lw(K1, K0, 0) : Lw(K1, K0, 0x100));
        handler.Add(Lui(T2, (int)(PspHardwareRig.DataBase >> 16)));
        handler.Add(Lw(T1, T2, 0));
        handler.Add(Addiu(T1, T1, 1));
        handler.Add(Sw(T1, T2, 0));
        handler.Add(Sw(K1, T2, 4));
        handler.Add(Eret);
        return new PspHardwareRig(code, [.. handler]);
    }

    [Fact]
    public void MipsProgramTakesOneTimerInterruptPerPeriod()
    {
        PspHardwareRig rig = TimerProgram(acknowledge: true);
        rig.Run(1000);
        Assert.True(rig.Core.Halted);
        Assert.Equal(0u, rig.Data(0));

        rig.Run(10 * 100_000 / PspHardwareRig.NsPerInstruction); // ~1 ms of core time: ten periods
        Assert.InRange(rig.Data(0), 9u, 10u);
        Assert.Equal(1u << 24, rig.Data(1) & 0xFF000000);
        Assert.Equal(Status | 4800u, rig.Data(1) & 0x00FFFFFF);
        Assert.False(rig.Hardware.Interrupts.Output);
        Assert.True(rig.Core.Halted);
    }

    [Fact]
    public void WithoutTheStatusReadTheTimerInterruptIsTakenAgainAndAgain()
    {
        PspHardwareRig rig = TimerProgram(acknowledge: false);
        rig.Run(40_000);
        Assert.True(rig.Data(0) > 10);
        Assert.True(rig.Hardware.Interrupts.Output);
    }
}
