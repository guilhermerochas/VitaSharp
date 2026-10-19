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

    public volatile bool StopRequested;
    /// <summary>Makes <see cref="Run"/> return <see cref="ExitReason.Yield"/> at the next block boundary.</summary>
    public volatile bool InterruptRequested;
    /// <summary>Polled every ~0.5 ms of guest CPU time: true when the thread must give its core to a higher-priority one.</summary>
    public Func<bool>? ShouldYield;
    public long BlocksExecuted;

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
        if (b != null && b.Start == pc && b.Thumb == thumb) return b;
        b = _cache.Get(pc, thumb);
        _fast[slot] = b;
        return b;
    }

    public void FlushFastCache() => Array.Clear(_fast);

    /// <summary>Run until the guest raises an exit (SVC, undefined, breakpoint, yield) or a stop is requested.</summary>
    /// <summary>Debug probes: block-start addresses that invoke <see cref="ProbeHit"/> before executing.</summary>
    public static HashSet<uint>? Probes;
    public static ProbeFn? ProbeHit;
    public delegate void ProbeFn(CpuCore core, CpuState* s);

    /// <summary>
    /// Emulated CPU speed in guest instructions per second per thread (0 = unlimited). The Vita's Cortex-A9 runs games
    /// at 333 MHz; games budget work by measuring process time, so executing at host speed makes them do far more work
    /// per frame than on hardware (streaming / job loops) and changes their behaviour.
    /// </summary>
    /// <summary>
    /// Emulated CPU speed in cycles per second (see <see cref="BlockBuilder.CycleCost"/>); 0 runs unpaced. The Vita's
    /// Cortex-A9 runs at 444 MHz for games (sceKernelPowerSetArmClockFrequency may change it), and pacing to it is what
    /// makes time-budgeted game code (physics sub-stepping, frame timers) behave as on hardware.
    /// </summary>
    public static long InstructionsPerSecond = long.TryParse(Environment.GetEnvironmentVariable("VITASHARP_CPU_MHZ"), out var mhz) ? mhz * 1_000_000 : 444_000_000;
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
            InThrottle = true; long t0 = Util.GuestClock.Ticks; Util.GuestClock.WaitUntil(expected, allowSpin: false); InThrottle = false; Stat(ref SleepStats[0], t0, expected);
        }
    }

    /// <summary>Called when the thread resumes after a blocking wait: time spent blocked is not CPU time.</summary>
    public void ResetThrottle() { _throttleStart = -1; _throttleInstrs = 0; }

    /// <summary>Called before the thread blocks: work done since the last check must take its emulated time first, so
    /// short bursts between waits (job systems) are paced too, not only long runs.</summary>
    public void SettleThrottle(long pendingInstructions)
    {
        long rate = InstructionsPerSecond;
        if (rate <= 0 || Util.GuestClock.Turbo || _throttleStart < 0) return;
        _throttleInstrs += pendingInstructions;
        long expected = _throttleStart + _throttleInstrs * System.Diagnostics.Stopwatch.Frequency / rate;
        if (expected > Util.GuestClock.Ticks) { InThrottle = true; long t0 = Util.GuestClock.Ticks; Util.GuestClock.WaitUntil(expected, allowSpin: false); InThrottle = false; Stat(ref SleepStats[1], t0, expected); }
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
            var fn = b.Compiled;
            if (fn != null) fn(s);
            else
            {
                Interpret(s, b);
                if (++b.ExecCount >= 64 && b.JitState == 0 && (long)b.ExecCount * b.Insts.Length >= Jit.HotInstructions) Jit.Enqueue(b);
            }
            BlocksExecuted++;
            sinceCheck += b.Cycles; BlockCycles += b.Cycles;
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
