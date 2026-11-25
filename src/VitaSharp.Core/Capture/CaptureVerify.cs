using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VitaSharp.Core.Capture;

/// <summary>
/// <c>verify</c> (docs/captures.md, "Checking a fix"): whether the problem a capture recorded is still there with this
/// build. The capture's findings (the report's rules over frames.vsfr, <see cref="FrameFindings"/>, and the NaN/Inf
/// anomalies of its log) are set against the findings of the same rules over a replay of its GPU dump made by this
/// build (<c>replay-gpu --findings</c>: findings.txt), finding by finding (same kind at the same present, one present
/// either way) and kind by kind, with a short verdict: corrigido (fixed), ainda presente (still there), mudou
/// (changed). The timing findings (stalls, presentation order) and the audio need the game logic to run again: they
/// are set against a re-run's report (<see cref="CompareRerun"/>), which is not deterministic (counts, not presents).
/// </summary>
public static class CaptureVerify
{
    /// <summary>The finding kinds a replay of the GPU dump can show again: what was drawn, not when.</summary>
    public static readonly string[] PictureKinds = ["black-flash", "flash", "black-screen", "picture-order", NanKind];

    /// <summary>A NaN/Inf in a render target (debug mode's scan: <c>[anomaly] pN fM nan:</c> log lines).</summary>
    public const string NanKind = "nan";

    public const string FindingsFileName = "findings.txt";

    public enum Verdict
    {
        /// <summary>Nothing of the recording's could be checked (no finding in the replayed range).</summary>
        NothingToCheck,
        Fixed,
        StillPresent,
        Changed,
    }

    public static string Word(Verdict verdict) => verdict switch
    {
        Verdict.Fixed => "corrigido",
        Verdict.StillPresent => "ainda presente",
        Verdict.Changed => "mudou",
        _ => "nada a verificar",
    };

    /// <summary>One finding as findings.txt and the comparison carry it.</summary>
    public readonly record struct Item(string Kind, long Present, int Cell, int Count, string Detail)
    {
        public string Where => Cell >= 0 ? FormattableString.Invariant($"present {Present} cell {Cell % 4},{Cell / 4}") : FormattableString.Invariant($"present {Present}");
    }

    public static Item ToItem(CaptureAnalyzer.Finding finding) => new(finding.Kind, finding.Present, finding.Cell, finding.Count, finding.Detail);

