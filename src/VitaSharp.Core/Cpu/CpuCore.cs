using System.Runtime.CompilerServices;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Cpu;

/// <summary>Executes one guest thread's CPU state: block lookup → (JIT-compiled | interpreted) → exit handling.</summary>
public sealed unsafe class CpuCore : IDisposable
{
    public CpuState* State { get; }
    private readonly BlockCache _cache;

    // Small per-thread direct-mapped lookup in front of the shared dictionary.
    private const int FastSize = 4096;
    private readonly Block?[] _fast = new Block?[FastSize];

    private volatile bool _stop, _interrupt;
    public bool StopRequested
    {
        get => _stop;
        set
        {
            _stop = value;
            if (value)
            {
                State->LinkBreak = 1;
            }
        } // native code chained across blocks returns
    }
    /// <summary>Makes <see cref="Run"/> return <see cref="ExitReason.Yield"/> at the next block boundary.</summary>
    public bool InterruptRequested
    {
        get => _interrupt;
        set
        {
            _interrupt = value;
            if (value)
            {
                State->LinkBreak = 1;
            }
        }
    }
    /// <summary>Polled every ~0.5 ms of guest CPU time: true when the thread must give its core to a higher-priority one.</summary>
    public Func<bool>? ShouldYield;
    public long BlocksExecuted;
    /// <summary>Guest instructions executed by this thread (diagnostics; weighted cycles are in <see cref="BlockCycles"/>).</summary>
    public long InstructionsExecuted;

    /// <param name="cache">The process's translated code.</param>
    /// <param name="memBase">Guest address 0 of the process's address space (<see cref="Memory.AddressSpace.Base"/>);
    /// null = the creating thread's.</param>
    public CpuCore(BlockCache cache, byte* memBase = null)
    {
        _cache = cache;
        State = CpuState.Allocate();
        _memBase = memBase != null ? memBase : State->MemBase;
        State->MemBase = _memBase;
        HandlerTable.EnsureInitialized();
    }

    private readonly byte* _memBase;

