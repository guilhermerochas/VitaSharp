using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The thread manager's alarms as the FW 3.74 threadmgr.skprx has them (decompiled, study only).
/// ksceKernelRegisterTimer (a 32-bit delay) and NID_10517330 (a 64-bit one) make an alarm object
/// ("SceKernelAlarmObject" without a name) due that many microseconds of system time from now, with a handler and its
/// argument, and answer its UID. The firmware keeps the alarms in time order and programs its timer (the SceLT5
/// device) for the earliest; its interrupt (line 0x8D, "SceThreadmgrTimer") calls each due alarm's handler with the
/// argument in interrupt context: 0 ends the alarm (its object is deleted), anything else is the delay until it fires
/// again (at least 200 us), counted from the time it was due. NID_44CCF310 cancels an alarm that has not fired (its
/// object is deleted; 0x800280C2 from the alarm's own handler, 0x800280C1 for a UID that is not an alarm).
/// <para>
/// VitaSharp arms the same line of its interrupt manager (<see cref="SystemServicesVsh.ArmThreadmgrTimer"/>) for the
/// earliest alarm, so the handlers run on the interrupt thread at their guest time. An alarm is a kernel data object of
/// the kernel process (handler, argument); its due time is kept here.
/// </para>
/// </summary>
public sealed partial class ThreadmgrKernel : ISnapshotTimers
{
    /// <summary>A state load: the timer line is armed for the loaded state's earliest alarm (or left disarmed).</summary>
    void ISnapshotTimers.RearmTimers()
    {
        bool any;
        lock (_alarmDue)
        {
            any = _alarmDue.Count > 0;
        }

        if (any)
        {
            ArmAlarmTimer();
        }

        RunLoadedWorkTasks();
    }

    const string AlarmKind = "alarm";

    const string DefaultAlarmName = "SceKernelAlarmObject";

    /// <summary>The least delay before an alarm fires again.</summary>
    const ulong MinAlarmDelay = 200;

    /// <summary>Cancelling the alarm whose handler runs.</summary>
    const int AlarmRunning = unchecked((int)0x800280C2);

    /// <summary>The due time (guest microseconds of system time) of each pending alarm, by UID.</summary>
    [SnapshotMissing] // states of builds before it: no alarm had been registered
    readonly Dictionary<int, ulong> _alarmDue = new();

    /// <summary>The alarm whose handler runs now (0: none).</summary>
    [SnapshotIgnore]
    int _alarmRunning;

    SystemServicesVsh Intrmgr => Emu.Hle.Modules.OfType<SystemServicesVsh>().First();

    /// <summary>ksceKernelRegisterTimer(name, delay, handler, argument): an alarm <paramref name="delay"/> microseconds
    /// from now; a null name or handler is SCE_KERNEL_ERROR_ILLEGAL_ADDR.</summary>
    [Hle(Nid = 0xC58DF384, Library = "SceThreadmgrForDriver")]
    int ksceKernelRegisterTimer(uint name, uint delay, uint handler, uint argument)
    {
        if (name == 0 || handler == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        return RegisterAlarm(name, delay, handler, argument);
    }

    /// <summary>NID_10517330(name, SceUInt64 *delay, handler, argument): the same with a 64-bit delay.</summary>
    [Hle(Nid = 0x10517330, Library = "SceThreadmgrForDriver")]
    int NID_10517330(uint name, uint delay, uint handler, uint argument)
    {
        if (name == 0 || delay == 0 || handler == 0)
        {
            return SceError.ILLEGAL_ADDR;
        }

        return RegisterAlarm(name, Memory.GuestMemory.Read<ulong>(delay), handler, argument);
    }

    int RegisterAlarm(uint name, ulong delay, uint handler, uint argument)
    {
        string text = name == 0 ? DefaultAlarmName : Memory.GuestMemory.ReadCString(name, 0x20);
        var alarm = new KernelData { Name = text, Kind = AlarmKind, Address = handler, Aux = argument };
        int uid;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            uid = K.Objects.Register(alarm);
        }

        lock (_alarmDue)
        {
            _alarmDue[uid] = K.ProcessTimeUs + delay;
        }

        ArmAlarmTimer();
        return uid;
    }

