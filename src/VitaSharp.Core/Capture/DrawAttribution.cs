using System.Globalization;
using System.Text;

namespace VitaSharp.Core.Capture;

/// <summary>
/// Pixel -> draw attribution of a picture finding (docs/captures.md, "Which draw"): a replay of the GPU dump with the
/// renderer's draw probe (<c>replay-gpu --probe=PRESENT[:CELL]</c>) records, for every draw into the surface shown at
/// the flashing present and at its neighbours, how many pixels of the finding's region it changed and the depth and
/// stencil under the region before it, with its programs and state (<see cref="Probed"/>, probe.txt). The draws of the
/// flashing frame are lined up with the neighbour's (same programs and target, in order: a longest common
/// subsequence) and the first one that differs is the draw responsible: one only one of the two frames has, one that
/// writes the region in one frame and nothing in the other (rejected by the depth, stencil or alpha test, or moved),
/// or one whose state differs.
/// </summary>
public static class DrawAttribution
{
    public const string FileName = "probe.txt";

    /// <summary>One probed draw: the present whose picture it is behind, its number in the dump, the target it rendered
    /// into, its programs (hashes), its state (<c>depth=func/write st=... blend=... t0=...</c>), the pixels of the
    /// region it changed out of the region's pixels, and the mean depth (0-1) and stencil under the region before it
    /// (-1 without a depth/stencil surface).</summary>
    public readonly record struct Probed(long Present, long Draw, uint Target, ulong Vs, ulong Fs, int Changed, int Pixels, double Depth, double Stencil, string State)
    {
        public string Describe() => FormattableString.Invariant(
            $"draw {Draw} vs={Vs:X16} fs={Fs:X16} rt=0x{Target:X8} {State}");
    }

    public static void Write(string path, string header, IEnumerable<Probed> draws)
    {
        var sb = new StringBuilder();
        foreach (string line in header.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append("# ").Append(line.TrimEnd('\r')).Append('\n');
        }

        sb.Append("# present\tdraw\ttarget\tvs\tfs\tchanged\tpixels\tdepth\tstencil\tstate\n");
        foreach (Probed d in draws)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{d.Present}\t{d.Draw}\t{d.Target:X8}\t{d.Vs:X16}\t{d.Fs:X16}\t{d.Changed}\t{d.Pixels}\t{d.Depth:F5}\t{d.Stencil:F2}\t{d.State}\n");
        }

