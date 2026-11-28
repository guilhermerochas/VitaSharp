using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;

namespace VitaSharp.Core.Util;

/// <summary>
/// The statistics a run writes when it stops (the desktop Runner, the Android EmulationSession): JIT and CPU backend
/// report, the busiest HLE functions, guest threads and processes, PERF cycles per thread, held mutexes, the GPU
/// backend's report (PERF texcache / gpu / sync / vram / mobile...), video decoding, the AUDIO summary, debug-mode
/// summaries and the host process (PERF cpu, threads, allocations). <see cref="PeriodicLines"/> is the subset that
/// measures performance, for runs that may be killed before they stop (Android).
/// </summary>
public static class SessionReport
{
    /// <summary>Every end-of-run line, in the order the desktop always wrote them.</summary>
    public static IEnumerable<string> Lines(Emulator emu)
    {
        yield return $"Stopping. {VitaSharp.Core.Cpu.Jit.Describe()}";
        foreach (string l in CpuBackendLines())
        {
            yield return l;
        }

        foreach (HleFunction? f in emu.Hle.Functions.Where(f => f.CallCount > 0).OrderByDescending(f => f.CallCount).Take(40))
        {
            yield return $"  {f.CallCount,8} x {f.Name}{(f.Implemented ? "" : " (unimplemented)")}";
        }

        if (Environment.GetEnvironmentVariable(EnvVars.HleCalls) == "1")
        {
            foreach (Emulator? p in (emu.Vita?.Processes ?? []).Prepend(emu).DistinctBy(p => p.Hle))
            {
                foreach (HleFunction? f in p.Hle.Functions.Where(f => f.CallCount > 0).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    yield return $"  hle-call {p.TitleId} {f.CallCount} {f.Name}{(f.Implemented ? "" : " (unimplemented)")}";
                }
            }
        }

        foreach (GuestThread t in emu.Kernel.Threads)
        {
            yield return "  thread " + t.Describe();
        }

        foreach (string line in Kernel.WaitGraph.Lines(emu.Kernel.Threads))
        {
            yield return "  " + line;
        }
        // the other processes of the system (applications the shell started)
        foreach (Emulator? p in emu.Vita?.Processes.Where(p => !ReferenceEquals(p, emu)) ?? [])
        {
            yield return $"  process {p.Describe()}{(p.Suspended ? " (suspended)" : "")}:";
            var lines = new List<string>();
            using (VitaSharp.Core.Memory.AddressSpace.Use(p.Space))
            {
                foreach (GuestThread t in p.Kernel.Threads)
                {
                    lines.Add("    thread " + t.Describe());
                }
            }

            foreach (string l in lines)
            {
                yield return l;
            }
        }
        foreach (string l in CycleLines(emu, window: false))
        {
            yield return l;
        }

        foreach (string l in PhaseLines(emu, window: false))
        {
            yield return l;
        }
        // VITASHARP_PEEK_END=ADDR:LEN[,ADDR:LEN...] hex-dumps guest memory when the run stops (state of a hang)
        if (Environment.GetEnvironmentVariable(EnvVars.PeekEnd) is { } peekEnd)
        {
            foreach (string spec in peekEnd.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pp = spec.Split(':');
                uint pa = Convert.ToUInt32(pp[0], 16), pl = Convert.ToUInt32(pp[1], 16);
                var sb = new System.Text.StringBuilder($"  peek-end {pa:X8}:");
                for (uint i = 0; i < pl; i += 4)
                {
                    if (i % 32 == 0)
                    {
                        sb.Append($"\n   {pa + i:X8}:");
                    }

                    sb.Append($" {VitaSharp.Core.Memory.GuestMemory.R32(pa + i):X8}");
                }
                yield return sb.ToString();
            }
        }
        // held mutexes with waiters: the first thing to look at when threads sit in "wait=[mutex ...]" (deadlocks)
        foreach (Kernel.Mutex m in emu.Kernel.Objects.All<VitaSharp.Core.Kernel.Mutex>())
        {
            if (m.Owner != null && m.Waiters > 0)
            {
                yield return $"  mutex '{m.Name}' held by '{m.Owner.Name}' ({m.Owner.Status}) count {m.LockCount}, {m.Waiters} waiting";
            }
        }

        foreach (string l in IoTiming.SummaryLines())
        {
            yield return "  " + l;
        }

        foreach (string l in GpuLines(emu))
        {
            yield return l;
        }

        foreach (string l in AudioDiagnostics.SummaryLines())
        {
            yield return l; // always: ports, outputs/s, underruns per minute
        }

        foreach (string l in VitaSharp.Core.Hle.Modules.Ngs.PerfLines())
        {
            yield return l;
        }

        foreach (string l in DebugSummaryLines())
        {
            yield return l;
        }

        // since the previous PERF allocators line (the periodic one on Android, the steady-state mark on the desktop)
        if (AllocatorsLine(emu) is { } allocators)
        {
            yield return allocators;
        }

        foreach (string l in ProcessLines())
        {
            yield return l;
        }

        // the plugin hooks: each one's installer, calls and continuations, "never called" for those no call entered
        foreach (string l in VitaSharp.Core.Hle.Modules.PluginFramework.HookSummaryLines(emu))
        {
            yield return l;
        }
    }

