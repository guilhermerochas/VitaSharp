using VitaSharp.Core.Util;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The hardware side of the interrupt lines: when an emulated device raises one of the GIC's lines in guest time (a
/// Systimer reaching its interval, a DMA transfer finishing). A device arms a line with the guest microsecond it fires
/// at; when guest time gets there the scheduler calls the device's <c>expire</c> function (the line and the time it
/// was armed for) on its dispatcher
/// thread, a kernel guest thread that stands for the core taking the interrupt, and the device then re-arms its next
/// time and hands the line to SceIntrmgr, which runs the line's handler in interrupt context.
/// <para>
/// Nothing runs while no line is armed: the dispatcher thread is made by the first <see cref="Arm"/>, and without an
/// armed line it only sleeps until a device arms one, so games that never program a hardware timer pay nothing per
/// frame. A line late by several periods (the host was busy) fires once per period, up to
/// <see cref="MaxCatchUp"/>, so the counts a handler keeps (the Systimer offset) do not fall behind guest time.
/// </para>
/// </summary>
public sealed class GuestInterruptScheduler(Emulator emulator, Action<uint, ulong> expire)
{
    /// <summary>The GIC's interrupt lines (0..15 software, 16..255 the devices).</summary>
    public const int Lines = 256;

    /// <summary>The most periods of one line fired at once after the dispatcher woke late.</summary>
    public const int MaxCatchUp = 64;

    /// <summary>Longest host wait between two looks at the clock (ms): guest time moves on its own (real time, turbo, a
    /// test's controlled clock), so the dispatcher looks again at least this often.</summary>
    private const int MaxWaitMs = 20;

    private const ulong NotArmed = ulong.MaxValue;

    private readonly object _lock = new();
    private readonly ulong[] _deadlines = CreateDeadlines();
    private int _armed;
    private GuestThread? _thread;

    private static ulong[] CreateDeadlines()
    {
        var deadlines = new ulong[Lines];
        Array.Fill(deadlines, NotArmed);
        return deadlines;
    }

    /// <summary>Tests: no dispatcher thread; the due lines fire only by <see cref="DispatchDue"/>, at the time the test
    /// names.</summary>
    public bool ManualDispatch { get; set; }

    /// <summary>Guest time as the devices count it (microseconds of process time).</summary>
    public ulong NowUs => emulator.Kernel.ProcessTimeUs;

    /// <summary>Lines armed now.</summary>
    public int Armed
    {
        get
        {
            lock (_lock)
            {
                return _armed;
            }
        }
    }

    /// <summary>Whether the dispatcher thread has been made (the first line armed).</summary>
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
        if (line >= Lines || deadlineUs == NotArmed)
        {
            return;
        }

        lock (_lock)
        {
            if (_deadlines[line] == NotArmed)
            {
                _armed++;
            }

            _deadlines[line] = deadlineUs;
            if (_thread == null && !ManualDispatch)
            {
                StartDispatcher();
            }

            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>The line no longer fires (the device stopped counting).</summary>
    public void Disarm(uint line)
    {
        if (line >= Lines)
        {
            return;
        }

        lock (_lock)
        {
            if (_deadlines[line] != NotArmed)
            {
                _deadlines[line] = NotArmed;
                _armed--;
            }
        }
    }

    /// <summary>Fires every line whose time is at or before <paramref name="nowUs"/>, on the calling guest thread, in
    /// the order of their times (a line due several periods fires again as its device re-arms it, up to
    /// <see cref="MaxCatchUp"/> times); returns how many fired. The dispatcher thread's step, and the tests' way of
    /// firing them at a guest time they decide.</summary>
    public int DispatchDue(ulong nowUs)
    {
        int fired = 0;
        Span<int> perLine = stackalloc int[Lines];
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
        if (_armed == 0)
        {
            return false;
        }

        for (int index = 0; index < Lines; index++)
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
        _armed--;
        return true;
    }

    private ulong Earliest()
    {
        ulong best = NotArmed;
        if (_armed == 0)
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

    private void StartDispatcher()
    {
        GuestThread thread = emulator.Kernel.CreateThread("SceIntrmgrDispatch", 0, 64, 0x4000, 0, 0);
        thread.KernelMode = true;
        thread.HostBody = Dispatch;
        _thread = thread;
        thread.Start(0, 0);
    }

    /// <summary>The dispatcher: sleeps (without an emulated core) until the earliest line's time, then fires the due
    /// lines holding a core, as the core taking the interrupt does.</summary>
    private void Dispatch(GuestThread self)
    {
        while (!self.Core.StopRequested)
        {
            using (Scheduler.Block())
            {
                lock (_lock)
                {
                    ulong next = Earliest();
                    ulong now = NowUs;
                    if (next > now)
                    {
                        Monitor.Wait(_lock, WaitMs(next, now));
                        continue;
                    }
                }
            }

            DispatchDue(NowUs);
        }
    }

    /// <summary>The host wait until the guest time <paramref name="next"/> (1..<see cref="MaxWaitMs"/> ms).</summary>
    private static int WaitMs(ulong next, ulong now)
    {
        if (next == NotArmed || GuestClock.Frozen || GuestClock.Turbo)
        {
            return next == NotArmed ? MaxWaitMs : 2;
        }

        double ms = (next - now) / 1000.0 / GuestClock.Speed / TimeScale.Proc;
        return (int)Math.Clamp(ms, 1, MaxWaitMs);
    }
}
