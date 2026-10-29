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

    public void Post(Emulator? emu, int priority, uint func, uint[] args, Action? after = null)
    {
        if (func == 0) { after?.Invoke(); return; }
        var call = new Call(func, args, after);
        if (Sink != null) { Sink(call); after?.Invoke(); return; }
        if (emu == null) { after?.Invoke(); return; }
        lock (_queue)
        {
            if (_thread == null)
            {
                var t = emu.Kernel.CreateThread(name, 0, priority, 0x4000, 0, 0);
                int gen = _gen;
                t.HostBody = self => Body(emu, self, gen);
                _thread = t;
                t.Start(0, 0);
            }
        }
        _queue.Add(call);
    }

    void Body(Emulator emu, GuestThread self, int gen)
    {
        while (gen == _gen)
        {
            Call? call;
            using (Scheduler.Block()) // waiting for work: do not hold an emulated core
                if (!_queue.TryTake(out call, 100)) call = null;
            if (call == null)
            {
                if (!emu.Running && emu.MainModule != null) break;
                continue;
            }
            try { self.CallGuest(call.Func, call.Args); }
            catch (GuestThreadExitException) { break; }
            catch (Exception e) { Log.Error($"{name}: callback 0x{call.Func:X8} failed: {e.Message}"); }
            finally { call.After?.Invoke(); }
        }
    }

    /// <summary>Library term: the thread ends; a later post (after a new init) starts a new one.</summary>
    public void Close()
    {
        lock (_queue) { _gen++; _thread = null; }
    }
}
