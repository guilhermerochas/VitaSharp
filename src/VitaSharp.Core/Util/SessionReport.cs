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
        foreach (var l in CpuBackendLines())
        {
            yield return l;
        }

        foreach (var f in emu.Hle.Functions.Where(f => f.CallCount > 0).OrderByDescending(f => f.CallCount).Take(40))
        {
            yield return $"  {f.CallCount,8} x {f.Name}{(f.Implemented ? "" : " (unimplemented)")}";
        }

        if (Environment.GetEnvironmentVariable(EnvVars.HleCalls) == "1")
        {
            foreach (var p in (emu.Vita?.Processes ?? []).Prepend(emu).DistinctBy(p => p.Hle))
            {
                foreach (var f in p.Hle.Functions.Where(f => f.CallCount > 0).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    yield return $"  hle-call {p.TitleId} {f.CallCount} {f.Name}{(f.Implemented ? "" : " (unimplemented)")}";
                }
            }
        }

        foreach (var t in emu.Kernel.Threads)
        {
            yield return "  thread " + t.Describe();
        }
        // the other processes of the system (applications the shell started)
        foreach (var p in emu.Vita?.Processes.Where(p => !ReferenceEquals(p, emu)) ?? [])
        {
            yield return $"  process {p.Describe()}{(p.Suspended ? " (suspended)" : "")}:";
            var lines = new List<string>();
            using (VitaSharp.Core.Memory.AddressSpace.Use(p.Space))
            {
                foreach (var t in p.Kernel.Threads)
                {
                    lines.Add("    thread " + t.Describe());
                }
            }

            foreach (var l in lines)
            {
                yield return l;
            }
        }
        foreach (var l in CycleLines(emu))
        {
            yield return l;
        }
        // VITASHARP_PEEK_END=ADDR:LEN[,ADDR:LEN...] hex-dumps guest memory when the run stops (state of a hang)
        if (Environment.GetEnvironmentVariable(EnvVars.PeekEnd) is { } peekEnd)
        {
            foreach (var spec in peekEnd.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var pp = spec.Split(':');
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
        foreach (var m in emu.Kernel.Objects.All<VitaSharp.Core.Kernel.Mutex>())
        {
            if (m.Owner != null && m.Waiters > 0)
            {
                yield return $"  mutex '{m.Name}' held by '{m.Owner.Name}' ({m.Owner.Status}) count {m.LockCount}, {m.Waiters} waiting";
            }
        }

        yield return IoTiming.FastLoading
            ? "  storage: fast loading (no read speed limit)"
            : $"  storage: {IoTiming.BytesPerSecond / 1e6:0.#} MB/s, {IoTiming.TimedReads} device reads, {IoTiming.DeviceTicks / (double)System.Diagnostics.Stopwatch.Frequency:0.0} s of device time";
        if (!IoTiming.FastLoading)
        {
            foreach (var l in IoTiming.WaitLines())
            {
                yield return l;
            }
        }

        foreach (var l in GpuLines(emu))
        {
            yield return l;
        }

        foreach (var l in AudioDiagnostics.SummaryLines())
        {
            yield return l; // always: ports, outputs/s, underruns per minute
        }

        foreach (var l in VitaSharp.Core.Hle.Modules.Ngs.PerfLines())
        {
            yield return l;
        }

        foreach (var l in DebugSummaryLines())
        {
            yield return l;
        }

        foreach (var l in ProcessLines())
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
        Add(() => CycleLines(emu));
        Add(() => [HleCallsLine(emu)]);
        Add(() => GpuLines(emu));
        Add(AudioDiagnostics.SummaryLines);
        Add(VitaSharp.Core.Hle.Modules.Ngs.PerfLines);
        Add(ProcessLines);
        return lines;
    }

    /// <summary>The busiest HLE functions so far, on one line (the end-of-run report lists 40, one per line).</summary>
    static string HleCallsLine(Emulator emu)
    {
        long frames = Math.Max(1, VitaSharp.Core.Hle.Modules.Display.FramesPresented);
        var top = emu.Hle.Functions.Where(f => f.CallCount > 0).OrderByDescending(f => f.CallCount).Take(12)
            .Select(f => $"{f.Name} {f.CallCount} ({f.CallCount / (double)frames:F0}/frame)");
        return $"PERF hle calls: {string.Join(", ", top)}; GXM uniforms copied {VitaSharp.Core.Hle.Modules.Gxm.UniformBytesCopied / 1024.0 / frames:F0} KB/frame";
    }

    /// <summary>Native JIT verification and the native backend's report.</summary>
    static IEnumerable<string> CpuBackendLines()
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

    /// <summary>Guest cycles per frame of the six busiest threads.</summary>
    static IEnumerable<string> CycleLines(Emulator emu)
    {
        long fr = Math.Max(1, VitaSharp.Core.Hle.Modules.Display.FramesPresented);
        foreach (var t in emu.Kernel.Threads.ToArray().Where(t => t.Core.BlockCycles + t.Core.ChargedCycles > 0).OrderByDescending(t => t.Core.BlockCycles + t.Core.ChargedCycles).Take(6))
        {
            yield return $"PERF cycles {t.Name}: guest code {t.Core.BlockCycles / 1e6 / fr:F2} M/frame ({t.Core.BlocksExecuted / 1e3 / fr:F0} K blocks), charged HLE {t.Core.ChargedCycles / 1e6 / fr:F2} M/frame" +
                (t.Core.NativeTicks > 0 ? $"; host time in native code {t.Core.NativeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / fr:F2} ms/frame, {t.Core.NativeEntries / (double)fr:F0} entries/frame ({t.Core.NativeReturnsUnlinked / (double)fr:F0} returned through an exit with no link or table entry)" : "") +
                (t.Core.SimdOps + t.Core.CondBranches > 0 ? $", {t.Core.SimdOps / 1e3 / fr:F0} K VFP/NEON ops/frame, {t.Core.CondBranches / 1e3 / fr:F0} K conditional branches/frame" : "");
        }
    }

    /// <summary>The GPU backend's report (PERF gpu, PERF mobile...), video decoding and the GPU command counts.</summary>
    static IEnumerable<string> GpuLines(Emulator emu)
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

        foreach (var l in Log.WarningSummary())
        {
            yield return l;
        }

        foreach (var l in DebugMode.HleSummary())
        {
            yield return l;
        }
    }

    /// <summary>Presented frame from which the PERF steady lines measure host CPU per thread (VITASHARP_WORKER_PROF_FROM;
    /// none without it): the whole-run PERF thread lines include the boot and the loading screens.</summary>
    public static readonly long ThreadMarkFrom = long.TryParse(Environment.GetEnvironmentVariable(EnvVars.WorkerProfFrom), out long tm) && tm > 0 ? tm : long.MaxValue;
    public static long ThreadMarkFrame { get; private set; } = -1;
    static Dictionary<int, double>? _threadMark;
    static double _processMark;

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
        _processMark = proc.TotalProcessorTime.TotalMilliseconds;
        _threadMark = mark;
        ThreadMarkFrame = frame;
    }

    static IEnumerable<string> SteadyThreadLines(System.Diagnostics.Process proc, long frames)
    {
        if (_threadMark is not { } mark || frames <= ThreadMarkFrame)
        {
            yield break;
        }

        double n = frames - ThreadMarkFrame;
        yield return $"PERF steady cpu (frames {ThreadMarkFrame}-{frames}): {(proc.TotalProcessorTime.TotalMilliseconds - _processMark) / n:F2} ms/frame";
        var rows = new List<(string Name, double Ms)>();
        foreach (System.Diagnostics.ProcessThread t in proc.Threads)
        {
            try
            {
                rows.Add((NativeThread.Names.GetValueOrDefault(t.Id, $"host#{t.Id}"), t.TotalProcessorTime.TotalMilliseconds - mark.GetValueOrDefault(t.Id)));
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or PlatformNotSupportedException or System.ComponentModel.Win32Exception) { }
        }
        foreach (var (name, ms) in rows.OrderByDescending(x => x.Ms).Take(14))
        {
            yield return $"PERF steady thread {name}: {ms / n:F2} ms/frame";
        }
    }
    /// <summary>Host process: CPU time, the busiest host threads, allocations and GC.</summary>
    static IEnumerable<string> ProcessLines()
    {
        long frames = Math.Max(1, VitaSharp.Core.Hle.Modules.Display.FramesPresented);
        var gcInfo = GC.GetGCMemoryInfo();
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
        foreach (var (name, ms) in byCpu.OrderByDescending(x => x.Ms).Take(12))
        {
            yield return $"PERF thread {name}: {ms / 1000:F1} s cpu ({ms / frames:F2} ms/frame)";
        }

        foreach (var l in SteadyThreadLines(proc, frames))
        {
            yield return l;
        }

        yield return $"PERF uniforms: recycled chunks zeroed {VitaSharp.Core.Gpu.UniformArena.ClearedBytes / 1024.0 / frames:F0} KB/frame in {VitaSharp.Core.Gpu.UniformArena.ClearTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames:F3} ms/frame; copied by the guest {VitaSharp.Core.Hle.Modules.Gxm.UniformBytesCopied / 1024.0 / frames:F0} KB/frame; slot tails zeroed {VitaSharp.Core.Hle.Modules.Gxm.UniformTailBytesZeroed / 1024.0 / frames:F0} KB/frame; read from a copy shared in the scene {VitaSharp.Core.Hle.Modules.Gxm.UniformBytesShared / 1024.0 / frames:F0} KB/frame" + (VitaSharp.Core.Hle.Modules.Gxm.UbZeroCheckDraws > 0 ? $"; check: {VitaSharp.Core.Hle.Modules.Gxm.UbZeroCheckBad} of {VitaSharp.Core.Hle.Modules.Gxm.UbZeroCheckDraws} slices with unwritten words" : "");
        if (VitaSharp.Core.Hle.Modules.Gxm.UbStatReport() is { } ubs)
        {
            yield return ubs;
        }

        yield return "PERF delays (per frame: 0 / <=100us / <=1ms / <=5ms / longer; host sleeps, asked and late on average):" + string.Concat(VitaSharp.Core.Kernel.Wait.Delays.OrderByDescending(kv => kv.Value.Sum()).Take(8).Select(kv => $" {kv.Key} {kv.Value[0] / (double)frames:F1}/{kv.Value[1] / (double)frames:F1}/{kv.Value[2] / (double)frames:F1}/{kv.Value[3] / (double)frames:F1}/{kv.Value[4] / (double)frames:F1} slept {kv.Value[5] / (double)frames:F1} (asked {kv.Value[6] / Math.Max(1.0, kv.Value[5]):F0} us, late {kv.Value[7] / Math.Max(1.0, kv.Value[5]):F0} us);"));
        yield return $"PERF sched: core acquires that waited {VitaSharp.Core.Kernel.Scheduler.AcquireBlocked / (double)frames:F1}/frame, re-waits after a wake without a core {VitaSharp.Core.Kernel.Scheduler.AcquireRewaits / (double)frames:F1}/frame; blocking syscall host waits {VitaSharp.Core.Kernel.Wait.BlockingWaits / (double)frames:F1}/frame, wake-up latencies {VitaSharp.Core.Kernel.Wait.Latencies / (double)frames:F1}/frame";
        yield return $"PERF allocated {GC.GetTotalAllocatedBytes() / 1048576.0:F0} MB total ({GC.GetTotalAllocatedBytes() / 1024.0 / frames:F0} KB/frame), GC gen0 {GC.CollectionCount(0)} gen1 {GC.CollectionCount(1)} gen2 {GC.CollectionCount(2)}, pause {gcInfo.PauseTimePercentage:F1}%, heap {gcInfo.HeapSizeBytes / 1048576.0:F0} MB, working set {Environment.WorkingSet / 1048576.0:F0} MB";
    }
}
