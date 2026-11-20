using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The thread manager's alarms as FW 3.74 threadmgr has them (decompiled, study only): ksceKernelRegisterTimer and its
/// 64-bit twin make an alarm due after the delay, the timer interrupt (line 0x8D) calls its handler with the argument
/// in interrupt context at that guest time, a handler's non-zero result fires it again that much later (at least 200
/// us after the time it was due) and 0 ends it; cancelling ends one that has not fired. Guest time is the test's
/// (<see cref="ControlledGuestClock"/>) and the line fires only when the test dispatches it.
/// </summary>
[Collection("guest")]
public sealed class KernelAlarmTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int IllegalAddr = unchecked((int)0x80020006), UnknownAlarmId = unchecked((int)0x800280C1);

    private const uint TimerLine = 0x8D;

    private readonly HleHarness _harness = new(fixture, "kalarm");

    public void Dispose() => _harness.Dispose();

    private ThreadmgrKernel Threadmgr => _harness.Module<ThreadmgrKernel>();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    /// <summary>ARM code: r1 = *r0 + 1, *r0 = r1, r0 = r0[1] (the handler's result), return.</summary>
    private uint CountingHandler()
    {
        uint code = _harness.Alloc(0x20);
        GuestMemory.W32(code, 0xE5901000); // ldr r1, [r0]
        GuestMemory.W32(code + 4, 0xE2811001); // add r1, r1, #1
        GuestMemory.W32(code + 8, 0xE5801000); // str r1, [r0]
        GuestMemory.W32(code + 12, 0xE5900004); // ldr r0, [r0, #4]
        GuestMemory.W32(code + 16, 0xE12FFF1E); // bx lr
        return code;
    }

    /// <summary>An argument block: the call count, then what the handler answers.</summary>
    private uint Argument(uint result)
    {
        uint argument = _harness.Alloc(8);
        GuestMemory.W32(argument + 4, result);
        return argument;
    }

    private int Dispatch(ulong nowUs)
    {
        int fired = 0;
        _harness.OnGuest(_ => fired = (int)Invoke(Services, "IntrDispatchDue", nowUs)!);
        return fired;
    }

    private ulong? Deadline() => (((ulong? Deadline, bool Dispatcher))Invoke(Services, "IntrSchedule", TimerLine)!).Deadline;

    [Fact]
    public void AnAlarmFiresAtItsTimeAndAZeroResultEndsIt()
    {
        using var clock = ControlledGuestClock.Stop();
        Invoke(Services, "IntrDispatchByHand");
        uint argument = Argument(0);
        ulong start = _harness.Emu.Kernel.ProcessTimeUs;
        int uid = Call(Threadmgr, "ksceKernelRegisterTimer", _harness.Str("kalarm"), 1000u, CountingHandler(), argument);
        Assert.True(uid > 0);
        Assert.Equal("kalarm", _harness.Emu.Kernel.Objects.Get<KernelData>(uid)!.Name);
        Assert.Equal(start + 1000, Deadline());
        // not yet due
        Dispatch(start + 999);
        Assert.Equal(0u, GuestMemory.R32(argument));
        clock.Advance(1000);
        Dispatch(start + 1000);
        Assert.Equal(1u, GuestMemory.R32(argument));
        Assert.Null(_harness.Emu.Kernel.Objects.Get<KernelData>(uid));
        Assert.Null(Deadline());
        Assert.Equal(UnknownAlarmId, Call(Threadmgr, "NID_44CCF310", uid));
    }

    [Fact]
    public void ANonZeroResultFiresItAgainFromTheTimeItWasDue()
    {
        using var clock = ControlledGuestClock.Stop();
        Invoke(Services, "IntrDispatchByHand");
        uint argument = Argument(50); // below the least delay: 200
        uint delay = _harness.Alloc(8);
        GuestMemory.Write<ulong>(delay, 500);
        ulong start = _harness.Emu.Kernel.ProcessTimeUs;
        int uid = Call(Threadmgr, "NID_10517330", _harness.Str("kalarm64"), delay, CountingHandler(), argument);
        clock.Advance(600);
        Dispatch(start + 600);
        Assert.Equal(1u, GuestMemory.R32(argument));
        Assert.Equal(start + 500 + 200, (ulong?)Invoke(Threadmgr, "AlarmDue", uid));
        Assert.Equal(start + 700, Deadline());
        GuestMemory.W32(argument + 4, 1000);
        clock.Advance(100);
        Dispatch(start + 700);
        Assert.Equal(2u, GuestMemory.R32(argument));
        Assert.Equal(start + 1700, (ulong?)Invoke(Threadmgr, "AlarmDue", uid));
        Assert.Equal(0, Call(Threadmgr, "NID_44CCF310", uid));
        Assert.Null((ulong?)Invoke(Threadmgr, "AlarmDue", uid));
        Assert.Null(Deadline());
    }

    [Fact]
    public void AlarmsFireEarliestFirstAndACancelledOneNever()
    {
        using var clock = ControlledGuestClock.Stop();
        Invoke(Services, "IntrDispatchByHand");
        uint handler = CountingHandler();
        uint late = Argument(0);
        uint early = Argument(0);
        uint cancelled = Argument(0);
        ulong start = _harness.Emu.Kernel.ProcessTimeUs;
        Call(Threadmgr, "ksceKernelRegisterTimer", _harness.Str("late"), 3000u, handler, late);
        Call(Threadmgr, "ksceKernelRegisterTimer", _harness.Str("early"), 1000u, handler, early);
        int gone = Call(Threadmgr, "ksceKernelRegisterTimer", _harness.Str("gone"), 2000u, handler, cancelled);
        Assert.Equal(start + 1000, Deadline());
        Assert.Equal(0, Call(Threadmgr, "NID_44CCF310", gone));
        clock.Advance(1500);
        Dispatch(start + 1500);
        Assert.Equal(1u, GuestMemory.R32(early));
        Assert.Equal(0u, GuestMemory.R32(late));
        Assert.Equal(start + 3000, Deadline());
        clock.Advance(2000);
        Dispatch(start + 3500);
        Assert.Equal(1u, GuestMemory.R32(late));
        Assert.Equal(0u, GuestMemory.R32(cancelled));
    }

    [Fact]
    public void TheCallsCheckTheirPointers()
    {
        Assert.Equal(IllegalAddr, Call(Threadmgr, "ksceKernelRegisterTimer", 0u, 1000u, 0x81000001u, 0u));
        Assert.Equal(IllegalAddr, Call(Threadmgr, "ksceKernelRegisterTimer", _harness.Str("n"), 1000u, 0u, 0u));
        Assert.Equal(IllegalAddr, Call(Threadmgr, "NID_10517330", _harness.Str("n"), 0u, 0x81000001u, 0u));
        Assert.Equal(UnknownAlarmId, Call(Threadmgr, "NID_44CCF310", 0x7FFF0001));
    }
}
