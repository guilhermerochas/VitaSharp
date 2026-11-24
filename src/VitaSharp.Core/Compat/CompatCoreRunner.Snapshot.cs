using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Compat;

/// <summary>
/// Save states (docs/notes/save-states.md): the core's time (its cycles and the time it spent halted) and its
/// interrupt line as the mailbox drives it and as the core last saw it. A save is taken while the core's thread is
/// parked between two slices (<see cref="GuestThread.ParksAtSafePoints"/>), so no halt is being counted then. The core's
/// thread is a service thread of the process (<see cref="IServiceThreadOwner"/>, through SceCompat): a load into a
/// session without a running core gives the saved thread its body again (<see cref="Adopt"/>), one into a session whose
/// core the state does not have lets the running one go (<see cref="Release"/>).
/// </summary>
[GenerateSnapshot, SnapshotIgnoreFields("_hardware", "_thread", "_stopRequested", "_haltStartUs", "_progressWaiters", "_failure")]
public sealed partial class CompatCoreRunner
{
    /// <summary>Why the runner cannot be saved now (its core is being stopped), or null.</summary>
    internal string? SnapshotProblem
    {
        get
        {
            lock (_lock)
            {
                return _stopRequested ? "the PSP compat core is stopping" : null;
            }
        }
    }

    /// <summary>The restored core's thread of a loaded state: it runs this runner's core from where the state left it
    /// (the load starts it once everything is restored).</summary>
    internal void Adopt(GuestThread thread)
    {
        thread.KernelMode = true;
        thread.HostBody = Body;
        thread.ParksAtSafePoints = true;
        lock (_lock)
        {
            _stopRequested = false;
            _haltStartUs = -1;
        }

        _thread = thread;
    }

    /// <summary>The running core's thread, which the loaded state does not have: it runs no more slices and ends (the
    /// load asks it to stop).</summary>
    internal void Release()
    {
        lock (_lock)
        {
            _stopRequested = true;
            Monitor.PulseAll(_lock);
        }

        _thread = null;
    }

    /// <summary>The core's thread: this runner's.</summary>
    internal bool Owns(GuestThread thread)
    {
        return ReferenceEquals(_thread, thread);
    }
}
