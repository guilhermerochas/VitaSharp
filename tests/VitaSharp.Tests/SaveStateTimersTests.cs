using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// What the guest-time scheduler has armed is part of a save state: after a load the sources the state had scheduled
/// fire at their guest times, and what the running session armed after the save no longer fires. A Systimer counting
/// with an interval raises its line again (it stayed silent after a load), the thread manager's alarm line is armed
/// for the loaded earliest alarm, the RTC alarm of the loaded clocks rings at its time even when the running session
/// had already rung it, and a kernel timer of the running session that the state does not have leaves the scheduler.
/// The loads are made in the same session, the worst case (the arms of the running session are all there). Guest time
/// is a controlled clock once the state is loaded (a save and a load pause and resume the emulation, which restarts
/// the clock); <see cref="SyntheticTitle"/> runs in the process.
/// </summary>
[Collection("guest")]
public sealed class SaveStateTimersTests : IDisposable
{
    private const uint LongRangeLine0 = 0x88, ThreadmgrTimerLine = 0x8D;
    private const ulong FarUs = 60_000_000;
    private readonly HleHarness _harness;
    private readonly Emulator _emu;
    private readonly Emulator _kernel;

    public SaveStateTimersTests(GuestMemoryFixture fixture)
    {
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "sstimers");
        _emu = _harness.Emu;
        _emu.LoadTitle(SyntheticTitle.Install(_harness.Root));
        _emu.Start();
        _kernel = _emu.Vita.KernelProcessFor(_emu);
    }

    public void Dispose()
    {
        _emu.Stop();
        _harness.Dispose();
    }

    private string StatePath => Path.Combine(_harness.Root, "state.vss");

    private T KernelModule<T>() => _kernel.Hle.Modules.OfType<T>().First();

    private int KernelCall(object module, string name, params object[] args)
    {
        using (ProcessScope.Enter(_kernel))
        {
            return Call(module, name, args);
        }
    }

    private uint KernelAlloc(uint size, string name)
    {
        uint address;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            address = _emu.Memory.Alloc(size, name, MemoryRegion.Kernel);
        }

        GuestMemory.Fill(address, 0, size);
        return address;
    }

    /// <summary>ARM code that stores r0 and r1 after the word at <paramref name="target"/>, then adds 1 to it; returns 0.</summary>
    private uint Counter(uint target)
    {
        uint code = KernelAlloc(48, "test:counter code");
        uint[] words =
        [
            0xE59FC01C, // ldr r12, [pc, #28]  (the word at +36)
            0xE58C0004, // str r0, [r12, #4]
            0xE58C1008, // str r1, [r12, #8]
            0xE59C2000, // ldr r2, [r12]
            0xE2822001, // add r2, r2, #1
            0xE58C2000, // str r2, [r12]
            0xE3A00000, // mov r0, #0
            0xE12FFF1E, // bx lr
            0xE12FFF1E,
            target,
        ];
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(code + (uint)i * 4, words[i]);
        }

        return code;
    }

    private ulong NowUs => _kernel.Kernel.ProcessTimeUs;

    /// <summary>How far ahead of the stopped clock a paced guest thread reads guest time (its CPU position; a pacing
    /// window is about 1 ms at the default clock rate).</summary>
    private const long ReadAheadUs = 2_000;

    /// <summary>What the restored title's thread does while the clock is stopped (on macOS CI it had, and the alarm due
    /// at the advanced time never fired): a paced thread reads guest time at its own CPU position, ahead of the
    /// stopped clock, and every later reading is at least that (<see cref="GuestClock.Observed"/>). Made here so the
    /// advance is checked against it every run.</summary>
    private static void ReadAheadAsAPacedThread()
    {
        GuestClock.Observed(GuestClock.Ticks + GuestClock.FromMicroseconds(ReadAheadUs));
    }

    /// <summary>A long-range Systimer counting with an interval of a minute raises its line after a load at the saved
    /// time; a second timer started after the save is not armed after the load.</summary>
    [Fact]
    public void ACountingSystimerRaisesItsLineAgainAfterALoad()
    {
        SystemServicesVsh services = KernelModule<SystemServicesVsh>();
        uint seen = KernelAlloc(16, "test:systimer seen");
        uint id = (uint)KernelCall(services, "ksceKernelSysTimerAlloc", 1u);
        Assert.Equal(0, KernelCall(services, "ksceKernelSysTimerSetInterval", id, 0u, (uint)FarUs, 0u));
        Assert.Equal(0, KernelCall(services, "ksceKernelSysTimerSetHandler", id, Counter(seen), 0xFu, 0x77u));
        Assert.Equal(0, KernelCall(services, "ksceKernelSysTimerStartCount", id));
        ulong deadline = services.IntrSchedule(LongRangeLine0).Deadline ?? throw new Xunit.Sdk.XunitException("the timer's line is not armed");

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        Assert.Equal(0, KernelCall(services, "ksceKernelSysTimerStopCount", id));
        uint second = (uint)KernelCall(services, "ksceKernelSysTimerAlloc", 1u);
        Assert.Equal(0, KernelCall(services, "ksceKernelSysTimerSetInterval", second, 0u, 1000u, 0u));
        Assert.Equal(0, KernelCall(services, "ksceKernelSysTimerStartCount", second));
        Assert.NotNull(services.IntrSchedule(LongRangeLine0 + 1).Deadline);

        SaveStates.Load(_emu, StatePath);
        using var clock = ControlledGuestClock.Stop();
        Assert.Equal(deadline, services.IntrSchedule(LongRangeLine0).Deadline);
        Assert.Null(services.IntrSchedule(LongRangeLine0 + 1).Deadline);
        Assert.Equal(0u, GuestMemory.R32(seen));
        Assert.True(deadline > NowUs, "the loaded time is past the deadline");
        ReadAheadAsAPacedThread();
        clock.AdvanceTo(deadline);
        Until(() => GuestMemory.R32(seen) == 1, "the timer's handler after the load");
        Assert.Equal(id, GuestMemory.R32(seen + 4));
        Assert.Equal(0x77u, GuestMemory.R32(seen + 8));
        Assert.Equal(deadline + FarUs, services.IntrSchedule(LongRangeLine0).Deadline);
    }

    /// <summary>A thread manager alarm pending at the save and cancelled after it is armed again by the load (its timer
    /// line), and its handler runs at its time.</summary>
    [Fact]
    public void APendingKernelAlarmFiresAfterALoad()
    {
        SystemServicesVsh services = KernelModule<SystemServicesVsh>();
        ThreadmgrKernel threadmgr = KernelModule<ThreadmgrKernel>();
        uint seen = KernelAlloc(16, "test:alarm seen");
        uint name = KernelAlloc(16, "test:alarm name");
        GuestMemory.WriteCString(name, "VsTestAlarm");
        int alarm = KernelCall(threadmgr, "ksceKernelRegisterTimer", name, (uint)FarUs, Counter(seen), 0x55u);
        Assert.True(alarm > 0, $"ksceKernelRegisterTimer: 0x{alarm:X8}");
        ulong due = threadmgr.AlarmDue(alarm) ?? throw new Xunit.Sdk.XunitException("the alarm is not pending");
        Assert.Equal(due, services.IntrSchedule(ThreadmgrTimerLine).Deadline);

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        Assert.Equal(0, KernelCall(threadmgr, "NID_44CCF310", alarm));
        Assert.Null(services.IntrSchedule(ThreadmgrTimerLine).Deadline);

        SaveStates.Load(_emu, StatePath);
        using var clock = ControlledGuestClock.Stop();
        Assert.Equal(due, services.IntrSchedule(ThreadmgrTimerLine).Deadline);
        Assert.True(due > NowUs, "the loaded time is past the alarm");
        ReadAheadAsAPacedThread();
        clock.AdvanceTo(due);
        Until(() => GuestMemory.R32(seen) == 1, "the alarm's handler after the load");
        Assert.Equal(0x55u, GuestMemory.R32(seen + 4));
    }

    /// <summary>The RTC alarm set before the save rings after the load at its time, also when the running session had
    /// rung it after the save (the scheduler's entry remembered that ring).</summary>
    [Fact]
    public void TheRtcAlarmRingsAgainAfterALoad()
    {
        RtcKernel rtc = KernelModule<RtcKernel>();
        SysconChip chip = _emu.Vita.Syscon;
        const int AlarmBit = 3;
        uint tick = KernelAlloc(8, "test:alarm tick");
        ulong alarmTick = RtcKernel.ClockTick(_emu.Vita.RtcClocks, RtcClocks.Main) + FarUs / 2;
        GuestMemory.Write(tick, alarmTick);
        Assert.Equal(0, KernelCall(rtc, "ksceRtcSetAlarmTick", tick, 0u));
        Assert.False(chip.StatusBit(AlarmBit));

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        using (var clock = ControlledGuestClock.Stop())
        {
            clock.Advance((long)FarUs);
            Until(() => chip.StatusBit(AlarmBit), "the alarm rings after the save");
        }

        SaveStates.Load(_emu, StatePath);
        Assert.False(chip.StatusBit(AlarmBit));
        using (var clock = ControlledGuestClock.Stop())
        {
            clock.Advance((long)FarUs);
            Until(() => chip.StatusBit(AlarmBit), "the alarm rings after the load");
        }
    }

    /// <summary>A kernel timer of the running session with an armed event leaves the scheduler once a load replaced
    /// the object table (the state's own timers are new objects).</summary>
    [Fact]
    public void ARunningSessionsTimerStopsAfterALoad()
    {
        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        var timer = new TimerObject { Name = "VsTestOldTimer", Clock = () => _emu.Kernel.ProcessTimeUs };
        using (ProcessScope.Enter(_emu))
        {
            _emu.Kernel.Objects.Register(timer);
        }

        Assert.Equal(0, timer.SetEvent(0, FarUs, repeat: true));
        Assert.Equal(0, timer.Start());
        Assert.True(timer.Armed);

        SaveStates.Load(_emu, StatePath);
        Until(() => !timer.Armed, "the old timer leaves the scheduler");
        Assert.False(timer.HasPending);
    }
}
