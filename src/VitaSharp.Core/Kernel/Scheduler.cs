namespace VitaSharp.Core.Kernel;

/// <summary>
/// Models the Vita's CPU cores: at most <see cref="Cores"/> guest threads execute at any moment, chosen by priority
/// (lower number = higher priority, strict: a runnable higher-priority thread preempts a lower one at its next check).
/// Guest threads are host threads; a thread holds a "core" while it runs guest code or non-blocking HLE, and releases
/// it while blocked in a kernel wait or sleep. Without this, every guest thread runs truly in parallel on the host,
/// and games whose job systems rely on the real scheduling (3 cores, priorities) hit races that never occur on
/// hardware.
/// The Vita gives applications three of its four cores; the fourth runs the system software: the shell's threads
/// (SceShell, libpaf's job and text threads, the IME plugin...) share that one core and never run in parallel with
/// each other. libpaf relies on it (two of the shell's job threads swapping the same resource's file object without a
/// lock freed it under the other when they ran concurrently). Threads of a process that
/// <see cref="Emulator.RunsOnSystemCore"/> are scheduled on that separate single core.
/// </summary>
public static class Scheduler
{
    /// <summary>Cores available to the application (the Vita reserves the 4th for the system). 0 = unlimited.</summary>
    public static int Cores = 3;

    /// <summary>Cores of the system software (the shell process). 0 = unlimited.</summary>
    public static int SystemCores = 1;

    sealed class Pool
    {
        public readonly object L = new();
        public int Running;
        public readonly List<GuestThread> Waiting = new();
    }

    static readonly Pool App = new(), Sys = new();

    static Pool PoolOf(GuestThread t) => t.OnSystemCore ? Sys : App;
    static int CoresOf(Pool p) => p == Sys ? SystemCores : Cores;

    /// <summary>Blocks until the calling thread may run guest code.</summary>
    public static void Acquire(GuestThread t)
    {
        if (t.HasCore) return;
        var p = PoolOf(t);
        int cores = CoresOf(p);
        if (cores <= 0) return;
        lock (p.L)
        {
            bool queued = false;
            while (true)
            {
                if (p.Running < cores && !HigherPriorityWaiting(p, t.Priority))
                {
                    if (queued) p.Waiting.Remove(t);
                    p.Running++;
                    t.HasCore = true;
                    t.Core.ResetThrottle();
                    return;
                }
                if (!queued) { p.Waiting.Add(t); queued = true; }
                Monitor.Wait(p.L, 2);
            }
        }
    }

    public static void Release(GuestThread t)
    {
        if (!t.HasCore) return;
        if (t == GuestThread.Current) { t.Core.SettleThrottle(t.Core.PendingInstructions); t.Core.PendingInstructions = 0; }
        var p = PoolOf(t);
        lock (p.L)
        {
            p.Running--;
            t.HasCore = false;
            Monitor.PulseAll(p.L);
        }
    }

    /// <summary>Called periodically by a running thread: a higher-priority thread is waiting for its core.</summary>
    public static bool ShouldYield(GuestThread t)
    {
        var p = PoolOf(t);
        int cores = CoresOf(p);
        if (cores <= 0 || p.Waiting.Count == 0) return false;
        lock (p.L) return HigherPriorityWaiting(p, t.Priority) || (p.Running >= cores && SamePriorityWaiting(p, t.Priority));
    }

    /// <summary>Gives the core away (if anyone of higher or equal priority wants it) and takes one again.</summary>
    public static void Yield(GuestThread t)
    {
        if (!ShouldYield(t)) return;
        Release(t);
        Thread.Yield();
        Acquire(t);
    }

    static bool HigherPriorityWaiting(Pool p, int priority)
    {
        foreach (var w in p.Waiting) if (w.Priority < priority) return true;
        return false;
    }

    static bool SamePriorityWaiting(Pool p, int priority)
    {
        foreach (var w in p.Waiting) if (w.Priority == priority) return true;
        return false;
    }

    /// <summary>Forgets all core bookkeeping (process exec after every thread has exited).</summary>
    public static void Reset()
    {
        foreach (var p in new[] { App, Sys })
            lock (p.L) { p.Running = 0; p.Waiting.Clear(); Monitor.PulseAll(p.L); }
    }

    /// <summary>Releases the current thread's core for the duration of a blocking host wait.</summary>
    public static Blocked Block() => new(GuestThread.Current);

    public readonly struct Blocked : IDisposable
    {
        readonly GuestThread? _t;
        public Blocked(GuestThread? t) { _t = t; if (t != null) { t.BlockedInHost = true; Release(t); } }
        public void Dispose() { if (_t != null) { Acquire(_t); _t.BlockedInHost = false; } }
    }
}
