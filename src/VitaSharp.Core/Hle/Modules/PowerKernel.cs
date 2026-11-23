using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePowerForDriver, the kernel side of ScePower (FW 3.74 power.skprx in the boot image, read in the module, study
/// only), over the system's power state (<see cref="ScePowerState"/>) and the syscon chip (battery, AC adapter, event
/// bits). The exports that are also user-mode ScePower syscalls (same NIDs: the battery, the clocks, the callbacks,
/// the requests) are bound to SystemMisc's ScePower functions, which read the same state.
/// <para>What is not emulated: the clock generators and voltages (a clock set is checked, rounded to the steps the
/// module uses and kept, the emulated hardware runs as it does), the power sequences (a soft reset is logged; a
/// shutdown or hibernation turns the system off). The idle timers' callbacks run at their times (PowerKernel.Idle.cs).</para>
/// </summary>
public sealed partial class PowerKernel : HleModule
{
    /// <summary>ScePower: an argument out of range; a slot already taken.</summary>
    const int POWER_ERR_INVALID = unchecked((int)0x802B0000), POWER_ERR_BUSY = unchecked((int)0x802B0001);

    /// <summary>The id ScePower's per-process exports take for the system itself (its idle timer 0).</summary>
    const int SystemProcess = 0x10021;

    /// <summary>The process power flags (the processmgr word +0x38 the module sets): two requests (bits 0 and 1)
    /// that, with a GPU at 166 MHz or more, cap the display brightness at 0xC4EC.</summary>
    const uint ProcessFlagsBrightnessCap = 3;

    ScePowerState State => Emu.Vita.PowerState;

    SysconChip Chip => Emu.Vita.Syscon;

    SystemMisc Misc => Emu.Hle.Modules.OfType<SystemMisc>().First();

    SysconKernel Syscon => Emu.Hle.Modules.OfType<SysconKernel>().First();

    static long NowUs => GuestClock.Microseconds;

    // ------------------------------------------------------------------ the module's words

    /// <summary>NID_2B51FE2F(): the wireless is active.</summary>
    [Hle(Nid = 0x2B51FE2F, Library = "ScePowerForDriver")]
    int NID_2B51FE2F() => State.WlanActive ? 1 : 0;

    /// <summary>kscePowerWlanActivate(): 0.</summary>
    [Hle(Nid = 0x6D2CA84B, Library = "ScePowerForDriver")]
    int kscePowerWlanActivate()
    {
        State.WlanActive = true;
        return 0;
    }

    /// <summary>kscePowerWlanDeactivate(): 0.</summary>
    [Hle(Nid = 0x23BB0A60, Library = "ScePowerForDriver")]
    int kscePowerWlanDeactivate()
    {
        State.WlanActive = false;
        return 0;
    }

    /// <summary>kscePowerGetResumeCount(): how many times the system came back from standby.</summary>
    [Hle(Nid = 0x0074EF9B, Library = "ScePowerForDriver", ReturnsValue = true)]
    uint kscePowerGetResumeCount() => State.ResumeCount;

    /// <summary>kscePowerGetWakeupFactor(): what woke the system last (0: it never slept).</summary>
    [Hle(Nid = 0x9F26222A, Library = "ScePowerForDriver", ReturnsValue = true)]
    uint kscePowerGetWakeupFactor() => State.WakeupFactor;

    /// <summary>kscePowerRequestResume(): kept for the main thread. 0.</summary>
    [Hle(Nid = 0x02DB1035, Library = "ScePowerForDriver")]
    int kscePowerRequestResume()
    {
        State.ResumeRequested = true;
        return 0;
    }

    /// <summary>kscePowerSetCallbackState(state): 0.</summary>
    [Hle(Nid = 0xA6FF5997, Library = "ScePowerForDriver")]
    int kscePowerSetCallbackState(uint state)
    {
        State.CallbackState = state;
        return 0;
    }

    /// <summary>kscePowerGetPowerSwMode(): the two modes or'ed.</summary>
    [Hle(Nid = 0x165CE085, Library = "ScePowerForDriver", ReturnsValue = true)]
    uint kscePowerGetPowerSwMode() => State.PowerSwMode | State.PowerSwMode2;

    /// <summary>kscePowerSetPowerSwMode(mode): bits 0..2 only, else 0x802B0000.</summary>
    [Hle(Nid = 0xC1853BA7, Library = "ScePowerForDriver")]
    int kscePowerSetPowerSwMode(uint mode)
    {
        if ((mode & ~7u) != 0)
        {
            return POWER_ERR_INVALID;
        }

        State.PowerSwMode = mode;
        return 0;
    }

    /// <summary>kscePowerSetPowerSwMode2(mode): the second mode word, bits 0..2.</summary>
    [Hle(Nid = 0x0CD21B1F, Library = "ScePowerForDriver")]
    int kscePowerSetPowerSwMode2(uint mode)
    {
        if ((mode & ~7u) != 0)
        {
            return POWER_ERR_INVALID;
        }

        State.PowerSwMode2 = mode;
        return 0;
    }

    /// <summary>kscePowerSetStandbyButtonPushTime(µs): how long POWER is held for the standby menu (the button
    /// tracker uses it). 0.</summary>
    [Hle(Nid = 0x675A84ED, Library = "ScePowerForDriver")]
    int kscePowerSetStandbyButtonPushTime(uint time)
    {
        Emu.Vita.Power.Button.StandbyPushTimeUs = time;
        return 0;
    }

    /// <summary>kscePowerSetPsButtonPushTime(µs): 0.</summary>
    [Hle(Nid = 0xCF8F0529, Library = "ScePowerForDriver")]
    int kscePowerSetPsButtonPushTime(uint time)
    {
        State.PsButtonPushTimeUs = time;
        return 0;
    }

    /// <summary>kscePowerSetProcessIdleCallback(callback, argument): 0.</summary>
    [Hle(Nid = 0x0856FD0A, Library = "ScePowerForDriver")]
    int kscePowerSetProcessIdleCallback(uint callback, uint argument)
    {
        State.ProcessIdleCallback = callback;
        State.ProcessIdleArgument = argument;
        return 0;
    }

    /// <summary>NID_FE35B73D(a, b): keeps the pair. 0.</summary>
    [Hle(Nid = 0xFE35B73D, Library = "ScePowerForDriver")]
    int NID_FE35B73D(uint a, uint b)
    {
        State.Words[0] = a;
        State.Words[1] = b;
        return 0;
    }

    /// <summary>NID_711C68DF(a, b, c, d): keeps the four words. 0.</summary>
    [Hle(Nid = 0x711C68DF, Library = "ScePowerForDriver")]
    int NID_711C68DF(uint a, uint b, uint c, uint d)
    {
        State.Words[2] = a;
        State.Words[3] = b;
        State.Words[4] = c;
        State.Words[5] = d;
        return 0;
    }

    /// <summary>NID_38415146(value): keeps the value (and wakes the battery thread). 0.</summary>
    [Hle(Nid = 0x38415146, Library = "ScePowerForDriver")]
    int NID_38415146(uint value)
    {
        State.Words[6] = value;
        return 0;
    }

    /// <summary>NID_B24263F0(index, a, b): index below 4: keeps the pair; else 0x802B0000.</summary>
    [Hle(Nid = 0xB24263F0, Library = "ScePowerForDriver")]
    int NID_B24263F0(uint index, uint a, uint b)
    {
        if (index >= 4)
        {
            return POWER_ERR_INVALID;
        }

        State.DeviceTableA[index] = a;
        State.DeviceTableB[index] = b;
        return 0;
    }

    /// <summary>NID_C62B6164(callback, argument, threshold): the battery temperature callback (the module calls it when
    /// the temperature crosses the threshold). 0.</summary>
    [Hle(Nid = 0xC62B6164, Library = "ScePowerForDriver")]
    int NID_C62B6164(uint callback, uint argument, uint threshold)
    {
        State.TemperatureThreshold = threshold;
        State.TemperatureArgument = argument;
        State.TemperatureCallback = callback;
        return 0;
    }

    /// <summary>NID_F535D928(bits, set): sets or clears bits of the module's flag word. 0.</summary>
    [Hle(Nid = 0xF535D928, Library = "ScePowerForDriver")]
    int NID_F535D928(uint bits, int set)
    {
        lock (State)
        {
            State.Flags = set != 0 ? State.Flags | bits : State.Flags & ~bits;
        }

        return 0;
    }

    /// <summary>NID_D516D446(uint *value): the module's word +0x104 (0). 0.</summary>
    [Hle(Nid = 0xD516D446, Library = "ScePowerForDriver")]
    int NID_D516D446(uint value)
    {
        if (value != 0)
        {
            GuestMemory.W32(value, State.Words[7]);
        }

        return 0;
    }

    /// <summary>NID_394DE492(): the display is set up (the module's flag, 1 once it started).</summary>
    [Hle(Nid = 0x394DE492, Library = "ScePowerForDriver")]
    int NID_394DE492() => 1;

    // ------------------------------------------------------------------ requests

    /// <summary>kscePowerRequestSoftReset(): ScePower 3.74 sets the reset request (0x40) on its power thread, which
    /// ends with the syscon's soft reset: the system reboots (<see cref="SystemPower.Reboot"/>: the session ends and
    /// the host boots the system again). 0.</summary>
    [Hle(Nid = 0x2875994B, Library = "ScePowerForDriver")]
    int kscePowerRequestSoftReset()
    {
        Log.Info("kscePowerRequestSoftReset: reboot");
        Emu.Vita.Power.Reboot();
        return 0;
    }

    /// <summary>kscePowerRequestHibernate(): the system turns off (its state is not kept). 0.</summary>
    [Hle(Nid = 0x08951418, Library = "ScePowerForDriver")]
    int kscePowerRequestHibernate()
    {
        Log.Info("kscePowerRequestHibernate: power off");
        Emu.Vita.Power.PowerOff();
        return 0;
    }

    /// <summary>kscePowerRequestShutdown(reason): the system turns off. 0.</summary>
    [Hle(Nid = 0x8921A7A0, Library = "ScePowerForDriver")]
    int kscePowerRequestShutdown(uint reason)
    {
        State.Words[8] = reason;
        Log.Info($"kscePowerRequestShutdown({reason}): power off");
        Emu.Vita.Power.PowerOff();
        return 0;
    }

    // ------------------------------------------------------------------ the battery and charging

    /// <summary>NID_8C0D2051(uint *status): the battery status word the module polls (command 0x800; 0 under the
    /// fake status). 0.</summary>
    [Hle(Nid = 0x8C0D2051, Library = "ScePowerForDriver")]
    int NID_8C0D2051(uint status)
    {
        GuestMemory.W32(status, State.FakeBattery ? 0 : Chip.BatteryStatusWord);
        return 0;
    }

    /// <summary>NID_0D56C601(): under the fake status, 1 once its time ran out; else the chip's status bit 19.</summary>
    [Hle(Nid = 0x0D56C601, Library = "ScePowerForDriver")]
    int NID_0D56C601() => FakeRanOut(0) ? 1 : (Chip.Battery.Present && Chip.StatusBit(19) ? 1 : 0);

    /// <summary>NID_627A89C6(): under the fake status, 1 once its time ran out; else the chip's status bit 18.</summary>
    [Hle(Nid = 0x627A89C6, Library = "ScePowerForDriver")]
    int NID_627A89C6() => FakeRanOut(0) ? 1 : (Chip.Battery.Present && Chip.StatusBit(18) ? 1 : 0);

    /// <summary>The fake status is on and <paramref name="marginUs"/> past its time left.</summary>
    internal static bool FakeRanOut(ScePowerState state, long marginUs)
    {
        return state.FakeBattery && NowUs - state.FakeSetUs >= state.FakeLifeTimeUs + marginUs;
    }

    bool FakeRanOut(long marginUs) => FakeRanOut(State, marginUs);

    /// <summary>kscePowerSetBatteryFakeStatus(const SceUInt32 status[4] or NULL): {size 0x10, remaining capacity,
    /// full capacity (0: 2000), time left in µs}; NULL turns it off. Another size is 0x802B0000.</summary>
    [Hle(Nid = 0x0C6973B8, Library = "ScePowerForDriver")]
    int kscePowerSetBatteryFakeStatus(uint status)
    {
        if (status == 0)
        {
            State.FakeBattery = false;
            State.FakeMode = 0;
            State.FakeRemainCapacity = 0;
            State.FakeFullCapacity = 0;
            State.FakeLifeTimeUs = 0;
            return 0;
        }

        if (GuestMemory.R32(status) != 0x10)
        {
            return POWER_ERR_INVALID;
        }

        State.FakeMode = GuestMemory.R32(status);
        State.FakeFullCapacity = GuestMemory.R32(status + 8) is 0 ? 2000u : GuestMemory.R32(status + 8);
        State.FakeLifeTimeUs = GuestMemory.R32(status + 12);
        uint remain = GuestMemory.R32(status + 4);
        if (remain == 0)
        {
            remain = Chip.Battery.Present ? (uint)Chip.Battery.RemainCapacity : State.FakeFullCapacity;
        }

        State.FakeRemainCapacity = remain;
        State.FakeSetUs = NowUs;
        State.FakeBattery = true;
        return 0;
    }

    /// <summary>kscePowerBatteryEnableUsbCharging(): USB charging on; the previous state.</summary>
    [Hle(Nid = 0x733F973B, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerBatteryEnableUsbCharging()
    {
        bool before = State.UsbCharging;
        State.UsbCharging = true;
        return before ? 1 : 0;
    }

    /// <summary>kscePowerBatteryDisableUsbCharging(): USB charging off. 0.</summary>
    [Hle(Nid = 0x90285886, Library = "ScePowerForDriver")]
    int kscePowerBatteryDisableUsbCharging()
    {
        State.UsbCharging = false;
        return 0;
    }

    /// <summary>kscePowerBatteryStopUsbCharging(): while USB charging is on, tells the chip to stop (command 0x804
    /// {1}). 0.</summary>
    [Hle(Nid = 0xACC857A4, Library = "ScePowerForDriver")]
    int kscePowerBatteryStopUsbCharging()
    {
        if (State.UsbCharging)
        {
            Syscon.SendToChip(0x804, [1]);
        }

        return 0;
    }

    /// <summary>kscePowerBatterySetUsbStatus(status): sent to the chip (command 0x89A {status}). 0.</summary>
    [Hle(Nid = 0xE4E3C316, Library = "ScePowerForDriver")]
    int kscePowerBatterySetUsbStatus(uint status)
    {
        State.UsbStatus = status;
        int result = Syscon.SendToChip(0x89A, [(byte)status]);
        return Math.Min(result, 0);
    }

    /// <summary>kscePowerBatteryForbidCharging(): one more reason not to charge. 0.</summary>
    [Hle(Nid = 0x166922EC, Library = "ScePowerForDriver")]
    int kscePowerBatteryForbidCharging()
    {
        lock (State)
        {
            State.ChargingForbidden++;
        }

        return 0;
    }

    /// <summary>kscePowerBatteryPermitCharging(): one reason less (never below 0). 0.</summary>
    [Hle(Nid = 0xDD3D4DAC, Library = "ScePowerForDriver")]
    int kscePowerBatteryPermitCharging()
    {
        lock (State)
        {
            if (State.ChargingForbidden > 0)
            {
                State.ChargingForbidden--;
            }
        }

        return 0;
    }

    /// <summary>
    /// kscePowerEncodeUBattery (0x3951AF53; ScePower 3.74, decompiled, study only: it is not a battery call): waits for
    /// the power request in flight. It polls the power thread's event flag and, when a request (bits 0x6F0: standby,
    /// power off, reset, hibernate...) is set, waits for the thread to take it (0x20000) and finish it (0x40000); its
    /// result is the wait's (0 with no request). Here each request is carried out by the call that makes it (the
    /// standby, the power off and the reboot happen before that call returns to the guest), so no request is ever in
    /// flight when a thread gets here: 0, as the firmware answers with none pending.
    /// </summary>
    [Hle(Nid = 0x3951AF53, Library = "ScePowerForDriver")]
    int kscePowerEncodeUBattery() => 0;

    // ------------------------------------------------------------------ the display brightness

    /// <summary>kscePowerGetDisplayMaxBrightness(): a PS TV 0x10000; else the limit for the AC adapter or the
    /// battery.</summary>
    [Hle(Nid = 0xD8759B55, Library = "ScePowerForDriver", ReturnsValue = true)]
    uint kscePowerGetDisplayMaxBrightness()
    {
        if (Emu.Hle.Modules.OfType<SystemServicesVsh>().First().ksceSblAimgrIsDolceThroughEntry() != 0)
        {
            return 0x10000;
        }

        return Chip.AcAdapter ? State.DisplayMaxBrightnessAc : State.DisplayMaxBrightness;
    }

    /// <summary>kscePowerSetDisplayMaxBrightness(limit): both limits (1 is taken as 2). 0.</summary>
    [Hle(Nid = 0x77027B6B, Library = "ScePowerForDriver")]
    int kscePowerSetDisplayMaxBrightness(uint limit)
    {
        if (limit == 1)
        {
            limit = 2;
        }

        State.DisplayMaxBrightness = limit;
        State.DisplayMaxBrightnessAc = limit;
        return 0;
    }

    /// <summary>kscePowerSetDisplayBrightness(level): a level below 21 is a step of 1/20 (16.16), a bigger value the
    /// 16.16 brightness itself. 0.</summary>
    [Hle(Nid = 0x43D5CE1D, Library = "ScePowerForDriver")]
    int kscePowerSetDisplayBrightness(uint level)
    {
        State.DisplayBrightness = level < 0x15 ? (uint)((ulong)(level << 16) * 0xCCCCCCCD >> 36) : level;
        return 0;
    }

    // ------------------------------------------------------------------ the clocks

    /// <summary>kscePowerGetDmac5ClockFrequency().</summary>
    [Hle(Nid = 0xF1E14EA9, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerGetDmac5ClockFrequency() => State.Dmac5Clock;

    /// <summary>kscePowerGetCameraBusClockFrequency().</summary>
    [Hle(Nid = 0x668F01D4, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerGetCameraBusClockFrequency() => State.CameraBusClock;

    /// <summary>kscePowerGetVeneziaClockFrequency().</summary>
    [Hle(Nid = 0x64641E6A, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerGetVeneziaClockFrequency() => State.VeneziaClock;

    /// <summary>kscePowerGetVipClockFrequency().</summary>
    [Hle(Nid = 0x0E58FCDF, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerGetVipClockFrequency() => State.VipClock;

    /// <summary>kscePowerGetCompatClockFrequency().</summary>
    [Hle(Nid = 0xCBCC11CC, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerGetCompatClockFrequency() => State.CompatClock;

    /// <summary>kscePowerGetGpuClockFrequencyInternal(int *core, int *second): the two GPU clocks. 0.</summary>
    [Hle(Nid = 0x475BCC82, Library = "ScePowerForDriver")]
    int kscePowerGetGpuClockFrequencyInternal(uint core, uint second)
    {
        if (core != 0)
        {
            GuestMemory.W32(core, (uint)State.GpuClockInternal);
        }

        if (second != 0)
        {
            GuestMemory.W32(second, (uint)State.GpuClockInternal2);
        }

        return 0;
    }

    /// <summary>The GPU clock steps of kscePowerSetGpuClockFrequencyInternal: 41, 55, 83, 111, 166, 222 MHz.</summary>
    static int GpuStep(int mhz)
    {
        return mhz switch
        {
            < 0x2A => 0x29,
            < 0x38 => 0x37,
            < 0x54 => 0x53,
            < 0x70 => 0x6F,
            < 0xA7 => 0xA6,
            <= 0xDE => 0xDE,
            _ => -1,
        };
    }

    /// <summary>kscePowerSetGpuClockFrequencyInternal(core, second): each rounded up to a step (0, 0: both off); the
    /// second may not be above the core, nor at 166 MHz or more while the core is above it. Out of range:
    /// 0x802B0000.</summary>
    [Hle(Nid = 0x264C24FC, Library = "ScePowerForDriver")]
    int kscePowerSetGpuClockFrequencyInternal(int core, int second)
    {
        if ((core | second) < 0)
        {
            return POWER_ERR_INVALID;
        }

        int coreStep = 0, secondStep = 0;
        if (core != 0 || second != 0)
        {
            coreStep = GpuStep(core);
            secondStep = GpuStep(second);
            if (coreStep < 0 || secondStep < 0 || secondStep > coreStep)
            {
                return POWER_ERR_INVALID;
            }

            if (secondStep == 0xA6 && coreStep > 0xA6)
            {
                return POWER_ERR_INVALID;
            }
        }

        State.GpuClockInternal = coreStep;
        State.GpuClockInternal2 = secondStep;
        return 0;
    }

    /// <summary>kscePowerSetCameraBusClockFrequency(MHz): 0 off, else rounded up to 41, 67, 83, 133 or 166; above
    /// 166 or negative 0x802B0000.</summary>
    [Hle(Nid = 0xC743E392, Library = "ScePowerForDriver")]
    int kscePowerSetCameraBusClockFrequency(int mhz)
    {
        int step = mhz switch
        {
            < 0 => -1,
            0 => 0,
            < 0x2A => 0x29,
            < 0x44 => 0x43,
            < 0x54 => 0x53,
            < 0x86 => 0x85,
            <= 0xA6 => 0xA6,
            _ => -1,
        };
        if (step < 0)
        {
            return POWER_ERR_INVALID;
        }

        State.CameraBusClock = step;
        return 0;
    }

    /// <summary>kscePowerSetDmac5ClockFrequency(MHz): 0 off, else rounded up to 41, 83, 133 or 166.</summary>
    [Hle(Nid = 0xA902CDDF, Library = "ScePowerForDriver")]
    int kscePowerSetDmac5ClockFrequency(int mhz)
    {
        int step = mhz switch
        {
            < 0 => -1,
            0 => 0,
            < 0x2A => 0x29,
            < 0x54 => 0x53,
            < 0x86 => 0x85,
            <= 0xA6 => 0xA6,
            _ => -1,
        };
        if (step < 0)
        {
            return POWER_ERR_INVALID;
        }

        State.Dmac5Clock = step;
        return 0;
    }

    /// <summary>kscePowerSetVeneziaClockFrequency(MHz): 0..333, kept as given.</summary>
    [Hle(Nid = 0x621BD8FD, Library = "ScePowerForDriver")]
    int kscePowerSetVeneziaClockFrequency(int mhz)
    {
        if (mhz < 0 || mhz > 0x14D)
        {
            return POWER_ERR_INVALID;
        }

        State.VeneziaClock = mhz;
        return 0;
    }

    /// <summary>kscePowerSetVipClockFrequency(MHz): 0..222, kept as given.</summary>
    [Hle(Nid = 0xE5573571, Library = "ScePowerForDriver")]
    int kscePowerSetVipClockFrequency(int mhz)
    {
        if (mhz < 0 || mhz > 0xDE)
        {
            return POWER_ERR_INVALID;
        }

        State.VipClock = mhz;
        return 0;
    }

    /// <summary>kscePowerSetCompatClockFrequency(MHz): 0, 222 or up to 333, kept as given (the compat side's ARM
    /// clock).</summary>
    [Hle(Nid = 0xFFC84E69, Library = "ScePowerForDriver")]
    int kscePowerSetCompatClockFrequency(int mhz)
    {
        if (mhz > 0x14D)
        {
            return POWER_ERR_INVALID;
        }

        State.CompatClock = mhz;
        return 0;
    }

    // ------------------------------------------------------------------ the per-process clocks and flags

    /// <summary>kscePowerGetArmClockFrequencyProc(pid): the ARM clock the process asked for.</summary>
    [Hle(Nid = 0x2E9000F7, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerGetArmClockFrequencyProc(int pid) => ProcessClock(State.ArmClockByProcess, pid);

    /// <summary>kscePowerGetBusClockFrequencyProc(pid).</summary>
    [Hle(Nid = 0x2777A517, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerGetBusClockFrequencyProc(int pid) => ProcessClock(State.BusClockByProcess, pid);

    /// <summary>kscePowerGetGpuXbarClockFrequencyProc(pid).</summary>
    [Hle(Nid = 0x62C5406C, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerGetGpuXbarClockFrequencyProc(int pid) => ProcessClock(State.GpuXbarClockByProcess, pid);

    /// <summary>kscePowerGetGpuClockFrequencyProc(pid).</summary>
    [Hle(Nid = 0x642E0AF2, Library = "ScePowerForDriver", ReturnsValue = true)]
    int kscePowerGetGpuClockFrequencyProc(int pid) => ProcessClock(State.GpuClockByProcess, pid);

    static int ProcessClock(Dictionary<int, int> clocks, int pid)
    {
        lock (clocks)
        {
            return clocks.GetValueOrDefault(pid);
        }
    }

    /// <summary>The highest clock any process asked for.</summary>
    static int Highest(Dictionary<int, int> clocks, int pid, int mhz)
    {
        lock (clocks)
        {
            clocks[pid] = mhz;
            int highest = 0;
            foreach (int value in clocks.Values)
            {
                highest = Math.Max(highest, value);
            }

            return highest;
        }
    }

    /// <summary>kscePowerSetArmClockFrequencyProc(pid, MHz): 1..444; the system runs at the highest process's.</summary>
    [Hle(Nid = 0x86CB5218, Library = "ScePowerForDriver")]
    int kscePowerSetArmClockFrequencyProc(int pid, int mhz)
    {
        if ((uint)(mhz - 1) >= 0x1BC)
        {
            return POWER_ERR_INVALID;
        }

        return Misc.scePowerSetArmClockFrequencyThroughEntry(Highest(State.ArmClockByProcess, pid, mhz));
    }

    /// <summary>kscePowerSetBusClockFrequencyProc(pid, MHz): 1..222.</summary>
    [Hle(Nid = 0x20A33D58, Library = "ScePowerForDriver")]
    int kscePowerSetBusClockFrequencyProc(int pid, int mhz)
    {
        if ((uint)(mhz - 1) >= 0xDE)
        {
            return POWER_ERR_INVALID;
        }

        return Misc.scePowerSetBusClockFrequencyThroughEntry(Highest(State.BusClockByProcess, pid, mhz));
    }

    /// <summary>kscePowerSetGpuXbarClockFrequencyProc(pid, MHz): 1..166.</summary>
    [Hle(Nid = 0x8D18F728, Library = "ScePowerForDriver")]
    int kscePowerSetGpuXbarClockFrequencyProc(int pid, int mhz)
    {
        if ((uint)(mhz - 1) >= 0xA6)
        {
            return POWER_ERR_INVALID;
        }

        return Misc.scePowerSetGpuXbarClockFrequencyThroughEntry(Highest(State.GpuXbarClockByProcess, pid, mhz));
    }

    /// <summary>kscePowerSetGpuClockFrequencyProc(pid, MHz): 1..222: both GPU clocks at the highest process's, and
    /// the brightness cap of the process flags.</summary>
    [Hle(Nid = 0xB104EFE2, Library = "ScePowerForDriver")]
    int kscePowerSetGpuClockFrequencyProc(int pid, int mhz)
    {
        if ((uint)(mhz - 1) >= 0xDE)
        {
            return POWER_ERR_INVALID;
        }

        int highest = Highest(State.GpuClockByProcess, pid, mhz);
        int result = kscePowerSetGpuClockFrequencyInternalThroughEntry(highest, highest);
        State.GpuClock = highest;
        ApplyBrightnessCap();
        return result;
    }

    Dictionary<int, uint> ProcessFlagTable => State.ProcessFlags;

    uint ProcessFlags(int pid)
    {
        lock (ProcessFlagTable)
        {
            return ProcessFlagTable.GetValueOrDefault(pid);
        }
    }

    int SetProcessFlag(int pid, uint bit, uint on)
    {
        if (on > 1)
        {
            return POWER_ERR_INVALID;
        }

        lock (ProcessFlagTable)
        {
            uint flags = ProcessFlagTable.GetValueOrDefault(pid);
            ProcessFlagTable[pid] = on != 0 ? flags | bit : flags & ~bit;
        }

        ApplyBrightnessCap();
        return 0;
    }

    /// <summary>With a GPU at 166 MHz or more and a process asking (flags bits 0/1), the display is capped at
    /// 0xC4EC; else 0x10000.</summary>
    void ApplyBrightnessCap()
    {
        uint any = 0;
        lock (ProcessFlagTable)
        {
            foreach (uint flags in ProcessFlagTable.Values)
            {
                any |= flags;
            }
        }

        int gpu = 0;
        lock (State.GpuClockByProcess)
        {
            foreach (int value in State.GpuClockByProcess.Values)
            {
                gpu = Math.Max(gpu, value);
            }
        }

        kscePowerSetDisplayMaxBrightnessThroughEntry(gpu >= 0xA6 && (any & ProcessFlagsBrightnessCap) != 0 ? 0xC4ECu : 0x10000u);
    }

    /// <summary>NID_4A69163A(pid, on): the process's power flag bit 0.</summary>
    [Hle(Nid = 0x4A69163A, Library = "ScePowerForDriver")]
    int NID_4A69163A(int pid, uint on) => SetProcessFlag(pid, 1, on);

    /// <summary>NID_661D670D(pid, on): the process's power flag bit 1.</summary>
    [Hle(Nid = 0x661D670D, Library = "ScePowerForDriver")]
    int NID_661D670D(int pid, uint on) => SetProcessFlag(pid, 2, on);

    /// <summary>NID_2784A6BD(pid): the process's flag bit 0.</summary>
    [Hle(Nid = 0x2784A6BD, Library = "ScePowerForDriver")]
    int NID_2784A6BD(int pid) => (int)(ProcessFlags(pid) & 1);

    /// <summary>NID_0D80B917(pid): the process's flag bit 1.</summary>
    [Hle(Nid = 0x0D80B917, Library = "ScePowerForDriver")]
    int NID_0D80B917(int pid) => (int)(ProcessFlags(pid) >> 1 & 1);

    /// <summary>NID_4159E0D9(pid): the process's flag bit 4.</summary>
    [Hle(Nid = 0x4159E0D9, Library = "ScePowerForDriver")]
    int NID_4159E0D9(int pid) => (int)(ProcessFlags(pid) >> 4 & 1);

    /// <summary>NID_5210A6FE(pid): the process's flag bit 5.</summary>
    [Hle(Nid = 0x5210A6FE, Library = "ScePowerForDriver")]
    int NID_5210A6FE(int pid) => (int)(ProcessFlags(pid) >> 5 & 1);

    /// <summary>NID_06AF03DB(pid): either of the process's flags 0 and 1.</summary>
    [Hle(Nid = 0x06AF03DB, Library = "ScePowerForDriver")]
    int NID_06AF03DB(int pid) => (ProcessFlags(pid) & 3) != 0 ? 1 : 0;

    // ------------------------------------------------------------------ the idle timers

    /// <summary>kscePowerSetIdleCallback(slot, flags, time (64 bits, µs), callback, argument): slot below 8; a slot
    /// with a callback cannot take another (0x802B0001). The timer starts now.</summary>
    [Hle(Nid = 0x1BA2FCAE, Library = "ScePowerForDriver")]
    void kscePowerSetIdleCallback(HleContext context)
    {
        uint slot = context.Gpr(0);
        uint flags = context.Gpr(1) & 0xFFFF;
        long timeUs = (long)(context.Gpr(2) | (ulong)context.Gpr(3) << 32);
        uint callback = context.Gpr(4);
        uint argument = context.Gpr(5);
        if (slot >= ScePowerState.IdleTimerCount)
        {
            context.Return(POWER_ERR_INVALID);
            return;
        }

        lock (State)
        {
            if (callback != 0 && State.IdleCallback[slot] != 0)
            {
                context.Return(POWER_ERR_BUSY);
                return;
            }

            State.IdleFlags[slot] = flags;
            State.IdleStartUs[slot] = NowUs;
            State.IdleTimeoutUs[slot] = timeUs;
            State.IdleCallback[slot] = callback;
            State.IdleArgument[slot] = argument;
            State.IdleShareMask[slot] = 0;
            State.IdleTickPending[slot] = false;
            State.IdleState[slot] = IdleNotChecked;
        }

        ArmIdleCheck();
        context.Return(0);
    }

    /// <summary>kscePowerSetIdleTimer(slot, time (64 bits)): a slot with a callback; the slots that share its time
    /// (NID_991BDEAF) take it too.</summary>
    [Hle(Nid = 0x6BC26FC7, Library = "ScePowerForDriver")]
    void kscePowerSetIdleTimer(HleContext context)
    {
        uint slot = context.Gpr(0);
        long timeUs = (long)(context.Gpr(2) | (ulong)context.Gpr(3) << 32);
        lock (State)
        {
            if (slot >= ScePowerState.IdleTimerCount || State.IdleCallback[slot] == 0)
            {
                context.Return(POWER_ERR_INVALID);
                return;
            }

            State.IdleTimeoutUs[slot] = timeUs;
            uint share = State.IdleShareMask[slot];
            for (int other = 0; other < ScePowerState.IdleTimerCount; other++)
            {
                if ((share >> other & 1) != 0)
                {
                    State.IdleTimeoutUs[other] = timeUs;
                }
            }
        }

        ArmIdleCheck();
        context.Return(0);
    }

    /// <summary>NID_FB5C3C75(pid, time (64 bits)): the system (0x10021): idle timer 0's time, with the slots that
    /// share it (0x802B0000 without its callback); a process: its own idle time.</summary>
    [Hle(Nid = 0xFB5C3C75, Library = "ScePowerForDriver")]
    void NID_FB5C3C75(HleContext context)
    {
        int pid = (int)context.Gpr(0);
        long timeUs = (long)(context.Gpr(2) | (ulong)context.Gpr(3) << 32);
        if (pid != SystemProcess)
        {
            lock (ProcessIdleTable)
            {
                ProcessIdleTable[pid] = timeUs;
            }

            context.Return(0);
            return;
        }

        lock (State)
        {
            if (State.IdleCallback[0] == 0)
            {
                context.Return(POWER_ERR_INVALID);
                return;
            }

            State.IdleTimeoutUs[0] = timeUs;
            for (int other = 1; other < ScePowerState.IdleTimerCount; other++)
            {
                if ((State.IdleShareMask[0] >> other & 1) != 0)
                {
                    State.IdleTimeoutUs[other] = timeUs;
                }
            }
        }

        ArmIdleCheck();
        context.Return(0);
    }

    Dictionary<int, long> ProcessIdleTable => State.ProcessIdleTimeUs;

    /// <summary>NID_C715F4F0(pid): the system: idle timer 0 is idle (it fired, or was not checked yet, and no tick came
    /// since); a process: it set an idle time.</summary>
    [Hle(Nid = 0xC715F4F0, Library = "ScePowerForDriver")]
    int NID_C715F4F0(int pid)
    {
        if (pid == SystemProcess)
        {
            lock (State)
            {
                return !State.IdleTickPending[0] && State.IdleState[0] != IdleCounting ? 1 : 0;
            }
        }

        lock (ProcessIdleTable)
        {
            return ProcessIdleTable.GetValueOrDefault(pid) != 0 ? 1 : 0;
        }
    }

    /// <summary>kscePowerGetIdleTimer(slot, SceUInt64 *elapsed, SceInt64 *remaining): the time since the slot's last
    /// start (kscePowerTick restarts it) and what is left of its time. Slot 8 or more: 0x802B0000.</summary>
    [Hle(Nid = 0xEDC13FE5, Library = "ScePowerForDriver")]
    int kscePowerGetIdleTimer(uint slot, uint elapsed, uint remaining)
    {
        if (slot >= ScePowerState.IdleTimerCount)
        {
            return POWER_ERR_INVALID;
        }

        long since;
        long left;
        lock (State)
        {
            since = NowUs - State.IdleStartUs[slot];
            left = State.IdleTimeoutUs[slot] - since;
        }

        if (elapsed != 0)
        {
            GuestMemory.Write(elapsed, since);
        }

        if (remaining != 0)
        {
            GuestMemory.Write(remaining, left);
        }

        return 0;
    }

    /// <summary>NID_991BDEAF(slot, mask): the slots in <paramref name="mask"/> follow the slot's time. 0.</summary>
    [Hle(Nid = 0x991BDEAF, Library = "ScePowerForDriver")]
    int NID_991BDEAF(uint slot, uint mask)
    {
        if (slot >= ScePowerState.IdleTimerCount)
        {
            return POWER_ERR_INVALID;
        }

        lock (State)
        {
            State.IdleShareMask[slot] |= mask;
        }

        return 0;
    }

    /// <summary>NID_660D5AB4(slot): a slot with a time fires at the next check, whatever its time. 0.</summary>
    [Hle(Nid = 0x660D5AB4, Library = "ScePowerForDriver")]
    int NID_660D5AB4(uint slot)
    {
        if (slot >= ScePowerState.IdleTimerCount)
        {
            return POWER_ERR_INVALID;
        }

        lock (State)
        {
            if (State.IdleTimeoutUs[slot] != 0)
            {
                State.IdleState[slot] = IdleForced;
            }
        }

        ArmIdleCheck();
        return 0;
    }

    /// <summary>kscePowerTick(type): the user did something: the idle timers in <paramref name="type"/> (and the
    /// slots that share their time) start over, once per check (a slot with a tick pending keeps its start); 0
    /// restarts every slot without flag 0x200. The next check tells a fired slot's callback (elapsed 0). 0.</summary>
    [Hle(Nid = 0xEFD3C963, Library = "ScePowerForDriver")]
    int kscePowerTick(uint type)
    {
        lock (State)
        {
            uint slots = type;
            if (type == 0)
            {
                for (int slot = 0; slot < ScePowerState.IdleTimerCount; slot++)
                {
                    if ((State.IdleFlags[slot] & IdleFlagNotAll) == 0)
                    {
                        slots |= 1u << slot;
                    }
                }
            }
            uint shared = 0;
            for (int slot = 0; slot < ScePowerState.IdleTimerCount; slot++)
            {
                if ((slots >> slot & 1) != 0)
                {
                    shared |= State.IdleShareMask[slot];
                }
            }

            slots |= shared;
            long now = NowUs;
            for (int slot = 0; slot < ScePowerState.IdleTimerCount; slot++)
            {
                if ((slots >> slot & 1) != 0 && !State.IdleTickPending[slot])
                {
                    State.IdleTickPending[slot] = true;
                    State.IdleStartUs[slot] = now;
                }
            }
        }

        ArmIdleCheck();
        return 0;
    }
}
