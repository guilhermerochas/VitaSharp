using System.Diagnostics;

namespace VitaSharp.Core.Util;

/// <summary>
/// How a session starts, for every front end (desktop, Android) without a profiler: when the first frame is
/// presented (seconds after the emulator was created, the user's click) and, over the first <see cref="Seconds"/>
/// after it, the frames presented, the slow ones (above <see cref="SlowMs"/> and <see cref="VerySlowMs"/>, with their
/// time above a 33.3 ms frame) and, of the slow ones, those with more than <see cref="JitMs"/> of JIT compilation while
/// the game ran in their interval or the one before (<see cref="FrameLedger.Cat.JitCompile"/>: what the ARM code
/// precompilation and the code cache are there to remove). One log line once the minute is over (or when the session
/// ends before it). Time is the emulation's (<see cref="EmulationPause.ActiveHostTicks"/>): a pause is not a slow frame.
/// Per frame it costs a few additions, during the first minute only.
/// </summary>
public sealed class StartupMeter
{
    /// <summary>The start of a session measured: how long a game takes to run smoothly is what the start-up work
    /// (code precompilation, shader cache) and the JIT compiling the code reached first decide.</summary>
    public const double Seconds = 60;

    public const double SlowMs = 50;
    public const double VerySlowMs = 100;
    public const double JitMs = 5;

    /// <summary>The meter of the running system (<see cref="Hle.Modules.Display"/> feeds it).</summary>
    public static readonly StartupMeter Current = new(Stopwatch.Frequency);

    private readonly double _ticksPerMs;
    private readonly object _lock = new();
    private volatile bool _started;
    private volatile bool _done;
    private long _boot;
    private long _last;
    private long _jitLast;
    private double _jitPrevious;
    private double _firstFrameSeconds = -1;
    private double _elapsedMs;
    private int _frames;
    private int _slow;
    private int _verySlow;
    private int _slowWithJit;
    private double _excessMs;
    private double _jitMs;

    /// <param name="frequency">Ticks per second of the timestamps given.</param>
    public StartupMeter(long frequency)
    {
        _ticksPerMs = frequency / 1000.0;
    }

    /// <summary>A session starts (the emulator was created) at <paramref name="now"/>: the measure starts over.</summary>
    public void Begin(long now)
    {
        lock (_lock)
        {
            _started = true;
            _done = false;
            _boot = now;
            _last = 0;
            _jitLast = 0;
            _jitPrevious = 0;
            _firstFrameSeconds = -1;
            _elapsedMs = 0;
            _frames = 0;
            _slow = 0;
            _verySlow = 0;
            _slowWithJit = 0;
            _excessMs = 0;
            _jitMs = 0;
        }
    }

    /// <summary>A frame was presented at <paramref name="now"/>, with <paramref name="jitTicks"/> of JIT compilation
    /// so far (a running total); returns the report line when this frame completes the first minute, else null.</summary>
    public string? Present(long now, long jitTicks)
    {
        if (!_started || _done)
        {
            return null; // no lock: after the first minute (or before a session) a present costs two reads
        }

        lock (_lock)
        {
            if (!_started || _done)
            {
                return null;
            }

            if (_firstFrameSeconds < 0)
            {
                _firstFrameSeconds = (now - _boot) / (_ticksPerMs * 1000);
                _last = now;
                _jitLast = jitTicks;
                return null;
            }

            double interval = (now - _last) / _ticksPerMs;
            double jit = (jitTicks - _jitLast) / _ticksPerMs;
            _last = now;
            _jitLast = jitTicks;
            _elapsedMs += interval;
            _frames++;
            _jitMs += jit;
            if (interval > SlowMs)
            {
                _slow++;
                _excessMs += interval - 1000.0 / 30;
                if (interval > VerySlowMs)
                {
                    _verySlow++;
                }

                if (jit + _jitPrevious > JitMs)
                {
                    _slowWithJit++;
                }
            }

            _jitPrevious = jit;
            if (_elapsedMs < Seconds * 1000)
            {
                return null;
            }

            _done = true;
            return Describe(complete: true);
        }
    }

    /// <summary>The session ends: the report line when the first minute was not over yet (null when it was already
    /// reported, or no session started).</summary>
    public string? Finish()
    {
        lock (_lock)
        {
            if (!_started || _done)
            {
                return null;
            }

            _done = true;
            return Describe(complete: false);
        }
    }

    private string Describe(bool complete)
    {
        if (_firstFrameSeconds < 0)
        {
            return "Startup: no frame presented";
        }

        string span = complete ? $"first {Seconds:F0} s" : $"first {_elapsedMs / 1000:F1} s (the session ended)";
        return $"Startup: first frame {_firstFrameSeconds:F1} s after the emulator started; {span}: {_frames} frames, "
            + $"{_slow} over {SlowMs:F0} ms ({_verySlow} over {VerySlowMs:F0} ms, {_excessMs:F0} ms above 33.3 ms), "
            + $"{_slowWithJit} of them with JIT compilation over {JitMs:F0} ms, JIT compiling {_jitMs:F0} ms";
    }

    /// <summary><see cref="Present"/> on <see cref="Current"/> from the display, logging the line.</summary>
    public static void OnPresent()
    {
        StartupMeter meter = Current;
        if (!meter._started || meter._done)
        {
            return;
        }

        if (meter.Present(EmulationPause.ActiveHostTicks, FrameLedger.Total(FrameLedger.Cat.JitCompile)) is { } line)
        {
            Log.Info(line);
        }
    }
}
