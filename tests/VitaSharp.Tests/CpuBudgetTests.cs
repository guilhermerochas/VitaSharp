using System.Diagnostics;
using VitaSharp.Core.Capture;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// Budget tests: the dispatcher paths a hot guest loop must take once warm, checked with deterministic counts of the
/// thread's phase profile (<see cref="PhaseProfile"/>) instead of wall-clock time: a hot block runs as code (no
/// interpreted cycles, no interpreter callouts), nothing is compiled on the guest thread, block switches are linked
/// (native turns only at the pacing checks), and VFP short-vector blocks run as code. Each of these slow paths alone
/// kept Dead Space's mixer round over the 11 ms its audio consumer waits (VFP vector blocks only in the interpreter, a
/// 13 ms compile on the guest thread). Short wait timeouts are covered by PreciseTimeoutTests.
/// </summary>
[Collection("guest")]
[Trait("Category", "Budget")]
public sealed unsafe class CpuBudgetTests(GuestMemoryFixture fx)
{
    private const uint CodeBase = 0x0011_0000;
    private const uint BaseFpscr = 0x0300_0000; // DN + FZ (the Vita runtime's mode)
    private const uint Svc = 0xEF000000;

    /// <summary>vmsr fpscr, r0; loop: vadd.f32 s8, s16, s24; subs r2, r2, #1; bne loop; vmsr fpscr, r1; svc.</summary>
    private static readonly uint[] VectorLoop = [0xEEE10A10, 0xEE384A0C, 0xE2522001, 0x1AFFFFFC, 0xEEE11A10, Svc];

    /// <summary>loop: vadd.f32 s8, s16, s24; add r3, r3, #1; subs r2, r2, #1; bne loop; svc.</summary>
    private static readonly uint[] ScalarLoop = [0xEE384A0C, 0xE2833001, 0xE2522001, 0x1AFFFFFB, Svc];

    /// <summary>Three blocks per iteration: add r3; b next; add r4; b next; subs r2; bne first; svc.</summary>
    private static readonly uint[] SwitchLoop = [0xE2833001, 0xEAFFFFFF, 0xE2844001, 0xEAFFFFFF, 0xE2522001, 0x1AFFFFF9, Svc];

    private static bool HasBackend => Jit.Enabled && Jit.Chain.Length > 0 && PhaseProfile.Enabled;

    private void MapCode(uint[] words)
    {
        if (fx.Memory.FindBlock(CodeBase) == null)
        {
            fx.Memory.MapFixed(CodeBase, 0x1000, "budget code");
        }

        byte[] bytes = words.SelectMany(BitConverter.GetBytes).ToArray();
        bytes.CopyTo(GuestMemory.Span(CodeBase, bytes.Length));
    }

    private static void RunLoop(CpuCore core, uint iterations)
    {
        CpuState* s = core.State;
        s->R[0] = BaseFpscr | (3 << 16); // LEN = 4
        s->R[1] = BaseFpscr;
        s->R[2] = iterations;
        s->R[15] = CodeBase;
        s->Cpsr = 0x10;
        s->Fpscr = BaseFpscr;
        Assert.Equal(ExitReason.Svc, core.Run());
        Assert.Equal(BaseFpscr, s->Fpscr); // the loops end in scalar mode
    }

    /// <summary>Runs the loop until all its blocks are hot (the exit block too: once per run, so the loop runs more
    /// times than the JIT's threshold), compiled and published, then once more to link them.</summary>
    private static void WarmUp(CpuCore core)
    {
        for (int i = 0; i < Jit.MinExecutions * 2; i++)
        {
            RunLoop(core, 100);
        }

        Assert.True(Jit.WaitIdle(TimeSpan.FromSeconds(60)), "the JIT queue did not drain");
        RunLoop(core, 100);
    }

