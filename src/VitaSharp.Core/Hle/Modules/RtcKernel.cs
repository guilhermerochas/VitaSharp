using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceRtcForDriver's clocks and alarms (FW 3.74 rtc.skprx, read in the module, study only) over the system's
/// <see cref="RtcClocks"/> and the syscon chip. A clock read is the system time plus the clock's offset (0x80251002
/// while the clock is not set); setting the main clock also resets the chip's RTC counter and keeps the tick in its
/// scratchpad, as the module does; the alarm goes to the chip (command 0x82, in half seconds) and rings through the
/// single guest-time scheduler (<see cref="TimerDispatcher"/>): the chip raises its alarm bit (status bit 3) and the
/// registered callback is notified. The conversions and calendar exports are the user-mode SceRtc functions (same
/// code in librtc and the kernel module), bound to their kernel NIDs in <see cref="Rtc"/>.
/// </summary>
public sealed class RtcKernel : HleModule, ISnapshotTimers
{
    /// <summary>A state load: the alarms of the loaded clocks ring at their times; one that had rung before the save
    /// (the chip's alarm bit is up; a secure alarm whose time had passed) does not ring again.</summary>
    void ISnapshotTimers.RearmTimers()
    {
        AlarmTimer alarm = Alarm;
        bool pending;
        lock (Clocks)
        {
            alarm.Restore(Chip.StatusBit(AlarmBit), SecureAlarmed());
            pending = alarm.HasPending;
        }

        if (pending)
        {
            TimerDispatcher.Arm(alarm);
        }
    }

    /// <summary>SceRtc: a bad value; NULL; a clock that is not set; a callback already registered; another one.</summary>
    const int RTC_ERR_INVALID = unchecked((int)0x80251000), RTC_ERR_NULL = unchecked((int)0x80251001),
        RTC_ERR_NOT_SET = unchecked((int)0x80251002), RTC_ERR_REGISTERED = unchecked((int)0x80251003),
        RTC_ERR_NOT_REGISTERED = unchecked((int)0x80251004);

    /// <summary>The chip's status bit for the RTC alarm (SceSyscon's flag 0x8100C0C5).</summary>
    const int AlarmBit = 3;

    /// <summary>The chip's RTC counts half seconds.</summary>
    const long ChipRtcUnitUs = 500_000;

    RtcClocks Clocks => Emu.Vita.RtcClocks;

    SysconChip Chip => Emu.Vita.Syscon;

    /// <summary>The alarms' entry in the guest-time scheduler (made on first use; a load arms the loaded state's alarms
    /// again, <see cref="ISnapshotTimers.RearmTimers"/>).</summary>
    [SnapshotIgnore]
    AlarmTimer? _alarm;

    AlarmTimer Alarm => _alarm ??= new AlarmTimer(this);

    /// <summary>The local time's offset from UTC in minutes: the configuration ksceRtcSetConf gave; before it, the
    /// system registry's zone (/CONFIG/DATE/time_zone, an id of the firmware's list) plus an hour with summer_time,
    /// and the host's zone and daylight saving where the registry has no value (SceAppUtil's time zone parameters).
    /// </summary>
    internal static int LocalOffsetMinutes(Emulator emu)
    {
        RtcClocks clocks = emu.Vita.RtcClocks;
        if (clocks.ConfSet)
        {
            return clocks.TimeZoneMinutes + clocks.DaylightMinutes;
        }

        Firmware.Registry? registry = AppUtil.SystemRegistry(emu);
        int zone = AppUtil.SystemParamInt(6, registry);
        int summer = AppUtil.SystemParamInt(7, registry) != 0 ? 60 : 0;
        return zone + summer;
    }

    /// <summary>The tick of a clock now.</summary>
    internal static ulong ClockTick(RtcClocks clocks, int clock) => (ulong)((long)Rtc.NowTick + clocks.OffsetUs[clock]);

    ulong ClockTick(int clock) => ClockTick(Clocks, clock);

