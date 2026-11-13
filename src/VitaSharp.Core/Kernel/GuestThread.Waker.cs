using System.Diagnostics;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// A woken thread does not run ahead of the thread that woke it.
/// <para>
/// On the Vita the cores share one clock: when a thread's system call wakes a thread on another core, the waker goes on
/// from that moment as fast as the woken one does, so a call it makes a few instructions later (~1 us) always happens
/// before anything the woken thread does after its own wake-up (tens of microseconds of kernel exit and context switch,
/// then its work). Games rely on it without knowing: Metal Gear Solid HD's main thread sets "go" on its End Frame Event
/// and clears "done" right after; the End Frame thread waits for "go" (clearing the pattern), works, and sets "done".
/// </para>
/// <para>
/// Here each guest thread is a host thread, and the host may stop one for milliseconds at any point: a busy host (a
/// woken host thread preempting its waker, other processes), or CPU pacing sleeping off a thread that ran ahead of real
/// time right after a costly call. The End Frame thread then did its whole cycle and set "done" before main cleared it;
/// main wiped it and both threads waited for each other for good.
/// </para>
/// <para>
/// So a thread woken by another guest thread keeps that thread as its waker, and at each of its system calls (the
/// points where it can affect other threads) it may not be further past its wake-up, in guest cycles, than the waker is
/// past the waking call, while the waker still holds an emulated core (still running guest code or a non-blocking
/// call). Only the first <see cref="WakerReleaseCycles"/> after the wake-up are kept in step: the waker blocking,
/// yielding its core or exiting, or getting past that window, ends the dependency. Measured on Metal Gear's boot, the
/// woken threads wait ~0.6 times a frame for ~0.3 ms in all, and the load and frame rate stay the same. This is the
/// conservative synchronisation of parallel simulators (a core never acts at a time another core has not reached),
/// limited to the pair that a wake-up makes causal, so threads that do not wake each other are not slowed down.
/// </para>
/// </summary>
public sealed unsafe partial class GuestThread
{
    /// <summary>Longest a thread waits for its waker (real time): a waker that holds its core while the host runs long
    /// host-side work for it (no guest progress) must not stall the threads it woke indefinitely.</summary>
    internal static long WakerWaitLimitTicks = Stopwatch.Frequency / 10;

    /// <summary>The window after a wake-up that is kept in step (guest cycles): ~0.3 ms at 333 MHz, past what any call
    /// "right after" the waking one is, and past one pacing window (so a waker that sleeps off its pacing just after the
    /// call still holds the woken thread back). Keeping whole runs in step instead (no window) made each wait last as
    /// long as the woken thread's lead: 9 s of waits in a 75 s boot and a slower load.</summary>
    internal const long WakerReleaseCycles = 100_000;

    /// <summary>Statistics: system calls that waited for the waker, and waits that hit <see cref="WakerWaitLimitTicks"/>.</summary>
    public static long WakerWaits, WakerWaitTimeouts;

    private GuestThread? _waker;
    private long _wakerBase, _wokenBase;

    /// <summary>Guest cycles this thread has run, its HLE calls' costs included (the owning thread's view).</summary>
    internal long OwnCycles => Volatile.Read(ref Core.BlockCycles) + Volatile.Read(ref Core.ChargedCycles);

    /// <summary>Guest cycles at the entry of the HLE call the thread is in (before its cost is charged).</summary>
    private long _hleEntryCycles;

    /// <summary>How far the thread has got, as other threads see it: inside an HLE call, the cycles at its entry (the
    /// call has not finished, its effects may not all have happened); otherwise the cycles run so far.</summary>
    internal long Progress => (_hleSeq & 1) == 1 ? Volatile.Read(ref _hleEntryCycles) : OwnCycles;

    /// <summary>The thread's blocking wait ended because <paramref name="waker"/> woke it (null: no guest thread did,
    /// a timeout, a host-side signal), at <paramref name="wakerCycles"/> of the waker's cycles.</summary>
    internal void WokenBy(GuestThread? waker, long wakerCycles)
    {
        if (waker == null || waker == this)
        {
            _waker = null;
            return;
        }

        _wakerBase = wakerCycles;
        _wokenBase = OwnCycles;
        _waker = waker;
    }

    /// <summary>
    /// Called at the entry of every HLE call: while the thread is further past its wake-up than its waker is past the
    /// waking call (within <see cref="WakerReleaseCycles"/>) and the waker still holds a core, waits for the waker (see
    /// the class remarks).
    /// </summary>
    internal void WaitForWaker()
    {
        GuestThread? waker = _waker;
        if (waker == null)
        {
            return;
        }

        // the woken thread may not get past the first WakerReleaseCycles the waker runs after the waking call before
        // the waker has: beyond that window nothing either does is "right after" the wake-up
        long ahead = Math.Min(OwnCycles - _wokenBase, WakerReleaseCycles);
        long wakerAhead = waker.Progress - _wakerBase;
        if (!Volatile.Read(ref waker.HasCore) || wakerAhead >= WakerReleaseCycles)
        {
            _waker = null; // the waker blocked, or is past the window: the dependency is over
            return;
        }
        if (wakerAhead >= ahead)
        {
            return;
        }

        Interlocked.Increment(ref WakerWaits);
        long start = Stopwatch.GetTimestamp();
        int rounds = 0;
        while (Volatile.Read(ref waker.HasCore) && waker.Progress - _wakerBase < ahead && !Core.StopRequested)
        {
            if (EmulationPause.IsPaused)
            {
                start = Stopwatch.GetTimestamp(); // the waker parks; the limit counts running time only
            }
            else if (Stopwatch.GetTimestamp() - start > WakerWaitLimitTicks)
            {
                Interlocked.Increment(ref WakerWaitTimeouts);
                Util.Log.WarnOnce("waker-wait-limit", $"'{Name}' waited {WakerWaitLimitTicks * 1000 / Stopwatch.Frequency} ms for its waker '{waker.Name}' to make progress; going on");
                break;
            }

            // a few yields (the waker is usually microseconds away), then short sleeps: spinning would take the host
            // CPU the waker needs (Thread.Sleep(1) would overshoot to the timer tick, up to 15.6 ms on Windows)
            if (++rounds < 16)
            {
                Thread.Yield();
            }
            else
            {
                Util.PreciseSleep.Microseconds(50);
            }
        }
        if (!Volatile.Read(ref waker.HasCore) || waker.Progress - _wakerBase >= WakerReleaseCycles || Core.StopRequested)
        {
            _waker = null;
        }
    }
}
