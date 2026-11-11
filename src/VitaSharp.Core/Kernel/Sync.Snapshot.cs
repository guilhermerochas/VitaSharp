using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Kernel;

// Save states: the state of each kernel object kind. Wait queues are not saved: a thread blocked in a wait is saved
// as about to issue the call again (GuestThread.Snapshot.cs), so every waiter count starts at 0 and refills when the
// restored threads reissue their waits. Thread references are UIDs, resolved once every object exists.

public sealed partial class Semaphore
{
    internal void Save(SnapshotWriter w)
    {
        w.Write(Attr);
        w.Write(InitCount);
        w.Write(Count);
        w.Write(MaxCount);
        w.Write(_deleted);
    }
    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        InitCount = r.ReadInt32();
        Count = r.ReadInt32();
        MaxCount = r.ReadInt32();
        _deleted = r.ReadBool();
    }
}

public sealed partial class EventFlag
{
    internal void Save(SnapshotWriter w)
    {
        w.Write(Attr);
        w.Write(InitPattern);
        w.Write(Pattern);
        w.Write(_deleted);
    }
    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        InitPattern = r.ReadUInt32();
        Pattern = r.ReadUInt32();
        _deleted = r.ReadBool();
    }
}

public sealed partial class Mutex
{
    internal int _ownerUid;
    internal void Save(SnapshotWriter w)
    {
        w.Write(Attr);
        w.Write(InitCount);
        w.WriteKernelObject(Owner);
        w.Write(LockCount);
        w.Write(WorkArea);
        w.Write(_deleted);
    }
    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        InitCount = r.ReadInt32();
        _ownerUid = r.ReadInt32();
        LockCount = r.ReadInt32();
        WorkArea = r.ReadUInt32();
        _deleted = r.ReadBool();
    }
    /// <summary>A lightweight mutex's owner and count are its guest work area's (restored with the memory): the owner
    /// word is rewritten without <see cref="Contended"/>, since no thread sleeps on it after a load.</summary>
    internal void Resolve(ObjectTable t)
    {
        if (IsLight)
        {
            Threads = t;
        }

        Owner = _ownerUid != 0 ? t.Get<GuestThread>(_ownerUid) : null;
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
            w.Write(_data.ToArray());
        }
    }

    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        BufferSize = r.ReadInt32();
        CreatorThreadUid = r.ReadInt32();
        _deleted = r.ReadBool();
        _data.Clear();
        foreach (byte b in r.ReadBytes() ?? [])
        {
            _data.Enqueue(b);
        }
    }
}

public sealed partial class RwLock
{
    internal int _writerUid;
    internal void Save(SnapshotWriter w)
    {
        w.Write(Attr);
        w.Write(Readers);
        w.WriteKernelObject(Writer);
        w.Write(WriteCount);
        w.Write(_deleted);
    }
    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        Readers = r.ReadInt32();
        _writerUid = r.ReadInt32();
        WriteCount = r.ReadInt32();
        _deleted = r.ReadBool();
    }
    internal void Resolve(ObjectTable t) => Writer = _writerUid != 0 ? t.Get<GuestThread>(_writerUid) : null;
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
    }
    internal void Resolve(ObjectTable t) => Owner = t.Get<GuestThread>(_ownerUid) ?? throw new SnapshotException($"callback {Name}: its thread 0x{_ownerUid:X} is missing");
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
    }
}

public sealed partial class SimpleEvent
{
    internal void Save(SnapshotWriter w)
    {
        w.Write(Attr);
        w.Write(Pattern);
        w.Write(LastUserData);
    }
    internal void Load(SnapshotReader r)
    {
        Attr = r.ReadUInt32();
        Pattern = r.ReadUInt32();
        LastUserData = r.ReadUInt64();
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

    /// <summary>Puts <paramref name="o"/> in the table under the UID it already has (save state load).</summary>
    internal void Put(KernelObject o) => _objects[o.Uid] = o;

    internal void Clear() => _objects.Clear();
}
