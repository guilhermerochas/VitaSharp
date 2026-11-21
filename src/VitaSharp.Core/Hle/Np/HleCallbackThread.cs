using System.Collections.Concurrent;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Np;

/// <summary>
/// A library's own callback thread: some system libraries (SceNpMatching2) call the game's callbacks from an internal
/// thread created at init, not from a CheckCallback poll. Posted callbacks run in order on a guest thread whose body is
/// host code (like the GXM display queue). <see cref="Sink"/> (tests) receives the calls instead.
/// </summary>
public sealed class HleCallbackThread(string name)
{
    /// <summary>A queued guest call: function, arguments, then cleanup on the host (e.g. free the response buffer).</summary>
    public sealed record Call(uint Func, uint[] Args, Action? After = null);

    readonly BlockingCollection<Call> _queue = new();
    GuestThread? _thread;
    volatile int _gen;

    /// <summary>Tests: when set, posted calls are delivered here synchronously and no guest thread is created.</summary>
    public Action<Call>? Sink { get; set; }

    /// <summary>Calls not yet delivered.</summary>
    public int Pending => _queue.Count;

    /// <summary>The name of the library's thread (its identity in a save state, SaveState.IServiceThreadOwner).</summary>
    public string Name => name;

    /// <summary>Called with the guest thread when it is made (the library writes its UID where the firmware keeps it).</summary>
    public Action<GuestThread>? Created { get; set; }

    /// <summary>The library's guest thread runs.</summary>
    public bool HasLiveThread
    {
        get
        {
            lock (_queue)
            {
                return _thread is { IsLive: true };
            }
        }
    }

    /// <summary>The guest thread, while it runs (save states: which callout context a kept thread serves).</summary>
    public GuestThread? Thread
    {
        get
        {
            lock (_queue)
            {
                return _thread;
            }
        }
    }

    public void Post(Emulator? emu, int priority, uint func, uint[] args, Action? after = null)
    {
        if (func == 0)
        {
            after?.Invoke();
            return;
        }

        var call = new Call(func, args, after);
        if (Sink != null)
        {
            Sink(call);
            after?.Invoke();
            return;
        }

        if (emu == null)
        {
            after?.Invoke();
            return;
        }

        lock (_queue)
        {
            if (_thread == null)
            {
                GuestThread thread = emu.Kernel.CreateThread(name, 0, priority, 0x4000, 0, 0);
                int gen = _gen;
                thread.HostBody = self => Body(emu, self, gen);
                _thread = thread;
                Created?.Invoke(thread);
                thread.Start(0, 0);
            }
        }

        _queue.Add(call);
    }

    /// <summary>
    /// Save states: the saved thread of a state, while this library runs none, becomes its thread again (it starts with
    /// the restored threads, with its saved UID); false when one runs already.
    /// </summary>
    internal bool Adopt(Emulator emu, GuestThread thread)
    {
        lock (_queue)
        {
            if (_thread is { IsLive: true } running && !ReferenceEquals(running, thread))
            {
                return false;
            }

            int gen = _gen;
            thread.HostBody = self => Body(emu, self, gen);
            _thread = thread;
        }

        return true;
    }

    /// <summary>Save states: the thread a loaded state does not have is let go (it takes no more calls and ends; the
    /// next post makes a new one); false when it is not this library's.</summary>
    internal bool Release(GuestThread thread)
    {
        lock (_queue)
        {
            if (!ReferenceEquals(thread, _thread))
            {
                return false;
            }

            _gen++;
            _thread = null;
        }

        return true;
    }

    /// <summary>Save states: the calls the running session posted are not the loaded state's (their buffers are in the
    /// running session's heap): a load drops them without their cleanup.</summary>
    internal void DropPending()
    {
        while (_queue.TryTake(out _))
        {
        }
    }

    void Body(Emulator emu, GuestThread self, int gen)
    {
        while (gen == _gen && !self.Core.StopRequested)
        {
            Call? call;
            using (Scheduler.Block()) // waiting for work: do not hold an emulated core
            {
                if (!_queue.TryTake(out call, 100))
                {
                    call = null;
                }
            }

            if (call == null)
            {
                if (!emu.Running && emu.MainModule != null)
                {
                    break;
                }

                continue;
            }

            if (gen != _gen || self.Core.StopRequested)
            {
                // let go by a state load while it waited: the call is not the loaded state's
                break;
            }

            try
            {
                self.CallGuest(call.Func, call.Args);
            }
            catch (GuestThreadExitException)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error($"{name}: callback 0x{call.Func:X8} failed: {e.Message}");
            }
            finally
            {
                call.After?.Invoke();
            }
        }
    }

    /// <summary>Library term: the thread ends; a later post (after a new init) starts a new one.</summary>
    public void Close()
    {
        lock (_queue)
        {
            _gen++;
            _thread = null;
        }
    }
}
