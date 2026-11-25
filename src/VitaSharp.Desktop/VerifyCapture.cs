using System.Diagnostics;
using System.Text;
using VitaSharp.Core.Capture;
using VitaSharp.Core.Localization;

namespace VitaSharp.Desktop;

/// <summary>
/// <c>verify &lt;capture folder|zip&gt; [--out=DIR] [--rerun] [replay options]</c>: is the problem a "Gravar tudo"
/// capture recorded still there with this build? The capture's GPU dump is replayed by this build (a child
/// <c>replay-gpu --findings</c>: the renderer switches are read once per process), the capture report's picture rules
/// and the NaN/Inf scan run over the replayed frames, and every finding of the recording inside the dump is set against
/// them (<see cref="CaptureVerify.Compare"/>): corrigido / ainda presente / mudou, finding by finding and kind by kind.
/// <c>--rerun</c> also re-runs the game logic from the capture's state (<c>rerun</c>) and compares the reports kind by
/// kind (stalls, order, audio gaps and clicks). Nothing of the capture is changed: everything goes into
/// <c>--out</c> (default <c>&lt;capture&gt;/verify</c>). Exit code 0 fixed, 1 still present, 2 changed, 3 nothing to
/// check, 4 the replay failed. See docs/captures.md, "Checking a fix".
/// </summary>
static class VerifyCapture
{
    public static void PrintUsage()
    {
        Console.WriteLine("  verify <capture folder|zip> [--out=<dir>] [--rerun [--from=preroll|start|markN]] [--ab] [replay options]  is the capture's problem still there with this build?");
        Console.WriteLine("                           replays its GPU dump and compares the findings (flash, black frames, NaN/Inf) with the recording's:");
        Console.WriteLine("                           corrigido / ainda presente / mudou (exit 0 / 1 / 2; 3 nothing to check); --rerun also re-runs the logic");
        Console.WriteLine("                           (stalls, order, audio); replay options (--res-scale=, --env=K=V, --msaa=...) go to the replay");
        Console.WriteLine("    --ab[=NAME,...]        replay again with each renderer switch (depth seed, MSAA, store writeback, scene breaks, fetch");
        Console.WriteLine("                           passes, phone paths, poison, native scale) and say which one removes or changes the finding");
        Console.WriteLine("    --ab-env=KEY=VALUE     one more switch to try (repeatable)");
    }

    static readonly string[] s_ownOptions = ["--out=", "--rerun", "--from=", "--keep-frames", "--ab"];

    public static int Run(string capture, string[] options)
    {
        string dir = Path.GetFullPath(capture);
        string outDir = Runner.Opt(options, "--out") is { } o ? Path.GetFullPath(o) : "";
        if (File.Exists(dir) && dir.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            outDir = outDir.Length > 0 ? outDir : Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileNameWithoutExtension(dir) + "_verify");
            string extracted = Path.Combine(outDir, "capture");
            if (!File.Exists(Path.Combine(extracted, "session.txt")))
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(dir, extracted, overwriteFiles: true);
            }

