using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Host scheduling priority of the emulator's latency-critical threads: the guest threads (emulated cores) and the
/// GPU worker run above normal priority, so other programs on the PC (and our own below-normal JIT workers) do not
/// take their time slices. Measured in docs/notes/thread-priority.md: no change on an idle machine, but with every
/// logical CPU busy elsewhere Sonic Mania went from 19-20 to 31.6 fps (445 to 19 frames over 50 ms).
/// Each thread calls <see cref="Apply"/> on itself when it starts.
/// Windows: THREAD_PRIORITY_ABOVE_NORMAL. macOS: QOS_CLASS_USER_INTERACTIVE. Linux: unchanged (raising a thread's
/// priority needs CAP_SYS_NICE).
/// Off: <c>--host-prio=off</c> or <c>VITASHARP_HOST_PRIO=0</c> (the environment variable wins).
/// </summary>
public static class HostThreads
{
    public enum Role { Guest, Gpu }

    /// <summary>Set by the front end from <c>--host-prio=on|off</c> before the emulator starts threads.</summary>
    public static bool Enabled = true;

    static bool On => Environment.GetEnvironmentVariable(EnvVars.HostPrio) switch
    {
        "0" or "off" or "false" or "no" => false,
        "1" or "on" or "true" or "yes" => true,
        _ => Enabled,
    };

    /// <summary>Raises the calling thread's host priority for its role (both roles get the same boost today).</summary>
    public static void Apply(Role role)
    {
        if (!On) return;
        try
        {
            if (OperatingSystem.IsMacOS()) { pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0); return; }
            if (OperatingSystem.IsWindows()) Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }
        catch (Exception e) { Log.WarnOnce("host-prio-" + role, $"host thread priority for {role} not set: {e.Message}"); }
    }

    const uint QOS_CLASS_USER_INTERACTIVE = 0x21;
    [DllImport("libc")] static extern int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);
}
