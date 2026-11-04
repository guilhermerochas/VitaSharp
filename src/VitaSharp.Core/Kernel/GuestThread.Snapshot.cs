using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// Save states (docs/notes/save-states.md): a guest thread is saved only at a safe point, where its whole state is
/// guest state:
/// <list type="bullet">
/// <item>parked at a block boundary at call depth 0 (the emulation is paused);</item>
/// <item>blocked inside an HLE wait at call depth 0: saved as "about to issue the call" (PC = the SVC, registers as at
/// the call), and the wait is issued again on load with its timeout restarted;</item>
/// <item>dormant (created and not started, or ended).</item>
/// </list>
/// A thread inside a guest callback (nested <see cref="CallGuest"/> frames) or busy in host code is not at a safe
/// point: the save resumes the emulation for a moment and retries.
/// </summary>
public sealed unsafe partial class GuestThread
{
    /// <summary>Call depth at which the thread is parked at a block boundary; -1 while it is not parked.</summary>
    volatile int _parkedDepth = -1;
    /// <summary>Incremented on entering and on leaving an HLE call: odd while inside one.</summary>
    volatile int _hleSeq;
    /// <summary>The mutex lock count a condition wait released (given back to the thread when its wait is reissued).</summary>
    internal int CondWaitCount;

    volatile bool _saveHold;

    /// <summary>A save state is being taken: the thread parks at its next block boundary at call depth 0 while the
    /// emulation (and the guest clock) still runs, so threads inside guest callbacks and module service threads
    /// (the GXM display queue waits for vblanks in its callback) get to a safe point. Not the thread that has a GPU
    /// scene open: it runs on until the scene ends.</summary>
    bool SaveHeld => _saveHold && _callDepth == 0 && !FinishingScene;

    internal void HoldForSave(bool hold)
    {
        lock (_suspendLock)
        {
            _saveHold = hold;
            Monitor.PulseAll(_suspendLock);
        }
        if (hold) Core.InterruptRequested = true;
    }

    /// <summary>The host thread is running (started and not ended).</summary>
    public bool IsLive => !_finished.IsSet;

    internal int HleSequence => _hleSeq;

    /// <summary>How this thread is saved.</summary>
    internal enum SafeKind { Dormant, Parked, Reissue, HostBody }

    /// <summary>Null when the thread is at a safe point (<paramref name="kind"/> says which), else why not.</summary>
    internal string? SafePointProblem(out SafeKind kind)
    {
        kind = SafeKind.Dormant;
        if (!IsLive) return null;
        if (HostBody != null)
        {
            kind = SafeKind.HostBody;
            return BlockedInHost && _callDepth == 0 ? null : $"thread '{Name}' (host) is busy (depth {_callDepth}, parked {_parkedDepth}, {Status}, in {InWhat()})";
        }
        int parked = _parkedDepth;
        if (parked == 0) { kind = SafeKind.Parked; return null; }
        if (parked > 0 || _callDepth > 0) return $"thread '{Name}' is inside a guest callback";
        if ((_hleSeq & 1) == 1 && (Status == ThreadStatus.Waiting || BlockedInHost))
        {
            var f = _emu.Hle.GetFunction(Core.State->SvcImm);
            // a condition wait that was signalled and now waits for its mutex: reissuing it would wait for a new signal
            if (CurrentWaitSync is Mutex && f.Name.Contains("Cond", StringComparison.Ordinal))
                return $"thread '{Name}' is taking back a mutex in {f.Name}";
            kind = SafeKind.Reissue;
            return null;
        }
        if ((_hleSeq & 1) == 1) return $"thread '{Name}' is busy in {_emu.Hle.GetFunction(Core.State->SvcImm).Name}";
        return $"thread '{Name}' is running guest code";
    }

    string InWhat() => (_hleSeq & 1) == 1 ? _emu.Hle.GetFunction(Core.State->SvcImm).Name : "guest code";

    internal void WriteSnapshot(SnapshotWriter w, SafeKind kind)
    {
        w.Write(Uid);
        w.Write(Name);
        w.Write(Entry);
        w.Write(InitPriority);
        w.Write(Priority);
        w.Write(Attr);
        w.Write(Affinity);
        w.Write(StackAddr);
        w.Write(StackSize);
        w.Write(TlsBase);
        w.Write((int)kind);
        w.Write(ExitStatus);
        w.Write(WakeupCount);
        w.Write(SignalPending);
        w.Write(PendingCallbacks);
        w.Write(SuspendCount);
        w.Write(OpenCount);
        w.Write(OwnerPid);
        // a reissued condition wait gives the mutex back first
        var cond = kind == SafeKind.Reissue ? CurrentWaitSync as CondVar : null;
        w.Write(cond?.Mutex.Uid ?? 0);
        w.Write(cond != null ? CondWaitCount : 0);
        WriteCpu(w, Core.State, kind == SafeKind.Reissue);
    }