            dir = extracted;
        }

        if (!File.Exists(Path.Combine(dir, "session.txt")))
        {
            Console.Error.WriteLine($"verify: {dir} is not a capture folder (no session.txt)");
            return 3;
        }

        outDir = outDir.Length > 0 ? outDir : Path.Combine(dir, "verify");
        Directory.CreateDirectory(outDir);
        string[] replayOptions = options.Where(a => !s_ownOptions.Any(a.StartsWith)).ToArray();
        var report = new StringBuilder();
        report.AppendLine($"verify {Path.GetFileName(dir)} with {Diagnostics.BuildVersion()}");
        List<CaptureVerify.Item> recorded = RecordedFindings(dir);
        report.AppendLine(Loc.Format("VerifyRecorded", recorded.Count, string.Join(", ", recorded.GroupBy(r => r.Kind).Select(g => $"{g.Count()} {g.Key}"))));
        var verdicts = new List<CaptureVerify.Verdict>();
        string dump = Path.Combine(dir, "gpu.vsgd");
        if (File.Exists(dump))
        {
            string replayOut = Path.Combine(outDir, "replay");
            (int rc, CaptureVerify.Result? result) = ReplayAndCompare(dump, replayOut, recorded, replayOptions, keepFrames: options.Contains("--keep-frames"));
            if (result == null)
            {
                report.AppendLine(Loc.Format("VerifyReplayFailed", rc, Path.Combine(replayOut, "log.txt")));
                Console.Write(report);
                File.WriteAllText(Path.Combine(outDir, "verify.txt"), report.ToString());
                return 4;
            }

            report.AppendLine(Loc.Format("VerifyReplayHeader", result.Recorded));
            foreach (string line in ResultLines(result, rerun: false))
            {
                report.AppendLine(line);
            }

            report.AppendLine(Loc.Format("VerifyReplayVerdict", Word(result.Verdict)) + (result.Verdict == CaptureVerify.Verdict.StillPresent ? Loc.Get("VerifyReplayStillHint") : ""));
            verdicts.Add(result.Verdict);
            Attribute(dump, outDir, result.Kept, replayOptions, report);
            if (options.Any(o => o == "--ab" || o.StartsWith("--ab=", StringComparison.Ordinal) || o.StartsWith("--ab-env=", StringComparison.Ordinal)))
            {
                SwitchTest(dump, outDir, CaptureVerify.ReadFindings(Path.Combine(replayOut, CaptureVerify.FindingsFileName)), replayOptions, options, report);
            }
        }
        else
        {
            report.AppendLine(Loc.Get("VerifyNoDump"));
        }

        if (options.Contains("--rerun"))
        {
            // the replay options are the replay's; the re-run takes the state to start from (--from=preroll|start|markN)
            CaptureVerify.Result rerun = Rerun(dir, outDir, options.Where(o => o.StartsWith("--from=", StringComparison.Ordinal)).ToArray(), report);
            verdicts.Add(rerun.Verdict);
        }

        CaptureVerify.Verdict verdict = Combine(verdicts);
        report.AppendLine(Loc.Format("VerifyResult", Word(verdict)));
        Console.Write(report);
        File.WriteAllText(Path.Combine(outDir, "verify.txt"), report.ToString());
        Console.WriteLine($"-> {Path.Combine(outDir, "verify.txt")}");
        return verdict switch
        {
            CaptureVerify.Verdict.Fixed => 0,
            CaptureVerify.Verdict.StillPresent => 1,
            CaptureVerify.Verdict.Changed => 2,
            _ => 3,
        };
    }

    /// <summary>Still present if any part says so; fixed if a part is fixed and none changed; nothing when no part had anything.</summary>
    internal static CaptureVerify.Verdict Combine(IReadOnlyList<CaptureVerify.Verdict> verdicts)
    {
        if (verdicts.Contains(CaptureVerify.Verdict.StillPresent))
        {
            return CaptureVerify.Verdict.StillPresent;
        }

        if (verdicts.Contains(CaptureVerify.Verdict.Changed))
        {
            return CaptureVerify.Verdict.Changed;
        }

        return verdicts.Contains(CaptureVerify.Verdict.Fixed) ? CaptureVerify.Verdict.Fixed : CaptureVerify.Verdict.NothingToCheck;
    }

    /// <summary>The recording's findings (the report's rules over frames.vsfr, (info) left out) and the NaN/Inf
    /// anomalies of its log.</summary>
    internal static List<CaptureVerify.Item> RecordedFindings(string dir)
    {
        var items = new List<CaptureVerify.Item>();
        string frames = Path.Combine(dir, "frames.vsfr");
        if (File.Exists(frames))
        {
            List<InputEvent> input = File.Exists(Path.Combine(dir, "input.tsv"))
                ? File.ReadLines(Path.Combine(dir, "input.tsv")).Select(InputEvent.Parse).OfType<InputEvent>().ToList()
                : new List<InputEvent>();
            items.AddRange(FrameFindings.OfFramesFile(frames, 6, input).Where(f => !f.Info).Select(CaptureVerify.ToItem));
        }

        items.AddRange(CaptureVerify.NanEvents(Path.Combine(dir, "log.txt")));
        return items;
    }

    // ------------------------------------------------------------------ the report's texts (Texts.resx, Verify*)

    internal static string Word(CaptureVerify.Verdict verdict) => Loc.Get(verdict switch
    {
        CaptureVerify.Verdict.Fixed => "VerifyWord.Fixed",
        CaptureVerify.Verdict.StillPresent => "VerifyWord.StillPresent",
        CaptureVerify.Verdict.Changed => "VerifyWord.Changed",
        _ => "VerifyWord.NothingToCheck",
    });

    internal static string Word(CaptureVerify.Outcome outcome) => Loc.Get(outcome switch
    {
        CaptureVerify.Outcome.Fixed => "VerifyWord.Fixed",
        CaptureVerify.Outcome.StillPresent => "VerifyWord.StillPresent",
        CaptureVerify.Outcome.New => "VerifyWord.New",
        _ => "VerifyWord.Changed",
    });

    internal static string Word(CaptureVerify.SwitchEffect effect) => Loc.Get(effect switch
    {
        CaptureVerify.SwitchEffect.Removes => "VerifyEffect.Removes",
        CaptureVerify.SwitchEffect.Changes => "VerifyEffect.Changes",
        _ => "VerifyEffect.Same",
    });

    /// <summary>What a switch changes (VerifySwitch.NAME; one given with --ab-env: VerifySwitchCustom).</summary>
    internal static string What(CaptureVerify.Switch sw) =>
        CaptureVerify.RendererSwitches.Any(s => s.Name == sw.Name) ? Loc.Get($"VerifySwitch.{sw.Name}") : Loc.Get("VerifySwitchCustom");

    internal static string EntryLine(CaptureVerify.Entry entry)
    {
        CaptureVerify.Item f = entry.Finding;
        string head = $"[{f.Kind}] {f.Where}";
        return entry.Outcome switch
        {
            CaptureVerify.Outcome.StillPresent or CaptureVerify.Outcome.Moved when entry.Replayed is { } r => Loc.Format($"VerifyOutcome.{entry.Outcome}", head, r.Where, r.Detail),
            CaptureVerify.Outcome.Fixed => Loc.Format("VerifyOutcome.Fixed", head, f.Present - 1, f.Present + 1),
            CaptureVerify.Outcome.New => Loc.Format("VerifyOutcome.New", head, f.Detail),
            _ => Loc.Format("VerifyOutcome.NotChecked", head),
        };
    }

    /// <summary>A comparison as the report shows it: every finding, then every kind (recording -> replay or re-run).</summary>
    internal static List<string> ResultLines(CaptureVerify.Result result, bool rerun)
    {
        var lines = result.Entries.Select(e => "  " + EntryLine(e)).ToList();
        lines.Add(Loc.Get(rerun ? "VerifyByKindRerun" : "VerifyByKindReplay"));
        lines.AddRange(result.Kinds.Select(k => FormattableString.Invariant($"    {k.Kind}: {k.Before} -> {k.After} ({Word(k.Outcome)})")));
        return lines;
    }

    /// <summary>The draw responsible for a finding as attribution_P.txt and the report show it.</summary>
    internal static List<string> AttributionLines(DrawAttribution.Result result)
    {
        var lines = new List<string> { Loc.Format("VerifyAttrSummary", result.Present, result.FlashDraws, result.FlashWriting, result.Neighbour, result.NeighbourDraws, result.NeighbourWriting) };
        if (result.FlashDraws == 0 && result.NeighbourDraws == 0)
        {
            lines.Add(Loc.Get("VerifyAttrNoDraws"));
            return lines;
        }

        if (result.Responsible is not { } responsible)
        {
            lines.Add(Loc.Get("VerifyAttrNone"));
            return lines;
        }

        lines.Add(Loc.Format("VerifyAttrResponsible", responsible.Draw.Describe()));
        lines.Add(Loc.Format("VerifyAttrWhy", Why(responsible, result.Neighbour)));
        if (result.Candidates.Count > 1)
        {
            lines.Add(Loc.Format("VerifyAttrOthers", result.Candidates.Count - 1));
            lines.AddRange(result.Candidates.Skip(1).Take(12).Select(c => $"  {Why(c, result.Neighbour)}: {c.Draw.Describe()}"));
        }

        return lines;
    }

    static string Why(DrawAttribution.Candidate candidate, long neighbour)
    {
        string why = Loc.Format($"VerifyReason.{candidate.Reason}", neighbour, candidate.Draw.Changed, candidate.OtherChanged);
        if (candidate.Differences.Count == 0)
        {
            return why;
        }

        IEnumerable<string> fields = candidate.Differences.Select(d => Loc.Format("VerifyDifference",
            d.Field is DrawAttribution.DepthField or DrawAttribution.StencilField ? Loc.Get($"VerifyField.{d.Field}") : d.Field, d.Flash, d.Neighbour));
        return why + "; " + string.Join(", ", fields);
    }

    /// <summary>The switches of --ab (all, or the names given) and of every --ab-env=KEY=VALUE.</summary>
    internal static List<CaptureVerify.Switch> Switches(string[] options)
    {
        var chosen = new List<CaptureVerify.Switch>();
        if (options.Contains("--ab"))
        {
            chosen.AddRange(CaptureVerify.RendererSwitches);
        }

        foreach (string names in options.Where(o => o.StartsWith("--ab=", StringComparison.Ordinal)).Select(o => o["--ab=".Length..]))
        {
            foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (CaptureVerify.RendererSwitches.FirstOrDefault(s => s.Name == name) is { } known)
                {
                    chosen.Add(known);
                }
                else
                {
                    Console.Error.WriteLine($"verify --ab: no switch {name} (the switches: {string.Join(", ", CaptureVerify.RendererSwitches.Select(s => s.Name))})");
                }
            }
        }

        foreach (string env in options.Where(o => o.StartsWith("--ab-env=", StringComparison.Ordinal)).Select(o => o["--ab-env=".Length..]))
        {
            int eq = env.IndexOf('=');
            if (eq > 0)
            {
                chosen.Add(new CaptureVerify.Switch(env, env[..eq], env[(eq + 1)..]));
            }
        }

        return chosen.DistinctBy(s => s.Env).ToList();
    }

    /// <summary>
    /// --ab: the dump replayed once more per renderer switch (a child process each: the switches are read once per
    /// process), up to two presents after the last picture finding, and its findings against the switch-free
    /// replay's: which switch removes the finding (the class of the bug), changes it, or leaves it the same.
    /// </summary>
    static void SwitchTest(string dump, string outDir, (long First, long Last, List<CaptureVerify.Item> Items) baseline, string[] replayOptions, string[] options, StringBuilder report)
    {
        List<CaptureVerify.Switch> switches = Switches(options);
        var findings = baseline.Items.Where(i => CaptureVerify.PictureKinds.Contains(i.Kind)).ToList();
        report.AppendLine(Loc.Format("VerifyAbHeader", switches.Count));
        if (findings.Count == 0 || switches.Count == 0)
        {
            report.AppendLine(Loc.Get(findings.Count == 0 ? "VerifyAbNothing" : "VerifyAbNoneChosen"));
            return;
        }

        long stop = findings.Max(i => i.Present) + 2;
        var effects = new List<(CaptureVerify.Switch Switch, CaptureVerify.SwitchEffect Effect)>();
        foreach (CaptureVerify.Switch sw in switches)
        {
            string dir = Path.Combine(outDir, "ab", string.Concat(sw.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')));
            var args = replayOptions.Where(o => !o.StartsWith("--frame=", StringComparison.Ordinal)).Append($"--env={sw.Env}").Append($"--frame={stop}");
            (int rc, (long First, long Last, List<CaptureVerify.Item> Items)? replayed) = Replay(dump, dir, args, keepFrames: false);
            if (replayed is not { } r)
            {
                report.AppendLine(Loc.Format("VerifyAbReplayFailed", sw.Name, sw.Env, rc, Path.Combine(dir, "log.txt")));
                continue;
            }

            long last = Math.Min(Math.Min(baseline.Last, r.Last), stop);
            (CaptureVerify.SwitchEffect effect, CaptureVerify.Result result) = CaptureVerify.CompareSwitch(findings.Where(i => i.Present <= last).ToList(), r.Items, Math.Max(baseline.First, r.First), last);
            effects.Add((sw, effect));
            report.AppendLine($"  {sw.Name} ({sw.Env}: {What(sw)}): {Word(effect)}");
            if (effect == CaptureVerify.SwitchEffect.Changes)
            {
                foreach (CaptureVerify.Entry entry in result.Entries.Where(e => e.Outcome != CaptureVerify.Outcome.StillPresent).Take(3))
                {
                    report.AppendLine("    " + EntryLine(entry));
                }
            }
        }

        var removing = effects.Where(e => e.Effect == CaptureVerify.SwitchEffect.Removes).ToList();
        report.AppendLine(removing.Count > 0
            ? Loc.Format("VerifyAbRemovedBy", string.Join(", ", removing.Select(e => e.Switch.Name)), string.Join("; ", removing.Select(e => What(e.Switch))))
            : effects.Any(e => e.Effect == CaptureVerify.SwitchEffect.Changes)
                ? Loc.Format("VerifyAbChangedBy", string.Join(", ", effects.Where(e => e.Effect == CaptureVerify.SwitchEffect.Changes).Select(e => e.Switch.Name)))
                : Loc.Get("VerifyAbNoEffect"));
    }

    /// <summary>Most findings still present whose draws are probed (one replay each, up to their present).</summary>
    internal const int MaxProbed = 3;

    /// <summary>The draw responsible for each finding the replay still shows (<c>replay-gpu --probe</c> in a child
    /// process: the draws into the shown surface at the present and its neighbours, region by region), into the
    /// report.</summary>
    static void Attribute(string dump, string outDir, IReadOnlyList<CaptureVerify.Item> kept, string[] replayOptions, StringBuilder report)
    {
        foreach (CaptureVerify.Item item in kept.Where(k => k.Kind is "flash" or "black-flash").Take(MaxProbed))
        {
            string dir = Path.Combine(outDir, $"probe_{item.Present}");
            Directory.CreateDirectory(dir);
            var args = new List<string> { "replay-gpu", dump, $"--out={dir}", $"--probe={item.Present}:{item.Cell}", "--fast", "--dump-every=1000000000" };
            args.AddRange(replayOptions);
            int rc = RunSelf(args, Path.Combine(dir, "console.txt"));
            string attribution = Path.Combine(dir, $"attribution_{item.Present}.txt");
            report.AppendLine(Loc.Format("VerifyDrawResponsible", item.Kind, item.Where));
            if (!File.Exists(attribution))
            {
                report.AppendLine(Loc.Format("VerifyProbeFailed", rc, Path.Combine(dir, "log.txt")));
                continue;
            }

            foreach (string line in File.ReadLines(attribution).Skip(1))
            {
                report.AppendLine("    " + line);
            }
        }
    }

    /// <summary>Replays <paramref name="dump"/> in a child process with --findings into <paramref name="outDir"/> and
    /// compares its findings with <paramref name="recorded"/>; null when the replay wrote no findings.txt.</summary>
    internal static (int ExitCode, CaptureVerify.Result? Result) ReplayAndCompare(string dump, string outDir, IReadOnlyList<CaptureVerify.Item> recorded, string[] replayOptions, bool keepFrames)
    {
        (int rc, (long First, long Last, List<CaptureVerify.Item> Items)? replayed) = Replay(dump, outDir, replayOptions, keepFrames);
        if (replayed is not { } r)
        {
            return (rc, null);
        }

        return (rc, CaptureVerify.Compare(recorded, r.Items, r.First, r.Last));
    }

    /// <summary>One <c>replay-gpu DUMP --findings --fast --out=DIR</c> in a child process (its renderer switches are its
    /// own); its findings.txt, or null.</summary>
    internal static (int ExitCode, (long First, long Last, List<CaptureVerify.Item> Items)? Findings) Replay(string dump, string outDir, IEnumerable<string> replayOptions, bool keepFrames)
    {
        Directory.CreateDirectory(outDir);
        string findings = Path.Combine(outDir, CaptureVerify.FindingsFileName);
        File.Delete(findings);
        // no PNG of every frame unless asked: the findings come from the present tap
        var args = new List<string> { "replay-gpu", dump, $"--out={outDir}", "--findings", "--fast" };
        if (!keepFrames)
        {
            args.Add("--dump-every=1000000000");
        }

        args.AddRange(replayOptions);
        int rc = RunSelf(args, Path.Combine(outDir, "console.txt"));
        return (rc, File.Exists(findings) ? CaptureVerify.ReadFindings(findings) : null);
    }

    /// <summary>Runs this program again with <paramref name="args"/> (the apphost, or dotnet with this assembly), its
    /// output into <paramref name="consoleFile"/>.</summary>
    internal static int RunSelf(IEnumerable<string> args, string consoleFile)
    {
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("no executable path");
        var psi = new ProcessStartInfo(self) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "VitaSharp.Desktop.dll"));
        }

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{self} did not start");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        lock (output)
        {
            File.WriteAllText(consoleFile, output.ToString());
        }

        return process.ExitCode;
    }

    /// <summary>--rerun: the capture's logic again from its state (<c>rerun</c>, a child process), its report against
    /// the recording's, kind by kind.</summary>
    static CaptureVerify.Result Rerun(string dir, string outDir, string[] extra, StringBuilder report)
    {
        string reruns = Path.Combine(dir, "reruns");
        var before = Directory.Exists(reruns) ? Directory.GetDirectories(reruns).ToHashSet() : new HashSet<string>();
        int rc = RunSelf(new[] { "rerun", dir }.Concat(extra), Path.Combine(outDir, "rerun_console.txt"));
        string? latest = Directory.Exists(reruns)
            ? Directory.GetDirectories(reruns).Where(d => !before.Contains(d) && File.Exists(Path.Combine(d, "report.txt"))).OrderBy(Directory.GetLastWriteTimeUtc).LastOrDefault()
            : null;
        if (latest == null)
        {
            report.AppendLine(Loc.Format("VerifyRerunFailed", rc, reruns));
            return new CaptureVerify.Result(CaptureVerify.Verdict.NothingToCheck, new List<CaptureVerify.Entry>(), new List<CaptureVerify.KindCount>(), 0, 0, 0, 0, 0);
        }

        SortedDictionary<string, int> recorded = CaptureVerify.ReportCounts(Path.Combine(dir, "report.txt"));
        SortedDictionary<string, int> again = CaptureVerify.ReportCounts(Path.Combine(latest, "report.txt"));
        CaptureVerify.Result result = CaptureVerify.CompareRerun(recorded, again);
        report.AppendLine(Loc.Format("VerifyRerunHeader", latest));
        foreach (string line in ResultLines(result, rerun: true))
        {
            report.AppendLine(line);
        }

        report.AppendLine(Loc.Format("VerifyRerunVerdict", Word(result.Verdict)));
        return result;
    }
}
