using VitaSharp.Core.Util;
using System.Runtime.CompilerServices;

namespace VitaSharp.Core.Cpu;

/// <summary>Executes one guest thread's CPU state: block lookup â†’ (JIT-compiled | interpreted) â†’ exit handling.</summary>
public sealed unsafe class CpuCore : IDisposable
{
    public CpuState* State { get; }
    readonly BlockCache _cache;

    // Small per-thread direct-mapped lookup in front of the shared dictionary.
    const int FastSize = 4096;
    readonly Block?[] _fast = new Block?[FastSize];

    volatile bool _stop, _interrupt;
    public bool StopRequested
    {
        get => _stop;
        set { _stop = value; if (value) State->LinkBreak = 1; } // native code chained across blocks returns
    }
    /// <summary>Makes <see cref="Run"/> return <see cref="ExitReason.Yield"/> at the next block boundary.</summary>
    public bool InterruptRequested
    {
        get => _interrupt;
        set { _interrupt = value; if (value) State->LinkBreak = 1; }
    }
    /// <summary>Polled every ~0.5 ms of guest CPU time: true when the thread must give its core to a higher-priority one.</summary>
    public Func<bool>? ShouldYield;
    public long BlocksExecuted;
    /// <summary>Guest instructions executed by this thread (diagnostics; weighted cycles are in <see cref="BlockCycles"/>).</summary>
    public long InstructionsExecuted;