        File.WriteAllText(path, sb.ToString());
    }

    public static List<Probed> Read(string path)
    {
        var list = new List<Probed>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split('\t', 10);
            if (f.Length < 10)
            {
                continue;
            }

            list.Add(new Probed(
                long.Parse(f[0], CultureInfo.InvariantCulture), long.Parse(f[1], CultureInfo.InvariantCulture),
                uint.Parse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                ulong.Parse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture), ulong.Parse(f[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(f[5], CultureInfo.InvariantCulture), int.Parse(f[6], CultureInfo.InvariantCulture),
                double.Parse(f[7], CultureInfo.InvariantCulture), double.Parse(f[8], CultureInfo.InvariantCulture), f[9]));
        }

        return list;
    }

    /// <summary>Why a draw differs between the flashing frame and the neighbour.</summary>
    public enum Reason
    {
        /// <summary>The neighbour has the draw (and it writes the region), the flashing frame does not.</summary>
        MissingInFlash,
        /// <summary>Only the flashing frame has the draw (and it writes the region).</summary>
        OnlyInFlash,
        /// <summary>The same draw wrote nothing in the region in the flashing frame (rejected by the depth, stencil or
        /// alpha test, clipped or moved), but did in the neighbour.</summary>
        WroteNothingInFlash,
        /// <summary>The same draw wrote the region only in the flashing frame.</summary>
        WroteOnlyInFlash,
        /// <summary>The same draw, writing the region in both, with another state.</summary>
        OtherState,
    }

    /// <summary>A state field (depth=, st=, blend=...) or the depth/stencil under the region before the draw, as it was
    /// in the flashing frame and in the neighbour.</summary>
    public readonly record struct Difference(string Field, string Flash, string Neighbour);

    /// <summary>One draw that differs: the draw, why, the pixels the matching draw of the other frame wrote, and the
    /// fields that differ.</summary>
    public sealed record Candidate(Probed Draw, Reason Reason, int OtherChanged, List<Difference> Differences);

    /// <summary>The depth and stencil fields of <see cref="Difference"/> (the others are the state's own names).</summary>
    public const string DepthField = "depth-before", StencilField = "stencil-before";

    /// <summary>The attribution: the presents compared, their draws into the shown surface (and how many wrote the
    /// region), and the draws that differ, the first being the draw responsible (none: the difference comes from a
    /// surface drawn earlier and sampled, or from which buffer was presented).</summary>
    public sealed record Result(long Present, long Neighbour, int FlashDraws, int FlashWriting, int NeighbourDraws, int NeighbourWriting, List<Candidate> Candidates)
    {
        public Candidate? Responsible => Candidates.Count > 0 ? Candidates[0] : null;
    }

    /// <summary>
    /// Lines up the draws behind <paramref name="flashPresent"/> with those behind the neighbour present (the one
    /// before, else the one after) and names the first draw that differs in the region.
    /// </summary>
    public static Result Attribute(IReadOnlyList<Probed> draws, long flashPresent)
    {
        var flash = draws.Where(d => d.Present == flashPresent).OrderBy(d => d.Draw).ToList();
        long neighbourPresent = draws.Any(d => d.Present == flashPresent - 1) ? flashPresent - 1 : flashPresent + 1;
        var other = draws.Where(d => d.Present == neighbourPresent).OrderBy(d => d.Draw).ToList();
        var candidates = new List<Candidate>();
        var result = new Result(flashPresent, neighbourPresent, flash.Count, flash.Count(d => d.Changed > 0), other.Count, other.Count(d => d.Changed > 0), candidates);

        // longest common subsequence of (vs, fs): the same draws of the two frames, in order
        int n = flash.Count, m = other.Count;
        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = SameDraw(flash[i], other[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int a = 0, b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && SameDraw(flash[a], other[b]))
            {
                Probed x = flash[a], y = other[b];
                if ((x.Changed > 0) != (y.Changed > 0))
                {
                    candidates.Add(new Candidate(x, x.Changed > 0 ? Reason.WroteOnlyInFlash : Reason.WroteNothingInFlash, y.Changed, Differences(x, y)));
                }
                else if (x.Changed > 0 && x.State != y.State)
                {
                    candidates.Add(new Candidate(x, Reason.OtherState, y.Changed, Differences(x, y)));
                }

                a++;
                b++;
            }
            else if (b >= m || (a < n && lcs[a + 1, b] >= lcs[a, b + 1]))
            {
                if (flash[a].Changed > 0)
                {
                    candidates.Add(new Candidate(flash[a], Reason.OnlyInFlash, 0, new List<Difference>()));
                }

                a++;
            }
            else
            {
                if (other[b].Changed > 0)
                {
                    candidates.Add(new Candidate(other[b], Reason.MissingInFlash, other[b].Changed, new List<Difference>()));
                }

                b++;
            }
        }

        return result;
    }

    /// <summary>The same draw of two frames: the same programs (the shown surface may be another buffer of a double or
    /// triple buffered display, so the target is not compared).</summary>
    private static bool SameDraw(Probed a, Probed b) => a.Vs == b.Vs && a.Fs == b.Fs;

    /// <summary>The state fields that differ (depth=, st=, blend=...), and the depth/stencil under the region before the
    /// draw when they differ.</summary>
    private static List<Difference> Differences(Probed x, Probed y)
    {
        var parts = new List<Difference>();
        Dictionary<string, string> sx = Fields(x.State), sy = Fields(y.State);
        foreach (string key in sx.Keys.Union(sy.Keys))
        {
            string vx = sx.GetValueOrDefault(key, "-"), vy = sy.GetValueOrDefault(key, "-");
            if (vx != vy)
            {
                parts.Add(new Difference(key, vx, vy));
            }
        }

        if (Math.Abs(x.Depth - y.Depth) > 1e-4)
        {
            parts.Add(new Difference(DepthField, x.Depth.ToString("F4", CultureInfo.InvariantCulture), y.Depth.ToString("F4", CultureInfo.InvariantCulture)));
        }

        if (Math.Abs(x.Stencil - y.Stencil) > 0.01)
        {
            parts.Add(new Difference(StencilField, x.Stencil.ToString("F2", CultureInfo.InvariantCulture), y.Stencil.ToString("F2", CultureInfo.InvariantCulture)));
        }

        return parts;
    }

    private static Dictionary<string, string> Fields(string state)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in state.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                d[part[..eq]] = part[(eq + 1)..];
            }
        }

        return d;
    }

    /// <summary>The region of cell <paramref name="cell"/> of the 4x4 grid over a <paramref name="w"/> x
    /// <paramref name="h"/> picture (the whole picture for -1 and -2).</summary>
    public static (uint X, uint Y, uint W, uint H) CellRegion(int cell, uint w, uint h)
    {
        if (cell < 0)
        {
            return (0, 0, w, h);
        }

        uint x0 = (uint)(cell % 4) * w / 4, x1 = (uint)(cell % 4 + 1) * w / 4, y0 = (uint)(cell / 4) * h / 4, y1 = (uint)(cell / 4 + 1) * h / 4;
        return (x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }
}
