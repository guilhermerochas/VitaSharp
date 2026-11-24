using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Compat.Psp;
using static VitaSharp.Tests.AllegrexAsm;

namespace VitaSharp.Tests;

/// <summary>
/// The PSP system time (0xBC600000): the microsecond count, the alarm and its interrupt (Thread0), the alarm write as
/// the acknowledge, the wrap, and a MIPS program that waits on alarms the way the thread manager does.
/// </summary>
public sealed class PspSystemTimeTests
{
    private static PspSystemTime SystemTime(out PspHardwareClock clock, out PspInterruptController interrupts)
    {
        clock = new PspHardwareClock();
        interrupts = new PspInterruptController();
        interrupts.Write32(0x08, 1u << PspInterrupt.Thread0);
        return new PspSystemTime(clock, interrupts);
    }

    [Fact]
    public void CountsMicrosecondsOfTheCoreTime()
    {
        PspSystemTime time = SystemTime(out PspHardwareClock clock, out _);
        Assert.Equal(0u, time.Read32(0x00));
        clock.AdvanceTo(1999);
        Assert.Equal(1u, time.Read32(0x00));
        clock.AdvanceTo(5_000_000_000);
        Assert.Equal(5_000_000u, time.Read32(0x00));
    }

    [Fact]
    public void WritingTheCountMovesIt()
    {
        PspSystemTime time = SystemTime(out PspHardwareClock clock, out _);
        clock.AdvanceTo(10_000);
        time.Write32(0x00, 0xFFFFFFF0);
        Assert.Equal(0xFFFFFFF0u, time.Read32(0x00));
        clock.AdvanceTo(30_000);
        Assert.Equal(4u, time.Read32(0x00)); // wrapped
    }

    [Fact]
    public void AlarmRaisesTheLineAtItsTimeAndTheNextAlarmWriteAcknowledges()
    {
        PspSystemTime time = SystemTime(out PspHardwareClock clock, out PspInterruptController interrupts);
        Assert.Equal(long.MaxValue, time.NextEventNs);
        time.Write32(0x04, 250);
        Assert.Equal(250u, time.Read32(0x04));
        Assert.Equal(250_000, time.NextEventNs);
        clock.AdvanceTo(249_999);
        time.Update();
        Assert.False(interrupts.Output);
        clock.AdvanceTo(250_000);
        time.Update();
        Assert.True(interrupts.Output);
        Assert.Equal(1u << PspInterrupt.Thread0, interrupts.Read32(0x00));
        Assert.Equal(long.MaxValue, time.NextEventNs);

        // stays high until the alarm is written again
        clock.AdvanceTo(400_000);
        time.Update();
        Assert.True(interrupts.Output);
        time.Write32(0x04, 500);
        Assert.False(interrupts.Output);
        Assert.Equal(500_000, time.NextEventNs);
    }

    [Fact]
    public void AlarmAlreadyPassedFiresAtOnce()
    {
        PspSystemTime time = SystemTime(out PspHardwareClock clock, out PspInterruptController interrupts);
        clock.AdvanceTo(1_000_000);
        time.Write32(0x04, 999);
        Assert.True(interrupts.Output);
    }

    [Fact]
    public void AlarmAcrossTheWrapWaitsForTheWrap()
    {
        PspSystemTime time = SystemTime(out PspHardwareClock clock, out PspInterruptController interrupts);
        time.Write32(0x00, 0xFFFFFF00);
        time.Write32(0x04, 0x10);
        Assert.False(interrupts.Output);
        Assert.Equal(0x110 * 1000L, time.NextEventNs);
        clock.AdvanceTo(0x110 * 1000L);
        time.Update();
        Assert.True(interrupts.Output);
    }

    [Fact]
    public void OtherRegistersReadBackAndResetClearsAll()
    {
        PspSystemTime time = SystemTime(out PspHardwareClock clock, out PspInterruptController interrupts);
        time.Write32(0x08, 0x30);
        time.Write32(0x0C, 1);
        time.Write32(0x10, 7);
        Assert.Equal(0x30u, time.Read32(0x08));
        Assert.Equal(1u, time.Read32(0x0C));
        Assert.Equal(7u, time.Read32(0x10));
        time.Write32(0x04, 0);
        Assert.True(interrupts.Output);
        clock.AdvanceTo(5000);
        time.Reset();
        Assert.False(interrupts.Output);
        Assert.Equal(5u, time.Read32(0x00)); // the count follows the clock (PspHardware.Reset resets the clock)
        Assert.Equal(0u, time.Read32(0x08));
        Assert.Equal(long.MaxValue, time.NextEventNs);
    }

    /// <summary>
    /// Main: enables Thread0, sets the first alarm at count + 100 µs, halts in a loop. Handler: count the interrupt in
    /// data[0], keep the count in data[1], set the next alarm 100 µs later (the acknowledge, when
    /// <paramref name="rearm"/>), eret.
    /// </summary>
    private static PspHardwareRig AlarmProgram(bool rearm)
    {
        uint[] code =
        [
            Lui(T0, 0xBC30),
            .. PspHardwareRig.LoadImmediate(T1, 1u << PspInterrupt.Thread0),
            Sw(T1, T0, 8),
            Lui(T0, 0xBC60),
            Lw(T1, T0, 0),
            Addiu(T1, T1, 100),
            Sw(T1, T0, 4),
            Ori(T2, Zero, PspHardwareRig.InterruptsOn),
            Mtc0(T2, AllegrexCore.Cop0Status),
            .. PspHardwareRig.IdleLoop(),
        ];
        List<uint> handler = [Lui(K0, 0xBC60), Lw(K1, K0, 0)];
        if (rearm)
        {
            handler.Add(Lw(T1, K0, 4));
            handler.Add(Addiu(T1, T1, 100));
            handler.Add(Sw(T1, K0, 4));
        }

        handler.Add(Lui(T2, (int)(PspHardwareRig.DataBase >> 16)));
        handler.Add(Lw(T1, T2, 0));
        handler.Add(Addiu(T1, T1, 1));
        handler.Add(Sw(T1, T2, 0));
        handler.Add(Sw(K1, T2, 4));
        handler.Add(Eret);
        return new PspHardwareRig(code, [.. handler]);
    }

    [Fact]
    public void MipsProgramTakesOneAlarmInterruptPerAlarm()
    {
        PspHardwareRig rig = AlarmProgram(rearm: true);
        rig.Run(1_000_000 / PspHardwareRig.NsPerInstruction); // 1 ms of core time
        Assert.InRange(rig.Data(0), 9u, 10u);
        Assert.InRange(rig.Data(1), 100u * rig.Data(0), (100u * rig.Data(0)) + 5);
        Assert.True(rig.Core.Halted);
    }

    [Fact]
    public void WithoutTheNextAlarmWriteTheInterruptIsTakenAgainAndAgain()
    {
        PspHardwareRig rig = AlarmProgram(rearm: false);
        rig.Run(200_000 / PspHardwareRig.NsPerInstruction);
        Assert.True(rig.Data(0) > 20);
        Assert.False(rig.Core.Halted);
    }
}
