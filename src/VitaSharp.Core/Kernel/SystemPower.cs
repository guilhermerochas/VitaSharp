using System.Diagnostics;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The system's power side: the POWER button (<see cref="PowerButtonTracker"/>), the power callbacks
/// (<see cref="PowerCallbacks"/>), standby (the console's suspend: scePowerRequestSuspend) and power off
/// (scePowerRequestStandby, or POWER held until the kernel turns the system off).
/// <para>On the console the kernel only notifies: the shell reacts to POWER_PRESS by suspending and to POWER_HOLD with
/// its power menu. With the real shell running (a system program registered) that is left to it. Without a shell (a
/// game launched directly) the system's part is done here: a press suspends, and nothing shows a power menu.</para>
/// <para>Standby stops the emulation (<see cref="EmulationPause"/>; the guest clock stops) until POWER is pressed
/// again. The state carries SYSTEM_SUSPEND while suspended; on the way back the processes are told SYSTEM_RESUMING
/// (a state bit) and then SYSTEM_RESUME (an event), which applications get with their mask.</para>
/// </summary>
public sealed class SystemPower : IDisposable
{
    private const int PollMs = 10;
    private const string StandbyReason = "standby";

    private readonly object _lock = new();
    private Thread? _poller;
    private volatile bool _stop;
    private Func<IInputBackend?>? _input;
    private bool _wasPowerDown;

    public PowerCallbacks Callbacks { get; } = new();
    public PowerButtonTracker Button { get; } = new();

    /// <summary>The system is in standby.</summary>
    public bool Suspended { get; private set; }

    /// <summary>Raised after the system goes to standby (true) or comes back from it (false).</summary>
    public event Action<bool>? StandbyChanged;

    /// <summary>Raised once when the system turns off: the session is over (the host closes the game).</summary>
    public event Action? PoweredOff;

    /// <summary>
    /// Raised once when the system reboots (kscePowerRequestSoftReset, the syscon's cold or soft reset): on the console
    /// the syscon resets the application processor and the boot runs again, so everything in memory is gone and the
    /// system starts from its boot target (the shell, or the title it was started with). The host ends this session
    /// and boots a new one with the same target in the same window or screen, without closing the application.
    /// </summary>
    public event Action? Rebooted;

    private int _poweredOff;
    private int _rebooted;

    /// <summary>The system was asked to reboot (the session is ending to boot again).</summary>
    public bool RebootRequested => Volatile.Read(ref _rebooted) != 0;

    /// <summary>Starts reading the POWER button from <paramref name="input"/> (the host's controller, port 0) on a host
    /// thread, which keeps running while the emulation is in standby.</summary>
    public void Start(Func<IInputBackend?> input)
    {
        lock (_lock)
        {
            if (_poller != null)
            {
                return;
            }

            _input = input;
            _stop = false;
            _poller = new Thread(Poll)
            {
                IsBackground = true,
                Name = "System buttons",
            };
            _poller.Start();
        }
    }

    private void Poll()
    {
        long frequency = Stopwatch.Frequency;
        while (!_stop)
        {
            try
            {
                if (_input?.Invoke() is { } input)
                {
                    long nowUs = Stopwatch.GetTimestamp() * 1_000_000 / frequency;
                    OnButtons(input.GetController(0).Buttons, nowUs);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Warn($"[power] button poll: {e.Message}");
            }

            Thread.Sleep(PollMs);
        }
    }

    /// <summary>One sample of the buttons (the poller's, or a test's with its own clock).</summary>
    public void OnButtons(VitaButtons buttons, long nowUs)
    {
        bool powerDown = (buttons & VitaButtons.PowerButton) != 0;
        bool pressed = powerDown && !_wasPowerDown;
        _wasPowerDown = powerDown;
        if (Suspended)
        {
            // the power button wakes the system (syscon); that press is not one of its own
            if (pressed)
            {
                Button.IgnoreUntilReleased();
                Resume();
            }

            return;
        }

        uint events = Button.Update(buttons, nowUs, out bool powerOff);
        if (events != 0)
        {
            Log.Info($"[power] button event 0x{events:X8}");
            Callbacks.Post(0, 0, events);
            if (!Callbacks.HasSystemRegistrant)
            {
                ReactWithoutShell(events);
            }
        }

        if (powerOff)
        {
            Log.Info("[power] POWER held: the kernel turns the system off");
            PowerOff();
        }
    }

    /// <summary>What the shell would do with a button event, when no shell runs.</summary>
    private void ReactWithoutShell(uint events)
    {
        if ((events & PowerEvents.ButtonPowerPress) != 0)
        {
            Suspend();
        }
        else if ((events & PowerEvents.ButtonPowerHold) != 0)
        {
            Log.Info("[power] POWER held: the power menu is the shell's (none runs); held on, the system turns off");
        }
    }

    /// <summary>Standby (scePowerRequestSuspend, a POWER press): the emulation stops until POWER is pressed again.</summary>
    public void Suspend()
    {
        lock (_lock)
        {
            if (Suspended)
            {
                return;
            }

            Callbacks.Post(0, PowerEvents.SystemSuspend, 0);
            Suspended = true;
        }
        Log.Info("[power] standby");
        EmulationPause.Pause(StandbyReason, @internal: true);
        StandbyChanged?.Invoke(true);
    }

    /// <summary>Back from standby: the emulation runs again and the processes are told the system resumed.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (!Suspended)
            {
                return;
            }

            Suspended = false;
        }
        Log.Info("[power] resume from standby");
        EmulationPause.Resume(StandbyReason);
        Callbacks.Post(PowerEvents.SystemSuspend, PowerEvents.SystemResuming, 0);
        Callbacks.Post(PowerEvents.SystemResuming, 0, PowerEvents.SystemResume);
        StandbyChanged?.Invoke(false);
    }

    /// <summary>The system turns off (scePowerRequestStandby, POWER held): the session ends.</summary>
    public void PowerOff()
    {
        if (Interlocked.Exchange(ref _poweredOff, 1) != 0)
        {
            return;
        }

        if (Suspended)
        {
            Resume();
        }

        Log.Info("[power] power off: the session ends");
        PoweredOff?.Invoke();
    }

    /// <summary>The system reboots: the session ends and the host boots the system again (<see cref="Rebooted"/>). A
    /// host that cannot boot again (none listens) ends the session as at power off.</summary>
    public void Reboot()
    {
        if (Volatile.Read(ref _poweredOff) != 0 || Interlocked.Exchange(ref _rebooted, 1) != 0)
        {
            return;
        }

        if (Suspended)
        {
            Resume();
        }

        Action? rebooted = Rebooted;
        if (rebooted == null)
        {
            Log.Info("[power] reboot: no host boots the system again: the session ends");
            PowerOff();
            return;
        }

        Log.Info("[power] reboot: the session ends and the system boots again");
        rebooted();
    }

    public void Dispose()
    {
        Thread? poller;
        lock (_lock)
        {
            _stop = true;
            poller = _poller;
            _poller = null;
        }
        poller?.Join(1000);
    }
}