    /// <summary>
    /// The performance lines of a run that is still going (Android writes them every 30 s to its log file: the
    /// system often kills the game process instead of stopping it): CPU backend report, PERF cycles, the GPU report
    /// (PERF gpu, PERF mobile...), video decoding, the AUDIO summary and the host process. Safe to call while the
    /// emulation runs: a part that fails to read is left out.
    /// </summary>
    public static IEnumerable<string> PeriodicLines(Emulator emu)
    {
        var lines = new List<string> { $"PERF periodic summary: frame {VitaSharp.Core.Hle.Modules.Display.FramesPresented}" };
        void Add(Func<IEnumerable<string>> part)
        {
            try
            {
                lines.AddRange(part());
            }
            catch (Exception e)
            {
                lines.Add($"PERF periodic summary: {e.GetType().Name} ({e.Message})");
            }
        }
        Add(CpuBackendLines);
        Add(() => CycleLines(emu, window: true));
        Add(() => PhaseLines(emu, window: true));
        Add(() => [HleCallsLine(emu)]);
        Add(() => AllocatorsLine(emu) is { } allocators ? [allocators] : []);
        Add(() => HleTimeReport.Lines(emu));
        Add(() => GpuLines(emu));
        Add(AudioDiagnostics.SummaryLines);
        Add(() => [GuestClock.LagLine()]);
        Add(VitaSharp.Core.Hle.Modules.Ngs.PerfLines);
        Add(ProcessLines);
        Add(() => StallLines(emu));
        Add(() => ThreadStateSampler.Lines(emu));
        return lines;
    }

    /// <summary>"PERF allocators" since the previous call (null on the first one): the desktop Runner calls it once at
    /// the steady-state mark (VITASHARP_WORKER_PROF_FROM) so the end-of-run line leaves the boot out.</summary>
    public static string? AllocatorsLine(Emulator emu) =>
        AllocLedger.Line(VitaSharp.Core.Hle.Modules.Display.FramesPresented,
            emu.Hle.Functions.Where(f => f.AllocatedBytes > 0).Select(f => ("HLE " + f.Name, f.AllocatedBytes)));

    /// <summary>Starts the PERF gc window and PERF allocators measurements at <paramref name="frame"/> (the desktop's
    /// steady-state mark): their end-of-run lines then cover only the stretch after it.</summary>
    public static void MarkAllocations(Emulator emu, long frame)
    {
        AllocatorsLine(emu);
        GcWindow.Line(frame);
    }

    private static long s_lastPeriodicFrame = -1;
    private static long s_stallReportedAt = -1;

