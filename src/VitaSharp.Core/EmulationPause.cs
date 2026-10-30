using System.Diagnostics;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Util;

namespace VitaSharp.Core;

/// <summary>
/// Pauses the whole emulated system (the front-end's pause key, the Android pause menu, --start-paused), every process
/// at once:
/// <list type="bullet">
/// <item>every guest thread stops at its next safe point (the same parking as a suspended process, see
/// <see cref="GuestThread"/>), except the thread that has a GPU scene open, which runs on until it closes the scene so
/// the GPU finishes the frame it was drawing;</item>
/// <item>the guest clock stops (<see cref="GuestClock"/>): timeouts, vblanks, audio pacing and process time do not
/// advance, and on resume the guest sees no jump;</item>
/// <item>host devices follow through <see cref="Changed"/>: the audio output pauses (no underruns while the queue is
/// not fed), the renderer keeps presenting the last frame.</item>
/// </list>
/// Static like the guest clock it controls: one emulated system per host process.
/// </summary>
public static class EmulationPause
{
    static readonly object Lock = new();
    static volatile bool _paused;
    static long _pausedSince, _pausedTotal; // host Stopwatch ticks
    static long _guestAtPause, _framesAtPause;

    public static bool IsPaused => _paused;

    /// <summary>Raised after every change (true = paused), on the thread that made it. Handlers must not block.</summary>
    public static event Action<bool>? Changed;

    /// <summary>Stops the emulation. False when it was already paused.</summary>
    public static bool Pause(string reason = "")
    {
        lock (Lock)
        {
            if (_paused) return false;
            GuestClock.Freeze();
            _pausedSince = Stopwatch.GetTimestamp();
            _guestAtPause = GuestClock.Ticks;
            _framesAtPause = Hle.Modules.Display.FramesPresented;
            _paused = true;
        }
        GuestThread.InterruptAll();
        Log.Info($"Emulation paused{Why(reason)} at guest frame {Hle.Modules.Display.FramesPresented}, guest time {GuestClock.Seconds:F3} s");
        Raise(true);
        return true;
    }

    /// <summary>Lets the emulation run again. False when it was not paused.</summary>
    public static bool Resume(string reason = "")
    {
        double seconds, guestMs;
        long frames;
        lock (Lock)
        {
            if (!_paused) return false;
            long d = Stopwatch.GetTimestamp() - _pausedSince;
            _pausedTotal += d;
            seconds = d / (double)Stopwatch.Frequency;
            // what the guest saw of the pause: nothing, but frames of a scene being finished (normally 0)
            guestMs = (GuestClock.Ticks - _guestAtPause) * 1000.0 / Stopwatch.Frequency;
            frames = Hle.Modules.Display.FramesPresented - _framesAtPause;
            GuestClock.Thaw();
            _paused = false;
            Monitor.PulseAll(Lock);
        }
        GuestThread.WakeAll();
        Log.Info($"Emulation resumed{Why(reason)} after {seconds:F1} s paused (guest clock +{guestMs:F3} ms, frames +{frames} meanwhile), at guest frame {Hle.Modules.Display.FramesPresented}, guest time {GuestClock.Seconds:F3} s");
        Raise(false);
        return true;
    }

    /// <summary>Pauses a running emulation or resumes a paused one; returns the new state (true = paused).</summary>
    public static bool Toggle(string reason = "")
    {
        if (!Pause(reason)) Resume(reason);
        return _paused;
    }

    /// <summary>Host time spent paused so far (Stopwatch ticks), the current pause included.</summary>
    public static long PausedHostTicks
    {
        get
        {
            lock (Lock) return _pausedTotal + (_paused ? Stopwatch.GetTimestamp() - _pausedSince : 0);
        }
    }

    /// <summary>A host timestamp that stands still while the emulation is paused (Stopwatch ticks): statistics measured
    /// in host time (audio output gaps) do not count the pause.</summary>
    public static long ActiveHostTicks => Stopwatch.GetTimestamp() - PausedHostTicks;

    /// <summary>
    /// Blocks the calling thread while the emulation is paused (host-side waits on the guest clock: the clock is frozen,
    /// so they would never end, or end at once in turbo mode). A guest thread gives its emulated core away meanwhile.
    /// Returns at once for the guest thread that is finishing a GPU scene, and when the thread is asked to stop; the
    /// result is true when the emulation is still paused on return.
    /// </summary>
    public static bool WaitWhilePaused()
    {
        if (!_paused) return false;
        var self = GuestThread.Current;
        if (self != null && (self.FinishingScene || self.Core.StopRequested)) return true;
        bool hadCore = self is { HasCore: true, Core.InThrottle: false };
        if (hadCore) Scheduler.Release(self!);
        try
        {
            lock (Lock)
                while (_paused && self?.Core.StopRequested != true) Monitor.Wait(Lock, 50);
        }
        finally { if (hadCore) Scheduler.Acquire(self!); }
        return _paused;
    }

    static string Why(string reason) => reason.Length > 0 ? $" ({reason})" : "";

    static void Raise(bool paused)
    {
        if (Changed is not { } handlers) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action<bool>)h)(paused); }
            catch (Exception e) { Log.Warn($"Pause: a {(paused ? "pause" : "resume")} handler failed: {e.Message}"); }
        }
    }
}
