using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Host scheduling priority of the emulator's latency-critical threads: the guest threads (emulated cores), the JIT
/// workers and the GPU worker run above normal priority, so other programs on the PC do not take their time slices.
/// Measured in docs/notes/thread-priority.md: no change on an idle machine, but with every logical CPU busy elsewhere
/// Sonic Mania went from 19-20 to 31.6 fps (445 to 19 frames over 50 ms).
/// The JIT workers are never below the guest threads: a guest thread runs a block in the interpreter until a worker
/// has compiled it, so a starved worker stalls the guest (Killzone Mercenary's four guest threads polling
/// sceKernelDelayThread kept below-normal workers off the CPU for 14-65 s at boot). Dolphin, FEX and Box64 compile on
/// the guest thread itself, at its priority.
/// Each thread calls <see cref="Apply"/> on itself when it starts.
/// Windows: THREAD_PRIORITY_ABOVE_NORMAL. macOS: QOS_CLASS_USER_INTERACTIVE. Linux: unchanged (raising a thread's
/// priority needs CAP_SYS_NICE).
/// Off: <c>--host-prio=off</c> or <c>VITASHARP_HOST_PRIO=0</c> (the environment variable wins).
/// </summary>
public static class HostThreads
{
    public enum Role
    {
        Guest, Gpu, JitWorker
    }

    /// <summary>Set by the front end from <c>--host-prio=on|off</c> before the emulator starts threads.</summary>
    public static bool Enabled = true;

    private static bool On => Environment.GetEnvironmentVariable(EnvVars.HostPrio) switch
    {
        "0" or "off" or "false" or "no" => false,
        "1" or "on" or "true" or "yes" => true,
        _ => Enabled,
    };

    /// <summary>The managed priority a thread of <paramref name="role"/> runs at (Windows; every role gets the same
    /// boost today, and normal when the boost is off).</summary>
    public static ThreadPriority PriorityOf(Role role) => On ? ThreadPriority.AboveNormal : ThreadPriority.Normal;

    /// <summary>Raises the calling thread's host priority for its role.</summary>
    public static void Apply(Role role)
    {
        if (!On)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);
                return;
            }
            if (OperatingSystem.IsWindows())
            {
                Thread.CurrentThread.Priority = PriorityOf(role);
            }
        }
        catch (Exception e)
        {
            Log.WarnOnce("host-prio-" + role, $"host thread priority for {role} not set: {e.Message}");
        }
    }

    /// <summary>The nice value a background thread of <paramref name="priority"/> gets on Linux and Android: 5 below
    /// normal, 10 lowest (Android's THREAD_PRIORITY_BACKGROUND); 0 otherwise. Under the Linux scheduler a nice-0 thread
    /// sharing a core with a nice-5 one gets ~75% of it, with a nice-10 one ~90%.</summary>
    public static int NiceOf(ThreadPriority priority) => priority switch
    {
        ThreadPriority.Lowest => 10,
        ThreadPriority.BelowNormal => 5,
        _ => 0,
    };

    /// <summary>
    /// Lowers the calling thread to <paramref name="priority"/> for background work (speculative code translation,
    /// spare shader compiles, texture transcoding): the managed priority (Windows honours it) and, on Linux and
    /// Android, the thread's nice value (<see cref="NiceOf"/>), because .NET's <see cref="Thread.Priority"/> does
    /// nothing there (CoreCLR's PAL and NativeAOT leave SCHED_OTHER threads at nice 0), so a "lowest" background
    /// thread competed as an equal with the game's own threads on a phone's LITTLE cores. Lowering needs no privilege
    /// (raising does: <see cref="Apply"/> leaves Linux alone); Linux's nice value is per thread, so
    /// <c>setpriority(PRIO_PROCESS, 0, n)</c> changes the calling thread only. macOS: QOS_CLASS_UTILITY.
    /// <c>VITASHARP_BG_NICE=0</c> keeps the managed priority only (A/B). Returns the nice value set (0: none).
    /// </summary>
    public static int LowerCurrent(ThreadPriority priority)
    {
        try
        {
            Thread.CurrentThread.Priority = priority;
        }
        catch (ThreadStateException)
        {
            // the thread is ending
        }

        int nice = NiceOf(priority);
        if (nice == 0 || !BackgroundNiceOn)
        {
            return 0;
        }

        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
            {
                if (CurrentNice() >= nice)
                {
                    return 0; // already as low (a niced process): raising it back would need a privilege
                }

                if (setpriority(PRIO_PROCESS, 0, nice) == 0)
                {
                    return nice;
                }

                Log.WarnOnce("bg-nice", $"background thread nice {nice} not set: errno {Marshal.GetLastPInvokeError()}");
                return 0;
            }
            if (OperatingSystem.IsMacOS())
            {
                pthread_set_qos_class_self_np(QOS_CLASS_UTILITY, 0);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.WarnOnce("bg-nice", $"background thread priority not set: {e.Message}");
        }
        return 0;
    }

    /// <summary>The calling thread's nice value (Linux and Android; 0 elsewhere or when it cannot be read).</summary>
    public static int CurrentNice()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()))
        {
            return 0;
        }

        try
        {
            Marshal.SetLastPInvokeError(0);
            int value = getpriority(PRIO_PROCESS, 0);
            return value == -1 && Marshal.GetLastPInvokeError() != 0 ? 0 : value;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static bool BackgroundNiceOn => Environment.GetEnvironmentVariable(EnvVars.BackgroundNice) is not ("0" or "off" or "false" or "no");

    private const int PRIO_PROCESS = 0;
    private const uint QOS_CLASS_USER_INTERACTIVE = 0x21, QOS_CLASS_UTILITY = 0x11;
    [DllImport("libc")] private static extern int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);
    [DllImport("libc", SetLastError = true)] private static extern int setpriority(int which, uint who, int prio);
    [DllImport("libc", SetLastError = true)] private static extern int getpriority(int which, uint who);
}