    /// <summary>
    /// "PERF stall": when no frame was presented since the previous summary while the emulation runs, every guest
    /// thread once per stall (status, what it waits on or the HLE call it is in, PC and backtrace): the phone has no
    /// sampler, and a game frozen at 0 fps (Uncharted after a load state at its title screen, 13/11/2026) otherwise
    /// left only "frame N" lines to read.
    /// </summary>
    private static IEnumerable<string> StallLines(Emulator emu)
    {
        long frame = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
        long last = s_lastPeriodicFrame;
        s_lastPeriodicFrame = frame;
        if (frame != last || frame == 0 || EmulationPause.IsPaused || s_stallReportedAt == frame)
        {
            return [];
        }

        s_stallReportedAt = frame;
        var lines = new List<string> { $"PERF stall: no frame presented since frame {frame}; guest threads:" };
        foreach (Kernel.GuestThread t in emu.Kernel.Threads)
        {
            lines.Add("PERF stall:   " + t.Describe());
        }

        foreach (string line in Kernel.WaitGraph.Lines(emu.Kernel.Threads))
        {
            lines.Add("PERF stall:   " + line);
        }

        return lines;
    }

    /// <summary>The busiest HLE functions so far, on one line (the end-of-run report lists 40, one per line).</summary>
    private static string HleCallsLine(Emulator emu)
    {
        long frames = Math.Max(1, VitaSharp.Core.Hle.Modules.Display.FramesPresented);
        IEnumerable<string> top = emu.Hle.Functions.Where(f => f.CallCount > 0).OrderByDescending(f => f.CallCount).Take(12)
            .Select(f => $"{f.Name} {f.CallCount} ({f.CallCount / (double)frames:F0}/frame)");
        return $"PERF hle calls: {string.Join(", ", top)}; GXM uniforms copied {VitaSharp.Core.Hle.Modules.Gxm.UniformBytesCopied / 1024.0 / frames:F0} KB/frame";
    }

    /// <summary>Native JIT verification and the native backend's report.</summary>
    private static IEnumerable<string> CpuBackendLines()
    {
        if (VitaSharp.Core.Cpu.Native.NativeVerifier.Enabled)
        {
            yield return $"CPU backend report: native verify: {VitaSharp.Core.Cpu.Native.NativeVerifier.Verified} block runs verified, {VitaSharp.Core.Cpu.Native.NativeVerifier.Mismatches} mismatches ({VitaSharp.Core.Cpu.Native.NativeVerifier.SimdMismatches} in blocks with NEON/VFP ops), {VitaSharp.Core.Cpu.Native.NativeVerifier.Skipped} interpreted (unloggable stores)";
        }

        if (VitaSharp.Core.Cpu.Jit.Chain.Any(c => c is VitaSharp.Core.Cpu.Backends.NativeX64Compiler or VitaSharp.Core.Cpu.Backends.NativeArm64Compiler))
        {
            yield return $"CPU backend report: {VitaSharp.Core.Cpu.Backends.NativeX64Compiler.Report()}";
        }

        yield return $"CPU backend report: {VitaSharp.Core.Cpu.CpuCore.TierReport()}";
    }

    /// <summary>
    /// Guest cycles and native-code statistics per frame of the six busiest threads. <paramref name="window"/>: since the
    /// previous periodic report (what the periodic lines show; averages since boot are dominated by the JIT warm-up after
    /// a load), else over the whole run (the end-of-run summary).
    /// </summary>
    private static IEnumerable<string> CycleLines(Emulator emu, bool window)
    {
        long frames = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
        var samples = new List<(GuestThread Thread, VitaSharp.Core.Cpu.CpuCore.PerfSample Sample)>();
        foreach (GuestThread t in emu.Kernel.Threads.ToArray())
        {
            samples.Add((t, window ? t.Core.TakePerfWindow(frames) : t.Core.PerfTotals(frames)));
        }

        string span = window ? "since the last report" : "over the run";
        foreach ((GuestThread t, VitaSharp.Core.Cpu.CpuCore.PerfSample d) in samples.Where(x => x.Sample.BlockCycles + x.Sample.ChargedCycles > 0)
            .OrderByDescending(x => x.Sample.BlockCycles + x.Sample.ChargedCycles).Take(6))
        {
            yield return CycleLine(t.Name, t.Core, d, span, window ? frames : -1);
        }
    }

