using VitaSharp.Core.Compat;
using VitaSharp.Core.Compat.Allegrex;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Save states (docs/notes/save-states.md): SceCompat's own state (the window, the owner, the core's state word and
/// handshake, the request rings, the semaphores, the dat block, the framebuffer and the scratch blocks), the PSP side it
/// drives (the shared SRAM, the mailbox, the bus's banks, the PSP hardware with its GE, the LCD DMA), the compat core
/// (the Allegrex's whole state) and its runner (the core's time and interrupt line). The core is saved between two of
/// its slices: its thread ("SceCompatCore") parks there for the save like a guest thread. A load runs the loaded core:
/// in place when the running session's core thread is the saved one's, else a new core (and runner, whose saved thread
/// SceCompat takes back, <see cref="IServiceThreadOwner"/>); a running core the state does not have is let go.
/// A core of the tests (no save state of its own) refuses the save.
/// </summary>
[GenerateSnapshot, SnapshotIgnoreFields("_core", "_runner", "CoreFactory")]
public sealed partial class Compat : IServiceThreadOwner
{
    /// <summary>Save states: one channel's ring.</summary>
    [GenerateSnapshot]
    sealed partial class RequestRing
    {
    }

    string? ISnapshotState.CheckSnapshot()
    {
        ICompatCore? core = _core;
        return core != null && core is not ISnapshotState ? $"the PSP compat core ({core.GetType().Name}) has no save state" : null;
    }

    void SaveExtra(SnapshotWriter w)
    {
        CompatCoreRunner? runner = _runner;
        if (runner?.SnapshotProblem is { } problem)
        {
            throw new SnapshotRetryException(problem);
        }

        w.Write(_core as ISnapshotState);
        w.Write(runner is { Running: true } ? runner : null);
    }

    void LoadExtra(SnapshotReader r)
    {
        if (r.ReadBool())
        {
            SnapshotReader core = r.ReadBlock();

            // the running session's core thread is the saved one's: it runs the loaded state on the same core
            if (_runner == null || _core is not ISnapshotState)
            {
                _core = CoreFactory(_bus);
            }

            if (_core is not ISnapshotState state)
            {
                throw new SnapshotException("the PSP compat core of this session has no save state");
            }

            state.LoadState(core);
        }
        else
        {
            _core = null;
        }

        if (r.ReadBool())
        {
            SnapshotReader runner = r.ReadBlock();
            if (_core == null)
            {
                throw new SnapshotException("the state runs a PSP compat core it does not have");
            }

            _runner ??= new CompatCoreRunner(Emu, _core, AllegrexCore.InterruptControllerLine, CompatClockMhz, _hardware);
            ((ISnapshotState)_runner).LoadState(runner);
        }
        else
        {
            _runner = null;
        }
    }

    /// <summary>Save states: the core's thread of a state that ran the core, while this session runs none, runs the
    /// loaded core again with its saved UID (LoadExtra made the runner).</summary>
    bool IServiceThreadOwner.AdoptServiceThread(GuestThread thread)
    {
        CompatCoreRunner? runner = _runner;
        if (!ReferenceEquals(thread.Process, Emu) || thread.Name != CompatCoreRunner.ThreadName || runner == null || runner.Running)
        {
            return false;
        }

        runner.Adopt(thread);
        return true;
    }

    /// <summary>Save states: the core's thread of this session, which the loaded state does not have (the core was
    /// started after the save, or stopped in it), is let go: the core stops there.</summary>
    bool IServiceThreadOwner.ReleaseServiceThread(GuestThread thread)
    {
        CompatCoreRunner? runner = _runner;
        if (runner == null || !runner.Owns(thread))
        {
            return false;
        }

        runner.Release();
        _runner = null;
        return true;
    }
}
