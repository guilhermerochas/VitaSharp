using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The hardware side of the interrupt lines: when an emulated device raises one of the GIC's lines in guest time (a
/// Systimer reaching its interval, a DMA transfer finishing). A device arms a line with the guest microsecond it fires
/// at. The timing is the one guest-time scheduler's (<see cref="TimerDispatcher"/>, which also fires the kernel timers'
/// events): this is one of its sources, and when the earliest line comes due the dispatcher hands it over to the
/// interrupt thread here, a kernel guest thread that stands for the core taking the interrupt. That thread calls the
/// device's <c>expire</c> function (the line and the time it was armed for) for each due line, and the device re-arms
/// its next time and hands the line to SceIntrmgr, which runs the line's handler in interrupt context.
/// <para>
/// Nothing runs while no line is armed: the interrupt thread is made by the first <see cref="Arm"/> and otherwise only
/// waits for a hand-over, and the dispatcher sleeps without a deadline, so games that never program a hardware timer
/// pay nothing per frame. A line late by several periods (the host was busy) fires once per period, up to
/// <see cref="MaxCatchUp"/>, so the counts a handler keeps (the Systimer offset) do not fall behind guest time.
/// </para>
/// </summary>
public sealed class GuestInterruptScheduler(Emulator emulator, Action<uint, ulong> expire) : IGuestTimed
{
    /// <summary>The GIC's interrupt lines (0..15 software, 16..255 the devices).</summary>
    public const int Lines = 256;

    /// <summary>The HLE drivers' own interrupts after the lines (<see cref="Lines"/> + n): a driver's handler the
    /// emulated hardware does not raise as a GIC line (a GPIO sub-interrupt, the vblank sub-interrupt), timed the same way.</summary>
    public const int HleSources = 8;

    /// <summary>Every source: the lines, then the HLE interrupts.</summary>
    public const int Sources = Lines + HleSources;

    /// <summary>The most periods of one line fired at once after the hand-over came late.</summary>
    public const int MaxCatchUp = 64;

    /// <summary>Longest wait of the interrupt thread for a hand-over (ms), so a stop request is seen.</summary>
    private const int MaxWaitMs = 50;

    private const ulong NotArmed = ulong.MaxValue;

    private readonly object _lock = new();
    private readonly ulong[] _deadlines = CreateDeadlines();
    private int _armedLines;
    private bool _handedOver;
    private GuestThread? _thread;

    private static ulong[] CreateDeadlines()
    {
        ulong[] deadlines = new ulong[Sources];
        Array.Fill(deadlines, NotArmed);
        return deadlines;
    }

    /// <summary>Tests: no interrupt thread and nothing given to the dispatcher; the due lines fire only by
    /// <see cref="DispatchDue"/>, at the time the test names.</summary>
    public bool ManualDispatch { get; set; }

    /// <summary>Guest time as the devices count it (microseconds of process time).</summary>
    public ulong NowUs => emulator.Kernel.ProcessTimeUs;

    /// <summary>In the dispatcher's list (guarded by its lock).</summary>
    public bool Armed { get; set; }

    /// <summary>A line is armed that has not been handed to the interrupt thread. Read without this source's lock: the
    /// dispatcher asks holding its own, which <see cref="Arm"/> takes inside this one.</summary>
    public bool HasPending => Volatile.Read(ref _armedLines) > 0 && !Volatile.Read(ref _handedOver);

    /// <summary>Lines armed now.</summary>
    public int ArmedLines
    {
        get
        {
            lock (_lock)
            {
                return _armedLines;
            }
        }
    }

    /// <summary>Whether the interrupt thread has been made (the first line armed).</summary>
    public bool DispatcherStarted
    {
        get
        {
            lock (_lock)
            {
                return _thread != null;
            }
        }
    }

    /// <summary>The guest microsecond <paramref name="line"/> fires at, or null when it is not armed.</summary>
    public ulong? Deadline(uint line)
    {
        lock (_lock)
        {
            ulong deadline = _deadlines[line];
            return deadline == NotArmed ? null : deadline;
        }
    }

    /// <summary>Arms <paramref name="line"/> to fire at guest microsecond <paramref name="deadlineUs"/> (replacing its
    /// earlier time).</summary>
    public void Arm(uint line, ulong deadlineUs)
    {
        if (line >= Sources || deadlineUs == NotArmed)
        {
            return;
        }

        lock (_lock)
        {
            if (_deadlines[line] == NotArmed)
            {
                _armedLines++;
            }

            _deadlines[line] = deadlineUs;
            if (ManualDispatch)
            {
                return;
            }

            if (_thread == null)
            {
                StartInterruptThread();
            }

            if (!_handedOver)
            {
                TimerDispatcher.Arm(this);
            }
        }
    }

    /// <summary>The line no longer fires (the device stopped counting).</summary>
    public void Disarm(uint line)
    {
        if (line >= Sources)
        {
            return;
        }

        lock (_lock)
        {
            if (_deadlines[line] != NotArmed)
            {
                _deadlines[line] = NotArmed;
                _armedLines--;
            }
        }
    }

