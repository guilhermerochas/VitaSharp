using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// An object of threadmgr's event class (FW 3.74: SceUIDEventClass, a child of SceUIDRegisterCallbackClass, the class
/// sceKernelRegisterCallbackToEvent looks the event up in, 0x8100cfcc): simple events, message pipes, timers, and the
/// event classes other modules derive through SceThreadmgrForDriver 0xDBB77032 (asynchronous I/O operations). Every one
/// keeps a list of registered callbacks, by callback UID (the same callback may be there more than once), and the
/// event's set (0x8101dcf8, which simple events, message pipes and timers all go through) notifies them, under the
/// object's lock, with a notify argument (0x8101cfcc).
/// </summary>
public abstract class EventObject : KernelObject
{
    /// <summary>One callback registered to the event, and the notifier ID it gets (the event's UID as the caller
    /// named it).</summary>
    private readonly record struct Registration(Callback Callback, int NotifyId);

    /// <summary>The callbacks registered to the event, by callback UID.</summary>
    private readonly List<Registration> _registrations = new();

    /// <summary>The registered callbacks of a loaded state, as (callback UID, notifier ID), until every object exists.</summary>
    private List<(int CallbackUid, int NotifyId)>? _loadedRegistrations;

    /// <summary>The sceKernelWaitMultipleEvents calls that read this event's state as a level (a pipe, a timer, an I/O
    /// operation; a simple event queues them instead).</summary>
    private List<MultiEventWait>? _watchers;

    internal void Watch(MultiEventWait multi)
    {
        lock (this)
        {
            (_watchers ??= new List<MultiEventWait>()).Add(multi);
        }
    }

    internal void Unwatch(MultiEventWait multi)
    {
        lock (this)
        {
            _watchers?.Remove(multi);
        }
    }

    /// <summary>The event's state changed: the calls that read it look again (the caller holds the object's lock).</summary>
    internal void PokeWatchers()
    {
        if (_watchers == null)
        {
            return;
        }

        foreach (MultiEventWait multi in _watchers)
        {
            multi.Poke();
        }
    }

    /// <summary>sceKernelRegisterCallbackToEvent (threadmgr 3.74, 0x8100cfcc): adds <paramref name="callback"/> to the
    /// event's list, after the registrations of callbacks with a lower or equal UID.</summary>
    public int RegisterCallback(Callback callback, int notifyId)
    {
        lock (this)
        {
            int index = 0;
            while (index < _registrations.Count && _registrations[index].Callback.Uid <= callback.Uid)
            {
                index++;
            }

            _registrations.Insert(index, new Registration(callback, notifyId));
            return SceError.OK;
        }
    }

    /// <summary>sceKernelUnregisterCallbackFromEvent (0x8100d108): removes the first registration of the callback, or
    /// CALLBACK_NOT_REGISTERED.</summary>
    public int UnregisterCallback(int callbackUid)
    {
        lock (this)
        {
            for (int i = 0; i < _registrations.Count; i++)
            {
                if (_registrations[i].Callback.Uid == callbackUid)
                {
                    _registrations.RemoveAt(i);
                    return SceError.OK;
                }
            }

            return SceError.CALLBACK_NOT_REGISTERED;
        }
    }

    /// <summary>sceKernelUnregisterCallbackFromEventAll (0x8100d1d0): removes every registration of the event.</summary>
    public int UnregisterAllCallbacks()
    {
        lock (this)
        {
            _registrations.Clear();
            return SceError.OK;
        }
    }

    /// <summary>The registered callbacks, for tests.</summary>
    internal int RegisteredCallbacks
    {
        get
        {
            lock (this)
            {
                return _registrations.Count;
            }
        }
    }

    /// <summary>The event's set notifies its registered callbacks (threadmgr 3.74, 0x8101cfcc; the caller holds the
    /// object's lock): each one in list order as sceKernelNotifyCallback does, with the registration's notifier ID and
    /// <paramref name="notifyArg"/>; a deleted callback's registration is dropped.</summary>
    protected void NotifyRegisteredCallbacks(int notifyArg)
    {
        for (int i = 0; i < _registrations.Count;)
        {
            Registration registration = _registrations[i];
            if (registration.Callback.Deleted)
            {
                _registrations.RemoveAt(i);
                continue;
            }

            registration.Callback.Notify(notifyArg, registration.NotifyId);
            i++;
        }
    }

    /// <summary>
    /// sceKernelSetEventWithNotifyCallback / sceKernelPulseEventWithNotifyCallback (threadmgr 3.74: the syscalls
    /// 0x118F646E / 0x3E49D3F1 copy {u64 userData, int notifyArg} in, 0x8102cf88 / 0x8102d040 refuse non-user bits
    /// (ILLEGAL_PATTERN), then the driver calls 0xB7DF3EDF / 0x714A107A look the UID up in SceUIDEventClass, so any
    /// object of the event class is accepted): a set pattern of 0 does nothing; otherwise, under the object's lock, the
    /// event set 0x8101dcf8 (pulse 0x8101e370) on the object's event and the notification of every registered callback
    /// in list order (0x8101cfcc), with the registration's notifier ID and <paramref name="notifyArg"/>.
    /// </summary>
    public int SetOrPulseWithNotify(uint setPattern, ulong userData, int notifyArg, bool pulse)
    {
        if ((setPattern & ~EventBits.UserMask) != 0)
        {
            return SceError.ILLEGAL_PATTERN;
        }

        if (setPattern == 0)
        {
            return SceError.OK;
        }

        lock (this)
        {
            SetForNotify(setPattern, userData, pulse);
            NotifyRegisteredCallbacks(notifyArg);
            return SceError.OK;
        }
    }

    /// <summary>The event set of <see cref="SetOrPulseWithNotify"/> on this object's event (the caller holds the lock).
    /// A message pipe or an asynchronous I/O operation keeps no user bits here: their waits look at their own state
    /// bits, so only the notification is seen (see the roadmap's sync item).</summary>
    protected virtual void SetForNotify(uint setPattern, ulong userData, bool pulse)
    {
    }

    /// <summary>Saves the registrations at the end of the object's record (the caller holds its lock).</summary>
    internal void SaveRegistrations(SnapshotWriter w)
    {
        w.Write(_registrations.Count);
        foreach (Registration registration in _registrations)
        {
            w.Write(registration.Callback.Uid);
            w.Write(registration.NotifyId);
        }
    }

    /// <summary>Reads the registrations at the end of a record; a record of a build before them has none (those builds
    /// kept none: [SnapshotMissing] for these hand-written records, the empty list).</summary>
    internal void LoadRegistrations(SnapshotReader r)
    {
        _registrations.Clear();
        int count = r.AtEnd ? 0 : r.ReadInt32();
        _loadedRegistrations = new List<(int, int)>(count);
        for (int i = 0; i < count; i++)
        {
            _loadedRegistrations.Add((r.ReadInt32(), r.ReadInt32()));
        }
    }

    /// <summary>Finds the registered callbacks (in the saved order); one missing from the state is dropped, as the
    /// kernel drops the registration of a deleted callback.</summary>
    internal void ResolveRegistrations(ObjectTable t)
    {
        if (_loadedRegistrations == null)
        {
            return;
        }

        foreach ((int callbackUid, int notifyId) in _loadedRegistrations)
        {
            if (t.Get<Callback>(callbackUid) is { } callback)
            {
                _registrations.Add(new Registration(callback, notifyId));
            }
        }
        _loadedRegistrations = null;
    }
}
