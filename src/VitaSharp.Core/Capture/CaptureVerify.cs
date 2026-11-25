using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VitaSharp.Core.Capture;

/// <summary>
/// <c>verify</c> (docs/captures.md, "Checking a fix"): whether the problem a capture recorded is still there with this
/// build. The capture's findings (the report's rules over frames.vsfr, <see cref="FrameFindings"/>, and the NaN/Inf
/// anomalies of its log) are set against the findings of the same rules over a replay of its GPU dump made by this
/// build (<c>replay-gpu --findings</c>: findings.txt), finding by finding (same kind at the same present, one present
/// either way) and kind by kind, with a short verdict (<see cref="Verdict"/>: fixed, still present, changed; the
/// front-end says it in the player's language). The timing findings (stalls, presentation order) and the audio need the game logic to run again: they
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

    /// <summary>What became of one finding, or of one kind of finding, in the replay or the re-run.</summary>
    public enum Outcome
    {
        /// <summary>The same kind at the same place (one present either way, the same cell or the whole picture).</summary>
        StillPresent,
        /// <summary>The same kind nearby (another cell, up to 3 presents away), or fewer of the kind.</summary>
        Moved,
        /// <summary>Not in the replay.</summary>
        Fixed,
        /// <summary>In the replay, not in the recording.</summary>
        New,
        /// <summary>Outside the replayed presents (before the dump: the pre-roll; after its end).</summary>
        NotChecked,
    }

    /// <summary>One finding and what became of it (<see cref="Replayed"/>: the replay's finding it was matched with).</summary>
    public readonly record struct Entry(Item Finding, Outcome Outcome, Item? Replayed);

    /// <summary>One kind of finding: how many in the recording and in the replay, and what became of them.</summary>
    public readonly record struct KindCount(string Kind, int Before, int After, Outcome Outcome);

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

    /// <summary>The result of one comparison: the verdict, every finding and every kind with what became of it.</summary>
    public sealed record Result(Verdict Verdict, List<Entry> Entries, List<KindCount> Kinds, int Recorded, int StillThere, int Gone, int Moved, int New)
    {
        /// <summary>The recorded findings the replay still shows (where the draw probe looks: verify).</summary>
        public List<Item> Kept { get; init; } = new();
    }

    /// <summary>
    /// The recording's picture findings inside the replayed presents (<paramref name="first"/>-<paramref name="last"/>)
    /// against the replay's: each recorded one is still there (the same kind within one present, the same cell or the
    /// whole picture), moved (the same kind nearby, in another cell or up to 3 presents away) or gone; replay findings
    /// that match none are new. Fixed = every recorded one gone and nothing new; still present = any still there.
    /// </summary>
    public static Result Compare(IReadOnlyList<Item> recorded, IReadOnlyList<Item> replayed, long first, long last)
    {
        var entries = new List<Entry>();
        var inRange = recorded.Where(r => PictureKinds.Contains(r.Kind) && r.Present >= first && r.Present <= last).ToList();
        var outside = recorded.Where(r => PictureKinds.Contains(r.Kind) && (r.Present < first || r.Present > last)).ToList();
        var candidates = replayed.Where(r => PictureKinds.Contains(r.Kind)).ToList();
        bool[] used = new bool[candidates.Count];
        int still = 0, gone = 0, moved = 0;
        var stillByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<Item>();
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
                kept.Add(r);
                entries.Add(new Entry(r, Outcome.StillPresent, candidates[exact]));
            }
            else if (near >= 0)
            {
                used[near] = true;
                moved++;
                entries.Add(new Entry(r, Outcome.Moved, candidates[near]));
            }
            else
            {
                gone++;
                entries.Add(new Entry(r, Outcome.Fixed, null));
            }
        }

        int fresh = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!used[i] && candidates[i].Present >= first && candidates[i].Present <= last)
            {
                fresh++;
                entries.Add(new Entry(candidates[i], Outcome.New, null));
            }
        }

        foreach (Item r in outside)
        {
            entries.Add(new Entry(r, Outcome.NotChecked, null));
        }

        var kinds = new List<KindCount>();
        foreach (string kind in PictureKinds)
        {
            int before = inRange.Count(r => r.Kind == kind), after = candidates.Count(c => c.Kind == kind && c.Present >= first && c.Present <= last);
            if (before == 0 && after == 0)
            {
                continue;
            }

            Outcome outcome = before == 0 ? Outcome.New : after == 0 ? Outcome.Fixed : stillByKind.GetValueOrDefault(kind) > 0 ? Outcome.StillPresent : Outcome.Moved;
            kinds.Add(new KindCount(kind, before, after, outcome));
        }

        Verdict verdict = inRange.Count == 0 && fresh == 0 ? Verdict.NothingToCheck
            : still > 0 ? Verdict.StillPresent
            : moved == 0 && fresh == 0 ? Verdict.Fixed
            : Verdict.Changed;
        return new Result(verdict, entries, kinds, inRange.Count, still, gone, moved, fresh) { Kept = kept };
    }

    /// <summary>One renderer switch the A/B runs (<c>verify --ab</c>): a name (what it changes is the front-end's text
    /// VerifySwitch.NAME), the environment variable and the value set for the replay (<c>replay-gpu --env=KEY=VALUE</c>).</summary>
    public sealed record Switch(string Name, string Key, string Value)
    {
        public string Env => $"{Key}={Value}";
    }

    /// <summary>
    /// The renderer's switches that change how a picture is drawn (EnvVars), each replayed on its own by
    /// <c>verify --ab</c>: a finding that goes away with one of them names the class of the bug (the depth seed, MSAA,
    /// the store writeback, the scene breaks, the fetch passes of programmable blending, the tile-based GPU paths,
    /// undefined contents, the resolution scale). Both values of the switches whose default depends on the GPU.
    /// </summary>
    public static readonly Switch[] RendererSwitches =
    [
        new("depth-seed-off", Util.EnvVars.DepthSeed, "0"),
        new("msaa-off", Util.EnvVars.Msaa, "off"),
        new("msaa-ss", Util.EnvVars.Msaa, "ss"),
        new("msaa-native", Util.EnvVars.Msaa, "native"),
        new("store-writeback-off", Util.EnvVars.GpuStoreWriteback, "0"),
        new("scene-breaks-strict", Util.EnvVars.SceneBreaks, "strict"),
        new("fetch-pass-merge-off", Util.EnvVars.FetchPassMerge, "0"),
        new("fetch-pass-merge-on", Util.EnvVars.FetchPassMerge, "1"),
        new("fetch-large-off", Util.EnvVars.FetchLarge, "0"),
        new("fetch-large-on", Util.EnvVars.FetchLarge, "1"),
        new("fetch-pass-join-off", Util.EnvVars.FetchPassJoin, "0"),
        new("fetch-pass-join-on", Util.EnvVars.FetchPassJoin, "1"),
        new("depth-feedback-off", Util.EnvVars.DepthFeedback, "0"),
        new("depth-feedback-on", Util.EnvVars.DepthFeedback, "1"),
        new("depth-ops-none-off", Util.EnvVars.DepthOpsNone, "0"),
        new("gpu-profile-mali", Util.EnvVars.GpuProfile, "mali"),
        new("gpu-poison", Util.EnvVars.GpuPoison, "1"),
        new("res-scale-1", Util.EnvVars.ResScale, "1"),
    ];

    /// <summary>What a switch does to the findings the replay showed without it.</summary>
    public enum SwitchEffect
    {
        /// <summary>The same findings.</summary>
        Same,
        /// <summary>The findings go away and nothing new appears.</summary>
        Removes,
        /// <summary>Some go away or move, or new ones appear.</summary>
        Changes,
    }

    /// <summary>The findings of a replay with one switch against the replay without it (<see cref="Compare"/>, the
    /// switch-free replay standing for the recording) over the presents both showed.</summary>
    public static (SwitchEffect Effect, Result Result) CompareSwitch(IReadOnlyList<Item> baseline, IReadOnlyList<Item> withSwitch, long first, long last)
    {
        Result r = Compare(baseline, withSwitch, first, last);
        SwitchEffect effect = r.Verdict switch
        {
            Verdict.Fixed => SwitchEffect.Removes,
            Verdict.StillPresent when r.Gone == 0 && r.Moved == 0 && r.New == 0 => SwitchEffect.Same,
            Verdict.NothingToCheck => SwitchEffect.Same,
            _ => SwitchEffect.Changes,
        };
        return (effect, r);
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
        var kinds = new List<KindCount>();
        int still = 0, gone = 0, fresh = 0;
        foreach (string kind in recorded.Keys.Union(rerun.Keys).Order(StringComparer.Ordinal))
        {
            int before = recorded.GetValueOrDefault(kind), after = rerun.GetValueOrDefault(kind);
            Outcome outcome = before > 0 && after == 0 ? Outcome.Fixed : before == 0 ? Outcome.New : after >= before ? Outcome.StillPresent : Outcome.Moved;
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

            kinds.Add(new KindCount(kind, before, after, outcome));
        }

        Verdict verdict = recorded.Count == 0 && rerun.Count == 0 ? Verdict.NothingToCheck
            : still > 0 ? Verdict.StillPresent
            : fresh == 0 ? Verdict.Fixed
            : Verdict.Changed;
        return new Result(verdict, new List<Entry>(), kinds, recorded.Values.Sum(), still, gone, 0, fresh);
    }
}
