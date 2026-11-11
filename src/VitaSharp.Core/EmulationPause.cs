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
    private static readonly object s_lock = new();
    private static volatile bool s_paused;
    /// <summary>The pause is the emulator's own (a save state being taken or loaded), not the player's.</summary>
    private static volatile bool s_internal;
    private static long s_pausedSince, s_pausedTotal; // host Stopwatch ticks
    private static long s_guestAtPause, s_framesAtPause;

    public static bool IsPaused => s_paused;
    /// <summary>Paused by the player (pause key, menu, --start-paused): the front-ends show the "Pausado" badge only
    /// then, not while a save state stops the game for a moment.</summary>
    public static bool IsUserPaused => s_paused && !s_internal;

    /// <summary>Raised after every change (true = paused), on the thread that made it. Handlers must not block.</summary>
    public static event Action<bool>? Changed;

    /// <summary>Stops the emulation. False when it was already paused (a player's pause during an internal one makes it
    /// the player's). <paramref name="internal"/>: the emulator's own short pause (save states).</summary>
    public static bool Pause(string reason = "", bool @internal = false)
    {
        bool promoted = false;
        lock (s_lock)
        {
            if (s_paused)
            {
                if (!@internal && s_internal)
                {
                    s_internal = false;
                    promoted = true;
                }
                if (!promoted)
                {
                    return false;
                }
            }
        }
        if (promoted)
        {
            Raise(true);
            return false;
        }
        lock (s_lock)
        {
            if (s_paused)
            {
                return false;
            }

            s_internal = @internal;
            GuestClock.Freeze();
            s_pausedSince = Stopwatch.GetTimestamp();
            s_guestAtPause = GuestClock.Ticks;
            s_framesAtPause = Hle.Modules.Display.FramesPresented;
            s_paused = true;
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
        lock (s_lock)
        {
            if (!s_paused)
            {
                return false;
            }

            long d = Stopwatch.GetTimestamp() - s_pausedSince;
            s_pausedTotal += d;
            seconds = d / (double)Stopwatch.Frequency;
            // what the guest saw of the pause: nothing, but frames of a scene being finished (normally 0)
            guestMs = (GuestClock.Ticks - s_guestAtPause) * 1000.0 / Stopwatch.Frequency;
            frames = Hle.Modules.Display.FramesPresented - s_framesAtPause;
            GuestClock.Thaw();
            s_paused = false;
            s_internal = false;
            Monitor.PulseAll(s_lock);
        }
        GuestThread.WakeAll();
        Log.Info($"Emulation resumed{Why(reason)} after {seconds:F1} s paused (guest clock +{guestMs:F3} ms, frames +{frames} meanwhile), at guest frame {Hle.Modules.Display.FramesPresented}, guest time {GuestClock.Seconds:F3} s");
        Raise(false);
        return true;
    }

    /// <summary>Pauses a running emulation or resumes a paused one; returns the new state (true = paused).</summary>
    public static bool Toggle(string reason = "")
    {
        if (!Pause(reason))
        {
            Resume(reason);
        }

        return s_paused;
    }

    /// <summary>Host time spent paused so far (Stopwatch ticks), the current pause included.</summary>
    public static long PausedHostTicks
    {
        get
        {
            lock (s_lock)
            {
                return s_pausedTotal + (s_paused ? Stopwatch.GetTimestamp() - s_pausedSince : 0);
            }
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
        if (!s_paused)
        {
            return false;
        }

        GuestThread? self = GuestThread.Current;
        if (self != null && (self.FinishingScene || self.Core.StopRequested))
        {
            return true;
        }

        bool hadCore = self is { HasCore: true, Core.InThrottle: false };
        if (hadCore)
        {
            Scheduler.Release(self!);
        }

        try
        {
            lock (s_lock)
            {
                while (s_paused && self?.Core.StopRequested != true)
                {
                    Monitor.Wait(s_lock, 50);
                }
            }
        }
        finally
        {
            if (hadCore)
            {
                Scheduler.Acquire(self!);
            }
        }
        return s_paused;
    }

    private static string Why(string reason) => reason.Length > 0 ? $" ({reason})" : "";

    private static void Raise(bool paused)
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (Delegate h in handlers.GetInvocationList())
        {
            try
            {
                ((Action<bool>)h)(paused);
            }
            catch (Exception e)
            {
                Log.Warn($"Pause: a {(paused ? "pause" : "resume")} handler failed: {e.Message}");
            }
        }
    }
}
