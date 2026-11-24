using VitaSharp.Core.Kernel;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Compat;

/// <summary>
/// Runs the compat core between sceCompatStart and sceCompatStop on its own kernel guest thread ("SceCompatCore"),
/// standing for the MIPS core of the SoC running beside the ARM cores: it runs the core in slices of
/// <see cref="SliceCycles"/>, gives its emulated ARM core away between slices (<see cref="Scheduler.Yield"/>) and
/// sleeps without one while the core is halted (wait/halt) and no interrupt line changed. No thread exists while no
/// core is started, so nothing is paid then.
/// <para>
/// Time: the core counts its own time (<see cref="CoreTimeUs"/>: the cycles it ran at the compat clock, plus the
/// guest time it spent halted). SceCompat's 5 s limits on the core (the boot handshake) are taken on this clock: a
/// host slower than the console runs the core slower, and the handshake must not fail for it.
/// </para>
/// </summary>
public sealed class CompatCoreRunner
{
    /// <summary>The thread's name.</summary>
    public const string ThreadName = "SceCompatCore";

    /// <summary>Cycles per slice: about 30 µs of the core at 333 MHz.</summary>
    public const int SliceCycles = 10_000;

    private readonly Emulator _emulator;
    private readonly ICompatCore _core;
    private readonly int _interruptLine;
    private readonly int _clockMhz;
    private readonly object _lock = new();
    private GuestThread? _thread;
    private bool _stopRequested;
    private int _wantedLevel;   // the interrupt line as the mailbox drives it (0 low, 1 high)
    private int _appliedLevel;
    private long _cycles;
    private long _haltedUs;
    private long _haltStartUs = -1;
    private int _progressWaiters;
    private Exception? _failure;

    /// <param name="emulator">The process the core's thread runs in (the window's).</param>
    /// <param name="core">The core.</param>
    /// <param name="interruptLine">The core's line the mailbox drives.</param>
    /// <param name="clockMhz">The compat clock (ScePowerForDriver_FFC84E69: 333 MHz while the core runs).</param>
    public CompatCoreRunner(Emulator emulator, ICompatCore core, int interruptLine, int clockMhz)
    {
        _emulator = emulator;
        _core = core;
        _interruptLine = interruptLine;
        _clockMhz = Math.Max(1, clockMhz);
    }

    /// <summary>The core's thread runs.</summary>
    public bool Running
    {
        get
        {
            GuestThread? thread = _thread;
            return thread != null && thread.IsLive;
        }
    }

    /// <summary>What stopped the core when it failed (an instruction not emulated yet...), else null.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    /// <summary>The cycles the core ran.</summary>
    public long Cycles => Interlocked.Read(ref _cycles);

    /// <summary>The core's time in microseconds: its cycles at the compat clock plus the time it spent halted.</summary>
    public ulong CoreTimeUs
    {
        get
        {
            long halted;
            lock (_lock)
            {
                halted = _haltedUs;
                if (_haltStartUs >= 0)
                {
                    halted += Math.Max(0, (long)_emulator.Kernel.ProcessTimeUs - _haltStartUs);
                }
            }

            return (ulong)(Interlocked.Read(ref _cycles) / _clockMhz + halted);
        }
    }

    /// <summary>Releases the core from reset at <paramref name="pc"/> and starts its thread.</summary>
    public void Start(uint pc)
    {
        _core.Reset(pc);
        GuestThread thread = _emulator.Kernel.CreateThread(ThreadName, 0, 64, 0x4000, 0, 0);
        thread.KernelMode = true;
        thread.HostBody = Body;
        _thread = thread;
        thread.Start(0, 0);
    }

    /// <summary>The mailbox's drive of the core's interrupt line (any thread): applied by the core's thread before its
    /// next slice, and wakes a halted core.</summary>
    public void SetInterruptLevel(bool high)
    {
        lock (_lock)
        {
            _wantedLevel = high ? 1 : 0;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Holds the core in reset and waits for its thread to end (from any guest thread but the core's).</summary>
    public void Stop()
    {
        GuestThread? thread = _thread;
        lock (_lock)
        {
            _stopRequested = true;
            Monitor.PulseAll(_lock);
        }

        if (thread == null || ReferenceEquals(thread, GuestThread.Current))
        {
            return;
        }

        using (Scheduler.Block())
        {
            thread.WaitForExit(Timeout.Infinite);
        }

        _emulator.Kernel.DeleteThread(thread);
        _thread = null;
    }

    /// <summary>
    /// Waits (releasing the caller's emulated core) until <paramref name="condition"/> holds, the core has run
    /// <paramref name="timeoutUs"/> of its own time, <paramref name="abort"/> holds or the core's thread ended; true
    /// when the condition held. The condition is checked after every slice of the core.
    /// </summary>
    public bool WaitUntil(Func<bool> condition, ulong timeoutUs, Func<bool> abort)
    {
        ulong start = CoreTimeUs;
        while (true)
        {
            if (condition())
            {
                return true;
            }

            if (abort() || !Running || CoreTimeUs - start >= timeoutUs)
            {
                return condition();
            }

            using (Scheduler.Block())
            {
                lock (_lock)
                {
                    _progressWaiters++;
                    Monitor.Wait(_lock, 10);
                    _progressWaiters--;
                }
            }
        }
    }

    private void Body(GuestThread self)
    {
        try
        {
            while (!self.Core.StopRequested)
            {
                if (!ApplyInterruptLevel())
                {
                    break;
                }

                // a halted core runs again first: a line raised since it halted wakes it inside Run
                int ran = _core.Run(SliceCycles);
                Interlocked.Add(ref _cycles, ran);
                NotifyProgress();
                if (ran == 0 && _core.Halted)
                {
                    if (!WaitWhileHalted())
                    {
                        break;
                    }

                    continue;
                }

                Scheduler.Yield(self);
            }
        }
        catch (Exception failure) when (failure is not GuestThreadExitException)
        {
            Volatile.Write(ref _failure, failure);
            Log.Error($"SceCompat core stopped: {failure.Message}");
        }
        finally
        {
            _core.Stop();
            lock (_lock)
            {
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>Applies the wanted interrupt level; false when a stop was asked.</summary>
    private bool ApplyInterruptLevel()
    {
        int wanted;
        lock (_lock)
        {
            if (_stopRequested)
            {
                return false;
            }

            wanted = _wantedLevel;
        }

        if (wanted != _appliedLevel)
        {
            _appliedLevel = wanted;
            if (wanted != 0)
            {
                _core.RaiseInterrupt(_interruptLine);
            }
            else
            {
                _core.ClearInterrupt(_interruptLine);
            }
        }

        return true;
    }

    /// <summary>Sleeps while the core is halted and its interrupt line keeps its level; false when a stop was asked.</summary>
    private bool WaitWhileHalted()
    {
        using (Scheduler.Block())
        {
            lock (_lock)
            {
                _haltStartUs = (long)_emulator.Kernel.ProcessTimeUs;
                Monitor.PulseAll(_lock); // the waiters see a halted core's time pass
                while (!_stopRequested && _wantedLevel == _appliedLevel)
                {
                    Monitor.Wait(_lock, 50);
                }

                _haltedUs += Math.Max(0, (long)_emulator.Kernel.ProcessTimeUs - _haltStartUs);
                _haltStartUs = -1;
                return !_stopRequested;
            }
        }
    }

    private void NotifyProgress()
    {
        if (Volatile.Read(ref _progressWaiters) == 0)
        {
            return;
        }

        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }
    }
}
