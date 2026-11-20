using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader.Self;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The POWER button and the power callbacks as ScePower 3.74 does them (os0:kd/bootimage.skprx: the button thread,
/// scePowerRegisterCallback / scePowerUnregisterCallback and the notification of every registrant), with standby and
/// power off. Before this, scePowerRegisterCallback kept nothing, the POWER bit was 0x4000000 instead of the
/// controller's 0x40000000 and no host control sent it: nothing reached the shell's power menu or standby.
/// </summary>
[Collection("guest")]
public sealed class PowerButtonTests(GuestMemoryFixture fixture) : IDisposable
{
    private const long Second = 1_000_000;
    private readonly HleHarness _harness = new(fixture, "power-button");

    public void Dispose()
    {
        _harness.Emu.Vita.Power.Resume(); // never leave the (static) emulation paused for the next test
        _harness.Dispose();
    }

    // ---------------------------------------------------------------- the button thread's decisions

    private static uint Sample(PowerButtonTracker tracker, VitaButtons buttons, long nowUs) => tracker.Update(buttons, nowUs, out _);

    [Fact]
    public void ThePowerBitIsTheControllersOne()
    {
        // SCE_CTRL_POWER (vita-headers psp2common/ctrl.h); ScePower's button thread tests 0x40000000
        Assert.Equal(0x40000000u, (uint)VitaButtons.PowerButton);
        Assert.Equal(VitaButtons.PowerButton, Core.Input.VitaControls.Button(Core.Input.VitaControl.Power));
    }

    [Fact]
    public void AShortPressIsAPressOnTheRelease()
    {
        var tracker = new PowerButtonTracker();
        Assert.Equal(0u, Sample(tracker, VitaButtons.PowerButton, 0));
        Assert.Equal(0u, Sample(tracker, VitaButtons.PowerButton, Second));
        Assert.Equal(PowerEvents.ButtonPowerPress, Sample(tracker, VitaButtons.None, Second + 990_000));
        Assert.Equal(0u, Sample(tracker, VitaButtons.None, 3 * Second));
    }

    [Fact]
    public void AHoldIsAHoldAtThePushTimeAndNothingOnTheRelease()
    {
        var tracker = new PowerButtonTracker();
        Sample(tracker, VitaButtons.PowerButton, 0);
        Assert.Equal(0u, Sample(tracker, VitaButtons.PowerButton, 2 * Second - 1));
        Assert.Equal(PowerEvents.ButtonPowerHold, Sample(tracker, VitaButtons.PowerButton, 2 * Second));
        Assert.Equal(0u, Sample(tracker, VitaButtons.PowerButton, 3 * Second));
        Assert.Equal(0u, Sample(tracker, VitaButtons.None, 3 * Second + 1));
    }

    [Theory]
    [InlineData(2 * Second, 4 * Second)]   // the default: off 4 s after the press
    [InlineData(5 * Second, 9 * Second)]   // a push time of 4 s or more: off 4 s after the hold
    [InlineData(3 * Second, 4 * Second)]
    public void HeldOnTheSystemTurnsOff(long pushTimeUs, long offAtUs)
    {
        var tracker = new PowerButtonTracker { StandbyPushTimeUs = pushTimeUs };
        Sample(tracker, VitaButtons.PowerButton, 0);
        Assert.Equal(PowerEvents.ButtonPowerHold, Sample(tracker, VitaButtons.PowerButton, pushTimeUs));
        tracker.Update(VitaButtons.PowerButton, offAtUs - 1, out bool early);
        Assert.False(early);
        tracker.Update(VitaButtons.PowerButton, offAtUs, out bool off);
        Assert.True(off);
        tracker.Update(VitaButtons.PowerButton, offAtUs + Second, out bool again);
        Assert.False(again);
    }

    [Fact]
    public void PsAndPowerTogetherAreTheirOwnEvent()
    {
        var tracker = new PowerButtonTracker();
        Sample(tracker, VitaButtons.PowerButton, 0);
        Assert.Equal(PowerEvents.ButtonPsPowerPress, Sample(tracker, VitaButtons.PowerButton | VitaButtons.PsButton, Second / 2));
        // nothing else (no press, no hold, no power off) until both are released
        Assert.Equal(0u, Sample(tracker, VitaButtons.PowerButton, 3 * Second));
        tracker.Update(VitaButtons.PowerButton, 10 * Second, out bool off);
        Assert.False(off);
        Assert.Equal(0u, Sample(tracker, VitaButtons.None, 11 * Second));
        Assert.Equal(PowerEvents.ButtonPsPowerPress, Sample(tracker, VitaButtons.PowerButton | VitaButtons.PsButton, 12 * Second));
    }

    [Fact]
    public void TheWakingPressIsNotAPress()
    {
        var tracker = new PowerButtonTracker();
        tracker.IgnoreUntilReleased();
        Assert.Equal(0u, Sample(tracker, VitaButtons.PowerButton, 0));
        Assert.Equal(0u, Sample(tracker, VitaButtons.None, Second / 10));
        Sample(tracker, VitaButtons.PowerButton, Second);
        Assert.Equal(PowerEvents.ButtonPowerPress, Sample(tracker, VitaButtons.None, Second + Second / 10));
    }

    // ---------------------------------------------------------------- the callbacks

    private int[] CreateCallbacks(int count)
    {
        ThreadMgr threadmgr = _harness.Module<ThreadMgr>();
        int[] uids = new int[count];
        _harness.OnGuest(_ =>
        {
            for (int i = 0; i < count; i++)
            {
                uids[i] = Call(threadmgr, "sceKernelCreateCallback", "power-cb", 0u, 0x81000001u, 0u);
            }
        });
        Assert.All(uids, uid => Assert.True(uid > 0, $"0x{uid:X8}"));
        return uids;
    }

    private Callback CallbackObject(int uid) => _harness.Emu.Kernel.Objects.Get<Callback>(uid)!;

    private void RunAsGame() => _harness.Emu.Access = new ProgramAccess(0x210000101CC404D8UL, new byte[ProgramAccess.CapabilitySize], false);

    private void RunAsSystemProgram()
    {
        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[0] = 0x40;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000025UL, capability, false);
        Assert.True(_harness.Emu.Access.IsSystemProgram);
    }

    /// <summary>The callback ran (the guest consumed its notifications).</summary>
    private static void Consume(Callback callback) => callback.NotifyCount = 0;

    [Fact]
    public void RegisteringNotifiesTheStateAtOnceAndTheErrorsAreScePowers()
    {
        _harness.Emu.Running = true;
        RunAsGame();
        SystemMisc power = _harness.Module<SystemMisc>();
        int[] callbacks = CreateCallbacks(2);

        Assert.Equal(0, Call(power, "scePowerRegisterCallback", callbacks[0]));
        Callback callback = CallbackObject(callbacks[0]);
        Assert.Equal(1, callback.NotifyCount);
        Assert.Equal((int)(PowerEvents.BatteryOnline), callback.NotifyArg);

        Assert.Equal(PowerCallbacks.ErrAlreadyRegistered, Call(power, "scePowerRegisterCallback", callbacks[0]));
        Assert.Equal(PowerCallbacks.ErrNotCallback, Call(power, "scePowerRegisterCallback", 0x7FFF0001));
        Assert.Equal(PowerCallbacks.ErrNotCallback, Call(power, "scePowerUnregisterCallback", 0x7FFF0001));
        Assert.Equal(PowerCallbacks.ErrNotRegistered, Call(power, "scePowerUnregisterCallback", callbacks[1]));

        Assert.Equal(0, Call(power, "scePowerUnregisterCallback", callbacks[0]));
        Consume(callback);
        _harness.Emu.Vita.Power.Callbacks.Post(0, 0, PowerEvents.SystemResume);
        Assert.Equal(0, callback.NotifyCount);
        Assert.Equal(PowerCallbacks.ErrNotRegistered, Call(power, "scePowerUnregisterCallback", callbacks[0]));
    }

    [Fact]
    public void ButtonsReachOnlySystemProgramsAndUnconsumedEventsAccumulate()
    {
        _harness.Emu.Running = true;
        SystemMisc power = _harness.Module<SystemMisc>();
        int[] callbacks = CreateCallbacks(2);
        RunAsSystemProgram();
        Assert.Equal(0, Call(power, "scePowerRegisterCallback", callbacks[0]));
        RunAsGame();
        Assert.Equal(0, Call(power, "scePowerRegisterCallback", callbacks[1]));
        Callback shell = CallbackObject(callbacks[0]);
        Callback game = CallbackObject(callbacks[1]);
        Consume(shell);
        Consume(game);
        uint state = PowerEvents.BatteryOnline;

        PowerCallbacks registry = _harness.Emu.Vita.Power.Callbacks;
        registry.Post(0, 0, PowerEvents.ButtonPowerHold);
        Assert.Equal((int)(state | PowerEvents.ButtonPowerHold), shell.NotifyArg);
        Assert.Equal((int)state, game.NotifyArg);

        // the shell's callback has not run yet: the next notification still carries the hold
        registry.Post(0, 0, PowerEvents.ButtonPowerPress);
        Assert.Equal(unchecked((int)(state | PowerEvents.ButtonPowerHold | PowerEvents.ButtonPowerPress)), shell.NotifyArg);
        Consume(shell);
        registry.Post(0, 0, PowerEvents.SystemResume);
        Assert.Equal((int)(state | PowerEvents.SystemResume), shell.NotifyArg);
        Assert.Equal((int)(state | PowerEvents.SystemResume), game.NotifyArg);
    }

    [Fact]
    public void WithoutAShellAPressIsStandbyAndTheNextPressWakesTheSystem()
    {
        _harness.Emu.Running = true;
        RunAsGame();
        SystemPower system = _harness.Emu.Vita.Power;
        SystemMisc power = _harness.Module<SystemMisc>();
        int game = CreateCallbacks(1)[0];
        Assert.Equal(0, Call(power, "scePowerRegisterCallback", game));
        Callback callback = CallbackObject(game);
        Consume(callback);

        system.OnButtons(VitaButtons.PowerButton, 0);
        system.OnButtons(VitaButtons.None, Second / 5);
        Assert.True(system.Suspended);
        Assert.True(EmulationPause.IsPaused);
        // every change notifies every registrant (the button, the suspend), but an application's mask leaves both out
        Assert.Equal(2, callback.NotifyCount);
        Assert.Equal((int)(PowerEvents.BatteryOnline), callback.NotifyArg);

        system.OnButtons(VitaButtons.PowerButton, 10 * Second);
        Assert.False(system.Suspended);
        Assert.False(EmulationPause.IsPaused);
        // resuming (a state bit), then resumed (an event)
        Assert.Equal(4, callback.NotifyCount);
        Assert.Equal((int)(PowerEvents.BatteryOnline | PowerEvents.SystemResume), callback.NotifyArg);
        // the waking press does not put the system back in standby on its release
        system.OnButtons(VitaButtons.None, 10 * Second + Second / 5);
        Assert.False(system.Suspended);
    }

    [Fact]
    public void WithTheShellThePressIsTheShellsToHandle()
    {
        _harness.Emu.Running = true;
        RunAsSystemProgram();
        SystemPower system = _harness.Emu.Vita.Power;
        int shell = CreateCallbacks(1)[0];
        Assert.Equal(0, Call(_harness.Module<SystemMisc>(), "scePowerRegisterCallback", shell));
        Callback callback = CallbackObject(shell);
        Consume(callback);

        system.OnButtons(VitaButtons.PowerButton, 0);
        system.OnButtons(VitaButtons.None, Second / 5);
        Assert.False(system.Suspended);
        Assert.Equal(unchecked((int)(PowerEvents.BatteryOnline | PowerEvents.ButtonPowerPress)), callback.NotifyArg);

        // the shell answers with scePowerRequestSuspend: standby, the shell told of the suspend
        Assert.Equal(0, Call(_harness.Module<SystemMisc>(), "scePowerRequestSuspend"));
        Assert.True(system.Suspended);
        Assert.NotEqual(0, callback.NotifyArg & (int)PowerEvents.SystemSuspend);
    }

    [Fact]
    public void PowerOffEndsTheSessionOnce()
    {
        _harness.Emu.Running = true;
        SystemPower system = _harness.Emu.Vita.Power;
        int raised = 0;
        system.PoweredOff += () => raised++;

        // held for 4 s: the kernel turns the system off
        system.OnButtons(VitaButtons.PowerButton, 0);
        system.OnButtons(VitaButtons.PowerButton, 2 * Second);
        system.OnButtons(VitaButtons.PowerButton, 4 * Second);
        Assert.Equal(1, raised);
        // the shell's Power Off (scePowerRequestStandby) after it changes nothing
        Assert.Equal(0, Call(_harness.Module<SystemMisc>(), "scePowerRequestStandby"));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void TheShellsPowerOffIsScePowerRequestStandby()
    {
        _harness.Emu.Running = true;
        int raised = 0;
        _harness.Emu.Vita.Power.PoweredOff += () => raised++;
        Assert.Equal(0, Call(_harness.Module<SystemMisc>(), "scePowerRequestStandby"));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ThePushTimeIsSetBySystemProgramsOnly()
    {
        VshBridge bridge = _harness.Module<VshBridge>();
        _harness.Emu.IsSystemProgram = false;
        Assert.Equal(unchecked((int)0x80010058), Call(bridge, "vshPowerSetStandbyButtonPushTime", 5_000_000));
        Assert.Equal(PowerButtonTracker.DefaultStandbyPushTimeUs, _harness.Emu.Vita.Power.Button.StandbyPushTimeUs);
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(0, Call(bridge, "vshPowerSetStandbyButtonPushTime", 5_000_000));
        Assert.Equal(5_000_000, _harness.Emu.Vita.Power.Button.StandbyPushTimeUs);
    }
}
