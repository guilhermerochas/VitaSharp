namespace VitaSharp.Core.Kernel;

/// <summary>
/// Models the Vita's CPU cores: at most <see cref="Cores"/> guest threads execute at any moment, chosen by priority
/// (lower number = higher priority, strict: a runnable higher-priority thread preempts a lower one at its next check).
/// Guest threads are host threads; a thread holds a "core" while it runs guest code or non-blocking HLE, and releases
/// it while blocked in a kernel wait or sleep. Without this, every guest thread runs truly in parallel on the host,
/// and games whose job systems rely on the real scheduling (3 cores, priorities) hit races that never occur on
/// hardware.
/// </summary>
public static class Scheduler
{
    /// <summary>Cores available to the application (the Vita reserves the 4th for the system). 0 = unlimited.</summary>
    public static int Cores = 3;

    static readonly object L = new();
    static int _running;
    static readonly List<GuestThread> _waiting = new();

    /// <summary>Blocks until the calling thread may run guest code.</summary>
    public static void Acquire(GuestThread t)
    {
        if (Cores <= 0 || t.HasCore) return;
        lock (L)
        {
            bool queued = false;
            while (true)
            {
                if (_running < Cores && !HigherPriorityWaiting(t.Priority))
                {
                    if (queued) _waiting.Remove(t);
                    _running++;
                    t.HasCore = true;
                    t.Core.ResetThrottle();
                    return;
                }
                if (!queued) { _waiting.Add(t); queued = true; }
                Monitor.Wait(L, 2);
            }
        }
    }

    public static void Release(GuestThread t)
    {
        if (!t.HasCore) return;
        if (t == GuestThread.Current) { t.Core.SettleThrottle(t.Core.PendingInstructions); t.Core.PendingInstructions = 0; }
        lock (L)
        {
            _running--;
            t.HasCore = false;
            Monitor.PulseAll(L);
        }
    }

    /// <summary>Called periodically by a running thread: a higher-priority thread is waiting for its core.</summary>
    public static bool ShouldYield(GuestThread t)
    {
        if (Cores <= 0 || _waiting.Count == 0) return false;
        lock (L) return HigherPriorityWaiting(t.Priority) || (_running >= Cores && SamePriorityWaiting(t.Priority));
    }

    /// <summary>Gives the core away (if anyone of higher or equal priority wants it) and takes one again.</summary>
    public static void Yield(GuestThread t)
    {
        if (!ShouldYield(t)) return;
        Release(t);
        Thread.Yield();
        Acquire(t);
    }

    static bool HigherPriorityWaiting(int priority)
    {
        foreach (var w in _waiting) if (w.Priority < priority) return true;
        return false;
    }

    static bool SamePriorityWaiting(int priority)
    {
        foreach (var w in _waiting) if (w.Priority == priority) return true;
        return false;
    }

    /// <summary>Forgets all core bookkeeping (process exec after every thread has exited).</summary>
    public static void Reset() { lock (L) { _running = 0; _waiting.Clear(); Monitor.PulseAll(L); } }

    /// <summary>Releases the current thread's core for the duration of a blocking host wait.</summary>
    public static Blocked Block() => new(GuestThread.Current);

    public readonly struct Blocked : IDisposable
    {
        readonly GuestThread? _t;
        public Blocked(GuestThread? t) { _t = t; if (t != null) { t.BlockedInHost = true; Release(t); } }
        public void Dispose() { if (_t != null) { Acquire(_t); _t.BlockedInHost = false; } }
    }
}
