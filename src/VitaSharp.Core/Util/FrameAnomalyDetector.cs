namespace VitaSharp.Core.Util;

/// <summary>Cheap colour metrics of one presented frame (fractions of the sampled pixels, mean brightness 0..1).</summary>
public struct FrameMetrics
{
    public float Mean, White, Black, Magenta, Green, Blue;
    public ulong Hash;

    [Flags]
    public enum Hit : byte { None = 0, Magenta = 1, Saturated = 2, Bright = 4, Black = 8 }

    public const float MagentaLimit = 0.05f, SaturatedLimit = 0.08f, WhiteLimit = 0.35f, MeanLimit = 0.85f, BlackLimit = 0.97f;

    /// <summary>Which per-frame thresholds this frame exceeds (the detector decides how long they must last).</summary>
    public readonly Hit Hits =>
        (Magenta > MagentaLimit ? Hit.Magenta : 0) | (Green + Blue > SaturatedLimit ? Hit.Saturated : 0) |
        (White > WhiteLimit || Mean > MeanLimit ? Hit.Bright : 0) | (Black > BlackLimit ? Hit.Black : 0);

    /// <summary>
    /// Measures an RGBA8 image on every <paramref name="step"/>-th pixel of every <paramref name="step"/>-th row:
    /// magenta = r,b &gt; 200 and g &lt; 80 (missing/garbage textures), green = g &gt; 180 and r,b &lt; 60, blue = b &gt; 180
    /// and r,g &lt; 60 (garbage target/effect), white = all &gt; 235, black = all &lt; 10. The hash (6 bits per channel)
    /// detects frozen output.
    /// </summary>
    public static unsafe FrameMetrics Measure(byte* rgba, int w, int h, int step = 2)
    {
        long sum = 0;
        int n = 0, white = 0, dark = 0, pink = 0, green = 0, blue = 0;
        ulong hash = 14695981039346656037;
        for (int y = 0; y < h; y += step)
        {
            byte* row = rgba + (long)y * w * 4;
            for (int x = 0; x < w; x += step)
            {
                byte* q = row + x * 4;
                int r = q[0], g = q[1], b = q[2];
                sum += (r * 54 + g * 183 + b * 19) >> 8;
                if (r > 235 && g > 235 && b > 235) white++;
                if (r < 10 && g < 10 && b < 10) dark++;
                if (r > 200 && b > 200 && g < 80) pink++;
                if (g > 180 && r < 60 && b < 60) green++;
                if (b > 180 && r < 60 && g < 60) blue++;
                hash = (hash ^ (ulong)((r >> 2) | (g >> 2) << 8 | (b >> 2) << 16)) * 1099511628211;
                n++;
            }
        }
        if (n == 0) return default;
        float fn = n;
        return new FrameMetrics
        {
            Mean = sum / (255f * fn), White = white / fn, Black = dark / fn, Magenta = pink / fn, Green = green / fn, Blue = blue / fn, Hash = hash,
        };
    }

    public readonly override string ToString() =>
        $"mean {Mean:F3} white {White:F3} black {Black:F3} magenta {Magenta:F3} green {Green:F3} blue {Blue:F3}";
}

/// <summary>
/// Debug mode: per-frame anomaly rules over <see cref="FrameMetrics"/> of every presented frame (see
/// docs/notes/debug-mode.md). Reacts to short bursts: a magenta or saturated green/blue spike in 2 of 3 consecutive
/// frames, or a brightness excursion of 1-3 frames that snaps back, fires at once. Slow conditions (blown-out, black,
/// frozen) need to last. Pure logic, no GPU: <see cref="Feed"/> returns the events of that frame.
/// </summary>
public sealed class FrameAnomalyDetector
{
    public readonly record struct Event(string Kind, string Detail, bool Capture, long Present);

    /// <summary>Spike kinds: hits needed within the last <see cref="SpikeWindow"/> frames.</summary>
    public const int SpikeHits = 2, SpikeWindow = 3;
    /// <summary>Brightness excursion: change of the mean, the longest excursion that still counts as a flicker (frames),
    /// and how close to the baseline the frame after it must be.</summary>
    public const float FlickerDelta = 0.35f, FlickerReturn = 0.12f;
    public const int FlickerMaxFrames = 3;
    public const int BrightFrames = 60, BlackFrames = 30, FrozenFrames = 600;