    /// <summary>A clock read: the tick, or 0x80251002 while the clock is not set (NULL: 0x80251001).</summary>
    int ReadClock(int clock, uint tick)
    {
        if (tick == 0)
        {
            return RTC_ERR_NULL;
        }

        if (!Clocks.Valid[clock])
        {
            return RTC_ERR_NOT_SET;
        }

        GuestMemory.Write(tick, ClockTick(clock));
        return 0;
    }

    /// <summary>A clock set: the clock runs from the tick from now on.</summary>
    int SetClock(int clock, uint tick)
    {
        if (tick == 0)
        {
            return RTC_ERR_NULL;
        }

        ulong value = GuestMemory.Read<ulong>(tick);
        lock (Clocks)
        {
            Clocks.OffsetUs[clock] = (long)value - (long)Rtc.NowTick;
            Clocks.Valid[clock] = true;
        }

        return 0;
    }

    // ------------------------------------------------------------------ the clocks

    /// <summary>ksceRtcSetCurrentTick(tick): the main clock; the chip's RTC counter starts again (command 0x81 {0})
    /// and the tick is kept in its scratchpad (+0x10), the alarm is cleared (command 0x82 {-1}).</summary>
    [Hle(Nid = 0x70A78CD2, Library = "SceRtcForDriver")]
    int ksceRtcSetCurrentTick(uint tick)
    {
        int result = SetClock(RtcClocks.Main, tick);
        if (result < 0)
        {
            return result;
        }

        SysconKernel syscon = Emu.Hle.Modules.OfType<SysconKernel>().First();
        syscon.SendToChip(0x82, [0xFF, 0xFF, 0xFF, 0xFF, 0]);
        syscon.SendToChip(0x81, [0, 0, 0, 0]);
        ulong value = GuestMemory.Read<ulong>(tick);
        Span<byte> stored = stackalloc byte[3 + 8];
        stored[0] = 0x10;
        stored[1] = 0;
        stored[2] = 8;
        BitConverter.TryWriteBytes(stored[3..], value);
        syscon.SendToChip(0x91, stored);
        return 0;
    }

    /// <summary>ksceRtcSetCurrentSecureTick(tick): the secure clock.</summary>
    [Hle(Nid = 0x7D431C87, Library = "SceRtcForDriver")]
    int ksceRtcSetCurrentSecureTick(uint tick) => SetClock(RtcClocks.Secure, tick);

    /// <summary>ksceRtcSetCurrentNetworkTick(tick): the network clock, which is also the one retained.</summary>
    [Hle(Nid = 0x162E486E, Library = "SceRtcForDriver")]
    int ksceRtcSetCurrentNetworkTick(uint tick)
    {
        int result = SetClock(RtcClocks.Network, tick);
        return result < 0 ? result : SetClock(RtcClocks.RetainedNetwork, tick);
    }

    /// <summary>ksceRtcSetCurrentDebugNetworkTick(tick): the network clock (the debug one reads it).</summary>
    [Hle(Nid = 0xAAB6053F, Library = "SceRtcForDriver")]
    int ksceRtcSetCurrentDebugNetworkTick(uint tick) => SetClock(RtcClocks.Network, tick);

    /// <summary>NID_A7236656(tick): the GPS clock (SceGps sets it).</summary>
    [Hle(Nid = 0xA7236656, Library = "SceRtcForDriver")]
    int NID_A7236656(uint tick) => SetClock(RtcClocks.Gps, tick);

    /// <summary>ksceRtcGetCurrentSecureTick(tick).</summary>
    [Hle(Nid = 0x401C0954, Library = "SceRtcForDriver")]
    int ksceRtcGetCurrentSecureTick(uint tick) => ReadClock(RtcClocks.Secure, tick);

    /// <summary>NID_A0D7899A(tick): the fourth clock of the table (SceSblPostSsMgr reads it).</summary>
    [Hle(Nid = 0xA0D7899A, Library = "SceRtcForDriver")]
    int NID_A0D7899A(uint tick) => ReadClock(RtcClocks.Manufacturing, tick);

    /// <summary>NID_ABA035B7(): every clock is no longer set (the module's reset). 0.</summary>
    [Hle(Nid = 0xABA035B7, Library = "SceRtcForDriver")]
    int NID_ABA035B7()
    {
        lock (Clocks)
        {
            Array.Clear(Clocks.Valid);
        }

        return 0;
    }

