using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop;

/// <summary>Poor man's profiler: samples every guest thread's state/PC every millisecond.</summary>
sealed class Sampler
{
    readonly Emulator _emu;
    readonly Thread _thread;
    volatile bool _stop;
    readonly Dictionary<string, Dictionary<string, int>> _states = new();
    readonly Dictionary<string, Dictionary<uint, int>> _pcs = new();
    int _samples;
    long _fpsFrames;
    readonly List<double> _fps = new();

    readonly long _from;
    Dictionary<VitaSharp.Core.Kernel.GuestThread, (long Cycles, long Blocks, long Hle, long Instrs)>? _base;
    readonly Dictionary<VitaSharp.Core.Hle.HleFunction, (long Calls, long Charged)> _baseFn = new();
    VitaSharp.Core.Cpu.CpuCore.SleepStat[] _baseSleep = new VitaSharp.Core.Cpu.CpuCore.SleepStat[2];
    long _baseFrame, _endFrame;
    double _baseTime, _endTime;

    /// <summary>Samples only once <paramref name="fromFrame"/> frames were presented (skip boot).</summary>
    public Sampler(Emulator emu, long fromFrame = 0)
    {
        _emu = emu;
        _from = fromFrame;
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
            if (_base == null) { _base = new(); _baseFrame = VitaSharp.Core.Hle.Modules.Display.FramesPresented; _baseTime = sw.Elapsed.TotalSeconds; foreach (var t in _emu.Kernel.Threads) _base[t] = (t.Core.BlockCycles, t.Core.BlocksExecuted, t.Core.ChargedCycles, t.Core.InstructionsExecuted); foreach (var f in _emu.Hle.Functions) _baseFn[f] = (f.CallCount, f.ChargedCycles); _baseSleep = (VitaSharp.Core.Cpu.CpuCore.SleepStat[])VitaSharp.Core.Cpu.CpuCore.SleepStats.Clone(); }
            _endFrame = VitaSharp.Core.Hle.Modules.Display.FramesPresented; _endTime = sw.Elapsed.TotalSeconds;
            _samples++;
            foreach (var t in _emu.Kernel.Threads)
            {
                string state = t.Status == ThreadStatus.Waiting ? "wait:" + t.WaitingOn : t.Core.InThrottle ? "throttle" : t.Status == ThreadStatus.Running && !t.HasCore && VitaSharp.Core.Kernel.Scheduler.Cores > 0 ? (t.BlockedInHost ? "host-block" : "core-wait") : t.Status.ToString();
                if (!_states.TryGetValue(t.Name, out var d)) _states[t.Name] = d = new();
                d[state] = d.GetValueOrDefault(state) + 1;
                if (t.Status == ThreadStatus.Running)
                {
                    uint pc = t.Core.State->R[15] & ~0xFFu;
                    if (!_pcs.TryGetValue(t.Name, out var p)) _pcs[t.Name] = p = new();
                    p[pc] = p.GetValueOrDefault(pc) + 1;
                }
            }
            double now = sw.Elapsed.TotalSeconds;
            if (now - lastFpsTime >= 5)
            {
                long f = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
                _fps.Add((f - _fpsFrames) / (now - lastFpsTime));
                _fpsFrames = f;
                lastFpsTime = now;
            }
        }
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
        foreach (var (name, d) in _states.OrderByDescending(kv => kv.Value.Where(x => x.Key == "Running").Sum(x => x.Value)).Take(8))
        {
            int total = d.Values.Sum();
            Log.Info($"[{name}] " + string.Join(", ", d.OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Key} {100.0 * x.Value / total:F0}%")));
            if (_pcs.TryGetValue(name, out var p))
                Log.Info("    hot: " + string.Join(" ", p.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key:X8}:{100.0 * x.Value / total:F1}%")));
        }
    }
}