    [Fact]
    public void HotBlockRunsAsCodeWithNoInterpretedCyclesOrCallouts()
    {
        if (!HasBackend)
        {
            return; // no backend on this host: only the interpreter exists
        }

        MapCode(ScalarLoop);
        using var core = new CpuCore(new BlockCache());
        WarmUp(core);
        PhaseProfile p = core.Phases;
        long interpreted = p.TurnsOf(CpuPhase.Interpret);
        long callouts = p.InterpreterCallouts;
        long hot = p.HotInterpretedCycles; // while the compile was queued (warm-up)
        long compiled = p.CyclesOf(CpuPhase.Native) + p.CyclesOf(CpuPhase.Il);
        RunLoop(core, 20_000);
        // the loop body never runs interpreted; only the exit block (svc), run once per call and so never hot, may
        Assert.True(p.TurnsOf(CpuPhase.Interpret) - interpreted <= 1, $"{p.TurnsOf(CpuPhase.Interpret) - interpreted} blocks interpreted in a warm loop");
        Assert.Equal(0, p.InterpreterCallouts - callouts);
        Assert.True(p.CyclesOf(CpuPhase.Native) + p.CyclesOf(CpuPhase.Il) - compiled >= 20_000 * 4, "the loop did not run as compiled code");
        Assert.Equal(hot, p.HotInterpretedCycles);
    }

    [Fact]
    public void NothingIsCompiledOnTheGuestThread()
    {
        if (!HasBackend)
        {
            return;
        }

        MapCode(ScalarLoop);
        using var core = new CpuCore(new BlockCache());
        WarmUp(core);
        MapCode(VectorLoop);
        using var vectorCore = new CpuCore(new BlockCache());
        RunLoop(vectorCore, 3); // the second run of the vector block queues its compile
        Assert.True(CpuCore.WaitVectorCompiles(TimeSpan.FromSeconds(60)), "the background compile did not finish");
        RunLoop(vectorCore, 100);
        Assert.Equal(0, core.Phases.GuestThreadCompiles);
        Assert.Equal(0, vectorCore.Phases.GuestThreadCompiles);
    }

    /// <summary>The detector itself: a compile on the guest thread (the test seam that compiles vector blocks inline)
    /// is counted, timed and raises an alert past the budget.</summary>
    [Fact]
    public void ACompileOnTheGuestThreadIsCountedAndAlerted()
    {
        if (!HasBackend)
        {
            return;
        }

        MapCode(VectorLoop);
        using var core = new CpuCore(new BlockCache());
        long budget = PhaseProfile.CompileBudgetTicks;
        CpuCore.CompileVectorBlocksInline = true;
        PhaseProfile.CompileBudgetTicks = 0;
        try
        {
            RunLoop(core, 3);
        }
        finally
        {
            CpuCore.CompileVectorBlocksInline = false;
            PhaseProfile.CompileBudgetTicks = budget;
        }

        Assert.True(core.Phases.GuestThreadCompiles >= 1, "the inline compile was not counted");
        Assert.True(core.Phases.GuestThreadCompileTicks > 0);
        Assert.True(core.Phases.AlertCount >= 1, "no alert for a compile over the budget");
    }

    [Fact]
    public void HotVectorModeLoopInterpretsNothingOnceWarm()
    {
        if (!HasBackend)
        {
            return;
        }

        MapCode(VectorLoop);
        using var core = new CpuCore(new BlockCache());
        RunLoop(core, 3);
        Assert.True(CpuCore.WaitVectorCompiles(TimeSpan.FromSeconds(60)), "the background compile did not finish");
        RunLoop(core, 3); // the exit block (vmsr; svc) runs once per loop in vector mode: its second run queues it
        Assert.True(CpuCore.WaitVectorCompiles(TimeSpan.FromSeconds(60)), "the background compile did not finish");
        PhaseProfile p = core.Phases;
        long interpreted = p.CyclesOf(CpuPhase.InterpretVector);
        long code = p.CyclesOf(CpuPhase.VectorCode);
        long hot = p.HotInterpretedCycles;
        RunLoop(core, 2000);
        Assert.Equal(0, p.CyclesOf(CpuPhase.InterpretVector) - interpreted);
        Assert.True(p.CyclesOf(CpuPhase.VectorCode) - code >= 2000, "the vector-mode block did not run as code");
        Assert.Equal(hot, p.HotInterpretedCycles);
    }