    /// <summary>
    /// The PERF phases lines (Cpu/PhaseProfile.cs) of the six threads that ran the most guest cycles: host time per guest
    /// cycle by dispatcher phase, HLE and pacing time, blocks decoded and compiled on the thread, alerts.
    /// <paramref name="window"/>: since the previous periodic report, else over the run.
    /// </summary>
    private static IEnumerable<string> PhaseLines(Emulator emu, bool window)
    {
        if (!VitaSharp.Core.Cpu.PhaseProfile.Enabled)
        {
            yield break;
        }

        var samples = new List<(string Name, long[] Counters)>();
        foreach (GuestThread t in emu.Kernel.Threads.ToArray())
        {
            samples.Add((t.Name, window ? t.Core.Phases.TakeWindow() : t.Core.Phases.Snapshot()));
        }

        foreach ((string name, long[] counters) in samples.Where(x => VitaSharp.Core.Cpu.PhaseProfile.TotalCycles(x.Counters) > 0)
            .OrderByDescending(x => VitaSharp.Core.Cpu.PhaseProfile.TotalCycles(x.Counters)).Take(6))
        {
            yield return VitaSharp.Core.Cpu.PhaseProfile.Format(name, counters, System.Diagnostics.Stopwatch.Frequency) + (window ? " (since the last report)" : " (over the run)");
        }
    }

    /// <summary>One PERF cycles line: the counters of <paramref name="d"/> per frame of its span. <paramref name="framesNow"/>:
    /// the frames presented so far for a windowed line (the return reasons and busiest targets are then windowed too), -1
    /// for totals.</summary>
    internal static string CycleLine(string name, VitaSharp.Core.Cpu.CpuCore core, VitaSharp.Core.Cpu.CpuCore.PerfSample d, string span, long framesNow)
    {
        double fr = Math.Max(1, d.Frames);
        double msPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var line = new System.Text.StringBuilder($"PERF cycles {name}: {span} ({d.Frames} frames), guest code {d.BlockCycles / 1e6 / fr:F2} M/frame ({d.BlocksExecuted / 1e3 / fr:F0} K blocks), charged HLE {d.ChargedCycles / 1e6 / fr:F2} M/frame");
        if (d.NativeTicks > 0)
        {
            string returns = framesNow >= 0 ? core.NativeReturnReport(framesNow) : core.NativeReturnTotals((long)fr);
            if (framesNow >= 0 && core.NativeReturnSitesReport(framesNow) is { Length: > 0 } sites)
            {
                returns += "; busiest: " + sites;
            }

            line.Append($"; host time in native code {d.NativeTicks * msPerTick / fr:F2} ms/frame (generated code {(d.NativeTicks - d.NativeHleTicks) * msPerTick / fr:F2}, HLE calls from it {d.NativeHleTicks * msPerTick / fr:F2}, waits included), {d.NativeReturnsUnlinked / fr:F0} returns/frame through an exit with no link or table entry; per frame: {returns}");
        }

        if (d.SimdOps + d.CondBranches > 0)
        {
            line.Append($", {d.SimdOps / 1e3 / fr:F0} K VFP/NEON ops/frame, {d.CondBranches / 1e3 / fr:F0} K conditional branches/frame");
        }

        return line.ToString();
    }