    /// <summary>NID_44CCF310(SceUID alarm): cancels an alarm that has not fired.</summary>
    [Hle(Nid = 0x44CCF310, Library = "SceThreadmgrForDriver")]
    int NID_44CCF310(int uid)
    {
        if (SystemServicesVsh.InInterrupt && Volatile.Read(ref _alarmRunning) == uid)
        {
            return AlarmRunning;
        }

        if (K.Objects.Get<KernelData>(uid) is not { Kind: AlarmKind })
        {
            return UnknownAlarmId;
        }

        bool pending;
        lock (_alarmDue)
        {
            pending = _alarmDue.Remove(uid);
        }

        if (pending)
        {
            K.Objects.Remove(uid);
            ArmAlarmTimer();
        }

        return 0;
    }

    /// <summary>Arms the timer line for the earliest pending alarm (nothing when none is left).</summary>
    void ArmAlarmTimer()
    {
        ulong earliest = ulong.MaxValue;
        lock (_alarmDue)
        {
            foreach (ulong due in _alarmDue.Values)
            {
                earliest = Math.Min(earliest, due);
            }
        }

        if (earliest == ulong.MaxValue)
        {
            Intrmgr.DisarmThreadmgrTimer();
        }
        else
        {
            Intrmgr.ArmThreadmgrTimer(earliest, AlarmInterrupt);
        }
    }

    /// <summary>The timer interrupt (line 0x8D, on the interrupt thread): calls every due alarm's handler, earliest first,
    /// then arms the line for the next one.</summary>
    void AlarmInterrupt(HleContext context)
    {
        ulong now = K.ProcessTimeUs;
        while (true)
        {
            int uid = 0;
            ulong due = ulong.MaxValue;
            lock (_alarmDue)
            {
                foreach ((int pending, ulong time) in _alarmDue)
                {
                    if (time <= now && time < due)
                    {
                        uid = pending;
                        due = time;
                    }
                }

                if (uid == 0)
                {
                    break;
                }

                _alarmDue.Remove(uid);
            }

            if (K.Objects.Get<KernelData>(uid) is not { Kind: AlarmKind } alarm)
            {
                continue;
            }

            Volatile.Write(ref _alarmRunning, uid);
            uint again;
            try
            {
                again = context.Thread.CallGuest(alarm.Address, alarm.Aux);
            }
            finally
            {
                Volatile.Write(ref _alarmRunning, 0);
            }

            if (again == 0)
            {
                K.Objects.Remove(uid);
                continue;
            }

            lock (_alarmDue)
            {
                _alarmDue[uid] = due + Math.Max(again, MinAlarmDelay);
            }
        }

        ArmAlarmTimer();
    }

    /// <summary>The alarm class of the debugger lists: SceKernelAlarmInfo (0x38): +0x28 the due time (64 bits), +0x30
    /// the handler, +0x34 its argument.</summary>
    ObjectClass AlarmClass => new(0x38, static o => o is KernelData { Kind: AlarmKind }, AlarmInfo);

    int AlarmInfo(int uid, uint info)
    {
        if (K.Objects.Get<KernelData>(uid) is not { Kind: AlarmKind } alarm)
        {
            return UnknownAlarmId;
        }

        Span<byte> fields = stackalloc byte[0x38];
        fields.Clear();
        KernelInfo.Header(fields, uid, alarm.Name, 0);
        KernelInfo.Put64(fields, 0x28, AlarmDue(uid) ?? 0);
        KernelInfo.Put(fields, 0x30, alarm.Address);
        KernelInfo.Put(fields, 0x34, alarm.Aux);
        return KernelInfo.Write(info, fields);
    }

    /// <summary>The pending alarms' due times (tests).</summary>
    internal ulong? AlarmDue(int uid)
    {
        lock (_alarmDue)
        {
            return _alarmDue.TryGetValue(uid, out ulong due) ? due : null;
        }
    }
}