    readonly List<Event> _events = new();
    // recent per-frame hit masks (newest at index 0) for the spike windows
    readonly FrameMetrics.Hit[] _recent = new FrameMetrics.Hit[SpikeWindow];
    bool _magentaOn, _saturatedOn;
    int _magentaQuiet, _saturatedQuiet;
    int _brightRun, _blackRun, _frozenRun;
    ulong _prevHash;
    // flicker: the last stable mean (not inside an excursion) and how long the current excursion lasts
    float _baseline = -1;
    bool _baselineBlack;
    int _excursion;
    float _excursionPeak;
    long _excursionStart;

    public IReadOnlyList<Event> Feed(long present, in FrameMetrics m)
    {
        _events.Clear();
        var hits = m.Hits;
        for (int i = SpikeWindow - 1; i > 0; i--) _recent[i] = _recent[i - 1];
        _recent[0] = hits;

        Spike(FrameMetrics.Hit.Magenta, "magenta", ref _magentaOn, ref _magentaQuiet, present,
            $"{m.Magenta:P0} of the image is magenta (missing/garbage textures)");
        Spike(FrameMetrics.Hit.Saturated, "saturated", ref _saturatedOn, ref _saturatedQuiet, present,
            $"{m.Green:P0} pure green and {m.Blue:P0} pure blue pixels (garbage render target / effect)");

        Run(hits.HasFlag(FrameMetrics.Hit.Bright), ref _brightRun, BrightFrames, "blown-out", true, present,
            $"{m.White:P0} saturated white, mean brightness {m.Mean:F2} for {BrightFrames} frames");
        Run(hits.HasFlag(FrameMetrics.Hit.Black), ref _blackRun, BlackFrames, "black", false, present,
            $"more than 97% black pixels for {BlackFrames} frames");
        Run(m.Hash == _prevHash, ref _frozenRun, FrozenFrames, "frozen", false, present,
            $"identical image for {FrozenFrames} presents");
        _prevHash = m.Hash;

        Flicker(present, m, hits.HasFlag(FrameMetrics.Hit.Black));
        return _events;
    }

    void Spike(FrameMetrics.Hit kind, string name, ref bool on, ref int quiet, long present, string detail)
    {
        int n = 0;
        foreach (var h in _recent) if ((h & kind) != 0) n++;
        if (!on)
        {
            if (n >= SpikeHits)
            {
                on = true;
                quiet = 0;
                _events.Add(new Event(name, $"{detail}, in {n} of the last {SpikeWindow} frames", true, present));
            }
            return;
        }
        // confirmed: cleared once the kind is gone for a whole window
        quiet = n == 0 ? quiet + 1 : 0;
        if (quiet >= SpikeWindow)
        {
            on = false;
            _events.Add(new Event(name, "cleared", false, present));
        }
    }

    void Run(bool hit, ref int run, int need, string name, bool capture, long present, string detail)
    {
        if (hit)
        {
            if (++run == need) _events.Add(new Event(name, detail, capture, present));
            return;
        }
        if (run >= need) _events.Add(new Event(name, $"cleared after {run} frames", false, present));
        run = 0;
    }

    void Flicker(long present, in FrameMetrics m, bool black)
    {
        if (_baseline < 0) { _baseline = m.Mean; _baselineBlack = black; return; }
        float d = MathF.Abs(m.Mean - _baseline);
        if (_excursion > 0)
        {
            if (d <= FlickerReturn && !black && !_baselineBlack)
            {
                // snapped back to the frame before the excursion: a transient flash/blackout, not a scene change
                _events.Add(new Event("flicker", $"mean brightness {_baseline:F2} -> {_excursionPeak:F2} -> {m.Mean:F2} for {_excursion} frame(s) from present #{_excursionStart}", true, present));
                _excursion = 0;
                _baseline = m.Mean;
                _baselineBlack = black;
                return;
            }
            if (d > FlickerDelta && _excursion < FlickerMaxFrames)
            {
                _excursion++;
                if (MathF.Abs(m.Mean - _baseline) > MathF.Abs(_excursionPeak - _baseline)) _excursionPeak = m.Mean;
                return;
            }
            // too long (a cut or a fade) or ended somewhere else: new baseline
            _excursion = 0;
        }
        else if (d > FlickerDelta)
        {
            _excursion = 1;
            _excursionPeak = m.Mean;
            _excursionStart = present;
            return;
        }
        _baseline = m.Mean;
        _baselineBlack = black;
    }
}