    /// <summary>Block switches are linked once warm: native code chains through the three blocks of every iteration and
    /// returns to the dispatcher only at the pacing checks (not once per block).</summary>
    [Fact]
    public void BlockSwitchesStayInNativeCodeOnceWarm()
    {
        if (!HasBackend || !Jit.Chain.Any(c => c is Core.Cpu.Backends.NativeX64Compiler or Core.Cpu.Backends.NativeArm64Compiler))
        {
            return; // linking is a native-code feature
        }

        MapCode(SwitchLoop);
        using var core = new CpuCore(new BlockCache());
        WarmUp(core);
        PhaseProfile p = core.Phases;
        long turns = p.TurnsOf(CpuPhase.Native) + p.TurnsOf(CpuPhase.Interpret) + p.TurnsOf(CpuPhase.Il);
        long cycles = PhaseProfile.TotalCycles(p.Snapshot());
        const uint Iterations = 30_000;
        RunLoop(core, Iterations);
        long ran = PhaseProfile.TotalCycles(p.Snapshot()) - cycles;
        long checkWindow = CpuCore.InstructionsPerSecond > 0 ? CpuCore.InstructionsPerSecond / 4000 : 200_000;
        long dispatched = p.TurnsOf(CpuPhase.Native) + p.TurnsOf(CpuPhase.Interpret) + p.TurnsOf(CpuPhase.Il) - turns;
        Assert.True(ran >= Iterations * 3, $"ran {ran} cycles");
        Assert.True(dispatched <= ran / checkWindow + 8, $"{dispatched} dispatcher turns for {Iterations * 3} block switches ({ran} cycles, pacing check every {checkWindow})");
    }

    /// <summary>The budget window: host time per guest cycle over the budget raises one alert naming the phase; under
    /// the budget, none. Synthetic counters (no clock).</summary>
    [Fact]
    public void WindowOverTheBudgetAlertsWithThePhase()
    {
        long ticksPerCycleAt600Ns = Math.Max(1, (long)(600e-9 * Stopwatch.Frequency * 1000)); // per 1000 cycles
        var slow = new PhaseProfile { ThreadName = "budget-slow" };
        var fast = new PhaseProfile { ThreadName = "budget-fast" };
        for (int i = 0; i < 300; i++)
        {
            slow.Count(CpuPhase.InterpretVector, 1000);
            slow.AddTimed(CpuPhase.InterpretVector, ticksPerCycleAt600Ns, 0, 0, 1);
            fast.Count(CpuPhase.Native, 1000);
            fast.AddTimed(CpuPhase.Native, 0, 0, 0, 1);
        }

        slow.CheckWindow();
        fast.CheckWindow();
        Assert.Equal(1, slow.AlertCount);
        Assert.Equal(0, fast.AlertCount);
        string line = PhaseProfile.Format("budget-slow", slow.Snapshot(), Stopwatch.Frequency);
        Assert.Contains("interp-vector 0.30 M cycles 100%", line);
    }

    [Fact]
    public void HotBlocksInterpretedAreAlerted()
    {
        var p = new PhaseProfile { ThreadName = "budget-hot" };
        for (int i = 0; i < 300; i++)
        {
            p.Count(CpuPhase.Interpret, 1000);
            p.Interpreted(PhaseProfile.HotExecutions, 1000);
        }

        p.CheckWindow();
        Assert.Equal(300_000, p.HotInterpretedCycles);
        Assert.Equal(1, p.AlertCount);
    }

    /// <summary>F8: the context.txt lines of the start and the stop give report.txt's "CPU phases" for the recording.</summary>
    [Fact]
    public void CaptureReportShowsThePhasesOfTheRecording()
    {
        var p = new PhaseProfile { ThreadName = "mixer|1" };
        p.Count(CpuPhase.Interpret, 5000);
        string start = p.ContextLine();
        p.Count(CpuPhase.InterpretVector, 40_000);
        p.CompiledOnGuestThread(1, 0x81000000);
        string stop = p.ContextLine();
        Assert.NotNull(PhaseProfile.ParseContextLine(stop["cpu-phases: ".Length..]));
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-phases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "log.txt"), "12:00:00 [W] [phase-alert] thread 'mixer': 612 ns of host time per guest cycle\n");
            List<string> lines = CaptureAnalyzer.CpuPhasesSection(dir, [start], [stop]);
            string all = string.Join("\n", lines);
            Assert.StartsWith("CPU phases", lines[0]);
            Assert.Contains("PERF phases mixer/1: 0.04 M guest cycles", all); // 40 000 cycles of the recording only
            Assert.Contains("compiled on the thread 1", all);
            Assert.Contains("612 ns of host time per guest cycle", all);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
