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

    public Sampler(Emulator emu)
    {
        _emu = emu;
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
            _samples++;
            foreach (var t in _emu.Kernel.Threads)
            {
                string state = t.Status == ThreadStatus.Waiting ? "wait:" + t.WaitingOn : t.Status == ThreadStatus.Running && !t.HasCore && VitaSharp.Core.Kernel.Scheduler.Cores > 0 ? (t.BlockedInHost ? "host-block" : "core-wait") : t.Status.ToString();
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
        foreach (var (name, d) in _states.OrderByDescending(kv => kv.Value.Where(x => x.Key == "Running").Sum(x => x.Value)).Take(8))
        {
            int total = d.Values.Sum();
            Log.Info($"[{name}] " + string.Join(", ", d.OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Key} {100.0 * x.Value / total:F0}%")));
            if (_pcs.TryGetValue(name, out var p))
                Log.Info("    hot: " + string.Join(" ", p.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key:X8}:{100.0 * x.Value / total:F1}%")));
        }
    }
}