    public CpuCore(BlockCache cache)
    {
        _cache = cache;
        State = CpuState.Allocate();
        HandlerTable.EnsureInitialized();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    Block Lookup(uint pc, bool thumb)
    {
        int slot = (int)((pc >> 1) & (FastSize - 1));
        var b = _fast[slot];
        if (b != null && b.Start == pc && b.Thumb == thumb && !b.Invalidated) return b; // invalidated: guest rewrote the code
        b = _cache.Get(pc, thumb);
        _fast[slot] = b;
        return b;
    }

    public void FlushFastCache() => Array.Clear(_fast);

    /// <summary>A native static exit returned through an empty link slot: point the slot at the next block if it is native.</summary>
    void LinkExit(CpuState* s)
    {
        nint slot = s->LastExitSlot;
        if (*(nint*)slot != 0) return; // linked already (the exit returned for pacing or an interrupt)
        var next = Lookup(s->R[15], s->Thumb != 0);
        if (next.NativeEntry != 0) Native.NativeRuntime.Link(slot, next);
    }

    /// <summary>Run until the guest raises an exit (SVC, undefined, breakpoint, yield) or a stop is requested.</summary>
    /// <summary>Debug probes: block-start addresses that invoke <see cref="ProbeHit"/> before executing.</summary>
    public static HashSet<uint>? Probes;
    public static ProbeFn? ProbeHit;
    public delegate void ProbeFn(CpuCore core, CpuState* s);
    /// <summary>Debugging: called before every block; returning true means it executed the block itself (differential testing).</summary>
    public static BlockFn? BlockHook;
    public delegate bool BlockFn(CpuCore core, CpuState* s, Block b);

    /// <summary>
    /// Emulated CPU speed in cycles per second (see <see cref="BlockBuilder.CycleCost"/>); 0 runs unpaced. Pacing to it
    /// is what makes time-budgeted game code (physics sub-stepping, frame timers, loads hidden behind timed sequences)
    /// behave as on hardware.
    /// <para>
    /// An application starts at the system's default ARM clock, 333 MHz; 444 MHz is the most it may ask for, through
    /// scePowerSetArmClockFrequency (which is why homebrew and the overclocking plugins raise it explicitly). None of
    /// the installed retail titles calls it. Pacing at 444 by default let Silent Hill: Book of Memories finish loading
    /// its title screen before its fixed-length splash sequence ended (17.5 s of fades): the splash thread then started
    /// the loading screen with a texture the main thread had already released ("Texture 'ui\ui_loading_screen.tga' was
    /// selected for rendering without being cached").
    /// </para>
    /// </summary>
    public const long DefaultClockHz = 333_000_000;
    public static long InstructionsPerSecond = long.TryParse(Environment.GetEnvironmentVariable(EnvVars.CpuMhz), out var mhz) ? mhz * 1_000_000 : DefaultClockHz;
    long _throttleInstrs, _throttleStart = -1;
    /// <summary>True while the thread sleeps off CPU pacing (profiling).</summary>
    public volatile bool InThrottle;
    public struct SleepStat { public long Count, Requested, Actual; }
    public static readonly SleepStat[] SleepStats = new SleepStat[2];
    static void Stat(ref SleepStat s, long t0, long expected) { Interlocked.Increment(ref s.Count); Interlocked.Add(ref s.Requested, expected - t0); Interlocked.Add(ref s.Actual, Util.GuestClock.Ticks - t0); }

    void Throttle()
    {
        long rate = InstructionsPerSecond;
        if (rate <= 0 || Util.GuestClock.Turbo) return;
        long now = Util.GuestClock.Ticks, freq = System.Diagnostics.Stopwatch.Frequency;
        if (_throttleStart < 0) { _throttleStart = now; _throttleInstrs = 0; return; }
        long expected = _throttleStart + _throttleInstrs * freq / rate;
        if (expected < now - freq / 100)
        {
            // more than 10 ms behind: the thread spent that time in host-side HLE work (no guest instructions); the
            // ledger restarts there rather than banking credit. Blocking waits reset it explicitly (ResetThrottle).
            _throttleStart = now - freq / 100; _throttleInstrs = 0;
        }
        else if (expected > now + freq / 1000) // ahead by > 1 ms: sleep it off (no spinning: on a phone that is battery)
        {
            PaceSleep(expected, ref SleepStats[0]);
        }
    }

    /// <summary>How late host sleeps wake up, on average (ticks; shared: it is a property of the host's timers).</summary>
    static long _wakeLate;

    /// <summary>
    /// Sleeps off pacing until <paramref name="expected"/>. Host sleeps wake 0.3-0.7 ms late (more under load), and the
    /// thread can only make that up at the host's speed margin over the emulated CPU, which for CPU-heavy games is
    /// small: aiming early by the average lateness makes the wake-ups land on the deadline on average instead.
    /// </summary>
    void PaceSleep(long expected, ref SleepStat stat)
    {
        InThrottle = true;
        long t0 = Util.GuestClock.Ticks, target = Math.Max(t0, expected - Volatile.Read(ref _wakeLate));
        Util.GuestClock.WaitUntil(target, allowSpin: false);
        if (target > t0)
        {
            long late = Math.Clamp(Util.GuestClock.Ticks - target, 0, MaxWakeCredit), avg = Volatile.Read(ref _wakeLate);
            Volatile.Write(ref _wakeLate, avg + (late - avg) / 16); // racy by design: a lost update only slows the average
        }
        InThrottle = false;
        Stat(ref stat, t0, expected);
    }

    /// <summary>
    /// Called when the thread resumes after a blocking wait: time spent blocked is not CPU time, so the ledger restarts.
    /// It restarts where the thread resumes on hardware, not at "now": a thread blocks at its emulated position P
    /// (<see cref="SettleThrottle"/>), and on hardware it resumes at max(P, signal) for an event wait, or at the
    /// deadline of a timed wait (<see cref="WokeAt"/>). Restarting at "now" threw away the host's wake-up lateness
    /// (0.3-0.7 ms per sleep, more under load) at every block, which made frequently blocking threads slower than
    /// their emulated clock; restarting lazily at the first check let short bursts between waits run unpaced.
    /// </summary>
    public void ResetThrottle()
    {
        long rate = InstructionsPerSecond, now = Util.GuestClock.Ticks;
        if (rate <= 0 || Util.GuestClock.Turbo) { _throttleStart = -1; _throttleInstrs = 0; _blockedAt = _wakeDeadline = 0; return; }
        long start = now;
        if (_wakeDeadline != 0) start = Math.Max(_wakeDeadline, now - MaxWakeCredit); // timed wait: resume at its deadline
        else if (_blockedAt != 0) start = Math.Max(now, Math.Min(_blockedAt, now + MaxWakeCredit)); // event: max(P, signal)
        _throttleStart = start; _throttleInstrs = 0; _blockedAt = _wakeDeadline = 0;
    }

    /// <summary>Most wake-up lateness a timed wait may recover (ticks); bounds the effect of a stalled host.</summary>
    static readonly long MaxWakeCredit = System.Diagnostics.Stopwatch.Frequency / 500; // 2 ms
    long _blockedAt, _wakeDeadline;

    /// <summary>The thread's emulated position when it last blocked (guest clock ticks), or real time when unpaced/
    /// unknown. Timed waits measure their delay from here: on hardware the delay starts where the thread is, and a
    /// thread still catching up after a late pacing wake-up (bounded) would have reached the call earlier.</summary>
    public long BlockedAt => _blockedAt != 0 ? Math.Max(_blockedAt, Util.GuestClock.Ticks - MaxWakeCredit) : Util.GuestClock.Ticks;

    /// <summary>A timed wait (delay, vblank) ended: on hardware the thread resumes at <paramref name="deadline"/> (or
    /// where it blocked, if that was later), however late the host woke it.</summary>
    public void WokeAt(long deadline) { if (_blockedAt != 0) _wakeDeadline = Math.Max(deadline, _blockedAt); }

    /// <summary>
    /// Where this thread's emulated CPU time is (guest clock ticks), or 0 when unknown/unpaced. A paced thread runs in
    /// host-speed bursts and sleeps once it is ~1 ms ahead of real time; the clock it reads must follow its own
    /// progress through the burst (see <see cref="Util.GuestClock.Observed"/>), or a polling loop sees time stand
    /// still and then jump by the sleep length. Valid on the owning thread (inside an HLE call).
    /// </summary>
    public long EmulatedPosition()
    {
        long rate = InstructionsPerSecond;
        if (rate <= 0 || Util.GuestClock.Turbo || _throttleStart < 0) return 0;
        return _throttleStart + (_throttleInstrs + PendingInstructions) * System.Diagnostics.Stopwatch.Frequency / rate;
    }

    /// <summary>
    /// Called before the thread blocks: work done since the last check is added to the ledger, and the position where
    /// the thread blocks is kept for <see cref="ResetThrottle"/>, so short bursts between waits (job systems) are paced
    /// too and their emulated time carries over the wait. Like <see cref="Throttle"/>, it only sleeps once the thread
    /// is more than 1 ms ahead of real time: every host sleep wakes late, and a thread that runs a little ahead before
    /// it blocks loses nothing (it resumes at max(position, signal)).
    /// </summary>
    public void SettleThrottle(long pendingInstructions)
    {
        long rate = InstructionsPerSecond;
        if (rate <= 0 || Util.GuestClock.Turbo || _throttleStart < 0) return;
        _throttleInstrs += pendingInstructions;
        long expected = _throttleStart + _throttleInstrs * System.Diagnostics.Stopwatch.Frequency / rate;
        _blockedAt = expected;
        if (expected > Util.GuestClock.Ticks + System.Diagnostics.Stopwatch.Frequency / 1000)
        {
            PaceSleep(expected, ref SleepStats[1]);
        }
    }
    /// <summary>Instructions executed since the last ledger check (the block loop keeps the running count here).</summary>
    public long PendingInstructions;

    /// <summary>Guest instructions a system call costs on hardware (kernel entry, parameter copies, cache effects):
    /// HLE calls are free on the host, so the CPU ledger charges this per call.</summary>
    public static long HleCallCost = 1500;

    /// <summary>Charges host-side work (an HLE call, a memory copy done natively) to the emulated CPU ledger.</summary>
    public void ChargeInstructions(long n) { ChargedCycles += n; if (InstructionsPerSecond > 0) _throttleInstrs += n; }
    /// <summary>Profiling totals: cycles of executed guest blocks and cycles charged for host-side (HLE) work.</summary>
    public long BlockCycles, ChargedCycles;

    public ExitReason Run()
    {
        var s = State;
        s->Exit = ExitReason.None;
        // every ~20 us of guest CPU time when throttling (a physics step of a few ms must be paced from inside), else ~0.5 ms
        long checkWindow = InstructionsPerSecond > 0 ? InstructionsPerSecond / 4000 : 200_000; // ~0.25 ms of guest CPU time
        long sinceCheck = PendingInstructions;
        PendingInstructions = 0;
        while (!StopRequested)
        {
            if (Probes != null && Probes.Contains(s->R[15])) ProbeHit?.Invoke(this, s);
            var b = Lookup(s->R[15], s->Thumb != 0);
            if (b.NativeEntry != 0 && BlockHook == null && Native.NativeVerifier.Enabled)
            {
                // differential mode: native run checked against the interpreter, which provides the result
                Native.NativeVerifier.Run(s, b);
                BlocksExecuted++;
                InstructionsExecuted += b.Insts.Length;
                sinceCheck += b.Cycles; BlockCycles += b.Cycles;
            }
            else if (b.NativeEntry != 0 && BlockHook == null)
            {
                // Native code accounts each block it runs in the state and chains through linked exits until the
                // pacing window is full, an interrupt/stop is flagged or an exit cannot be linked: the same per-block
                // bookkeeping as the loop below, done by the generated code.
                s->CycleAcc = sinceCheck;
                s->CheckWindow = Probes != null ? 0 : checkWindow;
                s->LinkBreak = _interrupt || _stop ? 1u : 0u;
                s->NBlocks = 0; s->NInstr = 0;
                Native.NativeRuntime.Instance.Run(s, b.NativeEntry);
                if (s->CalloutFault != 0) Native.NativeRuntime.ThrowPendingFault(s);
                BlocksExecuted += s->NBlocks;
                InstructionsExecuted += s->NInstr;
                BlockCycles += s->CycleAcc - sinceCheck;
                sinceCheck = s->CycleAcc;
                if (s->LastExitSlot != 0) LinkExit(s);
                else if (s->TierUpRequest != 0) { s->TierUpRequest = 0; Jit.TierUp(Lookup(s->R[15], s->Thumb != 0)); }
            }
            else
            {
                var fn = b.Compiled;
                if (BlockHook != null && BlockHook(this, s, b)) { } // the hook executed the block itself
                else if (fn != null) fn(s);
                else
                {
                    Interpret(s, b);
                    if (++b.ExecCount >= Jit.MinExecutions && b.JitState == 0 && (long)b.ExecCount * b.Insts.Length >= b.HotAt) Jit.Enqueue(b);
                }
                BlocksExecuted++;
                InstructionsExecuted += b.Insts.Length;
                sinceCheck += b.Cycles; BlockCycles += b.Cycles;
            }
            if (sinceCheck >= checkWindow) { _throttleInstrs += sinceCheck; sinceCheck = 0; Throttle(); if (ShouldYield != null && ShouldYield()) InterruptRequested = true; }
            if (s->Exit != ExitReason.None) { PendingInstructions = sinceCheck; return s->Exit; }
            if (InterruptRequested) { InterruptRequested = false; return ExitReason.Yield; }
        }
        return ExitReason.Halt;
    }

    /// <summary>Single-block step (debugging / tests).</summary>
    public ExitReason Step()
    {
        var s = State;
        s->Exit = ExitReason.None;
        var b = Lookup(s->R[15], s->Thumb != 0);
        Interpret(s, b);
        return s->Exit;
    }

    public static void Interpret(CpuState* s, Block b)
    {
        var insts = b.Insts;
        var ptrs = HandlerTable.Pointers;
        for (int i = 0; i < insts.Length; i++)
        {
            ref var d = ref insts[i];
            if (d.EndsBlock || i == insts.Length - 1) s->R[15] = d.Address + d.Size;
            if ((d.Flags & InstFlags.Conditional) != 0 && !Arm.CondPassed(s, d.Cond)) continue;
            ptrs[(int)d.Op](s, d.Raw, d.Address);
        }
    }

    public void Dispose() => CpuState.Free(State);
}