    /// <summary>The NaN/Inf anomalies of a session or replay log (<c>[anomaly] pPRESENT fFRAME nan: detail</c>).</summary>
    public static List<Item> NanEvents(string logPath)
    {
        var list = new List<Item>();
        if (!File.Exists(logPath))
        {
            return list;
        }

        var pattern = new Regex(@"\[anomaly\] p(\d+) f-?\d+ nan: (.*)$");
        // the log of a running replay is still open for writing
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            Match m = pattern.Match(line);
            if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long present))
            {
                list.Add(new Item(NanKind, present, -2, 1, m.Groups[2].Value.Trim()));
            }
        }

        return list;
    }

    /// <summary>findings.txt: the presents a replay showed (<c># presents FIRST LAST</c>), then one finding per line
    /// (kind, present, cell, frames, detail; tab-separated).</summary>
    public static void WriteFindings(string path, long firstPresent, long lastPresent, IEnumerable<Item> items)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# presents {firstPresent} {lastPresent}\n");
        sb.Append("# kind\tpresent\tcell\tframes\tdetail\n");
        foreach (Item item in items)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{item.Kind}\t{item.Present}\t{item.Cell}\t{item.Count}\t{item.Detail.Replace('\t', ' ').Replace('\n', ' ')}\n");
        }

        File.WriteAllText(path, sb.ToString());
    }

    public static (long First, long Last, List<Item> Items) ReadFindings(string path)
    {
        long first = 0, last = -1;
        var items = new List<Item>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("# presents ", StringComparison.Ordinal))
            {
                string[] p = line["# presents ".Length..].Split(' ');
                first = long.Parse(p[0], CultureInfo.InvariantCulture);
                last = long.Parse(p[1], CultureInfo.InvariantCulture);
                continue;
            }

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split('\t', 5);
            if (f.Length < 5)
            {
                continue;
            }

            items.Add(new Item(f[0], long.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture), int.Parse(f[3], CultureInfo.InvariantCulture), f[4]));
        }

        return (first, last, items);
    }

    /// <summary>The result of one comparison: the lines to print and the verdict.</summary>
    public sealed record Result(Verdict Verdict, List<string> Lines, int Recorded, int StillThere, int Gone, int Moved, int New);

    /// <summary>
    /// The recording's picture findings inside the replayed presents (<paramref name="first"/>-<paramref name="last"/>)
    /// against the replay's: each recorded one is still there (the same kind within one present, the same cell or the
    /// whole picture), moved (the same kind nearby, in another cell or up to 3 presents away) or gone; replay findings
    /// that match none are new. Fixed = every recorded one gone and nothing new; still present = any still there.
    /// </summary>
    public static Result Compare(IReadOnlyList<Item> recorded, IReadOnlyList<Item> replayed, long first, long last)
    {
        var lines = new List<string>();
        var inRange = recorded.Where(r => PictureKinds.Contains(r.Kind) && r.Present >= first && r.Present <= last).ToList();
        var outside = recorded.Where(r => PictureKinds.Contains(r.Kind) && (r.Present < first || r.Present > last)).ToList();
        var candidates = replayed.Where(r => PictureKinds.Contains(r.Kind)).ToList();
        bool[] used = new bool[candidates.Count];
        int still = 0, gone = 0, moved = 0;
        var stillByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Item r in inRange)
        {
            int exact = -1, near = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                Item c = candidates[i];
                if (used[i] || c.Kind != r.Kind)
                {
                    continue;
                }

                long distance = Math.Abs(c.Present - r.Present);
                bool sameCell = c.Cell == r.Cell || c.Cell == -1 || r.Cell == -1;
                if (distance <= 1 && sameCell)
                {
                    exact = i;
                    break;
                }

                if (distance <= 3 && near < 0)
                {
                    near = i;
                }
            }

            if (exact >= 0)
            {
                used[exact] = true;
                still++;
                stillByKind[r.Kind] = stillByKind.GetValueOrDefault(r.Kind) + 1;
                lines.Add($"  [{r.Kind}] {r.Where}: ainda presente (replay: {candidates[exact].Where}: {candidates[exact].Detail})");
            }
            else if (near >= 0)
            {
                used[near] = true;
                moved++;
                lines.Add($"  [{r.Kind}] {r.Where}: mudou (replay: {candidates[near].Where}: {candidates[near].Detail})");
            }
            else
            {
                gone++;
                lines.Add(FormattableString.Invariant($"  [{r.Kind}] {r.Where}: corrigido (replay: nada nos presents {r.Present - 1}-{r.Present + 1})"));
            }
        }

        int fresh = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!used[i] && candidates[i].Present >= first && candidates[i].Present <= last)
            {
                fresh++;
                lines.Add($"  [{candidates[i].Kind}] {candidates[i].Where}: novo (não estava na gravação): {candidates[i].Detail}");
            }
        }

        foreach (Item r in outside)
        {
            lines.Add($"  [{r.Kind}] {r.Where}: fora do dump da GPU (pré-gravação ou depois do fim do dump): não verificado pelo replay (use --rerun)");
        }

        lines.Add("  por tipo (gravação -> replay):");
        foreach (string kind in PictureKinds)
        {
            int before = inRange.Count(r => r.Kind == kind), after = candidates.Count(c => c.Kind == kind && c.Present >= first && c.Present <= last);
            if (before == 0 && after == 0)
            {
                continue;
            }

            string word = before == 0 ? "novo" : after == 0 ? "corrigido" : stillByKind.GetValueOrDefault(kind) > 0 ? "ainda presente" : "mudou";

            lines.Add(FormattableString.Invariant($"    {kind}: {before} -> {after} ({word})"));
        }

        Verdict verdict = inRange.Count == 0 && fresh == 0 ? Verdict.NothingToCheck
            : still > 0 ? Verdict.StillPresent
            : moved == 0 && fresh == 0 ? Verdict.Fixed
            : Verdict.Changed;
        return new Result(verdict, lines, inRange.Count, still, gone, moved, fresh);
    }

    /// <summary>The findings of a report.txt (<c>  [kind] present P ...</c> lines, (info) left out) by kind, and the
    /// audio's silent gaps and clicks summed over its files.</summary>
    public static SortedDictionary<string, int> ReportCounts(string reportPath)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(reportPath))
        {
            return counts;
        }

        var finding = new Regex(@"^  \[([a-z-]+)\] present ");
        var gaps = new Regex(@", (\d+) silent gaps inside the sound");
        var clicks = new Regex(@", (\d+) clicks \(");
        foreach (string line in File.ReadLines(reportPath))
        {
            Match m = finding.Match(line);
            if (m.Success && !line.Contains("(info)", StringComparison.Ordinal))
            {
                counts[m.Groups[1].Value] = counts.GetValueOrDefault(m.Groups[1].Value) + 1;
                continue;
            }

            if (line.StartsWith("  audio_mix.wav", StringComparison.Ordinal))
            {
                continue; // the mix repeats what its files have
            }

            if (gaps.Match(line) is { Success: true } g)
            {
                counts["audio-gap"] = counts.GetValueOrDefault("audio-gap") + int.Parse(g.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            if (clicks.Match(line) is { Success: true } c)
            {
                counts["audio-click"] = counts.GetValueOrDefault("audio-click") + int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return counts;
    }

    /// <summary>The recording's report against a re-run's, kind by kind (a re-run is not deterministic: counts only).</summary>
    public static Result CompareRerun(SortedDictionary<string, int> recorded, SortedDictionary<string, int> rerun)
    {
        var lines = new List<string> { "  por tipo (gravação -> re-run; o re-run não é determinístico: compare contagens):" };
        int still = 0, gone = 0, fresh = 0;
        foreach (string kind in recorded.Keys.Union(rerun.Keys).Order(StringComparer.Ordinal))
        {
            int before = recorded.GetValueOrDefault(kind), after = rerun.GetValueOrDefault(kind);
            string word = before > 0 && after == 0 ? "corrigido" : before == 0 ? "novo" : after >= before ? "ainda presente" : "mudou";
            if (before > 0 && after == 0)
            {
                gone++;
            }
            else if (before == 0)
            {
                fresh++;
            }
            else
            {
                still++;
            }

            lines.Add(FormattableString.Invariant($"    {kind}: {before} -> {after} ({word})"));
        }

        Verdict verdict = recorded.Count == 0 && rerun.Count == 0 ? Verdict.NothingToCheck
            : still > 0 ? Verdict.StillPresent
            : fresh == 0 ? Verdict.Fixed
            : Verdict.Changed;
        return new Result(verdict, lines, recorded.Values.Sum(), still, gone, 0, fresh);
    }
}
