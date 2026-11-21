using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Kernel;

// Save states: the state of each kernel object kind. Wait queues are not saved (except a semaphore's, below): a thread
// blocked in a wait is saved as about to issue the call again (GuestThread.Snapshot.cs), so every waiter count starts
// at 0 and refills when the restored threads reissue their waits. Thread references are UIDs, resolved once every
// object exists.

// A semaphore keeps its wait queue: the order a signal serves the waiters (and the waiters a signal already served
// that had not returned) is the saved session's, not the order the restored threads happen to reissue their waits in.
public sealed partial class Semaphore
{
    internal void Save(SnapshotWriter w)
    {
        lock (this)
        {
            int count = Count;
            List<WaitRequest> kept = new();
            foreach (WaitRequest request in _granted)
            {
                if (request.ThreadUid != 0)
                {
                    kept.Add(request);
                }
                else
                {
                    count += request.Need; // not a guest thread: nothing reissues it, the units go back
                }
            }
            foreach (WaitRequest request in _queue)
            {
                if (request.ThreadUid != 0)
                {
                    kept.Add(request);
                }
            }
            w.Write(Attr);
            w.Write(InitCount);
            w.Write(count);
            w.Write(MaxCount);
            w.Write(_deleted);
            // added 16/11/2026; states of builds before it have no queue: their waits are reissued as new ones, as
            // those builds did ([SnapshotMissing] for this hand-written record: the empty queue)
            w.Write(kept.Count);
            foreach (WaitRequest request in kept)
            {
                w.Write(request.ThreadUid);
                w.Write(request.Need);
                w.Write(request.Priority);
                w.Write(request.Granted);
            }
        }
    }

    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        InitCount = r.ReadInt32();
        Count = r.ReadInt32();
        MaxCount = r.ReadInt32();
        _deleted = r.ReadBool();
        _queue.Clear();
        _granted.Clear();
        _restored.Clear();
        Waiters = 0;
        int saved = r.AtEnd ? 0 : r.ReadInt32();
        for (int i = 0; i < saved; i++)
        {
            WaitRequest request = new()
            {
                ThreadUid = r.ReadInt32(),
                Need = r.ReadInt32(),
                Priority = r.ReadInt32(),
                Granted = r.ReadBool(),
            };
            _restored.Add(request);
            if (request.Granted)
            {
                _granted.Add(request);
            }
            else
            {
                _queue.Add(request); // saved in queue order
                Waiters++;
            }
        }
    }
}

public sealed partial class EventFlag
{
    internal void Save(SnapshotWriter w)
    {
        w.Write(Attr);
        w.Write(InitPattern);
        w.Write(SnapshotPattern);
        w.Write(_deleted);
    }

    /// <summary>The pattern a save state keeps: waits are reissued when the state loads, so a waiter that a set satisfied
    /// but that has not returned yet gives back the bits its clear mode took, and takes them again on the reissue.</summary>
    internal uint SnapshotPattern
    {
        get
        {
            lock (this)
            {
                uint pattern = Pattern;
                foreach (WaitRequest request in _granted)
                {
                    pattern |= request.Consumed;
                }

                return pattern;
            }
        }
    }

    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        InitPattern = r.ReadUInt32();
        Pattern = r.ReadUInt32();
        _deleted = r.ReadBool();
    }
}

