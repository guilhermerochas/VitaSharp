using System.Diagnostics;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;

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
/// yielding its core or exiting at any time since the waking call (counted by <see cref="CoreReleases"/>, so a short
/// block between two looks of the woken thread is not missed), or getting past that window, ends the dependency.
/// Measured on Metal Gear's boot, the woken threads wait ~0.6 times a frame for ~0.3 ms in all, and the load and frame rate stay the same. This is the
/// conservative synchronisation of parallel simulators (a core never acts at a time another core has not reached),
/// limited to the pair that a wake-up makes causal, so threads that do not wake each other are not slowed down.
/// </para>
/// </summary>
public sealed unsafe partial class GuestThread
{
    /// <summary>Longest a thread waits for its waker (real time): a waker that holds its core while the host runs long
    /// host-side work for it (no guest progress) must not stall the threads it woke indefinitely.</summary>
    private static readonly long s_wakerWaitLimitTicks = Stopwatch.Frequency / 10;

    /// <summary>
    /// Test seam: this thread's wait limit (Stopwatch ticks; <see cref="long.MaxValue"/>: none). The limit is the only
    /// host-clock decision in the mechanism, so a test that holds a waker for a known reason disables it on the woken
    /// thread and no longer depends on how long the host takes (a loaded CI host overslept a 50 ms stop past the
    /// 100 ms limit), and a test of the limit itself makes it short.
    /// </summary>
    internal long WakerWaitLimitTicks
    {
        get => Volatile.Read(ref _wakerWaitLimitTicks);
        set => Volatile.Write(ref _wakerWaitLimitTicks, value);
    }

    private long _wakerWaitLimitTicks = s_wakerWaitLimitTicks;

    private int _waitingForWaker;

    /// <summary>Test seam: the thread is held at a system call, waiting for its waker.</summary>
    internal bool IsWaitingForWaker => Volatile.Read(ref _waitingForWaker) != 0;

    /// <summary>The window after a wake-up that is kept in step (guest cycles): ~0.3 ms at 333 MHz, past what any call
    /// "right after" the waking one is, and past one pacing window (so a waker that sleeps off its pacing just after the
    /// call still holds the woken thread back). Keeping whole runs in step instead (no window) made each wait last as
    /// long as the woken thread's lead: 9 s of waits in a 75 s boot and a slower load.</summary>
    internal const long WakerReleaseCycles = 100_000;

    /// <summary>Statistics: system calls that waited for the waker, and waits that hit <see cref="s_wakerWaitLimitTicks"/>.</summary>
    public static long WakerWaits, WakerWaitTimeouts;

    private GuestThread? _waker;
    private long _wakerBase, _wokenBase;
    private int _wakerReleases;

    /// <summary>Guest cycles this thread has run, its HLE calls' costs included (the owning thread's view).</summary>
    internal long OwnCycles => Volatile.Read(ref Core.BlockCycles) + Volatile.Read(ref Core.ChargedCycles);

    /// <summary>Guest cycles at the entry of the HLE call the thread is in (before its cost is charged).</summary>
    private long _hleEntryCycles;

    /// <summary>How far the thread has got, as other threads see it: inside an HLE call, the cycles at its entry (the
    /// call has not finished, its effects may not all have happened); otherwise the cycles run so far.</summary>
    internal long Progress => (_hleSeq & 1) == 1 ? Volatile.Read(ref _hleEntryCycles) : OwnCycles;

    /// <summary>
    /// Test seam: called on the guest thread right after each HLE call returns, where it is back in guest code. A test
    /// stops the thread's host execution here (a host preemption, a pacing sleep) to reproduce a race between guest
    /// threads deterministically, without a game. Null in normal runs (one static read per call).
    /// </summary>
    internal static Action<GuestThread, HleFunction>? HleReturned { get; set; }

    /// <summary>
    /// Test seam: issues the HLE function <paramref name="name"/> on this thread (its own host thread) through the real
    /// dispatch path, as the guest's SVC does: cost charge, <see cref="WaitForWaker"/>, <see cref="HleReturned"/>.
    /// Up to four register arguments; returns R0.
    /// </summary>
    internal uint DispatchForTest(string name, params uint[] args)
    {
        if (args.Length > 4 || Current != this)
        {
            throw new ArgumentException("at most four register arguments, on the thread's own host thread");
        }

        foreach (NidInfo info in NidDatabase.LookupName(name))
        {
            uint index = _emu.Hle.GetHleFunctionIndex(info.Nid, 0, info.Library);
            if (_emu.Hle.GetFunction(index).Invoke == null)
            {
                continue;
            }

            CpuState* state = Core.State;
            for (int i = 0; i < args.Length; i++)
            {
                state->R[i] = args[i];
            }
            state->SvcImm = index;
            state->R[14] = state->R[15];
            DispatchHle(state);
            return state->R[0];
        }
        throw new ArgumentException($"no implemented HLE function {name}");
    }

    /// <summary>The thread's blocking wait ended because <paramref name="waker"/> woke it (null: no guest thread did,
    /// a timeout, a host-side signal), at <paramref name="wakerCycles"/> of the waker's cycles and after
    /// <paramref name="wakerReleases"/> of its core releases (both taken at the waking call).</summary>
    internal void WokenBy(GuestThread? waker, long wakerCycles, int wakerReleases)
    {
        if (waker == null || waker == this)
        {
            _waker = null;
            return;
        }

        _wakerBase = wakerCycles;
        _wokenBase = OwnCycles;
        _wakerReleases = wakerReleases;
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
        if (!WakerStillRunning(waker) || wakerAhead >= WakerReleaseCycles)
        {
            _waker = null; // the waker blocked, or is past the window: the dependency is over
            return;
        }
        if (wakerAhead >= ahead)
        {
            return;
        }

        Interlocked.Increment(ref WakerWaits);
        long limitTicks = WakerWaitLimitTicks;
        long start = Stopwatch.GetTimestamp();
        int rounds = 0;
        Volatile.Write(ref _waitingForWaker, 1);
        while (WakerStillRunning(waker) && waker.Progress - _wakerBase < ahead && !Core.StopRequested)
        {
            if (EmulationPause.IsPaused)
            {
                start = Stopwatch.GetTimestamp(); // the waker parks; the limit counts running time only
            }
            else if (Stopwatch.GetTimestamp() - start > limitTicks)
            {
                Interlocked.Increment(ref WakerWaitTimeouts);
                Util.Log.WarnOnce("waker-wait-limit", $"'{Name}' waited {limitTicks * 1000 / Stopwatch.Frequency} ms for its waker '{waker.Name}' to make progress; going on");
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
        Volatile.Write(ref _waitingForWaker, 0);
        if (!WakerStillRunning(waker) || waker.Progress - _wakerBase >= WakerReleaseCycles || Core.StopRequested)
        {
            _waker = null;
        }
    }

    /// <summary>The waker has held its core without a break since the waking call: it has not blocked, yielded or
    /// exited in between, even briefly (a 1 ms delay could start and end between two looks of the woken thread, which
    /// then waited for a waker that was no longer in step with it).</summary>
    private bool WakerStillRunning(GuestThread waker) =>
        Volatile.Read(ref waker.HasCore) && waker.CoreReleases == _wakerReleases;
}
