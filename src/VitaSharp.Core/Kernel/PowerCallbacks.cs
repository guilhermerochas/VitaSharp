namespace VitaSharp.Core.Kernel;

/// <summary>The power callback bits of ScePower (vita-headers psp2common/power.h, ScePowerCallbackType).</summary>
public static class PowerEvents
{
    public const uint AfterSystemResume = 0x00000080;
    public const uint BatteryOnline = 0x00000100;
    public const uint PowerOnline = 0x00001000;
    public const uint SystemSuspend = 0x00010000;
    public const uint SystemResuming = 0x00020000;
    public const uint SystemResume = 0x00040000;
    public const uint AppResume = 0x00200000;
    public const uint AppSuspend = 0x00400000;
    public const uint ButtonPsStartPress = 0x04000000;
    public const uint ButtonPsPowerPress = 0x08000000;
    public const uint ButtonPsHold = 0x10000000;
    public const uint ButtonPsPress = 0x20000000;
    public const uint ButtonPowerHold = 0x40000000;
    public const uint ButtonPowerPress = 0x80000000;

    /// <summary>What the kernel and system programs are notified of.</summary>
    public const uint SystemMask = 0xFCF71F80;

    /// <summary>What other processes (games, homebrew) are notified of: no button and no suspend bits.</summary>
    public const uint NonSystemMask = 0x00361180;
}

/// <summary>
/// The power callbacks of ScePower (FW 3.74, os0:kd/bootimage.skprx), shared by every process of the system:
/// <list type="bullet">
/// <item>scePowerRegisterCallback(cb): a UID that is not a callback is 0xC00280A1; a callback already registered
/// 0x802B0001. The registrant's mask is <see cref="PowerEvents.SystemMask"/> for system programs, else
/// <see cref="PowerEvents.NonSystemMask"/>. The callback is notified at once with the current state bits (battery
/// online; power online only with the AC adapter).</item>
/// <item>scePowerUnregisterCallback(cb): not a callback 0xC00280A1, not registered 0x802B0002.</item>
/// <item>A change (<see cref="Post"/>: state bits cleared and set, one-shot events) notifies every registrant with
/// state | events | the events it has not consumed yet (its callback has not run since: notify count above 0),
/// masked by its mask. A registrant whose callback is gone is dropped.</item>
/// </list>
/// </summary>
public sealed class PowerCallbacks
{
    internal const int ErrNotCallback = unchecked((int)0xC00280A1);
    internal const int ErrAlreadyRegistered = unchecked((int)0x802B0001);
    internal const int ErrNotRegistered = unchecked((int)0x802B0002);

    private sealed class Registrant
    {
        public required Emulator Process;
        public required int CallbackUid;
        public required uint Mask;
        /// <summary>Events notified that the callback has not consumed yet.</summary>
        public uint Pending;
    }

    private readonly object _lock = new();
    private readonly List<Registrant> _registrants = [];

    /// <summary>The state bits every notification carries: the battery is in and, as the syscon chip starts (a retail
    /// unit without its cable, <see cref="SysconChip.AcAdapter"/>), no AC adapter.</summary>
    public uint State { get; private set; } = PowerEvents.BatteryOnline;

    /// <summary>A system program (the shell) listens: the buttons' system reactions (standby, power menu) are its.</summary>
    public bool HasSystemRegistrant
    {
        get
        {
            lock (_lock)
            {
                foreach (Registrant registrant in _registrants)
                {
                    if (registrant.Mask == PowerEvents.SystemMask && registrant.Process.Running)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    public int Register(Emulator process, int callbackUid)
    {
        if (process.Kernel.Objects.Get<Callback>(callbackUid) is not { } callback)
        {
            return ErrNotCallback;
        }

        uint mask = process.Access.IsSystemProgram ? PowerEvents.SystemMask : PowerEvents.NonSystemMask;
        lock (_lock)
        {
            if (Find(process, callbackUid) >= 0)
            {
                return ErrAlreadyRegistered;
            }

            callback.Notify((int)(State & mask));
            _registrants.Insert(0, new Registrant { Process = process, CallbackUid = callbackUid, Mask = mask });
        }
        return 0;
    }

    public int Unregister(Emulator process, int callbackUid)
    {
        if (process.Kernel.Objects.Get<Callback>(callbackUid) == null)
        {
            return ErrNotCallback;
        }

        lock (_lock)
        {
            int index = Find(process, callbackUid);
            if (index < 0)
            {
                return ErrNotRegistered;
            }

            _registrants.RemoveAt(index);
        }
        return 0;
    }

    /// <summary>A process ended: its registrations go with it.</summary>
    public void RemoveProcess(Emulator process)
    {
        lock (_lock)
        {
            _registrants.RemoveAll(r => ReferenceEquals(r.Process, process));
        }
    }

    /// <summary>The state changes (<paramref name="clear"/> then <paramref name="set"/>) with the one-shot
    /// <paramref name="events"/>, and every registrant is notified.</summary>
    public void Post(uint clear, uint set, uint events)
    {
        lock (_lock)
        {
            State = set | (~clear & State);
            for (int i = _registrants.Count - 1; i >= 0; i--)
            {
                Registrant registrant = _registrants[i];
                Callback? callback = registrant.Process.Running ? registrant.Process.Kernel.Objects.Get<Callback>(registrant.CallbackUid) : null;
                if (callback == null)
                {
                    _registrants.RemoveAt(i);
                    continue;
                }

                uint unconsumed = callback.NotifyCount > 0 ? registrant.Pending & ~clear : 0;
                registrant.Pending = unconsumed | events;
                callback.Notify((int)((State | unconsumed | events) & registrant.Mask));
            }
        }
    }

    private int Find(Emulator process, int callbackUid)
    {
        for (int i = 0; i < _registrants.Count; i++)
        {
            if (ReferenceEquals(_registrants[i].Process, process) && _registrants[i].CallbackUid == callbackUid)
            {
                return i;
            }
        }

        return -1;
    }
}
