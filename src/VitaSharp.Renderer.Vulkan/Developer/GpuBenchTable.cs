#if VITASHARP_DEVELOPER
using System.Globalization;

namespace VitaSharp.Renderer.Vulkan.Developer;

/// <summary>The bench results table (results/RUN.tsv rows of <see cref="GpuBench"/>): vitasharp-perf bench table and the
/// Android developer screen.</summary>
public static class GpuBenchTable
{
    public static double Median(List<double> v)
    {
        var s = v.Order().ToList();
        return s.Count == 0 ? double.NaN : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>fixture x variant (x config) -> median GPU ms, spread (max-min) and delta against the base variant.</summary>
    public static string Format(IEnumerable<string> lines, string baseVariant, int scenes)
    {
        var o = new System.Text.StringBuilder();
        var rows = lines.Select(l => l.Split('\t')).Where(c => c.Length >= 6)
            .Select(c => (Fixture: c[0], Variant: c[1], Config: c[2], Frame: double.Parse(c[4], CultureInfo.InvariantCulture),
                Scenes: c[5].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray())).ToList();
        if (rows.Count == 0) return "";
        o.AppendLine($"{"fixture",-22} {"variant",-14} {"config",-12} {"n",3} {"GPU ms",8} {"spread",7} {"delta",8} {"%",6}");
        foreach (var fg in rows.GroupBy(r => r.Fixture))
        {
            var groups = fg.GroupBy(r => (r.Variant, r.Config)).ToList();
            var baseMed = groups.Where(g => g.Key.Variant == baseVariant).ToDictionary(g => g.Key.Config, g => Median(g.Select(r => r.Frame).ToList()));
            foreach (var g in groups.OrderBy(g => g.Key.Variant != baseVariant).ThenBy(g => g.Key.Variant).ThenBy(g => g.Key.Config))
            {
                var v = g.Select(r => r.Frame).ToList();
                double med = Median(v);
                // a draw bench's delta is against its own reference config of the same variant
                double? reference = g.Key.Config.StartsWith("without") ? Median(fg.Where(r => r.Variant == g.Key.Variant && r.Config == "frame").Select(r => r.Frame).ToList())
                    : g.Key.Config.StartsWith("only") ? Median(fg.Where(r => r.Variant == g.Key.Variant && r.Config == "none" + g.Key.Config[4..]).Select(r => r.Frame).ToList())
                    : g.Key.Variant != baseVariant && baseMed.TryGetValue(g.Key.Config, out var b) ? b : null;
                string delta = reference is { } rf && !double.IsNaN(rf) ? FormattableString.Invariant($"{med - rf,8:+0.000;-0.000} {(med - rf) / rf * 100,6:+0.0;-0.0}") : "";
                o.AppendLine(FormattableString.Invariant($"{fg.Key,-22} {g.Key.Variant,-14} {g.Key.Config,-12} {v.Count,3} {med,8:F3} {v.Max() - v.Min(),7:F3} {delta}"));
                if (scenes <= 0) continue;
                int n = g.Max(r => r.Scenes.Length);
                var sceneMed = Enumerable.Range(0, n).Select(i => (i, Median(g.Where(r => r.Scenes.Length > i).Select(r => r.Scenes[i]).ToList()))).OrderByDescending(x => x.Item2).Take(scenes);
                o.AppendLine("      top scenes: " + string.Join("  ", sceneMed.Select(x => FormattableString.Invariant($"s{x.i} {x.Item2:F3}"))));
            }
        }
        o.AppendLine("delta: the variant against the base variant (draw benches: 'withoutK' against 'frame', 'onlyK' against 'noneK' = the draw's cost); spread = max - min");
        return o.ToString();
    }
}
#endif
