using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Ends timed waits on a monitor at their microsecond deadline. Monitor.Wait only takes whole milliseconds, so a
/// sceKernelWaitSema with a 500 us timeout used to wait a whole millisecond plus the host timer's slack (1.1 ms, up to
/// 2 ms), where the Vita's threadmgr arms a microsecond timer. A waiter arms its deadline here and waits on its monitor
/// with a millisecond more as a fallback; one host thread blocks on a high-resolution timer (Linux and Android: a
/// timerfd; Windows: a high-resolution waitable timer) set to the earliest armed deadline and pulses that monitor when
/// it passes. Nothing spins: the thread sleeps in the kernel until the timer fires, and re-arming the timer from the
/// waiting threads moves its expiry while it sleeps. Ryujinx's and Dolphin's host timers work the same way (one timer
/// thread on a precise OS timer). macOS has neither timer object here: <see cref="Available"/> is false and the waits
/// keep the millisecond rounding.
/// </summary>
public static class HostTimeoutTimer
{
    /// <summary>One thread's armed deadline (each host thread waits on one monitor at a time).</summary>
    private sealed class Entry
    {
        public long Deadline;
        public object? Sync;
        public int Index = -1;
    }

    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static Entry? t_entry;

    private static readonly object s_lock = new();
    private static readonly List<Entry> s_armed = new();
    private static object[] s_due = new object[16];
    private static long s_timerDeadline = long.MaxValue;
    private static ITimerObject? s_timer;
    private static bool s_started, s_failed;

    /// <summary>Tests and A/B: false makes every wait use the millisecond rounding again.</summary>
    internal static bool Enabled { get; } = Environment.GetEnvironmentVariable(EnvVars.PreciseTimeouts) is not ("0" or "off");

    /// <summary>Statistics: monitors pulsed at their deadline.</summary>
    public static long Fired;

