using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Util;
using VitaSharp.Core.Hle;

namespace VitaSharp.Desktop;

/// <summary>Poor man's profiler: samples every guest thread's state/PC every millisecond.</summary>
sealed class Sampler
{
    readonly Emulator _emu;
    readonly Thread _thread;
    volatile bool _stop;
    readonly Dictionary<string, Dictionary<string, int>> _states = new();
    readonly Dictionary<string, Dictionary<uint, int>> _pcs = new();
    // the same per 5 s window, reported for windows below SlowWindowFps (a slow stretch is lost in the whole-run totals)
    readonly Dictionary<string, Dictionary<string, int>> _wStates = new();
    readonly Dictionary<string, Dictionary<uint, int>> _wPcs = new();
    const double SlowWindowFps = 20;
    int _slowReports;

    void ReportSlowWindow(long from, long to, double fps, double seconds)
    {
        if (_slowReports++ >= 40) return;
        Log.Info($"PROFILE slow window f{from}..f{to}: {fps:F1} fps");
        // threads that ran (guest code, HLE holding a core, waiting for a core or pacing), with the emulated cycles they
        // executed and were charged in the window: (guest + HLE) near the pacing rate means the CPU model limits
        foreach (var (name, d) in _wStates.OrderByDescending(kv => kv.Value.Where(x => x.Key.StartsWith("Running") || x.Key is "core-wait" or "throttle").Sum(x => x.Value)).Take(5))
        {
            int total = d.Values.Sum();
            var t = _emu.Kernel.Threads.FirstOrDefault(x => x.Name == name);
            string cyc = "";
            if (t != null && _wCycles.TryGetValue(t, out var c0))
            {
                double g = (t.Core.BlockCycles - c0.Guest) / 1e6, h = (t.Core.ChargedCycles - c0.Hle) / 1e6;
                cyc = $" ({g / seconds:F0}+{h / seconds:F0} M cycles/s guest+HLE)";
            }
            Log.Info($"    [{name}]{cyc} " + string.Join(", ", d.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Key} {100.0 * x.Value / total:F0}%")));
            if (_wPcs.TryGetValue(name, out var p))
                Log.Info("        hot: " + string.Join(" ", p.OrderByDescending(x => x.Value).Take(6).Select(x => $"{x.Key:X8}:{100.0 * x.Value / total:F1}%")));
        }
    }
    readonly Dictionary<VitaSharp.Core.Kernel.GuestThread, (long Guest, long Hle)> _wCycles = new();
    int _samples;
    long _fpsFrames;
    readonly List<double> _fps = new();

    readonly long _from;
    readonly double _cyclesPerMs;
    readonly bool _hostHle;
    Dictionary<string, double> _baseCpu = new();

    /// <summary>Host CPU ms per thread name (guest threads, GPU worker...; same-named threads summed).</summary>
    static Dictionary<string, double> HostThreadCpu()
    {
        var d = new Dictionary<string, double>();
        try
        {
            foreach (System.Diagnostics.ProcessThread t in System.Diagnostics.Process.GetCurrentProcess().Threads)
            {
                try { string n = NativeThread.Names.GetValueOrDefault(t.Id, ""); if (n.Length > 0) d[n] = d.GetValueOrDefault(n) + t.TotalProcessorTime.TotalMilliseconds; }
                catch (InvalidOperationException) { }
            }
        }
        catch (Exception) { }
        return d;
    }
    Dictionary<VitaSharp.Core.Kernel.GuestThread, (long Cycles, long Blocks, long Hle, long Instrs)>? _base;
    readonly Dictionary<VitaSharp.Core.Hle.HleFunction, (long Calls, long Charged)> _baseFn = new();
    VitaSharp.Core.Cpu.CpuCore.SleepStat[] _baseSleep = new VitaSharp.Core.Cpu.CpuCore.SleepStat[2];
    long _baseFrame, _endFrame;
    double _baseTime, _endTime;

    /// <summary>Samples only once <paramref name="fromFrame"/> frames were presented (skip boot). <paramref name="hostHle"/>
    /// (--profile-hle) also times every HLE call per thread (PROFILE host lines), which costs HLE-heavy games CPU.</summary>
    public Sampler(Emulator emu, long fromFrame = 0, bool hostHle = false)
    {
        _emu = emu;
        _from = fromFrame;
        _hostHle = hostHle;
        FrameLedger.Enable();
        if (hostHle)
        {
            // host CPU cycles per ms of QueryThreadCycleTime (the TSC rate): converts per-call HLE cycle counts to ms
            long s0 = System.Diagnostics.Stopwatch.GetTimestamp(); ulong c0 = NativeThread.CycleTime();
            while (System.Diagnostics.Stopwatch.GetTimestamp() - s0 < System.Diagnostics.Stopwatch.Frequency / 50) { }
            _cyclesPerMs = (NativeThread.CycleTime() - c0) / ((System.Diagnostics.Stopwatch.GetTimestamp() - s0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    unsafe void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double lastFpsTime = 0;
        while (!_stop)
        {
            Thread.Sleep(1);
            if (VitaSharp.Core.Hle.Modules.Display.FramesPresented < _from) { lastFpsTime = sw.Elapsed.TotalSeconds; _fpsFrames = VitaSharp.Core.Hle.Modules.Display.FramesPresented; continue; }
            if (_base == null) { _base = new(); _baseFrame = VitaSharp.Core.Hle.Modules.Display.FramesPresented; _baseTime = sw.Elapsed.TotalSeconds; foreach (var t in _emu.Kernel.Threads) _base[t] = (t.Core.BlockCycles, t.Core.BlocksExecuted, t.Core.ChargedCycles, t.Core.InstructionsExecuted); foreach (var f in _emu.Hle.Functions) _baseFn[f] = (f.CallCount, f.ChargedCycles); _baseSleep = (VitaSharp.Core.Cpu.CpuCore.SleepStat[])VitaSharp.Core.Cpu.CpuCore.SleepStats.Clone(); HleFunction.Track = true; HleFunction.Profile = _hostHle; _baseCpu = HostThreadCpu(); }
            _endFrame = VitaSharp.Core.Hle.Modules.Display.FramesPresented; _endTime = sw.Elapsed.TotalSeconds;
            _samples++;
            foreach (var t in _emu.Kernel.Threads)
            {
                string state = t.Status == ThreadStatus.Waiting ? "wait:" + t.WaitingOn : t.Core.InThrottle ? "throttle" : t.Status == ThreadStatus.Running && !t.HasCore && VitaSharp.Core.Kernel.Scheduler.Cores > 0 ? (t.BlockedInHost ? "host-block" : "core-wait") : t.Status.ToString();
                if (t.CurrentHle is { } inHle && state is "host-block" or "Running") state += ":" + inHle.Name;
                if (!_wStates.TryGetValue(t.Name, out var wd)) _wStates[t.Name] = wd = new();
                wd[state] = wd.GetValueOrDefault(state) + 1;
                if (!_states.TryGetValue(t.Name, out var d)) _states[t.Name] = d = new();
                d[state] = d.GetValueOrDefault(state) + 1;
                if (t.Status == ThreadStatus.Running)
                {
                    uint pc = t.Core.State->R[15] & ~0xFFu;
                    if (!_pcs.TryGetValue(t.Name, out var p)) _pcs[t.Name] = p = new();
                    p[pc] = p.GetValueOrDefault(pc) + 1;
                    if (!_wPcs.TryGetValue(t.Name, out var wp)) _wPcs[t.Name] = wp = new();
                    wp[pc] = wp.GetValueOrDefault(pc) + 1;
                }
            }
            double now = sw.Elapsed.TotalSeconds;
            if (now - lastFpsTime >= 5)
            {
                long f = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
                double wfps = (f - _fpsFrames) / (now - lastFpsTime);
                _fps.Add(wfps);
                if (wfps < SlowWindowFps) ReportSlowWindow(_fpsFrames, f, wfps, now - lastFpsTime);
                _wStates.Clear(); _wPcs.Clear(); _wCycles.Clear();
                foreach (var t in _emu.Kernel.Threads) _wCycles[t] = (t.Core.BlockCycles, t.Core.ChargedCycles);
                _fpsFrames = f;
                lastFpsTime = now;
            }
        }
    }

    /// <summary>
    /// Per-cause host time (FrameLedger) of the frames whose present interval exceeded 50 ms, over the hitch frame and
    /// the one before it (GPU-thread work runs about a frame behind the guest).
    /// </summary>
    void ReportHitchCauses(long[] pt, int mask)
    {
        if (!FrameLedger.Enabled) return;
        double tk = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        var hitches = new List<(long F, double Ms)>();
        for (long f = _baseFrame + 2; f < _endFrame - 1; f++)
        {
            double d = (pt[f & mask] - pt[(f - 1) & mask]) / tk;
            if (d > 50) hitches.Add((f, d));
        }
        var all = FrameLedger.Window(_baseFrame + 1, _endFrame - 2);
        double frames = Math.Max(1, _endFrame - _baseFrame - 2);
        Log.Info($"PROFILE ledger per frame (all frames, ms/frame): " + string.Join(" ", Enumerable.Range(0, all.Length).Select(i => $"{FrameLedger.Names[i]}={all[i] / frames:F2}")));
        if (hitches.Count == 0) return;
        var sum = new double[(int)FrameLedger.Cat.Count];
        double excess = 0;
        foreach (var (f, ms) in hitches)
        {
            var w = FrameLedger.Window(f - 1, f);
            for (int i = 0; i < sum.Length; i++) sum[i] += w[i];
            excess += ms - 1000.0 / 30;
        }
        Log.Info($"PROFILE hitch frames >50 ms: {hitches.Count} (>100 ms: {hitches.Count(h => h.Ms > 100)}, >250 ms: {hitches.Count(h => h.Ms > 250)}), excess over 33.3 ms: {excess:F0} ms; causes summed over [f-1,f]: {FrameLedger.Format(sum)}");
        // every hitch with its largest GPU-thread/guest cause (gpuIdle = the GPU thread had nothing to do: guest-bound)
        string Top(long f)
        {
            var w = FrameLedger.Window(f - 1, f);
            int best = 0;
            for (int i = 1; i < w.Length; i++) if (i != (int)FrameLedger.Cat.BgShader && i != (int)FrameLedger.Cat.BgPipeline && w[i] > w[best]) best = i;
            return FrameLedger.Names[best];
        }
        Log.Info($"PROFILE hitch list: {string.Join(" ", hitches.Take(300).Select(h => $"f{h.F}:{h.Ms:F0}:{Top(h.F)}"))}");
        foreach (var (f, ms) in hitches.OrderByDescending(h => h.Ms).Take(30))
            Log.Info($"PROFILE hitch f{f} {ms:F0} ms: {FrameLedger.Format(FrameLedger.Window(f - 1, f))}");
    }

    public void Report()
    {
        _stop = true;
        _thread.Join();
        Log.Info($"FPS per 5s window: {string.Join(" ", _fps.Select(f => f.ToString("F0")))}");
        long frames = Math.Max(1, _endFrame - _baseFrame);
        Log.Info($"PROFILE frames {_baseFrame}..{_endFrame}: {frames / Math.Max(0.001, _endTime - _baseTime):F1} frames/s");
        if (_base != null)
            foreach (var t in _emu.Kernel.Threads)
            {
                var b = _base.GetValueOrDefault(t);
                long cyc = t.Core.BlockCycles - b.Cycles, ins = t.Core.BlocksExecuted - b.Blocks, hle = t.Core.ChargedCycles - b.Hle, n = t.Core.InstructionsExecuted - b.Instrs;
                if (cyc + hle > frames * 10000) Log.Info($"PROFILE cycles {t.Name}: guest {cyc / 1e6 / frames:F2} M/frame ({ins / 1e3 / frames:F0} K blocks, {(double)cyc / Math.Max(1, ins):F1} cycles/block, {(double)cyc / Math.Max(1, n):F2} cycles/instruction), charged HLE {hle / 1e6 / frames:F2} M/frame");
            }
        if (_endFrame - _baseFrame > 2 && _endFrame - _baseFrame < VitaSharp.Core.Hle.Modules.Display.PresentTimes.Length)
        {
            var pt = VitaSharp.Core.Hle.Modules.Display.PresentTimes;
            int mask = pt.Length - 1;
            var ms = new List<double>();
            for (long f = _baseFrame + 1; f < _endFrame - 1; f++) ms.Add((pt[f & mask] - pt[(f - 1) & mask]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var slow = new List<string>();
            for (long f = _baseFrame + 1; f < _endFrame - 1; f++) { double d = (pt[f & mask] - pt[(f - 1) & mask]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (d > 50) slow.Add($"f{f}:{d:F0}ms"); }
            if (slow.Count > 0) Log.Info($"PROFILE hitches: {string.Join(" ", slow.Take(20))}");
            ReportHitchCauses(pt, mask);
            ms.Sort();
            double P(double q) => ms[(int)Math.Min(ms.Count - 1, q * ms.Count)];
            Log.Info($"PROFILE frame interval ms: p10 {P(0.1):F2} p50 {P(0.5):F2} p90 {P(0.9):F2} p99 {P(0.99):F2} max {ms[^1]:F2}; >17.5 ms: {ms.Count(x => x > 17.5)}, >25 ms: {ms.Count(x => x > 25)} of {ms.Count}");
            var win = new List<string>();
            for (long f = _baseFrame + 1; f + 50 < _endFrame; f += 50) win.Add((50 * (double)System.Diagnostics.Stopwatch.Frequency / (pt[(f + 50) & mask] - pt[f & mask])).ToString("F1"));
            Log.Info($"PROFILE fps per 50 frames: {string.Join(" ", win)}");
            var steady = ms.Where(x => x <= 50).ToList();
            Log.Info($"PROFILE steady fps (intervals > 50 ms, i.e. loads, excluded): {1000.0 / steady.Average():F1} over {steady.Count} frames");
        }
        for (int i = 0; i < 2; i++)
        {
            var s = VitaSharp.Core.Cpu.CpuCore.SleepStats[i]; var b0 = _baseSleep[i]; double tk = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            long n = s.Count - b0.Count;
            Log.Info($"PROFILE pacing sleeps ({(i == 0 ? "throttle" : "settle")}): {(double)n / frames:F1}/frame, requested {(s.Requested - b0.Requested) / tk / frames:F2} ms/frame, slept {(s.Actual - b0.Actual) / tk / frames:F2} ms/frame (avg overshoot {((s.Actual - b0.Actual) - (s.Requested - b0.Requested)) / tk / Math.Max(1, n) * 1000:F0} us)");
        }
        foreach (var (f, calls, charged) in _emu.Hle.Functions.Select(f => (f, f.CallCount - _baseFn.GetValueOrDefault(f).Calls, f.ChargedCycles - _baseFn.GetValueOrDefault(f).Charged)).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item3).Take(12))
            Log.Info($"PROFILE hle {f.Name}: {(double)calls / frames:F0} calls/frame, {charged / 1e6 / frames:F3} M cycles/frame");
        // host side of the HLE calls per guest thread: wall time inside the implementation (blocking included) and
        // the CPU the thread burned there (QueryThreadCycleTime), next to the thread's total CPU in the window
        var cpuNow = _hostHle ? HostThreadCpu() : new();
        double tms = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        foreach (var t in _hostHle ? _emu.Kernel.Threads.OrderByDescending(t => t.Core.BlockCycles - _base?.GetValueOrDefault(t).Cycles ?? 0).Take(6) : [])
        {
            var data = t.HleProfile;
            var rows = Enumerable.Range(0, data.Length / 3).Where(i => data[i * 3] > 0).Select(i => (F: _emu.Hle.GetFunction((uint)i), P: new[] { data[i * 3], data[i * 3 + 1], data[i * 3 + 2] })).ToArray();
            if (rows.Length == 0) continue;
            double cpuMs = cpuNow.TryGetValue(t.Name, out var c1) ? (c1 - _baseCpu.GetValueOrDefault(t.Name)) / frames : double.NaN;
            Log.Info($"PROFILE host {t.Name}: thread cpu {cpuMs:F2} ms/frame, in HLE: wall {rows.Sum(r => r.P[1]) / tms / frames:F2} ms/frame, cpu {(_cyclesPerMs > 0 ? rows.Sum(r => r.P[2]) / _cyclesPerMs / frames : double.NaN):F2} ms/frame, {rows.Sum(r => r.P[0]) / (double)frames:F0} calls/frame");
            foreach (var (f, p) in rows.OrderByDescending(r => r.P[2] + (_cyclesPerMs > 0 ? 0 : r.P[1])).Take(14))
                Log.Info($"    cpu {(_cyclesPerMs > 0 ? p[2] / _cyclesPerMs / frames : double.NaN),6:F3} wall {p[1] / tms / frames,7:F3} ms/frame {p[0] / (double)frames,7:F1} calls/frame {(_cyclesPerMs > 0 ? p[2] / _cyclesPerMs * 1000 / Math.Max(1, p[0]) : 0),7:F2} us/call  {f.Name}");
            foreach (var (f, p) in rows.Where(r => r.P[1] - (_cyclesPerMs > 0 ? (long)(r.P[2] / _cyclesPerMs * tms) : 0) > frames * tms / 10).OrderByDescending(r => r.P[1]).Take(8))
                Log.Info($"    wait {(p[1] - (_cyclesPerMs > 0 ? p[2] / _cyclesPerMs * tms : 0)) / tms / frames,7:F3} ms/frame {p[0] / (double)frames,7:F1} calls/frame  {f.Name}");
        }
        foreach (var (name, d) in _states.OrderByDescending(kv => kv.Value.Where(x => x.Key == "Running").Sum(x => x.Value)).Take(8))
        {
            int total = d.Values.Sum();
            Log.Info($"[{name}] " + string.Join(", ", d.OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Key} {100.0 * x.Value / total:F0}%")));
            if (_pcs.TryGetValue(name, out var p))
                Log.Info("    hot: " + string.Join(" ", p.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key:X8}:{100.0 * x.Value / total:F1}%")));
        }
    }
}