// A normal mutex keeps its wait queue as a semaphore does: the order an unlock hands it over in, and the waiters made
// the owner that had not returned (their reissued lock returns OK instead of finding themselves the owner).
public sealed partial class Mutex
{
    internal int _ownerUid;
    internal void Save(SnapshotWriter w)
    {
        lock (this)
        {
            // a condition wait handed its mutex back that had not returned is saved as the cond wait it reissues, which
            // takes the mutex itself: the mutex is saved free
            GuestThread? owner = Owner;
            bool reacquiring = owner != null && _granted.Exists(request => request.Reacquire && request.Thread == owner);
            w.Write(Attr);
            w.Write(InitCount);
            w.WriteKernelObject(reacquiring ? null : Owner);
            w.Write(reacquiring ? 0 : LockCount);
            w.Write(WorkArea);
            w.Write(_deleted);
            // added 16/11/2026; states of builds before it have no queue: their locks are reissued as new ones, as
            // those builds did ([SnapshotMissing] for this hand-written record: the empty queue)
            int kept = 0;
            foreach (WaitRequest request in _granted)
            {
                kept += Kept(request) ? 1 : 0;
            }

            foreach (WaitRequest request in _queue)
            {
                kept += Kept(request) ? 1 : 0;
            }

            w.Write(kept);
            foreach (List<WaitRequest> list in (List<WaitRequest>[])[_granted, _queue])
            {
                foreach (WaitRequest request in list)
                {
                    if (!Kept(request))
                    {
                        continue;
                    }

                    w.Write(request.ThreadUid);
                    w.Write(request.Count);
                    w.Write(request.Priority);
                    w.Write(request.Granted);
                }
            }
        }
    }

    /// <summary>A waiter the saved queue keeps: a guest thread's own lock (not a cond wait's, which is reissued).</summary>
    private static bool Kept(WaitRequest request) => request.ThreadUid != 0 && !request.Reacquire;

    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        InitCount = r.ReadInt32();
        _ownerUid = r.ReadInt32();
        LockCount = r.ReadInt32();
        WorkArea = r.ReadUInt32();
        _deleted = r.ReadBool();
        _queue.Clear();
        _granted.Clear();
        _restored.Clear();
        Waiters = 0;
        int saved = r.AtEnd ? 0 : r.ReadInt32();
        for (int i = 0; i < saved; i++)
        {
            WaitRequest request = new()
            {
                ThreadUid = r.ReadInt32(),
                Count = r.ReadInt32(),
                Priority = r.ReadInt32(),
                Granted = r.ReadBool(),
            };
            _restored.Add(request);
            if (request.Granted)
            {
                _granted.Add(request);
            }
            else
            {
                _queue.Add(request); // saved in queue order
                Waiters++;
            }
        }
    }

    /// <summary>A lightweight mutex's owner and count are its guest work area's (restored with the memory): the owner
    /// word is rewritten, flagged <see cref="Contended"/> only when the saved queue has waiters (the owner's unlock then
    /// comes to the kernel to hand it over). The waiters of the saved queue get their threads back (an unlock may hand
    /// them the mutex before they reissue).</summary>
    internal void Resolve(ObjectTable t)
    {
        if (IsLight)
        {
            Threads = t;
        }

        Owner = _ownerUid != 0 ? t.Get<GuestThread>(_ownerUid) : null;
        if (IsLight && _queue.Count > 0 && _ownerUid != 0)
        {
            Memory.GuestMemory.W32(WorkArea + LwOwnerOffset, (uint)_ownerUid | Contended);
        }

        foreach (WaitRequest request in _restored)
        {
            request.Thread = t.Get<GuestThread>(request.ThreadUid);
        }
    }
}

public sealed partial class CondVar
{
    internal int _mutexUid;
    internal void Save(SnapshotWriter w)
    {
        w.Write(Attr);
        w.WriteKernelObject(Mutex);
        w.Write(WorkArea);
        w.Write(_deleted);
    }
    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        _mutexUid = r.ReadInt32();
        WorkArea = r.ReadUInt32();
        _deleted = r.ReadBool();
    }
    internal void Resolve(ObjectTable t) => Mutex = t.Get<Mutex>(_mutexUid) ?? throw new SnapshotException($"condition variable {Name}: its mutex 0x{_mutexUid:X} is missing");
}

