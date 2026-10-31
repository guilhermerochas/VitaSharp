namespace VitaSharp.Android.Performance;

/// <summary>
/// The frame rate a game asks for, in vblanks per frame (1 = 59.94 fps, 2 = 29.97 fps...), from its presented frames.
/// A frame interval alone says how slow the phone is as much as what the game wants: a 60 fps game that takes 20 ms
/// per frame and then waits for the next vblank presents every 2 vblanks, and taking that for a 30 fps game would tell
/// the performance hints it is on time. So only two kinds of frames count: one-vblank frames (the game is on time at
/// 60), and frames during which the game waited more than one whole vblank period for the display (only a wait for two
/// or more vblanks lasts that long: <c>sceDisplayWaitVblankStartMulti(2)</c>, <c>sceDisplayWaitSetFrameBufMulti(2)</c>,
/// a deliberate lower rate). The rate is the smallest such interval, rounded to whole vblanks, among the last
/// <see cref="Window"/> counted frames, between 1 and <see cref="MaxVblanks"/>; 1 until there are any.
/// </summary>
public sealed class FrameRateEstimator
{
    public const int Window = 60, MaxVblanks = 4;
    public const double VblankSeconds = 1.0 / 59.94;

    readonly int[] _ring = new int[Window];
    int _count, _next;
    long _lastTicks = -1;

    /// <summary>Vblanks per frame (1-4).</summary>
    public int Vblanks { get; private set; } = 1;

    /// <summary>The target frame time, nanoseconds.</summary>
    public long TargetNanos => (long)(Vblanks * VblankSeconds * 1e9);

    /// <summary>The display refresh rate that shows this frame rate without judder (60, 30, 20, 15).</summary>
    public float RefreshRate => 60f / Vblanks;

    /// <summary>A frame was presented at <paramref name="ticks"/> (of <paramref name="frequency"/> per second);
    /// <paramref name="longWait"/>: the guest waited more than a vblank period for the display since the previous
    /// frame. Returns true when <see cref="Vblanks"/> changed.</summary>
    public bool OnFrame(long ticks, long frequency, bool longWait)
    {
        long last = _lastTicks;
        _lastTicks = ticks;
        if (last < 0 || ticks <= last) return false;
        double vblanks = (ticks - last) / (double)frequency / VblankSeconds;
        int rounded = Math.Max(1, (int)Math.Round(vblanks));
        if (rounded > MaxVblanks || rounded > 1 && !longWait) return false; // a load or hitch, or a frame paced by the host
        _ring[_next] = rounded;
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;
        int min = MaxVblanks;
        for (int i = 0; i < _count; i++) min = Math.Min(min, _ring[i]);
        if (min == Vblanks) return false;
        Vblanks = min;
        return true;
    }
}

/// <summary>
/// The work duration of a frame for the performance hint session: the CPU time of the busiest hot thread since the
/// previous frame (the critical path: the main guest thread, or the GPU worker when the game waits for it), never more
/// than the frame's wall time. Time a thread spends blocked (vblank waits, pacing sleeps, waiting for the GPU) is not
/// work, so a phone with headroom reports less than the target and the system may lower its clocks, and a phone that
/// is behind reports about the whole interval.
/// </summary>
public sealed class FrameWorkMeter
{
    long[] _last = [];
    int[] _tids = [];
    long _lastWall = -1;

    /// <summary>Starts measuring these threads (their CPU times now, nanoseconds; a negative time = unreadable).</summary>
    public void Reset(int[] tids, long[] cpuNanos, long wallNanos)
    {
        _tids = tids;
        _last = (long[])cpuNanos.Clone();
        _lastWall = wallNanos;
    }

    public int[] Tids => _tids;

    /// <summary>A frame ended: the threads' CPU times now (same order as the tids). Returns the work, nanoseconds, or
    /// -1 when there is nothing to report (first frame, no thread readable).</summary>
    public long OnFrame(long[] cpuNanos, long wallNanos)
    {
        long wall = _lastWall < 0 ? -1 : wallNanos - _lastWall;
        _lastWall = wallNanos;
        long work = -1;
        for (int i = 0; i < cpuNanos.Length && i < _last.Length; i++)
        {
            if (cpuNanos[i] >= 0 && _last[i] >= 0) work = Math.Max(work, cpuNanos[i] - _last[i]);
            _last[i] = cpuNanos[i];
        }
        if (wall <= 0 || work < 0) return -1;
        return Math.Clamp(work, 1, wall);
    }
}
