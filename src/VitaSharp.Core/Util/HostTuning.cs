namespace VitaSharp.Core.Util;

/// <summary>
/// Host-dependent trade-offs the front end chooses before the emulator starts. The defaults are the desktop's.
/// </summary>
public static class HostTuning
{
    /// <summary>
    /// True on phones (set by the Android app): nothing spins waiting for time to pass. <see cref="PreciseSleep.Until"/>
    /// sleeps the whole wait instead of spinning its last half millisecond (vblank waits, timed guest waits, the
    /// syscall wake-up latency), and the vblank callback notifier sleeps to the next vblank instead of polling every
    /// millisecond. A spinning core on a phone takes power and thermal headroom from the cores that run the game, and
    /// Linux's nanosleep is accurate to tens of microseconds there (the app lowers the hot threads' timer slack too).
    /// The desktop keeps spinning: Windows' timers are coarser.
    /// </summary>
    public static bool AvoidBusyWaits;
}