// A message pipe keeps its buffer and its wait queue: the order the pipe serves its waiters, how far each one got,
// and the waiters served or released that had not returned. A waiter's buffers are guest addresses, so a waiter the
// pipe reaches before its thread reissues the call is still served from them.
public sealed partial class MsgPipe
{
    internal void Save(SnapshotWriter w)
    {
        w.Write(Attr);
        w.Write(BufferSize);
        w.Write(CreatorThreadUid);
        w.Write(_deleted);
        lock (this)
        {
            byte[] held = new byte[_held];
            for (int i = 0; i < _held; i++)
            {
                held[i] = _ring![(_readPos + i) % BufferSize];
            }

            w.Write(held);
            // added 18/11/2026; states of builds before it have no queue: their calls are reissued as new ones, as
            // those builds did ([SnapshotMissing] for this hand-written record: the empty queue)
            List<Transfer> kept = new();
            foreach (Transfer transfer in _ended)
            {
                if (transfer.ThreadUid != 0)
                {
                    kept.Add(transfer);
                }
            }
            foreach (Transfer transfer in _queue)
            {
                if (transfer.ThreadUid != 0 || transfer.Completed != null)
                {
                    kept.Add(transfer);
                }
            }
            w.Write(kept.Count);
            foreach (Transfer transfer in kept)
            {
                w.Write(transfer.ThreadUid);
                w.Write(transfer.Completed != null);
                w.Write(transfer.Send);
                w.Write(transfer.Full);
                w.Write(transfer.Peek);
                w.Write(transfer.Priority);
                w.Write(transfer.Total);
                w.Write(transfer.Left);
                w.Write(transfer.Served);
                w.Write(transfer.Error);
                w.Write(transfer.Result.Address);
                w.Write(transfer.Buffers.Addrs.Length);
                for (int i = 0; i < transfer.Buffers.Addrs.Length; i++)
                {
                    w.Write(transfer.Buffers.Addrs[i]);
                    w.Write(transfer.Buffers.Sizes[i]);
                }
            }
            w.Write(_headPartial);
            // added 19/11/2026: the callbacks registered to the pipe (older states load with none)
            SaveRegistrations(w);
            w.Write(CorePattern);
            w.Write(CoreUserData);
        }
    }

    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        BufferSize = r.ReadInt32();
        CreatorThreadUid = r.ReadInt32();
        _deleted = r.ReadBool();
        byte[] held = r.ReadBytes() ?? [];
        _ring = held.Length > 0 ? new byte[BufferSize] : null;
        held.CopyTo(_ring.AsSpan());
        _readPos = 0;
        _held = held.Length;
        _writePos = BufferSize > 0 ? _held % BufferSize : 0;
        _queue.Clear();
        _ended.Clear();
        _restored.Clear();
        _headPartial = false;
        int saved = r.AtEnd ? 0 : r.ReadInt32();
        for (int i = 0; i < saved; i++)
        {
            var transfer = new Transfer { ThreadUid = r.ReadInt32() };
            bool later = r.ReadBool();
            transfer.Send = r.ReadBool();
            transfer.Full = r.ReadBool();
            transfer.Peek = r.ReadBool();
            transfer.Priority = r.ReadInt32();
            transfer.Total = r.ReadInt32();
            transfer.Left = r.ReadInt32();
            transfer.Served = r.ReadBool();
            transfer.Error = r.ReadInt32();
            transfer.Result = new Memory.Ptr<uint>(r.ReadUInt32());
            int count = r.ReadInt32();
            uint[] addrs = new uint[count];
            int[] sizes = new int[count];
            for (int j = 0; j < count; j++)
            {
                addrs[j] = r.ReadUInt32();
                sizes[j] = r.ReadInt32();
            }

            transfer.Buffers = new Segments(addrs, sizes);
            if (later)
            {
                transfer.Completed = NoCompletion; // the process's count of queued requests starts again at 0
                _queue.Add(transfer);
                continue;
            }

            _restored.Add(transfer);
            if (transfer.Served || transfer.Error != 0)
            {
                _ended.Add(transfer);
            }
            else
            {
                _queue.Add(transfer); // saved in queue order
            }
        }
        _headPartial = !r.AtEnd && r.ReadBool();
        LoadRegistrations(r);
        // added 21/11/2026: the pipe's event pattern and user data; a state of a build before it read the pipe's
        // state as a level ([SnapshotMissing] for this hand-written record: IN and "can receive" while bytes wait)
        if (r.AtEnd)
        {
            CorePattern = _held > 0 ? EventIn | EventReceivable : 0;
            CoreUserData = 0;
        }
        else
        {
            CorePattern = r.ReadUInt32();
            CoreUserData = r.ReadUInt64();
        }