    /// <summary>Points a (re)initialised state at the process's address space and indirect-exit table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Bind(CpuState* s)
    {
        if (s->MemBase == null)
        {
            s->MemBase = _memBase;
        }

        s->AbortGuard = _cache.DataAbortGuards ? 1u : 0u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Block Lookup(uint pc, bool thumb)
    {
        int slot = (int)((pc >> 1) & (FastSize - 1));
        Block? b = _fast[slot];
        if (b != null && b.Start == pc && b.Thumb == thumb && !b.Invalidated)
        {
            return b; // invalidated: guest rewrote the code
        }

        b = _cache.Get(pc, thumb);
        if (b.JitState == Jit.AheadPending)
        {
            Jit.ActivateAhead(b);
        }

        _fast[slot] = b;
        return b;
    }

    public void FlushFastCache() => Array.Clear(_fast);

    /// <summary>A native static exit returned through an empty link slot: point the slot at the next block if it is native.</summary>
    /// <summary>Classifies the native run that just returned (<see cref="NativeReturns"/>); an indirect miss is
    /// classified on the next loop turn, when its target block is looked up.</summary>
    private void CountNativeReturn(CpuState* s)
    {
        NativeReturn reason;
        if (s->Exit == ExitReason.Svc)
        {
            reason = NativeReturn.Svc;
        }
        else if (s->Exit != ExitReason.None)
        {
            reason = NativeReturn.OtherExit;
        }
        else if (s->TierUpRequest != 0)
        {
            reason = NativeReturn.TierUp;
        }
        else if (s->CycleAcc >= s->CheckWindow)
        {
            reason = NativeReturn.Window;
        }
        else if (s->LinkBreak != 0)
        {
            reason = NativeReturn.LinkBreak;
        }
        else if (s->LastExitSlot != 0)
        {
            if (*(nint*)s->LastExitSlot != 0)
            {
                // linked meanwhile by another thread of the process (the slot is shared): it went on unlinked
                reason = NativeReturn.StaticLinked;
            }
            else
            {
                reason = Lookup(s->R[15], s->Thumb != 0).NativeEntry != 0 ? NativeReturn.StaticLinked : NativeReturn.StaticNoCode;
            }
        }
        else
        {
            _pendingIndirect = true;
            return;
        }

        NativeReturns[(int)reason]++;
        if (CountReturnSites)
        {
            CountReturnSite(reason, s);
        }
    }

    /// <summary>Profiling (<see cref="EnvVars.NativeCounters"/>=1): returns to the dispatcher by reason and guest target,
    /// for the busiest ones on the PERF cycles line.</summary>
    public static bool CountReturnSites = Environment.GetEnvironmentVariable(EnvVars.NativeCounters) == "1";
    private Dictionary<ulong, long>? _returnSites;

    private void CountReturnSite(NativeReturn reason, CpuState* s)
    {
        if (reason is NativeReturn.Window or NativeReturn.LinkBreak)
        {
            return; // pacing: not tied to a target
        }

        Dictionary<ulong, long> sites = _returnSites ??= new Dictionary<ulong, long>();
        ulong key = (ulong)reason << 33 | (ulong)s->R[15] << 1 | (s->Thumb != 0 ? 1u : 0u);
        lock (sites) // the session report reads it from another thread
        {
            sites[key] = sites.GetValueOrDefault(key) + 1;
        }
    }

    /// <summary>Clears the return counts and sites (profiling windows, tests).</summary>
    public void ResetNativeReturns()
    {
        Array.Clear(NativeReturns);
        Array.Clear(_returnsAtReport);
        _framesAtReport = _framesAtSitesReport = _entriesAtReport = 0;
        NativeEntries = 0;
        if (_returnSites is { } sites)
        {
            lock (sites)
            {
                sites.Clear();
            }
        }
    }

    /// <summary>The <paramref name="count"/> busiest return sites since the previous call, "reason target[:T] n/frame"
    /// (<paramref name="frames"/>: the frames presented so far), or "" when not counted. Windowed like
    /// <see cref="NativeReturnReport"/>.</summary>
    public string NativeReturnSitesReport(long frames, int count = 6)
    {
        long window = Math.Max(1, frames - _framesAtSitesReport);
        _framesAtSitesReport = frames;
        if (_returnSites is not { Count: > 0 } sites)
        {
            return "";
        }

        KeyValuePair<ulong, long>[] busiest;
        lock (sites)
        {
            busiest = sites.OrderByDescending(kv => kv.Value).Take(count).ToArray();
            sites.Clear();
        }
        return string.Join(", ", busiest.Select(kv =>
            $"{(NativeReturn)(kv.Key >> 33)} {(uint)(kv.Key >> 1):X8}{((kv.Key & 1) != 0 ? ":T" : "")} {kv.Value / (double)window:F1}"));
    }

    /// <summary>The profiling counters of the PERF cycles line at one moment (<see cref="TakePerfWindow"/>).</summary>
    public readonly record struct PerfSample(long Frames, long BlockCycles, long BlocksExecuted, long ChargedCycles, long NativeTicks,
        long NativeHleTicks, long NativeEntries, long NativeReturnsUnlinked, long SimdOps, long CondBranches)
    {
        public static PerfSample operator -(PerfSample a, PerfSample b) => new(a.Frames - b.Frames, a.BlockCycles - b.BlockCycles,
            a.BlocksExecuted - b.BlocksExecuted, a.ChargedCycles - b.ChargedCycles, a.NativeTicks - b.NativeTicks,
            a.NativeHleTicks - b.NativeHleTicks, a.NativeEntries - b.NativeEntries, a.NativeReturnsUnlinked - b.NativeReturnsUnlinked,
            a.SimdOps - b.SimdOps, a.CondBranches - b.CondBranches);
    }

    private PerfSample _perfAtReport;

    /// <summary>The counters now, totals since the thread started (<paramref name="frames"/>: frames presented so far).</summary>
    public PerfSample PerfTotals(long frames) => new(frames, BlockCycles, BlocksExecuted, ChargedCycles, NativeTicks, NativeHleTicks,
        NativeEntries, NativeReturnsUnlinked, SimdOps, CondBranches);

    /// <summary>
    /// The counters since the previous call (the first call: since the thread started). The periodic PERF cycles lines
    /// report these windows: averages since boot are dominated by the JIT warm-up after a load (Killzone's "1,521 native
    /// entries per frame" were 240 in steady state). Read racily from the report thread (statistics).
    /// </summary>
    public PerfSample TakePerfWindow(long frames)
    {
        PerfSample now = PerfTotals(frames);
        PerfSample window = now - _perfAtReport;
        _perfAtReport = now;
        return window;
    }

    private readonly long[] _returnsAtReport = new long[(int)NativeReturn.Count];
    private long _framesAtReport, _framesAtSitesReport, _entriesAtReport;

    /// <summary>
    /// Native entries and returns by reason per frame since the previous call (<paramref name="frames"/>: the frames
    /// presented so far): "entries 240, Window 19, StaticNoCode 132, ...". Windowed on purpose: averages since boot are
    /// dominated by the JIT warm-up after a load (Killzone's "1,521 entries per frame" were 240 in steady state).
    /// </summary>
    public string NativeReturnReport(long frames)
    {
        double window = Math.Max(1, frames - _framesAtReport);
        _framesAtReport = frames;
        long entries = NativeEntries;
        var parts = new List<string> { $"entries {(entries - _entriesAtReport) / window:F1}" };
        _entriesAtReport = entries;
        for (int i = 0; i < (int)NativeReturn.Count; i++)
        {
            long now = NativeReturns[i];
            if (now != _returnsAtReport[i])
            {
                parts.Add($"{(NativeReturn)i} {(now - _returnsAtReport[i]) / window:F1}");
                _returnsAtReport[i] = now;
            }
        }
        return string.Join(", ", parts);
    }

    /// <summary>Native entries and returns by reason per frame over the whole run (the end-of-run summary).</summary>
    public string NativeReturnTotals(long frames)
    {
        double all = Math.Max(1, frames);
        var parts = new List<string> { $"entries {NativeEntries / all:F1}" };
        for (int i = 0; i < (int)NativeReturn.Count; i++)
        {
            if (NativeReturns[i] != 0)
            {
                parts.Add($"{(NativeReturn)i} {NativeReturns[i] / all:F1}");
            }
        }
        return string.Join(", ", parts);
    }

    private void LinkExit(CpuState* s)
    {
        nint slot = s->LastExitSlot;
        if (*(nint*)slot != 0)
        {
            return; // linked already (the exit returned for pacing or an interrupt)
        }

        Block next = Lookup(s->R[15], s->Thumb != 0);
        if (next.NativeEntry != 0)
        {
            Native.NativeRuntime.Link(slot, next);
        }
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
    public static long InstructionsPerSecond = long.TryParse(Environment.GetEnvironmentVariable(EnvVars.CpuMhz), out long mhz) ? mhz * 1_000_000 : DefaultClockHz;
    private long _throttleInstrs, _throttleStart = -1;
    /// <summary>True while the thread sleeps off CPU pacing (profiling).</summary>
    public volatile bool InThrottle;
    public struct SleepStat
    {
        public long Count, Requested, Actual;
    }
    public static readonly SleepStat[] SleepStats = new SleepStat[2];
    private static void Stat(ref SleepStat sleepStat, long t0, long expected)
    {
        Interlocked.Increment(ref sleepStat.Count);
        Interlocked.Add(ref sleepStat.Requested, expected - t0);
        Interlocked.Add(ref sleepStat.Actual, Util.GuestClock.Ticks - t0);
    }

    private void Throttle()
    {
        long rate = InstructionsPerSecond;
        if (rate <= 0 || Util.GuestClock.Turbo)
        {
            return;
        }

        long now = Util.GuestClock.Ticks, freq = System.Diagnostics.Stopwatch.Frequency;
        if (_throttleStart < 0)
        {
            _throttleStart = now;
            _throttleInstrs = 0;
            return;
        }
        long expected = _throttleStart + _throttleInstrs * freq / rate;
        if (expected < now - freq / 100)
        {
            // more than 10 ms behind: the thread spent that time in host-side HLE work (no guest instructions); the
            // ledger restarts there rather than banking credit. Blocking waits reset it explicitly (ResetThrottle).
            _throttleStart = now - freq / 100;
            _throttleInstrs = 0;
        }
        else if (expected > now + freq / 1000) // ahead by > 1 ms: sleep it off (no spinning: on a phone that is battery)
        {
            PaceSleep(expected, ref SleepStats[0]);
        }
    }

    /// <summary>How late host sleeps wake up, on average (ticks; shared: it is a property of the host's timers).</summary>
    private static long s_wakeLate;

    /// <summary>
    /// Sleeps off pacing until <paramref name="expected"/>. Host sleeps wake 0.3-0.7 ms late (more under load), and the
    /// thread can only make that up at the host's speed margin over the emulated CPU, which for CPU-heavy games is
    /// small: aiming early by the average lateness makes the wake-ups land on the deadline on average instead.
    /// </summary>
    private void PaceSleep(long expected, ref SleepStat stat)
    {
        // paused emulation: the guest clock stands still, there is nothing to pace against (the thread parks at its
        // next safe point, or it is the one finishing a GPU scene)
        if (EmulationPause.IsPaused)
        {
            return;
        }

        InThrottle = true;
        long t0 = Util.GuestClock.Ticks, target = Math.Max(t0, expected - Volatile.Read(ref s_wakeLate));
        Util.GuestClock.WaitUntil(target, allowSpin: false);
        if (target > t0)
        {
            long late = Math.Clamp(Util.GuestClock.Ticks - target, 0, s_maxWakeCredit), avg = Volatile.Read(ref s_wakeLate);
            Volatile.Write(ref s_wakeLate, avg + (late - avg) / 16); // racy by design: a lost update only slows the average
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
        if (rate <= 0 || Util.GuestClock.Turbo)
        {
            _throttleStart = -1;
            _throttleInstrs = 0;
            _blockedAt = _wakeDeadline = 0;
            return;
        }
        long start = now;
        if (_wakeDeadline != 0)
        {
            start = Math.Max(_wakeDeadline, now - s_maxWakeCredit); // timed wait: resume at its deadline
        }
        else if (_blockedAt != 0)
        {
            start = Math.Max(now, Math.Min(_blockedAt, now + s_maxWakeCredit)); // event: max(P, signal)
        }

        _throttleStart = start;
        _throttleInstrs = 0;
        _chargedSinceCheck = 0;
        _blockedAt = _wakeDeadline = 0;
    }

    /// <summary>Most wake-up lateness a timed wait may recover (ticks); bounds the effect of a stalled host.</summary>
    private static readonly long s_maxWakeCredit = System.Diagnostics.Stopwatch.Frequency / 500; // 2 ms
    private long _blockedAt, _wakeDeadline;

    /// <summary>The thread's emulated position when it last blocked (guest clock ticks), or real time when unpaced/
    /// unknown. Timed waits measure their delay from here: on hardware the delay starts where the thread is, and a
    /// thread still catching up after a late pacing wake-up (bounded) would have reached the call earlier.</summary>
    public long BlockedAt => _blockedAt != 0 ? Math.Max(_blockedAt, Util.GuestClock.Ticks - s_maxWakeCredit) : Util.GuestClock.Ticks;

    /// <summary>A timed wait (delay, vblank) ended: on hardware the thread resumes at <paramref name="deadline"/> (or
    /// where it blocked, if that was later), however late the host woke it.</summary>
    public void WokeAt(long deadline)
    {
        if (_blockedAt != 0)
        {
            _wakeDeadline = Math.Max(deadline, _blockedAt);
        }
    }

    /// <summary>
    /// Where this thread's emulated CPU time is (guest clock ticks), or 0 when unknown/unpaced. A paced thread runs in
    /// host-speed bursts and sleeps once it is ~1 ms ahead of real time; the clock it reads must follow its own
    /// progress through the burst (see <see cref="Util.GuestClock.Observed"/>), or a polling loop sees time stand
    /// still and then jump by the sleep length. Valid on the owning thread (inside an HLE call).
    /// </summary>
    public long EmulatedPosition()
    {
        long rate = InstructionsPerSecond;
        if (rate <= 0 || Util.GuestClock.Turbo || _throttleStart < 0)
        {
            return 0;
        }

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
        if (rate <= 0 || Util.GuestClock.Turbo || _throttleStart < 0)
        {
            return;
        }

        _throttleInstrs += pendingInstructions;
        _chargedSinceCheck = 0;
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

    /// <summary>
    /// Charges host-side work (an HLE call, a memory copy done natively) to the emulated CPU ledger. The charge counts
    /// toward the pacing window like executed guest cycles do (the way PPSSPP's hleEatCycles takes HLE time from the
    /// same downcount as MIPS code): once a window's worth is charged, the block loop checks the pacing at the next
    /// block boundary, and native code returns to it at its next exit (<see cref="CpuState.LinkBreak"/>). Counted only
    /// in guest cycles, a loop polling a costly call (Wipeout 2048's display callback spins on sceDisplayGetVcount,
    /// 12,000 cycles a call and ~25 cycles of its own code) ran ~3,000 calls (~100 ms of emulated time) between two
    /// checks and then slept it all off at once, past ~7 vblanks it was waiting for: 7.5 fps instead of 30.
    /// </summary>
    public void ChargeInstructions(long n)
    {
        ChargedCycles += n;
        if (InstructionsPerSecond > 0)
        {
            _throttleInstrs += n;
            _chargedSinceCheck += n;
            if (_chargedSinceCheck >= _checkWindow)
            {
                State->LinkBreak = 1;
            }
        }
    }

    /// <summary>Cycles charged by <see cref="ChargeInstructions"/> since the last pacing check (already in
    /// <see cref="_throttleInstrs"/>; this only decides when the next check runs).</summary>
    private long _chargedSinceCheck;

    /// <summary>The pacing window of the running <see cref="Run"/> (guest cycles between pacing checks).</summary>
    private long _checkWindow = DefaultClockHz / 4000;
    /// <summary>Profiling totals: cycles of executed guest blocks and cycles charged for host-side (HLE) work.</summary>
    public long BlockCycles, ChargedCycles;
    /// <summary>Profiling: host time (Stopwatch ticks) spent inside native-backend code, its callouts included.</summary>
    public long NativeTicks;
    /// <summary>Profiling: the part of <see cref="NativeTicks"/> spent in HLE calls made from native code (SVC callouts),
    /// blocking waits included: worker threads that wait on semaphores and event flags from native code showed ~35 ms per
    /// frame of "native time" for 0.3 M guest cycles, most of it asleep in the wait.</summary>
    public long NativeHleTicks;
    /// <summary>True while a timed native entry runs (<see cref="NativeTimingSample"/>): the SVC handler then adds the
    /// wall time of its HLE calls to <see cref="CalloutHleTicks"/>.</summary>
    public bool TimingNative;
    /// <summary>Wall ticks of HLE calls made from native code during the timed entry.</summary>
    public long CalloutHleTicks;
    /// <summary>Profiling: entries into native code, and returns with no exit slot and no exit reason (an indirect exit
    /// whose target was not in the dispatch table, or the pacing window / a link break at an indirect exit).</summary>
    public long NativeEntries, NativeReturnsUnlinked;

    /// <summary>Why native code returned to the dispatcher (profiling: the PERF cycles line), indexed by
    /// <see cref="NativeReturn"/>. A few compares per native entry, against hundreds of guest instructions run per entry.</summary>
    public readonly long[] NativeReturns = new long[(int)NativeReturn.Count];

    /// <summary>Reasons a native run ends (<see cref="NativeReturns"/>), in the order the dispatcher tells them apart.</summary>
    public enum NativeReturn
    {
        /// <summary>The guest raised an exit: an SVC the native callout left to the dispatcher (callback, return trap).</summary>
        Svc,
        /// <summary>Any other exit (undefined instruction, breakpoint, VFP vector mode, yield).</summary>
        OtherExit,
        /// <summary>The block asked for its IL tier-up or another FPSCR mode (the block is interpreted once).</summary>
        TierUp,
        /// <summary>The pacing window was full (guest cycles since the last pacing check).</summary>
        Window,
        /// <summary>An interrupt or stop was flagged (LinkBreak).</summary>
        LinkBreak,
        /// <summary>A static exit with an empty link slot whose target had native code: linked now.</summary>
        StaticLinked,
        /// <summary>A static exit with an empty link slot whose target has no native code yet (cold or compiling).</summary>
        StaticNoCode,
        /// <summary>An indirect exit missed the dispatch table and its target has native code (collision or eviction).</summary>
        IndirectTableMiss,
        /// <summary>An indirect exit whose target has no native code yet.</summary>
        IndirectNoCode,
        Count,
    }

    /// <summary>The reason of a native return whose target is looked up on the next loop turn (indirect exits).</summary>
    private bool _pendingIndirect;
    /// <summary>Profiling (VITASHARP_NATIVE_COUNTERS=1): VFP/NEON ops and conditional branches run in native code.</summary>
    public long SimdOps, CondBranches;
    /// <summary>Guest cycles (the block cost model, ~1 per instruction) this thread ran per execution tier: native code
    /// (its interpreter callouts included), IL blocks, and the interpreter (cold blocks, verification, VFP vector
    /// mode). Counted by the dispatcher from the cycle budget every native run keeps, so they hold in every build:
    /// ARM64 native code does not count its instructions by default (<see cref="Native.Arm64Emitter.A64Opt.NoBlockCounts"/>),
    /// and counting instructions read 0% native on every phone run. Flushed into <see cref="TierTotals"/> whenever
    /// <see cref="Run"/> returns.</summary>
    public long NativeCycles, IlCycles, InterpCycles;
    private long _flushedNative, _flushedIl, _flushedInterp;

    /// <summary>Process-wide totals of <see cref="NativeCycles"/>, <see cref="IlCycles"/> and
    /// <see cref="InterpCycles"/> (all guest threads, also the ones that ended): [native, il, interp].</summary>
    public static readonly long[] TierTotals = new long[3];

    /// <summary>Interpreted guest instructions of blocks with no compiled code, by <see cref="Block.JitState"/>: 0 not
    /// hot yet (or waiting for the next backend's threshold), 1 queued or compiling, 2 compiled but run interpreted
    /// (a native block that returned before running), 3 rejected by every backend. All threads, racy (statistics).</summary>
    public static readonly long[] InterpByJitState = new long[4];

    /// <summary>One line: where the executed guest code ran (CPU backend report).</summary>
    public static string TierReport()
    {
        long native = Volatile.Read(ref TierTotals[0]);
        long il = Volatile.Read(ref TierTotals[1]);
        long interp = Volatile.Read(ref TierTotals[2]);
        return FormatTierReport(native, il, interp, Native.NativeRuntime.CalloutHits.Sum(), InterpByJitState);
    }

    /// <summary>The <see cref="TierReport"/> line from the tier cycle totals, the interpreter callouts run from native
    /// code and the interpreted instructions by <see cref="Block.JitState"/>.</summary>
    public static string FormatTierReport(long nativeCycles, long ilCycles, long interpCycles, long callouts, ReadOnlySpan<long> interpByState)
    {
        long all = Math.Max(1, nativeCycles + ilCycles + interpCycles);
        long stateSum = 0;
        foreach (long count in interpByState)
        {
            stateSum += count;
        }

        double si = Math.Max(1, stateSum) / 100.0;
        return $"CPU tiers: executed guest cycles {all / 1_000_000} M: native {100.0 * nativeCycles / all:F2}% " +
               $"(of which ~{100.0 * callouts / all:F2}% in interpreter callouts), il {100.0 * ilCycles / all:F2}%, interpreter {100.0 * interpCycles / all:F2}% " +
               $"(blocks without compiled code, by state: not hot yet {interpByState[0] / si:F1}%, queued {interpByState[1] / si:F1}%, compiled but interpreted {interpByState[2] / si:F1}%, " +
               $"rejected {interpByState[3] / si:F1}%; verification and VFP vector mode not included)";
    }

    /// <summary>
    /// The bookkeeping after a native run: the blocks and instructions the generated code counted (none on ARM64 by
    /// default) and the guest cycles it spent from the budget it always keeps (<c>CycleAcc</c>, which started at
    /// <paramref name="sinceCheck"/>), which also make the native tier's share. Returns the new cycles since the last
    /// pacing check.
    /// </summary>
    public long AccountNativeRun(CpuState* s, long sinceCheck)
    {
        long cycles = s->CycleAcc - sinceCheck;
        BlocksExecuted += s->NBlocks;
        InstructionsExecuted += s->NInstr;
        BlockCycles += cycles;
        NativeCycles += cycles;
        return s->CycleAcc;
    }

    private void FlushTierCounts()
    {
        Interlocked.Add(ref TierTotals[0], NativeCycles - _flushedNative);
        _flushedNative = NativeCycles;
        Interlocked.Add(ref TierTotals[1], IlCycles - _flushedIl);
        _flushedIl = IlCycles;
        Interlocked.Add(ref TierTotals[2], InterpCycles - _flushedInterp);
        _flushedInterp = InterpCycles;
    }

    /// <summary>
    /// Handles a supervisor call inside <see cref="Run"/> (the guest thread's HLE dispatch): an HLE call returns to the
    /// block loop instead of leaving Run and entering it again, which on a phone cost more than most HLE functions do
    /// themselves (lwmutex lock/unlock run hundreds of thousands of times a second in Unity games).
    /// </summary>
    public interface ISvcHandler
    {
        /// <summary>False: not handled (Run returns <see cref="ExitReason.Svc"/>). True: handled; the guest continues at
        /// the state's pc, unless the handler set <c>s->Exit</c> (Run then returns that, e.g. <see cref="ExitReason.Handled"/>
        /// when the thread must be parked).</summary>
        bool TryHandleSvc(CpuState* s);

        /// <summary>The same from native code (an SVC callout, <see cref="Native.ArmFrontend.HleCallouts"/>): false leaves it to
        /// the dispatcher (the return trap, calls that run guest callbacks).</summary>
        bool TryHandleSvcNative(CpuState* s);
    }

    /// <summary>See <see cref="ISvcHandler"/>; null: every SVC leaves <see cref="Run"/>.</summary>
    public ISvcHandler? SvcHandler;

    /// <summary>The core the thread is running (HLE callouts from native code find its <see cref="SvcHandler"/> here).</summary>
    [ThreadStatic] public static CpuCore? CurrentCore;

    /// <summary>Profiling: one native entry in <see cref="NativeTimingSample"/> is timed (twice a clock read per entry
    /// was a few percent of a phone core when HLE calls enter and leave native code hundreds of thousands of times a
    /// second); <see cref="NativeTicks"/> is scaled back.</summary>
    private const int NativeTimingSample = 16;
    private int _nativeEntries;

    public ExitReason Run()
    {
        try
        {
            return RunBlocks();
        }
        finally
        {
            FlushTierCounts();
        }
    }

    private ExitReason RunBlocks()
    {
        CpuState* s = State;
        Bind(s);
        s->Exit = ExitReason.None;
        // every ~20 us of guest CPU time when throttling (a physics step of a few ms must be paced from inside), else ~0.5 ms
        long checkWindow = InstructionsPerSecond > 0 ? InstructionsPerSecond / 4000 : 200_000; // ~0.25 ms of guest CPU time
        _checkWindow = checkWindow;
        long sinceCheck = PendingInstructions;
        PendingInstructions = 0;
        // read once per Run: static reads cost a class-initialisation check each under Mono's AOT
        bool verify = Native.NativeVerifier.Enabled;
        CurrentCore = this;
        Native.NativeRuntime? native = null;
        bool resume = false; // an HLE call returned: native code continues through the dispatch table
        bool republish = false; // an indirect exit missed the dispatch table: the block found next takes its entry back
        bool interpretNext = false; // a native block returned before running (ARM64 FPSCR mode check): interpret it once
        while (!StopRequested)
        {
            Block? b = null;
            nint entry = 0;
            if (resume)
            {
                resume = false;
                entry = native!.ResumeEntry;
            }
            else if (s->ITState != 0)
            {
                // resuming inside an IT block (the return from a data abort taken there): the rest of the IT block
                // runs once in the interpreter with the IT state of the CPSR, then translated code goes on
                b = BlockBuilder.Build(s->R[15], s->Thumb != 0, s->ITState);
                s->ITState = 0;
                Interpret(s, b);
                BlocksExecuted++;
                InstructionsExecuted += b.Insts.Length;
                InterpCycles += b.Cycles;
                sinceCheck += b.Cycles;
                BlockCycles += b.Cycles;
                goto ran;
            }
            else if ((s->Fpscr & Vfp.FpscrVectorMask) != 0)
            {
                // VFP short-vector mode (FPSCR LEN/STRIDE): only the interpreter models it; generated code assumes
                // scalar VFP. VMSR ends its block and leaves native code (ExitReason.FpMode), so no generated code
                // runs in this mode.
                b = Lookup(s->R[15], s->Thumb != 0);
                InterpretVector(s, b);
                BlocksExecuted++;
                InstructionsExecuted += b.Insts.Length;
                InterpCycles += b.Cycles;
                sinceCheck += b.Cycles;
                BlockCycles += b.Cycles;
                goto ran;
            }
            else
            {
                if (Probes != null && Probes.Contains(s->R[15]))
                {
                    ProbeHit?.Invoke(this, s);
                }

                b = Lookup(s->R[15], s->Thumb != 0);
                if (b.NativeEntry != 0 && BlockHook == null && !verify && !interpretNext)
                {
                    entry = b.NativeEntry;
                }

                interpretNext = false;
                if (_pendingIndirect)
                {
                    _pendingIndirect = false;
                    NativeReturn indirect = b.NativeEntry != 0 ? NativeReturn.IndirectTableMiss : NativeReturn.IndirectNoCode;
                    NativeReturns[(int)indirect]++;
                    if (CountReturnSites)
                    {
                        CountReturnSite(indirect, s);
                    }
                }

                if (republish)
                {
                    republish = false;
                    if (entry != 0)
                    {
                        native!.Republish(b);
                    }
                }
            }
            if (entry == 0 && b!.NativeEntry != 0 && BlockHook == null && verify)
            {
                // differential mode: native run checked against the interpreter, which provides the result
                Native.NativeVerifier.Run(s, b);
                BlocksExecuted++;
                InstructionsExecuted += b.Insts.Length;
                InterpCycles += b.Cycles;
                sinceCheck += b.Cycles;
                BlockCycles += b.Cycles;
            }
            else if (entry != 0)
            {
                // Native code accounts each block it runs in the state and chains through linked exits until the
                // pacing window is full, an interrupt/stop is flagged or an exit cannot be linked: the same per-block
                // bookkeeping as the loop below, done by the generated code.
                s->CycleAcc = sinceCheck;
                s->CheckWindow = Probes != null ? 0 : checkWindow;
                s->LinkBreak = _interrupt || _stop ? 1u : 0u;
                s->NBlocks = 0;
                s->NInstr = 0;
                if (s->DispatchTable == 0)
                {
                    s->DispatchTable = _cache.DispatchTable;
                }

                native ??= Native.NativeRuntime.Instance;
                if (++_nativeEntries >= NativeTimingSample)
                {
                    _nativeEntries = 0;
                    TimingNative = true;
                    CalloutHleTicks = 0;
                    long n0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        native.Run(s, entry);
                    }
                    finally
                    {
                        TimingNative = false; // also when an HLE call throws out of native code (thread exit)
                    }

                    NativeTicks += (System.Diagnostics.Stopwatch.GetTimestamp() - n0) * NativeTimingSample;
                    NativeHleTicks += CalloutHleTicks * NativeTimingSample;
                }
                else
                {
                    native.Run(s, entry);
                }

                NativeEntries++;
                if (s->Exit == ExitReason.DataAbort && s->Thumb != 0)
                {
                    s->ITState = ItStateOfCached(s->SvcPc);
                }

                CountNativeReturn(s);
                if (s->LastExitSlot == 0 && s->Exit == ExitReason.None)
                {
                    NativeReturnsUnlinked++;
                    republish = true;
                }
                SimdOps += s->NSimdOps;
                CondBranches += s->NCondBranches;
                s->NSimdOps = 0;
                s->NCondBranches = 0;
                if (s->CalloutFault != 0)
                {
                    Native.NativeRuntime.ThrowPendingFault(s);
                }

                sinceCheck = AccountNativeRun(s, sinceCheck);
                if (s->LastExitSlot != 0)
                {
                    LinkExit(s);
                }
                else if (s->TierUpRequest == 2)
                {
                    s->TierUpRequest = 0;
                    interpretNext = true;
                } // FPSCR not in the mode the block assumes
                else if (s->TierUpRequest != 0)
                {
                    s->TierUpRequest = 0;
                    Jit.TierUp(Lookup(s->R[15], s->Thumb != 0));
                }
            }
            else
            {
                b ??= Lookup(s->R[15], s->Thumb != 0); // always looked up above (only a resume skips it, and it runs native code)
                JitBlockFn? fn = b.Compiled;
                if (BlockHook != null && BlockHook(this, s, b))
                {
                } // the hook executed the block itself
                else if (fn != null && Native.NativeVerifier.Enabled)
                {
                    Native.NativeVerifier.RunCompiled(s, b, fn);
                    IlCycles += b.Cycles;
                }
                else if (fn != null)
                {
                    fn(s);
                    if (s->Exit == ExitReason.DataAbort)
                    {
                        s->ITState = BlockBuilder.ItStateAt(b, s->SvcPc);
                    }

                    IlCycles += b.Cycles;
                    if (Backends.IlJitCompiler.CountExecutions)
                    {
                        b.ExecCount++; // IL dump index (racy: statistics)
                    }
                }
                else
                {
                    Interpret(s, b);
                    InterpCycles += b.Cycles;
                    InterpByJitState[b.JitState & 3] += b.Insts.Length; // racy on purpose: statistics only
                    if (++b.ExecCount >= Jit.MinExecutions && b.JitState == 0 && (long)b.ExecCount * b.Insts.Length >= b.HotAt)
                    {
                        Jit.Enqueue(b);
                    }
                    else if (b.JitState == Jit.AheadPending)
                    {
                        Jit.ActivateAhead(b); // translated in the background after this core last looked it up
                    }
                }
                BlocksExecuted++;
                InstructionsExecuted += b.Insts.Length;
                sinceCheck += b.Cycles;
                BlockCycles += b.Cycles;
            }
        ran:
            if (s->Exit == ExitReason.FpMode)
            {
                s->Exit = ExitReason.None; // VMSR changed the vector mode: the loop picks the path
            }

            if (sinceCheck >= checkWindow || _chargedSinceCheck >= checkWindow)
            {
                _throttleInstrs += sinceCheck;
                sinceCheck = 0;
                _chargedSinceCheck = 0;
                Throttle();
                if (ShouldYield != null && ShouldYield())
                {
                    InterruptRequested = true;
                }
            }
            if (s->Exit != ExitReason.None)
            {
                PendingInstructions = sinceCheck;
                if (s->Exit != ExitReason.Svc || SvcHandler is not { } svc)
                {
                    return s->Exit;
                }

                s->Exit = ExitReason.None;
                if (!svc.TryHandleSvc(s))
                {
                    s->Exit = ExitReason.Svc;
                    return ExitReason.Svc;
                }
                // handled in place: the HLE call may have settled the pending instructions (a blocking wait)
                sinceCheck = PendingInstructions;
                PendingInstructions = 0;
                if (s->Exit != ExitReason.None)
                {
                    PendingInstructions = sinceCheck;
                    return s->Exit;
                }
                resume = native != null && native.ResumeEntry != 0 && Probes == null && BlockHook == null && !verify
                    && (s->Fpscr & Vfp.FpscrVectorMask) == 0;
            }
            if (InterruptRequested)
            {
                InterruptRequested = false;
                return ExitReason.Yield;
            }
        }
        return ExitReason.Halt;
    }

    /// <summary>Single-block step (debugging / tests).</summary>
    public ExitReason Step()
    {
        CpuState* s = State;
        Bind(s);
        s->Exit = ExitReason.None;
        Block b = Lookup(s->R[15], s->Thumb != 0);
        Interpret(s, b);
        return s->Exit;
    }

    public static void Interpret(CpuState* s, Block b)
    {
        DecodedInst[] insts = b.Insts;
        delegate*<CpuState*, uint, uint, void>[] ptrs = HandlerTable.Pointers;
        int i = 0;
        try
        {
            for (; i < insts.Length; i++)
            {
                ref DecodedInst d = ref insts[i];
                if (d.EndsBlock || i == insts.Length - 1)
                {
                    s->R[15] = d.Address + d.Size;
                }

                if ((d.Flags & InstFlags.Conditional) != 0 && !Arm.CondPassed(s, d.Cond))
                {
                    continue;
                }

                ptrs[(int)d.Op](s, d.Raw, d.Address);
            }
        }
        catch (GuestDataAbortException)
        {
            RaiseDataAbort(s, b, i);
        }
    }

    /// <summary>The IT state of the Thumb instruction at <paramref name="pc"/>, from a translated block holding it (a data
    /// abort in native code, which may have run several linked blocks).</summary>
    private uint ItStateOfCached(uint pc)
    {
        foreach (Block b in _cache.Blocks)
        {
            if (b.Thumb && pc >= b.Start && pc < b.End)
            {
                return BlockBuilder.ItStateAt(b, pc);
            }
        }

        return 0;
    }

    /// <summary>Instruction <paramref name="index"/> of <paramref name="b"/> took a data abort (<see cref="DataAborts"/>):
    /// the block stops there with <see cref="ExitReason.DataAbort"/>, R15 and SvcPc at the instruction and the IT
    /// state it ran in.</summary>
    internal static void RaiseDataAbort(CpuState* s, Block b, int index)
    {
        uint pc = b.Insts[index].Address;
        s->R[15] = pc;
        s->SvcPc = pc;
        s->ITState = BlockBuilder.ItStateAt(b, pc);
        s->Exit = ExitReason.DataAbort;
    }

    private static bool s_vectorModeSeen;

    /// <summary><see cref="Interpret"/> in VFP short-vector mode: the vector-capable VFP instructions run as vectors.</summary>
    private static void InterpretVector(CpuState* s, Block b)
    {
        if (!s_vectorModeSeen)
        {
            s_vectorModeSeen = true;
            Util.Log.Info($"VFP short-vector mode in use (FPSCR 0x{s->Fpscr:X8}, block 0x{b.Start:X8})");
        }
        DecodedInst[] insts = b.Insts;
        delegate*<CpuState*, uint, uint, void>[] ptrs = HandlerTable.Pointers;
        int i = 0;
        try
        {
            for (; i < insts.Length; i++)
            {
                ref DecodedInst d = ref insts[i];
                if (d.EndsBlock || i == insts.Length - 1)
                {
                    s->R[15] = d.Address + d.Size;
                }

                if ((d.Flags & InstFlags.Conditional) != 0 && !Arm.CondPassed(s, d.Cond))
                {
                    continue;
                }

                if ((s->Fpscr & Vfp.FpscrVectorMask) != 0 && Vfp.IsVectorCapable(d.Op, d.Raw))
                {
                    Vfp.VectorOp(s, d.Op, d.Raw, d.Address);
                }
                else
                {
                    ptrs[(int)d.Op](s, d.Raw, d.Address);
                }
            }
        }
        catch (GuestDataAbortException)
        {
            RaiseDataAbort(s, b, i);
        }
    }

    public void Dispose() => CpuState.Free(State);
}