    /// <summary>The GPU backend's report (PERF gpu, PERF mobile...), video decoding and the GPU command counts.</summary>
    private static IEnumerable<string> GpuLines(Emulator emu)
    {
        if (emu.Gpu.Backend?.Report() is { } gpuReport)
        {
            yield return gpuReport;
        }

        yield return VitaSharp.Core.Gpu.GpuQueue.GuestWaitReport(VitaSharp.Core.Hle.Modules.Display.FramesPresented);
        long hwPics = VitaSharp.Core.Media.FFmpegVideoDecoder.HardwarePictures, swPics = VitaSharp.Core.Media.FFmpegVideoDecoder.SoftwarePictures;
        if (hwPics + swPics > 0)
        {
            yield return $"PERF video decode: {hwPics} pictures in hardware, {swPics} in software, {VitaSharp.Core.Media.FFmpegVideoDecoder.Fallbacks} hardware fallbacks, {VitaSharp.Core.Media.FFmpegVideoDecoder.DecodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms decoding ({VitaSharp.Core.Media.FFmpegVideoDecoder.DecodeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / (hwPics + swPics):F2} ms/picture)";
        }

        yield return $"  GPU commands: submitted {emu.Gpu.Submitted}, completed {emu.Gpu.Completed}; frames presented {VitaSharp.Core.Hle.Modules.Display.FramesPresented}";
        if (VitaSharp.Core.Hle.Modules.FrameRate.Mode != VitaSharp.Core.Hle.Modules.FrameRateMode.Native)
        {
            yield return $"  frame rate {VitaSharp.Core.Hle.Modules.FrameRate.Label}: {VitaSharp.Core.Hle.Modules.FrameRate.FlipsHeld} flips held to the fixed interval";
        }

        if (VitaSharp.Core.Hle.Modules.Display.ImmediateFlipsPaced > 0)
        {
            yield return $"  IMMEDIATE flips held to the next vblank (VITASHARP_DISPLAY_IMMEDIATE_PACE): {VitaSharp.Core.Hle.Modules.Display.ImmediateFlipsPaced}";
        }

        if (VitaSharp.Core.Hle.Modules.Display.PresentWaits > 0)
        {
            yield return $"  flips that waited for the previous present (VITASHARP_DISPLAY_PRESENT_WAIT): {VitaSharp.Core.Hle.Modules.Display.PresentWaits}";
        }

        if (VitaSharp.Core.Gpu.GpuQueue.SceneGateReleases > 0)
        {
            yield return $"  scene gates opened early (a guest waited for a held draw, or 250 ms passed): {VitaSharp.Core.Gpu.GpuQueue.SceneGateReleases}";
        }

        if (VitaSharp.Core.Hle.Modules.Gxm.UbAuditReport() is { } audit)
        {
            yield return audit;
        }
    }

    /// <summary>Debug mode: warnings by count and HLE error/unimplemented summaries (nothing otherwise).</summary>
    public static IEnumerable<string> DebugSummaryLines()
    {
        if (!DebugMode.Enabled)
        {
            yield break;
        }

        foreach (string l in Log.WarningSummary())
        {
            yield return l;
        }

        foreach (string l in DebugMode.HleSummary())
        {
            yield return l;
        }
    }

    /// <summary>Presented frame from which the PERF steady lines measure host CPU per thread (VITASHARP_WORKER_PROF_FROM;
    /// none without it): the whole-run PERF thread lines include the boot and the loading screens.</summary>
    public static readonly long ThreadMarkFrom = long.TryParse(Environment.GetEnvironmentVariable(EnvVars.WorkerProfFrom), out long tm) && tm > 0 ? tm : long.MaxValue;
    public static long ThreadMarkFrame { get; private set; } = -1;
    private static Dictionary<int, double>? s_threadMark;
    private static double s_processMark;