        CoreDeleted = _deleted;
    }

    /// <summary>A restored DONT_WAIT request's end: nothing to count.</summary>
    private static void NoCompletion()
    {
    }
}

// A rw lock keeps its wait queue, as the mutex does: the order its hand-over serves the waiters (and the waiters given
// the lock that had not returned) is the saved session's.
public sealed partial class RwLock
{
    internal int _writerUid;
    internal void Save(SnapshotWriter w)
    {
        lock (this)
        {
            int count = LockCount;
            List<WaitRequest> kept = new();
            foreach (List<WaitRequest> list in (List<WaitRequest>[])[_granted, _queue])
            {
                foreach (WaitRequest request in list)
                {
                    if (request.ThreadUid != 0)
                    {
                        kept.Add(request);
                    }
                    else if (request.Granted && !request.Write)
                    {
                        count--; // not a guest thread: nothing reissues it, its read lock goes back
                    }
                }
            }
            w.Write(Attr);
            // the record of the builds before the queue: the readers' count, the writer and its count apart
            w.Write(Writer == null ? count : 0);
            w.WriteKernelObject(Writer);
            w.Write(Writer != null ? count : 0);
            w.Write(_deleted);
            // added 19/11/2026; states of builds before it have no queue: their locks are reissued as new ones, as
            // those builds did ([SnapshotMissing] for this hand-written record: the empty queue)
            w.Write(kept.Count);
            foreach (WaitRequest request in kept)
            {
                w.Write(request.ThreadUid);
                w.Write(request.Write);
                w.Write(request.Priority);
                w.Write(request.Granted);
            }
        }
    }

    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        int readers = r.ReadInt32();
        _writerUid = r.ReadInt32();
        int writeCount = r.ReadInt32();
        _deleted = r.ReadBool();
        LockCount = _writerUid != 0 ? writeCount : readers;
        _queue.Clear();
        _granted.Clear();
        _restored.Clear();
        ReadWaiters = 0;
        WriteWaiters = 0;
        int saved = r.AtEnd ? 0 : r.ReadInt32();
        for (int i = 0; i < saved; i++)
        {
            WaitRequest request = new()
            {
                ThreadUid = r.ReadInt32(),
                Write = r.ReadBool(),
                Priority = r.ReadInt32(),
                Granted = r.ReadBool(),
            };
            _restored.Add(request);
            if (request.Granted)
            {
                _granted.Add(request);
            }
            else
            {
                _queue.Add(request); // saved in queue order
                if (request.Write)
                {
                    WriteWaiters++;
                }
                else
                {
                    ReadWaiters++;
                }
            }
        }
    }

    /// <summary>The writer and the waiters of the saved queue get their threads back (an unlock may hand them the lock
    /// before they reissue).</summary>
    internal void Resolve(ObjectTable t)
    {
        Writer = _writerUid != 0 ? t.Get<GuestThread>(_writerUid) : null;
        foreach (WaitRequest request in _restored)
        {
            request.Thread = t.Get<GuestThread>(request.ThreadUid);
        }
    }
}