    /// <summary>NID_A2D280B4(): after a resume, the module reads the chip's RTC and moves every clock by the time it
    /// slept; the emulated clocks run on the system time, which the chip's counter follows, so there is nothing to
    /// move. 0.</summary>
    [Hle(Nid = 0xA2D280B4, Library = "SceRtcForDriver")]
    int NID_A2D280B4() => 0;

    /// <summary>ksceRtcSetConf(mode, flag, time zone, daylight saving): mode below 3, flag below 2, the zone within
    /// ±1439 minutes, daylight saving up to 1440; else 0x80251000.</summary>
    [Hle(Nid = 0xACFE5A9F, Library = "SceRtcForDriver")]
    int ksceRtcSetConf(uint mode, uint flag, int timeZone, uint daylight)
    {
        if (mode >= 3 || flag >= 2 || (uint)(timeZone + 0x59F) >= 0xB3F || daylight >= 0x5A1)
        {
            return RTC_ERR_INVALID;
        }

        lock (Clocks)
        {
            Clocks.ConfMode = mode;
            Clocks.ConfFlag = flag;
            Clocks.TimeZoneMinutes = timeZone;
            Clocks.DaylightMinutes = (int)daylight;
            Clocks.ConfSet = true;
        }

        return 0;
    }

    /// <summary>ksceRtcConvertUtcToLocal(utc, local): the tick plus the zone and daylight saving of the
    /// configuration.</summary>
    [Hle(Nid = 0xC6E03DD4, Library = "SceRtcForDriver")]
    int ksceRtcConvertUtcToLocal(uint utc, uint local)
    {
        if (utc == 0 || local == 0)
        {
            return RTC_ERR_NULL;
        }

        long minutes = LocalOffsetMinutes(Emu);
        GuestMemory.Write(local, (ulong)((long)GuestMemory.Read<ulong>(utc) + minutes * 60_000_000));
        return 0;
    }

    // ------------------------------------------------------------------ the alarm (the syscon chip's)

    /// <summary>ksceRtcSetAlarmTick(tick, flag): NULL or 0 clears it; a tick already past is 0x80251000. The chip
    /// gets the alarm in half seconds of its counter (command 0x82); the alarm rings at its time.</summary>
    [Hle(Nid = 0xD610C646, Library = "SceRtcForDriver")]
    int ksceRtcSetAlarmTick(uint tick, uint flag)
    {
        ulong value = tick == 0 ? 0 : GuestMemory.Read<ulong>(tick);
        ulong now = ClockTick(RtcClocks.Main);
        uint count = uint.MaxValue;
        if (value != 0)
        {
            if (value < now)
            {
                return RTC_ERR_INVALID;
            }

            count = (uint)Math.Min((value - now) / ChipRtcUnitUs + 1, 0xFFFFFF00);
        }

        SysconKernel syscon = Emu.Hle.Modules.OfType<SysconKernel>().First();
        int result = syscon.SendToChip(0x82, [(byte)count, (byte)(count >> 8), (byte)(count >> 16), (byte)(count >> 24), (byte)flag]);
        if (result < 0)
        {
            return result;
        }

        lock (Clocks)
        {
            Clocks.AlarmTick = value;
            Clocks.AlarmFlag = flag;
            Chip.StatusWord &= ~(1u << AlarmBit);
        }

        if (value != 0)
        {
            TimerDispatcher.Arm(Alarm);
        }

        return 0;
    }

    /// <summary>ksceRtcGetAlarmTick(tick, flag): the alarm (0: none) and its flag; NULL tick 0x80251001.</summary>
    [Hle(Nid = 0xC838275A, Library = "SceRtcForDriver")]
    int ksceRtcGetAlarmTick(uint tick, uint flag)
    {
        if (tick == 0)
        {
            return RTC_ERR_NULL;
        }

        GuestMemory.Write(tick, Clocks.AlarmTick);
        if (flag != 0)
        {
            GuestMemory.W32(flag, Clocks.AlarmFlag);
        }

        return 0;
    }