    /// <summary>The host has a timer object this class can use (Windows, Linux, Android).</summary>
    public static bool Available => Enabled && !s_failed && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsAndroid());

    /// <summary>
    /// Arms a pulse of <paramref name="sync"/> (which the caller holds and is about to Monitor.Wait on) at
    /// <paramref name="deadlineTicks"/> (Stopwatch ticks). The pulse cannot be lost: it takes <paramref name="sync"/>,
    /// which the caller only gives up inside its Monitor.Wait. Returns false when no timer is available (the caller
    /// keeps its own millisecond timeout).
    /// </summary>
    public static bool Arm(object sync, long deadlineTicks)
    {
        if (!Available || !EnsureStarted())
        {
            return false;
        }

        Entry entry = t_entry ??= new Entry();
        lock (s_lock)
        {
            entry.Sync = sync;
            entry.Deadline = deadlineTicks;
            if (entry.Index < 0)
            {
                entry.Index = s_armed.Count;
                s_armed.Add(entry);
            }

            if (deadlineTicks < s_timerDeadline)
            {
                SetTimerLocked(deadlineTicks);
            }
        }
        return true;
    }

    /// <summary>The calling thread's wait ended: its deadline is no longer armed (a timer already set for it fires
    /// once for nothing and re-arms for the next deadline).</summary>
    public static void Disarm()
    {
        Entry? entry = t_entry;
        if (entry == null || entry.Index < 0)
        {
            return;
        }

        lock (s_lock)
        {
            RemoveLocked(entry);
        }
    }

    private static void RemoveLocked(Entry entry)
    {
        int index = entry.Index;
        if (index < 0)
        {
            return;
        }

        int last = s_armed.Count - 1;
        Entry moved = s_armed[last];
        s_armed[index] = moved;
        moved.Index = index;
        s_armed.RemoveAt(last);
        entry.Index = -1;
        entry.Sync = null;
    }

    private static void SetTimerLocked(long deadlineTicks)
    {
        s_timerDeadline = deadlineTicks;
        long left = deadlineTicks - Stopwatch.GetTimestamp();
        long nanoseconds = Math.Max(1, left <= 0 ? 1 : (long)(left * (1_000_000_000.0 / Stopwatch.Frequency)));
        s_timer!.Set(nanoseconds);
    }

    private static bool EnsureStarted()
    {
        if (Volatile.Read(ref s_started))
        {
            return true;
        }

        lock (s_lock)
        {
            if (s_started)
            {
                return true;
            }

            if (s_failed)
            {
                return false;
            }

            try
            {
                s_timer = OperatingSystem.IsWindows() ? new WindowsTimer() : new TimerFd();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                s_failed = true;
                Log.WarnOnce("host-timeout-timer", $"precise wait timeouts off: {e.Message}");
                return false;
            }

            var thread = new Thread(Run) { IsBackground = true, Name = "HLE timeouts", Priority = ThreadPriority.AboveNormal };
            thread.Start();
            Volatile.Write(ref s_started, true);
            return true;
        }
    }

    private static void Run()
    {
        NativeThread.Register("HLE timeouts", NativeThread.Role.Other);
        PreciseSleep.Microseconds(1); // Linux and Android: the 1 us timer slack of the threads that sleep precisely
        while (true)
        {
            s_timer!.Wait();
            int due = 0;
            lock (s_lock)
            {
                long now = Stopwatch.GetTimestamp();
                long next = long.MaxValue;
                for (int i = s_armed.Count - 1; i >= 0; i--)
                {
                    Entry entry = s_armed[i];
                    if (entry.Deadline <= now)
                    {
                        if (due == s_due.Length)
                        {
                            Array.Resize(ref s_due, due * 2);
                        }

                        if (entry.Sync is { } sync)
                        {
                            s_due[due++] = sync; // pulsed below, outside this lock
                        }

                        RemoveLocked(entry);
                    }
                    else if (entry.Deadline < next)
                    {
                        next = entry.Deadline;
                    }
                }

                s_timerDeadline = long.MaxValue;
                if (next != long.MaxValue)
                {
                    SetTimerLocked(next);
                }
            }

            for (int i = 0; i < due; i++)
            {
                object sync = s_due[i];
                s_due[i] = null!;
                Pulse(sync);
            }
        }
    }

    /// <summary>Pulses <paramref name="sync"/> without blocking this thread behind a long holder: a few tries, then the
    /// waiter's own millisecond fallback ends its wait.</summary>
    private static void Pulse(object sync)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (Monitor.TryEnter(sync))
            {
                try
                {
                    Monitor.PulseAll(sync);
                }
                finally
                {
                    Monitor.Exit(sync);
                }

                Interlocked.Increment(ref Fired);
                return;
            }

            Thread.Yield();
        }
    }

    private interface ITimerObject
    {
        /// <summary>Sets the expiry (relative, nanoseconds; thread safe, also while another thread waits).</summary>
        void Set(long nanoseconds);

        /// <summary>Blocks until the timer expires.</summary>
        void Wait();
    }

    private sealed class WindowsTimer : ITimerObject
    {
        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003, INFINITE = 0xFFFFFFFF;
        private readonly nint _handle;

        public WindowsTimer()
        {
            _handle = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (_handle == 0)
            {
                throw new InvalidOperationException($"CreateWaitableTimerExW failed ({Marshal.GetLastPInvokeError()})");
            }
        }

        public void Set(long nanoseconds)
        {
            long due = -Math.Max(1, nanoseconds / 100); // relative, 100 ns units
            SetWaitableTimerEx(_handle, ref due, 0, 0, 0, 0, 0);
        }

        public void Wait() => WaitForSingleObject(_handle, INFINITE);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll")]
        private static extern bool SetWaitableTimerEx(nint timer, ref long dueTime, int period, nint completion, nint arg, nint wakeContext, uint tolerableDelay);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(nint handle, uint ms);
    }

    /// <summary>Linux and Android: a CLOCK_MONOTONIC timerfd; read blocks until it expires.</summary>
    private sealed unsafe class TimerFd : ITimerObject
    {
        private const int CLOCK_MONOTONIC = 1, EINTR = 4;
        private readonly int _fd;

        public TimerFd()
        {
            _fd = timerfd_create(CLOCK_MONOTONIC, 0);
            if (_fd < 0)
            {
                throw new InvalidOperationException($"timerfd_create failed (errno {Marshal.GetLastPInvokeError()})");
            }
        }

        public void Set(long nanoseconds)
        {
            // struct itimerspec { timespec it_interval; timespec it_value; }: four pointer-sized fields on every ABI
            nint* spec = stackalloc nint[4];
            spec[0] = 0;
            spec[1] = 0;
            spec[2] = (nint)(nanoseconds / 1_000_000_000);
            spec[3] = (nint)(nanoseconds % 1_000_000_000);
            timerfd_settime(_fd, 0, spec, null);
        }

        public void Wait()
        {
            ulong expirations;
            while (read(_fd, &expirations, 8) < 0 && Marshal.GetLastPInvokeError() == EINTR)
            {
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int timerfd_create(int clock, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int timerfd_settime(int fd, int flags, nint* newValue, nint* oldValue);

        [DllImport("libc", SetLastError = true)]
        private static extern nint read(int fd, void* buffer, nint count);
    }
}
