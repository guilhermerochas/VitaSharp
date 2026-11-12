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

    private sealed class Pool
    {
        public readonly object L = new();
        public int Running;
        public readonly List<GuestThread> Waiting = new();
    }

    private static readonly Pool s_app = new(), s_sys = new();

    /// <summary>Statistics (PERF sched): acquires that had to wait for a core, and waits repeated after a wake-up that
    /// did not get one (another thread took it, or a timeout poll).</summary>
    public static long AcquireBlocked, AcquireRewaits;

    private static Pool PoolOf(GuestThread t) => t.OnSystemCore ? s_sys : s_app;
    private static int CoresOf(Pool pool) => pool == s_sys ? SystemCores : Cores;

    /// <summary>Blocks until the calling thread may run guest code.</summary>
    public static void Acquire(GuestThread t)
    {
        if (t.HasCore)
        {
            return;
        }

        Pool pool = PoolOf(t);
        int cores = CoresOf(pool);
        if (cores <= 0)
        {
            return;
        }

        lock (pool.L)
        {
            bool queued = false;
            while (true)
            {
                if (pool.Running < cores && !HigherPriorityWaiting(pool, t.Priority))
                {
                    if (queued)
                    {
                        pool.Waiting.Remove(t);
                    }

                    pool.Running++;
                    t.HasCore = true;
                    t.Core.ResetThrottle();
                    return;
                }
                if (!queued)
                {
                    pool.Waiting.Add(t);
                    queued = true;
                }
                Monitor.Wait(pool.L, 2);
            }
        }
    }

    public static void Release(GuestThread t)
    {
        if (!t.HasCore)
        {
            return;
        }

        if (t == GuestThread.Current)
        {
            t.Core.SettleThrottle(t.Core.PendingInstructions);
            t.Core.PendingInstructions = 0;
        }
        Pool pool = PoolOf(t);
        lock (pool.L)
        {
            pool.Running--;
            t.HasCore = false;
            Monitor.PulseAll(pool.L);
        }
    }

    /// <summary>Called periodically by a running thread: a higher-priority thread is waiting for its core.</summary>
    public static bool ShouldYield(GuestThread t)
    {
        Pool pool = PoolOf(t);
        int cores = CoresOf(pool);
        if (cores <= 0 || pool.Waiting.Count == 0)
        {
            return false;
        }

        lock (pool.L)
        {
            return HigherPriorityWaiting(pool, t.Priority) || (pool.Running >= cores && SamePriorityWaiting(pool, t.Priority));
        }
    }

    /// <summary>Gives the core away (if anyone of higher or equal priority wants it) and takes one again.</summary>
    public static void Yield(GuestThread t)
    {
        if (!ShouldYield(t))
        {
            return;
        }

        Release(t);
        Thread.Yield();
        Acquire(t);
    }

    private static bool HigherPriorityWaiting(Pool pool, int priority)
    {
        foreach (GuestThread w in pool.Waiting)
        {
            if (w.Priority < priority)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SamePriorityWaiting(Pool pool, int priority)
    {
        foreach (GuestThread w in pool.Waiting)
        {
            if (w.Priority == priority)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Forgets all core bookkeeping (process exec after every thread has exited).</summary>
    public static void Reset()
    {
        foreach (Pool? pool in new[] { s_app, s_sys })
        {
            lock (pool.L)
            {
                pool.Running = 0;
                pool.Waiting.Clear();
                Monitor.PulseAll(pool.L);
            }
        }
    }

    /// <summary>Releases the current thread's core for the duration of a blocking host wait.</summary>
    public static Blocked Block() => new(GuestThread.Current);

    public readonly struct Blocked : IDisposable
    {
        private readonly GuestThread? _thread;
        public Blocked(GuestThread? t)
        {
            _thread = t;
            if (t != null)
            {
                t.BlockedInHost = true;
                Release(t);
            }
        }
        public void Dispose()
        {
            if (_thread != null)
            {
                Acquire(_thread);
                _thread.BlockedInHost = false;
            }
        }
    }
}
