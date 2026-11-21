using VitaSharp.Core.Kernel;

namespace VitaSharp.Core.SaveState;

/// <summary>
/// The owner of service threads: guest threads whose body is the emulator's own code (<see cref="GuestThread.HostBody"/>)
/// that a system module makes when it first needs one (the global work queue's worker, the interrupt thread). A save
/// state keeps them by their record (UID, stack, TLS); a load matches each saved one with the running session's thread
/// of the same name, and asks the owner for the rest:
/// <list type="bullet">
/// <item>a saved service thread the running session does not have (it was made after the moment this session is at, or
/// this session never needed it): the owner takes the restored record back and gives it its body, so the thread runs on
/// with the saved UID, as the firmware's thread would have;</item>
/// <item>a running service thread the state does not have (made after the save): the owner lets it go and makes a new
/// one the next time it needs one, as the firmware would from that point. The thread is asked to stop and must not take
/// any more work.</item>
/// </list>
/// The name is the identity of a service thread: it is fixed by the firmware (the thread the module makes there).
/// </summary>
internal interface IServiceThreadOwner
{
    /// <summary>The restored record of a saved service thread this session does not run: true when it is this owner's
    /// (the owner gave it its body and keeps it; the load starts it after everything else is restored).</summary>
    bool AdoptServiceThread(GuestThread thread);

    /// <summary>A running service thread the loaded state does not have: true when it is this owner's (the owner forgot
    /// it; the load asks it to stop).</summary>
    bool ReleaseServiceThread(GuestThread thread);
}
