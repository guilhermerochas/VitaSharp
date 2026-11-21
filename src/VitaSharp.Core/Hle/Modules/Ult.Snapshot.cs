using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Save states of the SceUlt fallback. Its objects are kernel objects (KernelSnapshot saves each one as an "ult" record:
/// its kind, address, users and fields, the objects it points at by UID, resolved once the table is restored). The
/// module itself keeps the threads inside a condition wait (<see cref="_condWaits"/>), which a load reissues: libult's
/// wait released the mutex before the save, so the reissued call must not ask for it again (the thread no longer owns
/// it), and a thread that was signalled waits only for the mutex.
/// </summary>
[GenerateSnapshot]
public sealed partial class Ult
{
    /// <summary>A thread inside sceUltConditionVariableWait: the condition's UID, the mutex depth it released, and
    /// whether its signal came.</summary>
    internal readonly record struct CondWait(int Cond, int Depth, bool Signalled);

    /// <summary>The threads inside a condition wait, by thread UID (a loaded state's: their reissued wait goes on from
    /// where it was saved).</summary>
    [SnapshotMissing] // states of builds before it: no Ult object could be saved
    readonly Dictionary<int, CondWait> _condWaits = new();

    /// <summary>The saved part of a reissued condition wait of <paramref name="thread"/> on <paramref name="cond"/>.</summary>
    bool TakeCondReissue(GuestThread thread, UCond cond, out CondWait wait)
    {
        lock (_condWaits)
        {
            if (_condWaits.TryGetValue(thread.Uid, out wait) && wait.Cond == cond.Uid)
            {
                return true;
            }
        }

        wait = default;
        return false;
    }

    /// <summary>Tests: whether <paramref name="thread"/> is inside a wait on the condition at <paramref name="cv"/>, and
    /// whether its signal came.</summary>
    internal (bool Inside, bool Signalled) CondWaitOf(uint cv, GuestThread thread)
    {
        if (Find<UCond>(cv, out _) is not { } cond)
        {
            return (false, false);
        }

        lock (_condWaits)
        {
            return _condWaits.TryGetValue(thread.Uid, out CondWait wait) && wait.Cond == cond.Uid ? (true, wait.Signalled) : (false, false);
        }
    }

    /// <summary>Which guest thread is which ulthread follows the restored objects.</summary>
    void AfterSnapshotLoad()
    {
        _byThread.Clear();
        foreach (KernelObject kernelObject in K.Objects.Snapshot())
        {
            if (kernelObject is Ulthread ulthread)
            {
                _byThread.AddOrUpdate(ulthread.Thread, ulthread);
            }
        }
    }
    internal enum UltKind : byte
    {
        WaitingPool = 1,
        DataPool,
        Queue,
        Mutex,
        Cond,
        Sema,
        RwLock,
        Runtime,
        Ulthread,
    }

    /// <summary>A saved SceUlt object (KernelSnapshot's "ult" record), its references resolved by
    /// <see cref="UltObject.Resolve"/>.</summary>
    internal static UltObject LoadObject(SnapshotReader r, string name)
    {
        var kind = (UltKind)r.ReadByte();
        UltObject ultObject = kind switch
        {
            UltKind.WaitingPool => new WaitingPool(),
            UltKind.DataPool => new DataPool(),
            UltKind.Queue => new UQueue(),
            UltKind.Mutex => new UMutex(),
            UltKind.Cond => new UCond(),
            UltKind.Sema => new USema(),
            UltKind.RwLock => new URwLock(),
            UltKind.Runtime => new Runtime(),
            UltKind.Ulthread => new Ulthread(),
            _ => throw new SnapshotException($"unknown SceUlt object kind {(byte)kind}"),
        };
        ultObject.Name = name;
        ultObject.Address = r.ReadUInt32();
        ultObject.Users = r.ReadInt32();
        ultObject.LoadBody(r);
        return ultObject;
    }

    static T ResolveUid<T>(ObjectTable table, int uid, UltObject from) where T : KernelObject
    {
        return table.Get<T>(uid) ?? throw new SnapshotException($"SceUlt object {from.Name} points at 0x{uid:X}, which is not in the state");
    }

    static T? ResolveOptionalUid<T>(ObjectTable table, int uid, UltObject from) where T : KernelObject
    {
        return uid == 0 ? null : ResolveUid<T>(table, uid, from);
    }

    internal abstract partial class UltObject
    {
        internal abstract UltKind Kind { get; }

        internal void Save(SnapshotWriter w)
        {
            w.Write((byte)Kind);
            w.Write(Address);
            w.Write(SavedUsers());
            SaveBody(w);
        }

        /// <summary>The users count as the state keeps it.</summary>
        internal virtual int SavedUsers()
        {
            return Users;
        }

        internal abstract void SaveBody(SnapshotWriter w);

        internal abstract void LoadBody(SnapshotReader r);

        /// <summary>The objects this one points at, from the restored table.</summary>
        internal virtual void Resolve(ObjectTable table)
        {
        }
    }

    sealed partial class WaitingPool
    {
        internal override UltKind Kind => UltKind.WaitingPool;

        internal override void SaveBody(SnapshotWriter w)
        {
            w.Write(NumThreads);
            w.Write(NumSync);
        }

        internal override void LoadBody(SnapshotReader r)
        {
            NumThreads = r.ReadUInt32();
            NumSync = r.ReadUInt32();
        }
    }

    sealed partial class DataPool
    {
        private int _waitingUid;

        internal override UltKind Kind => UltKind.DataPool;

        internal override void SaveBody(SnapshotWriter w)
        {
            w.Write(NumData);
            w.Write(DataSize);
            w.Write(NumQueues);
            w.Write(UsedData);
            w.WriteKernelObject(Waiting);
        }

        internal override void LoadBody(SnapshotReader r)
        {
            NumData = r.ReadUInt32();
            DataSize = r.ReadUInt32();
            NumQueues = r.ReadUInt32();
            UsedData = r.ReadUInt32();
            _waitingUid = r.ReadInt32();
        }

        internal override void Resolve(ObjectTable table)
        {
            Waiting = ResolveOptionalUid<WaitingPool>(table, _waitingUid, this);
        }
    }

    sealed partial class UQueue
    {
        private int _poolUid;
        private int _waitingUid;

        internal override UltKind Kind => UltKind.Queue;

        internal override void SaveBody(SnapshotWriter w)
        {
            w.Write(DataSize);
            w.WriteKernelObject(Pool);
            w.WriteKernelObject(Waiting);
            lock (Lock)
            {
                w.Write(Items.Count);
                foreach (byte[] item in Items)
                {
                    w.Write(item);
                }
            }
        }

        internal override void LoadBody(SnapshotReader r)
        {
            DataSize = r.ReadUInt32();
            _poolUid = r.ReadInt32();
            _waitingUid = r.ReadInt32();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                Items.Enqueue(r.ReadBytes() ?? []);
            }
        }

        internal override void Resolve(ObjectTable table)
        {
            Pool = ResolveUid<DataPool>(table, _poolUid, this);
            Waiting = ResolveUid<WaitingPool>(table, _waitingUid, this);
        }
    }

    sealed partial class UMutex
    {
        private int _ownerUid;
        private int _waitingUid;

        internal override UltKind Kind => UltKind.Mutex;

        internal override void SaveBody(SnapshotWriter w)
        {
            w.WriteKernelObject(Owner);
            w.Write(Count);
            w.Write(Recursive);
            w.WriteKernelObject(Waiting);
        }

        internal override void LoadBody(SnapshotReader r)
        {
            _ownerUid = r.ReadInt32();
            Count = r.ReadInt32();
            Recursive = r.ReadBool();
            _waitingUid = r.ReadInt32();
        }

        internal override void Resolve(ObjectTable table)
        {
            Owner = ResolveOptionalUid<GuestThread>(table, _ownerUid, this);
            Waiting = ResolveUid<WaitingPool>(table, _waitingUid, this);
        }
    }

    sealed partial class UCond
    {
        private int _mutexUid;

        internal override UltKind Kind => UltKind.Cond;

        /// <summary>Without the threads waiting for the signal: their reissued waits count themselves again.</summary>
        internal override int SavedUsers()
        {
            return Users - Volatile.Read(ref SignalWaiters);
        }

        internal override void SaveBody(SnapshotWriter w)
        {
            w.Write(Sequence);
            w.WriteKernelObject(Mutex);
        }

        internal override void LoadBody(SnapshotReader r)
        {
            Sequence = r.ReadInt64();
            _mutexUid = r.ReadInt32();
        }

        internal override void Resolve(ObjectTable table)
        {
            Mutex = ResolveUid<UMutex>(table, _mutexUid, this);
        }
    }

    sealed partial class USema
    {
        private int _waitingUid;

        internal override UltKind Kind => UltKind.Sema;

        internal override void SaveBody(SnapshotWriter w)
        {
            w.Write(Count);
            w.WriteKernelObject(Waiting);
        }

        internal override void LoadBody(SnapshotReader r)
        {
            Count = r.ReadInt32();
            _waitingUid = r.ReadInt32();
        }

        internal override void Resolve(ObjectTable table)
        {
            Waiting = ResolveUid<WaitingPool>(table, _waitingUid, this);
        }
    }

    sealed partial class URwLock
    {
        private int _writerUid;
        private int _waitingUid;

        internal override UltKind Kind => UltKind.RwLock;

        internal override void SaveBody(SnapshotWriter w)
        {
            w.Write(Readers);
            w.WriteKernelObject(Writer);
            w.WriteKernelObject(Waiting);
        }

        internal override void LoadBody(SnapshotReader r)
        {
            Readers = r.ReadInt32();
            _writerUid = r.ReadInt32();
            _waitingUid = r.ReadInt32();
        }

        internal override void Resolve(ObjectTable table)
        {
            Writer = ResolveOptionalUid<GuestThread>(table, _writerUid, this);
            Waiting = ResolveUid<WaitingPool>(table, _waitingUid, this);
        }
    }

    sealed partial class Runtime
    {
        internal override UltKind Kind => UltKind.Runtime;

        internal override void SaveBody(SnapshotWriter w)
        {
            w.Write(MaxUlthreads);
            w.Write(Workers);
            w.Write(Live);
        }

        internal override void LoadBody(SnapshotReader r)
        {
            MaxUlthreads = r.ReadUInt32();
            Workers = r.ReadUInt32();
            Live = r.ReadInt32();
        }
    }

    sealed partial class Ulthread
    {
        private int _threadUid;
        private int _runtimeUid;

        internal override UltKind Kind => UltKind.Ulthread;

        internal override void SaveBody(SnapshotWriter w)
        {
            w.WriteKernelObject(Thread);
            w.WriteKernelObject(Runtime);
        }

        internal override void LoadBody(SnapshotReader r)
        {
            _threadUid = r.ReadInt32();
            _runtimeUid = r.ReadInt32();
        }

        internal override void Resolve(ObjectTable table)
        {
            Thread = ResolveUid<GuestThread>(table, _threadUid, this);
            Runtime = ResolveUid<Runtime>(table, _runtimeUid, this);
        }
    }
}