    /// <summary>The alarm rang: one is set, the chip's alarm bit is up and its time has come.</summary>
    bool Alarmed()
    {
        ulong alarm = Clocks.AlarmTick;
        return alarm != 0 && Chip.StatusBit(AlarmBit) && ClockTick(RtcClocks.Main) >= alarm;
    }

    /// <summary>ksceRtcIsAlarmed().</summary>
    [Hle(Nid = 0xCD295F0C, Library = "SceRtcForDriver")]
    int ksceRtcIsAlarmed()
    {
        Alarm.FireDueAndRemaining();
        return Alarmed() ? 1 : 0;
    }

    /// <summary>ksceRtcRegisterCallback(callback): one callback; it is notified at once with whether the alarm rang,
    /// and again when it rings. A second one: 0x80251003.</summary>
    [Hle(Nid = 0x5220870D, Library = "SceRtcForDriver")]
    int ksceRtcRegisterCallback(int callback)
    {
        if (Clocks.AlarmCallback >= 0)
        {
            return RTC_ERR_REGISTERED;
        }

        if (Emu.Kernel.Objects.Get<Callback>(callback) is not { } target)
        {
            return PowerCallbacks.ErrNotCallback;
        }

        target.Notify(Alarmed() ? 1 : 0);
        Clocks.AlarmCallback = callback;
        return 0;
    }

    /// <summary>ksceRtcUnregisterCallback(callback): another one 0x80251004.</summary>
    [Hle(Nid = 0x9546C68B, Library = "SceRtcForDriver")]
    int ksceRtcUnregisterCallback(int callback)
    {
        if (Clocks.AlarmCallback != callback)
        {
            return RTC_ERR_NOT_REGISTERED;
        }

        Clocks.AlarmCallback = RtcClocks.NoCallback;
        return 0;
    }

    // ------------------------------------------------------------------ the secure alarm (a kernel timer)

    /// <summary>ksceRtcSetSecureAlarmTick(tick): on the secure clock (not set: 0x80251002). NULL or 0 clears it
    /// (the callback is notified 0); a tick already past notifies 1 at once; a later one rings at its time.</summary>
    [Hle(Nid = 0x8333790D, Library = "SceRtcForDriver")]
    int ksceRtcSetSecureAlarmTick(uint tick)
    {
        if (!Clocks.Valid[RtcClocks.Secure])
        {
            return RTC_ERR_NOT_SET;
        }

        ulong value = tick == 0 ? 0 : GuestMemory.Read<ulong>(tick);
        Clocks.SecureAlarmTick = value;
        if (value == 0)
        {
            NotifyCallback(Clocks.SecureAlarmCallback, 0);
            return 0;
        }

        if (value <= ClockTick(RtcClocks.Secure))
        {
            NotifyCallback(Clocks.SecureAlarmCallback, 1);
            return 0;
        }

        NotifyCallback(Clocks.SecureAlarmCallback, 0);
        TimerDispatcher.Arm(Alarm);
        return 0;
    }

    /// <summary>ksceRtcGetSecureAlarmTick(tick): the secure alarm (0: none); the secure clock not set 0x80251002.</summary>
    [Hle(Nid = 0xBD53731C, Library = "SceRtcForDriver")]
    int ksceRtcGetSecureAlarmTick(uint tick)
    {
        if (!Clocks.Valid[RtcClocks.Secure])
        {
            return RTC_ERR_NOT_SET;
        }

        GuestMemory.Write(tick, Clocks.SecureAlarmTick);
        return 0;
    }

    bool SecureAlarmed()
    {
        ulong alarm = Clocks.SecureAlarmTick;
        return alarm != 0 && ClockTick(RtcClocks.Secure) >= alarm;
    }

    /// <summary>ksceRtcIsSecureAlarmed(): the secure clock not set 0x80251002.</summary>
    [Hle(Nid = 0xE39F2ABE, Library = "SceRtcForDriver")]
    int ksceRtcIsSecureAlarmed()
    {
        if (!Clocks.Valid[RtcClocks.Secure])
        {
            return RTC_ERR_NOT_SET;
        }

        return SecureAlarmed() ? 1 : 0;
    }