    /// <summary>No line fires any more (a state load: the devices arm the loaded state's lines again).</summary>
    public void DisarmAll()
    {
        lock (_lock)
        {
            Array.Fill(_deadlines, NotArmed);
            _armedLines = 0;
        }
    }

    /// <summary>The dispatcher's call: an armed line that came due is handed to the interrupt thread (null: the source
    /// leaves the dispatcher until the thread arms it again); otherwise the microseconds left to the earliest line.</summary>
    public ulong? FireDueAndRemaining()
    {
        lock (_lock)
        {
            ulong next = Earliest();
            if (next == NotArmed)
            {
                return null;
            }

            ulong now = NowUs;
            if (next > now)
            {
                return next - now;
            }

            _handedOver = true;
            Monitor.PulseAll(_lock);
            return null;
        }
    }

    /// <summary>Fires every line whose time is at or before <paramref name="nowUs"/>, on the calling guest thread, in
    /// the order of their times (a line due several periods fires again as its device re-arms it, up to
    /// <see cref="MaxCatchUp"/> times); returns how many fired. The interrupt thread's step, and the tests' way of
    /// firing them at a guest time they decide.</summary>
    public int DispatchDue(ulong nowUs)
    {
        int fired = 0;
        Span<int> perLine = stackalloc int[Sources];
        perLine.Clear();
        while (true)
        {
            uint line;
            ulong deadline;
            lock (_lock)
            {
                if (!TryTakeEarliestDue(nowUs, perLine, out line, out deadline))
                {
                    return fired;
                }
            }

            perLine[(int)line]++;
            fired++;
            expire(line, deadline);
        }
    }

    /// <summary>The earliest armed line due at <paramref name="nowUs"/> that has not fired its catch-up limit, taken out
    /// of the table (its device re-arms it).</summary>
    private bool TryTakeEarliestDue(ulong nowUs, Span<int> perLine, out uint line, out ulong deadline)
    {
        line = 0;
        deadline = NotArmed;
        ulong best = NotArmed;
        if (_armedLines == 0)
        {
            return false;
        }

        for (int index = 0; index < Sources; index++)
        {
            ulong armed = _deadlines[index];
            if (armed <= nowUs && armed < best && perLine[index] < MaxCatchUp)
            {
                best = armed;
                line = (uint)index;
            }
        }

        if (best == NotArmed)
        {
            return false;
        }

        deadline = best;
        _deadlines[line] = NotArmed;
        _armedLines--;
        return true;
    }

    private ulong Earliest()
    {
        ulong best = NotArmed;
        if (_armedLines == 0)
        {
            return best;
        }

        foreach (ulong deadline in _deadlines)
        {
            if (deadline < best)
            {
                best = deadline;
            }
        }

        return best;
    }

    /// <summary>The interrupt thread's name (the save state's identity of the thread, SaveState.IServiceThreadOwner).</summary>
    public const string ThreadName = "SceIntrmgrDispatch";

    /// <summary>Save states: the saved interrupt thread of a state whose session had one, while this session has none,
    /// becomes this scheduler's (it starts with the restored threads); false when it is not the interrupt thread of
    /// this scheduler's process or a thread runs already.</summary>
    public bool AdoptThread(GuestThread thread)
    {
        if (!ReferenceEquals(thread.Process, emulator) || thread.Name != ThreadName)
        {
            return false;
        }

        lock (_lock)
        {
            if (_thread != null && _thread.IsLive)
            {
                return false;
            }

            thread.KernelMode = true;
            thread.HostBody = Interrupts;
            _thread = thread;
            _handedOver = false;
        }

        return true;
    }

    /// <summary>Save states: the interrupt thread the loaded state does not have is let go (the next armed line makes a
    /// new one); false when it is not this scheduler's.</summary>
    public bool ReleaseThread(GuestThread thread)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(thread, _thread))
            {
                return false;
            }

            _thread = null;
            _handedOver = false;
            Monitor.PulseAll(_lock);
        }

        return true;
    }

    private void StartInterruptThread()
    {
        GuestThread thread = emulator.Kernel.CreateThread(ThreadName, 0, 64, 0x4000, 0, 0);
        thread.KernelMode = true;
        thread.HostBody = Interrupts;
        _thread = thread;
        thread.Start(0, 0);
    }

    /// <summary>The interrupt thread: waits (without an emulated core) for the dispatcher's hand-over, then fires the due
    /// lines holding a core, as the core taking the interrupt does, and gives the lines still armed back to the
    /// dispatcher.</summary>
    private void Interrupts(GuestThread self)
    {
        while (!self.Core.StopRequested)
        {
            using (Scheduler.Block())
            {
                lock (_lock)
                {
                    // a thread a state load let go (ReleaseThread) takes no more hand-overs: they are the next thread's
                    if (!ReferenceEquals(_thread, self) || self.Core.StopRequested)
                    {
                        return;
                    }

                    if (!_handedOver)
                    {
                        Monitor.Wait(_lock, MaxWaitMs);
                        continue;
                    }
                }
            }

            DispatchDue(NowUs);
            lock (_lock)
            {
                _handedOver = false;
                if (_armedLines > 0)
                {
                    TimerDispatcher.Arm(this);
                }
            }
        }
    }
}
