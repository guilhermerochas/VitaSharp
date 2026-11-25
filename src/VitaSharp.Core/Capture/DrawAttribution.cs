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

    /// <summary>The attribution: the lines to print and the draw responsible (null when no draw into the shown surface
    /// differs: the difference comes from a surface drawn earlier and sampled, or from which buffer was presented).</summary>
    public sealed record Result(Probed? Responsible, string Why, List<string> Lines);

    /// <summary>
    /// Lines up the draws behind <paramref name="flashPresent"/> with those behind the neighbour present (the one
    /// before, else the one after) and names the first draw that differs in the region.
    /// </summary>
    public static Result Attribute(IReadOnlyList<Probed> draws, long flashPresent)
    {
        var lines = new List<string>();
        var flash = draws.Where(d => d.Present == flashPresent).OrderBy(d => d.Draw).ToList();
        long neighbourPresent = draws.Any(d => d.Present == flashPresent - 1) ? flashPresent - 1 : flashPresent + 1;
        var other = draws.Where(d => d.Present == neighbourPresent).OrderBy(d => d.Draw).ToList();
        lines.Add(FormattableString.Invariant($"present {flashPresent}: {flash.Count} draws no alvo mostrado ({flash.Count(d => d.Changed > 0)} escreveram na região); vizinho {neighbourPresent}: {other.Count} draws ({other.Count(d => d.Changed > 0)} escreveram)"));
        if (flash.Count == 0 && other.Count == 0)
        {
            return new Result(null, "nenhum draw no alvo mostrado nos dois quadros (imagem da CPU, ou o dump não tem os draws)", lines);
        }

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

        Probed? responsible = null;
        string why = "";
        var candidates = new List<string>();
        void Candidate(Probed draw, string reason)
        {
            candidates.Add($"  {reason}: {draw.Describe()}");
            if (responsible == null)
            {
                responsible = draw;
                why = reason;
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
                    string written = x.Changed > 0
                        ? FormattableString.Invariant($"escreveu {x.Changed} px da região no quadro do pisca e 0 no vizinho")
                        : FormattableString.Invariant($"não escreveu na região no quadro do pisca (no vizinho: {y.Changed} px)");
                    Candidate(x, written + StateDifference(x, y));
                }
                else if (x.Changed > 0 && x.State != y.State)
                {
                    Candidate(x, "estado diferente do vizinho" + StateDifference(x, y));
                }

                a++;
                b++;
            }
            else if (b >= m || (a < n && lcs[a + 1, b] >= lcs[a, b + 1]))
            {
                if (flash[a].Changed > 0)
                {
                    Candidate(flash[a], FormattableString.Invariant($"só no quadro do pisca (escreveu {flash[a].Changed} px da região)"));
                }

                a++;
            }
            else
            {
                if (other[b].Changed > 0)
                {
                    Candidate(other[b], FormattableString.Invariant($"falta no quadro do pisca (no vizinho {neighbourPresent} escreveu {other[b].Changed} px da região)"));
                }

                b++;
            }
        }

        if (responsible is { } r)
        {
            lines.Add($"draw responsável: {r.Describe()}");
            lines.Add($"  por quê: {why}");
            if (candidates.Count > 1)
            {
                lines.Add($"outras diferenças ({candidates.Count - 1}):");
                lines.AddRange(candidates.Skip(1).Take(12));
            }

            return new Result(r, why, lines);
        }

        lines.Add("nenhum draw no alvo mostrado difere na região: a diferença vem de um alvo desenhado antes e amostrado (textura), ou de qual buffer foi apresentado");
        return new Result(null, "nenhum draw difere", lines);
    }

    /// <summary>The same draw of two frames: the same programs (the shown surface may be another buffer of a double or
    /// triple buffered display, so the target is not compared).</summary>
    private static bool SameDraw(Probed a, Probed b) => a.Vs == b.Vs && a.Fs == b.Fs;

    /// <summary>The state fields that differ (depth=, st=, blend=...), and the depth/stencil under the region before the
    /// draw when they differ.</summary>
    private static string StateDifference(Probed x, Probed y)
    {
        var parts = new List<string>();
        Dictionary<string, string> sx = Fields(x.State), sy = Fields(y.State);
        foreach (string key in sx.Keys.Union(sy.Keys))
        {
            string vx = sx.GetValueOrDefault(key, "-"), vy = sy.GetValueOrDefault(key, "-");
            if (vx != vy)
            {
                parts.Add($"{key} {vx} (vizinho {vy})");
            }
        }

        if (Math.Abs(x.Depth - y.Depth) > 1e-4)
        {
            parts.Add(FormattableString.Invariant($"profundidade antes {x.Depth:F4} (vizinho {y.Depth:F4})"));
        }

        if (Math.Abs(x.Stencil - y.Stencil) > 0.01)
        {
            parts.Add(FormattableString.Invariant($"estêncil antes {x.Stencil:F2} (vizinho {y.Stencil:F2})"));
        }

        return parts.Count == 0 ? "" : "; " + string.Join(", ", parts);
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