    /// <summary>ksceRtcRegisterSecureAlarmCallback(callback): notified at once with whether the secure alarm rang;
    /// a second one 0x80251003.</summary>
    [Hle(Nid = 0xCF84DFC0, Library = "SceRtcForDriver")]
    int ksceRtcRegisterSecureAlarmCallback(int callback)
    {
        if (Clocks.SecureAlarmCallback >= 0)
        {
            return RTC_ERR_REGISTERED;
        }

        if (Emu.Kernel.Objects.Get<Callback>(callback) is not { } target)
        {
            return PowerCallbacks.ErrNotCallback;
        }

        target.Notify(Clocks.Valid[RtcClocks.Secure] && SecureAlarmed() ? 1 : 0);
        Clocks.SecureAlarmCallback = callback;
        return 0;
    }

    /// <summary>ksceRtcUnregisterSecureAlarmCallback(callback): another one 0x80251004.</summary>
    [Hle(Nid = 0xEDDC2325, Library = "SceRtcForDriver")]
    int ksceRtcUnregisterSecureAlarmCallback(int callback)
    {
        if (Clocks.SecureAlarmCallback != callback)
        {
            return RTC_ERR_NOT_REGISTERED;
        }

        Clocks.SecureAlarmCallback = RtcClocks.NoCallback;
        return 0;
    }

    void NotifyCallback(int uid, int argument)
    {
        if (uid >= 0 && Emu.Kernel.Objects.Get<Callback>(uid) is { } callback)
        {
            callback.Notify(argument);
        }
    }

    /// <summary>The alarms in guest time: the chip's (its alarm bit goes up, the callback is notified 1) and the
    /// secure one (its callback is notified 1), each once at its time.</summary>
    sealed class AlarmTimer(RtcKernel owner) : IGuestTimed
    {
        bool _alarmRang, _secureRang;
        ulong _alarmFor, _secureFor;

        public bool Armed { get; set; }

        /// <summary>The loaded clocks' alarms (the caller holds the clocks' lock): whether each had rung by the saved
        /// time.</summary>
        public void Restore(bool alarmRang, bool secureRang)
        {
            RtcClocks clocks = owner.Clocks;
            _alarmFor = clocks.AlarmTick;
            _alarmRang = alarmRang;
            _secureFor = clocks.SecureAlarmTick;
            _secureRang = secureRang;
        }

        public bool HasPending
        {
            get
            {
                RtcClocks clocks = owner.Clocks;
                bool alarm = clocks.AlarmTick != 0 && !(_alarmRang && _alarmFor == clocks.AlarmTick);
                bool secure = clocks.SecureAlarmTick != 0 && !(_secureRang && _secureFor == clocks.SecureAlarmTick);
                return alarm || secure;
            }
        }

        public ulong? FireDueAndRemaining()
        {
            RtcClocks clocks = owner.Clocks;
            ulong? left = null;
            lock (clocks)
            {
                left = Step(clocks.AlarmTick, ClockTick(clocks, RtcClocks.Main), ref _alarmFor, ref _alarmRang, left, () =>
                {
                    owner.Chip.StatusWord |= 1u << AlarmBit;
                    owner.NotifyCallback(clocks.AlarmCallback, 1);
                });
                left = Step(clocks.SecureAlarmTick, ClockTick(clocks, RtcClocks.Secure), ref _secureFor, ref _secureRang, left, () =>
                {
                    owner.NotifyCallback(clocks.SecureAlarmCallback, 1);
                });
            }

            return left;
        }

        static ulong? Step(ulong alarm, ulong now, ref ulong armedFor, ref bool rang, ulong? left, Action ring)
        {
            if (alarm == 0)
            {
                return left;
            }

            if (armedFor != alarm)
            {
                armedFor = alarm;
                rang = false;
            }

            if (rang)
            {
                return left;
            }

            if (now >= alarm)
            {
                rang = true;
                ring();
                return left;
            }

            ulong remaining = alarm - now;
            return left is { } other ? Math.Min(other, remaining) : remaining;
        }
    }
}
