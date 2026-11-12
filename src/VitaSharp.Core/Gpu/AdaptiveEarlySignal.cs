using VitaSharp.Core.Util;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// <see cref="EnvVars.DisplayEarlySignal"/>=auto: decides, from the game's own frame times, whether a display queue
/// entry's flip callback runs at submit (early) or once the GPU finished the frame. Early helps a frame that is serial
/// on GPU latency (the game waits on the display queue while the worker idles; Killzone +12%) and hurts one that is CPU
/// bound (the game runs ahead and competes with the worker for the big cores; F1 2011 -15%), so neither is a safe
/// default. The rule measures instead of guessing: while the game misses a steady vblank cadence and waits on the display
/// queue, it tries early for one window and keeps it only when the mean frame time dropped by <see cref="GainNeeded"/>;
/// while early it re-checks the completion signal every <see cref="RecheckFrames"/> frames and goes back when that is
/// no slower. A failed try backs off (doubling, up to <see cref="BackoffMax"/> frames). A game locked to a vblank
/// multiple (nothing to gain) is never tried. Not thread-safe: the caller serializes <see cref="OnFrame"/>.
/// </summary>
public sealed class AdaptiveEarlySignal
{
    /// <summary>Frames per measurement window, and frames skipped after a switch before measuring.</summary>
    public const int Window = 60, Settle = 15;
    /// <summary>Mean display queue wait per frame (ms) that makes a try worthwhile.</summary>
    public const double MinWaitMs = 3;
    /// <summary>Early must bring the mean frame time to at most this fraction of the completion signal's.</summary>
    public const double GainNeeded = 0.95;
    /// <summary>While early, the completion signal is taken back when its mean is at most this factor of early's.</summary>
    public const double KeepMargin = 1.02;
    public const int RecheckFrames = 900, BackoffMin = 300, BackoffMax = 7200;
    /// <summary>A window is locked when this fraction of its frame intervals is within <see cref="LockedTolMs"/> of the
    /// median (the game already presents at a steady cadence).</summary>
    public const double LockedFraction = 0.9, LockedTolMs = 3;

    public enum Phase
    {
        Off, ProbeOn, On, ProbeOff
    }

    private readonly double[] _intervals = new double[Window], _sorted = new double[Window];
    private int _count, _skip, _cooldown, _backoff = BackoffMin, _sinceCheck;
    private double _sum, _waitSum, _baseline;

    public Phase State { get; private set; }
    /// <summary>The flip callback runs at submit.</summary>
    public bool Early => State is Phase.ProbeOn or Phase.On;
    /// <summary>The last decision, for the log (null until the first one).</summary>
    public string? LastDecision { get; private set; }

    /// <summary>One display queue entry: <paramref name="intervalMs"/> since the previous one and the time the game was
    /// blocked in it (<paramref name="displayWaitMs"/>). Returns whether this entry signals early; true when the state
    /// changed is reported through <paramref name="changed"/>.</summary>
    public bool OnFrame(double intervalMs, double displayWaitMs, out bool changed)
    {
        changed = false;
        if (_skip > 0)
        {
            _skip--;
            return Early;
        }
        if (_cooldown > 0)
        {
            _cooldown--;
        }

        _intervals[_count++] = intervalMs;
        _sum += intervalMs;
        _waitSum += displayWaitMs;
        if (_count < Window)
        {
            return Early;
        }

        double mean = _sum / Window, wait = _waitSum / Window;
        bool locked = Locked();
        _count = 0;
        _sum = _waitSum = 0;
        Phase before = State;
        switch (State)
        {
            case Phase.Off:
                if (_cooldown == 0 && wait >= MinWaitMs && !locked)
                {
                    _baseline = mean;
                    Go(Phase.ProbeOn, $"try early: frame {mean:F1} ms, display wait {wait:F1} ms");
                }
                break;
            case Phase.ProbeOn:
                if (mean <= _baseline * GainNeeded)
                {
                    _backoff = BackoffMin;
                    _sinceCheck = 0;
                    Go(Phase.On, $"early kept: {_baseline:F1} -> {mean:F1} ms");
                }
                else
                {
                    Back($"early dropped: {_baseline:F1} -> {mean:F1} ms");
                }

                break;
            case Phase.On:
                _sinceCheck += Window;
                if (_sinceCheck >= RecheckFrames)
                {
                    _baseline = mean;
                    Go(Phase.ProbeOff, $"re-check: early frame {mean:F1} ms");
                }
                break;
            case Phase.ProbeOff:
                if (mean <= _baseline * KeepMargin)
                {
                    Back($"completion signal back: early {_baseline:F1} vs {mean:F1} ms");
                }
                else
                {
                    _sinceCheck = 0;
                    Go(Phase.On, $"early stays: early {_baseline:F1} vs {mean:F1} ms");
                }
                break;
        }
        changed = State != before;
        return Early;
    }

    private void Go(Phase phase, string why)
    {
        if (phase != State)
        {
            _skip = Settle;
        }

        State = phase;
        LastDecision = why;
    }

    private void Back(string why)
    {
        Go(Phase.Off, why);
        _cooldown = _backoff;
        _backoff = Math.Min(_backoff * 2, BackoffMax);
    }

    private bool Locked()
    {
        Array.Copy(_intervals, _sorted, Window);
        Array.Sort(_sorted);
        double median = _sorted[Window / 2];
        int near = 0;
        foreach (double v in _intervals)
        {
            if (Math.Abs(v - median) <= LockedTolMs)
            {
                near++;
            }
        }

        return near >= LockedFraction * Window;
    }
}