    /// <summary>Snapshots every host thread's CPU time (start of the steady-state measurement).</summary>
    public static void MarkThreadCpu(long frame)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var mark = new Dictionary<int, double>();
        foreach (System.Diagnostics.ProcessThread t in proc.Threads)
        {
            try
            {
                mark[t.Id] = t.TotalProcessorTime.TotalMilliseconds;
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or PlatformNotSupportedException or System.ComponentModel.Win32Exception) { }
        }
        s_processMark = proc.TotalProcessorTime.TotalMilliseconds;
        s_threadMark = mark;
        ThreadMarkFrame = frame;
    }

    private static IEnumerable<string> SteadyThreadLines(System.Diagnostics.Process proc, long frames)
    {
        if (s_threadMark is not { } mark || frames <= ThreadMarkFrame)
        {
            yield break;
        }

        double n = frames - ThreadMarkFrame;
        yield return $"PERF steady cpu (frames {ThreadMarkFrame}-{frames}): {(proc.TotalProcessorTime.TotalMilliseconds - s_processMark) / n:F2} ms/frame";
        var rows = new List<(string Name, double Ms)>();
        foreach (System.Diagnostics.ProcessThread t in proc.Threads)
        {
            try
            {
                rows.Add((NativeThread.Names.GetValueOrDefault(t.Id, $"host#{t.Id}"), t.TotalProcessorTime.TotalMilliseconds - mark.GetValueOrDefault(t.Id)));
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or PlatformNotSupportedException or System.ComponentModel.Win32Exception) { }
        }
        foreach ((string? name, double ms) in rows.OrderByDescending(x => x.Ms).Take(14))
        {
            yield return $"PERF steady thread {name}: {ms / n:F2} ms/frame";
        }
    }
    /// <summary>Host process: CPU time, the busiest host threads, allocations and GC.</summary>
    private static IEnumerable<string> ProcessLines()
    {
        long frames = Math.Max(1, VitaSharp.Core.Hle.Modules.Display.FramesPresented);
        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        yield return $"PERF cpu {proc.TotalProcessorTime.TotalSeconds:F1} s ({proc.TotalProcessorTime.TotalMilliseconds / frames:F1} ms/frame, user {proc.UserProcessorTime.TotalSeconds:F1} s, kernel {proc.PrivilegedProcessorTime.TotalSeconds:F1} s), threads {proc.Threads.Count}";
        var byCpu = new List<(string Name, double Ms)>();
        foreach (System.Diagnostics.ProcessThread t in proc.Threads)
        {
            // a thread that ended meanwhile; Mono on Android may not report per-thread times
            try
            {
                byCpu.Add((NativeThread.Names.GetValueOrDefault(t.Id, $"host#{t.Id}"), t.TotalProcessorTime.TotalMilliseconds));
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or PlatformNotSupportedException or System.ComponentModel.Win32Exception) { }
        }
        foreach ((string? name, double ms) in byCpu.OrderByDescending(x => x.Ms).Take(12))
        {
            yield return $"PERF thread {name}: {ms / 1000:F1} s cpu ({ms / frames:F2} ms/frame)";
        }

        foreach (string l in SteadyThreadLines(proc, frames))
        {
            yield return l;
        }

        yield return $"PERF uniforms: recycled chunks zeroed {VitaSharp.Core.Gpu.UniformArena.ClearedBytes / 1024.0 / frames:F0} KB/frame in {VitaSharp.Core.Gpu.UniformArena.ClearTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames:F3} ms/frame; copied by the guest {VitaSharp.Core.Hle.Modules.Gxm.UniformBytesCopied / 1024.0 / frames:F0} KB/frame; slot tails zeroed {VitaSharp.Core.Hle.Modules.Gxm.UniformTailBytesZeroed / 1024.0 / frames:F0} KB/frame; read from a copy shared in the scene {VitaSharp.Core.Hle.Modules.Gxm.UniformBytesShared / 1024.0 / frames:F0} KB/frame" + (VitaSharp.Core.Hle.Modules.Gxm.UbZeroCheckDraws > 0 ? $"; check: {VitaSharp.Core.Hle.Modules.Gxm.UbZeroCheckBad} of {VitaSharp.Core.Hle.Modules.Gxm.UbZeroCheckDraws} slices with unwritten words" : "");
        if (VitaSharp.Core.Hle.Modules.Gxm.UbStatReport() is { } ubs)
        {
            yield return ubs;
        }

        yield return "PERF delays (per frame: 0 / <=100us / <=1ms / <=5ms / longer; host sleeps, asked and late on average):" + string.Concat(VitaSharp.Core.Kernel.Wait.Delays.OrderByDescending(kv => kv.Value.Sum()).Take(8).Select(kv => $" {kv.Key} {kv.Value[0] / (double)frames:F1}/{kv.Value[1] / (double)frames:F1}/{kv.Value[2] / (double)frames:F1}/{kv.Value[3] / (double)frames:F1}/{kv.Value[4] / (double)frames:F1} slept {kv.Value[5] / (double)frames:F1} (asked {kv.Value[6] / Math.Max(1.0, kv.Value[5]):F0} us, late {kv.Value[7] / Math.Max(1.0, kv.Value[5]):F0} us);"));
        yield return $"PERF sched: core acquires that waited {VitaSharp.Core.Kernel.Scheduler.AcquireBlocked / (double)frames:F1}/frame, re-waits after a wake without a core {VitaSharp.Core.Kernel.Scheduler.AcquireRewaits / (double)frames:F1}/frame; blocking syscall host waits {VitaSharp.Core.Kernel.Wait.BlockingWaits / (double)frames:F1}/frame, wake-up latencies {VitaSharp.Core.Kernel.Wait.Latencies / (double)frames:F1}/frame; calls that waited for their waker {VitaSharp.Core.Kernel.GuestThread.WakerWaits / (double)frames:F1}/frame ({VitaSharp.Core.Kernel.GuestThread.WakerWaitTimeouts} hit the limit)";
        yield return GuestClock.LagLine();
        if (GcWindow.Line(frames) is { } gcWindow)
        {
            yield return gcWindow;
        }

        yield return $"PERF allocated {GC.GetTotalAllocatedBytes() / 1048576.0:F0} MB total ({GC.GetTotalAllocatedBytes() / 1024.0 / frames:F0} KB/frame), GC gen0 {GC.CollectionCount(0)} gen1 {GC.CollectionCount(1)} gen2 {GC.CollectionCount(2)}, pause {gcInfo.PauseTimePercentage:F1}%, heap {gcInfo.HeapSizeBytes / 1048576.0:F0} MB, working set {Environment.WorkingSet / 1048576.0:F0} MB; {GcDetail()}";
    }

    /// <summary>The PERF allocated line's GC detail: the total pause and the pause per collection, the young generation
    /// before the last young GC (about the GC's gen0 budget: ~256 KB when the runtime could not read the CPU's cache
    /// size, ~4 MB on a desktop; a phone run showed ~19 gen0 GCs/s at 3.5 MB/s) and the memory load against the GC's
    /// high-load threshold. Cumulative, as the rest of the line: a window is the difference of two reports.</summary>
    public static string GcDetail()
    {
        int collections = GC.CollectionCount(0);
        double pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        GCMemoryInfo young = GC.GetGCMemoryInfo(GCKind.Ephemeral);
        long gen0Before = young.Index > 0 && young.GenerationInfo.Length > 0 ? young.GenerationInfo[0].SizeBeforeBytes : 0;
        double loadPercent = young.TotalAvailableMemoryBytes > 0 ? 100.0 * young.MemoryLoadBytes / young.TotalAvailableMemoryBytes : 0;
        double highPercent = young.TotalAvailableMemoryBytes > 0 ? 100.0 * young.HighMemoryLoadThresholdBytes / young.TotalAvailableMemoryBytes : 0;
        return $"GC pause total {pauseMs:F0} ms ({pauseMs / Math.Max(1, collections):F2} ms per GC), gen0 before the last young GC {gen0Before / 1024.0:F0} KB, memory load {loadPercent:F0}% (GC high-load threshold {highPercent:F0}%); {YoungGcDetail(young)}";
    }

    /// <summary>The last young GC: its pause, what survived it and the pinned objects. A pause long for what survived
    /// is the older generations' card scan or the thread suspension, not the young objects (phone: 17-29 ms each).</summary>
    private static string YoungGcDetail(GCMemoryInfo young)
    {
        if (young.Index == 0)
        {
            return "no young GC yet";
        }

        ReadOnlySpan<TimeSpan> pauses = young.PauseDurations;
        double pauseMs = pauses.Length > 0 ? pauses[0].TotalMilliseconds : 0;
        return $"last young GC (#{young.Index}, gen{young.Generation}) paused {pauseMs:F1} ms, promoted {young.PromotedBytes / 1024.0:F0} KB, {young.PinnedObjectsCount} pinned objects, {(young.Compacted ? "compacted" : "swept")}";
    }
}
