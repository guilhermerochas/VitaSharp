using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: matching contexts and their callbacks (the online session itself is offline/mock).</summary>
[GenerateSnapshot, SnapshotIgnoreFields("MemoryOverride")]
public sealed partial class NpMatching2 : IServiceThreadOwner
{
    [GenerateSnapshot]
    sealed partial class Context;

    /// <summary>The calls the running session posted are not the loaded state's (<see cref="HleCallbackThread.DropPending"/>).</summary>
    void AfterSnapshotLoad()
    {
        _callbacks.DropPending();
    }

    /// <summary>Save states: the saved callback thread, while this session runs none, is the library's again (with its
    /// saved UID). Matched by name: the fixed one the library makes it with, a service thread's identity in a state.</summary>
    bool IServiceThreadOwner.AdoptServiceThread(GuestThread thread)
    {
        return ReferenceEquals(thread.Process, Emu) && thread.Name == _callbacks.Name && _callbacks.Adopt(Emu, thread);
    }

    /// <summary>Save states: the callback thread made after the save is let go (the next callback makes a new one).</summary>
    bool IServiceThreadOwner.ReleaseServiceThread(GuestThread thread)
    {
        return _callbacks.Release(thread);
    }
}

/// <summary>Save states: the contexts (bind, peer, mux mode) and the polls with their entries. Offline no context
/// ever reaches a peer, so there is no network session to recreate; a thread waiting in sceRudpPollWait is saved
/// before the call and waits again after the load (the count of waits in progress is not state of the game).</summary>
[GenerateSnapshot]
public sealed partial class Rudp
{
    /// <summary>Nothing to recreate: the contexts and polls are loaded with the fields.</summary>
    void LoadExtra(SnapshotReader r)
    {
    }

    /// <summary>No wait is in progress at a load: the threads that waited issue sceRudpPollWait again.</summary>
    void AfterSnapshotLoad()
    {
        lock (_lock)
        {
            _blockedPollWaits = 0;
        }
    }
}
