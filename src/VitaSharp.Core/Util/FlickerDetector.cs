namespace VitaSharp.Core.Util;

/// <summary>
/// One-frame flashes and stale/dark frames in a stream of small frame pictures (RGBA thumbnails of equal size): frame N is
/// flagged when it differs strongly from both neighbours (mean channel difference above the threshold) while the
/// neighbours match each other (below a third of it). That is the rule of <c>vitasharp-perf flicker</c>, applied to the
/// whole picture and to each cell of a grid, so a flickering banner is found even when the rest of the screen is
/// still. Used by the desktop scenario runner and the developer GPU bench (on the phone).
/// </summary>
public sealed class FlickerDetector(double threshold = 6, int grid = 4)
{
    public readonly record struct Flag(long Frame, double Prev, double Next, double Around, int Cell, double Luma, double LumaPrev);

    readonly (long Frame, byte[] Rgba, int W, int H)?[] _last = new (long, byte[], int, int)?[2];
    public readonly List<Flag> Flagged = new();
    public long Frames { get; private set; }
    public double Threshold => threshold;

    /// <summary>Adds the next frame (consecutive frame numbers; a gap restarts the comparison). Returns the flag of the
    /// frame before it, if that one is a flash.</summary>
    public Flag? Add(long frame, byte[] rgba, int w, int h)
    {
        Frames++;
        Flag? flag = null;
        if (_last[0] is { } a && _last[1] is { } b && b.Frame == frame - 1 && a.Frame == frame - 2 && a.W == w && b.W == w && a.H == h && b.H == h)
        {
            int cells = grid * grid;
            for (int c = -1; c < cells && flag == null; c++)
            {
                double prev = Diff(a.Rgba, b.Rgba, w, h, c), next = Diff(b.Rgba, rgba, w, h, c), around = Diff(a.Rgba, rgba, w, h, c);
                if (prev > threshold && next > threshold && around < threshold / 3)
                    flag = new Flag(b.Frame, prev, next, around, c, Luma(b.Rgba), Luma(a.Rgba));
            }
            if (flag is { } f) Flagged.Add(f);
        }
        _last[0] = _last[1];
        _last[1] = (frame, rgba, w, h);
        return flag;
    }

    /// <summary>Mean absolute channel difference over the picture (cell -1) or one grid cell.</summary>
    double Diff(byte[] x, byte[] y, int w, int h, int cell)
    {
        int x0 = 0, y0 = 0, x1 = w, y1 = h;
        if (cell >= 0)
        {
            x0 = cell % grid * w / grid; x1 = (cell % grid + 1) * w / grid;
            y0 = cell / grid * h / grid; y1 = (cell / grid + 1) * h / grid;
        }
        long sum = 0, n = 0;
        for (int yy = y0; yy < y1; yy++)
            for (int xx = x0; xx < x1; xx++)
            {
                int o = (yy * w + xx) * 4;
                sum += Math.Abs(x[o] - y[o]) + Math.Abs(x[o + 1] - y[o + 1]) + Math.Abs(x[o + 2] - y[o + 2]);
                n += 3;
            }
        return n == 0 ? 0 : sum / (double)n;
    }

    public static double Luma(byte[] rgba)
    {
        double s = 0;
        for (int i = 0; i < rgba.Length; i += 4) s += 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2];
        return rgba.Length == 0 ? 0 : s / (rgba.Length / 4);
    }

    public string Summary => $"FLICKER {Frames} frames: {Flagged.Count} flagged (threshold {threshold}, grid {grid}x{grid})";

    public string Describe(Flag f) => FormattableString.Invariant(
        $"frame {f.Frame}: prev {f.Prev:F2} next {f.Next:F2} around {f.Around:F2} {(f.Cell < 0 ? "whole picture" : $"cell {f.Cell % grid},{f.Cell / grid}")} luma {f.Luma:F1} (prev {f.LumaPrev:F1})");
}