public sealed partial class Callback
{
    internal int _ownerUid;
    internal void Save(SnapshotWriter w)
    {
        w.WriteKernelObject(Owner);
        w.Write(Function);
        w.Write(Arg);
        w.Write(Attr);
        w.Write(NotifyCount);
        w.Write(NotifyArg);
        w.Write(NotifyId);
        // added 19/11/2026; states of builds before it load as not deleted (they freed a callback at once)
        w.Write(Deleted);
    }
    internal void Load(SnapshotReader r)
    {
        _ownerUid = r.ReadInt32();
        Function = r.ReadUInt32();
        Arg = r.ReadUInt32();
        Attr = r.ReadUInt32();
        NotifyCount = r.ReadInt32();
        NotifyArg = r.ReadInt32();
        NotifyId = r.ReadInt32();
        Deleted = !r.AtEnd && r.ReadBool();
    }

    /// <summary>Finds the owner and takes the callback's place in its list (creation order: the UIDs').</summary>
    internal void Resolve(ObjectTable t)
    {
        Owner = t.Get<GuestThread>(_ownerUid) ?? throw new SnapshotException($"callback {Name}: its thread 0x{_ownerUid:X} is missing");
        Owner.AddCallback(this);
    }
}

public sealed partial class TimerObject
{
    internal void Save(SnapshotWriter w)
    {
        w.Write(Attr);
        w.Write(Started);
        w.Write(Repeat);
        w.Write(Pulse);
        w.Write(EventSet);
        w.Write(Base);
        w.Write(StoppedCount);
        w.Write(Interval);
        w.Write(NextEvent);
        w.Write(_deleted);
        // added 19/11/2026: the callbacks registered to the timer (older states load with none), then the user bits and
        // user data of its event (sync8; older states load with none)
        lock (this)
        {
            SaveRegistrations(w);
            w.Write(UserPattern);
            w.Write(LastUserData);
        }
    }

    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        Started = r.ReadBool();
        Repeat = r.ReadBool();
        Pulse = r.ReadBool();
        EventSet = r.ReadBool();
        Base = r.ReadUInt64();
        StoppedCount = r.ReadUInt64();
        Interval = r.ReadUInt64();
        NextEvent = r.ReadUInt64();
        _deleted = r.ReadBool();
        LoadRegistrations(r);
        if (!r.AtEnd)
        {
            UserPattern = r.ReadUInt32();
            LastUserData = r.ReadUInt64();
        }
    }
}

public sealed partial class SimpleEvent
{
    internal void Save(SnapshotWriter w)
    {
        lock (this)
        {
            w.Write(Attr);
            w.Write(Pattern);
            w.Write(LastUserData);
            // added 19/11/2026; states of builds before it load with no callback registered (those builds kept none)
            SaveRegistrations(w);
        }
    }

    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        Pattern = r.ReadUInt32();
        LastUserData = r.ReadUInt64();
        LoadRegistrations(r);
    }
}

public sealed partial class ThreadEventHandler
{
    internal void Save(SnapshotWriter w)
    {
        w.Write(ThreadMask);
        w.Write(EventMask);
        w.Write(Handler);
        w.Write(Common);
    }
    internal void Load(SnapshotReader r)
    {
        ThreadMask = r.ReadInt32();
        EventMask = r.ReadUInt32();
        Handler = r.ReadUInt32();
        Common = r.ReadUInt32();
    }
}

public sealed partial class ObjectTable
{
    internal int NextUid { get => Volatile.Read(ref _next); set => Volatile.Write(ref _next, value); }

    /// <summary>Every object, by UID order.</summary>
    internal List<KernelObject> Snapshot() => _objects.Values.OrderBy(o => o.Uid).ToList();

    /// <summary>Puts <paramref name="kernelObject"/> in the table under the UID it already has (save state load).</summary>
    internal void Put(KernelObject kernelObject) => _objects[kernelObject.Uid] = kernelObject;

    internal void Clear() => _objects.Clear();
}