    internal static void WriteCpu(SnapshotWriter w, CpuState* s, bool reissue)
    {
        for (int i = 0; i < 15; i++) w.Write(s->R[i]);
        w.Write(reissue ? s->SvcPc : s->R[15]);
        w.Write(s->N); w.Write(s->Z); w.Write(s->C); w.Write(s->V);
        w.Write(s->Q); w.Write(s->GE); w.Write(s->Thumb); w.Write(s->ITState);
        w.Write(s->Fpscr); w.Write(s->TpidrUro); w.Write(s->TpidrUrw);
        w.Write(s->ExclusiveAddr); w.Write(s->ExclusiveValue); w.Write(s->ExclusiveValid); w.Write(s->ExclusiveGen);
        for (int i = 0; i < 32; i++) w.Write(s->D[i]);
    }

    internal static void ReadCpu(SnapshotReader r, CpuState* s)
    {
        for (int i = 0; i < 16; i++) s->R[i] = r.ReadUInt32();
        s->N = r.ReadUInt32(); s->Z = r.ReadUInt32(); s->C = r.ReadUInt32(); s->V = r.ReadUInt32();
        s->Q = r.ReadUInt32(); s->GE = r.ReadUInt32(); s->Thumb = r.ReadUInt32(); s->ITState = r.ReadUInt32();
        s->Fpscr = r.ReadUInt32(); s->TpidrUro = r.ReadUInt32(); s->TpidrUrw = r.ReadUInt32();
        s->ExclusiveAddr = r.ReadUInt32(); s->ExclusiveValue = r.ReadUInt64();
        r.ReadUInt32(); // valid: a reservation does not survive the load (a spurious STREX failure is architecturally allowed)
        s->ExclusiveValid = 0;
        s->ExclusiveGen = r.ReadUInt32();
        for (int i = 0; i < 32; i++) s->D[i] = r.ReadUInt64();
        s->Exit = ExitReason.None;
    }

    /// <summary>A thread recreated from a save state (its stack and TLS are restored guest memory).</summary>
    GuestThread(Emulator emu, SnapshotReader r, out SafeKind kind, out int condMutex, out int condCount)
    {
        _emu = emu;
        Uid = r.ReadInt32();
        Name = r.ReadStringNotNull();
        Entry = r.ReadUInt32();
        InitPriority = r.ReadInt32();
        Priority = r.ReadInt32();
        Attr = r.ReadUInt32();
        Affinity = r.ReadInt32();
        StackAddr = r.ReadUInt32();
        StackSize = r.ReadUInt32();
        TlsBase = r.ReadUInt32();
        kind = (SafeKind)r.ReadInt32();
        ExitStatus = r.ReadInt32();
        WakeupCount = r.ReadInt32();
        SignalPending = r.ReadBool();
        PendingCallbacks = r.ReadInt32();
        SuspendCount = r.ReadInt32();
        OpenCount = r.ReadInt32();
        OwnerPid = r.ReadInt32();
        condMutex = r.ReadInt32();
        condCount = r.ReadInt32();
        OnSystemCore = emu.RunsOnSystemCore;
        Core = new CpuCore(emu.Code, emu.Space.Base);
        Hle = new HleContext { Cpu = Core.State, Thread = this, Emu = emu };
        ReadCpu(r, Core.State);
        Status = kind is SafeKind.Dormant ? ThreadStatus.Dormant : ThreadStatus.Ready;
    }

    internal static GuestThread ReadSnapshot(Emulator emu, SnapshotReader r, out SafeKind kind, out int condMutex, out int condCount)
        => new(emu, r, out kind, out condMutex, out condCount);

    /// <summary>Starts the host thread of a restored live thread: it runs on from the restored registers (parking first
    /// while the emulation is paused).</summary>
    internal void StartRestored()
    {
        lock (WaitLock)
        {
            Status = ThreadStatus.Running;
            _finished.Reset();
            Core.StopRequested = false;
            _host = new Thread(HostMain, 256 * 1024) { Name = $"Guest:{Name}", IsBackground = true };
            _host.Start();
        }
    }

    /// <summary>A host-body thread kept across a load takes the UID it had in the saved session.</summary>
    internal void Rekey(int uid) => Uid = uid;
}
